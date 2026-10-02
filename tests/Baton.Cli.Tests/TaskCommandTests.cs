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
                TextWriter.Null, Resolve, Provision, Ct);
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
                project, size, spec), TextWriter.Null, Resolve, Provision, Ct));
            var conflict = await Assert.ThrowsAsync<CliArgumentException>(() =>
                TaskCommand.ExecuteAsync(new TaskOptions(TaskVerb.Submit, 48,
                    project, size), TextWriter.Null, Resolve, Provision, Ct));
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
                TaskCommand.ExecuteAsync(task, TextWriter.Null, Resolve, Provision, Ct));
            Assert.Equal(0, provisions);

            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with { Items = [] }, Ct);
            Assert.Equal(0, await TaskCommand.ExecuteAsync(task, TextWriter.Null, Resolve, Provision, Ct));
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
}
