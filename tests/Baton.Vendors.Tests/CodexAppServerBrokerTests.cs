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
