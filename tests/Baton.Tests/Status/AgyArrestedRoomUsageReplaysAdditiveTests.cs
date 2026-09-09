using Baton.Mutation;
using Baton.Status;
using Xunit;

namespace Baton.Tests.Status;

/// <summary>
/// #2144 fix-round: a second real-shape capture behind the additive claim
/// <see cref="AgyTerminalUsageIsCumulativeTests"/> pins on <c>38c24d11</c> (70 turns) alone. This one
/// is <c>b982</c>'s own 157 <c>agent_response</c> lines, copied verbatim from that room's real
/// <c>.stdout.log</c> (<c>queue-b982-822e7594/artifacts/execution_3eb34ef8b11444cc968178b3a49c5b95</c>)
/// — the room arrested by issue #2144 for exactly this arithmetic.
/// <para>
/// <b>The terminal line is synthesized, not captured</b> (<c>"status":"SYNTHESIZED_TERMINAL"</c>): the
/// room was arrested mid-run, so agy never emitted its own terminal <c>result</c> event. The
/// synthesized totals are not invented — they are the conductor-verified
/// <c>sum(input_tokens) + sum(output_tokens) = 1,203,855</c> over this same 157-line set, computed
/// independently of this repo with <c>grep -o '"input_tokens":[0-9]*' ... | awk ...</c> against the raw
/// file, and matching the arrest's own billed total. This test only confirms the parser/monitor
/// reproduce a total that was already established outside them; it does not itself establish the
/// total.
/// </para>
/// </summary>
public sealed class AgyArrestedRoomUsageReplaysAdditiveTests
{
    internal const string FixtureFileName = "agy-b982-agent-response-usage.jsonl";

    /// <summary>Conductor-verified: sum(input_tokens) + sum(output_tokens) over the real b982 capture.</summary>
    private const long VerifiedBilledTotal = 1_066_823 + 137_032;

    internal static string[] LoadRealAgyStream() =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", FixtureFileName));

    [Fact]
    public void The_real_capture_carries_157_turns_matching_the_arrest()
    {
        var lines = LoadRealAgyStream();
        var parser = new AgyUsageParser();

        var usageLines = lines.Count(line => parser.TryParseIncrementalUsage(line, out var u) && u is not null);

        Assert.Equal(157, usageLines);
    }

    [Fact]
    public void MEASURED_replaying_b982_s_captured_lines_reproduces_the_conductor_verified_total()
    {
        var lines = LoadRealAgyStream();
        var parser = new AgyUsageParser();
        var monitor = new TokenBudgetMonitor(budget: null, maxToolSteps: null, billedRateLimit: null, parser);

        foreach (var line in lines)
        {
            monitor.OnStdoutLine(line);
        }

        var liveBilled = monitor.SnapshotUsage().BilledTokens;

        Assert.Equal(VerifiedBilledTotal, liveBilled);
        // The reading the interface doc used to prescribe -- last turn's TokensIn only -- undercounts
        // by two orders of magnitude on this same capture, the same shape as the 70-turn fixture.
        Assert.True(parser.TryParseIncrementalUsage(lines[156], out var lastTurn) && lastTurn is not null);
        Assert.NotEqual(VerifiedBilledTotal, lastTurn!.TokensIn);
    }
}
