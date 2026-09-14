using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Baton.Core.Internal;

namespace Baton;

/// <summary>
/// Owns a direct child and, after successful Windows Job Object assignment, its complete descendant
/// tree for the lifetime of a one-shot operation. A launch that cannot be contained fails closed:
/// it never returns a usable <see cref="ChildProcessTree"/>.
/// </summary>
public sealed class ChildProcessTree : IDisposable
{
    private readonly SafeJobObjectHandle? _job;
    private readonly StreamReader _standardOutput;
    private readonly StreamReader _standardError;

    private ChildProcessTree(
        Process process,
        SafeJobObjectHandle? job,
        StreamReader standardOutput,
        StreamReader standardError)
    {
        Process = process;
        _job = job;
        _standardOutput = standardOutput;
        _standardError = standardError;
    }

    public Process Process { get; }

    public StreamReader StandardOutput => _standardOutput;

    public StreamReader StandardError => _standardError;

    public static ChildProcessTree Start(string fileName, Action<ProcessStartInfo>? configure = null) =>
        Start(fileName, configure, beforeResume: null);

    /// <summary>
    /// Test seam for the atomic Windows launch. The callback runs after the suspended process is in
    /// its Job Object and before its primary thread is resumed, so a test can pin both halves of that
    /// ordering without relying on scheduler timing.
    /// </summary>
    internal static ChildProcessTree Start(
        string fileName,
        Action<ProcessStartInfo>? configure,
        Action<SafeJobObjectHandle, Process, nint>? beforeResume)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var startInfo = ChildProcessStartInfo.Create(fileName, startInfo =>
        {
            configure?.Invoke(startInfo);

            // This seam owns its streams and their decoding. A caller can add arguments, environment
            // and a working directory, but cannot make the two platform paths disagree: stdin is
            // immediate EOF, and stdout/stderr are UTF-8 readers owned by ChildProcessTree.
            startInfo.RedirectStandardInput = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.StandardOutputEncoding = Encoding.UTF8;
            startInfo.StandardErrorEncoding = Encoding.UTF8;
        });

        if (!OperatingSystem.IsWindows())
        {
            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"{startInfo.FileName} did not start.");
            process.StandardInput.Close();
            return new ChildProcessTree(process, job: null, process.StandardOutput, process.StandardError);
        }

        var job = SafeJobObjectHandle.Create();
        try
        {
            var launch = ContainedProcessLauncher.Start(startInfo, job, beforeResume);
            return new ChildProcessTree(launch.Process, job, launch.StandardOutput, launch.StandardError);
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
        _standardOutput.Dispose();
        _standardError.Dispose();
        Process.Dispose();
    }
}
