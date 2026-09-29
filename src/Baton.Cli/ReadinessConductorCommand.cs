using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Accounting;
using Baton.Cli.Daemon;
using Baton.Conductor;
using Baton.Status;
using Baton.Vendors;

namespace Baton.Cli;

/// <summary>Explicit, one-shot owned readiness commands. No daemon or queue transition calls this path.</summary>
internal static class ReadinessConductorCommand
{
    private const int MaxInputBytes = 64 * 1024;
    private const string KeyPrefix = "owned-readiness:";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    internal static async Task<int> PrepareAsync(string file, TextWriter output, string root,
        ConductorObligationStore? store = null, CancellationToken cancellationToken = default)
    {
        var request = Deserialize<ReadinessRequest>(await ReadFileAsync(file, cancellationToken).ConfigureAwait(false));
        ValidateRequest(request);
        await VerifyAuthorityAsync(request.Repository, request.Workspace, request.Revision,
            request.Holder, root, cancellationToken).ConfigureAwait(false);
        var obligations = store ?? OpenStore(root);
        var key = KeyPrefix + request.Key;
        var obligation = await obligations.EnqueueAsync(new ConductorObligationRequest(
            key, request.Repository, null, null, null, "readiness-decision", request.Holder,
            request.ObservedAt, request.Adapter, "one-shot-readiness", true,
            TargetWorkspace: request.Workspace, TargetRevision: request.Revision,
            ContextSha256: request.ContextSha256), cancellationToken).ConfigureAwait(false);
        output.WriteLine(JsonSerializer.Serialize(new
        {
            obligation.ObligationId,
            obligation.IdempotencyKey,
            obligation.Status,
            obligation.TargetProject,
            obligation.TargetRevision,
            obligation.ContextSha256,
        }, Json));
        return 0;
    }

    internal static async Task<int> DecideAsync(string key, string contextFile, TextWriter output, string root,
        ConductorObligationStore? store = null,
        Func<string, ReadinessRequest, ReadinessContext, string, CancellationToken,
            Task<RetainedReadinessResponse>>? launch = null,
        CancellationToken cancellationToken = default)
    {
        if (!key.StartsWith(KeyPrefix, StringComparison.Ordinal))
            throw new CliArgumentException("Readiness obligation key must be the key returned by prepare.");
        var obligations = store ?? OpenStore(root);
        var obligation = await obligations.ReadAsync(key, cancellationToken).ConfigureAwait(false)
            ?? throw new ConductorObligationStoreException($"No conductor obligation exists for '{key}'.");
        if (obligation.RequestedAction != "readiness-decision" || obligation.TargetWorkspace is null
            || obligation.TargetRevision is null || obligation.ContextSha256 is null)
            throw new ConductorObligationStoreException("Obligation is not an owned readiness request.");
        var bytes = await ReadFileAsync(contextFile, cancellationToken).ConfigureAwait(false);
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (digest != obligation.ContextSha256)
            throw new ConductorObligationConflictException(key, "context digest changed");
        var context = Deserialize<ReadinessContext>(bytes);
        ValidateContext(context);
        if (KeyPrefix + context.Key != key || context.Repository != obligation.TargetProject
            || context.Workspace != obligation.TargetWorkspace || context.Revision != obligation.TargetRevision
            || context.ObservedAt.ToUniversalTime() != obligation.CreatedAt)
            throw new ConductorObligationConflictException(key, "context identity or revision differs");
        await VerifyAuthorityAsync(obligation.TargetProject, obligation.TargetWorkspace,
            obligation.TargetRevision, obligation.Owner, root, cancellationToken).ConfigureAwait(false);

        var request = new ReadinessRequest(1, context.Key, obligation.TargetProject,
            obligation.TargetWorkspace, obligation.TargetRevision, obligation.Owner,
            obligation.CreatedAt, obligation.ContextSha256, obligation.Adapter,
            CodexReadinessDecisionAdapter.Model, CodexReadinessDecisionAdapter.Effort);
        if (obligation.Adapter != CodexReadinessDecisionAdapter.AdapterName
            || obligation.AdapterCapability != "one-shot-readiness" || !obligation.AdapterSupported)
            throw new ConductorObligationStoreException("Only Codex subscription readiness decisions are supported.");
        // Reject deterministic provider input limits before the durable launch marker. After that
        // marker, a failure may mean a charged attempt and must remain uncertain until recovery.
        CodexReadinessDecisionAdapter.ValidatePrelaunch(obligation.ObligationId, request, context);
        var invoke = launch ?? new CodexReadinessDecisionAdapter().DecideAsync;
        var result = await obligations.DecideReadinessOnceAsync(key,
            async (item, token) =>
            {
                // Recheck at the launch boundary. A worktree or claim can change after the initial
                // command read; the marker makes any later ambiguity fail closed without another call.
                await VerifyAuthorityAsync(item.TargetProject, item.TargetWorkspace!, item.TargetRevision!,
                    item.Owner, root, token).ConfigureAwait(false);
                return await invoke(item.ObligationId, request, context,
                    obligations.GetReadinessEvidenceDirectory(key), token).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        output.WriteLine(JsonSerializer.Serialize(new
        {
            result.Obligation.ObligationId,
            result.Obligation.IdempotencyKey,
            result.Obligation.Status,
            result.Obligation.TransportReceipt,
            result.Response.Decision,
            result.Response.Usage,
        }, Json));
        return 0;
    }

    private static ConductorObligationStore OpenStore(string root)
    {
        if (string.Equals(Path.GetFullPath(root), Path.GetFullPath(BatonPaths.Root),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return new ConductorObligationStore(FleetEventLog.OpenOperational());
        }

        var fleet = Path.Combine(root, "fleet");
        return new ConductorObligationStore(new FleetEventLog(
            Path.Combine(fleet, "events.jsonl"), Path.Combine(fleet, "events.1.jsonl"),
            16 * 1024 * 1024), Path.Combine(fleet, BatonPaths.ConductorObligationsFileName));
    }

    private static void ValidateRequest(ReadinessRequest value)
    {
        if (value.SchemaVersion != 1 || string.IsNullOrWhiteSpace(value.Key) || value.Key.Length > 128
            || string.IsNullOrWhiteSpace(value.Repository) || string.IsNullOrWhiteSpace(value.Holder)
            || value.Holder.Length > 128 || value.ObservedAt == default
            || !Path.IsPathFullyQualified(value.Workspace)
            || value.Workspace != Path.GetFullPath(value.Workspace)
            || !IsSha(value.Revision, 40) || !IsSha(value.ContextSha256, 64)
            || value.Adapter != CodexReadinessDecisionAdapter.AdapterName
            || value.Model != CodexReadinessDecisionAdapter.Model
            || value.Effort != CodexReadinessDecisionAdapter.Effort)
            throw new CliArgumentException("Invalid readiness request or unsupported adapter/model/effort.");
    }

    private static void ValidateContext(ReadinessContext value)
    {
        if (value.SchemaVersion != 1 || string.IsNullOrWhiteSpace(value.Key)
            || string.IsNullOrWhiteSpace(value.Repository) || !Path.IsPathFullyQualified(value.Workspace)
            || value.Workspace != Path.GetFullPath(value.Workspace) || !IsSha(value.Revision, 40)
            || value.ObservedAt == default || value.Evidence is null || value.Evidence.Count > 32
            || value.Evidence.Any(item => item is null || string.IsNullOrWhiteSpace(item.Name)
                || string.IsNullOrWhiteSpace(item.Status) || string.IsNullOrWhiteSpace(item.Detail)
                || item.Name.Length > 128 || item.Status.Length > 128 || item.Detail.Length > 1024))
            throw new CliArgumentException("Invalid bounded readiness context.");
    }

    private static bool IsSha(string? value, int length) =>
        value is not null && value.Length == length && value.All(c => char.IsAsciiHexDigit(c))
        && value == value.ToLowerInvariant();

    private static T Deserialize<T>(byte[] bytes)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, Json)
                ?? throw new CliArgumentException("Readiness input cannot be JSON null.");
        }
        catch (JsonException ex)
        {
            throw new CliArgumentException($"Readiness input is not a valid typed JSON document: {ex.Message}");
        }
    }

    private static async Task<byte[]> ReadFileAsync(string file, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxInputBytes)
            throw new CliArgumentException("Readiness input file exceeds 64 KiB.");
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int count;
        while ((count = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + count > MaxInputBytes)
                throw new CliArgumentException("Readiness input file exceeds 64 KiB.");
            buffer.Write(chunk, 0, count);
        }

        return buffer.ToArray();
    }

    private static async Task VerifyAuthorityAsync(string repository, string workspace, string revision,
        string holder, string root, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(workspace) || !RepositoryIdentityResolver.IsWorkTreeRoot(workspace))
            throw new CliArgumentException("Readiness workspace must be an existing Git worktree root.");
        string? origin;
        try
        {
            origin = await GitAsync(workspace, cancellationToken, "config", "--get", "remote.origin.url")
                .ConfigureAwait(false);
        }
        catch (CliArgumentException)
        {
            origin = null;
        }

        var commonDir = await GitAsync(workspace, cancellationToken, "rev-parse",
            "--path-format=absolute", "--git-common-dir").ConfigureAwait(false);
        var identity = RepositoryIdentity.From(origin, commonDir);
        if (identity is null || identity.Value != repository)
            throw new CliArgumentException("Readiness repository does not match the canonical workspace identity.");
        var claim = await ConductorClaimStore.GetClaimAsync(identity, root, cancellationToken).ConfigureAwait(false);
        if (claim is null || claim.Holder != holder)
            throw new CliArgumentException("Readiness request holder does not own the repository claim.");
        var head = await GitAsync(workspace, cancellationToken, "rev-parse", "HEAD").ConfigureAwait(false);
        var status = await GitAsync(workspace, cancellationToken, "status", "--porcelain=v1", "--untracked-files=all")
            .ConfigureAwait(false);
        if (head != revision || status.Length != 0)
            throw new CliArgumentException("Readiness workspace is dirty or HEAD differs from the exact request revision.");
    }

    private static async Task<string> GitAsync(string workspace, CancellationToken cancellationToken,
        params string[] arguments)
    {
        var git = OutsideWorkspaceExecutableResolver.TryResolve(
            Environment.GetEnvironmentVariable("PATH"), workspace, "git", OperatingSystem.IsWindows())
            ?? throw new CliArgumentException("Could not resolve a trusted absolute Git executable outside the workspace.");
        using var child = ChildProcessTree.Start(git, info =>
        {
            info.WorkingDirectory = workspace;
            info.Environment["GIT_NO_LAZY_FETCH"] = "1";
            info.Environment["GIT_OPTIONAL_LOCKS"] = "0";
            info.Environment["GIT_TERMINAL_PROMPT"] = "0";
            foreach (var argument in new[] { "-c", "core.fsmonitor=false", "-c",
                "core.untrackedCache=false", "-c", "credential.helper=" }.Concat(arguments))
                info.ArgumentList.Add(argument);
        });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var stdout = ReadGitOutputAsync(child.StandardOutput, timeout.Token);
            var stderr = ReadGitOutputAsync(child.StandardError, timeout.Token);
            await child.Process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var result = await stdout.ConfigureAwait(false);
            var error = await stderr.ConfigureAwait(false);
            if (child.Process.ExitCode != 0)
                throw new CliArgumentException($"Git preflight failed: {error.Trim()}");
            return result.Trim();
        }
        catch
        {
            child.Terminate();
            throw;
        }
    }

    private static async Task<string> ReadGitOutputAsync(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[1024];
        var text = new StringBuilder();
        int count;
        while ((count = await reader.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (text.Length + count > 16 * 1024)
                throw new CliArgumentException("Git preflight output exceeds 16 KiB.");
            text.Append(buffer, 0, count);
        }

        return text.ToString();
    }
}
