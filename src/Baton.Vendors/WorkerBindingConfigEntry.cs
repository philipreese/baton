using Baton.Domain;
using Baton.Runway;

namespace Baton.Vendors;

/// <summary>
/// One worker role's entry in a worker-binding config file (M11 Phase 1's open question: "where
/// worker-binding config lives"). A workflow names abstract worker roles (e.g. <c>"architect"</c>);
/// this is the run-time sidecar mapping — worker name → {adapter, model, permission scope, prompt
/// template} — deliberately kept out of the frozen <see cref="WorkflowDefinitionSnapshot"/>, the
/// same way M7 Phase 7 kept a worker's <c>Timeout</c> off the step.
/// </summary>
/// <param name="Adapter">
/// The registered adapter name (e.g. <c>"claude"</c>) this entry resolves through — looked up in
/// the <see cref="IWorkerAdapter"/> registry <see cref="WorkerBindingResolver.Resolve"/> is given,
/// never hardcoded to a vendor here.
/// </param>
/// <param name="Contract">This worker role's <see cref="WorkerContract"/> — required inputs, declared outputs, optional metadata.</param>
/// <param name="PromptTemplate">Forwarded verbatim into the resolved <see cref="WorkerInvocation"/>.</param>
/// <param name="Timeout">The per-execution timeout carried on the resolved <c>Baton.Mutation.WorkerBinding.Process</c>.</param>
/// <param name="Model">Forwarded verbatim into the resolved <see cref="WorkerInvocation"/>.</param>
/// <param name="PermissionScope">Forwarded verbatim into the resolved <see cref="WorkerInvocation"/>.</param>
/// <param name="PermissionGrant">Forwarded verbatim into the resolved <see cref="WorkerInvocation"/> — see its docs for precedence over <paramref name="PermissionScope"/>.</param>
/// <param name="WorkingDirectory">
/// Where this worker role's process should run (M23 Phase 3, #272) — a rooted absolute path (used
/// directly, but not portable to a machine where that path doesn't exist) or a bare name, looked up
/// in the local per-machine profile mapping (<see cref="BatonProfileStore"/>) by
/// <see cref="WorkerBindingResolver.Resolve"/> — the same key resolves to a different real directory
/// on every machine that has its own copy of that mapping, keeping this bindings file itself
/// portable even though the project directory it points at is not. Null keeps the prior default (no
/// explicit cwd).
/// </param>
/// <param name="Effort">
/// Forwarded into the resolved <see cref="WorkerInvocation"/> unchanged as a string — but no longer
/// verbatim in effect, since #1318 widened this field's domain to also accept 0023's canonical effort
/// word; see <see cref="WorkerInvocation.Effort"/>'s own doc for where that word is resolved.
/// </param>
/// <param name="Worktree">
/// When set, the worker's workspace is a git worktree the engine provisions before dispatch and tears
/// down on Terminal (#669), rather than a pre-existing <paramref name="WorkingDirectory"/>. The two are
/// mutually exclusive — a worker runs in exactly one place — and setting both is refused before the
/// pump starts. Null (the default) keeps the referential-directory behaviour above.
/// </param>
/// <param name="IsWorktree">
/// <see cref="WorktreeWorkspaces.Provision"/>'s stamp that <paramref name="WorkingDirectory"/> now
/// points at a worktree it provisioned (#901) — NOT an author-facing setting; a hand-authored true
/// claims isolation that does not exist, and the post-run audit then fails closed against the
/// shared directory's unrelated dirt (loud, not silent — but still a lie the run pays for).
/// </param>
/// <param name="Label">
/// The operator-supplied <c>--label</c> (#1499) — full contract, including why it lives here rather
/// than a new file, is spec/baton.md §2/§6. Sanitized once at parse time
/// (<c>Baton.Cli.DispatchOptionsParser.SanitizeLabel</c>). Null when never supplied.
/// </param>
/// <param name="VerifyPixiTask">
/// #1623: <see cref="WorkerRole.VerifyPixiTask"/>, carried onto the resolved
/// <c>Baton.Mutation.WorkerBinding.Process</c> unchanged — the engine, never the worker, runs it. Since
/// #1702 this is only the lowest-precedence input to <c>Baton.Mutation.VerifyCommandResolver.Resolve</c>,
/// not the sole source of a verify step.
/// </param>
/// <param name="VerifyCommandOverride">
/// #1702: the <c>--verify</c> escape hatch (<see cref="RoleDispatch.ToBinding"/>'s
/// <c>verifyCommandOverride</c>), mirroring <paramref name="TokenBudget"/>'s override pattern —
/// highest precedence in <c>Baton.Mutation.VerifyCommandResolver.Resolve</c>. Null defers to the
/// workspace's own <c>.baton/verify</c> declaration, then <paramref name="VerifyPixiTask"/>.
/// </param>
/// <param name="TokenBudget">
/// #1623: <see cref="WorkerRole.TokenBudget"/>, or the <c>--token-budget</c> override
/// (<see cref="RoleDispatch.ToBinding"/>'s <c>tokenBudgetOverride</c>) when one was supplied.
/// </param>
/// <param name="MaxToolSteps">
/// #1682: <see cref="WorkerRole.MaxToolSteps"/>, or the <c>--max-tool-steps</c> override (#1686 review
/// F11, <see cref="RoleDispatch.ToBinding"/>'s <c>maxToolStepsOverride</c>) when one was supplied —
/// same axis shape as <paramref name="TokenBudget"/>'s <c>--token-budget</c>.
/// </param>
/// <param name="BilledRateLimit">
/// #1691: <see cref="WorkerRole.BilledRateLimit"/>, or the <c>--billed-rate-limit</c> override
/// (<see cref="RoleDispatch.ToBinding"/>'s <c>billedRateLimitOverride</c>) when one was supplied —
/// same axis shape as <paramref name="TokenBudget"/>'s <c>--token-budget</c>. In practice the override
/// is the ONLY source: no role declares a default (spec/baton.md §3).
/// </param>
/// <param name="Workstream">
/// The operator-supplied <c>--workstream</c> slug (#1619, rung 1 of #1614's ruling) — a grouping key,
/// not a title: unlike <paramref name="Label"/> it IS path-written, as the directory name of a
/// <c>~/.baton/by-workstream/&lt;slug&gt;/</c> junction the CLI creates at dispatch time
/// (<c>Baton.Cli.WorkstreamJunctionLinker</c>) — see spec/baton.md §2/§6. Sanitized and slug-validated
/// once at parse time (<c>Baton.Cli.DispatchOptionsParser.SanitizeWorkstream</c>). Null when never
/// supplied.
/// </param>
/// <param name="WorktreeBaseSha">
/// N2 (#1664 re-review): the commit <paramref name="Worktree"/>'s <see cref="WorktreeWorkspace.Ref"/>
/// resolved to at provisioning time (<see cref="Workspaces.WorktreeProvisioner.ResolveBaseCommit"/>),
/// stamped by <see cref="WorktreeWorkspaces"/> in the SAME expression that nulls
/// <paramref name="Worktree"/> and sets <paramref name="IsWorktree"/> — so the value the fix reads is
/// captured before the field carrying it is cleared, unlike the symbolic ref this replaces. Null
/// whenever <paramref name="IsWorktree"/> is false, or the ref could not be resolved against the
/// source repository.
/// </param>
/// <param name="WorktreeSourceRepository">
/// #1166 review finding A: <paramref name="Worktree"/>'s <see cref="WorktreeWorkspace.Repository"/>,
/// stamped by <see cref="WorktreeWorkspaces"/> in the SAME expression as <paramref name="WorktreeBaseSha"/>
/// and for the identical reason — captured before <paramref name="Worktree"/> is nulled. This is the
/// project-ceiling lookup key <see cref="ProjectCeilingGate"/> uses in preference to
/// <paramref name="WorkingDirectory"/> whenever it is set: a worktree's <paramref name="WorkingDirectory"/>
/// is a fresh, room-scoped directory allocated at provisioning time (never the same path twice, and
/// never known to the operator ahead of dispatch), so keying the ceiling on it would make an
/// auto-provisioned worktree permanently untrustable — the operator has no stable path to run
/// <c>baton trust</c> against. The source repository is the stable, operator-known path 0004's ceiling
/// is actually about. Null whenever <paramref name="IsWorktree"/> is false.
/// </param>
/// <param name="ToolSha">
/// #1668: The commit SHA of the baton binary that dispatched this room, stamped at dispatch
/// time so side-by-side tool pruning can preserve versions referenced by live rooms. Null when
/// dispatched by a binary that predates the field or when unresolved.
/// </param>
/// <param name="ChangesTree">
/// #1622/#1390: whether this role's CONTRACT is "change the tree" -- read/write files and run shell
/// commands, the same two-predicate reading <c>OutcomeClassifier</c> derives it from at settle time.
/// Computed once, here, from the CATALOG role's own <see cref="WorkerRole.Grant"/>
/// (<see cref="RoleDispatch.ToBinding"/>) -- deliberately NOT re-derived from
/// <paramref name="PermissionGrant"/> above, which <c>ToBinding</c> can widen
/// (<c>WriteFiles: true</c>, audited-not-enforced) for a role that declares outputs but no tree-write
/// grant, purely so a non-outbox-capable adapter can still write its own declared report -- re-reading
/// that widened grant downstream would misclassify e.g. <c>review</c> as tree-changing under such an
/// adapter. False for every entry not constructed through <see cref="RoleDispatch.ToBinding"/> (a
/// hand-authored <c>bindings.json</c>, or a future front door that never sets it) -- the safe default,
/// since <c>workspaceChanged</c>/<c>hollow</c> are an additive signal, not a gate: false simply omits
/// the two settle-time fields rather than fabricating one for a role catalog this entry never named.
/// </param>
/// <param name="DeliversBranch">
/// #1788: <see cref="WorkerRole.DeliversBranch"/>, carried onto the resolved
/// <c>Baton.Mutation.WorkerBinding.Process</c> unchanged -- whether the engine's post-exit delivery
/// check (<c>Baton.Mutation.DeliveryVerifier</c>) runs at all. False for every entry not constructed
/// through <see cref="RoleDispatch.ToBinding"/>, the same safe default <paramref name="ChangesTree"/> uses.
/// </param>
/// <param name="ExpectPr">
/// #1788: the delivery check's PR-half switch, ALREADY RESOLVED by <see cref="RoleDispatch.ToBinding"/>
/// as <c>expectPrOverride ?? role.DeliversBranch</c> -- so this field, unlike most others on this
/// record, never needs its own nullable "not specified" state; a plain <see langword="false"/> here
/// means "do not check for a PR", which is also the correct reading for any entry not constructed
/// through <see cref="RoleDispatch.ToBinding"/> (the <paramref name="DeliversBranch"/> default already
/// disables the whole check in that case).
/// </param>
/// <param name="AllowsSubagents">
/// #1802: <see cref="WorkerRole.AllowsSubagents"/> (see that member's own doc for what this gates and
/// why), carried onto the resolved <see cref="WorkerInvocation"/> unchanged. Defaults to
/// <see langword="false"/> here (#1811 review), the same default-closed shape as
/// <paramref name="ChangesTree"/>/<paramref name="DeliversBranch"/>/<paramref name="ExpectPr"/> above --
/// unlike those, this one drives an actual enforcement flag on the spawn argv rather than an additive
/// signal, which is exactly why a hand-authored <c>bindings.json</c> (the <c>baton run</c>/<c>resume</c>/
/// <c>decide</c> path) that omits it must fail closed rather than silently permit spawning. <see
/// cref="RoleDispatch.ToBinding"/> is the one caller that overrides this from the catalog role's own
/// value for every role dispatched through the front door.
/// </param>
/// <param name="Skills">
/// #1151 (contract: <c>spec/baton.md</c> §9, "Canonical skill packages" — not restated here): the
/// canonical skill package names attached to this worker, alongside <paramref name="Timeout"/> (#1442)
/// and <paramref name="Label"/> (#1499) as a binding-level fact chosen at dispatch and constant for
/// every execution of the binding. Written by <c>--skill</c> on <c>baton dispatch</c>/<c>redispatch</c>,
/// and readable by a harness authoring <c>bindings.json</c> for <c>baton run</c> — which is the point:
/// each name is resolved through <see cref="SkillPackageResolver"/> at
/// <see cref="WorkerBindingResolver.Resolve"/>, the one seam BOTH verbs cross, so a name a harness
/// writes here is realized rather than silently ignored. Null or empty attaches nothing.
/// <para>
/// This field is why a <c>redispatch</c> does not drop a lane's skills: it is on the entry the parent
/// room recorded, so the child inherits it (<c>RedispatchCommand.InheritBinding</c>). Without it, the
/// fix for #1512 would reintroduce #1512 one verb over.
/// </para>
/// </param>
/// <param name="FallbackOnExhaustion">
/// #802 (S6, the design ratified on that issue): a declared vendor to rebind this role onto when its own dispatch
/// parks on a vendor-quota <see cref="Domain.FailureClassification.ExhaustedUntil"/> outcome, rather
/// than waiting out the vendor's reset. Null (the default) keeps today's behaviour: the step parks,
/// and (per spec/baton.md's quota-park section) the status surfaces the wait and the operator verb
/// that rebinds it by hand. Resolved through the SAME <see cref="WorkerBindingResolver.Resolve"/>
/// pipeline as every other binding — same adapter registry lookup, same permission/ceiling gates — so
/// a fallback can never widen what a role is permitted to do. Undeclared automatic failover across
/// DIFFERENT roles is permanently out of scope (operator ruling, 2026-09-01): this field opts in one
/// role at a time, to one named vendor, never inferred.
/// </param>
/// <param name="ModelResolved">
/// #1927: the model this binding will ACTUALLY run on, as far as dispatch can know it at bind time —
/// what a render surface shows so a room dispatched without <c>--model</c> never displays a bare
/// vendor. Resolved by <see cref="RoleDispatch.ToBinding"/> in one order: the dispatcher's own
/// <c>--model</c>, then the role's tier (<c>WorkerTiers.json</c>), then the vendor's measured CLI
/// default (<c>Baton.Domain.AdapterDefaultModels</c>). <b>Null is a real answer</b> — none of the three
/// named one — and is left null rather than guessed; <c>DepthTierMapping</c>'s no-fallback rule is the
/// precedent. Never read as a dispatch input.
/// </param>
/// <param name="ModelSource">
/// #1927: which rung of that order answered — <see cref="BindingValueSource.Requested"/> when the
/// dispatcher named the model itself, <see cref="BindingValueSource.ResolvedDefault"/> when Baton
/// filled it in. Present exactly when <paramref name="ModelResolved"/> is. A render surface marks the
/// second case so an operator can tell "I asked for this" from "this is what it fell back to".
/// </param>
/// <param name="EffortResolved">
/// #1927: the same fact for effort — the dispatcher's <c>--effort</c>, else the role's tier, else
/// <b>agy's model-id suffix</b>, the issue's own stated mechanism: on that vendor effort is not a
/// separate axis but part of the model name, so <c>gemini-3.8-flash-high</c> IS effort <c>high</c>
/// (<c>RoleDispatch.ResolveEffortStamp</c> reads it through the rule <c>AgyWorkerAdapter</c> already
/// enforces agreement against). Without that third rung this field was an exact duplicate of
/// <see cref="Effort"/> for every input, which is what left the issue's own room rendering no effort
/// segment at all. There is deliberately no adapter-wide DEFAULT effort rung: no vendor's is measured
/// here, and codex's per-model <c>defaultReasoningEffort</c> (recorded in
/// <c>docs/vendor-codex-probe-2026-09-04.md</c>) is per model rather than per adapter, so reading it
/// is not this change.
/// </param>
/// <param name="EffortSource">Which rung answered for <paramref name="EffortResolved"/>; same vocabulary as <paramref name="ModelSource"/>.</param>
public sealed record WorkerBindingConfigEntry(
    string Adapter,
    WorkerContract Contract,
    string PromptTemplate,
    TimeSpan Timeout,
    string? Model = null,
    string? PermissionScope = null,
    PermissionGrant? PermissionGrant = null,
    string? WorkingDirectory = null,
    string? SessionId = null,
    bool ResumeSession = false,
    bool StreamJson = false,
    string? LogFilePath = null,
    string? Effort = null,
    WorktreeWorkspace? Worktree = null,
    GrantAuditMode GrantAuditMode = GrantAuditMode.Enforced,
    bool IsWorktree = false,
    string? Label = null,
    string? VerifyPixiTask = null,
    string? VerifyCommandOverride = null,
    long? TokenBudget = null,
    int? MaxToolSteps = null,
    long? BilledRateLimit = null,
    string? Workstream = null,
    string? WorktreeBaseSha = null,
    string? WorktreeSourceRepository = null,
    string? ToolSha = null,
    bool ChangesTree = false,
    bool DeliversBranch = false,
    bool ExpectPr = false,
    // #1802 review: default-closed like ChangesTree/DeliversBranch/ExpectPr above -- a hand-authored
    // bindings.json (baton run/resume/decide) that omits this must not be able to spawn.
    bool AllowsSubagents = false,
    FallbackBinding? FallbackOnExhaustion = null,
    RunwayOverride? RunwayOverride = null,
    // #1896, stamped on every entry at dispatch rather than only on an override. What it holds, why it
    // sits beside RunwayOverride instead of inside it, and why the type lives in the engine layer: its
    // own remarks (Baton.Runway.RunwayAdmission).
    RunwayAdmission? RunwayAdmission = null,
    // #1151: appended last, and null (never an empty list standing in for "no skills") is the default,
    // so every entry authored before this field existed round-trips through
    // WorkerBindingConfigParser/Writer unchanged.
    IReadOnlyList<string>? Skills = null,
    // #1927, the four display-only stamps. THEY ARE NOT DISPATCH INPUTS: `Model`/`Effort` above are
    // what become the vendor CLI's own flags, and these deliberately do not touch them -- stamping a
    // resolved default onto Model would start passing --model to a vendor Baton previously let choose
    // for itself, a live behaviour change well past what a display gap asks for. RoleDispatch.ToBinding
    // is the one writer; every reader is a render surface (spec/baton.md §2/§6).
    string? ModelResolved = null,
    string? ModelSource = null,
    string? EffortResolved = null,
    string? EffortSource = null);

/// <summary>
/// #1927: the closed vocabulary <see cref="WorkerBindingConfigEntry.ModelSource"/> and
/// <see cref="WorkerBindingConfigEntry.EffortSource"/> are spelled in — two values, stated once, so a
/// render surface testing for "resolved" cannot drift from the writer that stamps it.
/// </summary>
public static class BindingValueSource
{
    /// <summary>The dispatcher named the value itself (<c>--model</c> / <c>--effort</c>).</summary>
    public const string Requested = "requested";

    /// <summary>Baton filled the value in — from the role's tier, or the vendor's measured CLI default.</summary>
    public const string ResolvedDefault = "resolved-default";
}

/// <summary>
/// #1848: the audit record a <c>--override-runway "&lt;reason&gt;"</c> dispatch leaves on the room's
/// own <c>bindings.json</c> entry — the operator's reason verbatim, the vendor it applied to, and the
/// counters the gate actually read at admission time. Written by <c>DispatchCommand</c> only; a
/// hand-authored <c>bindings.json</c> that carries one changes nothing, because the gate is consulted
/// at dispatch and never re-read from a binding.
/// </summary>
/// <param name="Used">
/// <see langword="false"/> when the flag was passed and the gate admitted anyway — the flag bypassed
/// nothing, and the record says so rather than being omitted, so "an override was offered and not
/// needed" stays distinguishable from "no override was offered". <see langword="true"/> is the audited
/// case: a Hold that this reason overrode.
/// </param>
public sealed record RunwayOverride(
    string Vendor,
    string Reason,
    bool Used,
    IReadOnlyList<RunwayCounter> Counters,
    string? HoldReason = null);


/// <summary>
/// A worktree workspace spec on a <see cref="WorkerBindingConfigEntry"/> (#669): the local
/// <paramref name="Repository"/> to make a worktree of, and the <paramref name="Ref"/> (a branch or
/// commit) to check out. The provisioning, teardown, and the local-only / Credential-Isolation
/// rationale all live on <c>Baton.Workspaces.WorktreeProvisioner</c>; this record is only the
/// declared intent.
/// </summary>
public sealed record WorktreeWorkspace(string Repository, string Ref);

/// <summary>
/// #802: the vendor a role's dispatch rebinds onto when its primary binding parks on a vendor-quota
/// exhaustion. <paramref name="Model"/>/<paramref name="Effort"/> are required-if-a-swap-is-wanted,
/// not inherited from the primary entry — the same #1082 rule <c>RoleDispatch.ToBinding</c> already
/// applies to an operator-requested vendor swap: a binding authored for one vendor's tier words has
/// no correct translation onto another vendor's, so leaving either null resolves to the fallback
/// adapter's own default rather than silently carrying the primary's tier across.
/// </summary>
/// <param name="Adapter">
/// The registered adapter name to rebind onto — resolved through the same
/// <see cref="WorkerBindingResolver.Resolve"/> adapter lookup as <see cref="WorkerBindingConfigEntry.Adapter"/>,
/// so an unregistered name refuses the same way. Must differ from the entry's own
/// <see cref="WorkerBindingConfigEntry.Adapter"/> — a binding that falls back to itself reads as a
/// safety net and provides none, refused at parse time (<see cref="WorkerBindingConfigParser"/>).
/// </param>
/// <param name="Model">Forwarded verbatim into the resolved fallback <see cref="WorkerInvocation"/>. Null defers to the fallback adapter's own default.</param>
/// <param name="Effort">Forwarded verbatim into the resolved fallback <see cref="WorkerInvocation"/>. Null defers to the fallback adapter's own default.</param>
public sealed record FallbackBinding(string Adapter, string? Model = null, string? Effort = null);
