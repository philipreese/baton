using Baton.Accounting;
using Baton.Status;
using Baton.Tests.Shared;

namespace Baton.Cli.Tests;

/// <summary>
/// #2041 review LOW: which FILE <c>baton ledger --repo-identity &lt;key&gt;</c> opens, and what that arm
/// is allowed to create. Since #2041 it reaches <see cref="CostLedgerLocation"/>, whose write side is
/// not a pure lookup — the arm's own comment states exactly what it may do to disk and why the
/// read-only probe in front of it is load-bearing. Nothing pinned that order until this file.
/// </summary>
/// <remarks>
/// <c>LedgerViewOptionsParserTests</c> owns the parse and <c>LedgerViewCommandTests</c> the output; what
/// these own is path resolution alone, driven through the internal seam
/// <c>LedgerExportCommand</c> shares rather than through a rendered report.
/// </remarks>
public sealed class LedgerViewRepoIdentityPathTests
{
    private static string NewHome() =>
        Path.Combine(Path.GetTempPath(), $"ledger-repo-identity-{Guid.NewGuid():N}");

    /// <summary>
    /// The hazard the comment on that arm used to overstate away: a <c>--repo-identity</c> carrying
    /// separators and a drive letter is a rooted string, and <see cref="Path.Combine(string,string)"/>
    /// resolves it AWAY from the storage root rather than under it — so both derived candidates name
    /// files outside <c>{Root}</c>. What keeps that a read is the probe: neither candidate exists, so the
    /// resolver is reached with a sanitized slug instead and creates nothing out there.
    /// </summary>
    [Fact]
    public async Task A_repo_identity_that_resolves_outside_the_storage_root_creates_nothing_there()
    {
        var home = NewHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        var outside = Path.Combine(Path.GetTempPath(), $"ledger-escape-{Guid.NewGuid():N}");
        try
        {
            var resolved = await LedgerViewCommand.ResolveLedgerFilePathAsync(
                outside, roomDirectoryPath: null, TestContext.Current.CancellationToken);

            // Inside the root, by the slugged key -- and nothing at either path the raw string derives.
            Assert.StartsWith(BatonPaths.Root, resolved, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(BatonPaths.CostLedgerFile(RepositoryIdentity.FileSlugFor(outside.ToLowerInvariant())), resolved);
            Assert.False(Directory.Exists(outside));
            Assert.False(File.Exists(Path.Combine(outside, BatonPaths.CostLedgerFileName)));
            Assert.False(File.Exists($"{outside}.jsonl"));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(outside);
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    /// <summary>
    /// The other half of that arm, and the order it rests on: a key a ledger file really is filed under
    /// — the repository directory's own name, which is what <c>ls ~/.baton</c> shows — opens THAT file
    /// rather than the digest of the key. Pinned because the probe is what makes the arm above safe, and
    /// an edit that dropped it would still pass every parsing test in the tree.
    /// </summary>
    [Fact]
    public async Task A_repo_identity_naming_an_unmigrated_ledger_by_key_relocates_and_opens_that_file()
    {
        var home = NewHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            const string Key = "github-com-aer-works-baton-abcdef12";
            var legacy = BatonPaths.LegacyCostLedgerFile(Key);
            await CostLedgerStore.AppendAsync(
                [new CostLedgerEntry(
                    SourceKind: CostSourceKind.BatonExecution, Repository: "github.com/aer-works/baton", Execution: "exec-key")],
                legacy,
                TestContext.Current.CancellationToken);

            var resolved = await LedgerViewCommand.ResolveLedgerFilePathAsync(
                Key, roomDirectoryPath: null, TestContext.Current.CancellationToken);

            Assert.Equal(BatonPaths.CostLedgerFile(Key), resolved);
            Assert.False(File.Exists(legacy));
            Assert.Equal(
                "exec-key",
                Assert.Single(await CostLedgerStore.ReadAllAsync(resolved, TestContext.Current.CancellationToken)).Execution);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }
}
