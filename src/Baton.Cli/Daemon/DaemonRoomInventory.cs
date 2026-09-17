using Baton.Cli.Mcp;
using Baton.Status;
using Baton.Store;

namespace Baton.Cli.Daemon;

/// <summary>
/// The daemon-owned room-observation module. Its interface is deliberately small: callers choose
/// active rooms or the whole retained fleet, and whether a refresh must complete before the answer
/// is used. Discovery, refresh coalescing, terminal-room reuse, rerun detection, and eviction stay
/// behind this seam rather than being reimplemented by every hosted loop.
/// </summary>
internal sealed class DaemonRoomInventory
{
    internal static readonly TimeSpan ReuseWindow = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly Func<CancellationToken, Task<IReadOnlyList<FleetStatusTool.DiscoveredRoom>>> _discover;
    private readonly Func<string, bool, CancellationToken, Task<FleetRoomStatusView?>> _observe;
    private readonly Func<DiscoveryVersion> _discoveryVersion;
    private readonly Func<string, RoomVersion> _roomVersion;
    private readonly Func<string, bool> _roomExists;
    private readonly Func<DateTimeOffset> _now;

    private DiscoverySnapshot? _discovery;
    private Task<DiscoverySnapshot>? _discoveryRefresh;
    private ObservationSnapshot? _published;
    private Task<ObservationSnapshot>? _refresh;
    private readonly Dictionary<string, CachedTerminalObservation> _terminalCache =
        new(BatonPaths.RecordKeyComparer);

    internal DaemonRoomInventory()
        : this(
            cancellationToken => FleetStatusTool.DiscoverRoomsAsync([], cancellationToken),
            FleetStatusTool.ProcessRoomAsync,
            ReadDiscoveryVersion,
            ReadRoomVersion,
            Directory.Exists,
            () => DateTimeOffset.UtcNow)
    {
    }

    internal DaemonRoomInventory(
        Func<CancellationToken, Task<IReadOnlyList<FleetStatusTool.DiscoveredRoom>>> discover,
        Func<string, bool, CancellationToken, Task<FleetRoomStatusView?>> observe,
        Func<DiscoveryVersion> discoveryVersion,
        Func<string, RoomVersion> roomVersion,
        Func<string, bool> roomExists,
        Func<DateTimeOffset> now)
    {
        _discover = discover;
        _observe = observe;
        _discoveryVersion = discoveryVersion;
        _roomVersion = roomVersion;
        _roomExists = roomExists;
        _now = now;
    }

    /// <summary>
    /// Returns one immutable observation snapshot. <see cref="InventoryFreshness.Current"/> waits for
    /// a refresh and propagates refresh failure; the queue uses it because stale live-lane evidence
    /// could authorize an extra launch. <see cref="InventoryFreshness.LastComplete"/> may serve the
    /// prior complete snapshot while another caller refreshes, so slow projection/delivery work never
    /// owns a lock needed merely to read already-published evidence.
    /// </summary>
    internal async Task<IReadOnlyList<DaemonRoomObservation>> ObserveAsync(
        InventoryScope scope,
        InventoryFreshness freshness,
        CancellationToken cancellationToken)
    {
        ObservationSnapshot? published;
        Task<ObservationSnapshot>? refresh;
        TaskCompletionSource<ObservationSnapshot>? starter = null;

        lock (_gate)
        {
            published = _published;
            refresh = _refresh;

            if (published is not null && _now() - published.CompletedAt < ReuseWindow)
            {
                return SelectScope(published.Rooms, scope);
            }

            if (refresh is not null && freshness == InventoryFreshness.LastComplete && published is not null)
            {
                return SelectScope(published.Rooms, scope);
            }

            if (refresh is null)
            {
                starter = new TaskCompletionSource<ObservationSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
                refresh = starter.Task;
                _refresh = refresh;
            }
        }

        if (starter is not null)
        {
            _ = RefreshAndPublishAsync(starter);
        }

        var snapshot = await refresh!.WaitAsync(cancellationToken).ConfigureAwait(false);
        return SelectScope(snapshot.Rooms, scope);
    }

    private async Task RefreshAndPublishAsync(TaskCompletionSource<ObservationSnapshot> completion)
    {
        try
        {
            var discovered = await GetDiscoveryAsync().ConfigureAwait(false);
            var rooms = new List<DaemonRoomObservation>(discovered.Rooms.Count);
            var retainedKeys = new HashSet<string>(BatonPaths.RecordKeyComparer);

            using var roomScanPhase = DaemonLoopDriver.EnterPhase("room-scan");
            foreach (var room in discovered.Rooms)
            {
                if (!_roomExists(room.RoomDir))
                {
                    continue;
                }

                var key = BatonPaths.RecordKey(room.RoomDir);
                retainedKeys.Add(key);
                var version = _roomVersion(room.RoomDir);

                if (version.IsTerminal)
                {
                    CachedTerminalObservation? cached;
                    lock (_gate)
                    {
                        _terminalCache.TryGetValue(key, out cached);
                    }

                    if (cached is not null
                        && cached.Version == version
                        && string.Equals(cached.Project, room.Project, StringComparison.OrdinalIgnoreCase))
                    {
                        rooms.Add(new DaemonRoomObservation(room, cached.View, version));
                        continue;
                    }
                }

                var view = await _observe(room.RoomDir, true, CancellationToken.None)
                    .ConfigureAwait(false);
                if (view is null)
                {
                    continue;
                }

                if (room.Project is not null)
                {
                    view = view with { Project = room.Project };
                }

                rooms.Add(new DaemonRoomObservation(room, view, version));
                if (version.IsTerminal)
                {
                    lock (_gate)
                    {
                        _terminalCache[key] = new CachedTerminalObservation(version, room.Project, view);
                    }
                }
            }

            var snapshot = new ObservationSnapshot(rooms, _now());
            lock (_gate)
            {
                foreach (var stale in _terminalCache.Keys.Where(key => !retainedKeys.Contains(key)).ToList())
                {
                    _terminalCache.Remove(stale);
                }

                _published = snapshot;
            }

            completion.TrySetResult(snapshot);
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
        finally
        {
            lock (_gate)
            {
                _refresh = null;
            }
        }
    }

    private static IReadOnlyList<DaemonRoomObservation> SelectScope(
        IReadOnlyList<DaemonRoomObservation> rooms,
        InventoryScope scope) =>
        scope == InventoryScope.All
            ? rooms
            : rooms.Where(room => !room.Version.IsTerminal).ToList();

    private async Task<DiscoverySnapshot> GetDiscoveryAsync()
    {
        var version = _discoveryVersion();
        Task<DiscoverySnapshot>? refresh;
        TaskCompletionSource<DiscoverySnapshot>? starter = null;

        lock (_gate)
        {
            if (_discovery is not null && _discovery.Version == version)
            {
                return _discovery;
            }

            refresh = _discoveryRefresh;
            if (refresh is null)
            {
                starter = new TaskCompletionSource<DiscoverySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
                refresh = starter.Task;
                _discoveryRefresh = refresh;
            }
        }

        if (starter is not null)
        {
            _ = RefreshDiscoveryAsync(version, starter);
        }

        return await refresh.ConfigureAwait(false);
    }

    private async Task RefreshDiscoveryAsync(
        DiscoveryVersion version,
        TaskCompletionSource<DiscoverySnapshot> completion)
    {
        try
        {
            var rooms = await _discover(CancellationToken.None).ConfigureAwait(false);
            var snapshot = new DiscoverySnapshot(rooms, version);
            lock (_gate)
            {
                _discovery = snapshot;
            }

            completion.TrySetResult(snapshot);
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
        finally
        {
            lock (_gate)
            {
                _discoveryRefresh = null;
            }
        }
    }

    private static DiscoveryVersion ReadDiscoveryVersion() => new(
        FileVersion.ReadDirectory(BatonPaths.Rooms),
        FileVersion.ReadFile(BatonPaths.RoomRegistryFile));

    private static RoomVersion ReadRoomVersion(string roomDirectoryPath)
    {
        var terminal = FileVersion.ReadFile(
            Path.Combine(roomDirectoryPath, TerminalSentinelWriter.TerminalSentinelFileName));
        return new RoomVersion(
            terminal,
            FileVersion.ReadFile(Path.Combine(roomDirectoryPath, BatonPaths.RoomBindingsFileName)),
            FileVersion.ReadFile(Path.Combine(roomDirectoryPath, BatonPaths.FlowLogFileName)),
            FileVersion.ReadFile(Path.Combine(roomDirectoryPath, ".baton", BatonPaths.RoomMetadataFileName)));
    }

    internal enum InventoryScope
    {
        Active,
        All,
    }

    internal enum InventoryFreshness
    {
        LastComplete,
        Current,
    }

    internal readonly record struct FileVersion(bool Exists, long Length, long LastWriteTicks)
    {
        internal static FileVersion ReadFile(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists
                    ? new FileVersion(true, info.Length, info.LastWriteTimeUtc.Ticks)
                    : default;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new FileVersion(false, -1, DateTime.UtcNow.Ticks);
            }
        }

        internal static FileVersion ReadDirectory(string path)
        {
            try
            {
                var info = new DirectoryInfo(path);
                return info.Exists
                    ? new FileVersion(true, 0, info.LastWriteTimeUtc.Ticks)
                    : default;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new FileVersion(false, -1, DateTime.UtcNow.Ticks);
            }
        }
    }

    internal readonly record struct DiscoveryVersion(FileVersion Rooms, FileVersion Registry);

    internal readonly record struct RoomVersion(
        FileVersion Terminal,
        FileVersion Bindings,
        FileVersion Flow,
        FileVersion Metadata)
    {
        internal bool IsTerminal => Terminal.Exists;
    }

    private sealed record DiscoverySnapshot(
        IReadOnlyList<FleetStatusTool.DiscoveredRoom> Rooms,
        DiscoveryVersion Version);

    private sealed record ObservationSnapshot(
        IReadOnlyList<DaemonRoomObservation> Rooms,
        DateTimeOffset CompletedAt);

    private sealed record CachedTerminalObservation(
        RoomVersion Version,
        string? Project,
        FleetRoomStatusView View);
}

internal sealed record DaemonRoomObservation(
    FleetStatusTool.DiscoveredRoom Room,
    FleetRoomStatusView View,
    DaemonRoomInventory.RoomVersion Version);
