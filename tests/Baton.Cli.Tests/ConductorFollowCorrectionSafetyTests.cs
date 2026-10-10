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
    [Fact]
    public async Task Issue2666_Newer_Hold_wins_final_cutoff_and_the_bypassed_fence_negative_control_spends()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        await using var glass = await GlassFixture.StartAsync(fixture);
        await AcceptCorrectionAsync(fixture, glass);
        ConductorFollowSession.AfterCorrectionPersistence = stage =>
        {
            if (stage == "prelaunch")
            {
                var registration = CorrectionRegistration(fixture);
                var claim = ConductorClaimStore.GetClaimAsync(Identity, fixture.Root, Ct).GetAwaiter().GetResult()!;
                var row = JsonNode.Parse(registration.GetRawText())!.AsObject();
                row["controlRevision"] = claim.ControlRevision;
                ConductorFollowSession.ControlFromGlassAsync(HoldBody(JsonSerializer.SerializeToElement(row), "hold", "newer-cutoff-hold"),
                    fixture.Root, Operator, "hold", Ct).GetAwaiter().GetResult();
            }
        };
        try { Assert.Equal("held-pending", (await fixture.DeliverCorrectionAsync())!.Status); }
        finally { ConductorFollowSession.AfterCorrectionPersistence = null; }
        Assert.Empty(fixture.Calls);
        Assert.False(File.Exists(CorrectionEvidence(fixture, "launch.json")));
        // Exercise the same offline native transport directly, deliberately omitting the host fence.
        // The fixture can spend under Hold, so zero calls above measures the real cutoff.
        using var output = new StringWriter();
        using var error = new StringWriter();
        var controlDirectory = Path.Combine(fixture.Root, "negative-cutoff-control");
        Directory.CreateDirectory(controlDirectory);
        var configuration = new CodexBrokerConfiguration(fixture.Workspace, "gpt-5.6-luna", "low", null, false,
            new PermissionGrant(ReadFiles: true), ["response.txt"], false);
        await fixture.CorrectionBroker(configuration, "operator correction", controlDirectory,
            [CorrectionEvidence(fixture, "source.json")], output, error, Ct, (_, _) => Task.CompletedTask);
        Assert.Single(fixture.Calls);
        Assert.True((await ConductorClaimStore.GetClaimAsync(Identity, fixture.Root, Ct))!.Held);
    }

    [Theory]
    [InlineData("queue")]
    [InlineData("runway")]
    public async Task Issue2666_Existing_idle_tick_rechecks_clearable_admission_without_new_halt(string blocker)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        await using var glass = await GlassFixture.StartAsync(fixture);
        await AcceptCorrectionAsync(fixture, glass);
        if (blocker == "queue") await QueueStore.MutateAsync(BatonPaths.QueueFile, q => q with { Held = true }, Ct);
        else fixture.Usage(DateTimeOffset.UtcNow, 99);
        using var scheduler = fixture.Scheduler();
        await scheduler.TickOnceAsync(Ct);
        await WaitForAdvicePassAsync(scheduler);
        Assert.Empty(fixture.Calls);
        var waiting = (await LookupCorrectionAsync(fixture)).Receipt!;
        Assert.Equal("waiting", waiting.State);
        Assert.Contains(blocker == "queue" ? "Global queue Hold" : "runway", waiting.Reason, StringComparison.Ordinal);
        if (blocker == "queue") await QueueStore.MutateAsync(BatonPaths.QueueFile, q => q with { Held = false }, Ct);
        else fixture.Usage(DateTimeOffset.UtcNow, 1);
        await scheduler.TickOnceAsync(Ct);
        await WaitForAdvicePassAsync(scheduler);
        Assert.Single(fixture.Calls);
        Assert.Equal("delivered", (await LookupCorrectionAsync(fixture)).Receipt!.State);
        await scheduler.DrainStoppedWorkAdviceAsync();
    }

    [Theory]
    [InlineData("ReplaceReview")]
    [InlineData("applied")]
    public async Task Issue2666_Correction_recovery_Resume_and_Unhold_never_create_action_or_application_proof(string reply)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.EnableAutomaticAsync();
        await fixture.CommandAsync("attach");
        fixture.Reply = reply;
        await using var glass = await GlassFixture.StartAsync(fixture);
        var accepted = await AcceptCorrectionAsync(fixture, glass);
        var requestBefore = File.ReadAllBytes(Path.Combine(SessionDirectory(fixture), "request.json"));
        await fixture.DeliverCorrectionAsync();
        await fixture.DeliverCorrectionAsync();
        using var scheduler = fixture.Scheduler();
        await scheduler.RecoverAttachedFollowAsync(Ct);
        await WaitForAdvicePassAsync(scheduler);
        using (var detached = await glass.DetachAsync(DetachBody(await glass.StatusAsync()))) Assert.Equal(HttpStatusCode.OK, detached.StatusCode);
        using (var resumed = await glass.ResumeAsync(DetachBody(await glass.StatusAsync()))) Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
        await ApplyHoldAsync(glass, "hold", "output-hold");
        await ApplyHoldAsync(glass, "unhold", "output-unhold");
        await scheduler.TickOnceAsync(Ct);
        await WaitForAdvicePassAsync(scheduler);
        var found = await LookupCorrectionAsync(fixture);
        Assert.Equal(accepted.Receipt!.AcceptedAt, found.Receipt!.AcceptedAt);
        Assert.Equal("delivered", found.Receipt.State);
        Assert.Equal("not verified", found.Receipt.Application);
        Assert.Single(fixture.Calls);
        Assert.False(File.Exists(CorrectionEvidence(fixture, "decision.json")));
        Assert.Empty((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items);
        Assert.Equal(requestBefore, File.ReadAllBytes(Path.Combine(SessionDirectory(fixture), "request.json")));
        await scheduler.DrainStoppedWorkAdviceAsync();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("unknown")]
    [InlineData("conflicting")]
    [InlineData("alias")]
    public async Task Issue2666_Correction_kind_cannot_enter_legacy_decision_repair(string kind)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        fixture.Reply = "ReplaceReview";
        await using var glass = await GlassFixture.StartAsync(fixture);
        await AcceptCorrectionAsync(fixture, glass);
        await fixture.DeliverCorrectionAsync();
        var identityPath = CorrectionEvidence(fixture, "identity.json");
        var identity = JsonNode.Parse(File.ReadAllText(identityPath))!.AsObject();
        if (kind == "missing") identity.Remove("kind");
        if (kind == "null") identity["kind"] = null;
        if (kind == "unknown") identity["kind"] = "unknown";
        if (kind == "conflicting") identity["kind"] = "halted-task";
        if (kind == "alias") { identity.Remove("kind"); identity["Kind"] = "correction"; }
        File.WriteAllText(identityPath, identity.ToJsonString());
        Assert.Equal("unknown", (await LookupCorrectionAsync(fixture)).Outcome);
        Assert.NotNull(Record.Exception(() => ConductorFollowSession.ReadDecisionEvidence(Path.GetDirectoryName(identityPath)!)));
        await fixture.DeliverCorrectionAsync();
        Assert.Single(fixture.Calls);
        Assert.False(File.Exists(CorrectionEvidence(fixture, "decision.json")));
    }

    [Fact]
    public async Task Issue2666_Exact_legacy_halted_source_without_kind_remains_actionable()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.EnableAutomaticAsync();
        await fixture.CommandAsync("attach");
        fixture.Reply = "ReplaceReview";
        await fixture.HaltAsync("legacy-kind", notify: false);
        using var scheduler = fixture.Scheduler();
        await scheduler.ReconcileStoppedWorkAdviceAsync(Ct);
        Assert.Equal("delivered", (await fixture.FollowAsync("legacy-kind")).GetProperty("status").GetString());
        var path = fixture.EventEvidencePath("legacy-kind", "identity.json");
        var identity = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        identity.Remove("kind");
        File.WriteAllText(path, identity.ToJsonString());
        Assert.NotNull(ConductorFollowSession.ReadDecisionEvidence(Path.GetDirectoryName(path)!));
        Assert.Equal("replayed", (await fixture.FollowAsync("legacy-kind")).GetProperty("status").GetString());
        await scheduler.RecoverAttachedFollowAsync(Ct);
        await WaitForAdvicePassAsync(scheduler);
        Assert.NotNull((await fixture.RowAsync("legacy-kind")).ReplacementReviewAction);
        Assert.Single(fixture.Calls);
        await scheduler.DrainStoppedWorkAdviceAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Issue2666_Invalid_or_uncertain_predecessor_never_mutates_original_obligation(bool uncertain)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        await using var glass = await GlassFixture.StartAsync(fixture);
        await ApplyHoldAsync(glass, "hold", "predecessor-hold");
        await fixture.HaltAsync("predecessor", notify: false);
        var accepted = await AcceptCorrectionAsync(fixture, glass);
        using var scheduler = fixture.Scheduler();
        await scheduler.ReconcileStoppedWorkAdviceAsync(Ct);
        var original = await fixture.Store.ReadAsync(fixture.Key("predecessor"), Ct);
        if (uncertain) fixture.Interrupt = true;
        else fixture.PullRequestHeadForNumber = _ => new string('f', 40);
        await ApplyHoldAsync(glass, "unhold", "predecessor-unhold");
        await scheduler.TickOnceAsync(Ct);
        await WaitForAdvicePassAsync(scheduler);
        Assert.Equal(uncertain ? "uncertain" : "delivered", (await LookupCorrectionAsync(fixture)).Receipt!.State);
        Assert.Equal(original, await fixture.Store.ReadAsync(fixture.Key("predecessor"), Ct));
        Assert.Equal(accepted.Receipt!.ReceiptId, (await LookupCorrectionAsync(fixture)).Receipt!.ReceiptId);
        Assert.Single(fixture.Calls);
        await scheduler.DrainStoppedWorkAdviceAsync();
    }
}
