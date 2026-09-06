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

    /// <summary>The store decides a whole dispatch at once; every arm below but
    /// <see cref="A_sibling_vendors_hold_stops_the_admitted_vendor_reserving_anything"/> is about a
    /// one-vendor dispatch, so it unwraps here rather than at fourteen call sites.</summary>
    private static RunwayAdmissionEntry Decide(
        RunwayAdmissionRequest request, IReadOnlyList<RunwayAdmissionEntry> existingRows) =>
        RunwayAdmissionLedgerStore.Decide([request], existingRows).Single();

    private static RunwayAdmissionRequest Request(
        string vendor = "claude",
        double estimatePoints = 1.0,
        double? headroomPoints = 1.0,
        bool gateHeld = false,
        bool unmeasured = false,
        string? overrideReason = null,
        DateTimeOffset? snapshotHarvestedAt = null,
        DateTimeOffset? at = null) =>
        new(Vendor: vendor,
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
        Decide(Request(estimatePoints: points, at: at), []);

    [Fact]
    public void An_empty_ledger_admits_a_dispatch_that_fits_the_headroom()
    {
        var entry = Decide(Request(estimatePoints: 1.0, headroomPoints: 1.0), []);

        Assert.Equal(RunwayAdmissionDecisions.Admitted, entry.Decision);
        Assert.Equal(RunwayAdmissionDecidedBy.Counters, entry.DecidedBy);
        Assert.Equal(0, entry.OutstandingReservationPoints);
    }

    [Fact]
    public void A_second_dispatch_against_the_same_snapshot_is_held_by_the_reservation_arm()
    {
        var existing = new[] { AdmittedRow(1.0, HarvestedAt.AddMinutes(1)) };

        var entry = Decide(Request(estimatePoints: 1.0, headroomPoints: 1.0), existing);

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

        var entry = Decide(Request(estimatePoints: 1.0, headroomPoints: 1.0), existing);

        Assert.Equal(RunwayAdmissionDecisions.Admitted, entry.Decision);
        Assert.Equal(0, entry.OutstandingReservationPoints);
    }

    /// <summary>A refused dispatch never ran, so its estimate must not go on reserving headroom against
    /// the dispatches that follow it.</summary>
    [Fact]
    public void A_held_row_reserves_nothing_while_an_overridden_one_does()
    {
        var held = Decide(Request(estimatePoints: 5.0, headroomPoints: 1.0, at: HarvestedAt.AddMinutes(1)), []);
        Assert.Equal(RunwayAdmissionDecisions.Held, held.Decision);

        var afterHeld = Decide(Request(estimatePoints: 1.0, headroomPoints: 1.0), [held]);
        Assert.Equal(RunwayAdmissionDecisions.Admitted, afterHeld.Decision);
        Assert.Equal(0, afterHeld.OutstandingReservationPoints);

        var overridden = Decide(
            Request(estimatePoints: 5.0, headroomPoints: 1.0, overrideReason: "conductor lane", at: HarvestedAt.AddMinutes(1)),
            []);
        Assert.Equal(RunwayAdmissionDecisions.HeldOverridden, overridden.Decision);

        var afterOverride = Decide(Request(estimatePoints: 1.0, headroomPoints: 1.0), [overridden]);
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

        var entry = Decide(Request(estimatePoints: 1000, gateHeld: gateHeld, unmeasured: unmeasured), existing);

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

        var entry = Decide(Request(headroomPoints: null), existing);

        Assert.Equal(RunwayAdmissionDecisions.Admitted, entry.Decision);
        Assert.Null(entry.OutstandingReservationPoints);
    }

    /// <summary>
    /// Two vendors, one held: the whole dispatch is refused, so the admitted vendor's row must say the
    /// work did not happen and must reserve nothing afterwards — the phantom reservation spec/baton.md §7
    /// names. Both halves are asserted; the polarity arm below is the same pairing with no sibling hold.
    /// </summary>
    [Fact]
    public void A_sibling_vendors_hold_stops_the_admitted_vendor_reserving_anything()
    {
        var refused = RunwayAdmissionLedgerStore.Decide(
            [
                Request(vendor: "claude", estimatePoints: 1.0, headroomPoints: 40.0),
                Request(vendor: "agy", gateHeld: true),
            ],
            []);

        var admitted = refused.Single(e => e.Vendor == "claude");
        Assert.Equal(RunwayAdmissionDecisions.Admitted, admitted.Decision);
        Assert.False(admitted.Dispatched);

        // ... and it reserves nothing against the next dispatch on its own vendor.
        var next = Decide(Request(vendor: "claude", estimatePoints: 1.0, headroomPoints: 1.0), refused);
        Assert.Equal(RunwayAdmissionDecisions.Admitted, next.Decision);
        Assert.Equal(0, next.OutstandingReservationPoints);
    }

    /// <summary>The control arm for the pairing above: same two vendors, neither held, so the dispatch
    /// proceeded and the admitted row reserves as usual. Without this, the assertions above would also
    /// pass against an implementation that never reserved anything at all.</summary>
    [Fact]
    public void Two_vendors_that_both_admit_leave_the_dispatch_marked_as_run()
    {
        var proceeded = RunwayAdmissionLedgerStore.Decide(
            [
                Request(vendor: "claude", estimatePoints: 1.0, headroomPoints: 40.0),
                Request(vendor: "agy", estimatePoints: 1.0, headroomPoints: 40.0),
            ],
            []);

        Assert.All(proceeded, e => Assert.Null(e.Dispatched));

        var next = Decide(Request(vendor: "claude", estimatePoints: 1.0, headroomPoints: 1.0), proceeded);
        Assert.Equal(RunwayAdmissionDecisions.Held, next.Decision);
        Assert.Equal(1.0, next.OutstandingReservationPoints);
    }

    /// <summary>
    /// The race the issue was filed for, driven through the real lock. What it pins is that both writers'
    /// rows land and the arithmetic they land with is consistent — exactly one sees an empty ledger. Note
    /// what it does <b>not</b> do: the critical section is one small read plus one append, so two tasks
    /// will often serialize by luck, and this arm would frequently pass against a read-then-append
    /// implementation too. The rule itself is pinned by the pure arms above; this one is here because a
    /// lock that deadlocks or double-writes under real contention would show up nowhere else.
    /// </summary>
    [Fact]
    public async Task Two_concurrent_writers_never_both_see_an_empty_ledger()
    {
        var path = Path.Combine(Path.GetTempPath(), $"runway-ledger-{Guid.NewGuid():N}", "runway-admissions.jsonl");
        try
        {
            var both = (await Task.WhenAll(
                Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
                    RunwayAdmissionLedgerStore.ReserveAndRecordAsync(
                        [Request(estimatePoints: 1.0, headroomPoints: 1.0)], path, TestContext.Current.CancellationToken)))))
                .SelectMany(entries => entries)
                .ToList();

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
