using Baton.Accounting;

namespace Baton.Runway;

/// <summary>
/// What one about-to-be-dispatched role is expected to burn, in <b>percentage points of the vendor
/// window being gated on</b> — the same unit the hold's own thresholds are in, which is what makes
/// "reservations exceed headroom" a comparison at all.
/// </summary>
/// <remarks>
/// <b>A point is treated as fungible across the two gated windows</b> (week and session), and it is
/// not: one execution is a larger fraction of a five-hour window than of a weekly one. The reservation
/// arm applies this one estimate to each window's own headroom independently and holds if either is
/// exceeded, which is conservative on the week and optimistic on the session. That approximation is
/// deliberate and is exactly what <see cref="RunwayAdmissionEntry"/>'s recorded rows exist to correct:
/// capture first, decide later (operator direction, 2026-09-05, #1896). Do not read it as a measurement.
/// </remarks>
/// <param name="Points">Percentage points of the gated window. Never negative; zero means "reserve nothing".</param>
/// <param name="Source">
/// Which arm produced <paramref name="Points"/>, recorded verbatim on the row's
/// <see cref="RunwayAdmissionEntry.EstimateSource"/>. See <see cref="RunwayEstimateSources"/>.
/// </param>
public sealed record RunwayBurnEstimate(double Points, string Source);

/// <summary>The closed set of <see cref="RunwayBurnEstimate.Source"/> tokens.</summary>
public static class RunwayEstimateSources
{
    /// <summary>The declared per-role constant — <see cref="LedgerMedianRunwayReservationPolicy.FlatDefaultPoints"/>.</summary>
    public const string FlatDefault = "flat-default";

    /// <summary>Derived from this repository's cost ledger, per <see cref="LedgerMedianRunwayReservationPolicy"/>.</summary>
    public const string LedgerMedian = "ledger-median";

    /// <summary>Reservations are switched off for this fleet; the estimate is zero and nothing is ever held by the reservation arm.</summary>
    public const string Off = "off";
}

/// <summary>What the policy is asked to estimate for.</summary>
/// <param name="Vendor">The adapter tag this dispatch would spawn on.</param>
/// <param name="Role">The worker role, when the dispatch bound exactly one; null for a composed template with several.</param>
/// <param name="LedgerRows">
/// This repository's cost-ledger rows, oldest first, or empty when none were read. Supplied by the
/// caller rather than read here so the policy stays pure and testable, and so a policy that declares
/// <see cref="IRunwayReservationPolicy.UsesCostLedger"/> false costs no ledger read at all.
/// </param>
public sealed record RunwayEstimateContext(string Vendor, string? Role, IReadOnlyList<CostLedgerEntry> LedgerRows);

/// <summary>
/// <b>The pluggable half of #1896's reservation arithmetic.</b> The operator direction of 2026-09-05 is
/// that this must never be hard-wired: the shipped default is a conservative starting point, the fleet
/// can swap it through <c>settings.json</c> (<c>RunwayHoldSettings.ReservationPolicy</c>, resolved by
/// <see cref="RunwayReservationPolicies.Resolve"/>), and every decision it produces is recorded so a
/// later one can be argued from data instead of from taste.
/// </summary>
public interface IRunwayReservationPolicy
{
    /// <summary>The token this policy is selected by in <c>settings.json</c>.</summary>
    string Name { get; }

    /// <summary>
    /// Whether <see cref="Estimate"/> reads <see cref="RunwayEstimateContext.LedgerRows"/>. False lets
    /// the dispatch path skip resolving a repository identity and reading the cost ledger entirely —
    /// a git probe and a file read that would otherwise be paid on every dispatch for nothing.
    /// </summary>
    bool UsesCostLedger { get; }

    /// <summary>Never throws and never returns null; an unusable input resolves to the conservative default.</summary>
    RunwayBurnEstimate Estimate(RunwayEstimateContext context);
}

/// <summary>
/// <b>The shipped default</b> (<c>ledger-median</c>, per spec/baton.md §7). A flat per-role anchor,
/// multiplied by the ratio of that role's median billed tokens to the median across every role — but only
/// once at least <see cref="MinimumLedgerRows"/> of the role's own priced rows exist. Below that the
/// anchor stands alone, because a median over three rows is a rumour.
/// </summary>
/// <remarks>
/// <para>
/// <b>The ledger cannot say how big a vendor window is</b>, so it cannot convert tokens into points on
/// its own. What it can say is <i>relative</i>: this role's median billed tokens against the median
/// across every role. That ratio scales the declared anchor below. Anyone tempted to read
/// <see cref="FlatDefaultPoints"/> as measured should read <see cref="RunwayBurnEstimate"/>'s remarks
/// first — it is a starting point of the same kind #1848's 85/90 thresholds were.
/// </para>
/// <para>
/// <b>Both constants are declared here and nowhere else.</b> spec/baton.md §7 cites this type rather
/// than transcribing the numbers, for the reason the <c>record-once</c> gate exists.
/// </para>
/// </remarks>
public sealed class LedgerMedianRunwayReservationPolicy : IRunwayReservationPolicy
{
    /// <summary>
    /// The anchor: what one admission is assumed to burn, in percentage points of the gated window,
    /// before any ledger evidence exists. One point — deliberately small, because this arm can only ever
    /// refuse work, and a fleet whose first dispatch of the day is held by an invented number is worse
    /// off than one that races a little. It is large enough to bite: a snapshot one point under the week
    /// threshold in force (<c>RunwayThresholds.DefaultWeekHoldPct</c> is where the shipped value is
    /// declared, and an operator may retune it) has exactly one point of headroom, so the second
    /// concurrent dispatch against it is held.
    /// </summary>
    public const double FlatDefaultPoints = 1.0;

    /// <summary>
    /// How many of a role's own priced cost-ledger rows must exist before its median displaces
    /// <see cref="FlatDefaultPoints"/>. Ten: enough that one outlier attempt cannot move the median,
    /// small enough that a fresh repository reaches it inside a day of ordinary dispatching.
    /// </summary>
    public const int MinimumLedgerRows = 10;

    /// <summary>The token <c>settings.json</c> selects this policy by; also the shipped default when the key is absent.</summary>
    public const string PolicyName = "ledger-median";

    public string Name => PolicyName;

    public bool UsesCostLedger => true;

    public RunwayBurnEstimate Estimate(RunwayEstimateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Role is not { Length: > 0 } role || context.LedgerRows.Count == 0)
        {
            return new RunwayBurnEstimate(FlatDefaultPoints, RunwayEstimateSources.FlatDefault);
        }

        // Billed tokens, not the dollar estimate: a row is priced only when its tokens could be
        // attributed to one model (CostLedgerStore.Estimate), so pricing drops rows this ratio can
        // legitimately use. Rows with no billed figure carry no burn to compare and are absent here
        // rather than counted as zero.
        var burnByRole = new List<double>();
        var burnEverywhere = new List<double>();
        foreach (var row in context.LedgerRows)
        {
            if (row.BilledTokens is not { } billed || billed <= 0)
            {
                continue;
            }

            burnEverywhere.Add(billed);
            if (string.Equals(row.Role, role, StringComparison.OrdinalIgnoreCase))
            {
                burnByRole.Add(billed);
            }
        }

        if (burnByRole.Count < MinimumLedgerRows || Median(burnEverywhere) is not { } fleetMedian || fleetMedian <= 0)
        {
            return new RunwayBurnEstimate(FlatDefaultPoints, RunwayEstimateSources.FlatDefault);
        }

        var ratio = Median(burnByRole)!.Value / fleetMedian;
        var points = FlatDefaultPoints * ratio;

        // A non-finite or non-positive ratio is not evidence of anything; fall back rather than
        // reserving zero, which would silently disable the arm for that role.
        return double.IsFinite(points) && points > 0
            ? new RunwayBurnEstimate(points, RunwayEstimateSources.LedgerMedian)
            : new RunwayBurnEstimate(FlatDefaultPoints, RunwayEstimateSources.FlatDefault);
    }

    private static double? Median(List<double> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        values.Sort();
        var middle = values.Count / 2;
        return values.Count % 2 == 1 ? values[middle] : (values[middle - 1] + values[middle]) / 2;
    }
}

/// <summary>The anchor alone — <see cref="LedgerMedianRunwayReservationPolicy.FlatDefaultPoints"/> for every
/// role, with no ledger read. For a fleet that wants the reservation arm without the ledger-derived scaling.</summary>
public sealed class FlatRunwayReservationPolicy : IRunwayReservationPolicy
{
    public const string PolicyName = "flat";

    public string Name => PolicyName;

    public bool UsesCostLedger => false;

    public RunwayBurnEstimate Estimate(RunwayEstimateContext context) =>
        new(LedgerMedianRunwayReservationPolicy.FlatDefaultPoints, RunwayEstimateSources.FlatDefault);
}

/// <summary>
/// Reservations off: every estimate is zero, so the reservation arm never holds anything. <b>The
/// recording half still runs</b> — that is the point of having this as a policy rather than a
/// feature flag, and it is what makes "capture first, decide later" available to a fleet that does not
/// want the arithmetic enforcing yet.
/// </summary>
public sealed class NoReservationRunwayReservationPolicy : IRunwayReservationPolicy
{
    public const string PolicyName = "off";

    public string Name => PolicyName;

    public bool UsesCostLedger => false;

    public RunwayBurnEstimate Estimate(RunwayEstimateContext context) => new(0, RunwayEstimateSources.Off);
}

/// <summary>The one place a <c>settings.json</c> policy token becomes a policy object.</summary>
public static class RunwayReservationPolicies
{
    /// <summary>
    /// The policy named by <paramref name="name"/>. <b>An unrecognised or absent name resolves to the
    /// shipped default</b> rather than throwing or disabling the arm — the same posture
    /// <c>RunwayHoldSettings</c> already takes for an out-of-range threshold, and for the same reason: an
    /// operator typo must not silently change what the gate does.
    /// </summary>
    public static IRunwayReservationPolicy Resolve(string? name) =>
        name?.Trim().ToLowerInvariant() switch
        {
            FlatRunwayReservationPolicy.PolicyName => new FlatRunwayReservationPolicy(),
            NoReservationRunwayReservationPolicy.PolicyName => new NoReservationRunwayReservationPolicy(),
            _ => new LedgerMedianRunwayReservationPolicy(),
        };
}
