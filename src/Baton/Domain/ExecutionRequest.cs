namespace Baton.Domain;

/// <summary>
/// The immutable unit of execution, shared by identity across Flow's and Core's halves of the
/// Event Store. Immutable once emitted — never mutated, never reused; a retry is a
/// brand-new <see cref="ExecutionRequest"/> with a brand-new <see cref="ExecutionId"/>.
/// </summary>
/// <param name="StepId">
/// <c>null</c> for a step-less supplementary execution minted outside the DAG during a pause
/// — never associated with any <see cref="WorkflowStepDefinition"/>, and therefore
/// never perturbing any step's latest-attempt projection.
/// </param>
/// <param name="Timeout">
/// <c>null</c> for a <see cref="Mutation.WorkerBinding.NonProcess"/> dispatch — nothing runs as a
/// Core process, so nothing can time out.
/// </param>
/// <param name="UpstreamExecutionIds">
/// For each <see cref="StepId"/> this step depends on, exactly which of that dependency's
/// <see cref="ExecutionId"/>s this request's <paramref name="Inputs"/> were derived from. This is
/// what makes staleness derivable purely by reading the log.
/// </param>
/// <param name="LinkedFromExecutionId">
/// The prior execution this one continues (issue #1359's <c>baton resume</c>): the same step's own
/// <c>LatestExecutionId</c> at the moment the resume was recorded, resumed via the adapter's
/// vendor-session plumbing rather than dispatched fresh. <c>null</c> for every ordinary dispatch and
/// retry — a resume is the only request shape that ever sets this. Defaulted, not required, for the
/// same JSON-replay reason <see cref="FlowEvent.ExecutionFailed.Reason"/> documents: an older
/// <c>flow.jsonl</c> line written before this field existed must still replay.
/// </param>
/// <param name="SessionId">
/// The vendor session id this resume's bindings file recorded for <see cref="Worker"/> at dispatch
/// time (issue #1359 F6) — an opaque string Flow never interprets, carried purely so a LATER resume
/// of this same execution can check the operator's bindings file still names the session this one
/// actually continued, rather than trusting an unrecorded assertion. <c>null</c> for every ordinary
/// dispatch and retry, same as <paramref name="LinkedFromExecutionId"/>.
/// </param>
/// <param name="Adapter">
/// The vendor adapter bound to <see cref="Worker"/> at accept time (e.g. <c>"claude"</c>,
/// <c>"agy"</c>), recorded so a later failover rebind of <c>bindings.json</c> cannot retroactively
/// re-attribute this execution's already-recorded usage to whichever vendor is bound now (issue
/// #1567, quota-design S1 — full design in the 2026-09-01 proposal comment on #802). Before this
/// field existed, <see cref="Status.ExecutionUsageProjector"/> recovered the vendor by reading
/// <c>Adapter</c> out of the room's <em>current</em> <c>bindings.json</c> at read time — harmless
/// only because a room's adapter never changed mid-run. Failover changes that.
/// <para>
/// For an ordinary dispatch this is also the adapter that actually ran: the same resolved
/// <see cref="Mutation.WorkerBinding.Process"/> both spawns the process and supplies this value. On
/// the crash-recovery resubmit path (<c>MutationInterface</c>'s <c>toResubmit</c> loop, M10 Phase 3),
/// when a failover rebind between crash and resubmit causes the current binding to diverge from this
/// recorded value, Flow journals <see cref="FlowEvent.StepRebound"/> (issue #1583) so that
/// <see cref="Status.ExecutionUsageProjector"/> re-attributes the execution to the new binding.
/// </para>
/// <c>null</c> covers two cases, same defaulting rule as
/// <see cref="FlowEvent.ExecutionFailed.Reason"/>: every <c>flow.jsonl</c> line written before this
/// field existed — an older journal must still replay, and its attribution still falls back to the
/// bindings.json read this field exists to stop relying on — and any non-process
/// (<see cref="Mutation.WorkerBinding.NonProcess"/>) dispatch, which has no vendor adapter to name.
/// </param>
/// <param name="Model">
/// The model string this execution actually dispatched with, recorded alongside
/// <paramref name="Adapter"/> for the same reason. <c>null</c> covers three cases, not two: every
/// pre-existing journal line; a non-process (<see cref="Mutation.WorkerBinding.NonProcess"/>) dispatch,
/// which has no vendor model to name; and — reachable today, not merely hypothetical — a vendor swap
/// with no explicit <c>--model</c>, where <c>RoleDispatch.ToBinding</c> and
/// <c>RedispatchCommand</c> both deliberately drop the prior vendor's model string rather than hand
/// the new vendor a model name it may not recognize (#1082), so a real execution can run, and burn
/// real usage, on the new vendor's own default model while this field is still null.
/// </param>
/// <param name="HookCanaryArmed">
/// #1741: whether this dispatch's own resolved <see cref="Mutation.WorkerBinding.Process.Target"/>
/// carried a live <c>CountHookVerdicts</c> delegate at accept time — the same
/// <c>CoreDispatchTarget.CountHookVerdicts != null</c> fact <c>AgyWorkerAdapter.Resolve</c> already
/// decides (spec/baton.md §9's sole-hook-narrowing shape). Recorded so the crash-recovery replay
/// arms the first-verdict canary from THIS fact rather than re-resolving today's <c>bindings.json</c>,
/// which can legitimately refuse (the probe finds the hook dead now, or the entry moved off agy since
/// the crash) without that refusal meaning the execution itself never ran under sole-hook narrowing.
/// <see langword="true"/> arms the canary; <see langword="false"/> means this dispatch resolved and was
/// NOT armed (a claude binding, or a fully-granted agy one); <see langword="null"/> means a line
/// written before this field existed, where the replay keeps its pre-#1741 behaviour (spec/baton.md
/// §9 has the full rule).
/// </param>
/// <param name="HookVerdictLedgerFileName">
/// The file name (not a path) the hook's verdict ledger was written under inside this execution's own
/// output directory, recorded alongside <paramref name="HookCanaryArmed"/> so the replay can count
/// verdicts directly from disk without resolving today's binding to obtain the same delegate. Non-null
/// only when <paramref name="HookCanaryArmed"/> is <see langword="true"/>. Adapter Isolation
/// (docs/agents/developing-baton.md Architecture Rule 2): this is an opaque string Flow never interprets or defaults, only
/// carries — the vendor adapter is what names it.
/// </param>
public sealed record ExecutionRequest(
    ExecutionId ExecutionId,
    WorkflowId WorkflowId,
    StepId? StepId,
    string Worker,
    IReadOnlyList<string> Inputs,
    IReadOnlyList<string> Outputs,
    TimeSpan? Timeout,
    IReadOnlyList<EnvironmentVariable> Environment,
    IReadOnlyDictionary<StepId, ExecutionId> UpstreamExecutionIds,
    GrantAuditMode? GrantAuditMode = null,
    ExecutionId? LinkedFromExecutionId = null,
    string? SessionId = null,
    string? Adapter = null,
    string? Model = null,
    bool? HookCanaryArmed = null,
    string? HookVerdictLedgerFileName = null);
