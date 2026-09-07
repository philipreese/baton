using System.Diagnostics;
using System.IO.Pipes;
using Baton.Core.Internal;

namespace Baton.Tests.Core;

/// <summary>
/// #2030's measurement, as two arms of one experiment. The claim under test is the mechanism, not a
/// wiring detail: <b>a child Baton spawns inherits a duplicate of every inheritable handle in
/// Baton's table — its own redirects do not stop that — and holds it open for as long as it lives</b>,
/// which is why the wrapper shell reading a lane's redirected output never sees EOF after
/// <c>baton dispatch</c> exits.
/// </summary>
/// <remarks>
/// <para>
/// The pipe here stands in for the wrapper's redirected stdout, and is a pipe this test owns rather
/// than the test host's real standard streams: clearing the inherit flag is process-wide and
/// permanent, so doing it against the host's own stdout would leak into every other test in the run.
/// The invariant is a property of the handle, so proving it on an owned handle proves it.
/// </para>
/// <para>
/// The control arm is read first and is what makes the green arm mean anything: it shows this
/// harness CAN observe the blocked case, and — after killing the child — that it can observe EOF at
/// all. A harness that never saw EOF would pass the control and fail the green arm; one that always
/// saw EOF would pass the green arm while proving nothing.
/// </para>
/// </remarks>
public class StandardHandleInheritanceTests
{
    /// <summary>~2.7 hours of child, the same <c>ping -n 9999</c> the process-tree tests use: long
    /// enough that "the child is still alive" is never in question inside a test's lifetime.</summary>
    private static readonly string[] LongLivedChildArgs = ["-n", "9999", "127.0.0.1"];

    /// <summary>How long "no EOF" is observed for before calling it blocked. Generous is cheap: the
    /// green arm's EOF arrives in microseconds, so nothing here is a race against spawn latency.</summary>
    private static readonly TimeSpan BlockedObservationWindow = TimeSpan.FromSeconds(3);

    /// <summary>Ceiling on an EOF that should already have happened. Absorbs a contended runner.</summary>
    private static readonly TimeSpan EofBound = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Control_InheritableHandle_ChildHoldsTheWriteEndOpenSoTheReaderNeverSeesEof()
    {
        using var pipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);

        using Process child = StartLongLivedChildWithEveryStreamRedirected();
        try
        {
            pipe.DisposeLocalCopyOfClientHandle();
            Task<int> read = Task.Run(pipe.ReadByte);

            await Task.WhenAny(read, Task.Delay(BlockedObservationWindow, TestContext.Current.CancellationToken));
            Assert.False(
                read.IsCompleted,
                "the reader saw EOF while the child was still alive -- this arm is supposed to reproduce the "
                + "wedge (#2030), so either the child did not inherit the handle or it died early");
            Assert.False(child.HasExited, "the child exited early; this arm proves nothing about a live holder");

            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync(TestContext.Current.CancellationToken);

            // The discriminating half: without this, "never completed" could just mean the harness
            // cannot observe EOF at all, and the green arm below would be untrustworthy.
            await Task.WhenAny(read, Task.Delay(EofBound, TestContext.Current.CancellationToken));
            Assert.True(read.IsCompleted, "no EOF even after the only holder was killed -- the harness cannot see EOF");
            Assert.Equal(-1, await read);
        }
        finally
        {
            KillIfAlive(child);
        }
    }

    [Fact]
    public async Task NonInheritableHandle_ChildDoesNotHoldItSoTheReaderSeesEofWhileTheChildIsStillAlive()
    {
        using var pipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);

        Assert.True(
            StandardHandleInheritance.DisableFor(pipe.ClientSafePipeHandle.DangerousGetHandle()),
            "SetHandleInformation failed outright; the arm below would pass or fail for the wrong reason");

        using Process child = StartLongLivedChildWithEveryStreamRedirected();
        try
        {
            pipe.DisposeLocalCopyOfClientHandle();
            Task<int> read = Task.Run(pipe.ReadByte);

            await Task.WhenAny(read, Task.Delay(EofBound, TestContext.Current.CancellationToken));
            Assert.True(
                read.IsCompleted,
                "the reader still never saw EOF -- the spawned child is holding a handle it was not supposed "
                + "to inherit, which is exactly the wrapper wedge #2030 records");
            Assert.Equal(-1, await read);
            Assert.False(
                child.HasExited,
                "the child is gone, so EOF says nothing about inheritance -- the arm has stopped discriminating");
        }
        finally
        {
            KillIfAlive(child);
        }
    }

    /// <summary>
    /// Spawned exactly the way Baton spawns: through the shared start-info seam, with all three
    /// streams redirected. That the child STILL inherits the pipe in the control arm above is the
    /// finding — .NET passes <c>bInheritHandles: true</c> whenever anything is redirected, so
    /// per-child redirects are no defense against an inheritable handle held elsewhere in the table.
    /// </summary>
    private static Process StartLongLivedChildWithEveryStreamRedirected()
    {
        var startInfo = ChildProcessStartInfo.Create("ping", startInfo =>
        {
            startInfo.RedirectStandardInput = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.StandardOutputEncoding = System.Text.Encoding.UTF8;
            startInfo.StandardErrorEncoding = System.Text.Encoding.UTF8;
        });

        foreach (string arg in LongLivedChildArgs)
        {
            startInfo.ArgumentList.Add(arg);
        }

        Process? process = Process.Start(startInfo);
        Assert.NotNull(process);
        return process;
    }

    private static void KillIfAlive(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
        }
        catch (InvalidOperationException)
        {
            // Already reaped by the arm itself.
        }
    }
}
