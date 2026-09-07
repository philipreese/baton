using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Status;

namespace Baton.Accounting;

/// <summary>
/// <b>The one resolver of "which file is this repository's cost ledger"</b> (#2041). #1852's Q3 layout
/// makes the per-repository directory the unit, so the ledger moved from
/// <c>BatonPaths.LegacyCostLedgerFile</c> (<c>{Root}/ledger/&lt;slug&gt;.jsonl</c>) to
/// <c>BatonPaths.CostLedgerFile</c> (<c>{Root}/&lt;slug&gt;/cost-ledger.jsonl</c>) beside that
/// repository's memory store. Every production read and write goes through <see cref="Resolve"/>;
/// <c>BatonPaths.CostLedgerFile</c> on its own names the canonical path but does not know whether a
/// file is still sitting at the old one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Migrated once, not read from both places</b> — the choice #2041 asked to be stated. A reader
/// that falls back to the old path while the writer appends to the new one splits one repository's
/// ledger across two files the moment anything settles, which is the defect being fixed rather than a
/// transition strategy for it. Relocating instead leaves exactly one file per repository at every
/// instant, and it is safe against concurrent Baton processes precisely because <see cref="Resolve"/>
/// never returns the legacy path on the success arm: two processes racing here both take the legacy
/// file's own <see cref="MutexGuardedFileLock"/>, the loser re-checks and finds the move already done,
/// and no process can be appending to the legacy file meanwhile because none of them resolved to it.
/// </para>
/// <para>
/// <b>A move, never a re-serialization.</b> <see cref="CostLedgerEntry"/> has no catch-all for fields
/// it does not declare and <c>JsonLinesLedger.ReadAllUnlocked</c> skips malformed lines, so
/// reading the old file and writing its rows out again would silently drop whatever an older build
/// recorded — with the row count still matching. This ledger's stated value is durable, versioned
/// price provenance (spec/baton.md §7); the bytes move unchanged.
/// </para>
/// <para>
/// <b>Fails open, and fails open toward the data.</b> A lock timeout or an I/O failure during the move
/// returns the LEGACY path, so a read still sees the rows and a write still lands where they already
/// are. Returning the canonical path there would make <c>baton ledger</c> print "nothing has settled
/// here" over a repository holding a full ledger.
/// </para>
/// </remarks>
public static class CostLedgerLocation
{
    /// <summary>Same timeout <c>JsonLinesLedger</c> uses, for the same reason: the critical section is one rename.</summary>
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// One line of <c>BatonPaths.CostLedgerMigrationFile</c>: a ledger that WAS relocated, when, and
    /// between which two paths. Written only on the arm that actually moved bytes, so the file is a
    /// record of relocations rather than of resolutions — an operator who finds
    /// <c>~/.baton/ledger</c> empty has one place to learn where it went.
    /// </summary>
    public sealed record CostLedgerRelocation(
        [property: JsonPropertyName("at")] DateTime At,
        [property: JsonPropertyName("repository")] string Repository,
        [property: JsonPropertyName("from")] string From,
        [property: JsonPropertyName("to")] string To);

    /// <summary>
    /// The file to open for <paramref name="repositorySlug"/>, relocating a legacy-location ledger onto
    /// the canonical path first when one is there. See the type remarks for why it relocates rather
    /// than reading both, and what it returns when the relocation itself fails.
    /// <para>
    /// <b>Both files existing is left alone, warned about, and not merged.</b> That state is reachable
    /// only from an older build writing the legacy path after this one moved it, or from a restored
    /// backup — merging would mean re-serializing rows, which the type remarks rule out. The canonical
    /// path is returned and the legacy file is named on stderr; spec/baton.md §7's accepted-losses
    /// paragraph carries the ruling.
    /// </para>
    /// </summary>
    /// <param name="repositorySlug">Whatever <see cref="BatonPaths.CostLedgerFile"/>'s own parameter doc requires.</param>
    public static string Resolve(string repositorySlug)
    {
        ArgumentException.ThrowIfNullOrEmpty(repositorySlug);

        var canonical = BatonPaths.CostLedgerFile(repositorySlug);
        var legacy = BatonPaths.LegacyCostLedgerFile(repositorySlug);

        // The overwhelmingly common case, and the only one on a machine that never ran a pre-#2041
        // build: no lock, no I/O beyond one existence check.
        if (!File.Exists(legacy))
        {
            return canonical;
        }

        try
        {
            // The legacy file's OWN lock name -- the prefix a pre-#2041 build takes out against it --
            // so a concurrent older writer serializes against this move instead of racing it.
            return MutexGuardedFileLock.RunUnderLock(
                legacy,
                CostLedgerStore.Ledger.LockNamePrefix,
                LockTimeout,
                () => Relocate(repositorySlug, legacy, canonical));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            Console.Error.WriteLine(
                $"Could not relocate the cost ledger '{legacy}' to '{canonical}': {ex.Message}. "
                + "Reading and writing the old location instead.");
            return legacy;
        }
    }

    /// <summary>
    /// The critical section: re-checks under the lock (the racing process may have moved it already),
    /// moves, and records the move. Callers must hold the legacy file's
    /// <see cref="MutexGuardedFileLock"/>.
    /// </summary>
    private static string Relocate(string repositorySlug, string legacy, string canonical)
    {
        if (!File.Exists(legacy))
        {
            return canonical;
        }

        if (File.Exists(canonical))
        {
            Console.Error.WriteLine(
                $"A pre-#2041 cost ledger is still at '{legacy}' while the current one is at '{canonical}'. "
                + "Reading and writing the current one; the old file is left untouched and unread.");
            return canonical;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(canonical)!);
        File.Move(legacy, canonical);
        RecordRelocation(new CostLedgerRelocation(DateTime.UtcNow, repositorySlug, legacy, canonical));
        return canonical;
    }

    /// <summary>
    /// Appends one <see cref="CostLedgerRelocation"/> line. <b>Never fails the relocation</b>: the move
    /// has already happened and is the thing that mattered, so a manifest that could not be written is
    /// reported on stderr rather than thrown back at a settle site that is itself fail-open.
    /// </summary>
    private static void RecordRelocation(CostLedgerRelocation relocation)
    {
        try
        {
            var path = BatonPaths.CostLedgerMigrationFile;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, JsonSerializer.Serialize(relocation) + "\n", Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(
                $"Relocated the cost ledger to '{relocation.To}' but could not record it in "
                + $"'{BatonPaths.CostLedgerMigrationFile}': {ex.Message}.");
        }
    }
}
