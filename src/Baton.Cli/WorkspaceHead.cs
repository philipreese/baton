using System.ComponentModel;
using System.Diagnostics;

namespace Baton.Cli;

/// <summary>
/// The two read-only <c>git rev-parse</c> questions a dispatch asks about its workspace before any
/// worker runs: what commit <c>HEAD</c> is at — the base ref the capture worker (0047 §4) diffs
/// against, mirroring what <c>tools/baton-agy-loop/dispatch.py</c>'s <c>head_before</c> did before
/// #1759 retired it — and what branch it is on (#1944, <see cref="RoomDeliveryBranch"/>). Kept in the
/// CLI, not <c>Baton</c>: the engine stays git-agnostic, and the only other place that knows git is
/// <c>Baton.Vendors.CaptureWorkerAdapter</c> (Adapter Isolation).
/// </summary>
/// <remarks>
/// <b>The two answers fail in opposite directions, on purpose.</b> A missing base ref is fatal —
/// a capture step has nothing to diff against, so the dispatch is refused before it provisions
/// anything. A missing branch name costs one accounting join and nothing else, so it fails open.
/// Neither spawn carries a wall-clock bound of its own, unlike <see cref="WorkspaceDeliveryProbe"/>'s:
/// both are purely local <c>rev-parse</c> reads that touch no network and no credential helper, so the
/// caller's cancellation token is the only bound either needs.
/// </remarks>
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
        var result = await RunRevParseAsync(workingDirectory, ["HEAD"], cancellationToken).ConfigureAwait(false);
        if (!result.Started)
        {
            // The reason git did not start is carried through rather than assumed: not-on-PATH is the
            // usual one, but a Process.Start that returns null or refuses for another reason reaches
            // here too, and a refusal naming the wrong cause sends an operator to the wrong fix.
            throw new CliArgumentException(
                $"git could not be started, so the base ref for a capture step could not be captured in "
                + $"'{workingDirectory}'. A workflow with a diff-of-work-so-far step needs a git "
                + $"workspace. {result.Stderr.Trim()}");
        }

        if (result.ExitCode != 0)
        {
            throw new CliArgumentException(
                $"Could not resolve HEAD in workspace '{workingDirectory}', so a capture "
                + "(diff-of-work-so-far) step has no base ref to diff against — it is not a git "
                + $"repository, or has no commits yet. git said: {result.Stderr.Trim()}");
        }

        return result.Stdout.Trim();
    }

    /// <summary>
    /// The branch name checked out at <paramref name="workingDirectory"/> — <c>git rev-parse
    /// --abbrev-ref HEAD</c> — or <see langword="null"/> when there is no named branch to report.
    /// <para>
    /// <b>Fails open at every step</b>, which is what makes it safe to call on the ordinary dispatch
    /// path: git not on PATH, a workspace that is not a git repository, a repository with no commits,
    /// and a detached <c>HEAD</c> (which git answers with the literal string <c>HEAD</c>, never a
    /// branch) all read the same way — absent. The one caller records an accounting join key, so the
    /// cost of every one of those is that join and nothing else.
    /// </para>
    /// </summary>
    public static async Task<string?> TryReadBranchAsync(
        string workingDirectory, CancellationToken cancellationToken = default)
    {
        var result = await RunRevParseAsync(workingDirectory, ["--abbrev-ref", "HEAD"], cancellationToken)
            .ConfigureAwait(false);
        if (!result.Started || result.ExitCode != 0)
        {
            return null;
        }

        var branch = result.Stdout.Trim();
        return branch.Length == 0 || string.Equals(branch, "HEAD", StringComparison.Ordinal) ? null : branch;
    }

    /// <summary>
    /// One <c>git rev-parse</c> spawn. <c>Started: false</c> is git-not-on-PATH — reported rather than
    /// thrown, so each caller applies its own posture to it (see this type's remarks).
    /// </summary>
    private static async Task<(bool Started, int ExitCode, string Stdout, string Stderr)> RunRevParseAsync(
        string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
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
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return (false, -1, string.Empty, $"git was not found on PATH ({ex.Message}).");
        }

        if (process is null)
        {
            return (false, -1, string.Empty, "git did not start.");
        }

        using (process)
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return (
                true,
                process.ExitCode,
                await stdoutTask.ConfigureAwait(false),
                await stderrTask.ConfigureAwait(false));
        }
    }
}
