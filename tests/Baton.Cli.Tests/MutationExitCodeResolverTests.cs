using Baton.Domain;

namespace Baton.Cli.Tests;

/// <summary>
/// Pure unit coverage for <see cref="MutationExitCodeResolver"/> (#1650 F2) — the 0/1 table
/// <c>baton cancel</c>/<c>baton decide</c>/<c>baton supply</c> keep — against hand-built
/// <see cref="FlowState"/>s, mirroring <see cref="WorkflowOutcomeAndExitCodeTests"/>'s discipline for
/// the richer <see cref="RunExitCodeResolver"/> table.
/// <para>
/// Deliberately platform-agnostic, unlike <c>CancelCommandEndToEndTests</c>'s Windows-only arm: the
/// queued fall-through is reached there via a real OS sharing violation, but the classification it
/// feeds is ordinary logic and should be pinned everywhere, not only where the OS can reproduce the
/// collision that produces it.
/// </para>
/// </summary>
public class MutationExitCodeResolverTests
{
    private static readonly WorkflowDefinitionSnapshotId SnapshotId = new(Guid.NewGuid().ToString("N"));

    [Fact]
    public void A_terminal_all_succeeded_room_is_exit_0()
    {
        var state = State(WorkflowStatus.Terminal, Step(StepStatus.Succeeded), Step(StepStatus.Succeeded));

        Assert.Equal(MutationExitCodeResolver.Success, MutationExitCodeResolver.Resolve(Result(state)));
    }

    [Fact]
    public void A_terminal_room_with_one_failed_step_is_exit_1()
    {
        var state = State(WorkflowStatus.Terminal, Step(StepStatus.Succeeded), Step(StepStatus.Failed));

        Assert.Equal(MutationExitCodeResolver.Failure, MutationExitCodeResolver.Resolve(Result(state)));
    }

    [Fact]
    public void A_room_that_has_not_reached_Terminal_is_exit_1_even_with_every_step_succeeded()
    {
        // The pre-#1650 expression's own polarity, carried over unchanged: reaching a fixed point that
        // is merely Paused is not a completed workflow.
        var state = State(WorkflowStatus.Paused, Step(StepStatus.Succeeded));

        Assert.Equal(MutationExitCodeResolver.Failure, MutationExitCodeResolver.Resolve(Result(state)));
    }

    [Fact]
    public void A_queued_cancellation_is_exit_1_even_when_the_room_itself_reads_terminal_and_succeeded()
    {
        // #1650 F2. CancelCommand's live-pump fall-through wrote a cancel.request file and re-projected;
        // the room may genuinely be finished, but THIS invocation applied nothing. The resolver's own
        // comment carries why that is worth an exit code of its own.
        var state = State(WorkflowStatus.Terminal, Step(StepStatus.Succeeded));

        Assert.Equal(
            MutationExitCodeResolver.Failure,
            MutationExitCodeResolver.Resolve(Result(state, cancellationQueued: true)));
    }

    [Fact]
    public void The_queued_flag_is_the_only_difference_between_the_two_verdicts_on_one_room()
    {
        // Polarity in both directions on a single state: the flag alone flips it, so the arm above is
        // not passing on some property of the state it was handed.
        var state = State(WorkflowStatus.Terminal, Step(StepStatus.Succeeded));

        Assert.Equal(MutationExitCodeResolver.Success, MutationExitCodeResolver.Resolve(Result(state)));
        Assert.Equal(
            MutationExitCodeResolver.Failure,
            MutationExitCodeResolver.Resolve(Result(state, cancellationQueued: true)));
    }

    [Fact]
    public void A_zero_step_terminal_room_is_exit_0_vacuously_matching_the_expression_this_replaced()
    {
        var state = State(WorkflowStatus.Terminal);

        Assert.Equal(MutationExitCodeResolver.Success, MutationExitCodeResolver.Resolve(Result(state)));
    }

    [Fact]
    public void A_null_result_is_refused_rather_than_classified()
    {
        Assert.Throws<ArgumentNullException>(() => MutationExitCodeResolver.Resolve(null!));
    }

    /// <summary>
    /// #2073: the idempotent arm. A re-run cancel against an already-Failed room writes nothing and
    /// must exit 0 — the state alone reads 1 here, which is exactly what the flag exists to override.
    /// </summary>
    [Fact]
    public void A_no_op_cancel_is_exit_0_even_when_the_room_itself_reads_terminal_and_failed()
    {
        var state = State(WorkflowStatus.Terminal, Step(StepStatus.Failed));

        Assert.Equal(MutationExitCodeResolver.Failure, MutationExitCodeResolver.Resolve(Result(state)));
        Assert.Equal(MutationExitCodeResolver.Success, MutationExitCodeResolver.Resolve(Result(state, cancelWasNoOp: true)));
    }

    private static FlowState State(WorkflowStatus status, params StepState[] steps) =>
        new(SnapshotId, steps, status);

    private static StepState Step(StepStatus status) =>
        new(new StepId(Guid.NewGuid().ToString("N")), status, new ExecutionId(Guid.NewGuid().ToString("N")),
            new Dictionary<StepId, ExecutionId>());

    private static CommandResult Result(FlowState state, bool cancellationQueued = false, bool cancelWasNoOp = false) => new(
        state,
        new WorkflowDefinitionSnapshot(SnapshotId, new WorkflowTemplateId("t"), 1, []),
        CancellationQueued: cancellationQueued,
        CancelWasNoOp: cancelWasNoOp);
}
