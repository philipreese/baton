namespace Baton.Cli;

/// <summary>
/// Parsed arguments for the <c>baton dispatch</c> command — just the inputs. <see cref="DispatchCommand"/>
/// resolves <paramref name="Name"/> against the catalogs (one namespace) and drives the result through
/// the same pump <c>baton run</c> uses; what a role vs a template means, and why, lives there.
/// </summary>
/// <param name="Name">The catalog role or workflow template to dispatch (e.g. <c>review</c>), resolved by <see cref="DispatchCommand"/>.</param>
/// <param name="SpecFilePath">
/// The file whose contents are the task prompt — what a <em>role</em> is asked to do. Exactly one of
/// this, <paramref name="SpecText"/>, or <paramref name="SpecFromStdin"/> may be set for a role dispatch
/// (rejected for a template, same as the other two — a template's phases already carry their
/// instructions). Null when not supplied.
/// </param>
/// <param name="SpecText">
/// The task prompt given inline via <c>--spec-text</c> (#1518) — a scout question that does not
/// warrant a brief file. Mutually exclusive with <paramref name="SpecFilePath"/> and
/// <paramref name="SpecFromStdin"/>; <see cref="DispatchOptionsParser"/> enforces at most one of the
/// three, and <see cref="DispatchCommand"/> enforces that at least one is present for a role.
/// Why all three sources produce an identical room record is stated once, on the dispatch entry of
/// spec/baton.md — not repeated in this file.
/// </param>
/// <param name="SpecFromStdin">
/// True when <c>--spec -</c> (#1518) was passed — read the task prompt from stdin instead of a file.
/// Mutually exclusive with <paramref name="SpecFilePath"/> and <paramref name="SpecText"/>; see
/// <paramref name="SpecText"/>'s remarks.
/// </param>
/// <param name="RoomDirectoryPath">
/// Where this dispatch's durable state lives. Defaults to a fresh, uniquely-named directory per
/// invocation (see <see cref="DispatchOptionsParser"/>) so a repeated self-dispatch runs anew rather
/// than resuming — and so replaying a prior terminal snapshot — the way an orchestrator (#778) issues
/// the same name many times. Pass an explicit value to resume a specific interrupted dispatch.
/// </param>
/// <param name="Adapter">
/// A vendor adapter to run every role/phase on instead of its tier default — the escape hatch. Null
/// keeps each role's own tier-resolved adapter.
/// </param>
/// <param name="WorkflowId">A label forwarded to the run; defaults to the materialized template id.</param>
/// <param name="WorkspaceDirectory">
/// The directory a dispatched role runs in and may read. Null resolves to the process cwd in
/// <see cref="DispatchCommand"/>, the common case (dispatching from the repo). Why the binding needs a
/// path at all, and what broke without one, is on <see cref="Baton.Vendors.RoleDispatch"/>'s
/// <c>workingDirectory</c> parameter (#1083).
/// </param>
/// <param name="Model">
/// The model axis, independent of the role's tier ([0017]: vendor/model/effort are three
/// separate axes). Null keeps the tier's model; the vendor-swap carve-out is documented on
/// <see cref="Baton.Vendors.RoleDispatch"/>'s <c>modelOverride</c> (#1082).
/// </param>
/// <param name="Effort">The effort axis, independent of the role's tier ([0017]/[0023]); null keeps the tier's effort.</param>
/// <param name="OutputPath">The path where the primary worker report output must land (#1354); null keeps room artifact path default.</param>
/// <param name="Timeout">
/// The <c>--timeout</c> escape hatch (#1442) — semantics and rationale in spec/baton.md §2. Role
/// dispatch only, rejected for a workflow template the same way <see cref="OutputPath"/> is. Null
/// keeps the role's tier timeout. Validated and bounded by <see cref="DispatchOptionsParser"/>, not here.
/// </param>
/// <param name="Label">
/// The <c>--label</c> escape hatch (#1499) — full contract in spec/baton.md §2. Sanitized by
/// <see cref="DispatchOptionsParser"/>, not here. Null keeps a room unlabeled.
/// </param>
/// <param name="Workstream">
/// The <c>--workstream</c> grouping slug (#1619, rung 1 of #1614's ruling) — full contract in
/// spec/baton.md §2. Sanitized and slug-validated by <see cref="DispatchOptionsParser"/>, not here.
/// Null keeps a room out of every workstream group and skips the by-workstream junction.
/// </param>
/// <param name="Attachments">The <c>--attach</c> context files copied into the room (#1500).</param>
/// <param name="ListCapabilities">True when <c>--list-capabilities</c> was passed to print discoverability info (#1500).</param>
/// <param name="TokenBudget">
/// The <c>--token-budget</c> escape hatch (#1623) — per-execution token ceiling, independent of the
/// role like <paramref name="Timeout"/>. Role dispatch only, rejected for a workflow template the same
/// way <paramref name="Timeout"/> is. Null keeps the role's own default (<c>Baton.Vendors.WorkerRole.TokenBudget</c>).
/// </param>
/// <param name="RepoPath">
/// <c>--repo</c> (#1645): a checkout whose <see cref="InstalledVersionDrift"/> release version this
/// dispatch's installed <c>baton</c> is compared against, printing a WARN on stderr when behind. Null
/// falls back to <c>BATON_REPO</c> (<see cref="Baton.Status.BatonEnvironmentSnapshot.RepoOverride"/>);
/// neither present means no checkout is discoverable and the check is skipped, not refused.
/// </param>
/// <param name="MaxToolSteps">
/// The <c>--max-tool-steps</c> escape hatch (#1686 review F11) — per-execution real-tool-call ceiling,
/// mirroring <paramref name="TokenBudget"/> end to end. Role dispatch only, rejected for a workflow
/// template the same way <paramref name="TokenBudget"/> is. Null keeps the role's own default
/// (<c>Baton.Vendors.WorkerRole.MaxToolSteps</c>).
/// </param>
/// <param name="BilledRateLimit">
/// The <c>--billed-rate-limit</c> escape hatch (#1691) — the ceiling on billed tokens inside one
/// trailing <c>Baton.Mutation.TokenBudgetMonitor.BilledRateWindow</c> (5 minutes), mirroring
/// <paramref name="TokenBudget"/> end to end. Role dispatch only, rejected for a workflow template the
/// same way <paramref name="TokenBudget"/> is. Null keeps the role's own default
/// (<c>Baton.Vendors.WorkerRole.BilledRateLimit</c>) — which no role sets, so null means no rate
/// trigger at all.
/// </param>
/// <param name="VerifyCommand">
/// The <c>--verify</c> escape hatch (#1702) — the highest-precedence input to the engine's verify-command
/// resolution (spec/baton.md §3), ahead of the workspace's own <c>.baton/verify</c> declaration and the
/// role's <c>verify_pixi_task</c> default. Role dispatch only, rejected for a workflow template the same
/// way <paramref name="TokenBudget"/> is. Null defers to the workspace/role resolution.
/// </param>
/// <param name="ExpectPr">
/// The <c>--expect-pr</c> escape hatch (#1788) — whether the engine's post-exit delivery check
/// (spec/baton.md §3) requires an open PR for the pushed branch, in addition to the branch itself being
/// pushed. Role dispatch only, rejected for a workflow template the same way <paramref name="TokenBudget"/>
/// is. Null keeps the role's own default (true for a role with <c>Baton.Vendors.WorkerRole.DeliversBranch</c>
/// set, meaningless — never checked — for one without it).
/// </param>
/// <param name="ContinueFromRoomDirectoryPath">
/// <c>--continue &lt;room-dir&gt;</c> (#1381): rehire the worker that ran in this terminal room —
/// resume its vendor session for a follow-on brief rather than starting cold. Role dispatch only,
/// rejected for a workflow template the same way <paramref name="TokenBudget"/> is (a template has no
/// single worker to rehire). Resolution — the prior room's own single-worker bindings entry, its
/// adapter/SessionId, and the loud refusals when either is missing or the vendor differs — lives in
/// <see cref="DispatchCommand"/>, not here; this record carries only the raw path the operator passed.
/// Accepts a room directory only, not a bare execution id, despite the issue's own "room-or-execution-id"
/// phrasing — see <see cref="DispatchCommand"/>'s remarks on why that second form is refused rather than
/// silently only-sometimes honored.
/// </param>
/// <param name="VerifyCommands">
/// The repeatable <c>--verify-cmd</c> flag (#1882) — the allowlisted commands
/// <see cref="Baton.Mutation.VerifyStepRunner"/> runs (its own doc has when, and why no model is in
/// the loop; the contract is spec/baton.md §9). Nothing runs unless asked: null or empty means no
/// verify step at all, which is every dispatch that does not pass the flag. <b>Not the same thing as <c>--verify</c>/<see cref="VerifyCommand"/></b>, which is the
/// engine's post-exit gate on a mutating role (<c>implement</c>'s <c>verify_pixi_task</c>): that one
/// runs AFTER a worker exits and decides whether the execution settles; this one runs BEFORE the worker
/// starts and decides nothing — it is evidence the reviewer reads. Shapes are validated at parse time
/// by <c>Baton.Mutation.VerifyStepCommandParser</c>; role dispatch only, and refused for a non-review
/// role and for a workflow template the same way <see cref="TokenBudget"/> is.
/// </param>
/// <param name="VerifyTimeout">
/// The <c>--verify-timeout</c> bound (#1882) on EACH <paramref name="VerifyCommands"/> entry's wall
/// clock. Null keeps <c>Baton.Mutation.VerifyStepRunner.DefaultTimeout</c> (10 minutes).
/// </param>
/// <param name="OverrideRunwayReason">
/// <c>--override-runway "&lt;reason&gt;"</c> (#1848) — the only bypass of the runway hold, and the
/// reason is mandatory (a blank one is a parse error in <see cref="DispatchOptionsParser"/>). Unlike
/// <paramref name="Timeout"/> and its neighbours this is NOT role-dispatch-only: a workflow template
/// admits new vendor spend exactly the way a role does, so the flag applies to both. Null means no
/// override — a held vendor refuses the dispatch. On a cold dispatch, whether it was needed is recorded
/// either way (<see cref="Baton.Vendors.RunwayOverride.Used"/>); combined with
/// <paramref name="ContinueFromRoomDirectoryPath"/> it is REFUSED rather than recorded, because a
/// continuation consults no gate — there is nothing for it to bypass and no decision for its reason to
/// annotate.
/// </param>
/// <param name="Skills">
/// The repeatable <c>--skill &lt;name&gt;</c> flag (#1151) — canonical skill package names attached to
/// this dispatch, resolved through <c>Baton.Vendors.SkillPackageResolver</c>'s rung ladder and
/// persisted onto the binding. Role dispatch only, rejected for a workflow template the same way
/// <paramref name="TokenBudget"/> is. Null keeps the dispatch skill-less; the contract is
/// spec/baton.md §9.
/// </param>
public sealed record DispatchOptions(
    string Name,
    string? SpecFilePath,
    string RoomDirectoryPath,
    string? Adapter = null,
    string? WorkflowId = null,
    string? WorkspaceDirectory = null,
    string? Model = null,
    string? Effort = null,
    string? OutputPath = null,
    TimeSpan? Timeout = null,
    string? Label = null,
    string? Workstream = null,
    IReadOnlyList<string>? Attachments = null,
    bool ListCapabilities = false,
    long? TokenBudget = null,
    string? RepoPath = null,
    int? MaxToolSteps = null,
    long? BilledRateLimit = null,
    string? VerifyCommand = null,
    string? SpecText = null,
    bool SpecFromStdin = false,
    bool? ExpectPr = null,
    string? ContinueFromRoomDirectoryPath = null,
    IReadOnlyList<string>? VerifyCommands = null,
    TimeSpan? VerifyTimeout = null,
    string? OverrideRunwayReason = null,
    IReadOnlyList<string>? Skills = null);
