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
            FileCleanup.Delete(pidFile + ".tmp");
        }
    }

    /// <summary>
    /// The seam-level half of <c>SpawnOutputRedirectionTests</c> (#2117 review, finding 3), covering
    /// callers no per-file scan reaches: <see cref="DetachedProcess"/> clears the caller's inheritable
    /// stdout/stderr before every spawn, so a start info that redirects one output stream and not the
    /// other would hand the child a handle it cannot inherit and lose that stream silently. Pinned on
    /// the pure predicate <see cref="DetachedProcess.Refusal"/> rather than on <c>Start</c>, in both
    /// polarities: the four mixed shapes are refused, and the two whole shapes (both or neither) are
    /// not — without which a predicate that refused everything would pass the refusal arms. Nothing is
    /// spawned and the test host's own handles are never touched.
    /// </summary>
    [Theory]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, true)]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, false, true)]
    [InlineData(false, true, true, false)]
    [InlineData(false, false, false, false)]
    public void A_detached_start_info_must_redirect_both_output_streams_or_neither(
        bool redirectInput, bool redirectOutput, bool redirectError, bool expectRefused)
    {
        var startInfo = ChildProcessStartInfo.Create("this-is-not-a-real-binary-2117", configure =>
        {
            configure.RedirectStandardInput = redirectInput;
            configure.RedirectStandardOutput = redirectOutput;
            configure.RedirectStandardError = redirectError;
        });

        var refusal = DetachedProcess.Refusal(startInfo);

        if (expectRefused)
        {
            Assert.NotNull(refusal);
            Assert.Contains("both stdout and stderr or neither", refusal, StringComparison.Ordinal);
        }
        else
        {
            Assert.Null(refusal);
        }
    }

    /// <summary>
    /// And the throw is wired to the predicate: a refused shape is refused BEFORE the spawn (an
    /// <see cref="ArgumentException"/>, not a failure to find the fake binary), so the guard is not a
    /// predicate nobody calls. Only a refused shape is driven through <c>Start</c> here: an accepted
    /// one would flip the test host's own stdout/stderr inherit flags on its way to the spawn.
    /// </summary>
    [Fact]
    public void A_refused_shape_throws_before_anything_is_spawned()
    {
        var startInfo = ChildProcessStartInfo.Create("this-is-not-a-real-binary-2117", configure =>
        {
            configure.RedirectStandardOutput = true;
        });

        var refused = Assert.Throws<ArgumentException>(() => DetachedProcess.Start(startInfo));
        Assert.Contains("both stdout and stderr or neither", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_detached_start_info_may_not_use_shell_execution()
    {
        var startInfo = ChildProcessStartInfo.Create("this-is-not-a-real-binary-2117");
        startInfo.UseShellExecute = true;

        Assert.Contains("without shell execution", DetachedProcess.Refusal(startInfo)!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Polls for the pid the host writes. The host renames the file into place, so an existing file is
    /// a complete one; the sharing-violation catch below is for the write that a scan or an antivirus
    /// holds for a moment, and it retries rather than fails because the bound above is the failure.
    /// </summary>
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
                string raw;
                try
                {
                    raw = (await File.ReadAllTextAsync(pidFile).ConfigureAwait(false)).Trim();
                }
                catch (IOException)
                {
                    raw = string.Empty;
                }

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
