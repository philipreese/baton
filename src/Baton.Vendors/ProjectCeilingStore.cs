using System.Text.Json;
using Baton.Status;

namespace Baton.Vendors;

/// <summary>
/// #1166 — decision 0004's storage half: the project-keyed permission-ceiling store. Reads and writes
/// a single flat JSON map, canonical project path → <see cref="ProjectCeiling"/>, under
/// <see cref="BatonPaths.Root"/> — 0004's "AER's own app-level config, keyed by project path, never a
/// file inside the project's own directory" — never a per-process cache: every method reads the file
/// fresh, because <c>baton trust</c> may revise it mid-fleet (a separate, later <c>baton dispatch</c>
/// process must see the revision, and nothing here ever holds a loaded copy across calls).
/// <para>
/// <b>Canonicalisation</b> is <see cref="BatonPaths.RecordKey"/>/<see cref="BatonPaths.RecordKeyComparer"/>
/// verbatim, not a second implementation — the same "absolute, trailing separator trimmed,
/// case-insensitive on every filesystem" rule every other per-directory record in this tree already
/// keys on (room locks, per-session host state).
/// </para>
/// <para>
/// <b>Missing-file vs. malformed-file,</b> the same split <see cref="BatonProfileStore"/> draws: a
/// missing file is "no project has been trusted on this machine yet," a valid and common state that
/// resolves to an empty map (every project then reads as unseen and dispatch refuses, which is the
/// correct fail-closed default). A malformed file is different — <see cref="Load"/> throws
/// <see cref="ProjectCeilingStoreException"/> rather than silently discarding whatever ceilings the
/// operator already recorded.
/// </para>
/// </summary>
public static class ProjectCeilingStore
{
    /// <summary>
    /// #1166 review finding H1: <see cref="Set"/>/<see cref="Revoke"/> are load-then-modify-then-save,
    /// so two callers racing the same <paramref name="path"/> can lose an update (last writer wins)
    /// even though <see cref="Save"/>'s own write is atomic. That started as a plain in-process lock,
    /// whose own note named the condition for replacing it: <i>"switch this store onto
    /// <c>MutexGuardedFileLock</c> the day a second process writes ceilings (e.g. a daemon-side trust
    /// flow)"</i>. #2076 is that day — <c>baton dispatch</c> now records an inherited ceiling
    /// (<c>Baton.Cli.InheritedProjectCeiling</c>), so the daemon's launches and an operator's
    /// <c>baton trust</c> are concurrent OS processes writing one file. A lost update here is not a
    /// cosmetic race: the entry that vanishes is a lane's only trust record, and that lane then fails
    /// closed with the "has no recorded permission ceiling" refusal this issue exists to remove.
    /// </summary>
    private const string LockNamePrefix = "baton-project-ceilings";

    /// <summary>
    /// How long a writer waits for the lock above. The critical section is one small read, one
    /// dictionary edit and one atomic rewrite — the same shape and the same 30 seconds
    /// <c>QueueStore</c> allows its own snapshot mutations.
    /// </summary>
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The production location: <c>project-ceilings.json</c> under <see cref="BatonPaths.Root"/>. A
    /// re-resolving property, not a captured value, so it honours the root seam (<c>BATON_HOME</c>);
    /// tests construct against a temp file directly instead of this.
    /// </summary>
    public static string DefaultPath => Path.Combine(BatonPaths.Root, "project-ceilings.json");

    /// <summary>The canonical key a project path resolves to — <see cref="BatonPaths.RecordKey"/> verbatim.</summary>
    public static string CanonicalKey(string projectPath) => BatonPaths.RecordKey(projectPath);

    /// <summary>Loads the ceiling map from <paramref name="path"/>; a missing file resolves to an empty map.</summary>
    /// <exception cref="ProjectCeilingStoreException">The file exists but is not valid JSON, or is not a JSON object of ceiling values.</exception>
    public static IReadOnlyDictionary<string, ProjectCeiling> Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (!File.Exists(path))
        {
            return new Dictionary<string, ProjectCeiling>(BatonPaths.RecordKeyComparer);
        }

        Dictionary<string, ProjectCeiling>? ceilings;
        try
        {
            ceilings = JsonSerializer.Deserialize<Dictionary<string, ProjectCeiling>>(File.ReadAllText(path));
        }
        catch (JsonException ex)
        {
            throw new ProjectCeilingStoreException($"Malformed project-ceiling store at '{path}': {ex.Message}", ex);
        }

        return new Dictionary<string, ProjectCeiling>(ceilings ?? [], BatonPaths.RecordKeyComparer);
    }

    /// <summary>Persists <paramref name="ceilings"/> to <paramref name="path"/> atomically (temp file, then rename).</summary>
    public static void Save(IReadOnlyDictionary<string, ProjectCeiling> ceilings, string path)
    {
        ArgumentNullException.ThrowIfNull(ceilings);
        ArgumentException.ThrowIfNullOrEmpty(path);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(ceilings, new JsonSerializerOptions { WriteIndented = true });
        AtomicLaunchConfigWriter.Write(path, json);
    }

    /// <summary>The recorded ceiling for <paramref name="projectPath"/>, or null when the project has never been trusted.</summary>
    public static ProjectCeiling? TryGet(string projectPath, string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectPath);

        return Load(path).TryGetValue(CanonicalKey(projectPath), out var ceiling) ? ceiling : null;
    }

    /// <summary>Records (or replaces) <paramref name="projectPath"/>'s ceiling — the <c>baton trust &lt;path&gt; --ceiling …</c> write path.</summary>
    public static void Set(string projectPath, ProjectCeiling ceiling, string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectPath);
        ArgumentNullException.ThrowIfNull(ceiling);

        MutexGuardedFileLock.RunUnderLock(path, LockNamePrefix, LockTimeout, () =>
        {
            var ceilings = new Dictionary<string, ProjectCeiling>(Load(path), BatonPaths.RecordKeyComparer)
            {
                [CanonicalKey(projectPath)] = ceiling,
            };
            Save(ceilings, path);
        });
    }

    /// <summary>Removes <paramref name="projectPath"/>'s ceiling. Returns false when none was recorded.</summary>
    public static bool Revoke(string projectPath, string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectPath);

        return MutexGuardedFileLock.RunUnderLock(path, LockNamePrefix, LockTimeout, () =>
        {
            var ceilings = new Dictionary<string, ProjectCeiling>(Load(path), BatonPaths.RecordKeyComparer);
            if (!ceilings.Remove(CanonicalKey(projectPath)))
            {
                return false;
            }

            Save(ceilings, path);
            return true;
        });
    }
}
