using Baton.Dispatch;
using Baton.Domain;
using Baton.Mutation;
using Baton.Store;
using Baton.Artifacts;

namespace Baton.Tests.Mutation;

public sealed class MutationInterfaceRecoveryTests
{
    [Fact]
    public async Task Live_late_failure_settles_once_and_preserves_the_valid_artifact()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"late-failure-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        try
        {
            var stepId = new StepId("review");
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("late-failure-snapshot"),
                new WorkflowTemplateId("late-failure"),
                1,
                [new WorkflowStepDefinition(stepId, "review", [], ["report.md"], [], new RetryPolicy(1))]);
            var contract = new WorkerContract(
                "review", [], [new ProducedOutput("report.md", Schema: OutputSchema.NonEmptyText)], []);
            var binding = new WorkerBinding.Process(contract, new CoreDispatchTarget("unused", []), TimeSpan.FromSeconds(30));
            var dispatcher = new LiveLateFailureDispatcher(artifactsRoot);

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var state = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("late-failure-workflow"),
                roomDirectory,
                snapshot,
                new Dictionary<string, WorkerBinding> { ["review"] = binding },
                artifactsRoot,
                reader,
                writer,
                dispatcher,
                cancellationToken: TestContext.Current.CancellationToken);

            var step = Assert.Single(state.Steps);
            Assert.Equal(StepStatus.Succeeded, step.Status);
            Assert.Equal(1, dispatcher.CallCount);
            Assert.NotNull(step.LateFailureReason);
            Assert.True(File.Exists(Path.Combine(
                ArtifactManager.ResolveOutputDirectory(artifactsRoot, step.LatestExecutionId!.Value), "report.md")));

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Single(events.OfType<FlowEvent.ExecutionSucceededWithLateFailure>());
            Assert.DoesNotContain(events, e => e is FlowEvent.ExecutionFailed);
            var accepted = Assert.Single(events.OfType<FlowEvent.ExecutionRequestAccepted>());
            Assert.Equal(contract.ProducedOutputs, accepted.Request.ProducedOutputs);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task Outstanding_tool_recovery_continues_once_in_the_same_workspace_then_stops()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"recovery-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        try
        {
            var stepId = new StepId("implement");
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("recovery-snapshot"),
                new WorkflowTemplateId("recovery"),
                1,
                [new WorkflowStepDefinition(stepId, "implement", [], [], [], new RetryPolicy(3, BackoffPolicy.None))]);
            var workspace = Path.Combine(roomDirectory, "workspace");
            Directory.CreateDirectory(workspace);
            const string originalPrompt = "Finish the original acceptance.";
            var binding = new WorkerBinding.Process(
                new WorkerContract("implement", [], [], []),
                new CoreDispatchTarget("unused", [originalPrompt], workspace, PromptText: originalPrompt),
                TimeSpan.FromSeconds(30));
            var bindings = new Dictionary<string, WorkerBinding> { ["implement"] = binding };
            var dispatcher = new RecoveryDispatcher();

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("recovery-workflow"),
                roomDirectory,
                snapshot,
                bindings,
                artifactsRoot,
                reader,
                writer,
                dispatcher,
                cancellationToken: TestContext.Current.CancellationToken);

            var step = Assert.Single(finalState.Steps);
            Assert.Equal(StepStatus.Failed, step.Status);
            Assert.Equal(FailureClassification.Permanent, step.LatestFailureClassification);
            Assert.Equal(RecoveryCauseKind.OutstandingToolAtTerminalSuccess, step.LatestRecoveryCause!.Kind);
            Assert.Equal(2, step.RecoveryOccurrence);
            Assert.Equal(2, dispatcher.Targets.Count);
            Assert.Equal(workspace, dispatcher.Targets[0].WorkingDirectory);
            Assert.Equal(workspace, dispatcher.Targets[1].WorkingDirectory);
            Assert.Equal(originalPrompt, dispatcher.Targets[0].PromptText);
            Assert.Contains("same workspace", dispatcher.Targets[1].PromptText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("run_command", dispatcher.Targets[1].PromptText, StringComparison.Ordinal);
            Assert.Contains("synchronously", dispatcher.Targets[1].PromptText, StringComparison.Ordinal);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, events.OfType<FlowEvent.ExecutionRequestAccepted>().Count(e => e.Request.StepId == stepId));
            Assert.Equal(2, events.OfType<FlowEvent.ExecutionFailed>().Count());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    private sealed class RecoveryDispatcher : ICoreDispatcher
    {
        public List<CoreDispatchTarget> Targets { get; } = [];

        public Task<CoreDispatchResult> DispatchAsync(
            ExecutionRequest request,
            CoreDispatchTarget target,
            CancellationToken cancellationToken = default)
        {
            Targets.Add(target);
            var fact = new OutstandingToolAtTerminalSuccess("run_command", "dotnet build -warnaserror");
            return Task.FromResult(new CoreDispatchResult(
                0,
                CoreExitReason.Natural,
                TerminalSuccessObserved: true,
                OutstandingToolAtTerminalSuccess: fact));
        }
    }

    private sealed class LiveLateFailureDispatcher(string artifactsRoot) : ICoreDispatcher
    {
        public int CallCount { get; private set; }

        public Task<CoreDispatchResult> DispatchAsync(
            ExecutionRequest request,
            CoreDispatchTarget target,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            var outputDirectory = ArtifactManager.ResolveOutputDirectory(artifactsRoot, request.ExecutionId);
            Directory.CreateDirectory(outputDirectory);
            File.WriteAllText(Path.Combine(outputDirectory, "report.md"), "complete report");
            return Task.FromResult(new CoreDispatchResult(
                1,
                CoreExitReason.Natural,
                StderrTail: "late capacity failure",
                TerminalResultObserved: true));
        }
    }
}
