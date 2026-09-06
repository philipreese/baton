using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Baton.Accounting;

namespace Baton.Cli.Tests;

/// <summary>
/// #1901 C3's redaction (operator ruling 2026-09-05): <c>benchmarks/ledger/*.csv</c> is committed to a
/// PUBLIC repository, so the CSV writer reduces a path column to its basename and refuses outright any
/// cell that still carries a filesystem path or this machine's OS account name.
/// </summary>
/// <remarks>
/// Every case here is written in both polarities, because "redacted" and "wrote nothing at all" print
/// the same way to a naive assertion: each refusal arm has a control that differs by exactly the thing
/// under test and must be accepted, and the basename arm has <c>--format json</c> as its control —
/// that format is deliberately NOT redacted, so a test that passed against both formats would be
/// measuring nothing.
/// </remarks>
public sealed class LedgerCsvRedactionTests
{
    /// <summary>Some other machine's operator, so the arms are independent of whose machine runs the suite.</summary>
    private const string OtherAccount = "alice";

    private static readonly CostLedgerEntry Row = new(
        CostSourceKind.BatonExecution,
        Repository: "github.com/philipreese/baton",
        Room: @"C:\Users\alice\.baton\rooms\dispatch-implement-6f5e89cc",
        ParentRoom: @"C:\Users\alice\.baton\rooms\dispatch-conductor-11112222",
        Execution: "e1",
        Adapter: "claude",
        Model: "claude-opus-5",
        EndedAt: new DateTime(2026, 9, 5, 10, 0, 0, DateTimeKind.Utc),
        TokensIn: 100,
        EstimateStatus: EstimateStatus.Unpriced,
        PlanMeterEstimateStatus: EstimateStatus.Unpriced);

    [Fact]
    public void A_room_path_is_reduced_to_the_basename_that_is_the_rooms_whole_identity()
    {
        var csv = Write(Row, accountName: "nobody-by-this-name");

        Assert.Contains("dispatch-implement-6f5e89cc", csv, StringComparison.Ordinal);
        Assert.Contains("dispatch-conductor-11112222", csv, StringComparison.Ordinal);
        Assert.DoesNotContain(OtherAccount, csv, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"C:\", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("Users", csv, StringComparison.Ordinal);

        // The control that makes the assertions above mean something: the SAME row through the format
        // that is deliberately unredacted still carries the whole path. Without this, a writer that
        // dropped the two columns entirely would pass every line above.
        var json = JsonSerializer.Serialize(Row);
        Assert.Contains(@"C:\\Users\\alice", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// A trailing separator is the case a naive "everything after the last slash" gets wrong by
    /// returning the empty string — which would silently blank the room column rather than fail.
    /// </summary>
    [Fact]
    public void A_trailing_separator_still_yields_the_basename_rather_than_an_empty_cell()
    {
        var csv = Write(Row with { Room = @"C:\Users\alice\.baton\rooms\room-a\" }, accountName: "nobody");

        Assert.Contains("room-a", csv, StringComparison.Ordinal);
    }

    /// <summary>
    /// Both separators, on whichever host runs the suite. <see cref="Path.GetFileName(string)"/> is
    /// host-dependent — a backslash is an ordinary character on Linux — and the ledger is read wherever
    /// an analysis runs, not only where it was written.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Users\alice\.baton\rooms\room-a")]
    [InlineData("/home/alice/.baton/rooms/room-a")]
    public void Both_separators_are_reduced_regardless_of_the_host(string roomPath)
    {
        var csv = Write(Row with { Room = roomPath }, accountName: "nobody");

        Assert.Contains(",room-a,", csv, StringComparison.Ordinal);
        Assert.DoesNotContain(OtherAccount, csv, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The channel a column allowlist cannot close — <see cref="LedgerCsv"/>'s own note on why the
    /// guard is a shape rather than a list. A path in a free-text reason field refuses the export
    /// rather than being published.
    /// </summary>
    [Fact]
    public void A_path_left_in_a_free_text_column_refuses_the_whole_export()
    {
        var leaking = Row with { ResolutionReason = @"hand-fixed from C:\Users\alice\notes.md" };

        var refusal = Assert.Throws<LedgerCsvRedactionException>(() => Write(leaking, accountName: "nobody"));
        Assert.Equal("resolutionReason", refusal.Column);

        // Polarity: the same row differing only in that one cell is accepted, so the refusal is about
        // the path and not about the column being populated at all.
        Assert.Contains(
            "hand-fixed", Write(Row with { ResolutionReason = "hand-fixed" }, accountName: "nobody"), StringComparison.Ordinal);
    }

    /// <summary>
    /// The <c>raw</c> column — see <see cref="LedgerCsv"/>'s own note on why no column list can be the
    /// guard here. It is empty in every row written today, which is exactly why it is pinned now: the
    /// leak it can carry would first appear in a phase nobody is thinking about redaction in.
    /// </summary>
    [Fact]
    public void A_path_buried_in_the_raw_vendor_object_refuses_the_whole_export()
    {
        var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            """{"cwd":"C:\\Users\\alice\\source\\repos\\baton"}""")!;

        var refusal = Assert.Throws<LedgerCsvRedactionException>(
            () => Write(Row with { Raw = raw }, accountName: "nobody"));
        Assert.Equal("raw", refusal.Column);

        var innocuous = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"turns":4}""")!;
        Assert.Contains("turns", Write(Row with { Raw = innocuous }, accountName: "nobody"), StringComparison.Ordinal);
    }

    /// <summary>
    /// The account name check, which catches what the path-shape check cannot: a bare name in a cell
    /// with no separators around it. Both arms drive the SAME cell value, differing only in which
    /// account name the writer was told to refuse.
    /// </summary>
    [Fact]
    public void A_bare_OS_account_name_refuses_the_export_and_another_machines_name_does_not()
    {
        var row = Row with { Workstream = "alice-efficiency-study" };

        var refusal = Assert.Throws<LedgerCsvRedactionException>(() => Write(row, accountName: OtherAccount));
        Assert.Equal("workstream", refusal.Column);

        Assert.Contains("alice-efficiency-study", Write(row, accountName: "bob"), StringComparison.Ordinal);
    }

    /// <summary>
    /// A refusal names the offending column, and a refusal is a <see cref="BatonFlowException"/> rather
    /// than a bare <see cref="InvalidOperationException"/> — CLAUDE.md's error-handling rule, and the
    /// difference between an operator who can fix the row and one who reads a stack trace.
    /// </summary>
    [Fact]
    public void A_refusal_names_the_column_and_says_which_format_is_not_redacted()
    {
        var refusal = Assert.Throws<LedgerCsvRedactionException>(
            () => Write(Row with { EstimateReason = "/home/alice/x" }, accountName: "nobody"));

        Assert.IsAssignableFrom<BatonFlowException>(refusal);
        Assert.Contains("estimateReason", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("--format json", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <see cref="LedgerCsv.SchemaVersion"/> is a pure function of <see cref="LedgerCsv.Columns"/> and
    /// nothing else — that is the whole reason it exists rather than a hand-maintained literal, and it
    /// is what <c>benchmarks/ledger/README.md</c>'s "schema version" column means. Recomputed here from
    /// the column list independently, so adding a field to <see cref="CostLedgerEntry"/> moves the
    /// version automatically and a version pinned to a stale constant would fail.
    /// </summary>
    [Fact]
    public void The_schema_version_is_derived_from_the_column_list_alone()
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(',', LedgerCsv.Columns)));
        var expected = $"{LedgerCsv.Columns.Count}-{Convert.ToHexStringLower(digest.AsSpan(0, 6))}";

        Assert.Equal(expected, LedgerCsv.SchemaVersion);

        // Discriminating control: a version computed over a DIFFERENT column list must differ, so the
        // equality above is not satisfied by any constant.
        var otherDigest = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(',', LedgerCsv.Columns.Skip(1))));
        Assert.NotEqual(
            $"{LedgerCsv.Columns.Count - 1}-{Convert.ToHexStringLower(otherDigest.AsSpan(0, 6))}",
            LedgerCsv.SchemaVersion);
    }

    private static string Write(CostLedgerEntry row, string accountName)
    {
        var output = new StringWriter { NewLine = "\n" };
        LedgerCsv.Write(output, [row], accountName);
        return output.ToString();
    }
}
