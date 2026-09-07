using Baton.Vendors;
using Xunit;

namespace Baton.Vendors.Tests;

/// <summary>
/// #2002 rule 1's table. The two halves are one test on purpose: an assertion that
/// <c>Start-Process</c> is refused passes just as well on a detector that refuses every line, and the
/// lines this rule must NOT touch are the ordinary traffic of every claude and codex room, which
/// scanned clean in the issue's vendor scan. Both directions or neither.
/// </summary>
public sealed class BackgroundingShapeDetectorTests
{
    [Theory]
    [InlineData("Start-Process dotnet -ArgumentList 'build' -NoNewWindow -PassThru", "Start-Process")]
    [InlineData("start-process pwsh", "Start-Process")]
    [InlineData("Start-Job -ScriptBlock { dotnet test }", "Start-Job")]
    [InlineData("Start-ThreadJob -ScriptBlock { dotnet test }", "Start-Job")]
    [InlineData("Invoke-Command -ComputerName . -ScriptBlock { dotnet build } -AsJob", "Invoke-Command -AsJob")]
    [InlineData("nohup dotnet test", "nohup")]
    [InlineData("setsid dotnet test", "setsid")]
    [InlineData("dotnet test &", "a trailing &")]
    [InlineData("dotnet test > out.log 2>&1 &", "a trailing &")]
    // #2002 review LOW: the alias and cmd-builtin spellings. saps/sajb are plain words; `start` is
    // segment-anchored (see the negative arms below for what that buys).
    [InlineData("saps dotnet -ArgumentList 'build'", "Start-Process")]
    [InlineData("sajb { dotnet test }", "Start-Job")]
    [InlineData("start notepad.exe", "start")]
    [InlineData("git status; start dotnet.exe", "start")]
    [InlineData("cmd /c start dotnet build", "cmd /c start")]
    [InlineData("cmd /k start dotnet build", "cmd /c start")]
    public void Every_backgrounding_shape_is_named(string commandLine, string expectedShape) =>
        Assert.Equal(
            expectedShape,
            BackgroundingShapeDetector.Detect(commandLine, BackgroundingShapeDetector.ShellFamily.Windows));

    /// <summary>
    /// #2002 review LOW: the false positives the `start` spelling would cause if it were matched as a
    /// bare word anywhere on the line. The review brief pre-declares these HIGH severity, and they are
    /// the reason the match is anchored to a segment's first token rather than added to the word table.
    /// This arm is the control for the four positive `start` arms above: delete the anchoring and the
    /// positives still pass while these fail.
    /// </summary>
    [Theory]
    [InlineData("npm start")]
    [InlineData("pixi run start")]
    [InlineData("dotnet run -- start")]
    [InlineData("git commit -m \"start the daemon\"")]
    public void The_start_spelling_does_not_refuse_a_subcommand_named_start(string commandLine) =>
        Assert.Null(BackgroundingShapeDetector.Detect(
            commandLine, BackgroundingShapeDetector.ShellFamily.Windows));

    /// <summary>
    /// #2002 review LOW: the bare `&` is the one rule that differs by shell, so it is asserted in both
    /// directions on both families. A mid-line `&` backgrounds under a POSIX shell and separates under
    /// cmd.exe; a redirection's `&` is neither, on either.
    /// </summary>
    [Theory]
    [InlineData("dotnet build & sleep 1", BackgroundingShapeDetector.ShellFamily.Posix, "a background &")]
    [InlineData("dotnet build & sleep 1", BackgroundingShapeDetector.ShellFamily.Windows, null)]
    [InlineData("dotnet test &", BackgroundingShapeDetector.ShellFamily.Posix, "a background &")]
    [InlineData("dotnet test &", BackgroundingShapeDetector.ShellFamily.Windows, "a trailing &")]
    [InlineData("dotnet build && dotnet test", BackgroundingShapeDetector.ShellFamily.Posix, null)]
    [InlineData("dotnet test > out.log 2>&1", BackgroundingShapeDetector.ShellFamily.Posix, null)]
    [InlineData("dotnet test &> out.log", BackgroundingShapeDetector.ShellFamily.Posix, null)]
    [InlineData("echo \"a & b\"", BackgroundingShapeDetector.ShellFamily.Posix, null)]
    public void The_bare_ampersand_rule_is_shell_aware(
        string commandLine, BackgroundingShapeDetector.ShellFamily shell, string? expected) =>
        Assert.Equal(expected, BackgroundingShapeDetector.Detect(commandLine, shell));

    [Theory]
    // The ordinary traffic this rule must leave alone -- the two the issue names explicitly, plus the
    // shapes that merely LOOK like the ones above.
    [InlineData("git push -u origin 2002-lane")]
    [InlineData("dotnet test")]
    [InlineData("pixi run gates-fast-cover")]
    // A quoted mention: a worker writing the rule down, or grepping for it, backgrounds nothing.
    [InlineData("echo \"do not use Start-Process here\"")]
    [InlineData("echo 'nohup is banned'")]
    // A comment tail, both shells' spelling of one.
    [InlineData("dotnet build  # Start-Process would be refused")]
    // `2>&1` without the trailing `&` is a redirection, not a background.
    [InlineData("dotnet test > out.log 2>&1")]
    // cmd.exe's `&&` chain ends in an ampersand pair, which is a separator and not a background.
    [InlineData("dotnet build && dotnet test")]
    // Invoke-Command without -AsJob runs to completion.
    [InlineData("Invoke-Command -ScriptBlock { dotnet build }")]
    // Word boundaries: neither of these is the cmdlet or the flag.
    [InlineData("./My-Start-Process-Wrapper.ps1")]
    [InlineData("Invoke-Command -AsJobName x -ScriptBlock { dotnet build }")]
    // A `#` mid-token is an issue reference, not a comment marker; nothing here backgrounds either way.
    [InlineData("git log --grep=#2002 --oneline")]
    public void Ordinary_foreground_traffic_is_not_refused(string commandLine) =>
        Assert.Null(BackgroundingShapeDetector.Detect(
            commandLine, BackgroundingShapeDetector.ShellFamily.Windows));

    /// <summary>
    /// The one case the masking could get backwards: a comment marker INSIDE a quoted string does not
    /// start a comment, so a backgrounding token after the closing quote is still seen.
    /// </summary>
    [Fact]
    public void A_hash_inside_quotes_does_not_hide_the_rest_of_the_line() =>
        Assert.Equal("nohup", BackgroundingShapeDetector.Detect(
            "echo \"issue #2002\" && nohup dotnet test", BackgroundingShapeDetector.ShellFamily.Windows));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_to_read_is_not_a_refusal(string? commandLine) =>
        Assert.Null(BackgroundingShapeDetector.Detect(
            commandLine, BackgroundingShapeDetector.ShellFamily.Windows));

    /// <summary>
    /// The ceiling clause is the caller's, and its absence is a sentence rather than a gap — the two
    /// hook paths pass null because no Baton per-command ceiling applies to them, and a message that
    /// simply stopped there would leave the worker's actual fear (#1998) unanswered.
    /// </summary>
    [Fact]
    public void The_refusal_states_the_synchronous_contract_with_and_without_a_ceiling()
    {
        var withCeiling = BackgroundingShapeDetector.Refusal("Start-Process", "Baton kills it at 5 minutes.");
        var withoutCeiling = BackgroundingShapeDetector.Refusal("Start-Process", null);

        foreach (var refusal in new[] { withCeiling, withoutCeiling })
        {
            Assert.Contains("Start-Process", refusal, StringComparison.Ordinal);
            Assert.Contains("runs to completion synchronously", refusal, StringComparison.Ordinal);
            Assert.Contains("costs no tool step", refusal, StringComparison.Ordinal);
        }

        Assert.Contains("Baton kills it at 5 minutes.", withCeiling, StringComparison.Ordinal);
        Assert.Contains("no Baton per-command ceiling", withoutCeiling, StringComparison.Ordinal);
    }
}
