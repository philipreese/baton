using Baton.Tests.TestSupport;
using Baton.Artifacts;
using Baton.Dispatch;
using Baton.Domain;

namespace Baton.Tests.Artifacts;

public class ArtifactManagerTests
{
    private static readonly StepId Architect = new("architect");
    private static readonly StepId Critic = new("critic");

    private static WorkflowDefinitionSnapshot TwoStepSnapshot() => new(
        new WorkflowDefinitionSnapshotId("snapshot-1"),
        new WorkflowTemplateId("architect-critic"),
        WorkflowTemplateVersion: 1,
        Steps:
        [
            new WorkflowStepDefinition(Architect, "architect", [], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(1)),
            new WorkflowStepDefinition(Critic, "critic", ["plan"], ["review"], DependsOn: [Architect], RetryPolicy: new RetryPolicy(1)),
        ]);

    private static readonly IReadOnlyDictionary<StepId, ExecutionId> NoUpstream = new Dictionary<StepId, ExecutionId>();

    [Fact]
    public void AllocateOutputDirectory_creates_and_returns_the_execution_scoped_directory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"artifacts-{Guid.NewGuid():N}");
        try
        {
            var directory = ArtifactManager.AllocateOutputDirectory(root, new ExecutionId("exec-1"));

            Assert.Equal(Path.Combine(root, "execution_exec-1"), directory);
            Assert.True(Directory.Exists(directory));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public void AllocateOutputDirectory_is_idempotent_for_the_same_ExecutionId()
    {
        var root = Path.Combine(Path.GetTempPath(), $"artifacts-{Guid.NewGuid():N}");
        try
        {
            var first = ArtifactManager.AllocateOutputDirectory(root, new ExecutionId("exec-1"));
            var second = ArtifactManager.AllocateOutputDirectory(root, new ExecutionId("exec-1"));

            Assert.Equal(first, second);
            Assert.True(Directory.Exists(second));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public void ResolveInputPaths_returns_empty_for_a_step_with_no_declared_inputs()
    {
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [new StepState(Architect, StepStatus.Succeeded, new ExecutionId("A1"), NoUpstream)]);

        var paths = ArtifactManager.ResolveInputPaths(
            TwoStepSnapshot().Steps[0], TwoStepSnapshot(), state, "/artifacts");

        Assert.Empty(paths);
    }

    [Fact]
    public void ResolveInputPaths_resolves_an_input_to_its_producing_dependencys_output_directory()
    {
        var snapshot = TwoStepSnapshot();
        var state = new FlowState(
            snapshot.WorkflowDefinitionSnapshotId,
            [
                new StepState(Architect, StepStatus.Succeeded, new ExecutionId("A1"), NoUpstream),
                new StepState(Critic, StepStatus.Pending, null, NoUpstream),
            ]);

        var paths = ArtifactManager.ResolveInputPaths(snapshot.Steps[1], snapshot, state, "/artifacts");

        Assert.Equal([Path.Combine("/artifacts", "execution_A1", "plan")], paths);
    }

    [Fact]
    public void ResolveInputPaths_throws_when_no_direct_dependency_declares_the_input_name()
    {
        var snapshot = new WorkflowDefinitionSnapshot(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            new WorkflowTemplateId("wf"),
            WorkflowTemplateVersion: 1,
            Steps:
            [
                new WorkflowStepDefinition(Architect, "architect", [], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(1)),
                new WorkflowStepDefinition(Critic, "critic", ["nonexistent"], ["review"], DependsOn: [Architect], RetryPolicy: new RetryPolicy(1)),
            ]);
        var state = new FlowState(
            snapshot.WorkflowDefinitionSnapshotId,
            [
                new StepState(Architect, StepStatus.Succeeded, new ExecutionId("A1"), NoUpstream),
                new StepState(Critic, StepStatus.Pending, null, NoUpstream),
            ]);

        Assert.Throws<ArtifactResolutionException>(
            () => ArtifactManager.ResolveInputPaths(snapshot.Steps[1], snapshot, state, "/artifacts"));
    }

    [Fact]
    public void ResolveInputPaths_throws_when_the_producing_dependency_has_no_successful_execution_yet()
    {
        var snapshot = TwoStepSnapshot();
        var state = new FlowState(
            snapshot.WorkflowDefinitionSnapshotId,
            [
                new StepState(Architect, StepStatus.Pending, null, NoUpstream),
                new StepState(Critic, StepStatus.Pending, null, NoUpstream),
            ]);

        Assert.Throws<ArtifactResolutionException>(
            () => ArtifactManager.ResolveInputPaths(snapshot.Steps[1], snapshot, state, "/artifacts"));
    }

    [Fact]
    public void BuildEnvironment_numbers_inputs_in_order_and_appends_BATON_OUTPUT_DIR_and_BATON_ARTIFACTS_ROOT()
    {
        var variables = ArtifactManager.BuildEnvironment(
            [Qualified("/artifacts/execution_A1/plan"), Qualified("/artifacts/execution_B1/goal")], Qualified("/artifacts/execution_C1"), Qualified("/artifacts"));

        Assert.Equal(
            [
                new EnvironmentVariable.BatonComputed("BATON_INPUT_0", Qualified("/artifacts/execution_A1/plan")),
                new EnvironmentVariable.BatonComputed("BATON_INPUT_1", Qualified("/artifacts/execution_B1/goal")),
                new EnvironmentVariable.BatonComputed("BATON_OUTPUT_DIR", Qualified("/artifacts/execution_C1")),
                new EnvironmentVariable.BatonComputed("BATON_ARTIFACTS_ROOT", Qualified("/artifacts")),
                LockWaitLog("/artifacts/execution_C1"),
            ],
            variables);
    }

    [Fact]
    public void BuildEnvironment_with_no_inputs_still_sets_BATON_OUTPUT_DIR_and_BATON_ARTIFACTS_ROOT()
    {
        var variables = ArtifactManager.BuildEnvironment([], Qualified("/artifacts/execution_C1"), Qualified("/artifacts"));

        Assert.Equal(
            [
                new EnvironmentVariable.BatonComputed("BATON_OUTPUT_DIR", Qualified("/artifacts/execution_C1")),
                new EnvironmentVariable.BatonComputed("BATON_ARTIFACTS_ROOT", Qualified("/artifacts")),
                LockWaitLog("/artifacts/execution_C1"),
            ],
            variables);
    }

    [Fact]
    public void BuildEnvironment_with_a_supplement_appends_BATON_SUPPLEMENTARY_INPUT_after_BATON_ARTIFACTS_ROOT()
    {
        var variables = ArtifactManager.BuildEnvironment(
            [], Qualified("/artifacts/execution_C1"), Qualified("/artifacts"), Qualified("/artifacts/execution_S1"));

        Assert.Equal(
            [
                new EnvironmentVariable.BatonComputed("BATON_OUTPUT_DIR", Qualified("/artifacts/execution_C1")),
                new EnvironmentVariable.BatonComputed("BATON_ARTIFACTS_ROOT", Qualified("/artifacts")),
                LockWaitLog("/artifacts/execution_C1"),
                new EnvironmentVariable.BatonComputed("BATON_SUPPLEMENTARY_INPUT", Qualified("/artifacts/execution_S1")),
            ],
            variables);
    }

    /// <summary>
    /// #2019: the build-lock wait log every execution's commands append to, derived from the output
    /// directory. Spelled through the constants rather than by hand so a rename of either cannot leave
    /// these assertions agreeing with a file nothing writes.
    /// </summary>
    private static EnvironmentVariable.BatonComputed LockWaitLog(string posixOutputDirectory) =>
        new(
            BuildLockWaitCredit.LogEnvironmentVariable,
            Path.Combine(Qualified(posixOutputDirectory), BuildLockWaitCredit.LogFileName));

    /// <summary>
    /// A fixture path that is fully qualified on both platforms. <c>"/artifacts/x"</c> is rooted
    /// everywhere but fully qualified only on POSIX — on Windows it resolves against the current
    /// drive, so <see cref="ArtifactManager.BuildEnvironment"/> refuses it (#668). Written out, these
    /// three assertions passed on the Linux CI leg and failed on the Windows one.
    /// </summary>
    private static string Qualified(string posixPath) => Path.GetFullPath(posixPath);
}
