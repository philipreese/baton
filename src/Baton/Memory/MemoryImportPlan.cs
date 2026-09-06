namespace Baton.Memory;

/// <summary>
/// One memory file as read for import: its provenance, its digest, and its text.
/// </summary>
/// <remarks>
/// <b>The text is here because the importer's job is to copy it.</b> That is the one place #1852's
/// memory work reads a memory's contents at all — <c>baton memory audit</c> never does, and even here
/// the text goes straight into a store entry and is never parsed for meaning, printed, or used to
/// decide anything (<see cref="MemoryKindInference"/> reads a declared front-matter key and nothing
/// else).
/// </remarks>
/// <param name="Path">Absolute path of the source file.</param>
/// <param name="FileName">Its name, which is what the kind-prefix table reads.</param>
/// <param name="Text">Its text, verbatim.</param>
/// <param name="Sha256">Lower-case hex SHA-256 of its bytes.</param>
/// <param name="ModifiedUtc">Its last-write time.</param>
/// <param name="SizeBytes">Its length.</param>
public sealed record MemoryImportFile(
    string Path,
    string FileName,
    string Text,
    string Sha256,
    DateTime ModifiedUtc,
    long SizeBytes);

/// <summary>
/// One memory root as offered to the import: which repository it maps to (or why it maps to none),
/// and every file under it.
/// </summary>
/// <param name="RootDirectoryPath">The root's own directory.</param>
/// <param name="SourceVendor">The vendor whose root it is (<c>claude</c>, <c>codex</c>).</param>
/// <param name="SourceScope">Vendor-owned or Baton-managed.</param>
/// <param name="Archived">
/// Whether this is an archived root. Q2 (operator, 2026-09-05) makes every entry from one a
/// <see cref="MemoryKind.HistoricalNote"/>, whatever the file declares.
/// </param>
/// <param name="Repository">
/// The subject every entry from this root is filed under, or <see langword="null"/> when none could be
/// established — in which case <paramref name="UnfiledReason"/> says why and nothing is imported.
/// </param>
/// <param name="UnfiledReason">Why this root produced no entries. Ignored when <paramref name="Repository"/> is set.</param>
/// <param name="Files">The root's files, already read.</param>
public sealed record MemoryImportSource(
    string RootDirectoryPath,
    string SourceVendor,
    VendorMemoryScope SourceScope,
    bool Archived,
    string? Repository,
    string? UnfiledReason,
    IReadOnlyList<MemoryImportFile> Files);

/// <summary>
/// What an import would write: the entries, and an accounting of every file that produces none.
/// </summary>
/// <param name="Entries">The entries to append, ordered by subject then source path.</param>
/// <param name="Unfiled">Files in a root with no resolvable subject, each with the reason.</param>
public sealed record MemoryImportPlan(
    IReadOnlyList<MemoryEntry> Entries,
    IReadOnlyList<ImportSkippedRow> Unfiled)
{
    /// <summary>
    /// Turns already-read roots into the entries they produce. <b>Pure</b>: no filesystem, no git, no
    /// clock — <paramref name="importedAtUtc"/> is passed in — so the same roots produce the same plan,
    /// and <c>--dry-run</c> is the same computation as the real thing with the write left off rather
    /// than a second implementation of it.
    /// </summary>
    /// <param name="sources">The roots, in the order the caller wants them accounted for.</param>
    /// <param name="importedAtUtc">Stamped on every entry. Deliberately not part of any entry's id.</param>
    public static MemoryImportPlan Build(IReadOnlyList<MemoryImportSource> sources, DateTime importedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var entries = new List<MemoryEntry>();
        var unfiled = new List<ImportSkippedRow>();

        foreach (var source in sources)
        {
            foreach (var file in source.Files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
            {
                if (source.Repository is not { Length: > 0 } repository)
                {
                    unfiled.Add(new ImportSkippedRow(
                        file.Path,
                        file.Sha256,
                        file.ModifiedUtc,
                        file.SizeBytes,
                        source.UnfiledReason ?? "no repository identity could be established for this root."));
                    continue;
                }

                var (kind, kindSource) = source.Archived
                    ? (MemoryKind.HistoricalNote, MemoryKindSource.InferredFromArchive)
                    : MemoryKindInference.Infer(file.FileName, file.Text);

                entries.Add(new MemoryEntry(
                    MemoryEntry.Derive(repository, file.Path, file.Sha256),
                    repository,
                    kind,
                    kindSource,
                    file.Text,
                    file.Sha256,
                    file.Path,
                    source.SourceVendor,
                    source.SourceScope,
                    file.ModifiedUtc,
                    importedAtUtc));
            }
        }

        return new MemoryImportPlan(LinkSupersession(entries), unfiled);
    }

    /// <summary>
    /// Adds the Q2 supersession links: an entry from a live root supersedes one from an archived root
    /// when the two share a <b>subject</b> and a <b>file name</b> and their digests differ.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>All three conditions are load-bearing, and two of them are refusals.</b> Same subject,
    /// because a file called <c>MEMORY.md</c> under one repository says nothing about another's — a
    /// cross-subject link would be the "one repository's memory leaks into another checkout" failure
    /// the per-repository layout exists to make structurally impossible, reintroduced by a name match.
    /// Different digest, because two identical files are one fact stored twice, not a fact and its
    /// replacement; linking them would tell an operator an unchanged memory had been superseded by
    /// itself.
    /// </para>
    /// <para>
    /// <b>Only live-over-archived.</b> The audit refused per-file supersession precisely because a name
    /// collision alone is not evidence (<c>MemoryAuditReport</c>'s <c>stale</c> finding is per-root for
    /// that reason). What phase B adds is not a better heuristic but a stronger premise: the archive is
    /// a snapshot an undocumented migration took of the live roots, so live-over-archived is a
    /// direction the population itself supplies. Two live roots holding the same name are NOT linked —
    /// nothing here knows which is newer, and mtime is a property of the file rather than of the fact.
    /// </para>
    /// <para>
    /// The link is written on both entries: an archived entry read on its own says it was superseded,
    /// without the reader having to scan every other entry to find out.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<MemoryEntry> LinkSupersession(List<MemoryEntry> entries)
    {
        var supersedes = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var supersededBy = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var live in entries.Where(e => e.KindSource != MemoryKindSource.InferredFromArchive))
        {
            var liveName = Path.GetFileName(live.SourcePath);
            foreach (var archived in entries.Where(e => e.KindSource == MemoryKindSource.InferredFromArchive))
            {
                if (!string.Equals(live.Repository, archived.Repository, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(liveName, Path.GetFileName(archived.SourcePath), StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(live.Sha256, archived.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Add(supersedes, live.Id, archived.Id);
                Add(supersededBy, archived.Id, live.Id);
            }
        }

        return entries
            .Select(entry => entry with
            {
                Supersedes = Ordered(supersedes, entry.Id),
                SupersededBy = Ordered(supersededBy, entry.Id),
            })
            .OrderBy(e => e.Repository, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        static void Add(Dictionary<string, List<string>> map, string key, string value)
        {
            if (!map.TryGetValue(key, out var list))
            {
                list = [];
                map[key] = list;
            }

            list.Add(value);
        }

        // Null rather than an empty list: MemoryEntry's own doc states why "supersedes nothing" must
        // be an absent field rather than a present empty one.
        static IReadOnlyList<string>? Ordered(Dictionary<string, List<string>> map, string key) =>
            map.TryGetValue(key, out var list) ? list.Order(StringComparer.Ordinal).ToList() : null;
    }
}
