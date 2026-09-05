using Baton.Status;
using Baton.Tests.Shared;
using Baton.Vendors;

namespace Baton.Vendors.Tests;

/// <summary>
/// #1904. <see cref="CodexUsageSource"/> spawns nothing, so unlike the two <c>/usage</c> sources these
/// tests need no process double at all — the aggregation half is pure over
/// <see cref="QuotaLedgerEntry"/> rows, and the one <see cref="CodexUsageSource.ReadAsync"/> arm drives
/// a REAL ledger file written by <c>QuotaLedgerStore</c>'s own writer rather than hand-rolled JSON, so
/// a change to the persisted shape breaks this test instead of silently passing against a fixture that
/// no longer matches what the daemon writes.
/// </summary>
public class CodexUsageSourceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly string _tempDirectory =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"baton-codex-usage-{Guid.NewGuid():N}")).FullName;

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        DirectoryCleanup.DeleteRecursively(_tempDirectory);
    }

    private static QuotaLedgerEntry Row(
        string adapter,
        DateTimeOffset at,
        long? tokensIn = null,
        long? tokensOut = null,
        long? cacheCreation = null,
        long? thinking = null,
        long? cacheRead = null) =>
        new(
            At: at.UtcDateTime,
            Execution: Guid.NewGuid().ToString("N"),
            Adapter: adapter,
            TokensIn: tokensIn,
            TokensOut: tokensOut,
            CacheReadTokens: cacheRead,
            CacheCreationTokens: cacheCreation,
            ThinkingTokens: thinking);

    /// <summary>A settled row the ledger recorded with no instant — <see cref="QuotaLedgerEntry.At"/>
    /// is nullable, so this is a shape the store can actually hand back, not a hypothetical.</summary>
    private static QuotaLedgerEntry Undated(string adapter, long? tokensIn = null) =>
        new(
            At: null,
            Execution: Guid.NewGuid().ToString("N"),
            Adapter: adapter,
            TokensIn: tokensIn,
            TokensOut: null,
            CacheReadTokens: null,
            CacheCreationTokens: null,
            ThinkingTokens: null);

    [Fact]
    public void Aggregate_sums_only_codex_rows_and_only_the_billed_dimensions()
    {
        // 1000 + 200 + 30 = 1230 on the first row, + 10 on the second = 1240 billed; the 9999 thinking
        // and 8888 cache-read figures are deliberately
        // OUTSIDE the sum (WorkerUsage.BilledTokens' own definition, #1682), and the agy row is another
        // vendor's burn that must never land on codex's window.
        var snapshot = CodexUsageSource.Aggregate(
            [
                Row("codex", Now.AddMinutes(-10), tokensIn: 1000, tokensOut: 200, cacheCreation: 30, thinking: 9999, cacheRead: 8888),
                Row("codex", Now.AddMinutes(-20), tokensIn: 5, tokensOut: 5),
                Row("agy", Now.AddMinutes(-15), tokensIn: 700_000, tokensOut: 700_000),
            ],
            ceiling: null,
            Now);

        Assert.NotNull(snapshot);
        Assert.Equal("codex", snapshot.Vendor);
        Assert.Equal(VendorUsageProvenance.Derived, snapshot.Source);

        var fiveHour = Assert.Single(snapshot.Windows, w => w.Name == CodexUsageSource.FiveHourWindowName);
        Assert.Contains("1,240 billed tokens", fiveHour.RawLine, StringComparison.Ordinal);
        Assert.Contains("2 settled codex executions", fiveHour.RawLine, StringComparison.Ordinal);
        Assert.DoesNotContain("700,000", fiveHour.RawLine, StringComparison.Ordinal);
    }

    [Fact]
    public void Aggregate_rolls_a_row_out_of_the_five_hour_window_but_not_the_weekly_one()
    {
        // The control arm this test rests on is the SECOND assertion pair: without the 4h row landing in
        // both windows, "the 6h row is only in the weekly one" could equally mean the five-hour window
        // is broken and counts nothing at all.
        var entries = new[]
        {
            Row("codex", Now.AddHours(-4), tokensIn: 100),
            Row("codex", Now.AddHours(-6), tokensIn: 1_000),
            Row("codex", Now.AddDays(-8), tokensIn: 1_000_000),
        };

        var snapshot = CodexUsageSource.Aggregate(entries, ceiling: null, Now);
        Assert.NotNull(snapshot);

        var fiveHour = Assert.Single(snapshot.Windows, w => w.Name == CodexUsageSource.FiveHourWindowName);
        var weekly = Assert.Single(snapshot.Windows, w => w.Name == CodexUsageSource.WeeklyWindowName);

        // 5h holds only the 4h row; 7d holds the 4h and 6h rows; the 8-day row is outside both.
        Assert.Contains("100 billed tokens across 1 settled codex execution ", fiveHour.RawLine, StringComparison.Ordinal);
        Assert.Contains("1,100 billed tokens across 2 settled codex executions", weekly.RawLine, StringComparison.Ordinal);
    }

    [Fact]
    public void Aggregate_reports_no_percentage_without_a_declared_ceiling_and_one_with()
    {
        var entries = new[] { Row("codex", Now.AddHours(-1), tokensIn: 250, tokensOut: 250) };

        var withoutCeiling = CodexUsageSource.Aggregate(entries, ceiling: null, Now);
        var fiveHourNoCeiling = Assert.Single(withoutCeiling!.Windows, w => w.Name == CodexUsageSource.FiveHourWindowName);
        Assert.Null(fiveHourNoCeiling.PercentUsed);
        Assert.Contains("no plan ceiling declared", fiveHourNoCeiling.RawLine, StringComparison.Ordinal);

        // Same rows, same clock -- only the operator's declaration differs, so the polarity is the
        // ceiling's and nothing else's.
        var withCeiling = CodexUsageSource.Aggregate(
            entries,
            new CodexPlanCeilingSettings { FiveHourTokens = 2_000, WeeklyTokens = 10_000 },
            Now);
        var fiveHourWithCeiling = Assert.Single(withCeiling!.Windows, w => w.Name == CodexUsageSource.FiveHourWindowName);
        var weeklyWithCeiling = Assert.Single(withCeiling.Windows, w => w.Name == CodexUsageSource.WeeklyWindowName);
        Assert.Equal(25, fiveHourWithCeiling.PercentUsed);
        Assert.Equal(5, weeklyWithCeiling.PercentUsed);
    }

    [Fact]
    public void Aggregate_treats_a_non_positive_declared_ceiling_as_absent()
    {
        var snapshot = CodexUsageSource.Aggregate(
            [Row("codex", Now.AddHours(-1), tokensIn: 500)],
            new CodexPlanCeilingSettings { FiveHourTokens = 0, WeeklyTokens = -1 },
            Now);

        Assert.All(snapshot!.Windows, w => Assert.Null(w.PercentUsed));
    }

    [Fact]
    public void Aggregate_caps_a_burn_past_the_declared_ceiling_at_one_hundred_percent()
    {
        var snapshot = CodexUsageSource.Aggregate(
            [Row("codex", Now.AddHours(-1), tokensIn: 10_000)],
            new CodexPlanCeilingSettings { FiveHourTokens = 100, WeeklyTokens = 100 },
            Now);

        Assert.All(snapshot!.Windows, w => Assert.Equal(100, w.PercentUsed));
    }

    [Fact]
    public void Aggregate_returns_null_when_no_codex_row_has_ever_been_recorded()
    {
        // Null, NOT an empty snapshot -- CodexUsageSource.Aggregate's own doc comment has why the two
        // are different things and what rests on the distinction.
        Assert.Null(CodexUsageSource.Aggregate(
            [Row("claude", Now.AddMinutes(-5), tokensIn: 10), Row("agy", Now.AddMinutes(-5), tokensIn: 10)],
            ceiling: null,
            Now));
    }

    [Fact]
    public void Aggregate_harvests_zero_for_a_window_a_seen_codex_vendor_has_no_recent_rows_in()
    {
        // The polarity of the arm above: codex HAS been seen, just not lately. That is a real harvest
        // reporting zero, not an absence -- collapsing the two would make an idle vendor disappear from
        // the glass rather than reading as idle.
        var snapshot = CodexUsageSource.Aggregate([Row("codex", Now.AddDays(-30), tokensIn: 1_000)], ceiling: null, Now);

        Assert.NotNull(snapshot);
        // The EXACT clause, anchored on "derived: " -- a bare Contains("0 billed tokens") is satisfied
        // by "1,000 billed tokens" and so could not fail if the 30-day-old row leaked into the window.
        Assert.All(
            snapshot.Windows,
            w => Assert.Contains(
                "derived: 0 billed tokens across 0 settled codex executions",
                w.RawLine,
                StringComparison.Ordinal));
    }

    /// <summary>
    /// #1926 review. An undatable row must not by itself turn the harvest into a measured zero across
    /// both windows — that would report "codex burned nothing in the last 5 hours" on evidence saying
    /// only "codex ran, at an unknown time". Null is the honest answer, the same as never having seen
    /// codex at all; the guard's own comment in <see cref="CodexUsageSource"/> has the reasoning.
    /// </summary>
    [Fact]
    public void Aggregate_does_not_treat_an_undatable_codex_row_as_a_measured_zero()
    {
        Assert.Null(CodexUsageSource.Aggregate([Undated("codex", tokensIn: 500)], ceiling: null, Now));
    }

    /// <summary>
    /// The control arm one condition away from the one above: a DATABLE codex row that reported none of
    /// the three billed dimensions is still evidence codex ran, so it harvests — reporting zero tokens
    /// across zero executions rather than vanishing. Without this pair, "undatable returns null" could
    /// equally mean the whole tokenless path returns null.
    /// </summary>
    [Fact]
    public void Aggregate_harvests_zero_for_a_datable_codex_row_that_reported_no_tokens()
    {
        var snapshot = CodexUsageSource.Aggregate([Row("codex", Now.AddMinutes(-5))], ceiling: null, Now);

        Assert.NotNull(snapshot);
        Assert.All(
            snapshot.Windows,
            w => Assert.Contains(
                "derived: 0 billed tokens across 0 settled codex executions",
                w.RawLine,
                StringComparison.Ordinal));
    }

    /// <summary>
    /// The rolling window is NON-monotonic, which is the whole reason no burn ring is kept for a derived
    /// snapshot (<c>VendorUsageBurn.Advance</c>'s first rule, spec/baton.md §6). This is the arm that
    /// feeds a row rolling off the back: same ledger, same ceiling, two harvest instants five hours
    /// apart, and the five-hour percentage FALLS with no reset having occurred. The weekly window is the
    /// control — it still holds the row at both instants, so the fall is the five-hour lookback's edge
    /// and not the aggregation losing rows outright.
    /// </summary>
    [Fact]
    public void Aggregate_lets_the_five_hour_percentage_fall_as_a_row_rolls_off_the_back()
    {
        var entries = new[] { Row("codex", Now.AddHours(-1), tokensIn: 500) };
        var ceiling = new CodexPlanCeilingSettings { FiveHourTokens = 1_000, WeeklyTokens = 1_000 };

        var earlier = CodexUsageSource.Aggregate(entries, ceiling, Now);
        var later = CodexUsageSource.Aggregate(entries, ceiling, Now.AddHours(5));

        Assert.Equal(50, Assert.Single(earlier!.Windows, w => w.Name == CodexUsageSource.FiveHourWindowName).PercentUsed);
        Assert.Equal(0, Assert.Single(later!.Windows, w => w.Name == CodexUsageSource.FiveHourWindowName).PercentUsed);
        Assert.Equal(50, Assert.Single(earlier.Windows, w => w.Name == CodexUsageSource.WeeklyWindowName).PercentUsed);
        Assert.Equal(50, Assert.Single(later.Windows, w => w.Name == CodexUsageSource.WeeklyWindowName).PercentUsed);
    }

    [Fact]
    public void Aggregate_never_invents_a_reset_instant_for_a_rolling_window()
    {
        var snapshot = CodexUsageSource.Aggregate([Row("codex", Now.AddHours(-1), tokensIn: 1)], ceiling: null, Now);

        Assert.All(snapshot!.Windows, w => Assert.Null(w.ResetsAt));
        Assert.Equal(CodexUsageSource.DerivedCaveat, snapshot.Caveat);
    }

    [Fact]
    public async Task ReadAsync_returns_null_when_no_ledger_file_exists()
    {
        var source = new CodexUsageSource(
            Path.Combine(_tempDirectory, "absent.jsonl"),
            _ => Task.FromResult(new DaemonSettings()));

        Assert.Null(await source.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadAsync_aggregates_a_real_ledger_file_written_by_the_stores_own_writer()
    {
        var ledgerPath = Path.Combine(_tempDirectory, "quota-ledger.jsonl");
        var cancellationToken = TestContext.Current.CancellationToken;
        await QuotaLedgerStore.AppendAsync(
            [
                Row("codex", DateTimeOffset.UtcNow.AddMinutes(-5), tokensIn: 400, tokensOut: 100),
                Row("claude", DateTimeOffset.UtcNow.AddMinutes(-5), tokensIn: 999_999),
            ],
            ledgerPath,
            cancellationToken);

        var source = new CodexUsageSource(
            ledgerPath,
            _ => Task.FromResult(new DaemonSettings { CodexPlanCeiling = new CodexPlanCeilingSettings { FiveHourTokens = 1_000 } }));

        var snapshot = await source.ReadAsync(cancellationToken);

        Assert.NotNull(snapshot);
        Assert.Equal(VendorUsageProvenance.Derived, snapshot.Source);
        var fiveHour = Assert.Single(snapshot.Windows, w => w.Name == CodexUsageSource.FiveHourWindowName);
        Assert.Equal(50, fiveHour.PercentUsed);
        // No weekly ceiling was declared, so that window carries the tokens and no percentage -- the
        // two ceilings are independently absent, never borrowed from one another.
        Assert.Null(Assert.Single(snapshot.Windows, w => w.Name == CodexUsageSource.WeeklyWindowName).PercentUsed);
    }
}
