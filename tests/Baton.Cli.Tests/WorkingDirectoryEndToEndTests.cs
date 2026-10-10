using System.Diagnostics;
using System.Text.Json;
using Baton.Vendors;
using Baton.Cli.Tests.TestSupport;
using Baton.Domain;
using Baton.Store;
using Baton.Templates;

namespace Baton.Cli.Tests;

/// <summary>
/// M23 Phase 3's own verification bullet (#272): "a manual run pointing a task at a real directory
/// (tried against both a git repo and a plain folder) confirming file access works" — implemented
/// here as an automated, CI-safe equivalent (the shell-stub adapter, same convention
/// <see cref="RunCommandEndToEndTests"/> already uses, needs no live vendor CLI) rather than left as
/// an actually-manual step. Each test reads a file that already exists in the configured
/// <see cref="WorkerBindingConfigEntry.WorkingDirectory"/> using only its bare relative name — proof
/// the spawned process's own OS-level cwd is genuinely that directory, not merely that
/// <c>BATON_INPUT_&lt;n&gt;</c>/<c>BATON_OUTPUT_DIR</c> (always absolute, unaffected either way) resolved.
/// </summary>
public class WorkingDirectoryEndToEndTests
{
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter> { ["shell"] = new ShellCommandWorkerAdapter() };

    [Fact]
    public async Task A_step_pointed_at_a_plain_folder_reads_a_file_there_by_its_bare_relative_name()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-cwd-plain-{Guid.NewGuid():N}");
        var projectDirectory = Path.Combine(testRoot, "project");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            Directory.CreateDirectory(projectDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(projectDirectory, "notes.txt"), "from-the-real-project-folder", TestContext.Current.CancellationToken);

            await RunAgainstConfiguredDirectoryAsync(testRoot, roomDirectory, projectDirectory);

            var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
            var reader = new FlowEventLogReader(Path.Combine(roomDirectory, "flow.jsonl"));
            var executionId = (await reader.ReadAllAsync(TestContext.Current.CancellationToken))
                .OfType<FlowEvent.ExecutionSucceeded>().Single().ExecutionId;
            var outputPath = Path.Combine(artifactsRoot, $"execution_{executionId}", "output");
            Assert.Equal(
                "from-the-real-project-folder",
                (await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken)).Trim());
        }
        finally
        {
            ForceDeleteDirectory(testRoot);
        }
    }

    [Fact]
    public async Task A_step_pointed_at_a_git_repository_reads_a_file_there_by_its_bare_relative_name()
    {
        // No git-repo requirement (#272's own framing) — this proves a real repo works too, not
        // that one is needed: the .git directory's presence is otherwise irrelevant to cwd wiring.
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-cwd-git-{Guid.NewGuid():N}");
        var projectDirectory = Path.Combine(testRoot, "project");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            Directory.CreateDirectory(projectDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(projectDirectory, "notes.txt"), "from-the-real-git-repo", TestContext.Current.CancellationToken);
            await RunGitAsync(projectDirectory, "init");
            await RunGitAsync(projectDirectory, "add", "notes.txt");
            await RunGitAsync(projectDirectory, "-c", "user.email=test@example.com", "-c", "user.name=Test", "commit", "-m", "seed");

            await RunAgainstConfiguredDirectoryAsync(testRoot, roomDirectory, projectDirectory);

            var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
            var reader = new FlowEventLogReader(Path.Combine(roomDirectory, "flow.jsonl"));
            var executionId = (await reader.ReadAllAsync(TestContext.Current.CancellationToken))
                .OfType<FlowEvent.ExecutionSucceeded>().Single().ExecutionId;
            var outputPath = Path.Combine(artifactsRoot, $"execution_{executionId}", "output");
            Assert.Equal(
                "from-the-real-git-repo", (await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken)).Trim());
        }
        finally
        {
            ForceDeleteDirectory(testRoot);
        }
    }

    [Fact]
    public async Task A_step_with_a_worktree_workspace_runs_in_the_provisioned_tree_which_is_torn_down_on_a_clean_run()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-cwd-worktree-{Guid.NewGuid():N}");
        var repository = Path.Combine(testRoot, "repo");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            Directory.CreateDirectory(repository);
            await File.WriteAllTextAsync(
                Path.Combine(repository, "notes.txt"), "from-the-worktree", TestContext.Current.CancellationToken);
            await RunGitAsync(repository, "init");
            await RunGitAsync(repository, "add", "notes.txt");
            await RunGitAsync(repository, "-c", "user.email=test@example.com", "-c", "user.name=Test", "commit", "-m", "seed");
            await RunGitAsync(repository, "branch", "review-target"); // a ref not checked out in the main tree

            var workflowFilePath = await WriteSingleStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteWorktreeBindingsAsync(testRoot, repository, "review-target");
            var options = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);

            var finalState =
                (await RunCommand.ExecuteAsync(options, Adapters, cancellationToken: TestContext.Current.CancellationToken)).State;

            Assert.Equal(WorkflowStatus.Terminal, finalState.Status);
            FlowAssert.Succeeded(Assert.Single(finalState.Steps));

            // The worker read notes.txt by its bare name — proof its cwd was the provisioned worktree,
            // which the engine created from the ref with nobody checking anything out.
            var reader = new FlowEventLogReader(Path.Combine(roomDirectory, "flow.jsonl"));
            var executionId = (await reader.ReadAllAsync(TestContext.Current.CancellationToken))
                .OfType<FlowEvent.ExecutionSucceeded>().Single().ExecutionId;
            var outputPath = Path.Combine(roomDirectory, "artifacts", $"execution_{executionId}", "output");
            Assert.Equal(
                "from-the-worktree", (await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken)).Trim());

            // A clean Terminal run removes the worktree (the worker wrote only to BATON_OUTPUT_DIR).
            var worktreePath = Path.Combine(roomDirectory, WorktreeWorkspaces.WorkspacesDirectoryName, "reader");
            Assert.False(Directory.Exists(worktreePath), "a clean Terminal run should tear the provisioned worktree down");
        }
        finally
        {
            ForceDeleteDirectory(testRoot);
        }
    }

    private static async Task RunAgainstConfiguredDirectoryAsync(string testRoot, string roomDirectory, string workingDirectory)
    {
        var workflowFilePath = await WriteSingleStepWorkflowAsync(testRoot);
        var bindingsFilePath = await WriteReadRelativeFileBindingsAsync(testRoot, workingDirectory);
        var options = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);

        var finalState = (await RunCommand.ExecuteAsync(options, Adapters)).State;

        Assert.Equal(WorkflowStatus.Terminal, finalState.Status);
        FlowAssert.Succeeded(Assert.Single(finalState.Steps));
    }

    private static async Task<string> WriteSingleStepWorkflowAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("single-cwd-step"),
            1,
            [new WorkflowStepDefinition(new StepId("reader"), "reader", [], ["output"], [], new RetryPolicy(1))]);

        var path = Path.Combine(directory, "workflow.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(definition));
        return path;
    }

    private static async Task<string> WriteReadRelativeFileBindingsAsync(string directory, string workingDirectory)
    {
        Directory.CreateDirectory(directory);
        const string readRelativeFileCommand = "type notes.txt>%BATON_OUTPUT_DIR%\\output";

        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["reader"] = new WorkerBindingConfigEntry(
                "shell",
                new WorkerContract("reader", [], [new ProducedOutput("output")], []),
                readRelativeFileCommand,
                TimeSpan.FromSeconds(30),
                WorkingDirectory: workingDirectory),
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }

    private static async Task<string> WriteWorktreeBindingsAsync(string directory, string repository, string reference)
    {
        Directory.CreateDirectory(directory);
        const string readRelativeFileCommand = "type notes.txt>%BATON_OUTPUT_DIR%\\output";

        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["reader"] = new WorkerBindingConfigEntry(
                "shell",
                new WorkerContract("reader", [], [new ProducedOutput("output")], []),
                readRelativeFileCommand,
                TimeSpan.FromSeconds(30),
                Worktree: new WorktreeWorkspace(repository, reference)),
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }

    private static async Task RunGitAsync(string workingDirectory, params string[] args)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = global::Baton.Core.ProcessLaunch.Start(startInfo)!;
        var (_, stderr) = await BoundedProcessWait.RunToExitAsync(process, TimeSpan.FromSeconds(30));
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stderr}");
    }

    /// <summary>
    /// <c>git init</c>/<c>commit</c> leaves committed object files read-only by design — harmless on
    /// Unix (directory-write permission is what governs deletion there), but Windows' own
    /// <see cref="Directory.Delete(string, bool)"/> refuses to remove a read-only file regardless of
    /// its parent directory's permissions, throwing <see cref="UnauthorizedAccessException"/>. Clears
    /// the attribute on every file first so cleanup succeeds on every OS this test's git-repo variant runs on.
    /// </summary>
    private static void ForceDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        DirectoryCleanup.DeleteRecursively(path);
    }
}
