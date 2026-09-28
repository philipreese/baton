using Baton.Concurrency;
using Baton.Domain;
using Baton.Status;
using Baton.Store;

namespace Baton.Projection;

/// <summary>
/// Compacts a room's journal (<c>room.jsonl</c>) by dropping events belonging to COMPLETED runs (#972).
/// Follows existing seams (<see cref="RoomEventLogReader"/> and <see cref="RoomProjector"/>).
/// <para>
/// <b>Crash-Safe:</b> Rewrites retained entries to a temp file and atomically moves via <see cref="RetryingFileMove.Move"/>.
/// <b>Idempotent:</b> Running compaction twice in a row produces no changes on the second run.
/// <b>Scope:</b> Touches completed runs only (held work with <see cref="HeldWorkStatus.Resolved"/>).
/// Live and paused runs are untouched.
/// </para>
/// <para>
/// <b>Serialised against appenders by the room's own <see cref="ConcurrencyGuard"/></b>, held across
/// the whole read-rewrite-move — the same lock <c>RoomMutationInterface</c> takes to append. Not an
/// optimisation: this is the only rewriter of a file every other writer only appends to, so an
/// append landing between the read and the move would be dropped by the move with nothing left to
/// detect it. <see cref="RoomEventLogWriter"/>'s single-writer file sharing does not cover this,
/// because a compaction replaces the file rather than opening it for append.
/// </para>
/// </summary>
public static class RoomJournalCompactor
{
    /// <summary>
    /// Compacts the room journal at <paramref name="roomDirectoryPath"/> if present.
    /// Returns <c>true</c> if the journal was compacted (shrunk), or <c>false</c> if no compaction was needed.
    /// </summary>
    public static async Task<bool> CompactAsync(
        string roomDirectoryPath,
        CancellationToken cancellationToken = default)
        => await CompactAsync(roomDirectoryPath, cancellationToken, beforeReplace: null).ConfigureAwait(false);

    /// <summary>Test seam for the read/replace window; production passes null.</summary>
    internal static async Task<bool> CompactAsync(
        string roomDirectoryPath,
        CancellationToken cancellationToken,
        Func<Task>? beforeReplace)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);

        var roomLogPath = Path.Combine(roomDirectoryPath, BatonPaths.RoomLogFileName);
        if (!File.Exists(roomLogPath))
        {
            return false;
        }

        using var guard = ConcurrencyGuard.AcquireRoomEvents(roomDirectoryPath, "room journal compaction");

        var reader = new RoomEventLogReader(roomLogPath);
        var events = await reader.ReadAllRoomEventsAsync(cancellationToken).ConfigureAwait(false);

        if (events.Count == 0)
        {
            return false;
        }

        var roomState = RoomProjector.Project(events);
        var completedRefs = roomState.HeldWork
            .Where(kv => kv.Value.Status == HeldWorkStatus.Resolved)
            .Select(kv => kv.Key)
            .ToHashSet();

        if (completedRefs.Count == 0)
        {
            return false;
        }

        var rawLines = OrchestratorSessionStore.ReadRoomLogLines(roomDirectoryPath);
        if (rawLines.Length != events.Count)
        {
            // Defensive posture: if line count does not match parsed events count, do not compact
            return false;
        }

        var retainedLines = new List<string>(rawLines.Length);
        for (int i = 0; i < events.Count; i++)
        {
            var @event = events[i];
            if (IsEventOfCompletedRun(@event, completedRefs))
            {
                continue;
            }

            retainedLines.Add(rawLines[i]);
        }

        if (retainedLines.Count == rawLines.Length)
        {
            return false;
        }

        var tempFilePath = roomLogPath + ".tmp." + Guid.NewGuid().ToString("n");
        var textContent = retainedLines.Count > 0 ? string.Join('\n', retainedLines) + "\n" : string.Empty;

        try
        {
            await File.WriteAllTextAsync(tempFilePath, textContent, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // RetryingFileMove's own deleteSourceOnFinalFailure only covers a failing MOVE. A write
            // that fails or is cancelled partway leaves its uniquely-named temp beside the journal
            // with nothing that would ever collect it.
            TryDeleteTemp(tempFilePath);
            throw;
        }

        try
        {
            if (beforeReplace is not null) await beforeReplace().ConfigureAwait(false);
            RetryingFileMove.Move(tempFilePath, roomLogPath, overwrite: true, deleteSourceOnFinalFailure: true);
        }
        catch
        {
            TryDeleteTemp(tempFilePath);
            throw;
        }
        return true;
    }

    /// <summary>
    /// Best-effort, and deliberately so: the journal itself is already safe at this point (untouched),
    /// so a temp file that resists deletion must not turn a survivable write failure into a second one.
    /// </summary>
    private static void TryDeleteTemp(string tempFilePath)
    {
        try
        {
            if (File.Exists(tempFilePath))
            {
                File.Delete(tempFilePath);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool IsEventOfCompletedRun(RoomEvent @event, HashSet<HeldWorkRef> completedRefs)
    {
        return @event switch
        {
            RoomEvent.HeldWorkDispatched dispatched => completedRefs.Contains(dispatched.Ref),
            RoomEvent.HeldWorkEscalated escalated => completedRefs.Contains(escalated.Ref),
            RoomEvent.HeldWorkResolved resolved => completedRefs.Contains(resolved.Ref),
            RoomEvent.EscalationRaised escalation => escalation.Subject is EscalationSubject.HeldWork(var @ref) && completedRefs.Contains(@ref),
            _ => false,
        };
    }
}

