using Baton.Accounting;
using Baton.Cli.Tests.TestSupport;
using Baton.Vendors;

namespace Baton.Cli.Tests;

/// <summary>
/// #2076's second half: what ceiling <c>baton queue add --issue &lt;n&gt;</c> records for the worktree
/// it provisions. Both arms are needed to discriminate — inheriting proves the source repository's
/// ceiling is consulted, and the fallback proves an untrusted repository still gets the unrestricted
/// ceiling the verb has always recorded rather than being broken by the new lookup.
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

        var provisioned = await IssueWorktreeProvisioner.ProvisionAsync(
            2076, repository, _root, Runner(worktree), Probe(commonDir, repository, worktree),
            TestContext.Current.CancellationToken);

        Assert.Equal(worktree, provisioned);
        var recorded = ProjectCeilingStore.TryGet(worktree, ProjectCeilingStore.DefaultPath);
        Assert.NotNull(recorded);
        Assert.False(recorded.NetworkAccess);
        Assert.Equal(ProjectCeilingStore.CanonicalKey(repository), recorded.InheritedFrom);
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
        var provisioned = await IssueWorktreeProvisioner.ProvisionAsync(
            2076, repository, _root, Runner(worktree), Probe(commonDir, repository, worktree),
            TestContext.Current.CancellationToken);

        var recorded = ProjectCeilingStore.TryGet(provisioned, ProjectCeilingStore.DefaultPath);
        Assert.NotNull(recorded);
        Assert.True(recorded.IsUnrestricted);
        Assert.Null(recorded.InheritedFrom);
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
