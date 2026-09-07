using Baton.Cli.Daemon;

namespace Baton.Cli.Tests.Daemon;

/// <summary>
/// #2036's first gap: on 2026-09-07 the daemon stopped writing at 04:57:06Z and `daemon.log` carried
/// no exception, no shutdown notice and no exit line — the ending left no evidence at all. These arms
/// pin what each ending now leaves behind, and the two properties that decide whether it actually
/// reaches the log on a process that is failing: the write goes UNDER the shared console lock, and a
/// wedged writer costs the tail rather than the diagnosis.
/// </summary>
public class DaemonLastBreathTests
{
    [Fact]
    public void AnUnhandledException_LeavesAStampedLineCarryingTheStack()
    {
        var lines = new List<string>();
        var breath = DaemonLastBreath.ForTest(lines.Add, () => { });

        breath.OnUnhandledException(new InvalidOperationException("the room walk threw"), isTerminating: true);

        var line = Assert.Single(lines);
        Assert.Contains("baton daemon: unhandled exception", line);
        Assert.Contains("terminating", line);
        Assert.Contains("the room walk threw", line);
        Assert.Contains("InvalidOperationException", line);
        Assert.StartsWith("[", line);
        Assert.Contains("Z] ", line);
    }

    /// <summary>The other polarity of the same field: a non-terminating unhandled exception says so,
    /// rather than being reported as the process's ending.</summary>
    [Fact]
    public void ANonTerminatingUnhandledException_SaysSo()
    {
        var lines = new List<string>();
        var breath = DaemonLastBreath.ForTest(lines.Add, () => { });

        breath.OnUnhandledException(new InvalidOperationException("boom"), isTerminating: false);

        Assert.Contains("NOT terminating", Assert.Single(lines));
    }

    /// <summary>A non-<see cref="Exception"/> thrown object still gets a line — the point of the fix
    /// is that no ending is silent, and this is the ending most likely to be handled badly.</summary>
    [Fact]
    public void AnUnhandledNonException_StillLeavesALine()
    {
        var lines = new List<string>();
        var breath = DaemonLastBreath.ForTest(lines.Add, () => { });

        breath.OnUnhandledException("a bare string", isTerminating: true);

        Assert.Contains("a bare string", Assert.Single(lines));
    }

    [Fact]
    public void ProcessExit_LeavesTheExitCodeAndSaysWhichReadingIsAuthoritative()
    {
        var lines = new List<string>();
        var breath = DaemonLastBreath.ForTest(lines.Add, () => { });

        breath.OnProcessExit(DaemonWatchdog.HungExitCode);

        var line = Assert.Single(lines);
        Assert.Contains("process exiting", line);
        Assert.Contains($"Environment.ExitCode={DaemonWatchdog.HungExitCode}", line);
        Assert.Contains("wrapper", line);
    }

    /// <summary>The once-only rule <see cref="DaemonLastBreath.OnProcessExit"/> states, exercised
    /// through the sequence that makes it matter: an unhandled exception terminates the process, so
    /// both handlers fire, in that order.</summary>
    [Fact]
    public void BothEndingsFiring_WritesOnlyTheFirstLine()
    {
        var lines = new List<string>();
        var breath = DaemonLastBreath.ForTest(lines.Add, () => { });

        breath.OnUnhandledException(new InvalidOperationException("first"), isTerminating: true);
        breath.OnProcessExit(70);

        Assert.Contains("first", Assert.Single(lines));
    }

    /// <summary>The ordering that makes the line reach `daemon.log` at all: the diagnosis is written
    /// BEFORE the pending-tail flush, so a flush that never returns (the wedged-writer case this whole
    /// mechanism exists for) costs the tail and not the diagnosis.</summary>
    [Fact]
    public void TheDiagnosisIsWritten_BeforeTheFlushThatCanHang()
    {
        var order = new List<string>();
        var breath = DaemonLastBreath.ForTest(_ => order.Add("write"), () => order.Add("flush"));

        breath.OnProcessExit(0);

        Assert.Equal(["write", "flush"], order);
    }

    /// <summary>The flush covers BOTH wrappers the daemon installs. stdout is the one that matters
    /// most — the host's console logger writes there — and it is also the one an earlier draft of this
    /// wiring left out, which no arm taking an opaque flush action could have caught.</summary>
    [Fact]
    public void TheFlush_EmitsThePendingTailOfEveryWrapper_NotJustStderr()
    {
        var outSink = new StringWriter { NewLine = "\n" };
        var errSink = new StringWriter { NewLine = "\n" };
        var outWrapper = new TimestampedLineWriter(outSink);
        var errWrapper = new TimestampedLineWriter(errSink);
        outWrapper.Write("info: Microsoft.Hosting.Lifetime[0] half a line on stdout");
        errWrapper.Write("half a line on stderr");

        DaemonLastBreath.FlushAll(outWrapper, errWrapper)();

        Assert.Contains("half a line on stdout", outSink.ToString());
        Assert.Contains("half a line on stderr", errSink.ToString());
    }

    /// <summary>A failed write must not throw out of a ProcessExit handler: nothing above it catches,
    /// and the escaping exception would replace the exit code the scheduled task reads.</summary>
    [Fact]
    public void AWriteThatFails_DoesNotEscapeTheHandler()
    {
        var breath = DaemonLastBreath.ForTest(
            _ => throw new ObjectDisposedException("console"), () => { });

        breath.OnProcessExit(0);
    }
}
