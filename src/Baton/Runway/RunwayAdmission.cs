using System.Text.Json.Serialization;

namespace Baton.Runway;

/// <summary>
/// #1896's per-room record: one vendor's admission as stamped on that room's <c>bindings.json</c>
/// (<c>WorkerBindingConfigEntry.RunwayAdmission</c>). <b>Every field means exactly what the
/// same-named one on <see cref="RunwayAdmissionEntry"/> means</b> — that record's parameter docs are
/// the definitions, and these are not a second set. What is missing here is only the ledger's own
/// bookkeeping (timestamp, room key, role, snapshot instant), which a room already knows about itself.
/// The one exception is <see cref="UnrecordedReason"/>, which has no counterpart on that record by
/// construction: it says the row was never written, so it can only live on the room's copy.
/// </summary>
/// <remarks>
/// <para>
/// <b>Beside <c>RunwayOverride</c>, never folded into it.</b> That record is the operator's audited
/// bypass, and <c>RunwayOverrideReasons</c> reads it to stamp the cost ledger; this is the decision
/// itself, present on rooms nobody overrode anything on. Merging them would either put a null reason on
/// every ordinary room or make the override read guess which rooms were really bypassed.
/// </para>
/// <para>
/// <b>Lives here rather than beside the binding record it is written on</b>, for the same layering
/// reason <see cref="RunwayCounter"/> does: <c>WorkflowStatusView</c> carries its wire projection
/// (<see cref="RunwayAdmissionView"/>), and the engine layer cannot see <c>Baton.Vendors</c>. It is also
/// the only half a room-scoped surface can show, since a refused dispatch never provisions a room.
/// </para>
/// <para>
/// <b>No <see cref="JsonPropertyName"/> anywhere on it</b>, deliberately: <c>WorkerBindingConfigWriter</c>
/// serializes <c>bindings.json</c> with no naming policy and every other binding field is PascalCase, the
/// same call <c>RunwayOverride</c> already made (spec/baton.md §7). The camelCase wire spelling every
/// status surface uses is <see cref="RunwayAdmissionView"/>'s, not this record's.
/// </para>
/// </remarks>
/// <param name="UnrecordedReason">
/// Why the fleet ledger could not be written for this dispatch (#1932 review), or null — the ordinary
/// case — when it was. The ledger fails open by design (spec/baton.md §7), so an admission can proceed
/// with no row behind it and no headroom reserved anywhere; this is what keeps that gap auditable on the
/// room rather than only in one stderr line a conductor lane's log swallows. It is set on every vendor's
/// record for the dispatch, because the write that failed was the dispatch's one batch.
/// </param>
public sealed record RunwayAdmission(
    string Vendor,
    string Decision,
    string DecidedBy,
    string? Reason = null,
    IReadOnlyList<RunwayCounter>? Counters = null,
    int? WeekHoldPct = null,
    int? SessionHoldPct = null,
    double? HeadroomPoints = null,
    double? OutstandingReservationPoints = null,
    double? EstimatedBurnPoints = null,
    string? EstimateSource = null,
    string? UnrecordedReason = null);

/// <summary>
/// The wire projection of <see cref="RunwayAdmission"/> (#1896) — the same values under camelCase names,
/// never a second derivation. Shared verbatim by <c>baton status --json</c>
/// (<c>WorkflowStatusView.Runway</c>) and by <c>fleet_status</c>/the daemon's fleet projection
/// (<c>FleetRoomStatusView.Runway</c>), so the two surfaces cannot drift into two spellings of one fact.
/// </summary>
/// <remarks>
/// Both of those fields are a LIST of these, one per vendor the dispatch gated (#1932 review): a composed
/// template binding two vendors records a decision per vendor, and showing whichever one a dictionary
/// happened to yield first made the surfaces disagree with the ledger in exactly the case the batch
/// decision exists for. <see cref="AllFrom"/> is the one projection both call.
/// </remarks>
public sealed record RunwayAdmissionView(
    [property: JsonPropertyName("vendor")] string Vendor,
    [property: JsonPropertyName("decision")] string Decision,
    [property: JsonPropertyName("decidedBy")] string DecidedBy,
    [property: JsonPropertyName("reason")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Reason = null,
    [property: JsonPropertyName("counters")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<RunwayCounterView>? Counters = null,
    [property: JsonPropertyName("weekHoldPct")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? WeekHoldPct = null,
    [property: JsonPropertyName("sessionHoldPct")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? SessionHoldPct = null,
    [property: JsonPropertyName("headroomPoints")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    double? HeadroomPoints = null,
    [property: JsonPropertyName("outstandingReservationPoints")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    double? OutstandingReservationPoints = null,
    [property: JsonPropertyName("estimatedBurnPoints")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    double? EstimatedBurnPoints = null,
    [property: JsonPropertyName("estimateSource")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? EstimateSource = null,
    [property: JsonPropertyName("unrecordedReason")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? UnrecordedReason = null)
{
    /// <summary>
    /// Every distinct vendor's record off one room's bindings, ordered by vendor so two readings of the
    /// same room cannot disagree, or <b>null</b> when there is none — never an empty list, the same
    /// "absent means nothing to say" rule <c>WorkflowStatusView.Arrests</c> states for itself.
    /// </summary>
    /// <remarks>
    /// A composed template binds several WORKERS to one vendor and stamps every binding with that
    /// vendor's one decision, so the duplicates are dropped here rather than rendered twice. First
    /// occurrence wins; they are copies of one record, so which one wins cannot matter.
    /// </remarks>
    public static IReadOnlyList<RunwayAdmissionView>? AllFrom(IEnumerable<RunwayAdmission?>? admissions)
    {
        var projected = (admissions ?? [])
            .Where(admission => admission is not null)
            .GroupBy(admission => admission!.Vendor, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => From(group.First())!)
            .ToList();

        return projected.Count == 0 ? null : projected;
    }

    /// <summary>The projection, or null when there is no admission record to project — every room
    /// dispatched before #1896, and every hand-authored <c>bindings.json</c>.</summary>
    public static RunwayAdmissionView? From(RunwayAdmission? admission) =>
        admission is null
            ? null
            : new RunwayAdmissionView(
                admission.Vendor,
                admission.Decision,
                admission.DecidedBy,
                admission.Reason,
                admission.Counters?.Select(c => new RunwayCounterView(c.Window, c.PercentUsed)).ToList(),
                admission.WeekHoldPct,
                admission.SessionHoldPct,
                admission.HeadroomPoints,
                admission.OutstandingReservationPoints,
                admission.EstimatedBurnPoints,
                admission.EstimateSource,
                admission.UnrecordedReason);
}

/// <summary>One gated vendor window as the gate read it, on the wire.</summary>
public sealed record RunwayCounterView(
    [property: JsonPropertyName("window")] string Window,
    [property: JsonPropertyName("percentUsed")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? PercentUsed = null);
