using System.Text;
using System.Text.Json;
using Baton.Status;

namespace Baton.Memory;

/// <summary>
/// Reads and writes one repository's canonical memory file —
/// <c>{BatonPaths.Root}/&lt;repo-slug&gt;/memory/entries.jsonl</c> (#1852 phase B, Q3's layout) — as
/// immutable append-only JSONL.
/// </summary>
/// <remarks>
/// <para>
/// <b>The same store <c>CostLedgerStore</c> uses, not a second one.</b> Entries go through
/// <see cref="JsonLinesLedger{TEntry}"/> and, under it, <see cref="MutexGuardedFileLock"/>, under this
/// store's own lock-name prefix so the memory files never contend with either ledger's. A third
/// concurrency mechanism in this tree is precisely what those types' remarks exist to prevent.
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
    /// Appends the subset of <paramref name="entries"/> whose <see cref="MemoryEntry.Id"/> is not
    /// already in <paramref name="entriesFilePath"/>, in one read-check-then-append critical section.
    /// </summary>
    public static Task AppendAsync(
        IReadOnlyList<MemoryEntry> entries, string entriesFilePath, CancellationToken cancellationToken = default) =>
        Ledger.AppendAsync(entries, entriesFilePath, cancellationToken);

    /// <summary>This file's entries, oldest first.</summary>
    public static Task<IReadOnlyList<MemoryEntry>> ReadAllAsync(
        string entriesFilePath, CancellationToken cancellationToken = default) =>
        Ledger.ReadAllAsync(entriesFilePath, cancellationToken);

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
        IReadOnlyList<string> entryIds, string entriesFilePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entryIds);
        ArgumentException.ThrowIfNullOrEmpty(entriesFilePath);

        if (entryIds.Count == 0 || !File.Exists(entriesFilePath))
        {
            return Task.FromResult(0);
        }

        var removing = entryIds.ToHashSet(StringComparer.Ordinal);

        return Ledger.RunUnderLockAsync(
            entriesFilePath,
            () =>
            {
                var kept = new List<MemoryEntry>();
                var removed = 0;
                foreach (var entry in Ledger.ReadAllUnlocked(entriesFilePath))
                {
                    if (removing.Contains(entry.Id))
                    {
                        removed++;
                    }
                    else
                    {
                        kept.Add(entry);
                    }
                }

                if (removed > 0)
                {
                    WriteAllUnlocked(entriesFilePath, kept);
                }

                return removed;
            },
            cancellationToken);
    }

    /// <summary>
    /// Replaces the file's contents with one JSON line per entry, atomically. Callers must already
    /// hold this store's <see cref="MutexGuardedFileLock"/>; this method takes none.
    /// </summary>
    private static void WriteAllUnlocked(string entriesFilePath, IReadOnlyList<MemoryEntry> entries)
    {
        var tempPath = $"{entriesFilePath}.{Guid.NewGuid():N}.tmp";
        var builder = new StringBuilder();
        foreach (var entry in entries)
        {
            builder.Append(JsonSerializer.Serialize(entry, Ledger.SerializerOptions)).Append('\n');
        }

        File.WriteAllBytes(tempPath, Encoding.UTF8.GetBytes(builder.ToString()));
        File.Move(tempPath, entriesFilePath, overwrite: true);
    }
}
