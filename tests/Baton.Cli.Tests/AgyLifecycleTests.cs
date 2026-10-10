using System.Text.Json;
using Baton.Queue;

namespace Baton.Cli.Tests;

public sealed class AgyLifecycleTests
{
    [Fact]
    public void Named_transport_only_stage_is_retained_without_an_axis_rationale()
    {
        var task = TaskOptionsParser.Parse(["submit", "--issue", "42", "--project", "C:/repo",
            "--declared-size", "small", "--size-rationale", "one cluster", "--scope", "engine",
            "--stage", "review", "--enable-agy-correction"]);
        var selection = Assert.Single(task.StageSelections!);
        Assert.Equal(WorkStage.Review, selection.Stage);
        Assert.Null(selection.Adapter);
        Assert.Null(selection.Reason);
        Assert.Contains("\"EnableAgyCorrection\":true", JsonSerializer.Serialize(selection), StringComparison.Ordinal);
    }
}
