using Baton.Status;

namespace Baton.Memory;

/// <summary>A publication fence and content-free diagnostic for incomplete durable ownership.</summary>
public sealed record MemoryImportOperationProblem(string State, string Detail, IReadOnlySet<string> AffectedSlugs)
{
    public bool Blocks(string slug) => AffectedSlugs.Count == 0 || FleetMemory.IsFleet(slug)
        || AffectedSlugs.Contains(FleetMemory.Slug) || AffectedSlugs.Contains(slug);
}

/// <summary>Reconciles canonical ownership with durable operation records for publication and audit.</summary>
public static class MemoryImportOperationHealth
{
    public static IReadOnlyList<MemoryImportOperationProblem> Scan(string batonRoot) => Sample(batonRoot).Problems;

    public static (string Generation, IReadOnlyList<MemoryImportOperationProblem> Problems) Sample(string batonRoot)
    {
        var sample = MemoryCanonicalGeneration.Inspect(batonRoot, () => ScanUnlocked(batonRoot));
        return (sample.Generation, sample.Value);
    }

    private static IReadOnlyList<MemoryImportOperationProblem> ScanUnlocked(string batonRoot)
    {
        var problems = new List<MemoryImportOperationProblem>();
        var owned = new List<OwnedRow>();
        var records = new Dictionary<string, ImportManifest>(StringComparer.Ordinal);
        try
        {
            // The canonical generation mutex excludes ledger and manifest mutations across this
            // entire read. No ledger mutex is nested; sharing/I/O failures still fence loudly.
            if (Directory.Exists(batonRoot))
            {
                foreach (var directory in Directory.EnumerateDirectories(batonRoot))
                {
                    var slug = Path.GetFileName(directory);
                    var memory = Path.Combine(directory, BatonPaths.MemoryDirectoryName);
                    foreach (var row in MemoryStore.Ledger.ReadAllUnlocked(Path.Combine(memory, BatonPaths.MemoryEntriesFileName)))
                    {
                        if (row.ImportOperationId is not null)
                            owned.Add(new(row.ImportOperationId, row.Repository, row.Id, slug, "entry"));
                    }
                    foreach (var row in MemoryStore.LinkLedger.ReadAllUnlocked(Path.Combine(memory, BatonPaths.MemoryLinksFileName)))
                    {
                        if (row.ImportOperationId is not null)
                            owned.Add(new(row.ImportOperationId, row.Repository, row.Id, slug, "link"));
                    }
                }
            }
            var aliases = MemoryAliasStore.Ledger.ReadAllUnlocked(Path.Combine(batonRoot, BatonPaths.MemoryAliasFileName));
            foreach (var row in aliases)
            {
                if (row.ImportOperationId is not null)
                    owned.Add(new(row.ImportOperationId, row.Repository, row.Path, FleetMemory.SlugFor(row.Repository), "alias"));
            }

            foreach (var path in MemoryImportOperationStore.ManifestPaths(batonRoot))
            {
                try
                {
                    var manifest = ImportManifest.Read(path);
                    MemoryImportOperationStore.RequireRoot(manifest, batonRoot);
                    if (manifest.OperationId is { } operation && !records.TryAdd(operation, manifest))
                        throw new InvalidDataException($"Duplicate durable operation id '{operation}'.");
                    if (manifest.OperationState == ImportOperationState.Intent)
                        problems.Add(new("pending", $"'{path}' awaits replay; canonical counts are partial.",
                            MemoryImportOperationStore.AffectedSlugs(manifest)));
                    if (manifest.OperationState == ImportOperationState.Reversing)
                        problems.Add(new("pending", $"'{path}' awaits reversal; canonical counts are partial.",
                            MemoryImportOperationStore.AffectedSlugs(manifest)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BatonMemoryException or InvalidDataException)
                {
                    // A corrupt file cannot reliably identify its affected repositories: fence all.
                    problems.Add(new("corrupt", $"'{path}': {ex.Message}", new HashSet<string>()));
                }
            }

            foreach (var manifest in records.Values.Where(m => m.OperationState != ImportOperationState.Intent))
            {
                // Aliases survive undo. Every accepted assertion must retain its exact owner.
                // Old settlements implicitly owned their plan.
                foreach (var accepted in manifest.AcceptedAliases ?? manifest.PlannedAliases ?? [])
                {
                    var matches = aliases.Where(a => BatonPaths.RecordKeyComparer.Equals(a.Path, accepted.Path)).ToList();
                    if (matches.Count != 1 || matches[0] != accepted)
                        problems.Add(new("corrupt", $"Operation '{manifest.OperationId}' lost accepted alias ownership for '{accepted.Path}'.",
                            MemoryImportOperationStore.AffectedSlugs(manifest)));
                }
            }

            // Consume ownership rows once. Duplicate canonical rows and settled accounting that
            // calls an owned row AlreadyPresent cannot silently pass. Missing canonical rows are
            // permitted after undo; extra owned rows are never permitted, even for legacy manifests.
            var consumed = new HashSet<(string Operation, string Kind, MemoryImportOperationStore.OwnershipIdentity Key)>();
            foreach (var row in owned)
            {
                if (string.IsNullOrWhiteSpace(row.Operation) || string.IsNullOrWhiteSpace(row.Repository)
                    || string.IsNullOrWhiteSpace(row.Id))
                {
                    problems.Add(new("corrupt", $"Incomplete canonical {row.Kind} ownership in '{row.Slug}'.",
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { row.Slug }));
                    continue;
                }
                if (!records.TryGetValue(row.Operation, out var manifest))
                {
                    problems.Add(new("orphan", $"{row.Kind} in '{row.Slug}' names missing operation '{row.Operation}'.",
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { row.Slug }));
                    continue;
                }
                var key = MemoryImportOperationStore.OwnershipKey(row.Repository, row.Id);
                var pending = manifest.OperationState == ImportOperationState.Intent;
                var accounted = row.Kind switch
                {
                    "entry" => manifest.OperationState != ImportOperationState.Reversed && manifest.Entries.Any(e => (pending || !e.AlreadyPresent)
                        && MemoryImportOperationStore.OwnershipKey(e.Repository, e.EntryId) == key),
                    "link" => manifest.OperationState != ImportOperationState.Reversed && (manifest.Links ?? []).Any(l => (pending || !l.AlreadyPresent)
                        && MemoryImportOperationStore.OwnershipKey(l.Repository, l.LinkId) == key),
                    _ => (pending ? manifest.PlannedAliases ?? [] : manifest.AcceptedAliases ?? manifest.PlannedAliases ?? []).Any(a =>
                        a.ImportOperationId == row.Operation &&
                        MemoryImportOperationStore.OwnershipKey(a.Repository, a.Path) == key),
                };
                if (!accounted || !string.Equals(row.Slug, FleetMemory.SlugFor(row.Repository), StringComparison.OrdinalIgnoreCase)
                    || !consumed.Add((row.Operation, row.Kind, key)))
                {
                    problems.Add(new("corrupt", $"Operation '{row.Operation}' does not account for each owned {row.Kind} in '{row.Slug}'.",
                        MemoryImportOperationStore.AffectedSlugs(manifest).Append(row.Slug).ToHashSet(StringComparer.OrdinalIgnoreCase)));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            problems.Add(new("corrupt", $"Cannot reconcile durable operation ownership: {ex.Message}", new HashSet<string>()));
        }
        return problems;
    }

    private sealed record OwnedRow(string Operation, string Repository, string Id, string Slug, string Kind);
}
