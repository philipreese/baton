using Baton.Domain;
using Baton.Scheduling;

namespace Baton.Tests.Scheduling;

/// <summary>
/// #1373: the continuation preamble a retry-after-timeout carries. One template, pinned for its exact
/// wording — the text IS the deliverable here, not an implementation detail of one: it is the only
/// thing that tells attempt 2 that attempt 1 existed, and a reword that dropped "finish rather than
/// restart" would leave the machinery intact and the fix gone.
/// </summary>
public sealed class ContinuationBriefTests
{
    private static readonly StepId Implement = new("implement");

    private static StepState TimedOutStep(int consecutiveFailureCount) => new(
        Implement,
        StepStatus.Failed,
        new ExecutionId("exec-1"),
        new Dictionary<StepId, ExecutionId>(),
        ConsecutiveFailureCount: consecutiveFailureCount,
        LatestFailureReason: "Execution timed out.");

    [Fact]
    public void The_brief_names_the_attempt_the_budget_and_the_instruction_to_finish()
    {
        var brief = ContinuationBrief.ForRetryAfterTimeout(TimedOutStep(1), maxAttempts: 3, TimeSpan.FromMinutes(60));

        Assert.NotNull(brief);
        Assert.StartsWith("[baton] CONTINUATION BRIEF", brief, StringComparison.Ordinal);
        Assert.Contains("This is attempt 2 of 3.", brief, StringComparison.Ordinal);
        // #2058 review (MEDIUM): "at least", and the parenthesis naming what the figure is. #2019's
        // credit can push a killed predecessor past its configured box, so the flat equality this
        // sentence used to assert is a claim the brief cannot make -- and it is the one surface that
        // states the number to the worker itself.
        Assert.Contains(
            "Attempt 1 ran for at least its full 1h timeout budget (the configured box, before any "
            + "build-lock wait credit) and was killed by baton.",
            brief,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ran its full 1h timeout budget", brief, StringComparison.Ordinal);
        Assert.Contains("SAME workspace it left behind", brief, StringComparison.Ordinal);
        Assert.Contains("FINISH what attempt 1 started", brief, StringComparison.Ordinal);
        Assert.Contains("Do not restart it from the beginning", brief, StringComparison.Ordinal);
        // The prepended text has to hand off to the original brief, or a worker reads the whole thing
        // as one instruction set and cannot tell which half is its actual task.
        Assert.EndsWith("\n", brief, StringComparison.Ordinal);
        Assert.Contains("The original brief follows, unchanged.", brief, StringComparison.Ordinal);
    }

    [Fact]
    public void A_third_attempt_counts_from_the_step_and_not_from_a_constant()
    {
        var brief = ContinuationBrief.ForRetryAfterTimeout(TimedOutStep(2), maxAttempts: 4, TimeSpan.FromMinutes(90));

        Assert.NotNull(brief);
        Assert.Contains("This is attempt 3 of 4.", brief, StringComparison.Ordinal);
        Assert.Contains("Attempt 2 ran for at least its full 1h 30m timeout budget", brief, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ordinary_failure_gets_no_brief()
    {
        var failedStep = new StepState(
            Implement,
            StepStatus.Failed,
            new ExecutionId("exec-1"),
            new Dictionary<StepId, ExecutionId>(),
            ConsecutiveFailureCount: 1,
            LatestFailureReason: "Worker exited with non-zero code 1.");

        // Polarity, and the discriminating control for every arm above: the same step shape, differing
        // only in why it failed. Without this, a brief returned unconditionally would still pass them.
        Assert.Null(ContinuationBrief.ForRetryAfterTimeout(failedStep, maxAttempts: 3, TimeSpan.FromMinutes(60)));
    }

    [Fact]
    public void A_first_attempt_gets_no_brief()
    {
        var freshStep = new StepState(
            Implement,
            StepStatus.Pending,
            LatestExecutionId: null,
            new Dictionary<StepId, ExecutionId>());

        Assert.Null(ContinuationBrief.ForRetryAfterTimeout(freshStep, maxAttempts: 3, TimeSpan.FromMinutes(60)));
    }

    [Theory]
    [InlineData(45, "45s")]
    [InlineData(60, "1m")]
    [InlineData(3599, "59m")]
    [InlineData(3600, "1h")]
    [InlineData(5400, "1h 30m")]
    [InlineData(7200, "2h")]
    public void A_duration_reads_back_in_the_units_it_was_dispatched_in(int totalSeconds, string expected) =>
        Assert.Equal(expected, ContinuationBrief.DescribeDuration(TimeSpan.FromSeconds(totalSeconds)));
}
