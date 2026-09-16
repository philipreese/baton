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
            Event(2, FleetEventKind.AttemptSettled, "implement"),
            Event(3, FleetEventKind.RevisionProduced, "implement", revision: B),
            Event(4, FleetEventKind.AttemptStarted, "review", WorkStage.Review, B,
                parents: ["implement"], edges: [FleetAttemptEdgeKind.Reviews]),
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
            Event(2, FleetEventKind.AttemptSettled, "implement"),
            Event(3, FleetEventKind.RevisionProduced, "implement", revision: B),
            Event(4, FleetEventKind.AttemptStarted, "review", WorkStage.Review, B,
                parents: ["implement"], edges: [FleetAttemptEdgeKind.Reviews]),
            Event(5, FleetEventKind.AttemptSettled, "review"),
            Event(6, FleetEventKind.ReviewVerdictObserved, "review", revision: B, verdict: "block"),
            Event(7, FleetEventKind.AttemptStarted, "fix", WorkStage.Fix, B,
                parents: ["review"], edges: [FleetAttemptEdgeKind.Repairs]),
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
            Event(2, FleetEventKind.AttemptSettled, "implement"),
            Event(3, FleetEventKind.RevisionProduced, "implement", revision: B),
            Event(4, FleetEventKind.AttemptStarted, "review", WorkStage.Review, B,
                parents: ["implement"], edges: [FleetAttemptEdgeKind.Reviews]),
            Event(5, FleetEventKind.AttemptSettled, "review"),
            Event(6, FleetEventKind.ReviewVerdictObserved, "review", revision: A, verdict: "block"),
            Event(7, FleetEventKind.AttemptStarted, "fix", WorkStage.Fix, B,
                parents: ["review"], edges: [FleetAttemptEdgeKind.Repairs]),
        ], Pr(B));

        Assert.Equal(LifecycleGraphHaltKind.UnsatisfiedDependency, graph.Halt!.Kind);
    }

    [Fact]
    public void Approval_for_a_stale_head_or_failing_checks_is_not_done()
    {
        var facts = new[]
        {
            Event(1, FleetEventKind.AttemptStarted, "implement", WorkStage.Implement, A),
            Event(2, FleetEventKind.AttemptSettled, "implement"),
            Event(3, FleetEventKind.RevisionProduced, "implement", revision: B),
            Event(4, FleetEventKind.AttemptStarted, "review", WorkStage.Review, B,
                parents: ["implement"], edges: [FleetAttemptEdgeKind.Reviews]),
            Event(5, FleetEventKind.AttemptSettled, "review"),
            Event(6, FleetEventKind.ReviewVerdictObserved, "review", revision: B, verdict: "approve"),
        };

        var stale = LifecycleAttemptGraph.Build(Item(), facts, Pr(A));
        var failing = LifecycleAttemptGraph.Build(Item(), facts, new(42, B, true, true, "failing"));

        Assert.Equal(QueueItemState.Queued, stale.Projection.DisplayState);
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

    private static FleetEvent Event(
        long id,
        FleetEventKind kind,
        string attempt,
        WorkStage? stage = null,
        string? input = null,
        string? revision = null,
        string? verdict = null,
        string[]? parents = null,
        FleetAttemptEdgeKind[]? edges = null) =>
        new(
            id,
            DateTimeOffset.Parse("2026-09-16T12:00:00Z"),
            kind,
            $"{kind}:{id}",
            AttemptId: new FleetAttemptId(attempt),
            WorkId: new FleetWorkId("2363-lane"),
            LifecycleStage: stage?.ToString(),
            InputRevisionId: input is null ? null : new FleetRevisionId(input),
            RevisionId: revision is null ? null : new FleetRevisionId(revision),
            ReviewVerdict: verdict,
            Outcome: kind == FleetEventKind.AttemptSettled ? WorkflowOutcome.Succeeded : null,
            ParentAttemptIds: parents?.Select(value => new FleetAttemptId(value)).ToList(),
            ParentEdgeKinds: edges);
}
