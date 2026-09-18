using System.Diagnostics;
using Baton.Domain;
using Baton.Core.Internal;
using Baton.Cli.Mcp;
using Baton.Projection;
using Baton.Queue;
using Baton.Status;
using Baton.Store;
using Baton.Templates;
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
/// <b>A dispatched lane outlives the tick that started it, and outlives this daemon too</b>
/// (spec/baton.md §13's launch/adopt contract, #2082). Here that means two things to hold on to:
/// <see cref="ResolveFinishedItemsAsync"/> is what closes an item out, it reads the room off disk,
/// and it therefore also closes out items some earlier daemon process started; and the first thing
/// <see cref="ExecuteAsync"/> does is hand every still-launched row to
/// <see cref="QueueLauncher.AdoptLaunchedLanesAsync"/>, so a lane the previous daemon left running
/// is supervised again rather than merely read.
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
    private readonly Func<CancellationToken, Task<IReadOnlyList<QueueLaneAdoption>>> _adopt;
    private readonly Func<CancellationToken, Task<double>> _liveWeight;
    private readonly Func<double?> _freeGb;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<string, CancellationToken, Task<string?>> _workspaceHead;
    private readonly Func<string, IReadOnlyList<string>> _workspaceLocks;
    private readonly WorkItemAdvancer _advancer;
    private readonly Func<CancellationToken, Task>? _beforeLaunchClaim;
    private readonly Func<CancellationToken, Task>? _afterFailureMutation;
    private readonly Func<FleetEventDraft, CancellationToken, Task<FleetEvent?>> _appendFleetEvent;
    private readonly QueueFleetEventOutbox _fleetOutbox;
    private readonly DaemonLoopDriver _loopDriver;
    private readonly DaemonRoomInventory _roomInventory;

    private DateTimeOffset? _lastLaunchAt;
    private string? _lastVerdictKey;

    public QueueSchedulerService()
        : this(null, null, null, null, appendFleetEvent: AppendOperationalFleetEventAsync)
    {
    }

    internal QueueSchedulerService(DaemonRoomInventory roomInventory)
        : this(
            null,
            null,
            null,
            null,
            appendFleetEvent: AppendOperationalFleetEventAsync,
            roomInventory: roomInventory)
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
        WorkItemAdvancer? advancer = null,
        Func<CancellationToken, Task<IReadOnlyList<QueueLaneAdoption>>>? adopt = null,
        Func<CancellationToken, Task>? beforeLaunchClaim = null,
        Func<CancellationToken, Task>? afterFailureMutation = null,
        Func<FleetEventDraft, CancellationToken, Task<FleetEvent?>>? appendFleetEvent = null,
        Func<string, CancellationToken, Task<string?>>? workspaceHead = null,
        Func<string, IReadOnlyList<string>>? workspaceLocks = null,
        DaemonLoopDriver? loopDriver = null,
        DaemonRoomInventory? roomInventory = null)
    {
        _roomInventory = roomInventory ?? new DaemonRoomInventory();
        _launch = launch ?? QueueLauncher.LaunchAsync;
        _adopt = adopt ?? QueueLauncher.AdoptLaunchedLanesAsync;
        _liveWeight = liveWeight ?? CountLiveWeightAsync;
        _freeGb = freeGb ?? FreePhysicalMemory.TryReadGiB;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _workspaceHead = workspaceHead ?? WorkspaceHead.TryCaptureAsync;
        _workspaceLocks = workspaceLocks ?? (workspace => GitWorkspaceLockProbe.FindExisting(workspace));
        _beforeLaunchClaim = beforeLaunchClaim;
        _afterFailureMutation = afterFailureMutation;
        _appendFleetEvent = appendFleetEvent ?? ((_, _) => Task.FromResult<FleetEvent?>(null));
        _fleetOutbox = new QueueFleetEventOutbox(_appendFleetEvent);
        _advancer = advancer ?? new WorkItemAdvancer(null, null, appendFleetEvent: _appendFleetEvent);
        _loopDriver = loopDriver ?? new DaemonLoopDriver();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // #2082: re-adopt before the first tick, so a lane the previous daemon left running has a
        // supervisor again before anything else reads the queue. A failure here is logged and the
        // loop still starts: the rows stay `launched`, done detection still reads their rooms, and
        // refusing to schedule anything because one journal was unreadable would be the wrong trade.
        try
        {
            await _adopt(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"QueueSchedulerService: re-adopting launched lanes failed, so this daemon supervises none of them "
                + $"until they settle on their own: {ex.Message}");
        }

        await _loopDriver.RunAsync(
            nameof(QueueSchedulerService),
            TickOnceAsync,
            () => TimeSpan.FromSeconds(QueueSettings.DefaultTickSeconds),
            _ => TimeSpan.FromSeconds(QueueSettings.DefaultTickSeconds),
            ex => Console.Error.WriteLine($"QueueSchedulerService: iteration failed: {ex.Message}"),
            stoppingToken).ConfigureAwait(false);
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

        // Queue state and fleet history are deliberately separate files. Drain the queue-owned
        // outbox before resolving, advancing, or selecting anything so a committed attempt fact is
        // durable before a later action can replace its envelope.
        using (DaemonLoopDriver.EnterPhase("fleet-event-outbox"))
        {
            await _fleetOutbox.PumpAsync(BatonPaths.QueueFile, cancellationToken).ConfigureAwait(false);
        }

        // A claim can be lost to an operator cancellation after a candidate is chosen. One fresh pass
        // keeps the next static candidate launchable in this tick; further churn returns to the daemon
        // loop so its heartbeat and configured delay cannot be starved by an endless race.
        for (var schedulingPass = 0; schedulingPass < 2; schedulingPass++)
        {
            await ReconcileCancelledItemsAsync(cancellationToken).ConfigureAwait(false);
            await ResolveFinishedItemsAsync(cancellationToken).ConfigureAwait(false);

            // The lifecycle advance runs BETWEEN done detection and the launch decision, and both
            // orderings matter (#1934 slice 2). After resolve, because it acts on items resolve has just
            // moved out of `launched`; before Decide, because an item it queues for its next round is a
            // candidate this same tick rather than one tick later.
            await AdvanceWorkItemsAsync(cancellationToken).ConfigureAwait(false);

            QueueSnapshot snapshot;
            using (DaemonLoopDriver.EnterPhase("queue-store"))
            {
                snapshot = await QueueStore.LoadAsync(BatonPaths.QueueFile, cancellationToken).ConfigureAwait(false);
            }
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
                        decision.LiveWeight, decision.FreeGb, decision.FloorGb,
                        ActiveLifecycles: decision.Context?.Portfolio.ActiveLifecycles,
                        PrePullRequestLifecycles: decision.Context?.Portfolio.PrePullRequestLifecycles,
                        LiveReviews: decision.Context?.Portfolio.LiveReviews,
                        PriorityBand: decision.Context?.SelectedBand?.ToString().ToLowerInvariant(),
                        PassedNewWorkHead: decision.Context?.PassedNewWorkHead,
                        OldestOccupyingLifecycle: decision.Context?.OldestOccupyingLifecycleTag,
                        ConsumingLifecycles: decision.Context?.ConsumingLifecycleTags,
                        NewWorkHeadCap: decision.Context?.NewWorkHeadCapToken),
                    cancellationToken).ConfigureAwait(false);
                return interval;
            }

            var item = decision.Item!;
            var admittedDeclaration = item.DeclaredTaskSize;
            QueueTierResolution tier;
            TaskRequirementAdmission admission;
            FleetAttemptId attemptId;
            string? attemptBaseRevision;
            QueueAttemptEnvelope envelope;
            string roomDirectory;

            // A queue commit may have completed before the daemon stopped. Resume that exact
            // envelope after its admission draft is pumped; never resolve a newer tier or grant.
            if (item.AttemptEnvelope is { } retained)
            {
                if (item.State == QueueItemState.Queued
                    && item.AttemptRefusedFactDurable
                    && !QueueFleetEventOutbox.HasPendingFor(snapshot, retained.AttemptId))
                {
                    await ResetRetryableRefusedAttemptAsync(item.Tag, retained.AttemptId).ConfigureAwait(false);
                    continue;
                }

                if (item.LaunchMayHaveBegunAt is not null)
                {
                    if (Directory.Exists(retained.RoomDirectory))
                    {
                        await MarkStartedAsync(item.Tag, retained.AttemptId, retained.RoomDirectory!, _now()).ConfigureAwait(false);
                        await PumpFleetEventsAsync(cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await HaltAmbiguousLaunchAsync(item).ConfigureAwait(false);
                    }
                    continue;
                }

                if (item.State != QueueItemState.Queued
                    || item.Retirement is not null
                    || retained.AdmissionDecision != TaskRequirementAdmission.Admitted
                    || !item.AttemptAdmissionFactDurable
                    || retained.RoomDirectory is not { Length: > 0 })
                {
                    // Failed lifecycle rows are reconciled by WorkItemAdvancer. A malformed current
                    // attempt is not launch authority and therefore cannot fall through to defaults.
                    continue;
                }

                envelope = retained;
                tier = TierFromEnvelope(envelope);
                admission = AdmissionFromEnvelope(envelope, item.LastAdmission);
                attemptId = envelope.AttemptId;
                attemptBaseRevision = envelope.AttemptBaseRevision;
                roomDirectory = envelope.RoomDirectory;
                item = item with
                {
                    LastAdmission = admission,
                    AttemptId = attemptId,
                    AttemptBaseRevision = attemptBaseRevision,
                    RoomDirectory = roomDirectory,
                };
                try
                {
                    item = item with { Skills = QueueLauncher.NormalizeSkillsForLaunch(item) };
                }
                catch (CliArgumentException ex)
                {
                    var remedy = ex.TryInvocation is { Length: > 0 } ? $" Try: {ex.TryInvocation}" : string.Empty;
                    await FailAsync(
                        item, ex.Message + remedy, room: null, now, decision, tier, cancellationToken)
                        .ConfigureAwait(false);
                    return interval;
                }
            }
            else
            {
                WorkerRole role;
                try
                {
                    role = WorkerRoleCatalog.For(item.Role);
                    tier = item.Stage is { } stage
                        ? QueueTierTable.ResolveForStage(
                            item, stage, settings, WorkerRoleCatalog.QueueTierFor, WorkerRoleCatalog.QueueTierForRole)
                        : QueueTierTable.Resolve(
                            item, settings, WorkerRoleCatalog.QueueTierFor, WorkerRoleCatalog.QueueTierForRole);
                    // A persisted decision is the launch authority. Refreshing configuration here may
                    // still validate the row and the runway gate may still hold it, but no scheduler tick
                    // may silently re-rank the worker selected at queue-add time.
                    tier = QueueLauncher.ApplyFrozenAssignment(item, tier);
                }
                catch (KeyNotFoundException ex)
                {
                    await FailAsync(
                        item, ex.Message, room: null, now, decision,
                        new QueueTierResolution(null, item.Adapter, item.Model, item.Effort, false, null),
                        cancellationToken).ConfigureAwait(false);
                    return interval;
                }

                // Fail closed, per spec/baton.md §13's tier-resolution ruling. Reachable only through a
                // hand-edited queue file, since QueueOptionsParser already refuses the scope class -- which is
                // why the daemon checks anyway rather than trusting the verb that wrote the item.
                if (item.ScopeClass is { Length: > 0 } scopeClass && tier.TierKey is not null
                    && QueueTierTable.LookupTier(tier.TierKey, settings, WorkerRoleCatalog.QueueTierFor) is null)
                {
                    await FailAsync(
                        item, $"no tier is configured for '{tier.TierKey}' (scope class '{scopeClass}', role '{item.Role}')",
                        room: null, now, decision, tier, cancellationToken).ConfigureAwait(false);
                    return interval;
                }

                // Queue add and import validate skills, but saved rows can predate that validation or be
                // hand-edited. Re-apply the shared launch policy before claiming a room.
                try
                {
                    item = item with { Skills = QueueLauncher.NormalizeSkillsForLaunch(item) };
                }
                catch (CliArgumentException ex)
                {
                    var remedy = ex.TryInvocation is { Length: > 0 } ? $" Try: {ex.TryInvocation}" : string.Empty;
                    await FailAsync(
                        item, ex.Message + remedy, room: null, now, decision, tier, cancellationToken)
                        .ConfigureAwait(false);
                    return interval;
                }

                // #2142: imported and hand-edited legacy rows bypass queue add, so apply the same
                // final-tuple policy before claiming a room or spawning a lane.
                if (WorkerInvocationModelPolicy.RefusalMessage(tier.Adapter, tier.Model) is { } refusal)
                {
                    var remedy = WorkerInvocationModelPolicy.TryInvocation(tier.Adapter) is { } suggestion
                        ? $" Re-add the item with {suggestion}"
                        : string.Empty;
                    await FailAsync(
                        item, refusal + remedy,
                        room: null, now, decision, tier, cancellationToken).ConfigureAwait(false);
                    return interval;
                }

                // The role catalog is read on every admission, rather than trusting the grant that was
                // current when queue add ran. This check remains before a room claim or vendor spawn.
                var hasMemoryAdd = item.Requirements?.Contains(TaskRequirements.MemoryAdd, StringComparer.Ordinal) == true;
                if (hasMemoryAdd
                    && (item.MemoryAddGrant is not { IsWellFormed: true } memoryGrant
                        || !string.Equals(memoryGrant.Repository, item.Repository, StringComparison.Ordinal)
                        || !WorkerAdapterRegistry.ProvidesHostMediatedExecution(tier.Adapter)))
                {
                    var reason = $"task requirement '{TaskRequirements.MemoryAdd}' requires an exact durable grant and a host-mediated adapter; no lane was started.";
                    var refusedAdmission = new TaskRequirementAdmission(
                        item.Requirements, [], TaskRequirementAdmission.Refused, [TaskRequirements.MemoryAdd], VendorUsage: 0);
                    envelope = CreateEnvelope(item, tier, FleetAttemptId.New(), refusedAdmission, now, room: null, baseRevision: null);
                    await CommitAdmissionAsync(
                        item, envelope, AdmissionEvent(envelope), refusedAdmission, QueueItemState.Failed, reason,
                        AttemptFact(FleetEventKind.AttemptRefused, envelope, now, reason, room: null, usage: null, artifacts: null))
                        .ConfigureAwait(false);
                    return interval;
                }
                attemptId = FleetAttemptId.New();
                var preflightItem = hasMemoryAdd
                    ? item with
                    {
                        Requirements = item.Requirements!.Where(requirement =>
                            !string.Equals(requirement, TaskRequirements.MemoryAdd, StringComparison.Ordinal)).ToArray(),
                    }
                    : item;
                var projectPreflight = RecordedProjectCeilingAdmission.Evaluate(
                    preflightItem, role, settings.RequireDeclaredRequirements);
                admission = projectPreflight.Admission;
                if (admission.Result == TaskRequirementAdmission.Refused)
                {
                    var missing = admission.Missing is { Count: > 0 }
                        ? string.Join(", ", admission.Missing)
                        : "an invalid requirement declaration";
                    var admissionRefusal = projectPreflight.CeilingFound
                        ? projectPreflight.RefusalMessage(item.Workspace, item.Role)
                            + " Choose a role that fits the recorded ceiling or explicitly correct trust for that exact workspace; requirements never grant authority."
                        : $"task requirements are incompatible with role '{item.Role}'s effective grant: missing {missing}. "
                            + "Choose a role whose grant supplies the requirement, or amend the task declaration; requirements never grant authority.";
                    envelope = CreateEnvelope(item, tier, attemptId, admission, now, room: null, baseRevision: null);
                    var refused = await CommitAdmissionAsync(
                        item, envelope, AdmissionEvent(envelope), admission, QueueItemState.Failed, admissionRefusal,
                        AttemptFact(
                            FleetEventKind.AttemptRefused,
                            envelope,
                            now,
                            admissionRefusal,
                            room: null,
                            usage: null,
                            artifacts: null))
                        .ConfigureAwait(false);
                    if (refused is not null)
                    {
                        await PumpFleetEventsAsync(cancellationToken).ConfigureAwait(false);
                        if (_afterFailureMutation is not null)
                        {
                            await _afterFailureMutation(cancellationToken).ConfigureAwait(false);
                        }

                        await RecordIfNotRetiredAsync(item.Tag,
                            new QueueDecisionEntry(
                                now, item.Tag, QueueDecisionEntry.Failed, admissionRefusal,
                                decision.LiveWeight, decision.FreeGb, decision.FloorGb,
                                tier.TierKey, tier.Adapter, tier.Model, tier.Effort, tier.IsOverride,
                                tier.OverrideReason, Room: null, tier.SelectionSource, admission),
                            cancellationToken).ConfigureAwait(false);
                    }

                    return interval;
                }

                // Only a stage allowed to author code receives a pre-attempt revision baseline.
                attemptBaseRevision = item.Stage is WorkStage.Implement or WorkStage.Fix or WorkStage.Continue
                    ? await _workspaceHead(item.Workspace, cancellationToken).ConfigureAwait(false)
                    : null;
                roomDirectory = QueueLauncher.RoomDirectoryFor(item);
                envelope = CreateEnvelope(item, tier, attemptId, admission, now, roomDirectory, attemptBaseRevision);
                var admitted = await CommitAdmissionAsync(
                    item, envelope, AdmissionEvent(envelope), admission, QueueItemState.Queued, error: null)
                    .ConfigureAwait(false);
                if (admitted is null)
                {
                    continue;
                }

                // Keep the pre-launch queue row retireable, while giving the launcher the exact
                // retained binding it must observe once this tick wins the launch claim.
                item = admitted with
                {
                    LastAdmission = admission,
                    AttemptId = attemptId,
                    AttemptBaseRevision = attemptBaseRevision,
                    RoomDirectory = roomDirectory,
                };
                await PumpFleetEventsAsync(cancellationToken).ConfigureAwait(false);
            }
            // #2115's final pre-launch Git-lock refusal; spec/baton.md §13 owns the safety boundary.
            IReadOnlyList<string> workspaceLocks;
            try
            {
                workspaceLocks = _workspaceLocks(item.Workspace);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                or ArgumentException or NotSupportedException)
            {
                var failed = await TryFailPreLaunchAsync(
                    item,
                    $"Baton could not inspect Git lock state for workspace '{item.Workspace}': {ex.Message} "
                    + "No lane was started. Repair the workspace metadata, then re-add the queue item.",
                    now, decision, tier, cancellationToken, admission, attemptId).ConfigureAwait(false);
                if (failed)
                {
                    return interval;
                }
                continue;
            }

            if (workspaceLocks.Count > 0)
            {
                var listedLocks = string.Join(", ", workspaceLocks.Select(path => $"'{path}'"));
                var failed = await TryFailPreLaunchAsync(
                    item,
                    $"Baton refused to reuse workspace '{item.Workspace}' because Git lock file(s) exist: "
                    + $"{listedLocks}. Baton did not remove them because a Git lock carries no ownership evidence. "
                    + "First stop or finish every Git writer using this workspace; remove a lock only after independently "
                    + "establishing that no writer owns it, then re-add the queue item.",
                    now, decision, tier, cancellationToken, admission, attemptId).ConfigureAwait(false);
                if (failed)
                {
                    return interval;
                }
                continue;
            }

            if (_beforeLaunchClaim is not null)
            {
                await _beforeLaunchClaim(cancellationToken).ConfigureAwait(false);
            }

            // Re-check Queued under QueueStore's mutation lock, rather than trusting the snapshot this
            // tick read above: `baton queue cancel` owns the same seam. A cancellation that gets there
            // first wins and this scheduler never starts a lane from its stale candidate.
            var launchClaimed = false;
            IReadOnlyList<QueueItem>? claimedItems = null;
            using (DaemonLoopDriver.EnterPhase("queue-store"))
            {
                await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot =>
                {
                    var current = snapshot.Items.FirstOrDefault(i => string.Equals(i.Tag, item.Tag, StringComparison.Ordinal));
                    if (current?.State != QueueItemState.Queued
                        || current.Retirement is not null)
                    {
                        return snapshot;
                    }

                    if (!HasSameAdmissionDeclaration(current, item, admittedDeclaration))
                    {
                        // Admission belongs to the exact declaration it inspected. If that row is
                        // replaced before the launch claim, retire only this unlaunched envelope and
                        // let the next scheduling pass admit the replacement from scratch. The
                        // admission fact has already been pumped, so clearing the queue binding does
                        // not erase history or risk replaying a worker that may have started.
                        if (current.AttemptEnvelope?.AttemptId != attemptId
                            || current.LaunchMayHaveBegunAt is not null)
                        {
                            return snapshot;
                        }

                        claimedItems = Replace(snapshot.Items, item.Tag, existing => existing with
                        {
                            ParentAttemptId = existing.AttemptId ?? existing.ParentAttemptId,
                            AttemptId = null,
                            AttemptBaseRevision = null,
                            AttemptEnvelope = null,
                            LaunchMayHaveBegunAt = null,
                            AttemptAdmissionFactDurable = false,
                            AttemptStartedFactDurable = false,
                            AttemptRefusedFactDurable = false,
                            AttemptSettledFactDurable = false,
                            LaunchRecoveryKind = null,
                            OriginatingPullRequestRecoveryClaim = null,
                            LastAdmission = null,
                            RoomDirectory = null,
                        });
                        return snapshot with { Items = claimedItems };
                    }

                    launchClaimed = true;
                    claimedItems = Replace(snapshot.Items, item.Tag, existing => existing with
                    {
                        State = QueueItemState.Launched,
                        RoomDirectory = roomDirectory,
                        LaunchedAt = now,
                        LaunchMayHaveBegunAt = now,
                        Error = null,
                        LastAdmission = admission,
                        AttemptId = attemptId,
                        AttemptBaseRevision = attemptBaseRevision,
                    });
                    return snapshot with
                    {
                        Items = claimedItems,
                    };
                }, CancellationToken.None).ConfigureAwait(false);
            }

            if (!launchClaimed)
            {
                // The snapshot chose a candidate that cancellation has now removed. Re-evaluate instead
                // of recording no-items from that stale snapshot: another queued item may be launchable.
                // This reuses the whole current-state decision path, including its honest wait reason,
                // once. Continuing cancellation churn is reconsidered by the next daemon tick.
                continue;
            }

            // The original decision context is the pre-claim admission snapshot. A launched fact
            // is also the board's durable WIP reading, so record the exact post-claim queue image
            // instead of saying "zero active" while this first lifecycle is already running.
            var recordedDecision = decision with
            {
                Context = QueueScheduler.ContextAfterLaunchClaim(decision.Context!, claimedItems!),
            };

            _lastLaunchAt = now;

            QueueLaunchOutcome outcome;
            try
            {
                using (DaemonLoopDriver.EnterPhase("worker-launch"))
                {
                    outcome = await _launch(new QueueLaunchRequest(item, tier, roomDirectory), CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Nothing in the launch takes a cancellable token any more, so this is the belt to that
                // braces. `failed`, not left launched: the queue has lost track of whether a lane started,
                // and the room id on the item is how an operator finds out.
                var shutdownFailure = $"the daemon shut down while launching into room '{roomDirectory}'; check that room before "
                    + "re-adding this item, because the lane may have started";
                await HaltAmbiguousLaunchAsync(item, shutdownFailure).ConfigureAwait(false);
                await RecordIfNotRetiredAsync(item.Tag,
                    new QueueDecisionEntry(
                        now, item.Tag, QueueDecisionEntry.Failed, shutdownFailure,
                        recordedDecision.LiveWeight, recordedDecision.FreeGb, recordedDecision.FloorGb,
                        tier.TierKey, tier.Adapter, tier.Model, tier.Effort, tier.IsOverride, tier.OverrideReason,
                        roomDirectory, tier.SelectionSource, admission),
                    CancellationToken.None).ConfigureAwait(false);
                return interval;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                // A throw the launcher does not model as a QueueLaunchOutcome -- an IO failure inside
                // TerminalSettleRecorder, say. Mapped to the item's own Failed state and one recorded fact
                // rather than unwound into the loop's catch-all, which would leave the item launched with
                // nothing said about why (#1939 review).
                var launchFailure = $"the launch into room '{roomDirectory}' threw {ex.GetType().Name}: {ex.Message}";
                await HaltAmbiguousLaunchAsync(item, launchFailure).ConfigureAwait(false);
                await RecordIfNotRetiredAsync(item.Tag,
                    new QueueDecisionEntry(
                        now, item.Tag, QueueDecisionEntry.Failed, launchFailure,
                        recordedDecision.LiveWeight, recordedDecision.FreeGb, recordedDecision.FloorGb,
                        tier.TierKey, tier.Adapter, tier.Model, tier.Effort, tier.IsOverride, tier.OverrideReason,
                        roomDirectory, tier.SelectionSource, admission),
                    CancellationToken.None).ConfigureAwait(false);
                return interval;
            }

            if (outcome.RunwayHeld || outcome.Deferred)
            {
                // A runway hold or active cleanup claim is a modeled no-launch result for this admitted attempt.
                // Publish its refusal before clearing the envelope; the next retry receives a fresh attempt id.
                var waitReason = outcome.Deferred ? QueueWaitReason.CleanupClaim : QueueWaitReason.RunwayHeld;
                await MarkAttemptRefusedAsync(item.Tag, attemptId, waitReason, now).ConfigureAwait(false);
                await PumpFleetEventsAsync(cancellationToken).ConfigureAwait(false);
                await ResetRetryableRefusedAttemptAsync(item.Tag, attemptId).ConfigureAwait(false);
                await RecordAsync(
                    new QueueDecisionEntry(
                        now, item.Tag, QueueDecisionEntry.Waited,
                        QueueWaitReasons.Token(waitReason),
                        decision.LiveWeight, decision.FreeGb, decision.FloorGb,
                        tier.TierKey, tier.Adapter, tier.Model, tier.Effort, tier.IsOverride, tier.OverrideReason,
                        SelectionSource: tier.SelectionSource, Admission: admission,
                        ActiveLifecycles: decision.Context?.Portfolio.ActiveLifecycles,
                        PrePullRequestLifecycles: decision.Context?.Portfolio.PrePullRequestLifecycles,
                        LiveReviews: decision.Context?.Portfolio.LiveReviews,
                        PriorityBand: decision.Context?.SelectedBand?.ToString().ToLowerInvariant(),
                        PassedNewWorkHead: decision.Context?.PassedNewWorkHead,
                        OldestOccupyingLifecycle: decision.Context?.OldestOccupyingLifecycleTag,
                        ConsumingLifecycles: decision.Context?.ConsumingLifecycleTags,
                        NewWorkHeadCap: decision.Context?.NewWorkHeadCapToken),
                    cancellationToken).ConfigureAwait(false);
                return interval;
            }

            if (outcome.Error is { Length: > 0 } error)
            {
                // outcome.RoomDirectory, not the path above: the launcher reports it only when the dispatch
                // actually provisioned the room, and a refusal that never got that far must leave the item
                // pointing at nothing rather than at a directory that does not exist.
                await FailAsync(
                    item, error, outcome.RoomDirectory, now, recordedDecision, tier, CancellationToken.None,
                    attemptId: attemptId)
                    .ConfigureAwait(false);
                return interval;
            }

            await MarkStartedAsync(item.Tag, attemptId, outcome.RoomDirectory ?? roomDirectory, _now()).ConfigureAwait(false);
            await PumpFleetEventsAsync(cancellationToken).ConfigureAwait(false);

            // The item is already marked launched, above. All that is left is the fact.
            await RecordIfNotRetiredAsync(item.Tag,
                new QueueDecisionEntry(
                    now, item.Tag, QueueDecisionEntry.Launched, null,
                    recordedDecision.LiveWeight, recordedDecision.FreeGb, recordedDecision.FloorGb,
                    tier.TierKey, tier.Adapter, tier.Model, tier.Effort, tier.IsOverride, tier.OverrideReason,
                    outcome.RoomDirectory ?? roomDirectory, tier.SelectionSource, admission,
                    ActiveLifecycles: recordedDecision.Context?.Portfolio.ActiveLifecycles,
                    PrePullRequestLifecycles: recordedDecision.Context?.Portfolio.PrePullRequestLifecycles,
                    LiveReviews: recordedDecision.Context?.Portfolio.LiveReviews,
                    PriorityBand: recordedDecision.Context?.SelectedBand?.ToString().ToLowerInvariant(),
                    PassedNewWorkHead: recordedDecision.Context?.PassedNewWorkHead,
                    OldestOccupyingLifecycle: recordedDecision.Context?.OldestOccupyingLifecycleTag,
                    ConsumingLifecycles: recordedDecision.Context?.ConsumingLifecycleTags,
                    NewWorkHeadCap: recordedDecision.Context?.NewWorkHeadCapToken),
                CancellationToken.None).ConfigureAwait(false);

            return interval;
        }

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
            using (DaemonLoopDriver.EnterPhase("work-item-advance"))
            {
                facts = await _advancer.AdvanceAsync(_now(), cancellationToken).ConfigureAwait(false);
            }
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
    private static async Task MarkAsync(string tag, Func<QueueItem, QueueItem> update)
    {
        using (DaemonLoopDriver.EnterPhase("queue-store"))
        {
            await QueueStore.MutateAsync(
                BatonPaths.QueueFile,
                s => s with { Items = Replace(s.Items, tag, update) },
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static Task MarkStartedAsync(
        string tag, FleetAttemptId attemptId, string room, DateTimeOffset occurredAt) =>
        QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot =>
        {
            var current = snapshot.Items.FirstOrDefault(item => string.Equals(item.Tag, tag, StringComparison.Ordinal));
            if (current?.AttemptEnvelope is not { } envelope || envelope.AttemptId != attemptId)
            {
                return snapshot;
            }

            var updated = current with
            {
                State = QueueItemState.Launched,
                RoomDirectory = room,
                LaunchMayHaveBegunAt = null,
            };
            var next = snapshot with { Items = Replace(snapshot.Items, tag, _ => updated) };
            return QueueFleetEventOutbox.Enqueue(
                next,
                AttemptFact(
                    FleetEventKind.AttemptStarted,
                    envelope,
                    occurredAt,
                    outcome: null,
                    room,
                    usage: null,
                    artifacts: null));
        }, CancellationToken.None);

    private async Task FailAsync(
        QueueItem item,
        string error,
        string? room,
        DateTimeOffset now,
        QueueDecision decision,
        QueueTierResolution tier,
        CancellationToken cancellationToken,
        TaskRequirementAdmission? admission = null,
        FleetAttemptId? attemptId = null)
    {
        // RoomDirectory is assigned, never merged with what the item already carried: the pre-launch
        // mark writes the room the dispatch was GOING to use, and a refusal that never provisioned it
        // must not leave that path behind as if a room existed to go and read.
        var failed = false;
        using (DaemonLoopDriver.EnterPhase("queue-store"))
        {
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot =>
            {
                var current = snapshot.Items.FirstOrDefault(i => string.Equals(i.Tag, item.Tag, StringComparison.Ordinal));
                if (current is null || current.Retirement is not null
                    || attemptId is { } exactAttempt
                        && (current.AttemptId != exactAttempt
                            || current.AttemptEnvelope?.AttemptId != exactAttempt))
                {
                    return snapshot;
                }

                failed = true;
                var existing = current!;
                var updated = existing with
                {
                    State = QueueItemState.Failed,
                    Error = error,
                    RoomDirectory = room,
                    LastAdmission = admission ?? existing.LastAdmission,
                    AttemptId = attemptId ?? existing.AttemptId,
                    LaunchMayHaveBegunAt = null,
                    OriginatingPullRequestRecoveryClaim = attemptId is { } failedAttempt
                        && existing.OriginatingPullRequestRecoveryClaim == failedAttempt
                            ? null
                            : existing.OriginatingPullRequestRecoveryClaim,
                };
                var next = snapshot with { Items = Replace(snapshot.Items, item.Tag, _ => updated) };
                return updated.AttemptEnvelope is { } envelope
                    ? QueueFleetEventOutbox.Enqueue(
                        next,
                        AttemptFact(
                            FleetEventKind.AttemptRefused,
                            envelope,
                            now,
                            error,
                            room: null,
                            usage: null,
                            artifacts: null))
                    : next;
            }, CancellationToken.None).ConfigureAwait(false);
        }

        if (!failed)
        {
            return;
        }

        if (_afterFailureMutation is not null)
        {
            await _afterFailureMutation(cancellationToken).ConfigureAwait(false);
        }

        await RecordIfNotRetiredAsync(item.Tag,
            new QueueDecisionEntry(
                now, item.Tag, QueueDecisionEntry.Failed, error,
                decision.LiveWeight, decision.FreeGb, decision.FloorGb,
                tier.TierKey, tier.Adapter, tier.Model, tier.Effort, tier.IsOverride, tier.OverrideReason, room,
                tier.SelectionSource, admission),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Records a final pre-launch refusal only while the exact admitted row remains queued. The same
    /// conditional mutation as the launch claim lets an operator cancellation or row replacement win.
    /// </summary>
    private async Task<bool> TryFailPreLaunchAsync(
        QueueItem item,
        string error,
        DateTimeOffset now,
        QueueDecision decision,
        QueueTierResolution tier,
        CancellationToken cancellationToken,
        TaskRequirementAdmission admission,
        FleetAttemptId attemptId)
    {
        var failed = false;
        using (DaemonLoopDriver.EnterPhase("queue-store"))
        {
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot =>
            {
                var current = snapshot.Items.FirstOrDefault(i => string.Equals(i.Tag, item.Tag, StringComparison.Ordinal));
                if (current?.State != QueueItemState.Queued
                    || current.Retirement is not null
                    || !HasSameAdmissionDeclaration(current, item))
                {
                    return snapshot;
                }

                failed = true;
                var updated = current with
                {
                    State = QueueItemState.Failed,
                    Error = error,
                    RoomDirectory = null,
                    LastAdmission = admission,
                    AttemptId = attemptId,
                };
                var next = snapshot with { Items = Replace(snapshot.Items, item.Tag, _ => updated) };
                return updated.AttemptEnvelope is { } envelope
                    ? QueueFleetEventOutbox.Enqueue(
                        next,
                        AttemptFact(
                            FleetEventKind.AttemptRefused,
                            envelope,
                            now,
                            error,
                            room: null,
                            usage: null,
                            artifacts: null))
                    : next;
            }, CancellationToken.None).ConfigureAwait(false);
        }

        if (!failed)
        {
            return false;
        }

        if (_afterFailureMutation is not null)
        {
            await _afterFailureMutation(cancellationToken).ConfigureAwait(false);
        }

        await RecordIfNotRetiredAsync(item.Tag,
            new QueueDecisionEntry(
                now, item.Tag, QueueDecisionEntry.Failed, error,
                decision.LiveWeight, decision.FreeGb, decision.FloorGb,
                tier.TierKey, tier.Adapter, tier.Model, tier.Effort, tier.IsOverride, tier.OverrideReason,
                Room: null, tier.SelectionSource, admission),
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Done detection (spec/baton.md §13): every launched item whose room now projects terminal is
    /// moved out of <see cref="QueueItemState.Launched"/> by
    /// <see cref="ClassifyTerminal"/>. One queue read, one write for the whole batch — a per-item
    /// mutation would take the file lock once per launched item every tick.
    /// </summary>
    /// <remarks>
    /// Nothing here retries, resolves, or composes a continuation; the item is marked and left alone.
    /// The read precedence and the diagnostic gate that keeps this from racing ordinary terminal
    /// finalization are stated once in spec/baton.md §13 (#2248); <see cref="TryProjectTerminalAsync"/>
    /// is that fallback's implementation.
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
        using (DaemonLoopDriver.EnterPhase("fleet-event-outbox"))
        {
            await _fleetOutbox.PumpAsync(BatonPaths.QueueFile, cancellationToken).ConfigureAwait(false);
        }

        QueueSnapshot snapshot;
        using (DaemonLoopDriver.EnterPhase("queue-store"))
        {
            snapshot = await QueueStore.LoadAsync(BatonPaths.QueueFile, cancellationToken).ConfigureAwait(false);
        }
        var launched = snapshot.Items
            .Where(i => i.State == QueueItemState.Launched && i.RoomDirectory is { Length: > 0 })
            .ToList();
        if (launched.Count == 0)
        {
            return;
        }

        var resolved = new Dictionary<string, (
            QueueItemState State,
            string? Error,
            WorkflowStatusView? Terminal,
            FleetAttemptId? AttemptId,
            string Room)>(StringComparer.Ordinal);
        foreach (var item in launched)
        {
            if (item.AttemptEnvelope is { } recoveringEnvelope
                && item.LaunchMayHaveBegunAt is not null)
            {
                if (Directory.Exists(item.RoomDirectory!))
                {
                    await MarkStartedAsync(item.Tag, recoveringEnvelope.AttemptId, item.RoomDirectory!, _now())
                        .ConfigureAwait(false);
                    await PumpFleetEventsAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await HaltAmbiguousLaunchAsync(item).ConfigureAwait(false);
                }
                continue;
            }

            if (item.AttemptEnvelope is { } envelope && !item.AttemptStartedFactDurable && Directory.Exists(item.RoomDirectory!))
            {
                await MarkStartedAsync(item.Tag, envelope.AttemptId, item.RoomDirectory!, _now()).ConfigureAwait(false);
                await PumpFleetEventsAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (item.AttemptEnvelope is null && item.AttemptId is { } legacyAttemptId && Directory.Exists(item.RoomDirectory!))
            {
                await _appendFleetEvent(
                    AttemptStartedEvent(item, tier: null, legacyAttemptId, item.RoomDirectory!, item.LaunchedAt ?? _now()),
                    cancellationToken).ConfigureAwait(false);
            }

            var terminal = await TerminalSentinelWriter.TryReadAsync(item.RoomDirectory!, cancellationToken).ConfigureAwait(false)
                ?? await TryProjectTerminalAsync(item.RoomDirectory!, cancellationToken).ConfigureAwait(false);
            if (terminal is not null)
            {
                var outcome = ClassifyTerminal(terminal, item.RoomDirectory!);
                if (item.AttemptEnvelope is null && item.AttemptId is { } legacySettlementAttemptId)
                {
                    await _appendFleetEvent(
                        AttemptSettledEvent(item, legacySettlementAttemptId, terminal, _now()), cancellationToken)
                        .ConfigureAwait(false);
                }
                resolved[item.Tag] = (outcome.State, outcome.Error, terminal, item.AttemptId, item.RoomDirectory!);
                continue;
            }

            if (IsRoomlessPastGrace(item))
            {
                resolved[item.Tag] = (QueueItemState.Failed,
                    $"room {item.RoomDirectory} was never created — the dispatch refused or faulted before it "
                    + "provisioned the room, so nothing ran; re-add the item once you know why",
                    null, item.AttemptId, item.RoomDirectory!);
            }
        }

        if (resolved.Count == 0)
        {
            return;
        }

        using (DaemonLoopDriver.EnterPhase("queue-store"))
        {
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s =>
            {
                var next = s with
                {
                    Items = s.Items
                        .Select(i => resolved.TryGetValue(i.Tag, out var outcome)
                            && i.State == QueueItemState.Launched
                            && i.AttemptId == outcome.AttemptId
                            && string.Equals(i.RoomDirectory, outcome.Room, StringComparison.Ordinal)
                            ? i with
                            {
                                State = outcome.State,
                                Error = outcome.Error,
                                OriginatingPullRequestRecoveryClaim =
                                    i.OriginatingPullRequestRecoveryClaim == outcome.AttemptId
                                        ? null
                                        : i.OriginatingPullRequestRecoveryClaim,
                            }
                            : i)
                        .ToList(),
                };
                foreach (var (tag, outcome) in resolved)
                {
                    var updated = next.Items.FirstOrDefault(item => string.Equals(item.Tag, tag, StringComparison.Ordinal));
                    if (updated?.AttemptEnvelope is { } currentEnvelope
                        && updated.AttemptId == outcome.AttemptId
                        && string.Equals(updated.RoomDirectory, outcome.Room, StringComparison.Ordinal)
                        && outcome.Terminal is not null)
                    {
                        next = QueueFleetEventOutbox.Enqueue(
                            next,
                            AttemptSettledFact(
                                currentEnvelope,
                                updated.RoomDirectory!,
                                outcome.Terminal,
                                _now()));
                    }
                }

                return next;
            }, cancellationToken).ConfigureAwait(false);
        }

        await PumpFleetEventsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Projects the room's authoritative snapshot and journal into the same terminal view a sentinel
    /// carries, without writing the room. This is the no-sentinel half of #2248; it exists because
    /// <see cref="DeadPumpProbe"/> is deliberately bounded to one journal fact.
    /// </summary>
    private static async Task<WorkflowStatusView?> TryProjectTerminalAsync(
        string roomDirectoryPath,
        CancellationToken cancellationToken)
    {
        var snapshotPath = Path.Combine(roomDirectoryPath, BatonPaths.SnapshotFileName);
        var logPath = Path.Combine(roomDirectoryPath, BatonPaths.FlowLogFileName);
        if (!File.Exists(snapshotPath) || !File.Exists(logPath))
        {
            return null;
        }

        try
        {
            var snapshot = await SnapshotBinder.LoadFromFileAsync(snapshotPath, cancellationToken).ConfigureAwait(false);
            var entries = await new FlowEventLogReader(logPath)
                .ReadAllEntriesWithTimestampsAsync(cancellationToken).ConfigureAwait(false);
            if (!entries.OfType<LogEntry.FlowLogEntry>()
                .Any(entry => DeadPumpProbe.IsTerminalDiagnostic(entry.Event)))
            {
                return null;
            }

            var events = entries
                .OfType<LogEntry.FlowLogEntry>()
                .Select(entry => entry.Event)
                .ToList();
            var state = StateProjector.Project(events, snapshot, ProjectionCheckpointStore.Load(roomDirectoryPath));
            return state.Status == WorkflowStatus.Terminal
                ? WorkflowStatusProjector.Project(state, snapshot, roomDirectoryPath, entries)
                : null;
        }
        catch (Exception ex) when (ex is SnapshotLoadException or FlowEventLogReadException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
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

    /// <summary>
    /// Failure and final-launch records share the queue ordering seam with merged retirement. A
    /// retirement that commits after the scheduler's CAS therefore either follows this append, or
    /// suppresses the stale scheduler record; ledger availability still cannot undo the CAS.
    /// </summary>
    private async Task RecordIfNotRetiredAsync(string tag, QueueDecisionEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            using (DaemonLoopDriver.EnterPhase("queue-store"))
            {
                await QueueStore.RecordIfCurrentAsync(
                    BatonPaths.QueueFile,
                    snapshot => snapshot.Items.Any(item => string.Equals(item.Tag, tag, StringComparison.Ordinal)
                        && item.Retirement is null),
                    () => _lastVerdictKey = QueueDecisionLedgerStore.AppendUnderQueueLock(
                        entry, _lastVerdictKey, BatonPaths.QueueDecisionLedgerFile, cancellationToken),
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            Console.Error.WriteLine(
                $"Could not append to the queue decision ledger at '{BatonPaths.QueueDecisionLedgerFile}': {ex.Message}.");
        }
    }

    /// <summary>
    /// Repairs the one ledger fact promised by persisted cancellation state. The CLI attempts the
    /// append immediately and reports a failure; the daemon is the automatic recovery path, so an
    /// unavailable ledger is logged but cannot stop later queue work from being considered.
    /// </summary>
    private static async Task ReconcileCancelledItemsAsync(CancellationToken cancellationToken)
    {
        QueueSnapshot snapshot;
        using (DaemonLoopDriver.EnterPhase("queue-store"))
        {
            snapshot = await QueueStore.LoadAsync(BatonPaths.QueueFile, cancellationToken).ConfigureAwait(false);
        }
        try
        {
            await QueueDecisionLedgerStore.ReconcileCancellationsAsync(
                snapshot.Items, BatonPaths.QueueDecisionLedgerFile, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            Console.Error.WriteLine(
                $"Could not reconcile cancelled queue items into the decision ledger at "
                + $"'{BatonPaths.QueueDecisionLedgerFile}': {ex.Message}.");
        }
    }

    private static IReadOnlyList<QueueItem> Replace(
        IReadOnlyList<QueueItem> items, string tag, Func<QueueItem, QueueItem> update) =>
        items.Select(i => string.Equals(i.Tag, tag, StringComparison.Ordinal) ? update(i) : i).ToList();

    /// <summary>
    /// The preflight verdict is meaningful only for the exact role and requirement declaration it
    /// inspected. The pre-attempt revision is likewise evidence about one exact workspace and stage.
    /// Queue replacement is allowed while an item is queued, so claiming by tag/state alone could
    /// otherwise launch a just-replaced imported task under an old verdict or revision baseline.
    /// </summary>
    private static bool HasSameAdmissionDeclaration(
        QueueItem current,
        QueueItem admitted,
        TaskSizeDeclaration? declaredTaskSize = null) =>
        string.Equals(current.Role, admitted.Role, StringComparison.Ordinal)
        && current.Stage == admitted.Stage
        && string.Equals(current.Workspace, admitted.Workspace, StringComparison.Ordinal)
        && current.DeclaredTaskSize.Size == (declaredTaskSize ?? admitted.DeclaredTaskSize).Size
        && string.Equals(
            current.DeclaredTaskSize.Rationale,
            (declaredTaskSize ?? admitted.DeclaredTaskSize).Rationale,
            StringComparison.Ordinal)
        && SameRequirements(current.Requirements, admitted.Requirements);

    private static bool SameRequirements(IReadOnlyList<string>? left, IReadOnlyList<string>? right) =>
        left is null || right is null
            ? left is null && right is null
            : left.SequenceEqual(right, StringComparer.Ordinal);

    private static Task<FleetEvent?> AppendOperationalFleetEventAsync(
        FleetEventDraft draft, CancellationToken cancellationToken) =>
        FleetEventLog.OpenOperational().Append(draft, cancellationToken);

    private static QueueTierResolution TierFromEnvelope(QueueAttemptEnvelope envelope) =>
        new(null, envelope.Adapter, envelope.Model, envelope.Effort, false, null);

    private static TaskRequirementAdmission AdmissionFromEnvelope(
        QueueAttemptEnvelope envelope,
        TaskRequirementAdmission? current) =>
        new(
            envelope.RequestedRequirements,
            envelope.EffectiveGrant,
            envelope.AdmissionDecision,
            envelope.MissingCapabilities,
            current?.VendorUsage);

    private static QueueAttemptEnvelope CreateEnvelope(
        QueueItem item,
        QueueTierResolution tier,
        FleetAttemptId attemptId,
        TaskRequirementAdmission admission,
        DateTimeOffset at,
        string? room,
        string? baseRevision) =>
        new(
            attemptId,
            item.ParentAttemptId,
            item.Tag,
            item.Issue,
            item.PullRequest,
            item.Stage,
            item.Role,
            tier.Adapter,
            tier.Model,
            tier.Effort,
            admission.EffectiveGrant,
            admission.Requested,
            admission.Missing,
            admission.Result,
            room,
            room is null ? null : BatonPaths.RecordKey(room),
            baseRevision,
            at);

    private async Task<QueueItem?> CommitAdmissionAsync(
        QueueItem item,
        QueueAttemptEnvelope envelope,
        FleetEventDraft admissionEvent,
        TaskRequirementAdmission admission,
        QueueItemState state,
        string? error,
        FleetEventDraft? transitionEvent = null)
    {
        QueueItem? committed = null;
        await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot =>
        {
            var current = snapshot.Items.FirstOrDefault(candidate =>
                string.Equals(candidate.Tag, item.Tag, StringComparison.Ordinal));
            if (current?.State != QueueItemState.Queued
                || current.AttemptEnvelope is not null
                || current.Retirement is not null
                || !HasSameAdmissionDeclaration(current, item))
            {
                return snapshot;
            }

            committed = current with
            {
                State = state,
                Error = error,
                Skills = item.Skills,
                AttemptId = state == QueueItemState.Failed ? envelope.AttemptId : current.AttemptId,
                AttemptBaseRevision = state == QueueItemState.Failed
                    ? envelope.AttemptBaseRevision
                    : current.AttemptBaseRevision,
                // The envelope reserves the intended room binding. The queue row gains a room only
                // at the launch claim, so cancellation and merged retirement can still win before
                // any worker may have begun.
                RoomDirectory = null,
                LastAdmission = state == QueueItemState.Failed ? admission : current.LastAdmission,
                AttemptEnvelope = envelope,
                LaunchMayHaveBegunAt = null,
                AttemptAdmissionFactDurable = false,
                AttemptStartedFactDurable = false,
                AttemptRefusedFactDurable = false,
                AttemptSettledFactDurable = false,
                LaunchRecoveryKind = null,
            };
            var queued = snapshot with { Items = Replace(snapshot.Items, item.Tag, _ => committed) };
            queued = QueueFleetEventOutbox.Enqueue(queued, admissionEvent);
            return transitionEvent is null
                ? queued
                : QueueFleetEventOutbox.Enqueue(queued, transitionEvent);
        }, CancellationToken.None).ConfigureAwait(false);
        return committed;
    }

    private Task PumpFleetEventsAsync(CancellationToken cancellationToken) =>
        _fleetOutbox.PumpAsync(BatonPaths.QueueFile, cancellationToken);

    private static Task HaltAmbiguousLaunchAsync(QueueItem item, string? detail = null) =>
        QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
        {
            Items = Replace(snapshot.Items, item.Tag, current => current.AttemptEnvelope?.AttemptId == item.AttemptEnvelope?.AttemptId
                ? current with
                {
                    State = QueueItemState.Failed,
                    Halted = true,
                    LaunchRecoveryKind = QueueLaunchRecoveryKind.AmbiguousEvidence,
                    OriginatingPullRequestRecoveryClaim =
                        current.OriginatingPullRequestRecoveryClaim == item.AttemptEnvelope!.AttemptId
                            ? null
                            : current.OriginatingPullRequestRecoveryClaim,
                    Error = detail
                        ?? $"attempt '{item.AttemptEnvelope!.AttemptId.Value}' may have begun but has no authoritative room evidence; recovery halted",
                }
                : current),
        }, CancellationToken.None);

    private static Task MarkAttemptRefusedAsync(string tag, FleetAttemptId attemptId, QueueWaitReason waitReason, DateTimeOffset occurredAt) =>
        QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot =>
        {
            var current = snapshot.Items.FirstOrDefault(item => string.Equals(item.Tag, tag, StringComparison.Ordinal));
            if (current?.AttemptEnvelope is not { } envelope
                || envelope.AttemptId != attemptId
                || current.State != QueueItemState.Launched)
            {
                return snapshot;
            }

            var updated = current with
            {
                State = QueueItemState.Queued,
                RoomDirectory = null,
                LaunchedAt = null,
                LaunchMayHaveBegunAt = null,
                OriginatingPullRequestRecoveryClaim =
                    current.OriginatingPullRequestRecoveryClaim == attemptId
                        ? null
                        : current.OriginatingPullRequestRecoveryClaim,
            };
            var next = snapshot with { Items = Replace(snapshot.Items, tag, _ => updated) };
            return QueueFleetEventOutbox.Enqueue(
                next,
                AttemptFact(
                    FleetEventKind.AttemptRefused,
                    envelope,
                    occurredAt,
                    QueueWaitReasons.Token(waitReason),
                    room: null,
                    usage: null,
                    artifacts: null));
        }, CancellationToken.None);

    private static Task ResetRetryableRefusedAttemptAsync(string tag, FleetAttemptId attemptId) =>
        QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
        {
            Items = Replace(snapshot.Items, tag, current =>
                current.State == QueueItemState.Queued
                && current.AttemptEnvelope?.AttemptId == attemptId
                && current.AttemptRefusedFactDurable
                && !QueueFleetEventOutbox.HasPendingFor(snapshot, attemptId)
                    ? current with
                    {
                        ParentAttemptId = attemptId,
                        AttemptId = null,
                        AttemptBaseRevision = null,
                        AttemptEnvelope = null,
                        AttemptAdmissionFactDurable = false,
                        AttemptStartedFactDurable = false,
                        AttemptRefusedFactDurable = false,
                        AttemptSettledFactDurable = false,
                        LaunchRecoveryKind = null,
                        OriginatingPullRequestRecoveryClaim =
                            current.OriginatingPullRequestRecoveryClaim == attemptId
                                ? null
                                : current.OriginatingPullRequestRecoveryClaim,
                    }
                    : current),
        }, CancellationToken.None);

    private static FleetEventDraft AdmissionEvent(QueueAttemptEnvelope envelope) =>
        AttemptFact(
            FleetEventKind.AdmissionDecided,
            envelope,
            envelope.FactTimestamp,
            outcome: null,
            room: null,
            usage: null,
            artifacts: null);

    private static FleetEventDraft AttemptSettledFact(
        QueueAttemptEnvelope envelope,
        string room,
        WorkflowStatusView terminal,
        DateTimeOffset observedAt)
    {
        var executions = terminal.Steps
            .Where(step => step.Execution is { Length: > 0 })
            .GroupBy(step => step.Execution!, StringComparer.Ordinal)
            .ToList();
        var execution = executions.Count == 1 ? executions[0].First() : null;
        var usage = execution?.Usage;
        return AttemptFact(
            FleetEventKind.AttemptSettled,
            envelope,
            observedAt,
            terminal.State,
            room,
            usage is null
                ? null
                : new FleetEventUsage(
                    usage.TokensIn,
                    usage.TokensOut,
                    usage.CacheReadTokens,
                    usage.CacheCreationTokens,
                    usage.ThinkingTokens,
                    usage.Turns,
                    usage.ToolSteps,
                    usage.RefusedToolSteps,
                    usage.RepeatedToolSteps),
            terminal.Outputs.Count == 0 ? null : terminal.Outputs,
            executionId: execution is null ? null : new ExecutionId(execution.Execution!),
            outcomeDetail: terminal.Error,
            elapsedMilliseconds: usage?.WallClockMs);
    }

    private static FleetEventDraft AttemptFact(
        FleetEventKind kind,
        QueueAttemptEnvelope envelope,
        DateTimeOffset occurredAt,
        string? outcome,
        string? room,
        FleetEventUsage? usage,
        IReadOnlyList<string>? artifacts,
        ExecutionId? executionId = null,
        string? outcomeDetail = null,
        long? elapsedMilliseconds = null) =>
        new(
            kind,
            kind switch
            {
                FleetEventKind.AdmissionDecided => $"admission:{envelope.AttemptId.Value}",
                FleetEventKind.AttemptStarted => $"attempt-started:{envelope.AttemptId.Value}",
                FleetEventKind.AttemptRefused => $"attempt-refused:{envelope.AttemptId.Value}",
                FleetEventKind.AttemptSettled => $"attempt-settled:{envelope.AttemptId.Value}",
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            },
            occurredAt,
            AttemptId: envelope.AttemptId,
            ParentAttemptId: envelope.ParentAttemptId,
            WorkId: new FleetWorkId(envelope.WorkId),
            RoomId: room is null ? null : new FleetRoomId(BatonPaths.RecordKey(room)),
            ExecutionId: executionId,
            IssueId: envelope.Issue,
            PullRequestId: envelope.PullRequest,
            Vendor: envelope.Adapter,
            Model: envelope.Model,
            Effort: envelope.Effort,
            DeclaredRole: envelope.DeclaredRole,
            EffectiveGrant: envelope.EffectiveGrant,
            RequestedRequirements: envelope.RequestedRequirements,
            MissingCapabilities: envelope.MissingCapabilities,
            AdmissionDecision: envelope.AdmissionDecision,
            Outcome: outcome,
            OutcomeDetail: outcomeDetail,
            ElapsedMilliseconds: elapsedMilliseconds,
            Usage: usage,
            ArtifactReferences: artifacts,
            Stage: envelope.Stage is { } stage ? WorkStages.Token(stage) : null,
            AttemptBaseRevision: envelope.AttemptBaseRevision);

    private static FleetEventDraft AdmissionEvent(
        QueueItem item,
        QueueTierResolution tier,
        FleetAttemptId attemptId,
        TaskRequirementAdmission admission,
        DateTimeOffset at) =>
        new(
            FleetEventKind.AdmissionDecided,
            $"admission:{attemptId.Value}",
            at,
            AttemptId: attemptId,
            ParentAttemptId: item.ParentAttemptId,
            WorkId: new FleetWorkId(item.Tag),
            IssueId: item.Issue,
            PullRequestId: item.PullRequest,
            Vendor: tier.Adapter,
            Model: tier.Model,
            Effort: tier.Effort,
            DeclaredRole: item.Role,
            EffectiveGrant: admission.EffectiveGrant,
            RequestedRequirements: admission.Requested,
            MissingCapabilities: admission.Missing,
            AdmissionDecision: admission.Result);

    private static FleetEventDraft AttemptStartedEvent(
        QueueItem item,
        QueueTierResolution? tier,
        FleetAttemptId attemptId,
        string room,
        DateTimeOffset at) =>
        new(
            FleetEventKind.AttemptStarted,
            $"attempt-started:{attemptId.Value}",
            at,
            AttemptId: attemptId,
            ParentAttemptId: item.ParentAttemptId,
            WorkId: new FleetWorkId(item.Tag),
            RoomId: new FleetRoomId(BatonPaths.RecordKey(room)),
            IssueId: item.Issue,
            PullRequestId: item.PullRequest,
            Vendor: tier?.Adapter ?? item.Adapter,
            Model: tier?.Model ?? item.Model,
            Effort: tier?.Effort ?? item.Effort,
            DeclaredRole: item.Role,
            EffectiveGrant: item.LastAdmission?.EffectiveGrant);

    private static FleetEventDraft AttemptRefusedEvent(
        QueueItem item,
        QueueTierResolution tier,
        FleetAttemptId attemptId,
        DateTimeOffset at,
        string outcome) =>
        new(
            FleetEventKind.AttemptRefused,
            $"attempt-refused:{attemptId.Value}",
            at,
            AttemptId: attemptId,
            ParentAttemptId: item.ParentAttemptId,
            WorkId: new FleetWorkId(item.Tag),
            IssueId: item.Issue,
            PullRequestId: item.PullRequest,
            Vendor: tier.Adapter,
            Model: tier.Model,
            Effort: tier.Effort,
            DeclaredRole: item.Role,
            EffectiveGrant: item.LastAdmission?.EffectiveGrant,
            Outcome: outcome);

    private static FleetEventDraft AttemptSettledEvent(
        QueueItem item,
        FleetAttemptId attemptId,
        WorkflowStatusView sentinel,
        DateTimeOffset observedAt)
    {
        var executions = sentinel.Steps
            .Where(step => step.Execution is { Length: > 0 })
            .Select(step => step)
            .GroupBy(step => step.Execution!, StringComparer.Ordinal)
            .ToList();
        var execution = executions.Count == 1 ? executions[0].First() : null;
        var usage = execution?.Usage;

        return new FleetEventDraft(
            FleetEventKind.AttemptSettled,
            $"attempt-settled:{attemptId.Value}",
            observedAt,
            AttemptId: attemptId,
            ParentAttemptId: item.ParentAttemptId,
            WorkId: new FleetWorkId(item.Tag),
            RoomId: new FleetRoomId(BatonPaths.RecordKey(item.RoomDirectory!)),
            ExecutionId: execution is null ? null : new ExecutionId(execution.Execution!),
            IssueId: item.Issue,
            PullRequestId: item.PullRequest,
            DeclaredRole: item.Role,
            EffectiveGrant: item.LastAdmission?.EffectiveGrant,
            Outcome: sentinel.State,
            OutcomeDetail: sentinel.Error,
            ElapsedMilliseconds: usage?.WallClockMs,
            Usage: usage is null
                ? null
                : new FleetEventUsage(
                    usage.TokensIn,
                    usage.TokensOut,
                    usage.CacheReadTokens,
                    usage.CacheCreationTokens,
                    usage.ThinkingTokens,
                    usage.Turns,
                    usage.ToolSteps,
                    usage.RefusedToolSteps,
                    usage.RepeatedToolSteps),
            ArtifactReferences: sentinel.Outputs.Count == 0 ? null : sentinel.Outputs);
    }

    /// <summary>
    /// The live tally comes from the daemon's shared <see cref="DaemonRoomInventory"/>, but requires a
    /// current snapshot because stale admission evidence could exceed the configured fleet ceiling.
    /// <see cref="QueueWeights.For"/> is the one weight function, called here over running rooms and in
    /// <see cref="QueueScheduler"/> over the candidate.
    /// </summary>
    private async Task<double> CountLiveWeightAsync(CancellationToken cancellationToken)
    {
        var total = 0.0;
        IReadOnlyList<DaemonRoomObservation> observations;
        using (DaemonLoopDriver.EnterPhase("room-discovery"))
        {
            // Protected invariant: queue admission never uses a stale live-lane tally. If the shared
            // inventory refresh fails, this tick fails closed instead of authorizing excess work.
            observations = await _roomInventory
                .ObserveAsync(
                    DaemonRoomInventory.InventoryScope.Active,
                    DaemonRoomInventory.InventoryFreshness.Current,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        foreach (var observation in observations)
        {
            var view = observation.View;
            if (view.State != "Running")
            {
                continue;
            }

            total += QueueWeights.For(view.Role, view.Adapter);
        }

        return total;
    }
}
