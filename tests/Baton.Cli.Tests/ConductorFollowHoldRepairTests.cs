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
        foreach (var hosted in await scheduler.SnapshotHostedTasksAsync())
            await hosted.WaitAsync(TimeSpan.FromSeconds(30), Ct); // wait-ok: Bound each acquisition's offline hosted pass.
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
        await fixture.HaltAsync("stale-first", notify: false, pullRequest: 78);
        var valid = await fixture.HaltAsync("valid-later", notify: false);
        var staleHeadChecks = 0;
        fixture.PullRequestHeadForNumber = number =>
        {
            if (number != 78) return Head;
            staleHeadChecks++;
            return new string('f', 40);
        };
        await ApplyHoldAsync(glass, "unhold", "fair-unhold");
        for (var pass = 0; pass < 3; pass++)
        {
            await scheduler.TickOnceAsync(Ct);
            await WaitForAdvicePassAsync(scheduler);
        }
        await scheduler.DrainStoppedWorkAdviceAsync();
        var continued = await fixture.RowAsync(valid.Tag);
        Assert.NotNull(continued.ReplacementReviewAction);
        Assert.False(continued.StoppedWorkJudgment!.FollowContinuationPending);
        Assert.Single(fixture.Calls);
        Assert.Equal(0, fixture.LegacyCalls);
        Assert.True(File.Exists(Path.Combine(legacyDirectory, "source-checked")));
        var refused = await fixture.RowAsync("stale-first");
        Assert.True(refused.StoppedWorkJudgment!.FollowContinuationPending);
        Assert.Equal("Claim, trust, source, or state admission refused; no vendor call.", refused.StoppedWorkJudgment.FollowContinuationWait);
        Assert.Equal("Owner must inspect retained evidence and current eligibility; uncertain launches have no retry path.", refused.StoppedWorkJudgment.FollowContinuationTrigger);
        Assert.Null(refused.ReplacementReviewAction);
        Assert.Equal(valid.AutomaticFixUsed, continued.AutomaticFixUsed);
        Assert.True(staleHeadChecks >= 2, "Retained mutable-head refusal is rechecked on later scheduler passes.");
        fixture.PullRequestHeadForNumber = _ => Head;
        using var recovered = fixture.Scheduler();
        await recovered.TickOnceAsync(Ct);
        await WaitForAdvicePassAsync(recovered);
        var newlyEligible = await fixture.RowAsync("stale-first");
        Assert.NotNull(newlyEligible.ReplacementReviewAction);
        Assert.False(newlyEligible.StoppedWorkJudgment!.FollowContinuationPending);
        Assert.Equal(refused.AutomaticFixUsed, newlyEligible.AutomaticFixUsed);
        Assert.Equal(2, fixture.Calls.Count);
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
        await scheduler.DrainStoppedWorkAdviceAsync();
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
        var statePath = Path.Combine(SessionDirectory(fixture), "session.json");
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var priorState = JsonSerializer.Deserialize<ConductorFollowState>(File.ReadAllText(statePath), json)!;
        fixture.Interrupt = true;
        await fixture.HaltAsync("unrelated-uncertain", notify: false);
        await scheduler.NotifyOwnedHaltAsync(await fixture.RowAsync("unrelated-uncertain"), Ct);
        fixture.Interrupt = false;
        Assert.Equal("uncertain", (await fixture.FollowAsync("unrelated-uncertain")).GetProperty("status").GetString());
        var frozenState = JsonSerializer.Deserialize<ConductorFollowState>(File.ReadAllText(statePath), json)!;
        Assert.True(frozenState.Frozen);
        Assert.Equal(priorState, frozenState with { Frozen = priorState.Frozen });
        await scheduler.TickOnceAsync(Ct);
        await scheduler.TickOnceAsync(Ct);
        await WaitForAdvicePassAsync(scheduler);
        await scheduler.DrainStoppedWorkAdviceAsync();
        var issued = await fixture.RowAsync("admitted-before-freeze");
        Assert.True(issued.ReplacementReviewAction!.BlockedReason is null, issued.ReplacementReviewAction.BlockedReason);
        Assert.Equal(1, launches);
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
    public async Task Issue2662_Repair_Hold_preserves_the_prior_marker_free_action_trigger(bool paused)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.EnableAutomaticAsync();
        await fixture.CommandAsync("attach");
        fixture.Reply = "ReplaceReview";
        await AdmitControlFollowAsync(fixture, "retained-trigger");
        using var scheduler = fixture.Scheduler();
        if (paused)
        {
            await fixture.ChangeAsync("retained-trigger", "opt-in");
            await scheduler.ReconcileReplacementReviewActionsAsync(Ct);
        }
        var original = (await fixture.RowAsync("retained-trigger")).ReplacementReviewAction!;
        Assert.Equal(paused, original.PausedReason is not null);
        await using var glass = await GlassFixture.StartAsync(fixture);
        await ApplyHoldAsync(glass, "hold", "trigger-hold");
        await scheduler.ReconcileReplacementReviewActionsAsync(Ct);
        var held = (await fixture.RowAsync("retained-trigger")).ReplacementReviewAction!;
        Assert.True(held.HeldPending);
        Assert.Equal(original.NextTrigger, held.NextTrigger);
        var displayed = (await glass.StatusAsync()).GetProperty("actions").EnumerateArray()
            .Single(action => action.GetProperty("tag").GetString() == "retained-trigger");
        Assert.Equal("Unhold this acquisition; the scheduler rechecks source, head, grants, opt-in and runway before launch.",
            displayed.GetProperty("nextTrigger").GetString());
        await ApplyHoldAsync(glass, "unhold", "trigger-unhold");
        await scheduler.ReconcileReplacementReviewActionsAsync(Ct);
        var continued = (await fixture.RowAsync("retained-trigger")).ReplacementReviewAction!;
        Assert.False(continued.HeldPending);
        Assert.Equal(original.NextTrigger, continued.NextTrigger);
        Assert.Equal(original.PausedReason, continued.PausedReason);
        Assert.Single(fixture.Calls);
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
