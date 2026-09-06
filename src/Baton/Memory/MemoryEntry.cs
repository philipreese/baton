using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Baton.Memory;

/// <summary>
/// What a canonical memory entry <b>is</b> — the five kinds #1852's plan enumerates, plus the honest
/// sixth for an entry whose kind nothing here could establish.
/// </summary>
/// <remarks>
/// <b>Never inferred from an entry's text.</b> A kind is declared by the writer, or read off a
/// declaration the source file already carried, or inferred from a filename prefix and recorded as
/// such (<see cref="MemoryKindSource"/>) — the one thing it is never derived from is what the memory
/// SAYS, which is the "silently promote a transcript inference to truth" failure #1852 names. Two
/// members accordingly have no import-time producer at all: nothing in the observed filename
/// vocabulary maps to <see cref="Hypothesis"/> or <see cref="ExecutionDerivedSummary"/>, and inventing
/// a mapping to fill the table would be exactly that inference.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<MemoryKind>))]
public enum MemoryKind
{
    /// <summary>Something true about the world that outlives the conversation it was learned in.</summary>
    [JsonStringEnumMemberName("durable-fact")] DurableFact,

    /// <summary>How the operator wants work done — a correction, a confirmed approach, a standing rule.</summary>
    [JsonStringEnumMemberName("operator-preference")] OperatorPreference,

    /// <summary>A belief held provisionally. No import-time producer; see the type remarks.</summary>
    [JsonStringEnumMemberName("hypothesis")] Hypothesis,

    /// <summary>
    /// A fact recorded as history rather than as current truth. Every entry imported from an archived
    /// root is one, by Q2's ruling (operator, 2026-09-05) — see <see cref="MemoryKindSource.InferredFromArchive"/>.
    /// </summary>
    [JsonStringEnumMemberName("historical-note")] HistoricalNote,

    /// <summary>Derived from execution evidence rather than authored. No import-time producer; see the type remarks.</summary>
    [JsonStringEnumMemberName("execution-derived-summary")] ExecutionDerivedSummary,

    /// <summary>
    /// Nothing available established a kind. <b>Not a default and not a sixth category of memory</b> —
    /// it is the absence of a kind, recorded rather than guessed at, and it is what an entry with no
    /// declaration and no recognised filename prefix gets.
    /// </summary>
    [JsonStringEnumMemberName("unknown")] Unknown,
}

/// <summary>
/// Where an entry's <see cref="MemoryKind"/> came from. Carried on every entry so a reader can tell a
/// kind the writer asserted from one this importer guessed at — the two are different claims and a
/// single <c>kind</c> field would flatten them.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MemoryKindSource>))]
public enum MemoryKindSource
{
    /// <summary>The source file declared it in its own front-matter. The strongest reading available.</summary>
    [JsonStringEnumMemberName("declared")] Declared,

    /// <summary>
    /// Inferred from the filename's leading token (<c>feedback_…</c>, <c>project_…</c>) through
    /// <see cref="MemoryKindInference"/>'s pinned table. A guess, labelled as one.
    /// </summary>
    [JsonStringEnumMemberName("inferred-from-prefix")] InferredFromPrefix,

    /// <summary>
    /// The entry came from an archived root, which Q2 (operator, 2026-09-05) rules
    /// <see cref="MemoryKind.HistoricalNote"/> whatever the file itself declares. Recorded distinctly
    /// from <see cref="Declared"/> precisely because it can overrule one.
    /// </summary>
    [JsonStringEnumMemberName("inferred-from-archive")] InferredFromArchive,

    /// <summary>No declaration and no recognised prefix.</summary>
    [JsonStringEnumMemberName("unknown")] Unknown,
}

/// <summary>
/// One entry in Baton's canonical memory store (#1852 phase B) — a memory's text, the repository it
/// is <b>about</b>, and where it came from, as one immutable append-only row.
/// </summary>
/// <remarks>
/// <para>
/// <b>Subject and provenance are two facts, not one</b> (Q1, operator 2026-09-05, and spec/baton.md
/// §12). <see cref="Repository"/> is the subject — whose memory this is — and
/// <see cref="SourcePath"/>/<see cref="SourceVendor"/>/<see cref="SourceScope"/>/<see cref="SourceMtimeUtc"/>/<see cref="Sha256"/>
/// are the provenance of the file it was read out of. A Baton fact authored from another repository's
/// checkout is keyed to Baton and still records the checkout it came from. The import in this phase
/// only ever files an entry under the identity the source root <i>derived</i>, because deciding
/// otherwise requires reading the entry and adjudicating it: <see cref="AssertedBy"/> is the field
/// that would carry such an adjudication, and <b>this phase ships no writer for it</b> — stated so a
/// reader does not read the field's existence as a capability.
/// </para>
/// <para>
/// <b><see cref="Id"/> is derived, not minted</b>, and that is what makes a re-import a no-op:
/// <c>JsonLinesLedger</c> skips a row whose key is already in the file, so the same file imported
/// twice produces the same id and the second append writes nothing. <see cref="Derive"/> states which
/// facts go into it and, more importantly, which deliberately do not.
/// </para>
/// <para>
/// <b>The store is a copy, never a move</b>: the import opens every source read-only and leaves it
/// byte-identical (<c>ImportManifest</c> is what makes the copy reversible). What is stored is the
/// file's <b>whole</b> text — front-matter included, nothing parsed out, nothing summarised — but it
/// is a UTF-8 <i>decode</i> of the bytes rather than the bytes themselves; see <paramref name="Text"/>
/// and <paramref name="Sha256"/> for which of the two is the authority.
/// </para>
/// </remarks>
/// <param name="Id">See <see cref="Derive"/>. The store's dedupe key.</param>
/// <param name="Repository">The subject: a <c>RepositoryIdentity.Value</c>, never a checkout path.</param>
/// <param name="Kind">What this entry is.</param>
/// <param name="KindSource">How <paramref name="Kind"/> was arrived at.</param>
/// <param name="Text">
/// The source file's whole text — front-matter included, nothing parsed out — as <b>decoded UTF-8</b>,
/// with a leading byte-order mark consumed and any byte sequence that is not valid UTF-8 replaced by
/// U+FFFD. It is therefore not guaranteed to reproduce <paramref name="Sha256"/>: for a BOM-prefixed
/// or non-UTF-8 source it provably will not. <paramref name="Sha256"/> is the authority on what the
/// file held; this is the readable copy of it.
/// </param>
/// <param name="Sha256">
/// Lower-case hex SHA-256 of the source file's BYTES, taken from <b>the same read</b> that produced
/// <paramref name="Text"/> — not from the earlier inventory walk, so a file edited between the two
/// cannot be stored under a digest that describes a version of it nobody kept.
/// </param>
/// <param name="SourcePath">The absolute path the text was read from.</param>
/// <param name="SourceVendor">Which vendor's root it sat in (<c>claude</c>, <c>codex</c>).</param>
/// <param name="SourceScope">Whether that root is the vendor's own or Baton-managed.</param>
/// <param name="SourceMtimeUtc">
/// The source file's last-write time, taken from <b>the same read</b> that produced
/// <paramref name="Text"/> and <paramref name="Sha256"/> — off that read's own open handle, not from
/// the earlier inventory walk, so this cannot date a version of the file the digest is not of.
/// </param>
/// <param name="ImportedAtUtc">When this row was built. Deliberately NOT part of <paramref name="Id"/>.</param>
/// <param name="Supersedes">
/// Ids this entry replaces — the live-over-archived link Q2 asks for. Absent when it replaces nothing;
/// never an empty array, so "supersedes nothing" and "supersedes an unknown set" cannot be confused.
/// <b>A projection, not a stored field</b>: <c>MemoryStore.ReadResolvedAsync</c> fills it in from
/// <c>links.jsonl</c>, and a row on disk never carries it. <see cref="MemorySupersessionLink"/> states
/// why an append-only row with a derived id cannot hold a link that a later import discovers.
/// </param>
/// <param name="SupersededBy">The mirror of <paramref name="Supersedes"/>, on the archived side, and equally a projection.</param>
/// <param name="Evidence">
/// Back-pointers into the append-only ledgers (spec/baton.md §7) for an entry derived from execution
/// evidence.
/// <b>Always absent on an imported entry</b> — an import has no execution behind it — and present here
/// so a later phase's writer does not need a schema change to say what it always was.
/// </param>
/// <param name="AssertedBy">
/// Who adjudicated <paramref name="Repository"/>, when it was asserted rather than derived. Absent
/// means derived, which is every entry this phase writes.
/// </param>
public sealed record MemoryEntry(
    [property: JsonPropertyName("id")]
    string Id,
    [property: JsonPropertyName("repository")]
    string Repository,
    [property: JsonPropertyName("kind")]
    MemoryKind Kind,
    [property: JsonPropertyName("kindSource")]
    MemoryKindSource KindSource,
    [property: JsonPropertyName("text")]
    string Text,
    [property: JsonPropertyName("sha256")]
    string Sha256,
    [property: JsonPropertyName("sourcePath")]
    string SourcePath,
    [property: JsonPropertyName("sourceVendor")]
    string SourceVendor,
    [property: JsonPropertyName("sourceScope")]
    VendorMemoryScope SourceScope,
    [property: JsonPropertyName("sourceMtimeUtc")]
    DateTime SourceMtimeUtc,
    [property: JsonPropertyName("importedAtUtc")]
    DateTime ImportedAtUtc,
    [property: JsonPropertyName("supersedes")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? Supersedes = null,
    [property: JsonPropertyName("supersededBy")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? SupersededBy = null,
    [property: JsonPropertyName("evidence")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? Evidence = null,
    [property: JsonPropertyName("assertedBy")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? AssertedBy = null)
{
    /// <summary>
    /// The id of the entry a given source file produces for a given subject: a 32-hex-character
    /// SHA-256 over exactly three facts — the subject, the source path, and the content digest.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What is in it, and why each one.</b> The subject, so the same file adjudicated to two
    /// repositories is two entries rather than one shared row. The source path, so two copies of one
    /// text in two roots stay two rows with two provenances (the audit's <c>duplicate</c> finding is
    /// about telling an operator they exist, not about collapsing them here). The content digest, so
    /// an edited file is a NEW entry rather than a silent overwrite of an old one — the store is
    /// append-only and has no overwrite to offer.
    /// </para>
    /// <para>
    /// <b>What is deliberately NOT in it: any clock.</b> Neither the import time nor the source mtime
    /// participates, because a file that is touched but not edited must not mint a second entry — that
    /// would make "re-import is a no-op" hold only until something ran <c>touch</c>, which is not a
    /// property worth claiming.
    /// </para>
    /// <para>
    /// <b>The path is case-folded and fully qualified</b> through <c>BatonPaths.RecordKey</c> plus a
    /// lower-casing this method owns. <c>RecordKey</c> normalises separators and relative segments but
    /// does not fold case (its comparer, <c>BatonPaths.RecordKeyComparer</c>, is what does) — so
    /// hashing its output directly would give <c>C:\X\a.md</c> and <c>c:\x\a.md</c> two ids and quietly
    /// re-import the same file. <c>RepositoryIdentity.From</c> folds its own path half for the same
    /// reason.
    /// </para>
    /// </remarks>
    public static string Derive(string repository, string sourcePath, string contentSha256)
    {
        ArgumentException.ThrowIfNullOrEmpty(repository);
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        ArgumentException.ThrowIfNullOrEmpty(contentSha256);

        var key = string.Join(
            '\n',
            repository.ToLowerInvariant(),
            Status.BatonPaths.RecordKey(sourcePath).ToLowerInvariant(),
            contentSha256.ToLowerInvariant());

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32].ToLowerInvariant();
    }
}
