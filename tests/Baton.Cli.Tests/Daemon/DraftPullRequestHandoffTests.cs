using Baton.Accounting;
using Baton.Cli;
using Baton.Cli.Daemon;
using Baton.Domain;
using Baton.Queue;
using Baton.Status;
using Baton.Tests.Shared;
using Baton.Vendors;
using Baton.CrashTestHost;
using System.Text.Json;
using Xunit;

namespace Baton.Cli.Tests.Daemon;

[Collection(SerializedEnvironmentCollection.Name)]
public sealed class DraftPullRequestHandoffTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
    private const string Repository = "github.com/example/project";
    private const string Branch = "2486-lane";
    private const string Head = "0123456789abcdef0123456789abcdef01234567";
    private const string OtherHead = "abcdef0123456789abcdef0123456789abcdef01";
    private static readonly RepositoryIdentity Identity = RepositoryIdentity.From("https://" + Repository, null)!;

    private sealed class FakeForge : IGhCliRunner
    {
        private readonly object _sync = new();
        public List<string[]> Calls { get; } = [];
        public string? PullRequestJson { get; set; }
        public bool HistoryFails { get; set; }
        public string? HistoryOverride { get; set; }
        public bool AppearBeforeCreate { get; set; }
        public bool CreateProducesPullRequest { get; set; } = true;
        public bool CreateThrows { get; set; }
        public bool HangCreate { get; set; }
        public int CreateExitCode { get; set; }
        public string HeadSha { get; set; } = Head;
        public GhCliResult RequiredChecksResult { get; set; } = new(true, 0, "[]", string.Empty);
        public int CreateCount { get; private set; }
        public Action<int>? OnAllStateRead { get; set; }
        private int _allStateReads;

        public Task<GhCliResult> RunAsync(string workspace, IReadOnlyList<string> args, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                Calls.Add(args.ToArray());
                if (args is ["api", ..])
                {
                    using var observed = PullRequestJson is null ? null : JsonDocument.Parse(PullRequestJson);
                    var observedHead = observed?.RootElement.GetProperty("headRefOid").GetString() ?? HeadSha;
                    return Task.FromResult(RequiredCheckFixture.Read(args, Repository, observedHead, RequiredChecksResult));
                }
                Assert.Equal("--repo", args[^2]);
                Assert.Equal(Repository, args[^1]);
                if (args is ["pr", "create", ..])
                {
                    CreateCount++;
                    if (HangCreate) return new TaskCompletionSource<GhCliResult>().Task;
                    if (CreateThrows) throw new IOException("fake caller died after the durable marker");
                    if (CreateProducesPullRequest)
                        PullRequestJson = Pr(77, HeadSha);
                    return Task.FromResult(new GhCliResult(true, CreateExitCode,
                        CreateExitCode == 0 ? "https://github.com/example/project/pull/77" : string.Empty,
                        CreateExitCode == 0 ? string.Empty : "create result uncertain"));
                }

                if (args is ["pr", "checks", ..])
                    return Task.FromResult(RequiredChecksResult);
                if (args is ["pr", "view", ..])
                    return Task.FromResult(PullRequestJson is null
                        ? new GhCliResult(true, 1, string.Empty, "not found")
                        : new GhCliResult(true, 0, PullRequestJson, string.Empty));
                if (args.Contains("all", StringComparer.Ordinal))
                    OnAllStateRead?.Invoke(++_allStateReads);
                if (args.Contains("all", StringComparer.Ordinal) && HistoryFails)
                    return Task.FromResult(new GhCliResult(true, 1, string.Empty, "forge unavailable"));
                if (args.Contains("all", StringComparer.Ordinal) && AppearBeforeCreate
                    && PullRequestJson is null)
                    PullRequestJson = Pr(77, Head);
                if (args.Contains("all", StringComparer.Ordinal) && HistoryOverride is not null)
                    return Task.FromResult(new GhCliResult(true, 0, HistoryOverride, string.Empty));
                var openOnly = args.Contains("open", StringComparer.Ordinal);
                var visible = PullRequestJson is not null
                    && (!openOnly || PullRequestJson.Contains("\"state\":\"OPEN\"", StringComparison.Ordinal));
                return Task.FromResult(new GhCliResult(true, 0,
                    visible ? "[" + PullRequestJson + "]" : "[]", string.Empty));
            }
        }

        public static string Pr(int number, string head, bool draft = true, string state = "OPEN") =>
            $$$"""{"number":{{{number}}},"state":"{{{state}}}","isDraft":{{{draft.ToString().ToLowerInvariant()}}},"headRefOid":"{{{head}}}","headRefName":"2486-lane","baseRefName":"main","isCrossRepository":false,"statusCheckRollup":[]}""";
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _home = Path.Combine(Path.GetTempPath(), "baton_draft_pr_" + Guid.NewGuid().ToString("N"));
        private readonly IDisposable _scope;
        public FakeForge Forge { get; } = new();
        public string? RemoteHead { get; set; } = Head;
        public string Status { get; set; } = string.Empty;
        public string? LocalHead { get; set; } = Head;
        public string? LocalBranch { get; set; } = Branch;
        public string? MainHead { get; set; } = OtherHead;
        public QueueItem Item { get; private set; } = null!;
        public string Home => _home;

        public Fixture()
        {
            Directory.CreateDirectory(_home);
            _scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = _home });
        }

        public async Task SeedAsync(
            bool enabled = true, bool settled = true, string? settingsRepository = Repository,
            bool awaitingMissingPullRequest = true, bool succeeded = true,
            WorkStage stage = WorkStage.Implement, string? attemptBaseRevision = null)
        {
            var room = Path.Combine(_home, "room");
            var workspace = Path.Combine(_home, "workspace");
            Directory.CreateDirectory(room);
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(BatonPaths.QueueSpecsDirectory);
            var spec = BatonPaths.QueueSpecFile(Branch);
            await File.WriteAllTextAsync(spec, "# Implement #2486", Ct);
            var accountPath = Path.Combine(room, "artifacts", "execution_grant-test", "changes.md");
            if (succeeded)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(accountPath)!);
                await File.WriteAllTextAsync(accountPath, "Settled implementation account.", Ct);
            }
            await TerminalSentinelWriter.WriteAsync(room,
                new WorkflowStatusView(succeeded ? WorkflowOutcome.Succeeded : WorkflowOutcome.Failed,
                    [new WorkflowStatusStepView(WorkStages.RoleFor(stage), succeeded ? "Succeeded" : "Failed", "grant-test")],
                    succeeded ? [accountPath] : [], null), Ct);
            if (settingsRepository is not null)
                await DaemonSettingsStore.SaveAsync(new DaemonSettings
                {
                    Queue = new QueueSettings
                    {
                        DraftPullRequestHandoff = new Dictionary<string, JsonElement>
                        {
                            [settingsRepository] = JsonSerializer.SerializeToElement(enabled),
                        },
                    },
                }, BatonPaths.SettingsFile, Ct);
            var attempt = new FleetAttemptId("draft-pr-attempt");
            var envelope = new QueueAttemptEnvelope(attempt, null, Branch, 2486, null,
                stage, WorkStages.RoleFor(stage), "codex", "test", "low", [], [], null,
                TaskRequirementAdmission.Admitted, room, "room-id", attemptBaseRevision ?? OtherHead, Now);
            Item = new QueueItem
            {
                Tag = Branch,
                Role = WorkStages.RoleFor(stage),
                Workspace = workspace,
                WorkspaceOrigin = WorkspaceOrigins.IssueProvisioned,
                SpecFile = spec,
                Issue = 2486,
                Branch = Branch,
                Repository = Repository,
                Stage = stage,
                State = QueueItemState.Failed,
                RoomDirectory = room,
                AttemptId = attempt,
                AttemptBaseRevision = attemptBaseRevision,
                AttemptEnvelope = envelope,
                AttemptAdmissionFactDurable = true,
                AttemptStartedFactDurable = true,
                AttemptSettledFactDurable = settled,
                Halted = awaitingMissingPullRequest,
                ReconciliationKind = awaitingMissingPullRequest
                    ? QueueReconciliationKind.AwaitingVerifiedPullRequest : null,
                Error = awaitingMissingPullRequest ? "original missing-PR delivery halt" : null,
                AutomaticFixUsed = false,
                Instructions = "Build the issue.",
            };
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with { Items = [Item] }, Ct);
        }

        public WorkItemAdvancer Advancer(TimeSpan? timeout = null) => new(
            Forge,
            (_, _) => Task.FromResult(LocalHead),
            (_, _) => Task.FromResult<RepositoryIdentity?>(Identity),
            git: (program, workspace, args, _) => Task.FromResult(Git(args)),
            draftCommandTimeout: timeout);

        private GhCliResult Git(IReadOnlyList<string> args)
        {
            Assert.Equal(new[] { "-c", "core.fsmonitor=false" }, args.Take(2));
            args = args.Skip(2).ToArray();
            if (args is ["config", "--get", "remote.origin.url"])
                return new GhCliResult(true, 0, "https://" + Repository, "");
            if (args is ["symbolic-ref", ..])
                return new GhCliResult(LocalBranch is not null, LocalBranch is null ? 1 : 0, LocalBranch ?? "", "");
            if (args is ["status", ..]) return new GhCliResult(true, 0, Status, "");
            if (args is ["rev-parse", ..])
                return new GhCliResult(LocalHead is not null, LocalHead is null ? 1 : 0, LocalHead ?? "", "");
            if (args is ["ls-remote", ..])
            {
                var lines = new List<string>();
                if (RemoteHead is not null) lines.Add($"{RemoteHead}\trefs/heads/{Branch}");
                if (MainHead is not null) lines.Add($"{MainHead}\trefs/heads/main");
                return new GhCliResult(true, 0, string.Join('\n', lines), "");
            }
            throw new InvalidOperationException("Unexpected git proof command: " + string.Join(' ', args));
        }

        public async Task<QueueItem> ReadAsync() =>
            (await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items.Single();

        public async Task WriteGrantDecisionsAsync(params GrantDecision[] decisions)
        {
            var execution = Path.Combine(Item.RoomDirectory!, "artifacts", "execution_grant-test");
            Directory.CreateDirectory(execution);
            await File.WriteAllLinesAsync(Path.Combine(execution, ".baton-grants.ndjson"),
                decisions.Select(decision => decision.ToJsonLine()), Ct);
        }

        public void Dispose()
        {
            _scope.Dispose();
            DirectoryCleanup.DeleteRecursively(_home);
        }
    }

    [Fact]
    public async Task Opted_in_settled_implementation_creates_once_then_uses_existing_review_transition()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        var fact = Assert.Single(await fixture.Advancer().AdvanceAsync(Now, Ct));
        var next = await fixture.ReadAsync();
        Assert.Equal(QueueDecisionEntry.Advanced, fact.Decision);
        Assert.Equal(WorkStage.Review, next.Stage);
        Assert.Equal(QueueItemState.Queued, next.State);
        Assert.Equal(77, next.PullRequest);
        Assert.Equal(Head, next.DraftPullRequestCreateMarker?.HeadSha);
        Assert.Equal("main", next.DraftPullRequestCreateMarker?.BaseBranch);
        Assert.Equal(1, fixture.Forge.CreateCount);
        Assert.Contains(fixture.Forge.Calls, args => args is ["pr", "create", "--draft", "--head", Branch,
            "--base", "main", ..]);
        Assert.Contains(fixture.Forge.Calls, args => args.Contains("all") && args.Contains("--base"));
        Assert.Empty(await fixture.Advancer().AdvanceAsync(Now.AddMinutes(1), Ct));
        Assert.Equal(1, fixture.Forge.CreateCount);
    }

    [Theory]
    [InlineData(8, "pending")]
    [InlineData(1, "fail")]
    public async Task Nonzero_exit_with_typed_required_checks_still_advances_draft_to_review(
        int exitCode, string bucket)
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        fixture.Forge.RequiredChecksResult = new GhCliResult(true, exitCode,
            $$$"""[{"bucket":"{{{bucket}}}","name":"build","state":"PENDING","workflow":"ci"}]""",
            "required checks not complete");

        var fact = Assert.Single(await fixture.Advancer().AdvanceAsync(Now, Ct));
        Assert.Equal(QueueDecisionEntry.Advanced, fact.Decision);
        Assert.Equal(WorkStage.Review, (await fixture.ReadAsync()).Stage);
        Assert.Equal(1, fixture.Forge.CreateCount);
    }

    [Fact]
    public async Task Workspace_path_git_shim_cannot_authorize_a_draft_for_the_wrong_origin()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        var workspace = fixture.Item.Workspace;
        var realGit = OutsideWorkspaceExecutableResolver.TryResolve(
            Environment.GetEnvironmentVariable("PATH"), workspace, "git", isWindows: true);
        Assert.NotNull(realGit);
        var bare = Path.Combine(fixture.Home, "actual-origin.git");
        await GitAsync(realGit, fixture.Home, "init", "--bare", bare);
        await GitAsync(realGit, workspace, "init", "--initial-branch", "main");
        await GitAsync(realGit, workspace, "config", "user.email", "test@example.invalid");
        await GitAsync(realGit, workspace, "config", "user.name", "Baton Test");
        await File.WriteAllTextAsync(Path.Combine(workspace, ".gitignore"), ".baton-shim/\n", Ct);
        await GitAsync(realGit, workspace, "add", ".gitignore");
        await GitAsync(realGit, workspace, "commit", "-m", "fixture");
        const string actualOrigin = "https://github.com/other/project";
        await GitAsync(realGit, workspace, "remote", "add", "origin", actualOrigin);
        await GitAsync(realGit, workspace, "config", $"url.{new Uri(bare).AbsoluteUri}.insteadOf", actualOrigin);
        await GitAsync(realGit, workspace, "branch", Branch);
        await GitAsync(realGit, workspace, "push", "origin", "main", Branch);
        await GitAsync(realGit, workspace, "checkout", Branch);
        var actualHead = (await GitAsync(realGit, workspace, "rev-parse", "HEAD")).Trim();
        fixture.Forge.HeadSha = actualHead;

        var shim = Path.Combine(workspace, ".baton-shim");
        Directory.CreateDirectory(shim);
        var hostOutput = Path.GetDirectoryName(typeof(Scenarios).Assembly.Location)!;
        foreach (var source in Directory.EnumerateFiles(hostOutput, "Baton.CrashTestHost*"))
            File.Copy(source, Path.Combine(shim, Path.GetFileName(source)));
        File.Copy(Path.Combine(hostOutput, "Baton.dll"), Path.Combine(shim, "Baton.dll"));
        File.Copy(Path.Combine(shim, "Baton.CrashTestHost.exe"), Path.Combine(shim, "git.exe"));

        var priorPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", shim + Path.PathSeparator + priorPath);
            Environment.SetEnvironmentVariable("BATON_DRAFT_PR_GIT_SHIM_ORIGIN", "https://" + Repository);
            Environment.SetEnvironmentVariable("BATON_DRAFT_PR_GIT_SHIM_HEAD", actualHead);
            var advancer = new WorkItemAdvancer(fixture.Forge, workspaceHead: null,
                repositoryIdentity: RepositoryIdentityResolver.TryResolveAsync);
            await advancer.AdvanceAsync(Now, Ct);
            Assert.Null((await fixture.ReadAsync()).DraftPullRequestCreateMarker);
            Assert.Equal(0, fixture.Forge.CreateCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BATON_DRAFT_PR_GIT_SHIM_HEAD", null);
            Environment.SetEnvironmentVariable("BATON_DRAFT_PR_GIT_SHIM_ORIGIN", null);
            Environment.SetEnvironmentVariable("PATH", priorPath);
        }
    }

    private static async Task<string> GitAsync(string git, string workingDirectory, params string[] args)
    {
        var result = await WorkspaceDeliveryProbe.SpawnAsync(git, workingDirectory, args, Ct);
        Assert.True(result.Started && result.ExitCode == 0,
            $"git {string.Join(' ', args)} failed: {result.Stderr}");
        return result.Stdout;
    }

    [Fact]
    public async Task Exact_list_read_refusal_is_incidental_and_allows_one_draft()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync(awaitingMissingPullRequest: false);
        await fixture.WriteGrantDecisionsAsync(new GrantDecision(
            "codex", "run_command", false, GrantRules.OwnPullRequestOnly,
            GrantRefusal.Stamp("Baton refuses this command: `gh pr list` enumerates pull requests this room does not own — an implement lane reads its own PR only. No `gh pr` read is allowed until this room's own `gh pr create` reports one. `gh issue view` is unaffected."),
            "digest", Now));

        var firstHalt = Assert.Single(await fixture.Advancer().AdvanceAsync(Now, Ct));
        Assert.Equal(QueueDecisionEntry.Failed, firstHalt.Decision);
        Assert.Equal(
            "the implement lane settled succeeded but no pull request is open on '2486-lane' — a pushed branch is not PR evidence; the item is still at implement, so 'baton queue add' with the same tag replaces it once you have fixed what it needs",
            (await fixture.ReadAsync()).Error);
        Assert.Equal(0, fixture.Forge.CreateCount);

        var fact = Assert.Single(await fixture.Advancer().AdvanceAsync(Now.AddMinutes(1), Ct));
        Assert.Equal(QueueDecisionEntry.Advanced, fact.Decision);
        Assert.Equal(WorkStage.Review, (await fixture.ReadAsync()).Stage);
        Assert.Equal(1, fixture.Forge.CreateCount);
        await fixture.Advancer().AdvanceAsync(Now.AddMinutes(2), Ct);
        Assert.Equal(1, fixture.Forge.CreateCount);
    }

    [Theory]
    [InlineData(WorkStage.Fix)]
    [InlineData(WorkStage.Continue)]
    public async Task Distinct_repair_without_pr_retains_typed_reconciliation_and_attempt_fields(WorkStage stage)
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync(awaitingMissingPullRequest: false, stage: stage, attemptBaseRevision: OtherHead);
        var before = await fixture.ReadAsync();

        var fact = Assert.Single(await fixture.Advancer().AdvanceAsync(Now, Ct));
        Assert.Equal(QueueDecisionEntry.Failed, fact.Decision);
        var after = await fixture.ReadAsync();
        var expected = $"the {WorkStages.Token(stage)} lane settled succeeded but no pull request is open on "
            + $"'{Branch}' — a pushed branch is not PR evidence; open the exact draft PR and Baton will reconcile "
            + "this retained terminal row";
        Assert.Equal(expected, after.Error);
        Assert.Equal(QueueReconciliationKind.AwaitingVerifiedPullRequest, after.ReconciliationKind);
        Assert.Equal(before.Tag, after.Tag);
        Assert.Equal(before.Stage, after.Stage);
        Assert.Equal(before.State, after.State);
        Assert.Equal(before.Round, after.Round);
        Assert.Equal(before.AttemptId, after.AttemptId);
        Assert.Equal(before.AttemptBaseRevision, after.AttemptBaseRevision);
        Assert.Equal(before.AttemptEnvelope?.AttemptId, after.AttemptEnvelope?.AttemptId);
        Assert.Equal(before.AttemptEnvelope?.Stage, after.AttemptEnvelope?.Stage);
        Assert.Equal(before.AttemptEnvelope?.AttemptBaseRevision, after.AttemptEnvelope?.AttemptBaseRevision);
        Assert.Equal(before.AttemptEnvelope?.RoomDirectory, after.AttemptEnvelope?.RoomDirectory);
        Assert.Equal(before.AttemptEnvelope?.AdmissionDecision, after.AttemptEnvelope?.AdmissionDecision);
        Assert.Equal(before.RoomDirectory, after.RoomDirectory);
        Assert.True(after.Halted);
        Assert.Equal(before.PullRequest, after.PullRequest);
        Assert.Equal(before.AutomaticFixUsed, after.AutomaticFixUsed);
        Assert.Equal(0, fixture.Forge.CreateCount);
    }

    [Theory]
    [InlineData("Baton cannot classify this shell wrapper's syntax without interpreting it.")]
    [InlineData("Only one standalone bare `gh pr create` command is supported; path-qualified executables and chained commands are refused.")]
    [InlineData("Baton refuses this command: `gh pr edit 12` is not this room's pull request — an implement lane reads its own PR only. This room opened example/project#11; that is the only pull request it may read. `gh issue view` is unaffected.")]
    [InlineData("Baton refuses this command: `gh pr comment 12` is not this room's pull request — an implement lane reads its own PR only. This room opened example/project#11; that is the only pull request it may read. `gh issue view` is unaffected.")]
    [InlineData("Baton refuses this command: `gh pr checkout 12` moves this room onto another pull request's branch — an implement lane reads its own PR only.")]
    [InlineData("Baton refuses this command: `gh pr view 12` is not this room's pull request — an implement lane reads its own PR only. This room opened example/project#11; that is the only pull request it may read. `gh issue view` is unaffected.")]
    [InlineData("AER: Baton refuses this command: `gh pr list` enumerates pull requests this room does not own — an implement lane reads its own PR only. No `gh pr` read is allowed until this room's own `gh pr create` reports one. `gh issue view` is unaffected.")]
    [InlineData("unknown legacy refusal wording")]
    [InlineData(null)]
    public async Task Other_own_pr_only_denials_block_creation_on_every_advance(string? reason)
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync(awaitingMissingPullRequest: false);
        await fixture.WriteGrantDecisionsAsync(new GrantDecision(
            "codex", "run_command", false, GrantRules.OwnPullRequestOnly,
            reason is null ? null : GrantRefusal.Stamp(reason), "digest", Now));

        var firstHalt = Assert.Single(await fixture.Advancer().AdvanceAsync(Now, Ct));
        Assert.Equal(QueueDecisionEntry.Failed, firstHalt.Decision);
        Assert.Empty(await fixture.Advancer().AdvanceAsync(Now.AddMinutes(1), Ct));

        var current = await fixture.ReadAsync();
        Assert.True(current.Halted);
        Assert.Equal(QueueReconciliationKind.AwaitingVerifiedPullRequest, current.ReconciliationKind);
        Assert.Contains("typed pull-request authority refusal", current.Error, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Forge.CreateCount);
    }

    [Fact]
    public async Task Malformed_reason_on_valid_own_pr_record_blocks_creation()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync(awaitingMissingPullRequest: false);
        var execution = Path.Combine(fixture.Item.RoomDirectory!, "artifacts", "execution_grant-test");
        Directory.CreateDirectory(execution);
        await File.WriteAllTextAsync(Path.Combine(execution, ".baton-grants.ndjson"),
            """{"type":"baton.grant","vendor":"codex","tool":"run_command","decision":"deny","rule":"own-pr-only","input":"digest","at":"2026-09-28T12:00:00.0000000+00:00","reason":42}""", Ct);

        Assert.Equal(QueueDecisionEntry.Failed,
            Assert.Single(await fixture.Advancer().AdvanceAsync(Now, Ct)).Decision);
        Assert.Empty(await fixture.Advancer().AdvanceAsync(Now.AddMinutes(1), Ct));
        Assert.Equal(0, fixture.Forge.CreateCount);
    }

    [Fact]
    public async Task Mixed_read_and_mutation_refusals_keep_creation_blocked()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync(awaitingMissingPullRequest: false);
        await fixture.WriteGrantDecisionsAsync(
            new GrantDecision("codex", "run_command", false, GrantRules.OwnPullRequestOnly,
                GrantRefusal.Stamp("Baton refuses this command: `gh pr list` enumerates pull requests this room does not own — an implement lane reads its own PR only. No `gh pr` read is allowed until this room's own `gh pr create` reports one. `gh issue view` is unaffected."),
                "read", Now),
            new GrantDecision("codex", "run_command", false, GrantRules.OwnPullRequestOnly,
                GrantRefusal.Stamp("Baton refuses this command: `gh pr checkout 12` moves this room onto another pull request's branch — an implement lane reads its own PR only."),
                "mutation", Now));

        Assert.Equal(QueueDecisionEntry.Failed,
            Assert.Single(await fixture.Advancer().AdvanceAsync(Now, Ct)).Decision);
        Assert.Empty(await fixture.Advancer().AdvanceAsync(Now.AddMinutes(1), Ct));
        Assert.Equal(0, fixture.Forge.CreateCount);
    }

    [Theory]
    [InlineData(false, true, Repository)]
    [InlineData(true, true, "github.com/other/project")]
    [InlineData(true, false, Repository)]
    public async Task Off_wrong_repository_or_missing_settlement_never_marks_or_creates(
        bool enabled, bool settled, string repository)
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync(enabled, settled, repository);
        await fixture.Advancer().AdvanceAsync(Now, Ct);
        Assert.Null((await fixture.ReadAsync()).DraftPullRequestCreateMarker);
        Assert.Equal(0, fixture.Forge.CreateCount);
    }

    [Fact]
    public async Task Closed_history_blocks_creation_and_remote_drift_blocks_marker()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        fixture.Forge.PullRequestJson = FakeForge.Pr(77, Head, state: "CLOSED");
        await fixture.Advancer().AdvanceAsync(Now, Ct);
        Assert.Null((await fixture.ReadAsync()).DraftPullRequestCreateMarker);
        Assert.Equal(0, fixture.Forge.CreateCount);
        fixture.Forge.PullRequestJson = null;
        fixture.RemoteHead = OtherHead;
        await fixture.Advancer().AdvanceAsync(Now.AddMinutes(1), Ct);
        Assert.Null((await fixture.ReadAsync()).DraftPullRequestCreateMarker);
        Assert.Equal(0, fixture.Forge.CreateCount);
    }

    [Fact]
    public async Task Preexisting_draft_on_wrong_head_does_not_enter_review_or_create()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        fixture.Forge.PullRequestJson = FakeForge.Pr(77, OtherHead);
        Assert.Empty(await fixture.Advancer().AdvanceAsync(Now, Ct));
        var current = await fixture.ReadAsync();
        Assert.Equal(WorkStage.Implement, current.Stage);
        Assert.True(current.Halted);
        Assert.Null(current.DraftPullRequestCreateMarker);
        Assert.Equal(0, fixture.Forge.CreateCount);
    }

    [Fact]
    public async Task Pr_appearing_after_open_only_read_prevents_create_without_a_marker()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        fixture.Forge.AppearBeforeCreate = true;
        Assert.Empty(await fixture.Advancer().AdvanceAsync(Now, Ct));
        Assert.Equal(0, fixture.Forge.CreateCount);
        Assert.Null((await fixture.ReadAsync()).DraftPullRequestCreateMarker);
        var fact = Assert.Single(await fixture.Advancer().AdvanceAsync(Now.AddMinutes(1), Ct));
        Assert.Equal(QueueDecisionEntry.Advanced, fact.Decision);
    }

    [Fact]
    public async Task Uncertain_create_survives_restart_without_a_second_create()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        fixture.Forge.CreateExitCode = 1;
        fixture.Forge.CreateProducesPullRequest = false;
        await fixture.Advancer().AdvanceAsync(Now, Ct);
        var marked = await fixture.ReadAsync();
        Assert.NotNull(marked.DraftPullRequestCreateMarker);
        Assert.True(marked.Halted);
        Assert.Equal(1, fixture.Forge.CreateCount);
        await fixture.Advancer().AdvanceAsync(Now.AddMinutes(1), Ct);
        Assert.Equal(1, fixture.Forge.CreateCount);
        Assert.Equal(marked.DraftPullRequestCreateMarker, (await fixture.ReadAsync()).DraftPullRequestCreateMarker);
        fixture.Forge.PullRequestJson = FakeForge.Pr(77, Head);
        var fact = Assert.Single(await fixture.Advancer().AdvanceAsync(Now.AddMinutes(2), Ct));
        Assert.Equal(QueueDecisionEntry.Advanced, fact.Decision);
        Assert.Equal(WorkStage.Review, (await fixture.ReadAsync()).Stage);
        Assert.Equal(1, fixture.Forge.CreateCount);
    }

    [Theory]
    [InlineData(OtherHead, true, "OPEN")]
    [InlineData(Head, false, "OPEN")]
    [InlineData(Head, true, "CLOSED")]
    public async Task Marked_conflicting_history_never_falls_through_to_unpinned_review(
        string prHead, bool draft, string state)
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        fixture.Forge.CreateProducesPullRequest = false;
        await fixture.Advancer().AdvanceAsync(Now, Ct);
        fixture.Forge.PullRequestJson = FakeForge.Pr(77, prHead, draft, state);
        Assert.Empty(await fixture.Advancer().AdvanceAsync(Now.AddMinutes(1), Ct));
        var marked = await fixture.ReadAsync();
        Assert.Equal(WorkStage.Implement, marked.Stage);
        Assert.True(marked.Halted);
        Assert.Equal(QueueReconciliationKind.AwaitingVerifiedPullRequest, marked.ReconciliationKind);
        Assert.Equal(1, fixture.Forge.CreateCount);
    }

    [Fact]
    public async Task Marked_recovery_after_opt_out_refuses_a_moved_local_head()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        fixture.Forge.CreateProducesPullRequest = false;
        await fixture.Advancer().AdvanceAsync(Now, Ct);
        Assert.NotNull((await fixture.ReadAsync()).DraftPullRequestCreateMarker);
        fixture.Forge.PullRequestJson = FakeForge.Pr(77, Head);
        fixture.LocalHead = OtherHead;
        await File.WriteAllTextAsync(BatonPaths.SettingsFile,
            """{"Queue":{"DraftPullRequestHandoff":{"github.com/example/project":false}}}""", Ct);

        Assert.Empty(await fixture.Advancer().AdvanceAsync(Now.AddMinutes(1), Ct));
        var current = await fixture.ReadAsync();
        Assert.Equal(WorkStage.Implement, current.Stage);
        Assert.True(current.Halted);
        Assert.Equal(1, fixture.Forge.CreateCount);
    }

    [Fact]
    public async Task Two_controllers_racing_for_one_settled_row_admit_one_create()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        await Task.WhenAll(
            fixture.Advancer().AdvanceAsync(Now, Ct),
            fixture.Advancer().AdvanceAsync(Now, Ct));
        Assert.Equal(1, fixture.Forge.CreateCount);
        Assert.NotNull((await fixture.ReadAsync()).DraftPullRequestCreateMarker);
    }

    [Fact]
    public async Task Failed_history_and_malformed_setting_do_not_admit_a_marker()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        fixture.Forge.HistoryFails = true;
        await fixture.Advancer().AdvanceAsync(Now, Ct);
        Assert.Null((await fixture.ReadAsync()).DraftPullRequestCreateMarker);
        Assert.Equal(0, fixture.Forge.CreateCount);
        fixture.Forge.HistoryFails = false;
        await File.WriteAllTextAsync(BatonPaths.SettingsFile,
            """{"Queue":{"MaxLiveWeight":1.5,"DraftPullRequestHandoff":{"github.com/example/project":"yes"}}}""", Ct);
        Assert.Equal(1.5, (await DaemonSettingsStore.LoadAsync(BatonPaths.SettingsFile, Ct)).Queue.MaxLiveWeight);
        await fixture.Advancer().AdvanceAsync(Now.AddMinutes(1), Ct);
        Assert.Null((await fixture.ReadAsync()).DraftPullRequestCreateMarker);
        Assert.Equal(0, fixture.Forge.CreateCount);
    }

    [Fact]
    public async Task Full_history_limit_is_unknown_not_absence()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        fixture.Forge.HistoryOverride = "[" + string.Join(',', Enumerable.Range(1, 100)
            .Select(number => FakeForge.Pr(number, Head, state: "CLOSED"))) + "]";
        await fixture.Advancer().AdvanceAsync(Now, Ct);
        var item = await fixture.ReadAsync();
        Assert.Null(item.DraftPullRequestCreateMarker);
        Assert.Contains("limit", item.Error!, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Forge.CreateCount);
    }

    [Fact]
    public async Task Exception_after_marker_is_uncertain_and_restart_cannot_retry_create()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        fixture.Forge.CreateThrows = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Advancer().AdvanceAsync(Now, Ct));
        var marker = (await fixture.ReadAsync()).DraftPullRequestCreateMarker;
        Assert.NotNull(marker);
        fixture.Forge.CreateThrows = false;
        Assert.Empty(await fixture.Advancer().AdvanceAsync(Now.AddMinutes(1), Ct));
        Assert.Equal(marker, (await fixture.ReadAsync()).DraftPullRequestCreateMarker);
        Assert.Equal(1, fixture.Forge.CreateCount);
    }

    [Fact]
    public async Task Timed_out_create_retains_marker_and_never_retries()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        fixture.Forge.HangCreate = true;
        await fixture.Advancer(TimeSpan.FromMilliseconds(50)).AdvanceAsync(Now, Ct);
        var marker = (await fixture.ReadAsync()).DraftPullRequestCreateMarker;
        Assert.NotNull(marker);
        fixture.Forge.HangCreate = false;
        Assert.Empty(await fixture.Advancer().AdvanceAsync(Now.AddMinutes(1), Ct));
        Assert.Equal(marker, (await fixture.ReadAsync()).DraftPullRequestCreateMarker);
        Assert.Equal(1, fixture.Forge.CreateCount);
    }

    [Fact]
    public async Task Legacy_workspace_origin_cannot_gain_create_authority()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
        {
            Items = snapshot.Items.Select(item => item with { WorkspaceOrigin = null }).ToList(),
        }, Ct);
        await fixture.Advancer().AdvanceAsync(Now, Ct);
        Assert.Null((await fixture.ReadAsync()).DraftPullRequestCreateMarker);
        Assert.Equal(0, fixture.Forge.CreateCount);
    }

    [Fact]
    public async Task Disabling_setting_at_admission_prevents_marker_even_after_clean_proofs()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        fixture.Forge.OnAllStateRead = count =>
        {
            if (count == 2)
                File.WriteAllText(BatonPaths.SettingsFile,
                    """{"Queue":{"DraftPullRequestHandoff":{"github.com/example/project":false}}}""");
        };
        await fixture.Advancer().AdvanceAsync(Now, Ct);
        Assert.Null((await fixture.ReadAsync()).DraftPullRequestCreateMarker);
        Assert.Equal(0, fixture.Forge.CreateCount);
    }

    [Fact]
    public async Task Replaced_attempt_between_proof_and_queue_cas_cannot_create()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        fixture.Forge.OnAllStateRead = count =>
        {
            if (count != 2) return;
            QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
            {
                Items = snapshot.Items.Select(item => item with
                {
                    AttemptId = new FleetAttemptId("new-attempt"),
                }).ToList(),
            }, CancellationToken.None).GetAwaiter().GetResult();
        };
        await fixture.Advancer().AdvanceAsync(Now, Ct);
        Assert.Null((await fixture.ReadAsync()).DraftPullRequestCreateMarker);
        Assert.Equal(0, fixture.Forge.CreateCount);
    }

    [Theory]
    [InlineData("dirty", Head, OtherHead)]
    [InlineData("", OtherHead, OtherHead)]
    [InlineData("", Head, null)]
    public async Task Dirty_local_head_or_missing_main_ref_refuses_creation(
        string status, string? localHead, string? mainHead)
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        fixture.Status = status;
        fixture.LocalHead = localHead;
        fixture.MainHead = mainHead;
        await fixture.Advancer().AdvanceAsync(Now, Ct);
        Assert.Null((await fixture.ReadAsync()).DraftPullRequestCreateMarker);
        Assert.Equal(0, fixture.Forge.CreateCount);
    }

    [Fact]
    public async Task Marked_row_rejects_cancel_replacement_import_and_retirement_without_erasing_marker()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        fixture.Forge.CreateProducesPullRequest = false;
        await fixture.Advancer().AdvanceAsync(Now, Ct);
        var marker = (await fixture.ReadAsync()).DraftPullRequestCreateMarker;
        Assert.NotNull(marker);

        await Assert.ThrowsAsync<CliArgumentException>(() => QueueCommand.ExecuteAsync(
            new QueueOptions(QueueVerb.Cancel, Tag: Branch), TextWriter.Null, Ct));
        await Assert.ThrowsAsync<CliArgumentException>(() => QueueCommand.ExecuteAsync(
            new QueueOptions(QueueVerb.Retire, Tag: Branch, Reason: "operator inspected"), TextWriter.Null, Ct));

        var import = Path.Combine(Path.GetDirectoryName((await fixture.ReadAsync()).Workspace)!, "import.json");
        await File.WriteAllTextAsync(import,
            """[{"tag":"2486-lane","role":"implement","workspace":"C:\\scratch\\w2486","launched":false}]""", Ct);
        await Assert.ThrowsAsync<CliArgumentException>(() => QueueCommand.ExecuteAsync(
            new QueueOptions(QueueVerb.Import, ImportFilePath: import), TextWriter.Null, Ct));

        var current = await fixture.ReadAsync();
        var spec = current.SpecFile;
        await Assert.ThrowsAsync<CliArgumentException>(() => QueueCommand.ExecuteAsync(
            new QueueOptions(QueueVerb.Add, Tag: Branch, Role: "implement", SpecFilePath: spec,
                WorkspaceDirectory: current.Workspace, ScopeClass: "engine"), TextWriter.Null, Ct));

        Assert.Equal(marker, (await fixture.ReadAsync()).DraftPullRequestCreateMarker);
        Assert.Equal(1, fixture.Forge.CreateCount);
    }
}
