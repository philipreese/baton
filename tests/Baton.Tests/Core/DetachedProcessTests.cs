using System.Diagnostics;
using System.Globalization;
using Baton.Core;
using Baton.Tests.Shared;
using Baton.Tests.TestSupport;
using Xunit;

namespace Baton.Tests.Core;

/// <summary>
/// #2082 finding 2's measurement, as two arms of one experiment: <b>what happens to a child when the
/// process that spawned it is killed.</b> The question the queue launcher's design rests on, so it is
/// pinned here rather than remembered. Both arms use the same killable host
/// (<c>Baton.CrashTestHost</c>) and the same sleeper; only the spawn path differs.
/// </summary>
/// <remarks>
/// <para>
/// The <b>control</b> arm spawns through <see cref="BatonTask"/> — the job-contained path every
/// worker takes — and expects the sleeper dead once the host is killed, because the job's
/// kill-on-close fires when the host's handle table goes. That is the mechanism that took both live
/// lanes down with the daemon on 2026-09-08. A control that did not die would mean the harness
/// cannot observe a death at all, and the green arm's survival would prove nothing.
/// </para>
/// <para>
/// The <b>green</b> arm spawns through <see cref="DetachedProcess"/> and expects the sleeper alive
/// after the same kill. <see cref="DetachedProcess"/>'s own remarks carry the Task Scheduler half of
/// the measurement that this fixture cannot reproduce without registering a task.
/// </para>
/// </remarks>
public sealed class DetachedProcessTests
{
    private static readonly TimeSpan PidFileBound = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan HostExitBound = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData("spawn-contained", false)]
    [InlineData("spawn-detached", true)]
    public async Task A_child_of_a_killed_parent_survives_only_when_it_was_started_detached(string mode, bool expectedAlive)
    {
        var pidFile = Path.Combine(Path.GetTempPath(), $"baton_detached_{mode}_{Guid.NewGuid():N}.txt");
        Process? host = null;
        var sleeperPid = -1;
        try
        {
            var startInfo = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
            startInfo.ArgumentList.Add("exec");
            startInfo.ArgumentList.Add(CrashTestHostLauncher.HostDllPath);
            startInfo.ArgumentList.Add(mode);
            startInfo.ArgumentList.Add(pidFile);
            host = Process.Start(startInfo) ?? throw new InvalidOperationException("the crash test host did not start");

            sleeperPid = await ReadPidAsync(pidFile, host);
            Assert.True(ProcessIsAlive(sleeperPid), $"sleeper {sleeperPid} was not alive before the parent was killed — the harness cannot discriminate");

            host.Kill();
            using (var exitBound = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken))
            {
                exitBound.CancelAfter(HostExitBound);
                await host.WaitForExitAsync(exitBound.Token);
            }

            // The job's kill-on-close is synchronous with the handle close, but the sleeper's own
            // exit is observed a beat later.
            // wait-ok: a fixed OS-settle delay after the parent's death, not a poll ceiling.
            await Task.Delay(500, TestContext.Current.CancellationToken);

            Assert.Equal(expectedAlive, ProcessIsAlive(sleeperPid));
        }
        finally
        {
            if (host is { HasExited: false })
            {
                host.Kill();
            }

            host?.Dispose();
            if (sleeperPid > 0)
            {
                CrashTestHostLauncher.TryKillOrphanedChild(sleeperPid);
            }

            FileCleanup.Delete(pidFile);
        }
    }

    private static async Task<int> ReadPidAsync(string pidFile, Process host)
    {
        var deadline = DateTime.UtcNow + PidFileBound;
        while (DateTime.UtcNow < deadline)
        {
            if (host.HasExited)
            {
                throw new InvalidOperationException($"the crash test host exited {host.ExitCode} before writing the sleeper's pid");
            }

            if (File.Exists(pidFile))
            {
                var raw = (await File.ReadAllTextAsync(pidFile).ConfigureAwait(false)).Trim();
                if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) && pid > 0)
                {
                    return pid;
                }
            }

            // wait-ok: the poll interval under PidFileBound's 60s ceiling, not the ceiling itself.
            await Task.Delay(50).ConfigureAwait(false);
        }

        throw new TimeoutException($"the crash test host never wrote a pid to '{pidFile}' within {PidFileBound}");
    }

    private static bool ProcessIsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
