using Baton.Artifacts;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Status;
using Baton.Store;
using Baton.Tests.Shared;

namespace Baton.Cli.Tests;

/// <summary>
/// #1921: <c>baton audit lanes</c> over a fixture set of three rooms, one per vendor, reproducing
/// hand-counted numbers — the acceptance the operator's scope addition names.
/// </summary>
/// <remarks>
/// <para>
/// The rooms are written the way production writes one — a real <c>flow.jsonl</c> through
/// <see cref="FlowEventLogWriter"/> and a real captured <c>.stdout.log</c> under
/// <see cref="ArtifactManager.ResolveOutputDirectory"/> — so this drives the same read path a settle
/// does. What it owns is the VERB's contract: the per-room and per-vendor arithmetic, the two filters,
/// and that a room with nothing to count is reported as such rather than as a room of zeros.
/// </para>
/// <para>
/// <b>The hand count is stated per room beside its fixture</b>, not derived in the assertion from the
/// same expressions that built the stream — a test that computes its expectation the way the code under
/// test does cannot discriminate.
/// </para>
/// </remarks>
public sealed class AuditLanesCommandTests : IDisposable
{
    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), $"baton-1921-{Guid.NewGuid():N}");

    private string RoomsRoot => Path.Combine(_sandbox, "rooms");

    public void Dispose() => DirectoryCleanup.DeleteRecursively(_sandbox);

    private const string Marker = GrantRefusal.Marker;

    /// <summary>
    /// claude room. Hand count: 3 <c>tool_use</c> blocks => 3 steps; 1 marked result => 1 refused;
    /// <c>Read a.cs</c> issued twice => 1 repeat; 1 blank result => 1 empty.
    /// </summary>
    private static readonly string[] ClaudeStream =
    [
        """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Read","input":{"file_path":"a.cs"}}]}}""",
        """{"type":"user","message":{"content":[{"type":"tool_result","content":"using System;"}]}}""",
        """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Read","input":{"file_path":"a.cs"}}]}}""",
        """{"type":"user","message":{"content":[{"type":"tool_result","content":"   "}]}}""",
        """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash","input":{"command":"gh api repos/x/y"}}]}}""",
        $$$"""{"type":"user","message":{"content":[{"type":"tool_result","content":"PreToolUse:Bash hook error: {{{Marker}}} AER: denied.","is_error":true}]}}""",
    ];

    /// <summary>
    /// agy room. Hand count: 2 terminal tool step_updates => 2 steps (the ACTIVE heartbeat is not one);
    /// 1 carrying the marker => 1 refused; two different parameter sets => 0 repeats; 0 empty.
    /// </summary>
    private static readonly string[] AgyStream =
    [
        """{"event":"step_update","step_update":{"state":"ACTIVE","step_type":"tool","tool_name":"run_command","tool_info":{"name":"run_command","parameters":{"CommandLine":"git status"}}}}""",
        """{"event":"step_update","step_update":{"state":"DONE","step_type":"tool","tool_name":"run_command","tool_info":{"name":"run_command","parameters":{"CommandLine":"git status"},"output":"## main"}}}""",
        """{"event":"step_update","step_update":{"state":"ERROR","step_type":"tool","tool_name":"write_to_file","tool_info":{"name":"write_to_file","parameters":{"TargetFile":"x.md"},"error":{"type":"TOOL_ERROR","message":"tool call denied by pre-tool hook: """
            + Marker
            + """ AER: denied."}}}}""",
        """{"event":"result","result":{"usage":{"input_tokens":10,"output_tokens":4},"num_turns":2}}""",
    ];

    /// <summary>
    /// codex room. Hand count: 3 <c>item.started</c> => 3 steps; 1 completed carrying the marker
    /// => 1 refused; the same tool+digest twice => 1 repeat; 1 blank aggregated_output => 1 empty.
    /// </summary>
    private static readonly string[] CodexStream =
    [
        """{"type":"item.started","item":{"type":"mcp_tool_call","tool":"baton_read_text","argumentsDigest":"aaaaaaaaaaaaaaaa"}}""",
        """{"type":"item.completed","item":{"type":"mcp_tool_call","tool":"baton_read_text","status":"completed","aggregated_output":"using System;"}}""",
        """{"type":"item.started","item":{"type":"mcp_tool_call","tool":"baton_read_text","argumentsDigest":"aaaaaaaaaaaaaaaa"}}""",
        """{"type":"item.completed","item":{"type":"mcp_tool_call","tool":"baton_read_text","status":"completed","aggregated_output":" "}}""",
        """{"type":"item.started","item":{"type":"mcp_tool_call","tool":"baton_run_command","argumentsDigest":"bbbbbbbbbbbbbbbb"}}""",
        $$$"""{"type":"item.completed","item":{"type":"mcp_tool_call","tool":"baton_run_command","status":"failed","aggregated_output":"{{{Marker}}} AER: denied."}}""",
        """{"type":"turn.completed","usage":{"input_tokens":10,"output_tokens":4}}""",
    ];

    [Fact]
    public async Task Three_rooms_one_per_vendor_reproduce_their_hand_counted_numbers()
    {
        await WriteRoomAsync("room-claude", "claude", ClaudeStream);
        await WriteRoomAsync("room-agy", "agy", AgyStream);
        await WriteRoomAsync("room-codex", "codex", CodexStream);

        var report = await BuildAsync(new AuditLanesOptions(RoomsRoot: RoomsRoot));

        Assert.Equal(3, report.RoomsWalked);
        Assert.Equal(0, report.RoomsWithoutCounts);

        var claude = Assert.Single(report.Rooms, room => room.Room == "room-claude");
        Assert.Equal(["claude"], claude.Vendors);
        Assert.Equal((3, 1, 1, 1), (claude.ToolSteps, claude.Refused, claude.Repeated, claude.EmptyResults));

        var agy = Assert.Single(report.Rooms, room => room.Room == "room-agy");
        Assert.Equal(["agy"], agy.Vendors);
        Assert.Equal((2, 1, 0, 0), (agy.ToolSteps, agy.Refused, agy.Repeated, agy.EmptyResults));

        var codex = Assert.Single(report.Rooms, room => room.Room == "room-codex");
        Assert.Equal(["codex"], codex.Vendors);
        Assert.Equal((3, 1, 1, 1), (codex.ToolSteps, codex.Refused, codex.Repeated, codex.EmptyResults));

        Assert.Equal(3, report.ByVendor.Count);
        Assert.Equal(8, report.ByVendor.Sum(vendor => vendor.ToolSteps));
        Assert.Equal(3, report.ByVendor.Sum(vendor => vendor.Refused));
    }

    [Fact]
    public async Task The_vendor_filter_selects_one_room_and_omits_the_others_rather_than_zeroing_them()
    {
        await WriteRoomAsync("room-claude", "claude", ClaudeStream);
        await WriteRoomAsync("room-agy", "agy", AgyStream);

        var report = await BuildAsync(new AuditLanesOptions(Vendor: "AGY", RoomsRoot: RoomsRoot));

        // Both rooms are walked -- the filter is on executions, not on which rooms are opened -- and the
        // one whose executions all filtered out is ABSENT, not present at zero.
        Assert.Equal(2, report.RoomsWalked);
        Assert.Equal("room-agy", Assert.Single(report.Rooms).Room);
        Assert.Equal(1, report.RoomsWithoutCounts);
    }

    [Fact]
    public async Task The_since_window_excludes_a_room_whose_journal_predates_it()
    {
        await WriteRoomAsync("room-old", "claude", ClaudeStream);
        await WriteRoomAsync("room-new", "claude", ClaudeStream);
        File.SetLastWriteTimeUtc(
            Path.Combine(RoomsRoot, "room-old", BatonPaths.FlowLogFileName),
            DateTime.UtcNow - TimeSpan.FromDays(9));

        var report = await BuildAsync(new AuditLanesOptions(Since: TimeSpan.FromDays(7), RoomsRoot: RoomsRoot));

        // The excluded room is not walked at all, which is the difference from the vendor filter above:
        // this window is about which rooms to open, so an excluded one is not counted anywhere.
        Assert.Equal(1, report.RoomsWalked);
        Assert.Equal("room-new", Assert.Single(report.Rooms).Room);
    }

    [Fact]
    public async Task A_room_whose_stream_carries_no_tool_activity_is_reported_as_uncounted_not_as_zeros()
    {
        // The polarity control for the whole report: a lane that only ever wrote prose must not appear
        // as a lane that ran zero tools, because that is the reading a zeroed row would support.
        await WriteRoomAsync("room-prose", "claude",
        [
            """{"type":"assistant","message":{"content":[{"type":"text","text":"thinking"}]}}""",
            """{"type":"result","subtype":"success","num_turns":1,"usage":{"input_tokens":9,"output_tokens":4}}""",
        ]);

        var report = await BuildAsync(new AuditLanesOptions(RoomsRoot: RoomsRoot));

        Assert.Equal(1, report.RoomsWalked);
        Assert.Empty(report.Rooms);
        Assert.Equal(1, report.RoomsWithoutCounts);
    }

    [Fact]
    public async Task The_text_view_names_every_count_and_discloses_the_uncounted_rooms()
    {
        await WriteRoomAsync("room-claude", "claude", ClaudeStream);

        using var output = new StringWriter();
        var exitCode = await AuditLanesCommand.ExecuteAsync(
            new AuditLanesOptions(RoomsRoot: RoomsRoot), output,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("room-claude", text);
        Assert.Contains("steps 3", text);
        Assert.Contains("refused 1", text);
        Assert.Contains("repeated 1", text);
        Assert.Contains("empty 1", text);
    }

    [Fact]
    public async Task An_absent_rooms_root_reports_nothing_and_exits_zero()
    {
        // The fail-open posture AuditLanesCommand's remarks state, asserted rather than asserted about.
        using var output = new StringWriter();
        var exitCode = await AuditLanesCommand.ExecuteAsync(
            new AuditLanesOptions(RoomsRoot: Path.Combine(_sandbox, "nothing-here")), output,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("no room carried tool activity", output.ToString());
    }

    private Task<AuditLanesReport> BuildAsync(AuditLanesOptions options) =>
        AuditLanesCommand.BuildReportAsync(options, DateTime.UtcNow, TestContext.Current.CancellationToken);

    private async Task WriteRoomAsync(string roomName, string adapter, IReadOnlyList<string> streamLines)
    {
        var roomDirectoryPath = Path.Combine(RoomsRoot, roomName);
        Directory.CreateDirectory(roomDirectoryPath);

        var id = new ExecutionId(Guid.NewGuid().ToString("N"));
        await using (var writer = new FlowEventLogWriter(Path.Combine(roomDirectoryPath, BatonPaths.FlowLogFileName)))
        {
            await writer.AppendAsync(
                new FlowEvent.ExecutionRequestAccepted(new ExecutionRequest(
                    id,
                    new WorkflowId("wf-audit"),
                    new StepId("implement"),
                    "implement",
                    Inputs: [],
                    Outputs: [],
                    Timeout: TimeSpan.FromSeconds(30),
                    Environment: [],
                    UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>(),
                    Adapter: adapter,
                    Model: "m")),
                TestContext.Current.CancellationToken);
            await writer.AppendAsync(new CoreEvent.ExecutionStarted(id, Pid: 1), TestContext.Current.CancellationToken);
            await writer.AppendAsync(
                new CoreEvent.ExecutionExited(id, 0, CoreExitReason.Natural), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new FlowEvent.ExecutionSucceeded(id), TestContext.Current.CancellationToken);
        }

        var outputDirectory = ArtifactManager.ResolveOutputDirectory(
            Path.Combine(roomDirectoryPath, ArtifactManager.ArtifactsDirectoryName), id);
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, ExecutionStreamLogger.StdoutLogFileName),
            string.Join("\n", streamLines) + "\n",
            TestContext.Current.CancellationToken);
    }
}
