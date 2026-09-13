using Baton.Dispatch;
using Baton.Domain;
using Baton.Mutation;
using Baton.Store;
using Baton.Tests.Shared;

namespace Baton.Tests.EndToEnd;

/// <summary>Exercises #2276's one-shot, artifact-only dispatch through the live mutation seam.</summary>
public sealed class ArtifactCheckpointEndToEndTests
{
    private const string ArrestingUsage =
        """{"type":"turn.usage","usage":{"input_tokens":500,"cached_input_tokens":0,"output_tokens":1,"round_trip":1}}""";

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
            var target = new CoreDispatchTarget("fake", ["ORIGINAL"], workspace, PromptText: "ORIGINAL");
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
            Assert.True(File.Exists(Path.Combine(artifacts, "report.md")));
            Assert.False(File.Exists(Path.Combine(artifacts, "verdict.json")));
            var checkpoint = Assert.Single(events.OfType<FlowEvent.ArtifactCheckpointAttempted>());
            Assert.Equal(["report.md", "verdict.json"], checkpoint.OutputNames);
            Assert.NotEqual(checkpoint.PredecessorExecutionId, checkpoint.CheckpointExecutionId);
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
                [new WorkflowStepDefinition(new StepId("review"), "review", [], ["report.md"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]);
            var binding = new WorkerBinding.Process(
                new WorkerContract("review", [], [new ProducedOutput("report.md")], []),
                new CoreDispatchTarget("fake", ["ORIGINAL"], workspace, PromptText: "ORIGINAL"),
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
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
        }
    }

    private sealed class CheckpointDispatcher(string artifacts, Action? cancelAfterCap = null) : ICoreDispatcher
    {
        public int CallCount { get; private set; }
        public CoreDispatchTarget? CheckpointTarget { get; private set; }

        public async Task<CoreDispatchResult> DispatchAsync(
            ExecutionRequest request, CoreDispatchTarget target, CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (CallCount == 1)
            {
                target.OnStdoutLine?.Invoke(ArrestingUsage);
                cancelAfterCap?.Invoke();
                var cancelled = new TaskCompletionSource();
                await using var registration = cancellationToken.Register(() => cancelled.TrySetResult());
                await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(120), CancellationToken.None);
                return new CoreDispatchResult(-1, CoreExitReason.CancelRequested);
            }

            CheckpointTarget = target;
            Directory.CreateDirectory(artifacts);
            File.WriteAllText(Path.Combine(artifacts, "report.md"), "BLOCK: insufficient observed evidence.");
            return new CoreDispatchResult(0, CoreExitReason.Natural);
        }
    }
}
