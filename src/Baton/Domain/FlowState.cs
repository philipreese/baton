namespace Baton.Domain;

/// <summary>
/// F1 (#1593 review): which of these four values settled a step's
/// <see cref="StepState.IndeterminateAwaitingResolution"/> flag — the discriminant <c>baton resolve</c>
/// (<c>Baton.Cli.ResolveCommand</c>, <c>Mutation.MutationInterface.RecordCaptureResolutionAsync</c>)
/// admits its verbs on, replacing a bare <see cref="StepState.LatestCapturedResponseFile"/> null/not-null
/// read that could not tell <see cref="ContractFailure"/> (which DOES have something for a conductor to
/// reject — their judgement after inspecting the workspace) from <see cref="VerifyFailed"/>/
/// <see cref="Arrested"/> (which never do, since neither carries a captured response at all).
/// </summary>
public enum IndeterminateProducer
{
    /// <summary>#1594/#1608: <see cref="FlowEvent.ExecutionIndeterminate"/> with a non-null <see cref="FlowEvent.ExecutionIndeterminate.CapturedResponseFile"/> — the worker's declared output(s) were missing but a terminal response was recoverable. <c>--accept-capture</c> and <c>--reject --reason</c> both admit this producer.</summary>
    CapturedResponse,

    /// <summary>#1593: <see cref="FlowEvent.ExecutionIndeterminate"/> with a null <see cref="FlowEvent.ExecutionIndeterminate.CapturedResponseFile"/> — an exit-0 contract failure (or a dead worker on a mutated workspace), and since #1373 a dispatch timeout that killed a worker on a workspace carrying work: three settle shapes, one value, because all three offer a conductor the same judgement and no captured body. Only <c>--reject --reason</c> admits this producer; <c>--accept-capture</c> has no capture to accept and keeps refusing. spec/baton.md §3's producer table is the register.</summary>
    ContractFailure,

    /// <summary>#1623: <see cref="FlowEvent.VerifyFailed"/> — the role's verify command exited non-zero after a clean, contract-satisfied worker exit. #1622 (d): <c>baton resolve --close --reason &lt;text&gt;</c> admits this producer, settling the step resolved-but-Failed; a fresh <c>baton dispatch</c> is still the only way to reopen it.</summary>
    VerifyFailed,

    /// <summary>#1623: <see cref="FlowEvent.ExecutionArrested"/> — a live execution crossed its role's token budget and was arrested. #1622 (d): <c>baton resolve --close --reason &lt;text&gt;</c> admits this producer, settling the step resolved-but-Failed; a fresh <c>baton dispatch</c> is still the only way to reopen it.</summary>
    Arrested,

    /// <summary>#1796: raised by <see cref="FlowEvent.VerifyNotRun"/>'s build-lock-busy shape — see that record's own <see cref="FlowEvent.VerifyNotRun.BuildLockBusy"/> doc for what it means. Admits <c>baton resolve --close --reason &lt;text&gt;</c> the same way <see cref="VerifyFailed"/>/<see cref="Arrested"/> do — a conductor's judgement call, no captured response to accept or reject.</summary>
    BuildLockBusy,
}

/// <summary>
/// <c>FlowState = Project(EventStore, WorkflowDefinitionSnapshot)</c> — workflow state
/// reconstructed from event history, never from live process state, wall-clock time, or anything
/// not frozen inside an event. Producing this from the event log is the State Projector's
/// job (M7 Phase 4); this type is only the shape it projects into.
/// </summary>
/// <param name="Status">
/// A pure projection of <paramref name="Steps"/>, never a stored event, letting a
/// caller distinguish why <c>StartWorkflowAsync</c>'s pump returned — finished vs. waiting on an
/// external decision. Defaults to <see cref="WorkflowStatus.Running"/> for call sites that
/// construct a <see cref="FlowState"/> directly rather than through <c>StateProjector.Project</c>.
/// </param>
/// <param name="StepLessExecutions">
/// Step-less supplementary executions still awaiting completion: accepted, but with no
/// terminal event recorded for them yet. Never affects <paramref name="Status"/> or any
/// <see cref="StepState"/> — by construction, a step-less execution belongs to no step.
/// </param>
/// <param name="CancellationRequestedExecutionIds">
/// <see cref="ExecutionId"/>s with a recorded <see cref="FlowEvent.CancellationRequested"/> and no
/// terminal event yet — the intent Flow still owes a resolution for, whether that
/// resolution is direct finalization for a non-process target (this milestone's Phase 1) or delivery
/// to a live Core process (Phase 2). An <see cref="ExecutionId"/> leaves this list the moment any
/// terminal event lands for it, the same "no terminal event yet" rule every other derived obligation
/// here already follows — so a too-late request never appears here at all. A
/// list, not a set, for the same reason as <paramref name="StepLessExecutions"/>: this type is
/// serialized (see <c>WorkflowDefinitionTests.FlowState_projects_a_skeleton_per_step_status</c>),
/// and <see cref="IReadOnlySet{T}"/> has no default JSON-constructible implementation.
/// </param>
public sealed record FlowState(
    WorkflowDefinitionSnapshotId WorkflowDefinitionSnapshotId,
    IReadOnlyList<StepState> Steps,
    WorkflowStatus Status = WorkflowStatus.Running,
    IReadOnlyList<StepLessExecutionState>? StepLessExecutions = null,
    IReadOnlyList<ExecutionId>? CancellationRequestedExecutionIds = null,
    IReadOnlyList<ExecutionId>? UnmatchedVerifyExecutionIds = null)
{
    /// <summary>Defaults to empty rather than <c>null</c> for call sites that omit the constructor argument.</summary>
    public IReadOnlyList<StepLessExecutionState> StepLessExecutions { get; init; } = StepLessExecutions ?? [];

    /// <summary>Defaults to empty rather than <c>null</c> for call sites that omit the constructor argument.</summary>
    public IReadOnlyList<ExecutionId> CancellationRequestedExecutionIds { get; init; } =
        CancellationRequestedExecutionIds ?? [];

    /// <summary>#1623 / F2: executions with an unmatched VerifyStarted, for crash recovery reconciliation.</summary>
    public IReadOnlyList<ExecutionId> UnmatchedVerifyExecutionIds { get; init; } =
        UnmatchedVerifyExecutionIds ?? [];
}

/// <summary>
/// A workflow's derived, whole-of-DAG status — computed from <see cref="StepState.Status"/>
/// across every step, never stored as its own event.
/// </summary>
public enum WorkflowStatus
{
    /// <summary>At least one step's latest attempt is still in flight (or Flow crashed before recording its outcome).</summary>
    Running,

    /// <summary>No step is running, and at least one is idle at a <see cref="FlowEvent.WorkflowPaused"/> awaiting a decision.</summary>
    Paused,

    /// <summary>The pump reached its fixed point: nothing running, nothing paused, nothing further to dispatch.</summary>
    Terminal,
}

/// <summary>
/// The status of a single step's most recent execution attempt. <see cref="StepStatus.Running"/> covers both
/// "genuinely still executing" and "Flow crashed before recording the outcome" — the two are
/// indistinguishable from the event log alone until a terminal event is observed.
/// </summary>
public enum StepStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    Paused,

    /// <summary>
    /// An external <see cref="DecisionType.Reject"/> resolved this step's pause:
    /// terminally failed with retry foreclosed regardless of remaining budget, and — unlike
    /// <see cref="Failed"/> — reachable even from an underlying <see cref="Succeeded"/> outcome
    /// (the approval-gate "no"). Never a stored event; derived from
    /// <see cref="FlowEvent.WorkflowResumed"/> plus the decision it resolves.
    /// </summary>
    Rejected,
}

/// <summary>A step's projected status, as of the most recent event concerning it.</summary>
/// <param name="UpstreamExecutionIds">
/// The <see cref="ExecutionRequest.UpstreamExecutionIds"/> recorded on <paramref name="LatestExecutionId"/>'s
/// request — empty when the step has no execution yet. This is what the Dependency Resolver's
/// staleness check (condition 2) compares against a dependency's current latest successful
/// <see cref="ExecutionId"/>.
/// </param>
/// <param name="ConsecutiveFailureCount">
/// The number of trailing consecutive <see cref="FlowEvent.ExecutionFailed"/> attempts for this step,
/// resetting to zero on <see cref="FlowEvent.ExecutionSucceeded"/> — the Retry Engine's input for
/// <c>RetryPolicy.MaxAttempts</c>.
/// </param>
/// <param name="LatestFailureClassification">
/// The <see cref="Domain.FailureClassification"/> carried on the latest attempt's
/// <see cref="FlowEvent.ExecutionFailed"/> event; <c>null</c> when the latest attempt did not fail or
/// reported no classification, which every consumer treats as <see cref="Domain.FailureClassification.Retryable"/>.
/// </param>
/// <param name="LatestFailureReason">
/// The Flow-derived diagnostic carried on the latest attempt's <see cref="FlowEvent.ExecutionFailed"/>
/// event — which condition of the failure-classification table was not met, and for a contract failure which
/// declared outputs were unsatisfied. Distinct from <paramref name="LatestFailureClassification"/>,
/// which is the worker's own retry hint rather than an account of what went wrong. <c>null</c> when
/// the latest attempt did not fail, or when it failed before this field existed; absent means "not
/// recorded", never "no reason was derivable".
/// </param>
/// <param name="PauseRecordedForLatestExecution">
/// Whether a <see cref="FlowEvent.WorkflowPaused"/> was ever appended for <paramref name="LatestExecutionId"/>
/// — distinct from <see cref="StepStatus.Paused"/>, which <see cref="FlowEvent.WorkflowResumed"/> clears.
/// The Pause Engine consults this, not the currently-<c>Paused</c> status, so a resumed
/// execution is never re-paused.
/// </param>
/// <param name="PausedOutcome">
/// The underlying terminal <see cref="StepStatus"/> (<see cref="StepStatus.Succeeded"/>,
/// <see cref="StepStatus.Failed"/>, or <see cref="StepStatus.Cancelled"/>) that <paramref name="LatestExecutionId"/>
/// reached before it was masked to <see cref="StepStatus.Paused"/>; <c>null</c> whenever
/// <paramref name="Status"/> is not <see cref="StepStatus.Paused"/>. This is what the External
/// Decision Handler validates <see cref="DecisionType.RetryWithRevision"/>/<see cref="DecisionType.Reject"/>
/// against, since <see cref="Status"/> itself no longer carries that information while paused.
/// </param>
/// <param name="PendingSupplementaryExecutionId">
/// A <see cref="DecisionType.RetryWithRevision"/> or <see cref="DecisionType.Supersede"/> decision's
/// <see cref="FlowEvent.ExternalDecisionRecorded.SupplementaryExecutionId"/>, still owed to this
/// step's next dispatch: recorded against this step (as referent for <c>RetryWithRevision</c>, or as
/// <see cref="FlowEvent.ExternalDecisionRecorded.TargetStepId"/> for <c>Supersede</c>) but not yet
/// carried by a newer <see cref="FlowEvent.ExecutionRequestAccepted"/> for it. A projected fact, not
/// handler state, so a crash between recording the decision and dispatching its consequence loses
/// nothing.
/// </param>
/// <param name="IsPendingSupersedeTarget">
/// Whether a <see cref="DecisionType.Supersede"/> named this (already-<see cref="StepStatus.Succeeded"/>)
/// step as <see cref="FlowEvent.ExternalDecisionRecorded.TargetStepId"/> and no newer
/// <see cref="FlowEvent.ExecutionRequestAccepted"/> has been recorded for it since — the direct
/// consequence the Dependency Resolver dispatches without regard to the staleness check's ordinary conditions,
/// since a superseded step is never "ready" through staleness alone.
/// </param>
/// <param name="LinkedFromExecutionId">
/// <see cref="Domain.ExecutionRequest.LinkedFromExecutionId"/> carried on <paramref name="LatestExecutionId"/>'s
/// own request (issue #1359) — the prior execution <c>baton resume</c> continued to produce this one.
/// <c>null</c> for every step whose latest attempt was an ordinary dispatch or retry.
/// </param>
/// <param name="ExecutionCount">
/// The total lifetime number of <see cref="FlowEvent.ExecutionRequestAccepted"/> events projected for
/// this step (issue #1522) — never reset by <see cref="DecisionType.RetryWithRevision"/> or skipped by
/// <see cref="FailureClassification.ExhaustedUntil"/>, so the Status projector can derive a true
/// execution ordinal.
/// </param>
/// <param name="LatestCapturedResponseFile">
/// #1594, conductor-writes shape: the engine-owned, dot-prefixed file the latest attempt's
/// <see cref="FlowEvent.ExecutionFailed.CapturedResponseFile"/> named, when that attempt's declared
/// output(s) were missing but a response was recoverable. Null when the latest attempt did not fail
/// this way, same "not recorded" semantics as <see cref="LatestFailureReason"/>.
/// </param>
/// <param name="LatestUnsatisfiedOutputNames">
/// <see cref="FlowEvent.ExecutionFailed.UnsatisfiedOutputNames"/>, carried the same hop.
/// </param>
/// <param name="RetryForeclosed">
/// #1586 S1: whether a <see cref="FlowEvent.StepRetryForeclosed"/> has been projected for this step
/// and not since reopened. <see cref="FlowEvent.StepRetryForeclosed"/>'s own remarks name which
/// events reopen it and which merely clear <see cref="RetryNotBefore"/> without doing so.
/// <see cref="Scheduling.RetryEngine.MayRetry"/> returns <c>false</c> unconditionally while this is
/// <c>true</c>, independent of <see cref="LatestFailureClassification"/> or
/// <see cref="ConsecutiveFailureCount"/>.
/// </param>
/// <param name="IndeterminateAwaitingResolution">
/// The single flag for <see cref="Status.WorkflowOutcome.Indeterminate"/>, whichever of its three
/// producers set it (#1608's <see cref="FlowEvent.ExecutionIndeterminate"/> captured-response settle,
/// and #1623's <see cref="FlowEvent.VerifyFailed"/> / <see cref="FlowEvent.ExecutionArrested"/>) —
/// <c>true</c> while one of those has been projected for this step's latest execution and nothing has
/// since reopened or resolved it (<see cref="FlowEvent.CaptureResolved"/>, or a fresh
/// <see cref="FlowEvent.ExecutionRequestAccepted"/>). Drives two independent reads:
/// <see cref="Status.WorkflowOutcome.Describe"/> reports the room <c>Indeterminate</c> whenever any
/// step reads <c>true</c> here (ahead of the ordinary <see cref="Failed"/>/<see cref="Rejected"/>
/// check, even though <see cref="Status"/> itself stays <see cref="Failed"/> — the "single added enum
/// value" ruling adds this at the room-level word only, never at <see cref="StepStatus"/>), and
/// <see cref="Scheduling.RetryEngine.MayRetry"/> refuses unconditionally while this is <c>true</c>,
/// the same explicit-arm shape as <see cref="RetryForeclosed"/>. An accepted resolution flips the
/// step's <b>raw</b> status to <see cref="Succeeded"/> in the same projected step that clears this —
/// but this can also read <c>true</c> while <see cref="Status"/> is <see cref="Paused"/>, not only
/// <see cref="Failed"/> — a step declaring a <see cref="PausePoint"/> settles into
/// <see cref="Paused"/> with this flag still set (spec/baton.md §3, "Unless the step declares a
/// <c>PausePoint</c>", for the mechanism and what follows from it). Never true while
/// <see cref="Status"/> is <see cref="Succeeded"/> or <see cref="Cancelled"/>.
/// </param>
/// <param name="IndeterminateReason">
/// #1623: the human-readable diagnostic for an Indeterminate settled by
/// <see cref="FlowEvent.VerifyFailed"/> or <see cref="FlowEvent.ExecutionArrested"/> — which gate
/// members failed, or what the arrest measured. <b>Diagnostic only: never a gate.</b>
/// <see cref="IndeterminateAwaitingResolution"/> is the one flag every reader branches on, so the
/// captured-response producer (which records its own account on
/// <see cref="LatestFailureReason"/> instead) leaves this null while still reading Indeterminate.
/// </param>
/// <param name="IndeterminateProducer">
/// F1 (#1593 review): which of <see cref="Domain.IndeterminateProducer"/>'s four values raised
/// <paramref name="IndeterminateAwaitingResolution"/> — the discriminant <c>baton resolve</c> admits
/// its verbs on. Null whenever <paramref name="IndeterminateAwaitingResolution"/> is false; cleared in
/// the same breath as <paramref name="IndeterminateReason"/> on resolution or reopen.
/// </param>
/// <param name="IndeterminateVerifyTail">
/// #1701: <see cref="FlowEvent.VerifyFailed"/>'s own <c>Tail</c> — the failing member(s)' OWN
/// captured output, not <paramref name="IndeterminateReason"/>'s one-line member-name summary. Set
/// only by the <see cref="Domain.IndeterminateProducer.VerifyFailed"/> producer (an arrest's
/// <paramref name="IndeterminateReason"/> is already the full diagnostic — nothing truncated to
/// recover); cleared in the same breath as <paramref name="IndeterminateReason"/> on resolution or
/// reopen. Why this exists at all: before #1701, a verify flake's own output lived only in
/// <c>flow.jsonl</c>'s raw event, unreachable from <c>baton status --json</c> — a conductor diagnosing
/// an Indeterminate room had no way to read it without reconstructing the room by hand.
/// </param>
public sealed record StepState(
    StepId StepId,
    StepStatus Status,
    ExecutionId? LatestExecutionId,
    IReadOnlyDictionary<StepId, ExecutionId> UpstreamExecutionIds,
    int ConsecutiveFailureCount = 0,
    FailureClassification? LatestFailureClassification = null,
    string? LatestFailureReason = null,
    bool PauseRecordedForLatestExecution = false,
    StepStatus? PausedOutcome = null,
    ExecutionId? PendingSupplementaryExecutionId = null,
    bool IsPendingSupersedeTarget = false,
    DateTimeOffset? RetryNotBefore = null,
    int? RetryDelayMs = null,
    ExecutionId? RetryScheduledForExecutionId = null,
    DateTimeOffset? LatestExecutionFailedRetryNotBefore = null,
    ExecutionId? LinkedFromExecutionId = null,
    int ExecutionCount = 0,
    string? LatestCapturedResponseFile = null,
    IReadOnlyList<string>? LatestUnsatisfiedOutputNames = null,
    bool RetryForeclosed = false,
    bool IndeterminateAwaitingResolution = false,
    string? IndeterminateReason = null,
    IndeterminateProducer? IndeterminateProducer = null,
    string? IndeterminateVerifyTail = null,
    // #1622 (c)/(d): schema at spec/baton.md §3. Cleared on a fresh dispatch.
    bool ResolvedByConductor = false,
    // #1622/#1390: schema at spec/baton.md §3.
    bool? WorkspaceChanged = null,
    bool? Hollow = null,
    string? HollowReason = null,
    // #1702: FlowEvent.VerifyNotRun's own reason for the latest attempt -- the pre-flight "not
    // runnable" verdict, never a gate. Null whenever the latest attempt's verify step either never ran
    // a pre-flight check (no resolved command, or the command WAS runnable), or the step has since had
    // a fresh execution accepted (StateProjector clears this the same breath it clears
    // IndeterminateReason on ExecutionRequestAccepted).
    string? VerifyNotRunReason = null,
    // F11 (#1720 review, conductor ruling): the `--reject` subset of ResolvedByConductor above --
    // see spec/baton.md §3 for why the two are not one flag and where they are told apart.
    bool ConductorRejected = false,
    // #1945: the latest succeeded execution for this step was killed by the dispatch timeout with a
    // clean, already-pushed workspace -- see FlowEvent.ExecutionSucceeded.FinishedDuringTeardown.
    // Read only by Status.WorkflowOutcome, to pick the room-level word.
    bool FinishedDuringTeardown = false);

/// <summary>
/// A step-less supplementary execution still awaiting completion: minted outside the
/// DAG during a pause, by <c>MutationInterface.RecordSupplementaryExecutionAsync</c>, so it belongs
/// to no <see cref="StepId"/> and never appears among <see cref="FlowState.Steps"/>.
/// </summary>
public sealed record StepLessExecutionState(ExecutionId ExecutionId, string Worker);
