using Baton.Queue;

namespace Baton.Tests.Queue;

public sealed class RequiredCheckPolicyTests
{
    private static readonly DateTimeOffset Started = DateTimeOffset.Parse("2026-10-01T00:00:00Z");

    [Theory]
    [InlineData("unknown")]
    [InlineData("skipped")]
    [InlineData("")]
    public void Unsupported_typed_verdict_cannot_fall_through_to_green(string verdict) =>
        Assert.Null(PullRequestChecks.SummarizeRequired([new("ci", 15368)],
            [new("ci", 15368, "check-run", 1, verdict, Started)]));

    [Fact]
    public void Missing_third_requirement_stays_pending_until_its_own_witness_exists()
    {
        RequiredCheckRequirement[] policy = [new("ci", 15368), new("lint", 15368), new("shape", 15368)];
        RequiredCheckWitness[] subset = [new("lint", 15368, "check-run", 1, PullRequestChecks.Passing, Started),
            new("shape", 15368, "check-run", 2, PullRequestChecks.Passing, Started)];
        Assert.Equal(PullRequestChecks.Pending, PullRequestChecks.SummarizeRequired(policy, subset));
        Assert.Equal(PullRequestChecks.Passing, PullRequestChecks.SummarizeRequired(policy,
            [.. subset, new("ci", 15368, "check-run", 3, PullRequestChecks.Passing, Started)]));
    }

    [Fact]
    public void An_unordered_pending_required_run_cannot_be_hidden_by_an_older_green()
    {
        Assert.Equal(PullRequestChecks.Pending, PullRequestChecks.SummarizeRequired([new("ci", 15368)],
            [new("ci", 15368, "check-run", 1, PullRequestChecks.Passing, Started),
                new("ci", 15368, "check-run", 2, PullRequestChecks.Pending, null)]));
    }

    [Fact]
    public void Optional_pending_and_failure_do_not_change_passing_required_evidence() =>
        Assert.Equal(PullRequestChecks.Passing, PullRequestChecks.SummarizeRequired([new("ci", 15368)],
            [new("ci", 15368, "check-run", 1, PullRequestChecks.Passing, Started),
                new("optional", 999, "check-run", 2, PullRequestChecks.Pending, null),
                new("optional-failed", 999, "check-run", 3, PullRequestChecks.Failing, Started)]));
}
