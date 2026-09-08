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
            var resolved = Assert.IsType<string>(CostLedgerLocation.ResolveForWrite(Repository.FileSlug).Path);

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

            var resolved = Assert.IsType<string>(CostLedgerLocation.ResolveForWrite(Repository.FileSlug).Path);

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

            var first = Assert.IsType<string>(CostLedgerLocation.ResolveForWrite(Repository.FileSlug).Path);
            var second = CostLedgerLocation.ResolveForRead(Repository.FileSlug);

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

            var resolved = CostLedgerLocation.ResolveForRead(Repository.FileSlug);

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
                CostLedgerLocation.ResolveForRead(Repository.FileSlug),
                CostLedgerLocation.ResolveForRead(other.FileSlug));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    /// <summary>
    /// The #2041 review's HIGH, pinned as the interleaving it describes: this process decides where to
    /// write while another holds the legacy file's mutex, THEN that holder completes the move, THEN this
    /// process appends. A resolver that answered the failed acquisition with the legacy path — including
    /// the incomplete "re-probe and return whichever exists" fix, whose probe is equally outside the
    /// append's critical section — recreates <c>{Root}/ledger/&lt;slug&gt;.jsonl</c> here and splits the
    /// repository's ledger for good.
    /// <para>
    /// So the assertion is on the FILES, not on the returned path: no legacy file exists afterwards, and
    /// the canonical one still holds exactly the row the move carried. Asserting only that the refusal
    /// came back would pass against a fix that still handed out a writable legacy path.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_write_resolved_while_another_holder_owns_the_lock_is_refused_and_recreates_no_legacy_file()
    {
        var home = NewHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var legacy = BatonPaths.LegacyCostLedgerFile(Repository.FileSlug);
            var canonical = BatonPaths.CostLedgerFile(Repository.FileSlug);
            await CostLedgerStore.AppendAsync(
                [Row("exec-legacy")], legacy, TestContext.Current.CancellationToken);

            using var holder = new LegacyLockHolder(legacy);

            // (1) A decides where to write while B holds the lock. The budget is a test-only overload of
            // the production one: 30 seconds of real waiting would buy nothing this assertion needs.
            var target = CostLedgerLocation.ResolveForWrite(Repository.FileSlug, TimeSpan.FromMilliseconds(150));

            Assert.Null(target.Path);
            Assert.NotNull(target.Refusal);
            Assert.Contains("could split this repository's ledger", target.Refusal, StringComparison.Ordinal);

            // (2) B completes the relocation and releases -- exactly the state A could not distinguish
            // from "the move never happened" while it was waiting.
            holder.RelocateAndRelease(canonical);

            // (3) A's caller honours the refusal. This is the whole contract: no path, no append.
            if (target.Path is { } path)
            {
                await CostLedgerStore.AppendAsync([Row("exec-after-refusal")], path, TestContext.Current.CancellationToken);
            }

            Assert.False(File.Exists(legacy));
            Assert.Equal(
                "exec-legacy",
                Assert.Single(await CostLedgerStore.ReadAllAsync(canonical, TestContext.Current.CancellationToken)).Execution);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    /// <summary>
    /// Both files present is a permanent, unchanging state — the legacy file is deliberately never
    /// removed — so resolving must not pay a lock acquisition for it on every command. Measured by
    /// holding that lock: an answer arrives anyway. The control is the test above, which holds the same
    /// lock in the one state where the resolver genuinely has something to move and is refused for it.
    /// </summary>
    [Fact]
    public async Task Both_files_present_answers_without_taking_the_legacy_lock()
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

            using var holder = new LegacyLockHolder(legacy);

            var target = CostLedgerLocation.ResolveForWrite(Repository.FileSlug, TimeSpan.FromMilliseconds(150));

            Assert.Equal(canonical, target.Path);
            Assert.Null(target.Refusal);
            Assert.True(File.Exists(legacy));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    /// <summary>
    /// Another process holding the legacy file's cost-ledger mutex, from a thread of its own —
    /// <see cref="Mutex"/> ownership is thread-affine, so the acquire and the release have to happen on
    /// one thread that is not the test's.
    /// </summary>
    private sealed class LegacyLockHolder : IDisposable
    {
        private readonly ManualResetEventSlim _release = new(false);
        private readonly Thread _thread;
        private string? _relocateTo;

        public LegacyLockHolder(string legacyFilePath)
        {
            var acquired = new ManualResetEventSlim(false);
            _thread = new Thread(() =>
            {
                using var mutex = new Mutex(
                    initiallyOwned: false,
                    name: MutexGuardedFileLock.BuildMutexName(legacyFilePath, CostLedgerStore.Ledger.LockNamePrefix));
                mutex.WaitOne();
                acquired.Set();
                _release.Wait();
                if (_relocateTo is { } destination)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Move(legacyFilePath, destination);
                }

                mutex.ReleaseMutex();
            })
            { IsBackground = true };

            _thread.Start();
            acquired.Wait();
        }

        /// <summary>Completes the racing process's move, then releases — step (2) of the interleaving.</summary>
        public void RelocateAndRelease(string canonicalFilePath)
        {
            _relocateTo = canonicalFilePath;
            _release.Set();
            _thread.Join();
        }

        public void Dispose()
        {
            _release.Set();
            _thread.Join();
            _release.Dispose();
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
