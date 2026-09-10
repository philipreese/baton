using System.Text.Json;
using Baton.Domain;

namespace Baton.Status;

/// <summary>
/// Parses the usage on Codex CLI JSONL events — one <c>turn.usage</c> per model round-trip (#2020)
/// and the terminal <c>turn.completed</c> (#1853). Codex reports
/// <c>input_tokens</c> inclusive of <c>cached_input_tokens</c>; Baton's additive shape keeps those
/// dimensions disjoint (as agy's input already is), so <see cref="WorkerUsage.TokensIn"/> is the non-cached remainder.
/// <para>
/// <b>No <see cref="IWorkerUsageParser.TryParseEchoedModel"/> override, because codex has no reachable
/// source for one</b> (#1927 review HIGH). The absence is a DIFFERENT kind from agy's beside it: agy's
/// vendor stream was measured to carry no <c>model</c> key, whereas codex never reaches Baton as a
/// vendor stream at all. Both lifecycle events on this vendor's stdout are synthesized by
/// <c>Baton.Vendors.CodexAppServerBroker</c> — <c>thread.started</c> carries a thread id and nothing
/// else, <c>turn.usage</c> and <c>turn.completed</c> a usage object and nothing else — so a parser
/// reading any of them would be reading Baton's own keys back. The emitter is in-tree, which makes this deterministic rather
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
    /// <summary>
    /// #2020: the per-model-round-trip usage line <c>Baton.Vendors.CodexAppServerBroker</c> writes on
    /// every <c>thread/tokenUsage/updated</c> notification. Named here rather than in the broker
    /// because this class is the only reader of it and the broker its only writer — the two would
    /// otherwise hold two copies of one string.
    /// </summary>
    public const string TurnUsageEventType = "turn.usage";

    /// <summary>
    /// #2020: the 1-based model round-trip a usage object reports, written by the same broker inside
    /// the <c>usage</c> object on both line types. Absent on every stream captured before that
    /// emitter, which is exactly how a reader tells the two eras apart without a version stamp.
    /// <para>
    /// <b>Why an index that restarts at 1 is safe to dedupe on, and the one shape where it is not</b>
    /// (#2020 review LOW). It is safe because a capture file normally holds ONE broker invocation, so
    /// one monotone sequence rather than two concatenated ones. Not for the reason it is tempting to
    /// give: <c>Dispatch.ExecutionStreamLogger</c> does not truncate — both creates are guarded by
    /// <c>File.Exists</c> and every later write is <c>FileMode.Append</c>, and its own <c>#1724</c>
    /// remark treats a second logger over one directory as a contemplated shape. What holds instead is
    /// the directory: an ordinary dispatch, a retry and a resume each mint a fresh
    /// <see cref="Domain.ExecutionId"/> (<c>new ExecutionId(Guid.NewGuid()…)</c>) and pass it to
    /// <see cref="Artifacts.ArtifactManager.AllocateOutputDirectory"/>, which addresses
    /// <c>execution_{id}</c> by that id — so each gets its own capture file.
    /// </para>
    /// <para>
    /// The exception, stated because it is invisible otherwise: <c>Mutation.MutationInterface</c>'s
    /// M10 Phase 3 crash-recovery RESUBMIT re-dispatches an already-accepted request under its
    /// EXISTING id ("the same attempt, not a retry", at that loop's own comment), so a resubmitted
    /// execution's second broker appends into the first's <c>.stdout.log</c> and the index restarts at
    /// 1 inside one file. Consequences, none of them new damage but none of them free: the fold above
    /// is unaffected (it sums every <c>turn.usage</c> line and both attempts really were billed);
    /// <c>Mutation.TokenBudgetMonitor</c>'s replay over such a concatenated file drops the resubmitted
    /// round-trips whose index collides with the crashed attempt's, so its live figure reads LOW rather
    /// than high — the fail-safe direction for an arrest, and the same direction the pre-#2020 code
    /// erred in; and <c>Vendors.CodexWorkerAdapter.IsPostResponseTerminalLine</c>'s backward scan can
    /// only reach the crashed attempt's final response if the resubmit produced no agent message of its
    /// own, which is a captured answer where there would otherwise have been none. Closing the
    /// collision needs an attempt discriminator the resubmit path does not currently write; nothing
    /// here depends on it being closed.
    /// </para>
    /// </summary>
    public const string RoundTripField = "round_trip";

    private const string TerminalEventType = "turn.completed";

    /// <summary>
    /// Sums one execution's usage over its complete captured stream (#2020), current and rolled
    /// segments together. State belongs to this read, never the shared parser instance; absent
    /// dimensions stay absent.
    /// <para>
    /// The <see cref="TurnUsageEventType"/> lines are the population, one per model round-trip. A
    /// stream captured BEFORE that emitter landed carries none of them and its whole usage report is
    /// the terminal <c>turn.completed</c>, so it falls back to folding those — which recovers exactly
    /// what such a stream ever knew (the final round-trip) rather than regressing it to absent. The
    /// two populations are never mixed: a current stream's terminal line restates a round-trip the
    /// <c>turn.usage</c> lines already carry, and folding both would double-count it.
    /// </para>
    /// <para>
    /// <b>Keyed on line TYPE, where <see cref="TryParseIncrementalUsage"/>'s reader keys on
    /// <see cref="RoundTripField"/>.</b> Two mechanisms for one no-double-count claim, deliberately:
    /// this fold sees the whole stream at once and can partition it, while the live monitor sees one
    /// line at a time and can only remember what it has already counted. They agree on every stream
    /// either can meet — <c>Baton.Vendors.Tests.CodexBrokerRoomUsageTests</c> asserts the agreement
    /// directly, as <c>BilledTokens == LiveBilledTokens</c> over the broker's own emitted bytes.
    /// </para></summary>
    public WorkerUsage? ParseExecutionUsage(IEnumerable<string> lines)
    {
        WorkerUsage? perRoundTrip = null;
        WorkerUsage? terminal = null;
        foreach (var line in lines)
        {
            if (TryParse(line, TurnUsageEventType, out var roundTrip) && roundTrip is not null)
            {
                perRoundTrip = Combine(perRoundTrip, roundTrip);
            }
            else if (TryParse(line, TerminalEventType, out var completed) && completed is not null)
            {
                terminal = Combine(terminal, completed);
            }
        }

        // #2020: the round-trip index is dropped on the way out. Combine carries `left`'s fields, so a
        // three-round-trip fold would otherwise claim round-trip 1's identity for a total that is not
        // any one round-trip. Nothing reads WorkerUsage.MessageId off an execution total today; this
        // keeps it that way rather than leaving a wrong value there for something to start reading.
        return (perRoundTrip ?? terminal) is { } folded ? folded with { MessageId = null } : null;
    }

    /// <summary>
    /// #2020 review LOW: one fold step, built with <c>with</c> so a dimension added to
    /// <see cref="WorkerUsage"/> is carried rather than silently dropped. The positional constructor
    /// this replaced named six of thirteen fields, so a two-or-more-turn read dropped the other seven
    /// while a one-turn read (which returns its single reading untouched) preserved them — a
    /// difference no caller could see coming. Only the summable dimensions are folded; every other
    /// field keeps <paramref name="left"/>'s value, which is the first reading's.
    /// </summary>
    internal static WorkerUsage Combine(WorkerUsage? left, WorkerUsage right) =>
        left is null ? right : left with
        {
            TokensIn = Sum(left.TokensIn, right.TokensIn),
            TokensOut = Sum(left.TokensOut, right.TokensOut),
            Turns = left.Turns + right.Turns,
            CacheReadTokens = Sum(left.CacheReadTokens, right.CacheReadTokens),
            CacheCreationTokens = Sum(left.CacheCreationTokens, right.CacheCreationTokens),
            ThinkingTokens = Sum(left.ThinkingTokens, right.ThinkingTokens),
        };

    private static long? Sum(long? left, long? right) =>
        left is null && right is null ? null : (left ?? 0) + (right ?? 0);

    public bool TryParseFinalUsage(string rawLine, out WorkerUsage? usage) =>
        TryParse(rawLine, TerminalEventType, out usage);

    /// <summary>
    /// #2020: BOTH line types, deduplicated by <see cref="RoundTripField"/> rather than by type. The
    /// live monitor sums the output side across matching lines, and on a current stream the terminal
    /// <c>turn.completed</c> restates a round-trip its own <c>turn.usage</c> line already reported —
    /// so the restatement carries the same index and <c>Mutation.TokenBudgetMonitor</c>'s existing
    /// repeated-<see cref="WorkerUsage.MessageId"/> rule drops it. Rejecting <c>turn.completed</c>
    /// outright would be the simpler guard and is wrong: a stream captured before that emitter has
    /// the terminal line as its ONLY usage line, and a running codex room reading it is what
    /// <c>rooms[].live</c> is built from (#1886).
    /// </summary>
    public bool TryParseIncrementalUsage(string rawLine, out WorkerUsage? usage) =>
        TryParse(rawLine, TurnUsageEventType, out usage)
        || TryParse(rawLine, TerminalEventType, out usage);

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
    /// The <c>mcp_tool_call</c> item field <c>Baton.Vendors.CodexAppServerBroker</c> writes (on both
    /// <c>item.started</c> and <c>item.completed</c> since #2008) and <see cref="ToolInvocationKeys"/> reads — named once here because those two are in different
    /// projects (<c>Baton.Vendors</c> → <c>Baton</c>, never the reverse), so this is the only symbol
    /// both can see. A rename that reached one and not the other would silently stop the repeat count.
    /// </summary>
    public const string ArgumentsDigestField = "argumentsDigest";

    /// <summary>
    /// #2008: the sibling field carrying the call's INPUT IDENTITY, as the worker wrote it (selected
    /// and length-capped, not normalised: whitespace and path spelling are the worker's own) — the command line of a
    /// <see cref="RunCommandToolName"/> call, the opaque reference of a retained-command-output read,
    /// the path of a read/list/search/write, the declared output name of a write-output, and the
    /// patched paths of an <c>apply_patch</c>.
    /// <c>Baton.Vendors.CodexDynamicToolPolicy.InputIdentity</c> is the one place that mapping lives and
    /// states what it excludes (file contents, patch bodies) and its length cap; named here for the same
    /// cross-project reason as <see cref="ArgumentsDigestField"/> above.
    /// <para>
    /// <b>Why a second field when the digest already identifies a call.</b> The digest answers only
    /// "are these two calls the same one", which is all a repeat count needs; it cannot say WHAT was
    /// run. claude's and agy's streams carry the arguments themselves
    /// (<c>ClaudeUsageParser.ShellCommandLines</c> reads <c>input.command</c>,
    /// <c>AgyUsageParser.ShellCommandLines</c> reads <c>tool_info.parameters.CommandLine</c>), so those
    /// two vendors could always answer it and codex could not — the gap #2008 measured. There is no
    /// shared JSON key across the three (the two that had one already disagree); what is shared is the
    /// <see cref="IWorkerUsageParser"/> seam, and this field is what lets codex answer at it.
    /// </para>
    /// <para>
    /// <b>Absent, never blank</b>, on a tool with no identifying argument — see
    /// <c>Baton.Vendors.CodexAppServerBroker.Describe</c> — so a reader distinguishes "this shape names
    /// no target" from "the target was empty".
    /// </para>
    /// </summary>
    public const string ArgumentsIdentityField = "argumentsIdentity";

    /// <summary>
    /// #2008: codex's shell tool is <see cref="RunCommandToolName"/> and its command line arrives on
    /// the <see cref="ArgumentsIdentityField"/> Baton's own broker stamps — codex's native
    /// <c>item.started</c> names the tool and nothing else, so unlike the other two vendors there is no
    /// arguments node to read and this is the whole of what makes the reading possible here.
    /// <para>
    /// <b>Anchored on <c>item.started</c>, the same single lifecycle line
    /// <see cref="ToolInvocationKeys"/> uses</b>, even though the identity is now stamped on the
    /// completed item too: both items of one call carry it, and reading both would report every codex
    /// command twice into <c>Mutation.TokenBudgetMonitor</c>'s dominant-shape denominator.
    /// </para>
    /// <para>
    /// Empty for a stream captured before this landed, exactly as <see cref="ToolInvocationKeys"/> is —
    /// the field is what the reading rests on, and a vendor that cannot answer contributes nothing
    /// rather than a fabricated shape.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> ShellCommandLines(string rawLine)
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
                || ReadString(item, "tool") != RunCommandToolName
                || ReadString(item, ArgumentsIdentityField) is not { Length: > 0 } commandLine)
            {
                return [];
            }

            return [commandLine];
        }
        catch (JsonException)
        {
            return [];
        }
    }

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

    private static bool TryParse(string rawLine, string expectedType, out WorkerUsage? usage)
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
                || type.GetString() != expectedType
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
                ThinkingTokens: reasoning,
                // #2020: the dedup key TryParseIncrementalUsage's remark explains, carried on the
                // field TokenBudgetMonitor already keys its repeated-reading rule on. Null when the
                // stream predates RoundTripField, which is what makes such a stream's single terminal
                // line accumulate as it always did.
                MessageId: ReadLong(reported, RoundTripField) is { } index
                    ? $"round-trip-{index}"
                    : null);
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
