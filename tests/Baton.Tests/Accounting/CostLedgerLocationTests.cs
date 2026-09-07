using System.Text.Json;
using Baton.Accounting;
using Baton.Status;

namespace Baton.Tests.Accounting;

/// <summary>
/// #2041: the cost ledger moved under the per-repository directory spec/baton.md §12's Q3 layout
/// already files the memory store in, and a legacy-location ledger is RELOCATED onto it rather than
/// read in place.
/// <para>
/// Every test here runs against a fixture root through <c>BatonEnvironmentSnapshot.BeginScope</c> —
/// never the operator's real <c>~/.baton</c>, and never a process-environment mutation (#1496).
/// </para>
/// </summary>
public sealed class CostLedgerLocationTests
{
    private static readonly RepositoryIdentity Repository =
        RepositoryIdentity.From("https://github.com/aer-works/baton.git", null)!;

    private static string NewHome() =>
        Path.Combine(Path.GetTempPath(), $"cost-ledger-home-{Guid.NewGuid():N}");

    private static CostLedgerEntry Row(string executionId) =>
        new(SourceKind: CostSourceKind.BatonExecution, Repository: Repository.Value, Execution: executionId);

    /// <summary>
    /// The relocation's destination, and the property the issue asks for in its own terms: a write with
    /// nothing at the old location lands INSIDE the repository directory, beside where
    /// <c>BatonPaths.MemoryDirectory</c> would put that repository's memory store — not under a
    /// per-concern <c>ledger/</c> directory keyed by slug.
    /// </summary>
    [Fact]
    public async Task A_new_write_lands_under_the_repository_directory_beside_the_memory_store()
    {
        var home = NewHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var resolved = CostLedgerLocation.Resolve(Repository.FileSlug);

            Assert.Equal(BatonPaths.CostLedgerFile(Repository.FileSlug), resolved);
            Assert.Equal(
                BatonPaths.RepositoryDirectory(Repository.FileSlug),
                Path.GetDirectoryName(resolved));
            Assert.Equal(
                Path.GetDirectoryName(BatonPaths.MemoryDirectory(Repository.FileSlug)),
                Path.GetDirectoryName(resolved));

            await CostLedgerStore.AppendAsync(
                [Row("exec-new-write")], resolved, TestContext.Current.CancellationToken);

            Assert.True(File.Exists(BatonPaths.CostLedgerFile(Repository.FileSlug)));
            Assert.False(File.Exists(BatonPaths.LegacyCostLedgerFile(Repository.FileSlug)));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    /// <summary>
    /// The migration itself. Asserting only "the rows come back" would pass on a revert — a
    /// pre-#2041 build reads the legacy file perfectly well — so this pins the four facts that are
    /// true ONLY after the relocation: the old file is gone, the new one holds the rows, the resolver
    /// returns the new one, and the move is recorded in the manifest.
    /// </summary>
    [Fact]
    public async Task A_legacy_location_ledger_is_relocated_onto_the_repository_directory_and_recorded()
    {
        var home = NewHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var legacy = BatonPaths.LegacyCostLedgerFile(Repository.FileSlug);
            await CostLedgerStore.AppendAsync(
                [Row("exec-legacy")], legacy, TestContext.Current.CancellationToken);
            Assert.True(File.Exists(legacy));

            var resolved = CostLedgerLocation.Resolve(Repository.FileSlug);

            Assert.Equal(BatonPaths.CostLedgerFile(Repository.FileSlug), resolved);
            Assert.False(File.Exists(legacy));
            Assert.True(File.Exists(resolved));
            Assert.Equal(
                "exec-legacy",
                Assert.Single(await CostLedgerStore.ReadAllAsync(resolved, TestContext.Current.CancellationToken)).Execution);

            var relocation = Assert.Single(ReadManifest());
            Assert.Equal(Repository.FileSlug, relocation.Repository);
            Assert.Equal(legacy, relocation.From);
            Assert.Equal(resolved, relocation.To);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    /// <summary>
    /// Resolve runs on every settle and every read, so it must be idempotent: the second call takes the
    /// no-legacy-file fast path and writes no second manifest line. A resolver that recorded on every
    /// call would grow the manifest once per baton command forever.
    /// </summary>
    [Fact]
    public async Task Resolving_twice_relocates_once_and_records_once()
    {
        var home = NewHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            await CostLedgerStore.AppendAsync(
                [Row("exec-idempotent")],
                BatonPaths.LegacyCostLedgerFile(Repository.FileSlug),
                TestContext.Current.CancellationToken);

            var first = CostLedgerLocation.Resolve(Repository.FileSlug);
            var second = CostLedgerLocation.Resolve(Repository.FileSlug);

            Assert.Equal(first, second);
            Assert.Single(await CostLedgerStore.ReadAllAsync(second, TestContext.Current.CancellationToken));
            Assert.Single(ReadManifest());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    /// <summary>
    /// The accepted loss spec/baton.md §7 states, pinned rather than left to be discovered: with both
    /// files present the canonical one is returned and the legacy one is left byte-identical and
    /// unmerged. The polarity that matters is that the canonical file is NOT overwritten or appended to
    /// — merging would mean re-serializing rows the move exists to carry through untouched.
    /// </summary>
    [Fact]
    public async Task A_legacy_file_beside_an_existing_canonical_one_is_left_untouched_and_unmerged()
    {
        var home = NewHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var legacy = BatonPaths.LegacyCostLedgerFile(Repository.FileSlug);
            var canonical = BatonPaths.CostLedgerFile(Repository.FileSlug);
            await CostLedgerStore.AppendAsync([Row("exec-old")], legacy, TestContext.Current.CancellationToken);
            await CostLedgerStore.AppendAsync([Row("exec-current")], canonical, TestContext.Current.CancellationToken);
            var legacyBytes = await File.ReadAllBytesAsync(legacy, TestContext.Current.CancellationToken);

            var resolved = CostLedgerLocation.Resolve(Repository.FileSlug);

            Assert.Equal(canonical, resolved);
            Assert.Equal(legacyBytes, await File.ReadAllBytesAsync(legacy, TestContext.Current.CancellationToken));
            Assert.Equal(
                "exec-current",
                Assert.Single(await CostLedgerStore.ReadAllAsync(canonical, TestContext.Current.CancellationToken)).Execution);
            Assert.Empty(ReadManifest());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    /// <summary>
    /// Two repositories still resolve to two ledgers — the property #1849 files under a repository key
    /// for, restated against the new layout because the slug moved from the filename to the directory
    /// name and a layout that collapsed there would pool two repositories into one file.
    /// </summary>
    [Fact]
    public void Two_repositories_resolve_to_two_files()
    {
        var home = NewHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var other = RepositoryIdentity.From("https://github.com/aer-works/other.git", null)!;

            Assert.NotEqual(
                CostLedgerLocation.Resolve(Repository.FileSlug),
                CostLedgerLocation.Resolve(other.FileSlug));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    private static IReadOnlyList<CostLedgerLocation.CostLedgerRelocation> ReadManifest()
    {
        var path = BatonPaths.CostLedgerMigrationFile;
        if (!File.Exists(path))
        {
            return [];
        }

        return File.ReadAllLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonSerializer.Deserialize<CostLedgerLocation.CostLedgerRelocation>(line)!)
            .ToList();
    }
}
