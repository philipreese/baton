using System.Text.Json;
using Baton.Vendors;
using Baton.Cli.Tests.TestSupport;
using Baton.Domain;
using Baton.Store;
using Baton.Templates;

namespace Baton.Cli.Tests;

/// <summary>
/// <c>baton supply</c> (M12 Phase 3, issue #97) exercised on its own: minting, populating, and
/// settling a step-less supplementary execution in one call, ahead of
/// <see cref="DecideCommandEndToEndTests"/>'s full supply → decide round trips.
/// </summary>
public class SupplyCommandEndToEndTests
{
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter> { ["shell"] = new ShellCommandWorkerAdapter() };

    [Fact]
    public async Task Supplying_an_artifact_mints_populates_and_settles_it_in_one_call()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-supply-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteSingleStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteSingleStepBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);
            await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            var sourceFilePath = Path.Combine(testRoot, "revision.txt");
            await File.WriteAllTextAsync(sourceFilePath, "the-revision", TestContext.Current.CancellationToken);
            var supplyOptions = new SupplyOptions(roomDirectory, "human", "revision", sourceFilePath, bindingsFilePath);

            var result = await SupplyCommand.ExecuteAsync(supplyOptions, Adapters, TestContext.Current.CancellationToken);

            Assert.Empty(result.Command.State.StepLessExecutions);
            var reader = new FlowEventLogReader(Path.Combine(roomDirectory, "flow.jsonl"));
            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Contains(events, e => e is FlowEvent.ExecutionSucceeded succeeded && succeeded.ExecutionId == result.ExecutionId);

            var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
            var outputPath = Path.Combine(artifactsRoot, $"execution_{result.ExecutionId}", "revision");
            Assert.Equal("the-revision", (await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken)).Trim());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Supplying_against_a_task_whose_journal_is_held_open_by_another_process_throws_FlowJournalHeldException_not_a_raw_IOException()
    {
        // #816's population: SupplyCommand shares the same FlowEventLogWriter construction as
        // decide/cancel, so it must surface the typed refusal too.
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-supply-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteSingleStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteSingleStepBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);
            await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            var sourceFilePath = Path.Combine(testRoot, "revision.txt");
            await File.WriteAllTextAsync(sourceFilePath, "the-revision", TestContext.Current.CancellationToken);

            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            using var liveEngineHolder = new FileStream(
                logPath, FileMode.Append, FileAccess.Write, FileShare.Read, bufferSize: 1, useAsync: true);

            var supplyOptions = new SupplyOptions(roomDirectory, "human", "revision", sourceFilePath, bindingsFilePath);

            await Assert.ThrowsAsync<FlowJournalHeldException>(
                () => SupplyCommand.ExecuteAsync(supplyOptions, Adapters, TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Supplying_against_a_bindings_file_that_also_names_an_unresolvable_worker_still_succeeds()
    {
        // #662, supply's half of the same defect CancelCommandEndToEndTests covers: a bindings file
        // naming a worker "human" never dispatches (here, an entry whose contract and grant make it
        // permanently unresolvable) must not block supplying an artifact for a different worker.
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-supply-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteSingleStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteSingleStepBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);
            await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            var sourceFilePath = Path.Combine(testRoot, "revision.txt");
            await File.WriteAllTextAsync(sourceFilePath, "the-revision", TestContext.Current.CancellationToken);
            var unresolvableBindingsFilePath = await WriteSingleStepBindingsWithAnUnresolvableEntryAsync(testRoot);
            var supplyOptions = new SupplyOptions(roomDirectory, "human", "revision", sourceFilePath, unresolvableBindingsFilePath);

            var adapters = new Dictionary<string, IWorkerAdapter>
            {
                ["shell"] = new ShellCommandWorkerAdapter(),
                ["unsatisfiable"] = new UnsatisfiableContractWorkerAdapter(),
            };

            var result = await SupplyCommand.ExecuteAsync(supplyOptions, adapters, TestContext.Current.CancellationToken);

            var reader = new FlowEventLogReader(Path.Combine(roomDirectory, "flow.jsonl"));
            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Contains(events, e => e is FlowEvent.ExecutionSucceeded succeeded && succeeded.ExecutionId == result.ExecutionId);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task A_supplied_verdict_has_its_model_written_instruments_stripped()
    {
        // #1911 low 1, supply's half: a verdict handed in through this verb lands on disk exactly like
        // a dispatched review's, and `baton watch` reads it the same way -- so an `instruments` array
        // in it must be the engine's record or absent. No verify step can have run for a file the
        // operator supplied, so absent is the only honest answer.
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-supply-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteSingleStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteSingleStepBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);
            await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            var sourceFilePath = await ModelWrittenVerdictFixture.WriteAsync(
                Path.Combine(testRoot, "supplied-verdict.json"), TestContext.Current.CancellationToken);
            var supplyOptions = new SupplyOptions(roomDirectory, "human", "verdict.json", sourceFilePath, bindingsFilePath);

            var result = await SupplyCommand.ExecuteAsync(supplyOptions, Adapters, TestContext.Current.CancellationToken);

            // The supplementary execution hangs off no step, so this also pins that the stamp reaches
            // an execution `State.Steps` never names.
            var suppliedPath = Path.Combine(roomDirectory, "artifacts", $"execution_{result.ExecutionId}", "verdict.json");
            var supplied = JsonDocument.Parse(
                await File.ReadAllBytesAsync(suppliedPath, TestContext.Current.CancellationToken)).RootElement;
            Assert.False(supplied.TryGetProperty("instruments", out _));
            Assert.Equal("all good", supplied.GetProperty("summary").GetString());

            // Control, in two directions: the source the operator handed in is not rewritten in place,
            // and it demonstrably carried the field -- so "absent" is a removal, not an empty fixture.
            var source = JsonDocument.Parse(
                await File.ReadAllBytesAsync(sourceFilePath, TestContext.Current.CancellationToken)).RootElement;
            Assert.True(source.TryGetProperty("instruments", out _));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task A_missing_source_file_throws_before_minting_anything()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-supply-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteSingleStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteSingleStepBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);
            await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            var missingSourcePath = Path.Combine(testRoot, "does-not-exist.txt");
            var supplyOptions = new SupplyOptions(roomDirectory, "human", "revision", missingSourcePath, bindingsFilePath);

            // Typed CliArgumentException, not a raw FileNotFoundException: the latter is not an
            // BatonFlowException and would escape Program's typed boundary as a crash rather than a clean
            // CLI failure — the missing-file class fixed alongside the file loaders.
            await Assert.ThrowsAsync<CliArgumentException>(() => SupplyCommand.ExecuteAsync(supplyOptions, Adapters, TestContext.Current.CancellationToken));

            var reader = new FlowEventLogReader(Path.Combine(roomDirectory, "flow.jsonl"));
            Assert.DoesNotContain(await reader.ReadAllAsync(TestContext.Current.CancellationToken), e => e is FlowEvent.ExecutionRequestAccepted accepted && accepted.Request.StepId is null);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Supplying_against_a_room_directory_with_no_snapshot_throws_a_typed_error()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-supply-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            Directory.CreateDirectory(testRoot);
            var bindingsFilePath = await WriteSingleStepBindingsAsync(testRoot);
            var sourceFilePath = Path.Combine(testRoot, "revision.txt");
            await File.WriteAllTextAsync(sourceFilePath, "the-revision", TestContext.Current.CancellationToken);
            var supplyOptions = new SupplyOptions(roomDirectory, "human", "revision", sourceFilePath, bindingsFilePath);

            await Assert.ThrowsAsync<SnapshotLoadException>(() => SupplyCommand.ExecuteAsync(supplyOptions, Adapters, TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    private static async Task<string> WriteSingleStepWorkflowAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("single-step"),
            1,
            [new WorkflowStepDefinition(new StepId("a"), "a", [], ["out_a"], [], new RetryPolicy(1))]);

        var path = Path.Combine(directory, "workflow.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(definition));
        return path;
    }

    private static async Task<string> WriteSingleStepBindingsAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["a"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("a", [], [new ProducedOutput("out_a")], []),
                WriteFileCommand("out_a", "a-out"), TimeSpan.FromSeconds(30)),
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }

    private static async Task<string> WriteSingleStepBindingsWithAnUnresolvableEntryAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["a"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("a", [], [new ProducedOutput("out_a")], []),
                WriteFileCommand("out_a", "a-out"), TimeSpan.FromSeconds(30)),
            ["reviewer"] = new WorkerBindingConfigEntry(
                "unsatisfiable",
                new WorkerContract("reviewer", [], [new ProducedOutput("review.md")], []),
                "irrelevant — never dispatched",
                TimeSpan.FromSeconds(30),
                PermissionGrant: new PermissionGrant(ReadFiles: true, WriteFiles: false)),
        };

        var path = Path.Combine(directory, "bindings-with-unresolvable-entry.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }

    private static string WriteFileCommand(string outputName, string content) =>
        $"echo {content}>%BATON_OUTPUT_DIR%\\{outputName}";
}
