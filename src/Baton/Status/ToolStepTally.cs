namespace Baton.Status;

/// <summary>
/// The four tool-step counts one execution's captured stream yields (#1921). All four present or the
/// whole record absent — <see cref="ToolStepTally.Snapshot"/> is the single decision point, and its
/// doc states the three states it decides between.
/// </summary>
/// <param name="ToolSteps">
/// Σ <see cref="IWorkerUsageParser.CountToolSteps"/> over the stream — the same unit
/// <c>MaxToolSteps</c> caps and <c>ArrestReason.ToolStepCap</c> arrests on, stated once in
/// spec/baton.md §3 and not restated here.
/// </param>
/// <param name="Refused">
/// Σ <see cref="IWorkerUsageParser.CountRefusedToolSteps"/> — results carrying
/// <see cref="Domain.GrantRefusal.Marker"/>.
/// </param>
/// <param name="Repeated">
/// <b>Occurrences beyond the first</b>, summed over distinct keys: a key seen three times contributes
/// 2. Not "how many keys repeated" (that reading would say 1), because the question this answers is how
/// many STEPS were spent re-asking something already answered, and each re-issue is one such step.
/// Zero when every key is distinct and zero when no key was derivable at all — the second case is
/// <see cref="IWorkerUsageParser.ToolInvocationKeys"/>'s documented gap, not a measurement of no repeats.
/// </param>
/// <param name="EmptyResults">Σ <see cref="IWorkerUsageParser.CountEmptyToolResults"/>.</param>
public readonly record struct ToolStepCounts(int ToolSteps, int Refused, int Repeated, int EmptyResults);

/// <summary>
/// Accumulates <see cref="ToolStepCounts"/> across one execution's captured stdout, one line at a time
/// (#1921) — the settle-time read behind the cost ledger's <c>toolSteps</c>/<c>refusedToolSteps</c>/
/// <c>repeatedToolSteps</c> and behind <c>baton audit lanes</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Shaped like <c>Mutation.TokenBudgetMonitor</c> and deliberately not folded into it.</b> That
/// monitor exists to ARREST a running execution and carries triggers, thresholds and sticky flags; this
/// one only ever reads, is replayed over an already-captured stream, and must never be able to stop
/// anything. Same reason the replay hands that monitor null budgets rather than reusing a live one.
/// </para>
/// <para>
/// <b>What a 0 refusal count means, and does not.</b> The count is a substring test for
/// <see cref="Domain.GrantRefusal.Marker"/>, which is stamped by the build that produced the stream. A
/// room captured before the marker landed carries its refusals in the old unmarked phrasings and reads
/// as <c>refusedToolSteps: 0</c> — a false zero, and the accepted cost of counting one marker instead of
/// a list of phrasings that no check could keep complete. It is bounded and it drains: it applies to
/// rooms already on disk and to no room captured after, and rooms are swept on retention.
/// </para>
/// <para>
/// <b>Memory is bounded by the distinct keys of one execution</b>, not by its line count — a stream that
/// re-issues one command a thousand times holds one key.
/// </para>
/// </remarks>
public sealed class ToolStepTally
{
    private readonly IWorkerUsageParser _parser;
    private readonly Dictionary<string, int> _keyOccurrences = new(StringComparer.Ordinal);
    private int _toolSteps;
    private int _refused;
    private int _emptyResults;

    public ToolStepTally(IWorkerUsageParser parser)
    {
        ArgumentNullException.ThrowIfNull(parser);
        _parser = parser;
    }

    /// <summary>Feeds one raw stdout line. A line matching nothing costs four cheap parses and changes nothing.</summary>
    public void OnStdoutLine(string rawLine)
    {
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return;
        }

        _toolSteps += _parser.CountToolSteps(rawLine);
        _refused += _parser.CountRefusedToolSteps(rawLine);
        _emptyResults += _parser.CountEmptyToolResults(rawLine);

        foreach (var key in _parser.ToolInvocationKeys(rawLine))
        {
            _keyOccurrences[key] = _keyOccurrences.TryGetValue(key, out var seen) ? seen + 1 : 1;
        }
    }

    /// <summary>
    /// The counts, or <see langword="null"/> when this stream carried no tool activity at all.
    /// <para>
    /// <b>Three states, decided here and nowhere else</b> — the shape
    /// <c>Accounting.CostLedgerStore.ResolveCompleteness</c> already establishes for the completeness
    /// field, for the same reason: a caller that null-coalesced each count independently could publish a
    /// row where two of the three were measured and the third silently read as a zero.
    /// <list type="bullet">
    /// <item><b>No captured stream at all</b> — this tally is never constructed, and the fields are
    /// absent on the row. Not this method's case.</item>
    /// <item><b>A stream with no tool activity</b> (a worker that only ever wrote prose, or a stream
    /// whose envelope no parser here understands) — <see langword="null"/>, i.e. ABSENT on the row.
    /// Reporting 0 here would state "this execution ran no tools", which an unreadable envelope is not
    /// evidence of.</item>
    /// <item><b>A stream with tool activity</b> — all four counts, INCLUDING zeros. A lane that ran 40
    /// tools and was refused none is the reading #1921 exists to make visible, and it is only visible if
    /// that 0 is written.</item>
    /// </list>
    /// "Tool activity" is any of the four signals, not <see cref="ToolStepCounts.ToolSteps"/> alone: on
    /// codex a call is announced on <c>item.started</c> and refused on <c>item.completed</c>, so a
    /// stream truncated between the two would otherwise drop a measured refusal on the floor.
    /// </para>
    /// </summary>
    public ToolStepCounts? Snapshot()
    {
        var repeated = 0;
        foreach (var occurrences in _keyOccurrences.Values)
        {
            repeated += occurrences - 1;
        }

        if (_toolSteps == 0 && _refused == 0 && _emptyResults == 0 && _keyOccurrences.Count == 0)
        {
            return null;
        }

        return new ToolStepCounts(_toolSteps, _refused, repeated, _emptyResults);
    }
}
