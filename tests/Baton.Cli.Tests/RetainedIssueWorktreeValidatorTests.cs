using Baton.Cli;
using Baton.Accounting;
using Baton.Cli.Tests.TestSupport;
using Baton.Queue;
using Baton.Vendors;
using Xunit;

namespace Baton.Cli.Tests;

public sealed class RetainedIssueWorktreeValidatorTests
{
    [Fact]
    public async Task Validation_returns_proof_without_mutation_and_uses_resolved_gh()
    {
        using var home = new IsolatedBatonHome();
        var root = Path.Combine(Path.GetTempPath(), $"retained-validator-{Guid.NewGuid():N}");
        var workspace = Path.Combine(root, "w2333");
        Directory.CreateDirectory(workspace);
        try
        {
            ProjectCeilingStore.Set(workspace, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);
            const string head = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            const string repository = "github.com/example/baton";
            var calls = new List<string>();
            Task<(int ExitCode, string Output)> Runner(
                string file, IReadOnlyList<string> arguments, string _, CancellationToken __)
            {
                calls.Add(file);
                if (arguments is ["worktree", "list", "--porcelain"])
                    return Task.FromResult((0, $"worktree {workspace}\nHEAD {head}\nbranch refs/heads/2333-lane\n"));
                if (arguments is ["rev-parse", ..]) return Task.FromResult((0, head));
                if (arguments is ["status", ..]) return Task.FromResult((0, string.Empty));
                if (arguments is ["pr", "list", ..]) return Task.FromResult((0, "[]"));
                throw new InvalidOperationException(string.Join(' ', arguments));
            }

            var proof = await RetainedIssueWorktreeValidator.ValidateAsync(
                workspace, 2333, repository, root, WorkerRoleCatalog.For("implement"), false, [], [],
                TestContext.Current.CancellationToken, Runner,
                (_, _) => Task.FromResult(RepositoryIdentity.From("https://github.com/example/baton.git", null)),
                (_, _, _) => @"C:\Program Files\GitHub CLI\gh.exe",
                QueueWorktreeLivenessProbe.Default with
                {
                    BuildLockPath = Path.Combine(root, "no-build-lock"),
                });

            Assert.Equal(workspace, proof.Workspace);
            Assert.Equal("2333-lane", proof.Branch);
            Assert.Contains(@"C:\Program Files\GitHub CLI\gh.exe", calls, StringComparer.Ordinal);
            Assert.DoesNotContain("gh", calls, StringComparer.Ordinal);
            Assert.Empty(proof.TerminalPredecessorTags);
        }
        finally
        {
            Baton.Tests.Shared.DirectoryCleanup.DeleteRecursively(root);
        }
    }

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

    [Fact]
    public void Failed_but_unretired_lifecycle_still_owns_a_retained_workspace()
    {
        var workspace = Path.GetFullPath(@"C:\repos\w2333");
        var active = new QueueItem
        {
            Tag = "failed-implementation",
            Role = "implement",
            Workspace = workspace,
            SpecFile = "unused",
            Branch = "2333-lane",
            State = QueueItemState.Failed,
            Stage = WorkStage.Implement,
            RoomDirectory = @"C:\rooms\still-owned",
        };

        var refusal = Assert.Throws<CliArgumentException>(() =>
            RetainedIssueWorktreeValidator.RefuseIfLiveQueueOwnership([active], workspace, "2333-lane"));

        Assert.Contains("queue-liveness", refusal.Message, StringComparison.Ordinal);
    }
}
