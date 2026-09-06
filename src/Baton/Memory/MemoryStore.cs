using System.Text;
using System.Text.Json;
using Baton.Status;

namespace Baton.Memory;

/// <summary>
/// Reads and writes one repository's canonical memory files —
/// <c>{BatonPaths.Root}/&lt;repo-slug&gt;/memory/entries.jsonl</c> and its
/// <c>links.jsonl</c> (#1852 phase B, Q3's layout) — as immutable append-only JSONL.
/// </summary>
/// <remarks>
/// <para>
/// <b>The same store <c>CostLedgerStore</c> uses, not a second one.</b> Entries go through
/// <see cref="JsonLinesLedger{TEntry}"/> and, under it, <see cref="MutexGuardedFileLock"/>, under this
/// store's own lock-name prefix so the memory files never contend with either ledger's. A third
/// concurrency mechanism in this tree is precisely what those types' remarks exist to prevent.
/// </para>
/// <para>
/// <b>Two files, because supersession is a fact about a PAIR of entries and entries are immutable.</b>
/// <see cref="MemorySupersessionLink"/>'s own remarks carry why a link cannot live on the entry row;
/// what this type adds is the reader that puts them back together, <see cref="ReadResolvedAsync"/>. A
/// stored entry row therefore never carries <see cref="MemoryEntry.Supersedes"/> or
/// <see cref="MemoryEntry.SupersededBy"/>: those two fields are populated on the way OUT, from the
/// links file, and <see cref="ReadAllAsync"/> is the raw read that shows the rows as they sit on disk.
/// </para>
/// <para>
/// <b>Append is idempotent because the id is derived</b> (<see cref="MemoryEntry.Derive"/>): the
/// ledger skips a row whose key is already present, so a second import of an unchanged file writes
/// nothing. Nothing here overwrites a row — an edited source file is a new entry, and the old one
/// stays as the history it is.
/// </para>
/// <para>
/// <b>Throws rather than swallowing</b>, exactly as the ledgers do: a caller that wants the fail-open
/// posture logs and swallows at its own site. Unlike the ledgers, the caller here is an operator-run
/// verb rather than a settle site, so <c>MemoryImportCommand</c> reports the failure rather than
/// discarding it — an import that silently wrote nothing is the failure mode this store exists to
/// make impossible.
/// </para>
/// </remarks>
public static class MemoryStore
{
    /// <summary>
    /// This store's shared ledger. <c>baton-memory-store</c> is deliberately unlike
    /// <c>baton-cost-ledger</c>'s and <c>QuotaLedgerStore</c>'s prefixes, so the files never contend;
    /// see <see cref="JsonLinesLedger{TEntry}"/>'s remarks for what renaming an existing prefix costs.
    /// </summary>
    internal static readonly JsonLinesLedger<MemoryEntry> Ledger =
        new("baton-memory-store", "memory store", entry => entry.Id);

    /// <summary>
    /// The links file's own ledger, under its own lock prefix — a distinct file takes a distinct lock,
    /// for the reason <see cref="JsonLinesLedger{TEntry}"/>'s remarks give, and the two are never held
    /// at once (<see cref="ReadResolvedAsync"/> reads one and then the other).
    /// </summary>
    internal static readonly JsonLinesLedger<MemorySupersessionLink> LinkLedger =
        new("baton-memory-links", "memory supersession links", link => link.Id);

    /// <summary>
    /// Appends the subset of <paramref name="entries"/> whose <see cref="MemoryEntry.Id"/> is not
    /// already in <paramref name="entriesFilePath"/>, in one read-check-then-append critical section.
    /// </summary>
    public static Task AppendAsync(
        IReadOnlyList<MemoryEntry> entries, string entriesFilePath, CancellationToken cancellationToken = default) =>
        Ledger.AppendAsync(entries, entriesFilePath, cancellationToken);

    /// <summary>
    /// This file's entries as they sit on disk, oldest first — <b>with no supersession resolved</b>.
    /// The read for a caller that is about to compute links or rewrite rows;
    /// <see cref="ReadResolvedAsync"/> is the one for a caller that wants to read a memory.
    /// </summary>
    public static Task<IReadOnlyList<MemoryEntry>> ReadAllAsync(
        string entriesFilePath, CancellationToken cancellationToken = default) =>
        Ledger.ReadAllAsync(entriesFilePath, cancellationToken);

    /// <summary>
    /// Appends the subset of <paramref name="links"/> whose <see cref="MemorySupersessionLink.Id"/> is
    /// not already in <paramref name="linksFilePath"/>. Idempotent for the same reason
    /// <see cref="AppendAsync"/> is: the id is the pair, so recomputing a link that is already recorded
    /// writes nothing.
    /// </summary>
    public static Task AppendLinksAsync(
        IReadOnlyList<MemorySupersessionLink> links, string linksFilePath, CancellationToken cancellationToken = default) =>
        LinkLedger.AppendAsync(links, linksFilePath, cancellationToken);

    /// <summary>This file's supersession links, oldest first.</summary>
    public static Task<IReadOnlyList<MemorySupersessionLink>> ReadLinksAsync(
        string linksFilePath, CancellationToken cancellationToken = default) =>
        LinkLedger.ReadAllAsync(linksFilePath, cancellationToken);

    /// <summary>
    /// One repository's entries with their supersession resolved from
    /// <paramref name="linksFilePath"/> — the read every consumer of a memory wants.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A link whose two entries are not BOTH present is dropped.</b> The store is append-only and
    /// the links file is its own file, so a dangling link is reachable in ordinary operation — an undo
    /// that removed entries, a hand-edited store, a partially replayed manifest. Resolving one anyway
    /// would put an id into <see cref="MemoryEntry.Supersedes"/> that names no entry in this store,
    /// which reads as "superseded by something you cannot see" rather than as the absence it is.
    /// Dropping it fails closed: the link reappears the moment the missing entry is imported again,
    /// because the link row itself was never removed.
    /// </para>
    /// <para>
    /// <b>Two sequential reads, never two nested locks.</b> Each ledger takes its own file's lock and
    /// releases it before the next is acquired; holding one while acquiring the other is how two
    /// callers taking them in opposite orders deadlock.
    /// </para>
    /// </remarks>
    public static async Task<IReadOnlyList<MemoryEntry>> ReadResolvedAsync(
        string entriesFilePath, string linksFilePath, CancellationToken cancellationToken = default)
    {
        var entries = await ReadAllAsync(entriesFilePath, cancellationToken).ConfigureAwait(false);
        var links = await ReadLinksAsync(linksFilePath, cancellationToken).ConfigureAwait(false);

        return Resolve(entries, links);
    }

    /// <summary>
    /// <paramref name="entries"/> with <see cref="MemoryEntry.Supersedes"/> and
    /// <see cref="MemoryEntry.SupersededBy"/> filled in from <paramref name="links"/>. Pure, so the
    /// projection is testable without a filesystem and is the same computation wherever it is applied.
    /// </summary>
    public static IReadOnlyList<MemoryEntry> Resolve(
        IReadOnlyList<MemoryEntry> entries, IReadOnlyList<MemorySupersessionLink> links)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(links);

        if (links.Count == 0)
        {
            return entries;
        }

        var present = entries.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var supersedes = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var supersededBy = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);

        foreach (var link in links)
        {
            if (!present.Contains(link.SupersedingId) || !present.Contains(link.SupersededId))
            {
                continue;
            }

            Add(supersedes, link.SupersedingId, link.SupersededId);
            Add(supersededBy, link.SupersededId, link.SupersedingId);
        }

        return entries
            .Select(entry => entry with
            {
                Supersedes = Ordered(supersedes, entry.Id),
                SupersededBy = Ordered(supersededBy, entry.Id),
            })
            .ToList();

        static void Add(Dictionary<string, SortedSet<string>> map, string key, string value)
        {
            if (!map.TryGetValue(key, out var set))
            {
                set = new SortedSet<string>(StringComparer.Ordinal);
                map[key] = set;
            }

            set.Add(value);
        }

        // Null rather than an empty list: MemoryEntry's own doc states why "supersedes nothing" must
        // be an absent field rather than a present empty one.
        static IReadOnlyList<string>? Ordered(Dictionary<string, SortedSet<string>> map, string key) =>
            map.TryGetValue(key, out var set) ? set.ToList() : null;
    }

    /// <summary>
    /// Removes exactly the rows in <paramref name="entryIds"/> from <paramref name="entriesFilePath"/>
    /// and returns how many were removed — <b>the undo half of a reversible import</b>, and the only
    /// operation in this namespace that unwrites anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why an append-only store has a remove at all.</b> "Reversible" is #1852's acceptance
    /// criterion, and a manifest that cannot be replayed backwards is a list, not a reversal. The
    /// narrowness is what keeps the append-only property meaningful: this removes an enumerated set of
    /// ids and nothing else, it is reachable only from <c>baton memory import --undo &lt;manifest&gt;</c>,
    /// and an id not in the file is not an error — a partially-undone import must be re-undoable, and
    /// an undo that threw on its second run would leave an operator with no way to finish one.
    /// </para>
    /// <para>
    /// <b>One lock acquisition, read-filter-rewrite.</b> Two would let a concurrent append land in the
    /// gap and be silently truncated away by the rewrite. The temp-file-then-move and the BOM-free
    /// UTF-8 are <c>QuotaLedgerStore.WriteAllUnlocked</c>'s discipline, for the reasons stated there:
    /// a reader under the same lock never sees a half-written file, and a rewritten file's bytes stay
    /// indistinguishable from an appended one's.
    /// </para>
    /// </remarks>
    public static Task<int> RemoveAsync(
        IReadOnlyList<string> entryIds, string entriesFilePath, CancellationToken cancellationToken = default) =>
        RemoveRowsAsync(Ledger, entry => entry.Id, entryIds, entriesFilePath, cancellationToken);

    /// <summary>
    /// The links half of the same reversal: removes exactly the link rows in
    /// <paramref name="linkIds"/> and returns how many were removed. An undo that removed an import's
    /// entries and left its links behind would leave rows that resolve to nothing
    /// (<see cref="ReadResolvedAsync"/> drops them) and would silently re-link the moment those
    /// entries were imported again by some later run.
    /// </summary>
    public static Task<int> RemoveLinksAsync(
        IReadOnlyList<string> linkIds, string linksFilePath, CancellationToken cancellationToken = default) =>
        RemoveRowsAsync(LinkLedger, link => link.Id, linkIds, linksFilePath, cancellationToken);

    /// <summary>
    /// <see cref="RemoveAsync"/>'s body, over either of this store's two ledgers. One implementation
    /// rather than two: the read-filter-rewrite-under-one-lock discipline the remarks above describe is
    /// the part that is easy to get subtly wrong, and a second copy of it is a second place to get it
    /// wrong in.
    /// </summary>
    private static Task<int> RemoveRowsAsync<TRow>(
        JsonLinesLedger<TRow> ledger,
        Func<TRow, string> keySelector,
        IReadOnlyList<string> keys,
        string filePath,
        CancellationToken cancellationToken)
        where TRow : class
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        if (keys.Count == 0 || !File.Exists(filePath))
        {
            return Task.FromResult(0);
        }

        var removing = keys.ToHashSet(StringComparer.Ordinal);

        return ledger.RunUnderLockAsync(
            filePath,
            () =>
            {
                var kept = new List<TRow>();
                var removed = 0;
                foreach (var row in ledger.ReadAllUnlocked(filePath))
                {
                    if (removing.Contains(keySelector(row)))
                    {
                        removed++;
                    }
                    else
                    {
                        kept.Add(row);
                    }
                }

                if (removed > 0)
                {
                    WriteAllUnlocked(ledger, filePath, kept);
                }

                return removed;
            },
            cancellationToken);
    }

    /// <summary>
    /// Replaces the file's contents with one JSON line per row, atomically. Callers must already
    /// hold that ledger's <see cref="MutexGuardedFileLock"/>; this method takes none.
    /// </summary>
    private static void WriteAllUnlocked<TRow>(
        JsonLinesLedger<TRow> ledger, string filePath, IReadOnlyList<TRow> rows)
        where TRow : class
    {
        var tempPath = $"{filePath}.{Guid.NewGuid():N}.tmp";
        var builder = new StringBuilder();
        foreach (var row in rows)
        {
            builder.Append(JsonSerializer.Serialize(row, ledger.SerializerOptions)).Append('\n');
        }

        File.WriteAllBytes(tempPath, Encoding.UTF8.GetBytes(builder.ToString()));
        File.Move(tempPath, filePath, overwrite: true);
    }
}
