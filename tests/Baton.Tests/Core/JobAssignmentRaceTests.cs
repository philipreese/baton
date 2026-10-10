using System.Diagnostics;
using Baton.Core;
using Baton.Core.Internal;

namespace Baton.Tests.Core;

/// <summary>
/// Pins the fix for #1484: a child that exits between <c>Process.Start</c> and the job-object
/// assignment must read as a completed run, never as <c>SpawnFailed</c>. The race itself is a
/// sub-10ms scheduling accident, so the first test pins the OS fact the guard keys on
/// (an already-exited process makes <c>AssignProcessToJobObject</c> fail with
/// ERROR_ACCESS_DENIED), and the second hammers the true race window through the public API.
/// </summary>
public class JobAssignmentRaceTests
{
    /// <summary>
    /// The OS-behavior pin: if a Windows update ever changes the error code returned for an
    /// exited process, the guard in <c>BatonProcessRunner</c> keys on a stale fact and this
    /// fails first, naming the new code.
    /// </summary>
    [Fact]
    public void Assigning_an_exited_process_to_a_job_fails_with_access_denied()
    {
        using Process process = global::Baton.Core.ProcessLaunch.Start(new ProcessStartInfo("cmd", "/c exit 0")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        process.WaitForExit();

        using SafeJobObjectHandle job = SafeJobObjectHandle.Create();
        bool assigned = job.TryAssign(process.SafeHandle);
        int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();

        Assert.False(assigned);
        Assert.Equal(5, error); // ERROR_ACCESS_DENIED -- the exact code the runner's guard tolerates
    }

    /// <summary>
    /// The end-to-end arm: a process fast enough to routinely exit inside the Start->assign
    /// window must always complete as a natural exit. Before the #1484 guard, a hit in the
    /// window surfaced as BatonException(SpawnFailed, "... Win32 error 5") -- the CI failure
    /// shape this test exists to keep dead. 25 iterations keeps the wall cost ~2s while giving
    /// the window real chances to land on a contended machine.
    /// </summary>
    [Fact]
    public void A_fast_exiting_child_never_reads_as_a_spawn_failure()
    {
        for (int i = 0; i < 25; i++)
        {
            using BatonTask task = new("cmd", "/c", "exit", "0");
            List<BatonEventArgs> events = [];
            task.EventRaised += (_, e) => events.Add(e);

            task.Run();

            Assert.Equal(BatonExitReason.Natural, events[^1].ExitReason);
            Assert.Equal(0, events[^1].ExitCode);
        }
    }
}
