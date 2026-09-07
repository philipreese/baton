using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Baton.Cli.Daemon;

/// <summary>
/// #734: the seam <see cref="DeliveryPoller"/> shells <c>gh</c> through — injectable so a test can
/// fake every transition (open, checks red, checks green, merged, closed-unmerged, gh missing)
/// without a real <c>gh</c> install or network access. The production implementation
/// (<see cref="GhCliRunner"/>) spawns <c>gh</c> the same way <c>Baton.Cli.WorkspaceHead.CaptureAsync</c>
/// spawns <c>git</c>: <see cref="ProcessStartInfo"/>, redirected output, a <see cref="Win32Exception"/>
/// catch for "not on PATH" (Credential Isolation: this never touches a vendor credential of its own —
/// it shells out to whatever <c>gh</c> is already authenticated on the host, same as every worker).
/// </summary>
public interface IGhCliRunner
{
    Task<GhCliResult> RunAsync(string workingDirectory, IReadOnlyList<string> args, CancellationToken cancellationToken);
}

/// <summary>
/// <paramref name="Started"/> false means the process itself never ran (<c>gh</c> missing from
/// PATH) — distinct from a non-zero <paramref name="ExitCode"/>, which means <c>gh</c> ran but
/// refused (not authenticated, PR not found, etc). <see cref="DeliveryPoller"/> treats both the same
/// way (log once, record nothing this tick) but keeps them separate here so a test can tell which one
/// it is faking.
/// </summary>
public sealed record GhCliResult(bool Started, int ExitCode, string Stdout, string Stderr);

/// <summary>Production <see cref="IGhCliRunner"/> — spawns the real <c>gh</c> binary.</summary>
public sealed class GhCliRunner : IGhCliRunner
{
    public async Task<GhCliResult> RunAsync(
        string workingDirectory, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var startInfo = ChildProcessStartInfo.Create("gh", startInfo =>
        {
            startInfo.WorkingDirectory = workingDirectory;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.StandardOutputEncoding = Encoding.UTF8;
            startInfo.StandardErrorEncoding = Encoding.UTF8;
        });
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("gh did not start.");
        }
        catch (Win32Exception)
        {
            return new GhCliResult(Started: false, ExitCode: -1, Stdout: string.Empty, Stderr: "gh was not found on PATH.");
        }

        using (process)
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            return new GhCliResult(Started: true, process.ExitCode, stdout, stderr);
        }
    }
}
