using System.Globalization;
using System.Text;
using Baton.Accounting;

namespace Baton.Cli;

/// <summary>
/// <c>baton ledger export --to &lt;dir&gt;</c> (#1901 C3, operator ruling 2026-09-05): writes the whole
/// repository-keyed cost ledger to <c>&lt;dir&gt;/&lt;yyyy-MM-dd&gt;.csv</c> and records that file in
/// <c>&lt;dir&gt;/README.md</c>'s table, so an efficiency analysis is reproducible from a repository
/// rather than from one machine's <c>~/.baton</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The bytes are <see cref="LedgerViewCommand"/>'s own.</b> This runs the same
/// <see cref="LedgerRollup.Build"/> with an empty query and hands the result to the same
/// <see cref="LedgerCsv.Write(TextWriter, IReadOnlyList{CostLedgerEntry})"/> — there is no second
/// writer here, so an export cannot disagree with <c>baton ledger --format csv</c>, and the redaction
/// and the column guard that writer carries apply by construction rather than by remembering to.
/// </para>
/// <para>
/// <b>Read-only over the store.</b> The only writes are into <c>--to</c>. This verb is not a sibling of
/// <c>baton ledger backfill</c>, which writes rows; the two are deliberately separate commands so the
/// weekly cadence spec/baton.md §7 states cannot accidentally mutate the thing it is publishing.
/// </para>
/// <para>
/// <b>Only the ledger.</b> Nothing else under <c>~/.baton</c> — rooms, streams, transcripts, memory —
/// is opened by this command. That is the C3 ruling's "not in scope" clause, made structural: the one
/// path it reads is the ledger file <see cref="LedgerViewCommand.ResolveLedgerFilePathAsync"/> returns.
/// </para>
/// <para>
/// Not a <see cref="CommandResult"/>/<see cref="FlowStateReporter"/> command, for the same reason the
/// other three <c>ledger</c> forms are not: there is no workflow pump here to report on.
/// </para>
/// </remarks>
public static class LedgerExportCommand
{
    /// <summary>
    /// The fences the generated table lives between in <c>README.md</c>. A marked REGION rather than
    /// the whole file, because the prose around it — what the columns mean, how the redaction works,
    /// what a reader must not conclude from a median — is hand-written and must survive every export.
    /// A README that exists but carries no markers is refused rather than appended to: silently
    /// growing a second table is how a reader ends up reading the stale one.
    /// </summary>
    public const string TableBeginMarker = "<!-- baton ledger export: table begins -->";

    /// <inheritdoc cref="TableBeginMarker"/>
    public const string TableEndMarker = "<!-- baton ledger export: table ends -->";

    private const string TableHeader =
        "| Export | Schema version | Rows | Newest row (endedAt, UTC) |\n|---|---|---|---|";

    public static async Task<int> ExecuteAsync(
        LedgerExportOptions options,
        TextWriter output,
        string? ledgerFilePathOverride = null,
        DateTime? todayOverride = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);

        if (options.Help)
        {
            Write(output, LedgerExportOptionsParser.Usage);
            foreach (var line in LedgerExportOptionsParser.HelpLines)
            {
                Write(output, line);
            }

            return 0;
        }

        var targetDirectoryPath = Path.GetFullPath(options.TargetDirectoryPath!);
        var ledgerFilePath = ledgerFilePathOverride
            ?? await LedgerViewCommand
                .ResolveLedgerFilePathAsync(options.RepositoryIdentityKey, null, cancellationToken)
                .ConfigureAwait(false);

        var entries = await CostLedgerStore.ReadAllAsync(ledgerFilePath, cancellationToken).ConfigureAwait(false);
        var rows = LedgerRollup.Build(entries, new LedgerQuery(), includeRows: true).Rows ?? [];

        var asOf = options.AsOf ?? (todayOverride ?? DateTime.Now).Date;
        var fileName = FormattableString.Invariant($"{asOf:yyyy-MM-dd}.csv");

        Directory.CreateDirectory(targetDirectoryPath);

        // Rendered whole BEFORE anything is opened for writing: LedgerCsv.Write refuses an unpublishable
        // cell by throwing, and a refusal must leave the previous export intact rather than truncated.
        var csv = new StringWriter { NewLine = "\n" };
        LedgerCsv.Write(csv, rows);

        var csvPath = Path.Combine(targetDirectoryPath, fileName);
        var existed = File.Exists(csvPath);
        await File.WriteAllTextAsync(csvPath, csv.ToString(), new UTF8Encoding(false), cancellationToken)
            .ConfigureAwait(false);

        var newestRow = rows.Select(row => row.EndedAt).Where(endedAt => endedAt is not null).Max();
        var readmePath = Path.Combine(targetDirectoryPath, "README.md");
        UpdateReadme(readmePath, fileName, rows.Count, newestRow);

        Write(output, $"Cost ledger export: {csvPath}");
        Write(
            output,
            $"  {rows.Count} row(s), schema {LedgerCsv.SchemaVersion}, newest endedAt {Instant(newestRow)}"
                + (existed ? " (rewrote an export already carrying this date)" : string.Empty));
        Write(output, $"  read {ledgerFilePath}; wrote nothing else under it");
        Write(output, $"  indexed in {readmePath}");
        return 0;
    }

    /// <summary>
    /// Replaces the marked table with one carrying <paramref name="fileName"/>'s row, newest export
    /// first. <b>Replaces, never appends</b> — re-exporting a date already in the table updates that
    /// date's row in place, which is the half of "idempotent" that is not simply rewriting the CSV.
    /// </summary>
    private static void UpdateReadme(string readmePath, string fileName, int rowCount, DateTime? newestRow)
    {
        var row = FormattableString.Invariant(
            $"| [`{fileName}`]({fileName}) | `{LedgerCsv.SchemaVersion}` | {rowCount} | {Instant(newestRow)} |");

        if (!File.Exists(readmePath))
        {
            File.WriteAllText(
                readmePath,
                DefaultReadme(row),
                new UTF8Encoding(false));
            return;
        }

        var existing = File.ReadAllText(readmePath).Replace("\r\n", "\n", StringComparison.Ordinal);
        var begin = existing.IndexOf(TableBeginMarker, StringComparison.Ordinal);
        var end = existing.IndexOf(TableEndMarker, StringComparison.Ordinal);
        if (begin < 0 || end < begin)
        {
            throw new CliArgumentException(
                $"'{readmePath}' exists but carries no '{TableBeginMarker}' / '{TableEndMarker}' pair, so "
                + "there is nowhere to write this export's row. The table is a MARKED REGION inside a "
                + "hand-written README rather than the whole file; add the markers where the table belongs, "
                + "or delete the README and let this command write a fresh one.",
                $"add {TableBeginMarker} and {TableEndMarker} to {readmePath}");
        }

        var kept = ExistingRows(existing[(begin + TableBeginMarker.Length)..end], fileName);
        kept.Add(row);
        // Newest export first: a reader who stops after the first data row has read the current one.
        kept.Sort(static (left, right) => string.CompareOrdinal(right, left));

        var rebuilt = new StringBuilder()
            .Append(existing.AsSpan(0, begin + TableBeginMarker.Length))
            .Append('\n')
            .Append(TableHeader)
            .Append('\n')
            .AppendJoin('\n', kept)
            .Append('\n')
            .Append(existing.AsSpan(end))
            .ToString();

        File.WriteAllText(readmePath, rebuilt, new UTF8Encoding(false));
    }

    /// <summary>
    /// The table's existing data rows, minus the one this export replaces and minus the header —
    /// matched on the file name in the first cell, so a re-export of the same date updates rather than
    /// duplicating, and an export of a NEW date leaves every older row alone.
    /// </summary>
    private static List<string> ExistingRows(string region, string fileName)
    {
        var replaced = $"[`{fileName}`]";
        return region.Split('\n')
            .Select(line => line.Trim())
            .Where(line =>
                line.StartsWith('|')
                && !line.StartsWith("|---", StringComparison.Ordinal)
                && !line.StartsWith("| Export", StringComparison.Ordinal)
                && !line.Contains(replaced, StringComparison.Ordinal))
            .ToList();
    }

    private static string DefaultReadme(string row) =>
        "# Cost-ledger exports\n"
        + "\n"
        + "Written by `baton ledger export --to <dir>`: each file is the whole repository-keyed cost\n"
        + "ledger (`~/.baton/ledger/<repository>.jsonl`) as it stood on that date, in the same bytes\n"
        + "`baton ledger --format csv` prints. The date names the file; it does not window the rows.\n"
        + "\n"
        + "The `Schema version` column is `LedgerCsv.SchemaVersion` — the column count and a truncated\n"
        + "digest of the header, derived from the column list itself. Equal versions mean two files'\n"
        + "headers are byte-comparable.\n"
        + "\n"
        + TableBeginMarker + "\n"
        + TableHeader + "\n"
        + row + "\n"
        + TableEndMarker + "\n";

    /// <summary>UTC, the frame the ledger records in — matching <see cref="LedgerViewCommand"/>'s own rendering.</summary>
    private static string Instant(DateTime? value)
    {
        if (value is not { } present)
        {
            return "-";
        }

        var utc = present.Kind == DateTimeKind.Local ? present.ToUniversalTime() : present;
        return DateTime.SpecifyKind(utc, DateTimeKind.Utc)
            .ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
    }

    /// <summary>LF explicitly, for the reason <see cref="LedgerViewCommand"/>'s own <c>Write</c> states.</summary>
    private static void Write(TextWriter output, string line)
    {
        output.Write(line);
        output.Write('\n');
    }
}
