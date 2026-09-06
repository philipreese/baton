namespace Baton.Cli.Daemon;

/// <summary>
/// Pure cadence decision for issue #1391's usage harvester — no process spawn, no file I/O, no clock
/// read of its own; <paramref name="now"/> is caller-supplied on every tick so
/// <c>VendorUsageHarvestSchedulerTests</c> can drive it with a fake clock deterministically. Kept
/// separate from <see cref="VendorUsageHarvester"/> (the <c>BackgroundService</c> that spawns
/// sources and persists snapshots) so the cadence rules — the part with the most ways to be subtly
/// wrong — are testable without a process double.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rules (issue #1391, operator-approved 2026-09-04; widened by #1966):</b> harvest every
/// <c>periodicInterval</c> while at least one lane is live on that vendor and every
/// <c>idleInterval</c> while none is; harvest once <c>postExitDelay</c> after any lane exits; harvest
/// once after each window boundary the vendor's last snapshot names; jitter the two periodic delays and
/// the post-exit delay by up to <c>jitter</c>; coalesce a trigger that lands within
/// <c>coalesceWindow</c> of the most recent actual harvest into that harvest rather than firing a
/// second one.
/// </para>
/// <para>
/// <b>#1966 replaced the idle backoff</b> — "no harvesting at all while idle" — with the slower
/// <c>idleInterval</c>. The backoff was what made an idle vendor's snapshot age past the runway hold's
/// staleness limit (measured 2026-09-06: a 12.2 h-old agy snapshot the conductor had to override), and
/// the whole point of a cadence that does not depend on live lanes is that the gate reads a snapshot
/// that already exists. spec/baton.md §7 is the register for the cadence and what it costs.
/// </para>
/// <para>
/// <b>The boundary trigger fires once per boundary regardless of outcome.</b> Dueness is decided
/// against <see cref="VendorState.LastHarvestedAt"/>, which this type stamps when it RETURNS true — it
/// never learns whether the harvest that followed succeeded. So a boundary whose harvest failed is not
/// retried at that boundary; the periodic interval is what covers it. The alternative, retrying until a
/// snapshot lands, turns a permanently broken vendor CLI into a spawn every tick.
/// </para>
/// <para>
/// <b>One <see cref="VendorState"/> per vendor tag</b> (<c>"claude"</c>/<c>"agy"</c>), lazily created
/// on first tick. <see cref="OnTick"/> is NOT reentrant-safe for the same vendor called
/// concurrently — <see cref="VendorUsageHarvester"/>'s own tick loop calls it sequentially, once per
/// vendor per tick, never overlapping.
/// </para>
/// </remarks>
public sealed class VendorUsageHarvestScheduler
{
    private readonly TimeSpan _periodicInterval;
    private readonly TimeSpan _idleInterval;
    private readonly TimeSpan _jitter;
    private readonly TimeSpan _postExitDelay;
    private readonly TimeSpan _coalesceWindow;
    private readonly Func<double> _jitterSource;

    private readonly Dictionary<string, VendorState> _states = new(StringComparer.Ordinal);

    /// <param name="idleInterval">#1966: the floor cadence, applied to a vendor with no live lane. Has
    /// no default and is positional, so a caller cannot get the pre-#1966 idle backoff back by omitting
    /// it — the omission would be invisible, and what it costs is a stale-snapshot hold.</param>
    /// <param name="jitterSource">Returns a value in [-1, 1]; multiplied by the relevant delay's own
    /// jitter budget. Defaults to <see cref="Random.Shared"/>; a test supplies a fixed value for a
    /// deterministic due instant.</param>
    public VendorUsageHarvestScheduler(
        TimeSpan periodicInterval,
        TimeSpan jitter,
        TimeSpan postExitDelay,
        TimeSpan coalesceWindow,
        TimeSpan idleInterval,
        Func<double>? jitterSource = null)
    {
        _periodicInterval = periodicInterval;
        _idleInterval = idleInterval;
        _jitter = jitter;
        _postExitDelay = postExitDelay;
        _coalesceWindow = coalesceWindow;
        _jitterSource = jitterSource ?? (() => Random.Shared.NextDouble() * 2 - 1);
    }

    /// <summary>
    /// Advances <paramref name="vendor"/>'s schedule by one tick and reports whether this tick should
    /// harvest. Called once per tick per vendor, regardless of whether a harvest fires — the periodic
    /// schedule and the post-exit trigger both depend on seeing every tick's
    /// <paramref name="anyLiveLaneNow"/> reading, not just the ticks a caller happens to poll.
    /// </summary>
    /// <param name="windowBoundaries">
    /// #1966: the reset instants the vendor's LAST persisted snapshot names, in any order — the caller
    /// reads them before harvesting (see <see cref="VendorUsageHarvester.TickOnceAsync"/>), since reading
    /// them after would compare against the snapshot this tick just wrote and never fire. Empty or null
    /// means the vendor has no snapshot, or none of its windows carried a parseable reset, in which case
    /// the periodic cadence alone drives it. Each boundary buys at most one harvest — see this type's own
    /// remarks for why a failed one is not retried at that boundary.
    /// </param>
    public bool OnTick(
        string vendor,
        DateTimeOffset now,
        bool anyLiveLaneNow,
        IReadOnlyList<DateTimeOffset>? windowBoundaries = null)
    {
        var state = _states.TryGetValue(vendor, out var existing) ? existing : _states[vendor] = new VendorState();

        var laneJustExited = state.WasLiveLastTick && !anyLiveLaneNow;
        state.WasLiveLastTick = anyLiveLaneNow;

        if (laneJustExited && state.PendingPostExitDueAt is null)
        {
            state.PendingPostExitDueAt = now + _postExitDelay + JitterFor(_postExitDelay);
        }

        // The periodic schedule exists in BOTH modes since #1966; liveness only picks the interval. A
        // vendor that goes live under a pending idle schedule is pulled in to the shorter live one --
        // otherwise the first live harvest could be up to a whole idle interval away, which is the
        // cadence a live lane is specifically not supposed to get. Going idle does not push a pending
        // live schedule back out: harvesting sooner than the floor is never the failure.
        if (state.NextPeriodicDueAt is null || (anyLiveLaneNow && !state.ScheduledWhileLive))
        {
            var interval = anyLiveLaneNow ? _periodicInterval : _idleInterval;
            var due = now + interval + JitterFor(interval);
            state.NextPeriodicDueAt = state.NextPeriodicDueAt is { } pending && pending < due ? pending : due;
            state.ScheduledWhileLive = anyLiveLaneNow;
        }

        var periodicDue = state.NextPeriodicDueAt is { } periodicAt && now >= periodicAt;
        var postExitDue = state.PendingPostExitDueAt is { } postExitAt && now >= postExitAt;
        var boundaryDue = IsBoundaryDue(state, now, windowBoundaries);

        if (!periodicDue && !postExitDue && !boundaryDue)
        {
            return false;
        }

        if (periodicDue)
        {
            var interval = anyLiveLaneNow ? _periodicInterval : _idleInterval;
            state.NextPeriodicDueAt = now + interval + JitterFor(interval);
            state.ScheduledWhileLive = anyLiveLaneNow;
        }

        if (postExitDue)
        {
            state.PendingPostExitDueAt = null;
        }

        // Coalesce: a trigger due within the coalesce window of the last ACTUAL harvest is satisfied
        // by that recent harvest rather than firing a second one on top of it. The due flags above are
        // still cleared/rescheduled either way -- a coalesced trigger is consumed, not deferred to the
        // next tick, since the recent harvest already produced a fresh-enough reading.
        if (state.LastHarvestedAt is { } last && now - last < _coalesceWindow)
        {
            return false;
        }

        state.LastHarvestedAt = now;
        return true;
    }

    /// <summary>
    /// Whether a window boundary has passed that no harvest of this vendor has followed yet (#1966).
    /// Deliberately keyed on <see cref="VendorState.LastHarvestedAt"/> rather than on a remembered
    /// boundary: what makes a reset instant interesting is that the counters behind it have not been
    /// re-read since, and any harvest after it re-reads them, whichever trigger fired it. A boundary
    /// still in the future is not due, and one already followed by a harvest never becomes due again.
    /// <b>On the first tick after a daemon start</b> there is no prior harvest, so a boundary already in
    /// the past IS due — which is the intended reading: nothing this process knows of has read the
    /// counters since the window turned over.
    /// </summary>
    private static bool IsBoundaryDue(
        VendorState state, DateTimeOffset now, IReadOnlyList<DateTimeOffset>? windowBoundaries)
    {
        if (windowBoundaries is null)
        {
            return false;
        }

        foreach (var boundary in windowBoundaries)
        {
            if (boundary <= now && (state.LastHarvestedAt is not { } last || last < boundary))
            {
                return true;
            }
        }

        return false;
    }

    private TimeSpan JitterFor(TimeSpan baseline)
    {
        var maxJitterSeconds = Math.Min(_jitter.TotalSeconds, baseline.TotalSeconds);
        return TimeSpan.FromSeconds(_jitterSource() * maxJitterSeconds);
    }

    private sealed class VendorState
    {
        public bool WasLiveLastTick;
        public DateTimeOffset? NextPeriodicDueAt;

        /// <summary>Which interval <see cref="NextPeriodicDueAt"/> was computed from, so a vendor going
        /// live under an idle schedule is rescheduled once rather than on every subsequent tick.</summary>
        public bool ScheduledWhileLive;
        public DateTimeOffset? PendingPostExitDueAt;
        public DateTimeOffset? LastHarvestedAt;
    }
}
