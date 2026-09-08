using System.Diagnostics;
using System.Text.Json;
using Baton.Vendors;
using Baton.Cli.Tests.TestSupport;
using Baton.Domain;
using Baton.Mutation;
using Baton.Store;
using Baton.Templates;
using Baton.Workspaces;

namespace Baton.Cli.Tests;

/// <summary>
/// #1012 verification: <c>baton decide</c>, <c>baton supply</c>, and <c>baton cancel</c> provision declared
/// worktree workspaces before resolving worker bindings downstream, and tear down provisioned trees
/// when reaching <see cref="WorkflowStatus.Terminal"/>.
/// </summary>
public class WorktreeProvisioningCommandTests : IDisposable
{
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter> { ["shell"] = new ShellCommandWorkerAdapter() };

    private readonly IsolatedBatonHome _batonHome = new();

    public void Dispose()
    {
        _batonHome.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task DecideCommand_provisions_declared_worktree_resuming_step_reads_file_there_and_tears_down_on_terminal()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-worktree-decide-{Guid.NewGuid():N}");
        var repository = Path.Combine(testRoot, "repo");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            await SetupGitRepositoryAsync(repository, "notes.txt", "from-the-decide-worktree", "review-target");

            var workflowFilePath = await WriteApprovalGateWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteWorktreeBindingsAsync(testRoot, repository, "review-target");

            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);
            var pausedResult = await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(WorkflowStatus.Paused, pausedResult.State.Status);

            var worktreePath = Path.Combine(roomDirectory, WorktreeWorkspaces.WorkspacesDirectoryName, "b");
            Assert.True(Directory.Exists(worktreePath), "Worktree should be provisioned during run");

            var pausedExecutionId = pausedResult.State.Steps.Single(s => s.StepId.Value == "a").LatestExecutionId!.Value;
            var decideOptions = new DecideOptions(
                roomDirectory, pausedExecutionId.Value, DecisionType.Resume, TargetStepId: null,
                SupplementaryExecutionId: null, bindingsFilePath);

            var finalResult = await DecideCommand.ExecuteAsync(decideOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, finalResult.State.Status);
            Assert.Equal(StepStatus.Succeeded, finalResult.State.Steps.Single(s => s.StepId.Value == "b").Status);

            var reader = new FlowEventLogReader(Path.Combine(roomDirectory, "flow.jsonl"));
            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var bExecutionId = events.OfType<FlowEvent.ExecutionSucceeded>().Last().ExecutionId;
            var outputPath = Path.Combine(roomDirectory, "artifacts", $"execution_{bExecutionId}", "output_b");
            Assert.Equal(
                "from-the-decide-worktree", (await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken)).Trim());

            Assert.False(Directory.Exists(worktreePath), "Worktree should be torn down on Terminal status");
        }
        finally
        {
            ForceDeleteDirectory(testRoot);
        }
    }

    [Fact]
    public async Task DecideCommand_leaves_worktree_intact_when_resumed_to_paused_state()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-worktree-decide-paused-{Guid.NewGuid():N}");
        var repository = Path.Combine(testRoot, "repo");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            await SetupGitRepositoryAsync(repository, "notes.txt", "keep-worktree-on-paused", "review-target");

            var workflowFilePath = await WriteTwoGateWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteWorktreeBindingsAsyncForTwoGate(testRoot, repository, "review-target");

            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);
            var pausedResult1 = await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(WorkflowStatus.Paused, pausedResult1.State.Status);

            var worktreePath = Path.Combine(roomDirectory, WorktreeWorkspaces.WorkspacesDirectoryName, "b");
            Assert.True(Directory.Exists(worktreePath));

            var pausedExecutionId = pausedResult1.State.Steps.Single(s => s.StepId.Value == "a").LatestExecutionId!.Value;
            var decideOptions = new DecideOptions(
                roomDirectory, pausedExecutionId.Value, DecisionType.Resume, TargetStepId: null,
                SupplementaryExecutionId: null, bindingsFilePath);

            var pausedResult2 = await DecideCommand.ExecuteAsync(decideOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Paused, pausedResult2.State.Status);
            Assert.True(Directory.Exists(worktreePath), "Worktree must NOT be torn down while run remains Paused");
        }
        finally
        {
            ForceDeleteDirectory(testRoot);
        }
    }

    [Fact]
    public async Task SupplyCommand_provisions_worktree_and_tears_down_on_terminal()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-worktree-supply-{Guid.NewGuid():N}");
        var repository = Path.Combine(testRoot, "repo");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            await SetupGitRepositoryAsync(repository, "notes.txt", "from-supply-repo", "review-target");

            var workflowFilePath = await WriteApprovalGateWorkflowAsync(testRoot);
            var plainBindingsFilePath = await WritePlainBindingsAsync(Path.Combine(testRoot, "plain"));
            var worktreeBindingsFilePath = await WriteWorktreeBindingsAsync(Path.Combine(testRoot, "wt"), repository, "review-target");

            var runOptions = new RunOptions(workflowFilePath, plainBindingsFilePath, roomDirectory);
            var pausedResult = await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(WorkflowStatus.Paused, pausedResult.State.Status);

            var worktreePath = Path.Combine(roomDirectory, WorktreeWorkspaces.WorkspacesDirectoryName, "b");
            Assert.False(Directory.Exists(worktreePath), "Worktree should not exist yet with plain bindings");

            var sourceFilePath = Path.Combine(testRoot, "supp.txt");
            await File.WriteAllTextAsync(sourceFilePath, "from-supply-repo", TestContext.Current.CancellationToken);
            var supplyOptions = new SupplyOptions(roomDirectory, "human", "output_a", sourceFilePath, worktreeBindingsFilePath);

            var supplyResult = await SupplyCommand.ExecuteAsync(supplyOptions, Adapters, TestContext.Current.CancellationToken);
            Assert.Equal(WorkflowStatus.Paused, supplyResult.Command.State.Status);

            // SupplyCommand with worktreeBindingsFilePath provisioned worktreePath for worker 'b'
            Assert.True(Directory.Exists(worktreePath), "SupplyCommand must provision worktree when given worktree bindings");

            var pausedExecutionId = pausedResult.State.Steps.Single(s => s.StepId.Value == "a").LatestExecutionId!.Value;
            var decideOptions = new DecideOptions(
                roomDirectory, pausedExecutionId.Value, DecisionType.Resume, TargetStepId: null,
                SupplementaryExecutionId: supplyResult.ExecutionId.Value, worktreeBindingsFilePath);
            var finalResult = await DecideCommand.ExecuteAsync(decideOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, finalResult.State.Status);
            Assert.False(Directory.Exists(worktreePath), "Worktree should be torn down on Terminal status");
        }
        finally
        {
            ForceDeleteDirectory(testRoot);
        }
    }

    /// <summary>
    /// Inverted by #2073: <c>baton cancel</c> no longer reads bindings at all (nothing in it
    /// dispatches), so worktree bindings handed to it provision nothing — the paused room here has no
    /// arrestable target, the cancel is a no-op, and the worktree only appears once <c>decide</c>
    /// (which does dispatch) provisions it, and is torn down on Terminal as before.
    /// </summary>
    [Fact]
    public async Task CancelCommand_does_not_provision_a_worktree_and_decide_still_tears_it_down_on_terminal()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-worktree-cancel-{Guid.NewGuid():N}");
        var repository = Path.Combine(testRoot, "repo");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            await SetupGitRepositoryAsync(repository, "notes.txt", "from-cancel-repo", "review-target");

            var workflowFilePath = await WriteApprovalGateWorkflowAsync(testRoot);
            var plainBindingsFilePath = await WritePlainBindingsAsync(Path.Combine(testRoot, "plain"));
            var worktreeBindingsFilePath = await WriteWorktreeBindingsAsync(Path.Combine(testRoot, "wt"), repository, "review-target");

            var runOptions = new RunOptions(workflowFilePath, plainBindingsFilePath, roomDirectory);
            var pausedResult = await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(WorkflowStatus.Paused, pausedResult.State.Status);

            var worktreePath = Path.Combine(roomDirectory, WorktreeWorkspaces.WorkspacesDirectoryName, "b");
            Assert.False(Directory.Exists(worktreePath), "Worktree should not exist yet with plain bindings");

            var pausedExecutionId = pausedResult.State.Steps.Single(s => s.StepId.Value == "a").LatestExecutionId!.Value;
            var cancelOptions = new CancelOptions(roomDirectory, pausedExecutionId.Value, worktreeBindingsFilePath);

            var cancelResult = await CancelCommand.ExecuteAsync(cancelOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(WorkflowStatus.Paused, cancelResult.State.Status);
            Assert.True(cancelResult.CancelWasNoOp, "a paused step's execution has already settled; nothing to arrest");

            // #2073: bindings are never read by cancel, so nothing was provisioned.
            Assert.False(Directory.Exists(worktreePath), "CancelCommand must not provision a worktree — it reads no bindings since #2073");

            var decideOptions = new DecideOptions(
                roomDirectory, pausedExecutionId.Value, DecisionType.Resume, TargetStepId: null,
                SupplementaryExecutionId: null, worktreeBindingsFilePath);
            var finalResult = await DecideCommand.ExecuteAsync(decideOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, finalResult.State.Status);
            Assert.False(Directory.Exists(worktreePath), "Worktree should be torn down on Terminal status");
        }
        finally
        {
            ForceDeleteDirectory(testRoot);
        }
    }

    [Fact]
    public async Task Polarity_bindings_without_worktree_are_unaffected_no_tree_created()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-worktree-polarity-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteApprovalGateWorkflowAsync(testRoot);
            var bindingsFilePath = await WritePlainBindingsAsync(testRoot);

            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);
            var pausedResult = await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            var pausedExecutionId = pausedResult.State.Steps.Single(s => s.StepId.Value == "a").LatestExecutionId!.Value;
            var decideOptions = new DecideOptions(
                roomDirectory, pausedExecutionId.Value, DecisionType.Resume, TargetStepId: null,
                SupplementaryExecutionId: null, bindingsFilePath);

            var finalResult = await DecideCommand.ExecuteAsync(decideOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(WorkflowStatus.Terminal, finalResult.State.Status);

            var workspacesDir = Path.Combine(roomDirectory, WorktreeWorkspaces.WorkspacesDirectoryName);
            Assert.False(Directory.Exists(workspacesDir), "No workspaces directory created for plain bindings");
        }
        finally
        {
            ForceDeleteDirectory(testRoot);
        }
    }

    [Fact]
    public async Task Idempotence_provisioning_twice_reuses_existing_tree_without_throwing()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-worktree-idempotent-{Guid.NewGuid():N}");
        var repository = Path.Combine(testRoot, "repo");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            await SetupGitRepositoryAsync(repository, "notes.txt", "idempotence-test", "review-target");

            var workflowFilePath = await WriteApprovalGateWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteWorktreeBindingsAsync(testRoot, repository, "review-target");

            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);
            var pausedResult = await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            var worktreePath = Path.Combine(roomDirectory, WorktreeWorkspaces.WorkspacesDirectoryName, "b");
            Assert.True(Directory.Exists(worktreePath));

            // Calling DecideCommand on existing room directory provisions again (idempotent reuse)
            var pausedExecutionId = pausedResult.State.Steps.Single(s => s.StepId.Value == "a").LatestExecutionId!.Value;
            var decideOptions = new DecideOptions(
                roomDirectory, pausedExecutionId.Value, DecisionType.Resume, TargetStepId: null,
                SupplementaryExecutionId: null, bindingsFilePath);

            var finalResult = await DecideCommand.ExecuteAsync(decideOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(WorkflowStatus.Terminal, finalResult.State.Status);
        }
        finally
        {
            ForceDeleteDirectory(testRoot);
        }
    }

    [Fact]
    public async Task RunCommand_refuses_with_WorkflowLockedException_and_creates_no_worktree_when_lock_is_held()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-worktree-locked-{Guid.NewGuid():N}");
        var repository = Path.Combine(testRoot, "repo");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            await SetupGitRepositoryAsync(repository, "notes.txt", "locked-test-content", "main");
            var workflowFilePath = await WriteApprovalGateWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteWorktreeBindingsAsync(testRoot, repository, "main");

            Directory.CreateDirectory(roomDirectory);
            using var heldByAnotherInstance = Baton.Concurrency.ConcurrencyGuard.Acquire(roomDirectory);

            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);
            await Assert.ThrowsAsync<Baton.Concurrency.WorkflowLockedException>(() =>
                RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken));

            var worktreePath = Path.Combine(roomDirectory, WorktreeWorkspaces.WorkspacesDirectoryName, "b");
            Assert.False(Directory.Exists(worktreePath), "Worktree should NOT be created when ConcurrencyGuard is held");
        }
        finally
        {
            ForceDeleteDirectory(testRoot);
        }
    }

    [Fact]
    public async Task ResumeCommand_reuses_the_kept_dirty_worktree_a_prior_run_left_behind_rather_than_provisioning_fresh()
    {
        // Issue #1359 F1: baton resume must continue in the EXACT workspace the execution being
        // resumed ran in. A worktree with uncommitted changes is KEPT (not torn down) on Terminal
        // (WorktreeProvisioner.Teardown) -- exactly the population this test exercises: it plants a
        // marker file directly in the worktree's own cwd (never committed, so the tree is left
        // dirty), then has the RESUMED invocation read that same marker back out. That only
        // succeeds if baton resume is running in the SAME directory, not a fresh `git worktree add`.
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-worktree-resume-reuse-{Guid.NewGuid():N}");
        var repository = Path.Combine(testRoot, "repo");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            await SetupGitRepositoryAsync(repository, "notes.txt", "unused", "review-target");

            var workflowFilePath = await WriteSingleStepWorkflowAsync(testRoot);
            const string firstCommand = "echo marker-from-first-run>marker.txt & echo out_b>%BATON_OUTPUT_DIR%\\output_b";
            var bindingsFilePath = await WriteWorktreeResumeBindingsAsync(
                testRoot, repository, "review-target", sessionId: "sess-resume-wt", command: firstCommand);

            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);
            var runResult = await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(WorkflowStatus.Terminal, runResult.State.Status);

            var worktreePath = Path.Combine(roomDirectory, WorktreeWorkspaces.WorkspacesDirectoryName, "b");
            Assert.True(Directory.Exists(worktreePath), "a worktree left dirty by its worker must be kept, not torn down");
            Assert.Contains(runResult.WorktreeTeardowns, t => t.Outcome == WorktreeTeardownOutcome.KeptUncommitted);

            var firstExecutionId = runResult.State.Steps.Single().LatestExecutionId!.Value;

            const string resumeCommand = "type marker.txt>%BATON_OUTPUT_DIR%\\output_b";
            var resumeOptions = new ResumeOptions(roomDirectory, "b", resumeCommand, null, bindingsFilePath);
            var resumeResult = await ResumeCommand.ExecuteAsync(resumeOptions, Adapters, TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, resumeResult.State.Status);
            var resumedStep = resumeResult.State.Steps.Single();
            Assert.Equal(StepStatus.Succeeded, resumedStep.Status);
            Assert.Equal(firstExecutionId, resumedStep.LinkedFromExecutionId);

            var outputPath = Path.Combine(
                roomDirectory, "artifacts", $"execution_{resumedStep.LatestExecutionId!.Value}", "output_b");
            Assert.Equal(
                "marker-from-first-run", (await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken)).Trim());
        }
        finally
        {
            ForceDeleteDirectory(testRoot);
        }
    }

    [Fact]
    public async Task ResumeCommand_refuses_with_a_Try_line_naming_the_path_when_the_prior_worktree_is_gone()
    {
        // Issue #1359 F1's refusal half: a worktree with NO uncommitted changes IS torn down on
        // Terminal, so the room's own bindings still declare a worktree for a directory that no
        // longer exists. baton resume must refuse rather than silently `git worktree add` a fresh one.
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-worktree-resume-gone-{Guid.NewGuid():N}");
        var repository = Path.Combine(testRoot, "repo");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            await SetupGitRepositoryAsync(repository, "notes.txt", "from-worktree-repo", "review-target");

            var workflowFilePath = await WriteSingleStepWorkflowAsync(testRoot);
            const string cleanCommand = "type notes.txt>%BATON_OUTPUT_DIR%\\output_b";
            var bindingsFilePath = await WriteWorktreeResumeBindingsAsync(
                testRoot, repository, "review-target", sessionId: "sess-resume-gone", command: cleanCommand);

            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);
            var runResult = await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(WorkflowStatus.Terminal, runResult.State.Status);

            var worktreePath = Path.Combine(roomDirectory, WorktreeWorkspaces.WorkspacesDirectoryName, "b");
            Assert.False(Directory.Exists(worktreePath), "a clean worktree must be torn down on Terminal");

            var resumeOptions = new ResumeOptions(roomDirectory, "b", "continue please", null, bindingsFilePath);
            var thrown = await Assert.ThrowsAsync<InvalidResumeException>(
                () => ResumeCommand.ExecuteAsync(resumeOptions, Adapters, TestContext.Current.CancellationToken));

            Assert.Contains(worktreePath, thrown.Message, StringComparison.Ordinal);
            Assert.NotNull(thrown.TryInvocation);

            // And nothing was re-provisioned as a side effect of the refusal.
            Assert.False(Directory.Exists(worktreePath), "a refused resume must not conjure a fresh worktree");
        }
        finally
        {
            ForceDeleteDirectory(testRoot);
        }
    }

    private static async Task<string> WriteWorktreeResumeBindingsAsync(
        string directory, string repository, string reference, string sessionId, string command)
    {
        Directory.CreateDirectory(directory);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["b"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("b", [], [new ProducedOutput("output_b")], []),
                command, TimeSpan.FromSeconds(30), // wait-ok: test config timeout
                Worktree: new WorktreeWorkspace(repository, reference), SessionId: sessionId),
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }

    private static async Task SetupGitRepositoryAsync(string repository, string filename, string content, string branchName)
    {
        Directory.CreateDirectory(repository);
        await File.WriteAllTextAsync(Path.Combine(repository, filename), content, TestContext.Current.CancellationToken);
        await RunGitAsync(repository, "init");
        await RunGitAsync(repository, "add", filename);
        await RunGitAsync(repository, "-c", "user.email=test@example.com", "-c", "user.name=Test", "commit", "-m", "seed");
        await RunGitAsync(repository, "branch", branchName);
    }

    private static async Task<string> WriteSingleStepWorkflowAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("single-step"),
            1,
            [new WorkflowStepDefinition(new StepId("b"), "b", [], ["output_b"], [], new RetryPolicy(1))]);

        var path = Path.Combine(directory, "workflow.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(definition));
        return path;
    }

    private static async Task<string> WriteApprovalGateWorkflowAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("approval-gate"),
            1,
            [
                new WorkflowStepDefinition(
                    new StepId("a"), "a", [], ["output_a"], [], new RetryPolicy(1), new PausePoint([])),
                new WorkflowStepDefinition(
                    new StepId("b"), "b", ["output_a"], ["output_b"], [new StepId("a")], new RetryPolicy(1)),
            ]);

        var path = Path.Combine(directory, "workflow.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(definition));
        return path;
    }

    private static async Task<string> WriteTwoGateWorkflowAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("two-gate"),
            1,
            [
                new WorkflowStepDefinition(
                    new StepId("a"), "a", [], ["output_a"], [], new RetryPolicy(1), new PausePoint([])),
                new WorkflowStepDefinition(
                    new StepId("b"), "b", ["output_a"], ["output_b"], [new StepId("a")], new RetryPolicy(1), new PausePoint([])),
                new WorkflowStepDefinition(
                    new StepId("c"), "c", ["output_b"], ["output_c"], [new StepId("b")], new RetryPolicy(1)),
            ]);

        var path = Path.Combine(directory, "workflow.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(definition));
        return path;
    }

    private static async Task<string> WriteWorktreeBindingsAsyncForSingleStep(string directory, string repository, string reference)
    {
        Directory.CreateDirectory(directory);
        const string commandB = "type notes.txt>%BATON_OUTPUT_DIR%\\output_b";

        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["b"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("b", [], [new ProducedOutput("output_b")], []),
                commandB, TimeSpan.FromSeconds(30), // wait-ok: test config timeout
                Worktree: new WorktreeWorkspace(repository, reference)),
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }

    private static async Task<string> WriteWorktreeBindingsAsync(string directory, string repository, string reference)
    {
        Directory.CreateDirectory(directory);
        const string commandA = "echo out_a>%BATON_OUTPUT_DIR%\\output_a";
        const string commandB = "type notes.txt>%BATON_OUTPUT_DIR%\\output_b";

        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["a"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("a", [], [new ProducedOutput("output_a")], []),
                commandA, TimeSpan.FromSeconds(30)), // wait-ok: test config timeout
            ["b"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("b", ["output_a"], [new ProducedOutput("output_b")], []),
                commandB, TimeSpan.FromSeconds(30), // wait-ok: test config timeout
                Worktree: new WorktreeWorkspace(repository, reference)),
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }

    private static async Task<string> WriteWorktreeBindingsAsyncForTwoGate(string directory, string repository, string reference)
    {
        Directory.CreateDirectory(directory);
        const string commandA = "echo out_a>%BATON_OUTPUT_DIR%\\output_a";
        const string commandB = "echo out_b>%BATON_OUTPUT_DIR%\\output_b";
        const string commandC = "type notes.txt>%BATON_OUTPUT_DIR%\\output_c";

        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["a"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("a", [], [new ProducedOutput("output_a")], []),
                commandA, TimeSpan.FromSeconds(30)), // wait-ok: test config timeout
            ["b"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("b", ["output_a"], [new ProducedOutput("output_b")], []),
                commandB, TimeSpan.FromSeconds(30), // wait-ok: test config timeout
                Worktree: new WorktreeWorkspace(repository, reference)),
            ["c"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("c", ["output_b"], [new ProducedOutput("output_c")], []),
                commandC, TimeSpan.FromSeconds(30)), // wait-ok: test config timeout
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }

    private static async Task<string> WritePlainBindingsAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        const string commandA = "echo out_a>%BATON_OUTPUT_DIR%\\output_a";
        const string commandB = "echo out_b>%BATON_OUTPUT_DIR%\\output_b";

        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["a"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("a", [], [new ProducedOutput("output_a")], []),
                commandA, TimeSpan.FromSeconds(30)), // wait-ok: test config timeout
            ["b"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("b", ["output_a"], [new ProducedOutput("output_b")], []),
                commandB, TimeSpan.FromSeconds(30)), // wait-ok: test config timeout
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }

    private static async Task<string> WriteFailingFirstStepWorkflowAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("failing-first-step"),
            1,
            [
                new WorkflowStepDefinition(
                    new StepId("a"), "a", [], ["output_a"], [], new RetryPolicy(2)),
                new WorkflowStepDefinition(
                    new StepId("b"), "b", ["output_a"], ["output_b"], [new StepId("a")], new RetryPolicy(2)),
            ]);

        var path = Path.Combine(directory, "workflow_failing.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(definition));
        return path;
    }

    private static async Task<string> WriteFailingWorktreeBindingsAsync(string directory, string repository, string reference)
    {
        Directory.CreateDirectory(directory);
        const string commandA = "cmd /c exit 1";
        const string commandB = "type notes.txt>%BATON_OUTPUT_DIR%\\output_b";

        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["a"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("a", [], [new ProducedOutput("output_a")], []),
                commandA, TimeSpan.FromSeconds(30)), // wait-ok: test config timeout
            ["b"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("b", ["output_a"], [new ProducedOutput("output_b")], []),
                commandB, TimeSpan.FromSeconds(30), // wait-ok: test config timeout
                Worktree: new WorktreeWorkspace(repository, reference)),
        };

        var path = Path.Combine(directory, "bindings_failing.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }

    private static async Task RunGitAsync(string workingDirectory, params string[] args)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)!;
        var (_, stderr) = await BoundedProcessWait.RunToExitAsync(process, TimeSpan.FromSeconds(30));
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stderr}");
    }

    private static void ForceDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        DirectoryCleanup.DeleteRecursively(path);
    }
}
