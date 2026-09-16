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
    public void Blocking_review_yields_fix_frontier_against_its_exact_revision()
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
        ], Pr(B));

        Assert.Null(graph.Halt);
        Assert.Equal(WorkStage.Fix, graph.Frontier!.Stage);
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
        Assert.Equal(QueueItemState.Queued, failing.Projection.DisplayState);
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
    public void Delivered_fix_plans_one_unstarted_rereview_frontier_consuming_C()
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
            Event(10, FleetEventKind.AttemptStarted, "rereview", WorkStage.ReReview, C,
                parents: ["fix"], edges: [FleetAttemptEdgeKind.Reviews]),
        };

        var graph = LifecycleAttemptGraph.Build(Item(), facts, Pr(C));

        Assert.Null(graph.Halt);
        Assert.Equal(WorkStage.ReReview, graph.Frontier!.Stage);
        Assert.False(graph.Frontier.Started);
        Assert.Equal(new FleetRevisionId(C), graph.Graph.Nodes.Single(n => n.AttemptId.Value == "rereview").InputRevision);
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

        var graph = LifecycleAttemptGraph.Build(item, facts, LifecycleQueueProjection.Observation(item));
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
            Event(2, FleetEventKind.AttemptSettled, "implement", outcome: WorkflowOutcome.Failed),
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

    private static QueueItem Item() => new()
    {
        Tag = "2363-lane",
        Role = "implement",
        Workspace = "C:\\fixture",
        SpecFile = "C:\\fixture\\brief.md",
        LifecycleGraphVersion = LifecycleAttemptGraph.Version,
        Stage = WorkStage.Implement,
    };

    private static LifecyclePullRequestObservation Pr(string head) =>
        new(42, head, true, true, PullRequestChecks.Passing);

    private static FleetEvent Started(long id, string attempt) => new(
        id, DateTimeOffset.Parse("2026-09-16T12:00:00Z"), FleetEventKind.AttemptStarted,
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
        string? outcome = null) =>
        new(
            id,
            DateTimeOffset.Parse("2026-09-16T12:00:00Z"),
            kind == FleetEventKind.AttemptStarted ? FleetEventKind.AttemptPlanned : kind,
            $"{kind}:{id}",
            AttemptId: new FleetAttemptId(attempt),
            WorkId: new FleetWorkId("2363-lane"),
            LifecycleStage: stage,
            InputRevisionId: input is null ? null : new FleetRevisionId(input),
            RevisionId: revision is null ? null : new FleetRevisionId(revision),
            ReviewVerdict: verdict,
            Outcome: outcome ?? (kind == FleetEventKind.AttemptSettled ? WorkflowOutcome.Succeeded : null),
            ParentEdges: parents?.Zip(edges ?? [], (parent, edge) =>
                new FleetAttemptEdge(new FleetAttemptId(parent), edge)).ToList());
}
