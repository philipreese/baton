using Baton.Domain;
using Baton.Projection;
using Baton.Queue;
using Baton.Status;
using Baton.Store;
using Baton.Templates;
using Baton.Vendors;

namespace Baton.Cli.Daemon;

/// <summary>What the scheduler asks the launcher to start.</summary>
/// <param name="Item">The queued item.</param>
/// <param name="Tier">Its resolved adapter/model/effort — <see cref="QueueTierTable.Resolve"/>'s answer, never re-derived here.</param>
/// <param name="RoomDirectory">
/// The room to dispatch into — <see cref="QueueLauncher.RoomDirectoryFor"/>'s answer, chosen by the
/// scheduler rather than here because the scheduler writes it onto the item BEFORE the launch starts
/// (<c>QueueSchedulerService.EvaluateAsync</c> states why). Passed rather than re-derived: two
/// generators would put the item's recorded room and the dispatch's actual room one GUID apart.
/// </param>
public sealed record QueueLaunchRequest(QueueItem Item, QueueTierResolution Tier, string RoomDirectory);

/// <summary>
/// How a launch attempt ended.
/// </summary>
/// <param name="RoomDirectory">The room the dispatch provisioned; present even for a failure that got that far.</param>
/// <param name="RunwayHeld">
/// True when <c>baton dispatch</c>'s runway gate refused. Distinct from <paramref name="Error"/>
/// because the two have opposite consequences for the item's state — spec/baton.md §13 names them.
/// </param>
/// <param name="Error">Why the launch failed, or null when it started.</param>
public sealed record QueueLaunchOutcome(string? RoomDirectory, bool RunwayHeld = false, string? Error = null);

/// <summary>
/// Turns a queued item into a running lane, through the SAME code path <c>baton dispatch</c> uses
/// (#1934 slice 1, item 2) — <see cref="DispatchCommand.ExecuteAsync"/> in-process, not a shell-out.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not a shell-out</b> — spec/baton.md §13 has the argument. Concretely, in-process is what
/// gives this method a typed exception and a live evaluator to observe, where a child process would
/// have offered one integer.
/// </para>
/// <para>
/// <b>A hold is read off the evaluator, never off the exception</b> — spec/baton.md §13 has the
/// argument. Mechanically: this method wraps
/// <see cref="DispatchCommand.CreateDiskRunwayEvaluatorAsync"/> in <c>Observe</c>, which returns each
/// <see cref="RunwayDecision"/> unchanged and sets a local flag when one is
/// <see cref="RunwayDecision.IsHold"/>; the two <c>catch</c> arms below then differ only by that flag.
/// </para>
/// <para>
/// <b>The lane is not awaited to completion.</b> This method returns as soon as the outcome is known;
/// the pump keeps running on its own task. <see cref="QueueSchedulerService"/>'s remarks have the
/// shutdown posture that follows.
/// </para>
/// </remarks>
public static class QueueLauncher
{
    /// <summary>
    /// Starts <paramref name="request"/> and returns as soon as the outcome is known: a refusal
    /// (hold or error) or a provisioned room whose pump is now running detached.
    /// </summary>
    /// <param name="cancellationToken">
    /// Bounds only the work BEFORE the outcome is known — the evaluator read and the refusal poll. The
    /// scheduler hands in <see cref="CancellationToken.None"/> (#1939 review): the dispatch below runs
    /// on that token by design, and cancelling the observation of a launch that is still starting is
    /// what left an item recorded as not-launched while its worker ran. The poll has its own deadline,
    /// so nothing here is unbounded.
    /// </param>
    public static async Task<QueueLaunchOutcome> LaunchAsync(QueueLaunchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var item = request.Item;
        if (!File.Exists(item.SpecFile))
        {
            return new QueueLaunchOutcome(null, Error: $"spec file '{item.SpecFile}' is gone");
        }

        var roomDirectory = request.RoomDirectory;
        var options = BuildOptions(request);

        var held = false;
        var evaluator = await DispatchCommand.CreateDiskRunwayEvaluatorAsync(cancellationToken).ConfigureAwait(false);
        RunwayDecision Observe(string vendor)
        {
            var decision = evaluator(vendor);
            held |= decision.IsHold;
            return decision;
        }

        // The dispatch runs to Terminal, which for an implement lane is tens of minutes. It is started
        // here and NOT awaited: CancellationToken.None, deliberately, so stopping the daemon does not
        // arrest a lane it launched (QueueSchedulerService's own remarks state that posture and its
        // cost). The continuation is what observes the task's exception -- an unobserved faulted task
        // would surface, if at all, as a process-level UnobservedTaskException far from here.
        var pump = Task.Run(
            () => DispatchCommand.ExecuteAsync(options, WorkerAdapterRegistry.Default, CancellationToken.None, evaluateRunway: Observe),
            CancellationToken.None);

        // One bounded wait for a refusal. Every pre-provision refusal DispatchCommand can make -- drain
        // marker, bad spec, unknown role, runway hold -- happens before it creates the room directory,
        // so "the room now exists" is the discriminator between "refused" and "running", and it is the
        // engine's own ordering rather than a timing guess. The timeout is the backstop for a dispatch
        // that is neither: it reports launched, which is true, and the room's own record takes over.
        var deadline = DateTimeOffset.UtcNow + RefusalWindow;
        while (!pump.IsCompleted && !Directory.Exists(roomDirectory) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(RefusalPollInterval, cancellationToken).ConfigureAwait(false);
        }

        if (pump.IsCompleted)
        {
            try
            {
                var result = await pump.ConfigureAwait(false);
                await TerminalSettleRecorder.RecordAsync(result, CancellationToken.None).ConfigureAwait(false);
                return new QueueLaunchOutcome(roomDirectory);
            }
            catch (Exception ex) when (ex is BatonFlowException or CliArgumentException)
            {
                return held
                    ? new QueueLaunchOutcome(null, RunwayHeld: true)
                    : new QueueLaunchOutcome(Directory.Exists(roomDirectory) ? roomDirectory : null, Error: ex.Message);
            }
        }

        // Still running: settle it when it finishes, so a queue-launched room gets the same
        // terminal.json and ledger rows a `baton dispatch` from a terminal would (TerminalSettleRecorder
        // is that block, shared rather than copied).
        _ = pump.ContinueWith(
            completed => SettleFinishedPumpAsync(completed, item.Tag, roomDirectory),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);

        return new QueueLaunchOutcome(roomDirectory);
    }

    /// <summary>
    /// What the detached continuation does when the pump above finally finishes: record the settle a
    /// pump that reached Terminal produced, or — for one that did not — the room's own post-launch
    /// failure record (<see cref="RecordPostLaunchFaultAsync"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Split out of the continuation lambda purely as a test seam (#1939 review round 2): the pump is
    /// built inside <see cref="LaunchAsync"/> from <see cref="DispatchCommand.ExecuteAsync"/>, so a
    /// faulted or cancelled one is not otherwise drivable, and the cancel arm below shipped
    /// unexercised because of it.
    /// </para>
    /// <para>
    /// <b>Gated on <c>!IsCompletedSuccessfully</c>, not <c>IsFaulted</c>.</b> An
    /// <see cref="OperationCanceledException"/> escaping the pump leaves the task
    /// <see cref="TaskStatus.Canceled"/>, NOT faulted, so an <c>IsFaulted</c>-only test dropped such a
    /// pump into the settle branch, where <c>completed.Result</c> throws an
    /// <see cref="AggregateException"/> the <see cref="IOException"/>-only catch there does not cover —
    /// unobserved, inside a discarded continuation, leaving the item in
    /// <see cref="QueueItemState.Launched"/> with nothing on disk to resolve it.
    /// <see cref="Task.Exception"/> is null for a cancelled task, which is why the reason falls back per
    /// state rather than dereferencing it.
    /// </para>
    /// </remarks>
    internal static async Task SettleFinishedPumpAsync(Task<CommandResult> completed, string tag, string roomDirectory)
    {
        ArgumentNullException.ThrowIfNull(completed);

        if (!completed.IsCompletedSuccessfully)
        {
            var reason = completed.Exception?.GetBaseException().Message
                ?? (completed.IsCanceled ? "the lane was cancelled after launch" : "the lane faulted after launch");
            Console.Error.WriteLine(
                $"QueueLauncher: lane '{tag}' in '{roomDirectory}' did not complete after launch: {reason}");
            await RecordPostLaunchFaultAsync(tag, roomDirectory, reason).ConfigureAwait(false);
            return;
        }

        try
        {
            await TerminalSettleRecorder.RecordAsync(completed.Result, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"QueueLauncher: could not record the settle for '{tag}': {ex.Message}");
        }
    }

    /// <summary>
    /// The verdict a lane that faulted — or was cancelled — <em>after</em> launch would otherwise never
    /// leave behind (#1939 review). A dispatch that throws twenty minutes in reaches no Terminal state, so
    /// <see cref="TerminalSettleRecorder"/> never runs and the room carries no <c>terminal.json</c> —
    /// and <c>QueueSchedulerService.ResolveFinishedItemsAsync</c>, which reads exactly that file,
    /// leaves the item <see cref="QueueItemState.Launched"/> forever. This writes the room's own
    /// failure so the item resolves the same way every other settled room does: through the sentinel,
    /// not through a second channel.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two things it deliberately does NOT do — both rulings, stated in spec/baton.md §13 with the
    /// argument for each: it never creates the room directory (the scheduler's own sweep is what
    /// resolves an item whose room was never provisioned), and it never replaces a sentinel the room
    /// already carries.
    /// </para>
    /// <para>
    /// <b>What it writes is the room's own record, not a fabricated blank</b> (#1939 review round 2) —
    /// spec/baton.md §13's post-launch bullet has the argument, and the three consequences it states
    /// (the outcome word is always <c>Failed</c>, the fault is the terminal reason with the room's own
    /// recorded failure folded in beside it, and liveness is dropped) are what the code below and
    /// <see cref="TryProjectRoomAsync"/> implement. The one mechanical thing worth pointing at from
    /// here: the write below is unconditional, taken even when the projection comes back null — the
    /// register says why that room in particular cannot be skipped.
    /// </para>
    /// <para>
    /// <b>A HELD ledger is retried; a corrupt or missing one is not</b> (#1951) — spec/baton.md §13's
    /// post-launch bullet has the argument and the bound. Mechanically: <see cref="TryProjectRoomAsync"/>
    /// reports WHY it could not project, this loop re-reads only while that answer is
    /// <see cref="RoomProjectionFailure.Held"/>, and whichever answer it ends on is appended to the
    /// sentinel's <c>error</c> — so a bare sentinel says which of the two produced it rather than
    /// leaving a reader to guess. The retry is a re-READ inside one fault record, not the item-level
    /// retry spec/baton.md §13 rules out.
    /// </para>
    /// </remarks>
    /// <param name="tag">The queued item's tag.</param>
    /// <param name="roomDirectory">The room the lane was dispatched into.</param>
    /// <param name="reason">Why the pump did not complete.</param>
    /// <param name="heldAttempts">
    /// How many projection attempts a held ledger gets in total, including the first. A test seam in
    /// the same sense as <see cref="SettleFinishedPumpAsync"/>: a holder released on a real machine's
    /// timing cannot be steered from outside, so the two arms that measure this path set their own
    /// bound rather than sleeping against the default.
    /// </param>
    /// <param name="heldRetryDelay">The pause between those attempts; defaults to <see cref="HeldLedgerRetryDelay"/>.</param>
    internal static async Task RecordPostLaunchFaultAsync(
        string tag,
        string roomDirectory,
        string reason,
        int heldAttempts = HeldLedgerAttempts,
        TimeSpan? heldRetryDelay = null)
    {
        try
        {
            if (!Directory.Exists(roomDirectory)
                || await TerminalSentinelWriter.TryReadAsync(roomDirectory, CancellationToken.None)
                    .ConfigureAwait(false) is not null)
            {
                return;
            }

            var delay = heldRetryDelay ?? HeldLedgerRetryDelay;
            var attempt = await TryProjectRoomAsync(roomDirectory).ConfigureAwait(false);
            for (var remaining = heldAttempts - 1; attempt.Failure == RoomProjectionFailure.Held && remaining > 0; remaining--)
            {
                await Task.Delay(delay).ConfigureAwait(false);

                // The guard above is no longer adjacent to the write, and this loop waits on a holder
                // that is USUALLY the room's own live engine (FlowJournalHeldException's message names
                // it) — which records this room's settle and only then lets the ledger go. Re-reading
                // per attempt is what keeps a verdict landing mid-backoff instead of overwriting it
                // with a Failed one, spec/baton.md §13's never-replace rule at a window this retry
                // opened.
                if (await TerminalSentinelWriter.TryReadAsync(roomDirectory, CancellationToken.None)
                        .ConfigureAwait(false) is not null)
                {
                    return;
                }

                attempt = await TryProjectRoomAsync(roomDirectory).ConfigureAwait(false);
            }

            var projected = attempt.View;
            var error = $"the queue-launched lane '{tag}' did not complete after launch: {reason}";
            if (projected?.Error is { Length: > 0 } recordedFailure)
            {
                error += $" — the room's own last recorded failure: {recordedFailure}";
            }
            else if (projected is null && attempt.Reason is { Length: > 0 } degraded)
            {
                var stillHeld = attempt.Failure == RoomProjectionFailure.Held
                    ? $" (still held after {heldAttempts} attempt(s))"
                    : string.Empty;
                error += $" — this record carries no steps or outputs because {degraded}{stillHeld}";
            }

            var view = (projected ?? new WorkflowStatusView(WorkflowOutcome.Failed, [], [], null)) with
            {
                State = WorkflowOutcome.Failed,
                Error = error,
            };

            await TerminalSentinelWriter.WriteAsync(roomDirectory, view, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(
                $"QueueLauncher: could not record the post-launch fault for '{tag}' in '{roomDirectory}': {ex.Message}");
        }
    }

    /// <summary>
    /// Why <see cref="TryProjectRoomAsync"/> came back without a view. Only
    /// <see cref="Held"/> is worth waiting on — the other two are as true a second later as they are now.
    /// </summary>
    private enum RoomProjectionFailure
    {
        /// <summary>It projected.</summary>
        None,

        /// <summary>No ledger yet, or no bound snapshot — nothing to project, and nothing arriving.</summary>
        Absent,

        /// <summary>Another process holds <c>flow.jsonl</c> with a conflicting share; a release makes this projectable.</summary>
        Held,

        /// <summary>The ledger or the snapshot is there and could not be read or parsed.</summary>
        Unreadable,
    }

    /// <summary>One projection attempt: the view, or why there is none in the words the sentinel carries.</summary>
    private readonly record struct RoomProjection(WorkflowStatusView? View, RoomProjectionFailure Failure, string? Reason);

    /// <summary>Total projection attempts a held ledger gets, including the first.</summary>
    private const int HeldLedgerAttempts = 5;

    /// <summary>
    /// The pause between those attempts. Four of them bound the wait at ~1.2s, which is what a
    /// transient append by a sibling command costs; a live <c>baton run</c> engine holding the ledger
    /// for its whole run is not something any bound here can outwait, and degrading is the answer for
    /// that one — the item resolving is what matters, and it resolves either way.
    /// </summary>
    private static readonly TimeSpan HeldLedgerRetryDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// The room's <em>actual</em> state, projected the way <see cref="TerminalSettleRecorder"/> and
    /// <c>StatusCommand</c> already project one: bound snapshot + <c>flow.jsonl</c> +
    /// <c>ProjectionCheckpointStore</c>, through the same <c>StateProjector</c>/
    /// <see cref="WorkflowStatusProjector"/> pair, so the steps and outputs a post-launch fault freezes
    /// are the room's own and never a second derivation of what an event log means. Inlined here rather
    /// than shared with <c>FleetStatusTool.ProcessRoomAsync</c>'s identical block, which is a seam worth
    /// extracting on its own rather than inside this fix.
    /// <para>
    /// Returns a null view — never throws — for a room this cannot project: no real ledger yet
    /// (<see cref="RoomLedgerProbe"/>, which is also why the ledger-less room in
    /// <c>QueueLauncherTests</c> still gets the bare view), no bound snapshot, or a read/parse failure.
    /// The caller writes the bare <c>Failed</c> sentinel in that case: a degraded record still resolves
    /// the item, where a throw out of the discarded continuation this runs in would resolve nothing.
    /// </para>
    /// <para>
    /// <b>A sharing violation is <see cref="RoomProjectionFailure.Held"/>, not
    /// <see cref="RoomProjectionFailure.Unreadable"/></b> (#1951). <see cref="FlowJournalHeldException"/>
    /// derives from <see cref="BatonFlowException"/>, so the single catch below used to fold a ledger
    /// somebody is mid-append on into the same answer as a truncated one — and the caller, seeing one
    /// answer, could neither wait out the first nor say which had happened. The narrow arm comes first
    /// for that reason; the ORDER is the fix.
    /// </para>
    /// <para>
    /// <b><see cref="WorkflowStatusStepView.Liveness"/> is dropped from every step</b>, while each
    /// step's recorded <c>state</c> is kept as projected, a mid-lane <c>Running</c> included —
    /// spec/baton.md §13's post-launch bullet has why the two are treated differently.
    /// </para>
    /// </summary>
    private static async Task<RoomProjection> TryProjectRoomAsync(string roomDirectory)
    {
        var snapshotPath = Path.Combine(roomDirectory, BatonPaths.SnapshotFileName);
        if (!RoomLedgerProbe.HasLedger(roomDirectory) || !File.Exists(snapshotPath))
        {
            return new RoomProjection(
                null,
                RoomProjectionFailure.Absent,
                "the room carries no ledger of its own, or no bound snapshot to project it against");
        }

        try
        {
            var snapshot = await SnapshotBinder.LoadFromFileAsync(snapshotPath, CancellationToken.None).ConfigureAwait(false);
            var entries = await new FlowEventLogReader(Path.Combine(roomDirectory, BatonPaths.FlowLogFileName))
                .ReadAllEntriesWithTimestampsAsync(CancellationToken.None).ConfigureAwait(false);

            var events = new List<FlowEvent>(entries.Count);
            foreach (var entry in entries)
            {
                if (entry is LogEntry.FlowLogEntry flowLogEntry)
                {
                    events.Add(flowLogEntry.Event);
                }
            }

            var state = StateProjector.Project(events, snapshot, ProjectionCheckpointStore.Load(roomDirectory));
            var view = WorkflowStatusProjector.Project(
                state, snapshot, roomDirectory, entries, WorkerAdapterRegistry.Default);

            return new RoomProjection(
                view with { Steps = [.. view.Steps.Select(step => step with { Liveness = null })] },
                RoomProjectionFailure.None,
                null);
        }
        catch (FlowJournalHeldException ex)
        {
            // Must precede the BatonFlowException arm below, which it would otherwise be swallowed by
            // — see this method's own remarks for why the two answers cannot share one.
            Console.Error.WriteLine(
                $"QueueLauncher: '{roomDirectory}' could not be projected for its post-launch fault "
                + $"record because its ledger is held: {ex.Message}");
            return new RoomProjection(
                null, RoomProjectionFailure.Held, $"the room's ledger is held open by another process: {ex.Message}");
        }
        catch (Exception ex) when (ex is BatonFlowException or IOException or UnauthorizedAccessException)
        {
            // SnapshotLoadException and FlowEventLogReadException are both BatonFlowException. Named
            // rather than swallowed: the sentinel this degrades to says the lane failed but not what it
            // had done, and the difference is otherwise invisible.
            Console.Error.WriteLine(
                $"QueueLauncher: could not project '{roomDirectory}' for its post-launch fault record, "
                + $"so its sentinel carries no steps or outputs: {ex.Message}");
            return new RoomProjection(
                null,
                RoomProjectionFailure.Unreadable,
                $"the room's ledger or snapshot could not be read: {ex.Message}");
        }
    }

    /// <summary>
    /// The room a queued item dispatches into: <c>queue-&lt;tag&gt;-&lt;8 hex&gt;</c> under
    /// <see cref="BatonPaths.Rooms"/>. Here rather than in the scheduler that calls it because the
    /// naming is this launcher's convention — the tag leads so a room is traceable to its item by
    /// eye, and the suffix keeps a re-added tag from colliding with its own earlier room.
    /// </summary>
    internal static string RoomDirectoryFor(QueueItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return Path.Combine(BatonPaths.Rooms, $"queue-{item.Tag}-{Guid.NewGuid().ToString("N")[..8]}");
    }

    /// <summary>How long to wait for a pre-provision refusal before reporting the lane launched.
    /// Every refusal happens before the room directory is created, so this is a backstop, not the
    /// mechanism.
    /// <para>
    /// <b>Coupled to <see cref="OnDemandRunwayHarvest.Bound"/> since #1923</b>, which is why that is
    /// named here rather than left for the next reader to find: the runway hold's inline harvest runs
    /// inside this same pre-provision phase, once per gated vendor with no snapshot, so a cold
    /// mixed-vendor dispatch can spend that bound twice before anything else in the phase starts. The
    /// headroom this window leaves the rest of pre-provision is therefore this value minus up to two
    /// bounds, not the whole of it. Changing either constant is a change to both.
    /// </para>
    /// </summary>
    public static readonly TimeSpan RefusalWindow = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan RefusalPollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// The item plus its tier, as <c>baton dispatch</c>'s own options. Kept separate from
    /// <see cref="LaunchAsync"/> so a test can assert what the queue forwards without running a
    /// dispatch.
    /// </summary>
    internal static DispatchOptions BuildOptions(QueueLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var item = request.Item;
        var tier = request.Tier;

        return new DispatchOptions(
            Name: item.Role,
            SpecFilePath: item.SpecFile,
            RoomDirectoryPath: request.RoomDirectory,
            Adapter: tier.Adapter,
            WorkspaceDirectory: item.Workspace,
            Model: tier.Model,
            Effort: tier.Effort,
            Timeout: item.TimeoutMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : null,
            // WorkerBindingConfigEntry.Label is the bindings field spec/baton.md §13 requires the
            // override's justification to reach; it doubles as the tag-to-room trace, so one field
            // carries both rather than a new one carrying half. Sanitized through the same
            // SanitizeLabel the CLI flag uses -- including its 60-character cap, which is why the tag
            // leads: a truncated justification still leaves the room identifiable.
            Label: DispatchOptionsParser.SanitizeLabel(
                tier.IsOverride && tier.OverrideReason is { Length: > 0 } reason
                    ? $"{item.Tag} — tier override: {reason}"
                    : item.Tag),
            TokenBudget: item.TokenBudget,
            MaxToolSteps: item.MaxToolSteps,
            OverrideRunwayReason: item.OverrideRunwayReason);
    }
}
