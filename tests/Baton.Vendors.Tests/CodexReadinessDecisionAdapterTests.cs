using System.Diagnostics;
using System.Text.Json;
using Baton.Conductor;
using Baton.CrashTestHost;

namespace Baton.Vendors.Tests;

public sealed class CodexReadinessDecisionAdapterTests
{
    private static readonly ReadinessRequest Request = new(1, "trip-2026-09-28", "philipreese/baton",
        @"C:\worktrees\baton", "0123456789abcdef0123456789abcdef01234567", "conductor",
        new DateTimeOffset(2026, 9, 28, 14, 30, 0, TimeSpan.Zero),
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
        CodexReadinessDecisionAdapter.AdapterName, CodexReadinessDecisionAdapter.Model,
        CodexReadinessDecisionAdapter.Effort);

    private static readonly ReadinessContext Context = new(1, Request.Key, Request.Repository,
        Request.Workspace, Request.Revision, Request.ObservedAt,
        [new ReadinessEvidence("required-checks", "green", "all required checks passed")]);

    [Fact]
    public async Task A_valid_decision_is_captured_with_usage_and_diagnostic_streams()
    {
        using var run = new FakeCodexRun("valid");
        var response = await run.Adapter.DecideAsync("obligation-1", Request, Context,
            run.Directory, TestContext.Current.CancellationToken);
        Assert.Equal("obligation-1", response.Decision.ObligationId);
        Assert.Equal(ReadinessChoice.Recommend, response.Decision.Decision);
        Assert.Equal(new ReadinessUsage(120, 40, 10), response.Usage);
        Assert.Contains("turn.completed", File.ReadAllText(run.StdoutPath));
        Assert.Contains("diagnostic stderr", File.ReadAllText(run.StderrPath));
        Assert.True(File.Exists(run.SchemaPath));
        Assert.True(File.Exists(run.AnswerPath));
        AssertStopped(run);
    }

    [Fact]
    public async Task An_identity_mismatch_is_rejected_after_the_real_process_runs()
    {
        using var run = new FakeCodexRun("identity");
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => run.Adapter.DecideAsync(
            "obligation-1", Request, Context, run.Directory, TestContext.Current.CancellationToken));
        Assert.Contains("identity or schema", exception.Message, StringComparison.OrdinalIgnoreCase);
        AssertStopped(run);
    }

    [Fact]
    public async Task A_missing_required_decision_member_is_rejected_as_invalid_json()
    {
        using var run = new FakeCodexRun("schema");
        await Assert.ThrowsAsync<JsonException>(() => run.Adapter.DecideAsync(
            "obligation-1", Request, Context, run.Directory, TestContext.Current.CancellationToken));
        AssertStopped(run);
    }

    [Fact]
    public async Task A_tool_event_is_rejected_and_the_owned_child_is_terminated()
    {
        using var run = new FakeCodexRun("tool");
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => run.Adapter.DecideAsync(
            "obligation-1", Request, Context, run.Directory, TestContext.Current.CancellationToken));
        Assert.Contains("tool", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mcp_tool_call", File.ReadAllText(run.StdoutPath));
        AssertStopped(run);
    }

    [Theory]
    [InlineData("stdout-overflow", true)]
    [InlineData("stderr-overflow", false)]
    public async Task Stream_overflow_is_bounded_and_terminates_the_owned_child(string mode, bool stdout)
    {
        var writes = 0;
        var started = Stopwatch.GetTimestamp();
        async Task SimulateWriteThroughLatency(FileStream _, CancellationToken token)
        {
            Interlocked.Increment(ref writes);
            await Task.Delay(TimeSpan.FromMilliseconds(75), token);
        }

        using var run = new FakeCodexRun(mode,
            afterStdoutWriteForTests: stdout ? SimulateWriteThroughLatency : null,
            afterStderrWriteForTests: stdout ? null : SimulateWriteThroughLatency);
        var failure = await Record.ExceptionAsync(() => run.Adapter.DecideAsync(
            "obligation-1", Request, Context, run.Directory, TestContext.Current.CancellationToken));
        var elapsed = Stopwatch.GetElapsedTime(started);
        var stdoutBytes = new FileInfo(run.StdoutPath).Length;
        var stderrBytes = new FileInfo(run.StderrPath).Length;
        Assert.True(failure is InvalidOperationException
                && failure.Message.Contains("exceeded 1 MiB", StringComparison.Ordinal),
            $"Expected bounded-overflow InvalidOperationException; got {failure?.GetType().Name}: "
            + $"{failure?.Message}; elapsed {elapsed}; durable writes {writes}; "
            + $"stdout {stdoutBytes} bytes; stderr {stderrBytes} bytes.");
        Assert.True(writes <= 32,
            $"Capture needed {writes} WriteThrough writes in {elapsed}; stdout {stdoutBytes} bytes; "
            + $"stderr {stderrBytes} bytes.");
        Assert.Equal(CodexReadinessDecisionAdapter.MaxStreamBytes,
            new FileInfo(stdout ? run.StdoutPath : run.StderrPath).Length);
        AssertStopped(run);
    }

    [Fact]
    public async Task A_failed_evidence_write_is_not_replayed_during_capture_cleanup()
    {
        var writes = 0;
        long persistedAtFailure = 0;
        Task FailAfterSecondWrite(FileStream evidence, CancellationToken _)
        {
            if (Interlocked.Increment(ref writes) == 2)
            {
                persistedAtFailure = evidence.Length;
                return Task.FromException(new IOException("simulated post-write capture failure"));
            }

            return Task.CompletedTask;
        }

        using var run = new FakeCodexRun("stdout-overflow", afterStdoutWriteForTests: FailAfterSecondWrite);
        var exception = await Assert.ThrowsAsync<IOException>(() => run.Adapter.DecideAsync(
            "obligation-1", Request, Context, run.Directory, TestContext.Current.CancellationToken));
        Assert.Contains("simulated post-write", exception.Message, StringComparison.Ordinal);
        Assert.Equal(2, writes);
        Assert.True(persistedAtFailure > 0 && persistedAtFailure < CodexReadinessDecisionAdapter.MaxStreamBytes);
        Assert.Equal(persistedAtFailure, new FileInfo(run.StdoutPath).Length);
        AssertStopped(run);
    }

    [Fact]
    public async Task A_nonzero_exit_is_rejected_after_stderr_is_retained()
    {
        using var run = new FakeCodexRun("nonzero");
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => run.Adapter.DecideAsync(
            "obligation-1", Request, Context, run.Directory, TestContext.Current.CancellationToken));
        Assert.Contains("exited 7", exception.Message, StringComparison.Ordinal);
        Assert.Contains("nonzero diagnostic", File.ReadAllText(run.StderrPath));
        AssertStopped(run);
    }

    [Fact]
    public async Task A_timeout_retains_diagnostics_and_terminates_the_owned_child()
    {
        // Four seconds covers apphost startup on loaded CI machines; the child writes its PID and
        // diagnostics before sleeping. A missing PID is a test failure, not a cleanup success.
        using var run = new FakeCodexRun("timeout", TimeSpan.FromSeconds(4));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.Adapter.DecideAsync(
            "obligation-1", Request, Context, run.Directory, TestContext.Current.CancellationToken));
        Assert.Contains("thread.started", File.ReadAllText(run.StdoutPath));
        Assert.Contains("timeout diagnostic", File.ReadAllText(run.StderrPath));
        AssertStopped(run);
    }

    [Fact]
    public async Task A_timeout_waits_for_owned_capture_before_diagnostics_are_readable()
    {
        if (!OperatingSystem.IsWindows()) return; // Windows share violations are the CI symptom.

        var captureEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var captureCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCapture = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var captureSettled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var run = new FakeCodexRun("timeout", TimeSpan.FromSeconds(4), async (evidence, token) =>
        {
            // An in-flight Windows FileStream write may retain its SafeHandle after the stream
            // is disposed. Hold that exact handle, not a second unrelated file lock, while
            // cancellation tears down the child and the capture task finishes.
            var handle = evidence.SafeFileHandle;
            var held = false;
            handle.DangerousAddRef(ref held);
            try
            {
                captureEntered.SetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    captureCanceled.SetResult();
                    await releaseCapture.Task;
                    throw;
                }
            }
            finally
            {
                if (held) handle.DangerousRelease();
                captureSettled.SetResult();
            }
        });

        var decisionTask = run.Adapter.DecideAsync("obligation-1", Request, Context, run.Directory,
            TestContext.Current.CancellationToken);
        try
        {
            await captureEntered.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            using var child = Process.GetProcessById(run.Pid);
            await captureCanceled.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            await child.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

            // Once the child is gone, the old failure path returns without joining this capture.
            // Reproduce CI's exact unreadable-file symptom if it returns while our handle is held.
            if (await Task.WhenAny(decisionTask,
                    Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken)) == decisionTask) // wait-ok: observe premature return before the five-second capture cleanup deadline
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => decisionTask);
                Assert.Contains("thread.started", File.ReadAllText(run.StdoutPath));
            }
        }
        finally
        {
            releaseCapture.TrySetResult();
            try
            {
                if (captureEntered.Task.IsCompleted)
                    await captureSettled.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            }
            finally
            {
                try
                {
                    await decisionTask.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
                }
                catch (Exception)
                {
                    // The assertion outside the cleanup path checks the original outcome.
                }
            }
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            decisionTask.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken));
        Assert.Contains("thread.started", File.ReadAllText(run.StdoutPath));
        Assert.Contains("timeout diagnostic", File.ReadAllText(run.StderrPath));
        AssertStopped(run);
    }

    [Fact]
    public async Task A_capture_that_outlives_cleanup_reports_incomplete_diagnostics()
    {
        var captureEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCapture = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var captureSettled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var run = new FakeCodexRun("timeout", TimeSpan.FromSeconds(4), async (_, token) =>
        {
            captureEntered.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                await releaseCapture.Task;
                throw;
            }
            finally
            {
                captureSettled.SetResult();
            }
        });
        var decisionTask = run.Adapter.DecideAsync("obligation-1", Request, Context, run.Directory,
            TestContext.Current.CancellationToken);
        try
        {
            await captureEntered.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                decisionTask.WaitAsync(TimeSpan.FromSeconds(11), TestContext.Current.CancellationToken)); // wait-ok: bound the four-second request timeout plus five-second cleanup; detect cleanup deadline regressions
            Assert.Contains("capture did not settle within the cleanup bound", error.Message);
            Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
            AssertStopped(run);
        }
        finally
        {
            releaseCapture.TrySetResult();
            try
            {
                if (captureEntered.Task.IsCompleted)
                    await captureSettled.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            }
            finally
            {
                try
                {
                    await decisionTask.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
                }
                catch (Exception)
                {
                    // The assertion above checks the explicit cleanup-bound failure.
                }
            }
        }
        Assert.Contains("thread.started", File.ReadAllText(run.StdoutPath));
        Assert.Contains("timeout diagnostic", File.ReadAllText(run.StderrPath));
    }

    private static void AssertStopped(FakeCodexRun run)
    {
        Assert.True(run.Pid > 0, "the fake executable must have started and recorded its PID");
        Assert.False(FakeCodexRun.IsAlive(run.Pid), "the owned child must be gone after the adapter returns");
    }
}

internal sealed class FakeCodexRun : IDisposable
{
    private readonly string _root;
    public string Directory => _root;
    public string PidPath => Path.Combine(_root, "fake.pid");
    public string SchemaPath => Path.Combine(_root, "decision.schema.json");
    public string AnswerPath => Path.Combine(_root, "decision.json");
    public string StdoutPath => Path.Combine(_root, "codex.stdout.jsonl");
    public string StderrPath => Path.Combine(_root, "codex.stderr.txt");
    public int Pid => File.Exists(PidPath) && int.TryParse(File.ReadAllText(PidPath), out var pid) ? pid : 0;
    public CodexReadinessDecisionAdapter Adapter { get; }

    public FakeCodexRun(string mode, TimeSpan? timeout = null,
        Func<FileStream, CancellationToken, Task>? afterStdoutWriteForTests = null,
        Func<FileStream, CancellationToken, Task>? afterStderrWriteForTests = null)
    {
        _root = Path.Combine(Path.GetTempPath(), "baton-readiness-run-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "mode.txt"), mode);
        var hostDirectory = Path.GetDirectoryName(typeof(Scenarios).Assembly.Location)!;
        var executable = Path.Combine(hostDirectory,
            "Baton.CrashTestHost" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
        Assert.True(File.Exists(executable), "the source-built crash test apphost must be present");
        Adapter = new CodexReadinessDecisionAdapter(executable, timeout ?? TimeSpan.FromSeconds(15),
            afterStdoutWriteForTests, afterStderrWriteForTests);
    }

    public void Dispose()
    {
        var pid = Pid;
        if (pid > 0 && IsAlive(pid))
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
        }

        DirectoryCleanup.DeleteRecursively(_root);
    }

    public static bool IsAlive(int pid)
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
