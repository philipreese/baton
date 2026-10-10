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
    [InlineData(false, "head")]
    [InlineData(false, "closed")]
    [InlineData(false, "merged")]
    [InlineData(false, "retired")]
    [InlineData(true, "head")]
    [InlineData(true, "closed")]
    [InlineData(true, "merged")]
    [InlineData(true, "retired")]
    public async Task Glass_obligation_observation_crash_recovers_exact_proof_without_current_authority(bool legacy, string change)
    {
        using var fixture = await Fixture.CreateAsync();
        await AdmitObservationCrashAsync(fixture, legacy);
        var launches = 0;
        using var scheduler = fixture.Scheduler(launch: (request, _) =>
        {
            launches++;
            return Task.FromResult(new QueueLaunchOutcome(request.RoomDirectory));
        });
        var before = await CrashAfterObligationObservationAsync(fixture, scheduler);
        var action = before.ReplacementReviewAction!;
        var obligation = (await fixture.Store.ReadAsync(action.ObligationKey, Ct))!;
        await ChangeObservedSourceAsync(fixture, change);
        await fixture.ChangeAsync("observation-crash", "opt-in");
        await fixture.ChangeAsync("observation-crash", "claim");
        await fixture.ChangeAsync("observation-crash", "held");
        var claimPath = Path.Combine(fixture.Root, Identity.FileSlug, BatonPaths.ConductorClaimFileName);
        var claimBytes = File.ReadAllBytes(claimPath);
        var facts = File.ReadAllBytes(BatonPaths.FleetEventsFile);
        var remoteCalls = fixture.RemoteCalls;
        using var recovered = fixture.Scheduler(fixture.Advancer((_, _) =>
            throw new InvalidOperationException("Historical recovery must not read today's workspace head")),
            launch: (_, _) => throw new InvalidOperationException("Observed action must not launch again"));
        await recovered.ReconcileReplacementReviewActionsAsync(Ct);
        var row = await fixture.RowAsync("observation-crash");
        Assert.Equal(action with
        {
            ActionObservedAt = obligation.ActionObservedAt,
            BlockedReason = null,
            NextTrigger = null,
        }, row.ReplacementReviewAction);
        Assert.Equal(before.Round, row.Round);
        Assert.Equal(before.AutomaticFixUsed, row.AutomaticFixUsed);
        Assert.Equal(JsonSerializer.Serialize(before.AttemptEnvelope), JsonSerializer.Serialize(row.AttemptEnvelope));
        Assert.Equal(before.LaunchMayHaveBegunAt, row.LaunchMayHaveBegunAt);
        var queueBytes = File.ReadAllBytes(BatonPaths.QueueFile);
        var stamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(BatonPaths.QueueFile, stamp);
        await recovered.ReconcileReplacementReviewActionsAsync(Ct);
        await recovered.ReconcileReplacementReviewActionsAsync(Ct);
        Assert.Equal(queueBytes, File.ReadAllBytes(BatonPaths.QueueFile));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(BatonPaths.QueueFile));
        Assert.Equal(remoteCalls, fixture.RemoteCalls);
        // Retain every row before ordinary lifecycle advancement to isolate recovery across ticks.
        await ChangeObservedSourceAsync(fixture, "retired");
        await QueueStore.MutateAsync(BatonPaths.QueueFile, queue => queue with
        {
            Items = queue.Items.Select(item => item.Tag == "observation-crash" ? item with
            {
                State = QueueItemState.Done,
            } : item).ToList(),
        }, Ct);
        queueBytes = File.ReadAllBytes(BatonPaths.QueueFile);
        File.SetLastWriteTimeUtc(BatonPaths.QueueFile, stamp);
        await recovered.TickOnceAsync(Ct);
        await recovered.TickOnceAsync(Ct);
        Assert.Equal(queueBytes, File.ReadAllBytes(BatonPaths.QueueFile));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(BatonPaths.QueueFile));
        Assert.Equal(remoteCalls, fixture.RemoteCalls);
        Assert.Equal(claimBytes, File.ReadAllBytes(claimPath));
        Assert.Equal(obligation, await fixture.Store.ReadAsync(action.ObligationKey, Ct));
        Assert.Equal(facts, File.ReadAllBytes(BatonPaths.FleetEventsFile));
        await using var glass = await GlassFixture.StartAsync(fixture);
        var projection = Assert.Single((await glass.StatusAsync()).GetProperty("actions").EnumerateArray());
        Assert.Equal("observed", projection.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, projection.GetProperty("reason").ValueKind);
        Assert.Equal(JsonValueKind.Null, projection.GetProperty("nextTrigger").ValueKind);
        Assert.Equal(1, launches);
        Assert.Equal(legacy ? 0 : 1, fixture.Calls.Count);
    }

    [Theory]
    [InlineData(false, "proof")]
    [InlineData(false, "missing-proof")]
    [InlineData(false, "attempt")]
    [InlineData(false, "room")]
    [InlineData(false, "holder")]
    [InlineData(false, "head")]
    [InlineData(false, "missing-issued")]
    [InlineData(false, "conflicting-provenance")]
    [InlineData(true, "proof")]
    [InlineData(true, "missing-proof")]
    [InlineData(true, "attempt")]
    [InlineData(true, "room")]
    [InlineData(true, "holder")]
    [InlineData(true, "head")]
    [InlineData(true, "advice")]
    public async Task Glass_obligation_observation_crash_refuses_missing_or_mismatched_evidence(bool legacy, string mismatch)
    {
        using var fixture = await Fixture.CreateAsync();
        await AdmitObservationCrashAsync(fixture, legacy);
        using var scheduler = fixture.Scheduler(launch: (request, _) => Task.FromResult(new QueueLaunchOutcome(request.RoomDirectory)));
        var before = await CrashAfterObligationObservationAsync(fixture, scheduler);
        var action = before.ReplacementReviewAction!;
        var obligation = await fixture.Store.ReadAsync(action.ObligationKey, Ct);
        await ChangeObservedSourceAsync(fixture, "retired");
        await QueueStore.MutateAsync(BatonPaths.QueueFile, queue => queue with
        {
            Items = queue.Items.Select(item => item.Tag == "observation-crash" ? item with
            {
                ReplacementReviewAction = mismatch switch
                {
                    "proof" => action with { CompletionProof = "replacement-review-sha256:" + new string('0', 64) },
                    "missing-proof" => action with { CompletionProof = null },
                    "attempt" => action with { ReplacementAttemptId = new Baton.Domain.FleetAttemptId("foreign") },
                    "room" => action with { ReplacementRoomDirectory = action.SourceRoomDirectory },
                    "holder" => action with { Holder = "foreign" },
                    "head" => action with { HeadSha = new string('f', 40) },
                    "missing-issued" => action with { IssuedAuthority = null },
                    "conflicting-provenance" => action with { AdviceDigest = "foreign" },
                    "advice" => action with { AdviceDigest = "foreign" },
                    _ => throw new InvalidOperationException(mismatch),
                },
            } : item).ToList(),
        }, Ct);
        await scheduler.ReconcileReplacementReviewActionsAsync(Ct);
        var unresolved = await fixture.RowAsync("observation-crash");
        Assert.Null(unresolved.ReplacementReviewAction!.ActionObservedAt);
        if (mismatch is not ("conflicting-provenance" or "missing-proof")) Assert.NotNull(unresolved.ReplacementReviewAction.BlockedReason);
        var bytes = File.ReadAllBytes(BatonPaths.QueueFile);
        var stamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(BatonPaths.QueueFile, stamp);
        await scheduler.ReconcileReplacementReviewActionsAsync(Ct);
        await scheduler.ReconcileReplacementReviewActionsAsync(Ct);
        Assert.Equal(bytes, File.ReadAllBytes(BatonPaths.QueueFile));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(BatonPaths.QueueFile));
        Assert.Equal(obligation, await fixture.Store.ReadAsync(action.ObligationKey, Ct));
        Assert.Equal(before.Round, unresolved.Round);
        Assert.Equal(before.AutomaticFixUsed, unresolved.AutomaticFixUsed);
        Assert.Equal(JsonSerializer.Serialize(before.AttemptEnvelope), JsonSerializer.Serialize(unresolved.AttemptEnvelope));
    }

    private static async Task AdmitObservationCrashAsync(Fixture fixture, bool legacy)
    {
        if (!legacy)
        {
            await fixture.EnableAutomaticAsync();
            await fixture.CommandAsync("attach");
            fixture.Reply = "ReplaceReview";
            await AdmitControlFollowAsync(fixture, "observation-crash");
            return;
        }
        await fixture.HaltAsync("observation-crash", notify: false);
        using var scheduler = fixture.Scheduler();
        await scheduler.ReconcileStoppedWorkAdviceAsync(Ct);
        await fixture.LegacyAsync("observation-crash", () => { });
        fixture.Store.MarkStoppedWorkAdviceSourceChecked(fixture.Key("observation-crash"));
        Assert.Equal(0, await ReplacementReviewConductorCommand.ExecuteAsync(ConductorOptionsParser.Parse(
            ["act", "--obligation", fixture.Key("observation-crash"), "--holder", "holder",
                "--action", "replace-review", "--expected-head", Head]), TextWriter.Null,
            fixture.Root, fixture.Advancer(), fixture.Store, Ct));
    }

    private static async Task<QueueItem> CrashAfterObligationObservationAsync(Fixture fixture, QueueSchedulerService scheduler)
    {
        await scheduler.TickOnceAsync(Ct);
        await fixture.CompleteReviewAsync("observation-crash");
        var issued = await fixture.RowAsync("observation-crash");
        Assert.NotNull(await fixture.Advancer().ObserveIssuedReplacementReviewResultAsync(issued, Ct));
        scheduler.ReplacementReviewAfterActionObserved = () => throw new IOException("Offline crash after durable obligation observation");
        await Assert.ThrowsAsync<IOException>(() => scheduler.ReconcileReplacementReviewActionsAsync(Ct));
        scheduler.ReplacementReviewAfterActionObserved = null;
        var row = await fixture.RowAsync("observation-crash");
        var action = row.ReplacementReviewAction!;
        var obligation = (await fixture.Store.ReadAsync(action.ObligationKey, Ct))!;
        Assert.Null(action.ActionObservedAt);
        Assert.NotNull(action.CompletionProof);
        Assert.Equal(ConductorObligationStatus.ActionObserved, obligation.Status);
        Assert.Equal(action.CompletionProof, obligation.ActionProof);
        Assert.NotNull(obligation.ActionObservedAt);
        return row;
    }

    private static async Task ChangeObservedSourceAsync(Fixture fixture, string change)
    {
        if (change == "head") await fixture.ChangeAsync("observation-crash", "head");
        if (change == "closed") fixture.PullRequestState = "CLOSED";
        if (change == "merged") fixture.PullRequestState = "MERGED";
        await QueueStore.MutateAsync(BatonPaths.QueueFile, queue => queue with
        {
            Items = queue.Items.Select(item => item.Tag == "observation-crash" ? item with
            {
                Retirement = change == "retired" ? new(QueueRetirement.Operator, DateTimeOffset.UtcNow, "Retained history") : item.Retirement,
            } : item).ToList(),
        }, Ct);
    }

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
