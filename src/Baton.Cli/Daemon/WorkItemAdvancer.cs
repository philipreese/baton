using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Baton.Accounting;
using Baton.Artifacts;
using Baton.Domain;
using Baton.Dispatch;
using Baton.Conductor;
using Baton.Queue;
using Baton.Status;
using Baton.Vendors;

namespace Baton.Cli.Daemon;

/// <summary>
/// The I/O half of #1934 slice 2: for every settled work item, read what its room, its verdict and its
/// PR say, ask <see cref="WorkItemLifecycle"/> what that means, and write the next round back onto the
/// queue with one recorded fact naming the evidence.
/// </summary>
/// <remarks>
/// <para>
/// <b>All the policy is in <see cref="WorkItemLifecycle.Decide"/>, which is pure</b> — the same split
/// <see cref="QueueSchedulerService"/> has with <c>QueueScheduler</c>. This class reads files, spawns
/// <c>gh</c> and mutates the queue; it decides nothing.
/// </para>
/// <para>
/// <b>No new process-spawn site.</b> <c>gh</c> goes through <see cref="IGhCliRunner"/>, the seam
/// <see cref="DeliveryPoller"/> already owns, the workspace head through
/// <see cref="WorkspaceHead.CaptureAsync"/>, and repository context through
/// <see cref="RepositoryIdentityResolver"/> — existing <c>git</c> spawns the CLI already has.
/// <c>VendorSpawnGateTests</c>'s population is unchanged by design, not by luck.
/// </para>
/// <para>
/// <b>A <see cref="WorkStage.Ready"/> item is parked in <see cref="QueueItemState.Queued"/>, not
/// marked done</b> — spec/baton.md §13 has the ruling and what it buys. The consequence for this file:
/// nothing here stops such an item launching, because <c>QueueScheduler.Decide</c> does, and a second
/// guard here would quietly become the one that mattered.
/// </para>
/// </remarks>
public sealed partial class WorkItemAdvancer
{
    private const int MaxRequiredCheckEvidenceAttempts = 6;
    private static readonly TimeSpan RequiredCheckEvidenceBackoff = TimeSpan.FromSeconds(30);

    private const string PullRequestJsonFields =
        "number,state,isDraft,headRefOid,statusCheckRollup,headRefName,baseRefName,isCrossRepository,mergeCommit";
    private const string BoardObservationJsonFields = "number,state,headRefOid";

    private readonly IGhCliRunner _gh;
    private readonly IGhCliRunner _draftGh;
    private readonly WorkspaceDeliveryProbe.CommandRunner _git;
    private readonly bool _gitInjected;
    private readonly TimeSpan _draftCommandTimeout;
    private readonly Func<string, CancellationToken, Task<string?>> _workspaceHead;
    private readonly Func<string, CancellationToken, Task<RepositoryIdentity?>>? _repositoryIdentity;
    private readonly TimeSpan _boardObservationTimeout;
    private readonly Func<FleetEventDraft, CancellationToken, Task<FleetEvent?>> _appendFleetEvent;
    private readonly QueueFleetEventOutbox _fleetOutbox;
    private readonly ConductorObligationStore _conductorObligations;
    private readonly Func<DateTimeOffset> _requiredCheckClock;
    internal Func<QueueItem, CancellationToken, Task>? OwnedHaltCommitted { get; set; }

    public WorkItemAdvancer()
        : this(
            null,
            null,
            RepositoryIdentityResolver.TryResolveAsync,
            appendFleetEvent: AppendOperationalFleetEventAsync)
    {
    }

    /// <summary>Test seam (Baton.Cli.Tests): all three probes are delegates, so every transition runs
    /// against a fixture room with no <c>gh</c>, no <c>git</c> and no network.</summary>
    internal WorkItemAdvancer(
        IGhCliRunner? gh,
        Func<string, CancellationToken, Task<string?>>? workspaceHead,
        Func<string, CancellationToken, Task<RepositoryIdentity?>>? repositoryIdentity = null,
        TimeSpan? boardObservationTimeout = null,
        Func<FleetEventDraft, CancellationToken, Task<FleetEvent?>>? appendFleetEvent = null,
        ConductorObligationStore? conductorObligations = null,
        WorkspaceDeliveryProbe.CommandRunner? git = null,
        TimeSpan? draftCommandTimeout = null,
        Func<DateTimeOffset>? requiredCheckClock = null)
    {
        _gh = gh ?? new GhCliRunner();
        _draftGh = gh ?? new GhCliRunner(requireOutsideWorkspace: true);
        _git = git ?? WorkspaceDeliveryProbe.SpawnAsync;
        _gitInjected = git is not null;
        _draftCommandTimeout = draftCommandTimeout ?? DefaultDraftPullRequestCommandTimeout;
        _requiredCheckClock = requiredCheckClock ?? (() => DateTimeOffset.UtcNow);
        _workspaceHead = workspaceHead ?? ReadWorkspaceHeadAsync;
        _repositoryIdentity = repositoryIdentity;
        _boardObservationTimeout = boardObservationTimeout ?? WorkspaceDeliveryProbe.SpawnTimeout;
        _appendFleetEvent = appendFleetEvent ?? ((_, _) => Task.FromResult<FleetEvent?>(null));
        _fleetOutbox = new QueueFleetEventOutbox(_appendFleetEvent);
        _conductorObligations = conductorObligations
            ?? new ConductorObligationStore(FleetEventLog.OpenOperational());
    }

    /// <summary>
    /// Advances every work item whose lane has settled. Returns the facts to record, in the order they
    /// happened — the caller appends them, because the ledger's collapse key is the scheduler's to
    /// carry across evaluations (<c>QueueDecisionLedgerStore.AppendAsync</c> says why).
    /// </summary>
    public async Task<IReadOnlyList<QueueDecisionEntry>> AdvanceAsync(
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        await _fleetOutbox.PumpAsync(BatonPaths.QueueFile, cancellationToken).ConfigureAwait(false);
        var snapshot = await QueueStore.LoadAsync(BatonPaths.QueueFile, cancellationToken).ConfigureAwait(false);

        // A disposition CAS is the linearization point; ledger availability must never manufacture
        // a fact before it. Replay only operations retained by committed queue rows.
        foreach (var committed in snapshot.Items.Where(item => item.DispositionOutbox.Count > 0))
        {
            foreach (var operation in committed.DispositionOutbox)
            {
                await QueueDecisionLedgerStore.AppendDispositionAsync(
                    committed.Tag, operation, BatonPaths.QueueDecisionLedgerFile, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        // Settled, staged, and not one this advance has already given up on. Ready items are included
        // even though they have no room: their persisted verdict must be reconciled against a later
        // GitHub head/check change. State
        // rather than the sentinel: QueueSchedulerService's own done detection has already read the room
        // this tick and is the one thing that moves an item out of `launched`, so re-deriving settledness
        // here would be a second reader of the same file that can disagree with the first. The
        // `Halted` half is what stops a NeedsOperator item being re-observed on every tick forever —
        // see QueueItem.Halted for what that cost.
        var candidates = snapshot.Items
            .Where(i => i.Stage is { } stage && i.Retirement is null
                && CanAdvanceCurrentAttempt(i, snapshot)
                && (i.RequiredCheckEvidenceWait is not { } waiting
                    || now - waiting.LatestObservationAt >= RequiredCheckEvidenceBackoff)
                && (stage == WorkStage.Ready
                    ? i.State == QueueItemState.Queued && !i.Halted
                        || i.State == QueueItemState.Failed && !snapshot.Held && IsAwaitingCheckEvidenceRecovery(i)
                    : i.State is QueueItemState.Done or QueueItemState.Failed
                        && (i.RoomDirectory is { Length: > 0 }
                            // A refusal is recorded before a launch can create a room, so unlike a
                            // roomless failure it is durable proof there is no late live room.
                            || IsAdmissionRefusedRoomlessFailure(i))
                        && (!i.Halted || i.Branch is { Length: > 0 }
                            && (IsAwaitingMissingPullRequestReconciliation(i)
                                || !snapshot.Held && IsAwaitingCheckEvidenceRecovery(i)))))
            .ToList();
        if (candidates.Count == 0)
        {
            return [];
        }

        var facts = new List<QueueDecisionEntry>();
        foreach (var item in candidates)
        {
            var fact = await AdvanceOneAsync(item, now, cancellationToken).ConfigureAwait(false);
            if (fact is not null)
            {
                facts.Add(fact);
            }
        }

        return facts;
    }

    private static bool CanAdvanceCurrentAttempt(QueueItem item, QueueSnapshot snapshot)
    {
        if (item.AttemptEnvelope is not { } envelope)
        {
            return true;
        }

        if (QueueFleetEventOutbox.HasPendingFor(snapshot, envelope.AttemptId)
            || !item.AttemptAdmissionFactDurable)
        {
            return false;
        }

        // A room-bearing terminal result must wait for attemptSettled. A roomless admission refusal
        // has no settle fact by design; its durable admission decision is the refusal proof.
        return item.RoomDirectory is null
            && item.LastAdmission?.Result == TaskRequirementAdmission.Refused
            || item.AttemptSettledFactDurable;
    }

    private static bool IsAdmissionRefusedRoomlessFailure(QueueItem item) =>
        item is
        {
            State: QueueItemState.Failed,
            RoomDirectory: null,
            LastAdmission.Result: TaskRequirementAdmission.Refused,
        };

    internal async Task<string> PrepareQueuedReviewBriefAsync(QueueItem item, CancellationToken cancellationToken)
    {
        if (item.Stage is not (WorkStage.Review or WorkStage.ReReview)
            || item.PullRequest is not > 0 || item.Repository is not { Length: > 0 }
            || item.Branch is not { Length: > 0 })
            throw new CliArgumentException("Queued lifecycle review lacks an exact repository, branch or PR; no worker was started.");

        // Protected invariant: a delayed queued review must name the PR/workspace revision observed
        // for this launch, not the revision used when its previous stage rendered the saved brief.
        // Required checks remain delivery gates, not permission to inspect the current source.
        var pr = await ReadPullRequestAsync(item, cancellationToken).ConfigureAwait(false);
        var head = await _workspaceHead(item.Workspace, cancellationToken).ConfigureAwait(false);
        if (!pr.Succeeded || pr.IsOpen != true || pr.Number != item.PullRequest
            || pr.HeadSha is not { Length: 40 } sha || !sha.All(char.IsAsciiHexDigit)
            || !string.Equals(head, sha, StringComparison.OrdinalIgnoreCase))
            throw new CliArgumentException("Queued lifecycle review could not verify an exact open PR and matching workspace revision; no worker was started.");

        var prior = ReadLastVerdict(item);
        return QueueBriefTemplates.Compose(item.Stage.Value, item, new QueueBriefTemplates.BriefContext(
            Title: $"Implement #{item.Issue}", Do: item.Instructions ?? string.Empty,
            PullRequest: item.PullRequest, HeadSha: sha, Round: item.Round,
            Findings: prior is null ? null : QueueBriefTemplates.RenderFindings(prior)));
    }

    // Only the typed, persisted recovery granted by the lifecycle may re-enter a halted row.
    // Error text is for the operator and can change without changing scheduler state.
    private static bool IsAwaitingMissingPullRequestReconciliation(QueueItem item) =>
        item.ReconciliationKind == QueueReconciliationKind.AwaitingVerifiedPullRequest;

    private static bool IsAwaitingCheckEvidenceRecovery(QueueItem item) =>
        item.Halted && item.ReconciliationKind == QueueReconciliationKind.AwaitingRequiredCheckEvidence
        && item.PullRequest is > 0 && item.RequiredCheckEvidenceWait is { HeadSha.Length: 40 };

    private async Task<QueueDecisionEntry?> AdvanceOneAsync(
        QueueItem item, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var stage = item.Stage!.Value;
        if (item.Repository is not { Length: > 0 }
            && (stage != WorkStage.Implement || IsAwaitingMissingPullRequestReconciliation(item)))
        {
            return await HaltLegacyRepositoryAsync(item, stage, now).ConfigureAwait(false);
        }

        var room = item.RoomDirectory;
        var sentinel = room is null
            ? null
            : await TerminalSentinelWriter.TryReadAsync(room, cancellationToken).ConfigureAwait(false);

        // A room with no sentinel that the scheduler has nonetheless resolved is one it failed for
        // never having been created (its own roomless sweep). "Failed with no outcome word" is exactly
        // what the lifecycle's not-succeeded arm reads, so the item still advances rather than sticking.
        var outcome = item.Stage == WorkStage.Ready
            ? WorkflowOutcome.Succeeded
            : sentinel?.State ?? WorkflowOutcome.Failed;
        var verdictPath = item.Stage == WorkStage.Ready ? item.LastVerdict : FindVerdict(sentinel);
        var verdict = verdictPath is null ? null : TryReadVerdict(verdictPath);
        var arrestedStep = sentinel?.Steps?.FirstOrDefault(step =>
            string.Equals(step.IndeterminateProducerKind, nameof(Baton.Domain.IndeterminateProducer.Arrested), StringComparison.Ordinal));

        // A permanent may-have-called marker takes precedence over the older unpinned open-PR
        // reconciliation. A moved head or non-draft PR must never reach lifecycle advancement.
        var markedHandoff = IsAwaitingMissingPullRequestReconciliation(item)
            && item.DraftPullRequestCreateMarker is not null;
        var trustedDraftHandoff = markedHandoff || IsAwaitingMissingPullRequestReconciliation(item)
            && item.Stage == WorkStage.Implement && IsDraftHandoffEnabledNow(item.Repository);
        var pr = markedHandoff
            ? await RecoverDraftPullRequestAsync(item, cancellationToken).ConfigureAwait(false)
            : await ReadPullRequestAsync(item, cancellationToken, trustedDraftHandoff).ConfigureAwait(false);
        if (pr is null)
        {
            return null;
        }
        var head = trustedDraftHandoff
            ? await ReadTrustedDraftHeadAsync(item, cancellationToken).ConfigureAwait(false)
            : await _workspaceHead(item.Workspace, cancellationToken).ConfigureAwait(false);

        var replacementProofItem = await TryPersistReplacementReviewProofAsync(item, sentinel, verdictPath, cancellationToken)
            .ConfigureAwait(false);
        if (replacementProofItem is null) return null;
        item = replacementProofItem;

        if (IsAwaitingCheckEvidenceRecovery(item))
        {
            // Green is permission to release this one typed halt, never to bypass lifecycle or
            // launch admission. Keep the room/stage intact; the next tick re-reads all evidence.
            // A different PR/head, or any non-green/unknown result, cannot inherit this permission.
            var wait = item.RequiredCheckEvidenceWait!;
            var recovered = pr is { Succeeded: true, IsOpen: true, RequiredChecks: PullRequestChecks.Passing }
                && pr.Number == item.PullRequest && pr.HeadSha == wait.HeadSha && head == wait.HeadSha;
            var operation = new QueueDispositionOperation(Guid.NewGuid().ToString("N"), now,
                QueueDecisionEntry.Restored,
                $"required-check evidence recovered for PR #{item.PullRequest} at {wait.HeadSha}; normal lifecycle admission resumes");
            var changed = await TryMarkAsync(item, existing => recovered
                ? existing with
                {
                    Halted = false,
                    State = existing.Stage == WorkStage.Ready ? QueueItemState.Queued : existing.State,
                    ReconciliationKind = null,
                    RequiredCheckEvidenceWait = null,
                    Error = null,
                    Checks = pr.Checks,
                    ChecksObservedAt = now,
                    ChecksHeadSha = pr.HeadSha,
                    DispositionOperations = [.. existing.DispositionOutbox, operation],
                }
                : existing with
                {
                    RequiredCheckEvidenceWait = wait with { LatestObservationAt = now },
                }).ConfigureAwait(false);
            if (changed && recovered)
                await QueueDecisionLedgerStore.AppendDispositionAsync(item.Tag, operation,
                    BatonPaths.QueueDecisionLedgerFile, cancellationToken).ConfigureAwait(false);
            return null;
        }
        var deliveryFailingMembers = ReadDeliveryFailingMembers(sentinel);
        await RecordOwnedObservationsAsync(item, stage, verdict, pr, head, now, cancellationToken)
            .ConfigureAwait(false);

        // A merged PR is delivery evidence, but it is not permission to abandon a room that is
        // still running. In particular, the roomless-timeout sweep can mark a late launch Failed
        // before that launch creates its room. Only the terminal sentinel is proof this room is no
        // longer live; the state word is merely the queue's earlier observation.
        var normalDeliveredReady = item is
        {
            Stage: WorkStage.Ready,
            State: QueueItemState.Queued,
            RoomDirectory: null,
            ReadinessMutationClaim: null,
        };
        var terminalRoomDelivery = sentinel is not null
            && item.State is QueueItemState.Done or QueueItemState.Failed
            && item.ReadinessMutationClaim is null;
        var admissionRefusedRoomlessDelivery = IsAdmissionRefusedRoomlessFailure(item)
            && item.ReadinessMutationClaim is null;
        if (pr.Succeeded && pr.MergeSha is { Length: > 0 }
            && (normalDeliveredReady || terminalRoomDelivery || admissionRefusedRoomlessDelivery))
        {
            var retired = false;
            var retirement = new QueueRetirement(QueueRetirement.Merged, now,
                $"trusted merged observation for PR #{pr.Number}");
            var operation = new QueueDispositionOperation(
                Guid.NewGuid().ToString("N"), now, QueueDecisionEntry.Retired, $"merged: PR #{pr.Number}");
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot =>
            {
                var current = snapshot.Items.FirstOrDefault(i => i.Tag == item.Tag);
                if (current is null || current.Retirement is not null
                    || current.RoomDirectory != item.RoomDirectory
                    || current.ReadinessMutationClaim is not null)
                {
                    return snapshot;
                }
                var currentNormalDeliveredReady = current is
                {
                    Stage: WorkStage.Ready, State: QueueItemState.Queued, RoomDirectory: null,
                };
                // This is only the sentinel-backed, room-bearing path observed above. A roomless
                // terminal row must re-prove its refused admission at the mutation point instead.
                var currentTerminalRoomDelivery = terminalRoomDelivery
                    && current.RoomDirectory is { Length: > 0 } currentRoom
                    && HasReadableTerminalSentinelAtMutation(currentRoom)
                    && current.State is QueueItemState.Done or QueueItemState.Failed;
                var currentAdmissionRefusedRoomlessDelivery = IsAdmissionRefusedRoomlessFailure(current);
                if (!currentNormalDeliveredReady && !currentTerminalRoomDelivery
                    && !currentAdmissionRefusedRoomlessDelivery)
                {
                    return snapshot;
                }
                retired = true;
                return snapshot with
                {
                    Items = snapshot.Items.Select(i => i.Tag == item.Tag
                    ? i with
                    {
                        Retirement = retirement,
                        ExpectedOriginatingPullRequestHead = null,
                        OriginatingPullRequestRecoveryClaim =
                            i.OriginatingPullRequestRecoveryClaim == i.AttemptId
                                ? null
                                : i.OriginatingPullRequestRecoveryClaim,
                        OriginatingPullRequestRecoveryProofDigest = null,
                        DispositionOperations = [.. i.DispositionOutbox, operation],
                    } : i).ToList()
                };
            }, cancellationToken).ConfigureAwait(false);
            if (retired)
            {
                await QueueDecisionLedgerStore.AppendDispositionAsync(
                    item.Tag, operation, BatonPaths.QueueDecisionLedgerFile, cancellationToken).ConfigureAwait(false);
                return null;
            }
        }

        // A halted item remains available only for the trusted-merge retirement above. Its halt
        // still forbids every ordinary lifecycle transition and retry.
        var awaitingMissingPullRequest = IsAwaitingMissingPullRequestReconciliation(item);
        var pullRequestAuthorityRefusal = HasPullRequestAuthorityRefusal(room, sentinel);
        if (item.Halted && !awaitingMissingPullRequest)
        {
            return null;
        }

        // Preserve the original delivery failure and terminal room while the operator has not yet
        // supplied the exact forge object. Re-observation is the supported recovery seam; it does
        // not infer a PR from a branch or manufacture another failure fact each scheduler tick.
        if (awaitingMissingPullRequest && item.Stage == WorkStage.Implement
            && IsDraftHandoffEnabledNow(item.Repository)
            && pr is { Succeeded: true, Number: not null, IsOpen: true, IsDraft: true }
            && !string.Equals(pr.HeadSha, head, StringComparison.OrdinalIgnoreCase))
        {
            await RetainDraftHandoffReasonAsync(item,
                "preexisting draft PR is not at the current implementation HEAD; operator reconciliation required")
                .ConfigureAwait(false);
            return null;
        }
        if (awaitingMissingPullRequest
            && item.DraftPullRequestCreateMarker is null
            && pr.Succeeded && pr.Number is null
            && item.Stage == WorkStage.Implement
            && IsDraftHandoffEnabledNow(item.Repository)
            && !pullRequestAuthorityRefusal)
        {
            var created = await TryCreateDraftPullRequestAsync(item, outcome, head, now, cancellationToken)
                .ConfigureAwait(false);
            if (created is null) return null;
            item = created.Value.Item;
            pr = created.Value.Observation;
        }
        if (awaitingMissingPullRequest
            && (!pr.Succeeded || pr.Number is null || pr.IsOpen != true || pr.IsDraft != true))
        {
            return null;
        }

        WorkItemObservation Observation(PullRequestObservation reading) => new(
            stage, item.Round, item.AutomaticFixUsed, item.Branch, outcome, verdict,
            reading.Number, reading.HeadSha, head, reading.Succeeded, reading.IsOpen,
            reading.IsDraft, reading.RequiredChecks, arrestedStep?.WorkspaceChanged,
            arrestedStep is null ? null : Baton.Domain.IndeterminateProducer.Arrested,
            sentinel?.Steps is { } terminalSteps ? terminalSteps.Count > 0 : null,
            item.AttemptBaseRevision, deliveryFailingMembers,
            pullRequestAuthorityRefusal);

        var transition = WorkItemLifecycle.Decide(Observation(pr));
        var readinessClaimed = false;
        var restoredDraft = false;

        // A crash can leave a durable claim after GitHub reached the requested state but before the
        // queue observation committed. Re-own it with the same complete-row CAS used below before
        // clearing or advancing the row, so two recovery ticks cannot both act on the orphan.
        if (item.ReadinessMutationClaim is not null
            && transition.PullRequestAction == PullRequestReadinessAction.None)
        {
            var recovered = await TryClaimReadinessAsync(item).ConfigureAwait(false);
            if (recovered is null)
            {
                return null;
            }

            item = recovered;
            readinessClaimed = true;
        }

        // An empty required set for this exact open head is neither green nor a failed worker.
        // Exceptions:
        // - A requested draft restoration has precedence over waiting for check evidence: an open
        //   non-draft PR with incomplete review evidence or a blocking verdict must be restored to
        //   draft immediately, not left visibly ready while waiting for empty checks (#2457).
        // - A verified open draft resolves the one halted no-PR delivery identity. Review
        //   may proceed without required-check evidence; trapping this recovery in the readiness wait
        //   would overwrite its original halt and eventually emit a second failure fact.
        async Task<(bool Waiting, QueueDecisionEntry? Entry)> RetainRequiredCheckWaitAsync(bool afterDraftRestoration = false)
        {
            // A missing verdict or selected repair/re-review remains actionable on an already-draft
            // PR too. Check waiting must not replace that diagnosis with an unrelated evidence halt.
            if (transition.Kind == WorkItemTransitionKind.NeedsOperator
                || transition.Kind == WorkItemTransitionKind.Dispatch
                    && (afterDraftRestoration || transition.NextStage != WorkStage.Review))
                return (false, null);
            if (!awaitingMissingPullRequest
                && transition.PullRequestAction != PullRequestReadinessAction.MarkDraft
                && pr is { Succeeded: true, Number: { } number, HeadSha: { Length: > 0 } headSha, IsOpen: true }
                && (pr.NoObservedRequiredEvidence || pr.RequiredChecks == PullRequestChecks.None
                    || pr.RequiredChecks is null && (transition.NextStage is not (WorkStage.Review or WorkStage.ReReview)
                        || item.RequiredCheckEvidenceWait?.HeadSha == headSha)))
            {
                var prior = item.RequiredCheckEvidenceWait;
                var changedHead = !string.Equals(prior?.HeadSha, headSha, StringComparison.Ordinal);
                var attempts = changedHead ? 1 : prior!.AttemptCount + 1;
                var reason = $"waiting for required-check evidence for open PR #{number} at {headSha} (attempt {attempts}/{MaxRequiredCheckEvidenceAttempts})"
                    + (pr.Error is { Length: > 0 } checkError ? $"; {checkError}" : string.Empty);
                var wait = new RequiredCheckEvidenceWait(
                    headSha, changedHead ? now : prior!.FirstUnreadableAt, now, attempts, reason);
                if (attempts >= MaxRequiredCheckEvidenceAttempts)
                {
                    var exhausted = new WorkItemTransition(WorkItemTransitionKind.NeedsOperator, null, 0,
                        $"{reason}; observation bound exhausted after first unreadable observation at "
                        + $"{(changedHead ? now : prior!.FirstUnreadableAt):O}; the settled room remains attached and no worker was dispatched",
                        ReconciliationKind: QueueReconciliationKind.AwaitingRequiredCheckEvidence,
                        HaltCause: StoppedWorkHaltCause.UnavailableCheckEvidence);
                    return (true, await FailAsync(item, stage, exhausted, verdictPath, now, room, wait,
                        pr, sentinel, verdict is not null, outcome, cancellationToken).ConfigureAwait(false));
                }

                await TryMarkAsync(item, existing => existing with
                {
                    PullRequest = number,
                    Checks = changedHead ? null : existing.Checks,
                    ChecksObservedAt = changedHead ? null : existing.ChecksObservedAt,
                    ChecksHeadSha = changedHead ? null : existing.ChecksHeadSha,
                    RequiredCheckEvidenceWait = wait,
                    Error = reason,
                    ReadinessMutationClaim = null,
                }).ConfigureAwait(false);
                return (true, null);
            }
            return (false, null);
        }

        var initialWait = await RetainRequiredCheckWaitAsync().ConfigureAwait(false);
        if (initialWait.Waiting) return initialWait.Entry;

        // A readiness mutation is never trusted from the command receipt. Re-observe the PR after
        // every attempt, then ask the pure lifecycle again. The bounded loop covers the one real
        // race: a head changes while a mark-ready is in flight, so the post-read requests mark-draft.
        // A third requested action means GitHub never converged; retain the obligation for next tick.
        for (var attempt = 0; transition.PullRequestAction != PullRequestReadinessAction.None && attempt < 3; attempt++)
        {
            if (transition.PullRequestAction == PullRequestReadinessAction.MarkReady)
            {
                // Protected invariant: admission cannot borrow a prior policy read. Reconcile fresh
                // complete policy and current-head evidence immediately before taking the claim.
                pr = await ReadPullRequestAsync(item with { PullRequest = pr.Number }, cancellationToken)
                    .ConfigureAwait(false);
                transition = WorkItemLifecycle.Decide(Observation(pr));
                var freshWait = await RetainRequiredCheckWaitAsync().ConfigureAwait(false);
                if (freshWait.Waiting) return freshWait.Entry;
                if (transition.PullRequestAction == PullRequestReadinessAction.None) break;
            }
            if (pr.Number is not { } pullRequest)
            {
                return await RetainReconciliationAsync(
                    item, transition.Reason + " (no exact PR number was available for the mutation)",
                    pr, now, room, recordFailure: true).ConfigureAwait(false);
            }

            if (!readinessClaimed)
            {
                // This durable compare-and-swap is the readiness operation's local linearization
                // point. Cancellation or an allowed same-tag replacement that committed first makes
                // the claim lose and therefore prevents the external mutation. Once the claim wins,
                // those commands refuse until this bounded reconciliation commits or releases it.
                var claimed = await TryClaimReadinessAsync(item).ConfigureAwait(false);
                if (claimed is null)
                {
                    return null;
                }

                item = claimed;
                readinessClaimed = true;
            }

            var args = transition.PullRequestAction == PullRequestReadinessAction.MarkDraft
                ? RepositoryArgs(item, "pr", "ready", pullRequest.ToString(CultureInfo.InvariantCulture), "--undo")
                : RepositoryArgs(item, "pr", "ready", pullRequest.ToString(CultureInfo.InvariantCulture));
            var mutation = await _gh.RunAsync(item.Workspace, args, cancellationToken).ConfigureAwait(false);

            var after = await ReadPullRequestAsync(
                item with { PullRequest = pullRequest }, cancellationToken).ConfigureAwait(false);
            var desiredDraft = transition.PullRequestAction == PullRequestReadinessAction.MarkDraft;
            var reachedDesiredState = after.Succeeded
                && after.Number == pullRequest
                && after.IsOpen == true
                && after.IsDraft == desiredDraft;
            if (!reachedDesiredState)
            {
                var receipt = !mutation.Started
                    ? "gh did not start"
                    : $"gh exited {mutation.ExitCode}";
                var observationError = after.Error is { Length: > 0 } error ? $"; {error}" : string.Empty;
                return await RetainReconciliationAsync(
                    item,
                    $"{transition.PullRequestAction} for PR #{pullRequest} was not confirmed ({receipt}{observationError}); "
                    + "the readiness obligation remains",
                    after,
                    now,
                    room,
                    recordFailure: true).ConfigureAwait(false);
            }

            pr = after;
            restoredDraft |= desiredDraft;
            transition = WorkItemLifecycle.Decide(Observation(pr));
        }

        if (transition.PullRequestAction != PullRequestReadinessAction.None)
        {
            return await RetainReconciliationAsync(
                item, "GitHub readiness did not converge after three confirmed observations; the obligation remains",
                pr, now, room, recordFailure: true).ConfigureAwait(false);
        }

        var finalWait = await RetainRequiredCheckWaitAsync(restoredDraft).ConfigureAwait(false);
        if (finalWait.Waiting) return finalWait.Entry;

        // A current, green, already-ready PR and a closed/merged PR are stable terminal observations.
        // Do not rewrite queue.json or emit another transition fact on every daemon tick.
        if (stage == WorkStage.Ready
            && transition.Kind == WorkItemTransitionKind.None
            && pr.Succeeded
            && (pr.IsOpen == false
                || pr.IsOpen == true && pr.IsDraft == false
                    && pr.RequiredChecks == PullRequestChecks.Passing))
        {
            // `readinessClaimed` also covers restart recovery where the previous daemon completed
            // the GitHub mutation but stopped before clearing its durable claim. The live observation
            // is already stable, so the replacement claim itself is the remaining state to commit.
            if (readinessClaimed || item.Error is not null)
            {
                await TryMarkAsync(item, existing => existing with
                {
                    PullRequest = pr.Number ?? existing.PullRequest,
                    Checks = pr.Checks ?? existing.Checks,
                    ChecksObservedAt = pr.Checks is null ? existing.ChecksObservedAt : now,
                    ChecksHeadSha = pr.Checks is null ? existing.ChecksHeadSha : pr.HeadSha,
                    RequiredCheckEvidenceWait = null,
                    Error = null,
                    ReadinessMutationClaim = null,
                }).ConfigureAwait(false);
            }

            return null;
        }

        return transition.Kind switch
        {
            WorkItemTransitionKind.None =>
                await RetainReconciliationAsync(
                    item,
                    pr.Error is { Length: > 0 } observationError
                        ? $"{transition.Reason}: {observationError}"
                        : transition.Reason,
                    pr, now, room,
                    recordFailure: !pr.Succeeded).ConfigureAwait(false),
            WorkItemTransitionKind.NeedsOperator =>
                await FailAsync(item, stage, transition, verdictPath, now, room,
                    null, pr, sentinel, verdict is not null, outcome, cancellationToken).ConfigureAwait(false),
            WorkItemTransitionKind.Stop =>
                await StopAsync(item, stage, transition, pr, verdictPath, now, room).ConfigureAwait(false),
            WorkItemTransitionKind.Dispatch =>
                await QueueNextRoundAsync(
                        item, stage, transition, pr, verdict, verdictPath, now, room, sentinel, cancellationToken)
                    .ConfigureAwait(false),
            _ => null,
        };
    }

    /// <summary>
    /// Keeps a settled lane eligible for the next reconciliation tick while recording the current
    /// PR/check observation and an actionable reason on the item. A failed GitHub attempt also emits
    /// a ledger fact; an ordinary pending-check wait does not pretend to be a failure.
    /// </summary>
    private static async Task<QueueDecisionEntry?> RetainReconciliationAsync(
        QueueItem item, string reason, PullRequestObservation pr, DateTimeOffset now, string? room,
        bool recordFailure)
    {
        var retained = await TryMarkAsync(item, existing => existing with
        {
            // A failed required-check read can still carry the PR snapshot that preceded it. If that
            // snapshot names a new head, no evidence or bounded-wait history from the old head may
            // survive under the new identity. The next readable observation starts its own wait.
            PullRequest = pr.Number ?? existing.PullRequest,
            Checks = InvalidatesPriorCheckEvidence(pr, existing) ? null : pr.Checks ?? existing.Checks,
            ChecksObservedAt = InvalidatesPriorCheckEvidence(pr, existing)
                ? null
                : pr.Checks is null ? existing.ChecksObservedAt : now,
            ChecksHeadSha = InvalidatesPriorCheckEvidence(pr, existing)
                ? null
                : pr.Checks is null ? existing.ChecksHeadSha : pr.HeadSha,
            RequiredCheckEvidenceWait = RequiredCheckEvidenceWaitAfter(pr, existing.RequiredCheckEvidenceWait),
            Error = reason,
            ReadinessMutationClaim = null,
        }).ConfigureAwait(false);

        return retained && recordFailure
            ? new QueueDecisionEntry(
                now, item.Tag, QueueDecisionEntry.Failed, reason,
                LiveWeight: 0, FreeGb: null, FloorGb: 0, Room: room)
            : null;
    }

    /// <summary>
    /// The next round: the brief is rendered FIRST, then the item is written. A render that throws must
    /// not leave an item queued against the previous round's brief, which is the failure mode of writing
    /// the state first.
    /// </summary>
    private async Task<QueueDecisionEntry?> QueueNextRoundAsync(
        QueueItem item,
        WorkStage from,
        WorkItemTransition transition,
        PullRequestObservation pr,
        ReviewVerdict? verdict,
        string? verdictPath,
        DateTimeOffset now,
        string? room,
        WorkflowStatusView? sentinel,
        CancellationToken cancellationToken)
    {
        var next = transition.NextStage!.Value;
        var destinationRole = WorkStages.RoleFor(next);
        var destinationSelection = QueueTierTable.SelectionForStage(item, next).Selection;
        var destinationRequirements = TaskRequirementPreflight.RequirementsFor(
            WorkerRoleCatalog.For(destinationRole), destinationSelection?.Requirements);

        if (next == WorkStage.Continue)
        {
            if (string.IsNullOrWhiteSpace(room) || sentinel is null)
            {
                return null;
            }

            var createdAt = item.LaunchedAt ?? item.AddedAt;
            if (createdAt is not { } stableCreatedAt)
            {
                return null;
            }

            var request = ConductorContinuation.TryRequest(
                item,
                room,
                sentinel,
                pr.HeadSha,
                transition,
                stableCreatedAt);
            if (request is null)
            {
                return null;
            }

            var obligation = await _conductorObligations.EnqueueAsync(request, cancellationToken)
                .ConfigureAwait(false);
            if (obligation.Status != ConductorObligationStatus.Pending)
            {
                return null;
            }
        }

        // The findings travel as TEXT, never the verdict's path -- QueueBriefTemplates' own remarks
        // have the mechanism and spec/baton.md §13 the ruling.
        //
        // The LAST REVIEW's verdict, not the room that just settled: a fix lane produces none, so a
        // re-review dispatched straight after one rendered "(no findings were recorded)" and asked the
        // reviewer to say whether a new head closed findings it could not see (#2004 review round 1).
        // `item.LastVerdict` is the path the previous review round already recorded here, read back
        // through the same single reader below.
        var findingsVerdict = verdict ?? ReadLastVerdict(item);
        var findings = findingsVerdict is null ? null : QueueBriefTemplates.RenderFindings(findingsVerdict);

        // The issue's own instructions, which a continuation still needs — read off the ITEM, because
        // the brief file this would otherwise be parsed out of is rewritten every round
        // (QueueItem.Instructions' remarks).
        var brief = QueueBriefTemplates.Compose(next, item, new QueueBriefTemplates.BriefContext(
            Title: $"Implement #{item.Issue}",
            Do: item.Instructions ?? string.Empty,
            PullRequest: pr.Number ?? item.PullRequest,
            HeadSha: pr.HeadSha,
            Round: transition.Round,
            Findings: findings));

        var advanced = await TryMarkAsync(item, existing => existing with
        {
            Stage = next,
            Role = destinationRole,
            Requirements = destinationRequirements,
            // Admission describes the attempt that just settled. It must not remain visible while
            // the next stage is queued, or status would project the source role's grant.
            LastAdmission = null,
            // A frozen assignment belongs to the attempt that just settled. The next lifecycle
            // stage has its own role and stage plan; carrying the old tuple forward would silently
            // run review/fix/re-review on the implementation worker.
            WorkerAssignment = null,
            MemoryAddGrant = null,
            Round = transition.Round,
            PullRequest = pr.Number ?? existing.PullRequest,
            // Coalesced, never assigned: a `gh` that did not run (missing, unauthenticated, no PR on
            // the branch) reports null, and overwriting a real observation with that would turn "no
            // answer this tick" into "no checks", which is a different claim. The stamp moves only when
            // the word does, so QueueItem.Checks' own "never render one without the other" rule cannot
            // be satisfied by an age that outlives its reading.
            Checks = pr.Checks ?? existing.Checks,
            ChecksObservedAt = pr.Checks is null ? existing.ChecksObservedAt : now,
            ChecksHeadSha = pr.Checks is null ? existing.ChecksHeadSha : pr.HeadSha,
            RequiredCheckEvidenceWait = RequiredCheckEvidenceWaitAfter(pr, existing.RequiredCheckEvidenceWait),
            LastVerdict = verdictPath ?? existing.LastVerdict,
            // The lifecycle is the one authority that says a BLOCK may spend this budget. Preserve
            // false, true, and legacy-null through every other transition so retries and
            // continuations cannot manufacture a fix history from their shared round count.
            AutomaticFixUsed = transition.UsesAutomaticFix ? true : existing.AutomaticFixUsed,
            State = QueueItemState.Queued,
            RoomDirectory = null,
            LaunchedAt = null,
            ParentAttemptId = existing.AttemptId ?? existing.ParentAttemptId,
            AttemptId = null,
            AttemptBaseRevision = null,
            ExpectedOriginatingPullRequestHead = from is WorkStage.Fix or WorkStage.Continue
                && next == WorkStage.Continue
                && pr.Succeeded
                && pr.IsOpen == true
                ? pr.HeadSha
                : null,
            OriginatingPullRequestRecoveryClaim = null,
            OriginatingPullRequestRecoveryProofDigest = null,
            AttemptEnvelope = null,
            LaunchMayHaveBegunAt = null,
            AttemptAdmissionFactDurable = false,
            AttemptStartedFactDurable = false,
            AttemptRefusedFactDurable = false,
            AttemptSettledFactDurable = false,
            LaunchRecoveryKind = null,
            Error = null,
            Halted = false,
            ReconciliationKind = null,
            ReadinessMutationClaim = null,
        }, () =>
        {
            Directory.CreateDirectory(BatonPaths.QueueSpecsDirectory);
            File.WriteAllText(item.SpecFile, brief);
        }).ConfigureAwait(false);

        return advanced ? Fact(item, from, next, transition, now, room) : null;
    }

    private static async Task<QueueDecisionEntry?> StopAsync(
        QueueItem item,
        WorkStage from,
        WorkItemTransition transition,
        PullRequestObservation pr,
        string? verdictPath,
        DateTimeOffset now,
        string? room)
    {
        TaskReadyReceipt? taskReceipt = null;
        if (item.OwnedTask is { } task)
        {
            // Protected invariant: readiness names the exact approving review attempt, verdict
            // bytes, PR head and required-check observation that the existing lifecycle accepted.
            // The advancer clears AttemptId at this transition, so capture it before the CAS.
            var path = verdictPath ?? item.LastVerdict;
            if (path is null || !File.Exists(path) || pr.Number is not { } prNumber
                || pr.HeadSha is not { Length: 40 } head || !head.All(Uri.IsHexDigit)
                || pr.RequiredChecks != PullRequestChecks.Passing
                || pr.RequiredEvidence is not { } requiredEvidence
                || requiredEvidence.Repository != task.Repository || requiredEvidence.BaseBranch != "main"
                || !string.Equals(requiredEvidence.HeadSha, head, StringComparison.OrdinalIgnoreCase))
                throw new QueueStoreException($"Task '{task.Id}' cannot retain a ready receipt without exact PR, verdict, head and passing required checks.");
            var bytes = File.ReadAllBytes(path);
            if (!ReviewVerdictSchema.TryParse(bytes, out var approved, out _)
                || approved is not { Completion: ReviewCompletion.Complete, Decision: ReviewDecision.Approve }
                || !string.Equals(approved.ReviewedRef, head, StringComparison.OrdinalIgnoreCase))
                throw new QueueStoreException($"Task '{task.Id}' ready verdict no longer approves its exact PR head.");
            var reviewAttempt = item.AttemptId?.Value ?? task.Ready?.ReviewAttemptId;
            if (reviewAttempt is null)
                throw new QueueStoreException($"Task '{task.Id}' has no retained review attempt for readiness.");
            var verdictDigest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var checksId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{task.Repository}\0{prNumber}\0{head}\0{JsonSerializer.Serialize(requiredEvidence)}"))).ToLowerInvariant();
            var receiptId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{task.Id}\0{head}\0{reviewAttempt}\0{verdictDigest}\0{checksId}"))).ToLowerInvariant();
            taskReceipt = new TaskReadyReceipt(receiptId, task.Id, task.Repository, task.Issue,
                prNumber, head, reviewAttempt, verdictDigest, pr.RequiredChecks, checksId,
                requiredEvidence.ObservedAt, now < requiredEvidence.ObservedAt ? requiredEvidence.ObservedAt : now, requiredEvidence);
        }
        var stopped = await TryMarkAsync(item, existing => existing with
        {
            Stage = WorkStage.Ready,
            PullRequest = pr.Number ?? existing.PullRequest,
            Checks = pr.Checks ?? existing.Checks,
            ChecksObservedAt = pr.Checks is null ? existing.ChecksObservedAt : now,
            ChecksHeadSha = pr.Checks is null ? existing.ChecksHeadSha : pr.HeadSha,
            RequiredCheckEvidenceWait = RequiredCheckEvidenceWaitAfter(pr, existing.RequiredCheckEvidenceWait),
            LastVerdict = verdictPath ?? existing.LastVerdict,
            State = QueueItemState.Queued,
            RoomDirectory = null,
            LaunchedAt = null,
            AttemptId = null,
            AttemptBaseRevision = null,
            ExpectedOriginatingPullRequestHead = null,
            OriginatingPullRequestRecoveryClaim = null,
            OriginatingPullRequestRecoveryProofDigest = null,
            AttemptEnvelope = null,
            LaunchMayHaveBegunAt = null,
            AttemptAdmissionFactDurable = false,
            AttemptStartedFactDurable = false,
            AttemptRefusedFactDurable = false,
            AttemptSettledFactDurable = false,
            LaunchRecoveryKind = null,
            Error = null,
            ReconciliationKind = null,
            ReadinessMutationClaim = null,
            OwnedTask = existing.OwnedTask is null || taskReceipt is null ? existing.OwnedTask
                : existing.OwnedTask with { Ready = taskReceipt, Blocked = null },
        }).ConfigureAwait(false);

        return stopped ? Fact(item, from, WorkStage.Ready, transition, now, room) : null;
    }

    private static RequiredCheckEvidenceWait? RequiredCheckEvidenceWaitAfter(
        PullRequestObservation pr, RequiredCheckEvidenceWait? existing) =>
        existing is not null
        && pr.HeadSha is { Length: > 0 } observedHead
        && !string.Equals(existing.HeadSha, observedHead, StringComparison.Ordinal)
            ? null
            : pr.Succeeded
              && pr.RequiredChecks is not null
              && pr.RequiredChecks != PullRequestChecks.None
              && !pr.NoObservedRequiredEvidence
            ? null
            : existing;

    private static bool InvalidatesPriorCheckEvidence(PullRequestObservation pr, QueueItem existing) =>
        !pr.Succeeded
        && pr.HeadSha is { Length: > 0 } observedHead
        && (existing.RequiredCheckEvidenceWait is { } wait
                && !string.Equals(wait.HeadSha, observedHead, StringComparison.Ordinal)
            || existing.ChecksHeadSha is { Length: > 0 } checksHead
                && !string.Equals(checksHead, observedHead, StringComparison.Ordinal));

    private async Task<QueueDecisionEntry?> FailAsync(
        QueueItem item,
        WorkStage from,
        WorkItemTransition transition,
        string? verdictPath,
        DateTimeOffset now,
        string? room,
        RequiredCheckEvidenceWait? requiredCheckEvidenceWait = null,
        PullRequestObservation? pullRequest = null,
        WorkflowStatusView? sentinel = null,
        bool verdictAvailable = false,
        string? terminalOutcome = null,
        CancellationToken cancellationToken = default)
    {
        // Failed, not silently left: every arm that reaches here is one where the queue would have to
        // guess, and a guess dispatches a lane against evidence nobody checked. The reason is on the
        // item, so `baton queue list` is where the operator finds it — and `Halted` is what makes this
        // the LAST tick that reads this item, rather than the first of an unbounded run of identical
        // ones (QueueItem.Halted's own remarks). The room and the stage are left on the item, for the
        // reason spec/baton.md §13 gives.
        var stoppedJudgment = transition.Kind == WorkItemTransitionKind.NeedsOperator
            ? await CaptureStoppedWorkJudgmentAsync(item, from, pullRequest, sentinel, verdictAvailable,
                terminalOutcome, transition.HaltCause, now,
                cancellationToken).ConfigureAwait(false)
            : null;
        var failed = await TryMarkAsync(item, existing => existing with
        {
            Stage = from,
            State = QueueItemState.Failed,
            Error = transition.Reason,
            ReconciliationKind = transition.ReconciliationKind,
            PullRequest = transition.ReconciliationKind == QueueReconciliationKind.AwaitingRequiredCheckEvidence
                ? pullRequest?.Number : existing.PullRequest,
            LastVerdict = verdictPath ?? existing.LastVerdict,
            RequiredCheckEvidenceWait = requiredCheckEvidenceWait ?? existing.RequiredCheckEvidenceWait,
            ExpectedOriginatingPullRequestHead = null,
            OriginatingPullRequestRecoveryClaim = null,
            OriginatingPullRequestRecoveryProofDigest = null,
            Halted = true,
            ReadinessMutationClaim = null,
            StoppedWorkJudgment = stoppedJudgment is null ? existing.StoppedWorkJudgment
                : stoppedJudgment with { FollowAttachmentId = ConductorFollowSession.AttachmentAtHalt(existing) },
            OwnedTask = existing.OwnedTask is null ? null : existing.OwnedTask with
            {
                Blocked = new TaskBlockedDisposition(
                    transition.HaltCause.ToString().ToLowerInvariant(), transition.Reason, now,
                    stoppedJudgment?.Key),
            },
        }).ConfigureAwait(false);

        if (failed && stoppedJudgment?.Key is not null
            && OwnedHaltCommitted is not null)
        {
            var committed = await QueueStore.LoadAsync(BatonPaths.QueueFile, cancellationToken).ConfigureAwait(false);
            var source = committed.Items.Single(row => row.Tag == item.Tag);
            await OwnedHaltCommitted(source, cancellationToken).ConfigureAwait(false);
        }
        return failed ? new QueueDecisionEntry(
            now, item.Tag, QueueDecisionEntry.Failed, transition.Reason,
            LiveWeight: 0, FreeGb: null, FloorGb: 0, Room: room) : null;
    }

    private async Task<StoppedWorkJudgment?> CaptureStoppedWorkJudgmentAsync(
        QueueItem item,
        WorkStage stage,
        PullRequestObservation? pullRequest,
        WorkflowStatusView? sentinel,
        bool verdictAvailable,
        string? terminalOutcome,
        StoppedWorkHaltCause haltCause,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        if (item.StoppedWorkJudgment is { } prior
            && (item.AttemptId is null || prior.AttemptId == item.AttemptId && prior.Stage == stage)
            || item.StoppedWorkJudgment is not null && item.AttemptId is null
            || item.Repository is not { Length: > 0 } repository
            || item.OwnedTask is null
                && !IsStoppedWorkAdviceEnabledNow(repository)
                && !(haltCause == StoppedWorkHaltCause.MissingVerdict
                    && StoppedWorkAdviceSettings.IsAutomaticMissingVerdictReplacementReviewEnabled(repository))
            || item.ReplacementReviewAction is
            {
                Origin: QueueReplacementReviewOrigin.Automatic,
                CompletionProof: null,
            })
        {
            return null;
        }

        var holder = item.OwnedTask?.ConductorHolder;
        var blockedReasons = new List<string>();
        try
        {
            var identity = RepositoryIdentity.From("https://" + repository, null);
            var claim = identity is null
                ? null
                : await ConductorClaimStore.GetClaimAsync(identity, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            if (item.OwnedTask is null) holder = claim?.Holder;
            if (string.IsNullOrWhiteSpace(claim?.Holder))
            {
                blockedReasons.Add("repository has no recorded conductor holder");
            }
            else if (item.OwnedTask is not null
                && !string.Equals(claim.Holder, holder, StringComparison.Ordinal))
            {
                blockedReasons.Add($"conductor ownership drifted from recorded holder '{holder}' to '{claim.Holder}'");
            }
        }
        catch (ConductorClaimException)
        {
            blockedReasons.Add("recorded conductor ownership is unavailable");
        }

        var attempt = item.AttemptId;
        if (attempt is null)
        {
            blockedReasons.Add("source attempt identity is unavailable");
        }

        var observedAtUtc = observedAt.ToUniversalTime();
        var checksObservedAt = pullRequest?.Checks is null ? (DateTimeOffset?)null : observedAtUtc;
        var repairAllowance = item.AutomaticFixUsed is true || item.Round >= WorkStages.MaxRounds
            ? "exhausted"
            : "available";
        var requiredChecks = pullRequest?.RequiredChecks;
        var blockedReason = blockedReasons.Count == 0 ? null : string.Join("; ", blockedReasons);
        var state = blockedReason is null ? StoppedWorkJudgmentState.Pending : StoppedWorkJudgmentState.Blocked;
        var key = attempt is null
            ? null
            : StoppedWorkJudgmentKey.For(repository, item.Tag, attempt.Value, stage);
        // This is a NEW halt admission fact. Exact-true opt-in is sampled here and is never
        // inferred later from mutable settings or added to the historical evidence digest.
        var adviceEligibleAtHalt = IsStoppedWorkAdviceEnabledNow(repository);

        // Hash exactly the typed snapshot retained below. Transient prose (including the lifecycle
        // reason) is deliberately excluded: a replay must identify the same evidence, not a later
        // rendering of its explanation.
        var evidence = new
        {
            key,
            repository,
            item.Tag,
            attemptId = attempt?.Value,
            stage = WorkStages.Token(stage),
            observedAt = observedAtUtc,
            holder,
            pullRequest = pullRequest?.Number,
            pullRequestHead = pullRequest?.HeadSha,
            attemptBaseRevision = item.AttemptBaseRevision,
            terminalOutcome,
            terminalEvidenceAvailable = sentinel is not null,
            checks = pullRequest?.Checks,
            checksObservedAt,
            haltCause,
            repairAllowance,
            verdictAvailable,
            requiredChecks,
            state,
            blockedReason,
        };
        var digest = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(evidence)))).ToLowerInvariant();
        var captured = new StoppedWorkJudgment(
            key,
            repository,
            item.Tag,
            attempt,
            stage,
            observedAtUtc,
            holder,
            pullRequest?.Number,
            pullRequest?.HeadSha,
            item.AttemptBaseRevision,
            terminalOutcome,
            sentinel is not null,
            pullRequest?.Checks,
            checksObservedAt,
            digest,
            haltCause,
            repairAllowance,
            verdictAvailable,
            requiredChecks,
            state,
            blockedReason,
            AutomaticMissingVerdictReplacementReviewEligible:
                haltCause == StoppedWorkHaltCause.MissingVerdict
                && StoppedWorkAdviceSettings.IsAutomaticMissingVerdictReplacementReviewEnabled(repository),
            AdviceEligibleAtHalt: adviceEligibleAtHalt);
        return attempt is null ? captured : captured with
        {
            ContextSha256 = StoppedWorkAdviceEvidence.Hash(StoppedWorkAdviceEvidence.Context(captured)),
        };
    }

    /// <summary>
    /// The transition fact. <b>The reason carries the stage pair</b> as well as the lifecycle's own
    /// evidence, because the ledger's collapse key is <c>decision|reason|tag</c> — see
    /// <c>QueueDecisionEntry.Advanced</c>.
    /// </summary>
    private static QueueDecisionEntry Fact(
        QueueItem item, WorkStage from, WorkStage to, WorkItemTransition transition,
        DateTimeOffset now, string? room) =>
        new(now, item.Tag, QueueDecisionEntry.Advanced,
            $"{WorkStages.Token(from)} → {WorkStages.Token(to)}: {transition.Reason}",
            LiveWeight: 0, FreeGb: null, FloorGb: 0, Room: room);

    private static async Task<QueueDecisionEntry?> HaltLegacyRepositoryAsync(
        QueueItem item, WorkStage stage, DateTimeOffset now)
    {
        var reason = $"the {WorkStages.Token(stage)} lifecycle item has no trusted repository identity "
            + "(legacy queue entry), and re-adding this tag past implement would erase its lifecycle history. "
            + $"The item is halted: no 'baton queue' verb repairs this field in place. Stop the daemon, back up "
            + $"{BatonPaths.QueueFileName}, set only this row's Repository to its canonical host/owner/repo and "
            + "Halted to false, preserve Stage, State, Round, RoomDirectory, LastVerdict and AutomaticFixUsed, "
            + "then restart the daemon.";
        var halted = await TryMarkAsync(item, existing => existing with
        {
            // Ready is parked as Queued by definition; preserving that state is what lets clearing
            // Halted after the documented identity repair make it a reconciliation candidate again.
            State = stage == WorkStage.Ready ? QueueItemState.Queued : QueueItemState.Failed,
            Error = reason,
            Halted = true,
            ReconciliationKind = null,
            ExpectedOriginatingPullRequestHead = null,
            OriginatingPullRequestRecoveryClaim = null,
            OriginatingPullRequestRecoveryProofDigest = null,
            ReadinessMutationClaim = null,
        }).ConfigureAwait(false);

        return halted ? new QueueDecisionEntry(
            now, item.Tag, QueueDecisionEntry.Failed, reason,
            LiveWeight: 0, FreeGb: null, FloorGb: 0, Room: item.RoomDirectory) : null;
    }

    /// <summary>
    /// Commits only when the complete row observed before external I/O is still current. The optional
    /// spec write runs under the same queue mutex and only after that comparison succeeds, matching
    /// <c>QueueCommand</c>'s established queue/spec lock order.
    /// </summary>
    private static async Task<bool> TryMarkAsync(
        QueueItem expected, Func<QueueItem, QueueItem> update, Action? coupledWrite = null)
    {
        var expectedJson = JsonSerializer.Serialize(expected);
        var changed = false;
        await QueueStore.MutateAsync(
            BatonPaths.QueueFile,
            snapshot =>
            {
                var current = snapshot.Items.FirstOrDefault(i =>
                    string.Equals(i.Tag, expected.Tag, StringComparison.Ordinal));
                if (current is null
                    || snapshot.Held && IsAwaitingCheckEvidenceRecovery(expected)
                    || !string.Equals(
                        JsonSerializer.Serialize(current),
                        expectedJson,
                        StringComparison.Ordinal)
                    || current.AttemptEnvelope is { } currentEnvelope
                        && QueueFleetEventOutbox.HasPendingFor(snapshot, currentEnvelope.AttemptId))
                {
                    return snapshot;
                }

                coupledWrite?.Invoke();
                changed = true;
                return snapshot with
                {
                    Items = snapshot.Items
                        .Select(i => ReferenceEquals(i, current) ? update(i) : i)
                        .ToList(),
                };
            },
            CancellationToken.None).ConfigureAwait(false);
        return changed;
    }

    /// <summary>
    /// Claims the complete observed row under the queue mutex and returns the exact claimed value.
    /// Replacing an existing token is restart recovery: only the caller whose replacement wins may
    /// continue. The mutex is released before any GitHub call.
    /// </summary>
    private static async Task<QueueItem?> TryClaimReadinessAsync(QueueItem expected)
    {
        var expectedJson = JsonSerializer.Serialize(expected);
        QueueItem? claimed = null;
        await QueueStore.MutateAsync(
            BatonPaths.QueueFile,
            snapshot =>
            {
                var current = snapshot.Items.FirstOrDefault(i =>
                    string.Equals(i.Tag, expected.Tag, StringComparison.Ordinal));
                if (current is null
                    || !string.Equals(
                        JsonSerializer.Serialize(current),
                        expectedJson,
                        StringComparison.Ordinal))
                {
                    return snapshot;
                }

                claimed = current with { ReadinessMutationClaim = Guid.NewGuid().ToString("N") };
                return snapshot with
                {
                    Items = snapshot.Items
                        .Select(i => ReferenceEquals(i, current) ? claimed : i)
                        .ToList(),
                };
            },
            CancellationToken.None).ConfigureAwait(false);
        return claimed;
    }

    /// <summary>
    /// Refreshes independent board evidence on the existing delivery poller's forge cadence. There is
    /// at most one read per poll, never one per retained lane. Reads finish before
    /// the queue lock is acquired; the mutation re-derives the live key set so a late result cannot
    /// recreate evidence for a PR whose last lane was removed meanwhile.
    /// </summary>
    internal async Task RefreshPullRequestObservationsAsync(
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var snapshot = await QueueStore.LoadAsync(BatonPaths.QueueFile, cancellationToken).ConfigureAwait(false);
        var keys = ObservationKeys(snapshot.Items);
        var existing = (snapshot.PullRequestObservations ?? [])
            .GroupBy(o => new QualifiedPullRequest(o.Repository, o.PullRequest))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(o => o.AttemptedAt).First());
        var retryAfter = FleetProjectionWriter.StaleAfter();
        var due = keys
            .Where(key => !existing.TryGetValue(key, out var cached)
                || cached.AttemptedAt > now || now - cached.AttemptedAt >= retryAfter)
            // Oldest attempt first is the rotation. This matters when the delivery cadence is wider
            // than the retry window: every cached key may be due, and insertion order would otherwise
            // select the same first PR forever.
            .OrderBy(key => existing.TryGetValue(key, out var cached)
                ? cached.AttemptedAt
                : DateTimeOffset.MinValue)
            .ThenBy(key => key.Repository, StringComparer.Ordinal)
            .ThenBy(key => key.PullRequest)
            // One network read per delivery-poller tick is the explicit rate bound. Attempt stamps
            // rotate a larger retained history across later polls instead of bursting every due key.
            .Take(1)
            .ToList();

        var refreshed = new Dictionary<QualifiedPullRequest, QueuePullRequestObservation>();
        foreach (var key in due)
        {
            existing.TryGetValue(key, out var prior);
            var workspaces = snapshot.Items
                .Where(i => i.PullRequest == key.PullRequest
                    && string.Equals(i.Repository, key.Repository, StringComparison.Ordinal))
                .Select(i => i.Workspace)
                .Where(workspace => !string.IsNullOrWhiteSpace(workspace))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            refreshed[key] = await ReadBoardObservationAsync(key, workspaces, prior, now, cancellationToken)
                .ConfigureAwait(false);
        }

        if (refreshed.Count == 0 && existing.Keys.All(keys.Contains) && existing.Count == keys.Count)
        {
            return;
        }

        var mergedRetirements = new List<(string Tag, QueueDispositionOperation Operation)>();
        await QueueStore.MutateAndRecordAsync(
            BatonPaths.QueueFile,
            current =>
            {
                var currentKeys = ObservationKeys(current.Items);
                var observations = (current.PullRequestObservations ?? [])
                    .Where(o => currentKeys.Contains(new QualifiedPullRequest(o.Repository, o.PullRequest)))
                    .GroupBy(o => new QualifiedPullRequest(o.Repository, o.PullRequest))
                    .ToDictionary(g => g.Key, g => g.OrderByDescending(o => o.AttemptedAt).First());
                foreach (var pair in refreshed)
                {
                    if (currentKeys.Contains(pair.Key))
                    {
                        observations[pair.Key] = pair.Value;
                    }
                }

                var items = current.Items;
                foreach (var pair in refreshed.Where(pair =>
                    pair.Value is { State: PullRequestObservationStates.Merged, Error: null }))
                {
                    var retirement = new QueueRetirement(QueueRetirement.Merged, now,
                        $"trusted merged observation for PR #{pair.Key.PullRequest}");
                    items = items.Select(item =>
                    {
                        if (!IsEligibleForMergedObservationRetirement(item, pair.Key))
                        {
                            return item;
                        }

                        var operation = new QueueDispositionOperation(
                            Guid.NewGuid().ToString("N"), now, QueueDecisionEntry.Retired,
                            $"merged: PR #{pair.Key.PullRequest}");
                        mergedRetirements.Add((item.Tag, operation));
                        var retired = item with
                        {
                            Retirement = retirement,
                            OriginatingPullRequestRecoveryClaim =
                                item.OriginatingPullRequestRecoveryClaim == item.AttemptId
                                    ? null
                                    : item.OriginatingPullRequestRecoveryClaim,
                            OriginatingPullRequestRecoveryProofDigest = null,
                            DispositionOperations = [.. item.DispositionOutbox, operation],
                        };
                        return item.AttemptEnvelope is not null
                            && item.State == QueueItemState.Queued
                            && item.LaunchMayHaveBegunAt is null
                            && !QueueFleetEventOutbox.HasPendingFor(current, item.AttemptEnvelope.AttemptId)
                            ? retired with
                            {
                                AttemptEnvelope = null,
                                AttemptAdmissionFactDurable = false,
                                AttemptStartedFactDurable = false,
                                AttemptRefusedFactDurable = false,
                                AttemptSettledFactDurable = false,
                                LaunchRecoveryKind = null,
                            }
                            : retired;
                    }).ToList();
                }

                return current with
                {
                    Items = items,
                    PullRequestObservations = observations.Values
                        .OrderBy(o => o.Repository, StringComparer.Ordinal)
                        .ThenBy(o => o.PullRequest)
                        .ToList(),
                };
            },
            () =>
            {
                foreach (var (tag, operation) in mergedRetirements)
                {
                    QueueDecisionLedgerStore.AppendDispositionUnderQueueLock(
                        tag, operation, BatonPaths.QueueDecisionLedgerFile, cancellationToken);
                }
            },
            CancellationToken.None).ConfigureAwait(false);
    }

    // A roomless failed row is not proof of no launch: its room may have been created but not
    // persisted. Only a durable refused admission proves that no worker was admitted.
    private static bool IsEligibleForMergedObservationRetirement(QueueItem item, QualifiedPullRequest observation) =>
        item is
        {
            Stage: not null,
            Retirement: null,
            State: QueueItemState.Queued or QueueItemState.Failed,
            RoomDirectory: null,
            ReadinessMutationClaim: null,
            PullRequest: not null,
            Repository: not null,
        }
        && (item.State != QueueItemState.Failed
            || item.LastAdmission?.Result == TaskRequirementAdmission.Refused)
        && item.PullRequest == observation.PullRequest
        && string.Equals(item.Repository, observation.Repository, StringComparison.Ordinal);

    private static HashSet<QualifiedPullRequest> ObservationKeys(IReadOnlyList<QueueItem> items) =>
        items
            .Where(i => i.Retirement is null && i.Stage is not null && i.PullRequest is > 0 && i.Repository is { Length: > 0 })
            .Select(i => new QualifiedPullRequest(i.Repository!, i.PullRequest!.Value))
            .ToHashSet();

    private async Task<QueuePullRequestObservation> ReadBoardObservationAsync(
        QualifiedPullRequest key,
        IReadOnlyList<string> workspaces,
        QueuePullRequestObservation? prior,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var canonical = RepositoryIdentity.From("https://" + key.Repository, gitCommonDirectoryPath: null);
        if (!string.Equals(canonical?.RemoteValue, key.Repository, StringComparison.Ordinal))
        {
            return InvalidBoardObservation(key, now, "the stored repository identity is invalid");
        }

        var survivingWorkspaces = workspaces
            .Where(Directory.Exists)
            .ToList();
        if (_repositoryIdentity is not null)
        {
            foreach (var workspace in survivingWorkspaces)
            {
                var current = await _repositoryIdentity(workspace, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(current?.RemoteValue, key.Repository, StringComparison.Ordinal))
                {
                    return InvalidBoardObservation(
                        key, now,
                        $"repository context at '{workspace}' drifted from persisted '{key.Repository}'");
                }
            }
        }

        var workingDirectory = survivingWorkspaces.FirstOrDefault()
            ?? Path.GetDirectoryName(BatonPaths.QueueFile)!;
        GhCliResult result;
        using (var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            bound.CancelAfter(_boardObservationTimeout);
            try
            {
                result = await _gh.RunAsync(
                    workingDirectory,
                    ["pr", "view", key.PullRequest.ToString(CultureInfo.InvariantCulture),
                     "--json", BoardObservationJsonFields, "--repo", key.Repository],
                    bound.Token).WaitAsync(bound.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return FailedBoardObservation(
                    key, prior, now,
                    $"gh pr view timed out after {_boardObservationTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} seconds");
            }
        }
        if (!result.Started || result.ExitCode != 0)
        {
            return FailedBoardObservation(
                key, prior, now,
                !result.Started ? "gh pr view did not start" : $"gh pr view exited {result.ExitCode}");
        }

        try
        {
            using var document = JsonDocument.Parse(result.Stdout);
            var root = document.RootElement;
            var number = root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("number", out var n) && n.TryGetInt32(out var parsed) ? parsed : 0;
            var state = Text(root, "state")?.ToUpperInvariant() switch
            {
                "OPEN" => PullRequestObservationStates.Open,
                "MERGED" => PullRequestObservationStates.Merged,
                "CLOSED" => PullRequestObservationStates.Closed,
                _ => null,
            };
            var head = Text(root, "headRefOid");
            if (number != key.PullRequest || state is null || head is not { Length: > 0 })
            {
                return FailedBoardObservation(key, prior, now, "gh pr view returned malformed or mismatched PR evidence");
            }

            return new QueuePullRequestObservation(
                key.Repository, key.PullRequest, state, head, now, now, Error: null);
        }
        catch (JsonException ex)
        {
            return FailedBoardObservation(key, prior, now, $"gh pr view returned malformed JSON: {ex.Message}");
        }
    }

    private static QueuePullRequestObservation FailedBoardObservation(
        QualifiedPullRequest key, QueuePullRequestObservation? prior, DateTimeOffset now, string error) =>
        new(key.Repository, key.PullRequest, prior?.State, prior?.HeadSha, prior?.ObservedAt, now, error);

    /// <summary>
    /// Identity failure invalidates the provenance of a prior reading, unlike a transient forge
    /// failure where the repository-qualified key remains trusted and the prior reading stays useful
    /// as explicitly stale history.
    /// </summary>
    private static QueuePullRequestObservation InvalidBoardObservation(
        QualifiedPullRequest key, DateTimeOffset now, string error) =>
        new(key.Repository, key.PullRequest, State: null, HeadSha: null, ObservedAt: null,
            AttemptedAt: now, Error: error);

    /// <summary>
    /// Discovers the branch's PR from an open-only, head-scoped list, then reads required checks and
    /// re-reads the exact PR by number. That exact-number snapshot is the stability fence: check
    /// evidence is accepted only when number, head and draft state still describe the same open PR.
    /// Command failure and malformed output are explicit failed observations, never "no PR".
    /// </summary>
    /// <remarks>
    /// Closed and merged matches are returned as such and never passed to a readiness command. An empty
    /// successful list is distinct from a failed list. The queue never merges and never reopens a PR.
    /// </remarks>
    private async Task<PullRequestObservation> ReadPullRequestAsync(
        QueueItem item, CancellationToken cancellationToken, bool trustedDraftHandoff = false)
    {
        if (item.Branch is not { Length: > 0 } branch)
        {
            return PullRequestObservation.NoPullRequest;
        }

        if (!Directory.Exists(item.Workspace))
        {
            return PullRequestObservation.Failed($"workspace '{item.Workspace}' is unavailable");
        }

        if (item.Repository is not { Length: > 0 } repository)
        {
            return PullRequestObservation.Failed(
                "the lifecycle item has no trusted repository identity (legacy queue entry); "
                + "re-add it from the owning repository before GitHub reconciliation can continue");
        }

        var persistedIdentity = RepositoryIdentity.From("https://" + repository, gitCommonDirectoryPath: null);
        if (!string.Equals(persistedIdentity?.RemoteValue, repository, StringComparison.Ordinal))
        {
            return PullRequestObservation.Failed(
                $"the lifecycle item carries an invalid remote repository identity '{repository}'; "
                + "re-add it from the owning repository");
        }

        var currentIdentity = trustedDraftHandoff
            ? await ReadTrustedDraftRepositoryIdentityAsync(item, cancellationToken).ConfigureAwait(false)
            : _repositoryIdentity is null
                ? persistedIdentity
                : await _repositoryIdentity(item.Workspace, cancellationToken).ConfigureAwait(false);
        if (currentIdentity?.RemoteValue is not { Length: > 0 } currentRepository)
        {
            return PullRequestObservation.Failed(
                $"the repository identity for workspace '{item.Workspace}' is unavailable; expected '{repository}'");
        }

        if (!string.Equals(currentRepository, repository, StringComparison.Ordinal))
        {
            return PullRequestObservation.Failed(
                $"repository context drifted from persisted '{repository}' to '{currentRepository}'; "
                + "refusing all GitHub PR reads and mutations");
        }

        var before = await ReadPullRequestSnapshotAsync(item, cancellationToken, trustedDraftHandoff)
            .ConfigureAwait(false);
        if (!before.Succeeded || before.Number is null || before.IsOpen != true)
        {
            return before;
        }

        var required = await ReadRequiredCheckEvidenceAsync(item, before.HeadSha!, trustedDraftHandoff, cancellationToken)
            .ConfigureAwait(false);

        // Even on the discovery tick, the stability read is exact by number. A second same-branch PR
        // appearing between the two reads therefore cannot replace the candidate about to be stored.
        var after = await ReadPullRequestSnapshotAsync(
            item with { PullRequest = before.Number }, cancellationToken, trustedDraftHandoff)
            .ConfigureAwait(false);
        if (!after.Succeeded)
        {
            return after;
        }

        if (after.Number != before.Number || after.IsOpen != true || after.HeadSha != before.HeadSha
            || after.IsDraft != before.IsDraft)
        {
            return PullRequestObservation.Failed(
                $"PR #{before.Number} changed while its required checks were being observed", after);
        }

        return after with
        {
            RequiredChecks = required.State,
            RequiredEvidence = required.Evidence,
            Error = required.Error,
            NoObservedRequiredEvidence = required.NoObservedRequiredEvidence
        };
    }

    private Task<RequiredCheckEvidenceReader.Reading> ReadRequiredCheckEvidenceAsync(
        QueueItem item, string head, bool trustedDraftHandoff, CancellationToken cancellationToken) =>
        new RequiredCheckEvidenceReader((args, token) => trustedDraftHandoff
            ? RunBoundedGhAsync(item, args, token) : _gh.RunAsync(item.Workspace, args, token), _requiredCheckClock)
            .ReadAsync(item.Repository!, head, cancellationToken);

    /// <summary>
    /// Reads one operator-supplied PR through the same repository-qualified forge seam as lifecycle
    /// reconciliation. No queue or fleet state is changed here; the caller owns the retirement CAS.
    /// </summary>
    internal async Task<(QueuePullRequestObservation? Observation, string? Error)>
        ReadMergedPullRequestObservationAsync(
            QueueItem item,
            int pullRequest,
            DateTimeOffset observedAt,
            CancellationToken cancellationToken)
    {
        if (item.Branch is not { Length: > 0 })
        {
            return (null, "the lifecycle item has no recorded branch");
        }

        if (item.Repository is not { Length: > 0 })
        {
            return (null, "the lifecycle item has no trusted repository identity");
        }

        var reading = await ReadPullRequestSnapshotAsync(
            item with
            {
                PullRequest = pullRequest,
                Workspace = ExistingGhWorkingDirectory(item.Workspace),
            }, cancellationToken).ConfigureAwait(false);
        if (!reading.Succeeded)
        {
            return (null, reading.Error ?? $"GitHub PR lookup for #{pullRequest} failed");
        }

        if (reading.Number != pullRequest
            || !string.Equals(reading.State, "MERGED", StringComparison.Ordinal))
        {
            return (null, $"PR #{pullRequest} did not report a positive merged state");
        }

        return (new QueuePullRequestObservation(
            item.Repository, pullRequest, PullRequestObservationStates.Merged,
            reading.HeadSha, observedAt, observedAt, Error: null), null);
    }

    private static string ExistingGhWorkingDirectory(string workspace) =>
        Directory.Exists(workspace) ? workspace : Environment.CurrentDirectory;

    private async Task<PullRequestObservation> ReadPullRequestSnapshotAsync(
        QueueItem item, CancellationToken cancellationToken, bool trustedDraftHandoff = false)
    {
        var branch = item.Branch!;
        var persistedNumber = item.PullRequest;
        var args = persistedNumber is { } exact
            ? RepositoryArgs(
                item,
                "pr", "view", exact.ToString(CultureInfo.InvariantCulture),
                "--json", PullRequestJsonFields)
            : RepositoryArgs(
                item,
                "pr", "list", "--head", branch, "--state", "open", "--limit", "100",
                "--json", PullRequestJsonFields);
        var result = trustedDraftHandoff
            ? await RunBoundedGhAsync(item, args, cancellationToken).ConfigureAwait(false)
            : await _gh.RunAsync(item.Workspace, args, cancellationToken).ConfigureAwait(false);
        if (!result.Started || result.ExitCode != 0)
        {
            var operation = persistedNumber is null ? "gh pr list" : $"gh pr view {persistedNumber}";
            return PullRequestObservation.Failed(
                !result.Started ? $"{operation} did not start" : $"{operation} exited {result.ExitCode}");
        }

        try
        {
            using var document = JsonDocument.Parse(result.Stdout);
            if (persistedNumber is { } expected)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return PullRequestObservation.Failed(
                        $"gh pr view returned a non-object JSON value for persisted PR #{expected}");
                }

                return TryReadPullRequest(document.RootElement, item, expected, out var exactObservation, out var exactError)
                    ? exactObservation
                    : PullRequestObservation.Failed(exactError!);
            }

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return PullRequestObservation.Failed("gh pr list returned a non-array JSON value");
            }

            var candidates = document.RootElement.EnumerateArray().ToList();
            if (candidates.Count == 0)
            {
                return PullRequestObservation.NoPullRequest;
            }

            var matches = new List<PullRequestObservation>();
            foreach (var candidate in candidates)
            {
                if (!TryReadPullRequest(candidate, item, expectedNumber: null, out var observation, out var error))
                {
                    return PullRequestObservation.Failed(error!);
                }

                matches.Add(observation);
            }

            if (matches.Count != 1)
            {
                return PullRequestObservation.Failed(
                    $"gh pr list found {matches.Count} identity-valid open PRs for branch '{branch}'; "
                    + "refusing to choose one before persistence");
            }

            return matches[0];
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine(
                $"WorkItemAdvancer: could not read GitHub PR output for branch '{branch}' as JSON: {ex.Message}");
            return PullRequestObservation.Failed($"GitHub PR lookup returned malformed JSON: {ex.Message}");
        }
    }

    /// <summary>
    /// Validates the PR identity before any number can reach queue persistence or a readiness mutation.
    /// Every command is explicitly scoped to the canonical <see cref="QueueItem.Repository"/>. A present
    /// workspace is also compared with that identity before the lookup; an absent historical workspace
    /// relies on the caller's persisted-identity validation. The remaining identity is the exact number
    /// (once persisted), same-repository head, recorded branch, and the lifecycle's <c>main</c> base.
    /// </summary>
    private static bool TryReadPullRequest(
        JsonElement root,
        QueueItem item,
        int? expectedNumber,
        out PullRequestObservation observation,
        out string? error)
    {
        observation = PullRequestObservation.NoPullRequest;
        error = null;
        if (root.ValueKind != JsonValueKind.Object)
        {
            error = "GitHub PR lookup returned a non-object pull-request entry";
            return false;
        }

        var number = root.TryGetProperty("number", out var n) && n.ValueKind == JsonValueKind.Number
            && n.TryGetInt32(out var parsedNumber) && parsedNumber > 0
                ? parsedNumber
                : (int?)null;
        if (number is null)
        {
            error = "GitHub PR lookup returned a PR without a valid number";
            return false;
        }

        if (expectedNumber is { } expected && number != expected)
        {
            error = $"GitHub returned PR #{number} while the queue tracks exact PR #{expected}";
            return false;
        }

        var state = Text(root, "state")?.ToUpperInvariant();
        var isOpen = state switch
        {
            "OPEN" => true,
            "CLOSED" or "MERGED" => false,
            _ => (bool?)null,
        };
        var isDraft = root.TryGetProperty("isDraft", out var d)
            && d.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? d.GetBoolean()
                : (bool?)null;
        var headSha = Text(root, "headRefOid");
        var mergeSha = root.TryGetProperty("mergeCommit", out var merge)
            && merge.ValueKind == JsonValueKind.Object
            ? Text(merge, "oid")
            : null;
        var headBranch = Text(root, "headRefName");
        var baseBranch = Text(root, "baseRefName");
        var sameRepository = root.TryGetProperty("isCrossRepository", out var cross)
            && cross.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? !cross.GetBoolean()
                : (bool?)null;

        if (isOpen is null || isDraft is null || headSha is not { Length: > 0 })
        {
            error = $"GitHub PR lookup returned incomplete state for PR #{number}";
            return false;
        }

        if (!string.Equals(headBranch, item.Branch, StringComparison.Ordinal)
            || !string.Equals(baseBranch, "main", StringComparison.Ordinal)
            || sameRepository != true)
        {
            error = $"PR #{number} does not match the queue item's repository/base/head identity";
            return false;
        }

        var statusCheckRollup = root.TryGetProperty("statusCheckRollup", out var rollup) ? rollup : (JsonElement?)null;
        var checks = PullRequestChecks.Summarize(statusCheckRollup);
        var checkRuns = PullRequestChecks.ObserveRuns(statusCheckRollup);
        observation = new PullRequestObservation(
            true, number, headSha, mergeSha, state, isOpen, isDraft, checks, checkRuns, null, null);
        return true;
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private async Task RecordOwnedObservationsAsync(
        QueueItem item,
        WorkStage stage,
        ReviewVerdict? verdict,
        PullRequestObservation pr,
        string? workspaceHead,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        if (item.AttemptId is not { } attemptId)
        {
            // Historical/imported rows have no producer-owned attempt id. Do not manufacture one
            // from their tag, room path, branch or timestamps.
            return;
        }

        FleetEventDraft Base(FleetEventKind kind, string key) => new(
            kind,
            key,
            observedAt,
            AttemptId: attemptId,
            ParentAttemptId: item.ParentAttemptId,
            WorkId: new FleetWorkId(item.Tag),
            RoomId: item.RoomDirectory is { Length: > 0 } room
                ? new FleetRoomId(BatonPaths.RecordKey(room))
                : null,
            IssueId: item.Issue,
            PullRequestId: pr.Number ?? item.PullRequest,
            Vendor: item.Adapter,
            Model: item.Model,
            Effort: item.Effort,
            DeclaredRole: item.Role,
            EffectiveGrant: item.LastAdmission?.EffectiveGrant);

        var revisionKind = stage switch
        {
            WorkStage.Implement => FleetRevisionKind.Implementation,
            WorkStage.Fix or WorkStage.Continue => FleetRevisionKind.Repair,
            _ => (FleetRevisionKind?)null,
        };
        if (revisionKind is not null
            && item.AttemptBaseRevision is { Length: > 0 } baseRevision
            && workspaceHead is { Length: > 0 } producedHead
            && !string.Equals(baseRevision, producedHead, StringComparison.OrdinalIgnoreCase))
        {
            await _appendFleetEvent(
                Base(FleetEventKind.RevisionProduced, $"revision:{attemptId.Value}:{producedHead}") with
                {
                    RevisionId = new FleetRevisionId(producedHead),
                    RevisionKind = revisionKind,
                }, cancellationToken).ConfigureAwait(false);
        }

        if (pr is { Succeeded: true, Number: { } number, HeadSha: { Length: > 0 } pullRequestHead })
        {
            await _appendFleetEvent(
                Base(FleetEventKind.PullRequestBound, $"pr-bound:{attemptId.Value}:{number}:{pullRequestHead}") with
                {
                    PullRequestId = number,
                    RevisionId = new FleetRevisionId(pullRequestHead),
                }, cancellationToken).ConfigureAwait(false);

            foreach (var check in pr.CheckRuns)
            {
                await _appendFleetEvent(
                    Base(
                        FleetEventKind.CheckObserved,
                        CheckDedupeKey(attemptId, number, pullRequestHead, check)) with
                    {
                        PullRequestId = number,
                        RevisionId = new FleetRevisionId(pullRequestHead),
                        CheckRunId = new FleetCheckRunId(check.CheckRunId),
                        CheckName = check.Name,
                        CheckStatus = check.Status,
                        CheckConclusion = check.Conclusion,
                        CheckStartedAt = check.StartedAt,
                        CheckCompletedAt = check.CompletedAt,
                    }, cancellationToken).ConfigureAwait(false);
            }
        }

        if (stage is WorkStage.Review or WorkStage.ReReview && verdict is not null)
        {
            var reviewedRevision = IsFullSha(verdict.ReviewedRef)
                ? new FleetRevisionId(verdict.ReviewedRef)
                : (FleetRevisionId?)null;
            await _appendFleetEvent(
                Base(
                    FleetEventKind.ReviewVerdictObserved,
                    $"review:{attemptId.Value}:{item.Round}:{verdict.ReviewedRef}:{verdict.Decision?.ToString() ?? "unknown"}") with
                {
                    RevisionId = reviewedRevision,
                    ReviewRoundId = new FleetReviewRoundId($"{item.Tag}:{item.Round}"),
                    ReviewVerdict = verdict.Decision?.ToString().ToLowerInvariant(),
                }, cancellationToken).ConfigureAwait(false);
        }

        if (pr is { Succeeded: true, Number: { } mergedNumber, MergeSha: { Length: > 0 } mergeSha })
        {
            await _appendFleetEvent(
                Base(FleetEventKind.MergeObserved, $"merge:{attemptId.Value}:{mergedNumber}:{mergeSha}") with
                {
                    PullRequestId = mergedNumber,
                    RevisionId = new FleetRevisionId(mergeSha),
                    Outcome = "merged",
                }, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsFullSha(string value) =>
        value.Length == 40 && value.All(char.IsAsciiHexDigit);

    private static string CheckDedupeKey(
        FleetAttemptId attemptId,
        int pullRequest,
        string revision,
        PullRequestCheckRun check) =>
        string.Join(
            ':',
            "check",
            attemptId.Value,
            pullRequest.ToString(CultureInfo.InvariantCulture),
            revision,
            check.CheckRunId,
            check.Status ?? "unknown",
            check.Conclusion ?? "unknown",
            check.StartedAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "unknown",
            check.CompletedAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "unknown");

    private static Task<FleetEvent?> AppendOperationalFleetEventAsync(
        FleetEventDraft draft, CancellationToken cancellationToken) =>
        FleetEventLog.OpenOperational().Append(draft, cancellationToken);

    private static string[] RepositoryArgs(QueueItem item, params string[] args) =>
        [.. args, "--repo", item.Repository!];

    private sealed record PullRequestObservation(
        bool Succeeded,
        int? Number,
        string? HeadSha,
        string? MergeSha,
        string? State,
        bool? IsOpen,
        bool? IsDraft,
        string? Checks,
        IReadOnlyList<PullRequestCheckRun> CheckRuns,
        string? RequiredChecks,
        string? Error,
        RequiredCheckEvidence? RequiredEvidence = null,
        bool NoObservedRequiredEvidence = false)
    {
        internal static PullRequestObservation NoPullRequest { get; } =
            new(true, null, null, null, null, false, null, null, [], null, null);

        internal static PullRequestObservation Failed(
            string error, PullRequestObservation? last = null) =>
            new(false, last?.Number, last?.HeadSha, last?.MergeSha, last?.State, last?.IsOpen, last?.IsDraft,
                last?.Checks, last?.CheckRuns ?? [], null, error);
    }

    /// <summary>
    /// The verdict this room produced, if any: the sentinel's own resolved <c>Outputs</c> searched for
    /// <c>verdict.json</c>, exactly as <c>WatchFireService.BuildPayload</c> does — the engine already
    /// owns that path, so nothing here re-derives an artifacts directory.
    /// </summary>
    private static string? FindVerdict(WorkflowStatusView? sentinel)
    {
        return sentinel?.Outputs?.FirstOrDefault(p => string.Equals(
            Path.GetFileName(p), CostLedgerStore.VerdictOutputName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Carries the terminal sentinel's engine-owned delivery members into the pure lifecycle. The
    /// sentinel can contain more than one execution after a resume, so prefer entries belonging to a
    /// terminal step execution; legacy sentinels without step execution ids use their only available
    /// delivery observations rather than dropping a typed failure.
    /// </summary>
    private static IReadOnlyList<string>? ReadDeliveryFailingMembers(WorkflowStatusView? sentinel)
    {
        if (sentinel?.Delivery is not { Count: > 0 } deliveries)
        {
            return null;
        }

        var stepExecutions = sentinel.Steps
            .Select(step => step.Execution)
            .Where(execution => execution is { Length: > 0 })
            .ToHashSet(StringComparer.Ordinal);
        var relevant = stepExecutions.Count == 0
            ? deliveries
            : deliveries.Where(delivery => stepExecutions.Contains(delivery.Execution));
        var members = relevant
            .SelectMany(delivery => delivery.AuthoritativeObservation.FailingMembers ?? [])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return members.Length == 0 ? null : members;
    }

    /// <summary>
    /// Reads the current terminal execution's engine-owned grant decisions. The aggregate refused-tool
    /// count is deliberately not used here. The exact Codex pre-creation list denial is incidental to
    /// draft creation; every other own-PR-only denial remains blocking.
    /// </summary>
    private static bool HasPullRequestAuthorityRefusal(
        string? roomDirectory, WorkflowStatusView? sentinel)
    {
        if (roomDirectory is not { Length: > 0 })
        {
            return false;
        }

        try
        {
            var artifactsRoot = Path.Combine(roomDirectory, ArtifactManager.ArtifactsDirectoryName);
            if (!Directory.Exists(artifactsRoot))
            {
                return false;
            }

            var executionIds = sentinel?.Steps
                .Select(step => step.Execution)
                .Where(execution => execution is { Length: > 0 })
                .Select(execution => execution!)
                .ToHashSet(StringComparer.Ordinal) ?? [];
            var executionDirectories = executionIds.Count > 0
                ? executionIds.Select(execution => Path.Combine(artifactsRoot, "execution_" + execution))
                : Directory.EnumerateDirectories(artifactsRoot, "execution_*", SearchOption.TopDirectoryOnly);
            foreach (var executionDirectory in executionDirectories)
            {
                var path = Path.Combine(executionDirectory, GrantDecisionLog.FileName);
                if (File.Exists(path) && ContainsBlockingPullRequestAuthorityDenial(path))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable optional observation is not evidence of a refusal. The lifecycle will
            // retain its ordinary settled-row handling and the operator can inspect the room.
        }

        return false;
    }

    private static bool ContainsBlockingPullRequestAuthorityDenial(string path)
    {
        foreach (var line in File.ReadLines(path))
        {
            var parseResult = GrantDecision.ParseJsonLine(line, out var decision);
            if (decision is not { Allowed: false, Rule: var rule }
                || rule != GrantRules.OwnPullRequestOnly)
            {
                continue;
            }

            if (parseResult == GrantDecisionParseResult.MalformedReason
                || decision.Reason is not { Length: > 0 } reason
                || !IsIncidentalOwnPullRequestReadReason(reason))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsIncidentalOwnPullRequestReadReason(string reason)
    {
        const string incidentalListReason =
            "[baton:grant-refused] Baton refuses this command: `gh pr list` enumerates pull requests this room does not own — "
            + "an implement lane reads its own PR only. No `gh pr` read is allowed until this room's own "
            + "`gh pr create` reports one. `gh issue view` is unaffected.";
        return reason == incidentalListReason;
    }

    /// <summary>
    /// The verdict, through <see cref="ReviewVerdictSchema.TryParse"/> and no second reader. A file that
    /// does not satisfy that one definition is null — which the lifecycle treats as "the review said
    /// nothing", never as an approval.
    /// </summary>
    private static ReviewVerdict? TryReadVerdict(string path)
    {
        try
        {
            return ReviewVerdictSchema.TryParse(File.ReadAllBytes(path), out var verdict, out _) ? verdict : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Re-proves a room-bearing terminal row while the queue mutation lock is held. The earlier
    /// asynchronous read drives lifecycle observation; it cannot authorize retirement after later
    /// PR and workspace awaits if the sentinel has since disappeared or become unreadable.
    /// </summary>
    private static bool HasReadableTerminalSentinelAtMutation(string roomDirectory)
    {
        var path = Path.Combine(roomDirectory, TerminalSentinelWriter.TerminalSentinelFileName);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<WorkflowStatusView>(stream) is not null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The verdict the item's last review round recorded, re-read off <see cref="QueueItem.LastVerdict"/>
    /// — null when there has been no review yet, when the operator moved that room, or when the file no
    /// longer parses. A brief renders without findings in that case rather than failing the round.
    /// </summary>
    private static ReviewVerdict? ReadLastVerdict(QueueItem item) =>
        item.LastVerdict is { Length: > 0 } path && File.Exists(path) ? TryReadVerdict(path) : null;

    /// <summary>The workspace's own HEAD, or null when it cannot be read — a worktree the operator
    /// removed, or one with no commits. Null reads as "not pushed".</summary>
    private static async Task<string?> ReadWorkspaceHeadAsync(string workspace, CancellationToken cancellationToken)
    {
        try
        {
            return await WorkspaceHead.CaptureAsync(workspace, cancellationToken).ConfigureAwait(false);
        }
        catch (CliArgumentException)
        {
            return null;
        }
    }
}
