using System.Text.Json.Serialization;
using Baton.Domain;

namespace Baton.Projection;

/// <summary>
/// A persisted projection checkpoint (#903 Scope 1): derived state recording the projected state
/// of a workflow execution along with the event-log offset (<see cref="EventOffset"/>) it corresponds to.
/// Replaying only events past <see cref="EventOffset"/> avoids O(history) re-projection on room open.
/// </summary>
public sealed record ProjectionCheckpoint(
    long EventOffset,
    ProjectionCheckpointState State,
    long ByteOffset = 0,
    // N3 (#1664 re-review): bumped from 3 to 4 because IndeterminateProducerByStepId is new in this
    // PR and its absence from an already-shipped checkpoint is NOT the ordinary
    // trailing-optional-coalesces-to-empty shape the other members above document — an empty map here
    // means "no producer for a step that IS awaiting resolution", which every admission predicate
    // reads as a producer no verb admits, not as "unknown, go find out". A pre-existing awaiting-
    // resolution room's checkpoint would otherwise deserialize as permanently unresolvable.
    // ProjectionCheckpointStore.Load's version gate is what actually forces the full replay this
    // depends on.
    // #1877: bumped 4 -> 5 (see CurrentVersion below) for the same reason at one remove. The change there is to how an ALREADY
    // JOURNALLED CaptureResolved(Accepted: false) projects (it now forecloses retry for every
    // producer, not just ContractFailure/null), so the fix is retroactive on replay — but a room
    // that already holds a Version-4 checkpoint written under the old rule would keep serving the
    // stale non-foreclosed RetryForeclosedStepIds and read Running forever, which is exactly the
    // symptom #1877 exists to end. The gate below forces the full replay that re-derives it.
    int Version = ProjectionCheckpoint.CurrentVersion)
{
    /// <summary>
    /// #1877 (record-once): the version number, written once. It was previously spelled as a bare
    /// literal in three places — this record's own default, <c>StateProjector</c>'s explicit
    /// <c>Version:</c> argument, and <c>ProjectionCheckpointStore.Load</c>'s gate — and the bump this
    /// issue needed silently left the second one behind, which made every checkpoint written be
    /// rejected by the gate on the next read (six checkpoint tests, caught only because they assert
    /// a saved checkpoint loads back). One const, three readers.
    /// </summary>
    public const int CurrentVersion = 5;
}

/// <summary>
/// Serializable snapshot of <see cref="StateProjector"/>'s internal working dictionaries and sets.
/// </summary>
public sealed record ProjectionCheckpointState(
    Dictionary<StepId, ExecutionId> LatestExecutionIdByStepId,
    Dictionary<StepId, Dictionary<StepId, ExecutionId>> UpstreamExecutionIdsByStepId,
    Dictionary<ExecutionId, StepStatus> TerminalStatusByExecutionId,
    HashSet<ExecutionId> PausedExecutionIds,
    HashSet<ExecutionId> EverPausedExecutionIds,
    Dictionary<DecisionId, ExecutionId> ReferencedExecutionIdByDecisionId,
    Dictionary<DecisionId, DecisionType> DecisionTypeByDecisionId,
    Dictionary<DecisionId, StepId> TargetStepIdByDecisionId,
    Dictionary<DecisionId, ExecutionId> SupplementaryExecutionIdByDecisionId,
    Dictionary<ExecutionId, StepId> StepIdByExecutionId,
    Dictionary<StepId, int> ConsecutiveFailureCountByStepId,
    Dictionary<StepId, FailureClassification?> LatestFailureClassificationByStepId,
    Dictionary<StepId, string?> LatestFailureReasonByStepId,
    Dictionary<StepId, DateTimeOffset?> LatestExecutionFailedRetryNotBeforeByStepId,
    HashSet<ExecutionId> CancellationRequestedExecutionIds,
    List<StepLessExecutionState> StepLessExecutionsInOrder,
    Dictionary<StepId, ExecutionId> PendingSupplementaryExecutionIdByStepId,
    HashSet<StepId> PendingSupersedeTargetStepIds,
    Dictionary<StepId, DateTimeOffset> RetryNotBeforeByStepId,
    Dictionary<StepId, int> RetryDelayMsByStepId,
    Dictionary<StepId, ExecutionId> RetryScheduledForExecutionIdByStepId,
    HashSet<ExecutionId> SucceededExecutionIds,
    Dictionary<ExecutionId, ExecutionRequest> AcceptedRequestByExecutionId,
    HashSet<ExecutionId> CoreStartedExecutionIds,
    Dictionary<ExecutionId, CoreEvent.ExecutionExited> CoreExitedByExecutionId,
    Dictionary<StepId, int>? ExecutionCountByStepId = null,
    Dictionary<StepId, string?>? LatestCapturedResponseFileByStepId = null,
    Dictionary<StepId, List<string>?>? LatestUnsatisfiedOutputNamesByStepId = null,
    HashSet<StepId>? RetryForeclosedStepIds = null,
    HashSet<StepId>? IndeterminateAwaitingResolutionStepIds = null,
    Dictionary<StepId, string?>? IndeterminateReasonByStepId = null,
    HashSet<ExecutionId>? UnmatchedVerifyExecutionIds = null,
    Dictionary<StepId, IndeterminateProducer?>? IndeterminateProducerByStepId = null,
    Dictionary<StepId, string?>? IndeterminateVerifyTailByStepId = null,
    HashSet<StepId>? ResolvedByConductorStepIds = null,
    Dictionary<StepId, bool?>? WorkspaceChangedByStepId = null,
    Dictionary<StepId, bool?>? HollowByStepId = null,
    Dictionary<StepId, string?>? HollowReasonByStepId = null,
    Dictionary<StepId, string?>? VerifyNotRunReasonByStepId = null,
    HashSet<StepId>? ConductorRejectedStepIds = null,
    Dictionary<ExecutionId, string>? WorkspaceHeadShaAtStartByExecutionId = null,
    Dictionary<ExecutionId, List<string>>? EnginePlacedPathsByExecutionId = null)
{
    public Dictionary<StepId, int> ExecutionCountByStepId { get; init; } = ExecutionCountByStepId ?? new();

    /// <summary>#1594, conductor-writes shape. Absent from an older checkpoint's serialized JSON coalesces to empty here, same replay-safety shape as <see cref="ExecutionCountByStepId"/> — no <see cref="ProjectionCheckpoint.Version"/> bump needed.</summary>
    public Dictionary<StepId, string?> LatestCapturedResponseFileByStepId { get; init; } = LatestCapturedResponseFileByStepId ?? new();

    public Dictionary<StepId, List<string>?> LatestUnsatisfiedOutputNamesByStepId { get; init; } = LatestUnsatisfiedOutputNamesByStepId ?? new();

    /// <summary>
    /// #1586 S1: which steps carry a projected <see cref="FlowEvent.StepRetryForeclosed"/> not since
    /// cleared. Absent from an older checkpoint's serialized JSON coalesces to empty here, the same
    /// trailing-optional replay-safety shape as <see cref="LatestCapturedResponseFileByStepId"/> above
    /// — no <see cref="ProjectionCheckpoint.Version"/> bump needed. <b>Load-bearing in
    /// <see cref="DeepCopy"/> specifically</b>: that method constructs a new instance positionally, so
    /// a member added only here (relying on this init default) would silently lose every foreclosure
    /// on the very next incremental-checkpoint resume — the exact landmine #1594's own
    /// <c>LatestCapturedResponseFileByStepId</c>/<c>LatestUnsatisfiedOutputNamesByStepId</c> pair hit
    /// first (#1606).
    /// </summary>
    public HashSet<StepId> RetryForeclosedStepIds { get; init; } = RetryForeclosedStepIds ?? new();

    /// <summary>
    /// Which steps carry an unresolved <see cref="Status.WorkflowOutcome.Indeterminate"/> settle, from
    /// any of its three producers: a projected <see cref="FlowEvent.ExecutionIndeterminate"/> not since
    /// resolved by a <see cref="FlowEvent.CaptureResolved"/> (#1608), or a projected
    /// <see cref="FlowEvent.VerifyFailed"/>/<see cref="FlowEvent.ExecutionArrested"/> not since
    /// reopened (#1623). Same trailing-optional replay-safety shape as
    /// <see cref="RetryForeclosedStepIds"/> above, including that member's own <b>load-bearing in
    /// <see cref="DeepCopy"/></b> warning — this hit the identical hazard the same day it was added,
    /// not a fresh one.
    /// </summary>
    public HashSet<StepId> IndeterminateAwaitingResolutionStepIds { get; init; } = IndeterminateAwaitingResolutionStepIds ?? new();

    /// <summary>
    /// #1623: the diagnostic text to show for a verify-failure/arrest Indeterminate — a companion to
    /// <see cref="IndeterminateAwaitingResolutionStepIds"/>, never a second flag, and always written
    /// and cleared in the same breath as it. Same trailing-optional replay-safety shape as
    /// <see cref="RetryForeclosedStepIds"/> above, and the same <see cref="DeepCopy"/> load-bearing
    /// note applies.
    /// </summary>
    public Dictionary<StepId, string?> IndeterminateReasonByStepId { get; init; } = IndeterminateReasonByStepId ?? new();

    /// <summary>
    /// #1623 / F2: executions carrying an unmatched <see cref="FlowEvent.VerifyStarted"/> not since
    /// resolved by verify outcome or terminal Flow event — same trailing-optional replay-safety shape.
    /// </summary>
    public HashSet<ExecutionId> UnmatchedVerifyExecutionIds { get; init; } = UnmatchedVerifyExecutionIds ?? new();

    /// <summary>
    /// F1 (#1593 review): a companion to <see cref="IndeterminateAwaitingResolutionStepIds"/>, never a
    /// second flag — which of <see cref="Domain.IndeterminateProducer"/>'s four values raised it, for
    /// <c>baton resolve</c>'s admission test. Same trailing-optional replay-safety shape as
    /// <see cref="RetryForeclosedStepIds"/> above, and the same <see cref="DeepCopy"/> load-bearing note
    /// applies.
    /// </summary>
    public Dictionary<StepId, IndeterminateProducer?> IndeterminateProducerByStepId { get; init; } = IndeterminateProducerByStepId ?? new();

    /// <summary>
    /// #1701: a companion to <see cref="IndeterminateReasonByStepId"/> for the
    /// <see cref="Domain.IndeterminateProducer.VerifyFailed"/> producer specifically — the failing
    /// member(s)' own captured output (<see cref="FlowEvent.VerifyFailed.Tail"/>), not the one-line
    /// member-name summary <see cref="IndeterminateReasonByStepId"/> already carries. Same
    /// trailing-optional replay-safety shape as <see cref="RetryForeclosedStepIds"/> above, and the
    /// same <see cref="DeepCopy"/> load-bearing note applies.
    /// </summary>
    public Dictionary<StepId, string?> IndeterminateVerifyTailByStepId { get; init; } = IndeterminateVerifyTailByStepId ?? new();

    /// <summary>
    /// #1622 (c)/(d): backs `resolvedByConductor` in the status/terminal-sentinel projection —
    /// see `Domain.FlowState.StepState.ResolvedByConductor`'s remarks for what it means.
    /// Same trailing-optional replay-safety shape as <see cref="RetryForeclosedStepIds"/> above; no
    /// <see cref="ProjectionCheckpoint.Version"/> bump needed. Same <see cref="DeepCopy"/>
    /// load-bearing note applies.
    /// </summary>
    public HashSet<StepId> ResolvedByConductorStepIds { get; init; } = ResolvedByConductorStepIds ?? new();

    /// <summary>
    /// F11 (#1720 review): the <c>--reject</c> SUBSET of <see cref="ResolvedByConductorStepIds"/> —
    /// see `Domain.FlowState.StepState.ConductorRejected`'s remarks for why the two are not the same
    /// set. Same trailing-optional replay-safety shape as <see cref="RetryForeclosedStepIds"/> above;
    /// no <see cref="ProjectionCheckpoint.Version"/> bump needed. Same <see cref="DeepCopy"/>
    /// load-bearing note applies.
    /// </summary>
    public HashSet<StepId> ConductorRejectedStepIds { get; init; } = ConductorRejectedStepIds ?? new();

    /// <summary>
    /// #1622/#1390: see spec/baton.md §3's `workspaceChanged` entry. Same trailing-optional
    /// replay-safety shape as <see cref="RetryForeclosedStepIds"/> above, and the same
    /// <see cref="DeepCopy"/> load-bearing note applies.
    /// </summary>
    public Dictionary<StepId, bool?> WorkspaceChangedByStepId { get; init; } = WorkspaceChangedByStepId ?? new();

    /// <summary>
    /// #1622/#1390: see spec/baton.md §3's `hollow` entry. Same replay-safety shape and
    /// <see cref="DeepCopy"/> load-bearing note as <see cref="WorkspaceChangedByStepId"/> above.
    /// </summary>
    public Dictionary<StepId, bool?> HollowByStepId { get; init; } = HollowByStepId ?? new();

    /// <summary>
    /// #1622/#1390: see spec/baton.md §3's `hollowReason` entry. Same replay-safety shape and
    /// <see cref="DeepCopy"/> load-bearing note as <see cref="WorkspaceChangedByStepId"/> above.
    /// </summary>
    public Dictionary<StepId, string?> HollowReasonByStepId { get; init; } = HollowReasonByStepId ?? new();

    /// <summary>
    /// #1702: which steps' latest attempt recorded a <see cref="FlowEvent.VerifyNotRun"/> — the
    /// pre-flight "not runnable" reason, surfaced on <see cref="Status.WorkflowStatusStepView"/> as
    /// <c>verify: "not-run"</c> so a conductor can tell "this ran unverified" apart from an ordinary
    /// Succeeded step. Same trailing-optional replay-safety shape as <see cref="RetryForeclosedStepIds"/>
    /// above, and the same <see cref="DeepCopy"/> load-bearing note applies. Cleared on a fresh
    /// <see cref="FlowEvent.ExecutionRequestAccepted"/> for the step, the same "the pump is dispatching
    /// it, so the prior attempt's diagnostic is stale" reasoning <see cref="IndeterminateReasonByStepId"/>
    /// already follows.
    /// </summary>
    public Dictionary<StepId, string?> VerifyNotRunReasonByStepId { get; init; } = VerifyNotRunReasonByStepId ?? new();

    /// <summary>
    /// #1373 follow-up: the durable half of <see cref="FlowEvent.ExecutionAttemptStarted"/> — see that
    /// event's own remarks. Same trailing-optional replay-safety shape as
    /// <see cref="RetryForeclosedStepIds"/> above, and the same <see cref="DeepCopy"/> load-bearing
    /// note applies.
    /// </summary>
    public Dictionary<ExecutionId, string> WorkspaceHeadShaAtStartByExecutionId { get; init; } = WorkspaceHeadShaAtStartByExecutionId ?? new();

    /// <summary>
    /// #1933: the durable half of <see cref="FlowEvent.EngineFilesPlaced"/> — see that event's own
    /// remarks for what these paths are and which reader needs them back. Same trailing-optional
    /// replay-safety shape as
    /// <see cref="RetryForeclosedStepIds"/> above, and the same <see cref="DeepCopy"/> load-bearing
    /// note applies — with the list value deep-copied per entry, the way
    /// <see cref="LatestUnsatisfiedOutputNamesByStepId"/> already is.
    /// <para>
    /// <b>No <see cref="ProjectionCheckpoint.Version"/> bump</b>, and not by the ordinary
    /// trailing-optional argument the members above make: this is the shape #1877's own comment says
    /// DOES force a bump — an already-journalled event changing how it projects — except that
    /// <see cref="FlowEvent.EngineFilesPlaced"/> is itself new in the same PR as this reader, so no
    /// checkpoint at <see cref="ProjectionCheckpoint.CurrentVersion"/> can carry one and there is no
    /// stale answer to re-derive.
    /// </para>
    /// </summary>
    public Dictionary<ExecutionId, List<string>> EnginePlacedPathsByExecutionId { get; init; } = EnginePlacedPathsByExecutionId ?? new();

    public static ProjectionCheckpointState CreateEmpty() => new(
        new Dictionary<StepId, ExecutionId>(),
        new Dictionary<StepId, Dictionary<StepId, ExecutionId>>(),
        new Dictionary<ExecutionId, StepStatus>(),
        new HashSet<ExecutionId>(),
        new HashSet<ExecutionId>(),
        new Dictionary<DecisionId, ExecutionId>(),
        new Dictionary<DecisionId, DecisionType>(),
        new Dictionary<DecisionId, StepId>(),
        new Dictionary<DecisionId, ExecutionId>(),
        new Dictionary<ExecutionId, StepId>(),
        new Dictionary<StepId, int>(),
        new Dictionary<StepId, FailureClassification?>(),
        new Dictionary<StepId, string?>(),
        new Dictionary<StepId, DateTimeOffset?>(),
        new HashSet<ExecutionId>(),
        new List<StepLessExecutionState>(),
        new Dictionary<StepId, ExecutionId>(),
        new HashSet<StepId>(),
        new Dictionary<StepId, DateTimeOffset>(),
        new Dictionary<StepId, int>(),
        new Dictionary<StepId, ExecutionId>(),
        new HashSet<ExecutionId>(),
        new Dictionary<ExecutionId, ExecutionRequest>(),
        new HashSet<ExecutionId>(),
        new Dictionary<ExecutionId, CoreEvent.ExecutionExited>(),
        new Dictionary<StepId, int>());

    public ProjectionCheckpointState DeepCopy() => new(
        new Dictionary<StepId, ExecutionId>(LatestExecutionIdByStepId),
        UpstreamExecutionIdsByStepId.ToDictionary(kvp => kvp.Key, kvp => new Dictionary<StepId, ExecutionId>(kvp.Value)),
        new Dictionary<ExecutionId, StepStatus>(TerminalStatusByExecutionId),
        new HashSet<ExecutionId>(PausedExecutionIds),
        new HashSet<ExecutionId>(EverPausedExecutionIds),
        new Dictionary<DecisionId, ExecutionId>(ReferencedExecutionIdByDecisionId),
        new Dictionary<DecisionId, DecisionType>(DecisionTypeByDecisionId),
        new Dictionary<DecisionId, StepId>(TargetStepIdByDecisionId),
        new Dictionary<DecisionId, ExecutionId>(SupplementaryExecutionIdByDecisionId),
        new Dictionary<ExecutionId, StepId>(StepIdByExecutionId),
        new Dictionary<StepId, int>(ConsecutiveFailureCountByStepId),
        new Dictionary<StepId, FailureClassification?>(LatestFailureClassificationByStepId),
        new Dictionary<StepId, string?>(LatestFailureReasonByStepId),
        new Dictionary<StepId, DateTimeOffset?>(LatestExecutionFailedRetryNotBeforeByStepId),
        new HashSet<ExecutionId>(CancellationRequestedExecutionIds),
        new List<StepLessExecutionState>(StepLessExecutionsInOrder),
        new Dictionary<StepId, ExecutionId>(PendingSupplementaryExecutionIdByStepId),
        new HashSet<StepId>(PendingSupersedeTargetStepIds),
        new Dictionary<StepId, DateTimeOffset>(RetryNotBeforeByStepId),
        new Dictionary<StepId, int>(RetryDelayMsByStepId),
        new Dictionary<StepId, ExecutionId>(RetryScheduledForExecutionIdByStepId),
        new HashSet<ExecutionId>(SucceededExecutionIds),
        new Dictionary<ExecutionId, ExecutionRequest>(AcceptedRequestByExecutionId),
        new HashSet<ExecutionId>(CoreStartedExecutionIds),
        new Dictionary<ExecutionId, CoreEvent.ExecutionExited>(CoreExitedByExecutionId),
        new Dictionary<StepId, int>(ExecutionCountByStepId),
        new Dictionary<StepId, string?>(LatestCapturedResponseFileByStepId),
        LatestUnsatisfiedOutputNamesByStepId.ToDictionary(kvp => kvp.Key, kvp => kvp.Value is null ? null : new List<string>(kvp.Value)),
        new HashSet<StepId>(RetryForeclosedStepIds),
        new HashSet<StepId>(IndeterminateAwaitingResolutionStepIds),
        new Dictionary<StepId, string?>(IndeterminateReasonByStepId),
        new HashSet<ExecutionId>(UnmatchedVerifyExecutionIds),
        new Dictionary<StepId, IndeterminateProducer?>(IndeterminateProducerByStepId),
        new Dictionary<StepId, string?>(IndeterminateVerifyTailByStepId),
        new HashSet<StepId>(ResolvedByConductorStepIds),
        new Dictionary<StepId, bool?>(WorkspaceChangedByStepId),
        new Dictionary<StepId, bool?>(HollowByStepId),
        new Dictionary<StepId, string?>(HollowReasonByStepId),
        new Dictionary<StepId, string?>(VerifyNotRunReasonByStepId),
        new HashSet<StepId>(ConductorRejectedStepIds),
        new Dictionary<ExecutionId, string>(WorkspaceHeadShaAtStartByExecutionId),
        EnginePlacedPathsByExecutionId.ToDictionary(kvp => kvp.Key, kvp => new List<string>(kvp.Value)));
}
