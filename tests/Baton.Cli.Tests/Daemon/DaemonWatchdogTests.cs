using System.Text.Json.Nodes;
using Baton.Cli.Daemon;
using Baton.Status;

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

    /// <summary>The hosted services (spec/baton.md §7), with fixture cadences selected independently
    /// of production timing. The multiplier arm below raises the shortest cadence above the floor.</summary>
    private static readonly (string Service, TimeSpan Interval)[] HostedServices =
    [
        (nameof(FleetProjectionWriter), TimeSpan.FromSeconds(30)),
        (nameof(WatchSweep), TimeSpan.FromSeconds(30)),
        (nameof(QueueSchedulerService), TimeSpan.FromSeconds(30)),
        (nameof(VendorUsageHarvester), TimeSpan.FromSeconds(30)),
        (nameof(DeliveryPoller), TimeSpan.FromSeconds(300)),
        (nameof(RoomRetentionSweep), TimeSpan.FromSeconds(300)),
        (nameof(DeadPumpProbe), TimeSpan.FromSeconds(300)),
    ];

    private static void TickAll(DaemonTickLedger ledger, IEnumerable<(string Service, TimeSpan Interval)> services)
    {
        foreach (var (service, interval) in services)
        {
            ledger.RecordTick(service, TimeSpan.FromMilliseconds(100), interval);
        }
    }

    /// <summary>
    /// #2082: the 2026-09-08 shape. Every service's last tick lands in one window, then nothing —
    /// and the trip has to come at twice the shortest interval, not at 5 x the projection interval
    /// (150 s here) and nowhere near the 778 s the incident's verdict line quoted for DeliveryPoller.
    /// </summary>
    [Fact]
    public void AllServicesStalling_TripsAtTwiceTheShortestInterval()
    {
        var clock = new FixtureClock(T0);
        var (watchdog, ledger, log, exits) = Build(clock);
        TickAll(ledger, HostedServices.Select(s =>
            (s.Service, s.Interval < TimeSpan.FromSeconds(45) ? TimeSpan.FromSeconds(45) : s.Interval)));

        // At the bound itself: not yet. The arm is "longer than", so a service completing exactly on
        // its second period is still on time.
        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.False(watchdog.CheckOnce());
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.False(watchdog.CheckOnce());
        Assert.Empty(exits);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(watchdog.CheckOnce());
        Assert.Equal(DaemonWatchdog.HungExitCode, Assert.Single(exits));

        var line = Assert.Single(log);
        Assert.Contains("in 91s", line);
        Assert.Contains("2 x the shortest service interval (45s)", line);
        Assert.DoesNotContain("projection interval", line);
    }

    /// <summary>The control for the arm above, and the ruling the class doc states: one service
    /// stalling beside siblings that keep ticking never trips, however long it stays silent. Without this
    /// the arm above would pass against a watchdog keyed on the QUIETEST service.</summary>
    [Fact]
    public void OtherServicesTicking_WhileOneStalls_NeverTrips()
    {
        var clock = new FixtureClock(T0);
        var (watchdog, ledger, _, exits) = Build(clock);
        TickAll(ledger, HostedServices);

        // Two hours: only DeliveryPoller falls silent -- longer than either arm's bound many times
        // over -- while its siblings keep completing.
        var healthy = HostedServices.Where(s => s.Service != nameof(DeliveryPoller)).ToArray();
        for (var i = 0; i < 240; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(30));
            TickAll(ledger, healthy);
            Assert.False(watchdog.CheckOnce());
        }

        Assert.Empty(exits);
    }

    /// <summary>The floor: a 5 s service does not make the fleet arm a 10 s trigger — a GC pause
    /// would then cost a restart, which today kills every queue-launched lane.</summary>
    [Fact]
    public void TheFleetArm_NeverTripsInsideTheFloor_WhateverTheShortestInterval()
    {
        Assert.Equal(DaemonWatchdog.FleetSilenceFloor, DaemonWatchdog.FleetSilenceLimit(TimeSpan.FromSeconds(5)));
        Assert.Equal(TimeSpan.FromSeconds(60), DaemonWatchdog.FleetSilenceLimit(TimeSpan.FromSeconds(30)));
        Assert.Equal(TimeSpan.FromMinutes(4), DaemonWatchdog.FleetSilenceLimit(TimeSpan.FromMinutes(2)));
        Assert.Null(DaemonWatchdog.FleetSilenceLimit(null));

        var clock = new FixtureClock(T0);
        var (watchdog, ledger, _, exits) = Build(clock);
        ledger.RecordTick(nameof(WatchSweep), TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(5));

        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.False(watchdog.CheckOnce());
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(watchdog.CheckOnce());
        Assert.Single(exits);
    }

    /// <summary>The supervision thread's own cadence never stretches past half the floor, or a
    /// projection interval of five minutes would make the 60 s arm a five-minute one in practice.</summary>
    [Fact]
    public void TheWakeCadence_FollowsTheProjectionInterval_ButNeverPastHalfTheFloor()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), DaemonWatchdog.WakeCadence(TimeSpan.FromSeconds(10)));
        Assert.Equal(TimeSpan.FromSeconds(30), DaemonWatchdog.WakeCadence(TimeSpan.FromSeconds(30)));
        Assert.Equal(TimeSpan.FromSeconds(30), DaemonWatchdog.WakeCadence(TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void ATickThatNeverCompletes_TripsTheExitPath()
    {
        var clock = new FixtureClock(T0);
        var (watchdog, ledger, log, exits) = Build(clock);

        // One healthy round first, so the trip below is about the SILENCE that follows and not about
        // a ledger that never held anything. Shortest interval 15 s, so the fleet arm sits at its
        // 60 s floor -- well inside the projection arm's 150 s, which is the whole point of #2082.
        ledger.RecordTick(nameof(FleetProjectionWriter), TimeSpan.FromSeconds(2), Interval);
        ledger.RecordTick(nameof(WatchSweep), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(15));

        clock.Advance(DaemonWatchdog.FleetSilenceFloor);
        Assert.False(watchdog.CheckOnce());
        Assert.Empty(exits);

        // ... and one wake past the floor, with still nothing completing.
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

    /// <summary>
    /// 2026-09-06 review, finding D: the trip sequence's ORDER, which is the whole finding — why that
    /// order and not another is on <see cref="DaemonWatchdog.CheckOnce"/>, not restated here. Asserted
    /// as a sequence rather than four presence checks, because an assertion that each step merely
    /// happened cannot discriminate the defect.
    /// </summary>
    [Fact]
    public void TheTripSequence_ArmsTheKillFallbackAndWritesTheVerdictFile_BeforeTouchingTheConsole()
    {
        var clock = new FixtureClock(T0);
        var ledger = new DaemonTickLedger(() => clock.Now);
        var order = new List<string>();
        var watchdog = new DaemonWatchdog(
            ledger, () => clock.Now, () => Interval,
            line => order.Add($"log:{line[..12]}"),
            code => order.Add($"exit:{code}"),
            verdict => order.Add($"file:{verdict[..12]}"),
            () => order.Add("arm"));

        clock.Advance(Interval * (DaemonWatchdog.MissedTickAllowance + 1));
        Assert.True(watchdog.CheckOnce());

        Assert.Equal(
            ["arm", "file:DaemonWatchd", "log:DaemonWatchd", $"exit:{DaemonWatchdog.HungExitCode}"],
            order);
    }

    /// <summary>The control for the arm above: a healthy daemon arms no killer and writes no verdict
    /// file. Without it, a watchdog that armed a 10-second <c>Process.Kill()</c> on every pass would
    /// pass the ordering test and take the daemon down forever.</summary>
    [Fact]
    public void AHealthyDaemon_ArmsNoKiller_AndWritesNoVerdictFile()
    {
        var clock = new FixtureClock(T0);
        var ledger = new DaemonTickLedger(() => clock.Now);
        var order = new List<string>();
        var watchdog = new DaemonWatchdog(
            ledger, () => clock.Now, () => Interval,
            _ => order.Add("log"), code => order.Add($"exit:{code}"),
            _ => order.Add("file"), () => order.Add("arm"));

        for (var i = 0; i < 20; i++)
        {
            clock.Advance(Interval);
            ledger.RecordTick(nameof(FleetProjectionWriter), TimeSpan.FromSeconds(1), Interval);
            Assert.False(watchdog.CheckOnce());
        }

        Assert.Empty(order);
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

    /// <summary>The projection arm's bound is 5 x whatever interval is actually in effect, not a pinned
    /// 150 seconds: an operator who widens the projection interval widens this with it. Three minutes
    /// of silence sits inside the fleet arm too (2 x 2 min), so it is the projection arm alone that
    /// separates the two calls.</summary>
    [Fact]
    public void TheProjectionArmsBound_TracksTheIntervalInEffect()
    {
        var clock = new FixtureClock(T0);
        var ledger = new DaemonTickLedger(() => clock.Now);
        ledger.RecordTick(nameof(FleetProjectionWriter), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(2));

        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Null(DaemonWatchdog.Evaluate(ledger, clock.Now, TimeSpan.FromMinutes(2)));
        var line = DaemonWatchdog.Evaluate(ledger, clock.Now, TimeSpan.FromSeconds(30));
        Assert.NotNull(line);
        Assert.Contains("5 x the 30s projection interval", line);
    }

    /// <summary>#2082: the verdict preserves capture time even when its write is delayed.</summary>
    [Fact]
    public void TheVerdict_CarriesTheHostLoadSampleTakenAtTheTrip()
    {
        var clock = new FixtureClock(T0);
        var ledger = new DaemonTickLedger(() => clock.Now);
        var log = new List<string>();
        var sampledAt = new List<DateTimeOffset>();
        var writtenAt = new List<DateTimeOffset>();
        var watchdog = new DaemonWatchdog(
            ledger, () => clock.Now, () => Interval, log.Add, _ => { },
            writeVerdictFile: _ =>
            {
                clock.Advance(TimeSpan.FromMinutes(14));
                writtenAt.Add(clock.Now);
            },
            sampleLoad: now =>
            {
                sampledAt.Add(now);
                return new HostLoadSample(now, 412, 3, 48L * 1024 * 1024, 96L * 1024 * 1024);
            });
        ledger.RecordTick(nameof(WatchSweep), TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(15));

        clock.Advance(TimeSpan.FromSeconds(90));
        Assert.True(watchdog.CheckOnce());

        Assert.Equal(T0.AddSeconds(90), Assert.Single(sampledAt));
        Assert.Equal(T0.AddSeconds(90).AddMinutes(14), Assert.Single(writtenAt));
        Assert.Contains($"sampled at {T0.AddSeconds(90):O}", Assert.Single(log));
        Assert.Contains("thread pool 412 pending on 3 threads, GC heap 48 MiB, working set 96 MiB", Assert.Single(log));
    }

    [Fact]
    public void TheHostLoadSample_RoundTripsThroughItsJson_AndAnOlderBodyReadsAsNoSample()
    {
        var sample = new HostLoadSample(T0.AddSeconds(42), 17, 9, 123_456_789, 987_654_321);

        var body = JsonNode.Parse(sample.ToJson().ToJsonString());
        Assert.Equal(sample, HostLoadSample.FromJson(body));

        // A heartbeat written before #2082 has no hostLoad at all; a half-written one is no better.
        Assert.Null(HostLoadSample.FromJson(null));
        Assert.Null(HostLoadSample.FromJson(JsonNode.Parse("""{"sampledAt":"2026-09-08T03:12:42+00:00"}""")));

        var live = HostLoadSample.Capture(T0);
        Assert.Equal(T0, live.SampledAt);
        Assert.True(live.GcTotalMemoryBytes > 0);
        Assert.True(live.WorkingSetBytes > 0);
    }

    [Theory]
    [InlineData("sampledAt", "42")]
    [InlineData("sampledAt", "\"invalid\"")]
    [InlineData("threadPoolPendingWorkItems", "\"412\"")]
    [InlineData("threadPoolThreads", "1.5")]
    [InlineData("threadPoolThreads", "2147483648")]
    [InlineData("gcTotalMemoryBytes", "{}")]
    [InlineData("workingSetBytes", "[]")]
    [InlineData("workingSetBytes", "null")]
    public void MalformedHostLoadFields_ReadAsNoSample(string field, string value)
    {
        var body = new HostLoadSample(T0, 412, 3, 48, 96).ToJson();
        body[field] = JsonNode.Parse(value);
        Assert.Null(HostLoadSample.FromJson(JsonNode.Parse(body.ToJsonString())));
    }

    /// <summary>Both polarities of the over-interval line <see cref="DaemonTickLedger.RecordTick"/>
    /// emits (#1981) — the quiet half is the control that keeps `daemon.log` worth reading.</summary>
    [Fact]
    public void ATickLongerThanItsInterval_IsLogged_AndAFastOneIsNot()
    {
        var clock = new FixtureClock(T0);
        var lines = new List<string>();
        var ledger = new DaemonTickLedger(() => clock.Now, lines.Add);

        ledger.RecordTick(nameof(FleetProjectionWriter), TimeSpan.FromSeconds(29), Interval);
        Assert.Empty(lines);

        ledger.RecordTick(nameof(FleetProjectionWriter), TimeSpan.FromSeconds(47.5), Interval);
        var line = Assert.Single(lines);
        Assert.Contains(nameof(FleetProjectionWriter), line);
        Assert.Contains("47.5s", line);
        Assert.Contains("30s interval", line);
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

        var load = new HostLoadSample(clock.Now, 5, 12, 40_000_000, 60_000_000);
        var root = JsonNode.Parse(ledger.RenderHeartbeatJson(load))!.AsObject();

        // tickCompletedAt is the NEWEST completion across services, so a reader needs no knowledge of
        // which services exist (DaemonTickLedger's doc has why that field is shaped that way).
        Assert.Equal(T0.AddSeconds(15).ToString("O"), root["tickCompletedAt"]!.GetValue<string>());
        Assert.Equal(T0.ToString("O"), root["startedAt"]!.GetValue<string>());

        var services = root["services"]!.AsObject();
        Assert.Equal(412.5, services[nameof(WatchSweep)]!["lastTickMs"]!.GetValue<double>());
        Assert.Equal(3000, services[nameof(FleetProjectionWriter)]!["lastTickMs"]!.GetValue<double>());
        Assert.Equal(
            T0.AddSeconds(10).ToString("O"),
            services[nameof(WatchSweep)]!["completedAt"]!.GetValue<string>());

        // #2082: the load sample rides beside the services, under the field name spec/baton.md §7 states.
        Assert.Equal(load, HostLoadSample.FromJson(root["hostLoad"]));
        Assert.Equal(TimeSpan.FromSeconds(15), ledger.ShortestInterval());
    }

    /// <summary>Before any tick has landed the heartbeat reports the process start time, never a
    /// fabricated "now" that would read as a healthy daemon to an outside reader.</summary>
    [Fact]
    public void AnEmptyLedgersHeartbeat_ReportsProcessStart_NotNow()
    {
        var clock = new FixtureClock(T0);
        var ledger = new DaemonTickLedger(() => clock.Now);
        clock.Advance(TimeSpan.FromMinutes(20));

        var root = JsonNode.Parse(ledger.RenderHeartbeatJson(HostLoadSample.Capture(clock.Now)))!.AsObject();
        Assert.Equal(T0.ToString("O"), root["tickCompletedAt"]!.GetValue<string>());
        Assert.Empty(root["services"]!.AsObject());
        Assert.Null(ledger.ShortestInterval());
    }

    /// <summary>2026-09-06 round-3 review: the PRODUCTION verdict writer, driven against a temp
    /// <c>BATON_HOME</c> — every arm above passes a recorder for that seam instead, and
    /// <see cref="DaemonWatchdog.WriteVerdictFile"/>'s own doc has what that left uncovered and why it
    /// matters. This arm is the covering one: given a home, the verdict lands under it.</summary>
    [Fact]
    public void TheProductionVerdictWriter_LandsUnderTheFleetDirectoryOfTheHomeItIsGiven()
    {
        var tempHome = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = tempHome });
        try
        {
            // No directory pre-created on purpose: on a fresh machine the watchdog trips before
            // anything else has made {Root}/fleet, so the writer's own CreateDirectory is load-bearing.
            var expected = Path.Combine(
                tempHome, BatonPaths.FleetDirectoryName, BatonPaths.FleetWatchdogVerdictFileName);
            Assert.False(File.Exists(expected));

            DaemonWatchdog.WriteVerdictFile("FleetProjectionWriter has not completed a tick");

            Assert.True(File.Exists(expected), $"the verdict must land at {expected}");
            var line = Assert.Single(File.ReadAllLines(expected));
            // Timestamped and naming the loop: an undated line, or one that only says "hung", is what
            // the console already effectively said on 2026-09-06.
            Assert.Contains("FleetProjectionWriter has not completed a tick", line);
            Assert.True(DateTimeOffset.TryParse(line.Split(' ')[0], out _),
                $"the verdict line must open with a parseable timestamp, not: {line}");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempHome);
        }
    }

    /// <summary>The other polarity, and the reason the writer is best-effort: a write that cannot
    /// land must not throw. It runs on the watchdog's own dedicated thread, between arming the kill
    /// timer and calling Exit — an escaping exception there would take out the recovery it is part
    /// of. A directory sitting where the file goes is the cheapest real failure to stage.</summary>
    [Fact]
    public void TheProductionVerdictWriter_DoesNotThrow_WhenTheFileCannotBeWritten()
    {
        var tempHome = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = tempHome });
        try
        {
            var blocked = Path.Combine(
                tempHome, BatonPaths.FleetDirectoryName, BatonPaths.FleetWatchdogVerdictFileName);
            Directory.CreateDirectory(blocked);

            DaemonWatchdog.WriteVerdictFile("FleetProjectionWriter has not completed a tick");

            Assert.True(Directory.Exists(blocked));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempHome);
        }
    }

    private static string CreateTempHome()
    {
        var tempHome = Path.Combine(
            Path.GetTempPath(), "baton_daemon_watchdog_test_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempHome);
        return tempHome;
    }
}
