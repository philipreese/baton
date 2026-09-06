using System.Text.Json;
using Baton.Cli.Mcp;
using Baton.Status;
using Baton.Vendors;
using Microsoft.Extensions.Hosting;

namespace Baton.Cli.Daemon;

/// <summary>
/// Issue #1391's daemon-side harvester: reads each vendor's own headless <c>/usage</c> report on the
/// cadence <see cref="VendorUsageHarvestScheduler"/> decides, and persists the latest snapshot per
/// vendor to <see cref="BatonPaths.VendorUsageSnapshotFile"/> — that property's own doc comment has
/// the restart-survival reasoning. Advisory only — nothing here gates dispatch (#1848 owns that);
/// this type only ever reads and writes, never blocks a worker.
/// <para>
/// It is not the only caller of <see cref="Persist"/> since #1923 — <c>OnDemandRunwayHarvest</c>
/// writes through this same method, for the same reason there is one snapshot format, when this
/// service has never produced a file for a vendor being dispatched to. Both read
/// <see cref="VendorUsageSources.Default"/>, so neither can be reading a vendor the other has never
/// heard of.
/// </para>
/// </summary>
/// <remarks>
/// Live-lane counts are read from the SAME room scan <see cref="FleetStatusTool.DiscoverRoomsAsync"/>/
/// <see cref="FleetStatusTool.ProcessRoomAsync"/> already do for <c>fleet_status</c> and
/// <see cref="FleetProjectionWriter"/> — a second, independent scan on this service's own tick rather
/// than threaded through from <see cref="FleetProjectionWriter"/>'s tick, so the two background
/// services stay decoupled (one's failure or interval change cannot affect the other's cadence).
/// </remarks>
public sealed class VendorUsageHarvester : BackgroundService
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan PeriodicInterval = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan Jitter = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan PostExitDelay = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan CoalesceWindow = TimeSpan.FromSeconds(60);

    /// <summary>
    /// #1966: the floor cadence for a vendor with NO live lane — thirty minutes, well inside the runway
    /// hold's six-hour staleness limit (<c>RunwayThresholds.EffectiveMaxSnapshotAge</c>), so an idle
    /// vendor's snapshot never ages out of being evidence. spec/baton.md §7 is the register for it and
    /// for what the extra <c>/usage</c> calls cost.
    /// </summary>
    public static readonly TimeSpan IdleInterval = TimeSpan.FromMinutes(30);

    private readonly IReadOnlyList<IVendorUsageSource> _sources;
    private readonly VendorUsageHarvestScheduler _scheduler;
    private readonly Func<CancellationToken, Task<Dictionary<string, int>>> _countLiveLanes;
    private readonly Func<string, IReadOnlyList<DateTimeOffset>> _readWindowBoundaries;

    public VendorUsageHarvester()
        : this(VendorUsageSources.Default)
    {
    }

    /// <summary>
    /// Test-only seam (Baton.Cli.Tests, via <c>InternalsVisibleTo</c>). <paramref name="countLiveLanes"/>
    /// substitutes for the room scan so <see cref="TickOnceAsync"/>'s three outcomes — scheduler says
    /// no, source returns null, source returns a snapshot — can be driven without fabricating a
    /// Running room per arm. The null-returns-nothing arm is #1869's red arm for
    /// "an errored harvest must not blank the last good snapshot".
    /// <paramref name="readWindowBoundaries"/> substitutes for the on-disk snapshot read so #1966's
    /// boundary trigger can be driven without persisting a fixture whose resets are relative to the
    /// suite's own clock.
    /// </summary>
    internal VendorUsageHarvester(
        IReadOnlyList<IVendorUsageSource> sources,
        VendorUsageHarvestScheduler? scheduler = null,
        Func<CancellationToken, Task<Dictionary<string, int>>>? countLiveLanes = null,
        Func<string, IReadOnlyList<DateTimeOffset>>? readWindowBoundaries = null)
    {
        _sources = sources;
        _scheduler = scheduler
            ?? new VendorUsageHarvestScheduler(PeriodicInterval, Jitter, PostExitDelay, CoalesceWindow, IdleInterval);
        _countLiveLanes = countLiveLanes ?? CountLiveLanesByVendorAsync;
        _readWindowBoundaries = readWindowBoundaries ?? ReadWindowBoundaries;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickOnceAsync(DateTimeOffset.UtcNow, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"VendorUsageHarvester: iteration failed: {ex.Message}");
            }

            try
            {
                await Task.Delay(TickInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// One tick's worth of work — internal entry point for tests, and what <see cref="ExecuteAsync"/>
    /// loops. <b>Strictly serial across vendors</b>, and that is a contract rather than an accident of
    /// the loop: every source spawns its vendor's own CLI, and two vendor CLIs running at once on the
    /// operator's machine is the cost #1966's every-vendor cadence would otherwise add. The
    /// <c>await</c> inside the loop is what enforces it; <c>VendorUsageHarvesterTests</c>' concurrency
    /// arm is what would notice a <c>Task.WhenAll</c> replacing it.
    /// </summary>
    internal async Task TickOnceAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var liveLanesByVendor = await _countLiveLanes(cancellationToken).ConfigureAwait(false);

        foreach (var source in _sources)
        {
            var anyLive = liveLanesByVendor.TryGetValue(source.Vendor, out var count) && count > 0;

            // Read BEFORE the harvest: after it, the boundaries would be the ones the snapshot this tick
            // just wrote names, which are in the future, so the trigger could never fire.
            var boundaries = _readWindowBoundaries(source.Vendor);
            if (!_scheduler.OnTick(source.Vendor, now, anyLive, boundaries))
            {
                continue;
            }

            VendorUsageSnapshot? snapshot;
            try
            {
                snapshot = await source.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"VendorUsageHarvester: harvest failed for {source.Vendor}: {ex.Message}");
                continue;
            }

            // Null means the vendor CLI did not produce a usable harvest (not spawned, non-zero exit,
            // or no output at all -- IVendorUsageSource.ReadAsync's contract). Skipping the write is
            // what keeps the LAST GOOD snapshot on disk instead of blanking it; pinned by
            // VendorUsageHarvesterTests' source-returns-null arm.
            if (snapshot is null)
            {
                continue;
            }

            Persist(source.Vendor, snapshot);
        }
    }

    /// <summary>
    /// The reset instants this vendor's last persisted snapshot names (#1966), read through the same
    /// <see cref="RunwaySnapshotReader"/> the runway hold reads — one on-disk snapshot format, one
    /// reader, so the cadence and the gate can never disagree about what was last harvested. A window
    /// whose <see cref="VendorUsageWindow.ResetsAt"/> did not parse contributes nothing rather than a
    /// guessed instant, which is #1391's "unparsed → unknown, never a number" applied here.
    /// </summary>
    private static IReadOnlyList<DateTimeOffset> ReadWindowBoundaries(string vendor) =>
        RunwaySnapshotReader.Read(vendor) is { } snapshot
            ? [.. snapshot.Windows.Where(w => w.ResetsAt is not null).Select(w => w.ResetsAt!.Value)]
            : [];

    private static async Task<Dictionary<string, int>> CountLiveLanesByVendorAsync(CancellationToken cancellationToken)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var discovered = await FleetStatusTool.DiscoverRoomsAsync([], cancellationToken).ConfigureAwait(false);
        foreach (var room in discovered)
        {
            var view = await FleetStatusTool.ProcessRoomAsync(room.RoomDir, includeTerminal: false, cancellationToken)
                .ConfigureAwait(false);
            if (view is null || view.State != "Running" || view.Adapter is not { } adapter)
            {
                continue;
            }

            counts[adapter] = counts.GetValueOrDefault(adapter) + 1;
        }

        return counts;
    }

    /// <summary>
    /// Serializes with the DEFAULT (PascalCase) options -- this file is machine-local persisted state
    /// this same process reads back (<see cref="VendorUsageProjectionReader"/>), never a wire contract,
    /// so it does not need the lowerCamelCase <c>JsonPropertyName</c> shape the fleet projection's own
    /// <c>vendors[]</c> block uses.
    /// <para>
    /// #1746: read-modify-write, because the file carries the per-window sample ring a burn rate needs
    /// two of. <see cref="VendorUsageBurn.Advance"/> owns every ring rule; this method only supplies
    /// the previous rings and fails OPEN when it cannot read them -- an unreadable or pre-#1746 file
    /// costs the history (rate absent until two fresh samples land), never the harvest itself.
    /// </para>
    /// <para>
    /// #1923 review: that read-modify-write is <b>serialized</b>, because this stopped being called only
    /// by the single-threaded daemon tick -- <c>OnDemandRunwayHarvest</c> now calls it from the runway
    /// hold, and <c>QueueLauncher</c> can evaluate that hold for two queued items at once in one
    /// process. <b>What the lock covers:</b> callers inside this process, which is where both new
    /// callers live. <b>What it does not:</b> two separate <c>baton</c> processes, which no in-process
    /// lock can. The residual there is bounded by the atomic <see cref="File.Move(string, string, bool)"/>
    /// below -- a reader never sees a torn file, and the worst outcome is one ring sample lost, which
    /// delays a burn rate rather than corrupting one. The hold decision reads windows, not rings, so it
    /// is unaffected either way.
    /// </para>
    /// </summary>
    internal static void Persist(string vendor, VendorUsageSnapshot snapshot)
    {
        lock (PersistLock)
        {
            PersistCore(vendor, snapshot);
        }
    }

    private static readonly object PersistLock = new();

    private static void PersistCore(string vendor, VendorUsageSnapshot snapshot)
    {
        try
        {
            var path = BatonPaths.VendorUsageSnapshotFile(vendor);
            var rings = VendorUsageBurn.Advance(ReadExistingRings(path), snapshot);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(new PersistedVendorUsage(
                snapshot.Vendor, snapshot.HarvestedAt, snapshot.Caveat, snapshot.Windows, rings, snapshot.Source));
            var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"VendorUsageHarvester: failed to persist snapshot for {vendor}: {ex.Message}");
        }
    }

    /// <summary>Previous rings, or null when there is no readable prior file (absent, pre-#1746, or
    /// corrupt) — see <see cref="Persist"/>'s fail-open note.</summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<VendorUsageSample>>? ReadExistingRings(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<PersistedVendorUsage>(File.ReadAllText(path))?.Rings
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
