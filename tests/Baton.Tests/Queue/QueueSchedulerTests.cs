using Baton.Domain;
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
    public void Active_lifecycles_hold_a_new_start_after_processes_settle()
    {
        var active = Enumerable.Range(1, 4)
            .Select(n => Item($"active-{n}") with { Stage = WorkStage.Review, Round = 1, State = QueueItemState.Done })
            .ToArray();
        var next = Item("new") with { Stage = WorkStage.Implement };

        var decision = QueueScheduler.Decide(LocalAt(12), [.. active, next], 0, 8.0, Defaults, null, held: false);

        Assert.Equal(QueueWaitReason.LifecycleCap, decision.WaitReason);
        Assert.Equal("new", decision.Item!.Tag);
    }

    [Fact]
    public void Existing_fix_passes_a_new_work_head_while_pre_pr_capacity_is_full()
    {
        var active = new[]
        {
            Item("active-1") with { Stage = WorkStage.Review, Round = 1, State = QueueItemState.Done },
            Item("active-2") with { Stage = WorkStage.Review, Round = 1, State = QueueItemState.Done },
        };
        var fix = Item("fix") with { Stage = WorkStage.Fix, Round = 1 };
        var start = Item("start") with { Stage = WorkStage.Implement };

        var decision = QueueScheduler.Decide(LocalAt(12), [start, .. active, fix], 0, 8.0, Defaults, null, held: false);

        Assert.Equal(QueueDecisionKind.Launch, decision.Kind);
        Assert.Equal("fix", decision.Item!.Tag);
    }

    [Fact]
    public void Live_reviews_are_bounded_without_spending_mutating_weight()
    {
        var live = new[]
        {
            Item("live-1") with { Stage = WorkStage.Review, State = QueueItemState.Launched, Round = 1 },
            Item("live-2") with { Stage = WorkStage.ReReview, State = QueueItemState.Launched, Round = 1 },
        };
        var review = Item("review") with { Stage = WorkStage.Review, Round = 1 };

        var decision = QueueScheduler.Decide(LocalAt(12), [.. live, review], 0, 8.0, Defaults, null, held: false);

        Assert.Equal(QueueWaitReason.ReviewCap, decision.WaitReason);
        Assert.Equal("review", decision.Item!.Tag);
    }

    [Fact]
    public void A_review_cap_blocked_review_does_not_block_an_independently_launchable_fix()
    {
        var live = new[]
        {
            Item("live-1") with { Stage = WorkStage.Review, State = QueueItemState.Launched, Round = 1 },
            Item("live-2") with { Stage = WorkStage.ReReview, State = QueueItemState.Launched, Round = 1 },
        };
        var blockedReview = Item("review") with { Stage = WorkStage.Review, Round = 1, AttemptId = FleetAttemptId.New() };
        var fix = Item("fix") with { Stage = WorkStage.Fix, Round = 1, AttemptId = FleetAttemptId.New() };

        var decision = QueueScheduler.Decide(LocalAt(12), [.. live, blockedReview, fix], 0, 8.0, Defaults, null, held: false);

        Assert.Equal(QueueDecisionKind.Launch, decision.Kind);
        Assert.Equal("fix", decision.Item!.Tag);
        Assert.Equal(QueuePriorityBand.Repair, decision.Context!.SelectedBand);
    }

    [Fact]
    public void A_round_zero_cancellation_is_not_an_active_lifecycle()
    {
        var cancelled = Item("cancelled") with { Stage = WorkStage.Implement, State = QueueItemState.Cancelled };
        var newWork = Item("new") with { Stage = WorkStage.Implement };

        var decision = QueueScheduler.Decide(LocalAt(12), [cancelled, newWork], 0, 8.0,
            new QueueSettings { MaxActiveLifecycles = 1 }, null, held: false);

        Assert.Equal(QueueDecisionKind.Launch, decision.Kind);
        Assert.Equal("new", decision.Item!.Tag);
        Assert.Equal(0, decision.Context!.Portfolio.ActiveLifecycles);
    }

    [Fact]
    public void A_cancelled_row_with_prior_launch_proof_still_consumes_wip_until_retirement()
    {
        var malformed = Item("prior") with
        {
            Stage = WorkStage.Ready,
            State = QueueItemState.Cancelled,
            AttemptId = FleetAttemptId.New(),
        };
        var next = Item("next") with { Stage = WorkStage.Implement };
        var settings = new QueueSettings { MaxActiveLifecycles = 1 };
        var blocked = QueueScheduler.Decide(LocalAt(12), [malformed, next], 0, 8,
            settings, null, held: false);
        var retired = malformed with
        {
            Retirement = new QueueRetirement(QueueRetirement.Operator, LocalAt(11), "resolved"),
        };
        var admitted = QueueScheduler.Decide(LocalAt(12), [retired, next], 0, 8,
            settings, null, held: false);

        Assert.Equal(QueueWaitReason.LifecycleCap, blocked.WaitReason);
        Assert.Equal(1, blocked.Context!.Portfolio.ActiveLifecycles);
        Assert.Equal(QueueDecisionKind.Launch, admitted.Kind);
    }

    [Fact]
    public void Legacy_lifecycle_evidence_counts_but_an_untouched_round_zero_row_does_not()
    {
        var legacyStarted = Item("legacy") with { Stage = WorkStage.Implement, RoomDirectory = "C:\\room" };
        var untouched = Item("untouched") with { Stage = WorkStage.Implement };

        Assert.True(QueueScheduler.IsActiveLifecycle(legacyStarted));
        Assert.False(QueueScheduler.IsActiveLifecycle(untouched));
    }

    [Fact]
    public void Retirement_replenishes_one_slot_and_one_tick_selects_only_one_new_start()
    {
        var active = Enumerable.Range(1, 4)
            .Select(n => Item($"active-{n}") with { Stage = WorkStage.Ready, Round = 1, PullRequest = 100 + n })
            .ToArray();
        var starts = new[]
        {
            Item("first") with { Stage = WorkStage.Implement },
            Item("second") with { Stage = WorkStage.Implement },
        };
        var full = QueueScheduler.Decide(LocalAt(12), [.. active, .. starts], 0, 8, Defaults, null, false);
        var retired = active[0] with
        {
            Retirement = new QueueRetirement(QueueRetirement.Operator, LocalAt(11), "resolved"),
        };
        var oneSlot = QueueScheduler.Decide(LocalAt(12), [retired, .. active[1..], .. starts],
            0, 8, Defaults, null, false);
        var claimed = starts[0] with { AttemptId = FleetAttemptId.New(), State = QueueItemState.Done };
        var fullAgain = QueueScheduler.Decide(LocalAt(12), [retired, .. active[1..], claimed, starts[1]],
            0, 8, Defaults, null, false);

        Assert.Equal(QueueWaitReason.LifecycleCap, full.WaitReason);
        Assert.Equal(QueueDecisionKind.Launch, oneSlot.Kind);
        Assert.Equal("first", oneSlot.Item!.Tag);
        Assert.Equal(3, oneSlot.Context!.Portfolio.ActiveLifecycles);
        Assert.Equal(QueueWaitReason.LifecycleCap, fullAgain.WaitReason);
        Assert.Equal("second", fullAgain.Item!.Tag);
    }

    [Fact]
    public void Halted_and_unobserved_pr_lifecycles_hold_capacity_until_trusted_retirement()
    {
        var ready = Item("ready") with { Stage = WorkStage.Ready, AttemptId = FleetAttemptId.New() };
        var halted = Item("halted") with
        {
            Stage = WorkStage.Fix,
            State = QueueItemState.Failed,
            Halted = true,
            AttemptId = FleetAttemptId.New(),
            PullRequest = null,
        };
        var newWork = Item("new") with { Stage = WorkStage.Implement };
        var settings = new QueueSettings { MaxActiveLifecycles = 2, MaxPrePullRequestLifecycles = 2 };
        var blocked = QueueScheduler.Decide(LocalAt(12), [ready, halted, newWork], 0, 8,
            settings, null, false);
        var retired = halted with
        {
            Retirement = new QueueRetirement(QueueRetirement.Operator, LocalAt(11), "resolved"),
        };
        var admitted = QueueScheduler.Decide(LocalAt(12), [ready, retired, newWork], 0, 8,
            settings, null, false);

        Assert.Equal(QueueWaitReason.LifecycleCap, blocked.WaitReason);
        Assert.Equal(2, blocked.Context!.Portfolio.PrePullRequestLifecycles);
        Assert.Equal("ready", blocked.Context.OldestOccupyingLifecycleTag);
        Assert.Equal(QueueDecisionKind.Launch, admitted.Kind);
    }

    [Fact]
    public void Finish_first_bands_pass_new_work_but_keep_operator_order_within_each_band()
    {
        var start = Item("start") with { Stage = WorkStage.Implement };
        var fix1 = Item("fix-1") with { Stage = WorkStage.Fix, Round = 1 };
        var fix2 = Item("fix-2") with { Stage = WorkStage.Continue, Round = 1 };
        var review1 = Item("review-1") with { Stage = WorkStage.Review, Round = 1 };
        var review2 = Item("review-2") with { Stage = WorkStage.ReReview, Round = 1 };
        var first = QueueScheduler.Decide(LocalAt(12), [start, fix1, review1, review2, fix2],
            0, 8, Defaults, null, false);
        var second = QueueScheduler.Decide(LocalAt(12), [start, fix1, review2, fix2],
            0, 8, Defaults, null, false);
        var repair = QueueScheduler.Decide(LocalAt(12), [start, fix1, fix2],
            0, 8, Defaults, null, false);

        Assert.Equal("review-1", first.Item!.Tag);
        Assert.Equal("review-2", second.Item!.Tag);
        Assert.Equal("fix-1", repair.Item!.Tag);
        Assert.Equal(QueuePriorityBand.Repair, repair.Context!.SelectedBand);
        Assert.True(repair.Context.PassedNewWorkHead);
    }

    [Fact]
    public void Round_zero_and_standalone_new_work_keep_fifo_even_when_head_is_cap_blocked()
    {
        var active = Item("active") with { Stage = WorkStage.Ready, Round = 1 };
        var start = Item("start") with { Stage = WorkStage.Implement };
        var standalone = Item("standalone");
        var settings = new QueueSettings { MaxActiveLifecycles = 1 };
        var blocked = QueueScheduler.Decide(LocalAt(12), [active, start, standalone],
            0, 8, settings, null, false);
        var firstStandalone = QueueScheduler.Decide(LocalAt(12), [active, standalone, start],
            0, 8, settings, null, false);

        Assert.Equal(QueueWaitReason.LifecycleCap, blocked.WaitReason);
        Assert.Equal("start", blocked.Item!.Tag);
        Assert.Equal(QueuePriorityBand.NewWork, blocked.Context!.SelectedBand);
        Assert.Equal(QueueDecisionKind.Launch, firstStandalone.Kind);
        Assert.Equal("standalone", firstStandalone.Item!.Tag);
    }

    [Fact]
    public void Partial_or_invalid_wip_settings_keep_every_other_positive_shipped_default()
    {
        var partial = new QueueSettings { MaxActiveLifecycles = 3 };
        var invalid = new QueueSettings
        {
            MaxActiveLifecycles = 0,
            MaxPrePullRequestLifecycles = -1,
            MaxLiveReviews = 0,
        };

        Assert.Equal(3, partial.EffectiveMaxActiveLifecycles);
        Assert.Equal(QueueSettings.DefaultMaxPrePullRequestLifecycles, partial.EffectiveMaxPrePullRequestLifecycles);
        Assert.Equal(QueueSettings.DefaultMaxLiveReviews, partial.EffectiveMaxLiveReviews);
        Assert.Equal(QueueSettings.DefaultMaxActiveLifecycles, invalid.EffectiveMaxActiveLifecycles);
        Assert.Equal(QueueSettings.DefaultMaxPrePullRequestLifecycles, invalid.EffectiveMaxPrePullRequestLifecycles);
        Assert.Equal(QueueSettings.DefaultMaxLiveReviews, invalid.EffectiveMaxLiveReviews);
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
    public void A_retired_queued_item_is_not_a_scheduler_candidate()
    {
        var retired = Item("history") with
        {
            Retirement = new QueueRetirement(QueueRetirement.Operator, DateTimeOffset.UtcNow, "handled manually"),
        };

        var decision = QueueScheduler.Decide(LocalAt(12), [retired], 0, 8.0, Defaults, null, held: false);

        Assert.Equal(QueueWaitReason.NoItems, decision.WaitReason);
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

    /// <summary>
    /// #2136: the shape measured on 2026-09-08 — an implement head at a full cap with two reviews
    /// behind it, neither of which launched for the whole wait.
    /// </summary>
    [Fact]
    public void A_standalone_review_does_not_pass_a_slot_blocked_new_work_head()
    {
        var items = new[] { Item("head"), Item("rev-1", role: "review"), Item("rev-2", role: "review") };

        var atCap = QueueScheduler.Decide(LocalAt(12), items, liveWeight: 4.0, 8.0, Defaults, null, held: false);

        Assert.Equal(QueueWaitReason.Slots, atCap.WaitReason);
        Assert.Equal("head", atCap.Item!.Tag);

        // Control: the same queue with room for the head launches the head, so the arm above is about
        // the pass-through and not about review always winning.
        var belowCap = QueueScheduler.Decide(LocalAt(12), items, liveWeight: 3.0, 8.0, Defaults, null, held: false);
        Assert.Equal("head", belowCap.Item!.Tag);
    }

    [Fact]
    public void A_standalone_review_does_not_pass_multiple_slot_blocked_new_work_rows()
    {
        // Standalone work stays in operator order even when its role is review.
        var items = new[] { Item("head"), Item("second"), Item("rev", role: "review") };

        var decision = QueueScheduler.Decide(LocalAt(12), items, liveWeight: 4.0, 8.0, Defaults, null, held: false);

        Assert.Equal(QueueWaitReason.Slots, decision.WaitReason);
        Assert.Equal("head", decision.Item!.Tag);
    }

    [Fact]
    public void A_non_review_item_never_passes_a_slot_blocked_head_regardless_of_what_is_behind_it()
    {
        // Live 3.5: the claude head (+1.0) is over the 4.0 cap. It waits anyway — order among
        // weighted items is untouched regardless of an item behind it (spec/baton.md §13); only
        // review is allowed to pass a slot-blocked head.
        var items = new[] { Item("head"), Item("behind", adapter: "codex"), Item("second") };

        var decision = QueueScheduler.Decide(LocalAt(12), items, liveWeight: 3.5, 8.0, Defaults, null, held: false);

        Assert.Equal(QueueWaitReason.Slots, decision.WaitReason);
        Assert.Equal("head", decision.Item!.Tag);
    }

    [Fact]
    public void A_review_behind_a_memory_blocked_head_does_not_pass_it()
    {
        // Under the day floor AND at the cap. Memory is a host fact: the review would bypass it as
        // the head (the control), but not as a passer behind a head the floor is holding.
        var items = new[] { Item("head"), Item("rev", role: "review") };

        var decision = QueueScheduler.Decide(LocalAt(12), items, liveWeight: 4.0, freeGb: 0.1, Defaults, null, held: false);

        Assert.Equal(QueueWaitReason.Memory, decision.WaitReason);
        Assert.Equal("head", decision.Item!.Tag);

        var reviewAtHead = QueueScheduler.Decide(
            LocalAt(12), [Item("rev", role: "review")], liveWeight: 4.0, freeGb: 0.1, Defaults, null, held: false);
        Assert.Equal(QueueDecisionKind.Launch, reviewAtHead.Kind);
    }

    [Fact]
    public void A_standalone_review_behind_a_new_work_head_preserves_new_work_order()
    {
        var now = LocalAt(12);
        var items = new[] { Item("head"), Item("rev", role: "review") };

        var heldDecision = QueueScheduler.Decide(now, items, liveWeight: 4.0, 8.0, Defaults, null, held: true);
        var inGap = QueueScheduler.Decide(now, items, liveWeight: 4.0, 8.0, Defaults, now - TimeSpan.FromSeconds(1), held: false);

        Assert.Equal(QueueWaitReason.Hold, heldDecision.WaitReason);
        Assert.Equal(QueueWaitReason.Gap, inGap.WaitReason);
        Assert.Equal("head", inGap.Item!.Tag);

        // Control: elapsed gap preserves the same new-work head; only an existing lifecycle may pass it.
        var afterGap = QueueScheduler.Decide(
            now, items, liveWeight: 4.0, 8.0, Defaults, now - TimeSpan.FromSeconds(Defaults.EffectiveGapSeconds), held: false);
        Assert.Equal("head", afterGap.Item!.Tag);
    }

    [Fact]
    public void A_standalone_review_never_passes_a_slot_blocked_new_work_head()
    {
        // The same candidacy predicate the head is chosen by: an external, a launched and a `ready`
        // review are all skipped, and the first eligible one after them is the passer.
        var items = new[]
        {
            Item("head"),
            Item("outside", role: "review", external: true),
            Item("running", role: "review", state: QueueItemState.Launched),
            Item("approved", role: "review") with { Stage = WorkStage.Ready },
            Item("rev", role: "review"),
        };

        var decision = QueueScheduler.Decide(LocalAt(12), items, liveWeight: 4.0, 8.0, Defaults, null, held: false);

        Assert.Equal(QueueWaitReason.Slots, decision.WaitReason);
        Assert.Equal("head", decision.Item!.Tag);

        // Control: removing the standalone review leaves the same head-of-line wait.
        var nonePass = QueueScheduler.Decide(LocalAt(12), items[..4], liveWeight: 4.0, 8.0, Defaults, null, held: false);
        Assert.Equal(QueueWaitReason.Slots, nonePass.WaitReason);
        Assert.Equal("head", nonePass.Item!.Tag);
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
