using Baton.Status;

namespace Baton.Cli.Tests;

/// <summary>
/// Unit coverage for <see cref="StatusCommand.FormatUsageSummary"/>, the human `baton status`
/// roll-up line (#1360, extended by #1581). #1569 added CacheReadTokens/CacheCreationTokens/
/// ThinkingTokens/BilledTokens to <see cref="ExecutionUsageView"/>'s JSON contract but left this
/// prose line unchanged; these tests pin the extension that wires them in.
/// </summary>
public class StatusCommandUsageSummaryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Observed_stream_contributions_are_qualified_in_selected_and_mixed_text(bool mixed)
    {
        var usages = new Dictionary<string, ExecutionUsageView>
        {
            ["stream"] = new(TokensIn: 30, TokensOut: 5, UsageCompleteness: "unavailable",
                ObservedBilledTokenFloor: new(35)),
        };
        if (mixed) usages["legacy"] = new(TokensIn: 100, TokensOut: 50);
        var text = StatusCommand.FormatUsageSummary(usages);
        Assert.Contains(mixed ? "130 tokens in" : "30 tokens in", text);
        Assert.Contains(mixed ? "55 tokens out" : "5 tokens out", text);
        Assert.Contains("1 observed incomplete contribution(s)", text);
        Assert.Contains("execution totals unavailable", text);
        Assert.Contains("35 observed billed token floor", text);
        Assert.DoesNotContain("35 billed tokens", text);
    }

    [Fact]
    public void Line_unchanged_from_before_1581_when_only_the_original_three_fields_are_reported()
    {
        var usageByExecutionId = new Dictionary<string, ExecutionUsageView>
        {
            ["exec-1"] = new ExecutionUsageView(WallClockMs: 1500, TokensIn: 100, TokensOut: 50, Turns: 2),
        };

        var line = StatusCommand.FormatUsageSummary(usageByExecutionId);

        Assert.Equal(
            "Usage: 1 execution(s), 1.5s execution time, 100 tokens in (1/1 reporting), " +
            "50 tokens out (1/1 reporting), 2 turns (1/1 reporting)",
            line);
    }

    [Fact]
    public void All_fields_present_print_billed_tokens_first_as_the_headline_followed_by_the_documented_order()
    {
        var usageByExecutionId = new Dictionary<string, ExecutionUsageView>
        {
            ["exec-1"] = new ExecutionUsageView(
                WallClockMs: 1000,
                TokensIn: 100,
                TokensOut: 50,
                Turns: 2,
                CacheReadTokens: 300,
                CacheCreationTokens: 400,
                ThinkingTokens: 25,
                BilledTokens: 550),
        };

        var line = StatusCommand.FormatUsageSummary(usageByExecutionId);

        Assert.Equal(
            "Usage: 1 execution(s), 1s execution time, 550 billed tokens (1/1 reporting), " +
            "100 tokens in (1/1 reporting), 50 tokens out (1/1 reporting), " +
            "300 cache read tokens (1/1 reporting), 400 cache creation tokens (1/1 reporting), " +
            "25 thinking tokens (1/1 reporting), 2 turns (1/1 reporting)",
            line);
    }

    [Fact]
    public void Cache_and_thinking_and_billed_parts_are_omitted_when_no_execution_reports_them()
    {
        var usageByExecutionId = new Dictionary<string, ExecutionUsageView>
        {
            ["exec-1"] = new ExecutionUsageView(WallClockMs: 2000, TokensIn: 10, TokensOut: 5),
        };

        var line = StatusCommand.FormatUsageSummary(usageByExecutionId);

        Assert.DoesNotContain("billed tokens", line);
        Assert.DoesNotContain("cache read tokens", line);
        Assert.DoesNotContain("cache creation tokens", line);
        Assert.DoesNotContain("thinking tokens", line);
        Assert.DoesNotContain("turns", line);
    }

    [Fact]
    public void Billed_tokens_reporting_count_is_disclosed_when_only_some_executions_report_it()
    {
        var usageByExecutionId = new Dictionary<string, ExecutionUsageView>
        {
            ["exec-1"] = new ExecutionUsageView(WallClockMs: 1000, BilledTokens: 200),
            ["exec-2"] = new ExecutionUsageView(WallClockMs: 1000),
        };

        var line = StatusCommand.FormatUsageSummary(usageByExecutionId);

        Assert.Contains("200 billed tokens (1/2 reporting)", line);
    }

    [Fact]
    public void Unresolved_child_does_not_turn_unknown_duration_into_zero_seconds()
    {
        var usageByExecutionId = new Dictionary<string, ExecutionUsageView>
        {
            ["grace-child"] = new ExecutionUsageView(Outcome: "Unresolved"),
        };

        var line = StatusCommand.FormatUsageSummary(usageByExecutionId);

        Assert.Equal("Usage: 1 execution(s), execution time unknown", line);
        Assert.DoesNotContain("0s execution time", line);
    }

    [Fact]
    public void Mixed_usage_summary_names_how_many_durations_are_observed()
    {
        var usageByExecutionId = new Dictionary<string, ExecutionUsageView>
        {
            ["settled"] = new ExecutionUsageView(WallClockMs: 1500),
            ["grace-child"] = new ExecutionUsageView(Outcome: "Unresolved"),
        };

        var line = StatusCommand.FormatUsageSummary(usageByExecutionId);

        Assert.Contains("1.5s execution time (1/2 with duration)", line);
    }
}
