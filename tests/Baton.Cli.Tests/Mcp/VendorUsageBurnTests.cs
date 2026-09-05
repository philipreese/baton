using System.Text.Json;
using Baton.Cli.Daemon;
using Baton.Cli.Mcp;
using Baton.Status;
using Baton.Vendors;
using Xunit;

namespace Baton.Cli.Tests.Mcp;

/// <summary>
/// #1746's two halves: <see cref="VendorUsageBurn.Advance"/>'s ring maintenance on the harvester's
/// write, and <see cref="VendorUsageBurn.Derive"/>'s rate/ETA on the projection's read. Both are pure,
/// so most arms touch no file at all; the two round-trip arms at the end run under a temp
/// <c>HomeOverride</c> scope so they cannot touch the operator's real
/// <c>~/.baton/fleet-glass/usage.*.json</c>.
/// </summary>
public sealed class VendorUsageBurnTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly string _tempHome;
    private readonly IDisposable _scope;

    public VendorUsageBurnTests()
    {
        _tempHome = Path.Combine(Path.GetTempPath(), $"baton-burn-test-home-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempHome);
        _scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = _tempHome });
    }

    public void Dispose()
    {
        _scope.Dispose();
        if (Directory.Exists(_tempHome))
        {
            DirectoryCleanup.DeleteRecursively(_tempHome);
        }
    }

    private static VendorUsageSnapshot Snapshot(DateTimeOffset at, params (string Name, int? Percent)[] windows) =>
        new("claude", at, Caveat: null,
            [.. windows.Select(w => new VendorUsageWindow(w.Name, w.Percent, null, $"{w.Name}: {w.Percent}% used"))]);

    private static IReadOnlyList<VendorUsageSample> RingFor(
        IReadOnlyDictionary<string, IReadOnlyList<VendorUsageSample>> rings, string name)
    {
        Assert.True(rings.TryGetValue(name, out var ring), $"expected a ring for window '{name}'");
        return ring!;
    }

    /// <summary>
    /// #1926 review. A <see cref="VendorUsageProvenance.Derived"/> snapshot's percentage is a rolling
    /// lookback, not a monotonic counter, so no ring is kept for it and any ring it already had is
    /// dropped — otherwise a fall caused by a row aging off the back of the window would be read as a
    /// window reset and a burn rate published off a boundary that never happened. The control is the
    /// second half: the SAME two readings under <see cref="VendorUsageProvenance.Vendor"/> do build a
    /// ring, so the emptiness is the provenance's doing and nothing else's.
    /// </summary>
    [Fact]
    public void Advance_keeps_no_ring_for_a_derived_snapshot_and_drops_one_it_already_had()
    {
        VendorUsageWindow[] windows = [new("rolling 5h (derived)", 50, null, "derived: 500 billed tokens")];

        var vendorRings = VendorUsageBurn.Advance(
            null, new VendorUsageSnapshot("codex", T0, null, windows, VendorUsageProvenance.Vendor));
        var vendorRings2 = VendorUsageBurn.Advance(
            vendorRings, new VendorUsageSnapshot("codex", T0.AddHours(1), null, windows, VendorUsageProvenance.Vendor));
        Assert.Equal(2, RingFor(vendorRings2, "rolling 5h (derived)").Count);

        // Same windows, same instants, same prior ring -- only the provenance differs.
        var derivedRings = VendorUsageBurn.Advance(
            vendorRings, new VendorUsageSnapshot("codex", T0.AddHours(1), null, windows, VendorUsageProvenance.Derived));
        Assert.Empty(derivedRings);

        // And the projection therefore publishes no rate and no ETA for that window.
        var (rate, eta) = VendorUsageBurn.Derive(
            derivedRings.GetValueOrDefault("rolling 5h (derived)"), percentUsed: 50);
        Assert.Null(rate);
        Assert.Null(eta);
    }

    [Fact]
    public void Advance_keeps_only_the_last_RingCapacity_harvests()
    {
        IReadOnlyDictionary<string, IReadOnlyList<VendorUsageSample>>? rings = null;
        var harvests = VendorUsageBurn.RingCapacity + 3;
        for (var i = 0; i < harvests; i++)
        {
            rings = VendorUsageBurn.Advance(rings, Snapshot(T0.AddMinutes(15 * i), ("session", i)));
        }

        var ring = RingFor(rings!, "session");
        Assert.Equal(VendorUsageBurn.RingCapacity, ring.Count);

        // Bounded from the FRONT: the oldest three readings are the ones dropped, and the newest is
        // still the last harvest -- a ring trimmed from the wrong end would also be "bounded at N".
        Assert.Equal(harvests - VendorUsageBurn.RingCapacity, ring[0].PercentUsed);
        Assert.Equal(harvests - 1, ring[^1].PercentUsed);
    }

    [Fact]
    public void Advance_drops_samples_older_than_MaxLookBack_so_an_idle_gap_is_not_averaged_into_the_rate()
    {
        // #1891 review; the why is on VendorUsageBurn.MaxLookBack. Three quick samples, a gap longer
        // than the bound, one more: only the post-gap sample survives, so the rate is absent until a
        // second recent sample lands rather than reading as "consumed 30 points over six hours".
        var rings = VendorUsageBurn.Advance(null, Snapshot(T0, ("session", 10)));
        rings = VendorUsageBurn.Advance(rings, Snapshot(T0.AddMinutes(15), ("session", 20)));
        rings = VendorUsageBurn.Advance(rings, Snapshot(T0.AddMinutes(30), ("session", 30)));
        var afterGap = T0.AddMinutes(30) + VendorUsageBurn.MaxLookBack + TimeSpan.FromMinutes(1);
        rings = VendorUsageBurn.Advance(rings, Snapshot(afterGap, ("session", 40)));

        var ring = RingFor(rings, "session");
        Assert.Single(ring);
        Assert.Equal(40, ring[0].PercentUsed);

        // A sample exactly at the horizon is kept (the bound is "older than", not "at least as old").
        rings = VendorUsageBurn.Advance(rings, Snapshot(afterGap + VendorUsageBurn.MaxLookBack, ("session", 50)));
        Assert.Equal(2, RingFor(rings, "session").Count);
    }

    [Fact]
    public void Derive_two_samples_yields_rate_and_minutes_to_exhaustion()
    {
        // 10% -> 20% over two hours = 5 percentage points/hour; 80 points left = 16 hours = 960 min.
        var ring = new List<VendorUsageSample>
        {
            new(T0, 10),
            new(T0.AddHours(2), 20),
        };

        var (rate, minutes) = VendorUsageBurn.Derive(ring, percentUsed: 20);

        Assert.Equal(5d, rate);
        Assert.Equal(960d, minutes);
    }

    [Fact]
    public void Derive_one_sample_has_no_rate_at_all_and_a_second_sample_supplies_one()
    {
        // Polarity in both directions: the SAME window with one reading must report no rate (never a
        // zero, which reads as "idle"), and with a second reading must report one -- otherwise the
        // absent arm could be explained by the derivation never working at all.
        var oneSample = new List<VendorUsageSample> { new(T0, 10) };
        var (rate, minutes) = VendorUsageBurn.Derive(oneSample, percentUsed: 10);
        Assert.Null(rate);
        Assert.Null(minutes);

        var twoSamples = new List<VendorUsageSample> { new(T0, 10), new(T0.AddHours(1), 14) };
        var (rate2, minutes2) = VendorUsageBurn.Derive(twoSamples, percentUsed: 14);
        Assert.Equal(4d, rate2);
        Assert.NotNull(minutes2);
    }

    [Fact]
    public void Derive_flat_ring_reports_a_zero_rate_with_no_exhaustion_estimate()
    {
        // Distinct from the one-sample arm above: here the rate IS known and is zero, so it is
        // reported as zero -- only the exhaustion estimate is absent, because nothing is burning.
        var ring = new List<VendorUsageSample> { new(T0, 30), new(T0.AddHours(1), 30) };

        var (rate, minutes) = VendorUsageBurn.Derive(ring, percentUsed: 30);

        Assert.Equal(0d, rate);
        Assert.Null(minutes);
    }

    [Fact]
    public void Derive_samples_at_one_instant_is_absent_rather_than_infinite()
    {
        // Non-finite would not merely be a wrong number: System.Text.Json refuses Infinity/NaN, which
        // would take the whole fleet_status response down.
        var ring = new List<VendorUsageSample> { new(T0, 10), new(T0, 40) };

        var (rate, minutes) = VendorUsageBurn.Derive(ring, percentUsed: 40);

        Assert.Null(rate);
        Assert.Null(minutes);
    }

    [Fact]
    public void Derive_absent_percent_keeps_the_rate_but_drops_the_exhaustion_estimate()
    {
        var ring = new List<VendorUsageSample> { new(T0, 10), new(T0.AddHours(1), 20) };

        var (rate, minutes) = VendorUsageBurn.Derive(ring, percentUsed: null);

        Assert.Equal(10d, rate);
        Assert.Null(minutes);
    }

    [Fact]
    public void Advance_window_rollover_clears_the_ring_and_no_rate_returns_until_two_post_reset_samples()
    {
        var rings = VendorUsageBurn.Advance(null, Snapshot(T0, ("session", 40)));
        rings = VendorUsageBurn.Advance(rings, Snapshot(T0.AddHours(1), ("session", 60)));
        Assert.NotNull(VendorUsageBurn.Derive(RingFor(rings, "session"), 60).RatePctPerHour);

        // The window rolled: percent DROPS. The drop reading is post-reset sample #1, so the ring
        // holds exactly it and no rate survives across the reset boundary.
        rings = VendorUsageBurn.Advance(rings, Snapshot(T0.AddHours(2), ("session", 3)));
        var postReset = RingFor(rings, "session");
        Assert.Equal(3, Assert.Single(postReset).PercentUsed);
        Assert.Null(VendorUsageBurn.Derive(postReset, 3).RatePctPerHour);

        // Second post-reset harvest: a rate again, computed only from post-reset readings (3 -> 9
        // over an hour), never from the pre-reset 60.
        rings = VendorUsageBurn.Advance(rings, Snapshot(T0.AddHours(3), ("session", 9)));
        Assert.Equal(6d, VendorUsageBurn.Derive(RingFor(rings, "session"), 9).RatePctPerHour);
    }

    [Fact]
    public void Advance_unparsed_percent_adds_no_sample_but_keeps_the_history()
    {
        var rings = VendorUsageBurn.Advance(null, Snapshot(T0, ("session", 10)));
        rings = VendorUsageBurn.Advance(rings, Snapshot(T0.AddHours(1), ("session", null)));

        var ring = RingFor(rings, "session");
        Assert.Equal(10, Assert.Single(ring).PercentUsed);
    }

    [Fact]
    public void Advance_drops_a_window_that_is_no_longer_reported_and_one_whose_name_is_ambiguous()
    {
        var rings = VendorUsageBurn.Advance(null, Snapshot(T0, ("session", 10), ("week", 20)));
        Assert.Equal(2, rings.Count);

        // "week" gone from this harvest -> its ring goes with it (bounded file); "session" reported
        // twice under one name -> no ring at all, since merging two rows would fabricate a rate.
        rings = VendorUsageBurn.Advance(rings, Snapshot(T0.AddHours(1), ("session", 12), ("session", 44)));
        Assert.Empty(rings);
    }

    [Fact]
    public void Persisted_ring_round_trips_into_the_wire_shape_rate_and_eta()
    {
        VendorUsageHarvester.Persist("claude", Snapshot(T0, ("session", 10)));
        VendorUsageHarvester.Persist("claude", Snapshot(T0.AddHours(2), ("session", 30)));

        var window = Assert.Single(Assert.Single(
            VendorUsageProjectionReader.ReadAll(new Dictionary<string, int>(StringComparer.Ordinal))!).Windows);

        Assert.Equal(10d, window.RatePctPerHour);        // 20 points over 2h
        Assert.Equal(420d, window.MinutesToExhaustion);  // 70 points left at 10/h

        // Both fields are omitted rather than emitted null, like every other optional field on this
        // wire shape -- asserted on a window that HAS them, so the JSON keys themselves are pinned.
        using var wire = JsonDocument.Parse(JsonSerializer.Serialize(window, FleetStatusTool.SerializerOptions));
        Assert.Equal(10d, wire.RootElement.GetProperty("ratePctPerHour").GetDouble());
        Assert.Equal(420d, wire.RootElement.GetProperty("minutesToExhaustion").GetDouble());
    }

    [Fact]
    public void A_pre_1746_snapshot_file_still_reads_with_the_burn_fields_simply_absent()
    {
        var path = BatonPaths.VendorUsageSnapshotFile("claude");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            // #1869's exact on-disk shape: the four snapshot fields, no Rings key.
            File.WriteAllText(path, JsonSerializer.Serialize(Snapshot(T0, ("session", 10))));

            var window = Assert.Single(Assert.Single(
                VendorUsageProjectionReader.ReadAll(new Dictionary<string, int>(StringComparer.Ordinal))!).Windows);

            Assert.Equal(10, window.PercentUsed);
            Assert.Null(window.RatePctPerHour);
            Assert.Null(window.MinutesToExhaustion);

            var json = JsonSerializer.Serialize(window, FleetStatusTool.SerializerOptions);
            Assert.DoesNotContain("ratePctPerHour", json);
            Assert.DoesNotContain("minutesToExhaustion", json);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }
}
