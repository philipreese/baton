using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Baton.Accounting;
using Baton.Conductor;
using Baton.Queue;
using Baton.Cli.Daemon;
using Baton.Status;

namespace Baton.Cli.Tests;

public sealed partial class ConductorFollowDeliveryTests
{
    [Theory]
    [InlineData("head")]
    [InlineData("closed")]
    [InlineData("retired")]
    public async Task Glass_observed_completion_survives_later_pr_changes_without_rechecks_or_queue_churn(string change)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.EnableAutomaticAsync();
        await fixture.CommandAsync("attach");
        fixture.Reply = "ReplaceReview";
        await AdmitControlFollowAsync(fixture, "historical");
        var launches = 0;
        using var scheduler = fixture.Scheduler(launch: (request, _) =>
        {
            launches++;
            return Task.FromResult(new QueueLaunchOutcome(request.RoomDirectory));
        });
        await scheduler.TickOnceAsync(Ct);
        await fixture.CompleteReviewAsync("historical");
        await scheduler.TickOnceAsync(Ct);
        var observed = (await fixture.RowAsync("historical")).ReplacementReviewAction!;
        Assert.NotNull(observed.ActionObservedAt);
        Assert.Null(observed.BlockedReason);
        Assert.Equal(ConductorObligationStatus.ActionObserved, (await fixture.Store.ReadAsync(observed.ObligationKey, Ct))!.Status);
        if (change == "head") await fixture.ChangeAsync("historical", "head");
        if (change == "closed") fixture.PullRequestState = "MERGED";
        await QueueStore.MutateAsync(BatonPaths.QueueFile, queue => queue with
        {
            Held = true,
            Items = queue.Items.Select(item => item.Tag == "historical" ? item with
            {
                Retirement = change == "retired" ? new(QueueRetirement.Operator, DateTimeOffset.UtcNow, "Retained history") : item.Retirement,
            } : item).ToList(),
        }, Ct);
        var queueBytes = File.ReadAllBytes(BatonPaths.QueueFile);
        var claimPath = Path.Combine(fixture.Root, Identity.FileSlug, BatonPaths.ConductorClaimFileName);
        var claimBytes = File.ReadAllBytes(claimPath);
        var obligation = await fixture.Store.ReadAsync(observed.ObligationKey, Ct);
        var stamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(BatonPaths.QueueFile, stamp);
        var remoteCalls = fixture.RemoteCalls;
        await scheduler.ReconcileReplacementReviewActionsAsync(Ct);
        await scheduler.ReconcileReplacementReviewActionsAsync(Ct);
        Assert.Equal(remoteCalls, fixture.RemoteCalls);
        Assert.Equal(queueBytes, File.ReadAllBytes(BatonPaths.QueueFile));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(BatonPaths.QueueFile));
        // Freeze ordinary PR lifecycle advancement so subsequent ticks measure retained-result churn.
        await QueueStore.MutateAsync(BatonPaths.QueueFile, queue => queue with
        {
            Items = queue.Items.Select(item => item.Tag == "historical" ? item with
            {
                Retirement = new(QueueRetirement.Operator, DateTimeOffset.UtcNow, "Retained history"),
            } : item).ToList(),
        }, Ct);
        queueBytes = File.ReadAllBytes(BatonPaths.QueueFile);
        File.SetLastWriteTimeUtc(BatonPaths.QueueFile, stamp);
        for (var tick = 0; tick < 2; tick++) await scheduler.TickOnceAsync(Ct);
        Assert.Equal(observed, (await fixture.RowAsync("historical")).ReplacementReviewAction);
        Assert.Equal(queueBytes, File.ReadAllBytes(BatonPaths.QueueFile));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(BatonPaths.QueueFile));
        Assert.Equal(claimBytes, File.ReadAllBytes(claimPath));
        Assert.Equal(obligation, await fixture.Store.ReadAsync(observed.ObligationKey, Ct));
        await using var glass = await GlassFixture.StartAsync(fixture);
        var projection = Assert.Single((await glass.StatusAsync()).GetProperty("actions").EnumerateArray());
        Assert.Equal("observed", projection.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, projection.GetProperty("reason").ValueKind);
        Assert.Equal(JsonValueKind.Null, projection.GetProperty("nextTrigger").ValueKind);
        Assert.Equal(1, launches);
        Assert.Single(fixture.Calls);
    }

    [Theory]
    [InlineData("stop", "prior-acquisition")]
    [InlineData("takeover", "taken-over")]
    public async Task Glass_prior_attachment_only_claims_transfer_for_a_matching_takeover_receipt(string verb, string expected)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        await using var glass = await GlassFixture.StartAsync(fixture);
        var prior = await glass.StatusAsync();
        using var response = await glass.ControlAsync(verb, ControlBody(prior, verb));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        if (verb == "stop")
        {
            await ConductorClaimStore.ReleaseAsync(Identity, "holder", "explicit new acquisition", fixture.Root, cancellationToken: Ct);
            await ConductorClaimStore.ClaimAsync(Identity, "holder", fixture.Root, cancellationToken: Ct);
        }
        var current = await glass.StatusAsync();
        Assert.Equal(expected, current.GetProperty("state").GetString());
        Assert.Equal(prior.GetProperty("claimGeneration").GetString(), current.GetProperty("retainedGeneration").GetString());
        Assert.True(current.GetProperty("historicalProvider").GetBoolean());
        Assert.False(current.GetProperty("resumeEligible").GetBoolean());
        Assert.False(current.GetProperty("stopEligible").GetBoolean());
        Assert.Equal(verb, Assert.Single(current.GetProperty("controls").EnumerateArray()).GetProperty("receipt").GetProperty("operation").GetString());
        Assert.Empty(fixture.Calls);
    }

    [Fact]
    public async Task Glass_unobserved_proof_stays_unresolved_after_head_change_without_unchanged_writes()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.EnableAutomaticAsync();
        await fixture.CommandAsync("attach");
        fixture.Reply = "ReplaceReview";
        await AdmitControlFollowAsync(fixture, "unobserved");
        var advancer = fixture.Advancer();
        using var scheduler = fixture.Scheduler(advancer, launch: (request, _) => Task.FromResult(new QueueLaunchOutcome(request.RoomDirectory)));
        await scheduler.TickOnceAsync(Ct);
        await fixture.CompleteReviewAsync("unobserved");
        advancer.ReplacementReviewAfterProofPersisted = () =>
        {
            fixture.ChangeAsync("unobserved", "head").GetAwaiter().GetResult();
            throw new IOException("Offline crash before observation");
        };
        await scheduler.TickOnceAsync(Ct);
        var proof = (await fixture.RowAsync("unobserved")).ReplacementReviewAction!;
        Assert.NotNull(proof.CompletionProof);
        Assert.Null(proof.ActionObservedAt);
        await scheduler.ReconcileReplacementReviewActionsAsync(Ct);
        var unresolved = (await fixture.RowAsync("unobserved")).ReplacementReviewAction!;
        Assert.Null(unresolved.ActionObservedAt);
        Assert.NotNull(unresolved.BlockedReason);
        Assert.NotNull(unresolved.NextTrigger);
        Assert.Equal(ConductorObligationStatus.Pending, (await fixture.Store.ReadAsync(proof.ObligationKey, Ct))!.Status);
        var bytes = File.ReadAllBytes(BatonPaths.QueueFile);
        var stamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(BatonPaths.QueueFile, stamp);
        await scheduler.ReconcileReplacementReviewActionsAsync(Ct);
        await scheduler.ReconcileReplacementReviewActionsAsync(Ct);
        Assert.Equal(bytes, File.ReadAllBytes(BatonPaths.QueueFile));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(BatonPaths.QueueFile));
        Assert.Single(fixture.Calls);
    }

    [Theory]
    [InlineData("CORP\\alice")]
    [InlineData("/alice")]
    [InlineData("owner:/alice")]
    [InlineData("alice@example.test")]
    public async Task Glass_takeover_destination_is_refused_or_remains_readable_with_exact_replay(string holder)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        await using var glass = await GlassFixture.StartAsync(fixture);
        var body = JsonNode.Parse(ControlBody(await glass.StatusAsync(), "takeover"))!;
        body["destinationHolder"] = holder;
        var claimPath = Path.Combine(fixture.Root, Identity.FileSlug, BatonPaths.ConductorClaimFileName);
        var claimBytes = File.ReadAllBytes(claimPath);
        var registration = File.ReadAllBytes(fixture.RegistrationPath);
        using var response = await glass.ControlAsync("takeover", body.ToJsonString());
        if (holder != "alice@example.test")
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal(claimBytes, File.ReadAllBytes(claimPath));
            Assert.Equal(registration, File.ReadAllBytes(fixture.RegistrationPath));
            Assert.Equal("attached", (await glass.StatusAsync()).GetProperty("state").GetString());
        }
        else
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var current = await glass.StatusAsync();
            Assert.Equal("taken-over", current.GetProperty("state").GetString());
            Assert.Equal(holder, current.GetProperty("holder").GetString());
            var receipt = Assert.Single(current.GetProperty("controls").EnumerateArray()).GetProperty("receipt");
            using var replay = await glass.ControlAsync("takeover", body.ToJsonString());
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            using var replayBody = JsonDocument.Parse(await replay.Content.ReadAsStringAsync(Ct));
            Assert.Equal(receipt.GetRawText(), replayBody.RootElement.GetProperty("receipt").GetRawText());
        }
        Assert.Empty(fixture.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Glass_stop_with_attached_registration_fences_reconcile_and_final_launch_claim(bool finalClaim)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.EnableAutomaticAsync();
        await fixture.CommandAsync("attach");
        fixture.Reply = "ReplaceReview";
        await AdmitControlFollowAsync(fixture, "attached-stop");
        var action = (await fixture.RowAsync("attached-stop")).ReplacementReviewAction!;
        await using var glass = await GlassFixture.StartAsync(fixture);
        var body = ControlBody(await glass.StatusAsync(), "stop");
        var armed = false;
        async Task StopAsync()
        {
            ConductorFollowSession.AfterHostedControlFence = () => throw new IOException("Offline cleanup failure");
            using var response = await glass.ControlAsync("stop", body);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        var advancer = fixture.Advancer(async (_, _) =>
        {
            if (armed) { armed = false; await StopAsync(); }
            return Head;
        });
        using var scheduler = fixture.Scheduler(advancer,
            launch: (_, _) => throw new InvalidOperationException("Stopped authority must never launch"),
            beforeLaunchClaim: _ => { armed = finalClaim; return Task.CompletedTask; });
        try
        {
            if (finalClaim) await scheduler.TickOnceAsync(Ct);
            else
            {
                await StopAsync();
                await scheduler.ReconcileReplacementReviewActionsAsync(Ct);
            }
            Assert.True(JsonNode.Parse(File.ReadAllText(fixture.RegistrationPath))!["attached"]!.GetValue<bool>());
            Assert.True((await ConductorClaimStore.GetClaimAsync(Identity, fixture.Root, Ct))!.Stopped);
            var refused = await fixture.RowAsync("attached-stop");
            Assert.Null(refused.LaunchMayHaveBegunAt);
            Assert.Null(refused.ReplacementReviewAction!.ReplacementAttemptId);
            Assert.NotNull(refused.ReplacementReviewAction.BlockedReason);
            Assert.Equal(action.EvidenceDigest, refused.ReplacementReviewAction.EvidenceDigest);
            Assert.Single(fixture.Calls);
        }
        finally { ConductorFollowSession.AfterHostedControlFence = null; }
    }
}
