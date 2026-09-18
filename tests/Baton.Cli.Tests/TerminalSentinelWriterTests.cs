using Baton.Status;

namespace Baton.Cli.Tests;

/// <summary>
/// #1374 F2 unit coverage for <see cref="TerminalSentinelWriter"/>'s write atomicity and malformed-read
/// handling, isolated from the real-process wiring <see cref="TerminalSentinelEndToEndTests"/> covers.
/// </summary>
public class TerminalSentinelWriterTests
{
    [Fact]
    public async Task TryWriteValidationRefused_returns_false_when_the_room_path_is_a_file()
    {
        // #2387: deterministic no-ACL denial. Program has already reported the validation
        // refusal by this point, so failure to create the optional terminal sentinel must be
        // contained rather than escaping through baton.exe.
        var root = Path.Combine(Path.GetTempPath(), $"terminal-sentinel-denied-{Guid.NewGuid():N}");
        var roomPath = Path.Combine(root, "room-is-a-file");
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(roomPath, "not a directory", TestContext.Current.CancellationToken);

            var written = await TerminalSentinelWriter.TryWriteValidationRefusedAsync(
                roomPath, "validation refused", TestContext.Current.CancellationToken);

            Assert.False(written);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public async Task WriteAsync_leaves_no_temp_file_behind_and_the_written_sentinel_round_trips()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"sentinel-atomic-{Guid.NewGuid():N}");
        try
        {
            var view = new WorkflowStatusView("Succeeded", [], ["C:/room/artifacts/plan"], null);

            await TerminalSentinelWriter.WriteAsync(roomDirectory, view, TestContext.Current.CancellationToken);

            // The temp-then-rename write (#1374 F2) must not leave its own temp sibling behind --
            // exactly one file, and it is the sentinel itself, not a stray "*.tmp".
            var entries = Directory.GetFiles(roomDirectory);
            var entry = Assert.Single(entries);
            Assert.Equal(TerminalSentinelWriter.TerminalSentinelFileName, Path.GetFileName(entry));

            var readBack = await TerminalSentinelWriter.TryReadAsync(roomDirectory, TestContext.Current.CancellationToken);
            Assert.NotNull(readBack);
            Assert.Equal("Succeeded", readBack!.State);
            Assert.Equal(view.Outputs, readBack.Outputs);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task WriteAsync_overwrites_a_prior_sentinel_leaving_only_the_new_one_on_disk()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"sentinel-overwrite-{Guid.NewGuid():N}");
        try
        {
            await TerminalSentinelWriter.WriteAsync(
                roomDirectory, new WorkflowStatusView("Failed", [], [], "first"), TestContext.Current.CancellationToken);
            await TerminalSentinelWriter.WriteAsync(
                roomDirectory, new WorkflowStatusView("Succeeded", [], [], null), TestContext.Current.CancellationToken);

            var entries = Directory.GetFiles(roomDirectory);
            var entry = Assert.Single(entries);
            Assert.Equal(TerminalSentinelWriter.TerminalSentinelFileName, Path.GetFileName(entry));

            var readBack = await TerminalSentinelWriter.TryReadAsync(roomDirectory, TestContext.Current.CancellationToken);
            Assert.Equal("Succeeded", readBack!.State);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task TryReadAsync_deserializes_a_pre_1569_terminal_json_with_the_new_fields_absent()
    {
        // #1569 compatibility: a real terminal.json captured before this change (a "usage" object
        // carrying only wallClockMs, no tokensIn/tokensOut/turns and no cache/thinking keys at all --
        // ~/.baton/rooms/dispatch-implement-01f4291f/terminal.json, read-only, copied verbatim) must
        // still deserialize, and the three new ExecutionUsageView fields must come back null rather
        // than throwing or defaulting to zero.
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"sentinel-pre1569-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(roomDirectory);
            var path = Path.Combine(roomDirectory, TerminalSentinelWriter.TerminalSentinelFileName);
            await File.WriteAllTextAsync(
                path,
                """
                {
                  "state": "Succeeded",
                  "steps": [
                    {
                      "id": "implement",
                      "state": "Succeeded",
                      "execution": "6560347b1f9246ba8345465933efa02f",
                      "linkedFrom": null,
                      "usage": {
                        "wallClockMs": 2258434
                      }
                    }
                  ],
                  "outputs": [
                    "C:\\room\\artifacts\\report.md"
                  ],
                  "error": null,
                  "try": null,
                  "rejected": false
                }
                """,
                TestContext.Current.CancellationToken);

            var readBack = await TerminalSentinelWriter.TryReadAsync(roomDirectory, TestContext.Current.CancellationToken);

            Assert.NotNull(readBack);
            Assert.Equal("Succeeded", readBack!.State);
            var step = Assert.Single(readBack.Steps);
            Assert.NotNull(step.Usage);
            Assert.Equal(2258434, step.Usage!.WallClockMs);
            Assert.Null(step.Usage.TokensIn);
            Assert.Null(step.Usage.TokensOut);
            Assert.Null(step.Usage.Turns);
            Assert.Null(step.Usage.CacheReadTokens);
            Assert.Null(step.Usage.CacheCreationTokens);
            Assert.Null(step.Usage.ThinkingTokens);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task TryReadAsync_treats_a_torn_or_malformed_sentinel_as_absent_rather_than_throwing()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"sentinel-torn-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(roomDirectory);
            var path = Path.Combine(roomDirectory, TerminalSentinelWriter.TerminalSentinelFileName);
            // What a reader could observe of a write caught mid-move, or a hand-corrupted file:
            // either way, not valid JSON matching WorkflowStatusView's shape (#1374 F2).
            await File.WriteAllTextAsync(path, "{\"state\":\"Succ", TestContext.Current.CancellationToken);

            var result = await TerminalSentinelWriter.TryReadAsync(roomDirectory, TestContext.Current.CancellationToken);

            Assert.Null(result);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }
}
