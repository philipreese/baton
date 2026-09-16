using Baton.Domain;
using Baton.Queue;
using Baton.Status;

namespace Baton.Cli.Daemon;

/// <summary>
/// Replays immutable lifecycle plan and result facts. Queue-row stage/state fields are compatibility
/// output only; this type never reads them as authority.
/// </summary>
public static class LifecycleAttemptGraph
{
    public const string Version = QueueItem.AttemptGraphVersion;

    public static LifecycleAttemptGraphResult Build(
        QueueItem workItem, IReadOnlyList<FleetEvent> fleetEvents, LifecyclePullRequestObservation pullRequest)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        ArgumentNullException.ThrowIfNull(fleetEvents);
        ArgumentNullException.ThrowIfNull(pullRequest);

        if (!string.Equals(workItem.LifecycleGraphVersion, Version, StringComparison.Ordinal))
        {
            return LifecycleAttemptGraphResult.Compatibility(new(
                LifecycleGraphHaltKind.LegacyUnproven,
                "legacy-unproven: this queue row has no complete typed attempt lineage"));
        }

        var relevant = fleetEvents
            .Where(e => e.WorkId is { } work && string.Equals(work.Value, workItem.Tag, StringComparison.Ordinal))
            .OrderBy(e => e.Id)
            .ToList();
        var nodes = new List<LifecycleAttemptNode>();

        foreach (var group in relevant.Where(e => e.AttemptId is not null).GroupBy(e => e.AttemptId!.Value))
        {
            var plans = group.Where(e => e.Kind == FleetEventKind.AttemptPlanned).ToList();
            if (plans.Count != 1)
            {
                return Halt(nodes, LifecycleGraphHaltKind.MissingOrDuplicatePlan,
                    $"attempt '{group.Key.Value}' has {plans.Count} attemptPlanned facts");
            }

            var plan = plans[0];
            if (plan.LifecycleStage is not { } stage || plan.InputRevisionId is not { } input)
            {
                return Halt(nodes, LifecycleGraphHaltKind.MissingIdentity,
                    $"attempt '{group.Key.Value}' has no typed stage or exact input revision");
            }

            var started = group.Where(e => e.Kind == FleetEventKind.AttemptStarted).ToList();
            var settled = group.Where(e => e.Kind == FleetEventKind.AttemptSettled).ToList();
            var revisions = group.Where(e => e.Kind == FleetEventKind.RevisionProduced).ToList();
            var verdicts = group.Where(e => e.Kind == FleetEventKind.ReviewVerdictObserved).ToList();
            var refusals = group.Where(e => e.Kind == FleetEventKind.AttemptRefused).ToList();
            if (started.Count > 1 || settled.Count > 1 || revisions.Count > 1 || verdicts.Count > 1 || refusals.Count > 1)
            {
                return Halt(nodes, LifecycleGraphHaltKind.DuplicateTerminalFact,
                    $"attempt '{group.Key.Value}' has duplicate start, terminal, revision, verdict, or refusal facts");
            }
            if (started.Count == 0 && (settled.Count > 0 || revisions.Count > 0 || verdicts.Count > 0))
            {
                return Halt(nodes, LifecycleGraphHaltKind.SettledWithoutStart,
                    $"attempt '{group.Key.Value}' has result facts without an attemptStarted fact");
            }
            if (refusals.Count > 0 && (started.Count > 0 || settled.Count > 0))
            {
                return Halt(nodes, LifecycleGraphHaltKind.ContradictoryTerminalFact,
                    $"attempt '{group.Key.Value}' is both refused and started or settled");
            }
            if (verdicts.SingleOrDefault() is { } verdict
                && (stage is not WorkStage.Review and not WorkStage.ReReview || verdict.RevisionId != input))
            {
                return Halt(nodes, LifecycleGraphHaltKind.MismatchedVerdictRevision,
                    $"attempt '{group.Key.Value}' records a verdict for a revision other than its exact review input");
            }
            if (revisions.SingleOrDefault()?.RevisionId is { } produced && produced == input)
            {
                return Halt(nodes, LifecycleGraphHaltKind.UnchangedRevision,
                    $"attempt '{group.Key.Value}' records a produced revision equal to its input revision");
            }

            var edges = plan.ParentEdges ?? [];
            if (edges.Select(e => e.ParentAttemptId).Distinct().Count() != edges.Count)
            {
                return Halt(nodes, LifecycleGraphHaltKind.ContradictoryEdge,
                    $"attempt '{group.Key.Value}' has duplicate typed parent edges");
            }

            nodes.Add(new LifecycleAttemptNode(
                group.Key, stage, input, edges, plan.Id, started.SingleOrDefault()?.Id,
                settled.SingleOrDefault()?.Outcome ?? refusals.SingleOrDefault()?.Outcome,
                refusals.Count > 0, revisions.SingleOrDefault()?.RevisionId,
                verdicts.SingleOrDefault()?.RevisionId, verdicts.SingleOrDefault()?.ReviewVerdict));
        }

        foreach (var node in nodes)
        {
            if (node.ParentEdges.Count == 0 && node.Stage != WorkStage.Implement)
            {
                return Halt(nodes, LifecycleGraphHaltKind.MissingParent,
                    $"attempt '{node.AttemptId.Value}' is {node.Stage} without a typed predecessor");
            }
            foreach (var edge in node.ParentEdges)
            {
                var parent = nodes.SingleOrDefault(n => n.AttemptId == edge.ParentAttemptId);
                if (parent is null)
                {
                    return Halt(nodes, LifecycleGraphHaltKind.MissingParent,
                        $"attempt '{node.AttemptId.Value}' names missing parent '{edge.ParentAttemptId.Value}'");
                }
                if (parent.PlanEventId >= node.PlanEventId)
                {
                    return Halt(nodes, LifecycleGraphHaltKind.FutureParent,
                        $"attempt '{node.AttemptId.Value}' names a future parent '{parent.AttemptId.Value}'");
                }
                if (!EdgeIsSatisfied(node, parent, edge.Kind))
                {
                    return Halt(nodes, LifecycleGraphHaltKind.UnsatisfiedDependency,
                        $"attempt '{node.AttemptId.Value}' has no satisfied {edge.Kind} dependency");
                }
            }
        }

        var runnable = nodes.Where(n => n.Outcome is null && !n.Refused).ToList();
        if (runnable.Count > 1)
        {
            return Halt(nodes, LifecycleGraphHaltKind.MultipleRunnableFrontier,
                "more than one lifecycle attempt is runnable");
        }

        var frontier = runnable.SingleOrDefault();
        var next = frontier is null ? DeriveNext(nodes, pullRequest) : LifecycleNextAttemptResult.None;
        if (next.Halt is { } nextHalt)
        {
            return Halt(nodes, nextHalt.Kind, nextHalt.Reason);
        }

        return new(
            new LifecycleAttemptDag(nodes),
            frontier is null ? null : new LifecycleFrontier(frontier.AttemptId, frontier.Stage, frontier.Started),
            next.Plan, null,
            LifecycleProjection.From(nodes, frontier, next.Plan, next.Ready, pullRequest),
            next.Ready, false);
    }

    public static FleetEventDraft PlanEvent(
        QueueItem item, FleetAttemptId attemptId, LifecycleNextAttempt plan, DateTimeOffset at) => new(
        FleetEventKind.AttemptPlanned,
        $"attempt-planned:{attemptId.Value}", at,
        AttemptId: attemptId,
        ParentAttemptId: plan.ParentEdges.FirstOrDefault()?.ParentAttemptId,
        WorkId: new FleetWorkId(item.Tag),
        IssueId: item.Issue,
        PullRequestId: item.PullRequest,
        LifecycleStage: plan.Stage,
        InputRevisionId: plan.InputRevision,
        ParentEdges: plan.ParentEdges);

    private static LifecycleNextAttemptResult DeriveNext(
        IReadOnlyList<LifecycleAttemptNode> nodes, LifecyclePullRequestObservation pullRequest)
    {
        if (nodes.Count == 0)
        {
            return LifecycleNextAttemptResult.None;
        }

        var last = nodes.MaxBy(n => n.PlanEventId)!;
        if (last.Refused)
        {
            return LifecycleNextAttemptResult.Halted(LifecycleGraphHaltKind.AttemptRefused,
                $"planned {WorkStages.Token(last.Stage)} attempt '{last.AttemptId.Value}' was refused before launch");
        }

        if (last.Stage is WorkStage.Review or WorkStage.ReReview)
        {
            if (last.ReviewedRevision is null || last.ReviewVerdict is null)
            {
                return LifecycleNextAttemptResult.Halted(LifecycleGraphHaltKind.MissingReviewVerdict,
                    $"settled {WorkStages.Token(last.Stage)} attempt '{last.AttemptId.Value}' has no exact typed verdict");
            }
            if (string.Equals(last.ReviewVerdict, "block", StringComparison.OrdinalIgnoreCase))
            {
                if (nodes.Any(n => n.Stage == WorkStage.Fix))
                {
                    return LifecycleNextAttemptResult.Halted(LifecycleGraphHaltKind.AutomaticFixExhausted,
                        "the immutable graph already contains the one automatic fix");
                }
                return LifecycleNextAttemptResult.Planned(new(
                    WorkStage.Fix, last.InputRevision,
                    [new FleetAttemptEdge(last.AttemptId, FleetAttemptEdgeKind.Repairs)],
                    "blocking exact-revision verdict authorizes one repair"));
            }
            if (!string.Equals(last.ReviewVerdict, "approve", StringComparison.OrdinalIgnoreCase))
            {
                return LifecycleNextAttemptResult.Halted(LifecycleGraphHaltKind.MissingReviewVerdict,
                    $"review attempt '{last.AttemptId.Value}' has no routing decision");
            }
            if (pullRequest.Succeeded && pullRequest.IsOpen == true
                && string.Equals(pullRequest.HeadRevision, last.InputRevision.Value, StringComparison.Ordinal)
                && string.Equals(pullRequest.RequiredChecks, PullRequestChecks.Passing, StringComparison.Ordinal))
            {
                return LifecycleNextAttemptResult.ReadyResult;
            }
            if (pullRequest.Succeeded && pullRequest.IsOpen == true
                && pullRequest.HeadRevision is { Length: > 0 } currentHead
                && !string.Equals(currentHead, last.InputRevision.Value, StringComparison.Ordinal))
            {
                var producer = nodes.LastOrDefault(n => n.ProducedRevision?.Value == currentHead);
                return producer is null
                    ? LifecycleNextAttemptResult.Halted(LifecycleGraphHaltKind.UnprovenPullRequestHead,
                        $"PR head {currentHead} has no producing attempt in the immutable graph")
                    : LifecycleNextAttemptResult.Planned(new(
                        WorkStage.ReReview, new FleetRevisionId(currentHead),
                        [new FleetAttemptEdge(producer.AttemptId, FleetAttemptEdgeKind.Reviews)],
                        "the prior approval is stale; review the current produced PR head"));
            }
            return LifecycleNextAttemptResult.None;
        }

        if (last.ProducedRevision is not { } produced)
        {
            if (last.Stage == WorkStage.Fix)
            {
                return LifecycleNextAttemptResult.Halted(LifecycleGraphHaltKind.MissingProducedRevision,
                    $"repair attempt '{last.AttemptId.Value}' settled without producing a different revision");
            }
            if (WorkflowOutcome.IsSucceededShaped(last.Outcome))
            {
                return LifecycleNextAttemptResult.Halted(LifecycleGraphHaltKind.MissingProducedRevision,
                    $"successful {WorkStages.Token(last.Stage)} attempt '{last.AttemptId.Value}' has no produced revision");
            }
            return LifecycleNextAttemptResult.Planned(new(
                WorkStage.Continue, last.InputRevision,
                [new FleetAttemptEdge(last.AttemptId, FleetAttemptEdgeKind.Continues)],
                "incomplete code attempt retains one explicit continuation frontier"));
        }

        if (!pullRequest.Succeeded || pullRequest.Number is null || pullRequest.IsOpen != true
            || !string.Equals(pullRequest.HeadRevision, produced.Value, StringComparison.Ordinal))
        {
            return LifecycleNextAttemptResult.Halted(LifecycleGraphHaltKind.UndeliveredRevision,
                $"produced revision {produced.Value} is not the exact open PR head");
        }

        return LifecycleNextAttemptResult.Planned(new(
            last.Stage == WorkStage.Fix ? WorkStage.ReReview : WorkStage.Review,
            produced,
            [new FleetAttemptEdge(last.AttemptId, FleetAttemptEdgeKind.Reviews)],
            "exact produced revision is present on the recorded open PR"));
    }

    private static bool EdgeIsSatisfied(LifecycleAttemptNode child, LifecycleAttemptNode parent, FleetAttemptEdgeKind edge) =>
        edge switch
        {
            FleetAttemptEdgeKind.Reviews => child.Stage is WorkStage.Review or WorkStage.ReReview
                && parent.ProducedRevision is { } output && output != parent.InputRevision && output == child.InputRevision,
            FleetAttemptEdgeKind.Repairs => child.Stage == WorkStage.Fix
                && parent.ReviewedRevision == parent.InputRevision && parent.InputRevision == child.InputRevision
                && string.Equals(parent.ReviewVerdict, "block", StringComparison.OrdinalIgnoreCase),
            FleetAttemptEdgeKind.Continues => child.Stage == WorkStage.Continue
                && parent.Outcome is { } outcome && !WorkflowOutcome.IsSucceededShaped(outcome)
                && parent.InputRevision == child.InputRevision,
            FleetAttemptEdgeKind.Supersedes => parent.Outcome is not null,
            _ => false,
        };

    private static LifecycleAttemptGraphResult Halt(
        IReadOnlyList<LifecycleAttemptNode> nodes, LifecycleGraphHaltKind kind, string reason) =>
        new(new LifecycleAttemptDag(nodes), null, null, new(kind, reason), LifecycleProjection.Halted, false, false);
}

public sealed record LifecycleAttemptDag(IReadOnlyList<LifecycleAttemptNode> Nodes);
public sealed record LifecycleAttemptNode(
    FleetAttemptId AttemptId, WorkStage Stage, FleetRevisionId InputRevision,
    IReadOnlyList<FleetAttemptEdge> ParentEdges, long PlanEventId, long? StartEventId,
    string? Outcome, bool Refused, FleetRevisionId? ProducedRevision,
    FleetRevisionId? ReviewedRevision, string? ReviewVerdict)
{
    public bool Started => StartEventId is not null;
}
public sealed record LifecycleFrontier(FleetAttemptId AttemptId, WorkStage Stage, bool Started);
public sealed record LifecycleNextAttempt(
    WorkStage Stage, FleetRevisionId InputRevision, IReadOnlyList<FleetAttemptEdge> ParentEdges, string Reason);

internal sealed record LifecycleNextAttemptResult(LifecycleNextAttempt? Plan, bool Ready, LifecycleGraphHalt? Halt)
{
    internal static readonly LifecycleNextAttemptResult None = new(null, false, null);
    internal static readonly LifecycleNextAttemptResult ReadyResult = new(null, true, null);
    internal static LifecycleNextAttemptResult Planned(LifecycleNextAttempt plan) => new(plan, false, null);
    internal static LifecycleNextAttemptResult Halted(LifecycleGraphHaltKind kind, string reason) => new(null, false, new(kind, reason));
}

public sealed record LifecyclePullRequestObservation(
    int? Number, string? HeadRevision, bool Succeeded, bool? IsOpen, string? RequiredChecks, bool? IsDraft = null);

public enum LifecycleGraphHaltKind
{
    LegacyUnproven,
    MissingOrDuplicatePlan,
    MissingIdentity,
    DuplicateTerminalFact,
    SettledWithoutStart,
    ContradictoryTerminalFact,
    UnchangedRevision,
    ContradictoryEdge,
    MissingParent,
    FutureParent,
    UnsatisfiedDependency,
    MismatchedVerdictRevision,
    MultipleRunnableFrontier,
    MissingReviewVerdict,
    AutomaticFixExhausted,
    MissingProducedRevision,
    UnprovenPullRequestHead,
    UndeliveredRevision,
    AttemptRefused,
}

public sealed record LifecycleGraphHalt(LifecycleGraphHaltKind Kind, string Reason);

public sealed record LifecycleProjection(
    WorkStage? DisplayStage, QueueItemState? DisplayState, int Round, bool AutomaticFixUsed,
    bool Active, bool PrePullRequest, bool LiveReview)
{
    public static readonly LifecycleProjection Halted = new(null, null, 0, false, false, false, false);

    internal static LifecycleProjection From(
        IReadOnlyList<LifecycleAttemptNode> nodes, LifecycleAttemptNode? frontier,
        LifecycleNextAttempt? next, bool ready, LifecyclePullRequestObservation pullRequest)
    {
        var stage = frontier?.Stage ?? next?.Stage ?? (ready ? WorkStage.Ready : nodes.LastOrDefault()?.Stage);
        var approvedWaiting = frontier is null && nodes.LastOrDefault() is { } completed
            && completed.Stage is WorkStage.Review or WorkStage.ReReview
            && string.Equals(completed.ReviewVerdict, "approve", StringComparison.OrdinalIgnoreCase);
        var state = frontier is not null
            ? frontier.Started ? QueueItemState.Launched : QueueItemState.Queued
            : ready || next is not null || approvedWaiting ? QueueItemState.Queued : QueueItemState.Done;
        // Historical terminal nodes do not occupy WIP. An initial unstarted implementation is new
        // work; an unstarted follow-up remains active only because a predecessor actually launched.
        var active = frontier is not null && (frontier.Started || nodes.Any(n => n.Started));
        return new(stage, state, Math.Max(0, nodes.Count - 1), nodes.Any(n => n.Stage == WorkStage.Fix),
            active, active && pullRequest.Number is null,
            frontier is { Started: true, Stage: WorkStage.Review or WorkStage.ReReview });
    }
}

public sealed record LifecycleAttemptGraphResult(
    LifecycleAttemptDag Graph, LifecycleFrontier? Frontier, LifecycleNextAttempt? NextAttempt,
    LifecycleGraphHalt? Halt, LifecycleProjection Projection, bool Ready, bool IsCompatibility)
{
    internal static LifecycleAttemptGraphResult Compatibility(LifecycleGraphHalt halt) =>
        new(new LifecycleAttemptDag([]), null, null, halt, LifecycleProjection.Halted, false, true);
}

/// <summary>
/// The one compatibility projection seam shared by scheduling, WIP, queue-list and Fleet Glass.
/// Consumers render ordinary <see cref="QueueItem"/> rows and never traverse the graph themselves.
/// </summary>
public static class LifecycleQueueProjection
{
    public static bool IsGraphVersioned(QueueItem item) =>
        string.Equals(item.LifecycleGraphVersion, LifecycleAttemptGraph.Version, StringComparison.Ordinal);

    public static LifecyclePullRequestObservation Observation(QueueItem item)
    {
        var evidence = item.LifecyclePullRequestEvidence;
        if (evidence is not { Succeeded: true })
        {
            return new(null, null, false, null, null);
        }

        var checks = string.Equals(item.ChecksHeadSha, evidence.HeadSha, StringComparison.Ordinal)
            ? item.Checks
            : null;
        return new(evidence.Number, evidence.HeadSha, true, evidence.IsOpen, checks, evidence.IsDraft);
    }

    public static IReadOnlyList<QueueItem> Project(
        IReadOnlyList<QueueItem> items, IReadOnlyList<FleetEvent> events) =>
        items.Select(item => IsGraphVersioned(item)
            ? Apply(item, LifecycleAttemptGraph.Build(item, events, Observation(item)))
            : item).ToList();

    public static QueueItem Apply(QueueItem item, LifecycleAttemptGraphResult graph)
    {
        // Retirement and cancellation remain operator authority. A plan appended immediately before
        // an operator wins the queue CAS is retained as history, but it must never resurrect the row.
        if (item.Retirement is not null || item.State == QueueItemState.Cancelled)
        {
            return item with
            {
                LifecycleGraphActive = false,
                LifecycleGraphPrePullRequest = false,
                LifecycleGraphLiveReview = false,
            };
        }
        if (graph.IsCompatibility)
        {
            return item;
        }
        if (graph.Halt is { } halt)
        {
            return item with
            {
                State = QueueItemState.Failed,
                Halted = true,
                Error = halt.Reason,
                LifecycleGraphActive = false,
                LifecycleGraphPrePullRequest = false,
                LifecycleGraphLiveReview = false,
            };
        }
        if (graph.Projection.DisplayStage is not { } stage || graph.Projection.DisplayState is not { } state)
        {
            return item;
        }

        var frontier = graph.Frontier is { } marker
            ? graph.Graph.Nodes.Single(node => node.AttemptId == marker.AttemptId)
            : null;
        var parent = frontier is not null
            ? frontier.ParentEdges.FirstOrDefault()?.ParentAttemptId
            : graph.Graph.Nodes.LastOrDefault()?.AttemptId;
        return item with
        {
            Stage = stage,
            Role = stage == WorkStage.Ready ? item.Role : WorkStages.RoleFor(stage),
            State = state,
            Round = graph.Projection.Round,
            AutomaticFixUsed = graph.Projection.AutomaticFixUsed,
            AttemptId = frontier?.Started == true ? frontier.AttemptId : null,
            AttemptBaseRevision = frontier?.Started == true ? frontier.InputRevision.Value : null,
            ParentAttemptId = parent,
            LifecycleGraphActive = graph.Projection.Active,
            LifecycleGraphPrePullRequest = graph.Projection.PrePullRequest,
            LifecycleGraphLiveReview = graph.Projection.LiveReview,
            RoomDirectory = frontier?.Started == true ? item.RoomDirectory : null,
            LaunchedAt = frontier?.Started == true ? item.LaunchedAt : null,
            Halted = false,
            Error = null,
        };
    }
}
