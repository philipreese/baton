using Baton.Domain;
using Baton.Mutation;
using Baton.Status;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Baton.Tests.Mutation;

/// <summary>
/// Coverage for <see cref="TokenBudgetMonitor"/> (#1623 ruling addendum, revised by #1682, issue title
/// in <c>TokenBudgetMonitor</c>'s own doc) — pure accumulation logic against real per-vendor parsers,
/// no process involved. <c>MutationInterfaceTests</c> covers the wiring into a live dispatch.
/// </summary>
public sealed class TokenBudgetMonitorTests
{
    [Fact]
    public void ArrestRequested_fires_once_the_running_SUM_of_billed_tokens_crosses_the_budget()
    {
        // #1682: billed is additive across turns, NOT a level -- unlike the pre-#1682 arithmetic this
        // replaces. #1706: on claude the only measurable billed component is cache_creation (the
        // mid-stream usage figures on this line are placeholders and are no longer read), so the two
        // turns bill 500 and 1100; running sum 1600 >= 1000 crosses on turn 2.
        var monitor = new TokenBudgetMonitor(budget: 1000, maxToolSteps: null, billedRateLimit: null, new ClaudeUsageParser());

        monitor.OnStdoutLine("""{"type":"assistant","message":{"usage":{"input_tokens":2,"cache_creation_input_tokens":500,"cache_read_input_tokens":0,"output_tokens":3}}}""");
        Assert.False(monitor.Arrested);
        Assert.False(monitor.ArrestRequested.IsCancellationRequested);

        monitor.OnStdoutLine("""{"type":"assistant","message":{"usage":{"input_tokens":2,"cache_creation_input_tokens":1100,"cache_read_input_tokens":0,"output_tokens":3}}}""");

        Assert.True(monitor.Arrested);
        Assert.True(monitor.ArrestRequested.IsCancellationRequested);
        Assert.Equal(ArrestReason.TokenBudget, monitor.ArrestReasonValue);
        Assert.Equal(1600, monitor.SnapshotUsage().BilledTokens);
    }

    [Fact]
    public void A_budget_never_reached_leaves_the_monitor_unarrested()
    {
        var monitor = new TokenBudgetMonitor(budget: 1_000_000, maxToolSteps: null, billedRateLimit: null, new ClaudeUsageParser());

        monitor.OnStdoutLine("""{"type":"assistant","message":{"usage":{"input_tokens":2,"cache_creation_input_tokens":500,"cache_read_input_tokens":0,"output_tokens":3}}}""");

        Assert.False(monitor.Arrested);
        Assert.False(monitor.ArrestRequested.IsCancellationRequested);
    }

    [Fact]
    public void SnapshotUsage_reports_the_context_level_display_field_unchanged_and_sums_billed_separately()
    {
        // Pins the two fields' distinct meaning: ContextLevelTokens is a LEVEL (replaced each line),
        // BilledTokens is a Σ. #1706: on claude the level is now cache_read + cache_creation, the
        // placeholder input_tokens having been dropped from the reading entirely -- and TokensIn/
        // TokensOut are null on the snapshot for the same reason.
        var monitor = new TokenBudgetMonitor(budget: 1_000_000, maxToolSteps: null, billedRateLimit: null, new ClaudeUsageParser());

        monitor.OnStdoutLine("""{"type":"assistant","message":{"usage":{"input_tokens":2,"cache_creation_input_tokens":10,"cache_read_input_tokens":100,"output_tokens":3}}}""");
        monitor.OnStdoutLine("""{"type":"assistant","message":{"usage":{"input_tokens":2,"cache_creation_input_tokens":20,"cache_read_input_tokens":200,"output_tokens":3}}}""");

        var usage = monitor.SnapshotUsage();

        Assert.Null(usage.TokensIn);
        Assert.Equal(220, usage.ContextLevelTokens);
        Assert.Null(usage.TokensOut);
        Assert.Equal(30, usage.BilledTokens);
        Assert.Equal(300, usage.CacheReadTokens);
        Assert.True(usage.BilledIsFloor);
    }

    /// <summary>#1557: the plan's one named gap in reuse -- <c>WorkerUsage.Turns</c> existed as a
    /// field but <c>SnapshotUsage</c> never populated it. See the <c>_turns</c> field's own comment
    /// for what it counts.</summary>
    [Fact]
    public void SnapshotUsage_counts_Turns_as_one_per_usage_bearing_line_contributing_to_BilledTokens()
    {
        var monitor = new TokenBudgetMonitor(budget: null, maxToolSteps: null, billedRateLimit: null, new ClaudeUsageParser());

        monitor.OnStdoutLine("""{"type":"assistant","message":{"usage":{"input_tokens":2,"cache_creation_input_tokens":10,"cache_read_input_tokens":100,"output_tokens":3}}}""");
        monitor.OnStdoutLine("not json, and not a usage line at all");
        monitor.OnStdoutLine("""{"type":"assistant","message":{"usage":{"input_tokens":2,"cache_creation_input_tokens":20,"cache_read_input_tokens":200,"output_tokens":3}}}""");

        Assert.Equal(2, monitor.SnapshotUsage().Turns);
    }

    [Fact]
    public void A_sub_agents_smaller_context_never_lowers_the_tracked_level_below_the_parents()
    {
        // #1666: parent line (level L1=300) -> sub-agent line (level 40 < L1, marked the way
        // ClaudeUsageParser.TryParseIncrementalUsage measures one) -> parent line (level L2=350 >=
        // L1). Red on origin/main: the old replace-on-every-line read let the sub-agent's 40 overwrite
        // 300, so the level dipped between the first and third assertions instead of holding at 300.
        var monitor = new TokenBudgetMonitor(budget: 1_000_000, maxToolSteps: null, billedRateLimit: null, new ClaudeUsageParser());

        monitor.OnStdoutLine(
            """{"type":"assistant","parent_tool_use_id":null,"message":{"usage":{"cache_creation_input_tokens":10,"cache_read_input_tokens":290}}}""");
        Assert.Equal(300, monitor.SnapshotUsage().ContextLevelTokens);

        monitor.OnStdoutLine(
            """{"type":"assistant","parent_tool_use_id":"toolu_01subagent","message":{"usage":{"cache_creation_input_tokens":5,"cache_read_input_tokens":35}}}""");
        Assert.Equal(300, monitor.SnapshotUsage().ContextLevelTokens);

        monitor.OnStdoutLine(
            """{"type":"assistant","parent_tool_use_id":null,"message":{"usage":{"cache_creation_input_tokens":10,"cache_read_input_tokens":340}}}""");
        Assert.Equal(350, monitor.SnapshotUsage().ContextLevelTokens);
    }

    [Fact]
    public void A_parent_line_clears_the_sub_agent_bucket_so_a_genuine_same_bucket_drop_still_shows()
    {
        // Review F3: parent line (level 300) -> sub-agent line (level 150) -> parent line (level 100,
        // a genuine drop, e.g. after compaction) asserts 100. The sub-agent reading sits BETWEEN the
        // two parent readings on purpose: without the clear, Math.Max(100, 150) reports 150, so this
        // arm is red on the uncleared-bucket code -- a sub-agent reading below 100 would pass either
        // way and pin nothing.
        var monitor = new TokenBudgetMonitor(budget: 1_000_000, maxToolSteps: null, billedRateLimit: null, new ClaudeUsageParser());

        monitor.OnStdoutLine(
            """{"type":"assistant","parent_tool_use_id":null,"message":{"usage":{"cache_creation_input_tokens":10,"cache_read_input_tokens":290}}}""");
        Assert.Equal(300, monitor.SnapshotUsage().ContextLevelTokens);

        monitor.OnStdoutLine(
            """{"type":"assistant","parent_tool_use_id":"toolu_01subagent","message":{"usage":{"cache_creation_input_tokens":10,"cache_read_input_tokens":140}}}""");
        Assert.Equal(300, monitor.SnapshotUsage().ContextLevelTokens);

        monitor.OnStdoutLine(
            """{"type":"assistant","parent_tool_use_id":null,"message":{"usage":{"cache_creation_input_tokens":10,"cache_read_input_tokens":90}}}""");
        Assert.Equal(100, monitor.SnapshotUsage().ContextLevelTokens);
    }

    [Fact]
    public void A_sub_agent_line_with_no_parent_reading_yet_still_counts_as_the_level()
    {
        // #1666, acceptance's second clause: a fan-out sub-agent turn that arrives before the parent
        // has ever reported usage must not be dropped -- the level starts from it.
        var monitor = new TokenBudgetMonitor(budget: 1_000_000, maxToolSteps: null, billedRateLimit: null, new ClaudeUsageParser());

        monitor.OnStdoutLine(
            """{"type":"assistant","parent_tool_use_id":"toolu_01subagent","message":{"usage":{"cache_creation_input_tokens":5,"cache_read_input_tokens":35}}}""");

        Assert.Equal(40, monitor.SnapshotUsage().ContextLevelTokens);
    }

    [Fact]
    public void Billed_tokens_include_cache_creation_but_never_thinking()
    {
        // Pins the exclusion StandardWorkerUsageParsersTests' real-line test proves against actual
        // vendor data (spec/baton.md §3) -- cache_creation counts here, thinking never does. #1706:
        // the line's own input_tokens (2) and output_tokens (4) no longer contribute either, being
        // mid-stream placeholders, so the whole billed figure on this vendor is the cache-creation column.
        var monitor = new TokenBudgetMonitor(budget: 1_000_000, maxToolSteps: null, billedRateLimit: null, new ClaudeUsageParser());

        monitor.OnStdoutLine(
            """{"type":"assistant","message":{"usage":{"input_tokens":2,"cache_creation_input_tokens":12066,"cache_read_input_tokens":15092,"output_tokens":4}}}""");

        var usage = monitor.SnapshotUsage();
        Assert.Equal(12066, usage.BilledTokens);
        Assert.Equal(15092, usage.CacheReadTokens);
    }

    [Fact]
    public void CacheReadTokens_on_the_snapshot_is_a_running_sum_not_the_latest_reading()
    {
        // #1682: display-only Σ across every incremental line, unlike ContextLevelTokens which stays a level.
        var monitor = new TokenBudgetMonitor(budget: 1_000_000, maxToolSteps: null, billedRateLimit: null, new ClaudeUsageParser());

        monitor.OnStdoutLine("""{"type":"assistant","message":{"usage":{"input_tokens":2,"output_tokens":3,"cache_read_input_tokens":100}}}""");
        monitor.OnStdoutLine("""{"type":"assistant","message":{"usage":{"input_tokens":2,"output_tokens":3,"cache_read_input_tokens":50}}}""");

        Assert.Equal(150, monitor.SnapshotUsage().CacheReadTokens);
    }

    [Fact]
    public void A_room_that_never_crosses_the_billed_budget_can_still_be_caught_by_the_tool_step_cap()
    {
        // The other #1682 evidence room never crosses a plausible budget at all (spec/baton.md §3) --
        // this pins that the cap alone still catches that shape. #1686 review F2: agy's cap counts a
        // REAL tool call only at its terminal (DONE/ERROR) line, not its ACTIVE heartbeat -- each call
        // here fires both, only the DONE line increments the count.
        var monitor = new TokenBudgetMonitor(budget: 600_000, maxToolSteps: 80, billedRateLimit: null, new AgyUsageParser());

        for (var i = 0; i < 80; i++)
        {
            monitor.OnStdoutLine(
                """{"event":"step_update","step_update":{"state":"ACTIVE","step_type":"tool","tool_name":"run_command"}}""");
            monitor.OnStdoutLine(
                """{"event":"step_update","step_update":{"state":"DONE","step_type":"tool","tool_name":"run_command"}}""");
        }

        Assert.False(monitor.Arrested);

        monitor.OnStdoutLine(
            """{"event":"step_update","step_update":{"state":"ACTIVE","step_type":"tool","tool_name":"run_command"}}""");
        monitor.OnStdoutLine(
            """{"event":"step_update","step_update":{"state":"DONE","step_type":"tool","tool_name":"run_command"}}""");

        Assert.True(monitor.Arrested);
        Assert.Equal(ArrestReason.ToolStepCap, monitor.ArrestReasonValue);
        Assert.Equal(81, monitor.SnapshotToolStepCount());
        Assert.Null(monitor.SnapshotUsage().BilledTokens);
    }

    [Fact]
    public void Agy_tool_step_cap_counts_the_terminal_line_only_not_the_ACTIVE_heartbeat()
    {
        // #1686 review F2: the pre-fix unit double-counted (ACTIVE + terminal) each real call. This
        // pins the fixed unit directly -- 3 ACTIVE-only heartbeats for calls that never reach a
        // terminal state must not arm a cap of 2.
        var monitor = new TokenBudgetMonitor(budget: null, maxToolSteps: 2, billedRateLimit: null, new AgyUsageParser());

        monitor.OnStdoutLine("""{"event":"step_update","step_update":{"state":"ACTIVE","step_type":"tool","tool_name":"a"}}""");
        monitor.OnStdoutLine("""{"event":"step_update","step_update":{"state":"ACTIVE","step_type":"tool","tool_name":"b"}}""");
        monitor.OnStdoutLine("""{"event":"step_update","step_update":{"state":"ACTIVE","step_type":"tool","tool_name":"c"}}""");

        Assert.False(monitor.Arrested);
        Assert.Equal(0, monitor.SnapshotToolStepCount());

        monitor.OnStdoutLine("""{"event":"step_update","step_update":{"state":"DONE","step_type":"tool","tool_name":"a"}}""");
        monitor.OnStdoutLine("""{"event":"step_update","step_update":{"state":"DONE","step_type":"tool","tool_name":"b"}}""");
        Assert.False(monitor.Arrested);
        monitor.OnStdoutLine("""{"event":"step_update","step_update":{"state":"ERROR","step_type":"tool","tool_name":"c"}}""");

        Assert.True(monitor.Arrested);
        Assert.Equal(ArrestReason.ToolStepCap, monitor.ArrestReasonValue);
        Assert.Equal(3, monitor.SnapshotToolStepCount());
    }

    [Fact]
    public void Claude_billed_tokens_dedupe_a_repeated_message_id_instead_of_summing_it_twice()
    {
        // #1686 review F6 -- ClaudeUsageParser.TryParseIncrementalUsage's own doc has the measured
        // shape this reproduces; summing every line without deduping over-counts.
        var monitor = new TokenBudgetMonitor(budget: 1_000_000, maxToolSteps: null, billedRateLimit: null, new ClaudeUsageParser());

        monitor.OnStdoutLine(
            """{"type":"assistant","message":{"id":"msg_1","usage":{"input_tokens":2,"cache_creation_input_tokens":110,"cache_read_input_tokens":0,"output_tokens":3}}}""");
        monitor.OnStdoutLine(
            """{"type":"assistant","message":{"id":"msg_1","usage":{"input_tokens":2,"cache_creation_input_tokens":110,"cache_read_input_tokens":0,"output_tokens":3}}}""");
        monitor.OnStdoutLine(
            """{"type":"assistant","message":{"id":"msg_1","usage":{"input_tokens":2,"cache_creation_input_tokens":110,"cache_read_input_tokens":0,"output_tokens":3}}}""");
        monitor.OnStdoutLine(
            """{"type":"assistant","message":{"id":"msg_2","usage":{"input_tokens":2,"cache_creation_input_tokens":55,"cache_read_input_tokens":0,"output_tokens":3}}}""");

        var usage = monitor.SnapshotUsage();
        Assert.Equal(110 + 55, usage.BilledTokens);
        // #1706: the placeholder output column is not read at all, so there is no Σ of it to report.
        Assert.Null(usage.TokensOut);
    }

    [Fact]
    public void The_dedupe_premise_holds_on_a_real_consecutive_pair_from_room_3dc5e21a()
    {
        // #1686 review F4: the premise that makes first-sighting dedupe correct -- that a repeated
        // message.id carries an IDENTICAL usage object across disjoint content-block chunks -- was
        // asserted, not recorded; every prior fixture used a synthetic msg_1 with identity true by
        // construction. This is a REAL consecutive pair, lines 5-6 of
        // `dispatch-implement-3dc5e21a`'s own `.stdout.log`
        // (`artifacts/execution_b3cdfeb7684f459a9af0baca24c6e1c3/.stdout.log`), trimmed to the `usage`
        // and `content[].type` fields per the review's own instruction. Measured: `usage` is IDENTICAL
        // between the two lines (input_tokens 2, cache_creation_input_tokens 39901,
        // cache_read_input_tokens 0, output_tokens 1 on both), and `content[].type` is DISJOINT ("text"
        // on the first, "tool_use" on the second) -- exactly the shape the dedupe assumes. This
        // confirms first-sighting dedupe is correct on the measured shape; it does not generalize past
        // this fixture (the review's own caveat).
        var monitor = new TokenBudgetMonitor(budget: 1_000_000, maxToolSteps: null, billedRateLimit: null, new ClaudeUsageParser());

        monitor.OnStdoutLine(
            """{"type":"assistant","message":{"id":"msg_011Cee7wqgwCecnuPg5NCH6y","content":[{"type":"text"}],"usage":{"input_tokens":2,"cache_creation_input_tokens":39901,"cache_read_input_tokens":0,"output_tokens":1}}}""");
        monitor.OnStdoutLine(
            """{"type":"assistant","message":{"id":"msg_011Cee7wqgwCecnuPg5NCH6y","content":[{"type":"tool_use"}],"usage":{"input_tokens":2,"cache_creation_input_tokens":39901,"cache_read_input_tokens":0,"output_tokens":1}}}""");

        var usage = monitor.SnapshotUsage();
        // Summed once, not twice: the second line's identical usage is deduped by shared message.id.
        // #1706: 39,901 rather than 2 + 1 + 39,901 -- the input_tokens 2 and output_tokens 1 visible in
        // both real lines above are the placeholders this issue measured, no longer read.
        Assert.Equal(39901, usage.BilledTokens);
        Assert.Null(usage.TokensOut);
        Assert.True(usage.BilledIsFloor);
    }

    [Fact]
    public void A_claude_usage_line_with_no_message_id_always_accumulates()
    {
        // A repeated-but-absent id must never be treated as "already seen" -- only an actual repeated
        // string dedupes.
        var monitor = new TokenBudgetMonitor(budget: 1_000_000, maxToolSteps: null, billedRateLimit: null, new ClaudeUsageParser());

        monitor.OnStdoutLine("""{"type":"assistant","message":{"usage":{"input_tokens":2,"cache_creation_input_tokens":10,"cache_read_input_tokens":0,"output_tokens":3}}}""");
        monitor.OnStdoutLine("""{"type":"assistant","message":{"usage":{"input_tokens":2,"cache_creation_input_tokens":10,"cache_read_input_tokens":0,"output_tokens":3}}}""");

        Assert.Equal(20, monitor.SnapshotUsage().BilledTokens);
    }

    [Fact]
    public void A_claude_usage_object_carrying_only_the_placeholder_columns_yields_no_reading_at_all()
    {
        // #1706, the deliberate consequence stated in ClaudeUsageParser.TryParseIncrementalUsage's own
        // doc: with input_tokens/output_tokens no longer read, a usage object carrying nothing else has
        // no figure left to report, and reporting a WorkerUsage of all nulls would put a fabricated 0
        // into the running Σ. That doc also records why this shape is unreachable on measured traffic;
        // this is pinned so the choice stays a decision rather than an accident.
        var monitor = new TokenBudgetMonitor(budget: 1_000_000, maxToolSteps: null, billedRateLimit: null, new ClaudeUsageParser());

        monitor.OnStdoutLine("""{"type":"assistant","message":{"usage":{"input_tokens":2,"output_tokens":3}}}""");

        var usage = monitor.SnapshotUsage();
        Assert.Null(usage.BilledTokens);
        Assert.Null(usage.ContextLevelTokens);
        Assert.False(usage.BilledIsFloor);
    }

    [Fact]
    public void The_tool_step_cap_fires_at_cap_plus_one_with_zero_usage_lines()
    {
        // Must 6c: independent of usage parsing entirely -- every line here fails TryParseIncrementalUsage.
        var monitor = new TokenBudgetMonitor(budget: null, maxToolSteps: 5, billedRateLimit: null, new ClaudeUsageParser());

        for (var i = 0; i < 5; i++)
        {
            monitor.OnStdoutLine(
                """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash","input":{}}]}}""");
            Assert.False(monitor.Arrested);
        }

        monitor.OnStdoutLine(
            """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash","input":{}}]}}""");

        Assert.True(monitor.Arrested);
        Assert.Equal(ArrestReason.ToolStepCap, monitor.ArrestReasonValue);
        Assert.Equal(6, monitor.SnapshotToolStepCount());
    }

    [Fact]
    public void A_multi_tool_claude_turn_counts_every_block_toward_the_cap()
    {
        var monitor = new TokenBudgetMonitor(budget: null, maxToolSteps: 1, billedRateLimit: null, new ClaudeUsageParser());

        monitor.OnStdoutLine(
            """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash"},{"type":"tool_use","name":"Read"}]}}""");

        Assert.True(monitor.Arrested);
        Assert.Equal(2, monitor.SnapshotToolStepCount());
    }

    [Fact]
    public void A_monitor_with_only_a_tool_step_cap_and_no_budget_still_watches()
    {
        // #1682: before this issue a monitor required a budget to be constructed at all. Terminal-state
        // lines per #1686 review F2's fixed unit.
        var monitor = new TokenBudgetMonitor(budget: null, maxToolSteps: 2, billedRateLimit: null, new AgyUsageParser());

        monitor.OnStdoutLine("""{"event":"step_update","step_update":{"state":"DONE","step_type":"tool","tool_name":"a"}}""");
        monitor.OnStdoutLine("""{"event":"step_update","step_update":{"state":"DONE","step_type":"tool","tool_name":"b"}}""");
        Assert.False(monitor.Arrested);
        monitor.OnStdoutLine("""{"event":"step_update","step_update":{"state":"DONE","step_type":"tool","tool_name":"c"}}""");
        Assert.True(monitor.Arrested);
    }

    [Fact]
    public void A_monitor_with_neither_budget_nor_cap_never_arrests()
    {
        var monitor = new TokenBudgetMonitor(budget: null, maxToolSteps: null, billedRateLimit: null, new ClaudeUsageParser());

        for (var i = 0; i < 1000; i++)
        {
            monitor.OnStdoutLine(
                """{"type":"assistant","message":{"usage":{"input_tokens":100000,"output_tokens":100000}}}""");
        }

        Assert.False(monitor.Arrested);
    }

    [Fact]
    public void Whichever_trigger_fires_first_wins_and_the_other_never_overwrites_it()
    {
        // Tool-step cap set low enough to fire before the budget, on a stream that would also cross
        // the budget eventually -- the reason recorded must be the one that actually fired first.
        var monitor = new TokenBudgetMonitor(budget: 100, maxToolSteps: 1, billedRateLimit: null, new AgyUsageParser());

        monitor.OnStdoutLine(
            """{"event":"step_update","step_update":{"state":"DONE","step_type":"tool","tool_name":"a"}}""");
        monitor.OnStdoutLine(
            """{"event":"step_update","step_update":{"state":"DONE","step_type":"tool","tool_name":"b"}}""");

        Assert.True(monitor.Arrested);
        Assert.Equal(ArrestReason.ToolStepCap, monitor.ArrestReasonValue);

        // Further lines, including ones that would cross the budget, never flip the recorded reason.
        // #1686 review F6: needs "step_type":"agent_response" -- without it, F4's own gate
        // (StandardWorkerUsageParsers.cs's TryParseIncrementalUsage) rejects the line outright, so
        // this arm would keep passing even if the already-arrested guard below it were deleted.
        monitor.OnStdoutLine(
            """{"event":"step_update","step_update":{"state":"DONE","step_type":"agent_response","usage":{"input_tokens":1000,"output_tokens":1000}}}""");
        Assert.Equal(ArrestReason.ToolStepCap, monitor.ArrestReasonValue);
    }

    [Fact]
    public void Lines_that_do_not_parse_as_usage_are_ignored()
    {
        var monitor = new TokenBudgetMonitor(budget: 10, maxToolSteps: null, billedRateLimit: null, new ClaudeUsageParser());

        monitor.OnStdoutLine("not json");
        monitor.OnStdoutLine("""{"type":"system","subtype":"init"}""");

        Assert.False(monitor.Arrested);
        Assert.Null(monitor.SnapshotUsage().TokensIn);
        // #1686 review F5: a stream with no usage line at all must not report a measured-zero context
        // level -- ContextLevelTokens now follows the same never-fabricated convention as BilledTokens
        // and CacheReadTokens below.
        Assert.Null(monitor.SnapshotUsage().ContextLevelTokens);
        Assert.Null(monitor.SnapshotUsage().BilledTokens);
        // #1686 review F7: a stream with no usage line at all must not report a measured-zero cache
        // read -- CacheReadTokens now follows the same never-fabricated convention as BilledTokens
        // above (TokenBudgetMonitor's own OnStdoutLine comment has the rule).
        Assert.Null(monitor.SnapshotUsage().CacheReadTokens);
    }

    [Fact]
    public void SnapshotLastToolNames_keeps_only_the_most_recent_names_bounded()
    {
        var monitor = new TokenBudgetMonitor(budget: 1_000_000, maxToolSteps: null, billedRateLimit: null, new AgyUsageParser());

        for (var i = 0; i < 15; i++)
        {
            monitor.OnStdoutLine(
                "{\"event\":\"step_update\",\"step_update\":{\"state\":\"ACTIVE\",\"step_type\":\"tool\",\"tool_name\":\"tool-" + i + "\"}}");
        }

        var names = monitor.SnapshotLastToolNames();

        Assert.Equal(10, names.Count);
        Assert.Equal("tool-14", names[^1]);
        Assert.Equal("tool-5", names[0]);
    }

    [Fact]
    public void The_terminal_result_line_is_never_double_counted_as_incremental_usage()
    {
        // Polarity: TryParseIncrementalUsage deliberately returns false for a "type":"result" line
        // (StandardWorkerUsageParsersTests pins the parser directly); this pins the monitor honours
        // that and never sums the terminal figure a second time.
        var monitor = new TokenBudgetMonitor(budget: 10, maxToolSteps: null, billedRateLimit: null, new ClaudeUsageParser());

        monitor.OnStdoutLine("""{"type":"result","usage":{"input_tokens":100,"output_tokens":100}}""");

        Assert.False(monitor.Arrested);
        Assert.Null(monitor.SnapshotUsage().TokensIn);
        // #1686 review F5: no incremental usage line ever parsed on this stream, so ContextLevelTokens
        // stays null rather than a fabricated 0 -- same convention as the never-double-counted assertion
        // above.
        Assert.Null(monitor.SnapshotUsage().ContextLevelTokens);
    }

    /// <summary>
    /// #1691: one agy usage line per second, 1,000 billed each. Inside the 5-minute window that is at
    /// most 300,000 no matter how long the stream runs, so a limit of 400,000 must NEVER fire even
    /// though the running total passes it — the discriminating difference between this trigger and the
    /// token budget, in one test.
    /// </summary>
    [Fact]
    public void A_billed_rate_limit_above_the_windows_capacity_never_fires_however_long_the_stream_runs()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var monitor = new TokenBudgetMonitor(
            budget: null, maxToolSteps: null, billedRateLimit: 400_000, new AgyUsageParser(), clock);

        for (var i = 0; i < 900; i++)
        {
            monitor.OnStdoutLine(AgyBilledLine(1_000));
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.False(monitor.Arrested);
        // 900,000 billed in total -- more than double the limit, and still no arrest, because the
        // WINDOW never held more than 301 samples. 301 rather than 300 because the window is closed at
        // both ends: eviction drops a sample only once it is STRICTLY older than the window, so one
        // sitting exactly on the 5-minute edge still counts. Pinned rather than rounded so the boundary
        // rule is checkable instead of inferred.
        Assert.Equal(900_000, monitor.SnapshotUsage().BilledTokens);
        Assert.Equal(301_000, monitor.SnapshotPeakBilledInWindow());
    }

    /// <summary>
    /// #1691, the opposite polarity of the test above over the identical stream shape: the same 1,000
    /// billed per line, delivered fast enough that the window fills. Fires on the 250th line, the first
    /// at which the trailing window holds 250,000.
    /// </summary>
    [Fact]
    public void A_billed_rate_limit_the_window_can_reach_fires_on_the_line_that_reaches_it()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var monitor = new TokenBudgetMonitor(
            budget: null, maxToolSteps: null, billedRateLimit: 250_000, new AgyUsageParser(), clock);

        var arrestedAtLine = -1;
        for (var i = 0; i < 900; i++)
        {
            monitor.OnStdoutLine(AgyBilledLine(1_000));
            if (monitor.Arrested && arrestedAtLine == -1)
            {
                arrestedAtLine = i;
            }

            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.True(monitor.Arrested);
        Assert.Equal(ArrestReason.BilledRate, monitor.ArrestReasonValue);
        Assert.Equal(249, arrestedAtLine);
    }

    /// <summary>
    /// #1691: eviction is by the window's own edge, not by a sample count. Two bursts of 200,000
    /// separated by more than the window never coexist in it, so a 250,000 limit that either burst
    /// alone cannot reach is never reached by their sum either.
    /// </summary>
    [Fact]
    public void Samples_that_have_fallen_out_of_the_window_no_longer_count_toward_the_limit()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var monitor = new TokenBudgetMonitor(
            budget: null, maxToolSteps: null, billedRateLimit: 250_000, new AgyUsageParser(), clock);

        monitor.OnStdoutLine(AgyBilledLine(200_000));
        clock.Advance(TokenBudgetMonitor.BilledRateWindow + TimeSpan.FromSeconds(1));
        monitor.OnStdoutLine(AgyBilledLine(200_000));

        Assert.False(monitor.Arrested);
        Assert.Equal(400_000, monitor.SnapshotUsage().BilledTokens);
        Assert.Equal(200_000, monitor.SnapshotPeakBilledInWindow());
    }

    /// <summary>
    /// #1691: the trigger ordering, pinned. A stream crossing the budget and the rate limit on the very
    /// same line records <see cref="ArrestReason.TokenBudget"/> — the pre-existing reason wins.
    /// spec/baton.md §3 states why that ordering, and not the other one.
    /// </summary>
    [Fact]
    public void The_token_budget_wins_when_a_single_line_crosses_both_it_and_the_rate_limit()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var monitor = new TokenBudgetMonitor(
            budget: 100_000, maxToolSteps: null, billedRateLimit: 100_000, new AgyUsageParser(), clock);

        monitor.OnStdoutLine(AgyBilledLine(100_000));

        Assert.True(monitor.Arrested);
        Assert.Equal(ArrestReason.TokenBudget, monitor.ArrestReasonValue);
    }

    /// <summary>
    /// #1691: with no limit armed — which is EVERY shipped role — the observed windowed peak is still
    /// accumulated, because that measurement is what spec/baton.md §3's blocked calibration needs and
    /// it costs nothing to keep.
    /// </summary>
    [Fact]
    public void The_observed_windowed_peak_is_measured_even_when_no_rate_limit_is_armed()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var monitor = new TokenBudgetMonitor(
            budget: null, maxToolSteps: null, billedRateLimit: null, new AgyUsageParser(), clock);

        monitor.OnStdoutLine(AgyBilledLine(70_000));
        clock.Advance(TimeSpan.FromMinutes(1));
        monitor.OnStdoutLine(AgyBilledLine(30_000));

        Assert.False(monitor.Arrested);
        Assert.Equal(100_000, monitor.SnapshotPeakBilledInWindow());
    }

    /// <summary>
    /// #1709 review: a monitor that never sees a usage-bearing line must report null, not the
    /// fabricated 0 a freshly-constructed <c>long</c> field would default to -- the same
    /// never-fabricate-a-zero convention <see cref="SnapshotUsage"/>'s own fields already follow.
    /// </summary>
    [Fact]
    public void A_monitor_that_never_admits_a_sample_reports_a_null_peak_not_a_fabricated_zero()
    {
        var monitor = new TokenBudgetMonitor(budget: null, maxToolSteps: null, billedRateLimit: null, new AgyUsageParser());

        // A tool-only line: parses, but carries no usage at all, so AdmitRateSample never runs.
        monitor.OnStdoutLine("""{"event":"tool_call","tool_call":{"name":"Read"}}""");

        Assert.Null(monitor.SnapshotPeakBilledInWindow());
    }

    /// <summary>
    /// #1709 review, opposite polarity of the test above: once a sample IS admitted, a genuine
    /// measured peak of 0 billed tokens is reported as 0, not null -- null means "never watched
    /// anything", not "watched and saw nothing to bill".
    /// </summary>
    [Fact]
    public void A_monitor_that_admits_a_zero_billed_sample_reports_a_measured_zero_peak()
    {
        var monitor = new TokenBudgetMonitor(budget: null, maxToolSteps: null, billedRateLimit: null, new AgyUsageParser());

        monitor.OnStdoutLine(AgyBilledLine(0));

        Assert.Equal(0, monitor.SnapshotPeakBilledInWindow());
    }

    /// <summary>One agy per-turn usage line billing exactly <paramref name="billed"/> (input only).</summary>
    private static string AgyBilledLine(long billed) =>
        "{\"event\":\"step_update\",\"step_update\":{\"state\":\"DONE\",\"step_type\":\"agent_response\",\"usage\":{"
        + "\"input_tokens\":" + billed + ",\"output_tokens\":0}}}";

    /// <summary>
    /// #1557: the daemon feeds a monitor incremental batches read off disk every ~30s tick, never the
    /// whole stream at once the way every OTHER test in this file does. This is the plan's own named
    /// order-of-batching risk -- <c>_apply_live_delta</c>'s pusher-side design already assumes
    /// accumulation is batch-shape-independent, unverified for this type under the daemon's new call
    /// pattern. Replays the real captured agy stream <c>CONTROL_an_agy_stream_reconciles_to_a_ZERO_under_read</c>
    /// (<c>ExecutionUsageProjectorTests.cs</c>) already pins the terminal totals for, split into
    /// differently-sized chunks, and asserts the two monitors converge on an identical snapshot.
    /// </summary>
    [Fact]
    public void SnapshotUsage_is_identical_whether_a_real_captured_stream_arrives_in_one_batch_or_many_small_ones()
    {
        var lines = File.ReadAllLines(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "agy-38c24d11-agent-response-usage.jsonl"));
        Assert.True(lines.Length > 10, "fixture must carry enough lines to make chunking meaningful");

        var oneShot = new TokenBudgetMonitor(budget: null, maxToolSteps: null, billedRateLimit: null, new AgyUsageParser());
        foreach (var line in lines)
        {
            oneShot.OnStdoutLine(line);
        }

        var incremental = new TokenBudgetMonitor(budget: null, maxToolSteps: null, billedRateLimit: null, new AgyUsageParser());
        const int chunkSize = 3;
        for (var i = 0; i < lines.Length; i += chunkSize)
        {
            foreach (var line in lines.Skip(i).Take(chunkSize))
            {
                incremental.OnStdoutLine(line);
            }
        }

        var oneShotUsage = oneShot.SnapshotUsage();
        var incrementalUsage = incremental.SnapshotUsage();

        Assert.Equal(oneShotUsage.BilledTokens, incrementalUsage.BilledTokens);
        Assert.Equal(oneShotUsage.Turns, incrementalUsage.Turns);
        Assert.Equal(oneShotUsage.TokensOut, incrementalUsage.TokensOut);
        Assert.Equal(oneShotUsage.CacheReadTokens, incrementalUsage.CacheReadTokens);
        Assert.Equal(oneShotUsage.BilledIsFloor, incrementalUsage.BilledIsFloor);
        Assert.Equal(oneShot.SnapshotToolStepCount(), incremental.SnapshotToolStepCount());
        Assert.NotNull(oneShotUsage.BilledTokens);
    }

    /// <summary>
    /// #2020 review MEDIUM, and the behaviour half of that change rather than the reporting half: a
    /// codex lane now arrests MID-turn, at the round-trip that crosses the budget, where before the
    /// per-round-trip emitter its only reading was the terminal <c>turn.completed</c> and the arrest
    /// could not fire until the turn was already over. Decided deliberately (see
    /// <see cref="TokenBudgetMonitor"/>'s own remark and the PR body): claude and agy already arrest
    /// mid-turn on this rung, and codex was the outlier by accident of emission.
    /// <para>
    /// Figures: codex's <c>TokensIn</c> is the non-cached remainder, so each round-trip bills
    /// (1000 − 0) + 100 output + 0 cache-write = 1100. Σ crosses a 2000 budget at round-trip 2 of 3,
    /// and both polarities are asserted — unarrested after 1, arrested after 2. Distinct
    /// <c>round_trip</c> values matter: they land on <see cref="WorkerUsage.MessageId"/> and a repeat
    /// is dropped by the dedup rule above.
    /// </para>
    /// </summary>
    [Fact]
    public void A_codex_stream_crossing_the_budget_at_round_trip_2_of_3_arrests_at_round_trip_2()
    {
        var monitor = new TokenBudgetMonitor(budget: 2000, maxToolSteps: null, billedRateLimit: null, new CodexUsageParser());

        monitor.OnStdoutLine(UsageLine(roundTrip: 1));
        Assert.False(monitor.Arrested);
        Assert.False(monitor.ArrestRequested.IsCancellationRequested);

        monitor.OnStdoutLine(UsageLine(roundTrip: 2));

        Assert.True(monitor.Arrested);
        Assert.True(monitor.ArrestRequested.IsCancellationRequested);
        Assert.Equal(ArrestReason.TokenBudget, monitor.ArrestReasonValue);
        Assert.Equal(2200, monitor.SnapshotUsage().BilledTokens);

        // The third round-trip never happens on a real arrested lane; feeding it here pins that the
        // arrest is latched at 2 rather than merely reported at whatever the last line was.
        monitor.OnStdoutLine(UsageLine(roundTrip: 3));
        Assert.Equal(ArrestReason.TokenBudget, monitor.ArrestReasonValue);

        // Substituted from the constants rather than typed out, so a rename of either cannot leave this
        // arm passing against a shape the broker no longer writes.
        static string UsageLine(int roundTrip) =>
            """{"type":"USAGE_TYPE","usage":{"input_tokens":1000,"cached_input_tokens":0,"cache_write_input_tokens":0,"output_tokens":100,"reasoning_output_tokens":0,"ROUND_TRIP_FIELD":RT}}"""
                .Replace("USAGE_TYPE", CodexUsageParser.TurnUsageEventType, StringComparison.Ordinal)
                .Replace("ROUND_TRIP_FIELD", CodexUsageParser.RoundTripField, StringComparison.Ordinal)
                .Replace("RT", roundTrip.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }
}
