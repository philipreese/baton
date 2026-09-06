namespace Baton.Cli;

/// <summary>
/// Raised by <see cref="LedgerCsv.Write(TextWriter, IReadOnlyList{Accounting.CostLedgerEntry})"/> when a
/// cell survives redaction still carrying something that must not be published (#1901 C3, operator
/// ruling 2026-09-05: "the operator's username must not appear in any cell").
/// </summary>
/// <remarks>
/// The CSV is what <c>baton ledger export</c> commits to a PUBLIC repository, and a leaked path or
/// account name is not undone by a later commit — the pushed object survives the deletion. So this
/// refuses the whole write rather than redacting a cell it does not understand: <see cref="LedgerCsv"/>
/// knows how to reduce a path COLUMN to a basename, and knows nothing about what an operator typed
/// into <c>resolutionReason</c> or what a vendor session log put in <c>raw</c>. Guessing at those would
/// be a redaction that silently corrupts the number an analysis reads; refusing is a failure the
/// operator can see and fix at the source row.
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
