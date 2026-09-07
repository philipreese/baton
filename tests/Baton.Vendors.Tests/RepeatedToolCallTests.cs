using System.Text.Json;
using Baton.Domain;
using Baton.Tests.Shared;
using Baton.Vendors;
using Xunit;

namespace Baton.Vendors.Tests;

/// <summary>
/// #2002 rules 1, 2 and 2b, driven through the broker rather than the ledger alone: the ledger's
/// verdict is only worth anything if the run-command and read-text handlers actually act on it, and
/// the handlers are where a refusal has to arrive BEFORE anything is spawned or served.
/// </summary>
public sealed class RepeatedToolCallTests
{
    /// <summary>
    /// The instant, deterministic command both shells this policy starts already have. Its output is
    /// stable, which is what lets a replay be compared byte-for-byte against the execution it stands in
    /// for.
    /// </summary>
    private static string StableCommand => OperatingSystem.IsWindows() ? "ver" : "uname";

    private static string StablePattern => OperatingSystem.IsWindows() ? "ver*" : "uname*";

    /// <summary>
    /// Rule 2's three arms in the order the issue states them. The polling shape it was measured on is
    /// <c>Get-Process -Id &lt;pid&gt;</c>; a cheap builtin stands in for it because what is under test
    /// is the ledger's verdict on a byte-identical line, not what the line happens to run.
    /// <para>
    /// <b>The replay is proof no process ran</b>, not merely evidence: the preamble is produced on the
    /// branch that returns before <c>Process.Start</c> is reached, so a body carrying it cannot have
    /// come from a second execution. The refusal is the same proof one step further on.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Three_identical_commands_are_one_execution_one_replay_and_one_refusal()
    {
        using var fixture = new RepeatFixture(StablePattern);

        var first = await fixture.RunAsync(StableCommand);
        fixture.Clock.Advance(TimeSpan.FromSeconds(12));
        var second = await fixture.RunAsync(StableCommand);
        var third = await fixture.RunAsync(StableCommand);

        Assert.True(first.Success);
        Assert.DoesNotContain("replayed:", first.Text, StringComparison.Ordinal);

        Assert.True(second.Success);
        Assert.Contains("[replayed: identical command 12 s ago]", second.Text, StringComparison.Ordinal);
        Assert.EndsWith(first.Text, second.Text, StringComparison.Ordinal);

        Assert.False(third.Success);
        Assert.Contains(
            "the previous run is still the answer; nothing runs in the background here",
            third.Text,
            StringComparison.Ordinal);
        Assert.Contains(GrantRefusal.Marker, third.Text);
    }

    /// <summary>
    /// The control, and the arm that makes the test above about repeats rather than about the second
    /// call of anything: a command on the volatile allowlist runs every single time. Without this,
    /// a build that refused every second command would pass the theory above.
    /// </summary>
    [Fact]
    public async Task Three_identical_git_status_calls_are_three_executions()
    {
        using var fixture = new RepeatFixture("git status*");

        var results = new List<CodexDynamicToolResult>();
        for (var i = 0; i < 3; i++)
        {
            results.Add(await fixture.RunAsync("git status --short"));
        }

        Assert.All(results, result =>
        {
            Assert.DoesNotContain("replayed:", result.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(GrantRefusal.Marker, result.Text);
        });
    }

    /// <summary>
    /// Outside the window nothing is a repeat: the world may have moved, and the ledger has no way to
    /// say it did not. The polarity partner of the 12-seconds-later replay above.
    /// </summary>
    [Fact]
    public async Task An_identical_command_past_the_window_executes_again()
    {
        using var fixture = new RepeatFixture(StablePattern);

        await fixture.RunAsync(StableCommand);
        fixture.Clock.Advance(RepeatedToolCallLedger.Window + TimeSpan.FromSeconds(1));
        var later = await fixture.RunAsync(StableCommand);

        Assert.True(later.Success);
        Assert.DoesNotContain("replayed:", later.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Rule 1 through the broker, with the no-spawn claim made falsifiable rather than asserted: the
    /// fixture's command ceiling is 150 ms and the backgrounded command would take far longer, so a
    /// build that spawned it would come back with the tool-limit failure text instead of this refusal.
    /// </summary>
    [Fact]
    public async Task A_backgrounded_command_is_refused_and_nothing_is_spawned()
    {
        var sleep = OperatingSystem.IsWindows() ? "ping -n 30 127.0.0.1" : "sleep 30";
        using var fixture = new RepeatFixture(
            "ping*", "sleep*", commandTimeout: TimeSpan.FromMilliseconds(150));

        var result = await fixture.RunAsync(sleep + " &");

        Assert.False(result.Success);
        Assert.Contains("backgrounds the work (a trailing &)", result.Text, StringComparison.Ordinal);
        Assert.Contains("runs to completion synchronously", result.Text, StringComparison.Ordinal);
        Assert.Contains("minute tool limit", result.Text, StringComparison.Ordinal);
        // The falsifier: a build that spawned this would have come back with the timeout FAILURE below
        // rather than a refusal, because the command outlives the fixture's 150 ms ceiling by minutes.
        Assert.DoesNotContain("Command exceeded", result.Text, StringComparison.Ordinal);
        Assert.Contains(GrantRefusal.Marker, result.Text);
    }

    /// <summary>
    /// Rule 2b's three arms. The predicate is the file's own stat, so this drives the file rather than
    /// the clock — the fixture's clock never advances here, and that is the point.
    /// </summary>
    [Fact]
    public async Task An_unchanged_file_is_replayed_then_refused()
    {
        using var fixture = new RepeatFixture(StablePattern);
        var path = fixture.WriteWorkspaceFile("notes.md", "one");

        var first = await fixture.ReadAsync(path);
        var second = await fixture.ReadAsync(path);
        var third = await fixture.ReadAsync(path);

        Assert.Equal("one", first.Text);
        Assert.Contains("replayed: identical read", second.Text, StringComparison.Ordinal);
        Assert.EndsWith("one", second.Text, StringComparison.Ordinal);
        Assert.False(third.Success);
        Assert.Contains("the previous read is still the answer", third.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A file something else rewrote between two reads is read again, truthfully, however fast the
    /// re-ask came — the reason the read predicate is a stat rather than the 60-second clock the
    /// command rule uses.
    /// </summary>
    [Fact]
    public async Task A_file_rewritten_by_another_process_is_read_again()
    {
        using var fixture = new RepeatFixture(StablePattern);
        var path = fixture.WriteWorkspaceFile("build.log", "before");

        var first = await fixture.ReadAsync(path);
        // The "external process" for this test's purposes: a writer that is not the broker, so the
        // eviction below plays no part and only the stat predicate can be what re-reads this.
        File.WriteAllText(path, "after the build rewrote it");

        var second = await fixture.ReadAsync(path);

        Assert.Equal("before", first.Text);
        Assert.Equal("after the build rewrote it", second.Text);
        Assert.DoesNotContain("replayed:", second.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The eviction arm — see <c>CodexDynamicToolPolicy.WriteText</c> for what it is for and why the
    /// stat pair cannot cover it. Written to land exactly on that case: same byte count, same tick,
    /// and the next read must still execute.
    /// </summary>
    [Fact]
    public async Task The_rooms_own_write_makes_the_next_read_execute()
    {
        using var fixture = new RepeatFixture(StablePattern);
        var path = fixture.WriteWorkspaceFile("draft.md", "aaa");

        var first = await fixture.ReadAsync(path);
        await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.WriteTextTool, new { path, content = "bbb" });
        var second = await fixture.ReadAsync(path);

        Assert.Equal("aaa", first.Text);
        Assert.Equal("bbb", second.Text);
        Assert.DoesNotContain("replayed:", second.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A clock a test can drive, so a 60-second window is exercised in microseconds. Only
    /// <see cref="GetUtcNow"/> is ever asked of it by the ledger.
    /// </summary>
    private sealed class StepClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class RepeatFixture : IDisposable
    {
        public RepeatFixture(params string[] shellPatterns)
            : this(shellPatterns, null)
        {
        }

        public RepeatFixture(string shellPattern, string secondPattern, TimeSpan? commandTimeout)
            : this([shellPattern, secondPattern], commandTimeout)
        {
        }

        private RepeatFixture(string[] shellPatterns, TimeSpan? commandTimeout)
        {
            Root = Path.Combine(Path.GetTempPath(), $"baton-repeat-{Guid.NewGuid():N}");
            Workspace = Path.Combine(Root, "workspace");
            Output = Path.Combine(Root, "output");
            Directory.CreateDirectory(Workspace);
            Directory.CreateDirectory(Output);
            Clock = new StepClock(new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero));
            Policy = new CodexDynamicToolPolicy(
                new PermissionGrant(
                    ReadFiles: true,
                    WriteFiles: true,
                    RunShellCommands: true,
                    ShellCommandPatterns: shellPatterns,
                    ShellCommandsAreReadOnly: true),
                Workspace,
                Output,
                [],
                ["report.md"],
                commandTimeout,
                Clock);
        }

        public string Root { get; }
        public string Workspace { get; }
        public string Output { get; }
        public StepClock Clock { get; }
        public CodexDynamicToolPolicy Policy { get; }

        public string WriteWorkspaceFile(string name, string content)
        {
            var path = Path.Combine(Workspace, name);
            File.WriteAllText(path, content);
            return path;
        }

        public Task<CodexDynamicToolResult> RunAsync(string command) =>
            ExecuteAsync(CodexDynamicToolPolicy.RunCommandTool, new { command });

        public Task<CodexDynamicToolResult> ReadAsync(string path) =>
            ExecuteAsync(CodexDynamicToolPolicy.ReadTextTool, new { path });

        public async Task<CodexDynamicToolResult> ExecuteAsync(string toolName, object arguments)
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(arguments));
            return await Policy.ExecuteAsync(toolName, doc.RootElement, TestContext.Current.CancellationToken);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                DirectoryCleanup.DeleteRecursively(Root);
            }
        }
    }
}
