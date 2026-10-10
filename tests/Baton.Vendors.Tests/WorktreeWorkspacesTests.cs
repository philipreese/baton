using Baton.Vendors;
using Baton.Concurrency;
using Baton.Domain;
using Baton.Workspaces;
using Baton.Tests.Shared;

namespace Baton.Vendors.Tests;

/// <summary>
/// The pre-dispatch pass (#669) that rewrites a declared worktree into a WorkingDirectory. These cover
/// the orchestration — the refusals, the passthrough, the rewrite, and resume reuse — without touching
/// git: a spec is refused before any git call, and the reuse path skips git when the tree already
/// exists. Actually adding a worktree is the primitive's own test.
/// </summary>
public sealed class WorktreeWorkspacesTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "baton-wtws-" + Guid.NewGuid().ToString("N"));

    public WorktreeWorkspacesTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Provision_refuses_with_WorkflowLockedException_when_task_ConcurrencyGuard_is_held()
    {
        using var heldByAnotherInstance = ConcurrencyGuard.Acquire(_root);
        var bindings = Bindings(("w", Entry(worktree: new WorktreeWorkspace(_root, "main"))));

        Assert.Throws<WorkflowLockedException>(() => WorktreeWorkspaces.Provision(bindings, _root));

        var worktreePath = Path.Combine(_root, WorktreeWorkspaces.WorkspacesDirectoryName, "w");
        Assert.False(Directory.Exists(worktreePath));
    }

    [Fact]
    public void ProvisionLazily_refuses_with_WorkflowLockedException_when_task_ConcurrencyGuard_is_held()
    {
        using var heldByAnotherInstance = ConcurrencyGuard.Acquire(_root);
        var bindings = Bindings(("w", Entry(worktree: new WorktreeWorkspace(_root, "main"))));

        Assert.Throws<WorkflowLockedException>(() => WorktreeWorkspaces.ProvisionLazily(bindings, _root));

        var worktreePath = Path.Combine(_root, WorktreeWorkspaces.WorkspacesDirectoryName, "w");
        Assert.False(Directory.Exists(worktreePath));
    }

    [Fact]
    public void Provision_leaves_a_binding_with_no_worktree_untouched()
    {
        var bindings = Bindings(("w", Entry(workingDirectory: "C:/somewhere")));

        var (result, provisioned) = WorktreeWorkspaces.Provision(bindings, _root);

        Assert.Same(bindings, result);
        Assert.Empty(provisioned);
    }

    /// <summary>
    /// #1646: RunWaitEndToEndTests' <c>decide</c> call raced a live <c>baton run --wait</c> pump's
    /// flow-lock release even though its bindings declare no worktree at all — the ordinary shell-worker
    /// shape, not the rare one. Pins the root cause: with nothing to provision, the walk must never
    /// touch the room's flow lock, so a live holder (simulated here directly, not by racing a real pump)
    /// cannot refuse it.
    /// </summary>
    [Fact]
    public void Provision_does_not_contend_the_flow_lock_when_no_binding_declares_a_worktree()
    {
        using var heldByALivePump = ConcurrencyGuard.Acquire(_root, "baton run pump");
        var bindings = Bindings(("w", Entry(workingDirectory: "C:/somewhere")));

        var (result, provisioned) = WorktreeWorkspaces.Provision(bindings, _root);

        Assert.Same(bindings, result);
        Assert.Empty(provisioned);
    }

    /// <summary>
    /// #1646: for the rarer case a binding DOES declare a worktree, the walk must still absorb a
    /// held-then-released flow lock rather than fail fast — the same "routine overlap" shape
    /// <see cref="ConcurrencyGuard.AcquireWithin"/> exists for. Deterministically injects the
    /// interleaving RunWaitEndToEndTests hit under CI load only intermittently: a holder that
    /// releases shortly after this call starts waiting, well inside the contention budget.
    /// </summary>
    [Fact]
    public async Task Provision_absorbs_a_flow_lock_released_shortly_after_it_starts_waiting()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var guard = ConcurrencyGuard.Acquire(_root, "baton run pump");
        // wait-ok: in-process release delay simulating the pump's brief post-Paused lock tail, not an external wait.
        var releaseAfterDelay = Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken)
            .ContinueWith(_ => guard.Dispose(), cancellationToken);

        const string worker = "reviewer";
        var expected = Path.Combine(_root, WorktreeWorkspaces.WorkspacesDirectoryName, worker);
        Directory.CreateDirectory(expected);
        var bindings = Bindings((worker, Entry(worktree: new WorktreeWorkspace(_root, "review-target"))));

        var (result, provisioned) = await Task.Run(
            () => WorktreeWorkspaces.Provision(bindings, _root), cancellationToken);
        await releaseAfterDelay;

        Assert.Equal(expected, result[worker].WorkingDirectory);
        Assert.Equal(expected, Assert.Single(provisioned).WorktreePath);
    }

    [Fact]
    public void Provision_refuses_a_binding_that_sets_both_a_working_directory_and_a_worktree()
    {
        var bindings = Bindings(("w",
            Entry(workingDirectory: "C:/somewhere", worktree: new WorktreeWorkspace(_root, "main"))));

        var ex = Assert.Throws<InvalidWorkspaceSpecException>(() => WorktreeWorkspaces.Provision(bindings, _root));
        Assert.Contains("exactly one place", ex.Message);
    }

    [Fact]
    public void Provision_refuses_a_worktree_with_a_relative_repository_before_touching_git()
    {
        var bindings = Bindings(("w", Entry(worktree: new WorktreeWorkspace("relative/repo", "main"))));

        Assert.Throws<InvalidWorkspaceSpecException>(() => WorktreeWorkspaces.Provision(bindings, _root));
    }

    [Fact]
    public void Provision_rewrites_WorkingDirectory_to_the_worktree_and_lists_it_for_teardown()
    {
        const string worker = "reviewer";
        // An already-present tree stands in for the resume case, so the pass reuses it and skips git.
        var expected = Path.Combine(_root, WorktreeWorkspaces.WorkspacesDirectoryName, worker);
        Directory.CreateDirectory(expected);
        var bindings = Bindings((worker, Entry(worktree: new WorktreeWorkspace(_root, "review-target"))));

        var (result, provisioned) = WorktreeWorkspaces.Provision(bindings, _root);

        Assert.Equal(expected, result[worker].WorkingDirectory);
        Assert.Null(result[worker].Worktree);
        Assert.Equal(expected, Assert.Single(provisioned).WorktreePath);
        Assert.Equal(_root, Assert.Single(provisioned).Repository);
    }

    [Fact]
    public void ProvisionLazily_skips_unprovisionable_entry_leaving_binding_untouched()
    {
        var bindings = Bindings(("bad", Entry(worktree: new WorktreeWorkspace("relative/repo", "main"))));

        var (result, provisioned, skipped) = WorktreeWorkspaces.ProvisionLazily(bindings, _root);

        Assert.Equal("relative/repo", result["bad"].Worktree?.Repository);
        Assert.Null(result["bad"].WorkingDirectory);
        Assert.False(result["bad"].IsWorktree);
        Assert.Empty(provisioned);
        var item = Assert.Single(skipped);
        Assert.Equal("bad", item.WorkerName);
        Assert.IsType<InvalidWorkspaceSpecException>(item.Exception);
    }

    /// <summary>
    /// P2 (#1664 third re-review): N2's actual fix was that a fresh provision now stamps
    /// <see cref="WorkerBindingConfigEntry.WorktreeBaseSha"/> rather than nulling it in the same
    /// expression that sets <see cref="WorkerBindingConfigEntry.IsWorktree"/> — nothing in the suite
    /// asserted that until now. Reverting the <c>WorktreeBaseSha = baseSha</c> assignment at
    /// <c>WorktreeWorkspaces.cs:196</c> turns this red (the property stays null) with the rest of the
    /// suite green.
    /// </summary>
    [Fact]
    public void Provision_stamps_WorktreeBaseSha_with_the_real_resolved_base_commit()
    {
        var sourceRepo = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceRepo);
        InitGitRepository(sourceRepo);
        var expectedBaseSha = WorktreeProvisioner.ResolveBaseCommit(sourceRepo, "HEAD");
        Assert.NotNull(expectedBaseSha);

        const string worker = "reviewer";
        var bindings = Bindings((worker, Entry(worktree: new WorktreeWorkspace(sourceRepo, "HEAD"))));

        var (result, provisioned) = WorktreeWorkspaces.Provision(bindings, _root);

        Assert.Equal(expectedBaseSha, result[worker].WorktreeBaseSha);
        Assert.True(result[worker].IsWorktree);
        Assert.Single(provisioned);
    }

    /// <summary>P2: the lazy/skip-capable walk shares the same stamping — same fix, same assertion.</summary>
    [Fact]
    public void ProvisionLazily_stamps_WorktreeBaseSha_with_the_real_resolved_base_commit()
    {
        var sourceRepo = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceRepo);
        InitGitRepository(sourceRepo);
        var expectedBaseSha = WorktreeProvisioner.ResolveBaseCommit(sourceRepo, "HEAD");
        Assert.NotNull(expectedBaseSha);

        const string worker = "reviewer";
        var bindings = Bindings((worker, Entry(worktree: new WorktreeWorkspace(sourceRepo, "HEAD"))));

        var (result, provisioned, skipped) = WorktreeWorkspaces.ProvisionLazily(bindings, _root);

        Assert.Equal(expectedBaseSha, result[worker].WorktreeBaseSha);
        Assert.True(result[worker].IsWorktree);
        Assert.Single(provisioned);
        Assert.Empty(skipped);
    }

    /// <summary>
    /// P2: the resume path's own stamping (<c>WorktreeWorkspaces.cs:120</c>) — a separate assignment
    /// from the fresh-provision one above, so a regression in one does not imply a regression in the
    /// other. Reverting just this site's <c>WorktreeBaseSha = baseSha</c> turns this red alone.
    /// </summary>
    [Fact]
    public void ReuseForResume_stamps_WorktreeBaseSha_with_the_real_resolved_base_commit()
    {
        var sourceRepo = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceRepo);
        InitGitRepository(sourceRepo);
        var expectedBaseSha = WorktreeProvisioner.ResolveBaseCommit(sourceRepo, "HEAD");
        Assert.NotNull(expectedBaseSha);

        const string worker = "reviewer";
        var worktreePath = Path.Combine(_root, WorktreeWorkspaces.WorkspacesDirectoryName, worker);
        WorktreeProvisioner.Provision(worktreePath, sourceRepo, "HEAD");
        var entry = Entry(worktree: new WorktreeWorkspace(sourceRepo, "HEAD"));

        var resumed = WorktreeWorkspaces.ReuseForResume(entry, worker, _root);

        Assert.Equal(expectedBaseSha, resumed.WorktreeBaseSha);
        Assert.True(resumed.IsWorktree);
        Assert.Equal(worktreePath, resumed.WorkingDirectory);
    }

    /// <summary>
    /// #1166 review finding A: <see cref="WorkerBindingConfigEntry.WorktreeSourceRepository"/> must be
    /// stamped the same way <see cref="WorkerBindingConfigEntry.WorktreeBaseSha"/> is, in the same
    /// expression -- <see cref="ProjectCeilingGate"/> keys the project ceiling on it. Reverting the
    /// <c>WorktreeSourceRepository = spec.Repository</c> assignment turns this red alone.
    /// </summary>
    [Fact]
    public void Provision_stamps_WorktreeSourceRepository_with_the_declared_repository()
    {
        var sourceRepo = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceRepo);
        InitGitRepository(sourceRepo);

        const string worker = "reviewer";
        var bindings = Bindings((worker, Entry(worktree: new WorktreeWorkspace(sourceRepo, "HEAD"))));

        var (result, _) = WorktreeWorkspaces.Provision(bindings, _root);

        Assert.Equal(sourceRepo, result[worker].WorktreeSourceRepository);
    }

    /// <summary>P2-style mirror: the lazy walk shares the same stamping.</summary>
    [Fact]
    public void ProvisionLazily_stamps_WorktreeSourceRepository_with_the_declared_repository()
    {
        var sourceRepo = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceRepo);
        InitGitRepository(sourceRepo);

        const string worker = "reviewer";
        var bindings = Bindings((worker, Entry(worktree: new WorktreeWorkspace(sourceRepo, "HEAD"))));

        var (result, _, skipped) = WorktreeWorkspaces.ProvisionLazily(bindings, _root);

        Assert.Equal(sourceRepo, result[worker].WorktreeSourceRepository);
        Assert.Empty(skipped);
    }

    /// <summary>The resume path's own separate stamping site.</summary>
    [Fact]
    public void ReuseForResume_stamps_WorktreeSourceRepository_with_the_declared_repository()
    {
        var sourceRepo = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceRepo);
        InitGitRepository(sourceRepo);

        const string worker = "reviewer";
        var worktreePath = Path.Combine(_root, WorktreeWorkspaces.WorkspacesDirectoryName, worker);
        WorktreeProvisioner.Provision(worktreePath, sourceRepo, "HEAD");
        var entry = Entry(worktree: new WorktreeWorkspace(sourceRepo, "HEAD"));

        var resumed = WorktreeWorkspaces.ReuseForResume(entry, worker, _root);

        Assert.Equal(sourceRepo, resumed.WorktreeSourceRepository);
    }

    private static void InitGitRepository(string path)
    {
        RunGitProcess(path, "init");
        RunGitProcess(path, "config", "user.name", "Test");
        RunGitProcess(path, "config", "user.email", "test@test.com");
        File.WriteAllText(Path.Combine(path, "README.md"), "init");
        RunGitProcess(path, "add", ".");
        RunGitProcess(path, "commit", "-m", "initial");
    }

    private static void RunGitProcess(string cwd, params string[] args)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }
        using var proc = global::Baton.Core.ProcessLaunch.Start(startInfo);
        proc?.WaitForExit();
    }

    private static Dictionary<string, WorkerBindingConfigEntry> Bindings(
        params (string Name, WorkerBindingConfigEntry Entry)[] entries)
    {
        var dict = new Dictionary<string, WorkerBindingConfigEntry>(StringComparer.Ordinal);
        foreach (var (name, entry) in entries)
        {
            dict[name] = entry;
        }

        return dict;
    }

    private static WorkerBindingConfigEntry Entry(string? workingDirectory = null, WorktreeWorkspace? worktree = null) =>
        new(
            Adapter: "claude",
            Contract: new WorkerContract("w", [], [], []),
            PromptTemplate: "do the thing",
            Timeout: TimeSpan.FromMinutes(5),
            WorkingDirectory: workingDirectory,
            Worktree: worktree);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            DirectoryCleanup.DeleteRecursively(_root);
        }
    }
}
