using System.Diagnostics;
using Baton.Tests.Shared;

namespace Baton.Tests.Core;

public sealed class BoundedProcessWaitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Caller_cancellation_does_not_kill_the_process_or_become_a_timeout()
    {
        using var process = StartSleeper();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                BoundedProcessWait.WaitForExitAsync(process, TimeSpan.FromMinutes(1), cancelled.Token));
            Assert.False(process.HasExited);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await BoundedProcessWait.WaitForExitAsync(process, TimeSpan.FromMinutes(1), Ct);
        }
    }

    [Fact]
    public async Task Caller_cancellation_after_io_and_exit_waits_are_armed_does_not_kill_the_process()
    {
        using var process = StartSleeper(redirectOutput: true);
        using var cancellation = new CancellationTokenSource();
        var armed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var waiting = BoundedProcessWait.RunToExitAsync(
                process,
                TimeSpan.FromMinutes(1),
                cancellation.Token,
                () => armed.TrySetResult(true));
            await armed.Task.WaitAsync(TimeSpan.FromMinutes(1), Ct);
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            Assert.False(process.HasExited);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await BoundedProcessWait.WaitForExitAsync(process, TimeSpan.FromMinutes(1), Ct);
        }
    }

    [Fact]
    public async Task Elapsed_timeout_kills_the_process_tree_and_reports_a_timeout()
    {
        using var process = StartSleeper();

        // wait-ok: deliberately short to exercise the timeout-and-kill branch in this focused control.
        var timeout = TimeSpan.FromMilliseconds(50);
        await Assert.ThrowsAsync<TimeoutException>(() =>
            BoundedProcessWait.WaitForExitAsync(process, timeout, Ct));

        // wait-ok: the helper already issued Kill; this only gives Windows time to publish process exit.
        Assert.True(process.WaitForExit(5_000), "The timed-out child process was not killed.");
    }

    private static Process StartSleeper(bool redirectOutput = false)
    {
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectOutput,
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add("Start-Sleep -Seconds 30");
        return Process.Start(startInfo) ?? throw new InvalidOperationException("The sleeper process did not start.");
    }
}
