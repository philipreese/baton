using System.Diagnostics;
using Baton.Queue;

namespace Baton.Cli;

internal static class TaskPreparationLiveness
{
    internal static bool IsOwnerAlive(QueueIssuePreparation preparation)
    {
        if (preparation.ProcessId is not { } pid || preparation.ProcessStartedAt is not { } start)
            return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            return Math.Abs((process.StartTime.ToUniversalTime() - start).TotalSeconds) < 2;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
