using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
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
        Assert.Null(submit.Adapter);
        Assert.Null(submit.Model);
        Assert.Null(submit.Effort);
        Assert.True(TaskOptionsParser.Parse(["status", "task-id", "--json"]).Json);
        Assert.Throws<CliArgumentException>(() => TaskOptionsParser.Parse(["submit", "--issue", "42",
            "--project", "C:/repo", "--declared-size", "small"]));
    }

    [Fact]
    public void Parser_accepts_optional_worker_selection_axes_and_refuses_malformed_ones()
    {
        string[] baseArgs =
            ["submit", "--issue", "42", "--project", "C:/repo", "--declared-size", "small", "--size-rationale", "one cluster"];

        var withAll = TaskOptionsParser.Parse([.. baseArgs, "--adapter", "claude", "--model", "opus", "--effort", "high"]);
        Assert.Equal("claude", withAll.Adapter);
        Assert.Equal("opus", withAll.Model);
        Assert.Equal("high", withAll.Effort);

        var modelOnly = TaskOptionsParser.Parse([.. baseArgs, "--model", "opus"]);
        Assert.Null(modelOnly.Adapter);
        Assert.Equal("opus", modelOnly.Model);
        Assert.Null(modelOnly.Effort);

        Assert.Throws<CliArgumentException>(() =>
            TaskOptionsParser.Parse([.. baseArgs, "--adapter", "claude", "--adapter", "codex"]));
        Assert.Throws<CliArgumentException>(() => TaskOptionsParser.Parse([.. baseArgs, "--model"]));
        Assert.Throws<CliArgumentException>(() => TaskOptionsParser.Parse([.. baseArgs, "--effort", " "]));
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
            var first = TaskCommand.ExecuteAsync(options, firstOutput, Resolve, Provision, Ct, IssuePreparationRunner.NoCollisions);
            await entered.Task.WaitAsync(Ct);

            var secondOutput = new StringWriter();
            Assert.Equal(0, await TaskCommand.ExecuteAsync(options, secondOutput, Resolve, Provision, Ct, IssuePreparationRunner.NoCollisions));
            Assert.Equal(1, provisions);
            var preparing = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(TaskPreparationState.Preparing, preparing.IssuePreparation!.State);
            Assert.False(File.Exists(BatonPaths.QueueSpecFile(preparing.Tag)));
            Assert.Contains("preparing", secondOutput.ToString(), StringComparison.Ordinal);

            await File.WriteAllTextAsync(spec, "changed during preparation", Ct);
            release.SetResult();
            Assert.Equal(0, await first);
            var prepared = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(TaskPreparationState.Prepared, prepared.IssuePreparation!.State);
            Assert.Equal("first immutable brief", prepared.Instructions);
            var renderedBrief = await File.ReadAllTextAsync(prepared.SpecFile, Ct);
            Assert.Contains("first immutable brief", renderedBrief, StringComparison.Ordinal);
            Assert.DoesNotContain("changed during preparation", renderedBrief, StringComparison.Ordinal);
            await File.WriteAllTextAsync(spec, "changed explicit brief", Ct);
            var conflict = await Assert.ThrowsAsync<CliArgumentException>(() =>
                TaskCommand.ExecuteAsync(options, TextWriter.Null, Resolve, Provision, Ct, IssuePreparationRunner.NoCollisions));
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
    public async Task Cancellation_during_provisioning_cannot_be_resurrected_by_preparation_commit()
    {
        var home = Path.Combine(Path.GetTempPath(), "baton-task-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var project = Path.Combine(home, "source");
            var workspace = Path.Combine(home, "w47");
            var spec = Path.Combine(home, "brief.md");
            Directory.CreateDirectory(project);
            await File.WriteAllTextAsync(spec, "frozen", Ct);
            var repository = RepositoryIdentity.From("https://github.com/example/repo", null)!;
            await ConductorClaimStore.ClaimAsync(repository, "owner", home, cancellationToken: Ct);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<RepositoryIdentity?> Resolve(string path, CancellationToken token) =>
                Task.FromResult<RepositoryIdentity?>(repository);
            async Task<IssueWorktreeProvisioner.ProvisionedIssueWorktree> Provision(
                int issue, string source, string? root, string repo, bool lifecycle,
                TextWriter writer, CancellationToken token)
            {
                entered.SetResult();
                await release.Task.WaitAsync(token);
                Directory.CreateDirectory(workspace);
                ProjectCeilingStore.Set(workspace, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);
                return new(workspace, "47-lane");
            }
            var submit = TaskCommand.ExecuteAsync(new TaskOptions(TaskVerb.Submit, 47, project,
                new TaskSizeDeclaration(DeclaredTaskSize.Small, "one issue"), spec),
                TextWriter.Null, Resolve, Provision, Ct, IssuePreparationRunner.NoCollisions);
            await entered.Task.WaitAsync(Ct);
            var id = TaskCommand.TaskId(repository.Value, 47);
            await QueueCommand.ExecuteAsync(new QueueOptions(QueueVerb.Cancel, Tag: id), TextWriter.Null, Ct);
            release.SetResult();
            await Assert.ThrowsAsync<CliArgumentException>(() => submit);
            var retained = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(QueueItemState.Cancelled, retained.State);
            Assert.NotNull(retained.CancelledAt);
            Assert.Equal(id, retained.OwnedTask?.Id);
            Assert.False(File.Exists(BatonPaths.QueueSpecFile(id)));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Empty_explicit_spec_is_not_the_same_submission_as_no_spec()
    {
        var home = Path.Combine(Path.GetTempPath(), "baton-task-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var project = Path.Combine(home, "source");
            var workspace = Path.Combine(home, "w48");
            var spec = Path.Combine(home, "empty.md");
            Directory.CreateDirectory(project);
            await File.WriteAllTextAsync(spec, string.Empty, Ct);
            var repository = RepositoryIdentity.From("https://github.com/example/repo", null)!;
            await ConductorClaimStore.ClaimAsync(repository, "owner", home, cancellationToken: Ct);
            Task<RepositoryIdentity?> Resolve(string path, CancellationToken token) =>
                Task.FromResult<RepositoryIdentity?>(repository);
            Task<IssueWorktreeProvisioner.ProvisionedIssueWorktree> Provision(
                int issue, string source, string? root, string repo, bool lifecycle,
                TextWriter writer, CancellationToken token)
            {
                Directory.CreateDirectory(workspace);
                ProjectCeilingStore.Set(workspace, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);
                return Task.FromResult(new IssueWorktreeProvisioner.ProvisionedIssueWorktree(workspace, "48-lane"));
            }
            var size = new TaskSizeDeclaration(DeclaredTaskSize.Small, "one issue");
            Assert.Equal(0, await TaskCommand.ExecuteAsync(new TaskOptions(TaskVerb.Submit, 48,
                project, size, spec), TextWriter.Null, Resolve, Provision, Ct, IssuePreparationRunner.NoCollisions));
            var conflict = await Assert.ThrowsAsync<CliArgumentException>(() =>
                TaskCommand.ExecuteAsync(new TaskOptions(TaskVerb.Submit, 48,
                    project, size), TextWriter.Null, Resolve, Provision, Ct, IssuePreparationRunner.NoCollisions));
            Assert.Contains(TaskCommand.TaskId(repository.Value, 48), conflict.Message, StringComparison.Ordinal);
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
            Assert.Equal(0, await TaskCommand.ExecuteAsync(options, output, Resolve, Provision, Ct, IssuePreparationRunner.NoCollisions));
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
            Assert.Equal("recorded-holder-current", json.RootElement.GetProperty("ownership").GetString());
            await ConductorClaimStore.TakeoverAsync(repository, "conductor-two", "handoff", home,
                cancellationToken: Ct);
            var afterTakeover = new StringWriter();
            await TaskCommand.ExecuteAsync(new TaskOptions(TaskVerb.Status, Id: id, Json: true), afterTakeover,
                Resolve, Provision, Ct);
            using var changed = JsonDocument.Parse(afterTakeover.ToString());
            Assert.Equal("conductor-one", changed.RootElement.GetProperty("conductorHolder").GetString());
            Assert.Equal("conductor-two", changed.RootElement.GetProperty("currentConductorHolder").GetString());
            Assert.Equal("holder-changed", changed.RootElement.GetProperty("ownership").GetString());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Theory]
    [InlineData("stale", "unavailable", "heartbeat-stale", "daemon-unavailable")]
    [InlineData("future", "unavailable", "heartbeat-in-future", "daemon-unavailable")]
    [InlineData("mismatch", "unavailable", "process-identity-mismatch", "daemon-unavailable")]
    [InlineData("equivalent-offset", "recently-observed", null, "awaiting-daemon-decision")]
    [InlineData("missing", "unavailable", "heartbeat-unavailable", "daemon-unavailable")]
    [InlineData("malformed", "unavailable", "heartbeat-unavailable", "daemon-unavailable")]
    public async Task Status_rejects_stale_future_reused_and_legacy_heartbeat_evidence(
        string caseName, string expectedAvailability, string? expectedDaemonReason, string expectedReason)
    {
        var home = Path.Combine(Path.GetTempPath(), "baton-task-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var now = new DateTimeOffset(2026, 10, 2, 23, 0, 0, TimeSpan.Zero);
            var heartbeatAt = caseName switch
            {
                "stale" => now.AddSeconds(-90),
                "future" => now.AddSeconds(1),
                _ => now.AddSeconds(-10),
            };
            var recordedStart = now.AddMinutes(-1);
            var id = TaskCommand.TaskId("github.com/example/repo", 2582);
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
            {
                Items = [new QueueItem
                {
                    Tag = id,
                    Role = "implement",
                    Workspace = home,
                    SpecFile = BatonPaths.QueueSpecFile(id),
                    Repository = "github.com/example/repo",
                    Issue = 2582,
                    Stage = WorkStage.Implement,
                    State = QueueItemState.Queued,
                    OwnedTask = new OwnedTaskSubmission(id, "github.com/example/repo", 2582,
                        "digest", "conductor", now),
                }],
            }, Ct);
            Directory.CreateDirectory(Path.GetDirectoryName(BatonPaths.FleetHeartbeatFile)!);
            var heartbeat = caseName switch
            {
                "missing" => "{\"tickCompletedAt\":\"" + heartbeatAt.ToString("O") + "\"}",
                "malformed" => "{not-json",
                "equivalent-offset" => "{\"tickCompletedAt\":\"" + heartbeatAt.ToOffset(TimeSpan.FromHours(-4)).ToString("O")
                    + "\",\"identity\":{\"pid\":1234,\"processStartTime\":\""
                    + recordedStart.ToOffset(TimeSpan.FromHours(-4)).ToString("O") + "\"}}",
                _ => JsonSerializer.Serialize(new
                {
                    tickCompletedAt = heartbeatAt,
                    identity = new { pid = 1234, processStartTime = recordedStart },
                }),
            };
            await File.WriteAllTextAsync(BatonPaths.FleetHeartbeatFile, heartbeat, Ct);
            DateTimeOffset ProcessStart(int _) => caseName == "mismatch"
                ? recordedStart.AddSeconds(2)
                : recordedStart;
            Task<RepositoryIdentity?> Resolve(string path, CancellationToken token) =>
                Task.FromResult<RepositoryIdentity?>(RepositoryIdentity.From("https://github.com/example/repo", null));
            Task<IssueWorktreeProvisioner.ProvisionedIssueWorktree> Provision(
                int issue, string source, string? root, string repo, bool lifecycle,
                TextWriter writer, CancellationToken token) => throw new NotSupportedException();

            var output = new StringWriter();
            await TaskCommand.ExecuteAsync(new TaskOptions(TaskVerb.Status, Id: id, Json: true), output,
                Resolve, Provision, Ct, utcNow: () => now, processStartTimeAccessor: ProcessStart);
            using var json = JsonDocument.Parse(output.ToString());
            Assert.Equal(expectedReason, json.RootElement.GetProperty("reason").GetString());
            var daemon = json.RootElement.GetProperty("daemon");
            Assert.Equal(expectedAvailability, daemon.GetProperty("availability").GetString());
            if (expectedDaemonReason is null)
                Assert.Equal(JsonValueKind.Null, daemon.GetProperty("reason").ValueKind);
            else
                Assert.Equal(expectedDaemonReason, daemon.GetProperty("reason").GetString());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Theory]
    [InlineData(false, "unknown", "process-identity-unverifiable", "daemon-observation-unknown")]
    [InlineData(true, "recently-observed", null, "awaiting-daemon-decision")]
    public async Task Status_distinguishes_denied_process_identity_from_matching_birth(
        bool matchingBirth, string expectedAvailability, string? expectedDaemonReason, string? expectedReason)
    {
        var home = Path.Combine(Path.GetTempPath(), "baton-task-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var now = new DateTimeOffset(2026, 10, 2, 23, 0, 0, TimeSpan.Zero);
            var heartbeatAt = now.AddSeconds(-10);
            var id = TaskCommand.TaskId("github.com/example/repo", 2580);
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
            {
                Items = [new QueueItem
                {
                    Tag = id,
                    Role = "implement",
                    Workspace = home,
                    SpecFile = BatonPaths.QueueSpecFile(id),
                    Repository = "github.com/example/repo",
                    Issue = 2580,
                    Stage = WorkStage.Implement,
                    State = QueueItemState.Queued,
                    OwnedTask = new OwnedTaskSubmission(id, "github.com/example/repo", 2580,
                        "digest", "conductor", now),
                }],
            }, Ct);
            Directory.CreateDirectory(Path.GetDirectoryName(BatonPaths.FleetHeartbeatFile)!);
            await File.WriteAllTextAsync(BatonPaths.FleetHeartbeatFile, JsonSerializer.Serialize(new
            {
                tickCompletedAt = heartbeatAt,
                identity = new { pid = 1234, processStartTime = now.AddMinutes(-1) },
            }), Ct);
            var before = await File.ReadAllBytesAsync(BatonPaths.QueueFile, Ct);
            DateTimeOffset ProcessStart(int _) => matchingBirth
                ? now.AddMinutes(-1)
                : throw new Win32Exception(5);
            Task<RepositoryIdentity?> Resolve(string path, CancellationToken token) =>
                Task.FromResult<RepositoryIdentity?>(RepositoryIdentity.From("https://github.com/example/repo", null));
            Task<IssueWorktreeProvisioner.ProvisionedIssueWorktree> Provision(
                int _, string __, string? ___, string ____, bool _____, TextWriter ______, CancellationToken _______)
                => throw new NotSupportedException();

            var jsonOutput = new StringWriter();
            await TaskCommand.ExecuteAsync(new TaskOptions(TaskVerb.Status, Id: id, Json: true), jsonOutput,
                Resolve, Provision, Ct, utcNow: () => now, processStartTimeAccessor: ProcessStart);
            using var json = JsonDocument.Parse(jsonOutput.ToString());
            Assert.Equal(expectedReason, json.RootElement.GetProperty("reason").GetString());
            var daemon = json.RootElement.GetProperty("daemon");
            Assert.Equal(expectedAvailability, daemon.GetProperty("availability").GetString());
            if (expectedDaemonReason is null)
                Assert.Equal(JsonValueKind.Null, daemon.GetProperty("reason").ValueKind);
            else
                Assert.Equal(expectedDaemonReason, daemon.GetProperty("reason").GetString());
            Assert.Equal(heartbeatAt, daemon.GetProperty("observedAt").GetDateTimeOffset());
            Assert.Equal(10, daemon.GetProperty("ageSeconds").GetInt32());

            var textOutput = new StringWriter();
            await TaskCommand.ExecuteAsync(new TaskOptions(TaskVerb.Status, Id: id), textOutput,
                Resolve, Provision, Ct, utcNow: () => now, processStartTimeAccessor: ProcessStart);
            if (matchingBirth)
                Assert.Contains("daemon: recently-observed", textOutput.ToString(), StringComparison.Ordinal);
            else
            {
                Assert.Contains("daemon: unknown (reason process-identity-unverifiable)",
                    textOutput.ToString(), StringComparison.Ordinal);
                Assert.Contains("recorded heartbeat time", textOutput.ToString(), StringComparison.Ordinal);
            }
            Assert.Equal(before, await File.ReadAllBytesAsync(BatonPaths.QueueFile, Ct));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Theory]
    [InlineData(QueueItemState.Failed, "ownership refused", false, false, "blocked", "conductor-judgment")]
    [InlineData(QueueItemState.Failed, null, false, false, "blocked", "conductor-judgment")]
    [InlineData(QueueItemState.Failed, " ", false, false, "blocked", "conductor-judgment")]
    [InlineData(QueueItemState.Failed, "ownership refused", false, true, "blocked", "conductor-judgment")]
    [InlineData(QueueItemState.Failed, "ownership refused", true, false, "retired", "none")]
    [InlineData(QueueItemState.Cancelled, "cancelled", false, false, "cancelled", "none")]
    [InlineData(QueueItemState.Queued, null, false, false, "queued", "daemon-tick")]
    [InlineData(QueueItemState.Launched, null, false, false, "running", "daemon-tick")]
    [InlineData(QueueItemState.Queued, null, false, true, "ready-as-of", "none")]
    public async Task Status_distinguishes_failed_launches_without_mutating_retained_evidence(
        QueueItemState queueState, string? error, bool retired, bool oldReady,
        string expectedState, string expectedTrigger)
    {
        var home = Path.Combine(Path.GetTempPath(), "baton-task-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            const string repository = "github.com/example/repo";
            var id = TaskCommand.TaskId(repository, 49);
            var now = DateTimeOffset.UtcNow;
            var receipt = oldReady ? new TaskReadyReceipt("ready", id, repository, 49, 50,
                new string('a', 40), "review", new string('b', 64), "passing", "checks", now, now) : null;
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
            {
                Items = [new QueueItem
                {
                    Tag = id,
                    Role = "implement",
                    Workspace = home,
                    SpecFile = BatonPaths.QueueSpecFile(id),
                    Repository = repository,
                    Issue = 49,
                    Stage = oldReady ? WorkStage.Ready : WorkStage.Continue,
                    State = queueState,
                    Halted = false,
                    Error = error,
                    Retirement = retired ? new QueueRetirement(QueueRetirement.Operator, now, "retained") : null,
                    OwnedTask = new OwnedTaskSubmission(id, repository, 49, "digest", "recorded-owner", now, receipt),
                }],
            }, Ct);
            var before = await File.ReadAllTextAsync(BatonPaths.QueueFile, Ct);
            var output = new StringWriter();
            await TaskCommand.ExecuteAsync(new TaskOptions(TaskVerb.Status, Id: id, Json: true), output, Ct);
            using var json = JsonDocument.Parse(output.ToString());
            Assert.Equal(expectedState, json.RootElement.GetProperty("state").GetString());
            Assert.Equal(expectedTrigger, json.RootElement.GetProperty("nextTrigger").GetString());
            Assert.Equal("recorded-owner", json.RootElement.GetProperty("conductorHolder").GetString());
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("blocked").ValueKind);
            Assert.Equal(retired ? JsonValueKind.Object : JsonValueKind.Null,
                json.RootElement.GetProperty("retirement").ValueKind);
            var text = new StringWriter();
            await TaskCommand.ExecuteAsync(new TaskOptions(TaskVerb.Status, Id: id), text, Ct);
            Assert.Contains(expectedState, text.ToString(), StringComparison.Ordinal);
            Assert.Contains($"next: {expectedTrigger}", text.ToString(), StringComparison.Ordinal);
            if (retired)
                Assert.Equal("retained", json.RootElement.GetProperty("reason").GetString());
            else
                Assert.DoesNotContain("latest checks (historical)", text.ToString(), StringComparison.Ordinal);
            if (expectedState == "blocked")
            {
                var expectedReason = string.IsNullOrWhiteSpace(error) ? "task-failed" : error;
                Assert.Equal(expectedReason, json.RootElement.GetProperty("reason").GetString());
                Assert.Contains(expectedReason, text.ToString(), StringComparison.Ordinal);
            }
            Assert.Equal(before, await File.ReadAllTextAsync(BatonPaths.QueueFile, Ct));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Theory]
    [InlineData(QueueRetirement.Operator, "operator handled the failed attempt", "operator handled the failed attempt")]
    [InlineData(QueueRetirement.Merged, "merged PR #50", "merged PR #50")]
    [InlineData(QueueRetirement.Operator, null, "retirement-reason-unavailable")]
    [InlineData(QueueRetirement.Merged, " ", "retirement-reason-unavailable")]
    public async Task Retired_task_status_reports_disposition_and_labels_old_attempt_error_as_history(
        string kind, string? recordedReason, string expectedReason)
    {
        var home = Path.Combine(Path.GetTempPath(), "baton-task-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            const string repository = "github.com/example/repo";
            const string oldError = "earlier launch failed";
            var id = TaskCommand.TaskId(repository, 50);
            var retiredAt = new DateTimeOffset(2026, 9, 29, 21, 0, 0, TimeSpan.Zero);
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
            {
                Items = [new QueueItem
                {
                    Tag = id,
                    Role = "implement",
                    Workspace = home,
                    SpecFile = BatonPaths.QueueSpecFile(id),
                    Repository = repository,
                    Issue = 50,
                    Stage = WorkStage.Continue,
                    State = QueueItemState.Failed,
                    Halted = true,
                    Error = oldError,
                    Retirement = new QueueRetirement(kind, retiredAt, recordedReason!),
                    OwnedTask = new OwnedTaskSubmission(id, repository, 50, "digest", "recorded-owner", retiredAt),
                }],
            }, Ct);
            var before = await File.ReadAllBytesAsync(BatonPaths.QueueFile, Ct);

            var jsonOutput = new StringWriter();
            await TaskCommand.ExecuteAsync(new TaskOptions(TaskVerb.Status, Id: id, Json: true), jsonOutput, Ct);
            using var json = JsonDocument.Parse(jsonOutput.ToString());
            var status = json.RootElement;
            Assert.Equal("retired", status.GetProperty("state").GetString());
            Assert.Equal(expectedReason, status.GetProperty("reason").GetString());
            Assert.Equal("none", status.GetProperty("nextTrigger").GetString());
            var retirement = status.GetProperty("retirement");
            Assert.Equal(kind, retirement.GetProperty("kind").GetString());
            Assert.Equal(retiredAt, retirement.GetProperty("at").GetDateTimeOffset());
            Assert.Equal(recordedReason, retirement.GetProperty("reason").GetString());
            Assert.Equal(oldError, status.GetProperty("latestChecks").GetProperty("error").GetString());
            Assert.Equal(JsonValueKind.Null, status.GetProperty("ready").ValueKind);
            Assert.Equal(JsonValueKind.Null, status.GetProperty("blocked").ValueKind);

            var textOutput = new StringWriter();
            await TaskCommand.ExecuteAsync(new TaskOptions(TaskVerb.Status, Id: id), textOutput, Ct);
            var text = textOutput.ToString();
            Assert.Contains($"retirement: {kind} at {retiredAt:O}; reason: {expectedReason}", text,
                StringComparison.Ordinal);
            Assert.Contains($"latest checks (historical): unknown; {oldError}", text, StringComparison.Ordinal);
            Assert.Contains("next: none", text, StringComparison.Ordinal);
            Assert.Equal(before, await File.ReadAllBytesAsync(BatonPaths.QueueFile, Ct));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Status_separates_current_blocker_from_whitelisted_stopped_work_history()
    {
        var observedAt = new DateTimeOffset(2026, 9, 30, 17, 0, 0, TimeSpan.Zero);
        const string repository = "github.com/example/repo";
        const string historyKey = "stopped-judgment:history";
        var cases = new[]
        {
            (Name: "recovered-running", State: QueueItemState.Launched, Stage: WorkStage.Continue,
                Halted: false, Retirement: (QueueRetirement?)null, Ready: (TaskReadyReceipt?)null,
                HasBlocked: true, BlockedKey: (string?)historyKey, RetainedKey: (string?)historyKey,
                ExpectedState: "running", ExpectedTrigger: "daemon-tick", ExpectedCurrent: false,
                HasHistory: true),
            (Name: "re-review", State: QueueItemState.Launched, Stage: WorkStage.ReReview,
                Halted: false, Retirement: (QueueRetirement?)null, Ready: (TaskReadyReceipt?)null,
                HasBlocked: true, BlockedKey: (string?)historyKey, RetainedKey: (string?)historyKey,
                ExpectedState: "running", ExpectedTrigger: "daemon-tick", ExpectedCurrent: false,
                HasHistory: true),
            (Name: "ready", State: QueueItemState.Queued, Stage: WorkStage.Ready,
                Halted: false, Retirement: (QueueRetirement?)null, Ready: ReadyReceipt(repository, 51),
                HasBlocked: true, BlockedKey: (string?)historyKey, RetainedKey: (string?)historyKey,
                ExpectedState: "ready-as-of", ExpectedTrigger: "none", ExpectedCurrent: false,
                HasHistory: true),
            (Name: "retired-operator", State: QueueItemState.Failed, Stage: WorkStage.Continue,
                Halted: true, Retirement: new QueueRetirement(QueueRetirement.Operator, observedAt, "handled"),
                Ready: (TaskReadyReceipt?)null, HasBlocked: true, BlockedKey: (string?)historyKey,
                RetainedKey: (string?)historyKey, ExpectedState: "retired", ExpectedTrigger: "none",
                ExpectedCurrent: false, HasHistory: true),
            (Name: "retired-merged", State: QueueItemState.Failed, Stage: WorkStage.Continue,
                Halted: true, Retirement: new QueueRetirement(QueueRetirement.Merged, observedAt, "merged"),
                Ready: (TaskReadyReceipt?)null, HasBlocked: true, BlockedKey: (string?)historyKey,
                RetainedKey: (string?)historyKey, ExpectedState: "retired", ExpectedTrigger: "none",
                ExpectedCurrent: false, HasHistory: true),
            (Name: "cancelled", State: QueueItemState.Cancelled, Stage: WorkStage.Continue,
                Halted: false, Retirement: (QueueRetirement?)null, Ready: (TaskReadyReceipt?)null,
                HasBlocked: true, BlockedKey: (string?)historyKey, RetainedKey: (string?)historyKey,
                ExpectedState: "cancelled", ExpectedTrigger: "none", ExpectedCurrent: false,
                HasHistory: true),
            (Name: "stale", State: QueueItemState.Queued, Stage: WorkStage.Ready,
                Halted: false, Retirement: (QueueRetirement?)null, Ready: ReadyReceipt(repository, 57),
                HasBlocked: true, BlockedKey: (string?)historyKey, RetainedKey: (string?)historyKey,
                ExpectedState: "stale", ExpectedTrigger: "conductor-reassessment", ExpectedCurrent: false,
                HasHistory: true),
            (Name: "linked-blocked", State: QueueItemState.Failed, Stage: WorkStage.Review,
                Halted: true, Retirement: (QueueRetirement?)null, Ready: (TaskReadyReceipt?)null,
                HasBlocked: true, BlockedKey: historyKey, RetainedKey: historyKey,
                ExpectedState: "blocked", ExpectedTrigger: "conductor-judgment", ExpectedCurrent: true,
                HasHistory: true),
            (Name: "missing-link", State: QueueItemState.Failed, Stage: WorkStage.Review,
                Halted: true, Retirement: (QueueRetirement?)null, Ready: (TaskReadyReceipt?)null,
                HasBlocked: false, BlockedKey: (string?)null, RetainedKey: historyKey,
                ExpectedState: "blocked", ExpectedTrigger: "conductor-judgment", ExpectedCurrent: false,
                HasHistory: true),
            (Name: "null-current-key", State: QueueItemState.Failed, Stage: WorkStage.Review,
                Halted: true, Retirement: (QueueRetirement?)null, Ready: (TaskReadyReceipt?)null,
                HasBlocked: true, BlockedKey: (string?)null, RetainedKey: historyKey,
                ExpectedState: "blocked", ExpectedTrigger: "conductor-judgment", ExpectedCurrent: false,
                HasHistory: true),
            (Name: "blank-current-key", State: QueueItemState.Failed, Stage: WorkStage.Review,
                Halted: true, Retirement: (QueueRetirement?)null, Ready: (TaskReadyReceipt?)null,
                HasBlocked: true, BlockedKey: " ", RetainedKey: historyKey,
                ExpectedState: "blocked", ExpectedTrigger: "conductor-judgment", ExpectedCurrent: false,
                HasHistory: true),
            (Name: "mismatched-key", State: QueueItemState.Failed, Stage: WorkStage.Review,
                Halted: true, Retirement: (QueueRetirement?)null, Ready: (TaskReadyReceipt?)null,
                HasBlocked: true, BlockedKey: "different-key", RetainedKey: historyKey,
                ExpectedState: "blocked", ExpectedTrigger: "conductor-judgment", ExpectedCurrent: false,
                HasHistory: true),
            (Name: "case-mismatched-key", State: QueueItemState.Failed, Stage: WorkStage.Review,
                Halted: true, Retirement: (QueueRetirement?)null, Ready: (TaskReadyReceipt?)null,
                HasBlocked: true, BlockedKey: historyKey.ToUpperInvariant(), RetainedKey: historyKey,
                ExpectedState: "blocked", ExpectedTrigger: "conductor-judgment", ExpectedCurrent: false,
                HasHistory: true),
            (Name: "null-retained-key", State: QueueItemState.Failed, Stage: WorkStage.Review,
                Halted: true, Retirement: (QueueRetirement?)null, Ready: (TaskReadyReceipt?)null,
                HasBlocked: true, BlockedKey: historyKey, RetainedKey: (string?)null,
                ExpectedState: "blocked", ExpectedTrigger: "conductor-judgment", ExpectedCurrent: false,
                HasHistory: true),
            (Name: "blank-retained-key", State: QueueItemState.Failed, Stage: WorkStage.Review,
                Halted: true, Retirement: (QueueRetirement?)null, Ready: (TaskReadyReceipt?)null,
                HasBlocked: true, BlockedKey: historyKey, RetainedKey: string.Empty,
                ExpectedState: "blocked", ExpectedTrigger: "conductor-judgment", ExpectedCurrent: false,
                HasHistory: true),
            (Name: "whitespace-retained-key", State: QueueItemState.Failed, Stage: WorkStage.Review,
                Halted: true, Retirement: (QueueRetirement?)null, Ready: (TaskReadyReceipt?)null,
                HasBlocked: true, BlockedKey: historyKey, RetainedKey: " \t",
                ExpectedState: "blocked", ExpectedTrigger: "conductor-judgment", ExpectedCurrent: false,
                HasHistory: true),
            (Name: "equal-empty-keys", State: QueueItemState.Failed, Stage: WorkStage.Review,
                Halted: true, Retirement: (QueueRetirement?)null, Ready: (TaskReadyReceipt?)null,
                HasBlocked: true, BlockedKey: string.Empty, RetainedKey: string.Empty,
                ExpectedState: "blocked", ExpectedTrigger: "conductor-judgment", ExpectedCurrent: false,
                HasHistory: true),
            (Name: "equal-whitespace-keys", State: QueueItemState.Failed, Stage: WorkStage.Review,
                Halted: true, Retirement: (QueueRetirement?)null, Ready: (TaskReadyReceipt?)null,
                HasBlocked: true, BlockedKey: " \t", RetainedKey: " \t",
                ExpectedState: "blocked", ExpectedTrigger: "conductor-judgment", ExpectedCurrent: false,
                HasHistory: true),
            (Name: "no-judgment", State: QueueItemState.Queued, Stage: WorkStage.Implement,
                Halted: false, Retirement: (QueueRetirement?)null, Ready: (TaskReadyReceipt?)null,
                HasBlocked: false, BlockedKey: (string?)null, RetainedKey: (string?)null,
                ExpectedState: "queued", ExpectedTrigger: "daemon-tick", ExpectedCurrent: false,
                HasHistory: false),
        };

        foreach (var testCase in cases)
        {
            var home = Path.Combine(Path.GetTempPath(), "baton-task-status-" + testCase.Name + "-"
                + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(home);
            using var scope = BatonEnvironmentSnapshot.BeginScope(
                BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
            try
            {
                var id = TaskCommand.TaskId(repository, 51);
                var judgment = testCase.HasHistory
                    ? new StoppedWorkJudgment(
                        testCase.RetainedKey, repository, id, new FleetAttemptId("attempt-history"), WorkStage.Review,
                        observedAt, "recorded-owner", 51, "head", "base", "Succeeded", true, "passing",
                        observedAt, "context", StoppedWorkHaltCause.MissingVerdict,
                        State: StoppedWorkJudgmentState.Blocked, Reason: "retained reason",
                        Choice: "retained choice", Explanation: "retained explanation")
                    : null;
                var owned = new OwnedTaskSubmission(id, repository, 51, "digest", "recorded-owner", observedAt,
                    testCase.Ready, testCase.HasBlocked
                        ? new TaskBlockedDisposition("halted", "retained evidence", observedAt,
                            testCase.BlockedKey)
                        : null);
                await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
                {
                    Items = [new QueueItem
                    {
                        Tag = id, Role = "implement", Workspace = home,
                        SpecFile = BatonPaths.QueueSpecFile(id), Repository = repository, Issue = 51,
                        Stage = testCase.Stage, State = testCase.State, Halted = testCase.Halted,
                        Error = testCase.ExpectedState == "stale" ? "old checks" : null,
                        Retirement = testCase.Retirement, OwnedTask = owned,
                        StoppedWorkJudgment = judgment,
                    }],
                    PullRequestObservations = testCase.ExpectedState == "stale"
                        ? [new QueuePullRequestObservation(repository, 57, "open", "new-head",
                            observedAt, observedAt, null)]
                        : null,
                }, Ct);
                var before = await File.ReadAllBytesAsync(BatonPaths.QueueFile, Ct);

                var jsonOutput = new StringWriter();
                await TaskCommand.ExecuteAsync(new TaskOptions(TaskVerb.Status, Id: id, Json: true),
                    jsonOutput, Ct);
                using var json = JsonDocument.Parse(jsonOutput.ToString());
                var status = json.RootElement;
                Assert.Equal(testCase.ExpectedState, status.GetProperty("state").GetString());
                var haltCause = status.GetProperty("haltCause");
                var obligationKey = status.GetProperty("obligationKey");
                if (testCase.ExpectedCurrent)
                {
                    Assert.Equal("MissingVerdict", haltCause.GetString());
                    Assert.Equal(historyKey, obligationKey.GetString());
                }
                else
                {
                    Assert.Equal(JsonValueKind.Null, haltCause.ValueKind);
                    Assert.Equal(JsonValueKind.Null, obligationKey.ValueKind);
                }

                var history = status.GetProperty("stoppedWorkHistory");
                if (testCase.HasHistory)
                {
                    Assert.Equal(JsonValueKind.Object, history.ValueKind);
                    Assert.Equal(5, history.EnumerateObject().Count());
                    Assert.Equal("MissingVerdict", history.GetProperty("haltCause").GetString());
                    Assert.Equal(testCase.RetainedKey, history.GetProperty("obligationKey").GetString());
                    Assert.Equal("attempt-history", history.GetProperty("attemptId").GetString());
                    Assert.Equal("review", history.GetProperty("stage").GetString());
                    Assert.Equal(observedAt, history.GetProperty("observedAt").GetDateTimeOffset());
                    Assert.DoesNotContain(history.EnumerateObject(), property =>
                        property.Name is "state" or "choice" or "explanation" or "holder" or "contextSha256");
                }
                else
                {
                    Assert.Equal(JsonValueKind.Null, history.ValueKind);
                }
                Assert.Equal(before, await File.ReadAllBytesAsync(BatonPaths.QueueFile, Ct));

                var textOutput = new StringWriter();
                await TaskCommand.ExecuteAsync(new TaskOptions(TaskVerb.Status, Id: id), textOutput, Ct);
                var text = textOutput.ToString();
                Assert.Contains($"Task {id}: {testCase.ExpectedState}", text, StringComparison.Ordinal);
                Assert.Contains($"next: {testCase.ExpectedTrigger};", text, StringComparison.Ordinal);
                if (testCase.HasBlocked)
                {
                    var blockerLabel = testCase.ExpectedState == "blocked"
                        ? "  current blocker: halted; retained evidence"
                        : "  retained blocker (historical): halted; retained evidence";
                    Assert.Contains(blockerLabel, text, StringComparison.Ordinal);
                }
                if (testCase.ExpectedCurrent)
                    Assert.Contains($"current stopped-work blocker: haltCause=MissingVerdict; obligationKey={historyKey}",
                        text, StringComparison.Ordinal);
                else
                    Assert.DoesNotContain("current stopped-work blocker:", text, StringComparison.Ordinal);
                if (testCase.HasHistory)
                {
                    var expectedHistoryKey = testCase.RetainedKey ?? "none";
                    Assert.Contains(
                        $"stopped-work history (one retained as-of snapshot; not latest or complete history): "
                        + $"haltCause=MissingVerdict; obligationKey={expectedHistoryKey}; "
                        + $"attemptId=attempt-history; stage=review; observedAt={observedAt:O}",
                        text, StringComparison.Ordinal);
                }
                else
                {
                    Assert.DoesNotContain("stopped-work history", text, StringComparison.Ordinal);
                }

                Assert.Equal(before, await File.ReadAllBytesAsync(BatonPaths.QueueFile, Ct));
            }
            finally
            {
                DirectoryCleanup.DeleteRecursively(home);
            }
        }

        TaskReadyReceipt ReadyReceipt(string repository, int issue)
            => new("ready", TaskCommand.TaskId(repository, issue), repository, issue, 77,
                new string('a', 40), "review", new string('b', 64), "passing", "checks", observedAt, observedAt);
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

    [Fact]
    public async Task Task_and_legacy_lifecycle_share_issue_reservation_before_provisioning()
    {
        var home = Path.Combine(Path.GetTempPath(), "baton-task-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var project = Path.Combine(home, "source");
            var workspace = Path.Combine(home, "w46");
            var spec = Path.Combine(home, "brief.md");
            Directory.CreateDirectory(project);
            await File.WriteAllTextAsync(spec, "one frozen brief", Ct);
            var repository = RepositoryIdentity.From("https://github.com/example/repo", null)!;
            await ConductorClaimStore.ClaimAsync(repository, "owner", home, cancellationToken: Ct);
            var provisions = 0;
            Task<RepositoryIdentity?> Resolve(string path, CancellationToken token) =>
                Task.FromResult<RepositoryIdentity?>(repository);
            Task<IssueWorktreeProvisioner.ProvisionedIssueWorktree> Provision(
                int issue, string source, string? root, string repo, bool lifecycle,
                TextWriter writer, CancellationToken token)
            {
                provisions++;
                Directory.CreateDirectory(workspace);
                ProjectCeilingStore.Set(workspace, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);
                return Task.FromResult(new IssueWorktreeProvisioner.ProvisionedIssueWorktree(workspace, "46-lane"));
            }
            var task = new TaskOptions(TaskVerb.Submit, 46, project,
                new TaskSizeDeclaration(DeclaredTaskSize.Small, "one issue"), spec);
            var legacy = new QueueOptions(QueueVerb.Add, Tag: "legacy-46", Role: "implement",
                SpecFilePath: spec, Issue: 46, Lifecycle: true, Requirements: []);
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
            {
                Items = [new QueueItem
                {
                    Tag = "legacy-46", Role = "implement", Workspace = workspace,
                    SpecFile = BatonPaths.QueueSpecFile("legacy-46"),
                    Repository = repository.Value, Issue = 46, Stage = WorkStage.Implement,
                }],
            }, Ct);
            await Assert.ThrowsAsync<CliArgumentException>(() =>
                TaskCommand.ExecuteAsync(task, TextWriter.Null, Resolve, Provision, Ct, IssuePreparationRunner.NoCollisions));
            Assert.Equal(0, provisions);

            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with { Items = [] }, Ct);
            Assert.Equal(0, await TaskCommand.ExecuteAsync(task, TextWriter.Null, Resolve, Provision, Ct, IssuePreparationRunner.NoCollisions));
            Assert.Equal(1, provisions);
            await Assert.ThrowsAsync<CliArgumentException>(() =>
                QueueCommand.ExecuteAsync(legacy, TextWriter.Null, Ct, project, Resolve, Provision));
            Assert.Equal(1, provisions);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public void No_selection_digest_preserves_exact_legacy_preimage_bytes()
    {
        var size = new TaskSizeDeclaration(DeclaredTaskSize.Medium, "scoped already");
        var specBytes = Encoding.UTF8.GetBytes("spec body");

        // Pinned fixture: these exact hex strings are the historical no-selection preimage
        // (repository\nissue\nsize\nrationale\n + "no-spec\n"/"spec\n" + raw spec bytes, SHA-256).
        // A change here silently breaks every retained task's idempotency/conflict check.
        Assert.Equal("a853306ca7de02ff062aa1b4651c280003259a9ccbf323e26f87ae357bd2fcb0",
            TaskCommand.ComputeInputDigest("github.com/example/repo", 77, size, null, null));
        Assert.Equal("9f5df45b8a1531d07086da86abc2bd5eda3677dd5596183ccbba63280a0b297b",
            TaskCommand.ComputeInputDigest("github.com/example/repo", 77, size, specBytes, null));
    }

    [Fact]
    public void Selected_digest_uses_a_distinct_domain_never_suffixed_onto_the_legacy_header()
    {
        var size = new TaskSizeDeclaration(DeclaredTaskSize.Medium, "scoped already");
        var noSelection = TaskCommand.ComputeInputDigest("github.com/example/repo", 77, size, null, null);
        var emptySelection = TaskCommand.ComputeInputDigest("github.com/example/repo", 77, size, null,
            new QueueStageSelection { Stage = WorkStage.Implement });
        var oneAxisSelection = TaskCommand.ComputeInputDigest("github.com/example/repo", 77, size, null,
            new QueueStageSelection { Stage = WorkStage.Implement, Model = "opus" });
        Assert.NotEqual(noSelection, emptySelection);
        Assert.NotEqual(noSelection, oneAxisSelection);
        Assert.NotEqual(emptySelection, oneAxisSelection);
    }

    [Fact]
    public void Selected_digest_is_deterministic_and_distinguishes_every_explicit_axis()
    {
        var size = new TaskSizeDeclaration(DeclaredTaskSize.Small, "one cluster");
        string Digest(string? adapter, string? model, string? effort) => TaskCommand.ComputeInputDigest(
            "github.com/example/repo", 1, size, null,
            new QueueStageSelection { Stage = WorkStage.Implement, Adapter = adapter, Model = model, Effort = effort });

        Assert.Equal(Digest("claude", "opus", "high"), Digest("claude", "opus", "high"));
        Assert.NotEqual(Digest("claude", "opus", "high"), Digest("claude", "opus", null));
        Assert.NotEqual(Digest("claude", "opus", null), Digest("claude", null, null));
        Assert.NotEqual(Digest("claude", null, null), Digest(null, "claude", null));
        Assert.NotEqual(Digest(null, null, "high"), Digest(null, null, "medium"));
    }

    [Fact]
    public void Selected_digest_is_collision_safe_across_adversarial_field_boundaries()
    {
        // Naive, un-delimited concatenation of ("ab", "cd") and ("a", "bcd") produces the identical
        // byte sequence "abcd"; length-prefixing each field is what keeps these two distinct
        // submissions from aliasing onto the same digest.
        var left = TaskCommand.ComputeInputDigest("github.com/example/repo", 1,
            new TaskSizeDeclaration(DeclaredTaskSize.Small, "ab"), null,
            new QueueStageSelection { Stage = WorkStage.Implement, Model = "cd" });
        var right = TaskCommand.ComputeInputDigest("github.com/example/repo", 1,
            new TaskSizeDeclaration(DeclaredTaskSize.Small, "a"), null,
            new QueueStageSelection { Stage = WorkStage.Implement, Model = "bcd" });
        Assert.NotEqual(left, right);

        // An absent spec and a zero-byte captured spec carry different presence tags, not merely
        // different lengths of the same "present" marker.
        var size = new TaskSizeDeclaration(DeclaredTaskSize.Small, "one cluster");
        var noSpec = TaskCommand.ComputeInputDigest("github.com/example/repo", 1, size, null,
            new QueueStageSelection { Stage = WorkStage.Implement, Model = "opus" });
        var emptySpec = TaskCommand.ComputeInputDigest("github.com/example/repo", 1, size, [],
            new QueueStageSelection { Stage = WorkStage.Implement, Model = "opus" });
        Assert.NotEqual(noSpec, emptySpec);

        // This pair really aliases in the historical newline header. A selected request must not
        // reuse that ambiguous header and merely append selection fields to it.
        var embeddedMarkers = new TaskSizeDeclaration(DeclaredTaskSize.Small, "x\nspec\npayload");
        var capturedMarkers = new TaskSizeDeclaration(DeclaredTaskSize.Small, "x");
        byte[] capturedSpec = Encoding.UTF8.GetBytes("payload\nno-spec\n");
        var selection = new QueueStageSelection { Stage = WorkStage.Implement, Model = "opus" };
        Assert.Equal(
            TaskCommand.ComputeInputDigest("github.com/example/repo", 1, embeddedMarkers, null, null),
            TaskCommand.ComputeInputDigest("github.com/example/repo", 1, capturedMarkers, capturedSpec, null));
        Assert.NotEqual(
            TaskCommand.ComputeInputDigest("github.com/example/repo", 1, embeddedMarkers, null, selection),
            TaskCommand.ComputeInputDigest("github.com/example/repo", 1, capturedMarkers, capturedSpec, selection));
    }

    [Fact]
    public async Task Adding_an_explicit_selection_to_a_previously_unselected_submission_conflicts()
    {
        var home = Path.Combine(Path.GetTempPath(), "baton-task-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var project = Path.Combine(home, "source");
            var workspace = Path.Combine(home, "w61");
            var spec = Path.Combine(home, "brief.md");
            Directory.CreateDirectory(project);
            await File.WriteAllTextAsync(spec, "one frozen brief", Ct);
            var repository = RepositoryIdentity.From("https://github.com/example/repo", null)!;
            await ConductorClaimStore.ClaimAsync(repository, "owner", home, cancellationToken: Ct);
            Task<RepositoryIdentity?> Resolve(string path, CancellationToken token) => Task.FromResult<RepositoryIdentity?>(repository);
            Task<IssueWorktreeProvisioner.ProvisionedIssueWorktree> Provision(
                int issue, string source, string? root, string repo, bool lifecycle,
                TextWriter writer, CancellationToken token)
            {
                Directory.CreateDirectory(workspace);
                ProjectCeilingStore.Set(workspace, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);
                return Task.FromResult(new IssueWorktreeProvisioner.ProvisionedIssueWorktree(workspace, "61-lane"));
            }
            var size = new TaskSizeDeclaration(DeclaredTaskSize.Small, "one issue");

            // Unselected -> selected is a conflict; the identical unselected input stays idempotent.
            var unselected = new TaskOptions(TaskVerb.Submit, 61, project, size, spec);
            Assert.Equal(0, await TaskCommand.ExecuteAsync(unselected, TextWriter.Null, Resolve, Provision, Ct, IssuePreparationRunner.NoCollisions));
            var id = TaskCommand.TaskId(repository.Value, 61);
            var addingSelection = unselected with { Model = "opus" };
            var addedConflict = await Assert.ThrowsAsync<CliArgumentException>(() =>
                TaskCommand.ExecuteAsync(addingSelection, TextWriter.Null, Resolve, Provision, Ct, IssuePreparationRunner.NoCollisions));
            Assert.Contains(id, addedConflict.Message, StringComparison.Ordinal);
            Assert.Equal(0, await TaskCommand.ExecuteAsync(unselected, TextWriter.Null, Resolve, Provision, Ct, IssuePreparationRunner.NoCollisions));
            Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Equivalent_model_selection_forms_keep_assignment_but_not_admission_identity(
        bool adapterFormFirst)
    {
        var home = Path.Combine(Path.GetTempPath(), "baton-task-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var project = Path.Combine(home, "source");
            var workspace = Path.Combine(home, "w61-equivalent");
            var spec = Path.Combine(home, "brief.md");
            Directory.CreateDirectory(project);
            await File.WriteAllTextAsync(spec, "one frozen brief", Ct);
            var repository = RepositoryIdentity.From("https://github.com/example/repo", null)!;
            await ConductorClaimStore.ClaimAsync(repository, "owner", home, cancellationToken: Ct);
            var provisions = 0;
            Task<RepositoryIdentity?> Resolve(string path, CancellationToken token) => Task.FromResult<RepositoryIdentity?>(repository);
            Task<IssueWorktreeProvisioner.ProvisionedIssueWorktree> Provision(
                int issue, string source, string? root, string repo, bool lifecycle,
                TextWriter writer, CancellationToken token)
            {
                provisions++;
                Directory.CreateDirectory(workspace);
                ProjectCeilingStore.Set(workspace, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);
                return Task.FromResult(new IssueWorktreeProvisioner.ProvisionedIssueWorktree(workspace, "61-equivalent-lane"));
            }

            var size = new TaskSizeDeclaration(DeclaredTaskSize.Small, "one issue");
            var modelOnly = new TaskOptions(TaskVerb.Submit, 61, project, size, spec, Model: "opus");
            var adapterAndModel = modelOnly with { Adapter = "claude" };
            var initial = adapterFormFirst ? adapterAndModel : modelOnly;
            var changed = adapterFormFirst ? modelOnly : adapterAndModel;
            Assert.Equal(0, await TaskCommand.ExecuteAsync(initial, TextWriter.Null, Resolve, Provision, Ct,
                IssuePreparationRunner.NoCollisions));

            var retained = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            var assignment = Assert.IsType<FrozenWorkerAssignment>(retained.WorkerAssignment);
            Assert.Equal(("claude", "opus", null),
                (assignment.Adapter, assignment.Model, assignment.Effort));
            var queueBeforeReplay = await File.ReadAllBytesAsync(BatonPaths.QueueFile, Ct);
            var briefBeforeReplay = await File.ReadAllBytesAsync(retained.SpecFile, Ct);
            var trustBeforeReplay = await File.ReadAllBytesAsync(ProjectCeilingStore.DefaultPath, Ct);

            // The alternate spelling resolves to the same worker tuple but remains a different
            // explicit submission, so admission must reject it before the preparation callback.
            var conflict = await Assert.ThrowsAsync<CliArgumentException>(() =>
                TaskCommand.ExecuteAsync(changed, TextWriter.Null, Resolve, Provision, Ct,
                    IssuePreparationRunner.NoCollisions));
            Assert.Contains(retained.OwnedTask!.Id, conflict.Message, StringComparison.Ordinal);
            Assert.Equal(1, provisions);
            Assert.Equal(queueBeforeReplay, await File.ReadAllBytesAsync(BatonPaths.QueueFile, Ct));
            Assert.Equal(briefBeforeReplay, await File.ReadAllBytesAsync(retained.SpecFile, Ct));
            Assert.Equal(trustBeforeReplay, await File.ReadAllBytesAsync(ProjectCeilingStore.DefaultPath, Ct));

            // Exact replay is idempotent and still cannot reach a vendor or provision another lane.
            Assert.Equal(0, await TaskCommand.ExecuteAsync(initial, TextWriter.Null, Resolve, Provision, Ct,
                IssuePreparationRunner.NoCollisions));
            Assert.Equal(1, provisions);
            Assert.Equal(queueBeforeReplay, await File.ReadAllBytesAsync(BatonPaths.QueueFile, Ct));
            Assert.Equal(briefBeforeReplay, await File.ReadAllBytesAsync(retained.SpecFile, Ct));
            Assert.Equal(trustBeforeReplay, await File.ReadAllBytesAsync(ProjectCeilingStore.DefaultPath, Ct));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Changing_or_dropping_a_retained_explicit_selection_conflicts_in_both_directions()
    {
        var home = Path.Combine(Path.GetTempPath(), "baton-task-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var project = Path.Combine(home, "source");
            var workspace = Path.Combine(home, "w62");
            var spec = Path.Combine(home, "brief.md");
            Directory.CreateDirectory(project);
            await File.WriteAllTextAsync(spec, "one frozen brief", Ct);
            var repository = RepositoryIdentity.From("https://github.com/example/repo", null)!;
            await ConductorClaimStore.ClaimAsync(repository, "owner", home, cancellationToken: Ct);
            Task<RepositoryIdentity?> Resolve(string path, CancellationToken token) => Task.FromResult<RepositoryIdentity?>(repository);
            Task<IssueWorktreeProvisioner.ProvisionedIssueWorktree> Provision(
                int issue, string source, string? root, string repo, bool lifecycle,
                TextWriter writer, CancellationToken token)
            {
                Directory.CreateDirectory(workspace);
                ProjectCeilingStore.Set(workspace, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);
                return Task.FromResult(new IssueWorktreeProvisioner.ProvisionedIssueWorktree(workspace, "62-lane"));
            }
            var size = new TaskSizeDeclaration(DeclaredTaskSize.Small, "one issue");

            // Selected -> changed and selected -> absent are both conflicts; the identical selected
            // input stays idempotent.
            var selected = new TaskOptions(TaskVerb.Submit, 62, project, size, spec, Adapter: "claude", Model: "opus");
            Assert.Equal(0, await TaskCommand.ExecuteAsync(selected, TextWriter.Null, Resolve, Provision, Ct, IssuePreparationRunner.NoCollisions));
            var id = TaskCommand.TaskId(repository.Value, 62);
            var changedModel = selected with { Model = "sonnet" };
            var changedConflict = await Assert.ThrowsAsync<CliArgumentException>(() =>
                TaskCommand.ExecuteAsync(changedModel, TextWriter.Null, Resolve, Provision, Ct, IssuePreparationRunner.NoCollisions));
            Assert.Contains(id, changedConflict.Message, StringComparison.Ordinal);
            var droppedSelection = selected with { Adapter = null, Model = null };
            var droppedConflict = await Assert.ThrowsAsync<CliArgumentException>(() =>
                TaskCommand.ExecuteAsync(droppedSelection, TextWriter.Null, Resolve, Provision, Ct, IssuePreparationRunner.NoCollisions));
            Assert.Contains(id, droppedConflict.Message, StringComparison.Ordinal);
            Assert.Equal(0, await TaskCommand.ExecuteAsync(selected, TextWriter.Null, Resolve, Provision, Ct, IssuePreparationRunner.NoCollisions));
            Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Selected_submission_is_idempotent_during_and_after_preparation()
    {
        var home = Path.Combine(Path.GetTempPath(), "baton-task-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var project = Path.Combine(home, "source");
            var workspace = Path.Combine(home, "w66");
            var spec = Path.Combine(home, "brief.md");
            Directory.CreateDirectory(project);
            await File.WriteAllTextAsync(spec, "one frozen brief", Ct);
            var repository = RepositoryIdentity.From("https://github.com/example/repo", null)!;
            await ConductorClaimStore.ClaimAsync(repository, "owner", home, cancellationToken: Ct);
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
                return new(workspace, "66-lane");
            }
            Task<RepositoryIdentity?> Resolve(string path, CancellationToken token) => Task.FromResult<RepositoryIdentity?>(repository);
            var options = new TaskOptions(TaskVerb.Submit, 66, project,
                new TaskSizeDeclaration(DeclaredTaskSize.Small, "one cluster"), spec,
                Adapter: "claude", Model: "opus", Effort: "high");
            var first = TaskCommand.ExecuteAsync(options, new StringWriter(), Resolve, Provision, Ct, IssuePreparationRunner.NoCollisions);
            await entered.Task.WaitAsync(Ct);

            // Identical explicit selection, resubmitted while preparation is still in flight.
            Assert.Equal(0, await TaskCommand.ExecuteAsync(options, new StringWriter(), Resolve, Provision, Ct, IssuePreparationRunner.NoCollisions));
            Assert.Equal(1, provisions);

            release.SetResult();
            Assert.Equal(0, await first);

            // Identical explicit selection, resubmitted after preparation completed.
            Assert.Equal(0, await TaskCommand.ExecuteAsync(options, new StringWriter(), Resolve, Provision, Ct, IssuePreparationRunner.NoCollisions));
            Assert.Equal(1, provisions);
            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(TaskPreparationState.Prepared, item.IssuePreparation!.State);
            Assert.Equal("claude", Assert.Single(item.StageSelections!).Adapter);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Selected_submission_sets_exactly_one_implement_stage_selection_leaving_other_stages_and_the_shared_tier_table_untouched()
    {
        var home = Path.Combine(Path.GetTempPath(), "baton-task-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var project = Path.Combine(home, "source");
            var workspace = Path.Combine(home, "w63");
            var spec = Path.Combine(home, "brief.md");
            Directory.CreateDirectory(project);
            await File.WriteAllTextAsync(spec, "one frozen brief", Ct);
            var repository = RepositoryIdentity.From("https://github.com/example/repo", null)!;
            await ConductorClaimStore.ClaimAsync(repository, "owner", home, cancellationToken: Ct);
            var sharedSettings = new DaemonSettings
            {
                Queue = new QueueSettings
                {
                    Tiers = new Dictionary<string, QueueTierSettings>
                    {
                        ["engine"] = new() { Adapter = "claude", Model = "opus", Effort = "high" },
                        ["review-engine"] = new() { Adapter = "codex", Model = "gpt-5.6-sol", Effort = "high" },
                    },
                    AdapterDefaultModels = new Dictionary<string, string> { ["agy"] = "gemini-3.8-flash-high" },
                },
            };
            await DaemonSettingsStore.SaveAsync(sharedSettings, BatonPaths.SettingsFile, Ct);
            var settingsBefore = await File.ReadAllBytesAsync(BatonPaths.SettingsFile, Ct);
            var loadedBefore = await DaemonSettingsStore.LoadAsync(BatonPaths.SettingsFile, Ct);
            var unrelated = new QueueItem
            {
                Tag = "unrelated-task",
                Role = "implement",
                Workspace = project,
                SpecFile = BatonPaths.QueueSpecFile("unrelated-task"),
                ScopeClass = "engine",
                Stage = WorkStage.Implement,
            };
            var unrelatedBefore = QueueTierTable.ResolveForStage(unrelated, WorkStage.Implement,
                loadedBefore.Queue, WorkerRoleCatalog.QueueTierFor, WorkerRoleCatalog.QueueTierForRole);
            Task<RepositoryIdentity?> Resolve(string path, CancellationToken token) => Task.FromResult<RepositoryIdentity?>(repository);
            Task<IssueWorktreeProvisioner.ProvisionedIssueWorktree> Provision(
                int issue, string source, string? root, string repo, bool lifecycle,
                TextWriter writer, CancellationToken token)
            {
                Directory.CreateDirectory(workspace);
                ProjectCeilingStore.Set(workspace, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);
                return Task.FromResult(new IssueWorktreeProvisioner.ProvisionedIssueWorktree(workspace, "63-lane"));
            }
            var size = new TaskSizeDeclaration(DeclaredTaskSize.Small, "one issue");
            var options = new TaskOptions(TaskVerb.Submit, 63, project, size, spec, Adapter: "claude", Model: "opus", Effort: "high");
            Assert.Equal(0, await TaskCommand.ExecuteAsync(options, TextWriter.Null, Resolve, Provision, Ct, IssuePreparationRunner.NoCollisions));
            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);

            Assert.False(item.LifecyclePin);
            Assert.Null(item.ScopeClass);
            var selection = Assert.Single(item.StageSelections!);
            Assert.Equal(WorkStage.Implement, selection.Stage);
            Assert.Equal("claude", selection.Adapter);
            Assert.Equal("opus", selection.Model);
            Assert.Equal("high", selection.Effort);

            // Later stages inherit nothing from this submission -- each resolves its own tier.
            var (reviewSelection, reviewSource) = QueueTierTable.SelectionForStage(item, WorkStage.Review);
            Assert.Null(reviewSelection);
            Assert.Equal(QueueSelectionSource.StageDefault, reviewSource);

            // Admission reads the shared catalog but never owns or rewrites it. The unrelated
            // default resolution is a discriminating control for accidental catalog mutation.
            Assert.Equal(settingsBefore, await File.ReadAllBytesAsync(BatonPaths.SettingsFile, Ct));
            var loadedAfter = await DaemonSettingsStore.LoadAsync(BatonPaths.SettingsFile, Ct);
            var unrelatedAfter = QueueTierTable.ResolveForStage(unrelated, WorkStage.Implement,
                loadedAfter.Queue, WorkerRoleCatalog.QueueTierFor, WorkerRoleCatalog.QueueTierForRole);
            Assert.Equal(unrelatedBefore, unrelatedAfter);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Selected_submission_freezes_initial_assignment_against_settings_drift_after_acceptance()
    {
        var home = Path.Combine(Path.GetTempPath(), "baton-task-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var project = Path.Combine(home, "source");
            var workspace = Path.Combine(home, "w64");
            var spec = Path.Combine(home, "brief.md");
            Directory.CreateDirectory(project);
            await File.WriteAllTextAsync(spec, "one frozen brief", Ct);
            var repository = RepositoryIdentity.From("https://github.com/example/repo", null)!;
            await ConductorClaimStore.ClaimAsync(repository, "owner", home, cancellationToken: Ct);
            Task<RepositoryIdentity?> Resolve(string path, CancellationToken token) => Task.FromResult<RepositoryIdentity?>(repository);
            Task<IssueWorktreeProvisioner.ProvisionedIssueWorktree> Provision(
                int issue, string source, string? root, string repo, bool lifecycle,
                TextWriter writer, CancellationToken token)
            {
                Directory.CreateDirectory(workspace);
                ProjectCeilingStore.Set(workspace, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);
                return Task.FromResult(new IssueWorktreeProvisioner.ProvisionedIssueWorktree(workspace, "64-lane"));
            }
            var size = new TaskSizeDeclaration(DeclaredTaskSize.Small, "one issue");
            // Explicit adapter, no model: the shipped per-adapter default fills the launch model at
            // add time -- exactly the ambient value a later settings edit can move.
            var options = new TaskOptions(TaskVerb.Submit, 64, project, size, spec, Adapter: "agy");
            Assert.Equal(0, await TaskCommand.ExecuteAsync(options, TextWriter.Null, Resolve, Provision, Ct, IssuePreparationRunner.NoCollisions));
            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.NotNull(item.WorkerAssignment);
            Assert.Equal("agy", item.WorkerAssignment!.Adapter);
            Assert.Equal("gemini-3.8-flash-high", item.WorkerAssignment.Model);

            // Discriminating control: an operator edit to the adapter default after acceptance really
            // does move what a fresh resolution would pick, so the freeze below is actually exercised.
            var driftedSettings = new QueueSettings
            {
                AdapterDefaultModels = new Dictionary<string, string> { ["agy"] = "gemini-4-ultra" },
            };
            var driftedCurrent = QueueTierTable.ResolveForStage(item, WorkStage.Implement, driftedSettings,
                WorkerRoleCatalog.QueueTierFor, WorkerRoleCatalog.QueueTierForRole);
            Assert.Equal("gemini-4-ultra", driftedCurrent.Model);

            var applied = QueueLauncher.ApplyFrozenAssignment(item, driftedCurrent);
            Assert.Equal("agy", applied.Adapter);
            Assert.Equal("gemini-3.8-flash-high", applied.Model);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Unknown_adapter_in_explicit_selection_refuses_before_any_queue_side_effect()
    {
        var home = Path.Combine(Path.GetTempPath(), "baton-task-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var project = Path.Combine(home, "source");
            Directory.CreateDirectory(project);
            var repository = RepositoryIdentity.From("https://github.com/example/repo", null)!;
            await ConductorClaimStore.ClaimAsync(repository, "owner", home, cancellationToken: Ct);
            Task<RepositoryIdentity?> Resolve(string path, CancellationToken token) => Task.FromResult<RepositoryIdentity?>(repository);
            var provisions = 0;
            var trustPath = ProjectCeilingStore.DefaultPath;
            var queuePath = BatonPaths.QueueFile;
            var taskId = TaskCommand.TaskId(repository.Value, 65);
            var briefPath = BatonPaths.QueueSpecFile(taskId);
            Assert.False(File.Exists(trustPath));
            Assert.False(File.Exists(queuePath));
            Assert.False(File.Exists(briefPath));
            Task<IssueWorktreeProvisioner.ProvisionedIssueWorktree> Provision(
                int issue, string source, string? root, string repo, bool lifecycle,
                TextWriter writer, CancellationToken token)
            {
                provisions++;
                throw new InvalidOperationException("Provisioning must not run after a refused selection.");
            }
            var size = new TaskSizeDeclaration(DeclaredTaskSize.Small, "one issue");
            var options = new TaskOptions(TaskVerb.Submit, 65, project, size, Adapter: "not-a-real-adapter", Model: "whatever");
            var ex = await Assert.ThrowsAsync<CliArgumentException>(() =>
                TaskCommand.ExecuteAsync(options, TextWriter.Null, Resolve, Provision, Ct, IssuePreparationRunner.NoCollisions));
            Assert.Contains("not-a-real-adapter", ex.Message, StringComparison.Ordinal);
            Assert.Equal(0, provisions);
            // No repository checkout was fabricated for this pre-provision validation refusal, so
            // Git-side witnesses are intentionally limited to the injected provisioner not running.
            Assert.False(File.Exists(trustPath));
            Assert.False(File.Exists(queuePath));
            Assert.False(File.Exists(briefPath));
            Assert.False(Directory.Exists(Path.Combine(home, "w65")));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Theory]
    [InlineData(null, "opus", null)]
    [InlineData("claude", null, "high")]
    public async Task Status_initial_worker_selection_normalizes_the_retained_implement_stage_entry(
        string? adapter, string? model, string? effort)
    {
        var home = Path.Combine(Path.GetTempPath(), "baton-task-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            const string repository = "github.com/example/repo";
            var id = TaskCommand.TaskId(repository, 70);
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
            {
                Items = [new QueueItem
                {
                    Tag = id, Role = "implement", Workspace = home,
                    SpecFile = BatonPaths.QueueSpecFile(id), Repository = repository, Issue = 70,
                    Stage = WorkStage.Implement,
                    StageSelections = [new QueueStageSelection
                    {
                        Stage = WorkStage.Implement, Adapter = adapter, Model = model, Effort = effort,
                    }],
                    OwnedTask = new OwnedTaskSubmission(id, repository, 70, "digest", "recorded-owner", DateTimeOffset.UtcNow),
                }],
            }, Ct);

            var jsonOutput = new StringWriter();
            await TaskCommand.ExecuteAsync(new TaskOptions(TaskVerb.Status, Id: id, Json: true), jsonOutput, Ct);
            using var json = JsonDocument.Parse(jsonOutput.ToString());
            var selection = json.RootElement.GetProperty("initialWorkerSelection");
            Assert.Equal(JsonValueKind.Object, selection.ValueKind);
            Assert.Equal(3, selection.EnumerateObject().Count());
            Assert.Equal(adapter, selection.GetProperty("adapter").GetString());
            Assert.Equal(model, selection.GetProperty("model").GetString());
            Assert.Equal(effort, selection.GetProperty("effort").GetString());
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("retainedWorkerAssignment").ValueKind);

            var textOutput = new StringWriter();
            await TaskCommand.ExecuteAsync(new TaskOptions(TaskVerb.Status, Id: id), textOutput, Ct);
            Assert.Contains(
                $"initial implement selection (retained plan): adapter={adapter ?? "none"}; model={model ?? "none"}; effort={effort ?? "none"}",
                textOutput.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Status_initial_worker_selection_is_null_without_a_retained_implement_stage_entry()
    {
        var home = Path.Combine(Path.GetTempPath(), "baton-task-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            const string repository = "github.com/example/repo";
            var noSelectionId = TaskCommand.TaskId(repository, 71);
            var reviewOnlyId = TaskCommand.TaskId(repository, 72);
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
            {
                Items =
                [
                    new QueueItem
                    {
                        Tag = noSelectionId, Role = "implement", Workspace = home,
                        SpecFile = BatonPaths.QueueSpecFile(noSelectionId), Repository = repository, Issue = 71,
                        Stage = WorkStage.Implement,
                        OwnedTask = new OwnedTaskSubmission(
                            noSelectionId, repository, 71, "digest", "recorded-owner", DateTimeOffset.UtcNow),
                    },
                    new QueueItem
                    {
                        Tag = reviewOnlyId, Role = "implement", Workspace = home,
                        SpecFile = BatonPaths.QueueSpecFile(reviewOnlyId), Repository = repository, Issue = 72,
                        Stage = WorkStage.Review,
                        StageSelections = [new QueueStageSelection { Stage = WorkStage.Review, Model = "sonnet" }],
                        OwnedTask = new OwnedTaskSubmission(
                            reviewOnlyId, repository, 72, "digest", "recorded-owner", DateTimeOffset.UtcNow),
                    },
                ],
            }, Ct);

            foreach (var id in new[] { noSelectionId, reviewOnlyId })
            {
                var jsonOutput = new StringWriter();
                await TaskCommand.ExecuteAsync(new TaskOptions(TaskVerb.Status, Id: id, Json: true), jsonOutput, Ct);
                using var json = JsonDocument.Parse(jsonOutput.ToString());
                Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("initialWorkerSelection").ValueKind);

                var textOutput = new StringWriter();
                await TaskCommand.ExecuteAsync(new TaskOptions(TaskVerb.Status, Id: id), textOutput, Ct);
                Assert.DoesNotContain("initial implement selection", textOutput.ToString(), StringComparison.Ordinal);
            }
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Status_clears_retained_assignment_on_review_advance_without_borrowing_the_distinct_attempt_envelope_tuple()
    {
        var home = Path.Combine(Path.GetTempPath(), "baton-task-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            const string repository = "github.com/example/repo";
            var id = TaskCommand.TaskId(repository, 73);
            var now = DateTimeOffset.UtcNow;
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
            {
                Items = [new QueueItem
                {
                    Tag = id, Role = "review", Workspace = home,
                    SpecFile = BatonPaths.QueueSpecFile(id), Repository = repository, Issue = 73,
                    Stage = WorkStage.Review, State = QueueItemState.Launched,
                    // Stage advance to review cleared the implement-stage worker assignment; the
                    // implement selection itself remains visible.
                    StageSelections = [new QueueStageSelection
                    {
                        Stage = WorkStage.Implement, Adapter = "claude", Model = "opus", Effort = "high",
                    }],
                    WorkerAssignment = null,
                    AttemptId = new FleetAttemptId("review-attempt"),
                    AttemptEnvelope = new QueueAttemptEnvelope(
                        new FleetAttemptId("review-attempt"), null, id, 73, null, WorkStage.Review,
                        "review", "codex", "gpt-5.6-sol", "high", [], null, null, "admitted", null, null, null, now),
                    OwnedTask = new OwnedTaskSubmission(id, repository, 73, "digest", "recorded-owner", now),
                }],
            }, Ct);

            var jsonOutput = new StringWriter();
            await TaskCommand.ExecuteAsync(new TaskOptions(TaskVerb.Status, Id: id, Json: true), jsonOutput, Ct);
            using var json = JsonDocument.Parse(jsonOutput.ToString());
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("retainedWorkerAssignment").ValueKind);
            var selection = json.RootElement.GetProperty("initialWorkerSelection");
            Assert.Equal("claude", selection.GetProperty("adapter").GetString());
            Assert.Equal("opus", selection.GetProperty("model").GetString());

            var textOutput = new StringWriter();
            await TaskCommand.ExecuteAsync(new TaskOptions(TaskVerb.Status, Id: id), textOutput, Ct);
            var text = textOutput.ToString();
            Assert.DoesNotContain("retained worker assignment", text, StringComparison.Ordinal);
            Assert.DoesNotContain("gpt-5.6-sol", text, StringComparison.Ordinal);
            Assert.Contains("initial implement selection (retained plan): adapter=claude; model=opus; effort=high",
                text, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Status_ready_row_truthfully_retains_historical_assignment_without_claiming_liveness(bool hasAssignment)
    {
        var home = Path.Combine(Path.GetTempPath(), "baton-task-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            const string repository = "github.com/example/repo";
            var issue = hasAssignment ? 74 : 75;
            var id = TaskCommand.TaskId(repository, issue);
            var now = DateTimeOffset.UtcNow;
            var ready = new TaskReadyReceipt("ready", id, repository, issue, 100,
                new string('a', 40), "review", new string('b', 64), "passing", "checks", now, now);
            var assignment = hasAssignment
                ? new FrozenWorkerAssignment("decision-1", "claude", "opus", "high", "pool-hash-1",
                    "legacy-single-candidate", "Legacy one-triple tier frozen before launch.", now)
                : null;
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
            {
                Items = [new QueueItem
                {
                    Tag = id, Role = "implement", Workspace = home,
                    SpecFile = BatonPaths.QueueSpecFile(id), Repository = repository, Issue = issue,
                    Stage = WorkStage.Ready, State = QueueItemState.Queued,
                    WorkerAssignment = assignment,
                    OwnedTask = new OwnedTaskSubmission(id, repository, issue, "digest", "recorded-owner", now, ready),
                }],
            }, Ct);

            var jsonOutput = new StringWriter();
            await TaskCommand.ExecuteAsync(new TaskOptions(TaskVerb.Status, Id: id, Json: true), jsonOutput, Ct);
            using var json = JsonDocument.Parse(jsonOutput.ToString());
            Assert.Equal("ready-as-of", json.RootElement.GetProperty("state").GetString());
            var retainedAssignment = json.RootElement.GetProperty("retainedWorkerAssignment");

            var textOutput = new StringWriter();
            await TaskCommand.ExecuteAsync(new TaskOptions(TaskVerb.Status, Id: id), textOutput, Ct);
            var text = textOutput.ToString();
            if (hasAssignment)
            {
                Assert.Equal(JsonValueKind.Object, retainedAssignment.ValueKind);
                Assert.Equal("claude", retainedAssignment.GetProperty("adapter").GetString());
                Assert.Equal("opus", retainedAssignment.GetProperty("model").GetString());
                Assert.Equal("decision-1", retainedAssignment.GetProperty("decisionId").GetString());
                Assert.Contains(
                    "retained worker assignment (as-of; not proof of liveness or vendor use): "
                    + "adapter=claude; model=opus; effort=high", text, StringComparison.Ordinal);
            }
            else
            {
                Assert.Equal(JsonValueKind.Null, retainedAssignment.ValueKind);
                Assert.DoesNotContain("retained worker assignment", text, StringComparison.Ordinal);
            }
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Status_retains_truthful_assignment_for_a_blocked_halted_row()
    {
        var home = Path.Combine(Path.GetTempPath(), "baton-task-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            const string repository = "github.com/example/repo";
            var id = TaskCommand.TaskId(repository, 76);
            var now = DateTimeOffset.UtcNow;
            var assignment = new FrozenWorkerAssignment("decision-2", "codex", "gpt-5.6-sol", null,
                "pool-hash-2", "legacy-single-candidate", "Legacy one-triple tier frozen before launch.", now);
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
            {
                Items = [new QueueItem
                {
                    Tag = id, Role = "implement", Workspace = home,
                    SpecFile = BatonPaths.QueueSpecFile(id), Repository = repository, Issue = 76,
                    Stage = WorkStage.Continue, State = QueueItemState.Failed, Halted = true,
                    Error = "needs-operator",
                    WorkerAssignment = assignment,
                    OwnedTask = new OwnedTaskSubmission(id, repository, 76, "digest", "recorded-owner", now),
                }],
            }, Ct);

            var jsonOutput = new StringWriter();
            await TaskCommand.ExecuteAsync(new TaskOptions(TaskVerb.Status, Id: id, Json: true), jsonOutput, Ct);
            using var json = JsonDocument.Parse(jsonOutput.ToString());
            Assert.Equal("blocked", json.RootElement.GetProperty("state").GetString());
            var retainedAssignment = json.RootElement.GetProperty("retainedWorkerAssignment");
            Assert.Equal(7, retainedAssignment.EnumerateObject().Count());
            Assert.Equal("codex", retainedAssignment.GetProperty("adapter").GetString());
            Assert.Equal("gpt-5.6-sol", retainedAssignment.GetProperty("model").GetString());
            Assert.Equal(JsonValueKind.Null, retainedAssignment.GetProperty("effort").ValueKind);
            Assert.Equal("decision-2", retainedAssignment.GetProperty("decisionId").GetString());
            Assert.Equal("pool-hash-2", retainedAssignment.GetProperty("poolHash").GetString());
            Assert.Equal("legacy-single-candidate", retainedAssignment.GetProperty("closedReason").GetString());
            Assert.Equal(now, retainedAssignment.GetProperty("decidedAt").GetDateTimeOffset());

            var textOutput = new StringWriter();
            await TaskCommand.ExecuteAsync(new TaskOptions(TaskVerb.Status, Id: id), textOutput, Ct);
            Assert.Contains(
                "retained worker assignment (as-of; not proof of liveness or vendor use): "
                + "adapter=codex; model=gpt-5.6-sol; effort=none", textOutput.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Status_retains_truthful_assignment_for_a_retired_row()
    {
        var home = Path.Combine(Path.GetTempPath(), "baton-task-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            const string repository = "github.com/example/repo";
            var id = TaskCommand.TaskId(repository, 77);
            var now = DateTimeOffset.UtcNow;
            var assignment = new FrozenWorkerAssignment("decision-3", "claude", "sonnet", "medium",
                "pool-hash-3", "legacy-single-candidate", "Legacy one-triple tier frozen before launch.", now);
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
            {
                Items = [new QueueItem
                {
                    Tag = id, Role = "implement", Workspace = home,
                    SpecFile = BatonPaths.QueueSpecFile(id), Repository = repository, Issue = 77,
                    Stage = WorkStage.Continue, State = QueueItemState.Failed, Halted = true,
                    Retirement = new QueueRetirement(QueueRetirement.Operator, now, "handled"),
                    WorkerAssignment = assignment,
                    OwnedTask = new OwnedTaskSubmission(id, repository, 77, "digest", "recorded-owner", now),
                }],
            }, Ct);

            var jsonOutput = new StringWriter();
            await TaskCommand.ExecuteAsync(new TaskOptions(TaskVerb.Status, Id: id, Json: true), jsonOutput, Ct);
            using var json = JsonDocument.Parse(jsonOutput.ToString());
            Assert.Equal("retired", json.RootElement.GetProperty("state").GetString());
            var retainedAssignment = json.RootElement.GetProperty("retainedWorkerAssignment");
            Assert.Equal("claude", retainedAssignment.GetProperty("adapter").GetString());
            Assert.Equal("sonnet", retainedAssignment.GetProperty("model").GetString());
            Assert.Equal("medium", retainedAssignment.GetProperty("effort").GetString());

            var textOutput = new StringWriter();
            await TaskCommand.ExecuteAsync(new TaskOptions(TaskVerb.Status, Id: id), textOutput, Ct);
            Assert.Contains(
                "retained worker assignment (as-of; not proof of liveness or vendor use): "
                + "adapter=claude; model=sonnet; effort=medium", textOutput.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Status_worker_projections_are_isolated_from_unrelated_settings_and_tasks_and_leave_bytes_unchanged()
    {
        var home = Path.Combine(Path.GetTempPath(), "baton-task-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            const string repository = "github.com/example/repo";
            var repositoryIdentity = RepositoryIdentity.From("https://" + repository, null)!;
            await ConductorClaimStore.ClaimAsync(repositoryIdentity, "owner", home, cancellationToken: Ct);
            var id = TaskCommand.TaskId(repository, 78);
            var unrelatedId = TaskCommand.TaskId(repository, 79);
            var now = DateTimeOffset.UtcNow;
            var targetAssignment = new FrozenWorkerAssignment("decision-4", "claude", "opus", "high",
                "pool-hash-4", "legacy-single-candidate", "Legacy one-triple tier frozen before launch.", now);
            var unrelatedAssignment = new FrozenWorkerAssignment("decision-5", "codex", "gpt-5.6-sol", null,
                "pool-hash-5", "legacy-single-candidate", "Legacy one-triple tier frozen before launch.", now);
            Directory.CreateDirectory(Path.GetDirectoryName(BatonPaths.QueueSpecFile(id))!);
            await File.WriteAllTextAsync(BatonPaths.QueueSpecFile(id), "target brief", Ct);
            await File.WriteAllTextAsync(BatonPaths.QueueSpecFile(unrelatedId), "unrelated brief", Ct);
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
            {
                Items =
                [
                    new QueueItem
                    {
                        Tag = id, Role = "implement", Workspace = home,
                        SpecFile = BatonPaths.QueueSpecFile(id), Repository = repository, Issue = 78,
                        Stage = WorkStage.Implement,
                        StageSelections = [new QueueStageSelection
                        {
                            Stage = WorkStage.Implement, Adapter = "claude", Model = "opus", Effort = "high",
                        }],
                        WorkerAssignment = targetAssignment,
                        OwnedTask = new OwnedTaskSubmission(id, repository, 78, "digest", "recorded-owner", now),
                    },
                    new QueueItem
                    {
                        Tag = unrelatedId, Role = "implement", Workspace = home,
                        SpecFile = BatonPaths.QueueSpecFile(unrelatedId), Repository = repository, Issue = 79,
                        Stage = WorkStage.Implement,
                        StageSelections = [new QueueStageSelection
                        {
                            Stage = WorkStage.Implement, Adapter = "codex", Model = "gpt-5.6-sol",
                        }],
                        WorkerAssignment = unrelatedAssignment,
                        OwnedTask = new OwnedTaskSubmission(unrelatedId, repository, 79, "digest", "recorded-owner", now),
                    },
                ],
            }, Ct);
            ProjectCeilingStore.Set(home, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);
            var settings = new DaemonSettings
            {
                Queue = new QueueSettings
                {
                    Tiers = new Dictionary<string, QueueTierSettings> { ["engine"] = new() { Adapter = "agy" } },
                },
            };
            await DaemonSettingsStore.SaveAsync(settings, BatonPaths.SettingsFile, Ct);

            var queueBefore = await File.ReadAllBytesAsync(BatonPaths.QueueFile, Ct);
            var trustBefore = await File.ReadAllBytesAsync(ProjectCeilingStore.DefaultPath, Ct);
            var briefBefore = await File.ReadAllBytesAsync(BatonPaths.QueueSpecFile(id), Ct);
            var claimPath = BatonPaths.ConductorClaimFile(repositoryIdentity.FileSlug);
            var claimBefore = await File.ReadAllBytesAsync(claimPath, Ct);
            var settingsBefore = await File.ReadAllBytesAsync(BatonPaths.SettingsFile, Ct);

            var jsonOutput = new StringWriter();
            await TaskCommand.ExecuteAsync(new TaskOptions(TaskVerb.Status, Id: id, Json: true), jsonOutput, Ct);
            using var json = JsonDocument.Parse(jsonOutput.ToString());
            var selection = json.RootElement.GetProperty("initialWorkerSelection");
            Assert.Equal("claude", selection.GetProperty("adapter").GetString());
            Assert.Equal("opus", selection.GetProperty("model").GetString());
            var retainedAssignment = json.RootElement.GetProperty("retainedWorkerAssignment");
            Assert.Equal("claude", retainedAssignment.GetProperty("adapter").GetString());
            Assert.Equal("decision-4", retainedAssignment.GetProperty("decisionId").GetString());
            Assert.DoesNotContain("codex", jsonOutput.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("gpt-5.6-sol", jsonOutput.ToString(), StringComparison.Ordinal);

            Assert.Equal(queueBefore, await File.ReadAllBytesAsync(BatonPaths.QueueFile, Ct));
            Assert.Equal(trustBefore, await File.ReadAllBytesAsync(ProjectCeilingStore.DefaultPath, Ct));
            Assert.Equal(briefBefore, await File.ReadAllBytesAsync(BatonPaths.QueueSpecFile(id), Ct));
            Assert.Equal(claimBefore, await File.ReadAllBytesAsync(claimPath, Ct));
            Assert.Equal(settingsBefore, await File.ReadAllBytesAsync(BatonPaths.SettingsFile, Ct));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Status_reports_null_worker_projections_for_an_old_source_row_missing_both_fields()
    {
        var home = Path.Combine(Path.GetTempPath(), "baton-task-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            const string repository = "github.com/example/repo";
            var id = TaskCommand.TaskId(repository, 80);
            var specFile = BatonPaths.QueueSpecFile(id);
            Directory.CreateDirectory(Path.GetDirectoryName(BatonPaths.QueueFile)!);

            // Hand-written to simulate a row persisted before #2566 added StageSelections/WorkerAssignment
            // readers, so the JSON keys are entirely absent rather than written by the current encoder.
            // The two asserts below (`DoesNotContain`) are the actual proof of that absence; against
            // origin/main's TaskCommand.cs, which writes neither field at all, the later
            // `GetProperty("initialWorkerSelection"|"retainedWorkerAssignment")` calls would throw
            // KeyNotFoundException per JsonElement's documented contract -- a language guarantee, not
            // a claim this test re-verifies against the old binary.
            var raw = "{\"items\":[{"
                + "\"Tag\":" + JsonSerializer.Serialize(id) + ","
                + "\"Role\":\"implement\","
                + "\"Workspace\":" + JsonSerializer.Serialize(home) + ","
                + "\"SpecFile\":" + JsonSerializer.Serialize(specFile) + ","
                + "\"Repository\":" + JsonSerializer.Serialize(repository) + ","
                + "\"Issue\":80,"
                + "\"Stage\":\"Implement\","
                + "\"OwnedTask\":{"
                + "\"Id\":" + JsonSerializer.Serialize(id) + ","
                + "\"Repository\":" + JsonSerializer.Serialize(repository) + ","
                + "\"Issue\":80,"
                + "\"InputDigest\":\"digest\","
                + "\"ConductorHolder\":\"recorded-owner\","
                + "\"SubmittedAt\":\"2026-01-01T00:00:00Z\""
                + "}"
                + "}],\"held\":false}";
            await File.WriteAllTextAsync(BatonPaths.QueueFile, raw, Ct);
            Assert.DoesNotContain("StageSelections", raw, StringComparison.Ordinal);
            Assert.DoesNotContain("WorkerAssignment", raw, StringComparison.Ordinal);

            var jsonOutput = new StringWriter();
            await TaskCommand.ExecuteAsync(new TaskOptions(TaskVerb.Status, Id: id, Json: true), jsonOutput, Ct);
            using var json = JsonDocument.Parse(jsonOutput.ToString());
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("initialWorkerSelection").ValueKind);
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("retainedWorkerAssignment").ValueKind);
            Assert.Equal("implement", json.RootElement.GetProperty("stage").GetString());

            var textOutput = new StringWriter();
            await TaskCommand.ExecuteAsync(new TaskOptions(TaskVerb.Status, Id: id), textOutput, Ct);
            var text = textOutput.ToString();
            Assert.DoesNotContain("initial implement selection", text, StringComparison.Ordinal);
            Assert.DoesNotContain("retained worker assignment", text, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }
}
