using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Baton.Status;

namespace Baton.Cli.Daemon;

/// <summary>
/// #1981 — what every hosted service in the daemon reports its ticks to, and the source
/// <see cref="DaemonWatchdog"/> judges and <see cref="BatonPaths.FleetHeartbeatFile"/> is written
/// from.
/// <para>
/// The incident this exists for: on 2026-09-06 the daemon stopped writing anything at 14:51 and was
/// only noticed thirteen minutes later, by a person, from the glass. The process was alive, the
/// scheduled task said Running, and `daemon.log` had gone quiet — there was no reading anywhere that
/// said "no loop has completed a pass since 14:51", which is the one fact that would have caught it.
/// </para>
/// <para>
/// <b>In-memory, and the watchdog reads it here rather than re-reading the file it produces.</b> The
/// file is for outside readers; the watchdog is inside the same process, and a read of its own
/// heartbeat would add a filesystem call to the one code path that must keep working when the
/// filesystem is what wedged. A tick that ran but whose heartbeat write failed is a different fault
/// (and is logged where the write happens), not a hang.
/// </para>
/// <para>
/// Every service records even when its tick threw: the loop being alive is what this measures, not
/// the tick succeeding. A service whose work throws every time is loud in the log already.
/// </para>
/// </summary>
internal sealed class DaemonTickLedger
{
    /// <summary>The process-wide instance every hosted service records into. A static rather than a DI
    /// singleton because <c>FleetProjectionWriter</c> and its siblings are registered with
    /// parameterless constructors that the daemon host does not build by hand, and adding a
    /// constructor-injected dependency to all six to reach one shared counter would be a wider change
    /// than the counter is worth.</summary>
    internal static DaemonTickLedger Instance { get; } = new(() => DateTimeOffset.UtcNow);

    private readonly Func<DateTimeOffset> _clock;
    private readonly Action<string> _log;
    private readonly ConcurrentDictionary<string, ServiceTick> _ticks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ActiveTickRegistration> _active = new(StringComparer.Ordinal);
    private readonly DateTimeOffset _startedAt;
    private IReadOnlyList<string>? _glassBoundPrefixes;

    internal DaemonTickLedger(Func<DateTimeOffset> clock, Action<string>? log = null)
    {
        _clock = clock;
        _log = log ?? Console.Error.WriteLine;
        _startedAt = clock();
    }

    /// <summary>When the process came up — the watchdog's baseline before any service has completed a
    /// first tick, so a daemon that wedges during startup still trips instead of being read as "no
    /// ticks expected yet".</summary>
    internal DateTimeOffset StartedAt => _startedAt;

    /// <summary>One completed pass of <paramref name="service"/>'s loop. <paramref name="interval"/> is
    /// that service's own cadence — the ledger keeps it so the heartbeat file and (since the same
    /// issue's logging change) the over-interval line can be judged per service rather than against one
    /// fleet-wide number.</summary>
    internal void RecordTick(
        string service,
        TimeSpan elapsed,
        TimeSpan interval,
        IReadOnlyList<DaemonPhase>? latePhases = null,
        IReadOnlyList<InFlightSample>? lateSamples = null)
    {
        var completedAt = _clock();
        var phases = latePhases is { Count: > 0 } ? [.. latePhases] : Array.Empty<DaemonPhase>();
        var samples = lateSamples is { Count: > 0 } ? [.. lateSamples] : Array.Empty<InFlightSample>();
        _ticks[service] = new ServiceTick(completedAt, elapsed, interval, phases, samples);

        // #1981's third item: a tick that outruns its own interval is the growth the 2026-09-06 stall
        // left no trace of -- `daemon.log` could not say whether the room walk had been getting slower
        // for an hour or seized in one second. Logged only when it EXCEEDS the interval, so a healthy
        // daemon adds no lines at all: a service that ticks inside its cadence is the normal case and
        // logging it every pass would bury the signal in exactly the file that has to stay readable.
        if (interval > TimeSpan.Zero && elapsed > interval)
        {
            var attribution = phases.Length == 0
                ? "; no phase attribution was captured"
                : "; phases: " + string.Join(
                    ", ", phases.Select(phase =>
                        $"{phase.Name}={phase.Elapsed.TotalSeconds:F1}s/{phase.Count}x"));
            var sampled = samples.Length == 0
                ? string.Empty
                : $"; independently sampled {samples.Length}x while in flight, latest: "
                  + Describe(samples[^1]);
            _log($"{service}: tick took {elapsed.TotalSeconds:F1}s, longer than its own "
                 + $"{interval.TotalSeconds:F0}s interval{attribution}{sampled}");
        }
    }

    /// <summary>Registers one currently executing loop pass. The watchdog samples these registrations
    /// from its dedicated thread, so thread-pool starvation can be observed while the affected loop
    /// is still waiting rather than reconstructed only after it returns.</summary>
    internal ActiveTickRegistration BeginTick(string service, Func<ActiveTick> snapshot)
    {
        var registration = new ActiveTickRegistration(this, service, snapshot);
        _active[service] = registration;
        return registration;
    }

    /// <summary>Captures overdue in-flight operations with the host counters sampled on the watchdog's
    /// dedicated thread. Samples stay in memory and ride the completed tick's ordinary log/heartbeat;
    /// the watchdog itself performs no extra filesystem or console write on a healthy pass.</summary>
    internal void SampleActive(HostLoadSample load)
    {
        foreach (var registration in _active.Values)
        {
            var active = registration.Snapshot();
            if (active.Elapsed > active.Interval)
            {
                registration.AddSample(new InFlightSample(
                    load.SampledAt,
                    active.Phase,
                    active.PhaseElapsed,
                    load));
            }
        }
    }

    internal IReadOnlyList<ActiveTick> SnapshotActive() =>
        [.. _active.Values.Select(registration => registration.Snapshot())];

    /// <summary>Every service's last completed tick, newest first. A snapshot — the watchdog reasons
    /// over a stable list rather than a dictionary that can change under it mid-verdict.</summary>
    internal IReadOnlyList<ServiceTick> Snapshot() =>
        [.. _ticks.Select(kv => kv.Value with { Service = kv.Key }).OrderByDescending(t => t.CompletedAt)];

    /// <summary>The shortest cadence any service has reported so far, or null before the first tick.
    /// What <see cref="DaemonWatchdog"/>'s fleet-silence arm is keyed on (#2082): read from the ticks
    /// themselves rather than a list of registered services, so a service that is added, removed or
    /// re-timed changes the bound without anyone remembering to update a constant.</summary>
    internal TimeSpan? ShortestInterval()
    {
        TimeSpan? shortest = null;
        foreach (var tick in _ticks.Values)
        {
            if (tick.Interval > TimeSpan.Zero && (shortest is null || tick.Interval < shortest))
            {
                shortest = tick.Interval;
            }
        }

        return shortest;
    }

    /// <summary>
    /// The heartbeat file's body — spec/baton.md §7 ("The daemon watches itself") is the schema's one
    /// statement. <c>tickCompletedAt</c> is the most recent completion across every service (the
    /// single field an outside reader needs to answer "is this daemon still turning over"); per
    /// service, the duration of its own last tick in milliseconds beside when it finished; and
    /// <paramref name="load"/> as <c>hostLoad</c>, the host as it looked when this body was rendered.
    /// </summary>

    /// <summary>The prefixes <see cref="GlassHttpService"/> actually bound, last reported -- #2130,
    /// spec/baton.md §7 states why a reverse proxy needs this. Null until that service has run at
    /// least once (off, or not yet at its first tick); empty once it has run with every bind
    /// refused, distinct from never having been configured at all.</summary>
    internal void RecordGlassBoundPrefixes(IReadOnlyList<string> prefixes) =>
        Volatile.Write(ref _glassBoundPrefixes, prefixes);

    internal string RenderHeartbeatJson(HostLoadSample load)
    {
        var services = new JsonObject();
        DateTimeOffset? newest = null;
        foreach (var tick in Snapshot())
        {
            newest ??= tick.CompletedAt;
            var service = new JsonObject
            {
                ["lastTickMs"] = Math.Round(tick.Elapsed.TotalMilliseconds, 1),
                ["completedAt"] = tick.CompletedAt.ToString("O"),
                ["intervalMs"] = Math.Round(tick.Interval.TotalMilliseconds, 1),
            };
            if (tick.LatePhases.Count > 0)
            {
                service["latePhases"] = new JsonArray(
                    [.. tick.LatePhases.Select(phase => (JsonNode?)new JsonObject
                    {
                        ["name"] = phase.Name,
                        ["elapsedMs"] = Math.Round(phase.Elapsed.TotalMilliseconds, 1),
                        ["count"] = phase.Count,
                    })]);
            }
            if (tick.LateSamples.Count > 0)
            {
                service["lateSamples"] = new JsonArray(
                    [.. tick.LateSamples.Select(sample => (JsonNode?)new JsonObject
                    {
                        ["sampledAt"] = sample.SampledAt.ToString("O"),
                        ["phase"] = sample.Phase,
                        ["phaseElapsedMs"] = Math.Round(sample.PhaseElapsed.TotalMilliseconds, 1),
                        ["hostLoad"] = sample.HostLoad.ToJson(),
                    })]);
            }

            services[tick.Service] = service;
        }

        var body = new JsonObject
        {
            // Never fabricated: before any service has completed a tick this is the process start
            // time, which is what "nothing has completed since" honestly means at that point.
            ["tickCompletedAt"] = (newest ?? _startedAt).ToString("O"),
            ["startedAt"] = _startedAt.ToString("O"),
            ["services"] = services,
            ["hostLoad"] = load.ToJson(),
        };

        // Omitted, not an empty array, when the glass listener has never run a tick -- "not
        // configured" must not read the same as "configured and every prefix refused".
        if (Volatile.Read(ref _glassBoundPrefixes) is { } prefixes)
        {
            body["glassBoundPrefixes"] = new JsonArray([.. prefixes.Select(p => (JsonNode?)p)]);
        }

        return body.ToJsonString();
    }

    /// <summary>One service's most recent completed tick. <see cref="Service"/> is filled in by
    /// <see cref="Snapshot"/> from the dictionary key, so the stored value carries no copy of it.</summary>
    internal sealed record ServiceTick(
        DateTimeOffset CompletedAt,
        TimeSpan Elapsed,
        TimeSpan Interval,
        IReadOnlyList<DaemonPhase> LatePhases,
        IReadOnlyList<InFlightSample> LateSamples)
    {
        internal string Service { get; init; } = string.Empty;
    }

    /// <summary>A completed named operation inside a late tick.</summary>
    internal sealed record DaemonPhase(string Name, TimeSpan Elapsed, int Count = 1);

    internal sealed record ActiveTick(
        string Service,
        TimeSpan Elapsed,
        TimeSpan Interval,
        string? Phase,
        TimeSpan PhaseElapsed);

    internal sealed record InFlightSample(
        DateTimeOffset SampledAt,
        string? Phase,
        TimeSpan PhaseElapsed,
        HostLoadSample HostLoad);

    internal sealed class ActiveTickRegistration
    {
        private const int MaxSamples = 8;
        private readonly DaemonTickLedger _owner;
        private readonly string _service;
        private readonly Func<ActiveTick> _snapshot;
        private readonly object _gate = new();
        private readonly List<InFlightSample> _samples = [];
        private int _completed;

        internal ActiveTickRegistration(DaemonTickLedger owner, string service, Func<ActiveTick> snapshot)
        {
            _owner = owner;
            _service = service;
            _snapshot = snapshot;
        }

        internal ActiveTick Snapshot() => _snapshot();

        internal void AddSample(InFlightSample sample)
        {
            lock (_gate)
            {
                if (Volatile.Read(ref _completed) != 0)
                {
                    return;
                }

                if (_samples.Count == MaxSamples)
                {
                    _samples.RemoveAt(0);
                }

                _samples.Add(sample);
            }
        }

        internal IReadOnlyList<InFlightSample> Complete()
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0
                && _owner._active.TryGetValue(_service, out var current)
                && ReferenceEquals(current, this))
            {
                _owner._active.TryRemove(_service, out _);
            }

            lock (_gate)
            {
                return [.. _samples];
            }
        }
    }

    private static string Describe(InFlightSample sample)
    {
        var phase = sample.Phase is null
            ? "no named phase"
            : $"{sample.Phase} for {sample.PhaseElapsed.TotalSeconds:F1}s";
        return $"{phase}; {sample.HostLoad.Describe()}";
    }
}
