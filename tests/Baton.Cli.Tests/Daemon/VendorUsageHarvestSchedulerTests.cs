using Baton.Cli.Daemon;
using Xunit;

namespace Baton.Cli.Tests.Daemon;

/// <summary>
/// Cadence coverage for <see cref="VendorUsageHarvestScheduler"/> (issue #1391), driven entirely by
/// caller-supplied <c>DateTimeOffset</c> ticks — no real clock, no process, no fake-clock package
/// (CLAUDE.md: <c>Baton.Cli</c>'s project graph carries no extra NuGet dependency for this).
/// </summary>
public sealed class VendorUsageHarvestSchedulerTests
{
    private static readonly TimeSpan Periodic = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan Jitter = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan PostExit = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Coalesce = TimeSpan.FromSeconds(60);
    private static readonly DateTimeOffset Start = new(2026, 9, 4, 0, 0, 0, TimeSpan.Zero);

    // Zero jitter throughout unless a test asserts the jitter bound itself -- makes every other test's
    // due instant exact rather than a range to reason about.
    private static VendorUsageHarvestScheduler NoJitterScheduler() =>
        new(Periodic, Jitter, PostExit, Coalesce, Idle, jitterSource: () => 0);

    /// <summary>
    /// #1966 inverted this arm. It used to assert that an idle vendor is NEVER harvested — the idle
    /// backoff, whose cost <see cref="VendorUsageHarvestScheduler"/>'s own remarks and spec/baton.md §7
    /// state. The floor cadence now applies with no live lane — and still at the slower idle interval,
    /// which is the second assertion: nothing due at the live interval, due at the idle one.
    /// </summary>
    [Fact]
    public void Idle_NoLiveLaneEver_StillHarvestsOnTheIdleInterval()
    {
        var scheduler = NoJitterScheduler();
        var now = Start;

        Assert.False(scheduler.OnTick("claude", now, anyLiveLaneNow: false));

        // The live interval is NOT what an idle vendor gets -- without this the arm below is satisfied
        // by a scheduler that simply ignores liveness.
        now += Periodic;
        Assert.False(scheduler.OnTick("claude", now, anyLiveLaneNow: false));

        now = Start + Idle;
        Assert.True(scheduler.OnTick("claude", now, anyLiveLaneNow: false));

        // And it repeats, rather than firing once and reverting to the old backoff.
        now += Idle;
        Assert.True(scheduler.OnTick("claude", now, anyLiveLaneNow: false));
    }

    /// <summary>
    /// The live/idle transition rule <see cref="VendorUsageHarvestScheduler.OnTick"/> states at the
    /// reschedule itself: a lane starting just after an idle harvest must not wait out the whole idle
    /// interval for its first live reading.
    /// </summary>
    [Fact]
    public void GoingLiveUnderAnIdleSchedule_PullsTheNextHarvestInToTheLiveInterval()
    {
        var scheduler = NoJitterScheduler();

        Assert.False(scheduler.OnTick("claude", Start, anyLiveLaneNow: false));
        Assert.False(scheduler.OnTick("claude", Start + TimeSpan.FromSeconds(30), anyLiveLaneNow: true));

        // Live interval measured from the tick the lane went live on, not from Start.
        var due = Start + TimeSpan.FromSeconds(30) + Periodic;
        Assert.False(scheduler.OnTick("claude", due - TimeSpan.FromSeconds(1), anyLiveLaneNow: true));
        Assert.True(scheduler.OnTick("claude", due, anyLiveLaneNow: true));
    }

    /// <summary>
    /// #1966's boundary trigger: one harvest after each reset instant the vendor's last snapshot names,
    /// and only one. A boundary still in the future buys nothing (the control — without it, a scheduler
    /// that harvested on every tick with any boundary at all would pass).
    /// </summary>
    [Fact]
    public void AWindowBoundaryThatHasPassed_HarvestsOnceAndNotAgain()
    {
        var scheduler = NoJitterScheduler();
        var boundary = Start + TimeSpan.FromMinutes(5);

        // Before the boundary: not due, though the same boundary list is supplied.
        Assert.False(scheduler.OnTick("claude", Start, anyLiveLaneNow: false, [boundary]));
        Assert.False(scheduler.OnTick("claude", boundary - TimeSpan.FromSeconds(1), anyLiveLaneNow: false, [boundary]));

        Assert.True(scheduler.OnTick("claude", boundary, anyLiveLaneNow: false, [boundary]));

        // Consumed: the same boundary never fires a second harvest, however many ticks pass under the
        // idle interval.
        for (var i = 1; i <= 20; i++)
        {
            Assert.False(scheduler.OnTick(
                "claude", boundary + TimeSpan.FromSeconds(30 * i), anyLiveLaneNow: false, [boundary]));
        }

        // A LATER boundary -- the next window's reset -- fires its own harvest.
        var next = boundary + TimeSpan.FromMinutes(10);
        Assert.True(scheduler.OnTick("claude", next, anyLiveLaneNow: false, [boundary, next]));
    }

    /// <summary>
    /// #1966 review: TWO boundaries already past on the same tick buy ONE harvest, not one each. Current
    /// behaviour, so this is coverage rather than a fix — the edit it exists to catch is turning
    /// <c>IsBoundaryDue</c> into a remembered-boundary set that pops one boundary per fire, which every
    /// other arm in this file stays green under because none supplies two past boundaries at once.
    /// </summary>
    [Fact]
    public void TwoBoundariesAlreadyPastOnTheSameTick_BuyOneHarvestBetweenThem()
    {
        var scheduler = NoJitterScheduler();
        var first = Start + TimeSpan.FromMinutes(5);
        var second = Start + TimeSpan.FromMinutes(6);

        // Control: both still ahead, so the harvest below is the boundaries' doing and not the tick's.
        Assert.False(scheduler.OnTick("claude", Start, anyLiveLaneNow: false, [first, second]));

        Assert.True(scheduler.OnTick("claude", second, anyLiveLaneNow: false, [first, second]));

        // The discriminator, placed PAST the coalesce window (a tick inside it would be refused by the
        // coalesce rule and prove nothing about the second boundary) and well inside the idle interval
        // (a tick past that would harvest for the periodic reason instead).
        var after = second + Coalesce + TimeSpan.FromSeconds(1);
        Assert.True(after < Start + Idle, "the follow-up tick must land before the idle schedule comes due");
        Assert.False(scheduler.OnTick("claude", after, anyLiveLaneNow: false, [first, second]));
    }

    /// <summary>
    /// #1966 review: a boundary that falls inside the coalesce window is CONSUMED by the harvest that
    /// opened the window, not deferred to a later tick. Before the fix the boundary stayed due and fired
    /// on the first tick past the window — a second spawn 90 s after the first, which is exactly what the
    /// coalesce window exists to prevent (spec/baton.md §7 states the consume rule).
    /// </summary>
    [Fact]
    public void ABoundaryInsideTheCoalesceWindow_IsConsumedByThatHarvestRatherThanDeferred()
    {
        var scheduler = NoJitterScheduler();
        var first = Start + TimeSpan.FromMinutes(5);
        Assert.True(scheduler.OnTick("claude", first, anyLiveLaneNow: false, [first]));

        // A second window turns over 30s later -- inside the 60s window, so it coalesces into the harvest
        // just taken.
        var inside = first + TimeSpan.FromSeconds(30);
        Assert.True(inside - first < Coalesce, "fixture must place the second boundary INSIDE the window");
        Assert.False(scheduler.OnTick("claude", inside, anyLiveLaneNow: false, [first, inside]));

        // The assertion the fix bought: past the coalesce window, the consumed boundary does not come
        // back. Deferred rather than consumed, this tick harvests.
        Assert.False(scheduler.OnTick(
            "claude", first + TimeSpan.FromSeconds(120), anyLiveLaneNow: false, [first, inside]));
    }

    /// <summary>
    /// The polarity of the arm above: the same second boundary one step the other side of the coalesce
    /// window fires its own harvest. Without this, a scheduler that consumed every boundary at the first
    /// one would satisfy the arm above.
    /// </summary>
    [Fact]
    public void ABoundaryOutsideTheCoalesceWindow_FiresItsOwnHarvest()
    {
        var scheduler = NoJitterScheduler();
        var first = Start + TimeSpan.FromMinutes(5);
        Assert.True(scheduler.OnTick("claude", first, anyLiveLaneNow: false, [first]));

        var outside = first + Coalesce + TimeSpan.FromSeconds(30);
        Assert.True(scheduler.OnTick("claude", outside, anyLiveLaneNow: false, [first, outside]));
    }

    /// <summary>
    /// #1966 review: the first tick after a daemon restart must not re-fire a boundary the persisted
    /// snapshot already read past. claude's reset parser accepts an instant up to three days behind the
    /// snapshot's own <c>HarvestedAt</c>, so a snapshot taken minutes ago can name yesterday's reset —
    /// and <c>pixi run tool-refresh</c> restarts the daemon routinely, so the cost was a full
    /// <c>/usage</c> spawn per such vendor per restart for no new counters. Both polarities, since the
    /// only thing that may decide it is which side of the boundary the snapshot was taken on.
    /// </summary>
    [Theory]
    [InlineData(-30, false)] // snapshot taken AFTER the boundary: those counters are already read
    [InlineData(-120, true)] // snapshot taken BEFORE it: the window turned over unread, so it is due
    public void ABoundaryTheLastSnapshotAlreadyReadPast_IsNotDueOnTheFirstTickAfterARestart(
        int snapshotMinutesFromStart, bool expected)
    {
        // A fresh scheduler IS the restart: no in-memory LastHarvestedAt, only what is on disk.
        var scheduler = NoJitterScheduler();
        var boundary = Start - TimeSpan.FromHours(1);

        Assert.Equal(
            expected,
            scheduler.OnTick(
                "claude",
                Start,
                anyLiveLaneNow: false,
                [boundary],
                snapshotHarvestedAt: Start + TimeSpan.FromMinutes(snapshotMinutesFromStart)));
    }

    [Fact]
    public void OneLiveLane_HarvestsOncePerPeriodicInterval()
    {
        var scheduler = NoJitterScheduler();
        var now = Start;

        // First tick with a live lane arms the schedule but does not itself harvest.
        Assert.False(scheduler.OnTick("claude", now, anyLiveLaneNow: true));

        // Nothing due before the interval elapses.
        now += Periodic - TimeSpan.FromSeconds(1);
        Assert.False(scheduler.OnTick("claude", now, anyLiveLaneNow: true));

        // Due exactly at the interval.
        now += TimeSpan.FromSeconds(1);
        Assert.True(scheduler.OnTick("claude", now, anyLiveLaneNow: true));

        // Rescheduled for another full interval out -- not due again immediately.
        now += TimeSpan.FromSeconds(1);
        Assert.False(scheduler.OnTick("claude", now, anyLiveLaneNow: true));

        now += Periodic - TimeSpan.FromSeconds(1);
        Assert.True(scheduler.OnTick("claude", now, anyLiveLaneNow: true));
    }

    [Fact]
    public void LaneExit_HarvestsOnceAfterPostExitDelay_ThenFallsBackToTheIdleInterval()
    {
        var scheduler = NoJitterScheduler();
        var now = Start;

        // Live, then quiet on the very next tick -- the exit transition.
        Assert.False(scheduler.OnTick("claude", now, anyLiveLaneNow: true));
        now += TimeSpan.FromSeconds(30);
        Assert.False(scheduler.OnTick("claude", now, anyLiveLaneNow: false));

        // Not yet due.
        now += PostExit - TimeSpan.FromSeconds(1);
        Assert.False(scheduler.OnTick("claude", now, anyLiveLaneNow: false));

        // Due: the one post-exit harvest fires.
        now += TimeSpan.FromSeconds(1);
        Assert.True(scheduler.OnTick("claude", now, anyLiveLaneNow: false));
        var postExitHarvest = now;

        // The post-exit trigger is one-shot: nothing fires again on its account, tick after tick.
        while (now < Start + Periodic - TimeSpan.FromSeconds(30))
        {
            now += TimeSpan.FromSeconds(30);
            Assert.False(scheduler.OnTick("claude", now, anyLiveLaneNow: false));
        }

        Assert.True(now > postExitHarvest + Coalesce, "the quiet stretch must outlast the coalesce window");

        // #1966: where this arm used to assert silence forever, the periodic schedule now survives the
        // lane exit -- the one armed while the lane was live still comes due -- and reschedules on the
        // slower IDLE interval afterwards, not the live one.
        Assert.True(scheduler.OnTick("claude", Start + Periodic, anyLiveLaneNow: false));
        Assert.False(scheduler.OnTick("claude", Start + Periodic + Periodic, anyLiveLaneNow: false));
        Assert.True(scheduler.OnTick("claude", Start + Periodic + Idle, anyLiveLaneNow: false));
    }

    /// <summary>
    /// Drives the exact sequence the coalesce rule exists for: a periodic harvest, then a lane exit
    /// that arms a post-exit trigger, then the tick on which that trigger comes due. Returns whether
    /// the post-exit tick harvested, plus that tick's gap from the periodic harvest.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="jitter"/> is what moves the post-exit trigger across the coalesce boundary,
    /// and it has to: <c>JitterFor</c> clamps the budget to <c>min(jitter, baseline)</c>, so with the
    /// SHIPPED constants (PostExit == Coalesce == 60s) and zero jitter the post-exit instant is
    /// always <c>exit + 60s</c>, and the exit tick is always strictly after the periodic harvest (a
    /// periodic harvest needs <c>anyLiveLaneNow</c>, an exit needs <c>!anyLiveLaneNow</c>) -- so the
    /// gap always exceeds 60s and the branch is only reachable on negative jitter. That is the
    /// narrow corner these two arms exercise; it is a property of the shipped constants, disclosed
    /// rather than tuned away, since changing a background service's cadence is a change to real
    /// vendor session traffic that #1869's review scoped out.
    /// </para>
    /// </remarks>
    private static (bool Harvested, TimeSpan GapFromPriorHarvest) RunPeriodicThenPostExit(
        double jitter, TimeSpan exitAfterHarvest)
    {
        var scheduler = new VendorUsageHarvestScheduler(
            Periodic, Jitter, PostExit, Coalesce, Idle, jitterSource: () => jitter);
        var now = Start;

        Assert.False(scheduler.OnTick("claude", now, anyLiveLaneNow: true));

        // Periodic due = interval + min(Jitter, interval) * jitter.
        var harvestedAt = Start + Periodic + TimeSpan.FromSeconds(Math.Min(Jitter.TotalSeconds, Periodic.TotalSeconds) * jitter);
        Assert.True(scheduler.OnTick("claude", harvestedAt, anyLiveLaneNow: true));

        // The lane exits -- arms the post-exit trigger, harvests nothing itself.
        var exitedAt = harvestedAt + exitAfterHarvest;
        Assert.False(scheduler.OnTick("claude", exitedAt, anyLiveLaneNow: false));

        // Post-exit due = delay + min(Jitter, delay) * jitter.
        var postExitDueAt = exitedAt + PostExit + TimeSpan.FromSeconds(Math.Min(Jitter.TotalSeconds, PostExit.TotalSeconds) * jitter);
        var harvested = scheduler.OnTick("claude", postExitDueAt, anyLiveLaneNow: false);

        if (!harvested)
        {
            // Discriminator: prove the trigger was DUE and got coalesced away, not merely early. A
            // consumed trigger never fires later; a deferred one would fire on this next tick, which
            // is past both the due instant and the coalesce window. One second past, not a whole
            // interval past: since #1966 the periodic schedule survives the lane exit, so a tick far
            // enough out would harvest for that reason instead and prove nothing about the trigger.
            Assert.False(scheduler.OnTick(
                "claude", postExitDueAt + Coalesce + TimeSpan.FromSeconds(1), anyLiveLaneNow: false));
        }

        return (harvested, postExitDueAt - harvestedAt);
    }

    [Fact]
    public void PostExitTriggerDueInsideCoalesceWindow_IsCoalescedIntoTheRecentHarvest()
    {
        // jitter -0.5 pulls the post-exit instant to exit+30s; the exit lands 10s after the periodic
        // harvest, so the trigger comes due 40s after it -- inside the 60s window.
        var (harvested, gap) = RunPeriodicThenPostExit(jitter: -0.5, exitAfterHarvest: TimeSpan.FromSeconds(10));

        Assert.True(gap < Coalesce, $"fixture must place the trigger INSIDE the window; gap was {gap}");
        Assert.False(harvested);
    }

    [Fact]
    public void PostExitTriggerDueOutsideCoalesceWindow_FiresItsOwnHarvest()
    {
        // Polarity arm, identical sequence: the same trigger one second the other side of the
        // boundary (61s after the harvest) must fire. Without this, the assertion above is satisfied
        // by a scheduler that never harvests at all.
        var (harvested, gap) = RunPeriodicThenPostExit(jitter: 0, exitAfterHarvest: TimeSpan.FromSeconds(1));

        Assert.True(gap > Coalesce, $"fixture must place the trigger OUTSIDE the window; gap was {gap}");
        Assert.True(harvested);
    }

    [Fact]
    public void PeriodicDueOutsideCoalesceWindowOfPriorHarvest_FiresIndependently()
    {
        var scheduler = NoJitterScheduler();
        var now = Start;

        Assert.False(scheduler.OnTick("claude", now, anyLiveLaneNow: true));
        now += Periodic;
        Assert.True(scheduler.OnTick("claude", now, anyLiveLaneNow: true)); // call #1

        // Second periodic due, a full interval later -- well outside the 60s coalesce window.
        now += Periodic;
        Assert.True(scheduler.OnTick("claude", now, anyLiveLaneNow: true)); // call #2
    }

    [Fact]
    public void JitterSource_ShiftsDueInstantWithinBudget()
    {
        // jitterSource always returns +1 -- the due instant should land at interval + full jitter, not
        // exactly at the bare interval.
        var scheduler = new VendorUsageHarvestScheduler(Periodic, Jitter, PostExit, Coalesce, Idle, jitterSource: () => 1);
        var now = Start;

        Assert.False(scheduler.OnTick("claude", now, anyLiveLaneNow: true));

        // Bare interval alone is not yet due -- the +jitter pushed the due instant later.
        now += Periodic;
        Assert.False(scheduler.OnTick("claude", now, anyLiveLaneNow: true));

        // Interval + full jitter budget is due.
        now += Jitter;
        Assert.True(scheduler.OnTick("claude", now, anyLiveLaneNow: true));
    }

    [Fact]
    public void VendorsAreIndependent_ClaudeLiveDoesNotArmAgy()
    {
        var scheduler = NoJitterScheduler();
        var now = Start;

        Assert.False(scheduler.OnTick("claude", now, anyLiveLaneNow: true));
        Assert.False(scheduler.OnTick("agy", now, anyLiveLaneNow: false));

        now += Periodic;
        Assert.True(scheduler.OnTick("claude", now, anyLiveLaneNow: true));
        Assert.False(scheduler.OnTick("agy", now, anyLiveLaneNow: false));
    }
}
