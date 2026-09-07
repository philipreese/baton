using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Baton.Status;

namespace Baton.Vendors;

/// <summary>
/// Bidirectional bridge between Baton's one-process JSONL dispatch seam and Codex app-server.
/// App-server's native mutation/tool surfaces are disabled; server-initiated dynamic tool calls are
/// answered only through <see cref="CodexDynamicToolPolicy"/>.
/// </summary>
public static class CodexAppServerBroker
{
    private const int InitializeRequestId = 1;
    private const int ThreadRequestId = 2;
    private const int TurnRequestId = 3;
    internal static readonly Encoding JsonLineEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static async Task<int> RunAsync(
        CodexBrokerConfiguration configuration,
        string prompt,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var outputDirectory = Environment.GetEnvironmentVariable("BATON_OUTPUT_DIR");
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            await error.WriteLineAsync("Codex broker requires BATON_OUTPUT_DIR.").ConfigureAwait(false);
            return 1;
        }

        string isolatedHome;
        try
        {
            isolatedHome = CodexIsolatedHome.Prepare(BatonPaths.Root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            await error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 1;
        }

        var inputPaths = Environment.GetEnvironmentVariables().Keys.Cast<object>()
            .Select(key => key.ToString())
            .Where(key => key?.StartsWith("BATON_INPUT_", StringComparison.Ordinal) == true)
            .Order(StringComparer.Ordinal)
            .Select(key => Environment.GetEnvironmentVariable(key!)!)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        var policy = new CodexDynamicToolPolicy(
            configuration.PermissionGrant,
            configuration.WorkingDirectory,
            outputDirectory,
            inputPaths,
            configuration.ProducedOutputNames);

        using var process = StartAppServer(configuration, isolatedHome);
        if (process is null)
        {
            await error.WriteLineAsync("Baton could not start codex app-server.").ConfigureAwait(false);
            return 1;
        }

        var stderrDrain = DrainStderrAsync(process.StandardError, error, cancellationToken);
        try
        {
            return await RunProtocolAsync(
                configuration, prompt, policy, process.StandardInput, process.StandardOutput,
                output, error, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException
            or UnauthorizedAccessException)
        {
            await EmitAsync(output, new JsonObject
            {
                ["type"] = "error",
                ["message"] = ex.Message,
            }).ConfigureAwait(false);
            return 1;
        }
        finally
        {
            process.StandardInput.Close();
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // The process raced the broker to a normal exit.
                }
            }
            await stderrDrain.ConfigureAwait(false);
        }
    }

    internal static async Task<int> RunProtocolAsync(
        CodexBrokerConfiguration configuration,
        string prompt,
        CodexDynamicToolPolicy policy,
        TextWriter serverInput,
        TextReader serverOutput,
        TextWriter batonOutput,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        await SendAsync(serverInput, new JsonObject
        {
            ["method"] = "initialize",
            ["id"] = InitializeRequestId,
            ["params"] = new JsonObject
            {
                ["clientInfo"] = new JsonObject
                {
                    ["name"] = "baton",
                    ["title"] = "Baton Codex broker",
                    ["version"] = "1",
                },
                ["capabilities"] = new JsonObject { ["experimentalApi"] = true },
            },
        }, cancellationToken).ConfigureAwait(false);
        await ReadResponseAsync(serverOutput, InitializeRequestId, error, cancellationToken).ConfigureAwait(false);
        await SendAsync(serverInput, new JsonObject
        {
            ["method"] = "initialized",
            ["params"] = new JsonObject(),
        }, cancellationToken).ConfigureAwait(false);

        var threadParams = BuildThreadParams(configuration, policy);
        await SendAsync(serverInput, new JsonObject
        {
            ["method"] = configuration.ResumeSession ? "thread/resume" : "thread/start",
            ["id"] = ThreadRequestId,
            ["params"] = threadParams,
        }, cancellationToken).ConfigureAwait(false);
        var threadResponse = await ReadResponseAsync(
            serverOutput, ThreadRequestId, error, cancellationToken).ConfigureAwait(false);
        var threadId = threadResponse["result"]?["thread"]?["id"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Codex app-server did not return a thread ID.");

        await EmitAsync(batonOutput, new JsonObject
        {
            ["type"] = "thread.started",
            ["thread_id"] = threadId,
        }).ConfigureAwait(false);

        await SendAsync(serverInput, new JsonObject
        {
            ["method"] = "turn/start",
            ["id"] = TurnRequestId,
            ["params"] = new JsonObject
            {
                ["threadId"] = threadId,
                ["input"] = new JsonArray(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = prompt,
                }),
                ["approvalPolicy"] = "never",
                ["sandboxPolicy"] = new JsonObject { ["type"] = "readOnly" },
                ["environments"] = new JsonArray(),
            },
        }, cancellationToken).ConfigureAwait(false);
        await ReadResponseAsync(serverOutput, TurnRequestId, error, cancellationToken).ConfigureAwait(false);
        await EmitAsync(batonOutput, new JsonObject { ["type"] = "turn.started" }).ConfigureAwait(false);

        JsonObject? lastUsage = null;
        while (await serverOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (!TryParseObject(line, out var message))
            {
                await error.WriteLineAsync($"Ignored non-JSON app-server output: {line}").ConfigureAwait(false);
                continue;
            }

            if (message["id"] is not null && message["method"]?.GetValue<string>() == "item/tool/call")
            {
                await HandleDynamicToolCallAsync(
                    message, policy, serverInput, batonOutput, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var method = message["method"]?.GetValue<string>();
            switch (method)
            {
                case "thread/tokenUsage/updated":
                    lastUsage = message["params"]?["tokenUsage"]?["last"]?.AsObject().DeepClone().AsObject();
                    break;
                case "item/completed":
                    await EmitCompletedItemAsync(message, batonOutput).ConfigureAwait(false);
                    break;
                case "error":
                    if (message["params"]?["willRetry"]?.GetValue<bool>() != true)
                    {
                        await EmitAsync(batonOutput, new JsonObject
                        {
                            ["type"] = "error",
                            ["error"] = message["params"]?["error"]?.DeepClone(),
                        }).ConfigureAwait(false);
                    }
                    break;
                case "turn/completed":
                    return await EmitTerminalTurnAsync(message, lastUsage, batonOutput).ConfigureAwait(false);
            }
        }

        throw new IOException("Codex app-server closed stdout before a terminal turn event.");
    }

    private static Process? StartAppServer(CodexBrokerConfiguration configuration, string isolatedHome)
    {
        var startInfo = new ProcessStartInfo(CodexExecutableResolver.Resolve())
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // app-server consumes one JSON-RPC object per line. Encoding.UTF8's preamble becomes
            // the first bytes on redirected stdin on Windows, and app-server rejects that BOM as
            // "expected value at line 1 column 1" before initialize. Pin every redirected stream
            // to BOM-less UTF-8; stdout/stderr use the same encoding for symmetry.
            StandardInputEncoding = JsonLineEncoding,
            StandardOutputEncoding = JsonLineEncoding,
            StandardErrorEncoding = JsonLineEncoding,
            WorkingDirectory = configuration.WorkingDirectory ?? Environment.CurrentDirectory,
        };
        startInfo.ArgumentList.Add("app-server");
        startInfo.ArgumentList.Add("--stdio");
        foreach (var feature in DisabledFeatures(configuration.AllowsSubagents))
        {
            startInfo.ArgumentList.Add("--disable");
            startInfo.ArgumentList.Add(feature);
        }
        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add("mcp_servers={}");
        startInfo.Environment["CODEX_HOME"] = isolatedHome;
        startInfo.Environment["CODEX_SQLITE_HOME"] = isolatedHome;
        foreach (var name in startInfo.Environment.Keys
            .Where(name => name.StartsWith("BATON_", StringComparison.Ordinal)).ToArray())
        {
            startInfo.Environment.Remove(name);
        }
        return Process.Start(startInfo);
    }

    internal static IReadOnlyList<string> DisabledFeatures(bool allowsSubagents)
    {
        List<string> features =
        [
            "shell_tool", "unified_exec", "apps", "browser_use",
            "computer_use", "image_generation",
        ];
        // Current Codex routes tool-backed work through Code Mode. Its runtime receives only the
        // nested tool definitions assembled for this turn; with the native tools above disabled,
        // those are precisely Baton's grant-derived dynamic tools. Disabling code_mode_host makes
        // the model attempt a tool path that is guaranteed to fail before any dynamic call reaches
        // Baton, so the host is an orchestration mechanism here, not an additional authority.
        // Dynamic tools are not inherited automatically by subagents. Keep delegation absent until
        // that inheritance has been measured; AllowsSubagents is a ceiling, not a requirement.
        features.Add("multi_agent");
        features.Add("multi_agent_v2");
        return features;
    }

    internal static JsonObject BuildThreadParams(
        CodexBrokerConfiguration configuration, CodexDynamicToolPolicy policy)
    {
        var tools = policy.BuildToolDefinitions();
        // #1996 re-review MEDIUM: the instruction used to say "only the provided baton_* dynamic
        // tools", a glob that by its own wording excluded the edit tool sitting in the very manifest
        // it constrains — the model obeying it lands back on "I cannot edit this workspace", which is
        // what #1996 measured. It names the edit tool from the policy's own constant, and only when
        // this thread actually declares it, so it stays a constraint on the list rather than a second
        // copy of it.
        var declaresEditTool = tools.Any(
            tool => tool?["name"]?.GetValue<string>() == CodexDynamicToolPolicy.ApplyPatchTool);
        var result = new JsonObject
        {
            ["cwd"] = configuration.WorkingDirectory,
            ["approvalPolicy"] = "never",
            ["sandbox"] = "read-only",
            ["ephemeral"] = false,
            ["model"] = configuration.Model,
            ["dynamicTools"] = tools,
            ["environments"] = new JsonArray(),
            ["developerInstructions"] =
                "You are a Baton worker. Use only the dynamic tools declared on this thread, whatever "
                + "their names."
                + (declaresEditTool
                    ? $" {CodexDynamicToolPolicy.ApplyPatchTool} is this thread's edit tool."
                    : string.Empty)
                + " A denied tool result is a hard permission boundary; do not seek another route.",
            ["config"] = new JsonObject
            {
                ["mcp_servers"] = new JsonObject(),
                ["web_search"] = "disabled",
                ["features"] = new JsonObject
                {
                    ["shell_tool"] = false,
                    ["unified_exec"] = false,
                    ["apps"] = false,
                    ["browser_use"] = false,
                    ["computer_use"] = false,
                    ["image_generation"] = false,
                    ["multi_agent"] = false,
                    ["multi_agent_v2"] = false,
                },
            },
        };
        if (configuration.Effort is { Length: > 0 })
        {
            result["config"]!["model_reasoning_effort"] = configuration.Effort;
        }
        if (configuration.ResumeSession)
        {
            result.Clear();
            result["threadId"] = configuration.SessionId;
            result["cwd"] = configuration.WorkingDirectory;
            result["approvalPolicy"] = "never";
            result["sandbox"] = "read-only";
            result["model"] = configuration.Model;
        }
        return result;
    }

    private static async Task HandleDynamicToolCallAsync(
        JsonObject message,
        CodexDynamicToolPolicy policy,
        TextWriter serverInput,
        TextWriter batonOutput,
        CancellationToken cancellationToken)
    {
        var tool = message["params"]?["tool"]?.GetValue<string>() ?? "unknown";
        var arguments = message["params"]?["arguments"];
        using var argumentsDocument = JsonDocument.Parse(arguments?.ToJsonString() ?? "{}");
        await EmitAsync(batonOutput, new JsonObject
        {
            ["type"] = "item.started",
            ["item"] = new JsonObject
            {
                ["type"] = "mcp_tool_call",
                ["tool"] = tool,
                // #1921: what makes "this lane re-issued a call it had already made" countable on codex.
                // Codex's own item.started names the tool and never its arguments, so without this the
                // repeat count has nothing to key on and CodexUsageParser.ToolInvocationKeys reports
                // none -- which is what it still does for a stream captured before this landed.
                //
                // A DIGEST rather than the arguments: a write_text call's arguments carry a whole file,
                // and a repeat count only ever asks two keys whether they are equal. Emitting the
                // arguments themselves would put a file's contents into the captured stream a second
                // time to answer a question 16 hex characters answer, and every byte of .stdout.log is
                // a byte the projector re-reads at settle and the rollover threshold counts.
                [CodexUsageParser.ArgumentsDigestField] = ArgumentsDigest(argumentsDocument.RootElement),
            },
        }).ConfigureAwait(false);
        var result = await policy.ExecuteAsync(tool, argumentsDocument.RootElement, cancellationToken)
            .ConfigureAwait(false);
        await SendAsync(serverInput, new JsonObject
        {
            ["id"] = message["id"]!.DeepClone(),
            ["result"] = new JsonObject
            {
                ["success"] = result.Success,
                ["contentItems"] = new JsonArray(new JsonObject
                {
                    ["type"] = "inputText",
                    ["text"] = result.Text,
                }),
            },
        }, cancellationToken).ConfigureAwait(false);
        await EmitAsync(batonOutput, new JsonObject
        {
            ["type"] = "item.completed",
            ["item"] = new JsonObject
            {
                ["type"] = "mcp_tool_call",
                ["tool"] = tool,
                ["status"] = result.Success ? "completed" : "failed",
                ["aggregated_output"] = result.Text,
            },
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// #1921: a short, stable fingerprint of one dynamic-tool call's arguments, as codex serialized
    /// them. Not a security boundary and not reversible-by-design — it exists only so two calls can be
    /// compared for equality without carrying their payloads through the stream. SHA-256 truncated to
    /// 16 hex characters: a collision would merge two distinct calls into one repeat, which costs an
    /// over-count of at most one on a diagnostic figure, and 64 bits makes that not worth the wider
    /// field.
    /// <para>
    /// The RAW text is hashed rather than a canonical re-serialization, so this fingerprint carries the
    /// same no-normalisation caveat <c>Status.ClaudeUsageParser.ToolInvocationKeys</c> states once for
    /// every vendor's key — that comment names what goes uncounted and why none of it is normalised.
    /// </para>
    /// </summary>
    private static string ArgumentsDigest(JsonElement arguments)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(arguments.GetRawText()));
        return Convert.ToHexStringLower(bytes)[..16];
    }

    private static async Task EmitCompletedItemAsync(JsonObject message, TextWriter output)
    {
        var item = message["params"]?["item"];
        if (item?["type"]?.GetValue<string>() != "agentMessage")
        {
            return;
        }
        await EmitAsync(output, new JsonObject
        {
            ["type"] = "item.completed",
            ["item"] = new JsonObject
            {
                ["type"] = "agent_message",
                ["text"] = item["text"]?.GetValue<string>() ?? string.Empty,
            },
        }).ConfigureAwait(false);
    }

    private static async Task<int> EmitTerminalTurnAsync(
        JsonObject message, JsonObject? usage, TextWriter output)
    {
        var turn = message["params"]?["turn"];
        var status = turn?["status"]?.GetValue<string>();
        if (status == "completed")
        {
            var terminal = new JsonObject { ["type"] = "turn.completed" };
            if (usage is not null)
            {
                terminal["usage"] = new JsonObject
                {
                    ["input_tokens"] = usage["inputTokens"]?.DeepClone(),
                    ["cached_input_tokens"] = usage["cachedInputTokens"]?.DeepClone(),
                    ["cache_write_input_tokens"] = usage["cacheWriteInputTokens"]?.DeepClone(),
                    ["output_tokens"] = usage["outputTokens"]?.DeepClone(),
                    ["reasoning_output_tokens"] = usage["reasoningOutputTokens"]?.DeepClone(),
                };
            }
            await EmitAsync(output, terminal).ConfigureAwait(false);
            return 0;
        }

        await EmitAsync(output, new JsonObject
        {
            ["type"] = "turn.failed",
            ["error"] = turn?["error"]?.DeepClone() ?? new JsonObject
            {
                ["message"] = $"Codex turn ended with status '{status ?? "unknown"}'.",
            },
        }).ConfigureAwait(false);
        return 1;
    }

    private static async Task<JsonObject> ReadResponseAsync(
        TextReader reader, int expectedId, TextWriter error, CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (!TryParseObject(line, out var message))
            {
                await error.WriteLineAsync($"Ignored non-JSON app-server output: {line}").ConfigureAwait(false);
                continue;
            }
            if (message["id"]?.GetValue<int>() != expectedId)
            {
                continue;
            }
            if (message["error"] is { } responseError)
            {
                throw new InvalidOperationException(
                    responseError["message"]?.GetValue<string>() ?? $"Codex app-server request {expectedId} failed.");
            }
            return message;
        }
        throw new IOException($"Codex app-server closed stdout before response {expectedId}.");
    }

    private static async Task SendAsync(
        TextWriter writer, JsonObject message, CancellationToken cancellationToken)
    {
        await writer.WriteLineAsync(message.ToJsonString().AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EmitAsync(TextWriter writer, JsonObject message)
    {
        await writer.WriteLineAsync(message.ToJsonString()).ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
    }

    private static bool TryParseObject(string line, out JsonObject message)
    {
        message = null!;
        try
        {
            message = JsonNode.Parse(line)?.AsObject()!;
            return message is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task DrainStderrAsync(
        TextReader source, TextWriter target, CancellationToken cancellationToken)
    {
        while (await source.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            await target.WriteLineAsync(line).ConfigureAwait(false);
        }
    }
}
