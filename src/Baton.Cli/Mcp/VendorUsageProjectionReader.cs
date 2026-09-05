using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Status;
using Baton.Vendors;

namespace Baton.Cli.Mcp;

/// <summary>
/// Issue #1391's fleet-wide <c>vendors[]</c> wire block — one entry per adapter that has ever been
/// harvested. Read by BOTH <see cref="FleetStatusTool"/> (the live MCP tool) and
/// <c>Baton.Cli.Daemon.FleetProjectionWriter</c> (the daemon-written projection file), each already
/// computing its own room list per call/tick; <see cref="ReadAll"/> takes that room list's derived
/// <paramref name="liveLanesByVendor"/> tally rather than re-deriving it, so this reader touches only
/// the harvested snapshot files (<see cref="BatonPaths.VendorUsageSnapshotFile"/>), never the rooms
/// directory itself.
/// </summary>
public static class VendorUsageProjectionReader
{
    /// <summary>Every adapter tag a snapshot file can exist for — claude/agy since #1391, plus codex
    /// since #1904 (whose snapshot is <see cref="VendorUsageProvenance.Derived"/>, not a vendor
    /// counter). One list, owned by
    /// <see cref="RunwayGate.MeasuredVendors"/> since #1848: the population that has an
    /// <see cref="Baton.Vendors.IVendorUsageSource"/> and the population whose snapshot files exist are
    /// the same population, and a second copy here is how one of them would go stale.</summary>
    private static readonly IReadOnlyList<string> KnownVendors = RunwayGate.MeasuredVendors;

    private static readonly JsonSerializerOptions PersistedSnapshotOptions = new();

    /// <summary>
    /// Reads every vendor's persisted snapshot file that exists and parses cleanly, pairing each with
    /// <paramref name="liveLanesByVendor"/>'s own count (0 when the vendor is absent from that
    /// dictionary). Returns null — never an empty list — when no snapshot file exists yet or none
    /// parses, matching every other optional field's <c>JsonIgnoreCondition.WhenWritingNull</c>
    /// absence convention on this wire shape: a fleet that has never harvested emits no <c>vendors</c>
    /// key at all rather than an empty array.
    /// </summary>
    /// <param name="liveLanesByVendor">Adapter tag to count of that adapter's currently-Running rooms
    /// — the caller's own already-built room list, tallied once and passed in rather than re-scanned
    /// here.</param>
    public static IReadOnlyList<VendorUsageProjectionView>? ReadAll(IReadOnlyDictionary<string, int> liveLanesByVendor)
    {
        List<VendorUsageProjectionView> entries = [];

        foreach (var vendor in KnownVendors)
        {
            var path = BatonPaths.VendorUsageSnapshotFile(vendor);
            if (!File.Exists(path))
            {
                continue;
            }

            PersistedVendorUsage? snapshot;
            try
            {
                var json = File.ReadAllText(path);
                snapshot = JsonSerializer.Deserialize<PersistedVendorUsage>(json, PersistedSnapshotOptions);
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                // Fail open, matching every other per-room read in FleetStatusTool: one unreadable or
                // corrupt snapshot degrades that vendor's own entry, never the whole call.
                continue;
            }

            if (snapshot is null)
            {
                continue;
            }

            var liveLanes = liveLanesByVendor.GetValueOrDefault(vendor);
            var rings = snapshot.Rings;
            var windows = snapshot.Windows
                .Select(w =>
                {
                    // #1746: burn is derived HERE, once, for both callers of this reader -- glass.html
                    // renders these two fields and never recomputes them. Absence rules live on
                    // VendorUsageBurn.Derive and, canonically, in spec/baton.md §6's windows[] table.
                    IReadOnlyList<VendorUsageSample>? ring = null;
                    rings?.TryGetValue(w.Name, out ring);
                    var (ratePctPerHour, minutesToExhaustion) = VendorUsageBurn.Derive(ring, w.PercentUsed);
                    return new VendorUsageWindowView(
                        w.Name, w.PercentUsed, w.ResetsAt, w.RawLine, ratePctPerHour, minutesToExhaustion);
                })
                .ToList();
            entries.Add(new VendorUsageProjectionView(
                vendor, snapshot.HarvestedAt, snapshot.Caveat, windows, liveLanes, snapshot.Source));
        }

        return entries.Count > 0 ? entries : null;
    }

    /// <summary>
    /// Tallies <paramref name="rooms"/> by <see cref="FleetRoomStatusView.Adapter"/> for every room
    /// currently displayed as <c>"Running"</c> — the same reading <c>fleet_status</c>'s own
    /// <c>role</c>/<c>adapter</c> fields already resolve (spec/baton.md §6), not a second derivation.
    /// </summary>
    public static Dictionary<string, int> CountLiveLanesByVendor(IEnumerable<FleetRoomStatusView> rooms)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var room in rooms)
        {
            if (room.State != "Running" || room.Adapter is not { } adapter)
            {
                continue;
            }

            counts[adapter] = counts.GetValueOrDefault(adapter) + 1;
        }

        return counts;
    }
}

/// <summary>One vendor's projected usage windows plus its current live-lane count (issue #1391).</summary>
/// <param name="Source">#1904: <c>"vendor"</c> when these windows are the vendor CLI's own counter,
/// <c>"derived"</c> when Baton computed them itself because the vendor exposes no counter Baton has
/// measured — see <see cref="VendorUsageProvenance"/>. Always emitted, never omitted: a reader that has
/// to infer provenance from a missing key is exactly what this field exists to prevent, and
/// <c>glass.html</c> renders the word beside the adapter tag on a derived block.</param>
public sealed record VendorUsageProjectionView(
    [property: JsonPropertyName("adapter")] string Adapter,
    [property: JsonPropertyName("harvestedAt")] DateTimeOffset HarvestedAt,
    [property: JsonPropertyName("caveat")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Caveat,
    [property: JsonPropertyName("windows")] IReadOnlyList<VendorUsageWindowView> Windows,
    [property: JsonPropertyName("liveLanes")] int LiveLanes,
    [property: JsonPropertyName("source")] VendorUsageProvenance Source = VendorUsageProvenance.Vendor);

/// <summary>One harvested usage window on the wire (issue #1391) — see
/// <see cref="Baton.Vendors.VendorUsageWindow"/>'s own doc comment for what each field means and when
/// it is absent. <c>ratePctPerHour</c>/<c>minutesToExhaustion</c> (#1746) are the only two fields NOT
/// harvested: they are derived from the persisted sample ring by <see cref="VendorUsageBurn.Derive"/>,
/// which owns their absence rules.</summary>
public sealed record VendorUsageWindowView(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("percentUsed")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? PercentUsed,
    [property: JsonPropertyName("resetsAt")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? ResetsAt,
    [property: JsonPropertyName("rawLine")] string RawLine,
    [property: JsonPropertyName("ratePctPerHour")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    double? RatePctPerHour = null,
    [property: JsonPropertyName("minutesToExhaustion")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    double? MinutesToExhaustion = null);

/// <summary>
/// <c>fleet_status</c>'s top-level response shape since issue #1391 — was a bare JSON array of
/// <see cref="FleetRoomStatusView"/> before this issue; <c>tools/fleet-glass/pusher.py</c>'s own
/// <c>drop_stale_rooms</c>/<c>derive_snapshot_and_timelines</c> already tolerated a <c>{"rooms": [...]}</c>
/// wrapper in anticipation of exactly this migration (their own comments name it). <see cref="Vendors"/>
/// is omitted, never an empty array, whenever <see cref="VendorUsageProjectionReader.ReadAll"/> finds
/// nothing to report.
/// </summary>
public sealed record FleetStatusResponse(
    [property: JsonPropertyName("rooms")] IReadOnlyList<FleetRoomStatusView> Rooms,
    [property: JsonPropertyName("vendors")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<VendorUsageProjectionView>? Vendors);
