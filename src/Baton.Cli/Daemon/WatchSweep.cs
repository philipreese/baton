using System.Diagnostics;
using System.Globalization;
using Baton.Status;
using Microsoft.Extensions.Hosting;

namespace Baton.Cli.Daemon;

/// <summary>
/// The sweep half of <c>baton watch</c> (#1488, spec/baton.md §2): fires every still-pending watch
/// whose room has since reached Terminal, then reaps <see cref="BatonPaths.Watches"/> so the directory
/// does not grow forever (fix round, spec/baton.md §2). Registered alongside
/// <see cref="RoomRetentionSweep"/> as a hosted service on the same <c>baton daemon</c> host — reusing
/// that already-running process rather than starting a second one, per the design note the issue
/// itself calls for. Unlike <see cref="RoomRetentionSweep"/>, this runs unconditionally (no
/// <c>BATON_*_ENABLED</c> gate): a registered watch that never fires because the operator forgot an
/// env flag is exactly the silent failure this feature exists to remove, and an empty or all-pending
/// <see cref="BatonPaths.Watches"/> directory makes each iteration cheap regardless.
/// </summary>
public sealed class WatchSweep : BackgroundService
{
    /// <summary>Deliberately much shorter than <see cref="RoomRetentionSweep.PlaceholderDefaultInterval"/>
    /// (5 minutes): a conductor waiting on this notification to resume is the entire point of the
    /// feature, so the poll cadence is tuned for "soon", not for housekeeping.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    /// <summary>How long a fired watch's file survives before <see cref="WatchStore.ReapAsync"/> deletes
    /// it — the same "env-configurable, bounded" shape <see cref="RoomRetentionSweep"/>'s own interval
    /// consts use. <c>baton watch --clear-fired</c> stays the manual, immediate path; this is the
    /// unattended default so an operator who never runs it does not accumulate watch files forever.</summary>
    public const string ReaperRetentionHoursEnvironmentVariable = "BATON_WATCH_REAPER_RETENTION_HOURS";

    public static readonly TimeSpan PlaceholderDefaultReaperRetention = TimeSpan.FromHours(24);

    // Same bounds rationale as RoomRetentionSweep.MinInterval/MaxInterval: the upper bound keeps a
    // pathological value ("1e300") from overflowing TimeSpan.FromHours, the lower bound keeps a
    // sub-hour typo from reaping a watch practically as soon as it fires.
    public static readonly TimeSpan MinReaperRetention = TimeSpan.FromHours(1);
    public static readonly TimeSpan MaxReaperRetention = TimeSpan.FromDays(365);

    private readonly IWatchNotifier _notifier;

    public WatchSweep()
        : this(new WatchNotifier())
    {
    }

    public WatchSweep(IWatchNotifier notifier)
    {
        _notifier = notifier;
    }

    public static TimeSpan GetReaperRetention()
    {
        var val = BatonEnvironmentSnapshot.Current.WatchReaperRetentionHoursOverride;
        if (!string.IsNullOrWhiteSpace(val) &&
            double.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out var hours) &&
            hours > 0)
        {
            return TimeSpan.FromHours(Math.Clamp(hours, MinReaperRetention.TotalHours, MaxReaperRetention.TotalHours));
        }

        return PlaceholderDefaultReaperRetention;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                await WatchFireService.SweepAsync(BatonPaths.Watches, _notifier, stoppingToken).ConfigureAwait(false);
                await WatchStore.ReapAsync(BatonPaths.Watches, GetReaperRetention(), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"WatchSweep: sweep iteration failed: {ex.Message}");
            }

            // #1981: DaemonTickLedger's own doc has why every service reports here, and why it reports
            // a tick that threw as well as one that succeeded.
            DaemonTickLedger.Instance.RecordTick(nameof(WatchSweep), Stopwatch.GetElapsedTime(started), Interval);

            try
            {
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
