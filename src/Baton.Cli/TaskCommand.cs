using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Baton.Accounting;
using Baton.Conductor;
using Baton.Domain;
using Baton.Queue;
using Baton.Status;

namespace Baton.Cli;

/// <summary>One issue-owned lifecycle over the existing queue and daemon. Submission never launches.</summary>
public static class TaskCommand
{
    public static Task<int> ExecuteAsync(TaskOptions options, TextWriter output, CancellationToken cancellationToken = default)
        => ExecuteAsync(options, output, RepositoryIdentityResolver.TryResolveAsync,
            static (issue, source, root, repository, lifecycle, writer, token) =>
                IssueWorktreeProvisioner.ProvisionAsync(issue, source, root, repository,
                    output: writer, cancellationToken: token, deterministicSourceCeiling: lifecycle),
            cancellationToken);

    internal static async Task<int> ExecuteAsync(
        TaskOptions options,
        TextWriter output,
        Func<string, CancellationToken, Task<RepositoryIdentity?>> repositoryResolver,
        Func<int, string, string?, string, bool, TextWriter, CancellationToken,
            Task<IssueWorktreeProvisioner.ProvisionedIssueWorktree>> issueProvisioner,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        if (options.Verb == TaskVerb.Status)
        {
            await PrintStatusAsync(options.Id!, options.Json, output, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        var project = Path.GetFullPath(options.Project!);
        if (!Directory.Exists(project)) throw new CliArgumentException($"Project '{project}' does not exist.");
        if (options.Spec is { } spec && !File.Exists(spec))
            throw new CliArgumentException($"Task spec '{spec}' does not exist.");
        var identity = await repositoryResolver(project, cancellationToken).ConfigureAwait(false);
        if (identity?.RemoteValue is null)
            throw new CliArgumentException($"Project '{project}' does not resolve to a canonical remote repository.");
        var issue = options.Issue!.Value;
        var id = TaskId(identity.Value, issue);
        var specBytes = options.Spec is null ? [] : await File.ReadAllBytesAsync(options.Spec, cancellationToken)
            .ConfigureAwait(false);
        var explicitHeader = $"{identity.Value}\n{issue}\n{options.Size!.Value.Size}\n{options.Size.Value.Rationale}\n";
        var digest = Convert.ToHexString(SHA256.HashData([
            .. Encoding.UTF8.GetBytes(explicitHeader), .. specBytes])).ToLowerInvariant();
        var existing = (await QueueStore.LoadAsync(BatonPaths.QueueFile, cancellationToken).ConfigureAwait(false))
            .Items.FirstOrDefault(item => item.OwnedTask?.Id == id);
        if (existing is not null)
        {
            if (existing.OwnedTask!.InputDigest != digest)
                throw new CliArgumentException($"Task '{id}' already retains different explicit submission input.");
            await PrintStatusAsync(id, false, output, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        var claim = await ConductorClaimStore.GetClaimAsync(identity, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (claim?.Holder is not { Length: > 0 } holder || claim.Repository != identity.Value)
            throw new CliArgumentException($"Repository '{identity.Value}' needs an existing conductor claim before task submission.");

        var owned = new OwnedTaskSubmission(id, identity.Value, issue, digest, holder, DateTimeOffset.UtcNow);
        var queueOptions = new QueueOptions(QueueVerb.Add, Tag: id, Role: "implement",
            SpecFilePath: options.Spec, Issue: issue, Lifecycle: true,
            DeclaredTaskSize: options.Size, Requirements: []);

        // QueueCommand owns the shared locked reservation and the exact existing provisioning path.
        // Its queue-oriented prose stays internal; this front door returns the task receipt.
        try
        {
            await QueueCommand.ExecuteAsync(queueOptions, new StringWriter(), cancellationToken, project,
                repositoryResolver, issueProvisioner, ownedTask: owned).ConfigureAwait(false);
        }
        catch (Exception) when (HasRetainedBlockedPreparation(id))
        {
            // An external preparation failed after the reservation. The retained blocked task is the
            // outcome; a retry must not provision another branch or overwrite the original brief.
        }

        await PrintStatusAsync(id, false, output, cancellationToken).ConfigureAwait(false);
        return 0;
    }

    internal static string TaskId(string repository, int issue)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{repository}\0{issue}"));
        return "task-" + Convert.ToHexString(bytes).ToLowerInvariant()[..58];
    }

    private static bool HasRetainedBlockedPreparation(string id)
    {
        try
        {
            var snapshot = QueueStore.LoadAsync(BatonPaths.QueueFile).GetAwaiter().GetResult();
            return snapshot.Items.Any(i => i.OwnedTask?.Id == id
                && i.IssuePreparation?.State == TaskPreparationState.Blocked);
        }
        catch (QueueStoreException) { return false; }
    }

    private static async Task PrintStatusAsync(string id, bool json, TextWriter output, CancellationToken token)
    {
        var snapshot = await QueueStore.LoadAsync(BatonPaths.QueueFile, token).ConfigureAwait(false);
        var item = snapshot.Items.SingleOrDefault(i => i.OwnedTask?.Id == id)
            ?? throw new CliArgumentException($"No retained task has ID '{id}'.");
        var owner = item.OwnedTask!;
        var now = DateTimeOffset.UtcNow;
        var repository = RepositoryIdentity.From("https://" + owner.Repository, null)
            ?? throw new QueueStoreException($"Task '{id}' has an invalid retained repository identity.");
        var currentClaim = await ConductorClaimStore.GetClaimAsync(repository, cancellationToken: token)
            .ConfigureAwait(false);
        var ownership = currentClaim?.Holder is null ? "unclaimed"
            : currentClaim.Holder == owner.ConductorHolder ? "recorded-holder-current" : "holder-changed";
        var heartbeat = ReadDaemonObservation(now);
        var abandonedPreparation = item.IssuePreparation is { State: TaskPreparationState.Preparing } preparing
            && !TaskPreparationLiveness.IsOwnerAlive(preparing);
        var reason = item.IssuePreparation?.State switch
        {
            TaskPreparationState.Preparing => abandonedPreparation
                ? "preparation-owner-exited-unverified" : "preparation-in-progress",
            TaskPreparationState.Blocked => item.IssuePreparation.Reason ?? "preparation-blocked",
            _ when snapshot.Held && item.State == QueueItemState.Queued => "queue-held",
            _ when item.State == QueueItemState.Queued && !heartbeat.Available => "daemon-unavailable",
            _ => null,
        };
        if (reason is null && item.State == QueueItemState.Queued && item.Stage != WorkStage.Ready)
        {
            var decisions = await QueueDecisionLedgerStore.ReadAllAsync(
                BatonPaths.QueueDecisionLedgerFile, token).ConfigureAwait(false);
            var latest = decisions.LastOrDefault(d => d.Tag == item.Tag && d.Decision == QueueDecisionEntry.Waited);
            reason = latest is null ? "awaiting-daemon-decision" : latest.Reason;
        }

        var currentPr = snapshot.PullRequestObservations?.LastOrDefault(o =>
            o.Repository == owner.Repository && o.PullRequest == item.PullRequest);
        var readiness = owner.Ready;
        var headChanged = readiness is not null && currentPr?.HeadSha is { } observedHead
            && !string.Equals(observedHead, readiness.HeadSha, StringComparison.Ordinal);
        var state = item.Retirement is not null ? "retired"
            : item.State == QueueItemState.Cancelled ? "cancelled"
            : item.IssuePreparation?.State == TaskPreparationState.Preparing
                ? abandonedPreparation ? "blocked" : "preparing"
            : item.IssuePreparation?.State == TaskPreparationState.Blocked || item.Halted ? "blocked"
            : item.State == QueueItemState.Launched ? "running"
            : readiness is not null ? item.Stage == WorkStage.Ready && !headChanged
                ? "ready-as-of" : "stale"
            : "queued";
        var nextTrigger = state switch
        {
            "preparing" => "preparation-completion",
            "queued" or "running" => "daemon-tick",
            "blocked" => "conductor-judgment",
            "stale" => "conductor-reassessment",
            _ => "none",
        };
        var status = new
        {
            taskId = owner.Id,
            repository = owner.Repository,
            issue = owner.Issue,
            conductorHolder = owner.ConductorHolder,
            currentConductorHolder = currentClaim?.Holder,
            ownership,
            state,
            reason,
            nextTrigger,
            stage = item.Stage is { } stage ? WorkStages.Token(stage) : null,
            attemptId = item.AttemptId?.Value,
            pullRequest = item.PullRequest,
            headSha = currentPr?.HeadSha,
            pullRequestObservation = new
            {
                observedAt = currentPr?.ObservedAt,
                ageSeconds = currentPr?.ObservedAt is { } prObservedAt
                    ? (int?)(now - prObservedAt).TotalSeconds : null,
                missingEvidence = currentPr is null ? "no-current-pr-observation" : currentPr.Error,
            },
            ready = readiness,
            blocked = owner.Blocked,
            haltCause = item.StoppedWorkJudgment?.HaltCause.ToString(),
            obligationKey = item.StoppedWorkJudgment?.Key,
            daemon = new
            {
                availability = heartbeat.Available ? "recently-observed" : "unavailable",
                observedAt = heartbeat.ObservedAt,
                ageSeconds = heartbeat.ObservedAt is { } at
                    ? (int?)(now - at).TotalSeconds : null
            },
        };
        if (json)
        {
            output.WriteLine(JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            output.WriteLine($"Task {owner.Id}: {state}" + (reason is null ? "" : $" ({reason})"));
            output.WriteLine($"  issue: {owner.Repository}#{owner.Issue}; conductor: {owner.ConductorHolder} ({ownership}"
                + (currentClaim?.Holder is { } current ? $", current {current}" : "") + ")");
            output.WriteLine($"  stage: {status.stage ?? "preparation"}; PR: {(item.PullRequest is null ? "none" : $"#{item.PullRequest}")}");
            output.WriteLine($"  next: {nextTrigger}; PR head: {status.headSha ?? "not observed"}");
            output.WriteLine($"  daemon: {status.daemon.availability}"
                + (heartbeat.ObservedAt is { } lastObserved ? $" (last observed {lastObserved:O})" : " (no observation)"));
            if (readiness is not null) output.WriteLine($"  ready receipt: {readiness.Id} at {readiness.ReadyObservedAt:O}");
            if (owner.Blocked is { } blocked) output.WriteLine($"  blocker: {blocked.ReasonCode}; {blocked.Evidence}");
        }
    }

    private static (bool Available, DateTimeOffset? ObservedAt) ReadDaemonObservation(DateTimeOffset now)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(BatonPaths.FleetHeartbeatFile));
            var root = document.RootElement;
            var observedAt = root.GetProperty("tickCompletedAt").GetDateTimeOffset();
            var identity = root.GetProperty("identity");
            var pid = identity.GetProperty("pid").GetInt32();
            var processStartedAt = identity.GetProperty("processStartTime").GetDateTimeOffset();
            using var process = Process.GetProcessById(pid);
            var sameProcess = Math.Abs((process.StartTime.ToUniversalTime() - processStartedAt).TotalSeconds) < 2;
            return (sameProcess && now >= observedAt && now - observedAt < TimeSpan.FromSeconds(90), observedAt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or KeyNotFoundException or InvalidOperationException or ArgumentException
            or System.ComponentModel.Win32Exception)
        {
            return (false, null);
        }
    }
}
