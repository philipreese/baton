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
    public void A_review_lane_that_finished_during_teardown_routes_on_its_verdict_exactly_as_a_succeeded_one()
    {
        // #1945's word is SUCCEEDED-shaped (spec/baton.md §3): the lane satisfied its contract and the
        // timeout kill landed during teardown, so its verdict.json is on disk and readable. Asserted as
        // an equality against the Succeeded transition rather than a re-statement of the fix arm — that
        // is the claim ("routes exactly as Succeeded does"), and it cannot pass by accident.
        var verdict = Verdict(Finding(ReviewFindingSeverity.High, ReviewFindingStatus.Confirmed));

        var teardown = WorkItemLifecycle.Decide(At(
            WorkStage.Review, outcome: WorkflowOutcome.FinishedDuringTeardown, verdict: verdict, round: 1));

        Assert.Equal(
            WorkItemLifecycle.Decide(At(
                WorkStage.Review, outcome: WorkflowOutcome.Succeeded, verdict: verdict, round: 1)),
            teardown);
        Assert.Equal(WorkStage.Fix, teardown.NextStage);

        // The control that makes this arm about the WORD and not about the verdict: a word that is not
        // succeeded-shaped discards the same verdict and re-dispatches a review of the same head.
        var failed = WorkItemLifecycle.Decide(At(
            WorkStage.Review, outcome: WorkflowOutcome.Failed, verdict: verdict, round: 1));

        Assert.Equal(WorkStage.ReReview, failed.NextStage);
    }

    [Fact]
    public void An_implement_lane_that_finished_during_teardown_goes_to_review_exactly_as_a_succeeded_one()
    {
        var teardown = WorkItemLifecycle.Decide(At(
            WorkStage.Implement, outcome: WorkflowOutcome.FinishedDuringTeardown));

        Assert.Equal(
            WorkItemLifecycle.Decide(At(WorkStage.Implement, outcome: WorkflowOutcome.Succeeded)),
            teardown);
        Assert.Equal(WorkStage.Review, teardown.NextStage);
    }

    [Fact]
    public void A_re_review_cycle_reaches_the_operator_at_the_ceiling_rather_than_running_forever()
    {
        // One below the ceiling is the control: the same stalled review lane still dispatches, so the
        // arm below measures the ROUND and not the stall.
        var below = WorkItemLifecycle.Decide(At(
            WorkStage.ReReview, outcome: WorkflowOutcome.Failed, round: WorkStages.MaxRounds - 1));

        Assert.Equal(WorkItemTransitionKind.Dispatch, below.Kind);
        Assert.Equal(WorkStages.MaxRounds, below.Round);

        var atCeiling = WorkItemLifecycle.Decide(At(
            WorkStage.ReReview, outcome: WorkflowOutcome.Failed, round: WorkStages.MaxRounds));

        Assert.Equal(WorkItemTransitionKind.NeedsOperator, atCeiling.Kind);
        Assert.Contains("re-review", atCeiling.Reason, StringComparison.Ordinal);
        Assert.Contains(
            WorkStages.MaxRounds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            atCeiling.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_continue_cycle_reaches_the_operator_at_the_ceiling_too()
    {
        // The other endless arm: a lane whose work never reaches the PR is continued, and a continuation
        // that keeps failing the same way would continue forever.
        var below = WorkItemLifecycle.Decide(At(
            WorkStage.Continue, outcome: WorkflowOutcome.Failed, workspaceHead: "0000111122223333",
            round: WorkStages.MaxRounds - 1));

        Assert.Equal(WorkStage.Continue, below.NextStage);

        var atCeiling = WorkItemLifecycle.Decide(At(
            WorkStage.Continue, outcome: WorkflowOutcome.Failed, workspaceHead: "0000111122223333",
            round: WorkStages.MaxRounds));

        Assert.Equal(WorkItemTransitionKind.NeedsOperator, atCeiling.Kind);
        Assert.Contains("continue", atCeiling.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_ordinary_path_to_ready_never_reaches_the_ceiling()
    {
        // implement → review → fix → review → ready, threading each transition's own round into the next
        // observation the way the advancer writes it back onto the item. Every step must be a dispatch:
        // a ceiling that trips on the path the queue exists to run is a ceiling set wrong.
        var review = WorkItemLifecycle.Decide(At(WorkStage.Implement, round: 0));
        Assert.Equal(WorkItemTransitionKind.Dispatch, review.Kind);
        Assert.Equal(1, review.Round);

        var fix = WorkItemLifecycle.Decide(At(
            WorkStage.Review, verdict: Verdict(Finding(ReviewFindingSeverity.High, ReviewFindingStatus.Confirmed)),
            round: review.Round));
        Assert.Equal(WorkStage.Fix, fix.NextStage);

        var reReview = WorkItemLifecycle.Decide(At(WorkStage.Fix, round: fix.Round));
        Assert.Equal(WorkItemTransitionKind.Dispatch, reReview.Kind);
        Assert.True(reReview.Round <= WorkStages.MaxRounds);

        var ready = WorkItemLifecycle.Decide(At(WorkStage.Review, verdict: Verdict(), round: reReview.Round));
        Assert.Equal(WorkItemTransitionKind.Stop, ready.Kind);
        Assert.Equal(WorkStage.Ready, ready.NextStage);
    }

    [Fact]
    public void A_stalled_review_lane_with_no_open_pr_has_nothing_left_to_review()
    {
        // The polarity partner of A_stalled_review_lane_is_re_reviewed_rather_than_continued: the ONE
        // input that differs is the PR, and without one there is no head to review at.
        var transition = WorkItemLifecycle.Decide(At(
            WorkStage.Review, outcome: WorkflowOutcome.Cancelled, pr: null, prHead: null));

        Assert.Equal(WorkItemTransitionKind.NeedsOperator, transition.Kind);
        Assert.Contains("nothing left to review", transition.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unsettled_room_produces_nothing()
    {
        var transition = WorkItemLifecycle.Decide(At(WorkStage.Implement, outcome: null));

        Assert.Equal(WorkItemTransitionKind.None, transition.Kind);
    }
}
