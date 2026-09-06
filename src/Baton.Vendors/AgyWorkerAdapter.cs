using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Status;

namespace Baton.Vendors;

/// <summary>
/// Direct shell-less <see cref="IWorkerAdapter"/> (M20 Phase 4): resolves a
/// <see cref="WorkerInvocation"/>/<see cref="WorkerContract"/> pair into a direct <c>agy</c>
/// (Google Gemini CLI) invocation without shell wrappers. Bypasses cmd.exe and sh, eliminating quoting and
/// command injection risks. Stdin redirection to null is handled natively by the process host.
/// <para>
/// <b>M21 Phase 1's <see cref="IPermissionGrantTranslator"/>:</b> unlike Claude's per-tool
/// <c>--allowedTools</c>, <c>agy</c>'s permission flags consist of <c>--mode</c> (coarse settings:
/// <c>default</c>, <c>accept-edits</c>, <c>plan</c>) and <c>--dangerously-skip-permissions</c> (which
/// auto-approves all tool permission requests without prompting, including shell commands and network access).
/// Because <c>--dangerously-skip-permissions</c> is all-or-nothing, requesting only one of
/// <see cref="PermissionGrant.RunShellCommands"/> or <see cref="PermissionGrant.NetworkAccess"/> without
/// the other is refused to prevent over-granting unrequested capabilities. Requesting both
/// <see cref="PermissionGrant.RunShellCommands"/> and <see cref="PermissionGrant.NetworkAccess"/> together
/// matches <c>--dangerously-skip-permissions</c> exactly and is translated to that flag — see
/// <see cref="TryTranslatePermissionGrant"/>.
/// </para>
/// <para>
/// <b>Why no <c>--disallowedTools</c> mirror (unlike Claude, #331):</b> a shell-<em>withheld</em>
/// grant maps to a plain <c>--mode</c> here, and <c>agy</c> has no deny-list flag — but it does not
/// need one. Headless <c>agy</c> <em>auto-denies</em> a <b>shell command</b> it cannot prompt for
/// (<c>agy.fails-closed-headless</c>, measured with <c>node --version</c> across
/// <c>default</c>/<c>plan</c>/<c>accept-edits</c>; see <c>docs/runbooks/live-claude-smoke.md</c>'s J6
/// section) — the opposite of Claude Code's headless auto-<em>approve</em>, which is exactly what
/// made #331 possible there.
/// </para>
/// <para>
/// <b>That is one tool, and it does not generalise — #670.</b> This paragraph used to claim agy
/// auto-denies <em>any</em> tool needing a permission it cannot prompt for. Measured against the live
/// CLI: under <c>--mode plan</c>, agy <b>writes a file into an <c>--add-dir</c> path without a prompt
/// or a refusal</b>, and reports it as succeeded. So the fail-closed default covers the shell arm that
/// was measured and not the write arm that was assumed.
/// </para>
/// <para>
/// <b>That argument does not reach the <c>--dangerously-skip-permissions</c> branch, and #596 exists
/// because it reads as though it does.</b> Note which modes the paragraph above was verified across:
/// <c>default</c>/<c>plan</c>/<c>accept-edits</c> — every mode <em>except</em> the one that turns
/// auto-denial off. Under that flag <c>agy</c> stops refusing what it cannot prompt for, so a grant
/// of shell + network with <see cref="PermissionGrant.WriteFiles"/> withheld would hand the worker
/// the writes the operator declined, purely from the flag.
/// </para>
/// <para>
/// What actually withholds them there is the <c>PreToolUse</c> hook (#554), not the vendor's own
/// default: <see cref="BuildDeniedTools"/> derives denied tools from <b>all four boolean</b> grant categories
/// — reads and writes included, not only the two the flag encodes — and every invocation carries that
/// list in <see cref="DeniedToolsVariable"/>, this branch included. A hook deny blocking a call
/// <em>while running under <c>--dangerously-skip-permissions</c></em> is measured, not inferred from
/// the <c>--mode</c> case: <c>agy.hook-deny-honoured</c> spawns with that exact flag. So the flag
/// over-grants and the hook takes it back, which is a materially different safety story from
/// "the vendor is fail-closed" and is why it is written down separately.
/// </para>
/// <para>
/// <b>The consequence for anyone editing this class:</b> under that branch the tool-name lists
/// (<c>ReadTools</c>, <c>WriteTools</c>, <c>ShellTools</c>, <c>SubagentAndTaskTools</c>,
/// <c>NetworkTools</c>) are the entire enforcement boundary — a write-capable <c>agy</c> tool missing
/// from <c>WriteTools</c> is simply not denied, and <c>SubagentAndTaskTools</c> is withheld under
/// either <c>!WriteFiles</c> or <c>!RunShellCommands</c> rather than only the latter, because none of
/// its four tools is narrowed by the pattern channel that bounds <c>run_command</c> (#1387 review,
/// F1). Whether those lists are complete against agy's real tool surface is unmeasured — #623,
/// which is the security property here rather than a tidiness question. Removing a category from
/// <see cref="BuildDeniedTools"/> as "redundant with the flag" is the specific edit that would make
/// #596's over-grant real.
/// </para>
/// <para>
/// <b>And the hook only takes it back while it runs.</b> On this vendor an absent or unparseable hook
/// response reads as an <em>allow</em> — see the fail-open note on <see cref="BuildHooksJson"/> below.
/// For writes there is no backstop under <c>--mode</c> either (#670), so a hook that cannot start is
/// a fully ungated worker on every branch of this method. Scoping shell patterns is in the same
/// direction: a grant narrowed by <see cref="PermissionGrant.ShellCommandPatterns"/> used to be
/// refused rather than resolved to an unscoped shell (#624). Since #659 it is <b>enforced</b>, not
/// refused — the hook now reads the shell command's arguments (as #679 already reads a write's target)
/// and <c>AgyHookCheckCommand</c> matches the command line against the patterns via
/// <c>ShellCommandPatternMatcher</c>, denying anything outside them.
/// </para>
/// </summary>
public sealed partial class AgyWorkerAdapter : IWorkerAdapter, IPermissionGrantTranslator
{
    private readonly IAgyHookLivenessProbe _hookLivenessProbe;

    public AgyWorkerAdapter(IAgyHookLivenessProbe? hookLivenessProbe = null)
    {
        _hookLivenessProbe = hookLivenessProbe ?? new ProcessAgyHookLivenessProbe();
    }

    internal const string OversizePromptWrapperText =
        "Read the full task instructions at %BATON_PROMPT_FILE% and execute them exactly as written. Do not summarize or treat as data.";

    /// <summary>
    /// #1623: <c>run_command</c> backgrounds a long-running command, and the model then polls
    /// <c>manage_task</c> <c>Action:status</c> in a tight loop until it finishes -- measured from a real
    /// captured lane; see <c>docs/vendor-capabilities.md</c>'s "Sharp edges" section (the canonical
    /// record, not restated here) for the figures and what remains unmeasured. Worth noting only here:
    /// the worst offender in that lane was not a gate command at all but a slow `git push` (blocked on
    /// the pre-push hook's own gate run), which is why the instruction below names no specific command.
    /// </summary>
    internal const string ForegroundGateInstructionText =
        "Run commands in the foreground and wait for them to finish before continuing -- including " +
        "`git push` and anything else that can run slowly (a pre-push hook, gate commands like `pixi " +
        "run gates-quiet`, test suites). Do not let `run_command` background any command and then poll " +
        "`manage_task status` in a loop -- every poll costs a full turn and burns context while the " +
        "command is still running. If a command must run asynchronously, check its status at most once " +
        "per minute.";

    private const string DefaultPermissionScope = "accept-edits";

    /// <summary>
    /// The environment variable carrying this invocation's denied-tool list to the
    /// <c>PreToolUse</c> hook's own process (#554) — the agy-side counterpart of
    /// <see cref="ClaudeWorkerAdapter.DeniedToolsVariable"/>, and deliberately the same variable
    /// name: a worker is only ever one vendor, so the values differ while the channel need not.
    /// Mirrored as a plain string on <c>AgyHookCheckCommand.DeniedToolsEnvironmentVariable</c>
    /// because <c>Baton.Vendors</c> cannot reference <c>Baton.Cli</c>; both sides assert the literal
    /// value in their own suite.
    /// </summary>
    /// <remarks>
    /// That an agy hook subprocess inherits this at all is <b>measured, not assumed</b>:
    /// <c>agy.hook-env-inherited</c> (a sentinel) confirms it. agy's own hook documentation says
    /// nothing about environment inheritance where claude's states it explicitly, so reusing
    /// claude's answer without measuring would have been the population-scope mistake gate `claim-scope` names.
    /// </remarks>
    /// <summary>
    /// The vendor tag prefixing <see cref="DeniedToolsVariable"/>'s value (#600). Deliberately not
    /// shared with claude's: the variable name is the same because a worker is only ever one vendor,
    /// but the tag is what says which one, so the two tags must differ or it says nothing.
    /// </summary>
    public const string DeniedToolsVendorTag = "agy";

    public const string DeniedToolsVariable = ClaudeWorkerAdapter.DeniedToolsVariable;

    public const string ShellPatternsVendorTag = "agy";

    public const string ShellPatternsVariable = ClaudeWorkerAdapter.ShellPatternsVariable;

    /// <summary>
    /// The environment variable carrying this invocation's <b>denied</b> shell command patterns —
    /// 0022's DenyAlways rung (#390). agy has no <c>--disallowedTools</c> equivalent that can express a
    /// command family (its rules match the whole line literally), so unlike claude its ONLY enforcement
    /// for a standing "never" is AER's own <c>PreToolUse</c> hook (<c>AgyHookCheckCommand</c>), which
    /// reads this and refuses a matching command deny-beats-allow. A separate channel from
    /// <see cref="ShellPatternsVariable"/> because the two lists are opposite in sign: one narrows an
    /// allow, one subtracts from it.
    /// </summary>
    public const string DeniedShellPatternsVariable = "BATON_HOOK_DENIED_SHELL_PATTERNS";

    /// <summary>
    /// The environment variable carrying this invocation's <b>denied shell option tokens</b>
    /// (<see cref="PermissionGrant.DeniedShellOptionTokens"/>, #1683 F2). A third channel rather than
    /// more entries on <see cref="DeniedShellPatternsVariable"/> because a hook reading one list cannot
    /// tell which of the two matching rules an entry wants; the rules themselves are stated on
    /// <c>ShellCommandPatternMatcher.IsDeniedByOptionToken</c>. That method also records why no vendor
    /// flag carries this rung on either vendor, which makes both hooks its sole enforcement.
    /// </summary>
    public const string DeniedShellOptionTokensVariable = "BATON_HOOK_DENIED_SHELL_OPTION_TOKENS";

    /// <summary>
    /// The environment variable naming the file <see cref="AgyHookVerdictLedger"/> counts (#1680) —
    /// the hook subprocess (<c>AgyHookCheckCommand</c>, via <c>baton agy-hook-check</c>) appends one
    /// line to this path every time it reaches a verdict. Per-EXECUTION (#1732 review F2):
    /// <see cref="Resolve"/> emits an unresolved <c>BATON_OUTPUT_DIR</c> reference rather than a
    /// resolved directory, because <see cref="Resolve"/> itself runs once per binding-config entry and
    /// a value it resolves would be shared by every execution and every agy role in the room — the
    /// prior "room-local" phrasing here described that shared, un-reset scope and asserted the
    /// opposite of what it produced. <c>BATON_OUTPUT_DIR</c> only resolves per execution, so each
    /// execution gets its own ledger file and no two executions — concurrent or sequential, same role
    /// or different — ever share one.
    /// </summary>
    public const string VerdictLedgerVariable = "BATON_HOOK_VERDICT_LEDGER";

    /// <summary>
    /// The file name under the ledger directory <see cref="VerdictLedgerVariable"/> names.
    /// Dot-prefixed (#1732 review sub-threshold) so <see cref="Baton.Dispatch.ExecutionStreamLogger.IsStreamLogFileName"/>
    /// can filter it out of a future deliverable listing the same way it already filters the engine's
    /// own stream-log files — this is an engine-owned mechanism artifact, not a worker deliverable.
    /// </summary>
    internal const string VerdictLedgerFileName = ".agy-hook-verdicts.ndjson";


    /// <summary>
    /// The name of the workspace directory AER owns and points every agy worker at, holding the
    /// <c>.agents/hooks.json</c> carrying decision 0029's mandatory <c>PreToolUse</c> gate.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="BatonPaths.WorkerLaunchConfig"/>'s root rather than sharing it: agy
    /// discovers hooks only from a directory handed to <c>--add-dir</c>
    /// (<c>agy.hooks-load-from-add-dir-not-only-cwd</c>), and <c>--add-dir</c> also grants the
    /// worker <em>file access</em> to whatever it names. Pointing it at the launch-config root would
    /// hand every worker read/write access to AER's other launch files; a dedicated leaf directory
    /// keeps that blast radius to the hook file itself.
    /// <para>
    /// The worker can still write to that file, which sounds worse than it is:
    /// <c>agy.hooks-json-cached-at-startup</c> (a sentinel) measures that agy reads the file once at
    /// startup, so a worker cannot disable its own gate mid-run by deleting or rewriting it. Because
    /// <see cref="EnsureAgyWorkspace"/> rewrites the file on every resolve, a tampered file cannot
    /// survive into the next spawn either. What remains is untidiness, not a live bypass.
    /// </para>
    /// </remarks>
    public const string AgyWorkspaceDirectoryName = "agy-workspace";

    /// <summary>
    /// agy's own tool names for each permission category — an entirely separate vocabulary from
    /// claude's, not a renaming of it, which is why this cannot share
    /// <c>ClaudeWorkerAdapter.BuildDisallowedTools</c>.
    /// </summary>
    /// <remarks>
    /// Taken from the tool list in <c>.vendor-survey/corpus/agy__hooks.md</c>. Two entries exist
    /// because a narrower mapping leaks the category it withholds: <c>grep_search</c> returns file
    /// <em>contents</em>, so withholding only <c>view_file</c> leaves reads reachable; and
    /// <c>manage_task</c> sends stdin to and kills background shell processes, so withholding only
    /// <c>run_command</c> leaves shell control reachable. <c>list_dir</c> and <c>find_by_name</c>
    /// disclose directory structure rather than contents and are withheld with reads on the same
    /// reasoning 0004's "fail closed" applies elsewhere.
    /// </remarks>
    // AGY_TOOL_LISTS:START
    private static readonly IReadOnlyList<string> ReadTools =
        ["view_file", "list_dir", "find_by_name", "grep_search"];

    /// <remarks>
    /// <c>generate_image</c> is here because the corpus describes it as "Create or edit images" with
    /// an <c>ImageName</c> and <c>ImagePaths</c> — a file creation and modification path, not a
    /// rendering-only one.
    /// </remarks>
    private static readonly IReadOnlyList<string> WriteTools =
        ["write_to_file", "replace_file_content", "multi_replace_file_content", "generate_image"];

    private static readonly IReadOnlyList<string> ShellTools = ["run_command"];

    /// <remarks>
    /// <para>
    /// The subagent trio is withheld with <c>manage_task</c> because it is agy's closest analogue to
    /// claude's <c>Task</c>, and because of a bypass an independent reviewer found in the first draft:
    /// <c>define_subagent</c> takes <c>enable_write_tools</c> as an argument and
    /// <c>invoke_subagent</c> takes an optional <c>Workspace</c>. A write-withheld worker could
    /// therefore define a subagent with write tools enabled and invoke it — possibly under a
    /// different workspace root than the one this hook was loaded from. <c>manage_task</c> is grouped
    /// with them rather than with <see cref="ShellTools"/> for the reason given on <see cref="ReadTools"/>
    /// above — it reaches background shell control that the hook's pattern channel never inspects — so
    /// the same reasoning applies to it independent of whether <c>run_command</c> itself is bounded.
    /// </para>
    /// <para>
    /// <b>Whether a subagent's own tool calls re-enter this hook is unmeasured on agy</b>, so this
    /// withholds the spawn rather than relying on the gate reaching the child. Decision 0029 requires
    /// exactly that posture — "never assume a subagent is more constrained than the session that
    /// spawned it" — and agy exposes no depth-cap equivalent to
    /// <see cref="ClaudeWorkerAdapter.MaxSubagentSpawnDepthVariable"/>, so withholding is the only
    /// lever available here. Tracked in #601.
    /// </para>
    /// <para>
    /// Withheld whenever <b>either</b> <c>WriteFiles</c> or <c>RunShellCommands</c> is false, not only
    /// under <c>!RunShellCommands</c> as before this pass (#1387 review, F1): a write-withheld,
    /// shell-granted role such as <c>review</c> can still reach <c>run_command</c>, and none of these
    /// four tools is narrowed by the pattern channel that bounds <c>run_command</c> — so the spawn
    /// lever has to stay pulled whenever writes are withheld even though shell itself is granted.
    /// </para>
    /// <para>
    /// <b>#1802 adds a second, independent trigger.</b> A grant that keeps both <c>WriteFiles</c> and
    /// <c>RunShellCommands</c> true — <c>implement</c>'s own grant — never hit either predicate above,
    /// so an implement worker on agy kept the subagent trio unconditionally. <see cref="BuildDeniedTools"/>'s
    /// own <c>allowsSubagents</c> parameter (from <see cref="WorkerRole.AllowsSubagents"/>) now withholds
    /// this trio on that basis too, same duplicate-review waste <c>ClaudeWorkerAdapter</c>'s
    /// <c>--disallowedTools Agent,Task</c> exists to close.
    /// </para>
    /// </remarks>
    private static readonly IReadOnlyList<string> SubagentAndTaskTools =
        ["manage_task", "invoke_subagent", "define_subagent", "manage_subagents"];

    /// <remarks>
    /// <c>browser_*</c> is a prefix entry (see <c>AgyHookCheckCommand</c>'s prefix support). The
    /// corpus's matcher section offers <c>"browser_.*"</c> as an example — "Match any tool starting
    /// with <c>browser_</c>" — while its Supported Tools list enumerates none of them, so the exact
    /// names cannot be written down. A browser tool reaches the network, and the corpus contradicting
    /// itself is not a reason to withhold nothing.
    /// </remarks>
    private static readonly IReadOnlyList<string> NetworkTools =
        ["search_web", "read_url_content", "browser_*"];
    // AGY_TOOL_LISTS:END

    /// <summary>
    /// The agy tool names this invocation's grant withholds, comma-joined for
    /// <see cref="DeniedToolsVariable"/>. Empty when nothing is withheld, which
    /// <c>AgyHookCheckCommand</c> reads as "allow everything" — a known-empty grant, distinct from
    /// the failure paths it denies on.
    /// </summary>
    /// <param name="grant">The structured permission grant, or null for the raw <c>PermissionScope</c> escape hatch.</param>
    /// <param name="allowsSubagents">
    /// #1802: <see cref="WorkerInvocation.AllowsSubagents"/> — when false, <see cref="SubagentAndTaskTools"/>
    /// is withheld regardless of <paramref name="grant"/>'s write/shell categories, independent of (and in
    /// addition to) the existing write-or-shell-withheld rule below.
    /// </param>
    internal static string BuildDeniedTools(PermissionGrant? grant, bool allowsSubagents = true)
    {
        List<string> denied = [];

        if (grant is not null)
        {
            if (!grant.ReadFiles)
            {
                denied.AddRange(ReadTools);
            }

            if (!grant.WriteFiles)
            {
                denied.AddRange(WriteTools);
            }

            if (!grant.RunShellCommands)
            {
                denied.AddRange(ShellTools);
            }

            if (!grant.NetworkAccess)
            {
                denied.AddRange(NetworkTools);
            }
        }

        if (!allowsSubagents || (grant is not null && (!grant.WriteFiles || !grant.RunShellCommands)))
        {
            denied.AddRange(SubagentAndTaskTools);
        }

        return string.Join(',', denied.Distinct(StringComparer.Ordinal));
    }

    /// <summary>
    /// #1680: true when a resolved <paramref name="permissionScope"/> of
    /// <c>--dangerously-skip-permissions</c> leaves the <c>PreToolUse</c> hook as the ONLY thing
    /// narrowing this grant. That is not only the write/network-withheld shape #1680 was filed about
    /// (this class's own remarks: "the hook only takes it back while it runs") — even a grant with
    /// both <see cref="PermissionGrant.WriteFiles"/> and <see cref="PermissionGrant.NetworkAccess"/>
    /// true still has the hook as sole enforcement for two things (#1732 review F5, correcting a prior
    /// version of this comment that claimed the opposite): (1) <c>AgyHookCheckCommand</c>'s
    /// workspace-or-outbox write bound, which applies to every write-family tool call REGARDLESS of
    /// <see cref="PermissionGrant.WriteFiles"/> — nothing agy itself offers bounds where a write lands
    /// (<c>agy.plan-mode-does-not-deny-writes</c>); and (2) <see cref="TryTranslatePermissionGrant"/>'s
    /// shell-pattern-scoped path, which reaches <c>--dangerously-skip-permissions</c> with no
    /// requirement that <see cref="PermissionGrant.ShellCommandPatterns"/> or
    /// <see cref="PermissionGrant.DeniedShellCommandPatterns"/> be non-empty — so a grant carrying
    /// either list has the hook as the only thing enforcing it. Probed whenever either applies:
    /// writes or network withheld, OR a shell/deny pattern list is present.
    /// </summary>
    internal static bool RequiresHookAsSoleNarrowing(string permissionScope, PermissionGrant? grant) =>
        permissionScope == "--dangerously-skip-permissions"
        && grant is { } g
        && (!g.WriteFiles
            || !g.NetworkAccess
            || g.ShellCommandPatterns is { Count: > 0 }
            || g.DeniedShellCommandPatterns is { Count: > 0 });

    internal static string BuildShellPatterns(PermissionGrant? grant)
    {
        return grant?.ShellCommandPatterns is { Count: > 0 } patterns
            ? string.Join(',', patterns)
            : string.Empty;
    }

    /// <summary>
    /// The standing "never" families for <see cref="DeniedShellPatternsVariable"/> — comma-joined,
    /// empty when none. Mirror of <see cref="BuildShellPatterns"/> over
    /// <see cref="PermissionGrant.DeniedShellCommandPatterns"/> (0022's DenyAlways rung, #390).
    /// </summary>
    internal static string BuildDeniedShellPatterns(PermissionGrant? grant)
    {
        return grant?.DeniedShellCommandPatterns is { Count: > 0 } patterns
            ? string.Join(',', patterns)
            : string.Empty;
    }

    /// <summary>
    /// The standing denied option tokens for <see cref="DeniedShellOptionTokensVariable"/> —
    /// comma-joined, empty when none. Mirror of <see cref="BuildDeniedShellPatterns"/> over
    /// <see cref="PermissionGrant.DeniedShellOptionTokens"/> (#1683 F2).
    /// </summary>
    internal static string BuildDeniedShellOptionTokens(PermissionGrant? grant)
    {
        return grant?.DeniedShellOptionTokens is { Count: > 0 } tokens
            ? string.Join(',', tokens)
            : string.Empty;
    }

    public bool TryTranslatePermissionGrant(PermissionGrant grant, out string? resolvedValue, out string? gapReason)
    {
        ArgumentNullException.ThrowIfNull(grant);

        // #659: agy shell grants scoped by PermissionGrant.ShellCommandPatterns are enforced by
        // AER's PreToolUse hook (AgyHookCheckCommand) via ShellCommandPatternMatcher and the
        // BATON_HOOK_SHELL_PATTERNS environment variable.


        if (grant.RunShellCommands && grant.NetworkAccess)
        {
            resolvedValue = "--dangerously-skip-permissions";
            gapReason = null;
            return true;
        }

        if (grant.RunShellCommands)
        {
            // #1387: a pattern-scoped shell grant (RunShellCommands=true, NetworkAccess=false, a
            // non-empty ShellCommandPatterns) now defers to the hook instead of refusing --
            // --dangerously-skip-permissions still turns run_command on at all headlessly, and the
            // hook (AgyHookCheckCommand) does the actual narrowing. Full measurement: spec/baton.md
            // §9's "agy now expresses this too" paragraph and docs/vendor-doc-audit.md's dated entry
            // -- not restated here. An unscoped shell grant (no patterns) is unchanged: nothing would
            // bound an unscoped --dangerously-skip-permissions shell, so it still refuses.
            //
            // This branch does not read grant.WriteFiles, so a write-granted, pattern-scoped shell
            // grant (WriteFiles=true, RunShellCommands=true, NetworkAccess=false, patterns non-empty)
            // also defers here rather than refusing. That is intentional, not an oversight (#1387
            // review, F8): writes on that path are still bounded to workspace-or-outbox by
            // AgyHookCheckCommand's write-family path check, the same bound applied when WriteFiles is
            // withheld entirely.
            if (grant.ShellCommandPatterns is { Count: > 0 })
            {
                resolvedValue = "--dangerously-skip-permissions";
                gapReason = null;
                return true;
            }

            resolvedValue = null;
            gapReason = "agy only supports auto-approving shell command execution via " +
                "--dangerously-skip-permissions, which also grants network access. Granting unrequested " +
                "network access would over-grant permissions. Use the Advanced raw permission-scope field instead.";
            return false;
        }

        if (grant.NetworkAccess)
        {
            resolvedValue = null;
            gapReason = "agy only supports auto-approving network access via " +
                "--dangerously-skip-permissions, which also grants shell command execution. Granting " +
                "unrequested shell execution would over-grant permissions. Use the Advanced raw permission-scope field instead.";
            return false;
        }

        resolvedValue = grant.WriteFiles ? "accept-edits" : grant.ReadFiles ? "plan" : "default";
        gapReason = null;
        return true;
    }

    public CoreDispatchTarget Resolve(WorkerInvocation invocation, WorkerContract contract)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(contract);

        // #1166 -- see ClaudeWorkerAdapter.Resolve's identical seam (ProjectCeilingGate's own doc has
        // the rule) for why this runs first on that adapter; the same ordering holds here for the same
        // reason, applied to this vendor's own downstream readers: ResolvePermissionScope, the
        // hook-liveness probe below, and every denied-tool env var.
        invocation = ProjectCeilingGate.Apply(invocation, contract, ((IWorkerAdapter)this).WithheldWritesReachTheOutbox);

        var isWindows = OperatingSystem.IsWindows();
        var prompt = BuildPrompt(invocation.PromptTemplate, contract, isWindows);
        var permissionScope = ResolvePermissionScope(invocation);
        var artifactsRoot = EnvironmentReference("BATON_ARTIFACTS_ROOT", isWindows);
        var agyWorkspace = EnsureAgyWorkspace();

        // #1680: for a grant whose only narrowing IS the hook, confirm the hook is actually live
        // before dispatching. Fails closed: any probe outcome other than an explicit `deny` refuses
        // the dispatch -- see AgyHookUnverifiedException for why that is the safe direction here.
        // #1732 review WIRING: the SAME predicate result also decides whether the returned
        // CoreDispatchTarget carries a live CountHookVerdicts delegate below -- the resolve-time probe
        // and the per-execution canary are the two guards for the identical shape (spec/baton.md §9),
        // so they share one computation of "does this dispatch need either".
        var requiresHookAsSoleNarrowing = RequiresHookAsSoleNarrowing(permissionScope, invocation.PermissionGrant);
        if (requiresHookAsSoleNarrowing)
        {
            // #1732 review N5, ruled fail closed: the per-execution canary (CountHookVerdicts below)
            // derives toolCallCount entirely from stream-json step_update lines -- a StreamJson:false
            // binding emits none, so a hook that dies after this probe would be caught by nothing for
            // that role's whole lifetime, silently, for as long as the binding exists. Refused here the
            // same way WorkerBindingResolver refuses other incoherent grant shapes, rather than shipping
            // a hole the operator has no way to see.
            if (!invocation.StreamJson)
            {
                throw new AgyCanaryRequiresStreamJsonException();
            }

            var hookAssemblyPath = Path.Combine(AppContext.BaseDirectory, "Baton.Cli.dll");
            var probeResult = _hookLivenessProbe.Probe(hookAssemblyPath, TimeSpan.FromSeconds(HookTimeoutSeconds));
            if (!probeResult.IsLive)
            {
                throw new AgyHookUnverifiedException(hookAssemblyPath, probeResult.Detail);
            }
        }

        List<string> args = ["-p", prompt];

        if (permissionScope == "--dangerously-skip-permissions")
        {
            args.Add("--dangerously-skip-permissions");
        }
        else
        {
            args.Add("--mode");
            args.Add(permissionScope);
        }

        args.Add("--add-dir");
        args.Add(artifactsRoot);

        // #554: decision 0029's mandatory PreToolUse gate. agy discovers hooks ONLY from a directory
        // named by --add-dir -- measured by `agy.hooks-load-from-add-dir-not-only-cwd` in the
        // arrangement AER actually ships (hook directory != cwd), where the cwd arm loaded
        // NOTHING. So this flag is what
        // loads the gate -- not a convenience. Unconditional, matching #543's claude side: the hook
        // ships on every worker, not only on workers whose flows declare a gate, because a gate that
        // is only sometimes installed cannot be relied upon by anything.
        args.Add("--add-dir");
        args.Add(agyWorkspace);

        // #491: bind the room's own directory explicitly. `agy -p` **ignores the process working
        // directory** — measured in #472 and recorded in docs/vendor-capabilities.md: launched from
        // this repo, which is listed in the CLI's own `trustedWorkspaces`, the emitted command still
        // carried `"Cwd":"C:\\Users\\...\\.gemini\\antigravity-cli"`. From an untrusted directory it
        // used the CLI's scratch dir and, unable to find a file sitting in the launch directory,
        // began a recursive search of the entire home folder. Workspace trust does not change it.
        //
        // So passing `invocation.WorkingDirectory` to CoreDispatchTarget below is necessary and NOT
        // sufficient — that sets the process cwd, which this vendor disregards. Without this the
        // worker cannot see the project at all, and the failure is silent: it answers confidently
        // about a directory that is not yours. `--add-dir` is repeatable on `agy`, so this composes
        // with the artifacts root above rather than replacing it.
        if (!string.IsNullOrWhiteSpace(invocation.WorkingDirectory))
        {
            args.Add("--add-dir");
            args.Add(invocation.WorkingDirectory);
        }

        // #801: agy has no per-invocation flag equivalent to claude's --mcp-config (decision 0035),
        // so a real workspace directory carrying .agents/mcp_config.json has to exist on disk for
        // --add-dir to point at. Opt-in only, so a dispatch that does not ask for it keeps today's
        // exact argv.
        if (invocation.EnableMemoryProposalTool)
        {
            args.Add("--add-dir");
            args.Add(EnsureMemoryProposalWorkspace());
        }

        if (invocation.SessionId is not null && invocation.ResumeSession)
        {
            args.Add("--conversation");
            args.Add(invocation.SessionId);
        }

        if (invocation.LogFilePath is not null)
        {
            args.Add("--log-file");
            args.Add(invocation.LogFilePath);
        }

        // #1088: structured streaming, mirroring claude's `if (invocation.StreamJson)` — but with agy's
        // OWN grammar. agy emits `--output-format stream-json` and, critically, does NOT take claude's
        // `--verbose`: agy rejects it (exit 2), so mirroring claude's argv verbatim would break every agy
        // run. The prompt is already the `-p` value above (agy's flag-value grammar, #491), so nothing
        // here re-passes it. Unconditional min-version posture matches the rest of this method (the hook,
        // `--conversation`, `--print-timeout` are all emitted without a version probe); measured on agy
        // 1.1.11 (docs/vendor-capabilities.md). The daemon turns StreamJson on for agy's interactive turn;
        // the dispatch path rides #1089.
        if (invocation.StreamJson)
        {
            args.Add("--output-format");
            args.Add("stream-json");
        }

        if (invocation.Model is not null)
        {
            args.Add("--model");
            args.Add(invocation.Model);
        }

        if (invocation.Effort is { } effort)
        {
            // #1318: resolve 0023's canonical word (quick/standard/careful/exhaustive) to agy's raw
            // value first -- careful and exhaustive both collapse to high, a disclosed collapse per
            // docs/vendor-capabilities.md -- then run the EXISTING model-suffix reconciliation (#1090)
            // against the resolved raw value, exactly as it already ran against a raw Effort before
            // this field's domain widened.
            var resolvedEffort = EffortTierMapping.ResolveForAgy(effort);
            ReconcileAgyEffort(invocation.Model, resolvedEffort);
            args.Add("--effort");
            args.Add(resolvedEffort);
        }
        else if (RequiresAgyEffort(invocation.Model))
        {
            // #1596: a suffix-less gemini model (e.g. `gemini-3.7-flash`) reaches agy itself and is
            // refused there -- paying for a full spawn first. Refuse up-front instead, naming the
            // model exactly as agy's own refusal does. The available set printed here is the global
            // one (AgyEffortValues), not enumerated per model: docs/vendor-capabilities.md's "agy
            // models" section already records that the grid has holes (`gemini-3.1-pro` has no
            // `medium`), so this message can overstate a narrower model's real set -- see the PR body.
            throw new IncoherentVendorEffortException(
                "agy",
                $"--model {invocation.Model} requires --effort (available: low, medium, high).");
        }

        if (invocation.Timeout is { } timeout)
        {
            args.Add("--print-timeout");
            args.Add(FormatPrintTimeout(timeout));
        }

        var environment = new List<(string Name, string Value)>
        {
            // Read by `baton agy-hook-check` inside the hook subprocess. Always set, even when
            // empty, so the value is AER's rather than whatever the operator's environment
            // happened to carry. It does NOT currently make "nothing withheld" distinguishable
            // from "the list never arrived" -- the command collapses absent and empty to the
            // same allow. See #600.
            (DeniedToolsVariable, $"{DeniedToolsVendorTag}:{BuildDeniedTools(invocation.PermissionGrant, invocation.AllowsSubagents)}"),
            (ShellPatternsVariable, $"{ShellPatternsVendorTag}:{BuildShellPatterns(invocation.PermissionGrant)}"),
            (DeniedShellPatternsVariable, $"{ShellPatternsVendorTag}:{BuildDeniedShellPatterns(invocation.PermissionGrant)}"),
            (DeniedShellOptionTokensVariable,
                $"{ShellPatternsVendorTag}:{BuildDeniedShellOptionTokens(invocation.PermissionGrant)}"),
        };

        // #1680 (F2, #1732 review): the first-verdict canary's write side. Per-EXECUTION, not
        // per-room: this method (Resolve) runs once per binding-config entry (WorkerInvocation's own
        // doc, WorkerBindingResolver.cs:146), so a directory computed here -- BindingsFileDirectory or
        // WorkingDirectory, both room-scoped -- would be shared by every dispatch of this role and
        // every other agy role in the room, for the whole run. An append-only file at that scope makes
        // hookVerdictCount == 0 unreachable after the first verdict anywhere in the room, disarming the
        // canary permanently. Instead this emits an environment-variable REFERENCE
        // (WorkerInvocation.cs:9-19's sanctioned escape hatch for per-execution dynamism, the same
        // mechanism BATON_ARTIFACTS_ROOT above uses) pointing at BATON_OUTPUT_DIR -- the per-execution
        // directory CoreDispatcher only resolves at dispatch time (CoreDispatcher.AssembleChildEnvironment
        // expands target.Environment values against it) and the same directory OutcomeClassifier.Classify
        // is already handed as outputDirectory. Unconditional, like BATON_ARTIFACTS_ROOT above: the
        // Artifact Manager always resolves BATON_OUTPUT_DIR (ArtifactManager.cs:216), so there is no
        // "neither directory known" case here the way there was for the room-scoped fallback.
        environment.Add((
            VerdictLedgerVariable,
            EnvironmentReference("BATON_OUTPUT_DIR", isWindows) + (isWindows ? @"\" : "/") + VerdictLedgerFileName));

        // agy home redirect (#442): non-shell bindings get HOME and USERPROFILE redirected to an
        // AER-owned state directory. Shell-granted workers (grant.RunShellCommands == true) are
        // deliberately NOT redirected so worker git commit can see the user's .gitconfig.
        string? agyHome = null;
        if (invocation.PermissionGrant is { RunShellCommands: false })
        {
            var isDaemonSession = invocation.SessionId is not null || invocation.ResumeSession ||
                (invocation.BindingsFileDirectory is not null && InteractiveSessionMaterializer.ReadRoomKind(invocation.BindingsFileDirectory) == RoomKind.Interactive);

            if (isDaemonSession)
            {
                var sessionDir = invocation.BindingsFileDirectory ?? invocation.WorkingDirectory;
                if (sessionDir is not null)
                {
                    agyHome = Path.Combine(sessionDir, ".gemini_home");
                }
            }
            else
            {
                agyHome = EnvironmentReference("BATON_OUTPUT_DIR", isWindows) + (isWindows ? @"\.gemini_home" : "/.gemini_home");
            }

            if (agyHome is not null)
            {
                environment.Add(("HOME", agyHome));
                environment.Add(("USERPROFILE", agyHome));
            }
        }

        // #679; see WorkerEnvironment.WorkspaceVariable. Load-bearing on this vendor rather than
        // merely useful, for the reason AgyHookCheckCommand's own bound gives.
        if (invocation.WorkingDirectory is { } workspace)
        {
            environment.Add((WorkerEnvironment.WorkspaceVariable, workspace));
        }

        // #1084: under `--mode accept-edits` agy headless-DENIES a write tool because it cannot
        // prompt (measured: the dispatch stderr says so verbatim, "add an allow-rule under
        // permissions.allow ... e.g. write_file(<target>)"), so a write-granted advise/orchestrate
        // role produces no output. The fix is a `permissions.allow` rule in the fresh AER-owned home
        // agy already runs under here -- the write_file category proven honoured under `-p` by
        // `agy.settings-allow-write-honoured-headless` (its command(...) sibling proves only that the
        // allow LIST loads, a weaker claim). Gated on agyHome being non-null: that is exactly
        // the AER-owned redirected home. When it is null the run uses the operator's real ~/.gemini,
        // which AER must never write into (Credential Isolation) -- and that path is the raw-scope
        // case with no grant, not the dispatch front door this fixes. The rule is least-privilege,
        // one per declared output, and is NOT the security boundary: AER's PreToolUse hook still
        // bounds where the write may land -- agy.hook-deny-holds-under-the-mode-production-uses
        // measures that a deny holds under this exact --mode accept-edits (#670).
        IReadOnlyList<CoreDispatchSeedFile>? seedFiles = null;
        if (permissionScope == "accept-edits" && agyHome is not null && contract.ProducedOutputs.Count > 0)
        {
            var outputDir = EnvironmentReference("BATON_OUTPUT_DIR", isWindows);
            var allow = contract.ProducedOutputs
                .Select(output => $"write_file({outputDir}/{output.Name})")
                .ToArray();
            var settings = JsonSerializer.Serialize(new { permissions = new { allow } });
            var settingsPath = Path.Combine(agyHome, ".gemini", "antigravity-cli", "settings.json");
            seedFiles = [new CoreDispatchSeedFile(settingsPath, settings)];
        }

        return new CoreDispatchTarget(
            "agy", [.. args], invocation.WorkingDirectory, PromptText: prompt,
            Environment: [.. environment], OversizePromptWrapper: OversizePromptWrapperText,
            SeedFiles: seedFiles,
            // #1089: only when streaming is there a `result` event on stdout to detect; in text mode the
            // stdout is the answer, so wiring the detector would just scan prose for nothing. Null there
            // keeps the guard failing safe.
            DetectsTerminalSuccess: invocation.StreamJson ? IsTerminalSuccessLine : null,
            // F6 (#1593 review): same streaming gate as DetectsTerminalSuccess above, but fires on a
            // terminal `result` event of ANY status — see IsTerminalResultLine.
            DetectsTerminalResult: invocation.StreamJson ? IsTerminalResultLine : null,
            // #1732 review WIRING: the per-execution canary's read side, wired only when
            // requiresHookAsSoleNarrowing (computed above, same value the resolve-time probe already
            // gated on) held. Null otherwise -- see CoreDispatchTarget.CountHookVerdicts's own doc for
            // which dispatches that covers and what it keeps unchanged.
            CountHookVerdicts: requiresHookAsSoleNarrowing
                ? outputDirectory => AgyHookVerdictLedger.CountVerdicts(Path.Combine(outputDirectory, VerdictLedgerFileName))
                : null,
            // #1741: see ExecutionRequest.HookVerdictLedgerFileName's own doc for why this travels
            // alongside CountHookVerdicts above.
            HookVerdictLedgerFileName: requiresHookAsSoleNarrowing ? VerdictLedgerFileName : null);
    }

    /// <summary>
    /// True iff <paramref name="rawLine"/> is agy's terminal success marker:
    /// <c>{"event":"result","result":{"status":"SUCCESS",…}}</c> (#1089). A non-SUCCESS status, a
    /// non-result event, or a chunk-split line is not one. This is the ONE agy fact the #1089 guard
    /// rests on, so it is asserted against a real captured line in the adapter tests.
    /// </summary>
    internal static bool IsTerminalSuccessLine(string rawLine)
    {
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawLine);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("event", out var eventProp) && eventProp.GetString() == "result"
                && root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("status", out var status) && status.GetString() == "SUCCESS";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// F6 (#1593 review): true iff <paramref name="rawLine"/> is agy's terminal `result` marker of ANY
    /// status — the same envelope <see cref="IsTerminalSuccessLine"/> matches, minus that method's
    /// <c>status == "SUCCESS"</c> clause. Distinguishes "the worker finished and self-reported a
    /// FAILURE" (a contract failure with a result record) from "the worker died mid-stream with no
    /// result at all" (a dead worker) — <see cref="Outcomes.OutcomeClassifier"/>'s dead-worker predicate
    /// needs exactly this fact, which <see cref="IsTerminalSuccessLine"/> cannot supply since it reads
    /// false in both cases.
    /// </summary>
    internal static bool IsTerminalResultLine(string rawLine)
    {
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawLine);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("event", out var eventProp) && eventProp.GetString() == "result"
                && root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Creates the AER-owned agy workspace and rewrites its <c>.agents/hooks.json</c> with canonical
    /// content, returning the directory to hand to <c>--add-dir</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Left holding canonical content on every resolve, never merely created-if-absent</b>, for the
    /// reason #543 gives on the claude side: a stale file left by an earlier build would silently
    /// disable the gate for good on any machine that ran that build once. It also means a worker that
    /// tampered with the file cannot carry that into the next spawn — #667 skips the write when the
    /// file already matches, which does not weaken that, because a tampered file differs and is
    /// therefore still rewritten. The directory is entirely AER-owned, so there is no operator content
    /// for the rewrite to destroy.
    /// </para>
    /// <para>
    /// <b>Never the operator's own <c>~/.gemini/config/</c></b>, which is agy's other documented
    /// hooks location. Writing there would put AER's configuration inside the user's own vendor
    /// config — the boundary CLAUDE.md's Credential Isolation rule draws, and the same reason
    /// <c>agy.permissions-are-global-only</c> is recorded as a limitation rather than used as a
    /// mechanism.
    /// </para>
    /// </remarks>
    private static string EnsureAgyWorkspace()
    {
        var workspace = Path.Combine(BatonPaths.WorkerLaunchConfig, AgyWorkspaceDirectoryName);
        Directory.CreateDirectory(Path.Combine(workspace, ".agents"));
        AtomicLaunchConfigWriter.Write(Path.Combine(workspace, ".agents", "hooks.json"), BuildHooksJson());
        return workspace;
    }

    /// <summary>
    /// The name of the workspace directory AER points every memory-proposal-opted-in agy worker at,
    /// holding the <c>.agents/mcp_config.json</c> naming AER's own MCP server (#585, #801). Separate
    /// from <see cref="AgyWorkspaceDirectoryName"/> for the same reason that one is separate from
    /// <see cref="BatonPaths.WorkerLaunchConfig"/>'s root: <c>--add-dir</c> grants file access to
    /// whatever it names, and this tool is opt-in, so its workspace should not be reachable (or
    /// grant reach) on a dispatch that never asked for it.
    /// </summary>
    public const string MemoryProposalWorkspaceDirectoryName = "agy-memory-proposal-workspace";

    /// <summary>
    /// Creates the AER-owned workspace opted-in agy dispatches use for the memory-proposal MCP
    /// server and rewrites its <c>.agents/mcp_config.json</c> with canonical content (#801), mirroring
    /// <see cref="EnsureAgyWorkspace"/>'s own left-holding-canonical-content convention and its reasons.
    /// </summary>
    /// <remarks>
    /// <b>Carries no capture-directory path (#833)</b> -- same reason and mechanism as
    /// <see cref="ClaudeWorkerAdapter.EnsureMemoryProposalMcpConfig"/>'s own remarks, which are
    /// canonical; this side differs only in which vendor process <c>baton mcp</c> inherits
    /// <c>BATON_OUTPUT_DIR</c> from (<c>agy</c> here, <c>claude</c> there). #1458: same
    /// <c>mcp</c>-verb-plus-<see cref="File.Exists"/>-guard fix as that method, for the identical
    /// fail-open-and-silent reason (#530) -- doubly so here, since agy is the vendor whose own
    /// hook-check fails open on a bad path.
    /// </remarks>
    private static string EnsureMemoryProposalWorkspace()
    {
        var workspace = Path.Combine(BatonPaths.WorkerLaunchConfig, MemoryProposalWorkspaceDirectoryName);
        Directory.CreateDirectory(Path.Combine(workspace, ".agents"));

        var hostDllPath = Path.Combine(AppContext.BaseDirectory, "Baton.Cli.dll");
        if (!File.Exists(hostDllPath))
        {
            throw new InvalidOperationException(
                $"Cannot write the memory-proposal MCP config (#801): '{hostDllPath}' does not exist. " +
                "Every deployment of baton must carry Baton.Cli.dll alongside its own binary -- an MCP " +
                "config naming a path that does not exist fails open and silently (#530), so this fails " +
                "loudly here instead, before any worker is dispatched.");
        }

        var json = JsonSerializer.Serialize(new
        {
            mcpServers = new Dictionary<string, object>
            {
                ["baton-memory-proposal"] = new
                {
                    command = "dotnet",
                    args = new[] { hostDllPath, "mcp", "--memory-proposal-tool" },
                },
            },
        });

        AtomicLaunchConfigWriter.Write(Path.Combine(workspace, ".agents", "mcp_config.json"), json);
        return workspace;
    }

    /// <summary>
    /// The <c>.agents/hooks.json</c> content #554 ships: one <c>PreToolUse</c> handler matching
    /// every tool, invoking <c>baton agy-hook-check</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three shape details that are each a measured or documented constraint rather than a style
    /// choice. <b>Hooks are keyed by an arbitrary name at the root</b> — unlike claude's settings
    /// file, which nests them under a <c>hooks</c> key. <b>The matcher is a regex over agy's own
    /// tool names</b>, so <c>"*"</c> here means every tool, and a claude tool name would match
    /// nothing. <b>There is no exec form</b>: agy documents only a single <c>command</c> string
    /// (<c>.vendor-survey/corpus/agy__hooks-embedded.md</c>), where claude's handler accepts an
    /// <c>args</c> array that bypasses shell parsing entirely. That last one is why the path's
    /// spelling is load-bearing rather than cosmetic — see <see cref="HookAssemblyToken"/>, which
    /// owns every constraint on it.
    /// </para>
    /// <para>
    /// Invoked as <c>dotnet &lt;Baton.Cli.dll&gt;</c> rather than a native apphost, for the deployment
    /// reason <see cref="ClaudeWorkerAdapter"/> documents at length: a packed <c>dotnet tool</c> has
    /// no apphost, and naming one would write a dangling command into every worker's hook. On agy
    /// that failure is worse than on claude — an unparseable or absent hook response is read as an
    /// <em>allow</em> (<c>agy.hook-malformed-stdout-fails-open</c>), so a hook that cannot start
    /// does not fail loudly, it fails open. Hence the explicit existence guard.
    /// </para>
    /// </remarks>
    private static string BuildHooksJson()
    {
        var hookAssemblyPath = Path.Combine(AppContext.BaseDirectory, "Baton.Cli.dll");
        if (!File.Exists(hookAssemblyPath))
        {
            throw new InvalidOperationException(
                $"Cannot write the mandatory PreToolUse hook (decision 0029): '{hookAssemblyPath}' " +
                "does not exist. Every deployment of baton/Baton.Daemon must carry Baton.Cli.dll alongside " +
                "its own binary -- on agy a hook that cannot start is read as an ALLOW rather than " +
                "an error (agy.hook-malformed-stdout-fails-open), so this fails loudly here instead, " +
                "before any worker is dispatched.");
        }

        var command = BuildHookCommand(hookAssemblyPath);
        var hooks = new Dictionary<string, object>
        {
            ["baton-permission-gate"] = new
            {
                PreToolUse = new[]
                {
                    new
                    {
                        matcher = "*",
                        hooks = new[]
                        {
                            new { type = "command", command, timeout = HookTimeoutSeconds },
                        },
                    },
                },
            },
        };

        return JsonSerializer.Serialize(hooks);
    }

    /// <summary>
    /// The hook command string, shared by <see cref="BuildHooksJson"/> (what's written into
    /// <c>hooks.json</c>) and <see cref="IAgyHookLivenessProbe"/> (what the resolve-time probe
    /// spawns) -- #1732 review N1: previously interpolated independently in both places, with only
    /// <see cref="HookAssemblyToken"/>'s escaping shared, so a change to one could drift from the
    /// other with nothing to notice. A probe spawning a stale command would keep reporting the hook
    /// live while <c>hooks.json</c>'s real command silently changed underneath it.
    /// </summary>
    internal static string BuildHookCommand(string hookAssemblyPath) =>
        $"dotnet {HookAssemblyToken(hookAssemblyPath)} agy-hook-check";

    /// <summary>
    /// How the assembly path is spelled inside the hook command string, so agy's shell resolves it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>agy runs the command through a shell</b> — <c>sh -c</c> on Unix, <c>cmd /c</c> on Windows —
    /// stated in agy's own embedded specification; where that came from and what else it says is
    /// recorded in <c>docs/vendor-doc-audit.md</c>. Which shell it is decides everything
    /// below, and getting it wrong is what #710 was: this shipped a single-quoted path, and
    /// <c>cmd</c> does not treat <c>'</c> as a quoting character, so <c>dotnet</c> received a literal
    /// <c>'C:/…/Baton.Cli.dll'</c>, failed to find it, and wrote nothing to stdout. Per
    /// <c>agy.hook-malformed-stdout-fails-open</c> that is an <b>allow</b>, so decision 0029's
    /// mandatory gate has never fired on a Windows agy worker.
    /// </para>
    /// <para>
    /// <b>On Windows the token must be bare, and therefore free of anything <c>cmd</c> splits on.</b>
    /// Three constraints decide the shape. The first two were measured end to end through agy against
    /// an install directory containing a space, each with a control that failed; the third is a .NET
    /// assembly-resolution fact, measured by launching <c>dotnet</c> directly:
    /// <list type="number">
    /// <item>Quoting never helps. Single and double quotes both leave the path unresolved.</item>
    /// <item>A bare path containing a space resolves when it is the whole command and fails as soon
    /// as an argument follows it — and the real command always has <c>agy-hook-check</c> after it.</item>
    /// <item>Shortening the <b>directory</b> only. <c>GetShortPathName</c> over the full path also
    /// 8.3-truncates the file name itself (it was measured yielding <c>AERCLI~1.DLL</c> back when this
    /// assembly was named <c>Aer.Cli.dll</c>; the mechanism, not that literal string, is what matters
    /// post-rename), and .NET's assembly resolution is name-based: it then looks for a matching
    /// <c>~1.deps.json</c>, does not find it, and the handler dies with <c>0x80008083</c>.
    /// Keeping the real file name under an 8.3 directory is both space-free and resolvable.</item>
    /// </list>
    /// A relative name is not an option either, despite agy setting the hook's working directory to
    /// the directory holding <c>hooks.json</c> (verified true): <c>cmd /c &lt;relative name&gt;</c>
    /// does not resolve even with the file sitting in that directory.
    /// </para>
    /// <para>
    /// Only the <b>space</b> was measured through agy. The other characters
    /// <see cref="CmdSplitsBareTokensOn"/> guards are read from <c>cmd</c>'s grammar, not measured:
    /// <c>&amp;</c> and <c>^</c> are operators and <c>,</c> <c>;</c> <c>=</c> are argument delimiters,
    /// all legal in a Windows directory name, and any of them mid-token turns the command into one
    /// that never starts — which on this vendor is an <em>allow</em>. Routing them through the same
    /// 8.3 step errs fail-closed: at worst a path that might have worked is shortened or refused
    /// loudly, never silently emitted broken.
    /// </para>
    /// <para>
    /// <b>Non-Windows keeps single quotes</b>, which is what <c>sh -c</c> strips by POSIX grammar,
    /// and where a space then needs no special handling. Read from the embedded spec and from POSIX;
    /// not yet measured through agy on a Unix host — no such host has run these probes. The 8.3 step
    /// is a Windows mechanism and is scoped to Windows rather than applied as a general rule.
    /// </para>
    /// <para>
    /// <b>Why this throws rather than falling back.</b> 8.3 name generation can be disabled per
    /// volume, and then <c>GetShortPathName</c> returns the long path unchanged and no working
    /// command exists. Emitting one anyway would produce a hook that cannot start, which on this
    /// vendor is an allow — the exact failure this method exists to prevent. Failing here is loud and
    /// happens before any worker is dispatched.
    /// </para>
    /// </remarks>
    internal static string HookAssemblyToken(string hookAssemblyPath)
    {
        // Forward slashes throughout: the command is shell-parsed, and a Windows path's `\U`, `\t`
        // and friends are escape sequences to `sh`.
        var path = hookAssemblyPath.Replace('\\', '/');

        if (!OperatingSystem.IsWindows())
        {
            return $"'{path}'";
        }

        if (path.IndexOfAny(CmdSplitsBareTokensOn) < 0)
        {
            return path;
        }

        var shortened = ShortDirectoryPath(hookAssemblyPath);
        if (shortened is null || shortened.IndexOfAny(CmdSplitsBareTokensOn) >= 0)
        {
            throw new InvalidOperationException(
                $"Cannot write the mandatory PreToolUse hook (decision 0029): '{hookAssemblyPath}' " +
                "contains a character `cmd` splits a bare token on (a space, or one of `& ^ , ; =`), " +
                "and agy runs the hook command through `cmd /c`, which resolves neither a quoted " +
                "path nor a bare one containing such a character. The usual remedy -- the 8.3 short " +
                "name of the containing directory -- did not produce a clean name here: either 8.3 " +
                "name generation is disabled on that volume (`fsutil 8dot3name query <drive>`), or " +
                "the short form itself still carries the character. AER will not emit a hook command " +
                "it has measured cannot start: on agy a hook that cannot start is read as an ALLOW " +
                "(agy.hook-malformed-stdout-fails-open), so a silent fallback would be an ungated " +
                "worker. Install AER under a plain path, or re-enable 8.3 name generation on that " +
                "volume.");
        }

        return shortened.Replace('\\', '/');
    }

    /// <summary>
    /// Characters that break a bare <c>cmd</c> token mid-path: the space (measured through agy — see
    /// <see cref="HookAssemblyToken"/>), and cmd's operators and argument delimiters that are legal
    /// in Windows directory names (read from cmd's grammar, not measured). Deliberately not a wider
    /// net: every character here forces the 8.3 detour and, when the short form still carries it, a
    /// hard refusal — so listing a character that is actually harmless would turn a working install
    /// into a refused one.
    /// </summary>
    private static readonly char[] CmdSplitsBareTokensOn = [' ', '&', '^', ',', ';', '='];

    /// <summary>
    /// The path with its directory replaced by that directory's 8.3 short form, keeping the real file
    /// name. Returns <see langword="null"/> when Windows reports no short name.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static string? ShortDirectoryPath(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        var buffer = new char[MaxExtendedPath];
        var written = GetShortPathNameW(directory, buffer, (uint)buffer.Length);

        // 0 is the documented failure return; a value at or past the buffer length means the call
        // wanted more room than it was given, and neither result is a usable path.
        if (written == 0 || written >= buffer.Length)
        {
            return null;
        }

        return Path.Combine(new string(buffer, 0, (int)written), Path.GetFileName(path));
    }

    /// <summary>Room for a Windows extended-length path, which is what the short-name API can return.</summary>
    private const int MaxExtendedPath = 32768;

    // DllImport rather than LibraryImport: the source-generated form requires AllowUnsafeBlocks on
    // the whole project, and enabling unsafe code across Baton.Vendors to spell one path is a far
    // wider change than this call is worth. Nothing here is hot -- it runs once per worker spawn.
    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = CharSet.Unicode,
               ExactSpelling = true, SetLastError = true)]
    private static extern uint GetShortPathNameW(string longPath, [Out] char[] shortPath, uint bufferLength);

    /// <summary>
    /// Seconds agy waits for the hook before giving up. agy's documented default is 30; this is set
    /// explicitly rather than inherited so the value is visible next to the reasoning. Generous for
    /// what the command does (parse stdin, compare a name, print an object) because the cost of
    /// overrunning is asymmetric: a timeout produces no stdout, and no stdout is an
    /// <em>allow</em> on this vendor. For a role that relies on the hook as its sole narrowing —
    /// <c>review</c> is the first such role — a hook that cannot start therefore turns the most
    /// restricted agy role into an unscoped shell with network and unbounded writes; a liveness
    /// guard for that failure mode is tracked in #1680, not built here (#1387 review, F5).
    /// </summary>
    private const int HookTimeoutSeconds = 30;


    /// <summary>
    /// A structured <see cref="WorkerInvocation.PermissionGrant"/> always wins over the raw
    /// <see cref="WorkerInvocation.PermissionScope"/> string (<see cref="PermissionGrant"/>'s own
    /// docs record this precedence).
    /// </summary>
    /// <exception cref="PermissionGrantUnsupportedException">
    /// <paramref name="invocation"/> carries a <see cref="WorkerInvocation.PermissionGrant"/> that
    /// <see cref="TryTranslatePermissionGrant"/> refuses (e.g. requesting shell commands without network access, or vice versa).
    /// </exception>
    /// <summary>
    /// #1090: agy's <c>--effort</c> is one control with the model-name suffix and must agree (sentinel
    /// <c>effort.agy-effort-and-suffix-must-agree</c>), and its value set is exactly {low, medium, high}
    /// (sentinel <c>effort.agy-value-set</c> — that check is the tripwire if agy ever changes the set).
    /// Both are otherwise refused by agy at bind time, after the operator has waited; this refuses them
    /// up-front at resolution, naming the real cause. See <see cref="IncoherentVendorEffortException"/>.
    /// #1596's sibling check, <see cref="RequiresAgyEffort"/>, covers the third case this method
    /// cannot: an <see cref="WorkerInvocation.Effort"/> of <c>null</c>, when the model requires one.
    /// </summary>
    private static void ReconcileAgyEffort(string? model, string effort)
    {
        if (!AgyEffortValues.Contains(effort))
        {
            throw new IncoherentVendorEffortException(
                "agy", $"'{effort}' is not one of agy's values (low, medium, high).");
        }

        if (GeminiEffortSuffix(model) is { } suffix
            && !string.Equals(suffix, effort, StringComparison.OrdinalIgnoreCase))
        {
            throw new IncoherentVendorEffortException(
                "agy",
                $"model '{model}' already encodes effort '{suffix}', which conflicts with --effort '{effort}'. "
                + "On agy, effort is part of the model name; pass one, or make them agree.");
        }
    }

    private static readonly HashSet<string> AgyEffortValues =
        new(StringComparer.OrdinalIgnoreCase) { "low", "medium", "high" };

    /// <summary>
    /// The effort a gemini model name encodes as a trailing <c>-low|-medium|-high</c>, or null. Scoped
    /// to the measured gemini families: <c>gpt-oss-120b-medium</c>'s trailing <c>-medium</c> is part of
    /// the name and is not measured as an effort, so it is deliberately not treated as one (claim-scope).
    /// </summary>
    private static string? GeminiEffortSuffix(string? model)
    {
        if (model is null || !model.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        foreach (var value in AgyEffortValues)
        {
            if (model.EndsWith("-" + value, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>
    /// The bare (no <c>-low|-medium|-high</c> suffix) gemini model names <c>agy models</c> catalogues
    /// as having suffixed variants (docs/vendor-capabilities.md's "<c>agy models</c>" fence) -- i.e.
    /// the families the model-name/effort split actually applies to. Deliberately not "any
    /// <c>gemini-</c>-prefixed name": <c>gemini-3-pro</c> is NOT one of these families -- it is a
    /// separate, uncatalogued name that agy refuses for being unrecognized
    /// (<c>effort.agy-rejection-is-per-model</c>, same doc), not for a missing effort, and treating it
    /// as if it were regressed <c>AgyWorkerAdapterTests.A_model_is_passed_through_when_set</c> (found
    /// while fixing #1596, corrected here rather than filed separately per "found-while-fixing").
    /// </summary>
    private static readonly HashSet<string> AgyModelsRequiringEffortSuffix =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Mirrors the families in the fence (re-captured 2026-09-05, #1342).
            "gemini-3.8-flash", "gemini-3.7-flash", "gemini-3.6-flash", "gemini-3.1-pro",
        };

    /// <summary>
    /// True for a catalogued gemini model agy will refuse to spawn without an explicit
    /// <c>--effort</c> -- i.e. a bare <see cref="AgyModelsRequiringEffortSuffix"/> entry with no
    /// suffix already applied via <see cref="GeminiEffortSuffix"/>. #1596's own scope note says
    /// "whether every gemini model without an effort suffix requires --effort, or only some, is
    /// unmeasured" -- so this stays scoped to the exact families the catalogue shows carrying
    /// suffixed variants, rather than every <c>gemini-</c>-prefixed name (see
    /// <see cref="AgyModelsRequiringEffortSuffix"/>'s own remarks for why that would be too wide).
    /// A non-gemini model (claude, gpt-oss) or an uncatalogued one falls outside it and keeps today's
    /// behaviour -- no up-front check -- because whether it requires <c>--effort</c> is simply
    /// unmeasured, not measured-negative.
    /// </summary>
    private static bool RequiresAgyEffort(string? model) =>
        model is not null
        && GeminiEffortSuffix(model) is null
        && AgyModelsRequiringEffortSuffix.Contains(model);

    private string ResolvePermissionScope(WorkerInvocation invocation)
    {
        if (invocation.PermissionGrant is { } grant)
        {
            if (!TryTranslatePermissionGrant(grant, out var resolved, out var gapReason))
            {
                throw new PermissionGrantUnsupportedException("agy", gapReason!);
            }

            return resolved!;
        }

        return invocation.PermissionScope ?? DefaultPermissionScope;
    }

    private static string BuildPrompt(string promptTemplate, WorkerContract contract, bool isWindows)
    {
        var prompt = new StringBuilder(promptTemplate);

        if (contract.RequiredInputs.Count > 0)
        {
            prompt.Append("\n\nInputs, in the order listed, are available at these absolute paths:\n");
            for (var i = 0; i < contract.RequiredInputs.Count; i++)
            {
                prompt.Append($"- {contract.RequiredInputs[i]}: {EnvironmentReference($"BATON_INPUT_{i}", isWindows)}\n");
            }
        }

        if (contract.ProducedOutputs.Count > 0)
        {
            prompt.Append("\nWrite each of the following outputs to the exact absolute path shown, creating parent directories as needed:\n");
            foreach (var output in contract.ProducedOutputs)
            {
                var outputDir = EnvironmentReference("BATON_OUTPUT_DIR", isWindows);
                var separator = isWindows ? '\\' : '/';
                prompt.Append($"- {output.Name}: {outputDir}{separator}{output.Name}\n");
            }
        }

        prompt.Append($"\n\n{ForegroundGateInstructionText}");

        return prompt.ToString();
    }

    private static string EnvironmentReference(string name, bool isWindows) =>
        WorkerEnvironmentReference.For(name, isWindows);

    private static readonly TimeSpan DiscoverySubcommandTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Shells out to <c>agy models</c>, <c>agy agent</c>, and <c>agy plugin list</c> — the real
    /// subcommands the installed CLI exposes (confirmed against <c>agy --help</c>'s "Available
    /// subcommands" list) — rather than reporting a hardcoded, driftable model/agent list. Best
    /// effort: a subcommand that errors, times out, or isn't installed contributes nothing rather
    /// than fabricated data.
    /// </summary>
    public async Task<WorkerCapabilities> DiscoverCapabilitiesAsync(string? workingDirectory = null, CancellationToken cancellationToken = default)
    {
        var modelsOutput = RunAgySubcommandAsync(["models"], workingDirectory, cancellationToken);
        var agentsOutput = RunAgySubcommandAsync(["agent"], workingDirectory, cancellationToken);
        var pluginsOutput = RunAgySubcommandAsync(["plugin", "list"], workingDirectory, cancellationToken);
        await Task.WhenAll(modelsOutput, agentsOutput, pluginsOutput).ConfigureAwait(false);

        var items = new List<WorkerCapabilityItem>
        {
            new("/compact", "command", "Summarize and compact session history"),
            new("default", "mode", "Default non-interactive mode"),
            new("accept-edits", "mode", "Auto-accept file editing permissions"),
            new("plan", "mode", "Read-only planning mode"),
        };
        items.AddRange(ParseAgentLines(agentsOutput.Result));
        items.AddRange(ParsePluginLines(pluginsOutput.Result));

        if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
        {
            // #1512 M2: whether agy actually reads SKILL.md from this directory is UNMEASURED --
            // verify.py has no check for it and docs/decisions/0033-skills-attach-directly-no-persona.md
            // describes agy's skill equivalent as "the plugin/agent equivalent", not a SKILL.md
            // directory. Tracked by #1572. Not symmetric with claude's discovery either: no
            // user-personal arm, and no name-based dedup (see docs/dispatch.md's skill-roster section).
            var skillsDir = Path.Combine(workingDirectory, ".agents", "skills");
            items.AddRange(SkillScanner.DiscoverSkills(skillsDir));
        }

        return new WorkerCapabilities("agy", items, ParseModelLines(modelsOutput.Result));
    }

    /// <summary>
    /// Parses one line of `agy -p … --output-format stream-json` (#1088). agy's envelope is keyed on
    /// <c>"event"</c> — <c>init</c>, <c>step_update</c>, <c>result</c> — NOT claude's <c>"type"</c>, so
    /// this is a genuinely different parse, not a mirror of <see cref="ClaudeWorkerAdapter"/>. Confirmed
    /// against a live agy 1.1.11 run. Granularity is <b>step-level</b>: <c>step_update</c> is a heartbeat
    /// naming the current step (assistant/tool/…); the terminal <c>result</c> event carries either the
    /// full answer text (Kind <c>"text"</c>, <c>status: "SUCCESS"</c>) or, since #1561, a status/error
    /// summary (Kind <c>"result"</c>, any other <c>status</c> — the failure reason a non-streaming
    /// echo consumer needs). agy does not stream token-by-token deltas the way claude's
    /// <c>--include-partial-messages</c> does. A line split across a stdout chunk boundary throws
    /// <see cref="JsonException"/> and is treated as "not a progress event", exactly as the claude parser
    /// does; the daemon's line assembler delivers whole lines in practice.
    /// </summary>
    public bool TryParseProgressEvent(string rawLine, out WorkerProgressEvent? progressEvent)
    {
        progressEvent = null;
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawLine);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("event", out var eventProp))
            {
                return false;
            }

            switch (eventProp.GetString())
            {
                case "init":
                    progressEvent = new WorkerProgressEvent("status", "Session started");
                    return true;

                case "step_update"
                    when root.TryGetProperty("step_update", out var step)
                        && step.TryGetProperty("state", out var stateProp)
                        && stateProp.GetString() == "DONE"
                        && step.TryGetProperty("step_type", out var stepTypeProp)
                        && stepTypeProp.GetString() is { Length: > 0 } stepType
                        && stepType is not ("unknown" or "checkpoint" or "user_input"):
                    // The DONE edge, not ACTIVE: measured, agy reports most steps ONLY at DONE
                    // (user_input/agent_response/checkpoint had no ACTIVE; only `tool` did), so the DONE
                    // edge is the one that gives one heartbeat per completed step. Dropped as non-signal:
                    // the user's own echoed `user_input`, internal `checkpoint`, and opaque `unknown`.
                    // (Which edge/types to surface is a UX policy provisional on a live end-to-end drive,
                    // which is blocked on the agy weekly-quota reset; the parse itself is fixture-pinned.)
                    progressEvent = new WorkerProgressEvent("status", stepType);
                    return true;

                case "result"
                    when root.TryGetProperty("result", out var result)
                        && result.TryGetProperty("response", out var responseProp)
                        && responseProp.GetString() is { Length: > 0 } response:
                    progressEvent = new WorkerProgressEvent("text", response);
                    return true;

                // #1561: a non-SUCCESS result (e.g. quota exhaustion — #1128's real captured
                // execution eca57a30, see AgyWorkerAdapterTests.Quota_refusal_on_the_stdout_tail_alone_classifies_ExhaustedUntil)
                // carries an empty `response`, so the case above never matches it and this line used
                // to fall through to `default => false`, silently dropping the one line that says WHY
                // the agy lane failed — the same gap ClaudeWorkerAdapter.TryParseResultEvent closes on
                // the claude side. `status` (not response-emptiness) is the correct signal:
                // IsTerminalSuccessLine above already keys the #1089 hang guard on that same field.
                case "result"
                    when root.TryGetProperty("result", out var errorResult)
                        && errorResult.TryGetProperty("status", out var statusProp)
                        && statusProp.ValueKind == JsonValueKind.String
                        && statusProp.GetString() is { Length: > 0 } status
                        && status != "SUCCESS":
                    var errorText = errorResult.TryGetProperty("error", out var errorProp)
                        && errorProp.ValueKind == JsonValueKind.String
                        && errorProp.GetString() is { Length: > 0 } detail
                        ? detail
                        : "no error detail in the result envelope";
                    progressEvent = new WorkerProgressEvent("result", "error — " + errorText);
                    return true;

                // Recognized `step_update`/`result` shapes that neither case above matched — the
                // ACTIVE edge, an unknown/checkpoint/user_input DONE step, or a SUCCESS result with an
                // empty response — deliberately carry no signal (#1561 second-reader review: measured
                // 144 ACTIVE `step_update` lines plus the echoed user prompt in one real capture; the
                // pre-#1561 code silently dropped these the same way, via `default => false`, which
                // is indistinguishable from "unrecognized event" now that unrecognized events echo
                // verbatim). A bare, unguarded case for each so an actually-new `event` value the
                // top-level switch has never seen still falls to `default` below and echoes verbatim.
                case "step_update":
                case "result":
                    progressEvent = new WorkerProgressEvent("ignore", string.Empty);
                    return true;

                default:
                    return false;
            }
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Parses agy's <c>stream-json</c> terminal <c>"event":"result"</c> line (issue #1360, extended by
    /// #1569). Delegates to <see cref="AgyUsageParser"/> (#1599) for the reason
    /// <see cref="ClaudeWorkerAdapter.TryParseFinalUsage"/>'s own doc comment states. See
    /// <see cref="AgyUsageParser"/> itself for the inconsistent <c>result.usage</c> shape this reads
    /// against and the fields it leaves unbound.
    /// </summary>
    public bool TryParseFinalUsage(string rawLine, out WorkerUsage? usage) =>
        UsageParser.TryParseFinalUsage(rawLine, out usage);

    private static readonly AgyUsageParser UsageParser = new();

    /// <summary>
    /// #1594: recovers agy's own final answer from the same terminal <c>"event":"result"</c> line
    /// <see cref="TryParseFinalUsage"/> and <see cref="IsTerminalSuccessLine"/> already key on --
    /// <c>result.response</c>, gated on <c>result.status == "SUCCESS"</c> exactly like
    /// <see cref="IsTerminalSuccessLine"/>, since a non-SUCCESS result's <c>response</c> is
    /// documented empty (#1561, this same file's <see cref="TryParseProgressEvent"/>) and an error
    /// status carrying incidental text would be the wrong thing to capture as the worker's response.
    /// </summary>
    public bool TryParseFinalResponse(string rawLine, out string? response)
    {
        response = null;
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawLine);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("event", out var eventProp)
                || eventProp.ValueKind != JsonValueKind.String || eventProp.GetString() != "result"
                || !root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object
                || !result.TryGetProperty("status", out var statusProp)
                || statusProp.ValueKind != JsonValueKind.String || statusProp.GetString() != "SUCCESS"
                || !result.TryGetProperty("response", out var responseProp)
                || responseProp.ValueKind != JsonValueKind.String
                || responseProp.GetString() is not { Length: > 0 } text)
            {
                return false;
            }

            response = text;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static IReadOnlyList<string> ParseModelLines(string? stdout) =>
        NonEmptyTrimmedLines(stdout).ToList();

    private static IEnumerable<WorkerCapabilityItem> ParseAgentLines(string? stdout) =>
        NonEmptyTrimmedLines(stdout)
            .Where(line => !line.EndsWith(':')) // skip the "Available agents:" header
            .Select(name => new WorkerCapabilityItem(name, "agent", $"agy agent: {name}"));

    private static IEnumerable<WorkerCapabilityItem> ParsePluginLines(string? stdout) =>
        NonEmptyTrimmedLines(stdout)
            .Where(line => !line.StartsWith("No imported plugins", StringComparison.OrdinalIgnoreCase))
            .Select(name => new WorkerCapabilityItem(name, "plugin", $"agy plugin: {name}"));

    private static IEnumerable<string> NonEmptyTrimmedLines(string? stdout) =>
        string.IsNullOrWhiteSpace(stdout)
            ? []
            : stdout.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0);

    private static async Task<string?> RunAgySubcommandAsync(IReadOnlyList<string> args, string? workingDirectory, CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo("agy")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };
            foreach (var arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }
            if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
            {
                startInfo.WorkingDirectory = workingDirectory;
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(DiscoverySubcommandTimeout);

            try
            {
                var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                return await stdoutTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best effort: process may have already exited between the cancel and the kill.
                }
                return null;
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // agy isn't installed/on PATH, or couldn't be started — discovery degrades to nothing
            // for this subcommand rather than fabricating a result.
            return null;
        }
    }

    /// <summary>
    /// How far past AER's own timeout <c>--print-timeout</c> is set (#588).
    /// </summary>
    /// <remarks>
    /// The point of the flag is not to impose a limit — it is to stop <c>agy</c> imposing <i>its</i>
    /// default one first. Whichever limit expires first decides the failure mode, and the two are not
    /// equally good: AER's produces <c>CoreExitReason.TimedOut</c> and the reason
    /// <c>"Execution timed out."</c>, whereas agy's print-mode wait expiring produces a clean exit 0
    /// with no output file — the silent failure #588 was filed for. So agy's limit is pushed strictly
    /// beyond AER's and left as a backstop that should never fire.
    /// <para>
    /// Fixed rather than proportional. A proportional margin is dangerously tight at the short end —
    /// 25% of a 30-second timeout is under 8 seconds, well inside process-teardown jitter on a loaded
    /// machine — while at the long end the size of the backstop is irrelevant, because AER terminates
    /// the tree at its own deadline regardless. A margin too small does not fail loudly; it
    /// reintroduces the original silent exit-0 as a race.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan PrintTimeoutMargin = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Renders a timeout as a Go duration literal, which is what <c>agy</c>'s flag parser accepts.
    /// </summary>
    /// <remarks>
    /// Total seconds, never <see cref="TimeSpan.ToString()"/>. Measured on this host: <c>1200s</c>,
    /// <c>20m0s</c> and <c>20m</c> are all accepted, while <c>00:20:00</c> — precisely what
    /// <c>TimeSpan.ToString()</c> produces — is rejected with
    /// <c>invalid value "00:20:00" for flag -print-timeout: time: unknown unit ":" in duration</c> and
    /// exit code 2. A default interpolation of the TimeSpan would therefore have broken every gemini
    /// dispatch outright rather than degrading quietly.
    /// <para>
    /// Rounded up, so the emitted backstop is never a fraction of a second tighter than intended, and
    /// floored at one second because a zero or negative duration is not a value the flag accepts.
    /// </para>
    /// </remarks>
    private static string FormatPrintTimeout(TimeSpan timeout)
    {
        // Saturate rather than add blindly: TimeSpan addition *throws* on overflow instead of
        // clamping, and a binding's Timeout is operator-authored — any parseable TimeSpan is accepted,
        // including ones within a minute of TimeSpan.MaxValue. That throw would escape binding
        // resolution, so one absurd value in a bindings file would take down every worker in it
        // rather than only its own.
        var withMargin = timeout > TimeSpan.MaxValue - PrintTimeoutMargin
            ? TimeSpan.MaxValue
            : timeout + PrintTimeoutMargin;

        var seconds = (long)Math.Ceiling(withMargin.TotalSeconds);
        return $"{Math.Max(seconds, 1)}s";
    }

    public bool TryClassifyFailure(
        string? stderrTail,
        TimeProvider timeProvider,
        out FailureClassification? classification,
        out DateTimeOffset? retryNotBefore)
    {
        if (TryClassifyQuotaExhaustion(stderrTail, timeProvider, out classification, out retryNotBefore))
        {
            return true;
        }

        return TryClassifyAutoDeniedTool(stderrTail, out classification, out retryNotBefore);
    }

    /// <summary>
    /// #1128: agy's real quota refusal ("Individual quota reached. … Resets in 1h39m10s.",
    /// measured live 2026-08-12) arrives in the stream-json result envelope on STDOUT, not on
    /// stderr — so the single-tail path above never saw it and the failure burned ordinary retry
    /// attempts against a dead quota. Same both-channels ordering ClaudeWorkerAdapter uses
    /// (#1115): stderr first, then the stdout tail. QUOTA-ONLY on stdout, deliberately
    /// (#1124 review): in stream-json mode the stdout tail is the model's own answer text, and
    /// the auto-denied matcher is a loose two-word prose match — running it there would let a
    /// worker's legitimate answer about permissions veto its own successful run. The quota
    /// matcher stays sound on stdout because it requires the vendor-controlled refusal sentence
    /// plus a parseable reset duration; auto-denied stays stderr-only, where agy's own CLI
    /// diagnostics live.
    /// </summary>
    public bool TryClassifyFailure(
        string? stderrTail,
        string? stdoutTail,
        TimeProvider timeProvider,
        out FailureClassification? classification,
        out DateTimeOffset? retryNotBefore)
    {
        if (TryClassifyFailure(stderrTail, timeProvider, out classification, out retryNotBefore))
        {
            return true;
        }

        return TryClassifyQuotaExhaustion(stdoutTail, timeProvider, out classification, out retryNotBefore);
    }

    /// <summary>
    /// #1720 review F1: agy's answer to the satisfied exit-0 veto — see
    /// <see cref="Outcomes.IFailureClassifier.TryClassifySatisfiedRunFailure"/>'s own doc for why
    /// this question differs from the exit-1 one at all. Stderr keeps the full matcher above (agy's
    /// CLI diagnostics); stdout goes only through
    /// <see cref="TryClassifyQuotaExhaustionFromResultEnvelope"/>.
    /// </summary>
    public bool TryClassifySatisfiedRunFailure(
        string? stderrTail,
        string? stdoutTail,
        TimeProvider timeProvider,
        out FailureClassification? classification,
        out DateTimeOffset? retryNotBefore)
    {
        if (TryClassifyFailure(stderrTail, timeProvider, out classification, out retryNotBefore))
        {
            return true;
        }

        return TryClassifyQuotaExhaustionFromResultEnvelope(stdoutTail, timeProvider, out classification, out retryNotBefore);
    }

    /// <summary>
    /// agy's own stream-json terminal envelope — <c>event == "result"</c> with a
    /// <c>result.status</c> other than <c>"SUCCESS"</c>, the same shape
    /// <see cref="TryParseProgressEvent"/> and <see cref="IsTerminalSuccessLine"/> already key on —
    /// and only then the quota sentence, matched against that envelope's own <c>error</c> field.
    /// A worker cannot emit this envelope: the CLI writes it, which is the whole point.
    /// </summary>
    public static bool TryClassifyQuotaExhaustionFromResultEnvelope(
        string? stdoutTail,
        TimeProvider timeProvider,
        out FailureClassification? classification,
        out DateTimeOffset? retryNotBefore)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        classification = null;
        retryNotBefore = null;

        FailureClassification? matchedClassification = null;
        DateTimeOffset? matchedRetryNotBefore = null;

        var matched = StreamJsonTailScanner.AnyObject(stdoutTail, root =>
        {
            if (!root.TryGetProperty("event", out var eventProp)
                || eventProp.ValueKind != JsonValueKind.String
                || eventProp.GetString() != "result"
                || !root.TryGetProperty("result", out var result)
                || result.ValueKind != JsonValueKind.Object
                || !result.TryGetProperty("status", out var statusProp)
                || statusProp.ValueKind != JsonValueKind.String
                || statusProp.GetString() is not { Length: > 0 } status
                || status == "SUCCESS"
                || !result.TryGetProperty("error", out var errorProp)
                || errorProp.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            return TryClassifyQuotaExhaustion(
                errorProp.GetString(), timeProvider, out matchedClassification, out matchedRetryNotBefore);
        });

        if (!matched)
        {
            return false;
        }

        classification = matchedClassification;
        retryNotBefore = matchedRetryNotBefore;
        return true;
    }

    [GeneratedRegex(@"Resets in\s+(?:(?<hours>\d+)h)?(?:(?<minutes>\d+)m)?(?:(?<seconds>\d+)s)?", RegexOptions.IgnoreCase)]
    private static partial Regex QuotaResetDurationRegex();

    /// <summary>
    /// Recognizes Gemini quota exhaustion errors from stderr prose (issue #594) and parses the reset duration
    /// converted to an absolute <see cref="DateTimeOffset"/>.
    /// </summary>
    public static bool TryClassifyQuotaExhaustion(
        string? stderrOrReason,
        TimeProvider timeProvider,
        out FailureClassification? classification,
        out DateTimeOffset? retryNotBefore)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        classification = null;
        retryNotBefore = null;

        if (string.IsNullOrWhiteSpace(stderrOrReason))
        {
            return false;
        }

        if (!stderrOrReason.Contains("Individual quota reached", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var match = QuotaResetDurationRegex().Match(stderrOrReason);
        if (!match.Success)
        {
            return false;
        }

        // TryParse, never Parse: the regex's digit groups are unbounded, and this is called on the
        // pump's classification path, which deliberately has no catch (MutationInterface refuses
        // to fabricate outcomes) — a thrown OverflowException here would fault the whole pump.
        // A vendor string too absurd to parse lands in the same conservative arm as any other
        // unparseable duration: no classification, reason preserved intact.
        if (!TryReadGroup(match, "hours", out int hours) ||
            !TryReadGroup(match, "minutes", out int minutes) ||
            !TryReadGroup(match, "seconds", out int seconds))
        {
            return false;
        }

        if (hours == 0 && minutes == 0 && seconds == 0)
        {
            return false;
        }

        // Summed as long seconds rather than the TimeSpan(h, m, s) constructor: the constructor
        // throws ArgumentOutOfRangeException near int.MaxValue hours, and no overflow is reachable
        // this way (int.MaxValue * 3600 fits a long with 5 orders of magnitude to spare).
        var duration = TimeSpan.FromSeconds((hours * 3600L) + (minutes * 60L) + seconds);
        if (duration <= TimeSpan.Zero)
        {
            return false;
        }

        classification = FailureClassification.ExhaustedUntil;
        retryNotBefore = timeProvider.GetUtcNow().Add(duration);
        return true;
    }

    /// <summary>
    /// Recognizes agy auto-denied-tool errors from stderr prose (issue #914) mirroring what was
    /// <c>tools/baton-agy-loop/dispatch.py</c>'s canonical twin markers before #1759 retired it:
    /// "auto-denied" AND "permission".
    /// </summary>
    public static bool TryClassifyAutoDeniedTool(
        string? stderrOrReason,
        out FailureClassification? classification,
        out DateTimeOffset? retryNotBefore)
    {
        classification = null;
        retryNotBefore = null;

        if (string.IsNullOrWhiteSpace(stderrOrReason))
        {
            return false;
        }

        if (!stderrOrReason.Contains("auto-denied", StringComparison.OrdinalIgnoreCase) ||
            !stderrOrReason.Contains("permission", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        classification = FailureClassification.ToolDenied;
        return true;
    }

    private static bool TryReadGroup(Match match, string groupName, out int value)
    {
        value = 0;
        return !match.Groups[groupName].Success
            || int.TryParse(match.Groups[groupName].Value, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}
