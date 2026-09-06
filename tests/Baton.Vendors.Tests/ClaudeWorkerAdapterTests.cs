using System.Diagnostics;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Outcomes;
using Baton.Status;
using Baton.Tests.Shared;

namespace Baton.Vendors.Tests;


/// <summary>
/// M20 Phase 4's deliverable: unit tests for the refactored, direct shell-less
/// <see cref="ClaudeWorkerAdapter"/> resolving.
/// </summary>
/// <remarks>
/// #1524: the two config-root tests isolate <see cref="BatonEnvironmentSnapshot.ClaudeConfigRootOverride"/>
/// through <see cref="BatonEnvironmentSnapshot.BeginScope"/> rather than mutating process environment,
/// so this class no longer needs <c>SerializedEnvironmentCollection</c> enrollment for that. It still
/// needs <see cref="LaunchConfigCollection"/>, unrelated to this fold: this class writes launch config
/// files (<c>claude-settings.json</c>/<c>claude-mcp.json</c>) under the assembly's shared
/// <c>BATON_HOME</c>, and <see cref="LaunchConfigCollection"/>'s own remarks record the
/// <see cref="UnauthorizedAccessException"/> race #667/#682 measured when a launch-config writer runs
/// in the default parallel pool instead.
/// </remarks>
[Collection(LaunchConfigCollection.Name)]
public class ClaudeWorkerAdapterTests
{
    private static readonly WorkerContract ArchitectContract = new(
        "architect", ["goal"], [new ProducedOutput("plan.md")], []);

    private static string GetPrompt(CoreDispatchTarget target) => target.Args[1];

    /// <summary>The value token immediately after <paramref name="flag"/> in the flat argv, or null.</summary>
    private static string? ArgValue(CoreDispatchTarget target, string flag)
    {
        for (var i = 0; i < target.Args.Count - 1; i++)
        {
            if (target.Args[i] == flag)
            {
                return target.Args[i + 1];
            }
        }

        return null;
    }

    [Fact]
    public void Resolves_to_direct_claude_execution_without_shell_wrapper()
    {
        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        Assert.Equal("claude", target.Program);
        Assert.Equal("-p", target.Args[0]);
        Assert.Equal("--allowedTools", target.Args[2]);
        Assert.Equal("Write", target.Args[3]);
        Assert.Equal("--add-dir", target.Args[4]);
        // #533 inserted --settings/--mcp-config after --add-dir's value; positional indices past
        // that point are no longer stable, so this uses the order-independent helper like every
        // newer test in this file already does.
        Assert.Equal("text", ArgValue(target, "--output-format"));
    }

    [Fact]
    public void Resolve_sets_OversizePromptWrapper_referencing_BATON_PROMPT_FILE()
    {
        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);
        Assert.NotNull(target.OversizePromptWrapper);
        Assert.Contains("%BATON_PROMPT_FILE%", target.OversizePromptWrapper);
    }

    /// <summary>
    /// #289: Claude Code's own directory-trust sandbox (separate from --allowedTools) was found,
    /// via a live run against the real authenticated CLI, to non-deterministically refuse to write
    /// BATON_OUTPUT_DIR when it falls outside the spawned process's cwd -- which it always does for a
    /// plain chat session with no WorkingDirectory. --add-dir BATON_ARTIFACTS_ROOT (the same grant
    /// AgyWorkerAdapter already carries for agy, per ArtifactManager.BuildEnvironment's own doc
    /// comment) eliminated the failure across every trial once added.
    /// </summary>
    [Fact]
    public void The_artifacts_root_is_granted_via_add_dir_so_output_writes_outside_cwd_are_trusted()
    {
        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        Assert.Equal("--add-dir", target.Args[4]);
        const string artifactsRootVar = "%BATON_ARTIFACTS_ROOT%";
        Assert.Equal(artifactsRootVar, target.Args[5]);
    }

    /// <summary>M23 Phase 3 (#272): WorkingDirectory carries no vendor-specific meaning — every adapter forwards it into CoreDispatchTarget unchanged.</summary>
    [Fact]
    public void A_configured_WorkingDirectory_is_forwarded_into_the_resolved_target()
    {
        // #1166: a WorkingDirectory now has to carry a recorded ceiling or Resolve refuses -- this
        // test is about forwarding, not the ceiling gate, so it trusts the fixture path unrestricted.
        ProjectCeilingStore.Set("/home/user/my-project", ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);

        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", WorkingDirectory: "/home/user/my-project"), ArchitectContract);

        Assert.Equal("/home/user/my-project", target.WorkingDirectory);
    }

    [Fact]
    public void A_null_WorkingDirectory_leaves_the_resolved_target_with_no_explicit_cwd()
    {
        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        Assert.Null(target.WorkingDirectory);
    }

    [Fact]
    public void An_explicit_permission_scope_overrides_the_default()
    {
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionScope: "Write,Bash(git:*)"), ArchitectContract);

        Assert.Equal("Write,Bash(git:*)", target.Args[3]);
    }

    [Fact]
    public void A_model_is_passed_through_when_set()
    {
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", Model: "claude-opus-4-5"), ArchitectContract);

        Assert.Equal("claude-opus-4-5", ArgValue(target, "--model"));
    }

    [Fact]
    public void No_model_flag_is_emitted_when_the_model_is_unset()
    {
        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        Assert.DoesNotContain("--model", target.Args);
    }

    [Fact]
    public void An_effort_is_passed_through_when_set()
    {
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", Effort: "high"), ArchitectContract);

        Assert.Equal("high", ArgValue(target, "--effort"));
    }

    [Fact]
    public void No_effort_flag_is_emitted_when_the_effort_is_unset()
    {
        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        Assert.DoesNotContain("--effort", target.Args);
    }

    [Fact]
    public void The_prompt_names_every_declared_output_and_its_env_var_path()
    {
        var contract = new WorkerContract(
            "architect", [], [new ProducedOutput("plan.md"), new ProducedOutput("summary.md")], []);

        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), contract);

        var prompt = GetPrompt(target);
        const string outputVar = "%BATON_OUTPUT_DIR%";
        const char separator = '\\';
        Assert.Contains($"plan.md: {outputVar}{separator}plan.md", prompt);
        Assert.Contains($"summary.md: {outputVar}{separator}summary.md", prompt);
    }

    [Fact]
    public void The_prompt_names_every_required_input_and_its_env_var_path()
    {
        var contract = new WorkerContract(
            "critic", ["plan", "guidelines"], [new ProducedOutput("review.md")], []);

        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Review the plan."), contract);

        var prompt = GetPrompt(target);
        const string inputVar0 = "%BATON_INPUT_0%";
        const string inputVar1 = "%BATON_INPUT_1%";
        Assert.Contains($"plan: {inputVar0}", prompt);
        Assert.Contains($"guidelines: {inputVar1}", prompt);
    }

    [Fact]
    public void A_contract_with_no_inputs_omits_the_inputs_section()
    {
        var contract = new WorkerContract("architect", [], [new ProducedOutput("plan.md")], []);

        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), contract);

        Assert.DoesNotContain("Inputs, in the order listed", GetPrompt(target));
    }

    [Fact]
    public void Prompt_keeps_newlines_for_readability_on_all_platforms()
    {
        var contract = new WorkerContract("architect", ["goal"], [new ProducedOutput("plan.md")], []);
        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), contract);

        Assert.Contains('\n', GetPrompt(target));
    }

    [Fact]
    public void Shell_metacharacters_and_percent_signs_are_passed_raw_because_no_shell_evaluates_them()
    {
        var invocation = new WorkerInvocation("Quote this: \"$HOME\" and `whoami` and 100% path %PATH%.");

        var target = new ClaudeWorkerAdapter().Resolve(invocation, ArchitectContract);

        var prompt = GetPrompt(target);
        Assert.Contains("Quote this: \"$HOME\" and `whoami` and 100% path %PATH%.", prompt);
    }

    /// <summary>Issue #292: CoreDispatcher's durable prompt.txt capture reads this field, not target.Args -- it must carry the identical text the -p argument does.</summary>
    [Fact]
    public void PromptText_carries_the_same_resolved_prompt_as_the_p_argument()
    {
        var contract = new WorkerContract("architect", ["goal"], [new ProducedOutput("plan.md")], []);
        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), contract);

        Assert.Equal(GetPrompt(target), target.PromptText);
    }

    [Fact]
    public void Null_invocation_or_contract_throws()
    {
        var adapter = new ClaudeWorkerAdapter();

        Assert.Throws<ArgumentNullException>(() => adapter.Resolve(null!, ArchitectContract));
        Assert.Throws<ArgumentNullException>(() => adapter.Resolve(new WorkerInvocation("Draft a plan."), null!));
    }

    // M21 Phase 1: the structured PermissionGrant builder path. The tests above are untouched —
    // proving a hand-typed raw PermissionScope still resolves identically is exactly "don't touch
    // the existing cases."

    [Fact]
    public void A_permission_grant_composes_every_category_into_allowedTools_in_a_fixed_order()
    {
        var grant = new PermissionGrant(ReadFiles: true, WriteFiles: true, RunShellCommands: true, NetworkAccess: true);
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract);

        Assert.Equal("Read,Edit,Write,NotebookEdit,Bash,WebFetch,WebSearch", target.Args[3]);
    }

    [Fact]
    public void A_permission_grant_scopes_shell_commands_to_its_patterns_when_given()
    {
        var grant = new PermissionGrant(RunShellCommands: true, ShellCommandPatterns: ["git:*", "npm:*"]);
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract);

        // The write tools precede the shell entries because #649 pre-approves them unconditionally —
        // pre-approval is not a ceiling, and the hook is what confines them to BATON_OUTPUT_DIR. The
        // pattern scoping this test is about is unaffected by that.
        Assert.Equal("Edit,Write,NotebookEdit,Bash(git:*),Bash(npm:*)", target.Args[3]);
    }

    [Fact]
    public void A_permission_grant_takes_precedence_over_a_raw_permission_scope_when_both_are_set()
    {
        var grant = new PermissionGrant(ReadFiles: true);
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionScope: "Write,Bash(git:*)", PermissionGrant: grant),
            ArchitectContract);

        // What this test is about is that the raw scope's Bash(git:*) is gone — the grant won. The
        // write tools present are the grant's own #649 pre-approval, not the raw scope leaking in.
        Assert.Equal("Read,Edit,Write,NotebookEdit", target.Args[3]);
        Assert.DoesNotContain("Bash", target.Args[3], StringComparison.Ordinal);
    }

    [Fact]
    public void TryTranslatePermissionGrant_never_refuses_for_claude()
    {
        var adapter = new ClaudeWorkerAdapter();

        var succeeded = adapter.TryTranslatePermissionGrant(
            new PermissionGrant(RunShellCommands: true, NetworkAccess: true), out var resolved, out var gapReason);

        Assert.True(succeeded);
        // Write tools ride the allow list unconditionally since #649; what this test is about is
        // that translation never returns false for claude, and that the shell/network arms resolve.
        Assert.Equal("Edit,Write,NotebookEdit,Bash,WebFetch,WebSearch", resolved);
        Assert.Null(gapReason);
    }

    // #331: --allowedTools only *pre-approves*; a withheld category must be *actively* denied via
    // --disallowedTools or a subscription worker still reaches the tool (a shell-denied session ran
    // `hostname`). These assert the enforcing flag is emitted onto the argv — the default-CI guard for
    // this class of bug, which shape-only translation tests could not catch. That the CLI *honours*
    // the flag is a live-vendor smoke gate (docs/runbooks/live-claude-smoke.md), not a unit test.

    [Fact]
    public void A_withheld_shell_grant_actively_denies_Bash_not_merely_omits_it_from_the_allow_list()
    {
        var grant = new PermissionGrant(ReadFiles: true, WriteFiles: true, RunShellCommands: false);
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract);

        Assert.DoesNotContain("Bash", ArgValue(target, "--allowedTools")!); // omitted from the allow-list...
        Assert.Contains("Bash", ArgValue(target, "--disallowedTools")!);    // ...and actively denied.
    }

    [Fact]
    public void The_disallowed_list_is_the_exact_complement_of_the_withheld_categories()
    {
        // Read granted; write, shell and network all withheld. Every withheld category maps to its
        // denied tool(s) EXCEPT writes, which #649 moved to the hook: named here, the CLI would refuse
        // the write before the hook could allow the one landing in BATON_OUTPUT_DIR. The hook's own list
        // still carries them — see Withheld_writes_leave_the_flag_and_move_to_the_hooks_list.
        var grant = new PermissionGrant(ReadFiles: true);
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant, AllowsSubagents: true), ArchitectContract);

        // Writes are pre-approved so the hook can be consulted at all, and absent from the deny flag
        // so the CLI does not refuse them first. Both halves are #649; neither is enforcement.
        Assert.Equal("Read,Edit,Write,NotebookEdit", ArgValue(target, "--allowedTools"));
        Assert.Equal("Bash,WebFetch,WebSearch", ArgValue(target, "--disallowedTools"));
    }

    [Fact]
    public void A_fully_permissive_grant_withholds_nothing_and_emits_no_disallowed_list()
    {
        var grant = new PermissionGrant(ReadFiles: true, WriteFiles: true, RunShellCommands: true, NetworkAccess: true);
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant, AllowsSubagents: true), ArchitectContract);

        Assert.DoesNotContain("--disallowedTools", target.Args);
    }

    [Fact]
    public void A_read_only_scoped_shell_grant_allows_only_its_patterns_and_denies_the_named_mutating_ones()
    {
        // #1456: the review role's actual grant shape -- read-only git/gh patterns allowed, mutating
        // families explicitly denied on top, no bare "Bash" anywhere on either flag. This is what
        // makes the ceiling real per docs/vendor-capabilities.md's measured negative control (a Bash
        // pattern not on the allow list is refused, not merely unprompted).
        var grant = new PermissionGrant(
            ReadFiles: true, WriteFiles: false, RunShellCommands: true,
            ShellCommandPatterns: ["git diff*", "gh pr view*"], NetworkAccess: false,
            DeniedShellCommandPatterns: ["git commit*", "git push*"], ShellCommandsAreReadOnly: true);
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract);

        var allowed = ArgValue(target, "--allowedTools")!;
        Assert.Contains("Bash(git diff*)", allowed);
        Assert.Contains("Bash(gh pr view*)", allowed);
        Assert.DoesNotContain("Bash,", allowed, StringComparison.Ordinal);
        Assert.DoesNotContain("Bash(git commit*)", allowed);

        var denied = ArgValue(target, "--disallowedTools")!;
        Assert.Contains("Bash(git commit*)", denied);
        Assert.Contains("Bash(git push*)", denied);
        Assert.DoesNotContain("Bash(git diff*)", denied);
        // Bare "Bash" (the category-level denial #331 emits when the shell is fully withheld) must
        // not appear -- this grant GRANTS the shell, just scoped, so the bare-tool denial branch
        // (WithheldToolNames) must not fire.
        Assert.DoesNotMatch(@"(^|,)Bash(,|$)", denied);
    }

    [Fact]
    public void Denied_option_tokens_ride_the_hook_channel_and_deliberately_reach_no_vendor_flag()
    {
        // #1683 F2, both halves of the decision this PR is required to state, pinned rather than left
        // in prose. The rung is real -- the env channel carries it to the hook -- and it is hook-only:
        // --disallowedTools matches the whole command line anchored, so a token deny is not expressible
        // there as an enforceable entry, and emitting `Bash(--output)` would be a positional pattern
        // wearing a token's name. If someone later wires it onto the flag, this fails and they have to
        // justify it against a measurement.
        var grant = new PermissionGrant(
            ReadFiles: true, WriteFiles: false, RunShellCommands: true,
            ShellCommandPatterns: ["git log*"], NetworkAccess: false,
            DeniedShellCommandPatterns: ["git push*"], ShellCommandsAreReadOnly: true,
            DeniedShellOptionTokens: ["--output"]);
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract);

        Assert.Contains(
            target.Environment!,
            env => env.Name == ClaudeWorkerAdapter.DeniedShellOptionTokensVariable
                && env.Value == "claude:--output");

        var denied = ArgValue(target, "--disallowedTools")!;
        Assert.Contains("Bash(git push*)", denied);
        Assert.DoesNotContain("--output", denied, StringComparison.Ordinal);
    }

    [Fact]
    public void A_raw_permission_scope_with_no_structured_grant_emits_no_disallowed_list()
    {
        // The Advanced escape hatch carries no categories to deny — a hand-typed scope is taken as-is.
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionScope: "Read,Edit", AllowsSubagents: true), ArchitectContract);

        Assert.DoesNotContain("--disallowedTools", target.Args);
    }

    // #1802: AllowsSubagents sits outside the four PermissionGrant categories BuildDisallowedTools
    // maps -- a write-and-shell-granted grant like implement's own never reaches Agent/Task through
    // the grant alone, so this needs its own coverage independent of the category tests above.

    [Fact]
    public void A_fully_permissive_grant_with_subagents_withheld_still_denies_Agent_and_Task()
    {
        var grant = new PermissionGrant(ReadFiles: true, WriteFiles: true, RunShellCommands: true, NetworkAccess: true);
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant, AllowsSubagents: false), ArchitectContract);

        Assert.Equal("Agent,Task", ArgValue(target, "--disallowedTools"));
    }

    [Fact]
    public void Subagent_withholding_composes_with_an_already_nonempty_disallowed_list()
    {
        var grant = new PermissionGrant(ReadFiles: true, WriteFiles: true, RunShellCommands: false, NetworkAccess: false);
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant, AllowsSubagents: false), ArchitectContract);

        var denied = ArgValue(target, "--disallowedTools")!;
        Assert.Contains("Bash", denied);
        Assert.Contains("WebFetch", denied);
        Assert.Contains("Agent,Task", denied);
    }

    [Fact]
    public void Subagents_allowed_emits_no_Agent_or_Task_denial()
    {
        var grant = new PermissionGrant(ReadFiles: true, WriteFiles: true, RunShellCommands: true, NetworkAccess: true);
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant, AllowsSubagents: true), ArchitectContract);

        Assert.DoesNotContain("--disallowedTools", target.Args);
    }

    [Fact]
    public void A_WorkerInvocation_built_with_defaults_denies_Agent_and_Task()
    {
        // #1811 review: AllowsSubagents must default closed on WorkerInvocation itself, not merely
        // on WorkerBindingConfigEntry -- a caller constructing one directly (bypassing the resolver)
        // must not be able to spawn a subagent without naming the opt-in explicitly.
        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        Assert.Equal("Agent,Task", ArgValue(target, "--disallowedTools"));
    }

    /// <summary>
    /// #533 constraints 1-2: hooks and MCP config load only from cwd's own `.claude/`, with no
    /// parent-directory fallback, and `--add-dir` loads neither on claude -- so both are passed
    /// explicitly, at files AER owns rather than the room's own directory.
    /// </summary>
    [Fact]
    public void Settings_and_mcp_config_are_passed_at_BATON_owned_paths_that_exist_and_are_valid_json()
    {
        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        var settingsPath = ArgValue(target, "--settings");
        var mcpConfigPath = ArgValue(target, "--mcp-config");

        Assert.NotNull(settingsPath);
        Assert.NotNull(mcpConfigPath);
        Assert.StartsWith(BatonPaths.WorkerLaunchConfig, settingsPath);
        Assert.StartsWith(BatonPaths.WorkerLaunchConfig, mcpConfigPath);
        Assert.True(File.Exists(settingsPath), "the file --settings points at must already exist");
        Assert.True(File.Exists(mcpConfigPath), "the file --mcp-config points at must already exist");

        // Both must be valid, parseable JSON, or the CLI invocation this constructs fails outright.
        using var settingsDoc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(settingsPath));
        using var mcpDoc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(mcpConfigPath));
        Assert.Equal(System.Text.Json.JsonValueKind.Object, settingsDoc.RootElement.ValueKind);
        Assert.True(mcpDoc.RootElement.TryGetProperty("mcpServers", out _));
    }

    /// <summary>
    /// #543 reverses #533's "never overwrite" for this one file: the settings file is entirely
    /// AER-owned (nothing an operator could have put there survives), and it now carries the
    /// mandatory `PreToolUse` hook, so leaving stale content in place would permanently disable the
    /// gate on any machine that ran a pre-#543 build even once.
    /// </summary>
    /// <remarks>
    /// <b>Has to be asserted through <c>Resolve</c>, not only on the writer.</b> With this test moved
    /// down to <c>AtomicLaunchConfigWriterTests</c>, swapping <c>EnsureLaunchConfigFiles</c> back to
    /// <c>EnsureFileExists</c> -- the pre-#543 regression itself -- left the suite green. The
    /// writer-level test proves the writer corrects drift; this one proves the adapter routes through
    /// it. Different claims, not a restatement.
    /// </remarks>
    [Fact]
    public void A_settings_file_with_stale_content_is_overwritten_with_the_canonical_hook_on_the_next_resolve()
    {
        new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);
        var settingsPath = Path.Combine(BatonPaths.WorkerLaunchConfig, "claude-settings.json");
        Assert.True(File.Exists(settingsPath));

        const string stale = """{"hooks":{"PreToolUse":[{"stale":"pre-543-content"}]}}""";
        File.WriteAllText(settingsPath, stale);

        new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft another plan."), ArchitectContract);

        var rewritten = File.ReadAllText(settingsPath);
        Assert.NotEqual(stale, rewritten);
        Assert.DoesNotContain("stale", rewritten);
    }

    /// <summary>
    /// The actual hook payload #543 ships: one `PreToolUse` matcher group covering every tool,
    /// invoked as `dotnet &lt;Baton.Cli.dll path&gt; hook-check` in exec form (`args` present, so
    /// Claude Code spawns it with no shell) -- see `BuildSettingsJson`'s doc comment for why this
    /// names the managed dll via `dotnet` rather than a native apphost (the packed global tool has
    /// no apphost at all).
    /// </summary>
    [Fact]
    public void The_settings_file_carries_a_PreToolUse_hook_that_matches_every_tool_and_points_at_hook_check()
    {
        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);
        var settingsPath = ArgValue(target, "--settings")!;

        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(settingsPath));
        var preToolUse = doc.RootElement.GetProperty("hooks").GetProperty("PreToolUse");
        Assert.Equal(1, preToolUse.GetArrayLength());

        var matcherGroup = preToolUse[0];
        Assert.Equal("*", matcherGroup.GetProperty("matcher").GetString());

        var handler = matcherGroup.GetProperty("hooks")[0];
        Assert.Equal("command", handler.GetProperty("type").GetString());
        Assert.Equal("dotnet", handler.GetProperty("command").GetString());

        var args = handler.GetProperty("args").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(2, args.Count);
        Assert.EndsWith("Baton.Cli.dll", args[0]);
        Assert.True(File.Exists(args[0]), "the hook's first arg must point at a real, existing Baton.Cli.dll");
        Assert.Equal("hook-check", args[1]);

        // `dotnet <dll>` needs the dll's own .runtimeconfig.json alongside it to run at all -- a
        // review pass on #543 pointed out that checking only the .dll's existence proves nothing
        // about whether `dotnet` can actually load it.
        var runtimeConfigPath = Path.ChangeExtension(args[0], null) + ".runtimeconfig.json";
        Assert.True(
            File.Exists(runtimeConfigPath),
            $"dotnet needs '{runtimeConfigPath}' alongside Baton.Cli.dll to run it at all");
    }

    /// <summary>
    /// #543: the settings file is one static, shared file across every spawn, so per-invocation
    /// data (what this specific worker was denied) has to reach hook-check another way -- the
    /// process environment, which a hook subprocess inherits from claude, which inherits it from
    /// AER's own spawn (confirmed in `.vendor-survey/corpus/claude__hooks.md`: "A hook process
    /// inherits the parent environment"). This is the same string `--disallowedTools` receives, not
    /// a separately-derived value, so the two mechanisms can never disagree about what was withheld.
    /// </summary>
    [Fact]
    public void The_denied_tools_environment_variable_is_the_flag_plus_the_write_tools()
    {
        var grant = new PermissionGrant(ReadFiles: true);
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant, AllowsSubagents: true), ArchitectContract);

        // #649: the two channels deliberately differ, on writes and only on writes. The flag is what
        // the CLI enforces directly; the hook list is what it enforces with the target path in hand.
        Assert.NotNull(target.Environment);
        var hookList = target.Environment!.Single(v => v.Name == ClaudeWorkerAdapter.DeniedToolsVariable).Value;

        // #600's vendor tag and #649's differing contents, on the same value.
        Assert.Equal("Bash,WebFetch,WebSearch", ArgValue(target, "--disallowedTools"));
        Assert.Equal("claude:Edit,Write,NotebookEdit,Bash,WebFetch,WebSearch", hookList);
    }

    [Fact]
    public void The_denied_tools_environment_variable_is_set_even_when_nothing_is_withheld()
    {
        // hook-check must see an explicit "" rather than a missing variable it could confuse with
        // "not spawned by AER at all" -- Contains below also proves the variable is present at all.
        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        Assert.NotNull(target.Environment);
        // #600: tagged, so "AER set this and nothing is withheld" is distinguishable from "the variable
        // never arrived". The empty list after the tag is the part that still means "nothing withheld".
        Assert.Contains((ClaudeWorkerAdapter.DeniedToolsVariable, "claude:"), target.Environment);
    }

    /// <summary>
    /// #1459: closes the gap <c>ShellPatternsVariable</c>'s own doc comment names. Both channels now
    /// reach the hook subprocess, tagged and comma-joined the same way the denied-tools channel is.
    /// </summary>
    [Fact]
    public void The_shell_pattern_channels_carry_the_grants_allowed_and_denied_patterns()
    {
        var grant = new PermissionGrant(
            RunShellCommands: true, ShellCommandPatterns: ["git diff*", "gh pr view*"],
            DeniedShellCommandPatterns: ["git commit*", "git push*"]);
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract);

        Assert.NotNull(target.Environment);
        Assert.Contains(
            (ClaudeWorkerAdapter.ShellPatternsVariable, "claude:git diff*,gh pr view*"), target.Environment);
        Assert.Contains(
            (ClaudeWorkerAdapter.DeniedShellPatternsVariable, "claude:git commit*,git push*"),
            target.Environment);
    }

    /// <summary>
    /// #1459: an unscoped shell (no pattern list, or no grant at all) must still set both channels,
    /// tagged and empty -- that is the "unscoped, not broken" reading <c>HookCheckCommand.Decide</c>
    /// depends on. A missing variable and an empty-but-tagged one must stay tellable apart the same
    /// way #600 already made the denied-tools channel tellable apart.
    /// </summary>
    [Fact]
    public void The_shell_pattern_channels_are_set_even_when_unscoped_or_absent()
    {
        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        Assert.NotNull(target.Environment);
        Assert.Contains((ClaudeWorkerAdapter.ShellPatternsVariable, "claude:"), target.Environment);
        Assert.Contains((ClaudeWorkerAdapter.DeniedShellPatternsVariable, "claude:"), target.Environment);
    }

    /// <summary>
    /// Regression, #1459 fix (PR #1506's adversarial security review): a raw <c>PermissionScope</c>
    /// carrying a <c>Bash(pattern)</c> clause used to reach <c>--allowedTools</c> while leaving
    /// <c>BATON_HOOK_SHELL_PATTERNS</c> tagged-and-empty, because the channel was built exclusively
    /// from the (here, null) structured <c>PermissionGrant</c>. The channel must now carry the same
    /// pattern the flag does.
    /// </summary>
    [Fact]
    public void A_raw_PermissionScope_Bash_pattern_clause_populates_the_shell_pattern_channel()
    {
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionScope: "Write,Bash(git diff*)"), ArchitectContract);

        Assert.Equal("Write,Bash(git diff*)", ArgValue(target, "--allowedTools"));
        Assert.NotNull(target.Environment);
        Assert.Contains(
            (ClaudeWorkerAdapter.ShellPatternsVariable, "claude:git diff*"), target.Environment);
        // The raw path has no denied-pattern concept to derive -- unchanged by this fix.
        Assert.Contains(
            (ClaudeWorkerAdapter.DeniedShellPatternsVariable, "claude:"), target.Environment);
    }

    /// <summary>
    /// Multiple <c>Bash(pattern)</c> clauses in the raw scope all reach the channel, comma-joined the
    /// same way the structured-grant path already joins <c>PermissionGrant.ShellCommandPatterns</c>.
    /// </summary>
    [Fact]
    public void Multiple_raw_PermissionScope_Bash_pattern_clauses_all_populate_the_shell_pattern_channel()
    {
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation(
                "Draft a plan.", PermissionScope: "Bash(git diff*),Read,Bash(gh pr view*)"),
            ArchitectContract);

        Assert.NotNull(target.Environment);
        Assert.Contains(
            (ClaudeWorkerAdapter.ShellPatternsVariable, "claude:git diff*,gh pr view*"),
            target.Environment);
    }

    /// <summary>
    /// The genuinely-unscoped-shell case (see <c>BuildShellPatternsFromRawScope</c>'s own doc
    /// comment for why a bare clause must stay excluded). The channel must stay empty, not deny.
    /// </summary>
    [Fact]
    public void A_bare_Bash_raw_scope_still_yields_an_empty_shell_pattern_channel()
    {
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionScope: "Write,Bash"), ArchitectContract);

        Assert.Equal("Write,Bash", ArgValue(target, "--allowedTools"));
        Assert.NotNull(target.Environment);
        Assert.Contains((ClaudeWorkerAdapter.ShellPatternsVariable, "claude:"), target.Environment);
    }

    /// <summary>
    /// The other half of the no-op case: a raw scope that names no <c>Bash(</c> clause at all (not
    /// even a bare one). Must read identically to the bare-<c>Bash</c> case above -- an empty channel,
    /// never the throw the unparseable-clause arm below asserts.
    /// </summary>
    [Fact]
    public void A_raw_scope_with_no_Bash_clause_at_all_still_yields_an_empty_shell_pattern_channel()
    {
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionScope: "Write,Read"), ArchitectContract);

        Assert.NotNull(target.Environment);
        Assert.Contains((ClaudeWorkerAdapter.ShellPatternsVariable, "claude:"), target.Environment);
    }

    /// <summary>
    /// Fix 3 (round-4 re-review of PR #1506): #1514 is why this now throws where fix 2 granted both
    /// patterns -- <c>ClaudeWorkerAdapter.BuildShellPatternsFromRawScope</c>'s own remarks carry the
    /// reasoning.
    /// </summary>
    [Fact]
    public void A_comma_list_inside_one_Bash_clause_makes_Resolve_throw_instead_of_granting_both_patterns()
    {
        var exception = Assert.Throws<PermissionGrantUnsupportedException>(() =>
            new ClaudeWorkerAdapter().Resolve(
                new WorkerInvocation(
                    "Draft a plan.", PermissionScope: "Write,Bash(git diff*, git status*)"),
                ArchitectContract));

        Assert.Equal("claude", exception.AdapterName);
    }

    /// <summary>
    /// Round-4 HIGH (PR #1506); <c>ClaudeWorkerAdapter.BuildShellPatternsFromRawScope</c>'s own remarks
    /// record the swallowed-grant mechanism this closes. Must throw here, not reach an empty no-op
    /// channel.
    /// </summary>
    [Fact]
    public void An_unbalanced_non_Bash_clause_that_would_swallow_a_real_Bash_grant_makes_Resolve_throw()
    {
        var exception = Assert.Throws<PermissionGrantUnsupportedException>(() =>
            new ClaudeWorkerAdapter().Resolve(
                new WorkerInvocation("Draft a plan.", PermissionScope: "Read(,Bash(git diff*)"),
                ArchitectContract));

        Assert.Equal("claude", exception.AdapterName);
    }

    /// <summary>
    /// The balance gate's negative arm: it only fires when a <c>Bash(</c> grant is present at all, a
    /// scope restriction <c>BuildShellPatternsFromRawScope</c>'s own remarks explain the reasoning for.
    /// </summary>
    [Fact]
    public void A_stray_unbalanced_paren_with_no_Bash_clause_still_yields_an_empty_shell_pattern_channel()
    {
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionScope: "Read),Write"), ArchitectContract);

        Assert.NotNull(target.Environment);
        Assert.Contains((ClaudeWorkerAdapter.ShellPatternsVariable, "claude:"), target.Environment);
    }

    /// <summary>
    /// LOW finding fixed alongside the comma-list bug: interior whitespace around a single pattern
    /// (<c>Bash( git diff* )</c>) used to reach the channel un-trimmed (<c>" git diff* "</c>), which
    /// never matches any real command line -- a permanently-dead grant that looked populated. The
    /// paren-aware split trims each extracted pattern the same way the structured-grant path already
    /// does.
    /// </summary>
    [Fact]
    public void Interior_whitespace_inside_a_single_Bash_clause_is_trimmed_from_the_channel()
    {
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionScope: "Write,Bash( git diff* )"),
            ArchitectContract);

        Assert.NotNull(target.Environment);
        Assert.Contains(
            (ClaudeWorkerAdapter.ShellPatternsVariable, "claude:git diff*"), target.Environment);
    }

    /// <summary>
    /// Nested, balanced parens inside a single pattern must survive whole rather than being cut at the
    /// first inner <c>)</c> -- the depth-tracking split, not a naive <c>IndexOf(')')</c>.
    /// </summary>
    [Fact]
    public void A_Bash_clause_with_nested_balanced_parens_yields_the_whole_pattern()
    {
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionScope: "Bash(foo(bar))"), ArchitectContract);

        Assert.NotNull(target.Environment);
        Assert.Contains(
            (ClaudeWorkerAdapter.ShellPatternsVariable, "claude:foo(bar)"), target.Environment);
    }

    /// <summary>
    /// Fail-closed half of #1459 fix 2: a clause that STARTS a <c>Bash(</c> grant but whose
    /// parentheses never balance (no closing <c>)</c> anywhere) must not fall back to the pre-fix
    /// silent-empty-channel behaviour -- that shape is indistinguishable from "deliberately unscoped
    /// shell" once it reaches <see cref="HookCheckCommand.Decide"/>, which is the exact #1459 bypass
    /// this fix closes. <see cref="Resolve"/> must throw <see cref="PermissionGrantUnsupportedException"/>
    /// instead, matching <see cref="TryTranslatePermissionGrant"/>'s own resolve-time fail-closed
    /// precedent for an untranslatable structured grant.
    /// </summary>
    [Fact]
    public void An_unbalanced_Bash_clause_in_the_raw_scope_makes_Resolve_throw_instead_of_emitting_an_empty_channel()
    {
        var exception = Assert.Throws<PermissionGrantUnsupportedException>(() =>
            new ClaudeWorkerAdapter().Resolve(
                new WorkerInvocation("Draft a plan.", PermissionScope: "Write,Bash(git diff*"),
                ArchitectContract));

        Assert.Equal("claude", exception.AdapterName);
    }

    /// <summary>
    /// Round-5 HIGH: a balanced string with no top-level comma is one clause -- when that clause
    /// starts with something other than <c>Bash(</c>, the loop's <c>StartsWith("Bash(")</c> drops it
    /// whole, taking a fused <c>Bash(</c> grant down with it. <c>ClaudeWorkerAdapter
    /// .BuildShellPatternsFromRawScope</c>'s own "Fusion gate" remarks record the conservation-count
    /// mechanism that now catches this instead of silently emitting an empty channel.
    /// </summary>
    [Fact]
    public void A_Bash_grant_fused_after_a_balanced_leading_clause_makes_Resolve_throw()
    {
        var exception = Assert.Throws<PermissionGrantUnsupportedException>(() =>
            new ClaudeWorkerAdapter().Resolve(
                new WorkerInvocation("Draft a plan.", PermissionScope: "Read()Bash(git diff*)"),
                ArchitectContract));

        Assert.Equal("claude", exception.AdapterName);
    }

    /// <summary>
    /// Two <c>Bash(</c> grants with no separating top-level comma are still one clause by
    /// <c>SplitAtTopLevelCommas</c>'s count -- the fusion gate's occurrence-vs-headed-clause count
    /// (2 vs 1) is what catches this, not the balance gate (the string balances).
    /// </summary>
    [Fact]
    public void Two_Bash_grants_fused_together_with_no_separating_comma_make_Resolve_throw()
    {
        var exception = Assert.Throws<PermissionGrantUnsupportedException>(() =>
            new ClaudeWorkerAdapter().Resolve(
                new WorkerInvocation(
                    "Draft a plan.", PermissionScope: "Bash(git diff*)Bash(git status*)"),
                ArchitectContract));

        Assert.Equal("claude", exception.AdapterName);
    }

    /// <summary>
    /// Leading text before a <c>Bash(</c> grant, with no separating comma, fuses the grant into a
    /// clause that does not start with <c>Bash(</c> -- must throw rather than drop it.
    /// </summary>
    [Fact]
    public void Leading_text_fused_before_a_Bash_grant_makes_Resolve_throw()
    {
        var exception = Assert.Throws<PermissionGrantUnsupportedException>(() =>
            new ClaudeWorkerAdapter().Resolve(
                new WorkerInvocation("Draft a plan.", PermissionScope: "x Bash(git diff*)"),
                ArchitectContract));

        Assert.Equal("claude", exception.AdapterName);
    }

    /// <summary>
    /// Same shape as the leading-text case with no whitespace at all -- the fused text sits directly
    /// against the grant, so the clause still does not start with <c>Bash(</c> after trimming.
    /// </summary>
    [Fact]
    public void A_Bash_grant_fused_directly_after_leading_text_with_no_space_makes_Resolve_throw()
    {
        var exception = Assert.Throws<PermissionGrantUnsupportedException>(() =>
            new ClaudeWorkerAdapter().Resolve(
                new WorkerInvocation("Draft a plan.", PermissionScope: "XBash(git diff*)"),
                ArchitectContract));

        Assert.Equal("claude", exception.AdapterName);
    }

    /// <summary>
    /// A <c>Bash(</c> grant nested inside a non-<c>Bash</c> clause's parens -- balanced, one top-level
    /// clause, headed by <c>Read(</c> rather than <c>Bash(</c>. The fusion gate must catch this the
    /// same way as the unnested fusion shapes above rather than treating "nested" as safe.
    /// </summary>
    [Fact]
    public void A_Bash_grant_nested_inside_a_non_Bash_clause_makes_Resolve_throw()
    {
        var exception = Assert.Throws<PermissionGrantUnsupportedException>(() =>
            new ClaudeWorkerAdapter().Resolve(
                new WorkerInvocation("Draft a plan.", PermissionScope: "Read(Bash(x))"),
                ArchitectContract));

        Assert.Equal("claude", exception.AdapterName);
    }

    /// <summary>
    /// Round-5 re-review MEDIUM: an explicit but empty <c>Bash()</c> clause used to clear both the
    /// balance gate and the fusion gate, then quietly vanish at the per-clause trim -- see
    /// <see cref="ClaudeWorkerAdapter.BuildShellPatternsFromRawScope"/>'s own remarks and its
    /// per-clause throw for the mechanism.
    /// </summary>
    [Fact]
    public void An_empty_pattern_Bash_clause_makes_Resolve_throw_instead_of_silently_yielding_an_empty_channel()
    {
        var exception = Assert.Throws<PermissionGrantUnsupportedException>(() =>
            new ClaudeWorkerAdapter().Resolve(
                new WorkerInvocation("Draft a plan.", PermissionScope: "Write,Bash()"),
                ArchitectContract));

        Assert.Equal("claude", exception.AdapterName);
    }

    /// <summary>
    /// Same shape as above with a whitespace-only interior -- <c>Trim()</c> reduces it to the same
    /// empty pattern, so it must throw identically rather than being read as a non-empty grant.
    /// </summary>
    [Fact]
    public void A_whitespace_only_pattern_Bash_clause_makes_Resolve_throw_instead_of_silently_yielding_an_empty_channel()
    {
        var exception = Assert.Throws<PermissionGrantUnsupportedException>(() =>
            new ClaudeWorkerAdapter().Resolve(
                new WorkerInvocation("Draft a plan.", PermissionScope: "Write,Bash(   )"),
                ArchitectContract));

        Assert.Equal("claude", exception.AdapterName);
    }

    /// <summary>
    /// #1515: <c>ClaudeWorkerAdapter.BuildShellPatternsFromRawScope</c>'s own remarks carry the
    /// measurement and the reasoning. Must throw here, not reach an empty no-op channel.
    /// </summary>
    [Fact]
    public void A_Bash_clause_with_a_space_before_the_paren_makes_Resolve_throw()
    {
        var exception = Assert.Throws<PermissionGrantUnsupportedException>(() =>
            new ClaudeWorkerAdapter().Resolve(
                new WorkerInvocation("Draft a plan.", PermissionScope: "Write,Bash (git diff*)"),
                ArchitectContract));

        Assert.Equal("claude", exception.AdapterName);
    }

    /// <summary>
    /// Same shape as above with a tab instead of a space -- <c>\s</c> covers both, and the CLI's own
    /// parser was not measured to distinguish them, so this must throw identically.
    /// </summary>
    [Fact]
    public void A_Bash_clause_with_a_tab_before_the_paren_makes_Resolve_throw()
    {
        var exception = Assert.Throws<PermissionGrantUnsupportedException>(() =>
            new ClaudeWorkerAdapter().Resolve(
                new WorkerInvocation("Draft a plan.", PermissionScope: "Write,Bash\t(git diff*)"),
                ArchitectContract));

        Assert.Equal("claude", exception.AdapterName);
    }

    /// <summary>
    /// #1515: the negative half of the measurement <c>BuildShellPatternsFromRawScope</c>'s own
    /// remarks record -- must NOT throw, and must yield an empty channel like any other non-Bash
    /// clause.
    /// </summary>
    [Fact]
    public void A_lowercase_bash_clause_still_yields_an_empty_shell_pattern_channel()
    {
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionScope: "Write,bash(git diff*)"),
            ArchitectContract);

        Assert.NotNull(target.Environment);
        Assert.Contains((ClaudeWorkerAdapter.ShellPatternsVariable, "claude:"), target.Environment);
    }

    /// <summary>
    /// The named pin for <c>TryExtractBalancedBashClauseInner</c>'s two paren offsets (PR #1952
    /// re-review): it drives the parser and reads the extracted pattern back off the channel, so it
    /// goes red on either mutation — starting the depth scan at <c>BashGrantPrefix.Length</c> (depth
    /// never reaches 1, so <c>Resolve</c> throws instead of granting) or slicing the interior from
    /// <c>BashToolName.Length</c> (the channel carries a leading <c>'('</c>). Not new coverage:
    /// <see cref="Interior_whitespace_inside_a_single_Bash_clause_is_trimmed_from_the_channel"/>
    /// already fails on the second mutation. What it buys is a test that names the invariant, in
    /// place of one that asserted <c>BashGrantPrefix</c>'s own definition back to itself.
    /// </summary>
    [Fact]
    public void A_Bash_clause_reaches_the_channel_as_exactly_its_interior_text()
    {
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionScope: "Bash(git status --porcelain)"),
            ArchitectContract);

        Assert.NotNull(target.Environment);
        Assert.Contains(
            (ClaudeWorkerAdapter.ShellPatternsVariable, "claude:git status --porcelain"),
            target.Environment);
    }

    /// <summary>
    /// The polarity twin of the arm above: the same interior text with the opening paren one
    /// character later is not a grant clause at all, so it must never reach the channel. The vendor
    /// half of that (claude honors <c>Bash (pattern)</c>, so this refuses rather than drops) is
    /// measured once at
    /// <see cref="A_Bash_clause_with_a_space_before_the_paren_makes_Resolve_throw"/>; this arm
    /// asserts only the offset polarity — one index of paren placement separates the two outcomes.
    /// </summary>
    [Fact]
    public void The_same_interior_with_the_paren_one_character_later_never_reaches_the_channel()
    {
        var exception = Assert.Throws<PermissionGrantUnsupportedException>(() =>
            new ClaudeWorkerAdapter().Resolve(
                new WorkerInvocation("Draft a plan.", PermissionScope: "Bash (git status --porcelain)"),
                ArchitectContract));

        Assert.Equal("claude", exception.AdapterName);
    }

    /// <summary>
    /// The canonical no-whitespace form must keep parsing normally alongside the new whitespace refusal
    /// -- this is #1506's original comma-list-refusal test re-asserted here to pin that the #1515 fix
    /// did not disturb it.
    /// </summary>
    [Fact]
    public void The_canonical_no_whitespace_Bash_clause_still_parses()
    {
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionScope: "Write,Bash(git diff*)"),
            ArchitectContract);

        Assert.NotNull(target.Environment);
        Assert.Contains(
            (ClaudeWorkerAdapter.ShellPatternsVariable, "claude:git diff*"), target.Environment);
    }

    /// <summary>
    /// #543, from review: an inherited `CLAUDE_CODE_SIMPLE=1` disables hooks the same way `--bare`
    /// does (see the doc comment above `SimpleModeVariable`'s declaration), and `BatonTask` inherits
    /// the full parent environment by default -- so this override has to actually be on the argv
    /// this method returns, not merely exist as an idea in a comment.
    /// </summary>
    [Fact]
    public void An_inherited_CLAUDE_CODE_SIMPLE_is_overridden_in_the_process_environment()
    {
        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        Assert.NotNull(target.Environment);
        Assert.Contains((ClaudeWorkerAdapter.SimpleModeVariable, "0"), target.Environment);
    }

    /// <summary>
    /// #533 constraint 3, measured (not vendor-documented) default: `verify.py`'s
    /// `fanout.nesting-allowed-by-default` found a subagent CAN spawn its own subagent with nothing
    /// configured, so AER sets the cap explicitly rather than trusting the vendor's stated default.
    /// </summary>
    [Fact]
    public void The_subagent_spawn_depth_is_capped_to_one_via_the_process_environment()
    {
        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        Assert.NotNull(target.Environment);
        Assert.Contains(
            (ClaudeWorkerAdapter.MaxSubagentSpawnDepthVariable, "1"),
            target.Environment);
    }

    // The tests above assert against the C# objects Resolve() builds -- they would pass equally
    // against a hook command that looks right on paper but fails the moment Claude Code actually
    // spawns it. These two spawn the exact command+args the settings file names, as a real child
    // process fed real stdin and the real environment variable, exactly as Claude Code's exec-form
    // hook dispatch does -- proving the wiring, not just the shape. `Baton.Vendors.Tests` has no
    // project reference to `Baton.Cli` (layering: the CLI depends on the adapters, never the
    // reverse), so this runs the built executable directly rather than calling HookCheckCommand
    // in-process; it needs `Baton.Cli` built into a sibling output directory, true for any normal
    // `pixi run test` / `pixi run build` run.

    [Fact]
    public void The_resolved_hook_command_actually_denies_a_withheld_tool_when_spawned_for_real()
    {
        var grant = new PermissionGrant(ReadFiles: true);
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract);

        var (exitCode, stderr) = RunResolvedHookCommand(target, """{"tool_name": "Bash"}""");

        Assert.Equal(2, exitCode);
        Assert.Contains("Bash", stderr);
    }

    [Fact]
    public void The_resolved_hook_command_actually_allows_a_granted_tool_when_spawned_for_real()
    {
        var grant = new PermissionGrant(ReadFiles: true);
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract);

        var (exitCode, stderr) = RunResolvedHookCommand(target, """{"tool_name": "Read"}""");

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr);
    }

    /// <summary>
    /// End-to-end regression, #1459 fix: a raw <c>PermissionScope</c> dispatch (no
    /// <c>PermissionGrant</c>) scoping its shell to <c>Bash(git diff*)</c> must have the #1461
    /// chaining escape denied by the real spawned hook process, exactly as a structured-grant
    /// dispatch already is. Before this fix the hook channel this test reads through
    /// <see cref="RunResolvedHookCommand"/> came out tagged-and-empty for a raw-scope dispatch, so
    /// <c>HookCheckCommand.Decide</c> took its deliberate unscoped-shell no-op branch and this command
    /// was allowed.
    /// </summary>
    [Fact]
    public void Regression_1459_the_resolved_hook_command_denies_the_1461_escape_under_a_raw_PermissionScope_dispatch()
    {
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionScope: "Write,Bash(git diff*)"), ArchitectContract);

        var (exitCode, stderr) = RunResolvedHookCommand(
            target, """{"tool_name": "Bash", "tool_input": {"command": "git diff; echo escaped"}}""");

        Assert.Equal(2, exitCode);
        Assert.Contains("this session's shell grant", stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// Control for the regression above: the same raw-scope dispatch must still ALLOW a command that
    /// actually matches the granted pattern, through the real spawned hook process -- proving the
    /// fix denies the escape specifically, not shell use in general.
    /// </summary>
    [Fact]
    public void The_resolved_hook_command_allows_a_matching_command_under_a_raw_PermissionScope_dispatch()
    {
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionScope: "Write,Bash(git diff*)"), ArchitectContract);

        var (exitCode, stderr) = RunResolvedHookCommand(
            target, """{"tool_name": "Bash", "tool_input": {"command": "git diff"}}""");

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr);
    }

    /// <summary>
    /// End-to-end regression, #1459 fix 3: the multi-clause form <c>Bash(git diff*),Bash(git status*)</c>
    /// (as opposed to the now-refused single-clause comma-list <c>Bash(git diff*, git status*)</c>) is
    /// the shape the engine itself emits for multiple patterns, and must, through the REAL spawned hook
    /// process, both DENY the #1461 chaining escape and ALLOW each of the two granted patterns.
    /// </summary>
    [Fact]
    public void Regression_1459fix3_the_resolved_hook_command_denies_the_1461_escape_and_allows_both_multi_clause_patterns()
    {
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation(
                "Draft a plan.", PermissionScope: "Write,Bash(git diff*),Bash(git status*)"),
            ArchitectContract);

        var (escapeExitCode, escapeStderr) = RunResolvedHookCommand(
            target, """{"tool_name": "Bash", "tool_input": {"command": "git diff; echo escaped"}}""");
        Assert.Equal(2, escapeExitCode);
        Assert.Contains("this session's shell grant", escapeStderr, StringComparison.Ordinal);

        var (diffExitCode, diffStderr) = RunResolvedHookCommand(
            target, """{"tool_name": "Bash", "tool_input": {"command": "git diff"}}""");
        Assert.Equal(0, diffExitCode);
        Assert.Empty(diffStderr);

        var (statusExitCode, statusStderr) = RunResolvedHookCommand(
            target, """{"tool_name": "Bash", "tool_input": {"command": "git status"}}""");
        Assert.Equal(0, statusExitCode);
        Assert.Empty(statusStderr);
    }

    private static (int ExitCode, string Stderr) RunResolvedHookCommand(CoreDispatchTarget target, string stdin)
    {
        var settingsPath = ArgValue(target, "--settings")!;
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(settingsPath));
        var handler = doc.RootElement.GetProperty("hooks").GetProperty("PreToolUse")[0].GetProperty("hooks")[0];
        var command = handler.GetProperty("command").GetString()!;
        var args = handler.GetProperty("args").EnumerateArray().Select(e => e.GetString()).ToList();

        var startInfo = new ProcessStartInfo(command)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg!);
        }

        // Forward every environment variable Resolve prepared, not just the denied-tools one -- a
        // real Claude Code spawn inherits the whole process environment, and #1459's own shell-pattern
        // channels need to reach this real subprocess too for a scoped-shell dispatch to be provable
        // end to end here (a partial simulation that only forwarded the denied-tools variable would
        // have passed the pre-fix bypass just as easily as the fixed behaviour).
        foreach (var (name, value) in target.Environment!)
        {
            startInfo.Environment[name] = value;
        }

        using var process = Process.Start(startInfo)!;
        process.StandardInput.Write(stdin);
        process.StandardInput.Close();
        var stderr = process.StandardError.ReadToEnd();
        var exited = process.WaitForExit(TimeSpan.FromSeconds(60));
        Assert.True(exited, "hook-check did not exit within 30s");

        return (process.ExitCode, stderr);
    }

    [Fact]
    public void Withheld_writes_leave_the_flag_and_move_to_the_hooks_list()
    {
        // #649's boundary change, asserted on both channels at once because the whole point is that
        // they now differ. A write named in --disallowedTools is refused by the CLI before the hook is
        // consulted, so leaving it there makes the outbox exemption unreachable and a read-only
        // reviewer unable to produce the artifact it was dispatched for. The hook keeps the names,
        // because it is what still denies a workspace write.
        var grant = new PermissionGrant(ReadFiles: true, WriteFiles: false);
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract);

        var flag = ArgValue(target, "--disallowedTools") ?? string.Empty;
        var hookList = target.Environment!.Single(v => v.Name == ClaudeWorkerAdapter.DeniedToolsVariable).Value;

        Assert.DoesNotContain("Write", flag, StringComparison.Ordinal);
        Assert.DoesNotContain("Edit", flag, StringComparison.Ordinal);
        Assert.Contains("Write", hookList, StringComparison.Ordinal);
        Assert.Contains("Edit", hookList, StringComparison.Ordinal);
        Assert.Contains("NotebookEdit", hookList, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_other_withheld_category_still_appears_on_both_channels()
    {
        // The control on the change above. Only writes move; a change that dropped every category from
        // the flag would pass the first assertion and quietly remove the enforcement the flag provides
        // for the categories where the hook has no path to inspect.
        var grant = new PermissionGrant(
            ReadFiles: false, WriteFiles: true, RunShellCommands: false, NetworkAccess: false);
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract);

        var flag = ArgValue(target, "--disallowedTools")!;
        var hookList = target.Environment!.Single(v => v.Name == ClaudeWorkerAdapter.DeniedToolsVariable).Value;

        foreach (var tool in new[] { "Read", "Bash", "WebFetch", "WebSearch" })
        {
            Assert.Contains(tool, flag, StringComparison.Ordinal);
            Assert.Contains(tool, hookList, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// #801: a dispatch that does not opt in must see today's exact `--mcp-config` -- the shared,
    /// deliberately empty `claude-mcp.json` -- with no silent behaviour change from this issue's work.
    /// </summary>
    [Fact]
    public void Not_opting_in_to_the_memory_proposal_tool_keeps_the_empty_mcp_config()
    {
        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        var mcpConfigPath = ArgValue(target, "--mcp-config");

        Assert.Equal(Path.Combine(BatonPaths.WorkerLaunchConfig, "claude-mcp.json"), mcpConfigPath);
        using var mcpDoc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(mcpConfigPath!));
        Assert.False(mcpDoc.RootElement.GetProperty("mcpServers").EnumerateObject().Any());
    }

    /// <summary>
    /// #801/#833: opting in points `--mcp-config` at a real config naming AER's own MCP server and
    /// the `memory-edit-proposal` tool, invoked via `Baton.Cli.dll mcp --memory-proposal-tool` -- the
    /// same `dotnet <dll>` shape #543 requires for the PreToolUse hook, for the identical
    /// packed-global-tool deployment reason. #1458: `mcp` is a verb on `Baton.Cli.dll` now, not its
    /// own `Baton.Mcp.Host.dll` -- asserted by exact args order below, not just membership, since a
    /// membership-only assertion is what let #1458 3b ship this path with the verb missing (a real
    /// escaped defect: this test asserted `EndsWith("Baton.Mcp.Host.dll")` after that project was
    /// deleted, and stayed green because nothing checked the `mcp` verb was ever added).
    /// No capture-directory path rides the args (#833) -- see
    /// `ClaudeWorkerAdapter.EnsureMemoryProposalMcpConfig`'s own remarks (canonical) for why.
    /// </summary>
    [Fact]
    public void Opting_in_to_the_memory_proposal_tool_points_mcp_config_at_a_real_server_naming_the_tool_host()
    {
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", EnableMemoryProposalTool: true), ArchitectContract);

        var mcpConfigPath = ArgValue(target, "--mcp-config");

        Assert.NotNull(mcpConfigPath);
        Assert.NotEqual(Path.Combine(BatonPaths.WorkerLaunchConfig, "claude-mcp.json"), mcpConfigPath);
        Assert.True(File.Exists(mcpConfigPath), "the file --mcp-config points at must already exist");

        using var mcpDoc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(mcpConfigPath!));
        var server = mcpDoc.RootElement.GetProperty("mcpServers").GetProperty("baton-memory-proposal");
        Assert.Equal("dotnet", server.GetProperty("command").GetString());
        var serverArgs = server.GetProperty("args").EnumerateArray().Select(a => a.GetString()).ToList();
        Assert.True(serverArgs.Count >= 3, "expected <dll path>, mcp, --memory-proposal-tool");
        Assert.EndsWith("Baton.Cli.dll", serverArgs[0], StringComparison.Ordinal);
        Assert.Equal("mcp", serverArgs[1]);
        Assert.Contains("--memory-proposal-tool", serverArgs);
        Assert.DoesNotContain(serverArgs, a => a!.Contains("memory-proposals", StringComparison.Ordinal));
    }

    [Fact]
    public void Claude_config_root_unset_injects_no_CLAUDE_CONFIG_DIR()
    {
        // Scope from Current, not Blank: Resolve() also writes claude-settings.json/claude-mcp.json
        // under BatonPaths.WorkerLaunchConfig (BatonPaths.Root -> HomeOverride), so the scope must
        // carry forward whatever redirected home is already ambient (BatonHomeRedirect's module
        // initializer, in this assembly) rather than blanking it back to the real ~/.baton. See
        // BatonEnvironmentSnapshot's remarks for why Blank is the wrong base for a partial override
        // here.
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Current with { ClaudeConfigRootOverride = null });

        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        Assert.DoesNotContain(target.Environment!, e => e.Name == ClaudeWorkerAdapter.ClaudeConfigDirVariable);
    }

    [Fact]
    public void Claude_config_root_set_injects_CLAUDE_CONFIG_DIR_for_batch_and_gate()
    {
        const string testPath = @"C:\baton\claude-root";
        // See the sibling test above: scope from Current so the redirected BATON_HOME survives.
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Current with { ClaudeConfigRootOverride = testPath });

        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", SessionId: "session-123", ResumeSession: true), ArchitectContract);

        Assert.Contains(target.Environment!, e => e.Name == ClaudeWorkerAdapter.ClaudeConfigDirVariable && e.Value == testPath);
    }

    /// <summary>
    /// #1834's measurement (#1827, CLI 2.1.258): a room directory with a <c>.claude</c> path component
    /// anywhere is refused, even with no operator-configured config root at all -- the refusal keys on
    /// the component, not on <c>CLAUDE_CONFIG_DIR</c>'s value.
    /// </summary>
    [Fact]
    public void HasSensitiveOutputPathComponent_refuses_a_dot_claude_component_mid_path()
    {
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Current with { ClaudeConfigRootOverride = null });

        const string roomDirectory = @"C:\repo\.claude\worktrees\room1";

        var matched = new ClaudeWorkerAdapter().HasSensitiveOutputPathComponent(roomDirectory, out var offendingComponent);

        Assert.True(matched);
        Assert.Equal(".claude", offendingComponent);
    }

    /// <summary>
    /// #1834's measurement: a room directory under a <c>CLAUDE_CONFIG_DIR</c> override whose own leaf is
    /// NOT named <c>.claude</c> is allowed -- the override's value plays no part in the predicate.
    /// </summary>
    [Fact]
    public void HasSensitiveOutputPathComponent_allows_a_room_under_a_non_dot_claude_config_root_override()
    {
        const string configRoot = @"C:\baton\cfg";
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Current with { ClaudeConfigRootOverride = configRoot });

        const string roomDirectory = @"C:\baton\cfg\room1";

        var matched = new ClaudeWorkerAdapter().HasSensitiveOutputPathComponent(roomDirectory, out var offendingComponent);

        Assert.False(matched);
        Assert.Null(offendingComponent);
    }

    /// <summary>
    /// #1834's measurement: only a component literally named <c>.claude</c> matches -- a look-alike
    /// like <c>.claudex</c> is a different name and does not.
    /// </summary>
    [Fact]
    public void HasSensitiveOutputPathComponent_allows_a_dot_claudex_lookalike_component()
    {
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Current with { ClaudeConfigRootOverride = null });

        const string roomDirectory = @"C:\repo\.claudex\room1";

        var matched = new ClaudeWorkerAdapter().HasSensitiveOutputPathComponent(roomDirectory, out var offendingComponent);

        Assert.False(matched);
        Assert.Null(offendingComponent);
    }

    /// <summary>
    /// #1834's measurement: comparison is case-insensitive on Windows (matching claude's own
    /// filesystem-backed refusal there). A case variant would be a different, case-sensitive file name
    /// on other filesystems; this suite runs only on Windows, so the refusal is asserted unconditionally.
    /// </summary>
    [Fact]
    public void HasSensitiveOutputPathComponent_refuses_a_windows_case_variant()
    {
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Current with { ClaudeConfigRootOverride = null });

        var roomDirectory = @"C:\repo\.Claude\worktrees\room1";

        var matched = new ClaudeWorkerAdapter().HasSensitiveOutputPathComponent(roomDirectory, out var offendingComponent);

        Assert.True(matched);
        Assert.Equal(".Claude", offendingComponent);
    }

    /// <summary>
    /// Tripwire for the leak the CI post-test pollution check caught (see
    /// <see cref="BatonEnvironmentSnapshot.Blank"/>'s remarks): under a <c>BeginScope</c> home
    /// override, the launch config <see cref="ClaudeWorkerAdapter.Resolve"/> writes on every call must
    /// land under that override — never under the real <c>~/.baton</c> — regardless of which other
    /// fields the same scope also overrides.
    /// </summary>
    [Fact]
    public void Resolve_writes_launch_config_under_a_scoped_home_override_never_the_real_home()
    {
        var overrideHome = Path.Combine(Path.GetTempPath(), $"claude-launch-config-tripwire-{Guid.NewGuid():N}");
        Directory.CreateDirectory(overrideHome);
        // The negative half of the name: the real ~/.baton/worker-launch must not be rewritten. A leaked
        // write stamps THIS test process's AppContext.BaseDirectory into the hook path (that is how the
        // CI pollution check's diff read), so the real files must not mention it afterwards. Content, not
        // mtime: on the operator's machine a legitimate dispatch can rewrite these files mid-test.
        var realLaunchDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".baton", "worker-launch");
        var realSettings = Path.Combine(realLaunchDir, "claude-settings.json");
        var realMcp = Path.Combine(realLaunchDir, "claude-mcp.json");
        var thisProcessMarker = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        try
        {
            using var scope = BatonEnvironmentSnapshot.BeginScope(
                BatonEnvironmentSnapshot.Current with { HomeOverride = overrideHome, ClaudeConfigRootOverride = null });

            new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

            Assert.True(File.Exists(Path.Combine(overrideHome, "worker-launch", "claude-settings.json")));
            Assert.True(File.Exists(Path.Combine(overrideHome, "worker-launch", "claude-mcp.json")));
            foreach (var realFile in new[] { realSettings, realMcp })
            {
                if (File.Exists(realFile))
                {
                    // JSON doubles backslashes; unescape before comparing so a Windows path can match at all.
                    var unescaped = File.ReadAllText(realFile).Replace("\\\\", "\\");
                    Assert.DoesNotContain(thisProcessMarker, unescaped, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(overrideHome);
        }
    }

    [Fact]
    public void TruncatedEnvelopeInTail_FailsClosed_NoClassificationNoThrow()
    {
        // #1115 review: the tail buffers cut front-first mid-line, so the classifier can be
        // handed half a JSON envelope — even one whose retained half still contains the literal
        // "credits_required". Unparseable input must fail closed: no classification, no throw.
        var frontCut = """error","errorCode":"credits_required","result":"Subscription quota exhausted."}""";
        var testTime = new TestTimeProvider(DateTimeOffset.UtcNow);

        var adapter = new ClaudeWorkerAdapter();
        var classified = adapter.TryClassifyFailure(frontCut, testTime, out var classification, out _);

        Assert.False(classified);
        Assert.Null(classification);
    }

    [Fact]
    public void CreditsRequired_ClassifiesExhaustedUntil()
    {
        var envelope = """{"type":"result","is_error":true,"errorCode":"credits_required","result":"Subscription quota exhausted."}""";
        var testTime = new TestTimeProvider(DateTimeOffset.UtcNow);

        var adapter = new ClaudeWorkerAdapter();
        var classified = adapter.TryClassifyFailure(envelope, testTime, out var classification, out var retryNotBefore);

        Assert.True(classified);
        Assert.Equal(FailureClassification.ExhaustedUntil, classification);
        Assert.Null(retryNotBefore);
    }

    [Theory]
    [InlineData("""{"type":"result","is_error":true,"errorCode":"other_error","result":"Failed"}""")]
    [InlineData("""{"type":"result","is_error":true,"result":"Failed without errorCode"}""")]
    public void OrdinaryError_StaysUnclassified(string envelope)
    {
        var testTime = new TestTimeProvider(DateTimeOffset.UtcNow);

        var adapter = new ClaudeWorkerAdapter();
        var classified = adapter.TryClassifyFailure(envelope, testTime, out var classification, out var retryNotBefore);

        Assert.False(classified);
        Assert.Null(classification);
        Assert.Null(retryNotBefore);
    }

    [Fact]
    public void CreditsRequiredProseInMessageText_DoesNotTrigger()
    {
        var envelope = """{"type":"assistant","message":{"content":[{"type":"text","text":"The system reported credits_required in prose text"}]}}""";
        var testTime = new TestTimeProvider(DateTimeOffset.UtcNow);

        var adapter = new ClaudeWorkerAdapter();
        var classified = adapter.TryClassifyFailure(envelope, testTime, out var classification, out var retryNotBefore);

        Assert.False(classified);
        Assert.Null(classification);
        Assert.Null(retryNotBefore);
    }

    [Fact]
    public void CreditsRequired_OnStdoutTail_ClassifiesExhaustedUntil()
    {
        var envelope = """{"type":"result","is_error":true,"errorCode":"credits_required","result":"Subscription quota exhausted."}""";
        var testTime = new TestTimeProvider(DateTimeOffset.UtcNow);

        IFailureClassifier adapter = new ClaudeWorkerAdapter();
        var classified = adapter.TryClassifyFailure(stderrTail: null, stdoutTail: envelope, testTime, out var classification, out var retryNotBefore);

        Assert.True(classified);
        Assert.Equal(FailureClassification.ExhaustedUntil, classification);
        Assert.Null(retryNotBefore);
    }

    [Fact]
    public void CreditsRequired_InRealisticStreamJsonStdoutTail_ClassifiesExhaustedUntil()
    {
        // #1540: multi-line stream-json tail containing system init, assistant message, and terminal error result
        var streamJsonTail = """
            {"type":"system","subtype":"init","session_id":"s-123","tools":["Bash"]}
            {"type":"assistant","message":{"content":[{"type":"text","text":"Attempting operation..."}]}}
            {"type":"result","subtype":"error","is_error":true,"errorCode":"credits_required","result":"Subscription quota exhausted."}
            """;
        var testTime = new TestTimeProvider(DateTimeOffset.UtcNow);

        IFailureClassifier adapter = new ClaudeWorkerAdapter();
        var classified = adapter.TryClassifyFailure(stderrTail: null, stdoutTail: streamJsonTail, testTime, out var classification, out var retryNotBefore);

        Assert.True(classified);
        Assert.Equal(FailureClassification.ExhaustedUntil, classification);
        Assert.Null(retryNotBefore);
    }

    /// <summary>
    /// #1622: the real integration arm for room dispatch-implement-d6101c3c's own measured shape —
    /// <see cref="ClaudeWorkerAdapter"/> itself as the <see cref="IFailureClassifier"/>, parsing a
    /// genuine multi-line stream-json tail, unlike Baton.Tests' OutcomeClassifierTests.cs arms, which
    /// stand in a canned double (Baton cannot reference Baton.Vendors, Architecture Rule 2 -- this is
    /// the one place the real parse and OutcomeClassifier.Classify run together).
    /// </summary>
    [Fact]
    public void Classify_vetoes_a_satisfied_exit_0_run_when_the_real_stream_json_stdout_tail_carries_credits_required()
    {
        var streamJsonTail = """
            {"type":"system","subtype":"init","session_id":"s-123","tools":["Bash"]}
            {"type":"assistant","message":{"content":[{"type":"text","text":"Attempting operation..."}]}}
            {"type":"result","subtype":"error","is_error":true,"errorCode":"credits_required","result":"Subscription quota exhausted."}
            """;
        var contract = new WorkerContract("worker", [], [], []);
        var directory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            IFailureClassifier adapter = new ClaudeWorkerAdapter();

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural, StderrTail: null, StdoutTail: streamJsonTail),
                contract,
                directory,
                adapter);

            Assert.Equal(OutcomeVerdict.Failed, classification.Verdict);
            Assert.Equal(FailureClassification.ExhaustedUntil, classification.FailureClassification);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    /// <summary>
    /// #1727 (found while fixing the #1720 review's F1): the SAME tail as the arm above, in the shape
    /// production actually captures it — see <see cref="StreamJsonTailScanner"/> for the collapse and
    /// what it did to the old whole-parse-then-split-on-newline check. Red before that scanner; the
    /// raw-newline arm above is the control that stayed green throughout, which is exactly why the
    /// gap was invisible.
    /// </summary>
    [Fact]
    public void CreditsRequired_InTheWhitespaceCollapsedTailProductionActuallyCaptures_ClassifiesExhaustedUntil()
    {
        var collapsedTail =
            """{"type":"system","subtype":"init","session_id":"s-123","tools":["Bash"]} """
            + """{"type":"assistant","message":{"content":[{"type":"text","text":"Attempting operation..."}]}} """
            + """{"type":"result","subtype":"error","is_error":true,"errorCode":"credits_required","result":"Subscription quota exhausted."}""";
        var testTime = new TestTimeProvider(DateTimeOffset.UtcNow);

        IFailureClassifier adapter = new ClaudeWorkerAdapter();
        var classified = adapter.TryClassifyFailure(
            stderrTail: null, stdoutTail: collapsedTail, testTime, out var classification, out _);

        Assert.True(classified);
        Assert.Equal(FailureClassification.ExhaustedUntil, classification);
    }

    /// <summary>
    /// #1727's polarity control: an assistant message whose own TEXT quotes the typed error code is
    /// nested under <c>message.content[].text</c>, so scanning for top-level objects must not match
    /// it — the scanner widened WHERE the check looks, not WHAT counts as the signal.
    /// </summary>
    [Fact]
    public void A_workers_own_answer_text_quoting_credits_required_does_not_classify()
    {
        var collapsedTail =
            """{"type":"system","subtype":"init","session_id":"s-123"} """
            + """{"type":"assistant","message":{"content":[{"type":"text","text":"The vendor reports errorCode credits_required when the subscription runs dry."}]}} """
            + """{"type":"result","subtype":"success","is_error":false,"result":"Done."}""";
        var testTime = new TestTimeProvider(DateTimeOffset.UtcNow);

        IFailureClassifier adapter = new ClaudeWorkerAdapter();
        var classified = adapter.TryClassifyFailure(
            stderrTail: null, stdoutTail: collapsedTail, testTime, out var classification, out _);

        Assert.False(classified);
        Assert.Null(classification);
    }

    /// <summary>
    /// #1720 review Finding G: the arm above quotes only the ERROR CODE in prose ("errorCode
    /// credits_required"), which the old check already rejected before this fix. The claim
    /// <see cref="StreamJsonTailScanner.AnyObject"/> actually has to defeat is a worker's answer text
    /// embedding a FULL verbatim JSON envelope — the escaped-quote shape a real vendor tail can never
    /// produce unescaped, per <see cref="StreamJsonTailScanner"/>'s own doc. Written as a raw string
    /// literal so the backslashes survive into the runtime bytes: a regular literal would collapse
    /// <c>\"</c> to <c>"</c> at compile time and pin nothing.
    /// </summary>
    [Fact]
    public void A_workers_own_answer_text_embedding_a_full_verbatim_envelope_does_not_classify()
    {
        var collapsedTail =
            """{"type":"system","subtype":"init","session_id":"s-123"} """
            + """{"type":"assistant","message":{"content":[{"type":"text","text":"the failure line was {\"errorCode\":\"credits_required\"}"}]}} """
            + """{"type":"result","subtype":"success","is_error":false,"result":"Done."}""";
        var testTime = new TestTimeProvider(DateTimeOffset.UtcNow);

        IFailureClassifier adapter = new ClaudeWorkerAdapter();
        var classified = adapter.TryClassifyFailure(
            stderrTail: null, stdoutTail: collapsedTail, testTime, out var classification, out _);

        Assert.False(classified);
        Assert.Null(classification);
    }

    /// <summary>
    /// #1166: decision 0004's project ceiling fails closed against a project directory
    /// <see cref="ProjectCeilingStore"/> has never seen -- red-first against the pre-#1166 behaviour,
    /// which spawned unconditionally whenever WorkingDirectory was set.
    /// </summary>
    [Fact]
    public void An_unseen_project_directory_is_refused_before_any_worker_spawns()
    {
        var unseenProject = Path.Combine(Path.GetTempPath(), $"baton-ceiling-unseen-{Guid.NewGuid():N}");

        var ex = Assert.Throws<ProjectNotTrustedException>(() => new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", WorkingDirectory: unseenProject), ArchitectContract));

        Assert.Equal(unseenProject, ex.ProjectPath);
        Assert.NotNull(ex.TryInvocation);
        Assert.Contains("baton trust", ex.TryInvocation, StringComparison.Ordinal);
    }

    /// <summary>
    /// #1166: effective grant = role grant ∩ project ceiling. The role grants WriteFiles; the
    /// project's recorded ceiling withholds it, so the capped grant must withhold it too. Asserted on
    /// the hook-denied-tools channel rather than <c>--allowedTools</c>, because #649 pre-approves
    /// Edit/Write/NotebookEdit unconditionally -- the flag alone would pass even if capping did nothing.
    /// </summary>
    [Fact]
    public void A_ceiling_below_the_role_grant_caps_the_effective_grant_to_the_intersection()
    {
        var project = Path.Combine(Path.GetTempPath(), $"baton-ceiling-cap-claude-{Guid.NewGuid():N}");
        ProjectCeilingStore.Set(
            project,
            new ProjectCeiling(ReadFiles: true, WriteFiles: false, RunShellCommands: true, NetworkAccess: true),
            ProjectCeilingStore.DefaultPath);
        var roleGrant = new PermissionGrant(ReadFiles: true, WriteFiles: true);

        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: roleGrant, WorkingDirectory: project),
            ArchitectContract);

        var hookDenied = target.Environment!.Single(v => v.Name == ClaudeWorkerAdapter.DeniedToolsVariable).Value;
        Assert.Contains("Write", hookDenied);
        Assert.Contains("Edit", hookDenied);
        Assert.Contains("NotebookEdit", hookDenied);
    }

    /// <summary>#1166: after 'baton trust --revoke', the next dispatch against that project refuses again.</summary>
    [Fact]
    public void A_revoked_project_is_refused_on_the_next_dispatch()
    {
        var project = Path.Combine(Path.GetTempPath(), $"baton-ceiling-revoke-claude-{Guid.NewGuid():N}");
        ProjectCeilingStore.Set(project, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);
        // Confirm it dispatches while trusted, so the refusal below is the revoke's effect and not a
        // ceiling that was never actually recorded.
        new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan.", WorkingDirectory: project), ArchitectContract);

        ProjectCeilingStore.Revoke(project, ProjectCeilingStore.DefaultPath);

        Assert.Throws<ProjectNotTrustedException>(() => new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", WorkingDirectory: project), ArchitectContract));
    }

    /// <summary>
    /// #1166 review finding A -- <see cref="ProjectCeilingGate"/>'s own doc has why. Both directions
    /// asserted (v-and-v): trust the source repo alone and dispatch succeeds even though the worktree
    /// path itself was never trusted; trust only the worktree path and dispatch still refuses, naming
    /// the source repo.
    /// </summary>
    [Fact]
    public void A_worktree_dispatch_keys_the_ceiling_on_the_source_repository_not_the_ephemeral_worktree_path()
    {
        var sourceRepo = Path.Combine(Path.GetTempPath(), $"baton-ceiling-worktree-src-{Guid.NewGuid():N}");
        var worktreePath = Path.Combine(Path.GetTempPath(), $"baton-ceiling-worktree-tree-{Guid.NewGuid():N}");
        ProjectCeilingStore.Set(sourceRepo, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);

        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation(
                "Draft a plan.", WorkingDirectory: worktreePath, WorktreeSourceRepository: sourceRepo),
            ArchitectContract);

        Assert.Equal(worktreePath, target.WorkingDirectory);

        var untrustedWorktreePath = Path.Combine(Path.GetTempPath(), $"baton-ceiling-worktree-tree2-{Guid.NewGuid():N}");
        ProjectCeilingStore.Set(untrustedWorktreePath, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);
        var otherSourceRepo = Path.Combine(Path.GetTempPath(), $"baton-ceiling-worktree-src2-{Guid.NewGuid():N}");

        var ex = Assert.Throws<ProjectNotTrustedException>(() => new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation(
                "Draft a plan.", WorkingDirectory: untrustedWorktreePath, WorktreeSourceRepository: otherSourceRepo),
            ArchitectContract));
        Assert.Equal(otherSourceRepo, ex.ProjectPath);
    }

    /// <summary>
    /// #1166 review finding B, the polarity partner of
    /// <see cref="AgyWorkerAdapterTests.A_ceiling_that_caps_away_write_files_refuses_a_contract_declaring_outputs_on_agy"/>:
    /// on claude a withheld write still reaches the outbox (#649, <c>WithheldWritesReachTheOutbox</c> is
    /// true), so capping WriteFiles away here must NOT refuse the same contract that throws on agy --
    /// otherwise the gate-level recheck would be over-firing rather than closing the specific #629 gap
    /// it exists for.
    /// </summary>
    [Fact]
    public void A_ceiling_that_caps_away_write_files_does_not_refuse_the_contract_on_claude()
    {
        var project = Path.Combine(Path.GetTempPath(), $"baton-ceiling-unsatisfiable-claude-{Guid.NewGuid():N}");
        ProjectCeilingStore.Set(
            project,
            new ProjectCeiling(ReadFiles: true, WriteFiles: false, RunShellCommands: false, NetworkAccess: false),
            ProjectCeilingStore.DefaultPath);
        var roleGrant = new PermissionGrant(ReadFiles: true, WriteFiles: true);

        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: roleGrant, WorkingDirectory: project),
            ArchitectContract);

        Assert.NotNull(target);
    }

    /// <summary>
    /// #1166 review finding C: neither of the gate's own two structural refusals had a test. A ceiling
    /// that withholds a category (here NetworkAccess) has nothing to intersect against when the
    /// invocation carries only the raw PermissionScope escape hatch, not a structured PermissionGrant --
    /// AER cannot verify an opaque vendor string against a category ceiling.
    /// </summary>
    [Fact]
    public void A_restrictive_ceiling_refuses_a_raw_PermissionScope_invocation_with_no_structured_grant()
    {
        var project = Path.Combine(Path.GetTempPath(), $"baton-ceiling-raw-scope-{Guid.NewGuid():N}");
        ProjectCeilingStore.Set(
            project,
            new ProjectCeiling(ReadFiles: true, WriteFiles: true, RunShellCommands: true, NetworkAccess: false),
            ProjectCeilingStore.DefaultPath);

        var ex = Assert.Throws<ProjectCeilingRequiresStructuredGrantException>(() => new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionScope: "Write", WorkingDirectory: project),
            ArchitectContract));

        Assert.Equal("architect", ex.WorkerName);
        Assert.Equal(project, ex.ProjectPath);
    }

    /// <summary>
    /// #1166 review finding C, the other untested structural refusal: a role grant that is coherent
    /// on its own (an unscoped shell alongside every other category) becomes the #529 shape once the
    /// ceiling caps WriteFiles away while leaving RunShellCommands granted -- the shell still reaches
    /// writes regardless of what the ceiling nominally withheld. This is the gate's own re-check, not
    /// WorkerBindingResolver's pre-existing bind-time one (which never sees the capped grant).
    /// </summary>
    [Fact]
    public void A_ceiling_that_makes_the_capped_grant_incoherent_refuses_rather_than_widen()
    {
        var project = Path.Combine(Path.GetTempPath(), $"baton-ceiling-incoherent-{Guid.NewGuid():N}");
        ProjectCeilingStore.Set(
            project,
            new ProjectCeiling(ReadFiles: true, WriteFiles: false, RunShellCommands: true, NetworkAccess: true),
            ProjectCeilingStore.DefaultPath);
        var roleGrant = new PermissionGrant(
            ReadFiles: true, WriteFiles: true, RunShellCommands: true, NetworkAccess: true);

        var ex = Assert.Throws<IncoherentPermissionGrantException>(() => new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: roleGrant, WorkingDirectory: project),
            ArchitectContract));

        Assert.Equal("architect", ex.WorkerName);
        Assert.Contains(nameof(PermissionGrant.WriteFiles), ex.WithheldCategories);
    }

    /// <summary>
    /// #1784: STRICT reading, operator ruling 2026-09-03. A ceiling that withholds NetworkAccess closes
    /// the category outright — even through a shell pattern the grant's own author vouches as read-only
    /// (<see cref="PermissionGrant.ShellCommandsAreReadOnly"/>). Today (pre-fix) this grant passes the
    /// gate, because <see cref="PermissionGrant.CategoriesDefeatedByTheShell(bool, IReadOnlySet{string})"/>'s read-only
    /// exemption is honored against the ceiling too; that is the bug #1784 files and the polarity
    /// partner below (no ceiling) proves the author's assertion is not itself wrong, only misapplied
    /// against an operator's outer bound.
    /// </summary>
    [Fact]
    public void A_ceiling_that_withholds_network_access_refuses_an_author_vouched_read_only_shell_pattern()
    {
        var project = Path.Combine(Path.GetTempPath(), $"baton-ceiling-readonly-network-{Guid.NewGuid():N}");
        ProjectCeilingStore.Set(
            project,
            new ProjectCeiling(ReadFiles: true, WriteFiles: true, RunShellCommands: true, NetworkAccess: false),
            ProjectCeilingStore.DefaultPath);
        var roleGrant = new PermissionGrant(
            ReadFiles: true,
            WriteFiles: true,
            RunShellCommands: true,
            ShellCommandPatterns: ["gh pr view*"],
            ShellCommandsAreReadOnly: true);

        var ex = Assert.Throws<IncoherentPermissionGrantException>(() => new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: roleGrant, WorkingDirectory: project),
            ArchitectContract));

        Assert.Equal("architect", ex.WorkerName);
        Assert.Contains(nameof(PermissionGrant.NetworkAccess), ex.WithheldCategories);
        Assert.DoesNotContain(nameof(PermissionGrant.WriteFiles), ex.WithheldCategories);
    }

    /// <summary>
    /// #1784 polarity partner: the same grant with no restrictive ceiling recorded (an unrestricted
    /// ceiling caps nothing) stays coherent — <see cref="PermissionGrant.ShellCommandsAreReadOnly"/>
    /// still answers the AUTHOR's own coherence question correctly on its own; only an operator ceiling
    /// changes the answer.
    /// </summary>
    [Fact]
    public void The_same_author_vouched_read_only_shell_pattern_stays_coherent_with_no_restrictive_ceiling()
    {
        var project = Path.Combine(Path.GetTempPath(), $"baton-ceiling-readonly-network-none-{Guid.NewGuid():N}");
        ProjectCeilingStore.Set(project, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);
        var roleGrant = new PermissionGrant(
            ReadFiles: true,
            WriteFiles: true,
            RunShellCommands: true,
            ShellCommandPatterns: ["gh pr view*"],
            ShellCommandsAreReadOnly: true);

        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: roleGrant, WorkingDirectory: project),
            ArchitectContract);

        Assert.NotNull(target);
    }

    /// <summary>#1784: same shape, WriteFiles closed by the ceiling instead of NetworkAccess.</summary>
    [Fact]
    public void A_ceiling_that_withholds_write_files_refuses_an_author_vouched_read_only_shell_pattern()
    {
        var project = Path.Combine(Path.GetTempPath(), $"baton-ceiling-readonly-write-{Guid.NewGuid():N}");
        ProjectCeilingStore.Set(
            project,
            new ProjectCeiling(ReadFiles: true, WriteFiles: false, RunShellCommands: true, NetworkAccess: true),
            ProjectCeilingStore.DefaultPath);
        var roleGrant = new PermissionGrant(
            ReadFiles: true,
            WriteFiles: true,
            RunShellCommands: true,
            ShellCommandPatterns: ["gh pr view*"],
            ShellCommandsAreReadOnly: true);

        var ex = Assert.Throws<IncoherentPermissionGrantException>(() => new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: roleGrant, WorkingDirectory: project),
            ArchitectContract));

        Assert.Equal("architect", ex.WorkerName);
        Assert.Contains(nameof(PermissionGrant.WriteFiles), ex.WithheldCategories);
        Assert.DoesNotContain(nameof(PermissionGrant.NetworkAccess), ex.WithheldCategories);
    }

    /// <summary>
    /// #1784 second-reader finding 1's exact repro, shaped after the built-in `review` role
    /// (WorkerRoles.json: WriteFiles and NetworkAccess both unset, a scoped shell asserted read-only
    /// via <see cref="PermissionGrant.ShellCommandsAreReadOnly"/>). A ceiling closing only WriteFiles
    /// correctly refuses that one category; before the fix it named NetworkAccess too, purely because
    /// this shape leaves it unset on the grant regardless of what the ceiling permits.
    /// </summary>
    [Fact]
    public void A_review_shaped_role_is_refused_for_write_files_only_when_the_ceiling_closes_it()
    {
        var project = Path.Combine(Path.GetTempPath(), $"baton-ceiling-review-shape-open-network-{Guid.NewGuid():N}");
        ProjectCeilingStore.Set(
            project,
            new ProjectCeiling(ReadFiles: true, WriteFiles: false, RunShellCommands: true, NetworkAccess: true),
            ProjectCeilingStore.DefaultPath);
        var reviewShapedGrant = new PermissionGrant(
            ReadFiles: true,
            WriteFiles: false,
            RunShellCommands: true,
            ShellCommandPatterns: ["gh pr view*"],
            NetworkAccess: false,
            ShellCommandsAreReadOnly: true);

        var ex = Assert.Throws<IncoherentPermissionGrantException>(() => new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: reviewShapedGrant, WorkingDirectory: project),
            ArchitectContract));

        Assert.Equal("architect", ex.WorkerName);
        Assert.Equal([nameof(PermissionGrant.WriteFiles)], ex.WithheldCategories);
    }

    /// <summary>
    /// Mirror of the case above with the closed and open categories swapped: refused for NetworkAccess
    /// only, not WriteFiles, even though the grant leaves WriteFiles unset too.
    /// </summary>
    [Fact]
    public void A_review_shaped_role_is_refused_for_network_access_only_when_the_ceiling_closes_it()
    {
        var project = Path.Combine(Path.GetTempPath(), $"baton-ceiling-review-shape-closed-network-{Guid.NewGuid():N}");
        ProjectCeilingStore.Set(
            project,
            new ProjectCeiling(ReadFiles: true, WriteFiles: true, RunShellCommands: true, NetworkAccess: false),
            ProjectCeilingStore.DefaultPath);
        var reviewShapedGrant = new PermissionGrant(
            ReadFiles: true,
            WriteFiles: false,
            RunShellCommands: true,
            ShellCommandPatterns: ["gh pr view*"],
            NetworkAccess: false,
            ShellCommandsAreReadOnly: true);

        var ex = Assert.Throws<IncoherentPermissionGrantException>(() => new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: reviewShapedGrant, WorkingDirectory: project),
            ArchitectContract));

        Assert.Equal("architect", ex.WorkerName);
        Assert.Equal([nameof(PermissionGrant.NetworkAccess)], ex.WithheldCategories);
    }

    private sealed class TestTimeProvider(DateTimeOffset utcNow, TimeZoneInfo? localTimeZone = null) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;

        public override TimeZoneInfo LocalTimeZone => localTimeZone ?? TimeZoneInfo.Utc;
    }

    // #1609: fixtures synthesized from the CLI bundle's minified strings (2026-09-03 issue comment,
    // Claude Code 2.1.258), not a live capture. `quotaLimits`'s placement (sibling of the stream-json
    // `message` object vs nested under it) is the open question a real capture must confirm -- both
    // are checked, and both are exercised below.
    private static string[] RateLimitFixtureLines() =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "claude-rate-limit.bundle-derived.jsonl"));

    [Fact]
    public void RateLimit_QuotaLimitsSiblingOfMessage_ParsesResetsAt()
    {
        var line = RateLimitFixtureLines()[0];
        var testTime = new TestTimeProvider(DateTimeOffset.UtcNow);

        var adapter = new ClaudeWorkerAdapter();
        var classified = adapter.TryClassifyFailure(line, testTime, out var classification, out var retryNotBefore);

        Assert.True(classified);
        Assert.Equal(FailureClassification.ExhaustedUntil, classification);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1893456000), retryNotBefore);

        Assert.True(ClaudeWorkerAdapter.TryClassifyQuotaExhaustion(
            line, testTime, out _, out _, out var placement));
        Assert.Equal("quotaLimits@root", placement);
    }

    [Fact]
    public void RateLimit_QuotaLimitsNestedUnderMessage_ParsesResetsAt()
    {
        // No "resets 3am" suffix on this line (#1810 review): the only way to land on the epoch
        // instant is reading quotaLimits nested under "message", proving that path actually fires
        // rather than falling through to the text-suffix fallback.
        var line = RateLimitFixtureLines()[1];
        var testTime = new TestTimeProvider(DateTimeOffset.UtcNow);

        var adapter = new ClaudeWorkerAdapter();
        var classified = adapter.TryClassifyFailure(line, testTime, out var classification, out var retryNotBefore);

        Assert.True(classified);
        Assert.Equal(FailureClassification.ExhaustedUntil, classification);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1893456000), retryNotBefore);

        Assert.True(ClaudeWorkerAdapter.TryClassifyQuotaExhaustion(
            line, testTime, out _, out _, out var placement));
        Assert.Equal("quotaLimits@message", placement);
    }

    [Fact]
    public void RateLimit_QuotaLimitsNestedUnderMessage_TypedValueWinsOverDisagreeingSuffix()
    {
        // Mirror of the line above: quotaLimits nested under "message" AND a "resets 3am" suffix that
        // disagrees with the typed epoch -- the typed value must win (#1810 review).
        var line = RateLimitFixtureLines()[2];
        var testTime = new TestTimeProvider(DateTimeOffset.UtcNow);

        var adapter = new ClaudeWorkerAdapter();
        var classified = adapter.TryClassifyFailure(line, testTime, out var classification, out var retryNotBefore);

        Assert.True(classified);
        Assert.Equal(FailureClassification.ExhaustedUntil, classification);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1893456000), retryNotBefore);

        Assert.True(ClaudeWorkerAdapter.TryClassifyQuotaExhaustion(
            line, testTime, out _, out _, out var placement));
        Assert.Equal("quotaLimits@message", placement);
    }

    [Fact]
    public void RateLimit_NoQuotaLimits_FallsBackToResetSuffixInContentText()
    {
        var line = RateLimitFixtureLines()[3];

        // 1am UTC (the fixed local zone below), strictly before the fixture's "resets 3am" -- so the
        // expected reset instant stays today, not tomorrow.
        var now = new DateTimeOffset(2026, 9, 4, 1, 0, 0, TimeSpan.Zero);
        var testTime = new TestTimeProvider(now, TimeZoneInfo.Utc);

        var adapter = new ClaudeWorkerAdapter();
        var classified = adapter.TryClassifyFailure(line, testTime, out var classification, out var retryNotBefore);

        Assert.True(classified);
        Assert.Equal(FailureClassification.ExhaustedUntil, classification);
        Assert.Equal(new DateTimeOffset(2026, 9, 4, 3, 0, 0, TimeSpan.Zero), retryNotBefore);

        Assert.True(ClaudeWorkerAdapter.TryClassifyQuotaExhaustion(
            line, testTime, out _, out _, out var placement));
        Assert.Equal("text-suffix", placement);
    }

    [Fact]
    public void RateLimit_NoQuotaLimits_ResetSuffixAlreadyPassedToday_RollsToTomorrow()
    {
        var line = RateLimitFixtureLines()[3];

        // 5am UTC is already past the fixture's "resets 3am" -- expect it to roll to tomorrow.
        var now = new DateTimeOffset(2026, 9, 4, 5, 0, 0, TimeSpan.Zero);
        var testTime = new TestTimeProvider(now, TimeZoneInfo.Utc);

        var adapter = new ClaudeWorkerAdapter();
        var classified = adapter.TryClassifyFailure(line, testTime, out var classification, out var retryNotBefore);

        Assert.True(classified);
        Assert.Equal(FailureClassification.ExhaustedUntil, classification);
        Assert.Equal(new DateTimeOffset(2026, 9, 5, 3, 0, 0, TimeSpan.Zero), retryNotBefore);
    }

    [Fact]
    public void RateLimit_CreditsRequiredInQuotaLimitsErrorCode_ClassifiesWithNoResetInstant()
    {
        var line = RateLimitFixtureLines()[4];
        var testTime = new TestTimeProvider(DateTimeOffset.UtcNow);

        var adapter = new ClaudeWorkerAdapter();
        var classified = adapter.TryClassifyFailure(line, testTime, out var classification, out var retryNotBefore);

        Assert.True(classified);
        Assert.Equal(FailureClassification.ExhaustedUntil, classification);
        Assert.Null(retryNotBefore);
    }

    [Fact]
    public void RateLimit_NothingParseable_StaysUnclassified()
    {
        var line = RateLimitFixtureLines()[5];
        var testTime = new TestTimeProvider(DateTimeOffset.UtcNow);

        var adapter = new ClaudeWorkerAdapter();
        var classified = adapter.TryClassifyFailure(line, testTime, out var classification, out var retryNotBefore);

        Assert.False(classified);
        Assert.Null(classification);
        Assert.Null(retryNotBefore);
    }

    // #1857: captured 2026-09-04 09:25 ET (13:25Z), claude 2.1.258 -- the weekly-limit wall's
    // terminal `result` event, not the synthetic `assistant`-line envelope the fixtures above cover.
    private static string[] WeeklyLimitResultFixtureLines() =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "claude-weekly-limit-result.captured.jsonl"));

    [Fact]
    public void WeeklyLimit_ResultEventWith429_ClassifiesExhaustedUntilWithNamedZoneInstant()
    {
        var line = WeeklyLimitResultFixtureLines()[0];
        var testTime = new TestTimeProvider(new DateTimeOffset(2026, 9, 4, 13, 25, 0, TimeSpan.Zero));

        var adapter = new ClaudeWorkerAdapter();
        var classified = adapter.TryClassifyFailure(line, testTime, out var classification, out var retryNotBefore);

        Assert.True(classified);
        Assert.Equal(FailureClassification.ExhaustedUntil, classification);
        // 2026-09-07T06:00 America/New_York is EDT (UTC-4) in September -> 10:00Z.
        Assert.Equal(new DateTimeOffset(2026, 9, 7, 10, 0, 0, TimeSpan.Zero), retryNotBefore);

        Assert.True(ClaudeWorkerAdapter.TryClassifyQuotaExhaustion(
            line, testTime, out _, out _, out var placement));
        Assert.Equal("result", placement);
    }

    [Fact]
    public void WeeklyLimit_ResultEventErrorWithout429_StaysAPlainFailure()
    {
        // Polarity control: `is_error: true` alone must not be enough -- only `api_error_status: 429`
        // makes this a rate-limit envelope, so a differently-shaped error result stays unclassified.
        var line = WeeklyLimitResultFixtureLines()[1];
        var testTime = new TestTimeProvider(new DateTimeOffset(2026, 9, 4, 13, 25, 0, TimeSpan.Zero));

        var adapter = new ClaudeWorkerAdapter();
        var classified = adapter.TryClassifyFailure(line, testTime, out var classification, out var retryNotBefore);

        Assert.False(classified);
        Assert.Null(classification);
        Assert.Null(retryNotBefore);
    }

    [Fact]
    public void WeeklyLimit_BareClockTimeSuffix_StillParsesUnchanged()
    {
        // #1810's fixture (bare "resets 3am", no date/zone) still parses via the shared text-suffix
        // path after the #1857 refactor pulled per-block parsing into TryParseResetSuffixFromText.
        var line = RateLimitFixtureLines()[3];
        var now = new DateTimeOffset(2026, 9, 4, 1, 0, 0, TimeSpan.Zero);
        var testTime = new TestTimeProvider(now, TimeZoneInfo.Utc);

        var adapter = new ClaudeWorkerAdapter();
        var classified = adapter.TryClassifyFailure(line, testTime, out var classification, out var retryNotBefore);

        Assert.True(classified);
        Assert.Equal(FailureClassification.ExhaustedUntil, classification);
        Assert.Equal(new DateTimeOffset(2026, 9, 4, 3, 0, 0, TimeSpan.Zero), retryNotBefore);
    }

    [Fact]
    public void WeeklyLimit_ResultEventWith429ButNoResetSuffix_ParksWithUnknownReset()
    {
        // #1860 review: the 429 result envelope is still a vendor wall when its text names no reset
        // instant -- #1609's unknown-reset park (ExhaustedUntil, RetryNotBefore null), never a plain
        // failure. The instant is what is unknown, not the classification.
        var line = "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":true,\"api_error_status\":429," +
            "\"result\":\"You've hit your weekly limit.\"}";
        var testTime = new TestTimeProvider(new DateTimeOffset(2026, 9, 4, 13, 25, 0, TimeSpan.Zero));

        var adapter = new ClaudeWorkerAdapter();
        var classified = adapter.TryClassifyFailure(line, testTime, out var classification, out var retryNotBefore);

        Assert.True(classified);
        Assert.Equal(FailureClassification.ExhaustedUntil, classification);
        Assert.Null(retryNotBefore);
    }

    [Fact]
    public void WeeklyLimit_Feb29ResetRollingIntoANonLeapYear_ClampsInsteadOfThrowing()
    {
        // #1860 review (low): "resets Feb 29" read after Feb 29 of a leap year must roll to the next
        // year, which has no Feb 29 -- clamp to Feb 28 rather than throw and lose the park.
        var line = "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":true,\"api_error_status\":429," +
            "\"result\":\"You've hit your weekly limit · resets Feb 29, 6am (Etc/UTC)\"}";
        var testTime = new TestTimeProvider(new DateTimeOffset(2028, 3, 1, 0, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc);

        var adapter = new ClaudeWorkerAdapter();
        var classified = adapter.TryClassifyFailure(line, testTime, out var classification, out var retryNotBefore);

        Assert.True(classified);
        Assert.Equal(FailureClassification.ExhaustedUntil, classification);
        Assert.Equal(new DateTimeOffset(2029, 2, 28, 6, 0, 0, TimeSpan.Zero), retryNotBefore);
    }

    [Fact]
    public void WeeklyLimit_UnknownZoneId_FallsBackToLocalZoneInsteadOfNull()
    {
        var line = "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":true,\"api_error_status\":429," +
            "\"result\":\"You've hit your weekly limit · resets Sep 7, 6am (Nowhere/Fake)\"}";
        var localZone = TimeZoneInfo.CreateCustomTimeZone("fixed-utc-plus-2", TimeSpan.FromHours(2), "fixed-utc-plus-2", "fixed-utc-plus-2");
        var testTime = new TestTimeProvider(new DateTimeOffset(2026, 9, 4, 13, 25, 0, TimeSpan.Zero), localZone);

        var adapter = new ClaudeWorkerAdapter();
        var classified = adapter.TryClassifyFailure(line, testTime, out var classification, out var retryNotBefore);

        Assert.True(classified);
        Assert.Equal(FailureClassification.ExhaustedUntil, classification);
        Assert.NotNull(retryNotBefore);
        // Fell back to the fixed +02:00 local zone: 2026-09-07T06:00+02:00 -> 04:00Z.
        Assert.Equal(new DateTimeOffset(2026, 9, 7, 4, 0, 0, TimeSpan.Zero), retryNotBefore);
    }

    // ---- #532: resolve-time hook liveness probe ----

    /// <summary>Deterministic test double -- see <see cref="IClaudeHookLivenessProbe"/>'s own remarks.</summary>
    private sealed class FakeClaudeHookLivenessProbe : IClaudeHookLivenessProbe
    {
        private readonly ClaudeHookLivenessResult _result;
        public int CallCount { get; private set; }

        public FakeClaudeHookLivenessProbe(ClaudeHookLivenessResult result) => _result = result;

        public ClaudeHookLivenessResult Probe(string hookAssemblyPath, TimeSpan timeout)
        {
            CallCount++;
            return _result;
        }
    }

    [Fact]
    public void A_missing_hook_refuses_dispatch_when_the_probe_reports_dead()
    {
        var probe = new FakeClaudeHookLivenessProbe(
            new ClaudeHookLivenessResult(false, "'C:/does/not/exist/Baton.Cli.dll' does not exist"));
        var adapter = new ClaudeWorkerAdapter(probe);

        var ex = Assert.Throws<ClaudeHookUnverifiedException>(
            () => adapter.Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract));

        Assert.Equal(1, probe.CallCount);
        Assert.Contains("does not exist", ex.Message);
        Assert.Contains("PreToolUse hook", ex.Message);
        Assert.Contains(ex.HookAssemblyPath, ex.Message);
    }

    [Theory]
    [InlineData("timed out")]
    [InlineData("the hook exited 0 instead of the deny code (2)")]
    [InlineData("the hook process could not be run: access denied")]
    public void A_tampered_or_unresponsive_hook_refuses_dispatch(string detail)
    {
        var probe = new FakeClaudeHookLivenessProbe(new ClaudeHookLivenessResult(false, detail));
        var adapter = new ClaudeWorkerAdapter(probe);

        var ex = Assert.Throws<ClaudeHookUnverifiedException>(
            () => adapter.Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract));

        Assert.Contains(detail, ex.Message);
    }

    [Fact]
    public void A_live_probe_lets_dispatch_proceed_normally()
    {
        var probe = new FakeClaudeHookLivenessProbe(new ClaudeHookLivenessResult(true, "deny"));
        var adapter = new ClaudeWorkerAdapter(probe);

        var target = adapter.Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        Assert.Equal(1, probe.CallCount);
        Assert.Equal("claude", target.Program);
    }

    [Fact]
    public void ProcessClaudeHookLivenessProbe_reports_dead_for_a_nonexistent_hook_path_without_spawning_a_process()
    {
        // No subprocess is spawned here at all -- File.Exists short-circuits first.
        var probe = new ProcessClaudeHookLivenessProbe();

        var result = probe.Probe(@"C:\definitely\does\not\exist\Baton.Cli.dll", TimeSpan.FromSeconds(1));

        Assert.False(result.IsLive);
        Assert.Contains("does not exist", result.Detail);
    }

    [Fact]
    public void ProcessClaudeHookLivenessProbe_reports_live_against_the_real_built_binary()
    {
        // The single load-bearing claim of the whole probe -- "with BATON_HOOK_DENIED_TOOLS set to a
        // withheld Write, dotnet <the real shipped dll> hook-check exits with the deny code" --
        // executed for real. The cache is reset first and the spawn counter asserted after, so this
        // test cannot be served from an entry some earlier test warmed for the same assembly path.
        ProcessClaudeHookLivenessProbe.ResetCacheForTesting();
        var assemblyPath = Path.Combine(AppContext.BaseDirectory, "Baton.Cli.dll");
        var probe = new ProcessClaudeHookLivenessProbe();

        var result = probe.Probe(assemblyPath, TimeSpan.FromSeconds(30));

        Assert.True(result.IsLive, $"expected the real hook to answer deny; got: {result.Detail}");
        Assert.Equal(1, ProcessClaudeHookLivenessProbe.SpawnCountForTesting);
    }

    [Fact]
    public void A_second_resolve_of_the_same_live_path_reuses_the_first_probe_instead_of_spawning_again()
    {
        ProcessClaudeHookLivenessProbe.ResetCacheForTesting();
        var assemblyPath = Path.Combine(AppContext.BaseDirectory, "Baton.Cli.dll");
        var probe = new ProcessClaudeHookLivenessProbe();

        var first = probe.Probe(assemblyPath, TimeSpan.FromSeconds(30));
        Assert.True(first.IsLive, $"expected the real hook to answer deny; got: {first.Detail}");
        var afterFirst = ProcessClaudeHookLivenessProbe.SpawnCountForTesting;
        Assert.Equal(1, afterFirst);

        var second = probe.Probe(assemblyPath, TimeSpan.FromSeconds(30));

        Assert.Equal(first, second);
        Assert.Equal(afterFirst, ProcessClaudeHookLivenessProbe.SpawnCountForTesting);
    }
}
