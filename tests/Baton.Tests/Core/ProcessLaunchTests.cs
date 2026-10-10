using System.Diagnostics;
using Baton.Core;
using Baton.Core.Internal;
using Baton.Tests.Shared;

namespace Baton.Tests.Core;

[Collection(SerializedEnvironmentCollection.Name)]
public class ProcessLaunchTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Managed_sibling_blocks_before_native_creation_and_does_not_retain_writers(bool instance)
    {
        if (!OperatingSystem.IsWindows()) return;

        using var job = SafeJobObjectHandle.Create();
        using var attempting = new ManualResetEventSlim();
        Process? sibling = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var start = Sleeper();
                attempting.Set();
                if (instance)
                {
                    sibling = new Process { StartInfo = start };
                    Assert.True(ProcessLaunch.Start(sibling));
                }
                else
                {
                    sibling = ProcessLaunch.Start(start);
                }
            }
            catch (Exception ex) { failure = ex; }
        });
        ContainedProcessLaunch? launch = null;
        try
        {
            launch = ContainedProcessLauncher.Start(ExitChild(), job, beforeResume: null, beforeCreateProcess: () =>
            {
                thread.Start();
                Assert.True(attempting.Wait(TimeSpan.FromSeconds(5)));
                Assert.True(SpinWait.SpinUntil(
                    () => (thread.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0 || !thread.IsAlive,
                    TimeSpan.FromSeconds(5)));
                Assert.True(thread.IsAlive, "managed sibling returned while native inheritable handles were exposed");
                Assert.Null(sibling);
            });
            Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
            Assert.Null(failure);
            Assert.NotNull(sibling);
            var stdout = launch.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var stderr = launch.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await BoundedProcessWait.WaitForExitAsync(launch.Process, TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            job.Terminate();
            Assert.Equal("out", await stdout.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
            Assert.Equal("err", await stderr.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
            Assert.False(sibling.HasExited);
        }
        finally
        {
            job.Terminate();
            if (thread.IsAlive) Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
            if (sibling is not null)
            {
                if (!sibling.HasExited) sibling.Kill(entireProcessTree: true);
                await BoundedProcessWait.WaitForExitAsync(sibling, TimeSpan.FromSeconds(60), CancellationToken.None);
                sibling.Dispose();
            }
            launch?.StandardOutput.Dispose();
            launch?.StandardError.Dispose();
            launch?.Process.Dispose();
        }
    }

    private static ProcessStartInfo ExitChild() => ChildProcessStartInfo.Create("cmd", start =>
    {
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("<nul set /p =out & <nul set /p =err 1>&2");
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
    });

    private static ProcessStartInfo Sleeper() => ChildProcessStartInfo.Create("ping", start =>
    {
        start.ArgumentList.Add("-n");
        start.ArgumentList.Add("9999");
        start.ArgumentList.Add("127.0.0.1");
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
    });
}
