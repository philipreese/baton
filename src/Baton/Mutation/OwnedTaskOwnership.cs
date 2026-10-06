using Baton.Domain;
using Baton.Queue;
using Baton.Status;
using Baton.Store;
using System.Text.Json;

namespace Baton.Mutation;

/// <summary>Resolves declarations against admission or exact immutable execution evidence.</summary>
public static class OwnedTaskOwnership
{
    public static async Task<OwnedTaskExecutionIdentity?> ResolveAsync(
        string room, OwnedTaskExecutionIdentity? declaration,
        IReadOnlyList<FlowEvent> events, OwnedTaskExecutionPredecessor? predecessor,
        CancellationToken cancellationToken)
    {
        OwnedTaskExecutionIdentity? inherited = null;
        if (predecessor is not null && !SameRoom(room, predecessor.RoomDirectory)
            && !await HasContinuationProvenanceAsync(room, predecessor, cancellationToken).ConfigureAwait(false))
            throw new InvalidOwnedTaskIdentityException(
                "Owned-task predecessor has no matching engine-established continuation or redispatch provenance; ownership remains unknown.");
        // The external reference establishes the first child acceptance. Later downstream work
        // inherits the child room's immutable owner, including a newly admitted child attempt.
        if (predecessor is not null && !SameRoom(room, predecessor.RoomDirectory)
            && events.OfType<FlowEvent.ExecutionRequestAccepted>().Any(e => e.Request.OwnedTaskIdentity is not null))
            predecessor = null;
        var hasPredecessor = predecessor is not null;
        if (predecessor is not null)
        {
            var parentEvents = SameRoom(room, predecessor.RoomDirectory)
                ? events
                : await new FlowEventLogReader(Path.Combine(predecessor.RoomDirectory, BatonPaths.FlowLogFileName))
                    .ReadAllAsync(cancellationToken).ConfigureAwait(false);
            var parents = parentEvents.OfType<FlowEvent.ExecutionRequestAccepted>()
                .Where(e => e.Request.ExecutionId.Value == predecessor.ExecutionId).ToList();
            if (parents.Count != 1)
                throw new InvalidOwnedTaskIdentityException("Owned-task predecessor must identify exactly one accepted execution.");
            inherited = parents[0].Request.OwnedTaskIdentity;
            if (inherited is not null && !IsAccepted(inherited, parents[0].Request.ExecutionId.Value, predecessor.RoomDirectory))
                throw new InvalidOwnedTaskIdentityException("Owned-task predecessor evidence is malformed or mismatched.");
        }
        else
        {
            // Once a room has accepted work, its immutable requests own downstream attribution.
            // A late queue edit cannot replace or establish that room's ownership.
            var accepted = events.OfType<FlowEvent.ExecutionRequestAccepted>().ToList();
            hasPredecessor = accepted.Count > 0;
            foreach (var entry in accepted)
            {
                if (entry.Request.OwnedTaskIdentity is not { } owner) continue;
                if (!IsAccepted(owner, entry.Request.ExecutionId.Value, room)
                    || inherited is not null && !SameOwner(inherited, owner))
                    throw new InvalidOwnedTaskIdentityException("Room contains conflicting owned-task execution evidence.");
                inherited = owner;
            }
        }

        if (inherited is not null)
        {
            // A newly admitted continuation attempt can replace the attempt coordinates, while
            // retaining the exact predecessor's task. Arbitrary declarations cannot do so.
            if (predecessor is not null && !SameRoom(room, predecessor.RoomDirectory)
                && declaration is not null && SameTask(inherited, declaration)
                && await IsAdmittedAsync(room, declaration, cancellationToken).ConfigureAwait(false))
                return declaration;
            if (declaration is not null
                && (!declaration.IsAdmissionShape()
                    || !SameOwner(inherited, declaration)
                    || !SameRoom(declaration.RoomDirectory!, room)))
                throw new InvalidOwnedTaskIdentityException("Owned-task declaration conflicts with the exact accepted predecessor.");
            return inherited with { RoomDirectory = room, ExecutionId = null };
        }

        if (declaration is null) return null;
        if (!hasPredecessor && await IsAdmittedAsync(room, declaration, cancellationToken).ConfigureAwait(false))
            return declaration;
        throw new InvalidOwnedTaskIdentityException("Owned-task declaration has no matching admitted attempt or accepted predecessor; ownership remains unknown.");
    }

    public static bool IsAccepted(OwnedTaskExecutionIdentity identity, string execution, string room) =>
        identity.HasExpectedTaskId() && identity.AttemptId is { Length: > 0 }
        && identity.ExecutionId == execution && identity.RoomDirectory is { Length: > 0 } recordedRoom
        && SameRoom(recordedRoom, room);

    public static bool SameOwner(OwnedTaskExecutionIdentity left, OwnedTaskExecutionIdentity right) =>
        SameTask(left, right) && left.AttemptId == right.AttemptId;

    private static bool SameTask(OwnedTaskExecutionIdentity left, OwnedTaskExecutionIdentity right) =>
        left.TaskId == right.TaskId && left.Repository == right.Repository && left.Issue == right.Issue;

    public static async Task<bool> IsAdmittedAsync(
        string room, OwnedTaskExecutionIdentity identity, CancellationToken cancellationToken)
    {
        if (!identity.IsAdmissionShape() || !SameRoom(identity.RoomDirectory!, room)) return false;
        var queue = await QueueStore.LoadAsync(BatonPaths.QueueFile, cancellationToken).ConfigureAwait(false);
        return queue.Items.Count(item => item.State == QueueItemState.Launched
            && item.OwnedTask is { } owner && owner.Id == identity.TaskId
            && owner.Repository == identity.Repository && owner.Issue == identity.Issue
            && item.Repository == identity.Repository && item.Issue == identity.Issue
            && item.AttemptId?.Value == identity.AttemptId
            && item.AttemptEnvelope is { } envelope && envelope.AttemptId.Value == identity.AttemptId
            && envelope.OwnedTaskIdentity == identity
            && item.RoomDirectory is { } recordedRoom && SameRoom(recordedRoom, room)) == 1;
    }

    private static bool SameRoom(string left, string right) =>
        BatonPaths.RecordKeyComparer.Equals(BatonPaths.RecordKey(left), BatonPaths.RecordKey(right));

    private static async Task<bool> HasContinuationProvenanceAsync(
        string room, OwnedTaskExecutionPredecessor predecessor, CancellationToken cancellationToken)
    {
        // These coordinates are written by the actual continue/redispatch materialization path,
        // independently of bindings.json. A copied binding reference cannot create child lineage.
        var path = Path.Combine(room, ".baton", BatonPaths.RoomMetadataFileName);
        if (!File.Exists(path)) return false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
            using var marker = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = marker.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("Kind", out var kind) && kind.ValueKind == JsonValueKind.String
                && kind.GetString() == "Workflow"
                && root.TryGetProperty("ParentRoomDirectoryPath", out var parentRoom) && parentRoom.ValueKind == JsonValueKind.String
                && parentRoom.GetString() is { Length: > 0 } recordedRoom && SameRoom(recordedRoom, predecessor.RoomDirectory)
                && root.TryGetProperty("ParentExecutionId", out var execution) && execution.ValueKind == JsonValueKind.String
                && execution.GetString() == predecessor.ExecutionId;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidOwnedTaskIdentityException(
                $"Owned-task continuation provenance could not be read; ownership remains unknown: {ex.Message}");
        }
    }
}

/// <summary>An untrusted predecessor declaration; external references also require the child's engine-written lineage.</summary>
public sealed record OwnedTaskExecutionPredecessor(string RoomDirectory, string ExecutionId);

public sealed class InvalidOwnedTaskIdentityException(string message) : BatonFlowException(message);
