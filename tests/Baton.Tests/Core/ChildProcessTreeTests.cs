using System.ComponentModel;
using System.Diagnostics;
using Baton.Core.Internal;
using Baton.Tests.Shared;

namespace Baton.Tests.Core;

public class ChildProcessTreeTests
{
    /// <summary>
    /// Pins #2030's successful-assignment race without relying on scheduling: the observation seam
    /// runs after CreateProcessW has atomically put the suspended root in the Job, but before any child
    /// instruction can launch the helper. Once resumed, that helper must inherit the Job and die with it.
    /// </summary>
    [Fact]
    public async Task Windows_launch_assigns_the_job_before_child_code_can_spawn_a_helper()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var testRoot = Path.Combine(Path.GetTempPath(), $"child-tree-atomic-job-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testRoot);
        var rootStartedPath = Path.Combine(testRoot, "root-started");
        var helperPidPath = Path.Combine(testRoot, "helper.pid");
        try
        {
            var script =
                $"[System.IO.File]::WriteAllText('{EscapePowerShell(rootStartedPath)}', 'started'); "
                + HelperScript(helperPidPath);

            using var child = ChildProcessTree.Start(
                "powershell",
                startInfo =>
                {
                    startInfo.ArgumentList.Add("-NoProfile");
                    startInfo.ArgumentList.Add("-NonInteractive");
                    startInfo.ArgumentList.Add("-Command");
                    startInfo.ArgumentList.Add(script);
                },
                (job, _, primaryThread) =>
                {
                    Assert.True(job.IsTreeAlive(), "the suspended root was not in the Job before resume");
                    Assert.True(
                        ContainedProcessLauncher.IsPrimaryThreadSuspendedForTest(primaryThread),
                        "the root's primary thread was not suspended before the observation seam");
                    Assert.False(File.Exists(rootStartedPath), "child code ran before Job assignment completed");
                });

            Assert.True(
                SpinWait.SpinUntil(() => File.Exists(helperPidPath), TimeSpan.FromSeconds(5)),
                "the resumed child did not record its helper");
            int helperPid = int.Parse(await File.ReadAllTextAsync(helperPidPath, TestContext.Current.CancellationToken));
            Assert.False(child.Process.HasExited, "the contained root exited before teardown was exercised");
            Assert.True(IsProcessAlive(helperPid), "the helper did not survive long enough to test Job teardown");

            child.Terminate();
            await child.Process.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            Assert.True(
                SpinWait.SpinUntil(() => !IsProcessAlive(helperPid), TimeSpan.FromSeconds(5)),
                "a helper spawned by the atomically-contained root survived Job teardown");
        }
        finally
        {
            if (File.Exists(helperPidPath) && int.TryParse(File.ReadAllText(helperPidPath), out int helperPid))
            {
                try
                {
                    using var helper = Process.GetProcessById(helperPid);
                    helper.Kill(entireProcessTree: true);
                    helper.WaitForExit(5_000);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
                {
                }
            }

            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// Negative control for the race the atomic launcher removes. This deliberately performs the old
    /// managed sequence: start the root, wait until it has spawned a helper, and only then assign the
    /// still-live root to a Job. Assignment succeeds, but Job teardown cannot retroactively capture
    /// the helper. The atomic-launch test above must prove both facts this control lacks: suspended
    /// before user code and already associated at that point.
    /// </summary>
    [Fact]
    public async Task Start_then_assign_control_leaves_a_preexisting_helper_outside_the_job()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var testRoot = Path.Combine(Path.GetTempPath(), $"child-tree-start-assign-control-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testRoot);
        var helperPidPath = Path.Combine(testRoot, "helper.pid");
        try
        {
            using var root = Process.Start(ChildProcessStartInfo.Create("powershell", startInfo =>
            {
                startInfo.ArgumentList.Add("-NoProfile");
                startInfo.ArgumentList.Add("-NonInteractive");
                startInfo.ArgumentList.Add("-Command");
                startInfo.ArgumentList.Add(HelperScript(helperPidPath));
            })) ?? throw new InvalidOperationException("The negative-control root did not start.");

            Assert.True(
                SpinWait.SpinUntil(() => File.Exists(helperPidPath), TimeSpan.FromSeconds(5)),
                "the negative-control root did not record its pre-assignment helper");
            int helperPid = int.Parse(await File.ReadAllTextAsync(helperPidPath, TestContext.Current.CancellationToken));

            using var job = SafeJobObjectHandle.Create();
            Assert.True(job.TryAssign(root.SafeHandle), "the live negative-control root did not enter its Job");
            job.Terminate();
            await root.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

            Assert.True(
                IsProcessAlive(helperPid),
                "the control stopped reproducing: post-start Job assignment unexpectedly captured the preexisting helper");
        }
        finally
        {
            KillRecordedHelper(helperPidPath);
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Windows_launch_gives_the_noninteractive_child_immediate_stdin_eof()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var child = ChildProcessTree.Start("powershell", startInfo =>
        {
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add("[Console]::Out.Write([Console]::In.ReadToEnd().Length)");
        });

        var stdout = child.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        await child.Process.WaitForExitAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        child.Terminate();

        Assert.Equal("0", (await stdout).Trim());
    }

    private static string HelperScript(string helperPidPath) =>
        "$startInfo = [System.Diagnostics.ProcessStartInfo]::new('ping.exe'); "
        + "$startInfo.UseShellExecute = $false; "
        + "$startInfo.Arguments = '-n 9999 127.0.0.1'; "
        + "$helper = [System.Diagnostics.Process]::Start($startInfo); "
        + $"[System.IO.File]::WriteAllText('{EscapePowerShell(helperPidPath)}', $helper.Id.ToString()); "
        + "Start-Sleep -Seconds 600";

    private static void KillRecordedHelper(string helperPidPath)
    {
        if (!File.Exists(helperPidPath) || !int.TryParse(File.ReadAllText(helperPidPath), out int helperPid))
        {
            return;
        }

        try
        {
            using var helper = Process.GetProcessById(helperPid);
            helper.Kill(entireProcessTree: true);
            helper.WaitForExit(5_000);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
        }
    }

    private static string EscapePowerShell(string path) => path.Replace("'", "''", StringComparison.Ordinal);

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
