using Baton.Cli.Tests.TestSupport;
using Baton.Concurrency;
using Baton.Domain;
using Baton.Status;
using Baton.Store;
using Baton.Templates;
using Baton.Vendors;

namespace Baton.Cli.Tests;

/// <summary>
/// #1495, restated for #2073: <c>baton cancel</c> against a room whose <c>flow.lock</c> is held —
/// genuinely live pump or not, the guard cannot tell the difference — never contends the lock. It
/// writes <see cref="CancelRequestFile"/> and waits for an answer. Holds the lock directly via
/// <see cref="ConcurrencyGuard.Acquire"/> rather than racing a real pump, so this is deterministic and
/// isolates exactly the held-lock branch; a pump that DOES answer is
/// <see cref="CancelCommandArrestTests"/>'s first test, and the real cross-process case is
/// <see cref="LiveCancelRequestChannelEndToEndTests"/>.
/// </summary>
[Collection(WorkingDirectoryCollection.Name)]
public class CancelCommandWorkflowLockedFallThroughTests
{
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter> { ["shell"] = new ShellCommandWorkerAdapter() };

    [Fact]
    public async Task Cancelling_a_room_whose_flow_lock_is_held_writes_the_request_file_and_reports_queued_when_nobody_answers()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var executionId = await WriteOpenRoomWithoutAWorkerPidAsync(roomDirectory);

            using (ConcurrencyGuard.Acquire(roomDirectory, "test holder simulating a live pump"))
            {
                var result = await CancelCommand.ExecuteAsync(
                    new CancelOptions(roomDirectory, executionId.Value, BindingsFilePath: "ignored"),
                    Adapters,
                    TestContext.Current.CancellationToken,
                    pumpAnswerWindow: TimeSpan.FromMilliseconds(300));

                // Nobody answered and the holder never let go: queued, not applied -- and nothing
                // was written to flow.jsonl, since only the lock's holder may settle it.
                Assert.True(result.CancellationQueued);
                Assert.Equal(WorkflowStatus.Running, result.State.Status);
                Assert.Equal(MutationExitCodeResolver.Failure, MutationExitCodeResolver.Resolve(result));

                var requestFilePath = CancelRequestFile.GetPath(roomDirectory);
                Assert.True(File.Exists(requestFilePath), "expected cancel.request to have been written");
                var content = await CancelRequestFile.TryReadAsync(requestFilePath, TestContext.Current.CancellationToken);
                Assert.NotNull(content);
                Assert.Equal(executionId.Value, content.Target);

                var events = await new FlowEventLogReader(Path.Combine(roomDirectory, BatonPaths.FlowLogFileName)).ReadAllAsync(TestContext.Current.CancellationToken);
                Assert.DoesNotContain(events, e => e is FlowEvent.ExecutionFailed or FlowEvent.StepRetryForeclosed);

                // The intent is on record regardless of who ends up settling the room.
                var roomEvents = await new RoomEventLogReader(Path.Combine(roomDirectory, BatonPaths.RoomLogFileName)).ReadAllRoomEventsAsync(TestContext.Current.CancellationToken);
                Assert.Single(roomEvents.OfType<RoomEvent.ArrestIntentRecorded>());
            }
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>A Running step with no <see cref="CoreEvent.ExecutionStarted"/> — nothing for the kill path to aim at, so the held-lock outcome is purely the queued report.</summary>
    private static async Task<ExecutionId> WriteOpenRoomWithoutAWorkerPidAsync(string roomDirectory)
    {
        Directory.CreateDirectory(roomDirectory);
        var step = new StepId("a");
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("one-open-step-fallthrough"), 1,
            [new WorkflowStepDefinition(step, "a", [], ["out"], [], new RetryPolicy(1))]);
        var snapshot = SnapshotBinder.Bind(definition);
        await SnapshotBinder.PersistAsync(snapshot, Path.Combine(roomDirectory, BatonPaths.SnapshotFileName), TestContext.Current.CancellationToken);

        var executionId = new ExecutionId("exec-open-1");
        var request = new ExecutionRequest(
            executionId, new WorkflowId("wf-fallthrough"), step, "a",
            Inputs: [], Outputs: [], Timeout: TimeSpan.FromMinutes(5), Environment: [],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

        await using var writer = new FlowEventLogWriter(Path.Combine(roomDirectory, BatonPaths.FlowLogFileName));
        await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(request), TestContext.Current.CancellationToken);
        return executionId;
    }
}
