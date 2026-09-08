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
/// repository's memory store. Every production read goes through <see cref="ResolveForRead"/> and
/// every production write through <see cref="ResolveForWrite"/>;
/// <c>BatonPaths.CostLedgerFile</c> on its own names the canonical path but does not know whether a
/// file is still sitting at the old one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Migrated once, not read from both places</b> — the choice #2041 asked to be stated. A reader
/// that falls back to the old path while the writer appends to the new one splits one repository's
/// ledger across two files the moment anything settles, which is the defect being fixed rather than a
/// transition strategy for it. Relocating instead leaves exactly one file per repository at every
/// instant, and it is safe against concurrent Baton processes precisely because no resolution ever
/// hands a WRITER the legacy path: two processes racing here both take the legacy file's own
/// <see cref="MutexGuardedFileLock"/>, the loser re-checks and finds the move already done, and no
/// process can be appending to the legacy file meanwhile because none of them resolved to it.
/// </para>
/// <para>
/// <b>A move, never a re-serialization.</b> <see cref="CostLedgerEntry"/> has no catch-all for fields
/// it does not declare and <c>JsonLinesLedger.ReadAllUnlocked</c> skips malformed lines, so
/// reading the old file and writing its rows out again would silently drop whatever an older build
/// recorded — with the row count still matching. This ledger's stated value is durable, versioned
/// price provenance (spec/baton.md §7); the bytes move unchanged.
/// </para>
/// <para>
/// <b>Reads fail open; writes fail CLOSED.</b> A lock timeout or an I/O failure during the move leaves
/// the rows wherever they already are, so <see cref="ResolveForRead"/> returns whichever file holds
/// them — returning the canonical path there would make <c>baton ledger</c> print "nothing has settled
/// here" over a repository holding a full ledger, and a read creates nothing
/// (<c>JsonLinesLedger.ReadAllUnlocked</c> opens no file that is absent). <see cref="ResolveForWrite"/>
/// refuses instead, returning no path at all. It cannot do what the read arm does: the existence
/// decision and the caller's append are separate critical sections, so handing back the legacy path
/// lets a process whose wait timed out RECREATE that file after the winner already moved it — one
/// repository's ledger split across two files permanently, and silently, because every later
/// resolution then takes the both-files arm and reads only the canonical one. Losing one settle's row
/// deterministically is the cheaper failure, and it is the one spec/baton.md §7 accepts.
/// </para>
/// </remarks>
public static class CostLedgerLocation
{
    /// <summary>Same timeout <c>JsonLinesLedger</c> uses, for the same reason: the critical section is one rename.</summary>
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Canonical paths this process has already named in the both-files warning. Keyed by the CANONICAL
    /// path rather than the slug, so two roots (a test fixture's <c>BATON_HOME</c>, an operator with
    /// two) each get their own first warning; the warning is a one-time remedy instruction, and
    /// reprinting it on every <c>baton</c> command that resolves is noise nothing the operator does
    /// can end.
    /// </summary>
    private static readonly HashSet<string> WarnedBothFiles = new(StringComparer.OrdinalIgnoreCase);

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
    /// <see cref="Probe"/>'s read-only answer: the file that holds the rows today, and the canonical
    /// path a write would relocate it to first (<see langword="null"/> when no move is pending).
    /// </summary>
    public sealed record CostLedgerProbe(string Path, string? RelocatesTo);

    /// <summary>
    /// <see cref="ResolveForWrite(string)"/>'s answer: exactly one of the two is set. A caller holding a
    /// <see cref="Refusal"/> must append NOTHING and report it — see that method for why a path is not
    /// offered on the failure arm.
    /// </summary>
    /// <param name="Path">The file to append to, or <see langword="null"/> when the write is refused.</param>
    /// <param name="Refusal">Why nothing may be written, phrased for stderr; <see langword="null"/> on the success arm.</param>
    public sealed record CostLedgerWriteTarget(string? Path, string? Refusal);

    /// <summary>
    /// Which file currently holds <paramref name="repositorySlug"/>'s rows, and whether opening it for
    /// writing would relocate one first — <b>read-only</b>: two <see cref="File.Exists(string)"/> calls,
    /// no lock, no directory created, nothing moved. What a reporting caller uses when it must describe
    /// the ledger without touching it (<c>baton ledger backfill --dry-run</c>, whose report says nothing
    /// was written and must therefore have written nothing).
    /// </summary>
    /// <returns>
    /// <c>Path</c> is the file to read today — the legacy one only while it is the sole file present.
    /// <c>RelocatesTo</c> is the canonical path a write WOULD move it to first, or <see langword="null"/>
    /// when no move is pending (nothing at the legacy location, or both files present, which is left
    /// alone).
    /// </returns>
    public static CostLedgerProbe Probe(string repositorySlug)
    {
        ArgumentException.ThrowIfNullOrEmpty(repositorySlug);

        var canonical = BatonPaths.CostLedgerFile(repositorySlug);
        var legacy = BatonPaths.LegacyCostLedgerFile(repositorySlug);

        return File.Exists(canonical) || !File.Exists(legacy)
            ? new CostLedgerProbe(canonical, RelocatesTo: null)
            : new CostLedgerProbe(legacy, RelocatesTo: canonical);
    }

    /// <summary>
    /// The file to READ for <paramref name="repositorySlug"/>, relocating a legacy-location ledger onto
    /// the canonical path first when one is there. See the type remarks for why it relocates rather
    /// than reading both, and why this arm falls back to the rows while
    /// <see cref="ResolveForWrite(string)"/> refuses.
    /// <para>
    /// <b>Both files existing is left alone, warned about, and not merged.</b> That state is reachable
    /// only from an older build writing the legacy path after this one moved it, or from a restored
    /// backup — merging would mean re-serializing rows, which the type remarks rule out. The canonical
    /// path is returned and the legacy file is named on stderr once per process; spec/baton.md §7's
    /// accepted-losses paragraph carries the ruling.
    /// </para>
    /// </summary>
    /// <param name="repositorySlug">Whatever <see cref="BatonPaths.CostLedgerFile"/>'s own parameter doc requires.</param>
    public static string ResolveForRead(string repositorySlug)
    {
        ArgumentException.ThrowIfNullOrEmpty(repositorySlug);

        var (canonical, failure) = TryRelocate(repositorySlug, LockTimeout);
        if (failure is null)
        {
            return canonical;
        }

        // Fail open toward the data: the rows did not move, so read them where they are. Safe in a way
        // the write side is not -- a read creates neither the file nor its parent directory, so this
        // cannot resurrect a legacy location another process just emptied.
        var fallback = Probe(repositorySlug).Path;
        Console.Error.WriteLine($"{failure} Reading '{fallback}' instead; this read writes nothing.");
        return fallback;
    }

    /// <summary>
    /// The file to APPEND to for <paramref name="repositorySlug"/>, or a refusal the caller must obey by
    /// not appending and saying so on stderr. Relocates a legacy-location ledger first, exactly as
    /// <see cref="ResolveForRead"/> does.
    /// <para>
    /// <b>Why a refusal rather than the legacy path</b> — the type remarks carry it: a caller's append
    /// is a separate critical section from this decision, so a path handed back after a lock timeout can
    /// be RECREATED by this process after another one already moved it, splitting the ledger for good.
    /// The cost is one settle's row, and both settle sites are already fail-open (they log and swallow),
    /// so the refusal is reported rather than raised.
    /// </para>
    /// </summary>
    public static CostLedgerWriteTarget ResolveForWrite(string repositorySlug) =>
        ResolveForWrite(repositorySlug, LockTimeout);

    /// <summary>
    /// Test-only seam (Baton.Tests / Baton.Cli.Tests, via <c>InternalsVisibleTo</c>):
    /// <see cref="ResolveForWrite(string)"/> with the lock budget shortened, so a test can hold the
    /// legacy file's mutex and observe the refusal arm without waiting out the production timeout.
    /// </summary>
    internal static CostLedgerWriteTarget ResolveForWrite(string repositorySlug, TimeSpan lockTimeout)
    {
        ArgumentException.ThrowIfNullOrEmpty(repositorySlug);

        var (canonical, failure) = TryRelocate(repositorySlug, lockTimeout);
        return failure is null
            ? new CostLedgerWriteTarget(canonical, Refusal: null)
            : new CostLedgerWriteTarget(
                Path: null,
                Refusal: $"{failure} Nothing will be appended: writing to the old location now could "
                    + "split this repository's ledger across two files permanently.");
    }

    /// <summary>
    /// The shared half of both resolutions: relocate if there is anything to relocate, and report the
    /// canonical path or the reason it could not be reached. Never throws — <b>and never leaves the
    /// caller holding a path a writer may create at the legacy location</b>, which is the whole point of
    /// returning the failure separately rather than a path.
    /// </summary>
    private static (string Canonical, string? Failure) TryRelocate(string repositorySlug, TimeSpan lockTimeout)
    {
        var canonical = BatonPaths.CostLedgerFile(repositorySlug);
        var legacy = BatonPaths.LegacyCostLedgerFile(repositorySlug);

        // The overwhelmingly common case, and the only one on a machine that never ran a pre-#2041
        // build: no lock, no I/O beyond one existence check.
        if (!File.Exists(legacy))
        {
            return (canonical, null);
        }

        // Both files present: there is nothing to move, so nothing to lock either. Checked out here
        // rather than only under the lock because this state is permanent -- the legacy file is
        // deliberately never removed -- and paying a lock acquisition on every resolve for a decision
        // that can never change is what made the warning below recur with a 30s worst case behind it.
        if (File.Exists(canonical))
        {
            WarnBothFilesOnce(legacy, canonical);
            return (canonical, null);
        }

        try
        {
            // The legacy file's OWN lock name -- the prefix a pre-#2041 build takes out against it --
            // so a concurrent older writer serializes against this move instead of racing it.
            return (
                MutexGuardedFileLock.RunUnderLock(
                    legacy,
                    CostLedgerStore.Ledger.LockNamePrefix,
                    lockTimeout,
                    () => Relocate(repositorySlug, legacy, canonical)),
                null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            return (canonical, $"Could not relocate the cost ledger '{legacy}' to '{canonical}': {ex.Message}.");
        }
    }

    /// <summary>
    /// The critical section: re-checks under the lock (the racing process may have moved it already),
    /// moves, and records the move. Callers must hold the legacy file's
    /// <see cref="MutexGuardedFileLock"/>.
    /// <para>
    /// <b>Rests on both paths sharing a volume</b> — <c>{Root}/ledger/</c> and <c>{Root}/&lt;slug&gt;/</c>
    /// do on any ordinary machine, which is what makes <see cref="File.Move(string,string)"/> a rename
    /// rather than a copy-then-delete. <c>BATON_HOME</c> is operator-settable and either directory could
    /// be a junction, though, and across volumes a failure mid-copy leaves a PARTIAL canonical file
    /// beside an intact legacy one — which the both-files arm above would then read as complete billing
    /// history. A root comparison would not catch that (both paths spell the same root, and a junction
    /// is invisible to <see cref="Path.GetPathRoot(string)"/>), so the guard is the cleanup below rather
    /// than a precondition: the partial file is removed and the legacy file is left as the whole truth.
    /// </para>
    /// </summary>
    private static string Relocate(string repositorySlug, string legacy, string canonical)
    {
        if (!File.Exists(legacy))
        {
            return canonical;
        }

        if (File.Exists(canonical))
        {
            WarnBothFilesOnce(legacy, canonical);
            return canonical;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(canonical)!);
        try
        {
            File.Move(legacy, canonical);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only when the source survived: a failure AFTER the source was gone would make this
            // partial file the only copy there is, and deleting it would be the loss itself.
            if (File.Exists(legacy) && File.Exists(canonical))
            {
                try
                {
                    File.Delete(canonical);
                }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine(
                        $"Could not remove the partial cost ledger '{canonical}' left by a failed relocation: "
                        + $"{cleanup.Message}. Reconcile it against '{legacy}' by hand before the next run.");
                }
            }

            throw new IOException($"Could not move '{legacy}' to '{canonical}': {ex.Message}", ex);
        }

        RecordRelocation(new CostLedgerRelocation(DateTime.UtcNow, repositorySlug, legacy, canonical));
        return canonical;
    }

    /// <summary>
    /// Names the legacy file beside a canonical one <b>once per process per canonical path</b>, with the
    /// remedy — a warning an operator cannot act on, reprinted on every command, is noise rather than a
    /// disclosure.
    /// </summary>
    private static void WarnBothFilesOnce(string legacy, string canonical)
    {
        lock (WarnedBothFiles)
        {
            if (!WarnedBothFiles.Add(canonical))
            {
                return;
            }
        }

        Console.Error.WriteLine(
            $"A pre-#2041 cost ledger is still at '{legacy}' while the current one is at '{canonical}'. "
            + "Reading and writing the current one; the old file is left untouched and unread -- move it "
            + "aside or delete it once you have reconciled its rows against the current file, and nothing "
            + "will look at it again.");
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
