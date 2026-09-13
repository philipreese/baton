using Baton.Domain;
using Baton.Projection;
using Baton.Queue;
using Baton.Status;
using Baton.Store;
using Baton.Templates;

namespace Baton.Cli;

/// <summary>
/// Reads the room-side terminal projection that qualifies a queue row's historical error.
/// </summary>
/// <remarks>
/// Queue rows retain the error that caused their transition for audit. This reader is deliberately
/// projection-only: it never rewrites that fact, and it is the one authority that decides whether a
/// subsequently conductor-resolved room makes the row's old resolution remedy stale.
/// </remarks>
internal static class QueueRoomSettlementProjection
{
    private const string AwaitingResolutionMarker = "awaiting conductor resolution";

    internal static async Task<string?> RenderAsync(
        QueueItem item,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.Error is not { Length: > 0 } error
            || !error.Contains(AwaitingResolutionMarker, StringComparison.Ordinal)
            || item.RoomDirectory is not { Length: > 0 } roomDirectory)
        {
            return null;
        }

        var terminal = await ReadTerminalAsync(roomDirectory, cancellationToken).ConfigureAwait(false);
        if (terminal?.ResolvedBy is not "conductor")
        {
            return null;
        }

        var reason = terminal.Error is { Length: > 0 } detail ? $": {detail}" : string.Empty;
        return $"  settlement: resolved by conductor ({terminal.State}){reason}";
    }

    private static async Task<WorkflowStatusView?> ReadTerminalAsync(
        string roomDirectory,
        CancellationToken cancellationToken)
    {
        if (!RoomLedgerProbe.HasLedger(roomDirectory))
        {
            var sentinel = await TerminalSentinelWriter.TryReadAsync(roomDirectory, cancellationToken).ConfigureAwait(false);
            return sentinel ?? throw new QueueStoreException(
                $"Could not read durable terminal state for queue room '{roomDirectory}': no room ledger or terminal sentinel exists.");
        }

        var snapshotPath = Path.Combine(roomDirectory, BatonPaths.SnapshotFileName);
        if (!File.Exists(snapshotPath))
        {
            throw new QueueStoreException(
                $"Could not read durable terminal state for queue room '{roomDirectory}': its ledger has no snapshot.");
        }

        try
        {
            var snapshot = await SnapshotBinder.LoadFromFileAsync(snapshotPath, cancellationToken).ConfigureAwait(false);
            var entries = await new FlowEventLogReader(Path.Combine(roomDirectory, BatonPaths.FlowLogFileName))
                .ReadAllEntriesWithTimestampsAsync(cancellationToken).ConfigureAwait(false);
            var events = entries.OfType<LogEntry.FlowLogEntry>().Select(entry => entry.Event).ToList();
            var state = StateProjector.Project(events, snapshot, ProjectionCheckpointStore.Load(roomDirectory));
            return state.Status == WorkflowStatus.Terminal
                ? WorkflowStatusProjector.Project(state, snapshot, roomDirectory, entries)
                : null;
        }
        catch (Exception ex) when (ex is SnapshotLoadException or FlowEventLogReadException or IOException or UnauthorizedAccessException)
        {
            throw new QueueStoreException($"Could not read durable terminal state for queue room '{roomDirectory}': {ex.Message}", ex);
        }
    }
}
