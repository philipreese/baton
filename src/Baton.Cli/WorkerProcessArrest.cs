using System.ComponentModel;
using System.Diagnostics;
using Baton.Outcomes;

namespace Baton.Cli;

/// <summary>How <see cref="WorkerProcessArrest.Kill"/> ended, one member per thing it can truthfully claim.</summary>
public enum WorkerKillOutcome
{
    /// <summary>The probe read Alive and the process tree was terminated.</summary>
    Killed,

    /// <summary>The probe read Dead: the recorded pid is gone, or is a recycled pid whose start time does not match.</summary>
    AlreadyExited,

    /// <summary>
    /// The probe read Unknown — no start time recorded (the two cases
    /// <see cref="Baton.Domain.CoreEvent.ExecutionStarted"/>'s <c>ProcessStartTimeUtc</c> remarks
    /// name), or the OS refused the identity read. Nothing was killed: a pid
    /// with no start time is a number, not an identity, and killing by number is exactly the
    /// pid-recycling hazard <see cref="EngineLivenessProbe"/> exists to rule out.
    /// </summary>
    NotConfirmed,

    /// <summary>The probe read Alive but the kill itself failed (access denied, or the process vanished mid-kill).</summary>
    KillFailed,
}

/// <param name="Outcome">Which of the four claims this is.</param>
/// <param name="Detail">A human sentence fragment for the terminal fact's reason and the console line, never parsed back.</param>
public sealed record WorkerKillResult(WorkerKillOutcome Outcome, string Detail);

/// <summary>
/// #2073: the one place <c>baton cancel</c> kills a worker by the pid the journal recorded
/// (<see cref="Baton.Domain.CoreEvent.ExecutionStarted"/>), gated on the ONE liveness probe this
/// codebase has — <see cref="EngineLivenessProbe"/>, fed the pid and the recorded start time — never a
/// second, ad-hoc pid check. Only an <see cref="EngineLivenessStatus.Alive"/> verdict kills; Dead and
/// Unknown both return without touching anything, and the caller's terminal fact says which.
/// <para>
/// A process TREE kill, matching what a live pump's own cancellation delivers
/// (<c>Baton.Core.Internal.BatonProcessRunner</c>): a vendor CLI spawns children, and killing the
/// parent alone would orphan them holding the room's artifact handles.
/// </para>
/// </summary>
public static class WorkerProcessArrest
{
    /// <summary>How long to wait for a killed tree to actually exit before reporting it killed anyway — a bound on a post-kill wait, expected in milliseconds.</summary>
    private static readonly TimeSpan PostKillExitWait = TimeSpan.FromSeconds(5);

    public static WorkerKillResult Kill(uint pid, DateTimeOffset? processStartTime)
    {
        if (pid > int.MaxValue)
        {
            return new WorkerKillResult(WorkerKillOutcome.NotConfirmed, $"worker pid {pid} is outside the range the OS process API accepts; not killed");
        }

        var liveness = EngineLivenessProbe.Probe((int)pid, processStartTime);
        switch (liveness.Status)
        {
            case EngineLivenessStatus.Dead:
                return new WorkerKillResult(WorkerKillOutcome.AlreadyExited, $"worker pid {pid} was already gone");

            case EngineLivenessStatus.Unknown:
                return new WorkerKillResult(
                    WorkerKillOutcome.NotConfirmed,
                    $"worker pid {pid} could not be confirmed as this execution's process ({liveness.Why ?? "liveness unknown"}); not killed");
        }

        try
        {
            using var process = Process.GetProcessById((int)pid);
            process.Kill(entireProcessTree: true);
            var exited = process.WaitForExit(PostKillExitWait); // wait-ok: bounding a post-Kill() exit, expected in milliseconds
            return new WorkerKillResult(
                WorkerKillOutcome.Killed,
                exited
                    ? $"worker pid {pid} was confirmed alive and its process tree was killed"
                    : $"worker pid {pid} was confirmed alive and its process tree was signalled to terminate, but had not exited within {PostKillExitWait.TotalSeconds:0}s");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // The probe read Alive a moment ago and the process is gone now: it exited in the gap.
            return new WorkerKillResult(WorkerKillOutcome.AlreadyExited, $"worker pid {pid} exited before the kill landed");
        }
        catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException or AggregateException)
        {
            return new WorkerKillResult(WorkerKillOutcome.KillFailed, $"worker pid {pid} was confirmed alive but could not be killed: {ex.Message}");
        }
    }
}
