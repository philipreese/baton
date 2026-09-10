using System.Text.Json.Nodes;
using Baton.Vendors;

namespace Baton.Vendors.Tests;

/// <summary>
/// #1904. Parser tests replay the checked-in, sanitized 0.153.2 capture or explicitly label a
/// constructed arm synthetic. No test starts Codex, reads credentials, or spends subscription quota.
/// </summary>
public class CodexUsageSourceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 16, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Measured_fixture_uses_the_multi_bucket_map_without_counting_the_legacy_alias()
    {
        var root = JsonNode.Parse(File.ReadAllText(FixturePath()))!.AsObject();
        Assert.Equal("codex-cli 0.153.2", root["cliVersion"]!.GetValue<string>());

        var snapshot = CodexUsageSource.Parse(root["result"]!.AsObject(), Now);

        Assert.Equal("codex", snapshot.Vendor);
        Assert.Equal(Now, snapshot.HarvestedAt);
        Assert.Equal(VendorUsageProvenance.Vendor, snapshot.Source);
        Assert.Null(snapshot.Caveat);
        Assert.Equal(4, snapshot.Windows.Count);

        var accountWeekly = Assert.Single(snapshot.Windows, window =>
            window.LimitId == CodexUsageSource.AccountLimitId
            && window.WindowDurationMins == CodexUsageSource.WeeklyDurationMins);
        Assert.Equal(48, accountWeekly.PercentUsed);
        Assert.Equal("primary", accountWeekly.WindowKind);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1789436798), accountWeekly.ResetsAt);
        Assert.Contains("10080", accountWeekly.RawLine, StringComparison.Ordinal);
        Assert.DoesNotContain(snapshot.Windows, window =>
            window.LimitId == CodexUsageSource.AccountLimitId
            && window.WindowDurationMins == CodexUsageSource.FiveHourDurationMins);

        var unavailable = Assert.Single(snapshot.Windows, window =>
            window.LimitId == CodexUsageSource.AccountLimitId && window.WindowKind == "secondary");
        Assert.Null(unavailable.PercentUsed);
        Assert.Null(unavailable.WindowDurationMins);
        Assert.Null(unavailable.ResetsAt);
        Assert.Equal("null", unavailable.RawLine);

        Assert.Equal(2, snapshot.Windows.Count(window => window.LimitId == "codex_bengalfox"));
    }

    [Fact]
    public void Measured_primary_is_weekly_not_five_hour_because_duration_defines_the_window()
    {
        var result = JsonNode.Parse(File.ReadAllText(FixturePath()))!["result"]!.AsObject();

        var accountPrimary = Assert.Single(CodexUsageSource.Parse(result, Now).Windows, window =>
            window.LimitId == CodexUsageSource.AccountLimitId && window.WindowKind == "primary");

        Assert.Equal(CodexUsageSource.WeeklyDurationMins, accountPrimary.WindowDurationMins);
        Assert.Contains("7d", accountPrimary.Name, StringComparison.Ordinal);
        Assert.DoesNotContain("5h", accountPrimary.Name, StringComparison.Ordinal);
    }

    /// <summary>
    /// Synthetic compatibility arm: older installed versions may expose only the documented legacy
    /// <c>rateLimits</c> object. It is used only when the multi-bucket map is absent.
    /// </summary>
    [Fact]
    public void Synthetic_legacy_only_shape_remains_readable()
    {
        var result = new JsonObject
        {
            ["rateLimits"] = Bucket(
                "codex",
                primary: Window(17, CodexUsageSource.FiveHourDurationMins, 1_800_000_000),
                secondary: Window(42, CodexUsageSource.WeeklyDurationMins, 1_800_010_000)),
        };

        var snapshot = CodexUsageSource.Parse(result, Now);

        Assert.Equal(2, snapshot.Windows.Count);
        Assert.Equal(17, Assert.Single(snapshot.Windows,
            window => window.WindowDurationMins == CodexUsageSource.FiveHourDurationMins).PercentUsed);
        Assert.Equal(42, Assert.Single(snapshot.Windows,
            window => window.WindowDurationMins == CodexUsageSource.WeeklyDurationMins).PercentUsed);
    }

    /// <summary>
    /// Synthetic precedence arm: the legacy object aliases an entry in the map. When the map is
    /// present it is authoritative even if the alias carries different values.
    /// </summary>
    [Fact]
    public void Synthetic_multi_bucket_shape_never_adds_the_legacy_alias()
    {
        var result = new JsonObject
        {
            ["rateLimits"] = Bucket("codex", Window(99, 300, 1_800_000_000), null),
            ["rateLimitsByLimitId"] = new JsonObject
            {
                ["codex"] = Bucket("codex", Window(12, 300, 1_800_000_000), null),
            },
        };

        var snapshot = CodexUsageSource.Parse(result, Now);

        Assert.Equal(2, snapshot.Windows.Count);
        Assert.Equal(12, Assert.Single(snapshot.Windows, window => window.WindowKind == "primary").PercentUsed);
        Assert.DoesNotContain(snapshot.Windows, window => window.PercentUsed == 99);
    }

    /// <summary>
    /// Synthetic malformed-field arm. A partially malformed vendor window stays present with unknown
    /// values; no percentage, duration, reset, or token allowance is inferred.
    /// </summary>
    [Fact]
    public void Synthetic_invalid_numeric_fields_are_unknown_without_losing_other_windows()
    {
        var result = new JsonObject
        {
            ["rateLimitsByLimitId"] = new JsonObject
            {
                ["codex"] = new JsonObject
                {
                    ["limitId"] = "codex",
                    ["primary"] = new JsonObject
                    {
                        ["usedPercent"] = 101,
                        ["windowDurationMins"] = 0,
                        ["resetsAt"] = long.MaxValue,
                    },
                    ["secondary"] = Window(23, 300, 1_800_000_000),
                },
            },
        };

        var snapshot = CodexUsageSource.Parse(result, Now);
        var invalid = Assert.Single(snapshot.Windows, window => window.WindowKind == "primary");
        Assert.Null(invalid.PercentUsed);
        Assert.Null(invalid.WindowDurationMins);
        Assert.Null(invalid.ResetsAt);
        Assert.Equal(23, Assert.Single(snapshot.Windows, window => window.WindowKind == "secondary").PercentUsed);
    }

    [Fact]
    public void Successful_unrecognized_result_is_an_empty_vendor_snapshot()
    {
        var snapshot = CodexUsageSource.Parse(new JsonObject { ["unrelated"] = true }, Now);

        Assert.Empty(snapshot.Windows);
        Assert.Equal(VendorUsageProvenance.Vendor, snapshot.Source);
    }

    [Fact]
    public async Task ReadAsync_returns_null_when_the_broker_reports_failure()
    {
        var source = new CodexUsageSource(_ => Task.FromResult<JsonObject?>(null), () => Now);

        Assert.Null(await source.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadAsync_stamps_the_harvest_clock_on_a_successful_result()
    {
        var result = new JsonObject
        {
            ["rateLimits"] = Bucket("codex", Window(31, 300, 1_800_000_000), null),
        };
        var source = new CodexUsageSource(_ => Task.FromResult<JsonObject?>(result), () => Now);

        var snapshot = await source.ReadAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(snapshot);
        Assert.Equal(Now, snapshot.HarvestedAt);
        Assert.Equal(VendorUsageProvenance.Vendor, snapshot.Source);
        Assert.Equal(31, snapshot.Windows[0].PercentUsed);
    }

    private static JsonObject Bucket(string limitId, JsonObject? primary, JsonObject? secondary) => new()
    {
        ["limitId"] = limitId,
        ["limitName"] = null,
        ["primary"] = primary,
        ["secondary"] = secondary,
    };

    private static JsonObject Window(int usedPercent, int durationMins, long resetsAt) => new()
    {
        ["usedPercent"] = usedPercent,
        ["windowDurationMins"] = durationMins,
        ["resetsAt"] = resetsAt,
    };

    private static string FixturePath() =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "codex", "codex-app-server-rate-limits-0.153.2.jsonl");
}
