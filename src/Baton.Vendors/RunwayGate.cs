using Baton.Runway;

namespace Baton.Vendors;

/// <summary>Whether the gate lets a new dispatch in (#1848).</summary>
public enum RunwayDisposition
{
    /// <summary>New spend is admitted on this vendor.</summary>
    Admit,

    /// <summary>New spend is held: the vendor's own counters say the runway is short, or they could not be read.</summary>
    Hold,
}

/// <summary>
/// One vendor's admission decision (#1848). <see cref="Reason"/> is present for every
/// <see cref="RunwayDisposition.Hold"/> and for the one Admit that is not a measurement —
/// <see cref="RunwayGate.UnmeasuredReason"/>, a vendor with no usage source at all.
/// </summary>
/// <param name="HeadroomPoints">
/// #1896: percentage points left before the NEARER of the two thresholds bites — the minimum of
/// (weekHoldPct − week%) and (sessionHoldPct − session%). Computed here rather than by the caller
/// because only this type's window-name table knows which counter is which. <b>Present only on an Admit
/// taken against readable counters</b>: a Hold has no headroom to report, and an unmeasured vendor has no
/// counters at all. That absence is what confines the reservation arm to the one case it is safe in.
/// </param>
/// <param name="SnapshotHarvestedAt">
/// #1896: when the snapshot this decision rests on was harvested, or null when there was none. The
/// reservation arm reconciles against this instant — spend recorded before it is already in the counters
/// — so it is carried on the decision rather than re-read from disk by the caller.
/// </param>
public sealed record RunwayDecision(
    string Vendor,
    RunwayDisposition Disposition,
    string? Reason,
    IReadOnlyList<RunwayCounter> Counters,
    double? HeadroomPoints = null,
    DateTimeOffset? SnapshotHarvestedAt = null)
{
    public bool IsHold => Disposition == RunwayDisposition.Hold;

    /// <summary>The counters as one line — <c>"week (all models) 87%, session 12%"</c>, or a plain
    /// note when nothing was readable, so a refusal never prints an empty clause.</summary>
    public string DescribeCounters() =>
        Counters.Count == 0
            ? "no counters readable"
            : string.Join(", ", Counters.Select(c => c.PercentUsed is { } pct ? $"{c.Window} {pct}%" : $"{c.Window} unknown"));
}

/// <summary>
/// The hold thresholds for one vendor. Defaults are the operator's 2026-09-04 starting point
/// (week ≥85%, session ≥90%), not a measurement — issue #1848's own wording. Overridable per vendor
/// through <see cref="RunwayHoldSettings"/>, which is where <c>~/.baton/settings.json</c> binds.
/// </summary>
public sealed record RunwayThresholds(
    int WeekHoldPct = RunwayThresholds.DefaultWeekHoldPct,
    int SessionHoldPct = RunwayThresholds.DefaultSessionHoldPct,
    TimeSpan? MaxSnapshotAge = null)
{
    public const int DefaultWeekHoldPct = 85;
    public const int DefaultSessionHoldPct = 90;
    public const int DefaultMaxSnapshotAgeHours = 6;

    /// <summary>
    /// How old the harvested snapshot may be before it stops counting as evidence. Six hours by
    /// default, against a 15-minute harvest cadence (<c>VendorUsageHarvester.PeriodicInterval</c>) —
    /// wide enough that a daemon restart or a few skipped harvests do not hold the fleet, narrow
    /// enough that the counters still describe roughly the window being gated on. <b>A stale counter
    /// is not evidence of headroom</b>: the number only ever moves one way inside a window, so an old
    /// reading is a lower bound on today's usage and can be arbitrarily far below it. Past this age
    /// the gate holds rather than admits, the same way an unreadable snapshot does.
    /// </summary>
    public TimeSpan EffectiveMaxSnapshotAge => MaxSnapshotAge ?? TimeSpan.FromHours(DefaultMaxSnapshotAgeHours);
}

/// <summary>
/// <b>The admission gate for new vendor spend (#1848).</b> Pure: it takes the vendor's latest
/// persisted usage snapshot and answers Admit or Hold. It never runs a vendor CLI — the daemon's
/// <c>VendorUsageHarvester</c> (#1391/#1869) harvests, dispatch reads, so a gate check costs no
/// subscription usage and cannot itself be the thing that exhausts the runway it is protecting.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hold means "do not start new work", never "stop running work".</b> Nothing in this type
/// arrests anything; the operator ruling (2026-09-04, issue #1848) is that running work always
/// finishes. spec/baton.md §7's "Runway hold (#1848)" states the whole contract, including which
/// entry points consult this gate and which deliberately do not.
/// </para>
/// <para>
/// <b>Every unreadable case holds.</b> A missing snapshot file, a snapshot with no window this table
/// recognizes, a recognized window whose percentage did not parse, and a snapshot older than
/// <see cref="RunwayThresholds.EffectiveMaxSnapshotAge"/> all Hold. The alternative — admitting
/// because nothing said not to — makes a broken harvester read as unlimited headroom.
/// </para>
/// </remarks>
public static class RunwayGate
{
    /// <summary>
    /// The vendors a usage snapshot exists for at all: exactly the adapter tags an
    /// <see cref="IVendorUsageSource"/> exists for. Canonical list — <c>VendorUsageProjectionReader</c>
    /// reads its snapshot-file population from here rather than keeping a second copy. A vendor
    /// outside it is admitted with <see cref="UnmeasuredReason"/>. The register for why that differs
    /// from an unreadable snapshot's Hold: spec/baton.md §7, "Runway hold (#1848)".
    /// <para>
    /// <b>Membership here is not the same as being gated (#1904).</b> <see cref="Evaluate"/> keys on
    /// <see cref="WindowNames"/>, not on this list, and codex is on this list without being in that
    /// table: <see cref="CodexUsageSource"/> harvests a snapshot (so the glass gets a codex block) whose
    /// windows are DERIVED and carry no percentage unless the operator declared a plan ceiling. Putting
    /// those names in the table would send every ceiling-less codex dispatch down the "recognized
    /// window, no percentage" Hold arm — holding a vendor for the same absence #1848 chose to admit as
    /// unmeasured. So codex is still admitted with <see cref="UnmeasuredReason"/>, and gating on the
    /// derived counters is a follow-up decision, not a side effect of this list growing.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> MeasuredVendors = ["claude", "agy", "codex"];

    /// <summary><see cref="RunwayDecision.Reason"/> for the Admit given to a vendor with no usage source.</summary>
    public const string UnmeasuredReason = "runway: unmeasured";

    /// <summary>
    /// The ONE window-name table (#1848). Each vendor's own parser decides these strings, and they
    /// differ per vendor: claude's are the words between "Current " and the colon in its
    /// <c>/usage</c> report (<see cref="ClaudeUsageSlashCommandSource.Parse"/>), agy's are the
    /// composed <c>"&lt;family&gt; · &lt;window&gt;"</c> name with "Remaining" stripped
    /// (<see cref="AgyUsageSlashCommandSource.Parse"/>). Matched ordinally and exactly, because claude
    /// also reports a <c>"week (Fable)"</c> window the operator ruling of 2026-09-05 excludes
    /// (spec/baton.md §7, "Runway hold (#1848)") — a prefix or contains match on "week" would silently
    /// gate on it.
    /// <para>
    /// <b>This table, not <see cref="MeasuredVendors"/>, is what makes a vendor gated</b>, and codex is
    /// deliberately absent from it even though it now has a source — that list's own doc comment states
    /// why (#1904).
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, (string Week, string Session)> WindowNames = new(StringComparer.Ordinal)
    {
        ["claude"] = ("week (all models)", "session"),
        ["agy"] = ("Gemini Models · Weekly Limit", "Gemini Models · Five Hour Limit"),
    };

    /// <summary>
    /// Decides admission for one vendor. <paramref name="snapshot"/> is that vendor's latest
    /// PERSISTED snapshot (null when none exists or it could not be read) — never a live
    /// <c>/usage</c> call made from inside dispatch.
    /// </summary>
    /// <param name="vendor">The adapter tag being dispatched to, e.g. <c>"claude"</c>.</param>
    /// <param name="now">The clock, passed in so the staleness arm is testable without waiting.</param>
    public static RunwayDecision Evaluate(
        string vendor,
        VendorUsageSnapshot? snapshot,
        RunwayThresholds thresholds,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrEmpty(vendor);
        ArgumentNullException.ThrowIfNull(thresholds);

        // Gated-or-not is keyed on the ADAPTER (this table), never on the snapshot's provenance mark.
        // A Derived-marked snapshot for a gated vendor gets no staleness exemption below: a derivation
        // is a lower bound on usage and goes stale exactly like a vendor counter, so exempting it would
        // fail open. The mark's one consumer is the burn ring (VendorUsageBurn), which skips derived
        // blocks because their rolling window is not monotonic (#1926 review).
        if (!WindowNames.TryGetValue(vendor, out var names))
        {
            return new RunwayDecision(vendor, RunwayDisposition.Admit, UnmeasuredReason, []);
        }

        if (snapshot is null)
        {
            return new RunwayDecision(
                vendor,
                RunwayDisposition.Hold,
                "no readable usage snapshot has been harvested for this vendor",
                []);
        }

        var age = now - snapshot.HarvestedAt;
        if (age > thresholds.EffectiveMaxSnapshotAge)
        {
            return new RunwayDecision(
                vendor,
                RunwayDisposition.Hold,
                // One decimal, invariant: an integer cast prints a 6h30m snapshot as "6h old (limit 6h)",
                // which reads as a refusal for no reason. Invariant because the refusal text is asserted
                // on, and a comma decimal separator is not what a message contract should turn on.
                $"the usage snapshot is {Hours(age)}h old (limit {Hours(thresholds.EffectiveMaxSnapshotAge)}h) — "
                + "a stale counter is a lower bound on today's usage, not evidence of headroom",
                Counters(snapshot, names),
                SnapshotHarvestedAt: snapshot.HarvestedAt);
        }

        var week = Find(snapshot, names.Week);
        var session = Find(snapshot, names.Session);
        var counters = Counters(snapshot, names);

        if (week is null || session is null)
        {
            var missing = week is null ? names.Week : names.Session;
            return new RunwayDecision(
                vendor,
                RunwayDisposition.Hold,
                $"the harvested snapshot carries no '{missing}' window — the vendor's report was not readable",
                counters,
                SnapshotHarvestedAt: snapshot.HarvestedAt);
        }

        if (week.PercentUsed is not { } weekPct || session.PercentUsed is not { } sessionPct)
        {
            return new RunwayDecision(
                vendor,
                RunwayDisposition.Hold,
                "the harvested snapshot carries a window with no percentage — the vendor's report was not readable",
                counters,
                SnapshotHarvestedAt: snapshot.HarvestedAt);
        }

        if (weekPct >= thresholds.WeekHoldPct)
        {
            return new RunwayDecision(
                vendor,
                RunwayDisposition.Hold,
                $"'{names.Week}' is at {weekPct}% (holds at {thresholds.WeekHoldPct}%)",
                counters,
                SnapshotHarvestedAt: snapshot.HarvestedAt);
        }

        if (sessionPct >= thresholds.SessionHoldPct)
        {
            return new RunwayDecision(
                vendor,
                RunwayDisposition.Hold,
                $"'{names.Session}' is at {sessionPct}% (holds at {thresholds.SessionHoldPct}%)",
                counters,
                SnapshotHarvestedAt: snapshot.HarvestedAt);
        }

        return new RunwayDecision(
            vendor,
            RunwayDisposition.Admit,
            Reason: null,
            counters,
            HeadroomPoints: Math.Min(thresholds.WeekHoldPct - weekPct, thresholds.SessionHoldPct - sessionPct),
            SnapshotHarvestedAt: snapshot.HarvestedAt);
    }

    private static string Hours(TimeSpan span) =>
        span.TotalHours.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

    private static VendorUsageWindow? Find(VendorUsageSnapshot snapshot, string name) =>
        snapshot.Windows.FirstOrDefault(w => string.Equals(w.Name, name, StringComparison.Ordinal));

    /// <summary>The two gated windows only — the counters a decision actually rests on. claude's
    /// <c>week (Fable)</c> and agy's other families are deliberately absent rather than reported
    /// beside numbers that did not decide anything.</summary>
    private static IReadOnlyList<RunwayCounter> Counters(VendorUsageSnapshot snapshot, (string Week, string Session) names)
    {
        List<RunwayCounter> counters = [];
        foreach (var name in new[] { names.Week, names.Session })
        {
            if (Find(snapshot, name) is { } window)
            {
                counters.Add(new RunwayCounter(window.Name, window.PercentUsed));
            }
        }

        return counters;
    }
}
