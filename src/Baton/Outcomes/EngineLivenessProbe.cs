using System.ComponentModel;
using System.Diagnostics;

namespace Baton.Outcomes;

public enum EngineLivenessStatus
{
    Alive,
    Dead,
    Unknown
}

public sealed record EngineLivenessResult(EngineLivenessStatus Status, string? Why = null);

/// <summary>
/// Whether the engine process that recorded a <c>FlowEvent.ExecutionRequestAccepted</c> is still
/// alive — the one liveness mechanism this codebase has, consulted by both <c>baton status</c>'s human
/// rendering (<c>Baton.Cli.StatusCommand.FormatStepStatus</c>) and <c>baton resume</c>'s STALLED
/// reconciliation (<c>MutationInterface.RecordResumeAsync</c>, issue #1359 F3) — never a second,
/// independently-invented check.
/// </summary>
public static class EngineLivenessProbe
{
    /// <summary>
    /// How far a live process's start time may sit from the journal's recorded one and still be
    /// taken for the same process; further apart, the pid has been reused and the answer is Dead.
    /// The value the probe has carried since #1359. Named so the daemon's adoption re-check
    /// (<c>QueueLauncher.AdoptAsync</c>, which re-reads the start time off the handle it actually
    /// attached to) reads this rather than its own literal: with two copies, widening one leaves the
    /// probe answering Alive and the re-check answering Dead about the same process (#2117
    /// re-review, finding 3).
    /// </summary>
    public static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(1);

    public static EngineLivenessResult Probe(int? pid, DateTimeOffset? startTime)
    {
        if (pid is null || startTime is null)
        {
            return new EngineLivenessResult(EngineLivenessStatus.Unknown, "no process identity recorded");
        }

        if (pid.Value <= 0)
        {
            return new EngineLivenessResult(EngineLivenessStatus.Unknown, "invalid process identity");
        }

        try
        {
            using var process = Process.GetProcessById(pid.Value);

            DateTimeOffset processStartTime;
            try
            {
                processStartTime = new DateTimeOffset(process.StartTime).ToUniversalTime();
            }
            catch (InvalidOperationException)
            {
                return new EngineLivenessResult(EngineLivenessStatus.Dead);
            }
            catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException)
            {
                return new EngineLivenessResult(EngineLivenessStatus.Unknown, ex.Message);
            }

            var recordedUtc = startTime.Value.ToUniversalTime();
            if ((processStartTime - recordedUtc).Duration() > StartTimeTolerance)
            {
                return new EngineLivenessResult(EngineLivenessStatus.Dead);
            }

            bool hasExited;
            try
            {
                hasExited = process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return new EngineLivenessResult(EngineLivenessStatus.Dead);
            }
            catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException)
            {
                return new EngineLivenessResult(EngineLivenessStatus.Unknown, ex.Message);
            }

            if (hasExited)
            {
                return new EngineLivenessResult(EngineLivenessStatus.Dead);
            }

            return new EngineLivenessResult(EngineLivenessStatus.Alive);
        }
        catch (ArgumentException)
        {
            return new EngineLivenessResult(EngineLivenessStatus.Dead);
        }
        catch (InvalidOperationException)
        {
            return new EngineLivenessResult(EngineLivenessStatus.Dead);
        }
        catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException)
        {
            return new EngineLivenessResult(EngineLivenessStatus.Unknown, ex.Message);
        }
    }
}
