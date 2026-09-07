using Baton.Dispatch;

namespace Baton.Tests.Dispatch;

/// <summary>
/// #2019: what a lane's recorded build-lock queueing is worth against its box, and what a log that
/// cannot be trusted is worth (nothing).
/// </summary>
public class BuildLockWaitCreditTests : IDisposable
{
    private readonly string directory =
        Path.Combine(Path.GetTempPath(), $"baton-lockwait-{Guid.NewGuid():N}");

    public BuildLockWaitCreditTests() => Directory.CreateDirectory(directory);

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not a test failure.
        }
    }

    /// <summary>
    /// The issue's own worked example: a room with a recorded 600 s wait and a 40-minute box settles
    /// at 50 minutes, not 40.
    /// </summary>
    [Fact]
    public void A_recorded_600s_wait_moves_a_40_minute_box_to_50_minutes()
    {
        var log = WriteLog("""{"waitMs": 400000}""", """{"waitMs": 200000}""");

        var credit = BuildLockWaitCredit.RecordedWait(log);
        Assert.Equal(TimeSpan.FromSeconds(600), credit);
        Assert.Equal(
            TimeSpan.FromMinutes(50),
            BuildLockWaitCredit.EffectiveTimeout(TimeSpan.FromMinutes(40), credit));
    }

    /// <summary>
    /// The polarity the arm above cannot show: no recorded wait leaves the box exactly where the
    /// operator set it. A credit applied unconditionally would silently double every lane's box.
    /// </summary>
    [Fact]
    public void No_recorded_wait_leaves_the_box_untouched()
    {
        Assert.Equal(
            TimeSpan.FromMinutes(40),
            BuildLockWaitCredit.EffectiveTimeout(TimeSpan.FromMinutes(40), TimeSpan.Zero));
    }

    [Fact]
    public void Credit_is_capped_at_twice_the_box()
    {
        var log = WriteLog("""{"waitMs": 36000000}""");

        Assert.Equal(
            TimeSpan.FromMinutes(80),
            BuildLockWaitCredit.EffectiveTimeout(TimeSpan.FromMinutes(40), BuildLockWaitCredit.RecordedWait(log)));
    }

    /// <summary>
    /// Every way the log can be untrustworthy reads as zero credit, one arm per way: no file at all
    /// (nothing has queued yet), a torn line an appender is mid-write on, a line in the OTHER sink's
    /// plain-integer shape (<c>BATON_BUILDLOCK_WAIT_LOG</c>'s), a non-numeric or negative value. The
    /// good lines around them still count — a single bad line must not void the whole measurement.
    /// </summary>
    [Fact]
    public void Untrustworthy_lines_credit_nothing_and_do_not_void_the_good_ones()
    {
        Assert.Equal(TimeSpan.Zero, BuildLockWaitCredit.RecordedWait(Path.Combine(directory, "absent.jsonl")));
        Assert.Equal(TimeSpan.Zero, BuildLockWaitCredit.RecordedWait(null));
        Assert.Equal(TimeSpan.Zero, BuildLockWaitCredit.RecordedWait(string.Empty));

        var log = WriteLog(
            """{"waitMs": 2000}""",
            """{"waitMs": 30""",
            "5000",
            """{"waitMs": "3000"}""",
            """{"waitMs": -9000}""",
            """{"somethingElse": 7000}""",
            string.Empty,
            """{"waitMs": 3000}""");

        Assert.Equal(TimeSpan.FromMilliseconds(5000), BuildLockWaitCredit.RecordedWait(log));
    }

    /// <summary>
    /// The reader must not need exclusive access: the writer holds the file open for the whole life of
    /// the lane (see <see cref="BuildLockWaitCredit"/>), so an open that failed under a live writer
    /// would credit nothing exactly when there is something to credit.
    /// </summary>
    [Fact]
    public void A_log_open_for_append_elsewhere_is_still_readable()
    {
        var log = WriteLog("""{"waitMs": 4000}""");

        using var writer = new FileStream(log, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

        Assert.Equal(TimeSpan.FromMilliseconds(4000), BuildLockWaitCredit.RecordedWait(log));
    }

    private string WriteLog(params string[] lines)
    {
        var path = Path.Combine(directory, "lock-wait.jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }
}
