namespace Baton.VendorProbe.Tests;

/// <summary>
/// The discriminating control the vendor probe never had (#1088). The suite recorded agy's structured
/// output and per-turn usage as absent across three versions because <c>Probes</c> hard-coded claude's
/// stream-json grammar and keyed detection on claude's <c>"type"</c> envelope — and nothing ever built
/// or tested this tool. These are <b>pure-function</b> tests: no <c>Cli.Invoke</c>, no vendor process,
/// safe in CI. The agy/claude fixtures are the first lines of real <c>--output-format stream-json</c>
/// runs captured on an authenticated host (2026-08-10, agy 1.1.11 / claude 2.1.222) and frozen here.
/// </summary>
public sealed class ProbeGrammarTests
{
    // First line of a real `agy -p "<prompt>" --output-format stream-json` run. Note: `"event"`, not `"type"`.
    private const string AgyFirstLine =
        """{"event":"init","conversation_id":"5ec0d582-de3a-4fce-b626-578a3fcd9815","init":{"cwd":"C:/tmp"}}""";

    // First line of a real `claude -p --output-format stream-json --verbose "<prompt>"` run.
    private const string ClaudeFirstLine =
        """{"type":"system","subtype":"hook_started","hook_event":"SessionStart","session_id":"361b75"}""";

    // What agy actually returns when handed claude's grammar: the prompt string became "--output-format",
    // so agy answered in prose and stream-json never engaged — the exact false-negative this fix ends.
    private const string AgyProseWhenMisinvoked =
        "Could you specify how you would like to use `--output-format`? For assistant responses...";

    private const string CodexTurn = """
        {"type":"thread.started","thread_id":"00000000-0000-0000-0000-000000000001"}
        {"type":"turn.completed","usage":{"input_tokens":100,"cached_input_tokens":20,"output_tokens":5}}
        """;

    private const string CodexModels = """
        {"id":2,"result":{"data":[{"model":"gpt-example","hidden":false,"supportedReasoningEfforts":[{"reasoningEffort":"low"},{"reasoningEffort":"high"}]},{"model":"hidden-reserve","hidden":true,"supportedReasoningEfforts":[{"reasoningEffort":"medium"}]}],"nextCursor":null}}
        """;

    private const string CodexRateLimits = """
        {"id":3,"result":{"rateLimits":{"primary":{"usedPercent":42,"resetsAt":1893456000},"secondary":{"usedPercent":7}}}}
        """;

    [Fact]
    public void Agy_stream_json_args_use_flag_value_grammar_and_omit_claude_verbose()
    {
        var args = Probes.StreamJsonArgs("agy", "Reply with exactly: ok");

        // The prompt sits immediately after -p; the grammar rationale lives on Probes.StreamJsonArgs.
        var p = Array.IndexOf(args, "-p");
        Assert.True(p >= 0 && p + 1 < args.Length, "expected a -p flag with a following value");
        Assert.Equal("Reply with exactly: ok", args[p + 1]);

        Assert.Contains("--output-format", args);
        Assert.Contains("stream-json", args);
        // and never claude's --verbose (rationale on Probes.StreamJsonArgs).
        Assert.DoesNotContain("--verbose", args);
    }

    [Fact]
    public void Claude_stream_json_args_keep_boolean_p_positional_prompt_and_verbose()
    {
        var args = Probes.StreamJsonArgs("claude", "Reply with exactly: ok");

        // claude's -p is a boolean; the prompt is positional (last), and --verbose is required for stream-json.
        var p = Array.IndexOf(args, "-p");
        Assert.True(p >= 0);
        Assert.Equal("--output-format", args[p + 1]); // NOT the prompt — claude grammar differs from agy
        Assert.Contains("--verbose", args);
        Assert.Equal("Reply with exactly: ok", args[^1]);
    }

    [Fact]
    public void Codex_stream_json_args_use_exec_json_grammar_and_a_read_only_sandbox()
    {
        var args = Probes.StreamJsonArgs("codex", "Reply with exactly: ok");

        Assert.Equal("exec", args[0]);
        Assert.Contains("--json", args);
        Assert.Contains("--ignore-user-config", args);
        Assert.Contains("--skip-git-repo-check", args);
        Assert.Equal("read-only", args[Array.IndexOf(args, "--sandbox") + 1]);
        Assert.Equal("gpt-5.6-luna", args[Array.IndexOf(args, "--model") + 1]);
        Assert.Contains("model_reasoning_effort=\"low\"", args);
        Assert.Contains("approval_policy=\"never\"", args);
        Assert.Contains("shell_tool", args);
        Assert.Contains("unified_exec", args);
        Assert.Contains("multi_agent", args);
        Assert.Contains("multi_agent_v2", args);
        Assert.Equal("Reply with exactly: ok", args[^1]);
        Assert.DoesNotContain("-p", args);
        Assert.DoesNotContain("--output-format", args);
    }

    [Fact]
    public void Codex_resume_keeps_common_options_before_the_subcommand_and_prompt_last()
    {
        var args = CodexProbe.ResumeJsonArgs(
            "00000000-0000-0000-0000-000000000001", "Reply with exactly: resumed-ok");
        var resume = Array.IndexOf(args, "resume");

        Assert.True(resume > Array.IndexOf(args, "--json"));
        Assert.Equal("00000000-0000-0000-0000-000000000001", args[resume + 1]);
        Assert.Equal("Reply with exactly: resumed-ok", args[^1]);
    }

    [Fact]
    public void Codex_app_server_uses_stdio_and_initializes_before_requests()
    {
        Assert.Equal(["app-server", "--stdio"], CodexProbe.AppServerArgs());

        var requests = CodexProbe.AppServerRequests();
        Assert.Equal(4, requests.Length);
        Assert.Contains("\"method\":\"initialize\"", requests[0]);
        Assert.Contains("\"method\":\"initialized\"", requests[1]);
        Assert.Contains("\"method\":\"model/list\"", requests[2]);
        Assert.Contains("\"id\":2", requests[2]);
        Assert.Contains("\"includeHidden\":false", requests[2]);
        Assert.Contains("\"method\":\"account/rateLimits/read\"", requests[3]);
        Assert.Contains("\"id\":3", requests[3]);
        Assert.Contains("\"params\":null", requests[3]);
        Assert.All(requests, request =>
        {
            Assert.DoesNotContain("\"jsonrpc\"", request);
            Assert.NotNull(System.Text.Json.JsonDocument.Parse(request));
        });
    }

    [Fact]
    public void Codex_response_matching_ignores_notifications_and_malformed_lines()
    {
        Assert.False(CodexProbe.IsRequestedResponse("not json", 2));
        Assert.False(CodexProbe.IsRequestedResponse("{\"method\":\"thread/started\",\"params\":{}}", 2));
        Assert.True(CodexProbe.IsRequestedResponse(CodexModels, 2));
        Assert.False(CodexProbe.IsRequestedResponse(CodexModels, 3));
    }

    [Fact]
    public void Codex_paid_probe_requires_explicit_chatgpt_subscription_status()
    {
        Assert.True(CodexProbe.IsChatGptSubscriptionAuth("Logged in using ChatGPT"));
        Assert.False(CodexProbe.IsChatGptSubscriptionAuth("Logged in using an API key"));
        Assert.False(CodexProbe.IsChatGptSubscriptionAuth("not logged in"));
    }

    [Theory]
    [InlineData("CLAUDE_CODE_ENTRYPOINT")]
    [InlineData("OPENAI_API_KEY")]
    [InlineData("OPENAI_BASE_URL")]
    [InlineData("AZURE_OPENAI_API_KEY")]
    [InlineData("CODEX_API_KEY")]
    [InlineData("CODEX_BASE_URL")]
    public void Vendor_probe_scrubs_parent_sessions_api_credentials_and_provider_overrides(string variable)
    {
        Assert.True(Cli.IsCredentialOrParentSessionVariable(variable));
        Assert.False(Cli.IsCredentialOrParentSessionVariable("CODEX_HOME"));
        Assert.False(Cli.IsCredentialOrParentSessionVariable("PATH"));
    }

    [Fact]
    public void Codex_exec_json_and_turn_usage_are_recognized_across_the_stream()
    {
        Assert.True(CodexProbe.LooksLikeExecJson("diagnostic\n" + CodexTurn));
        Assert.True(CodexProbe.HasTurnUsage(CodexTurn));
        Assert.False(CodexProbe.HasTurnUsage("{\"type\":\"turn.completed\"}"));
        Assert.False(CodexProbe.LooksLikeExecJson("plain prose"));
        Assert.True(CodexProbe.TryReadThreadId(CodexTurn, out var sessionId));
        Assert.Equal("00000000-0000-0000-0000-000000000001", sessionId);
        Assert.False(CodexProbe.TryReadThreadId("not json", out _));
    }

    [Fact]
    public void Codex_model_list_preserves_model_specific_effort_sets()
    {
        Assert.True(CodexProbe.TryDescribeModels(CodexModels, out var summary));
        Assert.Equal("gpt-example[low/high]", summary);
        Assert.DoesNotContain("hidden-reserve", summary);
        Assert.False(CodexProbe.TryDescribeModels("{\"id\":1,\"result\":{}}", out _));
    }

    [Fact]
    public void Codex_rate_limits_require_structured_used_percent_windows()
    {
        Assert.True(CodexProbe.TryDescribeRateLimits(CodexRateLimits, out var summary));
        Assert.Contains("42% used, resets 2030-01-01T00:00:00.0000000+00:00", summary);
        Assert.Contains("7% used", summary);
        Assert.False(CodexProbe.TryDescribeRateLimits("{\"id\":3,\"result\":{\"planType\":\"pro\"}}", out _));
    }

    [Fact]
    public void Program_recognizes_codex_as_a_default_probe_and_drift_vendor()
    {
        Assert.Equal(["claude", "agy", "codex"], Program.SupportedVendors);
    }

    [Fact]
    public void LooksLikeStreamJson_recognises_agy_event_envelope()
    {
        // The regression that mattered: agy streams `{"event":...}`, and the old `Contains("\"type\"")`
        // check read that as "not structured". This must be true.
        Assert.True(Probes.LooksLikeStreamJson(AgyFirstLine));
    }

    [Fact]
    public void LooksLikeStreamJson_recognises_claude_type_envelope()
    {
        Assert.True(Probes.LooksLikeStreamJson(ClaudeFirstLine));
    }

    [Fact]
    public void LooksLikeStreamJson_rejects_the_misinvoked_prose_and_empty()
    {
        Assert.False(Probes.LooksLikeStreamJson(AgyProseWhenMisinvoked));
        Assert.False(Probes.LooksLikeStreamJson(""));
        // A line merely containing the word "type" in prose must not masquerade as a stream (structural, not substring).
        Assert.False(Probes.LooksLikeStreamJson("The response type is JSON, apparently."));
    }

    [Fact]
    public void Codex_try_parse_live_catalog_extracts_visible_models_and_efforts()
    {
        Assert.True(CodexProbe.TryParseLiveCatalog(CodexModels, out var liveCatalog));
        Assert.Single(liveCatalog);
        Assert.True(liveCatalog.ContainsKey("gpt-example"));
        Assert.Equal(["low", "high"], liveCatalog["gpt-example"]);
        Assert.False(liveCatalog.ContainsKey("hidden-reserve"));
    }

    [Fact]
    public void Codex_compare_catalog_reports_current_when_catalogs_match()
    {
        var catalog = new Dictionary<string, IReadOnlyList<string>>
        {
            ["gpt-6-astra"] = ["low", "medium", "high"],
        };

        var drift = CodexProbe.CompareCatalog(catalog, catalog, "recording.jsonl");

        Assert.False(drift.HasDrift);
        Assert.Equal(CodexProbe.CatalogDriftVerdict.Current, drift.Verdict);
        Assert.Contains("matches embedded recording", drift.Explain());
    }

    [Fact]
    public void Codex_compare_catalog_detects_dropped_and_gained_models()
    {
        var recorded = new Dictionary<string, IReadOnlyList<string>>
        {
            ["gpt-6-astra"] = ["low", "high"],
            ["gpt-5.4-mini"] = ["low", "medium", "high"],
        };
        var live = new Dictionary<string, IReadOnlyList<string>>
        {
            ["gpt-6-astra"] = ["low", "high"],
            ["gpt-6.5-nova"] = ["medium"],
        };

        var drift = CodexProbe.CompareCatalog(live, recorded, "codex-model-list-2026-09-04.jsonl");

        Assert.True(drift.HasDrift);
        Assert.Equal(CodexProbe.CatalogDriftVerdict.Drifted, drift.Verdict);
        Assert.Equal(["gpt-5.4-mini"], drift.DroppedModels);
        Assert.Equal(["gpt-6.5-nova"], drift.GainedModels);
        Assert.Empty(drift.ChangedEfforts);
        var explanation = drift.Explain();
        Assert.Contains("dropped: gpt-5.4-mini", explanation);
        Assert.Contains("gained: gpt-6.5-nova", explanation);
    }

    [Fact]
    public void Codex_compare_catalog_detects_changed_efforts()
    {
        var recorded = new Dictionary<string, IReadOnlyList<string>>
        {
            ["gpt-6-astra"] = ["low", "high"],
        };
        var live = new Dictionary<string, IReadOnlyList<string>>
        {
            ["gpt-6-astra"] = ["low", "high", "ultra"],
        };

        var drift = CodexProbe.CompareCatalog(live, recorded, "recording.jsonl");

        Assert.True(drift.HasDrift);
        Assert.Equal(["gpt-6-astra (live: [low/high/ultra], recorded: [low/high])"], drift.ChangedEfforts);
        Assert.Contains("effort changed", drift.Explain());
    }
}
