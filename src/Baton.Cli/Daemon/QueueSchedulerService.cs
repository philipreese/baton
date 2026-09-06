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
/// <b>Launch is detached, and shutdown does not arrest it.</b> A dispatched lane runs for tens of
/// minutes; awaiting it inline would stop the queue evaluating for that whole time. So
/// <see cref="LaunchAsync"/> starts the dispatch on its own task with
/// <see cref="CancellationToken.None"/>, deliberately NOT the host's stopping token: stopping the
/// daemon must not kill lanes it started, the same posture the runway hold takes ("work already
/// running is unaffected"). The cost, stated rather than left emergent: a daemon that exits while a
/// queue-launched lane is live orphans that lane's supervision — the room's own record and its
/// worker keep going, but nothing marks the item done until a daemon comes back and re-reads the
/// room, which <see cref="ResolveFinishedItemsAsync"/> does from disk precisely so that a restart
/// recovers.
/// </para>
/// <para>
/// <b>The runway hold is discovered, not predicted (Q5).</b> The queue never reads <c>/usage</c>.
/// <see cref="LaunchAsync"/> hands <c>baton dispatch</c> a capturing wrapper around its own runway
/// evaluator and branches on <em>what the wrapper recorded</em>, never on the exception type: a
/// <see cref="CliArgumentException"/> is also what a missing spec file, an unknown role and a drain
/// marker raise, and treating those as "held" would leave a permanently-broken item retrying every
/// gap forever with a false reason in the ledger.
/// </para>
/// </remarks>
public sealed class QueueSchedulerService : BackgroundService
{
    private readonly Func<QueueLaunchRequest, CancellationToken, Task<QueueLaunchOutcome>> _launch;
    private readonly Func<CancellationToken, Task<double>> _liveWeight;
    private readonly Func<double?> _freeGb;
    private readonly Func<DateTimeOffset> _now;

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
        Func<DateTimeOffset>? now)
    {
        _launch = launch ?? QueueLauncher.LaunchAsync;
        _liveWeight = liveWeight ?? CountLiveWeightAsync;
        _freeGb = freeGb ?? FreePhysicalMemory.TryReadGiB;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
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
    internal async Task<TimeSpan> TickOnceAsync(CancellationToken cancellationToken)
    {
        var settings = (await DaemonSettingsStore.LoadAsync(BatonPaths.SettingsFile, cancellationToken)
            .ConfigureAwait(false)).Queue;
        var interval = TimeSpan.FromSeconds(settings.EffectiveTickSeconds);

        await ResolveFinishedItemsAsync(cancellationToken).ConfigureAwait(false);

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

        // Fail closed on a scope class with no tier: an item that named one and got nothing back would
        // otherwise silently launch on the role's own default model, which is exactly the "silently ran
        // on the wrong tier" failure the table exists to prevent.
        if (item.ScopeClass is { Length: > 0 } scopeClass && tier.TierKey is not null
            && QueueTierTable.LookupTier(tier.TierKey, settings) is null)
        {
            await FailAsync(
                item, $"no tier is configured for '{tier.TierKey}' (scope class '{scopeClass}', role '{item.Role}')",
                room: null, now, decision, tier, cancellationToken).ConfigureAwait(false);
            return interval;
        }

        var outcome = await _launch(new QueueLaunchRequest(item, tier), cancellationToken).ConfigureAwait(false);

        if (outcome.RunwayHeld)
        {
            // Q5: the item STAYS QUEUED and retries after the gap. The hold is a fleet condition, not a
            // property of this item, so consuming a queue slot for it would be wrong.
            _lastLaunchAt = now;
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
            await FailAsync(item, error, outcome.RoomDirectory, now, decision, tier, cancellationToken).ConfigureAwait(false);
            return interval;
        }

        _lastLaunchAt = now;
        await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with
        {
            Items = Replace(s.Items, item.Tag, existing => existing with
            {
                State = QueueItemState.Launched,
                RoomDirectory = outcome.RoomDirectory,
                LaunchedAt = now,
                Error = null,
            }),
        }, cancellationToken).ConfigureAwait(false);

        await RecordAsync(
            new QueueDecisionEntry(
                now, item.Tag, QueueDecisionEntry.Launched, null,
                decision.LiveWeight, decision.FreeGb, decision.FloorGb,
                tier.TierKey, tier.Adapter, tier.Model, tier.Effort, tier.IsOverride, tier.OverrideReason,
                outcome.RoomDirectory),
            cancellationToken).ConfigureAwait(false);

        return interval;
    }

    private async Task FailAsync(
        QueueItem item,
        string error,
        string? room,
        DateTimeOffset now,
        QueueDecision decision,
        QueueTierResolution tier,
        CancellationToken cancellationToken)
    {
        await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with
        {
            Items = Replace(s.Items, item.Tag, existing => existing with
            {
                State = QueueItemState.Failed,
                Error = error,
                RoomDirectory = room ?? existing.RoomDirectory,
            }),
        }, cancellationToken).ConfigureAwait(false);

        await RecordAsync(
            new QueueDecisionEntry(
                now, item.Tag, QueueDecisionEntry.Failed, error,
                decision.LiveWeight, decision.FreeGb, decision.FloorGb,
                tier.TierKey, tier.Adapter, tier.Model, tier.Effort, tier.IsOverride, tier.OverrideReason, room),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Item 5's done detection: a launched item whose room has reached a terminal state becomes
    /// <see cref="QueueItemState.Done"/>, or <see cref="QueueItemState.Failed"/> when that terminal
    /// state is Indeterminate or a timeout. Read from the ROOM, never from a sentinel file the queue
    /// writes for itself — a restarted daemon resolves an item it never launched.
    /// </summary>
    /// <remarks>
    /// Resolving and redispatching stay operator verbs in slice 1: nothing here retries, resolves, or
    /// composes a continuation. The item is marked and left alone, with its room id, which is what
    /// makes the failure investigable.
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
            if (sentinel is null)
            {
                continue;
            }

            resolved[item.Tag] = ClassifyTerminal(sentinel, item.RoomDirectory!);
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
    /// Item 5's classification, split out because it is the part worth a test: a room that ended
    /// Indeterminate or timed out is <see cref="QueueItemState.Failed"/> WITH the room id, everything
    /// else terminal is <see cref="QueueItemState.Done"/>.
    /// </summary>
    /// <remarks>
    /// The Indeterminate reading comes from the sentinel's own step states, not from a second
    /// taxonomy: <c>StepStatus.IndeterminateAwaitingResolution</c> is what the engine records for a
    /// worker that neither succeeded nor failed usefully, and a timeout settles there too. A room
    /// carrying an <c>Error</c> is a plain failure and is also not "done" — a done item is one nobody
    /// needs to look at.
    /// </remarks>
    internal static (QueueItemState State, string? Error) ClassifyTerminal(WorkflowStatusView sentinel, string roomDirectory)
    {
        ArgumentNullException.ThrowIfNull(sentinel);

        var indeterminate = sentinel.Steps?.Any(
            s => s.State.Contains("Indeterminate", StringComparison.OrdinalIgnoreCase)) ?? false;
        if (indeterminate)
        {
            return (QueueItemState.Failed,
                $"room {roomDirectory} settled indeterminate — resolve it with 'baton resolve' and redispatch if you want it redone");
        }

        if (sentinel.Error is { Length: > 0 } error)
        {
            return (QueueItemState.Failed, $"room {roomDirectory} settled with an error: {error}");
        }

        return (QueueItemState.Done, null);
    }

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
