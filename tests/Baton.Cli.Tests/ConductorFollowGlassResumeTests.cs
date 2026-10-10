using System.Net;
using System.Text.Json;
using Baton.Queue;
using Baton.Status;

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
        using var resume = await glass.ResumeAsync(DetachBody(await glass.StatusAsync()));
        Assert.Equal(HttpStatusCode.OK, resume.StatusCode);
        AssertRetainedBytes(evidence);
        Assert.Single(fixture.Calls);
        await fixture.Scheduler().RecoverAttachedFollowAsync(Ct);
        foreach (var tag in new[] { "pending", "saved", "detached" })
        {
            var row = await fixture.RowAsync(tag);
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

    private static Dictionary<string, byte[]> RetainedBytes(Fixture fixture) =>
        Directory.EnumerateFiles(Path.Combine(fixture.Root, "conductor-follow"), "*", SearchOption.AllDirectories)
            .Where(path => path != fixture.RegistrationPath).ToDictionary(path => path, File.ReadAllBytes);

    private static void AssertRetainedBytes(Dictionary<string, byte[]> evidence)
    {
        foreach (var file in evidence) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
    }
}
