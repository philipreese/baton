using System.Diagnostics;
using Baton.Core.Internal;
using Baton.Cli.Mcp;
using Baton.Queue;
using Baton.Status;
using Baton.Vendors;
using Microsoft.Extensions.Hosting;

namespace Baton.Cli.Daemon;

/// <summary>
/// The conductor queue's scheduler (#1934 slice 1, Q1 answer (b)): hosted in the daemon beside the
/// usage harvester and the fleet projection writer, and the only thing that launches a queued item.
/// </summary>
/// <remarks>
/// <para>
/// <b>All the policy lives in <see cref="QueueScheduler.Decide"/>, which is pure.</b> This service is
/// the I/O around it — read the queue, tally the live rooms, read free memory, call
/// <c>Decide</c>, launch, record the fact, resolve finished items. Every arm of the policy is
/// testable without any of that.
/// </para>
/// <para>
/// <b>A dispatched lane outlives the tick that started it, and outlives this service too</b>
/// (spec/baton.md §13 states the shutdown ruling and what it costs). Here that means one thing to
/// hold on to: <see cref="ResolveFinishedItemsAsync"/> is what closes an item out, it reads the room
/// off disk, and it therefore also closes out items some earlier daemon process started.
/// </para>
/// <para>
/// <b>The runway hold is discovered, not predicted (Q5)</b> — <see cref="QueueLauncher"/> owns that
/// mechanism. What this service does with it: a <see cref="QueueLaunchOutcome.RunwayHeld"/> outcome
/// leaves the item's state untouched, where a <see cref="QueueLaunchOutcome.Error"/> moves it to
/// <see cref="QueueItemState.Failed"/>.
/// </para>
/// </remarks>
public sealed class QueueSchedulerService : BackgroundService
{
    private readonly Func<QueueLaunchRequest, CancellationToken, Task<QueueLaunchOutcome>> _launch;
    private readonly Func<CancellationToken, Task<double>> _liveWeight;
    private readonly Func<double?> _freeGb;
    private readonly Func<DateTimeOffset> _now;
    private readonly WorkItemAdvancer _advancer;

    private DateTimeOffset? _lastLaunchAt;
    private string? _lastVerdictKey;

    public QueueSchedulerService()
        : this(null, null, null, null)
    {
    }

    /// <summary>
    /// Test seam (Baton.Cli.Tests, via <c>InternalsVisibleTo</c>): every source of nondeterminism is a
    /// delegate, so <see cref="TickOnceAsync"/>'s arms run with a fake clock, a fake memory reading and
    /// a fake live tally, and never spawn a process.
    /// </summary>
    internal QueueSchedulerService(
        Func<QueueLaunchRequest, CancellationToken, Task<QueueLaunchOutcome>>? launch,
        Func<CancellationToken, Task<double>>? liveWeight,
        Func<double?>? freeGb,
        Func<DateTimeOffset>? now,
        WorkItemAdvancer? advancer = null)
    {
        _launch = launch ?? QueueLauncher.LaunchAsync;
        _liveWeight = liveWeight ?? CountLiveWeightAsync;
        _freeGb = freeGb ?? FreePhysicalMemory.TryReadGiB;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _advancer = advancer ?? new WorkItemAdvancer();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var started = Stopwatch.GetTimestamp();
            TimeSpan interval;
            try
            {
                interval = await TickOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"QueueSchedulerService: iteration failed: {ex.Message}");
                interval = TimeSpan.FromSeconds(QueueSettings.DefaultTickSeconds);
            }

            // #1981 (rules: DaemonTickLedger). `interval` here is this tick's OWN next-delay decision,
            // which TickOnceAsync returns -- so the heartbeat file reports the cadence this service is
            // actually running at rather than a fixed default it may not be using.
            DaemonTickLedger.Instance.RecordTick(
                nameof(QueueSchedulerService), Stopwatch.GetElapsedTime(started), interval);

            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// One evaluation: resolve anything that finished, decide, launch if the decision says so, and
    /// record the fact. Returns the interval until the next tick.
    /// </summary>
    /// <remarks>
    /// <b>No evaluation leaves the ledger silent</b> — spec/baton.md §13 has that ruling. Each arm of
    /// <see cref="EvaluateAsync"/> records its own decision; this wrapper covers a throw that reaches
    /// none of them. The row's <c>liveWeight</c>/<c>floorGb</c> read zero only because nothing ever
    /// read them, which is what its reason says out loud, and <c>freeGb</c> is absent for the same
    /// reason rather than fabricated. The collapse on
    /// <see cref="QueueDecisionEntry.VerdictKey"/> keeps a tick-after-tick repeat to one line.
    /// </remarks>
    internal async Task<TimeSpan> TickOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await EvaluateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            await RecordAsync(
                new QueueDecisionEntry(
                    _now(), null, QueueDecisionEntry.Failed,
                    $"the evaluation itself failed and recorded no counters: {ex.Message}",
                    LiveWeight: 0, FreeGb: null, FloorGb: 0),
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<TimeSpan> EvaluateAsync(CancellationToken cancellationToken)
    {
        var settings = (await DaemonSettingsStore.LoadAsync(BatonPaths.SettingsFile, cancellationToken)
            .ConfigureAwait(false)).Queue;
        var interval = TimeSpan.FromSeconds(settings.EffectiveTickSeconds);

        await ResolveFinishedItemsAsync(cancellationToken).ConfigureAwait(false);

        // The lifecycle advance runs BETWEEN done detection and the launch decision, and both
        // orderings matter (#1934 slice 2). After resolve, because it acts on items resolve has just
        // moved out of `launched`; before Decide, because an item it queues for its next round is a
        // candidate this same tick rather than one tick later.
        await AdvanceWorkItemsAsync(cancellationToken).ConfigureAwait(false);

        var snapshot = await QueueStore.LoadAsync(BatonPaths.QueueFile, cancellationToken).ConfigureAwait(false);
        var now = _now();
        var liveWeight = await _liveWeight(cancellationToken).ConfigureAwait(false);
        var freeGb = _freeGb();

        var decision = QueueScheduler.Decide(now, snapshot.Items, liveWeight, freeGb, settings, _lastLaunchAt, snapshot.Held);

        if (decision.Kind == QueueDecisionKind.Wait)
        {
            await RecordAsync(
                new QueueDecisionEntry(
                    now, decision.Item?.Tag, QueueDecisionEntry.Waited,
                    QueueWaitReasons.Token(decision.WaitReason!.Value),
                    decision.LiveWeight, decision.FreeGb, decision.FloorGb),
                cancellationToken).ConfigureAwait(false);
            return interval;
        }

        var item = decision.Item!;
        var tier = QueueTierTable.Resolve(item, settings);

        // Fail closed, per spec/baton.md §13's tier-resolution ruling. Reachable only through a
        // hand-edited queue file, since QueueOptionsParser already refuses the scope class -- which is
        // why the daemon checks anyway rather than trusting the verb that wrote the item.
        if (item.ScopeClass is { Length: > 0 } scopeClass && tier.TierKey is not null
            && QueueTierTable.LookupTier(tier.TierKey, settings) is null)
        {
            await FailAsync(
                item, $"no tier is configured for '{tier.TierKey}' (scope class '{scopeClass}', role '{item.Role}')",
                room: null, now, decision, tier, cancellationToken).ConfigureAwait(false);
            return interval;
        }

        // The launch is RECORDED BEFORE IT IS STARTED, and started under the same token it was recorded
        // under -- spec/baton.md §13 states that ruling and the duplicate-worker failure it closes.
        // What belongs here rather than there: the two writes are deliberately asymmetric. The ITEM is
        // written first, because it is what the next daemon reads to pick candidates; the LEDGER row
        // waits for the outcome, below.
        var roomDirectory = QueueLauncher.RoomDirectoryFor(item);
        _lastLaunchAt = now;
        await MarkAsync(item.Tag, existing => existing with
        {
            State = QueueItemState.Launched,
            RoomDirectory = roomDirectory,
            LaunchedAt = now,
            Error = null,
        }).ConfigureAwait(false);

        QueueLaunchOutcome outcome;
        try
        {
            outcome = await _launch(new QueueLaunchRequest(item, tier, roomDirectory), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Nothing in the launch takes a cancellable token any more, so this is the belt to that
            // braces. `failed`, not left launched: the queue has lost track of whether a lane started,
            // and the room id on the item is how an operator finds out.
            await FailAsync(
                item, $"the daemon shut down while launching into room '{roomDirectory}'; check that room before "
                + "re-adding this item, because the lane may have started", roomDirectory, now, decision, tier,
                CancellationToken.None).ConfigureAwait(false);
            return interval;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // A throw the launcher does not model as a QueueLaunchOutcome -- an IO failure inside
            // TerminalSettleRecorder, say. Mapped to the item's own Failed state and one recorded fact
            // rather than unwound into the loop's catch-all, which would leave the item launched with
            // nothing said about why (#1939 review).
            await FailAsync(
                item, $"the launch into room '{roomDirectory}' threw {ex.GetType().Name}: {ex.Message}",
                Directory.Exists(roomDirectory) ? roomDirectory : null, now, decision, tier,
                CancellationToken.None).ConfigureAwait(false);
            return interval;
        }

        if (outcome.RunwayHeld)
        {
            // The item goes back to QUEUED, undoing the pre-launch mark above: nothing was dispatched,
            // so it must be the candidate again next tick (Q5's arm). _lastLaunchAt stays advanced, so
            // the gap paces the retry -- a held vendor must not be re-asked every TickSeconds.
            await MarkAsync(item.Tag, existing => existing with
            {
                State = QueueItemState.Queued,
                RoomDirectory = null,
                LaunchedAt = null,
            }).ConfigureAwait(false);
            await RecordAsync(
                new QueueDecisionEntry(
                    now, item.Tag, QueueDecisionEntry.Waited,
                    QueueWaitReasons.Token(QueueWaitReason.RunwayHeld),
                    decision.LiveWeight, decision.FreeGb, decision.FloorGb,
                    tier.TierKey, tier.Adapter, tier.Model, tier.Effort, tier.IsOverride, tier.OverrideReason),
                cancellationToken).ConfigureAwait(false);
            return interval;
        }

        if (outcome.Error is { Length: > 0 } error)
        {
            // outcome.RoomDirectory, not the path above: the launcher reports it only when the dispatch
            // actually provisioned the room, and a refusal that never got that far must leave the item
            // pointing at nothing rather than at a directory that does not exist.
            await FailAsync(item, error, outcome.RoomDirectory, now, decision, tier, CancellationToken.None)
                .ConfigureAwait(false);
            return interval;
        }

        // The item is already marked launched, above. All that is left is the fact.
        await RecordAsync(
            new QueueDecisionEntry(
                now, item.Tag, QueueDecisionEntry.Launched, null,
                decision.LiveWeight, decision.FreeGb, decision.FloorGb,
                tier.TierKey, tier.Adapter, tier.Model, tier.Effort, tier.IsOverride, tier.OverrideReason,
                outcome.RoomDirectory ?? roomDirectory),
            CancellationToken.None).ConfigureAwait(false);

        return interval;
    }

    /// <summary>
    /// Advances every settled work item one stage (#1934 slice 2) and records one fact per transition.
    /// </summary>
    /// <remarks>
    /// <b>A failure here does not stop the tick.</b> The advance reads <c>gh</c> and a worktree — two
    /// things that can be missing on a machine whose queue is otherwise fine — and letting that unwind
    /// into <see cref="TickOnceAsync"/>'s catch-all would stop the scheduler launching anything at all.
    /// Logged and recorded as a failed decision, never swallowed silently.
    /// </remarks>
    internal async Task AdvanceWorkItemsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<QueueDecisionEntry> facts;
        try
        {
            facts = await _advancer.AdvanceAsync(_now(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine($"QueueSchedulerService: advancing work items failed: {ex.Message}");
            await RecordAsync(
                new QueueDecisionEntry(
                    _now(), null, QueueDecisionEntry.Failed,
                    $"the work-item advance failed, so no item changed stage this tick: {ex.Message}",
                    LiveWeight: 0, FreeGb: null, FloorGb: 0),
                CancellationToken.None).ConfigureAwait(false);
            return;
        }

        foreach (var fact in facts)
        {
            await RecordAsync(fact, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One item's state change, on <see cref="CancellationToken.None"/> deliberately: these writes
    /// record a launch that runs on that same token, and
    /// <see cref="QueueStore.MutateAsync"/> on an already-cancelled token never runs its delegate at
    /// all — which is how a shutdown mid-launch used to lose the launch entirely.
    /// </summary>
    private static Task MarkAsync(string tag, Func<QueueItem, QueueItem> update) =>
        QueueStore.MutateAsync(
            BatonPaths.QueueFile,
            s => s with { Items = Replace(s.Items, tag, update) },
            CancellationToken.None);

    private async Task FailAsync(
        QueueItem item,
        string error,
        string? room,
        DateTimeOffset now,
        QueueDecision decision,
        QueueTierResolution tier,
        CancellationToken cancellationToken)
    {
        // RoomDirectory is assigned, never merged with what the item already carried: the pre-launch
        // mark writes the room the dispatch was GOING to use, and a refusal that never provisioned it
        // must not leave that path behind as if a room existed to go and read.
        await MarkAsync(item.Tag, existing => existing with
        {
            State = QueueItemState.Failed,
            Error = error,
            RoomDirectory = room,
        }).ConfigureAwait(false);

        await RecordAsync(
            new QueueDecisionEntry(
                now, item.Tag, QueueDecisionEntry.Failed, error,
                decision.LiveWeight, decision.FreeGb, decision.FloorGb,
                tier.TierKey, tier.Adapter, tier.Model, tier.Effort, tier.IsOverride, tier.OverrideReason, room),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Done detection (spec/baton.md §13): every launched item whose room now carries a terminal
    /// sentinel is moved out of <see cref="QueueItemState.Launched"/> by
    /// <see cref="ClassifyTerminal"/>. One queue read, one write for the whole batch — a per-item
    /// mutation would take the file lock once per launched item every tick.
    /// </summary>
    /// <remarks>
    /// Nothing here retries, resolves, or composes a continuation; the item is marked and left alone.
    /// An item whose room is not terminal yet is untouched, which is also what happens to one whose
    /// sentinel is momentarily unreadable — <c>TryReadAsync</c>'s "no answer yet" is indistinguishable
    /// from "not finished", and treating it as either kind of verdict would be a guess.
    /// <para>
    /// The one case where a missing sentinel IS a verdict is <see cref="IsRoomlessPastGrace"/>: a room
    /// that does not exist long after the launch was recorded can never produce one.
    /// </para>
    /// <para>
    /// An item carrying no room at all is skipped entirely by the filter above — that is the imported
    /// launched item <c>QueueImport</c>'s own remarks say the operator clears by hand, and it must not
    /// be swept as if the queue had launched it.
    /// </para>
    /// </remarks>
    internal async Task ResolveFinishedItemsAsync(CancellationToken cancellationToken)
    {
        var snapshot = await QueueStore.LoadAsync(BatonPaths.QueueFile, cancellationToken).ConfigureAwait(false);
        var launched = snapshot.Items
            .Where(i => i.State == QueueItemState.Launched && i.RoomDirectory is { Length: > 0 })
            .ToList();
        if (launched.Count == 0)
        {
            return;
        }

        var resolved = new Dictionary<string, (QueueItemState State, string? Error)>(StringComparer.Ordinal);
        foreach (var item in launched)
        {
            var sentinel = await TerminalSentinelWriter.TryReadAsync(item.RoomDirectory!, cancellationToken).ConfigureAwait(false);
            if (sentinel is not null)
            {
                resolved[item.Tag] = ClassifyTerminal(sentinel, item.RoomDirectory!);
                continue;
            }

            if (IsRoomlessPastGrace(item))
            {
                resolved[item.Tag] = (QueueItemState.Failed,
                    $"room {item.RoomDirectory} was never created — the dispatch refused or faulted before it "
                    + "provisioned the room, so nothing ran; re-add the item once you know why");
            }
        }

        if (resolved.Count == 0)
        {
            return;
        }

        await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with
        {
            Items = s.Items
                .Select(i => resolved.TryGetValue(i.Tag, out var outcome) && i.State == QueueItemState.Launched
                    ? i with { State = outcome.State, Error = outcome.Error }
                    : i)
                .ToList(),
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The other half of "no item stays <see cref="QueueItemState.Launched"/> forever" (#1939 review):
    /// a launch whose room was never created at all. A dispatch that refuses or faults <em>before</em>
    /// provisioning leaves nothing to read — no room, so no sentinel for
    /// <see cref="ResolveFinishedItemsAsync"/> to classify and no
    /// <c>QueueLauncher.RecordPostLaunchFaultAsync</c> write either, since that one deliberately never
    /// manufactures a room.
    /// </summary>
    /// <remarks>
    /// Gated on a grace period rather than read the instant the room is missing: the item is marked
    /// launched before the dispatch starts, so "the room does not exist yet" is the ordinary reading
    /// for the first seconds. <see cref="QueueLauncher.RefusalWindow"/> bounds the dispatch's own
    /// pre-provision phase; the five minutes on top are slack for a slow <c>git</c> spawn (which
    /// happens before the room is created) and a coarse tick. The trade it accepts, said out loud: a
    /// dispatch still stuck in a pre-provision spawn after that long is called failed here, and if it
    /// later recovers, its lane runs against an item already marked failed — the room id on the item
    /// is what makes that findable.
    /// </remarks>
    internal static readonly TimeSpan NoRoomGrace = QueueLauncher.RefusalWindow + TimeSpan.FromMinutes(5);

    private bool IsRoomlessPastGrace(QueueItem item) =>
        !Directory.Exists(item.RoomDirectory!)
        && item.LaunchedAt is { } launchedAt
        && _now() - launchedAt > NoRoomGrace;

    /// <summary>
    /// Which state a settled room puts its item in — split out from the I/O above because this is the
    /// part worth a test.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The room's own outcome word decides, in the same vocabulary the projector emits</b> —
    /// <see cref="WorkflowOutcome"/>'s constants, which <c>WorkflowStatusProjector</c> and
    /// <see cref="TerminalSentinelWriter.WriteValidationRefusedAsync"/> are the only producers of.
    /// The succeeded-shaped words are <see cref="QueueItemState.Done"/> and every other word is a
    /// failure carrying that word. <b>Which words those are is asked of
    /// <see cref="WorkflowOutcome.IsSucceededShaped"/>, never spelled here</b>: a consumer that spells
    /// the membership itself is one that silently stops honouring it the day a third word is added.
    /// What reading the step list or <see cref="WorkflowStatusView.Error"/> instead cost is in
    /// spec/baton.md §13, with the ruling.
    /// </para>
    /// <para>
    /// <b>Fails closed on a word this assembly does not know</b>, including the null a hand-written
    /// <c>terminal.json</c> with no <c>state</c> field deserializes to: an unreadable verdict is not a
    /// clean settle. <see cref="WorkflowStatusView.Error"/> is detail on the message, never the
    /// verdict.
    /// </para>
    /// <para>
    /// Indeterminate keeps a sentence of its own because it is the one outcome with a remedy
    /// (<c>baton resolve</c>). It is read at the ROOM level, never off a step: the #1608
    /// single-added-enum-value ruling leaves an indeterminate step projecting as
    /// <c>StepStatus.Failed</c>, so no step state ever carries the word.
    /// </para>
    /// <para>
    /// The failure message names the room, because a marked item with nowhere to look is not
    /// investigable.
    /// </para>
    /// </remarks>
    internal static (QueueItemState State, string? Error) ClassifyTerminal(WorkflowStatusView sentinel, string roomDirectory)
    {
        ArgumentNullException.ThrowIfNull(sentinel);

        // Above the switch because a switch expression cannot call a predicate in a case pattern, and
        // the predicate is the point: #1945's FinishedDuringTeardown is Done beside Succeeded — the
        // room finished inside its box, its work is on the remote, and the timeout kill landed after
        // that push — but naming the two words here is what WorkflowOutcome.IsSucceededShaped exists
        // to stop.
        if (WorkflowOutcome.IsSucceededShaped(sentinel.State))
        {
            return (QueueItemState.Done, null);
        }

        var detail = sentinel.Error is { Length: > 0 } error ? $": {error}" : string.Empty;
        return sentinel.State switch
        {
            WorkflowOutcome.Indeterminate => (QueueItemState.Failed,
                $"room {roomDirectory} settled indeterminate{detail} — resolve it with 'baton resolve' and "
                + "redispatch if you want it redone"),
            _ => (QueueItemState.Failed, $"room {roomDirectory} settled {DescribeOutcome(sentinel.State)}{detail}"),
        };
    }

    /// <summary>The outcome word verbatim, or a sentence for the sentinel that carries none.</summary>
    private static string DescribeOutcome(string? state) =>
        string.IsNullOrWhiteSpace(state) ? "with no outcome word" : state;

    private async Task RecordAsync(QueueDecisionEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            _lastVerdictKey = await QueueDecisionLedgerStore
                .AppendAsync(entry, _lastVerdictKey, BatonPaths.QueueDecisionLedgerFile, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            // The ledger's own fail-open contract: a recording failure must never be the reason a lane
            // that already launched is treated as not having launched. Logged, never swallowed silently.
            Console.Error.WriteLine(
                $"Could not append to the queue decision ledger at '{BatonPaths.QueueDecisionLedgerFile}': {ex.Message}.");
        }
    }

    private static IReadOnlyList<QueueItem> Replace(
        IReadOnlyList<QueueItem> items, string tag, Func<QueueItem, QueueItem> update) =>
        items.Select(i => string.Equals(i.Tag, tag, StringComparison.Ordinal) ? update(i) : i).ToList();

    /// <summary>
    /// The live tally, over the SAME room scan <c>fleet_status</c> and
    /// <see cref="FleetProjectionWriter"/> already walk (a second scan on this service's own tick, so
    /// the three background services stay decoupled — <see cref="VendorUsageHarvester"/>'s own remarks
    /// state that trade). <see cref="QueueWeights.For"/> is the one weight function, called here over
    /// running rooms and in <see cref="QueueScheduler"/> over the candidate.
    /// </summary>
    private static async Task<double> CountLiveWeightAsync(CancellationToken cancellationToken)
    {
        var total = 0.0;
        var discovered = await FleetStatusTool.DiscoverRoomsAsync([], cancellationToken).ConfigureAwait(false);
        foreach (var room in discovered)
        {
            var view = await FleetStatusTool.ProcessRoomAsync(room.RoomDir, includeTerminal: false, cancellationToken)
                .ConfigureAwait(false);
            if (view is null || view.State != "Running")
            {
                continue;
            }

            total += QueueWeights.For(view.Role, view.Adapter);
        }

        return total;
    }
}
