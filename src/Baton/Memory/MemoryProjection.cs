using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Baton.Memory;

/// <summary>
/// Where a projection candidate's authority comes from (#1852 phase C). The projector compares nothing
/// but this and a digest — it never reads what either fact <i>says</i>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MemoryFactOrigin>))]
public enum MemoryFactOrigin
{
    /// <summary>
    /// A fact checked into the repository itself. Wins every conflict, by #1852's ratified authority
    /// model (operator, 2026-09-04): repository truth outranks the canonical store, which outranks
    /// execution evidence, which outranks these projections.
    /// </summary>
    [JsonStringEnumMemberName("repository")] Repository,

    /// <summary>A fact that reached the canonical store from a vendor's memory root.</summary>
    [JsonStringEnumMemberName("vendor")] Vendor,

    /// <summary>
    /// A fact from the reserved fleet store (#2112) — an operator or machine fact that belongs to no
    /// repository and is merged into every repository's projection ahead of that repository's own
    /// entries. Never in conflict with either of the two above: its subject is <c>fleet</c>, so no
    /// conflict key and no id can collide across the two stores. The merge rule is stated once in
    /// spec/baton.md §12.
    /// </summary>
    [JsonStringEnumMemberName("fleet")] Fleet,
}

/// <summary>One entry offered to <see cref="MemoryProjection"/>, with where its authority comes from.</summary>
/// <param name="Entry">The canonical entry, supersession already resolved (<c>MemoryStore.ReadResolvedAsync</c>).</param>
/// <param name="Origin">See <see cref="MemoryFactOrigin"/>.</param>
public sealed record MemoryProjectionCandidate(MemoryEntry Entry, MemoryFactOrigin Origin);

/// <summary>
/// One Baton-owned detail document emitted beside the vendor's memory index.
/// </summary>
/// <param name="EntryId">The resolved entry represented by the detail.</param>
/// <param name="RelativePath">The file name the index links to, relative to the vendor root.</param>
/// <param name="Title">The stable, one-line title shown in the index.</param>
/// <param name="Description">The stable, one-line description shown in the index.</param>
/// <param name="Bytes">The complete UTF-8 detail document.</param>
public sealed record MemoryProjectionDetail(
    string EntryId,
    string RelativePath,
    string Title,
    string Description,
    byte[] Bytes);

/// <summary>
/// One entry that is accounted for in the report and <b>not</b> in the projected bytes, and why.
/// </summary>
/// <remarks>
/// Three producers, and the type is shared across them deliberately: superseded, overridden by
/// repository truth, and dropped by <see cref="ProjectionBudget"/> are three reasons a memory is
/// missing from a cache, and an operator reading the report needs the same three facts about each —
/// which entry, which file, and why. The repository is the enclosing report's and is not repeated on
/// every row. A count with no names attached is what "never dropped silently" forbids, whichever of
/// the three did the dropping.
/// </remarks>
/// <param name="EntryId">The canonical <see cref="MemoryEntry.Id"/>, so the operator can find it in the store.</param>
/// <param name="SourceFileName">The file it was imported from, for a reader who knows the memory by its name.</param>
/// <param name="SourcePath">That file's full path.</param>
/// <param name="Reason">Why it is not in the body, in the operator's terms.</param>
public sealed record ProjectionOmission(
    [property: JsonPropertyName("entryId")]
    string EntryId,
    [property: JsonPropertyName("sourceFileName")]
    string SourceFileName,
    [property: JsonPropertyName("sourcePath")]
    string SourcePath,
    [property: JsonPropertyName("reason")]
    string Reason);

/// <summary>
/// One repository's projected cache: the bytes to write, and the full account of what did not reach
/// them.
/// </summary>
/// <param name="Repository">The subject these entries are filed under.</param>
/// <param name="CanonicalStorePath">The <c>entries.jsonl</c> the bytes were projected from.</param>
/// <param name="Bytes">
/// Exactly what a target file receives — UTF-8, no BOM, <c>\n</c> line endings. See
/// <see cref="MemoryProjection"/> for why those three are properties rather than incidental.
/// </param>
/// <param name="BodySha256">The content hash the header carries. Over the body only; see the projector's remarks.</param>
/// <param name="ProjectedEntryIds">The entries that ARE in the body, in the order they appear.</param>
/// <param name="Superseded">Entries a supersession link retired.</param>
/// <param name="Overridden">Vendor entries a checked-in repository fact outranked.</param>
/// <param name="Dropped">Entries the budget could not fit.</param>
public sealed record MemoryProjectionResult(
    string Repository,
    string CanonicalStorePath,
    byte[] Bytes,
    string BodySha256,
    IReadOnlyList<string> ProjectedEntryIds,
    IReadOnlyList<ProjectionOmission> Superseded,
    IReadOnlyList<ProjectionOmission> Overridden,
    IReadOnlyList<ProjectionOmission> Dropped)
{
    /// <summary>The complete Baton-owned section to splice into a vendor index.</summary>
    public byte[] IndexBytes { get; init; } = [];

    /// <summary>Every detail file linked by <see cref="IndexBytes"/>, in index order.</summary>
    public IReadOnlyList<MemoryProjectionDetail> DetailFiles { get; init; } = [];
}

/// <summary>
/// Renders one repository's canonical memory into the bytes a vendor's memory root receives (#1852
/// phase C) — <b>a pure function of its arguments</b>, which is the whole of how the idempotence
/// acceptance line is met.
/// </summary>
/// <remarks>
/// <para>
/// <b>Byte-identical regeneration is structural here, not a property to be tested for and hoped at.</b>
/// Four things make it hold, and each removes a specific way it would otherwise fail:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>No clock reaches the output.</b> Not the import time, not the source mtime, not a generated-at
/// stamp. A stamp is the ordinary way a generated file is labelled and it is exactly what makes
/// "re-running sync produces no diff" impossible to claim — so the header carries a
/// <b>content hash</b> in its place, which says the same thing (this file is derived, here is what it
/// is derived from) without changing when nothing else did.
/// </description></item>
/// <item><description>
/// <b>A total order, applied before anything else.</b> Fleet-origin candidates sort ahead of every
/// repository one (#2112 — this is the merge rule's mechanism, not a preference), then
/// <c>(repository, kind, id)</c>, all three ordinal within each tier: the first two group the file the
/// way a reader reads it, and the id is what makes the order total — without it two entries of one
/// kind sort equal and their relative order is whatever the caller's enumeration happened to be.
/// </description></item>
/// <item><description>
/// <b><c>\n</c>, UTF-8, no BOM — pinned, not inherited.</b> <c>Environment.NewLine</c>,
/// <c>File.WriteAllText</c>'s default encoding and <c>StringWriter</c>'s default newline would each
/// make these bytes platform-dependent; the same three are pinned for the same reason in
/// <c>MemoryStore</c>'s own rewrite path. Entry text is normalised to <c>\n</c> on the way in, so a
/// CRLF source file and an LF one holding the same characters project identically.
/// </description></item>
/// <item><description>
/// <b>Every number goes through <see cref="CultureInfo.InvariantCulture"/></b>, so the header's counts
/// do not change with the machine's formatting.
/// </description></item>
/// </list>
/// <para>
/// <b>The hash covers the body and deliberately not the header.</b> Two reasons. The header reports
/// counts that are themselves derived from the body, so hashing it too would be self-referential; and
/// the header names the canonical store path, which is per-repository — keeping the <i>target</i> path
/// out of the projector's inputs entirely is what lets one repository's two vendor roots receive
/// byte-identical files rather than two files differing only in where they sit.
/// </para>
/// <para>
/// <b>A superseded entry is OMITTED from the body, and named in the report.</b> The rule is stated
/// here and glossed in one clause in spec/baton.md §12, per the record-once gate — a projection is the
/// <i>current</i> reading of a repository's memory, and an archived fact that a live one has replaced
/// is precisely what a reader must not act on. It is not dropped silently: it appears in
/// <see cref="MemoryProjectionResult.Superseded"/> with its canonical id, so an operator who wants the
/// history reads the store, which still holds every row (nothing in this namespace deletes one).
/// </para>
/// <para>
/// <b>An archived-origin entry (<see cref="MemoryKind.HistoricalNote"/>) IS projected, and is labelled
/// rather than hidden.</b> The filter above is on supersession, not on kind, and deliberately: an
/// archived fact whose live counterpart was renamed or deleted has no link, and dropping it would be
/// the silent omission this whole surface refuses. It cannot be mistaken for a current one — the
/// ordinal kind sort groups every <c>historical-note</c> contiguously, and each section names the kind
/// in both the machine comment and the prose line. The ruling is glossed in one clause in
/// spec/baton.md §12.
/// </para>
/// <para>
/// <b>Conflict resolution is precedence over a digest, never a merge and never a read.</b> A
/// repository-origin candidate and a vendor-origin one collide when they share a subject and a source
/// <b>filename</b> — the same key phase B already uses for supersession, reused rather than
/// re-derived, because a second definition of "the same fact" is a second thing to drift. The
/// repository side is projected, the vendor side goes to
/// <see cref="MemoryProjectionResult.Overridden"/> with the reason naming which of the two it was, and
/// nothing is combined. Comparing anything beyond the digest would mean reading what the memories say,
/// which Architecture Rule 1 and spec/baton.md §12's "never inferred from the body" both forbid; an identical digest
/// is reported as a duplicate rather than passed over, because a vendor copy that vanished from the
/// cache without a word reads to an operator exactly like one that was never there.
/// </para>
/// </remarks>
public static class MemoryProjection
{
    /// <summary>Format marker on the first line of every projected file, so a reader (or a later Baton) can tell what it is holding.</summary>
    public const string FormatMarker = "<!-- baton:projection v1 -->";

    /// <summary>The exact opening marker for the section Baton owns in a vendor index.</summary>
    public const string IndexStartMarker = "<!-- baton:memory-index:start -->";

    /// <summary>The exact closing marker for the section Baton owns in a vendor index.</summary>
    public const string IndexEndMarker = "<!-- baton:memory-index:end -->";

    /// <summary>The first-line marker on every per-entry detail document.</summary>
    public const string DetailFormatMarker = "<!-- baton:memory-detail v1 -->";

    /// <summary>The reserved prefix for Baton-owned per-entry detail file names.</summary>
    public const string DetailFilePrefix = "baton-memory-detail-";

    /// <summary>The suffix for Baton-owned per-entry detail file names.</summary>
    public const string DetailFileSuffix = ".md";

    /// <summary>
    /// Whether <paramref name="text"/> is a file this projector wrote — the first line is
    /// <see cref="FormatMarker"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The writer and the reader share one constant, which is the whole point of this method.</b>
    /// <c>baton memory sync --apply</c> writes a projection into a vendor memory root, and that root is
    /// <c>baton memory import</c>'s population — so without a test here the two verbs form a loop that
    /// re-ingests each cycle's cache as a fresh memory and doubles the store's bytes per cycle.
    /// <c>MemoryImportPlan.Build</c> is the caller; the loop and what it costs are stated once in
    /// spec/baton.md §12.
    /// </para>
    /// <para>
    /// <b>The marker, not the filename.</b> A name test would have to know two spellings —
    /// <see cref="ClaudeProjectionTarget.ProjectionFileName"/> and the
    /// <c>baton-projection.md.&lt;guid&gt;.tmp</c> a crashed write leaves behind — and would miss a
    /// projection an operator copied under another name;
    /// <c>MemoryImportTests.A_projection_under_an_ordinary_filename_is_skipped_on_its_marker</c> is the
    /// arm that makes that a measurement rather than an intention. What this predicate does NOT promise
    /// about a partially written file is stated once in spec/baton.md §12, with the loop above.
    /// </para>
    /// </remarks>
    public static bool IsProjectedFile(string? text) =>
        text is not null &&
        (text.StartsWith(FormatMarker, StringComparison.Ordinal)
         || text.StartsWith(DetailFormatMarker, StringComparison.Ordinal)
         || text.Contains(IndexStartMarker, StringComparison.Ordinal)
         || text.Contains(IndexEndMarker, StringComparison.Ordinal));

    /// <summary>
    /// Whether <paramref name="text"/> is the legacy full-file cache written before the vendor index
    /// migration. It remains separate from <see cref="IsProjectedFile"/> so import compatibility can
    /// account for a migrated projection set without reporting every member as a new cache.
    /// </summary>
    public static bool IsLegacyProjectedFile(string? text) =>
        text is not null && text.StartsWith(FormatMarker, StringComparison.Ordinal);

    /// <summary>
    /// Returns the deterministic, collision-resistant detail file name for an entry id.
    /// </summary>
    public static string DetailFileNameFor(string entryId)
    {
        ArgumentException.ThrowIfNullOrEmpty(entryId);

        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entryId))).ToLowerInvariant();
        return DetailFilePrefix + digest + DetailFileSuffix;
    }

    /// <summary>
    /// Whether <paramref name="fileName"/> is one of the explicitly Baton-owned detail names.
    /// </summary>
    public static bool IsOwnedDetailFileName(string fileName)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);

        if (!fileName.StartsWith(DetailFilePrefix, StringComparison.OrdinalIgnoreCase)
            || !fileName.EndsWith(DetailFileSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (fileName.Length < DetailFilePrefix.Length + DetailFileSuffix.Length)
        {
            return false;
        }

        var digest = fileName[DetailFilePrefix.Length..^DetailFileSuffix.Length];
        return digest.Length == 64 && digest.All(Uri.IsHexDigit);
    }

    /// <summary>
    /// Replaces exactly one valid Baton-owned index section, preserving every byte outside its two
    /// marker lines. A missing index file is represented by <see langword="null"/> and receives a new
    /// section; an existing file without a valid pair fails closed.
    /// </summary>
    public static byte[] MergeIndex(byte[]? existingBytes, byte[] ownedSectionBytes)
    {
        ArgumentNullException.ThrowIfNull(ownedSectionBytes);
        ValidateOwnedSection(ownedSectionBytes);

        if (existingBytes is null)
        {
            return ownedSectionBytes.ToArray();
        }

        var markers = FindIndexMarkers(existingBytes);
        if (markers.Start is null || markers.End is null)
        {
            throw new InvalidDataException(
                "The vendor memory index has missing Baton ownership markers; refusing to rewrite it.");
        }

        if (markers.Start.Value.Start >= markers.End.Value.End)
        {
            throw new InvalidDataException(
                "The vendor memory index has reordered Baton ownership markers; refusing to rewrite it.");
        }

        var merged = new byte[
            markers.Start.Value.Start
            + ownedSectionBytes.Length
            + existingBytes.Length - markers.End.Value.End];
        Buffer.BlockCopy(existingBytes, 0, merged, 0, markers.Start.Value.Start);
        Buffer.BlockCopy(ownedSectionBytes, 0, merged, markers.Start.Value.Start, ownedSectionBytes.Length);
        Buffer.BlockCopy(
            existingBytes,
            markers.End.Value.End,
            merged,
            markers.Start.Value.Start + ownedSectionBytes.Length,
            existingBytes.Length - markers.End.Value.End);
        return merged;
    }

    /// <summary>
    /// <paramref name="candidates"/> rendered for <paramref name="repository"/>, bounded by
    /// <paramref name="budget"/>, with everything left out accounted for.
    /// </summary>
    /// <param name="repository">
    /// The subject named in the header. <b>Not a filter</b> — every candidate handed in is projected,
    /// whatever its own <see cref="MemoryEntry.Repository"/> says, and the caller is what keeps the two
    /// agreeing (<c>MemorySyncCommand</c> reads one repository's store and filters the checked-in facts
    /// to it). Filtering here would drop a candidate with no named row, which is the one thing this
    /// surface refuses; see <see cref="ProjectionOmission"/>.
    /// </param>
    /// <param name="canonicalStorePath">
    /// The <c>entries.jsonl</c> these came from, named in the header so the cache points at its truth.
    /// </param>
    /// <param name="candidates">
    /// The entries, supersession already resolved. Duplicates by id are collapsed to the first
    /// occurrence, so a caller that concatenated two reads of one store does not double the body.
    /// </param>
    /// <param name="budget">See <see cref="ProjectionBudget"/>.</param>
    /// <param name="fleetStorePath">
    /// The fleet store's <c>entries.jsonl</c> when the caller merged it in (#2112), named in the header
    /// beside <paramref name="canonicalStorePath"/> so a reader of a fleet section knows which file to
    /// change. <see langword="null"/> when no fleet store was read, and then the header says nothing
    /// about one — which keeps a repository with no fleet store on this machine projecting the bytes it
    /// projected before the slug existed.
    /// </param>
    public static MemoryProjectionResult Build(
        string repository,
        string canonicalStorePath,
        IReadOnlyList<MemoryProjectionCandidate> candidates,
        ProjectionBudget budget,
        string? fleetStorePath = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(repository);
        ArgumentException.ThrowIfNullOrEmpty(canonicalStorePath);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(budget);

        // Fleet first, ahead of the (repository, kind, id) order, and this is the merge rule's
        // mechanism rather than a preference: the budget truncates a suffix, so putting the
        // machine-wide facts at the front is what makes a repository entry the one that drops before
        // a fleet one does. Nothing here shadows anything -- the subjects are disjoint, so both stores'
        // entries reach the body whatever their text or filename.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = candidates
            .Where(c => seen.Add(c.Entry.Id))
            .OrderBy(c => c.Origin == MemoryFactOrigin.Fleet ? 0 : 1)
            .ThenBy(c => c.Entry.Repository, StringComparer.Ordinal)
            .ThenBy(c => MemoryJsonNames.Of(c.Entry.Kind), StringComparer.Ordinal)
            .ThenBy(c => c.Entry.Id, StringComparer.Ordinal)
            .ToList();

        var superseded = new List<ProjectionOmission>();
        var live = new List<MemoryProjectionCandidate>();
        foreach (var candidate in ordered)
        {
            if (candidate.Entry.SupersededBy is { Count: > 0 } by)
            {
                superseded.Add(Omission(
                    candidate.Entry,
                    "superseded by " + string.Join(", ", by) +
                    " -- a projection carries the current reading, and the store still holds this row."));
            }
            else
            {
                live.Add(candidate);
            }
        }

        var overridden = new List<ProjectionOmission>();
        var selected = SelectRepositoryTruth(live, overridden);

        var sections = new List<(MemoryProjectionCandidate Candidate, string Text)>();
        var dropped = new List<ProjectionOmission>();
        var bodyBytes = 0;
        var stopped = false;
        foreach (var candidate in selected)
        {
            if (stopped)
            {
                dropped.Add(Omission(candidate.Entry, BudgetReason(budget)));
                continue;
            }

            var section = RenderSection(candidate);
            var size = Encoding.UTF8.GetByteCount(section);
            if (sections.Count + 1 > budget.MaxEntries || bodyBytes + size > budget.MaxBodyBytes)
            {
                stopped = true;
                dropped.Add(Omission(candidate.Entry, BudgetReason(budget)));
                continue;
            }

            bodyBytes += size;
            sections.Add((candidate, section));
        }

        var body = string.Concat(sections.Select(s => s.Text));
        var bodySha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

        var header = RenderHeader(
            repository, canonicalStorePath, fleetStorePath, bodySha256, budget,
            sections.Count, superseded.Count, overridden.Count, dropped.Count);

        var details = sections
            .Select(section =>
            {
                var title = IndexTitle(section.Candidate.Entry);
                var description = IndexDescription(section.Candidate.Entry);
                return new MemoryProjectionDetail(
                    section.Candidate.Entry.Id,
                    DetailFileNameFor(section.Candidate.Entry.Id),
                    title,
                    description,
                    Encoding.UTF8.GetBytes(RenderDetail(section.Candidate, section.Text)));
            })
            .ToList();

        return new MemoryProjectionResult(
            repository,
            canonicalStorePath,
            Encoding.UTF8.GetBytes(header + body),
            bodySha256,
            sections.Select(s => s.Candidate.Entry.Id).ToList(),
            superseded,
            overridden,
            dropped)
        {
            IndexBytes = RenderIndex(details),
            DetailFiles = details,
        };
    }

    /// <summary>
    /// <paramref name="live"/> with every vendor candidate that collides with a repository one removed
    /// and recorded in <paramref name="overridden"/>. Order is preserved, so the caller's total order
    /// survives.
    /// </summary>
    /// <remarks>
    /// The key is subject plus source filename, case-folded — the same collision test
    /// <c>MemoryImportPlan.LinkSupersession</c> applies, minus its differing-digest requirement, which
    /// belongs to supersession rather than to precedence: a vendor copy identical to the repository's
    /// is still a copy the projection must not emit twice, and saying so is cheaper than leaving the
    /// operator to notice it vanished.
    /// <para>
    /// <b>The key is case-INSENSITIVE, and two repository facts that collide under it are not an
    /// error.</b> The lookup groups rather than throwing: both repository facts are projected (a
    /// repository-origin candidate is never overridden), and the one a colliding vendor copy is reported
    /// as outranked by is the first in the caller's already-total order — fleet tier first (#2112), then
    /// <c>(repository, kind, id)</c> within it.
    /// So <c>Rules.md</c> and <c>rules.md</c> in one facts directory on a case-sensitive filesystem give
    /// a deterministic answer where <c>ToDictionary</c> threw <see cref="ArgumentException"/> out of a
    /// public pure function.
    /// </para>
    /// </remarks>
    private static List<MemoryProjectionCandidate> SelectRepositoryTruth(
        List<MemoryProjectionCandidate> live, List<ProjectionOmission> overridden)
    {
        var repositoryKeys = live
            .Where(c => c.Origin == MemoryFactOrigin.Repository)
            .GroupBy(ConflictKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Entry, StringComparer.OrdinalIgnoreCase);

        if (repositoryKeys.Count == 0)
        {
            return live;
        }

        var selected = new List<MemoryProjectionCandidate>(live.Count);
        foreach (var candidate in live)
        {
            if (candidate.Origin == MemoryFactOrigin.Repository
                || !repositoryKeys.TryGetValue(ConflictKey(candidate), out var winner))
            {
                selected.Add(candidate);
                continue;
            }

            var identical = string.Equals(winner.Sha256, candidate.Entry.Sha256, StringComparison.OrdinalIgnoreCase);
            overridden.Add(Omission(
                candidate.Entry,
                $"a checked-in repository fact of the same name ('{winner.SourcePath}', entry " +
                $"{winner.Id}) outranks it, so repository truth was projected and this vendor copy was " +
                $"not. The two are {(identical ? "byte-identical" : "DIFFERENT")}; nothing was merged, " +
                "and this row is untouched in the canonical store."));
        }

        return selected;
    }

    /// <summary>Subject plus source filename, the pair a conflict is defined on.</summary>
    private static string ConflictKey(MemoryProjectionCandidate candidate) =>
        candidate.Entry.Repository.ToLowerInvariant() + "\n" +
        Path.GetFileName(candidate.Entry.SourcePath).ToLowerInvariant();

    private static byte[] RenderIndex(IReadOnlyList<MemoryProjectionDetail> details)
    {
        var builder = new StringBuilder();
        builder.Append(IndexStartMarker).Append('\n');
        foreach (var detail in details)
        {
            builder.Append("- [")
                .Append(EscapeIndexText(detail.Title))
                .Append("](")
                .Append(detail.RelativePath)
                .Append(") — ")
                .Append(EscapeIndexText(detail.Description))
                .Append(" <!-- baton:entry id=")
                .Append(detail.EntryId)
                .Append(" -->\n");
        }

        builder.Append(IndexEndMarker).Append('\n');
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static string RenderDetail(MemoryProjectionCandidate candidate, string section) =>
        DetailFormatMarker + "\n" + section;

    private static string IndexTitle(MemoryEntry entry)
    {
        var heading = FirstMarkdownHeading(entry.Text);
        if (heading is { Length: > 0 })
        {
            return heading;
        }

        if (AuthoredMemory.IsAuthored(entry))
        {
            return "authored memory " + entry.Id;
        }

        var fileName = Path.GetFileName(entry.SourcePath);
        return fileName.Length > 0 ? fileName : entry.Id;
    }

    private static string IndexDescription(MemoryEntry entry)
    {
        var lines = Normalize(entry.Text).Split('\n');
        var sawHeading = false;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed == "---")
            {
                continue;
            }

            if (IsMarkdownHeading(trimmed))
            {
                sawHeading = true;
                continue;
            }

            if (sawHeading || trimmed.Length > 0)
            {
                return OneLine(trimmed);
            }
        }

        return "No description provided.";
    }

    private static string? FirstMarkdownHeading(string text)
    {
        foreach (var line in Normalize(text).Split('\n'))
        {
            var trimmed = line.Trim();
            if (IsMarkdownHeading(trimmed))
            {
                var heading = trimmed.TrimStart('#').Trim();
                while (heading.EndsWith('#'))
                {
                    heading = heading[..^1].TrimEnd();
                }

                return OneLine(heading);
            }
        }

        return null;
    }

    private static bool IsMarkdownHeading(string line)
    {
        var hashes = 0;
        while (hashes < line.Length && line[hashes] == '#')
        {
            hashes++;
        }

        return hashes is >= 1 and <= 6 && hashes < line.Length && line[hashes] == ' ';
    }

    private static string OneLine(string value)
    {
        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString().Trim();
    }

    private static string EscapeIndexText(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal);

    private static void ValidateOwnedSection(byte[] bytes)
    {
        var markers = FindIndexMarkers(bytes);
        if (markers.Start is null || markers.End is null
            || markers.Start.Value.Start >= markers.End.Value.End)
        {
            throw new InvalidDataException("The generated Baton memory index section has invalid ownership markers.");
        }
    }

    private static IndexMarkers FindIndexMarkers(byte[] bytes)
    {
        var start = (IndexMarkerLine?)null;
        var end = (IndexMarkerLine?)null;
        var startCount = 0;
        var endCount = 0;
        var malformed = false;
        var lineStart = 0;
        while (lineStart < bytes.Length)
        {
            var lineFeed = Array.IndexOf(bytes, (byte)'\n', lineStart);
            var lineEnd = lineFeed < 0 ? bytes.Length : lineFeed + 1;
            var contentEnd = lineFeed < 0 ? bytes.Length : lineFeed;
            if (contentEnd > lineStart && bytes[contentEnd - 1] == '\r')
            {
                contentEnd--;
            }

            var isStart = BytesEqual(bytes, lineStart, contentEnd, IndexStartMarker);
            var isEnd = BytesEqual(bytes, lineStart, contentEnd, IndexEndMarker);
            if (isStart)
            {
                startCount++;
                start ??= new IndexMarkerLine(lineStart, lineEnd);
            }
            else if (isEnd)
            {
                endCount++;
                end ??= new IndexMarkerLine(lineStart, lineEnd);
            }
            else if (ContainsMarkerFamily(bytes, lineStart, contentEnd))
            {
                malformed = true;
            }

            lineStart = lineEnd;
        }

        if (startCount != 1 || endCount != 1 || malformed)
        {
            throw new InvalidDataException(
                "The vendor memory index has duplicated, nested, reordered, or malformed Baton ownership markers; " +
                "refusing to rewrite it.");
        }

        return new IndexMarkers(start, end);
    }

    private static bool BytesEqual(byte[] bytes, int start, int end, string value)
    {
        if (end - start != value.Length)
        {
            return false;
        }

        for (var index = 0; index < value.Length; index++)
        {
            if (bytes[start + index] != value[index])
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainsMarkerFamily(byte[] bytes, int start, int end)
    {
        const string markerFamily = "baton:memory-index";
        if (end - start < markerFamily.Length)
        {
            return false;
        }

        for (var index = start; index <= end - markerFamily.Length; index++)
        {
            if (BytesEqual(bytes, index, index + markerFamily.Length, markerFamily))
            {
                return true;
            }
        }

        return false;
    }

    private sealed record IndexMarkers(IndexMarkerLine? Start, IndexMarkerLine? End);

    private sealed record IndexMarkerLine(int Start, int End);

    private static ProjectionOmission Omission(MemoryEntry entry, string reason) =>
        new(entry.Id, Path.GetFileName(entry.SourcePath), entry.SourcePath, reason);

    private static string BudgetReason(ProjectionBudget budget) =>
        $"beyond the projection budget ({budget.Describe()}). Truncation stops at the first entry that " +
        "does not fit and drops the rest of the order, so this entry and everything after it in the " +
        "fleet-first, then (repository, kind, id) order are absent from the cache and present in the " +
        "canonical store.";

    /// <summary>
    /// One entry's section. The HTML comment is the machine-readable back-pointer; the line beside it
    /// is what a person reads. Both name the id, because a cache whose provenance is only in a comment
    /// is one Markdown renderer away from having none.
    /// </summary>
    /// <remarks>
    /// <b>The provenance line differs by origin, because the two ids point at different things.</b> A
    /// vendor-origin id is a row in the canonical <c>entries.jsonl</c> named in the header, and saying
    /// so is the acceptance line about linking to canonical provenance. A repository-origin id is
    /// derived the same way (<see cref="MemoryEntry.Derive"/>, so it is stable across runs) but is
    /// <b>not in the store</b> — repository facts are read from a checkout at projection time and never
    /// imported. Printing "canonical entry" over one would send an operator into the store after an id
    /// that is not there, which is a back-pointer that resolves to nothing dressed as one that
    /// resolves.
    /// <para>
    /// <b>An authored entry (<c>baton memory add</c>, #2071) names its asserter instead of a path.</b>
    /// Its <see cref="MemoryEntry.SourcePath"/> is a content-addressed key rather than a location and
    /// no such file exists (<see cref="AuthoredMemory"/>), so printing it here would send a reader to
    /// open a file that was never written — the same resolves-to-nothing back-pointer the paragraph
    /// above refuses for a repository-origin id. The id is still the store row, so the sentence still
    /// reads "canonical entry".
    /// </para>
    /// </remarks>
    private static string RenderSection(MemoryProjectionCandidate candidate)
    {
        var entry = candidate.Entry;
        var authored = AuthoredMemory.IsAuthored(entry);
        var fileName = authored ? $"authored:{entry.Id}" : Path.GetFileName(entry.SourcePath);
        var builder = new StringBuilder();

        builder.Append("---\n\n");
        builder.Append(CultureInfo.InvariantCulture, $"## {fileName}\n\n");
        builder.Append(
            CultureInfo.InvariantCulture,
            $"<!-- baton:entry id={entry.Id} kind={MemoryJsonNames.Of(entry.Kind)} " +
            $"kind-source={MemoryJsonNames.Of(entry.KindSource)} origin={MemoryJsonNames.Of(candidate.Origin)} " +
            $"vendor={entry.SourceVendor} -->\n");
        var provenance = candidate.Origin == MemoryFactOrigin.Repository
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"Checked-in repository fact `{entry.Id}` ({MemoryJsonNames.Of(entry.Kind)}), read from " +
                $"`{entry.SourcePath}` in the checkout. **Not a canonical store row** -- this id is derived " +
                $"for reference and will not be found in the store file named above.\n\n")
            : candidate.Origin == MemoryFactOrigin.Fleet
            ? "Fleet entry `" + entry.Id + "` (" + MemoryJsonNames.Of(entry.Kind) + "), " +
              (authored
                  ? "authored through `baton memory add --repository " + FleetMemory.Slug + "` by `" + entry.AssertedBy + "`"
                  : "projected from `" + entry.SourcePath + "`") +
              ". **An operator or machine fact, not this repository's** -- it is a row in the fleet " +
              "store named in the header, not in this repository's store.\n\n"
            : authored
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"Canonical entry `{entry.Id}` ({MemoryJsonNames.Of(entry.Kind)}), authored through " +
                $"`baton memory add` by `{entry.AssertedBy}`. **No source file** -- it was written into the " +
                $"canonical store directly.\n\n")
            : string.Create(
                CultureInfo.InvariantCulture,
                $"Canonical entry `{entry.Id}` ({MemoryJsonNames.Of(entry.Kind)}), projected from `{entry.SourcePath}`.\n\n");
        builder.Append(provenance);
        builder.Append(Normalize(entry.Text).TrimEnd('\n'));
        builder.Append("\n\n");

        return builder.ToString();
    }

    /// <summary>
    /// The header every projected file opens with. It says four things, and each is there because a
    /// reader who was not told it would assume the opposite: this file is a cache, edits to it are
    /// lost, the truth lives at a named path, and there is no timestamp <i>on purpose</i>.
    /// </summary>
    private static string RenderHeader(
        string repository,
        string canonicalStorePath,
        string? fleetStorePath,
        string bodySha256,
        ProjectionBudget budget,
        int projected,
        int superseded,
        int overridden,
        int dropped)
    {
        var builder = new StringBuilder();

        builder.Append(FormatMarker).Append('\n');
        builder.Append(CultureInfo.InvariantCulture, $"# Baton memory projection -- {repository}\n\n");
        builder.Append(
            "**This file is a CACHE, not the truth.** It is generated in full by `baton memory sync` and\n" +
            "overwritten in full on every run: an edit made here is lost on the next sync and is never read\n" +
            "back into Baton. To change what it says, change the canonical store it is projected from:\n\n");
        builder.Append(CultureInfo.InvariantCulture, $"    {canonicalStorePath}\n\n");
        if (fleetStorePath is { Length: > 0 })
        {
            builder.Append(
                "Sections marked `origin=fleet` come first and are operator or machine facts filed under the\n" +
                "reserved `fleet` slug rather than under this repository. They are rows in the fleet store, which\n" +
                "every repository's projection merges in ahead of its own entries:\n\n");
            builder.Append(CultureInfo.InvariantCulture, $"    {fleetStorePath}\n\n");
        }

        builder.Append(
            "Sections marked `origin=repository` are the exception: those are checked-in facts read straight\n" +
            "from a checkout at projection time, they are **not** rows in the store above, and each says so\n" +
            "on its own line. Change one by editing the checked-in file.\n\n");
        builder.Append(
            "Every other section back-points to the canonical entry id it came from. **There is deliberately\n" +
            "no timestamp in this file** -- an unchanged store projects byte-identical bytes, so any diff here\n" +
            "means the store changed, and the content hash below is what a generated-at stamp would otherwise\n" +
            "have been.\n\n");
        builder.Append(
            CultureInfo.InvariantCulture,
            $"- body-sha256: `{bodySha256}`\n" +
            $"- entries projected: {projected}\n" +
            $"- omitted as superseded: {superseded}\n" +
            $"- overridden by checked-in repository truth: {overridden}\n" +
            $"- dropped by the projection budget ({budget.Describe()}): {dropped}\n\n");
        builder.Append(
            "Run `baton memory sync` with no `--apply` to see every omitted entry named, with its canonical id.\n\n");

        return builder.ToString();
    }

    /// <summary>CRLF and lone CR to <c>\n</c>, so the projected bytes do not carry a source file's line endings.</summary>
    private static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');
}
