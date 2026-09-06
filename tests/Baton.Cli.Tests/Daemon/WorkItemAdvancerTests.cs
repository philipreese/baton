using Baton.Cli.Daemon;
using Baton.Queue;
using Baton.Status;
using Baton.Tests.Shared;
using Xunit;

namespace Baton.Cli.Tests.Daemon;

/// <summary>
/// The four transitions #1934 slice 2 encodes, driven end to end against a FIXTURE ROOM — a real
/// <c>terminal.json</c> and a real <c>verdict.json</c> on disk — with <c>gh</c> and <c>git</c> as
/// delegates, so nothing here spawns a process or reaches the network.
/// </summary>
/// <remarks>
/// Isolated by <c>BatonEnvironmentSnapshot.BeginScope</c> the way <c>QueueCommandTests</c> is, and
/// torn down through <see cref="DirectoryCleanup"/>.
/// </remarks>
public sealed class WorkItemAdvancerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    private const string PushedSha = "aaaaaaaabbbbbbbbccccccccdddddddd";

    private sealed class FakeGh(string stdout, int exitCode = 0) : IGhCliRunner
    {
        public Task<GhCliResult> RunAsync(
            string workingDirectory, IReadOnlyList<string> args, CancellationToken cancellationToken) =>
            Task.FromResult(new GhCliResult(Started: true, exitCode, stdout, string.Empty));
    }

    private static string PrJson(int number, string headSha) =>
        $$"""{"number":{{number}},"headRefOid":"{{headSha}}","mergeStateStatus":"CLEAN"}""";

    private const string BlockingVerdict = """
        {"reviewedRef":"PR #77","summary":"one blocker","findings":[
          {"claim":"the guard is never reached","severity":"high","status":"confirmed",
           "anchor":{"file":"src/Baton/Queue/QueueScheduler.cs","line":62},"detail":"Decide returns first"}]}
        """;

    private const string ApprovingVerdict = """
        {"reviewedRef":"PR #77","summary":"nothing blocking","findings":[
          {"claim":"a nit","severity":"low","status":"confirmed"}]}
        """;

    /// <summary>Writes a settled room: the sentinel plus, when asked, the verdict the sentinel's own
    /// <c>outputs</c> point at — the same path <c>WatchFireService</c> reads one from.</summary>
    private static async Task<string> WriteSettledRoomAsync(string home, string outcome, string? verdictJson)
    {
        var room = Path.Combine(home, "rooms", "queue-1934-lane-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(room);

        var outputs = new List<string>();
        if (verdictJson is not null)
        {
            var verdictPath = Path.Combine(room, "verdict.json");
            await File.WriteAllTextAsync(verdictPath, verdictJson, Ct);
            outputs.Add(verdictPath);
        }

        await TerminalSentinelWriter.WriteAsync(room, new WorkflowStatusView(outcome, [], outputs, null), Ct);
        return room;
    }

    private static async Task<QueueItem> SeedAsync(
        string home, WorkStage stage, string room, QueueItemState state = QueueItemState.Done, int round = 0)
    {
        var workspace = Path.Combine(home, "w1934");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(BatonPaths.QueueSpecsDirectory);
        await File.WriteAllTextAsync(
            BatonPaths.QueueSpecFile("1934-lane"),
            "# Implement #1934\n\n## Do\n\nBuild the lifecycle.\n\n## Standing rules\n\nbuildlock.\n", Ct);

        var item = new QueueItem
        {
            Tag = "1934-lane",
            Role = WorkStages.RoleFor(stage),
            Workspace = workspace,
            SpecFile = BatonPaths.QueueSpecFile("1934-lane"),
            Issue = 1934,
            Branch = "1934-lane",
            Stage = stage,
            Round = round,
            State = state,
            RoomDirectory = room,
        };

        await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with { Items = [item] }, Ct);
        return item;
    }

    private static async Task<QueueItem> ReadBackAsync() =>
        (await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items.Single();

    private static string CreateTempHome()
    {
        var home = Path.Combine(Path.GetTempPath(), "baton_advancer_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(home);
        return home;
    }

    [Fact]
    public async Task A_succeeded_implement_lane_with_an_open_pr_is_queued_for_review()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var room = await WriteSettledRoomAsync(home, WorkflowOutcome.Succeeded, verdictJson: null);
            await SeedAsync(home, WorkStage.Implement, room);

            var facts = await new WorkItemAdvancer(new FakeGh(PrJson(77, PushedSha)), (_, _) => Task.FromResult<string?>(PushedSha))
                .AdvanceAsync(Now, Ct);

            var item = await ReadBackAsync();
            Assert.Equal(WorkStage.Review, item.Stage);
            Assert.Equal("review", item.Role);
            Assert.Equal(QueueItemState.Queued, item.State);
            Assert.Equal(77, item.PullRequest);
            Assert.Null(item.RoomDirectory);

            var fact = Assert.Single(facts);
            Assert.Equal(QueueDecisionEntry.Advanced, fact.Decision);
            Assert.Contains("implement → review", fact.Reason!, StringComparison.Ordinal);
            Assert.Equal(room, fact.Room);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task A_blocking_verdict_queues_a_fix_round_whose_brief_carries_the_findings_and_no_room_path()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var room = await WriteSettledRoomAsync(home, WorkflowOutcome.Succeeded, BlockingVerdict);
            await SeedAsync(home, WorkStage.Review, room);

            var facts = await new WorkItemAdvancer(new FakeGh(PrJson(77, PushedSha)), (_, _) => Task.FromResult<string?>(PushedSha))
                .AdvanceAsync(Now, Ct);

            var item = await ReadBackAsync();
            Assert.Equal(WorkStage.Fix, item.Stage);
            Assert.Equal("implement", item.Role);
            Assert.Equal(1, item.Round);
            Assert.Equal(Path.Combine(room, "verdict.json"), item.LastVerdict);

            var brief = await File.ReadAllTextAsync(item.SpecFile, Ct);
            Assert.Contains("Fix round for PR #77", brief, StringComparison.Ordinal);
            Assert.Contains("the guard is never reached", brief, StringComparison.Ordinal);
            Assert.Contains("Decide returns first", brief, StringComparison.Ordinal);

            // The findings travel as text; the room they came from must not (spec/baton.md §13). The
            // room path is recorded on the ITEM instead, asserted above — which is also the control
            // that this assertion is not passing because the room path is nowhere at all.
            Assert.DoesNotContain(room, brief, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("rooms", brief, StringComparison.OrdinalIgnoreCase);

            Assert.Contains("review → fix", Assert.Single(facts).Reason!, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task An_approving_verdict_makes_the_item_ready_and_it_is_never_dispatched_again()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var room = await WriteSettledRoomAsync(home, WorkflowOutcome.Succeeded, ApprovingVerdict);
            await SeedAsync(home, WorkStage.Review, room);
            var advancer = new WorkItemAdvancer(
                new FakeGh(PrJson(77, PushedSha)), (_, _) => Task.FromResult<string?>(PushedSha));

            var facts = await advancer.AdvanceAsync(Now, Ct);

            var item = await ReadBackAsync();
            Assert.Equal(WorkStage.Ready, item.Stage);
            Assert.Contains("approved", Assert.Single(facts).Reason!, StringComparison.Ordinal);

            // The scheduler is what refuses to launch it (QueueSchedulerTests pins that arm); what this
            // arm pins is the other half — the advancer never moves it on, so a ready item cannot walk
            // itself into another round on the next tick.
            var again = await advancer.AdvanceAsync(Now.AddMinutes(1), Ct);
            Assert.Empty(again);
            Assert.Equal(WorkStage.Ready, (await ReadBackAsync()).Stage);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task A_stalled_lane_with_its_work_pushed_is_re_reviewed_and_an_unpushed_one_continues()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var room = await WriteSettledRoomAsync(home, WorkflowOutcome.Failed, verdictJson: null);
            await SeedAsync(home, WorkStage.Implement, room, QueueItemState.Failed);

            // Pushed: the workspace head IS the PR's head.
            var pushedFacts = await new WorkItemAdvancer(
                new FakeGh(PrJson(77, PushedSha)), (_, _) => Task.FromResult<string?>(PushedSha)).AdvanceAsync(Now, Ct);

            Assert.Equal(WorkStage.ReReview, (await ReadBackAsync()).Stage);
            Assert.Contains("re-review", Assert.Single(pushedFacts).Reason!, StringComparison.Ordinal);

            // Unpushed: the ONE input that differs is the workspace head, which is what "the commit
            // never reached the PR" means.
            await SeedAsync(home, WorkStage.Implement, room, QueueItemState.Failed);
            var unpushedFacts = await new WorkItemAdvancer(
                new FakeGh(PrJson(77, PushedSha)), (_, _) => Task.FromResult<string?>("ffff0000ffff0000ffff0000ffff0000"))
                .AdvanceAsync(Now, Ct);

            var item = await ReadBackAsync();
            Assert.Equal(WorkStage.Continue, item.Stage);
            Assert.Contains("continue", Assert.Single(unpushedFacts).Reason!, StringComparison.Ordinal);

            var brief = await File.ReadAllTextAsync(item.SpecFile, Ct);
            Assert.Contains("Continue the work", brief, StringComparison.Ordinal);
            Assert.Contains("Build the lifecycle.", brief, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task A_stage_less_dispatch_request_is_left_exactly_as_it_was()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var room = await WriteSettledRoomAsync(home, WorkflowOutcome.Succeeded, verdictJson: null);
            var seeded = await SeedAsync(home, WorkStage.Implement, room);
            await QueueStore.MutateAsync(
                BatonPaths.QueueFile, s => s with { Items = [seeded with { Stage = null }] }, Ct);

            var facts = await new WorkItemAdvancer(new FakeGh(PrJson(77, PushedSha)), (_, _) => Task.FromResult<string?>(PushedSha))
                .AdvanceAsync(Now, Ct);

            var item = await ReadBackAsync();
            Assert.Empty(facts);
            Assert.Null(item.Stage);
            Assert.Equal(QueueItemState.Done, item.State);
            Assert.Equal(room, item.RoomDirectory);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }
}
