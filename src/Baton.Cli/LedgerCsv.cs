using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Baton.Accounting;

namespace Baton.Cli;

/// <summary>
/// <c>baton ledger --format csv</c>: the contributing rows, and nothing else — no subtotals, because a
/// spreadsheet's own SUM over these rows must be the same number the text and JSON views print, and a
/// pre-summed row mixed in would double it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The header is the ledger record's own field names</b> — <see cref="Columns"/> is the only
/// hand-written list in this file, and every cell is read out of the row's own serialized JSON by that
/// name rather than through a second list of accessors. A field renamed on
/// <see cref="CostLedgerEntry"/> therefore cannot quietly keep its old column with an empty value,
/// and <c>LedgerViewCommandTests.Csv_columns_are_exactly_the_ledger_records_own_field_names</c>
/// discovers that population by reflection so a phase-C field cannot go missing here unnoticed.
/// </para>
/// <para>
/// <b>LF, always</b> — written explicitly rather than through <see cref="TextWriter.WriteLine()"/>,
/// whose line ending is the host's. This repo builds and runs on Windows (spec/baton.md C-10), so
/// relying on the default would emit CRLF here and break the byte-identical-output promise the moment
/// a reading was compared across machines.
/// </para>
/// <para>
/// An absent field is an EMPTY cell, never <c>0</c> and never the string "null" — the ledger's
/// absence-is-not-zero doctrine surviving the export, since a CSV reader that sums a column must not
/// see a zero the record never wrote.
/// </para>
/// </remarks>
public static class LedgerCsv
{
    /// <summary>The <see cref="CostLedgerEntry"/> JSON field names, in the record's own declaration order.</summary>
    public static IReadOnlyList<string> Columns { get; } =
    [
        "sourceKind", "repository", "room", "parentRoom", "workstream", "workflow", "step", "execution",
        "attempt", "role", "adapter", "model", "modelEchoed", "modelsObserved", "effort", "outcome",
        "issue", "pr", "startedAt", "endedAt", "tokensIn", "tokensOut", "cacheRead", "cacheCreation",
        "thinking", "turns", "wallClockMs", "verifyStepMs", "verifyResultsBytes", "billedTokens", "liveBilledTokens", "billedUnderReadTokens",
        "peakBilledInWindow", "raw", "completeness", "completenessReason", "apiEquivalentUsd",
        "estimateStatus", "planMeterEstimateUsd", "planMeterEstimateStatus", "estimateReason",
        "priceCatalogId", "priceCatalogVersion", "planFactorTableId", "planFactorTableVersion",
        "runwayOverrideReason", "filesChanged", "additions", "deletions", "testFilesChanged",
        "reviewedRef", "reviewedPr", "reviewedHead", "findingsHigh", "findingsMedium", "findingsLow",
        "resolution", "resolutionReason",
    ];

    public static void Write(TextWriter output, IReadOnlyList<CostLedgerEntry> rows)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(rows);

        output.Write(string.Join(',', Columns));
        output.Write('\n');

        foreach (var row in rows)
        {
            var node = JsonSerializer.SerializeToNode(row)?.AsObject();
            output.Write(string.Join(',', Columns.Select(column => Cell(node, column))));
            output.Write('\n');
        }
    }

    /// <summary>
    /// One cell, RFC 4180-escaped. A nested value (<c>modelsObserved</c>, <c>raw</c>) keeps its JSON
    /// spelling inside the quoted cell rather than being flattened into a private syntax a reader would
    /// have to guess at.
    /// </summary>
    private static string Cell(JsonObject? node, string column)
    {
        if (node is null || !node.TryGetPropertyValue(column, out var value) || value is null)
        {
            return string.Empty;
        }

        var text = value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var asString)
            ? asString
            : value.ToJsonString();

        return Escape(text);
    }

    private static string Escape(string value)
    {
        if (value.IndexOfAny([',', '"', '\n', '\r']) < 0)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length + 2).Append('"');
        foreach (var c in value)
        {
            builder.Append(c);
            if (c == '"')
            {
                builder.Append('"');
            }
        }

        return builder.Append('"').ToString();
    }
}
