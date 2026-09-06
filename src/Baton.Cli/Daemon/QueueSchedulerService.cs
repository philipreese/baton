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

        var outcome = await _launch(new QueueLaunchRequest(item, tier), cancellationToken).ConfigureAwait(false);

        if (outcome.RunwayHeld)
        {
            // No state change: the item is untouched and will be the candidate again next tick. But
            // _lastLaunchAt IS advanced, so the gap paces the retry -- a held vendor must not be
            // re-asked every TickSeconds.
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
    /// Which state a settled room puts its item in — split out from the I/O above because this is the
    /// part worth a test.
    /// </summary>
    /// <remarks>
    /// The indeterminate reading comes from the sentinel's own step states, not a second taxonomy:
    /// <c>StepStatus.IndeterminateAwaitingResolution</c> is what the engine records for a worker that
    /// neither succeeded nor failed usefully, and a timeout settles there too. Matched as a substring
    /// of the token rather than against the enum, because this assembly reads the projected view's
    /// string; if that token is ever renamed, this predicate silently stops matching.
    /// <para>
    /// The failure message names the room, because a marked item with nowhere to look is not
    /// investigable.
    /// </para>
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
