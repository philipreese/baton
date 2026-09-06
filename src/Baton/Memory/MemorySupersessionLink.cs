using System.Text.Json.Serialization;

namespace Baton.Memory;

/// <summary>
/// One supersession fact — "this live entry replaces that archived one" — as its own append-only row
/// (#1852 phase B, Q2), keyed by the pair of entry ids it relates.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why supersession is a row and not a field.</b> <see cref="MemoryEntry.Derive"/> takes no
/// supersession input, so a live entry's id is identical whether or not it supersedes anything, and
/// <see cref="MemoryStore.AppendAsync"/> skips a row whose id is already in the file. An import that
/// files the live root first and the archive second would therefore recompute the link correctly and
/// then discard the live half of it — permanently, since nothing in this namespace rewrites a row that
/// is already down (<see cref="MemoryEntry"/>'s own doc says what an append-only store offers instead).
/// Two separate <c>--root</c> runs lose both halves. Recording the link as its own fact is what makes
/// it landable at any time, in either order, from either side.
/// </para>
/// <para>
/// <b><see cref="Id"/> is the pair, so a re-import is a no-op here too.</b> The same dedupe property
/// <see cref="MemoryEntry"/> gets from its derived id: the ledger skips a link whose id is already in
/// the file, so re-running an import that recomputes the same link writes nothing and no link is ever
/// duplicated.
/// </para>
/// <para>
/// <b>Both ids are always in one repository's store</b> — <c>MemoryImportPlan.LinkSupersession</c>
/// refuses a cross-subject link — so this file lives inside the repository directory beside the entries
/// it links, and a reader never has to consult another repository's store to resolve one.
/// </para>
/// </remarks>
/// <param name="Id">See the remarks: the ordered pair, and the store's dedupe key.</param>
/// <param name="SupersedingId">The <see cref="MemoryEntry.Id"/> of the entry that replaces.</param>
/// <param name="SupersededId">The <see cref="MemoryEntry.Id"/> of the entry that is replaced.</param>
/// <param name="Repository">The subject both entries are filed under. Recorded so a row is readable on its own.</param>
/// <param name="RecordedAtUtc">When the link was first computed. Deliberately NOT part of <paramref name="Id"/>.</param>
public sealed record MemorySupersessionLink(
    [property: JsonPropertyName("id")]
    string Id,
    [property: JsonPropertyName("supersedingId")]
    string SupersedingId,
    [property: JsonPropertyName("supersededId")]
    string SupersededId,
    [property: JsonPropertyName("repository")]
    string Repository,
    [property: JsonPropertyName("recordedAtUtc")]
    DateTime RecordedAtUtc)
{
    /// <summary>
    /// The id a given ordered pair produces: <c>&lt;superseding&gt;:&lt;superseded&gt;</c>. Readable
    /// rather than digested, because unlike <see cref="MemoryEntry.Derive"/> there is nothing here to
    /// case-fold or path-normalise — both halves are already derived ids — and a link an operator can
    /// read straight out of the file is worth more than twelve saved characters.
    /// </summary>
    public static string Derive(string supersedingId, string supersededId)
    {
        ArgumentException.ThrowIfNullOrEmpty(supersedingId);
        ArgumentException.ThrowIfNullOrEmpty(supersededId);

        return $"{supersedingId}:{supersededId}";
    }

    /// <summary>Builds a link, deriving its <see cref="Id"/> from the pair.</summary>
    public static MemorySupersessionLink Create(
        string supersedingId, string supersededId, string repository, DateTime recordedAtUtc) =>
        new(Derive(supersedingId, supersededId), supersedingId, supersededId, repository, recordedAtUtc);
}
