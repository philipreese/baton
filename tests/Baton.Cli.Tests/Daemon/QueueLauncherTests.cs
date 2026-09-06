using System.Text.Json;
using Baton.Cli.Daemon;
using Baton.Cli.Mcp;
using Baton.Cli.Tests.TestSupport;
using Baton.Domain;
using Baton.Queue;
using Baton.Status;
using Baton.Store;
using Baton.Templates;
using Baton.Vendors;
using Xunit;

namespace Baton.Cli.Tests.Daemon;

/// <summary>
/// The launcher's post-launch settle path (#1939 review) —
/// <see cref="QueueLauncher.SettleFinishedPumpAsync"/>'s three pump outcomes and
/// <see cref="QueueLauncher.RecordPostLaunchFaultAsync"/>'s own arms, whose remarks say what each is
/// for. Before them, the item this lane belonged to stayed launched forever with only a daemon stderr
/// line to say otherwise — and, in round 2's finding, the record that did land erased what the lane
/// had actually done.
/// </summary>
public sealed class QueueLauncherTests : IDisposable
{
    private readonly IsolatedBatonHome _batonHome = new();

    public void Dispose() => _batonHome.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> NoOpAdapters =
        new Dictionary<string, IWorkerAdapter> { [NoOpWorkerAdapter.AdapterName] = new NoOpWorkerAdapter() };

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "baton_queue_launcher_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public async Task A_lane_that_faults_after_launch_leaves_the_room_a_failed_sentinel_the_scheduler_can_read()
    {
        var root = CreateTempRoot();
        try
        {
            // The pre-ledger control for the projecting arm below: a room with nothing to project still
            // gets its sentinel, because the write is what resolves the item.
            var room = Path.Combine(root, "queue-t1-abcd");
            Directory.CreateDirectory(room);

            await QueueLauncher.RecordPostLaunchFaultAsync("t1", room, "the pump threw BatonFlowException");

            var sentinel = await TerminalSentinelWriter.TryReadAsync(room, Ct);
            Assert.NotNull(sentinel);
            Assert.Equal(WorkflowOutcome.Failed, sentinel.State);
            Assert.Contains("did not complete after launch", sentinel.Error!, StringComparison.Ordinal);
            Assert.Contains("BatonFlowException", sentinel.Error, StringComparison.Ordinal);
            Assert.Empty(sentinel.Steps);

            // The whole point: the classifier the scheduler runs over this file now fails the item.
            Assert.Equal(QueueItemState.Failed, QueueSchedulerService.ClassifyTerminal(sentinel, room).State);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public async Task A_held_ledger_is_projected_after_its_holder_releases_it()
    {
        var root = CreateTempRoot();
        try
        {
            var room = await RunTwoStepRoomAsync(root);
            var ledgerPath = Path.Combine(room, BatonPaths.FlowLogFileName);
            using var holder = new FileStream(ledgerPath, FileMode.Open, FileAccess.Read, FileShare.None);

            var recording = QueueLauncher.RecordPostLaunchFaultAsync("held", room, "the pump threw BatonFlowException");

            // The held path has started but must not degrade while its bounded retry is pending. The
            // pre-#1951 implementation completed here with a bare sentinel.
            // wait-ok: this is a readiness observation for the 100ms bounded projection backoff, not
            // an operator-facing recovery wait.
            await Task.Delay(TimeSpan.FromMilliseconds(150), Ct);
            Assert.False(recording.IsCompleted);

            holder.Dispose();
            await recording;

            var sentinel = await TerminalSentinelWriter.TryReadAsync(room, Ct);
            Assert.NotNull(sentinel);
            Assert.DoesNotContain("bare sentinel", sentinel.Error!, StringComparison.Ordinal);
            Assert.Equal(["a", "b"], sentinel.Steps.Select(step => step.Id).Order().ToArray());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public async Task A_corrupt_ledger_leaves_a_bare_sentinel_that_names_the_projection_failure()
    {
        var root = CreateTempRoot();
        try
        {
            var room = await RunTwoStepRoomAsync(root);
            await File.WriteAllTextAsync(
                Path.Combine(room, BatonPaths.FlowLogFileName), "{ corrupt jsonl\n", Ct);

            await QueueLauncher.RecordPostLaunchFaultAsync("corrupt", room, "the pump threw BatonFlowException");

            var sentinel = await TerminalSentinelWriter.TryReadAsync(room, Ct);
            Assert.NotNull(sentinel);
            Assert.Empty(sentinel.Steps);
            Assert.Contains("bare sentinel: ledger projection failed: Malformed line in the ledger", sentinel.Error!, StringComparison.Ordinal);

            await QueueStore.MutateAsync(
                BatonPaths.QueueFile,
                queue => queue with
                {
                    Items =
                    [
                        new QueueItem
                        {
                            Tag = "corrupt",
                            Role = "implement",
                            Workspace = root,
                            SpecFile = Path.Combine(root, "workflow.json"),
                            State = QueueItemState.Launched,
                            RoomDirectory = room,
                        },
                    ],
                },
                Ct);

            var scheduler = new QueueSchedulerService(
                (_, _) => Task.FromResult(new QueueLaunchOutcome(null)),
                _ => Task.FromResult(0d),
                () => null,
                () => DateTimeOffset.UtcNow);
            await scheduler.ResolveFinishedItemsAsync(Ct);

            var fact = Assert.Single(await QueueDecisionLedgerStore.ReadAllAsync(
                BatonPaths.QueueDecisionLedgerFile, Ct));
            Assert.Equal(QueueDecisionEntry.Failed, fact.Decision);
            Assert.Contains(sentinel.Error!, fact.Reason!, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    /// <summary>
    /// Round 2's MEDIUM, at the property spec/baton.md §13's post-launch bullet states: this sentinel
    /// carries the room's own projected record, because <c>fleet_status</c> returns the file verbatim
    /// and re-projects nothing.
    /// </summary>
    [Fact]
    public async Task A_faulted_lane_with_a_real_ledger_keeps_the_steps_that_ran_and_the_outputs_they_produced()
    {
        var root = CreateTempRoot();
        try
        {
            var room = await RunTwoStepRoomAsync(root);

            // The pump's own run wrote no sentinel: TerminalSettleRecorder is what does, and this lane
            // is the one that never reached it.
            Assert.Null(await TerminalSentinelWriter.TryReadAsync(room, Ct));

            await QueueLauncher.RecordPostLaunchFaultAsync("t2", room, "the pump threw BatonFlowException");

            var sentinel = await TerminalSentinelWriter.TryReadAsync(room, Ct);
            Assert.NotNull(sentinel);

            // The fault is still the terminal word and the terminal reason...
            Assert.Equal(WorkflowOutcome.Failed, sentinel.State);
            Assert.Contains("did not complete after launch", sentinel.Error!, StringComparison.Ordinal);
            Assert.Equal(QueueItemState.Failed, QueueSchedulerService.ClassifyTerminal(sentinel, room).State);

            // ...and the room's own record survives underneath it, which is what the reader lost.
            Assert.Equal(["a", "b"], sentinel.Steps.Select(step => step.Id).Order().ToArray());
            Assert.All(sentinel.Steps, step => Assert.Equal(nameof(StepStatus.Succeeded), step.State));
            Assert.Contains(sentinel.Outputs, path => path.EndsWith("out_a", StringComparison.Ordinal));
            Assert.Contains(sentinel.Outputs, path => path.EndsWith("out_b", StringComparison.Ordinal));

            // Read back off the bytes, not just the parse: fleet_status's fast path returns this file.
            var onDisk = JsonSerializer.Deserialize<WorkflowStatusView>(
                await File.ReadAllTextAsync(Path.Combine(room, "terminal.json"), Ct));
            Assert.Equal(2, onDisk!.Steps.Count);
            Assert.Equal(2, onDisk.Outputs.Count);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    /// <summary>
    /// The shape the fault path actually meets — a step still in flight when the pump threw. Its
    /// recorded <c>Running</c> is kept and the projection's live <c>liveness</c> probe is dropped —
    /// see spec/baton.md §13 (the same bullet the arm above cites) for the argument separating them.
    /// </summary>
    [Fact]
    public async Task A_step_still_in_flight_keeps_its_recorded_state_but_freezes_no_liveness_claim()
    {
        var root = CreateTempRoot();
        try
        {
            var room = await CreateRunningRoomAsync(root);

            await QueueLauncher.RecordPostLaunchFaultAsync("t3", room, "the pump threw BatonFlowException");

            var sentinel = await TerminalSentinelWriter.TryReadAsync(room, Ct);
            Assert.NotNull(sentinel);
            Assert.Equal(WorkflowOutcome.Failed, sentinel.State);

            var step = Assert.Single(sentinel.Steps);
            Assert.Equal("step-a", step.Id);
            Assert.Equal(nameof(StepStatus.Running), step.State);
            Assert.Null(step.Liveness);

            // The consumer that a frozen Running step could otherwise have wedged: the scan
            // QueueSchedulerService.CountLiveWeightAsync walks skips a room the moment it carries a
            // sentinel, so a settled lane never holds the queue's concurrency cap open.
            Assert.Null(await FleetStatusTool.ProcessRoomAsync(room, includeTerminal: false, Ct));

            // The control for the assertion above: the same projection, unfrozen, DOES claim liveness
            // for this step — so the null is this code path dropping it, not the projector never
            // populating it.
            using var statusJson = new StringWriter();
            await StatusCommand.ExecuteAsync(new StatusOptions(room, Json: true), statusJson, Ct);
            var live = JsonSerializer.Deserialize<WorkflowStatusView>(statusJson.ToString());
            Assert.NotNull(Assert.Single(live!.Steps).Liveness);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public async Task A_fault_before_the_room_was_provisioned_manufactures_no_room()
    {
        var root = CreateTempRoot();
        try
        {
            // The discriminating half of the pair above — QueueLauncher.RecordPostLaunchFaultAsync's
            // own remarks have the argument for why nothing is written here.
            var room = Path.Combine(root, "queue-t1-never-made");

            await QueueLauncher.RecordPostLaunchFaultAsync("t1", room, "refused before provisioning");

            Assert.False(Directory.Exists(room));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public async Task A_room_that_already_recorded_its_own_verdict_keeps_it()
    {
        var root = CreateTempRoot();
        try
        {
            var room = Path.Combine(root, "queue-t1-refused");
            await TerminalSentinelWriter.WriteValidationRefusedAsync(
                room, "spec file 'x.md' does not exist.", Ct, tryInvocation: "pass an existing file to --spec");

            await QueueLauncher.RecordPostLaunchFaultAsync("t1", room, "some later throw");

            // Both fields of the dispatch's own record survive, which is the point of not replacing it.
            var sentinel = await TerminalSentinelWriter.TryReadAsync(room, Ct);
            Assert.Equal("spec file 'x.md' does not exist.", sentinel!.Error);
            Assert.Equal("pass an existing file to --spec", sentinel.Try);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    /// <summary>
    /// Round 2's LOW: a pump whose <see cref="OperationCanceledException"/> escapes ends
    /// <see cref="TaskStatus.Canceled"/>, not faulted. The old <c>IsFaulted</c>-only gate sent it to the
    /// settle branch, where <c>completed.Result</c> throws unobserved and the item wedges in
    /// <see cref="QueueItemState.Launched"/>.
    /// </summary>
    [Fact]
    public async Task A_cancelled_pump_settles_the_room_rather_than_wedging_the_item_in_launched()
    {
        var root = CreateTempRoot();
        try
        {
            var room = Path.Combine(root, "queue-t4-cancelled");
            Directory.CreateDirectory(room);

            await QueueLauncher.SettleFinishedPumpAsync(
                Task.FromCanceled<CommandResult>(new CancellationToken(canceled: true)), "t4", room);

            var sentinel = await TerminalSentinelWriter.TryReadAsync(room, Ct);
            Assert.NotNull(sentinel);
            Assert.Equal(WorkflowOutcome.Failed, sentinel.State);
            Assert.Contains("cancelled after launch", sentinel.Error!, StringComparison.Ordinal);
            Assert.Equal(QueueItemState.Failed, QueueSchedulerService.ClassifyTerminal(sentinel, room).State);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    /// <summary>
    /// The polarity arm of the two above: a pump that completed is settled by
    /// <see cref="TerminalSettleRecorder"/> on its own terms, and this path fabricates nothing over it.
    /// A non-terminal result records nothing at all, so the absence here is that recorder's own contract
    /// rather than a fault sentinel that failed to write.
    /// </summary>
    [Fact]
    public async Task A_pump_that_completed_gets_no_fault_sentinel()
    {
        var root = CreateTempRoot();
        try
        {
            var room = Path.Combine(root, "queue-t5-completed");
            Directory.CreateDirectory(room);

            var snapshotId = new WorkflowDefinitionSnapshotId("done");
            var result = new CommandResult(
                new FlowState(snapshotId, [], WorkflowStatus.Running),
                new WorkflowDefinitionSnapshot(snapshotId, new WorkflowTemplateId("done"), 1, []),
                RoomDirectoryPath: room);

            await QueueLauncher.SettleFinishedPumpAsync(Task.FromResult(result), "t5", room);

            Assert.Null(await TerminalSentinelWriter.TryReadAsync(room, Ct));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    /// <summary>
    /// A room carried to Terminal through the real pump: two succeeded steps, each with a declared
    /// output, and the snapshot/ledger pair a projection needs. No terminal sentinel — writing that is
    /// <see cref="TerminalSettleRecorder"/>'s job, and the lane under test is the one that never reaches it.
    /// </summary>
    private static async Task<string> RunTwoStepRoomAsync(string root)
    {
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("queue-two-step"), 1,
            [
                new WorkflowStepDefinition(new StepId("a"), "a", [], ["out_a"], [], new RetryPolicy(1)),
                new WorkflowStepDefinition(new StepId("b"), "b", [], ["out_b"], [new StepId("a")], new RetryPolicy(1)),
            ]);
        var workflowPath = Path.Combine(root, "workflow.json");
        await File.WriteAllTextAsync(workflowPath, JsonSerializer.Serialize(definition), Ct);

        var bindings = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["a"] = new WorkerBindingConfigEntry(
                NoOpWorkerAdapter.AdapterName, new WorkerContract("a", [], [new ProducedOutput("out_a")], []),
                PromptTemplate: "unused-by-noop", TimeSpan.FromSeconds(30)),
            ["b"] = new WorkerBindingConfigEntry(
                NoOpWorkerAdapter.AdapterName, new WorkerContract("b", [], [new ProducedOutput("out_b")], []),
                PromptTemplate: "unused-by-noop", TimeSpan.FromSeconds(30)),
        };
        var bindingsPath = Path.Combine(root, "bindings.json");
        await File.WriteAllTextAsync(bindingsPath, JsonSerializer.Serialize(bindings), Ct);

        var room = Path.Combine(root, "queue-t2-ledgered");
        var result = await RunCommand.ExecuteAsync(
            new RunOptions(workflowPath, bindingsPath, room), NoOpAdapters, cancellationToken: Ct);

        Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
        return room;
    }

    /// <summary>
    /// A room with one step still Running under a live engine identity — the in-flight shape a lane that
    /// throws twenty minutes in leaves behind. Built from the events directly (rather than through a
    /// pump) because a pump that stops mid-step is exactly what a test cannot hold still; the same
    /// fixture shape <c>FleetProjectionWriterTests.CreateRunningRoomAsync</c> uses.
    /// </summary>
    private static async Task<string> CreateRunningRoomAsync(string root)
    {
        var room = Path.Combine(root, "queue-t3-running");
        Directory.CreateDirectory(room);

        var stepDef = new WorkflowStepDefinition(new StepId("step-a"), "architect", [], [], [], new RetryPolicy(1));
        var snapshot = SnapshotBinder.Bind(new WorkflowDefinition(new WorkflowTemplateId("wf"), 1, [stepDef]));
        await SnapshotBinder.PersistAsync(snapshot, Path.Combine(room, "snapshot.json"), Ct);

        var request = new ExecutionRequest(
            new ExecutionId("exec-running"), new WorkflowId("wf"), stepDef.StepId, stepDef.Worker,
            [], [], TimeSpan.FromMinutes(5), [], new Dictionary<StepId, ExecutionId>(), Adapter: NoOpWorkerAdapter.AdapterName);

        var self = System.Diagnostics.Process.GetCurrentProcess();
        var logWriter = new FlowEventLogWriter(Path.Combine(room, "flow.jsonl"));
        await logWriter.AppendAsync(
            new FlowEvent.ExecutionRequestAccepted(
                request,
                EnginePid: Environment.ProcessId,
                EngineStartTime: new DateTimeOffset(self.StartTime).ToUniversalTime()),
            Ct);
        await logWriter.DisposeAsync();

        return room;
    }
}
