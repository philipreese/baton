using Baton.Cli.Daemon;
using Baton.Domain;
using Baton.Queue;
using Baton.Status;
using Xunit;

namespace Baton.Cli.Tests.Daemon;

/// <summary>
/// #1934 slice 1, item 6: the daemon service's own arms — launch, the runway-held retry, failure, and
/// done detection from a fixture room. Every source of nondeterminism is injected, so nothing here
/// spawns a process or waits on a real clock.
/// </summary>
/// <remarks>
/// Each test takes its own <see cref="BatonEnvironmentSnapshot.BeginScope"/> temp home, so the queue
/// file, the decision ledger and the rooms directory are this test's own and never the operator's.
/// </remarks>
public sealed class QueueSchedulerServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string CreateTempHome()
    {
        var home = Path.Combine(Path.GetTempPath(), "baton_queue_svc_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(home);
        return home;
    }

    private static QueueItem Item(string tag = "t1", string role = "implement", string? scope = "engine") => new()
    {
        Tag = tag,
        Role = role,
        ScopeClass = scope,
        Workspace = @"C:\repos\w1",
        SpecFile = Path.Combine(Path.GetTempPath(), "never-read.md"),
    };

    private static QueueSchedulerService Service(
        Func<QueueLaunchRequest, CancellationToken, Task<QueueLaunchOutcome>> launch,
        double liveWeight = 0,
        double? freeGb = 16.0,
        DateTimeOffset? now = null) =>
        new(launch, _ => Task.FromResult(liveWeight), () => freeGb, () => now ?? DateTimeOffset.UtcNow);

    [Fact]
    public async Task A_launchable_item_is_marked_launched_with_its_room_and_recorded_as_launched()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with { Items = [Item()] }, Ct);
            QueueLaunchRequest? seen = null;
            var service = Service((request, _) =>
            {
                seen = request;
                return Task.FromResult(new QueueLaunchOutcome(request.RoomDirectory));
            });

            await service.TickOnceAsync(Ct);

            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(QueueItemState.Launched, item.State);

            // The scheduler picks the room and hands it to the launch, rather than learning it back
            // afterwards — that is what lets the item be marked before the dispatch starts.
            Assert.Equal(Path.Combine(BatonPaths.Rooms, "queue-t1-" + seen!.RoomDirectory[^8..]), item.RoomDirectory);
            Assert.NotNull(item.LaunchedAt);

            // The launcher gets the RESOLVED tier, not the raw item — the queue resolves once and the
            // launch does not re-derive it.
            Assert.Equal("claude", seen!.Tier.Adapter);
            Assert.Equal("opus", seen.Tier.Model);

            var fact = Assert.Single(await QueueDecisionLedgerStore.ReadAllAsync(BatonPaths.QueueDecisionLedgerFile, Ct));
            Assert.Equal("launched", fact.Decision);
            Assert.Equal("t1", fact.Tag);
            Assert.Equal("engine", fact.Tier);
            Assert.Equal(item.RoomDirectory, fact.Room);
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task The_item_is_marked_launched_before_the_launch_starts_so_a_shutdown_mid_launch_cannot_relaunch_it()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with { Items = [Item()] }, Ct);

            // Read the queue from INSIDE the launch, which is the window a daemon shutdown lands in:
            // the dispatch is running detached on CancellationToken.None, so whatever the file says
            // here is what the next daemon start reads.
            QueueItem? duringLaunch = null;
            CancellationToken tokenLaunchSaw = default;
            var service = Service(async (request, ct) =>
            {
                tokenLaunchSaw = ct;
                duringLaunch = (await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items.Single();
                return new QueueLaunchOutcome(request.RoomDirectory);
            });

            await service.TickOnceAsync(Ct);

            Assert.Equal(QueueItemState.Launched, duringLaunch!.State);
            Assert.NotNull(duringLaunch.RoomDirectory);
            Assert.NotNull(duringLaunch.LaunchedAt);

            // Recorded under the same token the launch runs under: a cancelled token would mean
            // QueueStore.MutateAsync never ran its delegate at all.
            Assert.False(tokenLaunchSaw.CanBeCanceled);

            // And the room the item was marked with is the room the dispatch was handed.
            var after = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(duringLaunch.RoomDirectory, after.RoomDirectory);
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task A_shutdown_raised_out_of_the_launch_fails_the_item_rather_than_leaving_it_launched()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with { Items = [Item()] }, Ct);
            var service = Service((_, _) => throw new OperationCanceledException());

            await service.TickOnceAsync(Ct);

            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(QueueItemState.Failed, item.State);
            Assert.Contains("shut down", item.Error!, StringComparison.Ordinal);

            var fact = Assert.Single(await QueueDecisionLedgerStore.ReadAllAsync(BatonPaths.QueueDecisionLedgerFile, Ct));
            Assert.Equal("failed", fact.Decision);
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task A_throw_the_launcher_does_not_model_is_recorded_as_a_failure_not_swallowed_by_the_loop()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with { Items = [Item()] }, Ct);
            var service = Service((_, _) => throw new IOException("the settle write failed"));

            await service.TickOnceAsync(Ct);

            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(QueueItemState.Failed, item.State);
            Assert.Contains("the settle write failed", item.Error!, StringComparison.Ordinal);

            var fact = Assert.Single(await QueueDecisionLedgerStore.ReadAllAsync(BatonPaths.QueueDecisionLedgerFile, Ct));
            Assert.Equal("failed", fact.Decision);
            Assert.Equal("t1", fact.Tag);
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task An_evaluation_that_throws_before_any_decision_still_writes_a_fact()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            // A malformed queue file is refused rather than read as empty (spec/baton.md §13), which
            // throws before the tick reaches a decision. The ledger must still carry a row: the hole
            // this closes is an evaluation that happened and left nothing behind.
            Directory.CreateDirectory(Path.GetDirectoryName(BatonPaths.QueueFile)!);
            await File.WriteAllTextAsync(BatonPaths.QueueFile, "{ not json", Ct);
            var launched = false;
            var service = Service((_, _) =>
            {
                launched = true;
                return Task.FromResult(new QueueLaunchOutcome(null));
            });

            await Assert.ThrowsAnyAsync<Exception>(() => service.TickOnceAsync(Ct));

            Assert.False(launched);
            var fact = Assert.Single(await QueueDecisionLedgerStore.ReadAllAsync(BatonPaths.QueueDecisionLedgerFile, Ct));
            Assert.Equal("failed", fact.Decision);
            Assert.Null(fact.Tag);
            // Absent, never a fabricated zero -- the reading was never taken.
            Assert.Null(fact.FreeGb);
            Assert.Contains("recorded no counters", fact.Reason!, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task A_runway_hold_leaves_the_item_queued_and_records_runway_held()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with { Items = [Item()] }, Ct);
            var service = Service((_, _) => Task.FromResult(new QueueLaunchOutcome(null, RunwayHeld: true)));

            await service.TickOnceAsync(Ct);

            // Q5's arm, and now also the undo of the pre-launch mark: nothing was dispatched, so the
            // item is back exactly as it was and the next tick considers it again.
            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(QueueItemState.Queued, item.State);
            Assert.Null(item.RoomDirectory);
            Assert.Null(item.LaunchedAt);

            var fact = Assert.Single(await QueueDecisionLedgerStore.ReadAllAsync(BatonPaths.QueueDecisionLedgerFile, Ct));
            Assert.Equal("waited", fact.Decision);
            Assert.Equal("runway-held", fact.Reason);
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task A_hold_and_a_launch_are_told_apart_by_the_outcome_not_by_an_exception()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with { Items = [Item()] }, Ct);
            // A refusal that is NOT a hold — a missing spec, an unknown role, a drain marker. It must
            // fail the item OUT of the queue rather than retry it forever with a false reason, which is
            // exactly what branching on the exception type would have done.
            var service = Service((_, _) => Task.FromResult(new QueueLaunchOutcome(null, Error: "no such role 'implment'")));

            await service.TickOnceAsync(Ct);

            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(QueueItemState.Failed, item.State);
            Assert.Contains("implment", item.Error!, StringComparison.Ordinal);

            var fact = Assert.Single(await QueueDecisionLedgerStore.ReadAllAsync(BatonPaths.QueueDecisionLedgerFile, Ct));
            Assert.Equal("failed", fact.Decision);
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task A_scope_class_with_no_configured_tier_fails_the_item_rather_than_launching_it()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            // 'review' + 'infra' is a key in neither the shipped table nor a configured one. Written
            // straight into the store here, bypassing the verb — which is the only way this state can
            // arise, and therefore the only way the daemon's own check can be exercised at all.
            await QueueStore.MutateAsync(
                BatonPaths.QueueFile, s => s with { Items = [Item(role: "review", scope: "infra")] }, Ct);
            var launched = false;
            var service = Service((_, _) =>
            {
                launched = true;
                return Task.FromResult(new QueueLaunchOutcome(@"C:\rooms\x"));
            });

            await service.TickOnceAsync(Ct);

            // The launcher must not be reached at all — a refusal recorded after a dispatch started
            // would be a lane already spending on some other tier's model.
            Assert.False(launched);
            Assert.Equal(
                QueueItemState.Failed,
                Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items).State);
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task A_held_queue_records_the_hold_and_never_calls_the_launcher()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with { Items = [Item()], Held = true }, Ct);
            var launched = false;
            var service = Service((_, _) =>
            {
                launched = true;
                return Task.FromResult(new QueueLaunchOutcome(@"C:\rooms\x"));
            });

            await service.TickOnceAsync(Ct);

            Assert.False(launched);
            Assert.Equal(
                "hold",
                Assert.Single(await QueueDecisionLedgerStore.ReadAllAsync(BatonPaths.QueueDecisionLedgerFile, Ct)).Reason);
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task Done_detection_reads_the_room_and_marks_a_clean_settle_done()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var room = Path.Combine(home, "rooms", "queue-t1-abcd");
            Directory.CreateDirectory(room);
            await File.WriteAllTextAsync(
                Path.Combine(room, TerminalSentinelWriter.TerminalSentinelFileName),
                // "Succeeded" is the room-level word WorkflowOutcome.Describe writes into a sentinel;
                // "Terminal" — what this fixture carried until #1939's review — is a WorkflowStatus
                // value no projector ever puts in this field, so the assertion below passed vacuously.
                """{"state":"Succeeded","steps":[{"id":"implement","state":"Succeeded","execution":"e1"}],"outputs":[],"error":null}""",
                Ct);

            await QueueStore.MutateAsync(
                BatonPaths.QueueFile,
                s => s with { Items = [Item() with { State = QueueItemState.Launched, RoomDirectory = room }] },
                Ct);

            await Service((_, _) => Task.FromResult(new QueueLaunchOutcome(null))).ResolveFinishedItemsAsync(Ct);

            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(QueueItemState.Done, item.State);
            Assert.Null(item.Error);
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task A_launched_item_whose_room_is_not_terminal_yet_stays_launched()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            // The control arm for the two classification tests: no sentinel means no verdict yet, not
            // "done". Without it, a test suite that only ever wrote sentinels could not tell the
            // detection from an unconditional mark.
            var room = Path.Combine(home, "rooms", "queue-t1-live");
            Directory.CreateDirectory(room);
            await QueueStore.MutateAsync(
                BatonPaths.QueueFile,
                s => s with { Items = [Item() with { State = QueueItemState.Launched, RoomDirectory = room }] },
                Ct);

            await Service((_, _) => Task.FromResult(new QueueLaunchOutcome(null))).ResolveFinishedItemsAsync(Ct);

            Assert.Equal(
                QueueItemState.Launched,
                Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items).State);
        }
        finally
        {
            Cleanup(home);
        }
    }

    /// <summary>
    /// The roomless sweep (#1939 review): a launch whose room was never created can never produce a
    /// sentinel, so without this the item sits in <see cref="QueueItemState.Launched"/> forever. The
    /// two cases are one clock apart, which is what makes the grace period the discriminator rather
    /// than "the directory is missing".
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_launched_item_whose_room_was_never_created_fails_only_once_the_grace_period_has_passed(bool pastGrace)
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var now = DateTimeOffset.UtcNow;
            var launchedAt = pastGrace
                ? now - QueueSchedulerService.NoRoomGrace - TimeSpan.FromMinutes(1)
                : now - TimeSpan.FromSeconds(5);
            var room = Path.Combine(home, "rooms", "queue-t1-never-made");

            await QueueStore.MutateAsync(
                BatonPaths.QueueFile,
                s => s with
                {
                    Items = [Item() with
                    {
                        State = QueueItemState.Launched, RoomDirectory = room, LaunchedAt = launchedAt,
                    }],
                },
                Ct);

            await Service((_, _) => Task.FromResult(new QueueLaunchOutcome(null)), now: now)
                .ResolveFinishedItemsAsync(Ct);

            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(pastGrace ? QueueItemState.Failed : QueueItemState.Launched, item.State);
            if (pastGrace)
            {
                Assert.Contains(room, item.Error!, StringComparison.Ordinal);
            }
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task An_imported_launched_item_carrying_no_room_is_never_swept()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            // QueueImport's own remarks: the runner recorded no room, so the operator clears these by
            // hand. Sweeping them as "no-room" would fail lanes that are in fact running.
            var now = DateTimeOffset.UtcNow;
            await QueueStore.MutateAsync(
                BatonPaths.QueueFile,
                s => s with
                {
                    Items = [Item() with
                    {
                        State = QueueItemState.Launched,
                        RoomDirectory = null,
                        LaunchedAt = now - TimeSpan.FromDays(1),
                    }],
                },
                Ct);

            await Service((_, _) => Task.FromResult(new QueueLaunchOutcome(null)), now: now)
                .ResolveFinishedItemsAsync(Ct);

            Assert.Equal(
                QueueItemState.Launched,
                Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items).State);
        }
        finally
        {
            Cleanup(home);
        }
    }

    /// <summary>
    /// Every non-success word the projector emits, with the word SOURCED from
    /// <see cref="WorkflowOutcome.Describe"/> over a hand-built terminal state rather than typed as a
    /// literal — the first assertion in each case is the control, and it is what the previous version
    /// of these tests lacked: they fed <c>"Terminal"</c>, a <see cref="WorkflowStatus"/> value no
    /// projector writes into that field, so they discriminated nothing (#1939 review, HIGH).
    /// </summary>
    /// <remarks>
    /// The three cases are the review's three failure scenarios: <c>baton cancel</c> on a launched
    /// lane, an approval-gate reject or <c>baton resolve --reject</c>, and a Terminal room left with an
    /// unreachable step. None of them sets <see cref="WorkflowStatusView.Error"/>, which is why each
    /// one read as Done before.
    /// </remarks>
    [Theory]
    [InlineData(StepStatus.Cancelled, WorkflowOutcome.Cancelled)]
    [InlineData(StepStatus.Rejected, WorkflowOutcome.Failed)]
    [InlineData(StepStatus.Pending, WorkflowOutcome.Failed)]
    public void A_room_that_did_not_settle_succeeded_is_failed_and_carries_its_own_outcome_word(
        StepStatus status, string expectedWord)
    {
        var word = WorkflowOutcome.Describe(TerminalState([Step("implement", status)]));
        Assert.Equal(expectedWord, word);

        var sentinel = new WorkflowStatusView(
            word, [new WorkflowStatusStepView("implement", status.ToString(), "e1")], [], null);

        var (state, error) = QueueSchedulerService.ClassifyTerminal(sentinel, @"C:\rooms\r1");

        Assert.Equal(QueueItemState.Failed, state);
        Assert.Contains(@"C:\rooms\r1", error!, StringComparison.Ordinal);
        Assert.Contains(word, error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_indeterminate_room_is_failed_and_names_the_resolve_remedy()
    {
        // The word is room-level, never a step state — QueueSchedulerService.ClassifyTerminal's own
        // remarks cite the #1608 ruling that makes it so. The control below is what pins it: a
        // predicate over step states, which is what this classifier used to run, could not have
        // produced this word from this step.
        var word = WorkflowOutcome.Describe(
            TerminalState([Step("implement", StepStatus.Failed) with { IndeterminateAwaitingResolution = true }]));
        Assert.Equal(WorkflowOutcome.Indeterminate, word);

        var sentinel = new WorkflowStatusView(
            word, [new WorkflowStatusStepView("implement", "Failed", "e1")], [], null);

        var (state, error) = QueueSchedulerService.ClassifyTerminal(sentinel, @"C:\rooms\r1");

        Assert.Equal(QueueItemState.Failed, state);
        Assert.Contains(@"C:\rooms\r1", error!, StringComparison.Ordinal);
        Assert.Contains("baton resolve", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_settled_success_is_the_only_thing_that_is_done_and_it_keeps_no_error()
    {
        var word = WorkflowOutcome.Describe(TerminalState([Step("implement", StepStatus.Succeeded)]));
        Assert.Equal(WorkflowOutcome.Succeeded, word);

        var (state, error) = QueueSchedulerService.ClassifyTerminal(
            new WorkflowStatusView(word, [new WorkflowStatusStepView("implement", "Succeeded", "e1")], [], null),
            @"C:\rooms\r1");

        Assert.Equal(QueueItemState.Done, state);
        Assert.Null(error);
    }

    /// <summary>
    /// The fail-closed arm <see cref="QueueSchedulerService.ClassifyTerminal"/>'s remarks describe: a
    /// hand-edited sentinel, one with no <c>state</c> field at all, or one written by a future
    /// <see cref="WorkflowOutcome"/> member nobody swept.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Terminal")]
    public void A_sentinel_carrying_no_outcome_word_this_assembly_knows_is_failed(string? word)
    {
        var (state, error) = QueueSchedulerService.ClassifyTerminal(
            new WorkflowStatusView(word!, [], [], null), @"C:\rooms\r1");

        Assert.Equal(QueueItemState.Failed, state);
        Assert.Contains(@"C:\rooms\r1", error!, StringComparison.Ordinal);
    }

    private static readonly WorkflowDefinitionSnapshotId SnapshotId = new(Guid.NewGuid().ToString("N"));

    private static FlowState TerminalState(IReadOnlyList<StepState> steps) =>
        new(SnapshotId, steps, WorkflowStatus.Terminal);

    private static StepState Step(string stepId, StepStatus status) =>
        new(new StepId(stepId), status, new ExecutionId(Guid.NewGuid().ToString("N")), new Dictionary<StepId, ExecutionId>());

    private static void Cleanup(string home) => DirectoryCleanup.DeleteRecursively(home);
}
