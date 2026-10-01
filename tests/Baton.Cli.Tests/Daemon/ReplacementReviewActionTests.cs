using System.Text.Json;
using Baton.Accounting;
using Baton.Cli.Daemon;
using Baton.Conductor;
using Baton.Domain;
using Baton.Queue;
using Baton.Status;
using Baton.Tests.Shared;
using Baton.Vendors;

namespace Baton.Cli.Tests.Daemon;

public sealed class ReplacementReviewActionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    private const string Repository = "github.com/aer-works/baton";
    private const string Head = "0123456789abcdef0123456789abcdef01234567";
    private const string OtherHead = "ffffffffffffffffffffffffffffffffffffffff";
    private const string Holder = "conductor-one";

    [Theory]
    [InlineData("identical")]
    [InlineData("changed-head")]
    [InlineData("changed-holder")]
    [InlineData("changed-advice")]
    [InlineData("changed-source")]
    [InlineData("no-slot")]
    public async Task Stale_advice_after_another_caller_admits_replays_only_the_exact_retained_slot(
        string scenario)
    {
        var home = TempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var (source, store, advancer, _) = await SeedAsync(home);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var paused = ReplacementReviewConductorCommand.ExecuteAsync(
                Options(source), TextWriter.Null, home, advancer, store, Ct,
                async token =>
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(token);
                });
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
                if (scenario == "no-slot")
                {
                    await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
                    {
                        Items = snapshot.Items.Select(item => item with
                        {
                            Halted = false,
                            State = QueueItemState.Queued,
                        }).ToList(),
                    }, Ct);
                }
                else
                {
                    await ReplacementReviewConductorCommand.ExecuteAsync(
                        Options(source), TextWriter.Null, home, advancer, store, Ct);
                    if (scenario != "identical")
                    {
                        await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
                        {
                            Items = snapshot.Items.Select(item => item with
                            {
                                ReplacementReviewAction = scenario switch
                                {
                                    "changed-head" => item.ReplacementReviewAction! with { HeadSha = OtherHead },
                                    "changed-holder" => item.ReplacementReviewAction! with { Holder = "different-holder" },
                                    "changed-advice" => item.ReplacementReviewAction! with { AdviceDigest = "different-advice" },
                                    _ => item.ReplacementReviewAction! with
                                    { SourceAttemptId = new FleetAttemptId("different-source") },
                                },
                            }).ToList(),
                        }, Ct);
                    }
                }
            }
            finally { release.TrySetResult(); }

            if (scenario == "identical")
                await paused;
            else if (scenario == "no-slot")
                await Assert.ThrowsAsync<ConductorObligationStoreException>(() => paused);
            else
                await Assert.ThrowsAsync<ConductorObligationConflictException>(() => paused);
            var row = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(scenario == "no-slot" ? 1 : 2, row.Round);
        }
        finally { DirectoryCleanup.DeleteRecursively(home); }
    }

    [Fact]
    public async Task Concurrent_callers_consume_one_round_and_replay_conflicts_with_changed_head()
    {
        var home = TempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var (source, store, advancer, _) = await SeedAsync(home);
            var options = Options(source);
            var attempts = Enumerable.Range(0, 2).Select(_ =>
                ReplacementReviewConductorCommand.ExecuteAsync(
                    options, TextWriter.Null, home, advancer, store, Ct));
            await Task.WhenAll(attempts);
            var row = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(2, row.Round);
            Assert.Equal(WorkStage.Review, row.Stage);
            Assert.Equal(QueueItemState.Queued, row.State);
            Assert.NotNull(row.ReplacementReviewAction);
            Assert.Equal(source.AttemptId, row.ReplacementReviewAction.SourceAttemptId);
            Assert.Null(row.ReplacementReviewAction.ReplacementAttemptId);
            Assert.Contains("review", await File.ReadAllTextAsync(row.SpecFile, Ct), StringComparison.OrdinalIgnoreCase);
            var inspection = await store.InspectAsync(Ct);
            var retainedAdvice = await store.ReadStoppedWorkAdviceViewAsync(
                (await store.ReadAsync(source.StoppedWorkJudgment!.Key!, Ct))!, Ct);
            var projection = ConductorObligationProjection.Project(inspection,
                new Dictionary<string, StoppedWorkAdviceView>
                {
                    [source.StoppedWorkJudgment.Key!] = retainedAdvice!,
                }, [row]).ToJsonString();
            Assert.Contains("replace-review", projection, StringComparison.Ordinal);
            Assert.DoesNotContain(source.RoomDirectory!, projection, StringComparison.Ordinal);
            Assert.DoesNotContain(row.ReplacementReviewAction.AdviceDigest, projection, StringComparison.Ordinal);
            await ReplacementReviewConductorCommand.ExecuteAsync(
                options, TextWriter.Null, home, advancer, store, Ct);
            var conflict = Options(source) with { ExpectedHead = OtherHead };
            await Assert.ThrowsAsync<ConductorObligationConflictException>(() =>
                ReplacementReviewConductorCommand.ExecuteAsync(
                    conflict, TextWriter.Null, home, advancer, store, Ct));
            Assert.Equal(2, Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items).Round);
        }
        finally { DirectoryCleanup.DeleteRecursively(home); }
    }

    [Fact]
    public async Task Held_queue_and_late_valid_block_refuse_admission()
    {
        var home = TempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var (source, store, advancer, _) = await SeedAsync(home);
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with { Held = true }, Ct);
            await Assert.ThrowsAsync<ConductorObligationStoreException>(() =>
                ReplacementReviewConductorCommand.ExecuteAsync(
                    Options(source), TextWriter.Null, home, advancer, store, Ct));
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with { Held = false }, Ct);
            await WriteVerdictAsync(source.RoomDirectory!, "block", reviewedRef: "short");
            await Assert.ThrowsAsync<ConductorObligationStoreException>(() =>
                ReplacementReviewConductorCommand.ExecuteAsync(
                    Options(source), TextWriter.Null, home, advancer, store, Ct));
            var row = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(1, row.Round);
            Assert.Null(row.ReplacementReviewAction);
        }
        finally { DirectoryCleanup.DeleteRecursively(home); }
    }

    [Theory]
    [InlineData("malformed", "short")]
    [InlineData("incomplete", "short")]
    [InlineData("decisionless", "short")]
    [InlineData("approve", "short")]
    public async Task Unusable_existing_verdicts_admit_the_one_replacement(
        string kind, string reviewedRef)
    {
        var home = TempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var (source, store, advancer, _) = await SeedAsync(home);
            var path = Path.Combine(source.RoomDirectory!, "verdict.json");
            var contents = kind switch
            {
                "malformed" => "{not-json",
                "incomplete" => "{\"reviewedRef\":\"short\",\"completion\":\"incomplete\",\"decision\":\"approve\",\"summary\":\"unfinished\",\"findings\":[]}",
                "decisionless" => "{\"reviewedRef\":\"short\",\"completion\":\"complete\",\"summary\":\"unknown\",\"findings\":[]}",
                _ => JsonSerializer.Serialize(new
                {
                    reviewedRef,
                    completion = "complete",
                    decision = "approve",
                    summary = "Approval without exact head",
                    findings = Array.Empty<object>(),
                }),
            };
            await File.WriteAllTextAsync(path, contents, Ct);
            await TerminalSentinelWriter.WriteAsync(source.RoomDirectory!,
                new WorkflowStatusView(WorkflowOutcome.Succeeded, [], [path], null), Ct);
            await ReplacementReviewConductorCommand.ExecuteAsync(
                Options(source), TextWriter.Null, home, advancer, store, Ct);
            Assert.NotNull(Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items)
                .ReplacementReviewAction);
        }
        finally { DirectoryCleanup.DeleteRecursively(home); }
    }

    [Fact]
    public async Task Exhausted_round_and_changed_owner_or_source_refuse_without_consuming_a_round()
    {
        var home = TempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var (source, store, advancer, _) = await SeedAsync(home);
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
            {
                Items = snapshot.Items.Select(item => item with { Round = WorkStages.MaxRounds }).ToList(),
            }, Ct);
            await Assert.ThrowsAsync<ConductorObligationStoreException>(() =>
                ReplacementReviewConductorCommand.ExecuteAsync(
                    Options(source), TextWriter.Null, home, advancer, store, Ct));
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
            {
                Items = snapshot.Items.Select(item => item with
                {
                    Round = 1,
                    AutomaticFixUsed = null,
                }).ToList(),
            }, Ct);
            await Assert.ThrowsAsync<ConductorObligationStoreException>(() =>
                ReplacementReviewConductorCommand.ExecuteAsync(
                    Options(source), TextWriter.Null, home, advancer, store, Ct));
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
            {
                Items = snapshot.Items.Select(item => item with
                {
                    Round = 1,
                    AutomaticFixUsed = false,
                    AttemptId = new FleetAttemptId("different-source"),
                }).ToList(),
            }, Ct);
            await Assert.ThrowsAsync<ConductorObligationStoreException>(() =>
                ReplacementReviewConductorCommand.ExecuteAsync(
                    Options(source), TextWriter.Null, home, advancer, store, Ct));
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
            {
                Items = snapshot.Items.Select(item => item with { AttemptId = source.AttemptId }).ToList(),
            }, Ct);
            var identity = RepositoryIdentity.From("https://" + Repository, null)!;
            await ConductorClaimStore.TakeoverAsync(identity, "different-holder", "fixture drift",
                cancellationToken: Ct);
            await Assert.ThrowsAsync<ConductorObligationStoreException>(() =>
                ReplacementReviewConductorCommand.ExecuteAsync(
                    Options(source), TextWriter.Null, home, advancer, store, Ct));
            Assert.Null(Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items)
                .ReplacementReviewAction);
        }
        finally { DirectoryCleanup.DeleteRecursively(home); }
    }

    [Fact]
    public async Task Hold_after_authorization_prevents_worker_launch_without_erasing_action()
    {
        var home = TempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var (source, store, advancer, _) = await SeedAsync(home);
            await ReplacementReviewConductorCommand.ExecuteAsync(
                Options(source), TextWriter.Null, home, advancer, store, Ct);
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with { Held = true }, Ct);
            var launches = 0;
            var scheduler = new QueueSchedulerService((_, _) =>
            {
                Interlocked.Increment(ref launches);
                return Task.FromResult(new QueueLaunchOutcome(null));
            }, _ => Task.FromResult(0d), () => 16d, () => Now,
                advancer: advancer, conductorObligations: store);
            await scheduler.TickOnceAsync(Ct);
            Assert.Equal(0, launches);
            var row = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(QueueItemState.Queued, row.State);
            Assert.NotNull(row.ReplacementReviewAction);
            Assert.Null(row.ReplacementReviewAction.ReplacementAttemptId);
        }
        finally { DirectoryCleanup.DeleteRecursively(home); }
    }

    [Fact]
    public async Task Launch_claim_binds_one_target_before_the_worker_boundary()
    {
        var home = TempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var (source, store, advancer, _) = await SeedAsync(home);
            await ReplacementReviewConductorCommand.ExecuteAsync(
                Options(source), TextWriter.Null, home, advancer, store, Ct);
            var launches = 0;
            var scheduler = new QueueSchedulerService(async (request, _) =>
            {
                Interlocked.Increment(ref launches);
                var row = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
                Assert.Equal(row.AttemptId, row.ReplacementReviewAction!.ReplacementAttemptId);
                Assert.Equal(row.RoomDirectory, row.ReplacementReviewAction.ReplacementRoomDirectory);
                return new QueueLaunchOutcome(request.RoomDirectory);
            }, _ => Task.FromResult(0d), () => 16d, () => Now,
                advancer: advancer, conductorObligations: store);
            await scheduler.TickOnceAsync(Ct);
            Assert.Equal(1, launches);
            var launched = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(QueueItemState.Launched, launched.State);
            Assert.Equal(launched.AttemptId, launched.ReplacementReviewAction!.ReplacementAttemptId);
            await scheduler.TickOnceAsync(Ct);
            Assert.Equal(1, launches);
        }
        finally { DirectoryCleanup.DeleteRecursively(home); }
    }

    [Fact]
    public async Task Uncertain_worker_launch_keeps_its_target_and_never_retries()
    {
        var home = TempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var (source, store, advancer, _) = await SeedAsync(home);
            await ReplacementReviewConductorCommand.ExecuteAsync(
                Options(source), TextWriter.Null, home, advancer, store, Ct);
            var launches = 0;
            var scheduler = new QueueSchedulerService((_, _) =>
            {
                Interlocked.Increment(ref launches);
                throw new IOException("simulated uncertain worker launch");
            }, _ => Task.FromResult(0d), () => 16d, () => Now,
                advancer: advancer, conductorObligations: store);
            await scheduler.TickOnceAsync(Ct);
            var failed = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.True(failed.Halted);
            Assert.NotNull(failed.ReplacementReviewAction!.ReplacementAttemptId);
            Assert.NotNull(failed.ReplacementReviewAction.ReplacementRoomDirectory);
            await scheduler.TickOnceAsync(Ct);
            Assert.Equal(1, launches);
            var retained = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(failed.ReplacementReviewAction.ReplacementAttemptId,
                retained.ReplacementReviewAction!.ReplacementAttemptId);
            Assert.NotNull(retained.ReplacementReviewAction.BlockedReason);
            Assert.Equal(ConductorObligationStatus.TransportAcknowledged,
                (await store.ReadAsync(source.StoppedWorkJudgment!.Key!, Ct))!.Status);
        }
        finally { DirectoryCleanup.DeleteRecursively(home); }
    }

    [Fact]
    public async Task Changed_remote_head_before_launch_blocks_the_action_and_starts_no_worker()
    {
        var home = TempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var (source, store, advancer, gh) = await SeedAsync(home);
            await ReplacementReviewConductorCommand.ExecuteAsync(
                Options(source), TextWriter.Null, home, advancer, store, Ct);
            gh.HeadSha = OtherHead;
            var launches = 0;
            var scheduler = new QueueSchedulerService((_, _) =>
            {
                Interlocked.Increment(ref launches);
                return Task.FromResult(new QueueLaunchOutcome(null));
            }, _ => Task.FromResult(0d), () => 16d, () => Now,
                advancer: advancer, conductorObligations: store);
            await scheduler.TickOnceAsync(Ct);
            Assert.Equal(0, launches);
            var row = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.NotNull(row.ReplacementReviewAction!.BlockedReason);
            Assert.Equal(QueueItemState.Failed, row.State);
            Assert.Null(row.ReplacementReviewAction.ReplacementAttemptId);
        }
        finally { DirectoryCleanup.DeleteRecursively(home); }
    }

    [Theory]
    [InlineData("approve", WorkStage.Ready)]
    [InlineData("block", WorkStage.Fix)]
    public async Task Exact_head_verdict_is_proved_before_advancement_erases_target(
        string decision, WorkStage expectedStage)
    {
        var home = TempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var (source, store, advancer, _) = await SeedAsync(home);
            await ReplacementReviewConductorCommand.ExecuteAsync(
                Options(source), TextWriter.Null, home, advancer, store, Ct);
            var targetRoom = Path.Combine(home, "rooms", "replacement");
            await WriteVerdictAsync(targetRoom, decision, Head);
            var target = new FleetAttemptId("replacement-attempt");
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
            {
                Items = snapshot.Items.Select(item => item with
                {
                    State = QueueItemState.Done,
                    AttemptId = target,
                    RoomDirectory = targetRoom,
                    ReplacementReviewAction = item.ReplacementReviewAction! with
                    {
                        ReplacementAttemptId = target,
                        ReplacementRoomDirectory = targetRoom,
                    },
                }).ToList(),
            }, Ct);
            await advancer.AdvanceAsync(Now, Ct);
            var advanced = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(expectedStage, advanced.Stage);
            Assert.Null(advanced.AttemptId);
            Assert.Null(advanced.RoomDirectory);
            var action = advanced.ReplacementReviewAction!;
            Assert.Equal(target, action.ReplacementAttemptId);
            Assert.Equal(targetRoom, action.ReplacementRoomDirectory);
            Assert.NotNull(action.CompletionProof);
            Assert.NotNull(action.AdviceDigest);
            Assert.Equal(ConductorObligationStatus.TransportAcknowledged,
                (await store.ReadAsync(action.ObligationKey, Ct))!.Status);

            var scheduler = Scheduler(store, advancer, new FakeGh());
            await scheduler.ReconcileReplacementReviewActionsAsync(Ct);
            var observed = (await store.ReadAsync(action.ObligationKey, Ct))!;
            Assert.Equal(ConductorObligationStatus.ActionObserved, observed.Status);
            Assert.Equal(action.CompletionProof, observed.ActionProof);
            await scheduler.ReconcileReplacementReviewActionsAsync(Ct);
            Assert.Equal(observed.ActionProof, (await store.ReadAsync(action.ObligationKey, Ct))!.ActionProof);
        }
        finally { DirectoryCleanup.DeleteRecursively(home); }
    }

    [Fact]
    public async Task Crash_after_proof_persistence_reconciles_the_same_target_before_advancement()
    {
        var home = TempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var (source, store, advancer, _) = await SeedAsync(home);
            await ReplacementReviewConductorCommand.ExecuteAsync(
                Options(source), TextWriter.Null, home, advancer, store, Ct);
            var room = Path.Combine(home, "rooms", "replacement");
            await WriteVerdictAsync(room, "block", Head);
            var target = new FleetAttemptId("replacement-attempt");
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
            {
                Items = snapshot.Items.Select(item => item with
                {
                    State = QueueItemState.Done,
                    AttemptId = target,
                    RoomDirectory = room,
                    ReplacementReviewAction = item.ReplacementReviewAction! with
                    {
                        ReplacementAttemptId = target,
                        ReplacementRoomDirectory = room,
                    },
                }).ToList(),
            }, Ct);
            advancer.ReplacementReviewAfterProofPersisted = () => throw new IOException("simulated crash cut");
            await Assert.ThrowsAsync<IOException>(() => advancer.AdvanceAsync(Now, Ct));
            var persisted = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(WorkStage.Review, persisted.Stage);
            Assert.Equal(target, persisted.AttemptId);
            var proof = Assert.IsType<string>(persisted.ReplacementReviewAction!.CompletionProof);
            var scheduler = Scheduler(store, advancer, new FakeGh());
            await scheduler.ReconcileReplacementReviewActionsAsync(Ct);
            Assert.Equal(proof, (await store.ReadAsync(source.StoppedWorkJudgment!.Key!, Ct))!.ActionProof);
            advancer.ReplacementReviewAfterProofPersisted = null;
            await advancer.AdvanceAsync(Now, Ct);
            var advanced = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(WorkStage.Fix, advanced.Stage);
            Assert.Equal(target, advanced.ReplacementReviewAction!.ReplacementAttemptId);
            Assert.Equal(proof, advanced.ReplacementReviewAction.CompletionProof);
        }
        finally { DirectoryCleanup.DeleteRecursively(home); }
    }

    [Fact]
    public async Task Round_two_block_keeps_the_one_fix_and_round_four_re_review_can_finish()
    {
        var home = TempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var (source, store, firstAdvancer, gh) = await SeedAsync(home);
            await ReplacementReviewConductorCommand.ExecuteAsync(
                Options(source), TextWriter.Null, home, firstAdvancer, store, Ct);
            var reviewRoom = Path.Combine(home, "rooms", "replacement");
            await WriteVerdictAsync(reviewRoom, "block", Head);
            var reviewAttempt = new FleetAttemptId("replacement-attempt");
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
            {
                Items = snapshot.Items.Select(item => item with
                {
                    State = QueueItemState.Done,
                    AttemptId = reviewAttempt,
                    RoomDirectory = reviewRoom,
                    ReplacementReviewAction = item.ReplacementReviewAction! with
                    {
                        ReplacementAttemptId = reviewAttempt,
                        ReplacementRoomDirectory = reviewRoom,
                    },
                }).ToList(),
            }, Ct);
            await firstAdvancer.AdvanceAsync(Now, Ct);
            var fix = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(WorkStage.Fix, fix.Stage);
            Assert.Equal(3, fix.Round);
            Assert.True(fix.AutomaticFixUsed);
            var originalProof = Assert.IsType<string>(fix.ReplacementReviewAction!.CompletionProof);

            var fixRoom = Path.Combine(home, "rooms", "fix");
            Directory.CreateDirectory(fixRoom);
            await TerminalSentinelWriter.WriteAsync(fixRoom,
                new WorkflowStatusView(WorkflowOutcome.Succeeded, [], [], null), Ct);
            gh.HeadSha = OtherHead;
            var identity = RepositoryIdentity.From("https://" + Repository, null)!;
            var laterAdvancer = new WorkItemAdvancer(gh,
                (_, _) => Task.FromResult<string?>(OtherHead),
                (_, _) => Task.FromResult<RepositoryIdentity?>(identity));
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
            {
                Items = snapshot.Items.Select(item => item with
                {
                    State = QueueItemState.Done,
                    AttemptId = new FleetAttemptId("fix-attempt"),
                    AttemptBaseRevision = Head,
                    RoomDirectory = fixRoom,
                }).ToList(),
            }, Ct);
            await laterAdvancer.AdvanceAsync(Now, Ct);
            var rereview = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(WorkStage.ReReview, rereview.Stage);
            Assert.Equal(4, rereview.Round);
            Assert.True(rereview.AutomaticFixUsed);
            Assert.Equal(originalProof, rereview.ReplacementReviewAction!.CompletionProof);

            var rereviewRoom = Path.Combine(home, "rooms", "rereview");
            await WriteVerdictAsync(rereviewRoom, "approve", OtherHead);
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
            {
                Items = snapshot.Items.Select(item => item with
                {
                    State = QueueItemState.Done,
                    AttemptId = new FleetAttemptId("rereview-attempt"),
                    RoomDirectory = rereviewRoom,
                }).ToList(),
            }, Ct);
            await laterAdvancer.AdvanceAsync(Now, Ct);
            var ready = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(WorkStage.Ready, ready.Stage);
            Assert.Equal(4, ready.Round);
            Assert.True(ready.AutomaticFixUsed);
            Assert.Equal(reviewAttempt, ready.ReplacementReviewAction!.ReplacementAttemptId);
            Assert.Equal(originalProof, ready.ReplacementReviewAction.CompletionProof);
        }
        finally { DirectoryCleanup.DeleteRecursively(home); }
    }

    [Fact]
    public async Task ReReview_replacement_preserves_used_fix_and_block_can_recover_review_without_another_fix()
    {
        var home = TempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var (source, store, advancer, _) = await SeedAsync(home,
                WorkStage.ReReview, round: 3, automaticFixUsed: true);
            await ReplacementReviewConductorCommand.ExecuteAsync(
                Options(source), TextWriter.Null, home, advancer, store, Ct);
            var queued = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(WorkStage.ReReview, queued.Stage);
            Assert.Equal(4, queued.Round);
            Assert.True(queued.AutomaticFixUsed);
            var room = Path.Combine(home, "rooms", "replacement");
            await WriteVerdictAsync(room, "block", Head);
            var target = new FleetAttemptId("replacement-attempt");
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
            {
                Items = snapshot.Items.Select(item => item with
                {
                    State = QueueItemState.Done,
                    AttemptId = target,
                    RoomDirectory = room,
                    ReplacementReviewAction = item.ReplacementReviewAction! with
                    {
                        ReplacementAttemptId = target,
                        ReplacementRoomDirectory = room,
                    },
                }).ToList(),
            }, Ct);
            await advancer.AdvanceAsync(Now, Ct);
            var stopped = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.True(stopped.Halted);
            Assert.Equal(WorkStage.ReReview, stopped.Stage);
            Assert.Equal(4, stopped.Round);
            Assert.True(stopped.AutomaticFixUsed);
            Assert.NotNull(stopped.ReplacementReviewAction!.CompletionProof);
            var scheduler = Scheduler(store, advancer, new FakeGh());
            await scheduler.ReconcileReplacementReviewActionsAsync(Ct);
            Assert.Equal(ConductorObligationStatus.ActionObserved,
                (await store.ReadAsync(source.StoppedWorkJudgment!.Key!, Ct))!.Status);
        }
        finally { DirectoryCleanup.DeleteRecursively(home); }
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("incomplete")]
    [InlineData("decisionless")]
    [InlineData("wrong-head-approve")]
    public async Task Unusable_replacement_outcomes_never_acknowledge_the_action(string kind)
    {
        var home = TempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var (source, store, advancer, _) = await SeedAsync(home);
            await ReplacementReviewConductorCommand.ExecuteAsync(
                Options(source), TextWriter.Null, home, advancer, store, Ct);
            var room = Path.Combine(home, "rooms", "replacement");
            await WriteVerdictAsync(room, "approve", kind == "wrong-head-approve" ? OtherHead : Head);
            var path = Path.Combine(room, "verdict.json");
            if (kind == "malformed") await File.WriteAllTextAsync(path, "{not-json", Ct);
            if (kind == "incomplete") await File.WriteAllTextAsync(path,
                "{\"reviewedRef\":\"" + Head + "\",\"completion\":\"incomplete\",\"decision\":\"approve\",\"summary\":\"unfinished\",\"findings\":[]}", Ct);
            if (kind == "decisionless") await File.WriteAllTextAsync(path,
                "{\"reviewedRef\":\"" + Head + "\",\"completion\":\"complete\",\"summary\":\"unknown\",\"findings\":[]}", Ct);
            var target = new FleetAttemptId("replacement-attempt");
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
            {
                Items = snapshot.Items.Select(item => item with
                {
                    State = QueueItemState.Done,
                    AttemptId = target,
                    RoomDirectory = room,
                    ReplacementReviewAction = item.ReplacementReviewAction! with
                    {
                        ReplacementAttemptId = target,
                        ReplacementRoomDirectory = room,
                    },
                }).ToList(),
            }, Ct);
            await advancer.AdvanceAsync(Now, Ct);
            var after = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Null(after.ReplacementReviewAction!.CompletionProof);
            var scheduler = Scheduler(store, advancer, new FakeGh());
            await scheduler.ReconcileReplacementReviewActionsAsync(Ct);
            Assert.Equal(ConductorObligationStatus.TransportAcknowledged,
                (await store.ReadAsync(source.StoppedWorkJudgment!.Key!, Ct))!.Status);
        }
        finally { DirectoryCleanup.DeleteRecursively(home); }
    }

    [Fact]
    public async Task Noncanonical_block_can_advance_normally_but_does_not_complete_action()
    {
        var home = TempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var (source, store, advancer, _) = await SeedAsync(home);
            await ReplacementReviewConductorCommand.ExecuteAsync(
                Options(source), TextWriter.Null, home, advancer, store, Ct);
            var targetRoom = Path.Combine(home, "rooms", "replacement");
            await WriteVerdictAsync(targetRoom, "block", "short");
            var target = new FleetAttemptId("replacement-attempt");
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
            {
                Items = snapshot.Items.Select(item => item with
                {
                    State = QueueItemState.Done,
                    AttemptId = target,
                    RoomDirectory = targetRoom,
                    ReplacementReviewAction = item.ReplacementReviewAction! with
                    {
                        ReplacementAttemptId = target,
                        ReplacementRoomDirectory = targetRoom,
                    },
                }).ToList(),
            }, Ct);
            await advancer.AdvanceAsync(Now, Ct);
            var advanced = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.Equal(WorkStage.Fix, advanced.Stage);
            Assert.Null(advanced.ReplacementReviewAction!.CompletionProof);
            var scheduler = Scheduler(store, advancer, new FakeGh());
            await scheduler.ReconcileReplacementReviewActionsAsync(Ct);
            var blocked = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
            Assert.NotNull(blocked.ReplacementReviewAction!.BlockedReason);
            Assert.Equal(ConductorObligationStatus.TransportAcknowledged,
                (await store.ReadAsync(source.StoppedWorkJudgment!.Key!, Ct))!.Status);
        }
        finally { DirectoryCleanup.DeleteRecursively(home); }
    }

    private static async Task<(QueueItem Item, ConductorObligationStore Store, WorkItemAdvancer Advancer, FakeGh Gh)>
        SeedAsync(string home, WorkStage stage = WorkStage.Review, int round = 1,
            bool automaticFixUsed = false)
    {
        await DaemonSettingsStore.SaveAsync(new DaemonSettings(), BatonPaths.SettingsFile, Ct);
        var sourceRoom = Path.Combine(home, "rooms", "source");
        Directory.CreateDirectory(sourceRoom);
        await TerminalSentinelWriter.WriteAsync(sourceRoom,
            new WorkflowStatusView(WorkflowOutcome.Succeeded, [], [], null), Ct);
        var attempt = new FleetAttemptId("source-attempt");
        var intent = new StoppedWorkJudgment(
            StoppedWorkJudgmentKey.For(Repository, "replacement-test", attempt, stage),
            Repository, "replacement-test", attempt, stage, Now, Holder, 77, Head, null,
            WorkflowOutcome.Succeeded, true, "passing", Now, string.Empty,
            StoppedWorkHaltCause.MissingVerdict, "available", false, "passing");
        intent = intent with { ContextSha256 = StoppedWorkAdviceEvidence.Hash(StoppedWorkAdviceEvidence.Context(intent)) };
        var source = new QueueItem
        {
            Tag = "replacement-test",
            Role = WorkStages.RoleFor(stage),
            ScopeClass = "engine",
            DeclaredTaskSize = TaskSizeDeclaration.Parse("small", "One bounded review recovery"),
            Workspace = home,
            Branch = "stopped-lane",
            Repository = Repository,
            PullRequest = 77,
            Issue = 2518,
            SpecFile = Path.Combine(home, "replacement-test.md"),
            Stage = stage,
            Round = round,
            AutomaticFixUsed = automaticFixUsed,
            State = QueueItemState.Failed,
            Halted = true,
            AttemptId = attempt,
            RoomDirectory = sourceRoom,
            Instructions = "Review the existing PR.",
            StoppedWorkJudgment = intent,
        };
        await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with { Items = [source] }, Ct);
        var identity = RepositoryIdentity.From("https://" + Repository, null)!;
        await ConductorClaimStore.ClaimAsync(identity, Holder, cancellationToken: Ct);
        var store = new ConductorObligationStore(
            new FleetEventLog(BatonPaths.FleetEventsFile, BatonPaths.FleetEventsRolloverFile, 100_000),
            BatonPaths.ConductorObligationsFile, () => Now);
        var obligation = await store.EnqueueAsync(new(
            intent.Key!, Repository, null, source.Tag, Head, StoppedWorkJudgmentKey.Action,
            Holder, Now, StoppedWorkJudgmentKey.Adapter, StoppedWorkJudgmentKey.Capability, true,
            TargetRevision: Head, ContextSha256: intent.ContextSha256), Ct);
        var context = StoppedWorkAdviceEvidence.Context(intent);
        var request = new StoppedWorkAdviceRequest(obligation.ObligationId, Repository, source.Tag,
            attempt, stage, intent.ContextSha256, Now, Head, null, Holder,
            StoppedWorkHaltCause.MissingVerdict, "available", false, "passing", intent.State);
        await store.DecideStoppedWorkOnceAsync(intent.Key!, request, context,
            (_, _) => Task.CompletedTask,
            (row, _, _, _, _) => Task.FromResult(new RetainedStoppedWorkAdviceResponse(
                new StoppedWorkAdviceDecision(row.ObligationId, Repository, source.Tag, attempt.Value,
                    intent.ContextSha256, StoppedWorkAdviceChoice.Hold, "Inspect the missing verdict."),
                CodexReadinessDecisionAdapter.AdapterName, CodexReadinessDecisionAdapter.Model,
                CodexReadinessDecisionAdapter.Effort, Now, new StoppedWorkAdviceUsage(null, null, null))), Ct);
        store.MarkStoppedWorkAdviceSourceChecked(intent.Key!);
        var gh = new FakeGh();
        var advancer = new WorkItemAdvancer(gh, (_, _) => Task.FromResult<string?>(Head),
            (_, _) => Task.FromResult<RepositoryIdentity?>(identity));
        return (source, store, advancer, gh);
    }

    private static ConductorOptions Options(QueueItem item) =>
        ConductorOptionsParser.Parse(["act", "--obligation", item.StoppedWorkJudgment!.Key!,
            "--holder", Holder, "--action", "replace-review", "--expected-head", Head]);

    private static async Task WriteVerdictAsync(string room, string decision, string reviewedRef)
    {
        Directory.CreateDirectory(room);
        var path = Path.Combine(room, "verdict.json");
        var json = JsonSerializer.Serialize(new
        {
            reviewedRef,
            completion = "complete",
            decision,
            summary = decision == "block" ? "A blocking finding." : "Nothing blocking.",
            findings = Array.Empty<object>(),
        });
        await File.WriteAllTextAsync(path, json, Ct);
        await TerminalSentinelWriter.WriteAsync(room,
            new WorkflowStatusView(WorkflowOutcome.Succeeded, [], [path], null), Ct);
    }

    private static QueueSchedulerService Scheduler(
        ConductorObligationStore store, WorkItemAdvancer advancer, FakeGh gh) =>
        new((_, _) => Task.FromResult(new QueueLaunchOutcome(null)),
            _ => Task.FromResult(0d), () => 16d, () => Now,
            advancer: advancer, conductorObligations: store);

    private static string TempHome()
    {
        var home = Path.Combine(Path.GetTempPath(), "baton_review_action_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        return home;
    }

    private sealed class FakeGh : IGhCliRunner
    {
        internal string HeadSha { get; set; } = Head;
        private bool _draft = true;
        public Task<GhCliResult> RunAsync(
            string workingDirectory, IReadOnlyList<string> args, CancellationToken cancellationToken)
        {
            if (args.Contains("checks", StringComparer.Ordinal))
                return Task.FromResult(new GhCliResult(true, 0,
                    "[{\"name\":\"ci\",\"bucket\":\"pass\",\"state\":\"SUCCESS\"}]", string.Empty));
            if (args.Contains("ready", StringComparer.Ordinal))
            {
                _draft = false;
                return Task.FromResult(new GhCliResult(true, 0, string.Empty, string.Empty));
            }
            var json = $$"""{"number":77,"state":"OPEN","isDraft":{{(_draft ? "true" : "false")}},"headRefOid":"{{HeadSha}}","headRefName":"stopped-lane","baseRefName":"main","isCrossRepository":false,"statusCheckRollup":[]}""";
            return Task.FromResult(new GhCliResult(true, 0,
                args.Contains("list", StringComparer.Ordinal) ? "[" + json + "]" : json, string.Empty));
        }
    }
}
