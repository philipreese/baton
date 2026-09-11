using System.Diagnostics;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Outcomes;
using Baton.Tests.Shared;
using Baton.Workspaces;

namespace Baton.Tests.Outcomes;

/// <summary>
/// #1622 (b)/#1390: <see cref="OutcomeClassifier.Classify"/>'s <c>changesTree</c> parameter is what
/// turns the work-product evidence spec/baton.md §3 specifies on; these tests exercise it against a
/// real git worktree, the same discipline <see cref="Workspaces.WorktreeProvisionerTests"/> uses,
/// since a fake would not discriminate a git status/rev-list probe.
/// </summary>
public sealed class OutcomeClassifierWorkProductTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "baton-outcome-workproduct-" + Guid.NewGuid().ToString("N"));

    public OutcomeClassifierWorkProductTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void A_tree_changing_role_with_no_diff_and_no_declared_outputs_settles_hollow_true()
    {
        var (worktree, outputDirectory) = ProvisionUntouchedWorktree();
        var contract = new WorkerContract("implement", [], [], []);

        var classification = OutcomeClassifier.Classify(
            new CoreDispatchResult(0, CoreExitReason.Natural, "asked for approval; nothing answered"),
            contract, outputDirectory, changesTreeWorkingDirectory: worktree, changesTree: true);

        Assert.Equal(OutcomeVerdict.Succeeded, classification.Verdict);
        Assert.False(classification.WorkspaceChanged);
        Assert.True(classification.Hollow);
        Assert.NotNull(classification.HollowReason);
    }

    [Fact]
    public void A_tree_changing_role_with_a_diff_settles_workspaceChanged_true_and_hollow_false()
    {
        var (worktree, outputDirectory) = ProvisionUntouchedWorktree();
        File.WriteAllText(Path.Combine(worktree, "new-file.txt"), "real work");
        var contract = new WorkerContract("implement", [], [], []);

        var classification = OutcomeClassifier.Classify(
            new CoreDispatchResult(0, CoreExitReason.Natural, null),
            contract, outputDirectory, changesTreeWorkingDirectory: worktree, changesTree: true);

        Assert.Equal(OutcomeVerdict.Succeeded, classification.Verdict);
        Assert.True(classification.WorkspaceChanged);
        Assert.False(classification.Hollow);
        Assert.Null(classification.HollowReason);
    }

    /// <summary>
    /// The polarity control for the hollow test above: an untouched worktree alone is not enough for
    /// <c>hollow: true</c> when the contract DOES declare an output (every shipped catalog role but a
    /// bespoke zero-output one) — see <c>OutcomeClassifier.BuildSucceededClassification</c>'s own
    /// remarks for why. <c>workspaceChanged</c> still reads false; only <c>hollow</c> differs.
    /// </summary>
    [Fact]
    public void A_tree_changing_role_with_no_diff_but_a_declared_output_settles_workspaceChanged_false_and_hollow_false()
    {
        var (worktree, outputDirectory) = ProvisionUntouchedWorktree();
        File.WriteAllText(Path.Combine(outputDirectory, "changes.md"), "nothing changed");
        var contract = new WorkerContract("implement", [], [new ProducedOutput("changes.md")], []);

        var classification = OutcomeClassifier.Classify(
            new CoreDispatchResult(0, CoreExitReason.Natural, null),
            contract, outputDirectory, changesTreeWorkingDirectory: worktree, changesTree: true);

        Assert.Equal(OutcomeVerdict.Succeeded, classification.Verdict);
        Assert.False(classification.WorkspaceChanged);
        Assert.False(classification.Hollow);
    }

    /// <summary>
    /// #1622 (b): the review-role field-absence arm -- same fixture shape as the hollow test above,
    /// <c>changesTree: false</c> (what every non-tree-changing role gets). Must read null, not false,
    /// so status --json omits the field rather than asserting "unchanged" about a role that was never
    /// tree-changing to begin with.
    /// </summary>
    [Fact]
    public void A_non_tree_changing_role_never_computes_workspaceChanged_or_hollow()
    {
        var (worktree, outputDirectory) = ProvisionUntouchedWorktree();
        var contract = new WorkerContract("review", [], [], []);

        var classification = OutcomeClassifier.Classify(
            new CoreDispatchResult(0, CoreExitReason.Natural, null),
            contract, outputDirectory, changesTreeWorkingDirectory: worktree, changesTree: false);

        Assert.Equal(OutcomeVerdict.Succeeded, classification.Verdict);
        Assert.Null(classification.WorkspaceChanged);
        Assert.Null(classification.Hollow);
        Assert.Null(classification.HollowReason);
    }

    /// <summary>Pins that the room word stays "Succeeded" even in the strongest hollow case (spec/baton.md §3).</summary>
    [Fact]
    public void Hollow_success_does_not_reclassify_the_verdict()
    {
        var (worktree, outputDirectory) = ProvisionUntouchedWorktree();
        var contract = new WorkerContract("implement", [], [], []);

        var classification = OutcomeClassifier.Classify(
            new CoreDispatchResult(0, CoreExitReason.Natural, null),
            contract, outputDirectory, changesTreeWorkingDirectory: worktree, changesTree: true);

        Assert.Equal(OutcomeVerdict.Succeeded, classification.Verdict);
        Assert.True(classification.Hollow);
    }

    [Fact]
    public void A_write_granted_lane_with_a_measured_zero_write_calls_and_an_unchanged_tree_fails()
    {
        var (worktree, outputDirectory) = ProvisionUntouchedWorktree();
        File.WriteAllText(Path.Combine(outputDirectory, "changes.md"), "claimed completion");
        var contract = new WorkerContract("implement", [], [new ProducedOutput("changes.md")], []);

        var classification = OutcomeClassifier.Classify(
            new CoreDispatchResult(0, CoreExitReason.Natural),
            contract,
            outputDirectory,
            changesTreeWorkingDirectory: worktree,
            changesTree: true,
            writeToolCallCount: 0);

        Assert.Equal(OutcomeVerdict.Failed, classification.Verdict);
        Assert.Equal(FailureClassification.Permanent, classification.FailureClassification);
        Assert.Contains("zero write-tool calls", classification.Reason!, StringComparison.Ordinal);
        Assert.False(classification.WorkspaceChanged);
    }

    [Fact]
    public void An_artifact_only_lane_with_measured_zero_write_calls_and_an_unchanged_tree_succeeds()
    {
        var (worktree, outputDirectory) = ProvisionUntouchedWorktree();
        File.WriteAllText(Path.Combine(outputDirectory, "report.md"), "measured result");
        var contract = new WorkerContract("measure", [], [new ProducedOutput("report.md")], []);

        var classification = OutcomeClassifier.Classify(
            new CoreDispatchResult(0, CoreExitReason.Natural),
            contract,
            outputDirectory,
            changesTreeWorkingDirectory: worktree,
            changesTree: true,
            writeToolCallCount: 0,
            verifiesWorkspace: false);

        Assert.Equal(OutcomeVerdict.Succeeded, classification.Verdict);
        Assert.False(classification.WorkspaceChanged);
        Assert.False(classification.Hollow);
    }

    [Fact]
    public void An_unchanged_tree_with_a_write_call_retains_the_existing_hollow_success_reading()
    {
        // The polarity control above: only the measured count changes. The conjunction, not an
        // unchanged tree by itself, is the failure signature approved in #2131.
        var (worktree, outputDirectory) = ProvisionUntouchedWorktree();
        File.WriteAllText(Path.Combine(outputDirectory, "changes.md"), "truthful no-op result");
        var contract = new WorkerContract("implement", [], [new ProducedOutput("changes.md")], []);

        var classification = OutcomeClassifier.Classify(
            new CoreDispatchResult(0, CoreExitReason.Natural),
            contract,
            outputDirectory,
            changesTreeWorkingDirectory: worktree,
            changesTree: true,
            writeToolCallCount: 1);

        Assert.Equal(OutcomeVerdict.Succeeded, classification.Verdict);
        Assert.False(classification.WorkspaceChanged);
        Assert.False(classification.Hollow);
    }

    /// <summary>
    /// A real git repository with one commit. Returns the repo path and the branch to provision a
    /// worktree of, mirroring <c>Workspaces.WorktreeProvisionerTests.CreateRepoWithBranch</c>.
    /// </summary>
    private (string Repository, string Reference) CreateRepoWithBranch(string committedFileName)
    {
        var repo = NewDir("repo");
        RunGit(repo, "init");
        RunGit(repo, "config", "user.email", "test@example.com");
        RunGit(repo, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(repo, committedFileName), "committed content");
        RunGit(repo, "add", ".");
        RunGit(repo, "commit", "-m", "initial");
        RunGit(repo, "branch", "worker-target");
        return (repo, "worker-target");
    }

    /// <summary>
    /// Provisions a real, freshly-checked-out worktree (no diff, no commits over base) plus a fresh
    /// output directory — the shared setup every test in this class starts from, mutating the worktree
    /// or the output directory afterward as its own fixture shape needs.
    /// </summary>
    private (string Worktree, string OutputDirectory) ProvisionUntouchedWorktree()
    {
        var (repo, reference) = CreateRepoWithBranch("committed.txt");
        var worktree = Path.Combine(NewDir("task"), "workspace");
        WorktreeProvisioner.Provision(worktree, repo, reference);
        var outputDirectory = NewDir("output");
        return (worktree, outputDirectory);
    }

    private string NewDir(string name)
    {
        var path = Path.Combine(_root, $"{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void RunGit(string workingDirectory, params string[] args)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)!;
        var stderr = process.StandardError.ReadToEndAsync();
        _ = process.StandardOutput.ReadToEndAsync();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stderr.Result}");
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        DirectoryCleanup.DeleteRecursively(_root);
    }
}
