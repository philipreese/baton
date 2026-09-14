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
    private const string CurrentHead = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string PreviousHead = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static WorkItemObservation At(
        WorkStage stage,
        string? outcome = WorkflowOutcome.Succeeded,
        ReviewVerdict? verdict = null,
        int? pr = 42,
        string? prHead = CurrentHead,
        string? workspaceHead = CurrentHead,
        int round = 0,
        bool? automaticFixUsed = false,
        bool prObserved = true,
        bool? prOpen = true,
        bool? prDraft = true,
        string? requiredChecks = PullRequestChecks.Passing,
        bool? workspaceChanged = null,
        IndeterminateProducer? indeterminateProducer = null) =>
        new(stage, round, automaticFixUsed, "1934-lane", outcome, verdict, pr, prHead, workspaceHead,
            prObserved, prOpen, prDraft, requiredChecks, workspaceChanged, indeterminateProducer);

    /// <summary>
    /// A verdict whose DECISION and whose FINDINGS are set independently — which is the whole point of
    /// the arms below: the two are crossed, so a lifecycle that had gone back to reading findings
    /// cannot pass.
    /// </summary>
    private static ReviewVerdict Verdict(ReviewDecision? decision, params ReviewFinding[] findings) =>
        new(CurrentHead, findings, "the summary, which nothing routes on", Decision: decision);

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
    public void A_blocking_decision_opens_a_fix_round_and_bumps_the_round()
    {
        var transition = WorkItemLifecycle.Decide(At(
            WorkStage.Review, verdict: Verdict(ReviewDecision.Block), round: 1));

        Assert.Equal(WorkItemTransitionKind.Dispatch, transition.Kind);
        Assert.Equal(WorkStage.Fix, transition.NextStage);
        Assert.Equal(2, transition.Round);
        Assert.True(transition.UsesAutomaticFix);
    }

    [Fact]
    public void A_second_block_after_the_one_automatic_fix_stops_for_the_conductor()
    {
        // The marker, not the aggregate round, is the evidence: a retry or continuation can reach
        // the same round without ever consuming a fix. Once this persisted history says the fix was
        // dispatched, another BLOCK never starts one automatically.
        var transition = WorkItemLifecycle.Decide(At(
            WorkStage.ReReview, verdict: Verdict(ReviewDecision.Block), round: 2, automaticFixUsed: true));

        Assert.Equal(WorkItemTransitionKind.NeedsOperator, transition.Kind);
        Assert.Contains("one automatic fix was already dispatched", transition.Reason, StringComparison.Ordinal);
        Assert.Contains("report.md", transition.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_block_with_legacy_fix_history_fails_closed()
    {
        var transition = WorkItemLifecycle.Decide(At(
            WorkStage.Review, verdict: Verdict(ReviewDecision.Block), automaticFixUsed: null));

        Assert.Equal(WorkItemTransitionKind.NeedsOperator, transition.Kind);
        Assert.Contains("no trustworthy automatic-fix history", transition.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_decision_routes_and_the_findings_do_not_touch_it()
    {
        // The crossed pair, and the reason this file's helper takes the two separately. Each arm is
        // one the deleted `severity: high && status: confirmed` predicate would have got WRONG: two
        // confirmed highs that the reviewer nonetheless approved, and a block over an empty findings
        // array. A lifecycle still reading findings fails both, in opposite directions — which is
        // what makes this a control rather than a restatement. spec/baton.md §13 has the ruling.
        var approvedWithHighs = WorkItemLifecycle.Decide(At(
            WorkStage.Review,
            verdict: Verdict(
                ReviewDecision.Approve,
                Finding(ReviewFindingSeverity.High, ReviewFindingStatus.Confirmed, "one"),
                Finding(ReviewFindingSeverity.High, ReviewFindingStatus.Confirmed, "two"))));

        Assert.Equal(WorkItemTransitionKind.Stop, approvedWithHighs.Kind);
        Assert.Equal(WorkStage.Ready, approvedWithHighs.NextStage);

        // The block a reviewer states without any finding an enumerated field could carry — the case
        // the old heuristic sent to `ready`.
        var blockedWithNothing = WorkItemLifecycle.Decide(At(
            WorkStage.Review, verdict: Verdict(ReviewDecision.Block)));

        Assert.Equal(WorkItemTransitionKind.Dispatch, blockedWithNothing.Kind);
        Assert.Equal(WorkStage.Fix, blockedWithNothing.NextStage);
    }

    [Fact]
    public void A_verdict_with_no_decision_reaches_the_operator_rather_than_being_guessed_from_its_findings()
    {
        // Neither arm of the pair above: the reviewer wrote a readable verdict and no decision in it,
        // which is a thing only a person can resolve. Asserted with findings PRESENT, so an
        // implementation that fell back to counting them would approve or block here instead.
        var transition = WorkItemLifecycle.Decide(At(
            WorkStage.Review,
            verdict: Verdict(null, Finding(ReviewFindingSeverity.High, ReviewFindingStatus.Confirmed))));

        Assert.Equal(WorkItemTransitionKind.NeedsOperator, transition.Kind);
        Assert.Contains("carries no decision", transition.Reason, StringComparison.Ordinal);

        // Distinguishable from the no-verdict-at-all arm below, whose recovery is a different one.
        Assert.DoesNotContain("no readable", transition.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_approving_verdict_stops_and_a_same_head_ready_item_is_not_dispatched_again()
    {
        var approved = WorkItemLifecycle.Decide(At(WorkStage.Review, verdict: Verdict(ReviewDecision.Approve)));
        Assert.Equal(WorkStage.Ready, approved.NextStage);

        // The item as the advancer leaves it: ready, and re-observed on the next tick.
        var again = WorkItemLifecycle.Decide(At(
            WorkStage.Ready, verdict: Verdict(ReviewDecision.Approve), prDraft: false));

        Assert.Equal(WorkItemTransitionKind.None, again.Kind);
        Assert.Null(again.NextStage);
    }

    [Fact]
    public void A_re_review_approval_reaches_ready_even_after_the_automatic_fix()
    {
        var transition = WorkItemLifecycle.Decide(At(
            WorkStage.ReReview, verdict: Verdict(ReviewDecision.Approve), automaticFixUsed: true));

        Assert.Equal(WorkItemTransitionKind.Stop, transition.Kind);
        Assert.Equal(WorkStage.Ready, transition.NextStage);
    }

    [Fact]
    public void An_approval_for_a_previous_head_re_drafts_and_re_reviews_the_new_head()
    {
        var stale = new ReviewVerdict(PreviousHead, [], Decision: ReviewDecision.Approve);

        var transition = WorkItemLifecycle.Decide(At(
            WorkStage.Review, verdict: stale, prDraft: false));

        Assert.Equal(WorkItemTransitionKind.Dispatch, transition.Kind);
        Assert.Equal(WorkStage.ReReview, transition.NextStage);
        Assert.Equal(PullRequestReadinessAction.MarkDraft, transition.PullRequestAction);
        Assert.Contains("approval is stale", transition.Reason, StringComparison.Ordinal);
    }

    public static IEnumerable<object?[]> NoncanonicalApprovingReviewedRefs =>
    [
        ["aaaaaaaaaaaa", "'aaaaaaaaaaaa'"],
        [$"PR #42 at {CurrentHead}", $"'PR #42 at {CurrentHead}'"],
        ["main", "'main'"],
        [null, "missing"],
        ["", "missing"],
        [$" {CurrentHead}", $"' {CurrentHead}'"],
        [$"{CurrentHead} ", $"'{CurrentHead} '"],
    ];

    [Theory]
    [MemberData(nameof(NoncanonicalApprovingReviewedRefs))]
    public void A_noncanonical_approving_reviewed_ref_stops_for_the_operator_without_spending_a_round(
        string? reviewedRef, string describedRef)
    {
        var transition = WorkItemLifecycle.Decide(At(
            WorkStage.Review,
            verdict: new ReviewVerdict(reviewedRef!, [], Decision: ReviewDecision.Approve),
            round: 3));

        Assert.Equal(WorkItemTransitionKind.NeedsOperator, transition.Kind);
        Assert.Null(transition.NextStage);
        Assert.Equal(0, transition.Round);
        Assert.Equal(PullRequestReadinessAction.None, transition.PullRequestAction);
        Assert.Contains($"noncanonical reviewedRef {describedRef}", transition.Reason, StringComparison.Ordinal);
        Assert.Contains("carry the round by hand", transition.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_case_varied_full_sha_approval_covers_the_current_head()
    {
        var transition = WorkItemLifecycle.Decide(At(
            WorkStage.Review,
            verdict: new ReviewVerdict(CurrentHead.ToUpperInvariant(), [], Decision: ReviewDecision.Approve)));

        Assert.Equal(WorkItemTransitionKind.Stop, transition.Kind);
        Assert.Equal(WorkStage.Ready, transition.NextStage);
    }

    [Theory]
    [InlineData(PullRequestChecks.Pending)]
    [InlineData(PullRequestChecks.Failing)]
    [InlineData(PullRequestChecks.None)]
    [InlineData(null)]
    public void Current_head_approval_waits_in_draft_until_required_checks_pass(string? checks)
    {
        var transition = WorkItemLifecycle.Decide(At(
            WorkStage.Review, verdict: Verdict(ReviewDecision.Approve), requiredChecks: checks));

        Assert.Equal(WorkItemTransitionKind.None, transition.Kind);
        Assert.Null(transition.NextStage);
        Assert.Equal(PullRequestReadinessAction.None, transition.PullRequestAction);
        Assert.Contains("required checks", transition.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Current_head_approval_with_green_required_checks_marks_a_draft_ready()
    {
        var transition = WorkItemLifecycle.Decide(At(
            WorkStage.Review, verdict: Verdict(ReviewDecision.Approve), prDraft: true));

        Assert.Equal(WorkStage.Ready, transition.NextStage);
        Assert.Equal(PullRequestReadinessAction.MarkReady, transition.PullRequestAction);
    }

    [Fact]
    public void Uncertain_GitHub_state_retains_the_round_without_claiming_no_PR()
    {
        var transition = WorkItemLifecycle.Decide(At(
            WorkStage.Implement, prObserved: false, pr: null, prHead: null,
            prOpen: null, prDraft: null, requiredChecks: null));

        Assert.Equal(WorkItemTransitionKind.None, transition.Kind);
        Assert.Contains("observation failed", transition.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_closed_or_merged_PR_is_never_reopened_or_advanced()
    {
        var transition = WorkItemLifecycle.Decide(At(
            WorkStage.Review, verdict: Verdict(ReviewDecision.Approve), prOpen: false, prDraft: false));

        Assert.Equal(WorkItemTransitionKind.NeedsOperator, transition.Kind);
        Assert.Equal(PullRequestReadinessAction.None, transition.PullRequestAction);
        Assert.Contains("will not reopen", transition.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_ready_item_with_a_new_head_is_re_drafted_and_re_reviewed()
    {
        var stale = new ReviewVerdict(PreviousHead, [], Decision: ReviewDecision.Approve);

        var transition = WorkItemLifecycle.Decide(At(
            WorkStage.Ready, verdict: stale, prDraft: false));

        Assert.Equal(WorkItemTransitionKind.Dispatch, transition.Kind);
        Assert.Equal(WorkStage.ReReview, transition.NextStage);
        Assert.Equal(PullRequestReadinessAction.MarkDraft, transition.PullRequestAction);
    }

    [Fact]
    public void A_ready_item_with_a_noncanonical_approval_is_re_drafted_and_stops_for_the_operator()
    {
        var transition = WorkItemLifecycle.Decide(At(
            WorkStage.Ready,
            verdict: new ReviewVerdict($"PR #42 at {CurrentHead}", [], Decision: ReviewDecision.Approve),
            prDraft: false,
            round: 3));

        Assert.Equal(WorkItemTransitionKind.NeedsOperator, transition.Kind);
        Assert.Null(transition.NextStage);
        Assert.Equal(0, transition.Round);
        Assert.Equal(PullRequestReadinessAction.MarkDraft, transition.PullRequestAction);
        Assert.Contains("noncanonical reviewedRef", transition.Reason, StringComparison.Ordinal);
        Assert.Contains("carry the round by hand", transition.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_ready_item_with_same_head_pending_checks_is_re_drafted_without_duplicate_review()
    {
        var transition = WorkItemLifecycle.Decide(At(
            WorkStage.Ready, verdict: Verdict(ReviewDecision.Approve), prDraft: false,
            requiredChecks: PullRequestChecks.Pending));

        Assert.Equal(WorkItemTransitionKind.None, transition.Kind);
        Assert.Null(transition.NextStage);
        Assert.Equal(PullRequestReadinessAction.MarkDraft, transition.PullRequestAction);
    }

    [Fact]
    public void A_closed_ready_PR_is_terminal_and_never_reopened()
    {
        var transition = WorkItemLifecycle.Decide(At(
            WorkStage.Ready, verdict: Verdict(ReviewDecision.Approve), prOpen: false, prDraft: false));

        Assert.Equal(WorkItemTransitionKind.None, transition.Kind);
        Assert.Equal(PullRequestReadinessAction.None, transition.PullRequestAction);
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

    [Theory]
    [InlineData(WorkStage.Review, WorkflowOutcome.Failed)]
    [InlineData(WorkStage.Review, WorkflowOutcome.Indeterminate)]
    [InlineData(WorkStage.Review, WorkflowOutcome.Cancelled)]
    [InlineData(WorkStage.Review, "TimedOut")]
    [InlineData(WorkStage.ReReview, WorkflowOutcome.Failed)]
    [InlineData(WorkStage.ReReview, WorkflowOutcome.Indeterminate)]
    [InlineData(WorkStage.ReReview, WorkflowOutcome.Cancelled)]
    [InlineData(WorkStage.ReReview, "TimedOut")]
    public void An_artifactless_terminal_review_or_re_review_stops_for_the_operator_without_spending_another_round(
        WorkStage stage, string outcome)
    {
        var transition = WorkItemLifecycle.Decide(At(
            stage, outcome: outcome, round: 2, prDraft: false));

        Assert.Equal(WorkItemTransitionKind.NeedsOperator, transition.Kind);
        Assert.Null(transition.NextStage);
        Assert.Equal(0, transition.Round);
        Assert.Equal(PullRequestReadinessAction.MarkDraft, transition.PullRequestAction);
        Assert.Contains(stage == WorkStage.Review ? "review lane settled" : "re-review lane settled", transition.Reason, StringComparison.Ordinal);
        Assert.Contains("no reviewer decision exists", transition.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ReviewDecision.Approve, WorkItemTransitionKind.Stop, WorkStage.Ready)]
    [InlineData(ReviewDecision.Block, WorkItemTransitionKind.Dispatch, WorkStage.Fix)]
    public void A_non_successful_review_with_a_readable_verdict_retains_its_decision(
        ReviewDecision decision, WorkItemTransitionKind expectedKind, WorkStage expectedStage)
    {
        var transition = WorkItemLifecycle.Decide(At(
            WorkStage.ReReview, outcome: WorkflowOutcome.Indeterminate, verdict: Verdict(decision), round: 1));

        Assert.Equal(expectedKind, transition.Kind);
        Assert.Equal(expectedStage, transition.NextStage);
    }

    [Fact]
    public void A_review_lane_that_finished_during_teardown_routes_on_its_verdict_exactly_as_a_succeeded_one()
    {
        // #1945's word is SUCCEEDED-shaped (spec/baton.md §3): the lane satisfied its contract and the
        // timeout kill landed during teardown, so its verdict.json is on disk and readable. Asserted as
        // an equality against the Succeeded transition rather than a re-statement of the fix arm — that
        // is the claim ("routes exactly as Succeeded does"), and it cannot pass by accident.
        var verdict = Verdict(ReviewDecision.Block, Finding(ReviewFindingSeverity.High, ReviewFindingStatus.Confirmed));

        var teardown = WorkItemLifecycle.Decide(At(
            WorkStage.Review, outcome: WorkflowOutcome.FinishedDuringTeardown, verdict: verdict, round: 1));

        Assert.Equal(
            WorkItemLifecycle.Decide(At(
                WorkStage.Review, outcome: WorkflowOutcome.Succeeded, verdict: verdict, round: 1)),
            teardown);
        Assert.Equal(WorkStage.Fix, teardown.NextStage);

        // The control that makes this arm about the WORD and not about the verdict: a word that is not
        // succeeded-shaped still preserves the readable decision and routes from it.
        var failed = WorkItemLifecycle.Decide(At(
            WorkStage.Review, outcome: WorkflowOutcome.Failed, verdict: verdict, round: 1));

        Assert.Equal(WorkStage.Fix, failed.NextStage);
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
    public void An_artifactless_re_review_stops_before_the_ceiling_without_spending_another_round()
    {
        var atCeiling = WorkItemLifecycle.Decide(At(
            WorkStage.ReReview, outcome: WorkflowOutcome.Failed, round: WorkStages.MaxRounds, prDraft: false));

        Assert.Equal(WorkItemTransitionKind.NeedsOperator, atCeiling.Kind);
        Assert.Contains("re-review", atCeiling.Reason, StringComparison.Ordinal);
        Assert.Contains("no reviewer decision exists", atCeiling.Reason, StringComparison.Ordinal);
        Assert.Equal(PullRequestReadinessAction.MarkDraft, atCeiling.PullRequestAction);
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

    [Theory]
    [InlineData(ReviewDecision.Approve, WorkItemTransitionKind.Stop, WorkStage.Ready)]
    [InlineData(ReviewDecision.Block, WorkItemTransitionKind.NeedsOperator, null)]
    public void The_automatic_fix_gets_one_paired_re_review_after_pre_review_continuations_at_the_ceiling(
        ReviewDecision reReviewDecision, WorkItemTransitionKind expectedKind, WorkStage? expectedStage)
    {
        // #2286: an arrested implement and a delivery retry consume two rounds before the first
        // review. The BLOCK then spends the one automatic fix at the ordinary ceiling.
        var firstContinue = WorkItemLifecycle.Decide(At(
            WorkStage.Implement, outcome: WorkflowOutcome.Indeterminate,
            workspaceHead: "0000111122223333"));
        var secondContinue = WorkItemLifecycle.Decide(At(
            WorkStage.Continue, outcome: WorkflowOutcome.Failed,
            workspaceHead: "0000111122223333", round: firstContinue.Round));
        var review = WorkItemLifecycle.Decide(At(WorkStage.Continue, round: secondContinue.Round));
        var fix = WorkItemLifecycle.Decide(At(
            WorkStage.Review, verdict: Verdict(ReviewDecision.Block), round: review.Round));

        Assert.Equal(WorkStages.MaxRounds, fix.Round);
        Assert.True(fix.UsesAutomaticFix);

        var pairedReReview = WorkItemLifecycle.Decide(At(
            WorkStage.Fix, round: fix.Round, automaticFixUsed: true));

        Assert.Equal(WorkItemTransitionKind.Dispatch, pairedReReview.Kind);
        Assert.Equal(WorkStage.ReReview, pairedReReview.NextStage);
        Assert.Equal(WorkStages.MaxRounds + 1, pairedReReview.Round);
        Assert.Contains("paired automatic-fix re-review", pairedReReview.Reason, StringComparison.Ordinal);

        var settled = WorkItemLifecycle.Decide(At(
            WorkStage.ReReview, verdict: Verdict(reReviewDecision), round: pairedReReview.Round,
            automaticFixUsed: true));

        Assert.Equal(expectedKind, settled.Kind);
        Assert.Equal(expectedStage, settled.NextStage);
    }

    [Fact]
    public void A_fix_without_the_durable_automatic_history_cannot_use_the_ceiling_exemption()
    {
        var transition = WorkItemLifecycle.Decide(At(
            WorkStage.Fix, round: WorkStages.MaxRounds, automaticFixUsed: false));

        Assert.Equal(WorkItemTransitionKind.NeedsOperator, transition.Kind);
        Assert.Null(transition.NextStage);
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
            WorkStage.Review, verdict: Verdict(ReviewDecision.Block), round: review.Round));
        Assert.Equal(WorkStage.Fix, fix.NextStage);

        var reReview = WorkItemLifecycle.Decide(At(WorkStage.Fix, round: fix.Round));
        Assert.Equal(WorkItemTransitionKind.Dispatch, reReview.Kind);
        Assert.True(reReview.Round <= WorkStages.MaxRounds);

        var ready = WorkItemLifecycle.Decide(At(
            WorkStage.Review, verdict: Verdict(ReviewDecision.Approve), round: reReview.Round));
        Assert.Equal(WorkItemTransitionKind.Stop, ready.Kind);
        Assert.Equal(WorkStage.Ready, ready.NextStage);
    }

    [Fact]
    public void An_artifactless_stalled_review_without_a_pr_still_names_the_missing_verdict()
    {
        var transition = WorkItemLifecycle.Decide(At(
            WorkStage.Review, outcome: WorkflowOutcome.Cancelled, pr: null, prHead: null));

        Assert.Equal(WorkItemTransitionKind.NeedsOperator, transition.Kind);
        Assert.Contains("no reviewer decision exists", transition.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_arrested_mutating_lane_with_pushed_work_routes_to_re_review_before_reading_change_evidence()
    {
        var transition = WorkItemLifecycle.Decide(At(
            WorkStage.Implement, outcome: WorkflowOutcome.Indeterminate,
            workspaceChanged: false, indeterminateProducer: IndeterminateProducer.Arrested));

        Assert.Equal(WorkItemTransitionKind.Dispatch, transition.Kind);
        Assert.Equal(WorkStage.ReReview, transition.NextStage);
    }

    [Fact]
    public void An_arrested_mutating_lane_with_unpushed_changed_work_continues()
    {
        var transition = WorkItemLifecycle.Decide(At(
            WorkStage.Implement, outcome: WorkflowOutcome.Indeterminate,
            workspaceHead: "0000111122223333", workspaceChanged: true,
            indeterminateProducer: IndeterminateProducer.Arrested));

        Assert.Equal(WorkItemTransitionKind.Dispatch, transition.Kind);
        Assert.Equal(WorkStage.Continue, transition.NextStage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void An_arrested_mutating_lane_without_positive_workspace_evidence_stops_for_the_operator(bool? workspaceChanged)
    {
        var transition = WorkItemLifecycle.Decide(At(
            WorkStage.Implement, outcome: WorkflowOutcome.Indeterminate,
            workspaceHead: "0000111122223333", workspaceChanged: workspaceChanged,
            indeterminateProducer: IndeterminateProducer.Arrested));

        Assert.Equal(WorkItemTransitionKind.NeedsOperator, transition.Kind);
        Assert.Contains(workspaceChanged == false ? "measured no workspace change" : "unmeasurable",
            transition.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Timeout_continues_mutating_work_but_an_artifactless_review_stops()
    {
        var timeout = WorkItemLifecycle.Decide(At(
            WorkStage.Implement, outcome: WorkflowOutcome.Failed, workspaceHead: "0000111122223333"));
        var review = WorkItemLifecycle.Decide(At(WorkStage.Review, outcome: WorkflowOutcome.Failed));

        Assert.Equal(WorkStage.Continue, timeout.NextStage);
        Assert.Equal(WorkItemTransitionKind.NeedsOperator, review.Kind);
        Assert.Null(review.NextStage);
    }

    [Fact]
    public void An_unsettled_room_produces_nothing()
    {
        var transition = WorkItemLifecycle.Decide(At(WorkStage.Implement, outcome: null));

        Assert.Equal(WorkItemTransitionKind.None, transition.Kind);
    }
}
