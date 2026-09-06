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
}

/// <summary>One entry offered to <see cref="MemoryProjection"/>, with where its authority comes from.</summary>
/// <param name="Entry">The canonical entry, supersession already resolved (<c>MemoryStore.ReadResolvedAsync</c>).</param>
/// <param name="Origin">See <see cref="MemoryFactOrigin"/>.</param>
public sealed record MemoryProjectionCandidate(MemoryEntry Entry, MemoryFactOrigin Origin);

/// <summary>
/// One entry that is accounted for in the report and <b>not</b> in the projected bytes, and why.
/// </summary>
/// <remarks>
/// Three producers, and the type is shared across them deliberately: superseded, overridden by
/// repository truth, and dropped by <see cref="ProjectionBudget"/> are three reasons a memory is
/// missing from a cache, and an operator reading the report needs the same four facts about each —
/// which entry, which file, which repository, and why. A count with no names attached is what "never
/// dropped silently" forbids, whichever of the three did the dropping.
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
    IReadOnlyList<ProjectionOmission> Dropped);

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
/// <b>A total order, applied before anything else.</b> <c>(repository, kind, id)</c>, all three
/// ordinal: the first two group the file the way a reader reads it, and the id is what makes the order
/// total — without it two entries of one kind sort equal and their relative order is whatever the
/// caller's enumeration happened to be.
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
/// <b>Conflict resolution is precedence over a digest, never a merge and never a read.</b> A
/// repository-origin candidate and a vendor-origin one collide when they share a subject and a source
/// <b>filename</b> — the same key phase B already uses for supersession, reused rather than
/// re-derived, because a second definition of "the same fact" is a second thing to drift. The
/// repository side is projected, the vendor side goes to
/// <see cref="MemoryProjectionResult.Overridden"/> with the reason naming which of the two it was, and
/// nothing is combined. Comparing anything beyond the digest would mean reading what the memories say,
/// which Architecture Rule 1 and §12's "never inferred from the body" both forbid; an identical digest
/// is reported as a duplicate rather than passed over, because a vendor copy that vanished from the
/// cache without a word reads to an operator exactly like one that was never there.
/// </para>
/// </remarks>
public static class MemoryProjection
{
    /// <summary>Format marker on the first line of every projected file, so a reader (or a later Baton) can tell what it is holding.</summary>
    public const string FormatMarker = "<!-- baton:projection v1 -->";

    /// <summary>
    /// <paramref name="candidates"/> rendered for <paramref name="repository"/>, bounded by
    /// <paramref name="budget"/>, with everything left out accounted for.
    /// </summary>
    /// <param name="repository">The subject. Candidates filed under any other are not this projection's.</param>
    /// <param name="canonicalStorePath">
    /// The <c>entries.jsonl</c> these came from, named in the header so the cache points at its truth.
    /// </param>
    /// <param name="candidates">
    /// The entries, supersession already resolved. Duplicates by id are collapsed to the first
    /// occurrence, so a caller that concatenated two reads of one store does not double the body.
    /// </param>
    /// <param name="budget">See <see cref="ProjectionBudget"/>.</param>
    public static MemoryProjectionResult Build(
        string repository,
        string canonicalStorePath,
        IReadOnlyList<MemoryProjectionCandidate> candidates,
        ProjectionBudget budget)
    {
        ArgumentException.ThrowIfNullOrEmpty(repository);
        ArgumentException.ThrowIfNullOrEmpty(canonicalStorePath);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(budget);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = candidates
            .Where(c => seen.Add(c.Entry.Id))
            .OrderBy(c => c.Entry.Repository, StringComparer.Ordinal)
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

        var sections = new List<(string EntryId, string Text)>();
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
            sections.Add((candidate.Entry.Id, section));
        }

        var body = string.Concat(sections.Select(s => s.Text));
        var bodySha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

        var header = RenderHeader(
            repository, canonicalStorePath, bodySha256, budget,
            sections.Count, superseded.Count, overridden.Count, dropped.Count);

        return new MemoryProjectionResult(
            repository,
            canonicalStorePath,
            Encoding.UTF8.GetBytes(header + body),
            bodySha256,
            sections.Select(s => s.EntryId).ToList(),
            superseded,
            overridden,
            dropped);
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
    /// </remarks>
    private static List<MemoryProjectionCandidate> SelectRepositoryTruth(
        List<MemoryProjectionCandidate> live, List<ProjectionOmission> overridden)
    {
        var repositoryKeys = live
            .Where(c => c.Origin == MemoryFactOrigin.Repository)
            .ToDictionary(ConflictKey, c => c.Entry, StringComparer.OrdinalIgnoreCase);

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

    private static ProjectionOmission Omission(MemoryEntry entry, string reason) =>
        new(entry.Id, Path.GetFileName(entry.SourcePath), entry.SourcePath, reason);

    private static string BudgetReason(ProjectionBudget budget) =>
        $"beyond the projection budget ({budget.Describe()}). Truncation stops at the first entry that " +
        "does not fit and drops the rest of the order, so this entry and everything after it in " +
        "(repository, kind, id) are absent from the cache and present in the canonical store.";

    /// <summary>
    /// One entry's section. The HTML comment is the machine-readable back-pointer to the canonical
    /// entry; the heading beside it is what a person reads. Both name the id, because a cache whose
    /// provenance is only in a comment is one Markdown renderer away from having none.
    /// </summary>
    private static string RenderSection(MemoryProjectionCandidate candidate)
    {
        var entry = candidate.Entry;
        var fileName = Path.GetFileName(entry.SourcePath);
        var builder = new StringBuilder();

        builder.Append("---\n\n");
        builder.Append(CultureInfo.InvariantCulture, $"## {fileName}\n\n");
        builder.Append(
            CultureInfo.InvariantCulture,
            $"<!-- baton:entry id={entry.Id} kind={MemoryJsonNames.Of(entry.Kind)} " +
            $"kind-source={MemoryJsonNames.Of(entry.KindSource)} origin={MemoryJsonNames.Of(candidate.Origin)} " +
            $"vendor={entry.SourceVendor} -->\n");
        builder.Append(
            CultureInfo.InvariantCulture,
            $"Canonical entry `{entry.Id}` ({MemoryJsonNames.Of(entry.Kind)}), projected from `{entry.SourcePath}`.\n\n");
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
        builder.Append(
            "Every section below back-points to the canonical entry id it came from. **There is deliberately\n" +
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
