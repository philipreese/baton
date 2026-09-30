using System.Text.Json;
using Baton.Accounting;
using Baton.Cli;
using Baton.Cli.Daemon;
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
        public int ReadyCalls { get; private set; }

        public Task<GhCliResult> RunAsync(string workspace, IReadOnlyList<string> args, CancellationToken token)
        {
            Assert.Equal("--repo", args[^2]);
            Assert.Equal(Repository, args[^1]);
            if (args is ["pr", "ready", ..])
            {
                Draft = false;
                ReadyCalls++;
                return Task.FromResult(new GhCliResult(true, 0, string.Empty, string.Empty));
            }
            if (args is ["pr", "checks", ..])
                return Task.FromResult(new GhCliResult(true, 0,
                    """[{"name":"ci","bucket":"pass","state":"SUCCESS"}]""", string.Empty));
            var pr = $$"""{"number":77,"state":"OPEN","isDraft":{{Draft.ToString().ToLowerInvariant()}},"headRefOid":"{{Head}}","headRefName":"44-lane","baseRefName":"main","isCrossRepository":false,"statusCheckRollup":[{"name":"ci","conclusion":"SUCCESS","status":"COMPLETED"}]}""";
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
                new TaskSizeDeclaration(DeclaredTaskSize.Large, "multiple lifecycle seams"), brief),
                TextWriter.Null, Resolve, Provision, Ct);
            var accepted = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(TaskPreparationState.Prepared, accepted.IssuePreparation!.State);
            Assert.Equal("recorded-conductor", accepted.OwnedTask!.ConductorHolder);

            var forge = new Forge();
            var head = HeadA;
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
                git: (program, worktree, args, token) => Task.FromResult(Git(program, worktree, args, token)));
            QueueSchedulerService Scheduler() => new(
                (request, _) =>
                {
                    launches.Add(request);
                    Directory.CreateDirectory(request.RoomDirectory);
                    return Task.FromResult(new QueueLaunchOutcome(request.RoomDirectory));
                },
                _ => Task.FromResult(0d), () => 16d, () => Now,
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
            forge.HasPullRequest = true; // worker-created PR, no draft-create opt-in
            await Settle(launches[0]);
            await TickUntilLaunchCount(2);
            Assert.Equal(WorkStage.Review, launches[1].Item.Stage);
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
            Assert.Equal(4, launches.Count);
            await Scheduler().TickOnceAsync(Ct);
            Assert.Equal(4, launches.Count);
            Assert.Equal(ready.OwnedTask.Ready?.Id,
                Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items).OwnedTask?.Ready?.Id);
            Assert.True(forge.ReadyCalls <= 1);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }
}
