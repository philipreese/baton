using System.Diagnostics;
using System.Text.Json;
using Baton.Cli.Daemon;
using Baton.CrashTestHost;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Mutation;
using Baton.Status;
using Baton.Store;

// M10 Phase 4 (issue #72): a small, test-only pump host standing in for Baton.Cli, which is still a
// stub. Baton.Tests spawns this as a real OS process, waits for a specific durable fact to
// appear in the log, then kills it — exercising MutationInterface.StartWorkflowAsync against real
// Core dispatch, then reconciling from a real, killed-mid-run log via a second, in-process run.
//
// args: <pausePoint> <roomDirectory> <artifactsRoot> <logPath> <pauseSignalPath> <cancelSignalPath>
//   pausePoint: "none" | "before-dispatch" | "after-dispatch" (see DispatchPausePoint).
//
// #2082: a second, unrelated use of the same killable host -- the two arms of Baton.Tests'
// DetachedProcessTests. `spawn-contained <pidFile>` starts a long sleeper through BatonTask (the
// job-contained path every worker takes); `spawn-detached <pidFile>` starts one through
// DetachedProcess (the path a queue-launched lane takes). Either way the host writes the sleeper's
// pid to <pidFile> and then waits to be killed; the test asserts the sleeper's fate.
// #2190: when a copy of this apphost is named git/git.exe, the read-only probe argv below make a
// hermetic native executable for the public dispatch provenance route. #2030 reuses that executable
// for a diff probe which exits after starting a sleeper that inherits its process handles; the PID
// file named by BATON_CRASH_TEST_SLEEPER_PID_FILE lets the E2E prove the descendant is gone. No shell
// or callback seam is involved: production starts the copied apphost directly.
const string HermeticHead = "0123456789abcdef0123456789abcdef01234567";
if (args is ["config", "--get", "remote.origin.url"])
{
    await Console.Out.WriteLineAsync("https://github.com/aer-works/baton.git");
    return 0;
}
if (args is ["rev-parse", "--abbrev-ref", "HEAD"])
{
    await Console.Out.WriteLineAsync("2190-verified-pr-ownership");
    return 0;
}
if (args is ["rev-parse", "HEAD"])
{
    await Console.Out.WriteLineAsync(HermeticHead);
    return 0;
}
if (Environment.GetEnvironmentVariable("BATON_CRASH_TEST_OPEN_PR") == "1"
    && args is ["pr", "list", "--head", _, "--json", "number,headRefOid"])
{
    var headStart = new ProcessStartInfo("git")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    headStart.ArgumentList.Add("rev-parse");
    headStart.ArgumentList.Add("HEAD");
    using var headProcess = Process.Start(headStart)
        ?? throw new InvalidOperationException("Could not start git for the hermetic gh fixture.");
    var head = (await headProcess.StandardOutput.ReadToEndAsync()).Trim();
    await headProcess.WaitForExitAsync();
    if (headProcess.ExitCode != 0)
    {
        return headProcess.ExitCode;
    }

    await Console.Out.WriteLineAsync($$"""[{"number":2362,"headRefOid":"{{head}}"}]""");
    return 0;
}
if (Environment.GetEnvironmentVariable("BATON_CRASH_TEST_DELIVERY_PROBE_SLEEPER") == "1"
    && args is ["-c", "credential.interactive=false", "ls-remote", "--exit-code", "--heads", "origin", "2190-verified-pr-ownership"])
{
    // #2309: DeliveryVerifier's real surviving git probe must be job-contained even though this
    // child exits normally while its inherited-handle sleeper would otherwise keep CaptureAsync open.
    var sleeperStart = new ProcessStartInfo("ping.exe")
    {
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    sleeperStart.ArgumentList.Add("-n");
    sleeperStart.ArgumentList.Add("9999");
    sleeperStart.ArgumentList.Add("127.0.0.1");
    using var sleeper = Process.Start(sleeperStart)
        ?? throw new InvalidOperationException("Could not start the delivery-probe inherited-handle sleeper.");
    if (Environment.GetEnvironmentVariable("BATON_CRASH_TEST_SLEEPER_PID_FILE") is { Length: > 0 } pidFile)
    {
        WritePidAtomically(pidFile, sleeper.Id);
    }

    await Console.Out.WriteLineAsync($"{HermeticHead}\trefs/heads/2190-verified-pr-ownership");
    return 0;
}
if (Environment.GetEnvironmentVariable("BATON_CRASH_TEST_DELIVERY_PROBE_SLEEPER") == "1"
    && args is ["-c", "credential.interactive=false", "ls-remote", "--heads", "origin", "2190-verified-pr-ownership"])
{
    // The delivery stamp makes a second, separate remote-head observation after the check.
    // A missing second answer must not inherit the earlier passing probe's authority.
    if (Environment.GetEnvironmentVariable("BATON_CRASH_TEST_SECOND_REMOTE_OBSERVATION_MISSING") == "1")
    {
        return 0;
    }
    if (Environment.GetEnvironmentVariable("BATON_CRASH_TEST_SECOND_REMOTE_OBSERVATION_CHANGED") == "1")
    {
        await Console.Out.WriteLineAsync("fedcba9876543210fedcba9876543210fedcba98\trefs/heads/2190-verified-pr-ownership");
        return 0;
    }
    // Keep the positive provenance control truthful without launching another sleeper.
    await Console.Out.WriteLineAsync($"{HermeticHead}\trefs/heads/2190-verified-pr-ownership");
    return 0;
}
if (Environment.GetEnvironmentVariable("BATON_CRASH_TEST_DELIVERY_PROBE_SLEEPER") == "1"
    && args is ["rev-parse", "origin/2190-verified-pr-ownership"])
{
    await Console.Out.WriteLineAsync(HermeticHead);
    return 0;
}
if (Environment.GetEnvironmentVariable("BATON_CRASH_TEST_DELIVERY_PROBE_SLEEPER") == "1"
    && (args is ["-c", "credential.interactive=false", "fetch", "origin", "+refs/heads/2190-verified-pr-ownership:refs/remotes/origin/2190-verified-pr-ownership"]
        or ["merge-base", "--is-ancestor", HermeticHead, HermeticHead]))
{
    return 0;
}
if (args is ["rev-parse", "--path-format=absolute", "--git-common-dir"])
{
    await Console.Out.WriteLineAsync("C:\\fixture\\.git");
    return 0;
}
if (args is ["pr", "view", "2304", "--repo", "aer-works/baton", "--json", "state,headRefName,headRefOid"])
{
    if (Environment.GetEnvironmentVariable("BATON_CRASH_TEST_GH_MARKER") is { Length: > 0 } marker)
    {
        await File.WriteAllTextAsync(marker, Environment.ProcessPath ?? string.Empty);
    }
    await Console.Out.WriteLineAsync(
        $$"""{"state":"OPEN","headRefName":"2190-verified-pr-ownership","headRefOid":"{{HermeticHead}}"}""");
    return 0;
}
if (args.Contains("diff", StringComparer.Ordinal))
{
    var sleeperStart = new ProcessStartInfo("ping.exe")
    {
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    sleeperStart.ArgumentList.Add("-n");
    sleeperStart.ArgumentList.Add("9999");
    sleeperStart.ArgumentList.Add("127.0.0.1");
    using var sleeper = Process.Start(sleeperStart)
        ?? throw new InvalidOperationException("Could not start the inherited-handle sleeper.");
    if (Environment.GetEnvironmentVariable("BATON_CRASH_TEST_SLEEPER_PID_FILE") is { Length: > 0 } pidFile)
    {
        WritePidAtomically(pidFile, sleeper.Id);
    }

    await Console.Out.WriteLineAsync("1\t0\tfixture.txt");
    return 0;
}

if (args.Length == 2 && args[0] is "spawn-contained" or "spawn-detached")
{
    return await SpawnArmAsync(args[0], args[1]);
}

if (args is ["daemon-loop-threadpool-control"])
{
    return await RunDaemonLoopThreadPoolControlAsync();
}
if (args is ["daemon-loop-registry-control"])
{
    return await RunDaemonLoopRegistryControlAsync();
}
if (args is ["daemon-loop-child-control"])
{
    return await RunDaemonLoopChildControlAsync();
}
if (args is ["daemon-loop-child-wait"])
{
    await Console.Out.WriteLineAsync("ready");
    _ = await Console.In.ReadLineAsync();
    return 0;
}

if (args.Length != 6)
{
    await Console.Error.WriteLineAsync(
        "usage: <pausePoint> <roomDirectory> <artifactsRoot> <logPath> <pauseSignalPath> <cancelSignalPath>");
    return 1;
}

var pausePoint = args[0] switch
{
    "none" => DispatchPausePoint.None,
    "before-dispatch" => DispatchPausePoint.BeforeDispatch,
    "after-dispatch" => DispatchPausePoint.AfterDispatch,
    _ => throw new ArgumentException($"Unknown pausePoint '{args[0]}'."),
};
var roomDirectory = args[1];
var artifactsRoot = args[2];
var logPath = args[3];
var pauseSignalPath = args[4];
var cancelSignalPath = args[5];

// The worker only needs to be genuinely long-running for the no-pause (orphan) scenario, where
// this run's own real timing — not a decorator pause — is what leaves it still executing when
// killed. Both paused scenarios never let a real dispatch reach the worker at all (before-dispatch)
// or let it run to a real, fast, natural exit before pausing (after-dispatch).
var workerKind = pausePoint == DispatchPausePoint.None ? ScenarioWorker.LongSleep : ScenarioWorker.QuickSuccess;
var (snapshot, bindings) = Scenarios.Build(workerKind);

await using var writer = new FlowEventLogWriter(logPath);
var reader = new FlowEventLogReader(logPath);
var dispatcher = new PausableCoreDispatcher(new CoreDispatcher(writer, writer), pausePoint, pauseSignalPath);
var inFlightExecutions = new InFlightExecutionRegistry();

// Fire-and-forget: harmless if this process is killed before cancelSignalPath ever appears (the
// common case for every scenario except the unfulfilled-cancellation one), since nothing here has
// any effect until that file exists.
_ = WatchForCancelSignalAsync(cancelSignalPath, reader, inFlightExecutions);

await MutationInterface.StartWorkflowAsync(
    Scenarios.WorkflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher,
    inFlightExecutions: inFlightExecutions);

return 0;

// The #2082 arms described at the top of this file. The sleeper is the same `ping -n 9999` the
// process-tree tests use: long enough that "still alive" is never in question inside a test.
static async Task<int> SpawnArmAsync(string mode, string pidFile)
{
    string[] sleeperArgs = ["-n", "9999", "127.0.0.1"];
    if (mode == "spawn-detached")
    {
        // The convenience overload, with nothing redirected: the sleeper's output is not wanted, and
        // DetachedProcess refuses the one-stream shape that would lose it silently.
        using var child = Baton.Core.DetachedProcess.Start("ping", startInfo =>
        {
            foreach (var arg in sleeperArgs)
            {
                startInfo.ArgumentList.Add(arg);
            }
        });
        WritePidAtomically(pidFile, child.Id);

        // Killed from outside; never returns on its own.
        await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
        return 0;
    }

    using var task = new Baton.Core.BatonTask("ping", sleeperArgs);
    task.EventRaised += (_, e) =>
    {
        if (e.Kind == Baton.Core.BatonTaskEventKind.Started)
        {
            WritePidAtomically(pidFile, e.Pid);
        }
    };

    // Blocks until the sleeper exits, which the test never lets happen: it kills this host first.
    task.Run();
    return 0;
}

// #2324: isolated-process control for the shared-loop seam. Restricting the pool would corrupt the
// test runner itself, so this lives in the killable host. Two independent loop continuations queue
// behind the only two workers, both record the same virtual-time miss, and both recover on the next
// tick after the workers are released. No wall-clock cadence or sub-minute sleep is asserted.
static async Task<int> RunDaemonLoopThreadPoolControlAsync()
{
    ThreadPool.GetMinThreads(out _, out var minimumIo);
    ThreadPool.GetMaxThreads(out _, out var maximumIo);
    if (!ThreadPool.SetMinThreads(2, minimumIo) || !ThreadPool.SetMaxThreads(2, maximumIo))
    {
        await Console.Error.WriteLineAsync("could not constrain the isolated worker pool");
        return 2;
    }

    var clock = new ControlTimeProvider();
    var ledger = new DaemonTickLedger(() => DateTimeOffset.UnixEpoch);
    var releaseTicks = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var ticksEntered = new CountdownEvent(2);
    using var blockersEntered = new CountdownEvent(2);
    using var releaseBlockers = new ManualResetEventSlim();
    using var firstStop = new CancellationTokenSource();
    using var secondStop = new CancellationTokenSource();
    var observations = new List<object>();
    var observationGate = new object();

    Task StartLoopAsync(string service, CancellationTokenSource stop)
    {
        var pass = 0;
        var driver = new DaemonLoopDriver(
            ledger,
            clock,
            (_, _) =>
            {
                if (pass >= 2)
                {
                    stop.Cancel();
                    return Task.FromCanceled(stop.Token);
                }

                return Task.CompletedTask;
            });

        return driver.RunAsync(
            service,
            async _ =>
            {
                pass++;
                if (pass == 1)
                {
                    using (DaemonLoopDriver.EnterPhase("runtime-continuation"))
                    {
                        ticksEntered.Signal();
                        await releaseTicks.Task.ConfigureAwait(false);
                    }
                }

                return TimeSpan.FromSeconds(10);
            },
            () => TimeSpan.FromSeconds(10),
            _ => TimeSpan.FromSeconds(10),
            _ => { },
            stop.Token,
            () =>
            {
                var tick = ledger.Snapshot().Single(candidate => candidate.Service == service);
                lock (observationGate)
                {
                    observations.Add(new
                    {
                        service,
                        elapsedMs = tick.Elapsed.TotalMilliseconds,
                        phases = tick.LatePhases.Select(phase => phase.Name).ToArray(),
                    });
                }
            });
    }

    var first = StartLoopAsync("first", firstStop);
    var second = StartLoopAsync("second", secondStop);
    if (!ticksEntered.Wait(TimeSpan.FromSeconds(5)))
    {
        return 3;
    }

    for (var index = 0; index < 2; index++)
    {
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            blockersEntered.Signal();
            releaseBlockers.Wait();
        }, null);
    }

    if (!blockersEntered.Wait(TimeSpan.FromSeconds(5)))
    {
        return 4;
    }

    clock.Advance(TimeSpan.FromSeconds(25));
    releaseTicks.SetResult();
    var continuationsWereStarved = !first.IsCompleted && !second.IsCompleted;
    releaseBlockers.Set();
    await Task.WhenAll(first, second).ConfigureAwait(false);

    await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new
    {
        continuationsWereStarved,
        observations,
    }));
    return continuationsWereStarved ? 0 : 5;
}

// The two differential controls below use the real blocking primitive they name. Only the affected
// loop advances across the virtual 25-second interval; the unrelated loop completes at zero. That
// signature is deliberately narrower than the thread-pool control, where both continuations miss.
static async Task<int> RunDaemonLoopRegistryControlAsync()
{
    var registry = Path.Combine(Path.GetTempPath(), $"baton-registry-control-{Guid.NewGuid():N}.jsonl");
    await File.WriteAllTextAsync(registry, string.Empty);
    try
    {
        var clock = new ControlTimeProvider();
        var ledger = new DaemonTickLedger(() => DateTimeOffset.UnixEpoch);
        using var slowStop = new CancellationTokenSource();
        using var fastStop = new CancellationTokenSource();
        using var attempted = new ManualResetEventSlim();
        using var acquired = new ManualResetEventSlim();
        var slowDriver = new DaemonLoopDriver(ledger, clock, (_, _) => CancelControlDelayAsync(slowStop));
        var fastDriver = new DaemonLoopDriver(ledger, clock, (_, _) => CancelControlDelayAsync(fastStop));
        Task? slowRun = null;
        Task? fastRun = null;

        MutexGuardedFileLock.RunUnderLock(registry, "baton-room-registry", TimeSpan.FromSeconds(5), () =>
        {
            slowRun = slowDriver.RunAsync(
                "registry",
                async _ =>
                {
                    using (DaemonLoopDriver.EnterPhase("room-registry"))
                    {
                        await Task.Run(() =>
                        {
                            attempted.Set();
                            MutexGuardedFileLock.RunUnderLock(
                                registry,
                                "baton-room-registry",
                                TimeSpan.FromSeconds(5),
                                () =>
                                {
                                    acquired.Set();
                                });
                        });
                    }

                    return TimeSpan.FromSeconds(10);
                },
                () => TimeSpan.FromSeconds(10),
                _ => TimeSpan.FromSeconds(10),
                _ => { },
                slowStop.Token);

            // Queue a second real lock contender with an explicit pre-wait signal. The contender's
            // action cannot run while this outer action owns the same named mutex.
            if (!attempted.Wait(TimeSpan.FromSeconds(5)) || acquired.IsSet)
            {
                throw new InvalidOperationException("Registry control did not establish contention.");
            }

            fastRun = fastDriver.RunAsync(
                "unrelated",
                _ => Task.FromResult(TimeSpan.FromSeconds(10)),
                () => TimeSpan.FromSeconds(10),
                _ => TimeSpan.FromSeconds(10),
                _ => { },
                fastStop.Token);
            clock.Advance(TimeSpan.FromSeconds(25));
        });

        await Task.WhenAll(slowRun!, fastRun!).ConfigureAwait(false);
        if (!acquired.IsSet)
        {
            return 6;
        }

        return await WriteDifferentialControlResultAsync(ledger).ConfigureAwait(false);
    }
    finally
    {
        Baton.Tests.Shared.FileCleanup.Delete(registry);
    }
}

static async Task<int> RunDaemonLoopChildControlAsync()
{
    var clock = new ControlTimeProvider();
    var ledger = new DaemonTickLedger(() => DateTimeOffset.UnixEpoch);
    using var slowStop = new CancellationTokenSource();
    using var fastStop = new CancellationTokenSource();
    using var childReady = new ManualResetEventSlim();
    var releaseChild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var slowDriver = new DaemonLoopDriver(ledger, clock, (_, _) => CancelControlDelayAsync(slowStop));
    var fastDriver = new DaemonLoopDriver(ledger, clock, (_, _) => CancelControlDelayAsync(fastStop));

    var slowRun = slowDriver.RunAsync(
        "child",
        async cancellationToken =>
        {
            using (DaemonLoopDriver.EnterPhase("child-process"))
            {
                var start = new ProcessStartInfo("dotnet")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                };
                start.ArgumentList.Add(typeof(Scenarios).Assembly.Location);
                start.ArgumentList.Add("daemon-loop-child-wait");
                using var child = Process.Start(start)
                    ?? throw new InvalidOperationException("Could not start child-process control.");
                var ready = await child.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (ready != "ready")
                {
                    throw new InvalidOperationException($"Child-process control reported '{ready}'.");
                }

                childReady.Set();
                await releaseChild.Task.ConfigureAwait(false);
                await child.StandardInput.WriteLineAsync("release").ConfigureAwait(false);
                child.StandardInput.Close();
                await child.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }

            return TimeSpan.FromSeconds(10);
        },
        () => TimeSpan.FromSeconds(10),
        _ => TimeSpan.FromSeconds(10),
        _ => { },
        slowStop.Token);

    if (!childReady.Wait(TimeSpan.FromSeconds(5)))
    {
        return 7;
    }

    var fastRun = fastDriver.RunAsync(
        "unrelated",
        _ => Task.FromResult(TimeSpan.FromSeconds(10)),
        () => TimeSpan.FromSeconds(10),
        _ => TimeSpan.FromSeconds(10),
        _ => { },
        fastStop.Token);
    clock.Advance(TimeSpan.FromSeconds(25));
    releaseChild.SetResult();
    await Task.WhenAll(slowRun, fastRun).ConfigureAwait(false);
    return await WriteDifferentialControlResultAsync(ledger).ConfigureAwait(false);
}

static Task CancelControlDelayAsync(CancellationTokenSource stop)
{
    stop.Cancel();
    return Task.FromCanceled(stop.Token);
}

static async Task<int> WriteDifferentialControlResultAsync(DaemonTickLedger ledger)
{
    var observations = ledger.Snapshot()
        .OrderBy(tick => tick.Service, StringComparer.Ordinal)
        .Select(tick => new
        {
            service = tick.Service,
            elapsedMs = tick.Elapsed.TotalMilliseconds,
            phases = tick.LatePhases.Select(phase => phase.Name).ToArray(),
        })
        .ToArray();
    await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { observations }));
    return 0;
}

// Written beside and renamed into place, so the test's poll never sees the file exist while this
// host still holds it open for writing: a direct write is created empty first, and a reader that
// opens it in that window gets a sharing violation (CI run 34243412086, windows-shard-flow).
static void WritePidAtomically(string pidFile, long pid)
{
    var staging = pidFile + ".tmp";
    File.WriteAllText(staging, pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
    File.Move(staging, pidFile, overwrite: true);
}

static async Task WatchForCancelSignalAsync(
    string cancelSignalPath, IEventLogReader reader, InFlightExecutionRegistry inFlightExecutions)
{
    try
    {
        while (true)
        {
            try
            {
                if (File.Exists(cancelSignalPath))
                {
                    break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Transient filesystem exception while checking for cancelSignalPath existence under CI load.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10)).ConfigureAwait(false);
        }

        // Resolves from this process's own log rather than a passed-in argument: the ExecutionId is
        // minted fresh by MutationInterface on every run and unknowable to the test harness in advance.
        //
        // Retries reading the log and delivering cancellation until RequestCancellationAsync succeeds
        // (returns true): PrepareExecutionAsync durably appends ExecutionRequestAccepted to flow.jsonl
        // BEFORE MutationInterface calls InFlightExecutionRegistry.Register for that execution (issue #513).
        // Under CI load, the test harness can observe ExecutionRequestAccepted and write cancel.signal
        // in that narrow window before Register runs. If the watcher only attempts cancellation once, it
        // finds ExecutionRequestAccepted in the log, calls RequestCancellationAsync before Register has
        // been called, drops the request as a no-op, and exits — hanging the test waiting for
        // CancellationRequested. Polling until RequestCancellationAsync returns true ensures the watcher
        // waits for Register to complete if it races signal file creation.
        while (true)
        {
            try
            {
                var events = await reader.ReadAllAsync().ConfigureAwait(false);
                var accepted = events.OfType<FlowEvent.ExecutionRequestAccepted>().FirstOrDefault(e => e.Request.StepId == Scenarios.StepA);
                if (accepted is not null)
                {
                    if (await inFlightExecutions.RequestCancellationAsync(accepted.Request.ExecutionId).ConfigureAwait(false))
                    {
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or FlowEventLogReadException or UnauthorizedAccessException)
            {
                // Transient file access / log-reading collision while FlowEventLogWriter is concurrently flushing or appending.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10)).ConfigureAwait(false);
        }
    }
    // Swallows any unhandled non-transient exceptions by writing details to stderr because WatchForCancelSignalAsync is
    // an unawaited background task (_ = WatchForCancelSignalAsync(...)) launched from Main. Rethrowing here would result in an
    // unobserved task exception, whereas writing to stderr ensures background watcher failures are visible in test logs
    // without silently hanging or crashing the process.
    catch (Exception ex)
    {
        await Console.Error.WriteLineAsync($"WatchForCancelSignalAsync failed: {ex}").ConfigureAwait(false);
    }
}

sealed class ControlTimeProvider : TimeProvider
{
    private long _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Volatile.Read(ref _timestamp);

    public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _timestamp, elapsed.Ticks);
}
