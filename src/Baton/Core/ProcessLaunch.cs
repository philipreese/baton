using System.Diagnostics;

namespace Baton.Core;

/// <summary>Shared managed process-start seam; callers retain ownership of the process and streams.</summary>
public static class ProcessLaunch
{
    public static Process? Start(ProcessStartInfo startInfo) => Process.Start(startInfo);

    public static bool Start(Process process) => process.Start();
}
