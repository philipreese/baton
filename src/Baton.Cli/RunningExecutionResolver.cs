using Baton.Domain;
using Baton.Projection;

namespace Baton.Cli;

/// <summary>
/// Resolves "the target lane" (#1495, widened by #1607) from a room's own projected
/// <see cref="FlowState"/> — the shared targeting rule for the three CLI callers:
/// <see cref="CancelCommand"/>'s room-level <c>--execution</c>-omitted path,
/// <see cref="CancelRequestPoller"/>'s <c>latest</c> literal, and <see cref="ExecutionProgressHeartbeat"/>'s
/// own tick. A candidate is either a currently <see cref="StepStatus.Running"/> step, or a
/// quota-parked one as <see cref="ArrestableExecutions"/> defines.
/// Fail closed: zero or more than one candidate is refused rather than guessed.
/// <para>
/// #1556 PR 1: a two-line shim over <see cref="ArrestableExecutions.ResolveSingleStepLane"/>, the one
/// register this shape now lives in (record-once — see that type's own remarks for the other two
/// predicates it also replaced). Kept as its own type rather than deleted, so its three existing
/// callers and their own doc comments naming it stay put.
/// </para>
/// </summary>
public static class RunningExecutionResolver
{
    /// <param name="Single">
    /// The one candidate execution's id, or <c>null</c> when <see cref="RunningExecutionIds"/> does
    /// not contain exactly one entry.
    /// </param>
    /// <param name="RunningExecutionIds">
    /// Every currently-<see cref="StepStatus.Running"/> or quota-parked step's latest execution id,
    /// in <see cref="FlowState.Steps"/> order — the candidate list a refusal message names. The name
    /// predates #1607's widening (record-once: kept rather than renamed, to avoid touching every
    /// caller and message for a cosmetic-only change) — it no longer means "Running only."
    /// </param>
    public sealed record Result(ExecutionId? Single, IReadOnlyList<ExecutionId> RunningExecutionIds);

    public static Result Resolve(FlowState state)
    {
        var resolved = ArrestableExecutions.ResolveSingleStepLane(state);
        return new Result(resolved.Single, resolved.Candidates);
    }
}
