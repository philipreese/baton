using System.Diagnostics;

namespace Baton.Tests.Shared;

/// <summary>
/// Bounds a child process's stdout/stderr/exit wait so a process that never exits fails the ONE
/// test loudly instead of hanging the whole run under <c>-m:1</c> (#1804: an unbounded
/// <c>WaitForExitAsync</c>/<c>ReadToEndAsync</c> pair with no timeout held the machine-wide build
/// lock for up to 65 minutes). Every caller gets a named timeout and the child's captured
/// stdout/stderr tail on failure, rather than an indefinite wait.
/// </summary>
internal static class BoundedProcessWait
{
    public static async Task WaitForExitAsync(
        Process process,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var waitTask = process.WaitForExitAsync(CancellationToken.None);
        if (!await CompletesBeforeTimeoutAsync(waitTask, timeout, cancellationToken))
        {
            TryKill(process);
            throw new TimeoutException($"Process '{process.StartInfo.FileName}' did not exit within {timeout}.");
        }
    }

    public static async Task<(string Stdout, string Stderr)> RunToExitAsync(
        Process process,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        Action? waitsArmed = null)
    {
        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var waitTask = process.WaitForExitAsync(CancellationToken.None);

        var all = Task.WhenAll(waitTask, stdoutTask, stderrTask);
        if (!await CompletesBeforeTimeoutAsync(all, timeout, cancellationToken, waitsArmed))
        {
            TryKill(process);
            var partialStdout = await SnapshotAsync(stdoutTask);
            var partialStderr = await SnapshotAsync(stderrTask);
            throw new TimeoutException(
                $"Process '{process.StartInfo.FileName}' did not exit within {timeout}. " +
                $"stdout tail: {Tail(partialStdout)}{Environment.NewLine}stderr tail: {Tail(partialStderr)}");
        }

        return (await stdoutTask, await stderrTask);
    }

    private static async Task<bool> CompletesBeforeTimeoutAsync(
        Task operation,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Action? waitsArmed = null)
    {
        var timeoutTask = Task.Delay(timeout, CancellationToken.None);
        var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        waitsArmed?.Invoke();
        var completed = await Task.WhenAny(operation, timeoutTask, cancellationTask);

        if (completed == operation)
        {
            await operation;
            return true;
        }

        if (completed == cancellationTask)
        {
            await cancellationTask;
        }

        // If cancellation raced the timeout, cancellation wins before any destructive cleanup.
        cancellationToken.ThrowIfCancellationRequested();
        return false;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Exited between the HasExited check and the Kill call — nothing left to kill.
        }
    }

    private static async Task<string> SnapshotAsync(Task<string> readTask)
    {
        try
        {
            // wait-ok: grace window for a stream to close after Kill(), expected in milliseconds (#1804)
            var winner = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(5)));
            return winner == readTask ? await readTask : "(stream still open after kill)";
        }
        catch (Exception ex)
        {
            return $"(unavailable: {ex.Message})";
        }
    }

    private static string Tail(string text, int maxLength = 2000) =>
        text.Length <= maxLength ? text : text[^maxLength..];
}
