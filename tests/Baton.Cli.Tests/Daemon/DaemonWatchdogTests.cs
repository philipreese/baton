using System.Text.Json.Nodes;
using Baton.Cli.Daemon;

namespace Baton.Cli.Tests.Daemon;

/// <summary>
/// #1981: the daemon hung at 14:51 on 2026-09-06 and nothing in baton noticed for thirteen minutes.
/// These arms drive <see cref="DaemonWatchdog"/> on a fixture clock — a tick that never completes has
/// to reach the exit path, and (the control that makes that mean anything) a daemon ticking normally
/// must never reach it, however long it runs.
/// </summary>
public class DaemonWatchdogTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private static readonly DateTimeOffset T0 = new(2026, 9, 6, 14, 51, 0, TimeSpan.Zero);

    /// <summary>A clock the test advances by hand: no wall-clock waiting, and the 5-interval bound is
    /// crossed exactly where the test says it is rather than approximately.</summary>
    private sealed class FixtureClock(DateTimeOffset start)
    {
        public DateTimeOffset Now { get; private set; } = start;
        public void Advance(TimeSpan by) => Now += by;
    }

    private static (DaemonWatchdog Watchdog, DaemonTickLedger Ledger, List<string> Log, List<int> Exits)
        Build(FixtureClock clock)
    {
        var ledger = new DaemonTickLedger(() => clock.Now);
        var log = new List<string>();
        var exits = new List<int>();
        var watchdog = new DaemonWatchdog(ledger, () => clock.Now, () => Interval, log.Add, exits.Add);
        return (watchdog, ledger, log, exits);
    }

    [Fact]
    public void ATickThatNeverCompletes_TripsTheExitPath()
    {
        var clock = new FixtureClock(T0);
        var (watchdog, ledger, log, exits) = Build(clock);

        // One healthy round first, so the trip below is about the SILENCE that follows and not about
        // a ledger that never held anything.
        ledger.RecordTick(nameof(FleetProjectionWriter), TimeSpan.FromSeconds(2), Interval);
        ledger.RecordTick(nameof(WatchSweep), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(15));

        clock.Advance(Interval * DaemonWatchdog.MissedTickAllowance);
        Assert.False(watchdog.CheckOnce());
        Assert.Empty(exits);

        // ... and one tick past the allowance, with still nothing completing.
        clock.Advance(Interval);
        Assert.True(watchdog.CheckOnce());

        Assert.Equal(DaemonWatchdog.HungExitCode, Assert.Single(exits));
        Assert.NotEqual(0, DaemonWatchdog.HungExitCode);

        // One line, and it has to name which loop went quiet -- "the daemon is hung" alone is what
        // the log already effectively said on 2026-09-06.
        var line = Assert.Single(log);
        Assert.Contains(nameof(FleetProjectionWriter), line);
        Assert.Contains(nameof(WatchSweep), line);
        Assert.Contains("Exiting 70", line);
    }

    /// <summary>The control. Without it the arm above would pass just as well against a watchdog that
    /// trips unconditionally, which would take the daemon down every five intervals forever.</summary>
    [Fact]
    public void NormalTicks_NeverTrip_HoweverLongTheDaemonRuns()
    {
        var clock = new FixtureClock(T0);
        var (watchdog, ledger, log, exits) = Build(clock);

        // Two hours of ordinary ticking, including ticks that run long (20s against a 30s interval --
        // slow, but not hung), which must not be mistaken for silence.
        for (var i = 0; i < 240; i++)
        {
            clock.Advance(Interval);
            ledger.RecordTick(nameof(FleetProjectionWriter), TimeSpan.FromSeconds(20), Interval);
            Assert.False(watchdog.CheckOnce());
        }

        Assert.Empty(exits);
        Assert.Empty(log);
    }

    /// <summary>A daemon that wedges before any service has completed a first tick still trips —
    /// measured from process start, so "nothing has ever ticked" is a hang, not a grace period.</summary>
    [Fact]
    public void AStartupThatNeverCompletesAFirstTick_AlsoTrips()
    {
        var clock = new FixtureClock(T0);
        var (watchdog, _, log, exits) = Build(clock);

        clock.Advance(Interval * DaemonWatchdog.MissedTickAllowance);
        Assert.False(watchdog.CheckOnce());

        clock.Advance(Interval);
        Assert.True(watchdog.CheckOnce());
        Assert.Equal(DaemonWatchdog.HungExitCode, Assert.Single(exits));
        Assert.Contains("none has ever reported a tick", Assert.Single(log));
    }

    /// <summary>The bound is 5 x whatever interval is actually in effect, not a pinned 150 seconds:
    /// an operator who widens the projection interval widens this with it.</summary>
    [Fact]
    public void TheBoundTracksTheIntervalInEffect()
    {
        var clock = new FixtureClock(T0);
        var ledger = new DaemonTickLedger(() => clock.Now);
        ledger.RecordTick(nameof(FleetProjectionWriter), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(2));

        clock.Advance(TimeSpan.FromMinutes(9));
        Assert.Null(DaemonWatchdog.Evaluate(ledger, clock.Now, TimeSpan.FromMinutes(2)));
        Assert.NotNull(DaemonWatchdog.Evaluate(ledger, clock.Now, TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void TheHeartbeatBody_CarriesTickCompletedAt_AndEveryServicesLastDuration()
    {
        var clock = new FixtureClock(T0);
        var ledger = new DaemonTickLedger(() => clock.Now);

        clock.Advance(TimeSpan.FromSeconds(10));
        ledger.RecordTick(nameof(WatchSweep), TimeSpan.FromMilliseconds(412.5), TimeSpan.FromSeconds(15));
        clock.Advance(TimeSpan.FromSeconds(5));
        ledger.RecordTick(nameof(FleetProjectionWriter), TimeSpan.FromSeconds(3), Interval);

        var root = JsonNode.Parse(ledger.RenderHeartbeatJson())!.AsObject();

        // tickCompletedAt is the NEWEST completion across services -- what an outside reader needs to
        // answer "is this daemon still turning over" without knowing the service list.
        Assert.Equal(T0.AddSeconds(15).ToString("O"), root["tickCompletedAt"]!.GetValue<string>());
        Assert.Equal(T0.ToString("O"), root["startedAt"]!.GetValue<string>());

        var services = root["services"]!.AsObject();
        Assert.Equal(412.5, services[nameof(WatchSweep)]!["lastTickMs"]!.GetValue<double>());
        Assert.Equal(3000, services[nameof(FleetProjectionWriter)]!["lastTickMs"]!.GetValue<double>());
        Assert.Equal(
            T0.AddSeconds(10).ToString("O"),
            services[nameof(WatchSweep)]!["completedAt"]!.GetValue<string>());
    }

    /// <summary>Before any tick has landed the heartbeat reports the process start time, never a
    /// fabricated "now" that would read as a healthy daemon to an outside reader.</summary>
    [Fact]
    public void AnEmptyLedgersHeartbeat_ReportsProcessStart_NotNow()
    {
        var clock = new FixtureClock(T0);
        var ledger = new DaemonTickLedger(() => clock.Now);
        clock.Advance(TimeSpan.FromMinutes(20));

        var root = JsonNode.Parse(ledger.RenderHeartbeatJson())!.AsObject();
        Assert.Equal(T0.ToString("O"), root["tickCompletedAt"]!.GetValue<string>());
        Assert.Empty(root["services"]!.AsObject());
    }
}
