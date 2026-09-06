using Baton.Domain;

namespace Baton.Projection;

/// <summary>
/// Reconstructs <see cref="FlowState"/> from event history:
/// <c>FlowState = Project(EventStore, WorkflowDefinitionSnapshot)</c>. A pure function — no I/O, no
/// wall-clock time, no live process state — so identical inputs always produce an identical
/// result. Supports incremental projection via <see cref="ProjectionCheckpoint"/> (#903 Scope 1).
/// </summary>
public static class StateProjector
{
    /// <summary>
    /// Projects <paramref name="events"/> — read linearly, in append order, from Flow's half of the
    /// Event Store — against <paramref name="snapshot"/> into a <see cref="FlowState"/>.
    /// If an optional <paramref name="checkpoint"/> is provided and valid, replays only events past
    /// <see cref="ProjectionCheckpoint.EventOffset"/>, returning the updated projected state.
    /// </summary>
    public static FlowState Project(
        IReadOnlyList<FlowEvent> events,
        WorkflowDefinitionSnapshot snapshot,
        ProjectionCheckpoint? checkpoint = null)
    {
        return ProjectAndCheckpoint(events, snapshot, checkpoint).State;
    }

    /// <summary>
    /// Projects <paramref name="events"/> against <paramref name="snapshot"/>, returning both the projected
    /// <see cref="FlowState"/> and a fresh <see cref="ProjectionCheckpoint"/> capturing the state at <paramref name="events"/>.Count.
    /// </summary>
    public static (FlowState State, ProjectionCheckpoint Checkpoint) ProjectAndCheckpoint(
        IReadOnlyList<FlowEvent> events,
        WorkflowDefinitionSnapshot snapshot,
        ProjectionCheckpoint? checkpoint = null,
        long logByteOffset = 0)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(snapshot);

        int skipCount = 0;
        long totalEventOffset = 0;
        ProjectionCheckpointState state;

        if (checkpoint is not null)
        {
            if (checkpoint.EventOffset < 0 || (logByteOffset == 0 && checkpoint.EventOffset > events.Count))
            {
                Console.Error.WriteLine(
                    $"[ProjectionCheckpoint] Fallback to full replay LOUDLY: Checkpoint EventOffset ({checkpoint.EventOffset}) exceeds log event count ({events.Count}) or is invalid.");
                state = ProjectionCheckpointState.CreateEmpty();
                skipCount = 0;
                totalEventOffset = events.Count;
            }
            else if (logByteOffset == 0)
            {
                // Full event list supplied
                state = checkpoint.State.DeepCopy();
                skipCount = (int)checkpoint.EventOffset;
                totalEventOffset = events.Count;
            }
            else
            {
                // Tail-only event list supplied
                state = checkpoint.State.DeepCopy();
                skipCount = 0;
                totalEventOffset = checkpoint.EventOffset + events.Count;
            }
        }
        else
        {
            state = ProjectionCheckpointState.CreateEmpty();
            skipCount = 0;
            totalEventOffset = events.Count;
        }

        for (int i = skipCount; i < events.Count; i++)
        {
            ApplyEvent(events[i], state);
        }

        var flowState = DeriveFlowState(state, snapshot);
        var finalByteOffset = logByteOffset > 0 ? logByteOffset : (checkpoint?.ByteOffset ?? 0);
        var newCheckpoint = new ProjectionCheckpoint(totalEventOffset, state.DeepCopy(), finalByteOffset, ProjectionCheckpoint.CurrentVersion);
        return (flowState, newCheckpoint);
    }

    private static void ApplyEvent(FlowEvent flowEvent, ProjectionCheckpointState state)
    {
        switch (flowEvent)
        {
            case FlowEvent.ExecutionAttemptStarted attemptStarted:
                state.WorkspaceHeadShaAtStartByExecutionId[attemptStarted.ExecutionId] = attemptStarted.WorkspaceHeadShaAtStart;
                break;

            case FlowEvent.ExecutionRequestAccepted accepted:
                state.AcceptedRequestByExecutionId[accepted.Request.ExecutionId] = accepted.Request;
                if (accepted.Request.StepId is { } acceptedStepId)
                {
                    state.LatestExecutionIdByStepId[acceptedStepId] = accepted.Request.ExecutionId;
                    state.UpstreamExecutionIdsByStepId[acceptedStepId] = new Dictionary<StepId, ExecutionId>(accepted.Request.UpstreamExecutionIds);
                    state.StepIdByExecutionId[accepted.Request.ExecutionId] = acceptedStepId;
                    state.ExecutionCountByStepId[acceptedStepId] =
                        state.ExecutionCountByStepId.GetValueOrDefault(acceptedStepId) + 1;

                    // This dispatch is the consequence a prior decision was owed, if any — fulfilled now.
                    state.PendingSupplementaryExecutionIdByStepId.Remove(acceptedStepId);
                    state.PendingSupersedeTargetStepIds.Remove(acceptedStepId);
                    state.RetryNotBeforeByStepId.Remove(acceptedStepId);
                    state.RetryDelayMsByStepId.Remove(acceptedStepId);
                    state.RetryScheduledForExecutionIdByStepId.Remove(acceptedStepId);

                    // #1586 S1 / #1623 F5: a fresh dispatch reopens a foreclosed or indeterminate step —
                    // a foreclosure/indeterminate state blocks MayRetry, not admission, and this is the
                    // same "the pump is dispatching it, so whatever blocked it is moot" reasoning the
                    // clears above already rest on.
                    state.RetryForeclosedStepIds.Remove(acceptedStepId);

                    // #1608 review: the same "the pump is dispatching it, so whatever blocked it is
                    // moot" reasoning applies to an unresolved Indeterminate, whichever of its three
                    // producers set it. For the captured-response producer nothing ever reaches this
                    // while true (MayRetry refuses the step unconditionally and
                    // ExternalDecisionValidator refuses a decide against it, so only CaptureResolved
                    // clears the flag before any fresh dispatch can be admitted) — cleared here anyway,
                    // defensively, so a future producer (S2's baton settle, or a new DecisionType) that
                    // ever mints a fresh execution for this step cannot leave WorkflowOutcome pinned to
                    // Indeterminate and MayRetry permanently false underneath a legitimate new attempt.
                    // The reason text is cleared in the same breath as the flag, never separately.
                    state.IndeterminateAwaitingResolutionStepIds.Remove(acceptedStepId);
                    state.IndeterminateReasonByStepId.Remove(acceptedStepId);
                    state.IndeterminateProducerByStepId.Remove(acceptedStepId);
                    state.IndeterminateVerifyTailByStepId.Remove(acceptedStepId);

                    // #1622 (c)/(d): a fresh dispatch is a new attempt, not a continuation of a
                    // previously conductor-resolved one -- same reasoning as the clears above.
                    state.ResolvedByConductorStepIds.Remove(acceptedStepId);
                    state.ConductorRejectedStepIds.Remove(acceptedStepId);

                    // #1622/#1390: a fresh dispatch's own eventual ExecutionSucceeded is what sets
                    // these next, if it settles Succeeded at all -- a prior attempt's workspaceChanged/
                    // hollow must not survive onto this one, same "whatever was true before is moot"
                    // reasoning as every clear above.
                    state.WorkspaceChangedByStepId.Remove(acceptedStepId);
                    state.HollowByStepId.Remove(acceptedStepId);
                    state.HollowReasonByStepId.Remove(acceptedStepId);

                    // #1702: a fresh dispatch's own verify step (if any) speaks for this attempt, not
                    // whatever the PRIOR attempt's pre-flight check found.
                    state.VerifyNotRunReasonByStepId.Remove(acceptedStepId);
                }
                else
                {
                    state.StepLessExecutionsInOrder.Add(new StepLessExecutionState(accepted.Request.ExecutionId, accepted.Request.Worker));
                }

                break;

            case FlowEvent.ExecutionSucceeded succeeded:
                state.UnmatchedVerifyExecutionIds.Remove(succeeded.ExecutionId);
                state.SucceededExecutionIds.Add(succeeded.ExecutionId);
                state.TerminalStatusByExecutionId[succeeded.ExecutionId] = StepStatus.Succeeded;
                if (state.StepIdByExecutionId.TryGetValue(succeeded.ExecutionId, out var succeededStepId))
                {
                    state.ConsecutiveFailureCountByStepId[succeededStepId] = 0;
                    state.LatestFailureClassificationByStepId[succeededStepId] = null;
                    state.LatestFailureReasonByStepId[succeededStepId] = null;
                    state.LatestExecutionFailedRetryNotBeforeByStepId[succeededStepId] = null;
                    state.LatestCapturedResponseFileByStepId[succeededStepId] = null;
                    state.LatestUnsatisfiedOutputNamesByStepId[succeededStepId] = null;
                    // #1622/#1390: carried verbatim off the event -- see FlowEvent.ExecutionSucceeded's
                    // own remarks for the null-means-not-tree-changing-or-history-predates-the-field
                    // reading.
                    state.WorkspaceChangedByStepId[succeededStepId] = succeeded.WorkspaceChanged;
                    state.HollowByStepId[succeededStepId] = succeeded.Hollow;
                    state.HollowReasonByStepId[succeededStepId] = succeeded.HollowReason;
                }

                break;

            case FlowEvent.ExecutionFailed failed:
                state.UnmatchedVerifyExecutionIds.Remove(failed.ExecutionId);
                state.TerminalStatusByExecutionId[failed.ExecutionId] = StepStatus.Failed;
                if (state.StepIdByExecutionId.TryGetValue(failed.ExecutionId, out var failedStepId))
                {
                    if (failed.FailureClassification != FailureClassification.ExhaustedUntil)
                    {
                        state.ConsecutiveFailureCountByStepId[failedStepId] =
                            state.ConsecutiveFailureCountByStepId.GetValueOrDefault(failedStepId) + 1;
                    }

                    state.LatestFailureClassificationByStepId[failedStepId] = failed.FailureClassification;
                    state.LatestFailureReasonByStepId[failedStepId] = failed.Reason;
                    state.LatestExecutionFailedRetryNotBeforeByStepId[failedStepId] = failed.RetryNotBefore;
                    state.LatestCapturedResponseFileByStepId[failedStepId] = failed.CapturedResponseFile;
                    state.LatestUnsatisfiedOutputNamesByStepId[failedStepId] =
                        failed.UnsatisfiedOutputNames is null ? null : new List<string>(failed.UnsatisfiedOutputNames);
                }

                break;

            case FlowEvent.ExecutionCancelled cancelled:
                state.UnmatchedVerifyExecutionIds.Remove(cancelled.ExecutionId);
                state.TerminalStatusByExecutionId[cancelled.ExecutionId] = StepStatus.Cancelled;

                // #1563: a park-abort settles a Failed, quota-parked execution as Cancelled (the
                // idle-deferral wait's own arrest seam) without ever dispatching a new attempt — so,
                // unlike ExecutionRequestAccepted's clear above, nothing else will clear the retry
                // this exact execution was scheduled for. Left in place, the idle wait's own
                // pendingDeferrals check (MutationInterface) reads this stale RetryNotBefore and
                // keeps waiting out the very deadline the cancellation was meant to end. Guarded by
                // matching RetryScheduledForExecutionId, not just StepId: a retry already
                // re-scheduled for a NEWER execution of the same step must survive this clear.
                if (state.StepIdByExecutionId.TryGetValue(cancelled.ExecutionId, out var cancelledStepId)
                    && state.RetryScheduledForExecutionIdByStepId.GetValueOrDefault(cancelledStepId) == cancelled.ExecutionId)
                {
                    state.RetryNotBeforeByStepId.Remove(cancelledStepId);
                    state.RetryDelayMsByStepId.Remove(cancelledStepId);
                    state.RetryScheduledForExecutionIdByStepId.Remove(cancelledStepId);
                }

                break;

            case FlowEvent.WorkflowPaused paused:
                state.PausedExecutionIds.Add(paused.ExecutionId);
                state.EverPausedExecutionIds.Add(paused.ExecutionId);
                break;

            case FlowEvent.ExternalDecisionRecorded decision:
                state.ReferencedExecutionIdByDecisionId[decision.DecisionId] = decision.ReferencedExecutionId;
                state.DecisionTypeByDecisionId[decision.DecisionId] = decision.DecisionType;
                if (decision.TargetStepId is { } declaredTargetStepId)
                {
                    state.TargetStepIdByDecisionId[decision.DecisionId] = declaredTargetStepId;
                }

                if (decision.SupplementaryExecutionId is { } declaredSupplementaryExecutionId)
                {
                    state.SupplementaryExecutionIdByDecisionId[decision.DecisionId] = declaredSupplementaryExecutionId;
                }

                break;

            case FlowEvent.WorkflowResumed resumed:
                if (state.ReferencedExecutionIdByDecisionId.TryGetValue(resumed.DecisionId, out var resumedExecutionId))
                {
                    state.PausedExecutionIds.Remove(resumedExecutionId);
                    var resumedDecisionType = state.DecisionTypeByDecisionId.GetValueOrDefault(resumed.DecisionId);
                    ExecutionId? supplementaryExecutionId = state.SupplementaryExecutionIdByDecisionId.TryGetValue(
                        resumed.DecisionId, out var declaredSupplement)
                        ? declaredSupplement
                        : null;

                    if (resumedDecisionType == DecisionType.Reject)
                    {
                        state.TerminalStatusByExecutionId[resumedExecutionId] = StepStatus.Rejected;
                    }

                    if (resumedDecisionType == DecisionType.RetryWithRevision &&
                        state.StepIdByExecutionId.TryGetValue(resumedExecutionId, out var retryStepId))
                    {
                        state.ConsecutiveFailureCountByStepId[retryStepId] = 0;
                        state.LatestFailureClassificationByStepId[retryStepId] = null;
                        state.LatestFailureReasonByStepId[retryStepId] = null;
                        state.LatestExecutionFailedRetryNotBeforeByStepId[retryStepId] = null;
                        state.LatestCapturedResponseFileByStepId[retryStepId] = null;
                        state.LatestUnsatisfiedOutputNamesByStepId[retryStepId] = null;
                        state.RetryNotBeforeByStepId.Remove(retryStepId);
                        state.RetryDelayMsByStepId.Remove(retryStepId);
                        state.RetryScheduledForExecutionIdByStepId.Remove(retryStepId);

                        // #1586 S1: RetryWithRevision reopens the step regardless of whether it was
                        // foreclosed — the same never-permanent rule ExecutionRequestAccepted's own
                        // clear above enforces for the ordinary dispatch path.
                        state.RetryForeclosedStepIds.Remove(retryStepId);

                        if (supplementaryExecutionId is { } retrySupplement)
                        {
                            state.PendingSupplementaryExecutionIdByStepId[retryStepId] = retrySupplement;
                        }
                        else
                        {
                            state.PendingSupplementaryExecutionIdByStepId.Remove(retryStepId);
                        }
                    }

                    if (resumedDecisionType == DecisionType.Supersede &&
                        state.TargetStepIdByDecisionId.TryGetValue(resumed.DecisionId, out var supersedeTargetStepId))
                    {
                        state.PendingSupersedeTargetStepIds.Add(supersedeTargetStepId);

                        if (supplementaryExecutionId is { } supersedeSupplement)
                        {
                            state.PendingSupplementaryExecutionIdByStepId[supersedeTargetStepId] = supersedeSupplement;
                        }
                    }
                }

                break;

            case FlowEvent.StepRetryScheduled retryScheduled:
                state.RetryNotBeforeByStepId[retryScheduled.StepId] = retryScheduled.RetryNotBefore;
                state.RetryDelayMsByStepId[retryScheduled.StepId] = retryScheduled.RetryDelayMs;
                state.RetryScheduledForExecutionIdByStepId[retryScheduled.StepId] = retryScheduled.ForExecutionId;
                break;

            case FlowEvent.CancellationRequested cancellationRequested:
                state.CancellationRequestedExecutionIds.Add(cancellationRequested.ExecutionId);
                break;

            case FlowEvent.StepRetryForeclosed foreclosed:
                // #1586 S1: all-or-nothing, the same discipline ExecutionCancelled's own retry-field
                // clear already follows (#1605) — guarded on ForExecutionId still matching the
                // scheduled retry this step carries now (FlowEvent.StepRetryForeclosed.ForExecutionId's
                // own remarks explain why a stale name must be a no-op). Applying the flag while
                // skipping the field clear (or the reverse) would leave RetryNotBefore set AND
                // MayRetry false at once — DeriveWorkflowStatus's deliverability predicate ORs the two
                // (`step.RetryNotBefore is not null` / MayRetry), so a half-applied foreclosure can
                // neither terminate nor retry.
                //
                // #1877: a second arm for the administrative foreclosure `baton resolve --close`
                // records against an already-rejected capture
                // (Mutation.MutationInterface.RecordCaptureResolutionAsync), which has no scheduled
                // retry to name. FlowEvent.StepRetryForeclosed.ForExecutionId's own remarks are the
                // register for both arms and for why a stale name no-ops under either.
                var scheduledForStep = state.RetryScheduledForExecutionIdByStepId.TryGetValue(foreclosed.StepId, out var scheduledExecutionId)
                    ? scheduledExecutionId
                    : (ExecutionId?)null;
                var foreclosesLatestUnscheduled = scheduledForStep is null
                    && state.LatestExecutionIdByStepId.GetValueOrDefault(foreclosed.StepId) == foreclosed.ForExecutionId;
                if (scheduledForStep == foreclosed.ForExecutionId || foreclosesLatestUnscheduled)
                {
                    state.RetryForeclosedStepIds.Add(foreclosed.StepId);
                    state.RetryNotBeforeByStepId.Remove(foreclosed.StepId);
                    state.RetryDelayMsByStepId.Remove(foreclosed.StepId);
                    state.RetryScheduledForExecutionIdByStepId.Remove(foreclosed.StepId);
                }

                break;

            case FlowEvent.VerifyStarted verifyStarted:
                state.UnmatchedVerifyExecutionIds.Add(verifyStarted.ExecutionId);
                break;

            case FlowEvent.VerifyPassed verifyPassed:
                state.UnmatchedVerifyExecutionIds.Remove(verifyPassed.ExecutionId);
                break;

            case FlowEvent.VerifyFailed verifyFailed:
                state.UnmatchedVerifyExecutionIds.Remove(verifyFailed.ExecutionId);
                ApplyIndeterminate(state, verifyFailed.ExecutionId, DescribeVerifyFailure(verifyFailed), IndeterminateProducer.VerifyFailed, verifyFailed.Tail);
                break;

            case FlowEvent.VerifyNotRun verifyNotRun:
                // #1796: the BuildLockBusy shape DID start a real verify run (VerifyStarted fired), so
                // it must clear UnmatchedVerifyExecutionIds the same way VerifyFailed/VerifyPassed do
                // below, and it settles Indeterminate -- "nothing verified this run" is not a fact a
                // room may report as Succeeded. This is the one branch of this event that ever calls
                // ApplyIndeterminate; the pre-flight shape below it never does.
                if (verifyNotRun.BuildLockBusy)
                {
                    state.UnmatchedVerifyExecutionIds.Remove(verifyNotRun.ExecutionId);
                    ApplyIndeterminate(
                        state, verifyNotRun.ExecutionId,
                        $"{verifyNotRun.Reason} — awaiting conductor resolution.",
                        IndeterminateProducer.BuildLockBusy);
                    break;
                }

                // #1702: diagnostic only, same shape as VerifyStarted/VerifyPassed above -- no
                // ApplyIndeterminate call. The execution's own already-recorded classification (this
                // event only appends when that classification was Succeeded) decides StepStatus and
                // WorkflowOutcome unassisted; this only records WHY the step ran unverified.
                // #1788: FIRST reason wins, via TryAdd rather than an unconditional overwrite -- the
                // engine-run gate's own not-run (if any) always ran first and is the more actionable
                // diagnostic, and a second, orthogonal not-run from the post-exit delivery check
                // (Mutation.DeliveryVerifier) for the SAME execution must not silently erase it. Both
                // events still land in flow.jsonl regardless; this only decides which reason the
                // projected StepState surfaces.
                if (state.StepIdByExecutionId.TryGetValue(verifyNotRun.ExecutionId, out var notRunStepId))
                {
                    state.VerifyNotRunReasonByStepId.TryAdd(notRunStepId, verifyNotRun.Reason);
                }

                break;

            case FlowEvent.ExecutionArrested arrested:
                state.UnmatchedVerifyExecutionIds.Remove(arrested.ExecutionId);
                ApplyIndeterminate(state, arrested.ExecutionId, DescribeArrest(arrested), IndeterminateProducer.Arrested);
                break;

            case FlowEvent.StepRebound rebound:
                // Overrides the frozen Adapter/Model on the accepted request so the rebind survives
                // replay (spec/baton.md §3, #802 section 3.3's own stated reason for freezing the value
                // into the event in the first place — a full replay must recover it without re-deriving
                // from bindings.json). No StepState/FlowState consequence otherwise: this does not
                // affect step lifecycle.
                if (state.AcceptedRequestByExecutionId.TryGetValue(rebound.ForExecutionId, out var reboundRequest))
                {
                    state.AcceptedRequestByExecutionId[rebound.ForExecutionId] =
                        reboundRequest with { Adapter = rebound.NewAdapter, Model = rebound.NewModel };
                }

                break;

            case FlowEvent.EngineFilesPlaced enginePlaced:
                // #1933: NOT diagnostic-only, unlike the arm below -- this event used to sit in it. The
                // crash-recovery classification rebuilds its CoreDispatchResult from a recorded exit, so
                // this projection is the only place the paths AER itself wrote can come back from (see
                // FlowEvent.EngineFilesPlaced's own remarks). It still changes no StepState: it is
                // per-execution evidence a classifier reads, the same shape
                // WorkspaceHeadShaAtStartByExecutionId above already has.
                state.EnginePlacedPathsByExecutionId[enginePlaced.ExecutionId] = [.. enginePlaced.Paths];
                break;

            case FlowEvent.ExecutionRequestRejected:
            case FlowEvent.ZeroOutputsDespiteSubstantialWork:
            case FlowEvent.VerifyDeclarationIgnored:
            case FlowEvent.VerifyDeclarationUnreviewed:
            case FlowEvent.ExecutionProgress:
            case FlowEvent.CancellationDelivered:
            case FlowEvent.CancellationRejected:
            case FlowEvent.DeliveryPrOpened:
            case FlowEvent.DeliveryChecksGreen:
            case FlowEvent.DeliveryChecksRed:
            case FlowEvent.DeliveryMerged:
            case FlowEvent.StreamLogLossDeclared:
                // Diagnostic-only facts: durable in the ledger, but no StepState/FlowState consequence.
                // The two VerifyDeclaration* events are listed here on purpose rather than by falling off
                // the end of this switch -- see their own docs for why they stay reader-less (#1708 H1/M1).
                // The three #1549 events (progress heartbeat, cancellation delivered/rejected) are the
                // same shape: durable operator/observability facts that never change what a step's own
                // state projects to. The four #734 delivery events are the same shape once more, proven
                // the same interleaved-baseline way in this assembly's own test suite (spec/baton.md §2).
                // #1885's StreamLogLossDeclared joins them: it is read by ExecutionUsageProjector off the
                // raw ledger entries, never off this projection, so a stream-log gap has no bearing on
                // whether a step succeeded, failed, or may retry.
                break;

            case FlowEvent.ExecutionIndeterminate indeterminate:
                // #1608: projects to StepStatus.Failed, same as FlowEvent.ExecutionFailed — the
                // "single added enum value" ruling adds Indeterminate at the room-level word only
                // (WorkflowOutcome.DescribeTerminal, below), never at StepStatus. What actually
                // distinguishes this from an ordinary Failed step is IndeterminateAwaitingResolutionStepIds.
                state.TerminalStatusByExecutionId[indeterminate.ExecutionId] = StepStatus.Failed;
                if (state.StepIdByExecutionId.TryGetValue(indeterminate.ExecutionId, out var indeterminateStepId))
                {
                    state.ConsecutiveFailureCountByStepId[indeterminateStepId] =
                        state.ConsecutiveFailureCountByStepId.GetValueOrDefault(indeterminateStepId) + 1;
                    state.LatestFailureClassificationByStepId[indeterminateStepId] = null;
                    state.LatestFailureReasonByStepId[indeterminateStepId] = indeterminate.Reason;
                    state.LatestExecutionFailedRetryNotBeforeByStepId[indeterminateStepId] = null;
                    state.LatestCapturedResponseFileByStepId[indeterminateStepId] = indeterminate.CapturedResponseFile;
                    state.LatestUnsatisfiedOutputNamesByStepId[indeterminateStepId] =
                        indeterminate.UnsatisfiedOutputNames is null ? null : new List<string>(indeterminate.UnsatisfiedOutputNames);
                    state.IndeterminateAwaitingResolutionStepIds.Add(indeterminateStepId);

                    // F1 (#1593 review): the discriminant baton resolve's admission test reads.
                    // spec/baton.md §3's producer table explains the CapturedResponse/ContractFailure
                    // split.
                    state.IndeterminateProducerByStepId[indeterminateStepId] = indeterminate.CapturedResponseFile is not null
                        ? IndeterminateProducer.CapturedResponse
                        : IndeterminateProducer.ContractFailure;

                    // Neither arm here is VerifyFailed, so a tail recorded by an earlier VerifyFailed
                    // producer on this step must not survive being overwritten — same discipline as
                    // the other clear sites (the ExecutionRequestAccepted and CaptureResolved arms).
                    state.IndeterminateVerifyTailByStepId.Remove(indeterminateStepId);
                }

                break;

            case FlowEvent.CaptureResolved resolved:
                // Guarded on StepId matching the event's own recorded target, the same discipline
                // FlowEvent.StepRetryForeclosed's ForExecutionId guard already follows — a stale
                // resolution (replayed against a step a later fresh dispatch has since moved past)
                // must be a no-op, not a misapplication to whichever execution the id now maps to.
                if (state.StepIdByExecutionId.TryGetValue(resolved.ExecutionId, out var resolvedStepId)
                    && resolvedStepId == resolved.StepId)
                {
                    var resolvedProducer = state.IndeterminateProducerByStepId.GetValueOrDefault(resolvedStepId);
                    state.IndeterminateAwaitingResolutionStepIds.Remove(resolvedStepId);
                    state.IndeterminateReasonByStepId.Remove(resolvedStepId);
                    state.IndeterminateProducerByStepId.Remove(resolvedStepId);
                    state.IndeterminateVerifyTailByStepId.Remove(resolvedStepId);

                    if (resolved.Accepted)
                    {
                        // #1608 review finding 5: this event is journaled BEFORE the real output
                        // file(s) it describes (MutationInterface.RecordCaptureResolutionAsync) — the
                        // opposite of ExecutionSucceeded's own clear below, which only ever records a
                        // write already durable on disk. A replay can therefore project Succeeded here
                        // for a file that is not (yet, or ever) actually on disk; that gap is what
                        // RecordCaptureResolutionAsync's own repair path (ReconcileAcceptedCaptureAsync)
                        // exists to close on a later matching --execution, not something this pure
                        // projection can see or correct.
                        state.TerminalStatusByExecutionId[resolved.ExecutionId] = StepStatus.Succeeded;
                        state.ConsecutiveFailureCountByStepId[resolvedStepId] = 0;
                        state.LatestFailureClassificationByStepId[resolvedStepId] = null;
                        state.LatestFailureReasonByStepId[resolvedStepId] = null;
                        state.LatestCapturedResponseFileByStepId[resolvedStepId] = null;
                        state.LatestUnsatisfiedOutputNamesByStepId[resolvedStepId] = null;
                    }
                    else
                    {
                        // F8 (#1593 review): forecloses retry on a reject, the same way #1623's
                        // VerifyFailed/Arrested producers already foreclose unconditionally in
                        // ApplyIndeterminate -- otherwise CaptureResolved(Accepted: false) alone would
                        // leave the step retry-eligible again on the very next pump.
                        //
                        // #1877 (ruling): EVERY producer, not just ContractFailure/null. What the old
                        // narrower arm left behind, the room it was measured on, and how an operator
                        // asks for a retry now, all live in spec/baton.md §3's settle-shape table --
                        // the register for this rule, not restated here.
                        state.RetryForeclosedStepIds.Add(resolvedStepId);
                    }

                    if (!resolved.Accepted)
                    {
                        // #1622 (c)/(d): see spec/baton.md §3 (the "Both --reject and --close clear..."
                        // paragraph) for why this rewrite happens and for which producers it applies to.
                        var priorReason = state.LatestFailureReasonByStepId.GetValueOrDefault(resolvedStepId);
                        state.LatestFailureReasonByStepId[resolvedStepId] =
                            BuildConductorResolvedReason(priorReason, resolved.Reason);
                        state.ResolvedByConductorStepIds.Add(resolvedStepId);

                        // F11 (#1720 review, conductor ruling): WHICH verb, discriminated here and
                        // nowhere else -- the producer is cleared four lines above, so nothing
                        // downstream can still tell the two apart. Read off the admission table
                        // (Cli.ResolveCommand, Mutation.MutationInterface, and spec/baton.md §3's
                        // settle-shape table, which is where the reasoning lives).
                        if (resolvedProducer is IndeterminateProducer.CapturedResponse or IndeterminateProducer.ContractFailure)
                        {
                            state.ConductorRejectedStepIds.Add(resolvedStepId);
                        }
                    }

                    // Rejected: Status stays Failed, LatestCapturedResponseFile/UnsatisfiedOutputNames
                    // stay recorded (the audit trail of what was captured and refused) — clearing
                    // IndeterminateAwaitingResolutionStepIds above is what lets
                    // WorkflowOutcome.DescribeTerminal read this as an ordinary Failed step again, and
                    // (#1877) the foreclosure above is what keeps RetryEngine.MayRetry false for every
                    // producer, so DeriveWorkflowStatus settles the room Terminal rather than Running.
                }

                break;
        }
    }

    /// <summary>
    /// #1623: shared apply for the two verify-side Indeterminate producers
    /// (<see cref="FlowEvent.VerifyFailed"/>, <see cref="FlowEvent.ExecutionArrested"/>) — settles the
    /// execution's terminal status as <see cref="StepStatus.Failed"/> (so
    /// <see cref="DeriveWorkflowStatus"/>'s existing deliverability predicate reaches Terminal the same
    /// way any other failure does), raises the one Indeterminate flag
    /// (<see cref="ProjectionCheckpointState.IndeterminateAwaitingResolutionStepIds"/> — the same flag
    /// #1608's <see cref="FlowEvent.ExecutionIndeterminate"/> arm raises, so all three producers reach
    /// <see cref="Status.WorkflowOutcome.DescribeTerminal"/> and
    /// <see cref="Scheduling.RetryEngine.MayRetry"/> through one predicate rather than two parallel
    /// ones), records <paramref name="reason"/> alongside it as diagnostic text, and forecloses retry.
    /// <see cref="Scheduling.RetryEngine.MayRetry"/> refuses on the flag directly, not merely on the
    /// foreclosure side effect, per the ruling's "retry-ineligible by an explicit arm, not an accident
    /// of a default."
    /// <para>
    /// Deliberately leaves <see cref="ProjectionCheckpointState.LatestCapturedResponseFileByStepId"/>
    /// untouched: <c>baton resolve</c> discriminates its capture-resolution targets on that file, not
    /// on the Indeterminate flag, so a verify-failed step is Indeterminate without becoming a
    /// capture-resolution target (<c>Mutation.MutationInterface.RecordCaptureResolutionAsync</c>).
    /// </para>
    /// </summary>
    private static void ApplyIndeterminate(
        ProjectionCheckpointState state, ExecutionId executionId, string reason, IndeterminateProducer producer,
        string? verifyTail = null)
    {
        state.TerminalStatusByExecutionId[executionId] = StepStatus.Failed;
        if (!state.StepIdByExecutionId.TryGetValue(executionId, out var stepId))
        {
            return;
        }

        state.ConsecutiveFailureCountByStepId[stepId] = state.ConsecutiveFailureCountByStepId.GetValueOrDefault(stepId) + 1;
        state.LatestFailureClassificationByStepId[stepId] = FailureClassification.Permanent;
        state.LatestFailureReasonByStepId[stepId] = reason;
        state.IndeterminateAwaitingResolutionStepIds.Add(stepId);
        state.IndeterminateReasonByStepId[stepId] = reason;
        state.IndeterminateProducerByStepId[stepId] = producer;
        // #1701: null (never fabricated) for every producer but VerifyFailed -- an arrest's `reason`
        // above is already the full diagnostic, and the other two producers carry their own account
        // on LatestCapturedResponseFileByStepId/LatestUnsatisfiedOutputNamesByStepId instead.
        state.IndeterminateVerifyTailByStepId[stepId] = verifyTail;
        state.RetryForeclosedStepIds.Add(stepId);
        state.RetryNotBeforeByStepId.Remove(stepId);
        state.RetryDelayMsByStepId.Remove(stepId);
        state.RetryScheduledForExecutionIdByStepId.Remove(stepId);
    }

    /// <summary>
    /// #1622 (c)/(d): see spec/baton.md §3 for why this rewrite exists. Strips the trailing "awaiting
    /// conductor resolution." clause every Indeterminate-producing reason above ends with (<see
    /// cref="ApplyIndeterminate"/>'s <paramref name="priorReason"/> arm above, and the #1608
    /// captured-response arm in <c>Outcomes.OutcomeClassifier</c>). A prior reason that does not end
    /// with the marker (an older ledger line, or a future producer that phrases it differently) still
    /// gets the resolution clause appended, never silently dropped.
    /// </summary>
    private static string BuildConductorResolvedReason(string? priorReason, string? conductorReason)
    {
        const string awaitingMarker = "awaiting conductor resolution.";
        var resolutionClause = string.IsNullOrWhiteSpace(conductorReason)
            ? "Resolved by the conductor."
            : $"Resolved by the conductor: {conductorReason}";

        if (string.IsNullOrWhiteSpace(priorReason))
        {
            return resolutionClause;
        }

        var trimmed = priorReason.TrimEnd();
        var withoutMarker = trimmed.EndsWith(awaitingMarker, StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^awaitingMarker.Length].TrimEnd()
            : trimmed;

        return $"{withoutMarker} {resolutionClause}";
    }

    private static string DescribeVerifyFailure(FlowEvent.VerifyFailed verifyFailed)
    {
        return verifyFailed.Kind switch
        {
            VerifyFailedKind.EngineRestart => "Verify did not complete across an engine restart — awaiting conductor resolution.",
            VerifyFailedKind.TimedOut => "Verify timed out — awaiting conductor resolution.",
            VerifyFailedKind.Cancelled => "Verify cancelled — awaiting conductor resolution.",
            _ => verifyFailed.FailingMembers is { Count: > 0 }
                ? $"Verify failed ({string.Join(", ", verifyFailed.FailingMembers)}) — awaiting conductor resolution."
                : "Verify failed — awaiting conductor resolution.",
        };
    }

    private static string DescribeArrest(FlowEvent.ExecutionArrested arrested)
    {
        // #1682/#1691: total over the three known producers (spec/baton.md §3). Pre-#1682 ledger lines
        // carry no Reason, so null falls into the TokenBudget arm rather than a fabricated third case.
        // StateProjectorTests.ExecutionArrested_DescribeArrest_covers_every_ArrestReason_member pins
        // this switch total against Enum.GetValues<ArrestReason>() so a new member fails a gate rather
        // than the throwing default arm below in production.
        return arrested.Reason switch
        {
            ArrestReason.ToolStepCap => arrested.ToolStepCount is { } steps and > 0
                ? $"Execution arrested: tool-step cap exceeded ({steps} tool steps measured) — awaiting conductor resolution."
                : "Execution arrested: tool-step cap exceeded — awaiting conductor resolution.",
            ArrestReason.BilledRate => DescribeBilledRateArrest(arrested),
            ArrestReason.TokenBudget or null => DescribeTokenBudgetArrest(arrested),
            _ => throw new ArgumentOutOfRangeException(nameof(arrested), arrested.Reason, "Unknown ArrestReason."),
        };
    }

    private static string DescribeBilledRateArrest(FlowEvent.ExecutionArrested arrested)
    {
        // #1691: names all three quantities a conductor needs to tell a false fire from a real one --
        // the window's width, the rate observed inside it, and the limit that was armed. The window is
        // read off TokenBudgetMonitor rather than restated, so it cannot drift from the code measuring
        // it. Degrades to the bare sentence when a ledger line carries neither figure (only possible on
        // a line written by an older writer, since this reason did not exist before #1691).
        // Fully qualified, and a compile-time constant width -- NOT a clock read: this projector's own
        // "no wall-clock time" purity contract is untouched.
        var window = $"{Mutation.TokenBudgetMonitor.BilledRateWindow.TotalMinutes:0.##} min";
        if (arrested.PeakBilledInWindow is { } observed && arrested.BilledRateLimit is { } limit)
        {
            return $"Execution arrested: billed-token rate limit exceeded ({observed} billed tokens in a {window} window, limit {limit}) — awaiting conductor resolution.";
        }

        return $"Execution arrested: billed-token rate limit exceeded (over a {window} window) — awaiting conductor resolution.";
    }

    private static string DescribeTokenBudgetArrest(FlowEvent.ExecutionArrested arrested)
    {
        // #1682: BilledTokens (Σ input + Σ output [+ Σ cache_creation]) is what the budget actually
        // arrests on now -- ContextLevelTokens + TokensOut was #1623's "not shown reachable" reading,
        // replaced wholesale rather than kept as a fallback (spec/baton.md §3 states the arithmetic).
        // A pre-#1682 ledger line's WorkerUsage never set BilledTokens, so this falls back to the old
        // ContextLevelTokens + TokensOut reading for that legacy case only.
        var billed = arrested.Usage?.BilledTokens
            ?? (arrested.Usage?.ContextLevelTokens ?? arrested.Usage?.TokensIn ?? 0) + (arrested.Usage?.TokensOut ?? 0);
        // #1686 review F8: a null Reason is a pre-#1682 ledger line, which never computed BilledTokens
        // at all -- the figure above is the OLD level-based reading, not billed tokens, so the legacy
        // arm must not claim it is. A real TokenBudget arrest (post-#1682) always has BilledTokens set
        // and keeps the accurate wording.
        var figureLabel = arrested.Reason is null ? "tokens" : "billed tokens";
        // #1706: on claude the live figure is a LOWER BOUND, not a measurement -- the vendor's
        // mid-stream usage carries no real input or output count anywhere (ClaudeUsageParser
        // .TryParseIncrementalUsage has the measurement). Saying "measured" there would assert
        // something the stream cannot support, and the direction matters to whoever reads this: the
        // real spend is at least this, never at most. A pre-#1682 ledger line carries the flag's
        // default (false) and keeps the wording it always had.
        var figureVerb = arrested.Usage?.BilledIsFloor == true ? "measured as a floor — the real spend is at least this" : "measured";
        // #1745: names the adapter whose (possibly per-adapter) budget applied -- see
        // FlowEvent.ExecutionArrested.Adapter's own remarks for when it is null.
        var adapterClause = arrested.Adapter is { Length: > 0 } adapter ? $" on adapter '{adapter}'" : string.Empty;
        return billed > 0
            ? $"Execution arrested: token budget exceeded ({billed} {figureLabel} {figureVerb}){adapterClause} — awaiting conductor resolution."
            : $"Execution arrested: token budget exceeded{adapterClause} — awaiting conductor resolution.";
    }

    private static FlowState DeriveFlowState(
        ProjectionCheckpointState state,
        WorkflowDefinitionSnapshot snapshot)
    {
        var steps = new List<StepState>(snapshot.Steps.Count);
        foreach (var stepDefinition in snapshot.Steps)
        {
            if (!state.LatestExecutionIdByStepId.TryGetValue(stepDefinition.StepId, out var latestExecutionId))
            {
                steps.Add(new StepState(
                    stepDefinition.StepId,
                    StepStatus.Pending,
                    LatestExecutionId: null,
                    UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>(),
                    ExecutionCount: state.ExecutionCountByStepId.GetValueOrDefault(stepDefinition.StepId)));
                continue;
            }

            var rawStatus = state.TerminalStatusByExecutionId.GetValueOrDefault(latestExecutionId, StepStatus.Running);
            var isPaused = state.PausedExecutionIds.Contains(latestExecutionId);
            var status = isPaused ? StepStatus.Paused : rawStatus;

            var upstreamExecs = state.UpstreamExecutionIdsByStepId.TryGetValue(stepDefinition.StepId, out var dict)
                ? (IReadOnlyDictionary<StepId, ExecutionId>)dict
                : new Dictionary<StepId, ExecutionId>();

            // #1359: the latest attempt's own recorded request, not a separate tracking dict — the
            // same source AcceptedRequestByExecutionId already is for every other request-carried
            // fact (contract reconstruction, GrantAuditMode replay).
            var linkedFromExecutionId = state.AcceptedRequestByExecutionId.TryGetValue(latestExecutionId, out var latestRequest)
                ? latestRequest.LinkedFromExecutionId
                : null;

            steps.Add(new StepState(
                stepDefinition.StepId,
                status,
                latestExecutionId,
                upstreamExecs,
                state.ConsecutiveFailureCountByStepId.GetValueOrDefault(stepDefinition.StepId),
                state.LatestFailureClassificationByStepId.GetValueOrDefault(stepDefinition.StepId),
                state.LatestFailureReasonByStepId.GetValueOrDefault(stepDefinition.StepId),
                state.EverPausedExecutionIds.Contains(latestExecutionId),
                isPaused ? rawStatus : null,
                state.PendingSupplementaryExecutionIdByStepId.TryGetValue(stepDefinition.StepId, out var pendingSupplement)
                    ? pendingSupplement
                    : null,
                state.PendingSupersedeTargetStepIds.Contains(stepDefinition.StepId),
                state.RetryNotBeforeByStepId.TryGetValue(stepDefinition.StepId, out var rnb) ? rnb : null,
                state.RetryDelayMsByStepId.TryGetValue(stepDefinition.StepId, out var rdm) ? rdm : null,
                state.RetryScheduledForExecutionIdByStepId.TryGetValue(stepDefinition.StepId, out var rfe) ? rfe : null,
                state.LatestExecutionFailedRetryNotBeforeByStepId.GetValueOrDefault(stepDefinition.StepId),
                linkedFromExecutionId,
                state.ExecutionCountByStepId.GetValueOrDefault(stepDefinition.StepId),
                state.LatestCapturedResponseFileByStepId.GetValueOrDefault(stepDefinition.StepId),
                state.LatestUnsatisfiedOutputNamesByStepId.GetValueOrDefault(stepDefinition.StepId),
                state.RetryForeclosedStepIds.Contains(stepDefinition.StepId),
                state.IndeterminateAwaitingResolutionStepIds.Contains(stepDefinition.StepId),
                state.IndeterminateReasonByStepId.GetValueOrDefault(stepDefinition.StepId),
                state.IndeterminateProducerByStepId.GetValueOrDefault(stepDefinition.StepId),
                state.IndeterminateVerifyTailByStepId.GetValueOrDefault(stepDefinition.StepId),
                state.ResolvedByConductorStepIds.Contains(stepDefinition.StepId),
                state.WorkspaceChangedByStepId.GetValueOrDefault(stepDefinition.StepId),
                state.HollowByStepId.GetValueOrDefault(stepDefinition.StepId),
                state.HollowReasonByStepId.GetValueOrDefault(stepDefinition.StepId),
                state.VerifyNotRunReasonByStepId.GetValueOrDefault(stepDefinition.StepId),
                state.ConductorRejectedStepIds.Contains(stepDefinition.StepId)));
        }

        var workflowStatus = DeriveWorkflowStatus(steps, snapshot);

        var pendingStepLessExecutions = state.StepLessExecutionsInOrder
            .Where(execution => !state.TerminalStatusByExecutionId.ContainsKey(execution.ExecutionId))
            .ToList();

        var unfulfilledCancellationRequestExecutionIds = state.CancellationRequestedExecutionIds
            .Where(executionId => !state.TerminalStatusByExecutionId.ContainsKey(executionId))
            .ToList();

        var unmatchedVerifyExecutionIds = state.UnmatchedVerifyExecutionIds
            .Where(executionId => !state.TerminalStatusByExecutionId.ContainsKey(executionId))
            .ToList();

        return new FlowState(
            snapshot.WorkflowDefinitionSnapshotId,
            steps,
            workflowStatus,
            pendingStepLessExecutions,
            unfulfilledCancellationRequestExecutionIds,
            unmatchedVerifyExecutionIds);
    }

    private static WorkflowStatus DeriveWorkflowStatus(
        IReadOnlyList<StepState> steps, WorkflowDefinitionSnapshot snapshot)
    {
        if (steps.Any(step => step.Status == StepStatus.Running))
        {
            return WorkflowStatus.Running;
        }

        if (steps.Any(step => step.Status == StepStatus.Paused))
        {
            return WorkflowStatus.Paused;
        }

        var stepById = steps.ToDictionary(step => step.StepId);
        var definitionById = snapshot.Steps.ToDictionary(definition => definition.StepId);

        if (steps.Any(step => step.IsPendingSupersedeTarget))
        {
            return WorkflowStatus.Running;
        }

        var deliverableByStepId = new Dictionary<StepId, bool>();
        bool CanStillDeliver(StepId stepId)
        {
            if (deliverableByStepId.TryGetValue(stepId, out var known))
            {
                return known;
            }

            deliverableByStepId[stepId] = false;
            var step = stepById[stepId];
            var eligible = step.Status == StepStatus.Succeeded
                || step.Status == StepStatus.Pending
                || step.RetryNotBefore is not null
                || (step.Status == StepStatus.Failed
                    && Scheduling.RetryEngine.MayRetry(step, definitionById[stepId].RetryPolicy))
                || step.PendingSupplementaryExecutionId is not null;
            var deliverable = eligible
                && (step.Status == StepStatus.Succeeded
                    || definitionById[stepId].DependsOn.All(CanStillDeliver));
            deliverableByStepId[stepId] = deliverable;
            return deliverable;
        }

        return steps.Any(step => step.Status != StepStatus.Succeeded && CanStillDeliver(step.StepId))
            ? WorkflowStatus.Running
            : WorkflowStatus.Terminal;
    }
}
