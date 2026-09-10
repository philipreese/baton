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
/// <b>That clear is process-wide and permanent: from the first call on, for the rest of this
/// process's life, every spawn from it — this seam's or anyone else's — must redirect both output
/// streams or neither.</b> .NET sets <c>STARTF_USESTDHANDLES</c> the moment any one stream is
/// redirected, filling the streams the caller did NOT redirect with this process's own standard
/// handles. After the clear those are non-inheritable: the child receives a handle it cannot use,
/// and whatever it writes on that stream is thrown away — no exception, no exit code, a log with
/// lines missing. With nothing redirected there is no <c>STARTF_USESTDHANDLES</c> and the child simply
/// gets the hidden console below, where its output reaches nobody, which is the shape a caller that
/// does not want the output should use. In the daemon this is not hypothetical: <c>WatchNotifier</c>'s
/// operator command used to redirect stdin only and read its stdout by inheritance, and after the
/// daemon's first queue launch that output would have vanished (#2117 re-review, finding 1) — it now
/// redirects and drains both. Two per-spawn alternatives were weighed and rejected there. A
/// <c>PROC_THREAD_ATTRIBUTE_HANDLE_LIST</c> on the spawn is not reachable through
/// <see cref="Process"/> and would need a <c>CreateProcess</c> P/Invoke of Baton's own, which
/// Architecture Rule 3 forbids (<see href="../../../docs/agents/developing-baton.md"/>). Clearing
/// and restoring the flag around this one spawn
/// would reopen, for the width of that window, exactly the leak this clear exists to close: any
/// other spawn racing it (a <c>git</c> or <c>gh</c> child, a notify command) would duplicate the
/// daemon's stdout into a child that may outlive the daemon, and the wrapper shell's restart would
/// wedge on it again. Permanent is the safe shape, and what it costs is enforced rather than asked
/// for: <see cref="Start(ProcessStartInfo)"/> refuses the mixed shape at this seam, and
/// <c>SpawnOutputRedirectionTests</c> requires both streams at every <c>src/</c> spawn site, with
/// this seam its only exception.
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
