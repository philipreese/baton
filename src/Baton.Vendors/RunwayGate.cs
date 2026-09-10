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
/// One on-demand harvest attempt made for a vendor that had no persisted snapshot (#1923), passed
/// into <see cref="RunwayGate.Evaluate"/> so a Hold can say which of two different things happened.
/// <b>"Never harvested" and "harvested and it failed" are not the same claim</b>: the first is a
/// bootstrap state the operator can fix by starting the daemon, the second is a vendor or CLI fault
/// they have to look at. #1923 measured the cost of collapsing them — an agy window that had just
/// reset read as "no readable usage snapshot" and refused every dispatch.
/// </summary>
/// <param name="At">
/// When the attempt was made, in the offset the refusal should print it in. Callers pass a LOCAL
/// instant: an operator reads this beside a reset time their vendor quoted in their own zone, and a
/// bare unlabelled <c>HH:MM</c> in UTC is a true sentence they would draw the wrong conclusion from.
/// </param>
/// <param name="FailureReason">
/// Why the harvest produced no usable snapshot, or null when it produced one. A non-null value here
/// with a null snapshot is the "attempted and failed" arm; null with a null snapshot means the
/// harvest ran and wrote something the gate could not then read, which is stated as its own failure
/// rather than silently falling back to the never-harvested wording.
/// </param>
public sealed record RunwayHarvestAttempt(DateTimeOffset At, string? FailureReason);

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
/// persisted usage snapshot and answers Admit or Hold. <b>This type</b> never runs a vendor CLI, and
/// that stays true after #1923 — the on-demand harvest a missing snapshot now triggers happens in
/// <c>Baton.Cli.OnDemandRunwayHarvest</c>, before this method is called, and reaches it only as a
/// <see cref="RunwayHarvestAttempt"/> value. What is no longer true of the gate as a whole is that a
/// check costs no subscription usage: the first check for a vendor with no snapshot spends one
/// vendor usage read. That bound is stated in spec/baton.md §7.
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
    /// Codex joined the gated population when #1904 replaced its interim ledger estimate with the
    /// vendor's authenticated account counters. <see cref="WindowSelectors"/> identifies its account
    /// bucket and measured durations without assuming that <c>primary</c> means a particular window.
    /// </summary>
    public static readonly IReadOnlyList<string> MeasuredVendors = ["claude", "agy", "codex"];

    /// <summary><see cref="RunwayDecision.Reason"/> for the Admit given to a vendor with no usage source.</summary>
    public const string UnmeasuredReason = "runway: unmeasured";

    /// <summary>
    /// The ONE window selector table (#1848/#1904). Claude and agy select their textual names, which
    /// differ per vendor: claude's are the words between "Current " and the colon in its
    /// <c>/usage</c> report (<see cref="ClaudeUsageSlashCommandSource.Parse"/>), agy's are the
    /// composed <c>"&lt;family&gt; · &lt;window&gt;"</c> name with "Remaining" stripped
    /// (<see cref="AgyUsageSlashCommandSource.Parse"/>). Matched ordinally and exactly, because claude
    /// also reports a <c>"week (Fable)"</c> window the operator ruling of 2026-09-05 excludes
    /// (spec/baton.md §7, "Runway hold (#1848)") — a prefix or contains match on "week" would silently
    /// gate on it.
    /// Codex instead selects <c>limitId=codex</c> and exact measured durations. This keeps a separate
    /// per-model bucket from satisfying the account runway and makes a null account secondary window
    /// unreadable rather than zero.
    /// </summary>
    private static readonly Dictionary<string, WindowSelector> WindowSelectors = new(StringComparer.Ordinal)
    {
        ["claude"] = new("week (all models)", "session"),
        ["agy"] = new("Gemini Models · Weekly Limit", "Gemini Models · Five Hour Limit"),
        ["codex"] = new(
            WeekName: null,
            SessionName: null,
            LimitId: CodexUsageSource.AccountLimitId,
            WeekDurationMins: CodexUsageSource.WeeklyDurationMins,
            SessionDurationMins: CodexUsageSource.FiveHourDurationMins),
    };

    private sealed record WindowSelector(
        string? WeekName,
        string? SessionName,
        string? LimitId = null,
        int? WeekDurationMins = null,
        int? SessionDurationMins = null);

    /// <summary>
    /// Whether this vendor's counters are what decide its admission — membership in the selector
    /// table above. Exposed for #1923's on-demand harvest, which must not spend a vendor usage read on
    /// an adapter whose decision cannot turn on the result.
    /// </summary>
    public static bool IsGated(string vendor) =>
        !string.IsNullOrEmpty(vendor) && WindowSelectors.ContainsKey(vendor);

    /// <summary>
    /// Whether <paramref name="snapshot"/> is evidence this gate can decide on at <paramref name="now"/>:
    /// present, and no older than <see cref="RunwayThresholds.EffectiveMaxSnapshotAge"/>. <b>The one place
    /// that comparison is written</b> — <see cref="Evaluate"/>'s staleness arm and #1966's caller
    /// (<c>DispatchCommand.CreateDiskRunwayEvaluator</c>, deciding whether to harvest inline) both read it
    /// here rather than each spelling out an age test that could drift apart into a hold the harvest never
    /// tries to clear.
    /// </summary>
    public static bool IsUsable(VendorUsageSnapshot? snapshot, RunwayThresholds thresholds, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(thresholds);
        return snapshot is not null && now - snapshot.HarvestedAt <= thresholds.EffectiveMaxSnapshotAge;
    }

    /// <summary>
    /// Decides admission for one vendor. <paramref name="snapshot"/> is that vendor's latest
    /// PERSISTED snapshot (null when none exists or it could not be read) — this method itself never
    /// makes a live vendor usage read.
    /// </summary>
    /// <param name="vendor">The adapter tag being dispatched to, e.g. <c>"claude"</c>.</param>
    /// <param name="now">The clock, passed in so the staleness arm is testable without waiting.</param>
    /// <param name="harvest">
    /// #1923: the on-demand harvest the caller already ran for this vendor because its persisted snapshot
    /// was absent — or, since #1966, stale — or null when none was run (no source for the vendor, or a
    /// caller that does not harvest at all). It only ever changes the WORDING of those two Holds — never
    /// the disposition, which stays a Hold in every arm, because a failed harvest is exactly as much
    /// evidence of headroom as no harvest at all.
    /// </param>
    public static RunwayDecision Evaluate(
        string vendor,
        VendorUsageSnapshot? snapshot,
        RunwayThresholds thresholds,
        DateTimeOffset now,
        RunwayHarvestAttempt? harvest = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(vendor);
        ArgumentNullException.ThrowIfNull(thresholds);

        // Gated-or-not is keyed on the ADAPTER (this table), never on the snapshot's provenance mark.
        // A Derived-marked snapshot for a gated vendor gets no staleness exemption below: a derivation
        // is a lower bound on usage and goes stale exactly like a vendor counter, so exempting it would
        // fail open. The mark's one consumer is the burn ring (VendorUsageBurn), which skips derived
        // blocks because their rolling window is not monotonic (#1926 review).
        if (!WindowSelectors.TryGetValue(vendor, out var selector))
        {
            return new RunwayDecision(vendor, RunwayDisposition.Admit, UnmeasuredReason, []);
        }

        if (snapshot is null)
        {
            return new RunwayDecision(
                vendor,
                RunwayDisposition.Hold,
                DescribeMissingSnapshot(harvest),
                []);
        }

        if (!IsUsable(snapshot, thresholds, now))
        {
            return new RunwayDecision(
                vendor,
                RunwayDisposition.Hold,
                // One decimal, invariant: an integer cast prints a 6h30m snapshot as "6h old (limit 6h)",
                // which reads as a refusal for no reason. Invariant because the refusal text is asserted
                // on, and a comma decimal separator is not what a message contract should turn on.
                $"the usage snapshot is {Hours(now - snapshot.HarvestedAt)}h old "
                + $"(limit {Hours(thresholds.EffectiveMaxSnapshotAge)}h) — "
                + "a stale counter is a lower bound on today's usage, not evidence of headroom"
                + DescribeFailedHarvest(harvest),
                Counters(snapshot, selector),
                SnapshotHarvestedAt: snapshot.HarvestedAt);
        }

        var week = Find(snapshot, selector, weekly: true);
        var session = Find(snapshot, selector, weekly: false);
        var counters = Counters(snapshot, selector);

        if (week is null || session is null)
        {
            var missing = Expected(selector, weekly: week is null);
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
                $"'{week.Name}' is at {weekPct}% (holds at {thresholds.WeekHoldPct}%)",
                counters,
                SnapshotHarvestedAt: snapshot.HarvestedAt);
        }

        if (sessionPct >= thresholds.SessionHoldPct)
        {
            return new RunwayDecision(
                vendor,
                RunwayDisposition.Hold,
                $"'{session.Name}' is at {sessionPct}% (holds at {thresholds.SessionHoldPct}%)",
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

    /// <summary>
    /// The missing-snapshot Hold's wording, which is the whole point of #1923: it must say whether a
    /// harvest was ever attempted. The time is rendered from the attempt's OWN offset with the
    /// invariant culture — the same reason the staleness arm below formats its number that way, since
    /// this text is asserted on and a local-time conversion would make the assertion machine-dependent.
    /// </summary>
    private static string DescribeMissingSnapshot(RunwayHarvestAttempt? harvest) => harvest switch
    {
        null => "no readable usage snapshot has been harvested for this vendor",
        { FailureReason: { } why } => Attempted(harvest.At, why),
        _ => Attempted(harvest.At, "it wrote no snapshot this gate could read"),
    };

    /// <summary>
    /// The same "attempted and failed" clause, appended to the STALE Hold (#1966). A stale snapshot now
    /// takes the same inline-harvest path an absent one has taken since #1923, so the refusal owes the
    /// operator the same distinction: an old number nobody has tried to refresh is a daemon that is not
    /// running, an old number a harvest just failed to refresh is a vendor or CLI fault to look at.
    /// Empty ONLY when no harvest was attempted (a caller that does not harvest). A harvest that reported
    /// success and still left the snapshot stale gets its own sentence rather than a silent omission,
    /// since it means the vendor's report itself is dated — an outcome an operator has to be told about,
    /// not one to hide behind the bare staleness line.
    /// </summary>
    private static string DescribeFailedHarvest(RunwayHarvestAttempt? harvest) => harvest switch
    {
        null => string.Empty,
        { FailureReason: { } why } => $"; {Attempted(harvest.At, why)}",
        _ => $"; harvest attempted at {At(harvest.At)} and the snapshot it wrote is older than the limit too",
    };

    /// <summary>The ONE "attempted and failed" wording, shared by every arm that reports one so the
    /// absent and stale refusals cannot drift into two spellings of one outcome.</summary>
    private static string Attempted(DateTimeOffset at, string why) =>
        $"harvest attempted at {At(at)} and failed: {why}";

    private static string At(DateTimeOffset instant) =>
        instant.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    private static string Hours(TimeSpan span) =>
        span.TotalHours.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

    private static VendorUsageWindow? Find(VendorUsageSnapshot snapshot, WindowSelector selector, bool weekly)
    {
        var name = weekly ? selector.WeekName : selector.SessionName;
        if (name is not null)
        {
            return snapshot.Windows.FirstOrDefault(
                w => string.Equals(w.Name, name, StringComparison.Ordinal));
        }

        var duration = weekly ? selector.WeekDurationMins : selector.SessionDurationMins;
        return snapshot.Windows.FirstOrDefault(w =>
            string.Equals(w.LimitId, selector.LimitId, StringComparison.Ordinal)
            && w.WindowDurationMins == duration);
    }

    private static string Expected(WindowSelector selector, bool weekly)
    {
        var name = weekly ? selector.WeekName : selector.SessionName;
        if (name is not null)
        {
            return name;
        }

        var duration = weekly ? selector.WeekDurationMins : selector.SessionDurationMins;
        return $"{selector.LimitId} {duration}-minute";
    }

    /// <summary>The two gated windows only — the counters a decision actually rests on. claude's
    /// <c>week (Fable)</c> and agy's other families are deliberately absent rather than reported
    /// beside numbers that did not decide anything.</summary>
    private static IReadOnlyList<RunwayCounter> Counters(VendorUsageSnapshot snapshot, WindowSelector selector)
    {
        List<RunwayCounter> counters = [];
        foreach (var weekly in new[] { true, false })
        {
            if (Find(snapshot, selector, weekly) is { } window)
            {
                counters.Add(new RunwayCounter(window.Name, window.PercentUsed));
            }
        }

        return counters;
    }
}
