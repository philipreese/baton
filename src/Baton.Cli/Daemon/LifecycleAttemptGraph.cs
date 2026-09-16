using System.Security.Cryptography;
using System.Text;
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
            var admissions = group.Where(e => e.Kind == FleetEventKind.AdmissionDecided).ToList();
            var revisions = group.Where(e => e.Kind == FleetEventKind.RevisionProduced).ToList();
            var verdicts = group.Where(e => e.Kind == FleetEventKind.ReviewVerdictObserved).ToList();
            var refusals = group.Where(e => e.Kind == FleetEventKind.AttemptRefused).ToList();
            var nonProductions = group.Where(e => e.Kind == FleetEventKind.RevisionNotProducedUnchangedHeadAfterWorkspaceChange).ToList();
            if (started.Count > 1 || settled.Count > 1 || admissions.Count > 1 || revisions.Count > 1 || verdicts.Count > 1
                || refusals.Count > 1 || nonProductions.Count > 1)
            {
                return Halt(nodes, LifecycleGraphHaltKind.DuplicateTerminalFact,
                    $"attempt '{group.Key.Value}' has duplicate admission, start, terminal, revision, verdict, or refusal facts");
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
            if (settled.SingleOrDefault() is { Outcome: null or "" })
            {
                return Halt(nodes, LifecycleGraphHaltKind.MissingOutcome,
                    $"attempt '{group.Key.Value}' has an attemptSettled fact without a typed outcome");
            }
            if (started.SingleOrDefault() is { } start && start.Id <= plan.Id
                || settled.SingleOrDefault() is { } settlement
                    && (started.SingleOrDefault() is not { } startedFact || settlement.Id <= startedFact.Id)
                || revisions.SingleOrDefault() is { } revision
                    && (settled.SingleOrDefault() is not { } settledFact || revision.Id <= settledFact.Id)
                || verdicts.SingleOrDefault() is { } verdictFact
                    && (settled.SingleOrDefault() is not { } verdictSettlement || verdictFact.Id <= verdictSettlement.Id)
                || nonProductions.SingleOrDefault() is { } nonProduction
                    && (settled.SingleOrDefault() is not { } nonProductionSettlement || nonProduction.Id <= nonProductionSettlement.Id)
                || refusals.SingleOrDefault() is { } refusal && refusal.Id <= plan.Id)
            {
                return Halt(nodes, LifecycleGraphHaltKind.InvalidChronology,
                    $"attempt '{group.Key.Value}' has result facts outside plan-start-settle order");
            }
            if (admissions.SingleOrDefault() is { } admission && admission.Id <= plan.Id)
            {
                return Halt(nodes, LifecycleGraphHaltKind.InvalidChronology,
                    $"attempt '{group.Key.Value}' has an admission fact before its plan");
            }
            var typedStart = started.SingleOrDefault();
            var typedSettlement = settled.SingleOrDefault();
            if (typedStart is not null
                && ((typedStart.LifecycleStage is { } startStage && startStage != stage)
                    || (typedStart.InputRevisionId is { } startInput && startInput != input)))
            {
                return Halt(nodes, LifecycleGraphHaltKind.ContradictoryAttemptIdentity,
                    $"attempt '{group.Key.Value}' start identity disagrees with its immutable plan");
            }
            if (typedSettlement is not null
                && ((typedSettlement.LifecycleStage is { } settledStage && settledStage != stage)
                    || (typedSettlement.InputRevisionId is { } settledInput && settledInput != input)
                    || typedStart?.RoomId is { } startedRoom && typedSettlement.RoomId is { } settledRoom
                        && settledRoom != startedRoom))
            {
                return Halt(nodes, LifecycleGraphHaltKind.ContradictoryAttemptIdentity,
                    $"attempt '{group.Key.Value}' settlement identity disagrees with its planned start");
            }
            if (settled.SingleOrDefault()?.ArtifactReferences is { } artifacts
                && (artifacts.Any(string.IsNullOrWhiteSpace)
                    || artifacts.Distinct(StringComparer.Ordinal).Count() != artifacts.Count))
            {
                return Halt(nodes, LifecycleGraphHaltKind.MalformedArtifacts,
                    $"attempt '{group.Key.Value}' has malformed produced artifact references");
            }
            var binding = LifecycleAttemptBinding.From(admissions.SingleOrDefault());
            if (binding is not null
                && (!binding.Matches(started.SingleOrDefault()) || !binding.Matches(settled.SingleOrDefault())))
            {
                return Halt(nodes, LifecycleGraphHaltKind.ContradictoryAttemptBinding,
                    $"attempt '{group.Key.Value}' binding or assignment decision disagrees across durable facts");
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
                settled.Count > 0, settled.SingleOrDefault()?.Outcome ?? refusals.SingleOrDefault()?.Outcome,
                refusals.Count > 0, settled.SingleOrDefault()?.WorkspaceChanged,
                nonProductions.Count > 0, revisions.SingleOrDefault()?.RevisionId,
                verdicts.SingleOrDefault()?.RevisionId, verdicts.SingleOrDefault()?.ReviewVerdict,
                binding, started.SingleOrDefault()?.RoomId ?? settled.SingleOrDefault()?.RoomId,
                settled.SingleOrDefault()?.ExecutionId, settled.SingleOrDefault()?.ArtifactReferences));
        }

        nodes.Sort((left, right) => left.PlanEventId.CompareTo(right.PlanEventId));
        foreach (var node in nodes)
        {
            var isRoot = ReferenceEquals(node, nodes[0]);
            if (node.ParentEdges.Count == 0 && (!isRoot || node.Stage != WorkStage.Implement))
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

        var runnable = nodes.Where(n => !n.Settled && !n.Refused).ToList();
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

    /// <summary>
    /// Concurrent scheduler ticks must converge on one plan identity. Random ids turn the event log's
    /// dedupe guarantee into two valid competing frontiers; hashing the complete immutable plan makes
    /// the same logical frontier the same append key in every process.
    /// </summary>
    public static FleetAttemptId PlanAttemptId(QueueItem item, LifecycleNextAttempt plan)
    {
        var edges = string.Join("\n", plan.ParentEdges
            .OrderBy(edge => edge.ParentAttemptId.Value, StringComparer.Ordinal)
            .ThenBy(edge => edge.Kind)
            .Select(edge => $"{edge.Kind}:{edge.ParentAttemptId.Value}"));
        var source = $"{item.Tag}\n{plan.Stage}\n{plan.InputRevision.Value}\n{edges}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
        return new FleetAttemptId($"plan-{hash[..24]}");
    }

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
                return PlanNext(nodes, last, new(
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
                    : PlanNext(nodes, last, new(
                        WorkStage.ReReview, new FleetRevisionId(currentHead),
                        [new FleetAttemptEdge(producer.AttemptId, FleetAttemptEdgeKind.Reviews)],
                        "the prior approval is stale; review the current produced PR head"));
            }
            return LifecycleNextAttemptResult.None;
        }

        if (last.ProducedRevision is not { } produced)
        {
            if (last.RevisionNotProduced)
            {
                return LifecycleNextAttemptResult.Halted(LifecycleGraphHaltKind.UnchangedRevision,
                    $"{WorkStages.Token(last.Stage)} attempt '{last.AttemptId.Value}' changed the workspace but produced no revision");
            }
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
            if (last.WorkspaceChanged != true)
            {
                return LifecycleNextAttemptResult.Halted(LifecycleGraphHaltKind.NoRetainedContinuationState,
                    $"incomplete {WorkStages.Token(last.Stage)} attempt '{last.AttemptId.Value}' has no positive typed workspace-change evidence");
            }
            return PlanNext(nodes, last, new(
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

        return PlanNext(nodes, last, new(
            last.Stage == WorkStage.Fix ? WorkStage.ReReview : WorkStage.Review,
            produced,
            [new FleetAttemptEdge(last.AttemptId, FleetAttemptEdgeKind.Reviews)],
            "exact produced revision is present on the recorded open PR"));
    }

    private static LifecycleNextAttemptResult PlanNext(
        IReadOnlyList<LifecycleAttemptNode> nodes, LifecycleAttemptNode last, LifecycleNextAttempt plan)
    {
        var nextRound = nodes.Count;
        var pairedAutomaticFixReview = last.Stage == WorkStage.Fix && plan.Stage == WorkStage.ReReview;
        return nextRound > WorkStages.MaxRounds && !pairedAutomaticFixReview
            ? LifecycleNextAttemptResult.Halted(LifecycleGraphHaltKind.RoundLimitReached,
                $"the immutable graph already contains {nodes.Count - 1} automatic round(s); "
                + $"the ceiling is {WorkStages.MaxRounds}")
            : LifecycleNextAttemptResult.Planned(plan);
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
                && parent.Settled && parent.WorkspaceChanged == true
                && parent.Outcome is { } outcome && !WorkflowOutcome.IsSucceededShaped(outcome)
                && parent.InputRevision == child.InputRevision,
            FleetAttemptEdgeKind.Supersedes => parent.Settled || parent.Refused,
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
    bool Settled, string? Outcome, bool Refused, bool? WorkspaceChanged, bool RevisionNotProduced,
    FleetRevisionId? ProducedRevision,
    FleetRevisionId? ReviewedRevision, string? ReviewVerdict,
    LifecycleAttemptBinding? Binding, FleetRoomId? RoomId, ExecutionId? ExecutionId,
    IReadOnlyList<string>? ProducedArtifacts)
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
    MissingOutcome,
    InvalidChronology,
    SettledWithoutStart,
    ContradictoryTerminalFact,
    ContradictoryAttemptIdentity,
    ContradictoryAttemptBinding,
    MalformedArtifacts,
    UnchangedRevision,
    ContradictoryEdge,
    MissingParent,
    FutureParent,
    UnsatisfiedDependency,
    MismatchedVerdictRevision,
    MultipleRunnableFrontier,
    MissingReviewVerdict,
    AutomaticFixExhausted,
    RoundLimitReached,
    NoRetainedContinuationState,
    MissingProducedRevision,
    UnprovenPullRequestHead,
    UndeliveredRevision,
    AttemptRefused,
}

public sealed record LifecycleGraphHalt(LifecycleGraphHaltKind Kind, string Reason);

/// <summary>
/// The resolved binding that admission made for one attempt. This keeps the immutable node able to
/// reject a later start or settlement recorded under a different assignment without re-reading a
/// mutable queue row.
/// </summary>
public sealed record LifecycleAttemptBinding(
    string? Vendor, string? Model, string? Effort, string? DeclaredRole,
    IReadOnlyList<string>? EffectiveGrant, string? AssignmentDecisionId)
{
    internal static LifecycleAttemptBinding? From(FleetEvent? admission) => admission is null ? null : new(
        admission.Vendor, admission.Model, admission.Effort, admission.DeclaredRole,
        admission.EffectiveGrant, admission.AssignmentDecisionId);

    internal bool Matches(FleetEvent? fact) => fact is null
        || Same(Vendor, fact.Vendor)
        && Same(Model, fact.Model)
        && Same(Effort, fact.Effort)
        && Same(DeclaredRole, fact.DeclaredRole)
        && Same(AssignmentDecisionId, fact.AssignmentDecisionId)
        && Same(EffectiveGrant, fact.EffectiveGrant);

    private static bool Same(string? decision, string? fact) => fact is null || decision == fact;

    private static bool Same(IReadOnlyList<string>? decision, IReadOnlyList<string>? fact) => fact is null
        || decision is not null && decision.SequenceEqual(fact, StringComparer.Ordinal);
}

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
        var state = frontier is not null
            ? frontier.Started ? QueueItemState.Launched : QueueItemState.Queued
            : ready || next is not null ? QueueItemState.Queued : QueueItemState.Done;
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

    public static LifecyclePullRequestObservation Observation(
        QueueItem item, IReadOnlyList<FleetEvent> events, DateTimeOffset now)
    {
        var evidence = item.LifecyclePullRequestEvidence;
        if (evidence is not { Succeeded: true, Number: { } number }
            || item.PullRequest != number
            || evidence.ObservedAt == default
            || evidence.ObservedAt > now
            || now - evidence.ObservedAt > FleetProjectionWriter.StaleAfter())
        {
            return new(null, null, false, null, null);
        }

        var checks = events
            .Where(e => e.Kind == FleetEventKind.CheckObserved
                && e.WorkId is { } work && string.Equals(work.Value, item.Tag, StringComparison.Ordinal)
                && e.PullRequestId == number
                && e.RevisionId is { } revision && string.Equals(revision.Value, evidence.HeadSha, StringComparison.Ordinal)
                && e.RequiredChecks is not null
                && e.At <= now && now - e.At <= FleetProjectionWriter.StaleAfter())
            .OrderByDescending(e => e.Id)
            .Select(e => e.RequiredChecks)
            .FirstOrDefault(checks => checks is PullRequestChecks.Passing or PullRequestChecks.Pending
                or PullRequestChecks.Failing or PullRequestChecks.None);
        return new(evidence.Number, evidence.HeadSha, true, evidence.IsOpen, checks, evidence.IsDraft);
    }

    public static IReadOnlyList<QueueItem> Project(
        IReadOnlyList<QueueItem> items, IReadOnlyList<FleetEvent> events) =>
        items.Select(item => IsGraphVersioned(item) || item.Stage is not null && item.State == QueueItemState.Cancelled
            ? Apply(item, LifecycleAttemptGraph.Build(item, events, Observation(item, events, DateTimeOffset.UtcNow)),
                LegacyCancelledHasNoLiveOrRunnableAttempt(item, events))
            : item).ToList();

    /// <summary>
    /// This is deliberately narrower than graph migration. A cancelled legacy row whose retained
    /// typed facts show every known attempt settled/refused and no plan left to launch has no live
    /// work to reserve WIP for. It remains an explicit compatibility halt and retirement still has
    /// to prove every old room independently.
    /// </summary>
    public static bool LegacyCancelledHasNoLiveOrRunnableAttempt(QueueItem item, IReadOnlyList<FleetEvent> events)
    {
        if (IsGraphVersioned(item) || item.State != QueueItemState.Cancelled)
        {
            return false;
        }

        var attempts = events
            .Where(e => e.WorkId is { } work && string.Equals(work.Value, item.Tag, StringComparison.Ordinal)
                && e.AttemptId is not null)
            .GroupBy(e => e.AttemptId!.Value)
            .Select(group => group.Select(e => e.Kind).ToHashSet())
            .ToList();
        return attempts.Count > 0 && attempts.All(kinds =>
            !(kinds.Contains(FleetEventKind.AttemptPlanned)
                && !kinds.Contains(FleetEventKind.AttemptStarted)
                && !kinds.Contains(FleetEventKind.AttemptRefused))
            && !(kinds.Contains(FleetEventKind.AttemptStarted)
                && !kinds.Contains(FleetEventKind.AttemptSettled)
                && !kinds.Contains(FleetEventKind.AttemptRefused)));
    }

    public static QueueItem Apply(
        QueueItem item, LifecycleAttemptGraphResult graph, bool legacyCancelledHasNoLiveOrRunnableAttempt = false)
    {
        // Retirement and cancellation remain operator authority. A plan appended immediately before
        // an operator wins the queue CAS is retained as history, but it must never resurrect the row.
        if (item.Retirement is not null || item.State == QueueItemState.Cancelled)
        {
            return item with
            {
                LifecycleGraphActive = graph.IsCompatibility && !legacyCancelledHasNoLiveOrRunnableAttempt
                    ? item.LifecycleGraphActive : false,
                LifecycleGraphPrePullRequest = graph.IsCompatibility && !legacyCancelledHasNoLiveOrRunnableAttempt
                    ? item.LifecycleGraphPrePullRequest : false,
                LifecycleGraphLiveReview = graph.IsCompatibility && !legacyCancelledHasNoLiveOrRunnableAttempt
                    ? item.LifecycleGraphLiveReview : false,
                LifecycleCompatibilityHalt = graph.IsCompatibility && item.State == QueueItemState.Cancelled
                    ? graph.Halt?.Reason
                    : item.LifecycleCompatibilityHalt,
                LifecycleCompatibilityReleasesWip = graph.IsCompatibility
                    && legacyCancelledHasNoLiveOrRunnableAttempt,
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
            AttemptId = frontier?.AttemptId,
            AttemptBaseRevision = frontier?.InputRevision.Value,
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
