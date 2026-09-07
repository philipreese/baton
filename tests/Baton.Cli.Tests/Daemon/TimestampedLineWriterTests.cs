using Baton.Cli.Daemon;

namespace Baton.Cli.Tests.Daemon;

/// <summary>
/// #1981's third item: `daemon.log` had no timestamps on most lines, so the 2026-09-06 stall could
/// not be placed in time from the one artifact that outlived the process. These arms pin what the
/// wrapper does — a stamp on every line, and nothing else about the line changed.
/// </summary>
public class TimestampedLineWriterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 6, 18, 51, 48, 123, TimeSpan.Zero);

    [Fact]
    public void EveryLineIsStamped_AndItsTextIsOtherwiseUntouched()
    {
        var sink = new StringWriter { NewLine = "\n" };
        using var writer = new TimestampedLineWriter(sink, () => T0);

        writer.WriteLine("FleetProjectionWriter: iteration failed: boom");
        writer.WriteLine("VendorUsageCommandRun: claude usage command did not run");

        Assert.Equal(
            "[2026-09-06T18:51:48.123Z] FleetProjectionWriter: iteration failed: boom\n"
            + "[2026-09-06T18:51:48.123Z] VendorUsageCommandRun: claude usage command did not run\n",
            sink.ToString());
    }

    /// <summary>The stamp is the moment the line STARTED, not the moment its newline arrived — the
    /// console logger and `Console.Write`-style callers build a line across several writes.</summary>
    [Fact]
    public void APieceWiseLineIsStampedWhenItStarted_AndEmittedOnceWhole()
    {
        var clock = T0;
        var sink = new StringWriter { NewLine = "\n" };
        using var writer = new TimestampedLineWriter(sink, () => clock);

        writer.Write("QueueLauncher: ");
        clock = clock.AddSeconds(9);
        writer.Write("launched lane 42");
        clock = clock.AddSeconds(1);
        writer.Write('\n');

        Assert.Equal("[2026-09-06T18:51:48.123Z] QueueLauncher: launched lane 42\n", sink.ToString());
    }

    /// <summary>Control: a line still being written is NOT emitted early. Splitting one log line into
    /// two stamped ones would corrupt the format this issue is trying to make readable.</summary>
    [Fact]
    public void APartialLineIsNotEmittedUntilItsNewlineArrives()
    {
        var sink = new StringWriter { NewLine = "\n" };
        var writer = new TimestampedLineWriter(sink, () => T0);

        writer.Write("half a line, no newline yet");
        writer.Flush();
        Assert.Equal("", sink.ToString());

        // ... and it is not LOST either: a daemon exiting mid-write still gets the line out.
        writer.Dispose();
        Assert.Equal("[2026-09-06T18:51:48.123Z] half a line, no newline yet\n", sink.ToString());
    }

    /// <summary>#2036: nothing ever disposed the daemon's writers, so the "a trailing partial line is
    /// emitted rather than dropped" promise above only held for tests. <see cref="DaemonLastBreath"/>
    /// calls this on the way out instead.</summary>
    [Fact]
    public void TryFlushPendingLine_EmitsTheTailWhenTheWriterIsFree()
    {
        var sink = new StringWriter { NewLine = "\n" };
        var writer = new TimestampedLineWriter(sink, () => T0);

        writer.Write("the last thing the daemon did");

        Assert.True(writer.TryFlushPendingLine(TimeSpan.FromSeconds(2)));
        Assert.Equal("[2026-09-06T18:51:48.123Z] the last thing the daemon did\n", sink.ToString());
    }

    /// <summary>The polarity that decides whether a dying daemon exits at all: a thread wedged
    /// mid-write holds the writer's lock forever, and this must give up rather than wait — .NET puts
    /// no timeout of its own on a ProcessExit handler.</summary>
    [Fact]
    public void TryFlushPendingLine_GivesUpWhenAnotherThreadHoldsTheWriter()
    {
        var sink = new StringWriter { NewLine = "\n" };
        var writer = new TimestampedLineWriter(sink, () => T0);
        writer.Write("a line nobody will finish");

        var held = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var wedged = new Thread(() =>
        {
            // The same lock every console write in the daemon takes: a writer whose inner sink blocks
            // is indistinguishable from this from the outside.
            lock (writer.LockForTest)
            {
                held.Set();
                release.Wait();
            }
        });
        wedged.Start();
        Assert.True(held.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        try
        {
            Assert.False(writer.TryFlushPendingLine(TimeSpan.FromMilliseconds(50)));
            Assert.Equal("", sink.ToString());
        }
        finally
        {
            release.Set();
            wedged.Join();
        }
    }

    [Fact]
    public void CrLfProducesOneLine_NotABlankOneBetween()
    {
        var sink = new StringWriter { NewLine = "\n" };
        using var writer = new TimestampedLineWriter(sink, () => T0);

        writer.Write("windows line\r\nsecond line\r\n");

        Assert.Equal(
            "[2026-09-06T18:51:48.123Z] windows line\n[2026-09-06T18:51:48.123Z] second line\n",
            sink.ToString());
    }
}
