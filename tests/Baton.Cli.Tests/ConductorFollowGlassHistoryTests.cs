using System.Text.Json;
using System.Text.Json.Nodes;
using Baton.Accounting;
using Baton.Queue;
using Baton.Status;

namespace Baton.Cli.Tests;

public sealed partial class ConductorFollowDeliveryTests
{
    [Fact]
    public async Task Glass_completed_hold_survives_pending_clear_replay_and_later_task_transition()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.EnableAutomaticAsync();
        await fixture.CommandAsync("attach");
        fixture.Reply = "Hold";
        var launches = 0;
        using var scheduler = fixture.Scheduler(launch: (_, _) =>
        {
            launches++;
            throw new InvalidOperationException("Hold cannot launch a replacement.");
        });
        await fixture.HaltAsync("history", scheduler: scheduler);
        Assert.False((await fixture.RowAsync("history")).StoppedWorkJudgment!.FollowContinuationPending);
        await scheduler.TickOnceAsync(Ct);
        await fixture.FollowAsync("history");
        await scheduler.RecoverAttachedFollowAsync(Ct);
        await QueueStore.MutateAsync(BatonPaths.QueueFile, queue => queue with
        {
            Items = queue.Items.Select(item => item with { Halted = false, State = QueueItemState.Done }).ToList(),
        }, Ct);
        await using var glass = await GlassFixture.StartAsync(fixture);
        var status = await glass.StatusAsync();
        var judgment = Assert.Single(status.GetProperty("history").GetProperty("judgments").EnumerateArray());
        Assert.Equal("Hold", judgment.GetProperty("decision").GetString());
        Assert.Equal("history", judgment.GetProperty("tag").GetString());
        Assert.Equal(Head, judgment.GetProperty("sourceHeadSha").GetString());
        var source = JsonNode.Parse(File.ReadAllText(fixture.EventEvidencePath("history", "source.json")))!;
        Assert.Equal(source["context"]!["observedAt"]!.GetValue<string>(), judgment.GetProperty("sourceObservedAt").GetString());
        Assert.True(status.GetProperty("stopEligible").GetBoolean());
        Assert.True(status.GetProperty("holdEligible").GetBoolean());
        Assert.Empty(status.GetProperty("actions").EnumerateArray());
        Assert.Single(fixture.Calls);
        Assert.Equal(0, launches);
        Assert.DoesNotContain(fixture.Root, judgment.GetRawText());
        Assert.DoesNotContain("retained-thread", judgment.GetRawText());
        Assert.False(judgment.TryGetProperty("state", out _));
        Assert.False(judgment.TryGetProperty("rationale", out _));
        Assert.False(judgment.TryGetProperty("completedAt", out _));
    }
}
