using Baton.Domain;
using Baton.Status;
using Xunit;

namespace Baton.Tests.Status;

/// <summary>
/// #1921's settle-time reader: <see cref="ToolStepTally"/> over the three vendors' real stream shapes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every fixture line below is a shape measured on a room on disk</b>, not one invented from a
/// vendor's documentation — claude's refusal envelope from <c>dispatch-implement-00c359a5</c>, agy's
/// from <c>dispatch-implement-05550343</c>, codex's tool items from
/// <c>codex-1870-patch-sol-high-20260904-01</c>. That matters for the `right-instrument` gate: a reader
/// tested only against fixtures its own author shaped answers "does this do what I designed", and this
/// counter's whole job is to match what the vendors actually emit.
/// </para>
/// <para>
/// <b>The three streams the issue asks for — refused, clean, mixed — are per vendor</b>, plus the
/// absent/zero polarity that <see cref="ToolStepTally.Snapshot"/>'s three states turn on: a stream with
/// tool activity and no refusals must report <c>0</c>, and a stream with no tool activity at all must
/// report ABSENT. Those two are one condition apart, so both directions are asserted.
/// </para>
/// </remarks>
public sealed class ToolStepTallyTests
{
    private const string RefusalReason =
        "PreToolUse:Bash hook error: [dotnet Baton.Cli.dll hook-check]: "
        + GrantRefusal.Marker
        + " AER: the 'Bash' command is denied under this session's shell grant.";

    private static string ClaudeToolUse(string name, string input) =>
        $$$"""{"type":"assistant","message":{"content":[{"type":"tool_use","name":"{{{name}}}","input":{{{input}}}}]}}""";

    private static string ClaudeToolResult(string text) =>
        $$$"""{"type":"user","message":{"role":"user","content":[{"type":"tool_result","content":{{{System.Text.Json.JsonSerializer.Serialize(text)}}}}]}}""";

    private static ToolStepCounts? Tally(IWorkerUsageParser parser, params string[] lines)
    {
        var tally = new ToolStepTally(parser);
        foreach (var line in lines)
        {
            tally.OnStdoutLine(line);
        }

        return tally.Snapshot();
    }

    [Fact]
    public void Claude_refused_stream_counts_every_marked_result()
    {
        var counts = Tally(
            new ClaudeUsageParser(),
            ClaudeToolUse("Bash", """{"command":"gh api repos/x/y"}"""),
            ClaudeToolResult(RefusalReason),
            ClaudeToolUse("Bash", """{"command":"gh issue view 1"}"""),
            ClaudeToolResult(RefusalReason));

        Assert.Equal(new ToolStepCounts(ToolSteps: 2, Refused: 2, Repeated: 0, EmptyResults: 0), counts);
    }

    [Fact]
    public void Claude_clean_stream_reports_zero_refusals_rather_than_absent()
    {
        // The polarity arm for Snapshot's second and third states: this stream HAS tool activity, so the
        // zero is a measurement and must be written. Absent here would collapse the two states that
        // method's doc keeps apart.
        var counts = Tally(
            new ClaudeUsageParser(),
            ClaudeToolUse("Read", """{"file_path":"a.cs"}"""),
            ClaudeToolResult("using System;"),
            ClaudeToolUse("Read", """{"file_path":"b.cs"}"""),
            ClaudeToolResult("namespace X;"));

        Assert.Equal(new ToolStepCounts(2, 0, 0, 0), counts);
    }

    [Fact]
    public void Claude_mixed_stream_counts_refusals_repeats_and_empty_results_independently()
    {
        // Hand count. Four tool_use blocks => 4 steps. One marked result => 1 refused. `Read a.cs` is
        // issued three times => 2 occurrences beyond the first; `Bash` once => 0. One blank result => 1
        // empty.
        var counts = Tally(
            new ClaudeUsageParser(),
            ClaudeToolUse("Read", """{"file_path":"a.cs"}"""),
            ClaudeToolResult("using System;"),
            ClaudeToolUse("Read", """{"file_path":"a.cs"}"""),
            ClaudeToolResult("using System;"),
            ClaudeToolUse("Read", """{"file_path":"a.cs"}"""),
            ClaudeToolResult("   "),
            ClaudeToolUse("Bash", """{"command":"gh api repos/x/y"}"""),
            ClaudeToolResult(RefusalReason));

        Assert.Equal(new ToolStepCounts(ToolSteps: 4, Refused: 1, Repeated: 2, EmptyResults: 1), counts);
    }

    [Fact]
    public void Claude_multi_tool_turn_counts_each_block_and_keys_each_separately()
    {
        const string line =
            """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Read","input":{"file_path":"a.cs"}},{"type":"tool_use","name":"Read","input":{"file_path":"a.cs"}}]}}""";

        var counts = Tally(new ClaudeUsageParser(), line);

        // Two calls on one line, identical: 2 steps and 1 repeat. A key reader that returned only the
        // first block (the shape TryParseToolName has) would report 2 steps and 0 repeats.
        Assert.Equal(new ToolStepCounts(2, 0, 1, 0), counts);
    }

    [Fact]
    public void A_stream_with_no_tool_activity_is_absent_rather_than_zero()
    {
        var counts = Tally(
            new ClaudeUsageParser(),
            """{"type":"assistant","message":{"content":[{"type":"text","text":"thinking about it"}]}}""",
            """{"type":"result","subtype":"success","num_turns":1,"usage":{"input_tokens":9,"output_tokens":4}}""");

        Assert.Null(counts);
    }

    [Fact]
    public void An_unparseable_envelope_is_absent_rather_than_zero()
    {
        // The second half of the same polarity: an envelope no parser understands must read as absent
        // rather than as a measured zero.
        Assert.Null(Tally(new ClaudeUsageParser(), "not json at all", "{\"unrelated\":true}"));
    }

    [Fact]
    public void Agy_refused_and_clean_terminal_steps_are_counted_off_the_same_anchor()
    {
        var refused = AgyToolStep("ERROR", "write_to_file", """{"TargetFile":"x.md"}""",
            error: "tool call denied by pre-tool hook: " + RefusalReason);
        var clean = AgyToolStep("DONE", "run_command", """{"CommandLine":"git status"}""", output: "## main");

        Assert.Equal(new ToolStepCounts(1, 1, 0, 0), Tally(new AgyUsageParser(), refused));
        Assert.Equal(new ToolStepCounts(1, 0, 0, 0), Tally(new AgyUsageParser(), clean));
    }

    [Fact]
    public void Agy_mixed_stream_reproduces_a_hand_count()
    {
        // Hand count. Four terminal tool steps (the ACTIVE heartbeat is not one, matching CountToolSteps'
        // own unit) => 4 steps. One refused. `git status` twice => 1 repeat. One blank output => 1 empty.
        var counts = Tally(
            new AgyUsageParser(),
            AgyToolStep("ACTIVE", "run_command", """{"CommandLine":"git status"}"""),
            AgyToolStep("DONE", "run_command", """{"CommandLine":"git status"}""", output: "## main"),
            AgyToolStep("DONE", "run_command", """{"CommandLine":"git status"}""", output: "## main"),
            AgyToolStep("DONE", "run_command", """{"CommandLine":"git log"}""", output: "  "),
            AgyToolStep("ERROR", "write_to_file", """{"TargetFile":"x.md"}""",
                error: "tool call denied by pre-tool hook: " + RefusalReason));

        Assert.Equal(new ToolStepCounts(ToolSteps: 4, Refused: 1, Repeated: 1, EmptyResults: 1), counts);
    }

    [Fact]
    public void Codex_counts_calls_on_started_and_refusals_on_completed()
    {
        // Hand count. Three item.started => 3 steps. One completed carrying the marker => 1 refused.
        // baton_read_text with the same argument digest twice => 1 repeat. One blank aggregated_output
        // => 1 empty.
        var counts = Tally(
            new CodexUsageParser(),
            CodexStarted("baton_read_text", "aaaaaaaaaaaaaaaa"),
            CodexCompleted("baton_read_text", "completed", "using System;"),
            CodexStarted("baton_read_text", "aaaaaaaaaaaaaaaa"),
            CodexCompleted("baton_read_text", "completed", "   "),
            CodexStarted("baton_run_command", "bbbbbbbbbbbbbbbb"),
            CodexCompleted("baton_run_command", "failed", RefusalReason));

        Assert.Equal(new ToolStepCounts(ToolSteps: 3, Refused: 1, Repeated: 1, EmptyResults: 1), counts);
    }

    [Fact]
    public void Codex_reports_no_repeats_when_the_stream_carries_no_argument_digest()
    {
        // CodexUsageParser.ToolInvocationKeys' documented gap, asserted in the direction that matters:
        // a stream with no digest must report 0 repeats rather than a fabricated one.
        var counts = Tally(
            new CodexUsageParser(),
            """{"type":"item.started","item":{"type":"mcp_tool_call","tool":"baton_read_text"}}""",
            CodexCompleted("baton_read_text", "completed", "a"),
            """{"type":"item.started","item":{"type":"mcp_tool_call","tool":"baton_read_text"}}""",
            CodexCompleted("baton_read_text", "completed", "b"));

        Assert.Equal(new ToolStepCounts(ToolSteps: 2, Refused: 0, Repeated: 0, EmptyResults: 0), counts);
    }

    private static string AgyToolStep(
        string state, string toolName, string parameters, string? output = null, string? error = null)
    {
        var payload = error is not null
            ? $$$"""{"name":"{{{toolName}}}","parameters":{{{parameters}}},"error":{"type":"TOOL_ERROR","message":{{{System.Text.Json.JsonSerializer.Serialize(error)}}}}}"""
            : $$$"""{"name":"{{{toolName}}}","parameters":{{{parameters}}},"output":{{{System.Text.Json.JsonSerializer.Serialize(output ?? string.Empty)}}}}""";

        return $$$"""{"event":"step_update","step_update":{"state":"{{{state}}}","step_type":"tool","tool_name":"{{{toolName}}}","tool_info":{{{payload}}}}}""";
    }

    private static string CodexStarted(string tool, string digest) =>
        $$$"""{"type":"item.started","item":{"type":"mcp_tool_call","tool":"{{{tool}}}","argumentsDigest":"{{{digest}}}"}}""";

    private static string CodexCompleted(string tool, string status, string aggregatedOutput) =>
        $$$"""{"type":"item.completed","item":{"type":"mcp_tool_call","tool":"{{{tool}}}","status":"{{{status}}}","aggregated_output":{{{System.Text.Json.JsonSerializer.Serialize(aggregatedOutput)}}}}}""";
}
