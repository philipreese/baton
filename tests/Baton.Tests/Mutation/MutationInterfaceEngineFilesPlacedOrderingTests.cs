using Baton.Dispatch;
using Baton.Domain;
using Baton.Mutation;
using Baton.Store;
using Baton.Tests.TestSupport;

namespace Baton.Tests.Mutation;

/// <summary>
/// #1929 review round 3 (LOW): <see cref="FlowEvent.EngineFilesPlaced"/> is journaled from the
/// dispatcher's own pre-spawn placement callback, NOT from the result <c>DispatchAsync</c> returns.
/// The distinction is the whole fix, and the crash window it removes is described once on
/// <see cref="FlowEvent.EngineFilesPlaced"/>.
/// </summary>
/// <remarks>
/// Two arms, one condition apart, and the first is the discriminating control: a dispatch that returns
/// a result CARRYING placed files but never raises the callback must journal nothing. A test that only
/// asserted the second arm would pass unchanged against the old post-return append.
/// <para>
/// This pins MutationInterface's half (which signal the append hangs off).
/// <c>CoreDispatcherTests.The_seed_copy_placement_is_announced_before_the_worker_runs</c> pins the
/// dispatcher's half (that the signal itself precedes the spawn). Neither alone is the claim.
/// </para>
/// </remarks>
public class MutationInterfaceEngineFilesPlacedOrderingTests
{
    private static readonly StepId Implement = new("implement");
    private static readonly WorkerContract Contract = new("skill-worker", [], [], []);

    private static readonly EnginePlacedFile[] Placed =
        [new(@"C:\repo\.claude\skills\audit-tool\SKILL.md", "9f2b1c")];

    [Fact]
    public async Task A_result_carrying_placed_files_journals_nothing_without_the_pre_spawn_callback()
    {
        var events = await RunAsync(raisePlacementCallback: false);
        Assert.DoesNotContain(events, e => e is FlowEvent.EngineFilesPlaced);
    }

    [Fact]
    public async Task The_room_fact_is_journaled_from_the_pre_spawn_placement_callback()
    {
        var events = await RunAsync(raisePlacementCallback: true);

        var placed = Assert.Single(events.OfType<FlowEvent.EngineFilesPlaced>());
        Assert.Equal(Placed, placed.Files);
        Assert.Equal(["audit-tool"], placed.Groups);

        // Ordering, not merely presence: the fact is on disk before anything this execution's outcome
        // is derived from. The callback runs inside DispatchAsync, so the append precedes the exit the
        // crash-recovery path would read.
        var factIndex = events.FindIndex(e => e is FlowEvent.EngineFilesPlaced);
        var outcomeIndex = events.FindIndex(e => e is FlowEvent.ExecutionSucceeded or FlowEvent.ExecutionFailed);
        Assert.True(factIndex >= 0 && outcomeIndex > factIndex);
    }

    private static async Task<List<FlowEvent>> RunAsync(bool raisePlacementCallback)
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");

        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-1929-order"),
                new WorkflowTemplateId("template-1929-order"),
                WorkflowTemplateVersion: 1,
                Steps:
                [
                    new WorkflowStepDefinition(
                        Implement, "skill-worker", Inputs: [], Outputs: [], DependsOn: [],
                        RetryPolicy: new RetryPolicy(MaxAttempts: 1, Backoff: BackoffPolicy.None)),
                ]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["skill-worker"] = new WorkerBinding.Process(
                    Contract, new CoreDispatchTarget("skill-worker-cli", []), TimeSpan.FromMinutes(60)),
            };

            var stub = new StubCoreDispatcher();
            if (raisePlacementCallback)
            {
                stub.RaisePlacementOnDispatch = Placed;
                stub.RaisePlacementGroups = ["audit-tool"];
            }

            // Both arms return the SAME result shape, so the only difference between them is whether the
            // pre-spawn callback was raised.
            var completion = stub.EnqueueResult(Implement);
            completion.SetResult(new CoreDispatchResult(0, CoreExitReason.Natural, EnginePlacedFiles: Placed));

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);

            await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-1929-order"), roomDirectory, snapshot, bindings, artifactsRoot,
                reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);

            return [.. await reader.ReadAllAsync(TestContext.Current.CancellationToken)];
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }
}
