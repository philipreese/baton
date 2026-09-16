using Baton.Queue;
using Baton.Status;
using Baton.Domain;

namespace Baton.Cli.Daemon;

/// <summary>
/// Replays the immutable lifecycle facts for one graph-versioned queue item. It deliberately accepts
/// no display state as evidence: a missing, duplicate, contradictory, or forward reference closes the
/// frontier with a typed diagnostic instead of selecting work from the mutable row.
/// </summary>
public static class LifecycleAttemptGraph
{
    public const string Version = "attempt-dag-v1";

    public static LifecycleAttemptGraphResult Build(
        QueueItem workItem,
        IReadOnlyList<FleetEvent> fleetEvents,
        LifecyclePullRequestObservation pullRequest)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        ArgumentNullException.ThrowIfNull(fleetEvents);
        ArgumentNullException.ThrowIfNull(pullRequest);

        if (!string.Equals(workItem.LifecycleGraphVersion, Version, StringComparison.Ordinal))
        {
            return LifecycleAttemptGraphResult.Compatibility(
                new LifecycleGraphHalt(LifecycleGraphHaltKind.LegacyUnproven,
                    "legacy-unproven: this queue row has no complete typed attempt lineage"));
        }

        var relevant = fleetEvents
            .Where(e => e.WorkId is { } work && string.Equals(work.Value, workItem.Tag, StringComparison.Ordinal))
            .OrderBy(e => e.Id)
            .ToList();
        var nodes = new List<LifecycleAttemptNode>();

        foreach (var group in relevant.Where(e => e.AttemptId is not null).GroupBy(e => e.AttemptId!.Value))
        {
            var started = group.Where(e => e.Kind == FleetEventKind.AttemptStarted).ToList();
            if (started.Count != 1)
            {
                return Halt(nodes, LifecycleGraphHaltKind.MissingOrDuplicateStart,
                    $"attempt '{group.Key.Value}' has {started.Count} attemptStarted facts");
            }

            var start = started[0];
            if (start.LifecycleStage is not { } stage || start.InputRevisionId is not { } input)
            {
                return Halt(nodes, LifecycleGraphHaltKind.MissingIdentity,
                    $"attempt '{group.Key.Value}' has no typed stage or exact input revision");
            }

            var settled = group.Where(e => e.Kind == FleetEventKind.AttemptSettled).ToList();
            var revisions = group.Where(e => e.Kind == FleetEventKind.RevisionProduced).ToList();
            var verdicts = group.Where(e => e.Kind == FleetEventKind.ReviewVerdictObserved).ToList();
            if (settled.Count > 1 || revisions.Count > 1 || verdicts.Count > 1)
            {
                return Halt(nodes, LifecycleGraphHaltKind.DuplicateTerminalFact,
                    $"attempt '{group.Key.Value}' has duplicate terminal, revision, or verdict facts");
            }

            // A verdict is evidence about precisely the revision a review consumed. Validate this
            // at node construction, rather than only when a later repair happens to name it: an
            // approval on a stale head must not leave a graph apparently eligible to become ready.
            if (verdicts.SingleOrDefault() is { } verdict
                && (stage is not WorkStage.Review and not WorkStage.ReReview
                    || verdict.RevisionId != input))
            {
                return Halt(nodes, LifecycleGraphHaltKind.MismatchedVerdictRevision,
                    $"attempt '{group.Key.Value}' records a verdict for a revision other than its exact review input");
            }

            if (revisions.SingleOrDefault()?.RevisionId is { } produced && produced == input)
            {
                return Halt(nodes, LifecycleGraphHaltKind.UnchangedRevision,
                    $"attempt '{group.Key.Value}' records a produced revision equal to its input revision");
            }

            var parents = start.ParentAttemptIds ?? (start.ParentAttemptId is { } parent ? [parent] : []);
            var edges = start.ParentEdgeKinds ?? (start.ParentAttemptId is not null ? [FleetAttemptEdgeKind.Implements] : []);
            if (parents.Count != edges.Count || parents.Distinct().Count() != parents.Count)
            {
                return Halt(nodes, LifecycleGraphHaltKind.ContradictoryEdge,
                    $"attempt '{group.Key.Value}' has inconsistent typed parent edges");
            }

            nodes.Add(new LifecycleAttemptNode(
                group.Key, stage, input, parents, edges, start.Id, settled.SingleOrDefault()?.Outcome,
                revisions.SingleOrDefault()?.RevisionId, verdicts.SingleOrDefault()?.RevisionId,
                verdicts.SingleOrDefault()?.ReviewVerdict));
        }

        foreach (var node in nodes)
        {
            if (node.Parents.Count == 0 && node.Stage != WorkStage.Implement)
            {
                return Halt(nodes, LifecycleGraphHaltKind.MissingParent,
                    $"attempt '{node.AttemptId.Value}' is {node.Stage} without a typed predecessor");
            }

            for (var index = 0; index < node.Parents.Count; index++)
            {
                var parent = nodes.SingleOrDefault(n => n.AttemptId == node.Parents[index]);
                if (parent is null)
                {
                    return Halt(nodes, LifecycleGraphHaltKind.MissingParent,
                        $"attempt '{node.AttemptId.Value}' names missing parent '{node.Parents[index].Value}'");
                }

                if (parent.StartEventId >= node.StartEventId)
                {
                    return Halt(nodes, LifecycleGraphHaltKind.FutureParent,
                        $"attempt '{node.AttemptId.Value}' names a future parent '{parent.AttemptId.Value}'");
                }

                if (!EdgeIsSatisfied(node, parent, node.ParentEdges[index]))
                {
                    return Halt(nodes, LifecycleGraphHaltKind.UnsatisfiedDependency,
                        $"attempt '{node.AttemptId.Value}' has no satisfied {node.ParentEdges[index]} dependency");
                }
            }
        }

        var active = nodes.Where(n => n.Outcome is null).ToList();
        if (active.Count > 1)
        {
            return Halt(nodes, LifecycleGraphHaltKind.MultipleRunnableFrontier,
                "more than one lifecycle attempt is runnable");
        }

        var frontier = active.SingleOrDefault();
        return new LifecycleAttemptGraphResult(
            new LifecycleAttemptDag(nodes), frontier is null ? null : new LifecycleFrontier(frontier.AttemptId, frontier.Stage),
            null, LifecycleProjection.From(workItem, nodes, frontier, pullRequest), false);
    }

    private static bool EdgeIsSatisfied(
        LifecycleAttemptNode child,
        LifecycleAttemptNode parent,
        FleetAttemptEdgeKind edge) => edge switch
        {
            FleetAttemptEdgeKind.Implements => child.Stage == WorkStage.Implement
                && parent.ProducedRevision is { } output && output == child.InputRevision,
            FleetAttemptEdgeKind.Reviews => child.Stage is WorkStage.Review or WorkStage.ReReview
                && parent.ProducedRevision is { } output
                && output != parent.InputRevision
                && output == child.InputRevision,
            FleetAttemptEdgeKind.Repairs => child.Stage == WorkStage.Fix
                && parent.ReviewedRevision is { } reviewed
                && reviewed == parent.InputRevision
                && reviewed == child.InputRevision
                && string.Equals(parent.ReviewVerdict, "block", StringComparison.OrdinalIgnoreCase),
            FleetAttemptEdgeKind.Continues => child.Stage == WorkStage.Continue
                && parent.Outcome is { } outcome && !WorkflowOutcome.IsSucceededShaped(outcome)
                && parent.InputRevision == child.InputRevision,
            FleetAttemptEdgeKind.Supersedes => parent.Outcome is not null,
            _ => false,
        };

    private static LifecycleAttemptGraphResult Halt(
        IReadOnlyList<LifecycleAttemptNode> nodes,
        LifecycleGraphHaltKind kind,
        string reason) =>
        new(new LifecycleAttemptDag(nodes), null, new LifecycleGraphHalt(kind, reason),
            LifecycleProjection.Halted, false);
}

public sealed record LifecycleAttemptDag(IReadOnlyList<LifecycleAttemptNode> Nodes);

public sealed record LifecycleAttemptNode(
    FleetAttemptId AttemptId,
    WorkStage Stage,
    FleetRevisionId InputRevision,
    IReadOnlyList<FleetAttemptId> Parents,
    IReadOnlyList<FleetAttemptEdgeKind> ParentEdges,
    long StartEventId,
    string? Outcome,
    FleetRevisionId? ProducedRevision,
    FleetRevisionId? ReviewedRevision,
    string? ReviewVerdict);

public sealed record LifecycleFrontier(FleetAttemptId AttemptId, WorkStage Stage);

/// <summary>The exact external PR/check reading supplied to a graph replay.</summary>
public sealed record LifecyclePullRequestObservation(
    int? Number,
    string? HeadRevision,
    bool Succeeded,
    bool? IsOpen,
    string? RequiredChecks);

public enum LifecycleGraphHaltKind
{
    LegacyUnproven,
    MissingOrDuplicateStart,
    MissingIdentity,
    DuplicateTerminalFact,
    UnchangedRevision,
    ContradictoryEdge,
    MissingParent,
    FutureParent,
    UnsatisfiedDependency,
    MismatchedVerdictRevision,
    MultipleRunnableFrontier,
}

public sealed record LifecycleGraphHalt(LifecycleGraphHaltKind Kind, string Reason);

public sealed record LifecycleProjection(
    WorkStage? DisplayStage,
    QueueItemState? DisplayState,
    int Round,
    bool Active,
    bool PrePullRequest,
    bool LiveReview)
{
    public static readonly LifecycleProjection Halted = new(null, null, 0, false, false, false);

    internal static LifecycleProjection From(
        QueueItem item,
        IReadOnlyList<LifecycleAttemptNode> nodes,
        LifecycleAttemptNode? frontier,
        LifecyclePullRequestObservation pullRequest)
    {
        var ready = frontier is null && nodes.LastOrDefault() is { } completed
            && completed.Stage is WorkStage.Review or WorkStage.ReReview
            && completed.ReviewedRevision == completed.InputRevision
            && string.Equals(completed.ReviewVerdict, "approve", StringComparison.OrdinalIgnoreCase)
            && pullRequest.Succeeded
            && pullRequest.IsOpen == true
            && string.Equals(pullRequest.HeadRevision, completed.InputRevision.Value, StringComparison.Ordinal)
            && string.Equals(pullRequest.RequiredChecks, PullRequestChecks.Passing, StringComparison.Ordinal);

        return new(frontier?.Stage ?? (ready ? WorkStage.Ready : item.Stage),
            frontier is not null ? QueueItemState.Launched : ready ? QueueItemState.Done : QueueItemState.Queued,
            nodes.Count, frontier is not null,
            frontier?.Stage is WorkStage.Implement or WorkStage.Fix or WorkStage.Continue,
            frontier?.Stage is WorkStage.Review or WorkStage.ReReview);
    }
}

public sealed record LifecycleAttemptGraphResult(
    LifecycleAttemptDag Graph,
    LifecycleFrontier? Frontier,
    LifecycleGraphHalt? Halt,
    LifecycleProjection Projection,
    bool IsCompatibility)
{
    internal static LifecycleAttemptGraphResult Compatibility(LifecycleGraphHalt halt) =>
        new(new LifecycleAttemptDag([]), null, halt, LifecycleProjection.Halted, true);
}
