using Baton.Status;

namespace Baton.Memory;

/// <summary>
/// Rolls durable import intents forward through the existing canonical ledgers and settles their exact
/// reversal ownership. The manifest is both the operation record and the recovery plan; there is no
/// process-local coordinator whose loss can strand a partial multi-repository import.
/// </summary>
public static class MemoryImportOperationStore
{
    /// <summary>Fixture-only observation after each durable mutation boundary.</summary>
    private static readonly AsyncLocal<Action<string>?> BoundaryObservation = new();

    internal static Action<string>? BoundaryObserver
    {
        get => BoundaryObservation.Value;
        set => BoundaryObservation.Value = value;
    }

    /// <summary>
    /// Applies every row in a durable intent, then replaces the intent atomically with the exact
    /// ownership observed from the canonical rows themselves. Cancellation leaves the intent replayable.
    /// </summary>
    public static async Task<ImportManifest> ApplyAsync(
        string manifestPath,
        ImportManifest intent,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(manifestPath);
        ArgumentNullException.ThrowIfNull(intent);
        cancellationToken.ThrowIfCancellationRequested();

        if (intent.OperationState == ImportOperationState.Settled)
        {
            return intent;
        }

        ValidateIntent(intent);
        var operationId = intent.OperationId!;
        var plannedEntries = intent.PlannedEntries!;
        var plannedLinks = intent.PlannedLinks ?? [];

        // The intent is the commit boundary. Cancellation may stop between ledgers, but it cannot
        // strand unowned rows: every append carries this operation id and the same intent is replayable.
        foreach (var group in plannedEntries.GroupBy(e => e.Repository, StringComparer.OrdinalIgnoreCase))
        {
            var slug = FleetMemory.SlugFor(group.Key);
            var entriesFile = BatonPaths.MemoryEntriesFile(slug);
            await MemoryStoreMetadataStore.EnsureAsync(group.Key, slug, cancellationToken).ConfigureAwait(false);
            await MemoryStore.AppendAndGetAppendedAsync(group.ToList(), entriesFile, cancellationToken)
                .ConfigureAwait(false);
            BoundaryObserver?.Invoke($"entries:{slug}");

            // A duplicate means another writer may have won, but the store is still initialized. The
            // ownership scan below distinguishes that row by its operation id.
            if (MemoryStore.HasAnyParseableEntry(entriesFile))
            {
                await MemoryStoreMetadataStore.CompleteInitializationAsync(
                    group.Key, slug, cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var group in plannedLinks.GroupBy(l => l.Repository, StringComparer.OrdinalIgnoreCase))
        {
            await MemoryStore.AppendLinksAndGetAppendedAsync(
                    group.ToList(),
                    BatonPaths.MemoryLinksFile(FleetMemory.SlugFor(group.Key)),
                    cancellationToken)
                .ConfigureAwait(false);
            BoundaryObserver?.Invoke($"links:{FleetMemory.SlugFor(group.Key)}");
        }

        var ownedEntries = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in plannedEntries.GroupBy(e => e.Repository, StringComparer.OrdinalIgnoreCase))
        {
            var stored = await MemoryStore.ReadAllStrictAsync(
                    BatonPaths.MemoryEntriesFile(FleetMemory.SlugFor(group.Key)),
                    cancellationToken)
                .ConfigureAwait(false);
            foreach (var row in stored.Where(e => string.Equals(
                         e.ImportOperationId, operationId, StringComparison.Ordinal)))
            {
                ownedEntries.Add(row.Id);
            }
        }

        var ownedLinks = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in plannedLinks.GroupBy(l => l.Repository, StringComparer.OrdinalIgnoreCase))
        {
            var stored = await MemoryStore.ReadLinksStrictAsync(
                    BatonPaths.MemoryLinksFile(FleetMemory.SlugFor(group.Key)),
                    cancellationToken)
                .ConfigureAwait(false);
            foreach (var row in stored.Where(l => string.Equals(
                         l.ImportOperationId, operationId, StringComparison.Ordinal)))
            {
                ownedLinks.Add(row.Id);
            }
        }

        var settled = intent with
        {
            Entries = intent.Entries.Select(row =>
                row with { AlreadyPresent = !ownedEntries.Remove(row.EntryId) }).ToList(),
            Links = (intent.Links ?? []).Select(row =>
                row with { AlreadyPresent = !ownedLinks.Remove(row.LinkId) }).ToList(),
            OperationState = ImportOperationState.Settled,
            PlannedEntries = null,
            PlannedLinks = null,
        };
        BoundaryObserver?.Invoke("before-settlement");
        settled.Write(manifestPath);
        return settled;
    }

    /// <summary>
    /// Replays every pending intent after restart. A failed intent remains durable and its affected
    /// stores remain fenced from publication; other stores may continue through the sweep.
    /// </summary>
    public static async Task<IReadOnlySet<string>> RecoverPendingAsync(
        TextWriter diagnostics,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directory = Path.Combine(BatonPaths.Root, BatonPaths.MemoryImportsDirectoryName);
        if (!Directory.Exists(directory))
        {
            return blocked;
        }

        foreach (var path in Directory.EnumerateFiles(directory, "*.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ImportManifest manifest;
            try
            {
                manifest = ImportManifest.Read(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BatonMemoryException)
            {
                diagnostics.WriteLine($"Memory import recovery: could not read '{path}': {ex.Message}");
                continue;
            }

            if (manifest.OperationState != ImportOperationState.Intent)
            {
                continue;
            }

            var affected = AffectedSlugs(manifest);
            try
            {
                await ApplyAsync(path, manifest, CancellationToken.None).ConfigureAwait(false);
                diagnostics.WriteLine($"Memory import recovery: settled '{path}'.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                blocked.UnionWith(affected);
                diagnostics.WriteLine(
                    $"Memory import recovery: '{path}' remains pending and its stores were not published: {ex.Message}");
            }
        }

        return blocked;
    }

    /// <summary>
    /// Publication fence checked under the existing projection obligation mutex immediately before
    /// target replacement. Fleet changes affect every projection; fleet publication observes every
    /// pending repository operation.
    /// </summary>
    public static bool BlocksProjection(string repositorySlug)
    {
        ArgumentException.ThrowIfNullOrEmpty(repositorySlug);
        var directory = Path.Combine(BatonPaths.Root, BatonPaths.MemoryImportsDirectoryName);
        if (!Directory.Exists(directory))
        {
            return false;
        }

        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            var manifest = ImportManifest.Read(path);
            if (manifest.OperationState != ImportOperationState.Intent)
            {
                continue;
            }

            var affected = AffectedSlugs(manifest);
            if (FleetMemory.IsFleet(repositorySlug)
                || affected.Contains(FleetMemory.Slug)
                || affected.Contains(repositorySlug))
            {
                return true;
            }
        }

        return false;
    }

    private static HashSet<string> AffectedSlugs(ImportManifest manifest) =>
        (manifest.PlannedEntries ?? [])
            .Select(e => FleetMemory.SlugFor(e.Repository))
            .Concat((manifest.PlannedLinks ?? []).Select(l => FleetMemory.SlugFor(l.Repository)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static void ValidateIntent(ImportManifest intent)
    {
        if (intent.OperationId is not { Length: > 0 }
            || intent.PlannedEntries is null
            || intent.PlannedEntries.Any(e =>
                !string.Equals(e.ImportOperationId, intent.OperationId, StringComparison.Ordinal))
            || (intent.PlannedLinks ?? []).Any(l =>
                !string.Equals(l.ImportOperationId, intent.OperationId, StringComparison.Ordinal)))
        {
            throw new InvalidDataException("Memory import intent has incomplete or mismatched ownership data.");
        }

        if (!BatonPaths.RecordKeyComparer.Equals(
                BatonPaths.RecordKey(intent.BatonRoot), BatonPaths.RecordKey(BatonPaths.Root)))
        {
            throw new InvalidDataException(
                $"Memory import intent belongs to storage root '{intent.BatonRoot}', not '{BatonPaths.Root}'.");
        }

        foreach (var row in intent.Entries)
        {
            var expected = BatonPaths.MemoryEntriesFile(FleetMemory.SlugFor(row.Repository));
            if (!BatonPaths.RecordKeyComparer.Equals(
                    BatonPaths.RecordKey(expected), BatonPaths.RecordKey(row.EntriesFilePath)))
            {
                throw new InvalidDataException($"Memory import intent entry path '{row.EntriesFilePath}' is not canonical.");
            }
        }

        foreach (var row in intent.Links ?? [])
        {
            var expected = BatonPaths.MemoryLinksFile(FleetMemory.SlugFor(row.Repository));
            if (!BatonPaths.RecordKeyComparer.Equals(
                    BatonPaths.RecordKey(expected), BatonPaths.RecordKey(row.LinksFilePath)))
            {
                throw new InvalidDataException($"Memory import intent link path '{row.LinksFilePath}' is not canonical.");
            }
        }
    }
}
