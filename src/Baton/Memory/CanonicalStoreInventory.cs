using Baton.Status;

namespace Baton.Memory;

/// <summary>
/// One canonical memory store found under a Baton root: the fleet store or one repository's.
/// </summary>
/// <param name="Slug">The directory name under the root — <see cref="FleetMemory.Slug"/> or a repository slug.</param>
/// <param name="EntriesFile">Its <c>entries.jsonl</c>, which exists (that is the membership test).</param>
/// <param name="LinksFile">Its <c>links.jsonl</c>, which may not exist yet.</param>
/// <param name="IsFleet">Whether this is the reserved fleet store (#2112).</param>
public sealed record CanonicalStoreLocation(string Slug, string EntriesFile, string LinksFile, bool IsFleet);

/// <summary>
/// Every canonical memory store under a Baton root — the enumeration <c>baton memory sync</c> walks
/// and <c>baton memory audit</c> reports, spelled once (#2112).
/// </summary>
/// <remarks>
/// The enumeration is over <c>{root}/&lt;slug&gt;/memory/entries.jsonl</c> rather than over a
/// registry, because there is no registry: Q3's layout makes the directory the unit, so the
/// directories on disk ARE the list. A directory with no store file is not a store — <c>rooms/</c>,
/// <c>queue/</c> and the rest of the root sit beside them and are skipped by exactly that test, as is
/// a repository directory holding only the cost ledger #2041 moved in beside the store. The fleet
/// store is listed first, then the rest by slug, so a reader meets the machine-wide store before any
/// repository's — the same order every projection applies.
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
            if (File.Exists(entries))
            {
                stores.Add(new CanonicalStoreLocation(
                    slug,
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
