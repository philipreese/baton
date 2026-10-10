using System.Diagnostics;

namespace Baton.Core;

/// <summary>
/// Starts managed children without inheriting another Baton launch's transient pipe handles.
/// Callers retain ownership of the process and streams; containment policy stays with the caller.
/// </summary>
public static class ProcessLaunch
{
    // The same Monitor protects the native launch from its first inheritable handle until all
    // local inherited copies are closed and retained parent handles are non-inheritable. It
    // never covers callbacks, waits or child lifetime. Raw third-party starts cannot participate.
    internal static object WindowsInheritanceGate { get; } = new();

    public static Process? Start(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (!OperatingSystem.IsWindows()) return Process.Start(startInfo);
        lock (WindowsInheritanceGate)
        {
            return Process.Start(startInfo);
        }
    }

    public static bool Start(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!OperatingSystem.IsWindows()) return process.Start();
        lock (WindowsInheritanceGate)
        {
            return process.Start();
        }
    }
}
