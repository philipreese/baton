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
/// against a killed parent. This paragraph is the one home for that measurement; spec/baton.md §13's
/// launch/adopt contract cites it rather than restating it.
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
/// <b>That clear is done for the caller, and it has a cost the caller must price: redirect both
/// output streams or neither.</b> The clear is process-wide, not per spawn, and .NET sets
/// <c>STARTF_USESTDHANDLES</c> the moment any one stream is redirected, filling the streams the
/// caller did NOT redirect with this process's own standard handles. After the clear those are
/// non-inheritable, so a child handed one cannot inherit it and everything it writes on that stream
/// is discarded — no exception, no exit code, a lane log with lines missing. With nothing redirected
/// there is no <c>STARTF_USESTDHANDLES</c> and the child simply gets the hidden console below, where
/// its output reaches nobody, which is the shape a caller that does not want the output should use.
/// <see cref="Start(ProcessStartInfo)"/> refuses the mixed shape outright rather than documenting it
/// away: <c>SpawnOutputRedirectionTests</c> is the same rule as a source scan, and this is the same
/// rule at the seam, for the callers a per-file scan cannot see.
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
    /// The convenience form of <see cref="Start(ProcessStartInfo)"/> for a caller with nothing to
    /// redirect; a caller that redirects builds its own start info through
    /// <see cref="ChildProcessStartInfo.Create"/> and passes it, so the file that decides the
    /// redirects and their decode is the file the spawn-site scans read.
    /// </summary>
    /// <exception cref="InvalidOperationException">The OS returned no process.</exception>
    /// <exception cref="System.ComponentModel.Win32Exception">The spawn itself failed — propagated as-is.</exception>
    public static Process Start(string fileName, Action<ProcessStartInfo>? configure = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        return Start(ChildProcessStartInfo.Create(fileName, configure));
    }

    /// <summary>
    /// Starts <paramref name="startInfo"/> as a child this process will not contain. The start info
    /// must come from <see cref="ChildProcessStartInfo.Create"/> — the raw constructor is confined to
    /// that seam (<c>VendorSpawnGateTests</c>) — and must redirect both output streams or neither
    /// (the class remarks say what the mixed shape loses).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="startInfo"/> redirects some stream without redirecting both stdout and stderr,
    /// or asks for shell execution, which no redirect and no hidden console survives.
    /// </exception>
    /// <exception cref="InvalidOperationException">The OS returned no process.</exception>
    /// <exception cref="System.ComponentModel.Win32Exception">The spawn itself failed — propagated as-is.</exception>
    public static Process Start(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (Refusal(startInfo) is { } why)
        {
            throw new ArgumentException(why, nameof(startInfo));
        }

        StandardHandleInheritance.Disable();

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start '{startInfo.FileName}': the OS returned no process.");
    }

    /// <summary>
    /// Why <see cref="Start(ProcessStartInfo)"/> would refuse <paramref name="startInfo"/>, or null when
    /// it would spawn it. The shape rule the class remarks state, as a pure predicate: internal so
    /// <c>DetachedProcessTests</c> can pin both polarities without spawning anything and without
    /// running the process-wide handle clear inside the test host.
    /// </summary>
    internal static string? Refusal(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (startInfo.UseShellExecute)
        {
            return "a detached child must be started without shell execution; build the start info through ChildProcessStartInfo.Create.";
        }

        var anyRedirected = startInfo.RedirectStandardInput || startInfo.RedirectStandardOutput || startInfo.RedirectStandardError;
        if (anyRedirected && !(startInfo.RedirectStandardOutput && startInfo.RedirectStandardError))
        {
            return "a detached child must redirect both stdout and stderr or neither: this process's own standard handles are "
                + "made non-inheritable before the spawn, so a stream left un-redirected would be handed a handle the child "
                + "cannot inherit and its writes would be silently discarded (#2030).";
        }

        return null;
    }
}
