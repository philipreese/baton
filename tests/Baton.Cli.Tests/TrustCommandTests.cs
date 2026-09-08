using Baton.Accounting;
using Baton.Cli.Tests.TestSupport;
using Baton.Vendors;

namespace Baton.Cli.Tests;

/// <summary>
/// <c>baton trust</c>'s end-to-end command surface (#1166): register/list/revoke against
/// <see cref="ProjectCeilingStore"/>, isolated through <see cref="IsolatedBatonHome"/> so no test
/// touches a developer's or CI runner's real <c>~/.baton/project-ceilings.json</c>.
/// </summary>
public sealed class TrustCommandTests
{
    [Fact]
    public async Task ExecuteAsync_Register_RecordsTheCeiling()
    {
        using var home = new IsolatedBatonHome();
        var project = Path.Combine(Path.GetTempPath(), $"trust-cmd-{Guid.NewGuid():N}");
        var output = new StringWriter();
        var options = new TrustOptions(TrustMode.Register, project, ProjectCeiling.Unrestricted);

        var exitCode = await TrustCommand.ExecuteAsync(options, output, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("Trusted", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(ProjectCeiling.Unrestricted, ProjectCeilingStore.TryGet(project, ProjectCeilingStore.DefaultPath));
    }

    [Fact]
    public async Task ExecuteAsync_List_NoCeilings_PrintsNone()
    {
        using var home = new IsolatedBatonHome();
        var output = new StringWriter();

        var exitCode = await TrustCommand.ExecuteAsync(
            new TrustOptions(TrustMode.List, null, null), output, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("No project ceilings recorded.", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_List_PrintsARegisteredCeiling()
    {
        using var home = new IsolatedBatonHome();
        var project = Path.Combine(Path.GetTempPath(), $"trust-cmd-list-{Guid.NewGuid():N}");
        ProjectCeilingStore.Set(project, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);
        var output = new StringWriter();

        await TrustCommand.ExecuteAsync(
            new TrustOptions(TrustMode.List, null, null), output, TestContext.Current.CancellationToken);

        Assert.Contains(ProjectCeilingStore.CanonicalKey(project), output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_Revoke_RemovesARecordedCeiling()
    {
        using var home = new IsolatedBatonHome();
        var project = Path.Combine(Path.GetTempPath(), $"trust-cmd-revoke-{Guid.NewGuid():N}");
        ProjectCeilingStore.Set(project, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);
        var output = new StringWriter();

        var exitCode = await TrustCommand.ExecuteAsync(
            new TrustOptions(TrustMode.Revoke, project, null), output, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("Revoked", output.ToString(), StringComparison.Ordinal);
        Assert.Null(ProjectCeilingStore.TryGet(project, ProjectCeilingStore.DefaultPath));
    }

    /// <summary>
    /// #2076: revoking a source prints one line per inherited entry removed with it — the cascade
    /// <see cref="ProjectCeilingStore.Revoke"/> defines, seen from the verb's output.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_Revoke_NamesTheInheritedEntriesRemovedWithTheSource()
    {
        using var home = new IsolatedBatonHome();
        var source = Path.Combine(Path.GetTempPath(), $"trust-cmd-cascade-{Guid.NewGuid():N}");
        var derived = Path.Combine(source + "-w1", "lane");
        ProjectCeilingStore.Set(source, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);
        ProjectCeilingStore.Set(
            derived,
            ProjectCeiling.Unrestricted with { InheritedFrom = ProjectCeilingStore.CanonicalKey(source) },
            ProjectCeilingStore.DefaultPath);
        var output = new StringWriter();

        var exitCode = await TrustCommand.ExecuteAsync(
            new TrustOptions(TrustMode.Revoke, source, null), output, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains($"Also revoked '{ProjectCeilingStore.CanonicalKey(derived)}', which had inherited it.", output.ToString(), StringComparison.Ordinal);
        Assert.Null(ProjectCeilingStore.TryGet(derived, ProjectCeilingStore.DefaultPath));
    }

    /// <summary>#2121: a revoked entry is listed as revoked, with when and its provenance — not as <c>none</c>, and not omitted.</summary>
    [Fact]
    public async Task ExecuteAsync_List_ShowsATombstoneAsRevoked()
    {
        using var home = new IsolatedBatonHome();
        var source = Path.Combine(Path.GetTempPath(), $"trust-cmd-list-tomb-src-{Guid.NewGuid():N}");
        var revoked = Path.Combine(Path.GetTempPath(), $"trust-cmd-list-tomb-{Guid.NewGuid():N}");
        var at = new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        ProjectCeilingStore.Set(revoked, ProjectCeiling.Unrestricted with { InheritedFrom = ProjectCeilingStore.CanonicalKey(source) }, ProjectCeilingStore.DefaultPath);
        ProjectCeilingStore.Revoke(revoked, ProjectCeilingStore.DefaultPath, at);
        var output = new StringWriter();

        await TrustCommand.ExecuteAsync(
            new TrustOptions(TrustMode.List, null, null), output, TestContext.Current.CancellationToken);

        var line = Assert.Single(output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(
            $"{ProjectCeilingStore.CanonicalKey(revoked)}  revoked {at:u}  (inherited from {ProjectCeilingStore.CanonicalKey(source)})",
            line);
    }

    /// <summary>
    /// #2121: re-trusting one path of a revoked repository clears the tombstones on its other paths,
    /// and only those — an unrelated repository's tombstone stays. The probe is injected: the temp
    /// directories are not git checkouts.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_Register_ClearsTheTombstonesOfTheSameRepositoryOnly()
    {
        using var home = new IsolatedBatonHome();
        var root = Path.Combine(Path.GetTempPath(), $"trust-cmd-retrust-{Guid.NewGuid():N}");
        var main = Path.Combine(root, "baton");
        var worktree = Path.Combine(root, "w2121");
        var unrelated = Path.Combine(root, "basis");
        foreach (var directory in new[] { main, worktree, unrelated })
        {
            Directory.CreateDirectory(directory);
        }

        try
        {
            ProjectCeilingStore.Set(main, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);
            ProjectCeilingStore.Set(worktree, ProjectCeiling.Unrestricted with { InheritedFrom = ProjectCeilingStore.CanonicalKey(main) }, ProjectCeilingStore.DefaultPath);
            ProjectCeilingStore.Set(unrelated, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);
            ProjectCeilingStore.Revoke(main, ProjectCeilingStore.DefaultPath);
            ProjectCeilingStore.Revoke(unrelated, ProjectCeilingStore.DefaultPath);
            var commonDir = Path.Combine(main, ".git");
            Func<string, CancellationToken, Task<RepositoryIdentity?>> probe = (path, _) => Task.FromResult(
                path.Equals(main, StringComparison.OrdinalIgnoreCase) || path.Equals(worktree, StringComparison.OrdinalIgnoreCase)
                    ? RepositoryIdentity.From(null, commonDir)
                    : path.Equals(unrelated, StringComparison.OrdinalIgnoreCase)
                        ? RepositoryIdentity.From("https://github.com/philipreese/basis", null)
                        : null);
            var output = new StringWriter();

            var exitCode = await TrustCommand.ExecuteAsync(
                new TrustOptions(TrustMode.Register, main, ProjectCeiling.Unrestricted), output, probe, TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            Assert.Equal(ProjectCeiling.Unrestricted, ProjectCeilingStore.TryGet(main, ProjectCeilingStore.DefaultPath));
            Assert.Null(ProjectCeilingStore.TryGetRecord(worktree, ProjectCeilingStore.DefaultPath));
            Assert.True(ProjectCeilingStore.TryGetRecord(unrelated, ProjectCeilingStore.DefaultPath)?.IsRevoked);
            Assert.Contains($"Cleared the revocation of '{ProjectCeilingStore.CanonicalKey(worktree)}'", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(ProjectCeilingStore.CanonicalKey(unrelated), output.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Baton.Tests.Shared.DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public async Task ExecuteAsync_Revoke_NeverTrusted_SaysNothingToRevoke()
    {
        using var home = new IsolatedBatonHome();
        var project = Path.Combine(Path.GetTempPath(), $"trust-cmd-revoke-none-{Guid.NewGuid():N}");
        var output = new StringWriter();

        var exitCode = await TrustCommand.ExecuteAsync(
            new TrustOptions(TrustMode.Revoke, project, null), output, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("nothing to revoke", output.ToString(), StringComparison.Ordinal);
    }
}
