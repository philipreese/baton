using System.Diagnostics;
using Baton.Tests.Shared;
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
    public void Spoofing_only_the_local_tracking_ref_is_not_safe()
    {
        using var fixture = new GraceRepository();
        var checkpoint = fixture.Capture();
        fixture.Commit("grace child");

        fixture.Run("update-ref", fixture.TrackingRef, fixture.Head);

        Assert.False(WorktreeProvisioner.IsSafeGraceCheckpoint(fixture.Repository, checkpoint));
        Assert.Equal(checkpoint.Head, fixture.RevParse("HEAD~1"));
    }

    [Fact]
    public void An_actual_remote_rewrite_is_not_safe()
    {
        using var fixture = new GraceRepository();
        var checkpoint = fixture.Capture();
        fixture.Commit("grace child");
        fixture.Push();
        fixture.UpdateRemoteReference(checkpoint.MergeRef, checkpoint.RemoteTip);

        Assert.False(WorktreeProvisioner.IsSafeGraceCheckpoint(fixture.Repository, checkpoint));
        Assert.Equal(fixture.Head, fixture.RevParse(fixture.TrackingRef));
    }

    [Fact]
    public void Branch_remote_configuration_drift_is_not_safe()
    {
        using var fixture = new GraceRepository();
        var checkpoint = fixture.Capture();
        fixture.Commit("grace child");
        fixture.Push();
        fixture.Run("config", $"branch.{fixture.BranchName}.remote", "replacement");

        Assert.False(WorktreeProvisioner.IsSafeGraceCheckpoint(fixture.Repository, checkpoint));
    }

    [Fact]
    public void Redirecting_the_remote_name_and_pushing_elsewhere_is_not_safe()
    {
        using var fixture = new GraceRepository();
        var checkpoint = fixture.Capture();
        var divertedRemote = fixture.CreateDistinctBareRemote("diverted.git");
        fixture.Commit("grace child");
        fixture.Run("config", $"remote.{fixture.RemoteName}.url", divertedRemote);
        fixture.Push();

        Assert.False(WorktreeProvisioner.IsSafeGraceCheckpoint(fixture.Repository, checkpoint));
        Assert.Equal(checkpoint.RemoteTip, fixture.ReadRemoteReference(checkpoint.MergeRef));
    }

    [Fact]
    public void A_dot_remote_or_named_remote_pointing_at_this_repository_is_not_safe()
    {
        using var fixture = new GraceRepository();
        fixture.Run("config", $"branch.{fixture.BranchName}.remote", ".");
        Assert.Null(WorktreeProvisioner.CaptureGraceCheckpoint(fixture.Repository));

        fixture.Run("config", $"branch.{fixture.BranchName}.remote", fixture.RemoteName);
        fixture.Run("config", $"remote.{fixture.RemoteName}.url", fixture.Repository);
        Assert.Null(WorktreeProvisioner.CaptureGraceCheckpoint(fixture.Repository));

        fixture.Run("config", $"remote.{fixture.RemoteName}.url", fixture.Remote);
        var checkpoint = fixture.Capture();
        fixture.Commit("unpublished child");
        fixture.Run("config", $"remote.{fixture.RemoteName}.url", fixture.Repository);

        Assert.False(WorktreeProvisioner.IsSafeGraceCheckpoint(fixture.Repository, checkpoint));
    }

    [Fact]
    public async Task A_cancelled_remote_probe_returns_no_proof_without_mutating_the_repository()
    {
        using var fixture = new GraceRepository();
        var originalHead = fixture.Head;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Null(await WorktreeProvisioner.CaptureGraceCheckpointAsync(fixture.Repository, cancelled.Token));
        Assert.Equal(originalHead, fixture.Head);
        Assert.True(fixture.IsAncestor(originalHead, fixture.TrackingRef));
    }

    [Fact]
    public async Task A_refused_remote_probe_returns_no_proof_without_mutating_the_repository()
    {
        using var fixture = new GraceRepository();
        var originalHead = fixture.Head;
        using var probe = WorktreeProvisioner.BeginGraceRemoteProbeProgramScope("missing-grace-remote-probe");

        Assert.Null(await WorktreeProvisioner.CaptureGraceCheckpointAsync(fixture.Repository, CancellationToken.None));
        Assert.Equal(originalHead, fixture.Head);
        Assert.True(fixture.IsAncestor(originalHead, fixture.TrackingRef));
    }

    [Fact]
    public async Task A_hung_remote_probe_is_timed_out_and_its_process_tree_is_reaped_without_mutating_the_repository()
    {
        using var fixture = new GraceRepository();
        var originalHead = fixture.Head;
        var signalPath = Path.Combine(Path.GetTempPath(), $"grace-remote-probe-{Guid.NewGuid():N}.txt");
        var temporarySignalPath = signalPath + ".tmp";
        var timeout = TimeSpan.FromSeconds(1);
        var command = $"$child = Start-Process -FilePath powershell.exe -ArgumentList '-NoLogo','-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 30' -PassThru; [IO.File]::WriteAllText('{temporarySignalPath.Replace("'", "''")}', \"$PID,$($child.Id)\"); [IO.File]::Move('{temporarySignalPath.Replace("'", "''")}', '{signalPath.Replace("'", "''")}'); Start-Sleep -Seconds 30";
        Task<GraceCheckpoint?>? capture = null;
        Process? helper = null;
        Process? child = null;
        Exception? primaryFailure = null;
        try
        {
            using var probe = WorktreeProvisioner.BeginGraceRemoteProbeScope(
                timeout, "powershell.exe", "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", command);
            var stopwatch = Stopwatch.StartNew();
            capture = WorktreeProvisioner.CaptureGraceCheckpointAsync(fixture.Repository, CancellationToken.None);
            (helper, child) = await ReadProbeProcessIdsAsync(signalPath, TimeSpan.FromSeconds(5));

            Assert.False(helper.HasExited);
            Assert.False(child.HasExited);
            Assert.Null(await capture);
            stopwatch.Stop();

            Assert.InRange(stopwatch.Elapsed, timeout, TimeSpan.FromSeconds(10));
            Assert.True(helper.HasExited);
            Assert.True(child.HasExited);
            Assert.Equal(originalHead, fixture.Head);
            Assert.True(fixture.IsAncestor(originalHead, fixture.TrackingRef));
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            throw;
        }
        finally
        {
            try
            {
                if (capture is not null)
                {
                    await capture;
                }
            }
            catch when (primaryFailure is not null)
            {
                // Preserve the assertion or startup failure while still observing cleanup.
            }
            finally
            {
                try
                {
                    helper?.Dispose();
                    child?.Dispose();
                    FileCleanup.Delete(signalPath);
                    FileCleanup.Delete(temporarySignalPath);
                }
                catch when (primaryFailure is not null)
                {
                    // Preserve the assertion or startup failure if releasing a retained handle fails.
                }
            }
        }
    }

    [Fact]
    public void A_branch_without_a_configured_remote_cannot_capture_a_grace_checkpoint()
    {
        using var fixture = new GraceRepository();
        fixture.Run("config", "--unset-all", $"branch.{fixture.BranchName}.remote");

        Assert.Null(WorktreeProvisioner.CaptureGraceCheckpoint(fixture.Repository));
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
        Assert.True(fixture.IsAncestor(checkpoint.Head, fixture.TrackingRef));
    }

    private sealed class GraceRepository : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), $"grace-{Guid.NewGuid():N}");

        public GraceRepository()
        {
            Repository = Path.Combine(root, "repo");
            Directory.CreateDirectory(Repository);
            TempGitRepository.InitWithEverythingCommitted(Repository);
            Remote = TempGitRepository.InitBareRepository(Path.Combine(root, "origin.git"));
            TempGitRepository.AddRemote(Repository, "origin", Remote);
            TempGitRepository.Push(Repository, "origin", "HEAD:refs/heads/main");
            Run("branch", "--set-upstream-to", "origin/main");
        }

        public string Repository { get; }
        public string Remote { get; }
        public string RemoteName => "origin";
        public string TrackingRef => "refs/remotes/origin/main";
        public string BranchName => RunCapturing("branch", "--show-current").Trim();
        public string Head => RevParse("HEAD");

        public GraceCheckpoint Capture() => Assert.IsType<GraceCheckpoint>(WorktreeProvisioner.CaptureGraceCheckpoint(Repository));

        public void Commit(string message)
        {
            File.WriteAllText(Path.Combine(Repository, $"{Guid.NewGuid():N}.txt"), message);
            Run("add", "-A");
            Run("commit", "-m", message);
        }

        public void Push() => Run("push", "origin", "HEAD:refs/heads/main");
        public void UpdateRemoteReference(string reference, string target) => Run("--git-dir", Remote, "update-ref", reference, target);
        public string CreateDistinctBareRemote(string name) => TempGitRepository.InitBareRepository(Path.Combine(root, name));
        public string ReadRemoteReference(string reference) =>
            RunCapturing("--git-dir", Remote, "rev-parse", "--verify", $"{reference}^{{commit}}").Trim();
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

    private static async Task<(Process Helper, Process Child)> ReadProbeProcessIdsAsync(string signalPath, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!File.Exists(signalPath))
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The hung remote probe did not report its process tree.");
            }

            // wait-ok: this bounded startup-signal poll prevents a process-observation race; it is not an assertion delay.
            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }

        var ids = (await File.ReadAllTextAsync(signalPath)).Split(',');
        Assert.Equal(2, ids.Length);
        Assert.True(int.TryParse(ids[0], out var helperPid) && helperPid > 0, "The probe root PID was invalid.");
        Assert.True(int.TryParse(ids[1], out var childPid) && childPid > 0, "The probe child PID was invalid.");
        return (Process.GetProcessById(helperPid), Process.GetProcessById(childPid));
    }
}
