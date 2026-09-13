using System.Diagnostics;
using Baton.Tests.TestSupport;
using Baton.Workspaces;

namespace Baton.Tests.Workspaces;

/// <summary>Real-git regression coverage for the grace checkpoint invariant in spec/baton.md section 3.</summary>
public sealed class GraceCheckpointRealGitTests
{
    [Fact]
    public void One_published_child_on_the_captured_branch_is_safe()
    {
        using var fixture = new GraceRepository();
        var checkpoint = fixture.Capture();

        fixture.Commit("grace child");
        fixture.Push();

        Assert.True(WorktreeProvisioner.IsSafeGraceCheckpoint(fixture.Repository, checkpoint));
    }

    [Fact]
    public void An_amended_child_is_not_safe_and_is_not_destructively_recovered()
    {
        using var fixture = new GraceRepository();
        var checkpoint = fixture.Capture();
        fixture.Commit("first child");
        var amended = fixture.Head;
        fixture.Run("commit", "--amend", "--no-edit");

        Assert.False(WorktreeProvisioner.IsSafeGraceCheckpoint(fixture.Repository, checkpoint));
        Assert.False(WorktreeProvisioner.RestoreGraceCheckpointToDirty(fixture.Repository, checkpoint));
        Assert.Equal(fixture.Head, fixture.RevParse("HEAD"));
        Assert.NotEqual(checkpoint.Head, amended);
    }

    [Fact]
    public void Multiple_new_commits_are_not_safe_and_remain_reachable()
    {
        using var fixture = new GraceRepository();
        var checkpoint = fixture.Capture();
        fixture.Commit("first child");
        var firstChild = fixture.Head;
        fixture.Commit("second child");

        Assert.False(WorktreeProvisioner.IsSafeGraceCheckpoint(fixture.Repository, checkpoint));
        Assert.False(WorktreeProvisioner.RestoreGraceCheckpointToDirty(fixture.Repository, checkpoint));
        Assert.Equal(firstChild, fixture.RevParse("HEAD~1"));
        Assert.Equal(checkpoint.Head, fixture.RevParse("HEAD~2"));
    }

    [Theory]
    [InlineData("checkout", "-b", "other")]
    [InlineData("checkout", "--detach")]
    public void A_branch_switch_or_detached_head_is_not_safe(params string[] command)
    {
        using var fixture = new GraceRepository();
        var checkpoint = fixture.Capture();
        fixture.Run(command);

        Assert.False(WorktreeProvisioner.IsSafeGraceCheckpoint(fixture.Repository, checkpoint));
    }

    [Fact]
    public void Rewritten_or_unpublished_upstream_is_not_safe()
    {
        using var fixture = new GraceRepository();
        var checkpoint = fixture.Capture();
        fixture.Commit("grace child");

        fixture.Run("update-ref", checkpoint.UpstreamRef, checkpoint.UpstreamHead);

        Assert.False(WorktreeProvisioner.IsSafeGraceCheckpoint(fixture.Repository, checkpoint));
        Assert.Equal(checkpoint.Head, fixture.RevParse("HEAD~1"));
    }

    [Fact]
    public void An_existing_unpushed_commit_is_captured_and_retained_by_the_published_child()
    {
        using var fixture = new GraceRepository();
        fixture.Commit("operator commit before grace");
        var checkpoint = fixture.Capture();
        fixture.Commit("grace child");
        fixture.Push();

        Assert.True(WorktreeProvisioner.IsSafeGraceCheckpoint(fixture.Repository, checkpoint));
        Assert.Equal(checkpoint.Head, fixture.RevParse("HEAD~1"));
        Assert.True(fixture.IsAncestor(checkpoint.Head, checkpoint.UpstreamRef));
    }

    private sealed class GraceRepository : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), $"grace-{Guid.NewGuid():N}");

        public GraceRepository()
        {
            Repository = Path.Combine(root, "repo");
            Directory.CreateDirectory(Repository);
            TempGitRepository.InitWithEverythingCommitted(Repository);
            var remote = TempGitRepository.InitBareRepository(Path.Combine(root, "origin.git"));
            TempGitRepository.AddRemote(Repository, "origin", remote);
            TempGitRepository.Push(Repository, "origin", "HEAD:refs/heads/main");
            Run("branch", "--set-upstream-to", "origin/main");
        }

        public string Repository { get; }
        public string Head => RevParse("HEAD");

        public GraceCheckpoint Capture() => Assert.IsType<GraceCheckpoint>(WorktreeProvisioner.CaptureGraceCheckpoint(Repository));

        public void Commit(string message)
        {
            File.WriteAllText(Path.Combine(Repository, $"{Guid.NewGuid():N}.txt"), message);
            Run("add", "-A");
            Run("commit", "-m", message);
        }

        public void Push() => Run("push", "origin", "HEAD:refs/heads/main");
        public string RevParse(string reference) => RunCapturing("rev-parse", "--verify", $"{reference}^{{commit}}").Trim();
        public bool IsAncestor(string ancestor, string descendant) => RunExitCode("merge-base", "--is-ancestor", ancestor, descendant) == 0;

        public void Run(params string[] arguments)
        {
            var output = RunCapturing(arguments);
            _ = output;
        }

        public void Dispose() => DirectoryCleanup.DeleteRecursively(root);

        private int RunExitCode(params string[] arguments)
        {
            using var process = Start(arguments);
            process.WaitForExit();
            return process.ExitCode;
        }

        private string RunCapturing(params string[] arguments)
        {
            using var process = Start(arguments);
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"git {string.Join(' ', arguments)} exited {process.ExitCode}: {stderr}");
            }

            return stdout;
        }

        private Process Start(IEnumerable<string> arguments)
        {
            var startInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = Repository,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            return Process.Start(startInfo) ?? throw new InvalidOperationException("git could not be started.");
        }
    }
}
