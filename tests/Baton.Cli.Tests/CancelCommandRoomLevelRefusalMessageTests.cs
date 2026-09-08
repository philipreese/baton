using Baton.Cli.Tests.TestSupport;
using Baton.Concurrency;
using Baton.Domain;
using Baton.Status;
using Baton.Store;
using Baton.Templates;
using Baton.Vendors;

namespace Baton.Cli.Tests;

/// <summary>
/// #1607 review findings F4/F5: <see cref="RunningExecutionResolverTests"/> pins the resolver's own
/// predicate, but nothing previously asserted on the CLI-side refusal text
/// <see cref="CancelCommand"/>'s <c>ResolveRunningExecution</c> actually throws for the
/// zero-candidate and ambiguous-candidate room-level cases — collapsing either message back to its
/// pre-#1607 wording would have passed every other test in this project unnoticed.
/// <para>
/// #2073: the zero-candidate arm is now proven against a NON-terminal room (a step succeeded, its
/// dependant not yet dispatched) — a fully terminal room no longer reaches the resolver at all, it
/// takes the idempotent no-op arm (<see cref="CancelCommandArrestTests"/>).
/// </para>
/// </summary>
public class CancelCommandRoomLevelRefusalMessageTests
{
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter> { ["shell"] = new ShellCommandWorkerAdapter() };

    [Fact]
    public async Task Bare_cancel_against_a_non_terminal_room_with_nothing_arrestable_names_zero_candidates_and_points_at_status()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            await WriteSucceededThenPendingFixtureAsync(roomDirectory);

            var cancelOptions = new CancelOptions(roomDirectory, ExecutionId: null, BindingsFilePath: "ignored");
            var ex = await Assert.ThrowsAsync<CliArgumentException>(
                () => CancelCommand.ExecuteAsync(cancelOptions, Adapters, TestContext.Current.CancellationToken));

            Assert.Contains("no currently-Running or", ex.Message, StringComparison.Ordinal);
            Assert.Contains("quota-parked step to target", ex.Message, StringComparison.Ordinal);
            Assert.NotNull(ex.TryInvocation);
            Assert.Contains("--execution", ex.TryInvocation, StringComparison.Ordinal);
            Assert.Contains("baton status", ex.TryInvocation, StringComparison.Ordinal);

            // A refusal writes nothing: no intent fact for a target that was never resolved.
            Assert.False(File.Exists(Path.Combine(roomDirectory, BatonPaths.RoomLogFileName)));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// #2073: the other idempotent arm — an EXPLICIT target that has already settled in a room that
    /// is not itself terminal. Says so, writes nothing, exits 0.
    /// </summary>
    [Fact]
    public async Task Explicit_cancel_of_an_already_settled_execution_in_a_non_terminal_room_is_a_no_op()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var succeededExecutionId = await WriteSucceededThenPendingFixtureAsync(roomDirectory);

            var result = await CancelCommand.ExecuteAsync(
                new CancelOptions(roomDirectory, succeededExecutionId.Value, BindingsFilePath: "ignored"), Adapters, TestContext.Current.CancellationToken);

            Assert.True(result.CancelWasNoOp);
            Assert.Equal(MutationExitCodeResolver.Success, MutationExitCodeResolver.Resolve(result));
            Assert.False(File.Exists(Path.Combine(roomDirectory, BatonPaths.RoomLogFileName)));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// F5: a Running step plus a quota-parked sibling — the case spec/baton.md §2 calls out as newly
    /// ambiguous since #1607's widening. <c>flow.lock</c> is held directly (as
    /// <see cref="CancelCommandParkedRoomLevelTargetingTests"/> already does) so that, were the
    /// resolver ever to pick one, the test could not settle a room by accident.
    /// </summary>
    [Fact]
    public async Task Bare_cancel_against_a_running_step_and_a_parked_sibling_labels_which_is_which()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var (runningExecutionId, parkedExecutionId) = await WriteRunningAndParkedFixtureAsync(roomDirectory);

            using (ConcurrencyGuard.Acquire(roomDirectory, "test holder simulating a live pump"))
            {
                var cancelOptions = new CancelOptions(roomDirectory, ExecutionId: null, BindingsFilePath: "ignored");
                var ex = await Assert.ThrowsAsync<CliArgumentException>(
                    () => CancelCommand.ExecuteAsync(cancelOptions, Adapters, TestContext.Current.CancellationToken));

                Assert.Contains($"{runningExecutionId.Value} (Running)", ex.Message, StringComparison.Ordinal);
                Assert.Contains($"{parkedExecutionId.Value} (quota-parked)", ex.Message, StringComparison.Ordinal);
                Assert.NotNull(ex.TryInvocation);
                Assert.Contains("--execution", ex.TryInvocation, StringComparison.Ordinal);
                Assert.Contains("baton status", ex.TryInvocation, StringComparison.Ordinal);
            }
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>Step <c>a</c> succeeded; step <c>b</c> depends on it and was never dispatched — the room is not Terminal, and nothing in it is arrestable.</summary>
    private static async Task<ExecutionId> WriteSucceededThenPendingFixtureAsync(string roomDirectory)
    {
        Directory.CreateDirectory(roomDirectory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("succeeded-then-pending"),
            1,
            [
                new WorkflowStepDefinition(new StepId("a"), "a", [], ["out-a"], [], new RetryPolicy(1)),
                new WorkflowStepDefinition(new StepId("b"), "b", ["out-a"], ["out-b"], [new StepId("a")], new RetryPolicy(1)),
            ]);
        var snapshot = SnapshotBinder.Bind(definition);
        await SnapshotBinder.PersistAsync(snapshot, Path.Combine(roomDirectory, BatonPaths.SnapshotFileName), TestContext.Current.CancellationToken);

        var executionId = new ExecutionId("exec-a-1");
        await using var writer = new FlowEventLogWriter(Path.Combine(roomDirectory, BatonPaths.FlowLogFileName));
        await writer.AppendAsync(
            new FlowEvent.ExecutionRequestAccepted(new ExecutionRequest(
                executionId, new WorkflowId("wf-pending"), new StepId("a"), "a",
                Inputs: [], Outputs: ["out-a"], Timeout: TimeSpan.FromSeconds(30), Environment: [],
                UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>())),
            TestContext.Current.CancellationToken);
        await writer.AppendAsync(new FlowEvent.ExecutionSucceeded(executionId), TestContext.Current.CancellationToken);
        return executionId;
    }

    private static async Task<(ExecutionId RunningExecutionId, ExecutionId ParkedExecutionId)> WriteRunningAndParkedFixtureAsync(
        string roomDirectory)
    {
        Directory.CreateDirectory(roomDirectory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("running-and-parked-probe"),
            1,
            [
                new WorkflowStepDefinition(new StepId("running-step"), "running-step", [], ["out-a"], [], new RetryPolicy(3)),
                new WorkflowStepDefinition(new StepId("parked-step"), "parked-step", [], ["out-b"], [], new RetryPolicy(3)),
            ]);
        var snapshot = SnapshotBinder.Bind(definition);
        var snapshotPath = Path.Combine(roomDirectory, "snapshot.json");
        await SnapshotBinder.PersistAsync(snapshot, snapshotPath, TestContext.Current.CancellationToken);

        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var runningExecutionId = new ExecutionId("exec-running-1");
        var parkedExecutionId = new ExecutionId("exec-parked-1");
        var retryNotBefore = DateTimeOffset.UtcNow.AddMinutes(45);

        await using (var writer = new FlowEventLogWriter(logPath))
        {
            await writer.AppendAsync(
                new FlowEvent.ExecutionRequestAccepted(new ExecutionRequest(
                    runningExecutionId,
                    new WorkflowId("wf-mixed"),
                    new StepId("running-step"),
                    "running-step",
                    Inputs: [],
                    Outputs: [],
                    Timeout: TimeSpan.FromSeconds(30),
                    Environment: [],
                    UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>())),
                TestContext.Current.CancellationToken);

            await writer.AppendAsync(
                new FlowEvent.ExecutionRequestAccepted(new ExecutionRequest(
                    parkedExecutionId,
                    new WorkflowId("wf-mixed"),
                    new StepId("parked-step"),
                    "parked-step",
                    Inputs: [],
                    Outputs: [],
                    Timeout: TimeSpan.FromSeconds(30),
                    Environment: [],
                    UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>())),
                TestContext.Current.CancellationToken);
            await writer.AppendAsync(
                new FlowEvent.ExecutionFailed(parkedExecutionId, FailureClassification.ExhaustedUntil, "attempt failed", retryNotBefore),
                TestContext.Current.CancellationToken);
            await writer.AppendAsync(
                new FlowEvent.StepRetryScheduled(new StepId("parked-step"), parkedExecutionId, retryNotBefore, 2_700_000),
                TestContext.Current.CancellationToken);
        }

        return (runningExecutionId, parkedExecutionId);
    }
}
