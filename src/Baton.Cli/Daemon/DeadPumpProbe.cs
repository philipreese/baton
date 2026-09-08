using System.Diagnostics;
using Baton.Cli.Mcp;
using Baton.Concurrency;
using Baton.Domain;
using Baton.Projection;
using Baton.Status;
using Baton.Store;
using Baton.Templates;
using Baton.Vendors;
using Microsoft.Extensions.Hosting;

namespace Baton.Cli.Daemon;

/// <summary>
/// #2072 (slice one of #1530): the daemon's dead-pump liveness probe — see spec/baton.md §7's
/// "No daemon reaper" paragraph for the ruling this implements and the line it draws (this appends a
/// terminal fact; it never re-drives a room).
/// <para>
/// A live <c>baton run</c> pump holds the room's <c>flow.lock</c> for its whole run
/// (<c>MutationInterface.StartWorkflowAsync</c>), and the OS releases that lock the instant the
/// holding process exits, crashed or not (<see cref="ConcurrencyGuard"/>). So "the lock is free and
/// this room still has an arrestable execution" is exactly "the pump was killed out from under a
/// still-open execution" — the one arrest outcome nothing in the system recorded until a
/// <em>subsequent</em> <c>baton run</c> happened to reach the room (#1530 Residual 1's last case).
/// </para>
/// </summary>
/// <remarks>
/// <b>Its own hosted service, not a hook inside <see cref="FleetProjectionWriter"/>.</b> That service is
/// the read-only projection producer and is built around "one room must never sink the tick"; this one
/// writes, and takes a lock to do it. <see cref="DeliveryPoller"/> is the shape mirrored here — the
/// daemon's existing room-journal writer — including its per-room failure isolation and its
/// <see cref="DaemonTickLedger"/> report.
/// </remarks>
public sealed class DeadPumpProbe : BackgroundService
{
    /// <summary>
    /// Fixed, deliberately: what an operator tunes is the quiet window below (the predicate), not how
    /// often the daemon re-asks the question. A tick that finds nothing costs one <c>stat</c> per room.
    /// </summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Three consecutive missed <see cref="FlowEvent.ExecutionProgress"/> heartbeats — the cadence is
    /// <see cref="ExecutionProgressHeartbeat.PlaceholderDefaultInterval"/>'s, multiplied here rather
    /// than transcribed, so re-timing the heartbeat re-times this with it.
    /// <para>
    /// A margin, not the predicate. The free lock is already conclusive on its own; the window exists so
    /// a room whose journal was being written seconds ago is never called dead over a momentary
    /// handoff, and three missed heartbeats is the smallest count that cannot be one late tick.
    /// </para>
    /// </summary>
    public static readonly TimeSpan DefaultQuietWindow = ExecutionProgressHeartbeat.PlaceholderDefaultInterval * 3;

    /// <summary>Attribution written onto <see cref="FlowEvent.StepRetryForeclosed.ForeclosedBy"/>, so a
    /// foreclosure this probe recorded is tellable from <c>baton resolve --close</c>'s.</summary>
    public const string DiagnosticName = "dead-pump probe";

    private readonly DaemonSettings _settings;

    public DeadPumpProbe(DaemonSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    /// <summary>
    /// <see cref="DaemonSettings.DeadPumpQuietMinutes"/>, or <see cref="DefaultQuietWindow"/> when it is
    /// absent or non-positive. An out-of-range value falls back rather than being honoured, the same
    /// posture <c>RunwayHoldSettings</c> already takes: <c>0</c> would arrest every room the instant a
    /// pump released its lock, which is not a setting anyone means.
    /// </summary>
    public static TimeSpan ResolveQuietWindow(DaemonSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.DeadPumpQuietMinutes is { } minutes && minutes > 0
            ? TimeSpan.FromMinutes(minutes)
            : DefaultQuietWindow;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                await ProbeOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"DeadPumpProbe: sweep iteration failed: {ex.Message}");
            }

            // #1981: DaemonTickLedger owns what this report is for.
            DaemonTickLedger.Instance.RecordTick(nameof(DeadPumpProbe), Stopwatch.GetElapsedTime(started), Interval);

            try
            {
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One tick's worth of work over every discovered room — internal entry point for tests.
    /// Returns how many facts were appended across the fleet.</summary>
    internal async Task<int> ProbeOnceAsync(CancellationToken cancellationToken = default, TextWriter? diagnostics = null)
    {
        var discovered = await FleetStatusTool.DiscoverRoomsAsync([], cancellationToken).ConfigureAwait(false);
        var appended = 0;
        foreach (var room in discovered)
        {
            try
            {
                appended += await ProbeRoomAsync(room.RoomDir, cancellationToken, diagnostics).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Per-room isolation, DeliveryPoller's own posture: one unreadable room must not cost
                // the fleet its probe.
                (diagnostics ?? Console.Error).WriteLine($"DeadPumpProbe: room '{room.RoomDir}' failed: {ex.Message}");
            }
        }

        return appended;
    }

    /// <summary>
    /// One room's worth of work — internal so a test can drive a single fixture directly. Returns how
    /// many facts were appended (0 or, for a multi-step DAG, one per still-arrestable target; which
    /// fact each gets is the two-shape split inside).
    /// </summary>
    internal async Task<int> ProbeRoomAsync(
        string roomDirectoryPath, CancellationToken cancellationToken = default, TextWriter? diagnostics = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);

        // A settled room, by the same sentinel FleetStatusTool's own fast path keys on: nothing to say.
        if (File.Exists(Path.Combine(roomDirectoryPath, TerminalSentinelWriter.TerminalSentinelFileName)))
        {
            return 0;
        }

        var logPath = Path.Combine(roomDirectoryPath, BatonPaths.FlowLogFileName);
        var snapshotPath = Path.Combine(roomDirectoryPath, BatonPaths.SnapshotFileName);
        if (!File.Exists(logPath) || !File.Exists(snapshotPath))
        {
            return 0;
        }

        var quietWindow = ResolveQuietWindow(_settings);
        var now = DateTime.UtcNow;

        // The cheap prefilter, so an ordinary tick over hundreds of rooms costs one stat each and never
        // parses a journal it cannot act on. Conservative in the safe direction by construction: the
        // file's mtime is never OLDER than its last entry's timestamp, so this can only skip a room the
        // authoritative check below would also have skipped.
        if (now - File.GetLastWriteTimeUtc(logPath) < quietWindow)
        {
            return 0;
        }

        // The lock try IS the acquire, fail-fast. A separate ConcurrencyGuard.IsHeld probe followed by
        // an acquire leaves a window for a pump to take the lock in between, and appending a terminal
        // fact into a LIVE lane is the worst thing this probe could do — the room would read settled
        // while its worker kept running. Everything the decision rests on is therefore re-read from
        // inside the guard below, the same "resolved against THIS round's own fresh projection, never a
        // possibly stale one" rule MutationInterface's arrest-intent drain follows.
        ConcurrencyGuard guard;
        try
        {
            guard = ConcurrencyGuard.Acquire(roomDirectoryPath, $"baton daemon dead-pump probe (pid {Environment.ProcessId})");
        }
        catch (WorkflowLockedException)
        {
            // A pump holds it: the room is alive. Also the honest reading of "cannot tell" — the lock is
            // only reported free when this process actually held it.
            return 0;
        }

        using (guard)
        {
            var snapshot = await SnapshotBinder.LoadFromFileAsync(snapshotPath, cancellationToken).ConfigureAwait(false);
            var entries = await new FlowEventLogReader(logPath).ReadAllEntriesWithTimestampsAsync(cancellationToken)
                .ConfigureAwait(false);

            var flowEvents = new List<FlowEvent>(entries.Count);
            foreach (var entry in entries)
            {
                if (entry is LogEntry.FlowLogEntry flowLogEntry)
                {
                    flowEvents.Add(flowLogEntry.Event);
                }
            }

            // The same checkpoint FleetStatusTool.ProcessRoomAsync passes, so what this probe decides on
            // is the state `baton status` and the glass are showing for the same room.
            var state = StateProjector.Project(flowEvents, snapshot, ProjectionCheckpointStore.Load(roomDirectoryPath));

            // #1556 PR 1's single register for "what could be arrested right now" — a Running step's
            // latest execution, a quota-parked one, or a step-less supplementary execution. Reused, never
            // restated: a second copy of this predicate is exactly what that class collapsed.
            var targets = ArrestableExecutions.All(state, snapshot);
            if (targets.Count == 0)
            {
                return 0;
            }

            var lastEventUtc = LastEntryTimestampUtc(entries) ?? File.GetLastWriteTimeUtc(logPath);
            if (now - lastEventUtc < quietWindow)
            {
                return 0;
            }

            var workerPidByExecution = new Dictionary<ExecutionId, uint>();
            foreach (var entry in entries)
            {
                if (entry is LogEntry.CoreLogEntry { Event: CoreEvent.ExecutionStarted started })
                {
                    workerPidByExecution[started.ExecutionId] = started.Pid;
                }
            }

            await using var writer = new FlowEventLogWriter(logPath);
            foreach (var target in targets)
            {
                // The lock's name is BatonPaths', never a literal here (#1271's tripwire).
                var reason =
                    $"Arrested: pump dead — no process holds this room's {BatonPaths.FlowLockFileName} and this "
                        + $"execution never settled, so the engine was killed out from under it. Last journal event "
                        + $"{new DateTimeOffset(DateTime.SpecifyKind(lastEventUtc, DateTimeKind.Utc)):O}"
                        + (workerPidByExecution.TryGetValue(target.ExecutionId, out var workerPid)
                            ? $"; worker pid {workerPid}."
                            : ".");

                // Two shapes, one fact each — because ArrestableExecutions.All admits two shapes and a
                // single event settles only one of them.
                //
                // A quota-parked target (#1607: StepStatus.Failed with a scheduled StepState.RetryNotBefore)
                // has ALREADY settled its latest execution — that is what the park IS, an
                // ExecutionFailed(ExhaustedUntil) plus a StepRetryScheduled nothing will now drive. What is
                // still open there is the step's retry obligation, not the execution, so the fact that
                // records the arrest is FlowEvent.StepRetryForeclosed (#1586 S1's retry-foreclosure
                // primitive, whose whole purpose is this) rather than a second ExecutionFailed. A second
                // ExecutionFailed would be wrong twice over: StateProjector never clears
                // RetryNotBeforeByStepId on that arm, so DeriveWorkflowStatus would keep ORing
                // `step.RetryNotBefore is not null` into deliverability and the room would still read
                // Running — the arrest invisible, which is the defect this probe exists to fix — and the
                // target would stay arrestable, so the next tick would append it again, forever.
                if (target is { StepId: { } parkedStepId, Status: StepStatus.Failed })
                {
                    await writer.AppendAsync(
                            new FlowEvent.StepRetryForeclosed(
                                parkedStepId, target.ExecutionId, reason, ForeclosedBy: DiagnosticName),
                            cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                await writer.AppendAsync(
                        new FlowEvent.ExecutionFailed(
                            target.ExecutionId,
                            // Permanent, not the Retryable crash recovery writes for the same shape. That
                            // arm hands the room straight back to a pump that will schedule the retry;
                            // here there is no pump, and StateProjector.DeriveWorkflowStatus keeps a
                            // room whose step RetryEngine.MayRetry admits reading Running — so a
                            // Retryable fact would land, change nothing an operator sees, and leave the
                            // room claiming it can still deliver when nothing will ever drive it again.
                            FailureClassification.Permanent,
                            reason),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            (diagnostics ?? Console.Error).WriteLine(
                $"DeadPumpProbe: '{roomDirectoryPath}' had {targets.Count} open execution(s) with a free "
                    + $"{BatonPaths.FlowLockFileName} and no journal activity since {lastEventUtc:O} — recorded as arrested.");

            return targets.Count;
        }
    }

    /// <summary>
    /// The newest timestamp any line carries. Not simply the LAST line's: a line written before
    /// timestamps existed carries none (<see cref="LogEntry.FlowLogEntry.WriterUtcTimestamp"/> is
    /// nullable), and reading the newest present is what keeps one such trailing line from silently
    /// discarding every stamp before it.
    /// </summary>
    private static DateTime? LastEntryTimestampUtc(IReadOnlyList<LogEntry> entries)
    {
        DateTime? newest = null;
        foreach (var entry in entries)
        {
            var stamped = entry switch
            {
                LogEntry.FlowLogEntry flow => flow.WriterUtcTimestamp,
                LogEntry.CoreLogEntry core => core.WriterUtcTimestamp,
                LogEntry.RoomLogEntry room => room.WriterUtcTimestamp,
                _ => null,
            };

            if (stamped is { } value && (newest is null || value > newest))
            {
                newest = DateTime.SpecifyKind(value, DateTimeKind.Utc);
            }
        }

        return newest;
    }
}
