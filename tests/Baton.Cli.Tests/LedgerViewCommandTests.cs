using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Accounting;
using Baton.Status;
using Baton.Tests.Shared;

namespace Baton.Cli.Tests;

/// <summary>
/// #1849 phase B's verb, driven end to end over a real ledger file. <c>LedgerRollupTests</c> owns the
/// arithmetic; this file owns what an operator and a machine consumer actually receive — the fixed
/// output ORDER, the JSON contract #1746 and #1848 read, the CSV export's header and line endings, and
/// the byte-identity promise.
/// </summary>
/// <remarks>
/// The fixture is a mixed-vendor ledger written through <see cref="CostLedgerStore.AppendAsync"/>
/// rather than hand-written JSONL, so these tests read the same bytes production writes: a claude
/// retry pair, an agy row with no cache-creation dimension, an unpriced codex row, a partial row, and
/// an undated row a window cannot place.
/// </remarks>
public sealed class LedgerViewCommandTests : IDisposable
{
    private static readonly DateTime Sep4 = new(2026, 9, 4, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _ledgerFilePath = Path.Combine(
        Path.GetTempPath(), $"baton-1849b-{Guid.NewGuid():N}", "ledger.jsonl");

    private readonly string _roomA = BatonPaths.RecordKey(Path.Combine(Path.GetTempPath(), "baton-1849b", "room-a"));
    private readonly string _roomB = BatonPaths.RecordKey(Path.Combine(Path.GetTempPath(), "baton-1849b", "room-b"));

    public void Dispose()
    {
        var directory = Path.GetDirectoryName(_ledgerFilePath);
        if (directory is not null)
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public async Task Text_prints_every_vendor_subtotal_before_the_labelled_all_vendor_estimate()
    {
        var text = await RunAsync("--format", "text");

        var claude = text.IndexOf("claude --", StringComparison.Ordinal);
        var agy = text.IndexOf("agy --", StringComparison.Ordinal);
        var total = text.IndexOf("all vendors --", StringComparison.Ordinal);

        Assert.True(agy >= 0 && claude > agy, text);
        Assert.True(total > claude, text);
        Assert.Contains("ESTIMATES", text, StringComparison.Ordinal);
        Assert.Contains("Neither is an invoice", text, StringComparison.Ordinal);

        // Token totals sit beside the money, never money alone.
        Assert.Contains("tokens: in 300, out 30, cache-read 40, cache-creation -, thinking 9", text, StringComparison.Ordinal);

        // The unpriced codex row is counted as an attempt and disclosed as unpriced, not as $0.
        Assert.Contains("codex -- 1 attempt(s)", text, StringComparison.Ordinal);
        Assert.Contains(
            "API-equivalent estimate: - (summed from 0 of 1 attempt(s); unpriced: 1)", text, StringComparison.Ordinal);

        // ...and agy's plan meter says what the ROW says -- never measured for this vendor -- rather
        // than borrowing codex's word for a missing rate.
        Assert.Contains(
            "plan-meter estimate: - (summed from 0 of 1 attempt(s); unmeasured: 1)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("unmeasured: 1, unpriced", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// #1893 review M1, at the surface an operator reads: the all-vendor cache-creation figure is
    /// claude's and codex's rows only — agy reports no such dimension — and the line above it says so.
    /// The control is the per-vendor blocks, where every attempt fed every dimension it reported, so
    /// none of them carries the line: a disclosure printed unconditionally would fail here.
    /// </summary>
    [Fact]
    public async Task The_all_vendor_line_discloses_a_token_total_only_some_attempts_fed()
    {
        var text = await RunAsync("--format", "text");

        var total = text.IndexOf("all vendors --", StringComparison.Ordinal);
        var disclosure = text.IndexOf(
            "partial -- summed from SOME of the attempts only: cache-creation 5 of 6", StringComparison.Ordinal);

        Assert.True(disclosure > total, text);
        Assert.Equal(1, text.Split("partial -- summed from SOME").Length - 1);
    }

    [Fact]
    public async Task Drill_rows_come_after_the_subtotals_they_roll_into_and_are_omitted_without_the_flag()
    {
        var withoutDrill = await RunAsync("--format", "text");
        Assert.DoesNotContain("Rows contributing", withoutDrill, StringComparison.Ordinal);

        var withDrill = await RunAsync("--format", "text", "--drill");
        var total = withDrill.IndexOf("all vendors --", StringComparison.Ordinal);
        var rows = withDrill.IndexOf("Rows contributing to the subtotals above (6):", StringComparison.Ordinal);

        Assert.True(rows > total, withDrill);
        Assert.Contains("(no endedAt)", withDrill, StringComparison.Ordinal);
    }

    /// <summary>
    /// The machine contract, checked as a contract rather than as a string: one object with those
    /// members, the ledger record's own field names inside them, and no <c>null</c>-valued members for
    /// facets nobody set.
    /// </summary>
    [Fact]
    public async Task Json_is_one_object_of_query_vendors_total_and_omits_what_was_not_written()
    {
        using var document = JsonDocument.Parse(await RunAsync("--format", "json"));
        var root = document.RootElement;

        Assert.Equal(
            ["query", "vendors", "total"],
            root.EnumerateObject().Select(p => p.Name).ToArray());

        var query = root.GetProperty("query");
        Assert.False(query.TryGetProperty("since", out _));
        Assert.Equal(0, query.GetProperty("undatedExcluded").GetInt32());

        var vendors = root.GetProperty("vendors");
        Assert.Equal(["agy", "claude", "codex"], vendors.EnumerateArray().Select(v => v.GetProperty("adapter").GetString()).ToArray());

        var agy = vendors.EnumerateArray().Single(v => v.GetProperty("adapter").GetString() == "agy");
        Assert.Equal(300, agy.GetProperty("tokensIn").GetInt64());

        // Absent stays absent through the projection: agy reports no cache-creation at all.
        Assert.False(agy.TryGetProperty("cacheCreation", out _));
        Assert.True(agy.TryGetProperty("cacheRead", out _));

        Assert.Equal(6, root.GetProperty("total").GetProperty("attempts").GetInt32());
        Assert.False(root.TryGetProperty("rows", out _));
    }

    /// <summary>
    /// The drill-down claim #1849 makes — a fleet aggregate drills down to the rows that produced it —
    /// checked as arithmetic rather than as presence: each vendor subtotal equals the sum of its own
    /// rows in the same document.
    /// </summary>
    [Fact]
    public async Task Json_drill_rows_sum_to_the_subtotals_they_roll_into()
    {
        using var document = JsonDocument.Parse(await RunAsync("--format", "json", "--drill"));
        var root = document.RootElement;
        var rows = root.GetProperty("rows").EnumerateArray().ToList();

        Assert.Equal(6, rows.Count);

        foreach (var vendor in root.GetProperty("vendors").EnumerateArray())
        {
            var adapter = vendor.GetProperty("adapter").GetString();
            var own = rows.Where(r => r.GetProperty("adapter").GetString() == adapter).ToList();

            Assert.Equal(vendor.GetProperty("attempts").GetInt32(), own.Count);
            Assert.Equal(
                own.Sum(r => r.TryGetProperty("tokensIn", out var t) ? t.GetInt64() : 0),
                vendor.GetProperty("tokensIn").GetInt64());
            Assert.Equal(
                own.Sum(r => r.TryGetProperty("apiEquivalentUsd", out var c) ? c.GetDecimal() : 0m),
                vendor.TryGetProperty("apiEquivalentUsd", out var subtotal) ? subtotal.GetDecimal() : 0m);
        }
    }

    [Fact]
    public async Task Csv_writes_the_records_own_header_then_one_LF_terminated_line_per_row()
    {
        var csv = await RunAsync("--format", "csv", "--drill");

        Assert.DoesNotContain('\r', csv);
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(string.Join(',', LedgerCsv.Columns), lines[0]);
        Assert.Equal(7, lines.Length);
        Assert.EndsWith("\n", csv, StringComparison.Ordinal);

        // An absent dimension is an EMPTY cell, never a zero a spreadsheet would sum.
        var agyLine = lines.Single(l => l.Contains(",agy,", StringComparison.Ordinal));
        var cells = agyLine.Split(',');
        Assert.Equal(LedgerCsv.Columns.Count, cells.Length);
        Assert.Equal(string.Empty, cells[LedgerCsv.Columns.ToList().IndexOf("cacheCreation")]);
        Assert.Equal("40", cells[LedgerCsv.Columns.ToList().IndexOf("cacheRead")]);
    }

    /// <summary>
    /// <c>--drill</c> is not a prerequisite for the export — <see cref="LedgerViewCommand"/>'s csv
    /// branch states why. Pinned here because the failure it prevents is silent: a header, no rows,
    /// and exit 0.
    /// </summary>
    [Fact]
    public async Task Csv_exports_every_matching_row_with_or_without_drill()
    {
        var withoutDrill = await RunAsync("--format", "csv");

        Assert.Equal(await RunAsync("--format", "csv", "--drill"), withoutDrill);
        Assert.Equal(7, withoutDrill.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);

        // And a filter still narrows it -- the export follows the query, it is not a dump of the file.
        var filtered = await RunAsync("--format", "csv", "--vendor", "agy");
        Assert.Equal(2, filtered.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    /// <summary>
    /// The guard the CSV export cannot supply for itself: a field added to (or renamed on)
    /// <see cref="CostLedgerEntry"/> — phase C fills six reserved ones — must appear as a column, and a
    /// column with no field behind it would export empty forever. Discovered by reflection, never listed.
    /// <para>
    /// <b>Fail-closed on the attribute itself</b> (#1893 review L4): filtering out the properties that
    /// lack <c>[JsonPropertyName]</c> would let a phase-C field added without one serialize under its
    /// default name, never reach <see cref="LedgerCsv.Columns"/>, and vanish from the export with this
    /// test still green. Every public instance property must carry it.
    /// </para>
    /// </summary>
    [Fact]
    public void Csv_columns_are_exactly_the_ledger_records_own_field_names()
    {
        var properties = typeof(CostLedgerEntry)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToList();

        Assert.Empty(
            properties
                .Where(p => p.GetCustomAttribute<JsonPropertyNameAttribute>() is null)
                .Select(p => p.Name));

        var recordFields = properties
            .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()!.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(recordFields);
        Assert.Empty(recordFields.Except(LedgerCsv.Columns));
        Assert.Empty(LedgerCsv.Columns.Except(recordFields));
    }

    [Fact]
    public async Task The_same_query_over_the_same_file_is_byte_identical()
    {
        var first = await RunAsync("--format", "json", "--drill", "--since", "2026-09-04T00:00:00Z");
        var second = await RunAsync("--format", "json", "--drill", "--since", "2026-09-04T00:00:00Z");

        Assert.Equal(first, second);
        Assert.Equal(await RunAsync("--format", "text"), await RunAsync("--format", "text"));
    }

    /// <summary>#1849: "room and fleet surfaces use one accounting projection" — asserted as an identity between two invocations, not by reading one implementation.</summary>
    [Fact]
    public async Task A_room_view_is_the_fleet_view_filtered_to_that_room()
    {
        using var roomView = JsonDocument.Parse(await RunAsync(_roomB, "--format", "json", "--drill"));
        using var fleetView = JsonDocument.Parse(await RunAsync("--format", "json", "--drill"));

        var fromRoom = roomView.RootElement.GetProperty("rows").EnumerateArray()
            .Select(r => r.GetProperty("execution").GetString()).ToArray();
        var fromFleet = fleetView.RootElement.GetProperty("rows").EnumerateArray()
            .Where(r => BatonPaths.RecordKeyComparer.Equals(r.GetProperty("room").GetString(), _roomB))
            .Select(r => r.GetProperty("execution").GetString()).ToArray();

        Assert.Equal(fromFleet, fromRoom);
        Assert.NotEmpty(fromRoom);
        Assert.Equal(
            roomView.RootElement.GetProperty("total").GetProperty("attempts").GetInt32(),
            fromRoom.Length);

        // ...and the same room in the other casing is the same room (#1893 review L1): the argument
        // above is already canonical, so only this arm exercises the CLI's own Resolve -> RecordKey
        // chain and the case-insensitive comparer behind it. An ordinal comparison anywhere along it
        // answers "no rows here", which the NotEmpty above would then catch.
        using var otherCasing = JsonDocument.Parse(
            await RunAsync(_roomB.ToUpperInvariant(), "--format", "json", "--drill"));
        Assert.Equal(
            fromRoom,
            otherCasing.RootElement.GetProperty("rows").EnumerateArray()
                .Select(r => r.GetProperty("execution").GetString()).ToArray());
    }

    /// <summary>
    /// A room the ledger has never heard of must not read as a room that cost nothing — the same
    /// disclosure the missing-file line makes, one level down. The control is the populated room: it
    /// prints no such line.
    /// </summary>
    [Fact]
    public async Task A_room_no_row_carries_says_so_rather_than_reporting_a_zero_total()
    {
        var stranger = Path.Combine(Path.GetTempPath(), "baton-1849b", "room-never-settled");

        var text = await RunAsync(stranger);
        Assert.Contains("no row in this ledger carries room", text, StringComparison.Ordinal);
        Assert.Contains(BatonPaths.RecordKey(stranger), text, StringComparison.Ordinal);

        Assert.DoesNotContain("no row in this ledger carries room", await RunAsync(_roomB), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_window_excludes_an_undated_row_and_says_how_many_it_excluded()
    {
        using var document = JsonDocument.Parse(
            await RunAsync("--format", "json", "--since", "2026-09-04", "--until", "2026-09-05"));

        Assert.Equal(1, document.RootElement.GetProperty("query").GetProperty("undatedExcluded").GetInt32());

        var text = await RunAsync("--since", "2026-09-04T00:00:00Z", "--until", "2026-09-05T00:00:00Z");
        Assert.Contains("1 excluded by the window for having no endedAt", text, StringComparison.Ordinal);
        Assert.Contains("endedAt >= 2026-09-04T00:00:00Z (inclusive) and < 2026-09-05T00:00:00Z (exclusive)", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// #1901's acceptance criterion "<c>baton ledger --format json</c> groups correctly by <c>pr</c>".
    /// <c>pr</c> is a FACET, not a grouping dimension — <c>LedgerRollup</c> groups by vendor and
    /// nothing else — so "grouping by PR" is the query narrowed to one, and what this pins is that the
    /// narrowing is exact in both directions: the other PR's rows are out, and so are the rows carrying
    /// no PR at all. Both spellings of the same PR are one PR
    /// (<c>LedgerQuery.NormalizeNumberReference</c>), which is the arm a writer that recorded
    /// <c>#1907</c> and a filter that compared ordinally would fail.
    /// </summary>
    [Fact]
    public async Task Json_narrowed_to_one_pr_carries_that_prs_rows_and_only_those()
    {
        using var first = JsonDocument.Parse(await RunAsync("--format", "json", "--drill", "--pr", "1907"));
        using var second = JsonDocument.Parse(await RunAsync("--format", "json", "--drill", "--pr", "#1908"));
        using var everything = JsonDocument.Parse(await RunAsync("--format", "json", "--drill"));

        static string[] Executions(JsonDocument document) =>
            [.. document.RootElement.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("execution").GetString()!)];

        Assert.Equal(["e1", "e2"], Executions(first));
        Assert.Equal(["e3"], Executions(second));

        // The two views partition their PRs' rows, and neither swept in the three rows with no PR --
        // the whole file is strictly larger than their union.
        Assert.Equal(2, first.RootElement.GetProperty("total").GetProperty("attempts").GetInt32());
        Assert.Equal(1, second.RootElement.GetProperty("total").GetProperty("attempts").GetInt32());
        Assert.Equal(6, everything.RootElement.GetProperty("total").GetProperty("attempts").GetInt32());

        // The echoed query says what the total is a total OF, which is what makes a stored reading
        // interpretable later.
        Assert.Equal("1907", first.RootElement.GetProperty("query").GetProperty("pr").GetString());
    }

    [Fact]
    public async Task Help_says_which_ledger_this_reads_and_which_instant_the_window_is_on()
    {
        var help = await RunAsync("--help");

        Assert.Contains("Filter on each attempt's endedAt", help, StringComparison.Ordinal);
        Assert.Contains("--since is INCLUSIVE, --until is EXCLUSIVE", help, StringComparison.Ordinal);
        Assert.Contains("quota-ledger.jsonl", help, StringComparison.Ordinal);
        Assert.Contains("never an invoice", help, StringComparison.Ordinal);
    }

    private async Task<string> RunAsync(params string[] args)
    {
        await SeedLedgerAsync();

        var options = LedgerViewOptionsParser.Parse(args);
        var output = new StringWriter { NewLine = "\n" };
        var exitCode = await LedgerViewCommand.ExecuteAsync(
            options, output, _ledgerFilePath, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        return output.ToString();
    }

    /// <summary>Written once per file, through the production writer — <see cref="CostLedgerStore.AppendAsync"/> skips an execution id already present, so re-seeding is idempotent.</summary>
    private async Task SeedLedgerAsync()
    {
        var claude = new CostLedgerEntry(
            CostSourceKind.BatonExecution,
            Repository: "github.com/aer-works/baton",
            Room: _roomA,
            Workflow: "wf1",
            Step: "s1",
            Execution: "e1",
            Role: "implement",
            Adapter: "claude",
            Model: "claude-opus-5",
            Outcome: "Succeeded",
            EndedAt: Sep4.AddHours(10),
            TokensIn: 100,
            TokensOut: 50,
            CacheReadTokens: 5,
            CacheCreationTokens: 10,
            ThinkingTokens: 7,
            Completeness: CostCompleteness.Complete,
            ApiEquivalentUsd: 1.5m,
            EstimateStatus: EstimateStatus.Estimated,
            PlanMeterEstimateUsd: 0.5m,
            PlanMeterEstimateStatus: EstimateStatus.Estimated);

        CostLedgerEntry[] entries =
        [
            // #1901 C1: e1/e2 belong to one PR, e3 to another, and e4-e6 to none — so the --pr facet
            // has something to be wrong about in both directions (a PR that over-matches, and rows with
            // no PR being swept in).
            claude with { PullRequest = "1907" },
            claude with { Execution = "e2", Outcome = "Failed", EndedAt = Sep4.AddHours(11), TokensIn = 200, PullRequest = "1907" },
            claude with
            {
                Execution = "e3",
                PullRequest = "1908",
                Room = _roomB,
                Adapter = "agy",
                Model = "gemini-3-pro",
                Role = "review",
                EndedAt = Sep4.AddHours(12),
                TokensIn = 300,
                TokensOut = 30,
                CacheReadTokens = 40,
                CacheCreationTokens = null,
                ThinkingTokens = 9,
                PlanMeterEstimateUsd = null,
                PlanMeterEstimateStatus = EstimateStatus.Unmeasured,
            },
            claude with
            {
                Execution = "e4",
                Room = _roomB,
                Adapter = "codex",
                Model = "gpt-5-codex",
                EndedAt = Sep4.AddDays(1).AddHours(9),
                ApiEquivalentUsd = null,
                EstimateStatus = EstimateStatus.Unpriced,
                EstimateReason = "model-mismatch",
                PlanMeterEstimateUsd = null,
                PlanMeterEstimateStatus = EstimateStatus.Unpriced,
            },
            claude with
            {
                Execution = "e5",
                EndedAt = Sep4.AddHours(13),
                Completeness = CostCompleteness.Partial,
                CompletenessReason = "no-terminal-billed-figure",
            },
            claude with { Execution = "e6", EndedAt = null, Completeness = null },
        ];

        await CostLedgerStore.AppendAsync(entries, _ledgerFilePath, TestContext.Current.CancellationToken);
    }
}
