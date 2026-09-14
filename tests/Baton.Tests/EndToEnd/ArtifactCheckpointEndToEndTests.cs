using System.Diagnostics;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Mutation;
using Baton.Store;
using Baton.Tests.Shared;
using Baton.Vendors;

namespace Baton.Tests.EndToEnd;

/// <summary>Exercises #2276's one-shot, artifact-only dispatch through the live mutation seam.</summary>
public sealed class ArtifactCheckpointEndToEndTests
{
    private const string ArrestingUsage =
        """{"type":"turn.usage","usage":{"input_tokens":500,"cached_input_tokens":0,"output_tokens":1,"round_trip":1}}""";

    [Fact]
    public async Task A_cap_arrest_with_valid_artifacts_from_a_mutating_delivering_role_continues_through_workspace_safety()
    {
        var room = Path.Combine(Path.GetTempPath(), "baton-artifact-boundary-mutating-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(room, "workspace");
        var artifacts = Path.Combine(room, "artifacts");
        var log = Path.Combine(room, "flow.jsonl");
        Directory.CreateDirectory(workspace);
        try
        {
            InitializeGitRepository(workspace);
            var state = await RunBoundaryExecutionAsync(
                room, workspace, artifacts, log,
                [new ProducedOutput("changes.md", Schema: OutputSchema.NonEmptyText)],
                artifactDirectory =>
                {
                    File.WriteAllText(Path.Combine(artifactDirectory, "changes.md"), "Work is unfinished.");
                    File.WriteAllText(Path.Combine(workspace, "unfinished.txt"), "dirty");
                },
                role: "implement", changesTree: true, verifiesWorkspace: true, deliversBranch: true);

            var events = await new FlowEventLogReader(log).ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Equal(StepStatus.Failed, Assert.Single(state.Steps).Status);
            Assert.Single(events.OfType<FlowEvent.ExecutionArrested>());
            Assert.Empty(events.OfType<FlowEvent.ExecutionSucceeded>());
            Assert.Single(events.OfType<FlowEvent.GraceTurnAttempted>());
            Assert.Empty(events.OfType<FlowEvent.ArtifactCheckpointAttempted>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
        }
    }

    [Fact]
    public async Task A_cap_arrest_with_valid_quiesced_artifacts_preserves_the_completed_account_without_a_checkpoint()
    {
        var room = Path.Combine(Path.GetTempPath(), "baton-artifact-boundary-valid-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(room, "workspace");
        var artifacts = Path.Combine(room, "artifacts");
        var log = Path.Combine(room, "flow.jsonl");
        Directory.CreateDirectory(workspace);
        try
        {
            var state = await RunBoundaryExecutionAsync(
                room, workspace, artifacts, log,
                [new ProducedOutput("report.md", Schema: OutputSchema.NonEmptyText),
                 new ProducedOutput("verdict.json", Schema: OutputSchema.NonEmptyText)],
                artifactDirectory =>
                {
                    File.WriteAllText(Path.Combine(artifactDirectory, "report.md"), "Review complete.");
                    File.WriteAllText(Path.Combine(artifactDirectory, "verdict.json"), "{\"decision\":\"APPROVE\"}");
                });

            var events = await new FlowEventLogReader(log).ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Equal(StepStatus.Succeeded, Assert.Single(state.Steps).Status);
            Assert.Single(events.OfType<FlowEvent.ExecutionArrested>());
            Assert.Single(events.OfType<FlowEvent.ExecutionSucceeded>());
            Assert.Empty(events.OfType<FlowEvent.ArtifactCheckpointAttempted>());
            Assert.Empty(events.OfType<FlowEvent.ArtifactCheckpointCompleted>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
        }
    }

    [Fact]
    public async Task A_cap_arrest_with_hollow_boundary_artifacts_remains_indeterminate()
    {
        var room = Path.Combine(Path.GetTempPath(), "baton-artifact-boundary-hollow-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(room, "workspace");
        var artifacts = Path.Combine(room, "artifacts");
        var log = Path.Combine(room, "flow.jsonl");
        Directory.CreateDirectory(workspace);
        try
        {
            var state = await RunBoundaryExecutionAsync(
                room, workspace, artifacts, log,
                [new ProducedOutput("report.md", Schema: OutputSchema.NonEmptyText),
                 new ProducedOutput("verdict.json", Schema: OutputSchema.NonEmptyText)],
                artifactDirectory =>
                {
                    File.WriteAllText(Path.Combine(artifactDirectory, "report.md"), "   ");
                    File.WriteAllText(Path.Combine(artifactDirectory, "verdict.json"), "{\"decision\":\"APPROVE\"}");
                });

            var events = await new FlowEventLogReader(log).ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Equal(StepStatus.Failed, Assert.Single(state.Steps).Status);
            Assert.Single(events.OfType<FlowEvent.ExecutionArrested>());
            Assert.Empty(events.OfType<FlowEvent.ExecutionSucceeded>());
            Assert.Empty(events.OfType<FlowEvent.ArtifactCheckpointAttempted>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
        }
    }

    [Fact]
    public async Task A_codex_cap_arrest_gets_one_artifact_only_checkpoint_before_the_indeterminate_arrest()
    {
        var room = Path.Combine(Path.GetTempPath(), "baton-artifact-checkpoint-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(room, "workspace");
        var artifacts = Path.Combine(room, "artifacts");
        var log = Path.Combine(room, "flow.jsonl");
        Directory.CreateDirectory(workspace);
        try
        {
            var stepId = new StepId("review");
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("artifact-checkpoint"), new WorkflowTemplateId("review"), 1,
                [new WorkflowStepDefinition(stepId, "review", [], ["report.md", "verdict.json"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]);
            var target = CheckpointTarget(workspace);
            var binding = new WorkerBinding.Process(
                new WorkerContract("review", [], [new ProducedOutput("report.md"), new ProducedOutput("verdict.json")], []),
                target, TimeSpan.FromSeconds(30), Adapter: "codex", TokenBudget: 100, VerifiesWorkspace: false);
            var dispatcher = new CheckpointDispatcher(artifacts);
            await using var writer = new FlowEventLogWriter(log);
            var reader = new FlowEventLogReader(log);

            var state = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("artifact-checkpoint"), room, snapshot,
                new Dictionary<string, WorkerBinding> { ["review"] = binding }, artifacts, reader, writer, dispatcher,
                cancellationToken: TestContext.Current.CancellationToken);
            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);

            Assert.Equal(StepStatus.Failed, Assert.Single(state.Steps).Status);
            Assert.Equal(2, dispatcher.CallCount);
            Assert.Equal(["report.md", "verdict.json"], dispatcher.CheckpointTarget!.ArtifactOnlyOutputNames);
            Assert.Equal(ArtifactCheckpoint.PromptText, dispatcher.CheckpointTarget.PromptText);
            Assert.NotNull(dispatcher.CheckpointTarget.CaptureDirectory);
            Assert.NotEqual(artifacts, dispatcher.CheckpointTarget.CaptureDirectory);
            Assert.True(File.Exists(Path.Combine(dispatcher.OutputDirectory!, "report.md")));
            Assert.False(File.Exists(Path.Combine(dispatcher.OutputDirectory!, "verdict.json")));
            var checkpoint = Assert.Single(events.OfType<FlowEvent.ArtifactCheckpointAttempted>());
            Assert.Equal(["report.md", "verdict.json"], checkpoint.OutputNames);
            Assert.NotEqual(checkpoint.PredecessorExecutionId, checkpoint.CheckpointExecutionId);
            var completion = Assert.Single(events.OfType<FlowEvent.ArtifactCheckpointCompleted>());
            Assert.Equal(checkpoint.CheckpointExecutionId, completion.CheckpointExecutionId);
            var ordered = events.Where(e => e is FlowEvent.ArtifactCheckpointAttempted or FlowEvent.ExecutionArrested).ToArray();
            Assert.IsType<FlowEvent.ArtifactCheckpointAttempted>(ordered[0]);
            Assert.IsType<FlowEvent.ExecutionArrested>(ordered[1]);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
        }
    }

    [Fact]
    public async Task A_partial_valid_review_contract_survives_the_checkpoint_and_settles_successfully()
    {
        var room = Path.Combine(Path.GetTempPath(), "baton-artifact-checkpoint-partial-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(room, "workspace");
        var artifacts = Path.Combine(room, "artifacts");
        var log = Path.Combine(room, "flow.jsonl");
        Directory.CreateDirectory(workspace);
        try
        {
            var state = await RunCheckpointExecutionAsync(
                room, workspace, artifacts, log,
                [new ProducedOutput("report.md", Schema: OutputSchema.NonEmptyText),
                 new ProducedOutput("verdict.json", Schema: OutputSchema.NonEmptyText)],
                artifactDirectory => File.WriteAllText(Path.Combine(artifactDirectory, "report.md"), "Review complete."),
                artifactDirectory => File.WriteAllText(Path.Combine(artifactDirectory, "verdict.json"), "{\"decision\":\"approve\"}"));

            var events = await new FlowEventLogReader(log).ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Equal(StepStatus.Succeeded, Assert.Single(state.Steps).Status);
            Assert.Single(events.OfType<FlowEvent.ExecutionArrested>());
            Assert.Single(events.OfType<FlowEvent.ExecutionSucceeded>());
            var checkpoint = Assert.Single(events.OfType<FlowEvent.ArtifactCheckpointAttempted>());
            Assert.Equal(["verdict.json"], checkpoint.OutputNames);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
        }
    }

    [Fact]
    public async Task A_cancellation_racing_a_cap_arrest_does_not_start_an_artifact_checkpoint()
    {
        var room = Path.Combine(Path.GetTempPath(), "baton-artifact-checkpoint-cancel-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(room, "workspace");
        var artifacts = Path.Combine(room, "artifacts");
        var log = Path.Combine(room, "flow.jsonl");
        Directory.CreateDirectory(workspace);
        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("artifact-checkpoint-cancel"), new WorkflowTemplateId("review"), 1,
                [new WorkflowStepDefinition(new StepId("review"), "review", [], ["report.md", "verdict.json"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]);
            var binding = new WorkerBinding.Process(
                new WorkerContract("review", [], [new ProducedOutput("report.md"), new ProducedOutput("verdict.json")], []),
                CheckpointTarget(workspace),
                TimeSpan.FromSeconds(30), Adapter: "codex", TokenBudget: 100, VerifiesWorkspace: false);
            using var cancellation = new CancellationTokenSource();
            var dispatcher = new CheckpointDispatcher(artifacts, cancellation.Cancel);
            await using var writer = new FlowEventLogWriter(log);
            var reader = new FlowEventLogReader(log);

            await MutationInterface.StartWorkflowAsync(
                new WorkflowId("artifact-checkpoint-cancel"), room, snapshot,
                new Dictionary<string, WorkerBinding> { ["review"] = binding }, artifacts, reader, writer, dispatcher,
                cancellationToken: cancellation.Token);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1, dispatcher.CallCount);
            Assert.Empty(events.OfType<FlowEvent.ArtifactCheckpointAttempted>());
            Assert.Empty(events.OfType<FlowEvent.ArtifactCheckpointCompleted>());
            Assert.Single(events.OfType<FlowEvent.ExecutionArrested>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
        }
    }

    [Fact]
    public async Task A_structured_grant_broker_target_resumes_the_captured_thread_for_the_checkpoint()
    {
        var room = Path.Combine(Path.GetTempPath(), "baton-artifact-checkpoint-broker-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(room, "workspace");
        var artifacts = Path.Combine(room, "artifacts");
        var log = Path.Combine(room, "flow.jsonl");
        Directory.CreateDirectory(workspace);
        try
        {
            var contract = new WorkerContract("review", [], [new ProducedOutput("report.md")], []);
            var target = new CodexWorkerAdapter().Resolve(
                new WorkerInvocation("Review.", PermissionGrant: new PermissionGrant(ReadFiles: true)), contract);
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("artifact-checkpoint-broker"), new WorkflowTemplateId("review"), 1,
                [new WorkflowStepDefinition(new StepId("review"), "review", [], ["report.md"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]);
            var binding = new WorkerBinding.Process(
                contract, target, TimeSpan.FromSeconds(30), Adapter: "codex", TokenBudget: 100, VerifiesWorkspace: false);
            var dispatcher = new CheckpointDispatcher(artifacts);
            await using var writer = new FlowEventLogWriter(log);
            var reader = new FlowEventLogReader(log);

            await MutationInterface.StartWorkflowAsync(
                new WorkflowId("artifact-checkpoint-broker"), room, snapshot,
                new Dictionary<string, WorkerBinding> { ["review"] = binding }, artifacts, reader, writer, dispatcher,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(2, dispatcher.CallCount);
            Assert.NotNull(dispatcher.CheckpointTarget);
            Assert.Equal("dotnet", dispatcher.CheckpointTarget.Program);
            Assert.Contains("codex-broker", dispatcher.CheckpointTarget.Args);
            Assert.NotNull(dispatcher.CheckpointTarget.ResumeTarget);
            Assert.Equal(["report.md"], dispatcher.CheckpointTarget.ArtifactOnlyOutputNames);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
        }
    }

    private static CoreDispatchTarget CheckpointTarget(string workspace) => new(
        "fake", ["ORIGINAL"], workspace, PromptText: "ORIGINAL",
        TryGetSessionId: _ => "checkpoint-session",
        ResumeArgs: (_, prompt) => [prompt]);

    private static void InitializeGitRepository(string workspace)
    {
        RunGit(workspace, "init", "-b", "main");
        RunGit(workspace, "config", "user.email", "test@example.com");
        RunGit(workspace, "config", "user.name", "Test");
        RunGit(workspace, "commit", "--allow-empty", "-m", "initial");
    }

    private static void RunGit(string workingDirectory, params string[] args)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)!;
        var stderr = process.StandardError.ReadToEndAsync();
        _ = process.StandardOutput.ReadToEndAsync();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stderr.Result}");
    }

    private static async Task<FlowState> RunBoundaryExecutionAsync(
        string room,
        string workspace,
        string artifacts,
        string log,
        IReadOnlyList<ProducedOutput> outputs,
        Action<string> writeArtifacts,
        string role = "review",
        bool changesTree = false,
        bool verifiesWorkspace = false,
        bool deliversBranch = false)
    {
        var stepId = new StepId(role);
        var snapshot = new WorkflowDefinitionSnapshot(
            new WorkflowDefinitionSnapshotId("artifact-boundary"), new WorkflowTemplateId("review"), 1,
            [new WorkflowStepDefinition(stepId, role, [], outputs.Select(output => output.Name).ToArray(), DependsOn: [], RetryPolicy: new RetryPolicy(1))]);
        var binding = new WorkerBinding.Process(
            new WorkerContract(role, [], outputs, []),
            CheckpointTarget(workspace), TimeSpan.FromSeconds(30), Adapter: "codex", TokenBudget: 100,
            ChangesTree: changesTree, VerifiesWorkspace: verifiesWorkspace, DeliversBranch: deliversBranch);
        var dispatcher = new CheckpointDispatcher(artifacts, writeAtCap: writeArtifacts, finishAtCap: true);
        await using var writer = new FlowEventLogWriter(log);
        var reader = new FlowEventLogReader(log);
        return await MutationInterface.StartWorkflowAsync(
            new WorkflowId("artifact-boundary"), room, snapshot,
            new Dictionary<string, WorkerBinding> { [role] = binding }, artifacts, reader, writer, dispatcher,
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private static async Task<FlowState> RunCheckpointExecutionAsync(
        string room,
        string workspace,
        string artifacts,
        string log,
        IReadOnlyList<ProducedOutput> outputs,
        Action<string> writeAtCap,
        Action<string> writeAtCheckpoint)
    {
        var stepId = new StepId("review");
        var snapshot = new WorkflowDefinitionSnapshot(
            new WorkflowDefinitionSnapshotId("artifact-checkpoint-partial"), new WorkflowTemplateId("review"), 1,
            [new WorkflowStepDefinition(stepId, "review", [], outputs.Select(output => output.Name).ToArray(), DependsOn: [], RetryPolicy: new RetryPolicy(1))]);
        var binding = new WorkerBinding.Process(
            new WorkerContract("review", [], outputs, []), CheckpointTarget(workspace), TimeSpan.FromSeconds(30),
            Adapter: "codex", TokenBudget: 100, VerifiesWorkspace: false);
        var dispatcher = new CheckpointDispatcher(artifacts, writeAtCap: writeAtCap, writeAtCheckpoint: writeAtCheckpoint);
        await using var writer = new FlowEventLogWriter(log);
        var reader = new FlowEventLogReader(log);
        return await MutationInterface.StartWorkflowAsync(
            new WorkflowId("artifact-checkpoint-partial"), room, snapshot,
            new Dictionary<string, WorkerBinding> { ["review"] = binding }, artifacts, reader, writer, dispatcher,
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private sealed class CheckpointDispatcher(
        string artifacts,
        Action? cancelAfterCap = null,
        Action<string>? writeAtCap = null,
        Action<string>? writeAtCheckpoint = null,
        bool finishAtCap = false) : ICoreDispatcher
    {
        public int CallCount { get; private set; }
        public CoreDispatchTarget? CheckpointTarget { get; private set; }
        public string? OutputDirectory { get; private set; }

        public async Task<CoreDispatchResult> DispatchAsync(
            ExecutionRequest request, CoreDispatchTarget target, CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (CallCount == 1)
            {
                target.OnStdoutLine?.Invoke("""{"type":"thread.started","thread_id":"checkpoint-session"}""");
                target.OnStdoutLine?.Invoke(ArrestingUsage);
                var primaryOutputDirectory = request.Environment
                    .OfType<EnvironmentVariable.BatonComputed>()
                    .Single(variable => variable.Name == "BATON_OUTPUT_DIR")
                    .Value;
                OutputDirectory = primaryOutputDirectory;
                Directory.CreateDirectory(primaryOutputDirectory);
                writeAtCap?.Invoke(primaryOutputDirectory);
                if (finishAtCap)
                {
                    return new CoreDispatchResult(0, CoreExitReason.Natural);
                }

                cancelAfterCap?.Invoke();
                var cancelled = new TaskCompletionSource();
                await using var registration = cancellationToken.Register(() => cancelled.TrySetResult());
                await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(120), CancellationToken.None);
                return new CoreDispatchResult(-1, CoreExitReason.CancelRequested);
            }

            CheckpointTarget = target;
            var outputDirectory = request.Environment
                .OfType<EnvironmentVariable.BatonComputed>()
                .Single(variable => variable.Name == "BATON_OUTPUT_DIR")
                .Value;
            Directory.CreateDirectory(artifacts);
            Directory.CreateDirectory(outputDirectory);
            if (writeAtCheckpoint is not null)
            {
                writeAtCheckpoint(outputDirectory);
            }
            else
            {
                File.WriteAllText(Path.Combine(outputDirectory, "report.md"), "BLOCK: insufficient observed evidence.");
            }
            return new CoreDispatchResult(0, CoreExitReason.Natural);
        }
    }
}
