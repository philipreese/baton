using Baton.Queue;

namespace Baton.Tests.Queue;

/// <summary>
/// #1934 slice 1, item 6: every scheduler arm, with a fake clock and a fake memory reading. The
/// policy is a pure function precisely so this file needs no daemon, no room and no process.
/// </summary>
/// <remarks>
/// Each gate is asserted in BOTH polarities where two behaviours are one condition apart — an item
/// that waits and the same item that launches once the one input changes. A test that only ever
/// asserts the wait cannot tell "the gate works" from "nothing ever launches".
/// </remarks>
public sealed class QueueSchedulerTests
{
    private static readonly QueueSettings Defaults = new();

    private static QueueItem Item(
        string tag = "t1", string role = "implement", string? adapter = null, QueueItemState state = QueueItemState.Queued,
        bool external = false) =>
        new()
        {
            Tag = tag,
            Role = role,
            Adapter = adapter,
            Workspace = @"C:\repos\w1",
            SpecFile = @"C:\baton\queue\specs\t1.md",
            State = state,
            External = external,
        };

    /// <summary>A local instant with a chosen wall-clock hour, built as a DateTimeOffset carrying THIS
    /// machine's offset — which is what makes the hour-band arms below discriminate: computed in UTC
    /// they would read a different hour on any host not at UTC+0.</summary>
    private static DateTimeOffset LocalAt(int hour, int minute = 0)
    {
        var local = new DateTime(2026, 9, 5, hour, minute, 0, DateTimeKind.Local);
        return new DateTimeOffset(local);
    }

    [Fact]
    public void An_empty_queue_waits_with_no_items()
    {
        var decision = QueueScheduler.Decide(LocalAt(12), [], 0, 8.0, Defaults, null, held: false);

        Assert.Equal(QueueDecisionKind.Wait, decision.Kind);
        Assert.Equal(QueueWaitReason.NoItems, decision.WaitReason);
        Assert.Null(decision.Item);
    }

    [Fact]
    public void A_ready_work_item_is_never_the_candidate_however_launchable_it_looks()
    {
        // The item as WorkItemAdvancer leaves an approved one: still QUEUED, because the conductor has
        // yet to merge it. The control arm is the identical item one stage earlier — so this measures
        // the READY stage and not, say, the presence of a stage at all.
        var ready = Item() with { Stage = WorkStage.Ready };
        var notReady = Item() with { Stage = WorkStage.Review };

        var readyDecision = QueueScheduler.Decide(LocalAt(12), [ready], 0, 8.0, Defaults, null, held: false);
        var reviewDecision = QueueScheduler.Decide(LocalAt(12), [notReady], 0, 8.0, Defaults, null, held: false);

        Assert.Equal(QueueDecisionKind.Wait, readyDecision.Kind);
        Assert.Equal(QueueWaitReason.NoItems, readyDecision.WaitReason);
        Assert.Equal(QueueDecisionKind.Launch, reviewDecision.Kind);
    }

    [Fact]
    public void A_stage_less_dispatch_request_is_unchanged_by_the_ready_guard()
    {
        var decision = QueueScheduler.Decide(LocalAt(12), [Item()], 0, 8.0, Defaults, null, held: false);

        Assert.Equal(QueueDecisionKind.Launch, decision.Kind);
        Assert.Null(decision.Item!.Stage);
    }

    [Fact]
    public void A_held_queue_waits_on_hold_even_with_a_launchable_item()
    {
        var items = new[] { Item() };

        var launchable = QueueScheduler.Decide(LocalAt(12), items, 0, 8.0, Defaults, null, held: false);
        var heldDecision = QueueScheduler.Decide(LocalAt(12), items, 0, 8.0, Defaults, null, held: true);

        // The control arm: the identical inputs minus the hold DO launch, so the hold is what the
        // second arm is measuring and not some other closed gate.
        Assert.Equal(QueueDecisionKind.Launch, launchable.Kind);
        Assert.Equal(QueueWaitReason.Hold, heldDecision.WaitReason);
    }

    [Fact]
    public void Only_queued_non_external_items_are_candidates()
    {
        var items = new[]
        {
            Item("done", state: QueueItemState.Done),
            Item("running", state: QueueItemState.Launched),
            Item("outside", external: true),
            Item("mine"),
        };

        var decision = QueueScheduler.Decide(LocalAt(12), items, 0, 8.0, Defaults, null, held: false);

        Assert.Equal(QueueDecisionKind.Launch, decision.Kind);
        Assert.Equal("mine", decision.Item!.Tag);
    }

    [Fact]
    public void An_external_only_queue_waits_with_no_items_rather_than_launching_one()
    {
        var decision = QueueScheduler.Decide(
            LocalAt(12), [Item("outside", external: true)], 0, 8.0, Defaults, null, held: false);

        Assert.Equal(QueueWaitReason.NoItems, decision.WaitReason);
    }

    [Fact]
    public void The_gap_blocks_a_launch_until_it_has_elapsed()
    {
        var now = LocalAt(12);
        var justLaunched = now - TimeSpan.FromSeconds(Defaults.EffectiveGapSeconds - 1);
        var longEnoughAgo = now - TimeSpan.FromSeconds(Defaults.EffectiveGapSeconds);

        var blocked = QueueScheduler.Decide(now, [Item()], 0, 8.0, Defaults, justLaunched, held: false);
        var admitted = QueueScheduler.Decide(now, [Item()], 0, 8.0, Defaults, longEnoughAgo, held: false);

        Assert.Equal(QueueWaitReason.Gap, blocked.WaitReason);
        Assert.Equal(QueueDecisionKind.Launch, admitted.Kind);
    }

    [Theory]
    // The band boundary in BOTH directions, in local wall clock. 19:59 is day (2.0 GiB floor), 20:00 is
    // night (1.2). A free reading of 1.5 GiB is above the night floor and below the day one, so these
    // two arms differ ONLY by which band the hour lands in — which is what makes them fail if the band
    // were computed in UTC on any host with a non-zero offset.
    [InlineData(19, 59, QueueDecisionKind.Wait)]
    [InlineData(20, 0, QueueDecisionKind.Launch)]
    [InlineData(8, 59, QueueDecisionKind.Launch)]
    [InlineData(9, 0, QueueDecisionKind.Wait)]
    public void The_memory_floor_changes_at_the_local_hour_band_boundary(int hour, int minute, QueueDecisionKind expected)
    {
        var decision = QueueScheduler.Decide(LocalAt(hour, minute), [Item()], 0, freeGb: 1.5, Defaults, null, held: false);

        Assert.Equal(expected, decision.Kind);
        if (expected == QueueDecisionKind.Wait)
        {
            Assert.Equal(QueueWaitReason.Memory, decision.WaitReason);
        }
    }

    [Fact]
    public void An_unmeasured_memory_reading_does_not_block_and_is_carried_through_absent()
    {
        // Below the day floor if it were a reading; null must not be read as zero.
        var decision = QueueScheduler.Decide(LocalAt(12), [Item()], 0, freeGb: null, Defaults, null, held: false);

        Assert.Equal(QueueDecisionKind.Launch, decision.Kind);
        Assert.Null(decision.FreeGb);
    }

    [Fact]
    public void The_weighted_cap_blocks_an_implement_lane_at_the_ceiling_and_admits_it_below()
    {
        var atCeiling = QueueScheduler.Decide(LocalAt(12), [Item()], liveWeight: 4.0, 8.0, Defaults, null, held: false);
        var belowCeiling = QueueScheduler.Decide(LocalAt(12), [Item()], liveWeight: 3.0, 8.0, Defaults, null, held: false);

        Assert.Equal(QueueWaitReason.Slots, atCeiling.WaitReason);
        Assert.Equal(QueueDecisionKind.Launch, belowCeiling.Kind);
    }

    [Fact]
    public void A_codex_lane_weighs_half_so_it_fits_where_an_implement_lane_does_not()
    {
        // Live weight 3.5: + 1.0 exceeds the 4.0 cap, + 0.5 lands exactly on it (and the cap is a
        // ceiling, not a strict bound). The two arms differ only by the candidate's adapter, which is
        // the weight rule under test.
        var claudeLane = QueueScheduler.Decide(LocalAt(12), [Item()], 3.5, 8.0, Defaults, null, held: false);
        var codexLane = QueueScheduler.Decide(
            LocalAt(12), [Item(adapter: "codex")], 3.5, 8.0, Defaults, null, held: false);

        Assert.Equal(QueueWaitReason.Slots, claudeLane.WaitReason);
        Assert.Equal(QueueDecisionKind.Launch, codexLane.Kind);
    }

    [Fact]
    public void A_review_lane_bypasses_the_cap_and_the_floor_where_an_implement_lane_does_not()
    {
        // Both gates closed at once: the fleet is over the cap AND under the day floor.
        var implement = QueueScheduler.Decide(LocalAt(12), [Item()], liveWeight: 9.0, freeGb: 0.1, Defaults, null, held: false);
        var review = QueueScheduler.Decide(
            LocalAt(12), [Item(role: "review")], liveWeight: 9.0, freeGb: 0.1, Defaults, null, held: false);

        Assert.Equal(QueueDecisionKind.Wait, implement.Kind);
        Assert.Equal(QueueDecisionKind.Launch, review.Kind);
    }

    [Fact]
    public void A_review_lane_still_honours_the_hold_and_the_gap()
    {
        var now = LocalAt(12);
        var items = new[] { Item(role: "review") };

        Assert.Equal(
            QueueWaitReason.Hold,
            QueueScheduler.Decide(now, items, 0, 8.0, Defaults, null, held: true).WaitReason);
        Assert.Equal(
            QueueWaitReason.Gap,
            QueueScheduler.Decide(now, items, 0, 8.0, Defaults, now - TimeSpan.FromSeconds(1), held: false).WaitReason);
    }

    [Fact]
    public void The_hold_is_reported_ahead_of_every_other_closed_gate()
    {
        // Four gates shut at once — held, over the cap, under the floor, inside the gap. This is the
        // arm that pins the ORDER rather than any one gate; it goes red if the ifs are rearranged.
        var decision = QueueScheduler.Decide(
            LocalAt(12), [Item()], liveWeight: 99, freeGb: 0.0, Defaults, LocalAt(12), held: true);

        Assert.Equal(QueueWaitReason.Hold, decision.WaitReason);
    }

    [Fact]
    public void Every_decision_carries_the_counters_it_was_made_against()
    {
        var decision = QueueScheduler.Decide(LocalAt(21), [Item()], liveWeight: 2.5, freeGb: 3.25, Defaults, null, held: false);

        Assert.Equal(2.5, decision.LiveWeight);
        Assert.Equal(3.25, decision.FreeGb);
        Assert.Equal(QueueSettings.DefaultFloorGbNight, decision.FloorGb);
    }

    [Fact]
    public void Out_of_range_settings_fall_back_to_the_shipped_defaults_rather_than_being_honoured()
    {
        // Three plausible typos, each dangerous in a different direction (spec/baton.md §13). The
        // assertions read the Effective* properties because those are the only accessors the
        // scheduler uses; a raw-field read would report the typo back unchanged.
        var typo = new QueueSettings { MaxLiveWeight = 0, GapSeconds = -5, NightStartHour = 99 };

        Assert.Equal(QueueSettings.DefaultMaxLiveWeight, typo.EffectiveMaxLiveWeight);
        Assert.Equal(QueueSettings.DefaultGapSeconds, typo.EffectiveGapSeconds);
        Assert.Equal(QueueSettings.DefaultNightStartHour, typo.EffectiveNightStartHour);
    }

    [Fact]
    public void A_non_wrapping_band_is_the_plain_interval_not_the_wrap_around_one()
    {
        // nightStart 2, dayStart 6: 03:00 is night, 12:00 is day. Under the wrap-around comparison
        // alone (hour >= 2 || hour < 6) noon would read as night.
        var settings = new QueueSettings { NightStartHour = 2, DayStartHour = 6 };

        Assert.True(settings.IsNightBand(new DateTime(2026, 9, 5, 3, 0, 0, DateTimeKind.Local)));
        Assert.False(settings.IsNightBand(new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Local)));
    }
}
