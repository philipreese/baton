using Baton.Status;
using Baton.Cli.Daemon;

namespace Baton.Cli;

/// <summary>
/// <c>baton watch</c> (#1488): block-free registration of a one-shot notification for when a room
/// reaches Terminal — spec/baton.md §2 has the full contract. Not a
/// <see cref="Baton.CommandResult"/>/<see cref="FlowStateReporter"/> command: registering, listing,
/// and clearing watches produce no projected <see cref="Baton.Domain.FlowState"/>, the same carve-out
/// <c>keep</c>/<c>unkeep</c>/<c>room</c>/<c>rooms</c> already take in <c>Program.cs</c>.
/// </summary>
public static class WatchCommand
{
    /// <param name="notifier">Test seam (<c>WatchCommandTests</c> passes a
    /// <c>RecordingWatchNotifier</c>) — <c>null</c> in production, where registration uses a real
    /// <see cref="WatchNotifier"/> for the "no lost wake-up" immediate-fire check.</param>
    public static async Task<int> ExecuteAsync(
        WatchOptions options, TextWriter output, CancellationToken cancellationToken = default, IWatchNotifier? notifier = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);

        var watchesDirectoryPath = BatonPaths.Watches;

        switch (options.Mode)
        {
            case WatchMode.List:
                await ListAsync(watchesDirectoryPath, output, cancellationToken).ConfigureAwait(false);
                return 0;

            case WatchMode.ClearFired:
                var removed = await WatchStore.RemoveFiredAsync(watchesDirectoryPath, cancellationToken).ConfigureAwait(false);
                output.WriteLine($"Removed {removed} fired watch(es).");
                return 0;

            case WatchMode.Register:
                return await RegisterAsync(options, watchesDirectoryPath, output, notifier ?? new WatchNotifier(), cancellationToken)
                    .ConfigureAwait(false);

            default:
                throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    private static async Task ListAsync(string watchesDirectoryPath, TextWriter output, CancellationToken cancellationToken)
    {
        var watches = await WatchStore.ListAsync(watchesDirectoryPath, cancellationToken).ConfigureAwait(false);
        if (watches.Count == 0)
        {
            output.WriteLine("No watches registered.");
            return;
        }

        foreach (var watch in watches.OrderBy(w => w.CreatedAt))
        {
            output.WriteLine(watch.FiredAt is null
                ? $"{watch.WatchId}  pending  {watch.RoomDirectoryPath} -> {watch.NotifyTarget}  (registered {watch.CreatedAt:O})"
                : $"{watch.WatchId}  fired    {watch.RoomDirectoryPath} -> {watch.NotifyTarget}  (fired {watch.FiredAt:O})");
        }
    }

    private static async Task<int> RegisterAsync(
        WatchOptions options, string watchesDirectoryPath, TextWriter output, IWatchNotifier notifier, CancellationToken cancellationToken)
    {
        var roomDirectoryPath = options.RoomDirectoryPath!;
        if (!Directory.Exists(roomDirectoryPath))
        {
            throw new CliArgumentException(
                $"Room directory '{roomDirectoryPath}' does not exist — 'baton watch' registers a watch " +
                "against a room 'baton run'/'dispatch' already started, and never creates one.");
        }

        var watchId = Guid.NewGuid().ToString("n");
        var record = new WatchRecord(watchId, roomDirectoryPath, options.NotifyTarget!, DateTime.UtcNow);
        await WatchStore.WriteAsync(record, watchesDirectoryPath, cancellationToken).ConfigureAwait(false);
        output.WriteLine($"Registered watch '{watchId}' on '{roomDirectoryPath}' -> {options.NotifyTarget}.");

        // spec/baton.md §2: no lost wake-up -- a room that is already terminal at registration time
        // fires immediately here, in-process, rather than waiting for the daemon's next sweep.
        var fired = await WatchFireService
            .TryFireIfTerminalAsync(watchesDirectoryPath, record, notifier, cancellationToken)
            .ConfigureAwait(false);
        if (fired)
        {
            output.WriteLine("Room was already terminal — notification fired immediately.");
            return 0;
        }

        if (!IsDaemonLikelyRunning())
        {
            Console.Error.WriteLine(
                "baton watch: no 'baton daemon' process detected for this storage root — this watch will only fire " +
                "on a room transition once a daemon is running (it already fires immediately for an " +
                "already-terminal room, which just happened not to be the case here). Run 'baton daemon' to " +
                "receive the notification automatically. (A daemon started with --no-mutex is invisible to " +
                "this check and appears absent.)");
        }

        return 0;
    }

    /// <summary>
    /// Best-effort liveness read via the same named <see cref="Mutex"/> <c>DaemonHost</c> takes on
    /// startup (the resolved root's <c>Global\BatonDaemonMutex_{rootHash}</c>) — <see cref="Mutex.TryOpenExisting(string,out Mutex)"/>
    /// finds it without contending for ownership. Not authoritative: a daemon started with
    /// <c>--no-mutex</c> holds no such mutex and reads as absent even though it is running (the
    /// message above says so), and an inability to even ask the OS (no permission, or the kernel
    /// object name is momentarily held by something else) fails open — this is an operator hint, never
    /// a gate, so an unclear answer must not print a false warning.
    /// </summary>
    internal static bool IsDaemonLikelyRunning()
    {
        try
        {
            if (Mutex.TryOpenExisting(DaemonHost.MutexName(BatonPaths.Root), out var mutex))
            {
                mutex.Dispose();
                return true;
            }

            return false;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            return true;
        }
    }
}
