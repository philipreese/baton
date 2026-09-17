using Baton.Queue;
using Baton.Status;
using Baton.Vendors;

namespace Baton.Cli;

/// <summary>
/// The protected command boundary for worker-authored memory. A worker memory write reaches only its
/// recorded repository, and only when the current room carries the exact durable per-dispatch
/// memory-add grant; missing, stale, forged, widened, or inconsistent authority writes nothing.
/// </summary>
public static class MemoryAddLaneGrantGate
{
    public sealed record Authorization(string Repository, string DispatchId, int Issue, string AssertedBy);

    public static async Task<Authorization?> TryAuthorizeAsync(string? artifactsRoot, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(artifactsRoot))
        {
            return null; // operator invocation is the pre-existing standing authority path.
        }

        try
        {
            if (!Path.IsPathRooted(artifactsRoot)
                || !Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(artifactsRoot)))
                    .Equals(Baton.Artifacts.ArtifactManager.ArtifactsDirectoryName, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var room = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(artifactsRoot)));
            if (room is null || !File.Exists(BatonPaths.RoomBindingsFile(room)))
            {
                return null;
            }

            var bindings = WorkerBindingConfigParser.Parse(File.ReadAllText(BatonPaths.RoomBindingsFile(room)));
            var binding = ConductorRoomDetector.TryResolveSoleBinding(bindings);
            if (binding is not { } resolved || resolved.Entry.MemoryAddGrant is not { } grant || !grant.IsWellFormed)
            {
                return null;
            }

            var queue = await QueueStore.LoadAsync(BatonPaths.QueueFile, cancellationToken).ConfigureAwait(false);
            var item = queue.Items.SingleOrDefault(candidate =>
                candidate.MemoryAddGrant == grant
                && candidate.Requirements?.Contains(TaskRequirements.MemoryAdd, StringComparer.Ordinal) == true
                && candidate.Issue is > 0
                && candidate.RoomDirectory is { Length: > 0 } recordedRoom
                && BatonPaths.RecordKeyComparer.Equals(BatonPaths.RecordKey(recordedRoom), BatonPaths.RecordKey(room)));
            var model = resolved.Entry.ModelResolved ?? resolved.Entry.Model;
            var assignment = item?.AttemptEnvelope;
            var recordedAdapter = assignment?.Adapter ?? item?.Adapter;
            var recordedModel = assignment?.Model ?? item?.Model;
            if (item is null || item.Issue is not int issue
                || string.IsNullOrWhiteSpace(recordedAdapter)
                || !string.Equals(item.Repository, grant.Repository, StringComparison.Ordinal))
            {
                return null;
            }

            if (!string.Equals(resolved.Role, item.Role, StringComparison.Ordinal)
                || !string.Equals(resolved.Entry.Adapter, recordedAdapter, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(model, recordedModel, StringComparison.Ordinal))
            {
                return null;
            }

            return new Authorization(
                grant.Repository,
                grant.DispatchId,
                issue,
                $"room={Path.GetFileName(room)};role={resolved.Role};adapter={resolved.Entry.Adapter};model={model ?? "unresolved-model"};issue={issue};dispatch={grant.DispatchId}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WorkerBindingConfigException or QueueStoreException)
        {
            return null;
        }
    }
}
