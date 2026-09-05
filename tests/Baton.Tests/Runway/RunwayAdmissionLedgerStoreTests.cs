using Baton.Runway;
using Baton.Tests.Shared;

namespace Baton.Tests.Runway;

/// <summary>
/// #1896's reservation arithmetic, driven directly against a row list so each arm is about the rule
/// rather than about a file lock. The one arm that must go through the lock — read, decide and append as
/// one critical section — is <see cref="Two_concurrent_writers_never_both_see_an_empty_ledger"/>.
/// </summary>
public sealed class RunwayAdmissionLedgerStoreTests
{
    private static readonly DateTimeOffset HarvestedAt = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    private static RunwayAdmissionRequest Request(
        double estimatePoints = 1.0,
        double? headroomPoints = 1.0,
        bool gateHeld = false,
        bool unmeasured = false,
        string? overrideReason = null,
        DateTimeOffset? snapshotHarvestedAt = null,
        DateTimeOffset? at = null) =>
        new(Vendor: "claude",
            GateHeld: gateHeld,
            Unmeasured: unmeasured,
            GateReason: gateHeld ? "'week (all models)' is at 87% (holds at 85%)" : null,
            Counters: [new RunwayCounter("week (all models)", 84), new RunwayCounter("session", 12)],
            WeekHoldPct: 85,
            SessionHoldPct: 90,
            MaxSnapshotAgeHours: 6,
            SnapshotHarvestedAt: snapshotHarvestedAt ?? HarvestedAt,
            HeadroomPoints: headroomPoints,
            Estimate: new RunwayBurnEstimate(estimatePoints, "test"),
            Room: @"C:\rooms\one",
            Role: "implement",
            OverrideReason: overrideReason,
            At: at ?? HarvestedAt.AddMinutes(1));

    private static RunwayAdmissionEntry AdmittedRow(double points, DateTimeOffset at) =>
        RunwayAdmissionLedgerStore.Decide(Request(estimatePoints: points, at: at), []);

    [Fact]
    public void An_empty_ledger_admits_a_dispatch_that_fits_the_headroom()
    {
        var entry = RunwayAdmissionLedgerStore.Decide(Request(estimatePoints: 1.0, headroomPoints: 1.0), []);

        Assert.Equal(RunwayAdmissionDecisions.Admitted, entry.Decision);
        Assert.Equal(RunwayAdmissionDecidedBy.Counters, entry.DecidedBy);
        Assert.Equal(0, entry.OutstandingReservationPoints);
    }

    [Fact]
    public void A_second_dispatch_against_the_same_snapshot_is_held_by_the_reservation_arm()
    {
        var existing = new[] { AdmittedRow(1.0, HarvestedAt.AddMinutes(1)) };

        var entry = RunwayAdmissionLedgerStore.Decide(Request(estimatePoints: 1.0, headroomPoints: 1.0), existing);

        Assert.Equal(RunwayAdmissionDecisions.Held, entry.Decision);
        Assert.Equal(RunwayAdmissionDecidedBy.Reservation, entry.DecidedBy);
        Assert.Equal(1.0, entry.OutstandingReservationPoints);
        Assert.Contains("reserved by dispatches", entry.Reason!, StringComparison.Ordinal);
    }

    /// <summary>The reconciliation rule: a row recorded before the snapshot being decided against is
    /// already in the counters, so it is no longer outstanding.</summary>
    [Fact]
    public void A_row_older_than_the_current_snapshot_is_no_longer_outstanding()
    {
        var existing = new[] { AdmittedRow(1.0, HarvestedAt.AddMinutes(-30)) };

        var entry = RunwayAdmissionLedgerStore.Decide(Request(estimatePoints: 1.0, headroomPoints: 1.0), existing);

        Assert.Equal(RunwayAdmissionDecisions.Admitted, entry.Decision);
        Assert.Equal(0, entry.OutstandingReservationPoints);
    }

    /// <summary>A refused dispatch never ran, so its estimate must not go on reserving headroom against
    /// the dispatches that follow it.</summary>
    [Fact]
    public void A_held_row_reserves_nothing_while_an_overridden_one_does()
    {
        var held = RunwayAdmissionLedgerStore.Decide(
            Request(estimatePoints: 5.0, headroomPoints: 1.0, at: HarvestedAt.AddMinutes(1)), []);
        Assert.Equal(RunwayAdmissionDecisions.Held, held.Decision);

        var afterHeld = RunwayAdmissionLedgerStore.Decide(Request(estimatePoints: 1.0, headroomPoints: 1.0), [held]);
        Assert.Equal(RunwayAdmissionDecisions.Admitted, afterHeld.Decision);
        Assert.Equal(0, afterHeld.OutstandingReservationPoints);

        var overridden = RunwayAdmissionLedgerStore.Decide(
            Request(estimatePoints: 5.0, headroomPoints: 1.0, overrideReason: "conductor lane", at: HarvestedAt.AddMinutes(1)),
            []);
        Assert.Equal(RunwayAdmissionDecisions.HeldOverridden, overridden.Decision);

        var afterOverride = RunwayAdmissionLedgerStore.Decide(
            Request(estimatePoints: 1.0, headroomPoints: 1.0), [overridden]);
        Assert.Equal(RunwayAdmissionDecisions.Held, afterOverride.Decision);
        Assert.Equal(5.0, afterOverride.OutstandingReservationPoints);
    }

    /// <summary>#1923's guard, stated on <c>ReserveAndRecordAsync</c>: an unmeasured vendor and a
    /// counters-hold both pass through untouched, even with an estimate far past any headroom.</summary>
    [Theory]
    [InlineData(true, false, RunwayAdmissionDecisions.Unmeasured, RunwayAdmissionDecidedBy.Unmeasured)]
    [InlineData(false, true, RunwayAdmissionDecisions.Held, RunwayAdmissionDecidedBy.Counters)]
    public void The_reservation_arm_never_reaches_an_unmeasured_vendor_or_a_counters_hold(
        bool unmeasured, bool gateHeld, string expectedDecision, string expectedDecidedBy)
    {
        var existing = new[] { AdmittedRow(1000, HarvestedAt.AddMinutes(1)) };

        var entry = RunwayAdmissionLedgerStore.Decide(
            Request(estimatePoints: 1000, gateHeld: gateHeld, unmeasured: unmeasured), existing);

        Assert.Equal(expectedDecision, entry.Decision);
        Assert.Equal(expectedDecidedBy, entry.DecidedBy);
        Assert.Null(entry.OutstandingReservationPoints);
    }

    /// <summary>A gate that admitted but reported no headroom (no snapshot instant to reconcile against)
    /// leaves the arithmetic unrun rather than guessing at a headroom of zero, which would hold everything.</summary>
    [Fact]
    public void An_admit_with_no_headroom_reading_runs_no_reservation_arithmetic()
    {
        var existing = new[] { AdmittedRow(1000, HarvestedAt.AddMinutes(1)) };

        var entry = RunwayAdmissionLedgerStore.Decide(Request(headroomPoints: null), existing);

        Assert.Equal(RunwayAdmissionDecisions.Admitted, entry.Decision);
        Assert.Null(entry.OutstandingReservationPoints);
    }

    /// <summary>
    /// The race the issue was filed for. Reserving and recording share ONE
    /// <c>MutexGuardedFileLock</c> acquisition, so of two writers starting against an empty ledger at the
    /// same instant exactly one can see nothing outstanding. Two lock acquisitions — read then append —
    /// would let both admit, which is the shape this arm discriminates against.
    /// </summary>
    [Fact]
    public async Task Two_concurrent_writers_never_both_see_an_empty_ledger()
    {
        var path = Path.Combine(Path.GetTempPath(), $"runway-ledger-{Guid.NewGuid():N}", "runway-admissions.jsonl");
        try
        {
            var both = await Task.WhenAll(
                Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
                    RunwayAdmissionLedgerStore.ReserveAndRecordAsync(
                        Request(estimatePoints: 1.0, headroomPoints: 1.0), path, TestContext.Current.CancellationToken))));

            Assert.Equal(1, both.Count(e => e.Decision == RunwayAdmissionDecisions.Admitted));
            Assert.Equal(1, both.Count(e => e.DecidedBy == RunwayAdmissionDecidedBy.Reservation));

            var rows = await RunwayAdmissionLedgerStore.ReadAllAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(2, rows.Count);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(Path.GetDirectoryName(path)!);
        }
    }
}
