using Baton.Domain;

namespace Baton.Status;

/// <summary>
/// The single coarse outcome word for a <see cref="FlowState"/> — "Running", "Paused", or, once
/// <see cref="WorkflowStatus.Terminal"/> is reached, whichever terminal word it settled into. The
/// vocabulary itself is enumerated once, in <c>spec/baton.md</c> §3's table (and pinned by
/// <c>WorkflowOutcomeAndExitCodeTests</c>'s vocabulary test) — deliberately NOT restated here, where a
/// hand-written list went stale twice, on #1608's <see cref="Indeterminate"/> and again on #1945's
/// <see cref="FinishedDuringTeardown"/>.
/// <see cref="WorkflowStatus"/> itself only says the pump reached its fixed point, not
/// which one — every other terminal-outcome consumer (<c>StatusCommand</c>'s <c>--json</c>,
/// <c>RunExitCodeResolver</c>, the terminal sentinel) needs this same word, so it is computed here
/// once rather than re-derived per caller (#1356).
/// </summary>
public static class WorkflowOutcome
{
    public const string Running = "Running";
    public const string Paused = "Paused";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";

    /// <summary>
    /// #1945: every step succeeded, and at least one did so on
    /// <see cref="Baton.Outcomes.OutcomeClassifier"/>'s arm for a dispatch timeout that killed a
    /// worker <b>after its push had landed</b> — clean workspace, nothing ahead of the tracking
    /// branch, contract satisfied. <b>A SUCCEEDED-shaped word, not a failure one</b>: every consumer
    /// that asks "did this room finish?" accepts it exactly where it accepts <see cref="Succeeded"/>.
    /// <para>
    /// The full ruling — what the predicate means and does not mean, why it is a separate word rather
    /// than a bare <see cref="Succeeded"/>, and the consumer list that owes it the succeeded reading —
    /// is stated once, in <c>spec/baton.md</c> §3's terminal-vocabulary table. Not re-derived here.
    /// </para>
    /// </summary>
    public const string FinishedDuringTeardown = "FinishedDuringTeardown";

    /// <summary>
    /// #1586 S1 (state-truth design, ratified 2026-09-01 amendment): journal facts alone cannot
    /// distinguish success from failure for this room — the two-predicate model (execution outcome vs
    /// contract completion) disagrees with itself, e.g. work-evidence contradicts contract-evidence
    /// (#1594's missing-output-with-envelope shape is the canonical instance) or a worktree fingerprint
    /// does not reconcile at settle time. A single added value, not a two-field split, per the ruling's
    /// own wording.
    /// <para>
    /// <b>Four producers, one reading.</b> <see cref="DescribeTerminal"/> returns this whenever any
    /// step reads <see cref="StepState.IndeterminateAwaitingResolution"/> true — a single predicate,
    /// deliberately not one check per producer. Which events raise that flag are enumerated once in
    /// <c>spec/baton.md</c> §3's producer table ("Producers, since #1608 and #1593"); not restated
    /// here. What belongs to this class: the flag alone decides the room-level word, and
    /// <see cref="StepState.IndeterminateReason"/>/<see cref="StepState.IndeterminateProducer"/> —
    /// per-producer detail some of those producers carry for a human or for <c>baton resolve</c> — are
    /// deliberately not read here.
    /// </para>
    /// <para>
    /// <b>Consumer obligations (ruling item 2)</b> — spelled out in full in <c>spec/baton.md</c> §3,
    /// "Consumer obligations, ratified with the value itself". Not re-derived here.
    /// </para>
    /// </summary>
    public const string Indeterminate = "Indeterminate";

    public static string Describe(FlowState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return state.Status switch
        {
            WorkflowStatus.Running => Running,
            WorkflowStatus.Paused => Paused,
            WorkflowStatus.Terminal => DescribeTerminal(state.Steps),
            _ => state.Status.ToString(),
        };
    }

    /// <summary>
    /// A step whose reason names a dispatch timeout (<see cref="Baton.Outcomes.OutcomeClassifier"/>'s
    /// fixed "Execution timed out." sentence) — the only signal available for that distinction today.
    /// There is no structural <see cref="FailureClassification"/> value for it (its vocabulary
    /// is <c>Retryable</c>/<c>Permanent</c>/<c>ExhaustedUntil</c>/<c>ToolDenied</c> only), so this reads
    /// the same fixed diagnostic sentence a person already reads in <c>FlowStateReporter</c>'s output
    /// rather than adding a second, parallel classification the event log does not carry.
    /// </summary>
    public static bool IsTimeoutFailure(StepState step) =>
        step.Status == StepStatus.Failed
        && step.LatestFailureReason is { } reason
        // #1373: the writer's own constant, not a second copy of the literal — the classifier now
        // opens two different reasons with it, and a reword there must not leave this reading a
        // sentence nothing produces any more.
        && reason.StartsWith(Baton.Outcomes.OutcomeClassifier.TimeoutSentence, StringComparison.Ordinal);

    private static string DescribeTerminal(IReadOnlyList<StepState> steps)
    {
        // Vacuously Succeeded for a zero-step Terminal state (a degenerate workflow with nothing to
        // run) — the same reading `Program`'s pre-#1356 exit-code check already gave it; preserved
        // rather than reclassified so this refactor changes no observable behaviour for that case.
        if (steps.Count == 0)
        {
            return Succeeded;
        }

        if (steps.All(step => step.Status == StepStatus.Succeeded))
        {
            // #1945: ahead of the plain Succeeded return, or it could never fire. A FLAG, never a
            // reason-string prefix the way IsTimeoutFailure below has to work: StateProjector nulls
            // LatestFailureReason on the succeeded path by construction, so no sentence survives that
            // hop.
            return steps.Any(step => step.FinishedDuringTeardown) ? FinishedDuringTeardown : Succeeded;
        }

        // #1608 / #1623: checked ahead of the ordinary Failed/Rejected read below — an unresolved
        // indeterminate step IS Status.Failed whichever producer put it there (the
        // single-added-enum-value ruling keeps StepStatus itself untouched), so this must win the
        // room-level word or every such room would misreport Failed again, exactly the collapse
        // #1608 exists to undo. One predicate, never one check per producer: they unify upstream, in
        // StateProjector, not here.
        if (steps.Any(step => step.IndeterminateAwaitingResolution))
        {
            return Indeterminate;
        }

        if (steps.Any(step => step.Status is StepStatus.Failed or StepStatus.Rejected))
        {
            return Failed;
        }

        if (steps.Any(step => step.Status == StepStatus.Cancelled))
        {
            return Cancelled;
        }

        // Reachable only by a step left Pending in a Terminal workflow with nothing else failed or
        // cancelled to explain why it was never dispatched (e.g. a DAG the Dependency Resolver could
        // never reach) — treated as Failed rather than silently reading as Succeeded.
        return Failed;
    }
}
