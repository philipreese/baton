using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Status;

// Lives in Baton, not Baton.Vendors: fleet_status (Baton.Cli's `baton mcp`, #1458: ex-Baton.Mcp.Host)
// needs to read this registry
// and deliberately has no Baton.Vendors project reference. The namespace stays Baton.Vendors so it
// sits next to DaemonSettingsStore and the rest of AER's per-machine storage stores it is written from.
namespace Baton.Vendors;

/// <summary>
/// One registration of a room into the machine-local, multi-project registry (spec/baton.md §8):
/// the room's own directory, the project root it was dispatched for, and when the registration was
/// written. <see cref="RoomPath"/> is what <c>fleet_status</c> scans; <see cref="ProjectRoot"/> is
/// what lets it group rooms by project without a caller having to enumerate every project directory
/// as a <c>roots</c> entry.
/// </summary>
public sealed record RoomRegistryEntry(
    [property: JsonPropertyName("roomPath")] string RoomPath,
    [property: JsonPropertyName("projectRoot")] string ProjectRoot,
    [property: JsonPropertyName("createdAt")] DateTime CreatedAt);

/// <summary>
/// Reads and writes <see cref="BatonPaths.RoomRegistryFile"/> — the spec/baton.md §8 multi-project room registry.
/// </summary>
/// <remarks>
/// <para>
/// <b>Append-only, not a rewritten JSON map.</b> Registrations come from separate, potentially
/// concurrent <c>baton</c> processes (the very situation the registry exists for — spec/baton.md §8
/// carries the design rationale); appending sidesteps the cross-process read-modify-write a
/// rewritten map would force onto every registration.
/// </para>
/// <para>
/// <b>Every access is serialized by a named, machine-wide <see cref="Mutex"/>.</b> <c>FileMode.Append</c>
/// does <em>not</em> give atomic, non-interleaving writes across separate .NET processes on Windows —
/// spec/baton.md §8 records the measurement (unlocked concurrent appenders losing ~1/5 of their
/// lines, some corrupted into unparseable concatenations). <see cref="AppendAsync"/> itself opens with the narrower
/// <c>FileShare.Read</c> (an exclusive write lock, the same choice <see cref="Baton.Store.FlowEventLogWriter"/>
/// makes) — that alone stops the byte-level interleaving above, but it does not stop losses: without
/// the <see cref="Mutex"/>, a second concurrent writer would get a sharing-violation
/// <see cref="IOException"/> instead, which this type's fail-open contract requires swallowing (see
/// below) — a dropped registration rather than corrupted bytes, but still a room <c>fleet_status</c>
/// never learns about. A named <see cref="Mutex"/> keyed on <paramref name="registryFilePath"/> (via
/// the private <c>RunUnderLock</c>) makes at most one process touch the file at a time, for both reads
/// and writes, so a concurrent writer waits and then succeeds instead of losing its registration to a
/// sharing violation — which is what actually delivers "last-writer-wins per room, folded on read"
/// (<see cref="ReadDistinctByRoomAsync"/>) without a single registration lost.
/// The no-lost-entries guarantee is pinned by a many-writer test in <c>RoomRegistryStoreTests</c>.
/// </para>
/// <para>
/// <b>Why every critical section is synchronous, wrapped in one <c>Task.Run</c>, rather than async all
/// the way down.</b> <see cref="Mutex"/> ownership is thread-affine on Windows: the OS thread that
/// calls <see cref="Mutex.WaitOne()"/> is the only one allowed to call <see cref="Mutex.ReleaseMutex"/>.
/// An <c>await</c> between acquiring and releasing can resume on a different thread-pool thread with no
/// synchronization context to pin it — <c>Task.Run</c> still moved the initial call to a worker thread,
/// but that was caught in review: awaiting the
/// stream I/O in between made <c>ReleaseMutex</c> throw <c>"Object synchronization method was called
/// from an unsynchronized block of code"</c> under real concurrency. Keeping acquire, I/O, and release
/// in one synchronous delegate — no <c>await</c> inside it — guarantees they all run on the exact same
/// thread.
/// </para>
/// <para>
/// <b>Fails open, never gates.</b> The registry only ever <em>adds</em> coverage to
/// <c>fleet_status</c>'s existing directory scan (spec/baton.md §8) — it must never be the reason a
/// dispatch fails or a room goes unreported. A write failure, including a lock-acquire timeout, is the
/// caller's concern to log and swallow, not this type's to throw past; a malformed or missing file on
/// read resolves to whatever valid lines could still be parsed (or none), never an exception.
/// </para>
/// </remarks>
public static class RoomRegistryStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    /// <summary>
    /// How a caller waits for another process to finish its own registry access before giving up
    /// (#1942) — the one place this store's wait budget is stated; spec/baton.md §8 describes the shape
    /// and points here for the numbers rather than transcribing them.
    /// </summary>
    /// <remarks>
    /// Each critical section here is one small append or one whole-file read, so a single wait was
    /// originally set at a flat 30 s on the reasoning that contention past it meant something was
    /// genuinely wrong. Six or more live rooms falsified that: a lane's teardown write and a
    /// <c>baton resolve</c>'s read both hit the 30 s ceiling while the holder was doing ordinary work,
    /// and the same access succeeded ~20 s later — so the timeout was reporting a *transient queue*, not
    /// a wedged one, and turned a finished lane into a runner "timed out" line an operator had to
    /// resolve by hand. The budget is now three waits rather than one, which keeps every attempt long
    /// relative to the critical sections it guards while taking the total past 30 s, and the jittered
    /// gap between them desynchronizes a group of processes that arrived together
    /// (<see cref="LockWaitPolicy"/>'s own remarks carry why that gap is not equivalent to one longer
    /// wait). It stays bounded: an access that genuinely cannot be had still fails open, on this store's
    /// unchanged contract, roughly a minute in rather than never.
    /// </remarks>
    private static readonly LockWaitPolicy WaitPolicy = new(
        AttemptTimeout: TimeSpan.FromSeconds(20),
        MaxAttempts: 3,
        BackoffBase: TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// Appends one registration line to <paramref name="registryFilePath"/>, creating the file and its
    /// parent directory if neither exists yet. <paramref name="roomPath"/> and
    /// <paramref name="projectRoot"/> are normalized through <see cref="BatonPaths.RecordKey"/> so every
    /// reader compares registry entries against directory-scan results the same way every other
    /// per-directory record in AER already does.
    /// </summary>
    /// <param name="explicitRegister">
    /// #1657: a repro is not fleet. Without this, a <paramref name="roomPath"/> under the user temp
    /// directory or a project's own <c>.scratch*</c>/<c>.baton</c> directory (see
    /// <see cref="IsThrowawayReproPath"/>) is skipped rather than written — <c>baton run</c>'s
    /// <c>--register</c> flag is the one caller that sets this true for such a path on purpose. A room
    /// under <see cref="BatonPaths.Rooms"/> (every <c>baton dispatch</c>/<c>redispatch</c> room, and
    /// most deliberate <c>baton run --room-dir</c> invocations) is never skipped regardless of this flag.
    /// </param>
    /// <exception cref="IOException">
    /// Another process held the registry lock for the whole of <see cref="WaitPolicy"/>'s budget, every
    /// retry included. Callers (see <c>RunCommand.RegisterRoomAsync</c>, <c>DeliverCommand</c>) treat
    /// this the same as any other registry write failure: log and swallow, never fail the run.
    /// </exception>
    /// <exception cref="WaitHandleCannotBeOpenedException">
    /// A non-mutex kernel object already holds the lock's name — vanishingly unlikely (the name is a
    /// SHA-256 digest) but not impossible. Callers treat this exactly like <see cref="IOException"/>
    /// above: log and swallow.
    /// </exception>
    public static Task AppendAsync(
        string roomPath,
        string projectRoot,
        string registryFilePath,
        bool explicitRegister = false,
        CancellationToken cancellationToken = default) =>
        AppendAsync(roomPath, projectRoot, registryFilePath, explicitRegister, WaitPolicy, cancellationToken);

    /// <summary>
    /// Test-only seam (Baton.Vendors.Tests, via <c>InternalsVisibleTo</c>): the same append against an
    /// explicit <paramref name="waitPolicy"/>, so the contended-lock behaviour can be driven in
    /// milliseconds rather than by sleeping out the real <see cref="WaitPolicy"/> budget. No production
    /// caller passes one — the store has exactly one wait policy, stated above.
    /// </summary>
    internal static Task AppendAsync(
        string roomPath,
        string projectRoot,
        string registryFilePath,
        bool explicitRegister,
        LockWaitPolicy waitPolicy,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomPath);
        ArgumentException.ThrowIfNullOrEmpty(projectRoot);
        ArgumentException.ThrowIfNullOrEmpty(registryFilePath);

        var recordedRoomPath = BatonPaths.RecordKey(roomPath);
        if (!explicitRegister && IsThrowawayReproPath(recordedRoomPath))
        {
            Console.Error.WriteLine(
                $"Room registry: skipping '{roomPath}' (looks like a repro room, not fleet work). Pass --register to include it.");
            return Task.CompletedTask;
        }

        var directory = Path.GetDirectoryName(registryFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var entry = new RoomRegistryEntry(recordedRoomPath, BatonPaths.RecordKey(projectRoot), DateTime.UtcNow);
        var line = JsonSerializer.Serialize(entry, SerializerOptions);
        var bytes = Encoding.UTF8.GetBytes(line + "\n");

        return Task.Run(
            () =>
            {
                RunUnderLock(registryFilePath, waitPolicy, () =>
                {
                    // #1657: a bare `baton run` re-registering an unchanged room on every call through
                    // the pump (see the type remarks) would otherwise write an identical line every
                    // time -- skip when a line for this exact (RoomPath, ProjectRoot) pair is already
                    // present, so only an actual change (or a genuinely new room) grows the file.
                    if (File.Exists(registryFilePath) && IsAlreadyRegistered(registryFilePath, entry))
                    {
                        return;
                    }

                    using var stream = new FileStream(
                        registryFilePath, FileMode.Append, FileAccess.Write, FileShare.Read, bufferSize: 4096, useAsync: false);
                    stream.Write(bytes);
                    stream.Flush();
                });
            },
            cancellationToken);
    }

    /// <summary>
    /// #1657: true for a <paramref name="recordedRoomPath"/> (already run through
    /// <see cref="BatonPaths.RecordKey"/>) that reads as a throwaway repro room rather than fleet work —
    /// under the user temp directory, or carrying a <c>.scratch*</c>/<c>.baton</c> path segment. A path
    /// under <see cref="BatonPaths.Rooms"/> is never throwaway: that is every room's legitimate home,
    /// and it happens to contain a <c>.baton</c> segment of its own (<c>{UserProfile}/.baton/rooms</c>),
    /// which is exactly why that check runs first.
    /// </summary>
    private static bool IsThrowawayReproPath(string recordedRoomPath)
    {
        var homeRooms = BatonPaths.RecordKey(BatonPaths.Rooms);
        if (IsUnderOrEqual(recordedRoomPath, homeRooms))
        {
            return false;
        }

        var tempDirectory = BatonPaths.RecordKey(Path.GetTempPath());
        if (IsUnderOrEqual(recordedRoomPath, tempDirectory))
        {
            return true;
        }

        var segments = recordedRoomPath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(segment =>
            segment.Equals(".baton", StringComparison.OrdinalIgnoreCase)
            || segment.StartsWith(".scratch", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsUnderOrEqual(string candidate, string root) =>
        candidate.Equals(root, StringComparison.OrdinalIgnoreCase)
        || candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// #1657: reads <paramref name="registryFilePath"/> (already known to exist, and already inside the
    /// caller's <see cref="RunUnderLock{T}(string,Func{T})"/> section) looking for a line whose
    /// <see cref="RoomRegistryEntry.RoomPath"/> and <see cref="RoomRegistryEntry.ProjectRoot"/> both
    /// match <paramref name="candidate"/> exactly — <see cref="RoomRegistryEntry.CreatedAt"/> is
    /// deliberately excluded from the comparison, since it differs on every call by construction. A
    /// project-root *change* for an already-registered room path is not a duplicate and still appends,
    /// preserving <see cref="ReadDistinctByRoomAsync"/>'s last-writer-wins fold.
    /// </summary>
    private static bool IsAlreadyRegistered(string registryFilePath, RoomRegistryEntry candidate)
    {
        string text;
        try
        {
            text = File.ReadAllText(registryFilePath, Encoding.UTF8);
        }
        catch (IOException)
        {
            return false;
        }

        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            RoomRegistryEntry? existing;
            try
            {
                existing = JsonSerializer.Deserialize<RoomRegistryEntry>(line, SerializerOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (existing is not null
                && string.Equals(existing.RoomPath, candidate.RoomPath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(existing.ProjectRoot, candidate.ProjectRoot, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reads every entry in <paramref name="registryFilePath"/>, folded down to the last entry written
    /// for each distinct <see cref="RoomRegistryEntry.RoomPath"/> (append order is write order, so the
    /// last occurrence in the file is the last-writer-wins value). A missing file resolves to an empty
    /// list; a malformed or empty line is skipped rather than failing the whole read — one bad line
    /// must never hide every well-formed registration around it. Never throws: a lock-acquire timeout,
    /// an I/O failure, or a lock-name collision (see <see cref="WaitHandleCannotBeOpenedException"/> on
    /// <see cref="AppendAsync"/>) all resolve to an empty list, same as a missing file — the caller
    /// (<c>FleetStatusTool</c>) must never fail the whole call because the registry, which only ever
    /// adds coverage, could not be read.
    /// </summary>
    public static Task<IReadOnlyList<RoomRegistryEntry>> ReadDistinctByRoomAsync(
        string registryFilePath, CancellationToken cancellationToken = default) =>
        ReadDistinctByRoomAsync(registryFilePath, WaitPolicy, cancellationToken);

    /// <summary>
    /// Test-only seam (Baton.Vendors.Tests, via <c>InternalsVisibleTo</c>): the read counterpart of the
    /// <paramref name="waitPolicy"/>-taking <see cref="AppendAsync(string,string,string,bool,LockWaitPolicy,CancellationToken)"/>,
    /// for the same reason.
    /// </summary>
    internal static Task<IReadOnlyList<RoomRegistryEntry>> ReadDistinctByRoomAsync(
        string registryFilePath, LockWaitPolicy waitPolicy, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(registryFilePath);

        if (!File.Exists(registryFilePath))
        {
            return Task.FromResult<IReadOnlyList<RoomRegistryEntry>>([]);
        }

        return Task.Run(
            () =>
            {
                string text;
                try
                {
                    text = RunUnderLock(registryFilePath, waitPolicy, () =>
                    {
                        using var stream = new FileStream(registryFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                        using var reader = new StreamReader(stream, Encoding.UTF8);
                        return reader.ReadToEnd();
                    });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
                {
                    Console.Error.WriteLine($"Could not read the room registry at '{registryFilePath}': {ex.Message}.");
                    return (IReadOnlyList<RoomRegistryEntry>)[];
                }

                var byRoom = new Dictionary<string, RoomRegistryEntry>(BatonPaths.RecordKeyComparer);
                foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    RoomRegistryEntry? entry;
                    try
                    {
                        entry = JsonSerializer.Deserialize<RoomRegistryEntry>(line, SerializerOptions);
                    }
                    catch (JsonException)
                    {
                        continue;
                    }

                    if (entry is null || string.IsNullOrWhiteSpace(entry.RoomPath) || string.IsNullOrWhiteSpace(entry.ProjectRoot))
                    {
                        continue;
                    }

                    byRoom[entry.RoomPath] = entry;
                }

                return (IReadOnlyList<RoomRegistryEntry>)byRoom.Values.ToList();
            },
            cancellationToken);
    }

    /// <summary>
    /// #1659: removes every line whose <see cref="RoomRegistryEntry.RoomPath"/> matches
    /// <paramref name="roomPath"/> (compared through <see cref="BatonPaths.RecordKey"/>, the same
    /// normalization every other registry comparison uses) and rewrites the file under the same
    /// <see cref="Mutex"/> every other access takes — a delete is a writer like any other, and must
    /// serialize against a concurrent <see cref="AppendAsync"/> the same way. Returns the number of
    /// lines removed (0 for a missing file or a room path with no matching line — never throws for
    /// either, matching this type's fail-open contract on read).
    /// </summary>
    public static Task<int> RemoveByRoomPathAsync(
        string registryFilePath, string roomPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(registryFilePath);
        ArgumentException.ThrowIfNullOrEmpty(roomPath);

        if (!File.Exists(registryFilePath))
        {
            return Task.FromResult(0);
        }

        var recordedRoomPath = BatonPaths.RecordKey(roomPath);

        return Task.Run(
            () => RunUnderLock(registryFilePath, () =>
            {
                var (survivors, removedCount) = ReadAndFilter(
                    registryFilePath, entry => !string.Equals(entry.RoomPath, recordedRoomPath, StringComparison.OrdinalIgnoreCase));
                if (removedCount > 0)
                {
                    WriteAllLines(registryFilePath, survivors);
                }

                return removedCount;
            }),
            cancellationToken);
    }

    /// <summary>
    /// #1659: the compaction spec/baton.md §8 names as "left undone" — fold every entry down to
    /// last-writer-wins per <see cref="RoomRegistryEntry.RoomPath"/> (the same rule
    /// <see cref="ReadDistinctByRoomAsync"/> already applies at read time) and drop any entry whose
    /// room directory no longer exists on disk, then rewrite the file under the same <see cref="Mutex"/>
    /// every other access takes. <c>baton rooms prune</c> runs this on every invocation, gated by
    /// nothing — dedupe/missing-dir cleanup is registry hygiene, independent of the <c>--terminal</c>
    /// batch-delete filter. Returns (entries removed by dedupe, entries dropped for a missing
    /// directory); both are 0 for a missing or already-compact file.
    /// </summary>
    public static Task<(int DedupedCount, int MissingDirectoryCount)> CompactAsync(
        string registryFilePath, CancellationToken cancellationToken = default) =>
        CompactAsync(registryFilePath, write: true, cancellationToken);

    /// <summary>
    /// #1659: read-only counterpart of <see cref="CompactAsync(string,CancellationToken)"/> — computes
    /// the exact same (deduped, missing-directory) counts without rewriting the file. What
    /// <c>baton rooms prune</c>'s dry-run listing (the default, no <c>--yes</c>) calls, so the counts it
    /// prints match what <c>--yes</c> would actually do without mutating the registry to find out.
    /// </summary>
    public static Task<(int DedupedCount, int MissingDirectoryCount)> PreviewCompactionAsync(
        string registryFilePath, CancellationToken cancellationToken = default) =>
        CompactAsync(registryFilePath, write: false, cancellationToken);

    private static Task<(int DedupedCount, int MissingDirectoryCount)> CompactAsync(
        string registryFilePath, bool write, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(registryFilePath);

        if (!File.Exists(registryFilePath))
        {
            return Task.FromResult((0, 0));
        }

        return Task.Run(
            () => RunUnderLock(registryFilePath, () =>
            {
                var text = File.ReadAllText(registryFilePath, Encoding.UTF8);
                var originalCount = 0;
                var byRoom = new Dictionary<string, RoomRegistryEntry>(BatonPaths.RecordKeyComparer);
                foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    RoomRegistryEntry? entry;
                    try
                    {
                        entry = JsonSerializer.Deserialize<RoomRegistryEntry>(line, SerializerOptions);
                    }
                    catch (JsonException)
                    {
                        continue;
                    }

                    if (entry is null || string.IsNullOrWhiteSpace(entry.RoomPath) || string.IsNullOrWhiteSpace(entry.ProjectRoot))
                    {
                        continue;
                    }

                    originalCount++;
                    byRoom[entry.RoomPath] = entry;
                }

                var dedupedCount = originalCount - byRoom.Count;
                var survivors = byRoom.Values.Where(entry => Directory.Exists(entry.RoomPath)).ToList();
                var missingDirectoryCount = byRoom.Count - survivors.Count;

                if (write && (dedupedCount > 0 || missingDirectoryCount > 0))
                {
                    WriteAllLines(registryFilePath, survivors);
                }

                return (dedupedCount, missingDirectoryCount);
            }),
            cancellationToken);
    }

    /// <summary>
    /// Reads every parseable, well-formed line in <paramref name="registryFilePath"/> (already known to
    /// exist, and already inside the caller's <see cref="RunUnderLock{T}(string,Func{T})"/> section),
    /// returning the ones <paramref name="keep"/> selects alongside how many did not survive — a
    /// malformed line is silently dropped from both counts, matching this type's read tolerance
    /// elsewhere. Shared by <see cref="RemoveByRoomPathAsync"/>; <see cref="CompactAsync"/> has its own
    /// dedupe-then-filter pass instead, since it needs the pre-dedupe count too.
    /// </summary>
    private static (List<RoomRegistryEntry> Survivors, int RemovedCount) ReadAndFilter(
        string registryFilePath, Func<RoomRegistryEntry, bool> keep)
    {
        var text = File.ReadAllText(registryFilePath, Encoding.UTF8);
        var survivors = new List<RoomRegistryEntry>();
        var removedCount = 0;

        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            RoomRegistryEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<RoomRegistryEntry>(line, SerializerOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (entry is null || string.IsNullOrWhiteSpace(entry.RoomPath) || string.IsNullOrWhiteSpace(entry.ProjectRoot))
            {
                continue;
            }

            if (keep(entry))
            {
                survivors.Add(entry);
            }
            else
            {
                removedCount++;
            }
        }

        return (survivors, removedCount);
    }

    /// <summary>
    /// Replaces <paramref name="registryFilePath"/>'s entire contents with one JSON line per
    /// <paramref name="entries"/>, via a temp-file-then-move so a concurrent reader under the same
    /// <see cref="Mutex"/> never observes a truncated file — the same atomic-replace discipline
    /// <see cref="Baton.Status.TerminalSentinelWriter"/> uses for the same reason. Callers already hold
    /// the registry <see cref="Mutex"/>.
    /// </summary>
    private static void WriteAllLines(string registryFilePath, IReadOnlyList<RoomRegistryEntry> entries)
    {
        var tempPath = $"{registryFilePath}.{Guid.NewGuid():N}.tmp";
        var builder = new StringBuilder();
        foreach (var entry in entries)
        {
            builder.Append(JsonSerializer.Serialize(entry, SerializerOptions)).Append('\n');
        }

        File.WriteAllText(tempPath, builder.ToString(), Encoding.UTF8);
        File.Move(tempPath, registryFilePath, overwrite: true);
    }

    /// <summary>
    /// The lock name prefix <see cref="MutexGuardedFileLock"/> combines with a digest of the registry
    /// file path — kept as the exact literal this store always used before the extraction to
    /// <see cref="MutexGuardedFileLock"/> (#1570). That type's own remarks state why the literal must
    /// not move.
    /// </summary>
    private const string LockNamePrefix = "baton-room-registry";

    /// <summary>
    /// Runs <paramref name="action"/> holding a named <see cref="Mutex"/> keyed on
    /// <paramref name="registryFilePath"/>, so every process touching the same registry file — reader
    /// or writer — serializes against every other one. <see cref="MutexGuardedFileLock"/> (#1570) is
    /// the mechanism itself, shared with <see cref="QuotaLedgerStore"/> — this store's own remarks on
    /// why every critical section must stay synchronous, and on the digest, moved there with it.
    /// <paramref name="waitPolicy"/> is <see cref="WaitPolicy"/> for every production caller; the
    /// parameter exists so the two internal test seams above can reach the contended path cheaply.
    /// </summary>
    private static T RunUnderLock<T>(string registryFilePath, LockWaitPolicy waitPolicy, Func<T> action) =>
        MutexGuardedFileLock.RunUnderLock(registryFilePath, LockNamePrefix, waitPolicy, action);

    private static void RunUnderLock(string registryFilePath, LockWaitPolicy waitPolicy, Action action) =>
        MutexGuardedFileLock.RunUnderLock(registryFilePath, LockNamePrefix, waitPolicy, action);

    private static T RunUnderLock<T>(string registryFilePath, Func<T> action) =>
        RunUnderLock(registryFilePath, WaitPolicy, action);
}
