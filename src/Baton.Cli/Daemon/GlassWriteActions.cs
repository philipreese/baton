using Baton.Status;
using Baton.Vendors;

namespace Baton.Cli.Daemon;

/// <summary>Thin HTTP adapters over Baton's existing mutation commands; no state transition lives here.</summary>
internal static class GlassWriteActions
{
    internal static async Task<string> SetQueueHoldAsync(bool held, CancellationToken cancellationToken)
    {
        await QueueCommand.SetHoldAsync(held, TextWriter.Null, cancellationToken).ConfigureAwait(false);
        return held ? "Queue held." : "Queue resumed.";
    }

    internal static async Task<string> CancelRoomAsync(string roomId, CancellationToken cancellationToken)
    {
        if (!IsValidRoomId(roomId))
        {
            throw new CliArgumentException("The Glass cancel route requires one room id.");
        }

        var roomPath = Path.Combine(BatonPaths.Rooms, roomId);
        var result = await CancelCommand.ExecuteAsync(
                new CancelOptions(
                    roomPath,
                    ExecutionId: null,
                    BatonPaths.RoomBindingsFile(roomPath),
                    Reason: "Fleet Glass operator cancel"),
                WorkerAdapterRegistry.Default,
                cancellationToken)
            .ConfigureAwait(false);

        return result.CancelWasNoOp
            ? $"Room {roomId} was already settled."
            : result.CancellationQueued
                ? $"Cancellation requested for room {roomId}."
                : $"Room {roomId} cancelled.";
    }

    internal static bool IsValidRoomId(string? roomId) =>
        !string.IsNullOrWhiteSpace(roomId)
        && roomId is not "." and not ".."
        && string.Equals(roomId, Path.GetFileName(roomId), StringComparison.Ordinal)
        && roomId.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) < 0;
}
