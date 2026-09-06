namespace Baton.Vendors;

/// <summary>
/// The vendor-neutral description of what a worker adapter must invoke a worker to do (CLAUDE.md
/// rule #2's canonical protocol, M11 Phase 1). Paired with the <see cref="Baton.Domain.WorkerContract"/>
/// a <see cref="IWorkerAdapter"/> resolves alongside it, which already carries the ordered input
/// role names and declared outputs — this record adds only what the contract doesn't: the
/// human-authored prompt and the vendor-facing invocation knobs.
/// <para>
/// Built once, when a worker-binding config entry is resolved into a <see cref="Baton.Mutation.WorkerBinding"/>
/// — not once per execution. The resulting <see cref="Baton.Dispatch.CoreDispatchTarget"/> is
/// reused by <c>Baton.Mutation.MutationInterface.StartWorkflowAsync</c> for every dispatch of
/// this worker role across the whole run, so nothing here may carry a resolved, execution-specific
/// file path. Per-execution dynamism (which files exist at <c>BATON_INPUT_&lt;n&gt;</c>/
/// <c>BATON_OUTPUT_DIR</c> right now) is carried entirely by the environment variables
/// <c>Baton.Artifacts.ArtifactManager</c> already resolves per dispatch — an adapter
/// references those variables by name (shell-expanded at dispatch time, e.g. via a shell-wrapped
/// <see cref="Baton.Dispatch.CoreDispatchTarget"/>), the same convention the shell-stub workers
/// already use.
/// </para>
/// </summary>
/// <param name="PromptTemplate">
/// The instructional text handed to the worker, authored per worker role in the worker-binding
/// config — what to do, not how to do it. How it references its inputs/output (env var name, cwd,
/// shell expansion, absolute-path interpolation) is entirely the adapter's concern; two vendors can
/// need different accommodations for the identical template (spike #21).
/// </param>
/// <param name="Model">The vendor model identifier to invoke, if the vendor takes one. Null when not applicable.</param>
/// <param name="PermissionScope">
/// The raw, hand-typed permission grant to pre-authorize (e.g. Claude's <c>--allowedTools</c>
/// value) — each vendor's flag and vocabulary differs (spike #21), which is exactly why this is an
/// opaque string the adapter alone interprets, never a shared enum Baton or this record would
/// have to version. Superseded by <paramref name="PermissionGrant"/> when both are set (M21 Phase
/// 1) — kept only as the bindings editor's "Advanced" escape hatch for vendor vocabulary the
/// structured model can't yet express.
/// </param>
/// <param name="PermissionGrant">
/// The structured, vendor-neutral permission grant (M21 Phase 1) — the bindings editor's builder-UI
/// primary path. When set, an <see cref="IPermissionGrantTranslator"/>-implementing adapter's
/// <c>Resolve</c> translates it into the vendor-native flag value itself, ignoring
/// <paramref name="PermissionScope"/> entirely (<see cref="PermissionGrant"/>'s own docs record this
/// precedence). Null means "no structured grant configured" — the same "fall through to the
/// adapter's own default" behavior a null <paramref name="PermissionScope"/> already has.
/// </param>
/// <param name="WorkingDirectory">
/// The real, already-resolved absolute directory the spawned process should run in (M23 Phase 3,
/// #272) — resolved by <see cref="WorkerBindingResolver.Resolve"/> from
/// <see cref="WorkerBindingConfigEntry.WorkingDirectory"/> (a rooted path used directly, or a
/// per-machine profile name looked up in the local, never-portable profile mapping) before this
/// record is ever constructed, so every adapter receives the same real path regardless of which
/// machine or profile named it. Null keeps the prior default (no explicit cwd — AER's own scratch
/// artifacts folder). Every <see cref="IWorkerAdapter"/> forwards this into the
/// <see cref="Baton.Dispatch.CoreDispatchTarget"/> it builds unchanged — it carries no
/// vendor-specific meaning, unlike <paramref name="PromptTemplate"/>.
/// </param>
/// <param name="BindingsFileDirectory">
/// The directory the worker-bindings config file this invocation was resolved from lives in, if
/// known (M23 Phase 3, #272) — plain context, not an instruction: most adapters ignore it entirely
/// (<paramref name="PromptTemplate"/> is prose to them). No shipped adapter currently repurposes
/// <paramref name="PromptTemplate"/> as a file path to resolve against this directory — the adapter
/// that once did was retired with the dialogue worker (#1408) — but the field stays available for a
/// future adapter that needs the same sidecar-path portability fix (a bindings file copied to a new
/// machine, or a different directory on the same one).
/// </param>
/// <param name="SessionId">
/// The native vendor session identifier or session Guid for interactive sessions (M24 Phase 1).
/// </param>
/// <param name="ResumeSession">
/// <see langword="true"/> to resume an existing native session (Claude <c>--resume</c>, Gemini <c>--conversation</c>);
/// <see langword="false"/> to initialize a new native session (Claude <c>--session-id</c>).
/// </param>
/// <param name="StreamJson">
/// <see langword="true"/> to emit real-time stream-json output for live in-turn progress streaming (Claude <c>--output-format stream-json</c>).
/// </param>
/// <param name="LogFilePath">
/// The path to a log file where the vendor CLI writes side-channel logs (e.g. Gemini <c>--log-file</c> for capturing conversation id).
/// </param>
/// <param name="Effort">
/// 0023's canonical effort word (quick/standard/careful/exhaustive) OR a vendor's own raw
/// effort-level string (Claude's <c>--effort low|medium|high|xhigh|max</c>, Gemini's <c>--effort
/// low|medium|high</c>; see <c>docs/vendor-capabilities.md</c>) — the two sets are disjoint, so the
/// field's domain widened rather than gaining a second one (decision 0058's #1318 scope ruling 4).
/// No longer forwarded verbatim: each adapter's <c>Resolve</c> runs this through
/// <see cref="EffortTierMapping"/>, which translates a canonical word to that vendor's raw value and
/// passes a value already in the vendor's own raw set through untouched as the <c>#566</c> escape
/// hatch — see that type for why an unrecognized value is refused rather than forwarded. Null when
/// not applicable or not set.
/// </param>
/// <param name="Timeout">
/// The timeout AER will itself enforce on this worker's executions — the same
/// <c>WorkerBindingConfigEntry.Timeout</c> that becomes <c>ExecutionRequest.Timeout</c> (#588).
/// Supplied so an adapter can tell its vendor CLI about a limit the CLI would otherwise apply its own
/// default to; <c>AgyWorkerAdapter</c> is the one that needs it today, because <c>agy -p</c> has an
/// internal 5-minute print-mode wait that is otherwise completely decoupled from AER's timeout.
/// <para>
/// This is per <i>binding entry</i>, not per execution, which is what makes it legitimate here at all:
/// <see cref="IWorkerAdapter.Resolve"/> runs once per binding-config entry, and the timeout is
/// declared on that same entry (deliberately kept off the step — see
/// <c>WorkerBindingConfigEntry</c>). So this carries no execution-specific value, exactly like every
/// other member of this record.
/// </para>
/// Null when the caller has no timeout to declare; adapters must treat that as "say nothing and leave
/// the vendor's own default in effect".
/// </param>
/// <param name="EnableMemoryProposalTool">
/// <see langword="true"/> to wire this dispatch to AER's own MCP server (#585, #801) carrying the
/// <c>memory-edit-proposal</c> tool -- <see cref="ClaudeWorkerAdapter"/> points <c>--mcp-config</c>
/// at a config naming that server instead of its default empty one;
/// <see cref="AgyWorkerAdapter"/> materializes a workspace directory carrying
/// <c>.agents/mcp_config.json</c> and grants it via an extra <c>--add-dir</c>. Default
/// <see langword="false"/> keeps today's exact argv for every dispatch that does not opt in -- this
/// is an opt-in per #801's scope, not a default every worker now carries the way the mandatory
/// <c>PreToolUse</c> hook is (0029).
/// </param>
/// <param name="WorktreeSourceRepository">
/// #1166 review finding A: forwarded verbatim from <see cref="WorkerBindingConfigEntry.WorktreeSourceRepository"/>
/// -- see that member's own doc for why <see cref="ProjectCeilingGate"/> keys the project ceiling on
/// this rather than <paramref name="WorkingDirectory"/> whenever it is set.
/// </param>
/// <param name="AllowsSubagents">
/// #1802: forwarded verbatim from <see cref="WorkerBindingConfigEntry.AllowsSubagents"/> (see
/// <see cref="WorkerRole.AllowsSubagents"/> for what this gates and why). <see langword="false"/> makes
/// <see cref="ClaudeWorkerAdapter"/> append <c>Agent</c>/<c>Task</c> to <c>--disallowedTools</c> and
/// <see cref="AgyWorkerAdapter"/> add its subagent tool names to the denied-tools list, alongside
/// (never instead of) whatever each already withholds from <paramref name="PermissionGrant"/>. Default
/// <see langword="false"/> (#1811 review) -- an invocation built without naming this explicitly must
/// not be able to spawn a subagent; only <see cref="WorkerBindingResolver.Resolve"/>, forwarding a
/// <see cref="WorkerBindingConfigEntry.AllowsSubagents"/> that itself defaults closed, or a caller
/// that opts in explicitly, produces <see langword="true"/>.
/// </param>
/// <param name="Skills">
/// #1151: the canonical skill packages this binding declared, ALREADY RESOLVED through
/// <see cref="SkillPackageResolver"/>'s rung ladder by <see cref="WorkerBindingResolver.Resolve"/> —
/// the adapters receive packages, never names, so no adapter re-implements resolution or precedence.
/// Legal on a per-binding record for the reason the type remarks above give: a skill set is chosen at
/// dispatch and is constant for every execution of the binding, exactly like
/// <paramref name="Timeout"/>.
/// <para>
/// Null or empty means the binding declared none, and each adapter then falls back to discovering
/// canonical packages under its working directory — the behaviour #1929 shipped, kept so a repository
/// carrying <c>skills/</c> still realizes them for a binding that names nothing. A non-empty list is
/// the declared set and REPLACES that scan: a binding that says which skills it wants must not silently
/// also get whichever ones happen to be checked into the repository it was pointed at.
/// </para>
/// </param>
public sealed record WorkerInvocation(
    string PromptTemplate,
    string? Model = null,
    string? PermissionScope = null,
    PermissionGrant? PermissionGrant = null,
    string? WorkingDirectory = null,
    string? BindingsFileDirectory = null,
    string? SessionId = null,
    bool ResumeSession = false,
    bool StreamJson = false,
    string? LogFilePath = null,
    string? Effort = null,
    TimeSpan? Timeout = null,
    bool EnableMemoryProposalTool = false,
    string? WorktreeSourceRepository = null,
    // #1802 review: default-closed; only RoleDispatch.ToBinding (from the catalog) ever sets true.
    bool AllowsSubagents = false,
    // #1151: see the Skills doc above.
    IReadOnlyList<SkillPackage>? Skills = null);

