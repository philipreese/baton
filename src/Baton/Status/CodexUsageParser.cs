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

    /// <summary>
    /// #1921. Codex announces a call on <c>item.started</c> and reports its result on
    /// <c>item.completed</c>, so a refusal is counted on a DIFFERENT line from the one
    /// <see cref="CountToolSteps"/> counts — that asymmetry is
    /// <see cref="IWorkerUsageParser.CountRefusedToolSteps"/>'s general contract, and codex is where it
    /// is most visible. The payload is <c>item.aggregated_output</c>, which is
    /// <c>CodexDynamicToolResult.Text</c> verbatim (<c>Baton.Vendors.CodexAppServerBroker</c> copies it
    /// there), so the marker arrives unwrapped on this vendor.
    /// </summary>
    public int CountRefusedToolSteps(string rawLine) =>
        TryReadCompletedToolItem(rawLine, out var item)
            && GrantRefusal.IsRefusal(ReadString(item, "aggregated_output"))
                ? 1
                : 0;

    /// <summary>
    /// #1998. The same <c>item.completed</c> anchor, narrowed to the one tool that can carry a command
    /// ceiling at all (<see cref="RunCommandToolName"/>), so an unrelated failed read or write is
    /// <see langword="false"/> rather than being left out of the ordering the reader depends on. The
    /// payload is <c>aggregated_output</c> — <c>CodexDynamicToolResult.Text</c> verbatim — so the marker
    /// arrives unwrapped here exactly as the refusal marker does.
    /// <para>
    /// <b>The STATUS is read too, and it is what the tool anchor alone does not buy.</b> A timeout is a
    /// <c>Failed</c> result, which the broker stamps <c>"failed"</c>; a command that SUCCEEDED and merely
    /// printed the marker — this repository's own source, a diff of it — is <c>"completed"</c> and is
    /// <see langword="false"/> here. That is the same acceptance <c>GrantRefusal</c> tolerates for a
    /// COUNT, refused here because this answer is a binary causal claim decided by one final item rather
    /// than one over-count on a tally.
    /// </para>
    /// </summary>
    public bool? ReportsShippingCeilingTimeout(string rawLine) =>
        TryReadCompletedToolItem(rawLine, out var item)
            && ReadString(item, "tool") == RunCommandToolName
                ? ReadString(item, "status") == "failed"
                    && ShellCommandCeilings.IsShippingCeilingTimeout(ReadString(item, "aggregated_output"))
                : null;

    /// <summary>
    /// The dynamic tool whose result <see cref="ReportsShippingCeilingTimeout"/> anchors on. Named here
    /// for the same reason <see cref="ArgumentsDigestField"/> is: <c>Baton.Vendors</c> declares the tool
    /// and this project reads its results back, the dependency runs one way only, and this is the one
    /// symbol both can see. <c>Baton.Vendors.CodexDynamicToolPolicy.RunCommandTool</c> is this constant.
    /// </summary>
    public const string RunCommandToolName = "baton_run_command";

    /// <summary>
    /// #1921. The same completed item with an <c>aggregated_output</c> that is present and blank. A
    /// <c>"status":"failed"</c> item is not an empty result — its payload is its reason — and a refusal
    /// is a failed item by construction, so neither is counted here.
    /// </summary>
    public int CountEmptyToolResults(string rawLine) =>
        TryReadCompletedToolItem(rawLine, out var item)
            && ReadString(item, "status") != "failed"
            && item.TryGetProperty("aggregated_output", out var output)
            && output.ValueKind == JsonValueKind.String
            && string.IsNullOrWhiteSpace(output.GetString())
                ? 1
                : 0;

    /// <summary>
    /// #1921. <c>tool</c> plus the <c>argumentsDigest</c> Baton's own broker stamps on the
    /// <c>item.started</c> envelope (<c>Baton.Vendors.CodexAppServerBroker</c> — that method's comment
    /// states why a digest rather than the arguments themselves).
    /// <para>
    /// <b>No digest, no key</b>, and the two cases that produces are both real: a stream captured before
    /// the digest landed, and any codex tool call that did not go through Baton's dynamic-tool broker.
    /// Codex's native <c>command_execution</c>/<c>file_change</c> items are not keyed for that reason —
    /// Baton grants codex no native shell or file tool at all (<c>CodexDynamicToolPolicy</c>), so such an
    /// item cannot occur in a Baton-driven stream, and inventing a key for one would be a shape nothing
    /// here has measured. Contributing nothing rather than keying on the tool name alone is
    /// <see cref="IWorkerUsageParser.ToolInvocationKeys"/>'s general rule, applied here.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> ToolInvocationKeys(string rawLine)
    {
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(rawLine);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var eventType) || eventType.GetString() != "item.started"
                || !root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object
                || ReadString(item, "type") != "mcp_tool_call"
                || ReadString(item, "tool") is not { Length: > 0 } tool
                || ReadString(item, ArgumentsDigestField) is not { Length: > 0 } digest)
            {
                return [];
            }

            return [tool + " " + digest];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// The <c>item.started</c> field <c>Baton.Vendors.CodexAppServerBroker</c> writes and
    /// <see cref="ToolInvocationKeys"/> reads — named once here because those two are in different
    /// projects (<c>Baton.Vendors</c> → <c>Baton</c>, never the reverse), so this is the only symbol
    /// both can see. A rename that reached one and not the other would silently stop the repeat count.
    /// </summary>
    public const string ArgumentsDigestField = "argumentsDigest";

    /// <summary>
    /// A completed dynamic-tool item — the one anchor <see cref="CountRefusedToolSteps"/> and
    /// <see cref="CountEmptyToolResults"/> share.
    /// </summary>
    /// <remarks>The node is cloned out so it outlives the parsed document's <c>using</c>.</remarks>
    private static bool TryReadCompletedToolItem(string rawLine, out JsonElement item)
    {
        item = default;
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(rawLine);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var eventType) || eventType.GetString() != "item.completed"
                || !root.TryGetProperty("item", out var candidate) || candidate.ValueKind != JsonValueKind.Object
                || ReadString(candidate, "type") is not ("mcp_tool_call" or "command_execution"))
            {
                return false;
            }

            item = candidate.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
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
