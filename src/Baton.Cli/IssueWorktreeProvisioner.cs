using System.Diagnostics;
using Baton.Queue;
using Baton.Vendors;

namespace Baton.Cli;

/// <summary>
/// What <c>baton queue add --issue &lt;n&gt;</c> does before the item is queued: the three steps the
/// scratchpad runner did by hand — <c>gh issue develop &lt;n&gt; --name &lt;n&gt;-lane</c>,
/// <c>git worktree add &lt;root&gt;/w&lt;n&gt;</c>, then trust the workspace at the <c>all</c> ceiling
/// (#1934 slice 1, item 1).
/// </summary>
/// <remarks>
/// <para>
/// <b>At add time, not launch time.</b> The operator who queues eight items at 23:00 learns
/// immediately that issue 1940 does not exist, rather than at 04:00 when the scheduler reaches it —
/// and the daemon stays free of <c>gh</c>/<c>git</c> spawning, so the recorded scheduling fact stays a
/// fact about scheduling.
/// </para>
/// <para>
/// <b>Trust is a store write, not a shell-out.</b> The issue's wording is "<c>baton trust &lt;ws&gt;
/// --ceiling all</c> before dispatch", and that verb is nothing but
/// <see cref="ProjectCeilingStore.Set"/> (<see cref="TrustCommand"/>) — calling it in-process is the
/// same effect with no fourth process, and it cannot drift from what the verb does because it is what
/// the verb does.
/// </para>
/// <para>
/// <b>The ceiling is deliberately <c>all</c>, and that is a real widening.</b> It is what the runner
/// did and what an implement lane in a fresh worktree needs; it is named here rather than left
/// implicit so a reader does not have to infer that queueing an issue grants its workspace an
/// unrestricted grant ceiling. <c>ProjectCeiling</c>'s own doc has what a ceiling does and does not
/// bound.
/// </para>
/// </remarks>
public static class IssueWorktreeProvisioner
{
    /// <summary>Each spawn's wall-clock bound. <c>gh issue develop</c> touches the network, so the
    /// hang safety here is the time bound, not the environment — the same posture
    /// <see cref="WorkspaceDeliveryProbe"/> documents for its own <c>gh</c> spawns.</summary>
    public static readonly TimeSpan SpawnTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Provisions and trusts the worktree for <paramref name="issue"/>, returning its path.
    /// </summary>
    /// <param name="issue">The GitHub issue number.</param>
    /// <param name="repositoryDirectory">The checkout <c>gh</c> and <c>git</c> are run in.</param>
    /// <param name="worktreeRoot">
    /// Where <c>w&lt;n&gt;</c> is created. Null resolves to <paramref name="repositoryDirectory"/>'s
    /// parent — the sibling-repos layout the runner assumed, which the issue's own
    /// <c>&lt;repos&gt;</c> never defined. <c>QueueSettings.WorktreeRoot</c> is what an operator with
    /// a different layout sets.
    /// </param>
    /// <param name="runner">Test seam: runs one command and returns (exit code, stdout+stderr).</param>
    /// <exception cref="CliArgumentException">Any of the three steps failed, with the tool's own output in the message.</exception>
    public static async Task<string> ProvisionAsync(
        int issue,
        string repositoryDirectory,
        string? worktreeRoot,
        Func<string, IReadOnlyList<string>, string, CancellationToken, Task<(int ExitCode, string Output)>>? runner = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(issue);
        ArgumentException.ThrowIfNullOrEmpty(repositoryDirectory);

        runner ??= RunAsync;

        var root = worktreeRoot ?? Path.GetDirectoryName(Path.GetFullPath(repositoryDirectory))
            ?? throw new CliArgumentException(
                $"Cannot derive a worktree root from '{repositoryDirectory}' — it has no parent directory.",
                "set Queue.WorktreeRoot in ~/.baton/settings.json to say where w<n> worktrees belong.");

        var workspace = Path.Combine(root, $"w{issue}");
        var branch = $"{issue}-lane";

        if (Directory.Exists(workspace))
        {
            // Not an error: the runner's own habit is to re-queue against a worktree that already
            // exists. Trust it and hand it back rather than failing the add -- `git worktree add` would
            // refuse anyway, and refusing here would make a re-add of a live lane impossible.
            Trust(workspace);
            return workspace;
        }

        var (developExit, developOutput) = await runner(
            "gh", ["issue", "develop", issue.ToString(System.Globalization.CultureInfo.InvariantCulture), "--name", branch],
            repositoryDirectory, cancellationToken).ConfigureAwait(false);
        if (developExit != 0)
        {
            throw new CliArgumentException(
                $"'gh issue develop {issue} --name {branch}' failed (exit {developExit}): {developOutput.Trim()}",
                "check that the issue exists and that 'gh' is authenticated for this repository.");
        }

        var (worktreeExit, worktreeOutput) = await runner(
            "git", ["worktree", "add", workspace, branch], repositoryDirectory, cancellationToken).ConfigureAwait(false);
        if (worktreeExit != 0)
        {
            throw new CliArgumentException(
                $"'git worktree add {workspace} {branch}' failed (exit {worktreeExit}): {worktreeOutput.Trim()}",
                $"the branch '{branch}' exists on the remote now — remove any stale worktree at '{workspace}' and retry.");
        }

        Trust(workspace);
        return workspace;
    }

    private static void Trust(string workspace) =>
        ProjectCeilingStore.Set(workspace, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);

    /// <summary>
    /// The production runner. Spawns <c>gh</c>/<c>git</c> — read-and-write forge and repo commands,
    /// never a vendor CLI — with stdout and stderr merged, bounded by <see cref="SpawnTimeout"/> and
    /// by the caller's token. A timeout kills the child and surfaces as a non-zero exit with the
    /// output collected so far, so the refusal message above still names something.
    /// </summary>
    private static async Task<(int ExitCode, string Output)> RunAsync(
        string fileName, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Pinned rather than inherited: the console code page decides otherwise, and a gh error
            // message carrying a non-ASCII issue title would come back mojibake in the refusal the
            // operator reads. RedirectedProcessEncodingTests is what makes this non-optional.
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // Same two variables WorkspaceDeliveryProbe sets, and with the same disclosed limit: they make
        // a credential prompt less likely, they do not stop an OS credential manager. The time bound
        // below is what this actually rests on.
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GCM_INTERACTIVE"] = "never";

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (127, $"could not start '{fileName}': {ex.Message}");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SpawnTimeout);

        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return (process.ExitCode, $"{await stdout.ConfigureAwait(false)}{await stderr.ConfigureAwait(false)}");
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone. Nothing to clean up, and reporting the kill failure would replace the
                // real answer (the command did not finish in time) with a less useful one.
            }

            cancellationToken.ThrowIfCancellationRequested();
            return (124, $"'{fileName}' did not finish within {SpawnTimeout.TotalMinutes:0} minutes and was killed.");
        }
    }
}
