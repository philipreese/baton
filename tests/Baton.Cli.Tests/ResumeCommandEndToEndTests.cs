using System.Diagnostics;
using System.Text.Json;
using Baton.Vendors;
using Baton.Cli.Tests.TestSupport;
using Baton.Domain;
using Baton.Status;
using Baton.Templates;

namespace Baton.Cli.Tests;

/// <summary>
/// Issue #1359's <c>baton resume</c> verb, end to end: dispatch a review lane, let it finish, resume it
/// with a follow-up message, and the room ledger shows both executions (the acceptance criterion the
/// issue itself names). Two registers, mirroring <see cref="TerminalSentinelEndToEndTests"/>'s split —
/// an in-process pass with a <see cref="ResumeObservingWorkerAdapter"/> to assert exactly what reached
/// the adapter (the message, <c>ResumeSession</c>, the recorded <c>SessionId</c>), and a real spawned
/// <c>baton</c> process (via the production <c>noop</c> adapter) to prove the completion contract —
/// truthful exit codes, <c>terminal.json</c>, <c>status --json</c> — the same way that file's own
/// process-spawn tests prove it for <c>run</c>.
/// </summary>
[Collection(WorkingDirectoryCollection.Name)]
public class ResumeCommandEndToEndTests : IDisposable
{
    private readonly IsolatedBatonHome _batonHome = new();

    public void Dispose()
    {
        _batonHome.Dispose();
        GC.SuppressFinalize(this);
    }

    // Instance, not static (#1388 review F10): xUnit gives each [Fact] its own class instance, so an
    // instance field gives each test its own ResumeObservingWorkerAdapter -- a static one accumulated
    // every test's invocations for the class's whole lifetime, which made ObservedInvocations.First()
    // below a control that could not fail (it read whichever test in the class happened to dispatch
    // first, not this test's own first invocation).
    private readonly IReadOnlyDictionary<string, IWorkerAdapter> ObservingAdapters =
        new Dictionary<string, IWorkerAdapter> { ["observer"] = new ResumeObservingWorkerAdapter() };

    [Fact]
    public async Task Resuming_a_succeeded_step_dispatches_a_linked_execution_carrying_the_message_and_ResumeSession()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-resume-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteOneStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteObservingBindingsAsync(testRoot, sessionId: "sess-abc123");

            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);
            var runResult = await RunCommand.ExecuteAsync(runOptions, ObservingAdapters, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(WorkflowStatus.Terminal, runResult.State.Status);
            var firstExecutionId = runResult.State.Steps.Single().LatestExecutionId!.Value;

            var resumeOptions = new ResumeOptions(roomDirectory, "observer", "also cover the CI workflows", null, bindingsFilePath);
            var resumeResult = await ResumeCommand.ExecuteAsync(resumeOptions, ObservingAdapters, TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, resumeResult.State.Status);
            var resumedStep = resumeResult.State.Steps.Single();
            FlowAssert.Succeeded(resumedStep);
            Assert.NotEqual(firstExecutionId, resumedStep.LatestExecutionId);

            // The ledger shows both executions -- the issue's own acceptance wording -- via the same
            // status --json shape #1356 already renders every other execution fact through.
            var logEntries = await new Baton.Store.FlowEventLogReader(Path.Combine(roomDirectory, "flow.jsonl"))
                .ReadAllEntriesWithTimestampsAsync(TestContext.Current.CancellationToken);
            var view = WorkflowStatusProjector.Project(resumeResult.State, resumeResult.Snapshot, roomDirectory, logEntries);
            var stepView = view.Steps.Single();
            Assert.Equal(resumedStep.LatestExecutionId!.Value.Value, stepView.Execution);
            Assert.Equal(firstExecutionId.Value, stepView.LinkedFrom);

            // #1360 F2: the resumed execution and the one it linked from each carry their OWN usage --
            // two distinct entries, not one merged/overwritten figure. Discriminating, not tautological
            // (the review's finding): the resume stub sleeps so the two wall-clock figures are actually
            // distinguishable, and both step-view fields are checked against an independent oracle
            // (ExecutionUsageProjector's own map, keyed by the two known execution ids) rather than
            // only asserted non-negative -- inverting WorkflowStatusProjector's field mapping (using
            // LatestExecutionId for both Usage and LinkedFromUsage) fails this.
            var artifactsRootPath = Path.Combine(roomDirectory, Baton.Artifacts.ArtifactManager.ArtifactsDirectoryName);
            var usageByExecutionId = ExecutionUsageProjector.BuildByExecutionId(
                logEntries, artifactsRootPath, WorkerAdapterRegistry.Default, roomDirectory);

            Assert.NotNull(stepView.Usage);
            Assert.NotNull(stepView.LinkedFromUsage);
            Assert.NotEqual(stepView.LinkedFromUsage!.WallClockMs, stepView.Usage!.WallClockMs);
            Assert.Equal(usageByExecutionId[resumedStep.LatestExecutionId!.Value.Value].WallClockMs, stepView.Usage.WallClockMs);
            Assert.Equal(usageByExecutionId[firstExecutionId.Value].WallClockMs, stepView.LinkedFromUsage.WallClockMs);

            var adapter = (ResumeObservingWorkerAdapter)ObservingAdapters["observer"];
            var resumedInvocation = adapter.ObservedInvocations.Last();
            Assert.True(resumedInvocation.ResumeSession);
            Assert.Equal("sess-abc123", resumedInvocation.SessionId);
            Assert.Equal("also cover the CI workflows", resumedInvocation.PromptTemplate);

            // And the first invocation was NOT a resume -- confirms this isn't just always-on.
            var firstInvocation = adapter.ObservedInvocations.First();
            Assert.False(firstInvocation.ResumeSession);

            // #1388 review, question 3: everything BESIDES the message and ResumeSession must be
            // byte-for-byte identical to the original dispatch -- the "implement lane silently
            // narrowed to review defaults" failure this guards has no other test.
            Assert.Equal(
                firstInvocation with { PromptTemplate = "also cover the CI workflows", ResumeSession = true },
                resumedInvocation);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task A_resumed_reviews_verdict_has_its_model_written_instruments_stripped()
    {
        // #1911 low 1: `baton resume` puts a fresh worker turn into a bound room, and a resumed review
        // writes a verdict like any other -- one nothing stamped, so a fabricated `instruments` rode
        // into `--notify` payloads unchallenged. This verb runs no verify step, so removal is the whole
        // arm (VerdictInstrumentStamp's doc has the scope removal is allowed to cover).
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-resume-verdict-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var fixturePath = await ModelWrittenVerdictFixture.WriteAsync(
                Path.Combine(testRoot, "worker-verdict.json"), TestContext.Current.CancellationToken);
            var adapters = new Dictionary<string, IWorkerAdapter>(StringComparer.Ordinal)
            {
                ["fake"] = new ContractOutputWorkerAdapter(
                    satisfyOutputs: true,
                    outputFixtures: new Dictionary<string, string>(StringComparer.Ordinal) { ["verdict.json"] = fixturePath }),
            };

            var workflowFilePath = await WriteVerdictProducingWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteVerdictProducingBindingsAsync(testRoot);

            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);
            var runResult = await RunCommand.ExecuteAsync(runOptions, adapters, cancellationToken: TestContext.Current.CancellationToken);
            var firstExecutionId = runResult.State.Steps.Single().LatestExecutionId!.Value;

            var resumeResult = await ResumeCommand.ExecuteAsync(
                new ResumeOptions(roomDirectory, "reviewer", "another pass", null, bindingsFilePath),
                adapters, TestContext.Current.CancellationToken);

            var resumedExecutionId = resumeResult.State.Steps.Single().LatestExecutionId!.Value;
            Assert.NotEqual(firstExecutionId, resumedExecutionId);

            var resumed = ReadVerdict(roomDirectory, resumedExecutionId);
            Assert.False(resumed.TryGetProperty("instruments", out _));
            // The rest of the worker's verdict is untouched -- a field is removed, the review is not
            // rewritten -- which doubles as the control that the file was read and rewritten at all.
            Assert.Equal("all good", resumed.GetProperty("summary").GetString());

            // Discriminating control: `baton run` stamps nothing, so the execution the resume linked
            // FROM still carries the model's array. Without this, "no instruments" could be a fake
            // worker that never wrote one. It is also the pin behind resume's unscoped walk: the
            // earlier execution is no longer the step's latest, so the walk provably never visits it.
            Assert.True(ReadVerdict(roomDirectory, firstExecutionId).TryGetProperty("instruments", out _));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    private static JsonElement ReadVerdict(string roomDirectory, ExecutionId executionId) =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
            roomDirectory, "artifacts", $"execution_{executionId}", "verdict.json"))).RootElement;

    private static async Task<string> WriteVerdictProducingWorkflowAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("one-step-review"), 1,
            [new WorkflowStepDefinition(new StepId("solo"), "reviewer", [], ["verdict.json"], [], new RetryPolicy(1))]);

        var path = Path.Combine(directory, "workflow.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(definition));
        return path;
    }

    private static async Task<string> WriteVerdictProducingBindingsAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["reviewer"] = new WorkerBindingConfigEntry(
                "fake", new WorkerContract("reviewer", [], [new ProducedOutput("verdict.json")], []),
                "review the branch", TimeSpan.FromSeconds(30), SessionId: "sess-verdict"),
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }

    [Fact]
    public async Task Resuming_from_a_message_file_reads_its_full_contents_as_the_message()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-resume-msgfile-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteOneStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteObservingBindingsAsync(testRoot, sessionId: "sess-xyz");
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);
            await RunCommand.ExecuteAsync(runOptions, ObservingAdapters, cancellationToken: TestContext.Current.CancellationToken);

            var messageFilePath = Path.Combine(testRoot, "message.txt");
            await File.WriteAllTextAsync(messageFilePath, "a longer follow-up message", TestContext.Current.CancellationToken);

            var resumeOptions = new ResumeOptions(roomDirectory, "observer", null, messageFilePath, bindingsFilePath);
            await ResumeCommand.ExecuteAsync(resumeOptions, ObservingAdapters, TestContext.Current.CancellationToken);

            var adapter = (ResumeObservingWorkerAdapter)ObservingAdapters["observer"];
            Assert.Equal("a longer follow-up message", adapter.ObservedInvocations.Last().PromptTemplate);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Resuming_a_missing_message_file_throws_a_CliArgumentException_with_a_Try_line()
    {
        // #1388 review F10: the message-file-missing refusal (ResumeCommand.cs:74-77) had no test.
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-resume-nomsgfile-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteOneStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteObservingBindingsAsync(testRoot, sessionId: "sess-unused");
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);
            await RunCommand.ExecuteAsync(runOptions, ObservingAdapters, cancellationToken: TestContext.Current.CancellationToken);

            var missingMessageFilePath = Path.Combine(testRoot, "does-not-exist.txt");
            var resumeOptions = new ResumeOptions(roomDirectory, "observer", null, missingMessageFilePath, bindingsFilePath);
            var thrown = await Assert.ThrowsAsync<CliArgumentException>(
                () => ResumeCommand.ExecuteAsync(resumeOptions, ObservingAdapters, TestContext.Current.CancellationToken));

            Assert.Contains(missingMessageFilePath, thrown.Message, StringComparison.Ordinal);
            Assert.NotNull(thrown.TryInvocation);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Resuming_a_worker_missing_from_the_bindings_file_throws_a_CliArgumentException_with_a_Try_line()
    {
        // #1388 review F10: the missing-bindings-entry refusal (ResumeCommand.cs:100-105) had no test.
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-resume-nobindingsentry-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteOneStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteObservingBindingsAsync(testRoot, sessionId: "sess-unused");
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);
            await RunCommand.ExecuteAsync(runOptions, ObservingAdapters, cancellationToken: TestContext.Current.CancellationToken);

            var resumeOptions = new ResumeOptions(roomDirectory, "no-such-worker", "continue please", null, bindingsFilePath);
            var thrown = await Assert.ThrowsAsync<CliArgumentException>(
                () => ResumeCommand.ExecuteAsync(resumeOptions, ObservingAdapters, TestContext.Current.CancellationToken));

            Assert.Contains("no-such-worker", thrown.Message, StringComparison.Ordinal);
            Assert.NotNull(thrown.TryInvocation);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Resuming_a_room_with_no_bound_snapshot_throws_a_SnapshotLoadException_with_a_Try_line()
    {
        // #1388 review F10: the pre-ledger SnapshotLoadException refusal (ResumeCommand.cs:86-91) had
        // no test -- a room directory baton run never touched at all.
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-resume-nosnapshot-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var bindingsFilePath = await WriteObservingBindingsAsync(testRoot, sessionId: "sess-unused");

            var resumeOptions = new ResumeOptions(roomDirectory, "observer", "continue please", null, bindingsFilePath);
            var thrown = await Assert.ThrowsAsync<SnapshotLoadException>(
                () => ResumeCommand.ExecuteAsync(resumeOptions, ObservingAdapters, TestContext.Current.CancellationToken));

            Assert.NotNull(thrown.TryInvocation);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Resuming_a_worker_with_no_recorded_SessionId_refuses_loudly_with_a_Try_line()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-resume-nosession-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteOneStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteObservingBindingsAsync(testRoot, sessionId: null);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);
            await RunCommand.ExecuteAsync(runOptions, ObservingAdapters, cancellationToken: TestContext.Current.CancellationToken);

            var resumeOptions = new ResumeOptions(roomDirectory, "observer", "continue please", null, bindingsFilePath);
            var thrown = await Assert.ThrowsAsync<WorkerCannotResumeException>(
                () => ResumeCommand.ExecuteAsync(resumeOptions, ObservingAdapters, TestContext.Current.CancellationToken));

            Assert.Contains("SessionId", thrown.Message, StringComparison.Ordinal);
            Assert.NotNull(thrown.TryInvocation);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task The_real_CLI_process_exits_0_for_a_successful_resume_and_the_sentinel_shows_the_linked_execution()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-resume-proc-ok-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteOneStepWorkflowAsync(testRoot, worker: "solo");
            var bindingsFilePath = await WriteNoOpBindingsAsync(testRoot, sessionId: "sess-real-proc");

            using (var runProcess = StartBatonProcess(
                "run", workflowFilePath, "--bindings", bindingsFilePath, "--room-dir", roomDirectory))
            {
                await BoundedProcessWait.RunToExitAsync(
                    runProcess, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
                Assert.Equal(0, runProcess.ExitCode);
            }

            var sentinelPath = Path.Combine(roomDirectory, "terminal.json");
            var firstView = JsonSerializer.Deserialize<WorkflowStatusView>(
                await File.ReadAllTextAsync(sentinelPath, TestContext.Current.CancellationToken));
            var firstExecutionId = firstView!.Steps.Single().Execution;

            using var resumeProcess = StartBatonProcess(
                "resume", roomDirectory, "--worker", "solo", "--message", "continue",
                "--bindings", bindingsFilePath);
            await BoundedProcessWait.RunToExitAsync(
                resumeProcess, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.Equal(0, resumeProcess.ExitCode);

            var secondView = JsonSerializer.Deserialize<WorkflowStatusView>(
                await File.ReadAllTextAsync(sentinelPath, TestContext.Current.CancellationToken));
            Assert.Equal("Succeeded", secondView!.State);
            var stepView = secondView.Steps.Single();
            Assert.NotEqual(firstExecutionId, stepView.Execution);
            Assert.Equal(firstExecutionId, stepView.LinkedFrom);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task The_real_CLI_process_exits_ValidationRefused_when_no_SessionId_is_recorded()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-resume-proc-nosession-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteOneStepWorkflowAsync(testRoot, worker: "solo");
            var bindingsFilePath = await WriteNoOpBindingsAsync(testRoot, sessionId: null);

            using (var runProcess = StartBatonProcess(
                "run", workflowFilePath, "--bindings", bindingsFilePath, "--room-dir", roomDirectory))
            {
                await BoundedProcessWait.RunToExitAsync(
                    runProcess, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
                Assert.Equal(0, runProcess.ExitCode);
            }

            using var resumeProcess = StartBatonProcess(
                "resume", roomDirectory, "--worker", "solo", "--message", "continue",
                "--bindings", bindingsFilePath);
            var (_, stderr) = await BoundedProcessWait.RunToExitAsync(
                resumeProcess, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.Equal((int)RunExitCode.ValidationRefused, resumeProcess.ExitCode);
            Assert.Contains("SessionId", stderr, StringComparison.Ordinal);
            Assert.Contains("Try:", stderr, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    private Process StartBatonProcess(params string[] args)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        // #1645: hand the child the same storage root -- IsolatedBatonHome's scope stops at this
        // process boundary, and the `resume` arm below is a real subprocess.
        startInfo.Environment[BatonPaths.HomeEnvironmentVariable] = _batonHome.Path;
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(typeof(RunCommand).Assembly.Location);
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        return Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start 'baton'.");
    }

    private static async Task<string> WriteOneStepWorkflowAsync(string directory, string worker = "observer")
    {
        Directory.CreateDirectory(directory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("one-step-resume"), 1,
            [new WorkflowStepDefinition(new StepId("solo"), worker, [], ["plan"], [], new RetryPolicy(1))]);

        var path = Path.Combine(directory, "workflow.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(definition));
        return path;
    }

    private static async Task<string> WriteObservingBindingsAsync(string directory, string? sessionId)
    {
        Directory.CreateDirectory(directory);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["observer"] = new WorkerBindingConfigEntry(
                "observer", new WorkerContract("observer", [], [new ProducedOutput("plan")], []),
                "the original task", TimeSpan.FromSeconds(30), SessionId: sessionId),
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }

    private static async Task<string> WriteNoOpBindingsAsync(string directory, string? sessionId)
    {
        Directory.CreateDirectory(directory);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["solo"] = new WorkerBindingConfigEntry(
                NoOpWorkerAdapter.AdapterName, new WorkerContract("solo", [], [new ProducedOutput("plan")], []),
                PromptTemplate: "unused-by-noop", TimeSpan.FromSeconds(30), SessionId: sessionId),
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }
}
