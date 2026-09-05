using Baton.Vendors;

namespace Baton.Cli.Mcp;

/// <summary>
/// One harvest's reading of a single usage window — the unit of the short ring
/// <see cref="PersistedVendorUsage.Rings"/> keeps so a burn RATE can be derived at all (#1746). A
/// single reading carries no rate: two are the minimum, which is the absence rule spec/baton.md §6's
/// <c>windows[]</c> table states canonically.
/// </summary>
public sealed record VendorUsageSample(DateTimeOffset At, int PercentUsed);

/// <summary>
/// The on-disk shape of <see cref="Baton.Status.BatonPaths.VendorUsageSnapshotFile"/> since #1746 —
/// the latest <see cref="VendorUsageSnapshot"/>'s own four fields, flat and byte-identical to what
/// #1869 persisted, PLUS the per-window sample rings. Flat on purpose: a pre-#1746 file (no
/// <see cref="Rings"/> key) still deserializes, with a null ring dictionary read as "no history yet",
/// and a reader that only wants the snapshot half can still deserialize this file as a bare
/// <see cref="VendorUsageSnapshot"/>.
/// <para>
/// Machine-local persisted state, not a wire contract — serialized with the DEFAULT (PascalCase)
/// options, the same as #1869's snapshot file. The wire shape is
/// <see cref="VendorUsageWindowView"/>, whose <c>ratePctPerHour</c>/<c>minutesToExhaustion</c> are
/// DERIVED from these rings at read time (<see cref="VendorUsageProjectionReader.ReadAll"/>), never
/// persisted and never recomputed by <c>glass.html</c> — one arithmetic, in the daemon's projection.
/// </para>
/// </summary>
/// <param name="Rings">Sample ring per <see cref="VendorUsageWindow.Name"/>, oldest first, bounded at
/// <see cref="VendorUsageBurn.RingCapacity"/>. Null on a pre-#1746 file.</param>
/// <param name="Source">#1904: <see cref="VendorUsageSnapshot.Source"/>, persisted. Trailing with a
/// <see cref="VendorUsageProvenance.Vendor"/> default, so a pre-#1904 file (no <c>Source</c> key,
/// written only by the two vendor-reported sources) deserializes to exactly what it always was rather
/// than needing a migration to say so.</param>
public sealed record PersistedVendorUsage(
    string Vendor,
    DateTimeOffset HarvestedAt,
    string? Caveat,
    IReadOnlyList<VendorUsageWindow> Windows,
    IReadOnlyDictionary<string, IReadOnlyList<VendorUsageSample>>? Rings,
    VendorUsageProvenance Source = VendorUsageProvenance.Vendor)
{
    /// <summary>The snapshot half, for a caller that wants #1869's record shape back.</summary>
    public VendorUsageSnapshot ToSnapshot() => new(Vendor, HarvestedAt, Caveat, Windows, Source);
}

/// <summary>
/// #1746's burn arithmetic: ring maintenance on the harvester's write, rate and
/// minutes-to-exhaustion on the projection's read. Pure and static so both arms are testable without
/// touching a file or spawning a vendor CLI, and so the two halves cannot drift into two different
/// notions of what a "sample" is.
/// <para>
/// <b>Advisory only</b>, like everything else in the <c>vendors[]</c> block. What these two numbers
/// are, what they are not, and who may ever gate on them: spec/baton.md §6, "Burn rate and
/// minutes-to-exhaustion".
/// </para>
/// </summary>
public static class VendorUsageBurn
{
    /// <summary>How many harvests of one window are retained. Twelve harvests at the harvester's
    /// 15-minute periodic cadence is three hours when lanes are live; the count alone does not bound
    /// the span, because the harvester backs off while the fleet is idle, so
    /// <see cref="MaxLookBack"/> bounds it in time as well (#1891 review).</summary>
    public const int RingCapacity = 12;

    /// <summary>The oldest a retained sample may be, measured from the newest sample in the same ring.
    /// Three hours: long enough for a rate to mean something on claude's 5-hour session window, short
    /// enough that a burst an hour ago stops dominating the number, and the bound that keeps an idle
    /// gap (the harvester's own backoff) from being averaged into a rate as though tokens were being
    /// spent across it.</summary>
    public static readonly TimeSpan MaxLookBack = TimeSpan.FromHours(3);

    /// <summary>
    /// Folds <paramref name="snapshot"/> into <paramref name="existing"/>, returning the rings to
    /// persist beside it. Rules, in the order they apply per window:
    /// <list type="bullet">
    /// <item>A <see cref="VendorUsageProvenance.Derived"/> snapshot keeps NO ring at all, and drops any
    /// it already had. Keyed on PROVENANCE rather than on any one window's shape, deliberately: the
    /// rollover rule below detects a reset by the reading falling, which is only sound when the vendor
    /// declares the boundary it fell across. A derived figure has no such boundary to be checked
    /// against, so no derived reading can be told apart from a rolled-over one — whatever shape a
    /// future derived source's windows take. What makes today's one fall without a reset:
    /// <see cref="CodexUsageSource"/>'s "rolling total is NOT monotonic" paragraph, which also has why
    /// the monotonic alternative is unavailable. spec/baton.md §6's <c>windows[]</c> table states the
    /// resulting wire absence.</item>
    /// <item>A window whose <see cref="VendorUsageWindow.Name"/> appears more than once in this one
    /// snapshot keeps NO ring — two rows under one key would merge into one nonsense rate, and no
    /// rate is the conservative reading. (Neither vendor's parser produces duplicates today; agy
    /// composes <c>family · window</c> precisely so its rows stay distinct.)</item>
    /// <item>A window with no <see cref="VendorUsageWindow.PercentUsed"/> contributes no sample — an
    /// unparsed reading is not a number — but its existing ring survives, so one degraded harvest
    /// does not throw away the history.</item>
    /// <item>A percent BELOW the previous sample's means the vendor's window rolled over: the ring is
    /// cleared first, and this reading becomes post-reset sample #1. Rate is therefore absent until a
    /// second post-reset harvest lands.</item>
    /// <item>Samples older than <see cref="MaxLookBack"/> before this reading are dropped before the
    /// capacity trim, so a ring that sat idle across the harvester's backoff resumes from the recent
    /// samples only; the rate never averages across a gap nothing was measured in.</item>
    /// <item>A window absent from this snapshot loses its ring entirely, which is what keeps the file
    /// bounded when a vendor renames or drops a window.</item>
    /// </list>
    /// </summary>
    public static Dictionary<string, IReadOnlyList<VendorUsageSample>> Advance(
        IReadOnlyDictionary<string, IReadOnlyList<VendorUsageSample>>? existing,
        VendorUsageSnapshot snapshot)
    {
        if (snapshot.Source == VendorUsageProvenance.Derived)
        {
            return new Dictionary<string, IReadOnlyList<VendorUsageSample>>(StringComparer.Ordinal);
        }

        var duplicated = snapshot.Windows
            .GroupBy(w => w.Name, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);

        var advanced = new Dictionary<string, IReadOnlyList<VendorUsageSample>>(StringComparer.Ordinal);

        foreach (var window in snapshot.Windows)
        {
            if (duplicated.Contains(window.Name))
            {
                continue;
            }

            List<VendorUsageSample> ring =
                existing is not null && existing.TryGetValue(window.Name, out var prior) && prior is not null
                    ? [.. prior]
                    : [];

            if (window.PercentUsed is not { } percentUsed)
            {
                if (ring.Count > 0)
                {
                    advanced[window.Name] = ring;
                }

                continue;
            }

            if (ring.Count > 0 && percentUsed < ring[^1].PercentUsed)
            {
                ring.Clear();
            }

            ring.Add(new VendorUsageSample(snapshot.HarvestedAt, percentUsed));
            var horizon = snapshot.HarvestedAt - MaxLookBack;
            ring.RemoveAll(sample => sample.At < horizon);
            if (ring.Count > RingCapacity)
            {
                ring.RemoveRange(0, ring.Count - RingCapacity);
            }

            advanced[window.Name] = ring;
        }

        return advanced;
    }

    /// <summary>
    /// Derives one window's advisory burn from its ring: rate over the ring's whole span (oldest to
    /// newest sample), and minutes until <paramref name="percentUsed"/> reaches 100 at that rate.
    /// <b>Every absence rule below is spec/baton.md §6's <c>windows[]</c> table, which states them
    /// canonically — this method implements them and does not restate them.</b> The one rule that is
    /// about this code rather than about the wire: non-finite arithmetic (two samples at one instant,
    /// a clock stepping backwards) resolves to an absence, because a serialized
    /// <c>Infinity</c>/<c>NaN</c> is not a wrong number but a <c>System.Text.Json</c> throw that
    /// would take the whole <c>fleet_status</c> response down with it.
    /// </summary>
    /// <param name="percentUsed">The window's LATEST percent used.</param>
    public static (double? RatePctPerHour, double? MinutesToExhaustion) Derive(
        IReadOnlyList<VendorUsageSample>? ring,
        int? percentUsed)
    {
        if (ring is null || ring.Count < 2)
        {
            return (null, null);
        }

        var first = ring[0];
        var last = ring[^1];
        var hours = (last.At - first.At).TotalHours;
        if (hours <= 0)
        {
            return (null, null);
        }

        var rate = (last.PercentUsed - first.PercentUsed) / hours;
        if (!double.IsFinite(rate))
        {
            return (null, null);
        }

        // Two decimals is display precision, not a second computation -- and a rate too small to
        // survive it is reported as 0 with no ETA, rather than as an exhaustion months away.
        rate = Math.Round(rate, 2);
        if (rate <= 0 || percentUsed is not { } pct)
        {
            return (rate, null);
        }

        var remaining = 100 - pct;
        if (remaining <= 0)
        {
            return (rate, 0);
        }

        var minutes = remaining / rate * 60.0;
        return double.IsFinite(minutes) ? (rate, Math.Round(minutes, 1)) : (rate, null);
    }
}
