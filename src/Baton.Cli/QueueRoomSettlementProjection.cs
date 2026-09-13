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

        var settlement = await ReadSettlementAsync(roomDirectory, cancellationToken).ConfigureAwait(false);
        if (settlement is null)
        {
            return null;
        }

        var reason = settlement.Reason is { Length: > 0 } detail ? $": {detail}" : string.Empty;
        return $"  settlement: conductor {settlement.Kind.DisplayName()} ({settlement.Terminal.State}){reason}";
    }

    private static async Task<RoomSettlement?> ReadSettlementAsync(
        string roomDirectory,
        CancellationToken cancellationToken)
    {
        if (!RoomLedgerProbe.HasLedger(roomDirectory))
        {
            if (await TerminalSentinelWriter.TryReadAsync(roomDirectory, cancellationToken).ConfigureAwait(false) is null)
            {
                throw new QueueStoreException(
                $"Could not read durable terminal state for queue room '{roomDirectory}': no room ledger or terminal sentinel exists.");
            }

            // A terminal sentinel has no resolution event history. Preserve legacy queue output rather
            // than guessing that a successful/failed terminal state represents a particular command.
            return null;
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
            if (state.Status != WorkflowStatus.Terminal)
            {
                return null;
            }

            var resolution = events.OfType<FlowEvent.CaptureResolved>().LastOrDefault();
            if (resolution is null)
            {
                return null;
            }

            var kind = resolution.Accepted
                ? SettlementKind.AcceptedCapture
                : events.OfType<FlowEvent.ExecutionIndeterminate>()
                    .Any(indeterminate => indeterminate.ExecutionId == resolution.ExecutionId)
                    ? SettlementKind.Rejected
                    : SettlementKind.Closed;
            var terminal = WorkflowStatusProjector.Project(state, snapshot, roomDirectory, entries);
            return new RoomSettlement(kind, resolution.Reason, terminal);
        }
        catch (Exception ex) when (ex is SnapshotLoadException or FlowEventLogReadException or IOException or UnauthorizedAccessException)
        {
            throw new QueueStoreException($"Could not read durable terminal state for queue room '{roomDirectory}': {ex.Message}", ex);
        }
    }

    private sealed record RoomSettlement(SettlementKind Kind, string? Reason, WorkflowStatusView Terminal);

    private enum SettlementKind
    {
        Closed,
        Rejected,
        AcceptedCapture,
    }

    private static string DisplayName(this SettlementKind kind) => kind switch
    {
        SettlementKind.Closed => "closed",
        SettlementKind.Rejected => "rejected capture",
        SettlementKind.AcceptedCapture => "accepted capture",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
