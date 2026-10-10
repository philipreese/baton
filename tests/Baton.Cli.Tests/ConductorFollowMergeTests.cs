using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Baton.Cli.Daemon;
using Baton.Domain;
using Baton.Queue;
using Baton.Status;
using Baton.Store;
using Baton.Vendors;
using Baton.Cli.Tests.Daemon;

namespace Baton.Cli.Tests;

public sealed partial class ConductorFollowDeliveryTests
{
    [Fact]
    public async Task Issue2671_Authenticated_exact_grant_to_observed_squash_once()
    {
        using var harness = await MergeHarness.CreateAsync();
        await using var glass = await GlassFixture.StartAsync(harness.Fixture);
        using var denied = await glass.ControlAsync("merge/grant", await harness.BodyAsync(), null);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Empty(harness.View().Grants);
        using var accepted = await glass.ControlAsync("merge/grant", await harness.BodyAsync());
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        await harness.DeliverAsync();
        await harness.DeliverAsync();
        var grant = Assert.Single(harness.View().Grants);
        Assert.Equal("spent", grant.Permission);
        Assert.Equal("complete", grant.Delivery);
        Assert.Equal("Merge", grant.Decision);
        Assert.Equal("confirmed", grant.Operation);
        Assert.Equal("merged confirmed and observed", grant.Observation!.State);
        Assert.Equal(new string('c', 40), grant.Observation.MergeCommitSha);
        Assert.Equal(1, harness.ModelCalls);
        var command = Assert.Single(harness.Gh.Mutations);
        Assert.Equal(WorkItemAdvancer.ExactMergeArguments(harness.Request), command);
        Assert.DoesNotContain("pr", command);
        Assert.DoesNotContain("--admin", command);
        Assert.DoesNotContain("--auto", command);
        Assert.Empty(harness.Fixture.Calls);
        var ledger = JsonSerializer.Deserialize<ExactMergeLedger>(File.ReadAllText(Path.Combine(harness.Fixture.Root,
            "conductor-merge", Identity.FileSlug, "grants.json")), MergeHarness.Json)!;
        var issued = Assert.Single(ledger.Grants).Attempt!;
        Assert.Equal(command, issued.Arguments);
        Assert.Equal("fixture-actor", issued.Qualification!.Actor);
        Assert.Equal(ConductorFollowSession.MergeDigest(issued.Qualification), issued.QualificationSha256);
        Assert.Contains("HTTP/2.0 200", issued.Response);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(issued.Response!))).ToLowerInvariant(), issued.ResponseDigest);
    }

    [Theory]
    [InlineData("Hold")]
    [InlineData("ReplaceReview")]
    [InlineData("prose")]
    [InlineData("foreign-head")]
    [InlineData("foreign-grant")]
    [InlineData("foreign-receipt")]
    [InlineData("duplicate")]
    [InlineData("unknown-field")]
    public async Task Issue2671_Hold_or_ineligible_typed_reply_consumes_judgment_without_repeat_or_allowance(string reply)
    {
        using var harness = await MergeHarness.CreateAsync();
        harness.Reply = reply;
        await harness.AcceptAsync();
        await harness.DeliverAsync();
        await harness.DeliverAsync();
        Assert.Equal(1, harness.ModelCalls);
        Assert.Empty(harness.Gh.Mutations);
        var grant = Assert.Single(harness.View().Grants);
        Assert.Equal("accepted/unspent", grant.Permission);
        Assert.Equal("complete", grant.Delivery);
        Assert.Equal(reply == "Hold" ? "Hold" : null, grant.Decision);
        Assert.Equal(reply == "Hold" ? "Fixture reason" : "reason unavailable", grant.Reason);
    }

    [Theory]
    [InlineData("admin-exempt")]
    [InlineData("queue")]
    [InlineData("ruleset-bypass")]
    [InlineData("operator-merge")]
    [InlineData("custom-role")]
    [InlineData("checks")]
    [InlineData("head")]
    [InlineData("policy")]
    [InlineData("review-bytes")]
    [InlineData("review-attempt")]
    [InlineData("review-flow")]
    [InlineData("ceiling")]
    public async Task Issue2671_Fresh_qualification_refuses_without_any_mutation(string change)
    {
        using var harness = await MergeHarness.CreateAsync();
        await harness.AcceptAsync();
        if (change == "review-bytes") File.AppendAllText(harness.Verdict, " ");
        else if (change == "review-flow") File.WriteAllText(harness.Flow, "");
        else if (change == "review-attempt") await harness.ChangeRowAsync(row => row with
        { OwnedTask = row.OwnedTask! with { Ready = row.OwnedTask.Ready! with { ReviewProof = null } } });
        else if (change == "ceiling") ProjectCeilingStore.Revoke(harness.Fixture.Workspace, harness.Fixture.CeilingPath);
        else harness.Gh.Change = change;
        await harness.DeliverAsync();
        await harness.DeliverAsync();
        Assert.Empty(harness.Gh.Mutations);
        Assert.NotEqual("spent", Assert.Single(harness.View().Grants).Permission);
        Assert.InRange(harness.ModelCalls, 0, 1);
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("expiry")]
    [InlineData("stop")]
    [InlineData("takeover")]
    [InlineData("detach")]
    [InlineData("resume")]
    [InlineData("hold")]
    public async Task Issue2671_Control_first_wins_final_cutoff_while_model_is_busy(string operation)
    {
        using var harness = await MergeHarness.CreateAsync();
        await using var glass = await GlassFixture.StartAsync(harness.Fixture);
        await harness.AcceptAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.WaitInModel = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); };
        var delivery = harness.DeliverAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct); // wait-ok: Offline model rendezvous.
            if (operation == "revoke") await harness.RevokeAsync();
            else if (operation == "expiry") ConductorFollowSession.MergeClock = () => harness.Request.ExpiresAt.AddSeconds(1);
            else if (operation is "detach" or "resume")
            {
                using var detached = await glass.DetachAsync(DetachBody(await glass.StatusAsync()));
                Assert.Equal(HttpStatusCode.OK, detached.StatusCode);
                // Resume cannot succeed while the turn is busy, and cannot inherit the old attachment.
                if (operation == "resume")
                {
                    using var resumed = await glass.ResumeAsync(DetachBody(await glass.StatusAsync()));
                    Assert.Equal(HttpStatusCode.Conflict, resumed.StatusCode);
                }
            }
            else
            {
                var row = await glass.StatusAsync();
                using var control = await glass.ControlAsync(operation, operation == "hold"
                    ? HoldBody(row, operation, "merge-control") : ControlBody(row, operation, "merge-control"));
                Assert.Equal(HttpStatusCode.OK, control.StatusCode);
            }
            Assert.Empty(harness.Gh.Mutations);
            Assert.Equal(1, harness.ModelCalls);
        }
        finally { release.TrySetResult(); }
        await delivery;
        Assert.Empty(harness.Gh.Mutations);
        if (operation == "hold")
        {
            await ApplyHoldAsync(glass, "unhold", "merge-unhold");
            harness.WaitInModel = null;
            await harness.DeliverAsync();
            Assert.Single(harness.Gh.Mutations);
            Assert.Equal(1, harness.ModelCalls);
        }
    }

    [Theory]
    [InlineData("judgment-source", 1, 1)]
    [InlineData("judgment-marker", 0, 0)]
    [InlineData("judgment-response", 1, 1)]
    [InlineData("judgment-delivery", 1, 1)]
    [InlineData("attempt-marker", 1, 0)]
    [InlineData("executor-response", 1, 1)]
    [InlineData("outcome-read", 1, 1)]
    public async Task Issue2671_Persistence_crash_recovery_never_repeats_issued_model_or_executor(string crash, int models, int commands)
    {
        using var harness = await MergeHarness.CreateAsync();
        await harness.AcceptAsync();
        ConductorFollowSession.AfterMergePersistence = stage => { if (stage == crash) throw new IOException("Offline persistence cut"); };
        await harness.DeliverAsync();
        ConductorFollowSession.AfterMergePersistence = null;
        await harness.DeliverAsync();
        await harness.DeliverAsync();
        Assert.Equal(models, harness.ModelCalls);
        Assert.Equal(commands, harness.Gh.Mutations.Count);
        var grant = Assert.Single(harness.View().Grants);
        if (crash is "attempt-marker" or "executor-response")
        {
            Assert.Equal("spent", grant.Permission);
            Assert.Equal("unknown", grant.Operation);
            Assert.Equal(crash == "executor-response" ? "merged observed; executor unconfirmed" : "uncertain", grant.Observation!.State);
        }
    }

    [Fact]
    public async Task Issue2671_Already_ready_binding_duplicate_conflict_and_supersession_are_exact()
    {
        using var harness = await MergeHarness.CreateAsync();
        var body = await harness.BodyAsync();
        var bad = harness.Request with { ReadyReceiptSha256 = new string('f', 64) };
        await Assert.ThrowsAnyAsync<BatonFlowException>(() => ConductorFollowSession.MergeControlFromGlassAsync(
            JsonSerializer.Serialize(bad, MergeHarness.Json), harness.Fixture.Root, Operator, false, Ct));
        var accepted = await harness.AcceptAsync();
        var duplicate = await ConductorFollowSession.MergeControlFromGlassAsync(body, harness.Fixture.Root, Operator, false, Ct);
        Assert.True(duplicate.Replayed);
        Assert.Equal(accepted.GrantId, duplicate.GrantId);
        await Assert.ThrowsAnyAsync<BatonFlowException>(() => ConductorFollowSession.MergeControlFromGlassAsync(
            JsonSerializer.Serialize(harness.Request with { ExpiresAt = harness.Request.ExpiresAt.AddSeconds(1) }, MergeHarness.Json),
            harness.Fixture.Root, Operator, false, Ct));
        harness.Request = harness.Request with { RequestId = "replacement" };
        var replacement = await harness.AcceptAsync();
        Assert.Equal("superseded", harness.View().Grants[0].Permission);
        await harness.RevokeAsync(accepted.GrantId, "stale-revoke");
        Assert.Equal("accepted/unspent", harness.View().Grants[1].Permission);
        await harness.DeliverAsync();
        Assert.Single(harness.Gh.Mutations);
        Assert.Equal(replacement.GrantId, harness.View().Grants[1].GrantId);
    }

    [Fact]
    public async Task Issue2671_Pending_ready_commit_notify_gap_binds_one_explicit_pair_without_backlog_replay()
    {
        using var harness = await MergeHarness.CreateAsync();
        var ready = (await harness.RowAsync()).OwnedTask!.Ready!;
        await harness.ChangeRowAsync(row => row with { Stage = WorkStage.Review, OwnedTask = row.OwnedTask! with { Ready = null } });
        harness.Request = harness.Request with { ReadyReceiptId = null, ReadyReceiptSha256 = null };
        await harness.AcceptAsync();
        await harness.DeliverAsync();
        Assert.Equal(0, harness.ModelCalls);
        await harness.ChangeRowAsync(row => row with
        {
            Stage = WorkStage.Ready,
            OwnedTask = row.OwnedTask! with { Ready = ready with { ReadyObservedAt = DateTimeOffset.UtcNow.AddSeconds(1) } }
        });
        await harness.DeliverAsync();
        await harness.DeliverAsync();
        Assert.Equal(1, harness.ModelCalls);
        Assert.Single(harness.Gh.Mutations);
    }

    [Theory]
    [InlineData("lost", "unknown", "merged observed; executor unconfirmed")]
    [InlineData("refused", "refused", "uncertain")]
    [InlineData("malformed", "unknown", "uncertain")]
    public async Task Issue2671_Executor_unknown_and_refusal_spend_permission_with_no_fallback(string response, string executor, string observation)
    {
        using var harness = await MergeHarness.CreateAsync();
        harness.Gh.Response = response;
        await harness.AcceptAsync();
        await harness.DeliverAsync();
        await harness.DeliverAsync();
        var grant = Assert.Single(harness.View().Grants);
        Assert.Equal("spent", grant.Permission);
        Assert.Equal(executor, grant.Operation);
        Assert.Equal(observation, grant.Observation!.State);
        Assert.Single(harness.Gh.Mutations);
        harness.Request = harness.Request with { RequestId = "second" };
        if (response != "refused") await Assert.ThrowsAnyAsync<BatonFlowException>(harness.AcceptAsync);
        else Assert.False((await harness.AcceptAsync()).Replayed);
    }

    [Theory]
    [InlineData("review")]
    [InlineData("head")]
    [InlineData("receipt")]
    [InlineData("ceiling")]
    public async Task Issue2671_Final_fence_rechecks_review_bytes_and_queue_after_remote_reads(string change)
    {
        using var harness = await MergeHarness.CreateAsync();
        await harness.AcceptAsync();
        ConductorFollowSession.AfterMergePersistence = stage =>
        {
            if (stage != "qualification") return;
            if (change == "review") File.AppendAllText(harness.Verdict, " ");
            else if (change == "ceiling") ProjectCeilingStore.Revoke(harness.Fixture.Workspace, harness.Fixture.CeilingPath);
            else harness.ChangeRowAsync(row => change == "head" ? row with { ChecksHeadSha = new string('b', 40) }
                : row with { OwnedTask = row.OwnedTask! with { Ready = row.OwnedTask.Ready! with { ReadyObservedAt = DateTimeOffset.UtcNow } } })
                .GetAwaiter().GetResult();
        };
        await harness.DeliverAsync();
        Assert.Empty(harness.Gh.Mutations);
        Assert.Equal("refused", Assert.Single(harness.View().Grants).Delivery);
    }

    [Theory]
    [InlineData("qualification", "revoke", 0)]
    [InlineData("qualification", "expiry", 0)]
    [InlineData("qualification", "hold", 0)]
    [InlineData("qualification", "stop", 0)]
    [InlineData("qualification", "takeover", 0)]
    [InlineData("qualification", "detach", 0)]
    [InlineData("attempt-marker", "expiry", 1)]
    [InlineData("attempt-marker", "hold", 1)]
    [InlineData("attempt-marker", "stop", 1)]
    [InlineData("attempt-marker", "takeover", 1)]
    [InlineData("attempt-marker", "detach", 1)]
    public async Task Issue2671_Controls_racing_remote_qualification_or_issue_obey_the_durable_cutoff(
        string cutoff, string operation, int commands)
    {
        using var harness = await MergeHarness.CreateAsync();
        await using var glass = await GlassFixture.StartAsync(harness.Fixture);
        await harness.AcceptAsync();
        ConductorFollowSession.AfterMergePersistence = stage =>
        {
            if (stage != cutoff) return;
            if (operation == "expiry") ConductorFollowSession.MergeClock = () => harness.Request.ExpiresAt.AddSeconds(1);
            else if (operation == "revoke") harness.RevokeAsync().GetAwaiter().GetResult();
            else
            {
                var status = glass.StatusAsync().GetAwaiter().GetResult();
                using var reply = (operation == "detach" ? glass.DetachAsync(DetachBody(status))
                    : glass.ControlAsync(operation, operation == "hold" ? HoldBody(status, operation, "cutoff-control")
                        : ControlBody(status, operation, "cutoff-control"))).GetAwaiter().GetResult();
                Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
            }
        };
        await harness.DeliverAsync();
        ConductorFollowSession.AfterMergePersistence = null;
        await harness.DeliverAsync();
        Assert.Equal(1, harness.ModelCalls);
        Assert.Equal(commands, harness.Gh.Mutations.Count);
        Assert.Equal(commands == 1, Assert.Single(harness.View().Grants).Permission == "spent");
    }

    [Theory]
    [InlineData("HTTP/2.0 200 OK\n\n{\"merged\":true,\"sha\":\"cccccccccccccccccccccccccccccccccccccccc\"}", "confirmed")]
    [InlineData("HTTP/2.0 202 Accepted\n\n{\"merged\":true,\"sha\":\"cccccccccccccccccccccccccccccccccccccccc\"}", "unknown")]
    [InlineData("HTTP/2.0 200 OK\n\n{\"merged\":true}", "unknown")]
    [InlineData("HTTP/2.0 409 Conflict\n\n{\"merged\":false}", "refused")]
    [InlineData("HTTP/2.0 200 OK\n\n{\"merged\":false,\"merged\":true,\"sha\":\"cccccccccccccccccccccccccccccccccccccccc\"}", "unknown")]
    public void Issue2671_Transport_requires_positive_synchronous_structured_result(string bytes, string expected)
        => Assert.Equal(expected, WorkItemAdvancer.ParseExactMergeResponse(new(true, 0, bytes, "")).State);

    [Fact]
    public async Task Issue2671_Revoke_after_issued_marker_audits_but_does_not_cancel_or_restore_allowance()
    {
        using var harness = await MergeHarness.CreateAsync();
        var accepted = await harness.AcceptAsync();
        ConductorFollowSession.AfterMergePersistence = stage =>
        {
            if (stage == "attempt-marker") harness.RevokeAsync(accepted.GrantId).GetAwaiter().GetResult();
        };
        await harness.DeliverAsync();
        await harness.DeliverAsync();
        var grant = Assert.Single(harness.View().Grants);
        Assert.True(grant.Revoked);
        Assert.Equal("spent", grant.Permission);
        Assert.Equal("merged confirmed and observed", grant.Observation!.State);
        Assert.Single(harness.Gh.Mutations);
        Assert.True(ConductorFollowSession.LookupMergeControl(harness.Fixture.Root, Repository, Operator,
            "merge-revoke")!.Replayed);
    }

    [Fact]
    public async Task Issue2671_Hold_reconciliation_preserves_ready_identity_and_queue_bytes()
    {
        using var harness = await MergeHarness.CreateAsync();
        harness.Reply = "Hold";
        await harness.AcceptAsync();
        await harness.DeliverAsync();
        var bytes = File.ReadAllBytes(BatonPaths.QueueFile);
        var stamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(BatonPaths.QueueFile, stamp);
        await harness.DeliverAsync();
        await harness.DeliverAsync();
        Assert.Equal(bytes, File.ReadAllBytes(BatonPaths.QueueFile));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(BatonPaths.QueueFile));
        Assert.Equal(1, harness.ModelCalls);
        Assert.Empty(harness.Gh.Mutations);
    }

    [Fact]
    public async Task Issue2671_Unresolved_issued_target_fences_fresh_acquisition_even_after_revoke()
    {
        using var harness = await MergeHarness.CreateAsync();
        var original = await harness.AcceptAsync();
        ConductorFollowSession.AfterMergePersistence = stage =>
        {
            if (stage == "attempt-marker") throw new IOException("Offline marker before spawn");
        };
        await harness.DeliverAsync();
        ConductorFollowSession.AfterMergePersistence = null;
        await harness.RevokeAsync(original.GrantId);
        await harness.Fixture.ChangeAsync("merge-task", "claim");
        await Baton.Conductor.ConductorClaimStore.TakeoverAsync(Identity, "holder", "fixture", harness.Fixture.Root,
            cancellationToken: Ct);
        var attachment = JsonSerializer.Deserialize<ConductorFollowAttachment>(File.ReadAllText(harness.Fixture.RegistrationPath), MergeHarness.Json)!;
        var acquisitionFence = await Assert.ThrowsAnyAsync<BatonFlowException>(() => harness.Fixture.CommandAsync("attach"));
        Assert.Contains("different claim acquisition", acquisitionFence.Message);
        // Independently simulate a future valid attachment to test the durable merge fence.
        // Keep all original session/attempt evidence; production Attach still refuses above.
        var claim = (await Baton.Conductor.ConductorClaimStore.GetClaimAsync(Identity, harness.Fixture.Root, Ct))!;
        var generation = Baton.Conductor.ConductorClaimStore.GetClaimGeneration(claim);
        var directory = Path.Combine(harness.Fixture.Root, "conductor-follow", Identity.FileSlug,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(Repository + "\n" + generation))).ToLowerInvariant());
        Directory.CreateDirectory(directory);
        File.Copy(Path.Combine(attachment.SessionDirectory, "request.json"), Path.Combine(directory, "request.json"));
        var state = JsonSerializer.Deserialize<ConductorFollowState>(File.ReadAllText(
            Path.Combine(attachment.SessionDirectory, "session.json")), MergeHarness.Json)!;
        File.WriteAllText(Path.Combine(directory, "session.json"), JsonSerializer.Serialize(
            state with { ClaimGeneration = generation, SessionId = null, Frozen = false }, MergeHarness.Json));
        attachment = attachment with
        {
            Id = Guid.NewGuid().ToString("N"),
            ClaimGeneration = generation,
            SessionDirectory = directory,
            CutoverAt = DateTimeOffset.UtcNow
        };
        File.WriteAllText(harness.Fixture.RegistrationPath, JsonSerializer.Serialize(attachment, MergeHarness.Json));
        harness.Request = harness.Request with
        {
            RequestId = "new-acquisition",
            ClaimGeneration = attachment.ClaimGeneration,
            AttachmentId = attachment.Id
        };
        var refused = await Assert.ThrowsAnyAsync<BatonFlowException>(harness.AcceptAsync);
        Assert.Contains("issued merge attempt fences", refused.Message);
        await harness.DeliverAsync();
        Assert.Empty(harness.Gh.Mutations);
        Assert.Equal("spent", Assert.Single(harness.View().Grants).Permission);
    }

    [Fact]
    public async Task Issue2671_Resume_does_not_inherit_old_attachment_grant()
    {
        using var harness = await MergeHarness.CreateAsync();
        await using var glass = await GlassFixture.StartAsync(harness.Fixture);
        await harness.AcceptAsync();
        using (var detached = await glass.DetachAsync(DetachBody(await glass.StatusAsync())))
            Assert.Equal(HttpStatusCode.OK, detached.StatusCode);
        using (var resumed = await glass.ResumeAsync(DetachBody(await glass.StatusAsync())))
            Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
        await harness.DeliverAsync();
        Assert.Equal("authority-invalidated", Assert.Single(harness.View().Grants).Permission);
        Assert.Equal(0, harness.ModelCalls);
        Assert.Empty(harness.Gh.Mutations);
    }

    [Theory]
    [InlineData("identity.json")]
    [InlineData("source.json")]
    [InlineData("response.json")]
    [InlineData("receipt.txt")]
    [InlineData("delivery.jsonl")]
    [InlineData("source.json:grant")]
    [InlineData("source.json:judgment")]
    [InlineData("source.json:request")]
    [InlineData("source.json:configuration")]
    [InlineData("source.json:grant.request")]
    [InlineData("source.json:grant.judgment")]
    [InlineData("source.json:grant.judgment.ready")]
    [InlineData("source.json:judgment.ready")]
    public async Task Issue2671_Saved_response_requires_complete_retained_provenance(string member)
    {
        using var harness = await MergeHarness.CreateAsync();
        await harness.AcceptAsync();
        ConductorFollowSession.AfterMergePersistence = stage =>
        {
            if (stage == "judgment-delivery") throw new IOException("Offline saved delivery");
        };
        await harness.DeliverAsync();
        ConductorFollowSession.AfterMergePersistence = null;
        var registration = JsonSerializer.Deserialize<ConductorFollowAttachment>(File.ReadAllText(harness.Fixture.RegistrationPath), MergeHarness.Json)!;
        var parts = member.Split(':');
        var path = Assert.Single(Directory.GetFiles(registration.SessionDirectory, parts[0], SearchOption.AllDirectories));
        if (parts.Length == 1) File.WriteAllText(path, "{}");
        else
        {
            var source = JsonNode.Parse(File.ReadAllText(path))!;
            var keys = parts[1].Split('.');
            var parent = source;
            foreach (var key in keys[..^1]) parent = parent[key]!;
            parent[keys[^1]] = null;
            File.WriteAllText(path, source.ToJsonString());
        }
        await harness.DeliverAsync();
        Assert.Empty(harness.Gh.Mutations);
        Assert.Equal(1, harness.ModelCalls);
    }

    [Theory]
    [InlineData("acceptance")]
    [InlineData("revocation")]
    public async Task Issue2671_Control_persistence_before_lost_acknowledgement_replays_one_exact_receipt(string crash)
    {
        using var harness = await MergeHarness.CreateAsync();
        if (crash == "revocation") await harness.AcceptAsync();
        ConductorFollowSession.AfterMergePersistence = stage =>
        {
            if (stage == crash) throw new IOException("Offline lost control acknowledgement");
        };
        await Assert.ThrowsAsync<IOException>(() => crash == "acceptance" ? harness.AcceptAsync() : harness.RevokeAsync());
        ConductorFollowSession.AfterMergePersistence = null;
        var receipt = await (crash == "acceptance" ? harness.AcceptAsync() : harness.RevokeAsync());
        Assert.True(receipt.Replayed);
        Assert.True(ConductorFollowSession.LookupMergeControl(harness.Fixture.Root, Repository, Operator,
            crash == "acceptance" ? harness.Request.RequestId : "merge-revoke")!.Replayed);
        Assert.Single(harness.View().Grants);
        await harness.DeliverAsync();
        await harness.DeliverAsync();
        Assert.Equal(crash == "acceptance" ? 1 : 0, harness.Gh.Mutations.Count);
    }

    [Fact]
    public async Task Issue2671_Frozen_judgment_remains_revocable_without_another_model_or_merge()
    {
        using var harness = await MergeHarness.CreateAsync();
        await using var glass = await GlassFixture.StartAsync(harness.Fixture);
        var accepted = await harness.AcceptAsync();
        ConductorFollowSession.AfterMergePersistence = stage =>
        {
            if (stage == "judgment-marker") throw new IOException("Offline uncertain native launch");
        };
        await harness.DeliverAsync();
        ConductorFollowSession.AfterMergePersistence = null;
        Assert.Equal("frozen", (await glass.StatusAsync()).GetProperty("state").GetString());
        using var revoked = await glass.ControlAsync("merge/revoke", JsonSerializer.Serialize(
            new ExactMergeRevokeRequest(Repository, accepted.GrantId, "frozen-revoke"), MergeHarness.Json));
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        await harness.DeliverAsync();
        Assert.Equal("revoked", Assert.Single(harness.View().Grants).Permission);
        Assert.Equal(0, harness.ModelCalls);
        Assert.Empty(harness.Gh.Mutations);
    }

    [Theory]
    [InlineData("ready")]
    [InlineData("server-policy")]
    public async Task Issue2671_Incomplete_retained_ledger_fails_closed_before_reconciliation(string member)
    {
        using var harness = await MergeHarness.CreateAsync();
        await harness.AcceptAsync();
        await harness.DeliverAsync();
        var path = Path.Combine(harness.Fixture.Root, "conductor-merge", Identity.FileSlug, "grants.json");
        var ledger = JsonNode.Parse(File.ReadAllText(path))!;
        var grant = ledger["grants"]![0]!;
        if (member == "ready") grant["judgment"]!["ready"] = null;
        else grant["attempt"]!["qualification"]!["serverPolicy"] = null;
        File.WriteAllText(path, ledger.ToJsonString());
        await Assert.ThrowsAsync<IOException>(harness.DeliverAsync);
        Assert.NotNull(harness.View().Diagnostic);
        Assert.Single(harness.Gh.Mutations);
        Assert.Equal(1, harness.ModelCalls);
    }

    [Fact]
    public async Task Issue2671_Null_independent_review_attempt_is_a_provenance_refusal()
    {
        using var harness = await MergeHarness.CreateAsync();
        var row = await harness.RowAsync();
        var ready = row.OwnedTask!.Ready!;
        var missing = ready with { ReviewProof = ready.ReviewProof! with { Attempt = null! } };
        Assert.Throws<CliArgumentException>(() => WorkItemAdvancer.ValidateExactReviewBytes(row, missing));
        Assert.Empty(harness.Gh.Mutations);
    }

    private sealed class MergeHarness(Fixture fixture) : IDisposable
    {
        internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        internal Fixture Fixture { get; } = fixture;
        internal MergeGh Gh { get; } = new();
        internal ExactMergeRequest Request { get; set; } = null!;
        internal int ModelCalls { get; private set; }
        internal string Reply { get; set; } = "Merge";
        internal Func<CancellationToken, Task>? WaitInModel { get; set; }
        internal string Verdict { get; private set; } = "";
        internal string Flow { get; private set; } = "";
        private WorkItemAdvancer Advancer => new(Gh, (_, _) => Task.FromResult<string?>(Head));

        internal static Task<MergeHarness> CreateAsync()
        {
            // Establish the environment scope synchronously in the caller, as the shared fixture
            // requires; an async factory would lose its AsyncLocal root at the first return.
            var pending = Fixture.CreateAsync();
            return InitializeAsync(pending);
        }

        private static async Task<MergeHarness> InitializeAsync(Task<Fixture> pending)
        {
            var harness = new MergeHarness(await pending);
            await harness.Fixture.CommandAsync("attach");
            var room = Path.Combine(harness.Fixture.Root, "merge-review");
            var execution = Guid.NewGuid().ToString("N");
            harness.Verdict = Path.Combine(room, "artifacts", "execution_" + execution, "verdict.json");
            Directory.CreateDirectory(Path.GetDirectoryName(harness.Verdict)!);
            File.WriteAllText(harness.Verdict, JsonSerializer.Serialize(new
            {
                reviewedRef = Head,
                completion = "complete",
                decision = "approve",
                summary = "Exact independent review",
                findings = Array.Empty<object>()
            }));
            await TerminalSentinelWriter.WriteAsync(room, new WorkflowStatusView(WorkflowOutcome.Succeeded,
                [new("review", "Succeeded", execution)], [harness.Verdict], null), Ct);
            var taskId = OwnedTaskExecutionIdentity.TaskIdFor(Repository, 2632);
            var attempt = new FleetAttemptId("exact-review");
            var owner = new OwnedTaskExecutionIdentity(taskId, Repository, 2632, attempt.Value, room);
            harness.Flow = Path.Combine(room, BatonPaths.FlowLogFileName);
            await using (var writer = new FlowEventLogWriter(harness.Flow))
                await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(new ExecutionRequest(new(execution), new("review-flow"),
                    new("review"), "review", [], ["verdict.json"], TimeSpan.FromMinutes(1), [], new Dictionary<StepId, ExecutionId>(),
                    DeliversBranch: false, OwnedTaskIdentity: owner with { ExecutionId = execution })), Ct);
            var envelope = new QueueAttemptEnvelope(attempt, new("implementation"), "merge-task", 2632, 77, WorkStage.Review,
                "review", "codex", "fixture", "low", ["ReadFiles"], [], [], "admitted", room, "merge-review", Head, DateTimeOffset.UtcNow, owner);
            var row = new QueueItem
            {
                Tag = "merge-task",
                Role = "review",
                Workspace = harness.Fixture.Workspace,
                SpecFile = Path.Combine(harness.Fixture.Root, "merge-task.md"),
                Issue = 2632,
                Repository = Repository,
                Branch = "2632-lane",
                PullRequest = 77,
                Stage = WorkStage.Review,
                State = QueueItemState.Done,
                Round = 1,
                RoomDirectory = room,
                AttemptId = attempt,
                AttemptBaseRevision = Head,
                AttemptEnvelope = envelope,
                AttemptAdmissionFactDurable = true,
                AttemptSettledFactDurable = true,
                OwnedTask = new(taskId, Repository, 2632, "fixture", "holder", DateTimeOffset.UtcNow)
            };
            await QueueStore.MutateAsync(BatonPaths.QueueFile, q => q with { Items = [row] }, Ct);
            Assert.Single(await harness.Advancer.AdvanceAsync(DateTimeOffset.UtcNow, Ct));
            var ready = (await harness.RowAsync()).OwnedTask!.Ready!;
            Assert.NotNull(ready.ReviewProof);
            Assert.NotNull(WorkItemAdvancer.ValidateExactReviewBytes(await harness.RowAsync(), ready));
            var registration = JsonSerializer.Deserialize<ConductorFollowAttachment>(File.ReadAllText(harness.Fixture.RegistrationPath), Json)!;
            harness.Request = new(Repository, "holder", registration.ClaimGeneration, registration.Id, "merge-request", taskId,
                77, ConductorFollowSession.MergePrUrl(Repository, 77), Head, "squash", DateTimeOffset.UtcNow.AddHours(1),
                ready.Id, ConductorFollowSession.ReadyDigest(ready));
            return harness;
        }

        internal Task<string> BodyAsync() => Task.FromResult(JsonSerializer.Serialize(Request, Json));
        internal Task<GlassMergeControlResult> AcceptAsync() => ConductorFollowSession.MergeControlFromGlassAsync(
            JsonSerializer.Serialize(Request, Json), Fixture.Root, Operator, false, Ct);
        internal Task<GlassMergeControlResult> RevokeAsync(string? grantId = null, string id = "merge-revoke") =>
            ConductorFollowSession.MergeControlFromGlassAsync(JsonSerializer.Serialize(new ExactMergeRevokeRequest(Repository,
                grantId ?? View().Grants.Last().GrantId, id), Json), Fixture.Root, Operator, true, Ct);
        internal Task<QueueItem> RowAsync() => Fixture.RowAsync("merge-task");
        internal Task ChangeRowAsync(Func<QueueItem, QueueItem> change) => QueueStore.MutateAsync(BatonPaths.QueueFile,
            q => q with { Items = q.Items.Select(change).ToArray() }, Ct);
        internal GlassMergeView View() => ConductorFollowSession.ReadGlassMerge(Identity, Fixture.Root,
            QueueStore.LoadAsync(BatonPaths.QueueFile, Ct).GetAwaiter().GetResult().Items,
            Baton.Conductor.ConductorClaimStore.GetClaimAsync(Identity, Fixture.Root, Ct).GetAwaiter().GetResult());
        internal async Task DeliverAsync()
        {
            var target = await ConductorFollowSession.MergeTargetAsync(Fixture.Root, Repository, Ct);
            if (target is not null) await ConductorFollowSession.NotifyMergeAsync(target, Fixture.Root, Advancer, Ct,
                Broker, (_, _) => Task.FromResult<Baton.Accounting.RepositoryIdentity?>(Identity));
        }

        private ConductorFollowBroker Broker => async (configuration, _, _, inputs, output, _, token, started) =>
        {
            ModelCalls++;
            Assert.Equal(new PermissionGrant(ReadFiles: true), configuration.PermissionGrant);
            if (WaitInModel is not null) await WaitInModel(token);
            var source = JsonSerializer.Deserialize<ExactMergeSource>(File.ReadAllText(Assert.Single(inputs)), Json)!;
            var r = source.Grant.Request;
            var typed = new ExactMergeDecision(1, source.Grant.Id, source.Judgment.Id, source.Judgment.Ready.Id,
                source.Judgment.ReadySha256, r.Repository, r.TaskId, r.PullRequest, r.HeadSha, r.Holder, r.ClaimGeneration,
                r.AttachmentId, Reply == "Hold" ? "Hold" : Reply == "ReplaceReview" ? "ReplaceReview" : "Merge", "Fixture reason");
            if (Reply == "foreign-head") typed = typed with { HeadSha = new string('b', 40) };
            if (Reply == "foreign-grant") typed = typed with { GrantId = new string('b', 64) };
            if (Reply == "foreign-receipt") typed = typed with { ReadyReceiptSha256 = new string('b', 64) };
            var reply = JsonSerializer.Serialize(typed, Json);
            if (Reply == "prose") reply = "Please " + reply;
            if (Reply == "duplicate") reply = reply.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal);
            if (Reply == "unknown-field") reply = reply.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"force\":true", StringComparison.Ordinal);
            await started!("merge-thread", token);
            await output.WriteLineAsync("{\"type\":\"thread.started\",\"thread_id\":\"merge-thread\"}");
            await output.WriteLineAsync("{\"type\":\"turn.started\"}");
            await output.WriteLineAsync(JsonSerializer.Serialize(new { type = "item.completed", item = new { type = "agent_message", text = reply } }));
            await output.WriteLineAsync("{\"type\":\"turn.completed\"}");
            return 0;
        };

        public void Dispose()
        {
            ConductorFollowSession.AfterMergePersistence = null;
            ConductorFollowSession.MergeClock = () => DateTimeOffset.UtcNow;
            Fixture.Dispose();
        }
    }

    private sealed class MergeGh : IGhCliRunner
    {
        internal List<IReadOnlyList<string>> Mutations { get; } = [];
        internal string? Change { get; set; }
        internal string? Response { get; set; }
        private bool _merged;
        public Task<GhCliResult> RunAsync(string workspace, IReadOnlyList<string> args, CancellationToken token)
        {
            if (args.Contains("PUT"))
            {
                Mutations.Add(args.ToArray());
                _merged = Response is null or "lost";
                if (Response == "lost") throw new IOException("Offline lost response");
                return Task.FromResult(new GhCliResult(true, Response == "refused" ? 1 : 0,
                    Response == "refused" ? "HTTP/2.0 409 Conflict\n\n{\"merged\":false}"
                    : Response == "malformed" ? "not-json" : "HTTP/2.0 200 OK\n\n{\"merged\":true,\"sha\":\"" + new string('c', 40) + "\"}", ""));
            }
            if (args is ["api", ..])
            {
                if (args.Count == 4)
                {
                    if (Change == "policy" && args[3].EndsWith("protection", StringComparison.Ordinal))
                        return Task.FromResult(new GhCliResult(true, 0,
                            "{\"required_status_checks\":{\"contexts\":[\"different\"],\"checks\":[{\"context\":\"different\",\"app_id\":15368}]}}", ""));
                    return Task.FromResult(RequiredCheckFixture.Read(args, Repository, Change == "head" ? new string('b', 40) : Head,
                        new(true, 0, Change == "checks" ? "[{\"name\":\"ci\",\"bucket\":\"fail\"}]"
                            : "[{\"name\":\"ci\",\"bucket\":\"pass\"}]", "")));
                }
                var endpoint = args[5];
                var prefix = "repos/" + Repository["github.com/".Length..];
                var text = endpoint == "user" ? "{\"login\":\"fixture-actor\"}"
                    : endpoint == prefix ? "{\"full_name\":\"" + Repository["github.com/".Length..] + "\",\"allow_squash_merge\":true,\"permissions\":{\"push\":true}}"
                    : endpoint.Contains("/collaborators/", StringComparison.Ordinal) ? "{\"role_name\":\"" + (Change == "custom-role" ? "custom" : "admin") + "\",\"permission\":\"admin\",\"user\":{\"login\":\"fixture-actor\"}}"
                    : endpoint.EndsWith("/protection", StringComparison.Ordinal) ? "{\"enforce_admins\":{\"enabled\":" + (Change == "admin-exempt" ? "false" : "true") + "},\"required_status_checks\":{\"contexts\":[\"ci\"]},\"required_pull_request_reviews\":null}"
                    : endpoint.Contains("/labels?", StringComparison.Ordinal) ? Change == "operator-merge" ? "[{\"name\":\"operator-merge\"}]" : "[]"
                    : endpoint.Contains("/rulesets/", StringComparison.Ordinal) ? "{\"id\":1,\"enforcement\":\"active\",\"bypass_actors\":[{\"actor_id\":5}],\"rules\":[]}"
                    : Change is "queue" or "ruleset-bypass" ? "[{\"ruleset_id\":1,\"type\":\"" + (Change == "queue" ? "merge_queue" : "pull_request") + "\",\"ruleset_source_type\":\"Repository\",\"ruleset_source\":\"" + Repository["github.com/".Length..] + "\"}]"
                    : "[]";
                return Task.FromResult(new GhCliResult(true, 0, text, ""));
            }
            var pr = JsonSerializer.Serialize(new
            {
                number = 77,
                state = _merged ? "MERGED" : "OPEN",
                isDraft = false,
                headRefOid = Change == "head" ? new string('b', 40) : Head,
                headRefName = "2632-lane",
                baseRefName = "main",
                isCrossRepository = false,
                statusCheckRollup = Array.Empty<object>(),
                mergeCommit = _merged ? new { oid = new string('c', 40) } : null
            });
            return Task.FromResult(new GhCliResult(true, 0, args is ["pr", "view", ..] ? pr : "[" + pr + "]", ""));
        }
    }
}
