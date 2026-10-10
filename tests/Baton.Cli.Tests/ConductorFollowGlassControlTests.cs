using System.Net;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
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
    private static string ControlBody(JsonElement status, string verb, string requestId = "operator-request")
    {
        var body = JsonNode.Parse(DetachBody(status))!.AsObject();
        body["requestId"] = requestId;
        body["reason"] = "Operator takes responsibility";
        if (verb == "takeover")
        {
            body["destinationHolder"] = "successor";
            body["destinationAddress"] = "Phone operator <owner@example.test>";
        }
        return body.ToJsonString();
    }

    private static async Task AdmitControlFollowAsync(Fixture fixture, string tag)
    {
        await fixture.HaltAsync(tag, notify: false);
        using var scheduler = fixture.Scheduler();
        await scheduler.ReconcileStoppedWorkAdviceAsync(Ct);
        Assert.Equal("delivered", (await fixture.FollowAsync(tag)).GetProperty("status").GetString());
        await scheduler.RecoverAttachedFollowAsync(Ct);
        var row = await fixture.RowAsync(tag);
        Assert.Equal(QueueItemState.Queued, row.State);
        Assert.NotNull(row.ReplacementReviewAction);
        Assert.Null(row.LaunchMayHaveBegunAt);
    }

    [Theory]
    [InlineData("stop", null)]
    [InlineData("takeover", null)]
    [InlineData("stop", "foreign@example.test")]
    [InlineData("takeover", "operator@example.test, operator@example.test")]
    public async Task Glass_hosted_control_requires_exact_server_authenticated_issuer(string verb, string? login)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        await using var glass = await GlassFixture.StartAsync(fixture);
        var claimPath = Path.Combine(fixture.Root, Identity.FileSlug, BatonPaths.ConductorClaimFileName);
        var claim = File.ReadAllBytes(claimPath);
        var registration = File.ReadAllBytes(fixture.RegistrationPath);
        using var response = await glass.ControlAsync(verb, ControlBody(await glass.StatusAsync(), verb), login);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(claim, File.ReadAllBytes(claimPath));
        Assert.Equal(registration, File.ReadAllBytes(fixture.RegistrationPath));
        Assert.Empty(fixture.Calls);
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("takeover")]
    public async Task Glass_control_wins_real_replacement_launch_cutoff_and_manual_promotion(string verb)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.EnableAutomaticAsync();
        await fixture.CommandAsync("attach");
        fixture.Reply = "ReplaceReview";
        await AdmitControlFollowAsync(fixture, "pending");
        var source = await fixture.RowAsync("pending");
        var original = (await fixture.Store.ReadAsync(fixture.Key("pending"), Ct))!;
        await using var glass = await GlassFixture.StartAsync(fixture);
        var body = ControlBody(await glass.StatusAsync(), verb);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launches = 0;
        using var scheduler = fixture.Scheduler(launch: (request, _) =>
        {
            Interlocked.Increment(ref launches);
            return Task.FromResult(new QueueLaunchOutcome(request.RoomDirectory));
        }, beforeLaunchClaim: async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromMinutes(1), token);
        });
        var tick = scheduler.TickOnceAsync(Ct);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromMinutes(1), Ct);
            Assert.Null((await fixture.RowAsync("pending")).LaunchMayHaveBegunAt);
            ConductorFollowSession.AfterHostedControlFence = () => throw new IOException("Offline cleanup failure");
            using var response = await glass.ControlAsync(verb, body);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var receipt = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            Assert.Equal("operator@example.test", receipt.RootElement.GetProperty("receipt").GetProperty("issuer").GetString());
            Assert.StartsWith("pending", receipt.RootElement.GetProperty("cleanup").GetString());
            var action = source.ReplacementReviewAction!;
            var refused = await Record.ExceptionAsync(() => ReplacementReviewConductorCommand.ExecuteAsync(
                ConductorOptionsParser.Parse(["act", "--obligation", action.ObligationKey, "--holder", action.Holder,
                    "--action", "replace-review", "--expected-head", action.HeadSha]),
                TextWriter.Null, fixture.Root, fixture.Advancer(), fixture.Store, Ct));
            Assert.NotNull(refused);
            Assert.Equal(QueueReplacementReviewOrigin.Automatic, (await fixture.RowAsync("pending")).ReplacementReviewAction!.Origin);
        }
        finally
        {
            release.TrySetResult();
            try { await tick.WaitAsync(TimeSpan.FromMinutes(1), Ct); }
            finally { ConductorFollowSession.AfterHostedControlFence = null; }
        }
        var blocked = await fixture.RowAsync("pending");
        Assert.Equal(0, launches);
        Assert.Null(blocked.LaunchMayHaveBegunAt);
        Assert.Null(blocked.ReplacementReviewAction!.ReplacementAttemptId);
        Assert.NotNull(blocked.ReplacementReviewAction.BlockedReason);
        Assert.Equal(source.Round, blocked.Round);
        Assert.Equal(source.ReplacementReviewAction!.EvidenceDigest, blocked.ReplacementReviewAction.EvidenceDigest);
        Assert.Equal(original, await fixture.Store.ReadAsync(fixture.Key("pending"), Ct));
        Assert.True(JsonNode.Parse(File.ReadAllText(fixture.RegistrationPath))!["attached"]!.GetValue<bool>());
        using var restarted = fixture.Scheduler(launch: (_, _) => throw new InvalidOperationException("Revoked slot must never launch"));
        await restarted.TickOnceAsync(Ct);
        await restarted.RecoverAttachedFollowAsync(Ct);
        Assert.Single(fixture.Calls);
        Assert.Equal(0, fixture.LegacyCalls);
        var status = await glass.StatusAsync();
        Assert.Equal(verb == "stop" ? "stopped" : "taken-over", status.GetProperty("state").GetString());
        Assert.False(status.GetProperty("resumeEligible").GetBoolean());
        Assert.True(status.GetProperty("historicalProvider").GetBoolean());
        if (verb == "takeover")
        {
            Assert.Equal("successor", status.GetProperty("holder").GetString());
            Assert.Equal("Phone operator <owner@example.test>", status.GetProperty("destinationAddress").GetString());
            Assert.Equal("holder", status.GetProperty("retainedHolder").GetString());
        }
    }

    [Theory]
    [InlineData("stop", false)]
    [InlineData("takeover", false)]
    [InlineData("stop", true)]
    [InlineData("takeover", true)]
    public async Task Glass_control_wins_marker_free_second_follow_without_session_lock_or_resurrection(string verb, bool cleanupFault)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.EnableAutomaticAsync();
        await fixture.CommandAsync("attach");
        fixture.Reply = "Hold";
        await fixture.HaltAsync("first");
        await fixture.HaltAsync("second", notify: false);
        await fixture.Scheduler().ReconcileStoppedWorkAdviceAsync(Ct);
        await using var glass = await GlassFixture.StartAsync(fixture);
        var body = ControlBody(await glass.StatusAsync(), verb);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var turn = fixture.FollowWithAdmissionAsync("second", (_, _) => Task.CompletedTask, async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromMinutes(1), token);
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromMinutes(1), Ct);
            Assert.False(File.Exists(fixture.EventEvidencePath("second", "launch.json")));
            if (cleanupFault) ConductorFollowSession.AfterHostedControlFence = () => throw new IOException("Offline cleanup failure");
            using var response = await glass.ControlAsync(verb, body);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.False(turn.IsCompleted);
        }
        finally { ConductorFollowSession.AfterHostedControlFence = null; release.TrySetResult(); }
        Assert.Equal("refused", (await turn.WaitAsync(TimeSpan.FromMinutes(1), Ct)).Status);
        Assert.False(File.Exists(fixture.EventEvidencePath("second", "launch.json")));
        using var resume = await glass.ResumeAsync(DetachBody(await glass.StatusAsync()));
        Assert.Equal(HttpStatusCode.Conflict, resume.StatusCode);
        Assert.NotNull(await Record.ExceptionAsync(() => fixture.CommandAsync("attach")));
        using var restarted = fixture.Scheduler();
        await restarted.RecoverAttachedFollowAsync(Ct);
        Assert.Single(fixture.Calls);
        Assert.False(JsonNode.Parse(File.ReadAllText(Path.Combine(SessionDirectory(fixture), "session.json")))!["frozen"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("takeover")]
    public async Task Glass_delayed_model_reply_after_control_retains_response_without_consuming_action(string verb)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.EnableAutomaticAsync();
        await fixture.CommandAsync("attach");
        await using var glass = await GlassFixture.StartAsync(fixture);
        var body = ControlBody(await glass.StatusAsync(), verb);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Reply = "ReplaceReview";
        fixture.WaitInBroker = async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromMinutes(1), token);
        };
        var halt = fixture.HaltAsync("delayed");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromMinutes(1), Ct);
            ConductorFollowSession.AfterHostedControlFence = () => throw new IOException("Offline cleanup failure");
            using var response = await glass.ControlAsync(verb, body);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.False(halt.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            try { await halt.WaitAsync(TimeSpan.FromMinutes(1), Ct); }
            finally { ConductorFollowSession.AfterHostedControlFence = null; }
        }
        await halt.WaitAsync(TimeSpan.FromMinutes(1), Ct);
        Assert.True(JsonNode.Parse(File.ReadAllText(fixture.RegistrationPath))!["attached"]!.GetValue<bool>());
        Assert.NotNull(fixture.Receipt("delayed"));
        Assert.NotNull(ConductorFollowSession.ReadDecisionEvidence(Path.GetDirectoryName(fixture.EventEvidencePath("delayed", "decision.json"))!));
        Assert.Null((await fixture.RowAsync("delayed")).ReplacementReviewAction);
        Assert.Null((await fixture.RowAsync("delayed")).LaunchMayHaveBegunAt);
        using var restarted = fixture.Scheduler();
        await restarted.RecoverAttachedFollowAsync(Ct);
        Assert.Single(fixture.Calls);
        Assert.Equal(ConductorObligationStatus.Pending, (await fixture.Store.ReadAsync(fixture.Key("delayed"), Ct))!.Status);
    }

    [Theory]
    [InlineData("stop", "complete")]
    [InlineData("takeover", "complete")]
    [InlineData("stop", "stale-head")]
    [InlineData("takeover", "artifactless")]
    [InlineData("stop", "wrong-attempt")]
    [InlineData("stop", "marker-crash")]
    public async Task Glass_marker_wins_retains_one_issued_worker_and_late_exact_outcome_after_revocation(string verb, string completion)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.EnableAutomaticAsync();
        await fixture.CommandAsync("attach");
        fixture.Reply = "ReplaceReview";
        await AdmitControlFollowAsync(fixture, "issued");
        await using var glass = await GlassFixture.StartAsync(fixture);
        var body = ControlBody(await glass.StatusAsync(), verb);
        var launches = 0;
        QueueItem? issued = null;
        using var scheduler = fixture.Scheduler(launch: async (request, _) =>
        {
            launches++;
            issued = await fixture.RowAsync("issued");
            Assert.NotNull(issued.LaunchMayHaveBegunAt);
            Assert.NotNull(issued.ReplacementReviewAction!.IssuedAuthority);
            using var response = await glass.ControlAsync(verb, body);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            if (completion == "marker-crash") throw new IOException("Offline crash after worker marker");
            return new QueueLaunchOutcome(request.RoomDirectory);
        });
        await scheduler.TickOnceAsync(Ct);
        Assert.Equal(1, launches);
        await fixture.CompleteReviewAsync("issued", completion == "marker-crash" ? "complete" : completion);
        await QueueStore.MutateAsync(BatonPaths.QueueFile, queue => queue with { Held = true }, Ct);
        await fixture.ChangeAsync("issued", "opt-in");
        using var restarted = fixture.Scheduler(launch: (_, _) => throw new InvalidOperationException("No second call"));
        await restarted.TickOnceAsync(Ct);
        await restarted.ReconcileReplacementReviewActionsAsync(Ct);
        var completed = await fixture.RowAsync("issued");
        Assert.Equal(issued!.ReplacementReviewAction!.IssuedAuthority, completed.ReplacementReviewAction!.IssuedAuthority);
        Assert.Equal(issued.ReplacementReviewAction.ReplacementAttemptId, completed.ReplacementReviewAction.ReplacementAttemptId);
        Assert.NotNull(completed.ReplacementReviewAction.TerminalObservation);
        Assert.Equal(completion is "complete" or "marker-crash" ? ConductorObligationStatus.ActionObserved : ConductorObligationStatus.Pending,
            (await fixture.Store.ReadAsync(fixture.Key("issued"), Ct))!.Status);
        if (completion is "complete" or "marker-crash")
        {
            Assert.NotNull(completed.ReplacementReviewAction.ActionObservedAt);
            Assert.NotNull(completed.ReplacementReviewAction.TerminalObservation);
        }
        else Assert.NotNull(completed.ReplacementReviewAction.BlockedReason);
        Assert.Single(fixture.Calls);
        Assert.Equal(1, launches);
        Assert.True((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Held);
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("takeover")]
    public async Task Glass_fence_cleanup_failure_restart_and_historical_duplicate_preserve_new_registration(string verb)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        string body;
        JsonElement receipt;
        await using (var glass = await GlassFixture.StartAsync(fixture))
        {
            body = ControlBody(await glass.StatusAsync(), verb);
            ConductorFollowSession.AfterHostedControlFence = () => throw new IOException("Offline crash after authoritative fence");
            try
            {
                using var response = await glass.ControlAsync(verb, body);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
                receipt = result.RootElement.GetProperty("receipt").Clone();
                Assert.StartsWith("pending", result.RootElement.GetProperty("cleanup").GetString());
            }
            finally { ConductorFollowSession.AfterHostedControlFence = null; }
            Assert.True(JsonNode.Parse(File.ReadAllText(fixture.RegistrationPath))!["attached"]!.GetValue<bool>());
            Assert.NotNull(await Record.ExceptionAsync(() => fixture.CommandAsync("attach")));
        }
        await ConductorFollowSession.ReconcileHostedControlCleanupAsync(fixture.Root, Ct);
        Assert.False(JsonNode.Parse(File.ReadAllText(fixture.RegistrationPath))!["attached"]!.GetValue<bool>());
        await ConductorClaimStore.ReleaseAsync(Identity, verb == "stop" ? "holder" : "successor", "new explicit acquisition", fixture.Root, cancellationToken: Ct);
        var newClaim = await ConductorClaimStore.ClaimAsync(Identity, "holder", fixture.Root, cancellationToken: Ct);
        var registration = JsonNode.Parse(File.ReadAllText(fixture.RegistrationPath))!;
        registration["id"] = new string('b', 32);
        registration["claimGeneration"] = ConductorClaimStore.GetClaimGeneration(newClaim);
        registration["attached"] = true;
        await QueueStore.MutateAsync(BatonPaths.QueueFile, queue =>
        {
            File.WriteAllText(fixture.RegistrationPath, registration.ToJsonString());
            return queue;
        }, Ct);
        var bytes = File.ReadAllBytes(fixture.RegistrationPath);
        var claimBytes = File.ReadAllBytes(Path.Combine(fixture.Root, Identity.FileSlug, BatonPaths.ConductorClaimFileName));
        await using var restarted = await GlassFixture.StartAsync(fixture);
        using var replay = await restarted.ControlAsync(verb, body);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var replayResult = JsonDocument.Parse(await replay.Content.ReadAsStringAsync(Ct));
        Assert.Equal(receipt.GetRawText(), replayResult.RootElement.GetProperty("receipt").GetRawText());
        Assert.Equal(bytes, File.ReadAllBytes(fixture.RegistrationPath));
        Assert.Equal(claimBytes, File.ReadAllBytes(Path.Combine(fixture.Root, Identity.FileSlug, BatonPaths.ConductorClaimFileName)));
        var changed = JsonNode.Parse(body)!;
        changed["reason"] = "different input";
        using var conflicting = await restarted.ControlAsync(verb, changed.ToJsonString());
        Assert.Equal(HttpStatusCode.Conflict, conflicting.StatusCode);
        changed["requestId"] = "new-stale-request";
        using var stale = await restarted.ControlAsync(verb, changed.ToJsonString());
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(bytes, File.ReadAllBytes(fixture.RegistrationPath));
        Assert.Empty(fixture.Calls);
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("takeover")]
    public async Task Glass_frozen_and_unhealthy_delivery_evidence_is_stoppable_without_recovery(string verb)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        var path = Path.Combine(SessionDirectory(fixture), "session.json");
        var state = JsonNode.Parse(File.ReadAllText(path))!;
        state["frozen"] = true;
        File.WriteAllText(path, state.ToJsonString());
        File.WriteAllText(Path.Combine(SessionDirectory(fixture), "delivery.jsonl"), "{}");
        var retained = RetainedBytes(fixture);
        await using var glass = await GlassFixture.StartAsync(fixture);
        var status = await glass.StatusAsync();
        Assert.True(status.GetProperty("stopEligible").GetBoolean());
        using var response = await glass.ControlAsync(verb, ControlBody(status, verb));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertRetainedBytes(retained);
        Assert.Empty(fixture.Calls);
    }

    [Theory]
    [InlineData("reason")]
    [InlineData("issuer")]
    [InlineData("attachmentId")]
    [InlineData("claimGeneration")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("oversized")]
    [InlineData("null")]
    public async Task Glass_control_invalid_input_refuses_with_pinned_safe_diagnostic(string change)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        await using var glass = await GlassFixture.StartAsync(fixture);
        var body = JsonNode.Parse(ControlBody(await glass.StatusAsync(), "stop"))!;
        if (change == "reason") body["reason"] = " ";
        else if (change == "issuer") body["issuer"] = "operator@example.test";
        else if (change == "unknown") body["workspace"] = fixture.Workspace;
        else if (change == "oversized") body["reason"] = new string('x', 4097);
        else if (change == "null") body["requestId"] = null;
        else if (change != "duplicate") body[change] = "foreign";
        var text = body.ToJsonString();
        if (change == "duplicate") text = text[..^1] + ",\"holder\":\"holder\"}";
        var bytes = File.ReadAllBytes(fixture.RegistrationPath);
        using var response = await glass.ControlAsync("stop", text);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Control refused: exact identity, request ID, reason or destination could not be verified. Refresh before retrying.",
            await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(bytes, File.ReadAllBytes(fixture.RegistrationPath));
    }

    [Fact]
    public async Task Glass_stop_and_takeover_race_has_one_transition_and_stopped_target_can_transfer_once()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        await using var glass = await GlassFixture.StartAsync(fixture);
        var status = await glass.StatusAsync();
        var replies = await Task.WhenAll(glass.ControlAsync("stop", ControlBody(status, "stop", "stop-race")),
            glass.ControlAsync("takeover", ControlBody(status, "takeover", "takeover-race")));
        try { Assert.Contains(replies, reply => reply.StatusCode == HttpStatusCode.OK); }
        finally { foreach (var reply in replies) reply.Dispose(); }
        var claim = (await ConductorClaimStore.GetClaimAsync(Identity, fixture.Root, Ct))!;
        // Stop followed by takeover is legal; takeover followed by stale Stop is refused.
        Assert.InRange(claim.Transitions!.Count(t => t.Control is not null), 1, 2);
        if (claim.Stopped)
        {
            using var takeover = await glass.ControlAsync("takeover", ControlBody(await glass.StatusAsync(), "takeover", "after-stop"));
            Assert.Equal(HttpStatusCode.OK, takeover.StatusCode);
        }
        var current = await glass.StatusAsync();
        Assert.Equal("taken-over", current.GetProperty("state").GetString());
        Assert.Equal("successor", current.GetProperty("holder").GetString());
        Assert.Empty(fixture.Calls);
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("takeover")]
    public async Task Glass_lost_acknowledgement_reconciles_exact_retained_receipt_after_controller_restart(string verb)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        await using (var glass = await GlassFixture.StartAsync(fixture))
        {
            var body = ControlBody(await glass.StatusAsync(), verb, "lost-acknowledgement");
            glass.DropControlAcknowledgement();
            var failure = await Record.ExceptionAsync(async () =>
            {
                using var response = await glass.ControlAsync(verb, body);
                response.EnsureSuccessStatusCode();
            });
            Assert.NotNull(failure);
        }
        await using var restarted = await GlassFixture.StartAsync(fixture);
        var status = await restarted.StatusAsync();
        var control = Assert.Single(status.GetProperty("controls").EnumerateArray());
        Assert.Equal("lost-acknowledgement", control.GetProperty("receipt").GetProperty("request").GetProperty("requestId").GetString());
        Assert.Equal("complete", control.GetProperty("cleanup").GetString());
        Assert.Equal(verb == "stop" ? "stopped" : "taken-over", status.GetProperty("state").GetString());
        Assert.Single((await ConductorClaimStore.GetClaimAsync(Identity, fixture.Root, Ct))!.Transitions!, t => t.Control is not null);
        Assert.Empty(fixture.Calls);
    }

    [Fact]
    public async Task Glass_duplicate_stop_and_resume_race_allocate_only_eligible_exact_cutovers()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        await fixture.CommandAsync("detach");
        await using var glass = await GlassFixture.StartAsync(fixture);
        var status = await glass.StatusAsync();
        var stopBody = ControlBody(status, "stop", "stop-resume-race");
        var replies = await Task.WhenAll(glass.ControlAsync("stop", stopBody), glass.ResumeAsync(DetachBody(status)));
        try
        {
            Assert.Single(replies, reply => reply.StatusCode == HttpStatusCode.OK);
            Assert.Single(replies, reply => reply.StatusCode == HttpStatusCode.Conflict);
        }
        finally { foreach (var reply in replies) reply.Dispose(); }
        var current = await glass.StatusAsync();
        if (current.GetProperty("state").GetString() != "stopped")
        {
            Assert.NotEqual(status.GetProperty("attachmentId").GetString(), current.GetProperty("attachmentId").GetString());
            stopBody = ControlBody(current, "stop", "fresh-stop");
        }
        var repeated = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => glass.ControlAsync("stop", stopBody)));
        try { Assert.All(repeated, reply => Assert.Equal(HttpStatusCode.OK, reply.StatusCode)); }
        finally { foreach (var reply in repeated) reply.Dispose(); }
        Assert.Single((await ConductorClaimStore.GetClaimAsync(Identity, fixture.Root, Ct))!.Transitions!, t => t.Control is not null);
        Assert.Equal("stopped", (await glass.StatusAsync()).GetProperty("state").GetString());
    }

    [Theory]
    [InlineData("stop", false)]
    [InlineData("takeover", true)]
    public async Task Glass_completion_proof_crash_reconciles_after_new_claim_and_attachment_without_reusing_permission(string verb, bool manual)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.EnableAutomaticAsync();
        await fixture.CommandAsync("attach");
        fixture.Reply = "ReplaceReview";
        await AdmitControlFollowAsync(fixture, "proof-crash");
        var action = (await fixture.RowAsync("proof-crash")).ReplacementReviewAction!;
        if (manual)
            await ReplacementReviewConductorCommand.ExecuteAsync(ConductorOptionsParser.Parse(
                ["act", "--obligation", action.ObligationKey, "--holder", action.Holder,
                    "--action", "replace-review", "--expected-head", action.HeadSha]),
                TextWriter.Null, fixture.Root, fixture.Advancer(), fixture.Store, Ct);
        var launches = 0;
        var advancer = fixture.Advancer();
        using var scheduler = fixture.Scheduler(advancer, launch: (request, _) =>
        {
            launches++;
            return Task.FromResult(new QueueLaunchOutcome(request.RoomDirectory));
        });
        await scheduler.TickOnceAsync(Ct);
        var issued = (await fixture.RowAsync("proof-crash")).ReplacementReviewAction!;
        Assert.NotNull(issued.IssuedAuthority);
        Assert.Equal(ReplacementReviewEvidenceProvenance.CompletedFollow, issued.EvidenceProvenance);
        Assert.Equal(manual ? QueueReplacementReviewOrigin.Manual : QueueReplacementReviewOrigin.Automatic, issued.Origin);
        await fixture.CompleteReviewAsync("proof-crash");
        await using var glass = await GlassFixture.StartAsync(fixture);
        var body = ControlBody(await glass.StatusAsync(), verb, "after-proof");
        var interrupted = false;
        advancer.ReplacementReviewAfterProofPersisted = () =>
        {
            interrupted = true;
            using var response = glass.ControlAsync(verb, body).GetAwaiter().GetResult();
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            throw new IOException("Offline crash after proof persistence and before observation");
        };
        await scheduler.TickOnceAsync(Ct);
        Assert.True(interrupted);
        var claim = (await ConductorClaimStore.GetClaimAsync(Identity, fixture.Root, Ct))!;
        if (claim.Stopped)
        {
            await ConductorClaimStore.ReleaseAsync(Identity, claim.Holder!, "explicit new acquisition", fixture.Root, cancellationToken: Ct);
            claim = await ConductorClaimStore.ClaimAsync(Identity, "successor", fixture.Root, cancellationToken: Ct);
        }
        await InstallNewHostedFixtureAsync(fixture, claim);
        var registration = File.ReadAllBytes(fixture.RegistrationPath);
        await QueueStore.MutateAsync(BatonPaths.QueueFile, queue => queue with { Held = true }, Ct);
        await fixture.ChangeAsync("proof-crash", "opt-in");
        using var restarted = fixture.Scheduler(launch: (_, _) => throw new InvalidOperationException("Issued permission cannot be reused"));
        await restarted.ReconcileReplacementReviewActionsAsync(Ct);
        await restarted.TickOnceAsync(Ct);
        Assert.Equal(ConductorObligationStatus.ActionObserved, (await fixture.Store.ReadAsync(action.ObligationKey, Ct))!.Status);
        var completed = (await fixture.RowAsync("proof-crash")).ReplacementReviewAction!;
        Assert.Equal(issued.IssuedAuthority, completed.IssuedAuthority);
        Assert.NotNull(completed.ActionObservedAt);
        Assert.Equal(registration, File.ReadAllBytes(fixture.RegistrationPath));
        Assert.Equal("successor", (await ConductorClaimStore.GetClaimAsync(Identity, fixture.Root, Ct))!.Holder);
        Assert.Equal(1, launches);
        Assert.Single(fixture.Calls);
    }

    [Theory]
    [InlineData("stop", "registration")]
    [InlineData("takeover", "session")]
    [InlineData("stop", "request")]
    [InlineData("takeover", "path")]
    [InlineData("stop", "claim")]
    [InlineData("takeover", "null-holder")]
    public async Task Glass_control_corrupt_or_unsafe_authority_identity_refuses_without_fence(string verb, string corruption)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        await using var glass = await GlassFixture.StartAsync(fixture);
        var body = ControlBody(await glass.StatusAsync(), verb);
        var claimPath = Path.Combine(fixture.Root, Identity.FileSlug, BatonPaths.ConductorClaimFileName);
        if (corruption == "claim") File.WriteAllText(claimPath, "{}");
        else if (corruption == "session") File.WriteAllText(Path.Combine(SessionDirectory(fixture), "session.json"), "{}");
        else if (corruption == "request") File.WriteAllText(Path.Combine(SessionDirectory(fixture), "request.json"), "{}");
        else
        {
            var registration = JsonNode.Parse(File.ReadAllText(fixture.RegistrationPath))!;
            registration[corruption == "path" ? "sessionDirectory" : corruption == "null-holder" ? "holder" : "claimGeneration"]
                = corruption == "null-holder" ? null : corruption == "path" ? Path.Combine(fixture.Root, "foreign") : "foreign";
            File.WriteAllText(fixture.RegistrationPath, registration.ToJsonString());
        }
        var claimBytes = File.ReadAllBytes(claimPath);
        var registrationBytes = File.ReadAllBytes(fixture.RegistrationPath);
        using var response = await glass.ControlAsync(verb, body);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(claimBytes, File.ReadAllBytes(claimPath));
        Assert.Equal(registrationBytes, File.ReadAllBytes(fixture.RegistrationPath));
        Assert.Empty(fixture.Calls);
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("takeover")]
    public async Task Glass_control_queue_busy_is_bounded_and_never_mutates_authority(string verb)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        await using var glass = await GlassFixture.StartAsync(fixture);
        var body = ControlBody(await glass.StatusAsync(), verb);
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
            using var response = await glass.ControlAsync(verb, body).WaitAsync(TimeSpan.FromSeconds(5), Ct); // wait-ok: Verify bounded queue-lock refusal over authenticated HTTP.
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }
        finally { release.TrySetResult(); await blocker; }
        Assert.Equal(bytes, File.ReadAllBytes(claimPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Hosted_issued_launcher_refusal_keeps_marker_and_slot_without_retry(bool deferred)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.EnableAutomaticAsync();
        await fixture.CommandAsync("attach");
        fixture.Reply = "ReplaceReview";
        await AdmitControlFollowAsync(fixture, "issued-refused");
        var calls = 0;
        using var scheduler = fixture.Scheduler(launch: (_, _) =>
        {
            calls++;
            return Task.FromResult(new QueueLaunchOutcome(null, RunwayHeld: !deferred, Deferred: deferred));
        });
        await scheduler.TickOnceAsync(Ct);
        var issued = await fixture.RowAsync("issued-refused");
        Assert.Equal(QueueItemState.Failed, issued.State);
        Assert.True(issued.Halted);
        Assert.NotNull(issued.LaunchMayHaveBegunAt);
        Assert.NotNull(issued.ReplacementReviewAction!.IssuedAuthority);
        Assert.NotNull(issued.ReplacementReviewAction.ReplacementAttemptId);
        Assert.NotNull(issued.ReplacementReviewAction.BlockedReason);
        Assert.True(issued.AttemptRefusedFactDurable);
        using var restarted = fixture.Scheduler(launch: (_, _) => throw new InvalidOperationException("Issued slot cannot retry"));
        await restarted.TickOnceAsync(Ct);
        Assert.Equal(issued.ReplacementReviewAction.IssuedAuthority, (await fixture.RowAsync("issued-refused")).ReplacementReviewAction!.IssuedAuthority);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("takeover")]
    public async Task Glass_control_waiting_for_queue_cannot_revoke_same_holder_reacquisition(string verb)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        await using var glass = await GlassFixture.StartAsync(fixture);
        var status = await glass.StatusAsync();
        var body = ControlBody(status, verb, "before-reacquisition");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = Task.Run(() => MutexGuardedFileLock.RunUnderLock(BatonPaths.QueueFile, QueueStore.LockNamePrefix,
            TimeSpan.FromSeconds(1), () =>
            {
                entered.TrySetResult();
                release.Task.WaitAsync(TimeSpan.FromMinutes(1), Ct).GetAwaiter().GetResult();
                return true;
            }), Ct);
        ConductorClaimRecord fresh;
        Task<HttpResponseMessage> control;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromMinutes(1), Ct);
            control = glass.ControlAsync(verb, body);
            await ConductorClaimStore.ReleaseAsync(Identity, "holder", "explicit reacquisition", fixture.Root, cancellationToken: Ct);
            fresh = await ConductorClaimStore.ClaimAsync(Identity, "holder", fixture.Root, cancellationToken: Ct);
            Assert.NotEqual(status.GetProperty("claimGeneration").GetString(), ConductorClaimStore.GetClaimGeneration(fresh));
        }
        finally { release.TrySetResult(); await blocker; }
        await InstallNewHostedFixtureAsync(fixture, fresh);
        var registration = File.ReadAllBytes(fixture.RegistrationPath);
        using var response = await control;
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await ConductorFollowSession.ReconcileHostedControlCleanupAsync(fixture.Root, Ct);
        Assert.Equal(registration, File.ReadAllBytes(fixture.RegistrationPath));
        var current = (await ConductorClaimStore.GetClaimAsync(Identity, fixture.Root, Ct))!;
        Assert.Equal(ConductorClaimStore.GetClaimGeneration(fresh), ConductorClaimStore.GetClaimGeneration(current));
        Assert.False(current.Stopped);
        Assert.DoesNotContain(current.Transitions!, transition => transition.Control is not null);
        Assert.Empty(fixture.Calls);
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("takeover")]
    public async Task Glass_control_linked_session_refuses_without_mutating_authority_or_link_target(string verb)
    {
        Assert.True(OperatingSystem.IsWindows());
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        await using var glass = await GlassFixture.StartAsync(fixture);
        var body = ControlBody(await glass.StatusAsync(), verb);
        var directory = SessionDirectory(fixture);
        var target = Path.Combine(fixture.Root, "control-link-target");
        Directory.Move(directory, target);
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "/c", "mklink", "/J", directory, target }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        await BoundedProcessWait.RunToExitAsync(process, TimeSpan.FromSeconds(30), Ct);
        Assert.Equal(0, process.ExitCode);
        try
        {
            var claimPath = Path.Combine(fixture.Root, Identity.FileSlug, BatonPaths.ConductorClaimFileName);
            var claim = File.ReadAllBytes(claimPath);
            var registration = File.ReadAllBytes(fixture.RegistrationPath);
            var evidence = Directory.GetFiles(target, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
            using var response = await glass.ControlAsync(verb, body);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal(claim, File.ReadAllBytes(claimPath));
            Assert.Equal(registration, File.ReadAllBytes(fixture.RegistrationPath));
            AssertRetainedBytes(evidence);
            Assert.Empty(fixture.Calls);
        }
        finally { DirectoryCleanup.DeleteRecursively(directory); }
    }

    private static async Task InstallNewHostedFixtureAsync(Fixture fixture, ConductorClaimRecord claim)
    {
        // An explicit offline successor fixture: fresh identity, no copied native conversation.
        var oldDirectory = SessionDirectory(fixture);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var request = JsonSerializer.Deserialize<ConductorFollowRequest>(File.ReadAllText(Path.Combine(oldDirectory, "request.json")), json)! with { Holder = claim.Holder! };
        var state = JsonSerializer.Deserialize<ConductorFollowState>(File.ReadAllText(Path.Combine(oldDirectory, "session.json")), json)! with
        {
            Holder = claim.Holder!,
            ClaimGeneration = ConductorClaimStore.GetClaimGeneration(claim),
            SessionId = null,
            Frozen = false,
        };
        static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        var directory = Path.Combine(fixture.Root, "conductor-follow", Identity.FileSlug, Digest(Repository + "\n" + state.ClaimGeneration));
        Directory.CreateDirectory(directory);
        var requestText = JsonSerializer.Serialize(request, json);
        File.WriteAllText(Path.Combine(directory, "request.json"), requestText);
        File.WriteAllText(Path.Combine(directory, "session.json"), JsonSerializer.Serialize(state, json));
        var registration = new ConductorFollowAttachment(1, Guid.NewGuid().ToString("N"), Repository,
            state.ClaimGeneration, claim.Holder!, directory, Digest(requestText), DateTimeOffset.UtcNow, true);
        await QueueStore.MutateAsync(BatonPaths.QueueFile, queue =>
        {
            File.WriteAllText(fixture.RegistrationPath, JsonSerializer.Serialize(registration, json));
            return queue;
        }, Ct);
    }

    [Fact]
    public async Task Issued_legacy_record_without_historical_hosted_binding_is_unresolved_without_new_call()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.EnableAutomaticAsync();
        await fixture.CommandAsync("attach");
        fixture.Reply = "ReplaceReview";
        await AdmitControlFollowAsync(fixture, "legacy-issued");
        var launches = 0;
        using var scheduler = fixture.Scheduler(launch: (request, _) =>
        {
            launches++;
            return Task.FromResult(new QueueLaunchOutcome(request.RoomDirectory));
        });
        await scheduler.TickOnceAsync(Ct);
        await QueueStore.MutateAsync(BatonPaths.QueueFile, queue => queue with
        {
            Items = queue.Items.Select(item => item.Tag == "legacy-issued" ? item with
            {
                ReplacementReviewAction = item.ReplacementReviewAction! with { IssuedAuthority = null },
            } : item).ToList(),
        }, Ct);
        await fixture.CompleteReviewAsync("legacy-issued");
        await scheduler.TickOnceAsync(Ct);
        await scheduler.ReconcileReplacementReviewActionsAsync(Ct);
        var result = (await fixture.RowAsync("legacy-issued")).ReplacementReviewAction!;
        Assert.NotNull(result.CompletionProof);
        Assert.Null(result.IssuedAuthority);
        Assert.NotNull(result.BlockedReason);
        Assert.NotNull(result.NextTrigger);
        Assert.Equal(ConductorObligationStatus.Pending, (await fixture.Store.ReadAsync(result.ObligationKey, Ct))!.Status);
        Assert.Equal(1, launches);
        Assert.Single(fixture.Calls);
    }
}
