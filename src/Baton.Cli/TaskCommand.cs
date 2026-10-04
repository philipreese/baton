using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
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
        CancellationToken cancellationToken = default,
        Func<string, IReadOnlyList<string>, string, CancellationToken, Task<(int ExitCode, string Output)>>? preparationRunner = null,
        Func<int, DateTimeOffset>? processStartTimeAccessor = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        if (options.Verb == TaskVerb.Status)
        {
            await PrintStatusAsync(options.Id!, options.Json, output, cancellationToken,
                processStartTimeAccessor, utcNow).ConfigureAwait(false);
            return 0;
        }

        var scopeClass = NormalizeScopeClass(options.ScopeClass);
        var hasImplementationAxis = options.Adapter is not null || options.Model is not null || options.Effort is not null;
        if (options.Reason is not null && (scopeClass is null || !hasImplementationAxis))
            throw new CliArgumentException("'--reason' requires '--scope' and at least one explicit implement axis.");
        if (scopeClass is not null && hasImplementationAxis && string.IsNullOrWhiteSpace(options.Reason))
            throw new CliArgumentException(
                "An explicit adapter, model or effort combined with '--scope' requires a non-blank '--reason'.");

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
        var selection = options.Adapter is null && options.Model is null && options.Effort is null
            ? null
            : new QueueStageSelection
            {
                Stage = WorkStage.Implement,
                Adapter = options.Adapter,
                Model = options.Model,
                Effort = options.Effort,
                Reason = options.Reason,
            };
        var digest = ComputeInputDigest(
            identity.Value, issue, options.Size!.Value, options.Spec is null ? null : specBytes, selection,
            scopeClass, options.Reason);
        var existing = (await QueueStore.LoadAsync(BatonPaths.QueueFile, cancellationToken).ConfigureAwait(false))
            .Items.FirstOrDefault(item => item.OwnedTask?.Id == id);
        if (existing is not null)
        {
            if (existing.OwnedTask!.InputDigest != digest)
                throw new CliArgumentException($"Task '{id}' already retains different explicit submission input.");
            await PrintStatusAsync(id, false, output, cancellationToken,
                processStartTimeAccessor, utcNow).ConfigureAwait(false);
            return 0;
        }

        var claim = await ConductorClaimStore.GetClaimAsync(identity, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (claim?.Holder is not { Length: > 0 } holder || claim.Repository != identity.Value)
            throw new CliArgumentException($"Repository '{identity.Value}' needs an existing conductor claim before task submission.");

        var owned = new OwnedTaskSubmission(id, identity.Value, issue, digest, holder, DateTimeOffset.UtcNow);
        var queueOptions = new QueueOptions(QueueVerb.Add, Tag: id, Role: "implement",
            SpecFilePath: options.Spec, Issue: issue, Lifecycle: true,
            DeclaredTaskSize: options.Size, Requirements: [],
            ScopeClass: scopeClass,
            StageSelections: selection is null ? null : [selection]);

        // QueueCommand owns the shared locked reservation and the exact existing provisioning path.
        // Its queue-oriented prose stays internal; this front door returns the task receipt.
        try
        {
            await QueueCommand.ExecuteAsync(queueOptions, new StringWriter(), cancellationToken, project,
                repositoryResolver, issueProvisioner, ownedTask: owned,
                capturedSpecBytes: options.Spec is null ? null : specBytes,
                preparationRunner: preparationRunner).ConfigureAwait(false);
        }
        catch (Exception) when (HasRetainedBlockedPreparation(id))
        {
            // An external preparation failed after the reservation. The retained blocked task is the
            // outcome; a retry must not provision another branch or overwrite the original brief.
        }

        await PrintStatusAsync(id, false, output, cancellationToken,
            processStartTimeAccessor, utcNow).ConfigureAwait(false);
        return 0;
    }

    internal static string TaskId(string repository, int issue)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{repository}\0{issue}"));
        return "task-" + Convert.ToHexString(bytes).ToLowerInvariant()[..58];
    }

    /// <summary>
    /// No-selection submissions preserve the exact historical newline-header preimage (legacy
    /// digest/idempotency fixtures depend on these exact bytes). A selected submission instead uses a
    /// distinct, domain-separated, typed/length-delimited encoding of the complete explicit input —
    /// never suffixed onto the ambiguous legacy header, so a delimiter/newline/spec-marker byte inside
    /// a rationale or spec cannot alias two different submissions onto the same digest.
    /// </summary>
    internal static string ComputeInputDigest(
        string repository, int issue, TaskSizeDeclaration size, byte[]? specBytes, QueueStageSelection? selection,
        string? scopeClass = null, string? reason = null)
    {
        var effectiveReason = reason ?? selection?.Reason;
        if (scopeClass is null && effectiveReason is null && selection is null)
        {
            var explicitHeader = $"{repository}\n{issue}\n{size.Size}\n{size.Rationale}\n"
                + (specBytes is null ? "no-spec\n" : "spec\n");
            return Convert.ToHexString(SHA256.HashData([
                .. Encoding.UTF8.GetBytes(explicitHeader), .. (specBytes ?? [])])).ToLowerInvariant();
        }

        if (scopeClass is not null || effectiveReason is not null)
        {
            var scopedBuffer = new List<byte>();
            AppendField(scopedBuffer, Encoding.UTF8.GetBytes(ScopedInputDomain));
            AppendField(scopedBuffer, Encoding.UTF8.GetBytes(repository));
            AppendField(scopedBuffer, Encoding.UTF8.GetBytes(issue.ToString(CultureInfo.InvariantCulture)));
            AppendField(scopedBuffer, Encoding.UTF8.GetBytes(size.Size.ToString()));
            AppendOptionalField(scopedBuffer, size.Rationale is null ? null : Encoding.UTF8.GetBytes(size.Rationale));
            AppendOptionalField(scopedBuffer, specBytes);
            AppendOptionalField(scopedBuffer, selection?.Adapter is { } adapter
                ? Encoding.UTF8.GetBytes(adapter) : null);
            AppendOptionalField(scopedBuffer, selection?.Model is { } model
                ? Encoding.UTF8.GetBytes(model) : null);
            AppendOptionalField(scopedBuffer, selection?.Effort is { } effort
                ? Encoding.UTF8.GetBytes(effort) : null);
            AppendOptionalField(scopedBuffer, scopeClass is { } scope
                ? Encoding.UTF8.GetBytes(scope) : null);
            AppendOptionalField(scopedBuffer, effectiveReason is { } rationale
                ? Encoding.UTF8.GetBytes(rationale) : null);
            return Convert.ToHexString(SHA256.HashData(scopedBuffer.ToArray())).ToLowerInvariant();
        }

        var buffer = new List<byte>();
        AppendField(buffer, Encoding.UTF8.GetBytes(SelectedInputDomain));
        AppendField(buffer, Encoding.UTF8.GetBytes(repository));
        AppendField(buffer, Encoding.UTF8.GetBytes(issue.ToString(CultureInfo.InvariantCulture)));
        AppendField(buffer, Encoding.UTF8.GetBytes(size.Size.ToString()));
        AppendOptionalField(buffer, size.Rationale is null ? null : Encoding.UTF8.GetBytes(size.Rationale));
        AppendOptionalField(buffer, specBytes);
        AppendOptionalField(buffer, selection!.Adapter is null ? null : Encoding.UTF8.GetBytes(selection.Adapter));
        AppendOptionalField(buffer, selection.Model is null ? null : Encoding.UTF8.GetBytes(selection.Model));
        AppendOptionalField(buffer, selection.Effort is null ? null : Encoding.UTF8.GetBytes(selection.Effort));
        return Convert.ToHexString(SHA256.HashData(buffer.ToArray())).ToLowerInvariant();
    }

    // "v1" of the selected-submission domain. Bumping this value is the only way the encoding below
    // may ever change shape; a version bump is itself a new domain, never a mutation of this one.
    private const string SelectedInputDomain = "baton-task-selected-input-v1";

    // New scope/rationale fields use a new domain so selected-input-v1 remains byte-for-byte stable.
    private const string ScopedInputDomain = "baton-task-scoped-input-v1";

    private static string? NormalizeScopeClass(string? scopeClass)
    {
        if (scopeClass is null)
            return null;

        var normalized = scopeClass.Trim().ToLowerInvariant();
        if (!QueueTierTable.ScopeClasses.Contains(normalized, StringComparer.Ordinal))
            throw new CliArgumentException(
                $"Unknown scope class '{scopeClass}'. Pass one of: {string.Join(", ", QueueTierTable.ScopeClasses)}.");
        return normalized;
    }

    /// <summary>A present, always-required field: a 1-byte present tag, a 4-byte big-endian length,
    /// then the bytes. The length prefix is what makes two fields' boundary unambiguous regardless of
    /// what bytes either one contains.</summary>
    private static void AppendField(List<byte> buffer, byte[] value)
    {
        buffer.Add(1);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        buffer.AddRange(length.ToArray());
        buffer.AddRange(value);
    }

    /// <summary>A nullable field: a 1-byte tag (0 = absent, nothing follows; 1 = present, as
    /// <see cref="AppendField"/>). Absence and an explicit zero-length value are never the same byte
    /// sequence.</summary>
    private static void AppendOptionalField(List<byte> buffer, byte[]? value)
    {
        if (value is null)
        {
            buffer.Add(0);
            return;
        }

        AppendField(buffer, value);
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

    private static async Task PrintStatusAsync(
        string id,
        bool json,
        TextWriter output,
        CancellationToken token,
        Func<int, DateTimeOffset>? processStartTimeAccessor = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        var snapshot = await QueueStore.LoadAsync(BatonPaths.QueueFile, token).ConfigureAwait(false);
        var item = snapshot.Items.SingleOrDefault(i => i.OwnedTask?.Id == id)
            ?? throw new CliArgumentException($"No retained task has ID '{id}'.");
        var owner = item.OwnedTask!;
        var now = utcNow is null ? DateTimeOffset.UtcNow : utcNow();
        var repository = RepositoryIdentity.From("https://" + owner.Repository, null)
            ?? throw new QueueStoreException($"Task '{id}' has an invalid retained repository identity.");
        var currentClaim = await ConductorClaimStore.GetClaimAsync(repository, cancellationToken: token)
            .ConfigureAwait(false);
        var ownership = currentClaim?.Holder is null ? "unclaimed"
            : currentClaim.Holder == owner.ConductorHolder ? "recorded-holder-current" : "holder-changed";
        var heartbeat = ReadDaemonObservation(now, processStartTimeAccessor ?? ReadProcessStartTime);
        var abandonedPreparation = item.IssuePreparation is { State: TaskPreparationState.Preparing } preparing
            && !TaskPreparationLiveness.IsOwnerAlive(preparing);
        var reason = item.IssuePreparation?.State switch
        {
            TaskPreparationState.Preparing => abandonedPreparation
                ? "preparation-owner-exited-unverified" : "preparation-in-progress",
            TaskPreparationState.Blocked => item.IssuePreparation.Reason ?? "preparation-blocked",
            _ when snapshot.Held && item.State == QueueItemState.Queued => "queue-held",
            _ when item.State == QueueItemState.Queued && heartbeat.Availability == DaemonAvailability.Unknown
                => "daemon-observation-unknown",
            _ when item.State == QueueItemState.Queued && heartbeat.Availability == DaemonAvailability.Unavailable
                => "daemon-unavailable",
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
        // Checks is the aggregate display word, not required-check policy. An optional failure
        // can coexist with passing required checks; the advancer's retained reconciliation error
        // and required-check wait are the authority for a current readiness regression.
        var readinessRegressed = readiness is not null
            && (item.Error is not null || item.RequiredCheckEvidenceWait is not null);
        var state = item.Retirement is not null ? "retired"
            : item.State == QueueItemState.Cancelled ? "cancelled"
            : item.IssuePreparation?.State == TaskPreparationState.Preparing
                ? abandonedPreparation ? "blocked" : "preparing"
            : item.IssuePreparation?.State == TaskPreparationState.Blocked || item.Halted
                || item.State == QueueItemState.Failed ? "blocked"
            : item.State == QueueItemState.Launched ? "running"
            : readiness is not null ? item.Stage == WorkStage.Ready && !headChanged && !readinessRegressed
                ? "ready-as-of" : "stale"
            : "queued";
        // Failed rows are not scheduler candidates. Report retained failure evidence without
        // inventing a blocked receipt or promising a daemon retry (#2530).
        if (state == "blocked" && item.State == QueueItemState.Failed
            && item.IssuePreparation?.State is not (TaskPreparationState.Preparing or TaskPreparationState.Blocked))
            reason = string.IsNullOrWhiteSpace(item.Error) ? "task-failed" : item.Error;
        if (state == "stale")
            reason = item.Error ?? (headChanged ? "observed-pr-head-changed" : "readiness-evidence-no-longer-current");
        // Retirement is the current disposition; an older attempt error remains history, not its
        // reason. Legacy records with no reason say so without inventing delivery or recovery.
        if (item.Retirement is { } retirement)
            reason = string.IsNullOrWhiteSpace(retirement.Reason)
                ? "retirement-reason-unavailable" : retirement.Reason;
        var nextTrigger = state switch
        {
            "preparing" => "preparation-completion",
            "queued" or "running" => "daemon-tick",
            "blocked" => "conductor-judgment",
            "stale" => "conductor-reassessment",
            _ => "none",
        };
        var currentStoppedWork = state == "blocked"
            && owner.Blocked?.ObligationKey is { } currentKey
            && !string.IsNullOrWhiteSpace(currentKey)
            && item.StoppedWorkJudgment?.Key is { } retainedKey
            && !string.IsNullOrWhiteSpace(retainedKey)
            && string.Equals(currentKey, retainedKey, StringComparison.Ordinal)
            ? item.StoppedWorkJudgment
            : null;
        var stoppedWorkHistory = item.StoppedWorkJudgment is { } stopped
            ? new
            {
                haltCause = stopped.HaltCause.ToString(),
                obligationKey = stopped.Key,
                attemptId = stopped.AttemptId?.Value,
                stage = WorkStages.Token(stopped.Stage),
                observedAt = stopped.ObservedAt,
            }
            : null;
        // Retained routing facts only: the normalized implement-stage plan and the frozen pre-launch
        // decision, neither reconstructed from settings/ready receipts nor borrowed from the review
        // stage's distinct AttemptEnvelope tuple (#2566).
        var initialWorkerSelection = item.StageSelections?.SingleOrDefault(s => s.Stage == WorkStage.Implement)
            is { } implementSelection
            ? new
            {
                adapter = implementSelection.Adapter,
                model = implementSelection.Model,
                effort = implementSelection.Effort,
            }
            : null;
        var retainedWorkerAssignment = item.WorkerAssignment is { } assignment
            ? new
            {
                adapter = assignment.Adapter,
                model = assignment.Model,
                effort = assignment.Effort,
                decisionId = assignment.DecisionId,
                poolHash = assignment.PoolHash,
                closedReason = assignment.ClosedReason,
                decidedAt = assignment.DecidedAt,
            }
            : null;
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
            retirement = item.Retirement,
            nextTrigger,
            stage = item.Stage is { } stage ? WorkStages.Token(stage) : null,
            attemptId = item.AttemptId?.Value,
            pullRequest = item.PullRequest,
            headSha = currentPr?.HeadSha,
            latestChecks = new
            {
                state = item.Checks,
                headSha = item.ChecksHeadSha,
                observedAt = item.ChecksObservedAt,
                error = item.Error
            },
            pullRequestObservation = new
            {
                observedAt = currentPr?.ObservedAt,
                ageSeconds = currentPr?.ObservedAt is { } prObservedAt
                    ? (int?)(now - prObservedAt).TotalSeconds : null,
                missingEvidence = currentPr is null ? "no-current-pr-observation" : currentPr.Error,
            },
            ready = readiness,
            blocked = owner.Blocked,
            haltCause = currentStoppedWork?.HaltCause.ToString(),
            obligationKey = currentStoppedWork?.Key,
            stoppedWorkHistory,
            initialWorkerSelection,
            retainedWorkerAssignment,
            daemon = new
            {
                availability = heartbeat.Availability switch
                {
                    DaemonAvailability.RecentlyObserved => "recently-observed",
                    DaemonAvailability.Unavailable => "unavailable",
                    _ => "unknown",
                },
                reason = heartbeat.Reason,
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
            if (item.Retirement is { } recordedRetirement)
                output.WriteLine($"  retirement: {recordedRetirement.Kind} at {recordedRetirement.At:O}; reason: {reason}");
            output.WriteLine($"  stage: {status.stage ?? "preparation"}; PR: {(item.PullRequest is null ? "none" : $"#{item.PullRequest}")}");
            output.WriteLine($"  next: {nextTrigger}; PR head: {status.headSha ?? "not observed"}");
            if (item.ChecksObservedAt is not null || item.Error is not null)
                output.WriteLine($"  latest checks{(item.Retirement is null ? "" : " (historical)")}: {item.Checks ?? "unknown"}"
                    + (item.ChecksObservedAt is { } checksAt ? $" at {checksAt:O}" : "")
                    + (item.Error is null ? "" : $"; {item.Error}"));
            output.WriteLine($"  daemon: {status.daemon.availability}"
                + (heartbeat.Reason is { } daemonReason ? $" (reason {daemonReason})" : "")
                + (heartbeat.ObservedAt is { } lastObserved
                    ? heartbeat.Availability != DaemonAvailability.RecentlyObserved
                        ? $" (recorded heartbeat time {lastObserved:O}; liveness not verified)"
                        : $" (last observed {lastObserved:O})"
                    : " (no observation)"));
            if (readiness is not null) output.WriteLine($"  ready receipt: {readiness.Id} at {readiness.ReadyObservedAt:O}");
            if (initialWorkerSelection is { } selected)
                output.WriteLine("  initial implement selection (retained plan): "
                    + $"adapter={selected.adapter ?? "none"}; model={selected.model ?? "none"}; effort={selected.effort ?? "none"}");
            if (retainedWorkerAssignment is { } retainedAssignment)
                output.WriteLine("  retained worker assignment (as-of; not proof of liveness or vendor use): "
                    + $"adapter={retainedAssignment.adapter}; model={retainedAssignment.model ?? "none"}; "
                    + $"effort={retainedAssignment.effort ?? "none"}; decidedAt={retainedAssignment.decidedAt:O}");
            if (owner.Blocked is { } blocked)
            {
                var blockerLabel = state == "blocked" ? "current blocker" : "retained blocker (historical)";
                output.WriteLine($"  {blockerLabel}: {blocked.ReasonCode}; {blocked.Evidence}");
            }
            if (currentStoppedWork is { } linked)
                output.WriteLine($"  current stopped-work blocker: haltCause={linked.HaltCause}; obligationKey={linked.Key}");
            if (stoppedWorkHistory is { } history)
                output.WriteLine("  stopped-work history (one retained as-of snapshot; not latest or complete history):"
                    + $" haltCause={history.haltCause}; obligationKey={history.obligationKey ?? "none"}"
                    + $"; attemptId={history.attemptId ?? "none"}; stage={history.stage}; observedAt={history.observedAt:O}");
        }
    }

    private enum DaemonAvailability
    {
        RecentlyObserved,
        Unavailable,
        Unknown,
    }

    private sealed record DaemonObservation(
        DaemonAvailability Availability,
        string? Reason,
        DateTimeOffset? ObservedAt);

    private static DateTimeOffset ReadProcessStartTime(int pid)
    {
        using var process = Process.GetProcessById(pid);
        return process.StartTime.ToUniversalTime();
    }

    private static DaemonObservation ReadDaemonObservation(
        DateTimeOffset now,
        Func<int, DateTimeOffset> processStartTimeAccessor)
    {
        string heartbeat;
        try
        {
            heartbeat = File.ReadAllText(BatonPaths.FleetHeartbeatFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(DaemonAvailability.Unavailable, "heartbeat-unavailable", null);
        }

        DateTimeOffset observedAt;
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(heartbeat);
            root = document.RootElement.Clone();
            observedAt = root.GetProperty("tickCompletedAt").GetDateTimeOffset();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException
            or ArgumentException
            or FormatException or OverflowException)
        {
            return new(DaemonAvailability.Unavailable, "heartbeat-unavailable", null);
        }

        if (now < observedAt)
            return new(DaemonAvailability.Unavailable, "heartbeat-in-future", null);
        if (now - observedAt >= TimeSpan.FromSeconds(90))
            return new(DaemonAvailability.Unavailable, "heartbeat-stale", observedAt);

        int pid;
        DateTimeOffset processStartedAt;
        try
        {
            var identity = root.GetProperty("identity");
            pid = identity.GetProperty("pid").GetInt32();
            processStartedAt = identity.GetProperty("processStartTime").GetDateTimeOffset();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException
            or ArgumentException or FormatException or OverflowException)
        {
            return new(DaemonAvailability.Unavailable, "heartbeat-unavailable", observedAt);
        }

        return InspectProcessIdentity(pid, processStartedAt, observedAt, processStartTimeAccessor);
    }

    private static DaemonObservation InspectProcessIdentity(
        int pid,
        DateTimeOffset processStartedAt,
        DateTimeOffset observedAt,
        Func<int, DateTimeOffset> processStartTimeAccessor)
    {
        if (pid <= 0)
            return new(DaemonAvailability.Unavailable, "process-unavailable", observedAt);

        try
        {
            var actualProcessStartedAt = processStartTimeAccessor(pid);
            var sameProcess = Math.Abs((actualProcessStartedAt - processStartedAt).TotalSeconds) < 2;
            return sameProcess
                ? new(DaemonAvailability.RecentlyObserved, null, observedAt)
                : new(DaemonAvailability.Unavailable, "process-identity-mismatch", observedAt);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new(DaemonAvailability.Unknown, "process-identity-unverifiable", observedAt);
        }
        catch (UnauthorizedAccessException)
        {
            return new(DaemonAvailability.Unknown, "process-identity-unverifiable", observedAt);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return new(DaemonAvailability.Unavailable, "process-unavailable", observedAt);
        }
    }
}
