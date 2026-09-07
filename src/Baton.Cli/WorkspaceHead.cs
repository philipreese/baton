using System.ComponentModel;
using System.Diagnostics;

namespace Baton.Cli;

/// <summary>
/// Captures a git workspace's current HEAD commit — the base ref the capture worker (0047 §4) diffs
/// against, taken at workflow start (the git-aware entrypoint, mirroring what <c>tools/baton-agy-loop/dispatch.py</c>'s
/// <c>head_before</c> did before #1759 retired it). Kept in the CLI, not <c>Baton</c>: the engine stays git-agnostic, and the
/// only other place that knows git is <c>Baton.Vendors.CaptureWorkerAdapter</c> (Adapter Isolation).
/// </summary>
internal static class WorkspaceHead
{
    /// <summary>
    /// The full HEAD SHA of the git repository at <paramref name="workingDirectory"/>. Throws a
    /// <see cref="CliArgumentException"/> naming the workspace when it is not a git repository (or git
    /// is unavailable) — so a template that declares a capture step against a non-git workspace fails
    /// loudly before any worker runs, rather than the capture step failing opaquely mid-run.
    /// </summary>
    public static async Task<string> CaptureAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        var startInfo = ChildProcessStartInfo.Create("git", startInfo =>
        {
            startInfo.WorkingDirectory = workingDirectory;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.StandardOutputEncoding = System.Text.Encoding.UTF8;
            startInfo.StandardErrorEncoding = System.Text.Encoding.UTF8;
        });
        startInfo.ArgumentList.Add("rev-parse");
        startInfo.ArgumentList.Add("HEAD");

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new CliArgumentException(
                $"Could not start git to capture the base ref for a capture step in '{workingDirectory}'.");
        }
        catch (Win32Exception)
        {
            throw new CliArgumentException(
                $"git was not found on PATH, so the base ref for a capture step could not be captured in "
                + $"'{workingDirectory}'. A workflow with a diff-of-work-so-far step needs a git workspace.");
        }

        using (process)
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                throw new CliArgumentException(
                    $"Could not resolve HEAD in workspace '{workingDirectory}', so a capture "
                    + "(diff-of-work-so-far) step has no base ref to diff against — it is not a git "
                    + $"repository, or has no commits yet. git said: {stderr.Trim()}");
            }

            return stdout.Trim();
        }
    }
}
