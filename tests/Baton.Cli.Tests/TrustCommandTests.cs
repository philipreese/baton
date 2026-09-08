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
