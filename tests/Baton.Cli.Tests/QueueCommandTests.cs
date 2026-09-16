using System.Text.Json;
using Baton.Accounting;
using Baton.Cli.Daemon;
using Baton.Cli.Tests.TestSupport;
using Baton.Domain;
using Baton.Queue;
using Baton.Status;
using Baton.Store;
using Baton.Templates;
using Baton.Tests.Shared;
using Baton.Vendors;
using Xunit;

namespace Baton.Cli.Tests;

/// <summary>
/// The two <c>baton queue</c> verbs whose ORDER of operations is the behaviour (#1939 review): what
/// <c>add</c> is allowed to have touched by the time it refuses, and whether <c>list</c> answers the
/// question spec/baton.md §13 sends a reader to it with.
/// </summary>
/// <remarks>
/// Isolated the same way <c>QueueSchedulerServiceTests</c> is, and for the reason stated there.
/// </remarks>
public sealed class QueueCommandTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter> { ["shell"] = new ShellCommandWorkerAdapter() };

    [Theory]
    [InlineData("agy", null, "gemini-3.8-flash-high")]
    [InlineData("claude", "sonnet", "sonnet")]
    [InlineData("claude", "claude-opus-4-8", "claude-opus-4-8")]
    [InlineData("claude", "sonnet[1m]", "sonnet[1m]")]
    [InlineData("agy", "future-agy-model", "future-agy-model")]
    [InlineData("claude", "claude-sonnet-4-6", "claude-sonnet-4-6")]
    [InlineData("agy", "claude-sonnet-4-6", "claude-sonnet-4-6")]
    public async Task Add_honours_an_unscoped_adapters_own_model_rules(
        string adapter, string? model, string displayedModel)
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var brief = Path.Combine(home, "brief.md");
            await File.WriteAllTextAsync(brief, "implement this", Ct);
            var output = new StringWriter();

            var exit = await QueueCommand.ExecuteAsync(
                new QueueOptions(QueueVerb.Add, Tag: "explicit", Role: "implement",
                    SpecFilePath: brief, WorkspaceDirectory: home, Adapter: adapter, Model: model),
                output, Ct);

            Assert.Equal(0, exit);
            Assert.Contains($"tier: {adapter} / {displayedModel} / role default effort",
                output.ToString(), StringComparison.Ordinal);
            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(adapter, item.Adapter);
            Assert.Equal(model, item.Model);
            Assert.Null(item.Effort);
            Assert.NotNull(item.WorkerAssignment);
            Assert.Equal(adapter, item.WorkerAssignment.Adapter);
            Assert.Equal(displayedModel, item.WorkerAssignment.Model);
            Assert.Null(item.WorkerAssignment.Effort);
            Assert.Equal("legacy-single-candidate", item.WorkerAssignment.ClosedReason);
            Assert.Contains($"assignment: {adapter}/{displayedModel}/role-default", output.ToString(), StringComparison.Ordinal);
            Assert.Equal(WorkspaceOrigins.OperatorSupplied, item.WorkspaceOrigin);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Add_prints_override_reason_and_persists_it_for_ordinary_adds()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var brief = Path.Combine(home, "brief.md");
            await File.WriteAllTextAsync(brief, "implement this", Ct);
            var output = new StringWriter();

            var exit = await QueueCommand.ExecuteAsync(
                new QueueOptions(
                    QueueVerb.Add, Tag: "override-reason", Role: "implement",
                    SpecFilePath: brief, WorkspaceDirectory: home, ScopeClass: "engine",
                    Adapter: "codex", Model: "gpt-5.6-terra", Effort: "medium",
                    Reason: "measured worker fit"),
                output,
                Ct);

            Assert.Equal(0, exit);
            Assert.Contains("override: measured worker fit", output.ToString(), StringComparison.Ordinal);

            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal("measured worker fit", item.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Add_refuses_an_unpinned_claude_before_any_queue_side_effect()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var workspace = Path.Combine(home, "workspace");
            var brief = Path.Combine(home, "brief.md");
            Directory.CreateDirectory(workspace);
            await File.WriteAllTextAsync(brief, "implement this", Ct);

            var refusal = await Assert.ThrowsAsync<CliArgumentException>(() => QueueCommand.ExecuteAsync(
                new QueueOptions(
                    QueueVerb.Add, Tag: "unpinned-claude", Role: "implement", SpecFilePath: brief,
                    WorkspaceDirectory: workspace, Adapter: "claude"),
                TextWriter.Null,
                Ct));

            Assert.Contains("standing model policy", refusal.Message, StringComparison.Ordinal);
            Assert.Equal("pass --model sonnet, --model opus, or --model haiku.", refusal.TryInvocation);
            Assert.False(File.Exists(BatonPaths.QueueFile));
            Assert.False(Directory.Exists(BatonPaths.QueueSpecsDirectory));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Add_persists_declared_requirements_and_list_exposes_migration_coverage()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var brief = Path.Combine(home, "brief.md");
            await File.WriteAllTextAsync(brief, "implement this", Ct);

            await QueueCommand.ExecuteAsync(
                new QueueOptions(QueueVerb.Add, Tag: "declared", Role: "implement",
                    SpecFilePath: brief, WorkspaceDirectory: home, Requirements: ["file-write", "shell"]),
                TextWriter.Null, Ct);

            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(["file-write", "shell"], item.Requirements);
            Assert.Equal("admitted", item.LastAdmission!.Result);
            Assert.Equal(["file-write", "shell"], item.LastAdmission.Requested);
            var output = new StringWriter();
            await QueueCommand.ExecuteAsync(new QueueOptions(QueueVerb.List), output, Ct);
            Assert.Contains("requirements: file-write, shell", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Requirement coverage: 1/1 declared", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Add_refuses_a_requirement_mismatch_before_issue_worktree_provisioning()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var brief = Path.Combine(home, "brief.md");
            await File.WriteAllTextAsync(brief, "measure this", Ct);
            var resolvedRepository = false;
            var provisioned = false;

            var refusal = await Assert.ThrowsAsync<CliArgumentException>(() => QueueCommand.ExecuteAsync(
                new QueueOptions(QueueVerb.Add, Tag: "mismatch", Role: "advise", SpecFilePath: brief,
                    Issue: 2234, Requirements: ["file-write", "shell"]),
                TextWriter.Null,
                Ct,
                home,
                (_, _) =>
                {
                    resolvedRepository = true;
                    return Task.FromResult<RepositoryIdentity?>(null);
                },
                (_, _, _, _, _, _, _) =>
                {
                    provisioned = true;
                    return Task.FromResult(new IssueWorktreeProvisioner.ProvisionedIssueWorktree("never", "never-lane"));
                }));

            Assert.Contains("file-write, shell", refusal.Message, StringComparison.Ordinal);
            Assert.False(resolvedRepository);
            Assert.False(provisioned);
            Assert.False(File.Exists(BatonPaths.QueueFile));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Add_refuses_a_provisioned_implement_lane_whose_inherited_ceiling_withholds_required_categories()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var sourceRepository = Path.Combine(home, "source");
            var workspace = Path.Combine(home, "w2353");
            var brief = Path.Combine(home, "brief.md");
            Directory.CreateDirectory(sourceRepository);
            await File.WriteAllTextAsync(brief, "implement this", Ct);

            var refusal = await Assert.ThrowsAsync<CliArgumentException>(() => QueueCommand.ExecuteAsync(
                new QueueOptions(QueueVerb.Add, Tag: "2353-lane", Role: "implement", Issue: 2353,
                    Lifecycle: true, SpecFilePath: brief,
                    DeclaredTaskSize: new TaskSizeDeclaration(DeclaredTaskSize.Small, "one admission fixture"),
                    Requirements: ["file-write", "network", "github-write"]),
                TextWriter.Null, Ct, sourceRepository,
                (_, _) => Task.FromResult(RepositoryIdentity.From("https://github.com/Owner/Repo.git", null)),
                (_, _, _, _, deterministicSourceCeiling, _, _) =>
                {
                    Assert.True(deterministicSourceCeiling);
                    Directory.CreateDirectory(workspace);
                    ProjectCeilingStore.Set(workspace,
                        new ProjectCeiling(ReadFiles: true, WriteFiles: false,
                            RunShellCommands: true, NetworkAccess: false),
                        ProjectCeilingStore.DefaultPath);
                    return Task.FromResult(new IssueWorktreeProvisioner.ProvisionedIssueWorktree(workspace, "2353-lane"));
                }));

            Assert.Contains(workspace, refusal.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("file-write", refusal.Message, StringComparison.Ordinal);
            Assert.Contains("WriteFiles", refusal.Message, StringComparison.Ordinal);
            Assert.Contains("NetworkAccess", refusal.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(BatonPaths.QueueFile));
            Assert.False(File.Exists(BatonPaths.QueueSpecFile("2353-lane")));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Add_refuses_an_explicit_workspace_with_a_narrow_ceiling_before_copying_its_spec()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var workspace = Path.Combine(home, "narrow-workspace");
            var brief = Path.Combine(home, "brief.md");
            Directory.CreateDirectory(workspace);
            await File.WriteAllTextAsync(brief, "implement this", Ct);
            ProjectCeilingStore.Set(workspace,
                new ProjectCeiling(ReadFiles: true, WriteFiles: false,
                    RunShellCommands: true, NetworkAccess: false), ProjectCeilingStore.DefaultPath);

            var refusal = await Assert.ThrowsAsync<CliArgumentException>(() => QueueCommand.ExecuteAsync(
                new QueueOptions(QueueVerb.Add, Tag: "narrow-explicit", Role: "implement",
                    WorkspaceDirectory: workspace, SpecFilePath: brief,
                    Requirements: ["file-write", "network"]), TextWriter.Null, Ct));

            Assert.Contains(workspace, refusal.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("WriteFiles", refusal.Message, StringComparison.Ordinal);
            Assert.Contains("NetworkAccess", refusal.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(BatonPaths.QueueFile));
            Assert.False(File.Exists(BatonPaths.QueueSpecFile("narrow-explicit")));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Theory]
    [InlineData("advise", false, false, false, "repository-read")]
    [InlineData("implement", true, true, true, "network")]
    public async Task Add_admits_a_role_whose_grant_fits_the_recorded_ceiling(
        string role, bool writes, bool shell, bool network, string requirement)
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var workspace = Path.Combine(home, "trusted-workspace");
            var brief = Path.Combine(home, "brief.md");
            Directory.CreateDirectory(workspace);
            await File.WriteAllTextAsync(brief, "do this", Ct);
            ProjectCeilingStore.Set(workspace,
                new ProjectCeiling(ReadFiles: true, WriteFiles: writes,
                    RunShellCommands: shell, NetworkAccess: network), ProjectCeilingStore.DefaultPath);

            Assert.Equal(0, await QueueCommand.ExecuteAsync(
                new QueueOptions(QueueVerb.Add, Tag: "trusted-lane", Role: role,
                    WorkspaceDirectory: workspace, SpecFilePath: brief,
                    Requirements: [requirement]), TextWriter.Null, Ct));

            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(TaskRequirementAdmission.Admitted, item.LastAdmission?.Result);
            Assert.True(File.Exists(BatonPaths.QueueSpecFile("trusted-lane")));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Add_fails_closed_on_a_recorded_ceiling_that_cannot_safely_bind_implement(
        bool revoked)
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var workspace = Path.Combine(home, "closed-workspace");
            var brief = Path.Combine(home, "brief.md");
            Directory.CreateDirectory(workspace);
            await File.WriteAllTextAsync(brief, "implement this", Ct);
            ProjectCeilingStore.Set(workspace,
                new ProjectCeiling(ReadFiles: true, WriteFiles: true,
                    RunShellCommands: true, NetworkAccess: false), ProjectCeilingStore.DefaultPath);
            if (revoked)
            {
                ProjectCeilingStore.Revoke(workspace, ProjectCeilingStore.DefaultPath);
            }

            var refusal = await Assert.ThrowsAsync<CliArgumentException>(() => QueueCommand.ExecuteAsync(
                new QueueOptions(QueueVerb.Add, Tag: "closed-lane", Role: "implement",
                    WorkspaceDirectory: workspace, SpecFilePath: brief,
                    Requirements: []), TextWriter.Null, Ct));

            Assert.Contains(workspace, refusal.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(revoked ? "revoked" : "shell defeats", refusal.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(BatonPaths.QueueFile));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Theory]
    [InlineData("claude", "claude-fable-5-1", "Fable")]
    [InlineData("codex", "gpt-6-astra", "Astra")]
    [InlineData("codex", null, "Astra")]
    public async Task Add_refuses_conductor_models_or_their_resolved_default_before_any_queue_side_effect(
        string adapter, string? model, string family)
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var brief = Path.Combine(home, "brief.md");
            await File.WriteAllTextAsync(brief, "implement this", Ct);

            var refusal = await Assert.ThrowsAsync<CliArgumentException>(() => QueueCommand.ExecuteAsync(
                new QueueOptions(QueueVerb.Add, Tag: "conductor-only", Role: "implement",
                    SpecFilePath: brief, WorkspaceDirectory: home, Adapter: adapter, Model: model),
                TextWriter.Null, Ct));

            Assert.Contains($"{family} is conductor-only", refusal.Message, StringComparison.Ordinal);
            Assert.Equal(
                string.Equals(adapter, "claude", StringComparison.Ordinal)
                    ? "pass --model sonnet, --model opus, or --model haiku."
                    : null,
                refusal.TryInvocation);
            Assert.False(File.Exists(BatonPaths.QueueFile));
            Assert.False(Directory.Exists(BatonPaths.QueueSpecsDirectory));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Theory]
    [InlineData(null, "claude-sonnet-4-6", "multiple candidate adapters: claude, agy; specify --adapter")]
    [InlineData("codex", "opus", "absent from the recorded Codex capability snapshot")]
    [InlineData("claude", "claude-opus-4.8", "cannot use the requested --model")]
    [InlineData("agy", "gpt-5.6-sol", "cannot use it")]
    [InlineData("claude", "gpt-5.6-terra", "cannot use it")]
    public async Task Add_refuses_ambiguous_or_invalid_models_before_any_queue_side_effect(
        string? adapter, string model, string message)
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var brief = Path.Combine(home, "brief.md");
            await File.WriteAllTextAsync(brief, "implement this", Ct);
            var refusal = await Assert.ThrowsAsync<CliArgumentException>(() => QueueCommand.ExecuteAsync(
                new QueueOptions(QueueVerb.Add, Tag: "refused", Role: "implement",
                    SpecFilePath: brief, Issue: 2077, Adapter: adapter, Model: model),
                TextWriter.Null, Ct));

            Assert.Contains(message, refusal.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(BatonPaths.QueueFile));
            Assert.False(Directory.Exists(BatonPaths.QueueSpecsDirectory));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }


    [Fact]
    public async Task List_active_selects_live_work_and_lifecycle_terminal_obligations()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            QueueItem Item(string tag, QueueItemState state, WorkStage? stage = null, bool halted = false,
                IReadOnlyList<string>? requirements = null) => new()
                {
                    Tag = tag,
                    Role = "implement",
                    Workspace = home,
                    SpecFile = Path.Combine(home, tag + ".md"),
                    State = state,
                    Stage = stage,
                    Halted = halted,
                    Requirements = requirements,
                };

            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with
            {
                Items =
                [
                    Item("ordinary-queued", QueueItemState.Queued, requirements: []),
                    Item("ready-lifecycle", QueueItemState.Queued, WorkStage.Ready, requirements: ["shell"]),
                    Item("launched", QueueItemState.Launched, requirements: null),
                    Item("done-lifecycle", QueueItemState.Done, WorkStage.Review, requirements: []),
                    Item("failed-lifecycle", QueueItemState.Failed, WorkStage.Fix, requirements: []),
                    Item("halted-failure", QueueItemState.Failed, WorkStage.Fix, halted: true, requirements: []),
                    Item("done-history", QueueItemState.Done, requirements: []),
                    Item("failed-history", QueueItemState.Failed, requirements: []),
                    Item("cancelled", QueueItemState.Cancelled, WorkStage.Implement, requirements: []),
                ],
            }, Ct);

            var output = new StringWriter();
            Assert.Equal(0, await QueueCommand.ExecuteAsync(new QueueOptions(QueueVerb.List, Active: true), output, Ct));
            var printed = output.ToString();

            foreach (var selected in new[] { "ordinary-queued", "ready-lifecycle", "launched", "done-lifecycle", "failed-lifecycle", "halted-failure" })
            {
                Assert.Contains(selected, printed, StringComparison.Ordinal);
            }

            foreach (var excluded in new[] { "done-history", "failed-history", "cancelled" })
            {
                Assert.DoesNotContain(excluded, printed, StringComparison.Ordinal);
            }

            Assert.Contains("failed halted", printed, StringComparison.Ordinal);
            Assert.Contains("Requirement coverage: 5/6 declared (selected); 1 unknown migration row(s).", printed, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task List_renders_the_graph_frontier_instead_of_the_stale_mutable_stage()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            const string input = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            const string head = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            var implement = new FleetAttemptId("list-implement");
            var review = new FleetAttemptId("list-review");
            var item = new QueueItem
            {
                Tag = "2363-lane",
                Role = "implement",
                Workspace = home,
                SpecFile = BatonPaths.QueueSpecFile("2363-lane"),
                Stage = WorkStage.Implement,
                State = QueueItemState.Queued,
                Issue = 2363,
                PullRequest = 77,
                LifecycleGraphVersion = LifecycleAttemptGraph.Version,
                Checks = PullRequestChecks.Passing,
                ChecksHeadSha = head,
                LifecyclePullRequestEvidence = new QueuePullRequestEvidence(
                    77, head, true, true, true, DateTimeOffset.UtcNow),
            };
            await QueueStore.MutateAsync(BatonPaths.QueueFile, state => state with { Items = [item] }, Ct);

            var log = FleetEventLog.OpenOperational();
            await log.Append(LifecycleAttemptGraph.PlanEvent(item, implement,
                new LifecycleNextAttempt(WorkStage.Implement, new FleetRevisionId(input), [], "initial"),
                DateTimeOffset.UtcNow.AddMinutes(-3)), Ct);
            await log.Append(new FleetEventDraft(
                FleetEventKind.AttemptStarted, "list-implement-started", DateTimeOffset.UtcNow.AddMinutes(-2),
                AttemptId: implement, WorkId: new FleetWorkId(item.Tag)), Ct);
            await log.Append(new FleetEventDraft(
                FleetEventKind.AttemptSettled, "list-implement-settled", DateTimeOffset.UtcNow.AddMinutes(-1),
                AttemptId: implement, WorkId: new FleetWorkId(item.Tag), Outcome: WorkflowOutcome.Succeeded), Ct);
            await log.Append(new FleetEventDraft(
                FleetEventKind.RevisionProduced, "list-implement-revision", DateTimeOffset.UtcNow,
                AttemptId: implement, WorkId: new FleetWorkId(item.Tag), RevisionId: new FleetRevisionId(head)), Ct);
            await log.Append(LifecycleAttemptGraph.PlanEvent(item, review,
                new LifecycleNextAttempt(WorkStage.Review, new FleetRevisionId(head),
                    [new FleetAttemptEdge(implement, FleetAttemptEdgeKind.Reviews)], "review exact head"),
                DateTimeOffset.UtcNow), Ct);

            var output = new StringWriter();
            Assert.Equal(0, await QueueCommand.ExecuteAsync(new QueueOptions(QueueVerb.List), output, Ct));

            Assert.Contains("2363-lane  queued  review  stage: review (round 1)  PR #77", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("stage: implement", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task List_active_reports_an_empty_selection_without_losing_banners()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with
            {
                Held = true,
                Items =
                [
                    new QueueItem
                    {
                        Tag = "history", Role = "implement", Workspace = home, SpecFile = Path.Combine(home, "history.md"),
                        State = QueueItemState.Done,
                    },
                ],
            }, Ct);

            var output = new StringWriter();
            Assert.Equal(0, await QueueCommand.ExecuteAsync(new QueueOptions(QueueVerb.List, Active: true), output, Ct));
            Assert.Equal(
                "Queue is HELD — no new launches until 'baton queue resume'. Live lanes are unaffected." + Environment.NewLine
                + "No active queue items." + Environment.NewLine,
                output.ToString());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task List_without_active_remains_byte_compatible()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with
            {
                Items =
                [
                    new QueueItem
                    {
                        Tag = "history", Role = "implement", Workspace = home, SpecFile = Path.Combine(home, "history.md"),
                        State = QueueItemState.Done, Requirements = [],
                    },
                ],
            }, Ct);

            var output = new StringWriter();
            Assert.Equal(0, await QueueCommand.ExecuteAsync(new QueueOptions(QueueVerb.List), output, Ct));
            Assert.Equal(
                "history  done  implement" + Environment.NewLine
                + "  requirements: none" + Environment.NewLine
                + "Requirement coverage: 1/1 declared; 0 unknown migration row(s)." + Environment.NewLine,
                output.ToString());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    private static string CreateTempHome()
    {
        var home = Path.Combine(Path.GetTempPath(), "baton_queue_cmd_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(home);
        return home;
    }

    [Fact]
    public async Task Add_measure_issue_resolves_repository_and_queues_the_provisioned_workspace()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var sourceRepository = Path.Combine(home, "source");
            var workspace = Path.Combine(home, "w2202");
            var brief = Path.Combine(home, "brief.md");
            Directory.CreateDirectory(sourceRepository);
            await File.WriteAllTextAsync(brief, "measure completion evidence", Ct);
            string? provisionedRepository = null;

            var exit = await QueueCommand.ExecuteAsync(
                new QueueOptions(
                    QueueVerb.Add, Tag: "2202-measure", Role: "measure", SpecFilePath: brief, Issue: 2202),
                TextWriter.Null,
                Ct,
                sourceRepository,
                (path, _) => Task.FromResult(RepositoryIdentity.From("https://github.com/Owner/Repo.git", null)),
                (issue, source, _, repository, _, _, _) =>
                {
                    Assert.Equal(2202, issue);
                    Assert.Equal(sourceRepository, source);
                    provisionedRepository = repository;
                    Directory.CreateDirectory(workspace);
                    return Task.FromResult(new IssueWorktreeProvisioner.ProvisionedIssueWorktree(workspace, "2202-lane"));
                });

            Assert.Equal(0, exit);
            Assert.Equal("github.com/owner/repo", provisionedRepository);
            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal("measure", item.Role);
            Assert.Equal(2202, item.Issue);
            Assert.Equal(workspace, item.Workspace);
            Assert.Equal("github.com/owner/repo", item.Repository);
            Assert.Null(item.Stage);
            Assert.Equal("2202-lane", item.Branch);
            Assert.Equal(WorkspaceOrigins.IssueProvisioned, item.WorkspaceOrigin);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Add_ordinary_issue_records_repository_without_enabling_lifecycle()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var sourceRepository = Path.Combine(home, "source");
            var workspace = Path.Combine(home, "w2225");
            var brief = Path.Combine(home, "brief.md");
            Directory.CreateDirectory(sourceRepository);
            await File.WriteAllTextAsync(brief, "one ordinary lane", Ct);

            await QueueCommand.ExecuteAsync(
                new QueueOptions(
                    QueueVerb.Add, Tag: "2225-implement", Role: "implement", SpecFilePath: brief, Issue: 2225,
                    Skills: ["house-style"]),
                TextWriter.Null,
                Ct,
                sourceRepository,
                (_, _) => Task.FromResult(RepositoryIdentity.From("git@github.com:Owner/Repo.git", null)),
                (_, _, _, _, deterministicSourceCeiling, _, _) =>
                {
                    Assert.False(deterministicSourceCeiling);
                    Directory.CreateDirectory(workspace);
                    return Task.FromResult(new IssueWorktreeProvisioner.ProvisionedIssueWorktree(workspace, "2225-lane-2"));
                });

            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal("github.com/owner/repo", item.Repository);
            Assert.Null(item.Stage);
            Assert.Equal("2225-lane-2", item.Branch);
            Assert.Null(item.AutomaticFixUsed);
            Assert.Equal(["house-style"], item.Skills);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Add_lifecycle_issue_keeps_lifecycle_fields_with_the_same_repository_provenance()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var sourceRepository = Path.Combine(home, "source");
            var workspace = Path.Combine(home, "w2225");
            var brief = Path.Combine(home, "brief.md");
            Directory.CreateDirectory(sourceRepository);
            await File.WriteAllTextAsync(brief, "lifecycle implementation", Ct);

            await QueueCommand.ExecuteAsync(
                new QueueOptions(
                    QueueVerb.Add, Tag: "2225-lane", Role: "implement", SpecFilePath: brief,
                    Issue: 2225, Lifecycle: true,
                    DeclaredTaskSize: new TaskSizeDeclaration(DeclaredTaskSize.Small, "one lifecycle fixture")),
                TextWriter.Null,
                Ct,
                sourceRepository,
                (_, _) => Task.FromResult(RepositoryIdentity.From("https://github.com/Owner/Repo", null)),
                (_, _, _, _, deterministicSourceCeiling, _, _) =>
                {
                    Assert.True(deterministicSourceCeiling);
                    Directory.CreateDirectory(workspace);
                    return Task.FromResult(new IssueWorktreeProvisioner.ProvisionedIssueWorktree(workspace, "2225-lane"));
                });

            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal("github.com/owner/repo", item.Repository);
            Assert.Equal(WorkStage.Implement, item.Stage);
            Assert.Equal("2225-lane", item.Branch);
            Assert.False(item.AutomaticFixUsed);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Add_explicit_workspace_does_not_resolve_or_record_repository_provenance()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var workspace = Path.Combine(home, "workspace");
            var brief = Path.Combine(home, "brief.md");
            Directory.CreateDirectory(workspace);
            await File.WriteAllTextAsync(brief, "use this workspace", Ct);
            var resolverCalled = false;
            var provisionerCalled = false;

            await QueueCommand.ExecuteAsync(
                new QueueOptions(
                    QueueVerb.Add, Tag: "explicit-workspace", Role: "implement",
                    SpecFilePath: brief, WorkspaceDirectory: workspace),
                TextWriter.Null,
                Ct,
                repositoryDirectory: null,
                (_, _) =>
                {
                    resolverCalled = true;
                    return Task.FromResult<RepositoryIdentity?>(null);
                },
                (_, _, _, _, _, _, _) =>
                {
                    provisionerCalled = true;
                    return Task.FromResult(new IssueWorktreeProvisioner.ProvisionedIssueWorktree(workspace, "unused-lane"));
                });

            Assert.False(resolverCalled);
            Assert.False(provisionerCalled);
            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(Path.GetFullPath(workspace), item.Workspace);
            Assert.Null(item.Repository);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Add_issue_refuses_missing_or_local_only_repository_before_provisioning_or_queue_mutation(
        bool missingIdentity)
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var sourceRepository = Path.Combine(home, "source");
            var workspace = Path.Combine(home, "w2225");
            var brief = Path.Combine(home, "brief.md");
            Directory.CreateDirectory(sourceRepository);
            await File.WriteAllTextAsync(brief, "must not be copied", Ct);
            var provisionerCalled = false;
            var identity = missingIdentity
                ? null
                : RepositoryIdentity.From(null, Path.Combine(sourceRepository, ".git"));

            var refusal = await Assert.ThrowsAsync<CliArgumentException>(() => QueueCommand.ExecuteAsync(
                new QueueOptions(
                    QueueVerb.Add, Tag: "repository-refusal", Role: "measure", SpecFilePath: brief, Issue: 2225),
                TextWriter.Null,
                Ct,
                sourceRepository,
                (_, _) => Task.FromResult(identity),
                (_, _, _, _, _, _, _) =>
                {
                    provisionerCalled = true;
                    Directory.CreateDirectory(workspace);
                    return Task.FromResult(new IssueWorktreeProvisioner.ProvisionedIssueWorktree(workspace, "unused-lane"));
                }));

            Assert.Contains("canonical remote repository identity", refusal.Message, StringComparison.Ordinal);
            Assert.False(provisionerCalled);
            Assert.False(Directory.Exists(workspace));
            Assert.False(File.Exists(BatonPaths.QueueFile));
            Assert.False(Directory.Exists(BatonPaths.QueueSpecsDirectory));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Add_resolves_an_unscoped_models_unique_adapter_before_writing_the_item()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var workspace = Path.Combine(home, "workspace");
            var brief = Path.Combine(home, "brief.md");
            Directory.CreateDirectory(workspace);
            await File.WriteAllTextAsync(brief, "implement this", Ct);

            var output = new StringWriter();
            var exit = await QueueCommand.ExecuteAsync(
                new QueueOptions(
                    QueueVerb.Add, Tag: "model-route", Role: "implement", SpecFilePath: brief,
                    WorkspaceDirectory: workspace, Model: "opus", Effort: "high"),
                output,
                Ct);

            Assert.Equal(0, exit);
            Assert.Contains("tier: claude (from --model opus) / opus / high", output.ToString(), StringComparison.Ordinal);
            Assert.Equal("claude", (await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items.Single().Adapter);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Add_refuses_an_unknown_model_before_creating_a_queue_row()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var workspace = Path.Combine(home, "workspace");
            var brief = Path.Combine(home, "brief.md");
            Directory.CreateDirectory(workspace);
            await File.WriteAllTextAsync(brief, "implement this", Ct);

            var refusal = await Assert.ThrowsAsync<CliArgumentException>(() => QueueCommand.ExecuteAsync(
                new QueueOptions(
                    QueueVerb.Add, Tag: "unknown-model", Role: "implement", SpecFilePath: brief,
                    WorkspaceDirectory: workspace, Model: "not-a-model"),
                TextWriter.Null,
                Ct));

            Assert.Contains("no recorded adapter candidate", refusal.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(BatonPaths.QueueFile));
            Assert.False(Directory.Exists(BatonPaths.QueueSpecsDirectory));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Add_refuses_a_model_the_scopes_resolved_adapter_cannot_use_before_creating_a_queue_row()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var workspace = Path.Combine(home, "workspace");
            var brief = Path.Combine(home, "brief.md");
            Directory.CreateDirectory(workspace);
            await File.WriteAllTextAsync(brief, "implement this", Ct);

            var refusal = await Assert.ThrowsAsync<CliArgumentException>(() => QueueCommand.ExecuteAsync(
                new QueueOptions(
                    QueueVerb.Add, Tag: "model-mismatch", Role: "implement", SpecFilePath: brief,
                    WorkspaceDirectory: workspace, ScopeClass: "tooling", Model: "opus", Reason: "deliberate"),
                TextWriter.Null,
                Ct));

            Assert.Contains("resolved codex adapter cannot use it", refusal.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(BatonPaths.QueueFile));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Add_refuses_an_invalid_later_stage_selection_before_provisioning_or_worker_spend()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var refusal = await Assert.ThrowsAsync<CliArgumentException>(() => QueueCommand.ExecuteAsync(
                new QueueOptions(
                    QueueVerb.Add, Tag: "2181-lane", Role: "implement", Issue: 2181, Lifecycle: true,
                    ScopeClass: "tooling",
                    DeclaredTaskSize: new TaskSizeDeclaration(DeclaredTaskSize.Small, "one validation fixture"),
                    StageSelections:
                    [
                        new QueueStageSelection { Stage = WorkStage.Review, Model = "opus", Reason = "test" },
                    ]),
                TextWriter.Null,
                Ct));

            Assert.Contains("review selection", refusal.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(BatonPaths.QueueFile));
            Assert.False(Directory.Exists(BatonPaths.QueueSpecsDirectory));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Add_refuses_a_later_claude_stage_without_a_model_before_provisioning()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var refusal = await Assert.ThrowsAsync<CliArgumentException>(() => QueueCommand.ExecuteAsync(
                new QueueOptions(
                    QueueVerb.Add, Tag: "2142-lifecycle", Role: "implement", Issue: 2142, Lifecycle: true,
                    DeclaredTaskSize: new TaskSizeDeclaration(DeclaredTaskSize.Small, "one validation fixture"),
                    StageSelections:
                    [
                        new QueueStageSelection { Stage = WorkStage.Review, Adapter = "claude" },
                    ]),
                TextWriter.Null, Ct));

            Assert.Contains("review selection", refusal.Message, StringComparison.Ordinal);
            Assert.Contains("standing model policy", refusal.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(BatonPaths.QueueFile));
            Assert.False(Directory.Exists(BatonPaths.QueueSpecsDirectory));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Add_refuses_an_Astra_selection_for_a_later_lifecycle_stage_before_provisioning()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var refusal = await Assert.ThrowsAsync<CliArgumentException>(() => QueueCommand.ExecuteAsync(
                new QueueOptions(
                    QueueVerb.Add, Tag: "astra-review", Role: "implement", Issue: 2233, Lifecycle: true,
                    DeclaredTaskSize: new TaskSizeDeclaration(DeclaredTaskSize.Small, "one validation fixture"),
                    StageSelections:
                    [
                        new QueueStageSelection
                        {
                            Stage = WorkStage.Review,
                            Adapter = "codex",
                            Model = "gpt-6-astra",
                            Reason = "must not become a later-stage escape hatch",
                        },
                    ]),
                TextWriter.Null, Ct));

            Assert.Contains("review selection", refusal.Message, StringComparison.Ordinal);
            Assert.Contains("Astra is conductor-only", refusal.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(BatonPaths.QueueFile));
            Assert.False(Directory.Exists(BatonPaths.QueueSpecsDirectory));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public void A_model_only_stage_selection_keeps_its_unique_models_adapter()
    {
        var selections = QueueCommand.NormalizeLifecycleStageSelections(
            [new QueueStageSelection { Stage = WorkStage.Review, Model = "gpt-5.6-sol" }], scopeClass: null);

        Assert.Equal("codex", Assert.Single(selections!).Adapter);
    }

    [Fact]
    public async Task Add_refuses_an_adapter_only_lifecycle_pin_with_an_inherited_incompatible_model_before_provisioning()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var refusal = await Assert.ThrowsAsync<CliArgumentException>(() => QueueCommand.ExecuteAsync(
                new QueueOptions(
                    QueueVerb.Add, Tag: "2181-pin", Role: "implement", Issue: 2181, Lifecycle: true,
                    ScopeClass: "tooling", Adapter: "agy", LifecyclePin: true,
                    DeclaredTaskSize: new TaskSizeDeclaration(DeclaredTaskSize.Small, "one validation fixture")),
                TextWriter.Null, Ct));

            Assert.Contains("cannot use", refusal.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(BatonPaths.QueueFile));
            Assert.False(Directory.Exists(BatonPaths.QueueSpecsDirectory));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Add_prints_an_unscoped_roles_resolved_adapter_without_a_model()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var workspace = Path.Combine(home, "workspace");
            var brief = Path.Combine(home, "brief.md");
            Directory.CreateDirectory(workspace);
            await File.WriteAllTextAsync(brief, "implement this", Ct);

            var output = new StringWriter();
            await QueueCommand.ExecuteAsync(
                new QueueOptions(
                    QueueVerb.Add, Tag: "role-route", Role: "implement", SpecFilePath: brief,
                    WorkspaceDirectory: workspace),
                output,
                Ct);

            Assert.Contains("tier: codex / gpt-5.6-sol / medium", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("role default adapter", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Re_adding_a_launched_tag_is_refused_before_the_spec_copy_is_overwritten()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var workspace = Path.Combine(home, "w1");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(BatonPaths.QueueSpecsDirectory);

            // What the running lane was queued with.
            await File.WriteAllTextAsync(BatonPaths.QueueSpecFile("t1"), "the brief the lane is running", Ct);
            await QueueStore.MutateAsync(
                BatonPaths.QueueFile,
                s => s with
                {
                    Items =
                    [
                        new QueueItem
                        {
                            Tag = "t1",
                            Role = "implement",
                            Workspace = workspace,
                            SpecFile = BatonPaths.QueueSpecFile("t1"),
                            State = QueueItemState.Launched,
                            RoomDirectory = Path.Combine(home, "rooms", "queue-t1-abcd"),
                        },
                    ],
                },
                Ct);

            var newBrief = Path.Combine(home, "new-brief.md");
            await File.WriteAllTextAsync(newBrief, "a different brief entirely", Ct);

            await Assert.ThrowsAsync<CliArgumentException>(() => QueueCommand.ExecuteAsync(
                new QueueOptions(
                    QueueVerb.Add, Tag: "t1", Role: "implement", SpecFilePath: newBrief,
                    WorkspaceDirectory: workspace),
                TextWriter.Null,
                Ct));

            // The refusal's own reason is that the running lane's record would be overwritten, so the
            // copy must not already have happened by the time it is raised.
            Assert.Equal(
                "the brief the lane is running",
                await File.ReadAllTextAsync(BatonPaths.QueueSpecFile("t1"), Ct));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Theory]
    [InlineData("unauthorized")]
    [InlineData("io")]
    public async Task Add_refuses_a_queue_spec_write_failure_without_mutating_the_queue_or_prior_brief(string failure)
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            Directory.CreateDirectory(BatonPaths.QueueSpecsDirectory);
            var destination = BatonPaths.QueueSpecFile("write-failure");
            await File.WriteAllTextAsync(destination, "the retained brief", Ct);
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
            {
                Items = [new QueueItem { Tag = "write-failure", Role = "implement", Workspace = home, SpecFile = destination }],
            }, Ct);
            var replacement = Path.Combine(home, "replacement.md");
            await File.WriteAllTextAsync(replacement, "the replacement brief", Ct);

            var refusal = await Assert.ThrowsAsync<CliArgumentException>(() => QueueCommand.ExecuteAsync(
                new QueueOptions(QueueVerb.Add, Tag: "write-failure", Role: "implement", SpecFilePath: replacement,
                    WorkspaceDirectory: home),
                TextWriter.Null,
                Ct,
                home,
                (_, _) => Task.FromResult<RepositoryIdentity?>(null),
                (_, _, _, _, _, _, _) => Task.FromResult(new IssueWorktreeProvisioner.ProvisionedIssueWorktree(home, "write-failure-lane")),
                (_, _) =>
                {
                    if (failure == "unauthorized")
                    {
                        throw new UnauthorizedAccessException("test write denial");
                    }

                    throw new IOException("test write failure");
                }));

            Assert.Contains(destination, refusal.Message, StringComparison.Ordinal);
            Assert.Equal("make the queue-spec path writable, then retry 'baton queue add'.", refusal.TryInvocation);
            Assert.Equal("the retained brief", await File.ReadAllTextAsync(destination, Ct));
            Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Add_refuses_a_windows_reader_held_queue_spec_replacement_without_truncating_it()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            Directory.CreateDirectory(BatonPaths.QueueSpecsDirectory);
            var destination = BatonPaths.QueueSpecFile("reader-held");
            await File.WriteAllTextAsync(destination, "the retained brief", Ct);
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
            {
                Items = [new QueueItem { Tag = "reader-held", Role = "implement", Workspace = home, SpecFile = destination }],
            }, Ct);
            var replacement = Path.Combine(home, "replacement.md");
            await File.WriteAllTextAsync(replacement, "the replacement brief", Ct);

            using var reader = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read);
            var refusal = await Assert.ThrowsAsync<CliArgumentException>(() => QueueCommand.ExecuteAsync(
                new QueueOptions(QueueVerb.Add, Tag: "reader-held", Role: "implement", SpecFilePath: replacement,
                    WorkspaceDirectory: home), TextWriter.Null, Ct));

            Assert.Contains(destination, refusal.Message, StringComparison.Ordinal);
            Assert.Equal("the retained brief", await File.ReadAllTextAsync(destination, Ct));
            Assert.Equal("reader-held", Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items).Tag);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Re_adding_a_cancelled_tag_leaves_its_retained_brief_unchanged()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var workspace = Path.Combine(home, "w2159");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(BatonPaths.QueueSpecsDirectory);
            var spec = BatonPaths.QueueSpecFile("2159-lane");
            await File.WriteAllTextAsync(spec, "the brief the operator cancelled", Ct);
            await QueueStore.MutateAsync(
                BatonPaths.QueueFile,
                s => s with
                {
                    Items =
                    [
                        new QueueItem
                        {
                            Tag = "2159-lane",
                            Role = "implement",
                            Workspace = workspace,
                            SpecFile = spec,
                            State = QueueItemState.Cancelled,
                            CancelledAt = DateTimeOffset.UtcNow,
                        },
                    ],
                },
                Ct);

            var replacement = Path.Combine(home, "replacement.md");
            await File.WriteAllTextAsync(replacement, "a replacement brief", Ct);

            await Assert.ThrowsAsync<CliArgumentException>(() => QueueCommand.ExecuteAsync(
                new QueueOptions(
                    QueueVerb.Add, Tag: "2159-lane", Role: "implement", SpecFilePath: replacement,
                    WorkspaceDirectory: workspace),
                TextWriter.Null,
                Ct));

            // The locked add check protects the same canonical file cancellation retained. This is the
            // cancellation-wins half of the add/cancel race; the old early read alone could not prove it.
            Assert.Equal("the brief the operator cancelled", await File.ReadAllTextAsync(spec, Ct));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Re_adding_a_tag_with_a_claimed_readiness_mutation_is_refused_before_the_spec_copy()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var workspace = Path.Combine(home, "w2131");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(BatonPaths.QueueSpecsDirectory);
            var spec = BatonPaths.QueueSpecFile("2131-lane");
            await File.WriteAllTextAsync(spec, "the claimed row's brief", Ct);
            await QueueStore.MutateAsync(
                BatonPaths.QueueFile,
                state => state with
                {
                    Items =
                    [
                        new QueueItem
                        {
                            Tag = "2131-lane",
                            Role = "implement",
                            Workspace = workspace,
                            SpecFile = spec,
                            Stage = WorkStage.Implement,
                            ReadinessMutationClaim = "active-claim",
                        },
                    ],
                },
                Ct);
            var replacement = Path.Combine(home, "replacement.md");
            await File.WriteAllTextAsync(replacement, "replacement brief", Ct);

            var refusal = await Assert.ThrowsAsync<CliArgumentException>(() => QueueCommand.ExecuteAsync(
                new QueueOptions(
                    QueueVerb.Add, Tag: "2131-lane", Role: "implement", SpecFilePath: replacement,
                    WorkspaceDirectory: workspace),
                TextWriter.Null,
                Ct));

            Assert.Contains("already authorized", refusal.Message, StringComparison.Ordinal);
            Assert.Equal("the claimed row's brief", await File.ReadAllTextAsync(spec, Ct));
            Assert.Equal("active-claim", Assert.Single(
                (await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items).ReadinessMutationClaim);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Re_adding_a_work_item_past_implement_is_refused_before_its_brief_is_overwritten()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            // A work item between rounds is QUEUED, not launched, so slice 1's launched-tag refusal
            // does not cover it — and a `--lifecycle` tag defaults to `<n>-lane`, so re-typing the same
            // add is an ordinary thing to do. Without the refusal this resets fix round 2 to implement
            // round 0 and overwrites the brief carrying the reviewer's findings.
            var workspace = Path.Combine(home, "w1934");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(BatonPaths.QueueSpecsDirectory);
            await File.WriteAllTextAsync(BatonPaths.QueueSpecFile("1934-lane"), "the fix brief with the findings", Ct);
            await QueueStore.MutateAsync(
                BatonPaths.QueueFile,
                s => s with
                {
                    Items =
                    [
                        new QueueItem
                        {
                            Tag = "1934-lane",
                            Role = "implement",
                            Workspace = workspace,
                            SpecFile = BatonPaths.QueueSpecFile("1934-lane"),
                            Issue = 1934,
                            Branch = "1934-lane",
                            Stage = WorkStage.Fix,
                            Round = 2,
                            State = QueueItemState.Queued,
                        },
                    ],
                },
                Ct);

            var newBrief = Path.Combine(home, "new-brief.md");
            await File.WriteAllTextAsync(newBrief, "an implement brief", Ct);

            var refusal = await Assert.ThrowsAsync<CliArgumentException>(() => QueueCommand.ExecuteAsync(
                new QueueOptions(
                    QueueVerb.Add, Tag: "1934-lane", Role: "implement", SpecFilePath: newBrief,
                    WorkspaceDirectory: workspace),
                TextWriter.Null,
                Ct));

            Assert.Contains("stage 'fix'", refusal.Message, StringComparison.Ordinal);
            Assert.Equal(
                "the fix brief with the findings",
                await File.ReadAllTextAsync(BatonPaths.QueueSpecFile("1934-lane"), Ct));

            var item = (await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items.Single();
            Assert.Equal(WorkStage.Fix, item.Stage);
            Assert.Equal(2, item.Round);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task A_work_item_still_at_implement_is_replaceable_like_any_other_queued_item()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            // The polarity control for the refusal above: the same re-add, one stage earlier, must
            // still go through — there is no round history to lose at implement, and refusing here
            // would make correcting a just-queued item impossible.
            var workspace = Path.Combine(home, "w1934");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(BatonPaths.QueueSpecsDirectory);
            await File.WriteAllTextAsync(BatonPaths.QueueSpecFile("1934-lane"), "the first brief", Ct);
            await QueueStore.MutateAsync(
                BatonPaths.QueueFile,
                s => s with
                {
                    Items =
                    [
                        new QueueItem
                        {
                            Tag = "1934-lane",
                            Role = "implement",
                            Workspace = workspace,
                            SpecFile = BatonPaths.QueueSpecFile("1934-lane"),
                            Issue = 1934,
                            Stage = WorkStage.Implement,
                            State = QueueItemState.Queued,
                        },
                    ],
                },
                Ct);

            var newBrief = Path.Combine(home, "new-brief.md");
            await File.WriteAllTextAsync(newBrief, "a corrected brief", Ct);

            var exit = await QueueCommand.ExecuteAsync(
                new QueueOptions(
                    QueueVerb.Add, Tag: "1934-lane", Role: "implement", SpecFilePath: newBrief,
                    WorkspaceDirectory: workspace),
                TextWriter.Null,
                Ct);

            Assert.Equal(0, exit);
            Assert.Equal("a corrected brief", await File.ReadAllTextAsync(BatonPaths.QueueSpecFile("1934-lane"), Ct));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Re_adding_a_queued_tag_still_replaces_it_and_its_spec()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            // The control for the refusal above: a QUEUED tag is the operator editing their list, and
            // that path must still copy. Without this, moving the copy later could have disabled it.
            var workspace = Path.Combine(home, "w1");
            Directory.CreateDirectory(workspace);
            var brief = Path.Combine(home, "brief.md");
            await File.WriteAllTextAsync(brief, "the first brief", Ct);

            var options = new QueueOptions(
                QueueVerb.Add, Tag: "t1", Role: "implement", SpecFilePath: brief, WorkspaceDirectory: workspace);
            Assert.Equal(0, await QueueCommand.ExecuteAsync(options, TextWriter.Null, Ct));

            await File.WriteAllTextAsync(brief, "the rewritten brief", Ct);
            Assert.Equal(0, await QueueCommand.ExecuteAsync(options, TextWriter.Null, Ct));

            Assert.Equal("the rewritten brief", await File.ReadAllTextAsync(BatonPaths.QueueSpecFile("t1"), Ct));
            Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task List_prints_the_wait_the_ledger_last_recorded()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var at = new DateTimeOffset(2026, 9, 6, 1, 30, 0, TimeSpan.Zero);
            await QueueDecisionLedgerStore.AppendAsync(
                new QueueDecisionEntry(at, "t1", QueueDecisionEntry.Waited, "memory", 2.0, 1.4, 2.0),
                previousVerdictKey: null, BatonPaths.QueueDecisionLedgerFile, Ct);

            var output = new StringWriter();
            await QueueCommand.ExecuteAsync(new QueueOptions(QueueVerb.List), output, Ct);

            var printed = output.ToString();
            Assert.Contains("Waiting on memory", printed, StringComparison.Ordinal);
            Assert.Contains("1.4 GiB against a 2 GiB floor", printed, StringComparison.Ordinal);
            Assert.Contains("candidate 't1'", printed, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task List_shows_recorded_wip_and_flow_selection_not_a_recomputed_count()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var at = new DateTimeOffset(2026, 9, 6, 1, 30, 0, TimeSpan.Zero);
            await QueueDecisionLedgerStore.AppendAsync(
                new QueueDecisionEntry(at, "fix", QueueDecisionEntry.Launched, null, 0, 8, 2,
                    ActiveLifecycles: 2, PrePullRequestLifecycles: 1, LiveReviews: 0,
                    PriorityBand: "repair", PassedNewWorkHead: true,
                    ConsumingLifecycles: ["old", "fix"], NewWorkHeadCap: "lifecycle-cap"),
                previousVerdictKey: null, BatonPaths.QueueDecisionLedgerFile, Ct);
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
            {
                Items =
                [
                    new QueueItem
                    {
                        Tag = "fix", Role = "implement", Stage = WorkStage.Ready,
                        Workspace = home, SpecFile = Path.Combine(home, "fix.md"),
                        State = QueueItemState.Done,
                    },
                ],
            }, Ct);

            var output = new StringWriter();
            await QueueCommand.ExecuteAsync(new QueueOptions(QueueVerb.List), output, Ct);

            var printed = output.ToString();
            Assert.Contains("active 2 / 4; pre-PR 1 / 2; live reviews 0 / 2", printed, StringComparison.Ordinal);
            Assert.Contains("oldest first: old, fix", printed, StringComparison.Ordinal);
            Assert.Contains("last scheduler selection: fix (repair); passed new-work head; new head held by lifecycle-cap",
                printed, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    /// <summary>
    /// The two tokens the line is suppressed for, and why — <c>QueueCommand.PrintWaitAsync</c>'s own
    /// remarks. Both are things this listing already says, one of them on the line directly above.
    /// </summary>
    [Theory]
    [InlineData(QueueWaitReason.NoItems)]
    [InlineData(QueueWaitReason.Hold)]
    public async Task List_suppresses_the_wait_line_for_a_reason_the_listing_already_states(QueueWaitReason reason)
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            await QueueDecisionLedgerStore.AppendAsync(
                new QueueDecisionEntry(
                    new DateTimeOffset(2026, 9, 6, 1, 30, 0, TimeSpan.Zero), null, QueueDecisionEntry.Waited,
                    QueueWaitReasons.Token(reason), 0, 6.4, 2.0),
                previousVerdictKey: null, BatonPaths.QueueDecisionLedgerFile, Ct);
            if (reason == QueueWaitReason.Hold)
            {
                await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with { Held = true }, Ct);
            }

            var output = new StringWriter();
            await QueueCommand.ExecuteAsync(new QueueOptions(QueueVerb.List), output, Ct);

            Assert.DoesNotContain("Waiting on", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task List_prints_each_lifecycle_stages_effective_choice_and_provenance()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            await QueueStore.MutateAsync(
                BatonPaths.QueueFile,
                s => s with
                {
                    Items =
                    [
                        new QueueItem
                        {
                            Tag = "2181-lane",
                            Role = "implement",
                            Workspace = home,
                            SpecFile = BatonPaths.QueueSpecFile("2181-lane"),
                            Stage = WorkStage.Implement,
                            StageSelections = [],
                        },
                    ],
                },
                Ct);

            var output = new StringWriter();
            await QueueCommand.ExecuteAsync(new QueueOptions(QueueVerb.List), output, Ct);

            Assert.Contains("effective stage plan: implement=", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("review=", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("(stage-default)", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    /// <summary>
    /// A halted item is marked, and one that merely failed its lane is not — the distinction
    /// <c>QueueCommand.ListAsync</c>'s comment states this token exists for.
    /// </summary>
    /// <remarks>
    /// Both items are in ONE listing and both are <c>failed</c>, so the token cannot be tracking the
    /// state word: a listing that printed <c>halted</c> for every failure would fail the second
    /// assertion, and one that printed it for none would fail the first.
    /// </remarks>
    [Fact]
    public async Task List_marks_the_item_the_queue_has_given_up_on()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            Directory.CreateDirectory(BatonPaths.QueueSpecsDirectory);
            await QueueStore.MutateAsync(
                BatonPaths.QueueFile,
                s => s with
                {
                    Items =
                    [
                        new QueueItem
                        {
                            Tag = "given-up",
                            Role = "implement",
                            Workspace = Path.Combine(home, "w1"),
                            SpecFile = BatonPaths.QueueSpecFile("given-up"),
                            State = QueueItemState.Failed,
                            Halted = true,
                        },
                        new QueueItem
                        {
                            Tag = "retryable",
                            Role = "implement",
                            Workspace = Path.Combine(home, "w2"),
                            SpecFile = BatonPaths.QueueSpecFile("retryable"),
                            State = QueueItemState.Failed,
                        },
                    ],
                },
                Ct);

            var output = new StringWriter();
            await QueueCommand.ExecuteAsync(new QueueOptions(QueueVerb.List), output, Ct);

            var lines = output.ToString().ReplaceLineEndings("\n").Split('\n');
            Assert.Contains(lines, line => line.StartsWith("given-up  failed halted", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.StartsWith("retryable  failed  ", StringComparison.Ordinal));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task List_says_nothing_about_waiting_when_the_last_decision_was_a_launch()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            // The discriminating half: the line reports the CURRENT verdict, so a stale wait from
            // before a launch must not be printed as if the queue were still waiting on it.
            var at = new DateTimeOffset(2026, 9, 6, 1, 30, 0, TimeSpan.Zero);
            var key = await QueueDecisionLedgerStore.AppendAsync(
                new QueueDecisionEntry(at, "t1", QueueDecisionEntry.Waited, "memory", 2.0, 1.4, 2.0),
                previousVerdictKey: null, BatonPaths.QueueDecisionLedgerFile, Ct);
            await QueueDecisionLedgerStore.AppendAsync(
                new QueueDecisionEntry(at.AddMinutes(1), "t1", QueueDecisionEntry.Launched, null, 2.0, 3.4, 2.0),
                key, BatonPaths.QueueDecisionLedgerFile, Ct);

            var output = new StringWriter();
            await QueueCommand.ExecuteAsync(new QueueOptions(QueueVerb.List), output, Ct);

            Assert.DoesNotContain("Waiting on", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    /// <summary>
    /// The queue keeps the original indeterminate transition as its audit fact, but its listing must
    /// read the room's later conductor settlement rather than repeat a remedy that has already run.
    /// This drives the real run, resolve, persisted queue reload, and queue-list command boundaries.
    /// </summary>
    [Fact]
    public async Task List_replaces_a_stale_indeterminate_remedy_with_the_durable_conductor_settlement()
    {
        var home = CreateTempHome();
        var room = Path.Combine(home, "room");
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var workflowPath = Path.Combine(home, "workflow.json");
            var bindingsPath = Path.Combine(home, "bindings.json");
            var definition = new WorkflowDefinition(
                new WorkflowTemplateId("queue-settlement"), 1,
                [new WorkflowStepDefinition(new StepId("a"), "a", [], ["advice.md"], [], new RetryPolicy(1))]);
            await File.WriteAllTextAsync(workflowPath, JsonSerializer.Serialize(definition), Ct);
            await File.WriteAllTextAsync(bindingsPath, JsonSerializer.Serialize(new Dictionary<string, WorkerBindingConfigEntry>
            {
                ["a"] = new("shell", new WorkerContract("a", [], [new ProducedOutput("advice.md")], []),
                    PromptTemplate: "exit 1", Timeout: TimeSpan.FromSeconds(30)),
            }), Ct);

            var run = await RunCommand.ExecuteAsync(new RunOptions(workflowPath, bindingsPath, room), Adapters, cancellationToken: Ct);
            var executionId = Assert.Single(run.State.Steps).LatestExecutionId!.Value;
            await using (var writer = new FlowEventLogWriter(Path.Combine(room, BatonPaths.FlowLogFileName)))
            {
                await writer.AppendAsync(new FlowEvent.VerifyFailed(executionId, ["fmt-check"], "GATES: FAIL 1 of 1"), Ct);
            }

            const string historicalError = "room room settled indeterminate: GATES: FAIL 1 of 1 — resolve it with 'baton resolve' and redispatch if you want it redone; awaiting conductor resolution";
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
            {
                Items = [new QueueItem
                {
                    Tag = "2268-lane", Role = "implement", Workspace = home,
                    SpecFile = BatonPaths.QueueSpecFile("2268-lane"), State = QueueItemState.Failed,
                    RoomDirectory = room, Error = historicalError,
                }],
            }, Ct);

            await ResolveCommand.ExecuteAsync(
                new ResolveOptions(room, executionId.Value, Accept: false, Reason: "operator verified the work already landed", Close: true), Ct);

            var output = new StringWriter();
            Assert.Equal(0, await QueueCommand.ExecuteAsync(new QueueOptions(QueueVerb.List), output, Ct));
            Assert.DoesNotContain("awaiting conductor resolution", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("resolve it with 'baton resolve'", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("settlement: conductor closed (Failed)", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("operator verified the work already landed", output.ToString(), StringComparison.Ordinal);
            Assert.Equal(historicalError, Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items).Error);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task List_reloads_and_distinguishes_capture_settlements_without_changing_unresolved_or_legacy_rows()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var acceptedRoom = await CreateCaptureRoomAsync(home, "accepted", accepted: true);
            var rejectedRoom = await CreateCaptureRoomAsync(home, "rejected", accepted: false);
            var unresolvedRoom = await CreateCaptureRoomAsync(home, "unresolved", accepted: null);
            const string stale = "settled indeterminate — resolve it with 'baton resolve'; awaiting conductor resolution";

            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
            {
                Items =
                [
                    SettlementQueueItem("accepted", acceptedRoom, stale),
                    SettlementQueueItem("rejected", rejectedRoom, stale),
                    SettlementQueueItem("unresolved", unresolvedRoom, stale),
                    SettlementQueueItem("legacy", roomDirectory: null, stale),
                ],
            }, Ct);

            var output = new StringWriter();
            Assert.Equal(0, await QueueCommand.ExecuteAsync(new QueueOptions(QueueVerb.List), output, Ct));
            var printed = output.ToString();

            Assert.Contains("settlement: conductor accepted capture (Succeeded)", printed, StringComparison.Ordinal);
            Assert.Contains("settlement: conductor rejected capture (Failed): capture rejected", printed, StringComparison.Ordinal);
            Assert.Equal(2, printed.Split("awaiting conductor resolution", StringSplitOptions.None).Length - 1);
            Assert.Equal(stale, (await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items[0].Error);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task List_fails_when_a_stale_remedy_references_an_unreadable_room()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
            {
                Items = [new QueueItem
                {
                    Tag = "unreadable-room", Role = "implement", Workspace = home,
                    SpecFile = BatonPaths.QueueSpecFile("unreadable-room"), State = QueueItemState.Failed,
                    RoomDirectory = Path.Combine(home, "missing-room"),
                    Error = "settled indeterminate — awaiting conductor resolution",
                }],
            }, Ct);

            var ex = await Assert.ThrowsAsync<QueueStoreException>(() => QueueCommand.ExecuteAsync(
                new QueueOptions(QueueVerb.List), TextWriter.Null, Ct));
            Assert.Contains("Could not read durable terminal state", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Cancel_retains_a_queued_items_history_and_records_the_cancellation()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var workspace = Path.Combine(home, "w2159");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(BatonPaths.QueueSpecsDirectory);
            var spec = BatonPaths.QueueSpecFile("2159-lane");
            await File.WriteAllTextAsync(spec, "the queued brief", Ct);
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with
            {
                Items = [new QueueItem
                {
                    Tag = "2159-lane",
                    Role = "implement",
                    Stage = WorkStage.Implement,
                    Issue = 2159,
                    Workspace = workspace,
                    SpecFile = spec,
                }],
            }, Ct);

            var output = new StringWriter();
            Assert.Equal(0, await QueueCommand.ExecuteAsync(new QueueOptions(QueueVerb.Cancel, Tag: "2159-lane"), output, Ct));

            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(QueueItemState.Cancelled, item.State);
            Assert.NotNull(item.CancelledAt);
            Assert.True(Directory.Exists(workspace));
            Assert.Equal("the queued brief", await File.ReadAllTextAsync(spec, Ct));
            Assert.Contains("retained", output.ToString(), StringComparison.Ordinal);
            var fact = Assert.Single(await QueueDecisionLedgerStore.ReadAllAsync(BatonPaths.QueueDecisionLedgerFile, Ct));
            Assert.Equal(QueueDecisionEntry.Cancelled, fact.Decision);
            Assert.Equal("2159-lane", fact.Tag);

            var repeat = await Assert.ThrowsAsync<CliArgumentException>(() => QueueCommand.ExecuteAsync(
                new QueueOptions(QueueVerb.Cancel, Tag: "2159-lane"), TextWriter.Null, Ct));
            Assert.Contains("already cancelled", repeat.Message, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Cancel_refuses_after_a_readiness_mutation_is_claimed()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            await QueueStore.MutateAsync(BatonPaths.QueueFile, state => state with
            {
                Items =
                [
                    new QueueItem
                    {
                        Tag = "2131-lane",
                        Role = "review",
                        Workspace = home,
                        SpecFile = BatonPaths.QueueSpecFile("2131-lane"),
                        Stage = WorkStage.Ready,
                        State = QueueItemState.Queued,
                        ReadinessMutationClaim = "active-claim",
                    },
                ],
            }, Ct);

            var refusal = await Assert.ThrowsAsync<CliArgumentException>(() => QueueCommand.ExecuteAsync(
                new QueueOptions(QueueVerb.Cancel, Tag: "2131-lane"), TextWriter.Null, Ct));

            Assert.Contains("in-flight pull-request readiness update", refusal.Message, StringComparison.Ordinal);
            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(QueueItemState.Queued, item.State);
            Assert.Null(item.CancelledAt);
            Assert.Equal("active-claim", item.ReadinessMutationClaim);
            Assert.Empty(await QueueDecisionLedgerStore.ReadAllAsync(BatonPaths.QueueDecisionLedgerFile, Ct));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Cancel_retry_backfills_a_ledger_fact_that_failed_after_the_queue_write()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var workspace = Path.Combine(home, "w2159");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(BatonPaths.QueueSpecsDirectory);
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with
            {
                Items =
                [
                    new QueueItem
                    {
                        Tag = "2159-lane",
                        Role = "implement",
                        Workspace = workspace,
                        SpecFile = BatonPaths.QueueSpecFile("2159-lane"),
                    },
                ],
            }, Ct);

            // A directory at the ledger's filename makes its append fail after the queue mutation,
            // without depending on the machine's permissions.
            Directory.CreateDirectory(BatonPaths.QueueDecisionLedgerFile);
            var failure = await Record.ExceptionAsync(() => QueueCommand.ExecuteAsync(
                new QueueOptions(QueueVerb.Cancel, Tag: "2159-lane"), TextWriter.Null, Ct));
            Assert.True(failure is IOException or UnauthorizedAccessException);

            var cancelled = (await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items.Single();
            Assert.Equal(QueueItemState.Cancelled, cancelled.State);
            Assert.NotNull(cancelled.CancelledAt);

            Directory.Delete(BatonPaths.QueueDecisionLedgerFile);
            var repeat = await Assert.ThrowsAsync<CliArgumentException>(() => QueueCommand.ExecuteAsync(
                new QueueOptions(QueueVerb.Cancel, Tag: "2159-lane"), TextWriter.Null, Ct));
            Assert.Contains("already cancelled", repeat.Message, StringComparison.Ordinal);

            var fact = Assert.Single(await QueueDecisionLedgerStore.ReadAllAsync(BatonPaths.QueueDecisionLedgerFile, Ct));
            Assert.Equal(QueueDecisionEntry.Cancelled, fact.Decision);
            Assert.Equal("2159-lane", fact.Tag);
            Assert.Equal(cancelled.CancelledAt, fact.At);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Import_refuses_a_collision_with_a_retained_cancelled_item()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var workspace = Path.Combine(home, "w2159");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(BatonPaths.QueueSpecsDirectory);
            var spec = BatonPaths.QueueSpecFile("2159-lane");
            var cancelledAt = new DateTimeOffset(2026, 9, 9, 20, 0, 0, TimeSpan.Zero);
            await File.WriteAllTextAsync(spec, "the cancelled brief", Ct);
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with
            {
                Items =
                [
                    new QueueItem
                    {
                        Tag = "2159-lane",
                        Role = "implement",
                        Workspace = workspace,
                        SpecFile = spec,
                        State = QueueItemState.Cancelled,
                        CancelledAt = cancelledAt,
                    },
                ],
            }, Ct);
            await QueueDecisionLedgerStore.AppendCancellationAsync(
                cancelledAt, "2159-lane", BatonPaths.QueueDecisionLedgerFile, Ct);

            var import = Path.Combine(home, "scratchpad.json");
            await File.WriteAllTextAsync(
                import,
                """[{ "tag": "2159-lane", "role": "implement", "workspace": "C:\\scratch\\w2159", "launched": false }]""",
                Ct);

            var refusal = await Assert.ThrowsAsync<CliArgumentException>(() => QueueCommand.ExecuteAsync(
                new QueueOptions(QueueVerb.Import, ImportFilePath: import), TextWriter.Null, Ct));
            Assert.Contains("cancelled before launch", refusal.Message, StringComparison.Ordinal);

            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(QueueItemState.Cancelled, item.State);
            Assert.Equal(cancelledAt, item.CancelledAt);
            Assert.Equal("the cancelled brief", await File.ReadAllTextAsync(spec, Ct));
            var fact = Assert.Single(await QueueDecisionLedgerStore.ReadAllAsync(BatonPaths.QueueDecisionLedgerFile, Ct));
            Assert.Equal(QueueDecisionEntry.Cancelled, fact.Decision);
            Assert.Equal(cancelledAt, fact.At);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Import_normalizes_and_persists_declared_skills()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var import = Path.Combine(home, "scratchpad.json");
            await File.WriteAllTextAsync(
                import,
                """[{ "tag": "skills", "role": "review", "workspace": "C:\\scratch\\w2231", "skills": [" house-style ", "thorough-review", "house-style"] }]""",
                Ct);

            await QueueCommand.ExecuteAsync(
                new QueueOptions(QueueVerb.Import, ImportFilePath: import), TextWriter.Null, Ct);

            var item = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(["house-style", "thorough-review"], item.Skills);
            Assert.Equal(WorkspaceOrigins.ImportedUnknown, item.WorkspaceOrigin);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Import_refuses_a_null_skill_entry_without_writing_the_queue()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var import = Path.Combine(home, "scratchpad.json");
            await File.WriteAllTextAsync(
                import,
                """[{ "tag": "skills", "role": "review", "workspace": "C:\\scratch\\w2231", "skills": [null] }]""",
                Ct);

            var refusal = await Assert.ThrowsAsync<CliArgumentException>(() => QueueCommand.ExecuteAsync(
                new QueueOptions(QueueVerb.Import, ImportFilePath: import), TextWriter.Null, Ct));

            Assert.Contains("cannot be null", refusal.Message, StringComparison.Ordinal);
            Assert.Contains("remove null skill entries", refusal.TryInvocation!, StringComparison.Ordinal);
            Assert.False(File.Exists(BatonPaths.QueueFile));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Cancel_reports_a_missing_tag_without_recording_a_false_cancellation()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var refusal = await Assert.ThrowsAsync<CliArgumentException>(() => QueueCommand.ExecuteAsync(
                new QueueOptions(QueueVerb.Cancel, Tag: "missing"), TextWriter.Null, Ct));

            Assert.Contains("does not exist", refusal.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(BatonPaths.QueueDecisionLedgerFile));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Cancel_refuses_a_queued_but_started_lifecycle_without_freeing_its_wip_slot()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var started = new QueueItem
            {
                Tag = "started",
                Role = "implement",
                Stage = WorkStage.Ready,
                Issue = 2314,
                AttemptId = FleetAttemptId.New(),
                State = QueueItemState.Queued,
                Workspace = home,
                SpecFile = Path.Combine(home, "started.md"),
            };
            var next = new QueueItem
            {
                Tag = "new",
                Role = "implement",
                Stage = WorkStage.Implement,
                Issue = 2315,
                Workspace = home,
                SpecFile = Path.Combine(home, "new.md"),
            };
            await QueueStore.MutateAsync(BatonPaths.QueueFile,
                snapshot => snapshot with { Items = [started, next] }, Ct);

            var refusal = await Assert.ThrowsAsync<CliArgumentException>(() => QueueCommand.ExecuteAsync(
                new QueueOptions(QueueVerb.Cancel, Tag: "started"), TextWriter.Null, Ct));
            Assert.Contains("already-started lifecycle", refusal.Message, StringComparison.Ordinal);
            Assert.Contains("queue retire", refusal.TryInvocation, StringComparison.Ordinal);

            var retained = await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct);
            Assert.Equal(QueueItemState.Queued, retained.Items[0].State);
            Assert.Null(retained.Items[0].CancelledAt);
            Assert.Empty(await QueueDecisionLedgerStore.ReadAllAsync(BatonPaths.QueueDecisionLedgerFile, Ct));
            var decision = QueueScheduler.Decide(DateTimeOffset.UtcNow, retained.Items, 0, 8,
                new QueueSettings { MaxActiveLifecycles = 1 }, null, held: false);
            Assert.Equal(QueueWaitReason.LifecycleCap, decision.WaitReason);
            Assert.Equal(1, decision.Context!.Portfolio.ActiveLifecycles);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Cancel_refuses_a_launched_item_with_the_room_cancel_remedy()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var room = Path.Combine(home, "rooms", "queue-2159-lane-abcd1234");
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with
            {
                Items = [new QueueItem
                {
                    Tag = "2159-lane",
                    Role = "implement",
                    Workspace = Path.Combine(home, "w2159"),
                    SpecFile = BatonPaths.QueueSpecFile("2159-lane"),
                    State = QueueItemState.Launched,
                    RoomDirectory = room,
                }],
            }, Ct);

            var refusal = await Assert.ThrowsAsync<CliArgumentException>(() => QueueCommand.ExecuteAsync(
                new QueueOptions(QueueVerb.Cancel, Tag: "2159-lane"), TextWriter.Null, Ct));

            Assert.Equal("cancel the launched lane with 'baton cancel " + room + "'.", refusal.TryInvocation);
            Assert.Equal(QueueItemState.Launched, (await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items.Single().State);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Retire_refuses_a_roomless_failed_row_that_may_have_a_late_live_room()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with
            {
                Items = [new QueueItem
                {
                    Tag = "late-room", Role = "implement", Workspace = home,
                    SpecFile = BatonPaths.QueueSpecFile("late-room"), Stage = WorkStage.Implement,
                    State = QueueItemState.Failed,
                }],
            }, Ct);

            var refusal = await Assert.ThrowsAsync<CliArgumentException>(() => QueueCommand.ExecuteAsync(
                new QueueOptions(QueueVerb.Retire, Tag: "late-room", Reason: "operator handled"), TextWriter.Null, Ct));

            Assert.Contains("insufficient settled failure evidence", refusal.Message, StringComparison.Ordinal);
            Assert.Null((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items.Single().Retirement);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Retire_accepts_an_exactly_refused_roomless_attempt_with_a_terminal_parent_room()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var tag = "legacy-refused";
            var parent = new FleetAttemptId("parent-legacy-refused");
            var current = new FleetAttemptId("current-legacy-refused");
            var room = Path.Combine(home, "rooms", "queue-legacy-refused-parent");
            Directory.CreateDirectory(room);
            await TerminalSentinelWriter.WriteAsync(
                room, new WorkflowStatusView(WorkflowOutcome.Failed, [], [], "parent terminal"), Ct);
            var log = new FleetEventLog(
                BatonPaths.FleetEventsFile, BatonPaths.FleetEventsRolloverFile, maxLiveBytes: 100_000);
            var at = DateTimeOffset.Parse("2026-09-15T14:00:00Z");
            await log.Append(new FleetEventDraft(
                FleetEventKind.AttemptStarted, $"attempt-started:{parent.Value}", at,
                AttemptId: parent, WorkId: new FleetWorkId(tag),
                RoomId: new FleetRoomId(BatonPaths.RecordKey(room))), Ct);
            await log.Append(new FleetEventDraft(
                FleetEventKind.AttemptSettled, $"attempt-settled:{parent.Value}", at.AddMinutes(1),
                AttemptId: parent, WorkId: new FleetWorkId(tag),
                RoomId: new FleetRoomId(BatonPaths.RecordKey(room)), Outcome: "Failed"), Ct);
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with
            {
                Items = [new QueueItem
                {
                    Tag = tag, Role = "implement", Workspace = home,
                    SpecFile = BatonPaths.QueueSpecFile(tag), Stage = WorkStage.Continue,
                    State = QueueItemState.Failed, AttemptId = current, ParentAttemptId = parent,
                    LaunchedAt = at.AddMinutes(2),
                    LastAdmission = new TaskRequirementAdmission(
                        [], ["repository-read"], TaskRequirementAdmission.Admitted),
                }],
            }, Ct);

            await Assert.ThrowsAsync<CliArgumentException>(() => QueueCommand.ExecuteAsync(
                new QueueOptions(QueueVerb.Retire, Tag: tag, Reason: "missing exact refusal"),
                TextWriter.Null, Ct));
            // Force the terminal parent into retained rollover while the refusal stays live.
            var rotatedLog = new FleetEventLog(BatonPaths.FleetEventsFile,
                BatonPaths.FleetEventsRolloverFile,
                maxLiveBytes: new FileInfo(BatonPaths.FleetEventsFile).Length + 1);
            await rotatedLog.Append(new FleetEventDraft(
                FleetEventKind.AdmissionDecided, $"admission:{current.Value}", at.AddMinutes(2),
                AttemptId: current, ParentAttemptId: parent, WorkId: new FleetWorkId(tag),
                AdmissionDecision: TaskRequirementAdmission.Admitted), Ct);
            await rotatedLog.Append(new FleetEventDraft(
                FleetEventKind.AttemptRefused, $"attempt-refused:{current.Value}", at.AddMinutes(2),
                AttemptId: current, ParentAttemptId: parent, WorkId: new FleetWorkId(tag),
                Outcome: "Queue continue cannot launch without its canonical repository, recorded branch, and tracked open PR."), Ct);

            var retainedLive = await File.ReadAllBytesAsync(BatonPaths.FleetEventsFile, Ct);
            foreach (var brokenTail in new[] { "{", "{broken\n" })
            {
                await File.AppendAllTextAsync(BatonPaths.FleetEventsFile, brokenTail, Ct);
                var unreadable = await Assert.ThrowsAsync<QueueCommand.LegacyRetirementProofReadException>(() => QueueCommand.ExecuteAsync(
                    new QueueOptions(QueueVerb.Retire, Tag: tag, Reason: "unreadable fleet proof"),
                    TextWriter.Null, Ct));
                Assert.Contains("proof read failed", unreadable.Message, StringComparison.Ordinal);
                await File.WriteAllBytesAsync(BatonPaths.FleetEventsFile, retainedLive, Ct);
            }
            var currentItem = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            using (var lease = QueueCommand.TryAcquireLegacyRetirementProof(currentItem))
            {
                Assert.NotNull(lease);
                foreach (var source in new[] { BatonPaths.FleetEventsFile,
                    BatonPaths.FleetEventsRolloverFile,
                    Path.Combine(room, TerminalSentinelWriter.TerminalSentinelFileName) })
                {
                    Assert.Throws<IOException>(() => new FileStream(source, FileMode.Append,
                        FileAccess.Write, FileShare.Read).Dispose());
                }
            }

            await QueueCommand.ExecuteAsync(
                new QueueOptions(QueueVerb.Retire, Tag: tag, Reason: "typed refusal and terminal parent"),
                TextWriter.Null, Ct);

            Assert.Equal(QueueRetirement.Operator,
                Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items).Retirement?.Kind);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Retire_accepts_a_legacy_cancelled_next_stage_with_a_recorded_terminal_parent_room()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var tag = "legacy-cancelled";
            var parent = new FleetAttemptId("parent-legacy-cancelled");
            var room = Path.Combine(home, "rooms", "queue-legacy-cancelled-parent");
            Directory.CreateDirectory(room);
            await TerminalSentinelWriter.WriteAsync(
                room, new WorkflowStatusView(WorkflowOutcome.Succeeded, [], [], null), Ct);
            var log = new FleetEventLog(
                BatonPaths.FleetEventsFile, BatonPaths.FleetEventsRolloverFile, maxLiveBytes: 100_000);
            var at = DateTimeOffset.Parse("2026-09-15T14:00:00Z");
            await log.Append(new FleetEventDraft(
                FleetEventKind.AttemptStarted, $"attempt-started:{parent.Value}", at,
                AttemptId: parent, WorkId: new FleetWorkId(tag),
                RoomId: new FleetRoomId(BatonPaths.RecordKey(room))), Ct);
            await log.Append(new FleetEventDraft(
                FleetEventKind.AttemptSettled, $"attempt-settled:{parent.Value}", at.AddMinutes(1),
                AttemptId: parent, WorkId: new FleetWorkId(tag),
                RoomId: new FleetRoomId(BatonPaths.RecordKey(room)), Outcome: "Succeeded"), Ct);
            var earlierRoom = Path.Combine(home, "rooms", "queue-legacy-cancelled-earlier");
            Directory.CreateDirectory(earlierRoom);
            await QueueDecisionLedgerStore.AppendAsync(new QueueDecisionEntry(
                at.AddMinutes(-10), tag, QueueDecisionEntry.Launched, null, 0, 8, 2,
                Room: earlierRoom), previousVerdictKey: null, BatonPaths.QueueDecisionLedgerFile, Ct);
            await QueueDecisionLedgerStore.AppendAsync(new QueueDecisionEntry(
                at, tag, QueueDecisionEntry.Launched, null, 0, 8, 2, Room: room),
                previousVerdictKey: null, BatonPaths.QueueDecisionLedgerFile, Ct);
            var cancelledAt = at.AddMinutes(2);
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with
            {
                Items = [new QueueItem
                {
                    Tag = tag, Role = "review", Workspace = home,
                    SpecFile = BatonPaths.QueueSpecFile(tag), Stage = WorkStage.Review,
                    State = QueueItemState.Cancelled, CancelledAt = cancelledAt,
                    ParentAttemptId = parent, Round = 1,
                }],
            }, Ct);

            await Assert.ThrowsAsync<CliArgumentException>(() => QueueCommand.ExecuteAsync(
                new QueueOptions(QueueVerb.Retire, Tag: tag, Reason: "missing keyed cancellation"),
                TextWriter.Null, Ct));
            await QueueDecisionLedgerStore.AppendCancellationAsync(
                cancelledAt, tag, BatonPaths.QueueDecisionLedgerFile, Ct);

            var missingSentinel = await Assert.ThrowsAsync<QueueCommand.LegacyRetirementProofReadException>(() => QueueCommand.ExecuteAsync(
                new QueueOptions(QueueVerb.Retire, Tag: tag, Reason: "earlier room not terminal"),
                TextWriter.Null, Ct));
            Assert.Contains("terminal.json", missingSentinel.Message, StringComparison.Ordinal);
            await TerminalSentinelWriter.WriteAsync(earlierRoom,
                new WorkflowStatusView(WorkflowOutcome.Failed, [], [], null), Ct);
            var retainedDecisions = await File.ReadAllBytesAsync(BatonPaths.QueueDecisionLedgerFile, Ct);
            await File.AppendAllTextAsync(BatonPaths.QueueDecisionLedgerFile, "{broken\n", Ct);
            var unreadableDecisions = await Assert.ThrowsAsync<QueueCommand.LegacyRetirementProofReadException>(() =>
                QueueCommand.ExecuteAsync(new QueueOptions(QueueVerb.Retire, Tag: tag,
                    Reason: "corrupt keyed decision proof"), TextWriter.Null, Ct));
            Assert.Contains("proof read failed", unreadableDecisions.Message, StringComparison.Ordinal);
            await File.WriteAllBytesAsync(BatonPaths.QueueDecisionLedgerFile, retainedDecisions, Ct);
            var currentItem = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            using (var lease = QueueCommand.TryAcquireLegacyRetirementProof(currentItem))
            {
                Assert.NotNull(lease);
                foreach (var source in new[] { BatonPaths.QueueDecisionLedgerFile,
                    Path.Combine(earlierRoom, TerminalSentinelWriter.TerminalSentinelFileName) })
                {
                    Assert.Throws<IOException>(() => new FileStream(source, FileMode.Append,
                        FileAccess.Write, FileShare.Read).Dispose());
                }
            }

            await QueueCommand.ExecuteAsync(
                new QueueOptions(QueueVerb.Retire, Tag: tag, Reason: "cancelled next stage and terminal parent"),
                TextWriter.Null, Ct);

            Assert.Equal(QueueRetirement.Operator,
                Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items).Retirement?.Kind);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Theory]
    [InlineData("started-current")]
    [InlineData("wrong-parent")]
    [InlineData("wrong-work-tag")]
    [InlineData("valid-plus-wrong-parent")]
    [InlineData("valid-plus-wrong-work-tag")]
    [InlineData("valid-plus-wrong-admission")]
    [InlineData("valid-plus-wrong-launch-timestamp")]
    [InlineData("nonterminal-parent")]
    public async Task Retire_refuses_legacy_proof_with_a_live_attempt_or_broken_parent(string brokenProof)
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var tag = "legacy-broken-proof";
            var parent = new FleetAttemptId("parent-broken-proof");
            var current = new FleetAttemptId("current-broken-proof");
            var room = Path.Combine(home, "rooms", "queue-legacy-broken-parent");
            Directory.CreateDirectory(room);
            await TerminalSentinelWriter.WriteAsync(room,
                new WorkflowStatusView(brokenProof == "nonterminal-parent"
                    ? WorkflowOutcome.Running : WorkflowOutcome.Failed, [], [], null), Ct);
            var log = new FleetEventLog(BatonPaths.FleetEventsFile,
                BatonPaths.FleetEventsRolloverFile, maxLiveBytes: 100_000);
            var at = DateTimeOffset.Parse("2026-09-15T14:00:00Z");
            await log.Append(new FleetEventDraft(FleetEventKind.AttemptStarted,
                $"attempt-started:{parent.Value}", at, AttemptId: parent,
                WorkId: new FleetWorkId(tag), RoomId: new FleetRoomId(BatonPaths.RecordKey(room))), Ct);
            await log.Append(new FleetEventDraft(FleetEventKind.AttemptSettled,
                $"attempt-settled:{parent.Value}", at.AddMinutes(1), AttemptId: parent,
                WorkId: new FleetWorkId(tag), RoomId: new FleetRoomId(BatonPaths.RecordKey(room)),
                Outcome: WorkflowOutcome.Failed), Ct);
            if (brokenProof.StartsWith("valid-plus-", StringComparison.Ordinal))
            {
                await log.Append(new FleetEventDraft(FleetEventKind.AttemptRefused,
                    $"attempt-refused:{current.Value}", at.AddMinutes(2), AttemptId: current,
                    ParentAttemptId: parent, WorkId: new FleetWorkId(tag)), Ct);
            }
            if (brokenProof is "valid-plus-wrong-admission" or "valid-plus-wrong-launch-timestamp")
            {
                await log.Append(new FleetEventDraft(FleetEventKind.AdmissionDecided,
                    $"admission:{current.Value}:conflict", at.AddMinutes(2), AttemptId: current,
                    ParentAttemptId: brokenProof == "valid-plus-wrong-admission"
                        ? new FleetAttemptId("some-other-parent") : parent,
                    WorkId: new FleetWorkId(tag),
                    AdmissionDecision: TaskRequirementAdmission.Admitted), Ct);
            }
            else
            {
                await log.Append(new FleetEventDraft(FleetEventKind.AttemptRefused,
                    $"attempt-refused:{current.Value}:conflict", at.AddMinutes(2), AttemptId: current,
                    ParentAttemptId: brokenProof is "wrong-parent" or "valid-plus-wrong-parent"
                        ? new FleetAttemptId("some-other-parent") : parent,
                    WorkId: new FleetWorkId(brokenProof is "wrong-work-tag" or "valid-plus-wrong-work-tag"
                        ? "other-work" : tag)), Ct);
            }
            if (brokenProof == "started-current")
            {
                await log.Append(new FleetEventDraft(FleetEventKind.AttemptStarted,
                    $"attempt-started:{current.Value}", at.AddMinutes(3), AttemptId: current,
                    ParentAttemptId: parent, WorkId: new FleetWorkId(tag),
                    RoomId: new FleetRoomId(BatonPaths.RecordKey(room))), Ct);
            }
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with
            {
                Items = [new QueueItem
                {
                    Tag = tag, Role = "implement", Workspace = home,
                    SpecFile = BatonPaths.QueueSpecFile(tag), Stage = WorkStage.Continue,
                    State = QueueItemState.Failed, AttemptId = current, ParentAttemptId = parent,
                    LaunchedAt = brokenProof switch
                    {
                        "valid-plus-wrong-launch-timestamp" => at.AddMinutes(3),
                        "started-current" => at.AddMinutes(2),
                        _ => null,
                    },
                }],
            }, Ct);

            await Assert.ThrowsAsync<CliArgumentException>(() => QueueCommand.ExecuteAsync(
                new QueueOptions(QueueVerb.Retire, Tag: tag, Reason: "broken legacy proof"),
                TextWriter.Null, Ct));
            Assert.Null(Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items).Retirement);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public void Legacy_retirement_CAS_refuses_a_changed_attempt_or_stage()
    {
        var observed = new QueueItem
        {
            Tag = "cas-legacy",
            Role = "implement",
            Workspace = "unused",
            SpecFile = "unused",
            Stage = WorkStage.Continue,
            State = QueueItemState.Failed,
            AttemptId = new FleetAttemptId("current"),
            ParentAttemptId = new FleetAttemptId("parent"),
        };
        Assert.True(QueueCommand.SameRetirementAttempt(observed, observed));
        Assert.False(QueueCommand.SameRetirementAttempt(observed,
            observed with { AttemptId = new FleetAttemptId("late-launch") }));
        Assert.False(QueueCommand.SameRetirementAttempt(observed,
            observed with { RoomDirectory = "late-room" }));
        Assert.False(QueueCommand.SameRetirementAttempt(observed,
            observed with { LaunchedAt = DateTimeOffset.Parse("2026-09-15T14:00:00Z") }));
        Assert.False(QueueCommand.SameRetirementAttempt(observed,
            observed with { Stage = WorkStage.Review }));
    }

    [Fact]
    public async Task Retire_accepts_a_failed_room_with_terminal_journal_proof_but_no_sentinel()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var (item, log) = await WriteRetirementRoomAsync(home, terminal: true);
            var room = item.RoomDirectory!;
            var journalBefore = await File.ReadAllBytesAsync(log, Ct);
            Assert.False(File.Exists(Path.Combine(room, TerminalSentinelWriter.TerminalSentinelFileName)));

            Assert.Equal(0, await QueueCommand.ExecuteAsync(
                new QueueOptions(QueueVerb.Retire, Tag: "journal-terminal", Reason: "operator handled"),
                TextWriter.Null, Ct));
            Assert.Equal(QueueRetirement.Operator,
                Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items).Retirement!.Kind);
            Assert.Equal(journalBefore, await File.ReadAllBytesAsync(log, Ct));
            Assert.False(File.Exists(Path.Combine(room, TerminalSentinelWriter.TerminalSentinelFileName)));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Theory]
    [InlineData("open")]
    [InlineData("missing-snapshot")]
    [InlineData("malformed-journal")]
    [InlineData("ordinary-terminal")]
    public async Task Retire_refuses_missing_or_nonterminal_journal_proof(string evidence)
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var (item, log) = await WriteRetirementRoomAsync(home, terminal: evidence != "open",
                deadPumpDiagnostic: evidence != "ordinary-terminal");
            if (evidence == "missing-snapshot")
            {
                FileCleanup.EnsureDeleted(Path.Combine(item.RoomDirectory!, BatonPaths.SnapshotFileName));
            }
            else if (evidence == "malformed-journal")
            {
                await File.AppendAllTextAsync(log, "{broken\n", Ct);
            }

            await Assert.ThrowsAsync<CliArgumentException>(() => QueueCommand.ExecuteAsync(
                new QueueOptions(QueueVerb.Retire, Tag: item.Tag, Reason: "operator handled"), TextWriter.Null, Ct));
            Assert.Null(Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items).Retirement);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task A_late_journal_append_invalidates_terminal_retirement_proof_at_mutation()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var (item, log) = await WriteRetirementRoomAsync(home, terminal: true);
            var proof = await QueueCommand.ReadTerminalRoomProofAsync(item, Ct);
            Assert.True(proof.IsProven);
            Assert.True(QueueCommand.HasTerminalRoomProofAtMutation(item, proof, out var lease));
            using (lease)
            {
                // This is the writer's exact open/share shape. The held lease spans the queue
                // callback and its subsequent write, so a resumed append cannot slip between them.
                Assert.Throws<IOException>(() => new FileStream(log, FileMode.Append,
                    FileAccess.Write, FileShare.Read).Dispose());
            }

            var stepId = new StepId("implement");
            var resumed = new ExecutionRequest(
                new ExecutionId("resumed-execution"), new WorkflowId("retire-journal"), stepId,
                "implement", [], [], TimeSpan.FromMinutes(1), [], new Dictionary<StepId, ExecutionId>());
            await using (var writer = new FlowEventLogWriter(log))
            {
                await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(resumed, 2, null), Ct);
            }

            Assert.False(QueueCommand.HasTerminalRoomProofAtMutation(item, proof, out var changedLease));
            Assert.Null(changedLease);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task A_dead_pump_journal_lease_remains_held_through_the_queue_write()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var (item, log) = await WriteRetirementRoomAsync(home, terminal: true);
            var proof = await QueueCommand.ReadTerminalRoomProofAsync(item, Ct);
            QueueCommand.RoomJournalLease? lease = null;
            try
            {
                await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot =>
                {
                    Assert.True(QueueCommand.HasTerminalRoomProofAtMutation(item, proof, out lease));
                    return snapshot with
                    {
                        Items = snapshot.Items.Select(current => current.Tag == item.Tag
                            ? current with
                            {
                                Retirement = new QueueRetirement(QueueRetirement.Operator,
                                    DateTimeOffset.UtcNow, "operator handled"),
                            }
                            : current).ToList(),
                    };
                }, Ct);
                // MutateAsync has returned only after writing queue.json, while the same lease
                // acquired in its callback is still held. A resumed journal writer cannot interleave.
                Assert.NotNull(Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items).Retirement);
                Assert.Throws<IOException>(() => new FileStream(log, FileMode.Append,
                    FileAccess.Write, FileShare.Read).Dispose());
            }
            finally
            {
                lease?.Dispose();
            }
            using var resumedWriter = new FileStream(log, FileMode.Append, FileAccess.Write, FileShare.Read);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    private static async Task<(QueueItem Item, string Log)> WriteRetirementRoomAsync(
        string home, bool terminal, bool deadPumpDiagnostic = true)
    {
        var room = Path.Combine(home, "rooms", "journal-terminal");
        Directory.CreateDirectory(room);
        var stepId = new StepId("implement");
        var snapshot = SnapshotBinder.Bind(new WorkflowDefinition(
            new WorkflowTemplateId("retire-journal"), 1,
            [new WorkflowStepDefinition(stepId, "implement", [], [], [], new RetryPolicy(1))]));
        await SnapshotBinder.PersistAsync(snapshot, Path.Combine(room, BatonPaths.SnapshotFileName), Ct);
        var executionId = new ExecutionId("retire-journal-execution");
        var request = new ExecutionRequest(
            executionId, new WorkflowId("retire-journal"), stepId, "implement", [], [],
            TimeSpan.FromMinutes(1), [], new Dictionary<StepId, ExecutionId>());
        var log = Path.Combine(room, BatonPaths.FlowLogFileName);
        await using (var writer = new FlowEventLogWriter(log))
        {
            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(request, 1, null), Ct);
            if (terminal)
            {
                await writer.AppendAsync(new FlowEvent.ExecutionFailed(
                    executionId, FailureClassification.Permanent,
                    deadPumpDiagnostic ? DeadPumpProbe.FailureReasonPrefix + " fixture" : "ordinary terminal failure"), Ct);
            }
        }

        var item = new QueueItem
        {
            Tag = "journal-terminal",
            Role = "implement",
            Workspace = home,
            SpecFile = BatonPaths.QueueSpecFile("journal-terminal"),
            Stage = WorkStage.Implement,
            State = QueueItemState.Failed,
            RoomDirectory = room,
        };
        await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with { Items = [item] }, Ct);
        return (item, log);
    }

    [Fact]
    public async Task Restore_replays_the_retire_fact_before_committing_its_successor()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var at = new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
            var retire = new QueueDispositionOperation("retire-key", at, QueueDecisionEntry.Retired, "operator: handled");
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with
            {
                Items = [new QueueItem
                {
                    Tag = "restore-outbox", Role = "implement", Workspace = home,
                    SpecFile = BatonPaths.QueueSpecFile("restore-outbox"), Stage = WorkStage.Implement,
                    State = QueueItemState.Failed,
                    WorkspaceOrigin = WorkspaceOrigins.IssueProvisioned,
                    Retirement = new QueueRetirement(QueueRetirement.Operator, at, "handled"),
                    DispositionOperations = [retire],
                }],
            }, Ct);

            Assert.Equal(0, await QueueCommand.ExecuteAsync(
                new QueueOptions(QueueVerb.Restore, Tag: "restore-outbox", Reason: "resume attention"), TextWriter.Null, Ct));

            var item = (await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items.Single();
            Assert.Null(item.Retirement);
            Assert.Equal(WorkspaceOrigins.IssueProvisioned, item.WorkspaceOrigin);
            Assert.Equal([QueueDecisionEntry.Retired, QueueDecisionEntry.Restored], item.DispositionOutbox.Select(x => x.Decision));
            Assert.Equal([QueueDecisionEntry.Retired, QueueDecisionEntry.Restored],
                (await QueueDecisionLedgerStore.ReadAllAsync(BatonPaths.QueueDecisionLedgerFile, Ct)).Select(x => x.Decision));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Retire_replays_a_failed_restore_before_its_successor_and_fails_closed_until_that_replay_succeeds()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with
            {
                Items = [new QueueItem
                {
                    Tag = "ordered-retire", Role = "implement", Workspace = home,
                    SpecFile = BatonPaths.QueueSpecFile("ordered-retire"), Stage = WorkStage.Ready,
                    State = QueueItemState.Queued, Repository = "owner/repo", PullRequest = 42,
                }],
                PullRequestObservations = [new QueuePullRequestObservation(
                    "owner/repo", 42, PullRequestObservationStates.Closed, "head",
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null)],
            }, Ct);

            Assert.Equal(0, await QueueCommand.ExecuteAsync(
                new QueueOptions(QueueVerb.Retire, Tag: "ordered-retire", Reason: "handled"), TextWriter.Null, Ct));

            QueueDecisionLedgerStore.DispositionAppendFault = (_, operation) =>
                operation.Decision == QueueDecisionEntry.Restored ? new IOException("planned restore append failure") : null;
            await Assert.ThrowsAsync<IOException>(() => QueueCommand.ExecuteAsync(
                new QueueOptions(QueueVerb.Restore, Tag: "ordered-retire", Reason: "resume"), TextWriter.Null, Ct));

            var afterFailedRestore = (await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items.Single();
            Assert.Null(afterFailedRestore.Retirement);
            Assert.Equal([QueueDecisionEntry.Retired, QueueDecisionEntry.Restored],
                afterFailedRestore.DispositionOutbox.Select(operation => operation.Decision));

            await Assert.ThrowsAsync<IOException>(() => QueueCommand.ExecuteAsync(
                new QueueOptions(QueueVerb.Retire, Tag: "ordered-retire", Reason: "handled again"), TextWriter.Null, Ct));
            var afterFencedRetire = (await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items.Single();
            Assert.Null(afterFencedRetire.Retirement);
            Assert.Equal([QueueDecisionEntry.Retired, QueueDecisionEntry.Restored],
                afterFencedRetire.DispositionOutbox.Select(operation => operation.Decision));
            Assert.Equal([QueueDecisionEntry.Retired],
                (await QueueDecisionLedgerStore.ReadAllAsync(BatonPaths.QueueDecisionLedgerFile, Ct)).Select(entry => entry.Decision));

            QueueDecisionLedgerStore.DispositionAppendFault = null;
            Assert.Equal(0, await QueueCommand.ExecuteAsync(
                new QueueOptions(QueueVerb.Retire, Tag: "ordered-retire", Reason: "handled again"), TextWriter.Null, Ct));
            Assert.Equal([QueueDecisionEntry.Retired, QueueDecisionEntry.Restored, QueueDecisionEntry.Retired],
                (await QueueDecisionLedgerStore.ReadAllAsync(BatonPaths.QueueDecisionLedgerFile, Ct)).Select(entry => entry.Decision));
        }
        finally
        {
            QueueDecisionLedgerStore.DispositionAppendFault = null;
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Add_refuses_to_replace_a_retired_tag()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var brief = Path.Combine(home, "brief.md");
            await File.WriteAllTextAsync(brief, "new work", Ct);
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with
            {
                Items = [new QueueItem
                {
                    Tag = "retained", Role = "implement", Workspace = home,
                    SpecFile = BatonPaths.QueueSpecFile("retained"), Stage = WorkStage.Ready,
                    State = QueueItemState.Queued,
                    Retirement = new QueueRetirement(QueueRetirement.Merged, DateTimeOffset.UtcNow, "merged"),
                }],
            }, Ct);

            var refusal = await Assert.ThrowsAsync<CliArgumentException>(() => QueueCommand.ExecuteAsync(
                new QueueOptions(QueueVerb.Add, Tag: "retained", Role: "implement", SpecFilePath: brief, WorkspaceDirectory: home),
                TextWriter.Null, Ct));

            Assert.Contains("retired as 'merged'", refusal.Message, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    private static QueueItem SettlementQueueItem(string tag, string? roomDirectory, string error) => new()
    {
        Tag = tag,
        Role = "implement",
        Workspace = Directory.GetCurrentDirectory(),
        SpecFile = BatonPaths.QueueSpecFile(tag),
        State = QueueItemState.Failed,
        RoomDirectory = roomDirectory,
        Error = error,
    };

    private static async Task<string> CreateCaptureRoomAsync(string home, string name, bool? accepted)
    {
        var room = Path.Combine(home, name);
        var workflowPath = Path.Combine(home, $"{name}-workflow.json");
        var bindingsPath = Path.Combine(home, $"{name}-bindings.json");
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId($"queue-{name}"), 1,
            [new WorkflowStepDefinition(new StepId("a"), "a", [], ["advice.md"], [], new RetryPolicy(1))]);
        await File.WriteAllTextAsync(workflowPath, JsonSerializer.Serialize(definition), Ct);
        await File.WriteAllTextAsync(bindingsPath, JsonSerializer.Serialize(new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["a"] = new("shell", new WorkerContract("a", [], [new ProducedOutput("advice.md")], []),
                PromptTemplate: "exit 1", Timeout: TimeSpan.FromSeconds(30)),
        }), Ct);

        var run = await RunCommand.ExecuteAsync(new RunOptions(workflowPath, bindingsPath, room), Adapters, cancellationToken: Ct);
        var executionId = Assert.Single(run.State.Steps).LatestExecutionId!.Value;
        await using (var writer = new FlowEventLogWriter(Path.Combine(room, BatonPaths.FlowLogFileName)))
        {
            await writer.AppendAsync(new FlowEvent.ExecutionIndeterminate(
                executionId, "captured; awaiting conductor resolution", ".captured-response.md", ["advice.md"]), Ct);
        }

        var artifacts = Path.Combine(room, "artifacts", $"execution_{executionId.Value}");
        Directory.CreateDirectory(artifacts);
        await File.WriteAllTextAsync(Path.Combine(artifacts, ".captured-response.md"),
            "# Captured response\n\nanswer", Ct);
        if (accepted is { } decision)
        {
            await ResolveCommand.ExecuteAsync(new ResolveOptions(
                room, executionId.Value, decision, decision ? null : "capture rejected"), Ct);
        }

        return room;
    }
}
