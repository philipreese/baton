using Baton.Vendors;
using Xunit;

namespace Baton.Vendors.Tests;

/// <summary>
/// Coverage for <see cref="AgyWorkerAdapter.TryParseProgressEvent"/> (#1088), whose doc-comment owns the
/// description of agy's envelope. Fixtures are captured verbatim from a live agy 1.1.11 run with
/// <c>-p … --output-format stream-json</c>, so these fail if a future agy changes the envelope. The last
/// test pins that one adapter's parser does not accept the other vendor's lines.
/// </summary>
public sealed class AgyStreamJsonProgressParsingTests
{
    private readonly AgyWorkerAdapter _adapter = new();

    // Built by concatenation, not an interpolated raw string, because the JSON's own `}}` collides with
    // `$$"""` interpolation delimiters.
    private static string StepUpdateLine(string state, string stepType) =>
        "{\"event\":\"step_update\",\"step_update\":{\"conversation_id\":\"5ec0d582\",\"step_index\":2,"
        + "\"state\":\"" + state + "\",\"step_type\":\"" + stepType + "\"}}";

    [Fact]
    public void Init_event_returns_session_started_status()
    {
        const string line = """
            {"event":"init","conversation_id":"5ec0d582-de3a-4fce-b626-578a3fcd9815","init":{"cwd":"C:\\tmp"}}
            """;

        var parsed = _adapter.TryParseProgressEvent(line, out var progressEvent);

        Assert.True(parsed);
        Assert.Equal("status", progressEvent!.Kind);
        Assert.Equal("Session started", progressEvent.Text);
        Assert.False(progressEvent.IsPartial);
    }

    [Theory]
    [InlineData("agent_response")]
    [InlineData("tool")]
    public void Step_update_DONE_meaningful_type_returns_status_named_by_step(string stepType)
    {
        var parsed = _adapter.TryParseProgressEvent(StepUpdateLine("DONE", stepType), out var progressEvent);

        Assert.True(parsed);
        Assert.Equal("status", progressEvent!.Kind);
        Assert.Equal(stepType, progressEvent.Text);
    }

    [Fact]
    public void Step_update_DONE_tool_step_uses_the_actual_tool_name()
    {
        const string line = """
            {"event":"step_update","step_update":{"conversation_id":"5ec0d582","step_index":2,"state":"DONE","step_type":"tool","tool_name":"view_file"}}
            """;

        var parsed = _adapter.TryParseProgressEvent(line, out var progressEvent);

        Assert.True(parsed);
        Assert.Equal("tool", progressEvent!.Kind);
        Assert.Equal("view_file", progressEvent.Text);
    }

    [Theory]
    [InlineData("user_input")]  // the user's own echoed input, not worker progress
    [InlineData("checkpoint")]  // internal bookkeeping
    [InlineData("unknown")]     // opaque
    public void Step_update_DONE_noise_type_is_recognized_but_ignored(string stepType)
    {
        // #1561: recognized (parsed=true) but deliberately carries no signal (Kind "ignore") --
        // distinct from an event this adapter has never seen at all, which still returns false so a
        // never-swallow consumer can echo it verbatim instead of guessing it's noise.
        var parsed = _adapter.TryParseProgressEvent(StepUpdateLine("DONE", stepType), out var progressEvent);

        Assert.True(parsed);
        Assert.Equal("ignore", progressEvent!.Kind);
    }

    [Fact]
    public void Step_update_ACTIVE_edge_is_recognized_but_ignored_only_DONE_renders_status()
    {
        // Measured: most agy steps report only a DONE edge; surfacing ACTIVE too would double a `tool`
        // step and miss every DONE-only one. The DONE edge is the one heartbeat per step. #1561:
        // recognized (parsed=true, Kind "ignore"), not unknown -- see the theory above.
        const string line = """
            {"event":"step_update","step_update":{"state":"ACTIVE","step_type":"tool"}}
            """;

        var parsed = _adapter.TryParseProgressEvent(line, out var progressEvent);

        Assert.True(parsed);
        Assert.Equal("ignore", progressEvent!.Kind);
    }

    [Theory]
    [InlineData("ERROR")]
    [InlineData("CANCELLED")]
    public void Result_event_non_success_status_returns_error_summary(string status)
    {
        // #1561: the failure-reason gap the second-reader review found -- a non-SUCCESS result
        // carries an empty `response`, so Result_event_returns_the_response_as_text's case never
        // matched it and the line silently vanished. Fixture shape verbatim from #1128's real
        // captured quota refusal (execution eca57a30, AgyWorkerAdapterTests).
        var line = """{"event":"result","result":{"conversation_id":"eca57a30","status":"""
            + $"\"{status}\""
            + ""","response":"","error":"Individual quota reached. Please upgrade your subscription to increase your limits. Resets in 1h39m10s."}}""";

        var parsed = _adapter.TryParseProgressEvent(line, out var progressEvent);

        Assert.True(parsed);
        Assert.Equal("result", progressEvent!.Kind);
        Assert.Equal("error — Individual quota reached. Please upgrade your subscription to increase your limits. Resets in 1h39m10s.", progressEvent.Text);
    }

    [Fact]
    public void Result_event_success_status_with_empty_response_is_recognized_but_ignored()
    {
        const string line = """{"event":"result","result":{"status":"SUCCESS","response":""}}""";

        var parsed = _adapter.TryParseProgressEvent(line, out var progressEvent);

        Assert.True(parsed);
        Assert.Equal("ignore", progressEvent!.Kind);
    }

    [Fact]
    public void Result_event_returns_the_response_as_text()
    {
        // Deliberately DIFFERENT from claude, whose `result` returns false: claude streams the answer via
        // `assistant` events during the turn, so its result is a redundant summary. agy streams NO
        // incremental text, so the terminal `result` is the only carrier of the answer on the progress
        // channel — it must surface.
        const string line = """
            {"event":"result","result":{"conversation_id":"5ec0d582","status":"SUCCESS","response":"Created note.txt containing HELLO-WORLD.","duration_seconds":3.6,"num_turns":1,"usage":{"input_tokens":14407,"output_tokens":1173,"thinking_tokens":992,"cache_read_tokens":40765,"total_tokens":15580}}}
            """;

        var parsed = _adapter.TryParseProgressEvent(line, out var progressEvent);

        Assert.True(parsed);
        Assert.Equal("text", progressEvent!.Kind);
        Assert.Contains("Created note.txt", progressEvent.Text);
        Assert.False(progressEvent.IsPartial);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"event\":\"init\",\"conv")]                                   // chunk-split line
    [InlineData("{\"event\":\"unknown_future_event\"}")]                          // forward-compatible
    [InlineData("{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"x\"}")] // claude's envelope: NOT agy's
    public void Unrecognized_or_foreign_line_returns_false_without_throwing(string line)
    {
        var parsed = _adapter.TryParseProgressEvent(line, out var progressEvent);

        Assert.False(parsed);
        Assert.Null(progressEvent);
    }
}
