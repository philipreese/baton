using System.Diagnostics;
using Baton.Artifacts;
using Baton.Concurrency;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Outcomes;
using Baton.Projection;
using Baton.Scheduling;
using Baton.Status;
using Baton.Store;

namespace Baton.Mutation;

/// <summary>
/// The single external entry point for all Flow state mutation — no other code path may
/// append to <c>flow.jsonl</c>. <see cref="StartWorkflowAsync"/> is the "pump" design decided on: it
/// blocks until the workflow reaches a fixed point. From M8 Phase 3 on, every step ready in a given
/// scheduling round dispatches concurrently rather than one at a time — a diamond's B and C run
/// simultaneously, and a slow step never delays unrelated ready work.
/// </summary>
public static class MutationInterface
{
    // #1183: the longest single Task.Delay the deferral waits below will ever issue, however far out
    // the deadline they are waiting on actually is -- distinct from MaxExhaustionParkHorizon (the
    // longest reset instant GetRetryObligations will trust), since a change to one must not silently
    // move the other. Task.Delay's TimeSpan overload throws past ~49.7 days; the loop's `continue`
    // after each wait re-checks readiness and re-issues the remainder, so any value safely under that
    // ceiling works here.
    private static readonly TimeSpan MaxParkWaitChunk = TimeSpan.FromDays(1);

    /// <summary>
    /// Acquires the room's concurrency guard, then repeatedly projects <see cref="FlowState"/>,
    /// resolves every ready step (retry-aware), and dispatches all of them to Core
    /// concurrently. Each completion (<c>Task.WhenAny</c>) triggers a fresh round — re-projecting
    /// and dispatching any newly-ready work — while the rest stay in flight. Returns once nothing is
    /// ready and nothing remains in flight.
    /// </summary>
    /// <param name="inFlightExecutions">
    /// M10 Phase 2's live-cancellation delivery point: populated with every
    /// process-bound dispatch this call has in flight, so a caller retaining this instance can
    /// cancel one of them via <see cref="InFlightExecutionRegistry.RequestCancellationAsync"/> while
    /// this call is still running — the only way a live execution is reachable at all, since the
    /// concurrency guard blocks any second mutation-surface call for the same room until this one returns.
    /// Defaults to a fresh, unshared instance when the caller has no need to interact with it.
    /// </param>
    /// <param name="cancellationToken">
    /// A host-initiated stop: when cancelled, every execution this call currently has in flight
    /// gets a <see cref="FlowEvent.CancellationRequested"/> recorded and fsync'd, then is signalled —
    /// never the reverse, and never signalled directly without a recorded intent first.
    /// </param>
    /// <exception cref="WorkflowLockedException">
    /// Another Flow instance already holds <paramref name="roomDirectoryPath"/>'s lock.
    /// </exception>
    public static async Task<FlowState> StartWorkflowAsync(
        WorkflowId workflowId,
        string roomDirectoryPath,
        WorkflowDefinitionSnapshot snapshot,
        IReadOnlyDictionary<string, WorkerBinding> workerBindings,
        string artifactsRootPath,
        IEventLogReader eventLogReader,
        IEventLogWriter eventLogWriter,
        ICoreDispatcher dispatcher,
        InFlightExecutionRegistry? inFlightExecutions = null,
        CancellationToken cancellationToken = default,
        TimeProvider? timeProvider = null,
        Func<double>? jitterSource = null,
        string? holderDescription = null,
        // #1094: fired once when the pump enters a paced wait on a vendor-quota (ExhaustedUntil) park,
        // with the local-time-resolvable reset instant. The foreground CLI prints it so a day-long
        // quota wait never reads as a hang; null (the daemon/default) stays silent. Never touches the
        // 0026 wait itself — surfacing only.
        Action<DateTimeOffset>? onVendorQuotaPark = null,
        // #1184 / 0026 §4: when true (attended session turn), an ExhaustedUntil step settles immediately
        // rather than scheduling a paced retry obligation. Defaults to false (unattended workflow steps).
        bool settleOnVendorExhaustion = false,
        // #1767: test-observable only, null in every production call site. Fired each time the pump
        // re-reads the clock and re-arms a deferral wait (either the idle-deferral branch or the
        // busy-wait branch below) — never on any other path, and never changes production timing
        // itself, since it fires after the delay task is already constructed. Mirrors
        // CancelRequestPoller's per-tick shape: a plain callback, absent cost when null.
        Action? onDeferralWaitArmed = null,
        // #802: resolved via WorkerBindingResolver.ResolveFallbacks -- one entry per worker role that
        // declares a FallbackOnExhaustion, already resolved through the same adapter/ceiling gates as
        // workerBindings. Null (every caller that hasn't been updated to build one) behaves exactly
        // like today: a quota-parked step waits out the vendor's own reset instant.
        IReadOnlyDictionary<string, WorkerBinding>? fallbackWorkerBindings = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(workerBindings);
        ArgumentException.ThrowIfNullOrEmpty(artifactsRootPath);
        ArgumentNullException.ThrowIfNull(eventLogReader);
        ArgumentNullException.ThrowIfNull(eventLogWriter);
        ArgumentNullException.ThrowIfNull(dispatcher);

        using var guard = ConcurrencyGuard.Acquire(roomDirectoryPath, holderDescription);

        return await PumpToFixedPointAsync(
                workflowId, roomDirectoryPath, snapshot, workerBindings, artifactsRootPath, eventLogReader, eventLogWriter, dispatcher,
                inFlightExecutions ?? new InFlightExecutionRegistry(), cancellationToken,
                timeProvider ?? TimeProvider.System, jitterSource ?? (() => Random.Shared.NextDouble()), onVendorQuotaPark, settleOnVendorExhaustion,
                onDeferralWaitArmed, fallbackWorkerBindings)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// A second mutation-surface entry point: records an external decision
    /// against a currently paused execution, resumes the workflow, and drives the consequences to
    /// the next fixed point through the same pump <see cref="StartWorkflowAsync"/> uses. Validates
    /// every <see cref="DecisionType"/> against projected state (the closed-set rules) before
    /// appending anything — an invalid decision throws and leaves the log untouched.
    /// </summary>
    /// <exception cref="WorkflowLockedException">
    /// Another Flow instance still held <paramref name="roomDirectoryPath"/>'s lock after
    /// <see cref="RoutineHoldBudget"/> elapsed. #1650 F1: bounded rather than fail-fast, unlike
    /// <see cref="StartWorkflowAsync"/>'s guard — a decision is the operator-facing half of a
    /// <c>run --wait</c> handoff, and the holder it normally loses to is that same run's pump in the
    /// act of releasing. Failing fast here turns the routine tail into a refusal the operator can
    /// only answer by retrying the identical command. A second live pump mid-step still refuses.
    /// </exception>
    /// <exception cref="InvalidExternalDecisionException">The decision violates one of the validation rules.</exception>
    public static async Task<FlowState> RecordDecisionAsync(
        WorkflowId workflowId,
        string roomDirectoryPath,
        WorkflowDefinitionSnapshot snapshot,
        IReadOnlyDictionary<string, WorkerBinding> workerBindings,
        string artifactsRootPath,
        IEventLogReader eventLogReader,
        IEventLogWriter eventLogWriter,
        ICoreDispatcher dispatcher,
        ExecutionId referencedExecutionId,
        DecisionType decisionType,
        StepId? targetStepId = null,
        ExecutionId? supplementaryExecutionId = null,
        InFlightExecutionRegistry? inFlightExecutions = null,
        CancellationToken cancellationToken = default,
        TimeProvider? timeProvider = null,
        Func<double>? jitterSource = null,
        string? holderDescription = null,
        bool settleOnVendorExhaustion = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(workerBindings);
        ArgumentException.ThrowIfNullOrEmpty(artifactsRootPath);
        ArgumentNullException.ThrowIfNull(eventLogReader);
        ArgumentNullException.ThrowIfNull(eventLogWriter);
        ArgumentNullException.ThrowIfNull(dispatcher);

        // #1650 F1: AcquireWithin, not the fail-fast Acquire every other entry point here takes —
        // see this method's own <exception> doc and RoutineHoldBudget for why the decision path is
        // the one that must absorb a routine overlap rather than refuse it.
        using var guard = ConcurrencyGuard.AcquireWithin(roomDirectoryPath, RoutineHoldBudget.Duration, holderDescription);

        var checkpoint = ProjectionCheckpointStore.Load(roomDirectoryPath);
        var log = await eventLogReader.ReadSnapshotFromOffsetAsync(checkpoint?.ByteOffset ?? 0, cancellationToken).ConfigureAwait(false);
        if (log.IsFallbackToFull)
        {
            checkpoint = null;
        }
        var (state, latestCheckpoint) = StateProjector.ProjectAndCheckpoint(log.FlowEvents, snapshot, checkpoint, log.ByteOffset);
        var succeededExecutionIds = latestCheckpoint.State.SucceededExecutionIds;

        ExternalDecisionValidator.Validate(
            state, snapshot, succeededExecutionIds, referencedExecutionId, decisionType, targetStepId, supplementaryExecutionId,
            roomDirectoryPath);

        var decisionId = new DecisionId(Guid.NewGuid().ToString("n"));

        // Both fsync'd — lifecycle events, same write-sequence discipline as any other append.
        await eventLogWriter.AppendAsync(
                new FlowEvent.ExternalDecisionRecorded(
                    decisionId, referencedExecutionId, decisionType, targetStepId, supplementaryExecutionId),
                cancellationToken)
            .ConfigureAwait(false);
        await eventLogWriter.AppendAsync(new FlowEvent.WorkflowResumed(decisionId), cancellationToken).ConfigureAwait(false);

        return await PumpToFixedPointAsync(
                workflowId, roomDirectoryPath, snapshot, workerBindings, artifactsRootPath, eventLogReader, eventLogWriter, dispatcher,
                inFlightExecutions ?? new InFlightExecutionRegistry(), cancellationToken,
                timeProvider ?? TimeProvider.System, jitterSource ?? (() => Random.Shared.NextDouble()),
                settleOnVendorExhaustion: settleOnVendorExhaustion)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// #1608: the conductor resolution surface — <c>baton resolve</c>'s own mutation-surface entry
    /// point; see <see cref="FlowEvent.CaptureResolved"/>'s own remarks for the exclusivity claim this
    /// enforces. Unlike every other entry point above, this never pumps.
    /// <para>
    /// A step with no declared <see cref="PausePoint"/> keeps an unresolved
    /// <see cref="FlowEvent.ExecutionIndeterminate"/> entirely unreachable from <c>baton decide</c>
    /// (<see cref="ExternalDecisionValidator"/> only ever admits a Paused step, or a step parked on a
    /// pending retry deadline), so nothing downstream is waiting on this call the way a paused workflow
    /// waits on <see cref="RecordDecisionAsync"/>. A step that DOES declare <see cref="PausePoint"/> is
    /// the exception: <see cref="Scheduling.PauseEngine.GetPauseObligations"/> reaches it through the
    /// ordinary <c>Failed &amp;&amp; !MayRetry</c> path regardless of why retry is refused, so it becomes
    /// <see cref="StepStatus.Paused"/> with <see cref="StepState.IndeterminateAwaitingResolution"/>
    /// still set — and IS reachable from <c>baton decide</c> in that state (#1608 review finding 3).
    /// This verb does not special-case that step; a conductor deciding not to resolve first gets
    /// whatever ordinary pause-decision consequence follows, still carrying the unresolved flag.
    /// </para>
    /// #1877: a rejection no longer leaves the step retry-eligible — it forecloses retry for every
    /// producer (<see cref="Projection.StateProjector"/>'s <see cref="FlowEvent.CaptureResolved"/>
    /// arm), so a rejected room settles Terminal rather than reading Running with no worker or pump
    /// alive. A follow-up <c>baton run --room-dir</c> still re-drives the DAG for the OTHER
    /// non-Terminal shape this verb can leave behind — an accept that makes a downstream step newly
    /// deliverable; an operator who wants rejected work redone dispatches it fresh
    /// (<c>baton redispatch</c>, whose Indeterminate-parent refusal this resolution clears).
    /// </summary>
    /// <param name="accepted">
    /// Same boolean as <see cref="FlowEvent.CaptureResolved.Accepted"/> — see its remarks for what
    /// each value means and why the prose-safe/all-or-nothing rule (spec/baton.md §3, "Consumer
    /// obligations") is not re-derived here. When the unsatisfied-output list names more than one
    /// output, every name gets the SAME captured body verbatim on acceptance — there is only ever
    /// one captured response per execution, never one per declared name, so a two-name capture (e.g.
    /// two prose-safe `.md` outputs on one contract) produces two identical files. No shipped role
    /// hits this today (every multi-output role's second output is structured, which blocks the
    /// capture from forming at all per <see cref="Outcomes.OutputMaterializer"/>'s own gate), so this
    /// is latent rather than live. <c>false</c>: no file is written; <paramref name="reason"/> is
    /// required.
    /// </param>
    /// <exception cref="InvalidCaptureResolutionException">
    /// <paramref name="executionId"/> names no step this verb admits for the requested
    /// <paramref name="accepted"/> value — see the guard's own comment for
    /// <see cref="Domain.IndeterminateProducer"/>'s per-verb admission table (F1, #1593 review): an
    /// Indeterminate settled by <see cref="FlowEvent.VerifyFailed"/> or
    /// <see cref="FlowEvent.ExecutionArrested"/> is never a target of either verb, and one settled by
    /// this class's own #1593 contract-failure arm (<see cref="Outcomes.OutcomeClassifier"/>, no
    /// captured response) admits <c>--reject</c> only. <paramref name="accepted"/> is <c>false</c> and
    /// <paramref name="reason"/> is null/whitespace, or reading/writing a captured or declared output
    /// file failed.
    /// </exception>
    /// <exception cref="Baton.Concurrency.WorkflowLockedException">
    /// Another Flow instance already holds <paramref name="roomDirectoryPath"/>'s lock.
    /// </exception>
    public static async Task<FlowState> RecordCaptureResolutionAsync(
        string roomDirectoryPath,
        WorkflowDefinitionSnapshot snapshot,
        string artifactsRootPath,
        IEventLogReader eventLogReader,
        IEventLogWriter eventLogWriter,
        ExecutionId executionId,
        bool accepted,
        string? reason,
        bool close = false,
        CancellationToken cancellationToken = default,
        string? holderDescription = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrEmpty(artifactsRootPath);
        ArgumentNullException.ThrowIfNull(eventLogReader);
        ArgumentNullException.ThrowIfNull(eventLogWriter);

        if (!accepted && string.IsNullOrWhiteSpace(reason))
        {
            throw new InvalidCaptureResolutionException(
                close
                    ? "Closing an Indeterminate settle requires --reason: the conductor's justification " +
                      "is itself the room fact this verb exists to record."
                    : "Rejecting a captured response requires --reason: the conductor's justification is " +
                      "itself the room fact this verb exists to record.");
        }

        using var guard = ConcurrencyGuard.Acquire(roomDirectoryPath, holderDescription);

        var checkpoint = ProjectionCheckpointStore.Load(roomDirectoryPath);
        var log = await eventLogReader.ReadSnapshotFromOffsetAsync(checkpoint?.ByteOffset ?? 0, cancellationToken).ConfigureAwait(false);
        if (log.IsFallbackToFull)
        {
            checkpoint = null;
        }

        var (state, _) = StateProjector.ProjectAndCheckpoint(log.FlowEvents, snapshot, checkpoint, log.ByteOffset);

        var target = state.Steps.FirstOrDefault(step => step.LatestExecutionId == executionId);

        // F1 (#1593 review): IndeterminateProducer, not a bare LatestCapturedResponseFile null/not-null
        // read, is what makes a step a target of this verb, and which of the two verbs. Mirrors
        // ResolveCommand's own admission check one layer up.
        // N3 (#1664 re-review): a null IndeterminateProducer on a step that IS awaiting resolution and
        // DOES carry a captured response file is the legacy pre-#1593 shape — the same fallback
        // RedispatchCommand.cs already applies to a pre-field terminal.json — not "a producer no verb
        // admits". ProjectionCheckpointStore's Version gate (checkpoint.Version <
        // ProjectionCheckpoint.CurrentVersion, the single spelling of the current version) means this can now
        // only be reached via a full replay off an old flow.jsonl that genuinely predates the field, so
        // treating it as CapturedResponse is a correct read of the journal, not a workaround for a stale
        // checkpoint.
        var effectiveProducer = target?.IndeterminateProducer
            ?? (target?.LatestCapturedResponseFile is not null ? IndeterminateProducer.CapturedResponse : (IndeterminateProducer?)null);
        // #1622 (d)/#1700: --close admits exactly the producers --accept-capture/--reject never did —
        // VerifyFailed/Arrested/null — mirroring ResolveCommand's own widened admission one layer up.
        var admitsThisVerb = target is { IndeterminateAwaitingResolution: true }
            && (close
                ? effectiveProducer is IndeterminateProducer.VerifyFailed or IndeterminateProducer.Arrested
                    or IndeterminateProducer.BuildLockBusy or null
                : effectiveProducer == IndeterminateProducer.CapturedResponse
                    || (accepted == false && effectiveProducer == IndeterminateProducer.ContractFailure));
        if (!admitsThisVerb)
        {
            // #1608 review finding 5: an explicit --execution naming a step whose latest attempt
            // already recorded an ACCEPTED CaptureResolved for this exact execution is a repair
            // request, not an invalid target — see ReconcileAcceptedCaptureAsync's own remarks for why
            // "already resolved" must not always mean "refuse" now that the fact is journaled before
            // the files it describes. Reads the full log rather than the checkpoint-relative slice
            // above: this branch is rare (a crash-repair path) and the prior resolution can predate
            // the checkpoint this call happened to load. Gated on `accepted` too: a --reject against
            // an already-accepted execution must still refuse, not silently reinterpret the caller's
            // explicit reject as a repair of someone else's earlier accept.
            if (target is not null && accepted)
            {
                var fullEvents = await eventLogReader.ReadAllAsync(cancellationToken).ConfigureAwait(false);
                var priorResolution = fullEvents.OfType<FlowEvent.CaptureResolved>()
                    .LastOrDefault(resolved => resolved.ExecutionId == executionId && resolved.StepId == target.StepId);
                if (priorResolution is { Accepted: true })
                {
                    var outcome = await ReconcileAcceptedCaptureAsync(priorResolution, artifactsRootPath, cancellationToken)
                        .ConfigureAwait(false);
                    switch (outcome)
                    {
                        case ReconciliationOutcome.Repaired:
                            return StateProjector.Project(fullEvents, snapshot);
                        case ReconciliationOutcome.Unrecoverable:
                            throw new InvalidCaptureResolutionException(
                                $"Execution '{executionId.Value}' was already accepted in room '{roomDirectoryPath}', but its " +
                                "declared output(s) are missing on disk AND its captured response is also gone — nothing " +
                                "left to re-materialize from; this room needs manual repair.");
                        case ReconciliationOutcome.NothingToRepair:
                        default:
                            // Every declared output is already on disk -- an ordinary duplicate
                            // resolution attempt, not a crash to repair. Falls through to the
                            // exactly-once refusal below, unchanged from before this repair path
                            // existed (MutationInterfaceCaptureResolutionTests' own
                            // "second resolution throws" pin).
                            break;
                    }
                }
            }

            // #1877: the administrative close of an ALREADY-rejected capture. A room resolved under
            // the pre-#1877 rule (a CapturedResponse reject left the step retry-eligible) has no
            // unresolved capture left to target, yet its step is the one still dangling — every other
            // verb refused it, and the operator's remaining options were to redispatch real vendor
            // work, delete the evidence, or hand-edit the ledger. The admission predicate below (and
            // spec/baton.md §3, which holds why it is shaped this way) reads only durable journal
            // facts. Records FlowEvent.StepRetryForeclosed rather
            // than re-resolving the capture — see that event's own remarks for the exactly-once claim
            // this respects, and spec/baton.md §3 for why a foreclosure is the right shape for an
            // administrative close. Idempotent: re-running appends another foreclosure, same state.
            if (close && target is not null && target.LatestExecutionId == executionId
                && target.Status != StepStatus.Succeeded)
            {
                var priorEvents = await eventLogReader.ReadAllAsync(cancellationToken).ConfigureAwait(false);
                var priorRejection = priorEvents.OfType<FlowEvent.CaptureResolved>()
                    .LastOrDefault(resolved => resolved.ExecutionId == executionId && resolved.StepId == target.StepId);
                if (priorRejection is { Accepted: false })
                {
                    await eventLogWriter.AppendAsync(
                            new FlowEvent.StepRetryForeclosed(
                                target.StepId, executionId, reason!, ForeclosedBy: "resolve --close"),
                            cancellationToken)
                        .ConfigureAwait(false);

                    var closedEvents = await eventLogReader.ReadAllAsync(cancellationToken).ConfigureAwait(false);
                    return StateProjector.Project(closedEvents, snapshot);
                }
            }

            throw new InvalidCaptureResolutionException(
                $"Execution '{executionId.Value}' has no unresolved indeterminate capture in room " +
                $"'{roomDirectoryPath}' — 'baton resolve' only targets a step still awaiting conductor resolution.");
        }

        IReadOnlyList<string> resolvedOutputNames = target!.LatestUnsatisfiedOutputNames ?? [];

        if (accepted)
        {
            if (target.LatestCapturedResponseFile is null || resolvedOutputNames.Count == 0)
            {
                throw new InvalidCaptureResolutionException(
                    $"Execution '{executionId.Value}' has no captured response body to accept in room '{roomDirectoryPath}'.");
            }

            var outputDirectory = ArtifactManager.ResolveOutputDirectory(artifactsRootPath, executionId);
            var capturedPath = Path.Combine(outputDirectory, target.LatestCapturedResponseFile);
            string capturedContent;
            try
            {
                capturedContent = await File.ReadAllTextAsync(capturedPath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidCaptureResolutionException(
                    $"Could not read captured response '{capturedPath}' for execution '{executionId.Value}': {ex.Message}.", ex);
            }

            var body = Outcomes.OutputMaterializer.StripCapturedResponseHeader(capturedContent);

            // #1608 review finding 3: validated in its own pass, entirely before the fact is journaled
            // below — sharing one foreach with the write pass meant a later name's reserved/traversal
            // failure could leave an earlier name already written to disk with no
            // FlowEvent.CaptureResolved ever appended (InvalidCaptureResolutionException's own remarks
            // promise "no file is written" when it's thrown, and a declared output sitting on disk
            // while the room still reads Indeterminate is exactly the filesystem-level false-Succeeded
            // gap OutputMaterializer's class remarks exist to prevent). Every name here already passed
            // ProducedOutput's own reserved/traversal checks at contract-declaration time
            // (WorkerContract.cs) and OutputMaterializer's prose-safe/all-or-nothing gate at capture
            // time — this is defense-in-depth on the one permitted writer under a declared name
            // (spec/baton.md §3), not re-validation of either.
            foreach (var outputName in resolvedOutputNames)
            {
                if (ReservedOutputNames.IsReserved(outputName) || ReservedOutputNames.IsPathTraversal(outputName))
                {
                    throw new InvalidCaptureResolutionException(
                        $"Declared output name '{outputName}' for execution '{executionId.Value}' is not " +
                        "a bare, non-reserved file name — refusing to write it.");
                }
            }

            // #1608 review finding 5: "fact then files" — this append deliberately precedes the writes
            // below, trading a self-healing gap (ledger Succeeded, declared output still missing) for
            // the un-healable one the reverse order left open. spec/baton.md §3 holds why; the healing
            // half is ReconcileAcceptedCaptureAsync below, which a later explicit --execution re-enters.
            await eventLogWriter.AppendAsync(
                    new FlowEvent.CaptureResolved(target.StepId, executionId, accepted, reason, resolvedOutputNames),
                    cancellationToken)
                .ConfigureAwait(false);

            foreach (var outputName in resolvedOutputNames)
            {
                var outputPath = Path.Combine(outputDirectory, outputName);
                try
                {
                    await File.WriteAllTextAsync(outputPath, body, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new InvalidCaptureResolutionException(
                        $"Could not write declared output '{outputPath}' for execution '{executionId.Value}': {ex.Message}. " +
                        "The resolution itself is already durably recorded — re-run 'baton resolve' naming this same " +
                        "--execution once the environment issue is fixed to re-materialize the missing output(s).", ex);
                }
            }

            var acceptedFinalEvents = await eventLogReader.ReadAllAsync(cancellationToken).ConfigureAwait(false);
            return StateProjector.Project(acceptedFinalEvents, snapshot);
        }

        await eventLogWriter.AppendAsync(
                new FlowEvent.CaptureResolved(target.StepId, executionId, accepted, reason, resolvedOutputNames),
                cancellationToken)
            .ConfigureAwait(false);

        var finalEvents = await eventLogReader.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        return StateProjector.Project(finalEvents, snapshot);
    }

    /// <summary>
    /// #1608 review finding 5: repairs the one crash window "fact then files" (above) can leave open
    /// — a durable <see cref="FlowEvent.CaptureResolved"/> with <c>Accepted: true</c> whose write(s)
    /// never completed, or completed only partially, because the process died between the append and
    /// the writes it describes. Never called automatically: an explicit <c>baton resolve --execution
    /// &lt;id&gt;</c> naming an execution whose step is no longer
    /// <see cref="StepState.IndeterminateAwaitingResolution"/> re-enters here (see
    /// <see cref="RecordCaptureResolutionAsync"/>'s own remarks) rather than being refused outright,
    /// the same "just run it again" idempotent-retry shape the pre-#1608-review write order already
    /// promised — restored, not abandoned, by moving what "again" means past the fact instead of
    /// before it.
    /// <para>
    /// #1608 re-review finding 3 — what "missing" means, and its two edges. A declared output counts as
    /// missing when it is absent <b>or</b> zero-length, so the likeliest crash shape (killed mid-write,
    /// leaving an empty file <see cref="File.Exists"/> reports as present) is repairable rather than
    /// reported as nothing-to-repair. A <b>torn but non-empty</b> write is NOT detected and needs manual
    /// repair: nothing recorded on <see cref="FlowEvent.CaptureResolved"/> says how long the body should
    /// have been, and re-deriving it by re-reading the capture on every call would clobber a declared
    /// output a human deliberately edited after acceptance — the case this same existence-only predicate
    /// is what protects. In the other direction, an output that is legitimately empty would read as
    /// permanently repairable (and, with the capture also gone, as needing manual repair) — reachable
    /// only by hand: <see cref="Outcomes.OutputMaterializer.TryCaptureFinalResponse"/> refuses to
    /// capture a blank response at all, and it is the only producer of the unresolved captures this
    /// path repairs, so an engine-produced accepted capture always has a non-empty body. Stated rather
    /// than defended against, since the repair is idempotent and appends no second fact — but it is
    /// also why that capture-time gate must not be relaxed without revisiting this predicate.
    /// </para>
    /// </summary>
    /// <returns>
    /// <see cref="ReconciliationOutcome.NothingToRepair"/> when every declared output the resolving
    /// <see cref="FlowEvent.CaptureResolved"/> named is already on disk — an ordinary duplicate
    /// resolution attempt, not a crash, and the caller's exactly-once refusal applies unchanged.
    /// <see cref="ReconciliationOutcome.Repaired"/> once this call has re-materialized every name that
    /// was missing. <see cref="ReconciliationOutcome.Unrecoverable"/> when outputs are missing AND the
    /// underlying captured-response file this resolution was accepted from is ALSO gone — nothing left
    /// to re-derive from; the caller fails closed on that result.
    /// </returns>
    private static async Task<ReconciliationOutcome> ReconcileAcceptedCaptureAsync(
        FlowEvent.CaptureResolved resolution,
        string artifactsRootPath,
        CancellationToken cancellationToken)
    {
        var outputDirectory = ArtifactManager.ResolveOutputDirectory(artifactsRootPath, resolution.ExecutionId);
        var outputNames = resolution.ResolvedOutputNames ?? [];
        var missingNames = outputNames.Where(name =>
        {
            // #1608 re-review finding 3: absent OR zero-length. File.WriteAllTextAsync opens with
            // FileMode.Create, so a kill DURING the write -- not merely between the append and the
            // loop -- leaves a file that exists and is empty; existence alone would report that as
            // NothingToRepair and the caller would tell the operator the room has nothing to fix.
            // One FileInfo stat answers both, rather than an Exists check the Length read could race.
            var info = new FileInfo(Path.Combine(outputDirectory, name));
            return !info.Exists || info.Length == 0;
        }).ToList();

        if (missingNames.Count == 0)
        {
            return ReconciliationOutcome.NothingToRepair;
        }

        // Deliberately re-derived from the well-known capture path rather than
        // StepState.LatestCapturedResponseFile — StateProjector's CaptureResolved(Accepted: true) case
        // clears that field from projected state (it belongs to the audit trail of an UNresolved
        // capture, not a resolved one), so this repair must read the raw file, not projected state.
        var capturedPath = Path.Combine(outputDirectory, Outcomes.OutputMaterializer.CapturedResponseFileName);
        if (!File.Exists(capturedPath))
        {
            return ReconciliationOutcome.Unrecoverable;
        }

        var capturedContent = await File.ReadAllTextAsync(capturedPath, cancellationToken).ConfigureAwait(false);
        var body = Outcomes.OutputMaterializer.StripCapturedResponseHeader(capturedContent);

        foreach (var outputName in missingNames)
        {
            var outputPath = Path.Combine(outputDirectory, outputName);
            try
            {
                await File.WriteAllTextAsync(outputPath, body, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidCaptureResolutionException(
                    $"Could not re-materialize declared output '{outputPath}' for execution " +
                    $"'{resolution.ExecutionId.Value}': {ex.Message}.", ex);
            }
        }

        return ReconciliationOutcome.Repaired;
    }

    /// <summary>See <see cref="ReconcileAcceptedCaptureAsync"/>'s own remarks for each member.</summary>
    private enum ReconciliationOutcome
    {
        NothingToRepair,
        Repaired,
        Unrecoverable,
    }

    /// <summary>
    /// A third mutation-surface entry point: mints a step-less supplementary
    /// execution — a human, or any other non-process party, producing a new artifact outside the
    /// DAG during a pause. Appends <see cref="FlowEvent.ExecutionRequestAccepted"/> with
    /// <c>StepId: null</c> and pre-allocates the output directory exactly like any other worker,
    /// but does not run the pump: minting one changes no step's readiness by itself, and
    /// nothing here needs driving to a fixed point (no daemon). The returned
    /// <see cref="ExecutionId"/> becomes usable as a <see cref="DecisionType.RetryWithRevision"/> or
    /// <see cref="DecisionType.Supersede"/> decision's <c>SupplementaryExecutionId</c> once
    /// completion — <see cref="NonProcessCompletionDetector"/>, consulted by a later
    /// <see cref="StartWorkflowAsync"/> or <see cref="RecordDecisionAsync"/> pump — has recorded it
    /// as <see cref="FlowEvent.ExecutionSucceeded"/>.
    /// </summary>
    /// <exception cref="WorkflowLockedException">
    /// Another Flow instance already holds <paramref name="roomDirectoryPath"/>'s lock.
    /// </exception>
    /// <exception cref="UnresolvedWorkerException">
    /// <paramref name="worker"/> has no corresponding <see cref="WorkerBinding.NonProcess"/> among
    /// <paramref name="workerBindings"/> — a supplementary execution is non-process by definition,
    /// so naming a <see cref="WorkerBinding.Process"/> role (or no role at all) is invalid.
    /// </exception>
    public static async Task<(FlowState State, ExecutionId ExecutionId)> RecordSupplementaryExecutionAsync(
        WorkflowId workflowId,
        string roomDirectoryPath,
        WorkflowDefinitionSnapshot snapshot,
        IReadOnlyDictionary<string, WorkerBinding> workerBindings,
        string artifactsRootPath,
        string worker,
        IReadOnlyList<string> inputs,
        IEventLogReader eventLogReader,
        IEventLogWriter eventLogWriter,
        CancellationToken cancellationToken = default,
        string? holderDescription = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(workerBindings);
        ArgumentException.ThrowIfNullOrEmpty(artifactsRootPath);
        ArgumentException.ThrowIfNullOrEmpty(worker);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(eventLogReader);
        ArgumentNullException.ThrowIfNull(eventLogWriter);

        using var guard = ConcurrencyGuard.Acquire(roomDirectoryPath, holderDescription);

        if (!workerBindings.TryGetValue(worker, out var binding) || binding is not WorkerBinding.NonProcess nonProcess)
        {
            throw new UnresolvedWorkerException($"No non-process WorkerBinding registered for Worker '{worker}'.");
        }

        var executionId = new ExecutionId(Guid.NewGuid().ToString("n"));
        var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRootPath, executionId);
        var environment = ArtifactManager.BuildEnvironment(inputs, outputDirectory, artifactsRootPath);
        var outputs = nonProcess.Contract.ProducedOutputs.Select(output => output.Name).ToList();

        var request = new ExecutionRequest(
            executionId,
            workflowId,
            StepId: null,
            worker,
            inputs,
            outputs,
            Timeout: null,
            environment,
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>(),
            GrantAuditMode: nonProcess.GrantAuditMode);


        // The write-sequence discipline still applies: appended and fsync'd before this method
        // returns, even though no Core process ever follows it.
        await eventLogWriter.AppendAsync(CreateExecutionRequestAccepted(request), cancellationToken)
            .ConfigureAwait(false);

        var checkpoint = ProjectionCheckpointStore.Load(roomDirectoryPath);
        var log = await eventLogReader.ReadSnapshotFromOffsetAsync(checkpoint?.ByteOffset ?? 0, cancellationToken).ConfigureAwait(false);
        if (log.IsFallbackToFull)
        {
            checkpoint = null;
        }
        var (state, _) = StateProjector.ProjectAndCheckpoint(log.FlowEvents, snapshot, checkpoint, log.ByteOffset);

        return (state, executionId);
    }

    /// <summary>
    /// A fourth mutation-surface entry point: records an on-demand
    /// cancellation intent — fsync'd before anything else happens, even when the target has already
    /// reached a terminal outcome (a too-late no-op; intent-first ordering) — then
    /// drives the consequences to the next fixed point through the same pump
    /// <see cref="StartWorkflowAsync"/> uses. Phase 1 finalizes only targets with no live Core
    /// process to signal: a pending non-process execution's obligation is fulfilled directly, in the
    /// same round, by <see cref="NonProcessCancellationDetector"/>. A still-running
    /// <see cref="WorkerBinding.Process"/> target's request is durably recorded here but not yet
    /// delivered — that is Phase 2's machinery.
    /// </summary>
    /// <exception cref="WorkflowLockedException">
    /// Another Flow instance already holds <paramref name="roomDirectoryPath"/>'s lock.
    /// </exception>
    /// <exception cref="UnknownExecutionIdException">
    /// <paramref name="targetExecutionId"/> was never admitted via <see cref="FlowEvent.ExecutionRequestAccepted"/>.
    /// </exception>
    public static async Task<FlowState> RequestCancellationAsync(
        WorkflowId workflowId,
        string roomDirectoryPath,
        WorkflowDefinitionSnapshot snapshot,
        IReadOnlyDictionary<string, WorkerBinding> workerBindings,
        string artifactsRootPath,
        IEventLogReader eventLogReader,
        IEventLogWriter eventLogWriter,
        ICoreDispatcher dispatcher,
        ExecutionId targetExecutionId,
        InFlightExecutionRegistry? inFlightExecutions = null,
        CancellationToken cancellationToken = default,
        TimeProvider? timeProvider = null,
        Func<double>? jitterSource = null,
        string? holderDescription = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(workerBindings);
        ArgumentException.ThrowIfNullOrEmpty(artifactsRootPath);
        ArgumentNullException.ThrowIfNull(eventLogReader);
        ArgumentNullException.ThrowIfNull(eventLogWriter);
        ArgumentNullException.ThrowIfNull(dispatcher);

        using var guard = ConcurrencyGuard.Acquire(roomDirectoryPath, holderDescription);

        var checkpoint = ProjectionCheckpointStore.Load(roomDirectoryPath);
        var log = await eventLogReader.ReadSnapshotFromOffsetAsync(checkpoint?.ByteOffset ?? 0, cancellationToken).ConfigureAwait(false);
        if (log.IsFallbackToFull)
        {
            checkpoint = null;
        }
        var (_, latestCheckpoint) = StateProjector.ProjectAndCheckpoint(log.FlowEvents, snapshot, checkpoint, log.ByteOffset);
        var knownExecutionIds = latestCheckpoint.State.AcceptedRequestByExecutionId.Keys.ToHashSet();

        CancellationValidator.Validate(knownExecutionIds, targetExecutionId);

        // The write-sequence discipline: recorded and fsync'd before anything else, whether the
        // target turns out to be a live process, a pending non-process execution, or already
        // terminal (the record itself is the too-late outcome; nothing else changes).
        await eventLogWriter.AppendAsync(
                new FlowEvent.CancellationRequested(targetExecutionId, CancellationOrigin.Operator), cancellationToken)
            .ConfigureAwait(false);

        return await PumpToFixedPointAsync(
                workflowId, roomDirectoryPath, snapshot, workerBindings, artifactsRootPath, eventLogReader, eventLogWriter, dispatcher,
                inFlightExecutions ?? new InFlightExecutionRegistry(), cancellationToken,
                timeProvider ?? TimeProvider.System, jitterSource ?? (() => Random.Shared.NextDouble()))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// A fifth mutation-surface entry point (issue #1359): re-enters an already-dispatched step's
    /// worker with a new message on the same workspace and grants, via the adapter's existing
    /// resume-session plumbing (<c>WorkerInvocation.ResumeSession</c>/<c>SessionId</c>, in
    /// <c>Baton.Vendors</c> — <c>Baton</c> never references that assembly, per Adapter Isolation).
    /// <paramref name="workerBindings"/> must already carry the resume-shaped override for
    /// <paramref name="worker"/> (<c>ResumeSession: true</c>, the operator's message as its
    /// <c>PromptTemplate</c>) — <c>Baton.Cli.ResumeCommand</c>
    /// builds that override the same way <c>SupplyCommand</c> overlays its own single-worker binding;
    /// this method only decides WHICH step that binding dispatches against and links the resulting
    /// execution to the one it continues.
    /// <para>
    /// Unlike <see cref="StartWorkflowAsync"/>'s readiness-driven dispatch, this always dispatches
    /// exactly one execution regardless of <see cref="Scheduling.DependencyResolver"/>'s ordinary
    /// conditions — a resume is an explicit operator override of an already-terminal (or paused)
    /// step, not a step the DAG itself would ever re-offer as ready on its own. Blocks until that one
    /// dispatch completes and its outcome is recorded; unlike every other entry point above, this
    /// does NOT pump to a fixed point on its own (#1359's scope: "one message per resume invocation",
    /// no cascading multi-step orchestration folded in here) — a caller wanting downstream
    /// consequences (a sibling step this one's outcome newly unblocks, or a pause obligation) makes a
    /// separate <see cref="StartWorkflowAsync"/> call afterward, the same two-call sequence
    /// <c>SupplyCommand</c> already uses for its own single-execution mutation.
    /// </para>
    /// </summary>
    /// <param name="worker">
    /// The worker ROLE (<see cref="WorkflowStepDefinition.Worker"/>) to resume — identifies the
    /// target step by which snapshot step declares it, not by step id. Refused as ambiguous if more
    /// than one step in <paramref name="snapshot"/> names the same worker.
    /// </param>
    /// <param name="sessionId">
    /// The vendor session id the caller's bindings file records for <paramref name="worker"/> right
    /// now, stored on <see cref="ExecutionRequest.SessionId"/> (that field's doc owns the why). Here
    /// it is also the refusal input: a resume whose target execution already recorded a DIFFERENT
    /// session id is refused up front instead of silently forking the vendor session. <c>null</c> is
    /// never checked against — the first resume of an ordinary dispatch has nothing to compare.
    /// </param>
    /// <exception cref="Baton.Concurrency.WorkflowLockedException">
    /// Another Flow instance already holds <paramref name="roomDirectoryPath"/>'s lock.
    /// </exception>
    /// <exception cref="InvalidResumeException">
    /// No step names <paramref name="worker"/>, more than one does, the target step has never been
    /// dispatched (<see cref="StepStatus.Pending"/>), its latest attempt is still
    /// <see cref="StepStatus.Running"/> (mid-flight steering is out of #1359's scope),
    /// <paramref name="workerBindings"/> resolves it to a <see cref="WorkerBinding.NonProcess"/>
    /// (nothing to resume a session on), or <paramref name="sessionId"/> disagrees with the session
    /// id the execution being resumed actually recorded (F6).
    /// </exception>
    public static async Task<(FlowState State, ExecutionId ExecutionId)> RecordResumeAsync(
        WorkflowId workflowId,
        string roomDirectoryPath,
        WorkflowDefinitionSnapshot snapshot,
        IReadOnlyDictionary<string, WorkerBinding> workerBindings,
        string artifactsRootPath,
        string worker,
        IEventLogReader eventLogReader,
        IEventLogWriter eventLogWriter,
        ICoreDispatcher dispatcher,
        CancellationToken cancellationToken = default,
        TimeProvider? timeProvider = null,
        string? holderDescription = null,
        string? sessionId = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(workerBindings);
        ArgumentException.ThrowIfNullOrEmpty(artifactsRootPath);
        ArgumentException.ThrowIfNullOrEmpty(worker);
        ArgumentNullException.ThrowIfNull(eventLogReader);
        ArgumentNullException.ThrowIfNull(eventLogWriter);
        ArgumentNullException.ThrowIfNull(dispatcher);

        using var guard = ConcurrencyGuard.Acquire(roomDirectoryPath, holderDescription);

        var checkpoint = ProjectionCheckpointStore.Load(roomDirectoryPath);
        var log = await eventLogReader.ReadSnapshotFromOffsetAsync(checkpoint?.ByteOffset ?? 0, cancellationToken).ConfigureAwait(false);
        if (log.IsFallbackToFull)
        {
            checkpoint = null;
        }
        var (state, resumeCheckpoint) = StateProjector.ProjectAndCheckpoint(log.FlowEvents, snapshot, checkpoint, log.ByteOffset);

        var matchingSteps = snapshot.Steps.Where(s => s.Worker == worker).ToList();
        if (matchingSteps.Count == 0)
        {
            throw new InvalidResumeException($"No step in this workflow names worker '{worker}'.")
            {
                TryInvocation = "pass --worker naming one of this workflow's roles: " +
                    $"{string.Join(", ", snapshot.Steps.Select(s => s.Worker).Distinct())}.",
            };
        }

        if (matchingSteps.Count > 1)
        {
            throw new InvalidResumeException(
                $"Worker '{worker}' is bound to {matchingSteps.Count} steps " +
                $"({string.Join(", ", matchingSteps.Select(s => s.StepId))}) — baton resume needs a single, " +
                "unambiguous target step.")
            {
                TryInvocation = "give each step its own worker name in the workflow definition, so baton " +
                    "resume can target exactly one.",
            };
        }

        var stepDefinition = matchingSteps[0];
        var stepState = state.Steps.Single(s => s.StepId == stepDefinition.StepId);

        if (stepState.Status == StepStatus.Pending)
        {
            throw new InvalidResumeException($"Step '{stepDefinition.StepId}' (worker '{worker}') has never run — nothing to resume.")
            {
                TryInvocation = "dispatch it at least once first (`baton run` or `baton dispatch`), then resume it.",
            };
        }

        if (stepState.Status == StepStatus.Running)
        {
            // #1359 F3: room-says-Running is not the same fact as "the engine dispatching it is still
            // alive" — reuse the same probe `baton status`'s human rendering already consults rather than
            // inventing a second liveness mechanism (StatusCommand.FormatStepStatus).
            var allEvents = await eventLogReader.ReadAllAsync(cancellationToken).ConfigureAwait(false);
            var accepted = allEvents.OfType<FlowEvent.ExecutionRequestAccepted>()
                .LastOrDefault(e => e.Request.ExecutionId == stepState.LatestExecutionId);
            var liveness = EngineLivenessProbe.Probe(accepted?.EnginePid, accepted?.EngineStartTime);

            if (liveness.Status != EngineLivenessStatus.Dead)
            {
                var unknownSuffix = liveness.Status == EngineLivenessStatus.Unknown ? $" (liveness unknown: {liveness.Why})" : string.Empty;
                throw new InvalidResumeException(
                    $"Step '{stepDefinition.StepId}' (worker '{worker}') is still running{unknownSuffix} — baton resume only " +
                    "continues a terminal or stalled (paused) worker; steering a live one is out of scope for " +
                    "this verb.")
                {
                    TryInvocation = $"wait for the current run to finish, or check `baton status {roomDirectoryPath}` " +
                        "for progress; retry once it reaches a terminal or stalled state.",
                };
            }

            // STALLED (#1359 F3): the room projects Running, but the engine that accepted this
            // execution is provably dead — the crash-recovery case this verb exists to rescue.
            // Record the takeover before dispatching the resume's own linked execution, so the
            // orphaned attempt is never left with an accepted request and no resolution.
            await eventLogWriter.AppendAsync(
                    new FlowEvent.ExecutionFailed(
                        stepState.LatestExecutionId!.Value,
                        FailureClassification.Retryable,
                        "Abandoned: baton resume found the engine behind this execution is no longer alive " +
                        "(a stalled run) and took over the step."),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var previousExecutionId = stepState.LatestExecutionId
            ?? throw new InvalidResumeException($"Step '{stepDefinition.StepId}' (worker '{worker}') has no recorded execution to resume.")
            {
                TryInvocation = "re-run `baton run` (or `baton dispatch`) to dispatch it fresh — there is no recorded execution for baton resume to continue.",
            };

        // F6: the execution being resumed already recorded which session IT continued (null for the
        // first resume of an ordinary dispatch, which never had one). If the bindings file now names
        // a DIFFERENT session, the operator's SessionId edit and the ledger's own history disagree —
        // refuse rather than silently record a continuity nothing actually backs.
        if (resumeCheckpoint.State.AcceptedRequestByExecutionId.TryGetValue(previousExecutionId, out var previousRequest)
            && previousRequest.SessionId is { } previousSessionId
            && sessionId is not null
            && !string.Equals(previousSessionId, sessionId, StringComparison.Ordinal))
        {
            throw new InvalidResumeException(
                $"Worker '{worker}''s bindings file records SessionId '{sessionId}', but the execution " +
                $"being resumed ({previousExecutionId}) already recorded session '{previousSessionId}' — " +
                "baton resume refuses rather than silently forking the vendor session under a claimed " +
                "continuity nothing backs.")
            {
                TryInvocation = $"fix the SessionId recorded for '{worker}' in the bindings file back to " +
                    $"'{previousSessionId}' (the session the execution being resumed actually continued), " +
                    "or target the room/worker whose bindings file's SessionId edit was intentional.",
            };
        }

        if (!workerBindings.TryGetValue(worker, out var binding) || binding is not WorkerBinding.Process processBinding)
        {
            throw new InvalidResumeException($"Worker '{worker}' has no dispatchable (process) binding to resume a session on.")
            {
                TryInvocation = $"check the bindings file's entry for '{worker}' — baton resume needs a Process " +
                    "binding (a vendor CLI with a session to continue), not a non-process worker.",
            };
        }

        var executionId = new ExecutionId(Guid.NewGuid().ToString("n"));
        var inputPaths = ArtifactManager.ResolveInputPaths(stepDefinition, snapshot, state, artifactsRootPath);
        var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRootPath, executionId);
        var environment = ArtifactManager.BuildEnvironment(inputPaths, outputDirectory, artifactsRootPath);
        var (hookCanaryArmed, hookVerdictLedgerFileName) = CaptureHookCanaryArmingFields(processBinding);

        var request = new ExecutionRequest(
            executionId,
            workflowId,
            stepDefinition.StepId,
            worker,
            inputPaths,
            stepDefinition.Outputs,
            processBinding.Timeout,
            environment,
            stepState.UpstreamExecutionIds,
            GrantAuditMode: binding.GrantAuditMode,
            LinkedFromExecutionId: previousExecutionId,
            SessionId: sessionId,
            Adapter: processBinding.Adapter,
            Model: processBinding.Model,
            HookCanaryArmed: hookCanaryArmed,
            HookVerdictLedgerFileName: hookVerdictLedgerFileName);

        // The write-sequence rule: intent recorded and fsync'd before Core is ever asked to run.
        await eventLogWriter.AppendAsync(CreateExecutionRequestAccepted(request), cancellationToken).ConfigureAwait(false);

        var inFlightExecutions = new InFlightExecutionRegistry();
        inFlightExecutions.Bind(eventLogWriter);
        var dispatchCancellationToken = inFlightExecutions.Register(executionId);
        var prepared = new PreparedExecution(request, outputDirectory);

        // Awaited directly, not fire-and-forget: a resume is a single-shot operation that blocks and
        // reports exactly like the rest of this surface (DecideCommand's own doc comment states the
        // same contract), not a round dispatching arbitrarily many concurrent siblings.
        await DispatchAndRecordOutcomeAsync(
                prepared, processBinding, eventLogWriter, dispatcher, inFlightExecutions, dispatchCancellationToken, timeProvider ?? TimeProvider.System)
            .ConfigureAwait(false);

        var finalCheckpoint = ProjectionCheckpointStore.Load(roomDirectoryPath);
        var finalLog = await eventLogReader.ReadSnapshotFromOffsetAsync(finalCheckpoint?.ByteOffset ?? 0, cancellationToken).ConfigureAwait(false);
        if (finalLog.IsFallbackToFull)
        {
            finalCheckpoint = null;
        }
        var (finalState, _) = StateProjector.ProjectAndCheckpoint(finalLog.FlowEvents, snapshot, finalCheckpoint, finalLog.ByteOffset);

        return (finalState, executionId);
    }

    /// <summary>
    /// The scheduling pump shared by every mutation-surface entry point that needs one: repeatedly
    /// projects <see cref="FlowState"/>, finalizes any settled non-process execution, finalizes any
    /// non-process execution with an unfulfilled cancellation request, appends any owed
    /// <see cref="FlowEvent.WorkflowPaused"/> obligations, resolves every ready step, and dispatches
    /// all of them concurrently — to Core, or, for a <see cref="WorkerBinding.NonProcess"/> step,
    /// nowhere at all — until nothing is ready and nothing remains in flight. Assumes the caller
    /// already holds the concurrency guard.
    /// </summary>
    /// <remarks>
    /// M10 Phase 2: every process-bound dispatch this loop starts is registered with
    /// <paramref name="inFlightExecutions"/> under its own <see cref="CancellationTokenSource"/> —
    /// never the ambient <paramref name="cancellationToken"/> directly, so a cancellation of that
    /// host token can never reach Core without <see cref="FlowEvent.CancellationRequested"/> being
    /// recorded first. While dispatches are in flight, this loop also races
    /// <paramref name="cancellationToken"/> itself: the instant it is cancelled, every execution
    /// still registered gets its intent recorded and is then signalled via
    /// <see cref="InFlightExecutionRegistry.RequestStopAsync"/> — the host-initiated stop.
    /// </remarks>
    private static async Task<FlowState> PumpToFixedPointAsync(
        WorkflowId workflowId,
        string roomDirectoryPath,
        WorkflowDefinitionSnapshot snapshot,
        IReadOnlyDictionary<string, WorkerBinding> workerBindings,
        string artifactsRootPath,
        IEventLogReader eventLogReader,
        IEventLogWriter eventLogWriter,
        ICoreDispatcher dispatcher,
        InFlightExecutionRegistry inFlightExecutions,
        CancellationToken cancellationToken,
        TimeProvider timeProvider,
        Func<double> jitterSource,
        Action<DateTimeOffset>? onVendorQuotaPark = null,
        bool settleOnVendorExhaustion = false,
        Action? onDeferralWaitArmed = null,
        IReadOnlyDictionary<string, WorkerBinding>? fallbackWorkerBindings = null)
    {
        inFlightExecutions.Bind(eventLogWriter);

        var inFlight = new List<Task>();
        var hostStopRequested = false;

        // #1094: dedupes the vendor-quota park notice to the reset instant currently being waited on,
        // so re-projection loops do not reprint it. Surfacing only — see onVendorQuotaPark.
        DateTimeOffset? lastQuotaParkNotified = null;

        // #1634/#1762: this pump's own view of the ledger's Origin: Operator CancellationRequested
        // targets, re-accumulated every round the same way lastQuotaParkNotified above persists
        // across rounds. Scope (checkpoint-window, not "this call's own writes"), why
        // FlowState.CancellationRequestedExecutionIds/ProjectionCheckpoint.State's equivalent were
        // passed over, and why Origin had to become a durable field rather than staying an in-memory
        // gate: spec/baton.md §2.
        var cancellationRequestedExecutionIds = new HashSet<ExecutionId>();

        // #1577: which steps' current retry backoff already carries THIS process's own engine
        // identity on a StepRetryScheduled event -- populated the moment this pump appends one
        // (fresh obligation below, or a revival renewal in the idle-deferral branch), so a step
        // is stamped at most once per call no matter how many MaxParkWaitChunk pieces its wait
        // splits into. A step absent from this set when the idle branch is about to sleep on it
        // means some EARLIER pump (possibly now dead) is the last one who journaled its identity --
        // exactly the false-Stalled shape #1577 exists to close.
        var engineStampedStepIds = new HashSet<StepId>();

        // Starts as the caller's own token, but is switched to CancellationToken.None the instant a
        // host stop is detected below (M10 Phase 2): every read/write this loop performs to reach
        // its fixed point must keep completing even after the ambient token has fired, or the pump
        // could never converge to the consistent, fully-classified state a host stop promises.
        var ioCancellationToken = cancellationToken;
        FlowState state;
        ProjectionCheckpoint? currentCheckpoint = ProjectionCheckpointStore.Load(roomDirectoryPath);
        ProjectionCheckpoint? latestCheckpoint = null;

        while (true)
        {
            try
            {
                // Captured before the log read below, not after (issue #81): a sibling dispatch's
                // DispatchAndRecordOutcomeAsync always appends its outcome and fsyncs it before calling
                // Unregister, so if an ExecutionId has already dropped out of this snapshot, the append
                // that preceded its Unregister is guaranteed to already be durable — and therefore
                // visible to the log read started right after. Reading the log first and checking the
                // registry second (the previous order) offered no such guarantee: a sibling could finish
                // its append-then-Unregister sequence in the gap after the read had already started,
                // leaving a Running step that looks unregistered and unstarted-in-Core — indistinguishable
                // from the "safe pre-spawn crash" state — even though it had, in fact, just succeeded.
                var registeredExecutionIds = inFlightExecutions.RegisteredExecutionIds();

                // A single read of the combined log per round — feeding both Flow's own projection and
                // M10 Phase 3's crash reconciliation from one pass, rather than reading and parsing the
                // same file twice for no new information.
                var log = await eventLogReader.ReadSnapshotFromOffsetAsync(currentCheckpoint?.ByteOffset ?? 0, ioCancellationToken).ConfigureAwait(false);
                if (log.IsFallbackToFull)
                {
                    currentCheckpoint = null;
                }
                var events = log.FlowEvents;
                var projection = StateProjector.ProjectAndCheckpoint(events, snapshot, currentCheckpoint, log.ByteOffset);
                state = projection.State;
                latestCheckpoint = projection.Checkpoint;
                currentCheckpoint = latestCheckpoint;

                // #1634/#1762: this round's slice of the raw ledger, Origin: Operator only — a
                // HostStop or legacy (null-Origin) line is never added, which is what makes the block
                // below correct without also needing !hostStopRequested's help across a process
                // boundary. spec/baton.md §2.
                foreach (var flowEvent in events)
                {
                    if (flowEvent is FlowEvent.CancellationRequested { Origin: CancellationOrigin.Operator } cancellationRequested)
                    {
                        cancellationRequestedExecutionIds.Add(cancellationRequested.ExecutionId);
                    }
                }

                var acceptedRequestByExecutionId = latestCheckpoint.State.AcceptedRequestByExecutionId;

                // M10 Phase 3 (full robustness): joins Core's half of the log — read back here for
                // the first time since M7 Phase 6 wrote it — to Flow's own intents by ExecutionId,
                // distinguishing a process-bound step's "genuinely still Running" from "a prior pump
                // crashed before recording its outcome" (until now indistinguishable, per StateProjector's
                // own comment). A dispatch this very call still has registered is excluded — that pump is
                // this pump, not a crashed one.
                var (mergedStarted, mergedExited) = CoreEventAggregation.Merge(
                    latestCheckpoint.State.CoreStartedExecutionIds,
                    latestCheckpoint.State.CoreExitedByExecutionId,
                    log.CoreEvents);

                // Folded back into the working checkpoint immediately, not only at the save site:
                // each round reads the log from the previous round's offset, so a later round's
                // merge must start from these aggregates or the earlier tail's core events vanish
                // from its view. Load-bearing whenever one read surfaces obligations in two
                // priority buckets — the bucket blocks below each `continue` after acting, so the
                // lower bucket's execution is handled a round AFTER the read that observed it, and
                // without this carry it would re-derive as ToResubmit: a duplicate live dispatch
                // of a process that may still be running (PumpCheckpointCarryTests' two-bucket
                // fixture is exactly that trace).
                latestCheckpoint = latestCheckpoint with
                {
                    State = latestCheckpoint.State with
                    {
                        CoreStartedExecutionIds = mergedStarted,
                        CoreExitedByExecutionId = mergedExited,
                    },
                };
                currentCheckpoint = latestCheckpoint;

                var crashRecovery = ProcessCrashRecoveryDetector.GetObligations(
                    state, snapshot, workerBindings, mergedStarted, mergedExited, registeredExecutionIds);

                // ToClassify: the recorded exit and the contract on disk decide, exactly as if the
                // completion had just arrived — see ProcessCrashRecoveryDetector's remarks for the
                // obligation taxonomy; an unfulfilled cancellation request simply derives as too late
                // unless the recorded exit reason was itself CancelRequested (the crash clause).
                if (crashRecovery.ToClassify.Count > 0)
                {
                    foreach (var (executionId, exit) in crashRecovery.ToClassify)
                    {
                        // #1623 / F2: an execution carrying an unmatched VerifyStarted must NOT settle by
                        // classification (which would see exit 0 with contract satisfied and append
                        // ExecutionSucceeded, failing open). Replaying a verify subprocess across an engine
                        // restart belongs only to live dispatch; here we settle Indeterminate via VerifyFailed.
                        if (state.UnmatchedVerifyExecutionIds.Contains(executionId))
                        {
                            await eventLogWriter.AppendAsync(
                                new FlowEvent.VerifyFailed(
                                    executionId,
                                    FailingMembers: null,
                                    Tail: "verify did not complete across an engine restart",
                                    Kind: VerifyFailedKind.EngineRestart),
                                ioCancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        var request = acceptedRequestByExecutionId[executionId];
                        var contract = GetContractForClassification(request, workerBindings);
                        var outputDirectory = ArtifactManager.ResolveOutputDirectory(artifactsRootPath, executionId);
                        // The recorded request is the durable truth: a pre-#901 line carries no
                        // GrantAuditMode, which means no audit was promised for that execution —
                        // falling back to the binding's CURRENT mode would reinterpret history
                        // (and fail-closed against a worktree that may be long gone).
                        var grantAuditMode = request.GrantAuditMode ?? GrantAuditMode.Enforced;
                        string? worktreePath = null;
                        string? worktreeBaseRef = null;
                        IWorkerResponseParser? responseParser = null;
                        var changesTree = false;
                        string? changesTreeWorkingDirectory = null;
                        Func<string, int>? countHookVerdicts = null;
                        try
                        {
                            if (workerBindings.TryGetValue(request.Worker, out var b) && b is WorkerBinding.Process p)
                            {
                                // F4 (#1593 review): only an ACTUALLY-provisioned worktree, never the
                                // operator's own repository — see WorkerBinding.Process.IsWorktree's
                                // remarks for why a retry decision must not see that directory at all.
                                if (p.IsWorktree)
                                {
                                    worktreePath = p.Target.WorkingDirectory;
                                    worktreeBaseRef = p.WorktreeBaseSha;
                                }

                                responseParser = p.ResponseParser;
                                // #1622/#1390: the same bit the live-dispatch path reads off
                                // `binding.ChangesTree` below. 7c (#1720 review) corrects the
                                // mechanism this used to state: the binding is NOT re-derived from
                                // the role catalog here -- ChangesTree is a serialized field of
                                // WorkerBindingConfigEntry, written into the room's own bindings.json
                                // at dispatch and read back from that file, so this is the value
                                // recorded at dispatch and a catalog grant that changed since then
                                // cannot diverge the two.
                                changesTree = p.ChangesTree;
                                // #1622/#1390: deliberately NOT gated on p.IsWorktree the way worktreePath
                                // above is -- see OutcomeClassifier.Classify's own parameter doc for why a
                                // tree-changing role never gets an auto-provisioned worktree, so that gate
                                // would leave this permanently null for every real run.
                                changesTreeWorkingDirectory = changesTree ? p.Target.WorkingDirectory : null;
                                // #1741: still captured as a fallback for a PRE-#1741 journal line
                                // (request.HookCanaryArmed is null, below) -- a current line no longer
                                // relies on this, since HookCanaryArmed/HookVerdictLedgerFileName are
                                // now the recorded facts the canary arms from.
                                countHookVerdicts = p.Target.CountHookVerdicts;
                            }
                        }
                        catch (BatonFlowException)
                        {
                            // A recovery candidate's binding may legitimately refuse to resolve —
                            // the crash clause classifies from recorded facts alone (the test
                            // pinning this: StartWorkflowAsync_classifies_crash_recovery_candidate_
                            // when_its_worker_binding_refuses_to_resolve). The consequence is not a
                            // skip: if the journal promised an audit, Classify fails closed on the
                            // null worktree path. countHookVerdicts also stays null on this path, which
                            // only matters for a pre-#1741 journal line (ExecutionRequest.HookCanaryArmed's
                            // own doc has the full #1741 reasoning) -- see the counting block below for
                            // a line that already recorded arming.
                        }

                        // #1586 S1: the same recorded-adapter preference ExecutionUsageProjector's own
                        // #1567 comment explains — the durable request, not the binding's current
                        // resolution, since this is the crash-recovery path classifying from recorded
                        // facts alone.
                        var usageParser = request.Adapter is { } recoveryAdapter
                            ? StandardWorkerUsageParsers.Default.GetValueOrDefault(recoveryAdapter)
                            : null;

                        // #1741: arms from the RECORDED request fact, never from today's binding --
                        // see ExecutionRequest.HookCanaryArmed's own doc for why (spec/baton.md §9).
                        int? toolCallCount = null;
                        int? hookVerdictCount = null;
                        if (request.HookCanaryArmed is { } armed)
                        {
                            if (armed)
                            {
                                toolCallCount = CountToolCallsFromStdoutLog(usageParser, outputDirectory);
                                hookVerdictCount = request.HookVerdictLedgerFileName is { } ledgerFileName
                                    ? HookVerdictLedger.CountLines(Path.Combine(outputDirectory, ledgerFileName))
                                    : 0;
                            }
                        }
                        else if (countHookVerdicts is not null)
                        {
                            // request.HookCanaryArmed is null: a journal line predating #1741, kept on
                            // its old path (ExecutionRequest.HookCanaryArmed's own doc has the rule).
                            toolCallCount = CountToolCallsFromStdoutLog(usageParser, outputDirectory);
                            hookVerdictCount = countHookVerdicts(outputDirectory);
                        }

                        // #1373 follow-up (spec/baton.md §3): the journaled FlowEvent.ExecutionAttemptStarted
                        // is used when present; falls back to binding.WorktreeBaseSha and then the
                        // reflog heuristic exactly as before when absent.
                        var workspaceHeadShaAtStart =
                            latestCheckpoint.State.WorkspaceHeadShaAtStartByExecutionId.GetValueOrDefault(executionId);
                        var classification = OutcomeClassifier.Classify(
                            new CoreDispatchResult(exit.ExitCode, exit.Reason, exit.StderrTail), contract, outputDirectory,
                            grantAuditMode: grantAuditMode, worktreePath: worktreePath, responseParser: responseParser,
                            usageParser: usageParser, worktreeBaseRef: worktreeBaseRef, changesTree: changesTree,
                            changesTreeWorkingDirectory: changesTreeWorkingDirectory, toolCallCount: toolCallCount,
                            hookVerdictCount: hookVerdictCount, workspaceHeadShaAtStart: workspaceHeadShaAtStart);

                        // #1709: no TokenBudgetMonitor in scope on this path -- this classifies a
                        // RECORDED exit from a possibly-defunct workspace, never a live process, so
                        // ToOutcomeEvent's peakBilledInWindow stays at its null default.
                        await eventLogWriter.AppendAsync(ToOutcomeEvent(executionId, classification), ioCancellationToken)
                            .ConfigureAwait(false);
                        await AppendZeroOutputsTripwireIfAnyAsync(eventLogWriter, executionId, classification, ioCancellationToken)
                            .ConfigureAwait(false);
                    }

                    continue;
                }

                // No ExecutionStarted was ever recorded for this target (the crash clause): the cancel
                // wins, finalized directly — there was never anything to forward to Core in the first
                // place, and re-dispatching now would race the intent that already decided this attempt
                // is not to run.
                if (crashRecovery.ToFinalizeAsCancelled.Count > 0)
                {
                    foreach (var executionId in crashRecovery.ToFinalizeAsCancelled)
                    {
                        await eventLogWriter.AppendAsync(new FlowEvent.ExecutionCancelled(executionId), ioCancellationToken)
                            .ConfigureAwait(false);
                    }

                    continue;
                }

                // The orphan (the third crash state): ExecutionStarted with no ExecutionExited, this
                // call's own registry proving it is not still genuinely in flight here. Nothing can
                // re-attach (no daemon; BatonTask is spawn-and-await) and a second
                // execution for the same request is forbidden, so the attempt is finalized from recorded facts alone
                // as abandoned — a real, chargeable failed attempt — regardless of whether a
                // cancellation was also pending for it. There is no live handle left to re-issue a
                // cancellation toward (this pump is not the one that dispatched it); the best-effort
                // re-issue the spec allows for is therefore a documented no-op given BatonTask has no
                // cross-process re-attach capability, not a new mechanism this phase introduces.
                if (crashRecovery.ToFinalizeAsAbandoned.Count > 0)
                {
                    // #1530: the 2026-09-01 janitor sweep's actual question was "was the WORKER
                    // killed out from under a still-live pump" -- CoreEvent.ExecutionStarted.Pid is
                    // the worker's own pid, and is named first since it is the pid that answers that
                    // question. It carries no recorded start time (CoreEvent.ExecutionStarted's own
                    // shape), so EngineLivenessProbe -- which needs a start time to rule out pid
                    // reuse -- is never run against it; naming it plainly, unlabelled, is honest
                    // where claiming a liveness verdict this codebase cannot actually check would not
                    // be (claim-scope). The engine pid/liveness clause is the SAME EnginePid/
                    // EngineStartTime pair and EngineLivenessProbe every other liveness read in this
                    // codebase consults (StatusCommand.FormatStepStatus, RecordResumeAsync's STALLED
                    // check above) -- never a second, ad-hoc PID check -- kept as secondary context:
                    // reaching this branch at all already proves the prior pump released flow.lock
                    // without recording an exit, so the probe's verdict only enriches the sentence and
                    // an Unknown reading must not gate the finalization, which stays unconditional
                    // exactly as it was before this change.
                    var abandonedEvents = await eventLogReader.ReadAllAsync(ioCancellationToken).ConfigureAwait(false);
                    var abandonedCoreEvents = await eventLogReader.ReadAllCoreEventsAsync(ioCancellationToken).ConfigureAwait(false);
                    foreach (var executionId in crashRecovery.ToFinalizeAsAbandoned)
                    {
                        var accepted = abandonedEvents
                            .OfType<FlowEvent.ExecutionRequestAccepted>()
                            .LastOrDefault(e => e.Request.ExecutionId == executionId);
                        var workerPid = abandonedCoreEvents
                            .OfType<CoreEvent.ExecutionStarted>()
                            .LastOrDefault(e => e.ExecutionId == executionId)?.Pid;
                        var enginePidClause = accepted?.EnginePid is { } enginePid
                            ? $"engine pid {enginePid} is {EngineLivenessProbe.Probe(accepted.EnginePid, accepted.EngineStartTime).Status.ToString().ToLowerInvariant()}"
                            : null;
                        var workerPidClause = workerPid is { } pid ? $"worker pid {pid}" : null;
                        var clauses = new[] { workerPidClause, enginePidClause }.Where(c => c is not null);
                        var joinedClauses = string.Join("; ", clauses);
                        var pidClause = joinedClauses.Length > 0 ? $" ({joinedClauses})" : string.Empty;

                        await eventLogWriter.AppendAsync(
                                new FlowEvent.ExecutionFailed(
                                    executionId,
                                    FailureClassification.Retryable,
                                    "Abandoned during crash recovery: no ExecutionExited was recorded for this execution before Flow restarted"
                                        + pidClause + "."),
                                ioCancellationToken)
                            .ConfigureAwait(false);
                    }

                    continue;
                }

                // A derived obligation, re-evaluated from projected state on every round for
                // the same crash-safety reason the pause obligation below is: the filesystem is read
                // only here, at classification time, and the resulting ExecutionSucceeded is the
                // durable truth from then on. Must run before pause obligations, so a step that
                // just settled this way can still owe a WorkflowPaused append in the same pass.
                var settledNonProcessExecutionIds = NonProcessCompletionDetector.GetSettledExecutions(
                    state, snapshot, workerBindings, artifactsRootPath);
                if (settledNonProcessExecutionIds.Count > 0)
                {
                    foreach (var executionId in settledNonProcessExecutionIds)
                    {
                        await eventLogWriter.AppendAsync(new FlowEvent.ExecutionSucceeded(executionId), ioCancellationToken)
                            .ConfigureAwait(false);
                    }

                    continue;
                }

                // #1556: a derived obligation for every arrest intent the poller (or a sibling wait's
                // wake handler) marked on inFlightExecutions since this pump call's last round —
                // resolved against THIS round's own fresh projection, never the poller's possibly
                // stale one. Must run after the completion check above (Q2, #1530: completion beats
                // arrest within a round) and before the cancellation detector below, so a target this
                // settles finalizes through that SAME detector on the very next round rather than a
                // second settle path. SettleArrestIntentsAsync's own remarks have the full shape,
                // including why a step-tied Running target needs its binding proven NonProcess here
                // before anything is recorded.
                if (await SettleArrestIntentsAsync(
                            state, snapshot, workerBindings, acceptedRequestByExecutionId, inFlightExecutions, eventLogWriter, ioCancellationToken)
                        .ConfigureAwait(false))
                {
                    continue;
                }

                // A derived obligation (vacuous with no process), re-evaluated from
                // projected state on every round for the same crash-safety reason as the settlement
                // check above. Must run before pause obligations, so a step just cancelled this way can
                // still owe a WorkflowPaused append in the same pass.
                var cancelledNonProcessExecutionIds = NonProcessCancellationDetector.GetCancelledExecutions(
                    state, snapshot, workerBindings);
                if (cancelledNonProcessExecutionIds.Count > 0)
                {
                    foreach (var executionId in cancelledNonProcessExecutionIds)
                    {
                        await eventLogWriter.AppendAsync(new FlowEvent.ExecutionCancelled(executionId), ioCancellationToken)
                            .ConfigureAwait(false);
                    }

                    continue;
                }

                // A derived obligation, re-evaluated from projected state on every round rather
                // than welded into the dispatch continuation, so a crash between the outcome event and
                // this append loses nothing. Appending changes a paused step's projected
                // status from its terminal outcome to Paused, which must be reflected before readiness
                // is resolved — re-reading and re-projecting the freshly appended events is simpler than
                // threading that one status change through by hand.
                var pauseObligations = PauseEngine.GetPauseObligations(state, snapshot);
                if (pauseObligations.Count > 0)
                {
                    foreach (var (stepId, executionId) in pauseObligations)
                    {
                        await eventLogWriter.AppendAsync(new FlowEvent.WorkflowPaused(executionId, stepId), ioCancellationToken)
                            .ConfigureAwait(false);
                    }

                    continue;
                }

                // #1634/#1762: a poller-less pump (e.g. CancelCommand's DIRECT path) never delivers a
                // parked step's cancel through MarkArrestIntent/SettleArrestIntentsAsync (#1556),
                // so read the ledger directly here, before GetRetryObligations/
                // DependencyResolver.GetReadySteps get a chance to redispatch it instead. Sourced from
                // cancellationRequestedExecutionIds -- already Origin: Operator only, see its own
                // remarks -- filtered through IsParkedRetryTarget, the same terminal
                // SettleArrestIntentsAsync would produce. Also gated on !hostStopRequested, same
                // guard readyStepIds uses below -- why both this gate AND Origin: spec/baton.md §2.
                // Filters state.Steps directly
                // rather than the accumulator's own HashSet -- state.Steps is itself built by
                // iterating snapshot.Steps in order (StateProjector), so this gives the
                // ExecutionCancelled appends below the same deterministic-emission discipline the
                // ready-step loop further down uses, with no separate join back through
                // snapshot.Steps that could silently drop a match if that 1:1 shape ever changed.
                var parkedCancelExecutionIds = hostStopRequested
                    ? []
                    : state.Steps
                        .Where(s => s.LatestExecutionId is { } latestExecutionId
                            && cancellationRequestedExecutionIds.Contains(latestExecutionId)
                            && IsParkedRetryTarget(state, latestExecutionId))
                        .Select(s => s.LatestExecutionId!.Value)
                        .ToList();
                if (parkedCancelExecutionIds.Count > 0)
                {
                    foreach (var executionId in parkedCancelExecutionIds)
                    {
                        await eventLogWriter.AppendAsync(new FlowEvent.ExecutionCancelled(executionId), ioCancellationToken)
                            .ConfigureAwait(false);
                    }

                    continue;
                }

                // A derived obligation (#712), re-evaluated from projected state on every round for
                // the same crash-safety reason the pause obligation above is: evaluated after pause obligations
                // and before readiness.
                var retryObligations = GetRetryObligations(
                    state, snapshot, timeProvider, jitterSource, settleOnVendorExhaustion, fallbackWorkerBindings, acceptedRequestByExecutionId);
                if (retryObligations.Count > 0)
                {
                    var (obligationPid, obligationStartTime) = GetCurrentEngineIdentity();
                    foreach (var obligation in retryObligations)
                    {
                        await eventLogWriter.AppendAsync(
                                new FlowEvent.StepRetryScheduled(
                                    obligation.StepId,
                                    obligation.ForExecutionId,
                                    obligation.RetryNotBefore,
                                    obligation.RetryDelayMs,
                                    obligationPid,
                                    obligationStartTime),
                                ioCancellationToken)
                            .ConfigureAwait(false);
                        engineStampedStepIds.Add(obligation.StepId);
                    }

                    continue;
                }

                // Once a host stop is underway, no newly-ready step should be dispatched — cancellation
                // is winding this call down, not making room for fresh work. The same applies to a
                // crash-recovery resubmission (M10 Phase 3): it is a brand-new dispatch to Core too.
                var now = timeProvider.GetUtcNow();
                var readyStepIds = hostStopRequested
                    ? (IReadOnlySet<StepId>)new HashSet<StepId>()
                    : DependencyResolver.GetReadySteps(state, snapshot, now);
                var toResubmit = hostStopRequested ? (IReadOnlyList<ExecutionId>)[] : crashRecovery.ToResubmit;

                // Snapshot declaration order, not the ready set's (unordered) iteration order, so a
                // round's intents are always emitted in the same sequence for the same FlowState
                // regardless of how concurrent dispatches later complete.
                foreach (var stepDefinition in snapshot.Steps)
                {
                    if (!readyStepIds.Contains(stepDefinition.StepId))
                    {
                        continue;
                    }

                    if (!workerBindings.TryGetValue(stepDefinition.Worker, out var binding))
                    {
                        throw new UnresolvedWorkerException(
                            $"No WorkerBinding registered for Worker '{stepDefinition.Worker}' (step '{stepDefinition.StepId}').");
                    }

                    // #802: a step parked on ExhaustedUntil with a declared, not-yet-tried fallback
                    // (GetRetryObligations already paced it to redispatch immediately rather than wait)
                    // dispatches on the fallback binding instead of the primary — same predicate,
                    // recomputed fresh from projected state rather than carried from that earlier call,
                    // so this stays correct across a crash-and-replay between the two.
                    var stepStateForDispatch = state.Steps.First(s => s.StepId == stepDefinition.StepId);
                    var fallbackBinding = ResolveVendorExhaustionFallback(
                        stepStateForDispatch, stepDefinition.Worker, fallbackWorkerBindings, acceptedRequestByExecutionId);
                    var previousBinding = binding;
                    if (fallbackBinding is not null)
                    {
                        binding = fallbackBinding;
                    }

                    // The write-sequence rule, extended to a concurrent round: each intent is appended
                    // and fsync'd here — awaited sequentially, in declaration order — before that step's
                    // own dispatch is even started, and before the next step's intent is written.
                    var prepared = await PrepareExecutionAsync(
                            workflowId, stepDefinition, snapshot, state, binding, artifactsRootPath, eventLogWriter, ioCancellationToken)
                        .ConfigureAwait(false);

                    if (fallbackBinding is not null && previousBinding is WorkerBinding.Process previousProcess)
                    {
                        // #802: the one room fact naming the original binding, the fallback binding and
                        // the reset time it rescued the step from waiting out — journaled AFTER the new
                        // ExecutionRequestAccepted above so StateProjector's StepRebound handler (which
                        // requires the execution id to already be accepted) has something to apply to;
                        // that apply is a same-value no-op here since the new request was already built
                        // from the fallback binding, so this line is diagnostic, not corrective.
                        await eventLogWriter.AppendAsync(
                                new FlowEvent.StepRebound(
                                    stepDefinition.StepId,
                                    prepared.Request.ExecutionId,
                                    PreviousAdapter: previousProcess.Adapter,
                                    PreviousModel: previousProcess.Model,
                                    NewAdapter: fallbackBinding.Adapter,
                                    NewModel: fallbackBinding.Model,
                                    Reason: "vendor-exhaustion fallback: "
                                        + $"{previousProcess.Adapter} parked until "
                                        + $"{stepStateForDispatch.LatestExecutionFailedRetryNotBefore?.ToString("O") ?? "unknown"}"),
                                ioCancellationToken)
                            .ConfigureAwait(false);
                    }

                    // A non-process worker is fully handled by the append above: no Core
                    // process to spawn, so nothing joins the in-flight set. The pump reaches its fixed
                    // point with the step awaiting external completion (no daemon); a later round's
                    // NonProcessCompletionDetector call is what eventually finalizes it.
                    if (binding is WorkerBinding.Process processBinding)
                    {
                        // Registered under its own token (M10 Phase 2) — never the ambient
                        // cancellationToken directly — so this specific execution, and only this one, can
                        // be signalled without touching a sibling dispatched in the same round.
                        var executionId = prepared.Request.ExecutionId;
                        var dispatchCancellationToken = inFlightExecutions.Register(executionId);

                        // Not awaited here: starts the dispatch and joins the in-flight set, so a slow
                        // step never blocks this round from dispatching the rest of its ready work.
                        inFlight.Add(DispatchAndRecordOutcomeAsync(
                            prepared, processBinding, eventLogWriter, dispatcher, inFlightExecutions, dispatchCancellationToken, timeProvider));
                    }
                }

                // M10 Phase 3's re-submission crash state: the same attempt, not a retry — the
                // intent is already durably recorded (ExecutionRequestAccepted), so this re-dispatches
                // the existing request as-is rather than calling PrepareExecutionAsync, which would
                // append a new one and charge a fresh ExecutionId against nothing.
                foreach (var executionId in toResubmit)
                {
                    var request = acceptedRequestByExecutionId[executionId];
                    var processBinding = (WorkerBinding.Process)workerBindings[request.Worker];

                    // #1583 (spec/baton.md §3, pulling S6 / #802 section 3.3 forward): when the resubmit's current binding differs
                    // from the request's recorded Adapter/Model, journal FlowEvent.StepRebound naming old->new
                    // before dispatching so that usage projection attributes this execution to the new binding.
                    // request.Adapter is null both for a pre-#1567 journal line (no Adapter field existed yet)
                    // and for a real rebind's dropped model string (#1082) — the two are told apart by Model:
                    // a pre-#1567 line has neither field recorded, so require both null before treating the
                    // absence as "no prior binding recorded" rather than a divergence to journal.
                    var isLegacyUnrecordedBinding = request.Adapter is null && request.Model is null;
                    if (!isLegacyUnrecordedBinding
                        && (request.Adapter != processBinding.Adapter || request.Model != processBinding.Model))
                    {
                        var stepId = request.StepId
                            ?? throw new InvalidRoomMutationException(
                                $"Crash-recovery resubmit for execution {executionId} has no recorded StepId; a step-less request must never reach the resubmit loop.");
                        await eventLogWriter.AppendAsync(
                            new FlowEvent.StepRebound(
                                stepId,
                                executionId,
                                PreviousAdapter: request.Adapter,
                                PreviousModel: request.Model,
                                NewAdapter: processBinding.Adapter,
                                NewModel: processBinding.Model,
                                Reason: "crash-recovery resubmit: binding changed since accept"),
                            ioCancellationToken).ConfigureAwait(false);

                        request = request with
                        {
                            Adapter = processBinding.Adapter,
                            Model = processBinding.Model,
                        };
                        acceptedRequestByExecutionId[executionId] = request;
                    }

                    var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRootPath, executionId);
                    var prepared = new PreparedExecution(request, outputDirectory);

                    var dispatchCancellationToken = inFlightExecutions.Register(executionId);
                    inFlight.Add(DispatchAndRecordOutcomeAsync(
                        prepared, processBinding, eventLogWriter, dispatcher, inFlightExecutions, dispatchCancellationToken, timeProvider));
                }

                if (inFlight.Count == 0)
                {
                    // A round that dispatched only non-process work still changed projected state (new
                    // ExecutionRequestAccepted events) even though nothing joined inFlight — loop back
                    // around to re-project and return the state that actually reflects it, rather than
                    // the stale snapshot read at the top of this iteration.
                    if (readyStepIds.Count > 0)
                    {
                        continue;
                    }

                    // Only a deadline still ahead justifies waiting, measured against the same `now`
                    // the resolver just used. A deferral whose deadline has already passed while its
                    // step stayed un-ready is blocked on something other than time — a dependency
                    // superseded and then terminally failed — and a passed deadline can never become
                    // ready by waiting, so treating it as waitable turns this branch into a zero-delay
                    // spin (delay <= 0, continue, re-project, repeat). With no future deadline, no
                    // ready step and nothing in flight, this state IS the pump's fixed point.
                    var pendingDeferralSteps = state.Steps
                        .Where(s => s.RetryNotBefore is not null && s.RetryNotBefore.Value > now)
                        .ToList();
                    var pendingDeferrals = pendingDeferralSteps
                        .Select(s => s.RetryNotBefore!.Value)
                        .ToList();

                    if (pendingDeferrals.Count > 0 && !hostStopRequested && !state.Steps.Any(s => s.Status == StepStatus.Paused))
                    {
                        // #1577: engineStampedStepIds' own remarks above have why. Renews a step's
                        // StepRetryScheduled (same schedule, this process's identity) the first time
                        // this call finds it already pending rather than having just scheduled it.
                        var stepsToRenew = pendingDeferralSteps
                            .Where(s => !engineStampedStepIds.Contains(s.StepId))
                            .ToList();
                        if (stepsToRenew.Count > 0)
                        {
                            var (renewPid, renewStartTime) = GetCurrentEngineIdentity();
                            foreach (var s in stepsToRenew)
                            {
                                await eventLogWriter.AppendAsync(
                                        new FlowEvent.StepRetryScheduled(
                                            s.StepId,
                                            s.RetryScheduledForExecutionId!.Value,
                                            s.RetryNotBefore!.Value,
                                            s.RetryDelayMs ?? 0,
                                            renewPid,
                                            renewStartTime),
                                        ioCancellationToken)
                                    .ConfigureAwait(false);
                                engineStampedStepIds.Add(s.StepId);
                            }
                        }

                        var minNotBefore = pendingDeferrals.Min();
                        var nowAtCheck = timeProvider.GetUtcNow();
                        var delay = minNotBefore - nowAtCheck;

                        if (delay > TimeSpan.Zero)
                        {
                            // #1094: surface a vendor-quota park to the foreground before the (possibly
                            // day-long) paced wait, so it does not read as a hang. Ordinary retry backoff
                            // is not a quota park and stays quiet. Notification only — the 0026 wait below
                            // is unchanged.
                            var quotaParkStep = state.Steps.FirstOrDefault(s => s.RetryNotBefore == minNotBefore
                                && s.LatestFailureClassification == FailureClassification.ExhaustedUntil);
                            if (onVendorQuotaPark is not null && quotaParkStep is not null)
                            {
                                // #1183: deduped on the RAW vendor-reported instant
                                // (LatestExecutionFailedRetryNotBefore), not the paced `minNotBefore` —
                                // PastResetInstantRetryFloor recomputes a fresh `now + 1s` obligation on
                                // every retry of a repeating stale instant, so deduping on the paced value
                                // would re-notify (and re-print) once per second forever instead of once
                                // per distinct vendor refusal.
                                var dedupeInstant = quotaParkStep.LatestExecutionFailedRetryNotBefore ?? minNotBefore;
                                if (lastQuotaParkNotified != dedupeInstant)
                                {
                                    lastQuotaParkNotified = dedupeInstant;
                                    onVendorQuotaPark(minNotBefore);
                                }
                            }

                            // #1183: Task.Delay's TimeSpan overload throws past ~49.7 days -- clamp
                            // to a chunk and let the loop's `continue` below re-check readiness and
                            // re-issue the remainder, rather than trust `delay` to already be sane.
                            // GetRetryObligations caps every obligation it schedules, so this is
                            // belt-and-suspenders for the wait itself, not the only guard.
                            var chunkedDelay = delay > MaxParkWaitChunk ? MaxParkWaitChunk : delay;
                            var delayTask = Task.Delay(chunkedDelay, timeProvider, ioCancellationToken);
                            // #1767: fires after nowAtCheck's clock read and the delay task's
                            // construction above, never before — a test awaiting this signal is
                            // guaranteed the pump has already re-armed on the current time.
                            onDeferralWaitArmed?.Invoke();
                            var deferralHostStopWatcher = cancellationToken.CanBeCanceled
                                ? Task.Delay(Timeout.Infinite, cancellationToken)
                                : null;

                            // #1556 (generalized from #1563's narrower quota-parked-only latch):
                            // captured fresh on every entry into this wait, never reused — a
                            // cancel.request the poller could not deliver through the registry above
                            // (no live process for the target: non-process work, a step-less
                            // execution, or the worker already exited on a quota park) marks this
                            // same latch (also wired into the busy `waitCandidates` wait below, for
                            // the sibling-still-in-flight shape), so a park that would otherwise sit
                            // until `delayTask` — possibly a day out on a vendor quota reset — wakes
                            // on the next round instead. The drain itself happens at the top of the
                            // next round (the arrest-intent derived-obligation block below), not here
                            // — this wait's only job is to wake for it.
                            var arrestWake = inFlightExecutions.NextArrestWake();

                            var deferralCandidates = new List<Task> { delayTask, arrestWake };
                            if (deferralHostStopWatcher is not null)
                            {
                                deferralCandidates.Add(deferralHostStopWatcher);
                            }

                            var completedWait = await Task.WhenAny(deferralCandidates).ConfigureAwait(false);
                            // The delay task and the watcher cancel off the same host token, so a host
                            // stop can complete the *delay* task first and WhenAny returns it instead of
                            // the watcher. Reaching the token directly closes that race: without it, the
                            // next round's tail read returns synchronously when the log has no new bytes
                            // (no awaited token observation anywhere in the round), both tasks arrive
                            // here already cancelled, WhenAny picks the delay task again, and the loop
                            // spins without ever noticing the stop (Test12's 30s timeout under load).
                            // F1 sub-point (#1605 review): this guard wins the race over
                            // `arrestWake` below whenever both fire around the same instant — an
                            // arrest mark landing in the same tick as a host stop is dropped:
                            // this call returns without ever draining it, RequestStopAsync below only
                            // reaches a live process's CancellationTokenSource (a non-process or
                            // parked target has none), and the in-memory mark itself does not survive
                            // process exit.
                            // Accepted, not fixed: this pump call is exiting either way, the target
                            // was never going to settle through it once a host stop lands, and
                            // CancelRequestFile.DeleteStalePendingRequestAsync sweeps this still-pending
                            // request file on the room's next `baton run` once it can confirm (#1649)
                            // the request predates that run and its writer is no longer alive — so the
                            // worst case is the operator re-issuing `baton cancel` once that run starts,
                            // not a request that silently vanishes with no trace.
                            if (completedWait == deferralHostStopWatcher || cancellationToken.IsCancellationRequested)
                            {
                                hostStopRequested = true;
                                ioCancellationToken = CancellationToken.None;
                                await inFlightExecutions.RequestStopAsync(CancellationToken.None).ConfigureAwait(false);
                            }
                            else if (completedWait == arrestWake)
                            {
                                // F8 (#1605 review): reset BEFORE the next round's drain, load-bearing,
                                // not incidental ordering. A mark landing between this reset and the
                                // drain (top of the round the `continue` below re-enters) still lands
                                // safely either way: ResetArrestWake only swaps in a fresh latch
                                // if the one it is given is still current, so a mark racing this reset
                                // either signals the brand-new latch (caught next round instantly,
                                // since it is already complete) or still lands in the set that drain
                                // reads. Drain-then-reset would instead let that same mark
                                // signal the OLD, already-fired latch (a no-op — TrySetResult on a
                                // completed TCS does nothing) and then get its own fresh latch swapped
                                // out from under it by the reset that follows, stalling the intent
                                // until some unrelated future mark happens to notice it pending.
                                inFlightExecutions.ResetArrestWake(arrestWake);
                            }

                            continue;
                        }
                        else
                        {
                            continue;
                        }
                    }

                    // #1556: not actually a fixed point if an arrest intent landed after this round's
                    // own drain (SettleArrestIntentsAsync, above) and before this exact instant —
                    // returning here releases the concurrency guard AND (via RunCommand's poller
                    // lifetime) kills the poller that could otherwise re-offer the mark, so an
                    // undrained intent would be silently dropped rather than merely delayed. Same
                    // "this round is not actually done" shape as the readyStepIds check above.
                    if (inFlightExecutions.HasPendingArrestIntents())
                    {
                        continue;
                    }

                    if (latestCheckpoint is not null)
                    {
                        // Write pruned merged core aggregates into the checkpoint before saving.
                        // Invariant note: Carrying core aggregates in the checkpoint removes the reliance on saving
                        // checkpoints only at clean pump return (after in-flight stops record exits) for correctness.
                        // However, saving at clean return remains a performance assumption to avoid unneeded disk writes.
                        var (prunedStarted, prunedExited) = CoreEventAggregation.Prune(mergedStarted, mergedExited, state);
                        latestCheckpoint = latestCheckpoint with
                        {
                            State = latestCheckpoint.State with
                            {
                                CoreStartedExecutionIds = prunedStarted,
                                CoreExitedByExecutionId = prunedExited
                            }
                        };
                        ProjectionCheckpointStore.Save(roomDirectoryPath, latestCheckpoint);
                    }

                    return state;
                }

                // Races the round's in-flight dispatches against the host token itself (M10 Phase 2): a
                // Task.Delay(Timeout.Infinite, ...) never completes on its own, only transitions to
                // Canceled the instant cancellationToken fires, which Task.WhenAny treats as "done" —
                // exactly the wakeup a host-initiated stop needs without polling.
                var hostStopWatcher = !hostStopRequested && cancellationToken.CanBeCanceled
                    ? Task.Delay(Timeout.Infinite, cancellationToken)
                    : null;
                var waitCandidates = new List<Task>(inFlight);
                if (hostStopWatcher is not null)
                {
                    waitCandidates.Add(hostStopWatcher);
                }

                // #1556 (generalized from #1563's narrower quota-parked-only latch): the same wake
                // this loop's idle-deferral branch watches, needed here too — a DIFFERENT step's
                // arrest (non-process, step-less, or quota-parked) while THIS step's dispatch is
                // still in flight would otherwise only wake on that dispatch completing, a host stop,
                // or `deferralWakeup` below (which fires at the very deadline the cancel exists to
                // end early) — reachable review finding: a workflow with any sibling step running
                // concurrently reopens the exact bug #1563 fixed for the parked case. Captured fresh
                // every entry into this wait, same as the idle branch, so a mark landing anywhere
                // before capture is never lost.
                var waitArrestWake = inFlightExecutions.NextArrestWake();
                waitCandidates.Add(waitArrestWake);

                // A deferral deadline must wake this wait too, not only the idle branch above: a
                // deferred retry whose sibling is still mid-flight would otherwise sleep until that
                // sibling completes, stretching a sub-second backoff to the sibling's full runtime.
                // The timer only wakes the loop — releasing the step stays the resolver's decision on
                // the re-projection after `continue`, same as the idle branch.
                Task? deferralWakeup = null;
                if (!hostStopRequested)
                {
                    var pendingRetryDeadlines = state.Steps
                        .Where(s => s.RetryNotBefore is not null)
                        .Select(s => s.RetryNotBefore!.Value)
                        .ToList();
                    if (pendingRetryDeadlines.Count > 0)
                    {
                        var wakeDelay = pendingRetryDeadlines.Min() - timeProvider.GetUtcNow();
                        if (wakeDelay > TimeSpan.Zero)
                        {
                            // #1183: same clamp as the idle branch's delayTask above -- an early
                            // wakeup here is harmless, `completed == deferralWakeup` below already
                            // just `continue`s to re-check readiness against the real deadline.
                            var chunkedWakeDelay = wakeDelay > MaxParkWaitChunk ? MaxParkWaitChunk : wakeDelay;
                            deferralWakeup = Task.Delay(chunkedWakeDelay, timeProvider, ioCancellationToken);
                            waitCandidates.Add(deferralWakeup);
                            // #1767: same signal as the idle branch's onDeferralWaitArmed call above —
                            // fires after this branch's own clock read (timeProvider.GetUtcNow() feeding
                            // wakeDelay) and re-arm, never before.
                            onDeferralWaitArmed?.Invoke();
                        }
                    }
                }

                var completed = await Task.WhenAny(waitCandidates).ConfigureAwait(false);
                // Same shared-token race as the idle branch's wait (see the comment there): the
                // wakeup must not swallow a host stop it lost the WhenAny race to. Unlike there,
                // losing this race is self-recovering (the watcher precedes the wakeup in the
                // candidate list, and a cancelled-token append refuses before any post-stop
                // dispatch could land) — the guard buys symmetry and one round of latency, not a
                // hang fix.
                if (completed == deferralWakeup && !cancellationToken.IsCancellationRequested)
                {
                    continue;
                }
                if (completed == hostStopWatcher || (completed == deferralWakeup && cancellationToken.IsCancellationRequested))
                {
                    hostStopRequested = true;

                    // From here on every read/write this loop performs must survive the now-cancelled
                    // ambient token so the pump can still converge (see ioCancellationToken's own
                    // remarks above).
                    ioCancellationToken = CancellationToken.None;

                    // Intent-first, for every execution still in flight, before any of them is signalled —
                    // RequestStopAsync itself enforces that ordering.
                    await inFlightExecutions.RequestStopAsync(CancellationToken.None).ConfigureAwait(false);
                    continue;
                }
                if (completed == waitArrestWake)
                {
                    // F8 (#1605 review): reset-before-the-next-round's-drain is load-bearing here too
                    // — see the same ordering's full explanation at this loop's idle-deferral branch
                    // above (`arrestWake`'s own ResetArrestWake call).
                    inFlightExecutions.ResetArrestWake(waitArrestWake);
                    continue;
                }

                inFlight.Remove(completed);
                await completed.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!hostStopRequested && cancellationToken.IsCancellationRequested)
            {
                // A host stop is a request to converge, not to crash — but the two parked waits
                // above are the only places that used to translate the ambient token into the
                // graceful path (hostStopRequested → RequestStopAsync → converge on a no-cancel
                // token). A cancel landing anywhere else — the loop-top log read, a dispatch
                // preparation — surfaced as OperationCanceledException and killed the pump with
                // in-flight processes never told to stop (#718). Route every ambient-token
                // cancellation into the same graceful path instead; `inFlight` and the registry
                // live outside the loop, so the next round still owns and awaits everything
                // already running.
                hostStopRequested = true;
                ioCancellationToken = CancellationToken.None;
                await inFlightExecutions.RequestStopAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static async Task<PreparedExecution> PrepareExecutionAsync(
        WorkflowId workflowId,
        WorkflowStepDefinition step,
        WorkflowDefinitionSnapshot snapshot,
        FlowState state,
        WorkerBinding binding,
        string artifactsRootPath,
        IEventLogWriter eventLogWriter,
        CancellationToken cancellationToken)
    {
        var stateByStepId = state.Steps.ToDictionary(s => s.StepId);

        var executionId = new ExecutionId(Guid.NewGuid().ToString("n"));
        var inputPaths = ArtifactManager.ResolveInputPaths(step, snapshot, state, artifactsRootPath);
        var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRootPath, executionId);

        // A RetryWithRevision/Supersede consequence still owed to this step carries its
        // supplement into this dispatch — a projected fact, so this holds whether this round is the
        // decision's immediate consequence or a replay resuming after a crash between the two.
        var supplementaryInputPath = stateByStepId[step.StepId].PendingSupplementaryExecutionId is { } supplementaryExecutionId
            ? ArtifactManager.ResolveSupplementaryInputPath(artifactsRootPath, supplementaryExecutionId)
            : null;
        var environment = ArtifactManager.BuildEnvironment(inputPaths, outputDirectory, artifactsRootPath, supplementaryInputPath);

        var upstreamExecutionIds = new Dictionary<StepId, ExecutionId>();
        foreach (var dependencyStepId in step.DependsOn)
        {
            // The Dependency Resolver's condition 1 already guarantees every DependsOn entry has a
            // successful execution — LatestExecutionId is never null here.
            upstreamExecutionIds[dependencyStepId] = stateByStepId[dependencyStepId].LatestExecutionId!.Value;
        }

        var processBindingForRequest = binding as WorkerBinding.Process;
        var (hookCanaryArmed, hookVerdictLedgerFileName) = CaptureHookCanaryArmingFields(processBindingForRequest);

        var request = new ExecutionRequest(
            executionId,
            workflowId,
            step.StepId,
            step.Worker,
            inputPaths,
            step.Outputs,
            processBindingForRequest?.Timeout,
            environment,
            upstreamExecutionIds,
            GrantAuditMode: binding.GrantAuditMode,
            Adapter: processBindingForRequest?.Adapter,
            Model: processBindingForRequest?.Model,
            HookCanaryArmed: hookCanaryArmed,
            HookVerdictLedgerFileName: hookVerdictLedgerFileName);


        // #1373: built from the step as projected BEFORE the accept below is appended, which is what
        // leaves LatestFailureReason still naming the predecessor's timeout. Null for a first attempt,
        // for an ordinary failure's retry, and for a non-process worker (nothing spawns, so there is no
        // prompt to prepend to). A `baton resume` dispatch mints its own request in RecordResumeAsync
        // and never reaches here -- deliberately: a resume carries the operator's own message, and
        // RetryEngine never auto-retries a resumed step anyway (StepState.LinkedFromExecutionId).
        var continuationBrief = processBindingForRequest is null
            ? null
            : Scheduling.ContinuationBrief.ForRetryAfterTimeout(
                stateByStepId[step.StepId], step.RetryPolicy.MaxAttempts, processBindingForRequest.Timeout);

        // The write-sequence rule: intent recorded and fsync'd before Core is ever asked to run.
        await eventLogWriter.AppendAsync(CreateExecutionRequestAccepted(request), cancellationToken)
            .ConfigureAwait(false);

        return new PreparedExecution(request, outputDirectory, continuationBrief);
    }

    // #1741: the one fact every Process-dispatch ExecutionRequest construction site must journal --
    // see ExecutionRequest.HookCanaryArmed's own doc for why (spec/baton.md §9). Shared so the two
    // sites (a fresh step dispatch here, a `baton resume` dispatch in RecordResumeAsync) can't drift
    // the way the #1753 review found RecordResumeAsync had.
    private static (bool? HookCanaryArmed, string? HookVerdictLedgerFileName) CaptureHookCanaryArmingFields(
        WorkerBinding.Process? processBinding) =>
        (processBinding?.Target.CountHookVerdicts is not null, processBinding?.Target.HookVerdictLedgerFileName);

    private static (int Pid, DateTimeOffset StartTime) GetCurrentEngineIdentity()
    {
        var pid = Environment.ProcessId;
        var startTime = new DateTimeOffset(Process.GetCurrentProcess().StartTime).ToUniversalTime();
        return (pid, startTime);
    }

    private static FlowEvent.ExecutionRequestAccepted CreateExecutionRequestAccepted(ExecutionRequest request)
    {
        var (pid, startTime) = GetCurrentEngineIdentity();
        return new FlowEvent.ExecutionRequestAccepted(request, pid, startTime);
    }

    private static async Task DispatchAndRecordOutcomeAsync(
        PreparedExecution prepared,
        WorkerBinding.Process binding,
        IEventLogWriter eventLogWriter,
        ICoreDispatcher dispatcher,
        InFlightExecutionRegistry inFlightExecutions,
        CancellationToken dispatchCancellationToken,
        TimeProvider? timeProvider = null)
    {
        try
        {
            // #1586 S1: the same recorded-adapter preference ExecutionUsageProjector's own #1567
            // comment explains — prepared.Request.Adapter, not the binding, so this site and the
            // crash-recovery site below both read the same source (identical value here, since
            // prepared.Request.Adapter is frozen from this binding at preparation).
            var usageParser = prepared.Request.Adapter is { } liveAdapter
                ? StandardWorkerUsageParsers.Default.GetValueOrDefault(liveAdapter)
                : null;

            // #1623 ruling addendum: a live token-budget watch, wired the same way
            // CoreDispatcher.DetectsTerminalSuccess composes onto an existing OnStdoutLine sink —
            // never replacing whatever a caller (e.g. the M24 live-streaming seam) already wired.
            // Only possible when the adapter is known: with no usage parser there is nothing to read
            // usage from, so an execution with a role budget but an unrecognized adapter simply runs
            // unwatched rather than refusing to dispatch.
            TokenBudgetMonitor? budgetMonitor = null;
            var target = binding.Target;

            // #1373: applied before every other `target with` rewrite below, and to the ARGUMENT the
            // worker is invoked with as well as the archival PromptText -- see
            // CoreDispatchTarget.WithPromptPreamble for why prepending to only one of the two would put
            // the brief in prompt.txt and nowhere the worker can read it.
            if (prepared.ContinuationBrief is { } continuationBrief)
            {
                target = target.WithPromptPreamble(continuationBrief);
            }
            // #1682: a monitor now arms on EITHER trigger existing -- a role with only a tool-step cap
            // and no token budget still watches, where before this issue a budget was required for a
            // monitor to be constructed at all.
            // #1691: the billed-rate trigger joins the same disjunction -- a dispatch carrying only
            // --billed-rate-limit still watches.
            if ((binding.TokenBudget is not null || binding.MaxToolSteps is not null || binding.BilledRateLimit is not null)
                && usageParser is not null)
            {
                budgetMonitor = new TokenBudgetMonitor(
                    binding.TokenBudget, binding.MaxToolSteps, binding.BilledRateLimit, usageParser);
                var innerOnStdoutLine = target.OnStdoutLine;
                target = target with
                {
                    OnStdoutLine = line =>
                    {
                        innerOnStdoutLine?.Invoke(line);
                        budgetMonitor.OnStdoutLine(line);
                    },
                };
            }

            using var linkedCancellation = budgetMonitor is not null
                ? CancellationTokenSource.CreateLinkedTokenSource(dispatchCancellationToken, budgetMonitor.ArrestRequested)
                : null;
            var effectiveCancellationToken = linkedCancellation?.Token ?? dispatchCancellationToken;

            // #1708 H1/M1: the workspace's REVIEWED .baton/verify is read HERE, before the worker is
            // spawned -- not in the verify block below, which runs against a working tree the worker has
            // just had write access to. Both halves matter: the merge-base with origin/main (so neither
            // an edit to the working tree nor a commit on the lane's own branch is inside it) and
            // pre-dispatch. See VerifyCommandResolver.ReadCommittedRepoDeclarationAsync for what a failed
            // read falls back to, and for the one shape (no merge-base) that is announced as unreviewed.
            var committedVerify = await VerifyCommandResolver
                .ReadCommittedRepoDeclarationAsync(binding.Target.WorkingDirectory, dispatchCancellationToken)
                .ConfigureAwait(false);
            var committedVerifyDeclaration = committedVerify.CommandLine;
            if (committedVerify.Unreviewed)
            {
                await eventLogWriter.AppendAsync(
                    new FlowEvent.VerifyDeclarationUnreviewed(
                        prepared.Request.ExecutionId,
                        VerifyCommandResolver.DeclarationDigest(committedVerifyDeclaration)),
                    CancellationToken.None).ConfigureAwait(false);
            }

            // F4 (#1593 review): only an ACTUALLY-provisioned worktree, never the operator's own
            // repository — see WorkerBinding.Process.IsWorktree's remarks.
            var worktreePath = binding.IsWorktree ? binding.Target.WorkingDirectory : null;
            // #1622/#1390: deliberately NOT gated on binding.IsWorktree the way worktreePath above is —
            // see OutcomeClassifier.Classify's own changesTreeWorkingDirectory parameter doc for why.
            var changesTreeWorkingDirectory = binding.ChangesTree ? binding.Target.WorkingDirectory : null;

            // #1373: read HERE, before the worker is spawned, because "new commits since this attempt
            // started" has no meaning read afterwards. Best-effort by construction (ResolveBaseCommit
            // returns null on any git failure rather than throwing), and a null only costs the probe its
            // exact commit count -- OutcomeClassifier.Classify falls back to the worktree's provisioned
            // base and then to the reflog heuristic, both of which still answer in the fail-closed
            // direction. Off the intent-append path deliberately: this shells out to git, and the loop
            // that appends ExecutionRequestAccepted for a whole round must not wait on one.
            var mutationProbePath = worktreePath ?? changesTreeWorkingDirectory;
            var workspaceHeadShaAtStart = mutationProbePath is null
                ? null
                : Workspaces.WorktreeProvisioner.ResolveBaseCommit(mutationProbePath, "HEAD");

            // #1373 follow-up (spec/baton.md §3): journaled here, still off the round's own
            // intent-append loop -- this method runs per-execution, not inside PrepareExecutionAsync's
            // loop above. See FlowEvent.ExecutionAttemptStarted's own remarks for why. Never appended
            // when there is no mutation-probe path -- nothing for a recovered classification to
            // compare against.
            if (workspaceHeadShaAtStart is not null)
            {
                await eventLogWriter.AppendAsync(
                        new FlowEvent.ExecutionAttemptStarted(prepared.Request.ExecutionId, workspaceHeadShaAtStart),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }

            // Rests on ICoreDispatcher's contract that cancellation via its token argument comes back
            // as a normal CoreDispatchResult (CoreExitReason.CancelRequested), never as
            // OperationCanceledException — CoreDispatcher converts BatonCancelException two layers
            // down, agnostic to which linked source actually fired. If an implementation (or a test
            // double) ever let OCE escape here, the outcome append below would be skipped and, with
            // the ambient token also cancelled, the pump's round-level catch would absorb the
            // evidence. There is deliberately no local catch: that would convert a contract violation
            // into a fabricated outcome.
            var dispatchResult = await dispatcher.DispatchAsync(prepared.Request, target, effectiveCancellationToken)
                .ConfigureAwait(false);

            // #1929 review MEDIUM: the room's own record of what AER placed in the worker's working
            // directory before spawning it, and (the HIGH's escape clause) of which exact paths the
            // classification below therefore excludes from its work-product evidence. Appended from what
            // was WRITTEN, never from the plan — see FlowEvent.EngineFilesPlaced's own doc.
            if (dispatchResult.EnginePlacedPaths is { Count: > 0 } placedPaths)
            {
                await eventLogWriter.AppendAsync(
                        new FlowEvent.EngineFilesPlaced(
                            prepared.Request.ExecutionId, placedPaths, dispatchResult.EnginePlacedGroups ?? []),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }

            if (budgetMonitor is { Arrested: true })
            {
                // The budget's own token fired, not the caller's dispatchCancellationToken -- an
                // engine-initiated arrest, never operator intent, so this is FlowEvent.ExecutionArrested,
                // never FlowEvent.CancellationRequested/ExecutionCancelled (those mean the operator
                // asked). No OutcomeClassifier.Classify call at all: classifying a cancelled-out-from-
                // under-it process would only produce a Cancelled/Failed verdict that this replaces
                // wholesale, never Succeeded.
                await eventLogWriter.AppendAsync(
                    new FlowEvent.ExecutionArrested(
                        prepared.Request.ExecutionId,
                        budgetMonitor.SnapshotUsage(),
                        budgetMonitor.SnapshotLastToolNames(),
                        budgetMonitor.ArrestReasonValue,
                        budgetMonitor.SnapshotToolStepCount(),
                        // #1691: recorded on EVERY arrest, not only a BilledRate one -- see
                        // TokenBudgetMonitor.SnapshotPeakBilledInWindow for why.
                        budgetMonitor.SnapshotPeakBilledInWindow(),
                        binding.BilledRateLimit,
                        // #1745: same recorded-adapter preference as usageParser above -- the LIVE
                        // adapter this execution actually ran on, not binding.Adapter (the CATALOG's
                        // pre-crash-recovery value), so a rebound execution's arrest text names the
                        // vendor whose figure actually fired.
                        prepared.Request.Adapter),
                    CancellationToken.None).ConfigureAwait(false);
                return;
            }

            // The request's mode was set from this binding at preparation; null can only mean a
            // request shape that predates the mode, and those were never promised an audit.
            var grantAuditMode = prepared.Request.GrantAuditMode ?? GrantAuditMode.Enforced;

            // #1680/#1732 review WIRING: the first-verdict canary's two counts. Both stay null unless
            // this dispatch's own CoreDispatchTarget carries a live CountHookVerdicts delegate --
            // AgyWorkerAdapter.Resolve only wires that up for an agy grant whose PreToolUse hook is the
            // sole narrowing (RequiresHookAsSoleNarrowing), so a claude binding or a fully-granted agy
            // one keeps passing null/null here exactly like every call site before this PR (Adapter
            // Isolation: this file never names "agy" -- the vendor decided applicability at resolve
            // time, this file only asks the target it was handed).
            int? toolCallCount = null;
            int? hookVerdictCount = null;
            if (target.CountHookVerdicts is { } countHookVerdicts)
            {
                toolCallCount = CountToolCallsFromStdoutLog(usageParser, prepared.OutputDirectory);
                hookVerdictCount = countHookVerdicts(prepared.OutputDirectory);
            }

            var classification = OutcomeClassifier.Classify(
                dispatchResult, binding.Contract, prepared.OutputDirectory, binding.FailureClassifier, timeProvider,
                grantAuditMode, worktreePath, binding.ResponseParser, usageParser, binding.WorktreeBaseSha, binding.ChangesTree,
                changesTreeWorkingDirectory, toolCallCount, hookVerdictCount, workspaceHeadShaAtStart);

            // #1623 (contract: spec/baton.md §3): the engine's own verify
            // step, spawned here -- between Classify returning Succeeded and the outcome event append
            // below -- rather than inside OutcomeClassifier.Classify itself, because Classify also runs
            // on the crash-recovery ToClassify branch (PumpToFixedPointAsync above) replaying a
            // recorded exit from a possibly-defunct workspace; a real subprocess belongs only on the
            // live-dispatch path.
            // #1702: the resolution order lives on VerifyCommandResolver's own doc, not restated here.
            // #1708 H1: the repo-declaration arm is the pre-dispatch committed snapshot above; a
            // redispatch still re-reads it (a fresh dispatch takes a fresh snapshot), which is the
            // no-stale-command property spec/baton.md §3 states.
            // #1708 L1: appended on DRIFT, whatever the verdict -- not only on a Succeeded execution,
            // and whatever the precedence outcome, including when --verify would have won anyway. The
            // operator-facing fact is "the file in your workspace is not what graded this run", which is
            // true either way; spec/baton.md §3 states why it is owed after a failed, arrested or
            // cancelled run too.
            var workingTreeDeclaration = VerifyCommandResolver.ReadWorkingTreeRepoDeclaration(binding.Target.WorkingDirectory);
            if (!string.Equals(workingTreeDeclaration, committedVerifyDeclaration, StringComparison.Ordinal))
            {
                await eventLogWriter.AppendAsync(
                    new FlowEvent.VerifyDeclarationIgnored(
                        prepared.Request.ExecutionId,
                        VerifyCommandResolver.DeclarationDigest(committedVerifyDeclaration),
                        VerifyCommandResolver.DeclarationDigest(workingTreeDeclaration)),
                    CancellationToken.None).ConfigureAwait(false);
            }

            ResolvedVerifyCommand? resolvedVerify = classification.Verdict == OutcomeVerdict.Succeeded
                ? VerifyCommandResolver.Resolve(
                    committedVerifyDeclaration, binding.VerifyCommandOverride, binding.VerifyPixiTask)
                : null;

            if (resolvedVerify is not null)
            {
                var (runnable, notRunnableReason) = await VerifyCommandResolver.CheckRunnableAsync(
                    resolvedVerify, binding.Target.WorkingDirectory, dispatchCancellationToken).ConfigureAwait(false);
                if (!runnable)
                {
                    // #1702: see FlowEvent.VerifyNotRun's own doc for what this settles to and why.
                    // No VerifyStarted here: it never started.
                    await eventLogWriter.AppendAsync(
                        new FlowEvent.VerifyNotRun(prepared.Request.ExecutionId, notRunnableReason ?? "verify command not runnable"),
                        CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    await eventLogWriter.AppendAsync(new FlowEvent.VerifyStarted(prepared.Request.ExecutionId), CancellationToken.None)
                        .ConfigureAwait(false);
                    var verifyOutcome = await VerifyRunner.RunProcessAsync(
                        resolvedVerify.Program, resolvedVerify.Args, binding.Target.WorkingDirectory, dispatchCancellationToken)
                        .ConfigureAwait(false);
                    if (verifyOutcome.Passed)
                    {
                        await eventLogWriter.AppendAsync(new FlowEvent.VerifyPassed(prepared.Request.ExecutionId), CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    else if (verifyOutcome.Kind == VerifyFailedKind.Cancelled && dispatchCancellationToken.IsCancellationRequested)
                    {
                        // The operator's own cancel landed inside the verify window: VerifyStarted above
                        // stays as the diagnostic record of what was running, but the settlement is
                        // ExecutionCancelled, not VerifyFailed -- the journal *can* decide here (it holds
                        // the cancel), so this must not fall into ApplyIndeterminate's retry-foreclosed,
                        // no-discharge-verb path (#1623 re-review N3). A verify TIMEOUT still settles
                        // Indeterminate via the VerifyFailed branch below -- only an operator-driven cancel
                        // gets this arm.
                        await eventLogWriter.AppendAsync(
                            new FlowEvent.ExecutionCancelled(prepared.Request.ExecutionId),
                            CancellationToken.None).ConfigureAwait(false);
                        return;
                    }
                    else if (verifyOutcome.Kind == VerifyFailedKind.BuildLockBusy)
                    {
                        // #1796: see FlowEvent.VerifyNotRun.BuildLockBusy's own doc for the condition
                        // this reports and why it settles differently from the VerifyFailed branch
                        // below. VerifyStarted already fired above, distinguishing this from the
                        // pre-flight not-run shape appended earlier in this method.
                        await eventLogWriter.AppendAsync(
                            new FlowEvent.VerifyNotRun(
                                prepared.Request.ExecutionId,
                                verifyOutcome.NotRunReason ?? "build lock busy",
                                BuildLockBusy: true),
                            CancellationToken.None).ConfigureAwait(false);
                        return;
                    }
                    else
                    {
                        // Never a blind retry (the ruling's own words): this IS the terminal event for this
                        // execution -- no FlowEvent.ExecutionSucceeded, no ZeroOutputsTripwire check, the
                        // step settles Indeterminate via StateProjector.ApplyIndeterminate instead.
                        await eventLogWriter.AppendAsync(
                            new FlowEvent.VerifyFailed(
                                prepared.Request.ExecutionId,
                                verifyOutcome.FailingMembers,
                                verifyOutcome.Tail,
                                verifyOutcome.Kind ?? VerifyFailedKind.GatesFailed),
                            CancellationToken.None).ConfigureAwait(false);
                        return;
                    }
                }
            }

            // #1788: DeliveryVerifier.CheckAsync's own doc names the contract (spec/baton.md §3). Placed
            // here so it only runs once the block above has fallen through without an early return
            // (verify passed, was not runnable, or the role declares none) -- never after a VerifyFailed
            // return.
            if (classification.Verdict == OutcomeVerdict.Succeeded && binding.DeliversBranch)
            {
                var deliveryOutcome = await DeliveryVerifier.CheckAsync(
                    binding.Target.WorkingDirectory, binding.ExpectPr, dispatchCancellationToken).ConfigureAwait(false);
                switch (deliveryOutcome.Status)
                {
                    // #1788 review: the operator's own cancel landing inside this check's own window --
                    // mirrors the ordinary verify block's identical arm a few lines up. Never a
                    // VerifyFailed/VerifyNotRun (both would be a misleading account of an execution the
                    // operator asked to stop, not one the delivery check itself judged), and never a
                    // silent fall-through to the Succeeded outcome append below.
                    case DeliveryCheckStatus.Cancelled when dispatchCancellationToken.IsCancellationRequested:
                        await eventLogWriter.AppendAsync(
                                new FlowEvent.ExecutionCancelled(prepared.Request.ExecutionId),
                                CancellationToken.None).ConfigureAwait(false);
                        return;
                    case DeliveryCheckStatus.Failed:
                        await eventLogWriter.AppendAsync(
                                new FlowEvent.VerifyFailed(
                                    prepared.Request.ExecutionId,
                                    deliveryOutcome.FailingMembers,
                                    deliveryOutcome.Tail,
                                    VerifyFailedKind.DeliveryFailed),
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        return;
                    case DeliveryCheckStatus.NotRun:
                        await eventLogWriter.AppendAsync(
                                new FlowEvent.VerifyNotRun(prepared.Request.ExecutionId, deliveryOutcome.NotRunReason!),
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        break;
                    case DeliveryCheckStatus.Passed:
                    default:
                        break;
                }
            }

            // Never gated on dispatchCancellationToken: that token having fired is exactly what
            // produced this outcome (Cancelled) in the first place, so recording it must not itself
            // be cancellable by the same signal — the outcome append always completes once
            // dispatch has returned.
            //
            // #1709: budgetMonitor is in scope here (never for the crash-recovery caller below), so a
            // live dispatch's Succeeded/Failed outcome carries the same peak an arrest would have --
            // the false-positive-side lanes spec/baton.md §3's calibration needs.
            await eventLogWriter.AppendAsync(
                    ToOutcomeEvent(prepared.Request.ExecutionId, classification, budgetMonitor?.SnapshotPeakBilledInWindow()),
                    CancellationToken.None)
                .ConfigureAwait(false);
            await AppendZeroOutputsTripwireIfAnyAsync(eventLogWriter, prepared.Request.ExecutionId, classification, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Dispatch.PromptPreambleException ex)
        {
            // #1373: a deterministic refusal to spawn, the same shape as the guard below and recorded
            // for the same reason — the intent is already journalled, so an uncaught throw here would
            // leave the room stuck at ExecutionRequestAccepted forever, which is exactly what that
            // arm's own comment says it exists to prevent. Permanent: an adapter whose prompt is not
            // one of its arguments refuses identically on every resubmission.
            await eventLogWriter.AppendAsync(
                new FlowEvent.ExecutionFailed(
                    prepared.Request.ExecutionId,
                    FailureClassification.Permanent,
                    ex.Message),
                CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (CommandLineTooLongException ex)
        {
            // A deterministic refusal to spawn: re-submission re-refuses identically, so Permanent
            // (#747; the retry gate in RetryEngine.MayRetry is what makes that stick). Recorded so
            // flow.jsonl is not left stuck at ExecutionRequestAccepted forever.
            await eventLogWriter.AppendAsync(
                new FlowEvent.ExecutionFailed(
                    prepared.Request.ExecutionId,
                    FailureClassification.Permanent,
                    ex.Message),
                CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Baton.Core.BatonException ex)
        {
            // The rest of the refusal family (#747's review): the OS declining the spawn — missing
            // binary, bad working directory, or some other spawn failure the typed guard above cannot
            // pre-empt (#612 measures and refuses an over-long command line up-front; Windows-only,
            // #1405, so its ceiling always resolves) — surfaces as the binding's BatonException, not the
            // typed guard above. Retryable, not Permanent: these are not proven deterministic, and a
            // genuinely stuck cause terminates through RetryPolicy exhaustion instead. Same reason as
            // above for recording at all; OperationCanceledException stays deliberately uncaught either
            // way.
            await eventLogWriter.AppendAsync(
                new FlowEvent.ExecutionFailed(
                    prepared.Request.ExecutionId,
                    FailureClassification.Retryable,
                    $"Spawn refused: {ex.Message}"),
                CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            inFlightExecutions.Unregister(prepared.Request.ExecutionId);
        }
    }

    /// <summary>
    /// Maps a classified outcome to the terminal <see cref="FlowEvent"/> it owes, shared by
    /// a fresh dispatch's own completion (<see cref="DispatchAndRecordOutcomeAsync"/>) and M10 Phase
    /// 3's from-the-log classification of a recorded exit — the same mapping either way.
    /// </summary>
    /// <param name="peakBilledInWindow">
    /// #1709: <see cref="FlowEvent.ExecutionSucceeded.PeakBilledInWindow"/>/
    /// <see cref="FlowEvent.ExecutionFailed.PeakBilledInWindow"/>'s reading. Only the live-dispatch
    /// caller has a <c>TokenBudgetMonitor</c> in scope to pass one; the crash-recovery caller classifies
    /// a recorded exit with no live monitor and always passes null.
    /// </param>
    private static FlowEvent ToOutcomeEvent(
        ExecutionId executionId, OutcomeClassification classification, long? peakBilledInWindow = null) =>
        classification.Verdict switch
        {
            OutcomeVerdict.Succeeded => new FlowEvent.ExecutionSucceeded(
                executionId, classification.WorkspaceChanged, classification.Hollow, classification.HollowReason,
                peakBilledInWindow),
            OutcomeVerdict.Failed => new FlowEvent.ExecutionFailed(
                executionId, classification.FailureClassification, classification.Reason, classification.RetryNotBefore,
                classification.CapturedResponseFile, classification.UnsatisfiedOutputNames, peakBilledInWindow),
            OutcomeVerdict.Cancelled => new FlowEvent.ExecutionCancelled(executionId),
            OutcomeVerdict.Indeterminate => new FlowEvent.ExecutionIndeterminate(
                executionId, classification.Reason, classification.CapturedResponseFile, classification.UnsatisfiedOutputNames),
            _ => throw new ArgumentOutOfRangeException(nameof(classification), classification.Verdict, "Unknown OutcomeVerdict."),
        };

    /// <summary>
    /// #1586 S1 (the #1594 ruling's tripwire): a no-op unless <paramref name="classification"/> carries
    /// <see cref="OutcomeClassification.SubstantialWorkNoOutputsEvidence"/> — appends
    /// <see cref="FlowEvent.ZeroOutputsDespiteSubstantialWork"/> right alongside the outcome event
    /// <see cref="ToOutcomeEvent"/> mapped, from every caller that classifies an outcome — both the
    /// just-completed live dispatch and the branch that settles a dead pump's recorded exit — so the
    /// tripwire fires identically regardless of which one produced the classification.
    /// <c>spec/baton.md</c> §3 names the two call sites; the same "one seam, every caller of it"
    /// placement #1594's own integration constraint required of the capture arm this mirrors.
    /// </summary>
    private static async Task AppendZeroOutputsTripwireIfAnyAsync(
        IEventLogWriter eventLogWriter, ExecutionId executionId, OutcomeClassification classification, CancellationToken cancellationToken)
    {
        if (classification.SubstantialWorkNoOutputsEvidence is not { } evidence)
        {
            return;
        }

        try
        {
            Console.Error.WriteLine(
                $"TRIPWIRE (#1594): execution '{executionId.Value}' produced NONE of its declared " +
                $"outputs, yet {evidence} -- this room's classification may not reflect what actually " +
                "happened. Investigate before trusting it.");
        }
        catch (IOException)
        {
            // Same best-effort posture as the #1594 capture line this mirrors (OutcomeClassifier.Classify) —
            // a broken stderr pipe must not itself orphan the execution; the durable event below still
            // records the fact regardless of whether this line reached the console.
        }

        await eventLogWriter.AppendAsync(new FlowEvent.ZeroOutputsDespiteSubstantialWork(executionId, evidence), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// #1556: resolves every arrest intent the registry's wake latch just woke this round for,
    /// against the CURRENT round's own projection — never against whatever the poller's thread saw,
    /// which can be a round stale by the time this runs. Intent-first, then let the round's own
    /// derived obligations settle it: this appends only <see cref="FlowEvent.CancellationRequested"/>
    /// — the SAME journal shape <see cref="RequestCancellationAsync"/>'s direct path writes, even
    /// when the target has already reached a terminal outcome (intent-first ordering) — and returns
    /// to let the caller <c>continue</c> the pump loop. The very next round's own derived obligations
    /// finalize it: <see cref="NonProcessCancellationDetector"/> handles the Running/step-less
    /// shapes, and the ledger-read parked-cancel block further down this loop (the one
    /// <see cref="IsParkedRetryTarget"/> guards) handles the quota-parked one — this method writes no
    /// finalizing event of its own (spec/baton.md's arrest section has the fuller rationale).
    /// <para>
    /// A target <see cref="ArrestableExecutions.Find"/> no longer admits (redispatched,
    /// already terminal, or never a real target at all — a mismatched/stale execution id,
    /// <c>Marking_an_intent_for_a_mismatched_execution_id...</c> pins exactly this) is dropped, named
    /// in one diagnostic line so the drop is not silent, and nothing is appended for it. A step-tied
    /// target still <see cref="StepStatus.Running"/> is admitted only if it resolves to a
    /// <see cref="WorkerBinding.NonProcess"/> binding, so a live <see cref="WorkerBinding.Process"/>
    /// target that simply has not registered with <paramref name="inFlightExecutions"/> yet is never
    /// mistaken for one (the exact race
    /// <c>False_but_still_running_execution_is_left_pending_then_delivered_on_later_tick_after_registration</c>
    /// pins): that target is left unrecorded here, and the poller's own re-mark on its next tick
    /// re-offers it once the race resolves either way. A parked (<see cref="StepStatus.Failed"/> with
    /// a scheduled <see cref="StepState.RetryNotBefore"/>) or step-less target needs no binding check
    /// — both are non-process by construction (a live process step cannot reach that shape without
    /// unregistering first; a step-less execution is only ever minted against a non-process binding,
    /// <see cref="RecordSupplementaryExecutionAsync"/>).
    /// </para>
    /// </summary>
    private static async Task<bool> SettleArrestIntentsAsync(
        FlowState state,
        WorkflowDefinitionSnapshot snapshot,
        IReadOnlyDictionary<string, WorkerBinding> workerBindings,
        IReadOnlyDictionary<ExecutionId, ExecutionRequest> acceptedRequestByExecutionId,
        InFlightExecutionRegistry inFlightExecutions,
        IEventLogWriter eventLogWriter,
        CancellationToken ioCancellationToken)
    {
        var intents = inFlightExecutions.DrainArrestIntents();
        var recordedAny = false;
        foreach (var (executionId, reason) in intents)
        {
            if (state.CancellationRequestedExecutionIds.Contains(executionId))
            {
                // Already owed an ExecutionCancelled by an earlier append (this call's own intent-
                // first append from a prior round, or an entirely separate path) -- nothing left to
                // record, and re-appending would be a spurious duplicate CancellationRequested.
                continue;
            }

            var target = ArrestableExecutions.Find(state, snapshot, executionId);
            if (target is null)
            {
                var droppedBecause = acceptedRequestByExecutionId.ContainsKey(executionId) ? "already settled" : "unknown execution id";
                LogDroppedArrestIntent(executionId, reason, droppedBecause);

                // #1530: the "dropped" case above used to be printed and never appended -- this
                // method's own early `continue` above (state.CancellationRequestedExecutionIds) proves
                // no CancellationRequested exists yet for this executionId, so this IS the one durable
                // event this lifecycle will ever produce for it, the same "rejection with nothing
                // open" shape ArrestLedgerView.Project now synthesizes a single-entry lifecycle for.
                await inFlightExecutions.RecordCancellationRejectedAsync(
                        executionId, $"arrest intent dropped ({droppedBecause}; marked because: {reason})", ioCancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            if (target.StepId is not null && target.Status == StepStatus.Running
                && (!workerBindings.TryGetValue(target.Worker, out var binding) || binding is not WorkerBinding.NonProcess))
            {
                // Not provably non-process: either the binding won't resolve, or it resolves to
                // Process -- a live dispatch racing its own Register call. Leave it unrecorded; the
                // poller re-marks on its next ~2s tick and this gate re-evaluates against fresh state.
                continue;
            }

            await eventLogWriter.AppendAsync(
                    new FlowEvent.CancellationRequested(executionId, CancellationOrigin.Operator), ioCancellationToken)
                .ConfigureAwait(false);
            recordedAny = true;
        }

        return recordedAny;
    }

    /// <summary>
    /// #1556: the stderr line naming why a drained arrest intent recorded nothing, the same
    /// best-effort diagnostic posture <see cref="AppendZeroOutputsTripwireIfAnyAsync"/> already uses
    /// for a console-only notice. #1916 fix round 2: the call site below also appends a durable
    /// <see cref="FlowEvent.CancellationRejected"/> for the same drop, so this line is a supplement
    /// to that record now, not the sole trace of it.
    /// </summary>
    private static void LogDroppedArrestIntent(ExecutionId executionId, string markedBecause, string droppedBecause)
    {
        try
        {
            Console.Error.WriteLine(
                $"arrest intent for execution '{executionId.Value}' (marked because: {markedBecause}) "
                + $"dropped: {droppedBecause}.");
        }
        catch (IOException)
        {
            // F6-equivalent: a broken stderr pipe must not itself fault the pump.
        }
    }

    /// <summary>
    /// The fail-closed check behind <see cref="SettleArrestIntentsAsync"/>: true only for a
    /// step whose LATEST execution is <paramref name="targetExecutionId"/>, currently
    /// <see cref="StepStatus.Failed"/>, and sitting on a scheduled <see cref="StepState.RetryNotBefore"/>
    /// — the idle-deferral park's exact shape. A step that already redispatched (a new
    /// <see cref="ExecutionId"/> is now latest) or was never parked at all resolves false.
    /// </summary>
    private static bool IsParkedRetryTarget(FlowState state, ExecutionId targetExecutionId) =>
        state.Steps.Any(s =>
            s.LatestExecutionId == targetExecutionId
            && s.Status == StepStatus.Failed
            && s.RetryNotBefore is not null);

    /// <summary>
    /// #802: the declared <see cref="WorkerBindingConfigEntry.FallbackOnExhaustion"/> binding for
    /// <paramref name="worker"/>, when it applies to <paramref name="stepState"/>'s CURRENT park —
    /// null when there is none declared, or when the step's latest execution already ran on that
    /// exact fallback (a single declared hop is the whole feature; #802 rules chained/repeated
    /// auto-failover out permanently). Pure function of projected state and resolved config, so both
    /// call sites (deciding whether to pace or redispatch immediately, and deciding which binding to
    /// dispatch on) recompute the identical answer on every round, including after a crash-and-replay
    /// between the two.
    /// </summary>
    private static WorkerBinding.Process? ResolveVendorExhaustionFallback(
        StepState stepState,
        string worker,
        IReadOnlyDictionary<string, WorkerBinding>? fallbackWorkerBindings,
        IReadOnlyDictionary<ExecutionId, ExecutionRequest> acceptedRequestByExecutionId)
    {
        if (fallbackWorkerBindings is null
            || !fallbackWorkerBindings.TryGetValue(worker, out var candidate)
            || candidate is not WorkerBinding.Process fallbackProcess)
        {
            return null;
        }

        if (stepState.LatestExecutionId is { } latestExecutionId
            && acceptedRequestByExecutionId.TryGetValue(latestExecutionId, out var latestRequest)
            && latestRequest.Adapter == fallbackProcess.Adapter
            && latestRequest.Model == fallbackProcess.Model)
        {
            return null;
        }

        return fallbackProcess;
    }

    /// <param name="ContinuationBrief">
    /// #1373: non-null exactly when this dispatch is a retry of an attempt the dispatch timeout killed
    /// — <see cref="Scheduling.ContinuationBrief.ForRetryAfterTimeout"/>'s text, applied to the target's
    /// prompt in <see cref="DispatchAndRecordOutcomeAsync"/>. In memory rather than on
    /// <see cref="ExecutionRequest"/>: every input to it (attempt count, recorded failure reason,
    /// binding timeout) is already journalled, so a field here would be a second copy of a projection
    /// — and the prompt the worker actually ran with is durably captured either way, as this
    /// execution's own <c>prompt.txt</c>.
    /// </param>
    private sealed record PreparedExecution(
        ExecutionRequest Request,
        string OutputDirectory,
        string? ContinuationBrief = null);

    private sealed record RetryObligation(
        StepId StepId,
        ExecutionId ForExecutionId,
        DateTimeOffset RetryNotBefore,
        int RetryDelayMs);

    // #1183: a vendor never legitimately reports a quota reset this far out (the instant comes from
    // PARSING vendor prose/fields, and a parse bug or garbage value must not become a pump crash) --
    // an ExhaustedUntil reset instant beyond this horizon is treated as bogus and capped rather than
    // trusted wholesale. Chosen comfortably under both the ~24.8-day int-ms cast range this obligation's
    // own RetryDelayMs is computed into, and the ~49.7-day range Task.Delay's TimeSpan overload accepts.
    private static readonly TimeSpan MaxExhaustionParkHorizon = TimeSpan.FromDays(14);

    // #1183: an ExhaustedUntil reset instant already at or in the past collapsed to a zero-delay
    // retry -- with ConsecutiveFailureCount frozen at 0 for quota hits, a vendor that keeps reporting
    // the same stale instant machine-guns the pump in a tight spend-nothing-but-CPU loop. A floor
    // makes the retry rate bounded instead, whether or not the instant is genuinely repeating.
    private static readonly TimeSpan PastResetInstantRetryFloor = TimeSpan.FromSeconds(1);

    private static List<RetryObligation> GetRetryObligations(
        FlowState state,
        WorkflowDefinitionSnapshot snapshot,
        TimeProvider timeProvider,
        Func<double> jitterSource,
        bool settleOnVendorExhaustion,
        IReadOnlyDictionary<string, WorkerBinding>? fallbackWorkerBindings,
        IReadOnlyDictionary<ExecutionId, ExecutionRequest> acceptedRequestByExecutionId)
    {
        var stepDefinitionByStepId = snapshot.Steps.ToDictionary(s => s.StepId);
        var obligations = new List<RetryObligation>();

        foreach (var stepState in state.Steps)
        {
            if (stepState.Status != StepStatus.Failed || stepState.LatestExecutionId is null)
            {
                continue;
            }

            var stepDef = stepDefinitionByStepId[stepState.StepId];
            if (!RetryEngine.MayRetry(stepState, stepDef.RetryPolicy))
            {
                continue;
            }

            // A Failed step whose ConsecutiveFailureCount is zero with no live classification is
            // one an operator just reopened via RetryWithRevision — StateProjector resets
            // both for exactly that decision. Backoff exists to pace the machine's own retries; a
            // person's explicit "retry now" is not paced, so no obligation is scheduled for it.
            // An ExhaustedUntil step also sits at zero (quota hits consume no budget, 0026) but is
            // the machine's own wait, not a person's reopen — it must still be paced to the reset.
            if (stepState.ConsecutiveFailureCount == 0
                && stepState.LatestFailureClassification != FailureClassification.ExhaustedUntil)
            {
                continue;
            }

            if (stepState.RetryScheduledForExecutionId == stepState.LatestExecutionId)
            {
                continue;
            }

            // #802: a declared, not-yet-tried fallback rescues this step from the park below,
            // known reset instant or not -- resolved once here and reused by both guards.
            var vendorExhaustionFallback = stepState.LatestFailureClassification == FailureClassification.ExhaustedUntil
                ? ResolveVendorExhaustionFallback(stepState, stepDef.Worker, fallbackWorkerBindings, acceptedRequestByExecutionId)
                : null;

            // 0026 §5 (#1115 review): an ExhaustedUntil step whose vendor gave NO reset instant
            // gets NO obligation at all — "nothing wakes up, and the product says so" — UNLESS a
            // declared fallback (#802) can rescue it: redispatching on a different vendor needs no
            // reset instant from the PARKED one to pace against. Falling through to ordinary backoff
            // here fabricated a ~1s-away instant on every cycle (ConsecutiveFailureCount is frozen at
            // 0 for quota hits, so the delay never grew), auto-retrying a claude dispatch against a
            // known-dead quota forever while the status surfaced the fabricated time as a vendor
            // reset. A person resumes this step (RetryWithRevision), or a later failure carries a
            // real instant.
            // 0026 §4 attended/unattended discriminator (#1184): when settleOnVendorExhaustion is true
            // (an attended interactive session turn), an ExhaustedUntil step ALSO gets NO retry obligation
            // regardless of a declared fallback — the operator is present and drives the rebind by hand.
            if (stepState.LatestFailureClassification == FailureClassification.ExhaustedUntil &&
                (settleOnVendorExhaustion
                    || (vendorExhaustionFallback is null && stepState.LatestExecutionFailedRetryNotBefore is null)))
            {
                continue;
            }

            DateTimeOffset notBefore;
            int delayMs;

            if (vendorExhaustionFallback is not null)
            {
                // #802: redispatch on the declared fallback immediately rather than pacing to the
                // primary vendor's reset instant (known or not) — DependencyResolver's own
                // `now >= notBefore` check then admits this step on the very round that follows.
                notBefore = timeProvider.GetUtcNow();
                delayMs = 0;
            }
            else if (stepState.LatestFailureClassification == FailureClassification.ExhaustedUntil &&
                stepState.LatestExecutionFailedRetryNotBefore is { } resetMoment)
            {
                var utcNow = timeProvider.GetUtcNow();

                // #1183: cap an absurd (parse-bug/garbage) far-future instant to the sane horizon
                // rather than trust it wholesale -- keeps RetryNotBefore and RetryDelayMs mutually
                // consistent for DependencyResolver's #712 backwards-clock-jump clamp below, and keeps
                // every downstream wait on this obligation's RetryNotBefore inside a range Task.Delay
                // actually accepts.
                var cappedResetMoment = resetMoment - utcNow > MaxExhaustionParkHorizon
                    ? utcNow + MaxExhaustionParkHorizon
                    : resetMoment;
                var rawDelay = cappedResetMoment - utcNow;

                // #1183: an instant less than PastResetInstantRetryFloor away -- already at or before
                // now (including one repeating unchanged), or legitimately future but imminent -- is
                // paced up to the floor instead of collapsing to a near-zero-delay retry. This branch
                // does not and need not distinguish "already past" from "about to hit": both would
                // otherwise machine-gun the pump the same way.
                if (rawDelay < PastResetInstantRetryFloor)
                {
                    notBefore = utcNow + PastResetInstantRetryFloor;
                    delayMs = (int)PastResetInstantRetryFloor.TotalMilliseconds;
                }
                else
                {
                    notBefore = cappedResetMoment;
                    // #1183: Ceiling, not Round -- DependencyResolver's #712 clamp needs
                    // delayMs >= the real notBefore-utcNow gap so a sub-millisecond rounddown can never
                    // make `remaining > maxDelay` misfire and release this step before cappedResetMoment.
                    delayMs = (int)Math.Ceiling(rawDelay.TotalMilliseconds);
                }
            }
            else
            {
                double jitterSample = jitterSource();
                int attempt = stepState.ConsecutiveFailureCount;
                TimeSpan delay = stepDef.RetryPolicy.Backoff.DelayFor(attempt, jitterSample);
                delayMs = (int)Math.Round(delay.TotalMilliseconds);
                notBefore = timeProvider.GetUtcNow().AddMilliseconds(delayMs);
            }

            obligations.Add(new RetryObligation(
                stepState.StepId,
                stepState.LatestExecutionId.Value,
                notBefore,
                delayMs));
        }

        return obligations;
    }

    /// <summary>
    /// The contract a crash-recovery classification runs against (#724): the live binding's when it
    /// resolves, else one reconstructed from the recorded <see cref="ExecutionRequest"/> — the
    /// execution already ran, so what it was asked to produce is a recorded fact, and a bindings
    /// file that changed or broke since must not make the recorded outcome unclassifiable (the
    /// #662 lesson, on the recovery path). The reconstruction carries output NAMES only: any
    /// <c>OutputCondition</c> the original contract declared is unknowable from the request today,
    /// so a conditioned output classifies on existence alone in this fallback. Recording the full
    /// contract on the request is #672's design to make.
    /// </summary>
    private static WorkerContract GetContractForClassification(
        ExecutionRequest request,
        IReadOnlyDictionary<string, WorkerBinding> workerBindings)
    {
        try
        {
            if (workerBindings.TryGetValue(request.Worker, out var binding) && binding is WorkerBinding.Process processBinding)
            {
                return processBinding.Contract;
            }
        }
        catch (BatonFlowException)
        {
            // Resolution refused (missing adapter, unsatisfiable grant) — exactly the case the
            // recorded request exists to cover. Anything else still propagates.
        }

        return new WorkerContract(
            request.Worker,
            RequiredInputs: [],
            ProducedOutputs: [.. request.Outputs.Select(o => new ProducedOutput(o))],
            OptionalMetadata: []);
    }

    /// <summary>
    /// The first-verdict canary's tool-call count, shared by the live-dispatch and crash-recovery
    /// replay call sites (#1732 review N4). Reads <see cref="ExecutionStreamLogger.StdoutRolloverFileName"/>
    /// first, when it exists, before <see cref="ExecutionStreamLogger.StdoutLogFileName"/> --
    /// <c>ExecutionStreamLogger</c> performs a single 8 MiB rollover per stream, so a long run's
    /// earliest segment (the rolled-out <c>.stdout.log.1</c>) and its current tail (<c>.stdout.log</c>)
    /// are two separate files, and skipping the first would undercount a run whose terminal tool
    /// steps happened to land before the roll. The canary only needs "&gt; 0", so summing both
    /// segments in file order is sufficient without reconstructing one contiguous stream.
    /// </summary>
    private static int CountToolCallsFromStdoutLog(IWorkerUsageParser? usageParser, string outputDirectory)
    {
        var toolCallCount = 0;
        if (usageParser is null)
        {
            return toolCallCount;
        }

        foreach (var fileName in new[] { ExecutionStreamLogger.StdoutRolloverFileName, ExecutionStreamLogger.StdoutLogFileName })
        {
            var path = Path.Combine(outputDirectory, fileName);
            if (!File.Exists(path))
            {
                continue;
            }

            foreach (var line in File.ReadLines(path))
            {
                toolCallCount += usageParser.CountToolSteps(line);
            }
        }

        return toolCallCount;
    }

}
