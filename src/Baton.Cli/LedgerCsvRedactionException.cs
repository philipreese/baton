namespace Baton.Cli;

/// <summary>
/// Raised by <see cref="LedgerCsv.Write(TextWriter, IReadOnlyList{Accounting.CostLedgerEntry})"/> when a
/// cell survives redaction still carrying something that must not be published (#1901 C3, operator
/// ruling 2026-09-05: "the operator's username must not appear in any cell").
/// </summary>
/// <remarks>
/// Why refusing beats scrubbing is spec/baton.md §7's export paragraph. What that leaves for this
/// type: a refusal has to be legible at the SOURCE ROW, since the operator's only fix is upstream of
/// the export, so the message names the offending column and row position and points at the
/// unredacted format for a local reading.
/// </remarks>
public sealed class LedgerCsvRedactionException : BatonFlowException
{
    public string Column { get; }
    public int RowIndex { get; }

    public LedgerCsvRedactionException(string column, int rowIndex, string reason)
        : base(
            $"Cost-ledger CSV row {rowIndex} column '{column}' cannot be published: {reason}. "
            + "The CSV format is the one that leaves this machine (`baton ledger export` commits it to a "
            + "public repository), so a cell that still carries a filesystem path or this machine's OS "
            + "account name refuses the export rather than being written. Room and parentRoom are already "
            + "reduced to their basename; a path anywhere else came in through a free-text field "
            + "(completenessReason, estimateReason, runwayOverrideReason, resolutionReason) or through "
            + "`raw`. Fix the offending ledger row, or read this file with `--format json`, which is "
            + "local-only and not redacted.")
    {
        Column = column;
        RowIndex = rowIndex;
    }
}
