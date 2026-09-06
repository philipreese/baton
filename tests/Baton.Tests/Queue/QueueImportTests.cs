using Baton.Queue;

namespace Baton.Tests.Queue;

/// <summary>
/// #1934 slice 1, item 6: importing a fixture in the scratchpad runner's own shape (Q7's cutover).
/// The fixture below is the shape the issue body names, field for field.
/// </summary>
public sealed class QueueImportTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 23, 30, 0, TimeSpan.Zero);

    private const string RunnerFixture = """
    [
      {
        "tag": "1934-queue",
        "role": "implement",
        "model": "opus",
        "effort": "high",
        "timeout": 95,
        "workspace": "C:\\repos\\w1934",
        "issue": 1934,
        "adapter": "claude",
        "maxToolSteps": 900,
        "tokenBudget": 4000000,
        "overrideRunway": "milestone night",
        "reason": "engine work needs the frontier tier",
        "pinModel": true,
        "external": false,
        "launched": true
      },
      {
        "tag": "1912-glass",
        "role": "review",
        "workspace": "C:\\repos\\w1912"
      }
    ]
    """;

    private static string SpecFor(string tag) => Path.Combine(@"C:\baton\queue\specs", $"{tag}.md");

    [Fact]
    public void The_runners_shape_imports_field_for_field()
    {
        var items = QueueImport.Parse(RunnerFixture, SpecFor, Now);

        Assert.Equal(2, items.Count);
        var first = items[0];
        Assert.Equal("1934-queue", first.Tag);
        Assert.Equal("implement", first.Role);
        Assert.Equal("opus", first.Model);
        Assert.Equal("high", first.Effort);
        Assert.Equal(95, first.TimeoutMinutes);
        Assert.Equal(@"C:\repos\w1934", first.Workspace);
        Assert.Equal(1934, first.Issue);
        Assert.Equal("claude", first.Adapter);
        Assert.Equal(900, first.MaxToolSteps);
        Assert.Equal(4_000_000L, first.TokenBudget);
        Assert.Equal("milestone night", first.OverrideRunwayReason);
        Assert.Equal("engine work needs the frontier tier", first.Reason);
        Assert.True(first.PinModel);
        Assert.False(first.External);
        Assert.Equal(SpecFor("1934-queue"), first.SpecFile);
    }

    [Fact]
    public void A_launched_tag_comes_in_launched_and_an_unlaunched_one_comes_in_queued()
    {
        var items = QueueImport.Parse(RunnerFixture, SpecFor, Now);

        // Both polarities from the SAME fixture: resetting a launched tag to queued would re-dispatch
        // a lane the operator already has running, and leaving an unlaunched one launched would strand
        // it forever.
        Assert.Equal(QueueItemState.Launched, items[0].State);
        Assert.Equal(Now, items[0].LaunchedAt);
        Assert.Equal(QueueItemState.Queued, items[1].State);
        Assert.Null(items[1].LaunchedAt);
    }

    [Fact]
    public void An_items_wrapper_object_is_accepted_as_well_as_a_bare_array()
    {
        var wrapped = $$"""{ "items": {{RunnerFixture}} }""";

        Assert.Equal(2, QueueImport.Parse(wrapped, SpecFor, Now).Count);
    }

    [Fact]
    public void An_item_with_no_workspace_refuses_the_whole_import()
    {
        const string json = """[{ "tag": "no-ws", "role": "implement" }]""";

        var ex = Assert.Throws<QueueStoreException>(() => QueueImport.Parse(json, SpecFor, Now));
        Assert.Contains("no-ws", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unusable_tag_refuses_the_whole_import_rather_than_dropping_that_item()
    {
        const string json = """
        [
          { "tag": "good", "role": "implement", "workspace": "C:\\repos\\w1" },
          { "tag": "../escape", "role": "implement", "workspace": "C:\\repos\\w2" }
        ]
        """;

        // A partial import at cutover time would look like a successful one and silently unqueue a
        // lane — which is why this refuses rather than importing 'good' alone.
        Assert.Throws<QueueStoreException>(() => QueueImport.Parse(json, SpecFor, Now));
    }

    [Fact]
    public void A_file_that_is_not_the_runners_shape_is_refused_with_a_sentence()
    {
        var ex = Assert.Throws<QueueStoreException>(() => QueueImport.Parse("""{"queue": 3}""", SpecFor, Now));
        Assert.Contains("items", ex.Message, StringComparison.Ordinal);
    }
}
