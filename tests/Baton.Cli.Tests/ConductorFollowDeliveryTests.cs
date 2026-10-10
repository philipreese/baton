using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
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

public sealed partial class ConductorFollowDeliveryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Repository = "github.com/philipreese/delivery-fixture";
    private const string Head = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly RepositoryIdentity Identity = RepositoryIdentity.From("https://" + Repository, null)!;

    [Fact]
    public async Task Typed_follow_actual_journey_launches_once_and_observes_exact_head_idempotently()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.EnableAutomaticAsync();
        await fixture.CommandAsync("attach");
        fixture.Reply = "Hold";
        await fixture.HaltAsync("warmup");
        fixture.Reply = "ReplaceReview";
        var launches = new List<QueueLaunchRequest>();
        var advancer = fixture.Advancer();
        using var scheduler = fixture.Scheduler(advancer, launch: (request, _) =>
        {
            launches.Add(request);
            return Task.FromResult(new QueueLaunchOutcome(request.RoomDirectory));
        });
        await fixture.HaltAsync("typed", scheduler: scheduler);
        var admitted = await fixture.RowAsync("typed");
        var action = Assert.IsType<QueueReplacementReviewAction>(admitted.ReplacementReviewAction);
        Assert.Equal(QueueReplacementReviewOrigin.Automatic, action.Origin);
        Assert.Equal(ReplacementReviewEvidenceProvenance.CompletedFollow, action.EvidenceProvenance);
        Assert.Equal(string.Empty, action.AdviceDigest);
        Assert.Equal(2, admitted.Round);
        Assert.True(admitted.AutomaticFixUsed);
        Assert.Equal("retained-thread", fixture.Calls[1].SessionId);
        var original = (await fixture.Store.ReadAsync(fixture.Key("typed"), Ct))!;
        Assert.Equal(ConductorObligationStatus.Pending, original.Status);
        Assert.Null(original.TransportReceipt);
        await scheduler.TickOnceAsync(Ct);
        var launched = await fixture.RowAsync("typed");
        Assert.Single(launches);
        Assert.Equal(launched.AttemptId, launched.ReplacementReviewAction!.ReplacementAttemptId);
        Assert.Equal(launched.RoomDirectory, launched.ReplacementReviewAction.ReplacementRoomDirectory);
        await fixture.CompleteReviewAsync("typed");
        await scheduler.TickOnceAsync(Ct);
        await scheduler.ReconcileReplacementReviewActionsAsync(Ct);
        var completed = await fixture.RowAsync("typed");
        var observed = (await fixture.Store.ReadAsync(fixture.Key("typed"), Ct))!;
        Assert.NotNull(completed.ReplacementReviewAction!.CompletionProof);
        ReplacementReviewEvidenceValidator.ValidateIssuedCompletedFollowCompletion(observed, completed, completed.ReplacementReviewAction);
        Assert.Equal(ConductorObligationStatus.ActionObserved, observed.Status);
        Assert.Equal(completed.ReplacementReviewAction!.CompletionProof, observed.ActionProof);
        ReplacementReviewEvidenceValidator.ValidateIssuedCompletedFollowCompletion(observed, completed, completed.ReplacementReviewAction);
        var decision = ConductorFollowSession.ReadDecisionEvidence(action.EvidenceDirectory!)!;
        Assert.Throws<ConductorObligationStoreException>(() => ReplacementReviewEvidenceValidator.ValidateCompletedFollow(
            observed, completed, new(ReplacementReviewEvidenceProvenance.CompletedFollow,
                action.EvidenceDigest!, action.EvidenceDirectory!, decision), "holder", Head, null));
        Assert.Throws<ConductorObligationStoreException>(() => ReplacementReviewEvidenceValidator.ValidateIssuedCompletedFollowCompletion(
            observed, completed, completed.ReplacementReviewAction with { CompletionProof = "foreign-proof" }));
        await scheduler.ReconcileReplacementReviewActionsAsync(Ct);
        await scheduler.TickOnceAsync(Ct);
        Assert.Equal(observed, await fixture.Store.ReadAsync(fixture.Key("typed"), Ct));
        Assert.Single(launches);
        Assert.Equal(2, fixture.Calls.Count);
        Assert.Equal(0, fixture.LegacyCalls);
    }

    [Theory]
    [InlineData("Hold")]
    [InlineData("prose")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("alias")]
    [InlineData("overlong")]
    [InlineData("foreign-key")]
    [InlineData("foreign-id")]
    [InlineData("foreign-head")]
    public async Task Typed_complete_invalid_or_hold_is_retained_without_action_or_paid_retry(string reply)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.EnableAutomaticAsync();
        await fixture.CommandAsync("attach");
        fixture.Reply = reply;
        await fixture.HaltAsync("invalid");
        Assert.NotNull(fixture.Receipt("invalid"));
        Assert.Equal(reply == "Hold", File.Exists(fixture.EventEvidencePath("invalid", "decision.json")));
        await fixture.Scheduler().RecoverAttachedFollowAsync(Ct);
        await fixture.Scheduler().RecoverAttachedFollowAsync(Ct);
        Assert.Null((await fixture.RowAsync("invalid")).ReplacementReviewAction);
        Assert.Equal(1, (await fixture.RowAsync("invalid")).Round);
        Assert.Single(fixture.Calls);
        Assert.Equal(ConductorObligationStatus.Pending, (await fixture.Store.ReadAsync(fixture.Key("invalid"), Ct))!.Status);
    }

    [Theory]
    [InlineData("both-trailers", true)]
    [InlineData("completion-then-usage", true)]
    [InlineData("usage-only", false)]
    [InlineData("error", false)]
    [InlineData("foreign-thread", false)]
    [InlineData("repeated-turn", false)]
    [InlineData("repeated-completion", false)]
    [InlineData("post-completion-message", false)]
    public async Task Typed_native_completion_requires_one_successful_same_thread_turn(string stream, bool valid)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.EnableAutomaticAsync();
        await fixture.CommandAsync("attach");
        fixture.Reply = "ReplaceReview";
        fixture.NativeStream = stream;
        await fixture.HaltAsync("native");
        Assert.Equal(valid, (await fixture.RowAsync("native")).ReplacementReviewAction is not null);
        if (!valid)
        {
            fixture.NativeStream = null;
            await fixture.HaltAsync("later");
            Assert.Null((await fixture.RowAsync("later")).ReplacementReviewAction);
        }
        Assert.Single(fixture.Calls);
    }

    [Theory]
    [InlineData("opt-in")]
    [InlineData("held")]
    [InlineData("detach")]
    [InlineData("claim")]
    [InlineData("trust")]
    [InlineData("ineligible-halt")]
    [InlineData("request")]
    [InlineData("generation")]
    [InlineData("thread")]
    [InlineData("context")]
    [InlineData("repository")]
    [InlineData("source-attempt")]
    [InlineData("receipt")]
    public async Task Completed_saved_decision_revalidates_authority_and_immutable_bindings_before_admission(string change)
    {
        using var fixture = await Fixture.CreateAsync();
        if (change != "ineligible-halt") await fixture.EnableAutomaticAsync();
        await fixture.CommandAsync("attach");
        await fixture.HaltAsync("saved", notify: false);
        await fixture.Scheduler().ReconcileStoppedWorkAdviceAsync(Ct);
        fixture.Reply = "ReplaceReview";
        Assert.Equal("delivered", (await fixture.FollowAsync("saved")).GetProperty("status").GetString());
        Assert.NotNull(ConductorFollowSession.ReadDecisionEvidence(Path.GetDirectoryName(fixture.EventEvidencePath("saved", "decision.json"))!));
        await fixture.ChangeAsync("saved", change);
        if (change == "ineligible-halt") await fixture.EnableAutomaticAsync();
        await fixture.Scheduler().RecoverAttachedFollowAsync(Ct);
        var row = await fixture.RowAsync("saved");
        Assert.Null(row.ReplacementReviewAction);
        Assert.Equal(1, row.Round);
        Assert.Single(fixture.Calls);
        Assert.True(File.Exists(fixture.EventEvidencePath("saved", "decision.json")));
    }

    [Fact]
    public async Task Startup_and_raced_notification_consume_saved_typed_decision_without_another_turn()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.EnableAutomaticAsync();
        await fixture.CommandAsync("attach");
        var halted = await fixture.HaltAsync("saved", notify: false);
        await fixture.Scheduler().ReconcileStoppedWorkAdviceAsync(Ct);
        fixture.Reply = "ReplaceReview";
        await fixture.FollowAsync("saved");
        Assert.Null((await fixture.RowAsync("saved")).ReplacementReviewAction);
        await Task.WhenAll(fixture.Scheduler().RecoverAttachedFollowAsync(Ct),
            fixture.Scheduler().NotifyOwnedHaltAsync(halted, Ct));
        Assert.NotNull((await fixture.RowAsync("saved")).ReplacementReviewAction);
        Assert.Equal(2, (await fixture.RowAsync("saved")).Round);
        Assert.Single(fixture.Calls);
    }

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
        await reachedIdle.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        await restarted.StopAsync(Ct);
        Assert.Single(fixture.Calls);
        Assert.NotNull(fixture.Receipt("gap"));
        await fixture.Scheduler().RecoverAttachedFollowAsync(Ct);
        Assert.Single(fixture.Calls);
    }

    [Fact]
    public async Task Hosted_startup_contains_malformed_attached_registration_and_reaches_unrelated_work()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        var halted = await fixture.HaltAsync("malformed-registration", notify: false);
        Assert.NotNull(halted.StoppedWorkJudgment?.FollowAttachmentId);
        File.WriteAllText(fixture.RegistrationPath, "{");
        File.WriteAllText(Path.Combine(fixture.Root, "unrelated-startup-work.md"), "Fixture");
        await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
        {
            Items = [.. snapshot.Items, new QueueItem
            {
                Tag = "unrelated-startup-work",
                Role = "review",
                Workspace = fixture.Workspace,
                SpecFile = Path.Combine(fixture.Root, "unrelated-startup-work.md"),
            }],
        }, Ct);

        var reachedUnrelatedWork = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scheduler = fixture.Scheduler(launch: (request, _) =>
        {
            reachedUnrelatedWork.TrySetResult();
            return Task.FromResult(new QueueLaunchOutcome(request.RoomDirectory));
        });
        await scheduler.StartAsync(Ct);
        await reachedUnrelatedWork.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        await scheduler.StopAsync(Ct);

        Assert.Empty(fixture.Calls);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(fixture.Root, "conductor-follow"),
            "launch.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Marker_free_refusal_for_one_key_does_not_freeze_a_completed_replay_for_another_key()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        await fixture.HaltAsync("never-launched", notify: false);
        await fixture.HaltAsync("real-delivery", notify: false);
        await fixture.Scheduler().ReconcileStoppedWorkAdviceAsync(Ct);

        var refused = await fixture.FollowWithAdmissionAsync("never-launched", (_, _) =>
        {
            ProjectCeilingStore.Revoke(fixture.Workspace, fixture.CeilingPath);
            return Task.CompletedTask;
        });
        Assert.Equal("refused", refused.Status);
        Assert.True(File.Exists(fixture.EventEvidencePath("never-launched", "identity.json")));
        Assert.False(File.Exists(fixture.EventEvidencePath("never-launched", "launch.json")));

        // A retained refusal remains coherent even after its live source is obsolete.
        await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
        {
            Items = snapshot.Items.Select(item => item.Tag == "never-launched"
                ? item with { Halted = false, State = QueueItemState.Done, StoppedWorkJudgment = null }
                : item).ToList(),
        }, Ct);
        ProjectCeilingStore.Set(fixture.Workspace, ProjectCeiling.Unrestricted, fixture.CeilingPath);
        var delivered = await fixture.FollowAsync("real-delivery");
        var replay = await fixture.FollowAsync("real-delivery");

        Assert.Equal("delivered", delivered.GetProperty("status").GetString());
        Assert.Equal("replayed", replay.GetProperty("status").GetString());
        Assert.Equal(delivered.GetProperty("receipt").GetString(), replay.GetProperty("receipt").GetString());
        Assert.Single(fixture.Calls);
        Assert.NotNull(await fixture.Store.ReadAsync(fixture.Key("real-delivery"), Ct));
    }

    [Theory]
    [InlineData("foreign-key", false)]
    [InlineData("foreign-key", true)]
    [InlineData("null-adapter", false)]
    [InlineData("null-adapter", true)]
    [InlineData("foreign-adapter", false)]
    [InlineData("foreign-adapter", true)]
    [InlineData("mismatched-adapter", false)]
    [InlineData("mismatched-key", false)]
    [InlineData("source-key", false)]
    [InlineData("source-tag", false)]
    [InlineData("source-attempt", false)]
    [InlineData("source-stage", false)]
    [InlineData("source-context", false)]
    [InlineData("source-capability", false)]
    public async Task Corrupt_marker_free_original_identity_freezes_delivery_and_replay(string change, bool replay)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        await fixture.HaltAsync("never-launched", notify: false);
        await fixture.HaltAsync("real-delivery", notify: false);
        await fixture.Scheduler().ReconcileStoppedWorkAdviceAsync(Ct);
        var refused = await fixture.FollowWithAdmissionAsync("never-launched", (_, _) =>
        {
            ProjectCeilingStore.Revoke(fixture.Workspace, fixture.CeilingPath);
            return Task.CompletedTask;
        });
        Assert.Equal("refused", refused.Status);
        var identityPath = fixture.EventEvidencePath("never-launched", "identity.json");
        var sourcePath = fixture.EventEvidencePath("never-launched", "source.json");
        var originalSource = File.ReadAllText(sourcePath);
        Assert.Null(JsonNode.Parse(File.ReadAllText(identityPath))!["sessionId"]);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(identityPath)!, "launch.json")));
        ProjectCeilingStore.Set(fixture.Workspace, ProjectCeiling.Unrestricted, fixture.CeilingPath);
        if (replay)
            Assert.Equal("delivered", (await fixture.FollowAsync("real-delivery")).GetProperty("status").GetString());

        var identity = JsonNode.Parse(File.ReadAllText(identityPath))!;
        var source = JsonNode.Parse(originalSource)!;
        switch (change)
        {
            case "foreign-key":
                identity["obligationKey"] = StoppedWorkJudgmentKey.For("github.com/foreign/repository",
                    "never-launched", new FleetAttemptId("attempt-never-launched"), WorkStage.Review);
                break;
            case "null-adapter": identity["sourceAdapter"] = null; break;
            case "foreign-adapter": identity["sourceAdapter"] = "foreign-adapter"; break;
            case "mismatched-adapter": identity["sourceAdapter"] = "claude-subscription-cli"; break;
            case "mismatched-key": identity["obligationKey"] = fixture.Key("different"); break;
            case "source-key": source["idempotencyKey"] = fixture.Key("different"); break;
            case "source-tag": source["context"]!["tag"] = "different"; break;
            case "source-attempt": source["context"]!["attemptId"] = JsonSerializer.SerializeToNode(new FleetAttemptId("different")); break;
            case "source-stage": source["context"]!["stage"] = (int)WorkStage.Implement; break;
            case "source-context": source["context"]!["terminalOutcome"] = "different"; break;
            case "source-capability": source["adapterCapability"] = "foreign-capability"; break;
            default: throw new InvalidOperationException(change);
        }
        File.WriteAllText(identityPath, identity.ToJsonString());
        File.WriteAllText(sourcePath, source.ToJsonString());
        if (!change.StartsWith("source-", StringComparison.Ordinal))
        {
            File.WriteAllText(sourcePath, originalSource);
            Assert.Equal(originalSource, File.ReadAllText(sourcePath));
        }

        var result = await fixture.FollowAsync("real-delivery");
        Assert.Equal("uncertain", result.GetProperty("status").GetString());
        Assert.Equal(replay ? 1 : 0, fixture.Calls.Count);
        Assert.Equal("uncertain", (await fixture.FollowAsync("real-delivery")).GetProperty("status").GetString());
        Assert.Equal(replay ? 1 : 0, fixture.Calls.Count);
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

    [Fact]
    public async Task Manual_follow_winning_legacy_scheduler_race_preserves_source_and_complete_replay()
    {
        using var fixture = await Fixture.CreateAsync();
        await DaemonSettingsStore.SaveAsync(new DaemonSettings
        {
            Queue = new QueueSettings
            {
                StoppedWorkAdvice = new Dictionary<string, JsonElement> { [Repository] = JsonSerializer.SerializeToElement(true) },
            },
        }, BatonPaths.SettingsFile, Ct);
        var source = await fixture.HaltAsync("legacy-waits", notify: false);
        Assert.Null(source.StoppedWorkJudgment!.FollowAttachmentId);
        Assert.True(source.StoppedWorkJudgment.AdviceEligibleAtHalt);
        using var scheduler = fixture.Scheduler();
        await scheduler.ReconcileStoppedWorkAdviceAsync(Ct);
        var original = await fixture.Store.ReadAsync(fixture.Key(source.Tag), Ct);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.WaitInBroker = async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        var follow = fixture.FollowAsync(source.Tag);
        Task? legacy = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);
            // Manual follow holds the shared per-key lock and has selected its durable owner.
            // The real scheduler schedules legacy transport while that owner is still busy.
            await scheduler.NotifyOwnedHaltAsync(source, Ct);
            legacy = Assert.IsAssignableFrom<Task>(typeof(QueueSchedulerService)
                .GetField("_stoppedWorkTask", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(scheduler));
            Assert.Equal(0, fixture.LegacyCalls);
        }
        finally { release.TrySetResult(); }
        var delivered = await follow.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        Assert.Equal("delivered", delivered.GetProperty("status").GetString());
        // Join without cancellation so the ownership refusal reaches the real scheduler catch.
        await legacy!.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        Assert.Equal(original, await fixture.Store.ReadAsync(fixture.Key(source.Tag), Ct));
        Assert.Equal(JsonSerializer.Serialize(source),
            JsonSerializer.Serialize((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items.Single()));
        var receipt = fixture.Receipt(source.Tag);
        Assert.NotNull(receipt);
        var replay = await fixture.FollowAsync(source.Tag);
        Assert.Equal("replayed", replay.GetProperty("status").GetString());
        Assert.Equal(delivered.GetProperty("receipt").GetString(), replay.GetProperty("receipt").GetString());
        Assert.Equal(delivered.GetProperty("response").GetRawText(), replay.GetProperty("response").GetRawText());
        Assert.Equal(receipt, fixture.Receipt(source.Tag));
        Assert.Single(fixture.Calls);
        Assert.Equal(0, fixture.LegacyCalls);
        var directory = fixture.Store.GetStoppedWorkAdviceEvidenceDirectory(fixture.Key(source.Tag));
        Assert.True(File.Exists(Path.Combine(directory, "delivery-owner.json")));
        Assert.False(File.Exists(Path.Combine(directory, "launch.json")));
    }

    [Theory]
    [InlineData("attach")]
    [InlineData("detach")]
    [InlineData("advice")]
    public async Task Deferred_legacy_notification_rechecks_follow_registration_and_advice_revocation(string change)
    {
        using var fixture = await Fixture.CreateAsync();
        await DaemonSettingsStore.SaveAsync(new DaemonSettings
        {
            Queue = new QueueSettings
            {
                StoppedWorkAdvice = new Dictionary<string, JsonElement> { [Repository] = JsonSerializer.SerializeToElement(true) },
            },
        }, BatonPaths.SettingsFile, Ct);
        var first = await fixture.HaltAsync("busy", notify: false);
        var second = await fixture.HaltAsync("deferred", notify: false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scheduler = fixture.Scheduler(legacy: async (obligation, request, _, _, token) =>
        {
            Assert.Equal(first.Tag, request.Tag);
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return new(new(obligation.ObligationId, Repository, first.Tag, request.AttemptId.Value,
                request.ContextSha256, StoppedWorkAdviceChoice.Hold, "Retained advice"),
                StoppedWorkAdviceProviderDescriptor.Codex.Adapter, StoppedWorkAdviceProviderDescriptor.Codex.Model,
                StoppedWorkAdviceProviderDescriptor.Codex.Effort, DateTimeOffset.UtcNow);
        });
        await scheduler.NotifyOwnedHaltAsync(first, Ct);
        var pending = Assert.IsAssignableFrom<Task>(typeof(QueueSchedulerService)
            .GetField("_stoppedWorkTask", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(scheduler));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);
            await scheduler.NotifyOwnedHaltAsync(second, Ct);
            if (change == "advice") await DaemonSettingsStore.SaveAsync(new DaemonSettings(), BatonPaths.SettingsFile, Ct);
            else
            {
                await fixture.CommandAsync("attach");
                if (change == "detach") await fixture.CommandAsync("detach");
            }
        }
        finally { release.TrySetResult(); }
        await pending.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        await scheduler.DrainStoppedWorkAdviceAsync();
        Assert.Equal(1, fixture.LegacyCalls);
        Assert.Equal(ConductorObligationStatus.Pending, (await fixture.Store.ReadAsync(fixture.Key(second.Tag), Ct))?.Status);
        Assert.False(File.Exists(Path.Combine(fixture.Store.GetStoppedWorkAdviceEvidenceDirectory(fixture.Key(second.Tag)), "launch.json")));
        if (change == "detach") await fixture.CommandAsync("attach");
        Assert.Equal("delivered", (await fixture.FollowAsync(second.Tag)).GetProperty("status").GetString());
        Assert.Single(fixture.Calls);
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

    [Theory]
    [InlineData("opt-in")]
    [InlineData("held")]
    [InlineData("detach")]
    [InlineData("claim")]
    [InlineData("trust")]
    [InlineData("request")]
    [InlineData("head")]
    public async Task Typed_admitted_action_revalidates_authority_before_registered_launch(string change)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.EnableAutomaticAsync();
        await fixture.CommandAsync("attach");
        fixture.Reply = "ReplaceReview";
        await fixture.HaltAsync("launch");
        var action = (await fixture.RowAsync("launch")).ReplacementReviewAction;
        Assert.NotNull(action);
        await fixture.ChangeAsync("launch", change);
        var launches = 0;
        await fixture.Scheduler(launch: (_, _) =>
        {
            launches++;
            return Task.FromResult(new QueueLaunchOutcome(null));
        }).TickOnceAsync(Ct);
        Assert.Equal(0, launches);
        var row = await fixture.RowAsync("launch");
        Assert.Equal(2, row.Round);
        Assert.Null(row.LaunchMayHaveBegunAt);
        Assert.Equal(action.EvidenceDigest, row.ReplacementReviewAction!.EvidenceDigest);
        Assert.Single(fixture.Calls);
    }

    [Theory]
    [InlineData("opt-in")]
    [InlineData("held")]
    [InlineData("detach")]
    [InlineData("claim")]
    [InlineData("trust")]
    [InlineData("head")]
    [InlineData("artifactless")]
    public async Task Typed_issued_completion_survives_live_policy_changes_but_revalidates_head_and_artifact(string change)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.EnableAutomaticAsync();
        await fixture.CommandAsync("attach");
        fixture.Reply = "ReplaceReview";
        var advancer = fixture.Advancer();
        using var scheduler = fixture.Scheduler(advancer, launch: (request, _) =>
            Task.FromResult(new QueueLaunchOutcome(request.RoomDirectory)));
        await fixture.HaltAsync("observe", scheduler: scheduler);
        await scheduler.TickOnceAsync(Ct);
        await fixture.CompleteReviewAsync("observe");
        var interrupted = false;
        advancer.ReplacementReviewAfterProofPersisted = () =>
        {
            interrupted = true;
            if (change == "artifactless")
                FileCleanup.EnsureDeleted(Path.Combine((fixture.RowAsync("observe").GetAwaiter().GetResult())
                    .ReplacementReviewAction!.ReplacementRoomDirectory!, "verdict.json"));
            else fixture.ChangeAsync("observe", change).GetAwaiter().GetResult();
            throw new IOException("Offline crash after proof commit");
        };
        await scheduler.TickOnceAsync(Ct);
        Assert.True(interrupted);
        var action = (await fixture.RowAsync("observe")).ReplacementReviewAction!;
        Assert.NotNull(action.CompletionProof);
        await scheduler.ReconcileReplacementReviewActionsAsync(Ct);
        Assert.Equal(change is "head" or "artifactless" ? ConductorObligationStatus.Pending : ConductorObligationStatus.ActionObserved,
            (await fixture.Store.ReadAsync(fixture.Key("observe"), Ct))!.Status);
        Assert.Equal(action.CompletionProof, (await fixture.RowAsync("observe")).ReplacementReviewAction!.CompletionProof);
        Assert.Single(fixture.Calls);
    }

    [Theory]
    [InlineData("artifactless")]
    [InlineData("incomplete")]
    [InlineData("stale-head")]
    [InlineData("wrong-attempt")]
    public async Task Typed_replacement_without_exact_independent_completion_never_observes_action(string kind)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.EnableAutomaticAsync();
        await fixture.CommandAsync("attach");
        fixture.Reply = "ReplaceReview";
        using var scheduler = fixture.Scheduler(launch: (request, _) => Task.FromResult(new QueueLaunchOutcome(request.RoomDirectory)));
        await fixture.HaltAsync("review", scheduler: scheduler);
        await scheduler.TickOnceAsync(Ct);
        await fixture.CompleteReviewAsync("review", kind);
        await scheduler.TickOnceAsync(Ct);
        await scheduler.ReconcileReplacementReviewActionsAsync(Ct);
        Assert.Null((await fixture.RowAsync("review")).ReplacementReviewAction!.CompletionProof);
        Assert.Equal(ConductorObligationStatus.Pending, (await fixture.Store.ReadAsync(fixture.Key("review"), Ct))!.Status);
        Assert.Single(fixture.Calls);
    }

    [Theory]
    [InlineData("missing-digest")]
    [InlineData("missing-directory")]
    [InlineData("legacy-digest")]
    [InlineData("missing-origin")]
    [InlineData("foreign-origin")]
    public async Task Follow_slot_with_missing_or_conflicting_provenance_never_falls_back_to_legacy(string change)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.EnableAutomaticAsync();
        await fixture.CommandAsync("attach");
        fixture.Reply = "ReplaceReview";
        await fixture.HaltAsync("provenance");
        await QueueStore.MutateAsync(BatonPaths.QueueFile, queue => queue with
        {
            Items = queue.Items.Select(item => item with
            {
                ReplacementReviewAction = change switch
                {
                    "missing-digest" => item.ReplacementReviewAction! with { EvidenceDigest = null },
                    "missing-directory" => item.ReplacementReviewAction! with { EvidenceDirectory = null },
                    "legacy-digest" => item.ReplacementReviewAction! with { AdviceDigest = item.ReplacementReviewAction.EvidenceDigest! },
                    "missing-origin" => item.ReplacementReviewAction! with { EvidenceProvenance = null },
                    _ => item.ReplacementReviewAction! with { EvidenceProvenance = "foreign" },
                },
            }).ToList(),
        }, Ct);
        var launches = 0;
        await fixture.Scheduler(launch: (_, _) =>
        {
            launches++;
            return Task.FromResult(new QueueLaunchOutcome(null));
        }).TickOnceAsync(Ct);
        Assert.Equal(0, launches);
        Assert.Equal(0, fixture.LegacyCalls);
        Assert.Single(fixture.Calls);
        Assert.Equal(2, (await fixture.RowAsync("provenance")).Round);
    }

    internal sealed class Fixture : IDisposable
    {
        private readonly IDisposable _scope;
        private readonly bool _ownsRoot;
        public string Root { get; }
        public string Workspace => Path.Combine(Root, "workspace");
        public string CeilingPath => Path.Combine(Root, "project-ceilings.json");
        public string RegistrationPath => Path.Combine(Root, "conductor-follow", Identity.FileSlug, "registration.json");
        private string RequestPath => Path.Combine(Root, "explicit-request.json");
        public List<CodexBrokerConfiguration> Calls { get; } = [];
        public bool Interrupt { get; set; }
        public string? Reply { get; set; }
        public string? NativeStream { get; set; }
        public Func<CancellationToken, Task>? WaitInBroker { get; set; }
        public int LegacyCalls { get; private set; }
        public ConductorObligationStore Store { get; }
        private FakeGh Gh { get; } = new();
        public string PullRequestState { set => Gh.State = value; }
        public Func<string, string>? PullRequestHeadForWorkspace { set => Gh.HeadForWorkspace = value; }
        public int RemoteCalls => Gh.Calls;

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

        public Task<ConductorFollowResult> FollowWithAdmissionAsync(string tag,
            Func<ConductorObligation, CancellationToken, Task> admission,
            Func<CancellationToken, Task>? beforeLaunchAdmission = null) => DeliverWithAdmissionAsync(
                Key(tag), admission, beforeLaunchAdmission);

        private async Task<ConductorFollowResult> DeliverWithAdmissionAsync(string key,
            Func<ConductorObligation, CancellationToken, Task> admission,
            Func<CancellationToken, Task>? beforeLaunchAdmission)
        {
            var session = await ConductorFollowSession.CreateAsync(RequestPath, Root,
                (_, _) => Task.FromResult<RepositoryIdentity?>(Identity), Ct, Broker);
            return await session.DeliverAsync(key, Ct, admission, beforeLaunchAdmission);
        }

        public string EventEvidencePath(string tag, string file)
        {
            foreach (var identityPath in Directory.EnumerateFiles(
                Path.Combine(Root, "conductor-follow"), "identity.json", SearchOption.AllDirectories))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(identityPath));
                if (document.RootElement.GetProperty("obligationKey").GetString() == Key(tag))
                    return Path.Combine(Path.GetDirectoryName(identityPath)!, file);
            }

            throw new InvalidOperationException($"No event evidence for {tag}.");
        }

        public QueueSchedulerService Scheduler(WorkItemAdvancer? advancer = null,
            Func<TimeSpan, CancellationToken, Task>? delay = null,
            Func<ConductorObligation, StoppedWorkAdviceRequest, StoppedWorkAdviceContext,
                string, CancellationToken, Task<RetainedStoppedWorkAdviceResponse>>? legacy = null,
            Func<QueueLaunchRequest, CancellationToken, Task<QueueLaunchOutcome>>? launch = null,
            Func<CancellationToken, Task>? beforeLaunchClaim = null) => new(
            launch ?? ((_, _) => Task.FromResult(new QueueLaunchOutcome(null))),
            _ => Task.FromResult(0d),
            () => 16d,
            () => DateTimeOffset.UtcNow,
            advancer: advancer ?? Advancer(),
            beforeLaunchClaim: beforeLaunchClaim,
            conductorObligations: Store,
            adopt: _ => Task.FromResult<IReadOnlyList<QueueLaneAdoption>>([]),
            loopDriver: new DaemonLoopDriver(delay: delay),
            stoppedWorkAdvice: (obligation, request, context, directory, token) =>
            {
                LegacyCalls++;
                if (legacy is not null) return legacy(obligation, request, context, directory, token);
                throw new InvalidOperationException("Offline control: legacy fallback must not launch.");
            })
            {
                FollowBroker = Broker,
                FollowRepositoryResolver = (_, _) => Task.FromResult<RepositoryIdentity?>(Identity),
            };

        public WorkItemAdvancer Advancer(Func<string, CancellationToken, Task<string?>>? workspaceHead = null) =>
            new(Gh, workspaceHead ?? ((_, _) => Task.FromResult<string?>(Gh.HeadSha)));

        public async Task<QueueItem> RowAsync(string tag) =>
            (await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items.Single(item => item.Tag == tag);

        public Task EnableAutomaticAsync() => DaemonSettingsStore.SaveAsync(new DaemonSettings
        {
            Queue = new QueueSettings
            {
                AutomaticMissingVerdictReplacementReview = new Dictionary<string, JsonElement>
                {
                    [Repository] = JsonSerializer.SerializeToElement(true),
                },
            },
        }, BatonPaths.SettingsFile, Ct);

        public async Task ChangeAsync(string tag, string change)
        {
            if (change == "opt-in") await DaemonSettingsStore.SaveAsync(new(), BatonPaths.SettingsFile, Ct);
            else if (change == "held") await QueueStore.MutateAsync(BatonPaths.QueueFile, queue => queue with { Held = true }, Ct);
            else if (change == "detach") await CommandAsync("detach");
            else if (change == "claim") await ConductorClaimStore.TakeoverAsync(Identity, "other", "fixture", Root, cancellationToken: Ct);
            else if (change == "trust") ProjectCeilingStore.Revoke(Workspace, CeilingPath);
            else if (change == "head") Gh.HeadSha = new string('f', 40);
            else if (change == "request")
            {
                var path = Path.GetFullPath(Path.Combine(EventEvidencePath(tag, "identity.json"), "..", "..", "..", "request.json"));
                var request = JsonNode.Parse(File.ReadAllText(path))!;
                request["initialInstructions"] = "changed";
                File.WriteAllText(path, request.ToJsonString());
            }
            else if (change is "generation" or "thread" or "context" or "repository")
            {
                var path = EventEvidencePath(tag, "identity.json");
                var identity = JsonNode.Parse(File.ReadAllText(path))!;
                identity[change switch
                {
                    "generation" => "claimGeneration",
                    "thread" => "sessionId",
                    "context" => "contextSha256",
                    _ => "repository",
                }] = "foreign";
                File.WriteAllText(path, identity.ToJsonString());
            }
            else if (change == "source-attempt")
            {
                var path = EventEvidencePath(tag, "source.json");
                var source = JsonNode.Parse(File.ReadAllText(path))!;
                source["context"]!["attemptId"] = JsonSerializer.SerializeToNode(new FleetAttemptId("foreign"));
                File.WriteAllText(path, source.ToJsonString());
            }
            else if (change == "receipt") File.WriteAllText(EventEvidencePath(tag, "receipt.txt"), "foreign");
            else if (change != "ineligible-halt") throw new InvalidOperationException(change);
        }

        public async Task CompleteReviewAsync(string tag, string kind = "complete")
        {
            var row = await RowAsync(tag);
            var room = row.RoomDirectory!;
            Directory.CreateDirectory(room);
            var verdict = Path.Combine(room, "verdict.json");
            File.WriteAllText(verdict, JsonSerializer.Serialize(new
            {
                reviewedRef = kind == "stale-head" ? new string('f', 40) : Head,
                completion = kind == "incomplete" ? "incomplete" : "complete",
                decision = "approve",
                summary = "Nothing blocking.",
                findings = Array.Empty<object>(),
            }));
            await TerminalSentinelWriter.WriteAsync(room,
                new(WorkflowOutcome.Succeeded, [], kind == "artifactless" ? [] : [verdict], null), Ct);
            await QueueStore.MutateAsync(BatonPaths.QueueFile, queue => queue with
            {
                Items = queue.Items.Select(item => item.Tag == tag ? item with
                {
                    AttemptId = kind == "wrong-attempt" ? new FleetAttemptId("foreign") : item.AttemptId,
                } : item).ToList(),
            }, Ct);
        }

        public async Task<QueueItem> HaltAsync(string tag, bool notify = true, QueueSchedulerService? scheduler = null,
            bool owned = true, string? workspace = null)
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
                ScopeClass = "engine",
                DeclaredTaskSize = TaskSizeDeclaration.Parse("small", "One bounded review recovery"),
                Round = 1,
                Workspace = workspace ?? Workspace,
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
                OwnedTask = owned ? new("task-" + tag, Repository, 2632, "digest", "holder", DateTimeOffset.UtcNow) : null,
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
            if (WaitInBroker is not null) await WaitInBroker(token);
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
            await QueueStore.MutateAsync(BatonPaths.QueueFile, queue => queue, token).WaitAsync(TimeSpan.FromSeconds(60), token);
            if (Interrupt)
            {
                await started!("retained-thread", token);
                throw new IOException("Offline interruption after native identity");
            }
            using var nativeInput = new StringWriter();
            var reply = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                obligationKey = Reply == "foreign-key" ? Key("foreign") : key,
                obligationId = Reply == "foreign-id" ? "foreign" : (await Store.ReadAsync(key, token))!.ObligationId,
                sourceHeadSha = Reply == "foreign-head" ? new string('f', 40) : Head,
                decision = Reply == "Hold" ? "Hold" : "ReplaceReview",
            });
            reply = Reply switch
            {
                "prose" => "Please " + reply,
                "duplicate" => reply.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal),
                "unknown" => reply.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"unknown\":true", StringComparison.Ordinal),
                "alias" => reply.Replace("obligationKey", "ObligationKey", StringComparison.Ordinal),
                "overlong" => new string(' ', 17000) + reply,
                _ => reply,
            };
            var message = Reply is null ? string.Empty : JsonSerializer.Serialize(new
            {
                method = "item/completed",
                @params = new { item = new { type = "agentMessage", text = reply } },
            }) + "\n";
            var usage = Reply is null ? string.Empty : "{\"method\":\"thread/tokenUsage/updated\",\"params\":{\"tokenUsage\":{\"last\":{\"inputTokens\":1,\"outputTokens\":1}}}}\n";
            using var nativeOutput = new StringReader("{\"id\":1,\"result\":{}}\n"
                + "{\"id\":2,\"result\":{\"thread\":{\"id\":\"retained-thread\"}}}\n"
                + "{\"id\":3,\"result\":{\"turn\":{\"id\":\"turn\",\"status\":\"inProgress\"}}}\n"
                + message + usage
                + "{\"method\":\"turn/completed\",\"params\":{\"turn\":{\"status\":\"completed\"}}}\n");
            using var captured = new StringWriter();
            var result = await CodexAppServerBroker.RunProtocolAsync(configuration, prompt,
                CodexAppServerBroker.CreateDynamicToolPolicy(configuration, directory, inputs, null), null,
                nativeInput, nativeOutput, captured, error, token, threadStarted: started);
            foreach (var line in captured.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (NativeStream == "usage-only" && line.Contains("turn.completed", StringComparison.Ordinal)) continue;
                await output.WriteLineAsync(NativeStream == "foreign-thread"
                    ? line.Replace("retained-thread", "foreign-thread", StringComparison.Ordinal) : line);
            }
            if (NativeStream == "error") await output.WriteLineAsync("{\"type\":\"error\",\"message\":\"failed\"}");
            if (NativeStream == "repeated-turn") await output.WriteLineAsync("{\"type\":\"turn.started\"}");
            if (NativeStream == "repeated-completion") await output.WriteLineAsync("{\"type\":\"turn.completed\"}");
            if (NativeStream == "completion-then-usage") await output.WriteLineAsync("{\"type\":\"turn.usage\",\"usage\":{}}");
            if (NativeStream == "post-completion-message") await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                type = "item.completed",
                item = new { type = "agent_message", text = reply },
            }));
            Assert.Contains(configuration.ResumeSession ? "thread/resume" : "thread/start", nativeInput.ToString(), StringComparison.Ordinal);
            Assert.Contains("\"threadId\":\"retained-thread\"", nativeInput.ToString(), StringComparison.Ordinal);
            return result;
        };

        public void Dispose() { _scope.Dispose(); if (_ownsRoot) DirectoryCleanup.DeleteRecursively(Root); }
    }

    private sealed class FakeGh : IGhCliRunner
    {
        public string HeadSha { get; set; } = Head;
        public Func<string, string>? HeadForWorkspace { get; set; }
        public string State { get; set; } = "OPEN";
        public int Calls { get; private set; }
        public Task<GhCliResult> RunAsync(string workspace, IReadOnlyList<string> args, CancellationToken cancellationToken)
        {
            Calls++;
            var head = HeadForWorkspace?.Invoke(workspace) ?? HeadSha;
            if (args is ["api", ..]) return Task.FromResult(RequiredCheckFixture.Read(args, Repository, HeadSha,
                new GhCliResult(true, 0, "[{\"name\":\"ci\",\"bucket\":\"pass\"}]", "")));
            if (args is ["pr", "checks", ..]) return Task.FromResult(new GhCliResult(true, 0,
                "[{\"name\":\"ci\",\"bucket\":\"pass\",\"state\":\"SUCCESS\"}]", ""));
            var pr = "{\"number\":77,\"state\":\"" + State + "\",\"isDraft\":true,\"headRefOid\":\"" + head
                + "\",\"headRefName\":\"2632-lane\",\"baseRefName\":\"main\",\"isCrossRepository\":false,\"statusCheckRollup\":[]}";
            return Task.FromResult(new GhCliResult(true, 0, args is ["pr", "view", ..] ? pr : "[" + pr + "]", ""));
        }
    }
}
