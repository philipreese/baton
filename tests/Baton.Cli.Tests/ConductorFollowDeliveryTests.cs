using System.Diagnostics;
using System.Text.Json;
using Baton.Accounting;
using Baton.Cli.Daemon;
using Baton.Cli.Tests.Daemon;
using Baton.Conductor;
using Baton.Domain;
using Baton.Queue;
using Baton.Status;
using Baton.Tests.Shared;
using Baton.Vendors;

namespace Baton.Cli.Tests;

public sealed class ConductorFollowDeliveryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Repository = "github.com/philipreese/delivery-fixture";
    private const string Head = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly RepositoryIdentity Identity = RepositoryIdentity.From("https://" + Repository, null)!;

    [Fact]
    public async Task Attach_actual_halt_enqueue_notification_receipt_restart_and_idle_controls()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.HaltAsync("historical", notify: false);
        Assert.Null(await fixture.Store.ReadAsync(fixture.Key("historical"), Ct));
        await fixture.CommandAsync("attach");
        await fixture.CommandAsync("attach");
        var scheduler = fixture.Scheduler();
        await scheduler.RecoverAttachedFollowAsync(Ct);
        await scheduler.TickOnceAsync(Ct);
        Assert.Empty(fixture.Calls);
        var halted = await fixture.HaltAsync("one", scheduler: scheduler);
        Assert.NotNull(halted.StoppedWorkJudgment?.FollowAttachmentId);
        Assert.Single(fixture.Calls);
        var original = await fixture.Store.ReadAsync(fixture.Key("one"), Ct);
        Assert.Equal(ConductorObligationStatus.Pending, original?.Status);
        Assert.NotNull(fixture.Receipt("one"));
        await scheduler.NotifyOwnedHaltAsync(halted, Ct);
        await fixture.FollowAsync("one");
        await fixture.Scheduler().RecoverAttachedFollowAsync(Ct);
        await fixture.Scheduler().RecoverAttachedFollowAsync(Ct);
        Assert.Single(fixture.Calls);
        await fixture.HaltAsync("two", scheduler: fixture.Scheduler());
        Assert.Equal(2, fixture.Calls.Count);
        Assert.True(fixture.Calls[1].ResumeSession);
        Assert.Equal("retained-thread", fixture.Calls[1].SessionId);
        Assert.Equal(original, await fixture.Store.ReadAsync(fixture.Key("one"), Ct));
        Assert.All(fixture.Calls, call =>
        {
            Assert.Equal("gpt-5.6-luna", call.Model);
            Assert.Equal("low", call.Effort);
            Assert.Equal(new PermissionGrant(ReadFiles: true), call.PermissionGrant);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Startup_recovers_post_cutover_commit_enqueue_notification_gaps_once(bool enqueued)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        await fixture.HaltAsync("gap", notify: false);
        if (enqueued) await fixture.Scheduler().ReconcileStoppedWorkAdviceAsync(Ct);
        Assert.Empty(fixture.Calls);
        Assert.Equal(enqueued, await fixture.Store.ReadAsync(fixture.Key("gap"), Ct) is not null);
        var reachedIdle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var restarted = fixture.Scheduler(delay: (_, token) =>
        {
            reachedIdle.TrySetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        await restarted.StartAsync(Ct);
        await reachedIdle.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await restarted.StopAsync(Ct);
        Assert.Single(fixture.Calls);
        Assert.NotNull(fixture.Receipt("gap"));
        await fixture.Scheduler().RecoverAttachedFollowAsync(Ct);
        Assert.Single(fixture.Calls);
    }

    [Theory]
    [InlineData("detach")]
    [InlineData("trust")]
    [InlineData("claim")]
    [InlineData("missing")]
    [InlineData("stale")]
    [InlineData("held")]
    public async Task Daemon_required_authority_and_budget_refuse_before_launch(string change)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        var halted = await fixture.HaltAsync("refused", notify: false);
        switch (change)
        {
            case "detach": await fixture.CommandAsync("detach"); break;
            case "trust": ProjectCeilingStore.Revoke(fixture.Workspace, fixture.CeilingPath); break;
            case "claim": await ConductorClaimStore.TakeoverAsync(Identity, "other", "fixture", fixture.Root, cancellationToken: Ct); break;
            case "missing": FileCleanup.EnsureDeleted(BatonPaths.VendorUsageSnapshotFile("codex")); break;
            case "stale": fixture.Usage(DateTimeOffset.UtcNow.AddDays(-3), 1); break;
            case "held": fixture.Usage(DateTimeOffset.UtcNow, 100); break;
        }
        await fixture.Scheduler().NotifyOwnedHaltAsync(halted, Ct);
        Assert.Empty(fixture.Calls);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(fixture.Root, "conductor-follow"), "launch.json", SearchOption.AllDirectories));
        if (change is "missing" or "stale" or "held")
        {
            // The same explicit manual invocation does not inherit daemon-only runway admission.
            Assert.Equal("delivered", (await fixture.FollowAsync("refused")).GetProperty("status").GetString());
            Assert.Single(fixture.Calls);
        }
    }

    [Fact]
    public async Task Racing_cli_daemon_and_legacy_share_durable_owner_and_replay()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        var source = await fixture.HaltAsync("race", notify: false);
        await fixture.Scheduler().ReconcileStoppedWorkAdviceAsync(Ct);
        var legacyCalls = 0;
        async Task<JsonElement> NotifyAsync()
        {
            await fixture.Scheduler().NotifyOwnedHaltAsync(source, Ct);
            return default;
        }
        var results = await Task.WhenAll(
            fixture.FollowAsync("race"),
            NotifyAsync(),
            Task.Run(async () =>
            {
                try { await fixture.LegacyAsync("race", () => Interlocked.Increment(ref legacyCalls)); }
                catch (ConductorObligationStoreException) { }
                return default(JsonElement);
            }, Ct));
        Assert.Equal(1, fixture.Calls.Count + legacyCalls);
        await fixture.FollowAsync("race");
        Assert.Equal(1, fixture.Calls.Count + legacyCalls);
        Assert.Equal(3, results.Length);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Legacy_complete_reused_and_uncertain_freezes_different_keys(bool uncertain, bool receiptGap)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        await fixture.HaltAsync("legacy", notify: false);
        await fixture.HaltAsync("other", notify: false);
        await fixture.Scheduler().ReconcileStoppedWorkAdviceAsync(Ct);
        var before = await fixture.Store.ReadAsync(fixture.Key("legacy"), Ct);
        if (uncertain || receiptGap)
            fixture.Store.StoppedWorkAdviceDurabilityObserver = point =>
            {
                if (point == (uncertain ? StoppedWorkAdviceDurabilityPoint.AfterLaunchMarker
                    : StoppedWorkAdviceDurabilityPoint.AfterResponse)) throw new IOException("offline interruption");
            };
        var legacyError = await Record.ExceptionAsync(() => fixture.LegacyAsync("legacy", () => { }));
        Assert.Equal(uncertain || receiptGap, legacyError is not null);
        var original = await fixture.Store.ReadAsync(fixture.Key("legacy"), Ct);
        var replay = await fixture.FollowAsync("legacy");
        Assert.Equal(uncertain ? "uncertain" : "replayed", replay.GetProperty("status").GetString());
        Assert.Empty(fixture.Calls);
        Assert.Equal(original, await fixture.Store.ReadAsync(fixture.Key("legacy"), Ct));
        Assert.Equal(before?.Adapter, original?.Adapter);
        if (uncertain)
        {
            Assert.Equal("Legacy launch outcome uncertain; inspect retained evidence.", replay.GetProperty("diagnostic").GetString());
            Assert.Equal("uncertain", (await fixture.FollowAsync("other")).GetProperty("status").GetString());
            Assert.Empty(fixture.Calls);
        }
        else
        {
            Assert.NotEqual(JsonValueKind.Null, replay.GetProperty("legacyResponse").ValueKind);
            Assert.StartsWith("stopped-work-advice-sha256:", replay.GetProperty("receipt").GetString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Uncertain_daemon_turn_freezes_whole_session_across_restart()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        fixture.Interrupt = true;
        await fixture.HaltAsync("one", scheduler: fixture.Scheduler());
        fixture.Interrupt = false;
        await fixture.HaltAsync("two", scheduler: fixture.Scheduler());
        await fixture.Scheduler().RecoverAttachedFollowAsync(Ct);
        Assert.Single(fixture.Calls);
        Assert.Equal("uncertain", (await fixture.FollowAsync("two")).GetProperty("status").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Detach_or_unreadable_registration_suppresses_legacy_fallback_and_preserves_halt(bool corrupt)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        await fixture.HaltAsync("one", scheduler: fixture.Scheduler());
        Assert.Single(fixture.Calls);
        await DaemonSettingsStore.SaveAsync(new DaemonSettings
        {
            Queue = new QueueSettings
            {
                StoppedWorkAdvice = new Dictionary<string, JsonElement> { [Repository] = JsonSerializer.SerializeToElement(true) },
            },
        }, BatonPaths.SettingsFile, Ct);
        if (corrupt)
            File.WriteAllText(Path.Combine(fixture.Root, "conductor-follow", Identity.FileSlug, "registration.json"), "{}");
        else await fixture.CommandAsync("detach");
        var blocked = await fixture.HaltAsync("detached", scheduler: fixture.Scheduler());
        Assert.True(blocked.Halted);
        Assert.NotNull(await fixture.Store.ReadAsync(fixture.Key("detached"), Ct));
        Assert.Single(fixture.Calls);
        Assert.Equal(0, fixture.LegacyCalls);
        if (!corrupt)
        {
            Assert.Equal("refused", (await fixture.FollowAsync("detached")).GetProperty("status").GetString());
            await fixture.CommandAsync("attach");
            await fixture.Scheduler().RecoverAttachedFollowAsync(Ct);
            Assert.Single(fixture.Calls);
            await fixture.HaltAsync("reattached", scheduler: fixture.Scheduler());
            Assert.Equal(2, fixture.Calls.Count);
            Assert.Equal("retained-thread", fixture.Calls[1].SessionId);
            Assert.Equal(0, fixture.LegacyCalls);
        }
    }

    [Fact]
    public async Task Cli_daemon_legacy_processes_share_one_durable_owner()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        await fixture.HaltAsync("process", notify: false);
        await fixture.Scheduler().ReconcileStoppedWorkAdviceAsync(Ct);
        var processes = new List<Process>();
        try
        {
            foreach (var mode in new[] { "follow", "daemon", "legacy" })
            {
                var start = new ProcessStartInfo("dotnet")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                start.ArgumentList.Add(typeof(ConductorFollowDeliveryTests).Assembly.Location);
                start.ArgumentList.Add("-method");
                start.ArgumentList.Add("*Delivery_process_fixture");
                start.Environment["BATON_DELIVERY_FIXTURE_ROOT"] = fixture.Root;
                start.Environment["BATON_DELIVERY_FIXTURE_MODE"] = mode;
                processes.Add(Process.Start(start)!);
            }
            var watch = Stopwatch.StartNew();
            while (Directory.EnumerateFiles(fixture.Root, "ready-*").Count() != 3)
            {
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), "Offline delivery processes did not reach the start barrier.");
                await Task.Delay(10, Ct); // wait-ok: poll the three process fixture barrier; bounded by the stopwatch
            }
            File.WriteAllText(Path.Combine(fixture.Root, "go"), "start");
            foreach (var process in processes)
            {
                var (stdout, stderr) = await BoundedProcessWait.RunToExitAsync(process, TimeSpan.FromSeconds(60), Ct);
                Assert.True(process.ExitCode == 0, stdout + stderr);
            }
            Assert.Single(File.ReadAllLines(Path.Combine(fixture.Root, "process-calls.jsonl")));
            Assert.Equal("replayed", (await fixture.FollowAsync("process")).GetProperty("status").GetString());
            Assert.Empty(fixture.Calls);
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    [Fact]
    public async Task Delivery_process_fixture()
    {
        var root = Environment.GetEnvironmentVariable("BATON_DELIVERY_FIXTURE_ROOT");
        if (root is null) return;
        using var fixture = new Fixture(root);
        var mode = Environment.GetEnvironmentVariable("BATON_DELIVERY_FIXTURE_MODE")!;
        File.WriteAllText(Path.Combine(root, "ready-" + mode), "ready");
        var watch = Stopwatch.StartNew();
        while (!File.Exists(Path.Combine(root, "go")))
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), "Offline delivery start barrier was not released.");
            await Task.Delay(10, Ct); // wait-ok: bounded process fixture barrier polling
        }
        if (mode == "follow") await fixture.FollowAsync("process");
        else if (mode == "daemon")
        {
            var source = (await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items.Single();
            await fixture.Scheduler().NotifyOwnedHaltAsync(source, Ct);
        }
        else
        {
            try
            {
                await fixture.LegacyAsync("process", () => File.AppendAllText(
                    Path.Combine(root, "process-calls.jsonl"), "legacy\n"));
            }
            catch (ConductorObligationStoreException) { }
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly IDisposable _scope;
        private readonly bool _ownsRoot;
        public string Root { get; }
        public string Workspace => Path.Combine(Root, "workspace");
        public string CeilingPath => Path.Combine(Root, "project-ceilings.json");
        private string RequestPath => Path.Combine(Root, "explicit-request.json");
        public List<CodexBrokerConfiguration> Calls { get; } = [];
        public bool Interrupt { get; set; }
        public int LegacyCalls { get; private set; }
        public ConductorObligationStore Store { get; }

        public Fixture(string? root = null)
        {
            Root = root ?? Path.Combine(Path.GetTempPath(), "baton-delivery-" + Guid.NewGuid().ToString("N"));
            _ownsRoot = root is null;
            _scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = Root });
            Directory.CreateDirectory(Workspace);
            Store = new(FleetEventLog.OpenOperational(), BatonPaths.ConductorObligationsFile);
        }

        public static Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            return fixture.InitializeAsync();
        }

        private async Task<Fixture> InitializeAsync()
        {
            var fixture = this;
            await ConductorClaimStore.ClaimAsync(Identity, "holder", fixture.Root, cancellationToken: Ct);
            ProjectCeilingStore.Set(fixture.Workspace, ProjectCeiling.Unrestricted, fixture.CeilingPath);
            File.WriteAllText(fixture.RequestPath, JsonSerializer.Serialize(new ConductorFollowRequest(1,
                Repository, fixture.Workspace, "holder", "codex", "gpt-5.6-luna", "low", 30,
                "Fixed instructions", new PermissionGrant(ReadFiles: true)), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            fixture.Usage(DateTimeOffset.UtcNow, 1);
            return fixture;
        }

        public void Usage(DateTimeOffset at, int pct) => VendorUsageHarvester.Persist("codex",
            new("codex", at, null, [new("week", pct, DateTimeOffset.UtcNow.AddDays(7), "offline",
                CodexUsageSource.AccountLimitId, "primary", CodexUsageSource.WeeklyDurationMins)]));

        public string Key(string tag) => StoppedWorkJudgmentKey.For(Repository, tag,
            new FleetAttemptId("attempt-" + tag), WorkStage.Review);

        public async Task CommandAsync(string verb)
        {
            using var output = new StringWriter();
            Assert.Equal(0, await ConductorCommand.ExecuteAsync(
                ConductorOptionsParser.Parse([verb, "--request", RequestPath]), output, Root,
                (_, _) => Task.FromResult<RepositoryIdentity?>(Identity), null, Ct, Broker));
        }

        public async Task<JsonElement> FollowAsync(string tag)
        {
            using var output = new StringWriter();
            using var input = new StringReader(JsonSerializer.Serialize(new { obligationKey = Key(tag) }) + "\n");
            await ConductorCommand.ExecuteAsync(ConductorOptionsParser.Parse(["follow", "--request", RequestPath]),
                output, Root, (_, _) => Task.FromResult<RepositoryIdentity?>(Identity), input, Ct, Broker);
            using var document = JsonDocument.Parse(output.ToString());
            return document.RootElement.Clone();
        }

        public QueueSchedulerService Scheduler(WorkItemAdvancer? advancer = null,
            Func<TimeSpan, CancellationToken, Task>? delay = null) => new(
            (_, _) => Task.FromResult(new QueueLaunchOutcome(null)), _ => Task.FromResult(0d), () => 16d,
            () => DateTimeOffset.UtcNow, advancer: advancer ?? Advancer(), conductorObligations: Store,
            adopt: _ => Task.FromResult<IReadOnlyList<QueueLaneAdoption>>([]),
            loopDriver: new DaemonLoopDriver(delay: delay),
            stoppedWorkAdvice: (_, _, _, _, _) =>
            {
                LegacyCalls++;
                throw new InvalidOperationException("Offline control: legacy fallback must not launch.");
            })
            {
                FollowBroker = Broker,
                FollowRepositoryResolver = (_, _) => Task.FromResult<RepositoryIdentity?>(Identity),
            };

        private static WorkItemAdvancer Advancer() => new(new FakeGh(), (_, _) => Task.FromResult<string?>(Head));

        public async Task<QueueItem> HaltAsync(string tag, bool notify = true, QueueSchedulerService? scheduler = null)
        {
            var room = Path.Combine(Root, "rooms", tag);
            Directory.CreateDirectory(room);
            var verdict = Path.Combine(room, "verdict.json");
            File.WriteAllText(verdict, "{\"reviewedRef\":\"PR #77\",\"completion\":\"complete\",\"summary\":\"No decision\",\"findings\":[]}");
            await TerminalSentinelWriter.WriteAsync(room, new WorkflowStatusView(WorkflowOutcome.Succeeded, [], [verdict], null), Ct);
            var item = new QueueItem
            {
                Tag = tag,
                Role = "review",
                Workspace = Workspace,
                SpecFile = Path.Combine(Root, tag + ".md"),
                Issue = 2632,
                Branch = "2632-lane",
                Repository = Repository,
                Stage = WorkStage.Review,
                State = QueueItemState.Done,
                RoomDirectory = room,
                AttemptId = new FleetAttemptId("attempt-" + tag),
                AttemptBaseRevision = Head,
                PullRequest = 77,
                Instructions = "Fixture",
                AutomaticFixUsed = true,
                OwnedTask = new("task-" + tag, Repository, 2632, "digest", "holder", DateTimeOffset.UtcNow),
            };
            File.WriteAllText(item.SpecFile, "Fixture");
            await QueueStore.MutateAsync(BatonPaths.QueueFile, queue => queue with { Items = [.. queue.Items, item] }, Ct);
            if (notify) await (scheduler ?? Scheduler()).TickOnceAsync(Ct);
            else Assert.Single(await Advancer().AdvanceAsync(DateTimeOffset.UtcNow, Ct));
            return (await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items.Single(row => row.Tag == tag);
        }

        public string? Receipt(string tag)
        {
            var identities = Directory.EnumerateFiles(Path.Combine(Root, "conductor-follow"), "identity.json", SearchOption.AllDirectories);
            foreach (var path in identities)
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.GetProperty("obligationKey").GetString() == Key(tag))
                    return File.ReadAllText(Path.Combine(Path.GetDirectoryName(path)!, "receipt.txt"));
            }
            return null;
        }

        public async Task LegacyAsync(string tag, Action called)
        {
            var obligation = (await Store.ReadAsync(Key(tag), Ct))!;
            var source = (await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items.Single(row => row.Tag == tag).StoppedWorkJudgment!;
            var request = new StoppedWorkAdviceRequest(obligation.ObligationId, Repository, tag, source.AttemptId!.Value,
                source.Stage, source.ContextSha256, source.ObservedAt, source.PullRequestHead, source.AttemptBaseRevision,
                "holder", source.HaltCause, source.RepairAllowance, source.VerdictAvailable, source.RequiredChecks, source.State);
            await Store.DecideStoppedWorkOnceAsync(Key(tag), request, StoppedWorkAdviceEvidence.Context(source),
                (_, _) => Task.CompletedTask, (_, _, _, _, _) =>
                {
                    called();
                    return Task.FromResult(new RetainedStoppedWorkAdviceResponse(new(obligation.ObligationId,
                        Repository, tag, source.AttemptId.Value.Value, source.ContextSha256, StoppedWorkAdviceChoice.Hold,
                        "Retained advice"), StoppedWorkAdviceProviderDescriptor.Codex.Adapter,
                        StoppedWorkAdviceProviderDescriptor.Codex.Model, StoppedWorkAdviceProviderDescriptor.Codex.Effort,
                        DateTimeOffset.UtcNow));
                }, Ct);
        }

        private ConductorFollowBroker Broker => async (configuration, prompt, directory, inputs, output, error, token, started) =>
        {
            Calls.Add(configuration);
            if (!_ownsRoot)
            {
                File.AppendAllText(Path.Combine(Root, "process-calls.jsonl"), "follow\n");
                await Task.Delay(150, token); // wait-ok: hold the offline broker open for competing process admission
            }
            var sourcePath = Assert.Single(inputs);
            using var source = JsonDocument.Parse(File.ReadAllText(sourcePath));
            var key = source.RootElement.GetProperty("idempotencyKey").GetString()!;
            Assert.NotNull(await Store.ReadAsync(key, token));
            Assert.Contains((await QueueStore.LoadAsync(BatonPaths.QueueFile, token)).Items,
                row => row.Halted && row.StoppedWorkJudgment?.Key == key);
            await QueueStore.MutateAsync(BatonPaths.QueueFile, queue => queue, token).WaitAsync(TimeSpan.FromSeconds(5), token);
            if (Interrupt)
            {
                await started!("retained-thread", token);
                throw new IOException("Offline interruption after native identity");
            }
            using var nativeInput = new StringWriter();
            using var nativeOutput = new StringReader("{\"id\":1,\"result\":{}}\n"
                + "{\"id\":2,\"result\":{\"thread\":{\"id\":\"retained-thread\"}}}\n"
                + "{\"id\":3,\"result\":{\"turn\":{\"id\":\"turn\",\"status\":\"inProgress\"}}}\n"
                + "{\"method\":\"turn/completed\",\"params\":{\"turn\":{\"status\":\"completed\"}}}\n");
            var result = await CodexAppServerBroker.RunProtocolAsync(configuration, prompt,
                CodexAppServerBroker.CreateDynamicToolPolicy(configuration, directory, inputs, null), null,
                nativeInput, nativeOutput, output, error, token, threadStarted: started);
            Assert.Contains(configuration.ResumeSession ? "thread/resume" : "thread/start", nativeInput.ToString(), StringComparison.Ordinal);
            Assert.Contains("\"threadId\":\"retained-thread\"", nativeInput.ToString(), StringComparison.Ordinal);
            return result;
        };

        public void Dispose() { _scope.Dispose(); if (_ownsRoot) DirectoryCleanup.DeleteRecursively(Root); }
    }

    private sealed class FakeGh : IGhCliRunner
    {
        public Task<GhCliResult> RunAsync(string workspace, IReadOnlyList<string> args, CancellationToken cancellationToken)
        {
            if (args is ["api", ..]) return Task.FromResult(RequiredCheckFixture.Read(args, Repository, Head,
                new GhCliResult(true, 0, "[{\"name\":\"ci\",\"bucket\":\"pass\"}]", "")));
            if (args is ["pr", "checks", ..]) return Task.FromResult(new GhCliResult(true, 0,
                "[{\"name\":\"ci\",\"bucket\":\"pass\",\"state\":\"SUCCESS\"}]", ""));
            var pr = "{\"number\":77,\"state\":\"OPEN\",\"isDraft\":true,\"headRefOid\":\"" + Head
                + "\",\"headRefName\":\"2632-lane\",\"baseRefName\":\"main\",\"isCrossRepository\":false,\"statusCheckRollup\":[]}";
            return Task.FromResult(new GhCliResult(true, 0, args is ["pr", "view", ..] ? pr : "[" + pr + "]", ""));
        }
    }
}
