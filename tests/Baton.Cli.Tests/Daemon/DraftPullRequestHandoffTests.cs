using Baton.Accounting;
using Baton.Cli;
using Baton.Cli.Daemon;
using Baton.Domain;
using Baton.Queue;
using Baton.Status;
using Baton.Tests.Shared;
using Baton.Vendors;
using System.Text.Json;
using Xunit;

namespace Baton.Cli.Tests.Daemon;

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
        public int CreateCount { get; private set; }
        public Action<int>? OnAllStateRead { get; set; }
        private int _allStateReads;

        public Task<GhCliResult> RunAsync(string workspace, IReadOnlyList<string> args, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                Calls.Add(args.ToArray());
                Assert.Equal("--repo", args[^2]);
                Assert.Equal(Repository, args[^1]);
                if (args is ["pr", "create", ..])
                {
                    CreateCount++;
                    if (HangCreate) return new TaskCompletionSource<GhCliResult>().Task;
                    if (CreateThrows) throw new IOException("fake caller died after the durable marker");
                    if (CreateProducesPullRequest)
                        PullRequestJson = Pr(77, Head);
                    return Task.FromResult(new GhCliResult(true, CreateExitCode,
                        CreateExitCode == 0 ? "https://github.com/example/project/pull/77" : string.Empty,
                        CreateExitCode == 0 ? string.Empty : "create result uncertain"));
                }

                if (args is ["pr", "checks", ..])
                    return Task.FromResult(new GhCliResult(true, 0, "[]", string.Empty));
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

        public Fixture()
        {
            Directory.CreateDirectory(_home);
            _scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = _home });
        }

        public async Task SeedAsync(bool enabled = true, bool settled = true, string? settingsRepository = Repository)
        {
            var room = Path.Combine(_home, "room");
            var workspace = Path.Combine(_home, "workspace");
            Directory.CreateDirectory(room);
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(BatonPaths.QueueSpecsDirectory);
            var spec = BatonPaths.QueueSpecFile(Branch);
            await File.WriteAllTextAsync(spec, "# Implement #2486", Ct);
            await TerminalSentinelWriter.WriteAsync(room, new WorkflowStatusView(WorkflowOutcome.Succeeded, [], [], null), Ct);
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
                WorkStage.Implement, "implement", "codex", "test", "low", [], [], null,
                TaskRequirementAdmission.Admitted, room, "room-id", OtherHead, Now);
            Item = new QueueItem
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
                State = QueueItemState.Failed,
                RoomDirectory = room,
                AttemptId = attempt,
                AttemptEnvelope = envelope,
                AttemptAdmissionFactDurable = true,
                AttemptStartedFactDurable = true,
                AttemptSettledFactDurable = settled,
                Halted = true,
                ReconciliationKind = QueueReconciliationKind.AwaitingVerifiedPullRequest,
                Error = "original missing-PR delivery halt",
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
