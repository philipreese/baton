using System.Text.Json.Serialization;

namespace Baton.Domain;

/// <summary>
/// The <c>flow.jsonl</c> event discriminated union — Flow's exclusive half of the Event Store.
/// There is deliberately no workflow-level transition event: workflow-level
/// status is a pure projection of these events plus the <see cref="WorkflowDefinitionSnapshot"/>,
/// never a stored event.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "eventType")]
[JsonDerivedType(typeof(ExecutionRequestAccepted), "executionRequestAccepted")]
[JsonDerivedType(typeof(ExecutionAttemptStarted), "executionAttemptStarted")]
[JsonDerivedType(typeof(ExecutionRequestRejected), "executionRequestRejected")]
[JsonDerivedType(typeof(ExecutionSucceeded), "executionSucceeded")]
[JsonDerivedType(typeof(ExecutionFailed), "executionFailed")]
[JsonDerivedType(typeof(ExecutionCancelled), "executionCancelled")]
[JsonDerivedType(typeof(CancellationRequested), "cancellationRequested")]
[JsonDerivedType(typeof(WorkflowPaused), "workflowPaused")]
[JsonDerivedType(typeof(ExternalDecisionRecorded), "externalDecisionRecorded")]
[JsonDerivedType(typeof(WorkflowResumed), "workflowResumed")]
[JsonDerivedType(typeof(StepRetryScheduled), "stepRetryScheduled")]
[JsonDerivedType(typeof(StepRetryForeclosed), "stepRetryForeclosed")]
[JsonDerivedType(typeof(ZeroOutputsDespiteSubstantialWork), "zeroOutputsDespiteSubstantialWork")]
[JsonDerivedType(typeof(StepRebound), "stepRebound")]
[JsonDerivedType(typeof(VerifyStarted), "verifyStarted")]
[JsonDerivedType(typeof(VerifyPassed), "verifyPassed")]
[JsonDerivedType(typeof(VerifyFailed), "verifyFailed")]
[JsonDerivedType(typeof(VerifyNotRun), "verifyNotRun")]
[JsonDerivedType(typeof(VerifyDeclarationIgnored), "verifyDeclarationIgnored")]
[JsonDerivedType(typeof(VerifyDeclarationUnreviewed), "verifyDeclarationUnreviewed")]
[JsonDerivedType(typeof(ExecutionArrested), "executionArrested")]
[JsonDerivedType(typeof(ExecutionIndeterminate), "executionIndeterminate")]
[JsonDerivedType(typeof(CaptureResolved), "captureResolved")]
[JsonDerivedType(typeof(ExecutionProgress), "executionProgress")]
[JsonDerivedType(typeof(CancellationDelivered), "cancellationDelivered")]
[JsonDerivedType(typeof(CancellationRejected), "cancellationRejected")]
[JsonDerivedType(typeof(DeliveryPrOpened), "deliveryPrOpened")]
[JsonDerivedType(typeof(DeliveryChecksGreen), "deliveryChecksGreen")]
[JsonDerivedType(typeof(DeliveryChecksRed), "deliveryChecksRed")]
[JsonDerivedType(typeof(DeliveryMerged), "deliveryMerged")]
[JsonDerivedType(typeof(StreamLogLossDeclared), "streamLogLossDeclared")]
[JsonDerivedType(typeof(EngineFilesPlaced), "engineFilesPlaced")]
public abstract record FlowEvent
{
    private FlowEvent()
    {
    }

    /// <summary>Flow has admitted this request for execution (pre-execution, admission control).</summary>
    public sealed record ExecutionRequestAccepted(
        ExecutionRequest Request,
        int? EnginePid = null,
        DateTimeOffset? EngineStartTime = null) : FlowEvent;

    /// <summary>
    /// #1373 follow-up (spec/baton.md §3's "per-attempt start sha is journaled" paragraph is the
    /// canonical explanation — not restated here): the same commit
    /// <see cref="Baton.Outcomes.OutcomeClassifier.Classify"/>'s <c>workspaceHeadShaAtStart</c>
    /// parameter reads on the live-dispatch path, made durable so crash recovery can read it too.
    /// </summary>
    public sealed record ExecutionAttemptStarted(ExecutionId ExecutionId, string WorkspaceHeadShaAtStart) : FlowEvent;

    /// <summary>Flow declined to submit this request, e.g. a concurrency cap.</summary>
    public sealed record ExecutionRequestRejected(ExecutionId ExecutionId, string Reason) : FlowEvent;

    /// <summary>Flow has classified a completed execution as successful.</summary>
    /// <param name="WorkspaceChanged">
    /// #1622/#1390: carried into <see cref="Projection.ProjectionCheckpointState.WorkspaceChangedByStepId"/>.
    /// Nullable because history predates the field: an older <c>flow.jsonl</c> line written before it
    /// existed still replays, with this null, the same "history predates the field" shape <see
    /// cref="ExecutionFailed.Reason"/> already documents.
    /// </param>
    /// <param name="Hollow">Companion to <paramref name="WorkspaceChanged"/>; see its own remarks.</param>
    /// <param name="HollowReason">Non-null only when <paramref name="Hollow"/> is true.</param>
    /// <param name="PeakBilledInWindow">
    /// #1709: the same <c>TokenBudgetMonitor.SnapshotPeakBilledInWindow()</c> reading
    /// <see cref="ExecutionArrested.PeakBilledInWindow"/> already carries, stamped here so a
    /// normally-completed execution's peak reaches the ledger too — before this field, only an arrest
    /// journalled one, which inverted the population spec/baton.md §3's billed-rate calibration
    /// actually needs (the false-positive side). Null whenever this execution ran with no live
    /// <c>TokenBudgetMonitor</c> in scope (a non-Process dispatch, a crash-recovery classification of a
    /// recorded exit, or a spawn refusal before dispatch), and on any ledger line written before this
    /// field existed.
    /// </param>
    /// <param name="FinishedDuringTeardown">
    /// #1945: this execution was killed by the dispatch timeout, but its workspace was clean with
    /// HEAD already on the remote — it finished inside its box and the kill landed in teardown (the
    /// repo's pre-push hook). Carried so <see cref="Status.WorkflowOutcome"/> can say
    /// <see cref="Status.WorkflowOutcome.FinishedDuringTeardown"/> at the room level rather than a
    /// bare "Succeeded" that loses why. False on every other succeeded execution, and on any journal
    /// line written before this field existed.
    /// </param>
    public sealed record ExecutionSucceeded(
        ExecutionId ExecutionId,
        bool? WorkspaceChanged = null,
        bool? Hollow = null,
        string? HollowReason = null,
        long? PeakBilledInWindow = null,
        bool FinishedDuringTeardown = false) : FlowEvent;

    /// <summary>Flow has classified a completed execution as failed.</summary>
    /// <param name="Reason">
    /// A human-readable diagnostic computed once at classification time (see
    /// <see cref="Baton.Outcomes.OutcomeClassifier"/>), distinct from <paramref name="FailureClassification"/>'s
    /// self-reported retry hint. Nullable because history predates the field (#597): an older
    /// <c>flow.jsonl</c> line written before it existed still replays, with this null.
    /// <para>
    /// <b>The <c>= null</c> default is what makes that work, and it is load-bearing — do not remove
    /// it, and do not add a member here without one.</b> Since #604 the journal is read with
    /// <see cref="Baton.Store.FlowEventLogJson.Options"/>, under which a constructor parameter
    /// carrying no default is genuinely required and an absent one fails replay. That is deliberate:
    /// it is what makes a lost or renamed member loud instead of silent. See that type's remarks for
    /// the whole rule — this doc deliberately does not restate it, because an earlier version did and
    /// went on asserting the opposite after the behaviour changed.
    /// </para>
    /// </param>
    /// <param name="CapturedResponseFile">
    /// #1594: carries <c>Outcomes.OutputMaterializer.CapturedResponse.FileName</c> onto the durable
    /// record — <c>OutputMaterializer</c> (the class) explains why this exists at all, and its
    /// <c>CapturedResponse</c> type explains what pairing this with
    /// <paramref name="UnsatisfiedOutputNames"/> means. Null on every execution this mechanism did not
    /// touch, including all history predating it (#597's same replay reasoning applies to every
    /// additive field on this union) — a
    /// required (no-default) parameter here would fail replay of every older line, per this record's
    /// own remarks above.
    /// </param>
    /// <param name="UnsatisfiedOutputNames">
    /// <c>Outcomes.OutputMaterializer.CapturedResponse.UnsatisfiedOutputNames</c>, carried the same hop.
    /// </param>
    /// <param name="PeakBilledInWindow">
    /// #1709: see <see cref="ExecutionSucceeded.PeakBilledInWindow"/>'s remarks — the identical field,
    /// stamped on the other ordinary terminal outcome so every completed execution (not only a
    /// successful one) carries the same measurement. Null under the same conditions that field is.
    /// </param>
    public sealed record ExecutionFailed(
        ExecutionId ExecutionId,
        FailureClassification? FailureClassification,
        string? Reason = null,
        DateTimeOffset? RetryNotBefore = null,
        string? CapturedResponseFile = null,
        IReadOnlyList<string>? UnsatisfiedOutputNames = null,
        long? PeakBilledInWindow = null) : FlowEvent;

    /// <summary>Flow has classified a completed execution as cancelled.</summary>
    public sealed record ExecutionCancelled(ExecutionId ExecutionId) : FlowEvent;

    /// <summary>
    /// Flow has forwarded an on-demand cancellation request toward Core for a still-running
    /// execution. Recorded and fsync'd before the request reaches Core, per the
    /// intent-first write sequence rule.
    /// </summary>
    /// <param name="Origin">
    /// #1762: <see cref="CancellationOrigin"/> — <c>Operator</c> vs. <c>HostStop</c>. Nullable
    /// because history predates the field: a line written before #1762 carries no <c>Origin</c> at
    /// all and replays as null, the same "history predates the field" shape
    /// <see cref="ExecutionSucceeded.WorkspaceChanged"/> already documents. A null <c>Origin</c> is
    /// NOT honoured by <c>MutationInterface</c>'s parked-cancel block (spec/baton.md §2) — that is
    /// exactly the pre-#1762 behaviour for those lines, so an existing ledger can never be made worse
    /// by this field's addition.
    /// </param>
    public sealed record CancellationRequested(ExecutionId ExecutionId, CancellationOrigin? Origin = null) : FlowEvent;

    /// <summary>
    /// A step declaring <see cref="PausePoint"/> reached a terminal outcome; Flow is idle
    /// until a matching <see cref="FlowEvent.ExternalDecisionRecorded"/> arrives.
    /// </summary>
    public sealed record WorkflowPaused(ExecutionId ExecutionId, StepId StepId) : FlowEvent;

    /// <summary>An external party recorded a decision in response to a <see cref="WorkflowPaused"/>.</summary>
    /// <param name="ReferencedExecutionId">Which execution's outcome this decision responds to.</param>
    /// <param name="TargetStepId">Required only for <see cref="DecisionType.Supersede"/>.</param>
    /// <param name="SupplementaryExecutionId">Optional for <see cref="DecisionType.RetryWithRevision"/>; required for <see cref="DecisionType.Supersede"/>.</param>
    /// <param name="Decider">Attribution info for the decider. Defaults to human.</param>
    public sealed record ExternalDecisionRecorded(
        DecisionId DecisionId,
        ExecutionId ReferencedExecutionId,
        DecisionType DecisionType,
        StepId? TargetStepId,
        ExecutionId? SupplementaryExecutionId,
        DeciderInfo? Decider = null) : FlowEvent
    {
        [JsonIgnore]
        public DeciderInfo EffectiveDecider => Decider ?? DeciderInfo.DefaultHuman;
    }



    /// <summary>The workflow is no longer paused following the referenced decision.</summary>
    public sealed record WorkflowResumed(DecisionId DecisionId) : FlowEvent;

    /// <summary>Flow has scheduled a retry backoff deadline for a failed step attempt.</summary>
    /// <param name="EnginePid">
    /// #1577: the same (pid, start-time) pair <see cref="ExecutionRequestAccepted"/> stamps -- why a
    /// step's retry wait needs its own copy is <see cref="Mutation.MutationInterface"/>'s
    /// <c>engineStampedStepIds</c> remarks. A later, identical-schedule re-append with a NEW identity
    /// is a revival renewal, never a re-schedule -- <see cref="Projection.StateProjector"/> applies it
    /// the same idempotent way as the first.
    /// </param>
    /// <param name="EngineStartTime">Paired with <paramref name="EnginePid"/>; see its remarks.</param>
    public sealed record StepRetryScheduled(
        StepId StepId,
        ExecutionId ForExecutionId,
        DateTimeOffset RetryNotBefore,
        int RetryDelayMs,
        int? EnginePid = null,
        DateTimeOffset? EngineStartTime = null) : FlowEvent;

    /// <summary>
    /// #1586 S1: a scheduled retry (<see cref="StepRetryScheduled"/>) was voided without ever being
    /// dispatched — the missing primitive the state-truth design proposal on #1586 names: clearing
    /// <see cref="StepRetryScheduled.RetryNotBefore"/> alone would re-arm the step (an
    /// <see cref="FailureClassification.ExhaustedUntil"/> step bypasses <c>RetryPolicy.MaxAttempts</c>
    /// by design, 0026), so this is a foreclosure, not a clear. <see cref="Scheduling.RetryEngine.MayRetry"/>
    /// returns <c>false</c> once projected, which is what lets <see cref="Projection.StateProjector"/>'s
    /// deliverability predicate go <c>Terminal</c>. Reopened by the same two events that already clear
    /// <see cref="StepRetryScheduled"/>'s fields for a fresh attempt — <see cref="ExecutionRequestAccepted"/>
    /// and a <see cref="DecisionType.RetryWithRevision"/> <see cref="WorkflowResumed"/> — so a
    /// deliberate re-drive reopens the step and a foreclosure is never permanent. (A third event,
    /// <see cref="ExecutionCancelled"/>'s own park-abort clear (#1563), also clears those fields but
    /// does NOT reopen a foreclosure — it terminates the execution rather than re-arming the step, so
    /// there is nothing to reopen.)
    /// </summary>
    /// <param name="ForExecutionId">
    /// The execution whose retry obligation this forecloses. Guards the apply the same way
    /// <see cref="ExecutionCancelled"/>'s own retry-field clear already does (#1605), through two arms
    /// that together mean "this event names the obligation the step carries NOW":
    /// <list type="bullet">
    /// <item>it matches <see cref="Projection.ProjectionCheckpointState.RetryScheduledForExecutionIdByStepId"/>'s
    /// recorded value for <see cref="StepId"/> — a retry already re-scheduled for a NEWER execution of
    /// the same step must survive this event; or</item>
    /// <item>#1877: NOTHING is scheduled for the step and it names
    /// <see cref="Projection.ProjectionCheckpointState.LatestExecutionIdByStepId"/> — a step with no
    /// scheduled retry has no obligation a newer execution could own, which is what lets an
    /// administrative foreclosure (<c>baton resolve --close</c> against an already-rejected capture)
    /// apply.</item>
    /// </list>
    /// A stale name no-ops under both arms: a newer execution moves
    /// <c>LatestExecutionIdByStepId</c> past it, and a retry scheduled for a newer execution fails the
    /// first arm.
    /// </param>
    /// <param name="Reason">Why the retry was foreclosed — a diagnostic, never parsed back.</param>
    /// <param name="ForeclosedBy">
    /// Attribution for who/what recorded the foreclosure (e.g. <c>"settle"</c> once S2's verb exists).
    /// Nullable — this slice writes no producer, so every foreclosure a test fabricates today may
    /// legitimately omit it.
    /// </param>
    public sealed record StepRetryForeclosed(
        StepId StepId,
        ExecutionId ForExecutionId,
        string Reason,
        string? ForeclosedBy = null) : FlowEvent;

    /// <summary>
    /// #1586 S1 (the #1594 ruling's tripwire): a completed execution's own final usage line shows real
    /// work (turns and/or output tokens reported) while every one of its contract's declared outputs is
    /// simply missing — recorded independent of <see cref="ExecutionFailed"/>'s <c>Verdict</c>/
    /// <c>FailureClassification</c> so it fires whether or not <see cref="Outcomes.OutputMaterializer"/>'s
    /// response capture succeeded alongside it (<see cref="Outcomes.OutcomeClassification.SubstantialWorkNoOutputsEvidence"/>
    /// explains the predicate). A diagnostic fact only — nothing in <see cref="Projection.StateProjector"/>
    /// changes <see cref="StepState"/> because of this event; it exists to be loud and durable, not to
    /// drive scheduling.
    /// </summary>
    public sealed record ZeroOutputsDespiteSubstantialWork(
        ExecutionId ExecutionId,
        string Evidence) : FlowEvent;

    /// <summary>
    /// #1623 (contract: <c>spec/baton.md</c> §3): the engine has begun running a
    /// role's declared verify command (<c>pixi run gates-quiet</c> for <c>implement</c>) against a
    /// worker execution that exited 0 with its output contract satisfied. Diagnostic only, the same
    /// "durable fact, no <see cref="StepState"/> consequence" shape as
    /// <see cref="ZeroOutputsDespiteSubstantialWork"/> — <see cref="VerifyPassed"/>/<see cref="VerifyFailed"/>
    /// record how it ended.
    /// </summary>
    public sealed record VerifyStarted(ExecutionId ExecutionId) : FlowEvent;

    /// <summary>#1623: the verify command <see cref="VerifyStarted"/> named exited 0. Diagnostic only.</summary>
    public sealed record VerifyPassed(ExecutionId ExecutionId) : FlowEvent;

    /// <summary>
    /// #1623 (contract: <c>spec/baton.md</c> §3): the role's verify command exited non-zero after the
    /// worker itself exited 0 with a satisfied output contract. Settles the step
    /// <see cref="Status.WorkflowOutcome.Indeterminate"/> — the ruling's own words, "never a blind
    /// retry"; the conductor resolves it. <paramref name="FailingMembers"/>/<paramref name="Tail"/>
    /// mirror <c>tools/gates/gates.py</c>'s own <c>--quiet</c> shape (member names from its
    /// <c>summarise()</c> line, plus a bounded output tail) — never a full log dump.
    /// </summary>
    /// <param name="FailingMembers">Which gate members failed, by name — empty/null if the verify
    /// command reports no per-member breakdown.</param>
    /// <param name="Tail">Each named failing member's OWN captured output (#1701) — see
    /// <see cref="Mutation.VerifyRunner"/>'s own remarks for why a blind tail of the whole run isn't
    /// this, and what happens when the shape isn't recognized.</param>
    /// <param name="Kind">#1623 / F3: whether the failure was broken gates, a timeout, a cancellation, or an engine restart.</param>
    public sealed record VerifyFailed(
        ExecutionId ExecutionId,
        IReadOnlyList<string>? FailingMembers = null,
        string? Tail = null,
        VerifyFailedKind Kind = VerifyFailedKind.GatesFailed) : FlowEvent;

    /// <summary>
    /// #1702 — spec/baton.md §3's not-run outcome:
    /// <see cref="Mutation.VerifyCommandResolver.CheckRunnableAsync"/>'s pre-flight probe found the
    /// resolved verify command not runnable, so it was never spawned. Diagnostic only, same "no
    /// <see cref="Status.WorkflowOutcome.Indeterminate"/> consequence" shape as <see cref="VerifyPassed"/>
    /// — the execution's own already-<c>Succeeded</c> classification decides the room word unassisted.
    /// This is the <see cref="BuildLockBusy"/><c>: false</c> (default) shape only: it is never emitted
    /// alongside <see cref="VerifyStarted"/> for the same execution, so
    /// <see cref="ProjectionCheckpointState.UnmatchedVerifyExecutionIds"/> and the #1608
    /// <c>EngineRestart</c> recovery path are both untouched by this arm.
    /// </summary>
    /// <param name="Reason"><see cref="Mutation.VerifyCommandResolver"/>'s own verdict text, never re-derived here — or, when <paramref name="BuildLockBusy"/> is <c>true</c>, <see cref="Mutation.VerifyRunner"/>'s own build-lock reason text.</param>
    /// <param name="BuildLockBusy">
    /// #1796: <c>true</c> when a verify run actually started (<see cref="VerifyStarted"/> DID fire) and
    /// its only failing member(s) were blocked on <c>tools/buildlock.py</c>'s lock rather than genuinely
    /// broken — contention, not a gate defect. Unlike the pre-flight shape above, this DOES settle the
    /// room Indeterminate (<c>Projection.StateProjector</c>'s <see cref="VerifyNotRun"/> arm), the same
    /// "awaiting conductor resolution" outcome <see cref="VerifyFailed"/> produces, because a build-lock
    /// timeout answers neither "the code passed" nor "the code failed" — it answers "nothing verified
    /// this run", which is not a fact a room may silently report as Succeeded. Defaults to <c>false</c>
    /// so a ledger line written before #1796 still deserializes into the original diagnostic-only shape.
    /// </param>
    public sealed record VerifyNotRun(ExecutionId ExecutionId, string Reason, bool BuildLockBusy = false) : FlowEvent;

    /// <summary>
    /// #1708 H1: the workspace's working-tree <c>.baton/verify</c> differed from the one committed in
    /// <c>HEAD</c> when this execution was dispatched, so the working-tree file was IGNORED and the
    /// committed declaration (or, if there is none, the role default) decided what verify ran. The
    /// self-verification boundary made audible: a worker can write that file, and this says when one
    /// did — or, just as often, that a legitimate declaration was never committed and therefore never
    /// took effect.
    /// <para>
    /// <b>Diagnostic only, and deliberately terminal as a record.</b> Same shape as
    /// <see cref="VerifyStarted"/>/<see cref="VerifyPassed"/>: no <see cref="StepState"/> field, no
    /// <c>WorkflowStatusView</c> surface, no <c>fleet_status</c> plumbing, no
    /// <see cref="Status.WorkflowOutcome"/> consequence. It changes no verdict, so it needs no reader
    /// beyond <c>flow.jsonl</c> — do not "complete" it into one.
    /// </para>
    /// </summary>
    /// <param name="CommittedDigest">
    /// <see cref="Mutation.VerifyCommandResolver.DeclarationDigest"/> of the COMMITTED command line —
    /// null when <c>HEAD</c> holds no declaration (including a non-git workspace), which is exactly the
    /// "an uncommitted declaration was ignored" case.
    /// </param>
    /// <param name="WorkingTreeDigest">The same digest of the working-tree command line; null when the file is absent or comment-only.</param>
    public sealed record VerifyDeclarationIgnored(
        ExecutionId ExecutionId,
        string? CommittedDigest,
        string? WorkingTreeDigest) : FlowEvent;

    /// <summary>
    /// #1708 M1: the declaration that graded this execution came from <c>HEAD</c> rather than from the
    /// merge-base with <c>origin/main</c>, because no merge-base could be computed — no remote, a
    /// default branch that is not <c>main</c>, or unrelated histories. The per-execution boundary still
    /// holds (the value was read before the worker spawned), but the WIDER property does not: on this
    /// workspace, a commit made by an earlier lane on the current branch is inside what grades the next
    /// one, and nothing has reviewed it. This is what says so out loud instead of leaving it to be
    /// inferred from the absence of a ref.
    /// <para>
    /// <b>Diagnostic only</b>, exactly like <see cref="VerifyDeclarationIgnored"/> — no
    /// <see cref="StepState"/> field, no <c>WorkflowStatusView</c> surface, no <c>fleet_status</c>
    /// plumbing, no <see cref="Status.WorkflowOutcome"/> consequence. It changes no verdict and needs no
    /// reader beyond <c>flow.jsonl</c>; do not "complete" it into one.
    /// </para>
    /// <para>
    /// Appended only when a declaration was actually FOUND that way. A workspace with no reviewed
    /// baseline and no <c>.baton/verify</c> at all has nothing unreviewed to announce — it runs the role
    /// default, same as any other.
    /// </para>
    /// </summary>
    /// <param name="Digest">
    /// <see cref="Mutation.VerifyCommandResolver.DeclarationDigest"/> of the command line that was read,
    /// so the journal names WHICH unreviewed line took effect rather than only that one did.
    /// </param>
    public sealed record VerifyDeclarationUnreviewed(
        ExecutionId ExecutionId,
        string? Digest) : FlowEvent;

    /// <summary>
    /// #1929 review (MEDIUM, and the escape clause of its HIGH): the files AER itself wrote into the
    /// worker's working directory immediately before spawning it — today only the claude adapter's
    /// canonical-skill projection (#1151). The room's durable answer to "what did AER put in my
    /// repository during this lane", which before this event existed only as terminal scrollback.
    /// <para>
    /// It is also the record the HIGH's fix rests on: those exact files are subtracted from
    /// <c>workspaceChanged</c> and from the #1373 timeout-retry guard
    /// (<c>WorktreeProvisioner.ChangedPathsExcludingEnginePlaced</c>), so an auditor can see which
    /// paths the engine excluded from its own work-product evidence rather than take the subtraction on
    /// trust. Appended only when at least one file was actually placed — a plan that placed nothing
    /// appends nothing, which is exactly the distinction the MEDIUM was about.
    /// </para>
    /// <para>
    /// <b>Appended before the spawn, not after the exit</b> (#1929 review round 3, LOW). The dispatcher
    /// raises <c>CoreDispatchTarget.OnEngineFilesPlaced</c> the moment the copies are made and awaits the
    /// append, so the fact is durable before the worker process exists. Journaling it after
    /// <c>DispatchAsync</c> returned left an interval — between the durable
    /// <c>CoreEvent.ExecutionExited</c> and the append — in which a crash produced exactly the
    /// crash-recovery classification below with no fact to read, i.e. the defect this event closed. Same
    /// ordering as <see cref="ExecutionAttemptStarted"/>, and now the same durability ordering too.
    /// </para>
    /// <para>
    /// It changes no StepState and no FlowState, but it is <b>not</b> reader-less the way the
    /// <c>Verify*</c> facts above are: <c>StateProjector</c> projects it into
    /// <c>ProjectionCheckpointState.EnginePlacedFilesByExecutionId</c> (#1933), which is what the
    /// crash-recovery path — where <c>CoreDispatchResult</c> is rebuilt from a recorded exit and would
    /// otherwise carry no placement list — reads to make the same subtraction the live path makes. Both
    /// paths therefore judge the worker's work product on the worker's own writes;
    /// <c>WorktreeProvisioner.ChangedPathsExcludingEnginePlaced</c>'s remarks state what an execution
    /// carrying no such fact then reads as.
    /// </para>
    /// </summary>
    /// <param name="Files">
    /// The destination paths actually written, in the order placed, each with the digest of the bytes
    /// placed there — see <see cref="EnginePlacedFile"/> for why the digest is not optional to the
    /// design. <see langword="null"/> only on a journal line predating this shape, which subtracts
    /// nothing.
    /// </param>
    /// <param name="Groups">
    /// The adapter's own labels for what was placed (<c>CoreDispatchSeedCopy.Group</c> — the canonical
    /// skill package names, for claude). Echoed, never interpreted (Architecture Rule 1).
    /// </param>
    public sealed record EngineFilesPlaced(
        ExecutionId ExecutionId,
        IReadOnlyList<EnginePlacedFile>? Files,
        IReadOnlyList<string> Groups) : FlowEvent;

    /// <summary>
    /// #1623 (contract: <c>spec/baton.md</c> §3; the addendum's own words are quoted on
    /// <see cref="Mutation.TokenBudgetMonitor"/>): a live execution's measured usage crossed its role's
    /// token budget, OR (#1682) its tool-step count crossed its role's tool-step cap, OR (#1691) its
    /// billed tokens inside one trailing <c>TokenBudgetMonitor.BilledRateWindow</c> crossed an
    /// operator-supplied <c>--billed-rate-limit</c>. The engine cancels
    /// the execution (arrest, not park) rather than let it keep running.
    /// <paramref name="Usage"/> is the measured usage at arrest time; <paramref name="LastToolNames"/>
    /// the last few tool calls observed, which is what a conductor reads to tell a runaway loop from a
    /// merely long task. Settles the step <see cref="Status.WorkflowOutcome.Indeterminate"/>, same as
    /// <see cref="VerifyFailed"/> — never a blind retry. Deliberately not
    /// <see cref="FlowEvent.CancellationRequested"/>: that event is operator intent, and this is a
    /// distinct, engine-initiated fact.
    /// </summary>
    /// <param name="Reason">
    /// #1682: which producer armed this arrest — see <see cref="ArrestReason"/>. Null on a
    /// pre-#1682 ledger line; <c>StateProjector.DescribeArrest</c> is where that reads as.
    /// </param>
    /// <param name="ToolStepCount">
    /// #1682: the tool-step count at arrest time, set independently of <paramref name="Usage"/> (spec/baton.md §3).
    /// </param>
    /// <param name="PeakBilledInWindow">
    /// #1691: the largest Σ billed tokens this execution held inside one trailing
    /// <c>TokenBudgetMonitor.BilledRateWindow</c> — the OBSERVED rate, recorded whether or not
    /// <paramref name="BilledRateLimit"/> was set. Null on any ledger line written before #1691.
    /// #1709 added the identical field to <see cref="ExecutionSucceeded"/>/<see cref="ExecutionFailed"/>
    /// so a normally-completed execution's peak reaches the ledger too — this field keeps its own
    /// meaning unchanged (the reading at arrest time specifically), never merged with theirs.
    /// </param>
    /// <param name="BilledRateLimit">
    /// #1691: the limit <paramref name="PeakBilledInWindow"/> was compared against, or null when no
    /// rate trigger was armed (every role's default — spec/baton.md §3).
    /// </param>
    /// <param name="Adapter">
    /// #1745: the adapter this execution actually ran on, so <c>StateProjector.DescribeArrest</c> can
    /// name it in a <see cref="ArrestReason.TokenBudget"/> arrest's text — the budget that applied is
    /// now per-adapter, so the reason it fired is incomplete without naming which vendor's figure it
    /// was. Null on a ledger line written before this field existed.
    /// </param>
    /// <param name="DominantCommandShape">
    /// #2002: the one normalised shell-command shape that held more than half this execution's shell
    /// commands (<c>Mutation.TokenBudgetMonitor.SnapshotDominantCommandShape</c>), or null when no
    /// shape did, when none was announced, or on a ledger line written before this field existed. Read
    /// only by a <see cref="ArrestReason.ToolStepCap"/> arrest's text: "arrested at 272 steps" and
    /// "arrested at 272 steps, 54 % of them `Get-Process -Id &lt;n&gt;`" are the same fact and a
    /// different decision for whoever resolves the room.
    /// </param>
    /// <param name="DominantCommandSharePercent">
    /// #2002: <paramref name="DominantCommandShape"/>'s share of the shell commands, as a whole
    /// percent. Always set together with it — both present or both absent.
    /// </param>
    public sealed record ExecutionArrested(
        ExecutionId ExecutionId,
        WorkerUsage? Usage = null,
        IReadOnlyList<string>? LastToolNames = null,
        ArrestReason? Reason = null,
        int? ToolStepCount = null,
        long? PeakBilledInWindow = null,
        long? BilledRateLimit = null,
        string? Adapter = null,
        string? DominantCommandShape = null,
        int? DominantCommandSharePercent = null) : FlowEvent;

    /// <summary>
    /// S6 (spec/baton.md §3, #802 section 3.3, pulled forward by #1583): records that a step's execution was rebound to a different
    /// adapter/model binding. When crash-recovery resubmission encounters a divergent binding
    /// (the current <c>bindings.json</c> differs from the accepted request's recorded <see cref="ExecutionRequest.Adapter"/>
    /// and/or <see cref="ExecutionRequest.Model"/>), Flow journals this event before dispatching so that
    /// usage attribution (<see cref="Status.ExecutionUsageProjector"/>) re-attributes this execution to the
    /// new binding rather than trusting the pre-crash frozen request.
    /// </summary>
    /// <param name="StepId">Which step was rebound.</param>
    /// <param name="ForExecutionId">The execution whose binding diverged.</param>
    /// <param name="PreviousAdapter">The adapter originally recorded on the accepted request.</param>
    /// <param name="PreviousModel">The model originally recorded on the accepted request.</param>
    /// <param name="NewAdapter">The new adapter resolved from the current worker bindings.</param>
    /// <param name="NewModel">The new model resolved from the current worker bindings.</param>
    /// <param name="Reason">Why the step was rebound (diagnostic).</param>
    public sealed record StepRebound(
        StepId StepId,
        ExecutionId ForExecutionId,
        string? PreviousAdapter = null,
        string? PreviousModel = null,
        string? NewAdapter = null,
        string? NewModel = null,
        string? Reason = null) : FlowEvent;

    /// <summary>
    /// #1608: Flow has classified a completed execution as <see cref="Outcomes.OutcomeVerdict.Indeterminate"/>
    /// — see that type's own remarks for what disagrees with what. Distinct from
    /// <see cref="ExecutionFailed"/> rather than reusing it with a sentinel classification: a reader
    /// of this journal sees the disagreement as its own fact, not a <c>Failed</c> collapsed onto a
    /// null <see cref="FailureClassification"/>. Carries no <see cref="FailureClassification"/> at
    /// all — see <see cref="Outcomes.OutcomeVerdict.Indeterminate"/>'s own remarks for why. Projects
    /// to <see cref="StepStatus.Failed"/> (the single-added-enum-value ruling keeps this out of
    /// <see cref="StepStatus"/> itself) plus <see cref="StepState.IndeterminateAwaitingResolution"/>,
    /// which is what actually drives the room-level <c>WorkflowOutcome.Indeterminate</c> reading and
    /// <see cref="Scheduling.RetryEngine.MayRetry"/>'s refusal.
    /// </summary>
    /// <param name="Reason">See <see cref="Outcomes.OutcomeClassification.Reason"/>'s remarks — the same "null means not recorded" rule.</param>
    /// <param name="CapturedResponseFile">See <see cref="ExecutionFailed.CapturedResponseFile"/>'s remarks — carried the same hop.</param>
    /// <param name="UnsatisfiedOutputNames">See <see cref="ExecutionFailed.UnsatisfiedOutputNames"/>'s remarks — carried the same hop.</param>
    public sealed record ExecutionIndeterminate(
        ExecutionId ExecutionId,
        string? Reason = null,
        string? CapturedResponseFile = null,
        IReadOnlyList<string>? UnsatisfiedOutputNames = null) : FlowEvent;

    /// <summary>
    /// #1608: the conductor resolution verb's own room fact — <c>baton resolve</c> is the only
    /// path ever allowed to write under a declared output name from a
    /// <see cref="Outcomes.OutputMaterializer.CapturedResponse"/>, and this event is what makes that
    /// resolution durable and falsifiable from the room record alone. Recorded exactly once per
    /// <see cref="ExecutionIndeterminate"/> — <see cref="Projection.StateProjector"/> clears
    /// <see cref="StepState.IndeterminateAwaitingResolution"/> on apply, so a second resolution
    /// attempt against the same execution is refused before this is ever appended
    /// (<c>Mutation.MutationInterface.RecordCaptureResolutionAsync</c>), not silently re-applied.
    /// </summary>
    /// <param name="StepId">
    /// The step this resolution applies to — carried explicitly (not solely derived via
    /// <paramref name="ExecutionId"/>) the same way <see cref="StepRetryForeclosed"/> carries both
    /// its <c>StepId</c> and its <c>ForExecutionId</c>, so a stale target is a guarded no-op on
    /// replay rather than a silent misapplication to whichever step now owns that execution id.
    /// </param>
    /// <param name="ExecutionId">The indeterminate execution this resolution settles.</param>
    /// <param name="Accepted">
    /// <c>true</c>: the capture honestly satisfies its declared output(s) — the step settles
    /// <see cref="StepStatus.Succeeded"/>, and this event is itself journaled BEFORE the real file(s)
    /// are written (#1608 review finding 5: fact then files, not files then fact — a crash in between
    /// leaves this fact durable with a declared output still missing, which
    /// <c>Mutation.MutationInterface</c>'s own resolution surface re-materializes from the still-durable
    /// capture on the next matching <c>--execution</c>, rather than the mirror gap the opposite order
    /// left open: an orphaned file on disk with no fact and a room still reading Indeterminate).
    /// <c>false</c>: rejected — the step stays
    /// <see cref="StepStatus.Failed"/>, no file is written, and (#1877) retry is foreclosed for every
    /// producer, so the step is terminal and the room settles rather than re-opening as retry-eligible
    /// with no worker or pump alive. An operator who wants the work redone dispatches it fresh; see
    /// spec/baton.md §3's settle-shape table.
    /// </param>
    /// <param name="Reason">
    /// The conductor's own justification — required by <c>ResolveOptionsParser</c> for a rejection,
    /// optional for an acceptance (the accept/reject choice already speaks for itself there).
    /// </param>
    /// <param name="ResolvedOutputNames">
    /// The declared output name(s) this resolution covers — <see cref="ExecutionIndeterminate.UnsatisfiedOutputNames"/>
    /// at resolution time, carried onto this event too so the durable record of "what was written, or
    /// refused" never depends on re-deriving it from projected state.
    /// </param>
    /// <param name="Decider">Attribution info for the decider. Defaults to human, same as <see cref="ExternalDecisionRecorded"/>.</param>
    public sealed record CaptureResolved(
        StepId StepId,
        ExecutionId ExecutionId,
        bool Accepted,
        string? Reason = null,
        IReadOnlyList<string>? ResolvedOutputNames = null,
        DeciderInfo? Decider = null) : FlowEvent
    {
        [JsonIgnore]
        public DeciderInfo EffectiveDecider => Decider ?? DeciderInfo.DefaultHuman;
    }

    /// <summary>
    /// #1549: a coarse, content-free progress heartbeat for a live execution. Carries nothing beyond
    /// the id — the writer-stamped timestamp every journal line already carries
    /// (<see cref="LogEntry.FlowLogEntry.WriterUtcTimestamp"/>) is the "timestamp" half of "execution
    /// id + timestamp only". <c>Baton.Cli.ExecutionProgressHeartbeat</c> is the sole producer and the
    /// canonical explanation of when this fires (cadence, the mtime gate, the coverage limits); see
    /// its own remarks rather than a second copy here. <c>spec/baton.md</c> §2 records why this event
    /// exists at all.
    /// </summary>
    public sealed record ExecutionProgress(ExecutionId ExecutionId) : FlowEvent;

    /// <summary>
    /// #1885: <see cref="Dispatch.ExecutionStreamLogger"/> has latched a declared loss on one of this
    /// execution's stream logs — the stream on disk is provably not a prefix of what the worker emitted.
    /// The JOURNAL half of the two-channel announcement whose whole rule — why a second channel exists,
    /// which one a reader takes first, what the two owe each other — is <c>spec/baton.md</c> §3's, cited
    /// rather than repeated here. Produced by <see cref="Dispatch.CoreDispatcher"/> and consumed by
    /// <see cref="Status.ExecutionUsageProjector"/>; diagnostic-only in
    /// <see cref="Projection.StateProjector"/>, the same shape as <see cref="ExecutionProgress"/>.
    /// </summary>
    /// <param name="Stream">
    /// <c>"stdout"</c> or <c>"stderr"</c> — which of the two stream logs lost bytes. What each is worth
    /// to a reader differs; <c>spec/baton.md</c> §3 draws that line.
    /// </param>
    /// <param name="Reason">
    /// The <see cref="Status.ExecutionUsageView.BilledReconciliationUnavailable"/> value this loss maps
    /// to — today always <c>stream-truncated-by-write-failure</c>, the literal the marker channel yields
    /// too. Deliberately that literal rather than prose: it is what makes <c>spec/baton.md</c> §3's
    /// agreement rule a decidable
    /// comparison instead of a judgement.
    /// </param>
    /// <param name="BytesSurrendered">
    /// Carried verbatim off <c>ExecutionStreamLogger.StreamLogLoss</c>, whose own doc states what this
    /// counts and when it is null.
    /// </param>
    /// <param name="MarkerLanded">
    /// Whether the logger's marker file had been written when this event was emitted.
    /// <c>spec/baton.md</c> §3 states what a
    /// second event carrying <c>false</c> means and why the fact is a field rather than a suffix on
    /// <paramref name="Reason"/>.
    /// </param>
    /// <param name="TerminalReannouncement">
    /// #1888: which of the two emissions this is — <c>false</c> for the declaration, <c>true</c> for the
    /// terminal re-announcement. Carried because <paramref name="MarkerLanded"/> does not identify
    /// either one; <c>spec/baton.md</c> §3 is where that is argued.
    /// <para>
    /// <c>null</c> means <b>a pre-#1888 writer said nothing</b> — deliberately not <c>false</c>, which
    /// would assert "this was the declaration" about a line that never carried the fact. Omitted from
    /// the wire when null (the one <c>WhenWritingNull</c> in this union), so replaying an old journal
    /// and re-serializing it does not invent a field its writer never had.
    /// </para>
    /// </param>
    public sealed record StreamLogLossDeclared(
        ExecutionId ExecutionId,
        string Stream,
        string Reason,
        long? BytesSurrendered = null,
        bool MarkerLanded = false,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        bool? TerminalReannouncement = null) : FlowEvent;

    /// <summary>
    /// #1549: an operator's <c>cancel.request</c> actually reached a live, still-registered
    /// <see cref="Mutation.InFlightExecutionRegistry"/> entry and its
    /// <see cref="System.Threading.CancellationTokenSource"/> was signalled — distinct from
    /// <see cref="CancellationRequested"/>, which only records that Flow forwarded the intent and is
    /// appended immediately before the same signal is attempted
    /// (<see cref="Mutation.InFlightExecutionRegistry.RequestCancellationAsync"/>). Recorded whether
    /// or not the signal actually reaches the worker process before it exits on its own; it is the
    /// delivery of the request into Core, not proof the worker observed it. Content-free by design,
    /// matching <see cref="CancellationRequested"/>'s own shape.
    /// </summary>
    public sealed record CancellationDelivered(ExecutionId ExecutionId) : FlowEvent;

    /// <summary>
    /// #1549: the pump-side <c>cancel.request</c> poller (<c>Baton.Cli.CancelRequestPoller</c>)
    /// exhausted its bounded retry (5 ticks) against a target that still projects
    /// <see cref="StepStatus.Running"/> but was never reachable through
    /// <see cref="Mutation.InFlightExecutionRegistry"/> — the "likely non-process work" refusal
    /// <c>CancelRequestFile.Reject</c> also records to the file channel. Recorded only when a
    /// concrete <see cref="ExecutionId"/> was resolved; a malformed request or an ambiguous
    /// <c>latest</c> (no execution ever named) has nothing to key an execution-scoped journal fact
    /// on and stays a file-and-stderr-only rejection, same as before this event existed.
    /// </summary>
    /// <param name="Reason">
    /// #1530: the same reason string <c>CancelRequestFile.Reject</c> writes into the <c>.rejected</c>
    /// file body — previously this event was content-free, so the room-side arrest ledger
    /// (<c>Status.ArrestLedgerProjector</c>) had to fall back to the ephemeral file for the "why",
    /// which the next <c>cancel.request</c> write silently overwrites. Nullable and defaulted per
    /// this union's own replay rule (<see cref="ExecutionSucceeded.WorkspaceChanged"/>'s remarks): a
    /// line written before #1530 carries no reason and replays as null.
    /// </param>
    public sealed record CancellationRejected(ExecutionId ExecutionId, string? Reason = null) : FlowEvent;

    /// <summary>#734: see spec/baton.md §2's "Delivery state facts" for the producer and the no-action rule shared by all four of these cases — not restated per-case here.</summary>
    /// <param name="Branch">The room's own declared branch name, when the step also declared one.</param>
    public sealed record DeliveryPrOpened(int PullRequestNumber, string? Branch = null) : FlowEvent;

    /// <summary>#734: see <see cref="DeliveryPrOpened"/>'s remarks. Not terminal, unlike <see cref="DeliveryMerged"/> — a later push can flip this again.</summary>
    public sealed record DeliveryChecksGreen(int PullRequestNumber) : FlowEvent;

    /// <summary>#734: see <see cref="DeliveryChecksGreen"/>'s remarks — the failing counterpart.</summary>
    public sealed record DeliveryChecksRed(int PullRequestNumber) : FlowEvent;

    /// <summary>
    /// #734: see <see cref="DeliveryPrOpened"/>'s remarks. <paramref name="Merged"/> discriminates an
    /// actual merge from closed-unmerged, so the latter reuses this kind rather than adding a fifth.
    /// Defaults <c>false</c> deliberately — fail closed: a corrupted or truncated line that lost this
    /// field must not replay as the one outcome ("shipped") a reader would act differently on than the
    /// other ("abandoned").
    /// </summary>
    public sealed record DeliveryMerged(int PullRequestNumber, bool Merged = false) : FlowEvent;

    /// <summary>
    /// #1779: an <c>eventType</c> this binary does not recognize, produced by
    /// <see cref="Baton.Store.FlowEventLogJson.DeserializeLine"/> -- see that method's remarks for why
    /// (the deliberate exception to <see cref="Baton.Store.FlowEventLogJson"/>'s own "loud beats
    /// silent" rule) and for the mechanism. Filtered out by <see cref="Baton.Store.FlowEventLogReader"/>
    /// before anything else -- including <see cref="Projection.StateProjector"/> -- ever sees it; it
    /// never appears in a <see cref="LogEntry.FlowLogEntry.Event"/> exposed outside the reader.
    /// </summary>
    internal sealed record UnknownFlowEvent(string Kind, string RawJson) : FlowEvent;
}
