using System.Text.Json;
using Baton.Accounting;
using Baton.Cli.Tests.TestSupport;
using Baton.Cli.Daemon;
using Baton.Conductor;
using Baton.Domain;
using Baton.Queue;
using Baton.Status;
using Baton.Store;
using Baton.Tests.Shared;
using Baton.Vendors;

namespace Baton.Cli.Tests;

public sealed class TaskCommandTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Parser_requires_one_bounded_issue_task_and_accepts_status_json()
    {
        var submit = TaskOptionsParser.Parse(["submit", "--issue", "42", "--project", "C:/repo",
            "--declared-size", "unknown", "--size-rationale", "scope has not been measured"]);
        Assert.Equal(TaskVerb.Submit, submit.Verb);
        Assert.Equal(DeclaredTaskSize.Unknown, submit.Size!.Value.Size);
        Assert.Equal("scope has not been measured", submit.Size.Value.Rationale);
        Assert.True(TaskOptionsParser.Parse(["status", "task-id", "--json"]).Json);
        Assert.Throws<CliArgumentException>(() => TaskOptionsParser.Parse(["submit", "--issue", "42",
            "--project", "C:/repo", "--declared-size", "small"]));
    }

    [Fact]
    public async Task Identical_concurrent_submissions_reserve_once_before_provisioning_and_replay_keeps_snapshot()
    {
        var home = Path.Combine(Path.GetTempPath(), "baton-task-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var project = Path.Combine(home, "source");
            var workspace = Path.Combine(home, "w42");
            var spec = Path.Combine(home, "brief.md");
            Directory.CreateDirectory(project);
            await File.WriteAllTextAsync(spec, "first immutable brief", Ct);
            var repository = RepositoryIdentity.From("https://github.com/Owner/Repo.git", null)!;
            await ConductorClaimStore.ClaimAsync(repository, "conductor-one", home, cancellationToken: Ct);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var provisions = 0;
            async Task<IssueWorktreeProvisioner.ProvisionedIssueWorktree> Provision(
                int issueNumber, string source, string? root, string repo, bool lifecycle,
                TextWriter writer, CancellationToken token)
            {
                Interlocked.Increment(ref provisions);
                entered.SetResult();
                await release.Task.WaitAsync(token);
                Directory.CreateDirectory(workspace);
                ProjectCeilingStore.Set(workspace, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);
                return new(workspace, "42-lane");
            }
            Task<RepositoryIdentity?> Resolve(string path, CancellationToken token) => Task.FromResult<RepositoryIdentity?>(repository);
            var options = new TaskOptions(TaskVerb.Submit, 42, project,
                new TaskSizeDeclaration(DeclaredTaskSize.Small, "one acceptance cluster"), spec);
            var firstOutput = new StringWriter();
            var first = TaskCommand.ExecuteAsync(options, firstOutput, Resolve, Provision, Ct);
            await entered.Task.WaitAsync(Ct);

            var secondOutput = new StringWriter();
            Assert.Equal(0, await TaskCommand.ExecuteAsync(options, secondOutput, Resolve, Provision, Ct));
            Assert.Equal(1, provisions);
            var preparing = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(TaskPreparationState.Preparing, preparing.IssuePreparation!.State);
            Assert.False(File.Exists(BatonPaths.QueueSpecFile(preparing.Tag)));
            Assert.Contains("preparing", secondOutput.ToString(), StringComparison.Ordinal);

            release.SetResult();
            Assert.Equal(0, await first);
            var prepared = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(TaskPreparationState.Prepared, prepared.IssuePreparation!.State);
            Assert.Equal("first immutable brief", prepared.Instructions);
            await File.WriteAllTextAsync(spec, "changed explicit brief", Ct);
            var conflict = await Assert.ThrowsAsync<CliArgumentException>(() =>
                TaskCommand.ExecuteAsync(options, TextWriter.Null, Resolve, Provision, Ct));
            Assert.Contains(prepared.OwnedTask!.Id, conflict.Message, StringComparison.Ordinal);
            Assert.Equal(1, provisions);
            Assert.Equal("first immutable brief", Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items).Instructions);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Missing_claim_refuses_before_preparation_and_held_task_is_queued_with_owner()
    {
        var home = Path.Combine(Path.GetTempPath(), "baton-task-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var project = Path.Combine(home, "source");
            var workspace = Path.Combine(home, "w43");
            var spec = Path.Combine(home, "brief.md");
            Directory.CreateDirectory(project);
            await File.WriteAllTextAsync(spec, "brief", Ct);
            var repository = RepositoryIdentity.From("https://github.com/Owner/Repo.git", null)!;
            Task<RepositoryIdentity?> Resolve(string path, CancellationToken token) => Task.FromResult<RepositoryIdentity?>(repository);
            Task<IssueWorktreeProvisioner.ProvisionedIssueWorktree> Provision(
                int issueNumber, string source, string? root, string repo, bool lifecycle,
                TextWriter writer, CancellationToken token)
            {
                Directory.CreateDirectory(workspace);
                ProjectCeilingStore.Set(workspace, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);
                return Task.FromResult(new IssueWorktreeProvisioner.ProvisionedIssueWorktree(workspace, "43-lane"));
            }
            var options = new TaskOptions(TaskVerb.Submit, 43, project,
                new TaskSizeDeclaration(DeclaredTaskSize.Medium, "one durable seam"), spec);
            await Assert.ThrowsAsync<CliArgumentException>(() =>
                TaskCommand.ExecuteAsync(options, TextWriter.Null, Resolve, Provision, Ct));
            Assert.False(File.Exists(BatonPaths.QueueFile));

            await ConductorClaimStore.ClaimAsync(repository, "conductor-one", home, cancellationToken: Ct);
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with { Held = true }, Ct);
            var output = new StringWriter();
            Assert.Equal(0, await TaskCommand.ExecuteAsync(options, output, Resolve, Provision, Ct));
            Assert.Contains("queued (queue-held)", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("conductor-one", output.ToString(), StringComparison.Ordinal);
            var status = new StringWriter();
            var id = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items).OwnedTask!.Id;
            await TaskCommand.ExecuteAsync(new TaskOptions(TaskVerb.Status, Id: id, Json: true), status,
                Resolve, Provision, Ct);
            using var json = JsonDocument.Parse(status.ToString());
            Assert.Equal("queued", json.RootElement.GetProperty("state").GetString());
            Assert.Equal("queue-held", json.RootElement.GetProperty("reason").GetString());
            Assert.Equal("conductor-one", json.RootElement.GetProperty("conductorHolder").GetString());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Abandoned_reservation_is_blocked_without_reprovision_or_worker_launch()
    {
        var home = Path.Combine(Path.GetTempPath(), "baton-task-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var id = TaskCommand.TaskId("github.com/example/repo", 45);
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
            {
                Items = [new QueueItem
                {
                    Tag = id,
                    Role = "implement",
                    Workspace = string.Empty,
                    SpecFile = BatonPaths.QueueSpecFile(id),
                    Repository = "github.com/example/repo",
                    Issue = 45,
                    Stage = WorkStage.Implement,
                    OwnedTask = new OwnedTaskSubmission(id, "github.com/example/repo", 45,
                        "digest", "recorded-owner", DateTimeOffset.UtcNow),
                    IssuePreparation = new QueueIssuePreparation(TaskPreparationState.Preparing,
                        DateTimeOffset.UtcNow.AddMinutes(-1), ProcessId: int.MaxValue,
                        ProcessStartedAt: DateTimeOffset.UtcNow.AddMinutes(-1)),
                }],
            }, Ct);
            var launches = 0;
            var scheduler = new QueueSchedulerService(
                (request, token) =>
                {
                    Interlocked.Increment(ref launches);
                    return Task.FromResult(new QueueLaunchOutcome(request.RoomDirectory));
                },
                _ => Task.FromResult(0d), () => 16d, () => DateTimeOffset.UtcNow);
            await scheduler.TickOnceAsync(Ct);
            var row = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(TaskPreparationState.Blocked, row.IssuePreparation?.State);
            Assert.Equal("preparation-owner-exited-unverified", row.OwnedTask?.Blocked?.ReasonCode);
            Assert.Equal(0, launches);

            var status = new StringWriter();
            await TaskCommand.ExecuteAsync(new TaskOptions(TaskVerb.Status, Id: id, Json: true), status, Ct);
            using var json = JsonDocument.Parse(status.ToString());
            Assert.Equal("blocked", json.RootElement.GetProperty("state").GetString());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }
}
