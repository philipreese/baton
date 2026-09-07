using System.Diagnostics;
using System.Text.Json;
using Baton.Artifacts;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Outcomes;
using Baton.Status;

namespace Baton.Vendors.Tests;

/// <summary>
/// M20 Phase 4's deliverable: unit tests for the refactored, direct shell-less
/// <see cref="AgyWorkerAdapter"/> resolving.
/// </summary>
[Collection(LaunchConfigCollection.Name)]
public class AgyWorkerAdapterTests
{
    private static readonly WorkerContract ArchitectContract = new(
        "architect", ["goal"], [new ProducedOutput("plan.md")], []);

    private static string GetPrompt(CoreDispatchTarget target) => target.Args[1];

    [Fact]
    public void Resolves_to_direct_agy_execution_without_shell_wrapper()
    {
        var target = new AgyWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        Assert.Equal("agy", target.Program);
        Assert.Equal("-p", target.Args[0]);
        Assert.Equal("--mode", target.Args[2]);
        Assert.Equal("accept-edits", target.Args[3]);
        Assert.Equal("--add-dir", target.Args[4]);

        const string artifactsRootVar = "%BATON_ARTIFACTS_ROOT%";
        Assert.Equal(artifactsRootVar, target.Args[5]);
    }

    /// <summary>
    /// M23 Phase 3 (#272): WorkingDirectory carries no vendor-specific meaning — every adapter forwards
    /// it into CoreDispatchTarget unchanged. For <c>agy</c> that is necessary and <b>not sufficient</b>;
    /// see <see cref="The_rooms_directory_is_bound_with_add_dir_because_agy_ignores_the_process_cwd"/>.
    /// </summary>
    [Fact]
    public void A_configured_WorkingDirectory_is_forwarded_into_the_resolved_target()
    {
        // #1166: same reason as ClaudeWorkerAdapterTests's identically-named test -- trust the fixture
        // path first so this test's own concern (forwarding) is what decides the outcome, not the gate.
        ProjectCeilingStore.Set("/home/user/my-project", ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);

        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", WorkingDirectory: "/home/user/my-project"), ArchitectContract);

        Assert.Equal("/home/user/my-project", target.WorkingDirectory);
    }

    /// <summary>
    /// #491: <c>agy -p</c> <b>ignores the process working directory</b>, so setting it on the dispatch
    /// target does not point the worker at the room's folder. Measured in #472 and recorded in
    /// <c>docs/vendor-capabilities.md</c>: launched from a directory listed in the CLI's own
    /// <c>trustedWorkspaces</c>, the emitted command still carried the CLI's install path as
    /// <c>Cwd</c>; from an untrusted directory it used the CLI's scratch dir and began a recursive
    /// search of the home folder looking for a file in the launch directory.
    /// </summary>
    /// <remarks>
    /// The failure this guards is silent rather than loud — a worker that cannot see the project does
    /// not error, it answers confidently about the wrong directory — and J11 (two subscriptions in one
    /// room) is a human-attested journey, so nothing automated would have caught it.
    /// </remarks>
    [Fact]
    public void Resolve_sets_OversizePromptWrapper_referencing_BATON_PROMPT_FILE()
    {
        var target = new AgyWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);
        Assert.NotNull(target.OversizePromptWrapper);
        Assert.Contains("%BATON_PROMPT_FILE%", target.OversizePromptWrapper);
    }

    /// <summary>
    /// #1088: under StreamJson, agy streams with its OWN grammar — `--output-format stream-json`, and
    /// NEVER claude's `--verbose`. <see cref="AgyWorkerAdapter"/>'s Resolve comment owns why.
    /// </summary>
    [Fact]
    public void StreamJson_emits_agy_output_format_stream_json_and_never_claude_verbose()
    {
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", StreamJson: true), ArchitectContract);

        Assert.Contains(
            target.Args.Zip(target.Args.Skip(1)),
            pair => pair.First == "--output-format" && pair.Second == "stream-json");
        Assert.DoesNotContain("--verbose", target.Args);
    }

    [Fact]
    public void Without_StreamJson_no_output_format_flag_is_emitted()
    {
        var target = new AgyWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        Assert.DoesNotContain("--output-format", target.Args);
        Assert.DoesNotContain("stream-json", target.Args);
    }

    /// <summary>
    /// #1089: under StreamJson the target carries a terminal-success detector that recognises agy's
    /// SUCCESS `result` event and nothing else; in text mode there is no such event, so it is null and
    /// the classifier's timeout guard fails safe.
    /// </summary>
    [Fact]
    public void StreamJson_wires_a_terminal_success_detector_that_recognises_only_agys_success_result()
    {
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", StreamJson: true), ArchitectContract);

        Assert.NotNull(target.DetectsTerminalSuccess);
        Assert.True(target.DetectsTerminalSuccess!(
            """{"event":"result","result":{"status":"SUCCESS","response":"done","usage":{"total_tokens":5}}}"""));
        Assert.False(target.DetectsTerminalSuccess!("""{"event":"result","result":{"status":"ERROR"}}"""));
        Assert.False(target.DetectsTerminalSuccess!("""{"event":"step_update","step_update":{"state":"DONE","step_type":"tool"}}"""));
        Assert.False(target.DetectsTerminalSuccess!("not json"));
    }

    [Fact]
    public void Without_StreamJson_no_terminal_success_detector_is_wired()
    {
        var target = new AgyWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        Assert.Null(target.DetectsTerminalSuccess);
    }

    /// <summary>
    /// N5/F6 (#1664 re-review): <see cref="AgyWorkerAdapter.IsTerminalResultLine"/> had zero test
    /// coverage — this is the single fact <c>OutcomeClassifier</c>'s dead-worker predicate now keys
    /// on (`TerminalResultObserved`), so a regression here silently reclassifies a self-reported
    /// FAILURE result as a dead worker again. Mirrors
    /// <see cref="StreamJson_wires_a_terminal_success_detector_that_recognises_only_agys_success_result"/>'s
    /// shape, but asserts the wider match: a `result` event of ANY status is terminal, unlike
    /// <see cref="AgyWorkerAdapter.IsTerminalSuccessLine"/> which only matches SUCCESS.
    /// </summary>
    [Fact]
    public void StreamJson_wires_a_terminal_result_detector_that_recognises_any_result_status()
    {
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", StreamJson: true), ArchitectContract);

        Assert.NotNull(target.DetectsTerminalResult);
        Assert.True(target.DetectsTerminalResult!(
            """{"event":"result","result":{"status":"SUCCESS","response":"done","usage":{"total_tokens":5}}}"""));
        // The polarity F6 exists for: a self-reported FAILURE is still a terminal RESULT, unlike
        // DetectsTerminalSuccess above, which reads false for the identical line.
        Assert.True(target.DetectsTerminalResult!("""{"event":"result","result":{"status":"FAILURE","is_error":true}}"""));
        Assert.False(target.DetectsTerminalResult!("""{"event":"step_update","step_update":{"state":"DONE","step_type":"tool"}}"""));
        Assert.False(target.DetectsTerminalResult!("not json"));
    }

    [Fact]
    public void Without_StreamJson_no_terminal_result_detector_is_wired()
    {
        var target = new AgyWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        Assert.Null(target.DetectsTerminalResult);
    }

    [Fact]
    public void The_rooms_directory_is_bound_with_add_dir_because_agy_ignores_the_process_cwd()
    {
        // #1166: see A_configured_WorkingDirectory_is_forwarded_into_the_resolved_target above -- Set
        // is idempotent, so re-trusting the same fixture path here does not depend on that test's
        // ordering relative to this one.
        ProjectCeilingStore.Set("/home/user/my-project", ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);

        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", WorkingDirectory: "/home/user/my-project"), ArchitectContract);

        var addDirValues = target.Args
            .Select((arg, i) => (arg, i))
            .Where(pair => pair.arg == "--add-dir")
            .Select(pair => target.Args[pair.i + 1])
            .ToList();

        Assert.Contains("/home/user/my-project", addDirValues);

        // Composes with the artifacts root rather than replacing it — --add-dir is repeatable on agy,
        // and the worker needs both its outputs and the project it is reasoning about.
        const string artifactsRootVar = "%BATON_ARTIFACTS_ROOT%";
        Assert.Contains(artifactsRootVar, addDirValues);
    }

    /// <summary>A directory-less room (#407's neutral-scratch case) must not emit an empty --add-dir.</summary>
    /// <remarks>
    /// <para>
    /// Rewritten twice by #554. It originally counted <c>--add-dir</c> occurrences as a proxy for "no
    /// empty value was emitted", which broke when the gate workspace added a second one. The first
    /// rewrite asserted only that no value was blank — and an independent reviewer showed that was
    /// weaker than the original in a way that mattered: changing the adapter to
    /// <c>invocation.WorkingDirectory ?? Directory.GetCurrentDirectory()</c> would still pass, while
    /// regressing #407 by binding the daemon's own cwd as the worker's workspace.
    /// </para>
    /// <para>
    /// So it now pins the <b>exact set</b>. A future third <c>--add-dir</c> on a directory-less room
    /// has to come through this test deliberately — which is the test doing its job, not failing for
    /// the wrong reason.
    /// </para>
    /// </remarks>
    [Fact]
    public void No_directory_add_dir_is_emitted_when_the_room_has_no_working_directory()
    {
        var target = new AgyWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        var addDirValues = target.Args
            .Select((arg, i) => (arg, i))
            .Where(pair => pair.arg == "--add-dir")
            .Select(pair => target.Args[pair.i + 1])
            .ToList();

        const string artifactsRootVar = "%BATON_ARTIFACTS_ROOT%";

        Assert.Equal(2, addDirValues.Count);
        Assert.Equal(artifactsRootVar, addDirValues[0]);
        Assert.EndsWith(AgyWorkerAdapter.AgyWorkspaceDirectoryName, addDirValues[1], StringComparison.Ordinal);
    }

    [Fact]
    public void An_explicit_permission_scope_overrides_the_default()
    {
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionScope: "yolo"), ArchitectContract);

        Assert.Equal("yolo", target.Args[3]);
    }

    // #588: agy -p has its own 5-minute print-mode wait, decoupled from anything AER configures, so
    // a long task under a 20-minute AER timeout died at 5 minutes with exit 0 and no output.

    [Fact]
    public void Resolve_passes_print_timeout_derived_from_the_invocations_own_timeout()
    {
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", Timeout: TimeSpan.FromMinutes(20)), ArchitectContract);

        // 20 minutes + the 60s margin, as whole seconds.
        Assert.Equal("1260s", ArgValue(target, "--print-timeout"));
    }

    /// <summary>
    /// The polarity control. Without it, an adapter that emitted a hardcoded <c>--print-timeout</c>
    /// regardless of the invocation would pass the test above — and would then be overriding the
    /// vendor default in cases where AER has no timeout to declare.
    /// </summary>
    [Fact]
    public void Resolve_omits_print_timeout_entirely_when_the_invocation_declares_no_timeout()
    {
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", Timeout: null), ArchitectContract);

        Assert.DoesNotContain("--print-timeout", target.Args);
    }

    /// <summary>
    /// agy's limit must expire strictly after AER's, never at the same moment. Whichever fires first
    /// decides the failure mode, and they are not equally good: AER's yields
    /// <c>CoreExitReason.TimedOut</c> and a real diagnostic, agy's yields a clean exit 0 with no
    /// output — the silent failure this issue was filed for. Equality would make that a race.
    /// </summary>
    [Theory]
    [InlineData(30)]
    [InlineData(300)]
    [InlineData(1200)]
    public void The_print_timeout_always_expires_strictly_after_AERs_own_timeout(int batonTimeoutSeconds)
    {
        var batonTimeout = TimeSpan.FromSeconds(batonTimeoutSeconds);
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", Timeout: batonTimeout), ArchitectContract);

        var emitted = ArgValue(target, "--print-timeout");
        Assert.NotNull(emitted);

        var emittedSeconds = int.Parse(emitted.TrimEnd('s'), System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(
            emittedSeconds > batonTimeoutSeconds,
            $"print-timeout {emittedSeconds}s must exceed AER's own {batonTimeoutSeconds}s, or agy can give up first");
    }

    /// <summary>
    /// Guards the exact formatting trap this was measured into. <c>agy</c> parses Go durations:
    /// <c>1200s</c>, <c>20m0s</c> and <c>20m</c> are accepted, but <c>00:20:00</c> — which is
    /// precisely what <see cref="TimeSpan.ToString()"/> produces — is rejected with
    /// <c>time: unknown unit ":" in duration</c> and exit code 2. Interpolating the TimeSpan directly
    /// would have broken every gemini dispatch outright.
    /// </summary>
    [Theory]
    [InlineData(30)]
    [InlineData(1200)]
    [InlineData(7200)]
    public void The_print_timeout_is_a_Go_duration_never_a_dotnet_TimeSpan_rendering(int seconds)
    {
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", Timeout: TimeSpan.FromSeconds(seconds)), ArchitectContract);

        var emitted = ArgValue(target, "--print-timeout");
        Assert.NotNull(emitted);
        Assert.Matches(@"^\d+s$", emitted);
        Assert.DoesNotMatch(@"^\d{2}:\d{2}:\d{2}", emitted);
    }

    /// <summary>
    /// A fractional duration must round up, never down: rounding down would emit a backstop fractionally
    /// tighter than intended, which is the direction that reintroduces the race. Zero is floored to a
    /// value the flag will actually parse.
    /// </summary>
    [Theory]
    [InlineData(0.5, "61s")]
    [InlineData(90.4, "151s")]
    [InlineData(0, "60s")]
    public void The_print_timeout_rounds_up_and_never_emits_a_non_positive_duration(
        double seconds, string expected)
    {
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", Timeout: TimeSpan.FromSeconds(seconds)), ArchitectContract);

        Assert.Equal(expected, ArgValue(target, "--print-timeout"));
    }

    [Fact]
    public void A_negative_timeout_still_yields_a_duration_agys_parser_accepts()
    {
        // The first version of this comment claimed a negative timeout was "a config error AER's own
        // timeout would reject first". Nothing rejected it: WorkerBindingConfigParser validated
        // Adapter, Contract, PromptTemplate and WorkingDirectory and never Timeout, so the value went
        // straight through to BatonTask.WithTimeout. A Timeout > TimeSpan.Zero check now exists there
        // (WorkerBindingConfigParser.Parse) and is what makes this unreachable in practice.
        //
        // The floor stays regardless, because it guards a different thing: an unparseable flag value
        // fails the whole dispatch at argument parsing with exit 2, which is a worse failure than the
        // one being fixed. This asserts the rendering stays parseable even for input the parser should
        // now never hand over.
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", Timeout: TimeSpan.FromSeconds(-9999)), ArchitectContract);

        Assert.Matches(@"^\d+s$", ArgValue(target, "--print-timeout"));
    }

    /// <summary>
    /// Adding the margin to a near-maximum <see cref="TimeSpan"/> overflows, and
    /// <see cref="TimeSpan"/> addition throws on overflow rather than saturating. A binding config is
    /// operator-authored and any parseable TimeSpan is accepted, so this is reachable — and it would
    /// throw out of binding <i>resolution</i>, taking down every worker in the file rather than the
    /// one with the silly value.
    /// </summary>
    [Fact]
    public void An_enormous_timeout_does_not_overflow_while_adding_the_margin()
    {
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", Timeout: TimeSpan.MaxValue), ArchitectContract);

        Assert.Matches(@"^\d+s$", ArgValue(target, "--print-timeout"));
    }

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

    /// <remarks>
    /// De-positioned by #554: this asserted <c>Args[6]</c>/<c>Args[7]</c>, which shifted when the
    /// gate workspace added a second <c>--add-dir</c> pair. The claim was always "the model is
    /// passed through", never "it sits at index 6" — <see cref="ArgValue"/> already existed for
    /// exactly this and is what the neighbouring effort test uses.
    /// </remarks>
    [Fact]
    public void A_model_is_passed_through_when_set()
    {
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", Model: "gemini-3-pro"), ArchitectContract);

        Assert.Equal("gemini-3-pro", ArgValue(target, "--model"));
    }

    [Fact]
    public void No_model_flag_is_emitted_when_the_model_is_unset()
    {
        var target = new AgyWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        Assert.DoesNotContain("--model", target.Args);
    }

    [Fact]
    public void An_effort_is_passed_through_when_set()
    {
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", Effort: "high"), ArchitectContract);

        Assert.Equal("high", ArgValue(target, "--effort"));
    }

    [Fact]
    public void No_effort_flag_is_emitted_when_the_effort_is_unset()
    {
        var target = new AgyWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        Assert.DoesNotContain("--effort", target.Args);
    }

    [Fact]
    public void The_prompt_names_every_declared_output_and_its_env_var_path()
    {
        var contract = new WorkerContract(
            "architect", [], [new ProducedOutput("plan.md"), new ProducedOutput("summary.md")], []);

        var target = new AgyWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), contract);

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

        var target = new AgyWorkerAdapter().Resolve(new WorkerInvocation("Review the plan."), contract);

        var prompt = GetPrompt(target);
        const string inputVar0 = "%BATON_INPUT_0%";
        const string inputVar1 = "%BATON_INPUT_1%";
        Assert.Contains($"plan: {inputVar0}", prompt);
        Assert.Contains($"guidelines: {inputVar1}", prompt);
    }

    /// <summary>
    /// #1623: every agy prompt carries the foreground instruction, so a lane never re-derives the
    /// backgrounded-`run_command`/tight-`manage_task`-poll behaviour <c>docs/vendor-capabilities.md</c>'s
    /// "Sharp edges" section records against a real captured lane.
    /// </summary>
    [Fact]
    public void The_prompt_instructs_agy_to_run_commands_in_the_foreground_and_never_poll_manage_task()
    {
        var target = new AgyWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        var prompt = GetPrompt(target);
        Assert.Contains("in the foreground", prompt);
        Assert.Contains("manage_task status", prompt);
        Assert.Contains(AgyWorkerAdapter.ForegroundGateInstructionText, prompt);
    }

    [Fact]
    public void A_contract_with_no_inputs_omits_the_inputs_section()
    {
        var contract = new WorkerContract("architect", [], [new ProducedOutput("plan.md")], []);

        var target = new AgyWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), contract);

        Assert.DoesNotContain("Inputs, in the order listed", GetPrompt(target));
    }

    [Fact]
    public void Prompt_keeps_newlines_for_readability_on_all_platforms()
    {
        var contract = new WorkerContract("architect", ["goal"], [new ProducedOutput("plan.md")], []);
        var target = new AgyWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), contract);

        Assert.Contains('\n', GetPrompt(target));
    }

    [Fact]
    public void Shell_metacharacters_and_percent_signs_are_passed_raw_because_no_shell_evaluates_them()
    {
        var invocation = new WorkerInvocation("Quote this: \"$HOME\" and `whoami` and 100% path %PATH%.");

        var target = new AgyWorkerAdapter().Resolve(invocation, ArchitectContract);

        var prompt = GetPrompt(target);
        Assert.Contains("Quote this: \"$HOME\" and `whoami` and 100% path %PATH%.", prompt);
    }

    /// <summary>Issue #292: CoreDispatcher's durable prompt.txt capture reads this field, not target.Args -- it must carry the identical text the -p argument does.</summary>
    [Fact]
    public void PromptText_carries_the_same_resolved_prompt_as_the_p_argument()
    {
        var target = new AgyWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        Assert.Equal(GetPrompt(target), target.PromptText);
    }

    [Fact]
    public void Null_invocation_or_contract_throws()
    {
        var adapter = new AgyWorkerAdapter();

        Assert.Throws<ArgumentNullException>(() => adapter.Resolve(null!, ArchitectContract));
        Assert.Throws<ArgumentNullException>(() => adapter.Resolve(new WorkerInvocation("Draft a plan."), null!));
    }

    // M21 Phase 1: the structured PermissionGrant builder path. The tests above are untouched —
    // proving a hand-typed raw PermissionScope (including "yolo", a value outside the --mode
    // vocabulary the structured translator emits) still resolves identically.

    [Theory]
    [InlineData(false, false, "default")]
    [InlineData(true, false, "plan")]
    [InlineData(true, true, "accept-edits")]
    [InlineData(false, true, "accept-edits")]
    public void A_permission_grant_maps_read_write_combinations_to_the_matching_mode(
        bool readFiles, bool writeFiles, string expectedMode)
    {
        var grant = new PermissionGrant(ReadFiles: readFiles, WriteFiles: writeFiles);
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract);

        Assert.Equal(expectedMode, target.Args[3]);
    }

    [Fact]
    public void A_permission_grant_takes_precedence_over_a_raw_permission_scope_when_both_are_set()
    {
        var grant = new PermissionGrant(WriteFiles: true);
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionScope: "yolo", PermissionGrant: grant), ArchitectContract);

        Assert.Equal("accept-edits", target.Args[3]);
    }

    [Fact]
    public void Requesting_shell_commands_is_refused_rather_than_approximated()
    {
        var grant = new PermissionGrant(RunShellCommands: true);

        var ex = Assert.Throws<PermissionGrantUnsupportedException>(() => new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract));

        Assert.Equal("agy", ex.AdapterName);
    }

    [Fact]
    public void Requesting_network_access_is_refused_rather_than_approximated()
    {
        var grant = new PermissionGrant(NetworkAccess: true);

        var ex = Assert.Throws<PermissionGrantUnsupportedException>(() => new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract));

        Assert.Equal("agy", ex.AdapterName);
    }

    [Fact]
    public void A_read_only_scoped_shell_grant_now_resolves_on_agy_by_deferring_to_the_hook()
    {
        // #1456 shipped review's exact claude-side grant shape (RunShellCommands scoped by read-only
        // patterns, NetworkAccess false, ShellCommandsAreReadOnly true) and this test used to assert
        // agy refused it outright, because --dangerously-skip-permissions is all-or-nothing and
        // ShellCommandsAreReadOnly is a PermissionGrant-level coherence exemption
        // (WorkerBindingResolver's #529 check), not a claim this adapter's own translator understood.
        //
        // The hook route expresses this grant correctly end to end -- measured by #1387's second
        // probe; see spec/baton.md §9 and docs/vendor-doc-audit.md for the full table, not restated
        // here. So this shape now resolves rather than refuses.
        var grant = new PermissionGrant(
            ReadFiles: true, WriteFiles: false, RunShellCommands: true,
            ShellCommandPatterns: ["git diff*"], NetworkAccess: false, ShellCommandsAreReadOnly: true);

        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant, StreamJson: true), ArchitectContract);

        Assert.Contains("--dangerously-skip-permissions", target.Args);
    }

    [Fact]
    public void TryTranslatePermissionGrant_refuses_shell_commands_without_throwing()
    {
        var adapter = new AgyWorkerAdapter();

        var succeeded = adapter.TryTranslatePermissionGrant(
            new PermissionGrant(RunShellCommands: true), out var resolved, out var gapReason);

        Assert.False(succeeded);
        Assert.Null(resolved);
        Assert.NotNull(gapReason);
    }

    /// <summary>
    /// #1387's polarity pair, arm 1: an UNSCOPED shell grant without network still refuses -- nothing
    /// would bound <c>--dangerously-skip-permissions</c>' network-side over-grant, so this arm must stay
    /// exactly as conservative as <see cref="TryTranslatePermissionGrant_refuses_shell_commands_without_throwing"/>
    /// even when the grant is otherwise identical to arm 2 below.
    /// </summary>
    [Fact]
    public void A_shell_grant_without_network_and_without_patterns_still_refuses()
    {
        var adapter = new AgyWorkerAdapter();
        var grant = new PermissionGrant(
            ReadFiles: true, RunShellCommands: true, NetworkAccess: false, ShellCommandPatterns: null);

        var succeeded = adapter.TryTranslatePermissionGrant(grant, out var resolved, out var gapReason);

        Assert.False(succeeded);
        Assert.Null(resolved);
        Assert.NotNull(gapReason);
    }

    /// <summary>
    /// #1387's polarity pair, arm 2: a PATTERN-SCOPED shell grant without network now defers to the
    /// hook instead of refusing -- the full measured table lives in spec/baton.md §9 and
    /// docs/vendor-doc-audit.md, not restated here. The deny half of that story is
    /// <c>agy.hook-deny-honoured</c>'s own claim (a <c>PreToolUse</c> deny blocks the call); this test
    /// only pins the translation this PR changes, not the hook's own enforcement, which that
    /// sentinel already covers.
    /// </summary>
    [Fact]
    public void A_pattern_scoped_shell_grant_without_network_defers_to_the_hook_instead_of_refusing()
    {
        var adapter = new AgyWorkerAdapter();
        var grant = new PermissionGrant(
            ReadFiles: true, RunShellCommands: true, NetworkAccess: false,
            ShellCommandPatterns: ["git status*", "git log*"]);

        var succeeded = adapter.TryTranslatePermissionGrant(grant, out var resolved, out var gapReason);

        Assert.True(succeeded);
        Assert.Equal("--dangerously-skip-permissions", resolved);
        Assert.Null(gapReason);
    }

    [Fact]
    public void Requesting_shell_and_network_access_together_translates_to_dangerously_skip_permissions()
    {
        var adapter = new AgyWorkerAdapter();
        var grant = new PermissionGrant(RunShellCommands: true, NetworkAccess: true);

        var succeeded = adapter.TryTranslatePermissionGrant(grant, out var resolved, out var gapReason);

        Assert.True(succeeded);
        Assert.Equal("--dangerously-skip-permissions", resolved);
        Assert.Null(gapReason);
    }

    [Fact]
    public void A_shell_grant_narrowed_by_patterns_is_refused_rather_than_widened_to_every_command()
    {
        // #659: agy shell grants scoped by PermissionGrant.ShellCommandPatterns translate and emit
        // BATON_HOOK_SHELL_PATTERNS to be enforced by the PreToolUse hook (AgyHookCheckCommand).
        var adapter = new AgyWorkerAdapter();
        var grant = new PermissionGrant(
            ReadFiles: true, WriteFiles: true, RunShellCommands: true,
            ShellCommandPatterns: ["git *"], NetworkAccess: true);

        var succeeded = adapter.TryTranslatePermissionGrant(grant, out var resolved, out var gapReason);

        Assert.True(succeeded);
        Assert.Equal("--dangerously-skip-permissions", resolved);
        Assert.Null(gapReason);
    }

    [Fact]
    public void Resolving_a_pattern_scoped_shell_grant_emits_shell_patterns_environment_variable()
    {
        var adapter = new AgyWorkerAdapter();
        var grant = new PermissionGrant(
            ReadFiles: true, WriteFiles: true, RunShellCommands: true,
            ShellCommandPatterns: ["git *"], NetworkAccess: true);

        var target = adapter.Resolve(new WorkerInvocation("Draft a plan.", PermissionGrant: grant, StreamJson: true), ArchitectContract);

        Assert.Contains(target.Environment!, env => env.Name == AgyWorkerAdapter.ShellPatternsVariable && env.Value == "agy:git *");
    }

    [Fact]
    public void Resolving_a_grant_with_a_denyalways_pattern_emits_the_denied_shell_patterns_variable()
    {
        // #390: the agy hook is the only enforcement for a standing "never", so the adapter must emit
        // BATON_HOOK_DENIED_SHELL_PATTERNS. Its absence is fail-closed at the hook, so a missing emission
        // would silently deny every run_command — this pins that the channel is actually sent.
        // The meaningful case: the shell is granted (agy expresses that only as the network-bundled
        // --dangerously-skip-permissions) and a standing "never" carves rm back out via the hook.
        var adapter = new AgyWorkerAdapter();
        var grant = new PermissionGrant(
            ReadFiles: true, RunShellCommands: true, NetworkAccess: true,
            DeniedShellCommandPatterns: ["rm *"]);

        var target = adapter.Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant, StreamJson: true),
            ArchitectContract);

        Assert.Contains(
            target.Environment!,
            env => env.Name == AgyWorkerAdapter.DeniedShellPatternsVariable && env.Value == "agy:rm *");
    }

    [Fact]
    public void Resolving_a_grant_with_denied_option_tokens_emits_that_channel_too()
    {
        // #1683 F2. The hook is the ONLY enforcement of this rung on either vendor -- there is no
        // --disallowedTools half -- so an unemitted channel is not a lost narrowing, it is the whole
        // rung silently gone. Pinned separately from the pattern channel above because the two are
        // matched by different rules and a field can be threaded to one and not the other.
        var adapter = new AgyWorkerAdapter();
        var grant = new PermissionGrant(
            ReadFiles: true, RunShellCommands: true, NetworkAccess: true,
            DeniedShellOptionTokens: ["--output"]);

        var target = adapter.Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant, StreamJson: true),
            ArchitectContract);

        Assert.Contains(
            target.Environment!,
            env => env.Name == AgyWorkerAdapter.DeniedShellOptionTokensVariable && env.Value == "agy:--output");
    }

    [Fact]
    public void A_grant_with_no_denied_option_tokens_still_emits_that_channel_present_but_empty()
    {
        // Same always-emitted contract as the two channels beside it: "agy:" at minimum, so the hook
        // can tell "nothing denied" from "the channel broke".
        var adapter = new AgyWorkerAdapter();
        var grant = new PermissionGrant(ReadFiles: true, RunShellCommands: true, NetworkAccess: true);

        var target = adapter.Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant, StreamJson: true),
            ArchitectContract);

        Assert.Contains(
            target.Environment!,
            env => env.Name == AgyWorkerAdapter.DeniedShellOptionTokensVariable && env.Value == "agy:");
    }

    [Fact]
    public void A_grant_with_no_denies_still_emits_the_denied_shell_patterns_variable_present_but_empty()
    {
        // The channel is always emitted ("agy:" at minimum) so the hook can tell "no standing denies"
        // (Present+empty) from "the channel broke" (Absent) — the same distinction the allow channel draws.
        var adapter = new AgyWorkerAdapter();
        var grant = new PermissionGrant(ReadFiles: true, RunShellCommands: true, NetworkAccess: true);

        var target = adapter.Resolve(new WorkerInvocation("Draft a plan.", PermissionGrant: grant, StreamJson: true), ArchitectContract);

        Assert.Contains(
            target.Environment!,
            env => env.Name == AgyWorkerAdapter.DeniedShellPatternsVariable && env.Value == "agy:");
    }

    [Fact]
    public void An_empty_pattern_list_alongside_a_shell_grant_still_translates()
    {
        // The control, and the polarity mirror of the refusal above: the two differ only in whether
        // the pattern list has anything in it. Without this, the refusal passes just as well on an
        // adapter that rejects every shell grant — which would break the daemon's "auto" permission
        // mode, the one live shape that grants the shell at all.
        var adapter = new AgyWorkerAdapter();
        var grant = new PermissionGrant(
            ReadFiles: true, WriteFiles: true, RunShellCommands: true,
            ShellCommandPatterns: [], NetworkAccess: true);

        var succeeded = adapter.TryTranslatePermissionGrant(grant, out var resolved, out var gapReason);

        Assert.True(succeeded);
        Assert.Equal("--dangerously-skip-permissions", resolved);
        Assert.Null(gapReason);
    }

    [Fact]
    public void Patterns_without_a_shell_grant_are_not_refused()
    {
        // The second control. Patterns only mean anything alongside a shell grant, so a stray list on
        // a grant that withholds the shell is inert rather than a contradiction — refusing it would
        // reject a harmless binding, and the UI keeps the text box populated when the box is unticked.
        var adapter = new AgyWorkerAdapter();
        var grant = new PermissionGrant(
            ReadFiles: true, WriteFiles: true, RunShellCommands: false,
            ShellCommandPatterns: ["git:*"], NetworkAccess: false);

        var succeeded = adapter.TryTranslatePermissionGrant(grant, out var resolved, out var gapReason);

        Assert.True(succeeded);
        Assert.Equal("accept-edits", resolved);
        Assert.Null(gapReason);
    }

    [Fact]
    public void Resolving_with_shell_and_network_access_emits_dangerously_skip_permissions_as_standalone_argument()
    {
        var grant = new PermissionGrant(RunShellCommands: true, NetworkAccess: true);
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant, StreamJson: true), ArchitectContract);

        Assert.Equal("agy", target.Program);
        Assert.Equal("-p", target.Args[0]);
        Assert.Equal("--dangerously-skip-permissions", target.Args[2]);
        Assert.DoesNotContain("--mode", target.Args);
        Assert.Equal("--add-dir", target.Args[3]);
    }

    // ---------------------------------------------------------------- #554: the PreToolUse gate
    //
    // Decision 0029 makes the hook mandatory on every spawned worker. The tests below assert the
    // three things that have to hold for it to actually gate anything: the workspace is handed to
    // --add-dir (agy loads hooks from nowhere else, #538), the denied-tool list reaches the hook
    // process (via the environment -- measured by the `agy.hook-env-inherited` sentinel), and the
    // mapping covers the tools that would otherwise leak the withheld category.

    private static string EnvValue(CoreDispatchTarget target, string name) =>
        target.Environment!.Single(pair => pair.Name == name).Value;

    [Fact]
    public void Every_invocation_carries_the_agy_workspace_on_add_dir_so_the_gate_is_loaded()
    {
        // Unconditional, like #543's claude side: not only when a flow declares a gate. A hook
        // installed only sometimes cannot be relied on by anything downstream.
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan."), ArchitectContract);

        var addDirValues = target.Args
            .Select((arg, i) => (arg, i))
            .Where(pair => pair.arg == "--add-dir")
            .Select(pair => target.Args[pair.i + 1])
            .ToList();

        Assert.Contains(addDirValues, dir =>
            dir.EndsWith(AgyWorkerAdapter.AgyWorkspaceDirectoryName, StringComparison.Ordinal));
    }

    [Fact]
    public void The_gate_workspace_holds_a_hooks_file_naming_the_agy_hook_check_command()
    {
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan."), ArchitectContract);

        var workspace = target.Args
            .Select((arg, i) => (arg, i))
            .Where(pair => pair.arg == "--add-dir")
            .Select(pair => target.Args[pair.i + 1])
            .Single(dir => dir.EndsWith(AgyWorkerAdapter.AgyWorkspaceDirectoryName, StringComparison.Ordinal));

        var hooksPath = Path.Combine(workspace, ".agents", "hooks.json");
        Assert.True(File.Exists(hooksPath), $"no hooks.json was written to '{hooksPath}'");

        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(hooksPath));
        var handler = doc.RootElement
            .EnumerateObject().Single().Value      // hooks are keyed by an arbitrary NAME at the root
            .GetProperty("PreToolUse")[0];
        Assert.Equal("*", handler.GetProperty("matcher").GetString());

        var command = handler.GetProperty("hooks")[0].GetProperty("command").GetString()!;
        Assert.Contains("agy-hook-check", command, StringComparison.Ordinal);
        // Shell-parsed, with no exec form available on this vendor: a raw Windows path's \U and \t
        // would be read as escapes, so the path must be forward-slashed inside its quotes.
        Assert.DoesNotContain('\\', command);
    }

    [Fact]
    public void The_written_hooks_json_command_equals_what_BuildHookCommand_would_hand_the_probe()
    {
        // #1732 review N1: hooks.json's command and the resolve-time probe's command used to be two
        // independent interpolations of the same string, pinned by nothing. Both now call
        // AgyWorkerAdapter.BuildHookCommand -- this parses the written hooks.json (never
        // substring-matches; see RunWrittenHookCommand's own remarks on why) and asserts its command
        // equals BuildHookCommand's own output for the identical assembly path, which is exactly what
        // ProcessAgyHookLivenessProbe.Probe now spawns.
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan."), ArchitectContract);

        var workspace = target.Args
            .Select((arg, i) => (arg, i))
            .Where(pair => pair.arg == "--add-dir")
            .Select(pair => target.Args[pair.i + 1])
            .Single(dir => dir.EndsWith(AgyWorkerAdapter.AgyWorkspaceDirectoryName, StringComparison.Ordinal));

        using var doc = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(workspace, ".agents", "hooks.json")));
        var writtenCommand = doc.RootElement
            .EnumerateObject().Single().Value
            .GetProperty("PreToolUse")[0]
            .GetProperty("hooks")[0]
            .GetProperty("command").GetString()!;

        var hookAssemblyPath = Path.Combine(AppContext.BaseDirectory, "Baton.Cli.dll");
        Assert.Equal(AgyWorkerAdapter.BuildHookCommand(hookAssemblyPath), writtenCommand);
    }

    [Fact]
    public void A_withheld_category_reaches_the_hook_through_the_environment()
    {
        var grant = new PermissionGrant(ReadFiles: true, WriteFiles: false,
                                        RunShellCommands: true, NetworkAccess: true);
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant, StreamJson: true), ArchitectContract);

        var denied = StripVendorTag(EnvValue(target, AgyWorkerAdapter.DeniedToolsVariable)).Split(',');

        Assert.Contains("write_to_file", denied);
        Assert.Contains("replace_file_content", denied);
        Assert.Contains("multi_replace_file_content", denied);
        // Polarity: the granted categories must NOT be withheld, or a gate that denies everything
        // would pass the assertions above while breaking every worker.
        Assert.DoesNotContain("view_file", denied);
        Assert.DoesNotContain("run_command", denied);
        Assert.DoesNotContain("search_web", denied);
    }

    [Fact]
    public void Withholding_reads_also_withholds_the_tools_that_return_file_contents()
    {
        // grep_search returns file CONTENT, and list_dir/find_by_name disclose structure -- mapping
        // ReadFiles to view_file alone leaves the withheld category reachable. Found by the
        // implementation advisor reading agy's tool list against the first draft of this mapping.
        var grant = new PermissionGrant(ReadFiles: false, WriteFiles: true,
                                        RunShellCommands: true, NetworkAccess: true);
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract);

        var denied = StripVendorTag(EnvValue(target, AgyWorkerAdapter.DeniedToolsVariable)).Split(',');

        Assert.Contains("view_file", denied);
        Assert.Contains("grep_search", denied);
        Assert.Contains("list_dir", denied);
        Assert.Contains("find_by_name", denied);
        Assert.DoesNotContain("write_to_file", denied);
    }

    [Fact]
    public void Withholding_the_shell_also_withholds_control_of_background_shell_processes()
    {
        // manage_task sends stdin to and kills background commands, so withholding run_command
        // alone leaves shell control reachable.
        //
        // Network is withheld here too, and not by choice: TryTranslatePermissionGrant refuses
        // shell-without-network and network-without-shell outright, because the only agy flag that
        // grants either grants both. So the two categories are expressible only together, and a
        // shell-withheld grant is always also a network-withheld one on this vendor.
        var grant = new PermissionGrant(ReadFiles: true, WriteFiles: true,
                                        RunShellCommands: false, NetworkAccess: false);
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract);

        var denied = StripVendorTag(EnvValue(target, AgyWorkerAdapter.DeniedToolsVariable)).Split(',');

        Assert.Contains("run_command", denied);
        Assert.Contains("manage_task", denied);
        Assert.DoesNotContain("view_file", denied);
    }

    /// <summary>
    /// #1387 review, F1: before this pass, <c>define_subagent</c>/<c>invoke_subagent</c>/
    /// <c>manage_subagents</c>/<c>manage_task</c> were withheld only under <c>!RunShellCommands</c>,
    /// so a write-withheld, shell-granted grant (the real <c>review</c> role's shape) left the
    /// subagent trio reachable and unnarrowed -- a subagent could be defined with write tools enabled
    /// and invoked, defeating the write withholding none of the hook's other branches inspects. This
    /// pins the fix directly against the two real shipped roles the finding named: <c>review</c> now
    /// denies all four, <c>implement</c> (writes granted) denies none of them.
    /// </summary>
    [Fact]
    public void The_real_review_role_denies_the_subagent_trio_and_manage_task()
    {
        var review = WorkerRoleCatalog.For("review");

        var denied = AgyWorkerAdapter.BuildDeniedTools(review.Grant).Split(',');

        Assert.Contains("define_subagent", denied);
        Assert.Contains("invoke_subagent", denied);
        Assert.Contains("manage_subagents", denied);
        Assert.Contains("manage_task", denied);
    }

    /// <summary>
    /// #1802 supersedes this test's original name/finding: through the write/shell channel ALONE
    /// (the grant, with no <c>allowsSubagents</c> argument -- <c>BuildDeniedTools</c>'s own default of
    /// <see langword="true"/>), implement's write-and-shell-granted grant still reaches none of the
    /// trio, exactly as #1387 found. What changed is that this channel is no longer the only one:
    /// <see cref="The_real_implement_role_denies_the_subagent_trio_through_allows_subagents_even_though_its_grant_alone_would_not"/>
    /// pins the second, independent trigger #1802 added.
    /// </summary>
    [Fact]
    public void The_real_implement_role_s_grant_alone_does_not_deny_the_subagent_trio_or_manage_task()
    {
        var implement = WorkerRoleCatalog.For("implement");

        var denied = AgyWorkerAdapter.BuildDeniedTools(implement.Grant).Split(',');

        Assert.DoesNotContain("define_subagent", denied);
        Assert.DoesNotContain("invoke_subagent", denied);
        Assert.DoesNotContain("manage_subagents", denied);
        Assert.DoesNotContain("manage_task", denied);
    }

    /// <summary>
    /// #1802 (see <c>AgyWorkerAdapter.SubagentAndTaskTools</c>'s own remarks for the full reasoning):
    /// implement's grant keeps both WriteFiles and RunShellCommands true, so the pre-existing
    /// write/shell-withheld predicate above never fires for it. Passing the real catalog value end to
    /// end is what a hand-typed <c>allowsSubagents: false</c> literal would not catch (it would pass even if
    /// <c>WorkerRoleCatalog.For("implement").AllowsSubagents</c> silently reverted to true).
    /// </summary>
    [Fact]
    public void The_real_implement_role_denies_the_subagent_trio_through_allows_subagents_even_though_its_grant_alone_would_not()
    {
        var implement = WorkerRoleCatalog.For("implement");

        var denied = AgyWorkerAdapter.BuildDeniedTools(implement.Grant, implement.AllowsSubagents).Split(',');

        Assert.Contains("define_subagent", denied);
        Assert.Contains("invoke_subagent", denied);
        Assert.Contains("manage_subagents", denied);
        Assert.Contains("manage_task", denied);
    }

    /// <summary>
    /// advise's own grant already withholds writes (read-only role), so the pre-existing
    /// write/shell-withheld predicate denies the subagent trio on agy regardless of #1802's new flag --
    /// a pre-existing, orthogonal restriction this change does not touch. What #1802 guarantees for
    /// advise is narrower: AllowsSubagents true adds nothing beyond what the grant alone already
    /// denies, i.e. it never forces a denial the grant wouldn't already produce.
    /// </summary>
    [Fact]
    public void The_real_advise_role_s_allows_subagents_true_adds_no_denial_beyond_its_own_grant()
    {
        var advise = WorkerRoleCatalog.For("advise");

        Assert.True(advise.AllowsSubagents);
        Assert.Equal(
            AgyWorkerAdapter.BuildDeniedTools(advise.Grant),
            AgyWorkerAdapter.BuildDeniedTools(advise.Grant, advise.AllowsSubagents));
    }

    /// <summary>
    /// The fourth category, which had no arm at all until #596 — reads, writes and the shell each had
    /// one, and <c>search_web</c> appeared in this file exactly once, as a polarity assertion inside
    /// another test. Deleting the <c>NetworkAccess</c> branch from <c>BuildDeniedTools</c> failed
    /// nothing, which matters more than usual here: under <c>--dangerously-skip-permissions</c> the
    /// denied-tools list is the entire enforcement boundary, so an unguarded category is an unguarded
    /// capability.
    /// </summary>
    /// <remarks>
    /// Withheld alongside the shell rather than alone, because it cannot be isolated: a grant with
    /// network withheld and the shell granted is refused outright by
    /// <c>TryTranslatePermissionGrant</c> (agy has no flag expressing that pair). The polarity arm is
    /// what keeps the test honest under that constraint — a gate denying everything would fail it.
    /// </remarks>
    [Fact]
    public void Withholding_network_access_also_withholds_the_tools_that_reach_the_network()
    {
        var grant = new PermissionGrant(ReadFiles: true, WriteFiles: true,
                                        RunShellCommands: false, NetworkAccess: false);
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract);

        var denied = StripVendorTag(EnvValue(target, AgyWorkerAdapter.DeniedToolsVariable)).Split(',');

        Assert.Contains("search_web", denied);
        Assert.Contains("read_url_content", denied);
        // A prefix entry, not a tool name: agy's corpus offers `browser_.*` as a matcher example
        // while enumerating none of the actual names, so the family is withheld by prefix.
        Assert.Contains("browser_*", denied);
        Assert.DoesNotContain("view_file", denied);
        Assert.DoesNotContain("write_to_file", denied);
    }

    /// <summary>
    /// Guards the <b>boolean</b> category population, and only that. Each of the four booleans is
    /// covered by a withholding test above, but nothing stopped a fifth <i>boolean</i> being added to
    /// <see cref="PermissionGrant"/> and silently contributing no denied tools — under
    /// <c>--dangerously-skip-permissions</c> that is a capability granted with no arm to catch it.
    /// This fails until the new one is covered, which is the point: a prompt to write the test, not a
    /// substitute for one.
    /// </summary>
    /// <remarks>
    /// <b>A non-boolean dimension already exists that this guard cannot see, by construction.</b>
    /// <see cref="PermissionGrant.ShellCommandPatterns"/> is the fifth constructor parameter and is
    /// filtered out below, so it contributes no denied tools and nothing here notices — nor would an
    /// enum or a host allowlist added later. That is not hypothetical drift: this adapter never reads
    /// the field at all, while <c>ClaudeWorkerAdapter</c> honours it, which is its own defect — #624. Widening the filter is not the fix, because a pattern list does not map onto
    /// "withheld → deny these names"; it needs a per-vendor answer.
    /// <para>
    /// <b><see cref="PermissionGrant.ShellCommandsAreReadOnly"/> (#1456) is a bool and is excluded by
    /// name, not by type.</b> It is not a permission category this adapter withholds tools for at
    /// all — it is a coherence-check assertion <c>PermissionGrant.CategoriesDefeatedByTheShell</c>/
    /// <c>WorkerBindingResolver</c> read, consumed nowhere in <see cref="AgyWorkerAdapter"/>'s own
    /// <c>BuildDeniedTools</c>/<c>TryTranslatePermissionGrant</c>. A "withholding arm" test for it
    /// would be testing nothing this adapter does; see
    /// <c>A_read_only_scoped_shell_grant_is_still_refused_shell_without_network_on_agy</c> for the
    /// test that actually exercises this field's interaction with this adapter (it refuses, same as
    /// any other shell-without-network grant, because agy's translator does not read the field either).
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_permission_category_has_a_withholding_arm_in_this_suite()
    {
        var categories = typeof(PermissionGrant)
            .GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Where(p => p.ParameterType == typeof(bool))
            .Select(p => p.Name!)
            .Where(name => name != nameof(PermissionGrant.ShellCommandsAreReadOnly))
            .ToHashSet();

        // Each name here is asserted by a test in this file: reads and writes by the two
        // skip-permissions arms, the shell by the background-process arm, the network by the arm
        // directly above.
        var covered = new HashSet<string>
        {
            nameof(PermissionGrant.ReadFiles),
            nameof(PermissionGrant.WriteFiles),
            nameof(PermissionGrant.RunShellCommands),
            nameof(PermissionGrant.NetworkAccess),
        };

        Assert.Equal(
            categories.OrderBy(n => n, StringComparer.Ordinal),
            covered.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void An_invocation_with_no_grant_sets_the_variable_to_empty_rather_than_omitting_it()
    {
        // Always present so the value is AER's own rather than an inherited one. This does NOT
        // make absent distinguishable from empty -- agy-hook-check collapses both to allow, see
        // #600 -- so this asserts only what it can: the variable is set, and set to empty.
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", AllowsSubagents: true), ArchitectContract);

        // #600: the tag is what makes this an empty list AER actively sent, rather than an absence.
        Assert.Equal("agy:", EnvValue(target, AgyWorkerAdapter.DeniedToolsVariable));
    }

    [Fact]
    public void A_WorkerInvocation_built_with_defaults_denies_the_subagent_trio_and_manage_task()
    {
        // See ClaudeWorkerAdapterTests.A_WorkerInvocation_built_with_defaults_denies_Agent_and_Task
        // for why this needs its own coverage; this is agy's arm of the same check.
        var target = new AgyWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        var denied = EnvValue(target, AgyWorkerAdapter.DeniedToolsVariable).Split(':', 2)[1].Split(',');

        Assert.Contains("define_subagent", denied);
        Assert.Contains("invoke_subagent", denied);
        Assert.Contains("manage_subagents", denied);
        Assert.Contains("manage_task", denied);
    }

    [Fact]
    public void The_denied_tools_variable_matches_the_cli_side_contract()
    {
        // Baton.Vendors cannot reference Baton.Cli, so this name is a plain string contract asserted
        // on both sides. If they drift the hook reads an empty list and allows everything.
        Assert.Equal("BATON_HOOK_DENIED_TOOLS", AgyWorkerAdapter.DeniedToolsVariable);
    }

    // Everything above asserts against the C# objects Resolve() builds and the JSON it writes --
    // all of which would pass equally against a hook command that looks correct on paper and fails
    // the instant agy spawns it. These take the command out of the written hooks.json, split it, and
    // launch the assembly directly with a real agy payload and the real environment variable.
    //
    // WHAT THEY DO NOT COVER, and #710 is what happens when that is forgotten. They spawn via
    // ProcessStartInfo.ArgumentList, so the arguments go to the child verbatim. agy does not: it
    // hands the whole string to `cmd /c` on Windows or `sh -c` on Unix, and the shell decides what
    // the arguments even are. These tests therefore prove the assembly and its arguments behave;
    // they are structurally incapable of catching a command string the shell cannot parse, which is
    // exactly the defect that left the gate dead for months while they passed.
    //
    // That half belongs to a vendor check that runs the shipped command through agy itself, named
    // here because a reader of this pair needs to know it is one of two halves, not the whole:
    // record-once-ok: #710 tools/vendor-verify/verify.py
    // `agy.hook-command-survives-a-metacharacter-in-its-path`.
    //
    // Why it matters more here than on the claude side, where the equivalent pair already exists
    // (ClaudeWorkerAdapterTests.RunResolvedHookCommand): agy's handler has no exec form -- only a
    // single shell-parsed `command` string -- and a hook that cannot start produces no stdout, which
    // `agy.hook-malformed-stdout-fails-open` measured as an ALLOW. So on this vendor a hook that
    // fails to launch is an ungated worker, silently, with no --disallowedTools backstop
    // (`agy.permissions-are-global-only`). The `File.Exists` guard in BuildHooksJson checks the
    // path and proves nothing about whether the assembled command can actually run.

    [Fact]
    public void The_written_hook_commands_assembly_denies_a_withheld_tool_when_launched_directly()
    {
        var (decision, reason) = RunWrittenHookCommand(
            new PermissionGrant(ReadFiles: true, WriteFiles: true, RunShellCommands: false, NetworkAccess: false),
            """{"toolCall":{"name":"run_command","args":{"CommandLine":"ls"}},"stepIdx":1}""");

        Assert.Equal("deny", decision);
        Assert.Contains("run_command", reason);
    }

    [Fact]
    public void The_written_hook_commands_assembly_allows_a_granted_tool_when_launched_directly()
    {
        // Same grant, same payload shape, different tool -- so neither verdict can come from a
        // command that answers unconditionally.
        var (decision, _) = RunWrittenHookCommand(
            new PermissionGrant(ReadFiles: true, WriteFiles: true, RunShellCommands: false, NetworkAccess: false),
            """{"toolCall":{"name":"view_file","args":{"AbsolutePath":"x"}},"stepIdx":1}""");

        Assert.Equal("allow", decision);
    }

    [Fact]
    public void The_hook_assembly_carries_its_runtimeconfig_so_dotnet_can_load_it()
    {
        // Added on the claude side by #543's own review pass, for the same reason: asserting the
        // .dll exists proves nothing about whether `dotnet <dll>` can start it. A missing
        // .runtimeconfig.json makes the hook fail to launch -- which on agy reads as an allow.
        var assemblyPath = Path.Combine(AppContext.BaseDirectory, "Baton.Cli.dll");
        var runtimeConfigPath = Path.ChangeExtension(assemblyPath, ".runtimeconfig.json");

        Assert.True(File.Exists(runtimeConfigPath),
            $"'{runtimeConfigPath}' is missing, so `dotnet \"{assemblyPath}\"` cannot start the hook");
    }

    // HookAssemblyToken's branches, exercised with real directories rather than left to the one
    // shape this machine's install path happens to have -- on a spaceless install every production
    // call takes the bare-path early return, so without these the 8.3 branch and the refusal never
    // run under test at all. Why each shape is required is the method's own xmldoc; these only pin
    // that the code does what it says there.

    [Fact]
    public void The_hook_token_is_the_bare_forward_slash_path_when_it_is_clean()
    {
        Assert.Equal("C:/plain/Baton.Cli.dll",
            AgyWorkerAdapter.HookAssemblyToken(@"C:\plain\Baton.Cli.dll"));
    }

    [Fact]
    public void A_spaced_windows_directory_yields_a_clean_token_for_the_same_file()
    {
        var (directory, assemblyPath) = TempHookAssemblyUnder("baton flow probe-");
        try
        {
            string token;
            try
            {
                token = AgyWorkerAdapter.HookAssemblyToken(assemblyPath);
            }
            catch (InvalidOperationException)
            {
                // The method's contract when the volume cannot produce a clean 8.3 form is a loud
                // refusal, and that is the right behaviour -- but on such a volume this test cannot
                // observe the clean-token half, and pretending it did would be the false green.
                Assert.Skip("this volume has 8.3 name generation disabled, so only the refusal branch is reachable");
                return;
            }

            AssertCmdCanTokenize(token);
            Assert.EndsWith("/Baton.Cli.dll", token, StringComparison.Ordinal);
            Assert.True(File.Exists(token),
                $"the token must still resolve to the same file, or the hook dies at spawn: {token}");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void An_ampersand_directory_is_never_emitted_as_a_token_cmd_would_split()
    {
        // `&` is legal in an 8.3 name, so unlike a space the short form may keep it -- Windows
        // preserves it when it falls inside the retained prefix and drops it with the truncated
        // tail otherwise. Both outcomes honour the contract; what the contract forbids is the
        // third one: returning a token that still carries the character, silently, for cmd to
        // split into a command that never starts and an allow nobody sees.
        var (directory, assemblyPath) = TempHookAssemblyUnder("a&b probe-");
        try
        {
            string token;
            try
            {
                token = AgyWorkerAdapter.HookAssemblyToken(assemblyPath);
            }
            catch (InvalidOperationException refusal)
            {
                Assert.Contains("decision 0029", refusal.Message);
                return;
            }

            AssertCmdCanTokenize(token);
            Assert.True(File.Exists(token),
                $"the token must still resolve to the same file, or the hook dies at spawn: {token}");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    private static (string Directory, string AssemblyPath) TempHookAssemblyUnder(string prefix)
    {
        var directory = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var assemblyPath = Path.Combine(directory, "Baton.Cli.dll");
        File.WriteAllText(assemblyPath, "not a real assembly; only existence matters here");
        return (directory, assemblyPath);
    }

    private static void AssertCmdCanTokenize(string token) =>
        Assert.DoesNotContain(token, t => t is ' ' or '&' or '^' or ',' or ';' or '=');

    [Fact]
    public void A_spaced_path_with_no_directory_to_shorten_is_refused_loudly()
    {
        // No directory component, so the 8.3 remedy has nothing to shorten -- the contract is a
        // loud refusal, never a command that is emitted and then silently reads as an allow.
        var refusal = Assert.Throws<InvalidOperationException>(
            () => AgyWorkerAdapter.HookAssemblyToken("Baton Cli.dll"));
        Assert.Contains("decision 0029", refusal.Message);
    }

    /// <summary>
    /// Spawns the <c>command</c> string out of the written <c>hooks.json</c> and returns the parsed
    /// verdict. Parsed, not substring-matched: agy parses this stream, and output that merely
    /// contains "deny" while being invalid JSON is an allow.
    /// </summary>
    private static (string Decision, string Reason) RunWrittenHookCommand(
        PermissionGrant grant, string stdin)
    {
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract);

        var workspace = target.Args
            .Select((arg, i) => (arg, i))
            .Where(pair => pair.arg == "--add-dir")
            .Select(pair => target.Args[pair.i + 1])
            .Single(dir => dir.EndsWith(AgyWorkerAdapter.AgyWorkspaceDirectoryName, StringComparison.Ordinal));

        using var doc = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(workspace, ".agents", "hooks.json")));
        var command = doc.RootElement
            .EnumerateObject().Single().Value
            .GetProperty("PreToolUse")[0]
            .GetProperty("hooks")[0]
            .GetProperty("command").GetString()!;

        // The pattern pins the shape cmd resolves, deliberately strict rather than permissive,
        // because the token the assembly path may wear is a measured constraint of the shell agy
        // uses, not a style. The token must be bare: `cmd /c` resolves neither a quoted path nor a
        // bare one containing a space once an argument follows, so a command that grew a quote here
        // would be one that never starts -- and on this vendor a hook that never starts is an ALLOW.
        // A tolerant regex would let that through silently, which is what happened twice: `"` until
        // #706, then `'` until #710.
        //
        // This assertion pins the SHAPE. Whether agy's shell really resolves it is a vendor question
        // this test cannot reach -- see the note above the pair, and
        // `agy.hook-command-survives-a-metacharacter-in-its-path`, which runs it through agy.
        const string pattern = @"^(\S+) (\S+) (\S+)$";
        var match = System.Text.RegularExpressions.Regex.Match(command, pattern);
        Assert.True(match.Success,
            $"hook command does not have the bare shape cmd was measured to resolve -- the wrong "
            + $"token here is a command agy's shell cannot start, which reads as an allow: {command}");

        var startInfo = new ProcessStartInfo(match.Groups[1].Value)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(match.Groups[2].Value);
        startInfo.ArgumentList.Add(match.Groups[3].Value);

        // Forward both channels the adapter emits and the hook reads (#600 denied tools, #659 shell
        // patterns). Since #659 the hook denies when the shell-pattern channel is Absent — the same
        // fail-closed posture as denied tools — so a launcher that forwarded only one var would deny
        // every call, including the granted one this test proves is allowed. Production sends the full
        // environment dict; this mirrors that for the two vars that gate the verdict.
        foreach (var name in new[] { AgyWorkerAdapter.DeniedToolsVariable, AgyWorkerAdapter.ShellPatternsVariable })
        {
            startInfo.Environment[name] = target.Environment!.First(e => e.Name == name).Value;
        }

        using var process = Process.Start(startInfo)!;
        process.StandardInput.Write(stdin);
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEnd();
        var exited = process.WaitForExit(TimeSpan.FromSeconds(60));
        Assert.True(exited, "agy-hook-check did not exit within 30s");

        using var verdict = System.Text.Json.JsonDocument.Parse(stdout);
        return (verdict.RootElement.GetProperty("decision").GetString()!,
                verdict.RootElement.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "");
    }

    /// <summary>
    /// #600 tags the denied-tools value with its vendor (<c>agy:</c>) so an absent list, an empty
    /// one AER set, and another vendor's list are distinguishable. Every assertion below is about the
    /// tool names, so the tag is removed here rather than repeated in each one — and pinned once, in
    /// <see cref="The_denied_tools_value_is_tagged_with_this_adapters_vendor"/>.
    /// </summary>
    private static string StripVendorTag(string value)
    {
        Assert.StartsWith("agy:", value, StringComparison.Ordinal);
        return value["agy:".Length..];
    }

    [Fact]
    public void The_denied_tools_value_is_tagged_with_this_adapters_vendor()
    {
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("p", PermissionGrant: new PermissionGrant(ReadFiles: true, WriteFiles: false)),
            ArchitectContract);

        var value = target.Environment!.Single(v => v.Name == AgyWorkerAdapter.DeniedToolsVariable).Value;

        Assert.StartsWith("agy:", value, StringComparison.Ordinal);
    }

    [Fact]
    public void Classifies_verbatim_specimen_as_ExhaustedUntil_with_exact_reset_timestamp()
    {
        var specimen = "Worker exited with non-zero code 1. stderr: Error: Individual quota reached. Please upgrade your subscription to increase your limits. Resets in 28m40s.";
        var now = new DateTimeOffset(2026, 7, 30, 15, 0, 0, TimeSpan.Zero);
        var testTime = new TestTimeProvider(now);

        var adapter = new AgyWorkerAdapter();
        var classified = adapter.TryClassifyFailure(specimen, testTime, out var classification, out var retryNotBefore);

        Assert.True(classified);
        Assert.Equal(FailureClassification.ExhaustedUntil, classification);
        Assert.Equal(now.AddMinutes(28).AddSeconds(40), retryNotBefore);
    }

    [Fact]
    public void Quota_refusal_on_the_stdout_tail_alone_classifies_ExhaustedUntil()
    {
        // #1128: the real refusal (measured live 2026-08-12, execution eca57a30) arrived in the
        // stream-json result envelope on STDOUT with empty stderr — the single-tail path never saw
        // it. Verbatim from that run's log.
        var stdoutTail = """{"event":"result","result":{"conversation_id":"eca57a30-db54-4be3-b760-53d708f8ae79","status":"ERROR","response":"","error":"Individual quota reached. Please upgrade your subscription to increase your limits. Resets in 1h39m10s."}}""";
        var now = new DateTimeOffset(2026, 8, 12, 22, 0, 0, TimeSpan.Zero);
        var testTime = new TestTimeProvider(now);

        var adapter = new AgyWorkerAdapter();
        var classified = adapter.TryClassifyFailure(stderrTail: null, stdoutTail, testTime, out var classification, out var retryNotBefore);

        Assert.True(classified);
        Assert.Equal(FailureClassification.ExhaustedUntil, classification);
        Assert.Equal(now.AddHours(1).AddMinutes(39).AddSeconds(10), retryNotBefore);
    }

    [Fact]
    public void Worker_prose_about_permissions_on_stdout_does_not_veto_the_run()
    {
        // #1124 review finding E — why is the doc comment on AgyWorkerAdapter's two-tail
        // TryClassifyFailure override. This proves the negative half: a worker legitimately
        // discussing this repo's gate ("auto-denied" + "permission" are its daily vocabulary)
        // keeps its successful run un-vetoed.
        var stdoutTail = """{"event":"result","result":{"status":"OK","response":"When a tool is auto-denied, the permission gate writes an ask file and the worker blocks until a human answers."}}""";
        var testTime = new TestTimeProvider(DateTimeOffset.UtcNow);

        var adapter = new AgyWorkerAdapter();
        var classified = adapter.TryClassifyFailure(stderrTail: null, stdoutTail, testTime, out var classification, out _);

        Assert.False(classified);
        Assert.Null(classification);
    }

    [Fact]
    public void Non_quota_stderr_classifies_as_null()
    {
        var stderr = "Worker exited with non-zero code 1. stderr: Error: Failed to execute tool.";
        var testTime = new TestTimeProvider(DateTimeOffset.UtcNow);

        var adapter = new AgyWorkerAdapter();
        var classified = adapter.TryClassifyFailure(stderr, testTime, out var classification, out var retryNotBefore);

        Assert.False(classified);
        Assert.Null(classification);
        Assert.Null(retryNotBefore);
    }

    [Fact]
    public void Quota_like_stderr_without_parseable_duration_classifies_as_null()
    {
        var stderr = "Worker exited with non-zero code 1. stderr: Error: Individual quota reached. Resets in tomorrow.";
        var testTime = new TestTimeProvider(DateTimeOffset.UtcNow);

        var adapter = new AgyWorkerAdapter();
        var classified = adapter.TryClassifyFailure(stderr, testTime, out var classification, out var retryNotBefore);

        Assert.False(classified);
        Assert.Null(classification);
        Assert.Null(retryNotBefore);
    }

    [Fact]
    public void Quota_reset_with_overflowing_digits_classifies_as_null_not_a_thrown_exception()
    {
        // A duration too large for int must classify false, not throw -- the why (the pump's
        // deliberately catch-free classification path) lives on the TryParse block in
        // AgyWorkerAdapter.TryClassifyQuotaExhaustion.
        var stderr = "Error: Individual quota reached. Please upgrade your subscription to increase your limits. Resets in 99999999999999999999m.";
        var testTime = new TestTimeProvider(DateTimeOffset.UtcNow);

        var adapter = new AgyWorkerAdapter();
        var classified = adapter.TryClassifyFailure(stderr, testTime, out var classification, out var retryNotBefore);

        Assert.False(classified);
        Assert.Null(classification);
        Assert.Null(retryNotBefore);
    }

    [Theory]
    [InlineData("Resets in 2h.", 2, 0, 0)]
    [InlineData("Resets in 28m.", 0, 28, 0)]
    [InlineData("Resets in 40s.", 0, 0, 40)]
    [InlineData("Resets in 1h30s.", 1, 0, 30)]
    public void Each_optional_duration_group_parses_alone_and_in_partial_combination(
        string resetText, int hours, int minutes, int seconds)
    {
        // The three regex groups are independently optional; only the all-three specimen was
        // covered, so a group-reading edit could break one combination with every test green.
        var stderr = $"Error: Individual quota reached. Please upgrade your subscription to increase your limits. {resetText}";
        var now = new DateTimeOffset(2026, 7, 30, 15, 0, 0, TimeSpan.Zero);
        var testTime = new TestTimeProvider(now);

        var adapter = new AgyWorkerAdapter();
        var classified = adapter.TryClassifyFailure(stderr, testTime, out var classification, out var retryNotBefore);

        Assert.True(classified);
        Assert.Equal(FailureClassification.ExhaustedUntil, classification);
        Assert.Equal(now.AddHours(hours).AddMinutes(minutes).AddSeconds(seconds), retryNotBefore);
    }

    [Fact]
    public void Classifies_verbatim_auto_denied_tool_stderr_as_ToolDenied_with_null_retryNotBefore()
    {
        var specimen = "jetski: no output produced — a tool required the \"command\" permission that headless mode cannot prompt for, so it was auto-denied. Add an allow-rule under permissions.allow in settings.json (e.g. command(<target>)).";
        var testTime = new TestTimeProvider(DateTimeOffset.UtcNow);

        var adapter = new AgyWorkerAdapter();
        var classified = adapter.TryClassifyFailure(specimen, testTime, out var classification, out var retryNotBefore);

        Assert.True(classified);
        Assert.Equal(FailureClassification.ToolDenied, classification);
        Assert.Null(retryNotBefore);
    }

    [Theory]
    [InlineData("a tool required the command permission that headless mode cannot prompt for")]
    [InlineData("a required tool was auto-denied without user input")]
    public void Single_marker_stderr_does_not_classify_as_auto_denied_tool(string stderr)
    {
        var testTime = new TestTimeProvider(DateTimeOffset.UtcNow);

        var adapter = new AgyWorkerAdapter();
        var classified = adapter.TryClassifyFailure(stderr, testTime, out var classification, out var retryNotBefore);

        Assert.False(classified);
        Assert.Null(classification);
        Assert.Null(retryNotBefore);
    }

    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    /// <summary>
    /// The agy arm of the no-silent-behaviour-change requirement stated on
    /// <see cref="ClaudeWorkerAdapterTests.Not_opting_in_to_the_memory_proposal_tool_keeps_the_empty_mcp_config"/>:
    /// no extra `--add-dir` for a workspace nobody asked for.
    /// </summary>
    // record-once-ok: #801 tests/Baton.Vendors.Tests/ClaudeWorkerAdapterTests.cs
    [Fact]
    public void Not_opting_in_to_the_memory_proposal_tool_adds_no_extra_add_dir()
    {
        var target = new AgyWorkerAdapter().Resolve(new WorkerInvocation("p"), ArchitectContract);

        Assert.DoesNotContain(target.Args, a => a.Contains("memory-proposal", StringComparison.Ordinal));
    }

    /// <summary>
    /// #801: opting in grants an extra `--add-dir` pointing to a workspace with `.agents/mcp_config.json`
    /// (see <see cref="AgyWorkerAdapter.MemoryProposalWorkspaceDirectoryName"/>) -- agy's only
    /// lever; why is the adapter's own remarks' business, not restated here.
    /// </summary>
    // record-once-ok: #801 src/Baton.Vendors/AgyWorkerAdapter.cs
    [Fact]
    public void Opting_in_to_the_memory_proposal_tool_materializes_a_workspace_config_and_grants_it()
    {
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("p", EnableMemoryProposalTool: true), ArchitectContract);

        var expectedWorkspace = Path.Combine(
            BatonPaths.WorkerLaunchConfig, AgyWorkerAdapter.MemoryProposalWorkspaceDirectoryName);
        var configPath = Path.Combine(expectedWorkspace, ".agents", "mcp_config.json");

        Assert.Contains(expectedWorkspace, target.Args);
        Assert.True(File.Exists(configPath), "the workspace's mcp_config.json must already exist");

        using var mcpDoc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(configPath));
        var server = mcpDoc.RootElement.GetProperty("mcpServers").GetProperty("baton-memory-proposal");
        Assert.Equal("dotnet", server.GetProperty("command").GetString());
        var serverArgs = server.GetProperty("args").EnumerateArray().Select(a => a.GetString()).ToList();
        // #1458: same args-order assertion as ClaudeWorkerAdapterTests' sibling test, and for the
        // identical reason -- see that test's own doc comment (canonical).
        Assert.True(serverArgs.Count >= 3, "expected <dll path>, mcp, --memory-proposal-tool");
        Assert.EndsWith("Baton.Cli.dll", serverArgs[0], StringComparison.Ordinal);
        Assert.Equal("mcp", serverArgs[1]);
        Assert.Contains("--memory-proposal-tool", serverArgs);
        Assert.DoesNotContain(serverArgs, a => a!.Contains("memory-proposals", StringComparison.Ordinal));
    }

    [Fact]
    public void Non_shell_grant_injects_HOME_and_USERPROFILE_redirects()
    {
        var nonShellGrant = new PermissionGrant(ReadFiles: true, WriteFiles: false, RunShellCommands: false, NetworkAccess: false);
        var target = new AgyWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan.", PermissionGrant: nonShellGrant), ArchitectContract);

        var home = target.Environment!.SingleOrDefault(e => e.Name == "HOME").Value;
        var userProfile = target.Environment!.SingleOrDefault(e => e.Name == "USERPROFILE").Value;

        Assert.NotNull(home);
        Assert.NotNull(userProfile);
        Assert.Equal(home, userProfile);
        Assert.Contains("gemini_home", home);
    }

    [Fact]
    public void Shell_granted_worker_does_not_inject_HOME_or_USERPROFILE_redirects()
    {
        var shellGrant = new PermissionGrant(ReadFiles: true, WriteFiles: true, RunShellCommands: true, NetworkAccess: true);
        var target = new AgyWorkerAdapter().Resolve(new WorkerInvocation("Build.", PermissionGrant: shellGrant), ArchitectContract);

        Assert.DoesNotContain(target.Environment!, e => e.Name == "HOME");
        Assert.DoesNotContain(target.Environment!, e => e.Name == "USERPROFILE");
    }

    [Fact]
    public void Batch_dispatch_points_home_redirect_under_execution_output_dir()
    {
        var nonShellGrant = new PermissionGrant(ReadFiles: true, WriteFiles: false, RunShellCommands: false, NetworkAccess: false);
        var target = new AgyWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan.", PermissionGrant: nonShellGrant), ArchitectContract);

        var home = target.Environment!.Single(e => e.Name == "HOME").Value;
        const string expectedRef = "%BATON_OUTPUT_DIR%";
        Assert.StartsWith(expectedRef, home);
    }

    /// <summary>
    /// No SessionId on purpose: the daemon mints a vendor session id only for claude, so a real agy
    /// session turn is classified entirely by the <c>.baton/room.json</c> kind marker (ReadRoomKind ==
    /// Interactive) on the bindings directory -- this pins that clause alone, not the claude-only
    /// shortcut in front of it.
    /// </summary>
    [Fact]
    public void Session_dispatch_points_home_redirect_under_session_root()
    {
        var tempSessionDir = Path.Combine(Path.GetTempPath(), "test-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempSessionDir, ".baton"));
        File.WriteAllText(Path.Combine(tempSessionDir, ".baton", "room.json"), "{}");

        try
        {
            var nonShellGrant = new PermissionGrant(ReadFiles: true, WriteFiles: false, RunShellCommands: false, NetworkAccess: false);
            var target = new AgyWorkerAdapter().Resolve(
                new WorkerInvocation("Chat turn.", PermissionGrant: nonShellGrant, BindingsFileDirectory: tempSessionDir), ArchitectContract);

            var home = target.Environment!.Single(e => e.Name == "HOME").Value;
            var expectedHome = Path.Combine(tempSessionDir, ".gemini_home");
            Assert.Equal(expectedHome, home);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempSessionDir);
        }
    }

    // #1084: the three arms below pin the seed AgyWorkerAdapter emits -- present for the write-granted
    // case, absent for the two that must not carry it. Why the seed is needed and what it permits is on
    // Resolve; the vendor claim that agy honours it is the live `baton dispatch advise --adapter agy` run.
    [Fact]
    public void Write_granted_accept_edits_role_seeds_write_allow_into_redirected_home()
    {
        var writeGrant = new PermissionGrant(ReadFiles: true, WriteFiles: true, RunShellCommands: false, NetworkAccess: false);
        var target = new AgyWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan.", PermissionGrant: writeGrant), ArchitectContract);

        var seed = Assert.Single(target.SeedFiles!);
        Assert.Contains("gemini_home", seed.PathTemplate);
        Assert.EndsWith(Path.Combine(".gemini", "antigravity-cli", "settings.json"), seed.PathTemplate);

        // Content parses as JSON (a Windows path substituted with backslashes would not) and carries the
        // least-privilege, per-output rule with a forward-slashed target and the unexpanded placeholder.
        using var doc = JsonDocument.Parse(seed.Content);
        var allow = doc.RootElement.GetProperty("permissions").GetProperty("allow");
        var rule = Assert.Single(allow.EnumerateArray()).GetString();
        const string expectedRef = "%BATON_OUTPUT_DIR%";
        Assert.Equal($"write_file({expectedRef}/plan.md)", rule);
    }

    [Fact]
    public void Write_allow_seeds_one_rule_per_declared_output()
    {
        // A single-output contract cannot catch a `.Select` regression that drops all but the first
        // output; a two-output contract pins the rule SET, not just that some rule was emitted.
        var contract = new WorkerContract(
            "architect", ["goal"], [new ProducedOutput("plan.md"), new ProducedOutput("risks.md")], []);
        var writeGrant = new PermissionGrant(ReadFiles: true, WriteFiles: true, RunShellCommands: false, NetworkAccess: false);
        var target = new AgyWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan.", PermissionGrant: writeGrant), contract);

        var seed = Assert.Single(target.SeedFiles!);
        using var doc = JsonDocument.Parse(seed.Content);
        var rules = doc.RootElement.GetProperty("permissions").GetProperty("allow")
            .EnumerateArray().Select(e => e.GetString()!).ToArray();
        const string expectedRef = "%BATON_OUTPUT_DIR%";
        Assert.Equal([$"write_file({expectedRef}/plan.md)", $"write_file({expectedRef}/risks.md)"], rules);
    }

    [Fact]
    public void Shell_granted_worker_does_not_seed_write_allow()
    {
        // The skip-permissions path already auto-approves writes -- seeding an allow-rule would be
        // redundant, and this is how the front door was built. Polarity for the positive arm above.
        var shellGrant = new PermissionGrant(ReadFiles: true, WriteFiles: true, RunShellCommands: true, NetworkAccess: true);
        var target = new AgyWorkerAdapter().Resolve(new WorkerInvocation("Build.", PermissionGrant: shellGrant), ArchitectContract);

        Assert.True(target.SeedFiles is null or { Count: 0 });
    }

    [Fact]
    public void Raw_accept_edits_scope_without_a_grant_seeds_nothing()
    {
        // No grant -> no redirected home -> the seed is gated off, so this raw-scope path emits nothing.
        // The isolation reason the gate protects (never the operator's own home) is on Resolve.
        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionScope: "accept-edits"), ArchitectContract);

        Assert.True(target.SeedFiles is null or { Count: 0 });
    }

    [Fact]
    public void Unknown_adapter_key_gemini_throws_plain_unknown_error_with_no_rename_hint()
    {
        // Hard cutover (#1035): "gemini" is not a recognized adapter identity — it falls through to
        // the generic unknown-adapter message, exactly like any other unregistered name. The message
        // must NOT carry the old "renamed to 'agy'" migration hint (polarity: proves the special-case
        // is gone, not merely that some message is thrown).
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["worker"] = new WorkerBindingConfigEntry(
                Adapter: "gemini",
                Contract: ArchitectContract,
                PromptTemplate: "Draft a plan.",
                Timeout: TimeSpan.FromMinutes(20))
        };

        var ex = Assert.Throws<UnknownWorkerAdapterException>(() =>
            WorkerBindingResolver.Resolve(config, WorkerAdapterRegistry.Default));

        Assert.Contains("gemini", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("renamed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WorkerTiers_json_loads_and_resolves_agy_adapter_while_preserving_gemini_model_strings()
    {
        var roles = BuiltInWorkflowTemplates.GetRoleTemplates();
        Assert.NotEmpty(roles);

        // Prove the agy-bound tier (cheap, since #1861 moved standard off agy) still maps to the agy
        // adapter while keeping its gemini-3.8-flash-* model name -- the adapter is named "agy",
        // never "gemini", and the model string is the vendor's own. #1863 (operator ruling,
        // 2026-09-06) moved the pin off the 3.6 Flash family; docs/dispatch.md's tier paragraph
        // carries the measurement behind the value.
        var tiersJsonPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Baton.Vendors", "WorkerTiers.json");
        Assert.True(File.Exists(tiersJsonPath), $"WorkerTiers.json must exist at {tiersJsonPath}");

        var json = File.ReadAllText(tiersJsonPath);
        Assert.Contains("\"adapter\": \"agy\"", json);
        Assert.DoesNotContain("\"adapter\": \"gemini\"", json);
        Assert.Contains("\"model\": \"gemini-3.8-flash-medium\"", json);

        Assert.True(WorkerAdapterRegistry.Default.TryGetValue("agy", out var agyAdapter));
        Assert.IsType<AgyWorkerAdapter>(agyAdapter);
    }

    // ---- #1387 review F7: the composition, not just the translation ----
    //
    // Every test above pins BuildShellPatterns/BuildDeniedShellPatterns's OUTPUT, or the adapter's
    // own translation. Neither runs that output through AgyHookCheckCommand.Execute, so nothing
    // exercises "adapter emits review's patterns -> hook denies curl / allows git status" -- the
    // composition #1387 actually depends on. agy.hook-deny-honoured (tools/vendor-verify/verify.py)
    // only proves a deny blocks a call under a hook that always denies; it says nothing about
    // review's real allow/deny lists producing the right verdict. This group closes that gap by
    // feeding the REAL review role (loaded from WorkerRoles.json, not a hand-written fixture)
    // straight into the hook, and asserts the deny REASON per arm so the two deny channels
    // (DenyAlways vs. allow-list-miss) are told apart rather than collapsed into "decision: deny".
    //
    // git merge-base is deliberately not asserted here: it is shadowed by the git merge* deny entry
    // (#1679) and would fail this test for a reason unrelated to the composition being proven.

    private static string HookPayload(string commandLine) => $$"""
        {"artifactDirectoryPath":"C:/x/brain/abc","conversationId":"abc",
         "toolCall":{"args":{"CommandLine":"{{commandLine}}","Cwd":"C:\\x","WaitMsBeforeAsync":5000},
                     "name":"run_command"},
         "transcriptPath":"C:/x/transcript_full.jsonl","workspacePaths":["C:/x"]}
        """;

    private static (string Decision, string? Reason) DecideForReviewRole(string commandLine)
    {
        var review = WorkerRoleCatalog.For("review");
        var deniedTools = $"{AgyWorkerAdapter.DeniedToolsVendorTag}:{AgyWorkerAdapter.BuildDeniedTools(review.Grant)}";
        var shellPatterns = $"{AgyWorkerAdapter.ShellPatternsVendorTag}:{AgyWorkerAdapter.BuildShellPatterns(review.Grant)}";
        var deniedShellPatterns = $"{AgyWorkerAdapter.ShellPatternsVendorTag}:{AgyWorkerAdapter.BuildDeniedShellPatterns(review.Grant)}";

        using var stdin = new StringReader(HookPayload(commandLine));
        using var stdout = new StringWriter();

        Baton.Cli.AgyHookCheckCommand.Execute(
            stdin, stdout, deniedTools, shellPatternsRaw: shellPatterns,
            deniedShellPatternsRaw: deniedShellPatterns, deniedShellOptionTokensRaw: "agy:");

        using var doc = JsonDocument.Parse(stdout.ToString());
        var decision = doc.RootElement.GetProperty("decision").GetString()!;
        var reason = doc.RootElement.TryGetProperty("reason", out var reasonProp)
            ? reasonProp.GetString()
            : null;
        return (decision, reason);
    }

    [Theory]
    [InlineData("git status", "allow", null)]
    [InlineData("git log -1", "allow", null)]
    [InlineData("curl https://example.com", "deny", "does not match any pattern this session's grant allows")]
    [InlineData("git push --dry-run origin HEAD", "deny", "matches this session's standing deny list")]
    public void The_real_review_role_s_shell_patterns_compose_correctly_through_the_hook(
        string commandLine, string expectedDecision, string? expectedReasonSubstring)
    {
        var (decision, reason) = DecideForReviewRole(commandLine);

        Assert.Equal(expectedDecision, decision);
        if (expectedReasonSubstring is null)
        {
            Assert.Null(reason);
        }
        else
        {
            Assert.Contains(expectedReasonSubstring, reason);
        }
    }

    // ---- F1 (#1720 review): the exit-0 veto, through the REAL agy classifier ----

    /// <summary>
    /// F1's red arm (<see cref="IFailureClassifier.TryClassifySatisfiedRunFailure"/> has the why): a
    /// worker that finished its job and wrote ABOUT a quota refusal — reviewing this adapter,
    /// summarising an incident, drafting a runbook paragraph — had its completed run discarded as
    /// Failed and parked until an instant fabricated from its own prose. Driven through the REAL
    /// adapter rather than a canned double, the agy twin of
    /// <c>ClaudeWorkerAdapterTests</c>'s own integration arm.
    /// </summary>
    [Fact]
    public void Classify_does_not_veto_a_satisfied_exit_0_agy_run_whose_answer_text_quotes_the_quota_sentence()
    {
        var stdoutTail =
            """{"event":"result","result":{"conversation_id":"c-1","status":"SUCCESS","response":"I read the log: agy answered 'Individual quota reached. Please upgrade your subscription to increase your limits. Resets in 1h39m10s.' and the lane parked."}}""";
        var contract = new WorkerContract("worker", [], [], []);
        var directory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            IFailureClassifier adapter = new AgyWorkerAdapter();

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural, StderrTail: null, StdoutTail: stdoutTail),
                contract,
                directory,
                adapter);

            Assert.Equal(OutcomeVerdict.Succeeded, classification.Verdict);
            Assert.Null(classification.FailureClassification);
            Assert.Null(classification.RetryNotBefore);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    /// <summary>
    /// F1's green arm, and the polarity control for the one above: the vendor's OWN terminal envelope
    /// — <c>result.status</c> other than <c>SUCCESS</c>, the quota sentence in its <c>result.error</c>
    /// — still parks a satisfied exit-0 run, with the reset instant read from that envelope. A worker
    /// cannot emit this; the CLI writes it. The two arms differ only in whether the signal is
    /// vendor-typed, which is the whole content of the fix.
    /// </summary>
    [Fact]
    public void Classify_vetoes_a_satisfied_exit_0_agy_run_carrying_the_vendors_own_quota_result_envelope()
    {
        var stdoutTail =
            """{"event":"result","result":{"conversation_id":"eca57a30-db54-4be3-b760-53d708f8ae79","status":"ERROR","response":"","error":"Individual quota reached. Please upgrade your subscription to increase your limits. Resets in 1h39m10s."}}""";
        var contract = new WorkerContract("worker", [], [], []);
        var directory = Directory.CreateTempSubdirectory().FullName;
        var now = new DateTimeOffset(2026, 8, 12, 22, 0, 0, TimeSpan.Zero);
        try
        {
            IFailureClassifier adapter = new AgyWorkerAdapter();

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural, StderrTail: null, StdoutTail: stdoutTail),
                contract,
                directory,
                adapter,
                timeProvider: new TestTimeProvider(now));

            Assert.Equal(OutcomeVerdict.Failed, classification.Verdict);
            Assert.Equal(FailureClassification.ExhaustedUntil, classification.FailureClassification);
            Assert.Equal(now.AddHours(1).AddMinutes(39).AddSeconds(10), classification.RetryNotBefore);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    /// <summary>
    /// agy's Finding G twin (#1720 review): the red arm above quotes the quota SENTENCE, not the
    /// vendor's own envelope, which the typed status/error shape already rejects. See
    /// <c>ClaudeWorkerAdapterTests.A_workers_own_answer_text_embedding_a_full_verbatim_envelope_does_not_classify</c>
    /// for what this fixture defends against and why it needs a C# raw string.
    /// </summary>
    [Fact]
    public void Classify_does_not_veto_a_satisfied_exit_0_agy_run_whose_answer_text_embeds_a_full_verbatim_envelope()
    {
        var stdoutTail =
            """{"event":"result","result":{"conversation_id":"c-2","status":"SUCCESS","response":"agy printed {\"event\":\"result\",\"result\":{\"status\":\"ERROR\",\"error\":\"Individual quota reached. Resets in 1h39m10s.\"}}"}}""";
        var contract = new WorkerContract("worker", [], [], []);
        var directory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            IFailureClassifier adapter = new AgyWorkerAdapter();

            var classification = OutcomeClassifier.Classify(
                new CoreDispatchResult(0, CoreExitReason.Natural, StderrTail: null, StdoutTail: stdoutTail),
                contract,
                directory,
                adapter);

            Assert.Equal(OutcomeVerdict.Succeeded, classification.Verdict);
            Assert.Null(classification.FailureClassification);
            Assert.Null(classification.RetryNotBefore);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    // ---- #1387 review F8: the write-granted scoped-shell variant is also intentionally in scope ----

    [Fact]
    public void A_write_granted_pattern_scoped_shell_grant_without_network_also_defers_to_the_hook()
    {
        // No shipped role has this shape (advise has writes without shell; implement/janitor have
        // both plus network) -- see TryTranslatePermissionGrant's own remark on this branch for what
        // it resolves to and why that's intentional (#1387 review, F8).
        var adapter = new AgyWorkerAdapter();
        var grant = new PermissionGrant(
            ReadFiles: true, WriteFiles: true, RunShellCommands: true,
            ShellCommandPatterns: ["git status*"], NetworkAccess: false);

        var succeeded = adapter.TryTranslatePermissionGrant(grant, out var resolved, out var gapReason);

        Assert.True(succeeded);
        Assert.Equal("--dangerously-skip-permissions", resolved);
        Assert.Null(gapReason);
    }

    // ---- #1680: resolve-time hook liveness probe ----

    /// <summary>Deterministic test double -- see <see cref="IAgyHookLivenessProbe"/>'s own remarks.</summary>
    private sealed class FakeHookLivenessProbe : IAgyHookLivenessProbe
    {
        private readonly AgyHookLivenessResult _result;
        public int CallCount { get; private set; }

        public FakeHookLivenessProbe(AgyHookLivenessResult result) => _result = result;

        public AgyHookLivenessResult Probe(string hookAssemblyPath, TimeSpan timeout)
        {
            CallCount++;
            return _result;
        }
    }

    private static readonly PermissionGrant ReviewShapedGrant =
        new(ReadFiles: true, WriteFiles: false, RunShellCommands: true, NetworkAccess: true);

    [Fact]
    public void A_grant_whose_only_narrowing_is_the_hook_refuses_dispatch_when_the_probe_reports_dead()
    {
        // #1680 acceptance: a hook the probe cannot confirm is live refuses at resolve time, naming
        // the hook path and what the probe actually reported -- here, a synthetic non-existent path,
        // matching the issue's own "hook path -> non-existent file" scenario, WITHOUT spawning any
        // real process (the probe is a fake; see FakeHookLivenessProbe).
        var probe = new FakeHookLivenessProbe(
            new AgyHookLivenessResult(false, "'C:/does/not/exist/Baton.Cli.dll' does not exist"));
        var adapter = new AgyWorkerAdapter(probe);

        var ex = Assert.Throws<AgyHookUnverifiedException>(() => adapter.Resolve(
            new WorkerInvocation("Review the diff.", PermissionGrant: ReviewShapedGrant, StreamJson: true), ArchitectContract));

        Assert.Equal(1, probe.CallCount);
        Assert.Contains("does not exist", ex.Message);
        Assert.Contains("PreToolUse hook", ex.Message);
        Assert.Contains(ex.HookAssemblyPath, ex.Message);
    }

    [Theory]
    [InlineData("timed out")]
    [InlineData("stdout carried no 'decision' field")]
    [InlineData("returned decision 'allow' instead of 'deny'")]
    public void Any_non_deny_probe_outcome_refuses_dispatch(string detail)
    {
        var probe = new FakeHookLivenessProbe(new AgyHookLivenessResult(false, detail));
        var adapter = new AgyWorkerAdapter(probe);

        var ex = Assert.Throws<AgyHookUnverifiedException>(() => adapter.Resolve(
            new WorkerInvocation("Review the diff.", PermissionGrant: ReviewShapedGrant, StreamJson: true), ArchitectContract));

        Assert.Contains(detail, ex.Message);
    }

    [Fact]
    public void A_live_probe_lets_dispatch_proceed_normally()
    {
        var probe = new FakeHookLivenessProbe(new AgyHookLivenessResult(true, "deny"));
        var adapter = new AgyWorkerAdapter(probe);

        var target = adapter.Resolve(
            new WorkerInvocation("Review the diff.", PermissionGrant: ReviewShapedGrant, StreamJson: true), ArchitectContract);

        Assert.Equal(1, probe.CallCount);
        Assert.Equal("agy", target.Program);
    }

    [Fact]
    public void A_non_streaming_sole_narrowing_grant_is_refused_at_resolve()
    {
        // #1732 review N5, ruled fail closed -- see AgyCanaryRequiresStreamJsonException's own
        // remarks for why this shape is refused rather than shipped as a silent hole. The probe must
        // not even run: there is nothing to confirm live if the dispatch is refused before it starts.
        var probe = new FakeHookLivenessProbe(new AgyHookLivenessResult(false, "must not be called"));
        var adapter = new AgyWorkerAdapter(probe);

        var ex = Assert.Throws<AgyCanaryRequiresStreamJsonException>(() => adapter.Resolve(
            new WorkerInvocation("Review the diff.", PermissionGrant: ReviewShapedGrant, StreamJson: false),
            ArchitectContract));

        Assert.Equal(0, probe.CallCount);
        Assert.Contains("StreamJson", ex.Message);
    }

    [Fact]
    public void The_same_grant_resolves_normally_once_StreamJson_is_true()
    {
        var probe = new FakeHookLivenessProbe(new AgyHookLivenessResult(true, "deny"));
        var adapter = new AgyWorkerAdapter(probe);

        var target = adapter.Resolve(
            new WorkerInvocation("Review the diff.", PermissionGrant: ReviewShapedGrant, StreamJson: true),
            ArchitectContract);

        Assert.Equal(1, probe.CallCount);
        Assert.NotNull(target.CountHookVerdicts);
        // #1741: the file name travels alongside the delegate so a caller can journal the arming
        // fact durably (ExecutionRequest.HookVerdictLedgerFileName) -- same gate as CountHookVerdicts.
        Assert.Equal(AgyWorkerAdapter.VerdictLedgerFileName, target.HookVerdictLedgerFileName);
    }

    [Fact]
    public void A_grant_with_both_categories_already_open_never_calls_the_probe()
    {
        // implement/janitor's shape: RunShellCommands and NetworkAccess both true reaches
        // --dangerously-skip-permissions, but WriteFiles is also true, so there is nothing left for
        // the hook to be the ONLY thing narrowing -- the probe must not run.
        var probe = new FakeHookLivenessProbe(new AgyHookLivenessResult(false, "must not be called"));
        var adapter = new AgyWorkerAdapter(probe);
        var grant = new PermissionGrant(
            ReadFiles: true, WriteFiles: true, RunShellCommands: true, NetworkAccess: true);

        adapter.Resolve(new WorkerInvocation("Build.", PermissionGrant: grant), ArchitectContract);

        Assert.Equal(0, probe.CallCount);
    }

    [Fact]
    public void A_grant_that_never_reaches_dangerously_skip_permissions_never_calls_the_probe()
    {
        // advise's shape: writes granted, no shell/network at all -- resolves to plain --mode
        // accept-edits, never --dangerously-skip-permissions, so the probe must not run regardless of
        // what is withheld.
        var probe = new FakeHookLivenessProbe(new AgyHookLivenessResult(false, "must not be called"));
        var adapter = new AgyWorkerAdapter(probe);
        var grant = new PermissionGrant(ReadFiles: true, WriteFiles: true);

        adapter.Resolve(new WorkerInvocation("Advise.", PermissionGrant: grant), ArchitectContract);

        Assert.Equal(0, probe.CallCount);
    }

    [Fact]
    public void A_default_scope_dispatch_with_no_PermissionGrant_never_calls_the_probe()
    {
        var probe = new FakeHookLivenessProbe(new AgyHookLivenessResult(false, "must not be called"));
        var adapter = new AgyWorkerAdapter(probe);

        adapter.Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        Assert.Equal(0, probe.CallCount);
    }

    [Theory]
    // WriteFiles withheld, network granted (review's own shape): sole narrowing.
    [InlineData("--dangerously-skip-permissions", false, true, true)]
    // Writes granted, network withheld (the #1387 pattern-scoped shape): sole narrowing.
    [InlineData("--dangerously-skip-permissions", true, false, true)]
    // Both withheld: still sole narrowing.
    [InlineData("--dangerously-skip-permissions", false, false, true)]
    // Both granted (implement/janitor's shape): nothing left for the hook to solely narrow.
    [InlineData("--dangerously-skip-permissions", true, true, false)]
    // Not --dangerously-skip-permissions at all: irrelevant what the grant withholds.
    [InlineData("accept-edits", false, false, false)]
    [InlineData("plan", false, false, false)]
    public void RequiresHookAsSoleNarrowing_predicate(
        string permissionScope, bool writeFiles, bool networkAccess, bool expected)
    {
        var grant = new PermissionGrant(WriteFiles: writeFiles, NetworkAccess: networkAccess);

        Assert.Equal(expected, AgyWorkerAdapter.RequiresHookAsSoleNarrowing(permissionScope, grant));
    }

    [Fact]
    public void RequiresHookAsSoleNarrowing_is_true_for_a_fully_granted_grant_carrying_a_shell_allow_pattern()
    {
        // #1732 review F5: writes and network both granted, but ShellCommandPatterns is non-empty --
        // TryTranslatePermissionGrant's pattern-scoped path (#1387) reaches
        // --dangerously-skip-permissions with no requirement that the pattern list be honoured by
        // anything but the hook, so this is still sole narrowing.
        var grant = new PermissionGrant(
            WriteFiles: true, NetworkAccess: true, RunShellCommands: true,
            ShellCommandPatterns: ["git *"]);

        Assert.True(AgyWorkerAdapter.RequiresHookAsSoleNarrowing("--dangerously-skip-permissions", grant));
    }

    [Fact]
    public void RequiresHookAsSoleNarrowing_is_true_for_a_fully_granted_grant_carrying_a_deny_always_pattern()
    {
        // #1732 review F5: same shape, but the standing "never" channel (#390) instead of the allow
        // list -- a write-granted role can still carry a DenyAlways rule (e.g. "never git push
        // --force") that only the hook enforces.
        var grant = new PermissionGrant(
            WriteFiles: true, NetworkAccess: true, RunShellCommands: true,
            DeniedShellCommandPatterns: ["git push --force"]);

        Assert.True(AgyWorkerAdapter.RequiresHookAsSoleNarrowing("--dangerously-skip-permissions", grant));
    }

    [Fact]
    public void ExecutionStreamLoggers_filter_recognises_the_verdict_ledgers_own_file_name()
    {
        // #1732 review sub-threshold: ExecutionStreamLogger (Baton core) cannot take a project
        // reference on Baton.Vendors (Adapter Isolation), so its filter duplicates
        // VerdictLedgerFileName's literal value rather than referencing this constant. This test
        // project references both assemblies, so it is the one place that duplication can be pinned
        // against the real constant -- if the two ever drift, this goes red rather than the ledger
        // silently reappearing in a future directory listing.
        Assert.True(ExecutionStreamLogger.IsStreamLogFileName(AgyWorkerAdapter.VerdictLedgerFileName));
    }

    [Fact]
    public void The_verdict_ledger_path_is_per_execution_so_a_second_executions_hook_never_inherits_the_firsts_verdicts()
    {
        // #1732 review F2's acceptance test. Resolve runs ONCE per binding entry (WorkerInvocation's
        // own doc), so the SAME CoreDispatchTarget below stands in for every execution of this role --
        // exactly the room-wide sharing the old room-scoped ledger path had. What must differ between
        // two executions is BATON_OUTPUT_DIR, which only CoreDispatcher.AssembleChildEnvironment
        // resolves, per dispatch -- so this drives that same expansion twice, with two different
        // per-execution output directories, the way two real dispatches of one role in one room would.
        var target = new AgyWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        var firstOutputDir = Path.Combine(Path.GetTempPath(), $"agy-ledger-exec1-{Guid.NewGuid():N}");
        var secondOutputDir = Path.Combine(Path.GetTempPath(), $"agy-ledger-exec2-{Guid.NewGuid():N}");
        Directory.CreateDirectory(firstOutputDir);
        Directory.CreateDirectory(secondOutputDir);
        try
        {
            var firstEnvironment = CoreDispatcher.AssembleChildEnvironment(MakeExecutionRequest(firstOutputDir), target);
            var secondEnvironment = CoreDispatcher.AssembleChildEnvironment(MakeExecutionRequest(secondOutputDir), target);

            var firstLedgerPath = firstEnvironment.Single(e => e.Name == AgyWorkerAdapter.VerdictLedgerVariable).Value;
            var secondLedgerPath = secondEnvironment.Single(e => e.Name == AgyWorkerAdapter.VerdictLedgerVariable).Value;

            Assert.NotEqual(firstLedgerPath, secondLedgerPath);
            Assert.StartsWith(firstOutputDir, firstLedgerPath, StringComparison.Ordinal);
            Assert.StartsWith(secondOutputDir, secondLedgerPath, StringComparison.Ordinal);

            // The first execution's hook is healthy and appends verdicts to ITS OWN path.
            File.WriteAllLines(firstLedgerPath, ["2026-01-01T00:00:00Z", "2026-01-01T00:00:01Z"]);

            Assert.Equal(2, AgyHookVerdictLedger.CountVerdicts(firstLedgerPath));
            // The second execution's hook wrote nothing to ITS OWN, different path -- unlike the prior
            // room-scoped path, it does not see the first execution's 2 verdicts.
            Assert.Equal(0, AgyHookVerdictLedger.CountVerdicts(secondLedgerPath));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(firstOutputDir);
            DirectoryCleanup.DeleteRecursively(secondOutputDir);
        }
    }

    private static ExecutionRequest MakeExecutionRequest(string outputDirectory) => new(
        new ExecutionId($"exec-{Guid.NewGuid():N}"),
        new WorkflowId("wf-1"),
        new StepId("step-1"),
        "agy",
        Inputs: [],
        Outputs: [],
        Timeout: TimeSpan.FromSeconds(30),
        Environment: ArtifactManager.BuildEnvironment([], outputDirectory, Path.GetTempPath()),
        UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

    [Fact]
    public void RequiresHookAsSoleNarrowing_is_false_for_a_null_grant()
    {
        Assert.False(AgyWorkerAdapter.RequiresHookAsSoleNarrowing("--dangerously-skip-permissions", null));
    }

    // ---- #1680: first-verdict canary primitives ----
    // #1732 review F4: CountToolCallLines (a DONE-only, no-tool_name-required re-implementation of
    // IWorkerUsageParser.CountToolSteps) was deleted along with its two tests here -- the wiring uses
    // the existing, already-in-scope-at-the-call-site usageParser.CountToolSteps instead.

    [Fact]
    public void ProcessAgyHookLivenessProbe_reports_dead_for_a_nonexistent_hook_path_without_spawning_a_process()
    {
        // No subprocess is spawned here at all -- File.Exists short-circuits first -- so this is a
        // real unit test of the shipped probe class, not merely of a fake, while still never touching
        // agy (this task's own "no live agy" constraint).
        var probe = new ProcessAgyHookLivenessProbe();

        var result = probe.Probe(@"C:\definitely\does\not\exist\Baton.Cli.dll", TimeSpan.FromSeconds(1));

        Assert.False(result.IsLive);
        Assert.Contains("does not exist", result.Detail);
    }

    [Fact]
    public void ProcessAgyHookLivenessProbe_reports_live_against_the_real_built_binary()
    {
        // #1732 review F8: the single load-bearing claim of the whole probe -- "with BATON_HOOK_*
        // stripped, the real shipped binary answers deny" -- executed for real, not merely traced by
        // reading. Needs only the built Baton.Cli.dll this suite already asserts is present with its
        // runtimeconfig (The_hook_assembly_carries_its_runtimeconfig_so_dotnet_can_load_it above). With
        // F6 landed this now also spawns through the real cmd/sh form, so this one test covers both.
        // No agy, no vendor spend, no live run. The per-process live-result cache is reset first and
        // the spawn counter asserted after, so this test cannot be served from an entry some earlier
        // test in this class warmed for the same assembly path (#1732 review round 3, Finding A): a
        // test whose whole purpose is executing the real binary must fail if nothing was executed.
        ProcessAgyHookLivenessProbe.ResetCacheForTesting();
        var assemblyPath = Path.Combine(AppContext.BaseDirectory, "Baton.Cli.dll");
        var probe = new ProcessAgyHookLivenessProbe();

        var result = probe.Probe(assemblyPath, TimeSpan.FromSeconds(30));

        Assert.True(result.IsLive, $"expected the real hook to answer deny; got: {result.Detail}");
        Assert.Equal("deny", result.Detail);
        Assert.Equal(1, ProcessAgyHookLivenessProbe.SpawnCountForTesting);
    }

    [Fact]
    public void A_second_resolve_of_the_same_live_path_reuses_the_first_probe_instead_of_spawning_again()
    {
        // #1732 review "Probe cost" (ruled ahead of #1731): two agy roles resolving in the same
        // process under one CLI invocation must not each pay for a cold `cmd /c dotnet …` start for a
        // liveness answer that cannot meaningfully change within one short-lived process.
        // ResetCacheForTesting's own remarks explain why this call is needed before asserting a
        // known-empty starting point.
        ProcessAgyHookLivenessProbe.ResetCacheForTesting();
        var assemblyPath = Path.Combine(AppContext.BaseDirectory, "Baton.Cli.dll");
        var probe = new ProcessAgyHookLivenessProbe();

        var first = probe.Probe(assemblyPath, TimeSpan.FromSeconds(30));
        Assert.True(first.IsLive, $"expected the real hook to answer deny; got: {first.Detail}");
        var afterFirst = ProcessAgyHookLivenessProbe.SpawnCountForTesting;
        Assert.Equal(1, afterFirst);

        var second = probe.Probe(assemblyPath, TimeSpan.FromSeconds(30));

        Assert.Equal(first, second);
        Assert.Equal(afterFirst, ProcessAgyHookLivenessProbe.SpawnCountForTesting);
    }

    [Theory]
    [InlineData("""{"decision":"deny","reason":"x"}""", true)]
    [InlineData("""{"decision":"allow"}""", false)]
    [InlineData("""{"decision":"maybe"}""", false)]
    [InlineData("""{"notADecision":true}""", false)]
    [InlineData("not json", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ProcessAgyHookLivenessProbe_Evaluate_reads_only_an_explicit_deny_as_live(string? stdout, bool expectedLive)
    {
        var result = ProcessAgyHookLivenessProbe.Evaluate(stdout);

        Assert.Equal(expectedLive, result.IsLive);
    }

    /// <summary>
    /// #1166: the agy side of <see cref="ClaudeWorkerAdapterTests.An_unseen_project_directory_is_refused_before_any_worker_spawns"/>
    /// -- see that test's own doc for the red-first claim both arms make against the pre-#1166 behaviour.
    /// </summary>
    [Fact]
    public void An_unseen_project_directory_is_refused_before_any_worker_spawns()
    {
        var unseenProject = Path.Combine(Path.GetTempPath(), $"baton-ceiling-unseen-agy-{Guid.NewGuid():N}");

        var ex = Assert.Throws<ProjectNotTrustedException>(() => new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", WorkingDirectory: unseenProject), ArchitectContract));

        Assert.Equal(unseenProject, ex.ProjectPath);
        Assert.NotNull(ex.TryInvocation);
        Assert.Contains("baton trust", ex.TryInvocation, StringComparison.Ordinal);
    }

    /// <summary>
    /// #1166: effective grant = role grant ∩ project ceiling. Kept out of the
    /// <c>--dangerously-skip-permissions</c> shape deliberately (RunShellCommands/NetworkAccess both
    /// stay false on the role grant) -- that shape also arms the hook-liveness probe
    /// (<see cref="AgyWorkerAdapter.RequiresHookAsSoleNarrowing"/>), which is a different concern this
    /// test does not need to pay for. Uses a contract with no declared outputs, deliberately: capping
    /// WriteFiles away on agy while a contract declares outputs now refuses
    /// (<see cref="A_ceiling_that_caps_away_write_files_refuses_a_contract_declaring_outputs_on_agy"/>,
    /// #1166 review finding B) -- that is a different, deliberately separate concern from the plain
    /// intersection this test asserts.
    /// </summary>
    [Fact]
    public void A_ceiling_below_the_role_grant_caps_the_effective_grant_to_the_intersection()
    {
        var project = Path.Combine(Path.GetTempPath(), $"baton-ceiling-cap-agy-{Guid.NewGuid():N}");
        ProjectCeilingStore.Set(
            project,
            new ProjectCeiling(ReadFiles: true, WriteFiles: false, RunShellCommands: true, NetworkAccess: true),
            ProjectCeilingStore.DefaultPath);
        var roleGrant = new PermissionGrant(ReadFiles: true, WriteFiles: true);
        var noOutputsContract = new WorkerContract("architect", ["goal"], [], []);

        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: roleGrant, WorkingDirectory: project),
            noOutputsContract);

        // The capped grant no longer grants writes, so the mode flag must reflect "plan", not
        // "accept-edits" -- the translation path a still-WriteFiles:true grant would have taken.
        Assert.Equal("plan", ArgValue(target, "--mode"));

        var denied = target.Environment!.Single(v => v.Name == AgyWorkerAdapter.DeniedToolsVariable).Value;
        Assert.Contains("write_to_file", denied);
    }

    /// <summary>
    /// #1166 review finding A -- the agy side of
    /// <see cref="ClaudeWorkerAdapterTests.A_worktree_dispatch_keys_the_ceiling_on_the_source_repository_not_the_ephemeral_worktree_path"/>,
    /// same claim, same both-directions assertion.
    /// </summary>
    [Fact]
    public void A_worktree_dispatch_keys_the_ceiling_on_the_source_repository_not_the_ephemeral_worktree_path()
    {
        var sourceRepo = Path.Combine(Path.GetTempPath(), $"baton-ceiling-worktree-src-agy-{Guid.NewGuid():N}");
        var worktreePath = Path.Combine(Path.GetTempPath(), $"baton-ceiling-worktree-tree-agy-{Guid.NewGuid():N}");
        ProjectCeilingStore.Set(sourceRepo, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);

        var target = new AgyWorkerAdapter().Resolve(
            new WorkerInvocation(
                "Draft a plan.", WorkingDirectory: worktreePath, WorktreeSourceRepository: sourceRepo),
            ArchitectContract);

        Assert.Equal(worktreePath, target.WorkingDirectory);

        var untrustedWorktreePath = Path.Combine(Path.GetTempPath(), $"baton-ceiling-worktree-tree2-agy-{Guid.NewGuid():N}");
        ProjectCeilingStore.Set(untrustedWorktreePath, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);
        var otherSourceRepo = Path.Combine(Path.GetTempPath(), $"baton-ceiling-worktree-src2-agy-{Guid.NewGuid():N}");

        var ex = Assert.Throws<ProjectNotTrustedException>(() => new AgyWorkerAdapter().Resolve(
            new WorkerInvocation(
                "Draft a plan.", WorkingDirectory: untrustedWorktreePath, WorktreeSourceRepository: otherSourceRepo),
            ArchitectContract));
        Assert.Equal(otherSourceRepo, ex.ProjectPath);
    }

    /// <summary>
    /// #1166 review finding B: on agy a withheld write does NOT reach the outbox (#670,
    /// <c>WithheldWritesReachTheOutbox</c> defaults false) -- so a ceiling that caps WriteFiles away
    /// from a role grant that had it, over a contract declaring outputs, must refuse here rather than
    /// let a worker that cannot write its declared output run to completion and pay for itself before
    /// failing the contract check (#629). See
    /// <see cref="ClaudeWorkerAdapterTests.A_ceiling_that_caps_away_write_files_does_not_refuse_the_contract_on_claude"/>
    /// for the polarity partner.
    /// </summary>
    [Fact]
    public void A_ceiling_that_caps_away_write_files_refuses_a_contract_declaring_outputs_on_agy()
    {
        var project = Path.Combine(Path.GetTempPath(), $"baton-ceiling-unsatisfiable-agy-{Guid.NewGuid():N}");
        ProjectCeilingStore.Set(
            project,
            new ProjectCeiling(ReadFiles: true, WriteFiles: false, RunShellCommands: false, NetworkAccess: false),
            ProjectCeilingStore.DefaultPath);
        var roleGrant = new PermissionGrant(ReadFiles: true, WriteFiles: true);

        var ex = Assert.Throws<UnsatisfiableOutputContractException>(() => new AgyWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: roleGrant, WorkingDirectory: project),
            ArchitectContract));

        Assert.Equal("architect", ex.WorkerName);
        Assert.Contains("plan.md", ex.UnwritableOutputs);
    }
}
