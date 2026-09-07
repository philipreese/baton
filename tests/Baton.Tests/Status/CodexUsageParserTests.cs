using Baton.Artifacts;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Status;

namespace Baton.Tests.Status;

public sealed class CodexUsageParserTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Room_usage_sums_three_turns_only_when_the_capture_is_complete(bool rolled, bool truncated)
    {
        var root = Path.Combine(Path.GetTempPath(), $"codex-usage-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("codex-three-turns");
            var start = DateTime.UtcNow;
            var request = new ExecutionRequest(
                executionId, new WorkflowId("usage"), new StepId("implement"), "implement",
                Inputs: [], Outputs: [], Timeout: TimeSpan.FromSeconds(30), Environment: [],
                UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>(), Adapter: "codex");
            LogEntry[] entries =
            [
                new LogEntry.FlowLogEntry(new FlowEvent.ExecutionRequestAccepted(request)),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(executionId, Pid: 1), start),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(executionId, 0, CoreExitReason.Natural), start.AddSeconds(1)),
            ];
            var lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "codex-three-turn-usage.jsonl"));
            var output = ArtifactManager.ResolveOutputDirectory(root, executionId);
            Directory.CreateDirectory(output);
            File.WriteAllLines(Path.Combine(output, ExecutionStreamLogger.StdoutLogFileName), rolled ? lines.Skip(2) : lines);
            if (rolled)
            {
                File.WriteAllLines(Path.Combine(output, ExecutionStreamLogger.StdoutRolloverFileName), lines.Take(2));
            }

            if (truncated)
            {
                File.WriteAllText(Path.Combine(output, ExecutionStreamLogger.StdoutTruncationMarkerFileName), "");
            }

            var view = Assert.Single(ExecutionUsageProjector.BuildByExecutionId(entries, root)).Value;
            if (truncated)
            {
                Assert.Null(view.TokensIn);
                Assert.Null(view.CacheReadTokens);
                Assert.Null(view.TokensOut);
                Assert.Null(view.ThinkingTokens);
                Assert.Equal(ExecutionUsageView.StreamTruncatedByRolloverReason, view.BilledReconciliationUnavailable);
                return;
            }

            Assert.Equal(1500, view.TokensIn);
            Assert.Equal(4500, view.CacheReadTokens);
            Assert.Equal(6000, view.TokensIn + view.CacheReadTokens);
            Assert.Equal(600, view.TokensOut);
            Assert.Equal(120, view.ThinkingTokens);
            Assert.Equal(3, view.Turns);
            Assert.Null(view.CacheCreationTokens);
            Assert.Equal(2100, view.BilledTokens);
            Assert.Equal(view.BilledTokens, view.LiveBilledTokens);
            Assert.Equal(0, view.BilledUnderReadTokens);
            Assert.Equal(view, Assert.Single(ExecutionUsageProjector.BuildByExecutionId(entries, root)).Value);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public void Execution_sum_is_isolated_per_read_and_preserves_partial_dimensions()
    {
        var parser = new CodexUsageParser();
        string[] lines =
        [
            """{"type":"turn.completed","usage":{"cached_input_tokens":10}}""",
            "unrelated output",
            """{"type":"turn.completed","usage":{"output_tokens":20,"cache_write_input_tokens":3}}""",
            """{"type":"turn.completed","usage":{"cached_input_tokens":5,"cache_write_input_tokens":4}}""",
        ];
        var total = parser.ParseExecutionUsage(lines);
        Assert.NotNull(total);
        Assert.Null(total.TokensIn);
        Assert.Null(total.ThinkingTokens);
        Assert.Equal(15, total.CacheReadTokens);
        Assert.Equal(20, total.TokensOut);
        Assert.Equal(7, total.CacheCreationTokens);
        Assert.Equal(3, total.Turns);
        Assert.Equal(total, parser.ParseExecutionUsage(lines));
        Assert.Null(parser.ParseExecutionUsage(["unrelated output"]));
        Assert.True(parser.TryParseFinalUsage(lines[0], out var single));
        Assert.Equal(single, parser.ParseExecutionUsage([lines[0]]));
    }

    [Fact]
    public void App_server_dynamic_tool_start_is_counted_by_the_shared_live_monitor_parser()
    {
        const string line = """
            {"type":"item.started","item":{"type":"mcp_tool_call","tool":"baton_write_output"}}
            """;
        var parser = new CodexUsageParser();

        Assert.Equal("baton_write_output", parser.TryParseToolName(line));
        Assert.Equal(1, parser.CountToolSteps(line));
        Assert.Equal(0, parser.CountToolSteps(
            "{\"type\":\"item.completed\",\"item\":{\"type\":\"mcp_tool_call\"}}"));
    }

    [Fact]
    public void Completed_turn_separates_uncached_input_from_cache_and_preserves_other_usage_fields()
    {
        var parser = new CodexUsageParser();
        const string line = """
            {"type":"turn.completed","usage":{"input_tokens":14750,"cached_input_tokens":8960,"cache_write_input_tokens":17,"output_tokens":211,"reasoning_output_tokens":48}}
            """;

        Assert.True(parser.TryParseFinalUsage(line, out var usage));
        Assert.NotNull(usage);
        Assert.Equal(5790, usage.TokensIn);
        Assert.Equal(8960, usage.CacheReadTokens);
        Assert.Equal(17, usage.CacheCreationTokens);
        Assert.Equal(211, usage.TokensOut);
        Assert.Equal(48, usage.ThinkingTokens);
        Assert.Equal(1, usage.Turns);
    }

    /// <summary>
    /// #2020: the two reads share per-turn semantics; incremental spans both line types while final
    /// stays on the terminal one. Asserting that the per-round-trip line is NOT a final-usage line is
    /// the half that discriminates — it is what stops a mid-turn line being read as an execution's
    /// terminal report.
    /// </summary>
    [Fact]
    public void Incremental_spans_both_line_types_while_final_usage_stays_on_the_terminal_one()
    {
        var parser = new CodexUsageParser();
        const string usage = """
            {"type":"turn.usage","usage":{"input_tokens":19579,"cached_input_tokens":11008,"output_tokens":9,"reasoning_output_tokens":0}}
            """;
        const string completed = """
            {"type":"turn.completed","usage":{"input_tokens":19579,"cached_input_tokens":11008,"output_tokens":9,"reasoning_output_tokens":0}}
            """;

        Assert.True(parser.TryParseIncrementalUsage(usage, out var incremental));
        Assert.True(parser.TryParseIncrementalUsage(completed, out var alsoIncremental));
        Assert.True(parser.TryParseFinalUsage(completed, out var final));
        Assert.Equal(final, incremental);
        Assert.Equal(final, alsoIncremental);

        Assert.False(parser.TryParseFinalUsage(usage, out var notFinal));
        Assert.Null(notFinal);
    }

    /// <summary>
    /// #2020: the terminal line restates the last round-trip, so a Σ over a CURRENT stream must count
    /// it once. The mechanism is <see cref="CodexUsageParser.RoundTripField"/> feeding
    /// <see cref="WorkerUsage.MessageId"/>, which
    /// <see cref="Baton.Mutation.TokenBudgetMonitor"/> already deduplicates on. The last assertion is
    /// the one that keeps the mechanism honest — why a pre-emitter capture must still yield an
    /// untagged reading is on <see cref="CodexUsageParser.TryParseIncrementalUsage"/>, and what it
    /// costs a real room if it does not is
    /// <c>Baton.Vendors.Tests.CodexBrokerRoomUsageTests</c>'s own control arm.
    /// </summary>
    [Fact]
    public void The_round_trip_index_is_carried_only_when_the_stream_reports_one()
    {
        var parser = new CodexUsageParser();

        Assert.True(parser.TryParseIncrementalUsage(
            """{"type":"turn.usage","usage":{"output_tokens":9,"round_trip":3}}""", out var tagged));
        Assert.Equal("round-trip-3", tagged!.MessageId);
        Assert.True(parser.TryParseIncrementalUsage(
            """{"type":"turn.completed","usage":{"output_tokens":9,"round_trip":3}}""", out var restated));
        Assert.Equal(tagged.MessageId, restated!.MessageId);

        Assert.True(parser.TryParseIncrementalUsage(
            """{"type":"turn.completed","usage":{"output_tokens":9}}""", out var legacy));
        Assert.Null(legacy!.MessageId);
    }

    /// <summary>
    /// #2020 review LOW: the fold carries every dimension it does not sum. <see cref="WorkerUsage"/>
    /// has thirteen fields and codex's own line parse sets six, so a fold driven from a codex stream
    /// cannot observe the other seven — which is exactly why the positional reconstruction this
    /// replaced could drop them unnoticed. Driving <c>Combine</c> directly with a reading that carries
    /// one of the seven is the arm that discriminates: red on the six-argument constructor, green on
    /// <c>with</c>.
    /// </summary>
    [Fact]
    public void Folding_two_turns_carries_the_dimensions_it_does_not_sum()
    {
        var first = new WorkerUsage(
            TokensIn: 10, TokensOut: 2, Turns: 1, CacheReadTokens: 5, CacheCreationTokens: 1,
            ThinkingTokens: 3)
        {
            MessageId = "msg-1",
            ContextLevelTokens = 900,
            CacheReadLevelTokens = 800,
            BilledTokens = 12,
            BilledIsFloor = true,
            IsSubAgentTurn = true,
            ModelsObserved = ["gpt-5.6-luna"],
        };
        var second = new WorkerUsage(
            TokensIn: 20, TokensOut: 4, Turns: 1, CacheReadTokens: 6, CacheCreationTokens: 2,
            ThinkingTokens: 7);

        var folded = CodexUsageParser.Combine(first, second);

        Assert.Equal(30, folded.TokensIn);
        Assert.Equal(6, folded.TokensOut);
        Assert.Equal(2, folded.Turns);
        Assert.Equal(11, folded.CacheReadTokens);
        Assert.Equal(3, folded.CacheCreationTokens);
        Assert.Equal(10, folded.ThinkingTokens);

        Assert.Equal("msg-1", folded.MessageId);
        Assert.Equal(900, folded.ContextLevelTokens);
        Assert.Equal(800, folded.CacheReadLevelTokens);
        Assert.Equal(12, folded.BilledTokens);
        Assert.True(folded.BilledIsFloor);
        Assert.True(folded.IsSubAgentTurn);
        Assert.Equal(["gpt-5.6-luna"], folded.ModelsObserved);
    }

    [Fact]
    public void Cached_input_larger_than_total_input_clamps_uncached_input_to_zero()
    {
        var parser = new CodexUsageParser();
        const string line = """
            {"type":"turn.completed","usage":{"input_tokens":5,"cached_input_tokens":8}}
            """;

        Assert.True(parser.TryParseFinalUsage(line, out var usage));
        Assert.Equal(0, usage!.TokensIn);
        Assert.Equal(8, usage.CacheReadTokens);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"type\":\"turn.started\"}")]
    [InlineData("{\"type\":\"turn.completed\"}")]
    [InlineData("{\"type\":\"turn.completed\",\"usage\":{}}")]
    [InlineData("{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":null,\"cached_input_tokens\":null,\"cache_write_input_tokens\":null,\"output_tokens\":null,\"reasoning_output_tokens\":null}}")]
    public void Malformed_unrelated_and_all_null_lines_are_not_usage(string line)
    {
        var parser = new CodexUsageParser();

        Assert.False(parser.TryParseFinalUsage(line, out var usage));
        Assert.Null(usage);
    }

    /// <summary>
    /// #2008, the read-back half: a codex room stream built from a real room's shape (scrubbed) —
    /// <c>dispatch-implement-26c16874</c>, whose every <c>item.started</c> carries the digest — with the
    /// input identity #2008 adds, and the two cross-vendor readings taken over it.
    /// <para>
    /// The counting arms are the discrimination, not the presence: both of a call's items now carry the
    /// field (<c>Baton.Vendors.CodexAppServerBroker.Describe</c> states why that counts nothing twice),
    /// so a reader that stopped gating on <c>item.started</c> would report each of these two commands
    /// twice. Two commands over four run-command items is what pins that.
    /// </para>
    /// </summary>
    [Fact]
    public void A_codex_room_stream_yields_one_command_line_and_one_key_per_tool_call()
    {
        var parser = new CodexUsageParser();
        string[] stream =
        [
            Started("baton_read_text", "aaaaaaaaaaaaaaaa", "spec/baton.md"),
            Completed("baton_read_text", "aaaaaaaaaaaaaaaa", "spec/baton.md"),
            Started("baton_run_command", "bbbbbbbbbbbbbbbb", "pixi run gates-fast"),
            Completed("baton_run_command", "bbbbbbbbbbbbbbbb", "pixi run gates-fast"),
            Started("baton_run_command", "cccccccccccccccc", "git push"),
            Completed("baton_run_command", "cccccccccccccccc", "git push"),
        ];

        var commands = stream.SelectMany(parser.ShellCommandLines).ToArray();
        var keys = stream.SelectMany(parser.ToolInvocationKeys).ToArray();

        Assert.Equal(["pixi run gates-fast", "git push"], commands);
        Assert.Equal(3, keys.Length);
        Assert.Equal(3, keys.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// #2008 polarity: the reading rests on the field Baton's broker stamps, so a codex stream captured
    /// before #2008 landed reports no command line rather than a fabricated one — the same "cannot
    /// answer contributes nothing" <see cref="CodexUsageParser.ToolInvocationKeys"/> already applies to
    /// a pre-#1963 stream's digest. Non-shell tools carry an identity too and are still not command
    /// lines.
    /// </summary>
    [Theory]
    [InlineData("""{"type":"item.started","item":{"type":"mcp_tool_call","tool":"baton_run_command","argumentsDigest":"bbbbbbbbbbbbbbbb"}}""")]
    [InlineData("""{"type":"item.started","item":{"type":"mcp_tool_call","tool":"baton_read_text","argumentsDigest":"aaaaaaaaaaaaaaaa","argumentsIdentity":"spec/baton.md"}}""")]
    [InlineData("""{"type":"item.completed","item":{"type":"mcp_tool_call","tool":"baton_run_command","argumentsDigest":"bbbbbbbbbbbbbbbb","argumentsIdentity":"git push","status":"completed","aggregated_output":""}}""")]
    [InlineData("""{"type":"item.started","item":{"type":"agent_message","text":"git push"}}""")]
    [InlineData("not json")]
    [InlineData("")]
    public void A_line_that_announces_no_codex_shell_command_reports_none(string rawLine) =>
        Assert.Empty(new CodexUsageParser().ShellCommandLines(rawLine));

    private static string Started(string tool, string digest, string identity) =>
        $$$"""{"type":"item.started","item":{"type":"mcp_tool_call","tool":"{{{tool}}}","argumentsDigest":"{{{digest}}}","argumentsIdentity":"{{{identity}}}"}}""";

    private static string Completed(string tool, string digest, string identity) =>
        $$$"""{"type":"item.completed","item":{"type":"mcp_tool_call","tool":"{{{tool}}}","argumentsDigest":"{{{digest}}}","argumentsIdentity":"{{{identity}}}","status":"completed","aggregated_output":"ok"}}""";

    [Fact]
    public void A_partial_usage_object_preserves_absence_instead_of_fabricating_zeroes()
    {
        var parser = new CodexUsageParser();
        const string line = """
            {"type":"turn.completed","usage":{"cached_input_tokens":321}}
            """;

        Assert.True(parser.TryParseFinalUsage(line, out var usage));
        Assert.Null(usage!.TokensIn);
        Assert.Equal(321, usage.CacheReadTokens);
        Assert.Null(usage.CacheCreationTokens);
        Assert.Null(usage.TokensOut);
        Assert.Null(usage.ThinkingTokens);
        Assert.Equal(1, usage.Turns);
    }
}
