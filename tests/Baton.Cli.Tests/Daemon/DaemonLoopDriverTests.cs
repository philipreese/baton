using System.Diagnostics;
using System.Text.Json;
using Baton.Cli.Daemon;
using Baton.CrashTestHost;

namespace Baton.Cli.Tests.Daemon;

public sealed class DaemonLoopDriverTests
{
    [Fact]
    public async Task Isolated_thread_pool_starvation_delays_independent_loops_and_both_recover()
    {
        var host = typeof(Scenarios).Assembly.Location;
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(host);
        start.ArgumentList.Add("daemon-loop-threadpool-control");

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start control host.");
        // wait-ok: failure ceiling only; the isolated virtual-time control contains no production-cadence wait.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        var output = await outputTask;
        var error = await errorTask;

        Assert.True(process.ExitCode == 0, $"Control host exited {process.ExitCode}: {error}");
        using var document = JsonDocument.Parse(output);
        Assert.True(document.RootElement.GetProperty("continuationsWereStarved").GetBoolean());
        var observations = document.RootElement.GetProperty("observations").EnumerateArray().ToArray();
        Assert.Equal(4, observations.Length);

        foreach (var service in new[] { "first", "second" })
        {
            var serviceTicks = observations
                .Where(item => item.GetProperty("service").GetString() == service)
                .ToArray();
            Assert.Equal(2, serviceTicks.Length);
            Assert.Equal(25_000, serviceTicks[0].GetProperty("elapsedMs").GetDouble());
            Assert.Equal(
                ["runtime-continuation"],
                serviceTicks[0].GetProperty("phases").EnumerateArray().Select(item => item.GetString()));
            Assert.Equal(0, serviceTicks[1].GetProperty("elapsedMs").GetDouble());
            Assert.Empty(serviceTicks[1].GetProperty("phases").EnumerateArray());
        }
    }

    [Fact]
    public async Task Concurrent_late_ticks_keep_their_phase_attribution_separate()
    {
        var firstClock = new StepTimeProvider();
        var secondClock = new StepTimeProvider();
        var ledger = new DaemonTickLedger(() => DateTimeOffset.UnixEpoch);

        using var firstStop = new CancellationTokenSource();
        using var secondStop = new CancellationTokenSource();
        var first = new DaemonLoopDriver(
            ledger, firstClock, (_, _) => CancelDelayAsync(firstStop));
        var second = new DaemonLoopDriver(
            ledger, secondClock, (_, _) => CancelDelayAsync(secondStop));
        using var entered = new CountdownEvent(2);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstRun = first.RunAsync(
                "first",
                async _ =>
                {
                    using (DaemonLoopDriver.EnterPhase("room-registry"))
                    {
                        entered.Signal();
                        await release.Task;
                        firstClock.Advance(TimeSpan.FromSeconds(12));
                    }

                    return TimeSpan.FromSeconds(10);
                },
                () => TimeSpan.FromSeconds(10),
                _ => TimeSpan.FromSeconds(10),
                _ => { },
                firstStop.Token);
        var secondRun = second.RunAsync(
                "second",
                async _ =>
                {
                    using (DaemonLoopDriver.EnterPhase("child-process"))
                    {
                        entered.Signal();
                        await release.Task;
                        secondClock.Advance(TimeSpan.FromSeconds(13));
                    }

                    return TimeSpan.FromSeconds(10);
                },
                () => TimeSpan.FromSeconds(10),
                _ => TimeSpan.FromSeconds(10),
                _ => { },
                secondStop.Token);

        Assert.True(
            entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken),
            "Both loop ticks did not overlap at the barrier.");
        Assert.False(firstRun.IsCompleted);
        Assert.False(secondRun.IsCompleted);
        release.SetResult();
        await Task.WhenAll(firstRun, secondRun);

        var ticks = ledger.Snapshot().ToDictionary(tick => tick.Service, StringComparer.Ordinal);
        Assert.Equal(["room-registry"], ticks["first"].LatePhases.Select(phase => phase.Name));
        Assert.Equal(["child-process"], ticks["second"].LatePhases.Select(phase => phase.Name));
    }

    [Fact]
    public async Task Late_tick_phase_attribution_is_bounded()
    {
        var clock = new StepTimeProvider();
        var ledger = new DaemonTickLedger(() => DateTimeOffset.UnixEpoch);
        using var stop = new CancellationTokenSource();
        var driver = new DaemonLoopDriver(ledger, clock, (_, _) => CancelDelayAsync(stop));

        await driver.RunAsync(
            "bounded",
            _ =>
            {
                for (var index = 0; index < DaemonLoopDriver.MaxRetainedPhases + 5; index++)
                {
                    using (DaemonLoopDriver.EnterPhase($"phase-{index:D2}"))
                    {
                        clock.Advance(TimeSpan.FromSeconds(1));
                    }
                }

                return Task.FromResult(TimeSpan.FromSeconds(10));
            },
            () => TimeSpan.FromSeconds(10),
            _ => TimeSpan.FromSeconds(10),
            _ => { },
            stop.Token);

        var tick = Assert.Single(ledger.Snapshot());
        Assert.Equal(DaemonLoopDriver.MaxRetainedPhases, tick.LatePhases.Count);
        Assert.Equal("phase-00", tick.LatePhases[0].Name);
        Assert.Equal(DaemonLoopDriver.OtherPhaseName, tick.LatePhases[^1].Name);
        Assert.Equal(6, tick.LatePhases[^1].Count);
        Assert.Equal(TimeSpan.FromSeconds(6), tick.LatePhases[^1].Elapsed);
    }

    [Theory]
    [InlineData("daemon-loop-registry-control", "registry", "room-registry")]
    [InlineData("daemon-loop-child-control", "child", "child-process")]
    public async Task Isolated_blocking_controls_implicate_only_the_responsible_loop(
        string mode,
        string responsibleService,
        string expectedPhase)
    {
        var root = await RunControlHostAsync(mode);
        var observations = root.GetProperty("observations").EnumerateArray().ToArray();
        Assert.Equal(2, observations.Length);

        var responsible = Assert.Single(observations, item =>
            item.GetProperty("service").GetString() == responsibleService);
        Assert.Equal(25_000, responsible.GetProperty("elapsedMs").GetDouble());
        Assert.Equal(
            [expectedPhase],
            responsible.GetProperty("phases").EnumerateArray().Select(item => item.GetString()));

        var unrelated = Assert.Single(observations, item =>
            item.GetProperty("service").GetString() == "unrelated");
        Assert.Equal(0, unrelated.GetProperty("elapsedMs").GetDouble());
        Assert.Empty(unrelated.GetProperty("phases").EnumerateArray());
    }

    [Fact]
    public async Task Dedicated_watchdog_sample_is_attached_to_the_late_tick_that_was_in_flight()
    {
        var clock = new StepTimeProvider();
        var ledger = new DaemonTickLedger(() => DateTimeOffset.UnixEpoch);
        using var stop = new CancellationTokenSource();
        var driver = new DaemonLoopDriver(ledger, clock, (_, _) => CancelDelayAsync(stop));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var run = driver.RunAsync(
                "sampled",
                async _ =>
                {
                    using (DaemonLoopDriver.EnterPhase("queue-store"))
                    {
                        entered.SetResult();
                        await release.Task;
                    }

                    return TimeSpan.FromSeconds(10);
                },
                () => TimeSpan.FromSeconds(10),
                _ => TimeSpan.FromSeconds(10),
                _ => { },
                stop.Token);

        await entered.Task;
        clock.Advance(TimeSpan.FromSeconds(12));
        var load = new HostLoadSample(DateTimeOffset.UnixEpoch.AddSeconds(12), 40, 2, 100, 200);
        ledger.SampleActive(load);
        release.SetResult();
        await run;

        var tick = Assert.Single(ledger.Snapshot());
        var sample = Assert.Single(tick.LateSamples);
        Assert.Equal("queue-store", sample.Phase);
        Assert.Equal(TimeSpan.FromSeconds(12), sample.PhaseElapsed);
        Assert.Equal(load, sample.HostLoad);
    }

    private static Task CancelDelayAsync(CancellationTokenSource source)
    {
        source.Cancel();
        return Task.FromCanceled(source.Token);
    }

    private static async Task<JsonElement> RunControlHostAsync(string mode)
    {
        var host = typeof(Scenarios).Assembly.Location;
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(host);
        start.ArgumentList.Add(mode);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start control host.");
        // wait-ok: failure ceiling only; both controls synchronize by signals rather than elapsed wall time.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        var output = await outputTask;
        var error = await errorTask;

        Assert.True(process.ExitCode == 0, $"Control host '{mode}' exited {process.ExitCode}: {error}");
        using var document = JsonDocument.Parse(output);
        return document.RootElement.Clone();
    }

    private sealed class StepTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Volatile.Read(ref _timestamp);

        internal void Advance(TimeSpan elapsed) =>
            Interlocked.Add(ref _timestamp, elapsed.Ticks);
    }
}
