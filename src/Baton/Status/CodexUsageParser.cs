using System.Text.Json;
using Baton.Domain;

namespace Baton.Status;

/// <summary>
/// Parses the per-turn usage on Codex CLI JSONL <c>turn.completed</c> events (#1853). Codex reports
/// <c>input_tokens</c> inclusive of <c>cached_input_tokens</c>; Baton's additive shape keeps those
/// dimensions disjoint, so <see cref="WorkerUsage.TokensIn"/> is the non-cached remainder.
/// </summary>
public sealed class CodexUsageParser : IWorkerUsageParser
{
    public bool TryParseFinalUsage(string rawLine, out WorkerUsage? usage) =>
        TryParse(rawLine, out usage);

    public bool TryParseIncrementalUsage(string rawLine, out WorkerUsage? usage) =>
        TryParse(rawLine, out usage);

    public string? TryParseToolName(string rawLine)
    {
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(rawLine);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var eventType)
                || eventType.GetString() != "item.started"
                || !root.TryGetProperty("item", out var item)
                || item.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return ReadString(item, "type") switch
            {
                "command_execution" => ReadString(item, "command") is { Length: > 0 } command
                    ? command
                    : "command",
                "file_change" => "file change",
                "mcp_tool_call" => ReadString(item, "tool") ?? ReadString(item, "name") ?? "MCP tool",
                "web_search" => "web search",
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public int CountToolSteps(string rawLine) => TryParseToolName(rawLine) is null ? 0 : 1;

    /// <summary>
    /// #1927: the model codex reports having RUN, off either lifecycle event that can name one —
    /// <c>thread.started</c> (stamped by <c>Baton.Vendors.CodexAppServerBroker</c> from the
    /// app-server's own <c>thread/start</c> answer, when that answer names a model) or
    /// <c>turn.completed</c>. Both are read because they are two independent chances at the same fact
    /// and the projector keeps the last one, so a mid-execution substitution announced on the terminal
    /// event wins over the thread's opening claim.
    /// <para>
    /// Absent-safe by construction: neither event is REQUIRED to carry <c>model</c>, and one that does
    /// not simply yields null here — the same absence agy's stream produces structurally, never a blank
    /// string.
    /// </para>
    /// </summary>
    public string? TryParseEchoedModel(string rawLine)
    {
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(rawLine);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || ReadString(root, "type") is not ("thread.started" or "turn.completed"))
            {
                return null;
            }

            return ReadString(root, "model") is { Length: > 0 } model ? model : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryParse(string rawLine, out WorkerUsage? usage)
    {
        usage = null;
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(rawLine);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type)
                || type.GetString() != "turn.completed"
                || !root.TryGetProperty("usage", out var reported)
                || reported.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var totalInput = ReadLong(reported, "input_tokens");
            var cachedInput = ReadLong(reported, "cached_input_tokens");
            var cacheWrite = ReadLong(reported, "cache_write_input_tokens");
            var output = ReadLong(reported, "output_tokens");
            var reasoning = ReadLong(reported, "reasoning_output_tokens");
            if (totalInput is null && cachedInput is null && cacheWrite is null
                && output is null && reasoning is null)
            {
                return false;
            }

            long? nonCachedInput = totalInput;
            if (totalInput is { } total && cachedInput is { } cached)
            {
                // An impossible vendor reading stays conservative rather than creating a negative token count.
                nonCachedInput = Math.Max(0, total - cached);
            }

            usage = new WorkerUsage(
                TokensIn: nonCachedInput,
                TokensOut: output,
                Turns: 1,
                CacheReadTokens: cachedInput,
                CacheCreationTokens: cacheWrite,
                ThinkingTokens: reasoning);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static long? ReadLong(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.Number
        && property.TryGetInt64(out var value)
            ? value
            : null;

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}
