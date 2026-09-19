using Baton.Cli.Daemon;
using Baton.Domain;
using Baton.Queue;
using Baton.Status;
using Baton.Tests.Shared;

namespace Baton.Cli.Tests.Daemon;

public sealed class ConductorContinuationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Deterministic_continue_identity_carries_canonical_source_evidence()
    {
        var room = Path.Combine(Path.GetTempPath(), "baton-continuation-room");
        var attempt = new FleetAttemptId("attempt-1");
        var item = Item(attempt, room);
        var transition = new WorkItemTransition(WorkItemTransitionKind.Dispatch, WorkStage.Continue, 3, "retry");
        var sentinel = new WorkflowStatusView(
            WorkflowOutcome.Failed,
            [new WorkflowStatusStepView("implement", "Failed", "execution-1")],
            [],
            null);

        var request = ConductorContinuation.TryRequest(
            item, room, sentinel, "head-1", transition, DateTimeOffset.Parse("2026-09-18T12:00:00Z"));

        Assert.NotNull(request);
        Assert.Equal("continue:continue-tag:attempt-1:3", request.IdempotencyKey);
        Assert.Equal("github.com/aer-works/baton", request.TargetProject);
        Assert.Equal(BatonPaths.RecordKey(room), request.TargetRoom);
        Assert.Equal("execution-1", request.TargetExecution);
        Assert.Equal("head-1", request.PullRequestHead);
        Assert.Equal("continue", request.RequestedAction);
    }

    [Fact]
    public async Task Scheduler_observes_the_same_open_obligation_after_restart()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var sourceAttempt = new FleetAttemptId("attempt-1");
            var room = Path.Combine(home, "rooms", "settled-source");
            Directory.CreateDirectory(room);
            var projection = BatonPaths.ConductorObligationsFile;
            var log = new FleetEventLog(
                BatonPaths.FleetEventsFile,
                BatonPaths.FleetEventsRolloverFile,
                100_000);
            var firstStore = new ConductorObligationStore(log, projection, () => DateTimeOffset.UnixEpoch);
            var request = ConductorContinuation.TryRequest(
                Item(sourceAttempt, room),
                room,
                new WorkflowStatusView(
                    WorkflowOutcome.Failed,
                    [new WorkflowStatusStepView("implement", "Failed", "execution-1")],
                    [],
                    null),
                "head-1",
                new WorkItemTransition(WorkItemTransitionKind.Dispatch, WorkStage.Continue, 3, "retry"),
                DateTimeOffset.UnixEpoch)!;
            await firstStore.EnqueueAsync(request, Ct);

            await QueueStore.MutateAsync(
                BatonPaths.QueueFile,
                snapshot => snapshot with
                {
                    Items =
                    [
                        Item(sourceAttempt, room) with
                        {
                            Stage = WorkStage.Continue,
                            Round = 3,
                            ParentAttemptId = sourceAttempt,
                            AttemptId = new FleetAttemptId("child-attempt"),
                            State = QueueItemState.Failed,
                            Halted = true,
                        },
                    ],
                },
                Ct);

            var restartedStore = new ConductorObligationStore(log, projection, () => DateTimeOffset.UnixEpoch);
            var service = new QueueSchedulerService(
                (_, _) => throw new InvalidOperationException("an observed continuation must not launch"),
                _ => Task.FromResult(0d),
                () => 16d,
                () => DateTimeOffset.UnixEpoch,
                conductorObligations: restartedStore);

            await service.TickOnceAsync(Ct);

            var observed = await restartedStore.ReadAsync(request.IdempotencyKey, Ct);
            Assert.Equal(ConductorObligationStatus.ActionObserved, observed!.Status);
            Assert.Empty(await restartedStore.ReconcileAsync(Ct));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Scheduler_requires_the_obligation_tag_when_selecting_continuation_evidence()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var sourceAttempt = new FleetAttemptId("attempt-1");
            var room = Path.Combine(home, "rooms", "settled-source");
            Directory.CreateDirectory(room);
            var log = new FleetEventLog(
                BatonPaths.FleetEventsFile,
                BatonPaths.FleetEventsRolloverFile,
                100_000);
            var store = new ConductorObligationStore(log, BatonPaths.ConductorObligationsFile);
            var request = ConductorContinuation.TryRequest(
                Item(sourceAttempt, room),
                room,
                new WorkflowStatusView(
                    WorkflowOutcome.Failed,
                    [new WorkflowStatusStepView("implement", "Failed", "execution-1")],
                    [],
                    null),
                null,
                new WorkItemTransition(WorkItemTransitionKind.Dispatch, WorkStage.Continue, 1, "retry"),
                DateTimeOffset.UnixEpoch)!;
            await store.EnqueueAsync(request, Ct);

            await QueueStore.MutateAsync(
                BatonPaths.QueueFile,
                snapshot => snapshot with
                {
                    Items =
                    [
                        Item(sourceAttempt, room) with
                        {
                            Tag = "different-tag",
                            Stage = WorkStage.Continue,
                            Round = 1,
                            ParentAttemptId = sourceAttempt,
                            AttemptId = new FleetAttemptId("child-attempt"),
                            State = QueueItemState.Failed,
                            Halted = true,
                        },
                    ],
                },
                Ct);

            var service = new QueueSchedulerService(
                (_, _) => throw new InvalidOperationException("a mismatched tag must not satisfy recovery"),
                _ => Task.FromResult(0d),
                () => 16d,
                () => DateTimeOffset.UnixEpoch,
                conductorObligations: store);

            await service.TickOnceAsync(Ct);

            var blocked = await store.ReadAsync(request.IdempotencyKey, Ct);
            Assert.Equal(ConductorObligationStatus.Blocked, blocked!.Status);
            Assert.Contains("source attempt", blocked.Reason!, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Scheduler_blocks_an_open_obligation_when_its_source_disappears()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var sourceAttempt = new FleetAttemptId("attempt-1");
            var room = Path.Combine(home, "rooms", "settled-source");
            Directory.CreateDirectory(room);
            var log = new FleetEventLog(
                BatonPaths.FleetEventsFile,
                BatonPaths.FleetEventsRolloverFile,
                100_000);
            var store = new ConductorObligationStore(log, BatonPaths.ConductorObligationsFile);
            var request = ConductorContinuation.TryRequest(
                Item(sourceAttempt, room),
                room,
                new WorkflowStatusView(
                    WorkflowOutcome.Failed,
                    [new WorkflowStatusStepView("implement", "Failed", "execution-1")],
                    [],
                    null),
                null,
                new WorkItemTransition(WorkItemTransitionKind.Dispatch, WorkStage.Continue, 1, "retry"),
                DateTimeOffset.UnixEpoch)!;
            await store.EnqueueAsync(request, Ct);

            var service = new QueueSchedulerService(
                (_, _) => throw new InvalidOperationException("a missing source must not launch"),
                _ => Task.FromResult(0d),
                () => 16d,
                () => DateTimeOffset.UnixEpoch,
                conductorObligations: store);

            await service.TickOnceAsync(Ct);

            var blocked = await store.ReadAsync(request.IdempotencyKey, Ct);
            Assert.Equal(ConductorObligationStatus.Blocked, blocked!.Status);
            Assert.Contains("source attempt", blocked.Reason!, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public void Ambiguous_or_missing_source_evidence_does_not_create_a_continue_obligation()
    {
        var room = Path.Combine(Path.GetTempPath(), "baton-continuation-room");
        var item = Item(new FleetAttemptId("attempt-1"), room);
        var transition = new WorkItemTransition(WorkItemTransitionKind.Dispatch, WorkStage.Continue, 1, "retry");

        Assert.Null(ConductorContinuation.TryRequest(
            item,
            room,
            new WorkflowStatusView(
                WorkflowOutcome.Failed,
                [
                    new WorkflowStatusStepView("implement", "Failed", "execution-1"),
                    new WorkflowStatusStepView("review", "Failed", "execution-2"),
                ],
                [],
                null),
            null,
            transition,
            DateTimeOffset.UnixEpoch));
        Assert.Null(ConductorContinuation.TryRequest(
            item with { AttemptId = null },
            room,
            new WorkflowStatusView(WorkflowOutcome.Failed, [], [], null),
            null,
            transition,
            DateTimeOffset.UnixEpoch));
    }

    private static QueueItem Item(FleetAttemptId attemptId, string room) => new()
    {
        Tag = "continue-tag",
        Role = "implement",
        Stage = WorkStage.Implement,
        Repository = "github.com/aer-works/baton",
        Workspace = room,
        SpecFile = Path.Combine(room, "brief.md"),
        RoomDirectory = room,
        State = QueueItemState.Done,
        AttemptId = attemptId,
        LaunchedAt = DateTimeOffset.UnixEpoch,
    };

    private static string CreateTempHome()
    {
        var home = Path.Combine(Path.GetTempPath(), "baton_continuation_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        return home;
    }
}
