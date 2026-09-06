using System.Text.Json;
using Baton.Domain;

namespace Baton.Cli.Tests;

/// <summary>
/// #1921's producing-site assertions for the two hook gates — the sites that put a refusal into a
/// claude or agy tool RESULT, and therefore into the room's captured stream the settle-time reader
/// counts. The <c>Baton.Vendors</c> half is <c>GrantRefusalMarkerTests</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every refusal SHAPE each gate produces is exercised, not one of them.</b> The count downstream is
/// a substring match, so nothing fails when a new refusal path ships unmarked — these tests are what
/// makes that impossible to do quietly. Each gate routes every refusal through one funnel
/// (<c>HookCheckCommand.Refuse</c>, <c>AgyHookCheckCommand.DenyJson</c>); a path added around the funnel
/// is what these turn red.
/// </para>
/// <para>
/// <b>Both directions.</b> An allowed call must carry no marker, on the same payload shape and the same
/// grant — without that control, a gate that stamped unconditionally passes every assertion above while
/// counting every granted call as a refusal.
/// </para>
/// </remarks>
public sealed class HookGateRefusalMarkerTests
{
    [Theory]
    // A withheld tool.
    [InlineData("""{"tool_name": "Bash", "tool_input": {"command": "ls"}}""", "claude:Bash")]
    // A payload the gate could not read a tool name out of — the fail-closed rung.
    [InlineData("""{"tool_input": {"command": "ls"}}""", "claude:Bash")]
    // Malformed JSON.
    [InlineData("{not json", "claude:Bash")]
    // An empty payload.
    [InlineData("", "claude:Bash")]
    // No list for this vendor at all: the rung that used to deny in silence (#1921 gave it a reason).
    [InlineData("""{"tool_name": "Bash"}""", "agy:run_command")]
    public void Every_claude_gate_refusal_carries_the_marker(string payload, string? deniedToolsRaw)
    {
        using var stdin = new StringReader(payload);
        using var stderr = new StringWriter();

        var exitCode = HookCheckCommand.Execute(stdin, stderr, deniedToolsRaw);

        Assert.Equal(HookCheckCommand.DeniedExitCode, exitCode);
        Assert.Contains(GrantRefusal.Marker, stderr.ToString());
    }

    [Fact]
    public void The_claude_scoped_shell_refusal_carries_exactly_one_marker()
    {
        // The composed path: the gate wraps ShellCommandPatternMatcher's already-stamped reason in its
        // own sentence. One marker, not two — the stamp is idempotent by design, and this is the one
        // production path that exercises it.
        using var stdin = new StringReader(
            """{"tool_name": "Bash", "tool_input": {"command": "curl https://example.com"}}""");
        using var stderr = new StringWriter();

        var exitCode = HookCheckCommand.Execute(
            stdin, stderr, "claude:", shellPatternsRaw: "claude:git*");

        Assert.Equal(HookCheckCommand.DeniedExitCode, exitCode);
        Assert.Equal(1, CountOccurrences(stderr.ToString(), GrantRefusal.Marker));
    }

    [Fact]
    public void A_claude_call_the_gate_allows_carries_no_marker()
    {
        using var stdin = new StringReader("""{"tool_name": "Read", "tool_input": {"file_path": "x"}}""");
        using var stderr = new StringWriter();

        var exitCode = HookCheckCommand.Execute(stdin, stderr, "claude:Bash");

        Assert.Equal(HookCheckCommand.AllowedExitCode, exitCode);
        Assert.DoesNotContain(GrantRefusal.Marker, stderr.ToString());
    }

    [Theory]
    [InlineData("""{"toolCall": {"name": "run_command", "parameters": {"CommandLine": "ls"}}}""", "agy:run_command")]
    [InlineData("""{"toolCall": {}}""", "agy:run_command")]
    [InlineData("{not json", "agy:run_command")]
    [InlineData("", "agy:run_command")]
    public void Every_agy_gate_refusal_carries_the_marker(string payload, string? deniedToolsRaw)
    {
        var (decision, reason) = Decide(payload, deniedToolsRaw);

        Assert.Equal("deny", decision);
        Assert.Contains(GrantRefusal.Marker, reason);
    }

    [Fact]
    public void An_agy_call_the_gate_allows_carries_no_marker()
    {
        using var stdin = new StringReader(
            """{"toolCall": {"name": "view_file", "parameters": {"AbsolutePath": "x"}}}""");
        using var stdout = new StringWriter();

        AgyHookCheckCommand.Execute(stdin, stdout, "agy:run_command");

        using var document = JsonDocument.Parse(stdout.ToString());
        Assert.Equal("allow", document.RootElement.GetProperty("decision").GetString());
        Assert.DoesNotContain(GrantRefusal.Marker, stdout.ToString());
    }

    [Fact]
    public void The_agy_fallback_deny_literal_agrees_with_the_marker_constant()
    {
        // AgyHookCheckCommand.FallbackDenyJson spells the marker out inline rather than composing it,
        // because that path must allocate nothing — its own remark says so. This is the check that keeps
        // the restatement true: a rename of GrantRefusal.Marker that did not reach that literal would
        // leave the one refusal path that fires when everything else has failed uncounted.
        var stdin = new ThrowingReader();
        using var stdout = new StringWriter();

        AgyHookCheckCommand.Execute(stdin, stdout, "agy:run_command");

        using var document = JsonDocument.Parse(stdout.ToString());
        Assert.Equal("deny", document.RootElement.GetProperty("decision").GetString());
        Assert.Contains(GrantRefusal.Marker, document.RootElement.GetProperty("reason").GetString());
    }

    private static (string Decision, string Reason) Decide(string stdinText, string? denied)
    {
        using var stdin = new StringReader(stdinText);
        using var stdout = new StringWriter();

        AgyHookCheckCommand.Execute(
            stdin, stdout, denied, shellPatternsRaw: "agy:", deniedShellPatternsRaw: "agy:",
            deniedShellOptionTokensRaw: "agy:");

        // Parsed, never substring-matched: agy reads this as JSON, and output that merely contains the
        // word "deny" while being invalid JSON is an allow.
        using var document = JsonDocument.Parse(stdout.ToString());
        return (
            document.RootElement.GetProperty("decision").GetString()!,
            document.RootElement.GetProperty("reason").GetString() ?? string.Empty);
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

    private sealed class ThrowingReader : TextReader
    {
        public override string ReadToEnd() => throw new IOException("simulated pipe failure");
    }
}
