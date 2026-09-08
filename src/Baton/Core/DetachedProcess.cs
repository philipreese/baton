using System.Diagnostics;
using Baton.Core.Internal;

namespace Baton.Core;

/// <summary>
/// Starts a child that is meant to <b>outlive this process</b> — the one spawn path in Baton that
/// deliberately does not contain what it starts (#2082 finding 2). <see cref="BatonTask"/> is the
/// opposite contract: everything it spawns sits in a Job Object with kill-on-close, so a worker tree
/// dies with the process that owns it. A queue-launched lane must not, because the daemon that
/// launches it restarts on its own schedule and nothing about the lane's work depends on that daemon
/// staying up.
/// </summary>
/// <remarks>
/// <para>
/// <b>What was measured before this existed (2026-09-08, Windows 11, Task Scheduler).</b> The daemon
/// itself runs inside Task Scheduler's own job (its wrapper shell, the daemon, and every worker the
/// daemon had spawned all answered <c>IsProcessInJob</c> true). A throwaway scheduled task that
/// spawned a plain <c>ping</c> child with no job assignment and no breakaway flag, then exited, left
/// that child alive; the same task stopped with <c>Stop-ScheduledTask</c> while still running also
/// left its child alive. So the scheduler's job lets orphans live on either ending, and what took a
/// lane down with the daemon was solely a job the <em>daemon</em> owned — the kill-on-close one
/// <see cref="BatonTask"/> creates per worker. Consequently this helper assigns no
/// job and asks for no breakaway (<c>CREATE_BREAKAWAY_FROM_JOB</c> would need
/// <c>JOB_OBJECT_LIMIT_BREAKAWAY_OK</c> on a job Baton does not own, and the measurement says it is
/// not needed). <c>Baton.Tests</c>' <c>DetachedProcessTests</c> pins both arms of that measurement
/// against a killed parent.
/// </para>
/// <para>
/// <b>Handles are the other half.</b> <see cref="StandardHandleInheritance"/>'s remarks carry the
/// mechanism: with any stream redirected, .NET duplicates every inheritable handle in this process
/// into the child, and a child that outlives us then holds our stdout/stderr open. For the daemon
/// that is not cosmetic — its wrapper shell redirects the daemon's output to <c>daemon.log</c> and
/// relaunches only once that stream reaches EOF, so a surviving lane would have wedged the very
/// restart this helper exists to survive. The clear is therefore done here, on every call, before
/// the spawn (idempotent, a no-op off Windows).
/// </para>
/// <para>
/// A console of its own: <see cref="ChildProcessStartInfo.Create"/>'s <c>CreateNoWindow</c> maps to
/// <c>CREATE_NO_WINDOW</c>, which gives the child a hidden console rather than attaching it to
/// ours — so a console-close event on the parent's console never reaches it either.
/// </para>
/// </remarks>
public static class DetachedProcess
{
    /// <summary>
    /// Starts <paramref name="fileName"/> as a child this process will not contain, after
    /// <paramref name="configure"/> has shaped the start info (arguments, redirects, environment).
    /// </summary>
    /// <exception cref="InvalidOperationException">The OS returned no process.</exception>
    /// <exception cref="System.ComponentModel.Win32Exception">The spawn itself failed — propagated as-is.</exception>
    public static Process Start(string fileName, Action<ProcessStartInfo>? configure = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);

        var startInfo = ChildProcessStartInfo.Create(fileName, configure);
        StandardHandleInheritance.Disable();

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start '{fileName}': the OS returned no process.");
    }
}
