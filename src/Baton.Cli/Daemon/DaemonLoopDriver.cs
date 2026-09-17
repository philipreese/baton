namespace Baton.Cli.Daemon;

/// <summary>
/// Runs one daemon background loop with a production clock/delay by default and injectable time in
/// tests. The interface deliberately owns the repeated loop mechanics once: tick timing, failure
/// recovery, cadence delay, ledger reporting, and bounded phase attribution.
/// </summary>
internal sealed class DaemonLoopDriver
{
    internal const int MaxRetainedPhases = 16;
    internal const string OtherPhaseName = "other";

    private static readonly AsyncLocal<TickTrace?> CurrentTrace = new();

    private readonly DaemonTickLedger _ledger;
    private readonly TimeProvider _timeProvider;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    internal DaemonLoopDriver(
        DaemonTickLedger? ledger = null,
        TimeProvider? timeProvider = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _ledger = ledger ?? DaemonTickLedger.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _delay = delay ?? ((interval, cancellationToken) =>
            Task.Delay(interval, _timeProvider, cancellationToken));
    }

    /// <summary>
    /// Runs <paramref name="tick"/> until cancellation. A tick returns the delay it selected for its
    /// next pass; <paramref name="failureInterval"/> supplies the same decision when the tick throws.
    /// </summary>
    internal async Task RunAsync(
        string service,
        Func<CancellationToken, Task<TimeSpan>> tick,
        Func<TimeSpan> activeInterval,
        Func<Exception, TimeSpan> failureInterval,
        Action<Exception> reportFailure,
        CancellationToken stoppingToken,
        Action? afterTick = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        ArgumentNullException.ThrowIfNull(tick);
        ArgumentNullException.ThrowIfNull(activeInterval);
        ArgumentNullException.ThrowIfNull(failureInterval);
        ArgumentNullException.ThrowIfNull(reportFailure);

        var expectedInterval = activeInterval();
        while (!stoppingToken.IsCancellationRequested)
        {
            var started = _timeProvider.GetTimestamp();
            var trace = new TickTrace(service, expectedInterval, _timeProvider);
            var active = _ledger.BeginTick(service, trace.SnapshotActive);
            var previous = CurrentTrace.Value;
            CurrentTrace.Value = trace;

            TimeSpan interval;
            IReadOnlyList<DaemonTickLedger.InFlightSample> samples = [];
            try
            {
                interval = await tick(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                reportFailure(ex);
                interval = failureInterval(ex);
            }
            finally
            {
                CurrentTrace.Value = previous;
                samples = active.Complete();
            }

            var elapsed = _timeProvider.GetElapsedTime(started);
            _ledger.RecordTick(
                service,
                elapsed,
                interval,
                elapsed > interval ? trace.Snapshot() : [],
                elapsed > interval ? samples : []);
            expectedInterval = interval;
            afterTick?.Invoke();

            try
            {
                await _delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Attributes elapsed time to the current loop tick. Outside a driver-owned tick it is a no-op,
    /// so the existing direct <c>TickOnceAsync</c> test seams remain valid.
    /// </summary>
    internal static IDisposable EnterPhase(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return CurrentTrace.Value?.Enter(name) ?? NoopDisposable.Instance;
    }

    private sealed class TickTrace(string service, TimeSpan interval, TimeProvider timeProvider)
    {
        private readonly object _gate = new();
        private readonly List<DaemonTickLedger.DaemonPhase> _phases = [];
        private readonly List<ActivePhase> _active = [];
        private readonly long _started = timeProvider.GetTimestamp();

        internal IDisposable Enter(string name)
        {
            var phase = new ActivePhase(name, timeProvider.GetTimestamp());
            lock (_gate)
            {
                _active.Add(phase);
            }

            return new PhaseScope(this, phase);
        }

        internal DaemonTickLedger.ActiveTick SnapshotActive()
        {
            lock (_gate)
            {
                var phase = _active.Count > 0 ? _active[^1] : null;
                return new DaemonTickLedger.ActiveTick(
                    service,
                    timeProvider.GetElapsedTime(_started),
                    interval,
                    phase?.Name,
                    phase is null ? TimeSpan.Zero : timeProvider.GetElapsedTime(phase.Started));
            }
        }

        internal IReadOnlyList<DaemonTickLedger.DaemonPhase> Snapshot()
        {
            lock (_gate)
            {
                return [.. _phases];
            }
        }

        private void Complete(ActivePhase completed)
        {
            lock (_gate)
            {
                _active.Remove(completed);
                var elapsed = timeProvider.GetElapsedTime(completed.Started);
                var existing = _phases.FindIndex(phase =>
                    string.Equals(phase.Name, completed.Name, StringComparison.Ordinal));
                if (existing >= 0)
                {
                    var phase = _phases[existing];
                    _phases[existing] = phase with
                    {
                        Elapsed = phase.Elapsed + elapsed,
                        Count = phase.Count + 1,
                    };
                    return;
                }

                if (_phases.Count < MaxRetainedPhases)
                {
                    _phases.Add(new DaemonTickLedger.DaemonPhase(completed.Name, elapsed));
                    return;
                }

                var overflow = _phases.FindIndex(phase => phase.Name == OtherPhaseName);
                if (overflow < 0)
                {
                    overflow = _phases.Count - 1;
                    var displaced = _phases[overflow];
                    _phases[overflow] = new DaemonTickLedger.DaemonPhase(
                        OtherPhaseName,
                        displaced.Elapsed + elapsed,
                        displaced.Count + 1);
                }
                else
                {
                    var phase = _phases[overflow];
                    _phases[overflow] = phase with
                    {
                        Elapsed = phase.Elapsed + elapsed,
                        Count = phase.Count + 1,
                    };
                }
            }
        }

        private sealed class ActivePhase(string name, long started)
        {
            internal string Name { get; } = name;
            internal long Started { get; } = started;
        }

        private sealed class PhaseScope(TickTrace owner, ActivePhase phase) : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    owner.Complete(phase);
                }
            }
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        internal static NoopDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
