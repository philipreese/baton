using Baton.Domain;
using Baton.Queue;
using Baton.Status;

namespace Baton.Tests.Queue;

/// <summary>
/// Every arm of the lifecycle #1934 slice 2 encodes, as polarity PAIRS: each transition is asserted
/// beside the one input change that produces the other branch, because a test that only ever asserts
/// the fix round cannot tell "BLOCK routes to fix" from "everything routes to fix".
/// </summary>
public sealed class WorkItemLifecycleTests
{
    private static WorkItemObservation At(
        WorkStage stage,
        string? outcome = WorkflowOutcome.Succeeded,
        ReviewVerdict? verdict = null,
        int? pr = 42,
        string? prHead = "aaaaaaaabbbbbbbb",
        string? workspaceHead = "aaaaaaaabbbbbbbb",
        int round = 0) =>
        new(stage, round, "1934-lane", outcome, verdict, pr, prHead, workspaceHead);

    private static ReviewVerdict Verdict(params ReviewFinding[] findings) =>
        new("PR #42", findings, "the summary, which nothing routes on");

    private static ReviewFinding Finding(
        ReviewFindingSeverity severity, ReviewFindingStatus status, string claim = "the claim") =>
        new(severity, claim, status, new ReviewFindingAnchor("src/Baton/Queue/QueueItem.cs", 12), "the detail");

    [Fact]
    public void An_implement_lane_that_opened_a_pr_goes_to_review()
    {
        var transition = WorkItemLifecycle.Decide(At(WorkStage.Implement));

        Assert.Equal(WorkItemTransitionKind.Dispatch, transition.Kind);
        Assert.Equal(WorkStage.Review, transition.NextStage);
        Assert.Contains("#42", transition.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_implement_lane_with_no_pr_needs_the_operator_rather_than_reviewing_nothing()
    {
        // The control arm above is the same lane WITH a PR: the only input that changed is the PR, so
        // this arm measures the PR and not some other refusal.
        var transition = WorkItemLifecycle.Decide(At(WorkStage.Implement, pr: null, prHead: null));

        Assert.Equal(WorkItemTransitionKind.NeedsOperator, transition.Kind);
        Assert.Contains("no pull request is open", transition.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_confirmed_high_finding_blocks_into_a_fix_round_and_bumps_the_round()
    {
        var transition = WorkItemLifecycle.Decide(At(
            WorkStage.Review, verdict: Verdict(Finding(ReviewFindingSeverity.High, ReviewFindingStatus.Confirmed)),
            round: 1));

        Assert.Equal(WorkItemTransitionKind.Dispatch, transition.Kind);
        Assert.Equal(WorkStage.Fix, transition.NextStage);
        Assert.Equal(2, transition.Round);
    }

    [Theory]
    // Both fields must line up for a BLOCK, so each is falsified on its own: a high finding the
    // reviewer refuted is not a block, and a confirmed medium one is not either.
    [InlineData(ReviewFindingSeverity.High, ReviewFindingStatus.Refuted)]
    [InlineData(ReviewFindingSeverity.High, ReviewFindingStatus.Unverified)]
    [InlineData(ReviewFindingSeverity.Medium, ReviewFindingStatus.Confirmed)]
    [InlineData(ReviewFindingSeverity.Low, ReviewFindingStatus.Confirmed)]
    public void A_finding_that_is_not_both_high_and_confirmed_approves(
        ReviewFindingSeverity severity, ReviewFindingStatus status)
    {
        var transition = WorkItemLifecycle.Decide(At(WorkStage.Review, verdict: Verdict(Finding(severity, status))));

        Assert.Equal(WorkItemTransitionKind.Stop, transition.Kind);
        Assert.Equal(WorkStage.Ready, transition.NextStage);
    }

    [Fact]
    public void An_empty_verdict_approves_and_a_ready_item_is_then_never_dispatched_again()
    {
        var approved = WorkItemLifecycle.Decide(At(WorkStage.Review, verdict: Verdict()));
        Assert.Equal(WorkStage.Ready, approved.NextStage);

        // The item as the advancer leaves it: ready, and re-observed on the next tick.
        var again = WorkItemLifecycle.Decide(At(WorkStage.Ready));

        Assert.Equal(WorkItemTransitionKind.None, again.Kind);
        Assert.Null(again.NextStage);
    }

    [Fact]
    public void A_review_that_wrote_no_verdict_is_not_read_as_an_approval()
    {
        var transition = WorkItemLifecycle.Decide(At(WorkStage.Review, verdict: null));

        Assert.Equal(WorkItemTransitionKind.NeedsOperator, transition.Kind);
        Assert.Contains("no readable", transition.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stalled_lane_whose_work_is_pushed_goes_to_re_review_not_fix()
    {
        var transition = WorkItemLifecycle.Decide(At(
            WorkStage.Implement, outcome: WorkflowOutcome.Failed,
            prHead: "deadbeefdeadbeef", workspaceHead: "deadbeefdeadbeef"));

        Assert.Equal(WorkItemTransitionKind.Dispatch, transition.Kind);
        Assert.Equal(WorkStage.ReReview, transition.NextStage);
    }

    [Fact]
    public void A_stalled_lane_whose_commit_never_reached_the_pr_goes_to_continue()
    {
        // Same failed lane, same PR — the ONE difference from the arm above is that the workspace head
        // is not the PR's head, which is what "unpushed" means.
        var transition = WorkItemLifecycle.Decide(At(
            WorkStage.Implement, outcome: WorkflowOutcome.Failed,
            prHead: "deadbeefdeadbeef", workspaceHead: "0000111122223333"));

        Assert.Equal(WorkItemTransitionKind.Dispatch, transition.Kind);
        Assert.Equal(WorkStage.Continue, transition.NextStage);
    }

    [Fact]
    public void A_stalled_review_lane_is_re_reviewed_rather_than_continued()
    {
        var transition = WorkItemLifecycle.Decide(At(WorkStage.Review, outcome: WorkflowOutcome.Cancelled));

        Assert.Equal(WorkStage.ReReview, transition.NextStage);
    }

    [Fact]
    public void An_unsettled_room_produces_nothing()
    {
        var transition = WorkItemLifecycle.Decide(At(WorkStage.Implement, outcome: null));

        Assert.Equal(WorkItemTransitionKind.None, transition.Kind);
    }
}
