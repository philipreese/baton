using System.Text.Json;
using Baton.Domain;

namespace Baton.Status;

/// <summary>
/// Parses the per-turn usage on Codex CLI JSONL <c>turn.completed</c> events (#1853). Codex reports
/// <c>input_tokens</c> inclusive of <c>cached_input_tokens</c>; Baton's additive shape keeps those
/// dimensions disjoint, so <see cref="WorkerUsage.TokensIn"/> is the non-cached remainder.
/// <para>
/// <b>No <see cref="IWorkerUsageParser.TryParseEchoedModel"/> override, because codex has no reachable
/// source for one</b> (#1927 review HIGH). The absence is a DIFFERENT kind from agy's beside it: agy's
/// vendor stream was measured to carry no <c>model</c> key, whereas codex never reaches Baton as a
/// vendor stream at all. Both lifecycle events on this vendor's stdout are synthesized by
/// <c>Baton.Vendors.CodexAppServerBroker</c> — <c>thread.started</c> carries a thread id and nothing
/// else, <c>turn.completed</c> a usage object and nothing else — so a parser reading either would be
/// reading Baton's own two keys back. The emitter is in-tree, which makes this deterministic rather
/// than a sample; the captured stream agrees (<c>tests/Baton.Cli.Tests/Fixtures/codex-live-stream.jsonl</c>,
/// 261 lines, no <c>model</c> key on any of them), and neither does the recorded app-server event
/// grammar name one — the probe document is the one <c>WorkerBindingConfigEntry.EffortResolved</c>
/// already cites by path. So <c>modelEchoed</c> is ABSENT
/// on every codex row and the fact is UNMEASURED rather than measured-negative: stamping the broker's
/// own <c>configuration.Model</c> onto its <c>thread.started</c> would echo Baton's INTENT, which is
/// exactly what claude's <c>system:init</c> is refused for (<see cref="ClaudeUsageParser"/>). Closing
/// it needs the app-server's own answer to <c>thread/start</c> inspected for a model field, which no
/// in-tree recording carries. spec/baton.md §7's ledger row is the register.
/// </para>
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
