using Baton.Accounting;
using Baton.Cli.Tests.TestSupport;
using Baton.Vendors;

namespace Baton.Cli.Tests;

/// <summary>
/// #2076's second half: what ceiling <c>baton queue add --issue &lt;n&gt;</c> records for the worktree
/// it provisions. All three arms are needed to discriminate — inheriting proves the source repository's
/// ceiling is consulted, the fallback proves an untrusted repository still gets the unrestricted
/// ceiling the verb has always recorded (and now says so) rather than being broken by the new lookup,
/// and the probe-failure arm proves that fallback is not reachable through a git that answered nothing.
/// </summary>
/// <remarks>
/// Drives <see cref="IssueWorktreeProvisioner.ProvisionAsync"/> through its own injected runner and
/// probe seams, so no <c>gh</c>, no <c>git</c> and no network are involved: the runner reports success
/// for both spawns and the test creates the worktree directory the way <c>git worktree add</c> would.
/// </remarks>
public sealed class IssueWorktreeProvisionerTrustTests : IDisposable
{
    private const string CapturedRepository = "github.com/right/repository";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"provision-trust-{Guid.NewGuid():N}");

    [Fact]
    public async Task Provision_inherits_a_narrowed_ceiling_from_the_source_repository()
    {
        using var home = new IsolatedBatonHome();
        var repository = MakeDirectory("baton");
        var worktree = Path.Combine(_root, "w2076");
        var commonDir = Path.Combine(repository, ".git");
        var noNetwork = new ProjectCeiling(ReadFiles: true, WriteFiles: true, RunShellCommands: true, NetworkAccess: false);
        ProjectCeilingStore.Set(repository, noNetwork, ProjectCeilingStore.DefaultPath);

        var output = new StringWriter();
        var provisioned = await IssueWorktreeProvisioner.ProvisionAsync(
            2076, repository, _root, CapturedRepository, Runner(worktree), Probe(commonDir, repository, worktree),
            output, TestContext.Current.CancellationToken);

        Assert.Equal(worktree, provisioned.Workspace);
        var recorded = ProjectCeilingStore.TryGet(worktree, ProjectCeilingStore.DefaultPath);
        Assert.NotNull(recorded);
        Assert.False(recorded.NetworkAccess);
        Assert.Equal(ProjectCeilingStore.CanonicalKey(repository), recorded.InheritedFrom);

        // The `add` is the ONLY place this can be said: by the time this lane dispatches, the workspace
        // is already trusted and TryRecordAsync returns null, so a line dropped here is dropped for good.
        Assert.Contains(repository, output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("inherited", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Provision_falls_back_to_the_unrestricted_ceiling_when_no_trusted_sibling_matches()
    {
        using var home = new IsolatedBatonHome();
        var repository = MakeDirectory("baton");
        var worktree = Path.Combine(_root, "w2076");
        var commonDir = Path.Combine(repository, ".git");

        // Deliberately NOT trusted: this is the repository an operator has never run `baton trust`
        // against, and the verb's own pre-#2076 widening is what it still gets.
        var output = new StringWriter();
        var provisioned = await IssueWorktreeProvisioner.ProvisionAsync(
            2076, repository, _root, CapturedRepository, Runner(worktree), Probe(commonDir, repository, worktree),
            output, TestContext.Current.CancellationToken);

        var recorded = ProjectCeilingStore.TryGet(provisioned.Workspace, ProjectCeilingStore.DefaultPath);
        Assert.NotNull(recorded);
        Assert.True(recorded.IsUnrestricted);
        Assert.Null(recorded.InheritedFrom);

        // The other half of the polarity: nothing was inherited, and the WIDENING is what gets said —
        // on the same output the inheritance line uses, so an operator reading the add learns that the
        // verb's own fallback wrote `all`, not that a ceiling was derived.
        var line = Assert.Single(output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(
            $"workspace {ProjectCeilingStore.CanonicalKey(worktree)}: source repository has no recorded ceiling; recorded ceiling all",
            line);
        Assert.DoesNotContain("inherited", line, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The fallback is NOT the answer to a probe that fails. With the repository's ceiling narrowed and
    /// git answering nothing for the new worktree (missing, timed out, non-zero — the resolver folds all
    /// three into <see langword="null"/>), the add refuses and records nothing, rather than stamping
    /// <c>all</c> over a ceiling the operator deliberately narrowed.
    /// </summary>
    [Fact]
    public async Task Provision_refuses_rather_than_falling_back_when_the_identity_probe_fails()
    {
        using var home = new IsolatedBatonHome();
        var repository = MakeDirectory("baton");
        var worktree = Path.Combine(_root, "w2076");
        var noNetwork = new ProjectCeiling(ReadFiles: true, WriteFiles: true, RunShellCommands: true, NetworkAccess: false);
        ProjectCeilingStore.Set(repository, noNetwork, ProjectCeilingStore.DefaultPath);

        var output = new StringWriter();
        var refusal = await Assert.ThrowsAsync<ProjectNotTrustedException>(() => IssueWorktreeProvisioner.ProvisionAsync(
            2076, repository, _root, CapturedRepository, Runner(worktree), (_, _) => Task.FromResult<RepositoryIdentity?>(null),
            output, TestContext.Current.CancellationToken));

        Assert.Equal(worktree, refusal.ProjectPath);
        Assert.Contains("probe", refusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(ProjectCeilingStore.TryGet(worktree, ProjectCeilingStore.DefaultPath));
        Assert.Equal(string.Empty, output.ToString());
    }

    /// <summary>
    /// #2121: the fallback is for a repository that was NEVER trusted, not one the operator revoked.
    /// Trust the root, revoke it (every recorded path of the repository is now a tombstone), then queue
    /// an issue against it: the add refuses with the revocation named, records nothing and announces
    /// nothing. The positive control is the fallback arm above: the same call, the same fixture, only
    /// the store differs (empty there, tombstoned here) — so the refusal is the tombstone's doing.
    /// </summary>
    [Fact]
    public async Task Provision_refuses_rather_than_falling_back_when_the_repository_was_revoked()
    {
        using var home = new IsolatedBatonHome();
        var repository = MakeDirectory("baton");
        var worktree = Path.Combine(_root, "w2121");
        var commonDir = Path.Combine(repository, ".git");
        ProjectCeilingStore.Set(repository, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);
        ProjectCeilingStore.Revoke(repository, ProjectCeilingStore.DefaultPath);

        var output = new StringWriter();
        var refusal = await Assert.ThrowsAsync<ProjectNotTrustedException>(() => IssueWorktreeProvisioner.ProvisionAsync(
            2121, repository, _root, CapturedRepository, Runner(worktree), Probe(commonDir, repository, worktree),
            output, TestContext.Current.CancellationToken));

        Assert.Equal(worktree, refusal.ProjectPath);
        Assert.Contains("revoked", refusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(ProjectCeilingStore.CanonicalKey(repository), refusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(ProjectCeilingStore.TryGetRecord(worktree, ProjectCeilingStore.DefaultPath));
        Assert.Equal(string.Empty, output.ToString());
    }

    /// <summary>
    /// #2121: <c>baton trust</c> after the revoke ends it. The same add that refused above inherits the
    /// re-trusted ceiling — narrowed here, so the assertion cannot be satisfied by the <c>all</c>
    /// fallback — and announces the inheritance.
    /// </summary>
    [Fact]
    public async Task Provision_inherits_again_once_the_revoked_repository_is_re_trusted()
    {
        using var home = new IsolatedBatonHome();
        var repository = MakeDirectory("baton");
        var worktree = Path.Combine(_root, "w2121");
        var commonDir = Path.Combine(repository, ".git");
        ProjectCeilingStore.Set(repository, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);
        ProjectCeilingStore.Revoke(repository, ProjectCeilingStore.DefaultPath);
        var noNetwork = new ProjectCeiling(ReadFiles: true, WriteFiles: true, RunShellCommands: true, NetworkAccess: false);
        await TrustCommand.ExecuteAsync(
            new TrustOptions(TrustMode.Register, repository, noNetwork), new StringWriter(),
            Probe(commonDir, repository, worktree), TestContext.Current.CancellationToken);

        var output = new StringWriter();
        var provisioned = await IssueWorktreeProvisioner.ProvisionAsync(
            2121, repository, _root, CapturedRepository, Runner(worktree), Probe(commonDir, repository, worktree),
            output, TestContext.Current.CancellationToken);

        var recorded = ProjectCeilingStore.TryGet(provisioned.Workspace, ProjectCeilingStore.DefaultPath);
        Assert.NotNull(recorded);
        Assert.False(recorded.NetworkAccess);
        Assert.Equal(ProjectCeilingStore.CanonicalKey(repository), recorded.InheritedFrom);
        Assert.Contains("inherited", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// #2121: <c>--forget</c> after the revoke is the other way out of the revoked state — it deletes the
    /// tombstone, so the same add that refused above now takes the never-trusted fallback and announces
    /// it, byte-for-byte the line the never-trusted arm pins. Together with the revoked arm this is the
    /// discriminating pair: same fixture, same call, only the tombstone's presence differs.
    /// </summary>
    [Fact]
    public async Task Provision_falls_back_and_announces_never_trusted_once_the_revoked_repository_is_forgotten()
    {
        using var home = new IsolatedBatonHome();
        var repository = MakeDirectory("baton");
        var worktree = Path.Combine(_root, "w2121");
        var commonDir = Path.Combine(repository, ".git");
        ProjectCeilingStore.Set(repository, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);
        ProjectCeilingStore.Revoke(repository, ProjectCeilingStore.DefaultPath);
        var forget = new StringWriter();
        await TrustCommand.ExecuteAsync(
            new TrustOptions(TrustMode.Forget, repository, null), forget, TestContext.Current.CancellationToken);
        Assert.Contains("Forgot", forget.ToString(), StringComparison.Ordinal);

        var output = new StringWriter();
        var provisioned = await IssueWorktreeProvisioner.ProvisionAsync(
            2121, repository, _root, CapturedRepository, Runner(worktree), Probe(commonDir, repository, worktree),
            output, TestContext.Current.CancellationToken);

        var recorded = ProjectCeilingStore.TryGet(provisioned.Workspace, ProjectCeilingStore.DefaultPath);
        Assert.NotNull(recorded);
        Assert.True(recorded.IsUnrestricted);
        Assert.Null(recorded.InheritedFrom);
        var line = Assert.Single(output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(
            $"workspace {ProjectCeilingStore.CanonicalKey(worktree)}: source repository has no recorded ceiling; recorded ceiling all",
            line);
    }

    /// <summary>
    /// #2121: a recorded path the probe cannot identify is not skipped as "no match" — it might be the
    /// tombstone. With the repository revoked and its own probe THROWING, the add refuses naming that
    /// path and the probe's error, records nothing and announces nothing, instead of reaching the
    /// never-trusted fallback the revoked arm exists to keep it from.
    /// </summary>
    [Fact]
    public async Task Provision_refuses_a_revoked_source_repository_even_when_an_old_probe_is_unreadable()
    {
        using var home = new IsolatedBatonHome();
        var repository = MakeDirectory("baton");
        var worktree = Path.Combine(_root, "w2121");
        var commonDir = Path.Combine(repository, ".git");
        ProjectCeilingStore.Set(repository, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);
        ProjectCeilingStore.Revoke(repository, ProjectCeilingStore.DefaultPath);
        Func<string, CancellationToken, Task<RepositoryIdentity?>> probe = (path, _) =>
            path.Equals(repository, StringComparison.OrdinalIgnoreCase)
                ? throw new InvalidOperationException("git rev-parse timed out after 10s")
                : Task.FromResult<RepositoryIdentity?>(RepositoryIdentity.From(null, commonDir));

        var output = new StringWriter();
        var refusal = await Assert.ThrowsAsync<ProjectNotTrustedException>(() => IssueWorktreeProvisioner.ProvisionAsync(
            2121, repository, _root, CapturedRepository, Runner(worktree), probe,
            output, TestContext.Current.CancellationToken));

        Assert.Equal(worktree, refusal.ProjectPath);
        Assert.Contains("revoked", refusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(ProjectCeilingStore.TryGetRecord(worktree, ProjectCeilingStore.DefaultPath));
        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public async Task Captured_repository_scopes_issue_view_and_develop_when_ambient_repository_conflicts()
    {
        using var home = new IsolatedBatonHome();
        const string ambientGhRepo = "github.com/wrong/repository";
        var repository = MakeDirectory("baton");
        var worktree = Path.Combine(_root, "w2212");
        var commonDir = Path.Combine(repository, ".git");
        var calls = new List<string[]>();
        async Task<(int ExitCode, string Output)> Runner(
            string fileName, IReadOnlyList<string> args, string _, CancellationToken cancellationToken)
        {
            calls.Add(args.ToArray());
            if (fileName == "git")
            {
                if (args is ["show-ref", "--verify", "--quiet", ..])
                {
                    return (1, string.Empty);
                }
                if (args is ["ls-remote", "--heads", "origin", ..])
                {
                    return (0, string.Empty);
                }
                Directory.CreateDirectory(worktree);
            }

            await Task.CompletedTask;
            return (0, args is ["issue", "view", ..] ? "{\"title\":\"right\",\"body\":\"repo\"}" : string.Empty);
        }

        var issue = await IssueWorktreeProvisioner.FetchIssueAsync(
            2212, repository, CapturedRepository, Runner, TestContext.Current.CancellationToken);
        await IssueWorktreeProvisioner.ProvisionAsync(
            2212, repository, _root, CapturedRepository, Runner, Probe(commonDir, repository, worktree),
            TextWriter.Null, TestContext.Current.CancellationToken);

        Assert.NotEqual(ambientGhRepo, CapturedRepository);
        Assert.Equal(("right", "repo"), issue);
        Assert.Contains(calls, args => args is
            ["issue", "view", "2212", "--json", "title,body", "--repo", CapturedRepository]);
        Assert.Contains(calls, args => args is
            ["issue", "develop", "2212", "--name", "2212-lane", "--repo", CapturedRepository]);
    }

    [Fact]
    public async Task Provision_uses_the_lowest_suffix_after_a_proven_branch_collision()
    {
        using var home = new IsolatedBatonHome();
        var repository = MakeDirectory("baton");
        var first = Path.Combine(_root, "w2293");
        var second = Path.Combine(_root, "w2293-2");
        var commonDir = Path.Combine(repository, ".git");
        var calls = new List<string[]>();
        async Task<(int ExitCode, string Output)> Runner(string file, IReadOnlyList<string> args, string workingDirectory, CancellationToken cancellationToken)
        {
            calls.Add(args.ToArray());
            if (args is ["issue", "develop", _, "--name", "2293-lane", ..])
            {
                return (1, "failed to create linked branch: API returned empty branch name");
            }
            if (args is ["show-ref", "--verify", "--quiet", "refs/heads/2293-lane"])
            {
                return (1, string.Empty);
            }
            if (args is ["ls-remote", "--heads", "origin", "refs/heads/2293-lane"])
            {
                return (0, "deadbeef\trefs/heads/2293-lane\n");
            }
            if (args is ["show-ref", "--verify", "--quiet", "refs/heads/2293-lane-2"])
            {
                return (1, string.Empty);
            }
            if (args is ["ls-remote", "--heads", "origin", "refs/heads/2293-lane-2"])
            {
                return (0, string.Empty);
            }
            if (file == "git" && args is ["worktree", "add", ..])
            {
                Directory.CreateDirectory(second);
            }
            return (0, string.Empty);
        }

        var provisioned = await IssueWorktreeProvisioner.ProvisionAsync(
            2293, repository, _root, CapturedRepository, Runner, Probe(commonDir, repository, second),
            TextWriter.Null, TestContext.Current.CancellationToken);

        Assert.Equal(new IssueWorktreeProvisioner.ProvisionedIssueWorktree(second, "2293-lane-2"), provisioned);
        Assert.DoesNotContain(calls, args => args is ["worktree", "add", var workspace, ..] && workspace == first);
    }

    [Fact]
    public async Task Provision_skips_an_existing_canonical_branch_without_asking_GitHub_to_create_it()
    {
        using var home = new IsolatedBatonHome();
        var repository = MakeDirectory("baton");
        var second = Path.Combine(_root, "w2293-2");
        var commonDir = Path.Combine(repository, ".git");
        var calls = new List<string[]>();
        Task<(int ExitCode, string Output)> Runner(string file, IReadOnlyList<string> args, string workingDirectory, CancellationToken cancellationToken)
        {
            calls.Add(args.ToArray());
            if (args is ["show-ref", "--verify", "--quiet", "refs/heads/2293-lane"])
            {
                return Task.FromResult((0, string.Empty));
            }
            if (args is ["show-ref", "--verify", "--quiet", ..])
            {
                return Task.FromResult((1, string.Empty));
            }
            if (args is ["ls-remote", "--heads", "origin", ..])
            {
                return Task.FromResult((0, string.Empty));
            }
            if (file == "git" && args is ["worktree", "add", ..])
            {
                Directory.CreateDirectory(second);
            }

            return Task.FromResult((0, string.Empty));
        }

        var provisioned = await IssueWorktreeProvisioner.ProvisionAsync(
            2293, repository, _root, CapturedRepository, Runner, Probe(commonDir, repository, second),
            TextWriter.Null, TestContext.Current.CancellationToken);

        Assert.Equal(new IssueWorktreeProvisioner.ProvisionedIssueWorktree(second, "2293-lane-2"), provisioned);
        Assert.DoesNotContain(calls, args => args is ["issue", "develop", "2293", "--name", "2293-lane", ..]);
        Assert.Contains(calls, args => args is ["issue", "develop", "2293", "--name", "2293-lane-2", ..]);
    }

    [Fact]
    public async Task Provision_reuses_an_existing_canonical_workspace_registered_on_its_branch()
    {
        using var home = new IsolatedBatonHome();
        var repository = MakeDirectory("baton");
        var workspace = Path.Combine(_root, "w2293");
        var commonDir = Path.Combine(repository, ".git");
        Directory.CreateDirectory(workspace);
        var calls = new List<string[]>();
        Task<(int ExitCode, string Output)> Runner(string file, IReadOnlyList<string> args, string workingDirectory, CancellationToken cancellationToken)
        {
            calls.Add(args.ToArray());
            return Task.FromResult(args is ["worktree", "list", "--porcelain"]
                ? (0, $"worktree {workspace}\nbranch refs/heads/2293-lane\n\n")
                : (0, string.Empty));
        }

        var provisioned = await IssueWorktreeProvisioner.ProvisionAsync(
            2293, repository, _root, CapturedRepository, Runner, Probe(commonDir, repository, workspace),
            TextWriter.Null, TestContext.Current.CancellationToken);

        Assert.Equal(new IssueWorktreeProvisioner.ProvisionedIssueWorktree(workspace, "2293-lane"), provisioned);
        Assert.Contains(calls, args => args is ["worktree", "list", "--porcelain"]);
        Assert.DoesNotContain(calls, args => args is ["issue", "develop", ..]);
    }

    [Fact]
    public async Task Provision_preserves_a_reopened_issues_older_worktree_and_selects_a_free_suffix()
    {
        using var home = new IsolatedBatonHome();
        var repository = MakeDirectory("baton");
        var oldWorkspace = Path.Combine(_root, "w2293");
        var nextWorkspace = Path.Combine(_root, "w2293-2");
        var commonDir = Path.Combine(repository, ".git");
        Directory.CreateDirectory(oldWorkspace);
        var oldSentinel = Path.Combine(oldWorkspace, "keep.bin");
        byte[] originalBytes = [0, 1, 2, 3, 255];
        File.WriteAllBytes(oldSentinel, originalBytes);
        var calls = new List<string[]>();
        Task<(int ExitCode, string Output)> Runner(string file, IReadOnlyList<string> args, string workingDirectory, CancellationToken cancellationToken)
        {
            calls.Add(args.ToArray());
            if (args is ["worktree", "list", "--porcelain"])
                return Task.FromResult((0, $"worktree {oldWorkspace}\nbranch refs/heads/2293-partial-artifact\n\n"));
            if (args is ["show-ref", "--verify", "--quiet", "refs/heads/2293-lane"])
                return Task.FromResult((0, string.Empty));
            if (args is ["show-ref", "--verify", "--quiet", "refs/heads/2293-lane-2"])
                return Task.FromResult((1, string.Empty));
            if (args is ["ls-remote", "--heads", "origin", "refs/heads/2293-lane-2"])
                return Task.FromResult((0, string.Empty));
            if (file == "git" && args is ["worktree", "add", var workspace, "2293-lane-2"] && workspace == nextWorkspace)
                Directory.CreateDirectory(nextWorkspace);
            return Task.FromResult((0, string.Empty));
        }

        var provisioned = await IssueWorktreeProvisioner.ProvisionAsync(
            2293, repository, _root, CapturedRepository, Runner, Probe(commonDir, repository, nextWorkspace),
            TextWriter.Null, TestContext.Current.CancellationToken);

        Assert.Equal(new IssueWorktreeProvisioner.ProvisionedIssueWorktree(nextWorkspace, "2293-lane-2"), provisioned);
        Assert.True(Directory.Exists(oldWorkspace));
        Assert.Equal(originalBytes, File.ReadAllBytes(oldSentinel));
        Assert.Contains(calls, args => args is ["issue", "develop", "2293", "--name", "2293-lane-2", ..]);
        Assert.DoesNotContain(calls, args => args is ["worktree", "add", var workspace, ..] && workspace == oldWorkspace);
        Assert.DoesNotContain(calls, args => args is ["worktree", "remove", ..] or ["branch", "-D", ..]);
    }

    [Fact]
    public async Task Provision_refuses_an_older_registered_workspace_without_a_proven_canonical_branch_collision()
    {
        using var home = new IsolatedBatonHome();
        var repository = MakeDirectory("baton");
        var oldWorkspace = Path.Combine(_root, "w2293");
        Directory.CreateDirectory(oldWorkspace);
        var calls = new List<string[]>();
        Task<(int ExitCode, string Output)> Runner(string file, IReadOnlyList<string> args, string workingDirectory, CancellationToken cancellationToken)
        {
            calls.Add(args.ToArray());
            return Task.FromResult(args switch
            {
                ["worktree", "list", "--porcelain"] => (0, $"worktree {oldWorkspace}\nbranch refs/heads/2293-partial-artifact\n\n"),
                ["show-ref", "--verify", "--quiet", "refs/heads/2293-lane"] => (1, string.Empty),
                ["ls-remote", "--heads", "origin", "refs/heads/2293-lane"] => (0, string.Empty),
                _ => (0, string.Empty),
            });
        }

        var refusal = await Assert.ThrowsAsync<CliArgumentException>(() => IssueWorktreeProvisioner.ProvisionAsync(
            2293, repository, _root, CapturedRepository, Runner, probe: null,
            output: TextWriter.Null, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("2293-partial-artifact", refusal.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(oldWorkspace));
        Assert.DoesNotContain(calls, args => args is ["issue", "develop", ..] or ["worktree", "add", ..]);
    }

    [Theory]
    [InlineData("unregistered")]
    [InlineData("unreadable")]
    public async Task Provision_refuses_an_unregistered_or_unreadable_existing_canonical_workspace(string caseName)
    {
        using var home = new IsolatedBatonHome();
        var repository = MakeDirectory("baton");
        var oldWorkspace = Path.Combine(_root, "w2293");
        Directory.CreateDirectory(oldWorkspace);
        var calls = new List<string[]>();
        Task<(int ExitCode, string Output)> Runner(string file, IReadOnlyList<string> args, string workingDirectory, CancellationToken cancellationToken)
        {
            calls.Add(args.ToArray());
            return Task.FromResult(args is ["worktree", "list", "--porcelain"]
                ? caseName == "unreadable" ? (1, "git registration probe failed") : (0, string.Empty)
                : (0, string.Empty));
        }

        var refusal = await Assert.ThrowsAsync<CliArgumentException>(() => IssueWorktreeProvisioner.ProvisionAsync(
            2293, repository, _root, CapturedRepository, Runner, probe: null,
            output: TextWriter.Null, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("worktree", refusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(oldWorkspace));
        Assert.DoesNotContain(calls, args => args is ["issue", "develop", ..] or ["worktree", "add", ..]);
    }

    [Fact]
    public async Task Provision_treats_only_an_exact_remote_branch_ref_as_a_collision()
    {
        using var home = new IsolatedBatonHome();
        var repository = MakeDirectory("baton");
        var workspace = Path.Combine(_root, "w2293");
        var commonDir = Path.Combine(repository, ".git");
        var calls = new List<string[]>();
        Task<(int ExitCode, string Output)> Runner(string file, IReadOnlyList<string> args, string workingDirectory, CancellationToken cancellationToken)
        {
            calls.Add(args.ToArray());
            if (args is ["show-ref", "--verify", "--quiet", "refs/heads/2293-lane"])
            {
                return Task.FromResult((1, string.Empty));
            }
            if (args is ["ls-remote", "--heads", "origin", "refs/heads/2293-lane"])
            {
                return Task.FromResult((0, "deadbeef\trefs/heads/foo/2293-lane\n"));
            }
            if (file == "git" && args is ["worktree", "add", ..])
            {
                Directory.CreateDirectory(workspace);
            }

            return Task.FromResult((0, string.Empty));
        }

        var provisioned = await IssueWorktreeProvisioner.ProvisionAsync(
            2293, repository, _root, CapturedRepository, Runner, Probe(commonDir, repository, workspace),
            TextWriter.Null, TestContext.Current.CancellationToken);

        Assert.Equal(new IssueWorktreeProvisioner.ProvisionedIssueWorktree(workspace, "2293-lane"), provisioned);
        Assert.Contains(calls, args => args is ["issue", "develop", "2293", "--name", "2293-lane", ..]);
        Assert.DoesNotContain(calls, args => args is ["issue", "develop", "2293", "--name", "2293-lane-2", ..]);
    }

    [Fact]
    public async Task Provision_refuses_an_arbitrary_develop_failure_without_selecting_a_suffix()
    {
        using var home = new IsolatedBatonHome();
        var repository = MakeDirectory("baton");
        var commonDir = Path.Combine(repository, ".git");
        var calls = new List<string[]>();
        Task<(int ExitCode, string Output)> Runner(string file, IReadOnlyList<string> args, string workingDirectory, CancellationToken cancellationToken)
        {
            calls.Add(args.ToArray());
            return Task.FromResult(args switch
            {
                ["issue", "develop", ..] => (1, "authentication required"),
                ["show-ref", ..] => (1, string.Empty),
                ["ls-remote", ..] => (0, string.Empty),
                _ => (0, string.Empty),
            });
        }

        var refusal = await Assert.ThrowsAsync<CliArgumentException>(() => IssueWorktreeProvisioner.ProvisionAsync(
            2293, repository, _root, CapturedRepository, Runner, Probe(commonDir, repository),
            TextWriter.Null, TestContext.Current.CancellationToken));

        Assert.Contains("authentication required", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(calls, args => args is ["issue", "develop", _, "--name", "2293-lane-2", ..]);
    }

    [Fact]
    public async Task Concurrent_provisioning_selects_distinct_branch_and_workspace_pairs()
    {
        using var home = new IsolatedBatonHome();
        var repository = MakeDirectory("baton");
        var first = Path.Combine(_root, "w2293");
        var second = Path.Combine(_root, "w2293-2");
        var commonDir = Path.Combine(repository, ".git");
        var branches = new HashSet<string>(StringComparer.Ordinal);
        var sync = new object();
        async Task<(int ExitCode, string Output)> Runner(string file, IReadOnlyList<string> args, string workingDirectory, CancellationToken cancellationToken)
        {
            await Task.Yield();
            if (args is ["issue", "develop", _, "--name", var branch, ..])
            {
                lock (sync)
                {
                    return branches.Add(branch) ? (0, string.Empty) : (1, "branch already exists");
                }
            }
            if (args is ["show-ref", "--verify", "--quiet", var reference])
            {
                lock (sync)
                {
                    return branches.Contains(reference["refs/heads/".Length..]) ? (0, string.Empty) : (1, string.Empty);
                }
            }
            if (args is ["ls-remote", "--heads", "origin", var remoteBranch])
            {
                lock (sync)
                {
                    return branches.Contains(remoteBranch) ? (0, $"sha\trefs/heads/{remoteBranch}\n") : (0, string.Empty);
                }
            }
            if (file == "git" && args is ["worktree", "add", var workspace, ..])
            {
                lock (sync)
                {
                    if (Directory.Exists(workspace)) return (1, "workspace already exists");
                    Directory.CreateDirectory(workspace);
                }
            }
            return (0, string.Empty);
        }

        var both = await Task.WhenAll(
            IssueWorktreeProvisioner.ProvisionAsync(2293, repository, _root, CapturedRepository, Runner,
                Probe(commonDir, repository, first, second), TextWriter.Null, TestContext.Current.CancellationToken),
            IssueWorktreeProvisioner.ProvisionAsync(2293, repository, _root, CapturedRepository, Runner,
                Probe(commonDir, repository, first, second), TextWriter.Null, TestContext.Current.CancellationToken));

        Assert.Equal(2, both.Select(result => result.Workspace).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(2, both.Select(result => result.Branch).Distinct(StringComparer.Ordinal).Count());
        Assert.All(both, result => Assert.True(Directory.Exists(result.Workspace)));
    }

    /// <summary>Reports both spawns successful and creates the worktree the way <c>git worktree add</c> would.</summary>
    private static Func<string, IReadOnlyList<string>, string, CancellationToken, Task<(int ExitCode, string Output)>> Runner(
        string worktree) =>
        (fileName, args, _, _) =>
        {
            if (fileName == "git")
            {
                if (args is ["show-ref", "--verify", "--quiet", ..])
                {
                    return Task.FromResult((1, string.Empty));
                }
                if (args is ["ls-remote", "--heads", "origin", ..])
                {
                    return Task.FromResult((0, string.Empty));
                }
                Directory.CreateDirectory(worktree);
            }

            return Task.FromResult((0, string.Empty));
        };

    /// <summary>The repository and its new worktree share one git common directory, as a real pair would.</summary>
    private static Func<string, CancellationToken, Task<RepositoryIdentity?>> Probe(
        string commonDir, params string[] paths) =>
        (path, _) => Task.FromResult(
            paths.Contains(path, StringComparer.OrdinalIgnoreCase) ? RepositoryIdentity.From(null, commonDir) : null);

    private string MakeDirectory(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Baton.Tests.Shared.DirectoryCleanup.DeleteRecursively(_root);
        }
    }
}
