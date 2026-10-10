using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Baton.Cli.Daemon;
using Baton.Conductor;
using Baton.Queue;
using Baton.Status;
using Baton.Vendors;

namespace Baton.Cli.Tests;

public sealed partial class ConductorFollowDeliveryTests
{
    private static Task? AdviceTask(QueueSchedulerService scheduler) =>
        (Task?)typeof(QueueSchedulerService).GetField("_stoppedWorkTask", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(scheduler);

    private static async Task WaitForAdvicePassAsync(QueueSchedulerService scheduler)
    {
        if (AdviceTask(scheduler) is { } task)
            await task.WaitAsync(TimeSpan.FromSeconds(30), Ct); // wait-ok: Bound the offline advisory pass independently of scheduler liveness.
    }

    private static Task EnableLegacyAndAutomaticAsync() => DaemonSettingsStore.SaveAsync(new DaemonSettings
    {
        Queue = new QueueSettings
        {
            StoppedWorkAdvice = new Dictionary<string, JsonElement> { [Repository] = JsonSerializer.SerializeToElement(true) },
            AutomaticMissingVerdictReplacementReview = new Dictionary<string, JsonElement> { [Repository] = JsonSerializer.SerializeToElement(true) },
        },
    }, BatonPaths.SettingsFile, Ct);

    [Fact]
    public async Task Issue2662_Repair_refused_first_event_does_not_starve_later_event_or_ordinary_replay()
    {
        using var fixture = await Fixture.CreateAsync();
        await EnableLegacyAndAutomaticAsync();
        var legacy = await fixture.HaltAsync("ordinary-replay", notify: false, owned: false);
        using var scheduler = fixture.Scheduler();
        await scheduler.ReconcileStoppedWorkAdviceAsync(Ct);
        await fixture.LegacyAsync(legacy.Tag, () => { });
        var legacyDirectory = fixture.Store.GetStoppedWorkAdviceEvidenceDirectory(fixture.Key(legacy.Tag));
        await fixture.CommandAsync("attach");
        fixture.Reply = "ReplaceReview";
        await using var glass = await GlassFixture.StartAsync(fixture);
        await ApplyHoldAsync(glass, "hold", "fair-hold");
        var staleWorkspace = Path.Combine(fixture.Root, "stale-workspace");
        Directory.CreateDirectory(staleWorkspace);
        await fixture.HaltAsync("stale-first", notify: false, workspace: staleWorkspace);
        var valid = await fixture.HaltAsync("valid-later", notify: false);
        fixture.PullRequestHeadForWorkspace = workspace => workspace == staleWorkspace ? new string('f', 40) : Head;
        await ApplyHoldAsync(glass, "unhold", "fair-unhold");
        for (var pass = 0; pass < 3; pass++)
        {
            await scheduler.TickOnceAsync(Ct);
            await WaitForAdvicePassAsync(scheduler);
        }
        var continued = await fixture.RowAsync(valid.Tag);
        Assert.NotNull(continued.ReplacementReviewAction);
        Assert.False(continued.StoppedWorkJudgment!.FollowContinuationPending);
        Assert.Single(fixture.Calls);
        Assert.Equal(0, fixture.LegacyCalls);
        Assert.True(File.Exists(Path.Combine(legacyDirectory, "source-checked")));
        var refused = await fixture.RowAsync("stale-first");
        Assert.True(refused.StoppedWorkJudgment!.FollowContinuationPending);
        Assert.Equal("Session admission or retained state refused; no new turn admitted.", refused.StoppedWorkJudgment.FollowContinuationWait);
        Assert.Equal("Owner must inspect retained evidence and current eligibility; uncertain launches have no retry path.", refused.StoppedWorkJudgment.FollowContinuationTrigger);
        Assert.Null(refused.ReplacementReviewAction);
        Assert.Equal(valid.AutomaticFixUsed, continued.AutomaticFixUsed);
    }

    [Fact]
    public async Task Issue2662_Repair_hosted_pass_drains_a_legacy_notification_arriving_while_busy()
    {
        using var fixture = await Fixture.CreateAsync();
        await EnableLegacyAndAutomaticAsync();
        var legacy = await fixture.HaltAsync("raced-legacy", notify: false, owned: false);
        await fixture.CommandAsync("attach");
        fixture.Reply = "Hold";
        await using var glass = await GlassFixture.StartAsync(fixture);
        await ApplyHoldAsync(glass, "hold", "drain-hold");
        await fixture.HaltAsync("busy-hosted", notify: false);
        await ApplyHoldAsync(glass, "unhold", "drain-unhold");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.WaitInBroker = async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        using var scheduler = fixture.Scheduler(legacy: (obligation, request, _, _, _) =>
        {
            delivered.TrySetResult();
            return Task.FromResult(new RetainedStoppedWorkAdviceResponse(new(obligation.ObligationId, Repository,
                request.Tag, request.AttemptId.Value, request.ContextSha256, StoppedWorkAdviceChoice.Hold, "Retained advice"),
                StoppedWorkAdviceProviderDescriptor.Codex.Adapter, StoppedWorkAdviceProviderDescriptor.Codex.Model,
                StoppedWorkAdviceProviderDescriptor.Codex.Effort, DateTimeOffset.UtcNow));
        });
        await scheduler.TickOnceAsync(Ct);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct); // wait-ok: Bound the offline broker rendezvous.
            await scheduler.NotifyOwnedHaltAsync(legacy, Ct);
            Assert.Equal(0, fixture.LegacyCalls);
        }
        finally { release.TrySetResult(); }
        await WaitForAdvicePassAsync(scheduler);
        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct); // wait-ok: Detect a lost notification without another tick or restart.
        await WaitForAdvicePassAsync(scheduler);
        Assert.Equal(1, fixture.LegacyCalls);
        Assert.Single(fixture.Calls);
        Assert.True(File.Exists(Path.Combine(fixture.Store.GetStoppedWorkAdviceEvidenceDirectory(fixture.Key(legacy.Tag)),
            "legacy-notification-handled.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Issue2662_Repair_frozen_conversation_preserves_an_already_admitted_action(bool hold)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.EnableAutomaticAsync();
        await fixture.CommandAsync("attach");
        fixture.Reply = "ReplaceReview";
        await AdmitControlFollowAsync(fixture, "admitted-before-freeze");
        var original = await fixture.RowAsync("admitted-before-freeze");
        await using var glass = await GlassFixture.StartAsync(fixture);
        if (hold) await ApplyHoldAsync(glass, "hold", "freeze-hold");
        var launches = 0;
        using var scheduler = fixture.Scheduler(launch: (request, _) =>
        {
            launches++;
            return Task.FromResult(new QueueLaunchOutcome(request.RoomDirectory));
        });
        if (hold) await scheduler.ReconcileReplacementReviewActionsAsync(Ct);
        // Freeze on a different real uncertain turn; the earlier admitted decision remains exact.
        if (hold) await ApplyHoldAsync(glass, "unhold", "freeze-unhold");
        fixture.Interrupt = true;
        await fixture.HaltAsync("unrelated-uncertain", notify: false);
        await scheduler.NotifyOwnedHaltAsync(await fixture.RowAsync("unrelated-uncertain"), Ct);
        fixture.Interrupt = false;
        await scheduler.TickOnceAsync(Ct);
        await scheduler.TickOnceAsync(Ct);
        var issued = await fixture.RowAsync("admitted-before-freeze");
        Assert.Equal(1, launches);
        Assert.Null(issued.ReplacementReviewAction!.BlockedReason);
        Assert.NotNull(issued.ReplacementReviewAction.ReplacementAttemptId);
        Assert.Equal(original.Round, issued.Round);
        Assert.Equal(original.AutomaticFixUsed, issued.AutomaticFixUsed);
        Assert.Equal(original.ReplacementReviewAction!.EvidenceDigest, issued.ReplacementReviewAction.EvidenceDigest);
        var status = await glass.StatusAsync();
        Assert.Equal("frozen", status.GetProperty("state").GetString());
        Assert.False(status.GetProperty("held").GetBoolean());
        Assert.Equal("uncertain", (await fixture.FollowAsync("unrelated-uncertain")).GetProperty("status").GetString());
        Assert.Equal(2, fixture.Calls.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Issue2662_Repair_mid_read_transition_clears_all_displayed_control_eligibility(bool held)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        await using var healthy = await GlassFixture.StartAsync(fixture);
        if (held) await ApplyHoldAsync(healthy, "hold", "initial-held");
        var displayed = await healthy.StatusAsync();
        var changed = false;
        await using var racing = await GlassFixture.StartAsync(fixture, async (_, _) =>
        {
            if (!changed)
            {
                changed = true;
                var operation = held ? "unhold" : "hold";
                using var response = await healthy.ControlAsync(operation, HoldBody(displayed, operation, "mid-read"));
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
            return Identity;
        });
        var status = await racing.StatusAsync();
        Assert.Equal("unavailable", status.GetProperty("state").GetString());
        Assert.False(status.GetProperty("stopEligible").GetBoolean());
        Assert.False(status.GetProperty("takeoverEligible").GetBoolean());
        Assert.False(status.GetProperty("holdEligible").GetBoolean());
        Assert.False(status.GetProperty("unholdEligible").GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Issue2662_Repair_unattached_halts_have_no_hosted_pending_flag(bool owned)
    {
        using var fixture = await Fixture.CreateAsync();
        await EnableLegacyAndAutomaticAsync();
        var halted = await fixture.HaltAsync("unattached", notify: false, owned: owned);
        Assert.NotNull(halted.StoppedWorkJudgment);
        Assert.Null(halted.StoppedWorkJudgment.FollowAttachmentId);
        Assert.False(halted.StoppedWorkJudgment.FollowContinuationPending);
    }
}
