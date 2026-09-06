using Baton.Accounting;
using Baton.Runway;

namespace Baton.Tests.Runway;

/// <summary>
/// #1896's pluggable half: which arm of the shipped default produces a burn estimate, what the row
/// threshold buys, and that the <c>settings.json</c> token resolves to a policy rather than throwing.
/// The arithmetic that CONSUMES an estimate is pinned in <see cref="RunwayAdmissionLedgerStoreTests"/>;
/// the dispatch wiring in <c>Baton.Cli.Tests.RunwayReservationDispatchTests</c>.
/// </summary>
public sealed class RunwayReservationPolicyTests
{
    private static CostLedgerEntry Row(string role, long billedTokens) =>
        new(SourceKind: CostSourceKind.BatonExecution,
            Execution: Guid.NewGuid().ToString(),
            Role: role,
            BilledTokens: billedTokens);

    private static IReadOnlyList<CostLedgerEntry> Rows(string role, long billedTokens, int count) =>
        Enumerable.Range(0, count).Select(_ => Row(role, billedTokens)).ToList();

    [Fact]
    public void The_flat_default_stands_while_the_ledger_is_empty()
    {
        var estimate = new LedgerMedianRunwayReservationPolicy()
            .Estimate(new RunwayEstimateContext("claude", "implement", []));

        Assert.Equal(LedgerMedianRunwayReservationPolicy.FlatDefaultPoints, estimate.Points);
        Assert.Equal(RunwayEstimateSources.FlatDefault, estimate.Source);
    }

    /// <summary>
    /// One below the threshold is still the flat default — a median over too few rows is a rumour, and
    /// the row that separates the two arms is the one the constant names.
    /// </summary>
    [Fact]
    public void One_row_short_of_the_threshold_is_still_the_flat_default()
    {
        var rows = Rows("implement", billedTokens: 400_000, count: LedgerMedianRunwayReservationPolicy.MinimumLedgerRows - 1)
            .Concat(Rows("review", billedTokens: 100_000, count: 20))
            .ToList();

        var estimate = new LedgerMedianRunwayReservationPolicy()
            .Estimate(new RunwayEstimateContext("claude", "implement", rows));

        Assert.Equal(LedgerMedianRunwayReservationPolicy.FlatDefaultPoints, estimate.Points);
        Assert.Equal(RunwayEstimateSources.FlatDefault, estimate.Source);
    }

    /// <summary>
    /// The polarity arm of the one above: at the threshold the median takes over, and a role that burns
    /// more than a typical execution is estimated ABOVE the anchor rather than at it.
    /// </summary>
    [Fact]
    public void At_the_threshold_a_heavier_role_is_estimated_above_the_flat_default()
    {
        var rows = Rows("implement", billedTokens: 400_000, count: LedgerMedianRunwayReservationPolicy.MinimumLedgerRows)
            .Concat(Rows("review", billedTokens: 100_000, count: 30))
            .ToList();

        var estimate = new LedgerMedianRunwayReservationPolicy()
            .Estimate(new RunwayEstimateContext("claude", "implement", rows));

        Assert.Equal(RunwayEstimateSources.LedgerMedian, estimate.Source);
        Assert.True(
            estimate.Points > LedgerMedianRunwayReservationPolicy.FlatDefaultPoints,
            $"a role burning 4x the fleet median should estimate above the anchor, got {estimate.Points}");
    }

    /// <summary>A dispatch whose role cannot be attributed borrows nothing from another role's median.</summary>
    [Fact]
    public void A_dispatch_with_no_resolvable_role_falls_back_to_the_flat_default()
    {
        var estimate = new LedgerMedianRunwayReservationPolicy()
            .Estimate(new RunwayEstimateContext("claude", Role: null, Rows("implement", 400_000, 50)));

        Assert.Equal(RunwayEstimateSources.FlatDefault, estimate.Source);
    }

    [Theory]
    [InlineData(null, LedgerMedianRunwayReservationPolicy.PolicyName)]
    [InlineData("", LedgerMedianRunwayReservationPolicy.PolicyName)]
    [InlineData("nonsense", LedgerMedianRunwayReservationPolicy.PolicyName)]
    [InlineData("Flat", FlatRunwayReservationPolicy.PolicyName)]
    [InlineData(" off ", NoReservationRunwayReservationPolicy.PolicyName)]
    public void An_unrecognised_settings_token_resolves_to_the_shipped_default(string? token, string expected) =>
        Assert.Equal(expected, RunwayReservationPolicies.Resolve(token).Name);

    /// <summary>Reservations off still estimates — zero — so the recording half keeps running.</summary>
    [Fact]
    public void The_off_policy_estimates_zero_rather_than_refusing_to_estimate()
    {
        var estimate = new NoReservationRunwayReservationPolicy()
            .Estimate(new RunwayEstimateContext("claude", "implement", []));

        Assert.Equal(0, estimate.Points);
        Assert.Equal(RunwayEstimateSources.Off, estimate.Source);
    }
}
