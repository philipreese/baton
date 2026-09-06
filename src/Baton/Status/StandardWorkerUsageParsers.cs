using System.Text.Json;
using Baton.Domain;

namespace Baton.Status;

/// <summary>
/// Built-in usage parsers for vendor CLI streaming logs (issue #1360).
/// </summary>
public static class StandardWorkerUsageParsers
{
    public static IReadOnlyDictionary<string, IWorkerUsageParser> Default { get; } =
        new Dictionary<string, IWorkerUsageParser>(StringComparer.Ordinal)
        {
            ["claude"] = new ClaudeUsageParser(),
            ["agy"] = new AgyUsageParser(),
            ["codex"] = new CodexUsageParser(),
        };
}

/// <summary>
/// Parses claude's <c>stream-json</c> terminal <c>"type":"result"</c> line (issue #1360, extended by
/// #1569). The sole implementation for this vendor (#1599) -- <c>ClaudeWorkerAdapter.TryParseFinalUsage</c>
/// delegates here rather than re-implementing the same read, closing the drift #1590's fix left
/// behind: an all-null result (no tokens, no turns, no cache/thinking figures) now returns
/// <see langword="false"/> here too, matching the guard the adapter carried before it delegated here, because a usage record with
/// nothing in it claims nothing.
/// <c>usage.input_tokens</c>/<c>output_tokens</c>/<c>cache_creation_input_tokens</c>/
/// <c>cache_read_input_tokens</c>, the nested <c>usage.output_tokens_details.thinking_tokens</c>, and
/// top-level <c>num_turns</c> are each read independently: a line reporting some and not others yields
/// exactly the fields it reported, never a fabricated zero (docs/vendor-capabilities.md's "Usage,
/// cost and quota" section is the register this reads against). <c>total_cost_usd</c> is real on this
/// vendor but outside #1569's additive shape, so it is read by nothing here.
/// <para>
/// <b>Scope, corrected by #1706 and again per-field by #1724: this reads <c>modelUsage</c>, the
/// WHOLE-TREE figure, and falls back to top-level <c>usage</c> PER FIELD -- independently for each of
/// the five figures, not only when the line carries no <c>modelUsage</c> at all.</b> The prior
/// reading was top-level-only and this doc recorded that as a known shortfall (docs/vendor-doc-audit.md,
/// #479: 22% on a single subagent, growing with the tree) while ruling <c>modelUsage</c> unreadable
/// because "summing it correctly needs a per-model breakdown this shape's scalars cannot carry". That
/// objection was about COST, which weights per model; this shape carries no cost field, and a TOKEN
/// COUNT sums across models without any breakdown being lost. Measured on spec/baton.md §3's two claude
/// evidence rooms: one moves from 298,095 to 884,568 billed tokens, and the other does not move at all
/// (294,769 both ways, its <c>modelUsage</c> being identical to its top-level <c>usage</c> field for
/// field) -- which is what reading another field looks like, as against rescaling every figure. **What
/// decides which room falls where is unmeasured**; spec/baton.md §3 carries the retraction of a first,
/// wrong answer (subagent fan-out) and the sweep that falsified it -- do not reintroduce a mechanism here.
/// AER caps a worker's own subagent fan-out at depth 1
/// (<c>ClaudeWorkerAdapter.MaxSubagentSpawnDepthVariable</c>) rather than zero, so a whole-tree read is
/// a real, reachable need either way. <c>num_turns</c> stays a top-level read --
/// <c>modelUsage</c> has no analogue. Per <c>spec/baton.md</c> §7, none of this shape is the reset-time
/// source of truth -- it is attribution, and the fleet-level <c>/usage</c> poll is what that section
/// rules authoritative.
/// </para>
/// </summary>
public sealed class ClaudeUsageParser : IWorkerUsageParser
{
    public bool TryParseFinalUsage(string rawLine, out WorkerUsage? usage)
    {
        usage = null;
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawLine);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var typeProp)
                || typeProp.GetString() != "result")
            {
                return false;
            }

            long? tokensIn = null;
            long? tokensOut = null;
            long? cacheReadTokens = null;
            long? cacheCreationTokens = null;
            long? thinkingTokens = null;
            // #1706, corrected per-field by #1724 item 4: whole-tree first. Each of the five figures
            // that modelUsage did not itself carry falls back independently to the top-level,
            // main-thread-only `usage` object below -- so a modelUsage entry missing one figure (e.g.
            // thinkingTokens) still gets it from the top level instead of losing it, and a capture
            // predating the field (or a vendor build that stops emitting it) still yields every figure
            // it always did.
            // #1883 review F1: the map's KEYS survive the sum now. Discarding them is what let a
            // whole-tree, possibly multi-model total be priced at one requested model's rate.
            IReadOnlyList<string>? modelsObserved = null;
            if (TryReadModelUsageTotals(root, ref tokensIn, ref tokensOut, ref cacheReadTokens, ref cacheCreationTokens, ref thinkingTokens, out var observed))
            {
                modelsObserved = observed;
            }

            if (root.TryGetProperty("usage", out var usageProp) && usageProp.ValueKind == JsonValueKind.Object)
            {
                if (tokensIn is null && usageProp.TryGetProperty("input_tokens", out var inProp) && inProp.TryGetInt64(out var inTokens))
                {
                    tokensIn = inTokens;
                }

                if (tokensOut is null && usageProp.TryGetProperty("output_tokens", out var outProp) && outProp.TryGetInt64(out var outTokens))
                {
                    tokensOut = outTokens;
                }

                if (cacheReadTokens is null && usageProp.TryGetProperty("cache_read_input_tokens", out var cacheReadProp) && cacheReadProp.TryGetInt64(out var cacheReadValue))
                {
                    cacheReadTokens = cacheReadValue;
                }

                if (cacheCreationTokens is null && usageProp.TryGetProperty("cache_creation_input_tokens", out var cacheCreationProp) && cacheCreationProp.TryGetInt64(out var cacheCreationValue))
                {
                    cacheCreationTokens = cacheCreationValue;
                }

                if (thinkingTokens is null
                    && usageProp.TryGetProperty("output_tokens_details", out var outputDetailsProp)
                    && outputDetailsProp.ValueKind == JsonValueKind.Object
                    && outputDetailsProp.TryGetProperty("thinking_tokens", out var thinkingProp)
                    && thinkingProp.TryGetInt64(out var thinkingValue))
                {
                    thinkingTokens = thinkingValue;
                }
            }

            int? turns = null;
            if (root.TryGetProperty("num_turns", out var turnsProp) && turnsProp.TryGetInt32(out var turnCount))
            {
                turns = turnCount;
            }

            if (tokensIn is null && tokensOut is null && turns is null
                && cacheReadTokens is null && cacheCreationTokens is null && thinkingTokens is null)
            {
                return false;
            }

            usage = new WorkerUsage(
                tokensIn, tokensOut, turns, cacheReadTokens, cacheCreationTokens, thinkingTokens, ModelsObserved: modelsObserved);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// #1706: sums the terminal line's <c>modelUsage</c> map — one entry per model the whole execution
    /// TREE used, subagents included — into the four token figures plus <c>thinkingTokens</c>. Its keys
    /// are camelCase (<c>inputTokens</c>/<c>outputTokens</c>/<c>cacheReadInputTokens</c>/
    /// <c>cacheCreationInputTokens</c>/<c>thinkingTokens</c>), NOT the snake_case names the sibling
    /// top-level <c>usage</c> object uses, which is why this cannot share the reader above.
    /// Each figure is accumulated independently: a model entry reporting some and not others
    /// contributes exactly what it reported. Returns <see langword="false"/> — leaving every ref
    /// argument untouched — when there is no <c>modelUsage</c> object, or when it is present but no
    /// entry yielded a single figure, so the caller's top-level fallback is reached on a shape this
    /// could not read rather than on one it read as all-zero.
    /// <para>
    /// #1883 review F1: <paramref name="modelsObserved"/> reports WHICH models the returned totals were
    /// summed across — every object-valued key of the map, in file order. It is populated only on the
    /// <see langword="true"/> return, i.e. exactly when these totals are the whole-tree figure; on a
    /// <see langword="false"/> return the caller falls back to the single-model top-level object and
    /// there is no model name to report. <see cref="WorkerUsage.ModelsObserved"/> is where the keys land
    /// and why they now have to survive the sum.
    /// </para>
    /// </summary>
    private static bool TryReadModelUsageTotals(
        JsonElement root,
        ref long? tokensIn,
        ref long? tokensOut,
        ref long? cacheReadTokens,
        ref long? cacheCreationTokens,
        ref long? thinkingTokens,
        out IReadOnlyList<string>? modelsObserved)
    {
        modelsObserved = null;
        if (!root.TryGetProperty("modelUsage", out var modelUsage) || modelUsage.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var names = new List<string>();
        long? summedIn = null;
        long? summedOut = null;
        long? summedCacheRead = null;
        long? summedCacheCreation = null;
        long? summedThinking = null;
        foreach (var model in modelUsage.EnumerateObject())
        {
            if (model.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            names.Add(model.Name);
            Accumulate(model.Value, "inputTokens", ref summedIn);
            Accumulate(model.Value, "outputTokens", ref summedOut);
            Accumulate(model.Value, "cacheReadInputTokens", ref summedCacheRead);
            Accumulate(model.Value, "cacheCreationInputTokens", ref summedCacheCreation);
            Accumulate(model.Value, "thinkingTokens", ref summedThinking);
        }

        if (summedIn is null && summedOut is null && summedCacheRead is null && summedCacheCreation is null && summedThinking is null)
        {
            return false;
        }

        tokensIn = summedIn;
        tokensOut = summedOut;
        cacheReadTokens = summedCacheRead;
        cacheCreationTokens = summedCacheCreation;
        thinkingTokens = summedThinking;
        modelsObserved = names;
        return true;

        static void Accumulate(JsonElement model, string propertyName, ref long? running)
        {
            if (model.TryGetProperty(propertyName, out var prop) && prop.TryGetInt64(out var value))
            {
                running = (running ?? 0) + value;
            }
        }
    }

    /// <summary>
    /// #1623: reads <c>message.usage</c> off a mid-stream <c>"type":"assistant"</c> line — the only
    /// place in the shipped <c>stream-json --verbose</c> mode where per-message usage appears at all.
    /// <c>num_turns</c> and <c>output_tokens_details.thinking_tokens</c> are NOT claimed on this line,
    /// so this deliberately leaves <see cref="WorkerUsage.Turns"/>/<see cref="WorkerUsage.ThinkingTokens"/>
    /// null here rather than reusing the terminal-line reader. The per-line/per-turn summing contract is
    /// <see cref="IWorkerUsageParser.TryParseIncrementalUsage"/>'s.
    /// <c>message.id</c> is also read onto <see cref="WorkerUsage.MessageId"/> (#1686 review F6):
    /// several consecutive <c>"type":"assistant"</c> lines can carry the SAME <c>message.id</c> and an
    /// identical <c>usage</c> object (one API response split across content-block chunks) — a caller
    /// summing every line's usage without deduping by this field over-counts by however many chunks
    /// that message split into.
    /// <para>
    /// <b>#1706: <c>input_tokens</c> and <c>output_tokens</c> on this line are PLACEHOLDERS and are
    /// deliberately NOT read.</b> Which columns here are the vendor's real figures for this message is
    /// docs/vendor-capabilities.md's measurement, not restated here; the two cache keys are the real
    /// pair, and they are what <see cref="WorkerUsage.BilledTokens"/> accumulates on this vendor. Because a
    /// billed component is therefore structurally missing from every live reading, each one carries
    /// <see cref="WorkerUsage.BilledIsFloor"/> — spec/baton.md §3 rules on what that costs the budget.
    /// </para>
    /// <para>
    /// Consequently a usage object carrying ONLY the two placeholder keys and neither cache key yields
    /// no reading at all (<see langword="false"/>), rather than a <see cref="WorkerUsage"/> whose every
    /// figure is null — a deliberate call, not an oversight: it keeps the never-fabricate-a-zero
    /// convention the rest of this file follows, and the register above records that every
    /// usage-bearing line on measured traffic carries all four keys together, so it is unreachable
    /// there.
    /// </para>
    /// </summary>
    public bool TryParseIncrementalUsage(string rawLine, out WorkerUsage? usage)
    {
        usage = null;
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawLine);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var typeProp)
                || typeProp.GetString() != "assistant"
                || !root.TryGetProperty("message", out var message)
                || message.ValueKind != JsonValueKind.Object
                || !message.TryGetProperty("usage", out var usageProp)
                || usageProp.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            long? cacheReadTokens = usageProp.TryGetProperty("cache_read_input_tokens", out var cacheReadProp) && cacheReadProp.TryGetInt64(out var cacheReadValue) ? cacheReadValue : null;
            long? cacheCreationTokens = usageProp.TryGetProperty("cache_creation_input_tokens", out var cacheCreationProp) && cacheCreationProp.TryGetInt64(out var cacheCreationValue) ? cacheCreationValue : null;
            string? messageId = message.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String ? idProp.GetString() : null;
            // #1666: a sub-agent's own turn carries a parent_tool_use_id field, set and non-null, at
            // the line's ROOT (not inside "message") -- spec/baton.md §3 has the measured shape.
            var isSubAgentTurn = root.TryGetProperty("parent_tool_use_id", out var parentToolUseIdProp)
                && parentToolUseIdProp.ValueKind == JsonValueKind.String;

            if (cacheReadTokens is null && cacheCreationTokens is null)
            {
                return false;
            }

            usage = new WorkerUsage(
                CacheReadTokens: cacheReadTokens,
                CacheCreationTokens: cacheCreationTokens,
                MessageId: messageId,
                BilledIsFloor: true,
                IsSubAgentTurn: isSubAgentTurn);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// #1623: a <c>"type":"assistant"</c> message's <c>tool_use</c> content block name, per the
    /// standard Anthropic Messages API streaming shape claude's own <c>stream-json</c> output is built
    /// on — not independently doc-audited the way the usage fields above are (docs/vendor-capabilities.md
    /// carries no dedicated finding for this specific field), so this degrades to null on any shape
    /// drift rather than throwing. First matching block wins; a message with several tool calls in one
    /// turn is a real but rare shape this simplifies rather than enumerating.
    /// </summary>
    public string? TryParseToolName(string rawLine)
    {
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawLine);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var typeProp) || typeProp.GetString() != "assistant"
                || !root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
                || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind == JsonValueKind.Object
                    && block.TryGetProperty("type", out var blockType) && blockType.GetString() == "tool_use"
                    && block.TryGetProperty("name", out var nameProp) && nameProp.GetString() is { Length: > 0 } name)
                {
                    return name;
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// #1682, unit fixed by #1686 review F2: the number of REAL tool calls a <c>"type":"assistant"</c>
    /// message reports — one per <c>tool_use</c> content block, every block, not just the first
    /// (unlike <see cref="TryParseToolName"/>, whose single display name would undercount a multi-tool
    /// turn). This is the SAME unit <see cref="AgyUsageParser.CountToolSteps"/> now counts (one per
    /// real tool call, at that call's terminal lifecycle line) — <c>MaxToolSteps</c> is comparable
    /// across vendors as of this fix; spec/baton.md §3 states the unit once. Same top-level shape
    /// <see cref="TryParseToolName"/> reads, deliberately not delegating to it.
    /// </summary>
    public int CountToolSteps(string rawLine)
    {
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return 0;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawLine);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var typeProp) || typeProp.GetString() != "assistant"
                || !root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
                || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                return 0;
            }

            var count = 0;
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind == JsonValueKind.Object
                    && block.TryGetProperty("type", out var blockType) && blockType.GetString() == "tool_use")
                {
                    count++;
                }
            }

            return count;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    /// <summary>
    /// #1927: the model claude reports having RUN — the terminal <c>"type":"result"</c> event's own
    /// <c>model</c>, falling back to an assistant turn's <c>message.model</c> when the result event
    /// carries none. Both are the CLI's own resolution of whatever id it was handed.
    /// <para>
    /// <b>Only the fallback is measured</b> (#1927 review LOW): <c>docs/vendor-doc-audit.md</c> §5
    /// records the assistant turn's <c>message.model</c>. The <c>result</c> rung is UNMEASURED here —
    /// the repository's only captured result lines
    /// (<c>tests/Baton.Vendors.Tests/Fixtures/claude-weekly-limit-result.captured.jsonl</c>) are both
    /// error results carrying <c>modelUsage</c> and no top-level <c>model</c>, which does not
    /// generalise to a successful one either way. It is read first and kept because claude answers
    /// through the measured fallback regardless, so the rung is free if the vendor never populates it;
    /// capturing one successful result line into the audit is what would settle it. Contrast codex,
    /// where the absence is not a gap in measurement but a structural one — <see cref="CodexUsageParser"/>.
    /// </para>
    /// <para>
    /// <b><c>"type":"system"</c> is deliberately not read</b>, and that is the whole discrimination this
    /// method exists for: <c>docs/vendor-doc-audit.md</c> §5 measured that <c>system:init</c> echoes the
    /// <c>--model</c> string VERBATIM — a bogus id is echoed back unchanged and the turn then fails with
    /// <c>model:"&lt;synthetic&gt;"</c> — so reading init would make this field a second copy of
    /// <c>CostLedgerEntry.Model</c> (intent) under a name that promises the opposite (what ran).
    /// </para>
    /// <para>
    /// <b>A failed turn's echo is the literal string <c>&lt;synthetic&gt;</c></b>, from the same
    /// measurement: an unrecognized model id resolves to <c>model:"&lt;synthetic&gt;"</c> with
    /// <c>is_error:true</c>. That is not a parser defect and is deliberately not filtered — it is the
    /// vendor's own answer to "what ran", it is true (nothing did), and inventing an absence for it
    /// would hide a failed dispatch behind the same blank a healthy agy row wears.
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
            using var doc = JsonDocument.Parse(rawLine);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var typeProp))
            {
                return null;
            }

            return typeProp.GetString() switch
            {
                "result" => ReadModel(root),
                "assistant" => root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object
                    ? ReadModel(message)
                    : null,
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadModel(JsonElement element) =>
        element.TryGetProperty("model", out var model)
        && model.ValueKind == JsonValueKind.String
        && model.GetString() is { Length: > 0 } name
            ? name
            : null;
}

/// <summary>
/// Parses agy's <c>stream-json</c> terminal <c>"event":"result"</c> line (issue #1360, extended by
/// #1569). The sole implementation for this vendor (#1599) -- <c>AgyWorkerAdapter.TryParseFinalUsage</c>
/// delegates here rather than re-implementing the same read. agy's <c>result.usage</c> shape is
/// inconsistent across observed captures (#1088, docs/vendor-capabilities.md): sometimes a full
/// breakdown (<c>input_tokens</c>/<c>output_tokens</c>/<c>thinking_tokens</c>/<c>cache_read_tokens</c>/
/// <c>total_tokens</c>), sometimes only <c>total_tokens</c>. Only <c>input_tokens</c>/
/// <c>output_tokens</c> map to this shape's <c>tokensIn</c>/<c>tokensOut</c> -- a lone
/// <c>total_tokens</c> is a real number but not a direction, and splitting it would fabricate a
/// breakdown agy never reported. <c>thinking_tokens</c>/<c>cache_read_tokens</c> read the same way,
/// independently of each other and of the input/output split. agy has never been observed reporting a
/// cache-creation figure (docs/vendor-capabilities.md), so this parser has no field to bind
/// <see cref="WorkerUsage.CacheCreationTokens"/> to and leaves it null rather than inventing one.
/// Turns come from <c>result.num_turns</c>, read independently of the usage object. This shape is
/// attribution, never the reset-time source of truth -- see <see cref="ClaudeUsageParser"/>'s own doc
/// comment for the <c>spec/baton.md</c> §7 ruling this rests on.
/// <para>
/// <b>No <see cref="IWorkerUsageParser.TryParseEchoedModel"/> override, on purpose</b> (#1927): agy's
/// stream was measured — a whole real room's capture — to carry no <c>model</c> key on any event, so
/// this vendor has nothing to echo and inherits the interface's null. That is why the resolved-at-bind
/// half of #1927 exists at all: for agy it is the only surface that can name a model, and
/// <c>CostLedgerEntry.ModelEchoed</c> stays ABSENT on every agy row rather than blank.
/// </para>
/// </summary>
public sealed class AgyUsageParser : IWorkerUsageParser
{
    public bool TryParseFinalUsage(string rawLine, out WorkerUsage? usage)
    {
        usage = null;
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawLine);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("event", out var eventProp) || eventProp.GetString() != "result"
                || !root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            long? tokensIn = null;
            long? tokensOut = null;
            long? cacheReadTokens = null;
            long? thinkingTokens = null;
            if (result.TryGetProperty("usage", out var usageProp) && usageProp.ValueKind == JsonValueKind.Object)
            {
                if (usageProp.TryGetProperty("input_tokens", out var inProp) && inProp.TryGetInt64(out var inTokens))
                {
                    tokensIn = inTokens;
                }

                if (usageProp.TryGetProperty("output_tokens", out var outProp) && outProp.TryGetInt64(out var outTokens))
                {
                    tokensOut = outTokens;
                }

                if (usageProp.TryGetProperty("cache_read_tokens", out var cacheReadProp) && cacheReadProp.TryGetInt64(out var cacheReadValue))
                {
                    cacheReadTokens = cacheReadValue;
                }

                if (usageProp.TryGetProperty("thinking_tokens", out var thinkingProp) && thinkingProp.TryGetInt64(out var thinkingValue))
                {
                    thinkingTokens = thinkingValue;
                }
            }

            int? turns = result.TryGetProperty("num_turns", out var turnsProp) && turnsProp.TryGetInt32(out var turnsValue)
                ? turnsValue
                : null;

            if (tokensIn is null && tokensOut is null && turns is null && cacheReadTokens is null && thinkingTokens is null)
            {
                return false;
            }

            usage = new WorkerUsage(tokensIn, tokensOut, turns, cacheReadTokens, ThinkingTokens: thinkingTokens);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// #1623: reads <c>step_update.usage</c> off a DONE-state <c>"event":"step_update"</c> line —
    /// measured live against a real agy lane's captured <c>.stdout.log</c> (2026-09-02): a
    /// <c>step_type":"agent_response"</c> step's DONE update carries the identical
    /// <c>input_tokens</c>/<c>output_tokens</c>/<c>thinking_tokens</c>/<c>cache_read_tokens</c>/
    /// <c>total_tokens</c> shape this class's own <see cref="TryParseFinalUsage"/> reads off the
    /// terminal <c>result</c> event's <c>usage</c> object -- same field names, different envelope. One
    /// line = one step's own usage; see <see cref="IWorkerUsageParser.TryParseIncrementalUsage"/> for
    /// the output-additive/input-level split a caller applies to it.
    /// #1686 review F4: gates on <c>step_type == "agent_response"</c> too, not just DONE state — the
    /// glass-side gate this now matches is spec/baton.md §3's own case, not restated here. Measured
    /// against the real `dispatch-implement-38c24d11` capture: no DONE/<c>step_type=="tool"</c> line in
    /// that stream carries a <c>usage</c> object, so this filter changes nothing observed there — it
    /// closes the gap for a shape that has not yet been seen rather than one that has.
    /// </summary>
    public bool TryParseIncrementalUsage(string rawLine, out WorkerUsage? usage)
    {
        usage = null;
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawLine);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("event", out var eventProp) || eventProp.GetString() != "step_update"
                || !root.TryGetProperty("step_update", out var stepUpdate) || stepUpdate.ValueKind != JsonValueKind.Object
                || !stepUpdate.TryGetProperty("state", out var stateProp) || stateProp.GetString() != "DONE"
                || !stepUpdate.TryGetProperty("step_type", out var stepTypeProp) || stepTypeProp.GetString() != "agent_response"
                || !stepUpdate.TryGetProperty("usage", out var usageProp) || usageProp.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            long? tokensIn = usageProp.TryGetProperty("input_tokens", out var inProp) && inProp.TryGetInt64(out var inTokens) ? inTokens : null;
            long? tokensOut = usageProp.TryGetProperty("output_tokens", out var outProp) && outProp.TryGetInt64(out var outTokens) ? outTokens : null;
            long? cacheReadTokens = usageProp.TryGetProperty("cache_read_tokens", out var cacheReadProp) && cacheReadProp.TryGetInt64(out var cacheReadValue) ? cacheReadValue : null;
            long? thinkingTokens = usageProp.TryGetProperty("thinking_tokens", out var thinkingProp) && thinkingProp.TryGetInt64(out var thinkingValue) ? thinkingValue : null;

            if (tokensIn is null && tokensOut is null && cacheReadTokens is null && thinkingTokens is null)
            {
                return false;
            }

            usage = new WorkerUsage(tokensIn, tokensOut, CacheReadTokens: cacheReadTokens, ThinkingTokens: thinkingTokens);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// #1623: a <c>"step_type":"tool"</c> step_update's own <c>tool_name</c> — measured against the
    /// same real agy lane capture <see cref="TryParseIncrementalUsage"/>'s doc names.
    /// </summary>
    public string? TryParseToolName(string rawLine)
    {
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawLine);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("event", out var eventProp) || eventProp.GetString() != "step_update"
                || !root.TryGetProperty("step_update", out var stepUpdate) || stepUpdate.ValueKind != JsonValueKind.Object
                || !stepUpdate.TryGetProperty("step_type", out var stepTypeProp) || stepTypeProp.GetString() != "tool"
                || !stepUpdate.TryGetProperty("tool_name", out var toolNameProp))
            {
                return null;
            }

            return toolNameProp.GetString() is { Length: > 0 } name ? name : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// #1682, unit fixed by #1686 review F2: 1 for a <c>"step_type":"tool"</c> step_update carrying a
    /// <c>tool_name</c> AT ITS TERMINAL LIFECYCLE STATE (<c>DONE</c> or <c>ERROR</c>) — one per REAL
    /// tool call, the same unit <see cref="ClaudeUsageParser.CountToolSteps"/> counts. Originally this
    /// counted the SAME gate <see cref="TryParseToolName"/> uses with no <c>state</c> filter at all, so
    /// agy's <c>ACTIVE</c> heartbeat and its terminal line for the SAME call were both counted — the
    /// two vendors' unit was not comparable under one `MaxToolSteps` scalar (spec/baton.md §3 has the
    /// F2 case). Measured against real captures (`dispatch-implement-38c24d11`, `dispatch-implement-f7b24a80`) —
    /// the per-room counts and the one-line gap on the cancelled room are spec/baton.md §3's own
    /// measurement, not restated here — so counting terminal-only halves the prior scalar without
    /// losing or double-counting a real call.
    /// </summary>
    public int CountToolSteps(string rawLine)
    {
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return 0;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawLine);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("event", out var eventProp) || eventProp.GetString() != "step_update"
                || !root.TryGetProperty("step_update", out var stepUpdate) || stepUpdate.ValueKind != JsonValueKind.Object
                || !stepUpdate.TryGetProperty("step_type", out var stepTypeProp) || stepTypeProp.GetString() != "tool"
                || !stepUpdate.TryGetProperty("tool_name", out var toolNameProp) || toolNameProp.GetString() is not { Length: > 0 }
                || !stepUpdate.TryGetProperty("state", out var stateProp))
            {
                return 0;
            }

            var state = stateProp.GetString();
            return state is "DONE" or "ERROR" ? 1 : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }
}
