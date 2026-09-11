using Baton.Status;

namespace Baton.Memory;

/// <summary>
/// One canonical memory store found under a Baton root: the fleet store or one repository's.
/// </summary>
/// <param name="Slug">The directory name under the root — <see cref="FleetMemory.Slug"/> or a repository slug.</param>
/// <param name="Repository">Its durable repository identity, or null for a legacy store not yet backfilled.</param>
/// <param name="EntriesFile">Its <c>entries.jsonl</c>, which may be empty or absent while metadata remains.</param>
/// <param name="LinksFile">Its <c>links.jsonl</c>, which may not exist yet.</param>
/// <param name="IsFleet">Whether this is the reserved fleet store (#2112).</param>
public sealed record CanonicalStoreLocation(
    string Slug,
    string? Repository,
    string EntriesFile,
    string LinksFile,
    bool IsFleet);

/// <summary>
/// Every canonical memory store under a Baton root — the enumeration <c>baton memory sync</c> walks
/// and <c>baton memory audit</c> reports, spelled once (#2112).
/// </summary>
/// <remarks>
/// The enumeration is over the durable <c>store.json</c> metadata, with <c>entries.jsonl</c> retained
/// as the compatibility membership test for stores created before that metadata shipped. Q3's layout
/// still makes the directory the unit; unrelated root children and a repository directory holding
/// only a cost ledger are skipped. The fleet store is listed first, then the rest by slug, so a reader
/// meets the machine-wide store before any repository's — the same order every projection applies.
/// </remarks>
public static class CanonicalStoreInventory
{
    /// <param name="batonRoot">
    /// The root to walk. <see cref="BatonPaths.Root"/> for production callers; a test seam otherwise,
    /// for the reason <c>MemoryAuditCommand</c>'s own seam gives.
    /// </param>
    public static IReadOnlyList<CanonicalStoreLocation> Scan(string batonRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(batonRoot);

        if (!Directory.Exists(batonRoot))
        {
            return [];
        }

        var stores = new List<CanonicalStoreLocation>();
        foreach (var directory in Directory.EnumerateDirectories(batonRoot))
        {
            var slug = Path.GetFileName(directory);
            var memory = Path.Combine(directory, BatonPaths.MemoryDirectoryName);
            var entries = Path.Combine(memory, BatonPaths.MemoryEntriesFileName);
            var metadata = MemoryStoreMetadataStore.TryRead(slug);
            if (metadata is not null || File.Exists(entries))
            {
                stores.Add(new CanonicalStoreLocation(
                    slug,
                    metadata?.Repository,
                    entries,
                    Path.Combine(memory, BatonPaths.MemoryLinksFileName),
                    FleetMemory.IsFleet(slug)));
            }
        }

        return stores
            .OrderBy(s => s.IsFleet ? 0 : 1)
            .ThenBy(s => s.Slug, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
