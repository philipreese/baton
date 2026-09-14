using System.ComponentModel;
using System.Diagnostics;
using Baton.Core.Internal;

namespace Baton;

/// <summary>
/// Owns a direct child and, on Windows, its complete descendant tree for the lifetime of a
/// one-shot operation. Disposing this object ends any descendant that outlived its direct parent.
/// </summary>
public sealed class ChildProcessTree : IDisposable
{
    private readonly SafeJobObjectHandle? _job;

    private ChildProcessTree(Process process, SafeJobObjectHandle? job)
    {
        Process = process;
        _job = job;
    }

    public Process Process { get; }

    public static ChildProcessTree Start(ProcessStartInfo startInfo)
    {
        // Callers must obtain this through ChildProcessStartInfo.Create: containment owns only the
        // already-reviewed launch, never construction of a new process command.
        ArgumentNullException.ThrowIfNull(startInfo);

        SafeJobObjectHandle? job = OperatingSystem.IsWindows() ? SafeJobObjectHandle.Create() : null;
        try
        {
            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"{startInfo.FileName} did not start.");

            if (job is not null && !job.TryAssign(process.SafeHandle))
            {
                // A child that exits in Start->assign can leave a helper behind. It was never in
                // the job, so PID-tree kill is the only containment available for that narrow race.
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
                {
                }
            }

            return new ChildProcessTree(process, job);
        }
        catch
        {
            job?.Dispose();
            throw;
        }
    }

    public void Terminate()
    {
        _job?.Terminate();
        try
        {
            if (!Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
        }
    }

    public void Dispose()
    {
        // Kill-on-close is intentional: a direct child is Baton-owned background activity and
        // must not survive the terminal settle that created it.
        _job?.Dispose();
        Process.Dispose();
    }
}
