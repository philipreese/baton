using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Baton.Tests.Shared;

namespace Baton.Vendors.Tests;

public sealed class CodexAppServerBrokerTests
{
    [Fact]
    public void App_server_json_lines_are_utf8_without_a_byte_order_mark()
    {
        Assert.Empty(CodexAppServerBroker.JsonLineEncoding.GetPreamble());
    }

    [Fact]
    public async Task Rate_limit_protocol_initializes_then_reads_the_account_without_starting_a_thread()
    {
        var transcript = string.Join('\n',
        [
            "{\"id\":1,\"result\":{\"userAgent\":\"fixture\"}}",
            "{\"id\":2,\"result\":{\"rateLimits\":{\"limitId\":\"codex\",\"primary\":null,\"secondary\":null}}}",
        ]) + "\n";
        using var serverOutput = new StringReader(transcript);
        using var serverInput = new StringWriter();
        using var error = new StringWriter();

        var result = await CodexAppServerBroker.ReadRateLimitsProtocolAsync(
            serverInput, serverOutput, error, TestContext.Current.CancellationToken);

        Assert.Equal("codex", result["rateLimits"]!["limitId"]!.GetValue<string>());
        var requests = Lines(serverInput).Select(line => JsonNode.Parse(line)!).ToArray();
        Assert.Equal(3, requests.Length);
        Assert.Equal("initialize", requests[0]["method"]!.GetValue<string>());
        Assert.Equal("initialized", requests[1]["method"]!.GetValue<string>());
        Assert.Equal("account/rateLimits/read", requests[2]["method"]!.GetValue<string>());
        Assert.Empty(requests[2]["params"]!.AsObject());
        Assert.DoesNotContain(requests, request =>
            request["method"]?.GetValue<string>().StartsWith("thread/", StringComparison.Ordinal) == true
            || request["method"]?.GetValue<string>().StartsWith("turn/", StringComparison.Ordinal) == true);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task Rate_limit_protocol_surfaces_the_vendor_error_response()
    {
        var transcript = string.Join('\n',
        [
            "{\"id\":1,\"result\":{\"userAgent\":\"fixture\"}}",
            "{\"id\":2,\"error\":{\"code\":-32000,\"message\":\"subscription login required\"}}",
        ]) + "\n";
        using var serverOutput = new StringReader(transcript);
        using var serverInput = new StringWriter();
        using var error = new StringWriter();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CodexAppServerBroker.ReadRateLimitsProtocolAsync(
                serverInput, serverOutput, error, TestContext.Current.CancellationToken));

        Assert.Equal("subscription login required", exception.Message);
    }

    [Fact]
    public async Task Rate_limit_protocol_rejects_a_success_response_without_a_result_object()
    {
        var transcript = string.Join('\n',
        [
            "{\"id\":1,\"result\":{\"userAgent\":\"fixture\"}}",
            "{\"id\":2,\"result\":null}",
        ]) + "\n";
        using var serverOutput = new StringReader(transcript);
        using var serverInput = new StringWriter();
        using var error = new StringWriter();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CodexAppServerBroker.ReadRateLimitsProtocolAsync(
                serverInput, serverOutput, error, TestContext.Current.CancellationToken));

        Assert.Equal("Codex app-server returned no rate-limit result.", exception.Message);
    }

    /// <summary>
    /// The daemon awaits sources serially. A server that starts and then answers neither initialize nor
    /// rateLimits must therefore consume a bounded response interval and a bounded cleanup interval,
    /// even when both underlying operations ignore cancellation.
    /// </summary>
    [Fact]
    public async Task Hung_rate_limit_response_and_cleanup_are_both_bounded()
    {
        var response = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupStarted = false;
        using var error = new StringWriter();
        var stopwatch = Stopwatch.StartNew();

        var result = await CodexAppServerBroker.ReadRateLimitsWithinBoundsAsync(
            _ => response.Task,
            _ =>
            {
                cleanupStarted = true;
                return cleanup.Task;
            },
            error,
            TimeSpan.FromMilliseconds(25),
            TimeSpan.FromMilliseconds(25),
            TestContext.Current.CancellationToken);

        stopwatch.Stop();
        Assert.Null(result);
        Assert.True(cleanupStarted);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"elapsed {stopwatch.Elapsed}");
        Assert.Contains("did not answer within", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("cleanup did not finish within", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ordinary_app_server_spawn_failure_returns_the_source_null_contract()
    {
        using var error = new StringWriter();

        var result = await CodexAppServerBroker.ReadRateLimitsAsync(
            () => throw new Win32Exception("codex executable was not found"),
            error,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(25),
            TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.Contains("codex executable was not found", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Protocol_translates_thread_tools_response_usage_and_terminal_success_to_exec_jsonl()
    {
        var root = Path.Combine(Path.GetTempPath(), $"baton-codex-broker-{Guid.NewGuid():N}");
        var workspace = Path.Combine(root, "workspace");
        var output = Path.Combine(root, "output");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(output);
        try
        {
            var grant = new PermissionGrant(ReadFiles: true);
            var configuration = new CodexBrokerConfiguration(
                workspace, "gpt-5.6-luna", "low", null, false, grant, ["report.md"], false);
            var policy = new CodexDynamicToolPolicy(grant, workspace, output, [], ["report.md"]);
            var transcript = string.Join('\n',
            [
                "{\"id\":1,\"result\":{\"userAgent\":\"fixture\"}}",
                "{\"id\":2,\"result\":{\"thread\":{\"id\":\"thread-1\"}}}",
                "{\"id\":3,\"result\":{\"turn\":{\"id\":\"turn-1\",\"status\":\"inProgress\",\"items\":[]}}}",
                "{\"id\":99,\"method\":\"item/tool/call\",\"params\":{\"tool\":\"baton_write_output\",\"arguments\":{\"name\":\"report.md\",\"content\":\"done\"},\"callId\":\"call-1\",\"threadId\":\"thread-1\",\"turnId\":\"turn-1\"}}",
                "{\"method\":\"item/completed\",\"params\":{\"threadId\":\"thread-1\",\"turnId\":\"turn-1\",\"item\":{\"id\":\"message-1\",\"type\":\"agentMessage\",\"text\":\"done\"}}}",
                "{\"method\":\"thread/tokenUsage/updated\",\"params\":{\"threadId\":\"thread-1\",\"turnId\":\"turn-1\",\"tokenUsage\":{\"last\":{\"inputTokens\":100,\"cachedInputTokens\":60,\"cacheWriteInputTokens\":4,\"outputTokens\":20,\"reasoningOutputTokens\":5,\"totalTokens\":120},\"total\":{\"inputTokens\":100,\"cachedInputTokens\":60,\"cacheWriteInputTokens\":4,\"outputTokens\":20,\"reasoningOutputTokens\":5,\"totalTokens\":120}}}}",
                "{\"method\":\"turn/completed\",\"params\":{\"threadId\":\"thread-1\",\"turn\":{\"id\":\"turn-1\",\"status\":\"completed\",\"items\":[]}}}",
            ]) + "\n";
            using var serverOutput = new StringReader(transcript);
            using var serverInput = new StringWriter();
            using var batonOutput = new StringWriter();
            using var error = new StringWriter();

            var exitCode = await CodexAppServerBroker.RunProtocolAsync(
                configuration, "Write the report.", policy, serverInput, serverOutput,
                batonOutput, error, TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            Assert.Equal("done", File.ReadAllText(Path.Combine(output, "report.md")));
            var requests = Lines(serverInput).Select(line => JsonNode.Parse(line)).ToArray();
            Assert.Equal("initialize", requests[0]!["method"]!.GetValue<string>());
            Assert.Equal("thread/start", requests[2]!["method"]!.GetValue<string>());
            Assert.Contains(requests[2]!["params"]!["dynamicTools"]!.AsArray(),
                tool => tool!["name"]!.GetValue<string>() == CodexDynamicToolPolicy.WriteOutputTool);
            Assert.Equal("turn/start", requests[3]!["method"]!.GetValue<string>());
            Assert.Equal(99, requests[4]!["id"]!.GetValue<int>());
            Assert.True(requests[4]!["result"]!["success"]!.GetValue<bool>());

            var events = Lines(batonOutput).Select(line => JsonNode.Parse(line)).ToArray();
            Assert.Equal("thread.started", events[0]!["type"]!.GetValue<string>());
            Assert.Equal("thread-1", events[0]!["thread_id"]!.GetValue<string>());
            Assert.Contains(events, item => item!["type"]!.GetValue<string>() == "item.completed"
                && item["item"]!["type"]!.GetValue<string>() == "agent_message");
            var terminal = events[^1]!;
            Assert.Equal("turn.completed", terminal["type"]!.GetValue<string>());
            Assert.Equal(100, terminal["usage"]!["input_tokens"]!.GetValue<int>());
            Assert.Equal(60, terminal["usage"]!["cached_input_tokens"]!.GetValue<int>());
            Assert.Equal(5, terminal["usage"]!["reasoning_output_tokens"]!.GetValue<int>());
            Assert.Equal(string.Empty, error.ToString());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    /// <summary>
    /// #1996, end to end over the seam the lane actually runs: a codex stream that calls
    /// <c>apply_patch</c> the way app-server delivers a dynamic-tool call, and the file on disk after
    /// it. The manifest assertion and the disk assertion are one test on purpose — a declared tool the
    /// broker cannot execute is the same "I cannot edit" the issue measured, one step later.
    /// </summary>
    [Fact]
    public async Task A_codex_stream_that_calls_apply_patch_edits_the_file_the_grant_allows()
    {
        var root = Path.Combine(Path.GetTempPath(), $"baton-codex-patch-{Guid.NewGuid():N}");
        var workspace = Path.Combine(root, "workspace");
        var output = Path.Combine(root, "output");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(output);
        try
        {
            var target = Path.Combine(workspace, "controls.py");
            File.WriteAllText(target, "def control():\n    return False\n");
            var patch = "*** Begin Patch\n*** Update File: controls.py\n"
                + " def control():\n-    return False\n+    return True\n*** End Patch";
            var grant = new PermissionGrant(ReadFiles: true, WriteFiles: true);
            var configuration = new CodexBrokerConfiguration(
                workspace, "gpt-5.6-terra", "high", null, false, grant, ["changes.md"], false);
            var policy = new CodexDynamicToolPolicy(grant, workspace, output, [], ["changes.md"]);
            var call = new JsonObject
            {
                ["id"] = 99,
                ["method"] = "item/tool/call",
                ["params"] = new JsonObject
                {
                    ["tool"] = CodexDynamicToolPolicy.ApplyPatchTool,
                    ["arguments"] = new JsonObject { ["input"] = patch },
                    ["callId"] = "call-1",
                    ["threadId"] = "thread-1",
                    ["turnId"] = "turn-1",
                },
            };
            var transcript = string.Join('\n',
            [
                "{\"id\":1,\"result\":{\"userAgent\":\"fixture\"}}",
                "{\"id\":2,\"result\":{\"thread\":{\"id\":\"thread-1\"}}}",
                "{\"id\":3,\"result\":{\"turn\":{\"id\":\"turn-1\",\"status\":\"inProgress\",\"items\":[]}}}",
                call.ToJsonString(),
                "{\"method\":\"turn/completed\",\"params\":{\"threadId\":\"thread-1\",\"turn\":{\"id\":\"turn-1\",\"status\":\"completed\",\"items\":[]}}}",
            ]) + "\n";
            using var serverOutput = new StringReader(transcript);
            using var serverInput = new StringWriter();
            using var batonOutput = new StringWriter();
            using var error = new StringWriter();

            var exitCode = await CodexAppServerBroker.RunProtocolAsync(
                configuration, "Fix the control.", policy, serverInput, serverOutput,
                batonOutput, error, TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            Assert.Equal("def control():\n    return True\n", File.ReadAllText(target));
            var requests = Lines(serverInput).Select(line => JsonNode.Parse(line)).ToArray();
            Assert.Contains(requests[2]!["params"]!["dynamicTools"]!.AsArray(),
                tool => tool!["name"]!.GetValue<string>() == CodexDynamicToolPolicy.ApplyPatchTool);
            Assert.True(requests[4]!["result"]!["success"]!.GetValue<bool>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    /// <summary>
    /// #2008, driven over the real emitter rather than a hand-written fixture: a codex app-server
    /// transcript whose turn issues four dynamic-tool calls, and the assertion that EVERY
    /// <c>mcp_tool_call</c> item the room's stream then carries — <c>item.started</c> and
    /// <c>item.completed</c> alike, which is the half that carried no digest at all — names both the
    /// arguments digest and the call's input identity.
    /// <para>
    /// <b><c>baton_run_command</c> is called on a grant that WITHHOLDS it</b>, on purpose. It keeps the
    /// test from spawning a real process, and it pins the discrimination the issue asked about
    /// directly: the identity is recorded because the call was ANNOUNCED, not because Baton executed
    /// it, so a refused command line is as auditable as a permitted one.
    /// </para>
    /// <para>
    /// The per-tool expectations are asserted by value, not just for presence: a
    /// <c>Describe</c> that stamped the tool name into both fields would satisfy "present" and answer
    /// nothing.
    /// </para>
    /// <para>
    /// <b>The claim is over items naming a tool Baton implements</b>, which is the whole population a
    /// room's grant can produce on purpose. The one exception —
    /// <see cref="An_unimplemented_tool_name_is_still_digested_and_honestly_carries_no_identity"/> — is
    /// its own test rather than a caveat in this one, so the universal here stays universal.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Every_tool_item_in_the_room_carries_the_arguments_digest_and_the_input_identity()
    {
        var root = Path.Combine(Path.GetTempPath(), $"baton-codex-identity-{Guid.NewGuid():N}");
        var workspace = Path.Combine(root, "workspace");
        var output = Path.Combine(root, "output");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(output);
        try
        {
            File.WriteAllText(Path.Combine(workspace, "controls.py"), "def control():\n    return False\n");
            // Read and write, but deliberately NOT RunShellCommands -- see the doc comment.
            var grant = new PermissionGrant(ReadFiles: true, WriteFiles: true);
            var configuration = new CodexBrokerConfiguration(
                workspace, "gpt-5.6-terra", "high", null, false, grant, ["report.md"], false);
            var policy = new CodexDynamicToolPolicy(grant, workspace, output, [], ["report.md"]);
            var patch = "*** Begin Patch\n*** Update File: controls.py\n"
                + " def control():\n-    return False\n+    return True\n*** End Patch";
            var transcript = string.Join('\n',
            [
                "{\"id\":1,\"result\":{\"userAgent\":\"fixture\"}}",
                "{\"id\":2,\"result\":{\"thread\":{\"id\":\"thread-1\"}}}",
                "{\"id\":3,\"result\":{\"turn\":{\"id\":\"turn-1\",\"status\":\"inProgress\",\"items\":[]}}}",
                ToolCall(11, CodexDynamicToolPolicy.ReadTextTool, new JsonObject { ["path"] = "controls.py" }),
                ToolCall(12, CodexDynamicToolPolicy.RunCommandTool,
                    new JsonObject { ["command"] = "pixi run gates-fast" }),
                ToolCall(13, CodexDynamicToolPolicy.ApplyPatchTool, new JsonObject { ["input"] = patch }),
                ToolCall(14, CodexDynamicToolPolicy.WriteOutputTool,
                    new JsonObject { ["name"] = "report.md", ["content"] = new string('x', 4096) }),
                "{\"method\":\"turn/completed\",\"params\":{\"threadId\":\"thread-1\",\"turn\":{\"id\":\"turn-1\",\"status\":\"completed\",\"items\":[]}}}",
            ]) + "\n";
            using var serverOutput = new StringReader(transcript);
            using var serverInput = new StringWriter();
            using var batonOutput = new StringWriter();
            using var error = new StringWriter();

            var exitCode = await CodexAppServerBroker.RunProtocolAsync(
                configuration, "Do the work.", policy, serverInput, serverOutput,
                batonOutput, error, TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            var stream = batonOutput.ToString();
            var toolItems = Lines(batonOutput).Select(line => JsonNode.Parse(line)!)
                .Where(node => node["item"]?["type"]?.GetValue<string>() == "mcp_tool_call")
                .ToArray();
            // Four calls, each announced twice: the control on "every item" actually being eight.
            Assert.Equal(8, toolItems.Length);
            foreach (var node in toolItems)
            {
                // Read through ContainsKey rather than dereferencing, so a missing field fails as the
                // claim it is ("this item carries no digest") instead of a NullReferenceException from
                // the assertion's own indexer.
                var item = node["item"]!.AsObject();
                var where = $"{node["type"]} {item["tool"]}";
                Assert.True(item.ContainsKey(Baton.Status.CodexUsageParser.ArgumentsDigestField), where);
                Assert.True(item.ContainsKey(Baton.Status.CodexUsageParser.ArgumentsIdentityField), where);
                Assert.Matches(
                    "^[0-9a-f]{16}$",
                    item[Baton.Status.CodexUsageParser.ArgumentsDigestField]!.GetValue<string>());
                Assert.NotEmpty(item[Baton.Status.CodexUsageParser.ArgumentsIdentityField]!.GetValue<string>());
            }

            static string Identity(JsonNode[] items, string tool, string lifecycle) =>
                items.Single(node => node["type"]!.GetValue<string>() == lifecycle
                        && node["item"]!["tool"]!.GetValue<string>() == tool)
                    ["item"]![Baton.Status.CodexUsageParser.ArgumentsIdentityField]!.GetValue<string>();

            Assert.Equal("controls.py", Identity(toolItems, CodexDynamicToolPolicy.ReadTextTool, "item.started"));
            Assert.Equal("controls.py", Identity(toolItems, CodexDynamicToolPolicy.ReadTextTool, "item.completed"));
            Assert.Equal("pixi run gates-fast", Identity(toolItems, CodexDynamicToolPolicy.RunCommandTool, "item.started"));
            Assert.Equal("controls.py", Identity(toolItems, CodexDynamicToolPolicy.ApplyPatchTool, "item.started"));
            Assert.Equal("report.md", Identity(toolItems, CodexDynamicToolPolicy.WriteOutputTool, "item.started"));
            // The withheld command was still recorded, and it was still refused.
            Assert.Equal("failed", toolItems
                .Single(node => node["type"]!.GetValue<string>() == "item.completed"
                    && node["item"]!["tool"]!.GetValue<string>() == CodexDynamicToolPolicy.RunCommandTool)
                ["item"]!["status"]!.GetValue<string>());
            // The bound the digest exists to hold: neither the 4 KiB output body nor the patch body
            // reaches the stream through the new field.
            Assert.DoesNotContain(new string('x', 64), stream, StringComparison.Ordinal);
            Assert.DoesNotContain("*** Begin Patch", stream, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    /// <summary>
    /// #2008, the polarity partner and the measured edge of the claim above: codex reaching for a tool
    /// Baton implements nowhere (#1920 recorded five such calls on one arm). The item pair is announced
    /// before <c>ExecuteAsync</c> rejects the name, so it is real traffic in a real room — and it
    /// carries the digest, which still tells two such calls apart, with the identity ABSENT rather than
    /// scraped out of arguments whose schema Baton does not know.
    /// <para>
    /// Absent, specifically, not blank — <c>ContainsKey</c> is the assertion. Why the two differ is
    /// stated once beside the emitter, in <c>CodexAppServerBroker.Describe</c>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_unimplemented_tool_name_is_still_digested_and_honestly_carries_no_identity()
    {
        var root = Path.Combine(Path.GetTempPath(), $"baton-codex-unknown-{Guid.NewGuid():N}");
        var output = Path.Combine(root, "output");
        Directory.CreateDirectory(output);
        try
        {
            var grant = new PermissionGrant(ReadFiles: true);
            var configuration = new CodexBrokerConfiguration(
                root, "gpt-5.6-luna", "low", null, false, grant, ["report.md"], false);
            var policy = new CodexDynamicToolPolicy(grant, root, output, [], ["report.md"]);
            var transcript = string.Join('\n',
            [
                "{\"id\":1,\"result\":{\"userAgent\":\"fixture\"}}",
                "{\"id\":2,\"result\":{\"thread\":{\"id\":\"thread-1\"}}}",
                "{\"id\":3,\"result\":{\"turn\":{\"id\":\"turn-1\",\"status\":\"inProgress\",\"items\":[]}}}",
                ToolCall(21, "str_replace_editor", new JsonObject { ["file"] = "controls.py" }),
                "{\"method\":\"turn/completed\",\"params\":{\"threadId\":\"thread-1\",\"turn\":{\"id\":\"turn-1\",\"status\":\"completed\",\"items\":[]}}}",
            ]) + "\n";
            using var serverOutput = new StringReader(transcript);
            using var serverInput = new StringWriter();
            using var batonOutput = new StringWriter();
            using var error = new StringWriter();

            await CodexAppServerBroker.RunProtocolAsync(
                configuration, "Edit it.", policy, serverInput, serverOutput,
                batonOutput, error, TestContext.Current.CancellationToken);

            var toolItems = Lines(batonOutput).Select(line => JsonNode.Parse(line)!)
                .Where(node => node["item"]?["type"]?.GetValue<string>() == "mcp_tool_call")
                .Select(node => node["item"]!.AsObject())
                .ToArray();

            Assert.Equal(2, toolItems.Length);
            foreach (var item in toolItems)
            {
                Assert.Equal("str_replace_editor", item["tool"]!.GetValue<string>());
                Assert.Matches(
                    "^[0-9a-f]{16}$",
                    item[Baton.Status.CodexUsageParser.ArgumentsDigestField]!.GetValue<string>());
                Assert.False(item.ContainsKey(Baton.Status.CodexUsageParser.ArgumentsIdentityField));
            }

            Assert.Equal("failed", toolItems[1]["status"]!.GetValue<string>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    private static string ToolCall(int id, string tool, JsonObject arguments) =>
        new JsonObject
        {
            ["id"] = id,
            ["method"] = "item/tool/call",
            ["params"] = new JsonObject
            {
                ["tool"] = tool,
                ["arguments"] = arguments,
                ["callId"] = $"call-{id}",
                ["threadId"] = "thread-1",
                ["turnId"] = "turn-1",
            },
        }.ToJsonString();

    /// <summary>
    /// #1996 re-review MEDIUM, and the checker that drift had none of: the instruction constraining
    /// which tools the model may use must not exclude a tool the same payload declares. What it used
    /// to exclude, and why that mattered, is stated once beside the instruction itself in
    /// <see cref="CodexAppServerBroker.BuildThreadParams"/>. The loop is the general form — any
    /// declared name outside the <c>baton_</c> prefix has to be named in the sentence or this fails.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Developer_instructions_exclude_no_tool_the_same_payload_declares(bool writeGranted)
    {
        var grant = new PermissionGrant(ReadFiles: true, WriteFiles: writeGranted);
        var configuration = new CodexBrokerConfiguration(
            "C:/workspace", "gpt-5.6-luna", "low", null, false, grant, ["report.md"], false);
        var policy = new CodexDynamicToolPolicy(grant, Path.GetTempPath(), Path.GetTempPath(), [], ["report.md"]);

        var parameters = CodexAppServerBroker.BuildThreadParams(configuration, policy);

        var instructions = parameters["developerInstructions"]!.GetValue<string>();
        var declared = parameters["dynamicTools"]!.AsArray()
            .Select(tool => tool!["name"]!.GetValue<string>()).ToArray();
        Assert.DoesNotContain("baton_*", instructions, StringComparison.Ordinal);
        Assert.Contains(
            "The read-only native sandbox applies to disabled native tools; declared Baton tools "
            + "operate under this role's actual grant.",
            instructions,
            StringComparison.Ordinal);
        Assert.Equal(writeGranted, declared.Contains(CodexDynamicToolPolicy.ApplyPatchTool));
        foreach (var name in declared.Where(name => !name.StartsWith("baton_", StringComparison.Ordinal)))
        {
            Assert.Contains(name, instructions, StringComparison.Ordinal);
        }
        if (writeGranted)
        {
            Assert.Contains(
                $"{CodexDynamicToolPolicy.ApplyPatchTool} is this thread's edit tool.",
                instructions,
                StringComparison.Ordinal);
        }
        else
        {
            // The polarity partner: a read-only thread is not told about an edit tool it will not be
            // offered, so the sentence tracks the manifest in both directions.
            Assert.DoesNotContain(CodexDynamicToolPolicy.ApplyPatchTool, instructions, StringComparison.Ordinal);
            Assert.DoesNotContain("is this thread's edit tool.", instructions, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Resume_request_reuses_the_persisted_thread_without_redeclaring_tools()
    {
        var grant = new PermissionGrant(ReadFiles: true);
        var configuration = new CodexBrokerConfiguration(
            "C:/workspace", "gpt-5.6-luna", "low", "thread-1", true, grant, ["report.md"], false);
        var policy = new CodexDynamicToolPolicy(grant, Path.GetTempPath(), Path.GetTempPath(), [], ["report.md"]);

        var parameters = CodexAppServerBroker.BuildThreadParams(configuration, policy);

        Assert.Equal("thread-1", parameters["threadId"]!.GetValue<string>());
        Assert.Null(parameters["dynamicTools"]);
        Assert.Null(parameters["config"]);
    }

    [Fact]
    public void Isolated_home_is_empty_until_the_operator_logs_in_with_the_vendor_cli()
    {
        var root = Path.Combine(Path.GetTempPath(), $"baton-codex-home-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source");
        var baton = Path.Combine(root, "baton");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "auth.json"), "original");
        File.WriteAllText(Path.Combine(source, "config.toml"), "mcp server config");
        try
        {
            var isolated = CodexIsolatedHome.Prepare(baton);

            Assert.False(File.Exists(Path.Combine(isolated, "config.toml")));
            Assert.False(File.Exists(Path.Combine(isolated, "AGENTS.md")));
            Assert.False(File.Exists(Path.Combine(isolated, "auth.json")));
            Assert.Equal("original", File.ReadAllText(Path.Combine(source, "auth.json")));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public void Native_escape_surfaces_are_disabled_but_the_grant_tool_orchestrator_remains_available()
    {
        var disabled = CodexAppServerBroker.DisabledFeatures(allowsSubagents: true);

        Assert.Contains("shell_tool", disabled);
        Assert.Contains("unified_exec", disabled);
        Assert.DoesNotContain("code_mode_host", disabled);
        Assert.Contains("apps", disabled);
        Assert.Contains("browser_use", disabled);
        Assert.Contains("computer_use", disabled);
        Assert.Contains("image_generation", disabled);
        Assert.Contains("multi_agent", disabled);
        Assert.Contains("multi_agent_v2", disabled);
    }

    private static string[] Lines(StringWriter writer) =>
        writer.ToString().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
}
