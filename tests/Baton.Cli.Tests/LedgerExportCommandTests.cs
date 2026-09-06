using Baton.Accounting;
using Baton.Tests.Shared;

namespace Baton.Cli.Tests;

/// <summary>
/// #1901 C3's verb, driven end to end over a real ledger file: the export's bytes, the README table it
/// maintains, its idempotence for one date, and that it never writes the store it reads.
/// </summary>
public sealed class LedgerExportCommandTests : IDisposable
{
    private static readonly DateTime Sep5 = new(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"baton-1901c3-{Guid.NewGuid():N}");

    private string LedgerFilePath => Path.Combine(_root, "ledger.jsonl");
    private string TargetDirectoryPath => Path.Combine(_root, "benchmarks", "ledger");
    private string ReadmePath => Path.Combine(TargetDirectoryPath, "README.md");

    public void Dispose() => DirectoryCleanup.DeleteRecursively(_root);

    /// <summary>
    /// The claim the whole verb rests on: an export is not a second rendering of the ledger, it is
    /// literally what <c>baton ledger --format csv</c> prints over the whole store. Compared as BYTES,
    /// because the two would still "agree" on every value while differing in line endings, quoting or
    /// row order — the three things #1849's determinism criterion is actually about.
    /// </summary>
    [Fact]
    public async Task The_exported_file_is_byte_for_byte_what_format_csv_prints_over_the_whole_store()
    {
        await SeedAsync();
        await ExportAsync("--to", TargetDirectoryPath, "--as-of", "2026-09-05");

        var view = new StringWriter { NewLine = "\n" };
        Assert.Equal(
            0,
            await LedgerViewCommand.ExecuteAsync(
                LedgerViewOptionsParser.Parse(["--format", "csv"]),
                view,
                LedgerFilePath,
                TestContext.Current.CancellationToken));

        var exported = await File.ReadAllTextAsync(
            Path.Combine(TargetDirectoryPath, "2026-09-05.csv"), TestContext.Current.CancellationToken);

        Assert.Equal(view.ToString(), exported);
        Assert.DoesNotContain('\r', exported);
    }

    /// <summary>
    /// The date names the FILE; the content is the whole store at write time. Pinned because the
    /// opposite reading — a dated file as a windowed one — is exactly what a reader's prior supplies,
    /// and a row that ended after the named date being present is what disproves it.
    /// </summary>
    [Fact]
    public async Task The_as_of_date_names_the_file_without_windowing_the_rows()
    {
        await SeedAsync();
        await ExportAsync("--to", TargetDirectoryPath, "--as-of", "2026-09-05");

        var exported = await File.ReadAllTextAsync(
            Path.Combine(TargetDirectoryPath, "2026-09-05.csv"), TestContext.Current.CancellationToken);

        // e3 ended on the 7th, two days after the name on the file, and is in it.
        Assert.Contains("e3", exported, StringComparison.Ordinal);
        Assert.Equal(4, exported.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public async Task The_readme_table_records_the_file_the_schema_version_the_row_count_and_the_newest_row()
    {
        await SeedAsync();
        await ExportAsync("--to", TargetDirectoryPath, "--as-of", "2026-09-05");

        var readme = await File.ReadAllTextAsync(ReadmePath, TestContext.Current.CancellationToken);

        Assert.Contains("[`2026-09-05.csv`](2026-09-05.csv)", readme, StringComparison.Ordinal);
        Assert.Contains($"`{LedgerCsv.SchemaVersion}`", readme, StringComparison.Ordinal);
        Assert.Contains("| 3 |", readme, StringComparison.Ordinal);
        Assert.Contains("2026-09-07T09:00:00Z", readme, StringComparison.Ordinal);
        Assert.Contains(LedgerExportCommand.TableBeginMarker, readme, StringComparison.Ordinal);
        Assert.Contains(LedgerExportCommand.TableEndMarker, readme, StringComparison.Ordinal);
    }

    /// <summary>
    /// Idempotence has two halves and only one of them is the CSV. Re-exporting a date must rewrite
    /// that day's file AND update its existing README row in place; appending a second row for the same
    /// date is the failure this pins, and it is invisible in the CSV half.
    /// </summary>
    [Fact]
    public async Task Re_exporting_the_same_date_updates_its_readme_row_in_place()
    {
        await SeedAsync();
        await ExportAsync("--to", TargetDirectoryPath, "--as-of", "2026-09-05");
        var first = await File.ReadAllTextAsync(
            Path.Combine(TargetDirectoryPath, "2026-09-05.csv"), TestContext.Current.CancellationToken);

        await ExportAsync("--to", TargetDirectoryPath, "--as-of", "2026-09-05");

        var readme = await File.ReadAllTextAsync(ReadmePath, TestContext.Current.CancellationToken);
        Assert.Equal(1, CountRowsFor(readme, "2026-09-05.csv"));
        Assert.Equal(
            first,
            await File.ReadAllTextAsync(
                Path.Combine(TargetDirectoryPath, "2026-09-05.csv"), TestContext.Current.CancellationToken));

        // The discriminating control: a DIFFERENT date is a second row, not a replacement -- so the
        // in-place update above is keyed on the date and is not "the table only ever holds one row".
        await ExportAsync("--to", TargetDirectoryPath, "--as-of", "2026-09-12");
        readme = await File.ReadAllTextAsync(ReadmePath, TestContext.Current.CancellationToken);
        Assert.Equal(1, CountRowsFor(readme, "2026-09-05.csv"));
        Assert.Equal(1, CountRowsFor(readme, "2026-09-12.csv"));

        // Newest first: a reader who stops after the first data row has read the current export.
        Assert.True(
            readme.IndexOf("2026-09-12.csv", StringComparison.Ordinal)
                < readme.IndexOf("2026-09-05.csv", StringComparison.Ordinal),
            readme);
    }

    /// <summary>
    /// The table is a marked REGION inside a hand-written README, so the prose explaining what the
    /// numbers do and do not mean has to survive every export. Regenerating the whole file would delete
    /// it silently, which is the failure this pins.
    /// </summary>
    [Fact]
    public async Task Hand_written_prose_around_the_table_survives_a_later_export()
    {
        await SeedAsync();
        await ExportAsync("--to", TargetDirectoryPath, "--as-of", "2026-09-05");

        var readme = await File.ReadAllTextAsync(ReadmePath, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            ReadmePath,
            readme.Replace(
                LedgerExportCommand.TableEndMarker,
                LedgerExportCommand.TableEndMarker + "\n\nHAND-WRITTEN PARAGRAPH.\n",
                StringComparison.Ordinal),
            TestContext.Current.CancellationToken);

        await ExportAsync("--to", TargetDirectoryPath, "--as-of", "2026-09-12");

        Assert.Contains(
            "HAND-WRITTEN PARAGRAPH.",
            await File.ReadAllTextAsync(ReadmePath, TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A README with no markers is refused rather than appended to: a second table quietly grown at the
    /// end of the file is how a reader ends up reading the stale one.
    /// </summary>
    [Fact]
    public async Task A_readme_carrying_no_markers_is_refused_rather_than_appended_to()
    {
        await SeedAsync();
        Directory.CreateDirectory(TargetDirectoryPath);
        await File.WriteAllTextAsync(ReadmePath, "# Something else entirely\n", TestContext.Current.CancellationToken);

        var refusal = await Assert.ThrowsAsync<CliArgumentException>(
            () => ExportAsync("--to", TargetDirectoryPath, "--as-of", "2026-09-05"));

        Assert.Contains("table begins", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The scope rule the C3 ruling states, made checkable: this verb reads the ledger and writes only
    /// into <c>--to</c>. Byte-compared rather than timestamp-compared, because a rewrite that produced
    /// identical mtimes would pass the cheaper check.
    /// </summary>
    [Fact]
    public async Task The_export_never_writes_the_store_it_reads()
    {
        await SeedAsync();
        var before = await File.ReadAllBytesAsync(LedgerFilePath, TestContext.Current.CancellationToken);

        await ExportAsync("--to", TargetDirectoryPath, "--as-of", "2026-09-05");

        Assert.Equal(before, await File.ReadAllBytesAsync(LedgerFilePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void The_destination_is_required_because_there_is_no_sensible_default()
    {
        var refusal = Assert.Throws<CliArgumentException>(() => LedgerExportOptionsParser.Parse(["--as-of", "2026-09-05"]));
        Assert.Contains("--to", refusal.Message, StringComparison.Ordinal);

        // Polarity: --help alone is legal and prints the grammar without a destination.
        Assert.True(LedgerExportOptionsParser.Parse(["--help"]).Help);
    }

    [Fact]
    public void An_as_of_carrying_a_time_of_day_is_refused_because_it_names_a_file()
    {
        Assert.Throws<CliArgumentException>(
            () => LedgerExportOptionsParser.Parse(["--to", "x", "--as-of", "2026-09-05T14:00:00Z"]));

        Assert.Equal(
            new DateTime(2026, 9, 5, 0, 0, 0, DateTimeKind.Unspecified),
            LedgerExportOptionsParser.Parse(["--to", "x", "--as-of", "2026-09-05"]).AsOf);
    }

    private static int CountRowsFor(string readme, string fileName) =>
        readme.Split('\n').Count(line => line.TrimStart().StartsWith($"| [`{fileName}`]", StringComparison.Ordinal));

    private Task ExportAsync(params string[] args) =>
        LedgerExportCommand.ExecuteAsync(
            LedgerExportOptionsParser.Parse(args),
            new StringWriter { NewLine = "\n" },
            LedgerFilePath,
            cancellationToken: TestContext.Current.CancellationToken);

    /// <summary>Three rows through the production writer, one of them ending after the export's own date.</summary>
    private async Task SeedAsync()
    {
        var row = new CostLedgerEntry(
            CostSourceKind.BatonExecution,
            Repository: "github.com/philipreese/baton",
            Room: @"C:\Users\alice\.baton\rooms\dispatch-implement-6f5e89cc",
            Execution: "e1",
            Role: "implement",
            Adapter: "claude",
            Model: "claude-opus-5",
            Outcome: "Succeeded",
            EndedAt: Sep5.AddHours(10),
            TokensIn: 100,
            BilledTokens: 400,
            Turns: 12,
            WallClockMs: 60000,
            Completeness: CostCompleteness.Complete,
            EstimateStatus: EstimateStatus.Unpriced,
            PlanMeterEstimateStatus: EstimateStatus.Unpriced);

        await CostLedgerStore.AppendAsync(
            [
                row,
                row with { Execution = "e2", EndedAt = Sep5.AddHours(11) },
                row with { Execution = "e3", EndedAt = Sep5.AddDays(2).AddHours(9) },
            ],
            LedgerFilePath,
            TestContext.Current.CancellationToken);
    }
}
