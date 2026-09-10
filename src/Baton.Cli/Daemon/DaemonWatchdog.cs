using System.Diagnostics;
using Baton.Status;
using Microsoft.Extensions.Hosting;

namespace Baton.Cli.Daemon;

/// <summary>
/// #1981 — the daemon's self-watchdog: dying loudly beats hanging quietly.
/// <para>
/// On 2026-09-06 at 14:51 the daemon stopped writing anything — the log and the projection — and
/// stayed that way for thirteen minutes with its process alive, a 6.6 MB working set,
/// and its scheduled task reporting Running. Nothing recovered it; a person did. This service exits
/// the process non-zero once the whole daemon has been silent past the bound spec/baton.md §7 ("The
/// daemon watches itself") states — two arms, either trips: <see cref="FleetSilenceMultiplier"/>
/// × the shortest service interval the ledger has seen, floored at <see cref="FleetSilenceFloor"/>
/// (#2082), or <see cref="MissedTickAllowance"/> × the projection interval (#1981) — so the
/// <c>baton-daemon</c> scheduled task's repeating five-minute trigger (registered by
/// <c>tools/tool-refresh/register-daemon-task.ps1</c>) brings it back. Same principle as the build
/// lock's own timeout: a stuck holder that dies is recoverable, a stuck holder that waits is not.
/// </para>
/// <para>
/// <b>Why the fleet-silence arm (#2082).</b> On 2026-09-08 at 03:12Z the projection writer's tick had
/// grown to 10.97 s against its 30 s interval, then every service's last recorded tick fell inside one
/// 30 s window; the surviving loop (<see cref="WatchSweep"/>) ticked until 03:19:23Z and the
/// projection arm fired 194 s after that, 619 s after the projection had gone stale. "No service at
/// all has ticked" is a stronger fact than "the newest tick is older than five projection intervals":
/// the fleet arm uses that distinction. See <see cref="FleetSilenceFloor"/> for the floor's
/// justification and spec/baton.md §7 for the current service count and cadence.
/// </para>
/// <para>
/// <b>What it does NOT catch, deliberately:</b> one service wedging while its siblings keep ticking.
/// Both arms read the NEWEST completion across every service, so a stalled
/// <see cref="FleetProjectionWriter"/> beside a healthy <see cref="WatchSweep"/> stays quiet here —
/// killing the whole daemon over one stuck loop would be a worse trade than the stale projection it
/// would be curing. That case is what the same issue's <c>fleet_status</c>/glass staleness reading
/// covers instead: it keys on the projection file's own <c>derived_at</c>, at three ticks. The cost
/// of that ruling is visible in the incident above: <c>heartbeat.json</c> is written by the projection
/// tick, so it froze at 03:12:42Z while <see cref="WatchSweep"/> kept going for seven more minutes,
/// and made the surviving loop look stopped too.
/// </para>
/// <para>
/// <b>Its own loop runs on a dedicated <see cref="Thread"/> waiting on a
/// <see cref="WaitHandle"/>, not a <c>BackgroundService</c> with <c>Task.Delay</c>.</b> The
/// incident's signature — four services stopping in the same second, a paged-out working set — is at
/// least as consistent with a wedged thread pool as with any one slow room walk, and a watchdog whose
/// next wake-up is a pool continuation would be frozen by exactly the condition it exists to catch.
/// The same reasoning is why the trip sequence in <see cref="CheckOnce"/> arms its
/// <see cref="Process.Kill()"/> fallback on a second dedicated thread: <see cref="HungExitCode"/>
/// records what is and is not measured about the orderly exit under a wedged pool.
/// </para>
/// </summary>
internal sealed class DaemonWatchdog : IHostedService
{
    /// <summary>Missed ticks before the daemon is declared hung. Five, not one or two: a tick that
    /// runs long under IO contention is ordinary (the incident's own box had four dotnet runs queued
    /// on the build lock), and this action is to kill the process — the false-positive costs a
    /// restart, so the bar sits well above ordinary slowness.</summary>
    internal const int MissedTickAllowance = 5;

    /// <summary>The fleet-silence arm (#2082): no service at all has completed a tick in this many
    /// of the SHORTEST service interval. Two, not five: the class doc has why a whole-process silence
    /// earns a lower bar than one slow loop.</summary>
    internal const int FleetSilenceMultiplier = 2;

    /// <summary>The fleet-silence arm never trips inside this, whatever the shortest interval is —
    /// it preserves the issue's requested bound rather than twice the current shortest cadence
    /// (the figures and service count are in spec/baton.md §7). The false-positive rate under
    /// host contention is unmeasured.</summary>
    internal static readonly TimeSpan FleetSilenceFloor = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Non-zero, and specifically not 1: an exit code an operator finds in the scheduled task's Last
    /// Run Result should name which self-diagnosis fired. 70 is <c>EX_SOFTWARE</c>.
    /// <para>
    /// <b>That the OS sees 70 is measured — for one population, which is narrower than the case this
    /// watchdog was built for</b> (2026-09-06, .NET 10, Windows).
    /// <see cref="Environment.Exit(int)"/> runs <c>ProcessExit</c> handlers, and the Generic Host's
    /// console lifetime hooks that event to stop the application — so the open question was whether
    /// the host's shutdown either zeroes the code or blocks on the very services that stopped
    /// ticking. A throwaway probe (a Generic Host whose hosted service's <c>StopAsync</c> never
    /// completes, killed from a second thread) exited <b>70</b>, promptly, in exactly that shape.
    /// </para>
    /// <para>
    /// <b>What that probe did NOT cover (2026-09-06 review, finding E):</b> it ran on a HEALTHY thread
    /// pool. The wedged-pool case — the incident's own leading hypothesis, and the whole reason this
    /// class owns a dedicated thread — is <b>unmeasured</b>: nothing here establishes what
    /// <c>Environment.Exit</c> does when its handler chain has no pool thread to run a shutdown
    /// continuation on. So the exit is no longer the only way out. <see cref="CheckOnce"/> arms
    /// <see cref="LastResortKillAfter"/> of grace on a second dedicated thread and then calls
    /// <see cref="Process.Kill()"/>, which is immune to the handler chain. Its Windows exit code is
    /// deliberately NOT 70 (<c>Kill()</c> does not get to choose one), and that difference is itself
    /// readable: 70 in Last Run Result means the orderly path worked, anything else beside a fresh
    /// <see cref="BatonPaths.FleetWatchdogVerdictFile"/> means the orderly path did not come back.
    /// </para>
    /// <para>
    /// The scheduled task's action must end in <c>; exit $LASTEXITCODE</c> for this to reach the
    /// scheduler at all — <c>tools/tool-refresh/register-daemon-task.ps1</c> carries that measurement.
    /// </para>
    /// </summary>
    internal const int HungExitCode = 70;

    /// <summary>How long the orderly <see cref="Environment.Exit(int)"/> path gets before
    /// <see cref="Process.Kill()"/> takes the process down regardless. Ten seconds: the measured
    /// orderly exit was prompt, so anything near this bound already means the shutdown is stuck, and
    /// a daemon that is going to die anyway loses nothing by dying ten seconds later.</summary>
    internal static readonly TimeSpan LastResortKillAfter = TimeSpan.FromSeconds(10);

    private readonly DaemonTickLedger _ledger;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<TimeSpan> _interval;
    private readonly Action<string> _log;
    private readonly Action<int> _exit;
    private readonly Action<string> _writeVerdictFile;
    private readonly Action _armLastResortKill;
    private readonly Func<DateTimeOffset, HostLoadSample> _sampleLoad;
    private readonly CancellationTokenSource _stopping = new();
    private Thread? _thread;

    public DaemonWatchdog()
        : this(DaemonTickLedger.Instance, () => DateTimeOffset.UtcNow, FleetProjectionWriter.GetInterval,
               Console.Error.WriteLine, Environment.Exit, WriteVerdictFile, ArmLastResortKill, HostLoadSample.Capture)
    {
    }

    /// <summary>Test-only seam (Baton.Cli.Tests, via <c>InternalsVisibleTo</c>): a fixture clock and a
    /// captured exit, so both polarities can be driven without waiting real minutes or killing the
    /// test host. The verdict-file and kill seams default to no-ops precisely so a test can never get
    /// the real <see cref="Process.Kill()"/> timer or write into a real <c>~/.baton</c>; a test that
    /// wants to observe the ordering passes recorders. <paramref name="sampleLoad"/> defaults to the
    /// live counters, which are harmless to read from a test.</summary>
    internal DaemonWatchdog(
        DaemonTickLedger ledger,
        Func<DateTimeOffset> clock,
        Func<TimeSpan> interval,
        Action<string> log,
        Action<int> exit,
        Action<string>? writeVerdictFile = null,
        Action? armLastResortKill = null,
        Func<DateTimeOffset, HostLoadSample>? sampleLoad = null)
    {
        _ledger = ledger;
        _clock = clock;
        _interval = interval;
        _log = log;
        _exit = exit;
        _writeVerdictFile = writeVerdictFile ?? (_ => { });
        _armLastResortKill = armLastResortKill ?? (() => { });
        _sampleLoad = sampleLoad ?? HostLoadSample.Capture;
    }

    /// <summary>How long the supervision thread sleeps between passes: the projection interval, but
    /// never more than half <see cref="FleetSilenceFloor"/> — an operator who widens the projection
    /// interval to minutes must not widen the fleet-silence arm's reaction time with it.</summary>
    internal static TimeSpan WakeCadence(TimeSpan projectionInterval) =>
        projectionInterval < FleetSilenceFloor / 2 ? projectionInterval : FleetSilenceFloor / 2;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "baton-daemon-watchdog",
        };
        _thread.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping.Cancel();
        return Task.CompletedTask;
    }

    private void Loop()
    {
        while (!_stopping.IsCancellationRequested)
        {
            // WaitHandle.WaitOne, not Thread.Sleep: same "no thread-pool continuation" property, and a
            // stopping daemon does not have to wait out a whole interval before its thread returns.
            if (_stopping.Token.WaitHandle.WaitOne(WakeCadence(_interval())))
            {
                return;
            }

            if (CheckOnce())
            {
                return;
            }
        }
    }

    /// <summary>
    /// One supervision pass. Returns true when it tripped (and therefore ran the trip sequence) — the
    /// return value is what the tests read; production's exit action does not come back.
    /// <para>
    /// <b>The order of the four steps below is the finding, not an accident</b> (2026-09-06 review,
    /// finding D). The fault this watchdog exists for silenced the daemon's console — and since the
    /// same PR routed every console write in the process through one <c>TimestampedLineWriter</c>
    /// lock, a thread stuck mid-write now blocks <see cref="_log"/> too. So the kill timer is armed
    /// FIRST (it covers everything after it, including the file write, which can block on the same
    /// filesystem), the durable diagnosis is written SECOND, and only then does anything go near the
    /// stream that went quiet. The console line is the nice-to-have; the exit code and the verdict
    /// file are the diagnosis.
    /// </para>
    /// </summary>
    internal bool CheckOnce()
    {
        var now = _clock();
        var interval = _interval();
        // Capture before judging, on every pass, so a trip never runs this code for the first time.
        // spec/baton.md §7 records the measurement scope of capture on this dedicated thread.
        var verdict = Evaluate(_ledger, now, interval, _sampleLoad(now));
        if (verdict is null)
        {
            return false;
        }

        _armLastResortKill();
        _writeVerdictFile(verdict);
        _log(verdict);
        _exit(HungExitCode);
        return true;
    }

    /// <summary>
    /// The production <see cref="_writeVerdictFile"/>: one line into
    /// <see cref="BatonPaths.FleetWatchdogVerdictFile"/>, which owns why it is a file of its own.
    /// Best-effort — a diagnosis that cannot be written must not stop the exit that recovers the
    /// daemon, and the log line and exit code below it are the other two copies.
    /// The prefix stamps the write; the host-load clause stamps capture. See spec/baton.md §7
    /// for interpreting a delayed verdict.
    /// <para>
    /// <c>internal</c> rather than private so a test can drive the real writer against a temp
    /// <see cref="BatonPaths.Root"/> (2026-09-06 round-3 review): the internal constructor defaults
    /// this seam to a no-op, which is the right default and also meant path resolution, the
    /// directory create and the line content were unexercised — and a silently broken writer inverts
    /// the read-out rule in this type's own remarks into the wrong diagnosis.
    /// </para>
    /// </summary>
    internal static void WriteVerdictFile(string verdict)
    {
        var path = BatonPaths.FleetWatchdogVerdictFile;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $"{DateTimeOffset.UtcNow:O} {verdict}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine($"DaemonWatchdog: could not write {path}: {ex.Message}");
        }
    }

    /// <summary>
    /// The production <see cref="_armLastResortKill"/>: a second dedicated thread that gives the
    /// orderly exit <see cref="LastResortKillAfter"/> and then terminates the process outright. A
    /// <see cref="Thread"/> and <see cref="Thread.Sleep(TimeSpan)"/> for the same reason the
    /// supervision loop uses one — a timer callback is a thread-pool continuation, and a wedged pool
    /// is the state this is the fallback for. Background, so it can never hold up a daemon that is
    /// shutting down normally.
    /// </summary>
    private static void ArmLastResortKill()
    {
        var killer = new Thread(() =>
        {
            Thread.Sleep(LastResortKillAfter);
            try
            {
                // Reached only when Environment.Exit did not come back within the grace period, so
                // the process is by definition still here to kill.
                Process.GetCurrentProcess().Kill();
            }
            catch (Exception ex)
            {
                // Handled, not swallowed: there is nothing above this to rethrow to (an unhandled
                // exception here would take the process down with an exit code that says less than
                // this line does), and a hung daemon that cannot even be killed is the one fact left
                // worth recording.
                Console.Error.WriteLine($"DaemonWatchdog: last-resort Kill() failed: {ex.Message}");
            }
        })
        {
            IsBackground = true,
            Name = "baton-daemon-watchdog-kill",
        };
        killer.Start();
    }

    /// <summary>The fleet-silence arm's bound for a ledger that has seen <paramref name="shortestInterval"/>
    /// as its fastest cadence, or null before any service has ticked (the projection arm, measured from
    /// process start, covers a daemon that wedges during its first pass). The rule itself is stated in
    /// spec/baton.md §7; this is its arithmetic.</summary>
    internal static TimeSpan? FleetSilenceLimit(TimeSpan? shortestInterval)
    {
        if (shortestInterval is not { } shortest)
        {
            return null;
        }

        var scaled = shortest * FleetSilenceMultiplier;
        return scaled > FleetSilenceFloor ? scaled : FleetSilenceFloor;
    }

    /// <summary>
    /// The judgment, pure over a ledger snapshot: null when the daemon is still turning over, otherwise
    /// the single line to log before exiting. The line names which arm tripped, the last service that
    /// DID complete a tick, the one that has been silent longest, and <paramref name="load"/> — the
    /// host as it looks right now, from the thread that is still running — because "the daemon is
    /// hung" alone is what <c>daemon.log</c> already effectively said on 2026-09-06, and "every loop
    /// stopped at 03:12" is what it said on 2026-09-08 without saying why.
    /// </summary>
    internal static string? Evaluate(
        DaemonTickLedger ledger, DateTimeOffset now, TimeSpan interval, HostLoadSample? load = null)
    {
        var projectionLimit = interval * MissedTickAllowance;
        var shortestInterval = ledger.ShortestInterval();
        var fleetLimit = FleetSilenceLimit(shortestInterval);
        var ticks = ledger.Snapshot();

        // No service has completed a tick at all yet: measured from process start, so a daemon that
        // wedges during its first pass trips too rather than being read as "nothing due yet".
        var newestAt = ticks.Count > 0 ? ticks[0].CompletedAt : ledger.StartedAt;
        var silence = now - newestAt;

        string bound;
        if (fleetLimit is { } fleet && silence > fleet)
        {
            bound = $"limit {fleet.TotalSeconds:F0}s = {FleetSilenceMultiplier} x the shortest service interval "
                    + $"({shortestInterval!.Value.TotalSeconds:F0}s), floored at {FleetSilenceFloor.TotalSeconds:F0}s";
        }
        else if (silence > projectionLimit)
        {
            bound = $"limit {projectionLimit.TotalSeconds:F0}s = {MissedTickAllowance} x the {interval.TotalSeconds:F0}s projection interval";
        }
        else
        {
            return null;
        }

        var lastCompleted = ticks.Count > 0
            ? $"{ticks[0].Service} at {ticks[0].CompletedAt:O} (its tick took {ticks[0].Elapsed.TotalMilliseconds:F0}ms)"
            : $"none since the process started at {ledger.StartedAt:O}";
        var quietest = ticks.Count > 0
            ? $"{ticks[^1].Service}, last completed {ticks[^1].CompletedAt:O} ({(now - ticks[^1].CompletedAt).TotalSeconds:F0}s ago, its interval is {ticks[^1].Interval.TotalSeconds:F0}s)"
            : "every registered service — none has ever reported a tick";
        var host = load is null ? "" : $"Host now: {load.Describe()}. ";

        return $"DaemonWatchdog: no service has completed a tick in {silence.TotalSeconds:F0}s ({bound}). "
               + $"Last to complete: {lastCompleted}. Longest silent: {quietest}. {host}"
               + $"Exiting {HungExitCode} so the baton-daemon scheduled task's repeating trigger relaunches the daemon; "
               + $"{BatonPaths.FleetHeartbeatFile} holds the per-service durations and the host-load sample as of the last projection tick that finished.";
    }
}
