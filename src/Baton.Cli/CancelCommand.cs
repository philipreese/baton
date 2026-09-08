using System.Diagnostics;
using Baton.Concurrency;
using Baton.Domain;
using Baton.Mutation;
using Baton.Outcomes;
using Baton.Projection;
using Baton.Status;
using Baton.Store;
using Baton.Templates;
using Baton.Vendors;

namespace Baton.Cli;

/// <summary>
/// <c>baton cancel &lt;room-dir&gt; [--execution &lt;id&gt;] [--reason &lt;why&gt;]</c> — the operator's arrest
/// verb, rebuilt by #2073 (slice two of #1530) around one ordering rule: <b>the intent fact is written
/// first, and everything that can stop the worker comes after it.</b> In order:
/// <list type="number">
/// <item><b>Idempotency.</b> Nothing left to arrest (the room is Terminal, or the named execution
/// settled earlier) is said aloud and exits 0 with no append at all, intent included
/// (<see cref="CommandResult.CancelWasNoOp"/>). Re-running a cancel is never a new failure.</item>
/// <item><b>Target.</b> An explicit <c>--execution</c> is validated against the room's accepted
/// requests; a bare invocation resolves "the target lane" via <see cref="RunningExecutionResolver"/>
/// (#1495/#1607 — exactly one Running or quota-parked step, fail closed otherwise).</item>
/// <item><b>The intent fact</b> — <see cref="RoomEvent.ArrestIntentRecorded"/>, appended to
/// <c>room.jsonl</c> (the choice of journal is explained on that record).</item>
/// <item><b>Arrest through the existing channel.</b> Whether a pump is alive is answered by trying
/// <c>flow.lock</c> without waiting (<c>Baton.Cli.Daemon.DeadPumpProbe</c> reads liveness the same
/// way): a live pump holds that lock for its whole run, so a held lock means write
/// <see cref="CancelRequestFile"/> (#1528's channel, which #1825's pump-side seam settles) and give the
/// target <see cref="ResolvePumpAnswerWindow"/> to leave <see cref="ArrestableExecutions"/>. A free
/// lock means there is no pump to answer.</item>
/// <item><b>The kill, only if no pump answered.</b> <see cref="WorkerProcessArrest"/> is handed the
/// pid and start time <see cref="CoreEvent.ExecutionStarted"/> recorded and kills on nothing short
/// of <see cref="EngineLivenessProbe"/>'s Alive verdict for that pair.</item>
/// <item><b>The terminal fact</b>, slice one's two shapes with cause <c>operator cancel</c>
/// (spec/baton.md §7's <c>DeadPumpProbe</c> bullet owns the Running/parked split), appended under
/// <c>flow.lock</c>. If a pump still holds that lock once the kill is done, it records its own
/// worker's exit and this command reports <see cref="CommandResult.CancellationQueued"/> instead.</item>
/// </list>
/// Exit code (#2103): a cancel that got the target settled — by the pump's answer or by this command's
/// own terminal fact — reports <see cref="CommandResult.CancelApplied"/> and exits 0; the queued arm
/// exits 1; the no-op exits 0. <see cref="MutationExitCodeResolver"/> is the table.
/// </summary>
/// <remarks>
/// <b>What this retired</b> — spec/baton.md §2's <c>baton cancel</c> paragraph is the record; in
/// one clause each: the pre-#2073 direct <c>MutationInterface.RequestCancellationAsync</c> call (against
/// a dead pump's Running execution it ran crash recovery and the retry engine spawned the worker
/// again — the verb undid itself); the #1586/#1607 dead-holder gate (a parked room with no
/// confirmed-live pump was refused for want of a settling verb, and this is now that verb: a free
/// lock is settled, with the holder sidecar's record carried into the terminal fact's reason); and
/// bindings loading (<c>--bindings</c>/<c>--workflow-id</c> are accepted and ignored — nothing here
/// dispatches).
/// </remarks>
public static class CancelCommand
{
    /// <summary>
    /// Attribution written onto <see cref="FlowEvent.StepRetryForeclosed.ForeclosedBy"/> and named in
    /// every terminal reason this verb writes, so an operator cancel is tellable from
    /// <c>resolve --close</c>'s and from the dead-pump probe's.
    /// </summary>
    public const string DiagnosticName = "baton cancel";

    /// <summary>What <see cref="RoomEvent.ArrestIntentRecorded.RequestedBy"/> carries. This verb is the only producer.</summary>
    public const string OperatorRequestedBy = "operator";

    /// <summary>The fixed opening of every terminal reason this verb writes — the cause, in the words #2073 ruled.</summary>
    public const string ArrestReasonPrefix = "Arrested: operator cancel";

    /// <summary>
    /// How long a live pump gets to answer the <c>cancel.request</c> before the worker is killed by
    /// pid. Derived from the pump's own poll cadence rather than transcribed: the poller's bounded
    /// retry gives up after five ticks (<c>CancelRequestPoller.TickAsync</c>), the pump's arrest-intent
    /// drain needs a round after that, and a process-tree kill plus outcome record takes a few seconds
    /// more — fifteen ticks covers all three with margin. Overridable per machine via
    /// <see cref="DaemonSettings.CancelPumpAnswerSeconds"/>.
    /// </summary>
    public static readonly TimeSpan DefaultPumpAnswerWindow = CancelRequestPoller.DefaultPollInterval * 15;

    /// <summary>How often the answer wait re-projects the room. Cheap (one journal read), and a quarter of the poller's own tick so a settle is noticed promptly.</summary>
    private static readonly TimeSpan AnswerPollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// <see cref="DaemonSettings.CancelPumpAnswerSeconds"/>, or <see cref="DefaultPumpAnswerWindow"/>
    /// when it is absent or non-positive — the same fall-back-rather-than-honour posture
    /// <c>DeadPumpProbe.ResolveQuietWindow</c> takes for its own key.
    /// </summary>
    public static TimeSpan ResolvePumpAnswerWindow(DaemonSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.CancelPumpAnswerSeconds is { } seconds && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : DefaultPumpAnswerWindow;
    }

    /// <exception cref="SnapshotLoadException">
    /// record-once-ok: #443 src/Baton.Cli/DecideCommand.cs
    /// The room directory has no persisted snapshot yet (never started via <c>baton run</c>), or its
    /// persisted snapshot is malformed.
    /// </exception>
    /// <exception cref="UnknownExecutionIdException">
    /// <paramref name="options"/>'s <c>ExecutionId</c> was never admitted for execution.
    /// </exception>
    /// <exception cref="CliArgumentException">
    /// <paramref name="options"/>'s <c>ExecutionId</c> is <c>null</c> (room-level targeting, #1495) and
    /// the room's own projected state has zero or more than one candidate — a currently
    /// <see cref="StepStatus.Running"/> step or a quota-parked one (#1607) — fail closed rather than
    /// guess; the message names every candidate found.
    /// </exception>
    /// <param name="adapters">
    /// Unused since #2073 — kept so <c>Program.cs</c> and the mutation-verb call shape stay uniform.
    /// This verb never dispatches, so it never resolves a worker adapter.
    /// </param>
    /// <param name="pumpAnswerWindow">
    /// Test seam: overrides <see cref="ResolvePumpAnswerWindow"/>'s settings read. Production callers
    /// pass <c>null</c>.
    /// </param>
    /// <param name="killWorker">
    /// Test seam: replaces <see cref="WorkerProcessArrest.Kill"/> so the kill's ORDER relative to the
    /// intent fact can be observed without a live process. Production callers pass <c>null</c>.
    /// </param>
    public static async Task<CommandResult> ExecuteAsync(
        CancelOptions options,
        IReadOnlyDictionary<string, IWorkerAdapter> adapters,
        CancellationToken cancellationToken = default,
        TimeSpan? pumpAnswerWindow = null,
        Func<uint, DateTimeOffset?, WorkerKillResult>? killWorker = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(adapters);

        var roomDirectoryPath = options.RoomDirectoryPath;
        var snapshotPath = Path.Combine(roomDirectoryPath, BatonPaths.SnapshotFileName);
        var logPath = Path.Combine(roomDirectoryPath, BatonPaths.FlowLogFileName);
        var roomLogPath = Path.Combine(roomDirectoryPath, BatonPaths.RoomLogFileName);

        if (!File.Exists(snapshotPath))
        {
            throw new SnapshotLoadException(
                $"Room directory '{roomDirectoryPath}' has no bound snapshot — 'baton cancel' " +
                "targets a room 'baton run' has already started, and never binds one fresh.");
        }

        var snapshot = await SnapshotBinder.LoadFromFileAsync(snapshotPath, cancellationToken).ConfigureAwait(false);
        var reader = new FlowEventLogReader(logPath);
        killWorker ??= WorkerProcessArrest.Kill;

        var (entries, flowEvents) = await ReadJournalAsync(reader, cancellationToken).ConfigureAwait(false);
        var state = StateProjector.Project(flowEvents, snapshot, ProjectionCheckpointStore.Load(roomDirectoryPath));

        // An explicit id is validated the way MutationInterface.RequestCancellationAsync validated it
        // (same exception, same message) BEFORE the idempotency read below: a typo is a caller error
        // whatever state the room is in, and turning it into a quiet exit 0 would hide it.
        ExecutionId? explicitTargetExecutionId = null;
        if (options.ExecutionId is { } explicitExecutionId)
        {
            explicitTargetExecutionId = new ExecutionId(explicitExecutionId);
            var knownExecutionIds = flowEvents
                .OfType<FlowEvent.ExecutionRequestAccepted>()
                .Select(e => e.Request.ExecutionId)
                .ToHashSet();
            CancellationValidator.Validate(knownExecutionIds, explicitTargetExecutionId.Value);
        }

        // 1. Idempotency, before anything is written. Projected state, not terminal.json: the
        // sentinel is a fact about a settle Program already recorded, the projection is the room.
        if (state.Status == WorkflowStatus.Terminal)
        {
            Console.Out.WriteLine(
                $"Room '{roomDirectoryPath}' is already terminal ({WorkflowOutcome.Describe(state)}) — nothing to cancel, nothing written.");
            return new CommandResult(state, snapshot, RoomDirectoryPath: roomDirectoryPath, CancelWasNoOp: true);
        }

        // 2. The target.
        var targetExecutionId = explicitTargetExecutionId ?? ResolveRunningExecution(state, roomDirectoryPath);

        if (ArrestableExecutions.Find(state, snapshot, targetExecutionId) is null)
        {
            Console.Out.WriteLine(
                $"Execution '{targetExecutionId.Value}' in room '{roomDirectoryPath}' has already settled — nothing to arrest, nothing written.");
            return new CommandResult(state, snapshot, RoomDirectoryPath: roomDirectoryPath, CancelWasNoOp: true);
        }

        // 3. The intent fact — FIRST. Every line below that can stop the worker comes after this
        // append returns, and CancelCommandArrestTests pins that order with a kill seam that reads
        // room.jsonl at the moment it is invoked.
        var intentRecordedAtUtc = DateTimeOffset.UtcNow;
        await using (var roomWriter = new RoomEventLogWriter(roomLogPath))
        {
            await roomWriter.AppendAsync(
                    new RoomEvent.ArrestIntentRecorded(targetExecutionId.Value, OperatorRequestedBy, options.Reason, intentRecordedAtUtc),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        // 4. Arrest through the existing channel. The lock try is the liveness question — see the
        // class doc. The sidecar is read BEFORE the try only so the terminal reason can name the
        // holder that died; ConcurrencyGuard.Acquire overwrites and Dispose deletes that sidecar, so
        // the journal is where the record survives from here on.
        var (recordedHolderDescription, holderPid, _, _) = ConcurrencyGuard.ReadHolderInfo(roomDirectoryPath);
        WorkerKillResult? kill = null;
        var guard = TryAcquire(roomDirectoryPath, out var lockedBy);
        if (guard is null)
        {
            var explicitTarget = options.ExecutionId is not null;
            var fileTarget = explicitTarget ? targetExecutionId.Value : CancelRequestFile.LatestTarget;
            await CancelRequestFile.WriteAsync(roomDirectoryPath, fileTarget, cancellationToken).ConfigureAwait(false);

            var window = pumpAnswerWindow
                ?? ResolvePumpAnswerWindow(await DaemonSettingsStore.LoadAsync(BatonPaths.SettingsFile, cancellationToken).ConfigureAwait(false));
            Console.Out.WriteLine(
                $"Requested — '{roomDirectoryPath}'s {BatonPaths.FlowLockFileName} is held by '{lockedBy ?? "an unnamed holder"}', " +
                $"so this is being delivered through {CancelRequestFile.FileName} for the live pump to act on; waiting up to " +
                $"{window.TotalSeconds:0}s for it to answer.");

            if (await WaitForSettleAsync(reader, snapshot, targetExecutionId, window, cancellationToken).ConfigureAwait(false))
            {
                var answered = await ProjectAsync(reader, snapshot, roomDirectoryPath, cancellationToken).ConfigureAwait(false);
                Console.Out.WriteLine($"Arrested — the live pump settled execution '{targetExecutionId.Value}'.");
                return new CommandResult(answered, snapshot, RoomDirectoryPath: roomDirectoryPath, CancelApplied: true);
            }

            // No pump answered inside the window. Kill by the recorded pid, probe-gated, then give the
            // pump (if it is a pump and not a transient holder) one more window to record the exit it
            // just observed — a live pump settles its own worker's death, and that record is the
            // settle; this process only writes the terminal fact if it can win the lock afterwards.
            kill = KillRecordedWorker(entries, targetExecutionId, killWorker);
            Console.Out.WriteLine($"No pump answered within {window.TotalSeconds:0}s — {kill.Detail}.");

            if (kill.Outcome == WorkerKillOutcome.Killed
                && await WaitForSettleAsync(reader, snapshot, targetExecutionId, window, cancellationToken).ConfigureAwait(false))
            {
                var settledByPump = await ProjectAsync(reader, snapshot, roomDirectoryPath, cancellationToken).ConfigureAwait(false);
                Console.Out.WriteLine($"Arrested — the live pump recorded execution '{targetExecutionId.Value}' settling after the kill.");
                return new CommandResult(settledByPump, snapshot, RoomDirectoryPath: roomDirectoryPath, CancelApplied: true);
            }

            guard = TryAcquire(roomDirectoryPath, out lockedBy);
            if (guard is null)
            {
                var stillHeld = await ProjectAsync(reader, snapshot, roomDirectoryPath, cancellationToken).ConfigureAwait(false);
                Console.Out.WriteLine(
                    $"Not settled by this command: '{lockedBy ?? "an unnamed holder"}' still holds {BatonPaths.FlowLockFileName}, so the " +
                    "terminal fact is left to it — a live pump records its worker's exit itself, and the daemon's dead-pump probe " +
                    "settles a room whose pump is gone (spec/baton.md §7). The intent fact is on record either way.");
                return new CommandResult(stillHeld, snapshot, RoomDirectoryPath: roomDirectoryPath, CancellationQueued: true);
            }
        }
        else
        {
            var holderClause = holderPid is { } deadPid
                ? $"the last recorded holder was '{recordedHolderDescription ?? $"pid {deadPid}"}', no longer holding it"
                : "no holder record at all";
            Console.Out.WriteLine(
                $"No live pump holds '{roomDirectoryPath}'s {BatonPaths.FlowLockFileName} ({holderClause}) — settling the room directly.");
        }

        // 5/6. Under the lock: re-read, re-check, kill if not already attempted, then the terminal
        // fact — the same "decide on THIS round's own fresh projection" rule DeadPumpProbe follows.
        using (guard)
        {
            var (freshEntries, freshFlowEvents) = await ReadJournalAsync(reader, cancellationToken).ConfigureAwait(false);
            var freshState = StateProjector.Project(freshFlowEvents, snapshot, ProjectionCheckpointStore.Load(roomDirectoryPath));
            var target = ArrestableExecutions.Find(freshState, snapshot, targetExecutionId);
            if (target is null)
            {
                Console.Out.WriteLine($"Execution '{targetExecutionId.Value}' settled while this command was waiting for the lock — nothing further to write.");
                // Applied, not a no-op: the intent fact is on record and the target is settled, which
                // is the outcome the operator asked for — who wrote the terminal fact is the ledger's
                // business (ArrestLedgerProjector reads it as Delivered either way).
                return new CommandResult(freshState, snapshot, RoomDirectoryPath: roomDirectoryPath, CancelApplied: true);
            }

            kill ??= KillRecordedWorker(freshEntries, targetExecutionId, killWorker);

            var reasonClause = string.IsNullOrWhiteSpace(options.Reason) ? string.Empty : $" ({options.Reason.Trim()})";
            var holderClause = holderPid is { } deadPid
                ? $"; last recorded {BatonPaths.FlowLockFileName} holder '{recordedHolderDescription ?? $"pid {deadPid}"}' was no longer holding it"
                : string.Empty;
            var reason =
                $"{ArrestReasonPrefix}{reasonClause} — intent recorded by '{DiagnosticName}' at {intentRecordedAtUtc:O}; " +
                $"no pump answered, so this command settled the room{holderClause}; {kill.Detail}.";

            FlowEventLogWriter writer;
            try
            {
                writer = new FlowEventLogWriter(logPath);
            }
            catch (FlowJournalHeldException ex)
            {
                // #816's population, with the lock FREE: a killed process whose journal handle the OS
                // has not finished tearing down, or a sibling command mid-append. Not a pump (a pump
                // holds flow.lock), so no cancel.request; the intent is on record and the kill above
                // has happened, only the terminal fact is missing. Said plainly and left to a retry
                // or the dead-pump probe, rather than escaping as a crash after the kill.
                Console.Out.WriteLine(
                    $"Not settled by this command: {kill.Detail}, but '{logPath}' is held open by another process, so the " +
                    $"terminal fact was not written ({ex.Message}). The intent fact is on record; retry once nothing holds the " +
                    "ledger, or leave it to the daemon's dead-pump probe (spec/baton.md §7).");
                return new CommandResult(freshState, snapshot, RoomDirectoryPath: roomDirectoryPath, CancellationQueued: true);
            }

            await using var _ = writer;
            // Two shapes, one fact each — slice one's split, for slice one's reasons (DeadPumpProbe's
            // own remarks at the same fork are the register; not restated here).
            if (target is { StepId: { } parkedStepId, Status: StepStatus.Failed })
            {
                await writer.AppendAsync(
                        new FlowEvent.StepRetryForeclosed(parkedStepId, targetExecutionId, reason, ForeclosedBy: DiagnosticName),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await writer.AppendAsync(
                        new FlowEvent.ExecutionFailed(targetExecutionId, FailureClassification.Permanent, reason),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            Console.Out.WriteLine($"Arrested — {kill.Detail}; terminal fact recorded for execution '{targetExecutionId.Value}'.");
        }

        var settled = await ProjectAsync(reader, snapshot, roomDirectoryPath, cancellationToken).ConfigureAwait(false);
        return new CommandResult(settled, snapshot, RoomDirectoryPath: roomDirectoryPath, CancelApplied: true);
    }

    /// <summary>
    /// Room-level target resolution via <see cref="RunningExecutionResolver"/>; throws
    /// <see cref="CliArgumentException"/> when the room state does not contain exactly one candidate —
    /// a currently-<see cref="StepStatus.Running"/> step or a quota-parked one (#1607).
    /// </summary>
    private static ExecutionId ResolveRunningExecution(FlowState state, string roomDirectoryPath)
    {
        var resolved = RunningExecutionResolver.Resolve(state);

        if (resolved.Single is { } single)
        {
            return single;
        }

        if (resolved.RunningExecutionIds.Count == 0)
        {
            throw new CliArgumentException(
                $"No --execution given, and room '{roomDirectoryPath}' has no currently-Running or "
                + "quota-parked step to target — 'baton cancel' refuses to guess.",
                $"pass --execution explicitly, naming the execution to cancel — dig it out of "
                + $"`baton status {roomDirectoryPath}`.");
        }

        // F5 (#1607 review): name which candidate is which, not just their ids -- the ambiguity is
        // reachable (a Running step plus any sibling in ordinary retry backoff, spec/baton.md §2), so
        // this message is an operator's first encounter with the widened behaviour rather than a rare
        // edge case, and "Running or quota-parked" alone forces them to go find out which is which
        // some other way.
        var labeledCandidates = resolved.RunningExecutionIds.Select(id =>
        {
            var step = state.Steps.First(s => s.LatestExecutionId == id);
            var label = step.Status == StepStatus.Running ? "Running" : "quota-parked";
            return $"{id.Value} ({label})";
        });

        throw new CliArgumentException(
            $"No --execution given, and room '{roomDirectoryPath}' has {resolved.RunningExecutionIds.Count} "
            + $"currently-Running or quota-parked steps ({string.Join(", ", labeledCandidates)}) "
            + "— 'baton cancel' refuses to guess which one.",
            $"pass --execution explicitly, naming the one to cancel — see `baton status {roomDirectoryPath} --json` "
            + "for each step's current status; why this refuses rather than guesses is spec/baton.md §2.");
    }

    /// <summary>
    /// Fail-fast acquire. <c>null</c> means another process holds it — a live pump, or a transient
    /// holder (a memory sweep, a concurrent cancel); <paramref name="lockedBy"/> carries whatever the
    /// exception knew about who.
    /// </summary>
    private static ConcurrencyGuard? TryAcquire(string roomDirectoryPath, out string? lockedBy)
    {
        try
        {
            lockedBy = null;
            return ConcurrencyGuard.Acquire(roomDirectoryPath, $"{DiagnosticName} (pid {Environment.ProcessId})");
        }
        catch (WorkflowLockedException ex)
        {
            lockedBy = ex.HolderDescription;
            return null;
        }
    }

    /// <summary>
    /// The worker pid the journal recorded for <paramref name="targetExecutionId"/>, handed to
    /// <paramref name="killWorker"/> with its recorded start time. No <see cref="CoreEvent.ExecutionStarted"/>
    /// at all (a non-process worker, or a spawn that never happened) and an
    /// <see cref="CoreEvent.ExecutionExited"/> already on record both mean there is nothing to kill.
    /// </summary>
    private static WorkerKillResult KillRecordedWorker(
        IReadOnlyList<LogEntry> entries, ExecutionId targetExecutionId, Func<uint, DateTimeOffset?, WorkerKillResult> killWorker)
    {
        CoreEvent.ExecutionStarted? started = null;
        var exited = false;
        foreach (var entry in entries)
        {
            if (entry is not LogEntry.CoreLogEntry core)
            {
                continue;
            }

            switch (core.Event)
            {
                case CoreEvent.ExecutionStarted s when s.ExecutionId == targetExecutionId:
                    started = s;
                    exited = false;
                    break;
                case CoreEvent.ExecutionExited e when e.ExecutionId == targetExecutionId:
                    exited = true;
                    break;
            }
        }

        if (started is null)
        {
            return new WorkerKillResult(WorkerKillOutcome.AlreadyExited, "no worker process was ever recorded for this execution, so there was nothing to kill");
        }

        if (exited)
        {
            return new WorkerKillResult(WorkerKillOutcome.AlreadyExited, $"worker pid {started.Pid} had already recorded its exit, so there was nothing to kill");
        }

        var startTime = started.ProcessStartTimeUtc is { } utc
            ? new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc))
            : (DateTimeOffset?)null;
        return killWorker(started.Pid, startTime);
    }

    /// <summary>
    /// True once <paramref name="targetExecutionId"/> is no longer admitted by
    /// <see cref="ArrestableExecutions.Find"/> — whoever settled it, and however. Bounded by
    /// <paramref name="window"/>; a monotonic clock, not wall time, for the reason
    /// <c>ConcurrencyGuard.AcquireWithinCore</c> gives.
    /// </summary>
    private static async Task<bool> WaitForSettleAsync(
        FlowEventLogReader reader, WorkflowDefinitionSnapshot snapshot, ExecutionId targetExecutionId, TimeSpan window, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            var events = await reader.ReadAllAsync(cancellationToken).ConfigureAwait(false);
            var state = StateProjector.Project(events, snapshot);
            if (ArrestableExecutions.Find(state, snapshot, targetExecutionId) is null)
            {
                return true;
            }

            if (Stopwatch.GetElapsedTime(started) >= window)
            {
                return false;
            }

            await Task.Delay(AnswerPollInterval, cancellationToken).ConfigureAwait(false); // wait-ok: bounded by `window` above (#1804)
        }
    }

    private static async Task<(IReadOnlyList<LogEntry> Entries, IReadOnlyList<FlowEvent> FlowEvents)> ReadJournalAsync(
        FlowEventLogReader reader, CancellationToken cancellationToken)
    {
        var entries = await reader.ReadAllEntriesWithTimestampsAsync(cancellationToken).ConfigureAwait(false);
        var flowEvents = new List<FlowEvent>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry is LogEntry.FlowLogEntry flowLogEntry)
            {
                flowEvents.Add(flowLogEntry.Event);
            }
        }

        return (entries, flowEvents);
    }

    private static async Task<FlowState> ProjectAsync(
        FlowEventLogReader reader, WorkflowDefinitionSnapshot snapshot, string roomDirectoryPath, CancellationToken cancellationToken)
    {
        var events = await reader.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        return StateProjector.Project(events, snapshot, ProjectionCheckpointStore.Load(roomDirectoryPath));
    }
}
