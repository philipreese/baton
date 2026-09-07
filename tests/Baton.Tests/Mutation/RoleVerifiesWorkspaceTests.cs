using Baton.Dispatch;
using Baton.Domain;
using Baton.Mutation;
using Baton.Store;
using Baton.Tests.TestSupport;
using static Baton.Tests.TestSupport.ShellWorkerCommands;

namespace Baton.Tests.Mutation;

/// <summary>
/// #2029: the engine-side half of <c>Baton.Vendors.WorkerRole.VerifiesWorkspace</c>, whose own remarks
/// state the rule these tests exercise. The three here drive the SAME red tree — one workspace, one
/// committed <c>.baton/verify</c> that exits non-zero — so the polarity pair is a single condition
/// apart and the tree cannot be what differs.
/// <para>
/// The red is a hermetic <c>python -c "sys.exit(1)"</c> rather than a real <c>audit-*</c> task: the
/// measured room's red happened to be <c>audit-waitceiling</c>, but nothing about this behaviour is
/// about WHICH gate went red, and a test that spawns the real gate suite would be slow and would tie
/// itself to <c>pixi.toml</c>'s task list.
/// </para>
/// </summary>
public class RoleVerifiesWorkspaceTests
{
    private static readonly StepId Worker = new("architect");

    /// <summary>
    /// The measured defect (room <c>dispatch-review-2aa39890</c>, 2026-09-07): a review lane whose
    /// verdict landed cleanly settled <c>Failed — Verify failed</c> because the workspace's audits ran
    /// against the branch it was reviewing. Against the pre-fix engine this settles <c>Failed</c> with
    /// a <c>VerifyFailed</c> event.
    /// </summary>
    [Fact]
    public async Task A_role_that_does_not_verify_the_workspace_settles_Succeeded_on_a_tree_whose_verify_is_red()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var workspace = await CreateWorkspaceWithRedVerifyDeclarationAsync();
        try
        {
            var (finalState, events) = await RunAsync(
                roomDirectory, workspace, "read-shaped", binding => binding with { VerifiesWorkspace = false });

            var step = Assert.Single(finalState.Steps);
            Assert.Equal(StepStatus.Succeeded, step.Status);
            Assert.Null(step.IndeterminateReason);
            // Not a not-run either: the command was never resolved, so there is nothing to report as
            // unrunnable and no UNVERIFIED chip to render.
            Assert.Null(step.VerifyNotRunReason);

            Assert.Empty(events.OfType<FlowEvent.VerifyStarted>());
            Assert.Empty(events.OfType<FlowEvent.VerifyFailed>());
            Assert.Empty(events.OfType<FlowEvent.VerifyNotRun>());
            Assert.Single(events.OfType<FlowEvent.ExecutionSucceeded>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
            DirectoryCleanup.DeleteRecursively(workspace);
        }
    }

    /// <summary>
    /// The discriminating control for the test above: the SAME tree, the same worker, one flag flipped.
    /// Without this arm a resolver that had simply stopped running any verify at all would pass.
    /// </summary>
    [Fact]
    public async Task A_role_that_verifies_the_workspace_still_fails_on_the_same_red_tree()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var workspace = await CreateWorkspaceWithRedVerifyDeclarationAsync();
        try
        {
            var (finalState, events) = await RunAsync(
                roomDirectory, workspace, "tree-changing", binding => binding with { VerifiesWorkspace = true });

            var step = Assert.Single(finalState.Steps);
            Assert.Equal(StepStatus.Failed, step.Status);
            Assert.NotNull(step.IndeterminateReason);

            Assert.Single(events.OfType<FlowEvent.VerifyStarted>());
            Assert.Single(events.OfType<FlowEvent.VerifyFailed>());
            Assert.Empty(events.OfType<FlowEvent.ExecutionSucceeded>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
            DirectoryCleanup.DeleteRecursively(workspace);
        }
    }

    /// <summary>
    /// Arm 1 stays role-independent (spec/baton.md §3): an operator who typed <c>--verify</c> for this
    /// dispatch gets it even on a role the declaration and role-default arms are withheld from. The
    /// override here is its own red, so a run that ignored it would settle Succeeded.
    /// </summary>
    [Fact]
    public async Task An_operator_verify_override_still_runs_for_a_role_that_does_not_verify_the_workspace()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var workspace = await CreateWorkspaceWithRedVerifyDeclarationAsync();
        try
        {
            var (finalState, events) = await RunAsync(
                roomDirectory,
                workspace,
                "read-shaped-with-override",
                binding => binding with { VerifiesWorkspace = false, VerifyCommandOverride = "exit 3" });

            var step = Assert.Single(finalState.Steps);
            Assert.Equal(StepStatus.Failed, step.Status);
            Assert.Single(events.OfType<FlowEvent.VerifyStarted>());
            Assert.Single(events.OfType<FlowEvent.VerifyFailed>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
            DirectoryCleanup.DeleteRecursively(workspace);
        }
    }

    /// <summary>
    /// A committed, reviewed <c>.baton/verify</c> whose command exits non-zero — the one thing in these
    /// runs that can make a verify go red, so a red settle can only have come from it.
    /// </summary>
    private static async Task<string> CreateWorkspaceWithRedVerifyDeclarationAsync()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"workspace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(workspace, ".baton"));
        await File.WriteAllTextAsync(
            Path.Combine(workspace, ".baton", "verify"),
            "python -c \"import sys; sys.exit(1)\"\n",
            TestContext.Current.CancellationToken);
        TempGitRepository.InitWithEverythingCommitted(workspace);
        TempGitRepository.SetReviewedBaselineAtHead(workspace);
        return workspace;
    }

    private static async Task<(FlowState State, IReadOnlyList<FlowEvent> Events)> RunAsync(
        string roomDirectory, string workspace, string id, Func<WorkerBinding.Process, WorkerBinding.Process> configure)
    {
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");

        var snapshot = new WorkflowDefinitionSnapshot(
            new WorkflowDefinitionSnapshotId($"snapshot-{id}"),
            new WorkflowTemplateId(id),
            WorkflowTemplateVersion: 1,
            Steps: [new WorkflowStepDefinition(Worker, "architect", [], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]);

        var binding = new WorkerBinding.Process(
            new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
            WriteFile("plan", "architect") with { WorkingDirectory = workspace },
            TimeSpan.FromSeconds(30));

        var bindings = new Dictionary<string, WorkerBinding> { ["architect"] = configure(binding) };

        await using var writer = new FlowEventLogWriter(logPath);
        var reader = new FlowEventLogReader(logPath);
        var dispatcher = new CoreDispatcher(writer, writer);

        var finalState = await MutationInterface.StartWorkflowAsync(
            new WorkflowId($"wf-{id}"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher,
            cancellationToken: TestContext.Current.CancellationToken);

        return (finalState, await reader.ReadAllAsync(TestContext.Current.CancellationToken));
    }
}
