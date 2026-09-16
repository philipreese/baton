using Baton.Cli.Daemon;
using Baton.Domain;
using Baton.Queue;
using Baton.Status;

namespace Baton.Cli.Tests.Daemon;

public sealed class LifecycleAttemptGraphTests
{
    private const string A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string C = "cccccccccccccccccccccccccccccccccccccccc";

    [Fact]
    public void Implementing_B_then_open_pr_at_B_has_one_review_frontier()
    {
        var graph = LifecycleAttemptGraph.Build(Item(), [
            Event(1, FleetEventKind.AttemptStarted, "implement", WorkStage.Implement, A),
            Started(101, "implement"),
            Event(2, FleetEventKind.AttemptSettled, "implement"),
            Event(3, FleetEventKind.RevisionProduced, "implement", revision: B),
            Event(4, FleetEventKind.AttemptStarted, "review", WorkStage.Review, B,
                parents: ["implement"], edges: [FleetAttemptEdgeKind.Reviews]),
        ], Pr(B));

        Assert.Null(graph.Halt);
        Assert.Equal(new FleetAttemptId("review"), graph.Frontier!.AttemptId);
        Assert.Equal(WorkStage.Review, graph.Frontier.Stage);
    }

    [Fact]
    public void Blocking_review_derives_fix_plan_against_its_exact_revision()
    {
        var graph = LifecycleAttemptGraph.Build(Item(), [
            Event(1, FleetEventKind.AttemptStarted, "implement", WorkStage.Implement, A),
            Started(101, "implement"),
            Event(2, FleetEventKind.AttemptSettled, "implement"),
            Event(3, FleetEventKind.RevisionProduced, "implement", revision: B),
            Event(4, FleetEventKind.AttemptStarted, "review", WorkStage.Review, B,
                parents: ["implement"], edges: [FleetAttemptEdgeKind.Reviews]),
            Started(104, "review"),
            Event(5, FleetEventKind.AttemptSettled, "review"),
            Event(6, FleetEventKind.ReviewVerdictObserved, "review", revision: B, verdict: "block"),
        ], Pr(B));

        Assert.Null(graph.Halt);
        Assert.Null(graph.Frontier);
        Assert.Equal(WorkStage.Fix, graph.NextAttempt!.Stage);
        Assert.Equal(new FleetRevisionId(B), graph.NextAttempt.InputRevision);
    }

    [Fact]
    public void Unchanged_fix_cannot_authorize_rereview()
    {
        var graph = LifecycleAttemptGraph.Build(Item(), [
            Event(1, FleetEventKind.AttemptStarted, "implement", WorkStage.Implement, A),
            Started(101, "implement"),
            Event(2, FleetEventKind.AttemptSettled, "implement"),
            Event(3, FleetEventKind.RevisionProduced, "implement", revision: B),
            Event(4, FleetEventKind.AttemptStarted, "review", WorkStage.Review, B,
                parents: ["implement"], edges: [FleetAttemptEdgeKind.Reviews]),
            Started(104, "review"),
            Event(5, FleetEventKind.AttemptSettled, "review"),
            Event(6, FleetEventKind.ReviewVerdictObserved, "review", revision: B, verdict: "block"),
            Event(7, FleetEventKind.AttemptStarted, "fix", WorkStage.Fix, B,
                parents: ["review"], edges: [FleetAttemptEdgeKind.Repairs]),
            Started(107, "fix"),
            Event(8, FleetEventKind.AttemptSettled, "fix"),
            Event(9, FleetEventKind.AttemptStarted, "rereview", WorkStage.ReReview, B,
                parents: ["fix"], edges: [FleetAttemptEdgeKind.Reviews]),
        ], Pr(B));

        Assert.Equal(LifecycleGraphHaltKind.UnsatisfiedDependency, graph.Halt!.Kind);
        Assert.Contains("Reviews", graph.Halt.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_terminal_fact_fails_closed()
    {
        var graph = LifecycleAttemptGraph.Build(Item(), [
            Event(1, FleetEventKind.AttemptStarted, "implement", WorkStage.Implement, A),
            Started(101, "implement"),
            Event(2, FleetEventKind.AttemptSettled, "implement"),
            Event(3, FleetEventKind.AttemptSettled, "implement"),
        ], Pr(A));

        Assert.Equal(LifecycleGraphHaltKind.DuplicateTerminalFact, graph.Halt!.Kind);
    }

    [Fact]
    public void Produced_revision_equal_to_its_input_cannot_authorize_review()
    {
        var graph = LifecycleAttemptGraph.Build(Item(), [
            Event(1, FleetEventKind.AttemptStarted, "fix", WorkStage.Fix, B),
            Started(101, "fix"),
            Event(2, FleetEventKind.AttemptSettled, "fix"),
            Event(3, FleetEventKind.RevisionProduced, "fix", revision: B),
            Event(4, FleetEventKind.AttemptStarted, "rereview", WorkStage.ReReview, B,
                parents: ["fix"], edges: [FleetAttemptEdgeKind.Reviews]),
        ], Pr(B));

        Assert.Equal(LifecycleGraphHaltKind.UnchangedRevision, graph.Halt!.Kind);
    }

    [Fact]
    public void Verdict_for_a_different_revision_cannot_authorize_a_fix()
    {
        var graph = LifecycleAttemptGraph.Build(Item(), [
            Event(1, FleetEventKind.AttemptStarted, "implement", WorkStage.Implement, A),
            Started(101, "implement"),
            Event(2, FleetEventKind.AttemptSettled, "implement"),
            Event(3, FleetEventKind.RevisionProduced, "implement", revision: B),
            Event(4, FleetEventKind.AttemptStarted, "review", WorkStage.Review, B,
                parents: ["implement"], edges: [FleetAttemptEdgeKind.Reviews]),
            Started(104, "review"),
            Event(5, FleetEventKind.AttemptSettled, "review"),
            Event(6, FleetEventKind.ReviewVerdictObserved, "review", revision: A, verdict: "block"),
            Event(7, FleetEventKind.AttemptStarted, "fix", WorkStage.Fix, B,
                parents: ["review"], edges: [FleetAttemptEdgeKind.Repairs]),
        ], Pr(B));

        Assert.Equal(LifecycleGraphHaltKind.MismatchedVerdictRevision, graph.Halt!.Kind);
    }

    [Fact]
    public void Verdict_for_a_different_revision_halts_even_without_a_repair_child()
    {
        var graph = LifecycleAttemptGraph.Build(Item(), [
            Event(1, FleetEventKind.AttemptStarted, "implement", WorkStage.Implement, A),
            Started(101, "implement"),
            Event(2, FleetEventKind.AttemptSettled, "implement"),
            Event(3, FleetEventKind.RevisionProduced, "implement", revision: B),
            Event(4, FleetEventKind.AttemptStarted, "review", WorkStage.Review, B,
                parents: ["implement"], edges: [FleetAttemptEdgeKind.Reviews]),
            Started(104, "review"),
            Event(5, FleetEventKind.AttemptSettled, "review"),
            Event(6, FleetEventKind.ReviewVerdictObserved, "review", revision: A, verdict: "approve"),
        ], Pr(B));

        Assert.Equal(LifecycleGraphHaltKind.MismatchedVerdictRevision, graph.Halt!.Kind);
    }

    [Fact]
    public void Approval_for_a_stale_head_or_failing_checks_is_not_done()
    {
        var facts = new[]
        {
            Event(1, FleetEventKind.AttemptStarted, "implement", WorkStage.Implement, A),
            Started(101, "implement"),
            Event(2, FleetEventKind.AttemptSettled, "implement"),
            Event(3, FleetEventKind.RevisionProduced, "implement", revision: B),
            Event(4, FleetEventKind.AttemptStarted, "review", WorkStage.Review, B,
                parents: ["implement"], edges: [FleetAttemptEdgeKind.Reviews]),
            Started(104, "review"),
            Event(5, FleetEventKind.AttemptSettled, "review"),
            Event(6, FleetEventKind.ReviewVerdictObserved, "review", revision: B, verdict: "approve"),
        };

        var stale = LifecycleAttemptGraph.Build(Item(), facts, Pr(A));
        var failing = LifecycleAttemptGraph.Build(Item(), facts, new(42, B, true, true, "failing"));

        Assert.Equal(LifecycleGraphHaltKind.UnprovenPullRequestHead, stale.Halt!.Kind);
        Assert.Equal(QueueItemState.Done, failing.Projection.DisplayState);
    }

    [Fact]
    public void Legacy_rows_remain_an_explicit_compatibility_result()
    {
        var legacy = Item() with { LifecycleGraphVersion = null };

        var graph = LifecycleAttemptGraph.Build(legacy, [], Pr(A));

        Assert.True(graph.IsCompatibility);
        Assert.Equal(LifecycleGraphHaltKind.LegacyUnproven, graph.Halt!.Kind);
    }

    [Fact]
    public void Delivered_fix_derives_one_rereview_plan_consuming_C()
    {
        var facts = new[]
        {
            Event(1, FleetEventKind.AttemptStarted, "implement", WorkStage.Implement, A),
            Started(101, "implement"),
            Event(2, FleetEventKind.AttemptSettled, "implement"),
            Event(3, FleetEventKind.RevisionProduced, "implement", revision: B),
            Event(4, FleetEventKind.AttemptStarted, "review", WorkStage.Review, B,
                parents: ["implement"], edges: [FleetAttemptEdgeKind.Reviews]),
            Started(104, "review"),
            Event(5, FleetEventKind.AttemptSettled, "review"),
            Event(6, FleetEventKind.ReviewVerdictObserved, "review", revision: B, verdict: "block"),
            Event(7, FleetEventKind.AttemptStarted, "fix", WorkStage.Fix, B,
                parents: ["review"], edges: [FleetAttemptEdgeKind.Repairs]),
            Started(107, "fix"),
            Event(8, FleetEventKind.AttemptSettled, "fix"),
            Event(9, FleetEventKind.RevisionProduced, "fix", revision: C),
        };

        var graph = LifecycleAttemptGraph.Build(Item(), facts, Pr(C));

        Assert.Null(graph.Halt);
        Assert.Null(graph.Frontier);
        Assert.Equal(WorkStage.ReReview, graph.NextAttempt!.Stage);
        Assert.Equal(new FleetRevisionId(C), graph.NextAttempt.InputRevision);
    }

    [Fact]
    public void Exact_C_approval_and_green_checks_is_ready()
    {
        var facts = new[]
        {
            Event(1, FleetEventKind.AttemptStarted, "implement", WorkStage.Implement, A),
            Started(101, "implement"),
            Event(2, FleetEventKind.AttemptSettled, "implement"),
            Event(3, FleetEventKind.RevisionProduced, "implement", revision: C),
            Event(4, FleetEventKind.AttemptStarted, "review", WorkStage.Review, C,
                parents: ["implement"], edges: [FleetAttemptEdgeKind.Reviews]),
            Started(104, "review"),
            Event(5, FleetEventKind.AttemptSettled, "review"),
            Event(6, FleetEventKind.ReviewVerdictObserved, "review", revision: C, verdict: "approve"),
        };

        var graph = LifecycleAttemptGraph.Build(Item(), facts, Pr(C));

        Assert.True(graph.Ready);
        Assert.Equal(WorkStage.Ready, graph.Projection.DisplayStage);
        Assert.Equal(QueueItemState.Queued, graph.Projection.DisplayState);
    }

    [Fact]
    public void Display_replay_does_not_invent_an_open_pr_from_stale_green_row_fields()
    {
        var item = Item() with
        {
            PullRequest = 42,
            Checks = PullRequestChecks.Passing,
            ChecksHeadSha = C,
            LifecyclePullRequestEvidence = null,
        };
        var facts = new[]
        {
            Event(1, FleetEventKind.AttemptStarted, "implement", WorkStage.Implement, A),
            Started(101, "implement"),
            Event(2, FleetEventKind.AttemptSettled, "implement"),
            Event(3, FleetEventKind.RevisionProduced, "implement", revision: C),
            Event(4, FleetEventKind.AttemptStarted, "review", WorkStage.Review, C,
                parents: ["implement"], edges: [FleetAttemptEdgeKind.Reviews]),
            Started(104, "review"),
            Event(5, FleetEventKind.AttemptSettled, "review"),
            Event(6, FleetEventKind.ReviewVerdictObserved, "review", revision: C, verdict: "approve"),
        };

        var graph = LifecycleAttemptGraph.Build(item, facts,
            LifecycleQueueProjection.Observation(item, facts, DateTimeOffset.UtcNow));
        var projected = LifecycleQueueProjection.Apply(item, graph);

        Assert.False(graph.Ready);
        Assert.Equal(WorkStage.Review, projected.Stage);
        Assert.False(projected.Halted);
    }

    [Fact]
    public void Projection_replay_is_structurally_equivalent()
    {
        var facts = new[]
        {
            Event(1, FleetEventKind.AttemptStarted, "implement", WorkStage.Implement, A),
            Started(101, "implement"),
            Event(2, FleetEventKind.AttemptSettled, "implement"),
            Event(3, FleetEventKind.RevisionProduced, "implement", revision: B),
            Event(4, FleetEventKind.AttemptStarted, "review", WorkStage.Review, B,
                parents: ["implement"], edges: [FleetAttemptEdgeKind.Reviews]),
        };

        var first = LifecycleAttemptGraph.Build(Item(), facts, Pr(B));
        var replayed = LifecycleAttemptGraph.Build(Item(), facts.ToArray(), Pr(B));

        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(first),
            System.Text.Json.JsonSerializer.Serialize(replayed));
    }

    [Fact]
    public void Terminal_history_is_not_frontier_until_a_typed_recovery_edge_names_it()
    {
        var terminal = new[]
        {
            Event(1, FleetEventKind.AttemptStarted, "implement", WorkStage.Implement, A),
            Started(101, "implement"),
            Event(2, FleetEventKind.AttemptSettled, "implement", outcome: WorkflowOutcome.Failed,
                workspaceChanged: true),
        };

        var withoutRecovery = LifecycleAttemptGraph.Build(Item(), terminal, Pr(A));
        var withRecovery = LifecycleAttemptGraph.Build(Item(), [
            .. terminal,
            Event(3, FleetEventKind.AttemptStarted, "continue", WorkStage.Continue, A,
                parents: ["implement"], edges: [FleetAttemptEdgeKind.Continues]),
        ], Pr(A));

        Assert.Null(withoutRecovery.Frontier);
        Assert.Equal(WorkStage.Continue, withoutRecovery.NextAttempt!.Stage);
        Assert.Equal(WorkStage.Continue, withRecovery.Frontier!.Stage);
        Assert.False(withRecovery.Frontier.Started);
    }

    [Fact]
    public void Refused_attempt_is_history_and_only_typed_supersession_opens_a_frontier()
    {
        var refused = new[]
        {
            Event(1, FleetEventKind.AttemptStarted, "refused", WorkStage.Implement, A),
            Event(2, FleetEventKind.AttemptRefused, "refused", outcome: "admission-refused"),
        };

        var stopped = LifecycleAttemptGraph.Build(Item(), refused, Pr(A));
        var superseded = LifecycleAttemptGraph.Build(Item(), [
            .. refused,
            Event(3, FleetEventKind.AttemptStarted, "replacement", WorkStage.Implement, A,
                parents: ["refused"], edges: [FleetAttemptEdgeKind.Supersedes]),
        ], Pr(A));

        Assert.Equal(LifecycleGraphHaltKind.AttemptRefused, stopped.Halt!.Kind);
        Assert.Equal(new FleetAttemptId("replacement"), superseded.Frontier!.AttemptId);
    }

    [Fact]
    public void Graph_projection_is_the_WIP_input_and_excludes_fully_terminal_history()
    {
        var item = Item() with { PullRequest = 42 };
        var waitingReviewFacts = new[]
        {
            Event(1, FleetEventKind.AttemptStarted, "implement", WorkStage.Implement, A),
            Started(2, "implement"),
            Event(3, FleetEventKind.AttemptSettled, "implement"),
            Event(4, FleetEventKind.RevisionProduced, "implement", revision: B),
            Event(5, FleetEventKind.AttemptStarted, "review", WorkStage.Review, B,
                parents: ["implement"], edges: [FleetAttemptEdgeKind.Reviews]),
        };

        var waiting = LifecycleAttemptGraph.Build(item, waitingReviewFacts, Pr(B));
        var projected = LifecycleQueueProjection.Apply(item, waiting);
        var portfolio = QueuePortfolio.From([projected]);

        Assert.True(waiting.Projection.Active);
        Assert.Equal(1, portfolio.ActiveLifecycles);
        Assert.Equal(0, portfolio.PrePullRequestLifecycles);
        Assert.Equal(0, portfolio.LiveReviews);

        var running = LifecycleAttemptGraph.Build(item, [.. waitingReviewFacts, Started(6, "review")], Pr(B));
        var runningPortfolio = QueuePortfolio.From([LifecycleQueueProjection.Apply(item, running)]);
        Assert.True(running.Projection.LiveReview);
        Assert.Equal(1, runningPortfolio.LiveReviews);

        var ready = LifecycleAttemptGraph.Build(item, [
            .. waitingReviewFacts,
            Started(6, "review"),
            Event(7, FleetEventKind.AttemptSettled, "review"),
            Event(8, FleetEventKind.ReviewVerdictObserved, "review", revision: B, verdict: "approve"),
        ], Pr(B));
        var terminalPortfolio = QueuePortfolio.From([LifecycleQueueProjection.Apply(item, ready)]);

        Assert.True(ready.Ready);
        Assert.False(ready.Projection.Active);
        Assert.Equal(0, terminalPortfolio.ActiveLifecycles);
        Assert.Equal(0, terminalPortfolio.LiveReviews);
    }

    [Fact]
    public void A_later_parentless_implement_is_not_a_second_root()
    {
        var graph = LifecycleAttemptGraph.Build(Item(), [
            Event(1, FleetEventKind.AttemptStarted, "root", WorkStage.Implement, A),
            Started(101, "root"),
            Event(2, FleetEventKind.AttemptSettled, "root", outcome: WorkflowOutcome.Failed,
                workspaceChanged: true),
            Event(3, FleetEventKind.AttemptStarted, "second-root", WorkStage.Implement, A),
        ], Pr(A));

        Assert.Equal(LifecycleGraphHaltKind.MissingParent, graph.Halt!.Kind);
    }

    [Fact]
    public void Settlement_without_an_outcome_fails_closed()
    {
        var graph = LifecycleAttemptGraph.Build(Item(), [
            Event(1, FleetEventKind.AttemptStarted, "implement", WorkStage.Implement, A),
            Started(101, "implement"),
            Event(2, FleetEventKind.AttemptSettled, "implement", missingOutcome: true),
        ], Pr(A));

        Assert.Equal(LifecycleGraphHaltKind.MissingOutcome, graph.Halt!.Kind);
    }

    [Fact]
    public void Result_fact_before_start_fails_closed()
    {
        var graph = LifecycleAttemptGraph.Build(Item(), [
            Event(1, FleetEventKind.AttemptStarted, "implement", WorkStage.Implement, A),
            Started(100, "implement"),
            Event(2, FleetEventKind.AttemptSettled, "implement"),
        ], Pr(A));

        Assert.Equal(LifecycleGraphHaltKind.InvalidChronology, graph.Halt!.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public void Incomplete_attempt_without_positive_workspace_evidence_cannot_continue(bool? changed)
    {
        var graph = LifecycleAttemptGraph.Build(Item(), [
            Event(1, FleetEventKind.AttemptStarted, "implement", WorkStage.Implement, A),
            Started(101, "implement"),
            Event(2, FleetEventKind.AttemptSettled, "implement", outcome: WorkflowOutcome.Failed,
                workspaceChanged: changed),
        ], Pr(A));

        Assert.Equal(LifecycleGraphHaltKind.NoRetainedContinuationState, graph.Halt!.Kind);
    }

    [Fact]
    public void Cancelled_attempt_without_positive_workspace_evidence_cannot_continue()
    {
        var graph = LifecycleAttemptGraph.Build(Item(), [
            Event(1, FleetEventKind.AttemptStarted, "implement", WorkStage.Implement, A),
            Started(101, "implement"),
            Event(2, FleetEventKind.AttemptSettled, "implement", outcome: WorkflowOutcome.Cancelled),
        ], Pr(A));

        Assert.Equal(LifecycleGraphHaltKind.NoRetainedContinuationState, graph.Halt!.Kind);
    }

    [Fact]
    public void Typed_unchanged_head_nonproduction_cannot_authorize_continuation()
    {
        var graph = LifecycleAttemptGraph.Build(Item(), [
            Event(1, FleetEventKind.AttemptStarted, "implement", WorkStage.Implement, A),
            Started(101, "implement"),
            Event(2, FleetEventKind.AttemptSettled, "implement", outcome: WorkflowOutcome.Failed,
                workspaceChanged: true),
            Event(3, FleetEventKind.RevisionNotProducedUnchangedHeadAfterWorkspaceChange,
                "implement", revision: A),
        ], Pr(A));

        Assert.Equal(LifecycleGraphHaltKind.UnchangedRevision, graph.Halt!.Kind);
    }

    [Fact]
    public void The_fifth_ordinary_followup_is_stopped_by_the_round_ceiling()
    {
        var graph = LifecycleAttemptGraph.Build(Item(), [
            Event(1, FleetEventKind.AttemptStarted, "implement", WorkStage.Implement, A),
            Started(101, "implement"),
            Event(2, FleetEventKind.AttemptSettled, "implement", outcome: WorkflowOutcome.Failed, workspaceChanged: true),
            Event(3, FleetEventKind.AttemptStarted, "continue-1", WorkStage.Continue, A,
                parents: ["implement"], edges: [FleetAttemptEdgeKind.Continues]),
            Started(103, "continue-1"),
            Event(4, FleetEventKind.AttemptSettled, "continue-1", outcome: WorkflowOutcome.Failed, workspaceChanged: true),
            Event(5, FleetEventKind.AttemptStarted, "continue-2", WorkStage.Continue, A,
                parents: ["continue-1"], edges: [FleetAttemptEdgeKind.Continues]),
            Started(105, "continue-2"),
            Event(6, FleetEventKind.AttemptSettled, "continue-2", outcome: WorkflowOutcome.Failed, workspaceChanged: true),
            Event(7, FleetEventKind.AttemptStarted, "continue-3", WorkStage.Continue, A,
                parents: ["continue-2"], edges: [FleetAttemptEdgeKind.Continues]),
            Started(107, "continue-3"),
            Event(8, FleetEventKind.AttemptSettled, "continue-3", outcome: WorkflowOutcome.Failed, workspaceChanged: true),
            Event(9, FleetEventKind.AttemptStarted, "continue-4", WorkStage.Continue, A,
                parents: ["continue-3"], edges: [FleetAttemptEdgeKind.Continues]),
            Started(109, "continue-4"),
            Event(10, FleetEventKind.AttemptSettled, "continue-4", outcome: WorkflowOutcome.Failed, workspaceChanged: true),
        ], Pr(A));

        Assert.Equal(LifecycleGraphHaltKind.RoundLimitReached, graph.Halt!.Kind);
    }

    [Fact]
    public void Wrong_pr_or_stale_check_evidence_cannot_authorize_ready()
    {
        var observed = DateTimeOffset.Parse("2026-09-16T12:00:00Z");
        var facts = new[]
        {
            Event(1, FleetEventKind.AttemptStarted, "implement", WorkStage.Implement, A),
            Started(101, "implement"),
            Event(2, FleetEventKind.AttemptSettled, "implement"),
            Event(3, FleetEventKind.RevisionProduced, "implement", revision: B),
            Event(4, FleetEventKind.AttemptStarted, "review", WorkStage.Review, B,
                parents: ["implement"], edges: [FleetAttemptEdgeKind.Reviews]),
            Started(104, "review"),
            Event(5, FleetEventKind.AttemptSettled, "review"),
            Event(6, FleetEventKind.ReviewVerdictObserved, "review", revision: B, verdict: "approve"),
        };
        var wrongPr = Item() with
        {
            PullRequest = 42,
            Checks = PullRequestChecks.Passing,
            ChecksHeadSha = B,
            ChecksObservedAt = observed,
            LifecyclePullRequestEvidence = new(43, B, true, true, true, observed),
        };
        var staleChecks = wrongPr with
        {
            LifecyclePullRequestEvidence = new(42, B, true, true, true, observed),
            ChecksObservedAt = observed - TimeSpan.FromMinutes(1),
        };

        Assert.False(LifecycleAttemptGraph.Build(wrongPr, facts,
            LifecycleQueueProjection.Observation(wrongPr, facts, observed)).Ready);
        Assert.False(LifecycleAttemptGraph.Build(staleChecks, facts,
            LifecycleQueueProjection.Observation(staleChecks, facts, observed)).Ready);
    }

    [Fact]
    public void A_typed_start_that_disagrees_with_its_plan_halts_while_a_matching_start_remains_valid()
    {
        var plan = Event(1, FleetEventKind.AttemptStarted, "implement", WorkStage.Implement, A);
        IReadOnlyList<string> grant = ["file-write"];
        var admission = new FleetEvent(11, DateTimeOffset.UtcNow, FleetEventKind.AdmissionDecided, "admission",
            AttemptId: new FleetAttemptId("implement"), WorkId: new FleetWorkId("2363-lane"),
            LifecycleStage: WorkStage.Implement, InputRevisionId: new FleetRevisionId(A),
            Vendor: "codex", Model: "gpt", Effort: "high", DeclaredRole: "implement",
            EffectiveGrant: grant, AssignmentDecisionId: "assignment",
            AdmissionDecision: TaskRequirementAdmission.Admitted);
        var matching = new FleetEvent(12, DateTimeOffset.UtcNow, FleetEventKind.AttemptStarted, "start",
            AttemptId: new FleetAttemptId("implement"), WorkId: new FleetWorkId("2363-lane"),
            LifecycleStage: WorkStage.Implement, InputRevisionId: new FleetRevisionId(A),
            RoomId: new FleetRoomId("room"), Vendor: "codex", Model: "gpt", Effort: "high",
            DeclaredRole: "implement", EffectiveGrant: grant, AssignmentDecisionId: "assignment");
        var contradictory = matching with { LifecycleStage = WorkStage.Review };

        Assert.Null(Baton.Cli.Daemon.LifecycleAttemptGraph.Build(Item(), [plan, admission, matching], Pr(A)).Halt);
        Assert.Equal(LifecycleGraphHaltKind.ContradictoryAttemptIdentity,
            Baton.Cli.Daemon.LifecycleAttemptGraph.Build(Item(), [plan, admission, contradictory], Pr(A)).Halt!.Kind);
    }

    [Fact]
    public void Conflicting_binding_and_malformed_terminal_artifacts_halt_without_reading_the_queue_row()
    {
        var plan = Event(1, FleetEventKind.AttemptStarted, "implement", WorkStage.Implement, A);
        IReadOnlyList<string> grant = ["file-write"];
        var admission = new FleetEvent(11, DateTimeOffset.UtcNow, FleetEventKind.AdmissionDecided, "admission",
            AttemptId: new FleetAttemptId("implement"), WorkId: new FleetWorkId("2363-lane"),
            LifecycleStage: WorkStage.Implement, InputRevisionId: new FleetRevisionId(A),
            Vendor: "codex", Model: "gpt-5.6-sol", Effort: "high", DeclaredRole: "implement",
            EffectiveGrant: grant, AssignmentDecisionId: "decision-a",
            AdmissionDecision: TaskRequirementAdmission.Admitted);
        var started = new FleetEvent(12, DateTimeOffset.UtcNow, FleetEventKind.AttemptStarted, "start",
            AttemptId: new FleetAttemptId("implement"), WorkId: new FleetWorkId("2363-lane"),
            LifecycleStage: WorkStage.Implement, InputRevisionId: new FleetRevisionId(A),
            RoomId: new FleetRoomId("room-a"), Vendor: "codex", Model: "gpt-5.6-sol", Effort: "high",
            DeclaredRole: "implement", EffectiveGrant: grant, AssignmentDecisionId: "decision-a");
        var conflictingStart = started with { Vendor = "claude" };

        Assert.Equal(LifecycleGraphHaltKind.ContradictoryAttemptBinding,
            Baton.Cli.Daemon.LifecycleAttemptGraph.Build(Item(), [plan, admission, conflictingStart], Pr(A)).Halt!.Kind);

        var wrongRoom = started with
        {
            Id = 20,
            Kind = FleetEventKind.AttemptSettled,
            DedupeKey = "settled-room",
            Outcome = WorkflowOutcome.Succeeded,
            RoomId = new FleetRoomId("room-b"),
        };

        Assert.Equal(LifecycleGraphHaltKind.ContradictoryAttemptIdentity,
            Baton.Cli.Daemon.LifecycleAttemptGraph.Build(Item(), [plan, admission, started, wrongRoom], Pr(A)).Halt!.Kind);

        var settled = started with
        {
            Id = 20,
            Kind = FleetEventKind.AttemptSettled,
            DedupeKey = "settled",
            Outcome = WorkflowOutcome.Succeeded,
            ArtifactReferences = ["artifact.txt", "artifact.txt"],
        };

        Assert.Equal(LifecycleGraphHaltKind.MalformedArtifacts,
            Baton.Cli.Daemon.LifecycleAttemptGraph.Build(Item(), [plan, admission, started, settled], Pr(A)).Halt!.Kind);
    }

    [Fact]
    public void Only_fresh_durable_required_check_evidence_for_the_exact_pr_head_can_make_a_review_ready()
    {
        var now = DateTimeOffset.Parse("2026-09-16T12:00:00Z");
        var item = Item() with
        {
            PullRequest = 42,
            Checks = PullRequestChecks.Passing,
            ChecksHeadSha = B,
            ChecksObservedAt = now,
            LifecyclePullRequestEvidence = new(42, B, true, true, true, now),
        };
        var facts = new[]
        {
            Event(1, FleetEventKind.AttemptStarted, "implement", WorkStage.Implement, A),
            Started(101, "implement"),
            Event(2, FleetEventKind.AttemptSettled, "implement"),
            Event(3, FleetEventKind.RevisionProduced, "implement", revision: B),
            Event(4, FleetEventKind.AttemptStarted, "review", WorkStage.Review, B,
                parents: ["implement"], edges: [FleetAttemptEdgeKind.Reviews]),
            Started(104, "review"),
            Event(5, FleetEventKind.AttemptSettled, "review"),
            Event(6, FleetEventKind.ReviewVerdictObserved, "review", revision: B, verdict: "approve"),
            new FleetEvent(70, now - FleetProjectionWriter.StaleAfter() - TimeSpan.FromSeconds(1),
                FleetEventKind.CheckObserved, "required-stale", WorkId: new FleetWorkId("2363-lane"),
                PullRequestId: 42, RevisionId: new FleetRevisionId(B), RequiredChecks: PullRequestChecks.Passing),
        };

        Assert.False(LifecycleAttemptGraph.Build(item, facts,
            LifecycleQueueProjection.Observation(item, facts, now)).Ready);

        var fresh = facts.Append(new FleetEvent(71, now, FleetEventKind.CheckObserved, "required-fresh",
            WorkId: new FleetWorkId("2363-lane"), PullRequestId: 42,
            RevisionId: new FleetRevisionId(B), RequiredChecks: PullRequestChecks.Passing)).ToList();
        Assert.True(LifecycleAttemptGraph.Build(item, fresh,
            LifecycleQueueProjection.Observation(item, fresh, now)).Ready);
    }

    [Fact]
    public void Cancelled_legacy_history_with_no_typed_live_or_runnable_attempt_releases_only_wip()
    {
        var item = Item() with { LifecycleGraphVersion = null, State = QueueItemState.Cancelled };
        var facts = new[]
        {
            new FleetEvent(1, DateTimeOffset.UtcNow, FleetEventKind.AttemptStarted, "started",
                AttemptId: new FleetAttemptId("old"), WorkId: new FleetWorkId(item.Tag)),
            new FleetEvent(2, DateTimeOffset.UtcNow, FleetEventKind.AttemptSettled, "settled",
                AttemptId: new FleetAttemptId("old"), WorkId: new FleetWorkId(item.Tag), Outcome: WorkflowOutcome.Cancelled),
        };

        var projected = Assert.Single(LifecycleQueueProjection.Project([item], facts));

        Assert.Contains("legacy-unproven", projected.LifecycleCompatibilityHalt, StringComparison.Ordinal);
        Assert.True(projected.LifecycleCompatibilityReleasesWip);
        Assert.False(QueueScheduler.IsActiveLifecycle(projected));
        Assert.Null(projected.Retirement);
    }

    [Fact]
    public void Typed_attempts_require_a_complete_admission_before_a_matching_start_or_settlement()
    {
        var item = Item();
        var attempt = new FleetAttemptId("identity");
        var plan = FleetEvent.From(1, Baton.Cli.Daemon.LifecycleAttemptGraph.PlanEvent(
            item, attempt, new(WorkStage.Implement, new FleetRevisionId(A), [], "initial"), DateTimeOffset.UtcNow));
        var admission = new FleetEvent(2, DateTimeOffset.UtcNow, FleetEventKind.AdmissionDecided, "admission:identity",
            AttemptId: attempt, WorkId: new FleetWorkId(item.Tag), LifecycleStage: WorkStage.Implement,
            InputRevisionId: new FleetRevisionId(A), Vendor: "codex", Model: "gpt", Effort: "high",
            DeclaredRole: "implement", EffectiveGrant: ["file-write"], AssignmentDecisionId: "assignment",
            AdmissionDecision: TaskRequirementAdmission.Admitted);
        var start = new FleetEvent(3, DateTimeOffset.UtcNow, FleetEventKind.AttemptStarted, "start:identity",
            AttemptId: attempt, WorkId: new FleetWorkId(item.Tag), LifecycleStage: WorkStage.Implement,
            InputRevisionId: new FleetRevisionId(A), RoomId: new FleetRoomId("room-identity"), Vendor: "codex",
            Model: "gpt", Effort: "high", DeclaredRole: "implement", EffectiveGrant: ["file-write"],
            AssignmentDecisionId: "assignment");

        Assert.Null(Baton.Cli.Daemon.LifecycleAttemptGraph.Build(item, [plan, admission, start], Pr(A)).Halt);
        Assert.Equal(LifecycleGraphHaltKind.MissingAdmission,
            Baton.Cli.Daemon.LifecycleAttemptGraph.Build(item, [plan, start], Pr(A)).Halt!.Kind);
        Assert.Equal(LifecycleGraphHaltKind.InvalidChronology,
            Baton.Cli.Daemon.LifecycleAttemptGraph.Build(item, [plan, start, admission with { Id = 4 }], Pr(A)).Halt!.Kind);
        Assert.Equal(LifecycleGraphHaltKind.ContradictoryAttemptBinding,
            Baton.Cli.Daemon.LifecycleAttemptGraph.Build(item, [plan, admission, start with { Model = null }], Pr(A)).Halt!.Kind);

        var incompleteSettlement = new FleetEvent(4, DateTimeOffset.UtcNow, FleetEventKind.AttemptSettled,
            "settled:identity", AttemptId: attempt, WorkId: new FleetWorkId(item.Tag), Outcome: WorkflowOutcome.Succeeded);
        Assert.Equal(LifecycleGraphHaltKind.ContradictoryAttemptIdentity,
            Baton.Cli.Daemon.LifecycleAttemptGraph.Build(item, [plan, admission, start, incompleteSettlement], Pr(A)).Halt!.Kind);
    }

    [Fact]
    public void Equivalent_plans_have_one_deterministic_attempt_identity()
    {
        var plan = new LifecycleNextAttempt(WorkStage.Review, new FleetRevisionId(B),
            [new FleetAttemptEdge(new FleetAttemptId("implement"), FleetAttemptEdgeKind.Reviews)], "reason");

        Assert.Equal(
            LifecycleAttemptGraph.PlanAttemptId(Item(), plan),
            LifecycleAttemptGraph.PlanAttemptId(Item(), plan));
    }

    private static QueueItem Item() => new()
    {
        Tag = "2363-lane",
        Role = "implement",
        Workspace = "C:\\fixture",
        SpecFile = "C:\\fixture\\brief.md",
        LifecycleGraphVersion = LifecycleAttemptGraph.Version,
        Stage = WorkStage.Implement,
    };

    // Most graph scenarios describe routing, not the mechanics of a worker launch. Keep those
    // fixtures mechanically valid: production always persists this complete chain before a result.
    private static class LifecycleAttemptGraph
    {
        public const string Version = Baton.Cli.Daemon.LifecycleAttemptGraph.Version;

        public static LifecycleAttemptGraphResult Build(
            QueueItem item, IReadOnlyList<FleetEvent> facts, LifecyclePullRequestObservation pr) =>
            Baton.Cli.Daemon.LifecycleAttemptGraph.Build(item, CompleteLaunchEvidence(facts), pr);

        public static FleetAttemptId PlanAttemptId(QueueItem item, LifecycleNextAttempt plan) =>
            Baton.Cli.Daemon.LifecycleAttemptGraph.PlanAttemptId(item, plan);

        private static IReadOnlyList<FleetEvent> CompleteLaunchEvidence(IReadOnlyList<FleetEvent> facts)
        {
            var completed = facts.ToList();
            foreach (var group in facts.Where(fact => fact.AttemptId is not null).GroupBy(fact => fact.AttemptId!.Value))
            {
                var plan = group.SingleOrDefault(fact => fact.Kind == FleetEventKind.AttemptPlanned);
                var start = group.SingleOrDefault(fact => fact.Kind == FleetEventKind.AttemptStarted);
                if (plan?.LifecycleStage is not { } stage || plan.InputRevisionId is not { } input || start is null
                    || group.Any(fact => fact.Kind == FleetEventKind.AdmissionDecided))
                {
                    continue;
                }

                const string vendor = "fixture-vendor";
                const string model = "fixture-model";
                const string effort = "fixture-effort";
                const string role = "fixture-role";
                const string assignment = "fixture-assignment";
                IReadOnlyList<string> grant = ["fixture-grant"];
                var room = new FleetRoomId($"fixture-room-{group.Key.Value}");
                completed.Add(new FleetEvent(plan.Id + 1, plan.At, FleetEventKind.AdmissionDecided,
                    $"fixture-admission:{group.Key.Value}", AttemptId: group.Key, WorkId: plan.WorkId,
                    Vendor: vendor, Model: model, Effort: effort, DeclaredRole: role, EffectiveGrant: grant,
                    AssignmentDecisionId: assignment, LifecycleStage: stage, InputRevisionId: input,
                    AdmissionDecision: TaskRequirementAdmission.Admitted));
                completed[completed.IndexOf(start)] = start with
                {
                    LifecycleStage = stage,
                    InputRevisionId = input,
                    RoomId = room,
                    Vendor = vendor,
                    Model = model,
                    Effort = effort,
                    DeclaredRole = role,
                    EffectiveGrant = grant,
                    AssignmentDecisionId = assignment,
                };

                var settlement = group.FirstOrDefault(fact => fact.Kind == FleetEventKind.AttemptSettled);
                if (settlement is not null)
                {
                    completed[completed.IndexOf(settlement)] = settlement with
                    {
                        LifecycleStage = stage,
                        InputRevisionId = input,
                        RoomId = room,
                        Vendor = vendor,
                        Model = model,
                        Effort = effort,
                        DeclaredRole = role,
                        EffectiveGrant = grant,
                        AssignmentDecisionId = assignment,
                    };
                }
            }
            return completed.OrderBy(fact => fact.Id).ToList();
        }
    }

    private static LifecyclePullRequestObservation Pr(string head) =>
        new(42, head, true, true, PullRequestChecks.Passing);

    private static FleetEvent Started(long id, string attempt) => new(
        id >= 100 ? (id - 100) * 10 + 2 : id * 10 + 2,
        DateTimeOffset.Parse("2026-09-16T12:00:00Z"), FleetEventKind.AttemptStarted,
        $"started:{id}", AttemptId: new FleetAttemptId(attempt), WorkId: new FleetWorkId("2363-lane"));

    private static FleetEvent Event(
        long id,
        FleetEventKind kind,
        string attempt,
        WorkStage? stage = null,
        string? input = null,
        string? revision = null,
        string? verdict = null,
        string[]? parents = null,
        FleetAttemptEdgeKind[]? edges = null,
        string? outcome = null,
        bool? workspaceChanged = null,
        bool missingOutcome = false) =>
        new(
            id * 10,
            DateTimeOffset.Parse("2026-09-16T12:00:00Z"),
            kind == FleetEventKind.AttemptStarted ? FleetEventKind.AttemptPlanned : kind,
            $"{kind}:{id}",
            AttemptId: new FleetAttemptId(attempt),
            WorkId: new FleetWorkId("2363-lane"),
            LifecycleStage: stage,
            InputRevisionId: input is null ? null : new FleetRevisionId(input),
            RevisionId: revision is null ? null : new FleetRevisionId(revision),
            ReviewVerdict: verdict,
            Outcome: missingOutcome ? null : outcome ?? (kind == FleetEventKind.AttemptSettled ? WorkflowOutcome.Succeeded : null),
            ParentEdges: parents?.Zip(edges ?? [], (parent, edge) =>
                new FleetAttemptEdge(new FleetAttemptId(parent), edge)).ToList(),
            WorkspaceChanged: workspaceChanged);
}
