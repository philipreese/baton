using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
/// <para>
/// <b>The narrowing #1901 C3 adds lives here</b> (operator ruling 2026-09-05) — spec/baton.md §7's
/// export paragraph states the rule, which readings it applies to, and why it refuses rather than
/// scrubs. What belongs here rather than there: the redaction runs at the WRITE, after
/// <see cref="Baton.Accounting.LedgerRollup"/> has selected and sorted, because editing cells before
/// that projection sorts would be editing the inputs to the sort keys its own determinism promise
/// names.
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
        "resolution", "resolutionReason", "label", "commits", "reviewCount", "identitySource",
    ];

    /// <summary>
    /// <b>The one source of truth for "which schema was this file written under".</b> Derived from
    /// <see cref="Columns"/> itself — the count and a truncated SHA-256 of the joined header — rather
    /// than a hand-maintained version literal, because a hand-maintained one is a second place the
    /// column set is stated and would sit stale the first time a field is added to
    /// <see cref="CostLedgerEntry"/> without someone remembering to bump it. What
    /// <c>benchmarks/ledger/README.md</c> records per export, and what a reader compares two exports
    /// by: equal versions mean byte-comparable headers, and nothing else needs checking.
    /// </summary>
    public static string SchemaVersion { get; } = ComputeSchemaVersion();

    /// <summary>
    /// The columns that carry a filesystem path, reduced to a basename by <see cref="Write"/>. A room
    /// path is <c>{BatonPaths.Root}/rooms/&lt;room&gt;</c>, so the basename is the whole of the room's
    /// identity and the discarded prefix is the operator's home directory — the reduction is lossless
    /// for every reading built on this format.
    /// </summary>
    public static IReadOnlyList<string> PathColumns { get; } = ["room", "parentRoom"];

    /// <summary>
    /// The two shapes an account name actually arrives inside: a drive-qualified path (<c>C:\</c>,
    /// <c>C:/</c>) or a <c>Users</c>/<c>home</c> segment. A SHAPE rather than a second list of
    /// columns, because the columns that can carry one are not enumerable — the C3 ruling's own list
    /// named two fields this schema does not have, and the fields it does have include six free-text
    /// reasons and <c>raw</c>, whose contents are a vendor's verbatim session object
    /// (spec/baton.md §7, phase C).
    /// </summary>
    private static readonly Regex PathShapedCell =
        new(@"[A-Za-z]:[\\/]|[\\/](?:Users|home)[\\/]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static void Write(TextWriter output, IReadOnlyList<CostLedgerEntry> rows) =>
        Write(output, rows, Environment.UserName);

    /// <param name="accountName">
    /// The OS account name no cell may contain. Production callers pass
    /// <see cref="Environment.UserName"/>; a test injects one so the refusal arm can be exercised
    /// without depending on whose machine the suite runs on.
    /// </param>
    internal static void Write(TextWriter output, IReadOnlyList<CostLedgerEntry> rows, string? accountName)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(rows);

        output.Write(string.Join(',', Columns));
        output.Write('\n');

        var rowIndex = 0;
        foreach (var row in rows)
        {
            var node = JsonSerializer.SerializeToNode(row)?.AsObject();
            var cells = Columns.Select(column => Cell(node, column)).ToArray();

            // FAIL CLOSED, and before a single row is buffered anywhere a caller could still write it:
            // this file's destination is a public repository, and a leak there is not undoable by a
            // later commit. Refusing an export is recoverable; publishing an account name is not.
            for (var i = 0; i < cells.Length; i++)
            {
                if (Leak(cells[i], accountName) is { } leak)
                {
                    throw new LedgerCsvRedactionException(Columns[i], rowIndex, leak);
                }
            }

            output.Write(string.Join(',', cells));
            output.Write('\n');
            rowIndex++;
        }
    }

    /// <summary>Why <paramref name="cell"/> may not be published, or <see langword="null"/> when it may.</summary>
    private static string? Leak(string cell, string? accountName)
    {
        if (cell.Length == 0)
        {
            return null;
        }

        if (PathShapedCell.IsMatch(cell))
        {
            return "it still looks like a filesystem path";
        }

        // Length-gated: a two-character account name ('pi', 'ec2-user' is fine, 'pb' is not) matches
        // inside ordinary content -- a model id, an execution hash -- and a scan that fires on every
        // export is a scan that gets switched off rather than read.
        return accountName is { Length: >= 3 } name
            && cell.Contains(name, StringComparison.OrdinalIgnoreCase)
                ? $"it contains this machine's OS account name ('{name}')"
                : null;
    }

    private static string ComputeSchemaVersion()
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(',', Columns)));
        return $"{Columns.Count}-{Convert.ToHexStringLower(digest.AsSpan(0, 6))}";
    }

    /// <summary>
    /// The last segment of <paramref name="value"/>, splitting on BOTH separators regardless of host.
    /// <see cref="Path.GetFileName(string)"/> is host-dependent — on Linux a backslash is an ordinary
    /// character, so a Windows-written room path would come through whole — and the ledger is read on
    /// whatever machine an analysis runs on, not only the one that wrote it.
    /// </summary>
    private static string Basename(string value)
    {
        var trimmed = value.TrimEnd('\\', '/');
        var cut = trimmed.LastIndexOfAny(['\\', '/']);
        return cut < 0 ? trimmed : trimmed[(cut + 1)..];
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

        if (PathColumns.Contains(column))
        {
            text = Basename(text);
        }

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
