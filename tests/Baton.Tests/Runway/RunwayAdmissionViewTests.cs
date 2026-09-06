using Baton.Runway;

namespace Baton.Tests.Runway;

/// <summary>
/// #1932 review: <c>baton status</c> and <c>fleet_status</c> both showed ONE arbitrary binding's runway
/// record, dictionary-order dependent, which is wrong in exactly the case #1896's batch decision exists
/// for — a composed template whose vendors were decided differently. Both surfaces now project through
/// <see cref="RunwayAdmissionView.AllFrom"/>, so this is the one place that behaviour is pinned.
/// </summary>
public class RunwayAdmissionViewTests
{
    private static RunwayAdmission Admission(string vendor, string decision, string? unrecorded = null) =>
        new(vendor, decision, RunwayAdmissionDecidedBy.Counters, UnrecordedReason: unrecorded);

    [Fact]
    public void Every_vendors_record_is_projected_in_vendor_order()
    {
        var view = RunwayAdmissionView.AllFrom(
        [
            Admission("claude", RunwayAdmissionDecisions.Admitted),
            Admission("agy", RunwayAdmissionDecisions.HeldOverridden),
        ]);

        Assert.NotNull(view);
        Assert.Equal(["agy", "claude"], view.Select(a => a.Vendor));
        Assert.Equal(RunwayAdmissionDecisions.HeldOverridden, view[0].Decision);
        Assert.Equal(RunwayAdmissionDecisions.Admitted, view[1].Decision);
    }

    /// <summary>
    /// The dedupe <see cref="RunwayAdmissionView.AllFrom"/>'s remarks describe: duplicated input rows
    /// collapse to one. Paired with the arm above, this is what makes the list one-per-vendor rather than
    /// one-per-binding.
    /// </summary>
    [Fact]
    public void Several_workers_bound_to_one_vendor_yield_one_row()
    {
        var view = RunwayAdmissionView.AllFrom(
        [
            Admission("claude", RunwayAdmissionDecisions.Admitted),
            Admission("claude", RunwayAdmissionDecisions.Admitted),
            Admission("claude", RunwayAdmissionDecisions.Admitted),
        ]);

        Assert.Equal("claude", Assert.Single(view!).Vendor);
    }

    /// <summary>
    /// Absent, never an empty list: every room dispatched before #1896 carries no record, and the wire
    /// contract both views state is that a consumer tests presence rather than length.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_room_with_no_record_projects_to_null(bool nullInput) =>
        Assert.Null(RunwayAdmissionView.AllFrom(nullInput ? null : [null, null]));

    /// <summary>
    /// The fail-open ledger marker rides the same projection — it is the only thing on the room saying an
    /// admission proceeded with no row behind it (<c>DispatchCommand.RecordAdmissionsAsync</c>).
    /// </summary>
    [Fact]
    public void The_unrecorded_reason_reaches_the_wire()
    {
        var view = RunwayAdmissionView.AllFrom([Admission("claude", RunwayAdmissionDecisions.Admitted, "disk full")]);

        Assert.Equal("disk full", Assert.Single(view!).UnrecordedReason);
    }
}
