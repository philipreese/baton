using System.Text.Json;
using Baton.VendorProbe;

namespace Baton.VendorProbe.Tests;

/// <summary>
/// #1487: the grace window that stops a self-updated vendor CLI hard-failing <c>gates</c> the instant
/// it is noticed. Pure-function tests against a throwaway bookkeeping file — no <c>Cli.Invoke</c>, no
/// vendor process, safe in CI, and each test gets its own temp path so they cannot interfere.
/// </summary>
public sealed class DriftGraceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("aer-drift-grace-tests-").FullName;

    private string BookkeepingPath => Path.Combine(_dir, "drift.local.json");

    public void Dispose() => DirectoryCleanup.DeleteRecursively(_dir);

    [Fact]
    public void No_drift_with_no_bookkeeping_is_a_clean_pass()
    {
        var result = DriftGrace.Evaluate(BookkeepingPath, driftDetected: false, DateTimeOffset.Now);

        Assert.Equal(DriftGrace.Verdict.NoDrift, result.Verdict);
        Assert.False(result.Fatal);
        Assert.False(File.Exists(BookkeepingPath));
    }

    [Fact]
    public void Fresh_drift_records_the_instant_warns_and_passes()
    {
        var now = DateTimeOffset.Now;

        var result = DriftGrace.Evaluate(BookkeepingPath, driftDetected: true, now);

        Assert.Equal(DriftGrace.Verdict.FreshWarn, result.Verdict);
        Assert.False(result.Fatal);
        Assert.Contains("grace window", result.Message);
        Assert.True(File.Exists(BookkeepingPath), "first detection must record a bookkeeping file");

        var recorded = JsonSerializer.Deserialize<DriftGrace.Bookkeeping>(File.ReadAllText(BookkeepingPath));
        Assert.NotNull(recorded);
        Assert.Equal(now, recorded!.FirstDetectedAt);
    }

    [Fact]
    public void Drift_still_within_the_window_on_a_later_run_keeps_the_original_instant_and_still_passes()
    {
        var firstSeen = DateTimeOffset.Now.AddDays(-3);
        File.WriteAllText(
            BookkeepingPath,
            JsonSerializer.Serialize(new DriftGrace.Bookkeeping(firstSeen)));

        var result = DriftGrace.Evaluate(BookkeepingPath, driftDetected: true, DateTimeOffset.Now);

        Assert.Equal(DriftGrace.Verdict.FreshWarn, result.Verdict);
        Assert.False(result.Fatal);

        // The instant must not have moved -- a re-run within the window is not a second "first seen".
        var recorded = JsonSerializer.Deserialize<DriftGrace.Bookkeeping>(File.ReadAllText(BookkeepingPath));
        Assert.Equal(firstSeen, recorded!.FirstDetectedAt);
    }

    [Fact]
    public void Drift_past_the_grace_window_hard_fails()
    {
        var firstSeen = DateTimeOffset.Now - DriftGrace.Window - TimeSpan.FromHours(1);
        File.WriteAllText(
            BookkeepingPath,
            JsonSerializer.Serialize(new DriftGrace.Bookkeeping(firstSeen)));

        var result = DriftGrace.Evaluate(BookkeepingPath, driftDetected: true, DateTimeOffset.Now);

        Assert.Equal(DriftGrace.Verdict.StaleFail, result.Verdict);
        Assert.True(result.Fatal);
        Assert.Contains("grace window", result.Message);
    }

    [Fact]
    public void Corrupt_bookkeeping_fails_closed_rather_than_reopening_the_window()
    {
        File.WriteAllText(BookkeepingPath, "{ not valid json");

        var result = DriftGrace.Evaluate(BookkeepingPath, driftDetected: true, DateTimeOffset.Now);

        Assert.Equal(DriftGrace.Verdict.CorruptFail, result.Verdict);
        Assert.True(result.Fatal);

        // Failing closed also means it must not have overwritten the corrupt file with a fresh
        // "detected now" record -- that would silently convert unreadable bookkeeping into a clean
        // restart of the clock, which is exactly the failure mode this test guards against.
        Assert.Equal("{ not valid json", File.ReadAllText(BookkeepingPath));
    }

    [Fact]
    public void Empty_bookkeeping_file_also_fails_closed()
    {
        File.WriteAllText(BookkeepingPath, string.Empty);

        var result = DriftGrace.Evaluate(BookkeepingPath, driftDetected: true, DateTimeOffset.Now);

        Assert.Equal(DriftGrace.Verdict.CorruptFail, result.Verdict);
        Assert.True(result.Fatal);
    }

    [Fact]
    public void Cleared_drift_deletes_the_bookkeeping_file()
    {
        File.WriteAllText(
            BookkeepingPath,
            JsonSerializer.Serialize(new DriftGrace.Bookkeeping(DateTimeOffset.Now.AddDays(-2))));

        var result = DriftGrace.Evaluate(BookkeepingPath, driftDetected: false, DateTimeOffset.Now);

        Assert.Equal(DriftGrace.Verdict.NoDrift, result.Verdict);
        Assert.False(result.Fatal);
        Assert.False(File.Exists(BookkeepingPath), "a re-pinned probe must clear the recorded drift instant");
    }

    [Fact]
    public void Repin_with_stale_drift_file_clears_file_and_next_drift_starts_fresh_window()
    {
        var staleInstant = DateTimeOffset.Now - DriftGrace.Window - TimeSpan.FromDays(2);
        DriftGrace.Evaluate(BookkeepingPath, true, staleInstant,
            [new("claude", Staleness.Verdict.Drifted, "old", "new", staleInstant.AddDays(-1))]);

        // Re-pin via Staleness.Write, passing the drift bookkeeping path
        var lockPath = Path.Combine(_dir, "lock.json");
        var findings = new List<Finding>
        {
            Finding.Seen("stream-json", "claude", "observed", ["--output-format"], "detail", "2.1.263")
        };
        Staleness.Write(lockPath, findings, BookkeepingPath);

        Assert.False(File.Exists(BookkeepingPath), "re-pinning must delete the drift bookkeeping file");

        // Next drift occurs later
        var nextDriftTime = DateTimeOffset.Now;
        var result = DriftGrace.Evaluate(BookkeepingPath, driftDetected: true, nextDriftTime);

        Assert.Equal(DriftGrace.Verdict.FreshWarn, result.Verdict);
        Assert.False(result.Fatal);
        Assert.True(File.Exists(BookkeepingPath));

        var recorded = JsonSerializer.Deserialize<DriftGrace.Bookkeeping>(File.ReadAllText(BookkeepingPath));
        Assert.NotNull(recorded);
        Assert.Equal(nextDriftTime, recorded!.FirstDetectedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Stale_drift_file_is_not_cleared_by_another_vendors_repin(bool legacy)
    {
        var staleSeen = DateTimeOffset.Now - DriftGrace.Window - TimeSpan.FromDays(2);
        File.WriteAllText(
            BookkeepingPath,
            JsonSerializer.Serialize(new DriftGrace.Bookkeeping(staleSeen,
                legacy ? null : new Dictionary<string, DateTimeOffset> { ["claude"] = staleSeen })));

        // agy was re-pinned 2 days ago and is now Current (ok)
        var repinnedAt = DateTimeOffset.Now.AddDays(-2);
        var statuses = new List<Staleness.Status>
        {
            new("claude", Staleness.Verdict.Drifted, "2.1.258", "2.1.263", staleSeen.AddDays(-1)),
            new("agy", Staleness.Verdict.Current, "1.1.27", "1.1.27", repinnedAt),
        };

        var now = DateTimeOffset.Now;
        var result = DriftGrace.Evaluate(BookkeepingPath, driftDetected: true, now, statuses);

        Assert.Equal(DriftGrace.Verdict.StaleFail, result.Verdict);
        Assert.True(result.Fatal);
        Assert.Contains("past the 7-day grace window", result.Message);

        // agy's re-pin says nothing about claude's unresolved drift.
        var recorded = JsonSerializer.Deserialize<DriftGrace.Bookkeeping>(File.ReadAllText(BookkeepingPath));
        Assert.NotNull(recorded);
        Assert.Equal(staleSeen, recorded!.FirstDetectedAt);
    }

    [Fact]
    public void Drift_detected_after_ok_vendor_repin_retains_clock_and_fails_past_window()
    {
        var repinnedAt = DateTimeOffset.Now.AddDays(-10);
        // Drift was first detected 8 days ago (AFTER the re-pin at -10 days, but > 7 days ago)
        var firstSeen = DateTimeOffset.Now - DriftGrace.Window - TimeSpan.FromHours(1);
        File.WriteAllText(
            BookkeepingPath,
            JsonSerializer.Serialize(new DriftGrace.Bookkeeping(firstSeen)));

        var statuses = new List<Staleness.Status>
        {
            new("claude", Staleness.Verdict.Drifted, "2.1.258", "2.1.263", repinnedAt),
            new("agy", Staleness.Verdict.Current, "1.1.27", "1.1.27", repinnedAt),
        };

        var result = DriftGrace.Evaluate(BookkeepingPath, driftDetected: true, DateTimeOffset.Now, statuses);

        Assert.Equal(DriftGrace.Verdict.StaleFail, result.Verdict);
        Assert.True(result.Fatal);
    }

    [Fact]
    public void Drift_with_no_repinned_vendors_retains_drift_clock()
    {
        var firstSeen = DateTimeOffset.Now - DriftGrace.Window - TimeSpan.FromHours(1);
        File.WriteAllText(
            BookkeepingPath,
            JsonSerializer.Serialize(new DriftGrace.Bookkeeping(firstSeen)));

        var statuses = new List<Staleness.Status>
        {
            new("claude", Staleness.Verdict.Drifted, "2.1.258", "2.1.263", firstSeen.AddDays(-1)),
            new("agy", Staleness.Verdict.Drifted, "1.1.25", "1.1.27", firstSeen.AddDays(-1)),
        };

        var result = DriftGrace.Evaluate(BookkeepingPath, driftDetected: true, DateTimeOffset.Now, statuses);

        Assert.Equal(DriftGrace.Verdict.StaleFail, result.Verdict);
        Assert.True(result.Fatal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Narrowed_probe_preserves_another_vendors_clock(bool legacy)
    {
        var day0 = DateTimeOffset.Now.AddDays(-10);
        Staleness.Status[] statuses =
        [
            new("claude", Staleness.Verdict.Drifted, "old", "new", day0.AddDays(-1)),
            new("agy", Staleness.Verdict.Current, "1", "1", day0.AddDays(-1)),
        ];
        if (legacy)
        {
            File.WriteAllText(BookkeepingPath, JsonSerializer.Serialize(new DriftGrace.Bookkeeping(day0)));
        }
        else
        {
            Assert.Equal(DriftGrace.Verdict.FreshWarn,
                DriftGrace.Evaluate(BookkeepingPath, true, day0, statuses).Verdict);
        }

        var lockPath = Path.Combine(_dir, "lock.json");
        for (var day = 5; day <= 8; day++)
        {
            Staleness.Write(lockPath,
                [Finding.Seen("stream-json", "agy", "observed", ["--output-format"], "detail", "1")],
                BookkeepingPath);
            Assert.True(File.Exists(BookkeepingPath));
            var result = DriftGrace.Evaluate(BookkeepingPath, true, day0.AddDays(day), statuses);
            Assert.Equal(day <= 7 ? DriftGrace.Verdict.FreshWarn : DriftGrace.Verdict.StaleFail, result.Verdict);
            var recorded = JsonSerializer.Deserialize<DriftGrace.Bookkeeping>(File.ReadAllText(BookkeepingPath));
            Assert.Equal(day0, recorded!.FirstDetectedAt);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Narrowed_clean_check_preserves_unchecked_drift(bool uninspectable)
    {
        var day0 = DateTimeOffset.Now.AddDays(-10);
        Staleness.Status claude = new("claude", Staleness.Verdict.Drifted, "old", "new", day0.AddDays(-1));
        DriftGrace.Evaluate(BookkeepingPath, true, day0, [claude]);

        var result = DriftGrace.Evaluate(BookkeepingPath, false, day0.AddDays(5),
            uninspectable
                ? [new("claude", Staleness.Verdict.Uninspectable, "old", null, day0.AddDays(-1)),
                    new("agy", Staleness.Verdict.Current, "1", "1", day0.AddDays(4))]
                : [new("agy", Staleness.Verdict.Current, "1", "1", day0.AddDays(4))]);

        Assert.Equal(DriftGrace.Verdict.NoDrift, result.Verdict);
        Assert.True(File.Exists(BookkeepingPath));
        Assert.Equal(DriftGrace.Verdict.StaleFail,
            DriftGrace.Evaluate(BookkeepingPath, true, day0.AddDays(8), [claude]).Verdict);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Own_repin_recovers_surviving_bookkeeping_even_after_another_update(bool legacy)
    {
        var day0 = DateTimeOffset.Now.AddDays(-10);
        File.WriteAllText(BookkeepingPath, JsonSerializer.Serialize(new DriftGrace.Bookkeeping(day0,
            legacy ? null : new Dictionary<string, DateTimeOffset> { ["claude"] = day0 })));
        Staleness.Status[] statuses =
        [
            new("claude", Staleness.Verdict.Drifted, "repinned", "newer", day0.AddDays(5)),
            new("agy", Staleness.Verdict.Current, "1", "1", day0.AddDays(-1)),
            new("codex", Staleness.Verdict.Current, "1", "1", day0.AddDays(-1)),
        ];
        var now = day0.AddDays(8);

        Assert.Equal(DriftGrace.Verdict.FreshWarn,
            DriftGrace.Evaluate(BookkeepingPath, true, now, statuses).Verdict);
        var recorded = JsonSerializer.Deserialize<DriftGrace.Bookkeeping>(File.ReadAllText(BookkeepingPath));
        Assert.Equal(now, recorded!.Vendors!["claude"]);
        Assert.Equal(DriftGrace.Verdict.StaleFail,
            DriftGrace.Evaluate(BookkeepingPath, true, now.AddDays(8), statuses).Verdict);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Clearing_one_of_two_drifted_vendors_preserves_the_others_independent_instant(bool probe)
    {
        var day0 = DateTimeOffset.Now.AddDays(-10);
        Staleness.Status claude = new("claude", Staleness.Verdict.Drifted, "old", "new", day0.AddDays(-1));
        Staleness.Status agy = new("agy", Staleness.Verdict.Drifted, "old", "new", day0.AddDays(-1));
        DriftGrace.Evaluate(BookkeepingPath, true, day0, [claude]);
        DriftGrace.Evaluate(BookkeepingPath, true, day0.AddDays(3), [claude, agy]);

        if (probe)
        {
            Staleness.Write(Path.Combine(_dir, "lock.json"),
                [Finding.Seen("stream-json", "claude", "observed", ["--output-format"], "detail", "new")],
                BookkeepingPath);
        }
        else
        {
            DriftGrace.Evaluate(BookkeepingPath, true, day0.AddDays(5),
                [claude with { Verdict = Staleness.Verdict.Current }, agy]);
        }

        var recorded = JsonSerializer.Deserialize<DriftGrace.Bookkeeping>(File.ReadAllText(BookkeepingPath));
        Assert.Equal(day0.AddDays(3), recorded!.FirstDetectedAt);
        Assert.Single(recorded.Vendors!);
        Assert.Equal(day0.AddDays(3), recorded.Vendors!["agy"]);
        Assert.Equal(DriftGrace.Verdict.FreshWarn,
            DriftGrace.Evaluate(BookkeepingPath, true, day0.AddDays(8), [agy]).Verdict);
        Assert.Equal(DriftGrace.Verdict.StaleFail,
            DriftGrace.Evaluate(BookkeepingPath, true, day0.AddDays(11), [agy]).Verdict);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Probe_without_a_recorded_version_preserves_drift(bool empty)
    {
        var day0 = DateTimeOffset.Now.AddDays(-10);
        DriftGrace.Evaluate(BookkeepingPath, true, day0,
            [new("claude", Staleness.Verdict.NeverProbed, null, "1", null)]);
        var before = File.ReadAllText(BookkeepingPath);

        Staleness.Write(Path.Combine(_dir, "lock.json"),
            empty ? [] : [Finding.Seen("stream-json", "claude", "observed", [], "detail", null)],
            BookkeepingPath);

        Assert.Equal(before, File.ReadAllText(BookkeepingPath));
    }

    [Fact]
    public void Full_repin_clears_legacy_bookkeeping()
    {
        File.WriteAllText(BookkeepingPath,
            JsonSerializer.Serialize(new DriftGrace.Bookkeeping(DateTimeOffset.Now.AddDays(-10))));

        Staleness.Write(Path.Combine(_dir, "lock.json"),
            Program.SupportedVendors.Select(v => Finding.Seen("stream-json", v, "observed", [], "detail", "1")).ToList(),
            BookkeepingPath);

        Assert.False(File.Exists(BookkeepingPath));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{ not valid json")]
    public void Partial_repin_does_not_erase_corrupt_bookkeeping(string contents)
    {
        File.WriteAllText(BookkeepingPath, contents);
        Staleness.Write(Path.Combine(_dir, "lock.json"),
            [Finding.Seen("stream-json", "agy", "observed", [], "detail", "1")],
            BookkeepingPath);

        Assert.Equal(contents, File.ReadAllText(BookkeepingPath));
        Assert.Equal(DriftGrace.Verdict.CorruptFail,
            DriftGrace.Evaluate(BookkeepingPath, true, DateTimeOffset.Now,
                [new("claude", Staleness.Verdict.Drifted, "old", "new", DateTimeOffset.Now.AddDays(-20))]).Verdict);
    }
}
