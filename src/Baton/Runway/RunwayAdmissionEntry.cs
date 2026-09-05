using System.Text.Json.Serialization;

namespace Baton.Runway;

/// <summary>
/// One evaluation of the runway hold, recorded whether it admitted or refused (#1896). Append-only
/// JSONL at <c>BatonPaths.RunwayAdmissionLedgerFile</c>; spec/baton.md §7's "Runway hold" states what
/// the file is for and why every evaluation is recorded rather than only the refusals.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every field is absent-safe.</b> Serialized with <c>JsonIgnoreCondition.WhenWritingNull</c> and
/// read back through <c>JsonLinesLedger</c>, which skips a line it cannot deserialize rather than
/// failing the read — so a row written by a build that knows a field an older reader does not still
/// parses, minus that field. camelCase names are declared per-property, the same construction-site
/// convention <c>CostLedgerEntry</c> uses, because the shared serializer applies no naming policy.
/// </para>
/// <para>
/// <b>This is evidence, not policy.</b> The operator direction of 2026-09-05 on #1896 is capture
/// first, decide later: the reservation arithmetic that produced
/// <see cref="EstimatedBurnPoints"/>/<see cref="OutstandingReservationPoints"/> is a starting point
/// (<see cref="LedgerMedianRunwayReservationPolicy"/>), and these rows are what it is meant to be
/// retuned from. Read them; do not read a threshold out of them.
/// </para>
/// </remarks>
/// <param name="At">When the gate was consulted, UTC.</param>
/// <param name="Vendor">The adapter tag being dispatched to — <c>claude</c>, <c>agy</c>, <c>codex</c>.</param>
/// <param name="Decision">One of <see cref="RunwayAdmissionDecisions"/>.</param>
/// <param name="DecidedBy">
/// Which arm produced <paramref name="Decision"/>: <see cref="RunwayAdmissionDecidedBy"/>. Distinguishes
/// a hold the vendor's own counters caused from one this issue's reservation arithmetic caused, which is
/// the first thing a tuning read has to be able to separate.
/// </param>
/// <param name="Reason">The gate's or the reservation arm's own reason for a hold; absent on a plain admit.</param>
/// <param name="OverrideReason">The operator's <c>--override-runway</c> reason, verbatim, when one was passed.</param>
/// <param name="Room">
/// <c>BatonPaths.RecordKey</c> of the room directory this dispatch would provision. Recorded on a HELD
/// evaluation too, even though that room is never created — it is the handle that ties the refusal to
/// the invocation an operator is looking at.
/// </param>
/// <param name="Role">The worker role whose burn was estimated, when the dispatch bound exactly one.</param>
/// <param name="Counters">The gated windows the decision was taken against, exactly as the gate read them.</param>
/// <param name="WeekHoldPct">The week threshold in force for this vendor at this evaluation.</param>
/// <param name="SessionHoldPct">The session threshold in force for this vendor at this evaluation.</param>
/// <param name="MaxSnapshotAgeHours">The snapshot-staleness limit in force for this vendor at this evaluation.</param>
/// <param name="SnapshotHarvestedAt">
/// When the snapshot the decision rests on was harvested. Absent when there was none — which is itself a
/// hold (spec/baton.md §7), and #1923's known bootstrap hole.
/// </param>
/// <param name="HeadroomPoints">
/// <c>RunwayDecision.HeadroomPoints</c> as the gate computed it — that parameter's own doc defines it.
/// Absent unless the gate admitted on readable counters.
/// </param>
/// <param name="OutstandingReservationPoints">
/// The sum of <see cref="EstimatedBurnPoints"/> over admissions on this vendor recorded at or after
/// <see cref="SnapshotHarvestedAt"/> — the spend the counters cannot have seen yet. Absent when no
/// reservation arithmetic ran.
/// </param>
/// <param name="EstimatedBurnPoints">What <see cref="IRunwayReservationPolicy"/> estimated this dispatch would burn.</param>
/// <param name="EstimateSource">
/// Which arm of the policy produced <see cref="EstimatedBurnPoints"/> — <c>flat-default</c>,
/// <c>ledger-median</c>, <c>off</c>. A number with no provenance cannot be tuned, so the provenance is
/// on the row rather than inferred from the policy name in settings at read time.
/// </param>
public sealed record RunwayAdmissionEntry(
    [property: JsonPropertyName("at")] DateTimeOffset At,
    [property: JsonPropertyName("vendor")] string Vendor,
    [property: JsonPropertyName("decision")] string Decision,
    [property: JsonPropertyName("decidedBy")] string DecidedBy,
    [property: JsonPropertyName("reason")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Reason = null,
    [property: JsonPropertyName("overrideReason")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? OverrideReason = null,
    [property: JsonPropertyName("room")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Room = null,
    [property: JsonPropertyName("role")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Role = null,
    [property: JsonPropertyName("counters")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<RunwayCounter>? Counters = null,
    [property: JsonPropertyName("weekHoldPct")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? WeekHoldPct = null,
    [property: JsonPropertyName("sessionHoldPct")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? SessionHoldPct = null,
    [property: JsonPropertyName("maxSnapshotAgeHours")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    double? MaxSnapshotAgeHours = null,
    [property: JsonPropertyName("snapshotHarvestedAt")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? SnapshotHarvestedAt = null,
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
    string? EstimateSource = null);

/// <summary>
/// The closed set of <see cref="RunwayAdmissionEntry.Decision"/> tokens (#1896's own wording). Strings
/// rather than an enum for the same reason <c>QuotaLedgerEntry.Outcome</c> is one: a ledger row is read
/// by <c>jq</c> and by Python at least as often as by this assembly, and a token an older reader does
/// not know must not fail its whole read.
/// </summary>
public static class RunwayAdmissionDecisions
{
    /// <summary>New spend was let through.</summary>
    public const string Admitted = "admitted";

    /// <summary>New spend was refused, and the dispatch exited non-zero.</summary>
    public const string Held = "held";

    /// <summary>A hold that <c>--override-runway "&lt;reason&gt;"</c> bypassed; the reason is on the row.</summary>
    public const string HeldOverridden = "held-overridden";

    /// <summary>
    /// The vendor has no <c>IVendorUsageSource</c> at all, so nothing was measured and nothing was gated.
    /// Deliberately not <see cref="Admitted"/>: unmeasured is a different claim from measured-and-fine,
    /// and collapsing the two is what would make this ledger say the counters approved something they
    /// never saw.
    /// </summary>
    public const string Unmeasured = "unmeasured";
}

/// <summary>The closed set of <see cref="RunwayAdmissionEntry.DecidedBy"/> tokens; same string rationale as
/// <see cref="RunwayAdmissionDecisions"/>.</summary>
public static class RunwayAdmissionDecidedBy
{
    /// <summary>The vendor's own harvested counters, via <c>RunwayGate.Evaluate</c>.</summary>
    public const string Counters = "counters";

    /// <summary>#1896's cross-dispatch reservation arithmetic — the counters admitted, the outstanding reservations did not.</summary>
    public const string Reservation = "reservation";

    /// <summary>No usage source exists for this vendor, so neither arm ran.</summary>
    public const string Unmeasured = "unmeasured";
}
