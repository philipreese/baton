using System.Diagnostics;
using Baton.Accounting;
using Baton.Status;
using Baton.Vendors;

namespace Baton.Cli.Tests;

/// <summary>
/// #1849: <see cref="RepositoryIdentity"/>'s pure derivation is covered by <c>RepositoryIdentityTests</c>
/// against strings. This file covers the seam that decides WHICH strings — and it does so against real
/// git, because the worktree-convergence claim rests entirely on this type probing
/// <c>--git-common-dir</c> rather than <c>--git-dir</c>, and no string-level test can tell those apart.
/// A real <c>git worktree add</c> is the only instrument that can.
/// </summary>
public sealed class RepositoryIdentityResolverTests
{
    [Fact]
    public async Task A_linked_worktree_and_its_main_checkout_resolve_to_one_identity_with_no_remote()
    {
        // No remote on purpose: with one, both would converge through the origin URL and the
        // common-dir half -- the half a remote-less repository depends on entirely -- would go
        // unexercised. `--git-dir` would answer `<main>/.git/worktrees/<name>` from inside the linked
        // worktree and split the ledger one file per checkout, which is exactly the bug this excludes.
        var root = NewTempDirectory();
        try
        {
            var main = Path.Combine(root, "main");
            var linked = Path.Combine(root, "linked");
            await InitGitRepoAsync(main);
            await RunGitAsync(main, "worktree", "add", "-q", "-b", "side", linked);

            var fromMain = await RepositoryIdentityResolver.TryResolveAsync(main, TestContext.Current.CancellationToken);
            var fromLinked = await RepositoryIdentityResolver.TryResolveAsync(linked, TestContext.Current.CancellationToken);

            Assert.NotNull(fromMain);
            Assert.NotNull(fromLinked);
            Assert.Equal(fromMain.Value, fromLinked.Value);
            Assert.Equal(fromMain.FileSlug, fromLinked.FileSlug);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public async Task Two_unrelated_repositories_resolve_to_two_identities()
    {
        // The control for the arm above: without it, a resolver that returned one constant for every
        // directory would pass convergence and pool the whole fleet into a single ledger.
        var root = NewTempDirectory();
        try
        {
            var first = Path.Combine(root, "first");
            var second = Path.Combine(root, "second");
            await InitGitRepoAsync(first);
            await InitGitRepoAsync(second);

            var a = await RepositoryIdentityResolver.TryResolveAsync(first, TestContext.Current.CancellationToken);
            var b = await RepositoryIdentityResolver.TryResolveAsync(second, TestContext.Current.CancellationToken);

            Assert.NotNull(a);
            Assert.NotNull(b);
            Assert.NotEqual(a.Value, b.Value);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public async Task An_origin_remote_decides_the_identity_even_across_two_separate_clones()
    {
        // Two independent checkouts of one repository share no `.git` at all, so only the remote can
        // converge them -- and it must, or a second clone starts a second ledger for the same project.
        var root = NewTempDirectory();
        try
        {
            var first = Path.Combine(root, "clone-a");
            var second = Path.Combine(root, "clone-b");
            await InitGitRepoAsync(first);
            await InitGitRepoAsync(second);
            await RunGitAsync(first, "remote", "add", "origin", "https://github.com/aer-works/baton.git");
            await RunGitAsync(second, "remote", "add", "origin", "git@github.com:AER-Works/Baton.git");

            var a = await RepositoryIdentityResolver.TryResolveAsync(first, TestContext.Current.CancellationToken);
            var b = await RepositoryIdentityResolver.TryResolveAsync(second, TestContext.Current.CancellationToken);

            Assert.Equal("github.com/aer-works/baton", a!.Value);
            Assert.Equal(a.Value, b!.Value);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public async Task A_directory_that_is_not_a_repository_resolves_to_nothing_rather_than_throwing()
    {
        // What the settle site reads as "no row for this room". It must be an answer, not an exception
        // -- an accounting write never gates a run that already reached Terminal.
        var root = NewTempDirectory();
        try
        {
            var identity = await RepositoryIdentityResolver.TryResolveAsync(root, TestContext.Current.CancellationToken);
            Assert.Null(identity);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    /// <summary>
    /// #1908 review F1, leak (b). <see cref="RepositoryIdentityResolver.TryResolveAsync"/> answers for
    /// any directory INSIDE a checkout, because that is how git discovers a repository — correct for a
    /// room's recorded project root, and wrong as a tie-break for a GUESSED path, which is what
    /// <c>MemoryRootPath.Resolve</c> hands it. <see cref="RepositoryIdentityResolver.IsWorkTreeRoot"/>
    /// is the narrower predicate that tie-break needs.
    /// </summary>
    [Fact]
    public async Task A_directory_inside_a_checkout_is_not_a_work_tree_root_even_though_git_answers_for_it()
    {
        var root = NewTempDirectory();
        try
        {
            var checkout = Path.Combine(root, "checkout");
            await InitGitRepoAsync(checkout);
            var inside = Path.Combine(checkout, "memory");
            Directory.CreateDirectory(inside);

            // The control that makes the negative below mean something: git DOES hand back the
            // checkout's identity for the subdirectory, at full confidence and with no hint that it
            // walked up to find it. Bare existence as a tie-break is what turned that into a wrong
            // answer on a decoded reading.
            var walkedUp = await RepositoryIdentityResolver.TryResolveAsync(inside, TestContext.Current.CancellationToken);
            Assert.NotNull(walkedUp);
            Assert.Equal(
                (await RepositoryIdentityResolver.TryResolveAsync(checkout, TestContext.Current.CancellationToken))!.Value,
                walkedUp.Value);
            Assert.True(Directory.Exists(inside));

            Assert.True(RepositoryIdentityResolver.IsWorkTreeRoot(checkout));
            Assert.False(RepositoryIdentityResolver.IsWorkTreeRoot(inside));

            // And an ordinary directory that is no repository at all is not one either.
            Assert.False(RepositoryIdentityResolver.IsWorkTreeRoot(root));
            Assert.False(RepositoryIdentityResolver.IsWorkTreeRoot(Path.Combine(root, "absent")));
            Assert.False(RepositoryIdentityResolver.IsWorkTreeRoot("   "));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    /// <summary>
    /// A linked worktree's <c>.git</c> is a FILE, not a directory — the shape that makes every worktree
    /// of one repository share one identity (see <see cref="RepositoryIdentityResolver.TryResolveAsync"/>'s
    /// <c>--git-common-dir</c> comment). It is a work-tree root, so the predicate must accept it.
    /// </summary>
    [Fact]
    public async Task A_linked_worktree_whose_git_is_a_file_is_a_work_tree_root()
    {
        var root = NewTempDirectory();
        try
        {
            var checkout = Path.Combine(root, "checkout");
            await InitGitRepoAsync(checkout);

            var linked = Path.Combine(root, "linked");
            await RunGitAsync(checkout, "worktree", "add", "-q", "-b", "linked-branch", linked);

            Assert.True(File.Exists(Path.Combine(linked, ".git")));
            Assert.True(RepositoryIdentityResolver.IsWorkTreeRoot(linked));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public async Task A_missing_directory_resolves_to_nothing()
    {
        Assert.Null(await RepositoryIdentityResolver.TryResolveAsync(
            Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}"), TestContext.Current.CancellationToken));
        Assert.Null(await RepositoryIdentityResolver.TryResolveAsync("   ", TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// #1931 re-review MEDIUM. <see cref="CostLedgerEntry.IdentitySource"/> can only answer "how was
    /// this row keyed" if this resolver names the rung that answered — Program.cs's own comment at the
    /// append says why that matters for the population it stamps. Both arms below, because the VALUE is
    /// the discrimination: a resolver returning one constant would satisfy either arm alone.
    /// </summary>
    [Fact]
    public async Task A_room_with_a_recorded_project_root_reports_recorded_root()
    {
        var probed = new List<string>();
        var (identity, source) = await RepositoryIdentityResolver.TryResolveForRoomAsync(
            RoomPath,
            [new RoomRegistryEntry(BatonPaths.RecordKey(RoomPath), RecordedRoot, DateTime.UtcNow)],
            (directory, _) =>
            {
                probed.Add(directory);
                return Task.FromResult<RepositoryIdentity?>(RepositoryIdentity.From("https://github.com/o/r.git", null));
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(RepositoryIdentitySource.RecordedRoot, source);
        Assert.Equal("github.com/o/r", identity!.Value);
        Assert.Equal([RecordedRoot], probed);
    }

    /// <summary>
    /// The other arm: a room with NO registry entry — reachable because registration is fail-open — is
    /// keyed from wherever the process was started, which is the one case the settle site's (deliberately
    /// narrow) fallback bites. That row is well-formed and may be keyed to the wrong repository, so it is
    /// exactly the row the field has to mark.
    /// </summary>
    [Fact]
    public async Task A_room_with_no_registry_entry_falls_back_to_the_working_directory_and_says_so()
    {
        var probed = new List<string>();
        var (identity, source) = await RepositoryIdentityResolver.TryResolveForRoomAsync(
            RoomPath,
            [new RoomRegistryEntry(BatonPaths.RecordKey(Path.Combine(Path.GetTempPath(), "some-other-room")), RecordedRoot, DateTime.UtcNow)],
            (directory, _) =>
            {
                probed.Add(directory);
                return Task.FromResult<RepositoryIdentity?>(RepositoryIdentity.From("https://github.com/o/r.git", null));
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(RepositoryIdentitySource.WorkingDirectory, source);
        Assert.NotNull(identity);
        Assert.Equal([Environment.CurrentDirectory], probed);
    }

    /// <summary>
    /// What pins the settle site's fallback at its narrower width, which this change deliberately did
    /// not touch. The probe is asserted to have been called exactly once, against the recorded root: a
    /// SECOND call, against the working directory, would BE the widening.
    /// </summary>
    [Fact]
    public async Task A_recorded_root_that_no_longer_resolves_yields_no_identity_rather_than_falling_back()
    {
        var probed = new List<string>();
        var (identity, source) = await RepositoryIdentityResolver.TryResolveForRoomAsync(
            RoomPath,
            [new RoomRegistryEntry(BatonPaths.RecordKey(RoomPath), RecordedRoot, DateTime.UtcNow)],
            (directory, _) =>
            {
                probed.Add(directory);
                return Task.FromResult<RepositoryIdentity?>(null);
            },
            TestContext.Current.CancellationToken);

        Assert.Null(identity);
        Assert.Equal(RepositoryIdentitySource.RecordedRoot, source);
        Assert.Equal([RecordedRoot], probed);
    }

    private static readonly string RoomPath = Path.Combine(Path.GetTempPath(), "baton-identity-source-room");

    private static readonly string RecordedRoot = Path.Combine(Path.GetTempPath(), "baton-identity-source-checkout");

    private static string NewTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"repo-identity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task InitGitRepoAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        await RunGitAsync(directory, "init", "-q");
        // -c identity keeps the commit independent of any (absent) global git config on the runner.
        await RunGitAsync(
            directory, "-c", "user.email=test@example.invalid", "-c", "user.name=Test",
            "commit", "--allow-empty", "-q", "-m", "base");
    }

    private static async Task RunGitAsync(string workingDirectory, params string[] args)
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

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start git — is it on PATH? These tests need git.");
        var (stdout, stderr) = await BoundedProcessWait.RunToExitAsync(
            process, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stdout} {stderr.Trim()}");
        }
    }
}
