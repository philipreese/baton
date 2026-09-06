using System.Diagnostics;
using Baton.Vendors;
using Baton.Artifacts;
using Baton.Cli;
using Baton.Projection;
using Baton.Status;
using Baton.Store;
using Microsoft.Extensions.Hosting;

namespace Baton.Cli.Daemon;

/// <summary>
/// Background daemon service that periodically sweeps resident rooms to compact completed room journals (#1025)
/// and prune artifacts for terminal runs (#1027).
/// </summary>
public sealed class RoomRetentionSweep : BackgroundService
{
    /// <summary>
    /// #1659: the same <see cref="DaemonSettings"/> <c>DaemonHost</c> already loads for the concurrency
    /// caps, DI-injected here so this service can read <see cref="DaemonSettings.RoomsRetentionDays"/>
    /// without a second settings load. <c>null</c> (every unit test's shape, via the parameterless
    /// constructor) means "no retention config available" — <see cref="ResolveRoomsRetentionDays"/>
    /// treats that exactly like an explicit <c>null</c> setting: retention prune stays off.
    /// </summary>
    private readonly DaemonSettings? _settings;

    public RoomRetentionSweep()
        : this(settings: null)
    {
    }

    public RoomRetentionSweep(DaemonSettings? settings)
    {
        _settings = settings;
    }

    public const string EnabledEnvironmentVariable = "BATON_RETENTION_SWEEP_ENABLED";
    public const string IntervalSecondsEnvironmentVariable = "BATON_RETENTION_SWEEP_INTERVAL_SECONDS";
    public const string ThresholdBytesEnvironmentVariable = "BATON_RETENTION_SWEEP_THRESHOLD_BYTES";
    public const string PruneEnabledEnvironmentVariable = "BATON_RETENTION_PRUNE_ENABLED";
    public const string PruneGraceSecondsEnvironmentVariable = "BATON_RETENTION_PRUNE_GRACE_SECONDS";

    public static readonly TimeSpan PlaceholderDefaultInterval = TimeSpan.FromMinutes(5);
    public const long PlaceholderDefaultThresholdBytes = 1_048_576; // 1 MB placeholder
    public static readonly TimeSpan PlaceholderDefaultPruneGrace = TimeSpan.FromHours(1);

    // Bounds on the parsed interval, both ends load-bearing:
    //  - Upper: without it a pathological value (e.g. "1e300", "Infinity") reaches TimeSpan.FromSeconds,
    //    which throws OverflowException — and GetInterval() is called from ExecuteAsync's delay, whose only
    //    catch is OperationCanceledException, so the overflow would fault the BackgroundService and stop the
    //    whole daemon on a typo. A retention sweep never legitimately waits >1 day.
    //  - Lower: a sub-second typo (e.g. "1e-9") floors TimeSpan.FromSeconds to ~Zero, and Task.Delay(Zero)
    //    returns immediately, hot-looping ExecuteAsync so it re-enumerates every room continuously. One
    //    second is far below the placeholder cadence yet keeps the loop a loop, not a spin.
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxInterval = TimeSpan.FromDays(1);

    // Bounds on parsed prune grace:
    //  - Upper: prevents pathological values (e.g. "1e300") from overflowing TimeSpan.FromSeconds.
    //  - Lower: floors sub-second values (e.g. "1e-9") to 1s to prevent immediate pruning on a typo.
    public static readonly TimeSpan MinPruneGrace = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxPruneGrace = TimeSpan.FromDays(365);

    // record-once-ok: #1524 src/Baton/Status/BatonEnvironmentSnapshot.cs
    // #1524: folded into BatonEnvironmentSnapshot (this method and the four below -- IsPruneEnabled,
    // GetInterval, GetThresholdBytes, GetPruneGrace).
    public static bool IsEnabled()
    {
        var val = BatonEnvironmentSnapshot.Current.RetentionSweepEnabledOverride;
        return string.Equals(val, "true", StringComparison.OrdinalIgnoreCase) || val == "1";
    }

    public static bool IsPruneEnabled()
    {
        var val = BatonEnvironmentSnapshot.Current.RetentionPruneEnabledOverride;
        return string.Equals(val, "true", StringComparison.OrdinalIgnoreCase) || val == "1";
    }

    public static TimeSpan GetInterval()
    {
        var val = BatonEnvironmentSnapshot.Current.RetentionSweepIntervalSecondsOverride;
        if (!string.IsNullOrWhiteSpace(val) &&
            double.TryParse(val, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var seconds) &&
            seconds > 0)
        {
            // Clamp before FromSeconds: honors intent within [Min, Max], collapses Infinity/huge finite to
            // Max (no overflow) and sub-second values to Min (no hot-loop). NaN fails seconds > 0 above, so
            // Math.Clamp never sees it.
            return TimeSpan.FromSeconds(Math.Clamp(seconds, MinInterval.TotalSeconds, MaxInterval.TotalSeconds));
        }

        return PlaceholderDefaultInterval;
    }

    public static long GetThresholdBytes()
    {
        var val = BatonEnvironmentSnapshot.Current.RetentionSweepThresholdBytesOverride;
        if (!string.IsNullOrWhiteSpace(val) &&
            long.TryParse(val, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var bytes) &&
            bytes >= 0)
        {
            return bytes;
        }

        return PlaceholderDefaultThresholdBytes;
    }

    public static TimeSpan GetPruneGrace()
    {
        var val = BatonEnvironmentSnapshot.Current.RetentionPruneGraceSecondsOverride;
        if (!string.IsNullOrWhiteSpace(val) &&
            double.TryParse(val, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var seconds) &&
            seconds > 0)
        {
            return TimeSpan.FromSeconds(Math.Clamp(seconds, MinPruneGrace.TotalSeconds, MaxPruneGrace.TotalSeconds));
        }

        return PlaceholderDefaultPruneGrace;
    }

    public async Task<(int CompactedCount, int PrunedCount)> ExecuteSingleSweepAsync(
        string? roomsDirectoryOverride = null,
        long? thresholdBytesOverride = null,
        TimeSpan? graceOverride = null,
        bool? compactionEnabledOverride = null,
        bool? pruneEnabledOverride = null,
        CancellationToken cancellationToken = default)
    {
        // #1524 review rider (from #1526): BatonPaths.Rooms resolves through BatonPaths.Root, which
        // has read BatonEnvironmentSnapshot.Current since #1496 -- so this re-resolves every sweep
        // iteration across the daemon's multi-hour life, but pins to the ONE process snapshot taken
        // at first access, not to whatever BATON_HOME held at that iteration's start. Harmless today
        // (nothing mutates BATON_HOME in-process, and the OS env block can't change under a running
        // process); revisit this call if a daemon config-reload path is ever built.
        var roomsDir = roomsDirectoryOverride ?? BatonPaths.Rooms;
        if (!Directory.Exists(roomsDir))
        {
            return (0, 0);
        }

        string[] roomDirs;
        try
        {
            roomDirs = Directory.GetDirectories(roomsDir);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoomRetentionSweep: Failed to enumerate rooms directory '{roomsDir}': {ex.Message}");
            return (0, 0);
        }

        var thresholdBytes = thresholdBytesOverride ?? GetThresholdBytes();
        var grace = graceOverride ?? GetPruneGrace();

        // Enable resolution, per operation: an explicit *EnabledOverride wins; else passing a value override
        // (threshold/grace) implies "on" so a test can exercise one operation without setting env flags; else
        // the env flag. NOTE the coupling this creates: a future non-test caller that passes ONLY a value
        // override would force that operation on regardless of BATON_RETENTION_*_ENABLED. The sole production
        // caller (ExecuteAsync) passes no overrides, so in production the env flags stay authoritative — but a
        // caller wanting to tune a value while honouring the flag must pass the matching *EnabledOverride too.
        var compactionEnabled = compactionEnabledOverride ?? (thresholdBytesOverride.HasValue || IsEnabled());
        var pruneEnabled = pruneEnabledOverride ?? (graceOverride.HasValue || IsPruneEnabled());

        var compactedCount = 0;
        var prunedCount = 0;

        foreach (var roomDir in roomDirs)
        {
            if (compactionEnabled)
            {
                try
                {
                    if (await SweepRoomAsync(roomDir, thresholdBytes, cancellationToken).ConfigureAwait(false))
                    {
                        compactedCount++;
                    }
                }
                catch (OperationCanceledException)
                {
                    // Shutdown (or a caller-cancelled token) must unwind the whole sweep, not be logged as a
                    // per-room compaction error and swallowed so the loop marches to the next room.
                    throw;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"RoomRetentionSweep: Error compacting room at '{roomDir}': {ex.Message}");
                }
            }

            if (pruneEnabled)
            {
                try
                {
                    if (await PruneRoomAsync(roomDir, grace, cancellationToken).ConfigureAwait(false))
                    {
                        prunedCount++;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"RoomRetentionSweep: Error pruning room at '{roomDir}': {ex.Message}");
                }
            }
        }

        return (compactedCount, prunedCount);
    }

    internal static async Task<bool> SweepRoomAsync(
        string roomDirectoryPath,
        long thresholdBytes,
        CancellationToken cancellationToken = default)
    {
        var roomLogPath = Path.Combine(roomDirectoryPath, BatonPaths.RoomLogFileName);
        if (!File.Exists(roomLogPath))
        {
            return false;
        }

        var fileInfo = new FileInfo(roomLogPath);
        if (fileInfo.Length < thresholdBytes)
        {
            return false;
        }

        return await RoomJournalCompactor.CompactAsync(roomDirectoryPath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// #1157: rooms already warned about a legacy (pre-#745) journal, so the fallback below says so
    /// once per room rather than once per sweep iteration — at the placeholder five-minute cadence
    /// that is 288 identical lines a day, per room, which is how a real signal becomes noise nobody
    /// reads. Process-lifetime only: a daemon restart re-warns, deliberately, since the operator who
    /// reads the new process's stderr has not necessarily seen the old one's.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> LegacyTerminalInstantWarnedRooms =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Prunes <paramref name="roomDirectoryPath"/>'s artifacts once its run has been terminal for at
    /// least <paramref name="grace"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// #1157 retired the <c>flow.jsonl</c>-mtime proxy this used to key on. The grace window now reads
    /// the run's real terminal instant (<see cref="WorkflowProbeResult.TerminalAtUtc"/>), so a journal
    /// touched after the run ended — a late settlement line, a Core lifecycle line, a file copy that
    /// moved the mtime without adding a byte — no longer silently restarts the window and leaves a
    /// finished room's artifacts unswept indefinitely.
    /// </para>
    /// <para>
    /// <b>Behaviour change worth naming: a non-terminal room is now never pruned here.</b> The old
    /// gate admitted any room whose journal was quiet for <paramref name="grace"/>, terminal or not,
    /// and leaned on <see cref="ArtifactPruner.PruneAsync"/>'s own terminality probe to refuse. That is
    /// still the authoritative refusal (it runs under the room's <see cref="Baton.Concurrency.ConcurrencyGuard"/>, where
    /// this pre-filter cannot); what changes is that the answer is now reached honestly rather than by
    /// two gates disagreeing about what the mtime meant. A room with no terminal instant has not ended,
    /// and per spec/baton.md §3 nothing may invent one for it.
    /// </para>
    /// <para>
    /// The cost is one journal read per room per sweep that the mtime stat did not pay, plus
    /// <see cref="ArtifactPruner"/>'s own second read under the lock for the rooms that pass. Not
    /// deduplicated into one read on purpose: the two probes answer at different moments and the
    /// second one is the one holding the lock, which is exactly the property #973's second reader
    /// added it for.
    /// </para>
    /// </remarks>
    /// <param name="warningSink">
    /// Where the legacy-journal warning goes; <c>null</c> means <see cref="Console.Error"/>, which is
    /// what the daemon uses. A parameter rather than a swap of the process-global
    /// <see cref="Console.Error"/>, because a test asserting "exactly one line" against a stream every
    /// other test class in this assembly also writes to is asserting about scheduling, not about this
    /// gate (the defect <c>Baton.Tests</c>' own <c>ConsoleErrorCaptureCollection</c> exists for).
    /// </param>
    internal static async Task<bool> PruneRoomAsync(
        string roomDirectoryPath,
        TimeSpan grace,
        CancellationToken cancellationToken = default,
        TextWriter? warningSink = null)
    {
        var flowLogPath = Path.Combine(roomDirectoryPath, BatonPaths.FlowLogFileName);
        if (!File.Exists(flowLogPath))
        {
            return false;
        }

        var probe = await WorkflowTerminalProbe.ProbeAsync(roomDirectoryPath, cancellationToken).ConfigureAwait(false);
        if (!probe.IsTerminal)
        {
            return false;
        }

        DateTime terminalAtUtc;
        if (probe.TerminalAtUtc is { } instant)
        {
            terminalAtUtc = instant;
        }
        else
        {
            // The ONLY surviving use of the mtime here, for the two terminal-but-no-instant shapes:
            // refusing to prune such a room forever would be a worse answer than the proxy that has
            // been deciding it all along. Announced once per room (see LegacyTerminalInstantWarnedRooms)
            // so the population still on old journals is visible rather than inferred -- and announced
            // with the RIGHT cause, which is why the resolver reports one. Attributing every absent
            // instant to #745 made the census this warning exists to produce simply wrong: a zero-step
            // snapshot projects terminal off an empty prefix, so a room whose journal was written
            // minutes ago reaches this branch too, and blaming its age on a five-versions-old writer is
            // a false diagnostic an operator would act on.
            var cause = probe.TerminalAtAbsence switch
            {
                TerminalInstantAbsence.TransitionEntryUnstamped =>
                    "its journal predates writer timestamps (#745)",
                TerminalInstantAbsence.NoTransitionEntry =>
                    "no journal line ever transitioned it to terminal (a zero-step workflow projects " +
                    "terminal off its empty journal)",
                // Unreachable: this branch runs only under probe.IsTerminal, and the probe reports
                // NotTerminal/None only outside it. Named rather than defaulted so a future absence
                // reason cannot silently inherit one of the sentences above.
                var other => $"its terminal instant is unavailable ({other})",
            };

            if (LegacyTerminalInstantWarnedRooms.TryAdd(roomDirectoryPath, 0))
            {
                (warningSink ?? Console.Error).WriteLine(
                    $"RoomRetentionSweep: '{roomDirectoryPath}' is terminal but {cause}, so the retention " +
                    $"grace window is falling back to {BatonPaths.FlowLogFileName}'s mtime for this room. " +
                    "Reported once per room per daemon process.");
            }

            terminalAtUtc = File.GetLastWriteTimeUtc(flowLogPath);
        }

        if (DateTime.UtcNow - terminalAtUtc < grace)
        {
            return false;
        }

        // See #1027 follow-ups: operator pruned/ view remains open; operator KeepMarker setting
        // shipped as `baton keep`/`baton unkeep` (#1156).
        return await ArtifactPruner.PruneAsync(roomDirectoryPath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// #1659: <see cref="DaemonSettings.RoomsRetentionDays"/>, or <c>null</c> when unset/non-positive —
    /// the daemon's own "off unless the operator opts in" default. Never reads an environment
    /// variable, unlike every sibling <c>Get*</c>/<c>Is*</c> resolver above: this setting arrives
    /// through <c>settings.json</c> (<see cref="DaemonSettingsStore"/>), the config surface the issue
    /// asked for, not a new env var.
    /// </summary>
    internal int? ResolveRoomsRetentionDays(int? roomsRetentionDaysOverride = null)
    {
        var days = roomsRetentionDaysOverride ?? _settings?.RoomsRetentionDays;
        return days is > 0 ? days : null;
    }

    /// <summary>
    /// #1659: runs <c>baton rooms prune --terminal --older-than &lt;RoomsRetentionDays&gt; --yes</c>'s
    /// own logic directly (<see cref="RoomsPruneCommand.ExecuteAsync"/>) rather than shelling out to
    /// the CLI, the same in-process reuse every other daemon-hosted service in this tree already
    /// follows. A no-op (returns 0, touches nothing) when <see cref="ResolveRoomsRetentionDays"/>
    /// resolves to <c>null</c>. Per-room output is discarded (<see cref="TextWriter.Null"/>) — this is
    /// a background sweep, not an operator-typed command with something to print to; only a failure is
    /// reported, on stderr, the same posture every other per-operation catch in this type takes.
    /// </summary>
    public async Task<int> ExecuteRoomsRetentionPruneAsync(
        string? registryFilePathOverride = null,
        int? roomsRetentionDaysOverride = null,
        CancellationToken cancellationToken = default)
    {
        var roomsRetentionDays = ResolveRoomsRetentionDays(roomsRetentionDaysOverride);
        if (roomsRetentionDays is null)
        {
            return 0;
        }

        var registryFilePath = registryFilePathOverride ?? BatonPaths.RoomRegistryFile;
        var options = new RoomsPruneOptions(Terminal: true, OlderThanDays: roomsRetentionDays, State: null, DryRun: false, Yes: true);

        try
        {
            var result = await RoomsPruneCommand
                .ExecuteAsync(options, TextWriter.Null, registryFilePath, cancellationToken)
                .ConfigureAwait(false);
            return result.Deleted.Count;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoomRetentionSweep: Error running rooms-retention prune: {ex.Message}");
            return 0;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var started = Stopwatch.GetTimestamp();
            if (IsEnabled() || IsPruneEnabled())
            {
                try
                {
                    await ExecuteSingleSweepAsync(cancellationToken: stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"RoomRetentionSweep sweep iteration failed: {ex.Message}");
                }
            }

            if (ResolveRoomsRetentionDays() is not null)
            {
                try
                {
                    await ExecuteRoomsRetentionPruneAsync(cancellationToken: stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
            }

            // #1981: see DaemonTickLedger for why every service reports its tick here. A pass with
            // both halves disabled still reports — the loop turning over is what this measures.
            DaemonTickLedger.Instance.RecordTick(
                nameof(RoomRetentionSweep), Stopwatch.GetElapsedTime(started), GetInterval());

            try
            {
                await Task.Delay(GetInterval(), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}

