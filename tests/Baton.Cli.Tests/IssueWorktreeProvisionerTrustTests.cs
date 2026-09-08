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
            2076, repository, _root, Runner(worktree), Probe(commonDir, repository, worktree),
            output, TestContext.Current.CancellationToken);

        Assert.Equal(worktree, provisioned);
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
            2076, repository, _root, Runner(worktree), Probe(commonDir, repository, worktree),
            output, TestContext.Current.CancellationToken);

        var recorded = ProjectCeilingStore.TryGet(provisioned, ProjectCeilingStore.DefaultPath);
        Assert.NotNull(recorded);
        Assert.True(recorded.IsUnrestricted);
        Assert.Null(recorded.InheritedFrom);

        // The other half of the polarity: nothing was inherited, and the WIDENING is what gets said —
        // on the same output the inheritance line uses, so an operator reading the add learns that the
        // verb's own fallback wrote `all`, not that a ceiling was derived.
        var line = Assert.Single(output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(
            $"workspace {ProjectCeilingStore.CanonicalKey(worktree)}: no trusted repository to inherit from; recorded ceiling all",
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
            2076, repository, _root, Runner(worktree), (_, _) => Task.FromResult<RepositoryIdentity?>(null),
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
            2121, repository, _root, Runner(worktree), Probe(commonDir, repository, worktree),
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
            2121, repository, _root, Runner(worktree), Probe(commonDir, repository, worktree),
            output, TestContext.Current.CancellationToken);

        var recorded = ProjectCeilingStore.TryGet(provisioned, ProjectCeilingStore.DefaultPath);
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
            2121, repository, _root, Runner(worktree), Probe(commonDir, repository, worktree),
            output, TestContext.Current.CancellationToken);

        var recorded = ProjectCeilingStore.TryGet(provisioned, ProjectCeilingStore.DefaultPath);
        Assert.NotNull(recorded);
        Assert.True(recorded.IsUnrestricted);
        Assert.Null(recorded.InheritedFrom);
        var line = Assert.Single(output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(
            $"workspace {ProjectCeilingStore.CanonicalKey(worktree)}: no trusted repository to inherit from; recorded ceiling all",
            line);
    }

    /// <summary>
    /// #2121: a recorded path the probe cannot identify is not skipped as "no match" — it might be the
    /// tombstone. With the repository revoked and its own probe THROWING, the add refuses naming that
    /// path and the probe's error, records nothing and announces nothing, instead of reaching the
    /// never-trusted fallback the revoked arm exists to keep it from.
    /// </summary>
    [Fact]
    public async Task Provision_refuses_rather_than_falling_back_when_a_recorded_path_cannot_be_identified()
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
            2121, repository, _root, Runner(worktree), probe,
            output, TestContext.Current.CancellationToken));

        Assert.Equal(worktree, refusal.ProjectPath);
        Assert.Contains(ProjectCeilingStore.CanonicalKey(repository), refusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("git rev-parse timed out after 10s", refusal.Message, StringComparison.Ordinal);
        // The remedy names the RECORDED path, not the workspace: the workspace probed fine, and the
        // try-line is what the operator acts on (#2121 re-review, L2).
        Assert.Equal(ProjectCeilingStore.CanonicalKey(repository), refusal.CandidatePath);
        Assert.Equal(
            $"repair '{ProjectCeilingStore.CanonicalKey(repository)}' so 'git' can identify it, or baton trust "
            + $"\"{ProjectCeilingStore.CanonicalKey(repository)}\" --forget to drop its record if that checkout is gone, "
            + $"then retry — or baton trust \"{worktree}\" --ceiling all (or a comma-separated subset of "
            + "ReadFiles,WriteFiles,RunShellCommands,NetworkAccess) to record one by hand.",
            refusal.TryInvocation);
        Assert.DoesNotContain($"'{worktree}' is a git checkout", refusal.TryInvocation, StringComparison.Ordinal);
        Assert.Null(ProjectCeilingStore.TryGetRecord(worktree, ProjectCeilingStore.DefaultPath));
        Assert.Equal(string.Empty, output.ToString());
    }

    /// <summary>Reports both spawns successful and creates the worktree the way <c>git worktree add</c> would.</summary>
    private static Func<string, IReadOnlyList<string>, string, CancellationToken, Task<(int ExitCode, string Output)>> Runner(
        string worktree) =>
        (fileName, _, _, _) =>
        {
            if (fileName == "git")
            {
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
