using Baton.Artifacts;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Status;

namespace Baton.Tests.Status;

public sealed class CodexUsageParserTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Room_usage_sums_three_turns_only_when_the_capture_is_complete(bool rolled, bool truncated)
    {
        var root = Path.Combine(Path.GetTempPath(), $"codex-usage-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("codex-three-turns");
            var start = DateTime.UtcNow;
            var request = new ExecutionRequest(
                executionId, new WorkflowId("usage"), new StepId("implement"), "implement",
                Inputs: [], Outputs: [], Timeout: TimeSpan.FromSeconds(30), Environment: [],
                UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>(), Adapter: "codex");
            LogEntry[] entries =
            [
                new LogEntry.FlowLogEntry(new FlowEvent.ExecutionRequestAccepted(request)),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(executionId, Pid: 1), start),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(executionId, 0, CoreExitReason.Natural), start.AddSeconds(1)),
            ];
            var lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "codex-three-turn-usage.jsonl"));
            var output = ArtifactManager.ResolveOutputDirectory(root, executionId);
            Directory.CreateDirectory(output);
            File.WriteAllLines(Path.Combine(output, ExecutionStreamLogger.StdoutLogFileName), rolled ? lines.Skip(2) : lines);
            if (rolled)
            {
                File.WriteAllLines(Path.Combine(output, ExecutionStreamLogger.StdoutRolloverFileName), lines.Take(2));
            }

            if (truncated)
            {
                File.WriteAllText(Path.Combine(output, ExecutionStreamLogger.StdoutTruncationMarkerFileName), "");
            }

            var view = Assert.Single(ExecutionUsageProjector.BuildByExecutionId(entries, root)).Value;
            if (truncated)
            {
                Assert.Null(view.TokensIn);
                Assert.Null(view.CacheReadTokens);
                Assert.Null(view.TokensOut);
                Assert.Null(view.ThinkingTokens);
                Assert.Equal(ExecutionUsageView.StreamTruncatedByRolloverReason, view.BilledReconciliationUnavailable);
                return;
            }

            Assert.Equal(1500, view.TokensIn);
            Assert.Equal(4500, view.CacheReadTokens);
            Assert.Equal(6000, view.TokensIn + view.CacheReadTokens);
            Assert.Equal(600, view.TokensOut);
            Assert.Equal(120, view.ThinkingTokens);
            Assert.Equal(3, view.Turns);
            Assert.Null(view.CacheCreationTokens);
            Assert.Equal(2100, view.BilledTokens);
            Assert.Equal(view.BilledTokens, view.LiveBilledTokens);
            Assert.Equal(0, view.BilledUnderReadTokens);
            Assert.Equal(view, Assert.Single(ExecutionUsageProjector.BuildByExecutionId(entries, root)).Value);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public void Execution_sum_is_isolated_per_read_and_preserves_partial_dimensions()
    {
        var parser = new CodexUsageParser();
        string[] lines =
        [
            """{"type":"turn.completed","usage":{"cached_input_tokens":10}}""",
            "unrelated output",
            """{"type":"turn.completed","usage":{"output_tokens":20,"cache_write_input_tokens":3}}""",
            """{"type":"turn.completed","usage":{"cached_input_tokens":5,"cache_write_input_tokens":4}}""",
        ];
        var total = parser.ParseExecutionUsage(lines);
        Assert.NotNull(total);
        Assert.Null(total.TokensIn);
        Assert.Null(total.ThinkingTokens);
        Assert.Equal(15, total.CacheReadTokens);
        Assert.Equal(20, total.TokensOut);
        Assert.Equal(7, total.CacheCreationTokens);
        Assert.Equal(3, total.Turns);
        Assert.Equal(total, parser.ParseExecutionUsage(lines));
        Assert.Null(parser.ParseExecutionUsage(["unrelated output"]));
        Assert.True(parser.TryParseFinalUsage(lines[0], out var single));
        Assert.Equal(single, parser.ParseExecutionUsage([lines[0]]));
    }

    [Fact]
    public void App_server_dynamic_tool_start_is_counted_by_the_shared_live_monitor_parser()
    {
        const string line = """
            {"type":"item.started","item":{"type":"mcp_tool_call","tool":"baton_write_output"}}
            """;
        var parser = new CodexUsageParser();

        Assert.Equal("baton_write_output", parser.TryParseToolName(line));
        Assert.Equal(1, parser.CountToolSteps(line));
        Assert.Equal(0, parser.CountToolSteps(
            "{\"type\":\"item.completed\",\"item\":{\"type\":\"mcp_tool_call\"}}"));
    }

    [Fact]
    public void Completed_turn_separates_uncached_input_from_cache_and_preserves_other_usage_fields()
    {
        var parser = new CodexUsageParser();
        const string line = """
            {"type":"turn.completed","usage":{"input_tokens":14750,"cached_input_tokens":8960,"cache_write_input_tokens":17,"output_tokens":211,"reasoning_output_tokens":48}}
            """;

        Assert.True(parser.TryParseFinalUsage(line, out var usage));
        Assert.NotNull(usage);
        Assert.Equal(5790, usage.TokensIn);
        Assert.Equal(8960, usage.CacheReadTokens);
        Assert.Equal(17, usage.CacheCreationTokens);
        Assert.Equal(211, usage.TokensOut);
        Assert.Equal(48, usage.ThinkingTokens);
        Assert.Equal(1, usage.Turns);
    }

    [Fact]
    public void Incremental_and_final_parsing_use_the_same_per_turn_semantics()
    {
        var parser = new CodexUsageParser();
        const string line = """
            {"type":"turn.completed","usage":{"input_tokens":19579,"cached_input_tokens":11008,"output_tokens":9,"reasoning_output_tokens":0}}
            """;

        Assert.True(parser.TryParseFinalUsage(line, out var final));
        Assert.True(parser.TryParseIncrementalUsage(line, out var incremental));
        Assert.Equal(final, incremental);
    }

    [Fact]
    public void Cached_input_larger_than_total_input_clamps_uncached_input_to_zero()
    {
        var parser = new CodexUsageParser();
        const string line = """
            {"type":"turn.completed","usage":{"input_tokens":5,"cached_input_tokens":8}}
            """;

        Assert.True(parser.TryParseFinalUsage(line, out var usage));
        Assert.Equal(0, usage!.TokensIn);
        Assert.Equal(8, usage.CacheReadTokens);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"type\":\"turn.started\"}")]
    [InlineData("{\"type\":\"turn.completed\"}")]
    [InlineData("{\"type\":\"turn.completed\",\"usage\":{}}")]
    [InlineData("{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":null,\"cached_input_tokens\":null,\"cache_write_input_tokens\":null,\"output_tokens\":null,\"reasoning_output_tokens\":null}}")]
    public void Malformed_unrelated_and_all_null_lines_are_not_usage(string line)
    {
        var parser = new CodexUsageParser();

        Assert.False(parser.TryParseFinalUsage(line, out var usage));
        Assert.Null(usage);
    }

    [Fact]
    public void A_partial_usage_object_preserves_absence_instead_of_fabricating_zeroes()
    {
        var parser = new CodexUsageParser();
        const string line = """
            {"type":"turn.completed","usage":{"cached_input_tokens":321}}
            """;

        Assert.True(parser.TryParseFinalUsage(line, out var usage));
        Assert.Null(usage!.TokensIn);
        Assert.Equal(321, usage.CacheReadTokens);
        Assert.Null(usage.CacheCreationTokens);
        Assert.Null(usage.TokensOut);
        Assert.Null(usage.ThinkingTokens);
        Assert.Equal(1, usage.Turns);
    }
}
