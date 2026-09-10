using Baton.Domain;

namespace Baton.Projection;

/// <summary>
/// The single register for "which execution(s) could be arrested right now" (#1556 PR 1, collapsing
/// the three-place predicate PR #1528 review finding F10 named). A candidate is either a currently
/// <see cref="StepStatus.Running"/> step's latest execution, a quota-parked one (#1607 — an un-foreclosed
/// <see cref="StepStatus.Failed"/> with <see cref="FailureClassification.ExhaustedUntil"/>, whether its reset is
/// scheduled or unknown), or a step-less supplementary execution
/// still awaiting completion.
/// <para>
/// Three call sites used to restate this shape independently: <c>RunningExecutionResolver</c>'s
/// step-tied candidate list (now a shim over <see cref="ResolveSingleStepLane"/>),
/// <c>CancelRequestPoller</c>'s settle re-check (now <see cref="Find"/> — the D2 fix: a step-less
/// supplementary execution is no longer told "it already settled" while it is still pending), and
/// <c>NonProcessCancellationDetector</c>'s two Running/step-less arms (now a filter over
/// <see cref="All"/>). <see cref="ResolveSingleStepLane"/> deliberately stays step-tied only (ruling
/// Q3, #1530) — bare <c>baton cancel</c> room-level targeting never treats a step-less execution as a
/// candidate; only <see cref="Find"/> and <see cref="All"/> see one.
/// </para>
/// </summary>
public static class ArrestableExecutions
{
    /// <param name="StepId">Null for a step-less supplementary execution.</param>
    /// <param name="Worker">The worker name the execution is bound to.</param>
    /// <param name="Status">
    /// The owning step's projected <see cref="StepStatus"/> — <see cref="StepStatus.Running"/> or a
    /// quota-parked <see cref="StepStatus.Failed"/>; <c>null</c> for a step-less execution, which has
    /// no <see cref="StepState"/> of its own.
    /// </param>
    public sealed record Target(ExecutionId ExecutionId, StepId? StepId, string Worker, StepStatus? Status);

    /// <summary>
    /// Every execution that could be arrested right now: each Running or quota-parked step's latest
    /// execution, then every step-less supplementary execution still awaiting completion. In
    /// <see cref="FlowState.Steps"/> order, then <see cref="FlowState.StepLessExecutions"/> order.
    /// </summary>
    public static IReadOnlyList<Target> All(FlowState state, WorkflowDefinitionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(snapshot);

        var workerByStepId = snapshot.Steps.ToDictionary(s => s.StepId, s => s.Worker);
        var targets = new List<Target>();

        foreach (var step in state.Steps)
        {
            if (step.LatestExecutionId is not { } executionId || !IsStepArrestable(step))
            {
                continue;
            }

            targets.Add(new Target(executionId, step.StepId, workerByStepId[step.StepId], step.Status));
        }

        foreach (var stepLessExecution in state.StepLessExecutions)
        {
            targets.Add(new Target(stepLessExecution.ExecutionId, StepId: null, stepLessExecution.Worker, Status: null));
        }

        return targets;
    }

    /// <summary>"Is this id still arrestable?" — replaces P2, the settle re-check.</summary>
    public static Target? Find(FlowState state, WorkflowDefinitionSnapshot snapshot, ExecutionId id) =>
        All(state, snapshot).FirstOrDefault(t => t.ExecutionId == id);

    /// <param name="Single">
    /// The one candidate execution's id, or <c>null</c> when <see cref="Candidates"/> does not contain
    /// exactly one entry.
    /// </param>
    /// <param name="Candidates">
    /// Every currently-<see cref="StepStatus.Running"/> or quota-parked step's latest execution id, in
    /// <see cref="FlowState.Steps"/> order — the candidate list a refusal message names.
    /// </param>
    public sealed record SingleLaneResult(ExecutionId? Single, IReadOnlyList<ExecutionId> Candidates);

    /// <summary>
    /// "Which single STEP lane does the operator mean?" — replaces P1. Step-tied only, deliberately
    /// (ruling Q3, #1530): a step-less execution never becomes a room-level targeting candidate. Takes
    /// no <see cref="WorkflowDefinitionSnapshot"/> because a step-tied candidate list needs no worker
    /// binding — unlike <see cref="All"/>, whose <see cref="Target.Worker"/> field callers like
    /// <c>NonProcessCancellationDetector</c> depend on.
    /// </summary>
    public static SingleLaneResult ResolveSingleStepLane(FlowState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var candidates = state.Steps
            .Where(s => s.LatestExecutionId is not null && IsStepArrestable(s))
            .Select(s => s.LatestExecutionId!.Value)
            .ToList();

        return new SingleLaneResult(candidates.Count == 1 ? candidates[0] : null, candidates);
    }

    /// <summary>
    /// The canonical step-tied arrest predicate. An un-foreclosed scheduled retry remains arrestable.
    /// An <see cref="FailureClassification.ExhaustedUntil"/> failure with no recorded reset is also
    /// a park: <c>MutationInterface</c> deliberately leaves it without a retry obligation when it
    /// cannot derive a reset or fallback. A foreclosure closes either shape, while an ordinary failed
    /// step with neither a retry obligation nor the quota-park classification remains terminal.
    /// </summary>
    private static bool IsStepArrestable(StepState step) =>
        step.Status == StepStatus.Running
        || (step.Status == StepStatus.Failed
            && !step.RetryForeclosed
            && (step.RetryNotBefore is not null
                || step.LatestFailureClassification == FailureClassification.ExhaustedUntil));
}
