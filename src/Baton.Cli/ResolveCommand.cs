using Baton.Artifacts;
using Baton.Domain;
using Baton.Mutation;
using Baton.Projection;
using Baton.Status;
using Baton.Store;
using Baton.Templates;

namespace Baton.Cli;

/// <summary>
/// <c>baton resolve</c> (#1608): exposes <see cref="MutationInterface.RecordCaptureResolutionAsync"/>
/// on the CLI. Unlike <see cref="DecideCommand"/>/<see cref="CancelCommand"/>, this never loads worker
/// bindings and never pumps — see <see cref="MutationInterface.RecordCaptureResolutionAsync"/>'s own
/// remarks for why an unresolved indeterminate capture is unreachable any other way — so nothing here
/// dispatches, and there is nothing to bind for a room that already settled Indeterminate.
/// </summary>
public static class ResolveCommand
{
    private const string ArtifactsDirectoryName = ArtifactManager.ArtifactsDirectoryName;

    /// <exception cref="SnapshotLoadException">
    /// The room directory has no persisted snapshot yet (never started via <c>baton run</c>), or its
    /// persisted snapshot is malformed.
    /// </exception>
    /// <exception cref="CliArgumentException">
    /// <paramref name="options"/>'s <c>ExecutionId</c> is <c>null</c> (room-level targeting) and the
    /// room's own projected state has zero or more than one step still awaiting resolution — fail
    /// closed rather than guess; the message names every candidate found. Also thrown when an
    /// explicit <c>--execution</c> names a step with no unresolved indeterminate capture.
    /// </exception>
    /// <exception cref="InvalidCaptureResolutionException">
    /// The resolution itself is invalid against the room's current state — see
    /// <see cref="MutationInterface.RecordCaptureResolutionAsync"/>.
    /// </exception>
    /// <exception cref="Baton.Concurrency.WorkflowLockedException">
    /// Another Flow instance already holds this room directory's lock.
    /// </exception>
    public static async Task<CommandResult> ExecuteAsync(
        ResolveOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var snapshotPath = Path.Combine(options.RoomDirectoryPath, BatonPaths.SnapshotFileName);
        var logPath = Path.Combine(options.RoomDirectoryPath, BatonPaths.FlowLogFileName);
        var artifactsRootPath = Path.Combine(options.RoomDirectoryPath, ArtifactsDirectoryName);

        if (!File.Exists(snapshotPath))
        {
            throw new SnapshotLoadException(
                $"Room directory '{options.RoomDirectoryPath}' has no bound snapshot — 'baton resolve' " +
                "targets a room 'baton run' has already started, and never binds one fresh.");
        }

        var snapshot = await SnapshotBinder.LoadFromFileAsync(snapshotPath, cancellationToken).ConfigureAwait(false);
        var reader = new FlowEventLogReader(logPath);

        var targetExecutionId = options.ExecutionId is { } explicitExecutionId
            ? await ResolveExplicitExecutionAsync(
                    reader, snapshot, explicitExecutionId, options.RoomDirectoryPath, options.Accept, options.Close, cancellationToken)
                .ConfigureAwait(false)
            : await ResolveSingleCandidateAsync(reader, snapshot, options.RoomDirectoryPath, options.Accept, options.Close, cancellationToken)
                .ConfigureAwait(false);

        await using var writer = new FlowEventLogWriter(logPath);

        var state = await MutationInterface.RecordCaptureResolutionAsync(
                options.RoomDirectoryPath,
                snapshot,
                artifactsRootPath,
                reader,
                writer,
                targetExecutionId,
                options.Accept,
                options.Reason,
                options.Close,
                cancellationToken)
            .ConfigureAwait(false);

        return new CommandResult(state, snapshot, RoomDirectoryPath: options.RoomDirectoryPath);
    }

    private static async Task<ExecutionId> ResolveExplicitExecutionAsync(
        FlowEventLogReader reader,
        WorkflowDefinitionSnapshot snapshot,
        string explicitExecutionId,
        string roomDirectoryPath,
        bool accepted,
        bool close,
        CancellationToken cancellationToken)
    {
        var executionId = new ExecutionId(explicitExecutionId);
        var events = await reader.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        var state = StateProjector.Project(events, snapshot);

        var namedStep = state.Steps.FirstOrDefault(step => step.LatestExecutionId == executionId);

        // F1 (#1593 review), widened by #1622 (d)/#1700: admission is per-verb, keyed on
        // IndeterminateProducer rather than a bare LatestCapturedResponseFile null/not-null read — see
        // spec/baton.md §3's producer table for which verb admits which. Mirrors
        // MutationInterface.RecordCaptureResolutionAsync's own guard so the refusal lands here, with a
        // message naming the right remedy, rather than deeper in as a bare "no unresolved indeterminate
        // capture". --close admits exactly the four producers --reject does NOT: VerifyFailed,
        // Arrested, BuildLockBusy (#1796), and null (a step Indeterminate for no producer at all, the
        // legacy pre-#1593 shape). Since #1877 --close also admits one shape that is not a producer
        // at all: a capture ALREADY resolved-rejected whose step is still the dangling one
        // (IsDanglingRejected below).
        var admitsAccept = namedStep is { IndeterminateAwaitingResolution: true }
            && namedStep.IndeterminateProducer == IndeterminateProducer.CapturedResponse;
        var admitsReject = namedStep is { IndeterminateAwaitingResolution: true }
            && namedStep.IndeterminateProducer is IndeterminateProducer.CapturedResponse or IndeterminateProducer.ContractFailure;
        var admitsClose = namedStep is { IndeterminateAwaitingResolution: true }
            && namedStep.IndeterminateProducer is IndeterminateProducer.VerifyFailed or IndeterminateProducer.Arrested or IndeterminateProducer.BuildLockBusy or null;
        var isAwaitingResolution = accepted ? admitsAccept : close ? admitsClose : admitsReject;
        var isNonCaptureIndeterminate = namedStep is { IndeterminateAwaitingResolution: true } && !isAwaitingResolution;

        // #1608 review finding 5: also admit a step already ACCEPTED for this exact execution, but
        // only when this call is itself an --accept-capture -- MutationInterface's own gate on this
        // (RecordCaptureResolutionAsync) requires the same, so a --reject against an already-accepted
        // execution still refuses here rather than reading as a repair of someone else's accept.
        // MutationInterface.RecordCaptureResolutionAsync treats an admitted repair as a crash-repair
        // request (its own ReconcileAcceptedCaptureAsync), not a fresh resolution.
        var isRepairableAccepted = accepted && namedStep is not null
            && events.OfType<FlowEvent.CaptureResolved>()
                .LastOrDefault(resolved => resolved.ExecutionId == executionId && resolved.StepId == namedStep.StepId)
            is { Accepted: true };

        // #1877: --close also admits an already-rejected capture whose step is still the dangling one
        // — see MutationInterface.RecordCaptureResolutionAsync's own arm for why the predicate keys on
        // the journal fact rather than on retry-eligibility, and IsDanglingRejected below for the
        // predicate itself. Mirrored here so the refusal (or the admission) lands at the same layer
        // every other resolve target's does.
        var isDanglingRejected = close && IsDanglingRejected(namedStep, executionId, events);

        if (!isAwaitingResolution && !isRepairableAccepted && !isDanglingRejected)
        {
            // #1623 merge / F1 (#1593 review): stated as its own case rather than folded into the
            // generic refusal below, because the generic one's advice ("confirm 'state' reads
            // Indeterminate") is exactly the check this operator has already passed. Two distinct
            // shapes reach here — see ThrowDiscriminatedRefusal for which message each gets.
            if (isNonCaptureIndeterminate)
            {
                ThrowDiscriminatedRefusal(namedStep!.IndeterminateProducer, explicitExecutionId, roomDirectoryPath, accepted, close);
            }

            // #1971: the id named no step at all. That is a different mistake from "this step is not
            // resolvable", and the generic advice below cannot fix it -- the operator who hit this
            // passed a plausible-looking `exec-1`, read the room state the refusal pointed at, found it
            // Indeterminate exactly as promised, and had no way to learn the real id from the refusal.
            // So this arm SHOWS the ids instead of describing where to look them up.
            if (namedStep is null)
            {
                throw new CliArgumentException(
                    $"Execution '{explicitExecutionId}' is not the latest execution of any step in room '{roomDirectoryPath}'. "
                    + DescribeLatestExecutions(state)
                    + " Name one of those ids with --execution (or omit --execution entirely when exactly one step "
                    + "awaits resolution, and baton will pick it).");
            }

            // #1608 review finding 7: a resolved-but-Failed step and an unresolved one both read
            // ordinary "Failed" per-step in status --json (WorkflowStatusStepView carries no
            // IndeterminateAwaitingResolution field) -- the room-level `state` reading Indeterminate
            // is the one thing status --json actually distinguishes, so that is what this points at.
            throw new CliArgumentException(
                $"Execution '{explicitExecutionId}' has no unresolved indeterminate capture in room " +
                $"'{roomDirectoryPath}' — 'baton resolve' only targets a step still awaiting conductor resolution " +
                "(or already accepted, to repair a crash-interrupted write). " +
                $"Run 'baton status {roomDirectoryPath} --json' and confirm 'state' reads Indeterminate before naming --execution.");
        }

        return executionId;
    }

    /// <summary>
    /// #1971: every step's latest execution id, with the step and the state that go with it — enough
    /// for the caller to fix a wrong <c>--execution</c> from the refusal alone, without a second
    /// <c>baton status --json</c> read.
    /// </summary>
    /// <remarks>
    /// Worded for what <see cref="StateProjector"/> can actually prove here. A null
    /// <c>namedStep</c> covers two cases — an id naming nothing at all, and a real but SUPERSEDED
    /// execution of a step that has since been redispatched — and telling them apart would need an
    /// event scan this refusal does not otherwise need. "not the latest execution of any step" is true
    /// of both, and listing the latest ids fixes the caller's id either way.
    /// <para>
    /// <see cref="StepState.IndeterminateAwaitingResolution"/> is called out per step because
    /// <c>StepStatus</c> alone does not distinguish a resolved step from one still awaiting a
    /// conductor (finding 7 above), and that flag is precisely what decides which id this verb will
    /// accept.
    /// </para>
    /// </remarks>
    private static string DescribeLatestExecutions(FlowState state)
    {
        var known = state.Steps
            .Where(step => step.LatestExecutionId is not null)
            .Select(step => $"{step.StepId.Value} ({step.Status}"
                + (step.IndeterminateAwaitingResolution ? ", awaiting conductor resolution" : string.Empty)
                + $") -> {step.LatestExecutionId!.Value.Value}")
            .ToList();

        return known.Count == 0
            ? "This room has no step with a recorded execution yet."
            : "This room's steps and their latest executions: " + string.Join("; ", known) + ".";
    }

    /// <summary>
    /// Room-level target resolution, the same fail-closed shape <c>baton cancel</c>'s
    /// <c>ResolveRunningExecutionAsync</c> already uses for its own omitted-<c>--execution</c> case.
    /// </summary>
    /// <remarks>
    /// F1 (#1593 review): the sole candidate's <see cref="StepState.IndeterminateProducer"/> is checked
    /// against <paramref name="accepted"/> the same way <see cref="ResolveExplicitExecutionAsync"/>
    /// checks it, so a room whose only unresolved step is VerifyFailed/Arrested (or a ContractFailure
    /// targeted by <c>--accept-capture</c>) gets the discriminated refusal HERE — naming the right
    /// remedy — rather than being silently selected and refused two layers deeper by
    /// <c>MutationInterface.RecordCaptureResolutionAsync</c>'s generic "has no unresolved indeterminate
    /// capture" message, whose own advice ("confirm 'state' reads Indeterminate") is exactly the check
    /// this caller already passed by omitting <c>--execution</c> at all.
    /// </remarks>
    private static async Task<ExecutionId> ResolveSingleCandidateAsync(
        FlowEventLogReader reader,
        WorkflowDefinitionSnapshot snapshot,
        string roomDirectoryPath,
        bool accepted,
        bool close,
        CancellationToken cancellationToken)
    {
        var events = await reader.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        var state = StateProjector.Project(events, snapshot);
        var candidates = state.Steps
            .Where(step => step.IndeterminateAwaitingResolution && step.LatestExecutionId is not null)
            .ToList();

        if (candidates.Count == 1)
        {
            var candidate = candidates[0];
            var admitsVerb = accepted
                ? candidate.IndeterminateProducer == IndeterminateProducer.CapturedResponse
                : close
                    ? candidate.IndeterminateProducer is IndeterminateProducer.VerifyFailed or IndeterminateProducer.Arrested or IndeterminateProducer.BuildLockBusy or null
                    : candidate.IndeterminateProducer is IndeterminateProducer.CapturedResponse or IndeterminateProducer.ContractFailure;

            if (!admitsVerb)
            {
                ThrowDiscriminatedRefusal(
                    candidate.IndeterminateProducer, candidate.LatestExecutionId!.Value.Value, roomDirectoryPath, accepted, close);
            }

            return candidate.LatestExecutionId!.Value;
        }

        if (candidates.Count == 0)
        {
            // #1877: before refusing on "no unresolved capture", look for the step this --close would
            // actually close — an already-rejected capture left dangling by the pre-#1877 rule. The
            // refusal below is honest for --accept-capture/--reject, and was the exact dead end the
            // issue reported for --close: the verb refused on the absence of the very thing its target
            // no longer has.
            if (close)
            {
                var dangling = state.Steps
                    .Where(step => step.LatestExecutionId is not null
                        && IsDanglingRejected(step, step.LatestExecutionId.Value, events))
                    .ToList();

                if (dangling.Count == 1)
                {
                    return dangling[0].LatestExecutionId!.Value;
                }

                if (dangling.Count > 1)
                {
                    throw new CliArgumentException(
                        $"No --execution given, and room '{roomDirectoryPath}' has {dangling.Count} already-rejected " +
                        $"steps to close ({string.Join(", ", dangling.Select(step => step.LatestExecutionId!.Value.Value))}) " +
                        "— 'baton resolve' refuses to guess which one.",
                        "pass --execution explicitly, naming the one to close.");
                }
            }

            throw new CliArgumentException(
                $"No --execution given, and room '{roomDirectoryPath}' has no unresolved indeterminate " +
                "capture to resolve — 'baton resolve' refuses to guess.",
                $"run 'baton status {roomDirectoryPath}' to confirm the room's current state reads Indeterminate.");
        }

        throw new CliArgumentException(
            $"No --execution given, and room '{roomDirectoryPath}' has {candidates.Count} steps " +
            $"awaiting resolution ({string.Join(", ", candidates.Select(step => step.LatestExecutionId!.Value.Value))}) " +
            "— 'baton resolve' refuses to guess which one.",
            "pass --execution explicitly, naming the one to resolve.");
    }

    /// <summary>
    /// #1877: "this step is an already-rejected capture nobody can close any other way" — the one
    /// predicate both target-resolution paths above share, so an explicit <c>--execution</c> and a
    /// room-level <c>--close</c> cannot drift on what counts. Every clause is a durable journal fact,
    /// deliberately NOT the step's retry-eligibility: #1877's projector fix already forecloses retry
    /// on any rejection, so a predicate keyed on "still retry-eligible" would be dead code on every
    /// room the fix has re-projected, and a test written against it would pass while asserting
    /// nothing. <see cref="MutationInterface.RecordCaptureResolutionAsync"/>'s own mirror of this arm
    /// is what actually records the close.
    /// </summary>
    private static bool IsDanglingRejected(
        StepState? step, ExecutionId executionId, IReadOnlyList<FlowEvent> events)
        => step is { IndeterminateAwaitingResolution: false, Status: StepStatus.Failed }
            && step.LatestExecutionId == executionId
            && events.OfType<FlowEvent.CaptureResolved>()
                .LastOrDefault(resolved => resolved.ExecutionId == executionId && resolved.StepId == step.StepId)
                is { Accepted: false };

    /// <summary>
    /// F1 (#1593 review), widened by #1622 (d)/#1700: the shared refusal text for a step that settled
    /// Indeterminate through a producer the caller's verb (<paramref name="accepted"/>/
    /// <paramref name="close"/>) does not admit — shared between <see cref="ResolveExplicitExecutionAsync"/>
    /// and <see cref="ResolveSingleCandidateAsync"/> so the two callers cannot drift on what each
    /// producer's remedy actually is. #1700's own defect — <c>--reject</c> refusing a
    /// VerifyFailed/Arrested/null-producer step with no way to close it — is what <c>--close</c> exists
    /// to answer; this method's job is naming it as the remedy wherever the wrong verb was tried.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void ThrowDiscriminatedRefusal(
        IndeterminateProducer? producer, string executionId, string roomDirectoryPath, bool accepted, bool close)
    {
        if (producer is IndeterminateProducer.CapturedResponse or IndeterminateProducer.ContractFailure)
        {
            // A captured-response-shaped step (CapturedResponse or ContractFailure) was targeted with
            // --close, which is scoped to the producers that never had a capture to begin with.
            if (close)
            {
                throw new CliArgumentException(
                    $"Execution '{executionId}' in room '{roomDirectoryPath}' settled Indeterminate "
                    + "with a captured response or contract failure to rule on — '--close' is scoped to a "
                    + "verify failure, an arrest, or no producer at all, none of which apply here.",
                    producer == IndeterminateProducer.CapturedResponse
                        ? "pass --accept-capture (the capture honestly satisfies the declared output(s)) "
                          + "or --reject --reason <text>. See spec/baton.md §3."
                        : "pass --reject --reason, naming the conductor's own judgement after inspecting "
                          + "the workspace. See spec/baton.md §3.");
            }

            if (producer == IndeterminateProducer.ContractFailure && accepted)
            {
                throw new CliArgumentException(
                    $"Execution '{executionId}' in room '{roomDirectoryPath}' settled Indeterminate "
                    + "with no captured response to accept — an exit-0 contract failure, a dead worker on "
                    + "a mutated workspace, or (#1373) a dispatch timeout on one; never an "
                    + "unwritten-but-recoverable output. "
                    + "'baton resolve --reject --reason <text>' still resolves it.",
                    "pass --reject --reason, naming the conductor's own judgement after inspecting the "
                    + "workspace. See spec/baton.md §3.");
            }
        }

        // F1 nit (#1664 re-review), widened by #1796: explicit, not a catch-all else —
        // VerifyFailed/Arrested/BuildLockBusy/null are the only producers this "nothing to accept or
        // reject" text describes, and both callers only reach this helper for a producer the caller's
        // verb does not admit, so this arm only fires for --accept-capture/--reject against one of these
        // four (--close admits all four, so it never reaches here for them).
        if (producer is IndeterminateProducer.VerifyFailed or IndeterminateProducer.Arrested or IndeterminateProducer.BuildLockBusy or null)
        {
            throw new CliArgumentException(
                $"Execution '{executionId}' in room '{roomDirectoryPath}' settled Indeterminate "
                + "without a captured response — a verify failure or a token-budget arrest, not an "
                + "unwritten output. There is nothing for '--accept-capture'/'--reject' to accept or reject.",
                $"pass 'baton resolve {roomDirectoryPath} --execution {executionId} --close --reason <text>' "
                + "to close it without redoing the work (the work already landed), or read the step's "
                + $"failure reason (`baton status {roomDirectoryPath} --json`), fix the underlying cause, "
                + "and re-dispatch — a fresh execution reopens the step. See spec/baton.md §3.");
        }

        throw new InvalidOperationException(
            $"ThrowDiscriminatedRefusal reached for execution '{executionId}' in room '{roomDirectoryPath}' "
            + $"with producer '{producer}', accepted={accepted}, close={close} — every combination that "
            + "value admits is handled above, so this indicates a new producer or a new admission rule "
            + "reached this helper without a matching arm, not a step that genuinely has nothing to "
            + "accept or reject.");
    }
}
