using System.Diagnostics;
using Baton.Workspaces;
using Baton.Tests.Shared;

namespace Baton.Tests.Workspaces;

/// <summary>
/// Covers the engine half of #669: standing a worker up in an isolated worktree, and the three honest
/// teardown outcomes. Uses a real on-disk git repository per test — the provisioner shells out to git,
/// so a fake would not discriminate.
/// </summary>
public sealed class WorktreeProvisionerTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "baton-worktree-" + Guid.NewGuid().ToString("N"));

    public WorktreeProvisionerTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ValidateSpec_refuses_a_relative_repository_path()
    {
        var ex = Assert.Throws<InvalidWorkspaceSpecException>(
            () => WorktreeProvisioner.ValidateSpec("some/relative/repo", "main"));
        Assert.Contains("absolute", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateSpec_refuses_an_empty_ref()
    {
        var absolute = Path.Combine(_root, "repo");
        Assert.Throws<InvalidWorkspaceSpecException>(
            () => WorktreeProvisioner.ValidateSpec(absolute, "  "));
    }

    [Fact]
    public void ValidateSpec_accepts_an_absolute_repository_and_a_ref()
    {
        // No throw: the happy shape a real dispatch passes.
        WorktreeProvisioner.ValidateSpec(Path.Combine(_root, "repo"), "review-target");
    }

    [Fact]
    public void Provision_checks_out_the_requested_ref_into_a_new_worktree()
    {
        var (repo, reference) = CreateRepoWithBranch("committed.txt");
        var worktree = Path.Combine(NewDir("task"), "workspace");

        WorktreeProvisioner.Provision(worktree, repo, reference);

        Assert.True(Directory.Exists(worktree));
        Assert.True(File.Exists(Path.Combine(worktree, "committed.txt")),
            "the ref's committed file should be checked out into the worktree");
    }

    [Fact]
    public void Provision_throws_a_typed_error_when_the_ref_does_not_exist()
    {
        var (repo, _) = CreateRepoWithBranch("committed.txt");
        var worktree = Path.Combine(NewDir("task"), "workspace");

        Assert.Throws<WorktreeProvisioningException>(
            () => WorktreeProvisioner.Provision(worktree, repo, "no-such-ref"));
    }

    [Fact]
    public void Provision_is_idempotent_when_called_again_for_the_same_worktree_repo_and_ref()
    {
        var (repo, reference) = CreateRepoWithBranch("committed.txt");
        var worktree = Path.Combine(NewDir("task"), "workspace");

        // First call provisions the worktree
        WorktreeProvisioner.Provision(worktree, repo, reference);
        Assert.True(Directory.Exists(worktree));

        // Second call for the exact same worktree/repo/ref simulates winning the race: must not throw
        WorktreeProvisioner.Provision(worktree, repo, reference);
        Assert.True(Directory.Exists(worktree));
        Assert.True(File.Exists(Path.Combine(worktree, "committed.txt")));
    }

    [Fact]
    public void Provision_throws_when_worktree_path_is_occupied_by_a_different_ref()
    {
        var (repo, reference) = CreateRepoWithBranch("committed.txt");
        // Create another ref pointing to a different commit
        File.WriteAllText(Path.Combine(repo, "second.txt"), "second content");
        RunGit(repo, "add", ".");
        RunGit(repo, "commit", "-m", "second commit");
        RunGit(repo, "branch", "other-ref");

        var worktree = Path.Combine(NewDir("task"), "workspace");

        // Provision for reference ("review-target") first
        WorktreeProvisioner.Provision(worktree, repo, reference);

        // Attempting to provision the same worktree path for a DIFFERENT ref ("other-ref") must throw
        Assert.Throws<WorktreeProvisioningException>(
            () => WorktreeProvisioner.Provision(worktree, repo, "other-ref"));
    }

    [Fact]
    public void Provision_throws_when_path_is_occupied_by_unrelated_directory()
    {
        var (repo, reference) = CreateRepoWithBranch("committed.txt");
        var worktree = Path.Combine(NewDir("task"), "workspace");
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, "unrelated.txt"), "data");

        Assert.Throws<WorktreeProvisioningException>(
            () => WorktreeProvisioner.Provision(worktree, repo, reference));
    }

    [Fact]
    public void Teardown_removes_a_clean_worktree()
    {
        var (repo, reference) = CreateRepoWithBranch("committed.txt");
        var worktree = Path.Combine(NewDir("task"), "workspace");
        WorktreeProvisioner.Provision(worktree, repo, reference);

        var result = WorktreeProvisioner.Teardown(repo, worktree);

        Assert.Equal(WorktreeTeardownOutcome.Removed, result.Outcome);
        Assert.False(Directory.Exists(worktree));
    }

    [Fact]
    public void Teardown_keeps_a_worktree_that_carries_uncommitted_changes()
    {
        var (repo, reference) = CreateRepoWithBranch("committed.txt");
        var worktree = Path.Combine(NewDir("task"), "workspace");
        WorktreeProvisioner.Provision(worktree, repo, reference);

        // A worker's not-yet-committed output. Discarding it is worse than leaving a directory behind.
        File.WriteAllText(Path.Combine(worktree, "worker-output.md"), "half-written result");

        var result = WorktreeProvisioner.Teardown(repo, worktree);

        Assert.Equal(WorktreeTeardownOutcome.KeptUncommitted, result.Outcome);
        Assert.True(Directory.Exists(worktree));
        Assert.True(File.Exists(Path.Combine(worktree, "worker-output.md")));
    }

    [Fact]
    public void Teardown_reports_rather_than_throwing_when_removal_is_blocked()
    {
        var (repo, reference) = CreateRepoWithBranch("committed.txt");
        var worktree = Path.Combine(NewDir("task"), "workspace");
        WorktreeProvisioner.Provision(worktree, repo, reference);

        // A locked worktree stands in for the real blocker (a live build process holding an output):
        // it makes `git worktree remove` fail deterministically on every platform, where a held file
        // handle only blocks removal on Windows. What is under test is the handling — report, don't
        // throw, so the completed task still terminates cleanly — not the specific cause.
        RunGit(repo, "worktree", "lock", worktree);

        var result = WorktreeProvisioner.Teardown(repo, worktree);

        Assert.Equal(WorktreeTeardownOutcome.RemovalBlocked, result.Outcome);
        Assert.True(Directory.Exists(worktree));
        Assert.NotNull(result.Detail);

        RunGit(repo, "worktree", "unlock", worktree); // so cleanup can delete the tree
    }

    [Fact]
    public void IsWorktree_returns_true_for_a_provisioned_worktree()
    {
        var (repo, reference) = CreateRepoWithBranch("committed.txt");
        var worktree = Path.Combine(NewDir("task"), "workspace");
        WorktreeProvisioner.Provision(worktree, repo, reference);

        Assert.True(WorktreeProvisioner.IsWorktree(worktree));
    }

    [Fact]
    public void IsWorktree_returns_false_for_a_main_repo()
    {
        var (repo, _) = CreateRepoWithBranch("committed.txt");

        Assert.False(WorktreeProvisioner.IsWorktree(repo));
    }

    [Fact]
    public void IsWorktree_returns_false_for_non_existent_or_non_git_path()
    {
        Assert.False(WorktreeProvisioner.IsWorktree(_root));
        Assert.False(WorktreeProvisioner.IsWorktree(Path.Combine(_root, "nonexistent")));
    }

    // --- fixture ---

    private string NewDir(string name)
    {
        var path = Path.Combine(_root, name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// A real git repository with one commit and a branch <c>review-target</c> that is not checked out
    /// in the main tree (so a worktree can take it). Returns the repo path and that ref name.
    /// </summary>
    private (string Repository, string Reference) CreateRepoWithBranch(string committedFileName)
    {
        var repo = NewDir("repo");
        RunGit(repo, "init");
        RunGit(repo, "config", "user.email", "test@example.com");
        RunGit(repo, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(repo, committedFileName), "committed content");
        RunGit(repo, "add", ".");
        RunGit(repo, "commit", "-m", "initial");
        RunGit(repo, "branch", "review-target");
        return (repo, "review-target");
    }

    private static string RunGitCapture(string workingDirectory, params string[] args)
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
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stderr.Result}");
        return stdout.Result;
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

    public void Dispose()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        // git's committed object files are read-only by design, which Windows' Directory.Delete refuses
        // to remove; clear the attribute first so cleanup succeeds on every OS.
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        DirectoryCleanup.DeleteRecursively(_root);
    }

    /// <summary>
    /// #1103: git prints macOS temp paths in their resolved <c>/private/...</c> spelling while
    /// callers hold the <c>/var/...</c> symlink spelling; equality must see through that or the
    /// idempotence check above can never match on macOS. Pure string logic, so this discriminates
    /// on every platform — it is the unit-level pin for what was then a macOS CI failure.
    /// </summary>
    [Theory]
    [InlineData("/private/var/folders/x/task/workspace", "/var/folders/x/task/workspace", true)]
    [InlineData("/private/tmp/task", "/tmp/task", true)]
    [InlineData("/var/folders/x/a", "/var/folders/x/b", false)]
    [InlineData("/private/var/folders/x/a", "/var/folders/y/a", false)]
    public void Normalization_sees_through_the_macos_private_symlink_spelling(string a, string b, bool expected)
    {
        // Targets the normalization itself rather than PathsEqual: PathsEqual runs GetFullPath
        // first, which on Windows would re-root these POSIX literals under the current drive and
        // turn this into a test of GetFullPath instead of the /private equivalence.
        var equal = string.Equals(
            WorktreeProvisioner.NormalizeForComparison(a),
            WorktreeProvisioner.NormalizeForComparison(b),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(expected, equal);
    }

    [Fact]
    public void IsWorkspaceUntouched_returns_true_for_a_freshly_provisioned_worktree()
    {
        var (repo, reference) = CreateRepoWithBranch("committed.txt");
        var worktree = Path.Combine(NewDir("task"), "workspace");

        WorktreeProvisioner.Provision(worktree, repo, reference);

        Assert.True(WorktreeProvisioner.IsWorkspaceUntouched(worktree));
    }

    [Fact]
    public void IsWorkspaceUntouched_returns_false_when_worktree_carries_uncommitted_changes()
    {
        var (repo, reference) = CreateRepoWithBranch("committed.txt");
        var worktree = Path.Combine(NewDir("task"), "workspace");

        WorktreeProvisioner.Provision(worktree, repo, reference);
        File.WriteAllText(Path.Combine(worktree, "dirty.txt"), "dirty content");

        Assert.False(WorktreeProvisioner.IsWorkspaceUntouched(worktree));
    }

    [Fact]
    public void IsWorkspaceUntouched_returns_false_when_worktree_carries_commits_over_base()
    {
        var (repo, reference) = CreateRepoWithBranch("committed.txt");
        var worktree = Path.Combine(NewDir("task"), "workspace");

        WorktreeProvisioner.Provision(worktree, repo, reference);
        File.WriteAllText(Path.Combine(worktree, "committed2.txt"), "more content");
        RunGit(worktree, "add", ".");
        RunGit(worktree, "commit", "-m", "worker commit");

        Assert.False(WorktreeProvisioner.IsWorkspaceUntouched(worktree));
    }

    [Fact]
    public void IsWorkspaceUntouched_returns_false_for_null_empty_or_nonexistent_directory()
    {
        Assert.False(WorktreeProvisioner.IsWorkspaceUntouched(null));
        Assert.False(WorktreeProvisioner.IsWorkspaceUntouched("   "));
        Assert.False(WorktreeProvisioner.IsWorkspaceUntouched(Path.Combine(_root, "nonexistent")));
        Assert.False(WorktreeProvisioner.IsWorkspaceUntouched(_root));
    }

    /// <summary>
    /// F2 (#1720 review): the tri-state probe's UNMEASURABLE arm — where negating the fail-closed
    /// <see cref="WorktreeProvisioner.IsWorkspaceUntouched"/> above fabricated
    /// <c>workspaceChanged: true</c>. Each of these returns false ("did not measure") and must leave
    /// the out-parameter alone rather than reporting a change.
    /// </summary>
    [Fact]
    public void TryReadWorkspaceChanged_reports_unmeasured_when_git_cannot_answer()
    {
        // Not a git checkout at all: `git status` exits non-zero.
        var plainDirectory = NewDir("not-a-repo");
        Assert.False(WorktreeProvisioner.TryReadWorkspaceChanged(plainDirectory, baseRef: null, out var changedInPlainDir));
        Assert.False(changedInPlainDir);

        // A real checkout whose branch has no @{upstream}: nothing to count HEAD against.
        var (repo, _) = CreateRepoWithBranch("committed.txt");
        Assert.False(WorktreeProvisioner.TryReadWorkspaceChanged(repo, baseRef: null, out var changedInRepo));
        Assert.False(changedInRepo);

        // Nothing to probe at all.
        Assert.False(WorktreeProvisioner.TryReadWorkspaceChanged(null, baseRef: null, out _));
        Assert.False(WorktreeProvisioner.TryReadWorkspaceChanged("   ", baseRef: null, out _));
        Assert.False(WorktreeProvisioner.TryReadWorkspaceChanged(
            Path.Combine(_root, "nonexistent"), baseRef: null, out _));
    }

    /// <summary>
    /// F2's polarity control, in both directions: the probe must still MEASURE where it can, and an
    /// uncommitted change is conclusive on its own even in the no-upstream checkout the arm above
    /// reports unmeasurable — otherwise "unmeasurable" would swallow the very signal #1390 wants.
    /// </summary>
    [Fact]
    public void TryReadWorkspaceChanged_measures_a_clean_and_a_dirty_workspace()
    {
        var (repo, reference) = CreateRepoWithBranch("committed.txt");
        var worktree = Path.Combine(NewDir("task"), "workspace");
        WorktreeProvisioner.Provision(worktree, repo, reference);

        Assert.True(WorktreeProvisioner.TryReadWorkspaceChanged(worktree, reference, out var cleanChanged));
        Assert.False(cleanChanged);

        File.WriteAllText(Path.Combine(worktree, "dirty.txt"), "dirty content");
        Assert.True(WorktreeProvisioner.TryReadWorkspaceChanged(worktree, reference, out var dirtyChanged));
        Assert.True(dirtyChanged);

        // Same conclusive read on the no-upstream plain checkout, where the commit probes cannot run.
        File.WriteAllText(Path.Combine(repo, "dirty.txt"), "dirty content");
        Assert.True(WorktreeProvisioner.TryReadWorkspaceChanged(repo, baseRef: null, out var dirtyPlainChanged));
        Assert.True(dirtyPlainChanged);
    }

    [Fact]
    public void TryReadWorkspaceChanged_measures_commits_over_base()
    {
        var (repo, reference) = CreateRepoWithBranch("committed.txt");
        var worktree = Path.Combine(NewDir("task"), "workspace");
        WorktreeProvisioner.Provision(worktree, repo, reference);

        File.WriteAllText(Path.Combine(worktree, "committed2.txt"), "more content");
        RunGit(worktree, "add", ".");
        RunGit(worktree, "commit", "-m", "worker commit");

        // baseRef left null, exactly as IsWorkspaceUntouched's own commits-over-base arm does: the
        // worktree has `review-target` itself checked out, so a commit moves that branch too and
        // `review-target..HEAD` counts zero. The reflog heuristic is the arm that sees this shape.
        Assert.True(WorktreeProvisioner.TryReadWorkspaceChanged(worktree, baseRef: null, out var changed));
        Assert.True(changed);

        // And the base-ref arm on a base that does NOT move with the worker's commit.
        var baseSha = RunGitCapture(repo, "rev-parse", "HEAD").Trim();
        Assert.True(WorktreeProvisioner.TryReadWorkspaceChanged(worktree, baseSha, out var changedAgainstSha));
        Assert.True(changedAgainstSha);
    }

    /// <summary>
    /// #1929 review HIGH: a dispatch's own skill projection lands untracked in the worker's working
    /// directory, and both readers must attribute it to AER rather than to the worker. Against a real
    /// git tree, in the exact shape the claude adapter produces
    /// (<c>&lt;workspace&gt;/.claude/skills/&lt;name&gt;/</c>).
    /// </summary>
    /// <remarks>
    /// Three arms, and the two controls are the point. WITHOUT the list both readers must answer
    /// "changed" — that is the defect, and an exclusion that passed only because the tree was already
    /// clean would fail here. WITH a non-projected untracked file present alongside they must answer
    /// "changed" again — an exclusion that suppressed everything (or that matched the collapsed
    /// <c>?? .claude/</c> line <c>--untracked-files=normal</c> used to emit, and so swallowed the whole
    /// directory) passes the first arm and fails this one.
    /// </remarks>
    [Fact]
    public void Both_workspace_readers_subtract_paths_AER_itself_placed()
    {
        // A provisioned worktree rather than the plain checkout: once the projection is subtracted the
        // status arm is silent, and TryReadWorkspaceChanged then falls through to its commit probes --
        // which report UNMEASURABLE on a plain checkout with no upstream, by its own design. The
        // measured `changed: false` this test is about only exists where those probes can answer.
        var (repo, reference) = CreateRepoWithBranch("committed.txt");
        var workspace = Path.Combine(NewDir("task"), "workspace");
        WorktreeProvisioner.Provision(workspace, repo, reference);
        var baseSha = RunGitCapture(repo, "rev-parse", "HEAD").Trim();

        var projectedDirectory = Path.Combine(workspace, ".claude", "skills", "audit-tool");
        Directory.CreateDirectory(projectedDirectory);
        var projected = new[]
        {
            Path.Combine(projectedDirectory, "SKILL.md"),
            Path.Combine(projectedDirectory, "reference", "notes.md"),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(projected[1])!);
        foreach (var path in projected)
        {
            File.WriteAllText(path, "projected content");
        }

        // Control 1: the same tree, read the way it was read before this fix.
        Assert.True(WorktreeProvisioner.TryReadWorkspaceChanged(workspace, baseSha, out var changedWithoutList));
        Assert.True(changedWithoutList);
        Assert.True(WorktreeProvisioner.ReadWorkspaceMutation(workspace, baseSha)!.Mutated);

        // The claim: AER's own copies are not the worker's work product.
        Assert.True(WorktreeProvisioner.TryReadWorkspaceChanged(workspace, baseSha, out var changed, projected));
        Assert.False(changed);

        var reading = WorktreeProvisioner.ReadWorkspaceMutation(workspace, baseSha, projected)!;
        Assert.False(reading.Mutated);
        Assert.Equal(0, reading.ChangedPathCount);

        // Control 2: one path AER did not place, alongside the ones it did.
        File.WriteAllText(Path.Combine(workspace, "worker-output.txt"), "the worker's own work");
        Assert.True(WorktreeProvisioner.TryReadWorkspaceChanged(workspace, baseSha, out var changedWithWorkerFile, projected));
        Assert.True(changedWithWorkerFile);

        var readingWithWorkerFile = WorktreeProvisioner.ReadWorkspaceMutation(workspace, baseSha, projected)!;
        Assert.True(readingWithWorkerFile.Mutated);
        Assert.Equal(1, readingWithWorkerFile.ChangedPathCount);
    }

    // #1373: ReadWorkspaceMutation, against real git trees for the reason this class's own summary
    // gives — the probe shells out, so a double would only re-state the assumption under test. The
    // hermetic half (which branch OutcomeClassifier takes GIVEN a reading) lives in
    // OutcomeClassifierTests, which injects one.

    [Fact]
    public void ReadWorkspaceMutation_returns_null_when_there_is_no_workspace_to_read()
    {
        // Null, not Unmeasurable: an execution with nowhere to leave work keeps its retry, while one
        // whose workspace cannot be read does not. Folding the two together would foreclose the first.
        Assert.Null(WorktreeProvisioner.ReadWorkspaceMutation(null, "HEAD"));
        Assert.Null(WorktreeProvisioner.ReadWorkspaceMutation("   ", "HEAD"));
        Assert.Null(WorktreeProvisioner.ReadWorkspaceMutation(Path.Combine(_root, "nonexistent"), "HEAD"));
    }

    [Fact]
    public void ReadWorkspaceMutation_reads_an_untouched_workspace_as_unmutated()
    {
        var (repo, reference) = CreateRepoWithBranch("committed.txt");
        var worktree = Path.Combine(NewDir("task"), "workspace");
        WorktreeProvisioner.Provision(worktree, repo, reference);
        var startSha = RunGitCapture(worktree, "rev-parse", "HEAD").Trim();

        var reading = WorktreeProvisioner.ReadWorkspaceMutation(worktree, startSha);

        // The discriminating control for every arm below: if this one read "mutated" too, the arms
        // that follow would pass against a probe that answers "mutated" unconditionally.
        Assert.NotNull(reading);
        Assert.True(reading.Measured);
        Assert.False(reading.Mutated);
        Assert.Equal(0, reading.ChangedPathCount);
        Assert.Equal(0, reading.NewCommitCount);
    }

    [Fact]
    public void ReadWorkspaceMutation_counts_uncommitted_and_untracked_paths()
    {
        var (repo, reference) = CreateRepoWithBranch("committed.txt");
        var worktree = Path.Combine(NewDir("task"), "workspace");
        WorktreeProvisioner.Provision(worktree, repo, reference);
        var startSha = RunGitCapture(worktree, "rev-parse", "HEAD").Trim();

        File.WriteAllText(Path.Combine(worktree, "committed.txt"), "edited by the worker");
        File.WriteAllText(Path.Combine(worktree, "brand-new.txt"), "never added");

        var reading = WorktreeProvisioner.ReadWorkspaceMutation(worktree, startSha);

        Assert.NotNull(reading);
        Assert.True(reading.Mutated);
        Assert.Equal(2, reading.ChangedPathCount);
        Assert.Equal(0, reading.NewCommitCount);
        Assert.Contains("2 changed/untracked path(s)", reading.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void ReadWorkspaceMutation_counts_commits_made_since_the_start_sha()
    {
        var (repo, reference) = CreateRepoWithBranch("committed.txt");
        var worktree = Path.Combine(NewDir("task"), "workspace");
        WorktreeProvisioner.Provision(worktree, repo, reference);
        var startSha = RunGitCapture(worktree, "rev-parse", "HEAD").Trim();

        File.WriteAllText(Path.Combine(worktree, "work.txt"), "the worker's finished work");
        RunGit(worktree, "add", ".");
        RunGit(worktree, "commit", "-m", "worker commit");

        var reading = WorktreeProvisioner.ReadWorkspaceMutation(worktree, startSha);

        // A committed work product leaves a CLEAN tree — the shape #1580/#1584 hit, and the one a
        // status-only probe would report as "nothing here, retry away".
        Assert.NotNull(reading);
        Assert.True(reading.Mutated);
        Assert.Equal(0, reading.ChangedPathCount);
        Assert.Equal(1, reading.NewCommitCount);
        Assert.Contains("1 new commit(s)", reading.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void ReadWorkspaceMutation_without_a_start_ref_reports_a_commit_without_counting_it()
    {
        var (repo, reference) = CreateRepoWithBranch("committed.txt");
        var worktree = Path.Combine(NewDir("task"), "workspace");
        WorktreeProvisioner.Provision(worktree, repo, reference);

        File.WriteAllText(Path.Combine(worktree, "work.txt"), "the worker's finished work");
        RunGit(worktree, "add", ".");
        RunGit(worktree, "commit", "-m", "worker commit");

        var reading = WorktreeProvisioner.ReadWorkspaceMutation(worktree, sinceRef: null);

        // The crash-recovery fallback: the reflog can say THAT a commit happened, never how many, so
        // the count stays null rather than being fabricated — while Mutated still reads true.
        Assert.NotNull(reading);
        Assert.True(reading.Measured);
        Assert.True(reading.Mutated);
        Assert.True(reading.HasNewCommits);
        Assert.Null(reading.NewCommitCount);
    }

    [Fact]
    public void ReadWorkspaceMutation_reads_a_non_git_directory_as_mutated()
    {
        var plainDirectory = NewDir("not-a-repo");
        File.WriteAllText(Path.Combine(plainDirectory, "some-work.txt"), "content");

        var reading = WorktreeProvisioner.ReadWorkspaceMutation(plainDirectory, "HEAD");

        // Fail closed: git cannot answer, so surviving work cannot be ruled out, so no blind retry.
        Assert.NotNull(reading);
        Assert.False(reading.Measured);
        Assert.True(reading.Mutated);
    }

    [Fact]
    public void ReadWorkspaceMutation_reads_an_unresolvable_start_ref_as_mutated()
    {
        var (repo, reference) = CreateRepoWithBranch("committed.txt");
        var worktree = Path.Combine(NewDir("task"), "workspace");
        WorktreeProvisioner.Provision(worktree, repo, reference);

        var reading = WorktreeProvisioner.ReadWorkspaceMutation(worktree, "0000000000000000000000000000000000000000");

        // Half a reading is not a reading: the tree is clean, but the commit half failed, so this must
        // not settle on "clean" — polarity against the untouched-workspace arm above, which is the
        // same tree with a resolvable ref.
        Assert.NotNull(reading);
        Assert.False(reading.Measured);
        Assert.True(reading.Mutated);
    }
}
