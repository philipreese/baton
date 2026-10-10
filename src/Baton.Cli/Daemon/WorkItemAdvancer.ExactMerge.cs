using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Baton.Domain;
using Baton.Store;
using Baton.Queue;
using Baton.Status;
using Baton.Vendors;

namespace Baton.Cli.Daemon;

internal sealed class ExactMergeUnsupportedException() : BatonFlowException("Unsupported effective server enforcement or immediate merge transport.");

internal sealed record ExactMergeQualification(string QueueSha256, string ReviewSha256,
    RequiredCheckEvidence RequiredChecks, string ServerPolicySha256, string Actor, DateTimeOffset ObservedAt,
    ExactMergeServerPolicy ServerPolicy);

internal sealed record ExactMergeServerPolicy(JsonElement Actor, JsonElement Repository, JsonElement Permission,
    JsonElement Classic, JsonElement Rules, IReadOnlyList<JsonElement> Rulesets, JsonElement Labels);

internal sealed record ExactMergeExecutorResult(string State, int? ExitCode, int? HttpStatus,
    string? CommitSha, string? ResponseDigest, string? Response = null);

public sealed partial class WorkItemAdvancer
{
    private static string ExactBytesDigest(string path, int bound = 1024 * 1024)
    {
        if (AttachmentReadInputs.CrossesLink(path)) throw new IOException("Linked merge evidence refused.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > bound) throw new IOException("Merge evidence exceeds its bound.");
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static TaskReadyReviewProof? CaptureReadyReviewProof(QueueItem item, string path, string attempt)
    {
        if (item.OwnedTask?.Ready is { ReviewAttemptId: var previous, ReviewProof: { } retained } && previous == attempt)
            return retained;
        if (item.AttemptEnvelope is not { RoomDirectory: { } room, OwnedTaskIdentity: { } owner } envelope
            || envelope.AttemptId.Value != attempt || envelope.Stage is not (WorkStage.Review or WorkStage.ReReview)
            || envelope.DeclaredRole != "review" || envelope.ParentAttemptId is null || envelope.ParentAttemptId == envelope.AttemptId
            || owner.TaskId != item.OwnedTask?.Id || owner.Repository != item.Repository || owner.Issue != item.Issue
            || owner.AttemptId != attempt || owner.RoomDirectory != room || !owner.IsAdmissionShape()) return null;
        var terminal = TerminalSentinelWriter.TryReadAsync(room, CancellationToken.None).GetAwaiter().GetResult();
        var executions = terminal?.Steps.Where(s => s.Execution is not null && s.LinkedFrom is null && s.State == "Succeeded")
            .Select(s => s.Execution!).ToArray();
        if (terminal?.State != "Succeeded" || executions is not { Length: 1 }) return null;
        var expected = Path.Combine(room, "artifacts", "execution_" + executions[0], "verdict.json");
        if (!BatonPaths.RecordKeyComparer.Equals(Path.GetFullPath(path), Path.GetFullPath(expected))) return null;
        return new(envelope, path, ExactBytesDigest(Path.Combine(room, TerminalSentinelWriter.TerminalSentinelFileName)), executions[0]);
    }

    internal static string ValidateExactReviewBytes(QueueItem item, TaskReadyReceipt ready)
    {
        var proof = ready.ReviewProof ?? throw new CliArgumentException("Complete independent review provenance is unavailable.");
        var envelope = proof.Attempt ?? throw new CliArgumentException("Independent review attempt provenance is unavailable.");
        var owner = envelope.OwnedTaskIdentity;
        var room = envelope.RoomDirectory;
        if (room is null || owner is null || envelope.AttemptId.Value != ready.ReviewAttemptId
            || envelope.Stage is not (WorkStage.Review or WorkStage.ReReview) || envelope.DeclaredRole != "review"
            || envelope.ParentAttemptId is null || envelope.ParentAttemptId == envelope.AttemptId
            || owner.TaskId != ready.TaskId || owner.Repository != ready.Repository || owner.Issue != ready.Issue
            || owner.AttemptId != ready.ReviewAttemptId || owner.RoomDirectory != room || !owner.IsAdmissionShape()
            || item.LastVerdict != proof.VerdictPath
            || !QueueWorkerAccount.TryValidateSourceMetadata(envelope.AttemptId, proof.ExecutionId, ready.Repository, item.Workspace, out _)
            || !BatonPaths.RecordKeyComparer.Equals(Path.GetFullPath(proof.VerdictPath), Path.GetFullPath(
                Path.Combine(room, "artifacts", "execution_" + proof.ExecutionId, "verdict.json")))
            || ExactBytesDigest(Path.Combine(room, TerminalSentinelWriter.TerminalSentinelFileName)) != proof.TerminalSha256
            || ExactBytesDigest(proof.VerdictPath, 64 * 1024) != ready.VerdictSha256)
            throw new CliArgumentException("Independent review attempt, terminal identity or verdict bytes changed.");
        var verdict = TryReadVerdict(proof.VerdictPath);
        if (verdict is not { Completion: ReviewCompletion.Complete, Decision: ReviewDecision.Approve }
            || verdict.ReviewedRef != ready.HeadSha) throw new CliArgumentException("Exact reviewed head is not approved.");
        var terminal = TerminalSentinelWriter.TryReadAsync(room, CancellationToken.None).GetAwaiter().GetResult();
        if (terminal?.State != "Succeeded" || FindVerdict(terminal) != proof.VerdictPath
            || terminal.Steps.Count(s => s.Execution == proof.ExecutionId && s.LinkedFrom is null && s.State == "Succeeded") != 1)
            throw new CliArgumentException("Independent review terminal outcome is incomplete.");
        var flow = Path.Combine(room, BatonPaths.FlowLogFileName);
        _ = ExactBytesDigest(flow);
        var events = new FlowEventLogReader(flow).ReadAllAsync(CancellationToken.None).GetAwaiter().GetResult();
        var accepted = events.OfType<FlowEvent.ExecutionRequestAccepted>().Where(e => e.Request.ExecutionId.Value == proof.ExecutionId).ToArray();
        if (accepted.Length != 1 || accepted[0].Request.OwnedTaskIdentity != owner with { ExecutionId = proof.ExecutionId }
            || !accepted[0].Request.Outputs.Contains("verdict.json", StringComparer.Ordinal)
            || accepted[0].Request.DeliversBranch != false)
            throw new CliArgumentException("Independent review accepted-execution proof is unavailable.");
        return ConductorFollowSession.MergeDigest(new { proof, ready.VerdictSha256, Flow = ExactBytesDigest(flow) });
    }

    internal async Task<ExactMergeQualification> QualifyExactMergeAsync(QueueItem row, ExactMergeGrant grant, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(120));
        token = deadline.Token;
        var ready = grant.Judgment!.Ready;
        var review = ValidateExactReviewBytes(row, ready);
        if (row.OwnedTask?.Ready is not { } current || ConductorFollowSession.ReadyDigest(current) != grant.Judgment.ReadySha256
            || ready.RequiredEvidence is null || ready.RequiredChecks != PullRequestChecks.Passing
            || row.Repository != grant.Request.Repository || row.PullRequest != grant.Request.PullRequest
            || !Directory.Exists(row.Workspace) || ProjectCeilingStore.TryGet(row.Workspace,
                Path.Combine(BatonPaths.Root, "project-ceilings.json")) is not { IsUnrestricted: true })
            throw new CliArgumentException("Exact merge ready receipt or project action ceiling is unavailable.");
        var observed = await ReadPullRequestAsync(row, token).ConfigureAwait(false);
        if (!observed.Succeeded || observed.IsOpen != true || observed.IsDraft != false
            || observed.HeadSha != grant.Request.HeadSha || observed.Number != grant.Request.PullRequest
            || observed.RequiredChecks != PullRequestChecks.Passing || observed.RequiredEvidence is null
            || observed.RequiredEvidence.PolicySha256 != ready.RequiredEvidence.PolicySha256)
            throw new CliArgumentException("Current exact PR, review, checks or policy no longer qualify.");
        (string Digest, string Actor, ExactMergeServerPolicy Policy) server;
        try { server = await ReadExactServerPolicyAsync(row, token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw new ExactMergeUnsupportedException(); }
        return new(ConductorFollowSession.MergeDigest(row), review, observed.RequiredEvidence, server.Digest, server.Actor,
            DateTimeOffset.UtcNow, server.Policy);
    }

    private async Task<JsonElement> ReadMergeApiAsync(QueueItem row, string endpoint, CancellationToken token)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(token);
        bound.CancelAfter(TimeSpan.FromSeconds(20));
        var result = await _draftGh.RunAsync(row.Workspace,
            ["api", "--hostname", "github.com", "--method", "GET", endpoint, "--header", "X-GitHub-Api-Version: 2022-11-28"],
            bound.Token).WaitAsync(bound.Token).ConfigureAwait(false);
        if (!result.Started || result.ExitCode != 0 || Encoding.UTF8.GetByteCount(result.Stdout) > 1024 * 1024)
            throw new ExactMergeUnsupportedException();
        using var document = JsonDocument.Parse(result.Stdout);
        RequiredCheckEvidenceReader.ValidateJsonKeys(document.RootElement);
        return document.RootElement.Clone();
    }

    private async Task<(string Digest, string Actor, ExactMergeServerPolicy Policy)> ReadExactServerPolicyAsync(QueueItem row, CancellationToken token)
    {
        var prefix = "repos/" + row.Repository!["github.com/".Length..];
        var actor = await ReadMergeApiAsync(row, "user", token).ConfigureAwait(false);
        var login = actor.GetProperty("login").GetString();
        if (login is null || login.Length > 39 || !login.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            throw new ExactMergeUnsupportedException();
        var repository = await ReadMergeApiAsync(row, prefix, token).ConfigureAwait(false);
        var permission = await ReadMergeApiAsync(row, prefix + "/collaborators/" + login + "/permission", token).ConfigureAwait(false);
        var classic = await ReadMergeApiAsync(row, prefix + "/branches/main/protection", token).ConfigureAwait(false);
        var rules = await ReadMergeApiAsync(row, prefix + "/rules/branches/main?per_page=100&page=1", token).ConfigureAwait(false);
        var labels = await ReadMergeApiAsync(row, prefix + "/issues/" + row.PullRequest + "/labels?per_page=100&page=1", token).ConfigureAwait(false);
        var role = permission.GetProperty("role_name").GetString();
        // This supported subset proves enforcement for the authenticated actor, including admins.
        // Missing policy, custom roles, bypass allowances, queues and incomplete collections refuse.
        if (repository.GetProperty("full_name").GetString() != row.Repository["github.com/".Length..]
            || repository.GetProperty("allow_squash_merge").ValueKind != JsonValueKind.True
            || repository.GetProperty("permissions").GetProperty("push").ValueKind != JsonValueKind.True
            || permission.GetProperty("user").GetProperty("login").GetString() != login
            || role is not ("write" or "admin") || permission.GetProperty("permission").GetString() is not ("write" or "admin")
            || classic.GetProperty("enforce_admins").GetProperty("enabled").ValueKind != JsonValueKind.True
            || classic.GetProperty("required_status_checks").ValueKind != JsonValueKind.Object
            || classic.GetProperty("required_status_checks").GetProperty("contexts").GetArrayLength() == 0
            || rules.ValueKind != JsonValueKind.Array || rules.GetArrayLength() >= 100
            || labels.ValueKind != JsonValueKind.Array || labels.GetArrayLength() >= 100
            || labels.EnumerateArray().Any(l => l.GetProperty("name").GetString() == "operator-merge"))
            throw new ExactMergeUnsupportedException();
        var pulls = classic.GetProperty("required_pull_request_reviews");
        if (pulls.ValueKind == JsonValueKind.Object && pulls.TryGetProperty("bypass_pull_request_allowances", out var bypass)
            && bypass.EnumerateObject().Any(p => p.Value.ValueKind != JsonValueKind.Array || p.Value.GetArrayLength() != 0))
            throw new ExactMergeUnsupportedException();
        var rulesets = new List<JsonElement>();
        foreach (var group in rules.EnumerateArray().GroupBy(r => r.GetProperty("ruleset_id").GetInt64()))
        {
            if (rulesets.Count >= 10) throw new ExactMergeUnsupportedException();
            if (group.Key <= 0 || group.Any(r => r.GetProperty("type").GetString() == "merge_queue"
                || r.GetProperty("ruleset_source_type").GetString() != "Repository"
                || r.GetProperty("ruleset_source").GetString() != row.Repository["github.com/".Length..]))
                throw new ExactMergeUnsupportedException();
            var ruleset = await ReadMergeApiAsync(row, prefix + "/rulesets/" + group.Key, token).ConfigureAwait(false);
            if (ruleset.GetProperty("id").GetInt64() != group.Key || ruleset.GetProperty("enforcement").GetString() != "active"
                || ruleset.GetProperty("bypass_actors").GetArrayLength() != 0
                || ruleset.GetProperty("rules").EnumerateArray().Any(r => r.GetProperty("type").GetString() == "merge_queue"))
                throw new ExactMergeUnsupportedException();
            rulesets.Add(ruleset);
        }
        if (rulesets.Count > 10) throw new ExactMergeUnsupportedException();
        var policy = new ExactMergeServerPolicy(actor, repository, permission, classic, rules, rulesets, labels);
        return (ConductorFollowSession.MergeDigest(policy), login, policy);
    }

    internal static IReadOnlyList<string> ExactMergeArguments(ExactMergeRequest request)
    {
        if (request.Repository.Split('/') is not ["github.com", var owner, var repository]
            || !new[] { owner, repository }.All(v => v.Length is > 0 and <= 100
                && v.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
            || request.PullRequest <= 0 || request.HeadSha is not { Length: 40 } || !request.HeadSha.All(char.IsAsciiHexDigit)
            || request.Method != "squash") throw new CliArgumentException("Exact merge argv authority is invalid.");
        return ["api", "--hostname", "github.com", "--method", "PUT",
            $"repos/{owner}/{repository}/pulls/{request.PullRequest.ToString(CultureInfo.InvariantCulture)}/merge",
            "--raw-field", "sha=" + request.HeadSha, "--raw-field", "merge_method=squash",
            "--header", "X-GitHub-Api-Version: 2022-11-28", "--include"];
    }

    internal async Task<ExactMergeExecutorResult> ExecuteExactMergeAsync(QueueItem row, ExactMergeGrant grant, CancellationToken token)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(token);
        bound.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var result = await _draftGh.RunAsync(row.Workspace, ExactMergeArguments(grant.Request), bound.Token)
                .WaitAsync(bound.Token).ConfigureAwait(false);
            return ParseExactMergeResponse(result);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException)
        { return new("unknown", null, null, null, null); }
    }

    internal static ExactMergeExecutorResult ParseExactMergeResponse(GhCliResult result)
    {
        if (!result.Started || Encoding.UTF8.GetByteCount(result.Stdout) > 64 * 1024)
            return new("unknown", result.ExitCode, null, null, null);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(result.Stdout))).ToLowerInvariant();
        try
        {
            var text = result.Stdout.Replace("\r\n", "\n", StringComparison.Ordinal);
            var separator = text.IndexOf("\n\n", StringComparison.Ordinal);
            var first = text.Split('\n')[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (separator < 0 || first.Length < 2 || first[0] is not ("HTTP/2.0" or "HTTP/1.1")
                || !int.TryParse(first[1], CultureInfo.InvariantCulture, out var status))
                return new("unknown", result.ExitCode, null, null, digest, result.Stdout);
            using var document = JsonDocument.Parse(text[(separator + 2)..]);
            RequiredCheckEvidenceReader.ValidateJsonKeys(document.RootElement);
            var root = document.RootElement;
            var sha = root.TryGetProperty("sha", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            var merged = root.TryGetProperty("merged", out var m) ? m.ValueKind : JsonValueKind.Undefined;
            var confirmed = result.ExitCode == 0 && status == 200 && merged == JsonValueKind.True && sha is { Length: 40 } && sha.All(char.IsAsciiHexDigit);
            var refused = status is 405 or 409 or 422 && merged == JsonValueKind.False;
            return new(confirmed ? "confirmed" : refused ? "refused" : "unknown", result.ExitCode, status,
                confirmed ? sha : null, digest, result.Stdout);
        }
        catch (JsonException) { return new("unknown", result.ExitCode, null, null, digest, result.Stdout); }
    }

    internal async Task<ExactMergeObservation> ObserveExactMergeAsync(QueueItem row, ExactMergeGrant grant, CancellationToken token)
    {
        try
        {
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(token);
            bound.CancelAfter(TimeSpan.FromSeconds(20));
            var reading = await ReadPullRequestSnapshotAsync(row with
            {
                PullRequest = grant.Request.PullRequest,
                Workspace = ExistingGhWorkingDirectory(row.Workspace)
            }, bound.Token).WaitAsync(bound.Token).ConfigureAwait(false);
            if (reading.Succeeded && reading.State == "MERGED" && reading.HeadSha == grant.Request.HeadSha
                && reading.MergeSha is { Length: 40 } sha && sha.All(char.IsAsciiHexDigit))
            {
                var confirmed = grant.Attempt is { Executor: "confirmed", CommitSha: { } commit } && commit == sha;
                return new(confirmed ? "merged confirmed and observed" : "merged observed; executor unconfirmed",
                    reading.HeadSha, sha, DateTimeOffset.UtcNow, grant.Request.Holder,
                    confirmed ? "Exact attempt is terminal; permission remains spent." : "Owner must inspect attribution; permission remains spent and no retry is authorized.");
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or OperationCanceledException) { }
        return new("uncertain", null, null, DateTimeOffset.UtcNow, grant.Request.Holder,
            "Owner must reconcile the retained issued identity and forge outcome; no retry authority.");
    }
}
