namespace Baton.Cli.Daemon;

/// <summary>
/// #2036 — the daemon's last line before it dies.
/// <para>
/// On 2026-09-07 the daemon's last output was at 04:57:06Z and then nothing: no exception, no
/// <c>Application is shutting down</c>, no exit line. The process simply stopped writing, and
/// <c>daemon.log</c> — the one artifact that outlives it — carried no evidence of the ending at all,
/// so the outage was undiagnosable rather than merely unexplained. This type is the in-process half
/// of that fix: an unhandled exception and <c>ProcessExit</c> each leave one line behind.
/// <c>tools/tool-refresh/register-daemon-task.ps1</c>'s wrapper is the other half — it records the
/// exit code even for the endings this type never gets to see (a kill, a launcher that never
/// resolved), and its comment carries that division.
/// </para>
/// <para>
/// <b>It writes to the writer UNDERNEATH <see cref="TimestampedLineWriter"/>, with its own
/// timestamp.</b> Every console write in the daemon shares that wrapper's single lock, so a thread
/// wedged mid-write holds it for good — and a last-breath line that blocks on the lock the fault is
/// holding is a line that never appears, which is the exact defect being fixed. The pending partial
/// line is still worth having, so it is attempted second and with a bound
/// (<see cref="TimestampedLineWriter.TryFlushPendingLine"/>): the diagnosis first, the tail only if
/// it is free. Same ordering rule as <see cref="DaemonWatchdog.CheckOnce"/>'s.
/// </para>
/// </summary>
internal sealed class DaemonLastBreath : IDisposable
{
    /// <summary>How long the pending-tail flush may wait on a possibly-wedged writer. Short: this
    /// runs on a dying process, and everything that matters has already been written by then.</summary>
    internal static readonly TimeSpan PendingFlushTimeout = TimeSpan.FromSeconds(2);

    private readonly Action<string> _write;
    private readonly Action _flushPending;
    private readonly UnhandledExceptionEventHandler _onUnhandled;
    private readonly EventHandler _onProcessExit;
    private readonly AppDomain? _domain;
    private int _handled;

    private DaemonLastBreath(Action<string> write, Action flushPending, AppDomain? domain)
    {
        _write = write;
        _flushPending = flushPending;
        _domain = domain;
        _onUnhandled = (_, e) => OnUnhandledException(e.ExceptionObject, e.IsTerminating);
        _onProcessExit = (_, _) => OnProcessExit(Environment.ExitCode);
    }

    /// <summary>
    /// Registers both handlers against the real <see cref="AppDomain"/>. Disposing UNREGISTERS them:
    /// <see cref="DaemonHost"/>'s entry point is also a test seam, and a test that drives it must not
    /// leave a process-global handler holding a reference to a writer the test host has since
    /// replaced.
    /// </summary>
    internal static DaemonLastBreath Install(TextWriter underneath, params TimestampedLineWriter[] wrappers)
    {
        var instance = new DaemonLastBreath(
            line => underneath.WriteLine(line),
            FlushAll(wrappers),
            AppDomain.CurrentDomain);
        instance._domain!.UnhandledException += instance._onUnhandled;
        instance._domain!.ProcessExit += instance._onProcessExit;
        return instance;
    }

    /// <summary>
    /// The pending-tail flush, over EVERY wrapper the daemon installed — both of them, and the plural
    /// is the point: <c>Console.Out</c> and <c>Console.Error</c> get one wrapper each, and the host's
    /// console logger (the source most of <c>daemon.log</c> comes from) writes to <b>stdout</b>. A
    /// flush wired to stderr alone would leave the partial line most likely to exist unemitted.
    /// </summary>
    internal static Action FlushAll(params TimestampedLineWriter[] wrappers) =>
        () =>
        {
            foreach (var wrapper in wrappers)
            {
                wrapper.TryFlushPendingLine(PendingFlushTimeout);
            }
        };

    /// <summary>Test-only seam (Baton.Cli.Tests, via <c>InternalsVisibleTo</c>): the same object with
    /// nothing registered against the process, so both handler bodies can be driven and read back.
    /// The registration itself is one line in <see cref="Install"/> and is NOT covered by a test —
    /// there is no supported way to raise <c>AppDomain.ProcessExit</c> in-process.</summary>
    internal static DaemonLastBreath ForTest(Action<string> write, Action flushPending) =>
        new(write, flushPending, domain: null);

    /// <summary>What an unhandled exception leaves behind. The full <c>ToString()</c>, not just the
    /// message: a stack is what makes the difference between "it died" and "it died here", and this
    /// line is the only chance to record one.</summary>
    internal static string UnhandledExceptionLine(object? exceptionObject, bool isTerminating)
    {
        var detail = exceptionObject is Exception ex ? ex.ToString() : $"{exceptionObject}";
        var terminating = isTerminating ? "terminating" : "NOT terminating";
        return $"[{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ}] baton daemon: unhandled exception "
               + $"(runtime says {terminating}): {detail}";
    }

    /// <summary>What the process's ending leaves behind — reached by every orderly exit, including
    /// <see cref="DaemonWatchdog"/>'s <c>Environment.Exit(70)</c> and an operator's Ctrl-C. The exit
    /// code here is <see cref="Environment.ExitCode"/> as the runtime has it at handler time, which
    /// is NOT guaranteed to be the code the OS finally reports; the wrapper's own
    /// <c>baton daemon exited &lt;code&gt;</c> line is the authoritative reading of that.</summary>
    internal static string ProcessExitLine(int exitCode) =>
        $"[{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ}] baton daemon: process exiting, "
        + $"Environment.ExitCode={exitCode} (the wrapper's own line carries the code the OS reports).";

    internal void OnUnhandledException(object? exceptionObject, bool isTerminating) =>
        Emit(UnhandledExceptionLine(exceptionObject, isTerminating));

    /// <summary>Written at most once even if both handlers fire (an unhandled exception terminates
    /// the process, so <c>ProcessExit</c> follows it) — the FIRST line is the diagnosis, and a
    /// second one saying only "exiting" would push it up the log for no gain.</summary>
    internal void OnProcessExit(int exitCode) => Emit(ProcessExitLine(exitCode));

    private void Emit(string line)
    {
        if (Interlocked.Exchange(ref _handled, 1) != 0)
        {
            return;
        }

        try
        {
            _write(line);
            _flushPending();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Handled, not swallowed: this runs on a process that is already dying, there is nothing
            // above it to rethrow to, and a console that can no longer be written to is exactly the
            // condition under which nothing better can be recorded anywhere.
        }
    }

    public void Dispose()
    {
        if (_domain is null)
        {
            return;
        }

        _domain.UnhandledException -= _onUnhandled;
        _domain.ProcessExit -= _onProcessExit;
    }
}
