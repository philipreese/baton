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

    private readonly IReadOnlyList<IVendorUsageSource> _sources;
    private readonly VendorUsageHarvestScheduler _scheduler;
    private readonly Func<CancellationToken, Task<Dictionary<string, int>>> _countLiveLanes;

    public VendorUsageHarvester()
        : this([new ClaudeUsageSlashCommandSource(), new AgyUsageSlashCommandSource(), new CodexUsageSource()])
    {
    }

    /// <summary>
    /// Test-only seam (Baton.Cli.Tests, via <c>InternalsVisibleTo</c>). <paramref name="countLiveLanes"/>
    /// substitutes for the room scan so <see cref="TickOnceAsync"/>'s three outcomes — scheduler says
    /// no, source returns null, source returns a snapshot — can be driven without fabricating a
    /// Running room per arm. The null-returns-nothing arm is #1869's red arm for
    /// "an errored harvest must not blank the last good snapshot".
    /// </summary>
    internal VendorUsageHarvester(
        IReadOnlyList<IVendorUsageSource> sources,
        VendorUsageHarvestScheduler? scheduler = null,
        Func<CancellationToken, Task<Dictionary<string, int>>>? countLiveLanes = null)
    {
        _sources = sources;
        _scheduler = scheduler ?? new VendorUsageHarvestScheduler(PeriodicInterval, Jitter, PostExitDelay, CoalesceWindow);
        _countLiveLanes = countLiveLanes ?? CountLiveLanesByVendorAsync;
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

    /// <summary>One tick's worth of work — public entry point for tests, and what <see cref="ExecuteAsync"/> loops.</summary>
    internal async Task TickOnceAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var liveLanesByVendor = await _countLiveLanes(cancellationToken).ConfigureAwait(false);

        foreach (var source in _sources)
        {
            var anyLive = liveLanesByVendor.TryGetValue(source.Vendor, out var count) && count > 0;
            if (!_scheduler.OnTick(source.Vendor, now, anyLive))
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
    /// </summary>
    internal static void Persist(string vendor, VendorUsageSnapshot snapshot)
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
