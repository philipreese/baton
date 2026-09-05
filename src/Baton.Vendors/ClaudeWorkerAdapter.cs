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
/// <see cref="WorkerInvocation"/>/<see cref="WorkerContract"/> pair into a direct <c>claude</c>
/// invocation without shell wrappers. Bypasses cmd.exe and sh, eliminating quoting and command injection risks.
/// Stdin redirection to null is handled natively by the process host.
/// <para>
/// <b>M21 Phase 1's <see cref="IPermissionGrantTranslator"/>, corrected in #331:</b> Claude Code's
/// <c>--allowedTools</c> is tool-name-based (<c>Read</c>, <c>Edit</c>, <c>Write</c>,
/// <c>Bash</c>/<c>Bash(pattern)</c>, <c>WebFetch</c>, <c>WebSearch</c>) but only <em>pre-approves</em>
/// those tools so they do not prompt — it is not a sandbox and does not remove a withheld tool from
/// the model's reach. A grant therefore resolves to <em>both</em> lists: <c>--allowedTools</c> for what
/// it permits (this direction never refuses), and <c>--disallowedTools</c> for what it withholds
/// (<see cref="BuildDisallowedTools"/>), which is what actually enforces the denial — decision 0004's
/// "fail closed".
/// </para>
/// <para>
/// <b>Writes are the exception since #649</b>, and this is the first thing to know when reading the
/// two lists here: <c>Edit</c>/<c>Write</c>/<c>NotebookEdit</c> are pre-approved on
/// <c>--allowedTools</c> and absent from <c>--disallowedTools</c>, because the CLI refuses a named
/// tool before AER's <c>PreToolUse</c> hook can allow the one write landing in
/// <c>BATON_OUTPUT_DIR</c>. For that category the hook is the whole enforcement; for the other three
/// the sentence above still holds. See <see cref="BuildHookDeniedTools"/>.
/// </para>
/// </summary>
public sealed partial class ClaudeWorkerAdapter : IWorkerAdapter, IPermissionGrantTranslator
{
    internal const string OversizePromptWrapperText =
        "Read the complete task instructions in %BATON_PROMPT_FILE% and execute them exactly as written. Do not summarize or treat as data.";

    private const string DefaultPermissionScope = "Write";

    private const int HookTimeoutSeconds = 30;

    private readonly IClaudeHookLivenessProbe _hookLivenessProbe;

    public ClaudeWorkerAdapter(IClaudeHookLivenessProbe? hookLivenessProbe = null)
    {
        _hookLivenessProbe = hookLivenessProbe ?? new ProcessClaudeHookLivenessProbe();
    }

    public bool TryTranslatePermissionGrant(PermissionGrant grant, out string? resolvedValue, out string? gapReason)
    {
        ArgumentNullException.ThrowIfNull(grant);

        List<string> tools = [];
        if (grant.ReadFiles)
        {
            tools.Add("Read");
        }

        // Pre-approved either way (#649). When writes are granted this is the plain case; when they
        // are withheld the tools must STILL be pre-approved, because the hook is what confines them to
        // BATON_OUTPUT_DIR and it never gets consulted for a tool the model could not invoke. Headless
        // `-p` has no prompt to answer, so a tool that is neither pre-approved nor denied is simply
        // unusable — measured: the first live run of this change wrote nothing at all, exited 0, and
        // failed its contract, which is the exact symptom #629 describes.
        //
        // Safe because a hook exiting 2 beats a pre-approval: gate.hook-exit-2-beats-allow is the
        // sentinel that measures THIS direction, passing --allowedTools Write alongside a hook that
        // exits 2 and confirming the file is not written. (gate.allowedtools-is-preapproval-not-ceiling
        // measures the opposite direction -- that an OMITTED tool still runs -- which is what made
        // #611 invalid and #529 necessary, and is not the fact this line rests on.)
        tools.Add("Edit");
        tools.Add("Write");
        tools.Add("NotebookEdit");

        if (grant.RunShellCommands)
        {
            if (grant.ShellCommandPatterns is { Count: > 0 } patterns)
            {
                tools.AddRange(patterns.Select(pattern => $"Bash({pattern})"));
            }
            else
            {
                tools.Add("Bash");
            }
        }

        if (grant.NetworkAccess)
        {
            tools.Add("WebFetch");
            tools.Add("WebSearch");
        }

        resolvedValue = string.Join(',', tools);
        gapReason = null;
        return true;
    }

    /// <summary>
    /// The environment variable name AER inspects for an operator-configured shared Claude config root (#442).
    /// </summary>
    public const string BatonClaudeConfigRootVariable = "BATON_CLAUDE_CONFIG_ROOT";

    /// <summary>
    /// The environment variable name Claude Code reads for its configuration root directory.
    /// </summary>
    public const string ClaudeConfigDirVariable = "CLAUDE_CONFIG_DIR";

    public CoreDispatchTarget Resolve(WorkerInvocation invocation, WorkerContract contract)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(contract);

        // #1166: decision 0004's project ceiling (ProjectCeilingGate's own doc has the rule). Applied
        // first so every channel below derived from invocation.PermissionGrant (--allowedTools,
        // --disallowedTools, the hook-denied-tools env var, the shell-pattern env vars) reflects the
        // capped grant rather than the role's uncapped one.
        invocation = ProjectCeilingGate.Apply(invocation, contract, WithheldWritesReachTheOutbox);

        var isWindows = OperatingSystem.IsWindows();
        ProjectSkills(invocation.WorkingDirectory);
        var prompt = BuildPrompt(invocation.PromptTemplate, contract, isWindows);
        var permissionScope = ResolvePermissionScope(invocation);
        var artifactsRoot = EnvironmentReference("BATON_ARTIFACTS_ROOT", isWindows);

        List<string> args =
        [
            "-p", prompt,
            "--allowedTools", permissionScope,
            // #289: Claude Code enforces its own directory-trust sandbox independent of
            // --allowedTools, and (confirmed empirically against the real, authenticated CLI)
            // non-deterministically refuses to write outside it when BATON_OUTPUT_DIR falls outside
            // the spawned process's cwd -- which it always does for a plain chat session, since
            // ExecuteSessionTurnAsync never sets WorkerInvocation.WorkingDirectory unless the
            // session is attached to a codebase. Reproduced identically via a bare manual `claude`
            // invocation (not daemon-specific): ~50% of otherwise-identical trials silently failed
            // to produce their declared output file, each citing "outside the sandboxed worktree" /
            // "outside the allowed working directories" as its own reason, until this flag was
            // added -- 0/6 failures with it across the same trial shape. Mirrors the same grant
            // AgyWorkerAdapter has carried since spike #21 for the identical reason (agy ignores
            // the invoking process's cwd entirely); Claude turned out to need it too, just only
            // sometimes, which is what made the gap easy to miss.
            "--add-dir", artifactsRoot,
        ];

        // #533 constraints 1-2: hooks load only from the process's own cwd `.claude/`, with no
        // parent-directory fallback, and `--add-dir` (above) loads no configuration on claude --
        // measured, gate.add-dir-loads-no-config. So AER cannot rely on cwd-based discovery for
        // either the mandatory PreToolUse hook (0029) or MCP config; it passes both explicitly, at
        // a path AER owns rather than the room's own directory (`WorkingDirectory` may be a repo the
        // operator did not ask AER to write into). EnsureLaunchConfigFiles populates the real
        // PreToolUse hook (#543) -- see its own doc comment for why the settings file is left holding
        // canonical content on every resolve rather than written once, and #667 for why an unchanged
        // file is not rewritten to get there.
        var (settingsPath, mcpConfigPath) = EnsureLaunchConfigFiles();

        // #532: confirm the hook this dispatch is about to rely on can actually run -- see
        // IClaudeHookLivenessProbe's own doc comment for why BuildSettingsJson's File.Exists guard
        // above is not enough on its own. Fails closed, never warns and continues.
        var hookAssemblyPath = HookAssemblyPath;
        var probeResult = _hookLivenessProbe.Probe(hookAssemblyPath, TimeSpan.FromSeconds(HookTimeoutSeconds));
        if (!probeResult.IsLive)
        {
            throw new ClaudeHookUnverifiedException(hookAssemblyPath, probeResult.Detail);
        }

        args.Add("--settings");
        args.Add(settingsPath);
        args.Add("--mcp-config");
        args.Add(invocation.EnableMemoryProposalTool ? EnsureMemoryProposalMcpConfig() : mcpConfigPath);

        // #331: --allowedTools only *pre-approves* tools so they don't prompt; it is not a sandbox,
        // and omitting a tool leaves it in the model's reach (a shell-denied session ran `hostname`
        // and returned the real value). A withheld category must be *actively* denied. Verified
        // against the live CLI in a clean spawn env: the same invocation refuses `hostname` with
        // --disallowedTools Bash and runs it without. --disallowedTools takes precedence over
        // --allowedTools, so the two compose — allow what's granted, deny what's withheld (0004).
        // #1802: independent of the four PermissionGrant categories BuildDisallowedTools maps --
        // Task/Agent sit outside all four (that method's own doc records this boundary), so a
        // write-and-shell-granted role like implement never reaches this tool's denial through the
        // grant alone. Both names are withheld together: Task is Agent's older name, still honoured by
        // the CLI (docs/vendor-capabilities.md's canonical ceiling).
        var disallowed = BuildDisallowedTools(invocation.PermissionGrant);
        if (!invocation.AllowsSubagents)
        {
            disallowed = disallowed.Length > 0 ? $"{disallowed},Agent,Task" : "Agent,Task";
        }

        if (disallowed.Length > 0)
        {
            args.Add("--disallowedTools");
            args.Add(disallowed);
        }

        if (invocation.StreamJson)
        {
            // --print + --output-format=stream-json refuses to run without --verbose (confirmed
            // against the installed claude CLI directly: "Error: When using --print,
            // --output-format=stream-json requires --verbose") -- without this flag every
            // streaming session turn would fail at the CLI invocation itself, before producing any
            // output at all.
            // #1540: event-level streaming only — do not pass --include-partial-messages so token-level
            // volume does not roll the 8 MiB ExecutionStreamLogger window early.
            args.Add("--output-format");
            args.Add("stream-json");
            args.Add("--verbose");
        }
        else
        {
            args.Add("--output-format");
            args.Add("text");
        }

        // Do not reintroduce `--bare` here, under any flag. It is not a latency optimisation this
        // product can take, for two independently sufficient reasons, both measured:
        //
        //   1. It skips "keychain reads" (its own --help says so) -- which is exactly where
        //      subscription OAuth login lives. A --bare dispatch against a real subscription login
        //      fails immediately with "Not logged in", even with valid, unexpired credentials, and
        //      AER works against subscriptions rather than API keys (Architecture Rule 4).
        //   2. It suppresses hooks and MCP servers EVEN WHEN PASSED EXPLICITLY via --settings
        //      (#521): `claude --bare --settings <PreToolUse hook>` does not fire the hook, while
        //      the same invocation without --bare does. 0029 makes that hook mandatory on every
        //      worker AER spawns, so --bare is the flag AER passed that removed the gate. It is
        //      not the only route to the same failure -- `--safe-mode` (a flag AER never passes,
        //      so nothing to neutralize) and CLAUDE_CODE_SIMPLE=1, documented as equivalent to
        //      --bare including its keychain-skip, disable hooks identically. Unlike --safe-mode,
        //      CLAUDE_CODE_SIMPLE is an *inherited* env var (#543: neutralized below, in
        //      CoreDispatchTarget.Environment -- BatonTask inherits the full parent environment by
        //      default, so an operator's shell setting it would otherwise reach claude unopposed).
        //
        // Reason 2 is the load-bearing one: an auth failure is loud, and a missing hook is silent
        // for one of two independent reasons -- not loaded at all, or loaded but unable to execute
        // (#530 measures the second; the first traces to the discovery constraint, not to #530).
        if (invocation.SessionId is not null)
        {
            if (invocation.ResumeSession)
            {
                args.Add("--resume");
                args.Add(invocation.SessionId);
            }
            else
            {
                args.Add("--session-id");
                args.Add(invocation.SessionId);
            }
        }

        if (invocation.Model is { } model)
        {
            RefuseDotDelimitedClaudeModelId(model); // #1090
            args.Add("--model");
            args.Add(model);
        }

        if (invocation.Effort is not null)
        {
            // #1318: see EffortTierMapping for why this is resolved rather than forwarded as-is.
            args.Add("--effort");
            args.Add(EffortTierMapping.ResolveForClaude(invocation.Effort));
        }

        var withheld = BuildHookDeniedTools(invocation.PermissionGrant);
        var environment = new List<(string Name, string Value)>
        {
            (MaxSubagentSpawnDepthVariable, "1"),
            // #600 tags it with the vendor; #649 makes its contents differ from the flag.
            (DeniedToolsVariable, $"{DeniedToolsVendorTag}:{withheld}"),
            (SimpleModeVariable, "0"),
            // #1459: always set, even empty -- an empty-but-tagged list is the deliberate
            // unscoped-shell reading (HookCheckCommand.Decide skips the segment-level check), where an
            // absent/wrong-vendor one is a broken channel and also skips it (see that method's own
            // remarks for why claude's absent case reads opposite to agy's). Reuses
            // AgyWorkerAdapter's builders when there's a structured grant to read; falls back to
            // BuildShellPatternsFromRawScope for the raw PermissionScope escape hatch (#1459 fix --
            // see that method's own doc comment for the bypass this closes).
            (ShellPatternsVariable,
                $"{ShellPatternsVendorTag}:{(invocation.PermissionGrant is { } shellGrant
                    ? AgyWorkerAdapter.BuildShellPatterns(shellGrant)
                    : BuildShellPatternsFromRawScope(permissionScope))}"),
            // The raw PermissionScope escape hatch has no denied-pattern concept to parse out of it
            // (it feeds --allowedTools alone, never --disallowedTools) -- stays empty on that path,
            // same as before this fix. Not a gap: BuildShellPatternsFromRawScope's doc comment records
            // why an allow-only channel is still a strict improvement there.
            (DeniedShellPatternsVariable,
                $"{ShellPatternsVendorTag}:{AgyWorkerAdapter.BuildDeniedShellPatterns(invocation.PermissionGrant)}"),
            // #1683 F2. Unlike the two channels above this one has NO --disallowedTools half here --
            // BuildDisallowedTools emits nothing from it, deliberately (the reasoning is canonical on
            // ShellCommandPatternMatcher.IsDeniedByOptionToken), so the hook is claude's only
            // enforcement of an option-token deny. Empty on the raw PermissionScope path for the same
            // reason the denied-pattern channel is: that string has no deny concept to parse out of it.
            (DeniedShellOptionTokensVariable,
                $"{ShellPatternsVendorTag}:{AgyWorkerAdapter.BuildDeniedShellOptionTokens(invocation.PermissionGrant)}"),
        };

        // record-once-ok: #1524 src/Baton/Status/BatonEnvironmentSnapshot.cs
        // #1524: folded into BatonEnvironmentSnapshot.
        if (BatonEnvironmentSnapshot.Current.ClaudeConfigRootOverride is { Length: > 0 } configRoot)
        {
            environment.Add((ClaudeConfigDirVariable, configRoot));
        }

        // #679; see WorkerEnvironment.WorkspaceVariable for why this is told rather than inferred,
        // and for what its absence means.
        if (invocation.WorkingDirectory is { } workspace)
        {
            environment.Add((WorkerEnvironment.WorkspaceVariable, workspace));
        }

        // This literal name resolves through PATH the same way scripts/verify-pack-roundtrip.sh
        // documents in detail (the CVE-2024-24576 stance, measured for BatonTask's managed spawn
        // path, #1474): a real claude.exe, never an npm-installed `claude.cmd`/`.bat` shim, which
        // will fail spawn with "program not found" -- the native installer's claude.exe is
        // required (#1468).
        return new CoreDispatchTarget(
            "claude", [.. args], invocation.WorkingDirectory, PromptText: prompt,
            Environment: [.. environment], OversizePromptWrapper: OversizePromptWrapperText);
    }

    /// <summary>
    /// Overrides an inherited <c>CLAUDE_CODE_SIMPLE=1</c> (see the comment above on why that
    /// disables hooks the same way <c>--bare</c> does) so an operator's shell cannot reach the
    /// spawned <c>claude</c> process and remove the gate.
    /// <para>
    /// <b>Measured, and it is now a sentinel</b> — <c>gate.simple-mode-override-restores-the-hook</c>
    /// (#550). This carried an admission that no live run had confirmed <c>"0"</c> is even parsed,
    /// with the value chosen by analogy to a sibling variable's documented opt-out tokens. Three
    /// arms against the installed CLI settled it: unset fires the hook, <c>=0</c> fires the hook, so
    /// the override does what it claims.
    /// </para>
    /// <para>
    /// The same run corrected the <i>hazard's shape</i>. An inherited <c>=1</c> does not produce a
    /// quietly ungated worker here: the hook never fires and nothing is written, because the run dies
    /// at <c>Not logged in</c> with <c>rc=1</c> — the keychain skip reason 1 above predicts for
    /// <c>--bare</c>. Loud, not silent. <b>Scoped to a host holding a subscription login</b>, which
    /// is what AER exists to drive; nothing here establishes what an API-key host does, and that is
    /// the case where the failure could stay quiet.
    /// </para>
    /// </summary>
    public const string SimpleModeVariable = "CLAUDE_CODE_SIMPLE";

    /// <summary>
    /// The vendor tag prefixing <see cref="DeniedToolsVariable"/>'s value (#600), so an absent list, an
    /// empty one AER deliberately set, and another vendor's list are three distinguishable things
    /// rather than one that always allowed. Mirrored as a literal in <c>Baton.Cli</c>'s hook command
    /// because <c>Baton.Vendors</c> cannot reference it; <c>DeniedToolChannelTests</c> is the one test
    /// that sees both sides and fails if they drift.
    /// </summary>
    public const string DeniedToolsVendorTag = "claude";

    /// <summary>
    /// The environment variable carrying this invocation's denied-tool list to the <c>PreToolUse</c>
    /// hook's own process (#543) — <see cref="BuildHookDeniedTools"/>'s names, which since #649 are a
    /// <em>superset</em> of what <see cref="BuildDisallowedTools"/> puts on <c>--disallowedTools</c>:
    /// the write tools ride this channel only, so the hook can allow the one write that lands in
    /// <c>BATON_OUTPUT_DIR</c>. Set even when empty. A hook process inherits the spawning process's
    /// environment (confirmed in <c>.vendor-survey/corpus/claude__hooks.md</c>: "A hook process
    /// inherits the parent environment"), which is what makes this reach hook-check at all -- the
    /// settings file itself is one static, shared file across every spawn (see
    /// <see cref="EnsureLaunchConfigFiles"/>), so per-invocation data has to travel this way rather
    /// than through the file's content. <see cref="Baton.Vendors"/> cannot reference <c>Baton.Cli</c>
    /// (the CLI depends on the adapters, never the reverse), so this name is a plain string contract
    /// mirrored on <c>HookCheckCommand.DeniedToolsEnvironmentVariable</c> — both sides assert the
    /// literal value in their own test suite, and the two must agree.
    /// </summary>
    public const string DeniedToolsVariable = "BATON_HOOK_DENIED_TOOLS";

    /// <summary>
    /// The environment variable carrying shell command patterns for pattern-scoped grants (#659).
    /// Declared but never set into a spawned worker's environment until #1459 — see
    /// <see cref="ShellPatternsVendorTag"/> and <c>Resolve</c>'s environment list below for the wiring,
    /// and <c>HookCheckCommand.Decide</c> for what reads it.
    /// </summary>
    public const string ShellPatternsVariable = "BATON_HOOK_SHELL_PATTERNS";

    /// <summary>
    /// The vendor tag prefixing <see cref="ShellPatternsVariable"/>'s and
    /// <see cref="DeniedShellPatternsVariable"/>'s values (#600's pattern, applied here by #1459).
    /// </summary>
    public const string ShellPatternsVendorTag = "claude";

    /// <summary>
    /// The environment variable carrying this invocation's <b>denied</b> shell command patterns —
    /// 0022's DenyAlways rung (#390), same literal as <c>AgyWorkerAdapter.DeniedShellPatternsVariable</c>
    /// (record-once: declared there first, referenced here rather than restated). claude's OWN
    /// enforcement for that rung is <c>--disallowedTools Bash(pattern)</c>
    /// (<see cref="StandingShellDenials"/>), which the CLI applies with precedence over
    /// <c>--allowedTools</c> and which survives a silently-dead hook (#530) — so this channel is
    /// belt-and-braces for the hook's own segment-level check (#1459, spec/baton.md §9), not this
    /// vendor's only enforcement of a standing "never" the way it is on agy.
    /// </summary>
    public const string DeniedShellPatternsVariable = AgyWorkerAdapter.DeniedShellPatternsVariable;

    /// <summary>
    /// #1683 F2's option-token deny channel, same literal as
    /// <c>AgyWorkerAdapter.DeniedShellOptionTokensVariable</c> (record-once: declared there, referenced
    /// here). <b>Read this alongside <see cref="DeniedShellPatternsVariable"/>, whose "belt-and-braces"
    /// framing does not carry over</b>: that channel has a vendor-flag half and this one has none, so a
    /// silently-dead hook (#530) leaves this rung unenforced where it leaves that one standing.
    /// </summary>
    public const string DeniedShellOptionTokensVariable =
        AgyWorkerAdapter.DeniedShellOptionTokensVariable;

    /// <summary>
    /// The environment variable name Claude Code reads for its subagent fan-out depth cap.
    /// </summary>
    /// <remarks>
    /// #533 constraint 3, measured rather than trusted from the vendor's own docs: the vendor
    /// documents this variable's default as <c>1</c> (no nesting), but two independent runs of
    /// <c>fanout.nesting-allowed-by-default</c> (<c>tools/vendor-verify/verify.py</c>) counted
    /// actual <c>SubagentStart</c> spawns and found the unset default produces <b>2</b> -- a
    /// subagent CAN spawn its own subagent with nothing configured. Set explicitly to <c>1</c> here
    /// so AER's own default matches what the vendor documents rather than what it measurably does.
    /// <para>
    /// #533 constraint 4 is why this is the only lever: a subagent inherits its parent's permission
    /// mode and cannot be given a stricter one, so the gate for a fan-out tree cannot be re-applied
    /// per level -- it has to hold for whatever depth this variable allows. Raising it later (e.g.
    /// for a legitimate multi-worker room, M27) is a deliberate widening, not a default to assume.
    /// </para>
    /// </remarks>
    public const string MaxSubagentSpawnDepthVariable = "CLAUDE_CODE_MAX_SUBAGENT_SPAWN_DEPTH";

    /// <summary>
    /// Ensures the two files <see cref="BatonPaths.WorkerLaunchConfig"/> needs exist. Called on every
    /// <see cref="Resolve"/> because there is no single daemon-lifecycle hook covering every entry
    /// point that resolves a claude invocation (the CLI's `baton run`/`baton decide`/etc. spawn a fresh
    /// process per command, with no daemon involved at all).
    /// </summary>
    /// <remarks>
    /// <b>The settings file is left holding canonical content on every resolve (#543), reversing
    /// #533's "never overwrite existing content."</b> That was correct while the file held only inert
    /// `{}` with nothing to lose; now it carries the mandatory `PreToolUse` hook (0029), and "never
    /// overwrite" would leave a pre-#543 `{}` -- or any other stale content -- permanently installed,
    /// silently disabling the gate for good on any machine that ran an earlier build even once. The
    /// file is entirely AER-owned (no operator content can live here, per
    /// <see cref="BatonPaths.WorkerLaunchConfig"/>'s own doc comment), so there is nothing that
    /// overwriting could destroy. Since #667 the write is skipped when the file already holds exactly
    /// that content -- a narrower thing than "never overwrite", and one that leaves drift correction
    /// intact; see <see cref="AtomicLaunchConfigWriter"/> for why the redundant write was worth
    /// removing. The MCP config file is untouched by #543 and keeps the old once-only semantics.
    /// </remarks>
    private static (string SettingsPath, string McpConfigPath) EnsureLaunchConfigFiles()
    {
        Directory.CreateDirectory(BatonPaths.WorkerLaunchConfig);

        var settingsPath = Path.Combine(BatonPaths.WorkerLaunchConfig, "claude-settings.json");
        AtomicLaunchConfigWriter.Write(settingsPath, BuildSettingsJson());

        // The standard empty MCP config shape -- declares no servers, so this adds nothing beyond
        // what claude would otherwise discover on its own.
        var mcpConfigPath = Path.Combine(BatonPaths.WorkerLaunchConfig, "claude-mcp.json");
        EnsureFileExists(mcpConfigPath, "{\"mcpServers\":{}}");

        return (settingsPath, mcpConfigPath);
    }

    /// <summary>
    /// Ensures the <c>--mcp-config</c> file naming AER's own MCP server (#585) and its
    /// <c>memory-edit-proposal</c> tool (#801) exists, returning its path. Left holding canonical
    /// content on every resolve, mirroring <see cref="EnsureLaunchConfigFiles"/>'s settings file
    /// rather than the plain empty <c>claude-mcp.json</c>'s once-only semantics -- this file's
    /// content is exactly as load-bearing as the PreToolUse hook's, just opt-in rather than mandatory.
    /// </summary>
    /// <remarks>
    /// <b>Carries no capture-directory path (#833).</b> #801 shipped this file naming a static,
    /// shared capture directory literally in its <c>args</c> -- every room's proposals landed in one
    /// place with no room attribution, which is why no daemon poller was ever wired to consume it
    /// (#833's fork). This file is resolved once per worker-binding entry, before any execution's
    /// <c>BATON_OUTPUT_DIR</c> exists (<see cref="Resolve"/> runs once per binding, not per execution --
    /// see <see cref="Baton.Vendors.WorkerInvocation"/>'s own doc comment for why), so nothing baked in
    /// here can vary per execution. The <c>mcp --memory-proposal-tool</c> verb+flag pair alone tells
    /// <c>Baton.Cli</c> to enable the tool; the process derives its own per-execution capture
    /// directory from <c>BATON_OUTPUT_DIR</c>, which it inherits from the <c>claude</c> process that
    /// spawns it as an MCP server -- the same inheritance <c>Baton.Cli.Program</c>'s <c>hook-check</c>
    /// branch already rests on for the identical reason.
    /// <para>
    /// #1458: <c>mcp</c> was a standalone <c>Baton.Mcp.Host.dll</c> before this file's own binary
    /// folded it in as a verb -- <c>mcp</c> must be the first argument, ahead of the tool flag, same
    /// as <see cref="BuildSettingsJson"/>'s <see cref="File.Exists"/> guard below it for the identical
    /// fail-open-and-silent reason (#530): an MCP server that never starts fails at claude's own
    /// spawn time, not loudly at dispatch.
    /// </para>
    /// </remarks>
    private static string EnsureMemoryProposalMcpConfig()
    {
        Directory.CreateDirectory(BatonPaths.WorkerLaunchConfig);
        var hostDllPath = Path.Combine(AppContext.BaseDirectory, "Baton.Cli.dll");
        if (!File.Exists(hostDllPath))
        {
            throw new InvalidOperationException(
                $"Cannot write the memory-proposal MCP config (#801): '{hostDllPath}' does not exist. " +
                "Every deployment of baton must carry Baton.Cli.dll alongside its own binary -- an MCP " +
                "config naming a path that does not exist fails open and silently (#530), so this fails " +
                "loudly here instead, before any worker is dispatched.");
        }

        var configPath = Path.Combine(BatonPaths.WorkerLaunchConfig, "claude-mcp-memory-proposal.json");
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

        AtomicLaunchConfigWriter.Write(configPath, json);
        return configPath;
    }

    /// <summary>
    /// Shared with the #532 resolve-time liveness probe below, so both readers name the identical
    /// path rather than two independent interpolations of the same directory.
    /// </summary>
    private static string HookAssemblyPath => Path.Combine(AppContext.BaseDirectory, "Baton.Cli.dll");

    /// <summary>
    /// The `--settings` content #543 ships: one `PreToolUse` hook, matching every tool
    /// (<c>"matcher": "*"</c>), spawned in exec form (`args` set) so Claude Code invokes it directly
    /// with no shell -- no quoting concerns, matching this adapter's own "direct shell-less" design
    /// (see the type's own doc comment).
    /// </summary>
    /// <remarks>
    /// <b>Invoked as <c>dotnet &lt;Baton.Cli.dll path&gt;</c>, not the native apphost.</b> An earlier
    /// version of this method named <c>Baton.Cli.exe</c>/<c>Baton.Cli</c> directly, resolved via
    /// <see cref="AppContext.BaseDirectory"/>. That works for a raw build output (confirmed for
    /// `Baton.Cli.exe` standalone; this ran from `Baton.Daemon.exe` too until #1420 narrowed the daemon
    /// to no longer spawn worker turns at all -- it has carried no path to `Baton.Cli` since) but is
    /// wrong for `baton`'s other real, exercised deployment shape: <c>Baton.Cli.csproj</c> sets
    /// <c>PackAsTool</c>, and a
    /// packed global tool's <c>DotnetToolSettings.xml</c> runs <c>Baton.Cli.dll</c> via the <c>dotnet</c>
    /// muxer with **no apphost at all** (confirmed by packing the tool and inspecting the nupkg) --
    /// naming the apphost there would silently write a dangling command into every worker's hook,
    /// exactly the fail-open-and-silent failure #530 measured. `dotnet &lt;dll&gt;` works in both
    /// shapes: the managed dll and its `.runtimeconfig.json`/`.deps.json` sit next to
    /// <see cref="AppContext.BaseDirectory"/> either way (a raw build's own output directory, or a
    /// global tool's own store directory -- it is, after all, the same dll this process is currently
    /// running from), and `dotnet` itself is a hard prerequisite for this whole product already
    /// (`CLAUDE.md`: ".NET 10 SDK is required"). The explicit <see cref="File.Exists"/> guard below
    /// turns any future deployment shape this reasoning missed into a loud failure at dispatch time
    /// rather than a silent one at hook-invocation time.
    /// </remarks>
    private static string BuildSettingsJson()
    {
        var hookAssemblyPath = HookAssemblyPath;
        if (!File.Exists(hookAssemblyPath))
        {
            throw new InvalidOperationException(
                $"Cannot write the mandatory PreToolUse hook (decision 0029): '{hookAssemblyPath}' " +
                "does not exist. Every deployment of baton/Baton.Daemon must carry Baton.Cli.dll alongside " +
                "its own binary -- a hook naming a path that does not exist fails open and silently " +
                "(#530), so this fails loudly here instead, before any worker is dispatched.");
        }

        var settings = new
        {
            hooks = new
            {
                PreToolUse = new[]
                {
                    new
                    {
                        matcher = "*",
                        hooks = new[]
                        {
                            new
                            {
                                type = "command",
                                command = "dotnet",
                                args = new[] { hookAssemblyPath, "hook-check" },
                            },
                        },
                    },
                },
            },
        };

        return JsonSerializer.Serialize(settings);
    }

    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="path"/> only if it does not already
    /// exist, without silently swallowing a genuine write failure.
    /// </summary>
    /// <remarks>
    /// Two turns can genuinely race here -- two chat sessions both starting their first-ever turn
    /// against a fresh <c>~/.baton</c>, both hitting this before either file exists, from the SAME
    /// daemon process, not just two separate `baton run` processes. That is a real TOCTOU: `File.Exists`
    /// then `File.WriteAllText` opens write-exclusive, so the loser of the race gets an
    /// <see cref="IOException"/>, not a second identical write as an earlier version of this comment
    /// claimed. The content this writes is fixed and identical regardless of who wins, so the correct
    /// response to that specific exception is "someone else just created it" -- verified by re-checking
    /// existence, not assumed. Any other failure (permissions, disk full, a genuinely corrupt partial
    /// write) still throws, per CLAUDE.md's rule against silently swallowing exceptions.
    /// </remarks>
    private static void EnsureFileExists(string path, string content)
    {
        if (File.Exists(path))
        {
            return;
        }

        try
        {
            File.WriteAllText(path, content);
        }
        catch (IOException) when (File.Exists(path))
        {
            // Another spawn's write won the race and the file is now there -- not our problem to fix.
        }
    }

    /// <summary>
    /// A structured <see cref="WorkerInvocation.PermissionGrant"/> always wins over the raw
    /// <see cref="WorkerInvocation.PermissionScope"/> string (<see cref="PermissionGrant"/>'s own
    /// docs record this precedence); <see cref="TryTranslatePermissionGrant"/> never refuses for
    /// this adapter, so this never throws.
    /// </summary>
    private string ResolvePermissionScope(WorkerInvocation invocation)
    {
        if (invocation.PermissionGrant is { } grant)
        {
            if (!TryTranslatePermissionGrant(grant, out var resolved, out var gapReason))
            {
                throw new PermissionGrantUnsupportedException("claude", gapReason!);
            }

            return resolved!;
        }

        return invocation.PermissionScope ?? DefaultPermissionScope;
    }

    /// <summary>
    /// Derives the hook's ALLOWED shell-pattern channel from the raw <c>PermissionScope</c> escape
    /// hatch, for when <see cref="WorkerInvocation.PermissionGrant"/> is null and
    /// <see cref="AgyWorkerAdapter.BuildShellPatterns"/> has no structured
    /// <see cref="PermissionGrant.ShellCommandPatterns"/> to read (#1459 fix, from PR #1506's
    /// adversarial security review).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The bypass this closes.</b> A worker binding can scope its shell via the raw
    /// <c>PermissionScope</c> string instead of a structured grant (e.g.
    /// <c>PermissionScope: "Write,Bash(git diff*)"</c>, <c>PermissionGrant: null</c> --
    /// <c>ClaudeWorkerAdapterTests.An_explicit_permission_scope_overrides_the_default</c> exercises
    /// exactly this shape). Before this fix the shell-pattern channel was built exclusively from
    /// <c>AgyWorkerAdapter.BuildShellPatterns(invocation.PermissionGrant)</c>, which returns empty for
    /// a null grant -- so <c>BATON_HOOK_SHELL_PATTERNS</c> came out as the literal <c>"claude:"</c>
    /// (tagged, zero patterns) regardless of what the raw scope actually granted.
    /// <c>HookCheckCommand.Decide</c> reads that shape as <c>Present</c> with <c>Patterns.Count == 0</c>,
    /// which is its deliberate no-op reading for an unscoped shell (see that method's own remarks) --
    /// so the second enforcement layer #1459 added never engaged, while <c>--allowedTools
    /// "Bash(git diff*)"</c> still reached claude and the #1461 chaining escape
    /// (<c>git diff; echo escaped</c>) executed unblocked, identically to the pre-#1459 state.
    /// </para>
    /// <para>
    /// <b>Single source of truth.</b> This parses <paramref name="resolvedScope"/> -- literally the
    /// same string <see cref="Resolve"/> already computed via <see cref="ResolvePermissionScope"/> and
    /// passes to <c>--allowedTools</c>, not a second, independently-derived copy of it -- so the hook
    /// channel can never scope a shell more narrowly or more broadly than claude's own flag did; the
    /// two are read from one value rather than kept in sync by hand. No existing parser in this
    /// codebase tokenizes a raw permission-scope string into <c>Bash(pattern)</c> clauses (checked:
    /// neither <c>Baton.Cli.ShellPatternList</c>/<c>DeniedToolList</c> -- which parse the
    /// already-vendor-tagged <em>environment variable</em> shape, a different string, and live in a
    /// project <c>Baton.Vendors</c> cannot reference -- nor anything in
    /// <c>ShellCommandPatternMatcher</c>, which parses a shell *command line*, not a permission-scope
    /// clause list); this is new, minimal, and reused nowhere else that would otherwise drift from it.
    /// </para>
    /// <para>
    /// <b>Only <c>Bash(&lt;pattern&gt;)</c> clauses populate the channel.</b> A bare <c>Bash</c> clause
    /// (no parens -- genuinely unscoped shell) is deliberately left out: extracting a pattern from it
    /// would deny an intentionally-unscoped grant, which is the opposite defect from the one this
    /// fixes. This mirrors <see cref="TryTranslatePermissionGrant"/>'s own structured-grant handling,
    /// which likewise emits bare <c>Bash</c> only when <see cref="PermissionGrant.ShellCommandPatterns"/>
    /// is empty. An empty-interior <c>Bash()</c> clause is a different case -- it opens the grant syntax
    /// rather than omitting it -- and is refused rather than left out; see the per-clause throw below
    /// for why (round-5 re-review of PR #1506).
    /// </para>
    /// <para>
    /// <b>Categorically fail-closed (#1459 fix 3, from PR #1506's round-4 re-review).</b> The channel
    /// now accepts <em>only</em> the shape <see cref="TryTranslatePermissionGrant"/> itself ever emits:
    /// top-level comma-separated clauses, each either non-<c>Bash(</c> (ignored, unchanged) or a
    /// balanced <c>Bash(&lt;single pattern&gt;)</c>. Anything else throws
    /// <see cref="PermissionGrantUnsupportedException"/> rather than being silently dropped or guessed
    /// at. Two holes drove this:
    /// </para>
    /// <para>
    /// <b>1. Whole-string balance gate, checked before any split.</b> An unbalanced clause elsewhere in
    /// the scope can eat the top-level comma that should have separated it from a real <c>Bash(</c>
    /// grant -- <c>"Read(,Bash(git diff*)"</c> merges into one blob starting <c>Read(</c>, fails the
    /// <c>Bash(</c> prefix check, and the genuine <c>Bash(git diff*)</c> grant vanishes with no throw,
    /// reopening #1461. So before any clause splitting, if the parentheses across the <em>whole</em>
    /// <paramref name="resolvedScope"/> do not balance (tracked by the private <c>ParensBalance</c>
    /// helper) <em>and</em> the scope contains the substring <c>"Bash("</c>, this throws immediately. A
    /// scope with a stray unbalanced paren but no <c>Bash(</c> substring at all is still read as "no
    /// Bash grant present" and stays a no-op -- the gate exists to protect a real grant from an
    /// unrelated typo, not to reject every malformed scope on principle.
    /// </para>
    /// <para>
    /// <b>2. No comma-list inside one clause, even a balanced one.</b> Fix 2 (below, now superseded)
    /// read <c>Bash(git diff*, git status*)</c> as granting both patterns, on the assumption that
    /// claude's own <c>--allowedTools</c> parser tokenizes an internal comma-list the same way. That
    /// assumption was never measured (tracked as #1514), and this hook channel and claude's own grant
    /// are two independently-maintained layers that must not silently drift apart on an unmeasured
    /// parsing assumption. <see cref="TryTranslatePermissionGrant"/> itself never emits that shape --
    /// multiple patterns always come out as separate <c>Bash(p1),Bash(p2)</c> clauses -- so a clause
    /// whose balanced interior itself splits into more than one top-level piece is refused rather than
    /// honored: write it as separate <c>Bash(...)</c> clauses instead, which this parser still accepts
    /// and joins exactly as <see cref="TryTranslatePermissionGrant"/>'s own multi-pattern grants do.
    /// </para>
    /// <para>
    /// A clause that <em>starts</em> with <c>Bash(</c> but whose parens never balance (no closing
    /// <c>)</c>, or trailing content after the one that closes the outermost paren) is likewise refused
    /// with <see cref="PermissionGrantUnsupportedException"/> rather than silently dropped -- the
    /// pre-fix behaviour otherwise reached the exact same empty-channel shape
    /// <see cref="HookCheckCommand.Decide"/> reads as "deliberately unscoped shell"
    /// (<c>Patterns.Count == 0</c>), reopening this method's own #1459 bypass for any hand-typed scope
    /// a human gets the parens wrong on. This is the resolve-time analogue of
    /// <see cref="ShellCommandPatternMatcher.EvaluateChainedCommand"/>'s <c>Unparseable</c> verdict,
    /// which fails closed on the same kind of ambiguity at decide-time instead -- that method returns a
    /// sentinel because it runs per shell command inside the hook and a thrown exception there is not
    /// a decision the hook process can act on the same way; this one runs once at dispatch construction,
    /// the same place <see cref="TryTranslatePermissionGrant"/>'s own gap already throws
    /// <see cref="PermissionGrantUnsupportedException"/>, so throwing here reuses that precedent rather
    /// than inventing a second fail-closed vocabulary for the same adapter. A clause that is not a
    /// <c>Bash(...)</c> clause at all (no <c>Bash(</c> prefix -- including bare <c>Bash</c>) is
    /// untouched by any of this and stays excluded, per the paragraph above: "no <c>Bash(</c> clause
    /// present" and "a <c>Bash(</c> clause present but unparseable" are different states, and only the
    /// second one throws.
    /// </para>
    /// <para>
    /// <b>Denied patterns are not derivable from this path.</b> The raw scope string carries only what
    /// is ALLOWED -- it feeds <c>--allowedTools</c> alone, and there is no raw-scope equivalent of
    /// <see cref="PermissionGrant.DeniedShellCommandPatterns"/> to parse out of it, so
    /// <see cref="DeniedShellPatternsVariable"/> stays empty on this path, unchanged by this fix. That
    /// is still a strict improvement over the pre-fix behaviour: the hook's own allow-list-and-segment
    /// check (<see cref="ShellCommandPatternMatcher.EvaluateChainedCommand"/>) already denies anything
    /// not explicitly allowed, so an allow-only channel closes the #1461 chaining escape without
    /// needing a deny list of its own.
    /// </para>
    /// <para>
    /// <b>Whitespace before the opening paren is a grant, not text (#1515, #1514).</b> Measured against
    /// claude 2.1.258: <c>Bash (pattern)</c> -- whitespace between <c>Bash</c> and <c>(</c> -- IS
    /// honored by the CLI's own <c>--allowedTools</c> parser as a shell grant, so a clause of that
    /// shape reaching claude's own flag while this method reads it as ordinary non-<c>Bash</c> text
    /// (its <c>StartsWith("Bash(")</c> check fails on the whitespace) reopens the exact #1459 layer
    /// drift this method exists to close. Refused with <see cref="PermissionGrantUnsupportedException"/>
    /// rather than silently dropped. Lowercase <c>bash(</c> was measured the other way -- NOT honored
    /// as a grant on the vendor side -- so it is left alone, still dropped as text; only whitespace
    /// before the paren, case preserved, is refused. #1514's own measurement (a companion issue, not
    /// this one) confirmed the unrelated question above -- that <c>Bash(a, b)</c> is one literal
    /// pattern to the CLI, not two -- so the "no comma-list inside one clause" refusal above already
    /// matches the CLI and needed no change.
    /// </para>
    /// </remarks>
    private static string BuildShellPatternsFromRawScope(string resolvedScope)
    {
        if (string.IsNullOrWhiteSpace(resolvedScope))
        {
            return string.Empty;
        }

        // Whole-string balance gate, checked before any clause splitting -- this method's own remarks
        // above record the round-4 HIGH it closes and the scope this gate is narrowed to.
        if (!ParensBalance(resolvedScope) && resolvedScope.Contains("Bash(", StringComparison.Ordinal))
        {
            throw new PermissionGrantUnsupportedException(
                "claude",
                $"the raw PermissionScope '{resolvedScope}' has unbalanced parentheses somewhere in " +
                "the scope and contains a Bash( grant -- refusing rather than risking an unrelated " +
                "unbalanced clause silently swallowing the real Bash(...) grant");
        }

        // Fusion gate: every Bash( grant must head its own top-level clause. SplitAtTopLevelCommas can
        // only separate grants a top-level comma actually divides, and the loop below drops any clause
        // that doesn't START with Bash( -- so a Bash( grant fused into another clause with no separating
        // comma ("Read()Bash(git diff*)", "Bash(a)Bash(b)", "x Bash(git diff*)") is perfectly balanced,
        // passes the balance gate above, then vanishes silently on the StartsWith continue. The balance
        // gate cannot see this (the string balances); only a count can. If the scope names more Bash(
        // grants than there are top-level clauses that start with Bash(, at least one is buried inside a
        // clause we would drop -- refuse rather than lose it. This is the round-5 closure that makes the
        // no-op path reachable ONLY when no Bash( grant is present at all.
        var clauses = SplitAtTopLevelCommas(resolvedScope);
        var bashGrantOccurrences = CountOccurrences(resolvedScope, "Bash(");
        var bashHeadedClauses = 0;
        foreach (var rawClause in clauses)
        {
            if (rawClause.Trim().StartsWith("Bash(", StringComparison.Ordinal))
            {
                bashHeadedClauses++;
            }
        }

        if (bashGrantOccurrences != bashHeadedClauses)
        {
            throw new PermissionGrantUnsupportedException(
                "claude",
                $"the raw PermissionScope '{resolvedScope}' fuses a Bash(...) grant into another " +
                "clause without a separating top-level comma, where it would be silently dropped from " +
                "the shell-pattern hook channel -- write each Bash(...) grant as its own top-level " +
                "clause (Bash(p1),Bash(p2)) so the #1459 bypass this method exists to close stays shut");
        }

        List<string> patterns = [];
        foreach (var rawClause in clauses)
        {
            var clause = rawClause.Trim();
            if (clause.Length == 0)
            {
                continue;
            }

            // Not a Bash(...) clause at all -- bare `Bash` and every other category (Write, Read, ...)
            // are ignored here, unchanged from before this fix. Only a clause that STARTS a Bash(...)
            // grant and then fails to parse falls into a throw below.
            if (!clause.StartsWith("Bash(", StringComparison.Ordinal))
            {
                // #1515: measured against claude 2.1.258 that `Bash (pattern)` -- whitespace between
                // `Bash` and the opening paren -- IS honored by the CLI's own --allowedTools parser as
                // a shell grant, while this method's StartsWith("Bash(") check reads it as ordinary
                // non-Bash text and drops it. That is the exact #1459 layer drift: claude auto-approves
                // a shell the hook channel never scoped. Lowercase `bash(` was measured NOT to be a
                // grant on the vendor side, so it stays dropped as text -- only whitespace before the
                // paren, case preserved, is refused here.
                if (Regex.IsMatch(clause, @"^Bash\s+\(", RegexOptions.CultureInvariant))
                {
                    throw new PermissionGrantUnsupportedException(
                        "claude",
                        $"the raw PermissionScope clause '{clause}' has whitespace between Bash and " +
                        "its opening paren -- claude's own --allowedTools parser still honors this as " +
                        "a shell grant (measured #1515), so refusing rather than silently dropping it " +
                        "as non-Bash text, which would reopen the #1459 bypass this method exists to " +
                        "close -- write it as the canonical Bash(pattern) form instead");
                }

                continue;
            }

            if (!TryExtractBalancedBashClauseInner(clause, out var inner))
            {
                throw new PermissionGrantUnsupportedException(
                    "claude",
                    $"the raw PermissionScope clause '{clause}' opens a Bash(...) grant whose " +
                    "parentheses never balance -- refusing to silently drop it from the " +
                    "shell-pattern hook channel, which would reopen the #1459 bypass this method " +
                    "exists to close");
            }

            // #1514: whether claude's own --allowedTools tokenizes a comma-list inside one Bash(...)
            // clause the same way this channel would is unmeasured, and TryTranslatePermissionGrant
            // itself never emits that shape -- it always emits separate Bash(p1),Bash(p2) clauses for
            // multiple patterns. Refuse rather than let the two layers drift on a guess.
            if (SplitAtTopLevelCommas(inner).Count > 1)
            {
                throw new PermissionGrantUnsupportedException(
                    "claude",
                    $"the raw PermissionScope clause '{clause}' packs more than one pattern into a " +
                    "single Bash(...) clause via an internal comma -- this channel only honors " +
                    "single-pattern Bash(...) clauses; write separate Bash(p1),Bash(p2) clauses " +
                    "instead (see #1514 for why the comma-list-inside-one-clause form isn't honored)");
            }

            var pattern = inner.Trim();
            if (pattern.Length == 0)
            {
                throw new PermissionGrantUnsupportedException(
                    "claude",
                    $"the raw PermissionScope clause '{clause}' opens a Bash(...) grant with an empty " +
                    "pattern -- this channel yields an empty shell-pattern channel ONLY when no Bash( " +
                    "grant is present at all, so an explicit-but-empty Bash() grant is refused rather " +
                    "than silently read as unscoped, which would reopen the #1459 bypass this method closes");
            }

            patterns.Add(pattern);
        }

        return string.Join(',', patterns);
    }

    /// <summary>
    /// Whole-string parenthesis balance check used by <see cref="BuildShellPatternsFromRawScope"/>'s
    /// balance gate: walks every character, incrementing depth on <c>(</c> and decrementing on
    /// <c>)</c>; a <c>)</c> seen at depth zero (a stray close before any open) makes the string
    /// unbalanced immediately, and any nonzero depth left at the end (an unclosed open) does too.
    /// </summary>
    private static bool ParensBalance(string text)
    {
        var depth = 0;
        foreach (var c in text)
        {
            switch (c)
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    if (depth == 0)
                    {
                        return false;
                    }

                    depth--;
                    break;
            }
        }

        return depth == 0;
    }

    /// <summary>
    /// Counts non-overlapping occurrences of <paramref name="needle"/> in <paramref name="text"/>. Used
    /// by <see cref="BuildShellPatternsFromRawScope"/>'s fusion gate to count how many <c>Bash(</c>
    /// grants the raw scope names, which must equal the number of top-level clauses that START with
    /// <c>Bash(</c> -- any surplus is a grant fused into a clause that would be silently dropped.
    /// </summary>
    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = text.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }

    /// <summary>
    /// Splits <paramref name="text"/> on <c>,</c> at parenthesis depth 0 only -- a comma nested inside
    /// a <c>(...)</c> pair does not split. Shared by <see cref="BuildShellPatternsFromRawScope"/>'s two
    /// uses: splitting the raw scope into clauses, and -- since fix 3 -- checking whether one clause's
    /// already-balanced interior itself contains a top-level comma (which now makes the whole clause
    /// throw rather than grant multiple patterns; see that method's own doc comment). A nested clause
    /// like <c>Bash(foo(bar))</c> parses the same way at both call sites. Unbalanced input (a <c>(</c>
    /// with no matching <c>)</c>) is not an error here -- everything from the unmatched paren onward
    /// simply stays un-split, so the caller sees the malformed text as one piece; balance itself is
    /// judged elsewhere, by <see cref="ParensBalance"/> for the whole scope and by
    /// <see cref="TryExtractBalancedBashClauseInner"/> for one clause.
    /// </summary>
    private static List<string> SplitAtTopLevelCommas(string text)
    {
        List<string> result = [];
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '(':
                    depth++;
                    break;
                case ')' when depth > 0:
                    depth--;
                    break;
                case ',' when depth == 0:
                    result.Add(text[start..i]);
                    start = i + 1;
                    break;
            }
        }

        result.Add(text[start..]);
        return result;
    }

    /// <summary>
    /// Extracts the text between a <c>Bash(</c>-prefixed <paramref name="clause"/>'s outermost parens,
    /// tracking depth so a nested <c>(...)</c> inside the pattern (<c>Bash(foo(bar))</c>) does not end
    /// the clause early. Returns <see langword="false"/> -- the fail-closed signal
    /// <see cref="BuildShellPatternsFromRawScope"/> turns into a thrown
    /// <see cref="PermissionGrantUnsupportedException"/> -- when depth never returns to zero (no
    /// closing paren for the one right after <c>Bash</c>) or when the paren that does close it is not
    /// the clause's last character (trailing content after the grant this method cannot place).
    /// </summary>
    private static bool TryExtractBalancedBashClauseInner(string clause, out string inner)
    {
        var depth = 0;
        for (var i = 4; i < clause.Length; i++) // clause[4] is the '(' right after "Bash"
        {
            switch (clause[i])
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    if (depth == 0)
                    {
                        if (i != clause.Length - 1)
                        {
                            inner = string.Empty;
                            return false;
                        }

                        inner = clause[5..i];
                        return true;
                    }

                    break;
            }
        }

        inner = string.Empty;
        return false;
    }

    /// <summary>
    /// The deny-list mirror of <see cref="TryTranslatePermissionGrant"/> (#331): every category the
    /// grant <em>withholds</em> maps to the Claude Code tool(s) that would otherwise reach it, emitted
    /// as <c>--disallowedTools</c>. This is what makes a withheld checkbox true — <c>--allowedTools</c>
    /// only auto-approves, it does not remove an unlisted tool from the model's reach.
    /// <para>
    /// <b>Except the write tools, since #649.</b> <c>Edit</c>/<c>Write</c>/<c>NotebookEdit</c> are
    /// withheld by the <c>PreToolUse</c> hook alone (<see cref="BuildHookDeniedTools"/>), because a
    /// name on this flag is refused by the CLI before the hook can allow the one write landing in
    /// <c>BATON_OUTPUT_DIR</c>. <c>ChannelPopulationTests</c> holds the two channels to that split
    /// across all sixteen grants.
    /// </para>
    /// <para>
    /// <b>Boundary:</b> denial here is by <em>enumeration</em>, not default-deny. It covers the tools a
    /// grant category names; it does not cover tools outside the grant's four categories (<c>Task</c>,
    /// MCP server tools, or a tool a future CLI adds). Decision 0004's project ceiling
    /// (<see cref="ProjectCeilingGate"/>, #1166) narrows the same four categories this method already
    /// enumerates before <c>Resolve</c> ever reaches here — it does not widen this method's boundary,
    /// which is unchanged: still category-mapped, still silent on a tool outside the four. Returns
    /// <see cref="string.Empty"/> when there is no structured grant (the
    /// raw <see cref="WorkerInvocation.PermissionScope"/> escape hatch carries no category to deny) or
    /// when nothing is withheld.
    /// </para>
    /// <para>
    /// <b>WHAT THIS DOES NOT GUARANTEE — read before relying on it (#529, measured 2026-07-25).</b>
    /// This method bounds <em>which tool runs</em>. It does <em>not</em> bound what the worker can
    /// achieve, because <b>the model substitutes another tool and reaches the same goal</b>. Measured
    /// with <c>--disallowedTools Edit,Write,NotebookEdit</c> — the string this method emitted for a
    /// withheld-write grant before #649 moved those names to the hook: the file was created anyway,
    /// by <c>Bash</c>.
    /// Because the four categories are independent, <c>Bash</c> stays available whenever
    /// <see cref="PermissionGrant.RunShellCommands"/> is granted — and <c>Bash</c> alone defeats
    /// withheld <em>writes</em>, withheld <em>reads</em> (<c>cat</c>) and withheld <em>network</em>
    /// (<c>curl</c>). The caveat in the previous paragraph is about tools outside the four categories;
    /// this hole is <em>inside</em> them, and write-withheld-plus-shell-granted is a common grant
    /// shape rather than an exotic one.
    /// </para>
    /// <para>
    /// A <em>resolved binding</em> can no longer carry that shape:
    /// <see cref="WorkerBindingResolver.Resolve"/> refuses it
    /// (<see cref="IncoherentPermissionGrantException"/>). That narrows which grants reach this
    /// method; it does not close the gap, which is why everything above still holds. The substitution
    /// itself is untouched, and an entry using the raw <c>PermissionScope</c> escape hatch carries no
    /// <see cref="PermissionGrant"/> for that refusal to inspect — so it arrives here with
    /// <c>grant is null</c> and nothing denied at all.
    /// </para>
    /// <para>
    /// Treat the result as <b>pre-approval and routing, never as a security boundary</b>. The
    /// mechanisms measured to stop an <em>operation</em> gate on the operation rather than the tool
    /// (a <c>PreToolUse</c> hook exiting 2, an explicit <c>ask</c> rule, a hook returning
    /// <c>permissionDecision: "ask"</c>, and <c>requiresUserInteraction</c> on MCP tools), which is
    /// exactly why substitution does not defeat them. See <c>docs/vendor-doc-audit.md</c>; re-runnable
    /// via <c>pixi run vendor-verify -- --only gate.allowedtools-is-preapproval-not-ceiling</c>.
    /// </para>
    /// </summary>
    private static string BuildDisallowedTools(PermissionGrant? grant)
    {
        if (grant is null)
        {
            return string.Empty;
        }

        var names = WithheldToolNames(grant, includeWriteTools: false);
        names.AddRange(StandingShellDenials(grant));
        return string.Join(',', names);
    }

    /// <summary>
    /// 0022's DenyAlways families (#390) as <c>--disallowedTools</c> entries — <c>Bash(pattern)</c> per
    /// <see cref="PermissionGrant.DeniedShellCommandPatterns"/>, empty when none. This is claude's
    /// enforcement for the standing-"never" rung on BOTH dispatch paths: the CLI applies
    /// <c>--disallowedTools</c> with precedence over <c>--allowedTools</c> (measured — <c>git push</c>
    /// denied under <c>--allowedTools "Bash(git *)" --disallowedTools "Bash(git push*)"</c>,
    /// <c>docs/vendor-capabilities.md</c>) and hard-refuses BEFORE the hook, so a denied family is
    /// refused even under an unscoped grant and without re-asking. Under the runtime gate this is the
    /// <em>whole</em> of what <c>--disallowedTools</c> carries (withheld categories ride the ask band);
    /// off the gate it rides alongside them. Enforced independently of the <c>PreToolUse</c> hook, so it
    /// survives a silently-dead hook (#530). <b>#1731 corrected the claim that claude "needs no
    /// hook-side deny check"</b> — this flag matches the whole command line as typed (#1461), so it
    /// never catches a denied family riding a chain (<c>true &amp;&amp; gh label create x</c>); the hook's
    /// own segmented deny check (<c>HookCheckCommand</c>, <c>toolName == "Bash"</c>) closes that gap and
    /// is not merely belt-and-braces on top of this flag. See spec/baton.md §9.
    /// <para>
    /// <b><see cref="PermissionGrant.DeniedShellOptionTokens"/> deliberately emits nothing here
    /// (#1683 F2)</b> — that rung is hook-only on this vendor. The reasoning and the measurement gap
    /// behind that choice live on <c>ShellCommandPatternMatcher.IsDeniedByOptionToken</c>; a change to
    /// this line edits it there first. Pinned by
    /// <c>ClaudeWorkerAdapterTests.Denied_option_tokens_ride_the_hook_channel_and_deliberately_reach_no_vendor_flag</c>,
    /// so wiring it onto the flag fails a test rather than passing quietly.
    /// </para>
    /// </summary>
    private static IEnumerable<string> StandingShellDenials(PermissionGrant? grant) =>
        grant?.DeniedShellCommandPatterns is { Count: > 0 } denied
            ? denied.Select(pattern => $"Bash({pattern})")
            : [];

    /// <summary>
    /// The withheld tool names carried to the <c>PreToolUse</c> hook — the same list
    /// <see cref="BuildDisallowedTools"/> emits, <b>plus</b> the write tools it deliberately omits (#649).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two lists differ on exactly one category, and the difference is the whole of #649. A write
    /// named in <c>--disallowedTools</c> is refused by the CLI before the hook is consulted, so the
    /// hook could never allow a worker to write its own declared output — which is why a read-only
    /// reviewer could not produce a deliverable, and why every reviewing template granted a workspace
    /// write it never needed. Withholding writes therefore moves off the flag and onto the hook, which
    /// can see the target path and allow only the ones landing in <c>BATON_OUTPUT_DIR</c>.
    /// </para>
    /// <para>
    /// <b>This is an enforcement-boundary change, not a refactor.</b> Writes were denied by the flag
    /// measured to actually enforce (<c>gate.allowedtools-is-preapproval-not-ceiling</c> established
    /// that only the deny list does) and are now denied by the hook. Three things bound it: 0029 makes
    /// the hook mandatory on every spawned worker, #600 makes a missing or wrong-vendor denied list
    /// deny rather than allow, and on agy this changes nothing at all — under
    /// <c>--dangerously-skip-permissions</c> the hook was already the only boundary. Every other
    /// category keeps its flag denial as well as its hook entry, so only writes move.
    /// </para>
    /// </remarks>
    internal static string BuildHookDeniedTools(PermissionGrant? grant) =>
        grant is null ? string.Empty : string.Join(',', WithheldToolNames(grant, includeWriteTools: true));

    /// <summary>
    /// Yes, by the two mechanisms above acting together (#649): the write tools stay pre-approved on
    /// <c>--allowedTools</c> so the model can invoke them, they are absent from
    /// <see cref="BuildDisallowedTools"/> so the CLI does not refuse them first, and
    /// <see cref="BuildHookDeniedTools"/> hands the hook the names it confines to
    /// <c>BATON_OUTPUT_DIR</c>. Verified live: a <c>WriteFiles: false</c> worker wrote its declared
    /// output and failed to write its workspace.
    /// </summary>
    public bool WithheldWritesReachTheOutbox => true;

    /// <summary>
    /// #599, corrected to a component match by #1827/#1834's measurement — see the interface member's
    /// own remarks for what the predicate actually keys on and why
    /// <see cref="BatonEnvironmentSnapshot.ClaudeConfigRootOverride"/> plays no part in it.
    /// </summary>
    public bool HasSensitiveOutputPathComponent(string roomDirectoryPath, out string? offendingComponent)
    {
        offendingComponent = null;
        if (string.IsNullOrWhiteSpace(roomDirectoryPath))
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var fullPath = Path.GetFullPath(roomDirectoryPath);
        foreach (var component in fullPath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(component, SensitiveOutputPathComponentName, comparison))
            {
                offendingComponent = component;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The literal path component name #1827's measurement pins claude's write refusal to, independent
    /// of <c>CLAUDE_CONFIG_DIR</c>'s value.
    /// </summary>
    private const string SensitiveOutputPathComponentName = ".claude";

    private static List<string> WithheldToolNames(PermissionGrant grant, bool includeWriteTools)
    {
        List<string> denied = [];
        if (!grant.ReadFiles)
        {
            denied.Add("Read");
        }

        if (!grant.WriteFiles && includeWriteTools)
        {
            denied.Add("Edit");
            denied.Add("Write");
            denied.Add("NotebookEdit");
        }

        if (!grant.RunShellCommands)
        {
            denied.Add("Bash");
        }

        if (!grant.NetworkAccess)
        {
            denied.Add("WebFetch");
            denied.Add("WebSearch");
        }

        return denied;
    }

    private static string BuildPrompt(string promptTemplate, WorkerContract contract, bool isWindows)
    {
        var prompt = new StringBuilder(promptTemplate);

        if (contract.RequiredInputs.Count > 0)
        {
            prompt.Append("\n\nInputs, in the order listed, are available at:\n");
            for (var i = 0; i < contract.RequiredInputs.Count; i++)
            {
                prompt.Append($"- {contract.RequiredInputs[i]}: {EnvironmentReference($"BATON_INPUT_{i}", isWindows)}\n");
            }
        }

        if (contract.ProducedOutputs.Count > 0)
        {
            prompt.Append("\nWrite each of the following outputs to the exact path shown, creating parent directories as needed:\n");
            foreach (var output in contract.ProducedOutputs)
            {
                var outputDir = EnvironmentReference("BATON_OUTPUT_DIR", isWindows);
                var separator = isWindows ? '\\' : '/';
                prompt.Append($"- {output.Name}: {outputDir}{separator}{output.Name}\n");
            }
        }

        return prompt.ToString();
    }

    private static string EnvironmentReference(string name, bool isWindows) =>
        WorkerEnvironmentReference.For(name, isWindows);

    /// <summary>
    /// Projects canonical skill packages from <c>skills/&lt;name&gt;/SKILL.md</c> into the location Claude CLI
    /// reads (<c>.claude/skills/&lt;name&gt;/</c>) under the working directory (#1151).
    /// Copies all files in the package directory preserving layout.
    /// Returns the list of projected file paths, or an empty list if no canonical skill packages exist.
    /// </summary>
    public static IReadOnlyList<string> ProjectSkills(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return Array.Empty<string>();
        }

        var packages = SkillPackageReader.DiscoverPackages(workingDirectory);
        if (packages.Count == 0)
        {
            return Array.Empty<string>();
        }

        var targetBase = Path.Combine(workingDirectory, ".claude", "skills");
        var projectedPaths = new List<string>();

        foreach (var package in packages)
        {
            var targetDir = Path.Combine(targetBase, package.Name);
            Directory.CreateDirectory(targetDir);

            foreach (var file in Directory.GetFiles(package.DirectoryPath, "*", SearchOption.AllDirectories))
            {
                var relPath = Path.GetRelativePath(package.DirectoryPath, file);
                var destFile = Path.Combine(targetDir, relPath);
                var destDir = Path.GetDirectoryName(destFile);
                if (!string.IsNullOrEmpty(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }
                File.Copy(file, destFile, overwrite: true);
                projectedPaths.Add(destFile);
            }
        }

        return projectedPaths;
    }

    /// <summary>
    /// Claude Code has no machine-readable "list models" subcommand — <c>--model</c> only documents
    /// its accepted values as help-text examples (<c>claude --help</c>: "Provide an alias for the
    /// latest model (e.g. 'sonnet', 'opus') or a model's full name"). Aliases are the stable
    /// interface here: each always resolves to that tier's current model, so this list doesn't need
    /// updating every model generation the way a hardcoded full model ID would.
    /// </summary>
    public static readonly IReadOnlyList<string> ModelAliases = ["sonnet", "opus", "haiku"];

    /// <summary>
    /// #1090: a <c>claude-*</c> id whose version is dot-delimited (<c>claude-opus-4.8</c>) is a typo for
    /// the dash form (<c>claude-opus-4-8</c>) — see <see cref="MalformedVendorModelException"/> for the
    /// measurement. Scoped to the <c>claude-</c> prefix + a digit.digit run so it cannot fire on an
    /// alias (no dot) or a valid dash id; this is NOT a model-list check — claude ships none, see
    /// <see cref="ModelAliases"/>.
    /// </summary>
    private static readonly Regex DotDelimitedClaudeVersion =
        new(@"^claude-.*\d\.\d", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static void RefuseDotDelimitedClaudeModelId(string model)
    {
        if (DotDelimitedClaudeVersion.IsMatch(model))
        {
            var suggestion = Regex.Replace(model, @"(\d)\.(\d)", "$1-$2");
            throw new MalformedVendorModelException(
                "claude",
                $"'{model}' is dot-delimited; claude model ids use dashes. Did you mean '{suggestion}'?");
        }
    }

    private static readonly TimeSpan SkillDiscoveryTimeout = TimeSpan.FromSeconds(5);

    public Task<WorkerCapabilities> DiscoverCapabilitiesAsync(string? workingDirectory = null, CancellationToken cancellationToken = default) =>
        DiscoverCapabilitiesAsync(workingDirectory, userHomeDirectory: null, cancellationToken: cancellationToken);

    public Task<WorkerCapabilities> DiscoverCapabilitiesAsync(
        string? workingDirectory, string? userHomeDirectory, string? configRootDirectory = null, CancellationToken cancellationToken = default) =>
        BoundedDiscoverAsync(() => DiscoverCapabilitiesCore(workingDirectory, userHomeDirectory, configRootDirectory), cancellationToken);

    /// <summary>
    /// #1512 M7: <see cref="DiscoverCapabilitiesCore"/> is synchronous, unbounded file I/O — a full
    /// <c>File.ReadAllText</c> of every <c>SKILL.md</c> under both arms, with no timeout of its own.
    /// Before #1512 this method had no production call site; it is now on the critical path of every
    /// <c>baton dispatch</c> preamble, so a roaming/UNC <c>%USERPROFILE%</c> share that hangs must not
    /// hang the whole dispatch. <c>Task.Run</c> plus a linked timeout cannot abort the delegate
    /// mid-read once it has started (no managed API can interrupt a blocked synchronous file read),
    /// but it stops the caller from *awaiting* past the timeout, which is what keeps the preamble
    /// itself responsive — mirrors <c>AgyWorkerAdapter.DiscoverySubcommandTimeout</c>'s bound on the
    /// analogous risk for its subprocess calls.
    /// </summary>
    private static async Task<WorkerCapabilities> BoundedDiscoverAsync(Func<WorkerCapabilities> discover, CancellationToken cancellationToken)
    {
        var discoveryTask = Task.Run(discover, cancellationToken);
        var timeoutTask = Task.Delay(SkillDiscoveryTimeout, cancellationToken);
        var completed = await Task.WhenAny(discoveryTask, timeoutTask).ConfigureAwait(false);
        if (completed == discoveryTask)
        {
            return await discoveryTask.ConfigureAwait(false);
        }

        // Task.Delay observes cancellationToken too, so the delay task can "win" either because the
        // timeout genuinely elapsed or because the caller cancelled -- awaiting it rethrows
        // OperationCanceledException in the latter case, so cancellation still propagates as
        // cancellation rather than silently degrading to an empty roster.
        await timeoutTask.ConfigureAwait(false);
        return new WorkerCapabilities("claude", Array.Empty<WorkerCapabilityItem>(), ModelAliases);
    }

    private static WorkerCapabilities DiscoverCapabilitiesCore(string? workingDirectory, string? userHomeDirectory, string? configRootDirectory)
    {
        var items = new List<WorkerCapabilityItem>();
        var projectSkillDirs = new List<string>();
        var configRootSkillDirs = new List<string>();
        var userHomeSkillDirs = new List<string>();
        var commandDirs = new List<string>();

        if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
        {
            projectSkillDirs.Add(Path.Combine(workingDirectory, ".claude", "skills"));
            commandDirs.Add(Path.Combine(workingDirectory, ".claude", "commands"));
        }

        // #1512 M3: BATON_CLAUDE_CONFIG_ROOT redirects every spawned `claude`'s CLAUDE_CONFIG_DIR to a
        // shared root (see the injection near BatonClaudeConfigRootVariable's other use, and
        // docs/runbooks/claude-shared-config-root.md) -- when set, that root IS the worker's personal
        // config directory, replacing ~/.claude wholesale, not a home directory with a .claude
        // subdirectory underneath it. Whether Claude Code itself relocates *skill* lookup under a
        // redirected CLAUDE_CONFIG_DIR the same way it relocates auth/session state is unmeasured in
        // this repo (no docs/vendor-doc-audit.md entry covers it) -- but hardcoding %USERPROFILE%
        // while the adapter redirects the root is defensible in neither reading, so this follows the
        // root when the operator has actually set one, rather than assert a roster for a directory the
        // worker does not use.
        // Read through the snapshot, not the process env: #1524 folded BATON_CLAUDE_CONFIG_ROOT so a
        // BeginScope override is honoured here exactly as at the launch-config site above.
        var configRoot = configRootDirectory ?? BatonEnvironmentSnapshot.Current.ClaudeConfigRootOverride;
        if (!string.IsNullOrWhiteSpace(configRoot) && Directory.Exists(configRoot))
        {
            // #1575: flat `<root>/skills/`, not `<root>/.claude/skills/`, confirming #1566's
            // assumption.
            // record-once-ok: #1575 docs/vendor-doc-audit.md
            configRootSkillDirs.Add(Path.Combine(configRoot, "skills"));
            commandDirs.Add(Path.Combine(configRoot, "commands"));
        }
        else
        {
            var userHome = userHomeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(userHome) && Directory.Exists(userHome))
            {
                userHomeSkillDirs.Add(Path.Combine(userHome, ".claude", "skills"));
                commandDirs.Add(Path.Combine(userHome, ".claude", "commands"));
            }
        }

        // #1575: the CLI does not receive `--setting-sources` anywhere in this adapter's spawn argv
        // (see the args list built above in Resolve), so its default applies and user-scope skills
        // load alongside project ones. docs/vendor-doc-audit.md's #1575 entry pins the precedence
        // this relies on, scoped to a BATON_CLAUDE_CONFIG_ROOT-redirected root specifically: on a
        // name collision there, the CLI resolves to the config-root copy, not the project copy -- so
        // config-root skills are scanned first, ahead of project, making the GroupBy(...).First()
        // dedup below keep that copy. The plain ~/.claude fallback (no config root set) was never
        // part of that measurement, so it keeps its prior project-over-user ordering (L3) rather
        // than being changed on an assumption.
        // Canonical skill packages realization: discovered from skills/<name>/SKILL.md and reported as projected (#1151)
        var canonicalNames = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
        {
            var canonicalSkills = SkillPackageReader.DiscoverPackages(workingDirectory);
            foreach (var package in canonicalSkills)
            {
                canonicalNames.Add(package.Name);
                items.Add(new WorkerCapabilityItem($"{package.Name} (projected)", "skill", package.Description));
            }
        }

        foreach (var skillsDir in configRootSkillDirs.Concat(projectSkillDirs).Concat(userHomeSkillDirs))
        {
            var nativeSkills = SkillScanner.DiscoverSkills(skillsDir);
            foreach (var skill in nativeSkills)
            {
                if (!canonicalNames.Contains(skill.Name))
                {
                    items.Add(skill);
                }
            }
        }

        foreach (var commandsDir in commandDirs)
        {
            if (Directory.Exists(commandsDir))
            {
                try
                {
                    foreach (var file in Directory.GetFiles(commandsDir, "*.md"))
                    {
                        var name = Path.GetFileNameWithoutExtension(file);
                        items.Add(new WorkerCapabilityItem($"/{name}", "command", $"Custom command /{name}"));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    // #1512 M1: distinguishable from "no commands" -- see SkillScanner's own catch for
                    // the same rationale (fail open, but not silently).
                    Console.Error.WriteLine($"Warning: could not read commands directory '{commandsDir}': {ex.Message}");
                }
            }
        }

        items.Add(new WorkerCapabilityItem("/compact", "command", "Summarize and compact session history"));
        items.Add(new WorkerCapabilityItem("/clear", "command", "Clear session context"));

        // L3 / #1575: First() keeps the earliest entry when both arms report the same name, so
        // precedence is produced by scan order, not here. Commands keep project-over-config
        // ordering (unmeasured either way, left as it was). Skills scan config-root before project
        // before plain user-home (above), so a colliding skill name resolves to whichever copy the
        // CLI itself actually loads -- see the comment at the skill scan loop.
        var uniqueItems = items.GroupBy(i => i.Name).Select(g => g.First()).ToList();
        return new WorkerCapabilities("claude", uniqueItems, ModelAliases);
    }

    /// <summary>
    /// Parses one line of <c>claude --output-format stream-json --verbose</c>'s newline-delimited JSON
    /// (M24 Phase 1's live in-turn streaming). The <c>system</c>/<c>assistant</c> envelopes below are
    /// confirmed against a real, live invocation of the installed CLI (a same-shape
    /// <c>{"type":"assistant","message":{"content":[{"type":"text",...}]}}</c> line came back even from
    /// an unauthenticated run's error response) — those branches are load-bearing.
    /// The <c>stream_event</c>/<c>content_block_delta</c> branch mirrors the publicly documented
    /// Anthropic Messages streaming event shape Claude Code wraps for <c>--include-partial-messages</c>'
    /// token-level deltas. <b>Retained but currently unreachable</b>: #1540 deliberately dropped
    /// <c>--include-partial-messages</c> from every dispatch (event-level volume over token-level, to
    /// keep <c>ExecutionStreamLogger</c>'s 8 MiB window from rolling early — see
    /// <c>docs/vendor-capabilities.md</c>'s `#1540` row), so no live invocation can produce this shape
    /// today. Left in place rather than deleted: if a future issue reintroduces the flag, this branch
    /// is already correct and does not need rediscovering; if the real shape differs from the
    /// documented one, this simply never matches and contributes no partial deltas — full per-message
    /// text (the confirmed branch above) still arrives once each block completes.
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
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var typeProp))
            {
                return false;
            }

            return typeProp.GetString() switch
            {
                "system" => TryParseSystemEvent(root, out progressEvent),
                "assistant" => TryParseAssistantEvent(root, out progressEvent),
                "stream_event" => TryParseStreamEvent(root, out progressEvent),
                "result" => TryParseResultEvent(root, out progressEvent),
                _ => false,
            };
        }
        catch (JsonException)
        {
            // A line split across a stdout chunk boundary, or a non-JSON line this format never
            // produces -- not a progress event, not an error.
            return false;
        }
    }

    private static bool TryParseSystemEvent(JsonElement root, out WorkerProgressEvent? progressEvent)
    {
        progressEvent = null;
        if (!root.TryGetProperty("subtype", out var subtypeProp))
        {
            return false;
        }

        switch (subtypeProp.GetString())
        {
            case "init":
                progressEvent = new WorkerProgressEvent("status", "Session started");
                return true;
            case "status" when root.TryGetProperty("status", out var statusProp) && statusProp.GetString() is { Length: > 0 } status:
                progressEvent = new WorkerProgressEvent("status", status);
                return true;
            default:
                // A recognized `system` envelope whose subtype carries no user-facing signal (e.g. a
                // hook lifecycle marker) — deliberately filtered, not unknown (#1561 second-reader
                // review: falling through to `false` here would dump it as raw JSON instead).
                progressEvent = new WorkerProgressEvent("ignore", string.Empty);
                return true;
        }
    }

    /// <summary>
    /// Returns the FIRST text-or-tool_use content block of an <c>assistant</c> message only (#1561
    /// finding 9) — a message carrying <c>[text, tool_use]</c> surfaces the text and never the
    /// <c>[tool: ...]</c> marker, and a two-text-block message loses the second block.
    /// <see cref="WorkerProgressEvent"/> carries one event per call; looping every block would need a
    /// return shape wider than <c>TryParseProgressEvent</c>'s single out-parameter, which is out of
    /// scope for a doc-comment-only fix. Pre-existing behavior — what changed is that the caller
    /// (<c>--echo-worker</c>) makes it user-visible now.
    /// </summary>
    private static bool TryParseAssistantEvent(JsonElement root, out WorkerProgressEvent? progressEvent)
    {
        progressEvent = null;
        if (!root.TryGetProperty("message", out var messageProp) ||
            !messageProp.TryGetProperty("content", out var contentProp) ||
            contentProp.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var block in contentProp.EnumerateArray())
        {
            if (!block.TryGetProperty("type", out var blockTypeProp))
            {
                continue;
            }

            switch (blockTypeProp.GetString())
            {
                case "text" when block.TryGetProperty("text", out var textProp) && textProp.GetString() is { Length: > 0 } text:
                    progressEvent = new WorkerProgressEvent("text", text);
                    return true;
                case "tool_use" when block.TryGetProperty("name", out var nameProp) && nameProp.GetString() is { Length: > 0 } toolName:
                    progressEvent = new WorkerProgressEvent("tool", toolName);
                    return true;
            }
        }

        // A recognized `assistant` message whose content blocks carry no renderable text/tool_use —
        // e.g. a `thinking`-only block under extended thinking, or an empty text block — is
        // deliberately filtered, not unknown (#1561 second-reader review: this shape appears in the
        // very capture docs/vendor-capabilities.md's #1540 row quotes, once per turn on the primary
        // path with extended thinking on; falling through to `false` here would dump the raw
        // envelope, base64 thinking signature included, instead of staying quiet).
        progressEvent = new WorkerProgressEvent("ignore", string.Empty);
        return true;
    }

    /// <summary>
    /// Surfaces the <c>result</c> envelope as a turn-completion status line (issue #1561) — the one
    /// signal <see cref="EchoStreamJsonLine"/> needs to show WHY a lane failed, which the pre-#1561
    /// echo switch could never render because <see cref="TryParseProgressEvent"/> returned <c>false</c>
    /// for every <c>result</c> line. <c>is_error</c> decides the rendered text; the human-readable
    /// <c>result</c> field carries the failure reason on an error turn (e.g. "Subscription quota
    /// exhausted.", the exact string <see cref="TryClassifyFailure"/>'s quota fixtures use).
    /// <c>is_error</c> is required, not defaulted: every observed envelope carries it (both polarities
    /// — see the <c>docs/vendor-capabilities.md</c> `#1540` row's quoted capture), so its absence means
    /// an unfamiliar shape, not a confirmed success. Rendering "success" on a guess would fabricate a
    /// claim this method has no basis for (#1561 second-reader review) — return <c>false</c> instead
    /// and let the generic verbatim-echo fallback show the raw line.
    /// </summary>
    private static bool TryParseResultEvent(JsonElement root, out WorkerProgressEvent? progressEvent)
    {
        progressEvent = null;
        if (!root.TryGetProperty("is_error", out var isErrorProp)
            || isErrorProp.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        var isError = isErrorProp.ValueKind == JsonValueKind.True;
        var text = isError
            ? "error — " + (root.TryGetProperty("result", out var resultProp)
                && resultProp.ValueKind == JsonValueKind.String
                && resultProp.GetString() is { Length: > 0 } summary
                ? summary
                : "no error detail in the result envelope")
            : "success";

        progressEvent = new WorkerProgressEvent("result", text);
        return true;
    }

    /// <summary>
    /// Parses claude's <c>stream-json</c> terminal <c>"type":"result"</c> line (issue #1360, extended
    /// by #1569). Delegates to <see cref="ClaudeUsageParser"/> (#1599) — the same read
    /// <see cref="StandardWorkerUsageParsers.Default"/> registers for the <c>terminal.json</c>/
    /// <c>fleet_status</c> surface, so <c>baton status --json</c> resolves through exactly one
    /// implementation rather than a hand-duplicated second one. See that class's own doc comment for
    /// field-by-field provenance, the subagent-fan-out undercount (docs/vendor-doc-audit.md, #479),
    /// and why <c>modelUsage</c>/<c>total_cost_usd</c> are read by neither side.
    /// </summary>
    public bool TryParseFinalUsage(string rawLine, out WorkerUsage? usage) =>
        UsageParser.TryParseFinalUsage(rawLine, out usage);

    private static readonly ClaudeUsageParser UsageParser = new();

    /// <summary>
    /// #1594: recovers claude's own final answer from the same terminal <c>"type":"result"</c> line
    /// <see cref="TryParseFinalUsage"/> and <see cref="TryParseResultEvent"/> already key on. Unlike
    /// <see cref="TryParseResultEvent"/>, which reads <c>result</c> as an error summary on the
    /// <c>is_error: true</c> arm, this only ever reads it on the success arm
    /// (<c>is_error == false</c>) — an error turn's <c>result</c> text is a failure reason, not a
    /// worker's answer, and capturing one would be a failure message masquerading as a real report.
    /// <c>is_error</c> is required, not defaulted, for the
    /// same reason <see cref="TryParseResultEvent"/> requires it: every observed envelope carries it,
    /// so its absence means an unfamiliar shape rather than a confirmed success.
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
                || !root.TryGetProperty("type", out var typeProp)
                || typeProp.ValueKind != JsonValueKind.String || typeProp.GetString() != "result"
                || !root.TryGetProperty("is_error", out var isErrorProp)
                || isErrorProp.ValueKind != JsonValueKind.False
                || !root.TryGetProperty("result", out var resultProp)
                || resultProp.ValueKind != JsonValueKind.String
                || resultProp.GetString() is not { Length: > 0 } text)
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

    /// <summary>
    /// #1841: recovers claude's own session id from a <c>stream-json</c> <c>"type":"system"</c>,
    /// <c>"subtype":"init"</c> line — read-side only, never minted client-side (see
    /// <c>WorkerBindingConfigEntry.SessionId</c>'s dispatch-path caller for why: the resolved
    /// <see cref="Baton.Dispatch.CoreDispatchTarget"/> argv is frozen once per binding and reused
    /// verbatim across every #1373 retry, and claude's own <c>--session-id</c> reuse is
    /// existence-guarded, so a client-minted id baked into that argv would fail a retry outright).
    /// Confirmed against a recorded fixture
    /// (<c>tests/Baton.Cli.Tests/Fixtures/claude-stream-json-sample.log</c>'s own <c>init</c> line) —
    /// unlike <see cref="TryParseFinalResponse"/>, no terminal <c>"type":"result"</c> line carrying
    /// <c>session_id</c> has been recorded, so that arm is deliberately not parsed here; every
    /// observed envelope carries <c>session_id</c> on <c>init</c>, so this is the one shape claimed.
    /// </summary>
    public bool TryParseSessionId(string rawLine, out string? sessionId)
    {
        sessionId = null;
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawLine);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var typeProp) || typeProp.GetString() != "system"
                || !root.TryGetProperty("subtype", out var subtypeProp) || subtypeProp.GetString() != "init"
                || !root.TryGetProperty("session_id", out var sessionIdProp)
                || sessionIdProp.ValueKind != JsonValueKind.String
                || sessionIdProp.GetString() is not { Length: > 0 } id)
            {
                return false;
            }

            sessionId = id;
            return true;
        }
        catch (JsonException)
        {
            // A line split across a stdout chunk boundary, or a non-JSON line -- not a session id.
            return false;
        }
    }

    private static bool TryParseStreamEvent(JsonElement root, out WorkerProgressEvent? progressEvent)
    {
        progressEvent = null;
        if (root.TryGetProperty("event", out var eventProp) &&
            eventProp.TryGetProperty("type", out var eventTypeProp) &&
            eventTypeProp.GetString() == "content_block_delta" &&
            eventProp.TryGetProperty("delta", out var deltaProp) &&
            deltaProp.TryGetProperty("type", out var deltaTypeProp) &&
            deltaTypeProp.GetString() == "text_delta" &&
            deltaProp.TryGetProperty("text", out var deltaTextProp) &&
            deltaTextProp.GetString() is { Length: > 0 } deltaText)
        {
            progressEvent = new WorkerProgressEvent("text", deltaText, IsPartial: true);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Interprets Claude-specific failure output into a <see cref="FailureClassification"/> and reset instant (issue #1115).
    /// </summary>
    public bool TryClassifyFailure(
        string? stderrTail,
        TimeProvider timeProvider,
        out FailureClassification? classification,
        out DateTimeOffset? retryNotBefore)
    {
        return TryClassifyFailure(stderrTail, null, timeProvider, out classification, out retryNotBefore);
    }

    /// <summary>
    /// Interprets Claude-specific failure output from stderr and stdout tails into a <see cref="FailureClassification"/> and reset instant (issue #1115).
    /// </summary>
    public bool TryClassifyFailure(
        string? stderrTail,
        string? stdoutTail,
        TimeProvider timeProvider,
        out FailureClassification? classification,
        out DateTimeOffset? retryNotBefore)
    {
        if (TryClassifyQuotaExhaustion(stderrTail, timeProvider, out classification, out retryNotBefore))
        {
            return true;
        }

        return TryClassifyQuotaExhaustion(stdoutTail, timeProvider, out classification, out retryNotBefore);
    }


    /// <summary>
    /// Recognizes Claude subscription quota exhaustion errors from the typed field <c>errorCode == "credits_required"</c>
    /// (decision 0026 §1a, issue #1115), and from the CLI's <c>assistant</c>-line <c>rate_limit</c> envelope
    /// (issue #1609: bundle-derived from Claude Code 2.1.258, not yet confirmed against a live capture —
    /// see the reset-instant parse below for the caveat this rests on).
    /// </summary>
    public static bool TryClassifyQuotaExhaustion(
        string? stderrOrReason,
        TimeProvider timeProvider,
        out FailureClassification? classification,
        out DateTimeOffset? retryNotBefore) =>
        TryClassifyQuotaExhaustion(stderrOrReason, timeProvider, out classification, out retryNotBefore, out _);

    /// <summary>
    /// Same as the four-out overload, plus <paramref name="quotaLimitsPlacement"/> recording which of
    /// <c>quotaLimits@root</c> / <c>quotaLimits@message</c> / <c>text-suffix</c> produced the rate-limit
    /// classification (<c>null</c> for the typed <c>credits_required</c> path, which reads neither).
    /// Internal for now: no caller outside this adapter and its tests needs the placement yet, but it
    /// is the seam a future correlation against a live capture (or a park-reason detail string) would
    /// hang off without touching <see cref="FailureClassification"/>'s shape (#1810 review).
    /// </summary>
    internal static bool TryClassifyQuotaExhaustion(
        string? stderrOrReason,
        TimeProvider timeProvider,
        out FailureClassification? classification,
        out DateTimeOffset? retryNotBefore,
        out string? quotaLimitsPlacement)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        classification = null;
        retryNotBefore = null;
        quotaLimitsPlacement = null;

        if (string.IsNullOrWhiteSpace(stderrOrReason))
        {
            return false;
        }

        if (ContainsTypedCreditsRequiredError(stderrOrReason))
        {
            classification = FailureClassification.ExhaustedUntil;
            retryNotBefore = null;
            return true;
        }

        return TryClassifyRateLimitEnvelopeFromText(stderrOrReason, timeProvider, out classification, out retryNotBefore, out quotaLimitsPlacement);
    }

    /// <summary>
    /// Recognizes the CLI's synthetic <c>assistant</c>-line rate-limit envelope (#1609, zero-spend
    /// measurement 2026-09-03: read out of the installed CLI bundle's minified strings, not yet seen
    /// live). The envelope carries <c>error == "rate_limit"</c> at its root and a <c>quotaLimits</c>
    /// object whose OWN placement -- root sibling of the stream-json line's <c>message</c> object, or
    /// nested under it -- the bundle read could not confirm; both are checked (root first), and a live
    /// capture is what settles which one the CLI actually emits (#1810 review: <c>error</c>'s
    /// placement was previously, and wrongly, what selected the branch).
    /// Same whole-parse-then-split-on-<c>'\n'</c> bug #1727 fixed for
    /// <see cref="ContainsTypedCreditsRequiredError"/> applies here too: the retained tail is
    /// whitespace-collapsed on capture, so a real multi-object tail arrives as one line and a
    /// per-line parse finds nothing. Scans via <see cref="StreamJsonTailScanner"/> instead.
    /// </summary>
    private static bool TryClassifyRateLimitEnvelopeFromText(
        string input,
        TimeProvider timeProvider,
        out FailureClassification? classification,
        out DateTimeOffset? retryNotBefore) =>
        TryClassifyRateLimitEnvelopeFromText(input, timeProvider, out classification, out retryNotBefore, out _);

    /// <summary>
    /// Same as the four-out overload, plus <paramref name="quotaLimitsPlacement"/> -- which of the
    /// three read paths (<c>quotaLimits@root</c>, <c>quotaLimits@message</c>, <c>text-suffix</c>)
    /// actually produced the classification, so a future live capture can be correlated against which
    /// branch fired instead of only against the raw stderr tail already riding along in the park
    /// reason (#1810 review, medium finding).
    /// </summary>
    internal static bool TryClassifyRateLimitEnvelopeFromText(
        string input,
        TimeProvider timeProvider,
        out FailureClassification? classification,
        out DateTimeOffset? retryNotBefore,
        out string? quotaLimitsPlacement)
    {
        FailureClassification? matchedClassification = null;
        DateTimeOffset? matchedRetryNotBefore = null;
        string? matchedPlacement = null;

        var matched = StreamJsonTailScanner.AnyObject(input, root =>
            TryClassifyRateLimitEnvelope(root, timeProvider, out matchedClassification, out matchedRetryNotBefore, out matchedPlacement));

        classification = matched ? matchedClassification : null;
        retryNotBefore = matched ? matchedRetryNotBefore : null;
        quotaLimitsPlacement = matched ? matchedPlacement : null;
        return matched;
    }

    private static bool TryClassifyRateLimitEnvelope(
        JsonElement root,
        TimeProvider timeProvider,
        out FailureClassification? classification,
        out DateTimeOffset? retryNotBefore,
        out string? quotaLimitsPlacement)
    {
        classification = null;
        retryNotBefore = null;
        quotaLimitsPlacement = null;

        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        // #1857: the weekly-limit wall arrives on the terminal `result` event rather than the
        // synthetic `assistant`-line envelope above (spec/baton.md's quota-park section has the
        // full shape). Recognised separately from IsRateLimitContainer since it carries neither
        // an `error` nor a `quotaLimits` field.
        if (IsResultRateLimitEnvelope(root))
        {
            classification = FailureClassification.ExhaustedUntil;
            quotaLimitsPlacement = "result";
            if (root.TryGetProperty("result", out var resultProp) &&
                resultProp.ValueKind == JsonValueKind.String &&
                resultProp.GetString() is { Length: > 0 } resultText)
            {
                retryNotBefore = TryParseResetSuffixFromText(resultText, timeProvider);
            }

            return true;
        }

        // #1810 review fix: `error` always sits at the envelope root in the documented shape (the CLI
        // does not repeat it under "message"), so that -- not quotaLimits's own placement -- is what
        // decides whether this is a rate-limit envelope at all.
        if (!IsRateLimitContainer(root))
        {
            return false;
        }

        JsonElement? message = root.TryGetProperty("message", out var messageProp) &&
            messageProp.ValueKind == JsonValueKind.Object
                ? messageProp
                : null;

        // quotaLimits's OWN placement -- root sibling of "message", or nested under it -- is the actual
        // open question #1609's bundle read could not settle, and is looked up independently of where
        // "error" was found: root is checked first, then "message".
        JsonElement? quotaLimits;
        if (root.TryGetProperty("quotaLimits", out var qRoot) && qRoot.ValueKind == JsonValueKind.Object)
        {
            quotaLimits = qRoot;
            quotaLimitsPlacement = "quotaLimits@root";
        }
        else if (message is { } msg && msg.TryGetProperty("quotaLimits", out var qNested) && qNested.ValueKind == JsonValueKind.Object)
        {
            quotaLimits = qNested;
            quotaLimitsPlacement = "quotaLimits@message";
        }
        else
        {
            quotaLimits = null;
        }

        if (quotaLimits is { } limits &&
            limits.TryGetProperty("errorCode", out var errorCodeProp) &&
            errorCodeProp.ValueKind == JsonValueKind.String &&
            errorCodeProp.GetString() == "credits_required")
        {
            // Build step 2: credits_required riding inside quotaLimits classifies exactly like the
            // top-level typed shape above -- no known TTL (decision 0026 §1a), so no reset instant.
            classification = FailureClassification.ExhaustedUntil;
            retryNotBefore = null;
            return true;
        }

        if (quotaLimits is { } quota)
        {
            retryNotBefore = TryReadEpochSeconds(quota, "resetsAt") ?? TryReadEpochSeconds(quota, "overageResetsAt");
        }

        if (retryNotBefore is null && message is { } messageForText)
        {
            quotaLimitsPlacement = "text-suffix";
            retryNotBefore = TryParseResetSuffix(messageForText, timeProvider);
        }

        classification = FailureClassification.ExhaustedUntil;
        return true;
    }

    private static bool IsRateLimitContainer(JsonElement container) =>
        container.TryGetProperty("error", out var errorProp) &&
        errorProp.ValueKind == JsonValueKind.String &&
        errorProp.GetString() == "rate_limit";

    /// <summary>
    /// The weekly-limit wall's terminal envelope shape (#1857, spec/baton.md's quota-park section).
    /// Checked as an alternative to <see cref="IsRateLimitContainer"/>'s fields, which this envelope
    /// does not carry.
    /// </summary>
    private static bool IsResultRateLimitEnvelope(JsonElement container) =>
        container.TryGetProperty("type", out var typeProp) &&
        typeProp.ValueKind == JsonValueKind.String &&
        typeProp.GetString() == "result" &&
        container.TryGetProperty("is_error", out var isErrorProp) &&
        isErrorProp.ValueKind == JsonValueKind.True &&
        container.TryGetProperty("api_error_status", out var statusProp) &&
        statusProp.ValueKind == JsonValueKind.Number &&
        statusProp.TryGetInt32(out var status) &&
        status == 429;

    private static DateTimeOffset? TryReadEpochSeconds(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var prop))
        {
            return null;
        }

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var seconds))
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }

        if (prop.ValueKind == JsonValueKind.String &&
            long.TryParse(prop.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSeconds))
        {
            return DateTimeOffset.FromUnixTimeSeconds(parsedSeconds);
        }

        return null;
    }

    /// <summary>
    /// Falls back to the envelope's human-readable "&#183; resets 3am" / "resets 11:30pm" suffix
    /// (#1609) when no typed instant parsed -- the same duration-of-last-resort idiom
    /// <see cref="AgyWorkerAdapter"/>'s "Resets in &#8230;" parse uses, except the CLI reports a
    /// clock time rather than a duration: a bare clock time is today in local time, or tomorrow if
    /// that time has already passed today.
    /// </summary>
    private static DateTimeOffset? TryParseResetSuffix(JsonElement message, TimeProvider timeProvider)
    {
        if (!message.TryGetProperty("content", out var contentProp) || contentProp.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var block in contentProp.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object ||
                !block.TryGetProperty("type", out var typeProp) ||
                typeProp.ValueKind != JsonValueKind.String ||
                typeProp.GetString() != "text" ||
                !block.TryGetProperty("text", out var textProp) ||
                textProp.ValueKind != JsonValueKind.String ||
                textProp.GetString() is not { Length: > 0 } text)
            {
                continue;
            }

            if (TryParseResetSuffixFromText(text, timeProvider) is { } resetInstant)
            {
                return resetInstant;
            }
        }

        return null;
    }

    /// <summary>
    /// Text-level reset-suffix parse shared by the <c>message.content[]</c> walk above and the
    /// weekly-limit wall's plain string <c>result</c> field (#1857), which carries the same suffix
    /// with no surrounding content-block envelope. Tries the date-prefixed weekly-wall form first
    /// (<c>resets &lt;Mon&gt; &lt;d&gt;, &lt;h&gt;[:&lt;mm&gt;]&lt;am|pm&gt; (&lt;IANA zone&gt;)</c>),
    /// then falls back to the bare clock-time form the session wall uses.
    /// </summary>
    private static DateTimeOffset? TryParseResetSuffixFromText(string text, TimeProvider timeProvider)
    {
        var dateMatch = ResetDateTimeRegex().Match(text);
        if (dateMatch.Success && TryParseClockParts(dateMatch, out var dateHour24, out var dateMinute))
        {
            var zone = ResolveTimeZone(dateMatch.Groups["zone"].Value, timeProvider);
            var nowInZone = TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), zone);

            if (DateTime.TryParseExact(
                    dateMatch.Groups["month"].Value,
                    ["MMM", "MMMM"],
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var monthParse) &&
                int.TryParse(dateMatch.Groups["day"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var day))
            {
                // Next occurrence of that month/day at or after now in the named zone. A day the
                // target year lacks (Feb 29 rolling into a non-leap year) clamps to that month's last
                // day rather than throwing -- a park a day early beats an unparked lane (#1860 review).
                var year = nowInZone.Year;
                var candidateLocal = BuildLocalInstant(year, monthParse.Month, day, dateHour24, dateMinute);
                if (candidateLocal <= nowInZone.DateTime)
                {
                    candidateLocal = BuildLocalInstant(year + 1, monthParse.Month, day, dateHour24, dateMinute);
                }

                return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(candidateLocal, zone), TimeSpan.Zero);
            }
        }

        var clockMatch = ResetClockTimeRegex().Match(text);
        if (!clockMatch.Success || !TryParseClockParts(clockMatch, out var hour24, out var minute))
        {
            return null;
        }

        var localZone = timeProvider.LocalTimeZone;
        var nowLocal = TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), localZone);
        var candidate = new DateTime(nowLocal.Year, nowLocal.Month, nowLocal.Day, hour24, minute, 0, DateTimeKind.Unspecified);
        if (candidate <= nowLocal.DateTime)
        {
            candidate = candidate.AddDays(1);
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(candidate, localZone), TimeSpan.Zero);
    }

    private static DateTime BuildLocalInstant(int year, int month, int day, int hour24, int minute)
    {
        var clampedDay = Math.Min(Math.Max(day, 1), DateTime.DaysInMonth(year, month));
        return new DateTime(year, month, clampedDay, hour24, minute, 0, DateTimeKind.Unspecified);
    }

    private static bool TryParseClockParts(Match match, out int hour24, out int minute)
    {
        hour24 = 0;
        minute = 0;

        if (!int.TryParse(match.Groups["hour"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var hour) ||
            hour is < 1 or > 12)
        {
            return false;
        }

        if (match.Groups["minute"].Success &&
            (!int.TryParse(match.Groups["minute"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out minute) ||
             minute is < 0 or > 59))
        {
            return false;
        }

        var isPm = string.Equals(match.Groups["meridiem"].Value, "pm", StringComparison.OrdinalIgnoreCase);
        hour24 = (hour % 12) + (isPm ? 12 : 0);
        return true;
    }

    /// <summary>
    /// Resolves the reset suffix's IANA zone id (e.g. <c>America/New_York</c>). .NET on Windows maps
    /// IANA ids since .NET 6 when ICU is present, so this normally succeeds; if the id is unrecognised
    /// this falls back to <see cref="TimeProvider.LocalTimeZone"/> rather than null, since the text
    /// plainly named a date -- and writes one warning line to stderr (this adapter's diagnostic
    /// idiom, same as the commands-directory warning above) so the fallback lands in the room's
    /// captured stderr instead of an unconfigured trace listener.
    /// </summary>
    private static TimeZoneInfo ResolveTimeZone(string zoneId, TimeProvider timeProvider)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            Console.Error.WriteLine(
                $"Warning: unrecognised reset-suffix zone id '{zoneId}', falling back to the local time zone ({ex.GetType().Name}).");
            return timeProvider.LocalTimeZone;
        }
    }

    [GeneratedRegex(@"resets\s+(?<hour>\d{1,2})(?::(?<minute>\d{2}))?\s*(?<meridiem>am|pm)", RegexOptions.IgnoreCase)]
    private static partial Regex ResetClockTimeRegex();

    [GeneratedRegex(
        @"resets\s+(?<month>[A-Za-z]{3,9})\s+(?<day>\d{1,2}),\s*(?<hour>\d{1,2})(?::(?<minute>\d{2}))?\s*(?<meridiem>am|pm)\s*\((?<zone>[^)]+)\)",
        RegexOptions.IgnoreCase)]
    private static partial Regex ResetDateTimeRegex();

    /// <summary>
    /// #1720 review (found while fixing F1, issue #1727): this used to whole-parse the tail and then
    /// split it on <c>'\n'</c>, which finds nothing in a REAL captured tail — see
    /// <see cref="StreamJsonTailScanner"/> for the whitespace-collapse that makes a multi-object tail
    /// one line. The shared scanner reads both the collapsed and the raw-newline shape.
    /// </summary>
    private static bool ContainsTypedCreditsRequiredError(string input) =>
        StreamJsonTailScanner.AnyObject(input, HasTypedCreditsRequiredCode);

    private static bool HasTypedCreditsRequiredCode(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (element.TryGetProperty("errorCode", out var errorCodeProp) &&
            errorCodeProp.ValueKind == JsonValueKind.String &&
            errorCodeProp.GetString() == "credits_required")
        {
            return true;
        }

        if (element.TryGetProperty("error_code", out var errorCodeProp2) &&
            errorCodeProp2.ValueKind == JsonValueKind.String &&
            errorCodeProp2.GetString() == "credits_required")
        {
            return true;
        }

        if (element.TryGetProperty("error", out var errorProp) && errorProp.ValueKind == JsonValueKind.Object)
        {
            if (errorProp.TryGetProperty("code", out var codeProp) &&
                codeProp.ValueKind == JsonValueKind.String &&
                codeProp.GetString() == "credits_required")
            {
                return true;
            }

            if (errorProp.TryGetProperty("errorCode", out var codeProp2) &&
                codeProp2.ValueKind == JsonValueKind.String &&
                codeProp2.GetString() == "credits_required")
            {
                return true;
            }
        }

        return false;
    }
}
