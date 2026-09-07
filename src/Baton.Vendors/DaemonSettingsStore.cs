using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Queue;

namespace Baton.Vendors;

/// <summary>Daemon-side settings that apply machine-wide rather than to any one room. Starts with the
/// concurrency caps (#1298); see decision 0020's amendment for why this lives daemon-side rather than
/// per-desktop-install.</summary>
public sealed record DaemonSettings
{
    public int GlobalConcurrencyCap { get; init; } = DefaultGlobalConcurrencyCap;
    public int PerVendorConcurrencyCap { get; init; } = DefaultPerVendorConcurrencyCap;

    /// <summary>
    /// #1659: gates <see cref="Baton.Cli.Daemon.RoomRetentionSweep"/>'s automatic
    /// <c>baton rooms prune --terminal</c> call — <c>null</c> (the default) means off, matching the
    /// issue's "default off, so the operator opts in" ruling. A room's <c>terminal.json</c> at least
    /// this many days old is eligible; see <c>RoomsPruneOptions.OlderThanDays</c> for the exact
    /// predicate this value feeds.
    /// </summary>
    public int? RoomsRetentionDays { get; init; }

    /// <summary>
    /// #1848: the runway hold's thresholds, read by <c>baton dispatch</c> before it admits new vendor
    /// spend. Never null — an absent <c>RunwayHold</c> key in <c>settings.json</c> leaves the
    /// operator-approved defaults (week ≥85%, session ≥90%) in force, so the gate exists on a machine
    /// that has never been configured.
    /// </summary>
    /// <remarks>
    /// <b>An explicit <c>"RunwayHold": null</c> falls back to those same defaults</b>, which is why this
    /// reads through a nullable backing field rather than relying on the initializer. A well-formed file
    /// carrying an explicit null parses cleanly — <see cref="DaemonSettingsStore.LoadAsync"/>'s
    /// defaults-on-failure arm only fires for an absent, unreadable, or malformed file — so without this
    /// the deserializer would hand back a null here and the first <c>For(vendor)</c> would throw where
    /// the operator's typo should simply have left the gate at its shipped thresholds.
    /// </remarks>
    public RunwayHoldSettings RunwayHold
    {
        get => _runwayHold ?? DefaultRunwayHold;

        // Coalesced on the way IN as well as out, and the field carries the default from the start:
        // this is a record, so the synthesized equality compares the FIELD. Leaving it null on an
        // instance nobody configured would make `new DaemonSettings()` unequal to the same settings
        // round-tripped through the store, which is what DaemonSettingsStoreTests asserts.
        init => _runwayHold = value ?? DefaultRunwayHold;
    }

    /// <summary>
    /// #1904: the operator's own declared ChatGPT-plan token ceilings, and the ONLY thing that lets
    /// <see cref="CodexUsageSource"/> report a percentage at all. Null (the default) means no ceiling
    /// has been declared, and every derived codex window's <c>percentUsed</c> is then absent rather
    /// than computed against a guess — <see cref="CodexUsageSource"/>'s own doc comment has why Baton
    /// has no allowance of its own to fall back on. Shape:
    /// <code>
    /// { "CodexPlanCeiling": { "FiveHourTokens": 5000000, "WeeklyTokens": 120000000 } }
    /// </code>
    /// </summary>
    public CodexPlanCeilingSettings? CodexPlanCeiling { get; init; }

    /// <summary>
    /// #1934 slice 1 — see <see cref="QueueSettings"/> for what it holds. Never null, and by exactly
    /// the mechanism <see cref="RunwayHold"/> uses above: read-through nullable backing field,
    /// coalesced on the way in as well as out. That property's remarks carry the whole argument,
    /// record equality included, and it applies here unchanged.
    /// </summary>
    public QueueSettings Queue
    {
        get => _queue ?? DefaultQueue;
        init => _queue = value ?? DefaultQueue;
    }

    /// <summary>
    /// #1946 — the tailnet plane's listener (spec/baton.md §11 C-11). Never null, by the same
    /// read-through nullable backing field <see cref="RunwayHold"/> uses; that property's remarks carry
    /// the whole argument, record equality included. Off unless the operator opts in:
    /// <code>
    /// { "Glass": { "Listen": true, "Port": 8420 } }
    /// </code>
    /// </summary>
    public GlassListenerSettings Glass
    {
        get => _glass ?? DefaultGlass;
        init => _glass = value ?? DefaultGlass;
    }

    private static readonly RunwayHoldSettings DefaultRunwayHold = new();

    private static readonly QueueSettings DefaultQueue = new();

    private static readonly GlassListenerSettings DefaultGlass = new();

    private readonly QueueSettings? _queue = DefaultQueue;

    private readonly GlassListenerSettings? _glass = DefaultGlass;

    private readonly RunwayHoldSettings? _runwayHold = DefaultRunwayHold;

    public const int DefaultGlobalConcurrencyCap = 3;
    public const int DefaultPerVendorConcurrencyCap = 2;
}

/// <summary>
/// The operator's runway-hold configuration (#1848), living in the settings file baton already has
/// (<see cref="BatonPaths.SettingsFile"/>) rather than a second config file. Fleet-wide defaults plus
/// per-vendor overrides under <see cref="Vendors"/>, keyed by adapter tag:
/// <code>
/// { "RunwayHold": { "WeekHoldPct": 80, "Vendors": { "agy": { "SessionHoldPct": 95 } } } }
/// </code>
/// </summary>
/// <remarks>
/// <b>An out-of-range value falls back to the default rather than being clamped or honoured.</b> A
/// percentage outside 1–100 and a non-positive age are operator typos, and both plausible typos are
/// dangerous in opposite directions — <c>0</c> would hold every dispatch forever, <c>150</c> would
/// disable the gate silently. Neither is a setting anyone means, so the shipped default applies
/// instead; the same posture <see cref="DaemonSettingsStore"/> already takes for a malformed file.
/// </remarks>
public sealed record RunwayHoldSettings
{
    public int WeekHoldPct { get; init; } = RunwayThresholds.DefaultWeekHoldPct;
    public int SessionHoldPct { get; init; } = RunwayThresholds.DefaultSessionHoldPct;
    public int MaxSnapshotAgeHours { get; init; } = RunwayThresholds.DefaultMaxSnapshotAgeHours;

    /// <summary>
    /// #1896: which <c>Baton.Runway.IRunwayReservationPolicy</c> the cross-dispatch reservation arm runs.
    /// Fleet-wide only — a per-vendor override would let two vendors' reservations be priced in
    /// incomparable units. Null, absent, or unrecognised resolves to the shipped default;
    /// <c>Baton.Runway.RunwayReservationPolicies.Resolve</c> is the one place the token becomes a policy,
    /// and the policy types are where the default and its row threshold are declared.
    /// </summary>
    public string? ReservationPolicy { get; init; }

    /// <summary>Per-adapter-tag overrides; any field a vendor entry leaves null keeps the fleet-wide value above.</summary>
    public IReadOnlyDictionary<string, RunwayVendorHoldSettings>? Vendors { get; init; }

    /// <summary>The thresholds in force for one adapter tag, after the per-vendor overlay.</summary>
    public RunwayThresholds For(string vendor)
    {
        ArgumentException.ThrowIfNullOrEmpty(vendor);

        RunwayVendorHoldSettings? perVendor = null;
        if (Vendors is not null)
        {
            // Deserialized dictionaries carry an ordinal comparer; the adapter tag an operator types
            // into settings.json should not have to match its case exactly to take effect.
            foreach (var (key, value) in Vendors)
            {
                if (string.Equals(key, vendor, StringComparison.OrdinalIgnoreCase))
                {
                    perVendor = value;
                    break;
                }
            }
        }

        return new RunwayThresholds(
            Percent(perVendor?.WeekHoldPct ?? WeekHoldPct, RunwayThresholds.DefaultWeekHoldPct),
            Percent(perVendor?.SessionHoldPct ?? SessionHoldPct, RunwayThresholds.DefaultSessionHoldPct),
            TimeSpan.FromHours(Hours(perVendor?.MaxSnapshotAgeHours ?? MaxSnapshotAgeHours)));
    }

    private static int Percent(int value, int fallback) => value is > 0 and <= 100 ? value : fallback;

    private static int Hours(int value) => value > 0 ? value : RunwayThresholds.DefaultMaxSnapshotAgeHours;
}

/// <summary>
/// The operator's own statement of how many tokens their ChatGPT plan allows per window (#1904).
/// <b>Not a measurement</b> — nothing in the tree knows a real Codex allowance, and this record's
/// only purpose is to let an operator who knows their own plan turn Baton's derived token totals into
/// a percentage. Each field is independently absent: declaring only <see cref="WeeklyTokens"/> yields
/// a percentage on the 7-day window and none on the 5-hour one, rather than one borrowed from the
/// other. A zero or negative value is an operator typo — the same posture
/// <see cref="RunwayHoldSettings"/> takes for an out-of-range percentage — and reads as absent.
/// </summary>
public sealed record CodexPlanCeilingSettings
{
    public long? FiveHourTokens { get; init; }

    public long? WeeklyTokens { get; init; }

    /// <summary>The declared five-hour ceiling, or null when absent or non-positive.
    /// <c>[JsonIgnore]</c> because System.Text.Json serializes a public get-only property:
    /// <see cref="DaemonSettingsStore.SaveAsync"/> would otherwise write four keys into the operator's
    /// settings file where this record documents two.</summary>
    [JsonIgnore]
    public long? EffectiveFiveHourTokens => FiveHourTokens is > 0 ? FiveHourTokens : null;

    /// <summary>The declared weekly ceiling, or null when absent or non-positive — same
    /// <c>[JsonIgnore]</c> reason as above.</summary>
    [JsonIgnore]
    public long? EffectiveWeeklyTokens => WeeklyTokens is > 0 ? WeeklyTokens : null;
}

/// <summary>
/// #1946 — the daemon-served glass listener's configuration, and the ONE home for its key names and
/// its default port (README's daemon section cites this record rather than restating them).
/// <para>
/// <b><see cref="Listen"/> defaults to false, and that is the whole opt-in.</b> A machine that has
/// never been configured runs no listener at all: the tailnet plane is a surface the operator asks
/// for, not one that appears because the daemon was upgraded. The bind rule itself is not
/// configurable — loopback plus the machine's own tailnet address, never <c>0.0.0.0</c>
/// (spec/baton.md §11 C-11); <c>Baton.Cli.Daemon.GlassBindPolicy</c> is where that is decided and
/// there is deliberately no setting that can widen it.
/// </para>
/// </summary>
public sealed record GlassListenerSettings
{
    /// <summary>Whether <c>baton daemon</c> serves the glass at all. Off unless set.</summary>
    public bool Listen { get; init; }

    /// <summary>
    /// TCP port for every bound prefix. A zero, negative, or out-of-range value is an operator typo
    /// and reads as <see cref="DefaultPort"/> — the same posture <see cref="RunwayHoldSettings"/>
    /// takes for an out-of-range percentage, and for the same reason: <c>0</c> is the plausible typo
    /// and <see cref="System.Net.HttpListener"/> cannot bind an ephemeral port anyway.
    /// </summary>
    public int Port { get; init; } = DefaultPort;

    /// <summary>The port in force, after the typo fallback above.</summary>
    [JsonIgnore]
    public int EffectivePort => Port is > 0 and <= 65535 ? Port : DefaultPort;

    public const int DefaultPort = 8420;
}

/// <summary>One vendor's overrides of <see cref="RunwayHoldSettings"/>; every field is null-for-inherit.</summary>
public sealed record RunwayVendorHoldSettings
{
    public int? WeekHoldPct { get; init; }
    public int? SessionHoldPct { get; init; }
    public int? MaxSnapshotAgeHours { get; init; }
}

/// <summary>
/// Reads and writes <see cref="BatonPaths.SettingsFile"/>. Unlike <see cref="BatonProfileStore"/>, a
/// malformed file here is never fatal: a bad concurrency cap should not stop the daemon from starting
/// at all, so both an absent and a malformed file resolve to <see cref="DaemonSettings"/>'s defaults —
/// the latter after logging a warning so the operator can see it silently reset rather than wonder why
/// a cap they set stopped applying.
/// </summary>
public static class DaemonSettingsStore
{
    /// <summary>Loads settings from <paramref name="path"/>. Never throws: an absent file, an unreadable
    /// file, or one that fails to parse all resolve to <see cref="DaemonSettings"/>'s defaults, the last
    /// two after writing a warning to <see cref="Console.Error"/>.</summary>
    public static async Task<DaemonSettings> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (!File.Exists(path))
        {
            return new DaemonSettings();
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var settings = await JsonSerializer.DeserializeAsync<DaemonSettings>(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return settings ?? new DaemonSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Malformed or unreadable settings at '{path}', using defaults: {ex.Message}");
            return new DaemonSettings();
        }
    }

    /// <summary>Persists <paramref name="settings"/> to <paramref name="path"/>, creating parent directories as needed.</summary>
    public static async Task SaveAsync(DaemonSettings settings, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrEmpty(path);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, settings, new JsonSerializerOptions { WriteIndented = true }, cancellationToken)
            .ConfigureAwait(false);
    }
}
