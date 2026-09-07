using Baton.Mutation;
using Baton.Status;
using Xunit;

namespace Baton.Tests.Mutation;

/// <summary>
/// #2002 rule 3, read end to end off the vendors' own stream envelopes: the normaliser alone proves
/// nothing if <see cref="TokenBudgetMonitor"/> never sees a command line, and the measured room this
/// rule exists for is agy's, whose stream shape differs from claude's in every respect but the idea.
/// </summary>
public sealed class DominantCommandShapeTests
{
    /// <summary>
    /// The measured #2002 arm-A agy room in miniature: liveness polls of processes the worker had
    /// backgrounded itself, differing only in the pid, plus enough real work to keep the share honest.
    /// The room itself read 53.6 % over 207 <c>run_command</c> steps; this fixture is the same shape at
    /// a size a test can hold.
    /// </summary>
    [Fact]
    public void An_agy_stream_of_pid_polls_reports_one_dominant_shape()
    {
        var monitor = NewMonitor(new AgyUsageParser());

        foreach (var pid in new[] { 59340, 17056, 55796, 8904, 41118 })
        {
            monitor.OnStdoutLine(AgyRunCommand($"Get-Process -Id {pid} -ErrorAction SilentlyContinue"));
        }

        monitor.OnStdoutLine(AgyRunCommand("dotnet build -warnaserror"));
        monitor.OnStdoutLine(AgyRunCommand("git commit -m checkpoint"));

        var dominant = monitor.SnapshotDominantCommandShape();

        Assert.NotNull(dominant);
        Assert.Equal("Get-Process -Id <n> -ErrorAction SilentlyContinue", dominant.Value.Shape);
        Assert.Equal(71, dominant.Value.Percent);
    }

    /// <summary>
    /// The control that makes the assertion above about DOMINANCE rather than about "the most common
    /// shape": five distinct commands and a repeat leave the top shape at a third, and nothing is
    /// claimed. Without this arm, a build reporting the plurality would pass.
    /// </summary>
    [Fact]
    public void A_stream_with_no_majority_shape_claims_nothing()
    {
        var monitor = NewMonitor(new AgyUsageParser());

        foreach (var command in new[]
                 {
                     "dotnet build", "dotnet build", "git status --short", "pixi run test",
                     "gh pr view 1991", "git log -n 5 --oneline",
                 })
        {
            monitor.OnStdoutLine(AgyRunCommand(command));
        }

        Assert.Null(monitor.SnapshotDominantCommandShape());
    }

    /// <summary>
    /// The same reading off claude's envelope, which nests the command at
    /// <c>message.content[].input.command</c> under the tool name <c>Bash</c>. The 2026-09-06 audit
    /// measured claude's polling as real but small, so this arm is about the rule being vendor-neutral
    /// rather than about claude being the offender.
    /// </summary>
    [Fact]
    public void A_claude_stream_is_read_the_same_way()
    {
        var monitor = NewMonitor(new ClaudeUsageParser());

        monitor.OnStdoutLine(ClaudeBash("gh pr checks 1991"));
        monitor.OnStdoutLine(ClaudeBash("gh pr checks 1991"));
        monitor.OnStdoutLine(ClaudeBash("dotnet test"));

        var dominant = monitor.SnapshotDominantCommandShape();

        Assert.NotNull(dominant);
        Assert.Equal("gh pr checks <n>", dominant.Value.Shape);
        Assert.Equal(67, dominant.Value.Percent);
    }

    /// <summary>
    /// A stream that announced no shell command at all — the codex case, whose envelope carries a tool
    /// name and never its arguments — reports nothing rather than a shape derived from nothing.
    /// </summary>
    [Fact]
    public void A_stream_with_no_readable_command_lines_reports_nothing()
    {
        var monitor = NewMonitor(new CodexUsageParser());

        monitor.OnStdoutLine(AgyRunCommand("Get-Process -Id 59340"));

        Assert.Null(monitor.SnapshotDominantCommandShape());
    }

    /// <summary>
    /// The normaliser's two collapses, and the boundary between them: a pid is a number, a sha is a
    /// hash, and a hex-looking fragment glued into a word (<c>Win32_Process</c>) is neither a hash nor
    /// a reason to lose the word.
    /// </summary>
    [Theory]
    [InlineData("Get-Process -Id 59340 -ErrorAction SilentlyContinue", "Get-Process -Id <n> -ErrorAction SilentlyContinue")]
    [InlineData("git show 63e7b95e", "git show <hash>")]
    [InlineData("Get-CimInstance Win32_Process -Filter x", "Get-CimInstance Win<n>_Process -Filter x")]
    [InlineData("dotnet   test", "dotnet test")]
    [InlineData("git log -n 5 --oneline", "git log -n <n> --oneline")]
    public void The_shape_collapses_digits_and_hashes_and_nothing_else(string commandLine, string expected) =>
        Assert.Equal(expected, CommandShape.Normalize(commandLine));

    private static TokenBudgetMonitor NewMonitor(IWorkerUsageParser parser) =>
        new(budget: null, maxToolSteps: null, billedRateLimit: null, usageParser: parser);

    /// <summary>agy's terminal tool step, the one anchor every #1921/#2002 read off this vendor shares.</summary>
    private static string AgyRunCommand(string commandLine) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            @event = "step_update",
            step_update = new
            {
                step_type = "tool",
                state = "DONE",
                tool_info = new { name = "run_command", parameters = new { CommandLine = commandLine } },
            },
        });

    private static string ClaudeBash(string commandLine) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "assistant",
            message = new
            {
                content = new object[]
                {
                    new { type = "tool_use", name = "Bash", input = new { command = commandLine } },
                },
            },
        });
}
