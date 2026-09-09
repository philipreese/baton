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
    /// turn's own usage, and both <c>TokensIn</c> and <c>TokensOut</c> on <see cref="WorkerUsage"/> are
    /// additive — a caller sums each across calls, the same as a real per-call bill would.
    /// <para>
    /// #2144 correction: an earlier revision of this doc claimed <c>TokensIn</c> was a LEVEL restating
    /// the whole context each turn, by analogy from claude's shape rather than a measurement — claude
    /// never actually populates it on an incremental reading
    /// (<c>ClaudeUsageParser.TryParseIncrementalUsage</c>'s own doc has why), so nothing ever exercised
    /// the claim. Measured on the two vendors that do populate it: agy's is additive
    /// (<c>docs/vendor-capabilities.md</c>, "agy's terminal <c>result.usage</c> IS the cumulative Σ of
    /// its per-turn lines" — measured on three real captures, 70/258/263 turns).
    /// <c>AgyTerminalUsageIsCumulativeTests</c> pins it against one of those (70 turns), where summing
    /// every turn's <c>TokensIn</c> reproduces the vendor's own terminal total to the token, while
    /// reading only the last turn's figure — the LEVEL reading this doc used to prescribe — undercounts
    /// by two orders of magnitude on that capture.
    /// <c>AgyArrestedRoomUsageReplaysAdditiveTests</c> pins a second real shape: the 157-turn capture
    /// from the <c>b982</c> room issue #2144 arrested, reproducing that room's own conductor-verified
    /// billed total (<c>sum(input_tokens) + sum(output_tokens) = 1,203,855</c>, computed directly
    /// against the raw <c>.stdout.log</c> independently of any parser in this repo). The sibling arrest,
    /// <c>cmpa2-1951-agy-r2</c> (190 turns, total 1,203,170), is corroborated the same way but has no
    /// fixture of its own — it is recorded in <c>benchmarks/comparator.md:110</c>. Both arrests are the
    /// rooms the additive claim explains, not part of the measured population
    /// <c>docs/vendor-capabilities.md</c> states (70/258/263 turns); citing them as additional captured
    /// test fixtures is the mistake this correction removes. codex's <c>TokensIn</c> is additive by
    /// construction (<c>CodexUsageParser</c> computes it as each round-trip's own non-cached
    /// remainder and its <c>Combine</c> sums it across round-trips deliberately). No shipped parser's
    /// <c>TokensIn</c> reading is ever a level; only <see cref="Baton.Mutation.TokenBudgetMonitor"/>'s own
    /// DERIVED context-size aggregate (<c>ContextLevelTokens</c>, folding <c>TokensIn</c> +
    /// <c>CacheReadTokens</c> + <c>CacheCreationTokens</c>) is — that is a display figure the monitor
    /// computes by replacing, not a property of this field. <c>CacheReadTokens</c> keeps the dual
    /// treatment <see cref="WorkerUsage.CacheReadTokens"/> documents: additive on its own running Σ,
    /// folded into that same derived level for display.
    /// </para>
    /// <see cref="Baton.Mutation.TokenBudgetMonitor"/> is the worked example. Default false/null: a
    /// parser that only supports the final-usage read (a test double, a future vendor) opts out cleanly
    /// rather than being forced to implement this.
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
    /// LAST non-null answer over the stream, which is how a shipping-class command that timed out and
    /// was then followed by a successful command stops being read as the cause of anything.
    /// </para>
    /// <para>
    /// Anchored inside the vendor's tool-result node, never a search of the raw line — the rule
    /// <see cref="Baton.Domain.GrantRefusal.Marker"/> states once. <b>The anchor is not enough on its
    /// own</b>, and the difference is why this is not the same reading as that one: a lane working in
    /// Baton's OWN repository runs a run-command that PRINTS the marker's defining file, which the tool
    /// anchor admits. So the item's own outcome is read as well — only a result the vendor reports as
    /// failed, whose text LEADS with the marker
    /// (<see cref="Baton.Domain.ShellCommandCeilings.IsShippingCeilingTimeout"/>), is a timed-out
    /// shipping-class command (<c>git push</c>, <c>git commit</c>, or <c>gh pr create</c> alike); a
    /// successful command quoting it, or a failed one whose own exit line comes first, is
    /// <see langword="false"/>.
    /// </para>
    /// Default null: a vendor Baton enforces no command ceiling on (claude and agy both run their shell
    /// inside the vendor CLI) reports nothing rather than a fabricated false.
    /// </summary>
    bool? ReportsShippingCeilingTimeout(string rawLine) => null;

    /// <summary>
    /// #1921: the canonical <c>tool + arguments</c> keys <paramref name="rawLine"/> reports, for
    /// <see cref="ToolStepTally"/>'s repeat count. One entry per tool call the line announces (claude's
    /// multi-tool turn reports several); <b>empty when the line does not carry the arguments</b>, which
    /// is a real gap rather than a shrug — keying on the tool name alone would report two different
    /// reads of two different files as one file read twice, so a line that cannot answer contributes
    /// nothing to the repeat count rather than a fabricated one.
    /// <para>
    /// claude and agy carry the arguments natively. Codex's own <c>mcp_tool_call</c> names the
    /// <c>tool</c> and nothing else, so on that vendor the key rests on a field Baton's own broker
    /// stamps (<see cref="CodexUsageParser.ArgumentsDigestField"/>) — present since #1963, absent on
    /// every codex stream captured before it, which is the population that still reads empty here.
    /// </para>
    /// <para>
    /// The key is opaque and comparison is ordinal: only equality is ever asked of it, never its shape.
    /// </para>
    /// Default empty.
    /// </summary>
    IReadOnlyList<string> ToolInvocationKeys(string rawLine) => [];

    /// <summary>
    /// #2002: the raw SHELL command lines <paramref name="rawLine"/> announces — the argument of
    /// claude's <c>Bash</c> and of agy's <c>run_command</c>, and nothing else. Feeds
    /// <c>Mutation.TokenBudgetMonitor</c>'s dominant-shape reading, which is rule 3 of spec/baton.md
    /// §9's run-command rules.
    /// <para>
    /// <b>Each parser names its own vendor's shell tool</b> (Adapter Isolation), rather than reading
    /// <c>Baton.Vendors.ShellCommandPatternMatcher.ShellToolNames</c> — which this assembly cannot
    /// reference anyway, the dependency running the other way. That constant is canonical for the
    /// grant/display seam it documents; this is the usage-parsing seam, and the two answer different
    /// questions about the same tool names.
    /// </para>
    /// <para>
    /// #2008: <b>codex answers here too</b>, and this seam is what "the three vendors are one query"
    /// means — there is no JSON key the three streams share (claude's is <c>input.command</c>, agy's is
    /// <c>tool_info.parameters.CommandLine</c>, and they already disagreed). codex's native
    /// <c>mcp_tool_call</c> carries no arguments at all, so its command line rests on a field Baton's
    /// own broker stamps (<see cref="CodexUsageParser.ArgumentsIdentityField"/>) and is empty on any
    /// codex stream captured before that landed — the same "cannot answer contributes nothing"
    /// <see cref="ToolInvocationKeys"/> states.
    /// </para>
    /// Default empty.
    /// </summary>
    IReadOnlyList<string> ShellCommandLines(string rawLine) => [];
}
