using Baton.Domain;
using Baton.Mutation;
using Baton.Status;
using Xunit;

namespace Baton.Tests.Mutation;

/// <summary>
/// #1682 acceptance: "A replay of room `dispatch-implement-f7b24a80`'s stream (or a fixture shaped
/// like it) arrests before 600k billed." This fixture is shaped like `dispatch-implement-38c24d11`
/// instead (the OTHER #1682 evidence room) — its 70 (input_tokens, output_tokens) pairs copied
/// verbatim, per usage line, from that room's own real `.stdout.log`
/// (`dispatch-implement-38c24d11/artifacts/execution_a6de5637dbae488581ad3848729a0c1b/.stdout.log`),
/// in the same order they were emitted. Regenerating the fixture: filter that file's lines for
/// `"event":"step_update"` with `step_update.state == "DONE"` and read `step_update.usage.input_tokens`/
/// `output_tokens` off each.
/// </summary>
public sealed class TokenBudgetReplayTests
{
    /// <summary>(input_tokens, output_tokens) per usage line, room order.</summary>
    private static readonly (long In, long Out)[] Room38c24d11Turns =
    [
        (14205, 443), (16203, 1128), (17941, 180), (2994, 436), (36793, 116), (3212, 102), (3399, 100),
        (3583, 110), (7147, 135), (3308, 120), (11862, 104), (9438, 134), (5573, 108), (5765, 143),
        (5993, 167), (5302, 120), (5252, 97), (5453, 92), (5640, 78), (2842, 91), (12250, 1631),
        (5660, 138), (2847, 147), (8337, 134), (3470, 130), (7148, 28286), (31505, 973), (4092, 21413),
        (21613, 376), (5858, 284), (2158, 1072), (3407, 23509), (2443, 359), (2897, 498), (3619, 611),
        (4336, 22864), (23163, 182), (3013, 369), (3548, 336), (4112, 103), (4408, 177), (4811, 109),
        (5306, 550), (5952, 7330), (9276, 3681), (4874, 10766), (11627, 3347), (6891, 7359), (10244, 535),
        (2704, 248), (3352, 708), (4155, 7353), (7503, 169), (3683, 573), (4564, 7611), (96546, 3679),
        (6401, 10551), (12936, 655), (2049, 7612), (9739, 3655), (5315, 10281), (11584, 173), (3684, 459),
        (4252, 562), (4920, 235), (5613, 872), (2648, 278), (3239, 1455), (4863, 100), (5164, 754),
    ];

    // #1686 review F3: this was named `OldAndNewDefaultBudget` and its comment claimed 600,000 was
    // "still-in-force" -- both false as of that diff. 600,000 is the SUPERSEDED pre-#1682 figure the
    // original #1682 acceptance criterion asked a replay to arrest under, kept only so that criterion
    // still has a runnable test. That #2034 later re-pinned claude/codex `implement` to the same number
    // (`ShippedClaudeImplementBudget` below) is a coincidence of two different derivations -- the old
    // level-based default versus 3x the live billed p95 -- not a return to this one.
    private const long SupersededPre1682AcceptanceBudget = 600_000;

    // #1686 review F3: the `implement` budget #1682 shipped as a single scalar
    // (`src/Baton.Vendors/WorkerRoles.json`) -- what the "honest replay result" tests below were
    // measured against. #2034 moved claude and codex off it; it is still agy's figure today, and the
    // agy replays below (room 38c24d11) are therefore still replays at the shipped agy ceiling.
    private const long Pre2034ImplementBudget = 1_200_000;
    private const int ShippedImplementMaxToolSteps = 322;

    // #2034: `implement`'s claude ceiling today -- 3x the rolling live p95 (201,858 on 2026-09-08),
    // floored at 400,000; the derivation is spec/baton.md §3's "Ceiling rule (#2034)" paragraph.
    private const long ShippedClaudeImplementBudget = 600_000;

    // #1686 review F1: the OLD (pre-#1682, i.e. pre-THIS-PR) `implement` tool-step cap, in the OLD
    // double-counted unit (ACTIVE + terminal lifecycle line per real call) -- kept only for the
    // discriminating control below, which proves the F2 unit fix is what changed the replay's outcome,
    // not an unrelated change.
    private const int PreF2ImplementMaxToolSteps = 80;

    private static string AgyDoneLine(long inputTokens, long outputTokens) =>
        "{\"event\":\"step_update\",\"step_update\":{\"state\":\"DONE\",\"step_type\":\"agent_response\",\"usage\":{"
        + "\"input_tokens\":" + inputTokens + ",\"output_tokens\":" + outputTokens
        + ",\"total_tokens\":" + (inputTokens + outputTokens) + "}}}";

    private static string AgyToolLine(string state) =>
        "{\"event\":\"step_update\",\"step_update\":{\"state\":\"" + state + "\",\"step_type\":\"tool\",\"tool_name\":\"run_command\"}}";

    /// <summary>
    /// #1686 review F3: the REAL interleaved tool-step lines from room `38c24d11`'s own
    /// `.stdout.log`, positioned relative to the 70 usage lines above -- extracted by filtering the
    /// same capture for every `step_update` line (usage OR tool) in emitted order and reading off the
    /// shape: this room's stream is maximally regular -- each of the 70 turns is exactly
    /// [usage DONE/agent_response line, ACTIVE tool line, terminal (DONE) tool line], back to back,
    /// with NO trailing tool pair after the 70th (final) usage line, because the room was cancelled
    /// before that turn's own tool call reached ACTIVE. Regenerating: filter the room's `.stdout.log`
    /// for every `"event":"step_update"` line in order and classify each by
    /// `step_update.step_type`/`step_update.state` the same way <see cref="TokenBudgetReplayTests"/>'s
    /// class doc already documents regenerating the usage-only fixture above.
    /// #1686 review F8: unlike <see cref="Room38c24d11Turns"/>, which is copied verbatim per line, the
    /// tool lines this method emits (<see cref="AgyToolLine"/>) are RECONSTRUCTED from the counted
    /// [usage, ACTIVE, DONE] shape above, not copied literal lines -- the room's real tool_name and
    /// exact JSON are not reproduced, only the state/step_type/count regularity that governs what this
    /// monitor counts.
    /// </summary>
    /// <param name="monitor">Fed one real interleaved line at a time, in the room's own emitted order.</param>
    /// <returns>The 0-based usage-line index (matching <see cref="Room38c24d11Turns"/>) at which the monitor first arrested, or -1 if it never did.</returns>
    private static int ReplayRoom38c24d11(TokenBudgetMonitor monitor)
    {
        var arrestedAtLine = -1;
        for (var i = 0; i < Room38c24d11Turns.Length; i++)
        {
            var (inTokens, outTokens) = Room38c24d11Turns[i];
            monitor.OnStdoutLine(AgyDoneLine(inTokens, outTokens));
            if (monitor.Arrested && arrestedAtLine == -1)
            {
                arrestedAtLine = i;
            }

            if (i == Room38c24d11Turns.Length - 1)
            {
                // The room's capture ends right here -- no tool pair follows the final usage line.
                break;
            }

            monitor.OnStdoutLine(AgyToolLine("ACTIVE"));
            if (monitor.Arrested && arrestedAtLine == -1)
            {
                arrestedAtLine = i;
            }

            monitor.OnStdoutLine(AgyToolLine("DONE"));
            if (monitor.Arrested && arrestedAtLine == -1)
            {
                arrestedAtLine = i;
            }
        }

        return arrestedAtLine;
    }

    [Fact]
    public void GREEN_the_room_38c24d11_shaped_replay_arrests_before_600k_billed_under_the_new_reading()
    {
        // The original #1682 acceptance criterion, still runnable against the superseded budget --
        // NOT the shipped configuration (see the honest-replay tests below for that).
        var monitor = new TokenBudgetMonitor(budget: SupersededPre1682AcceptanceBudget, maxToolSteps: null, billedRateLimit: null, new AgyUsageParser());

        var arrestedAtLine = -1;
        for (var i = 0; i < Room38c24d11Turns.Length; i++)
        {
            var (inTokens, outTokens) = Room38c24d11Turns[i];
            monitor.OnStdoutLine(AgyDoneLine(inTokens, outTokens));
            if (monitor.Arrested && arrestedAtLine == -1)
            {
                arrestedAtLine = i;
            }
        }

        Assert.True(monitor.Arrested);
        Assert.Equal(ArrestReason.TokenBudget, monitor.ArrestReasonValue);
        // Measured: billed crosses 600,000 on usage line 56 (0-indexed 55) of this room's real stream --
        // long before the room's own eventual total of 794,940.
        Assert.Equal(55, arrestedAtLine);
        Assert.True(monitor.SnapshotUsage().BilledTokens >= SupersededPre1682AcceptanceBudget);
    }

    [Fact]
    public void HONEST_the_shipped_implement_configuration_does_NOT_arrest_the_real_room_38c24d11_replay()
    {
        // #1686 review F3 / "Replay verdict": the review's own bar is that the SHIPPED configuration
        // must be proven to arrest the real runaway room. Measured here rather than assumed: it does
        // NOT. Room 38c24d11's real capture makes only 69 real tool calls in its entire 70-turn, 794,940
        // -billed-token stream -- far under a tool-step cap wide enough to avoid false-arresting normal
        // `implement` traffic (322, and even that is exceeded by 2 of 26 real normal rooms measured for
        // this PR -- spec/baton.md §3). And 794,940 sits under the recalibrated 1,200,000 token budget
        // (still agy's ceiling after #2034; this is an agy room) by the SAME sound "2x normal" method. Neither trigger fires. This is not a bug in this
        // replay -- it is the honest result of fixing F1/F2 correctly, and it is recorded here as a
        // durable fact rather than left to only exist in a PR body that will drift out of sync with the
        // code the moment either constant changes again.
        var monitor = new TokenBudgetMonitor(budget: Pre2034ImplementBudget, maxToolSteps: ShippedImplementMaxToolSteps, billedRateLimit: null, new AgyUsageParser());

        var arrestedAtLine = ReplayRoom38c24d11(monitor);

        Assert.False(monitor.Arrested);
        Assert.Equal(-1, arrestedAtLine);
        Assert.Null(monitor.ArrestReasonValue);
        Assert.Equal(794_940, monitor.SnapshotUsage().BilledTokens);
        Assert.Equal(69, monitor.SnapshotToolStepCount());
    }

    [Fact]
    public void HONEST_the_token_trigger_alone_does_NOT_fire_on_room_38c24d11_at_the_shipped_1_2M_budget()
    {
        // #1686 review F3's second, independently satisfiable assertion: isolated from the tool-step
        // axis entirely (maxToolSteps: null), the token trigger alone never crosses 1,200,000 on this
        // room's real 794,940-token total.
        var monitor = new TokenBudgetMonitor(budget: Pre2034ImplementBudget, maxToolSteps: null, billedRateLimit: null, new AgyUsageParser());

        foreach (var (inTokens, outTokens) in Room38c24d11Turns)
        {
            monitor.OnStdoutLine(AgyDoneLine(inTokens, outTokens));
        }

        Assert.False(monitor.Arrested);
        Assert.Equal(794_940, monitor.SnapshotUsage().BilledTokens);
        Assert.True(monitor.SnapshotUsage().BilledTokens < Pre2034ImplementBudget);
    }

    [Fact]
    public void DISCRIMINATING_the_real_monitor_at_the_old_cap_value_does_NOT_arrest_under_the_fixed_unit()
    {
        // #1686 review F8: the cheap, genuinely discriminating half the review's own DISCRIMINATING_
        // test above was missing -- that one reimplements the old unit inline and never touches
        // AgyUsageParser/TokenBudgetMonitor at all, so it would pass even if both classes were deleted.
        // This runs the REAL monitor (real AgyUsageParser.CountToolSteps, real TokenBudgetMonitor) over
        // the real interleaved replay at the OLD cap value (80), isolated from the budget axis. Under
        // the fixed unit the room made only 69 real calls, so even the pre-F2 cap number does not fire --
        // proving the unit fix, not the recalibrated cap, is what changed this replay's outcome.
        var monitor = new TokenBudgetMonitor(budget: null, maxToolSteps: PreF2ImplementMaxToolSteps, billedRateLimit: null, new AgyUsageParser());

        var arrestedAtLine = ReplayRoom38c24d11(monitor);

        Assert.False(monitor.Arrested);
        Assert.Equal(-1, arrestedAtLine);
        Assert.Equal(69, monitor.SnapshotToolStepCount());
    }

    [Fact]
    public void DISCRIMINATING_the_pre_F2_double_counted_unit_WOULD_have_arrested_this_replay_the_fixed_unit_does_not()
    {
        // #1686 review F1/F2/F3, a control so the F2 tradeoff is checkable in the suite rather than only
        // in prose: same real interleaved replay, but counting BOTH the ACTIVE and terminal lifecycle
        // line per real tool call (the OLD unit AgyUsageParser.CountToolSteps used before this PR)
        // against the OLD cap of 80 in that unit. This DOES arrest -- the old cap's catch on this room
        // was genuine, but only because of the double count, not because the underlying real-call rate
        // was actually high (spec/baton.md §3 has the retraction this proves).
        var oldUnitToolStepCount = 0;
        var arrestedAtLine = -1;
        var billedTokens = 0L;
        var billedAtArrest = -1L;
        var arrestedOnToolCap = false;

        for (var i = 0; i < Room38c24d11Turns.Length; i++)
        {
            var (inTokens, outTokens) = Room38c24d11Turns[i];
            billedTokens += inTokens + outTokens;

            if (!arrestedOnToolCap && i == Room38c24d11Turns.Length - 1)
            {
                break;
            }

            oldUnitToolStepCount += 2; // ACTIVE + terminal, the pre-#1686-F2 unit.
            if (!arrestedOnToolCap && oldUnitToolStepCount > PreF2ImplementMaxToolSteps)
            {
                arrestedOnToolCap = true;
                arrestedAtLine = i;
                billedAtArrest = billedTokens;
            }
        }

        Assert.True(arrestedOnToolCap);
        Assert.Equal(40, arrestedAtLine); // cumulative old-unit count crosses 81 mid-turn 41 (0-indexed 40).
        // spec/baton.md §3's own prior calibration figure, corroborated independently by the #1686
        // review's own arithmetic: cumulative billed at usage line 41 (index 40) is 439,385 -- well
        // under the room's own eventual 794,940, and under even the OLD 600,000 ceiling.
        Assert.Equal(439_385, billedAtArrest);
    }

    [Fact]
    public void RED_the_same_replay_does_NOT_arrest_at_any_point_under_the_pre_1682_level_based_reading()
    {
        // #1682's own root cause, reproduced directly: the pre-#1682 TokenBudgetMonitor tracked
        // (LEVEL of input+cache_read+cache_creation, REPLACED every turn) + (SUM of output), checked
        // for a crossing after EVERY line -- never SUM of input. Replaying the identical 70-turn stream
        // against that formula, turn by turn, must never cross 600,000 at ANY point, which is exactly
        // what let this real evidence room run away unarrested. TokenBudgetMonitor itself no longer
        // implements this formula (that IS the fix) -- reproduced inline here, turn by turn, as the red
        // control, since the class this replaces no longer exists in the tree to run directly.
        long sumOutput = 0;
        long maxTrackedAtAnyTurn = 0;
        foreach (var (inTokens, outTokens) in Room38c24d11Turns)
        {
            sumOutput += outTokens;
            var level = inTokens; // #1623: the input side was a LEVEL -- each new reading REPLACES it, never adds.
            var trackedThisTurn = level + sumOutput;
            maxTrackedAtAnyTurn = Math.Max(maxTrackedAtAnyTurn, trackedThisTurn);

            Assert.True(trackedThisTurn < SupersededPre1682AcceptanceBudget,
                $"pre-#1682 formula crossed the budget mid-replay at tracked={trackedThisTurn} -- the bug this fixture exists to demonstrate did not reproduce.");
        }

        // Pins the measured peak so a change to the fixture data is caught here too, not just by the
        // per-turn assertion above never firing.
        Assert.Equal(258_160, maxTrackedAtAnyTurn);
    }

    // ---------------------------------------------------------------------------------------------
    // #1706: the claude side. Everything above replays agy, whose incremental usage IS its real usage.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Per distinct <c>message.id</c>, in emitted order, off room <c>dispatch-implement-3dc5e21a</c>'s
    /// real <c>.stdout.log</c>
    /// (<c>artifacts/execution_b3cdfeb7684f459a9af0baca24c6e1c3/.stdout.log</c>):
    /// <c>(output_tokens, cache_creation_input_tokens)</c>. 153 distinct ids over 246 usage-bearing
    /// <c>"type":"assistant"</c> lines; 37 of the 153 are subagent messages (non-null
    /// <c>parent_tool_use_id</c>) and are deliberately included, since the vendor bills them.
    /// <c>input_tokens</c> is not a column because it is the literal constant 2 on all 153 — that
    /// invariant is asserted by <see cref="ClaudePlaceholderInputTokens"/>'s use below rather than
    /// carried 153 times. The 93 repeat lines are not reproduced either: measured over this capture,
    /// 0 of the 93 carry a <c>usage</c> object differing from their id's first sighting, so replaying
    /// one line per id is arithmetically identical (the dedupe path itself keeps its own real-pair
    /// control in <c>TokenBudgetMonitorTests</c>).
    /// Regenerating: filter the capture for <c>"type":"assistant"</c> lines carrying
    /// <c>message.usage</c>, keep the first occurrence of each <c>message.id</c>, and read those two
    /// fields off each.
    /// </summary>
    private static readonly (long Out, long CacheCreation)[] Room3dc5e21aMessages =
    [
        (1, 39901), (1, 1889), (3, 5607), (5, 4979), (5, 16287), (3, 11004), (3, 2694), (4, 5788), (3, 3327), (5,
        3394), (4, 1680), (3, 3453), (4, 5939), (3, 4581), (2, 2462), (2, 1897), (4, 3763), (16, 347), (4, 2171),
        (17, 710), (16, 603), (3, 942), (16, 736), (3, 499), (4, 720), (17, 1286), (17, 1030), (6, 672), (3, 547),
        (17, 380), (6, 620), (3, 3640), (3, 1339), (17, 976), (17, 857), (17, 735), (2, 823), (4, 1129), (17, 965),
        (4, 2843), (3, 1008), (3, 1370), (2, 636), (17, 647), (3, 1838), (17, 430), (17, 582), (3, 1866), (16, 804),
        (20, 848), (3, 596), (2, 1214), (9, 789), (4, 4908), (1, 732), (3, 598), (17, 1406), (17, 883), (17, 554),
        (2, 968), (16, 388), (17, 220), (16, 159), (3, 1111), (6, 1465), (17, 1054), (16, 894), (3, 759), (16, 488),
        (17, 617), (17, 224), (4, 338), (20, 683), (7, 689), (16, 974), (20, 665), (3, 409), (17, 838), (17, 498),
        (20, 636), (3, 360), (16, 608), (5, 660), (3, 2975), (20, 356), (17, 437), (3, 459), (2, 860), (17, 1142),
        (17, 2965), (3, 401), (3, 447), (4, 2050), (1, 31849), (17, 868), (17, 1027), (3, 14005), (4, 7618), (3,
        2894), (3, 3480), (2, 2944), (5, 6150), (3, 1521), (3, 3469), (17, 989), (3, 574), (3, 5794), (8, 317), (3,
        1130), (3, 2156), (20, 4874), (2, 1164), (2, 3677), (2, 2353), (2, 1364), (3, 846), (3, 5204), (3, 3023),
        (10, 895), (16, 723), (10, 2459), (21, 4252), (3, 709), (3, 2021), (3, 1192), (2, 1147), (17, 583), (9,
        380), (3, 906), (3, 0), (3, 6796), (17, 1526), (20, 579), (4, 718), (17, 690), (17, 717), (3, 800), (20,
        1487), (14, 917), (17, 2051), (20, 734), (2, 538), (17, 0), (20, 461), (3, 794), (8, 922), (20, 574), (2,
        366), (20, 1568), (3, 349), (16, 631), (21, 1691), (2, 370),
    ];

    /// <summary>
    /// The same fixture for room <c>dispatch-implement-5d9686dd</c>
    /// (<c>artifacts/execution_01b37417062c4250ba8b211a851707a2/.stdout.log</c>), same regeneration
    /// recipe: 94 distinct ids over 176 usage-bearing lines, 82 repeats (again 0 of 82 differing),
    /// and — the fact that makes this room the discriminating half of the pair — ZERO subagent
    /// messages.
    /// </summary>
    private static readonly (long Out, long CacheCreation)[] Room5d9686ddMessages =
    [
        (2, 27817), (2, 1400), (17, 713), (8, 16550), (3, 2757), (2, 3952), (3, 24258), (2, 1207), (2, 3533), (3,
        4327), (2, 1848), (10, 3098), (3, 3499), (9, 1318), (3, 1574), (3, 5245), (20, 1109), (3, 2783), (3, 2329),
        (3, 1784), (1, 5481), (2, 393), (3, 2871), (20, 268), (1, 3955), (2, 3576), (20, 233), (2, 527), (20, 218),
        (20, 901), (2, 162), (2, 1283), (3, 1235), (7, 1332), (20, 1120), (2, 1005), (20, 420), (1, 2172), (2,
        1453), (3, 3757), (7, 993), (17, 472), (3, 540), (8, 1758), (20, 297), (2, 3118), (3, 479), (2, 554), (20,
        941), (20, 424), (9, 525), (2, 4440), (2, 4553), (20, 2294), (6, 487), (2, 763), (20, 494), (3, 580), (2,
        985), (3, 887), (20, 587), (3, 1029), (20, 2103), (4, 850), (2, 2482), (2, 2822), (6, 6895), (4, 2198), (3,
        1104), (3, 1984), (20, 527), (20, 1005), (8, 1049), (5, 1012), (3, 2992), (2, 8310), (6, 3184), (2, 508),
        (6, 3145), (5, 1035), (3, 532), (8, 925), (1, 2208), (3, 455), (20, 503), (20, 441), (20, 505), (7, 185),
        (2, 269), (3, 1883), (2, 1930), (4, 1126), (20, 205), (7, 2622),
    ];

    /// <summary>
    /// The value claude's mid-stream <c>message.usage.input_tokens</c> carries on every message of
    /// both captures — docs/vendor-capabilities.md records the measurement. Not "a small number" but
    /// the same number every time, which is what makes it a placeholder rather than a small real
    /// reading, and why it is a constant here instead of a fixture column.
    /// </summary>
    private const long ClaudePlaceholderInputTokens = 2;

    /// <summary>Room <c>3dc5e21a</c>'s real terminal <c>"type":"result"</c> line, trimmed to the
    /// <c>usage</c>/<c>modelUsage</c>/<c>num_turns</c> fields this parser reads, values verbatim.</summary>
    private const string Room3dc5e21aResultLine =
        """{"type":"result","num_turns":125,"usage":{"input_tokens":236,"cache_creation_input_tokens":221809,"cache_read_input_tokens":18306867,"output_tokens":76050,"output_tokens_details":{"thinking_tokens":15370}},"modelUsage":{"claude-opus-5":{"inputTokens":421821,"outputTokens":113293,"cacheReadInputTokens":21764631,"cacheCreationInputTokens":349454,"thinkingTokens":26232}}}""";

    /// <summary>Room <c>5d9686dd</c>'s, same treatment.</summary>
    private const string Room5d9686ddResultLine =
        """{"type":"result","num_turns":100,"usage":{"input_tokens":188,"cache_creation_input_tokens":227657,"cache_read_input_tokens":16254371,"output_tokens":66924,"output_tokens_details":{"thinking_tokens":37565}},"modelUsage":{"claude-sonnet-5":{"inputTokens":188,"outputTokens":66924,"cacheReadInputTokens":16254371,"cacheCreationInputTokens":227657,"thinkingTokens":37565}}}""";

    private static string ClaudeAssistantLine(int index, long outputTokens, long cacheCreationTokens) =>
        "{\"type\":\"assistant\",\"message\":{\"id\":\"msg_" + index + "\",\"usage\":{"
        + "\"input_tokens\":" + ClaudePlaceholderInputTokens
        + ",\"cache_creation_input_tokens\":" + cacheCreationTokens
        + ",\"cache_read_input_tokens\":0"
        + ",\"output_tokens\":" + outputTokens + "}}}";

    private static TokenBudgetMonitor ReplayClaudeRoom((long Out, long CacheCreation)[] messages, long? budget)
    {
        var monitor = new TokenBudgetMonitor(budget, maxToolSteps: null, billedRateLimit: null, new ClaudeUsageParser());
        for (var i = 0; i < messages.Length; i++)
        {
            monitor.OnStdoutLine(ClaudeAssistantLine(i, messages[i].Out, messages[i].CacheCreation));
        }

        return monitor;
    }

    private static long TerminalBilled(string resultLine)
    {
        Assert.True(new ClaudeUsageParser().TryParseFinalUsage(resultLine, out var usage));
        return (usage!.TokensIn ?? 0) + (usage.TokensOut ?? 0) + (usage.CacheCreationTokens ?? 0);
    }

    [Theory]
    [InlineData(nameof(Room3dc5e21aMessages), 344_225)]
    [InlineData(nameof(Room5d9686ddMessages), 228_536)]
    public void RED_the_pre_1706_reading_bills_the_placeholders_and_lands_on_the_wrong_number(string fixtureName, long preFixBilled)
    {
        // The defect, reproduced turn by turn against the real per-message data: the pre-#1706
        // ClaudeUsageParser summed `input_tokens + output_tokens + cache_creation_input_tokens` off
        // each deduped `assistant` line, and the first two of those three are placeholders. That
        // formula no longer exists in the tree (removing it IS the fix), so it is reproduced inline
        // here as the red control -- the same treatment
        // RED_the_same_replay_does_NOT_arrest_at_any_point_under_the_pre_1682_level_based_reading
        // already gives #1682's superseded formula.
        var messages = FixtureByName(fixtureName);

        var billed = messages.Sum(m => ClaudePlaceholderInputTokens + m.Out + m.CacheCreation);

        Assert.Equal(preFixBilled, billed);
    }

    [Theory]
    [InlineData(nameof(Room3dc5e21aMessages), 342_557, Room3dc5e21aResultLine, 884_568)]
    [InlineData(nameof(Room5d9686ddMessages), 227_657, Room5d9686ddResultLine, 294_769)]
    public void GREEN_the_fixed_reading_bills_only_the_measurable_component_and_says_so(
        string fixtureName, long liveBilled, string resultLine, long terminalBilled)
    {
        // The honest post-fix result, and the reason this issue's outcome is "state the bound" rather
        // than "close the gap": nothing in the shipped stream-json mode carries a real input or output
        // count, so the live figure moves by less than 0.5% (344,225 -> 342,557 and 228,536 -> 227,657)
        // and remains far under the terminal truth. What changes is that it no longer claims to be the
        // whole figure -- BilledIsFloor, and the terminal reconciliation below.
        var monitor = ReplayClaudeRoom(FixtureByName(fixtureName), budget: null);

        var usage = monitor.SnapshotUsage();
        Assert.Equal(liveBilled, usage.BilledTokens);
        Assert.True(usage.BilledIsFloor);
        // The placeholders are gone, not merely excluded from the Σ: nothing downstream can re-add
        // them by reading the snapshot's own raw fields.
        Assert.Null(usage.TokensIn);
        Assert.Null(usage.TokensOut);
        // The floor is a floor. #1706 review L2: `<=`, not `<` -- a floor GUARANTEES at-most, and a room
        // whose live Σ happened to reach the terminal figure (an agy-shaped stream, or a claude room
        // whose whole spend was cache creation) would be a correct reading that a strict `<` calls a
        // defect. Both of these fixtures happen to sit strictly under, which is exactly why the strict
        // form passed and had to be caught by reading rather than by running.
        Assert.Equal(terminalBilled, TerminalBilled(resultLine));
        Assert.True(usage.BilledTokens <= terminalBilled);
    }

    [Theory]
    [InlineData(nameof(Room3dc5e21aMessages), Room3dc5e21aResultLine, 542_011)]
    [InlineData(nameof(Room5d9686ddMessages), Room5d9686ddResultLine, 67_112)]
    public void The_live_under_read_is_room_dependent_not_a_vendor_constant(string fixtureName, string resultLine, long underRead)
    {
        // #1706's central claim, pinned rather than left in prose: the shortfall is 542,011 on one room
        // and 67,112 on the other. spec/baton.md §3 explains what drives the spread and what follows
        // from it; these two numbers are what that explanation has to keep agreeing with.
        var monitor = ReplayClaudeRoom(FixtureByName(fixtureName), budget: null);

        Assert.Equal(underRead, TerminalBilled(resultLine) - monitor.SnapshotUsage().BilledTokens);
    }

    [Fact]
    public void DISCRIMINATING_the_modelUsage_read_moves_only_the_room_whose_modelUsage_differs()
    {
        // #1706: the terminal read switched from top-level `usage` to `modelUsage`
        // (ClaudeUsageParser.TryParseFinalUsage's own doc has the case). The control that proves it
        // READS ANOTHER FIELD instead of scaling the old one: the room whose modelUsage differs
        // moves by 586,473, the room whose modelUsage equals its top-level usage stays put. A change
        // that moved BOTH would be a different defect, and this arm is the only thing in the suite that
        // could tell them apart. NOTE the earlier name and comment on this test attributed the split to
        // subagent fan-out; spec/baton.md §3 retracts that -- the sweep found 35 zero-subagent rooms on
        // the moving side -- so this asserts the arithmetic and claims nothing about the cause.
        Assert.Equal(298_095, TopLevelOnlyBilled(Room3dc5e21aResultLine));
        Assert.Equal(884_568, TerminalBilled(Room3dc5e21aResultLine));

        Assert.Equal(294_769, TopLevelOnlyBilled(Room5d9686ddResultLine));
        Assert.Equal(294_769, TerminalBilled(Room5d9686ddResultLine));
    }

    [Fact]
    public void HONEST_neither_delivered_claude_room_arrests_at_the_pre_2034_implement_budget_live_or_terminal()
    {
        // The #1706 budget re-derivation's own evidence (spec/baton.md §3): both rooms delivered their
        // work and neither crosses 1,200,000 on EITHER figure -- 884,568 is the higher of the two
        // corrected totals. So the value shipped then did not false-arrest a delivered claude room and
        // was left where it was; what changed was the derivation text, which claimed a "~2x the higher
        // measured normal room" method against figures that method was never applied to.
        Assert.False(ReplayClaudeRoom(Room3dc5e21aMessages, Pre2034ImplementBudget).Arrested);
        Assert.False(ReplayClaudeRoom(Room5d9686ddMessages, Pre2034ImplementBudget).Arrested);
        Assert.True(TerminalBilled(Room3dc5e21aResultLine) < Pre2034ImplementBudget);
        Assert.True(TerminalBilled(Room5d9686ddResultLine) < Pre2034ImplementBudget);
    }

    [Fact]
    public void HONEST_neither_delivered_claude_room_arrests_LIVE_at_the_2034_claude_implement_budget_though_one_terminal_total_exceeds_it()
    {
        // #2034 halves claude's ceiling to 600,000. The arrest is made on the LIVE Σ (the floor), and
        // both delivered rooms stay under it live (their live Σ are the denominators the effective-
        // ceiling test above divides by), so the re-pin false-arrests neither. The third assertion is the honest cost, stated rather than left to inference:
        // 3dc5e21a's terminal whole-tree total (884,568) is ABOVE the new ceiling, so a claude meter
        // that ever read its real spend live would arrest this room. It does not today, and the
        // effective-ceiling arithmetic in the test above is what says how far apart the two are.
        Assert.False(ReplayClaudeRoom(Room3dc5e21aMessages, ShippedClaudeImplementBudget).Arrested);
        Assert.False(ReplayClaudeRoom(Room5d9686ddMessages, ShippedClaudeImplementBudget).Arrested);
        Assert.True(TerminalBilled(Room3dc5e21aResultLine) > ShippedClaudeImplementBudget);
        Assert.True(TerminalBilled(Room5d9686ddResultLine) < ShippedClaudeImplementBudget);
    }

    [Fact]
    public void The_live_floor_widens_the_effective_claude_ceiling_by_the_room_s_own_under_read_factor()
    {
        // What shipping a floor costs the budget in real tokens. spec/baton.md §3 argues it and cites
        // this test by name; the assertions exist so that section and the code cannot drift apart.
        var effectiveCeiling3dc5e21a =
            (double)Pre2034ImplementBudget * TerminalBilled(Room3dc5e21aResultLine) / ReplayClaudeRoom(Room3dc5e21aMessages, budget: null).SnapshotUsage().BilledTokens!.Value;
        var effectiveCeiling5d9686dd =
            (double)Pre2034ImplementBudget * TerminalBilled(Room5d9686ddResultLine) / ReplayClaudeRoom(Room5d9686ddMessages, budget: null).SnapshotUsage().BilledTokens!.Value;

        Assert.InRange(effectiveCeiling3dc5e21a, 3_090_000, 3_110_000);
        Assert.InRange(effectiveCeiling5d9686dd, 1_550_000, 1_560_000);
    }

    [Fact]
    public void POLARITY_agy_s_incremental_reading_is_not_a_floor()
    {
        // The other direction of the same condition: agy's step_update usage carries real input and
        // output figures, so its Σ is a measurement and must not be labelled a floor. Without this arm
        // a parser that set BilledIsFloor unconditionally would pass every claude assertion above.
        var monitor = new TokenBudgetMonitor(budget: null, maxToolSteps: null, billedRateLimit: null, new AgyUsageParser());

        monitor.OnStdoutLine(AgyDoneLine(14205, 443));

        var usage = monitor.SnapshotUsage();
        Assert.False(usage.BilledIsFloor);
        Assert.Equal(14205 + 443, usage.BilledTokens);
    }

    private static (long Out, long CacheCreation)[] FixtureByName(string name) => name switch
    {
        nameof(Room3dc5e21aMessages) => Room3dc5e21aMessages,
        nameof(Room5d9686ddMessages) => Room5d9686ddMessages,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown claude replay fixture."),
    };

    /// <summary>
    /// The pre-#1706 terminal read — top-level <c>usage</c> only, ignoring <c>modelUsage</c> — kept
    /// solely so <see cref="DISCRIMINATING_the_modelUsage_read_moves_only_the_room_whose_modelUsage_differs"/>
    /// has something to discriminate against. Not a second implementation of anything shipped.
    /// </summary>
    private static long TopLevelOnlyBilled(string resultLine)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(resultLine);
        var usage = doc.RootElement.GetProperty("usage");
        return usage.GetProperty("input_tokens").GetInt64()
            + usage.GetProperty("output_tokens").GetInt64()
            + usage.GetProperty("cache_creation_input_tokens").GetInt64();
    }
}
