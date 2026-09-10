using Baton.Status;
using Xunit;

namespace Baton.Tests.Status;

/// <summary>
/// Coverage for <see cref="ClaudeUsageParser"/>/<see cref="AgyUsageParser"/> (issue #1360, extended by
/// #1569) — the sole implementation for each vendor's usage parsing (#1599/#1612). Passed explicitly
/// via <see cref="StandardWorkerUsageParsers.Default"/> by <c>FleetStatusTool</c> for active rooms,
/// resolved as the fallback for every other caller (<c>Program.cs</c>'s <c>terminal.json</c> write
/// included), and delegated to by <c>ClaudeWorkerAdapter</c>/<c>AgyWorkerAdapter</c> for
/// <c>baton status --json</c>. The #1578 divergence between the formerly duplicated parsers was
/// reconciled so an all-null result returns <see langword="false"/> across all paths.
/// </summary>
public sealed class StandardWorkerUsageParsersTests
{
    [Fact]
    public void Claude_parser_reads_cache_and_thinking_fields_when_present()
    {
        var parser = new ClaudeUsageParser();
        const string line = """
            {"duration_api_ms":2475,"stop_reason":"end_turn","session_id":"f2790c72-2c95-4b91-9786-ce6d5ba3aea8","total_cost_usd":0.039611,"usage":{"input_tokens":2,"cache_creation_input_tokens":0,"cache_read_input_tokens":38741,"output_tokens":17,"output_tokens_details":{"thinking_tokens":6}},"is_error":false,"num_turns":1,"subtype":"success","result":"1\n\n2\n\n3\n\n4\n\n5","type":"result"}
            """;

        var parsed = parser.TryParseFinalUsage(line, out var usage);

        Assert.True(parsed);
        Assert.Equal(2, usage!.TokensIn);
        Assert.Equal(17, usage.TokensOut);
        Assert.Equal(38741, usage.CacheReadTokens);
        Assert.Equal(0, usage.CacheCreationTokens);
        Assert.Equal(6, usage.ThinkingTokens);
    }

    [Fact]
    public void Claude_parser_leaves_cache_and_thinking_fields_null_when_absent()
    {
        var parser = new ClaudeUsageParser();
        const string line = """
            {"type":"result","subtype":"success","is_error":false,"num_turns":2,"result":"done","usage":{"input_tokens":9,"output_tokens":4}}
            """;

        var parsed = parser.TryParseFinalUsage(line, out var usage);

        Assert.True(parsed);
        Assert.Equal(9, usage!.TokensIn);
        Assert.Equal(4, usage.TokensOut);
        Assert.Null(usage.CacheReadTokens);
        Assert.Null(usage.CacheCreationTokens);
        Assert.Null(usage.ThinkingTokens);
    }

    [Fact]
    public void Agy_parser_reads_cache_and_thinking_fields_when_present()
    {
        var parser = new AgyUsageParser();
        const string line = """
            {"event":"result","result":{"conversation_id":"5ec0d582","status":"SUCCESS","response":"Created note.txt containing HELLO-WORLD.","duration_seconds":3.6,"num_turns":1,"usage":{"input_tokens":14407,"output_tokens":1173,"thinking_tokens":992,"cache_read_tokens":40765,"total_tokens":15580}}}
            """;

        var parsed = parser.TryParseFinalUsage(line, out var usage);

        Assert.True(parsed);
        Assert.Equal(14407, usage!.TokensIn);
        Assert.Equal(1173, usage.TokensOut);
        Assert.Equal(992, usage.ThinkingTokens);
        Assert.Equal(40765, usage.CacheReadTokens);
        Assert.Null(usage.CacheCreationTokens);
    }

    [Fact]
    public void Agy_parser_leaves_cache_and_thinking_fields_null_when_absent()
    {
        var parser = new AgyUsageParser();
        const string line = """
            {"event":"result","result":{"status":"SUCCESS","response":"done","num_turns":1,"usage":{"input_tokens":9,"output_tokens":4}}}
            """;

        var parsed = parser.TryParseFinalUsage(line, out var usage);

        Assert.True(parsed);
        Assert.Equal(9, usage!.TokensIn);
        Assert.Equal(4, usage.TokensOut);
        Assert.Null(usage.CacheReadTokens);
        Assert.Null(usage.ThinkingTokens);
        Assert.Null(usage.CacheCreationTokens);
    }

    // #1623: incremental (mid-stream) usage/tool-name reads, evaluated as usage arrives for the
    // token-budget watch. Claude's fixture matches docs/vendor-capabilities.md's 2026-09-01 finding
    // (message.usage on every "type":"assistant" line); agy's fixture is captured verbatim from a real
    // lane's .stdout.log (2026-09-02, ~/.aer/rooms/wb1396-advise-agy).

    [Fact]
    public void Claude_parser_reads_incremental_usage_off_a_midstream_assistant_line()
    {
        var parser = new ClaudeUsageParser();
        const string line = """
            {"type":"assistant","message":{"id":"msg_1","content":[{"type":"text","text":"ok"}],"usage":{"input_tokens":24619,"output_tokens":3,"cache_creation_input_tokens":24619,"cache_read_input_tokens":0}}}
            """;

        var parsed = parser.TryParseIncrementalUsage(line, out var usage);

        Assert.True(parsed);
        // #1706: the two cache columns are the only real figures on this line (docs/vendor-capabilities.md
        // has the measurement). Note this fixture's `input_tokens` happens to equal its
        // cache_creation_input_tokens, which reads as a plausible real input count -- exactly the trap
        // the measurement caught, kept here on purpose.
        Assert.Null(usage!.TokensIn);
        Assert.Null(usage.TokensOut);
        Assert.True(usage.BilledIsFloor);
        Assert.Equal(24619, usage.CacheCreationTokens);
        Assert.Equal(0, usage.CacheReadTokens);
        Assert.Null(usage.Turns);
        Assert.Null(usage.ThinkingTokens);
    }

    [Fact]
    public void Claude_parser_TryParseIncrementalUsage_ReturnsFalse_for_the_terminal_result_line()
    {
        var parser = new ClaudeUsageParser();
        const string line = """{"type":"result","usage":{"input_tokens":1,"output_tokens":1}}""";

        Assert.False(parser.TryParseIncrementalUsage(line, out var usage));
        Assert.Null(usage);
    }

    [Fact]
    public void Claude_parser_reads_the_first_tool_use_name_off_an_assistant_line()
    {
        var parser = new ClaudeUsageParser();
        const string line = """
            {"type":"assistant","message":{"content":[{"type":"text","text":"running"},{"type":"tool_use","id":"t1","name":"Bash","input":{}}]}}
            """;

        Assert.Equal("Bash", parser.TryParseToolName(line));
    }

    [Fact]
    public void Claude_parser_TryParseToolName_ReturnsNull_when_no_tool_use_block_is_present()
    {
        var parser = new ClaudeUsageParser();
        const string line = """{"type":"assistant","message":{"content":[{"type":"text","text":"ok"}]}}""";

        Assert.Null(parser.TryParseToolName(line));
    }

    [Fact]
    public void Agy_parser_reads_incremental_usage_off_a_DONE_step_update()
    {
        // Captured verbatim (2026-09-02) from a real agy lane's .stdout.log.
        var parser = new AgyUsageParser();
        const string line = """
            {"event":"step_update","step_update":{"conversation_id":"8ccbe59b-b86f-4efc-9169-5d75dcda3fb4","step_index":1,"state":"DONE","step_type":"agent_response","duration_seconds":4.1532041,"usage":{"input_tokens":14347,"output_tokens":262,"thinking_tokens":175,"cache_read_tokens":0,"total_tokens":14609}}}
            """;

        var parsed = parser.TryParseIncrementalUsage(line, out var usage);

        Assert.True(parsed);
        Assert.Equal(14347, usage!.TokensIn);
        Assert.Equal(262, usage.TokensOut);
        Assert.Equal(175, usage.ThinkingTokens);
        Assert.Equal(0, usage.CacheReadTokens);
    }

    [Fact]
    public void Agy_parser_TryParseIncrementalUsage_ReturnsFalse_for_a_non_DONE_step_update()
    {
        var parser = new AgyUsageParser();
        const string line = """
            {"event":"step_update","step_update":{"step_index":1,"state":"ACTIVE","step_type":"agent_response"}}
            """;

        Assert.False(parser.TryParseIncrementalUsage(line, out var usage));
        Assert.Null(usage);
    }

    [Fact]
    public void Agy_parser_reads_the_tool_name_off_a_tool_step_update()
    {
        // Captured verbatim (2026-09-02) from the same real agy lane.
        var parser = new AgyUsageParser();
        const string line = """
            {"event":"step_update","step_update":{"conversation_id":"8ccbe59b-b86f-4efc-9169-5d75dcda3fb4","step_index":2,"state":"ACTIVE","step_type":"tool","tool_name":"view_file","tool_info":{"name":"view_file","parameters":{"AbsolutePath":"x"}}}}
            """;

        Assert.Equal("view_file", parser.TryParseToolName(line));
    }

    [Fact]
    public void Agy_parser_TryParseToolName_ReturnsNull_for_a_non_tool_step_update()
    {
        var parser = new AgyUsageParser();
        const string line = """{"event":"step_update","step_update":{"step_index":1,"state":"DONE","step_type":"agent_response"}}""";

        Assert.Null(parser.TryParseToolName(line));
    }

    [Fact]
    public void Agy_billed_tokens_reproduce_the_vendor_total_tokens_exactly_on_a_real_evidence_line()
    {
        // #1682: this is the primary source proof spec/baton.md §3 cites -- a real captured line
        // (step_index 1 of dispatch-implement-38c24d11's .stdout.log): 14205 + 443 == 14648, and 349
        // does not belong in that sum.
        var parser = new AgyUsageParser();
        const string line = """
            {"event":"step_update","step_update":{"conversation_id":"a3815ffd-2aad-48f6-b01d-67534266bbdc","step_index":1,"state":"DONE","step_type":"agent_response","duration_seconds":4.7995627,"usage":{"input_tokens":14205,"output_tokens":443,"thinking_tokens":349,"cache_read_tokens":0,"total_tokens":14648}}}
            """;

        Assert.True(parser.TryParseIncrementalUsage(line, out var usage));
        var billed = (usage!.TokensIn ?? 0) + (usage.TokensOut ?? 0);
        Assert.Equal(14648, billed);
        Assert.NotEqual(14648, billed + (usage.ThinkingTokens ?? 0));
    }

    [Fact]
    public void Agy_CountToolSteps_counts_a_tool_step_update_ONLY_at_its_terminal_state()
    {
        // #1686 review F2, fixed unit: both ACTIVE and DONE step_updates for the SAME tool call carry
        // tool_name (captured verbatim 2026-09-02 from a real agy lane's .stdout.log), but only the
        // terminal (DONE/ERROR) line is a REAL tool call in the fixed unit -- counting ACTIVE too
        // double-counted the #1682 evidence table's own "138 tool steps" for this room (69 distinct
        // calls x 2 lifecycle lines each). See AgyUsageParser.CountToolSteps's own doc.
        var parser = new AgyUsageParser();
        const string activeLine = """
            {"event":"step_update","step_update":{"step_index":2,"state":"ACTIVE","step_type":"tool","tool_name":"view_file"}}
            """;
        const string doneLine = """
            {"event":"step_update","step_update":{"step_index":2,"state":"DONE","step_type":"tool","tool_name":"view_file"}}
            """;
        const string errorLine = """
            {"event":"step_update","step_update":{"step_index":3,"state":"ERROR","step_type":"tool","tool_name":"run_command"}}
            """;
        const string agentResponseLine = """
            {"event":"step_update","step_update":{"step_index":1,"state":"DONE","step_type":"agent_response"}}
            """;

        Assert.Equal(0, parser.CountToolSteps(activeLine));
        Assert.Equal(1, parser.CountToolSteps(doneLine));
        Assert.Equal(1, parser.CountToolSteps(errorLine));
        Assert.Equal(0, parser.CountToolSteps(agentResponseLine));
    }

    [Fact]
    public void Agy_CountWriteToolSteps_counts_only_terminal_write_family_calls()
    {
        var parser = new AgyUsageParser();
        const string activeWrite = """
            {"event":"step_update","step_update":{"state":"ACTIVE","step_type":"tool","tool_name":"write_to_file","tool_info":{"name":"write_to_file"}}}
            """;
        const string doneWrite = """
            {"event":"step_update","step_update":{"state":"DONE","step_type":"tool","tool_name":"write_to_file","tool_info":{"name":"write_to_file"}}}
            """;
        const string doneRead = """
            {"event":"step_update","step_update":{"state":"DONE","step_type":"tool","tool_name":"view_file","tool_info":{"name":"view_file"}}}
            """;

        Assert.Equal(0, parser.CountWriteToolSteps(activeWrite));
        Assert.Equal(1, parser.CountWriteToolSteps(doneWrite));
        Assert.Equal(0, parser.CountWriteToolSteps(doneRead));
    }

    [Fact]
    public void Agy_parser_TryParseIncrementalUsage_ReturnsFalse_for_a_DONE_tool_step_even_if_it_carried_usage()
    {
        // #1686 review F4 (AgyUsageParser.TryParseIncrementalUsage's own doc has the full predicate
        // case): a DONE/tool step is never a usage source, even in the unobserved case it carried one.
        var parser = new AgyUsageParser();
        const string line = """
            {"event":"step_update","step_update":{"state":"DONE","step_type":"tool","tool_name":"x","usage":{"input_tokens":1,"output_tokens":1}}}
            """;

        Assert.False(parser.TryParseIncrementalUsage(line, out var usage));
        Assert.Null(usage);
    }

    [Fact]
    public void Claude_CountToolSteps_counts_every_tool_use_block_in_a_multi_tool_turn()
    {
        var parser = new ClaudeUsageParser();
        const string multiToolLine = """
            {"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash"},{"type":"text","text":"ok"},{"type":"tool_use","name":"Read"}]}}
            """;
        const string noToolLine = """{"type":"assistant","message":{"content":[{"type":"text","text":"ok"}]}}""";

        Assert.Equal(2, parser.CountToolSteps(multiToolLine));
        Assert.Equal(0, parser.CountToolSteps(noToolLine));

        // Distinct from TryParseToolName, which deliberately reports only the FIRST block's name.
        Assert.Equal("Bash", parser.TryParseToolName(multiToolLine));
    }

    [Fact]
    public void Claude_CountWriteToolSteps_counts_every_write_family_block_and_no_read_blocks()
    {
        var parser = new ClaudeUsageParser();
        const string line = """
            {"type":"assistant","message":{"content":[{"type":"tool_use","name":"Edit"},{"type":"tool_use","name":"Read"},{"type":"tool_use","name":"NotebookEdit"}]}}
            """;

        Assert.Equal(2, parser.CountWriteToolSteps(line));
    }

    [Fact]
    public void Claude_parser_TryParseIncrementalUsage_reads_message_id_onto_the_usage_record()
    {
        // #1686 review F6: the caller (TokenBudgetMonitor) needs this to dedupe a repeated message.id
        // rather than summing the same message's usage twice.
        var parser = new ClaudeUsageParser();
        const string line = """{"type":"assistant","message":{"id":"msg_abc123","usage":{"input_tokens":2,"cache_creation_input_tokens":100,"cache_read_input_tokens":0,"output_tokens":3}}}""";

        Assert.True(parser.TryParseIncrementalUsage(line, out var usage));
        Assert.Equal("msg_abc123", usage!.MessageId);
    }

    [Fact]
    public void Claude_parser_TryParseIncrementalUsage_leaves_message_id_null_when_absent()
    {
        var parser = new ClaudeUsageParser();
        const string line = """{"type":"assistant","message":{"usage":{"input_tokens":2,"cache_creation_input_tokens":100,"cache_read_input_tokens":0,"output_tokens":3}}}""";

        Assert.True(parser.TryParseIncrementalUsage(line, out var usage));
        Assert.Null(usage!.MessageId);
    }

    [Fact]
    public void Claude_parser_TryParseFinalUsage_sums_the_whole_tree_modelUsage_over_the_top_level_usage()
    {
        // #1706: the terminal read is whole-tree now. Two models here, neither of them the shape a
        // single-model room produces -- summing across the map is the behaviour, and the top-level
        // `usage` object present on the SAME line (main-thread only) is what it must NOT return.
        var parser = new ClaudeUsageParser();
        const string line = """
            {"type":"result","num_turns":7,"usage":{"input_tokens":10,"output_tokens":20,"cache_read_input_tokens":30,"cache_creation_input_tokens":40,"output_tokens_details":{"thinking_tokens":5}},"modelUsage":{"claude-opus-5":{"inputTokens":100,"outputTokens":200,"cacheReadInputTokens":300,"cacheCreationInputTokens":400,"thinkingTokens":50},"claude-haiku-4-5":{"inputTokens":1,"outputTokens":2,"cacheReadInputTokens":3,"cacheCreationInputTokens":4,"thinkingTokens":5}}}
            """;

        Assert.True(parser.TryParseFinalUsage(line, out var usage));
        Assert.Equal(101, usage!.TokensIn);
        Assert.Equal(202, usage.TokensOut);
        Assert.Equal(303, usage.CacheReadTokens);
        Assert.Equal(404, usage.CacheCreationTokens);
        Assert.Equal(55, usage.ThinkingTokens);
        // num_turns has no modelUsage analogue and stays a top-level read.
        Assert.Equal(7, usage.Turns);
        // A terminal reading is a measurement, never a floor -- the polarity opposite of every
        // incremental claude reading above.
        Assert.False(usage.BilledIsFloor);
    }

    [Fact]
    public void Claude_parser_TryParseFinalUsage_falls_back_to_top_level_usage_when_modelUsage_is_absent()
    {
        // #1706: the fallback arm TryParseFinalUsage's own doc comment describes -- pinned so a later
        // simplification cannot quietly turn a modelUsage-less capture into no reading at all.
        var parser = new ClaudeUsageParser();
        const string line = """{"type":"result","num_turns":7,"usage":{"input_tokens":10,"output_tokens":20,"cache_read_input_tokens":30,"cache_creation_input_tokens":40}}""";

        Assert.True(parser.TryParseFinalUsage(line, out var usage));
        Assert.Equal(10, usage!.TokensIn);
        Assert.Equal(20, usage.TokensOut);
        Assert.Equal(30, usage.CacheReadTokens);
        Assert.Equal(40, usage.CacheCreationTokens);
    }

    [Fact]
    public void Claude_parser_TryParseFinalUsage_falls_back_PER_FIELD_when_modelUsage_is_missing_one_figure()
    {
        // #1724 item 4: modelUsage carries every figure except thinkingTokens (a real, measured shape --
        // both captures underlying spec/baton.md §3 carry all five keys together, but the whole-tree
        // read must not assume every future one will). Before the fix, ANY figure present in modelUsage
        // short-circuited the whole top-level fallback, so the top-level usage object's own
        // output_tokens_details.thinking_tokens:5 was silently dropped instead of filling the gap. This
        // goes red on the prior all-or-nothing read, which reports a null ThinkingTokens here.
        var parser = new ClaudeUsageParser();
        const string line = """
            {"type":"result","num_turns":7,"usage":{"input_tokens":10,"output_tokens":20,"cache_read_input_tokens":30,"cache_creation_input_tokens":40,"output_tokens_details":{"thinking_tokens":5}},"modelUsage":{"claude-opus-5":{"inputTokens":100,"outputTokens":200,"cacheReadInputTokens":300,"cacheCreationInputTokens":400}}}
            """;

        Assert.True(parser.TryParseFinalUsage(line, out var usage));
        // The four figures modelUsage DID carry win over the top-level object, same as the whole-tree test above.
        Assert.Equal(100, usage!.TokensIn);
        Assert.Equal(200, usage.TokensOut);
        Assert.Equal(300, usage.CacheReadTokens);
        Assert.Equal(400, usage.CacheCreationTokens);
        // The one figure modelUsage did NOT carry falls back to the top-level object instead of staying null.
        Assert.Equal(5, usage.ThinkingTokens);
    }

    [Fact]
    public void Claude_parser_TryParseFinalUsage_keeps_a_measured_zero_in_modelUsage_over_the_top_level_figure()
    {
        // #1724 item 4, the other half of the per-field rule: a figure modelUsage carries as an explicit
        // 0 is MEASURED, not absent, so it must win over a non-zero top-level value -- the fallback is
        // keyed on null, never on zero (the repo's absent-vs-zero discipline). Goes red if the fallback
        // ever tests `<= 0` or truthiness instead of `is null`.
        var parser = new ClaudeUsageParser();
        const string line = """
            {"type":"result","num_turns":1,"usage":{"input_tokens":999,"output_tokens":20,"cache_read_input_tokens":30,"cache_creation_input_tokens":40,"output_tokens_details":{"thinking_tokens":5}},"modelUsage":{"claude-opus-5":{"inputTokens":0,"outputTokens":200,"cacheReadInputTokens":300,"cacheCreationInputTokens":400,"thinkingTokens":0}}}
            """;

        Assert.True(parser.TryParseFinalUsage(line, out var usage));
        Assert.Equal(0, usage!.TokensIn);
        Assert.Equal(0, usage.ThinkingTokens);
        Assert.Equal(200, usage.TokensOut);
    }
}
