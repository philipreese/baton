using Baton.Vendors;

namespace Baton.Cli.Tests;

/// <summary>
/// #600: the denied-tools channel used to collapse "AER set this and nothing is withheld" and "the
/// variable never arrived" into one value, and both allowed. A vendor tag separates them and makes a
/// wrong-vendor list loud rather than a total allow.
/// </summary>
public class DeniedToolChannelTests
{
    private const string AgyDeny = "\"decision\":\"deny\"";
    // #2001: both payloads carry a READABLE command line, as a production one always does. The
    // sibling-pull-request rung (OwnPullRequestOnlyRule, last check on both hooks' shell branches)
    // governs an unscoped grant and denies a shell call whose command line it cannot read — so an
    // args-less payload would now deny for that reason, on a file whose subject is the denied-TOOL
    // channel. Same reasoning as the shell-channel note below: give the fixture the shape production
    // has, so the channel under test is the one deciding.
    private const string AgyPayload =
        """{"toolCall":{"name":"run_command","args":{"CommandLine":"git status"}}}""";
    private const string ClaudePayload =
        """{"tool_name":"Bash","tool_input":{"command":"git status"}}""";

    // These exercise the denied-tool channel, not the shell channel, but the default payload is a
    // run_command — so BOTH shell channels must be Present-and-unscoped ("agy:"), the way production
    // always emits them, or the fail-closed run_command gate (#659 allow, #390 deny) would deny before
    // the denied-tool logic under test is reached. Absent/wrong-vendor shell patterns are covered in
    // AgyHookCheckCommandTests, not here.
    private static string RunAgy(
        string? denied, string payload = AgyPayload, string? shellPatterns = "agy:",
        string? deniedShellPatterns = "agy:", string? deniedShellOptionTokens = "agy:")
    {
        using var stdout = new StringWriter();
        AgyHookCheckCommand.Execute(
            new StringReader(payload), stdout, denied, shellPatternsRaw: shellPatterns,
            deniedShellPatternsRaw: deniedShellPatterns,
            deniedShellOptionTokensRaw: deniedShellOptionTokens);
        return stdout.ToString();
    }

    private static int RunClaude(string? denied, string payload = ClaudePayload)
    {
        using var stderr = new StringWriter();
        return HookCheckCommand.Execute(new StringReader(payload), stderr, denied);
    }

    // --- absent: cannot know what is withheld, so deny -------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_absent_list_denies_on_both_vendors(string? absent)
    {
        // The failure agy.hook-env-inherited is a sentinel for: if a vendor stopped inheriting the
        // environment, this used to degrade to a total allow indistinguishable from a working gate.
        Assert.Contains(AgyDeny, RunAgy(absent), StringComparison.Ordinal);
        Assert.Equal(2, RunClaude(absent));
    }

    // --- present and empty: AER said nothing is withheld, so allow --------------------------------

    [Fact]
    public void A_tagged_empty_list_allows_on_both_vendors()
    {
        // The control that keeps the deny above honest, and the one that matters most in practice:
        // BuildDeniedTools returns empty whenever PermissionGrant is null — the raw PermissionScope
        // escape hatch — which is the ordinary `baton run` shape. Denying here breaks every such worker.
        Assert.DoesNotContain(AgyDeny, RunAgy("agy:"), StringComparison.Ordinal);
        Assert.Equal(0, RunClaude("claude:"));
    }

    // --- wrong vendor: names this gate cannot judge, so deny --------------------------------------

    [Fact]
    public void Another_vendors_list_denies_rather_than_matching_nothing_and_allowing()
    {
        // agy names tools run_command/view_file where claude names them Bash/Read. Comparing across
        // vocabularies matches nothing, which reads as allow-everything.
        Assert.Contains(AgyDeny, RunAgy("claude:Bash,Edit,Write"), StringComparison.Ordinal);
        Assert.Equal(2, RunClaude("agy:run_command,view_file"));
    }

    [Fact]
    public void An_untagged_legacy_value_denies_rather_than_being_assumed_to_be_ours()
    {
        // Only reachable from a worker spawned by an AER older than this hook binary. Guessing it is
        // ours would restore the ambiguity the tag exists to remove, so it fails closed.
        Assert.Contains(AgyDeny, RunAgy("run_command"), StringComparison.Ordinal);
        Assert.Equal(2, RunClaude("Bash"));
    }

    // --- the ordinary job still works ------------------------------------------------------------

    [Fact]
    public void A_tagged_list_still_denies_a_named_tool_and_allows_an_unnamed_one()
    {
        // The polarity control on the whole change: without it, everything above passes on a gate that
        // denies unconditionally.
        Assert.Contains(AgyDeny, RunAgy("agy:run_command"), StringComparison.Ordinal);
        Assert.DoesNotContain(AgyDeny, RunAgy("agy:view_file"), StringComparison.Ordinal);

        Assert.Equal(2, RunClaude("claude:Bash"));
        Assert.Equal(0, RunClaude("claude:Read"));
    }

    // --- the adapters emit what the hooks expect --------------------------------------------------

    [Fact]
    public void Each_adapter_emits_its_own_tag_so_the_two_halves_cannot_drift_apart()
    {
        // Baton.Vendors cannot reference Baton.Cli, so the tag is a literal on each side. This is the one
        // test that sees both, and it is what stops a rename on one side silently denying everything.
        var contract = new Baton.Domain.WorkerContract("w", [], [], []);
        var grant = new PermissionGrant(ReadFiles: true, WriteFiles: false);

        var claudeValue = new ClaudeWorkerAdapter()
            .Resolve(new WorkerInvocation("p", PermissionGrant: grant), contract)
            .Environment!.Single(v => v.Name == ClaudeWorkerAdapter.DeniedToolsVariable).Value;
        var agyValue = new AgyWorkerAdapter()
            .Resolve(new WorkerInvocation("p", PermissionGrant: grant), contract)
            .Environment!.Single(v => v.Name == AgyWorkerAdapter.DeniedToolsVariable).Value;

        Assert.StartsWith("claude:", claudeValue, StringComparison.Ordinal);
        Assert.StartsWith("agy:", agyValue, StringComparison.Ordinal);

        Assert.Equal(DeniedToolListStatus.Present, DeniedToolList.Parse(claudeValue, "claude").Status);
        Assert.Equal(DeniedToolListStatus.Present, DeniedToolList.Parse(agyValue, "agy").Status);
        Assert.Equal(DeniedToolListStatus.WrongVendor, DeniedToolList.Parse(claudeValue, "agy").Status);
        Assert.Equal(DeniedToolListStatus.WrongVendor, DeniedToolList.Parse(agyValue, "claude").Status);
    }
}
