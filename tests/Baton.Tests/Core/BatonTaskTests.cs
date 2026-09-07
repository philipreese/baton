using System.Text;
using Baton.Core;

namespace Baton.Tests.Core;

/// <summary>
/// Exercises the managed <see cref="BatonTask"/> surface: config validation, disposal, run-once
/// enforcement, and happy-path event shape. Carried over (#1474) from the same-named file the
/// deleted aer-core .NET binding kept under <c>Baton.Core.Tests\</c> — Windows-only now
/// (spec/baton.md C-10), so the cross-platform command branching that file carried is gone; only
/// the Windows arm survives.
/// </summary>
[Collection(SerializedEnvironmentCollection.Name)]
public class BatonTaskTests
{
    private static (string Program, string[] Args) EchoHello() => ("cmd", ["/c", "echo", "hello"]);

    private static (string Program, string[] Args) ExitZero() => ("cmd", ["/c", "exit 0"]);

    private static (string Program, string[] Args) LongRunning() => ("ping", ["-n", "61", "127.0.0.1"]);

    /// <summary>
    /// ~3s of child: long enough to outlive a 1s timeout, short enough that the #2019 budget arms
    /// below cost seconds rather than a minute.
    /// </summary>
    private static (string Program, string[] Args) ShortRunning() => ("ping", ["-n", "4", "127.0.0.1"]);

    private static (string Program, string[] Args) EchoEnvVar(string var) => ("cmd", ["/c", $"echo %{var}%"]);

    private static (string Program, string[] Args) PrintCwd() => ("cmd", ["/c", "cd"]);

    private static string DecodeChunks(IEnumerable<BatonEventArgs> events) =>
        Encoding.UTF8.GetString(
            [.. events.Where(e => e.Kind == BatonTaskEventKind.StdoutChunk).SelectMany(e => e.Data ?? [])]);

    [Fact]
    public void Constructor_NullProgram_ThrowsArgumentNullException()
    {
        _ = Assert.Throws<ArgumentNullException>(() => new BatonTask(null!));
    }

    [Fact]
    public void Constructor_NullArgs_ThrowsArgumentNullException()
    {
        _ = Assert.Throws<ArgumentNullException>(() => new BatonTask("cmd", null!));
    }

    [Fact]
    public void WithEnv_EmptyKey_ThrowsBatonExceptionWithInvalidArgument()
    {
        using BatonTask task = new("cmd");

        BatonException ex = Assert.Throws<BatonException>(() => task.WithEnv(string.Empty, "value"));
        Assert.Equal(BatonErrorCode.InvalidArgument, ex.ErrorCode);
    }

    [Fact]
    public void WithEnv_KeyContainingEquals_ThrowsBatonExceptionWithInvalidArgument()
    {
        using BatonTask task = new("cmd");

        BatonException ex = Assert.Throws<BatonException>(() => task.WithEnv("BAD=KEY", "value"));
        Assert.Equal(BatonErrorCode.InvalidArgument, ex.ErrorCode);
    }

    [Fact]
    public void WithCwd_EmptyPath_ThrowsBatonExceptionWithInvalidArgument()
    {
        using BatonTask task = new("cmd");

        BatonException ex = Assert.Throws<BatonException>(() => task.WithCwd(string.Empty));
        Assert.Equal(BatonErrorCode.InvalidArgument, ex.ErrorCode);
    }

    [Fact]
    public void WithCwd_InvalidDirectory_RunThrowsBatonExceptionWithSpawnFailedAndEmitsNoEvents()
    {
        (string prog, string[] args) = ExitZero();
        List<BatonEventArgs> events = [];
        using BatonTask task = new BatonTask(prog, args).WithCwd("definitely_not_a_real_directory_xyzzy_aer");
        task.EventRaised += (_, e) => events.Add(e);

        BatonException ex = Assert.Throws<BatonException>(task.Run);
        Assert.Equal(BatonErrorCode.SpawnFailed, ex.ErrorCode);
        Assert.Empty(events);
    }

    [Fact]
    public void Run_CalledTwice_ThrowsInvalidOperationException()
    {
        (string prog, string[] args) = EchoHello();
        using BatonTask task = new(prog, args);

        task.Run();

        _ = Assert.Throws<InvalidOperationException>(task.Run);
    }

    [Fact]
    public void Dispose_WithoutRunning_DoesNotThrow()
    {
        using BatonTask task = new("cmd");
        task.Dispose();
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        BatonTask task = new("cmd");
        task.Dispose();
        task.Dispose();
    }

    [Fact]
    public void Run_HappyPath_RaisesStartedThenChunksThenExited()
    {
        (string prog, string[] args) = EchoHello();
        List<BatonEventArgs> events = [];

        using BatonTask task = new BatonTask(prog, args).WithCaptureOutput();
        task.EventRaised += (_, e) => events.Add(e);

        task.Run();

        Assert.NotEmpty(events);
        Assert.Equal(BatonTaskEventKind.Started, events[0].Kind);
        Assert.Equal(BatonTaskEventKind.Exited, events[^1].Kind);

        int startedIndex = events.FindIndex(e => e.Kind == BatonTaskEventKind.Started);
        int exitedIndex = events.FindIndex(e => e.Kind == BatonTaskEventKind.Exited);
        foreach (int chunkIndex in events
            .Select((e, i) => (e, i))
            .Where(t => t.e.Kind == BatonTaskEventKind.StdoutChunk)
            .Select(t => t.i))
        {
            Assert.True(chunkIndex > startedIndex, "chunk must arrive after Started");
            Assert.True(chunkIndex < exitedIndex, "chunk must arrive before Exited");
        }

        BatonEventArgs exited = events[exitedIndex];
        Assert.Equal(0, exited.ExitCode);
        Assert.Equal(BatonExitReason.Natural, exited.ExitReason);

        string output = DecodeChunks(events);
        Assert.Contains("hello", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Run_TimeoutElapses_ThrowsBatonTimeoutException()
    {
        (string prog, string[] args) = LongRunning();
        using BatonTask task = new BatonTask(prog, args).WithTimeout(TimeSpan.FromMilliseconds(300));

        BatonTimeoutException ex = Assert.Throws<BatonTimeoutException>(task.Run);
        Assert.Equal(BatonErrorCode.TimedOut, ex.ErrorCode);
    }

    /// <summary>
    /// #2019, and the arm the whole credit mechanism rests on: an extension arriving after the monitor
    /// has already started — which is what crediting a wait measured mid-lane looks like — moves the
    /// kill rather than arriving too late to matter. The probe deliberately
    /// returns the configured timeout on its FIRST call and the wider budget only afterwards — a
    /// monitor that reads the budget once, however it reads it, kills this child at 1s.
    /// </summary>
    [Fact]
    public void Run_TimeoutBudgetGrowsAfterTheFirstRead_MovesTheKill()
    {
        (string prog, string[] args) = ShortRunning();
        int probes = 0;
        using BatonTask task = new BatonTask(prog, args)
            .WithTimeout(TimeSpan.FromSeconds(1))
            .WithTimeoutBudget(() => Interlocked.Increment(ref probes) == 1
                ? TimeSpan.FromSeconds(1)
                : TimeSpan.FromSeconds(30));

        task.Run();

        Assert.True(probes > 1, $"the monitor must re-read the budget; it read it {probes} time(s)");
    }

    /// <summary>
    /// The polarity the arm above cannot show: with no credit to report, the probe leaves the
    /// configured timeout exactly where it was. A budget mechanism that merely disabled the kill would
    /// pass the arm above and fail this one.
    /// </summary>
    [Fact]
    public void Run_TimeoutBudgetReportsNoCredit_StillTimesOut()
    {
        (string prog, string[] args) = ShortRunning();
        using BatonTask task = new BatonTask(prog, args)
            .WithTimeout(TimeSpan.FromSeconds(1))
            .WithTimeoutBudget(() => TimeSpan.FromSeconds(1));

        BatonTimeoutException ex = Assert.Throws<BatonTimeoutException>(task.Run);
        Assert.Equal(BatonErrorCode.TimedOut, ex.ErrorCode);
    }

    /// <summary>A budget shorter than the configured timeout can never cut a run short.</summary>
    [Fact]
    public void Run_TimeoutBudgetShorterThanTheTimeout_IsIgnored()
    {
        (string prog, string[] args) = ShortRunning();
        using BatonTask task = new BatonTask(prog, args)
            .WithTimeout(TimeSpan.FromSeconds(30))
            .WithTimeoutBudget(() => TimeSpan.FromMilliseconds(1));

        task.Run();
    }

    /// <summary>
    /// Fails closed: a probe that throws credits nothing and leaves the configured timeout in force —
    /// it neither leaks the exception out of the monitor thread nor leaves the tree with no deadline.
    /// </summary>
    [Fact]
    public void Run_TimeoutBudgetThrows_FallsBackToTheConfiguredTimeout()
    {
        (string prog, string[] args) = LongRunning();
        using BatonTask task = new BatonTask(prog, args)
            .WithTimeout(TimeSpan.FromMilliseconds(300))
            .WithTimeoutBudget(() => throw new InvalidOperationException("the wait log is unreadable"));

        BatonTimeoutException ex = Assert.Throws<BatonTimeoutException>(task.Run);
        Assert.Equal(BatonErrorCode.TimedOut, ex.ErrorCode);
    }

    [Fact]
    public async Task RunAsync_CancelledViaCancellationToken_ThrowsBatonCancelException()
    {
        (string prog, string[] args) = LongRunning();
        using BatonTask task = new(prog, args);
        using CancellationTokenSource cts = new();
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));

        BatonCancelException ex = await Assert.ThrowsAsync<BatonCancelException>(() => task.RunAsync(cts.Token));
        Assert.Equal(BatonErrorCode.Cancelled, ex.ErrorCode);
    }

    [Fact]
    public async Task RunAsync_NotCancelled_CompletesNormally()
    {
        (string prog, string[] args) = EchoHello();
        List<BatonEventArgs> events = [];
        using BatonTask task = new(prog, args);
        task.EventRaised += (_, e) => events.Add(e);

        await task.RunAsync(TestContext.Current.CancellationToken);

        Assert.Contains(events, e => e.Kind == BatonTaskEventKind.Exited && e.ExitCode == 0);
    }

    /// <summary>
    /// Rust equivalent: <c>cancel_before_spawn_reports_cancel_requested</c> (#79 regression). A token
    /// already cancelled before <see cref="BatonTask.RunAsync"/> is even called must still kill the
    /// process promptly once it spawns, rather than waiting for a cancellation signal that will never
    /// arrive again.
    /// </summary>
    [Fact]
    public async Task RunAsync_TokenAlreadyCancelledBeforeRun_KillsPromptlyAndReportsCancelRequested()
    {
        (string prog, string[] args) = ("ping", ["-n", "31", "127.0.0.1"]);
        List<BatonEventArgs> events = [];
        using BatonTask task = new(prog, args);
        task.EventRaised += (_, e) => events.Add(e);
        using CancellationTokenSource cts = new();
        cts.Cancel();

        DateTime start = DateTime.UtcNow;
        BatonCancelException ex = await Assert.ThrowsAsync<BatonCancelException>(() => task.RunAsync(cts.Token));
        TimeSpan elapsed = DateTime.UtcNow - start;

        Assert.Equal(BatonErrorCode.Cancelled, ex.ErrorCode);
        Assert.True(elapsed < TimeSpan.FromSeconds(15), $"took {elapsed} -- a pre-cancelled token should kill promptly rather than waiting out the ping");
        Assert.Equal(BatonTaskEventKind.Started, events[0].Kind);
        BatonEventArgs exited = Assert.Single(events, e => e.Kind == BatonTaskEventKind.Exited);
        Assert.Equal(-1, exited.ExitCode);
        Assert.Equal(BatonExitReason.CancelRequested, exited.ExitReason);
    }

    [Fact]
    public void WithEnv_MakesVariableVisibleToChild()
    {
        (string prog, string[] args) = EchoEnvVar("BATON_DOTNET_MANAGED_TEST_VAR");
        List<BatonEventArgs> events = [];

        using BatonTask task = new BatonTask(prog, args)
            .WithCaptureOutput()
            .WithEnv("BATON_DOTNET_MANAGED_TEST_VAR", "hello_from_managed_wrapper");
        task.EventRaised += (_, e) => events.Add(e);

        task.Run();

        string output = DecodeChunks(events);
        Assert.Contains("hello_from_managed_wrapper", output, StringComparison.Ordinal);
    }

    /// <summary>Rust equivalent: <c>with_env_repeated_call_same_key_overrides_earlier_value</c>.</summary>
    [Fact]
    public void WithEnv_RepeatedCallSameKey_OverridesEarlierValue()
    {
        (string prog, string[] args) = EchoEnvVar("BATON_DOTNET_REPEAT_VAR");
        List<BatonEventArgs> events = [];

        using BatonTask task = new BatonTask(prog, args)
            .WithCaptureOutput()
            .WithEnv("BATON_DOTNET_REPEAT_VAR", "first_value")
            .WithEnv("BATON_DOTNET_REPEAT_VAR", "second_value");
        task.EventRaised += (_, e) => events.Add(e);

        task.Run();

        string output = DecodeChunks(events);
        Assert.Contains("second_value", output, StringComparison.Ordinal);
        Assert.DoesNotContain("first_value", output, StringComparison.Ordinal);
    }

    /// <summary>Rust equivalent: <c>with_clear_env_removes_inherited_var_but_keeps_explicit_ones</c>.</summary>
    [Fact]
    public void WithClearEnv_RemovesInheritedVariable_KeepsExplicitOne()
    {
        Environment.SetEnvironmentVariable("BATON_DOTNET_INHERITED_VAR", "should_not_be_inherited");
        try
        {
            List<BatonEventArgs> events = [];
            using BatonTask task = new BatonTask(
                    "cmd", "/c", "echo %BATON_DOTNET_INHERITED_VAR% & echo %BATON_DOTNET_EXPLICIT_VAR%")
                .WithCaptureOutput()
                .WithClearEnv()
                .WithEnv("BATON_DOTNET_EXPLICIT_VAR", "should_be_present");
            task.EventRaised += (_, e) => events.Add(e);

            task.Run();

            string output = DecodeChunks(events);
            Assert.DoesNotContain("should_not_be_inherited", output, StringComparison.Ordinal);
            Assert.Contains("should_be_present", output, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BATON_DOTNET_INHERITED_VAR", null);
        }
    }

    [Fact]
    public void WithCwd_ChangesChildWorkingDirectory()
    {
        string targetDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        (string prog, string[] args) = PrintCwd();
        List<BatonEventArgs> events = [];

        using BatonTask task = new BatonTask(prog, args).WithCaptureOutput().WithCwd(targetDir);
        task.EventRaised += (_, e) => events.Add(e);

        task.Run();

        string output = DecodeChunks(events);
        string? printedLine = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        Assert.NotNull(printedLine);

        string actual = Path.TrimEndingDirectorySeparator(Path.GetFullPath(printedLine.Trim()));
        Assert.Equal(targetDir, actual, ignoreCase: true);
    }
}
