using Baton.Cli;
using Xunit;

namespace Baton.Cli.Tests;

public sealed class RetainedIssueWorktreeValidatorTests
{
    [Fact]
    public void Registration_is_retained_when_the_target_is_not_the_last_porcelain_entry()
    {
        var target = Path.GetFullPath(@"C:\repos\w2333");
        var another = Path.GetFullPath(@"C:\repos\baton");
        var porcelain = $"""
            worktree {target}
            HEAD aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
            branch refs/heads/2333-lane

            worktree {another}
            HEAD bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb
            branch refs/heads/main

            """;

        var found = RetainedIssueWorktreeValidator.TryRegistration(
            porcelain, target, out var head, out var branch);

        Assert.True(found);
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", head);
        Assert.Equal("refs/heads/2333-lane", branch);
    }

    [Fact]
    public void Registration_refuses_an_absent_target()
    {
        var found = RetainedIssueWorktreeValidator.TryRegistration(
            $"worktree {Path.GetFullPath(@"C:\repos\baton")}`nHEAD aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa`nbranch refs/heads/main`n",
            Path.GetFullPath(@"C:\repos\w2333"),
            out var head,
            out var branch);

        Assert.False(found);
        Assert.Null(head);
        Assert.Null(branch);
    }
}
