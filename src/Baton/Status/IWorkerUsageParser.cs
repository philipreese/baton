using Baton.Domain;

namespace Baton.Status;

/// <summary>
/// Parser interface for extracting terminal worker usage from captured stdout.
/// </summary>
public interface IWorkerUsageParser
{
    /// <summary>
    /// Attempts to interpret one raw stdout line — the last non-blank line of a completed execution's
    /// captured stream — as this vendor's terminal usage report (issue #1360).
    /// </summary>
    bool TryParseFinalUsage(string rawLine, out WorkerUsage? usage)
    {
        usage = null;
        return false;
    }

    /// <summary>
    /// #1623: attempts to read usage from a live, not-yet-terminal stdout line (e.g. claude's mid-stream
    /// <c>"type":"assistant"</c> <c>message.usage</c>, agy's <c>"step_update"</c> DONE-state
    /// <c>usage</c>) — for a running token budget evaluated as usage arrives, never a replacement for
    /// <see cref="TryParseFinalUsage"/>'s own terminal-line read. Each matching line reports that one
    /// turn's own usage, but the two output fields on <see cref="WorkerUsage"/> are NOT symmetric: the
    /// output side (<c>TokensOut</c>) is additive — a caller sums across calls. The input side
    /// (<c>TokensIn</c> + <c>CacheReadTokens</c> + <c>CacheCreationTokens</c>) is a LEVEL — a vendor's
    /// own <c>input_tokens</c> for a turn already restates the whole context sent that turn, so a
    /// caller replaces its running input total with each new reading rather than adding to it; summing
    /// it the way output is summed double-counts a long conversation's context on every turn.
    /// <see cref="Baton.Mutation.TokenBudgetMonitor"/> is the worked example of both halves together.
    /// Default false/null: a parser that only supports the final-usage read (a test double, a future
    /// vendor) opts out cleanly rather than being forced to implement this.
    /// </summary>
    bool TryParseIncrementalUsage(string rawLine, out WorkerUsage? usage)
    {
        usage = null;
        return false;
    }

    /// <summary>
    /// #1623: the tool name a live stdout line names, if any (e.g. agy's <c>step_update.tool_name</c>).
    /// Independent of <see cref="TryParseIncrementalUsage"/> — a line can report one, both, or neither.
    /// Default null.
    /// </summary>
    string? TryParseToolName(string rawLine) => null;

    /// <summary>
    /// #1682: how many tool-step events <paramref name="rawLine"/> itself reports — the quantity
    /// <c>Mutation.TokenBudgetMonitor</c>'s tool-step cap accumulates, independently of whether
    /// <see cref="TryParseIncrementalUsage"/> matches anything on the same line (the cap must still
    /// fire on a stream with malformed or entirely absent usage lines). Deliberately NOT
    /// <see cref="TryParseToolName"/> reused as a 0/1 count: that method exists to report ONE display
    /// name per line and, for claude, returns only the first <c>tool_use</c> block of a multi-tool
    /// turn — undercounting exactly the shape this cap exists to catch. A caller sums this across every
    /// line of the stream; each vendor's own doc comment on its implementation states what one line
    /// counts as. Default 0: a parser that reports no tool-step signal (a test double, a future vendor)
    /// opts out cleanly rather than being forced to implement this.
    /// </summary>
    int CountToolSteps(string rawLine) => 0;

    /// <summary>
    /// #1927: the model name <paramref name="rawLine"/> reports the vendor CLI as having actually RUN,
    /// or null when this line reports none. Read at settle over the whole captured stream
    /// (<c>ExecutionUsageProjector</c>), which keeps the LAST non-null answer, and landing on
    /// <c>Accounting.CostLedgerEntry.ModelEchoed</c>.
    /// <para>
    /// <b>Not every line naming a model qualifies.</b> The value has to be the vendor's own resolution,
    /// not its restatement of what Baton asked for: claude's <c>system:init</c> echoes the
    /// <c>--model</c> string verbatim even when that string is invalid (measured,
    /// <c>docs/vendor-doc-audit.md</c> §5), so an implementation that read it would report a model that
    /// never ran — the exact substitution/downgrade this field exists to expose. Each vendor's own
    /// implementation states which event it reads and why.
    /// </para>
    /// <para>
    /// Default null: a vendor that echoes nothing (agy — measured, no <c>model</c> key anywhere in its
    /// stream, #1927) opts out cleanly, and its ledger row carries the field ABSENT rather than blank.
    /// </para>
    /// </summary>
    string? TryParseEchoedModel(string rawLine) => null;
}
