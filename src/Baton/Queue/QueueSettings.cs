using System.Text.Json.Serialization;

namespace Baton.Queue;

/// <summary>
/// The <c>Queue</c> block of <c>BatonPaths.SettingsFile</c> (#1934 slice 1) — one settings file, not a
/// second config file, the same placement <c>RunwayHoldSettings</c> took.
/// </summary>
/// <remarks>
/// <para>
/// Every shipped default is the operator's own 2026-09-05 number, carried over from the PowerShell
/// runner this queue replaces. spec/baton.md §13's table is the register of what they are and why;
/// this file is where they live as data.
/// </para>
/// <para>
/// <b>The <c>Effective*</c> properties are what anything reads</b> — never the raw fields, which carry
/// whatever an operator typed. <c>RunwayHoldSettings</c> established that shape; §13 has the argument
/// for preferring it to clamping.
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

    /// <summary>First hour of the night band, in LOCAL wall clock. <see cref="FloorGbAt"/>'s parameter
    /// doc has why that qualifier matters.</summary>
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
    /// The directory <c>baton queue add --issue &lt;n&gt;</c> creates <c>w&lt;n&gt;</c> under. Null
    /// resolves in <c>IssueWorktreeProvisioner</c>, whose doc states the fallback; spec/baton.md §13
    /// records it as an assumption, since the issue never defined its own <c>&lt;repos&gt;</c>.
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
    /// <b>Local wall clock, not UTC</b> — spec/baton.md §13 has why the distinction is not cosmetic.
    /// The enforcement is that <c>QueueScheduler</c> takes a <see cref="DateTimeOffset"/> and calls
    /// <c>.LocalDateTime</c> exactly once at its entry point, so a caller cannot get this wrong
    /// independently; this parameter's type (a bare <see cref="DateTime"/>) is what makes the
    /// conversion someone else's already-made decision.
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
