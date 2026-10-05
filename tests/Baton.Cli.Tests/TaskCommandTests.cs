using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    [Theory]
    [InlineData("ENGINE", "engine")]
    [InlineData("Tooling", "tooling")]
    [InlineData("docs", "docs")]
    public void Parser_normalizes_each_supported_scope_and_forwards_implement_reason(string raw, string normalized)
    {
        var options = TaskOptionsParser.Parse(["submit", "--issue", "42", "--project", "C:/repo",
            "--declared-size", "small", "--size-rationale", "one cluster", "--scope", raw,
            "--model", "opus", "--reason", "matches the implementation tier"]);

        Assert.Equal(normalized, options.ScopeClass);
        Assert.Equal("matches the implementation tier", options.Reason);
    }

    [Fact]
    public void Parser_refuses_scope_reason_combinations_before_admission()
    {
        string[] baseArgs =
            ["submit", "--issue", "42", "--project", "C:/repo", "--declared-size", "small", "--size-rationale", "one cluster"];

        Assert.Throws<CliArgumentException>(() => TaskOptionsParser.Parse([.. baseArgs, "--reason", "why", "--model", "opus"]));
        Assert.Throws<CliArgumentException>(() => TaskOptionsParser.Parse([.. baseArgs, "--scope", "engine", "--reason", "why"]));
        Assert.Throws<CliArgumentException>(() => TaskOptionsParser.Parse([.. baseArgs, "--scope", "engine", "--model", "opus"]));
        Assert.Throws<CliArgumentException>(() => TaskOptionsParser.Parse([.. baseArgs, "--scope", "other"]));
        Assert.Throws<CliArgumentException>(() => TaskOptionsParser.Parse([.. baseArgs, "--scope", "engine", "--scope", "docs"]));
        Assert.Throws<CliArgumentException>(() => TaskOptionsParser.Parse([.. baseArgs, "--scope", "engine", "--model", "opus", "--reason", " "]));
        Assert.Throws<CliArgumentException>(() => TaskOptionsParser.Parse([.. baseArgs, "--scope", "engine", "--model", "opus", "--reason", "why", "--reason", "again"]));
    }

    [Fact]
    public void Parser_captures_stage_context_and_positive_global_brakes()
    {
        var options = TaskOptionsParser.Parse(["submit", "--issue", "42", "--project", "C:/repo",
            "--declared-size", "small", "--size-rationale", "one cluster", "--scope", "engine",
            "--stage", "implement", "--model", "opus", "--reason", "implementation fit",
            "--stage", "review", "--adapter", "codex", "--reason", "review independence",
            "--stage", "fix", "--effort", "high", "--reason", "fix findings", "--stage", "re-review", "--model", "opus",
            "--reason", "recheck findings", "--timeout", "15", "--max-tool-steps", "20",
            "--token-budget", "5000"]);

        Assert.Equal(15, options.TimeoutMinutes);
        Assert.Equal(20, options.MaxToolSteps);
        Assert.Equal(5000, options.TokenBudget);
        Assert.Null(options.Adapter);
        Assert.Equal(
            [WorkStage.Implement, WorkStage.Review, WorkStage.Fix, WorkStage.ReReview],
            options.StageSelections!.Select(selection => selection.Stage));
        var selections = options.StageSelections!;
        Assert.Equal("opus", selections[0].Model);
        Assert.Equal("codex", selections[1].Adapter);
        Assert.Equal("high", selections[2].Effort);
    }

    [Fact]
    public void Parser_refuses_empty_duplicate_conflicting_and_nonpositive_stage_input()
    {
        string[] baseArgs =
            ["submit", "--issue", "42", "--project", "C:/repo", "--declared-size", "small", "--size-rationale", "one cluster"];

        Assert.Throws<CliArgumentException>(() => TaskOptionsParser.Parse([.. baseArgs, "--stage", "review", "--stage", "fix", "--model", "opus"]));
        Assert.Throws<CliArgumentException>(() => TaskOptionsParser.Parse([.. baseArgs, "--stage", "review", "--model", "opus", "--model", "sonnet"]));
        Assert.Throws<CliArgumentException>(() => TaskOptionsParser.Parse([.. baseArgs, "--adapter", "claude", "--stage", "implement", "--adapter", "codex"]));
        Assert.Throws<CliArgumentException>(() => TaskOptionsParser.Parse([.. baseArgs, "--timeout", "0"]));
        Assert.Throws<CliArgumentException>(() => TaskOptionsParser.Parse([.. baseArgs, "--max-tool-steps", "0"]));
        Assert.Throws<CliArgumentException>(() => TaskOptionsParser.Parse([.. baseArgs, "--token-budget", "-1"]));
    }

    [Fact]
    public void Stage_and_cap_digest_is_ordered_typed_and_distinguishes_presence()
    {
        var size = new TaskSizeDeclaration(DeclaredTaskSize.Small, "one cluster");
        var implement = new QueueStageSelection { Stage = WorkStage.Implement, Model = "opus" };
        var review = new QueueStageSelection { Stage = WorkStage.Review, Adapter = "codex" };
        var first = TaskCommand.ComputeInputDigest("github.com/example/repo", 1, size, null, null,
            null, null, [implement, review], 10, 20, 5000);
        var reordered = TaskCommand.ComputeInputDigest("github.com/example/repo", 1, size, null, null,
            null, null, [review, implement], 10, 20, 5000);
        var changedStage = TaskCommand.ComputeInputDigest("github.com/example/repo", 1, size, null, null,
            null, null, [implement with { Model = "sonnet" }, review], 10, 20, 5000);
        var absentTimeout = TaskCommand.ComputeInputDigest("github.com/example/repo", 1, size, null, null,
            null, null, [implement, review], null, 20, 5000);

        Assert.Equal(first, reordered);
        Assert.NotEqual(first, changedStage);
        Assert.NotEqual(first, absentTimeout);
        Assert.NotEqual(first, TaskCommand.ComputeInputDigest(
            "github.com/example/repo", 1, size, null, implement, null, null));
        Assert.NotEqual(first, TaskCommand.ComputeInputDigest("github.com/example/repo", 1, size,
            Encoding.UTF8.GetBytes("brief\n--stage review\0spec-marker"), null, null, null,
            [implement, review], 10, 20, 5000));
        Assert.NotEqual(first, TaskCommand.ComputeInputDigest("github.com/example/repo", 1, size,
            null, null, null, null,
            [implement with { Reason = "why\n--stage fix\0marker" }, review], 10, 20, 5000));
        Assert.NotEqual(first, TaskCommand.ComputeInputDigest("github.com/example/repo", 1, size,
            null, null, null, null,
            [implement with { Model = string.Empty }, review], 10, 20, 5000));

        var aliasLeft = TaskCommand.ComputeInputDigest("github.com/example/repo", 1,
            new TaskSizeDeclaration(DeclaredTaskSize.Small, "x\nspec\npayload"), null, null,
            "engine", null, [implement, review], 10, 20, 5000);
        var aliasRight = TaskCommand.ComputeInputDigest("github.com/example/repo", 1,
            new TaskSizeDeclaration(DeclaredTaskSize.Small, "x"),
            Encoding.UTF8.GetBytes("payload\nno-spec\n"), null,
            "engine", null, [implement, review], 10, 20, 5000);
        Assert.NotEqual(aliasLeft, aliasRight);

        var absentModel = TaskCommand.ComputeInputDigest("github.com/example/repo", 1, size,
            null, null, "engine", null,
            [implement with { Model = null }, review], 10, 20, 5000);
        var emptyModel = TaskCommand.ComputeInputDigest("github.com/example/repo", 1, size,
            null, null, "engine", null,
            [implement with { Model = string.Empty }, review], 10, 20, 5000);
        Assert.NotEqual(absentModel, emptyModel);
    }

    [Fact]
    public async Task Task_admission_refuses_invalid_scope_inputs_before_resolution_or_provisioning()
    {
        var home = Path.Combine(Path.GetTempPath(), "baton-task-scope-refusal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var project = Path.Combine(home, "source");
            Directory.CreateDirectory(project);
            var repository = RepositoryIdentity.From("https://github.com/example/repo", null)!;
            var resolutions = 0;
            var provisions = 0;
            Task<RepositoryIdentity?> Resolve(string path, CancellationToken token)
            {
                resolutions++;
                return Task.FromResult<RepositoryIdentity?>(repository);
            }

            Task<IssueWorktreeProvisioner.ProvisionedIssueWorktree> Provision(
                int issue, string source, string? root, string repo, bool lifecycle,
                TextWriter writer, CancellationToken token)
            {
                provisions++;
                throw new InvalidOperationException("provision must not run");
            }

            var size = new TaskSizeDeclaration(DeclaredTaskSize.Small, "one cluster");
            var invalid = new TaskOptions(TaskVerb.Submit, 42, project, size,
                ScopeClass: "ENGINE", Reason: "why");
            await Assert.ThrowsAsync<CliArgumentException>(() =>
                TaskCommand.ExecuteAsync(invalid, TextWriter.Null, Resolve, Provision, Ct));
            Assert.Equal(0, resolutions);
            Assert.Equal(0, provisions);

            invalid = invalid with { ScopeClass = "engine", Reason = null, Model = "opus" };
            await Assert.ThrowsAsync<CliArgumentException>(() =>
                TaskCommand.ExecuteAsync(invalid, TextWriter.Null, Resolve, Provision, Ct));
            Assert.Equal(0, resolutions);
            Assert.Equal(0, provisions);

            invalid = invalid with { ScopeClass = "not-a-scope", Model = null };
            await Assert.ThrowsAsync<CliArgumentException>(() =>
                TaskCommand.ExecuteAsync(invalid, TextWriter.Null, Resolve, Provision, Ct));
            Assert.Equal(0, resolutions);
            Assert.Equal(0, provisions);

            invalid = invalid with { ScopeClass = null, Reason = "why" };
            await Assert.ThrowsAsync<CliArgumentException>(() =>
                TaskCommand.ExecuteAsync(invalid, TextWriter.Null, Resolve, Provision, Ct));
            Assert.Equal(0, resolutions);
            Assert.Equal(0, provisions);

            invalid = invalid with { ScopeClass = "engine", Model = "opus", Reason = "  " };
            await Assert.ThrowsAsync<CliArgumentException>(() =>
                TaskCommand.ExecuteAsync(invalid, TextWriter.Null, Resolve, Provision, Ct));
            Assert.Equal(0, resolutions);
            Assert.Equal(0, provisions);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Owned_submission_retains_each_stage_selection_and_dispatch_brake()
    {
        var home = Path.Combine(Path.GetTempPath(), "baton-task-stages-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var project = Path.Combine(home, "source");
            var workspace = Path.Combine(home, "w2620");
            var spec = Path.Combine(home, "brief.md");
            Directory.CreateDirectory(project);
            await File.WriteAllTextAsync(spec, "owned stage routing", Ct);
            var repository = RepositoryIdentity.From("https://github.com/example/repo", null)!;
            await ConductorClaimStore.ClaimAsync(repository, "owner", home, cancellationToken: Ct);

            Task<RepositoryIdentity?> Resolve(string path, CancellationToken token) => Task.FromResult<RepositoryIdentity?>(repository);
            Task<IssueWorktreeProvisioner.ProvisionedIssueWorktree> Provision(
                int issue, string source, string? root, string repo, bool lifecycle, TextWriter writer, CancellationToken token)
            {
                Directory.CreateDirectory(workspace);
                ProjectCeilingStore.Set(workspace, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);
                return Task.FromResult(new IssueWorktreeProvisioner.ProvisionedIssueWorktree(workspace, "2620-lane"));
            }

            var options = new TaskOptions(TaskVerb.Submit, 2620, project,
                new TaskSizeDeclaration(DeclaredTaskSize.Small, "one routing cluster"), spec,
                StageSelections:
                [
                    new QueueStageSelection { Stage = WorkStage.Implement, Adapter = "claude", Model = "opus", Effort = "high" },
                    new QueueStageSelection { Stage = WorkStage.Review, Adapter = "codex", Model = "gpt-6.1-sol", Effort = "high" },
                    new QueueStageSelection { Stage = WorkStage.Fix, Adapter = "claude", Model = "opus", Effort = "high" },
                    new QueueStageSelection { Stage = WorkStage.ReReview, Adapter = "codex", Model = "gpt-6.1-sol", Effort = "high" },
                ],
                TimeoutMinutes: 15, MaxToolSteps: 20, TokenBudget: 5000);

            Assert.Equal(0, await TaskCommand.ExecuteAsync(
                options, TextWriter.Null, Resolve, Provision, Ct, IssuePreparationRunner.NoCollisions));

            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(15, item.TimeoutMinutes);
            Assert.Equal(20, item.MaxToolSteps);
            Assert.Equal(5000, item.TokenBudget);
            Assert.Equal(
                [WorkStage.Implement, WorkStage.Review, WorkStage.Fix, WorkStage.ReReview],
                item.StageSelections!.Select(selection => selection.Stage));
            var settings = await DaemonSettingsStore.LoadAsync(BatonPaths.SettingsFile, Ct);
            Assert.Equal("codex", QueueTierTable.ResolveForStage(
                item, WorkStage.Review, settings.Queue, WorkerRoleCatalog.QueueTierFor,
                WorkerRoleCatalog.QueueTierForRole).Adapter);
            Assert.Equal("claude", QueueTierTable.ResolveForStage(
                item, WorkStage.Fix, settings.Queue, WorkerRoleCatalog.QueueTierFor,
                WorkerRoleCatalog.QueueTierForRole).Adapter);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task New_domain_replay_and_all_stage_cap_conflicts_preserve_preparation_snapshot()
    {
        var home = Path.Combine(Path.GetTempPath(), "baton-task-new-domain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var project = Path.Combine(home, "source");
            var workspace = Path.Combine(home, "w2620");
            var spec = Path.Combine(home, "brief.md");
            Directory.CreateDirectory(project);
            await File.WriteAllTextAsync(spec, "frozen new-domain brief\n--stage review\0marker", Ct);
            var repository = RepositoryIdentity.From("https://github.com/example/repo", null)!;
            await ConductorClaimStore.ClaimAsync(repository, "conductor-one", home, cancellationToken: Ct);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var provisions = 0;

            Task<RepositoryIdentity?> Resolve(string path, CancellationToken token) =>
                Task.FromResult<RepositoryIdentity?>(repository);
            async Task<IssueWorktreeProvisioner.ProvisionedIssueWorktree> Provision(
                int issue, string source, string? root, string repo, bool lifecycle,
                TextWriter writer, CancellationToken token)
            {
                Interlocked.Increment(ref provisions);
                entered.SetResult();
                await release.Task.WaitAsync(token);
                Directory.CreateDirectory(workspace);
                ProjectCeilingStore.Set(workspace, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);
                return new(workspace, "2620-lane");
            }

            var selections = new QueueStageSelection[]
            {
                new()
                {
                    Stage = WorkStage.Implement, Adapter = "claude", Model = "opus", Effort = "high",
                    Reason = "implementation reason",
                },
                new()
                {
                    Stage = WorkStage.Review, Adapter = "codex", Model = "gpt-5.6-sol", Effort = "high",
                    Reason = "review reason",
                },
                new()
                {
                    Stage = WorkStage.Fix, Adapter = "claude", Model = "opus", Effort = "medium",
                    Reason = "fix reason",
                },
                new()
                {
                    Stage = WorkStage.ReReview, Adapter = "codex", Model = "gpt-5.6-sol", Effort = "medium",
                    Reason = "re-review reason",
                },
                new()
                {
                    Stage = WorkStage.Continue, Adapter = "codex", Model = "gpt-5.6-sol", Effort = "low",
                    Reason = "continue reason",
                },
            };
            var firstOptions = new TaskOptions(TaskVerb.Submit, 2620, project,
                new TaskSizeDeclaration(DeclaredTaskSize.Small, "one new-domain routing cluster"), spec,
                ScopeClass: "engine", StageSelections: selections,
                TimeoutMinutes: 15, MaxToolSteps: 20, TokenBudget: 5000);

            var first = TaskCommand.ExecuteAsync(
                firstOptions, TextWriter.Null, Resolve, Provision, Ct, IssuePreparationRunner.NoCollisions);
            await entered.Task.WaitAsync(Ct);

            var equivalentBareImplement = firstOptions with
            {
                Adapter = "claude",
                Model = "opus",
                Effort = "high",
                Reason = "implementation reason",
                StageSelections = selections.Where(selection => selection.Stage != WorkStage.Implement).ToArray(),
            };
            Assert.Equal(0, await TaskCommand.ExecuteAsync(
                equivalentBareImplement, TextWriter.Null, Resolve, Provision, Ct, IssuePreparationRunner.NoCollisions));
            Assert.Equal(1, provisions);

            async Task AssertRefusedWithoutAnotherProvision(TaskOptions candidate)
            {
                var refusal = await Assert.ThrowsAsync<CliArgumentException>(() => TaskCommand.ExecuteAsync(
                    candidate, TextWriter.Null, Resolve, Provision, Ct, IssuePreparationRunner.NoCollisions));
                Assert.Contains("already retains different explicit submission input", refusal.Message,
                    StringComparison.Ordinal);
                Assert.Equal(1, provisions);
            }

            static IEnumerable<TaskOptions> ChangedSelections(TaskOptions original,
                IReadOnlyList<QueueStageSelection> baseline)
            {
                foreach (var stage in baseline)
                {
                    var changedAdapter = stage.Adapter == "claude" ? "codex" : "claude";
                    yield return original with
                    {
                        StageSelections = baseline.Select(item => item.Stage == stage.Stage
                            ? item with { Adapter = changedAdapter } : item).ToArray(),
                    };
                    yield return original with
                    {
                        StageSelections = baseline.Select(item => item.Stage == stage.Stage
                            ? item with { Adapter = null } : item).ToArray(),
                    };
                    yield return original with
                    {
                        StageSelections = baseline.Select(item => item.Stage == stage.Stage
                            ? item with { Model = "gpt-6.1-sol" } : item).ToArray(),
                    };
                    yield return original with
                    {
                        StageSelections = baseline.Select(item => item.Stage == stage.Stage
                            ? item with { Model = null } : item).ToArray(),
                    };
                    yield return original with
                    {
                        StageSelections = baseline.Select(item => item.Stage == stage.Stage
                            ? item with { Effort = stage.Effort == "low" ? "medium" : "low" } : item).ToArray(),
                    };
                    yield return original with
                    {
                        StageSelections = baseline.Select(item => item.Stage == stage.Stage
                            ? item with { Effort = null } : item).ToArray(),
                    };
                    yield return original with
                    {
                        StageSelections = baseline.Select(item => item.Stage == stage.Stage
                            ? item with { Reason = item.Reason + " changed" } : item).ToArray(),
                    };
                    // Scope plus any explicit stage axis requires a reason, so a valid reason drop
                    // removes that stage's whole selection rather than creating invalid input.
                    yield return original with
                    {
                        StageSelections = baseline.Where(item => item.Stage != stage.Stage).ToArray(),
                    };
                }
            }

            foreach (var candidate in ChangedSelections(firstOptions, selections))
                await AssertRefusedWithoutAnotherProvision(candidate);
            await AssertRefusedWithoutAnotherProvision(firstOptions with { TimeoutMinutes = 16 });
            await AssertRefusedWithoutAnotherProvision(firstOptions with { TimeoutMinutes = null });
            await AssertRefusedWithoutAnotherProvision(firstOptions with { MaxToolSteps = 21 });
            await AssertRefusedWithoutAnotherProvision(firstOptions with { MaxToolSteps = null });
            await AssertRefusedWithoutAnotherProvision(firstOptions with { TokenBudget = 5001 });
            await AssertRefusedWithoutAnotherProvision(firstOptions with { TokenBudget = null });

            release.SetResult();
            Assert.Equal(0, await first);
            foreach (var candidate in ChangedSelections(firstOptions, selections))
                await AssertRefusedWithoutAnotherProvision(candidate);
            await AssertRefusedWithoutAnotherProvision(firstOptions with { TimeoutMinutes = 16 });
            await AssertRefusedWithoutAnotherProvision(firstOptions with { TimeoutMinutes = null });
            await AssertRefusedWithoutAnotherProvision(firstOptions with { MaxToolSteps = 21 });
            await AssertRefusedWithoutAnotherProvision(firstOptions with { MaxToolSteps = null });
            await AssertRefusedWithoutAnotherProvision(firstOptions with { TokenBudget = 5001 });
            await AssertRefusedWithoutAnotherProvision(firstOptions with { TokenBudget = null });

            var retained = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(TaskPreparationState.Prepared, retained.IssuePreparation!.State);
            Assert.Equal("frozen new-domain brief\n--stage review\0marker", retained.Instructions);
            Assert.Equal(15, retained.TimeoutMinutes);
            Assert.Equal(20, retained.MaxToolSteps);
            Assert.Equal(5000, retained.TokenBudget);
            Assert.Equal(selections.Select(selection => selection.Stage),
                retained.StageSelections!.Select(selection => selection.Stage));
            Assert.Equal(selections.Select(selection => selection.Reason),
                retained.StageSelections!.Select(selection => selection.Reason));
            Assert.Equal(1, provisions);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
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

    [Fact]
    public async Task Fresh_submission_reports_unknown_daemon_without_rewriting_the_task()
    {
        var home = Path.Combine(Path.GetTempPath(), "baton-task-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var project = Path.Combine(home, "source");
            var workspace = Path.Combine(home, "w2582");
            var spec = Path.Combine(home, "brief.md");
            Directory.CreateDirectory(project);
            await File.WriteAllTextAsync(spec, "fresh brief", Ct);
            var repository = RepositoryIdentity.From("https://github.com/example/repo", null)!;
            await ConductorClaimStore.ClaimAsync(repository, "conductor", home, cancellationToken: Ct);
            Task<RepositoryIdentity?> Resolve(string path, CancellationToken token) => Task.FromResult<RepositoryIdentity?>(repository);
            Task<IssueWorktreeProvisioner.ProvisionedIssueWorktree> Provision(
                int issue, string source, string? root, string repo, bool lifecycle,
                TextWriter writer, CancellationToken token)
            {
                Directory.CreateDirectory(workspace);
                ProjectCeilingStore.Set(workspace, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);
                return Task.FromResult(new IssueWorktreeProvisioner.ProvisionedIssueWorktree(workspace, "2582-lane"));
            }

            var now = new DateTimeOffset(2026, 10, 2, 23, 0, 0, TimeSpan.Zero);
            Directory.CreateDirectory(Path.GetDirectoryName(BatonPaths.FleetHeartbeatFile)!);
            await File.WriteAllTextAsync(BatonPaths.FleetHeartbeatFile, JsonSerializer.Serialize(new
            {
                tickCompletedAt = now.AddSeconds(-10),
                identity = new { pid = 1234, processStartTime = now.AddMinutes(-1) },
            }), Ct);
            var processStartCalls = 0;
            DateTimeOffset DeniedProcessStart(int _)
            {
                processStartCalls++;
                throw new Win32Exception(5);
            }

            var options = new TaskOptions(TaskVerb.Submit, 2582, project,
                new TaskSizeDeclaration(DeclaredTaskSize.Small, "one acceptance cluster"), spec);
            var submissionOutput = new StringWriter();
            Assert.Equal(0, await TaskCommand.ExecuteAsync(options, submissionOutput, Resolve, Provision, Ct,
                IssuePreparationRunner.NoCollisions, DeniedProcessStart, () => now));
            Assert.Contains("queued (daemon-observation-unknown)", submissionOutput.ToString(), StringComparison.Ordinal);
            Assert.Contains("next: daemon-tick", submissionOutput.ToString(), StringComparison.Ordinal);
            Assert.Contains("daemon: unknown (reason process-identity-unverifiable)", submissionOutput.ToString(),
                StringComparison.Ordinal);
            Assert.Equal(1, processStartCalls);

            var beforeStatus = await File.ReadAllBytesAsync(BatonPaths.QueueFile, Ct);
            var statusOutput = new StringWriter();
            var id = TaskCommand.TaskId(repository.Value, 2582);
            await TaskCommand.ExecuteAsync(new TaskOptions(TaskVerb.Status, Id: id, Json: true), statusOutput,
                Resolve, Provision, Ct, processStartTimeAccessor: DeniedProcessStart, utcNow: () => now);
            using var status = JsonDocument.Parse(statusOutput.ToString());
            Assert.Equal("queued", status.RootElement.GetProperty("state").GetString());
            Assert.Equal("daemon-observation-unknown", status.RootElement.GetProperty("reason").GetString());
            Assert.Equal("daemon-tick", status.RootElement.GetProperty("nextTrigger").GetString());
            Assert.Equal("unknown", status.RootElement.GetProperty("daemon").GetProperty("availability").GetString());
            Assert.Equal(2, processStartCalls);
            Assert.Equal(beforeStatus, await File.ReadAllBytesAsync(BatonPaths.QueueFile, Ct));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Theory]
    [InlineData("held", "queued", "queue-held", "daemon-tick")]
    [InlineData("preparing", "preparing", "preparation-in-progress", "preparation-completion")]
    [InlineData("blocked", "blocked", "preparation-blocked", "conductor-judgment")]
    public async Task Status_preserves_priority_states_receipts_and_evidence_when_daemon_is_unknown(
        string caseName, string expectedState, string expectedReason, string expectedTrigger)
    {
        var home = Path.Combine(Path.GetTempPath(), "baton-task-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            const string repository = "github.com/example/repo";
            var now = new DateTimeOffset(2026, 10, 2, 23, 0, 0, TimeSpan.Zero);
            var id = TaskCommand.TaskId(repository, 2582);
            var ready = new TaskReadyReceipt("ready", id, repository, 2582, 50,
                new string('a', 40), "review-attempt", new string('b', 64), "passing", "checks-observation", now, now);
            var blocked = new TaskBlockedDisposition("retained-blocker", "preserved blocker evidence", now, "obligation-2582");
            using var currentProcess = Process.GetCurrentProcess();
            var preparation = caseName switch
            {
                "preparing" => new QueueIssuePreparation(TaskPreparationState.Preparing, now,
                    ProcessId: currentProcess.Id, ProcessStartedAt: currentProcess.StartTime.ToUniversalTime()),
                "blocked" => new QueueIssuePreparation(TaskPreparationState.Blocked, now, Reason: expectedReason,
                    ExpectedBranch: "2582-lane", ReservationId: "reservation-2582"),
                _ => null,
            };
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
            {
                Held = true,
                Items = [new QueueItem
                {
                    Tag = id,
                    Role = "implement",
                    Workspace = home,
                    SpecFile = BatonPaths.QueueSpecFile(id),
                    Repository = repository,
                    Issue = 2582,
                    Stage = WorkStage.Implement,
                    State = QueueItemState.Queued,
                    IssuePreparation = preparation,
                    OwnedTask = new OwnedTaskSubmission(id, repository, 2582, "digest", "recorded-owner", now,
                        Ready: caseName == "held" ? null : ready, Blocked: blocked),
                }],
            }, Ct);
            Directory.CreateDirectory(Path.GetDirectoryName(BatonPaths.FleetHeartbeatFile)!);
            await File.WriteAllTextAsync(BatonPaths.FleetHeartbeatFile, JsonSerializer.Serialize(new
            {
                tickCompletedAt = now.AddSeconds(-10),
                identity = new { pid = 1234, processStartTime = now.AddMinutes(-1) },
            }), Ct);
            var before = await File.ReadAllBytesAsync(BatonPaths.QueueFile, Ct);
            var processStartCalls = 0;
            DateTimeOffset DeniedProcessStart(int _)
            {
                processStartCalls++;
                throw new Win32Exception(5);
            }
            var output = new StringWriter();
            await TaskCommand.ExecuteAsync(new TaskOptions(TaskVerb.Status, Id: id, Json: true), output,
                static (_, _) => throw new NotSupportedException(),
                static (_, _, _, _, _, _, _) => throw new NotSupportedException(), Ct,
                processStartTimeAccessor: DeniedProcessStart, utcNow: () => now);
            using var json = JsonDocument.Parse(output.ToString());
            Assert.Equal(expectedState, json.RootElement.GetProperty("state").GetString());
            Assert.Equal(expectedReason, json.RootElement.GetProperty("reason").GetString());
            Assert.Equal(expectedTrigger, json.RootElement.GetProperty("nextTrigger").GetString());
            Assert.Equal("unknown", json.RootElement.GetProperty("daemon").GetProperty("availability").GetString());
            if (caseName == "held")
                Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("ready").ValueKind);
            else
            {
                Assert.True(json.RootElement.TryGetProperty("ready", out var readyElement), output.ToString());
                Assert.True(readyElement.TryGetProperty("Id", out var readyId), output.ToString());
                Assert.Equal("ready", readyId.GetString());
            }
            Assert.True(json.RootElement.TryGetProperty("blocked", out var blockedElement), output.ToString());
            Assert.Equal("retained-blocker", blockedElement.GetProperty("ReasonCode").GetString());
            Assert.Equal("preserved blocker evidence", json.RootElement.GetProperty("blocked").GetProperty("Evidence").GetString());
            Assert.Equal(1, processStartCalls);

            var text = new StringWriter();
            await TaskCommand.ExecuteAsync(new TaskOptions(TaskVerb.Status, Id: id), text,
                static (_, _) => throw new NotSupportedException(),
                static (_, _, _, _, _, _, _) => throw new NotSupportedException(), Ct,
                processStartTimeAccessor: DeniedProcessStart, utcNow: () => now);
            Assert.Contains($"Task {id}: {expectedState} ({expectedReason})", text.ToString(), StringComparison.Ordinal);
            Assert.Contains($"next: {expectedTrigger}", text.ToString(), StringComparison.Ordinal);
            if (caseName != "held")
                Assert.Contains("ready receipt: ready", text.ToString(), StringComparison.Ordinal);
            Assert.Contains("retained-blocker; preserved blocker evidence", text.ToString(), StringComparison.Ordinal);
            Assert.Equal(2, processStartCalls);
            Assert.Equal(before, await File.ReadAllBytesAsync(BatonPaths.QueueFile, Ct));
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
    [InlineData("invalid-tick-time", "unavailable", "heartbeat-unavailable", "daemon-unavailable")]
    [InlineData("wrong-tick-shape", "unavailable", "heartbeat-unavailable", "daemon-unavailable")]
    [InlineData("wrong-pid-shape", "unavailable", "heartbeat-unavailable", "daemon-unavailable")]
    [InlineData("wrong-start-shape", "unavailable", "heartbeat-unavailable", "daemon-unavailable")]
    [InlineData("invalid-start-time", "unavailable", "heartbeat-unavailable", "daemon-unavailable")]
    [InlineData("pid-overflow", "unavailable", "heartbeat-unavailable", "daemon-unavailable")]
    [InlineData("pid-fractional", "unavailable", "heartbeat-unavailable", "daemon-unavailable")]
    [InlineData("invalid-pid", "unavailable", "process-unavailable", "daemon-unavailable")]
    [InlineData("absent-process", "unavailable", "process-unavailable", "daemon-unavailable")]
    [InlineData("exited-process", "unavailable", "process-unavailable", "daemon-unavailable")]
    [InlineData("non-access-win32", "unknown", "process-identity-unverifiable", "daemon-observation-unknown")]
    [InlineData("stale-denied", "unavailable", "heartbeat-stale", "daemon-unavailable")]
    [InlineData("future-denied", "unavailable", "heartbeat-in-future", "daemon-unavailable")]
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
                "stale" or "stale-denied" => now.AddSeconds(-90),
                "future" or "future-denied" => now.AddSeconds(1),
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
                "invalid-tick-time" => "{\"tickCompletedAt\":\"not-a-date\",\"identity\":{\"pid\":1234,\"processStartTime\":\""
                    + recordedStart.ToString("O") + "\"}}",
                "wrong-tick-shape" => "{\"tickCompletedAt\":{},\"identity\":{\"pid\":1234,\"processStartTime\":\""
                    + recordedStart.ToString("O") + "\"}}",
                "wrong-pid-shape" => "{\"tickCompletedAt\":\"" + heartbeatAt.ToString("O")
                    + "\",\"identity\":{\"pid\":\"1234\",\"processStartTime\":\""
                    + recordedStart.ToString("O") + "\"}}",
                "wrong-start-shape" => "{\"tickCompletedAt\":\"" + heartbeatAt.ToString("O")
                    + "\",\"identity\":{\"pid\":1234,\"processStartTime\":[]}}",
                "invalid-start-time" => "{\"tickCompletedAt\":\"" + heartbeatAt.ToString("O")
                    + "\",\"identity\":{\"pid\":1234,\"processStartTime\":\"not-a-date\"}}",
                "pid-overflow" => "{\"tickCompletedAt\":\"" + heartbeatAt.ToString("O")
                    + "\",\"identity\":{\"pid\":3000000000,\"processStartTime\":\""
                    + recordedStart.ToString("O") + "\"}}",
                "pid-fractional" => "{\"tickCompletedAt\":\"" + heartbeatAt.ToString("O")
                    + "\",\"identity\":{\"pid\":1234.5,\"processStartTime\":\""
                    + recordedStart.ToString("O") + "\"}}",
                "equivalent-offset" => "{\"tickCompletedAt\":\"" + heartbeatAt.ToOffset(TimeSpan.FromHours(-4)).ToString("O")
                    + "\",\"identity\":{\"pid\":1234,\"processStartTime\":\""
                    + recordedStart.ToOffset(TimeSpan.FromHours(-4)).ToString("O") + "\"}}",
                _ => JsonSerializer.Serialize(new
                {
                    tickCompletedAt = heartbeatAt,
                    identity = new { pid = caseName == "invalid-pid" ? 0 : 1234, processStartTime = recordedStart },
                }),
            };
            await File.WriteAllTextAsync(BatonPaths.FleetHeartbeatFile, heartbeat, Ct);
            var before = await File.ReadAllBytesAsync(BatonPaths.QueueFile, Ct);
            var processStartCalls = 0;
            DateTimeOffset ProcessStart(int _) => ProcessStartCore();
            DateTimeOffset ProcessStartCore()
            {
                processStartCalls++;
                return caseName switch
                {
                    "mismatch" => recordedStart.AddSeconds(2),
                    "absent-process" => throw new ArgumentException("not found"),
                    "exited-process" => throw new InvalidOperationException("exited"),
                    "non-access-win32" or "stale-denied" or "future-denied" => throw new Win32Exception(
                        caseName is "non-access-win32" ? 87 : 5),
                    _ => recordedStart,
                };
            }
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
            if (caseName is "future" or "future-denied" or "malformed" or "invalid-tick-time" or "wrong-tick-shape")
            {
                Assert.Equal(JsonValueKind.Null, daemon.GetProperty("observedAt").ValueKind);
                Assert.Equal(JsonValueKind.Null, daemon.GetProperty("ageSeconds").ValueKind);
            }
            else
            {
                Assert.Equal(heartbeatAt, daemon.GetProperty("observedAt").GetDateTimeOffset());
                Assert.True(daemon.GetProperty("ageSeconds").GetInt32() >= 0);
            }
            var expectedProcessStartCalls = caseName is "mismatch" or "equivalent-offset" or "absent-process"
                or "exited-process" or "non-access-win32" ? 1 : 0;
            Assert.Equal(expectedProcessStartCalls, processStartCalls);

            var textOutput = new StringWriter();
            await TaskCommand.ExecuteAsync(new TaskOptions(TaskVerb.Status, Id: id), textOutput,
                Resolve, Provision, Ct, utcNow: () => now, processStartTimeAccessor: ProcessStart);
            if (daemon.GetProperty("observedAt").ValueKind == JsonValueKind.Null)
                Assert.Contains("(no observation)", textOutput.ToString(), StringComparison.Ordinal);
            else if (expectedAvailability != "recently-observed")
                Assert.Contains("recorded heartbeat time", textOutput.ToString(), StringComparison.Ordinal);
            Assert.Equal(before, await File.ReadAllBytesAsync(BatonPaths.QueueFile, Ct));
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
                Assert.Contains("queued (daemon-observation-unknown)",
                    textOutput.ToString(), StringComparison.Ordinal);
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
    [InlineData(QueueItemState.Failed, "older failure", true, true, "retired", "none")]
    [InlineData(QueueItemState.Cancelled, "cancelled", false, false, "cancelled", "none")]
    [InlineData(QueueItemState.Queued, null, false, false, "queued", "daemon-tick")]
    [InlineData(QueueItemState.Launched, null, false, false, "running", "daemon-tick")]
    [InlineData(QueueItemState.Queued, null, false, true, "ready-as-of", "conductor-handoff")]
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
                    PullRequest = oldReady ? 50 : null,
                    Retirement = retired ? new QueueRetirement(QueueRetirement.Operator, now, "retained") : null,
                    OwnedTask = new OwnedTaskSubmission(id, repository, 49, "digest", "recorded-owner", now, receipt),
                }],
            }, Ct);
            Directory.CreateDirectory(Path.GetDirectoryName(BatonPaths.FleetHeartbeatFile)!);
            await File.WriteAllTextAsync(BatonPaths.FleetHeartbeatFile, JsonSerializer.Serialize(new
            {
                tickCompletedAt = now.AddSeconds(-10),
                identity = new { pid = 1234, processStartTime = now.AddMinutes(-1) },
            }), Ct);
            DateTimeOffset DeniedProcessStart(int _) => throw new Win32Exception(5);
            var before = await File.ReadAllTextAsync(BatonPaths.QueueFile, Ct);
            var output = new StringWriter();
            await TaskCommand.ExecuteAsync(new TaskOptions(TaskVerb.Status, Id: id, Json: true), output,
                static (_, _) => throw new NotSupportedException(),
                static (_, _, _, _, _, _, _) => throw new NotSupportedException(), Ct,
                processStartTimeAccessor: DeniedProcessStart, utcNow: () => now);
            using var json = JsonDocument.Parse(output.ToString());
            Assert.Equal(expectedState, json.RootElement.GetProperty("state").GetString());
            Assert.Equal(expectedTrigger, json.RootElement.GetProperty("nextTrigger").GetString());
            var handoff = json.RootElement.GetProperty("conductorHandoff");
            Assert.Equal(expectedState is "ready-as-of" or "stale" or "blocked"
                ? JsonValueKind.Object : JsonValueKind.Null, handoff.ValueKind);
            if (handoff.ValueKind == JsonValueKind.Object)
            {
                Assert.Equal(id, handoff.GetProperty("taskId").GetString());
                Assert.Equal(repository, handoff.GetProperty("repository").GetString());
                Assert.Equal(49, handoff.GetProperty("issue").GetInt32());
                Assert.Equal("recorded-owner", handoff.GetProperty("holder").GetString());
                Assert.False(handoff.GetProperty("mergeGrant").GetBoolean());
                Assert.Equal(expectedState switch
                {
                    "ready-as-of" => "reconcile-review-and-fresh-forge-gates-then-merge-under-existing-authority",
                    "stale" => "reassess-current-readiness",
                    _ => "judge-retained-blocker",
                }, handoff.GetProperty("responsibility").GetString());
                var readiness = handoff.GetProperty("readiness");
                Assert.Equal(oldReady ? JsonValueKind.Object : JsonValueKind.Null, readiness.ValueKind);
                if (readiness.ValueKind == JsonValueKind.Object)
                {
                    Assert.Equal("ready", readiness.GetProperty("id").GetString());
                    Assert.Equal(id, readiness.GetProperty("taskId").GetString());
                    Assert.Equal("github.com/example/repo", readiness.GetProperty("repository").GetString());
                    Assert.Equal(49, readiness.GetProperty("issue").GetInt32());
                    Assert.Equal(50, readiness.GetProperty("pullRequest").GetInt32());
                    Assert.Equal(new string('a', 40), readiness.GetProperty("headSha").GetString());
                    Assert.Equal(now, readiness.GetProperty("observedAt").GetDateTimeOffset());
                    Assert.Equal("as-of", readiness.GetProperty("evidence").GetString());
                    Assert.Equal("complete", readiness.GetProperty("binding").GetProperty("status").GetString());
                }
            }
            Assert.Equal("unknown", json.RootElement.GetProperty("daemon").GetProperty("availability").GetString());
            Assert.Equal("recorded-owner", json.RootElement.GetProperty("conductorHolder").GetString());
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("blocked").ValueKind);
            Assert.Equal(retired ? JsonValueKind.Object : JsonValueKind.Null,
                json.RootElement.GetProperty("retirement").ValueKind);
            var text = new StringWriter();
            await TaskCommand.ExecuteAsync(new TaskOptions(TaskVerb.Status, Id: id), text,
                static (_, _) => throw new NotSupportedException(),
                static (_, _, _, _, _, _, _) => throw new NotSupportedException(), Ct,
                processStartTimeAccessor: DeniedProcessStart, utcNow: () => now);
            Assert.Contains(expectedState, text.ToString(), StringComparison.Ordinal);
            Assert.Contains($"next: {expectedTrigger}", text.ToString(), StringComparison.Ordinal);
            if (expectedState is "ready-as-of" or "stale" or "blocked")
            {
                Assert.Contains("conductor handoff: recorded-owner", text.ToString(), StringComparison.Ordinal);
                Assert.Contains("not a merge grant", text.ToString(), StringComparison.Ordinal);
            }
            if (oldReady)
            {
                if (expectedState == "ready-as-of")
                    Assert.Equal(50, handoff.GetProperty("pullRequest").GetInt32());
                Assert.Contains($"ready receipt{(retired ? " (historical)" : "")}: ready at {now:O}; head {new string('a', 40)} (as-of); binding complete",
                    text.ToString(), StringComparison.Ordinal);
                if (retired)
                {
                    Assert.Equal(JsonValueKind.Null, handoff.ValueKind);
                    Assert.Equal(JsonValueKind.Object, json.RootElement.GetProperty("ready").ValueKind);
                }
                Assert.Contains("PR head: not observed", text.ToString(), StringComparison.Ordinal);
                Assert.Equal("unclaimed", json.RootElement.GetProperty("ownership").GetString());
                Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("currentConductorHolder").ValueKind);
            }
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
    public async Task Status_binds_retained_receipt_subjects_and_reports_missing_row_binding_in_text_and_json()
    {
        const string repository = "github.com/example/repo";
        var home = Path.Combine(Path.GetTempPath(), "baton-task-receipt-binding-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var observedAt = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
            var id = TaskCommand.TaskId(repository, 51);
            var foreignId = TaskCommand.TaskId(repository, 57);
            var repositoryIdentity = RepositoryIdentity.From("https://" + repository, null)!;
            var receipt = new TaskReadyReceipt("foreign-ready", foreignId, repository, 57, 77,
                new string('c', 40), "review", new string('d', 64), "passing", "checks", observedAt, observedAt);
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
            {
                Items = [new QueueItem
                {
                    Tag = id, Role = "implement", Workspace = home, SpecFile = BatonPaths.QueueSpecFile(id),
                    Repository = repository, Issue = 51, PullRequest = null, Stage = WorkStage.Ready,
                    State = QueueItemState.Queued,
                    OwnedTask = new OwnedTaskSubmission(id, repository, 51, "digest", "recorded-owner", observedAt, receipt),
                }],
            }, Ct);
            await ConductorClaimStore.ClaimAsync(repositoryIdentity, "current-owner", home, cancellationToken: Ct);
            var queueBefore = await File.ReadAllBytesAsync(BatonPaths.QueueFile, Ct);
            var claimFile = BatonPaths.ConductorClaimFile(repositoryIdentity.FileSlug);
            var claimBefore = await File.ReadAllBytesAsync(claimFile, Ct);

            var jsonOutput = new StringWriter();
            await TaskCommand.ExecuteAsync(new TaskOptions(TaskVerb.Status, Id: id, Json: true), jsonOutput, Ct);
            using var json = JsonDocument.Parse(jsonOutput.ToString());
            var status = json.RootElement;
            Assert.Equal("stale", status.GetProperty("state").GetString());
            Assert.Equal("readiness-receipt-binding-mismatch", status.GetProperty("reason").GetString());
            Assert.Equal("holder-changed", status.GetProperty("ownership").GetString());
            Assert.Equal("current-owner", status.GetProperty("currentConductorHolder").GetString());
            var handoff = status.GetProperty("conductorHandoff");
            Assert.Equal(id, handoff.GetProperty("taskId").GetString());
            Assert.Equal(repository, handoff.GetProperty("repository").GetString());
            Assert.Equal(51, handoff.GetProperty("issue").GetInt32());
            Assert.Equal(JsonValueKind.Null, handoff.GetProperty("pullRequest").ValueKind);
            Assert.Equal("recorded-owner", handoff.GetProperty("holder").GetString());
            Assert.Equal("current-owner", handoff.GetProperty("currentHolder").GetString());
            Assert.Equal("reassess-current-readiness", handoff.GetProperty("responsibility").GetString());
            var retained = handoff.GetProperty("readiness");
            Assert.Equal("foreign-ready", retained.GetProperty("id").GetString());
            Assert.Equal(foreignId, retained.GetProperty("taskId").GetString());
            Assert.Equal(repository, retained.GetProperty("repository").GetString());
            Assert.Equal(57, retained.GetProperty("issue").GetInt32());
            Assert.Equal(77, retained.GetProperty("pullRequest").GetInt32());
            Assert.Equal(new string('c', 40), retained.GetProperty("headSha").GetString());
            Assert.Equal(observedAt, retained.GetProperty("observedAt").GetDateTimeOffset());
            Assert.Equal("mismatched", retained.GetProperty("binding").GetProperty("status").GetString());
            Assert.Contains("taskId", retained.GetProperty("binding").GetProperty("mismatches").EnumerateArray()
                .Select(value => value.GetString()));
            Assert.Contains("issue", retained.GetProperty("binding").GetProperty("mismatches").EnumerateArray()
                .Select(value => value.GetString()));
            Assert.Contains("rowPullRequest", retained.GetProperty("binding").GetProperty("missing").EnumerateArray()
                .Select(value => value.GetString()));

            var textOutput = new StringWriter();
            await TaskCommand.ExecuteAsync(new TaskOptions(TaskVerb.Status, Id: id), textOutput, Ct);
            var text = textOutput.ToString();
            Assert.Contains("conductor handoff: recorded-owner (holder-changed)", text, StringComparison.Ordinal);
            Assert.Contains($"ready receipt: foreign-ready at {observedAt:O}; head {new string('c', 40)} (as-of); binding mismatched",
                text, StringComparison.Ordinal);
            Assert.Contains("mismatches: taskId, issue", text, StringComparison.Ordinal);
            Assert.Contains("missing: rowPullRequest", text, StringComparison.Ordinal);
            Assert.Contains($"receipt subjects: task {foreignId}; repository {repository}; issue #57; PR #77",
                text, StringComparison.Ordinal);
            Assert.Contains("not a merge grant", text, StringComparison.Ordinal);
            Assert.Equal(queueBefore, await File.ReadAllBytesAsync(BatonPaths.QueueFile, Ct));
            Assert.Equal(claimBefore, await File.ReadAllBytesAsync(claimFile, Ct));

            var exactReceipt = receipt with { Id = "current-ready", TaskId = id, Issue = 51, PullRequest = 77 };
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
            {
                Items = [snapshot.Items[0] with
                {
                    PullRequest = 77,
                    OwnedTask = snapshot.Items[0].OwnedTask! with { Ready = exactReceipt },
                    RequiredCheckEvidenceWait = new RequiredCheckEvidenceWait(
                        exactReceipt.HeadSha, observedAt, observedAt, 1, "required checks unreadable"),
                }],
                PullRequestObservations = [new QueuePullRequestObservation(
                    repository, 77, "open", exactReceipt.HeadSha, observedAt, observedAt, null)],
            }, Ct);
            var evidenceWaitQueueBefore = await File.ReadAllBytesAsync(BatonPaths.QueueFile, Ct);
            var evidenceWaitText = new StringWriter();
            await TaskCommand.ExecuteAsync(new TaskOptions(TaskVerb.Status, Id: id), evidenceWaitText, Ct);
            Assert.Contains("Task " + id + ": stale (readiness-evidence-no-longer-current)",
                evidenceWaitText.ToString(), StringComparison.Ordinal);
            Assert.Contains($"ready receipt: current-ready at {observedAt:O}; head {exactReceipt.HeadSha} (as-of); binding complete",
                evidenceWaitText.ToString(), StringComparison.Ordinal);
            Assert.Equal(evidenceWaitQueueBefore, await File.ReadAllBytesAsync(BatonPaths.QueueFile, Ct));
            Assert.Equal(claimBefore, await File.ReadAllBytesAsync(claimFile, Ct));

            var partialReceipt = exactReceipt with { Id = "legacy-partial", PullRequest = 0 };
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
            {
                Items = [snapshot.Items[0] with
                {
                    PullRequest = null,
                    OwnedTask = snapshot.Items[0].OwnedTask! with { Ready = partialReceipt },
                    RequiredCheckEvidenceWait = null,
                }],
                PullRequestObservations = null,
            }, Ct);
            var partialQueueBefore = await File.ReadAllBytesAsync(BatonPaths.QueueFile, Ct);
            var partialJsonOutput = new StringWriter();
            await TaskCommand.ExecuteAsync(new TaskOptions(TaskVerb.Status, Id: id, Json: true), partialJsonOutput, Ct);
            using var partialJson = JsonDocument.Parse(partialJsonOutput.ToString());
            Assert.Equal("stale", partialJson.RootElement.GetProperty("state").GetString());
            Assert.Equal("readiness-receipt-binding-incomplete", partialJson.RootElement.GetProperty("reason").GetString());
            var partialHandoff = partialJson.RootElement.GetProperty("conductorHandoff");
            Assert.Equal(id, partialHandoff.GetProperty("taskId").GetString());
            Assert.Equal("legacy-partial", partialHandoff.GetProperty("readiness").GetProperty("id").GetString());
            Assert.Equal("incomplete", partialHandoff.GetProperty("readiness").GetProperty("binding")
                .GetProperty("status").GetString());
            Assert.Contains("pullRequest", partialHandoff.GetProperty("readiness").GetProperty("binding")
                .GetProperty("missing").EnumerateArray().Select(value => value.GetString()));
            Assert.Contains("rowPullRequest", partialHandoff.GetProperty("readiness").GetProperty("binding")
                .GetProperty("missing").EnumerateArray().Select(value => value.GetString()));
            var partialTextOutput = new StringWriter();
            await TaskCommand.ExecuteAsync(new TaskOptions(TaskVerb.Status, Id: id), partialTextOutput, Ct);
            Assert.Contains($"ready receipt: legacy-partial at {observedAt:O}; head {partialReceipt.HeadSha} (as-of); binding incomplete",
                partialTextOutput.ToString(), StringComparison.Ordinal);
            Assert.Contains("missing: rowPullRequest, pullRequest", partialTextOutput.ToString(), StringComparison.Ordinal);
            Assert.Equal(partialQueueBefore, await File.ReadAllBytesAsync(BatonPaths.QueueFile, Ct));
            Assert.Equal(claimBefore, await File.ReadAllBytesAsync(claimFile, Ct));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Theory]
    [InlineData("complete", "ready-as-of", "complete", null)]
    [InlineData("foreign-repository", "stale", "mismatched", "repository")]
    [InlineData("foreign-pr", "stale", "mismatched", "pullRequest")]
    [InlineData("null-id", "stale", "incomplete", "id")]
    [InlineData("absent-id", "stale", "incomplete", "id")]
    [InlineData("empty-id", "stale", "incomplete", "id")]
    [InlineData("whitespace-id", "stale", "incomplete", "id")]
    [InlineData("default-time", "stale", "incomplete", "observedAt")]
    [InlineData("absent-time", "stale", "incomplete", "observedAt")]
    [InlineData("required-evidence-wait", "stale", "complete", null)]
    [InlineData("cancelled-history", "cancelled", "complete", null)]
    public async Task Status_discriminates_single_receipt_defects_and_terminal_history_without_writes_or_calls(
        string caseName, string expectedState, string expectedBinding, string? defect)
    {
        const string repository = "github.com/example/repo";
        var home = Path.Combine(Path.GetTempPath(), "baton-task-receipt-control-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var at = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
            var id = TaskCommand.TaskId(repository, 51);
            var head = new string('c', 40);
            var identity = RepositoryIdentity.From("https://" + repository, null)!;
            var receipt = new TaskReadyReceipt("current-ready", id, repository, 51, 77,
                head, "review", new string('d', 64), "passing", "checks", at, at);
            receipt = caseName switch
            {
                "foreign-repository" => receipt with { Repository = "github.com/foreign/repo" },
                "foreign-pr" => receipt with { PullRequest = 78 },
                "null-id" => receipt with { Id = null! },
                "empty-id" => receipt with { Id = "" },
                "whitespace-id" => receipt with { Id = " \t " },
                "default-time" or "absent-time" => receipt with { ReadyObservedAt = default },
                _ => receipt,
            };
            var room = Path.Combine(BatonPaths.Rooms, "retained-review");
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
            {
                Items = [new QueueItem
                {
                    Tag = id, Role = "implement", Workspace = home, SpecFile = BatonPaths.QueueSpecFile(id),
                    Repository = repository, Issue = 51, PullRequest = 77, Stage = WorkStage.Ready,
                    State = caseName == "cancelled-history" ? QueueItemState.Cancelled : QueueItemState.Queued,
                    Error = null,
                    RequiredCheckEvidenceWait = caseName == "required-evidence-wait"
                        ? new RequiredCheckEvidenceWait(head, at, at, 1, "required checks unreadable") : null,
                    OwnedTask = new OwnedTaskSubmission(id, repository, 51, "digest", "recorded-owner", at, receipt),
                }],
                PullRequestObservations = [new QueuePullRequestObservation(repository, 77, "open", head, at, at, null)],
            }, Ct);
            if (caseName is "absent-id" or "absent-time" or "null-id")
            {
                var persisted = JsonNode.Parse(await File.ReadAllTextAsync(BatonPaths.QueueFile, Ct))!;
                var retained = persisted["items"]![0]!["OwnedTask"]!["Ready"]!.AsObject();
                if (caseName == "null-id") retained["Id"] = null;
                else Assert.True(retained.Remove(caseName == "absent-id" ? "Id" : "ReadyObservedAt"));
                await File.WriteAllTextAsync(BatonPaths.QueueFile, persisted.ToJsonString(), Ct);
                receipt = caseName == "absent-id" ? receipt with { Id = null! } : receipt;
            }
            await ConductorClaimStore.ClaimAsync(identity, "recorded-owner", home, cancellationToken: Ct);
            await QueueDecisionLedgerStore.AppendAsync(new QueueDecisionEntry(at, id, QueueDecisionEntry.Launched,
                null, 0, 10, 1, Tier: "engine", Adapter: "codex", Model: "retained-model", Effort: "high", Room: room),
                null, BatonPaths.QueueDecisionLedgerFile, Ct);
            var evidenceDirectory = Path.Combine(room, "artifacts", "execution_review");
            Directory.CreateDirectory(evidenceDirectory);
            await File.WriteAllTextAsync(Path.Combine(evidenceDirectory, CostLedgerStore.VerdictOutputName),
                "{\"reviewedRef\":\"" + head + "\",\"decision\":\"approve\"}", Ct);
            await File.WriteAllTextAsync(BatonPaths.RoomBindingsFile(room),
                "{\"reviewer\":{\"Adapter\":\"codex\",\"Model\":\"retained-model\"}}", Ct);
            await File.WriteAllTextAsync(Path.Combine(room, BatonPaths.FlowLogFileName),
                "{\"retainedReviewExecution\":\"review\"}\n", Ct);
            Directory.CreateDirectory(BatonPaths.WorkerLaunchConfig);
            await File.WriteAllTextAsync(Path.Combine(BatonPaths.WorkerLaunchConfig, "claude-settings.json"), "{}", Ct);
            var before = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var path in Directory.GetFiles(home, "*", SearchOption.AllDirectories))
                before.Add(path, await File.ReadAllBytesAsync(path, Ct));

            var resolveCalls = 0;
            var provisionCalls = 0;
            var commandCalls = 0;
            Task<RepositoryIdentity?> Resolve(string _, CancellationToken __)
            {
                resolveCalls++;
                throw new InvalidOperationException("Status must not resolve a forge repository.");
            }
            Task<IssueWorktreeProvisioner.ProvisionedIssueWorktree> Provision(
                int _, string __, string? ___, string ____, bool _____, TextWriter ______, CancellationToken _______)
            {
                provisionCalls++;
                throw new InvalidOperationException("Status must not provision a worker.");
            }
            Task<(int ExitCode, string Output)> Run(string _, IReadOnlyList<string> __, string ___, CancellationToken ____)
            {
                commandCalls++;
                throw new InvalidOperationException("Status must not invoke forge, vendor, or preparation commands.");
            }
            var jsonOutput = new StringWriter();
            Assert.Equal(0, await TaskCommand.ExecuteAsync(new TaskOptions(TaskVerb.Status, Id: id, Json: true),
                jsonOutput, Resolve, Provision, Ct, Run, utcNow: () => at));
            var textOutput = new StringWriter();
            Assert.Equal(0, await TaskCommand.ExecuteAsync(new TaskOptions(TaskVerb.Status, Id: id),
                textOutput, Resolve, Provision, Ct, Run, utcNow: () => at));
            using var json = JsonDocument.Parse(jsonOutput.ToString());
            var status = json.RootElement;
            var expectedTrigger = expectedState switch
            {
                "ready-as-of" => "conductor-handoff",
                "stale" => "conductor-reassessment",
                _ => "none",
            };
            Assert.Equal(expectedState, status.GetProperty("state").GetString());
            Assert.Equal(expectedTrigger, status.GetProperty("nextTrigger").GetString());
            Assert.Equal(id, status.GetProperty("taskId").GetString());
            Assert.Equal(repository, status.GetProperty("repository").GetString());
            Assert.Equal(51, status.GetProperty("issue").GetInt32());
            Assert.Equal(77, status.GetProperty("pullRequest").GetInt32());
            Assert.Equal(head, status.GetProperty("headSha").GetString());
            Assert.Equal("recorded-owner", status.GetProperty("currentConductorHolder").GetString());
            Assert.Equal(JsonValueKind.Null, status.GetProperty("latestChecks").GetProperty("error").ValueKind);
            var text = textOutput.ToString();
            Assert.Contains($"Task {id}: {expectedState}", text, StringComparison.Ordinal);
            Assert.Contains($"next: {expectedTrigger}; PR head: {head}", text, StringComparison.Ordinal);
            Assert.Contains("stage: ready; PR: #77", text, StringComparison.Ordinal);
            Assert.Contains($"receipt subjects: task {receipt.TaskId}; repository {receipt.Repository}; issue #{receipt.Issue}; PR #{receipt.PullRequest}",
                text, StringComparison.Ordinal);
            Assert.Contains($"{receipt.Id} at {receipt.ReadyObservedAt:O}; head {head} (as-of); binding {expectedBinding}",
                text, StringComparison.Ordinal);
            var handoff = status.GetProperty("conductorHandoff");
            if (expectedState == "cancelled")
            {
                Assert.Equal(JsonValueKind.Null, handoff.ValueKind);
                Assert.DoesNotContain("conductor handoff:", text, StringComparison.Ordinal);
                Assert.Contains("ready receipt (historical)", text, StringComparison.Ordinal);
                Assert.Equal(receipt.Id, status.GetProperty("ready").GetProperty("Id").GetString());
                Assert.Equal(head, status.GetProperty("ready").GetProperty("HeadSha").GetString());
            }
            else
            {
                Assert.Equal(id, handoff.GetProperty("taskId").GetString());
                Assert.Equal(repository, handoff.GetProperty("repository").GetString());
                Assert.Equal(51, handoff.GetProperty("issue").GetInt32());
                Assert.Equal(77, handoff.GetProperty("pullRequest").GetInt32());
                Assert.Equal("recorded-owner", handoff.GetProperty("holder").GetString());
                Assert.Equal("recorded-owner", handoff.GetProperty("currentHolder").GetString());
                Assert.False(handoff.GetProperty("mergeGrant").GetBoolean());
                Assert.Equal(expectedState == "ready-as-of"
                    ? "reconcile-review-and-fresh-forge-gates-then-merge-under-existing-authority"
                    : "reassess-current-readiness", handoff.GetProperty("responsibility").GetString());
                Assert.Contains("not a merge grant", text, StringComparison.Ordinal);
                var retained = handoff.GetProperty("readiness");
                Assert.Equal(receipt.Id, retained.GetProperty("id").GetString());
                Assert.Equal(receipt.TaskId, retained.GetProperty("taskId").GetString());
                Assert.Equal(receipt.Repository, retained.GetProperty("repository").GetString());
                Assert.Equal(receipt.Issue, retained.GetProperty("issue").GetInt32());
                Assert.Equal(receipt.PullRequest, retained.GetProperty("pullRequest").GetInt32());
                Assert.Equal(head, retained.GetProperty("headSha").GetString());
                Assert.Equal(receipt.ReadyObservedAt, retained.GetProperty("observedAt").GetDateTimeOffset());
                Assert.Equal("as-of", retained.GetProperty("evidence").GetString());
                var binding = retained.GetProperty("binding");
                Assert.Equal(expectedBinding, binding.GetProperty("status").GetString());
                Assert.Equal(expectedBinding == "mismatched" ? [defect] : Array.Empty<string?>(),
                    binding.GetProperty("mismatches").EnumerateArray().Select(value => value.GetString()));
                Assert.Equal(expectedBinding == "incomplete" ? [defect] : Array.Empty<string?>(),
                    binding.GetProperty("missing").EnumerateArray().Select(value => value.GetString()));
                if (defect is not null)
                {
                    var reason = "readiness-receipt-binding-" + (expectedBinding == "mismatched" ? "mismatch" : "incomplete");
                    Assert.Equal(reason, status.GetProperty("reason").GetString());
                    Assert.Contains(reason, text, StringComparison.Ordinal);
                    Assert.Contains((expectedBinding == "mismatched" ? "mismatches: " : "missing: ") + defect,
                        text, StringComparison.Ordinal);
                }
                if (caseName == "required-evidence-wait")
                {
                    Assert.Equal("readiness-evidence-no-longer-current", status.GetProperty("reason").GetString());
                    Assert.Contains("stale (readiness-evidence-no-longer-current)", text, StringComparison.Ordinal);
                }
            }
            Assert.Equal(0, resolveCalls);
            Assert.Equal(0, provisionCalls);
            Assert.Equal(0, commandCalls);
            Assert.Equal(before.Keys.Order(StringComparer.Ordinal),
                Directory.GetFiles(home, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal));
            foreach (var (path, bytes) in before)
                Assert.Equal(bytes, await File.ReadAllBytesAsync(path, Ct));
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
                ExpectedState: "ready-as-of", ExpectedTrigger: "conductor-handoff", ExpectedCurrent: false,
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
                Halted: false, Retirement: (QueueRetirement?)null, Ready: ReadyReceipt(repository, 51),
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
                        Error = null,
                        PullRequest = testCase.ExpectedState is "stale" or "ready-as-of" ? 77 : null,
                        Retirement = testCase.Retirement, OwnedTask = owned,
                        StoppedWorkJudgment = judgment,
                    }],
                    PullRequestObservations = testCase.ExpectedState == "stale"
                        ? [new QueuePullRequestObservation(repository, 77, "open", "new-head",
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
                if (testCase.ExpectedState == "stale")
                {
                    var handoff = status.GetProperty("conductorHandoff");
                    Assert.Equal(id, handoff.GetProperty("taskId").GetString());
                    Assert.Equal(77, handoff.GetProperty("pullRequest").GetInt32());
                    var readiness = handoff.GetProperty("readiness");
                    Assert.Equal(id, readiness.GetProperty("taskId").GetString());
                    Assert.Equal("github.com/example/repo", readiness.GetProperty("repository").GetString());
                    Assert.Equal(51, readiness.GetProperty("issue").GetInt32());
                    Assert.Equal(77, readiness.GetProperty("pullRequest").GetInt32());
                    Assert.Equal(new string('a', 40), readiness.GetProperty("headSha").GetString());
                    Assert.Equal("complete", readiness.GetProperty("binding").GetProperty("status").GetString());
                    Assert.Equal("observed-pr-head-changed", status.GetProperty("reason").GetString());
                }
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
        Assert.Equal("7843616743270887fd33e21fe183c9e45f9a4817d3d0d5096febc1c044ca58a8", oneAxisSelection);
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
    public void Scoped_digest_is_distinct_and_collision_safe_without_changing_old_branches()
    {
        var size = new TaskSizeDeclaration(DeclaredTaskSize.Small, "ab");
        var selection = new QueueStageSelection { Stage = WorkStage.Implement, Model = "opus" };
        var first = TaskCommand.ComputeInputDigest("github.com/example/repo", 1, size, null, selection,
            "engine", "cd");
        Assert.Equal("026052a33efe14e44cc68fe311626b29ac23f026f441f8cdc896c8cf979dd646", first);
        var changedScope = TaskCommand.ComputeInputDigest("github.com/example/repo", 1, size, null, selection,
            "tooling", "cd");
        var changedReason = TaskCommand.ComputeInputDigest("github.com/example/repo", 1, size, null, selection,
            "engine", "different");
        var boundaryRight = TaskCommand.ComputeInputDigest("github.com/example/repo", 1,
            new TaskSizeDeclaration(DeclaredTaskSize.Small, "a"), null, selection, "engine", "bcd");

        Assert.NotEqual(first, changedScope);
        Assert.NotEqual(first, changedReason);
        Assert.NotEqual(first, boundaryRight);
        Assert.NotEqual(first,
            TaskCommand.ComputeInputDigest("github.com/example/repo", 1, size, null, selection));
    }

    [Fact]
    public async Task Changed_scope_or_reason_conflicts_before_a_second_reservation_or_provision()
    {
        var home = Path.Combine(Path.GetTempPath(), "baton-task-scope-conflict-" + Guid.NewGuid().ToString("N"));
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
                return Task.FromResult(new IssueWorktreeProvisioner.ProvisionedIssueWorktree(workspace, "63-lane"));
            }

            var first = new TaskOptions(TaskVerb.Submit, 63, project,
                new TaskSizeDeclaration(DeclaredTaskSize.Small, "one issue"), spec,
                Adapter: "claude", Model: "opus", Effort: "high", ScopeClass: "ENGINE", Reason: "first reason");
            Assert.Equal(0, await TaskCommand.ExecuteAsync(first, TextWriter.Null, Resolve, Provision, Ct,
                IssuePreparationRunner.NoCollisions));
            var scopeConflict = await Assert.ThrowsAsync<CliArgumentException>(() =>
                TaskCommand.ExecuteAsync(first with { ScopeClass = "tooling" }, TextWriter.Null,
                    Resolve, Provision, Ct, IssuePreparationRunner.NoCollisions));
            var reasonConflict = await Assert.ThrowsAsync<CliArgumentException>(() =>
                TaskCommand.ExecuteAsync(first with { Reason = "second reason" }, TextWriter.Null,
                    Resolve, Provision, Ct, IssuePreparationRunner.NoCollisions));
            Assert.Contains("already retains different explicit submission input", scopeConflict.Message, StringComparison.Ordinal);
            Assert.Contains("already retains different explicit submission input", reasonConflict.Message, StringComparison.Ordinal);
            Assert.Equal(1, provisions);
            var retained = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal("engine", retained.ScopeClass);
            Assert.Equal("first reason", retained.StageSelections!.Single().Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
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

            // Exact replay is idempotent, uses the same observation seam, and still cannot reach a
            // vendor or provision another lane.
            var now = new DateTimeOffset(2026, 10, 2, 23, 0, 0, TimeSpan.Zero);
            Directory.CreateDirectory(Path.GetDirectoryName(BatonPaths.FleetHeartbeatFile)!);
            await File.WriteAllTextAsync(BatonPaths.FleetHeartbeatFile, JsonSerializer.Serialize(new
            {
                tickCompletedAt = now.AddSeconds(-10),
                identity = new { pid = 1234, processStartTime = now.AddMinutes(-1) },
            }), Ct);
            DateTimeOffset DeniedProcessStart(int _) => throw new Win32Exception(5);
            var replayOutput = new StringWriter();
            Assert.Equal(0, await TaskCommand.ExecuteAsync(initial, replayOutput, Resolve, Provision, Ct,
                IssuePreparationRunner.NoCollisions, DeniedProcessStart, () => now));
            Assert.Contains("queued (daemon-observation-unknown)", replayOutput.ToString(), StringComparison.Ordinal);
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
                    PullRequest = 100,
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
