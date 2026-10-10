using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Baton.Accounting;
using Baton.Cli.Daemon;
using Baton.Conductor;
using Baton.Queue;
using Baton.Status;
using Baton.Tests.Shared;

namespace Baton.Cli.Tests;

public sealed partial class ConductorFollowDeliveryTests
{
    private static string HoldBody(JsonElement row, string operation, string id)
    {
        var body = JsonNode.Parse(ControlBody(row, operation, id))!.AsObject();
        body["expectedControlRevision"] = row.GetProperty("controlRevision").GetInt64();
        return body.ToJsonString();
    }

    private static async Task ApplyHoldAsync(GlassFixture glass, string operation, string id)
    {
        using var response = await glass.ControlAsync(operation, HoldBody(await glass.StatusAsync(), operation, id));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(operation, result.RootElement.GetProperty("receipt").GetProperty("operation").GetString());
        Assert.Equal("operator@example.test", result.RootElement.GetProperty("receipt").GetProperty("issuer").GetString());
    }

    [Theory]
    [InlineData("hold", null)]
    [InlineData("hold", "foreign@example.test")]
    [InlineData("unhold", null)]
    [InlineData("unhold", "operator@example.test, operator@example.test")]
    public async Task Issue2662_Glass_Hold_requires_server_authentication(string operation, string? login)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        await using var glass = await GlassFixture.StartAsync(fixture);
        var path = Path.Combine(fixture.Root, Identity.FileSlug, BatonPaths.ConductorClaimFileName);
        var before = File.ReadAllBytes(path);
        using var response = await glass.ControlAsync(operation, HoldBody(await glass.StatusAsync(), operation, "denied"), login);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Empty(fixture.Calls);
    }

    [Fact]
    public async Task Issue2662_Glass_Hold_receipts_replay_across_restart_without_clearing_a_newer_Hold()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        await using var glass = await GlassFixture.StartAsync(fixture);
        var registration = File.ReadAllBytes(fixture.RegistrationPath);
        await ApplyHoldAsync(glass, "hold", "A");
        var heldA = await glass.StatusAsync();
        var unholdB = HoldBody(heldA, "unhold", "B");
        using (var response = await glass.ControlAsync("unhold", unholdB)) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await ApplyHoldAsync(glass, "hold", "C");
        var claimPath = Path.Combine(fixture.Root, Identity.FileSlug, BatonPaths.ConductorClaimFileName);
        var heldC = File.ReadAllBytes(claimPath);
        await using (var restarted = await GlassFixture.StartAsync(fixture))
        {
            using var duplicate = await restarted.ControlAsync("unhold", unholdB);
            Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
            using var stale = await restarted.ControlAsync("unhold", HoldBody(heldA, "unhold", "fresh-stale-A"));
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            var changed = JsonNode.Parse(unholdB)!.AsObject();
            changed["reason"] = "Changed input";
            using var conflict = await restarted.ControlAsync("unhold", changed.ToJsonString());
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            var row = await restarted.StatusAsync();
            Assert.True(row.GetProperty("held").GetBoolean());
            Assert.Equal(3, row.GetProperty("controlRevision").GetInt64());
            Assert.Equal(3, row.GetProperty("controls").GetArrayLength());
        }
        Assert.Equal(heldC, File.ReadAllBytes(claimPath));
        Assert.Equal(registration, File.ReadAllBytes(fixture.RegistrationPath));
        var same = await ConductorClaimStore.ClaimAsync(Identity, "holder", fixture.Root, cancellationToken: Ct);
        Assert.True(same.Held);
        Assert.False(ConductorClaimStore.IsCurrentHostedAuthority(same, "holder", ConductorClaimStore.GetClaimGeneration(same)));
        Assert.Empty(fixture.Calls);
    }

    [Theory]
    [InlineData("missing-revision")]
    [InlineData("issuer")]
    [InlineData("stale-attachment")]
    [InlineData("blank-reason")]
    public async Task Issue2662_Glass_Hold_refuses_incomplete_or_stale_inputs(string change)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        await using var glass = await GlassFixture.StartAsync(fixture);
        var body = JsonNode.Parse(HoldBody(await glass.StatusAsync(), "hold", "invalid"))!.AsObject();
        if (change == "missing-revision") body.Remove("expectedControlRevision");
        if (change == "issuer") body["issuer"] = "client@example.test";
        if (change == "stale-attachment") body["attachmentId"] = new string('f', 32);
        if (change == "blank-reason") body["reason"] = " ";
        using var response = await glass.ControlAsync("hold", body.ToJsonString());
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.False((await glass.StatusAsync()).GetProperty("held").GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Issue2662_Glass_Unhold_continues_real_events_arriving_during_Hold_after_restart(bool notificationGap)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.EnableAutomaticAsync();
        await fixture.CommandAsync("attach");
        fixture.Reply = "ReplaceReview";
        await using var glass = await GlassFixture.StartAsync(fixture);
        await ApplyHoldAsync(glass, "hold", "hold-event");
        var held = await fixture.HaltAsync("during-hold", notify: !notificationGap);
        Assert.NotNull(held.StoppedWorkJudgment!.FollowAttachmentId);
        Assert.True(held.StoppedWorkJudgment.FollowContinuationPending);
        if (!notificationGap)
        {
            Assert.Equal("Hosted acquisition is Held.", held.StoppedWorkJudgment.FollowContinuationWait);
            Assert.Equal("Unhold this acquisition; next scheduler reconciliation rechecks the retained event.", held.StoppedWorkJudgment.FollowContinuationTrigger);
        }
        Assert.Empty(fixture.Calls);
        Assert.Empty(Directory.EnumerateFiles(SessionDirectory(fixture), "launch.json", SearchOption.AllDirectories));
        var generation = (await glass.StatusAsync()).GetProperty("claimGeneration").GetString();
        await ApplyHoldAsync(glass, "unhold", "unhold-event");
        var launches = 0;
        using var restarted = fixture.Scheduler(launch: (request, _) =>
        {
            launches++;
            return Task.FromResult(new QueueLaunchOutcome(request.RoomDirectory));
        });
        // Exercise the normal tick's durable pending selection, not a manual follow invocation.
        await restarted.TickOnceAsync(Ct);
        await WaitForHeldContinuationAsync(fixture, "during-hold");
        await restarted.TickOnceAsync(Ct);
        Assert.Single(fixture.Calls);
        Assert.Equal(1, launches);
        var row = await fixture.RowAsync("during-hold");
        Assert.Equal(held.Round + 1, row.Round);
        Assert.Equal(held.AutomaticFixUsed, row.AutomaticFixUsed);
        Assert.Equal(generation, row.ReplacementReviewAction!.IssuedAuthority!.ClaimGeneration);
        Assert.False(row.StoppedWorkJudgment!.FollowContinuationPending);
        await restarted.TickOnceAsync(Ct);
        Assert.Equal(1, launches);
    }

    private static async Task WaitForHeldContinuationAsync(Fixture fixture, string tag)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(1));
        while ((await fixture.RowAsync(tag)).ReplacementReviewAction is null)
            await Task.Delay(10, timeout.Token); // wait-ok: Poll fixture completion within the independent one-minute deadline.
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("response-before-decision")]
    [InlineData("decision-before-journal")]
    public async Task Issue2662_Glass_Unhold_admits_saved_complete_response_without_another_model_call(string seam)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.EnableAutomaticAsync();
        await fixture.CommandAsync("attach");
        fixture.Reply = "ReplaceReview";
        await fixture.HaltAsync("saved-held", notify: false);
        await fixture.Scheduler().ReconcileStoppedWorkAdviceAsync(Ct);
        Assert.Equal("delivered", (await fixture.FollowAsync("saved-held")).GetProperty("status").GetString());
        var receipt = fixture.Receipt("saved-held");
        if (seam == "response-before-decision") FileCleanup.EnsureDeleted(fixture.EventEvidencePath("saved-held", "decision.json"));
        if (seam != "complete") FileCleanup.EnsureDeleted(Path.Combine(SessionDirectory(fixture), "delivery.jsonl"));
        await using var glass = await GlassFixture.StartAsync(fixture);
        await ApplyHoldAsync(glass, "hold", "saved-hold");
        using (var heldScheduler = fixture.Scheduler()) await heldScheduler.RecoverAttachedFollowAsync(Ct);
        Assert.Null((await fixture.RowAsync("saved-held")).ReplacementReviewAction);
        await ApplyHoldAsync(glass, "unhold", "saved-unhold");
        using var restarted = fixture.Scheduler();
        await restarted.RecoverAttachedFollowAsync(Ct);
        Assert.Single(fixture.Calls);
        Assert.Equal(receipt, fixture.Receipt("saved-held"));
        Assert.NotNull((await fixture.RowAsync("saved-held")).ReplacementReviewAction);
        Assert.False((await fixture.RowAsync("saved-held")).StoppedWorkJudgment!.FollowContinuationPending);
    }

    [Fact]
    public async Task Issue2662_Glass_Hold_wins_final_replacement_marker_then_Unhold_uses_the_reserved_slot()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.EnableAutomaticAsync();
        await fixture.CommandAsync("attach");
        fixture.Reply = "ReplaceReview";
        await AdmitControlFollowAsync(fixture, "slot");
        var admitted = await fixture.RowAsync("slot");
        await using var glass = await GlassFixture.StartAsync(fixture);
        await ApplyHoldAsync(glass, "hold", "previous-marker-hold");
        using (var preparing = fixture.Scheduler()) await preparing.ReconcileReplacementReviewActionsAsync(Ct);
        await ApplyHoldAsync(glass, "unhold", "previous-marker-unhold");
        var launches = 0;
        var heldOnce = false;
        using var scheduler = fixture.Scheduler(launch: (request, _) =>
        {
            launches++;
            return Task.FromResult(new QueueLaunchOutcome(request.RoomDirectory));
        });
        scheduler.ReplacementReviewBeforeLaunchMarker = async _ =>
        {
            if (heldOnce) return;
            heldOnce = true;
            await ApplyHoldAsync(glass, "hold", "marker-hold");
        };
        await scheduler.TickOnceAsync(Ct);
        var pending = await fixture.RowAsync("slot");
        Assert.Equal(0, launches);
        Assert.True(pending.ReplacementReviewAction!.HeldPending);
        Assert.Equal(admitted.ReplacementReviewAction!.NextTrigger, pending.ReplacementReviewAction.NextTrigger);
        var displayed = (await glass.StatusAsync()).GetProperty("actions").EnumerateArray()
            .Single(action => action.GetProperty("tag").GetString() == "slot");
        Assert.Equal("Unhold this acquisition; the scheduler rechecks source, head, grants, opt-in and runway before launch.",
            displayed.GetProperty("nextTrigger").GetString());
        Assert.Null(pending.ReplacementReviewAction.BlockedReason);
        Assert.Null(pending.LaunchMayHaveBegunAt);
        Assert.Equal(admitted.Round, pending.Round);
        Assert.NotNull(pending.AttemptEnvelope);
        Assert.NotNull(await Record.ExceptionAsync(() => ReplacementReviewConductorCommand.ExecuteAsync(
            ConductorOptionsParser.Parse(["act", "--obligation", pending.ReplacementReviewAction.ObligationKey,
                "--holder", "holder", "--action", "replace-review", "--expected-head", Head]), TextWriter.Null,
            fixture.Root, fixture.Advancer(), fixture.Store, Ct)));
        await ApplyHoldAsync(glass, "unhold", "marker-unhold");
        await scheduler.TickOnceAsync(Ct);
        await scheduler.TickOnceAsync(Ct);
        var issued = await fixture.RowAsync("slot");
        Assert.Equal(1, launches);
        Assert.Equal(admitted.Round, issued.Round);
        Assert.Equal(pending.AttemptEnvelope!.AttemptId, issued.AttemptId);
        Assert.Equal(admitted.ReplacementReviewAction!.EvidenceDigest, issued.ReplacementReviewAction!.EvidenceDigest);
        Assert.Single(fixture.Calls);
        await fixture.CompleteReviewAsync("slot");
        await ApplyHoldAsync(glass, "hold", "issued-hold");
        await scheduler.TickOnceAsync(Ct);
        var complete = await fixture.RowAsync("slot");
        Assert.NotNull(complete.ReplacementReviewAction!.CompletionProof);
        Assert.NotNull(complete.ReplacementReviewAction.ActionObservedAt);
        var obligation = await fixture.Store.ReadAsync(fixture.Key("slot"), Ct);
        Assert.Equal(ConductorObligationStatus.ActionObserved, obligation!.Status);
        var observed = complete.ReplacementReviewAction.ActionObservedAt;
        await scheduler.TickOnceAsync(Ct);
        Assert.Equal(observed, (await fixture.RowAsync("slot")).ReplacementReviewAction!.ActionObservedAt);
        Assert.Equal(1, launches);
    }

    [Fact]
    public async Task Issue2662_Glass_Hold_wins_follow_marker_without_waiting_for_the_conversation()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        fixture.Reply = "Hold";
        await fixture.HaltAsync("warm-held");
        await fixture.HaltAsync("marker-held", notify: false);
        await fixture.Scheduler().ReconcileStoppedWorkAdviceAsync(Ct);
        await using var glass = await GlassFixture.StartAsync(fixture);
        var turn = await fixture.FollowWithAdmissionAsync("marker-held", (_, _) => Task.CompletedTask,
            async _ => await ApplyHoldAsync(glass, "hold", "follow-marker-hold"));
        Assert.Equal("held-pending", turn.Status);
        Assert.Equal("Hosted acquisition is Held; Unhold continues this pending event without resetting its allowance.", turn.Diagnostic);
        Assert.Single(fixture.Calls);
        Assert.False(File.Exists(fixture.EventEvidencePath("marker-held", "launch.json")));
        await ApplyHoldAsync(glass, "unhold", "follow-marker-unhold");
        using var restarted = fixture.Scheduler();
        await restarted.RecoverAttachedFollowAsync(Ct);
        Assert.Equal(2, fixture.Calls.Count);
        Assert.True(fixture.Calls[1].ResumeSession);
        Assert.Equal(fixture.Calls[0].Model, fixture.Calls[1].Model);
    }

    [Fact]
    public async Task Issue2662_Glass_Hold_after_follow_admission_retains_late_response_then_Unhold_admits_it()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.EnableAutomaticAsync();
        await fixture.CommandAsync("attach");
        fixture.Reply = "ReplaceReview";
        await using var glass = await GlassFixture.StartAsync(fixture);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.WaitInBroker = async token => { entered.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromMinutes(1), token); };
        var halt = fixture.HaltAsync("late-held");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromMinutes(1), Ct);
            await ApplyHoldAsync(glass, "hold", "late-hold");
            Assert.False(halt.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await halt.WaitAsync(TimeSpan.FromMinutes(1), Ct);
        Assert.NotNull(fixture.Receipt("late-held"));
        Assert.NotNull(ConductorFollowSession.ReadDecisionEvidence(Path.GetDirectoryName(fixture.EventEvidencePath("late-held", "decision.json"))!));
        Assert.Null((await fixture.RowAsync("late-held")).ReplacementReviewAction);
        Assert.Equal("Complete response retained; hosted action admission is Held.", (await fixture.RowAsync("late-held")).StoppedWorkJudgment!.FollowContinuationWait);
        Assert.Equal("Unhold this acquisition; next scheduler reconciliation admits the saved decision without another model call.", (await fixture.RowAsync("late-held")).StoppedWorkJudgment!.FollowContinuationTrigger);
        await ApplyHoldAsync(glass, "unhold", "late-unhold");
        using var restarted = fixture.Scheduler();
        await restarted.RecoverAttachedFollowAsync(Ct);
        Assert.Single(fixture.Calls);
        Assert.NotNull((await fixture.RowAsync("late-held")).ReplacementReviewAction);
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("takeover")]
    public async Task Issue2662_Glass_Stop_or_Take_Over_supersedes_Hold_and_stale_Unhold_never_resurrects_it(string operation)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        await using var glass = await GlassFixture.StartAsync(fixture);
        await ApplyHoldAsync(glass, "hold", "superseded-hold");
        var unhold = HoldBody(await glass.StatusAsync(), "unhold", "superseded-unhold");
        using (var response = await glass.ControlAsync(operation, ControlBody(await glass.StatusAsync(), operation, "terminal")))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var refused = await glass.ControlAsync("unhold", unhold);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var row = await glass.StatusAsync();
        Assert.False(row.GetProperty("held").GetBoolean());
        Assert.False(row.GetProperty("unholdEligible").GetBoolean());
        Assert.Empty(fixture.Calls);
    }

    [Theory]
    [InlineData("held")]
    [InlineData("opt-in")]
    [InlineData("detach")]
    [InlineData("trust")]
    [InlineData("head")]
    public async Task Issue2662_Unhold_keeps_independent_action_blockers_and_allowance(string blocker)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.EnableAutomaticAsync();
        await fixture.CommandAsync("attach");
        fixture.Reply = "ReplaceReview";
        await AdmitControlFollowAsync(fixture, "blocked-held");
        var original = await fixture.RowAsync("blocked-held");
        await using var glass = await GlassFixture.StartAsync(fixture);
        await ApplyHoldAsync(glass, "hold", "blocker-hold");
        using var scheduler = fixture.Scheduler(launch: (request, _) => Task.FromResult(new QueueLaunchOutcome(request.RoomDirectory)));
        await scheduler.ReconcileReplacementReviewActionsAsync(Ct);
        if (blocker == "detach")
        {
            using var detached = await glass.DetachAsync(DetachBody(await glass.StatusAsync()));
            Assert.Equal(HttpStatusCode.OK, detached.StatusCode);
        }
        else await fixture.ChangeAsync("blocked-held", blocker);
        await ApplyHoldAsync(glass, "unhold", "blocker-unhold");
        await scheduler.TickOnceAsync(Ct);
        await scheduler.TickOnceAsync(Ct);
        var blocked = await fixture.RowAsync("blocked-held");
        Assert.Null(blocked.LaunchMayHaveBegunAt);
        Assert.Null(blocked.ReplacementReviewAction!.ReplacementAttemptId);
        Assert.Equal(original.Round, blocked.Round);
        Assert.Equal(original.AutomaticFixUsed, blocked.AutomaticFixUsed);
        Assert.Single(fixture.Calls);
        if (blocker == "held") Assert.True((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Held);
        if (blocker == "opt-in")
        {
            Assert.NotNull(blocked.ReplacementReviewAction.PausedReason);
            await fixture.EnableAutomaticAsync();
            await scheduler.TickOnceAsync(Ct);
            Assert.NotNull((await fixture.RowAsync("blocked-held")).ReplacementReviewAction!.ReplacementAttemptId);
            Assert.Equal(original.Round, (await fixture.RowAsync("blocked-held")).Round);
        }
    }

    [Fact]
    public async Task Issue2662_Hold_does_not_interfere_with_an_independent_ordinary_task()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.EnableAutomaticAsync();
        await fixture.CommandAsync("attach");
        fixture.Reply = "ReplaceReview";
        await AdmitControlFollowAsync(fixture, "hosted-held");
        var hosted = await fixture.RowAsync("hosted-held");
        var ordinary = hosted with
        {
            Tag = "ordinary",
            Stage = WorkStage.Implement,
            Role = "implement",
            Round = 0,
            OwnedTask = null,
            StoppedWorkJudgment = null,
            ReplacementReviewAction = null,
            AutomaticFixUsed = false,
            ParentAttemptId = null,
            Workspace = Path.Combine(fixture.Root, "ordinary-workspace"),
            SpecFile = Path.Combine(fixture.Root, "ordinary.md"),
            PullRequest = null,
            Requirements = null,
        };
        Directory.CreateDirectory(ordinary.Workspace);
        File.WriteAllText(ordinary.SpecFile, "Independent ordinary fixture task.");
        await QueueStore.MutateAsync(BatonPaths.QueueFile, queue => queue with { Items = [.. queue.Items, ordinary] }, Ct);
        await using var glass = await GlassFixture.StartAsync(fixture);
        await ApplyHoldAsync(glass, "hold", "independent-hold");
        var launched = new List<string>();
        using var scheduler = fixture.Scheduler(launch: (request, _) =>
        {
            launched.Add(request.Item.Tag);
            return Task.FromResult(new QueueLaunchOutcome(request.RoomDirectory));
        });
        await scheduler.TickOnceAsync(Ct);
        Assert.True(launched.Count == 1, JsonSerializer.Serialize(await fixture.RowAsync("ordinary")));
        Assert.Equal(["ordinary"], launched);
        Assert.True((await fixture.RowAsync("hosted-held")).ReplacementReviewAction!.HeldPending);
        Assert.Equal(hosted.Round, (await fixture.RowAsync("hosted-held")).Round);
        Assert.Single(fixture.Calls);
    }

    [Fact]
    public async Task Issue2662_Hold_lost_HTTP_reply_is_reconciled_from_persisted_receipt_and_current_state()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        await using var glass = await GlassFixture.StartAsync(fixture);
        var body = HoldBody(await glass.StatusAsync(), "hold", "lost-held");
        glass.DropControlAcknowledgement();
        var unknown = await Record.ExceptionAsync(async () =>
        {
            using var response = await glass.ControlAsync("hold", body);
            Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        });
        Assert.True(unknown is null or HttpRequestException or OperationCanceledException);
        await using var restarted = await GlassFixture.StartAsync(fixture);
        var current = await restarted.StatusAsync();
        Assert.True(current.GetProperty("held").GetBoolean());
        Assert.Equal("lost-held", Assert.Single(current.GetProperty("controls").EnumerateArray()).GetProperty("receipt").GetProperty("request").GetProperty("requestId").GetString());
        var path = Path.Combine(fixture.Root, Identity.FileSlug, BatonPaths.ConductorClaimFileName);
        var bytes = File.ReadAllBytes(path);
        using var duplicate = await restarted.ControlAsync("hold", body);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("claim")]
    [InlineData("registration")]
    [InlineData("session")]
    [InlineData("request")]
    public async Task Issue2662_Hold_corrupt_authority_refuses_before_receipt(string target)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        await using var glass = await GlassFixture.StartAsync(fixture);
        var body = HoldBody(await glass.StatusAsync(), "hold", "corrupt-held");
        var claimPath = Path.Combine(fixture.Root, Identity.FileSlug, BatonPaths.ConductorClaimFileName);
        var path = target == "claim" ? claimPath : target == "registration" ? fixture.RegistrationPath
            : Path.Combine(SessionDirectory(fixture), target + ".json");
        File.WriteAllText(path, "{}");
        var bytes = File.ReadAllBytes(claimPath);
        using var response = await glass.ControlAsync("hold", body);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(bytes, File.ReadAllBytes(claimPath));
        Assert.Empty(fixture.Calls);
    }

    [Fact]
    public async Task Issue2662_Hold_queue_lock_refusal_is_bounded_and_preserves_claim()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        await using var glass = await GlassFixture.StartAsync(fixture);
        var body = HoldBody(await glass.StatusAsync(), "hold", "busy-held");
        var claimPath = Path.Combine(fixture.Root, Identity.FileSlug, BatonPaths.ConductorClaimFileName);
        var bytes = File.ReadAllBytes(claimPath);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = Task.Run(() => MutexGuardedFileLock.RunUnderLock(BatonPaths.QueueFile, QueueStore.LockNamePrefix,
            TimeSpan.FromSeconds(1), () =>
            {
                entered.TrySetResult();
                release.Task.WaitAsync(TimeSpan.FromMinutes(1), Ct).GetAwaiter().GetResult();
                return true;
            }), Ct);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromMinutes(1), Ct);
            using var response = await glass.ControlAsync("hold", body).WaitAsync(TimeSpan.FromSeconds(5), Ct); // wait-ok: Verify the authenticated control's bounded busy-lock refusal.
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }
        finally { release.TrySetResult(); await blocker; }
        Assert.Equal(bytes, File.ReadAllBytes(claimPath));
    }

    [Fact]
    public async Task Issue2662_Hold_and_Unhold_survive_failed_delivery_projection_without_registration_cleanup()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        await using var healthy = await GlassFixture.StartAsync(fixture);
        var displayed = await healthy.StatusAsync();
        var registration = File.ReadAllBytes(fixture.RegistrationPath);
        await using var unavailable = await GlassFixture.StartAsync(fixture, (_, _) => Task.FromResult<RepositoryIdentity?>(null));
        Assert.Equal("unavailable", (await unavailable.StatusAsync()).GetProperty("state").GetString());
        ConductorFollowSession.AfterHostedControlFence = () => throw new IOException("Hold must not invoke terminal registration cleanup");
        try
        {
            using var held = await unavailable.ControlAsync("hold", HoldBody(displayed, "hold", "projection-hold"));
            Assert.Equal(HttpStatusCode.OK, held.StatusCode);
            var current = await unavailable.StatusAsync();
            Assert.True(current.GetProperty("held").GetBoolean());
            Assert.Equal("unavailable", current.GetProperty("state").GetString());
            using var unheld = await unavailable.ControlAsync("unhold", HoldBody(current, "unhold", "projection-unhold"));
            Assert.Equal(HttpStatusCode.OK, unheld.StatusCode);
            await ConductorFollowSession.ReconcileHostedControlCleanupAsync(fixture.Root, Ct);
        }
        finally { ConductorFollowSession.AfterHostedControlFence = null; }
        Assert.Equal(registration, File.ReadAllBytes(fixture.RegistrationPath));
        Assert.False((await healthy.StatusAsync()).GetProperty("held").GetBoolean());
        Assert.Empty(fixture.Calls);
    }
}
