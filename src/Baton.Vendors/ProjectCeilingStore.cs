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

    /// <summary>
    /// Loads the ceiling map from <paramref name="path"/>; a missing file resolves to an empty map.
    /// <b>Tombstones included</b> (#2121, <see cref="ProjectCeiling.RevokedAt"/>): this is the raw
    /// record, for the readers that must tell a revoked path from a never-recorded one. A reader asking
    /// what ceiling applies to one path uses <see cref="TryGet"/>, which hides them.
    /// </summary>
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

    /// <summary>
    /// The live ceiling for <paramref name="projectPath"/>, or null when none applies — never trusted,
    /// or revoked (#2121: a tombstone is not a ceiling; <see cref="TryGetRecord"/> is the reader that
    /// tells those two apart).
    /// </summary>
    public static ProjectCeiling? TryGet(string projectPath, string path) =>
        TryGetRecord(projectPath, path) is { IsRevoked: false } ceiling ? ceiling : null;

    /// <summary>The raw entry for <paramref name="projectPath"/> — a live ceiling, a tombstone, or null when nothing was ever recorded.</summary>
    public static ProjectCeiling? TryGetRecord(string projectPath, string path)
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

    /// <summary>
    /// Replaces <paramref name="projectPath"/>'s live ceiling with a tombstone
    /// (<see cref="ProjectCeiling.Tombstone"/>), and does the same to every live entry whose
    /// <see cref="ProjectCeiling.InheritedFrom"/> names it — transitively, so a chain of copies falls
    /// with its root. <see cref="ProjectCeilingRevocation.Revoked"/> is false when no live ceiling was
    /// recorded for <paramref name="projectPath"/> itself (never trusted, or already a tombstone), and
    /// then nothing else is touched either.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Revoke cascades (#2076 re-review).</b> An inherited entry is a one-time snapshot of its
    /// source, written by <c>Baton.Cli.InheritedProjectCeiling</c> without an operator typing anything.
    /// Left in place, it would keep granting what the operator has just withdrawn, discoverable only by
    /// reading <c>baton trust --list</c> for the provenance clause — the wrong default for a permission
    /// record. The cascade is one direction only: a source later NARROWED (re-trusted, not revoked) does
    /// not re-narrow its copies, and spec/baton.md §9 states that gap.
    /// </para>
    /// <para>
    /// <b>Revoke leaves a tombstone, not an absence (#2121).</b> Removing the entry outright left no way
    /// to tell a fully revoked repository from one never trusted, and the provisioner's unrestricted
    /// default for the latter then re-widened the former at its next <c>queue add --issue</c>. What
    /// the tombstone changes for each reader is spec/baton.md §9's, stated once there.
    /// </para>
    /// </remarks>
    public static ProjectCeilingRevocation Revoke(string projectPath, string path) => Revoke(projectPath, path, DateTimeOffset.UtcNow);

    /// <summary>The <see cref="Revoke(string, string)"/> overload with the tombstone's timestamp injected — for tests that read it back.</summary>
    public static ProjectCeilingRevocation Revoke(string projectPath, string path, DateTimeOffset revokedAt)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectPath);

        return MutexGuardedFileLock.RunUnderLock(path, LockNamePrefix, LockTimeout, () =>
        {
            var ceilings = new Dictionary<string, ProjectCeiling>(Load(path), BatonPaths.RecordKeyComparer);
            var key = CanonicalKey(projectPath);
            if (!ceilings.TryGetValue(key, out var live) || live.IsRevoked)
            {
                return new ProjectCeilingRevocation(Revoked: false, CascadedPaths: []);
            }

            ceilings[key] = ProjectCeiling.Tombstone(live, revokedAt);

            var cascaded = new List<string>();
            var sources = new Queue<string>([key]);
            while (sources.TryDequeue(out var source))
            {
                foreach (var derived in ceilings
                    .Where(pair => !pair.Value.IsRevoked
                        && pair.Value.InheritedFrom is { Length: > 0 } origin
                        && BatonPaths.RecordKeyComparer.Equals(CanonicalKey(origin), source))
                    .Select(pair => pair.Key)
                    .ToList())
                {
                    ceilings[derived] = ProjectCeiling.Tombstone(ceilings[derived], revokedAt);
                    cascaded.Add(derived);
                    sources.Enqueue(derived);
                }
            }

            Save(ceilings, path);
            return new ProjectCeilingRevocation(Revoked: true, CascadedPaths: cascaded);
        });
    }

    /// <summary>
    /// Deletes <paramref name="projectPath"/>'s record outright — tombstone or live — and returns what
    /// was deleted, or <see langword="null"/> when nothing was recorded. The <c>baton trust &lt;path&gt;
    /// --forget</c> write path (#2121), and the <b>only</b> remover an operator has: <see cref="Revoke"/>
    /// withdraws and leaves a tombstone, and nothing else in the product deletes an entry whose
    /// directory is gone. <b>No cascade:</b> this removes one record and says nothing about the entries
    /// copied from it — withdrawing a grant is <see cref="Revoke"/>'s job, and forgetting a live source
    /// leaves its live copies granting exactly what they did. spec/baton.md §9 states the two verbs once.
    /// </summary>
    public static ProjectCeiling? Forget(string projectPath, string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectPath);

        return MutexGuardedFileLock.RunUnderLock(path, LockNamePrefix, LockTimeout, () =>
        {
            var ceilings = new Dictionary<string, ProjectCeiling>(Load(path), BatonPaths.RecordKeyComparer);
            if (!ceilings.Remove(CanonicalKey(projectPath), out var forgotten))
            {
                return null;
            }

            Save(ceilings, path);
            return forgotten;
        });
    }

    /// <summary>
    /// Removes the tombstones at <paramref name="projectPaths"/> (#2121) — the <c>baton trust</c>
    /// register path clearing the revocation of every other path in the repository it just re-trusted.
    /// A path that is not a tombstone is left alone: a live ceiling is never removed by this, and one
    /// that was never recorded has nothing to remove.
    /// </summary>
    /// <returns>The canonical paths whose tombstone was removed, in the order given.</returns>
    public static IReadOnlyList<string> ClearRevocations(IEnumerable<string> projectPaths, string path)
    {
        ArgumentNullException.ThrowIfNull(projectPaths);

        return MutexGuardedFileLock.RunUnderLock(path, LockNamePrefix, LockTimeout, () =>
        {
            var ceilings = new Dictionary<string, ProjectCeiling>(Load(path), BatonPaths.RecordKeyComparer);
            var cleared = new List<string>();
            foreach (var projectPath in projectPaths)
            {
                var key = CanonicalKey(projectPath);
                if (ceilings.TryGetValue(key, out var record) && record.IsRevoked && ceilings.Remove(key))
                {
                    cleared.Add(key);
                }
            }

            if (cleared.Count > 0)
            {
                Save(ceilings, path);
            }

            return (IReadOnlyList<string>)cleared;
        });
    }
}

/// <summary>What <see cref="ProjectCeilingStore.Revoke"/> tombstoned.</summary>
/// <param name="Revoked">Whether the named path had a live ceiling to tombstone.</param>
/// <param name="CascadedPaths">The canonical paths of the inherited entries tombstoned with it, in order — empty when <paramref name="Revoked"/> is false.</param>
public sealed record ProjectCeilingRevocation(bool Revoked, IReadOnlyList<string> CascadedPaths);
