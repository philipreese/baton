using Baton.Artifacts;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Mutation;
using Baton.Store;
using Baton.Tests.TestSupport;

namespace Baton.Tests.Mutation;

public sealed class MutationInterfaceRuntimeDeliveryRouteTests
{
    private static readonly StepId Implementer = new("implementer");
    private static readonly WorkerContract Contract = new(
        "implementer", [], [new ProducedOutput("changes.md", Schema: OutputSchema.NonEmptyText)], []);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task RetryWithRevision_consumes_runtime_body_file_provenance()
    {
        var (workspace, origin) = CreatePushedWorkspace("route-retry");
        var (roomDirectory, artifactsRoot, logPath) = CreateRoomPaths();
        try
        {
            var snapshot = MakeSnapshot();
            var bindings = Bindings(workspace);
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new RouteDispatcher(workspace, artifactsRoot, "route-retry", firstSucceeds: false);
            var workflowId = new WorkflowId("wf-runtime-retry");
            var pausedState = await MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot,
                reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);
            var pausedExecutionId = Assert.Single(pausedState.Steps).LatestExecutionId!.Value;

            await MutationInterface.RecordDecisionAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot,
                reader, writer, dispatcher, pausedExecutionId, DecisionType.RetryWithRevision,
                cancellationToken: TestContext.Current.CancellationToken);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.True(events.OfType<FlowEvent.VerifyFailed>().Any(), string.Join("\n", events));
            var failure = events.OfType<FlowEvent.VerifyFailed>().Single();
            Assert.Equal(["generated-files-forbidden"], failure.FailingMembers);
            Assert.Contains("runtime direct pull-request creation body-file request", failure.Tail, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(roomDirectory, workspace, origin);
        }
    }

    [Fact]
    public async Task Continuation_consumes_runtime_body_file_provenance()
    {
        var (workspace, origin) = CreatePushedWorkspace("route-continuation");
        var (roomDirectory, artifactsRoot, logPath) = CreateRoomPaths();
        try
        {
            var snapshot = MakeSnapshot();
            var bindings = Bindings(workspace);
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new RouteDispatcher(workspace, artifactsRoot, "route-continuation", firstSucceeds: true);
            var workflowId = new WorkflowId("wf-runtime-continuation");
            var pausedState = await MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot,
                reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(StepStatus.Paused, Assert.Single(pausedState.Steps).Status);

            await MutationInterface.RecordResumeAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, "implementer",
                reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.True(events.OfType<FlowEvent.VerifyFailed>().Any(), string.Join("\n", events));
            var failure = events.OfType<FlowEvent.VerifyFailed>().Single();
            Assert.Equal(["generated-files-forbidden"], failure.FailingMembers);
            Assert.Contains("runtime direct pull-request creation body-file request", failure.Tail, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(roomDirectory, workspace, origin);
        }
    }

    private static IReadOnlyDictionary<string, WorkerBinding> Bindings(string workspace) =>
        new Dictionary<string, WorkerBinding>
        {
            ["implementer"] = new WorkerBinding.Process(
                Contract, new CoreDispatchTarget("stub", [], WorkingDirectory: workspace), Timeout,
                DeliversBranch: true),
        };

    private static WorkflowDefinitionSnapshot MakeSnapshot() => new(
        new WorkflowDefinitionSnapshotId($"runtime-route-{Guid.NewGuid():N}"),
        new WorkflowTemplateId("runtime-route"), 1,
        [new WorkflowStepDefinition(Implementer, "implementer", [], ["changes.md"], [],
            new RetryPolicy(1), new PausePoint([]))]);

    private sealed class RouteDispatcher(
        string workspace, string artifactsRoot, string branch, bool firstSucceeds) : ICoreDispatcher
    {
        private int _calls;

        public async Task<CoreDispatchResult> DispatchAsync(
            ExecutionRequest request, CoreDispatchTarget target, CancellationToken cancellationToken = default)
        {
            var call = ++_calls;
            var outputDirectory = ArtifactManager.ResolveOutputDirectory(artifactsRoot, request.ExecutionId);
            Directory.CreateDirectory(outputDirectory);
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "changes.md"), "handoff");
            if (call == 1)
            {
                await File.WriteAllTextAsync(Path.Combine(workspace, "initial-product.txt"), "product change\n");
            }
            else
            {
                using var producer = new RuntimeDirectPullRequestProducer(workspace, outputDirectory);
                await producer.ProduceAsync();
            }

            TempGitRepository.CommitAll(workspace, $"route attempt {call}");
            TempGitRepository.Push(workspace, "origin", branch);
            return new CoreDispatchResult(call == 1 && !firstSucceeds ? 1 : 0, CoreExitReason.Natural,
                TerminalSuccessObserved: call > 1 || firstSucceeds,
                TerminalResultObserved: true);
        }
    }

    private static (string Workspace, string Origin) CreatePushedWorkspace(string branch)
    {
        var origin = TempGitRepository.InitBareRepository(
            Path.Combine(Path.GetTempPath(), $"route-origin-{Guid.NewGuid():N}"));
        var workspace = Path.Combine(Path.GetTempPath(), $"route-workspace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        TempGitRepository.InitWithEverythingCommitted(workspace);
        TempGitRepository.AddRemote(workspace, "origin", origin);
        TempGitRepository.CreateAndCheckoutBranch(workspace, branch);
        TempGitRepository.CommitAll(workspace, "lane baseline");
        TempGitRepository.Push(workspace, "origin", branch);
        return (workspace, origin);
    }

    private static (string RoomDirectory, string ArtifactsRoot, string LogPath) CreateRoomPaths()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"runtime-route-{Guid.NewGuid():N}");
        return (roomDirectory, Path.Combine(roomDirectory, "artifacts"), Path.Combine(roomDirectory, "flow.jsonl"));
    }

    private static void Cleanup(string roomDirectory, string workspace, string origin)
    {
        DirectoryCleanup.DeleteRecursively(roomDirectory);
        DirectoryCleanup.DeleteRecursively(workspace);
        DirectoryCleanup.DeleteRecursively(origin);
    }
}
