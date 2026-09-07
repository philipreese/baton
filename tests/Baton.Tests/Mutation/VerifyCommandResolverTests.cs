using System.Linq;
using Baton.Mutation;
using Baton.Tests.Shared;
using Baton.Tests.TestSupport;
using Xunit;

namespace Baton.Tests.Mutation;

/// <summary>
/// Coverage for <see cref="VerifyCommandResolver"/> (#1702) — see its own class doc for the resolution
/// order and spec/baton.md §3 for the contract. Pure/unit — no pump, no real dispatch.
/// </summary>
public sealed class VerifyCommandResolverTests
{
    [Fact]
    public void Resolve_returns_null_when_nothing_resolves()
    {
        var resolved = VerifyCommandResolver.Resolve(committedRepoDeclaration: null, overrideCommand: null, roleVerifyPixiTask: null);

        Assert.Null(resolved);
    }

    [Fact]
    public void Resolve_falls_back_to_the_role_default_when_no_override_or_repo_declaration()
    {
        var resolved = VerifyCommandResolver.Resolve(committedRepoDeclaration: null, overrideCommand: null, roleVerifyPixiTask: "gates-quiet");

        Assert.NotNull(resolved);
        Assert.Equal(VerifyCommandSource.RoleDefault, resolved!.Source);
        Assert.Equal("pixi", resolved.Program);
        Assert.Equal(["run", "gates-quiet"], resolved.Args);
        Assert.Equal("gates-quiet", resolved.Label);
    }

    [Fact]
    public void Resolve_repo_declaration_wins_over_the_role_default()
    {
        var resolved = VerifyCommandResolver.Resolve(
            "python -c \"import sys; sys.exit(0)\"", overrideCommand: null, roleVerifyPixiTask: "gates-quiet");

        Assert.NotNull(resolved);
        Assert.Equal(VerifyCommandSource.RepoDeclaration, resolved!.Source);
        Assert.Equal("python -c \"import sys; sys.exit(0)\"", resolved.Label);
    }

    [Fact]
    public void Resolve_override_wins_over_both_the_repo_declaration_and_the_role_default()
    {
        var resolved = VerifyCommandResolver.Resolve(
            "python -c \"import sys; sys.exit(0)\"",
            overrideCommand: "python -c \"import sys; sys.exit(1)\"",
            roleVerifyPixiTask: "gates-quiet");

        Assert.NotNull(resolved);
        Assert.Equal(VerifyCommandSource.Override, resolved!.Source);
        Assert.Equal("python -c \"import sys; sys.exit(1)\"", resolved.Label);
    }

    [Fact]
    public void Resolve_repo_declaration_still_applies_when_the_role_declares_no_default()
    {
        // Pins the arm-2 rule spec/baton.md §3 states as rewritten by #2029: a role that declares no
        // VerifyPixiTask of its own -- janitor's shape -- dispatched against a workspace that declares
        // .baton/verify still gets a verify step. Which roles reach this arm at all is
        // WorkerRole.VerifiesWorkspace's to say and is gated at MutationInterface's call site, not here:
        // Resolve stays role-independent, which is why the read-shaped roles are not this test's subject
        // any more.
        var resolved = VerifyCommandResolver.Resolve(
            "python -c \"import sys; sys.exit(0)\"", overrideCommand: null, roleVerifyPixiTask: null);

        Assert.NotNull(resolved);
        Assert.Equal(VerifyCommandSource.RepoDeclaration, resolved!.Source);
    }

    // ---- #1708 H1: the declaration comes from the COMMITTED tree, read BEFORE the worker runs ----

    [Fact]
    public async Task ReadCommittedRepoDeclarationAsync_reads_the_committed_declaration_skipping_blanks_and_comments()
    {
        var workspace = CreateTempWorkspace();
        try
        {
            TempGitRepository.InitWithEverythingCommitted(workspace);
            WriteRepoDeclaration(workspace, "\n  \n# a comment\n  python -c \"import sys; sys.exit(0)\"  \n");
            TempGitRepository.CommitAll(workspace, "declare verify");

            TempGitRepository.SetReviewedBaselineAtHead(workspace);

            var committed = await VerifyCommandResolver.ReadCommittedRepoDeclarationAsync(
                workspace, TestContext.Current.CancellationToken);

            Assert.Equal("python -c \"import sys; sys.exit(0)\"", committed.CommandLine);
            Assert.False(committed.Unreviewed);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(workspace);
        }
    }

    /// <summary>
    /// The self-verification hole (#1708 H1), red-first: the "worker" writes its own <c>.baton/verify</c>
    /// into the live workspace, and the engine must not read it. Discriminates the committed-tree read
    /// from a merely-earlier working-tree read — the file here is written into a workspace that ALREADY
    /// held a different committed declaration, so a working-tree read at any time returns the worker's
    /// line and only a committed read returns the repo's.
    /// </summary>
    [Fact]
    public async Task ReadCommittedRepoDeclarationAsync_ignores_a_working_tree_file_the_worker_wrote()
    {
        var workspace = CreateTempWorkspace();
        try
        {
            TempGitRepository.InitWithEverythingCommitted(workspace);
            WriteRepoDeclaration(workspace, "python -c \"import sys; sys.exit(1)\"");
            TempGitRepository.CommitAll(workspace, "declare verify");
            TempGitRepository.SetReviewedBaselineAtHead(workspace);

            // The worker, mid-execution, replaces it with a verifier that always passes.
            WriteRepoDeclaration(workspace, "cmd /c exit 0");

            var committed = await VerifyCommandResolver.ReadCommittedRepoDeclarationAsync(
                workspace, TestContext.Current.CancellationToken);

            Assert.Equal("python -c \"import sys; sys.exit(1)\"", committed.CommandLine);
            Assert.Equal("cmd /c exit 0", VerifyCommandResolver.ReadWorkingTreeRepoDeclaration(workspace));

            // ...and what actually runs is the committed line, never the worker's.
            var resolved = VerifyCommandResolver.Resolve(committed.CommandLine, overrideCommand: null, roleVerifyPixiTask: "gates-quiet");
            Assert.Equal("python -c \"import sys; sys.exit(1)\"", resolved!.Label);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(workspace);
        }
    }

    /// <summary>
    /// The same hole with nothing committed at all — a worker inventing the file from scratch. Fails
    /// CLOSED: no committed declaration means the role default runs, not the worker's line.
    /// </summary>
    [Fact]
    public async Task ReadCommittedRepoDeclarationAsync_returns_null_for_an_uncommitted_declaration()
    {
        var workspace = CreateTempWorkspace();
        try
        {
            TempGitRepository.InitWithEverythingCommitted(workspace);
            TempGitRepository.SetReviewedBaselineAtHead(workspace);
            WriteRepoDeclaration(workspace, "cmd /c exit 0");

            var committed = await VerifyCommandResolver.ReadCommittedRepoDeclarationAsync(
                workspace, TestContext.Current.CancellationToken);

            Assert.Null(committed.CommandLine);
            Assert.False(committed.Unreviewed);

            var resolved = VerifyCommandResolver.Resolve(committed.CommandLine, overrideCommand: null, roleVerifyPixiTask: "gates-quiet");
            Assert.Equal(VerifyCommandSource.RoleDefault, resolved!.Source);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(workspace);
        }
    }

    /// <summary>
    /// The declaration is the DISPATCHED workspace's, not its repository root's. `git show HEAD:&lt;path&gt;`
    /// resolves against the repo root, so a monorepo package dispatched with <c>--workspace</c> would
    /// otherwise be graded by a root-level file it does not own — and the working-tree half of the
    /// drift comparison, which is workspace-relative, would be reading a different file entirely.
    /// </summary>
    [Fact]
    public async Task ReadCommittedRepoDeclarationAsync_reads_the_workspace_subdirectory_not_the_repository_root()
    {
        var repoRoot = CreateTempWorkspace();
        try
        {
            var package = Path.Combine(repoRoot, "packages", "thing");
            Directory.CreateDirectory(package);
            WriteRepoDeclaration(repoRoot, "cmd /c exit 0");
            TempGitRepository.InitWithEverythingCommitted(repoRoot);

            // The dispatched workspace declares nothing of its own, so it declares nothing at all --
            // never the root's line.
            Assert.Null((await VerifyCommandResolver.ReadCommittedRepoDeclarationAsync(
                package, TestContext.Current.CancellationToken)).CommandLine);

            // ...and when it does declare its own, that is the one read.
            WriteRepoDeclaration(package, "python -c \"import sys; sys.exit(1)\"");
            TempGitRepository.CommitAll(repoRoot, "declare package verify");
            TempGitRepository.SetReviewedBaselineAtHead(repoRoot);

            Assert.Equal(
                "python -c \"import sys; sys.exit(1)\"",
                (await VerifyCommandResolver.ReadCommittedRepoDeclarationAsync(
                    package, TestContext.Current.CancellationToken)).CommandLine);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(repoRoot);
        }
    }

    [Fact]
    public async Task ReadCommittedRepoDeclarationAsync_returns_null_outside_a_git_repository()
    {
        var workspace = CreateTempWorkspace();
        try
        {
            WriteRepoDeclaration(workspace, "cmd /c exit 0");

            Assert.Null((await VerifyCommandResolver.ReadCommittedRepoDeclarationAsync(
                workspace, TestContext.Current.CancellationToken)).CommandLine);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(workspace);
        }
    }

    [Fact]
    public async Task ReadCommittedRepoDeclarationAsync_returns_null_when_git_itself_cannot_spawn()
    {
        // Fails closed rather than throwing -- see RunGitAsync's own doc for why nothing on this path
        // may raise.
        var workspace = CreateTempWorkspace();
        try
        {
            Assert.Null((await VerifyCommandResolver.ReadCommittedRepoDeclarationAsync(
                workspace, TestContext.Current.CancellationToken, gitProgram: "this-is-not-a-real-git-binary-12345")).CommandLine);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(workspace);
        }
    }

    // ---- #1718: a stderr `warning:` from a git that still exits 0 must never be read as the declaration ----

    /// <summary>
    /// #1718, production path: real git can exit 0 while writing a <c>warning:</c> line to stderr — this
    /// fixture manufactures that deterministically (<see cref="TempGitRepository.TagHeadWithItsOwnSha"/>:
    /// tagging <c>HEAD</c> with its own full-sha makes the exact <c>git show &lt;rev&gt;:&lt;path&gt;</c>
    /// shape <see cref="VerifyCommandResolver.ReadCommittedRepoDeclarationAsync"/> runs print
    /// <c>advice.objectNameWarning</c>'s ambiguous-refname warning). Pins the #1708 L3 stdout-only
    /// hardening (spec/baton.md §3, "Verify command resolution": "stdout only, so a warning git writes
    /// to stderr can never be taken for the declaration's own first non-comment line") — the declaration
    /// returned must still be the blob's real first non-comment line, not the warning text.
    /// </summary>
    [Fact]
    public async Task ReadCommittedRepoDeclarationAsync_ignores_a_stderr_warning_from_a_git_that_exits_zero()
    {
        var workspace = CreateTempWorkspace();
        try
        {
            TempGitRepository.InitWithEverythingCommitted(workspace);
            WriteRepoDeclaration(workspace, "python -c \"import sys; sys.exit(1)\"");
            TempGitRepository.CommitAll(workspace, "declare verify");
            TempGitRepository.SetReviewedBaselineAtHead(workspace);

            // The reviewed baseline is HEAD, so ReadCommittedRepoDeclarationAsync's own merge-base read
            // resolves to this same sha, and its `git show` runs against exactly the ref this tags.
            TempGitRepository.TagHeadWithItsOwnSha(workspace);

            var committed = await VerifyCommandResolver.ReadCommittedRepoDeclarationAsync(
                workspace, TestContext.Current.CancellationToken);

            Assert.Equal("python -c \"import sys; sys.exit(1)\"", committed.CommandLine);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(workspace);
        }
    }

    /// <summary>
    /// #1718, RED-FIRST control arm: proves the fixture above really does produce stderr noise on the
    /// exact <c>git show</c> shape the resolver runs, so the assertion in the test above is not vacuous.
    /// Calls <see cref="VerifyRunner.CaptureAsync"/> directly with <c>stdoutOnly: false</c> — the
    /// combined stream must contain the warning text. (Confirmed separately, not re-asserted here: with
    /// <c>stdoutOnly</c> forced to <see langword="false"/> on the production path too, the test above
    /// fails because the declaration comes back polluted with the warning line instead of the command —
    /// see changes.md.)
    /// </summary>
    [Fact]
    public async Task CaptureAsync_without_stdoutOnly_interleaves_the_same_fixtures_stderr_warning()
    {
        var workspace = CreateTempWorkspace();
        try
        {
            TempGitRepository.InitWithEverythingCommitted(workspace);
            WriteRepoDeclaration(workspace, "python -c \"import sys; sys.exit(1)\"");
            TempGitRepository.CommitAll(workspace, "declare verify");
            var sha = TempGitRepository.TagHeadWithItsOwnSha(workspace);

            var (exitCode, combined) = await VerifyRunner.CaptureAsync(
                "git",
                ["--no-pager", "-c", "core.hooksPath=", "show", "--no-textconv", $"{sha}:./.baton/verify"],
                workspace,
                TestContext.Current.CancellationToken,
                stdoutOnly: false);

            Assert.Equal(0, exitCode);
            Assert.Contains("warning:", combined, StringComparison.Ordinal);
            Assert.Contains("ambiguous", combined, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(workspace);
        }
    }

    // ---- #1708 M1: the baseline is the merge-base with origin/main, not HEAD ----

    /// <summary>
    /// #1708 M1, red-first: the boundary is per-EXECUTION only if a lane's own commits are outside it.
    /// Here the reviewed baseline declares nothing, and the branch then COMMITS <c>exit 0</c> — exactly
    /// what an <c>implement</c> lane does as its ordinary designed behaviour. Against the pre-M1 code the
    /// read was <c>HEAD</c>'s, so <c>exit 0</c> came back and graded the next dispatch into the same
    /// worktree; the merge-base read returns nothing and falls through to the role default.
    /// </summary>
    [Fact]
    public async Task ReadCommittedRepoDeclarationAsync_ignores_a_declaration_the_branch_committed_after_the_reviewed_base()
    {
        var workspace = CreateTempWorkspace();
        try
        {
            TempGitRepository.InitWithEverythingCommitted(workspace);
            TempGitRepository.SetReviewedBaselineAtHead(workspace);

            // The lane commits its own declaration on its own branch, on top of the reviewed base.
            WriteRepoDeclaration(workspace, "exit 0");
            TempGitRepository.CommitAll(workspace, "lane declares its own verify");

            // The control arm: HEAD really does hold it, so this test can distinguish "read the base"
            // from "read nothing at all". Without this the assertion below would pass on a broken read.
            Assert.Equal("exit 0", GitShowAtHead(workspace));

            var committed = await VerifyCommandResolver.ReadCommittedRepoDeclarationAsync(
                workspace, TestContext.Current.CancellationToken);

            Assert.Null(committed.CommandLine);
            Assert.False(committed.Unreviewed);

            var resolved = VerifyCommandResolver.Resolve(committed.CommandLine, overrideCommand: null, roleVerifyPixiTask: "gates-quiet");
            Assert.Equal(VerifyCommandSource.RoleDefault, resolved!.Source);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(workspace);
        }
    }

    /// <summary>
    /// The other polarity: a declaration that IS in the reviewed base still takes effect, and a lane
    /// committing a different one on top does not replace it. Without this arm the test above would also
    /// pass for a read that returned <c>null</c> unconditionally.
    /// </summary>
    [Fact]
    public async Task ReadCommittedRepoDeclarationAsync_reads_the_reviewed_base_declaration_not_the_branch_tip_one()
    {
        var workspace = CreateTempWorkspace();
        try
        {
            WriteRepoDeclaration(workspace, "python -c \"import sys; sys.exit(1)\"");
            TempGitRepository.InitWithEverythingCommitted(workspace);
            TempGitRepository.SetReviewedBaselineAtHead(workspace);

            WriteRepoDeclaration(workspace, "exit 0");
            TempGitRepository.CommitAll(workspace, "lane rewrites the verify declaration");
            Assert.Equal("exit 0", GitShowAtHead(workspace));

            var committed = await VerifyCommandResolver.ReadCommittedRepoDeclarationAsync(
                workspace, TestContext.Current.CancellationToken);

            Assert.Equal("python -c \"import sys; sys.exit(1)\"", committed.CommandLine);
            Assert.False(committed.Unreviewed);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(workspace);
        }
    }

    /// <summary>
    /// No <c>origin/main</c> at all (no remote, or a default branch that is not <c>main</c>): the read
    /// falls back to <c>HEAD</c> — the narrower, per-execution boundary — and SAYS SO, so the wider claim
    /// is never made silently. spec/baton.md §3 scopes this; <c>FlowEvent.VerifyDeclarationUnreviewed</c>
    /// is what carries it into the journal.
    /// </summary>
    [Fact]
    public async Task ReadCommittedRepoDeclarationAsync_falls_back_to_HEAD_and_reports_unreviewed_with_no_origin_main()
    {
        var workspace = CreateTempWorkspace();
        try
        {
            WriteRepoDeclaration(workspace, "python -c \"import sys; sys.exit(1)\"");
            TempGitRepository.InitWithEverythingCommitted(workspace);

            var committed = await VerifyCommandResolver.ReadCommittedRepoDeclarationAsync(
                workspace, TestContext.Current.CancellationToken);

            Assert.Equal("python -c \"import sys; sys.exit(1)\"", committed.CommandLine);
            Assert.True(committed.Unreviewed);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(workspace);
        }
    }

    /// <summary>
    /// Unreviewed is a fact about a declaration, not about a repository: a workspace with no reviewed
    /// baseline AND no declaration has nothing to announce, so the flag stays false and the journal
    /// stays quiet.
    /// </summary>
    [Fact]
    public async Task ReadCommittedRepoDeclarationAsync_does_not_report_unreviewed_when_there_is_no_declaration_at_all()
    {
        var workspace = CreateTempWorkspace();
        try
        {
            TempGitRepository.InitWithEverythingCommitted(workspace);

            var committed = await VerifyCommandResolver.ReadCommittedRepoDeclarationAsync(
                workspace, TestContext.Current.CancellationToken);

            Assert.Null(committed.CommandLine);
            Assert.False(committed.Unreviewed);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(workspace);
        }
    }

    [Fact]
    public void DeclarationDigest_compares_the_command_line_not_the_file()
    {
        // What FlowEvent.VerifyDeclarationIgnored's two digests mean: a comment or whitespace edit is
        // not drift, a changed command always is, and "no declaration" is distinguishable from both.
        Assert.Equal(
            VerifyCommandResolver.DeclarationDigest("pixi run gates-quiet"),
            VerifyCommandResolver.DeclarationDigest("pixi run gates-quiet"));
        Assert.NotEqual(
            VerifyCommandResolver.DeclarationDigest("pixi run gates-quiet"),
            VerifyCommandResolver.DeclarationDigest("cmd /c exit 0"));
        Assert.Null(VerifyCommandResolver.DeclarationDigest(null));
    }

    [Fact]
    public async Task CheckRunnableAsync_role_default_reports_not_runnable_when_the_pixi_task_is_absent()
    {
        // #1702's own measured shape: a role's baked-in task name that a foreign (or just
        // out-of-date) workspace's `pixi task list` does not contain.
        var resolved = VerifyCommandResolver.Resolve(
            committedRepoDeclaration: null, overrideCommand: null, roleVerifyPixiTask: "this-task-definitely-does-not-exist");

        var (runnable, reason) = await VerifyCommandResolver.CheckRunnableAsync(resolved!, RepoRoot(), CancellationToken.None);

        Assert.False(runnable);
        Assert.Equal("task absent: this-task-definitely-does-not-exist", reason);
    }

    [Fact]
    public async Task CheckRunnableAsync_role_default_reports_runnable_when_the_pixi_task_is_present()
    {
        var resolved = VerifyCommandResolver.Resolve(
            committedRepoDeclaration: null, overrideCommand: null, roleVerifyPixiTask: "build");

        var (runnable, reason) = await VerifyCommandResolver.CheckRunnableAsync(resolved!, RepoRoot(), CancellationToken.None);

        Assert.True(runnable);
        Assert.Null(reason);
    }

    /// <summary>
    /// #1708 H2, red-first: a probe that SPAWNS and exits non-zero is an engine-environment failure,
    /// not evidence the task is absent (spec/baton.md §3). Before this fix it returned
    /// <c>"task absent: ... (pixi task list failed)"</c> and the gate was skipped with the step reading
    /// Succeeded. <c>git task list</c> is the stand-in: a real binary that runs and exits 1.
    /// </summary>
    [Fact]
    public async Task CheckRunnableAsync_role_default_reports_runnable_when_the_pixi_probe_itself_exits_non_zero()
    {
        var resolved = VerifyCommandResolver.Resolve(
            committedRepoDeclaration: null, overrideCommand: null, roleVerifyPixiTask: "gates-quiet");

        var (runnable, reason) = await VerifyCommandResolver.CheckRunnableAsync(
            resolved!, RepoRoot(), TestContext.Current.CancellationToken, pixiProgram: "git");

        Assert.True(runnable);
        Assert.Null(reason);
    }

    /// <summary>
    /// #1797, red-first: a stub that exits 0 without ever echoing one of the workspace's own declared
    /// task names. Rationale for why this must defer rather than read as absence lives on
    /// <see cref="VerifyCommandResolver"/>'s <c>CheckPixiTaskAsync</c> comment — not restated here.
    /// </summary>
    [Fact]
    public async Task CheckRunnableAsync_role_default_reports_runnable_when_the_pixi_probe_exits_zero_with_no_listing()
    {
        var stub = CreateEmptyListingBatchFile();
        try
        {
            var resolved = VerifyCommandResolver.Resolve(
                committedRepoDeclaration: null, overrideCommand: null, roleVerifyPixiTask: "gates-quiet");

            var (runnable, reason) = await VerifyCommandResolver.CheckRunnableAsync(
                resolved!, RepoRoot(), TestContext.Current.CancellationToken, pixiProgram: stub);

            Assert.True(runnable);
            Assert.Null(reason);
        }
        finally
        {
            FileCleanup.Delete(stub);
        }
    }

    /// <summary>Exits 0 like a genuine `pixi task list` run, but never actually lists a task — the degraded-answer shape #1797 hardens against.</summary>
    private static string CreateEmptyListingBatchFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aer-empty-listing-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(path, "@echo off\r\necho warning: could not load environment 1>&2\r\nexit /b 0\r\n");
        return path;
    }

    // ---- #1836: the discriminator must not depend on which pixi version wrote the listing ----

    /// <summary>
    /// pixi 0.68.1's real shape, captured verbatim from `pixi task list` in this repo's own checkout
    /// (this machine's installed pixi) -- the header text the pre-#1836 check keyed on.
    /// </summary>
    private const string Pixi0681ListingOfBuildAndTest =
        "Tasks that can run on this machine:\r\n" +
        "-----------------------------------\r\n" +
        "build, test, gates-quiet\r\n" +
        "Task  Description\r\n";

    /// <summary>
    /// pixi 0.79.0's shape (the version CI's setup-pixi installed for #1836) -- no fixed prose header at
    /// all, per PR prefix-dev/pixi#6367 ("Correct `task list` runnability and show every task", changelog
    /// 0.76.1) which replaced it with a per-environment summary plus a `Task\tDescription` table. Cited
    /// from that PR's own description rather than a locally installed 0.79.0, since this machine only
    /// carries 0.68.1; every token this test depends on (the task names, comma/tab separation) is common
    /// to both the PR description and the general shape pixi's docs describe.
    /// </summary>
    private const string Pixi0790ListingOfBuildAndTest =
        "Tasks per environment:\r\n" +
        "-----------------------\r\n" +
        "default (by design): build, test, gates-quiet\r\n" +
        "Task\tDescription\r\n" +
        "build\t\r\n" +
        "test\t\r\n" +
        "gates-quiet\t\r\n";

    [Theory]
    [InlineData(nameof(Pixi0681ListingOfBuildAndTest))]
    [InlineData(nameof(Pixi0790ListingOfBuildAndTest))]
    public async Task CheckRunnableAsync_role_default_reports_runnable_when_the_task_is_present_under_either_pixi_shape(string fixtureName)
    {
        var listing = fixtureName == nameof(Pixi0681ListingOfBuildAndTest) ? Pixi0681ListingOfBuildAndTest : Pixi0790ListingOfBuildAndTest;
        var stub = CreateFixedListingBatchFile(listing);
        try
        {
            var resolved = VerifyCommandResolver.Resolve(
                committedRepoDeclaration: null, overrideCommand: null, roleVerifyPixiTask: "build");

            var (runnable, reason) = await VerifyCommandResolver.CheckRunnableAsync(
                resolved!, RepoRoot(), TestContext.Current.CancellationToken, pixiProgram: stub);

            Assert.True(runnable);
            Assert.Null(reason);
        }
        finally
        {
            FileCleanup.Delete(stub);
        }
    }

    /// <summary>
    /// #1836's own repro: the CI failure was a real listing (positive evidence — it echoes tasks the
    /// workspace's own manifest declares) that simply doesn't name the probed task, under BOTH pixi
    /// shapes. Before this fix, only the 0.68.1 shape's header was recognized as "real"; the 0.79.0 shape
    /// fell through to "runnable" (deferred) and the absence was never reported.
    /// </summary>
    [Theory]
    [InlineData(nameof(Pixi0681ListingOfBuildAndTest))]
    [InlineData(nameof(Pixi0790ListingOfBuildAndTest))]
    public async Task CheckRunnableAsync_role_default_reports_not_runnable_when_the_task_is_absent_under_either_pixi_shape(string fixtureName)
    {
        var listing = fixtureName == nameof(Pixi0681ListingOfBuildAndTest) ? Pixi0681ListingOfBuildAndTest : Pixi0790ListingOfBuildAndTest;
        var stub = CreateFixedListingBatchFile(listing);
        try
        {
            var resolved = VerifyCommandResolver.Resolve(
                committedRepoDeclaration: null, overrideCommand: null, roleVerifyPixiTask: "this-task-definitely-does-not-exist");

            var (runnable, reason) = await VerifyCommandResolver.CheckRunnableAsync(
                resolved!, RepoRoot(), TestContext.Current.CancellationToken, pixiProgram: stub);

            Assert.False(runnable);
            Assert.Equal("task absent: this-task-definitely-does-not-exist", reason);
        }
        finally
        {
            FileCleanup.Delete(stub);
        }
    }

    /// <summary>A stub `pixi task list` that prints a fixed, pre-baked listing regardless of pixi version installed on the test machine.</summary>
    private static string CreateFixedListingBatchFile(string listing)
    {
        var path = Path.Combine(Path.GetTempPath(), $"aer-fixed-listing-{Guid.NewGuid():N}.cmd");
        var lines = listing.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var script = "@echo off\r\n" + string.Join("\r\n", lines.Select(line => $"echo {line}")) + "\r\nexit /b 0\r\n";
        File.WriteAllText(path, script);
        return path;
    }

    /// <summary>
    /// #1708 H3, red-first (control arm): an unresolvable first token is NOT a not-run verdict any more.
    /// It runs through <c>cmd.exe /d /c</c> and its exit code becomes a real <c>VerifyFailed</c> — the
    /// polarity here is inverted from what this same input asserted before the fix.
    /// </summary>
    [Theory]
    [InlineData("totally-not-a-real-binary-12345 --flag")]
    [InlineData("python -c \"import sys; sys.exit(0)\"")]
    [InlineData("exit 3")]
    [InlineData("echo ok")]
    [InlineData("call gates.bat")]
    public async Task CheckRunnableAsync_never_pre_probes_a_non_pixi_command_line(string overrideCommand)
    {
        // `exit`, `echo` and `call` are cmd.exe intrinsics: runnable through the shell, resolvable to no
        // file on PATH. The old filesystem lookup called all three "executable not found" and skipped
        // the gate. Nothing but a role-default pixi task is probed at all now.
        var resolved = VerifyCommandResolver.Resolve(
            committedRepoDeclaration: null, overrideCommand: overrideCommand, roleVerifyPixiTask: null);

        var (runnable, reason) = await VerifyCommandResolver.CheckRunnableAsync(
            resolved!, workingDirectory: null, TestContext.Current.CancellationToken);

        Assert.True(runnable);
        Assert.Null(reason);
    }

    /// <summary>
    /// The other half of H3's polarity: those intrinsic lines really do run, and their exit code really
    /// does decide. <c>exit 3</c> goes red, <c>echo ok</c> goes green — through the same
    /// <see cref="VerifyRunner"/> spawn the engine uses.
    /// </summary>
    [Theory]
    [InlineData("exit 3", false)]
    [InlineData("echo ok", true)]
    public async Task A_cmd_intrinsic_verify_line_actually_runs_and_its_exit_code_decides(string commandLine, bool expectedPassed)
    {
        var resolved = VerifyCommandResolver.Resolve(
            committedRepoDeclaration: null, overrideCommand: commandLine, roleVerifyPixiTask: null);

        var outcome = await VerifyRunner.RunProcessAsync(
            resolved!.Program, resolved.Args, workingDirectory: null, TestContext.Current.CancellationToken);

        Assert.Equal(expectedPassed, outcome.Passed);
    }

    /// <summary>
    /// #1797: a regression guard, not a repro — this arm was already green on arrival (the cancellation
    /// catch in <see cref="VerifyCommandResolver.CheckRunnableAsync"/> already existed before #1797's
    /// investigation). Kept because no prior test exercised a probe cancelled mid-flight specifically
    /// (only a non-zero exit and a can't-spawn); a stub `pixi` that sleeps well past a short deadline
    /// stands in for a probe stuck behind CPU/lock contention, since <see cref="VerifyCommandResolver.CheckRunnableAsync"/>
    /// takes no timeout of its own (spec/baton.md §3 — MutationInterface's dispatch-scoped token is the
    /// only bound).
    /// </summary>
    [Fact]
    public async Task CheckRunnableAsync_role_default_reports_runnable_when_the_pixi_probe_is_cancelled_mid_flight()
    {
        var sleeper = CreateSleeperBatchFile();
        try
        {
            var resolved = VerifyCommandResolver.Resolve(
                committedRepoDeclaration: null, overrideCommand: null, roleVerifyPixiTask: "gates-quiet");

            // wait-ok: forces cancellation mid-sleep against the 30s sleeper below, not a wait for external state.
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

            var (runnable, reason) = await VerifyCommandResolver.CheckRunnableAsync(
                resolved!, RepoRoot(), cts.Token, pixiProgram: sleeper);

            Assert.True(runnable);
            Assert.Null(reason);
        }
        finally
        {
            FileCleanup.Delete(sleeper);
        }
    }

    /// <summary>A `.cmd` that outlives any short test deadline — <c>ping</c>'s wait is not cancellable by closing stdin, so it is reliably still running when the token fires.</summary>
    private static string CreateSleeperBatchFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aer-sleeper-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(path, "@echo off\r\nping -n 30 127.0.0.1 >nul\r\n");
        return path;
    }

    [Fact]
    public async Task CheckRunnableAsync_role_default_reports_runnable_when_pixi_itself_cannot_spawn()
    {
        // Pins the CheckPixiTaskAsync BatonException arm's own contract -- see its comment for why.
        // RepoRoot() rather than null so this really does exercise that arm: #1708 M2's manifest check
        // runs FIRST, and a workspace with no pixi project would short-circuit to not-run before the
        // spawn was ever attempted.
        var resolved = VerifyCommandResolver.Resolve(
            committedRepoDeclaration: null, overrideCommand: null, roleVerifyPixiTask: "gates-quiet");

        var (runnable, reason) = await VerifyCommandResolver.CheckRunnableAsync(
            resolved!, RepoRoot(), CancellationToken.None, pixiProgram: "this-is-not-a-real-pixi-binary-12345");

        Assert.True(runnable);
        Assert.Null(reason);
    }

    // ---- #1708 M2: a workspace that is not a pixi project at all ----

    /// <summary>
    /// #1708 M2, red-first: <c>pixi run &lt;task&gt;</c> cannot exist in a workspace with no pixi
    /// manifest, and the filesystem says so positively — the same class of evidence as "the task list ran
    /// and did not name it", not the "the probe failed" class H2 refuses to read as absence.
    /// <c>DispatchCommandEndToEndTests</c> carries the end-to-end arm and the regression it pins.
    /// </summary>
    [Fact]
    public async Task CheckRunnableAsync_role_default_is_not_runnable_when_the_workspace_is_not_a_pixi_project()
    {
        var workspace = CreateTempWorkspace();
        try
        {
            var resolved = VerifyCommandResolver.Resolve(
                committedRepoDeclaration: null, overrideCommand: null, roleVerifyPixiTask: "gates-quiet");

            var (runnable, reason) = await VerifyCommandResolver.CheckRunnableAsync(
                resolved!, workspace, TestContext.Current.CancellationToken);

            Assert.False(runnable);
            Assert.Equal("no pixi project: gates-quiet", reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(workspace);
        }
    }

    /// <summary>
    /// The polarity that keeps the check honest, and the reason it is an ANCESTOR walk rather than a
    /// single <c>File.Exists</c> — spec/baton.md §3 states the monorepo shape this protects, which is
    /// #1708 H2's failure reintroduced from the other side.
    /// </summary>
    [Fact]
    public async Task CheckRunnableAsync_role_default_finds_a_pixi_manifest_in_an_ANCESTOR_of_the_workspace()
    {
        var root = CreateTempWorkspace();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, "pixi.toml"), "[workspace]\nname = \"m2-fixture\"\n", TestContext.Current.CancellationToken);
            var package = Path.Combine(root, "packages", "thing");
            Directory.CreateDirectory(package);

            var resolved = VerifyCommandResolver.Resolve(
                committedRepoDeclaration: null, overrideCommand: null, roleVerifyPixiTask: "gates-quiet");

            // pixi itself is pointed at an unspawnable name so this asserts only the manifest gate: the
            // walk found the ancestor manifest, so the "not a pixi project" arm did NOT fire and the
            // engine-environment arm (runnable, let the real run decide) did.
            var (runnable, reason) = await VerifyCommandResolver.CheckRunnableAsync(
                resolved!, package, TestContext.Current.CancellationToken, pixiProgram: "this-is-not-a-real-pixi-binary-12345");

            Assert.True(runnable);
            Assert.Null(reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    /// <summary>
    /// A <c>pyproject.toml</c> carrying <c>[tool.pixi]</c> is a pixi manifest too — pixi's own discovery
    /// accepts both, and treating only <c>pixi.toml</c> as a project would call a real pixi workspace
    /// unverifiable.
    /// </summary>
    [Fact]
    public async Task CheckRunnableAsync_role_default_accepts_a_pyproject_toml_pixi_manifest()
    {
        var workspace = CreateTempWorkspace();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(workspace, "pyproject.toml"),
                "[project]\nname = \"m2-fixture\"\n\n[tool.pixi.workspace]\nchannels = []\n",
                TestContext.Current.CancellationToken);

            var resolved = VerifyCommandResolver.Resolve(
                committedRepoDeclaration: null, overrideCommand: null, roleVerifyPixiTask: "gates-quiet");

            var (runnable, reason) = await VerifyCommandResolver.CheckRunnableAsync(
                resolved!, workspace, TestContext.Current.CancellationToken, pixiProgram: "this-is-not-a-real-pixi-binary-12345");

            Assert.True(runnable);
            Assert.Null(reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(workspace);
        }
    }

    /// <summary>
    /// The manifest check is a ROLE-DEFAULT concern only. An override or repo-declared command line is
    /// never <c>pixi run &lt;task&gt;</c> by construction, so a non-pixi workspace says nothing about
    /// whether it can run — #1708 H3's "nothing else is probed at all" is not weakened by M2.
    /// </summary>
    [Fact]
    public async Task CheckRunnableAsync_a_non_pixi_workspace_does_not_block_an_override_command()
    {
        var workspace = CreateTempWorkspace();
        try
        {
            var resolved = VerifyCommandResolver.Resolve(
                committedRepoDeclaration: null, overrideCommand: "exit 0", roleVerifyPixiTask: "gates-quiet");

            var (runnable, reason) = await VerifyCommandResolver.CheckRunnableAsync(
                resolved!, workspace, TestContext.Current.CancellationToken);

            Assert.True(runnable);
            Assert.Null(reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(workspace);
        }
    }

    private static string CreateTempWorkspace() => VerifyDeclarationWorkspace.CreateTemp();

    private static void WriteRepoDeclaration(string workspace, string content) =>
        VerifyDeclarationWorkspace.WriteDeclaration(workspace, content);

    private static string? GitShowAtHead(string workspace) => VerifyDeclarationWorkspace.ShowAtHead(workspace);


    /// <summary>The real baton repo checkout — its own <c>pixi task list</c> is what CheckRunnableAsync's role-default arms probe.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "pixi.toml")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repo root (pixi.toml) from test base directory.");
    }
}
