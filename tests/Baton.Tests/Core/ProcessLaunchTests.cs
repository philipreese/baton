using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
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
        bool returned = false;
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
                returned = true;
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
                Assert.False(returned);
            });
            Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
            Assert.Null(failure);
            Assert.NotNull(sibling);
            var stdout = launch.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var stderr = launch.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await BoundedProcessWait.WaitForExitAsync(launch.Process, TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            job.Terminate();
            // wait-ok: bounded EOF diagnosis after contained child exit, with the sibling deliberately alive.
            Assert.Equal("out", await stdout.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
            // wait-ok: independently diagnose stderr retention under the same bounded intervention.
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exposed_writer_control_drains_only_after_raw_sibling_dies_but_contained_sibling_does_not_hold_it(bool contained)
    {
        if (!OperatingSystem.IsWindows()) return;
        // This collection disables all assembly parallelism. The sole intentional raw launch
        // demonstrates the leak outside the ownership seam, without contaminating other tests.
        using var rootJob = SafeJobObjectHandle.Create();
        using var siblingJob = SafeJobObjectHandle.Create();
        Process? sibling = null;
        ContainedProcessLaunch? siblingLaunch = null;
        ContainedProcessLaunch? root = null;
        try
        {
            root = ContainedProcessLauncher.Start(ExitChild(), rootJob, null, beforeCreateProcess: () =>
            {
                if (contained && OperatingSystem.IsWindows())
                {
                    siblingLaunch = ContainedProcessLauncher.Start(Sleeper(), siblingJob, null);
                    sibling = siblingLaunch.Process;
                }
                else
                {
                    sibling = Process.Start(Sleeper()); // Intentional isolated bypass control (#2677).
                }
            });
            Assert.NotNull(sibling);
            var stdout = root.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var stderr = root.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await BoundedProcessWait.WaitForExitAsync(root.Process, TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            rootJob.Terminate();
            if (!contained)
            {
                // A finite negative observation alone proves little. Killing the sole holder and
                // seeing both reads complete below is the causal intervention.
                // wait-ok: negative observation arm; sibling termination and subsequent EOF are the causal proof.
                await Task.WhenAny(Task.WhenAll(stdout, stderr), Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
                Assert.False(stdout.IsCompleted);
                Assert.False(stderr.IsCompleted);
                Assert.False(sibling.HasExited);
                sibling.Kill(entireProcessTree: true);
                await BoundedProcessWait.WaitForExitAsync(sibling, TimeSpan.FromSeconds(60), CancellationToken.None);
            }
            // wait-ok: after the sole raw holder is killed, or with an allowlisted contained sibling still alive.
            Assert.Equal("out", await stdout.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
            // wait-ok: independent stderr EOF diagnosis after the same intervention.
            Assert.Equal("err", await stderr.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
            if (contained) Assert.False(sibling.HasExited);
        }
        finally
        {
            rootJob.Terminate();
            siblingJob.Terminate();
            if (sibling is not null)
            {
                if (!sibling.HasExited) sibling.Kill(entireProcessTree: true);
                await BoundedProcessWait.WaitForExitAsync(sibling, TimeSpan.FromSeconds(60), CancellationToken.None);
                sibling.Dispose();
            }
            siblingLaunch?.StandardOutput.Dispose();
            siblingLaunch?.StandardError.Dispose();
            root?.StandardOutput.Dispose();
            root?.StandardError.Dispose();
            root?.Process.Dispose();
        }
    }

    [Fact]
    public async Task Retained_parent_handles_are_noninheritable_and_callback_can_launch_on_another_thread()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var job = SafeJobObjectHandle.Create();
        Process? sibling = null;
        Task<Process?>? started = null;
        ContainedProcessLaunch? root = null;
        try
        {
            root = ContainedProcessLauncher.Start(ExitChild(), job, beforeResume: (_, _, _) =>
            {
                started = Task.Run(() => ProcessLaunch.Start(Sleeper()));
                Assert.True(started.Wait(TimeSpan.FromSeconds(5)), "launch exclusion still held during beforeResume");
                sibling = started.GetAwaiter().GetResult();
            }, clearInheritanceForTest: handle =>
            {
                // Some runtimes already make the server non-inheritable. Prime the adverse
                // state so removing our explicit clear still produces a discriminating red.
                Assert.True(SetHandleInformation(handle, 1, 1));
                return null;
            });
            foreach (var stream in new[] { root.StandardOutput, root.StandardError })
            {
                var pipe = Assert.IsType<System.IO.Pipes.AnonymousPipeServerStream>(stream.BaseStream);
                Assert.True(GetHandleInformation(pipe.SafePipeHandle.DangerousGetHandle(), out uint flags));
                Assert.Equal(0u, flags & 1u);
            }
            Assert.NotNull(sibling);
            await BoundedProcessWait.WaitForExitAsync(root.Process, TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            // wait-ok: EOF must arrive without waiting for the unrelated sibling's lifetime.
            Assert.Equal("out", await root.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
            // wait-ok: same independent stderr invariant, after the root has already exited.
            Assert.Equal("err", await root.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
            Assert.False(sibling.HasExited);
        }
        finally
        {
            job.Terminate();
            // wait-ok: bounded recovery of the callback's launch task, before killing its owned child.
            if (started is not null) sibling ??= await started.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            if (sibling is not null)
            {
                if (!sibling.HasExited) sibling.Kill(entireProcessTree: true);
                await BoundedProcessWait.WaitForExitAsync(sibling, TimeSpan.FromSeconds(60), CancellationToken.None);
                sibling.Dispose();
            }
            root?.StandardOutput.Dispose();
            root?.StandardError.Dispose();
            root?.Process.Dispose();
        }
    }

    [Theory]
    [InlineData(0)] // Native creation failure, after handles exist.
    [InlineData(1)] // First retained parent handle clear fails.
    [InlineData(2)] // Second clear fails, after the first succeeded.
    public async Task Failed_launch_closes_handles_terminates_suspended_child_and_releases_gate(int failurePoint)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var job = SafeJobObjectHandle.Create();
        var handles = new List<nint>();
        var start = ExitChild();
        if (failurePoint == 0) start.FileName = "baton-nonexistent-2677-" + Guid.NewGuid().ToString("N");
        bool resumed = false;
        var error = Assert.Throws<Win32Exception>(() =>
        {
            if (!OperatingSystem.IsWindows()) throw new InvalidOperationException();
            ContainedProcessLauncher.Start(start, job,
                beforeResume: (_, _, _) => resumed = true,
                clearInheritanceForTest: handle =>
                {
                    handles.Add(handle);
                    if (handles.Count == failurePoint) return (false, 5);
                    return null;
                });
        });
        if (failurePoint != 0) Assert.Equal(5, error.NativeErrorCode);
        Assert.False(resumed);
        Assert.True(SpinWait.SpinUntil(() => !job.IsTreeAlive(), TimeSpan.FromSeconds(5)));
        foreach (nint handle in handles) Assert.False(GetHandleInformation(handle, out _));
        // Must be a different thread: Monitor is reentrant and same-thread recovery is vacuous.
        using Process recovered = (await Task.Run(() => ProcessLaunch.Start(ExitChild())).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken))!; // wait-ok: detects leaked launch exclusion, not child work duration.
        await BoundedProcessWait.WaitForExitAsync(recovered, TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Assert.Equal("out", await recovered.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken));
        Assert.Equal("err", await recovered.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetHandleInformation(nint handle, out uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(nint handle, uint mask, uint flags);

    private static ProcessStartInfo ExitChild() => ChildProcessStartInfo.Create("cmd", start =>
    {
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("<nul set /p =out&1>&2 <nul set /p =err");
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
