using System.Text.Json;
using Baton.Accounting;
using Baton.Cli;
using Baton.Cli.Daemon;
using Baton.Cli.Tests.TestSupport;
using Baton.Conductor;
using Baton.Domain;
using Baton.Queue;
using Baton.Status;
using Baton.Tests.Shared;
using Baton.Vendors;

namespace Baton.Cli.Tests.Daemon;

/// <summary>Submission enters the ordinary scheduler/advancer. Every worker and forge effect is injected.</summary>
public sealed class OwnedTaskJourneyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 18, 0, 0, TimeSpan.Zero);
    private const string Repository = "github.com/example/project";
    private const string HeadA = "0123456789abcdef0123456789abcdef01234567";
    private const string HeadB = "abcdef0123456789abcdef0123456789abcdef01";
    private static readonly RepositoryIdentity Identity = RepositoryIdentity.From("https://" + Repository, null)!;

    private sealed class Forge : IGhCliRunner
    {
        public string Head { get; set; } = HeadA;
        public bool HasPullRequest { get; set; }
        public bool Draft { get; set; } = true;
        public string CheckBucket { get; set; } = "pass";
        public bool OptionalCheckFailing { get; set; }
        public bool FailChecks { get; set; }
        public int ReadyCalls { get; private set; }

        public Task<GhCliResult> RunAsync(string workspace, IReadOnlyList<string> args, CancellationToken token)
        {
            if (args is ["api", ..])
                return Task.FromResult(RequiredCheckFixture.Read(args, Repository, Head,
                    FailChecks ? new GhCliResult(true, 1, "", "transient required-check lookup failure")
                    : new GhCliResult(true, 0, $$"""[{"name":"ci","bucket":"{{CheckBucket}}"}]""", "")));
            Assert.Equal("--repo", args[^2]);
            Assert.Equal(Repository, args[^1]);
            if (args is ["pr", "ready", ..])
            {
                Draft = args.Contains("--undo", StringComparer.Ordinal);
                if (!Draft) ReadyCalls++;
                return Task.FromResult(new GhCliResult(true, 0, string.Empty, string.Empty));
            }
            if (args is ["pr", "checks", ..])
            {
                if (FailChecks)
                    return Task.FromResult(new GhCliResult(true, 1, string.Empty, "transient required-check lookup failure"));
                return Task.FromResult(new GhCliResult(true, 0,
                    $$"""[{"name":"ci","bucket":"{{CheckBucket}}","state":"SUCCESS"}]""", string.Empty));
            }
            var rollup = OptionalCheckFailing
                ? """[{"name":"ci","conclusion":"SUCCESS","status":"COMPLETED"},{"name":"optional","conclusion":"FAILURE","status":"COMPLETED"}]"""
                : """[{"name":"ci","conclusion":"SUCCESS","status":"COMPLETED"}]""";
            var pr = $$"""{"number":77,"state":"OPEN","isDraft":{{Draft.ToString().ToLowerInvariant()}},"headRefOid":"{{Head}}","headRefName":"44-lane","baseRefName":"main","isCrossRepository":false,"statusCheckRollup":{{rollup}}}""";
            if (args is ["pr", "view", ..])
                return Task.FromResult(HasPullRequest
                    ? new GhCliResult(true, 0, pr, string.Empty)
                    : new GhCliResult(true, 1, string.Empty, "not found"));
            if (args is ["pr", "list", ..])
                return Task.FromResult(new GhCliResult(true, 0, HasPullRequest ? "[" + pr + "]" : "[]", string.Empty));
            throw new InvalidOperationException("Unexpected forge call: " + string.Join(' ', args));
        }
    }

    [Fact]
    public async Task Submit_then_implement_review_block_fix_rereview_ready_survives_restart_without_relaunch()
    {
        var home = Path.Combine(Path.GetTempPath(), "baton_owned_journey_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var project = Path.Combine(home, "source");
            var workspace = Path.Combine(home, "w44");
            var brief = Path.Combine(home, "brief.md");
            Directory.CreateDirectory(project);
            await File.WriteAllTextAsync(brief, "Implement the issue.", Ct);
            await ConductorClaimStore.ClaimAsync(Identity, "recorded-conductor", home, cancellationToken: Ct);
            Task<RepositoryIdentity?> Resolve(string path, CancellationToken token) =>
                Task.FromResult<RepositoryIdentity?>(Identity);
            Task<IssueWorktreeProvisioner.ProvisionedIssueWorktree> Provision(
                int issue, string source, string? root, string repository, bool lifecycle,
                TextWriter writer, CancellationToken token)
            {
                Directory.CreateDirectory(workspace);
                ProjectCeilingStore.Set(workspace, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);
                return Task.FromResult(new IssueWorktreeProvisioner.ProvisionedIssueWorktree(workspace, "44-lane"));
            }

            await TaskCommand.ExecuteAsync(new TaskOptions(TaskVerb.Submit, 44, project,
                new TaskSizeDeclaration(DeclaredTaskSize.Large, "multiple lifecycle seams"), brief,
                ScopeClass: "ENGINE"),
                TextWriter.Null, Resolve, Provision, Ct, IssuePreparationRunner.NoCollisions);
            var accepted = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(TaskPreparationState.Prepared, accepted.IssuePreparation!.State);
            Assert.Equal("recorded-conductor", accepted.OwnedTask!.ConductorHolder);
            Assert.Equal("engine", accepted.ScopeClass);

            var forge = new Forge();
            var head = HeadA;
            var now = Now;
            var launches = new List<QueueLaunchRequest>();
            var events = new List<FleetEventDraft>();
            Task<FleetEvent?> Append(FleetEventDraft draft, CancellationToken token)
            {
                events.Add(draft);
                return Task.FromResult<FleetEvent?>(FleetEvent.From(events.Count, draft));
            }
            GhCliResult Git(string program, string worktree, IReadOnlyList<string> args, CancellationToken token)
            {
                var offset = 0;
                while (offset + 1 < args.Count && args[offset] == "-c") offset += 2;
                var command = args.Skip(offset).ToArray();
                if (command is ["config", "--get", "remote.origin.url"])
                    return new GhCliResult(true, 0, "https://" + Repository, "");
                if (command is ["symbolic-ref", ..]) return new GhCliResult(true, 0, "44-lane", "");
                if (command is ["status", ..]) return new GhCliResult(true, 0, string.Empty, "");
                if (command is ["rev-parse", ..]) return new GhCliResult(true, 0, head, "");
                if (command is ["ls-remote", ..]) return new GhCliResult(true, 0,
                    $"{head}\trefs/heads/44-lane\n{HeadA}\trefs/heads/main", "");
                throw new InvalidOperationException("Unexpected Git call: " + string.Join(' ', args));
            }
            WorkItemAdvancer Advancer() => new(forge, (_, _) => Task.FromResult<string?>(head),
                (_, _) => Task.FromResult<RepositoryIdentity?>(Identity), appendFleetEvent: Append,
                requiredCheckClock: () => now,
                git: (program, worktree, args, token) => Task.FromResult(Git(program, worktree, args, token)));
            QueueSchedulerService Scheduler() => new(
                (request, _) =>
                {
                    launches.Add(request);
                    Directory.CreateDirectory(request.RoomDirectory);
                    return Task.FromResult(new QueueLaunchOutcome(request.RoomDirectory));
                },
                _ => Task.FromResult(0d), () => 16d, () => now,
                advancer: Advancer(), appendFleetEvent: Append,
                workspaceHead: (_, _) => Task.FromResult<string?>(head), workspaceLocks: _ => []);

            async Task TickUntilLaunchCount(int count)
            {
                for (var i = 0; i < 5 && launches.Count < count; i++)
                    await Scheduler().TickOnceAsync(Ct);
                Assert.Equal(count, launches.Count);
            }
            async Task Settle(QueueLaunchRequest launch, string? decision = null)
            {
                var verdict = decision is null ? null : Path.Combine(launch.RoomDirectory, "verdict.json");
                if (verdict is not null)
                    await File.WriteAllTextAsync(verdict,
                        $$"""{"reviewedRef":"{{head}}","completion":"complete","decision":"{{decision}}","findings":[]}""", Ct);
                await TerminalSentinelWriter.WriteAsync(launch.RoomDirectory,
                    new WorkflowStatusView(WorkflowOutcome.Succeeded,
                        [new WorkflowStatusStepView(launch.Item.Role, "Succeeded", "synthetic-execution")],
                        verdict is null ? [] : [verdict], null), Ct);
            }

            await TickUntilLaunchCount(1);
            Assert.Equal(WorkStage.Implement, launches[0].Item.Stage);
            Assert.Equal("engine", launches[0].Tier.TierKey);
            forge.HasPullRequest = true; // worker-created PR, no draft-create opt-in
            await Settle(launches[0]);
            await TickUntilLaunchCount(2);
            Assert.Equal(WorkStage.Review, launches[1].Item.Stage);
            Assert.Equal("review-engine", launches[1].Tier.TierKey);
            await Settle(launches[1], "block");
            await TickUntilLaunchCount(3);
            Assert.Equal(WorkStage.Fix, launches[2].Item.Stage);
            head = HeadB;
            forge.Head = HeadB;
            await Settle(launches[2]);
            await TickUntilLaunchCount(4);
            Assert.Equal(WorkStage.ReReview, launches[3].Item.Stage);
            await Settle(launches[3], "approve");
            for (var i = 0; i < 5; i++) await Scheduler().TickOnceAsync(Ct);

            var ready = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(WorkStage.Ready, ready.Stage);
            Assert.Equal(HeadB, ready.OwnedTask!.Ready?.HeadSha);
            Assert.Equal(launches[3].Item.AttemptId?.Value, ready.OwnedTask.Ready?.ReviewAttemptId);
            Assert.NotNull(ready.OwnedTask.Ready?.RequiredEvidence);
            Assert.Equal(Repository, ready.OwnedTask.Ready!.RequiredEvidence!.Repository);
            Assert.Equal(HeadB, ready.OwnedTask.Ready.RequiredEvidence.HeadSha);
            Assert.True(ready.OwnedTask.Ready.ReadyObservedAt >= ready.OwnedTask.Ready.ChecksObservedAt);
            Assert.Equal(4, launches.Count);
            await Scheduler().TickOnceAsync(Ct);
            Assert.Equal(4, launches.Count);
            Assert.Equal(ready.OwnedTask.Ready?.Id,
                Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items).OwnedTask?.Ready?.Id);
            Assert.True(forge.ReadyCalls <= 1);

            forge.CheckBucket = "pending";
            await Scheduler().TickOnceAsync(Ct);
            var regressed = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(WorkStage.Ready, regressed.Stage);
            Assert.NotNull(regressed.Error);
            var regressedStatus = new StringWriter();
            await TaskCommand.ExecuteAsync(new TaskOptions(TaskVerb.Status,
                Id: ready.OwnedTask!.Id, Json: true), regressedStatus, Ct);
            using var regressedJson = JsonDocument.Parse(regressedStatus.ToString());
            Assert.Equal("stale", regressedJson.RootElement.GetProperty("state").GetString());

            // Required CI recovers while an optional check fails. Aggregate display checks are
            // failing, but the ordinary advancer restores exact-head readiness and clears Error.
            forge.CheckBucket = "pass";
            forge.OptionalCheckFailing = true;
            await Scheduler().TickOnceAsync(Ct);
            var recovered = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Null(recovered.Error);
            Assert.Equal(PullRequestChecks.Failing, recovered.Checks);
            var recoveredStatus = new StringWriter();
            await TaskCommand.ExecuteAsync(new TaskOptions(TaskVerb.Status,
                Id: ready.OwnedTask!.Id, Json: true), recoveredStatus, Ct);
            using var recoveredJson = JsonDocument.Parse(recoveredStatus.ToString());
            Assert.Equal("ready-as-of", recoveredJson.RootElement.GetProperty("state").GetString());

            // Stable, non-draft Ready: a transient required-check read fails, then succeeds
            // while an optional check remains red. This refreshes aggregate Checks after the old
            // receipt without minting a new one; aggregate failure must not veto required CI.
            Assert.False(forge.Draft);
            var retainedReadyId = recovered.OwnedTask!.Ready!.Id;
            now = Now.AddMinutes(2);
            forge.FailChecks = true;
            await Scheduler().TickOnceAsync(Ct);
            Assert.NotNull(Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items).Error);
            now = Now.AddMinutes(3);
            forge.FailChecks = false;
            await Scheduler().TickOnceAsync(Ct);
            var stableRecovered = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Null(stableRecovered.Error);
            Assert.Equal(retainedReadyId, stableRecovered.OwnedTask?.Ready?.Id);
            Assert.True(stableRecovered.ChecksObservedAt > stableRecovered.OwnedTask?.Ready?.ReadyObservedAt);
            Assert.Equal(PullRequestChecks.Failing, stableRecovered.Checks);
            var stableStatus = new StringWriter();
            await TaskCommand.ExecuteAsync(new TaskOptions(TaskVerb.Status,
                Id: ready.OwnedTask!.Id, Json: true), stableStatus, Ct);
            using var stableJson = JsonDocument.Parse(stableStatus.ToString());
            Assert.Equal("ready-as-of", stableJson.RootElement.GetProperty("state").GetString());

            // A later independent PR observation cannot make the old exact-head receipt current.
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
            {
                PullRequestObservations = [new QueuePullRequestObservation(Repository, 77, "open",
                    HeadA, Now, Now, null)],
            }, Ct);
            var staleStatus = new StringWriter();
            await TaskCommand.ExecuteAsync(new TaskOptions(TaskVerb.Status,
                Id: ready.OwnedTask!.Id, Json: true), staleStatus, Ct);
            using var status = JsonDocument.Parse(staleStatus.ToString());
            Assert.Equal("stale", status.RootElement.GetProperty("state").GetString());
            Assert.Equal(ready.OwnedTask!.Ready!.Id,
                status.RootElement.GetProperty("ready").GetProperty("Id").GetString());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }
}
