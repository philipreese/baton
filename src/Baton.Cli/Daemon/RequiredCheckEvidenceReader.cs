using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Baton.Queue;
using Baton.Accounting;

namespace Baton.Cli.Daemon;

/// <summary>One bounded read of effective policy and current-head sources, never a polling loop.</summary>
internal sealed class RequiredCheckEvidenceReader(
    Func<IReadOnlyList<string>, CancellationToken, Task<GhCliResult>> run,
    Func<DateTimeOffset>? observationClock = null)
{
    private const int PageSize = 100;
    private const int MaxPages = 5;
    private const int MaxResponseBytes = 1024 * 1024;
    private const int MaxCommands = 40;
    private int _commands;

    internal sealed record Reading(string? State, RequiredCheckEvidence? Evidence, string? Error,
        bool NoObservedRequiredEvidence = false);
    private sealed record Policy(IReadOnlyList<RequiredCheckRequirement> Requirements, string Digest);
    private sealed class Unreadable(string message) : Exception(message);

    internal async Task<Reading> ReadAsync(string repository, string head, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            var identity = RepositoryIdentity.From("https://" + repository, null);
            var parts = repository.Split('/');
            if (identity?.RemoteValue != repository || parts.Length != 3
                || head.Length != 40 || !head.All(Uri.IsHexDigit))
                throw new Unreadable("required-check repository/head identity is invalid");
            var host = parts[0];
            var prefix = $"repos/{Uri.EscapeDataString(parts[1])}/{Uri.EscapeDataString(parts[2])}";
            var before = await ReadPolicyAsync(host, prefix, deadline.Token).ConfigureAwait(false);
            var witnesses = await ReadWitnessesAsync(host, prefix, head, deadline.Token).ConfigureAwait(false);
            var after = await ReadPolicyAsync(host, prefix, deadline.Token).ConfigureAwait(false);
            if (before.Digest != after.Digest)
                throw new Unreadable("required-check policy changed during observation");
            var state = PullRequestChecks.SummarizeRequired(before.Requirements, witnesses);
            if (state is null) throw new Unreadable("required-check rerun/source evidence is ambiguous");
            var digest = Hash(JsonSerializer.Serialize(witnesses.OrderBy(w => w.Context, StringComparer.Ordinal)
                .ThenBy(w => w.Source, StringComparer.Ordinal).ThenBy(w => w.AppId).ThenBy(w => w.Id)));
            return new Reading(state, new RequiredCheckEvidence(repository, "main", head,
                before.Digest, digest, observationClock?.Invoke() ?? DateTimeOffset.UtcNow), null,
                before.Requirements.Count > 0 && !before.Requirements.Any(r => witnesses.Any(w =>
                    w.Context == r.Context && (r.AppId is null || w.Source == "check-run" && w.AppId == r.AppId))));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new Reading(null, null, "required-check observation exceeded its 60-second bound");
        }
        catch (Exception ex) when (ex is Unreadable or JsonException or InvalidOperationException or FormatException or OverflowException or KeyNotFoundException)
        {
            return new Reading(null, null, ex is Unreadable ? ex.Message : "required-check response is malformed");
        }
    }

    private async Task<JsonElement> ReadJsonAsync(string host, string endpoint, CancellationToken cancellationToken)
    {
        if (++_commands > MaxCommands) throw new Unreadable("required-check command bound exhausted");
        using var command = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        command.CancelAfter(TimeSpan.FromSeconds(20));
        var result = await run(["api", "--hostname", host, endpoint], command.Token)
            .WaitAsync(command.Token).ConfigureAwait(false);
        if (!result.Started || result.ExitCode != 0)
            throw new Unreadable("required-check policy/source lookup is unavailable; absence is not inferred");
        if (Encoding.UTF8.GetByteCount(result.Stdout) > MaxResponseBytes)
            throw new Unreadable("required-check response exceeded its 1 MiB acceptance bound");
        using var document = JsonDocument.Parse(result.Stdout);
        RejectDuplicateKeys(document.RootElement);
        return document.RootElement.Clone();
    }

    private async Task<Policy> ReadPolicyAsync(string host, string prefix, CancellationToken cancellationToken)
    {
        // A 404 can conceal inaccessible classic policy; never translate it to unprotected. A
        // successful full protection object with explicit null required_status_checks is absence.
        var classic = await ReadJsonAsync(host, prefix + "/branches/main/protection", cancellationToken)
            .ConfigureAwait(false);
        if (classic.ValueKind != JsonValueKind.Object || !classic.TryGetProperty("required_status_checks", out var checks))
            throw new Unreadable("classic required-check policy is incomplete");
        var requirements = new List<RequiredCheckRequirement>();
        var sources = new List<string>();
        if (checks.ValueKind != JsonValueKind.Null)
        {
            RequireArray(checks, "contexts", out var contexts);
            RequireArray(checks, "checks", out var bindings);
            var names = contexts.EnumerateArray().Select(value => String(value)).ToHashSet(StringComparer.Ordinal);
            foreach (var binding in bindings.EnumerateArray())
            {
                var context = String(binding.GetProperty("context"));
                var app = NullableApp(binding.GetProperty("app_id"));
                if (!names.Contains(context)) throw new Unreadable("classic context/App policy conflicts");
                requirements.Add(new(context, app));
            }
            if (names.Any(name => !requirements.Any(requirement => requirement.Context == name)))
                throw new Unreadable("classic required context lacks a source binding");
        }
        foreach (var rule in await ReadArrayPagesAsync(host, prefix + "/rules/branches/main", cancellationToken).ConfigureAwait(false))
        {
            var type = String(rule.GetProperty("type"));
            var sourceType = String(rule.GetProperty("ruleset_source_type"));
            var source = String(rule.GetProperty("ruleset_source"));
            var sourceId = PositiveId(rule.GetProperty("ruleset_id"));
            var repository = prefix["repos/".Length..];
            var owner = repository.Split('/')[0];
            if (!(sourceType == "Repository" && string.Equals(source, repository, StringComparison.OrdinalIgnoreCase)
                || sourceType == "Organization" && string.Equals(source, owner, StringComparison.OrdinalIgnoreCase)))
                throw new Unreadable("unsupported or mismatched effective branch-rule source");
            sources.Add($"{sourceType}\0{source}\0{sourceId}\0{type}");
            if (type == "required_status_checks")
            {
                var parameters = rule.GetProperty("parameters");
                RequireArray(parameters, "required_status_checks", out var rules);
                foreach (var requirement in rules.EnumerateArray())
                    requirements.Add(new(String(requirement.GetProperty("context")),
                        NullableApp(requirement.GetProperty("integration_id"))));
            }
            else if (type is not ("deletion" or "non_fast_forward" or "pull_request" or "creation"
                or "update" or "required_linear_history" or "required_signatures" or "required_deployments"
                or "code_scanning" or "file_path_restriction" or "max_file_path_length"
                or "file_extension_restriction" or "max_file_size" or "commit_message_pattern"
                or "commit_author_email_pattern" or "committer_email_pattern" or "branch_name_pattern"))
                throw new Unreadable("unsupported effective branch rule prevents complete required-check policy");
        }
        var normalized = requirements.Distinct().OrderBy(r => r.Context, StringComparer.Ordinal).ThenBy(r => r.AppId).ToArray();
        return new Policy(normalized, Hash(JsonSerializer.Serialize(new
        {
            Requirements = normalized,
            Sources = sources.Distinct().OrderBy(s => s, StringComparer.Ordinal).ToArray(),
        })));
    }

    private async Task<IReadOnlyList<JsonElement>> ReadArrayPagesAsync(
        string host, string endpoint, CancellationToken cancellationToken)
    {
        var all = new List<JsonElement>();
        for (var page = 1; page <= MaxPages; page++)
        {
            var root = await ReadJsonAsync(host, $"{endpoint}?per_page={PageSize}&page={page}", cancellationToken).ConfigureAwait(false);
            if (root.ValueKind != JsonValueKind.Array) throw new Unreadable("required-check paginated array is malformed");
            var entries = root.EnumerateArray().ToArray();
            if (entries.Length > PageSize) throw new Unreadable("required-check page exceeds its declared size");
            all.AddRange(entries);
            if (entries.Length < PageSize) return all;
        }
        throw new Unreadable("required-check page bound exhausted without completeness proof");
    }

    private async Task<IReadOnlyList<RequiredCheckWitness>> ReadWitnessesAsync(
        string host, string prefix, string head, CancellationToken cancellationToken)
    {
        var witnesses = new List<RequiredCheckWitness>();
        var expectedTotal = (int?)null;
        for (var page = 1; page <= MaxPages; page++)
        {
            var root = await ReadJsonAsync(host,
                $"{prefix}/commits/{head}/check-runs?filter=latest&per_page={PageSize}&page={page}", cancellationToken).ConfigureAwait(false);
            var count = root.GetProperty("total_count").GetInt32();
            if (count < 0 || expectedTotal is { } expected && expected != count)
                throw new Unreadable("required-check collection changed during pagination");
            expectedTotal = count;
            RequireArray(root, "check_runs", out var runs);
            var entries = runs.EnumerateArray().ToArray();
            if (entries.Length > PageSize) throw new Unreadable("required-check page exceeds its declared size");
            foreach (var entry in entries)
            {
                if (!string.Equals(String(entry.GetProperty("head_sha")), head, StringComparison.OrdinalIgnoreCase))
                    throw new Unreadable("required-check witness names a different head");
                var id = PositiveId(entry.GetProperty("id"));
                var app = PositiveId(entry.GetProperty("app").GetProperty("id"));
                var status = String(entry.GetProperty("status"));
                var conclusion = entry.GetProperty("conclusion");
                var verdict = status is "queued" or "in_progress" or "waiting" or "requested" or "pending"
                    ? PullRequestChecks.Pending : status == "completed" ? CheckRunVerdict(String(conclusion)) : null;
                if (verdict is null) throw new Unreadable("required-check status is unknown");
                var started = entry.GetProperty("started_at");
                witnesses.Add(new(String(entry.GetProperty("name")), app, "check-run", id, verdict,
                    started.ValueKind == JsonValueKind.Null && verdict == PullRequestChecks.Pending
                        ? null : Instant(started)));
            }
            // Check runs carry an authoritative total; unlike rule/status arrays, a full final
            // page can prove completeness without spending a sixth request beyond the bound.
            if (witnesses.Count == count) break;
            if (entries.Length < PageSize || page == MaxPages)
                throw new Unreadable("required-check collection is truncated or exceeds its page bound");
        }
        foreach (var entry in await ReadArrayPagesAsync(host, $"{prefix}/commits/{head}/statuses", cancellationToken).ConfigureAwait(false))
            witnesses.Add(new(String(entry.GetProperty("context")), null, "status", PositiveId(entry.GetProperty("id")),
                CommitStatusVerdict(String(entry.GetProperty("state"))), Instant(entry.GetProperty("created_at"))));
        if (witnesses.GroupBy(w => (w.Source, w.Id)).Any(group => group.Count() != 1))
            throw new Unreadable("required-check witness identity is duplicated");
        return witnesses;
    }

    private static string CheckRunVerdict(string state) => state switch
    {
        "success" or "neutral" or "skipped" => PullRequestChecks.Passing,
        "failure" or "timed_out" or "cancelled" or "action_required" or "startup_failure" or "stale" => PullRequestChecks.Failing,
        _ => throw new Unreadable("required check-run conclusion is unsupported"),
    };

    private static string CommitStatusVerdict(string state) => state switch
    {
        "success" => PullRequestChecks.Passing,
        "pending" => PullRequestChecks.Pending,
        "failure" or "error" => PullRequestChecks.Failing,
        _ => throw new Unreadable("required commit-status state is unsupported"),
    };

    private static string String(JsonElement value) => value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 and <= 1024 } text ? text : throw new Unreadable("required-check text is malformed");

    private static long PositiveId(JsonElement value) => value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var id) && id > 0 ? id : throw new Unreadable("required-check source identity is malformed");

    private static long? NullableApp(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null
        : value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var id) && id == -1 ? null : PositiveId(value);

    private static DateTimeOffset Instant(JsonElement value) => DateTimeOffset.TryParse(String(value), CultureInfo.InvariantCulture,
        DateTimeStyles.RoundtripKind, out var time) && time > DateTimeOffset.MinValue
            ? time : throw new Unreadable("required-check timestamp is malformed");

    private static void RequireArray(JsonElement owner, string name, out JsonElement array)
    {
        array = default;
        if (owner.ValueKind != JsonValueKind.Object || !owner.TryGetProperty(name, out array) || array.ValueKind != JsonValueKind.Array)
            throw new Unreadable("required-check array is missing or malformed");
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    internal static void ValidateJsonKeys(JsonElement value)
    {
        try { RejectDuplicateKeys(value); }
        catch (Unreadable ex) { throw new JsonException(ex.Message, ex); }
    }

    private static void RejectDuplicateKeys(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new Unreadable("required-check JSON property is duplicated");
                RejectDuplicateKeys(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var element in value.EnumerateArray()) RejectDuplicateKeys(element);
    }
}
