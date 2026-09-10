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
/// <param name="Decision"><c>launched</c> | <c>waited</c> | <c>failed</c> | <c>cancelled</c>.</param>
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

    /// <summary>An operator cancelled a queued request before the scheduler claimed its launch.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>
    /// A work item moved from one <see cref="WorkStage"/> to the next (#1934 slice 2). A fourth
    /// decision word rather than a launch: the transition and the launch it leads to are two facts, and
    /// folding them would lose what the transition was derived from — spec/baton.md §13 enumerates it.
    /// <para>
    /// <b>Its <see cref="Reason"/> must name that evidence</b>, for the mechanical reason spec/baton.md
    /// §13 states as well as the readable one: it feeds <see cref="VerdictKey"/>, which the collapse
    /// below is keyed on. <c>WorkItemLifecycle</c>'s reasons carry the stage pair and the head sha for
    /// exactly that purpose.
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

    /// <summary>
    /// The one decision kind whose persisted queue state promises a matching ledger fact. Other
    /// decisions remain append-only observations; cancellation is keyed by its retained timestamp so
    /// a retry after an append failure repairs that one missing fact without duplicating it.
    /// </summary>
    [JsonIgnore]
    internal string? CancellationKey =>
        Decision == Cancelled && Tag is { Length: > 0 } tag
            ? $"{tag}|{At.ToUniversalTime():O}"
            : null;
}

/// <summary>
/// The queue's append-only decision ledger, at <c>BatonPaths.QueueDecisionLedgerFile</c> — the same
/// <see cref="JsonLinesLedger{TEntry}"/> mechanism the burn and cost ledgers share (#1884), not a
/// third copy of it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cancellation-only durable dedupe.</b> Ordinary scheduling decisions have no durable dedupe key:
/// they are observations at instants, and two identical observations minutes apart are two facts. The
/// scheduler carries its own in-memory verdict key to collapse repeated ticks. Cancellation is the
/// exception because a persisted <see cref="QueueItemState.Cancelled"/> plus its timestamp promises one
/// matching fact even if the process dies between the queue write and the append. Its key is the tag
/// and retained timestamp, so the daemon may replay it without duplicating the fact.
/// </para>
/// <para>
/// <b>CLI and daemon have different failure handling.</b> This store throws. The CLI lets an immediate
/// cancellation append failure reach the operator after the queue mutation committed; the daemon
/// retries retained cancellations on later ticks and logs-and-continues if that replay cannot append.
/// Neither path lets ledger availability gate a launch.
/// </para>
/// </remarks>
public static class QueueDecisionLedgerStore
{
    internal static readonly JsonLinesLedger<QueueDecisionEntry> Ledger =
        new("baton-queue-ledger", "queue decision ledger", entry => entry.CancellationKey);

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

    /// <summary>
    /// Records the cancellation fact for one retained item, once. The queue write precedes this
    /// accounting append, so the CLI can report a failed immediate append while the daemon's later
    /// reconciliation repairs the persisted fact. Its key includes the retained timestamp: a tag is
    /// not enough, because the ledger's ordinary decisions deliberately do not deduplicate.
    /// </summary>
    public static Task AppendCancellationAsync(
        DateTimeOffset cancelledAt,
        string tag,
        string ledgerFilePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(tag);
        ArgumentException.ThrowIfNullOrEmpty(ledgerFilePath);

        return Ledger.AppendAsync(
            [new QueueDecisionEntry(
                cancelledAt, tag, QueueDecisionEntry.Cancelled, "operator cancelled before launch",
                LiveWeight: 0, FreeGb: null, FloorGb: 0)],
            ledgerFilePath,
            cancellationToken);
    }

    /// <summary>
    /// Replays each persisted cancellation that carries the timestamp required for its durable key.
    /// This is deliberately narrow reconciliation, not a general event delivery mechanism: only the
    /// retained <see cref="QueueItemState.Cancelled"/> state promises a ledger fact, and the ledger's
    /// cancellation key makes every replay idempotent.
    /// </summary>
    public static async Task ReconcileCancellationsAsync(
        IReadOnlyList<QueueItem> items,
        string ledgerFilePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentException.ThrowIfNullOrEmpty(ledgerFilePath);

        foreach (var item in items)
        {
            if (item is not { State: QueueItemState.Cancelled, CancelledAt: { } cancelledAt })
            {
                continue;
            }

            await AppendCancellationAsync(cancelledAt, item.Tag, ledgerFilePath, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Every parseable line, in write order — read tolerance and the never-throws posture are
    /// <see cref="JsonLinesLedger{TEntry}.ReadAllAsync"/>'s.</summary>
    public static Task<IReadOnlyList<QueueDecisionEntry>> ReadAllAsync(
        string ledgerFilePath, CancellationToken cancellationToken = default) =>
        Ledger.ReadAllAsync(ledgerFilePath, cancellationToken);
}
