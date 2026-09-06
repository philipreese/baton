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
    /// A settled claude execution that ran no tool at all. Hand count: 0 steps, and NOT four zeros —
    /// the all-four-or-none gate leaves every count null, which is the shape both uncounted-room
    /// buckets are reached through.
    /// </summary>
    private static readonly string[] ProseOnlyStream =
    [
        """{"type":"assistant","message":{"content":[{"type":"text","text":"thinking"}]}}""",
        """{"type":"result","subtype":"success","num_turns":1,"usage":{"input_tokens":9,"output_tokens":4}}""",
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

        // ...and it is the operator's filter that excluded it, NOT a stream this reader could not parse.
        // The claude room's stream parses fine, which is what makes counting it under
        // roomsWithoutCounts a false statement rather than an imprecise one (#1921 review MEDIUM).
        Assert.Equal(0, report.RoomsWithoutCounts);
        Assert.Equal(1, report.RoomsExcludedByVendor);
    }

    [Fact]
    public async Task A_vendor_no_walked_room_ran_says_so_rather_than_blaming_the_rooms_streams()
    {
        // The narrow-filter case, which empties the report entirely and so takes a different branch of
        // the text view from the mixed case above.
        await WriteRoomAsync("room-claude", "claude", ClaudeStream);
        await WriteRoomAsync("room-agy", "agy", AgyStream);

        using var output = new StringWriter();
        await AuditLanesCommand.ExecuteAsync(
            new AuditLanesOptions(Vendor: "codex", RoomsRoot: RoomsRoot), output,
            cancellationToken: TestContext.Current.CancellationToken);

        var text = output.ToString();
        Assert.Contains("2 walked room(s) ran no execution --vendor codex admitted", text);
        Assert.DoesNotContain("carried no tool activity this reader could parse", text);
    }

    [Fact]
    public async Task A_room_the_vendor_filter_only_partly_excluded_is_not_reported_as_excluded()
    {
        // The mixed room #1921's re-review named: a dispatch room running two vendors, where --vendor
        // removed one execution and ADMITTED the other, whose stream carried nothing this reader could
        // parse. The filter is not the explanation for this room -- an admitted execution was read and
        // yielded nothing -- so it must not land in a bucket whose sentence says no execution was
        // admitted. Routing it there told the operator to widen a filter that was never the cause.
        await WriteRoomAsync(
            "room-mixed", [("claude", ProseOnlyStream), ("codex", CodexStream)]);

        var report = await BuildAsync(new AuditLanesOptions(Vendor: "claude", RoomsRoot: RoomsRoot));

        Assert.Equal(1, report.RoomsWalked);
        Assert.Empty(report.Rooms);
        Assert.Equal(0, report.RoomsExcludedByVendor);
        Assert.Equal(1, report.RoomsWithoutCounts);

        // ...and the text view says the true cause rather than the filter.
        using var output = new StringWriter();
        await AuditLanesCommand.ExecuteAsync(
            new AuditLanesOptions(Vendor: "claude", RoomsRoot: RoomsRoot), output,
            cancellationToken: TestContext.Current.CancellationToken);

        var text = output.ToString();
        Assert.Contains("carried no tool activity this reader could parse", text);
        Assert.DoesNotContain("ran no execution --vendor claude admitted", text);
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
        await WriteRoomAsync("room-prose", "claude", ProseOnlyStream);

        var report = await BuildAsync(new AuditLanesOptions(RoomsRoot: RoomsRoot));

        Assert.Equal(1, report.RoomsWalked);
        Assert.Empty(report.Rooms);
        Assert.Equal(1, report.RoomsWithoutCounts);

        // The polarity partner of the vendor arm above: no filter was given, so nothing may land in the
        // excluded bucket. Without this, a fix that moved every uncounted room into that bucket passes.
        Assert.Equal(0, report.RoomsExcludedByVendor);
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

    private Task WriteRoomAsync(string roomName, string adapter, IReadOnlyList<string> streamLines) =>
        WriteRoomAsync(roomName, [(adapter, streamLines)]);

    /// <summary>
    /// One room, one settled execution per entry — a real <c>flow.jsonl</c> and a real captured
    /// <c>.stdout.log</c> each. More than one entry is how the mixed-vendor room is built: the two
    /// executions share a journal, which is what makes <c>--vendor</c> a per-execution filter inside a
    /// single walked room rather than a filter on rooms.
    /// </summary>
    private async Task WriteRoomAsync(
        string roomName, IReadOnlyList<(string Adapter, IReadOnlyList<string> Stream)> executions)
    {
        var roomDirectoryPath = Path.Combine(RoomsRoot, roomName);
        Directory.CreateDirectory(roomDirectoryPath);

        var ids = executions.Select(_ => new ExecutionId(Guid.NewGuid().ToString("N"))).ToArray();
        await using (var writer = new FlowEventLogWriter(Path.Combine(roomDirectoryPath, BatonPaths.FlowLogFileName)))
        {
            for (var index = 0; index < executions.Count; index++)
            {
                var id = ids[index];
                await writer.AppendAsync(
                    new FlowEvent.ExecutionRequestAccepted(new ExecutionRequest(
                        id,
                        new WorkflowId("wf-audit"),
                        new StepId($"implement-{index}"),
                        "implement",
                        Inputs: [],
                        Outputs: [],
                        Timeout: TimeSpan.FromSeconds(30),
                        Environment: [],
                        UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>(),
                        Adapter: executions[index].Adapter,
                        Model: "m")),
                    TestContext.Current.CancellationToken);
                await writer.AppendAsync(new CoreEvent.ExecutionStarted(id, Pid: 1), TestContext.Current.CancellationToken);
                await writer.AppendAsync(
                    new CoreEvent.ExecutionExited(id, 0, CoreExitReason.Natural), TestContext.Current.CancellationToken);
                await writer.AppendAsync(new FlowEvent.ExecutionSucceeded(id), TestContext.Current.CancellationToken);
            }
        }

        for (var index = 0; index < executions.Count; index++)
        {
            var outputDirectory = ArtifactManager.ResolveOutputDirectory(
                Path.Combine(roomDirectoryPath, ArtifactManager.ArtifactsDirectoryName), ids[index]);
            Directory.CreateDirectory(outputDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(outputDirectory, ExecutionStreamLogger.StdoutLogFileName),
                string.Join("\n", executions[index].Stream) + "\n",
                TestContext.Current.CancellationToken);
        }
    }
}
