using Baton.Vendors.Tests.TestSupport;
using Baton.Status;

namespace Baton.Vendors.Tests;

/// <summary>
/// spec/baton.md §8's writer/reader: <see cref="RoomRegistryStore"/> is the machine-local
/// multi-project room registry <c>fleet_status</c> unions with its own directory scan
/// (<c>FleetStatusToolTests</c> covers that union; this file covers the store in isolation).
/// </summary>
[Collection(ConsoleErrorCaptureCollection.Name)]
public class RoomRegistryStoreTests
{
    private static string TempRegistryPath() =>
        Path.Combine(Path.GetTempPath(), $"baton-room-registry-{Guid.NewGuid():N}.jsonl");

    [Fact]
    public async Task Reading_a_missing_file_resolves_to_an_empty_list()
    {
        var path = TempRegistryPath();

        var entries = await RoomRegistryStore.ReadDistinctByRoomAsync(path, TestContext.Current.CancellationToken);

        Assert.Empty(entries);
    }

    [Fact]
    public async Task Appending_then_reading_round_trips_the_room_and_project_root()
    {
        var path = TempRegistryPath();
        try
        {
            var roomDir = Path.Combine(Path.GetTempPath(), $"room-{Guid.NewGuid():N}");
            var projectDir = Path.Combine(Path.GetTempPath(), $"project-{Guid.NewGuid():N}");

            await RoomRegistryStore.AppendAsync(
                roomDir, projectDir, path, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);
            var entries = await RoomRegistryStore.ReadDistinctByRoomAsync(path, TestContext.Current.CancellationToken);

            var entry = Assert.Single(entries);
            Assert.Equal(BatonPaths.RecordKey(roomDir), entry.RoomPath);
            Assert.Equal(BatonPaths.RecordKey(projectDir), entry.ProjectRoot);
            Assert.True(entry.CreatedAt > DateTime.UtcNow.AddMinutes(-1));
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task Appending_creates_the_parent_directory_if_it_does_not_exist_yet()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"baton-registry-dir-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "room-registry.jsonl");
        try
        {
            await RoomRegistryStore.AppendAsync(
                "C:/room", "C:/project", path, cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(File.Exists(path));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public async Task Repeated_registrations_of_the_same_room_fold_to_the_last_write()
    {
        var path = TempRegistryPath();
        try
        {
            var roomDir = Path.Combine(Path.GetTempPath(), $"room-{Guid.NewGuid():N}");
            var firstProject = Path.Combine(Path.GetTempPath(), $"project-a-{Guid.NewGuid():N}");
            var secondProject = Path.Combine(Path.GetTempPath(), $"project-b-{Guid.NewGuid():N}");

            await RoomRegistryStore.AppendAsync(
                roomDir, firstProject, path, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);
            await RoomRegistryStore.AppendAsync(
                roomDir, secondProject, path, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);
            var entries = await RoomRegistryStore.ReadDistinctByRoomAsync(path, TestContext.Current.CancellationToken);

            var entry = Assert.Single(entries);
            Assert.Equal(BatonPaths.RecordKey(secondProject), entry.ProjectRoot);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    /// <summary>
    /// The reason <see cref="RoomRegistryStore"/> serializes every access behind a named
    /// <see cref="Mutex"/>: <c>FileMode.Append</c> alone is not atomic across concurrent writers on
    /// Windows — measured directly during review, six separate processes each appending under
    /// <c>FileMode.Append</c>/<c>FileShare.ReadWrite</c> with no lock lost roughly a fifth of their
    /// lines to interleaved, unterminated writes. Many concurrent <c>baton dispatch</c> invocations
    /// writing to one shared registry file is exactly the scenario the registry exists to serve, so
    /// this drives a real, if in-process, instance of that concurrency at the store's public API and
    /// asserts every registration survives.
    /// </summary>
    [Fact]
    public async Task Concurrent_appends_from_many_tasks_lose_no_entries()
    {
        var path = TempRegistryPath();
        try
        {
            const int writerCount = 50;
            var roomDirs = Enumerable.Range(0, writerCount)
                .Select(i => Path.Combine(Path.GetTempPath(), $"room-concurrent-{i}-{Guid.NewGuid():N}"))
                .ToList();

            await Task.WhenAll(roomDirs.Select(roomDir => Task.Run(() =>
                RoomRegistryStore.AppendAsync(
                    roomDir, "C:/project", path, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken))));

            var entries = await RoomRegistryStore.ReadDistinctByRoomAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(writerCount, entries.Count);
            var foundRoomPaths = entries.Select(e => e.RoomPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.All(roomDirs, roomDir => Assert.Contains(BatonPaths.RecordKey(roomDir), foundRoomPaths));
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task A_malformed_line_is_skipped_without_hiding_the_well_formed_entries_around_it()
    {
        var path = TempRegistryPath();
        try
        {
            var roomDir = Path.Combine(Path.GetTempPath(), $"room-{Guid.NewGuid():N}");
            var projectDir = Path.Combine(Path.GetTempPath(), $"project-{Guid.NewGuid():N}");
            await RoomRegistryStore.AppendAsync(
                roomDir, projectDir, path, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);
            await File.AppendAllTextAsync(path, "{ not valid json\n", TestContext.Current.CancellationToken);

            var otherRoomDir = Path.Combine(Path.GetTempPath(), $"room-{Guid.NewGuid():N}");
            var otherProjectDir = Path.Combine(Path.GetTempPath(), $"project-{Guid.NewGuid():N}");
            await RoomRegistryStore.AppendAsync(
                otherRoomDir, otherProjectDir, path, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);

            var entries = await RoomRegistryStore.ReadDistinctByRoomAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(2, entries.Count);
            Assert.Contains(entries, e => e.RoomPath == BatonPaths.RecordKey(roomDir));
            Assert.Contains(entries, e => e.RoomPath == BatonPaths.RecordKey(otherRoomDir));
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    /// <summary>
    /// #1657: the mechanism the issue reports — a lane's manual repro room under <c>%TEMP%</c> ends up
    /// on the fleet glass with nothing that will ever drive it. Skipping the write at the source is the
    /// registry-side half of the fix; the reader-side half (a registry entry whose room directory no
    /// longer exists is dropped) is <c>FleetStatusToolTests.RegistryEntry_WhoseRoomDirectoryWasDeleted_IsSkippedRatherThanErroring</c>
    /// in <c>Baton.Cli.Tests</c>.
    /// </summary>
    [Fact]
    public async Task Appending_a_room_under_the_temp_directory_is_skipped_and_reported_on_stderr()
    {
        var path = TempRegistryPath();
        var originalError = Console.Error;
        try
        {
            var stderr = new StringWriter();
            Console.SetError(stderr);

            var roomDir = Path.Combine(Path.GetTempPath(), $"manual-repro-{Guid.NewGuid():N}", "task");

            await RoomRegistryStore.AppendAsync(
                roomDir, "C:/project", path, cancellationToken: TestContext.Current.CancellationToken);

            var entries = await RoomRegistryStore.ReadDistinctByRoomAsync(path, TestContext.Current.CancellationToken);
            Assert.Empty(entries);
            Assert.Contains("Room registry: skipping", stderr.ToString());
        }
        finally
        {
            Console.SetError(originalError);
            FileCleanup.Delete(path);
        }
    }

    /// <summary>
    /// A project's own <c>.scratch*</c>/<c>.baton</c> directory (e.g. a bare <c>baton run</c>'s
    /// default room directory, <c>{cwd}/.baton/{workflow}</c>) is the second throwaway shape the issue
    /// names — <c>w1513\.baton\test-room</c> was one of the thirteen hand-pruned entries.
    /// </summary>
    [Theory]
    [InlineData(".baton")]
    [InlineData(".scratch-vp")]
    [InlineData(".scratch-verify-pack")]
    public async Task Appending_a_room_under_a_scratch_or_baton_project_directory_is_skipped(string scratchSegment)
    {
        var path = TempRegistryPath();
        try
        {
            var projectDir = Path.Combine(Path.GetTempPath(), $"project-{Guid.NewGuid():N}");
            var roomDir = Path.Combine(projectDir, scratchSegment, "task");

            await RoomRegistryStore.AppendAsync(
                roomDir, projectDir, path, cancellationToken: TestContext.Current.CancellationToken);

            var entries = await RoomRegistryStore.ReadDistinctByRoomAsync(path, TestContext.Current.CancellationToken);
            Assert.Empty(entries);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task Appending_a_throwaway_repro_room_with_explicitRegister_is_recorded()
    {
        var path = TempRegistryPath();
        try
        {
            var roomDir = Path.Combine(Path.GetTempPath(), $"manual-repro-{Guid.NewGuid():N}", "task");
            var projectDir = Path.Combine(Path.GetTempPath(), $"project-{Guid.NewGuid():N}");

            await RoomRegistryStore.AppendAsync(
                roomDir, projectDir, path, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);

            var entries = await RoomRegistryStore.ReadDistinctByRoomAsync(path, TestContext.Current.CancellationToken);
            var entry = Assert.Single(entries);
            Assert.Equal(BatonPaths.RecordKey(roomDir), entry.RoomPath);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    /// <summary>
    /// A room under <see cref="BatonPaths.Rooms"/> is never a repro, however its default temp-backed
    /// test isolation is exercised elsewhere: this specifically pins that a literal <c>.baton/rooms</c>
    /// path segment (which every home room carries) does not itself trip the <c>.baton</c> scratch
    /// exclusion.
    /// </summary>
    [Fact]
    public async Task Appending_a_room_under_the_home_rooms_directory_is_never_skipped()
    {
        var path = TempRegistryPath();
        var homeRoom = Path.Combine(BatonPaths.Rooms, $"room-{Guid.NewGuid():N}");
        try
        {
            await RoomRegistryStore.AppendAsync(
                homeRoom, "C:/project", path, cancellationToken: TestContext.Current.CancellationToken);

            var entries = await RoomRegistryStore.ReadDistinctByRoomAsync(path, TestContext.Current.CancellationToken);
            var entry = Assert.Single(entries);
            Assert.Equal(BatonPaths.RecordKey(homeRoom), entry.RoomPath);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    /// <summary>
    /// #1657: the registry "also does not dedupe" — the same (room, project) pair appended twice grew
    /// the file by one line every time. A project-root *change* for the same room path is still a real
    /// update and still appends (<see cref="Repeated_registrations_of_the_same_room_fold_to_the_last_write"/>).
    /// </summary>
    [Fact]
    public async Task Appending_an_identical_room_and_project_twice_writes_only_one_line()
    {
        var path = TempRegistryPath();
        try
        {
            var roomDir = Path.Combine(Path.GetTempPath(), $"room-{Guid.NewGuid():N}");
            var projectDir = Path.Combine(Path.GetTempPath(), $"project-{Guid.NewGuid():N}");

            await RoomRegistryStore.AppendAsync(
                roomDir, projectDir, path, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);
            await RoomRegistryStore.AppendAsync(
                roomDir, projectDir, path, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);

            var lineCount = (await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken))
                .Count(line => !string.IsNullOrWhiteSpace(line));
            Assert.Equal(1, lineCount);

            var entries = await RoomRegistryStore.ReadDistinctByRoomAsync(path, TestContext.Current.CancellationToken);
            Assert.Single(entries);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    // #1659: RemoveByRoomPathAsync backs `baton room delete`'s registry-line removal.
    [Fact]
    public async Task RemoveByRoomPathAsync_RemovesEveryLineForThatRoom_AndReturnsTheCount()
    {
        var path = TempRegistryPath();
        try
        {
            var roomDir = Path.Combine(Path.GetTempPath(), $"room-{Guid.NewGuid():N}");
            var otherRoomDir = Path.Combine(Path.GetTempPath(), $"room-{Guid.NewGuid():N}");
            var projectDir = Path.Combine(Path.GetTempPath(), $"project-{Guid.NewGuid():N}");

            // Two lines for roomDir (a project-root change re-appends, #1657) plus one unrelated room.
            await RoomRegistryStore.AppendAsync(roomDir, projectDir, path, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);
            await RoomRegistryStore.AppendAsync(roomDir, projectDir + "2", path, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);
            await RoomRegistryStore.AppendAsync(otherRoomDir, projectDir, path, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);

            var removedCount = await RoomRegistryStore.RemoveByRoomPathAsync(path, roomDir, TestContext.Current.CancellationToken);

            Assert.Equal(2, removedCount);
            var remaining = await RoomRegistryStore.ReadDistinctByRoomAsync(path, TestContext.Current.CancellationToken);
            var survivor = Assert.Single(remaining);
            Assert.Equal(BatonPaths.RecordKey(otherRoomDir), survivor.RoomPath);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task RemoveByRoomPathAsync_NoMatchingLine_ReturnsZero_AndLeavesTheFileUntouched()
    {
        var path = TempRegistryPath();
        try
        {
            var roomDir = Path.Combine(Path.GetTempPath(), $"room-{Guid.NewGuid():N}");
            await RoomRegistryStore.AppendAsync(roomDir, "C:/project", path, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);

            var removedCount = await RoomRegistryStore.RemoveByRoomPathAsync(
                path, Path.Combine(Path.GetTempPath(), "no-such-room"), TestContext.Current.CancellationToken);

            Assert.Equal(0, removedCount);
            Assert.Single(await RoomRegistryStore.ReadDistinctByRoomAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task RemoveByRoomPathAsync_MissingFile_ReturnsZero_NeverThrows()
    {
        var path = TempRegistryPath();
        var removedCount = await RoomRegistryStore.RemoveByRoomPathAsync(path, "C:/no-such-room", TestContext.Current.CancellationToken);
        Assert.Equal(0, removedCount);
    }

    // #1659: CompactAsync backs `baton rooms prune`'s unconditional registry-hygiene pass —
    // spec/baton.md §8's "left undone" compaction.
    [Fact]
    public async Task CompactAsync_DedupesAndDropsMissingDirectories_AndRewritesTheFile()
    {
        var path = TempRegistryPath();
        var keptRoomDir = Path.Combine(Path.GetTempPath(), $"baton-registry-kept-{Guid.NewGuid():N}");
        Directory.CreateDirectory(keptRoomDir);
        try
        {
            var missingRoomDir = Path.Combine(Path.GetTempPath(), $"baton-registry-missing-{Guid.NewGuid():N}");
            // Two raw lines for keptRoomDir (a duplicate registration, #1657's "does not dedupe" gap)
            // plus one line for a directory that was never created — CompactAsync must fold the first
            // pair to one survivor and drop the second entirely.
            await RoomRegistryStore.AppendAsync(keptRoomDir, "C:/project", path, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);
            await RoomRegistryStore.AppendAsync(keptRoomDir, "C:/project2", path, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);
            await RoomRegistryStore.AppendAsync(missingRoomDir, "C:/project", path, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);

            var (dedupedCount, missingDirectoryCount) = await RoomRegistryStore.CompactAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(1, dedupedCount);
            Assert.Equal(1, missingDirectoryCount);
            var remaining = await RoomRegistryStore.ReadDistinctByRoomAsync(path, TestContext.Current.CancellationToken);
            var survivor = Assert.Single(remaining);
            Assert.Equal(BatonPaths.RecordKey(keptRoomDir), survivor.RoomPath);
            var lineCount = (await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken)).Count(line => !string.IsNullOrWhiteSpace(line));
            Assert.Equal(1, lineCount);
        }
        finally
        {
            FileCleanup.Delete(path);
            DirectoryCleanup.DeleteRecursively(keptRoomDir);
        }
    }

    [Fact]
    public async Task PreviewCompactionAsync_ReportsTheSameCounts_ButNeverWritesTheFile()
    {
        var path = TempRegistryPath();
        try
        {
            var missingRoomDir = Path.Combine(Path.GetTempPath(), $"baton-registry-missing-{Guid.NewGuid():N}");
            await RoomRegistryStore.AppendAsync(missingRoomDir, "C:/project", path, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);
            await RoomRegistryStore.AppendAsync(missingRoomDir, "C:/project2", path, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);

            var beforeText = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            var (dedupedCount, missingDirectoryCount) = await RoomRegistryStore.PreviewCompactionAsync(path, TestContext.Current.CancellationToken);
            var afterText = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(1, dedupedCount);
            Assert.Equal(1, missingDirectoryCount);
            Assert.Equal(beforeText, afterText);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    /// <summary>
    /// #1942, the retry policy's own budget scaled down to milliseconds: many short attempts rather than
    /// three twenty-second ones, so a test can drive the contended path without sleeping out the real
    /// budget. The <em>shape</em> is what these tests pin — attempt, back off, attempt again — not the
    /// production numbers, which live on <c>RoomRegistryStore.WaitPolicy</c> alone.
    /// </summary>
    private static readonly LockWaitPolicy FastRetryPolicy = new(
        AttemptTimeout: TimeSpan.FromMilliseconds(100), MaxAttempts: 60, BackoffBase: TimeSpan.FromMilliseconds(20));

    /// <summary>
    /// The control arm's policy: exactly one attempt, which is what every registry access did before
    /// #1942. Same held lock, same duration — the only variable is whether the caller retries.
    /// </summary>
    private static readonly LockWaitPolicy SingleAttemptPolicy =
        LockWaitPolicy.Single(TimeSpan.FromMilliseconds(100));

    /// <summary>How long <see cref="HeldRegistryLock"/> stays held before the arms that release it do.</summary>
    private static readonly TimeSpan HoldDuration = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// #1942: a lane's teardown write while a sibling process holds the registry lock — the shape
    /// <c>RoomRegistryStore.WaitPolicy</c>'s remarks record the measurement for. With the retry policy
    /// the append waits the holder out and the registry line lands.
    /// <see cref="Appending_under_a_lock_held_past_a_single_attempt_still_throws_without_the_retry"/>
    /// is the control: identical fixture, one attempt, and it throws.
    /// </summary>
    [Fact]
    public async Task Appending_under_a_held_lock_lands_once_the_holder_releases()
    {
        var path = TempRegistryPath();
        var holder = new HeldRegistryLock(path);
        try
        {
            var roomDir = Path.Combine(Path.GetTempPath(), $"room-{Guid.NewGuid():N}");

            var append = RoomRegistryStore.AppendAsync(
                roomDir, "C:/project", path, explicitRegister: true, FastRetryPolicy, TestContext.Current.CancellationToken);

            // Outlasts several whole attempts, so the append is provably retrying rather than sitting in
            // one long wait — then hand the lock over.
            await Task.Delay(HoldDuration, TestContext.Current.CancellationToken);
            Assert.False(append.IsCompleted, "The append cannot have finished while the lock was still held.");
            holder.Release();

            await append;

            var entries = await RoomRegistryStore.ReadDistinctByRoomAsync(path, TestContext.Current.CancellationToken);
            var entry = Assert.Single(entries);
            Assert.Equal(BatonPaths.RecordKey(roomDir), entry.RoomPath);
        }
        finally
        {
            holder.Dispose();
            FileCleanup.Delete(path);
        }
    }

    /// <summary>
    /// The control for <see cref="Appending_under_a_held_lock_lands_once_the_holder_releases"/>: the
    /// pre-#1942 single-attempt wait, against the same held lock, throws — which is what made the
    /// harness's result about the retry rather than about the fixture failing to contend at all.
    /// </summary>
    [Fact]
    public async Task Appending_under_a_lock_held_past_a_single_attempt_still_throws_without_the_retry()
    {
        var path = TempRegistryPath();
        var holder = new HeldRegistryLock(path);
        try
        {
            var roomDir = Path.Combine(Path.GetTempPath(), $"room-{Guid.NewGuid():N}");

            await Assert.ThrowsAsync<IOException>(() => RoomRegistryStore.AppendAsync(
                roomDir, "C:/project", path, explicitRegister: true, SingleAttemptPolicy, TestContext.Current.CancellationToken));
        }
        finally
        {
            holder.Dispose();
            FileCleanup.Delete(path);
        }
    }

    /// <summary>
    /// The uncontended arm: with nothing holding the lock, the retry policy changes nothing — the first
    /// attempt takes it and the append lands, well inside a single attempt's slice.
    /// </summary>
    [Fact]
    public async Task Appending_with_the_lock_free_takes_it_on_the_first_attempt()
    {
        var path = TempRegistryPath();
        try
        {
            var roomDir = Path.Combine(Path.GetTempPath(), $"room-{Guid.NewGuid():N}");

            await RoomRegistryStore.AppendAsync(
                roomDir, "C:/project", path, explicitRegister: true, FastRetryPolicy, TestContext.Current.CancellationToken);

            var entries = await RoomRegistryStore.ReadDistinctByRoomAsync(path, FastRetryPolicy, TestContext.Current.CancellationToken);
            var entry = Assert.Single(entries);
            Assert.Equal(BatonPaths.RecordKey(roomDir), entry.RoomPath);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    /// <summary>
    /// #1942's third reported occurrence was a *read*, not a teardown write: <c>baton resolve --close</c>
    /// with five live rooms reported the 30 s registry timeout, and the same command succeeded twenty
    /// seconds later. A read fails open rather than throwing, so the damage is quieter and worse — the
    /// registry silently resolves to no entries. The retry waits the holder out and returns the real
    /// entry instead.
    /// </summary>
    [Fact]
    public async Task Reading_under_a_held_lock_returns_the_entries_once_the_holder_releases()
    {
        var path = TempRegistryPath();
        var roomDir = Path.Combine(Path.GetTempPath(), $"room-{Guid.NewGuid():N}");
        await RoomRegistryStore.AppendAsync(
            roomDir, "C:/project", path, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);

        var holder = new HeldRegistryLock(path);
        try
        {
            var read = RoomRegistryStore.ReadDistinctByRoomAsync(path, FastRetryPolicy, TestContext.Current.CancellationToken);

            await Task.Delay(HoldDuration, TestContext.Current.CancellationToken);
            Assert.False(read.IsCompleted, "The read cannot have finished while the lock was still held.");
            holder.Release();

            var entry = Assert.Single(await read);
            Assert.Equal(BatonPaths.RecordKey(roomDir), entry.RoomPath);
        }
        finally
        {
            holder.Dispose();
            FileCleanup.Delete(path);
        }
    }

    /// <summary>
    /// The read control, and the polarity the arm above needs to mean anything: one attempt against the
    /// same held lock resolves to *no entries* and one stderr line, which is exactly the silent coverage
    /// loss #1942 reported.
    /// </summary>
    [Fact]
    public async Task Reading_under_a_lock_held_past_a_single_attempt_still_degrades_to_empty_without_the_retry()
    {
        var path = TempRegistryPath();
        var roomDir = Path.Combine(Path.GetTempPath(), $"room-{Guid.NewGuid():N}");
        await RoomRegistryStore.AppendAsync(
            roomDir, "C:/project", path, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);

        var holder = new HeldRegistryLock(path);
        var originalError = Console.Error;
        try
        {
            var stderr = new StringWriter();
            Console.SetError(stderr);

            var entries = await RoomRegistryStore.ReadDistinctByRoomAsync(
                path, SingleAttemptPolicy, TestContext.Current.CancellationToken);

            Assert.Empty(entries);
            Assert.Contains("Could not read the room registry", stderr.ToString());
        }
        finally
        {
            Console.SetError(originalError);
            holder.Dispose();
            FileCleanup.Delete(path);
        }
    }

    /// <summary>
    /// Holds the real registry <see cref="Mutex"/> for <paramref name="registryFilePath"/> on a thread
    /// of its own — mutex ownership is thread-affine, and the thread that waits must be the one that
    /// releases, so this cannot be an <c>await</c>-based holder. The lock name is rebuilt from the same
    /// prefix literal <c>MutexGuardedFileLockTests</c> pins, deliberately rather than reaching for
    /// <c>RoomRegistryStore</c>'s private constant: a test that renamed itself alongside the production
    /// literal would stop contending with the shipped store at all and pass on an uncontended lock.
    /// </summary>
    private sealed class HeldRegistryLock : IDisposable
    {
        private const string RegistryLockNamePrefix = "baton-room-registry";
        private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(30);

        private readonly Thread thread;
        private readonly ManualResetEventSlim acquired = new(false);
        private readonly ManualResetEventSlim releaseRequested = new(false);

        internal HeldRegistryLock(string registryFilePath)
        {
            var mutexName = MutexGuardedFileLock.BuildMutexName(registryFilePath, RegistryLockNamePrefix);
            thread = new Thread(() =>
            {
                using var mutex = new Mutex(initiallyOwned: false, name: mutexName);
                mutex.WaitOne();
                acquired.Set();
                releaseRequested.Wait();
                mutex.ReleaseMutex();
            })
            {
                IsBackground = true,
                Name = "held-registry-lock",
            };
            thread.Start();
            Assert.True(acquired.Wait(JoinTimeout), "The holder thread never acquired the registry lock.");
        }

        /// <summary>Hands the lock back and waits for the holder thread to actually let go of it.</summary>
        internal void Release()
        {
            releaseRequested.Set();
            Assert.True(thread.Join(JoinTimeout), "The holder thread never released the registry lock.");
        }

        public void Dispose()
        {
            releaseRequested.Set();
            thread.Join(JoinTimeout);
            acquired.Dispose();
            releaseRequested.Dispose();
        }
    }
}
