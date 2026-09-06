using System.Text.Json.Serialization;
using Baton.Status;

namespace Baton.Memory;

/// <summary>
/// One operator assertion that a checkout path belongs to a repository — the fallback for a path git
/// can no longer answer for.
/// </summary>
/// <param name="Path">A <c>BatonPaths.RecordKey</c>, matched with <c>BatonPaths.RecordKeyComparer</c>.</param>
/// <param name="Repository">The canonical <c>RepositoryIdentity.Value</c> the operator asserts for it.</param>
/// <param name="AssertedBy">Who asserted it. Non-empty by construction: an unattributed assertion is indistinguishable from a measurement.</param>
/// <param name="AssertedAtUtc">When.</param>
/// <param name="Reason">Why, in the operator's own words. Optional.</param>
public sealed record MemoryAliasEntry(
    [property: JsonPropertyName("path")]
    string Path,
    [property: JsonPropertyName("repository")]
    string Repository,
    [property: JsonPropertyName("assertedBy")]
    string AssertedBy,
    [property: JsonPropertyName("assertedAtUtc")]
    DateTime AssertedAtUtc,
    [property: JsonPropertyName("reason")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Reason = null);

/// <summary>
/// The append-only <c>aliases.jsonl</c> beside the canonical stores: paths whose repository identity
/// git cannot produce, mapped to one an operator asserted (#1852's plan §1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Consulted only when the git probe yields nothing, and never as an override of one.</b> A live
/// probe is a measurement; an alias is an assertion, and an assertion that could silently displace a
/// measurement would make every identity in the store unreadable — a reader could no longer tell which
/// kind of claim a row's <c>repository</c> is. The population this exists for is the opposite case:
/// #1852's survey found two historical checkout paths (<c>…\repos\aer</c>, <c>…\repos\aer\aer-flow</c>)
/// that no longer exist, so nothing can be probed at them, and their memory would otherwise be
/// unfileable.
/// </para>
/// <para>
/// <b>It is deliberately NOT the subject-adjudication mechanism.</b> Q1 (operator, 2026-09-05) keeps
/// an entry's subject separate from its provenance, and that separation lives on
/// <see cref="MemoryEntry"/>'s own fields rather than here — this store answers "which repository is
/// at this PATH", never "whose memory is this FILE". The <c>alpaca-agent-bot</c> shape (a checkout
/// whose origin is one repository and whose memory names another) is therefore imported under the
/// derived identity, and phase B ships no writer for an adjudication that would change it. Stated
/// rather than left to inference: an operator wanting that today edits nothing here, because there is
/// nothing here that would do it.
/// </para>
/// <para>
/// Machine-wide rather than per-repository, and so it sits at the storage root rather than inside one
/// repository's directory: an alias maps a path to a repository, so filing it under the answer it
/// produces would require knowing the answer to find the file.
/// </para>
/// </remarks>
public static class MemoryAliasStore
{
    /// <summary>
    /// This store's shared ledger, under its own lock prefix. The dedupe key is the asserted PATH, so
    /// re-asserting the same path writes nothing — a correction is made by a human editing the file,
    /// not by an append that silently shadows an earlier row a reader would still see.
    /// </summary>
    internal static readonly JsonLinesLedger<MemoryAliasEntry> Ledger =
        new("baton-memory-aliases", "memory alias store", entry => entry.Path);

    /// <summary>Every assertion in the file, oldest first. A missing file is an empty list.</summary>
    public static Task<IReadOnlyList<MemoryAliasEntry>> ReadAllAsync(
        string aliasFilePath, CancellationToken cancellationToken = default) =>
        Ledger.ReadAllAsync(aliasFilePath, cancellationToken);

    /// <summary>Appends assertions whose path is not already recorded.</summary>
    public static Task AppendAsync(
        IReadOnlyList<MemoryAliasEntry> entries, string aliasFilePath, CancellationToken cancellationToken = default) =>
        Ledger.AppendAsync(entries, aliasFilePath, cancellationToken);

    /// <summary>
    /// The repository <paramref name="checkoutPath"/> is asserted to belong to, or
    /// <see langword="null"/>. Paths are compared through <see cref="BatonPaths.RecordKeyComparer"/>,
    /// so two spellings of one directory resolve to one assertion.
    /// </summary>
    public static string? Resolve(IReadOnlyList<MemoryAliasEntry> aliases, string? checkoutPath)
    {
        ArgumentNullException.ThrowIfNull(aliases);

        if (checkoutPath is not { Length: > 0 })
        {
            return null;
        }

        string key;
        try
        {
            key = BatonPaths.RecordKey(checkoutPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // A decoded reading that is not a usable path names no checkout and matches no assertion.
            return null;
        }

        return aliases.LastOrDefault(a => BatonPaths.RecordKeyComparer.Equals(a.Path, key))?.Repository;
    }
}
