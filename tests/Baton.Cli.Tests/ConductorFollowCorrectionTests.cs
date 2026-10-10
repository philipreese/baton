using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Baton.Accounting;
using Baton.Cli.Daemon;
using Baton.Conductor;
using Baton.Queue;
using Baton.Status;
using Baton.Tests.Shared;
using Baton.Vendors;

namespace Baton.Cli.Tests;

public sealed partial class ConductorFollowDeliveryTests
{
    private const string Operator = "operator@example.test";

    private static string CorrectionBody(JsonElement row, string id, string text = "Reconsider the retained evidence; do not apply a change.") =>
        JsonSerializer.Serialize(new
        {
            repository = row.GetProperty("repository").GetString(),
            holder = row.GetProperty("holder").GetString(),
            claimGeneration = row.GetProperty("claimGeneration").GetString(),
            attachmentId = row.GetProperty("attachmentId").GetString(),
            requestId = id,
            text,
        });

    private static async Task<GlassCorrectionResult> AcceptCorrectionAsync(Fixture fixture, GlassFixture glass, string id = "correction")
        => await ConductorFollowSession.CorrectFromGlassAsync(CorrectionBody(await glass.StatusAsync(), id), fixture.Root, Operator, Ct);

    private static Task<GlassCorrectionResult> LookupCorrectionAsync(Fixture fixture, string id = "correction") =>
        ConductorFollowSession.LookupCorrectionReceiptAsync(fixture.Root, Operator, id, Ct);

    private static string CorrectionEvidence(Fixture fixture, string file)
    {
        var slot = JsonNode.Parse(File.ReadAllText(Path.Combine(SessionDirectory(fixture), "correction.json")))!;
        var eventId = slot["eventId"]!.GetValue<string>();
        return Path.Combine(SessionDirectory(fixture), "events",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(eventId))).ToLowerInvariant(), file);
    }

    [Fact]
    public async Task Issue2666_Busy_acceptance_does_not_wait_for_session_and_existing_tick_delivers_once()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        fixture.Reply = "Hold";
        await using var glass = await GlassFixture.StartAsync(fixture);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.WaitInBroker = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); };
        using var scheduler = fixture.Scheduler();
        var current = fixture.HaltAsync("busy-correction", scheduler: scheduler);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct); // wait-ok: Offline broker rendezvous.
            var accepted = await AcceptCorrectionAsync(fixture, glass).WaitAsync(TimeSpan.FromSeconds(3), Ct); // wait-ok: Acceptance must avoid the busy session.
            Assert.Equal("accepted", accepted.Outcome);
            Assert.Equal("queued", accepted.Receipt!.State);
            Assert.Single(fixture.Calls);
            var capacity = await AcceptCorrectionAsync(fixture, glass, "other-request");
            Assert.Equal("capacity", capacity.Outcome);
            Assert.Equal(accepted.Receipt.ReceiptId, capacity.Receipt!.ReceiptId);
            await scheduler.TickOnceAsync(Ct);
            Assert.Single(fixture.Calls);
        }
        finally { release.TrySetResult(); }
        await current;
        fixture.WaitInBroker = null;
        await scheduler.TickOnceAsync(Ct);
        await WaitForAdvicePassAsync(scheduler);
        await scheduler.TickOnceAsync(Ct);
        await WaitForAdvicePassAsync(scheduler);
        Assert.Equal(2, fixture.Calls.Count);
        Assert.Equal("retained-thread", fixture.Calls[1].SessionId);
        Assert.True(fixture.Calls[1].ResumeSession);
        var receipt = (await LookupCorrectionAsync(fixture)).Receipt!;
        Assert.Equal("delivered", receipt.State);
        Assert.Equal("not verified", receipt.Application);
        Assert.Null((await fixture.RowAsync("busy-correction")).ReplacementReviewAction);
        Assert.False(File.Exists(CorrectionEvidence(fixture, "decision.json")));
        await scheduler.DrainStoppedWorkAdviceAsync();
    }

    [Theory]
    [InlineData("source", false)]
    [InlineData("slot", false)]
    [InlineData("acceptance", true)]
    public async Task Issue2666_Publication_crashes_distinguish_preparation_from_committed_acceptance(string crash, bool committed)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        await using var glass = await GlassFixture.StartAsync(fixture);
        var body = CorrectionBody(await glass.StatusAsync(), "crash-" + crash);
        ConductorFollowSession.AfterCorrectionPersistence = stage =>
        {
            if (stage == crash) throw new IOException("Offline persistence cut");
        };
        try
        {
            if (committed)
                Assert.Equal("unknown", (await ConductorFollowSession.CorrectFromGlassAsync(body, fixture.Root, Operator, Ct)).Outcome);
            else
            {
                if (crash == "slot")
                    Assert.IsType<IOException>((await Assert.ThrowsAsync<ConductorClaimException>(() =>
                        ConductorFollowSession.CorrectFromGlassAsync(body, fixture.Root, Operator, Ct))).InnerException);
                else
                    await Assert.ThrowsAsync<IOException>(() => ConductorFollowSession.CorrectFromGlassAsync(body, fixture.Root, Operator, Ct));
            }
        }
        finally { ConductorFollowSession.AfterCorrectionPersistence = null; }
        var lookup = await LookupCorrectionAsync(fixture, "crash-" + crash);
        Assert.Equal(committed ? "accepted" : "absent", lookup.Outcome);
        if (!committed)
        {
            Assert.Null(await fixture.DeliverCorrectionAsync());
            Assert.Empty(fixture.Calls);
        }
        var accepted = await ConductorFollowSession.CorrectFromGlassAsync(body, fixture.Root, Operator, Ct);
        Assert.Equal(committed, accepted.Replayed);
        Assert.Equal("accepted", accepted.Outcome);
        Assert.Equal("delivered", (await fixture.DeliverCorrectionAsync())!.Status);
        Assert.Single(fixture.Calls);
    }

    [Theory]
    [InlineData("marker", "uncertain")]
    [InlineData("response", "delivered")]
    [InlineData("delivery", "delivered")]
    public async Task Issue2666_Issued_crash_recovery_never_calls_twice_or_parses_a_correction_decision(string crash, string expected)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.EnableAutomaticAsync();
        await fixture.CommandAsync("attach");
        fixture.Reply = "ReplaceReview";
        await using var glass = await GlassFixture.StartAsync(fixture);
        await AcceptCorrectionAsync(fixture, glass);
        ConductorFollowSession.AfterCorrectionPersistence = stage =>
        {
            if (stage == crash) throw new IOException("Offline issued cut");
        };
        try { Assert.Equal("uncertain", (await fixture.DeliverCorrectionAsync())!.Status); }
        finally { ConductorFollowSession.AfterCorrectionPersistence = null; }
        var calls = fixture.Calls.Count;
        await fixture.DeliverCorrectionAsync();
        using var scheduler = fixture.Scheduler();
        await scheduler.RecoverAttachedFollowAsync(Ct);
        await WaitForAdvicePassAsync(scheduler);
        await scheduler.TickOnceAsync(Ct);
        await WaitForAdvicePassAsync(scheduler);
        Assert.Equal(calls, fixture.Calls.Count);
        Assert.Equal(expected, (await LookupCorrectionAsync(fixture)).Receipt!.State);
        Assert.False(File.Exists(CorrectionEvidence(fixture, "decision.json")));
        var capacity = await AcceptCorrectionAsync(fixture, glass, "next");
        if (expected == "uncertain") Assert.Equal("capacity", capacity.Outcome);
        else Assert.Equal("accepted", capacity.Outcome);
        Assert.Empty((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
        await scheduler.DrainStoppedWorkAdviceAsync();
    }

    [Theory]
    [InlineData("hold")]
    [InlineData("stop")]
    [InlineData("takeover")]
    [InlineData("detach")]
    public async Task Issue2666_Marker_before_control_preserves_late_delivered_receipt(string operation)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        await using var glass = await GlassFixture.StartAsync(fixture);
        var body = CorrectionBody(await glass.StatusAsync(), "late");
        await ConductorFollowSession.CorrectFromGlassAsync(body, fixture.Root, Operator, Ct);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.WaitInBroker = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); };
        var delivery = fixture.DeliverCorrectionAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct); // wait-ok: Marker-first rendezvous.
            Assert.Equal("issued", (await LookupCorrectionAsync(fixture, "late")).Receipt!.State);
            var row = await glass.StatusAsync();
            using var response = operation == "detach" ? await glass.DetachAsync(DetachBody(row))
                : await glass.ControlAsync(operation, operation == "hold" ? HoldBody(row, operation, "late-control") : ControlBody(row, operation, "late-control"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally { release.TrySetResult(); }
        Assert.Equal("delivered", (await delivery)!.Status);
        Assert.Equal("delivered", (await LookupCorrectionAsync(fixture, "late")).Receipt!.State);
        var replay = await ConductorFollowSession.CorrectFromGlassAsync(body, fixture.Root, Operator, Ct);
        Assert.True(replay.Replayed);
        Assert.Single(fixture.Calls);
    }

    [Fact]
    public async Task Issue2666_Hold_accepts_without_launch_then_Unhold_rechecks_exact_cutoff()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        await using var glass = await GlassFixture.StartAsync(fixture);
        await ApplyHoldAsync(glass, "hold", "correction-hold");
        var accepted = await AcceptCorrectionAsync(fixture, glass);
        Assert.Equal("accepted", accepted.Outcome);
        Assert.Equal("held-pending", (await fixture.DeliverCorrectionAsync())!.Status);
        Assert.Empty(fixture.Calls);
        Assert.Equal("waiting", (await LookupCorrectionAsync(fixture)).Receipt!.State);
        await ApplyHoldAsync(glass, "unhold", "correction-unhold");
        using var scheduler = fixture.Scheduler();
        await scheduler.TickOnceAsync(Ct);
        await WaitForAdvicePassAsync(scheduler);
        Assert.Single(fixture.Calls);
        Assert.Equal("delivered", (await LookupCorrectionAsync(fixture)).Receipt!.State);
        await scheduler.TickOnceAsync(Ct);
        await WaitForAdvicePassAsync(scheduler);
        Assert.Single(fixture.Calls);
        await scheduler.DrainStoppedWorkAdviceAsync();
    }

    [Fact]
    public async Task Issue2666_Exact_predecessor_gets_one_opportunity_before_correction_then_new_ordinary_work()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        fixture.Reply = "Hold";
        await using var glass = await GlassFixture.StartAsync(fixture);
        await ApplyHoldAsync(glass, "hold", "fairness-hold");
        await fixture.HaltAsync("older", notify: false);
        await fixture.HaltAsync("newer", notify: false);
        await AcceptCorrectionAsync(fixture, glass);
        var slot = JsonNode.Parse(File.ReadAllText(Path.Combine(SessionDirectory(fixture), "correction.json")))!;
        Assert.Equal(fixture.Key("older"), slot["predecessor"]!["key"]!.GetValue<string>());
        await ApplyHoldAsync(glass, "unhold", "fairness-unhold");
        using var scheduler = fixture.Scheduler();
        await scheduler.TickOnceAsync(Ct);
        await WaitForAdvicePassAsync(scheduler);
        Assert.Equal(2, fixture.Calls.Count);
        Assert.NotNull(fixture.Receipt("older"));
        Assert.True((await fixture.RowAsync("newer")).StoppedWorkJudgment!.FollowContinuationPending);
        Assert.Equal("delivered", (await LookupCorrectionAsync(fixture)).Receipt!.State);
        await AcceptCorrectionAsync(fixture, glass, "second");
        await scheduler.TickOnceAsync(Ct);
        await WaitForAdvicePassAsync(scheduler);
        Assert.NotNull(fixture.Receipt("newer"));
        Assert.Equal(4, fixture.Calls.Count);
        Assert.Equal("delivered", (await LookupCorrectionAsync(fixture, "second")).Receipt!.State);
        await scheduler.DrainStoppedWorkAdviceAsync();
    }

    [Fact]
    public async Task Issue2666_Direct_follow_cannot_bypass_an_accepted_correction()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        await using var glass = await GlassFixture.StartAsync(fixture);
        await AcceptCorrectionAsync(fixture, glass);
        await fixture.HaltAsync("later-direct", notify: false);
        using var scheduler = fixture.Scheduler();
        await scheduler.ReconcileStoppedWorkAdviceAsync(Ct);
        var result = await fixture.FollowAsync("later-direct");
        Assert.Equal("refused", result.GetProperty("status").GetString());
        Assert.Empty(fixture.Calls);
        Assert.Equal("delivered", (await fixture.DeliverCorrectionAsync())!.Status);
        Assert.Equal("delivered", (await fixture.FollowAsync("later-direct")).GetProperty("status").GetString());
        Assert.Equal(2, fixture.Calls.Count);
        await scheduler.DrainStoppedWorkAdviceAsync();
    }

    [Fact]
    public async Task Issue2666_Planted_correction_decision_is_corruption_in_lookup_Resume_and_recovery()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        fixture.Reply = "ReplaceReview";
        await using var glass = await GlassFixture.StartAsync(fixture);
        await AcceptCorrectionAsync(fixture, glass);
        await fixture.DeliverCorrectionAsync();
        var decision = CorrectionEvidence(fixture, "decision.json");
        File.WriteAllText(decision, "{}");
        Assert.Equal("unknown", (await LookupCorrectionAsync(fixture)).Outcome);
        Assert.Throws<IOException>(() => ConductorFollowSession.ReadDecisionEvidence(Path.GetDirectoryName(decision)!));
        using (var detached = await glass.DetachAsync(DetachBody(await glass.StatusAsync())))
            Assert.Equal(HttpStatusCode.OK, detached.StatusCode);
        var row = await glass.StatusAsync();
        using (var resumed = await glass.ResumeAsync(DetachBody(row)))
            Assert.Equal(HttpStatusCode.Conflict, resumed.StatusCode);
        FileCleanup.EnsureDeleted(decision);
        Assert.Single(fixture.Calls);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("alias")]
    [InlineData("duplicate")]
    [InlineData("blank")]
    [InlineData("overlong")]
    [InlineData("issuer")]
    [InlineData("stale")]
    public async Task Issue2666_Strict_HTTP_refuses_malformed_correction_without_calls(string change)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        await using var glass = await GlassFixture.StartAsync(fixture);
        var body = CorrectionBody(await glass.StatusAsync(), "strict");
        var json = JsonNode.Parse(body)!.AsObject();
        if (change == "unknown") json["path"] = fixture.Root;
        if (change == "issuer") json["issuer"] = Operator;
        if (change == "stale") json["attachmentId"] = new string('f', 32);
        if (change == "blank") json["text"] = " ";
        if (change == "overlong") json["text"] = new string('x', 4096);
        body = json.ToJsonString();
        if (change == "alias") body = body.Replace("\"requestId\"", "\"RequestId\"", StringComparison.Ordinal);
        if (change == "duplicate") body = body.Replace("\"requestId\":\"strict\"", "\"requestId\":\"strict\",\"requestId\":\"strict\"", StringComparison.Ordinal);
        using var response = await glass.ControlAsync("correct", body);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("absent", (await LookupCorrectionAsync(fixture, "strict")).Outcome);
        Assert.Empty(fixture.Calls);
    }

    [Fact]
    public async Task Issue2666_HTTP_auth_lost_reply_privacy_and_lookup_survive_authority_change()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        await using var glass = await GlassFixture.StartAsync(fixture);
        var body = CorrectionBody(await glass.StatusAsync(), "lost-http", "private operator text");
        using (var denied = await glass.ControlAsync("correct", body, null)) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var denied = await glass.CorrectionReceiptAsync("lost-http", null)) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        glass.LoseCorrectionReply = true;
        try { using var lost = await glass.ControlAsync("correct", body); }
        catch (HttpRequestException) { }
        glass.LoseCorrectionReply = false;
        var accepted = await LookupCorrectionAsync(fixture, "lost-http");
        Assert.Equal("queued", accepted.Receipt!.State);
        using (var exact = await glass.CorrectionReceiptAsync("lost-http"))
        {
            Assert.Equal(HttpStatusCode.OK, exact.StatusCode);
            Assert.Equal("no-store", exact.Headers.CacheControl!.ToString());
            var json = await exact.Content.ReadAsStringAsync(Ct);
            Assert.DoesNotContain("private operator text", json);
            Assert.DoesNotContain(fixture.Root, json);
            Assert.DoesNotContain("sessionId", json);
        }
        using (var detached = await glass.DetachAsync(DetachBody(await glass.StatusAsync()))) Assert.Equal(HttpStatusCode.OK, detached.StatusCode);
        await fixture.DeliverCorrectionAsync();
        Assert.Equal("not-delivered", (await LookupCorrectionAsync(fixture, "lost-http")).Receipt!.State);
        Assert.True((await ConductorFollowSession.CorrectFromGlassAsync(body, fixture.Root, Operator, Ct)).Replayed);
        var changed = JsonNode.Parse(body)!.AsObject();
        changed["repository"] = "github.com/test/another-target";
        await Assert.ThrowsAsync<CliArgumentException>(() => ConductorFollowSession.CorrectFromGlassAsync(changed.ToJsonString(), fixture.Root, Operator, Ct));
        Assert.Empty(fixture.Calls);
    }
}
