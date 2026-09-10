using Baton.Domain;

namespace Baton.Cli.Tests;

/// <summary>
/// #1495's room-level targeting rule: exactly one candidate resolves; zero or more than one refuses
/// (naming every candidate) rather than guessing. Shared by <see cref="CancelCommand"/>'s
/// <c>--execution</c>-omitted path and <see cref="CancelRequestPoller"/>'s <c>latest</c> resolution.
/// #1607 widened the candidate set beyond <see cref="StepStatus.Running"/> to also include a
/// quota-parked step as <see cref="ArrestableExecutions"/> defines — so this file's own tests pin
/// that shape rather than "any Failed step".
/// </summary>
public class RunningExecutionResolverTests
{
    private static readonly WorkflowDefinitionSnapshotId SnapshotId = new("snapshot-1");

    [Fact]
    public void Exactly_one_Running_step_resolves_to_its_execution_id()
    {
        var runningExecutionId = new ExecutionId("exec-running");
        var state = new FlowState(SnapshotId, [
            Step("a", StepStatus.Running, runningExecutionId),
            Step("b", StepStatus.Succeeded, new ExecutionId("exec-b")),
        ]);

        var result = RunningExecutionResolver.Resolve(state);

        Assert.Equal(runningExecutionId, result.Single);
        Assert.Equal([runningExecutionId], result.RunningExecutionIds);
    }

    [Fact]
    public void Zero_Running_steps_resolve_to_null_with_an_empty_candidate_list()
    {
        var state = new FlowState(SnapshotId, [
            Step("a", StepStatus.Succeeded, new ExecutionId("exec-a")),
            Step("b", StepStatus.Pending, null),
        ]);

        var result = RunningExecutionResolver.Resolve(state);

        Assert.Null(result.Single);
        Assert.Empty(result.RunningExecutionIds);
    }

    [Fact]
    public void More_than_one_Running_step_resolves_to_null_naming_every_candidate()
    {
        var first = new ExecutionId("exec-a");
        var second = new ExecutionId("exec-b");
        var state = new FlowState(SnapshotId, [
            Step("a", StepStatus.Running, first),
            Step("b", StepStatus.Running, second),
        ]);

        var result = RunningExecutionResolver.Resolve(state);

        Assert.Null(result.Single);
        Assert.Equal([first, second], result.RunningExecutionIds);
    }

    [Fact]
    public void Exactly_one_quota_parked_step_resolves_to_its_execution_id()
    {
        var parkedExecutionId = new ExecutionId("exec-parked");
        var state = new FlowState(SnapshotId, [
            Step("a", StepStatus.Failed, parkedExecutionId, FailureClassification.ExhaustedUntil, retryNotBefore: DateTimeOffset.UtcNow.AddHours(1)),
            Step("b", StepStatus.Succeeded, new ExecutionId("exec-b")),
        ]);

        var result = RunningExecutionResolver.Resolve(state);

        Assert.Equal(parkedExecutionId, result.Single);
        Assert.Equal([parkedExecutionId], result.RunningExecutionIds);
    }

    /// <summary>
    /// The polarity arm that stops the predicate drifting to "any Failed step": a Failed step with no
    /// scheduled retry (exhausted its policy, or was rejected) is NOT a candidate — it is genuinely
    /// terminal, not parked.
    /// </summary>
    [Fact]
    public void An_ordinary_terminal_Failed_step_with_no_RetryNotBefore_is_not_a_candidate()
    {
        var state = new FlowState(SnapshotId, [
            Step("a", StepStatus.Failed, new ExecutionId("exec-terminal-failed"), FailureClassification.Permanent, retryNotBefore: null),
        ]);

        var result = RunningExecutionResolver.Resolve(state);

        Assert.Null(result.Single);
        Assert.Empty(result.RunningExecutionIds);
    }

    [Fact]
    public void Exactly_one_unknown_reset_quota_park_resolves_to_its_execution_id()
    {
        var parkedExecutionId = new ExecutionId("exec-unknown-reset-parked");
        var state = new FlowState(SnapshotId, [
            Step("a", StepStatus.Failed, parkedExecutionId, FailureClassification.ExhaustedUntil),
        ]);

        var result = RunningExecutionResolver.Resolve(state);

        Assert.Equal(parkedExecutionId, result.Single);
        Assert.Equal([parkedExecutionId], result.RunningExecutionIds);
    }

    [Fact]
    public void An_already_foreclosed_quota_park_is_not_a_candidate()
    {
        var state = new FlowState(SnapshotId, [
            Step(
                "a", StepStatus.Failed, new ExecutionId("exec-foreclosed-parked"),
                FailureClassification.ExhaustedUntil,
                retryNotBefore: DateTimeOffset.UtcNow.AddHours(1),
                retryForeclosed: true),
        ]);

        var result = RunningExecutionResolver.Resolve(state);

        Assert.Null(result.Single);
        Assert.Empty(result.RunningExecutionIds);
    }

    [Fact]
    public void A_Running_step_and_a_quota_parked_step_together_are_ambiguous()
    {
        var running = new ExecutionId("exec-running");
        var parked = new ExecutionId("exec-parked");
        var state = new FlowState(SnapshotId, [
            Step("a", StepStatus.Running, running),
            Step("b", StepStatus.Failed, parked, FailureClassification.ExhaustedUntil, retryNotBefore: DateTimeOffset.UtcNow.AddHours(1)),
        ]);

        var result = RunningExecutionResolver.Resolve(state);

        Assert.Null(result.Single);
        Assert.Equal([running, parked], result.RunningExecutionIds);
    }

    [Fact]
    public void More_than_one_quota_parked_step_resolves_to_null_naming_every_candidate()
    {
        var first = new ExecutionId("exec-parked-a");
        var second = new ExecutionId("exec-parked-b");
        var state = new FlowState(SnapshotId, [
            Step("a", StepStatus.Failed, first, FailureClassification.ExhaustedUntil, retryNotBefore: DateTimeOffset.UtcNow.AddHours(1)),
            Step("b", StepStatus.Failed, second, FailureClassification.ExhaustedUntil, retryNotBefore: DateTimeOffset.UtcNow.AddMinutes(30)),
        ]);

        var result = RunningExecutionResolver.Resolve(state);

        Assert.Null(result.Single);
        Assert.Equal([first, second], result.RunningExecutionIds);
    }

    private static StepState Step(
        string stepId,
        StepStatus status,
        ExecutionId? latestExecutionId,
        FailureClassification? failureClassification = null,
        DateTimeOffset? retryNotBefore = null,
        bool retryForeclosed = false) =>
        new(
            new StepId(stepId), status, latestExecutionId, new Dictionary<StepId, ExecutionId>(),
            LatestFailureClassification: failureClassification,
            RetryNotBefore: retryNotBefore,
            RetryForeclosed: retryForeclosed);
}
