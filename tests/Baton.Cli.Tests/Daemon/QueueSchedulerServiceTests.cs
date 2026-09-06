using Baton.Cli.Daemon;
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
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with { Items = [Item()] });
            QueueLaunchRequest? seen = null;
            var service = Service((request, _) =>
            {
                seen = request;
                return Task.FromResult(new QueueLaunchOutcome(@"C:\rooms\queue-t1-abcd"));
            });

            await service.TickOnceAsync(CancellationToken.None);

            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile)).Items);
            Assert.Equal(QueueItemState.Launched, item.State);
            Assert.Equal(@"C:\rooms\queue-t1-abcd", item.RoomDirectory);
            Assert.NotNull(item.LaunchedAt);

            // The launcher gets the RESOLVED tier, not the raw item — the queue resolves once and the
            // launch does not re-derive it.
            Assert.Equal("claude", seen!.Tier.Adapter);
            Assert.Equal("opus", seen.Tier.Model);

            var fact = Assert.Single(await QueueDecisionLedgerStore.ReadAllAsync(BatonPaths.QueueDecisionLedgerFile));
            Assert.Equal("launched", fact.Decision);
            Assert.Equal("t1", fact.Tag);
            Assert.Equal("engine", fact.Tier);
            Assert.Equal(@"C:\rooms\queue-t1-abcd", fact.Room);
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
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with { Items = [Item()] });
            var service = Service((_, _) => Task.FromResult(new QueueLaunchOutcome(null, RunwayHeld: true)));

            await service.TickOnceAsync(CancellationToken.None);

            // Q5: the hold is a fleet condition, not a property of this item, so the item must stay
            // available for the next gap rather than being consumed.
            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile)).Items);
            Assert.Equal(QueueItemState.Queued, item.State);
            Assert.Null(item.RoomDirectory);

            var fact = Assert.Single(await QueueDecisionLedgerStore.ReadAllAsync(BatonPaths.QueueDecisionLedgerFile));
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
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with { Items = [Item()] });
            // A refusal that is NOT a hold — a missing spec, an unknown role, a drain marker. It must
            // fail the item OUT of the queue rather than retry it forever with a false reason, which is
            // exactly what branching on the exception type would have done.
            var service = Service((_, _) => Task.FromResult(new QueueLaunchOutcome(null, Error: "no such role 'implment'")));

            await service.TickOnceAsync(CancellationToken.None);

            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile)).Items);
            Assert.Equal(QueueItemState.Failed, item.State);
            Assert.Contains("implment", item.Error!, StringComparison.Ordinal);

            var fact = Assert.Single(await QueueDecisionLedgerStore.ReadAllAsync(BatonPaths.QueueDecisionLedgerFile));
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
            // 'engine' is a valid scope class for implement but there is no 'review-engine'... there is.
            // Use a role/scope pair whose key exists in neither the shipped table nor a configured one:
            // a role of 'review' with a scope only reachable by hand-editing the queue file.
            var item = Item(role: "review", scope: "infra");
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with { Items = [item] });
            var launched = false;
            var service = Service((_, _) =>
            {
                launched = true;
                return Task.FromResult(new QueueLaunchOutcome(@"C:\rooms\x"));
            });

            await service.TickOnceAsync(CancellationToken.None);

            // Fail closed: silently launching on the role's own default model is the "ran on the wrong
            // tier" failure the table exists to prevent.
            Assert.False(launched);
            Assert.Equal(QueueItemState.Failed, Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile)).Items).State);
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
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with { Items = [Item()], Held = true });
            var launched = false;
            var service = Service((_, _) =>
            {
                launched = true;
                return Task.FromResult(new QueueLaunchOutcome(@"C:\rooms\x"));
            });

            await service.TickOnceAsync(CancellationToken.None);

            Assert.False(launched);
            Assert.Equal("hold", Assert.Single(await QueueDecisionLedgerStore.ReadAllAsync(BatonPaths.QueueDecisionLedgerFile)).Reason);
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
                """{"state":"Terminal","steps":[{"id":"implement","state":"Succeeded","execution":"e1"}],"outputs":[],"error":null}""");

            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with
            {
                Items = [Item() with { State = QueueItemState.Launched, RoomDirectory = room }],
            });

            await Service((_, _) => Task.FromResult(new QueueLaunchOutcome(null))).ResolveFinishedItemsAsync(CancellationToken.None);

            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile)).Items);
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
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with
            {
                Items = [Item() with { State = QueueItemState.Launched, RoomDirectory = room }],
            });

            await Service((_, _) => Task.FromResult(new QueueLaunchOutcome(null))).ResolveFinishedItemsAsync(CancellationToken.None);

            Assert.Equal(
                QueueItemState.Launched,
                Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile)).Items).State);
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public void An_indeterminate_room_classifies_as_failed_with_its_room_id()
    {
        var indeterminate = new WorkflowStatusView(
            "Terminal",
            [new WorkflowStatusStepView("implement", "IndeterminateAwaitingResolution", "e1")],
            [],
            null);

        var (state, error) = QueueSchedulerService.ClassifyTerminal(indeterminate, @"C:\rooms\r1");

        Assert.Equal(QueueItemState.Failed, state);
        Assert.Contains(@"C:\rooms\r1", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_terminal_room_carrying_an_error_classifies_as_failed_and_a_clean_one_as_done()
    {
        var failed = new WorkflowStatusView(
            "Terminal", [new WorkflowStatusStepView("implement", "Failed", "e1")], [], "verify step failed");
        var succeeded = new WorkflowStatusView(
            "Terminal", [new WorkflowStatusStepView("implement", "Succeeded", "e1")], [], null);

        Assert.Equal(QueueItemState.Failed, QueueSchedulerService.ClassifyTerminal(failed, "r").State);
        Assert.Equal(QueueItemState.Done, QueueSchedulerService.ClassifyTerminal(succeeded, "r").State);
    }

    private static void Cleanup(string home)
    {
        try
        {
            Directory.Delete(home, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A temp home that will not delete is not this test's subject.
        }
    }
}
