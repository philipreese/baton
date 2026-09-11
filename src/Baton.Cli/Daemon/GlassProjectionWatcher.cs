using Baton.Status;

namespace Baton.Cli.Daemon;

/// <summary>
/// #1946/#2140 — a file-change signal the shared <c>/events</c> loop can wait on. Production creates
/// one for <see cref="BatonPaths.FleetProjectionFile"/> and one for
/// <see cref="BatonPaths.FleetEventsFile"/>. It derives no content: projection versions remain
/// transient reload hints, while durable event ids come only from <see cref="FleetEventLog"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Polled (mtime + length), not <see cref="FileSystemWatcher"/>.</b>
/// <see cref="FleetProjectionWriter.WriteAtomic"/> publishes by renaming a temp file over the target,
/// and what a <see cref="FileSystemWatcher"/> raises for a rename-with-overwrite depends on the
/// notify filters and the platform — which would make "emits on a change, and not otherwise" a
/// timing-dependent assertion rather than a fact. A stat every <see cref="DefaultPollInterval"/> is
/// deterministic, costs one stat per second against a file the writer touches every ~30s, and is
/// what the test can drive by touching the file.
/// </para>
/// <para>
/// <b>Length is compared as well as write time</b> because the file is rewritten wholesale on a
/// cadence that can land two writes inside one filesystem timestamp tick; a same-size rewrite in the
/// same tick is the one change this can miss, and the next tick's write catches it up. It never
/// reports a change that did not happen, which is the direction that matters for a stream a phone is
/// subscribed to.
/// </para>
/// </remarks>
internal sealed class GlassProjectionWatcher
{
    internal static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(1);

    private readonly string _path;
    private readonly TimeSpan _pollInterval;
    private readonly object _gate = new();

    private (DateTime WriteTimeUtc, long Length) _observed;
    private long _version;
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal GlassProjectionWatcher(string path, TimeSpan? pollInterval = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _path = path;
        _pollInterval = pollInterval ?? DefaultPollInterval;
        _observed = Stat();
    }

    /// <summary>Monotonic count of observed changes since construction. A subscriber that holds a
    /// lower number has an event owing to it.</summary>
    internal long Version
    {
        get
        {
            lock (_gate)
            {
                return _version;
            }
        }
    }

    /// <summary>Runs the poll loop until <paramref name="stoppingToken"/> fires.</summary>
    internal async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_pollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            PollOnce();
        }
    }

    /// <summary>One poll, exposed for the test that drives the loop deterministically rather than
    /// sleeping past a real interval. Returns the version after the poll.</summary>
    internal long PollOnce()
    {
        var current = Stat();
        TaskCompletionSource? toSignal = null;
        lock (_gate)
        {
            if (current != _observed)
            {
                _observed = current;
                _version++;
                toSignal = _changed;
                _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        toSignal?.TrySetResult();
        return Version;
    }

    /// <summary>
    /// Completes once <see cref="Version"/> has moved past <paramref name="knownVersion"/>, returning
    /// the new version. Returns immediately when it already has, so a subscriber can never miss a
    /// change that landed between its last write and this await.
    /// </summary>
    internal async Task<long> WaitForChangeAsync(long knownVersion, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task waiter;
            lock (_gate)
            {
                if (_version > knownVersion)
                {
                    return _version;
                }

                waiter = _changed.Task;
            }

            await waiter.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private (DateTime WriteTimeUtc, long Length) Stat()
    {
        try
        {
            var info = new FileInfo(_path);
            // An absent file is a real, distinguishable state (default, -1) rather than an error: the
            // daemon may be serving the page before the first projection tick has written anything,
            // and the file appearing is itself a change worth emitting.
            return info.Exists ? (info.LastWriteTimeUtc, info.Length) : (default, -1L);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Treat an unreadable stat as "unchanged": the alternative is an event storm against a
            // transiently locked file, which is exactly the noise a phone on a sleeping connection
            // pays for.
            lock (_gate)
            {
                return _observed;
            }
        }
    }
}
