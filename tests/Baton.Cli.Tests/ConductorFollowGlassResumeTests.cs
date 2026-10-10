using System.Net;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Baton.Conductor;
using Baton.Queue;
using Baton.Status;
using Baton.Tests.Shared;

namespace Baton.Cli.Tests;

public sealed partial class ConductorFollowDeliveryTests
{
    [Fact]
    public async Task Glass_resume_never_started_preserves_null_thread_and_hold_and_allocates_one_cutover()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        await using var glass = await GlassFixture.StartAsync(fixture);
        using var detach = await glass.DetachAsync(DetachBody(await glass.StatusAsync()));
        Assert.Equal(HttpStatusCode.OK, detach.StatusCode);
        var status = await glass.StatusAsync();
        Assert.True(status.GetProperty("resumeEligible").GetBoolean());
        var body = DetachBody(status);
        using var initialState = JsonDocument.Parse(File.ReadAllText(Path.Combine(SessionDirectory(fixture), "session.json")));
        Assert.Equal(JsonValueKind.Null, initialState.RootElement.GetProperty("sessionId").ValueKind);
        await QueueStore.MutateAsync(BatonPaths.QueueFile, queue => queue with { Held = true }, Ct);
        var evidence = RetainedBytes(fixture);
        var replies = await Task.WhenAll(glass.ResumeAsync(body), glass.ResumeAsync(body));
        try
        {
            Assert.Single(replies, reply => reply.StatusCode == HttpStatusCode.OK);
            Assert.Single(replies, reply => reply.StatusCode == HttpStatusCode.Conflict);
            using var receipt = JsonDocument.Parse(await replies.Single(reply => reply.IsSuccessStatusCode).Content.ReadAsStringAsync(Ct));
            var current = await glass.StatusAsync();
            Assert.Equal("attached", current.GetProperty("state").GetString());
            Assert.NotEqual(status.GetProperty("attachmentId").GetString(), current.GetProperty("attachmentId").GetString());
            foreach (var field in new[] { "repository", "holder", "claimGeneration", "attachmentId" })
                Assert.Equal(current.GetProperty(field).GetString(), receipt.RootElement.GetProperty(field).GetString());
        }
        finally { foreach (var reply in replies) reply.Dispose(); }
        AssertRetainedBytes(evidence);
        Assert.True((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Held);
        Assert.Empty(fixture.Calls);
        Assert.Equal(0, fixture.LegacyCalls);
    }

    [Fact]
    public async Task Glass_resume_real_halt_cutover_excludes_old_pending_saved_and_detached_work()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.EnableAutomaticAsync();
        await fixture.CommandAsync("attach");
        await fixture.HaltAsync("pending", notify: false);
        await fixture.HaltAsync("saved", notify: false);
        await fixture.Scheduler().ReconcileStoppedWorkAdviceAsync(Ct);
        fixture.Reply = "ReplaceReview";
        await fixture.FollowAsync("saved");
        await using var glass = await GlassFixture.StartAsync(fixture);
        var attached = await glass.StatusAsync();
        using var detach = await glass.DetachAsync(DetachBody(attached));
        Assert.Equal(HttpStatusCode.OK, detach.StatusCode);
        await fixture.HaltAsync("detached");
        Assert.Null((await fixture.RowAsync("detached")).StoppedWorkJudgment!.FollowAttachmentId);
        var evidence = RetainedBytes(fixture);
        var queueBytes = File.ReadAllBytes(BatonPaths.QueueFile);
        var obligations = File.ReadAllBytes(BatonPaths.ConductorObligationsFile);
        using var resume = await glass.ResumeAsync(DetachBody(await glass.StatusAsync()));
        Assert.Equal(HttpStatusCode.OK, resume.StatusCode);
        AssertRetainedBytes(evidence);
        Assert.Equal(queueBytes, File.ReadAllBytes(BatonPaths.QueueFile));
        Assert.Equal(obligations, File.ReadAllBytes(BatonPaths.ConductorObligationsFile));
        Assert.Single(fixture.Calls);
        await fixture.Scheduler().RecoverAttachedFollowAsync(Ct);
        foreach (var tag in new[] { "pending", "saved", "detached" })
        {
            var row = await fixture.RowAsync(tag);
            Assert.Equal(tag == "detached" ? null : attached.GetProperty("attachmentId").GetString(),
                row.StoppedWorkJudgment!.FollowAttachmentId);
            await fixture.Scheduler().NotifyOwnedHaltAsync(row, Ct);
            Assert.Null((await fixture.RowAsync(tag)).ReplacementReviewAction);
        }
        Assert.Single(fixture.Calls);
        await fixture.HaltAsync("future");
        Assert.Equal(2, fixture.Calls.Count);
        Assert.Equal("retained-thread", fixture.Calls[1].SessionId);
        Assert.True(fixture.Calls[1].ResumeSession);
        Assert.Equal((await glass.StatusAsync()).GetProperty("attachmentId").GetString(),
            (await fixture.RowAsync("future")).StoppedWorkJudgment!.FollowAttachmentId);
        Assert.NotNull((await fixture.RowAsync("future")).ReplacementReviewAction);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("someone-else@example.test")]
    [InlineData("operator@example.test, operator@example.test")]
    public async Task Glass_resume_denied_identity_never_changes_retained_state(string? login)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        await fixture.CommandAsync("detach");
        await using var glass = await GlassFixture.StartAsync(fixture);
        var evidence = RetainedBytes(fixture);
        var registration = File.ReadAllBytes(fixture.RegistrationPath);
        using var response = await glass.ResumeAsync(DetachBody(await glass.StatusAsync()), login);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(registration, File.ReadAllBytes(fixture.RegistrationPath));
        AssertRetainedBytes(evidence);
        Assert.Empty(fixture.Calls);
    }

    [Theory]
    [InlineData("repository")]
    [InlineData("holder")]
    [InlineData("claimGeneration")]
    [InlineData("attachmentId")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("overlong")]
    public async Task Glass_resume_requires_only_exact_displayed_identity(string change)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        await fixture.CommandAsync("detach");
        await using var glass = await GlassFixture.StartAsync(fixture);
        var body = JsonNode.Parse(DetachBody(await glass.StatusAsync()))!.AsObject();
        if (change == "unknown") body["permissionGrant"] = new JsonObject();
        else if (change == "missing") body.Remove("attachmentId");
        else if (change == "overlong") body["holder"] = new string('x', 4097);
        else if (change != "duplicate") body[change] = "stale";
        var text = body.ToJsonString();
        if (change == "duplicate") text = text[..^1] + ",\"holder\":\"holder\"}";
        var before = File.ReadAllBytes(fixture.RegistrationPath);
        using var response = await glass.ResumeAsync(text);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(before, File.ReadAllBytes(fixture.RegistrationPath));
        Assert.Equal("Resume refused: session busy, conductor identity or retained state could not be verified. Refresh before retrying.",
            await response.Content.ReadAsStringAsync(Ct));
        Assert.DoesNotContain(fixture.Root, await response.Content.ReadAsStringAsync(Ct));
        Assert.Empty(fixture.Calls);
    }

    [Theory]
    [InlineData("frozen")]
    [InlineData("unresolved-launch")]
    [InlineData("partial-response")]
    [InlineData("missing-response")]
    [InlineData("missing-receipt")]
    [InlineData("missing-journal")]
    [InlineData("conflicting-receipt")]
    [InlineData("journal")]
    [InlineData("identity")]
    [InlineData("source")]
    [InlineData("decision")]
    [InlineData("missing-decision")]
    [InlineData("partial-transcript")]
    [InlineData("empty-events")]
    [InlineData("state")]
    [InlineData("thread")]
    [InlineData("request")]
    [InlineData("trust")]
    [InlineData("claim")]
    [InlineData("registration")]
    [InlineData("missing-registration")]
    [InlineData("missing-state")]
    [InlineData("inspection-bound")]
    public async Task Glass_resume_invalid_retained_evidence_refuses_without_recovery_writes(string change)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        fixture.Reply = "ReplaceReview";
        await fixture.HaltAsync("retained");
        await fixture.CommandAsync("detach");
        await using var glass = await GlassFixture.StartAsync(fixture);
        var body = DetachBody(await glass.StatusAsync());
        var directory = SessionDirectory(fixture);
        var statePath = Path.Combine(directory, "session.json");
        var state = JsonNode.Parse(File.ReadAllText(statePath))!;
        switch (change)
        {
            case "frozen": state["frozen"] = true; File.WriteAllText(statePath, state.ToJsonString()); break;
            case "unresolved-launch": state["sessionId"] = null; File.WriteAllText(statePath, state.ToJsonString()); goto case "missing-response";
            case "partial-response": File.WriteAllText(fixture.EventEvidencePath("retained", "response.json"), "{}"); break;
            case "missing-response": FileCleanup.EnsureDeleted(fixture.EventEvidencePath("retained", "response.json")); break;
            case "missing-receipt": FileCleanup.EnsureDeleted(fixture.EventEvidencePath("retained", "receipt.txt")); break;
            case "missing-journal": FileCleanup.EnsureDeleted(Path.Combine(directory, "delivery.jsonl")); break;
            case "conflicting-receipt": File.WriteAllText(fixture.EventEvidencePath("retained", "receipt.txt"), "foreign"); break;
            case "journal": File.WriteAllText(Path.Combine(directory, "delivery.jsonl"), "{}"); break;
            case "identity": File.WriteAllText(fixture.EventEvidencePath("retained", "identity.json"), "{}"); break;
            case "source": File.WriteAllText(fixture.EventEvidencePath("retained", "source.json"), "{}"); break;
            case "missing-decision": FileCleanup.EnsureDeleted(fixture.EventEvidencePath("retained", "decision.json")); break;
            case "partial-transcript": File.WriteAllText(fixture.EventEvidencePath("retained", "partial-transcript.jsonl"), "partial"); break;
            case "empty-events":
                DirectoryCleanup.EnsureDeletedRecursively(Path.Combine(directory, "events"));
                Directory.CreateDirectory(Path.Combine(directory, "events"));
                FileCleanup.EnsureDeleted(Path.Combine(directory, "delivery.jsonl"));
                break;
            case "decision":
                var decisionPath = fixture.EventEvidencePath("retained", "decision.json");
                var decision = JsonNode.Parse(File.ReadAllText(decisionPath))!;
                decision["sessionId"] = "foreign-thread";
                File.WriteAllText(decisionPath, decision.ToJsonString());
                break;
            case "state": File.WriteAllText(statePath, "{}"); break;
            case "thread": state["sessionId"] = "foreign-thread"; File.WriteAllText(statePath, state.ToJsonString()); break;
            case "request":
                var requestPath = Path.Combine(directory, "request.json");
                var request = JsonNode.Parse(File.ReadAllText(requestPath))!;
                request["initialInstructions"] = "changed";
                File.WriteAllText(requestPath, request.ToJsonString());
                break;
            case "trust": File.WriteAllText(fixture.CeilingPath, "{}"); break;
            case "claim": await ConductorClaimStore.TakeoverAsync(Identity, "replacement", "test", fixture.Root, cancellationToken: Ct); break;
            case "registration": File.WriteAllText(fixture.RegistrationPath, "{}"); break;
            case "missing-registration": FileCleanup.EnsureDeleted(fixture.RegistrationPath); break;
            case "missing-state": FileCleanup.EnsureDeleted(statePath); break;
            case "inspection-bound":
                File.WriteAllText(Path.Combine(directory, "delivery.jsonl"), new string('x', ConductorFollowSession.MaxResponseBytes + 1));
                break;
        }
        var evidence = RetainedBytes(fixture);
        var registration = File.Exists(fixture.RegistrationPath) ? File.ReadAllBytes(fixture.RegistrationPath) : null;
        var status = await glass.StatusAsync();
        Assert.False(status.GetProperty("resumeEligible").GetBoolean());
        using var response = await glass.ResumeAsync(body);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(registration, File.Exists(fixture.RegistrationPath) ? File.ReadAllBytes(fixture.RegistrationPath) : null);
        AssertRetainedBytes(evidence);
        Assert.Equal(evidence.Keys.Order(), RetainedBytes(fixture).Keys.Order());
        Assert.Single(fixture.Calls);
        Assert.Equal(0, fixture.LegacyCalls);
    }

    [Fact]
    public async Task Glass_resume_busy_turn_is_bounded_and_never_waits_with_queue_lock()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        await using var glass = await GlassFixture.StartAsync(fixture);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.WaitInBroker = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); };
        var halt = fixture.HaltAsync("running");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            using var detach = await glass.DetachAsync(DetachBody(await glass.StatusAsync()));
            Assert.Equal(HttpStatusCode.OK, detach.StatusCode);
            var detached = await glass.StatusAsync();
            Assert.Equal("detached", detached.GetProperty("state").GetString());
            Assert.False(detached.GetProperty("resumeEligible").GetBoolean());
            var registration = File.ReadAllBytes(fixture.RegistrationPath);
            var resume = glass.ResumeAsync(DetachBody(detached));
            var timer = Stopwatch.StartNew();
            await QueueStore.MutateAsync(BatonPaths.QueueFile, queue => queue with { Held = true }, Ct)
                .WaitAsync(TimeSpan.FromMilliseconds(750), Ct);
            using var response = await resume.WaitAsync(TimeSpan.FromSeconds(4), Ct);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.InRange(timer.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(4));
            Assert.Equal(registration, File.ReadAllBytes(fixture.RegistrationPath));
            Assert.False(halt.IsCompleted);
            Assert.Single(fixture.Calls);
        }
        finally { release.TrySetResult(); await halt; }
    }

    [Theory]
    [InlineData("queue")]
    [InlineData("claim")]
    public async Task Glass_resume_busy_store_refuses_with_bounded_lock_wait(string store)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        await fixture.CommandAsync("detach");
        await using var glass = await GlassFixture.StartAsync(fixture);
        var body = DetachBody(await glass.StatusAsync());
        var registration = File.ReadAllBytes(fixture.RegistrationPath);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = Task.Run(() =>
        {
            bool Hold() { entered.TrySetResult(); release.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct).GetAwaiter().GetResult(); return true; }
            return store == "claim" ? ConductorClaimStore.WithCurrentClaim(Identity, fixture.Root, _ => Hold())
                : MutexGuardedFileLock.RunUnderLock(BatonPaths.QueueFile, QueueStore.LockNamePrefix, TimeSpan.FromSeconds(1), Hold);
        }, Ct);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            using var response = await glass.ResumeAsync(body).WaitAsync(TimeSpan.FromSeconds(4), Ct);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal(registration, File.ReadAllBytes(fixture.RegistrationPath));
            Assert.Empty(fixture.Calls);
        }
        finally { release.TrySetResult(); await blocker; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Glass_resume_linked_events_refuses_without_touching_link_target(bool output)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        await fixture.HaltAsync("retained");
        await fixture.CommandAsync("detach");
        await using var glass = await GlassFixture.StartAsync(fixture);
        var body = DetachBody(await glass.StatusAsync());
        var events = output ? Path.Combine(Path.GetDirectoryName(fixture.EventEvidencePath("retained", "identity.json"))!, "output")
            : Path.Combine(SessionDirectory(fixture), "events");
        var target = Path.Combine(fixture.Root, "link-target");
        Directory.Move(events, target);
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "/c", "mklink", "/J", events, target }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        await BoundedProcessWait.RunToExitAsync(process, TimeSpan.FromSeconds(30), Ct);
        Assert.Equal(0, process.ExitCode);
        try
        {
            var evidence = Directory.GetFiles(target, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
            Assert.False((await glass.StatusAsync()).GetProperty("resumeEligible").GetBoolean());
            using var response = await glass.ResumeAsync(body);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            AssertRetainedBytes(evidence);
            Assert.Single(fixture.Calls);
        }
        finally { DirectoryCleanup.DeleteRecursively(events); }
    }

    [Fact]
    public async Task Glass_resume_incomplete_body_times_out_without_acceptance()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        await fixture.CommandAsync("detach");
        await using var glass = await GlassFixture.StartAsync(fixture);
        var registration = File.ReadAllBytes(fixture.RegistrationPath);
        Assert.Contains(" 408 ", await glass.SlowDetachStatusLineAsync("resume"));
        Assert.Equal(registration, File.ReadAllBytes(fixture.RegistrationPath));
        Assert.Empty(fixture.Calls);
    }

    [Fact]
    public async Task Glass_concurrent_detach_resume_serializes_exact_cutover_and_old_identity_becomes_stale()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        await fixture.CommandAsync("detach");
        await using var glass = await GlassFixture.StartAsync(fixture);
        var body = DetachBody(await glass.StatusAsync());
        var responses = await Task.WhenAll(glass.ResumeAsync(body), glass.DetachAsync(body));
        try
        {
            Assert.Equal(HttpStatusCode.OK, responses[0].StatusCode);
            Assert.Contains(responses[1].StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict });
            Assert.Equal("attached", (await glass.StatusAsync()).GetProperty("state").GetString());
            var registration = File.ReadAllBytes(fixture.RegistrationPath);
            using var staleResume = await glass.ResumeAsync(body);
            using var staleDetach = await glass.DetachAsync(body);
            Assert.Equal(HttpStatusCode.Conflict, staleResume.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, staleDetach.StatusCode);
            Assert.Equal(registration, File.ReadAllBytes(fixture.RegistrationPath));
            Assert.Empty(fixture.Calls);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Fact]
    public async Task Glass_claim_change_racing_resume_never_reattaches_the_displaced_conductor()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CommandAsync("attach");
        await fixture.CommandAsync("detach");
        await using var glass = await GlassFixture.StartAsync(fixture);
        var body = DetachBody(await glass.StatusAsync());
        var registration = File.ReadAllBytes(fixture.RegistrationPath);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = Task.Run(() => MutexGuardedFileLock.RunUnderLock(BatonPaths.QueueFile,
            QueueStore.LockNamePrefix, TimeSpan.FromSeconds(1), () =>
            {
                entered.TrySetResult();
                release.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct).GetAwaiter().GetResult();
            }), Ct);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            var resume = glass.ResumeAsync(body);
            await ConductorClaimStore.TakeoverAsync(Identity, "replacement", "racing control", fixture.Root, cancellationToken: Ct);
            release.TrySetResult();
            using var response = await resume.WaitAsync(TimeSpan.FromSeconds(4), Ct);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal(registration, File.ReadAllBytes(fixture.RegistrationPath));
            Assert.Empty(fixture.Calls);
        }
        finally { release.TrySetResult(); await blocker; }
    }

    private static string SessionDirectory(Fixture fixture) =>
        JsonNode.Parse(File.ReadAllText(fixture.RegistrationPath))!["sessionDirectory"]!.GetValue<string>();

    private static Dictionary<string, byte[]> RetainedBytes(Fixture fixture) =>
        Directory.EnumerateFiles(Path.Combine(fixture.Root, "conductor-follow"), "*", SearchOption.AllDirectories)
            .Where(path => path != fixture.RegistrationPath).ToDictionary(path => path, File.ReadAllBytes);

    private static void AssertRetainedBytes(Dictionary<string, byte[]> evidence)
    {
        foreach (var file in evidence) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
    }
}
