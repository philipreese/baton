using System.Text.Json;
using Baton.Accounting;
using Baton.Cli.Daemon;
using Baton.Conductor;
using Baton.Domain;
using Baton.Queue;
using Baton.Status;
using Baton.Tests.Shared;
using Baton.Vendors;

namespace Baton.Cli.Tests.Daemon;

public sealed class StoppedWorkAdviceCaptureTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
    private const string Repository = "github.com/aer-works/baton";
    private const string Head = "aaaaaaaabbbbbbbbccccccccddddddddeeeeeeee";
    private static readonly RepositoryIdentity Identity =
        RepositoryIdentity.From("https://github.com/aer-works/baton.git", null)!;

    private sealed class FakeGh : IGhCliRunner
    {
        public List<string[]> Calls { get; } = [];

        public Task<GhCliResult> RunAsync(string workspace, IReadOnlyList<string> args,
            CancellationToken cancellationToken)
        {
            Calls.Add(args.ToArray());
            Assert.Equal(Repository, args[^1]);
            if (args is ["pr", "checks", ..])
            {
                return Task.FromResult(new GhCliResult(true, 0,
                    "[{\"name\":\"ci\",\"bucket\":\"pass\",\"state\":\"SUCCESS\"}]", ""));
            }

            const string pr = "{\"number\":77,\"state\":\"OPEN\",\"isDraft\":true,"
                + "\"headRefOid\":\"" + Head + "\",\"headRefName\":\"1934-lane\","
                + "\"baseRefName\":\"main\",\"isCrossRepository\":false,"
                + "\"statusCheckRollup\":[]}";
            return Task.FromResult(new GhCliResult(true, 0,
                args is ["pr", "view", ..] ? pr : "[" + pr + "]", ""));
        }
    }

    [Fact]
    public void Opt_in_is_exact_true_for_the_canonical_repository_only()
    {
        static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
        static QueueSettings Settings(string repository, string value) => new()
        {
            StoppedWorkAdvice = new Dictionary<string, JsonElement>
            {
                [repository] = Json(value),
            },
        };

        Assert.True(Settings(Repository, "true").IsStoppedWorkAdviceEnabled(Repository));
        Assert.False(Settings(Repository, "false").IsStoppedWorkAdviceEnabled(Repository));
        Assert.False(Settings(Repository, "\"true\"").IsStoppedWorkAdviceEnabled(Repository));
        Assert.False(Settings("https://github.com/aer-works/baton.git", "true")
            .IsStoppedWorkAdviceEnabled(Repository));
    }

    [Fact]
    public void Key_round_trip_uses_the_work_stage_token_for_re_review()
    {
        var key = StoppedWorkJudgmentKey.For(Repository, "1934-lane",
            new FleetAttemptId("attempt-re-review"), WorkStage.ReReview);
        Assert.True(StoppedWorkJudgmentKey.TryParse(key, out var repository, out var tag,
            out var attempt, out var stage));
        Assert.Equal(Repository, repository);
        Assert.Equal("1934-lane", tag);
        Assert.Equal("attempt-re-review", attempt.Value);
        Assert.Equal(WorkStage.ReReview, stage);
    }

    [Fact]
    public async Task A_new_needs_operator_halt_captures_typed_intent_when_opted_in()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            await EnableStoppedWorkAdviceAsync();
            await ConductorClaimStore.ClaimAsync(Identity, "conductor-fixture",
                cancellationToken: Ct);
            var room = await WriteSettledRoomAsync(home, WorkflowOutcome.Succeeded, DecisionlessVerdict);
            var attempt = new FleetAttemptId("stopped-advice-attempt");
            await SeedAsync(home, WorkStage.Review, room, attemptId: attempt);
            var gh = new FakeGh();

            var facts = await new WorkItemAdvancer(
                gh, (_, _) => Task.FromResult<string?>(Head)).AdvanceAsync(Now, Ct);

            Assert.Equal(QueueDecisionEntry.Failed, Assert.Single(facts).Decision);
            var item = await ReadBackAsync();
            Assert.NotNull(item.StoppedWorkJudgment);
            var judgment = item.StoppedWorkJudgment!;
            Assert.True(item.Halted);
            Assert.Equal(StoppedWorkJudgmentState.Pending, judgment.State);
            Assert.Equal(StoppedWorkJudgmentKey.For(Repository, item.Tag, attempt, WorkStage.Review),
                judgment.Key);
            Assert.Equal("conductor-fixture", judgment.Holder);
            Assert.Equal(attempt, judgment.AttemptId);
            Assert.Equal(Head, judgment.PullRequestHead);
            Assert.Equal(StoppedWorkAdviceEvidence.Hash(StoppedWorkAdviceEvidence.Context(judgment)),
                judgment.ContextSha256);

            var store = Store();
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            var scheduler = Scheduler(store, new WorkItemAdvancer(
                new FakeGh(), (_, _) => Task.FromResult<string?>(Head)),
                (obligation, request, _, _, _) =>
                {
                    Interlocked.Increment(ref calls);
                    entered.TrySetResult(true);
                    return Task.FromResult(Response(obligation, request));
                });
            await scheduler.TickOnceAsync(Ct);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);
            await WaitForAsync(async () =>
                (await store.ReadAsync(judgment.Key!, Ct))?.Status
                    == ConductorObligationStatus.TransportAcknowledged);
            Assert.Equal(1, calls);
            Assert.Equal(ConductorObligationStatus.TransportAcknowledged,
                (await store.ReadAsync(judgment.Key!, Ct))?.Status);
            await scheduler.DrainStoppedWorkAdviceAsync();
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task A_new_needs_operator_halt_creates_no_intent_when_opt_out_is_default()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var room = await WriteSettledRoomAsync(home, WorkflowOutcome.Succeeded, DecisionlessVerdict);
            await SeedAsync(home, WorkStage.Review, room,
                attemptId: new FleetAttemptId("stopped-advice-disabled"));

            var facts = await new WorkItemAdvancer(
                new FakeGh(), (_, _) => Task.FromResult<string?>(Head)).AdvanceAsync(Now, Ct);

            Assert.Equal(QueueDecisionEntry.Failed, Assert.Single(facts).Decision);
            var item = await ReadBackAsync();
            Assert.True(item.Halted);
            Assert.Null(item.StoppedWorkJudgment);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task A_missing_holder_and_attempt_capture_a_blocked_intent_without_guessing_identity()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            await EnableStoppedWorkAdviceAsync();
            var room = await WriteSettledRoomAsync(home, WorkflowOutcome.Succeeded, DecisionlessVerdict);
            await SeedAsync(home, WorkStage.Review, room);

            var facts = await new WorkItemAdvancer(
                new FakeGh(), (_, _) => Task.FromResult<string?>(Head)).AdvanceAsync(Now, Ct);

            Assert.Equal(QueueDecisionEntry.Failed, Assert.Single(facts).Decision);
            var blockedItem = await ReadBackAsync();
            Assert.NotNull(blockedItem.StoppedWorkJudgment);
            var judgment = blockedItem.StoppedWorkJudgment!;
            Assert.Equal(StoppedWorkJudgmentState.Blocked, judgment.State);
            Assert.Null(judgment.Key);
            Assert.Null(judgment.Holder);
            Assert.Null(judgment.AttemptId);
            Assert.Contains("conductor holder", judgment.Reason!, StringComparison.Ordinal);
            Assert.Contains("attempt identity", judgment.Reason!, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Enabling_advice_does_not_backfill_an_old_halted_needs_operator_row()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var room = await WriteSettledRoomAsync(home, WorkflowOutcome.Succeeded, DecisionlessVerdict);
            await SeedAsync(home, WorkStage.Review, room,
                attemptId: new FleetAttemptId("old-halted-attempt"));
            var firstAdvancer = new WorkItemAdvancer(
                new FakeGh(), (_, _) => Task.FromResult<string?>(Head));
            await firstAdvancer.AdvanceAsync(Now, Ct);
            Assert.True((await ReadBackAsync()).Halted);
            Assert.Null((await ReadBackAsync()).StoppedWorkJudgment);

            await EnableStoppedWorkAdviceAsync();
            await ConductorClaimStore.ClaimAsync(Identity, "conductor-fixture",
                cancellationToken: Ct);
            var secondGh = new FakeGh();
            Assert.Empty(await new WorkItemAdvancer(
                secondGh, (_, _) => Task.FromResult<string?>(Head)).AdvanceAsync(Now.AddMinutes(1), Ct));

            var retained = await ReadBackAsync();
            Assert.True(retained.Halted);
            Assert.Null(retained.StoppedWorkJudgment);
            Assert.Empty(secondGh.Calls);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    private const string DecisionlessVerdict = """
        {"reviewedRef":"PR #77","completion":"complete","summary":"I could not decide","findings":[
          {"claim":"maybe a problem","severity":"high","status":"confirmed"}]}
        """;

    private static async Task<string> WriteSettledRoomAsync(
        string home, string outcome, string? verdictJson)
    {
        var room = Path.Combine(home, "rooms", "queue-1934-lane-"
            + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(room);
        var outputs = new List<string>();
        if (verdictJson is not null)
        {
            var verdictPath = Path.Combine(room, "verdict.json");
            await File.WriteAllTextAsync(verdictPath, verdictJson, Ct);
            outputs.Add(verdictPath);
        }

        await TerminalSentinelWriter.WriteAsync(
            room, new WorkflowStatusView(outcome, [], outputs, null), Ct);
        return room;
    }

    private static async Task<QueueItem> SeedAsync(
        string home, WorkStage stage, string room, FleetAttemptId? attemptId = null)
    {
        var workspace = Path.Combine(home, "w1934");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(BatonPaths.QueueSpecsDirectory);
        await File.WriteAllTextAsync(
            BatonPaths.QueueSpecFile("1934-lane"), "# Implement #1934\n\nBuild the lifecycle.\n", Ct);

        var item = new QueueItem
        {
            Tag = "1934-lane",
            Role = WorkStages.RoleFor(stage),
            Workspace = workspace,
            SpecFile = BatonPaths.QueueSpecFile("1934-lane"),
            Issue = 1934,
            Branch = "1934-lane",
            Repository = Repository,
            Stage = stage,
            State = QueueItemState.Done,
            RoomDirectory = room,
            AttemptId = attemptId,
            Instructions = "Build the lifecycle.",
        };

        await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with { Items = [item] }, Ct);
        return item;
    }

    private static async Task<QueueItem> ReadBackAsync() =>
        (await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items.Single();

    private static string CreateTempHome()
    {
        var home = Path.Combine(Path.GetTempPath(), "baton_stopped_advice_"
            + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(home);
        return home;
    }

    private static async Task EnableStoppedWorkAdviceAsync() =>
        await DaemonSettingsStore.SaveAsync(new DaemonSettings
        {
            Queue = new QueueSettings
            {
                StoppedWorkAdvice = new Dictionary<string, JsonElement>
                {
                    [Repository] = JsonSerializer.SerializeToElement(true),
                },
            },
        }, BatonPaths.SettingsFile, Ct);

    private static ConductorObligationStore Store() => new(
        new FleetEventLog(
            BatonPaths.FleetEventsFile,
            BatonPaths.FleetEventsRolloverFile,
            100_000),
        BatonPaths.ConductorObligationsFile,
        () => Now);

    private static QueueSchedulerService Scheduler(
        ConductorObligationStore store,
        WorkItemAdvancer advancer,
        Func<ConductorObligation, StoppedWorkAdviceRequest, StoppedWorkAdviceContext,
            string, CancellationToken, Task<RetainedStoppedWorkAdviceResponse>> launch) =>
        new(
            (_, _) => Task.FromResult(new QueueLaunchOutcome(null)),
            _ => Task.FromResult(0d),
            () => 16d,
            () => Now,
            advancer: advancer,
            conductorObligations: store,
            stoppedWorkAdvice: launch);

    private static RetainedStoppedWorkAdviceResponse Response(
        ConductorObligation obligation, StoppedWorkAdviceRequest request) => new(
        new StoppedWorkAdviceDecision(
            obligation.ObligationId,
            request.Repository,
            request.Tag,
            request.AttemptId.Value,
            request.ContextSha256,
            StoppedWorkAdviceChoice.Hold,
            "The verdict is incomplete."),
        CodexReadinessDecisionAdapter.AdapterName,
        CodexReadinessDecisionAdapter.Model,
        CodexReadinessDecisionAdapter.Effort,
        Now,
        new StoppedWorkAdviceUsage(null, null, null));

    private static async Task WaitForAsync(Func<Task<bool>> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (await predicate().ConfigureAwait(false)) return;
            await Task.Delay(10, Ct).ConfigureAwait(false);
        }

        Assert.Fail("Timed out waiting for durable stopped-work advice state.");
    }

}
