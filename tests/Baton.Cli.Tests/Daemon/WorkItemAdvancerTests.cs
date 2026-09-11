using Baton.Cli.Daemon;
using Baton.Accounting;
using Baton.Domain;
using Baton.Queue;
using Baton.Status;
using Baton.Tests.Shared;
using System.Text.Json;
using Xunit;

namespace Baton.Cli.Tests.Daemon;

/// <summary>
/// The four transitions #1934 slice 2 encodes, driven end to end against a FIXTURE ROOM — a real
/// <c>terminal.json</c> and a real <c>verdict.json</c> on disk — with <c>gh</c> and <c>git</c> as
/// delegates, so nothing here spawns a process or reaches the network.
/// </summary>
/// <remarks>
/// Isolated by <c>BatonEnvironmentSnapshot.BeginScope</c> the way <c>QueueCommandTests</c> is, and
/// torn down through <see cref="DirectoryCleanup"/>.
/// </remarks>
public sealed class WorkItemAdvancerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    private const string PushedSha = "aaaaaaaabbbbbbbbccccccccdddddddd";

    private const string FullPushedSha = "0123456789abcdef0123456789abcdef01234567";

    private const string MergeSha = "89abcdef0123456789abcdef0123456789abcdef";

    private const string Repository = "github.com/aer-works/baton";

    private static readonly RepositoryIdentity ExpectedRepositoryIdentity =
        RepositoryIdentity.From("https://github.com/aer-works/baton.git", null)!;

    private sealed class FakeGh(
        string stdout, int exitCode = 0, string requiredBucket = "pass", int readyExitCode = 0) : IGhCliRunner
    {
        private bool _isDraft = !stdout.Contains("\"isDraft\":false", StringComparison.Ordinal);

        public List<string[]> Calls { get; } = [];

        public Task<GhCliResult> RunAsync(
            string workingDirectory, IReadOnlyList<string> args, CancellationToken cancellationToken)
        {
            Calls.Add(args.ToArray());
            Assert.Equal("--repo", args[args.Count - 2]);
            Assert.Equal(Repository, args[args.Count - 1]);
            if (exitCode != 0)
            {
                return Task.FromResult(new GhCliResult(Started: true, exitCode, stdout, string.Empty));
            }

            if (args is ["pr", "checks", ..])
            {
                return Task.FromResult(new GhCliResult(
                    Started: true, 0,
                    $$$"""[{"name":"ci","bucket":"{{{requiredBucket}}}","state":"SUCCESS"}]""",
                    string.Empty));
            }

            if (args is ["pr", "ready", ..])
            {
                if (readyExitCode != 0)
                {
                    return Task.FromResult(new GhCliResult(
                        Started: true, readyExitCode, string.Empty, "readiness mutation failed"));
                }

                _isDraft = args.Contains("--undo", StringComparer.Ordinal);
                return Task.FromResult(new GhCliResult(Started: true, 0, "ok", string.Empty));
            }

            var desiredDraft = $"\"isDraft\":{_isDraft.ToString().ToLowerInvariant()}";
            var observed = stdout
                .Replace("\"isDraft\":true", desiredDraft, StringComparison.Ordinal)
                .Replace("\"isDraft\":false", desiredDraft, StringComparison.Ordinal);
            if (args is ["pr", "view", var requested, ..])
            {
                using var document = JsonDocument.Parse(observed);
                if (document.RootElement.ValueKind == JsonValueKind.Array
                    && int.TryParse(requested, out var requestedNumber))
                {
                    var match = document.RootElement.EnumerateArray().FirstOrDefault(candidate =>
                        candidate.TryGetProperty("number", out var number)
                        && number.TryGetInt32(out var value)
                        && value == requestedNumber);
                    return Task.FromResult(match.ValueKind == JsonValueKind.Undefined
                        ? new GhCliResult(Started: true, 1, string.Empty, "not found")
                        : new GhCliResult(Started: true, 0, match.GetRawText(), string.Empty));
                }
            }

            return Task.FromResult(new GhCliResult(Started: true, 0, observed, string.Empty));
        }
    }

    private sealed class ObservationGh(Func<IReadOnlyList<string>, Task>? onCall = null) : IGhCliRunner
    {
        public List<string[]> Calls { get; } = [];
        public string State { get; set; } = "MERGED";
        public string? RawOutput { get; set; }
        public int ExitCode { get; set; }

        public async Task<GhCliResult> RunAsync(
            string workingDirectory, IReadOnlyList<string> args, CancellationToken cancellationToken)
        {
            Calls.Add(args.ToArray());
            if (onCall is not null)
            {
                await onCall(args);
            }

            var number = int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
            return new GhCliResult(true, ExitCode,
                RawOutput ?? $$$"""{"number":{{{number}}},"state":"{{{State}}}","headRefOid":"head-{{{number}}}"}""",
                string.Empty);
        }
    }

    private sealed class DelegateGh(
        Func<string, IReadOnlyList<string>, CancellationToken, Task<GhCliResult>> run) : IGhCliRunner
    {
        public Task<GhCliResult> RunAsync(
            string workingDirectory, IReadOnlyList<string> args, CancellationToken cancellationToken) =>
            run(workingDirectory, args, cancellationToken);
    }

    private static string PrJson(int number, string headSha, bool isDraft = true) =>
        $"[{PrObject(number, headSha, isDraft)}]";

    private static string PrObject(
        int number,
        string headSha,
        bool isDraft = true,
        string state = "OPEN",
        string headBranch = "1934-lane",
        string baseBranch = "main",
        bool isCrossRepository = false) =>
        $$$"""{"number":{{{number}}},"state":"{{{state}}}","isDraft":{{{isDraft.ToString().ToLowerInvariant()}}},"headRefOid":"{{{headSha}}}","headRefName":"{{{headBranch}}}","baseRefName":"{{{baseBranch}}}","isCrossRepository":{{{isCrossRepository.ToString().ToLowerInvariant()}}},"statusCheckRollup":[]}""";

    /// <summary>
    /// A blocking verdict whose blocking-ness is its <c>decision</c> and nothing else. The finding is
    /// a MEDIUM one on purpose — the deleted predicate would have advanced this PR to <c>ready</c>
    /// (spec/baton.md §13).
    /// </summary>
    private const string BlockingVerdict = """
        {"reviewedRef":"PR #77","decision":"block","summary":"one blocker","findings":[
          {"claim":"the guard is never reached","severity":"medium","status":"confirmed",
           "anchor":{"file":"src/Baton/Queue/QueueScheduler.cs","line":62},"detail":"Decide returns first"}]}
        """;

    /// <summary>The polarity partner, and crossed the other way: two CONFIRMED HIGHS the reviewer
    /// nonetheless approved.</summary>
    private const string ApprovingVerdict = """
        {"reviewedRef":"aaaaaaaabbbbbbbbccccccccdddddddd","decision":"approve","summary":"nothing blocking","findings":[
          {"claim":"a real one, already fixed on the branch","severity":"high","status":"confirmed"},
          {"claim":"another","severity":"high","status":"confirmed"}]}
        """;

    /// <summary>A readable verdict the reviewer left no decision on — the arm that has to reach a
    /// person rather than being guessed from the two confirmed highs in it.</summary>
    private const string DecisionlessVerdict = """
        {"reviewedRef":"PR #77","summary":"I could not decide","findings":[
          {"claim":"maybe a problem","severity":"high","status":"confirmed"}]}
        """;

    /// <summary>Writes a settled room: the sentinel plus, when asked, the verdict the sentinel's own
    /// <c>outputs</c> point at — the same path <c>WatchFireService</c> reads one from.</summary>
    private static async Task<string> WriteSettledRoomAsync(string home, string outcome, string? verdictJson)
    {
        var room = Path.Combine(home, "rooms", "queue-1934-lane-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(room);

        var outputs = new List<string>();
        if (verdictJson is not null)
        {
            var verdictPath = Path.Combine(room, "verdict.json");
            await File.WriteAllTextAsync(verdictPath, verdictJson, Ct);
            outputs.Add(verdictPath);
        }

        await TerminalSentinelWriter.WriteAsync(room, new WorkflowStatusView(outcome, [], outputs, null), Ct);
        return room;
    }

    private static async Task<QueueItem> SeedAsync(
        string home, WorkStage stage, string room, QueueItemState state = QueueItemState.Done, int round = 0,
        bool? automaticFixUsed = false, IReadOnlyList<QueueStageSelection>? stageSelections = null)
    {
        var workspace = Path.Combine(home, "w1934");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(BatonPaths.QueueSpecsDirectory);
        await File.WriteAllTextAsync(
            BatonPaths.QueueSpecFile("1934-lane"),
            "# Implement #1934\n\n## Do\n\nBuild the lifecycle.\n\n## Standing rules\n\nbuildlock.\n", Ct);

        var item = new QueueItem
        {
            Tag = "1934-lane",
            Role = WorkStages.RoleFor(stage),
            Workspace = workspace,
            SpecFile = BatonPaths.QueueSpecFile("1934-lane"),
            Issue = 1934,
            Branch = "1934-lane",
            Repository = Repository,
            Stage = stage,
            Round = round,
            AutomaticFixUsed = automaticFixUsed,
            State = state,
            RoomDirectory = room,
            Instructions = "Build the lifecycle.",
            StageSelections = stageSelections,
        };

        await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with { Items = [item] }, Ct);
        return item;
    }

    private static async Task<QueueItem> ReadBackAsync() =>
        (await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items.Single();

    private static WorkItemAdvancer Advancer(
        IGhCliRunner gh,
        Func<string, CancellationToken, Task<string?>> workspaceHead,
        RepositoryIdentity? repositoryIdentity = null) =>
        new(gh, workspaceHead, (_, _) => Task.FromResult<RepositoryIdentity?>(
            repositoryIdentity ?? ExpectedRepositoryIdentity));

    private static string CreateTempHome()
    {
        var home = Path.Combine(Path.GetTempPath(), "baton_advancer_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(home);
        return home;
    }

    [Fact]
    public async Task A_succeeded_implement_lane_with_an_open_pr_is_queued_for_review()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var room = await WriteSettledRoomAsync(home, WorkflowOutcome.Succeeded, verdictJson: null);
            await SeedAsync(home, WorkStage.Implement, room);
            var gh = new FakeGh(PrJson(77, PushedSha));

            var facts = await new WorkItemAdvancer(gh, (_, _) => Task.FromResult<string?>(PushedSha))
                .AdvanceAsync(Now, Ct);

            var item = await ReadBackAsync();
            Assert.Equal(WorkStage.Review, item.Stage);
            Assert.Equal("review", item.Role);
            Assert.Equal(QueueItemState.Queued, item.State);
            Assert.Equal(77, item.PullRequest);
            Assert.Null(item.RoomDirectory);
            Assert.DoesNotContain(gh.Calls, args => args is ["pr", "ready", ..]);

            var fact = Assert.Single(facts);
            Assert.Equal(QueueDecisionEntry.Advanced, fact.Decision);
            Assert.Contains("implement → review", fact.Reason!, StringComparison.Ordinal);
            Assert.Equal(room, fact.Room);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task A_legacy_lifecycle_item_without_repository_identity_retains_actionable_uncertainty()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var room = await WriteSettledRoomAsync(home, WorkflowOutcome.Succeeded, verdictJson: null);
            var seeded = await SeedAsync(home, WorkStage.Implement, room);
            await QueueStore.MutateAsync(
                BatonPaths.QueueFile,
                state => state with { Items = [seeded with { Repository = null }] },
                Ct);
            var gh = new FakeGh(PrJson(77, PushedSha));

            var fact = Assert.Single(await Advancer(gh, (_, _) => Task.FromResult<string?>(PushedSha))
                .AdvanceAsync(Now, Ct));

            var item = await ReadBackAsync();
            Assert.Equal(QueueDecisionEntry.Failed, fact.Decision);
            Assert.Equal(WorkStage.Implement, item.Stage);
            Assert.False(item.Halted);
            Assert.Contains("legacy queue entry", item.Error!, StringComparison.Ordinal);
            Assert.Contains("re-add", item.Error!, StringComparison.Ordinal);
            Assert.Empty(gh.Calls);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task A_later_stage_legacy_item_halts_with_history_preserving_recovery_instructions()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var room = await WriteSettledRoomAsync(home, WorkflowOutcome.Succeeded, BlockingVerdict);
            var seeded = await SeedAsync(home, WorkStage.Review, room, round: 3, automaticFixUsed: true);
            var verdictPath = Path.Combine(room, "verdict.json");
            await QueueStore.MutateAsync(
                BatonPaths.QueueFile,
                state => state with
                {
                    Items = [seeded with { Repository = null, LastVerdict = verdictPath }],
                },
                Ct);
            var gh = new FakeGh(PrJson(77, PushedSha));

            var fact = Assert.Single(await Advancer(gh, (_, _) => Task.FromResult<string?>(PushedSha))
                .AdvanceAsync(Now, Ct));

            var item = await ReadBackAsync();
            Assert.Equal(QueueDecisionEntry.Failed, fact.Decision);
            Assert.Equal(WorkStage.Review, item.Stage);
            Assert.Equal(3, item.Round);
            Assert.True(item.AutomaticFixUsed);
            Assert.Equal(room, item.RoomDirectory);
            Assert.Equal(verdictPath, item.LastVerdict);
            Assert.True(item.Halted);
            Assert.Contains("no 'baton queue' verb repairs this field in place", item.Error!, StringComparison.Ordinal);
            Assert.Contains("preserve Stage, State, Round, RoomDirectory, LastVerdict and AutomaticFixUsed", item.Error!,
                StringComparison.Ordinal);
            Assert.Empty(gh.Calls);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task A_same_tag_replacement_between_observation_and_commit_keeps_its_row_and_brief()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var room = await WriteSettledRoomAsync(home, WorkflowOutcome.Succeeded, verdictJson: null);
            await SeedAsync(home, WorkStage.Implement, room);
            const string replacementBrief = "replacement brief that belongs to the new row";
            var gh = new FakeGh(PrJson(77, PushedSha));
            async Task<string?> ReplaceBeforeCommit(string _, CancellationToken cancellationToken)
            {
                await QueueStore.MutateAsync(
                    BatonPaths.QueueFile,
                    snapshot =>
                    {
                        File.WriteAllText(BatonPaths.QueueSpecFile("1934-lane"), replacementBrief);
                        return snapshot with
                        {
                            Items = snapshot.Items.Select(item => item with
                            {
                                State = QueueItemState.Queued,
                                RoomDirectory = null,
                                AddedAt = Now.AddMinutes(1),
                                Instructions = "replacement instructions",
                            }).ToList(),
                        };
                    },
                    cancellationToken);
                return PushedSha;
            }

            var facts = await Advancer(gh, ReplaceBeforeCommit).AdvanceAsync(Now, Ct);

            var item = await ReadBackAsync();
            Assert.Empty(facts);
            Assert.Equal(WorkStage.Implement, item.Stage);
            Assert.Equal(QueueItemState.Queued, item.State);
            Assert.Null(item.PullRequest);
            Assert.Null(item.RoomDirectory);
            Assert.Equal("replacement instructions", item.Instructions);
            Assert.Equal(replacementBrief, await File.ReadAllTextAsync(item.SpecFile, Ct));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Repository_drift_blocks_a_same_number_branch_base_and_head_collision_before_any_PR_call()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var room = await WriteSettledRoomAsync(home, WorkflowOutcome.Succeeded, ApprovingVerdict);
            var seeded = await SeedAsync(home, WorkStage.Review, room, QueueItemState.Queued);
            await QueueStore.MutateAsync(
                BatonPaths.QueueFile,
                state => state with
                {
                    Items =
                    [
                        seeded with
                        {
                            Stage = WorkStage.Ready,
                            PullRequest = 77,
                            LastVerdict = Path.Combine(room, "verdict.json"),
                            RoomDirectory = null,
                        },
                    ],
                },
                Ct);
            // This unrelated repository deliberately has the same PR number, branch, base and head.
            // Before repository identity was persisted, every older identity check would pass and the
            // ready reconciliation below could mutate its PR.
            var unrelated = RepositoryIdentity.From("https://github.com/other-owner/other-repo.git", null)!;
            var gh = new FakeGh(PrJson(77, PushedSha, isDraft: true));

            var fact = Assert.Single(await Advancer(
                gh, (_, _) => Task.FromResult<string?>(PushedSha), unrelated).AdvanceAsync(Now, Ct));

            var item = await ReadBackAsync();
            Assert.Equal(QueueDecisionEntry.Failed, fact.Decision);
            Assert.Contains("repository context drifted", item.Error!, StringComparison.Ordinal);
            Assert.Contains(Repository, item.Error!, StringComparison.Ordinal);
            Assert.Contains("github.com/other-owner/other-repo", item.Error!, StringComparison.Ordinal);
            Assert.Empty(gh.Calls);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Every_PR_read_check_and_mutation_is_scoped_to_the_persisted_repository()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var room = await WriteSettledRoomAsync(home, WorkflowOutcome.Succeeded, ApprovingVerdict);
            await SeedAsync(home, WorkStage.Review, room);
            var gh = new FakeGh(PrJson(77, PushedSha));

            await Advancer(gh, (_, _) => Task.FromResult<string?>(PushedSha)).AdvanceAsync(Now, Ct);

            Assert.Contains(gh.Calls, args => args is ["pr", "list", .., "--repo", Repository]);
            Assert.Contains(gh.Calls, args => args is ["pr", "view", "77", .., "--repo", Repository]);
            Assert.Contains(gh.Calls, args => args is ["pr", "checks", "77", .., "--repo", Repository]);
            Assert.Contains(gh.Calls, args => args is ["pr", "ready", "77", "--repo", Repository]);
            Assert.All(gh.Calls, args => Assert.Equal(Repository, args[args.Length - 1]));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Discovery_fails_closed_when_two_identity_valid_open_PRs_share_the_branch()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var room = await WriteSettledRoomAsync(home, WorkflowOutcome.Succeeded, verdictJson: null);
            await SeedAsync(home, WorkStage.Implement, room);
            var gh = new FakeGh($"[{PrObject(77, PushedSha)},{PrObject(88, PushedSha)}]");

            await new WorkItemAdvancer(gh, (_, _) => Task.FromResult<string?>(PushedSha))
                .AdvanceAsync(Now, Ct);

            var item = await ReadBackAsync();
            Assert.Null(item.PullRequest);
            Assert.False(item.Halted);
            Assert.NotNull(item.Error);
            Assert.DoesNotContain(gh.Calls, args => args is ["pr", "ready", ..]);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Discovery_does_not_persist_a_same_branch_PR_for_another_base_repository_identity()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var room = await WriteSettledRoomAsync(home, WorkflowOutcome.Succeeded, verdictJson: null);
            await SeedAsync(home, WorkStage.Implement, room);
            var gh = new FakeGh($"[{PrObject(88, PushedSha, baseBranch: "release")}]");

            await new WorkItemAdvancer(gh, (_, _) => Task.FromResult<string?>(PushedSha))
                .AdvanceAsync(Now, Ct);

            var item = await ReadBackAsync();
            Assert.Null(item.PullRequest);
            Assert.False(item.Halted);
            Assert.NotNull(item.Error);
            Assert.DoesNotContain(gh.Calls, args => args is ["pr", "ready", ..]);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task A_persisted_closed_PR_is_read_exactly_and_an_open_same_branch_PR_is_never_mutated()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var room = await WriteSettledRoomAsync(home, WorkflowOutcome.Succeeded, ApprovingVerdict);
            var seeded = await SeedAsync(home, WorkStage.Review, room, QueueItemState.Queued);
            await QueueStore.MutateAsync(
                BatonPaths.QueueFile,
                state => state with
                {
                    Items =
                    [
                        seeded with
                        {
                            Stage = WorkStage.Ready,
                            PullRequest = 77,
                            LastVerdict = Path.Combine(room, "verdict.json"),
                            RoomDirectory = null,
                        },
                    ],
                },
                Ct);
            var gh = new FakeGh(
                $"[{PrObject(88, PushedSha, isDraft: false)},{PrObject(77, PushedSha, state: "CLOSED")}]");

            Assert.Empty(await new WorkItemAdvancer(gh, (_, _) => Task.FromResult<string?>(PushedSha))
                .AdvanceAsync(Now, Ct));

            Assert.Equal(77, (await ReadBackAsync()).PullRequest);
            Assert.Contains(gh.Calls, args => args is ["pr", "view", "77", ..]);
            Assert.DoesNotContain(gh.Calls, args => args is ["pr", "ready", ..]);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task A_blocking_verdict_queues_a_fix_round_whose_brief_carries_the_findings_and_no_room_path()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var room = await WriteSettledRoomAsync(home, WorkflowOutcome.Succeeded, BlockingVerdict);
            await SeedAsync(home, WorkStage.Review, room);

            var facts = await new WorkItemAdvancer(new FakeGh(PrJson(77, PushedSha)), (_, _) => Task.FromResult<string?>(PushedSha))
                .AdvanceAsync(Now, Ct);

            var item = await ReadBackAsync();
            Assert.Equal(WorkStage.Fix, item.Stage);
            Assert.Equal("implement", item.Role);
            Assert.Equal(1, item.Round);
            Assert.True(item.AutomaticFixUsed);
            Assert.Equal(Path.Combine(room, "verdict.json"), item.LastVerdict);

            var brief = await File.ReadAllTextAsync(item.SpecFile, Ct);
            Assert.Contains("Fix round for PR #77", brief, StringComparison.Ordinal);
            Assert.Contains("the guard is never reached", brief, StringComparison.Ordinal);
            Assert.Contains("Decide returns first", brief, StringComparison.Ordinal);

            // The findings travel as text; the room they came from must not (spec/baton.md §13). The
            // room path is recorded on the ITEM instead, asserted above — which is also the control
            // that this assertion is not passing because the room path is nowhere at all.
            Assert.DoesNotContain(room, brief, StringComparison.OrdinalIgnoreCase);

            Assert.Contains("review → fix", Assert.Single(facts).Reason!, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task A_second_block_halts_the_item_with_its_room_and_actionable_reason()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var room = await WriteSettledRoomAsync(home, WorkflowOutcome.Succeeded, BlockingVerdict);
            await SeedAsync(home, WorkStage.ReReview, room, round: 2, automaticFixUsed: true);
            await QueueStore.MutateAsync(BatonPaths.QueueFile,
                state => state with { Items = state.Items.Select(i => i with { LastVerdict = "previous-verdict.json" }).ToList() }, Ct);

            var fact = Assert.Single(await new WorkItemAdvancer(
                new FakeGh(PrJson(77, PushedSha)), (_, _) => Task.FromResult<string?>(PushedSha)).AdvanceAsync(Now, Ct));

            var item = await ReadBackAsync();
            Assert.Equal(QueueDecisionEntry.Failed, fact.Decision);
            Assert.Equal(QueueItemState.Failed, item.State);
            Assert.True(item.Halted);
            Assert.Equal(WorkStage.ReReview, item.Stage);
            Assert.Equal(room, item.RoomDirectory);
            Assert.Equal(Path.Combine(room, "verdict.json"), item.LastVerdict);
            Assert.Contains("one automatic fix was already dispatched", item.Error!, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task A_legacy_block_halts_instead_of_inventing_an_automatic_fix_budget()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var room = await WriteSettledRoomAsync(home, WorkflowOutcome.Succeeded, BlockingVerdict);
            await SeedAsync(home, WorkStage.Review, room, automaticFixUsed: null);

            await new WorkItemAdvancer(new FakeGh(PrJson(77, PushedSha)), (_, _) => Task.FromResult<string?>(PushedSha))
                .AdvanceAsync(Now, Ct);

            var item = await ReadBackAsync();
            Assert.True(item.Halted);
            Assert.Equal(room, item.RoomDirectory);
            Assert.Equal(Path.Combine(room, "verdict.json"), item.LastVerdict);
            Assert.Contains("no trustworthy automatic-fix history", item.Error!, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task An_approving_verdict_makes_the_item_ready_until_a_new_head_forces_a_draft_re_review()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var room = await WriteSettledRoomAsync(home, WorkflowOutcome.Succeeded, ApprovingVerdict);
            await SeedAsync(home, WorkStage.Review, room);
            var advancer = new WorkItemAdvancer(
                new FakeGh(PrJson(77, PushedSha)), (_, _) => Task.FromResult<string?>(PushedSha));

            var facts = await advancer.AdvanceAsync(Now, Ct);

            var item = await ReadBackAsync();
            Assert.Equal(WorkStage.Ready, item.Stage);
            Assert.Contains("approved", Assert.Single(facts).Reason!, StringComparison.Ordinal);

            // A same-head observation is idempotent: no transition fact and no new round.
            var again = await advancer.AdvanceAsync(Now.AddMinutes(1), Ct);
            Assert.Empty(again);
            Assert.Equal(WorkStage.Ready, (await ReadBackAsync()).Stage);

            // A later push invalidates the approval that made the item ready. The existing PR is
            // first re-drafted and only then is a cold re-review queued for the new exact head.
            const string newHead = "eeeeeeeeffffffff1111111122222222";
            var changedGh = new FakeGh(PrJson(77, newHead, isDraft: false));
            var changedFacts = await new WorkItemAdvancer(
                changedGh, (_, _) => Task.FromResult<string?>(newHead))
                .AdvanceAsync(Now.AddMinutes(2), Ct);

            item = await ReadBackAsync();
            Assert.NotEmpty(changedGh.Calls);
            Assert.NotEmpty(changedFacts);
            Assert.Contains("approval is stale", Assert.Single(changedFacts).Reason!, StringComparison.Ordinal);
            Assert.Equal(WorkStage.ReReview, item.Stage);
            Assert.Contains(changedGh.Calls, args => args is ["pr", "ready", "77", "--undo", "--repo", Repository]);
            Assert.Contains(newHead, await File.ReadAllTextAsync(item.SpecFile, Ct), StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task A_same_head_check_regression_re_drafts_then_restores_ready_without_another_review()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var room = await WriteSettledRoomAsync(home, WorkflowOutcome.Succeeded, ApprovingVerdict);
            await SeedAsync(home, WorkStage.Review, room);
            await new WorkItemAdvancer(
                new FakeGh(PrJson(77, PushedSha)), (_, _) => Task.FromResult<string?>(PushedSha))
                .AdvanceAsync(Now, Ct);

            var pendingGh = new FakeGh(
                PrJson(77, PushedSha, isDraft: false), requiredBucket: "pending");
            Assert.Empty(await new WorkItemAdvancer(
                pendingGh, (_, _) => Task.FromResult<string?>(PushedSha))
                .AdvanceAsync(Now.AddMinutes(1), Ct));

            var waiting = await ReadBackAsync();
            Assert.Equal(WorkStage.Ready, waiting.Stage);
            Assert.Contains("required checks are pending", waiting.Error!, StringComparison.Ordinal);
            Assert.Contains(pendingGh.Calls, args => args is ["pr", "ready", "77", "--undo", "--repo", Repository]);

            var passingGh = new FakeGh(PrJson(77, PushedSha, isDraft: true));
            Assert.Empty(await new WorkItemAdvancer(
                passingGh, (_, _) => Task.FromResult<string?>(PushedSha))
                .AdvanceAsync(Now.AddMinutes(2), Ct));

            var restored = await ReadBackAsync();
            Assert.Equal(WorkStage.Ready, restored.Stage);
            Assert.Null(restored.Error);
            Assert.Contains(passingGh.Calls, args => args is ["pr", "ready", "77", "--repo", Repository]);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Cancellation_that_changes_the_row_before_readiness_authorization_prevents_the_GitHub_mutation()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var room = await WriteSettledRoomAsync(home, WorkflowOutcome.Succeeded, ApprovingVerdict);
            var seeded = await SeedAsync(home, WorkStage.Review, room, QueueItemState.Queued);
            await QueueStore.MutateAsync(
                BatonPaths.QueueFile,
                state => state with
                {
                    Items =
                    [
                        seeded with
                        {
                            Stage = WorkStage.Ready,
                            PullRequest = 77,
                            LastVerdict = Path.Combine(room, "verdict.json"),
                            RoomDirectory = null,
                        },
                    ],
                },
                Ct);
            var gh = new FakeGh(PrJson(77, PushedSha, isDraft: true));

            async Task<string?> CancelBeforeClaim(string _, CancellationToken cancellationToken)
            {
                await QueueCommand.ExecuteAsync(
                    new QueueOptions(QueueVerb.Cancel, Tag: "1934-lane"), TextWriter.Null, cancellationToken);
                return PushedSha;
            }

            var facts = await Advancer(gh, CancelBeforeClaim).AdvanceAsync(Now, Ct);

            var item = await ReadBackAsync();
            Assert.Empty(facts);
            Assert.Equal(QueueItemState.Cancelled, item.State);
            Assert.Null(item.ReadinessMutationClaim);
            Assert.DoesNotContain(gh.Calls, args => args is ["pr", "ready", ..]);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Allowed_replacement_that_changes_the_row_before_draft_authorization_prevents_the_undo()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var room = await WriteSettledRoomAsync(home, WorkflowOutcome.Succeeded, verdictJson: null);
            var seeded = await SeedAsync(home, WorkStage.Implement, room);
            var replacementSource = Path.Combine(home, "replacement.md");
            await File.WriteAllTextAsync(replacementSource, "replacement brief", Ct);
            var gh = new FakeGh(PrJson(77, PushedSha, isDraft: false));

            async Task<string?> ReplaceBeforeClaim(string _, CancellationToken cancellationToken)
            {
                await QueueCommand.ExecuteAsync(
                    new QueueOptions(
                        QueueVerb.Add, Tag: "1934-lane", Role: "implement", SpecFilePath: replacementSource,
                        WorkspaceDirectory: seeded.Workspace),
                    TextWriter.Null,
                    cancellationToken);
                return PushedSha;
            }

            var facts = await Advancer(gh, ReplaceBeforeClaim).AdvanceAsync(Now, Ct);

            var item = await ReadBackAsync();
            Assert.Empty(facts);
            Assert.Null(item.Stage);
            Assert.Equal(QueueItemState.Queued, item.State);
            Assert.Null(item.ReadinessMutationClaim);
            Assert.Equal("replacement brief", await File.ReadAllTextAsync(item.SpecFile, Ct));
            Assert.DoesNotContain(gh.Calls, args => args is ["pr", "ready", ..]);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task A_restart_reclaims_an_orphaned_readiness_claim_and_clears_it_after_one_mutation()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var room = await WriteSettledRoomAsync(home, WorkflowOutcome.Succeeded, ApprovingVerdict);
            var seeded = await SeedAsync(home, WorkStage.Review, room, QueueItemState.Queued);
            await QueueStore.MutateAsync(
                BatonPaths.QueueFile,
                state => state with
                {
                    Items =
                    [
                        seeded with
                        {
                            Stage = WorkStage.Ready,
                            PullRequest = 77,
                            LastVerdict = Path.Combine(room, "verdict.json"),
                            RoomDirectory = null,
                            ReadinessMutationClaim = "orphaned-daemon-claim",
                        },
                    ],
                },
                Ct);
            var gh = new FakeGh(PrJson(77, PushedSha, isDraft: true));

            Assert.Empty(await Advancer(gh, (_, _) => Task.FromResult<string?>(PushedSha)).AdvanceAsync(Now, Ct));

            var item = await ReadBackAsync();
            Assert.Equal(WorkStage.Ready, item.Stage);
            Assert.Null(item.ReadinessMutationClaim);
            Assert.Single(gh.Calls, args => args is ["pr", "ready", "77", "--repo", Repository]);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task A_restart_clears_an_orphaned_claim_when_the_PR_already_reached_the_desired_state()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var room = await WriteSettledRoomAsync(home, WorkflowOutcome.Succeeded, ApprovingVerdict);
            var seeded = await SeedAsync(home, WorkStage.Review, room, QueueItemState.Queued);
            await QueueStore.MutateAsync(
                BatonPaths.QueueFile,
                state => state with
                {
                    Items =
                    [
                        seeded with
                        {
                            Stage = WorkStage.Ready,
                            PullRequest = 77,
                            LastVerdict = Path.Combine(room, "verdict.json"),
                            RoomDirectory = null,
                            ReadinessMutationClaim = "orphaned-after-github-converged",
                        },
                    ],
                },
                Ct);
            var gh = new FakeGh(PrJson(77, PushedSha, isDraft: false));

            Assert.Empty(await Advancer(gh, (_, _) => Task.FromResult<string?>(PushedSha)).AdvanceAsync(Now, Ct));

            var item = await ReadBackAsync();
            Assert.Equal(WorkStage.Ready, item.Stage);
            Assert.Null(item.ReadinessMutationClaim);
            Assert.DoesNotContain(gh.Calls, args => args is ["pr", "ready", ..]);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task A_failed_readiness_mutation_releases_the_claim_for_operator_cancellation()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var room = await WriteSettledRoomAsync(home, WorkflowOutcome.Succeeded, ApprovingVerdict);
            var seeded = await SeedAsync(home, WorkStage.Review, room, QueueItemState.Queued);
            await QueueStore.MutateAsync(
                BatonPaths.QueueFile,
                state => state with
                {
                    Items =
                    [
                        seeded with
                        {
                            Stage = WorkStage.Ready,
                            PullRequest = 77,
                            LastVerdict = Path.Combine(room, "verdict.json"),
                            RoomDirectory = null,
                        },
                    ],
                },
                Ct);
            var gh = new FakeGh(PrJson(77, PushedSha, isDraft: true), readyExitCode: 1);

            var fact = Assert.Single(
                await Advancer(gh, (_, _) => Task.FromResult<string?>(PushedSha)).AdvanceAsync(Now, Ct));

            var retained = await ReadBackAsync();
            Assert.Equal(QueueDecisionEntry.Failed, fact.Decision);
            Assert.Null(retained.ReadinessMutationClaim);
            Assert.Contains("readiness obligation remains", retained.Error!, StringComparison.Ordinal);
            Assert.Equal(0, await QueueCommand.ExecuteAsync(
                new QueueOptions(QueueVerb.Cancel, Tag: "1934-lane"), TextWriter.Null, Ct));
            Assert.Equal(QueueItemState.Cancelled, (await ReadBackAsync()).State);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task A_readable_verdict_with_no_decision_reaches_the_operator_rather_than_a_guess()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            // Same room shape as the blocking and approving arms above; the ONE thing that differs is
            // the verdict's `decision`, so this measures that field and not a broken room.
            var room = await WriteSettledRoomAsync(home, WorkflowOutcome.Succeeded, DecisionlessVerdict);
            await SeedAsync(home, WorkStage.Review, room);

            var fact = Assert.Single(await new WorkItemAdvancer(
                new FakeGh(PrJson(77, PushedSha)), (_, _) => Task.FromResult<string?>(PushedSha)).AdvanceAsync(Now, Ct));

            var item = await ReadBackAsync();
            Assert.Equal(QueueDecisionEntry.Failed, fact.Decision);
            Assert.Equal(QueueItemState.Failed, item.State);
            Assert.True(item.Halted);
            Assert.Equal(WorkStage.Review, item.Stage);
            Assert.Contains("carries no decision", item.Error!, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task A_re_review_after_a_fix_lane_carries_the_last_reviews_findings_not_an_empty_section()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            // The whole chain, because the defect only appears at the end of it (#2004 review round 1):
            // implement → review(block) → fix → re-review. A FIX room writes no verdict, so a re-review
            // rendered from the room that just settled says "(no findings were recorded)" and then asks
            // the reviewer whether the new head closes findings it was never shown.
            var reviewRoom = await WriteSettledRoomAsync(home, WorkflowOutcome.Succeeded, BlockingVerdict);
            var selections = new[]
            {
                new QueueStageSelection { Stage = WorkStage.Fix, Model = "gpt-6-astra", Reason = "fix experiment" },
                new QueueStageSelection { Stage = WorkStage.ReReview, Model = "gpt-5.6-sol", Reason = "independent review" },
            };
            await SeedAsync(home, WorkStage.Review, reviewRoom, round: 1, stageSelections: selections);

            var advancer = new WorkItemAdvancer(
                new FakeGh(PrJson(77, PushedSha)), (_, _) => Task.FromResult<string?>(PushedSha));
            await advancer.AdvanceAsync(Now, Ct);

            var afterBlock = await ReadBackAsync();
            Assert.Equal(WorkStage.Fix, afterBlock.Stage);
            Assert.Equal(Path.Combine(reviewRoom, "verdict.json"), afterBlock.LastVerdict);
            Assert.Equal("gpt-6-astra", afterBlock.StageSelections!.Single(s => s.Stage == WorkStage.Fix).Model);

            // That fix lane settles cleanly with its work pushed, and produces no verdict of its own.
            var fixRoom = await WriteSettledRoomAsync(home, WorkflowOutcome.Succeeded, verdictJson: null);
            await QueueStore.MutateAsync(
                BatonPaths.QueueFile,
                s => s with { Items = [afterBlock with { State = QueueItemState.Done, RoomDirectory = fixRoom }] },
                Ct);

            await advancer.AdvanceAsync(Now.AddMinutes(5), Ct);

            var item = await ReadBackAsync();
            Assert.Equal(WorkStage.ReReview, item.Stage);
            Assert.Equal("gpt-5.6-sol", item.StageSelections!.Single(s => s.Stage == WorkStage.ReReview).Model);

            var brief = await File.ReadAllTextAsync(item.SpecFile, Ct);
            Assert.Contains("Re-review PR #77", brief, StringComparison.Ordinal);
            Assert.Contains("the guard is never reached", brief, StringComparison.Ordinal);
            Assert.Contains("Decide returns first", brief, StringComparison.Ordinal);

            // The defect's own signature, and the reason the assertions above are not enough on their
            // own: the empty-findings text is what the brief carried before.
            Assert.DoesNotContain("no findings were recorded", brief, StringComparison.Ordinal);

            // Still text, never the path it was read back from (spec/baton.md §13).
            Assert.DoesNotContain(reviewRoom, brief, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task A_stalled_lane_with_its_work_pushed_is_re_reviewed_and_an_unpushed_one_continues()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var room = await WriteSettledRoomAsync(home, WorkflowOutcome.Failed, verdictJson: null);
            await SeedAsync(home, WorkStage.Implement, room, QueueItemState.Failed);

            // Pushed: the workspace head IS the PR's head.
            var pushedFacts = await new WorkItemAdvancer(
                new FakeGh(PrJson(77, PushedSha)), (_, _) => Task.FromResult<string?>(PushedSha)).AdvanceAsync(Now, Ct);

            Assert.Equal(WorkStage.ReReview, (await ReadBackAsync()).Stage);
            Assert.Contains("re-review", Assert.Single(pushedFacts).Reason!, StringComparison.Ordinal);

            // Unpushed: the ONE input that differs is the workspace head, which is what "the commit
            // never reached the PR" means.
            await SeedAsync(home, WorkStage.Implement, room, QueueItemState.Failed);
            var unpushedFacts = await new WorkItemAdvancer(
                new FakeGh(PrJson(77, PushedSha)), (_, _) => Task.FromResult<string?>("ffff0000ffff0000ffff0000ffff0000"))
                .AdvanceAsync(Now, Ct);

            var item = await ReadBackAsync();
            Assert.Equal(WorkStage.Continue, item.Stage);
            Assert.Contains("continue", Assert.Single(unpushedFacts).Reason!, StringComparison.Ordinal);

            var brief = await File.ReadAllTextAsync(item.SpecFile, Ct);
            Assert.Contains("Continue the work", brief, StringComparison.Ordinal);
            Assert.Contains("Build the lifecycle.", brief, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task The_issues_own_instructions_survive_a_round_that_rewrote_the_brief()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            // Round 1: the review blocks, so the spec file is REWRITTEN as a fix brief whose own
            // "## Do" section is "address each finding above". Round 2 must still hand a continuation
            // the ISSUE's instructions — which is only true because they live on the item rather than
            // being parsed back out of a file that mutates every round.
            var reviewRoom = await WriteSettledRoomAsync(home, WorkflowOutcome.Succeeded, BlockingVerdict);
            await SeedAsync(home, WorkStage.Review, reviewRoom);
            var advancer = new WorkItemAdvancer(
                new FakeGh(PrJson(77, PushedSha)), (_, _) => Task.FromResult<string?>(PushedSha));
            await advancer.AdvanceAsync(Now, Ct);

            var fixBrief = await File.ReadAllTextAsync((await ReadBackAsync()).SpecFile, Ct);
            Assert.DoesNotContain("Build the lifecycle.", fixBrief, StringComparison.Ordinal);

            // Round 2: that fix lane stalls without pushing.
            var fixRoom = await WriteSettledRoomAsync(home, WorkflowOutcome.Failed, verdictJson: null);
            await QueueStore.MutateAsync(
                BatonPaths.QueueFile,
                s => s with
                {
                    Items = [s.Items.Single() with { State = QueueItemState.Failed, RoomDirectory = fixRoom }],
                },
                Ct);

            await new WorkItemAdvancer(
                new FakeGh(PrJson(77, PushedSha)), (_, _) => Task.FromResult<string?>("ffff0000ffff0000"))
                .AdvanceAsync(Now.AddMinutes(5), Ct);

            var item = await ReadBackAsync();
            Assert.Equal(WorkStage.Continue, item.Stage);
            Assert.Contains("Build the lifecycle.", await File.ReadAllTextAsync(item.SpecFile, Ct), StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task An_item_the_queue_cannot_derive_fails_with_the_reason_on_it_and_is_never_re_read()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            // A hand-edited item: staged, settled succeeded, and with no branch to look for a PR on. The
            // fake gh is willing (it would answer with PR #77), so this arm measures the missing branch
            // and not an unavailable gh — ReadPullRequestAsync short-circuits before it spawns anything.
            var room = await WriteSettledRoomAsync(home, WorkflowOutcome.Succeeded, verdictJson: null);
            var seeded = await SeedAsync(home, WorkStage.Implement, room);
            await QueueStore.MutateAsync(
                BatonPaths.QueueFile, s => s with { Items = [seeded with { Branch = null }] }, Ct);

            var advancer = new WorkItemAdvancer(
                new FakeGh(PrJson(77, PushedSha)), (_, _) => Task.FromResult<string?>(PushedSha));

            var fact = Assert.Single(await advancer.AdvanceAsync(Now, Ct));
            Assert.Equal(QueueDecisionEntry.Failed, fact.Decision);

            var item = await ReadBackAsync();
            Assert.Equal(QueueItemState.Failed, item.State);
            Assert.True(item.Halted);
            Assert.Equal(WorkStage.Implement, item.Stage);
            Assert.Contains("no pull request is open", item.Error!, StringComparison.Ordinal);
            // The recovery the message names has to be one the code actually allows: an item still at
            // implement IS replaceable by a re-add (QueueCommand.RefuseIfNotReplaceable).
            Assert.Contains("baton queue add", item.Error!, StringComparison.Ordinal);
            // The room survives the failure, which is what the operator has to read (spec/baton.md §13).
            Assert.Equal(room, item.RoomDirectory);

            // The half that costs money: before `halted`, this same item matched the candidate filter on
            // every tick forever — a gh spawn, a git spawn and a queue.json rewrite apiece.
            Assert.Empty(await advancer.AdvanceAsync(Now.AddMinutes(1), Ct));
            Assert.Equal(item.Error, (await ReadBackAsync()).Error);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task A_stage_less_dispatch_request_is_left_exactly_as_it_was()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var room = await WriteSettledRoomAsync(home, WorkflowOutcome.Succeeded, verdictJson: null);
            var seeded = await SeedAsync(home, WorkStage.Implement, room);
            await QueueStore.MutateAsync(
                BatonPaths.QueueFile,
                s => s with { Items = [seeded with { Stage = null, Repository = null }] },
                Ct);

            var facts = await new WorkItemAdvancer(new FakeGh(PrJson(77, PushedSha)), (_, _) => Task.FromResult<string?>(PushedSha))
                .AdvanceAsync(Now, Ct);

            var item = await ReadBackAsync();
            Assert.Empty(facts);
            Assert.Null(item.Stage);
            Assert.Equal(QueueItemState.Done, item.State);
            Assert.Equal(room, item.RoomDirectory);
            Assert.Null(item.Error);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    /// <summary>
    /// #1912: the advance is the one place already spawning <c>gh</c> for this PR, so it is where the
    /// board's checks word is read — and it must be stamped with WHEN, since the advance only ever
    /// looks at a settled lane (<see cref="QueueItem.Checks"/> carries that rule).
    /// </summary>
    [Fact]
    public async Task The_advance_records_the_prs_checks_and_when_it_observed_them()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var room = await WriteSettledRoomAsync(home, WorkflowOutcome.Succeeded, verdictJson: null);
            await SeedAsync(home, WorkStage.Implement, room);

            const string prWithChecks = $$"""
                [{"number":77,"state":"OPEN","isDraft":true,"headRefOid":"{{PushedSha}}",
                  "headRefName":"1934-lane","baseRefName":"main","isCrossRepository":false,
                  "statusCheckRollup":[{"name":"gates","conclusion":"FAILURE"}]}]
                """;
            await new WorkItemAdvancer(new FakeGh(prWithChecks), (_, _) => Task.FromResult<string?>(PushedSha))
                .AdvanceAsync(Now, Ct);

            var item = await ReadBackAsync();
            Assert.Equal(PullRequestChecks.Failing, item.Checks);
            Assert.Equal(Now, item.ChecksObservedAt);
            Assert.Equal(PushedSha, item.ChecksHeadSha);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Review_attempt_observes_pr_check_and_verdict_without_claiming_the_existing_revision()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var room = await WriteSettledRoomAsync(home, WorkflowOutcome.Succeeded, ApprovingVerdict);
            var seeded = await SeedAsync(home, WorkStage.Review, room, round: 2);
            var attemptId = new FleetAttemptId("attempt-review-2");
            await QueueStore.MutateAsync(
                BatonPaths.QueueFile,
                state => state with { Items = [seeded with { AttemptId = attemptId }] }, Ct);
            var events = new List<FleetEventDraft>();
            var pr = $$$"""
                [{"number":77,"state":"OPEN","isDraft":true,"headRefOid":"{{{FullPushedSha}}}",
                  "headRefName":"1934-lane","baseRefName":"main","isCrossRepository":false,
                  "statusCheckRollup":[{"databaseId":101,"name":"gates","status":"COMPLETED",
                    "conclusion":"SUCCESS","startedAt":"2026-09-11T15:00:00Z",
                    "completedAt":"2026-09-11T15:02:00Z"}]}]
                """;
            var advancer = new WorkItemAdvancer(
                new FakeGh(pr),
                (_, _) => Task.FromResult<string?>(FullPushedSha),
                appendFleetEvent: (draft, _) =>
                {
                    events.Add(draft);
                    return Task.FromResult<FleetEvent?>(null);
                });

            await advancer.AdvanceAsync(Now, Ct);

            Assert.All(events, entry => Assert.Equal(attemptId, entry.AttemptId));
            Assert.Equal(
                [FleetEventKind.PullRequestBound, FleetEventKind.CheckObserved, FleetEventKind.ReviewVerdictObserved],
                events.Select(entry => entry.Kind));
            Assert.DoesNotContain(events, entry => entry.Kind == FleetEventKind.RevisionProduced);
            Assert.Equal(77, events[0].PullRequestId);
            Assert.Equal(new FleetCheckRunId("101"), events[1].CheckRunId);
            Assert.Equal("gates", events[1].CheckName);
            Assert.Equal("COMPLETED", events[1].CheckStatus);
            Assert.Equal("SUCCESS", events[1].CheckConclusion);
            Assert.Equal(new FleetReviewRoundId("1934-lane:2"), events[2].ReviewRoundId);
            Assert.Equal("approve", events[2].ReviewVerdict);
            // This fixture's reviewedRef is not a full immutable SHA, so it stays absent.
            Assert.Null(events[2].RevisionId);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Theory]
    [InlineData(WorkStage.Implement, FleetRevisionKind.Implementation)]
    [InlineData(WorkStage.Fix, FleetRevisionKind.Repair)]
    public async Task A_code_stage_that_changes_head_records_the_revision_it_produced(
        WorkStage stage, FleetRevisionKind revisionKind)
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var room = await WriteSettledRoomAsync(home, WorkflowOutcome.Succeeded, verdictJson: null);
            var seeded = await SeedAsync(home, stage, room);
            var attemptId = new FleetAttemptId($"attempt-{WorkStages.Token(stage)}");
            await QueueStore.MutateAsync(
                BatonPaths.QueueFile,
                state => state with
                {
                    Items = [seeded with
                    {
                        AttemptId = attemptId,
                        AttemptBaseRevision = MergeSha,
                    }],
                }, Ct);
            var events = new List<FleetEventDraft>();
            var advancer = new WorkItemAdvancer(
                new FakeGh(PrJson(77, FullPushedSha)),
                (_, _) => Task.FromResult<string?>(FullPushedSha),
                appendFleetEvent: (draft, _) =>
                {
                    events.Add(draft);
                    return Task.FromResult<FleetEvent?>(null);
                });

            await advancer.AdvanceAsync(Now, Ct);

            var revision = Assert.Single(events, entry => entry.Kind == FleetEventKind.RevisionProduced);
            Assert.Equal(attemptId, revision.AttemptId);
            Assert.Equal(new FleetRevisionId(FullPushedSha), revision.RevisionId);
            Assert.Equal(revisionKind, revision.RevisionKind);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task A_code_stage_that_does_not_change_head_records_no_revision()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var room = await WriteSettledRoomAsync(home, WorkflowOutcome.Succeeded, verdictJson: null);
            var seeded = await SeedAsync(home, WorkStage.Implement, room);
            await QueueStore.MutateAsync(
                BatonPaths.QueueFile,
                state => state with
                {
                    Items = [seeded with
                    {
                        AttemptId = new FleetAttemptId("attempt-no-op"),
                        AttemptBaseRevision = FullPushedSha,
                    }],
                }, Ct);
            var events = new List<FleetEventDraft>();

            await new WorkItemAdvancer(
                new FakeGh(PrJson(77, FullPushedSha)),
                (_, _) => Task.FromResult<string?>(FullPushedSha),
                appendFleetEvent: (draft, _) =>
                {
                    events.Add(draft);
                    return Task.FromResult<FleetEvent?>(null);
                }).AdvanceAsync(Now, Ct);

            Assert.DoesNotContain(events, entry => entry.Kind == FleetEventKind.RevisionProduced);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Overlapping_check_transitions_are_distinct_and_replays_are_idempotent()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var room = await WriteSettledRoomAsync(home, WorkflowOutcome.Succeeded, ApprovingVerdict);
            var seeded = await SeedAsync(home, WorkStage.Review, room, round: 2);
            await QueueStore.MutateAsync(
                BatonPaths.QueueFile,
                state => state with { Items = [seeded with { AttemptId = new FleetAttemptId("attempt-checks") }] }, Ct);
            var pr = $$$"""
                [{"number":77,"state":"OPEN","isDraft":true,"headRefOid":"{{{FullPushedSha}}}",
                  "headRefName":"1934-lane","baseRefName":"main","isCrossRepository":false,
                  "statusCheckRollup":[
                    {"databaseId":101,"name":"gates","status":"IN_PROGRESS","startedAt":"2026-09-11T15:00:00Z"},
                    {"detailsUrl":"https://github.com/aer-works/baton/actions/runs/88/job/202",
                     "name":"gates","status":"IN_PROGRESS","startedAt":"2026-09-11T15:01:00Z"},
                    {"databaseId":101,"name":"gates","status":"COMPLETED","conclusion":"SUCCESS",
                     "startedAt":"2026-09-11T15:00:00Z","completedAt":"2026-09-11T15:02:00Z"}]}]
                """;
            var log = new FleetEventLog(
                Path.Combine(home, "events.jsonl"), Path.Combine(home, "events.1.jsonl"), 100_000);
            var advancer = new WorkItemAdvancer(
                new FakeGh(pr),
                (_, _) => Task.FromResult<string?>(FullPushedSha),
                appendFleetEvent: log.Append);

            await advancer.AdvanceAsync(Now, Ct);
            await advancer.AdvanceAsync(Now.AddMinutes(1), Ct);

            var checks = (await log.ReadAfter(0, Ct))
                .Where(entry => entry.Kind == FleetEventKind.CheckObserved)
                .ToList();
            Assert.Equal(3, checks.Count);
            Assert.Equal(["101", "202", "101"], checks.Select(entry => entry.CheckRunId!.Value.Value));
            Assert.Equal(["IN_PROGRESS", "IN_PROGRESS", "COMPLETED"], checks.Select(entry => entry.CheckStatus));
            Assert.Null(checks[0].CheckConclusion);
            Assert.Equal("SUCCESS", checks[2].CheckConclusion);
            Assert.Equal(DateTimeOffset.Parse("2026-09-11T15:02:00Z"), checks[2].CheckCompletedAt);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Structured_merge_observation_uses_the_forge_owned_merge_revision()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var room = await WriteSettledRoomAsync(home, WorkflowOutcome.Succeeded, verdictJson: null);
            var seeded = await SeedAsync(home, WorkStage.Implement, room);
            var attemptId = new FleetAttemptId("attempt-merge");
            await QueueStore.MutateAsync(
                BatonPaths.QueueFile,
                state => state with { Items = [seeded with { AttemptId = attemptId }] }, Ct);
            var events = new List<FleetEventDraft>();
            var pr = $$$"""
                [{"number":77,"state":"MERGED","isDraft":false,"headRefOid":"{{{FullPushedSha}}}",
                  "headRefName":"1934-lane","baseRefName":"main","isCrossRepository":false,
                  "statusCheckRollup":[],"mergeCommit":{"oid":"{{{MergeSha}}}"}}]
                """;
            var advancer = new WorkItemAdvancer(
                new FakeGh(pr),
                (_, _) => Task.FromResult<string?>(FullPushedSha),
                appendFleetEvent: (draft, _) =>
                {
                    events.Add(draft);
                    return Task.FromResult<FleetEvent?>(null);
                });

            await advancer.AdvanceAsync(Now, Ct);

            var merged = Assert.Single(events, entry => entry.Kind == FleetEventKind.MergeObserved);
            Assert.Equal(attemptId, merged.AttemptId);
            Assert.Equal(77, merged.PullRequestId);
            Assert.Equal(new FleetRevisionId(MergeSha), merged.RevisionId);
            Assert.Equal("merged", merged.Outcome);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Historical_item_without_an_attempt_id_emits_no_guessed_lineage()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var room = await WriteSettledRoomAsync(home, WorkflowOutcome.Succeeded, verdictJson: null);
            await SeedAsync(home, WorkStage.Implement, room);
            var events = new List<FleetEventDraft>();
            var advancer = new WorkItemAdvancer(
                new FakeGh(PrJson(77, PushedSha)),
                (_, _) => Task.FromResult<string?>(PushedSha),
                appendFleetEvent: (draft, _) =>
                {
                    events.Add(draft);
                    return Task.FromResult<FleetEvent?>(null);
                });

            await advancer.AdvanceAsync(Now, Ct);

            Assert.Empty(events);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    /// <summary>
    /// The other half, and the one that makes the field trustworthy: a <c>gh</c> that answered nothing
    /// must not overwrite a real observation with "no checks" — see the coalescing comment in
    /// <c>WorkItemAdvancer</c>'s own next-round write.
    /// </summary>
    [Fact]
    public async Task A_gh_that_could_not_answer_leaves_the_last_observation_and_its_stamp_alone()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var room = await WriteSettledRoomAsync(home, WorkflowOutcome.Succeeded, verdictJson: null);
            var seeded = await SeedAsync(home, WorkStage.Implement, room);
            var observedAt = Now.AddHours(-2);
            await QueueStore.MutateAsync(
                BatonPaths.QueueFile,
                s => s with { Items = [seeded with { Checks = PullRequestChecks.Passing, ChecksObservedAt = observedAt }] },
                Ct);

            await new WorkItemAdvancer(new FakeGh(string.Empty, exitCode: 1), (_, _) => Task.FromResult<string?>(PushedSha))
                .AdvanceAsync(Now, Ct);

            var item = await ReadBackAsync();
            Assert.Equal(PullRequestChecks.Passing, item.Checks);
            Assert.Equal(observedAt, item.ChecksObservedAt);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Board_observations_are_persisted_once_per_qualified_PR_refresh_after_restart_and_can_reopen()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            Directory.CreateDirectory(BatonPaths.Queue);
            var one = new QueueItem
            {
                Tag = "one",
                Role = "review",
                Stage = WorkStage.Review,
                State = QueueItemState.Cancelled,
                Repository = Repository,
                PullRequest = 77,
                Workspace = home,
                SpecFile = Path.Combine(home, "one.md"),
            };
            var shared = one with { Tag = "shared", SpecFile = Path.Combine(home, "shared.md") };
            var other = one with
            {
                Tag = "other",
                Repository = "github.com/aer-works/other",
                SpecFile = Path.Combine(home, "other.md"),
            };
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with { Items = [one, shared, other] }, Ct);

            var gh = new ObservationGh();
            var firstProcess = new WorkItemAdvancer(gh, (_, _) => Task.FromResult<string?>(null));
            Assert.Equal(TimeSpan.FromMinutes(5), DeliveryPoller.DefaultInterval);
            Assert.Equal(TimeSpan.FromSeconds(90), FleetProjectionWriter.StaleAfter());
            await firstProcess.RefreshPullRequestObservationsAsync(Now, Ct);
            await firstProcess.RefreshPullRequestObservationsAsync(Now, Ct);
            Assert.Equal(2, gh.Calls.Count);

            var stored = await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct);
            Assert.Equal(2, stored.PullRequestObservations!.Count);
            Assert.All(stored.PullRequestObservations, o => Assert.Equal(PullRequestObservationStates.Merged, o.State));

            // A new advancer represents a daemon restart. The persisted attempt stamp applies the
            // existing three-projection-tick freshness window, so restart does not burst the forge.
            await new WorkItemAdvancer(gh, (_, _) => Task.FromResult<string?>(null))
                .RefreshPullRequestObservationsAsync(Now.AddMinutes(1), Ct);
            Assert.Equal(2, gh.Calls.Count);

            gh.State = "OPEN";
            var reopened = new WorkItemAdvancer(gh, (_, _) => Task.FromResult<string?>(null));
            var nextDeliveryPoll = Now + DeliveryPoller.DefaultInterval;
            await reopened.RefreshPullRequestObservationsAsync(nextDeliveryPoll, Ct);
            await reopened.RefreshPullRequestObservationsAsync(nextDeliveryPoll, Ct);
            Assert.Equal(4, gh.Calls.Count);
            stored = await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct);
            Assert.All(stored.PullRequestObservations!, o => Assert.Equal(PullRequestObservationStates.Open, o.State));

            // A failed refresh keeps the prior repository-qualified reading, but stamps both the
            // failed attempt and its error so the projection can call it stale rather than current.
            gh.RawOutput = "{ malformed";
            var malformed = new WorkItemAdvancer(gh, (_, _) => Task.FromResult<string?>(null));
            var followingDeliveryPoll = nextDeliveryPoll + DeliveryPoller.DefaultInterval;
            await malformed.RefreshPullRequestObservationsAsync(followingDeliveryPoll, Ct);
            await malformed.RefreshPullRequestObservationsAsync(followingDeliveryPoll, Ct);
            stored = await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct);
            Assert.All(stored.PullRequestObservations!, o =>
            {
                Assert.Equal(PullRequestObservationStates.Open, o.State);
                Assert.NotNull(o.HeadSha);
                Assert.NotNull(o.Error);
                Assert.Equal(nextDeliveryPoll, o.ObservedAt);
                Assert.Equal(followingDeliveryPoll, o.AttemptedAt);
            });
            Assert.All(gh.Calls, call => Assert.Equal(["pr", "view"], call.Take(2)));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task A_lookup_that_finishes_after_the_last_lane_is_removed_cannot_resurrect_its_key()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            Directory.CreateDirectory(BatonPaths.Queue);
            var item = new QueueItem
            {
                Tag = "gone",
                Role = "review",
                Stage = WorkStage.Review,
                State = QueueItemState.Cancelled,
                Repository = Repository,
                PullRequest = 77,
                Workspace = home,
                SpecFile = Path.Combine(home, "gone.md"),
            };
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with { Items = [item] }, Ct);
            var gh = new ObservationGh(async _ =>
            {
                await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with { Items = [] }, Ct);
            });

            await new WorkItemAdvancer(gh, (_, _) => Task.FromResult<string?>(null))
                .RefreshPullRequestObservationsAsync(Now, Ct);

            var stored = await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct);
            Assert.Empty(stored.Items);
            Assert.Empty(stored.PullRequestObservations ?? []);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task A_shared_PR_is_unknown_when_any_surviving_lane_workspace_has_repository_drift()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            Directory.CreateDirectory(BatonPaths.Queue);
            var valid = Path.Combine(home, "valid");
            var drifted = Path.Combine(home, "drifted");
            Directory.CreateDirectory(valid);
            Directory.CreateDirectory(drifted);
            QueueItem Lane(string tag, string workspace) => new()
            {
                Tag = tag,
                Role = "review",
                Stage = WorkStage.Review,
                State = QueueItemState.Cancelled,
                Repository = Repository,
                PullRequest = 77,
                Workspace = workspace,
                SpecFile = Path.Combine(home, tag + ".md"),
            };
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with
            {
                // The missing workspace comes first: selecting only the first lane used to skip all
                // validation and accept forge evidence despite the surviving drifted sibling.
                Items = [Lane("missing", Path.Combine(home, "missing")), Lane("valid", valid), Lane("drifted", drifted)],
                PullRequestObservations =
                [
                    // A previous terminal observation makes this a provenance test: drift must
                    // invalidate it, not merely attach an error while leaving history trusted.
                    new QueuePullRequestObservation(
                        Repository, 77, PullRequestObservationStates.Merged, "trusted-head",
                        Now.AddMinutes(-5), Now.AddMinutes(-5), null),
                ],
            }, Ct);
            var gh = new ObservationGh();
            var advancer = new WorkItemAdvancer(
                gh,
                (_, _) => Task.FromResult<string?>(null),
                (workspace, _) => Task.FromResult<RepositoryIdentity?>(workspace == valid
                    ? ExpectedRepositoryIdentity
                    : RepositoryIdentity.From("https://github.com/aer-works/other.git", null)));

            await advancer.RefreshPullRequestObservationsAsync(Now, Ct);

            Assert.Empty(gh.Calls);
            var observation = Assert.Single(
                (await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).PullRequestObservations!);
            Assert.Null(observation.State);
            Assert.Null(observation.HeadSha);
            Assert.Null(observation.ObservedAt);
            Assert.Equal(Now, observation.AttemptedAt);
            Assert.Contains(drifted, observation.Error!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task A_hung_board_forge_read_is_abandoned_at_the_observation_timeout()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            Directory.CreateDirectory(BatonPaths.Queue);
            var item = new QueueItem
            {
                Tag = "hung",
                Role = "review",
                Stage = WorkStage.Review,
                State = QueueItemState.Cancelled,
                Repository = Repository,
                PullRequest = 77,
                Workspace = home,
                SpecFile = Path.Combine(home, "hung.md"),
            };
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with { Items = [item] }, Ct);
            var never = new TaskCompletionSource<GhCliResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var gh = new DelegateGh((_, _, _) => never.Task);
            var advancer = new WorkItemAdvancer(
                gh,
                (_, _) => Task.FromResult<string?>(null),
                repositoryIdentity: null,
                boardObservationTimeout: TimeSpan.FromMilliseconds(20));

            await advancer.RefreshPullRequestObservationsAsync(Now, Ct).WaitAsync(TimeSpan.FromMinutes(1), Ct);

            var observation = Assert.Single(
                (await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).PullRequestObservations!);
            Assert.Null(observation.State);
            Assert.Contains("timed out", observation.Error!, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Observation_refresh_attempts_one_qualified_PR_per_poll_and_rotates_the_remainder()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            Directory.CreateDirectory(BatonPaths.Queue);
            var items = Enumerable.Range(1, 3)
                .Select(number => new QueueItem
                {
                    Tag = $"pr-{number}",
                    Role = "review",
                    Stage = WorkStage.Review,
                    State = QueueItemState.Cancelled,
                    Repository = Repository,
                    PullRequest = number,
                    Workspace = home,
                    SpecFile = Path.Combine(home, $"pr-{number}.md"),
                })
                .ToList();
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with { Items = items }, Ct);
            var gh = new ObservationGh();
            var advancer = new WorkItemAdvancer(gh, (_, _) => Task.FromResult<string?>(null));

            await advancer.RefreshPullRequestObservationsAsync(Now, Ct);
            Assert.Single(gh.Calls);

            await advancer.RefreshPullRequestObservationsAsync(Now, Ct);
            Assert.Equal(2, gh.Calls.Count);
            await advancer.RefreshPullRequestObservationsAsync(Now, Ct);
            Assert.Equal(3, gh.Calls.Count);
            Assert.Equal(3, (await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).PullRequestObservations!.Count);

            // All three attempts are still inside the established retry window, so a fourth poll
            // performs no read rather than starting again at the first key.
            await advancer.RefreshPullRequestObservationsAsync(Now.AddSeconds(1), Ct);
            Assert.Equal(3, gh.Calls.Count);

            // Once every key is due again, the oldest attempt rotates: refreshing #1 makes #2 the
            // next oldest instead of insertion order selecting #1 on every later poll.
            await advancer.RefreshPullRequestObservationsAsync(Now.AddMinutes(2), Ct);
            await advancer.RefreshPullRequestObservationsAsync(Now.AddMinutes(2), Ct);
            Assert.Equal("1", gh.Calls[^2][2]);
            Assert.Equal("2", gh.Calls[^1][2]);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }
}
