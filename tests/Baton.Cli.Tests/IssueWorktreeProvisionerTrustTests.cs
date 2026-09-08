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
