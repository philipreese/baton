using System.Text.Json;
using Baton.Accounting;
using Baton.Cli;
using Baton.Cli.Daemon;
using Baton.Domain;
using Baton.Queue;
using Baton.Status;
using Baton.Tests.Shared;
using Baton.Vendors;
using Xunit;

namespace Baton.Cli.Tests.Daemon;

/// <summary>
/// Queue ticks, not direct advancer calls: terminal implementation -> durable attempt fact ->
/// draft PR -> launched review -> terminal BLOCK verdict -> one launched fix. Reconstructed
/// scheduler/advancer instances must not repeat either external action.
/// All GitHub and Git reads are fakes; no vendor, forge, or real worker process runs.
/// </summary>
public sealed class DraftPullRequestJourneyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
    private const string Repository = "github.com/example/project";
    private const string Branch = "2486-lane";
    private const string Head = "0123456789abcdef0123456789abcdef01234567";
    private const string BaseHead = "abcdef0123456789abcdef0123456789abcdef01";
    private static readonly RepositoryIdentity Identity = RepositoryIdentity.From("https://" + Repository, null)!;

    private sealed class FakeForge : IGhCliRunner
    {
        public int CreateCount { get; private set; }
        private string? _pullRequest;

        public Task<GhCliResult> RunAsync(string workspace, IReadOnlyList<string> args, CancellationToken token)
        {
            if (args is ["api", ..])
                return Task.FromResult(RequiredCheckFixture.Read(args, Repository, Head,
                    new GhCliResult(true, 0, """[{"name":"ci","bucket":"pass"}]""", "")));
            Assert.Equal("--repo", args[^2]);
            Assert.Equal(Repository, args[^1]);
            if (args is ["pr", "create", ..])
            {
                Assert.Contains("--draft", args);
                Assert.Contains(Branch, args);
                CreateCount++;
                _pullRequest = $$"""{"number":77,"state":"OPEN","isDraft":true,"headRefOid":"{{Head}}","headRefName":"{{Branch}}","baseRefName":"main","isCrossRepository":false,"statusCheckRollup":[]}""";
                return Task.FromResult(new GhCliResult(true, 0,
                    "https://github.com/example/project/pull/77", string.Empty));
            }

            if (args is ["pr", "checks", ..])
                return Task.FromResult(new GhCliResult(true, 0,
                    """[{"name":"ci","bucket":"pass","state":"SUCCESS"}]""", string.Empty));
            if (args is ["pr", "view", ..])
                return Task.FromResult(_pullRequest is null
                    ? new GhCliResult(true, 1, string.Empty, "not found")
                    : new GhCliResult(true, 0, _pullRequest, string.Empty));
            if (args is ["pr", "list", ..])
                return Task.FromResult(new GhCliResult(true, 0,
                    _pullRequest is null ? "[]" : "[" + _pullRequest + "]", string.Empty));
            throw new InvalidOperationException("Unexpected fake GitHub command: " + string.Join(' ', args));
        }
    }

    [Fact]
    public async Task Settled_implementation_creates_draft_review_block_launches_one_fix_and_restarts_without_duplicates()
    {
        var home = Path.Combine(Path.GetTempPath(), "baton_draft_journey_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var workspace = Path.Combine(home, "workspace");
            var room = Path.Combine(home, "rooms", "implement-2486");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(room);
            Directory.CreateDirectory(BatonPaths.QueueSpecsDirectory);
            var spec = BatonPaths.QueueSpecFile(Branch);
            await File.WriteAllTextAsync(spec, "# Implement #2486", Ct);
            await TerminalSentinelWriter.WriteAsync(room,
                new WorkflowStatusView(WorkflowOutcome.Succeeded,
                    [new WorkflowStatusStepView("implement", "Succeeded", "execution-2486")], [], null), Ct);
            ProjectCeilingStore.Set(workspace,
                new ProjectCeiling(ReadFiles: true, WriteFiles: true,
                    RunShellCommands: true, NetworkAccess: true), ProjectCeilingStore.DefaultPath);
            await DaemonSettingsStore.SaveAsync(new DaemonSettings
            {
                Queue = new QueueSettings
                {
                    DraftPullRequestHandoff = new Dictionary<string, JsonElement>
                    {
                        [Repository] = JsonSerializer.SerializeToElement(true),
                    },
                },
            }, BatonPaths.SettingsFile, Ct);

            var attempt = new FleetAttemptId("attempt-2486");
            var envelope = new QueueAttemptEnvelope(attempt, null, Branch, 2486, null,
                WorkStage.Implement, "implement", "codex", "test", "low", [], [], null,
                TaskRequirementAdmission.Admitted, room, BatonPaths.RecordKey(room), BaseHead, Now);
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
            {
                Items = [new QueueItem
                {
                    Tag = Branch,
                    Role = "implement",
                    Workspace = workspace,
                    WorkspaceOrigin = WorkspaceOrigins.IssueProvisioned,
                    SpecFile = spec,
                    Issue = 2486,
                    Branch = Branch,
                    Repository = Repository,
                    Stage = WorkStage.Implement,
                    State = QueueItemState.Launched,
                    RoomDirectory = room,
                    AttemptId = attempt,
                    AttemptEnvelope = envelope,
                    AttemptAdmissionFactDurable = true,
                    AttemptStartedFactDurable = true,
                    LastAdmission = new TaskRequirementAdmission([], [], TaskRequirementAdmission.Admitted),
                    AutomaticFixUsed = false,
                    Instructions = "Build the issue.",
                }],
            }, Ct);

            var forge = new FakeForge();
            var facts = new List<FleetEventDraft>();
            var launches = new List<QueueLaunchRequest>();
            Task<FleetEvent?> Append(FleetEventDraft draft, CancellationToken _)
            {
                facts.Add(draft);
                return Task.FromResult<FleetEvent?>(FleetEvent.From(facts.Count, draft));
            }
            GhCliResult Git(string program, string worktree, IReadOnlyList<string> args, CancellationToken _)
            {
                var offset = 0;
                while (offset + 1 < args.Count && args[offset] == "-c") offset += 2;
                var command = args.Skip(offset).ToArray();
                if (command is ["config", "--get", "remote.origin.url"])
                    return new GhCliResult(true, 0, "https://" + Repository, "");
                if (command is ["symbolic-ref", ..]) return new GhCliResult(true, 0, Branch, "");
                if (command is ["status", ..]) return new GhCliResult(true, 0, string.Empty, "");
                if (command is ["rev-parse", ..]) return new GhCliResult(true, 0, Head, "");
                if (command is ["ls-remote", ..]) return new GhCliResult(true, 0,
                    $"{Head}\trefs/heads/{Branch}\n{BaseHead}\trefs/heads/main", "");
                throw new InvalidOperationException("Unexpected fake Git command: " + string.Join(' ', args));
            }
            WorkItemAdvancer Advancer() => new(forge, (_, _) => Task.FromResult<string?>(Head),
                (_, _) => Task.FromResult<RepositoryIdentity?>(Identity),
                appendFleetEvent: Append,
                git: (program, worktree, args, token) => Task.FromResult(Git(program, worktree, args, token)));
            var advancer = Advancer();
            QueueSchedulerService Scheduler() => new(
                (request, _) =>
                {
                    launches.Add(request);
                    Directory.CreateDirectory(request.RoomDirectory);
                    return Task.FromResult(new QueueLaunchOutcome(request.RoomDirectory));
                },
                _ => Task.FromResult(0d), () => 16d, () => Now,
                advancer: advancer, appendFleetEvent: Append,
                workspaceHead: (_, _) => Task.FromResult<string?>(Head),
                workspaceLocks: _ => []);

            var scheduler = Scheduler();
            for (var turn = 0; turn < 3 && launches.Count == 0; turn++)
                await scheduler.TickOnceAsync(Ct);

            Assert.Contains(facts, fact => fact.Kind == FleetEventKind.AttemptSettled
                && fact.AttemptId == attempt && fact.Outcome == WorkflowOutcome.Succeeded);
            Assert.Equal(1, forge.CreateCount);
            var review = Assert.Single(launches);
            Assert.Equal(WorkStage.Review, review.Item.Stage);
            Assert.Equal(77, review.Item.PullRequest);
            var current = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(WorkStage.Review, current.Stage);
            Assert.Equal(QueueItemState.Launched, current.State);
            Assert.Equal(Head, current.DraftPullRequestCreateMarker?.HeadSha);

            // Replay the persisted launched review through fresh service objects. No terminal
            // evidence means there is no new authorized action yet.
            advancer = Advancer();
            var restarted = Scheduler();
            await restarted.TickOnceAsync(Ct);
            Assert.Equal(1, forge.CreateCount);
            Assert.Single(launches);

            // The fake review worker now publishes the engine-owned verdict output and terminal
            // sentinel. This is not a forged queue transition: the scheduler must settle its own
            // review attempt, let WorkItemLifecycle interpret BLOCK, then launch the fix.
            var verdict = Path.Combine(review.RoomDirectory, "verdict.json");
            await File.WriteAllTextAsync(verdict, $$"""{"reviewedRef":"{{Head}}","completion":"complete","decision":"block","summary":"one blocker","findings":[{"claim":"guard is bypassed","severity":"medium","status":"confirmed","anchor":{"file":"src/Baton/Queue/QueueScheduler.cs","line":62},"detail":"the early return skips it"}]}""", Ct);
            await TerminalSentinelWriter.WriteAsync(review.RoomDirectory,
                new WorkflowStatusView(WorkflowOutcome.Succeeded,
                    [new WorkflowStatusStepView("review", "Succeeded", "execution-review-2486")],
                    [verdict], null), Ct);
            var reviewAttempt = current.AttemptId;
            for (var turn = 0; turn < 3 && launches.Count == 1; turn++)
                await restarted.TickOnceAsync(Ct);

            Assert.Contains(facts, fact => fact.Kind == FleetEventKind.AttemptSettled
                && fact.AttemptId == reviewAttempt && fact.Outcome == WorkflowOutcome.Succeeded);
            Assert.Equal(2, launches.Count);
            var fix = launches[1];
            Assert.Equal(WorkStage.Fix, fix.Item.Stage);
            Assert.Equal("implement", fix.Item.Role);
            Assert.Equal(77, fix.Item.PullRequest);
            current = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(WorkStage.Fix, current.Stage);
            Assert.Equal(QueueItemState.Launched, current.State);
            Assert.True(current.AutomaticFixUsed);
            Assert.Equal(verdict, current.LastVerdict);
            Assert.Contains("guard is bypassed", await File.ReadAllTextAsync(current.SpecFile, Ct));

            advancer = Advancer();
            await Scheduler().TickOnceAsync(Ct);
            Assert.Equal(1, forge.CreateCount);
            Assert.Equal(2, launches.Count);
        }
        finally
        {
            scope.Dispose();
            DirectoryCleanup.DeleteRecursively(home);
        }
    }
}
