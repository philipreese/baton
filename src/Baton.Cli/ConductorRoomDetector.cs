using Baton.Status;
using Baton.Vendors;

namespace Baton.Cli;

/// <summary>
/// The one conductor-room check <see cref="RoomsPruneCommand"/>, <see cref="RoomDeleteCommand"/>, and
/// <c>FleetStatusTool</c> all resolve role by (F4, 2026-09-02 review): a room whose <c>bindings.json</c>
/// has exactly one entry is that entry's dictionary key, the same "sole binding" resolution
/// <c>FleetStatusTool.TryResolveSoleBinding</c> already used for display metadata — never a raw
/// substring search over the file's text, which would also match an unrelated worker room whose
/// label, workstream, or worker name happens to contain the literal word "conductor".
/// </summary>
public static class ConductorRoomDetector
{
    public const string ConductorRole = "conductor";

    public static bool IsConductorRoom(string roomDirectoryPath)
    {
        if (Path.GetFileName(Path.TrimEndingDirectorySeparator(roomDirectoryPath))
                .Equals(ConductorRole, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var bindingsPath = BatonPaths.RoomBindingsFile(roomDirectoryPath);
        if (!File.Exists(bindingsPath))
        {
            return false;
        }

        IReadOnlyDictionary<string, WorkerBindingConfigEntry> bindings;
        try
        {
            bindings = WorkerBindingConfigParser.Parse(File.ReadAllText(bindingsPath), bindingsPath);
        }
        catch (Exception ex) when (ex is WorkerBindingConfigException or IOException or UnauthorizedAccessException)
        {
            return false;
        }

        var sole = TryResolveSoleBinding(bindings);
        return IsConductorRole(sole);
    }

    internal static bool IsConductorRole((string Role, WorkerBindingConfigEntry Entry)? binding) =>
        binding is { } resolved && string.Equals(resolved.Role, ConductorRole, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A <c>bindings.json</c> with exactly one entry resolves that entry's dictionary key as the
    /// room's role; any other shape (zero or multiple entries) resolves to no role, not a false
    /// positive. Shared verbatim with <c>FleetStatusTool</c>'s terminal-sentinel and no-snapshot
    /// paths so the definition cannot drift between callers.
    /// </summary>
    internal static (string Role, WorkerBindingConfigEntry Entry)? TryResolveSoleBinding(
        IReadOnlyDictionary<string, WorkerBindingConfigEntry>? bindings)
    {
        if (bindings is null || bindings.Count != 1)
        {
            return null;
        }

        var only = bindings.Single();
        return (only.Key, only.Value);
    }
}
