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

public sealed class StoppedWorkAdviceSchedulerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
    private const string Repository = "github.com/aer-works/baton";
    private const string Head = "0123456789abcdef0123456789abcdef01234567";

    [Theory]
    [InlineData("codex")]
    [InlineData("claude")]
    [InlineData("invalid")]
    public async Task Separate_provider_selection_is_frozen_and_invalid_selection_has_no_marker(string selection)
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var item = Item("provider-choice", "provider-attempt", "conductor-one", home);
            await SeedAsync(home, [item], "conductor-one");
            await DaemonSettingsStore.SaveAsync(new DaemonSettings
            {
                Queue = new QueueSettings
                {
                    StoppedWorkAdvice = new Dictionary<string, JsonElement> { [Repository] = JsonSerializer.SerializeToElement(true) },
                    StoppedWorkAdviceProvider = JsonSerializer.SerializeToElement(new Dictionary<string, string> { [Repository] = selection }),
                },
            }, BatonPaths.SettingsFile, Ct);
            var store = Store();
            var calls = 0;
            var scheduler = Scheduler(store, new WorkItemAdvancer(new PullRequestGh(), (_, _) => Task.FromResult<string?>(Head)),
                (obligation, request, _, _, _) =>
                {
                    calls++;
                    var provider = selection == "claude" ? StoppedWorkAdviceProviderDescriptor.Claude : StoppedWorkAdviceProviderDescriptor.Codex;
                    return Task.FromResult(Response(obligation, request) with
                    {
                        Adapter = provider.Adapter,
                        Model = provider.Model,
                        Effort = provider.Effort,
                    });
                });
            await scheduler.TickOnceAsync(Ct);
            await WaitForAsync(async () => (await store.ReadAsync(item.StoppedWorkJudgment!.Key!, Ct))?.Status is
                ConductorObligationStatus.TransportAcknowledged or ConductorObligationStatus.Blocked);
            await scheduler.DrainStoppedWorkAdviceAsync();
            var row = await store.ReadAsync(item.StoppedWorkJudgment!.Key!, Ct);
            if (selection == "invalid")
            {
                Assert.Equal(0, calls);
                Assert.False(File.Exists(Path.Combine(store.GetStoppedWorkAdviceEvidenceDirectory(row!.IdempotencyKey), "launch.json")));
            }
            else
            {
                Assert.Equal(1, calls);
                Assert.Equal(StoppedWorkJudgmentKey.ProviderRoute, row!.Adapter);
                Assert.Equal(ConductorObligationStatus.TransportAcknowledged, row.Status);
                await DaemonSettingsStore.SaveAsync(new DaemonSettings(), BatonPaths.SettingsFile, Ct);
                var restarted = Scheduler(Store(), new WorkItemAdvancer(new PullRequestGh(), (_, _) => Task.FromResult<string?>(Head)),
                    (_, _, _, _, _) => throw new InvalidOperationException("Settings drift cannot charge another provider"));
                await restarted.TickOnceAsync(Ct);
                await restarted.DrainStoppedWorkAdviceAsync();
                Assert.Equal(1, calls);
            }
        }
        finally { DirectoryCleanup.DeleteRecursively(home); }
    }

    [Fact]
    public async Task Disabling_advice_still_recovers_a_saved_response_without_another_call()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var item = Item("saved-response", "saved-attempt", "conductor-one", home);
            await SeedAsync(home, [item], "conductor-one");
            var intent = item.StoppedWorkJudgment!;
            var store = Store();
            var obligation = await store.EnqueueAsync(new(
                intent.Key!, Repository, null, item.Tag, Head, StoppedWorkJudgmentKey.Action,
                intent.Holder!, Now, StoppedWorkJudgmentKey.Adapter, StoppedWorkJudgmentKey.Capability,
                true, TargetRevision: Head, ContextSha256: intent.ContextSha256), Ct);
            var context = StoppedWorkAdviceEvidence.Context(intent);
            var request = new StoppedWorkAdviceRequest(obligation.ObligationId, Repository, item.Tag,
                intent.AttemptId!.Value, intent.Stage, intent.ContextSha256, Now, Head, null,
                intent.Holder!, intent.HaltCause, intent.RepairAllowance, intent.VerdictAvailable,
                intent.RequiredChecks, intent.State);
            store.StoppedWorkAdviceDurabilityObserver = point =>
            {
                if (point == StoppedWorkAdviceDurabilityPoint.AfterResponse)
                    throw new IOException("simulated interruption after saving response");
            };
            await Assert.ThrowsAsync<ConductorObligationStoreException>(() =>
                store.DecideStoppedWorkOnceAsync(intent.Key!, request, context,
                    (_, _) => Task.CompletedTask,
                    (_, _, _, _, _) => Task.FromResult(Response(obligation, request)), Ct));
            await DaemonSettingsStore.SaveAsync(new DaemonSettings(), BatonPaths.SettingsFile, Ct);
            var restarted = Store();
            var calls = 0;
            var scheduler = Scheduler(restarted, new WorkItemAdvancer(new PullRequestGh(),
                (_, _) => Task.FromResult<string?>(Head)), (_, _, _, _, _) =>
            {
                Interlocked.Increment(ref calls);
                throw new InvalidOperationException("Recovery must not call a provider");
            });
            await scheduler.TickOnceAsync(Ct);
            await WaitForAsync(async () =>
                (await restarted.ReadAsync(intent.Key!, Ct))?.Status
                    == ConductorObligationStatus.TransportAcknowledged);
            await scheduler.DrainStoppedWorkAdviceAsync();
            Assert.Equal(0, calls);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task An_opted_in_stopped_intent_is_called_once_across_duplicate_ticks_and_restart()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var item = Item("stopped-once", "attempt-one", "conductor-one", home);
            await SeedAsync(home, [item], "conductor-one");
            var store = Store();
            var calls = 0;
            var response = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var scheduler = Scheduler(store, new WorkItemAdvancer(new PullRequestGh(), (_, _) =>
                Task.FromResult<string?>(Head)), (obligation, request, _, _, _) =>
            {
                Interlocked.Increment(ref calls);
                response.TrySetResult(true);
                return Task.FromResult(Response(obligation, request));
            });

            await scheduler.TickOnceAsync(Ct);
            await response.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);
            await WaitForAsync(async () =>
                (await store.ReadAsync(item.StoppedWorkJudgment!.Key!, Ct))?.Status
                    == ConductorObligationStatus.TransportAcknowledged);

            await scheduler.TickOnceAsync(Ct);

            var restarted = Scheduler(store, new WorkItemAdvancer(new PullRequestGh(), (_, _) =>
                Task.FromResult<string?>(Head)), (_, _, _, _, _) =>
            {
                throw new InvalidOperationException("a retained stopped-work response must not relaunch");
            });
            await restarted.TickOnceAsync(Ct);

            Assert.Equal(1, calls);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Two_stopped_intents_are_serialized_to_one_provider_call_at_a_time()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var first = Item("stopped-a", "attempt-a", "conductor-one", home);
            var second = Item("stopped-b", "attempt-b", "conductor-one", home);
            await SeedAsync(home, [first, second], "conductor-one");
            var store = Store();
            var firstEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var active = 0;
            var maximum = 0;
            var calls = 0;
            async Task<RetainedStoppedWorkAdviceResponse> Launch(
                ConductorObligation obligation, StoppedWorkAdviceRequest request,
                StoppedWorkAdviceContext adviceContext, string evidenceDirectory,
                CancellationToken cancellationToken)
            {
                var now = Interlocked.Increment(ref active);
                Interlocked.Increment(ref calls);
                while (true)
                {
                    var old = Volatile.Read(ref maximum);
                    if (now <= old || Interlocked.CompareExchange(ref maximum, now, old) == old) break;
                }

                if (request.Tag == first.Tag)
                {
                    firstEntered.TrySetResult(true);
                    await releaseFirst.Task.WaitAsync(cancellationToken);
                }
                else
                {
                    secondEntered.TrySetResult(true);
                }

                Interlocked.Decrement(ref active);
                return Response(obligation, request);
            }

            var scheduler = Scheduler(store, new WorkItemAdvancer(new PullRequestGh(), (_, _) =>
                Task.FromResult<string?>(Head)), Launch);
            await scheduler.TickOnceAsync(Ct);
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);
            await scheduler.TickOnceAsync(Ct);
            Assert.Equal(1, calls);
            Assert.False(secondEntered.Task.IsCompleted);

            releaseFirst.TrySetResult(true);
            await WaitForAsync(async () =>
                (await store.ReadAsync(first.StoppedWorkJudgment!.Key!, Ct))?.Status
                    == ConductorObligationStatus.TransportAcknowledged);
            await WaitForAsync(async () =>
            {
                await scheduler.TickOnceAsync(Ct);
                return secondEntered.Task.IsCompleted;
            });
            Assert.Equal(1, Volatile.Read(ref maximum));

            await WaitForAsync(async () =>
                (await store.ReadAsync(second.StoppedWorkJudgment!.Key!, Ct))?.Status
                    == ConductorObligationStatus.TransportAcknowledged);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task A_tick_does_not_wait_for_stopped_work_advice()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var item = Item("stopped-wait", "attempt-wait", "conductor-one", home);
            await SeedAsync(home, [item], "conductor-one");
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var scheduler = Scheduler(Store(), new WorkItemAdvancer(new PullRequestGh(), (_, _) =>
                Task.FromResult<string?>(Head)), async (_, _, _, _, cancellationToken) =>
            {
                entered.TrySetResult(true);
                await release.Task.WaitAsync(cancellationToken);
                throw new OperationCanceledException(cancellationToken);
            });

            var tick = scheduler.TickOnceAsync(Ct);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);
            await tick.WaitAsync(TimeSpan.FromSeconds(60), Ct);

            release.TrySetResult(true);
            await scheduler.DrainStoppedWorkAdviceAsync();
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Stopping_the_scheduler_cancels_and_joins_the_advice_call()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var item = Item("stopped-stop", "attempt-stop", "conductor-one", home);
            await SeedAsync(home, [item], "conductor-one");
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var scheduler = Scheduler(Store(), new WorkItemAdvancer(new PullRequestGh(), (_, _) =>
                Task.FromResult<string?>(Head)), async (_, _, _, _, cancellationToken) =>
            {
                entered.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("unreachable");
            });

            await scheduler.TickOnceAsync(Ct);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);

            await scheduler.DrainStoppedWorkAdviceAsync().WaitAsync(TimeSpan.FromSeconds(60), Ct);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Owner_drift_is_refused_before_the_provider_call_or_launch_marker()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var item = Item("stopped-drift", "attempt-drift", "conductor-one", home);
            await SeedAsync(home, [item], "conductor-two");
            var store = Store();
            var calls = 0;
            var scheduler = Scheduler(store, new WorkItemAdvancer(new PullRequestGh(), (_, _) =>
                Task.FromResult<string?>(Head)), (_, _, _, _, _) =>
            {
                Interlocked.Increment(ref calls);
                throw new InvalidOperationException("preflight should refuse before provider launch");
            });

            await scheduler.TickOnceAsync(Ct);
            await WaitForAsync(async () =>
                (await store.ReadAsync(item.StoppedWorkJudgment!.Key!, Ct))?.Status
                    == ConductorObligationStatus.Blocked);

            Assert.Equal(0, calls);
            var evidence = store.GetStoppedWorkAdviceEvidenceDirectory(item.StoppedWorkJudgment!.Key!);
            Assert.False(File.Exists(Path.Combine(evidence, "launch.json")));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Changed_pull_request_head_is_refused_before_provider_launch()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var item = Item("stopped-head-drift", "attempt-head-drift", "conductor-one", home);
            await SeedAsync(home, [item], "conductor-one");
            var store = Store();
            var calls = 0;
            var scheduler = Scheduler(store, new WorkItemAdvancer(
                new PullRequestGh("ffffffffffffffffffffffffffffffffffffffff"),
                (_, _) => Task.FromResult<string?>(Head)), (_, _, _, _, _) =>
            {
                Interlocked.Increment(ref calls);
                throw new InvalidOperationException("changed source must refuse before provider launch");
            });

            await scheduler.TickOnceAsync(Ct);
            await WaitForAsync(async () =>
                (await store.ReadAsync(item.StoppedWorkJudgment!.Key!, Ct))?.Status
                    == ConductorObligationStatus.Blocked);

            Assert.Equal(0, calls);
            var evidence = store.GetStoppedWorkAdviceEvidenceDirectory(item.StoppedWorkJudgment!.Key!);
            Assert.False(File.Exists(Path.Combine(evidence, "launch.json")));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task Another_lifecycle_advances_while_stopped_advice_provider_is_blocked()
    {
        var home = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var stopped = Item("stopped-blocked", "attempt-blocked", "conductor-one", home);
            var room = await WriteSettledRoomAsync(home, ApprovingVerdict);
            var verdictPath = Path.Combine(room, "verdict.json");
            var advancing = new QueueItem
            {
                Tag = "advancing-review",
                Role = "review",
                ScopeClass = "engine",
                Workspace = home,
                Branch = "stopped-lane",
                Repository = Repository,
                PullRequest = 77,
                SpecFile = Path.Combine(home, "advancing-review.md"),
                Stage = WorkStage.Review,
                State = QueueItemState.Done,
                RoomDirectory = room,
                LastVerdict = verdictPath,
                Instructions = "Advance this settled review.",
            };
            await SeedAsync(home, [stopped], "conductor-one");
            await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with { Held = true }, Ct);

            var store = Store();
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            var scheduler = Scheduler(store, new WorkItemAdvancer(new PullRequestGh(),
                (_, _) => Task.FromResult<string?>(Head)), async (obligation, request, _, _, token) =>
            {
                Interlocked.Increment(ref calls);
                entered.TrySetResult(true);
                await release.Task.WaitAsync(token);
                return Response(obligation, request);
            });

            try
            {
                await scheduler.TickOnceAsync(Ct);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);
                await QueueStore.MutateAsync(BatonPaths.QueueFile,
                    state => state with { Items = state.Items.Append(advancing).ToArray() }, Ct);
                await scheduler.TickOnceAsync(Ct);
                var snapshot = await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct);
                var advanced = Assert.Single(snapshot.Items, current => current.Tag == advancing.Tag);
                Assert.True(advanced.Stage == WorkStage.Ready, advanced.Error);
                Assert.Equal(QueueItemState.Queued, advanced.State);
                Assert.Equal(1, calls);
                Assert.Equal(ConductorObligationStatus.Submitted,
                    (await store.ReadAsync(stopped.StoppedWorkJudgment!.Key!, Ct))?.Status);
            }
            finally
            {
                release.TrySetResult(true);
                await scheduler.DrainStoppedWorkAdviceAsync();
            }
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

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

    private static async Task SeedAsync(string home, IReadOnlyList<QueueItem> items, string claimHolder)
    {
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
        await QueueStore.MutateAsync(BatonPaths.QueueFile, state => state with { Items = items }, Ct);
        var identity = RepositoryIdentity.From("https://" + Repository, null)!;
        await ConductorClaimStore.ClaimAsync(identity, claimHolder, cancellationToken: Ct);
    }

    private static ConductorObligationStore Store() => new(
        new FleetEventLog(BatonPaths.FleetEventsFile, BatonPaths.FleetEventsRolloverFile, 100_000),
        BatonPaths.ConductorObligationsFile,
        () => Now);

    private static QueueItem Item(string tag, string attempt, string holder, string home) => new()
    {
        Tag = tag,
        Role = "review",
        ScopeClass = "engine",
        Workspace = home,
        Branch = "stopped-lane",
        Repository = Repository,
        PullRequest = 77,
        SpecFile = Path.Combine(home, tag + ".md"),
        Stage = WorkStage.Review,
        State = QueueItemState.Failed,
        Halted = true,
        AttemptId = new FleetAttemptId(attempt),
        StoppedWorkJudgment = new StoppedWorkJudgment(
            StoppedWorkJudgmentKey.For(Repository, tag, new FleetAttemptId(attempt), WorkStage.Review),
            Repository, tag, new FleetAttemptId(attempt), WorkStage.Review, Now, holder, 77, Head, null,
            "Succeeded", true, "passing", Now, StoppedWorkAdviceEvidence.Hash(new StoppedWorkAdviceContext(
                Repository, tag, new FleetAttemptId(attempt), WorkStage.Review, Now, Head, null,
                "Succeeded", true, "passing", Now, StoppedWorkHaltCause.MissingVerdict,
                "available", false, "passing")),
            StoppedWorkHaltCause.MissingVerdict, "available", false, "passing"),
    };

    private static RetainedStoppedWorkAdviceResponse Response(
        ConductorObligation obligation, StoppedWorkAdviceRequest request) => new(
        new StoppedWorkAdviceDecision(
            obligation.ObligationId, request.Repository, request.Tag, request.AttemptId.Value,
            request.ContextSha256, StoppedWorkAdviceChoice.Hold, "The verdict is incomplete."),
        CodexReadinessDecisionAdapter.AdapterName,
        CodexReadinessDecisionAdapter.Model,
        CodexReadinessDecisionAdapter.Effort,
        Now,
        new StoppedWorkAdviceUsage(null, null, null));

    private const string ApprovingVerdict = """
        {"reviewedRef":"0123456789abcdef0123456789abcdef01234567","completion":"complete","decision":"approve","summary":"nothing blocking","findings":[]}
        """;

    private static async Task<string> WriteSettledRoomAsync(string home, string verdictJson)
    {
        var room = Path.Combine(home, "rooms", "advancing-review-"
            + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(room);
        var verdictPath = Path.Combine(room, "verdict.json");
        await File.WriteAllTextAsync(verdictPath, verdictJson, Ct);
        await TerminalSentinelWriter.WriteAsync(
            room,
            new WorkflowStatusView(WorkflowOutcome.Succeeded, [], [verdictPath], null),
            Ct);
        return room;
    }

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

    private static string CreateTempHome()
    {
        var home = Path.Combine(Path.GetTempPath(), "baton_stopped_scheduler_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        return home;
    }

    private sealed class PullRequestGh(string? head = null) : IGhCliRunner
    {
        private readonly string _head = head ?? Head;
        private bool _isDraft = true;

        public Task<GhCliResult> RunAsync(
            string workingDirectory, IReadOnlyList<string> args, CancellationToken cancellationToken)
        {
            if (args.Contains("ready", StringComparer.Ordinal))
            {
                _isDraft = false;
                return Task.FromResult(new GhCliResult(true, 0, string.Empty, string.Empty));
            }

            if (args.Contains("checks", StringComparer.Ordinal))
            {
                return Task.FromResult(new GhCliResult(true, 0,
                    "[{\"name\":\"ci\",\"bucket\":\"pass\",\"state\":\"SUCCESS\"}]", string.Empty));
            }

            var json = $$"""{"number":77,"state":"OPEN","isDraft":{{(_isDraft ? "true" : "false")}},"headRefOid":"{{_head}}","headRefName":"stopped-lane","baseRefName":"main","isCrossRepository":false,"statusCheckRollup":[]}""";
            return Task.FromResult(new GhCliResult(true, 0,
                args.Contains("list", StringComparer.Ordinal) ? "[" + json + "]" : json,
                string.Empty));
        }
    }
}
