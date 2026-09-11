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

        // Disk is the authority even when a caller still holds a pre-interruption object.
        intent = ImportManifest.Read(manifestPath);
        RequireRoot(intent, BatonPaths.Root);
        if (intent.OperationState == ImportOperationState.Settled)
        {
            return intent;
        }

        ValidateRecord(intent);
        var operationId = intent.OperationId!;
        var plannedEntries = intent.PlannedEntries!;
        var plannedLinks = intent.PlannedLinks ?? [];

        await MemoryAliasStore.AppendAsync(
            intent.PlannedAliases ?? [], BatonPaths.MemoryAliasFile, cancellationToken).ConfigureAwait(false);
        BoundaryObserver?.Invoke("aliases");

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

        var ownedEntries = new HashSet<OwnershipIdentity>();
        foreach (var group in plannedEntries.GroupBy(e => e.Repository, StringComparer.OrdinalIgnoreCase))
        {
            var stored = await MemoryStore.ReadAllStrictAsync(
                    BatonPaths.MemoryEntriesFile(FleetMemory.SlugFor(group.Key)),
                    cancellationToken)
                .ConfigureAwait(false);
            foreach (var row in stored.Where(e => string.Equals(
                         e.ImportOperationId, operationId, StringComparison.Ordinal)))
            {
                ownedEntries.Add(OwnershipKey(row.Repository, row.Id));
            }
        }

        var ownedLinks = new HashSet<OwnershipIdentity>();
        foreach (var group in plannedLinks.GroupBy(l => l.Repository, StringComparer.OrdinalIgnoreCase))
        {
            var stored = await MemoryStore.ReadLinksStrictAsync(
                    BatonPaths.MemoryLinksFile(FleetMemory.SlugFor(group.Key)),
                    cancellationToken)
                .ConfigureAwait(false);
            foreach (var row in stored.Where(l => string.Equals(
                         l.ImportOperationId, operationId, StringComparison.Ordinal)))
            {
                ownedLinks.Add(OwnershipKey(row.Repository, row.Id));
            }
        }

        var settled = intent with
        {
            Entries = intent.Entries.Select(row =>
                row with { AlreadyPresent = !ownedEntries.Remove(OwnershipKey(row.Repository, row.EntryId)) }).ToList(),
            Links = (intent.Links ?? []).Select(row =>
                row with { AlreadyPresent = !ownedLinks.Remove(OwnershipKey(row.Repository, row.LinkId)) }).ToList(),
            OperationState = ImportOperationState.Settled,
            // Keep the plan: settlement must remain checkable against its accounting after restart.
        };
        if (ownedEntries.Count != 0 || ownedLinks.Count != 0)
        {
            throw new InvalidDataException("Canonical operation ownership exceeds its durable accounting.");
        }

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
        foreach (var path in ManifestPaths(BatonPaths.Root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ImportManifest manifest;
            try
            {
                manifest = ImportManifest.Read(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BatonMemoryException)
            {
                diagnostics.WriteLine($"Memory import recovery: corrupt record; could not read '{path}': {ex.Message}");
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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or BatonMemoryException)
            {
                blocked.UnionWith(affected);
                diagnostics.WriteLine(
                    $"Memory import recovery: '{path}' remains pending and its stores were not published: {ex.Message}");
            }
        }

        foreach (var problem in MemoryImportOperationHealth.Scan(BatonPaths.Root))
        {
            diagnostics.WriteLine($"Memory import recovery: {problem.State}: {problem.Detail}");
            blocked.UnionWith(problem.AffectedSlugs.Count == 0 ? [FleetMemory.Slug] : problem.AffectedSlugs);
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
        return MemoryImportOperationHealth.Scan(BatonPaths.Root).Any(p => p.Blocks(repositorySlug));
    }

    internal static IEnumerable<string> ManifestPaths(string batonRoot)
    {
        var directory = Path.Combine(batonRoot, BatonPaths.MemoryImportsDirectoryName);
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            : [];
    }

    internal static HashSet<string> AffectedSlugs(ImportManifest manifest) =>
        manifest.Entries.Select(e => FleetMemory.SlugFor(e.Repository))
            .Concat((manifest.Links ?? []).Select(l => FleetMemory.SlugFor(l.Repository)))
            .Concat((manifest.PlannedAliases ?? []).Select(a => FleetMemory.SlugFor(a.Repository)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    internal readonly record struct OwnershipIdentity(string Repository, string Id);

    internal static OwnershipIdentity OwnershipKey(string repository, string id) =>
        new(repository.ToUpperInvariant(), id);

    internal static void RequireRoot(ImportManifest manifest, string batonRoot)
    {
        if (!BatonPaths.RecordKeyComparer.Equals(
                BatonPaths.RecordKey(manifest.BatonRoot), BatonPaths.RecordKey(batonRoot)))
        {
            throw new InvalidDataException($"Memory import record belongs to a different storage root: '{manifest.BatonRoot}'.");
        }
    }

    // Every candidate occurrence (including duplicates) has exactly one accounting row. Ownership
    // is operation-qualified by the enclosing record and repository/id/path-qualified by this bag.
    // Legacy settled manifests have no replay plan, but their owned rows are reconciled at publication.
    internal static void ValidateRecord(ImportManifest intent)
    {
        if (intent.Entries is null || string.IsNullOrWhiteSpace(intent.BatonRoot)
            || !Enum.IsDefined(intent.OperationState)
            || intent.OperationId is not null && string.IsNullOrWhiteSpace(intent.OperationId)
            || intent.Entries.Any(e => e is null || string.IsNullOrWhiteSpace(e.EntryId)
                || string.IsNullOrWhiteSpace(e.Repository) || string.IsNullOrWhiteSpace(e.EntriesFilePath))
            || (intent.Links ?? []).Any(l => l is null || string.IsNullOrWhiteSpace(l.LinkId)
                || string.IsNullOrWhiteSpace(l.Repository) || string.IsNullOrWhiteSpace(l.LinksFilePath)))
        {
            throw new InvalidDataException("Incomplete memory operation accounting.");
        }

        var hasPlan = intent.PlannedEntries is not null || intent.PlannedLinks is not null || intent.PlannedAliases is not null;
        if (intent.OperationId is null && intent.OperationState == ImportOperationState.Settled && !hasPlan)
        {
            return; // Pre-operation manifests retain their existing undo/path compatibility.
        }

        if (intent.OperationState == ImportOperationState.Settled
            && (intent.Appended.Select(e => OwnershipKey(e.Repository, e.EntryId)).Distinct().Count() != intent.Appended.Count()
                || intent.AppendedLinks.Select(l => OwnershipKey(l.Repository, l.LinkId)).Distinct().Count() != intent.AppendedLinks.Count()))
        {
            throw new InvalidDataException("Settled accounting claims duplicate ownership.");
        }

        // Legacy undo may target a manifest from another root; validate paths against its own root.
        foreach (var row in intent.Entries)
        {
            RequireCanonicalPath(intent.BatonRoot, row.Repository, row.EntriesFilePath, BatonPaths.MemoryEntriesFileName);
        }
        foreach (var row in intent.Links ?? [])
        {
            RequireCanonicalPath(intent.BatonRoot, row.Repository, row.LinksFilePath, BatonPaths.MemoryLinksFileName);
        }

        if (intent.OperationState == ImportOperationState.Intent || hasPlan)
        {
            if (string.IsNullOrWhiteSpace(intent.OperationId) || intent.PlannedEntries is null
                || intent.PlannedEntries.Any(e => e is null || string.IsNullOrWhiteSpace(e.Id)
                    || string.IsNullOrWhiteSpace(e.Repository) || e.ImportOperationId != intent.OperationId)
                || (intent.PlannedLinks ?? []).Any(l => l is null || string.IsNullOrWhiteSpace(l.Id)
                    || string.IsNullOrWhiteSpace(l.Repository) || l.ImportOperationId != intent.OperationId)
                || (intent.PlannedAliases ?? []).Any(a => a is null || string.IsNullOrWhiteSpace(a.Path)
                    || string.IsNullOrWhiteSpace(a.Repository) || a.ImportOperationId != intent.OperationId))
            {
                throw new InvalidDataException("Memory import intent has incomplete or mismatched ownership data.");
            }

            RequireSameBag(intent.PlannedEntries.Select(e => OwnershipKey(e.Repository, e.Id)),
                intent.Entries.Select(e => OwnershipKey(e.Repository, e.EntryId)));
            RequireSameBag((intent.PlannedLinks ?? []).Select(l => OwnershipKey(l.Repository, l.Id)),
                (intent.Links ?? []).Select(l => OwnershipKey(l.Repository, l.LinkId)));
        }
    }

    private static void RequireCanonicalPath(string root, string repository, string path, string filename)
    {
        var expected = Path.Combine(root, FleetMemory.SlugFor(repository), BatonPaths.MemoryDirectoryName, filename);
        if (!BatonPaths.RecordKeyComparer.Equals(BatonPaths.RecordKey(expected), BatonPaths.RecordKey(path)))
        {
            throw new InvalidDataException($"Memory import accounting path '{path}' is not canonical.");
        }
    }

    private static void RequireSameBag(IEnumerable<OwnershipIdentity> plan, IEnumerable<OwnershipIdentity> accounting)
    {
        var expected = plan.GroupBy(key => key).ToDictionary(group => group.Key, group => group.Count());
        foreach (var key in accounting)
        {
            if (!expected.TryGetValue(key, out var count) || count == 0)
            {
                throw new InvalidDataException("Memory import accounting exceeds its duplicate-aware replay plan.");
            }
            expected[key] = count - 1;
        }
        if (expected.Values.Any(count => count != 0))
        {
            throw new InvalidDataException("Memory import plan and accounting are not an exact duplicate-aware bijection.");
        }
    }
}
