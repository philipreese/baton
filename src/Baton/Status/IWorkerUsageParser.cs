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

    /// <summary>
    /// #1921: how many tool RESULTS <paramref name="rawLine"/> reports that carry
    /// <see cref="Baton.Domain.GrantRefusal.Marker"/> — steps that bought the location of Baton's grant
    /// boundary and no information. <b>A different line from the one
    /// <see cref="CountToolSteps"/> counts on every vendor</b>: a step is counted where the CALL is
    /// announced and a refusal where the RESULT comes back, so the two are summed independently over
    /// the same stream rather than one being a filter over the other.
    /// <para>
    /// <b>It counts this build's marker, not refusals in general.</b> A stream captured before the
    /// marker landed carries the same refusals and reports 0 here — <see cref="ToolStepTally"/> states
    /// what that costs a historical reading and why the alternative (a list of phrasings) is the defect
    /// #1921 exists to remove.
    /// </para>
    /// Default 0, for the same reason <see cref="CountToolSteps"/>'s is.
    /// </summary>
    int CountRefusedToolSteps(string rawLine) => 0;

    /// <summary>
    /// #1921: how many tool RESULTS <paramref name="rawLine"/> reports whose payload is empty or
    /// whitespace — the other information-free step shape, and the one no grant refused: a search that
    /// matched nothing, a listing of an empty directory, a command that printed nothing. Reported by
    /// <c>baton audit lanes</c> beside the refusals and deliberately NOT on the cost-ledger row: an
    /// empty result is often the honest answer to a well-formed question, where a refusal never is
    /// (spec/baton.md §7 states that split once).
    /// <para>
    /// Never overlaps <see cref="CountRefusedToolSteps"/>: a refusal's payload is its reason, which is
    /// non-empty by construction.
    /// </para>
    /// Default 0.
    /// </summary>
    int CountEmptyToolResults(string rawLine) => 0;

    /// <summary>
    /// #1998: whether <paramref name="rawLine"/> reports a Baton RUN-COMMAND tool result that was killed
    /// at the <see cref="Baton.Domain.ShellCommandClass.Shipping"/> ceiling — the shape that leaves a
    /// finished lane with nothing on origin.
    /// <para>
    /// <b>Tri-state, and the third state is what makes it read the FINAL run-command rather than any of
    /// them.</b> <see langword="true"/>: this line is a completed run-command result carrying
    /// <see cref="Baton.Domain.ShellCommandCeilings.ShippingCeilingMarker"/>.
    /// <see langword="false"/>: a completed run-command result that is not one. <see langword="null"/>:
    /// this line reports no run-command result at all, so it says nothing either way. A reader keeps the
    /// LAST non-null answer over the stream, which is how a push that timed out and was then followed by
    /// a successful command stops being read as the cause of anything.
    /// </para>
    /// <para>
    /// Anchored inside the vendor's tool-result node, never a search of the raw line — the rule
    /// <see cref="Baton.Domain.GrantRefusal.Marker"/> states once. <b>The anchor is not enough on its
    /// own</b>, and the difference is why this is not the same reading as that one: a lane working in
    /// Baton's OWN repository runs a run-command that PRINTS the marker's defining file, which the tool
    /// anchor admits. So the item's own outcome is read as well — only a result the vendor reports as
    /// failed, whose text LEADS with the marker
    /// (<see cref="Baton.Domain.ShellCommandCeilings.IsShippingCeilingTimeout"/>), is a timed-out push;
    /// a successful command quoting it, or a failed one whose own exit line comes first, is
    /// <see langword="false"/>.
    /// </para>
    /// Default null: a vendor Baton enforces no command ceiling on (claude and agy both run their shell
    /// inside the vendor CLI) reports nothing rather than a fabricated false.
    /// </summary>
    bool? ReportsShippingCeilingTimeout(string rawLine) => null;

    /// <summary>
    /// #1921: the canonical <c>tool + arguments</c> keys <paramref name="rawLine"/> reports, for
    /// <see cref="ToolStepTally"/>'s repeat count. One entry per tool call the line announces (claude's
    /// multi-tool turn reports several); <b>empty when the vendor's stream does not carry the
    /// arguments</b>, which is a real gap rather than a shrug — codex names the <c>tool</c> of an
    /// <c>mcp_tool_call</c> and never its arguments, so keying those on the name alone would report two
    /// different reads of two different files as one file read twice. A vendor that cannot answer
    /// contributes nothing to the repeat count rather than a fabricated one.
    /// <para>
    /// The key is opaque and comparison is ordinal: only equality is ever asked of it, never its shape.
    /// </para>
    /// Default empty.
    /// </summary>
    IReadOnlyList<string> ToolInvocationKeys(string rawLine) => [];
}
