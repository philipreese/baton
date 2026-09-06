using Baton.Domain;
using Baton.Vendors;
using Xunit;

namespace Baton.Vendors.Tests;

/// <summary>
/// #1921's producing-site assertions for the two refusal sources that live in <c>Baton.Vendors</c>:
/// <see cref="ShellCommandPatternMatcher"/> and <see cref="CodexDynamicToolResult"/>. The two in
/// <c>Baton.Cli</c> — the claude and agy hook gates — are asserted in their own commands' test classes.
/// </summary>
/// <remarks>
/// <b>Why a test per site rather than one over the marker constant.</b> The count downstream is a
/// substring match, so nothing about it fails when a NEW refusal ships unmarked — which is exactly how
/// the five phrasings this issue replaced came to exist. These tests are the check that a new refusal
/// path cannot be added silently: each asserts that the site's own refusal-producing entry point stamps,
/// so a sixth reason routed around it turns one of them red.
/// </remarks>
public sealed class GrantRefusalMarkerTests
{
    [Theory]
    [InlineData("gh api repos/x/y", new[] { "gh issue view*" }, new[] { "gh api*" })]
    [InlineData("curl https://example.com", new[] { "git*" }, new string[0])]
    public void Every_scoped_shell_refusal_carries_the_marker(
        string commandLine, string[] allowed, string[] denied)
    {
        var result = ShellCommandPatternMatcher.EvaluateChainedCommand(commandLine, allowed, denied);

        Assert.False(result.IsAllowed);
        Assert.Contains(GrantRefusal.Marker, result.Reason);
    }

    [Fact]
    public void An_unparseable_command_line_carries_the_marker_too()
    {
        // The third refusal SHAPE this matcher produces (deny-list, no-allowed-pattern, unparseable) —
        // a different code path from the two above, and the one a stamp placed at a `return` rather than
        // on the record would have missed.
        var result = ShellCommandPatternMatcher.EvaluateChainedCommand(
            "echo $(whoami)", ["echo*"], []);

        Assert.Equal(ShellCommandPatternMatcher.ScopedShellVerdict.Unparseable, result.Verdict);
        Assert.Contains(GrantRefusal.Marker, result.Reason);
    }

    [Fact]
    public void An_allowed_command_carries_no_marker()
    {
        // The discriminating control. Without it every assertion above passes on a matcher that stamped
        // unconditionally — which would count every allowed call as a refusal.
        var result = ShellCommandPatternMatcher.EvaluateChainedCommand("git status", ["git*"], []);

        Assert.True(result.IsAllowed);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void A_refused_dynamic_tool_result_carries_the_marker_and_an_allowed_or_failed_one_does_not()
    {
        Assert.Contains(GrantRefusal.Marker, CodexDynamicToolResult.Refused("no reads in this grant").Text);
        Assert.DoesNotContain(GrantRefusal.Marker, CodexDynamicToolResult.Allowed("file contents").Text);

        // The third outcome, and the one the funnel used to swallow: unsuccessful, but not a decision
        // the grant took. Both unsuccessful factories asserted here so a future merge of the two turns
        // this red rather than silently restoring the over-count.
        var failed = CodexDynamicToolResult.Failed("Command exited 1.\n3 tests failed");
        Assert.False(failed.Success);
        Assert.DoesNotContain(GrantRefusal.Marker, failed.Text);
    }

    [Fact]
    public void A_composed_refusal_carries_exactly_one_marker()
    {
        // The idempotence that makes "stamp at every producing site" safe: the codex run-command handler
        // passes the matcher's already-stamped reason to Denied. Two markers would still count as one
        // refusal, so this is about the transcript a worker reads rather than the count.
        var matcherReason = ShellCommandPatternMatcher
            .EvaluateChainedCommand("curl example.com", ["git*"], []).Reason!;

        var text = CodexDynamicToolResult.Refused(matcherReason).Text;

        Assert.Equal(1, CountOccurrences(text, GrantRefusal.Marker));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
