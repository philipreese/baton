using System.Text.Json;
using System.Text.Json.Nodes;
using Baton.Accounting;
using Baton.Cli.Daemon;
using Baton.Conductor;
using Baton.Domain;
using Baton.Queue;
using Baton.Status;
using Baton.Tests.Shared;
using Baton.Vendors;

namespace Baton.Cli.Tests.Daemon;

public sealed class StoppedWorkAdviceCaptureTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
    private const string Repository = "github.com/aer-works/baton";
    private const string Head = "aaaaaaaabbbbbbbbccccccccddddddddeeeeeeee";
    private static readonly RepositoryIdentity Identity =
        RepositoryIdentity.From("https://github.com/aer-works/baton.git", null)!;

    private sealed class FakeGh : IGhCliRunner
    {
        public List<string[]> Calls { get; } = [];
        public int? FailFromCall { get; set; }
        public Func<bool>? RefuseWhen { get; set; }
        private bool _draft = true;

        public Task<GhCliResult> RunAsync(string workspace, IReadOnlyList<string> args,
            CancellationToken cancellationToken)
        {
            Calls.Add(args.ToArray());
            if (FailFromCall is { } failFromCall && Calls.Count >= failFromCall
                || RefuseWhen?.Invoke() == true)
                return Task.FromResult(new GhCliResult(false, 1, string.Empty, "transient fake GitHub failure"));
            if (args is ["api", ..])
                return Task.FromResult(RequiredCheckFixture.Read(args, Repository, Head,
                    new GhCliResult(true, 0, """[{"name":"ci","bucket":"pass"}]""", "")));
            Assert.Equal(Repository, args[^1]);
            if (args is ["pr", "checks", ..])
            {
                return Task.FromResult(new GhCliResult(true, 0,
                    "[{\"name\":\"ci\",\"bucket\":\"pass\",\"state\":\"SUCCESS\"}]", ""));
            }

            if (args.Contains("ready", StringComparer.Ordinal))
            {
                _draft = false;
                return Task.FromResult(new GhCliResult(true, 0, string.Empty, string.Empty));
            }

            var pr = $"{{\"number\":77,\"state\":\"OPEN\",\"isDraft\":{(_draft ? "true" : "false")},"
                + "\"headRefOid\":\"" + Head + "\",\"headRefName\":\"1934-lane\","
                + "\"baseRefName\":\"main\",\"isCrossRepository\":false,"
                + "\"statusCheckRollup\":[]}";
            return Task.FromResult(new GhCliResult(true, 0,
                args is ["pr", "view", ..] ? pr : "[" + pr + "]", ""));
        }
    }

    [Fact]
    public void Opt_in_is_exact_true_for_the_canonical_repository_only()
    {
        static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
        static QueueSettings Settings(string repository, string value) => new()
        {
            StoppedWorkAdvice = new Dictionary<string, JsonElement>
            {
                [repository] = Json(value),
            },
        };

        Assert.True(Settings(Repository, "true").IsStoppedWorkAdviceEnabled(Repository));
        Assert.False(Settings(Repository, "false").IsStoppedWorkAdviceEnabled(Repository));
        Assert.False(Settings(Repository, "\"true\"").IsStoppedWorkAdviceEnabled(Repository));
        Assert.False(Settings("https://github.com/aer-works/baton.git", "true")
            .IsStoppedWorkAdviceEnabled(Repository));
    }

    [Fact]
    public void Key_round_trip_uses_the_work_stage_token_for_re_review()
    {
        var key = StoppedWorkJudgmentKey.For(Repository, "1934-lane",
            new FleetAttemptId("attempt-re-review"), WorkStage.ReReview);
        Assert.True(StoppedWorkJudgmentKey.TryParse(key, out var repository, out var tag,
            out var attempt, out var stage));
        Assert.Equal(Repository, repository);
        Assert.Equal("1934-lane", tag);
        Assert.Equal("attempt-re-review", attempt.Value);
        Assert.Equal(WorkStage.ReReview, stage);
    }

    [Fact]
    public async Task A_new_needs_operator_halt_captures_typed_intent_when_opted_in()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            await EnableStoppedWorkAdviceAsync();
            await ConductorClaimStore.ClaimAsync(Identity, "conductor-fixture",
                cancellationToken: Ct);
            var room = await WriteSettledRoomAsync(home, WorkflowOutcome.Succeeded, DecisionlessVerdict);
            var attempt = new FleetAttemptId("stopped-advice-attempt");
            await SeedAsync(home, WorkStage.Review, room, attemptId: attempt);
            var gh = new FakeGh();

            var facts = await new WorkItemAdvancer(
                gh, (_, _) => Task.FromResult<string?>(Head)).AdvanceAsync(Now, Ct);

            Assert.Equal(QueueDecisionEntry.Failed, Assert.Single(facts).Decision);
            var item = await ReadBackAsync();
            Assert.NotNull(item.StoppedWorkJudgment);
            var judgment = item.StoppedWorkJudgment!;
            Assert.True(item.Halted);
            Assert.Equal(StoppedWorkJudgmentState.Pending, judgment.State);
            Assert.Equal(StoppedWorkJudgmentKey.For(Repository, item.Tag, attempt, WorkStage.Review),
                judgment.Key);
            Assert.Equal("conductor-fixture", judgment.Holder);
            Assert.Equal(attempt, judgment.AttemptId);
            Assert.Equal(Head, judgment.PullRequestHead);
            Assert.Equal(StoppedWorkAdviceEvidence.Hash(StoppedWorkAdviceEvidence.Context(judgment)),
                judgment.ContextSha256);

            var store = Store();
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            var scheduler = Scheduler(store, new WorkItemAdvancer(
                new FakeGh(), (_, _) => Task.FromResult<string?>(Head)),
                (obligation, request, _, _, _) =>
                {
                    Interlocked.Increment(ref calls);
                    entered.TrySetResult(true);
                    return Task.FromResult(Response(obligation, request));
                });
            await scheduler.TickOnceAsync(Ct);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);
            await WaitForAsync(async () =>
                (await store.ReadAsync(judgment.Key!, Ct))?.Status
                    == ConductorObligationStatus.TransportAcknowledged);
            Assert.Equal(1, calls);
            Assert.Equal(ConductorObligationStatus.TransportAcknowledged,
                (await store.ReadAsync(judgment.Key!, Ct))?.Status);
            await scheduler.DrainStoppedWorkAdviceAsync();
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Scheduler_recovers_an_opted_in_missing_verdict_through_advice_launch_and_exact_head_proof()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            await EnableAutomaticReplacementReviewAsync();
            await ConductorClaimStore.ClaimAsync(Identity, "conductor-fixture",
                cancellationToken: Ct);
            var sourceRoom = await WriteSettledRoomAsync(home, WorkflowOutcome.Succeeded, DecisionlessVerdict);
            await SeedAsync(home, WorkStage.Review, sourceRoom,
                new FleetAttemptId("automatic-source"));
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
            {
                Items = snapshot.Items.Select(item => item with
                {
                    PullRequest = 77,
                    Round = 1,
                    AutomaticFixUsed = false,
                }).ToList(),
            }, Ct);

            var advancer = new WorkItemAdvancer(new FakeGh(), (_, _) => Task.FromResult<string?>(Head));
            await advancer.AdvanceAsync(Now, Ct);
            var halted = await ReadBackAsync();
            var intent = Assert.IsType<StoppedWorkJudgment>(halted.StoppedWorkJudgment);
            Assert.True(intent.AutomaticMissingVerdictReplacementReviewEligible);

            var store = Store();
            var adviceCalls = 0;
            var launches = 0;
            var scheduler = new QueueSchedulerService(async (request, _) =>
            {
                Interlocked.Increment(ref launches);
                Directory.CreateDirectory(request.RoomDirectory);
                var verdictPath = Path.Combine(request.RoomDirectory, "verdict.json");
                await File.WriteAllTextAsync(verdictPath, $$"""
                    {"reviewedRef":"{{Head}}","completion":"complete","decision":"approve","summary":"Recovered.","findings":[]}
                    """, Ct);
                await TerminalSentinelWriter.WriteAsync(request.RoomDirectory,
                    new WorkflowStatusView(WorkflowOutcome.Succeeded, [], [verdictPath], null), Ct);
                return new QueueLaunchOutcome(request.RoomDirectory);
            }, _ => Task.FromResult(0d), () => 16d, () => Now,
                advancer: advancer, conductorObligations: store,
                stoppedWorkAdvice: (obligation, request, _, _, _) =>
                {
                    Interlocked.Increment(ref adviceCalls);
                    return Task.FromResult(Response(obligation, request, StoppedWorkAdviceChoice.Recommend));
                });

            await scheduler.TickOnceAsync(Ct);
            await WaitForAsync(async () => (await ReadBackAsync()).ReplacementReviewAction is not null);
            Assert.Equal(1, adviceCalls);

            await scheduler.TickOnceAsync(Ct);
            Assert.Equal(1, launches);
            Assert.Equal(QueueItemState.Launched, (await ReadBackAsync()).State);

            await WaitForAsync(async () =>
            {
                await scheduler.TickOnceAsync(Ct);
                return (await ReadBackAsync()).Stage == WorkStage.Ready;
            });
            var recovered = await ReadBackAsync();
            Assert.True(recovered.Stage == WorkStage.Ready,
                JsonSerializer.Serialize(new
                {
                    recovered.Stage,
                    recovered.State,
                    recovered.Error,
                    recovered.AttemptId,
                    recovered.AttemptSettledFactDurable,
                    Action = recovered.ReplacementReviewAction
                }));
            Assert.NotNull(recovered.ReplacementReviewAction?.CompletionProof);
            await scheduler.TickOnceAsync(Ct);
            Assert.Equal(ConductorObligationStatus.ActionObserved,
                (await store.ReadAsync(intent.Key!, Ct))?.Status);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Historical_ineligible_missing_verdict_recommends_without_automatic_admission_or_repeated_advice()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            // Capture while automatic replacement is disabled: this is an existing intent whose
            // eligibility fact must not be recomputed merely because the repository opts in later.
            await EnableStoppedWorkAdviceAsync();
            await ConductorClaimStore.ClaimAsync(Identity, "conductor-fixture",
                cancellationToken: Ct);
            var sourceRoom = await WriteSettledRoomAsync(home, WorkflowOutcome.Succeeded, DecisionlessVerdict);
            await SeedAsync(home, WorkStage.Review, sourceRoom,
                new FleetAttemptId("historical-ineligible-source"));
            await MakeReplacementEligibleAsync();
            var advancer = new WorkItemAdvancer(new FakeGh(), (_, _) => Task.FromResult<string?>(Head));
            await advancer.AdvanceAsync(Now, Ct);
            var halted = await ReadBackAsync();
            var intent = Assert.IsType<StoppedWorkJudgment>(halted.StoppedWorkJudgment);
            Assert.False(intent.AutomaticMissingVerdictReplacementReviewEligible);
            Assert.Equal(77, halted.PullRequest);
            Assert.Equal(1, halted.Round);
            Assert.False(halted.AutomaticFixUsed);

            await EnableAutomaticReplacementReviewAsync();
            var store = Store();
            var adviceCalls = 0;
            var launches = 0;
            var scheduler = new QueueSchedulerService((_, _) =>
            {
                Interlocked.Increment(ref launches);
                return Task.FromResult(new QueueLaunchOutcome(null));
            }, _ => Task.FromResult(0d), () => 16d, () => Now,
                advancer: advancer, conductorObligations: store,
                stoppedWorkAdvice: (obligation, request, _, _, _) =>
                {
                    Interlocked.Increment(ref adviceCalls);
                    return Task.FromResult(Response(obligation, request, StoppedWorkAdviceChoice.Recommend));
                });
            await scheduler.TickOnceAsync(Ct);
            await WaitForAsync(async () =>
            {
                var obligation = await store.ReadAsync(intent.Key!, Ct);
                return obligation is not null
                    && (await store.ReadStoppedWorkAdviceViewAsync(obligation, Ct))?.State
                        == StoppedWorkJudgmentState.Available;
            });
            var retained = await store.ReadStoppedWorkAdviceViewAsync(
                (await store.ReadAsync(intent.Key!, Ct))!, Ct);
            Assert.Equal(StoppedWorkAdviceChoice.Recommend, retained?.Response?.Decision.Choice);
            Assert.Equal(1, adviceCalls);
            Assert.Equal(0, launches);
            Assert.Null((await ReadBackAsync()).ReplacementReviewAction);

            await Assert.ThrowsAsync<ConductorObligationStoreException>(() =>
                ReplacementReviewConductorCommand.ExecuteAsync(
                    new ConductorOptions(ConductorVerb.Act, Holder: intent.Holder,
                        ObligationKey: intent.Key, Action: "replace-review", ExpectedHead: Head),
                    TextWriter.Null, home, advancer, store, Ct, automatic: true));

            await scheduler.TickOnceAsync(Ct);
            await scheduler.DrainStoppedWorkAdviceAsync();
            Assert.Equal(1, adviceCalls);
            Assert.Equal(0, launches);
            Assert.Null((await ReadBackAsync()).ReplacementReviewAction);

            await ReplacementReviewConductorCommand.ExecuteAsync(
                new ConductorOptions(ConductorVerb.Act, Holder: intent.Holder,
                    ObligationKey: intent.Key, Action: "replace-review", ExpectedHead: Head),
                TextWriter.Null, home, advancer, store, Ct);
            Assert.Equal(QueueReplacementReviewOrigin.Manual,
                (await ReadBackAsync()).ReplacementReviewAction?.Origin);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Revoked_automatic_admission_keeps_advice_available_for_manual_action()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            await EnableAutomaticReplacementReviewAsync();
            await ConductorClaimStore.ClaimAsync(Identity, "conductor-fixture", cancellationToken: Ct);
            var room = await WriteSettledRoomAsync(home, WorkflowOutcome.Succeeded, DecisionlessVerdict);
            await SeedAsync(home, WorkStage.Review, room, new FleetAttemptId("revoked-source"));
            await MakeReplacementEligibleAsync();
            var advancer = new WorkItemAdvancer(new FakeGh(), (_, _) => Task.FromResult<string?>(Head));
            await advancer.AdvanceAsync(Now, Ct);
            var halted = await ReadBackAsync();
            var intent = Assert.IsType<StoppedWorkJudgment>(halted.StoppedWorkJudgment);
            var store = Store();
            var scheduler = Scheduler(store, advancer, async (obligation, request, _, _, _) =>
            {
                await EnableStoppedWorkAdviceAsync();
                return Response(obligation, request, StoppedWorkAdviceChoice.Recommend);
            });

            await scheduler.TickOnceAsync(Ct);
            await WaitForAsync(async () =>
                (await store.ReadStoppedWorkAdviceViewAsync((await store.ReadAsync(intent.Key!, Ct))!, Ct))?.State
                    == StoppedWorkJudgmentState.Available);
            await scheduler.DrainStoppedWorkAdviceAsync();
            Assert.Null((await ReadBackAsync()).ReplacementReviewAction);
            Assert.Equal(StoppedWorkJudgmentState.Available,
                (await store.ReadStoppedWorkAdviceViewAsync((await store.ReadAsync(intent.Key!, Ct))!, Ct))?.State);

            await ReplacementReviewConductorCommand.ExecuteAsync(
                new ConductorOptions(ConductorVerb.Act, Holder: intent.Holder,
                    ObligationKey: intent.Key, Action: "replace-review", ExpectedHead: Head),
                TextWriter.Null, home, advancer, store, Ct);
            Assert.Equal(QueueReplacementReviewOrigin.Manual,
                (await ReadBackAsync()).ReplacementReviewAction?.Origin);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Automatic_recommendation_checked_while_held_remains_available_when_resume_source_check_fails()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            await EnableAutomaticReplacementReviewAsync();
            await ConductorClaimStore.ClaimAsync(Identity, "conductor-fixture", cancellationToken: Ct);
            var room = await WriteSettledRoomAsync(home, WorkflowOutcome.Succeeded, DecisionlessVerdict);
            await SeedAsync(home, WorkStage.Review, room, new FleetAttemptId("held-resume-source"));
            await MakeReplacementEligibleAsync();
            var gh = new FakeGh();
            var advancer = new WorkItemAdvancer(gh, (_, _) => Task.FromResult<string?>(Head));
            await advancer.AdvanceAsync(Now, Ct);
            var intent = Assert.IsType<StoppedWorkJudgment>((await ReadBackAsync()).StoppedWorkJudgment);
            var store = Store();
            var adviceCalls = 0;
            var scheduler = Scheduler(store, advancer, (obligation, request, _, _, _) =>
            {
                Interlocked.Increment(ref adviceCalls);
                return Task.FromResult(Response(obligation, request, StoppedWorkAdviceChoice.Recommend));
            });

            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with { Held = true }, Ct);
            await scheduler.TickOnceAsync(Ct);
            await WaitForAsync(async () =>
                (await store.ReadStoppedWorkAdviceViewAsync((await store.ReadAsync(intent.Key!, Ct))!, Ct))?.State
                    == StoppedWorkJudgmentState.Available);
            await scheduler.DrainStoppedWorkAdviceAsync();
            Assert.Equal(1, adviceCalls);
            Assert.Null((await ReadBackAsync()).ReplacementReviewAction);

            // Replay must not stale already checked advice on a new forge read. After the fix,
            // this same first failing operation belongs to automatic action admission instead.
            gh.FailFromCall = gh.Calls.Count + 1;
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with { Held = false }, Ct);
            await scheduler.TickOnceAsync(Ct);
            var evidenceDirectory = store.GetStoppedWorkAdviceEvidenceDirectory(intent.Key!);
            await WaitForAsync(() => Task.FromResult(
                File.Exists(Path.Combine(evidenceDirectory, "source-stale"))
                || File.Exists(Path.Combine(evidenceDirectory, "automatic-admission-refused"))));
            await scheduler.DrainStoppedWorkAdviceAsync();

            Assert.Equal(StoppedWorkJudgmentState.Available,
                (await store.ReadStoppedWorkAdviceViewAsync((await store.ReadAsync(intent.Key!, Ct))!, Ct))?.State);
            Assert.Null((await ReadBackAsync()).ReplacementReviewAction);
            Assert.True(File.Exists(Path.Combine(
                store.GetStoppedWorkAdviceEvidenceDirectory(intent.Key!), "automatic-admission-refused")));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Automatic_admission_refusal_is_durable_and_projection_preserves_manual_authority()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            await EnableAutomaticReplacementReviewAsync();
            await ConductorClaimStore.ClaimAsync(Identity, "conductor-fixture", cancellationToken: Ct);
            var room = await WriteSettledRoomAsync(home, WorkflowOutcome.Succeeded, DecisionlessVerdict);
            await SeedAsync(home, WorkStage.Review, room, new FleetAttemptId("refused-projection-source"));
            await MakeReplacementEligibleAsync();
            var gh = new FakeGh();
            var advancer = new WorkItemAdvancer(gh, (_, _) => Task.FromResult<string?>(Head));
            await advancer.AdvanceAsync(Now, Ct);
            var intent = Assert.IsType<StoppedWorkJudgment>((await ReadBackAsync()).StoppedWorkJudgment);
            var store = Store();
            var adviceCalls = 0;
            var scheduler = Scheduler(store, advancer, (obligation, request, _, _, _) =>
            {
                Interlocked.Increment(ref adviceCalls);
                // The marker is written only after advice's source check completes. A failure
                // triggered by it reaches the real action-admission branch, not that source check.
                gh.RefuseWhen = () => File.Exists(Path.Combine(
                    store.GetStoppedWorkAdviceEvidenceDirectory(intent.Key!), "source-checked"));
                return Task.FromResult(Response(obligation, request, StoppedWorkAdviceChoice.Recommend));
            });

            await scheduler.TickOnceAsync(Ct);
            var evidenceDirectory = store.GetStoppedWorkAdviceEvidenceDirectory(intent.Key!);
            await WaitForAsync(() => Task.FromResult(File.Exists(
                Path.Combine(evidenceDirectory, "automatic-admission-refused"))));
            await scheduler.DrainStoppedWorkAdviceAsync();
            var refused = await ReadBackAsync();
            Assert.Null(refused.ReplacementReviewAction);
            Assert.Equal(1, adviceCalls);
            Assert.Equal(StoppedWorkJudgmentState.Available,
                (await store.ReadStoppedWorkAdviceViewAsync((await store.ReadAsync(intent.Key!, Ct))!, Ct))?.State);

            var callsAfterRefusal = gh.Calls.Count;
            await scheduler.TickOnceAsync(Ct);
            await scheduler.DrainStoppedWorkAdviceAsync();
            Assert.Equal(callsAfterRefusal, gh.Calls.Count);
            Assert.Equal(1, adviceCalls);
            Assert.Null((await ReadBackAsync()).ReplacementReviewAction);

            var projection = await ConductorObligationProjection.ReadAsync(Ct);
            var rows = Assert.IsType<JsonArray>(projection["rows"]);
            var row = Assert.Single(rows.OfType<JsonObject>(), candidate =>
                candidate["requestedAction"]?.GetValue<string>() == "Assess stopped work");
            var automaticAdmission = Assert.IsType<JsonObject>(row["automaticAdmission"]);
            Assert.Equal("refused", automaticAdmission["state"]?.GetValue<string>());
            Assert.Equal("Repository conductor", automaticAdmission["owner"]?.GetValue<string>());
            var nextTrigger = automaticAdmission["nextTrigger"]?.GetValue<string>();
            Assert.NotNull(nextTrigger);
            Assert.InRange(nextTrigger!.Length, 1, 4096);
            Assert.Contains("manual", nextTrigger, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("head", nextTrigger, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Checked_recommendation_converges_after_restart_without_a_second_advice_call()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            await EnableAutomaticReplacementReviewAsync();
            await ConductorClaimStore.ClaimAsync(Identity, "conductor-fixture", cancellationToken: Ct);
            var room = await WriteSettledRoomAsync(home, WorkflowOutcome.Succeeded, DecisionlessVerdict);
            await SeedAsync(home, WorkStage.Review, room, new FleetAttemptId("restart-source"));
            await MakeReplacementEligibleAsync();
            var advancer = new WorkItemAdvancer(new FakeGh(), (_, _) => Task.FromResult<string?>(Head));
            await advancer.AdvanceAsync(Now, Ct);
            var intent = Assert.IsType<StoppedWorkJudgment>((await ReadBackAsync()).StoppedWorkJudgment);
            var store = Store();
            var adviceCalls = 0;
            var first = Scheduler(store, advancer, async (obligation, request, _, _, _) =>
            {
                Interlocked.Increment(ref adviceCalls);
                await EnableStoppedWorkAdviceAsync();
                return Response(obligation, request, StoppedWorkAdviceChoice.Recommend);
            });
            await first.TickOnceAsync(Ct);
            await WaitForAsync(async () =>
                (await store.ReadStoppedWorkAdviceViewAsync((await store.ReadAsync(intent.Key!, Ct))!, Ct))?.State
                    == StoppedWorkJudgmentState.Available);
            await first.DrainStoppedWorkAdviceAsync();
            Assert.Null((await ReadBackAsync()).ReplacementReviewAction);

            await EnableAutomaticReplacementReviewAsync();
            var restarted = Scheduler(store, advancer, (_, _, _, _, _) =>
            {
                Interlocked.Increment(ref adviceCalls);
                throw new InvalidOperationException("Retained advice must be replayed without another model call.");
            });
            await restarted.TickOnceAsync(Ct);
            await WaitForAsync(async () => (await ReadBackAsync()).ReplacementReviewAction is not null);
            Assert.Equal(1, adviceCalls);
            Assert.Equal(QueueReplacementReviewOrigin.Automatic,
                (await ReadBackAsync()).ReplacementReviewAction?.Origin);
            await restarted.DrainStoppedWorkAdviceAsync();
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Automatic_hold_retains_advice_without_admitting_an_action()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            await EnableAutomaticReplacementReviewAsync();
            await ConductorClaimStore.ClaimAsync(Identity, "conductor-fixture", cancellationToken: Ct);
            var room = await WriteSettledRoomAsync(home, WorkflowOutcome.Succeeded, DecisionlessVerdict);
            await SeedAsync(home, WorkStage.Review, room, new FleetAttemptId("hold-source"));
            await MakeReplacementEligibleAsync();
            var advancer = new WorkItemAdvancer(new FakeGh(), (_, _) => Task.FromResult<string?>(Head));
            await advancer.AdvanceAsync(Now, Ct);
            var intent = Assert.IsType<StoppedWorkJudgment>((await ReadBackAsync()).StoppedWorkJudgment);
            var store = Store();
            var calls = 0;
            var scheduler = Scheduler(store, advancer, (obligation, request, _, _, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(Response(obligation, request, StoppedWorkAdviceChoice.Hold));
            });

            await scheduler.TickOnceAsync(Ct);
            await WaitForAsync(async () =>
                (await store.ReadStoppedWorkAdviceViewAsync((await store.ReadAsync(intent.Key!, Ct))!, Ct))?.State
                    == StoppedWorkJudgmentState.Available);
            await scheduler.TickOnceAsync(Ct);
            Assert.Equal(1, calls);
            Assert.Null((await ReadBackAsync()).ReplacementReviewAction);
            await scheduler.DrainStoppedWorkAdviceAsync();
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task A_new_needs_operator_halt_creates_no_intent_when_opt_out_is_default()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var room = await WriteSettledRoomAsync(home, WorkflowOutcome.Succeeded, DecisionlessVerdict);
            await SeedAsync(home, WorkStage.Review, room,
                attemptId: new FleetAttemptId("stopped-advice-disabled"));

            var facts = await new WorkItemAdvancer(
                new FakeGh(), (_, _) => Task.FromResult<string?>(Head)).AdvanceAsync(Now, Ct);

            Assert.Equal(QueueDecisionEntry.Failed, Assert.Single(facts).Decision);
            var item = await ReadBackAsync();
            Assert.True(item.Halted);
            Assert.Null(item.StoppedWorkJudgment);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Task_owned_halt_keeps_recorded_owner_and_durable_obligation_with_advice_disabled()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            await ConductorClaimStore.ClaimAsync(Identity, "recorded-owner", home, cancellationToken: Ct);
            var room = await WriteSettledRoomAsync(home, WorkflowOutcome.Succeeded, DecisionlessVerdict);
            var attempt = new FleetAttemptId("owned-stopped-attempt");
            var seed = await SeedAsync(home, WorkStage.Review, room, attempt);
            var owned = new OwnedTaskSubmission("task-owned", Repository, 1934,
                "explicit-input-digest", "recorded-owner", Now);
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
            {
                Items = [seed with { OwnedTask = owned }],
            }, Ct);

            await new WorkItemAdvancer(new FakeGh(), (_, _) => Task.FromResult<string?>(Head))
                .AdvanceAsync(Now, Ct);
            var halted = await ReadBackAsync();
            var intent = Assert.IsType<StoppedWorkJudgment>(halted.StoppedWorkJudgment);
            Assert.Equal("recorded-owner", intent.Holder);
            Assert.Equal(StoppedWorkJudgmentKey.For(Repository, seed.Tag, attempt, WorkStage.Review), intent.Key);
            Assert.Equal(intent.Key, halted.OwnedTask?.Blocked?.ObligationKey);
            Assert.True(halted.Halted);

            var adviceCalls = 0;
            var store = Store();
            var scheduler = Scheduler(store,
                new WorkItemAdvancer(new FakeGh(), (_, _) => Task.FromResult<string?>(Head)),
                (_, _, _, _, _) =>
                {
                    Interlocked.Increment(ref adviceCalls);
                    throw new InvalidOperationException("Advice is disabled for this repository.");
                });
            await scheduler.TickOnceAsync(Ct);
            await scheduler.TickOnceAsync(Ct);
            var obligation = await store.ReadAsync(intent.Key!, Ct);
            Assert.Equal(ConductorObligationStatus.Pending, obligation?.Status);
            Assert.Equal("recorded-owner", obligation?.Owner);
            Assert.Equal(0, adviceCalls);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task A_missing_holder_and_attempt_capture_a_blocked_intent_without_guessing_identity()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            await EnableStoppedWorkAdviceAsync();
            var room = await WriteSettledRoomAsync(home, WorkflowOutcome.Succeeded, DecisionlessVerdict);
            await SeedAsync(home, WorkStage.Review, room);

            var facts = await new WorkItemAdvancer(
                new FakeGh(), (_, _) => Task.FromResult<string?>(Head)).AdvanceAsync(Now, Ct);

            Assert.Equal(QueueDecisionEntry.Failed, Assert.Single(facts).Decision);
            var blockedItem = await ReadBackAsync();
            Assert.NotNull(blockedItem.StoppedWorkJudgment);
            var judgment = blockedItem.StoppedWorkJudgment!;
            Assert.Equal(StoppedWorkJudgmentState.Blocked, judgment.State);
            Assert.Null(judgment.Key);
            Assert.Null(judgment.Holder);
            Assert.Null(judgment.AttemptId);
            Assert.Contains("conductor holder", judgment.Reason!, StringComparison.Ordinal);
            Assert.Contains("attempt identity", judgment.Reason!, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Enabling_advice_does_not_backfill_an_old_halted_needs_operator_row()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var room = await WriteSettledRoomAsync(home, WorkflowOutcome.Succeeded, DecisionlessVerdict);
            await SeedAsync(home, WorkStage.Review, room,
                attemptId: new FleetAttemptId("old-halted-attempt"));
            var firstAdvancer = new WorkItemAdvancer(
                new FakeGh(), (_, _) => Task.FromResult<string?>(Head));
            await firstAdvancer.AdvanceAsync(Now, Ct);
            Assert.True((await ReadBackAsync()).Halted);
            Assert.Null((await ReadBackAsync()).StoppedWorkJudgment);

            await EnableAutomaticReplacementReviewAsync();
            await ConductorClaimStore.ClaimAsync(Identity, "conductor-fixture",
                cancellationToken: Ct);
            var secondGh = new FakeGh();
            Assert.Empty(await new WorkItemAdvancer(
                secondGh, (_, _) => Task.FromResult<string?>(Head)).AdvanceAsync(Now.AddMinutes(1), Ct));

            var retained = await ReadBackAsync();
            Assert.True(retained.Halted);
            Assert.Null(retained.StoppedWorkJudgment);
            Assert.Empty(secondGh.Calls);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    private const string DecisionlessVerdict = """
        {"reviewedRef":"PR #77","completion":"complete","summary":"I could not decide","findings":[
          {"claim":"maybe a problem","severity":"high","status":"confirmed"}]}
        """;

    private static async Task<string> WriteSettledRoomAsync(
        string home, string outcome, string? verdictJson)
    {
        var room = Path.Combine(home, "rooms", "queue-1934-lane-"
            + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(room);
        var outputs = new List<string>();
        if (verdictJson is not null)
        {
            var verdictPath = Path.Combine(room, "verdict.json");
            await File.WriteAllTextAsync(verdictPath, verdictJson, Ct);
            outputs.Add(verdictPath);
        }

        await TerminalSentinelWriter.WriteAsync(
            room, new WorkflowStatusView(outcome, [], outputs, null), Ct);
        return room;
    }

    private static async Task<QueueItem> SeedAsync(
        string home, WorkStage stage, string room, FleetAttemptId? attemptId = null)
    {
        var workspace = Path.Combine(home, "w1934");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(BatonPaths.QueueSpecsDirectory);
        await File.WriteAllTextAsync(
            BatonPaths.QueueSpecFile("1934-lane"), "# Implement #1934\n\nBuild the lifecycle.\n", Ct);

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
            State = QueueItemState.Done,
            RoomDirectory = room,
            AttemptId = attemptId,
            Instructions = "Build the lifecycle.",
        };

        await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with { Items = [item] }, Ct);
        return item;
    }

    private static async Task<QueueItem> ReadBackAsync() =>
        (await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items.Single();

    private static string CreateTempHome()
    {
        var home = Path.Combine(Path.GetTempPath(), "baton_stopped_advice_"
            + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(home);
        return home;
    }

    private static async Task EnableStoppedWorkAdviceAsync() =>
        await DaemonSettingsStore.SaveAsync(new DaemonSettings
        {
            Queue = new QueueSettings
            {
                StoppedWorkAdvice = new Dictionary<string, JsonElement>
                {
                    [Repository] = JsonSerializer.SerializeToElement(true),
                },
            },
        }, BatonPaths.SettingsFile, Ct);

    private static async Task EnableAutomaticReplacementReviewAsync() =>
        await DaemonSettingsStore.SaveAsync(new DaemonSettings
        {
            Queue = new QueueSettings
            {
                StoppedWorkAdvice = new Dictionary<string, JsonElement>
                {
                    [Repository] = JsonSerializer.SerializeToElement(true),
                },
                AutomaticMissingVerdictReplacementReview = new Dictionary<string, JsonElement>
                {
                    [Repository] = JsonSerializer.SerializeToElement(true),
                },
            },
        }, BatonPaths.SettingsFile, Ct);

    private static Task MakeReplacementEligibleAsync() =>
        QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
        {
            Items = snapshot.Items.Select(item => item with
            {
                PullRequest = 77,
                Round = 1,
                AutomaticFixUsed = false,
            }).ToList(),
        }, Ct);

    private static ConductorObligationStore Store() => new(
        new FleetEventLog(
            BatonPaths.FleetEventsFile,
            BatonPaths.FleetEventsRolloverFile,
            100_000),
        BatonPaths.ConductorObligationsFile,
        () => Now);

    private static QueueSchedulerService Scheduler(
        ConductorObligationStore store,
        WorkItemAdvancer advancer,
        Func<ConductorObligation, StoppedWorkAdviceRequest, StoppedWorkAdviceContext,
            string, CancellationToken, Task<RetainedStoppedWorkAdviceResponse>> launch) =>
        new(
            (_, _) => Task.FromResult(new QueueLaunchOutcome(null)),
            _ => Task.FromResult(0d),
            () => 16d,
            () => Now,
            advancer: advancer,
            conductorObligations: store,
            stoppedWorkAdvice: launch);

    private static RetainedStoppedWorkAdviceResponse Response(
        ConductorObligation obligation, StoppedWorkAdviceRequest request,
        StoppedWorkAdviceChoice choice = StoppedWorkAdviceChoice.Hold) => new(
        new StoppedWorkAdviceDecision(
            obligation.ObligationId,
            request.Repository,
            request.Tag,
            request.AttemptId.Value,
            request.ContextSha256,
            choice,
            choice == StoppedWorkAdviceChoice.Recommend
                ? "A replacement review is appropriate."
                : "The verdict is incomplete."),
        CodexReadinessDecisionAdapter.AdapterName,
        CodexReadinessDecisionAdapter.Model,
        CodexReadinessDecisionAdapter.Effort,
        Now,
        new StoppedWorkAdviceUsage(null, null, null));

    private static async Task WaitForAsync(Func<Task<bool>> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (await predicate().ConfigureAwait(false)) return;
            await Task.Delay(10, Ct).ConfigureAwait(false); // wait-ok: polling interval, not the sixty-second failure ceiling
        }

        Assert.Fail("Timed out waiting for durable stopped-work advice state.");
    }

}
