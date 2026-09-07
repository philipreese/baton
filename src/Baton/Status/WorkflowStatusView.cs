using System.Text.Json.Serialization;
using Baton.Artifacts;
using Baton.Domain;
using Baton.Outcomes;
using Baton.Projection;
using Baton.Scheduling;

namespace Baton.Status;

/// <summary>
/// One step's machine-readable state, per <c>baton status --json</c>'s shape (#1356): a bare
/// <see cref="StepStatus"/> token, never the human prose <c>StatusCommand.FormatStepStatus</c> prints
/// (a parked/liveness-annotated sentence a machine consumer would have to parse back apart).
/// <see cref="Liveness"/> (#1375, spec/baton.md §3) is the one exception carried as a separate,
/// structured field rather than folded into <see cref="State"/> — see its own remarks.
/// </summary>
public sealed record WorkflowStatusStepView(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("execution")] string? Execution,
    // #1359: the execution `baton resume` continued, when Execution is a resume's own new attempt —
    // null for every ordinary dispatch/retry. Lets a status consumer render both executions of a
    // resumed step without a second lookup.
    [property: JsonPropertyName("linkedFrom")] string? LinkedFrom = null,
    // #1360: Execution's own usage -- absent (not present as a whole) when that execution has no
    // recorded start/exit pair to derive wall-clock from (still running, or Flow crashed before Core
    // recorded either lifecycle event). See ExecutionUsageProjector.
    [property: JsonPropertyName("usage")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ExecutionUsageView? Usage = null,
    // #1360: LinkedFrom's own usage, kept separate from Usage rather than merged -- a resumed step's
    // two executions are two distinct cost entries, not one to be added or overwritten.
    [property: JsonPropertyName("linkedFromUsage")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ExecutionUsageView? LinkedFromUsage = null,
    // #1375/#1513: the SAME EngineLivenessProbe the human `baton status` rendering consults
    // (StatusCommand.FormatStepStatus), never a second probe -- present for a Running step (except
    // one frozen by a room-level sentinel, whose probe is dropped rather than frozen into the file:
    // spec/baton.md §13, the one exception §3 names), and (#1513) for a Failed step still carrying
    // a RetryNotBefore, FormatStepStatus's own gate (why every other step claims nothing:
    // spec/baton.md §3). "alive" | "dead" | "unknown", lower-cased
    // from EngineLivenessStatus; omitted, never null, for every ungated step so the field's mere
    // presence already answers "does liveness apply here".
    [property: JsonPropertyName("liveness")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Liveness = null,
    // #1509/#1522: a lifetime execution ordinal derived from StateProjector's per-step execution
    // counter (incremented on every FlowEvent.ExecutionRequestAccepted), not from
    // ConsecutiveFailureCount. Monotonically increases across retries and survives
    // DecisionType.RetryWithRevision and FailureClassification.ExhaustedUntil without undercounting.
    // Present for Running and Failed steps that have executed; omitted for Pending steps that have
    // not had an execution accepted or steps whose status is neither Running nor Failed.
    [property: JsonPropertyName("attempt")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? Attempt = null,
    // #1509/#1522 finding 3: the step definition's RetryPolicy.MaxAttempts -- the PER-ROUND retry
    // budget RetryEngine.MayRetry gates on via ConsecutiveFailureCount, unrelated to Attempt's
    // lifetime execution ordinal. NOT a denominator for Attempt: RetryWithRevision resets
    // ConsecutiveFailureCount but not the lifetime count, so a revised step's Attempt can exceed
    // MaxAttempts (e.g. "attempt 4" against a MaxAttempts of 3), and an ExhaustedUntil outcome
    // consumes no retry budget (decision 0026) while still incrementing Attempt. A consumer must
    // render the two fields separately, never as "attempt N/M". Present only when Attempt is.
    [property: JsonPropertyName("maxAttempts")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? MaxAttempts = null,
    // #1510: StepState.LatestFailureClassification's enum member name verbatim (Retryable /
    // Permanent / ExhaustedUntil / ToolDenied) -- the engine's own taxonomy, never a new one.
    // Present only for a Failed step that recorded a classification; a Failed step whose worker
    // reported none stays omitted rather than defaulting to "Retryable", even though that is how
    // RetryEngine itself treats an absent classification -- the field states what was recorded, not
    // what it is treated as.
    [property: JsonPropertyName("failureKind")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? FailureKind = null,
    // #1510: RetryEngine.MayRetry's own verdict for this step, never a second taxonomy. Present
    // only alongside a Failed step; a step that hasn't failed has nothing to be "eligible to
    // retry".
    [property: JsonPropertyName("retryEligible")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? RetryEligible = null,
    // #1551: StepState.RetryNotBefore verbatim (ISO-8601, UTC) -- the instant an ExhaustedUntil park
    // auto-resumes, same value FormatVendorQuotaParkNotice/StatusCommand.FormatParkedStatus already
    // render as "resumes at HH:mm". Usually the vendor-reported reset instant, but not always: #1183
    // caps a far-future instant to MaxExhaustionParkHorizon and paces an already-past one to
    // PastResetInstantRetryFloor before GetRetryObligations ever records it here, so a degenerate
    // vendor value shows the engine's capped/floored instant, not the raw one. Gated on
    // FailureKind == "ExhaustedUntil" specifically (not any Failed step with a pending retry): an
    // ordinary Retryable backoff has a RetryNotBefore too, but this field answers "when does the
    // vendor-quota park lift", not "when is the next attempt". Present only when the engine
    // actually recorded a reset instant -- an un-obligated ExhaustedUntil park (RetryNotBefore
    // null, StatusCommand's "reset unknown") stays absent rather than fabricating one. Unchanged,
    // not re-derived, once liveness confirms the scheduling engine dead (#1513 Stalled) -- the field
    // still carries the recorded instant, only its honesty on render changes: that is the consuming
    // chip's job.
    [property: JsonPropertyName("exhaustedUntil")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ExhaustedUntil = null,
    // #1594: StepState.LatestCapturedResponseFile (OutputMaterializer.CapturedResponse explains the
    // ruling and what this pairs up with UnsatisfiedOutputs to mean), gated the same way FailureKind
    // is: present only for a currently-Failed step. This is the field a conductor reads instead of
    // opening the execution directory (review F1).
    [property: JsonPropertyName("capturedResponseFile")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? CapturedResponseFile = null,
    // StepState.LatestUnsatisfiedOutputNames, carried the same hop.
    [property: JsonPropertyName("unsatisfiedOutputs")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? UnsatisfiedOutputs = null,
    // F1/F10 (#1593 review): StepState.IndeterminateProducer's enum member name verbatim, gated the
    // same way CapturedResponseFile is above -- present only for a currently-Failed step. A consumer
    // (RedispatchCommand's Indeterminate-parent remedy) needs this to tell a ContractFailure parent
    // (which `baton resolve --reject --reason` can still resolve) from a VerifyFailed/Arrested one
    // (which `baton resolve --close --reason` resolves instead, #1622 (d)) without guessing from
    // CapturedResponseFile alone,
    // which both VerifyFailed/Arrested AND a not-yet-indeterminate step share as null.
    [property: JsonPropertyName("indeterminateProducer")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? IndeterminateProducerKind = null,
    // #1701: StepState.IndeterminateVerifyTail verbatim -- see that field's own remarks (FlowState.cs)
    // for why it exists and what it carries. Gated the same way IndeterminateProducerKind is above
    // (present only for a currently-Failed step). In practice only non-null when
    // IndeterminateProducerKind is VerifyFailed -- ApplyIndeterminate (StateProjector.cs) writes null
    // for every other producer -- but that invariant is enforced there, not by this gate; this field
    // carries whatever StepState.IndeterminateVerifyTail records.
    [property: JsonPropertyName("verifyTail")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? VerifyTail = null,
    // #1622 (c)/(d): mirrors StepState.ResolvedByConductor. Present per-step (as well as the
    // room-level WorkflowStatusView.Rejected/ResolvedBy below) so a multi-step room's caller can tell
    // WHICH step was resolved.
    [property: JsonPropertyName("resolvedByConductor")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    bool ResolvedByConductor = false,
    // #1622/#1390: mirrors StepState.WorkspaceChanged.
    [property: JsonPropertyName("workspaceChanged")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? WorkspaceChanged = null,
    // #1622/#1390: mirrors StepState.Hollow. Present under the identical gate as WorkspaceChanged
    // above, never without it.
    [property: JsonPropertyName("hollow")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? Hollow = null,
    // #1622/#1390: StepState.HollowReason verbatim -- non-null only when Hollow is true.
    [property: JsonPropertyName("hollowReason")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? HollowReason = null,
    // #1702: StepState.VerifyNotRunReason's mere presence, not its text -- the one machine-readable
    // token a status/glass consumer branches on ("this step ran unverified"), the same "bare token,
    // never prose" shape State/liveness/failureKind already keep. Always "not-run" when present; no
    // other value exists yet (an ordinary verify pass/fail carries no field here at all -- ordinary
    // Succeeded/Failed already say everything a consumer needs). Omitted, never null, for every step
    // whose latest attempt did not hit the pre-flight "not runnable" check.
    [property: JsonPropertyName("verify")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Verify = null,
    // #1702: the pre-flight verdict text (e.g. "task absent: gates-quiet") -- present only alongside
    // Verify above.
    [property: JsonPropertyName("verifyReason")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? VerifyReason = null);

/// <summary>
/// The one JSON object <c>baton status --json</c> writes to stdout (#1356's machine completion
/// contract): <c>{state, steps:[{id, state, execution, linkedFrom, usage, linkedFromUsage, liveness}],
/// outputs:[...], error, try, rejected}</c> — the canonical statement of this shape, full schema at
/// spec/baton.md §3 (see also <c>docs/agents/invoking-baton.md</c>'s <c>record-once-ok</c> marker,
/// which points here).
/// <c>linkedFrom</c> (#1359) is additive to #1356's shape, same as <c>Try</c> below — see
/// <see cref="WorkflowStatusStepView.LinkedFrom"/>. <c>usage</c>/<c>linkedFromUsage</c> (#1360) are
/// likewise additive — see <see cref="WorkflowStatusStepView.Usage"/>. Also what the terminal sentinel
/// (<c>terminal.json</c>, <see cref="TerminalSentinelWriter"/>) serializes, so a file-watching agent
/// and a polling <c>status --json</c> caller read the identical shape.
/// <c>Try</c> (#1382 F3) is additive to #1356's shape: the corrected-invocation text an
/// <see cref="Baton.BatonFlowException.TryInvocation"/>-carrying refusal set, kept as its own field
/// rather than appended into <see cref="Error"/> so a consumer can tell diagnosis from remedy apart.
/// Only ever populated on the pre-ledger sentinel path (<see cref="TerminalSentinelWriter.WriteValidationRefusedAsync"/>) —
/// a normal ledger projection has no exception to carry one.
/// <see cref="Rejected"/> (#1377) and <see cref="WorkflowStatusStepView.Liveness"/> (#1375) are the
/// two most recently added additive fields — see each property's own remarks for what they carry and
/// why.
/// </summary>
public sealed record WorkflowStatusView(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("steps")] IReadOnlyList<WorkflowStatusStepView> Steps,
    [property: JsonPropertyName("outputs")] IReadOnlyList<string> Outputs,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("try")] string? Try = null,
    // #1377, widened by #1622 (c)/(d): see spec/baton.md §3's `rejected` entry for the full branching
    // recipe (which two verbs settle it, and why no `reason` field is invented for the
    // DecisionType.Reject half). The `baton resolve` half's reason is instead folded into `Error`
    // (see Projection.StateProjector.BuildConductorResolvedReason) and named by `ResolvedBy` below.
    [property: JsonPropertyName("rejected")] bool Rejected = false,
    // #1622 (c)/(d): see spec/baton.md §3's `resolvedBy` entry. Non-null for either `baton resolve`
    // verb — it is the wider fact, so it is set on `--close` runs where `Rejected` stays false.
    [property: JsonPropertyName("resolvedBy")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ResolvedBy = null,
    // #1157: when this run ended (ISO-8601, UTC) -- Projection.TerminalInstantResolver's answer off
    // the journal's own writer stamps, never a file's mtime. What it means, and every case it is
    // absent in: spec/baton.md §3.
    [property: JsonPropertyName("terminalAt")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? TerminalAt = null,
    // #1530: the room-side arrest ledger -- absent (never an empty array) for a room with no
    // cancel.request in its history, so a consumer can test presence rather than length.
    [property: JsonPropertyName("arrests")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<ArrestLedgerEntryView>? Arrests = null,
    // #1916 fix round 2: the text-mode reader already has `Arrests: ledger unavailable (<reason>)`
    // (StatusCommand.PrintArrestLedger) to tell "the read failed" apart from "no cancel.request in
    // this room's history" -- both render Arrests absent here, so a --json consumer had no way to
    // make the same distinction. Present only on the read-failure path; a clean empty ledger keeps
    // this null.
    [property: JsonPropertyName("arrestLedgerUnavailableReason")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ArrestLedgerUnavailableReason = null,
    // The wire projection of the room's Baton.Runway.RunwayAdmission (#1896) -- ONE PER VENDOR this
    // dispatch gated (#1932 review), never whichever binding came first, since a composed template
    // decides each of its vendors separately. Absent (never an empty array) for a room carrying none,
    // the same presence-not-length rule Arrests states above. NOT populated by WorkflowStatusProjector:
    // it comes off bindings.json, which this layer has no parser for, so `baton status` sets it with a
    // `with` expression after projecting and every other producer (TerminalSentinelWriter's frozen
    // terminal.json included) leaves it null.
    [property: JsonPropertyName("runway")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<Baton.Runway.RunwayAdmissionView>? Runway = null);

/// <summary>
/// #1530: the wire shape for one <see cref="ArrestLedgerEntry"/> — plain strings throughout, the
/// same convention every other identifier on <see cref="WorkflowStatusStepView"/> already follows
/// (<c>Execution</c> is a bare <c>string?</c>, never a raw <see cref="ExecutionId"/>), since this
/// view is serialized under <see cref="System.Text.Json.JsonSerializerOptions"/> defaults
/// (<c>StatusCommand</c>'s plain <c>JsonSerializer.Serialize(view)</c> call), not
/// <see cref="Store.FlowEventLogJson.Options"/>.
/// </summary>
public sealed record ArrestLedgerEntryView(
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("executionId")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ExecutionId,
    // Absent while the request is still pending settlement -- see ArrestLedgerEntry.Outcome's own remarks.
    [property: JsonPropertyName("outcome")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Outcome,
    [property: JsonPropertyName("requestedBy")] string RequestedBy,
    [property: JsonPropertyName("reason")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Reason,
    [property: JsonPropertyName("requestedAt")] string RequestedAt,
    [property: JsonPropertyName("resolvedAt")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ResolvedAt)
{
    public static ArrestLedgerEntryView From(ArrestLedgerEntry entry) => new(
        entry.Target,
        entry.ExecutionId?.Value,
        entry.Outcome?.ToString().ToLowerInvariant(),
        entry.RequestedBy,
        entry.Reason,
        entry.RequestedAtUtc.ToString("O"),
        entry.ResolvedAtUtc?.ToString("O"));
}

/// <summary>
/// Builds <see cref="WorkflowStatusView"/> from the same <see cref="FlowState"/>
/// <c>StatusCommand.PrintState</c>/<c>FlowStateReporter.Report</c> already render (one derivation,
/// two — now three, counting the terminal sentinel — renderings; #1356 requires never forking the
/// projection itself). Never re-reads <c>flow.jsonl</c> or <c>snapshot.json</c> on its own: callers
/// pass in the already-projected <see cref="FlowState"/>, and (#1360) the raw <see cref="LogEntry"/>
/// list a caller already read for that same projection, when per-execution usage is wanted.
/// </summary>
public static class WorkflowStatusProjector
{
    /// <param name="entries">
    /// The same ledger entries the caller already read to produce <paramref name="state"/> (#1360) —
    /// source data for <see cref="ExecutionUsageProjector.BuildByExecutionId"/>. Omitted (or empty)
    /// yields a view with no <c>usage</c> on any step, never a fabricated one; a caller that has no
    /// use for usage data (or has not read the ledger for another reason) is not forced to.
    /// </param>
    /// <param name="adapters">
    /// Registered adapters (#1360) an execution's own dispatched worker is attributed to — primarily
    /// via the request's own recorded <c>Adapter</c> (#1567), falling back to
    /// <paramref name="roomDirectoryPath"/>'s <c>bindings.json</c> only for a journal line that
    /// predates it — see <see cref="ExecutionUsageProjector"/>'s remarks for how attribution works.
    /// Omitted (or null) resolves against <see cref="StandardWorkerUsageParsers.Default"/> instead of
    /// yielding no usage (#1590) — an explicitly-passed, adapter-less dictionary still yields none.
    /// </param>
    public static WorkflowStatusView Project(
        FlowState state,
        WorkflowDefinitionSnapshot snapshot,
        string roomDirectoryPath,
        IReadOnlyList<LogEntry>? entries = null,
        IReadOnlyDictionary<string, IWorkerUsageParser>? adapters = null,
        IReadOnlyList<ArrestLedgerEntry>? arrestLedger = null,
        string? arrestLedgerUnavailableReason = null) =>
        Project<IWorkerUsageParser>(state, snapshot, roomDirectoryPath, entries, adapters, arrestLedger, arrestLedgerUnavailableReason);

    public static WorkflowStatusView Project<TParser>(
        FlowState state,
        WorkflowDefinitionSnapshot snapshot,
        string roomDirectoryPath,
        IReadOnlyList<LogEntry>? entries = null,
        IReadOnlyDictionary<string, TParser>? adapters = null,
        IReadOnlyList<ArrestLedgerEntry>? arrestLedger = null,
        string? arrestLedgerUnavailableReason = null)
        where TParser : IWorkerUsageParser
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);

        var stepDefByStepId = snapshot.Steps.ToDictionary(step => step.StepId);
        var artifactsRootPath = Path.Combine(roomDirectoryPath, ArtifactManager.ArtifactsDirectoryName);

        var usageByExecutionId = ExecutionUsageProjector.BuildByExecutionId(
            entries ?? [], artifactsRootPath, adapters, roomDirectoryPath);

        // #1375: the same (pid, engine-start-time) pair StatusCommand.FormatStepStatus reads off
        // ExecutionRequestAccepted to drive EngineLivenessProbe -- built once here rather than
        // re-scanning `entries` per Running step.
        var engineIdentityByExecutionId = new Dictionary<string, (int? Pid, DateTimeOffset? StartTime)>(StringComparer.Ordinal);
        foreach (var entry in entries ?? [])
        {
            if (entry is LogEntry.FlowLogEntry { Event: FlowEvent.ExecutionRequestAccepted accepted })
            {
                engineIdentityByExecutionId[accepted.Request.ExecutionId.Value] = (accepted.EnginePid, accepted.EngineStartTime);
            }
            // #1577: FlowEvent.StepRetryScheduled.EnginePid's own remarks have why a revival renewal
            // lands here instead of a fresh ExecutionRequestAccepted -- read in log order same as
            // above, so the newest stamp for a given execution wins whichever kind carries it.
            else if (entry is LogEntry.FlowLogEntry { Event: FlowEvent.StepRetryScheduled { EnginePid: not null } retryScheduled })
            {
                engineIdentityByExecutionId[retryScheduled.ForExecutionId.Value] = (retryScheduled.EnginePid, retryScheduled.EngineStartTime);
            }
        }

        var steps = new List<WorkflowStatusStepView>(state.Steps.Count);
        var outputs = new List<string>();
        string? firstFailureReason = null;
        var anyRejected = false;
        string? resolvedBy = null;

        foreach (var step in state.Steps)
        {
            var usage = step.LatestExecutionId is { } latest && usageByExecutionId.TryGetValue(latest.Value, out var latestUsage)
                ? latestUsage
                : null;
            var linkedFromUsage = step.LinkedFromExecutionId is { } linkedFrom && usageByExecutionId.TryGetValue(linkedFrom.Value, out var linkedUsage)
                ? linkedUsage
                : null;

            // Probe steps this projection calls Running, PLUS a Failed step still carrying a
            // RetryNotBefore (#1513): that step's next attempt depends entirely on the same pump
            // process staying alive through its `Task.Delay` wait (MutationInterface's scheduling
            // loop) -- there is no other reaper. Its LatestExecutionId's ExecutionRequestAccepted
            // still names the engine that scheduled the wait, so the identical probe applies; a step
            // whose retry budget is exhausted (Failed, RetryNotBefore null) has no pending wait to
            // question and stays ungated, same as before. Unlike FormatStepStatus, a step with no
            // recorded ExecutionRequestAccepted identity still gets probed -- Probe(null, null) itself
            // already reads as EngineLivenessStatus.Unknown, so this always renders a value for a
            // gated step rather than silently omitting the field on a miss (review finding: the two
            // renderings must never disagree about WHETHER a verdict exists, only about its OS-level
            // result).
            string? liveness = null;
            var probedExecution = step.Status == StepStatus.Running
                ? step.LatestExecutionId
                : step.Status == StepStatus.Failed && step.RetryNotBefore is not null
                    ? step.LatestExecutionId
                    : null;
            if (probedExecution is { } executionToProbe)
            {
                var identity = engineIdentityByExecutionId.TryGetValue(executionToProbe.Value, out var found)
                    ? found
                    : (Pid: (int?)null, StartTime: (DateTimeOffset?)null);
                var probeResult = EngineLivenessProbe.Probe(identity.Pid, identity.StartTime);
                liveness = probeResult.Status switch
                {
                    EngineLivenessStatus.Alive => "alive",
                    EngineLivenessStatus.Dead => "dead",
                    _ => "unknown",
                };
            }

            // #1509/#1510/#1522: derived from StepState.ExecutionCount (lifetime ExecutionRequestAccepted
            // count), independent of ConsecutiveFailureCount reset semantics. Present for Running and
            // Failed steps that have executed; omitted for Pending or unexecuted steps.
            int? attempt = step switch
            {
                { Status: StepStatus.Running or StepStatus.Failed, ExecutionCount: > 0 } => step.ExecutionCount,
                _ => null,
            };
            int? maxAttempts = attempt is not null && stepDefByStepId.TryGetValue(step.StepId, out var attemptStepDef)
                ? attemptStepDef.RetryPolicy.MaxAttempts
                : null;
            string? failureKind = step.Status == StepStatus.Failed && step.LatestFailureClassification is { } classification
                ? classification.ToString()
                : null;
            bool? retryEligible = step.Status == StepStatus.Failed && stepDefByStepId.TryGetValue(step.StepId, out var retryStepDef)
                ? RetryEngine.MayRetry(step, retryStepDef.RetryPolicy)
                : null;
            // #1551: the reset instant, gated to an actual ExhaustedUntil park with a recorded
            // obligation -- see WorkflowStatusStepView.ExhaustedUntil's remarks for why this is
            // narrower than "any Failed step with a RetryNotBefore".
            string? exhaustedUntil = step.Status == StepStatus.Failed
                && step.LatestFailureClassification == FailureClassification.ExhaustedUntil
                && step.RetryNotBefore is { } resetInstant
                ? resetInstant.ToUniversalTime().ToString("O")
                : null;

            // #1594: gated the same way failureKind is above -- a step's raw StepState fields can
            // carry a stale value from a prior failed attempt (e.g. a later Cancelled attempt never
            // clears them), so only a currently-Failed step is allowed to surface a capture.
            string? capturedResponseFile = step.Status == StepStatus.Failed ? step.LatestCapturedResponseFile : null;
            IReadOnlyList<string>? unsatisfiedOutputs = step.Status == StepStatus.Failed ? step.LatestUnsatisfiedOutputNames : null;
            string? indeterminateProducerKind = step.Status == StepStatus.Failed ? step.IndeterminateProducer?.ToString() : null;
            string? verifyTail = step.Status == StepStatus.Failed ? step.IndeterminateVerifyTail : null;

            // #1702: gated on the reason being present at all, not on Status -- unlike failureKind/
            // capturedResponseFile above, a not-run verify step is ordinarily Succeeded, never Failed.
            string? verify = step.VerifyNotRunReason is not null ? "not-run" : null;
            string? verifyReason = step.VerifyNotRunReason;

            steps.Add(new WorkflowStatusStepView(
                step.StepId.Value, step.Status.ToString(), step.LatestExecutionId?.Value, step.LinkedFromExecutionId?.Value,
                usage, linkedFromUsage, liveness, attempt, maxAttempts, failureKind, retryEligible,
                exhaustedUntil, capturedResponseFile, unsatisfiedOutputs, indeterminateProducerKind, verifyTail,
                step.ResolvedByConductor, step.WorkspaceChanged, step.Hollow, step.HollowReason,
                verify, verifyReason));

            if (firstFailureReason is null && step.Status is StepStatus.Failed or StepStatus.Rejected
                && !string.IsNullOrWhiteSpace(step.LatestFailureReason))
            {
                firstFailureReason = step.LatestFailureReason;
            }

            // F11 (#1720 review, conductor ruling): `rejected` is the human "no" — a decide-time
            // Rejected step or a `baton resolve --reject`. A `--close` is an administrative
            // settlement whose own remedy text says the work already landed, so it sets `resolvedBy`
            // WITHOUT setting `rejected`; a harness branching on `rejected` to mean "a person refused
            // this work" would otherwise read a closed lane as refused. spec/baton.md §3.
            if (step.Status == StepStatus.Rejected || step.ConductorRejected)
            {
                anyRejected = true;
            }

            if (step.ResolvedByConductor)
            {
                resolvedBy = "conductor";
            }

            // #740's rule via StepOutputResolver, the one place it is implemented (#1374 F5) — this
            // must never drift from FlowStateReporter's own printed paths for the same room.
            if (stepDefByStepId.TryGetValue(step.StepId, out var stepDef))
            {
                outputs.AddRange(StepOutputResolver.Resolve(step, stepDef, artifactsRootPath).Select(o => o.Path));
            }
        }

        // #1157: only a terminal run has an instant to report, and only a caller that handed in the
        // entries can source one -- a `usage`-less caller (entries omitted) gets the field omitted
        // too rather than a second read of flow.jsonl this projector is documented never to do.
        // The prefix replays TerminalInstantResolver does are pure, so that documented no-I/O
        // property is unaffected; the non-terminal path pays nothing for them.
        string? terminalAt = null;
        if (state.Status == WorkflowStatus.Terminal && entries is { Count: > 0 })
        {
            terminalAt = TerminalInstantResolver.Resolve(entries, snapshot).AtUtc?.ToString("O");
        }

        var arrestViews = arrestLedger is { Count: > 0 }
            ? arrestLedger.Select(ArrestLedgerEntryView.From).ToList()
            : null;

        return new WorkflowStatusView(
            WorkflowOutcome.Describe(state), steps, outputs, firstFailureReason, Rejected: anyRejected,
            ResolvedBy: resolvedBy, TerminalAt: terminalAt, Arrests: arrestViews,
            ArrestLedgerUnavailableReason: arrestLedgerUnavailableReason);
    }

    /// <summary>
    /// Extracts UTC timestamps for each execution from log entries (Flow and Core lifecycle events),
    /// with latest event winning per execution ID.
    /// </summary>
    public static Dictionary<string, DateTime> ExtractEventTimestamps(IReadOnlyList<LogEntry> entries)
    {
        var timestamps = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            string? execId = null;
            DateTime? timestamp = null;

            switch (entry)
            {
                case LogEntry.FlowLogEntry flowEntry:
                    timestamp = flowEntry.WriterUtcTimestamp;
                    execId = flowEntry.Event switch
                    {
                        FlowEvent.ExecutionRequestAccepted accepted => accepted.Request.ExecutionId.Value,
                        FlowEvent.ExecutionSucceeded succeeded => succeeded.ExecutionId.Value,
                        FlowEvent.ExecutionFailed failed => failed.ExecutionId.Value,
                        FlowEvent.ExecutionCancelled cancelled => cancelled.ExecutionId.Value,
                        // #1608 review finding 8 / #1623: same terminal-event timestamp as
                        // ExecutionFailed above — without these arms a settle that ended in an
                        // Indeterminate (whichever of its three producers) fell back to
                        // CoreEvent.ExecutionExited (a few ms earlier), not a staleness bug but an
                        // unnecessary inconsistency with ExecutionSucceeded/ExecutionFailed above. The
                        // switch is still not exhaustive over every FlowEvent even with these arms:
                        // CaptureResolved (#1608 review finding 7) and #1623/#1702's own diagnostic-only
                        // VerifyStarted/VerifyPassed/VerifyNotRun all still fall to `_ => null` below.
                        FlowEvent.ExecutionIndeterminate indeterminate => indeterminate.ExecutionId.Value,
                        FlowEvent.ExecutionArrested arrested => arrested.ExecutionId.Value,
                        FlowEvent.VerifyFailed verifyFailed => verifyFailed.ExecutionId.Value,
                        _ => null,
                    };
                    break;
                case LogEntry.CoreLogEntry coreEntry:
                    timestamp = coreEntry.WriterUtcTimestamp;
                    execId = coreEntry.Event switch
                    {
                        CoreEvent.ExecutionStarted started => started.ExecutionId.Value,
                        CoreEvent.ExecutionExited exited => exited.ExecutionId.Value,
                        _ => null,
                    };
                    break;
            }

            if (execId is not null && timestamp.HasValue)
            {
                timestamps[execId] = timestamp.Value;
            }
        }

        return timestamps;
    }
}
