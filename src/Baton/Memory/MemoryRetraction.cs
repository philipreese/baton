using System.Text.Json.Serialization;

namespace Baton.Memory;

/// <summary>
/// One retraction — "this entry was true and is not any more" — as its own append-only row (#2113),
/// keyed by the entry it retracts.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a retraction is a row and not a deletion or a field.</b> The rule this type exists to keep
/// is stated once, in spec/baton.md §12: history is never deleted. An entry's id is derived from facts
/// a retraction does not change (<see cref="MemoryEntry.Derive"/>) and the store never rewrites a row
/// that is already down, so — exactly as <see cref="MemorySupersessionLink"/> reasons for a link — the
/// only place a fact discovered <i>after</i> the entry was written can land is a row of its own.
/// Deleting the entry instead would take the reason with it; <c>baton memory import --undo</c> already
/// exists for the different case of a row that should never have been written.
/// </para>
/// <para>
/// <b><see cref="EntryId"/> is the dedupe key, so an entry is retracted at most once.</b> The ledger
/// skips a second row for the same entry, which is why <c>baton memory retract</c> refuses an
/// already-retracted id up front rather than reporting a write that would have appended nothing. There
/// is deliberately no un-retract: a retraction that turned out to be wrong is answered by
/// <c>baton memory add</c> of the fact as it now stands, which leaves the mistaken retraction readable
/// as the history it is.
/// </para>
/// <para>
/// <b>Never parsed for meaning.</b> <see cref="Reason"/> is stored verbatim and read by a person
/// (<c>baton memory audit</c>, the sync report); nothing routes on it (Architecture Rule 1).
/// </para>
/// </remarks>
/// <param name="EntryId">The <see cref="MemoryEntry.Id"/> retracted. The store's dedupe key.</param>
/// <param name="Repository">The subject the entry is filed under. Recorded so a row is readable on its own.</param>
/// <param name="Reason">Why, in the retractor's own words. Required and non-blank by construction.</param>
/// <param name="RetractedBy">
/// Who retracted it — the same reading <see cref="MemoryEntry.AssertedBy"/> carries on an authored
/// entry: <c>operator</c> outside a lane, the lane's <c>role/vendor/room</c> inside one.
/// </param>
/// <param name="RetractedAtUtc">When. Deliberately NOT part of the key.</param>
public sealed record MemoryRetraction(
    [property: JsonPropertyName("entryId")]
    string EntryId,
    [property: JsonPropertyName("repository")]
    string Repository,
    [property: JsonPropertyName("reason")]
    string Reason,
    [property: JsonPropertyName("retractedBy")]
    string RetractedBy,
    [property: JsonPropertyName("retractedAtUtc")]
    DateTime RetractedAtUtc)
{
    /// <summary>Builds a retraction, refusing a blank reason — an unexplained retraction is indistinguishable from a deletion.</summary>
    public static MemoryRetraction Create(
        string entryId, string repository, string reason, string retractedBy, DateTime retractedAtUtc)
    {
        ArgumentException.ThrowIfNullOrEmpty(entryId);
        ArgumentException.ThrowIfNullOrEmpty(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentException.ThrowIfNullOrEmpty(retractedBy);

        return new MemoryRetraction(entryId, repository, reason, retractedBy, retractedAtUtc);
    }
}
