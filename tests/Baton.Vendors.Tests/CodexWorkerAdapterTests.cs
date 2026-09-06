using Baton.Dispatch;
using Baton.Domain;
using Baton.Outcomes;
using Baton.Tests.Shared;
using System.Text.Json;

namespace Baton.Vendors.Tests;

[Collection(LaunchConfigCollection.Name)]
public sealed class CodexWorkerAdapterTests
{
    private static readonly WorkerContract SingleOutputContract = new(
        "architect", ["goal"], [new ProducedOutput("plan.md")], []);

    private static readonly WorkerContract NoOutputContract = new(
        "chat", [], [], []);

    private const string OutputDirectory = "%BATON_OUTPUT_DIR%";

    private const char DirectorySeparator = '\\';

    [Fact]
    public void Windows_npm_shim_resolves_to_its_native_platform_binary_without_a_shell()
    {
        var root = Path.Combine(Path.GetTempPath(), $"baton-codex-resolver-{Guid.NewGuid():N}");
        var native = Path.Combine(
            root, "node_modules", "@openai", "codex", "node_modules", "@openai", "codex-win32-x64",
            "vendor", "x86_64-pc-windows-msvc", "bin", "codex.exe");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(native)!);
            File.WriteAllText(Path.Combine(root, "codex.cmd"), "synthetic shim");
            File.WriteAllText(native, "synthetic native binary");

            var resolved = CodexExecutableResolver.Resolve(
                root, System.Runtime.InteropServices.Architecture.X64, isWindows: true);

            Assert.Equal(native, resolved);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                DirectoryCleanup.DeleteRecursively(root);
            }
        }
    }

    [Fact]
    public void A_direct_windows_executable_wins_at_its_path_position()
    {
        var root = Path.Combine(Path.GetTempPath(), $"baton-codex-direct-{Guid.NewGuid():N}");
        var direct = Path.Combine(root, "codex.exe");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(direct, "synthetic native binary");

            var resolved = CodexExecutableResolver.Resolve(
                root, System.Runtime.InteropServices.Architecture.X64, isWindows: true);

            Assert.Equal(direct, resolved);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                DirectoryCleanup.DeleteRecursively(root);
            }
        }
    }

    [Fact]
    public void Unsupported_or_missing_native_install_falls_back_to_the_actionable_program_name()
    {
        Assert.Equal(
            "codex",
            CodexExecutableResolver.Resolve(
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
                System.Runtime.InteropServices.Architecture.X64,
                isWindows: true));
        Assert.Equal(
            "codex",
            CodexExecutableResolver.Resolve("ignored", System.Runtime.InteropServices.Architecture.X64, isWindows: false));
    }

    [Fact]
    public void New_turn_targets_codex_exec_with_the_prompt_last()
    {
        var target = new CodexWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan."), NoOutputContract);

        Assert.Contains(Path.GetFileName(target.Program), new[] { "codex", "codex.exe" });
        Assert.Equal("exec", target.Args[0]);
        Assert.Equal("read-only", ArgValue(target, "--sandbox"));
        Assert.Contains("--json", target.Args);
        Assert.Contains("--ignore-user-config", target.Args);
        Assert.Contains("--skip-git-repo-check", target.Args);
        Assert.DoesNotContain("resume", target.Args);
        Assert.Equal("Draft a plan.", target.Args[^1]);
        Assert.Equal(target.PromptText, target.Args[^1]);
        Assert.Equal("Draft a plan.", target.PromptText);
        Assert.NotNull(target.OversizePromptWrapper);
        Assert.Contains("%BATON_PROMPT_FILE%", target.OversizePromptWrapper);
    }

    [Fact]
    public void Resume_turn_places_common_exec_options_before_resume_then_session_and_prompt()
    {
        const string sessionId = "00000000-0000-0000-0000-000000000001";
        var target = new CodexWorkerAdapter().Resolve(
            new WorkerInvocation(
                "Continue the work.",
                SessionId: sessionId,
                ResumeSession: true),
            NoOutputContract);

        var resumeIndex = IndexOf(target, "resume");
        Assert.True(resumeIndex > IndexOf(target, "--skip-git-repo-check"));
        Assert.Equal(sessionId, target.Args[resumeIndex + 1]);
        Assert.Equal("Continue the work.", target.Args[resumeIndex + 2]);
        Assert.Equal(resumeIndex + 3, target.Args.Count);
        Assert.Equal(target.PromptText, target.Args[^1]);
    }

    [Fact]
    public void Session_id_without_resume_does_not_emit_a_resume_subcommand()
    {
        var target = new CodexWorkerAdapter().Resolve(
            new WorkerInvocation(
                "Start fresh.",
                SessionId: "00000000-0000-0000-0000-000000000009",
                ResumeSession: false),
            NoOutputContract);

        Assert.DoesNotContain("resume", target.Args);
        Assert.DoesNotContain("00000000-0000-0000-0000-000000000009", target.Args);
    }

    [Fact]
    public void Prompt_and_archived_prompt_are_identical_and_name_all_contract_paths()
    {
        var contract = new WorkerContract(
            "implementor",
            ["goal", "brief"],
            [new ProducedOutput("patch.diff"), new ProducedOutput("notes.md")],
            []);

        var target = new CodexWorkerAdapter().Resolve(
            new WorkerInvocation("Implement exactly this change."), contract);

        Assert.Equal(target.PromptText, target.Args[^1]);
        Assert.StartsWith("Implement exactly this change.", target.PromptText);
        Assert.Contains("goal: %BATON_INPUT_0%", target.PromptText);
        Assert.Contains("brief: %BATON_INPUT_1%", target.PromptText);
        Assert.Contains($"patch.diff: {OutputDirectory}{DirectorySeparator}patch.diff", target.PromptText);
        Assert.Contains($"notes.md: {OutputDirectory}{DirectorySeparator}notes.md", target.PromptText);
    }

    [Fact]
    public void Single_output_contract_emits_one_output_last_message_path()
    {
        var target = new CodexWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan."), SingleOutputContract);

        var outputFlags = target.Args.Count(arg => arg is "-o" or "--output-last-message");
        Assert.Equal(1, outputFlags);
        Assert.Equal(
            $"{OutputDirectory}{DirectorySeparator}plan.md",
            ArgValue(target, "-o") ?? ArgValue(target, "--output-last-message"));
    }

    [Fact]
    public void Multiple_outputs_do_not_emit_the_single_output_option()
    {
        var contract = new WorkerContract(
            "implementor", [], [new ProducedOutput("patch.diff"), new ProducedOutput("notes.md")], []);

        var target = new CodexWorkerAdapter().Resolve(
            new WorkerInvocation("Implement."), contract);

        Assert.DoesNotContain("-o", target.Args);
        Assert.DoesNotContain("--output-last-message", target.Args);
    }

    [Fact]
    public void Broker_supports_withheld_writes_for_every_declared_output_shape()
    {
        Assert.True(new CodexWorkerAdapter().WithheldWritesReachTheOutbox);
    }

    [Theory]
    [MemberData(nameof(BrokeredGrants))]
    public void Structured_grants_route_through_the_baton_broker(PermissionGrant grant)
    {
        var adapter = new CodexWorkerAdapter();

        Assert.True(adapter.TryTranslatePermissionGrant(grant, out var mode, out var reason));
        Assert.Equal("baton-broker", mode);
        Assert.Null(reason);
        var target = adapter.Resolve(
            new WorkerInvocation("Review.", PermissionGrant: grant), SingleOutputContract);
        Assert.Equal("dotnet", target.Program);
        Assert.Equal("codex-broker", target.Args[1]);
        Assert.Equal(target.PromptText, target.Args[^1]);
    }

    public static TheoryData<PermissionGrant> BrokeredGrants => new()
    {
        new PermissionGrant(),
        new PermissionGrant(ReadFiles: true),
        new PermissionGrant(ReadFiles: true, WriteFiles: true),
        new PermissionGrant(ReadFiles: true, WriteFiles: true, RunShellCommands: true),
    };

    [Fact]
    public void Granted_workspace_writes_root_at_the_project_and_add_only_baton_output_roots()
    {
        var project = Path.Combine(Path.GetTempPath(), $"baton-codex-write-{Guid.NewGuid():N}");
        ProjectCeilingStore.Set(project, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);
        var grant = new PermissionGrant(
            ReadFiles: true,
            WriteFiles: true,
            RunShellCommands: true);

        var target = new CodexWorkerAdapter().Resolve(
            new WorkerInvocation("Implement.", PermissionGrant: grant, WorkingDirectory: project),
            SingleOutputContract);

        Assert.Equal("dotnet", target.Program);
        Assert.Equal("codex-broker", target.Args[1]);
        Assert.Equal(project, target.WorkingDirectory);
        Assert.True(BrokerConfiguration(target).PermissionGrant.WriteFiles);
        Assert.True(BrokerConfiguration(target).PermissionGrant.RunShellCommands);
    }

    [Fact]
    public void Network_grant_enables_sandbox_network_and_live_web_search()
    {
        var grant = new PermissionGrant(
            ReadFiles: true, WriteFiles: true, RunShellCommands: true, NetworkAccess: true);

        var target = new CodexWorkerAdapter().Resolve(
            new WorkerInvocation("Research.", PermissionGrant: grant), NoOutputContract);

        Assert.True(BrokerConfiguration(target).PermissionGrant.NetworkAccess);
    }

    [Fact]
    public void Withheld_network_disables_sandbox_network_and_web_search()
    {
        var grant = new PermissionGrant(ReadFiles: true, WriteFiles: true, RunShellCommands: true);
        var target = new CodexWorkerAdapter().Resolve(
            new WorkerInvocation("Inspect.", PermissionGrant: grant), NoOutputContract);

        Assert.False(BrokerConfiguration(target).PermissionGrant.NetworkAccess);
    }

    [Fact]
    public void External_side_effect_features_are_disabled_for_every_worker()
    {
        var target = new CodexWorkerAdapter().Resolve(
            new WorkerInvocation("Inspect."), NoOutputContract);

        var disabled = ArgValues(target, "--disable");
        Assert.Contains("apps", disabled);
        Assert.Contains("browser_use", disabled);
        Assert.Contains("computer_use", disabled);
        Assert.Contains("image_generation", disabled);
    }

    [Fact]
    public void Subagents_are_disabled_by_default()
    {
        var target = new CodexWorkerAdapter().Resolve(
            new WorkerInvocation("Inspect."), NoOutputContract);

        Assert.Contains("multi_agent", ArgValues(target, "--disable"));
        Assert.Contains("multi_agent_v2", ArgValues(target, "--disable"));
    }

    [Fact]
    public void Explicit_subagent_grant_omits_both_multi_agent_disable_switches()
    {
        var target = new CodexWorkerAdapter().Resolve(
            new WorkerInvocation("Conduct.", AllowsSubagents: true), NoOutputContract);

        Assert.DoesNotContain("multi_agent", ArgValues(target, "--disable"));
        Assert.DoesNotContain("multi_agent_v2", ArgValues(target, "--disable"));
    }

    public static TheoryData<PermissionGrant> PatternGrants => new()
    {
        new PermissionGrant(ReadFiles: true, RunShellCommands: true, ShellCommandPatterns: ["git diff*"], ShellCommandsAreReadOnly: true),
        new PermissionGrant(ReadFiles: true, RunShellCommands: true, DeniedShellCommandPatterns: ["git push*"]),
        new PermissionGrant(ReadFiles: true, RunShellCommands: true, DeniedShellOptionTokens: ["--output"]),
    };

    [Theory]
    [MemberData(nameof(PatternGrants))]
    public void Pattern_and_option_scoped_shell_permissions_route_to_the_broker(PermissionGrant grant)
    {
        var adapter = new CodexWorkerAdapter();

        Assert.True(adapter.TryTranslatePermissionGrant(grant, out var mode, out var reason));
        Assert.Equal("baton-broker", mode);
        Assert.Null(reason);
        var target = adapter.Resolve(new WorkerInvocation("Inspect.", PermissionGrant: grant), NoOutputContract);
        var roundTripped = BrokerConfiguration(target).PermissionGrant;
        Assert.Equal(grant.ReadFiles, roundTripped.ReadFiles);
        Assert.Equal(grant.WriteFiles, roundTripped.WriteFiles);
        Assert.Equal(grant.RunShellCommands, roundTripped.RunShellCommands);
        Assert.Equal(grant.NetworkAccess, roundTripped.NetworkAccess);
        Assert.Equal(grant.ShellCommandsAreReadOnly, roundTripped.ShellCommandsAreReadOnly);
        Assert.Equal(grant.ShellCommandPatterns ?? [], roundTripped.ShellCommandPatterns ?? []);
        Assert.Equal(grant.DeniedShellCommandPatterns ?? [], roundTripped.DeniedShellCommandPatterns ?? []);
        Assert.Equal(grant.DeniedShellOptionTokens ?? [], roundTripped.DeniedShellOptionTokens ?? []);
    }

    [Fact]
    public void Read_and_write_permission_without_execution_tools_is_brokered_without_a_command_tool()
    {
        var grant = new PermissionGrant(ReadFiles: true, WriteFiles: true, RunShellCommands: false);
        var adapter = new CodexWorkerAdapter();

        Assert.True(adapter.TryTranslatePermissionGrant(grant, out var mode, out var reason));
        Assert.Equal("baton-broker", mode);
        Assert.Null(reason);
        var target = adapter.Resolve(
            new WorkerInvocation("Implement.", PermissionGrant: grant), SingleOutputContract);
        Assert.False(BrokerConfiguration(target).PermissionGrant.RunShellCommands);
    }

    [Fact]
    public void Unrestricted_and_dangerous_raw_permission_scopes_are_refused()
    {
        var adapter = new CodexWorkerAdapter();

        var exception = Assert.Throws<PermissionGrantUnsupportedException>(
            () => adapter.Resolve(
                new WorkerInvocation("Implement.", PermissionScope: "danger-full-access"),
                NoOutputContract));

        Assert.Equal("codex", exception.AdapterName);
        Assert.Contains("read-only", exception.Message);
        Assert.Contains("workspace-write", exception.Message);
    }

    [Theory]
    [InlineData("quick", "low")]
    [InlineData("standard", "medium")]
    [InlineData("careful", "high")]
    [InlineData("exhaustive", "max")]
    [InlineData("xhigh", "xhigh")]
    public void Model_and_effort_are_emitted_with_canonical_translation(
        string requestedEffort,
        string expectedEffort)
    {
        var target = new CodexWorkerAdapter().Resolve(
            new WorkerInvocation(
                "Implement.",
                Model: "gpt-5.6-sol",
                Effort: requestedEffort),
            NoOutputContract);

        Assert.Equal("gpt-5.6-sol", ArgValue(target, "--model"));
        Assert.Contains($"model_reasoning_effort=\"{expectedEffort}\"", ArgValues(target, "--config"));
    }

    [Fact]
    public void Ultra_is_independent_of_subagent_permission_and_features_remain_disabled()
    {
        var target = new CodexWorkerAdapter().Resolve(
            new WorkerInvocation(
                "Implement.",
                Model: "gpt-5.6-sol",
                Effort: "ultra",
                AllowsSubagents: false),
            NoOutputContract);

        Assert.Contains("model_reasoning_effort=\"ultra\"", ArgValues(target, "--config"));
        Assert.Contains("multi_agent", ArgValues(target, "--disable"));
        Assert.Contains("multi_agent_v2", ArgValues(target, "--disable"));
    }

    [Fact]
    public void Ultra_is_emitted_when_the_model_supports_it_and_subagents_are_allowed()
    {
        var target = new CodexWorkerAdapter().Resolve(
            new WorkerInvocation(
                "Conduct.",
                Model: "gpt-5.6-sol",
                Effort: "ultra",
                AllowsSubagents: true),
            NoOutputContract);

        Assert.Contains("model_reasoning_effort=\"ultra\"", ArgValues(target, "--config"));
        Assert.DoesNotContain("multi_agent", ArgValues(target, "--disable"));
        Assert.DoesNotContain("multi_agent_v2", ArgValues(target, "--disable"));
    }

    [Fact]
    public void Ultra_is_refused_for_a_known_model_that_does_not_advertise_it()
    {
        var exception = Assert.Throws<IncoherentVendorEffortException>(
            () => new CodexWorkerAdapter().Resolve(
                new WorkerInvocation(
                    "Conduct.",
                    Model: "gpt-5.6-luna",
                    Effort: "ultra",
                    AllowsSubagents: true),
                NoOutputContract));

        Assert.Contains("does not advertise 'ultra'", exception.Message);
    }

    [Fact]
    public void Ultra_is_refused_for_codex_spark_which_only_advertises_through_xhigh()
    {
        var exception = Assert.Throws<IncoherentVendorEffortException>(
            () => new CodexWorkerAdapter().Resolve(
                new WorkerInvocation(
                    "Conduct.",
                    Model: "gpt-5.3-codex-spark",
                    Effort: "ultra",
                    AllowsSubagents: true),
                NoOutputContract));

        Assert.Contains("does not advertise 'ultra'", exception.Message);
    }

    [Fact]
    public void Unknown_effort_is_refused_before_dispatch()
    {
        Assert.Throws<IncoherentVendorEffortException>(
            () => new CodexWorkerAdapter().Resolve(
                new WorkerInvocation("Inspect.", Model: "gpt-5.6-sol", Effort: "turbo"),
                NoOutputContract));
    }

    [Fact]
    public void Unknown_future_model_is_rejected_by_the_current_capability_snapshot()
    {
        var exception = Assert.Throws<IncoherentVendorEffortException>(
            () => new CodexWorkerAdapter().Resolve(
                new WorkerInvocation("Inspect.", Model: "gpt-future", Effort: "high"),
                NoOutputContract));

        Assert.Contains("absent from the recorded Codex capability snapshot", exception.Message);
        Assert.Contains("codex-model-list-2026-09-04.jsonl", exception.Message);

        // #1880: the refusal names which CLI's catalog said so, read from the recording's own
        // initialize line rather than restated here — the file's name already carries the date.
        Assert.Contains("codex-cli 0.153.2", exception.Message);
    }

    /// <summary>
    /// #1875: the validation table is derived from the embedded recording, so these are values a reader
    /// checks against that file by eye rather than by re-running the parser the table itself uses.
    /// Why `gpt-6-astra` keeps `ultra` despite the vendor's web page: `docs/vendor-capabilities.md`.
    /// </summary>
    [Fact]
    public void Astra_accepts_ultra_because_the_recorded_catalog_advertises_it()
    {
        var target = new CodexWorkerAdapter().Resolve(
            new WorkerInvocation("Conduct.", Model: "gpt-6-astra", Effort: "ultra", AllowsSubagents: true),
            NoOutputContract);

        Assert.Equal("gpt-6-astra", ArgValue(target, "--model"));
        Assert.Contains("model_reasoning_effort=\"ultra\"", ArgValues(target, "--config"));
    }

    [Fact]
    public void Luna_rejection_lists_exactly_the_efforts_the_recording_advertises_for_it()
    {
        var exception = Assert.Throws<IncoherentVendorEffortException>(
            () => new CodexWorkerAdapter().Resolve(
                new WorkerInvocation("Conduct.", Model: "gpt-5.6-luna", Effort: "ultra"),
                NoOutputContract));

        Assert.Contains("(available: low, medium, high, xhigh, max)", exception.Message);
    }

    /// <summary>
    /// The polarity partner of the two tests above and the behaviour change #1875 shipped: `gpt-5.4`
    /// was in the hand-written table but is not in the 2026-09-04 visible catalog, so deriving the
    /// table from the recording refuses it locally instead of sending it to fail at the vendor.
    /// `gpt-5.4-mini`, which the recording does carry, still resolves.
    /// </summary>
    [Fact]
    public void A_model_the_recording_does_not_carry_is_refused_while_its_mini_sibling_resolves()
    {
        Assert.Throws<IncoherentVendorEffortException>(
            () => new CodexWorkerAdapter().Resolve(
                new WorkerInvocation("Inspect.", Model: "gpt-5.4", Effort: "high"),
                NoOutputContract));

        var target = new CodexWorkerAdapter().Resolve(
            new WorkerInvocation("Inspect.", Model: "gpt-5.4-mini", Effort: "high"),
            NoOutputContract);

        Assert.Equal("gpt-5.4-mini", ArgValue(target, "--model"));
    }

    [Fact]
    public void The_recorded_capability_snapshot_ships_inside_the_vendors_assembly()
    {
        using var stream = typeof(CodexWorkerAdapter).Assembly
            .GetManifestResourceStream(CodexWorkerAdapter.ModelCatalogResourceName);

        Assert.NotNull(stream);
    }

    /// <summary>
    /// Every way a recording can be unusable, against the positive arm above: none may degrade to an
    /// empty table, because an empty one would still fail closed while blaming the model instead of the
    /// recording. `[]` is the case that would otherwise pass silently — it parses, and it is a valid
    /// `model/list` result shape.
    /// </summary>
    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"id\":1,\"result\":{\"data\":[]}}")]
    [InlineData("{\"id\":2,\"result\":{\"data\":[]}}")]
    [InlineData("{\"id\":2,\"result\":{\"data\":[{\"model\":\"gpt-6-astra\"}]}}")]
    public void An_unusable_recording_throws_rather_than_yielding_an_empty_table(string rawLine)
    {
        var exception = Assert.Throws<VendorCapabilitySnapshotException>(
            () => CodexWorkerAdapter.BuildEffortTable(rawLine, "synthetic-recording.jsonl"));

        Assert.Equal("codex", exception.AdapterName);
        Assert.Equal("synthetic-recording.jsonl", exception.ResourceName);
        Assert.Contains("synthetic-recording.jsonl", exception.Message);
    }

    /// <summary>
    /// The shipped shape: the raw three-line app-server session, notification and initialize line
    /// included. The loader must walk past both to the catalog line — and read the CLI version out of
    /// the initialize line while it passes, which is the whole reason the file is kept whole (#1880).
    /// </summary>
    [Fact]
    public void The_raw_recording_builds_the_table_it_advertises_and_names_the_cli_that_answered()
    {
        var recorded = CodexWorkerAdapter.BuildEffortTable(RecordedRecording(), "recording.jsonl");

        Assert.Equal(["low", "medium", "high", "xhigh", "max", "ultra"], recorded.EffortsByModel["gpt-6-astra"]);
        Assert.Equal(["low", "medium", "high", "xhigh", "max"], recorded.EffortsByModel["gpt-5.6-luna"]);
        Assert.False(recorded.EffortsByModel.ContainsKey("gpt-5.4"));
        Assert.Equal("0.153.2", recorded.CliVersion);
    }

    /// <summary>
    /// The polarity partner: a file trimmed to the catalog line alone — what shipped before #1880 —
    /// still yields the identical table, so line-iterating widened what the loader accepts rather than
    /// moving it. It has no initialize line, so the version is absent rather than invented.
    /// </summary>
    [Fact]
    public void A_result_only_recording_builds_the_same_table_with_no_version_to_name()
    {
        var raw = CodexWorkerAdapter.BuildEffortTable(RecordedRecording(), "recording.jsonl");
        var resultOnly = CodexWorkerAdapter.BuildEffortTable(RecordedModelListLine(), "result-only.jsonl");

        Assert.Equal(raw.EffortsByModel, resultOnly.EffortsByModel);
        Assert.Null(resultOnly.CliVersion);
    }

    /// <summary>
    /// A well-formed session that simply never answered `model/list` is the case line-iterating makes
    /// newly reachable: every line parses and none is a catalog, so it must refuse loudly rather than
    /// let "skip lines that do not qualify" skip all of them into an empty table.
    /// </summary>
    [Fact]
    public void A_recording_whose_lines_all_fail_to_qualify_is_refused_rather_than_emptied()
    {
        var exception = Assert.Throws<VendorCapabilitySnapshotException>(
            () => CodexWorkerAdapter.BuildEffortTable(
                "{\"id\":1,\"result\":{\"userAgent\":\"baton-conductor/0.153.2 (Windows)\"}}\n"
                + "{\"method\":\"remoteControl/status/changed\",\"params\":{\"status\":\"disabled\"}}\n",
                "no-catalog.jsonl"));

        Assert.Contains("no `model/list` result line", exception.Message);
    }

    [Fact]
    public void Explicit_effort_without_a_model_is_refused_before_dispatch()
    {
        var exception = Assert.Throws<IncoherentVendorEffortException>(
            () => new CodexWorkerAdapter().Resolve(
                new WorkerInvocation("Inspect.", Effort: "high"),
                NoOutputContract));

        Assert.Contains("requires an explicit model", exception.Message);
    }

    [Fact]
    public void Success_fixture_exposes_session_progress_final_response_and_terminal_usage()
    {
        var adapter = new CodexWorkerAdapter();
        var lines = FixtureLines("codex-exec-success.jsonl");

        Assert.True(adapter.TryParseSessionId(lines[0], out var sessionId));
        Assert.Equal("00000000-0000-0000-0000-000000000001", sessionId);
        AssertProgress(adapter, lines[0], "status", "Session started");
        AssertProgress(adapter, lines[1], "status", "Turn started");
        AssertProgress(adapter, lines[2], "text", "SYNTHETIC_OK");
        Assert.True(adapter.TryParseFinalResponse(lines[2], out var response));
        Assert.Equal("SYNTHETIC_OK", response);
        AssertProgress(adapter, lines[3], "result", "success");
        Assert.True(adapter.TryParseFinalUsage(lines[3], out var usage));
        Assert.Equal(5790, usage!.TokensIn);
        Assert.Equal(8960, usage.CacheReadTokens);
        Assert.Equal(11, usage.TokensOut);
    }

    [Fact]
    public void Resume_fixture_preserves_the_thread_id_and_returns_the_new_message()
    {
        var adapter = new CodexWorkerAdapter();
        var lines = FixtureLines("codex-exec-resume-success.jsonl");

        Assert.True(adapter.TryParseSessionId(lines[0], out var sessionId));
        Assert.Equal("00000000-0000-0000-0000-000000000001", sessionId);
        Assert.True(adapter.TryParseFinalResponse(lines[2], out var response));
        Assert.Equal("SYNTHETIC_RESUME_OK", response);
        Assert.True(adapter.TryParseFinalUsage(lines[3], out var usage));
        Assert.Equal(8571, usage!.TokensIn);
        Assert.Equal(11008, usage.CacheReadTokens);
        Assert.Equal(9, usage.TokensOut);
    }

    [Fact]
    public void Failed_and_top_level_error_fixtures_surface_error_progress()
    {
        var adapter = new CodexWorkerAdapter();
        var failed = FixtureLines("codex-exec-turn-failed.jsonl");
        var error = FixtureLines("codex-exec-error.jsonl");

        AssertProgress(adapter, failed[^1], "result", "error — SYNTHETIC_TURN_FAILURE");
        AssertProgress(adapter, error[^1], "result", "error — SYNTHETIC_TOP_LEVEL_ERROR");
    }

    [Fact]
    public void Unrelated_and_malformed_lines_are_not_session_progress_or_final_response()
    {
        var adapter = new CodexWorkerAdapter();

        Assert.False(adapter.TryParseSessionId("not json", out var sessionId));
        Assert.Null(sessionId);
        Assert.False(adapter.TryParseProgressEvent("not json", out var progress));
        Assert.Null(progress);
        Assert.False(adapter.TryParseFinalResponse("{\"type\":\"turn.completed\"}", out var response));
        Assert.Null(response);
    }

    [Fact]
    public void Terminal_detectors_distinguish_success_from_any_terminal_result()
    {
        var adapter = new CodexWorkerAdapter();
        var target = adapter.Resolve(new WorkerInvocation("Inspect."), NoOutputContract);
        var success = FixtureLines("codex-exec-success.jsonl")[^1];
        var failed = FixtureLines("codex-exec-turn-failed.jsonl")[^1];
        var error = FixtureLines("codex-exec-error.jsonl")[^1];

        Assert.NotNull(target.DetectsTerminalSuccess);
        Assert.NotNull(target.DetectsTerminalResult);
        Assert.True(target.DetectsTerminalSuccess!(success));
        Assert.False(target.DetectsTerminalSuccess!(failed));
        Assert.False(target.DetectsTerminalSuccess!(error));
        Assert.True(target.DetectsTerminalResult!(success));
        Assert.True(target.DetectsTerminalResult!(failed));
        Assert.True(target.DetectsTerminalResult!(error));
        Assert.False(target.DetectsTerminalResult!("not json"));
        Assert.True(adapter.IsPostResponseTerminalLine(success));
        Assert.False(adapter.IsPostResponseTerminalLine(failed));
        Assert.False(adapter.IsPostResponseTerminalLine(error));
        Assert.False(adapter.IsPostResponseTerminalLine("stray trailing output"));
    }

    [Fact]
    public void Tool_denial_fixture_counts_only_the_started_command_and_keeps_completed_turn_success()
    {
        var adapter = new CodexWorkerAdapter();
        var lines = FixtureLines("codex-exec-tool-denied-completes.jsonl");

        Assert.Equal("synthetic-write-command", adapter.TryParseToolName(lines[2]));
        Assert.Equal(1, adapter.CountToolSteps(lines[2]));
        Assert.Equal(0, adapter.CountToolSteps(lines[3]));
        Assert.Equal(0, adapter.CountToolSteps(lines[4]));
        AssertProgress(adapter, lines[2], "tool", "synthetic-write-command");
        AssertProgress(
            adapter, lines[3], "tool",
            "synthetic-write-command failed — SYNTHETIC: write rejected because the managed host retained a read-only sandbox.");
        AssertProgress(adapter, lines[4], "text", "SYNTHETIC_TOOL_DENIAL_REPORTED");
        AssertProgress(adapter, lines[5], "result", "success");

        Assert.False(adapter.TryClassifySatisfiedRunFailure(
            null,
            string.Join(Environment.NewLine, lines),
            TimeProvider.System,
            out var classification,
            out var retryNotBefore));
        Assert.Null(classification);
        Assert.Null(retryNotBefore);
    }

    [Theory]
    [InlineData("{\"type\":\"item.started\",\"item\":{\"type\":\"file_change\"}}", "file change")]
    [InlineData("{\"type\":\"item.started\",\"item\":{\"type\":\"mcp_tool_call\",\"tool\":\"baton.yield\"}}", "baton.yield")]
    [InlineData("{\"type\":\"item.started\",\"item\":{\"type\":\"web_search\"}}", "web search")]
    public void Each_supported_started_tool_item_counts_one_step(string line, string expectedTool)
    {
        var adapter = new CodexWorkerAdapter();

        Assert.Equal(expectedTool, adapter.TryParseToolName(line));
        Assert.Equal(1, adapter.CountToolSteps(line));
        AssertProgress(adapter, line, "tool", expectedTool);
    }

    [Fact]
    public void Brokered_live_fixtures_preserve_tool_steps_per_turn_usage_and_resume_cache_miss()
    {
        var adapter = new CodexWorkerAdapter();
        var initial = FixtureLines("codex-app-server-broker-readonly-success.jsonl");
        var resumed = FixtureLines("codex-app-server-broker-resume-cache-miss.jsonl");

        Assert.Equal(3, initial.Sum(adapter.CountToolSteps));
        Assert.Equal(1, resumed.Sum(adapter.CountToolSteps));
        Assert.True(adapter.TryParseFinalUsage(initial[^1], out var initialUsage));
        Assert.Equal(579, initialUsage!.TokensIn);
        Assert.Equal(23296, initialUsage.CacheReadTokens);
        Assert.True(adapter.TryParseFinalUsage(resumed[^1], out var resumedUsage));
        Assert.Equal(26379, resumedUsage!.TokensIn);
        Assert.Equal(0, resumedUsage.CacheReadTokens);
        Assert.NotEqual(
            (initialUsage.TokensIn ?? 0) + (initialUsage.CacheReadTokens ?? 0) + (resumedUsage.TokensIn ?? 0),
            resumedUsage.TokensIn);
    }

    [Fact]
    public void App_server_notifications_are_not_misclassified_as_exec_terminal_failures()
    {
        var adapter = new CodexWorkerAdapter();

        foreach (var line in FixtureLines("codex-app-server-errors.jsonl"))
        {
            Assert.False(adapter.TryClassifyFailure(
                null, line, TimeProvider.System, out var classification, out var retryNotBefore));
            Assert.Null(classification);
            Assert.Null(retryNotBefore);
        }
    }

    [Fact]
    public void Agent_prose_cannot_trigger_failure_classification()
    {
        const string line = """
            {"type":"item.completed","item":{"type":"agent_message","text":"I reviewed authentication, sandbox, and usage limit behavior."}}
            """;
        var adapter = new CodexWorkerAdapter();

        Assert.False(adapter.TryClassifyFailure(
            null, line, TimeProvider.System, out var classification, out var retryNotBefore));
        Assert.Null(classification);
        Assert.Null(retryNotBefore);
    }

    [Fact]
    public void Structured_quota_error_preserves_a_reported_reset_instant()
    {
        const string line = """
            {"type":"error","error":{"codexErrorInfo":"usageLimitExceeded","resetsAt":1893456000}}
            """;
        var adapter = new CodexWorkerAdapter();

        Assert.True(adapter.TryClassifyFailure(
            null, line, TimeProvider.System, out var classification, out var retryNotBefore));
        Assert.Equal(FailureClassification.ExhaustedUntil, classification);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1893456000), retryNotBefore);
    }

    [Fact]
    public void An_out_of_range_quota_reset_is_ignored_without_failing_classification()
    {
        const string line = """
            {"type":"error","error":{"codexErrorInfo":"usageLimitExceeded","resetsAt":9223372036854775807}}
            """;
        var adapter = new CodexWorkerAdapter();

        Assert.True(adapter.TryClassifyFailure(
            null, line, TimeProvider.System, out var classification, out var retryNotBefore));
        Assert.Equal(FailureClassification.ExhaustedUntil, classification);
        Assert.Null(retryNotBefore);
    }

    [Theory]
    [InlineData("unknown model gpt-missing")]
    [InlineData("unsupported reasoning effort turbo")]
    [InlineData("not logged in")]
    [InlineData("invalid config: malformed override")]
    public void Configuration_and_authentication_failures_are_permanent(string evidence)
    {
        var adapter = new CodexWorkerAdapter();

        Assert.True(adapter.TryClassifyFailure(
            evidence, TimeProvider.System, out var classification, out var retryNotBefore));
        Assert.Equal(FailureClassification.Permanent, classification);
        Assert.Null(retryNotBefore);
    }

    [Theory]
    [InlineData("tool denied by managed policy")]
    [InlineData("rejected by user approval settings")]
    [InlineData("permission denied")]
    [InlineData("sandbox prevented the operation")]
    public void Permission_and_sandbox_failures_are_tool_denied(string evidence)
    {
        var adapter = new CodexWorkerAdapter();

        Assert.True(adapter.TryClassifyFailure(
            evidence, TimeProvider.System, out var classification, out var retryNotBefore));
        Assert.Equal(FailureClassification.ToolDenied, classification);
        Assert.Null(retryNotBefore);
    }

    [Fact]
    public void Ordinary_failure_text_remains_unclassified()
    {
        var adapter = new CodexWorkerAdapter();

        Assert.False(adapter.TryClassifyFailure(
            "SYNTHETIC_TURN_FAILURE", TimeProvider.System, out var classification, out var retryNotBefore));
        Assert.Null(classification);
        Assert.Null(retryNotBefore);
    }

    [Fact]
    public void Typed_failed_turn_is_classified_even_when_the_process_exit_was_satisfied()
    {
        const string stream = """
            {"type":"thread.started","thread_id":"00000000-0000-0000-0000-000000000008"}
            {"type":"turn.failed","error":{"message":"quota exceeded","resetsAt":"2030-01-01T00:00:00Z"}}
            """;
        var adapter = new CodexWorkerAdapter();

        Assert.True(adapter.TryClassifySatisfiedRunFailure(
            null, stream, TimeProvider.System, out var classification, out var retryNotBefore));
        Assert.Equal(FailureClassification.ExhaustedUntil, classification);
        Assert.Equal(DateTimeOffset.Parse("2030-01-01T00:00:00Z"), retryNotBefore);
    }

    /// <summary>
    /// Re-pinned in #1875 from the dated recording the adapter now ships, instead of the fully
    /// sanitized fixture this used to read. That file could not vouch for any effort set — every
    /// option in it was "Sanitized option" — so an assertion that `gpt-6-astra` advertises `ultra`
    /// rested on a value someone had typed. This reads the same bytes the validation table comes from.
    /// </summary>
    [Fact]
    public void Recorded_model_list_discovers_the_visible_models_and_every_effort_pair()
    {
        var line = RecordedModelListLine();

        Assert.True(CodexWorkerAdapter.TryParseModelListResponse(line, out var capabilities));
        Assert.Equal("codex", capabilities.Vendor);
        Assert.Equal(
            [
                "gpt-6-astra", "gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna",
                "gpt-5.5", "gpt-5.4-mini", "gpt-5.3-codex-spark",
            ],
            capabilities.Models);
        Assert.Equal(35, capabilities.Items.Count);
        Assert.Contains(capabilities.Items, item => item.Name == "gpt-6-astra[ultra]" && item.Kind == "mode");
        Assert.Contains(capabilities.Items, item => item.Name == "gpt-5.6-sol[high]" && item.Kind == "mode");
        Assert.Contains(capabilities.Items, item => item.Name == "gpt-5.6-terra[max]" && item.Kind == "mode");
        Assert.Contains(capabilities.Items, item => item.Name == "gpt-5.6-luna[max]" && item.Kind == "mode");
        Assert.DoesNotContain(capabilities.Items, item => item.Name == "gpt-5.6-luna[ultra]");
        Assert.DoesNotContain(capabilities.Models, model => model == "gpt-5.4");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"id\":1,\"result\":{\"data\":[]}}")]
    [InlineData("{\"id\":2,\"result\":{}}")]
    public void Non_model_list_responses_are_not_misparsed(string line)
    {
        Assert.False(CodexWorkerAdapter.TryParseModelListResponse(line, out var capabilities));
        Assert.Equal("codex", capabilities.Vendor);
        Assert.Empty(capabilities.Models);
        Assert.Empty(capabilities.Items);
    }

    private static IReadOnlyList<string> ArgValues(CoreDispatchTarget target, string flag)
    {
        List<string> values = [];
        for (var index = 0; index < target.Args.Count - 1; index++)
        {
            if (target.Args[index] == flag)
            {
                values.Add(target.Args[index + 1]);
            }
        }

        return values;
    }

    private static CodexBrokerConfiguration BrokerConfiguration(CoreDispatchTarget target)
    {
        var seed = Assert.Single(target.SeedFiles!);
        return JsonSerializer.Deserialize<CodexBrokerConfiguration>(seed.Content)!;
    }

    private static string? ArgValue(CoreDispatchTarget target, string flag) =>
        ArgValues(target, flag).FirstOrDefault();

    private static int IndexOf(CoreDispatchTarget target, string value)
    {
        for (var index = 0; index < target.Args.Count; index++)
        {
            if (target.Args[index] == value)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// #1941 review LOW: codex has no skill realization, and until now a <c>--skill</c> on a codex
    /// binding resolved, linted, requirement-checked, persisted — and reached the worker as nothing,
    /// disclosed only in two registers the operator may never have opened. The notice is what
    /// <c>Resolve</c> writes to stderr; the polarity arm is a binding that declared none, which must
    /// stay silent rather than printing an empty roster line at every ordinary dispatch.
    /// </summary>
    [Fact]
    public void A_declared_skill_on_a_codex_binding_is_announced_as_skipped_rather_than_ignored()
    {
        var packageRoot = Path.Combine(Path.GetTempPath(), $"codex-skill-{Guid.NewGuid():N}", "house-style");
        Directory.CreateDirectory(packageRoot);
        try
        {
            File.WriteAllText(Path.Combine(packageRoot, "SKILL.md"), "description: House style");
            var package = SkillPackageReader.LoadPackage(packageRoot);

            var notice = CodexWorkerAdapter.SkillSkipNotice([package]);

            Assert.NotNull(notice);
            Assert.Contains("house-style", notice, StringComparison.Ordinal);
            Assert.Contains("will NOT reach this worker", notice, StringComparison.Ordinal);

            Assert.Null(CodexWorkerAdapter.SkillSkipNotice([]));
            Assert.Null(CodexWorkerAdapter.SkillSkipNotice(null));

            // And Resolve actually says it -- a notice nothing prints is the same silence, one
            // indirection further away.
            var originalError = Console.Error;
            using var captured = new StringWriter();
            try
            {
                Console.SetError(captured);
                new CodexWorkerAdapter().Resolve(
                    new WorkerInvocation("Inspect.", Skills: [package]), NoOutputContract);
            }
            finally
            {
                Console.SetError(originalError);
            }

            Assert.Contains("house-style", captured.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(Path.GetDirectoryName(packageRoot)!);
        }
    }

    /// <summary>
    /// The embedded recording exactly as it ships: raw app-server JSONL, initialize response and
    /// notification included. What the loader reads.
    /// </summary>
    private static string RecordedRecording()
    {
        using var stream = typeof(CodexWorkerAdapter).Assembly
            .GetManifestResourceStream(CodexWorkerAdapter.ModelCatalogResourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// The one `model/list` result line out of that recording. Live discovery parses a single stdout
    /// line, so this is what stands in for one — selected here the same way the loader selects it, by
    /// being the line whose id is 2, rather than by a position that a re-recording could shift.
    /// </summary>
    private static string RecordedModelListLine() =>
        RecordedRecording()
            .Split('\n')
            .Select(line => line.Trim())
            .Single(line => line.StartsWith("{\"id\":2,", StringComparison.Ordinal));

    private static string[] FixtureLines(string fileName) =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "codex", fileName));

    private static void AssertProgress(
        CodexWorkerAdapter adapter,
        string line,
        string expectedKind,
        string expectedText)
    {
        Assert.True(adapter.TryParseProgressEvent(line, out var progress));
        Assert.NotNull(progress);
        Assert.Equal(expectedKind, progress.Kind);
        Assert.Equal(expectedText, progress.Text);
        Assert.False(progress.IsPartial);
    }
}
