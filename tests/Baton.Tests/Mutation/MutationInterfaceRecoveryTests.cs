using Baton.Dispatch;
using Baton.Domain;
using Baton.Mutation;
using Baton.Store;

namespace Baton.Tests.Mutation;

public sealed class MutationInterfaceRecoveryTests
{
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
}
