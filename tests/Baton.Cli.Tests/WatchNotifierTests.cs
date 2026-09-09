using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Baton.Cli.Tests.TestSupport;

namespace Baton.Cli.Tests;

/// <summary>
/// <c>WatchNotifier</c> (#1488, spec/baton.md §2): the two <c>--notify</c> shapes, a URL POST and a
/// spawned command. See also <see cref="WatchFireServiceTests"/> for the surrounding claim/exactly-once
/// logic — this file only exercises the notifier's own send mechanics.
/// </summary>
public sealed class WatchNotifierTests
{
    private static WatchNotifyPayload SamplePayload() => new(
        Room: @"C:\rooms\room-1",
        State: "Succeeded",
        Verdict: null,
        Outputs: ["out.md"],
        TerminalAt: new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc));

    [Theory]
    [InlineData("https://example.invalid/hook")]
    [InlineData("http://example.invalid/hook")]
    public void IsHttpUrl_RecognizesHttpAndHttps(string url) => Assert.True(WatchNotifier.IsHttpUrl(url));

    [Theory]
    [InlineData("curl -X POST https://ntfy.sh/mytopic")]
    [InlineData(@"C:\scripts\wake.cmd")]
    [InlineData("ftp://example.invalid/x")]
    public void IsHttpUrl_RejectsEverythingElse(string target) => Assert.False(WatchNotifier.IsHttpUrl(target));

    [Fact]
    public async Task NotifyAsync_UrlTarget_PostsTheJsonPayloadToTheExactUrl()
    {
        var handler = new RecordingHttpMessageHandler();
        using var httpClient = new HttpClient(handler);
        var notifier = new WatchNotifier(httpClient);
        var payload = SamplePayload();

        await notifier.NotifyAsync("https://example.invalid/hook", payload, TestContext.Current.CancellationToken);

        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://example.invalid/hook", request.RequestUri!.ToString());
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);

        var deserialized = JsonSerializer.Deserialize<WatchNotifyPayload>(body);
        Assert.Equal(payload.Room, deserialized!.Room);
        Assert.Equal(payload.State, deserialized.State);
        Assert.Equal(payload.Outputs, deserialized.Outputs);
    }

    [Fact]
    public async Task NotifyAsync_UrlTarget_NonSuccessStatus_DoesNotThrow()
    {
        // Best-effort delivery: a non-2xx response is logged (stderr), never surfaced as an exception
        // that would make WatchFireService think the claim itself failed.
        var handler = new RecordingHttpMessageHandler { ResponseStatusCode = HttpStatusCode.InternalServerError };
        using var httpClient = new HttpClient(handler);
        var notifier = new WatchNotifier(httpClient);

        await notifier.NotifyAsync("https://example.invalid/hook", SamplePayload(), TestContext.Current.CancellationToken);

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task NotifyAsync_CommandTarget_ReceivesThePayloadOnStdinAndTheEnvironmentVariable()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"baton-watch-notifier-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var scriptPath = Path.Combine(tempDir, "capture.ps1");
        var stdinMarkerPath = Path.Combine(tempDir, "stdin.txt");
        var envMarkerPath = Path.Combine(tempDir, "env.txt");

        try
        {
            // Paths are baked directly into the generated script rather than passed as command-line
            // arguments, so this test carries no risk of cmd.exe/PowerShell argument-quoting drift --
            // the ONLY thing under test is whether WatchNotifier's own spawn wires stdin/the env var
            // correctly, not whether an operator's command-line quoting works.
            var errorMarkerPath = Path.Combine(tempDir, "error.txt");
            var script =
                "try {\n" +
                "$stdin = [Console]::In.ReadToEnd()\n" +
                $"[System.IO.File]::WriteAllText('{stdinMarkerPath}', $stdin)\n" +
                $"[System.IO.File]::WriteAllText('{envMarkerPath}', $env:{WatchNotifier.NotifyEventEnvironmentVariable})\n" +
                "} catch {\n" +
                $"[System.IO.File]::WriteAllText('{errorMarkerPath}', $_.Exception.ToString())\n" +
                "}\n";
            await File.WriteAllTextAsync(scriptPath, script, TestContext.Current.CancellationToken);

            var notifier = new WatchNotifier();
            var payload = SamplePayload();
            var command = $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"";

            await notifier.NotifyAsync(command, payload, TestContext.Current.CancellationToken);

            var errorText = File.Exists(errorMarkerPath) ? File.ReadAllText(errorMarkerPath) : "(no error.txt)";
            Assert.True(
                File.Exists(stdinMarkerPath),
                $"the spawned command never observed stdin close, or never ran. tempDir={tempDir} error={errorText}");
            var stdinContent = await File.ReadAllTextAsync(stdinMarkerPath, TestContext.Current.CancellationToken);
            var envContent = await File.ReadAllTextAsync(envMarkerPath, TestContext.Current.CancellationToken);

            var stdinPayload = JsonSerializer.Deserialize<WatchNotifyPayload>(stdinContent);
            Assert.Equal(payload.Room, stdinPayload!.Room);
            Assert.Equal(payload.State, stdinPayload.State);

            var envPayload = JsonSerializer.Deserialize<WatchNotifyPayload>(envContent);
            Assert.Equal(payload.Room, envPayload!.Room);
            Assert.Equal(payload.State, envPayload.State);

            // The env variable and stdin carry the identical bytes -- one serialization, two transports.
            Assert.Equal(stdinContent, envContent);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempDir);
        }
    }

    /// <summary>
    /// H1 (fix round): before the fix, the stdin write happened BEFORE any timeout was armed, so a
    /// command that never reads stdin blocked the write once the payload exceeded the OS pipe buffer
    /// (~4 KB on Windows) — the documented `curl -X POST …` shape. Red-first against the pre-fix code:
    /// this hangs (or times out at 30 s if the caller's own token has a deadline) instead of returning
    /// promptly. A short <c>commandTimeout</c> keeps the test itself fast while still exercising the
    /// exact code path a production 30 s timeout would.
    /// </summary>
    [Fact]
    public async Task NotifyAsync_CommandTarget_NeverDrainsStdin_ReturnsWithinTheTimeoutInsteadOfHangingForever()
    {
        var notifier = new WatchNotifier(commandTimeout: TimeSpan.FromMilliseconds(500));
        var largePayload = SamplePayload() with
        {
            Outputs = Enumerable.Range(0, 2000).Select(i => $"out-{i}.md").ToArray(),
        };
        Assert.True(
            JsonSerializer.Serialize(largePayload).Length > 8192,
            "test payload must exceed the pipe buffer to actually exercise the block.");

        var stopwatch = Stopwatch.StartNew();
        await notifier.NotifyAsync(
            "cmd /c ping -n 30 127.0.0.1 >nul", largePayload, TestContext.Current.CancellationToken);
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"NotifyAsync took {stopwatch.Elapsed} — the stdin write should have been killed at ~500ms, " +
            "not blocked for the command's ~30s runtime (or forever, pre-fix).");
    }

    /// <summary>
    /// #2117 re-review, finding 1: the command's stdout/stderr are redirected and drained rather than
    /// inherited, because inside the daemon the inherit flag is gone after the first detached queue
    /// launch and an inherited stream's writes then vanish. The drain is the half that matters: a
    /// redirect with no reader blocks the command once it has written the pipe buffer (~4 KB), so
    /// this command writes several times that on stdout and one line on stderr, and both the
    /// completion (well inside the command timeout) and the captured text are asserted. The last
    /// stdout line is the discriminating one — a stalled or absent drain never sees it. Sabotage
    /// check made while writing this: with the two <c>Begin*ReadLine</c> calls removed, the command
    /// stalls at the buffer, the OS-exit wait times out, and the assertion on the last line fails.
    /// </summary>
    [Fact]
    public async Task NotifyAsync_CommandTarget_WritesPastThePipeBufferOnStdout_CompletesAndItsOutputIsCaptured()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var commandTimeout = TimeSpan.FromSeconds(10);
        var notifier = new WatchNotifier(commandTimeout: commandTimeout, commandOutput: output, commandError: error);

        // 400 lines of ~50 bytes: ~20 KB on stdout, five times the pipe buffer; then one stderr line.
        const string command =
            "(for /L %i in (1,1,400) do @echo stdout-line-%i-0123456789abcdef0123456789abcdef) & echo stderr-marker 1>&2";

        var stopwatch = Stopwatch.StartNew();
        await notifier.NotifyAsync(command, SamplePayload(), TestContext.Current.CancellationToken);
        stopwatch.Stop();

        var captured = output.ToString();
        Assert.True(
            stopwatch.Elapsed < commandTimeout,
            $"NotifyAsync took {stopwatch.Elapsed} — the command stalled on an undrained pipe until the timeout.");
        Assert.True(
            captured.Length > 4096,
            $"only {captured.Length} chars of stdout were captured; the drain stopped at the pipe buffer.");
        Assert.Contains("stdout-line-400-", captured, StringComparison.Ordinal);
        Assert.Contains("stderr-marker", error.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// L1: the property that payload content never reaches the spawned command's argv already holds
    /// (verified by reading — <c>command</c>'s argv is built from the <c>--notify</c> target alone,
    /// never from any <see cref="WatchNotifyPayload"/> field), but nothing previously went red if a
    /// future refactor started interpolating. This asserts the discriminating side effect directly: a
    /// room path crafted to look like a shell-injection payload produces no side effect, and the exact
    /// same string still arrives on stdin, untouched.
    /// </summary>
    [Fact]
    public async Task NotifyAsync_CommandTarget_PayloadFieldLooksLikeShellInjection_NeverInterpolatedIntoTheCommand()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"baton-watch-notifier-inject-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var scriptPath = Path.Combine(tempDir, "capture.ps1");
        var stdinMarkerPath = Path.Combine(tempDir, "stdin.txt");
        var errorMarkerPath = Path.Combine(tempDir, "error.txt");
        var sideEffectMarkerPath = Path.Combine(tempDir, "pwned.txt");

        try
        {
            var script =
                "try {\n" +
                "$stdin = [Console]::In.ReadToEnd()\n" +
                $"[System.IO.File]::WriteAllText('{stdinMarkerPath}', $stdin)\n" +
                "} catch {\n" +
                $"[System.IO.File]::WriteAllText('{errorMarkerPath}', $_.Exception.ToString())\n" +
                "}\n";
            await File.WriteAllTextAsync(scriptPath, script, TestContext.Current.CancellationToken);

            var notifier = new WatchNotifier();
            var maliciousRoom = $"C:\\rooms\\room-1 & echo pwned > {sideEffectMarkerPath} & rem";
            var payload = SamplePayload() with { Room = maliciousRoom };
            var command = $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"";

            await notifier.NotifyAsync(command, payload, TestContext.Current.CancellationToken);

            Assert.False(
                File.Exists(sideEffectMarkerPath),
                "the payload's Room content ran as a shell command instead of staying inert JSON data " +
                "delivered only via stdin/BATON_WATCH_EVENT.");

            var errorText = File.Exists(errorMarkerPath) ? File.ReadAllText(errorMarkerPath) : "(no error.txt)";
            Assert.True(
                File.Exists(stdinMarkerPath),
                $"the spawned command never observed stdin close, or never ran. error={errorText}");
            var stdinContent = await File.ReadAllTextAsync(stdinMarkerPath, TestContext.Current.CancellationToken);
            var stdinPayload = JsonSerializer.Deserialize<WatchNotifyPayload>(stdinContent);
            Assert.Equal(payload.Room, stdinPayload!.Room);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempDir);
        }
    }
}
