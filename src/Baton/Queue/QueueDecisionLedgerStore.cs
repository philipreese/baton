using System.Text.Json.Serialization;
using Baton.Status;

namespace Baton.Queue;

/// <summary>
/// One recorded scheduling decision (#1934 Q4) — the fact the cost ledger (#1901) never had: the
/// ledger records what a lane <em>spent</em>, this records why it launched when it did, and #1912
/// becomes a reader of it.
/// </summary>
/// <param name="At">When the evaluation happened, UTC.</param>
/// <param name="Tag">The candidate item's tag, or null for a decision about no item at all (<c>no-items</c>, <c>hold</c>).</param>
/// <param name="Decision"><c>launched</c> | <c>waited</c> | <c>failed</c>.</param>
/// <param name="Reason">
/// For <c>waited</c>, a <see cref="QueueWaitReasons.Token"/> value. For <c>failed</c>, the error. Null
/// for <c>launched</c> — a launch has no reason beyond the counters beside it.
/// </param>
/// <param name="LiveWeight">The weighted tally over running rooms at evaluation time.</param>
/// <param name="FreeGb">The reading the decision compared against; absent, never a stand-in number, when there was none.</param>
/// <param name="FloorGb">The hour band's floor this evaluation compared against.</param>
/// <param name="Tier"><c>QueueTierResolution.TierKey</c> verbatim; absent when that was null.</param>
/// <param name="Adapter">The adapter resolved for the launch; absent for a wait.</param>
/// <param name="Model">The model resolved for the launch; absent for a wait.</param>
/// <param name="Effort">The effort resolved for the launch; absent for a wait.</param>
/// <param name="TierOverride">True when the item's axes differed from its tier's.</param>
/// <param name="OverrideReason">The item's <c>--reason</c>, present only alongside <paramref name="TierOverride"/>.</param>
/// <param name="Room">The room the item launched into; absent for a wait, present for a failure that had already provisioned one.</param>
public sealed record QueueDecisionEntry(
    [property: JsonPropertyName("at")] DateTimeOffset At,
    [property: JsonPropertyName("tag")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Tag,
    [property: JsonPropertyName("decision")] string Decision,
    [property: JsonPropertyName("reason")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Reason,
    [property: JsonPropertyName("liveWeight")] double LiveWeight,
    [property: JsonPropertyName("freeGb")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    double? FreeGb,
    [property: JsonPropertyName("floorGb")] double FloorGb,
    [property: JsonPropertyName("tier")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Tier = null,
    [property: JsonPropertyName("adapter")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Adapter = null,
    [property: JsonPropertyName("model")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Model = null,
    [property: JsonPropertyName("effort")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Effort = null,
    [property: JsonPropertyName("tierOverride")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    bool TierOverride = false,
    [property: JsonPropertyName("overrideReason")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? OverrideReason = null,
    [property: JsonPropertyName("room")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Room = null)
{
    public const string Launched = "launched";
    public const string Waited = "waited";
    public const string Failed = "failed";

    /// <summary>
    /// A work item moved from one <see cref="WorkStage"/> to the next (#1934 slice 2). A fourth
    /// decision word rather than a launch: the transition and the launch it leads to are two facts, and
    /// folding them would lose the evidence — the outcome word, the PR head, the verdict's counts —
    /// that the transition was derived from.
    /// <para>
    /// <b>Its <see cref="Reason"/> must name that evidence</b>, and not only for the reader: the
    /// collapse rule below keys on <see cref="VerdictKey"/>, so two rounds whose reasons read alike
    /// would collapse into one row and the second transition would vanish from the file this queue
    /// exists to make auditable. <c>WorkItemLifecycle</c>'s reasons carry the stage pair and the head
    /// sha for exactly that purpose.
    /// </para>
    /// </summary>
    public const string Advanced = "advanced";

    /// <summary>
    /// The identity a repeated verdict is collapsed on — see
    /// <see cref="QueueDecisionLedgerStore.AppendAsync"/> for what that collapse is and is not.
    /// Deliberately excludes the counters: a wait that is still "slots" with a live weight of 3.0
    /// instead of 2.0 is the same standing verdict, and re-recording it every tick would bury the
    /// launches in a heartbeat log nobody can read.
    /// </summary>
    [JsonIgnore]
    public string VerdictKey => $"{Decision}|{Reason}|{Tag}";
}

/// <summary>
/// The queue's append-only decision ledger, at <c>BatonPaths.QueueDecisionLedgerFile</c> — the same
/// <see cref="JsonLinesLedger{TEntry}"/> mechanism the burn and cost ledgers share (#1884), not a
/// third copy of it.
/// </summary>
/// <remarks>
/// <para>
/// <b>No dedupe key.</b> The shared ledger deduplicates on an execution id; a scheduling decision has
/// none — it is an observation at an instant, and two identical observations minutes apart are two
/// facts. The selector therefore returns null, which
/// <see cref="JsonLinesLedger{TEntry}.AppendAsync"/> already documents as "always appended".
/// </para>
/// <para>
/// <b>Fails open, never gates</b>, exactly as <c>QuotaLedgerStore</c> does, and with the same split:
/// this store throws, and the caller — the daemon's queue service — is where the log-and-swallow
/// happens.
/// </para>
/// </remarks>
public static class QueueDecisionLedgerStore
{
    internal static readonly JsonLinesLedger<QueueDecisionEntry> Ledger =
        new("baton-queue-ledger", "queue decision ledger", _ => null);

    /// <summary>
    /// Appends <paramref name="entry"/> unless <paramref name="previousVerdictKey"/> already equals
    /// its <see cref="QueueDecisionEntry.VerdictKey"/>, and returns the key the caller should carry
    /// into the next evaluation.
    /// </summary>
    /// <remarks>
    /// spec/baton.md §13 states what the ledger does and does not contain. Two mechanical notes that
    /// belong with the code rather than the spec: the guard is on
    /// <see cref="QueueDecisionEntry.VerdictKey"/> alone, never on the decision kind, so "a launch is
    /// always appended" is a consequence of a launch changing the item's state (and so the next
    /// verdict) rather than a second rule; and the key is the CALLER's to carry across evaluations,
    /// which is why it is returned rather than held in a field here — this store has no per-scheduler
    /// state and two schedulers would need two keys.
    /// </remarks>
    public static async Task<string> AppendAsync(
        QueueDecisionEntry entry,
        string? previousVerdictKey,
        string ledgerFilePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrEmpty(ledgerFilePath);

        var key = entry.VerdictKey;
        if (string.Equals(previousVerdictKey, key, StringComparison.Ordinal))
        {
            return key;
        }

        await Ledger.AppendAsync([entry], ledgerFilePath, cancellationToken).ConfigureAwait(false);
        return key;
    }

    /// <summary>Every parseable line, in write order — read tolerance and the never-throws posture are
    /// <see cref="JsonLinesLedger{TEntry}.ReadAllAsync"/>'s.</summary>
    public static Task<IReadOnlyList<QueueDecisionEntry>> ReadAllAsync(
        string ledgerFilePath, CancellationToken cancellationToken = default) =>
        Ledger.ReadAllAsync(ledgerFilePath, cancellationToken);
}
