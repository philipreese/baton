using System.Text.Json.Serialization;

namespace Baton.Queue;

/// <summary>
/// The conductor queue's scheduling policy and tier table (#1934 slice 1), living under
/// <c>Queue</c> in the settings file baton already has (<c>BatonPaths.SettingsFile</c>) rather than a
/// second config file — the same placement <c>RunwayHoldSettings</c> took, for the same reason.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every shipped default is the operator's own 2026-09-05 number</b>, carried over from the
/// PowerShell scratchpad runner this queue replaces (#1934 body, item 1). The values were doctrine in
/// that loop's comments and nowhere in the product; they are data here so the comparator (#1903) and
/// the glass (#1912) can read them. spec/baton.md §12 states them once — this file is the code that
/// holds them, not a second register of what they are.
/// </para>
/// <para>
/// <b>An out-of-range value falls back to the default rather than being clamped or honoured</b>, the
/// same posture <c>RunwayHoldSettings</c> takes and for the same reason: a zero
/// <see cref="MaxLiveWeight"/> would hold every launch forever and a negative
/// <see cref="GapSeconds"/> would disable the gap silently, and neither is a setting anyone means.
/// The effective values are the <c>Effective*</c> properties; nothing reads the raw fields directly.
/// </para>
/// </remarks>
public sealed record QueueSettings
{
    /// <summary>
    /// The weighted concurrency cap. An item launches only when the live weight already running,
    /// plus the candidate's own weight, would not exceed this. Weights are
    /// <see cref="QueueWeights"/>'s to define, not this record's.
    /// </summary>
    public double MaxLiveWeight { get; init; } = DefaultMaxLiveWeight;

    /// <summary>Free-physical-memory floor, in GiB, during the day band (see <see cref="NightStartHour"/>).</summary>
    public double FloorGbDay { get; init; } = DefaultFloorGbDay;

    /// <summary>Free-physical-memory floor, in GiB, during the night band — lower, because the
    /// operator is not also using the machine.</summary>
    public double FloorGbNight { get; init; } = DefaultFloorGbNight;

    /// <summary>
    /// First hour of the night band, in the operator's LOCAL wall clock — not UTC.
    /// <see cref="IsNightBand"/> owns the comparison and its own remarks state why the distinction is
    /// load-bearing.
    /// </summary>
    public int NightStartHour { get; init; } = DefaultNightStartHour;

    /// <summary>First hour of the day band, local wall clock — see <see cref="NightStartHour"/>.</summary>
    public int DayStartHour { get; init; } = DefaultDayStartHour;

    /// <summary>Minimum seconds between two launches, so a burst of queued items does not all start
    /// at once and compete for the same memory the floor above is protecting.</summary>
    public int GapSeconds { get; init; } = DefaultGapSeconds;

    /// <summary>How often the daemon's scheduler evaluates the queue. Distinct from
    /// <see cref="GapSeconds"/>: the tick is how often a decision is <em>made</em>, the gap is how
    /// often a launch is <em>allowed</em>.</summary>
    public int TickSeconds { get; init; } = DefaultTickSeconds;

    /// <summary>
    /// The role-and-scope tier table (#1934 Q3), keyed by <see cref="QueueTierTable.KeyFor"/>. Null
    /// (the default) means the shipped table in <see cref="QueueTierTable.ShippedDefaults"/>; a
    /// non-null table is overlaid on top of it entry by entry, so an operator who names one key does
    /// not lose the other five.
    /// </summary>
    public IReadOnlyDictionary<string, QueueTierSettings>? Tiers { get; init; }

    /// <summary>
    /// Per-adapter fallback model for an item whose resolved tier names an adapter but no model.
    /// Null keeps <see cref="QueueTierTable.ShippedAdapterDefaultModels"/> (today: <c>agy</c> →
    /// <c>gemini-3.8-flash-high</c>), overlaid the same way <see cref="Tiers"/> is.
    /// </summary>
    public IReadOnlyDictionary<string, string>? AdapterDefaultModels { get; init; }

    /// <summary>
    /// Where <c>baton queue add --issue &lt;n&gt;</c> provisions its worktree: the directory
    /// <c>w&lt;n&gt;</c> is created under. Null means "the parent directory of the checkout the verb
    /// was invoked from", which is what the scratchpad runner did; naming it here is what lets an
    /// operator whose repos are not siblings say so. Stated in spec/baton.md §12 as an assumption,
    /// because the issue's own <c>&lt;repos&gt;</c> was never defined.
    /// </summary>
    public string? WorktreeRoot { get; init; }

    [JsonIgnore]
    public double EffectiveMaxLiveWeight => MaxLiveWeight > 0 ? MaxLiveWeight : DefaultMaxLiveWeight;

    [JsonIgnore]
    public double EffectiveFloorGbDay => FloorGbDay >= 0 ? FloorGbDay : DefaultFloorGbDay;

    [JsonIgnore]
    public double EffectiveFloorGbNight => FloorGbNight >= 0 ? FloorGbNight : DefaultFloorGbNight;

    [JsonIgnore]
    public int EffectiveGapSeconds => GapSeconds >= 0 ? GapSeconds : DefaultGapSeconds;

    [JsonIgnore]
    public int EffectiveTickSeconds => TickSeconds > 0 ? TickSeconds : DefaultTickSeconds;

    [JsonIgnore]
    public int EffectiveNightStartHour => NightStartHour is >= 0 and <= 23 ? NightStartHour : DefaultNightStartHour;

    [JsonIgnore]
    public int EffectiveDayStartHour => DayStartHour is >= 0 and <= 23 ? DayStartHour : DefaultDayStartHour;

    /// <summary>
    /// The free-memory floor in force at <paramref name="localNow"/>.
    /// </summary>
    /// <param name="localNow">
    /// <b>The operator's local wall clock, not UTC.</b> "Night is 20:00–09:00" is a statement about
    /// when a person is at the machine; computing the band in UTC would move it by the host's offset
    /// (five hours, for the operator this default was measured on) and quietly apply the night floor
    /// through the afternoon. <c>QueueScheduler</c> is handed a <see cref="DateTimeOffset"/> and calls
    /// <c>.LocalDateTime</c> exactly once, at its own entry point, so this rule has one enforcement
    /// site rather than one per caller.
    /// </param>
    public double FloorGbAt(DateTime localNow) => IsNightBand(localNow) ? EffectiveFloorGbNight : EffectiveFloorGbDay;

    /// <summary>
    /// Whether <paramref name="localNow"/> falls in the night band — the wrap-around comparison
    /// <c>hour &gt;= nightStart || hour &lt; dayStart</c>, which is what makes a band that crosses
    /// midnight (20:00–09:00) work at all. See <see cref="FloorGbAt"/> for why the argument is local.
    /// </summary>
    public bool IsNightBand(DateTime localNow)
    {
        var hour = localNow.Hour;
        var nightStart = EffectiveNightStartHour;
        var dayStart = EffectiveDayStartHour;

        // A band that does not wrap (e.g. nightStart 2, dayStart 6) is the plain interval; only the
        // wrapping case needs the OR. Both are spelled out rather than assuming the shipped values.
        return nightStart >= dayStart
            ? hour >= nightStart || hour < dayStart
            : hour >= nightStart && hour < dayStart;
    }

    public const double DefaultMaxLiveWeight = 4.0;
    public const double DefaultFloorGbDay = 2.0;
    public const double DefaultFloorGbNight = 1.2;
    public const int DefaultNightStartHour = 20;
    public const int DefaultDayStartHour = 9;
    public const int DefaultGapSeconds = 180;
    public const int DefaultTickSeconds = 30;
}

/// <summary>One tier-table entry: the three axes decision 0017 keeps independent. Every field is
/// independently absent — a tier that names an adapter and no model leaves the model to
/// <see cref="QueueSettings.AdapterDefaultModels"/>, and one that names no effort leaves the role's
/// own tier effort in force.</summary>
public sealed record QueueTierSettings
{
    public string? Adapter { get; init; }
    public string? Model { get; init; }
    public string? Effort { get; init; }
}
