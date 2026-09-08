using Baton.Artifacts;
using Baton.Memory;
using Baton.Status;
using Baton.Vendors;

namespace Baton.Cli;

/// <summary>
/// Who is asserting the entry <c>baton memory add</c> is about to write (#2071): the operator, or the
/// lane the verb was run inside.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read from the ambient environment, and it is a reading rather than a credential.</b> A worker is
/// handed <c>BATON_ARTIFACTS_ROOT</c> by <see cref="ArtifactManager.BuildEnvironment"/> and inherits it
/// into anything it spawns, so its presence is what tells this verb it is running inside a lane. That
/// is exactly as strong as an environment variable, which is to say a caller outside a lane could set
/// one — <b>and that is a question this slice does not answer</b>: which roles may add, what an entry
/// may contain, and how a lane's assertion is distinguished from the operator's are #2071's explicitly
/// deferred lane-grant slice. What is built here is the honest recording of what the environment said.
/// </para>
/// <para>
/// <b><see cref="AuthoredMemory.Operator"/> means "no lane context", never "a lane I could not read".</b>
/// A lane whose room directory or <c>bindings.json</c> will not resolve produces
/// <see cref="UnidentifiedLane"/> or a room-only value, not the operator's name. The two states are
/// different claims about who wrote a memory, and collapsing the second into the first would file a
/// worker's assertion under the operator — the one mislabelling the deferred grant slice cannot undo
/// afterwards, because the row is already written and the store is append-only.
/// </para>
/// <para>
/// <b>The anchor is <c>BATON_ARTIFACTS_ROOT</c> rather than <c>BATON_OUTPUT_DIR</c>.</b> Both are
/// emitted unconditionally, but the room directory is one level up from the artifacts root and two up
/// from the per-execution outbox, so anchoring on the artifacts root is one fewer piece of path
/// arithmetic to be wrong about.
/// </para>
/// </remarks>
public static class MemoryLaneAssertion
{
    /// <summary>The variable whose presence means "this ran inside a lane".</summary>
    public const string ArtifactsRootVariable = "BATON_ARTIFACTS_ROOT";

    /// <summary>
    /// What a lane whose room could not be identified at all asserts as. Distinct from
    /// <see cref="AuthoredMemory.Operator"/> for the reason the type remarks give.
    /// </summary>
    public const string UnidentifiedLane = "lane/unknown";

    /// <summary>
    /// The <see cref="Baton.Memory.MemoryEntry.AssertedBy"/> value for this process, read from its own
    /// environment.
    /// </summary>
    public static string Resolve() => Resolve(Environment.GetEnvironmentVariable(ArtifactsRootVariable));

    /// <summary>
    /// <inheritdoc cref="Resolve()"/> The seam a test drives: the artifacts root is passed in rather
    /// than read, so both polarities — inside a lane and outside one — are reachable without mutating
    /// the process's environment.
    /// </summary>
    /// <param name="artifactsRoot">
    /// The value of <see cref="ArtifactsRootVariable"/>, or <see langword="null"/>/empty when it is
    /// unset — which is the ONLY input that yields <see cref="AuthoredMemory.Operator"/>.
    /// </param>
    public static string Resolve(string? artifactsRoot)
    {
        if (artifactsRoot is not { Length: > 0 } root)
        {
            return AuthoredMemory.Operator;
        }

        var roomDirectory = TryResolveRoomDirectory(root);
        if (roomDirectory is null)
        {
            return UnidentifiedLane;
        }

        var room = Path.GetFileName(Path.TrimEndingDirectorySeparator(roomDirectory));
        var binding = TryReadSoleBinding(roomDirectory);

        // Room-only rather than the unidentified value: the room IS identified, and only the role and
        // vendor are missing. Reporting less than was read would throw away the half that resolved.
        return binding is { } resolved
            ? $"{resolved.Role}/{resolved.Entry.Adapter}/{room}"
            : $"lane/{room}";
    }

    /// <summary>
    /// The room directory an artifacts root belongs to — its parent, since
    /// <see cref="ArtifactManager.ArtifactsDirectoryName"/> is a directory inside the room. Null when
    /// the value is not shaped like one (not rooted, wrong last component, no parent).
    /// </summary>
    /// <remarks>
    /// <b>Shape, not existence.</b> A room directory that has since been swept still identifies the
    /// lane that ran there, and refusing it would turn a late add inside a finished lane into an
    /// assertion by the operator — the exact collapse this file exists to prevent.
    /// </remarks>
    private static string? TryResolveRoomDirectory(string artifactsRoot)
    {
        if (!Path.IsPathRooted(artifactsRoot))
        {
            return null;
        }

        var trimmed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(artifactsRoot));
        return Path.GetFileName(trimmed).Equals(
                   ArtifactManager.ArtifactsDirectoryName, StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(trimmed)
            : null;
    }

    /// <summary>
    /// The room's sole binding, through <see cref="ConductorRoomDetector.TryResolveSoleBinding"/> — the
    /// one resolution of "this room's role" in this tree, never a second reading of the same file.
    /// Null on any room whose bindings are absent, unreadable, or not exactly one entry.
    /// </summary>
    private static (string Role, WorkerBindingConfigEntry Entry)? TryReadSoleBinding(string roomDirectory)
    {
        var bindingsPath = BatonPaths.RoomBindingsFile(roomDirectory);
        if (!File.Exists(bindingsPath))
        {
            return null;
        }

        try
        {
            return ConductorRoomDetector.TryResolveSoleBinding(
                WorkerBindingConfigParser.Parse(File.ReadAllText(bindingsPath), bindingsPath));
        }
        catch (Exception ex) when (ex is WorkerBindingConfigException or IOException or UnauthorizedAccessException)
        {
            // Swallowed to a null reading, not to the operator's name: the caller turns this into
            // `lane/<room>`, which still records that a lane wrote the entry.
            return null;
        }
    }
}
