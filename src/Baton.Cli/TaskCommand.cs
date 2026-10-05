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

        var scopeClass = TaskSubmissionInput.NormalizeScopeClass(options.ScopeClass);
        ValidateCaps(options);
        var stageSelections = BuildStageSelections(options);
        if (stageSelections is not null)
            TaskSubmissionInput.ValidateStageSelections(scopeClass, stageSelections);
        else
            TaskSubmissionInput.ValidateScopeAndReason(
                scopeClass, options.Adapter, options.Model, options.Effort, options.Reason);

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
            identity.Value, issue, options.Size!.Value, options.Spec is null ? null : specBytes,
            stageSelections is null ? selection : null, scopeClass,
            stageSelections is null ? options.Reason : null, stageSelections,
            options.TimeoutMinutes, options.MaxToolSteps, options.TokenBudget);
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
            StageSelections: stageSelections ?? (selection is null ? null : [selection]),
            TimeoutMinutes: options.TimeoutMinutes,
            MaxToolSteps: options.MaxToolSteps,
            TokenBudget: options.TokenBudget);

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
    /// Submissions with no scope, reason, or worker selection preserve the exact historical
    /// newline-header preimage (legacy digest/idempotency fixtures depend on these exact bytes).
    /// Selection-only submissions use the selected-input-v1 domain; submissions with a scope or
    /// reason use the scoped-input-v1 domain. Both newer domains are distinct, typed, and
    /// length-delimited encodings of the complete explicit input — never suffixes onto the ambiguous
    /// legacy header, so a delimiter/newline/spec-marker byte inside a rationale or spec cannot alias
    /// two different submissions onto the same digest.
    /// </summary>
    internal static string ComputeInputDigest(
        string repository, int issue, TaskSizeDeclaration size, byte[]? specBytes, QueueStageSelection? selection,
        string? scopeClass = null, string? reason = null,
        IReadOnlyList<QueueStageSelection>? stageSelections = null,
        int? timeoutMinutes = null, int? maxToolSteps = null, long? tokenBudget = null)
    {
        if (stageSelections is not null || timeoutMinutes is not null || maxToolSteps is not null || tokenBudget is not null)
        {
            return ComputeNewInputDigest(
                repository, issue, size, specBytes, selection, scopeClass, reason, stageSelections,
                timeoutMinutes, maxToolSteps, tokenBudget);
        }

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

    // New scope/reason (--reason) fields use a new domain so selected-input-v1 remains byte-for-byte stable.
    private const string ScopedInputDomain = "baton-task-scoped-input-v1";

    private const string StageAndCapsInputDomain = "baton-task-stage-caps-input-v1";

    private static readonly WorkStage[] DigestStages =
    [
        WorkStage.Implement,
        WorkStage.Review,
        WorkStage.Fix,
        WorkStage.ReReview,
        WorkStage.Continue,
    ];

    private static string ComputeNewInputDigest(
        string repository,
        int issue,
        TaskSizeDeclaration size,
        byte[]? specBytes,
        QueueStageSelection? selection,
        string? scopeClass,
        string? reason,
        IReadOnlyList<QueueStageSelection>? stageSelections,
        int? timeoutMinutes,
        int? maxToolSteps,
        long? tokenBudget)
    {
        var stages = new Dictionary<WorkStage, QueueStageSelection>();
        if (stageSelections is not null)
        {
            foreach (var stage in stageSelections)
            {
                if (!DigestStages.Contains(stage.Stage))
                    throw new ArgumentOutOfRangeException(nameof(stageSelections), stage.Stage, "Unknown dispatch stage.");
                if (!stages.TryAdd(stage.Stage, stage))
                    throw new ArgumentException($"Stage '{WorkStages.Token(stage.Stage)}' was supplied more than once.", nameof(stageSelections));
            }
        }

        if (selection is not null)
        {
            if (stages.TryGetValue(WorkStage.Implement, out var named))
            {
                stages[WorkStage.Implement] = named with
                {
                    Adapter = MergeDigestAxis(named.Adapter, selection.Adapter, "adapter"),
                    Model = MergeDigestAxis(named.Model, selection.Model, "model"),
                    Effort = MergeDigestAxis(named.Effort, selection.Effort, "effort"),
                    Reason = MergeDigestAxis(named.Reason, selection.Reason, "reason"),
                };
            }
            else
            {
                stages.Add(WorkStage.Implement, selection);
            }
        }

        var buffer = new List<byte>();
        AppendField(buffer, Encoding.UTF8.GetBytes(StageAndCapsInputDomain));
        AppendField(buffer, Encoding.UTF8.GetBytes(repository));
        AppendInt32Field(buffer, issue);
        AppendField(buffer, Encoding.UTF8.GetBytes(size.Size.ToString()));
        AppendOptionalField(buffer, size.Rationale is null ? null : Encoding.UTF8.GetBytes(size.Rationale));
        AppendOptionalField(buffer, specBytes);
        AppendOptionalField(buffer, scopeClass is null ? null : Encoding.UTF8.GetBytes(scopeClass));
        AppendOptionalField(buffer, reason is null ? null : Encoding.UTF8.GetBytes(reason));

        foreach (var stage in DigestStages)
        {
            if (!stages.TryGetValue(stage, out var value))
            {
                AppendOptionalField(buffer, null);
                continue;
            }

            var stageBuffer = new List<byte>();
            AppendOptionalField(stageBuffer, value.Adapter is null ? null : Encoding.UTF8.GetBytes(value.Adapter));
            AppendOptionalField(stageBuffer, value.Model is null ? null : Encoding.UTF8.GetBytes(value.Model));
            AppendOptionalField(stageBuffer, value.Effort is null ? null : Encoding.UTF8.GetBytes(value.Effort));
            AppendOptionalField(stageBuffer, value.Reason is null ? null : Encoding.UTF8.GetBytes(value.Reason));
            AppendOptionalField(buffer, stageBuffer.ToArray());
        }

        AppendOptionalInt32Field(buffer, timeoutMinutes);
        AppendOptionalInt32Field(buffer, maxToolSteps);
        AppendOptionalInt64Field(buffer, tokenBudget);
        return Convert.ToHexString(SHA256.HashData(buffer.ToArray())).ToLowerInvariant();
    }

    private static string? MergeDigestAxis(string? first, string? second, string axis)
    {
        if (first is not null && second is not null && !string.Equals(first, second, StringComparison.Ordinal))
            throw new ArgumentException($"Implement-stage {axis} values conflict.");
        return first ?? second;
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

    private static void AppendInt32Field(List<byte> buffer, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        AppendField(buffer, bytes.ToArray());
    }

    private static void AppendOptionalInt32Field(List<byte> buffer, int? value) =>
        AppendOptionalField(buffer, value is { } number ? Int32Bytes(number) : null);

    private static void AppendOptionalInt64Field(List<byte> buffer, long? value) =>
        AppendOptionalField(buffer, value is { } number ? Int64Bytes(number) : null);

    private static byte[] Int32Bytes(int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes.ToArray();
    }

    private static byte[] Int64Bytes(long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        return bytes.ToArray();
    }

    private static void ValidateCaps(TaskOptions options)
    {
        if (options.TimeoutMinutes is <= 0)
            throw new CliArgumentException("'--timeout' must be positive.");
        if (options.MaxToolSteps is <= 0)
            throw new CliArgumentException("'--max-tool-steps' must be positive.");
        if (options.TokenBudget is <= 0)
            throw new CliArgumentException("'--token-budget' must be positive.");
    }

    private static IReadOnlyList<QueueStageSelection>? BuildStageSelections(TaskOptions options)
    {
        var hasNewFields = options.StageSelections is not null
            || options.TimeoutMinutes is not null
            || options.MaxToolSteps is not null
            || options.TokenBudget is not null;
        if (!hasNewFields)
            return null;

        var stages = new Dictionary<WorkStage, QueueStageSelection>();
        foreach (var selection in options.StageSelections ?? [])
        {
            if (!stages.TryAdd(selection.Stage, selection))
                throw new CliArgumentException($"Stage '{WorkStages.Token(selection.Stage)}' was selected more than once.");
        }

        if (options.Adapter is not null || options.Model is not null || options.Effort is not null || options.Reason is not null)
        {
            stages.TryGetValue(WorkStage.Implement, out var existing);
            stages[WorkStage.Implement] = MergeStageSelection(
                existing, options.Adapter, options.Model, options.Effort, options.Reason);
        }

        return stages.Values.OrderBy(selection => selection.Stage).ToList();
    }

    private static QueueStageSelection MergeStageSelection(
        QueueStageSelection? existing, string? adapter, string? model, string? effort, string? reason)
    {
        existing ??= new QueueStageSelection { Stage = WorkStage.Implement };
        return existing with
        {
            Adapter = MergeTaskAxis(existing.Adapter, adapter, "adapter"),
            Model = MergeTaskAxis(existing.Model, model, "model"),
            Effort = MergeTaskAxis(existing.Effort, effort, "effort"),
            Reason = MergeTaskAxis(existing.Reason, reason, "reason"),
        };
    }

    private static string? MergeTaskAxis(string? first, string? second, string axis)
    {
        if (first is not null && second is not null && !string.Equals(first, second, StringComparison.Ordinal))
            throw new CliArgumentException($"Implement-stage {axis} values conflict between bare and named input.");
        return first ?? second;
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
        var receiptMismatches = new List<string>();
        var receiptMissing = new List<string>();
        if (readiness is not null)
        {
            if (string.IsNullOrWhiteSpace(readiness.TaskId)) receiptMissing.Add("taskId");
            else if (!string.Equals(readiness.TaskId, owner.Id, StringComparison.Ordinal)) receiptMismatches.Add("taskId");
            if (string.IsNullOrWhiteSpace(readiness.Repository)) receiptMissing.Add("repository");
            else if (!string.Equals(readiness.Repository, owner.Repository, StringComparison.Ordinal)) receiptMismatches.Add("repository");
            if (readiness.Issue <= 0) receiptMissing.Add("issue");
            else if (readiness.Issue != owner.Issue) receiptMismatches.Add("issue");
            if (item.PullRequest is null || item.PullRequest <= 0) receiptMissing.Add("rowPullRequest");
            if (readiness.PullRequest <= 0) receiptMissing.Add("pullRequest");
            else if (item.PullRequest is { } rowPullRequest && readiness.PullRequest != rowPullRequest)
                receiptMismatches.Add("pullRequest");
            if (string.IsNullOrWhiteSpace(readiness.HeadSha)) receiptMissing.Add("headSha");
        }
        var receiptBinding = readiness is null ? null : new
        {
            status = receiptMismatches.Count > 0 ? "mismatched"
                : receiptMissing.Count > 0 ? "incomplete" : "complete",
            mismatches = receiptMismatches,
            missing = receiptMissing,
        };
        var readinessProjection = readiness is null ? null : new
        {
            id = readiness.Id,
            taskId = readiness.TaskId,
            repository = readiness.Repository,
            issue = readiness.Issue,
            pullRequest = readiness.PullRequest,
            headSha = readiness.HeadSha,
            observedAt = readiness.ReadyObservedAt,
            evidence = "as-of",
            binding = receiptBinding,
        };
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
                && receiptBinding?.status == "complete"
                ? "ready-as-of" : "stale"
            : "queued";
        // Failed rows are not scheduler candidates. Report retained failure evidence without
        // inventing a blocked receipt or promising a daemon retry (#2530).
        if (state == "blocked" && item.State == QueueItemState.Failed
            && item.IssuePreparation?.State is not (TaskPreparationState.Preparing or TaskPreparationState.Blocked))
            reason = string.IsNullOrWhiteSpace(item.Error) ? "task-failed" : item.Error;
        if (state == "stale")
            reason = item.Error ?? (receiptBinding?.status == "mismatched" ? "readiness-receipt-binding-mismatch"
                : receiptBinding?.status == "incomplete" ? "readiness-receipt-binding-incomplete"
                : headChanged ? "observed-pr-head-changed" : "readiness-evidence-no-longer-current");
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
            "ready-as-of" => "conductor-handoff",
            _ => "none",
        };
        var conductorHandoff = state switch
        {
            "ready-as-of" => new
            {
                taskId = owner.Id,
                repository = owner.Repository,
                issue = owner.Issue,
                pullRequest = item.PullRequest,
                holder = owner.ConductorHolder,
                currentHolder = currentClaim?.Holder,
                ownership,
                readiness = readinessProjection,
                responsibility = "reconcile-review-and-fresh-forge-gates-then-merge-under-existing-authority",
                mergeGrant = false,
            },
            "stale" => new
            {
                taskId = owner.Id,
                repository = owner.Repository,
                issue = owner.Issue,
                pullRequest = item.PullRequest,
                holder = owner.ConductorHolder,
                currentHolder = currentClaim?.Holder,
                ownership,
                readiness = readinessProjection,
                responsibility = "reassess-current-readiness",
                mergeGrant = false,
            },
            "blocked" => new
            {
                taskId = owner.Id,
                repository = owner.Repository,
                issue = owner.Issue,
                pullRequest = item.PullRequest,
                holder = owner.ConductorHolder,
                currentHolder = currentClaim?.Holder,
                ownership,
                readiness = readinessProjection,
                responsibility = "judge-retained-blocker",
                mergeGrant = false,
            },
            _ => null,
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
            conductorHandoff,
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
            if (conductorHandoff is { } handoff)
                output.WriteLine($"  conductor handoff: {handoff.holder} ({handoff.ownership}); {handoff.responsibility}; readiness is as-of evidence, not a merge grant");
            if (readiness is not null)
                output.WriteLine($"  ready receipt{(state is "retired" or "cancelled" ? " (historical)" : "")}: "
                    + $"{readiness.Id} at {readiness.ReadyObservedAt:O}; head {readiness.HeadSha} (as-of); binding {receiptBinding!.status}"
                    + (receiptMismatches.Count == 0 ? "" : $"; mismatches: {string.Join(", ", receiptMismatches)}")
                    + (receiptMissing.Count == 0 ? "" : $"; missing: {string.Join(", ", receiptMissing)}"));
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
