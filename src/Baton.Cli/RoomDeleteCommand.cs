using Baton.Concurrency;
using Baton.Outcomes;
using Baton.Status;
using Baton.Vendors;

namespace Baton.Cli;

/// <summary>
/// <c>baton room delete</c> — the 2026-09-02 operator ruling spec/baton.md §8 records in full: Fleet
/// Glass's "dismiss" only ever hid a room from one browser's own localStorage, while the room
/// directory and its <c>room-registry.jsonl</c> lines persisted regardless. This verb removes both
/// local records.
/// </summary>
/// <remarks>
/// Not a <see cref="CommandResult"/>/<see cref="FlowStateReporter"/> command — deletion produces no
/// projected <see cref="Baton.Domain.FlowState"/> to report, the same shape <c>keep</c>/<c>unkeep</c>
/// already carved out. Handled directly in <c>Program.cs</c>.
/// </remarks>
public static class RoomDeleteCommand
{
    /// <summary>What one <c>baton room delete</c> (or one room deleted by <c>baton rooms prune</c>)
    /// actually removed — printed by the caller so an operator sees exactly what happened, never a
    /// blanket "done".</summary>
    public sealed record Result(
        string RoomDirectoryPath,
        bool DirectoryExisted,
        int RegistryLinesRemoved);

    /// <exception cref="CliArgumentException">
    /// The room has not reached a terminal state (no <c>terminal.json</c>) and <c>--force</c> was not
    /// given — spec/baton.md §8 states why that refusal exists (an engine that has not settled the
    /// room yet can still be writing into it).
    /// </exception>
    public static async Task<Result> ExecuteAsync(
        RoomDeleteOptions options, TextWriter output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);

        RefuseUnlessTerminalOrForced(options.RoomDirectoryPath, options.Force);
        var result = await DeleteAsync(options.RoomDirectoryPath, cancellationToken).ConfigureAwait(false);
        Print(result, output);
        return result;
    }

    /// <summary>What actually happened, printed so an operator sees the concrete removal rather than a
    /// blanket "done" — the same discipline <c>KeepCommand</c> already applies to its own one-line
    /// confirmation. Shared with <c>baton rooms prune</c>'s per-room reporting.</summary>
    internal static void Print(Result result, TextWriter output)
    {
        output.WriteLine(result.DirectoryExisted
            ? $"Removed room directory '{result.RoomDirectoryPath}'."
            : $"Room directory '{result.RoomDirectoryPath}' was already gone.");
        output.WriteLine(result.RegistryLinesRemoved > 0
            ? $"Removed {result.RegistryLinesRemoved} room-registry line(s)."
            : "No room-registry lines matched.");
    }

    /// <summary>
    /// The delete itself, shared with <c>baton rooms prune</c>'s batch path — both remove the same
    /// two things (the directory and its registry lines) once a caller has already established the
    /// room is safe to remove. Order matters only for the directory: it is deleted *first*, before
    /// the registry line, so a delete that dies mid-way (killed
    /// process, disk full) leaves a registry line pointing at a now-missing directory — a shape every
    /// reader already tolerates (<c>FleetStatusTool</c> drops it rather than surfacing a phantom room,
    /// and <c>RoomRegistryStore.CompactAsync</c>/<c>PreviewCompactionAsync</c> cleans it up on the very
    /// next <c>rooms prune</c>, including the automatic retention sweep, no human involved). The
    /// opposite order — registry first — trades that for a silent, disk-leaking failure mode:
    /// an orphaned directory with no registry line is invisible to <c>RoomsPruneCommand.FindCandidatesAsync</c>
    /// (registry-only, never a filesystem scan), so it can never become a delete candidate again and
    /// only a human manually re-running <c>baton room delete</c> against the exact path recovers it.
    /// </summary>
    internal static async Task<Result> DeleteAsync(
        string roomDirectoryPath, CancellationToken cancellationToken)
    {
        var directoryExisted = Directory.Exists(roomDirectoryPath);
        if (directoryExisted)
        {
            Directory.Delete(roomDirectoryPath, recursive: true);
        }

        var registryLinesRemoved = await RoomRegistryStore
            .RemoveByRoomPathAsync(BatonPaths.RoomRegistryFile, roomDirectoryPath, cancellationToken)
            .ConfigureAwait(false);

        return new Result(roomDirectoryPath, directoryExisted, registryLinesRemoved);
    }

    /// <summary>
    /// A room directory that no longer exists (already deleted by hand, or by a previous interrupted
    /// delete — see <see cref="DeleteAsync"/>'s own remarks) has nothing left for a live engine to hold,
    /// so it is always terminal for this check's purposes; it still reaches
    /// <see cref="DeleteAsync"/> to clean up any leftover registry line.
    /// </summary>
    internal static void RefuseUnlessTerminalOrForced(string roomDirectoryPath, bool force)
    {
        // F3 (2026-09-02 review): the conductor room is refused outright, --force included — unlike
        // the terminal-state refusal below, this is not a "not yet safe" check that --force can
        // override. `rooms prune --terminal` already excludes it by role (ConductorRoomDetector); a
        // forceless `room delete` was protected only incidentally (the room never gets a
        // terminal.json), so `--force` alone used to delete it outright.
        if (Directory.Exists(roomDirectoryPath) && ConductorRoomDetector.IsConductorRoom(roomDirectoryPath))
        {
            throw new CliArgumentException(
                $"Room '{roomDirectoryPath}' is the conductor room (role '{ConductorRoomDetector.ConductorRole}') "
                + "— refusing to delete it, even with --force.");
        }

        if (force || !Directory.Exists(roomDirectoryPath))
        {
            return;
        }

        var terminalSentinelPath = Path.Combine(roomDirectoryPath, TerminalSentinelWriter.TerminalSentinelFileName);
        if (File.Exists(terminalSentinelPath))
        {
            return;
        }

        // Same holder-liveness read `baton cancel` already uses (ConcurrencyGuard.ReadHolderInfo +
        // EngineLivenessProbe) — never a second, independently-invented liveness mechanism.
        var (holderDescription, holderPid, _, holderProcessStartTimeUtc) = ConcurrencyGuard.ReadHolderInfo(roomDirectoryPath);
        var holderProcessStartTime = holderProcessStartTimeUtc is { } startTimeUtc
            ? new DateTimeOffset(DateTime.SpecifyKind(startTimeUtc, DateTimeKind.Utc))
            : (DateTimeOffset?)null;
        var liveness = EngineLivenessProbe.Probe(holderPid, holderProcessStartTime);

        var holderClause = liveness.Status switch
        {
            EngineLivenessStatus.Alive =>
                $" A live engine (pid {holderPid}{(holderDescription is not null ? $", '{holderDescription}'" : string.Empty)}) currently holds it.",
            EngineLivenessStatus.Dead when holderDescription is not null =>
                $" Its last recorded holder ('{holderDescription}') is no longer running, but the room never reached a terminal state.",
            _ => string.Empty,
        };

        throw new CliArgumentException(
            $"Room '{roomDirectoryPath}' has not reached a terminal state (no {TerminalSentinelWriter.TerminalSentinelFileName}) " +
            $"— refusing to delete.{holderClause}",
            "pass --force to delete anyway.");
    }
}
