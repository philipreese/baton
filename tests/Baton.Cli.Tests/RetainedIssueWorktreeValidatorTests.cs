using Baton.Cli;
using Baton.Queue;
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

    [Fact]
    public void Default_worktree_root_is_the_source_repository_parent()
    {
        var repository = Path.GetFullPath(@"C:\repos\baton");

        var root = IssueWorktreeProvisioner.ResolveWorktreeRoot(null, repository);

        Assert.Equal(Path.GetDirectoryName(repository), root);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Live_queue_ownership_refuses_a_matching_path_or_branch(bool matchesPath)
    {
        var workspace = Path.GetFullPath(@"C:\repos\w2333");
        var live = new QueueItem
        {
            Tag = "other-tag",
            Role = "implement",
            Workspace = matchesPath ? workspace : Path.GetFullPath(@"C:\repos\w-other"),
            SpecFile = "unused",
            Branch = matchesPath ? "another-branch" : "2333-lane",
            State = QueueItemState.Queued,
        };

        var refusal = Assert.Throws<CliArgumentException>(() =>
            RetainedIssueWorktreeValidator.RefuseIfLiveQueueOwnership([live], workspace, "2333-lane"));

        Assert.Contains("queue-liveness", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Terminal_queue_history_does_not_own_a_retained_workspace()
    {
        var workspace = Path.GetFullPath(@"C:\repos\w2333");
        var terminal = new QueueItem
        {
            Tag = "previous-tag",
            Role = "implement",
            Workspace = workspace,
            SpecFile = "unused",
            Branch = "2333-lane",
            State = QueueItemState.Failed,
        };

        RetainedIssueWorktreeValidator.RefuseIfLiveQueueOwnership([terminal], workspace, "2333-lane");
    }
}
