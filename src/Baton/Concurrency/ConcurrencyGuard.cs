using System.Diagnostics;
using System.Text.Json;

namespace Baton.Concurrency;

/// <summary>
/// Enforces: at most one Flow instance may mutate a given room's workflow state at a
/// time. Backed by a kernel-held advisory file lock (<see cref="FileShare.None"/> on a
/// <see cref="FileStream"/>) scoped to the room's own directory — deliberately not a sentinel
/// file, whose mere existence would signal "locked" and would survive a crash requiring manual
/// clearing. The OS releases a <see cref="FileStream"/>'s lock the instant its owning process
/// exits, crashed or not, so a crashed holder never leaves a stale lock behind.
/// <para>
/// 0053: two independent lock files per room directory, one per log. <c>flow.lock</c> serializes
/// the engine and <c>flow.jsonl</c> writers; <c>room-events.lock</c> serializes <c>room.jsonl</c>
/// appends (and the daemon's <c>bindings.json</c> read-modify-writes). The single-writer
/// guarantee holds per log; #186 forbids replay from depending on cross-log order, so nothing may
/// rely on the accidental cross-log serialization the former single lock provided.
/// </para>
/// <para>
/// #618: Writes a sibling sidecar file <c>flow.lock.holder</c> on successful acquire so readers can name
/// the lock holder. A stale sidecar beside a FREE lock is harmless by construction: readers only
/// consult it when an acquire has just failed against a live holder, and every new holder rewrites it.
/// </para>
/// </summary>
public sealed class ConcurrencyGuard : IDisposable
{
    /// <summary>Mirrors <see cref="Baton.Status.BatonPaths.FlowLockFileName"/> — see that member's
    /// remarks for why the name lives there and the mechanism here.</summary>
    public const string FlowLockFileName = Baton.Status.BatonPaths.FlowLockFileName;
    public const string FlowHolderFileName = "flow.lock.holder";
    public const string RoomEventsLockFileName = "room-events.lock";
    public const string RoomEventsHolderFileName = "room-events.lock.holder";

    private readonly FileStream _lockStream;
    private readonly string? _sidecarPath;

    private ConcurrencyGuard(FileStream lockStream, string? sidecarPath = null)
    {
        _lockStream = lockStream;
        _sidecarPath = sidecarPath;
    }

    /// <summary>
    /// Acquires the flow lock for <paramref name="roomDirectoryPath"/>, creating the directory first
    /// if it does not yet exist.
    /// </summary>
    /// <exception cref="WorkflowLockedException">
    /// Another Flow instance already holds the lock for this room.
    /// </exception>
    public static ConcurrencyGuard Acquire(string roomDirectoryPath, string? holderDescription = null)
    {
        return AcquireCore(roomDirectoryPath, FlowLockFileName, FlowHolderFileName, holderDescription);
    }

    /// <summary>Acquire an already existing flow lock without creating a room or lock file.
    /// Archive capture uses this when a concurrently removed room must stay removed.</summary>
    public static ConcurrencyGuard AcquireExisting(string roomDirectoryPath, string? holderDescription = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        var lockPath = Path.Combine(roomDirectoryPath, FlowLockFileName);
        try
        {
            var stream = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return CreateWithSidecar(stream, roomDirectoryPath, FlowHolderFileName, holderDescription);
        }
        catch (IOException ex)
        {
            var holder = TryReadHolderInfoCore(roomDirectoryPath, FlowHolderFileName);
            throw new WorkflowLockedException(BuildLockedMessage(roomDirectoryPath, holder, FlowLockFileName),
                ex, holder?.HolderDescription, holder?.AcquiredAtUtc);
        }
    }

    /// <summary>
    /// Acquires the room-events lock for <paramref name="roomDirectoryPath"/>, creating the directory first
    /// if it does not yet exist.
    /// </summary>
    /// <exception cref="WorkflowLockedException">
    /// Another Flow instance already holds the room-events lock for this room.
    /// </exception>
    public static ConcurrencyGuard AcquireRoomEvents(string roomDirectoryPath, string? holderDescription = null)
    {
        return AcquireCore(roomDirectoryPath, RoomEventsLockFileName, RoomEventsHolderFileName, holderDescription);
    }

    private static ConcurrencyGuard AcquireCore(string roomDirectoryPath, string lockFileName, string holderFileName, string? holderDescription)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);

        Directory.CreateDirectory(roomDirectoryPath);
        var lockFilePath = Path.Combine(roomDirectoryPath, lockFileName);

        FileStream lockStream;
        try
        {
            lockStream = new FileStream(lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            var holder = TryReadHolderInfoCore(roomDirectoryPath, holderFileName);
            throw new WorkflowLockedException(BuildLockedMessage(roomDirectoryPath, holder, lockFileName), ex, holder?.HolderDescription, holder?.AcquiredAtUtc);
        }

        return CreateWithSidecar(lockStream, roomDirectoryPath, holderFileName, holderDescription);
    }

    /// <summary>
    /// Acquires the flow lock like <see cref="Acquire"/>, but retries a lost race until
    /// <paramref name="within"/> elapses instead of failing on the first attempt.
    /// <para>
    /// This is opt-in, and <see cref="Acquire"/> deliberately stays fail-fast: for an
    /// <c>baton run</c> pump, losing the lock means another pump owns this room and waiting for it is
    /// exactly the wrong behaviour. What this exists for is the opposite case — a holder known to
    /// let go in milliseconds, where failing fast turns a routine overlap into a user-visible
    /// error. #857: the room sweep takes this same bounded shape while escalating a newly-appeared
    /// memory proposal, so an operator's approve/reject could lose a coin-flip to a background tick
    /// and be refused, with nothing wrong and nothing to retry but the click. (Since 0053 the sweep
    /// contends on the room-events lock via <see cref="AcquireRoomEventsWithin"/>; the rationale is
    /// the mechanism's, not one lock file's.)
    /// </para>
    /// <para>
    /// Bounded rather than indefinite on purpose. A genuinely stuck holder must still surface as a
    /// failure; the budget is sized to cover a routine overlap, not to hide one that is not
    /// routine.
    /// </para>
    /// </summary>
    /// <exception cref="WorkflowLockedException">
    /// The lock was still held when <paramref name="within"/> ran out.
    /// </exception>
    public static ConcurrencyGuard AcquireWithin(string roomDirectoryPath, TimeSpan within, string? holderDescription = null)
    {
        return AcquireWithinCore(roomDirectoryPath, within, FlowLockFileName, FlowHolderFileName, holderDescription);
    }

    /// <summary>
    /// Acquires the room-events lock like <see cref="AcquireRoomEvents"/>, but retries a lost race until
    /// <paramref name="within"/> elapses instead of failing on the first attempt. Same
    /// fail-fast-vs-bounded rationale as <see cref="AcquireWithin"/>.
    /// </summary>
    /// <exception cref="WorkflowLockedException">
    /// The room-events lock was still held when <paramref name="within"/> ran out.
    /// </exception>
    public static ConcurrencyGuard AcquireRoomEventsWithin(string roomDirectoryPath, TimeSpan within, string? holderDescription = null)
    {
        return AcquireWithinCore(roomDirectoryPath, within, RoomEventsLockFileName, RoomEventsHolderFileName, holderDescription);
    }

    private static ConcurrencyGuard AcquireWithinCore(string roomDirectoryPath, TimeSpan within, string lockFileName, string holderFileName, string? holderDescription)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);

        Directory.CreateDirectory(roomDirectoryPath);
        var lockFilePath = Path.Combine(roomDirectoryPath, lockFileName);

        // Stopwatch, not DateTime.UtcNow: a wall clock can step backwards (an NTP correction, a
        // manual change) and silently stretch this wait well past its budget. Monotonic is what a
        // deadline actually wants.
        var elapsed = Stopwatch.StartNew();

        while (true)
        {
            try
            {
                var lockStream = new FileStream(lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return CreateWithSidecar(lockStream, roomDirectoryPath, holderFileName, holderDescription);
            }
            catch (IOException) when (elapsed.Elapsed < within)
            {
                // Thread.Sleep rather than Task.Delay: the caller may be on a starved pool, and a
                // retry that cannot be scheduled is a retry that does not happen.
                Thread.Sleep(TimeSpan.FromMilliseconds(25));
            }
            catch (IOException ex)
            {
                var holder = TryReadHolderInfoCore(roomDirectoryPath, holderFileName);
                var message = $"{BuildLockedMessage(roomDirectoryPath, holder, lockFileName)} Still held after waiting " +
                    $"{within.TotalMilliseconds:0}ms, so this is not a routine overlap.";

                // The OS holder probe costs hundreds of milliseconds, so it runs only here — the
                // budget is already spent and the holder is by definition anomalous. Acquire's
                // fail-fast refusal never pays for it: a routine sweep-vs-pump overlap must be
                // refused immediately (#857), and the sidecar above already names a cooperative
                // holder there.
                if (Store.FileHolderProbe.IsSharingViolation(ex))
                {
                    message += $" Current holder: {Store.FileHolderProbe.DescribeHolders(lockFilePath)}";
                }

                throw new WorkflowLockedException(message, ex, holder?.HolderDescription, holder?.AcquiredAtUtc);
            }
        }
    }

    private static ConcurrencyGuard CreateWithSidecar(FileStream lockStream, string roomDirectoryPath, string holderFileName, string? holderDescription)
    {
        var sidecarPath = Path.Combine(roomDirectoryPath, holderFileName);
        var description = holderDescription ?? DefaultHolderDescription();
        try
        {
            // #1604 F1: ProcessStartTimeUtc, not AcquiredAtUtc, is what a liveness probe's
            // pid-recycling discriminator needs — AcquiredAtUtc is when THIS lock was won, which is
            // almost always well over a second after the holding process itself started, so feeding
            // it to EngineLivenessProbe.Probe (a PROCESS START TIME check, ±1s) reads every real
            // holder as dead. Captured beside Pid so a reader never has to reconstruct it from a
            // pid alone.
            using var self = Process.GetCurrentProcess();
            var processStartTimeUtc = self.StartTime.ToUniversalTime();
            var info = new LockHolderInfo(description, Environment.ProcessId, DateTime.UtcNow, processStartTimeUtc);
            var json = JsonSerializer.Serialize(info);
            File.WriteAllText(sidecarPath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort: a failed sidecar write must not fail the acquire — the lock is real
            // even when the label write loses a race (Windows throws UnauthorizedAccessException,
            // not IOException, for several of the transient-handle cases here).
        }

        return new ConcurrencyGuard(lockStream, sidecarPath);
    }

    private static string DefaultHolderDescription() =>
        // A value the current process can always supply — the callers that know a better name
        // (the baton run pump) pass one explicitly.
        $"{Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "process"} (pid {Environment.ProcessId})";

    /// <summary>
    /// #857: the base message does not assert a single cause. It used to name "a live 'baton run'
    /// pump" as the likely holder, which predates rooms and is wrong in a case an operator can
    /// hit; it also does not name the room sweep specifically, because most callers lock a
    /// per-execution room directory no sweep ever touches. The lock file itself still cannot say
    /// who won it — what changed with #618 is the sidecar beside it: when a holder wrote one, its
    /// self-description is appended here, and the two-shapes wording stays as the fallback for a
    /// holder that did not (or whose write lost a race). Since 0053 there are two lock files per
    /// directory, so the message names which one (<paramref name="lockFileName"/>) was contended.
    /// </summary>
    private static string BuildLockedMessage(string roomDirectoryPath, LockHolderInfo? holder, string lockFileName)
    {
        var baseMsg = $"Directory '{roomDirectoryPath}' is already locked ('{lockFileName}') by another Flow instance — either a live " +
            "'baton run' pump, or a background component that takes this directory's lock briefly (a room's " +
            "memory-proposal sweep does this while escalating a new proposal).";

        if (string.Equals(lockFileName, FlowLockFileName, StringComparison.Ordinal))
        {
            baseMsg += " Against a live pump specifically, 'baton cancel' from a second terminal (#1495) falls through to a room-scoped " +
                "cancel.request file that pump polls on its own — not immediate, and not delivered at all if this hold turns out to be " +
                "the brief, unrelated kind instead of a live pump.";
        }

        if (holder != null && !string.IsNullOrWhiteSpace(holder.HolderDescription))
        {
            return $"{baseMsg} Currently held by: {holder.HolderDescription} since {holder.AcquiredAtUtc:O}.";
        }

        return baseMsg;
    }

    /// <summary>
    /// Reads the holder sidecar file <c>flow.lock.holder</c> for <paramref name="roomDirectoryPath"/> if present and readable.
    /// Tolerates absence/unreadability by returning null for all four fields. <c>Pid</c> is what
    /// #1586's dead-holder check (<c>Baton.Cli.CancelCommand</c>) reads before ever calling
    /// <see cref="Acquire"/> — the sidecar records it unconditionally (<see cref="LockHolderInfo"/>'s
    /// <c>Pid</c> is always <see cref="Environment.ProcessId"/>, never the caller-supplied
    /// description), so it is trustworthy even when <c>HolderDescription</c> is a generic default.
    /// <c>ProcessStartTimeUtc</c> (#1604) is that same check's PROCESS START TIME discriminator —
    /// distinct from <c>AcquiredAtUtc</c>, which is when the lock itself was won, not when the
    /// holding process started — and reads null against a sidecar written before #1604 (an old
    /// holder's JSON simply has no such property), which a caller must treat as Unknown, not Dead.
    /// </summary>
    public static (string? HolderDescription, int? Pid, DateTime? AcquiredAtUtc, DateTime? ProcessStartTimeUtc) ReadHolderInfo(string roomDirectoryPath)
    {
        return ReadHolderInfoCore(roomDirectoryPath, FlowHolderFileName);
    }

    /// <summary>
    /// Reads the holder sidecar file <c>room-events.lock.holder</c> for <paramref name="roomDirectoryPath"/> if present and readable.
    /// Tolerates absence/unreadability by returning null for both fields.
    /// </summary>
    public static (string? HolderDescription, DateTime? AcquiredAtUtc) ReadRoomEventsHolderInfo(string roomDirectoryPath)
    {
        var (description, _, acquiredAtUtc, _) = ReadHolderInfoCore(roomDirectoryPath, RoomEventsHolderFileName);
        return (description, acquiredAtUtc);
    }

    private static (string? HolderDescription, int? Pid, DateTime? AcquiredAtUtc, DateTime? ProcessStartTimeUtc) ReadHolderInfoCore(string roomDirectoryPath, string holderFileName)
    {
        var info = TryReadHolderInfoCore(roomDirectoryPath, holderFileName);
        return (info?.HolderDescription, info?.Pid, info?.AcquiredAtUtc, info?.ProcessStartTimeUtc);
    }

    private static LockHolderInfo? TryReadHolderInfoCore(string roomDirectoryPath, string holderFileName)
    {
        try
        {
            var sidecarPath = Path.Combine(roomDirectoryPath, holderFileName);
            if (File.Exists(sidecarPath))
            {
                var text = File.ReadAllText(sidecarPath);
                return JsonSerializer.Deserialize<LockHolderInfo>(text);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // The three ways a sidecar can be unreadable mid-race: held/vanished (IO), ACL-denied
            // (Windows reports it as UnauthorizedAccess), or half-written (Json). All collapse to
            // "the holder did not say", which the caller renders honestly; anything else is a real
            // bug and propagates.
        }

        return null;
    }

    /// <summary>
    /// Reports whether another live holder currently owns the flow lock for
    /// <paramref name="roomDirectoryPath"/>, without acquiring it and without creating the
    /// directory or the lock file. A read-only probe: callers that need the lock still go through
    /// <see cref="Acquire"/>. A missing <c>flow.lock</c> (or a non-existent directory) means no
    /// holder. A lock file left on disk by a previously-released guard is deliberately <em>not</em>
    /// treated as a hold — only the live <see cref="FileShare.None"/> stream carries
    /// meaning, not the file's existence — so this opens the file to test the OS-held lock rather
    /// than reading its mere presence.
    /// </summary>
    public static bool IsHeld(string roomDirectoryPath)
    {
        return IsHeldCore(roomDirectoryPath, FlowLockFileName);
    }

    /// <summary>
    /// Reports whether another live holder currently owns the room-events lock for
    /// <paramref name="roomDirectoryPath"/>, without acquiring it and without creating the
    /// directory or the lock file. Same read-only-probe semantics as <see cref="IsHeld"/>: only
    /// the live OS hold carries meaning, never the file's existence.
    /// </summary>
    public static bool IsRoomEventsHeld(string roomDirectoryPath)
    {
        return IsHeldCore(roomDirectoryPath, RoomEventsLockFileName);
    }

    private static bool IsHeldCore(string roomDirectoryPath, string lockFileName)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);

        var lockFilePath = Path.Combine(roomDirectoryPath, lockFileName);
        if (!File.Exists(lockFilePath))
        {
            return false;
        }

        try
        {
            using var probe = new FileStream(lockFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    /// <summary>
    /// Releases the lock. The lock file itself is deliberately left on disk — under the guarantee
    /// above, only the OS-held lock carries meaning, not the file's existence — so a
    /// subsequent acquire for the same room directory succeeds immediately.
    /// The holder sidecar is removed best-effort first, while the lock is still held, so no reader
    /// ever sees this holder's label on a lock it has already released; a delete that loses a race
    /// leaves only the stale-beside-free-lock case the class doc calls harmless by construction.
    /// </summary>
    public void Dispose()
    {
        if (_sidecarPath != null)
        {
            try
            {
                if (File.Exists(_sidecarPath))
                {
                    File.Delete(_sidecarPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort for the same reason the write is: the release must not fail over a label.
            }
        }

        _lockStream.Dispose();
    }

    private sealed record LockHolderInfo(string HolderDescription, int Pid, DateTime AcquiredAtUtc, DateTime? ProcessStartTimeUtc = null);
}
