using System.Text.Json;
using Baton.Vendors;
using Baton.Cli.Tests.TestSupport;
using Baton.Domain;
using Baton.Mutation;
using Baton.Status;
using Baton.Store;
using Baton.Templates;

namespace Baton.Cli.Tests;

[Collection(ConsoleOutCaptureCollection.Name)]
public class CancelCommandEndToEndTests
{
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter> { ["shell"] = new ShellCommandWorkerAdapter() };

    /// <summary>
    /// #816's population, as #2073 handles it: the lock is FREE (so no pump) but <c>flow.jsonl</c> is
    /// held open by another process — a killed pump whose handle the OS has not torn down, or a
    /// sibling command mid-append. The intent fact lands (room.jsonl is a different file), the kill
    /// path runs (nothing to kill here), and the terminal fact cannot be written — reported as queued,
    /// never as a raw <see cref="FlowJournalHeldException"/> escaping after the kill.
    /// </summary>
    [Fact]
    public async Task Cancelling_a_room_whose_journal_is_held_open_by_another_process_reports_queued_not_a_raw_IOException()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var executionId = await WriteOpenSingleStepRoomAsync(roomDirectory);
            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            using var liveEngineHolder = new FileStream(
                logPath, FileMode.Append, FileAccess.Write, FileShare.Read, bufferSize: 1, useAsync: true);

            var cancelOptions = new CancelOptions(roomDirectory, executionId.Value, BindingsFilePath: "ignored");

            var originalOut = Console.Out;
            var capturedOut = new StringWriter();
            Console.SetOut(capturedOut);
            CommandResult result;
            try
            {
                result = await CancelCommand.ExecuteAsync(
                    cancelOptions, Adapters, TestContext.Current.CancellationToken, pumpAnswerWindow: TimeSpan.FromMilliseconds(200));
            }
            finally
            {
                Console.SetOut(originalOut);
            }

            Assert.Contains("held open", capturedOut.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(WorkflowStatus.Running, result.State.Status);
            Assert.True(result.CancellationQueued);
            Assert.Equal(MutationExitCodeResolver.Failure, MutationExitCodeResolver.Resolve(result));
            Assert.False(File.Exists(CancelRequestFile.GetPath(roomDirectory)), "a free lock means no pump, so no cancel.request");

            var roomEvents = await new RoomEventLogReader(Path.Combine(roomDirectory, BatonPaths.RoomLogFileName)).ReadAllRoomEventsAsync(TestContext.Current.CancellationToken);
            Assert.Single(roomEvents.OfType<RoomEvent.ArrestIntentRecorded>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// #2073 inverted the pre-existing "a malformed bindings file throws" case on purpose: the verb no
    /// longer dispatches, so it no longer reads bindings at all — a malformed, missing, or unresolvable
    /// bindings file must not refuse a cancel. The room here is terminal, so the no-op arm is what
    /// proves the file was never opened (a parse would have thrown first).
    /// </summary>
    [Fact]
    public async Task Bindings_are_never_read_so_a_malformed_or_missing_bindings_path_does_not_refuse()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteThreeStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteThreeStepBindingsAsync(testRoot);
            var finalState = (await RunCommand.ExecuteAsync(new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory), Adapters, cancellationToken: TestContext.Current.CancellationToken)).State;
            Assert.Equal(WorkflowStatus.Terminal, finalState.Status);
            var architectExecutionId = finalState.Steps.First(s => s.StepId.Value == "architect").LatestExecutionId;
            Assert.NotNull(architectExecutionId);

            var malformedBindingsPath = Path.Combine(testRoot, "malformed.json");
            await File.WriteAllTextAsync(malformedBindingsPath, "{ not valid json", TestContext.Current.CancellationToken);
            var defaultBindingsPath = BatonPaths.RoomBindingsFile(roomDirectory);
            Assert.False(File.Exists(defaultBindingsPath), "bare 'baton run' must not have copied bindings.json into the room");

            foreach (var bindingsPath in new[] { malformedBindingsPath, defaultBindingsPath })
            {
                var result = await CancelCommand.ExecuteAsync(
                    new CancelOptions(roomDirectory, architectExecutionId.Value.Value, bindingsPath), Adapters, TestContext.Current.CancellationToken);
                Assert.True(result.CancelWasNoOp);
            }
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Cancelling_an_already_succeeded_execution_is_a_no_op_that_writes_nothing_and_exits_0()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteThreeStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteThreeStepBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);

            var finalState = (await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken)).State;
            Assert.Equal(WorkflowStatus.Terminal, finalState.Status);

            var architectExecutionId = finalState.Steps.First(s => s.StepId.Value == "architect").LatestExecutionId;
            Assert.NotNull(architectExecutionId);

            var cancelOptions = new CancelOptions(roomDirectory, architectExecutionId.Value.Value, bindingsFilePath);
            var result = await CancelCommand.ExecuteAsync(cancelOptions, Adapters, TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
            Assert.All(result.State.Steps, FlowAssert.Succeeded);
            Assert.True(result.CancelWasNoOp);
            Assert.Equal(MutationExitCodeResolver.Success, MutationExitCodeResolver.Resolve(result));

            // Before #2073 this appended a too-late CancellationRequested; a no-op now writes nothing.
            var events = await new FlowEventLogReader(Path.Combine(roomDirectory, "flow.jsonl")).ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain(events, e => e is FlowEvent.CancellationRequested);
            Assert.False(File.Exists(Path.Combine(roomDirectory, BatonPaths.RoomLogFileName)));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Cancelling_against_a_room_directory_with_no_snapshot_throws_a_typed_error()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            Directory.CreateDirectory(testRoot);
            var bindingsFilePath = await WriteThreeStepBindingsAsync(testRoot);
            var cancelOptions = new CancelOptions(roomDirectory, "exec-1", bindingsFilePath);

            await Assert.ThrowsAsync<SnapshotLoadException>(
                () => CancelCommand.ExecuteAsync(cancelOptions, Adapters, TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>An unknown id is refused BEFORE the idempotent arm (the ordering <c>CancelCommand.ExecuteAsync</c> comments on): a mistyped id must never turn into a quiet exit 0.</summary>
    [Fact]
    public async Task Cancelling_an_unknown_execution_id_throws_a_typed_error()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteThreeStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteThreeStepBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);

            await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            var cancelOptions = new CancelOptions(roomDirectory, "not-a-real-execution-id", bindingsFilePath);
            await Assert.ThrowsAsync<UnknownExecutionIdException>(
                () => CancelCommand.ExecuteAsync(cancelOptions, Adapters, TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>One accepted, never-settled execution with no worker pid recorded — Running, nothing to kill.</summary>
    private static async Task<ExecutionId> WriteOpenSingleStepRoomAsync(string roomDirectory)
    {
        Directory.CreateDirectory(roomDirectory);
        var step = new StepId("a");
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("one-open-step-held-journal"), 1,
            [new WorkflowStepDefinition(step, "a", [], ["out"], [], new RetryPolicy(1))]);
        var snapshot = SnapshotBinder.Bind(definition);
        await SnapshotBinder.PersistAsync(snapshot, Path.Combine(roomDirectory, BatonPaths.SnapshotFileName), TestContext.Current.CancellationToken);

        var executionId = new ExecutionId("exec-open-held-1");
        await using var writer = new FlowEventLogWriter(Path.Combine(roomDirectory, BatonPaths.FlowLogFileName));
        await writer.AppendAsync(
            new FlowEvent.ExecutionRequestAccepted(new ExecutionRequest(
                executionId, new WorkflowId("wf-held"), step, "a",
                Inputs: [], Outputs: [], Timeout: TimeSpan.FromMinutes(5), Environment: [],
                UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>())),
            TestContext.Current.CancellationToken);
        return executionId;
    }

    private static async Task<string> WriteThreeStepWorkflowAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("three-step-linear"),
            1,
            [
                new WorkflowStepDefinition(new StepId("architect"), "architect", [], ["plan"], [], new RetryPolicy(1)),
                new WorkflowStepDefinition(new StepId("critic"), "critic", ["plan"], ["review"], [new StepId("architect")], new RetryPolicy(1)),
                new WorkflowStepDefinition(new StepId("publisher"), "publisher", ["review"], ["summary"], [new StepId("critic")], new RetryPolicy(1)),
            ]);

        var path = Path.Combine(directory, "workflow.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(definition));
        return path;
    }

    private static async Task<string> WriteThreeStepBindingsAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry(
                "shell",
                new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                WriteFileCommand("plan", "the-plan"),
                TimeSpan.FromSeconds(30)),
            ["critic"] = new WorkerBindingConfigEntry(
                "shell",
                new WorkerContract("critic", ["plan"], [new ProducedOutput("review")], []),
                CopyFirstInputCommand("review"),
                TimeSpan.FromSeconds(30)),
            ["publisher"] = new WorkerBindingConfigEntry(
                "shell",
                new WorkerContract("publisher", ["review"], [new ProducedOutput("summary")], []),
                CopyFirstInputCommand("summary"),
                TimeSpan.FromSeconds(30)),
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }

    private static string WriteFileCommand(string outputName, string content) =>
        $"echo {content}>%BATON_OUTPUT_DIR%\\{outputName}";

    private static string CopyFirstInputCommand(string outputName) =>
        $"type %BATON_INPUT_0% >%BATON_OUTPUT_DIR%\\{outputName}";
}
