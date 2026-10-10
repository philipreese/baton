using Baton.Core;
using Baton.Artifacts;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Outcomes;
using Baton.Store;
using Baton.Tests.Shared;
using System.Diagnostics;
using Xunit;

namespace Baton.Vendors.Tests;

/// <summary>
/// Unit and integration tests for <see cref="CommandWorkerAdapter"/> (issue #887 stage 2 slice 1).
/// Verifies deterministic command execution without shell interpretation, stdout output artifact capture,
/// non-zero exit failure polarity, loud missing binary failure, and a git diff-shaped smoke case.
/// </summary>
public class CommandWorkerAdapterTests
{
    private static readonly CommandWorkerAdapter Adapter = new();

    [Fact]
    public void Resolve_parses_argv_json_and_sets_program_args_and_stdout_artifact()
    {
        var contract = new WorkerContract("cmd", [], [new ProducedOutput("output.txt")], []);
        var invocation = new WorkerInvocation("[\"git\", \"status\", \"--short\"]", WorkingDirectory: "/tmp/repo");

        var target = Adapter.Resolve(invocation, contract);

        Assert.Equal("git", target.Program);
        Assert.Equal(["status", "--short"], target.Args);
        Assert.Equal("/tmp/repo", target.WorkingDirectory);
        Assert.Equal("output.txt", target.StdoutArtifactName);
    }

    [Fact]
    public void Resolve_empty_or_invalid_prompt_template_throws()
    {
        var contract = new WorkerContract("cmd", [], [], []);

        Assert.Throws<InvalidOperationException>(() => Adapter.Resolve(new WorkerInvocation(""), contract));
        Assert.Throws<InvalidOperationException>(() => Adapter.Resolve(new WorkerInvocation("[invalid json"), contract));
        Assert.Throws<InvalidOperationException>(() => Adapter.Resolve(new WorkerInvocation("[]"), contract));
        Assert.Throws<InvalidOperationException>(() => Adapter.Resolve(new WorkerInvocation("[\"git\", null]"), contract));
        Assert.Throws<InvalidOperationException>(() => Adapter.Resolve(new WorkerInvocation("[\"git\", \" \"]"), contract));
    }

    [Fact]
    public async Task HappyPath_argv_runs_stdout_becomes_named_artifact()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "baton_cmd_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var logPath = Path.Combine(tempDir, "flow.jsonl");
        try
        {
            var contract = new WorkerContract("cmd", [], [new ProducedOutput("out.txt")], []);
            var prompt = "[\"dotnet\", \"--version\"]";
            var target = Adapter.Resolve(new WorkerInvocation(prompt), contract);

            await using var writer = new FlowEventLogWriter(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);

            var request = MakeRequest("exec1", tempDir, ["out.txt"]);

            var result = await dispatcher.DispatchAsync(request, target, TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(CoreExitReason.Natural, result.Reason);

            var artifactPath = Path.Combine(tempDir, "out.txt");
            Assert.True(File.Exists(artifactPath), "Artifact file should exist.");
            var content = await File.ReadAllTextAsync(artifactPath, TestContext.Current.CancellationToken);
            Assert.NotEmpty(content);

            var classification = OutcomeClassifier.Classify(result, contract, tempDir);
            Assert.Equal(OutcomeVerdict.Succeeded, classification.Verdict);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempDir);
        }
    }

    [Fact]
    public async Task FailurePolarity_nonzero_exit_returns_failed_verdict()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "baton_cmd_fail_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var logPath = Path.Combine(tempDir, "flow.jsonl");
        try
        {
            var contract = new WorkerContract("cmd", [], [new ProducedOutput("out.txt")], []);
            // Run git with an invalid subcommand to guarantee non-zero exit code
            var prompt = "[\"git\", \"invalid-subcommand-12345\"]";
            var target = Adapter.Resolve(new WorkerInvocation(prompt), contract);

            await using var writer = new FlowEventLogWriter(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);

            var request = MakeRequest("exec1", tempDir, ["out.txt"]);

            var result = await dispatcher.DispatchAsync(request, target, TestContext.Current.CancellationToken);

            Assert.NotEqual(0, result.ExitCode);

            var classification = OutcomeClassifier.Classify(result, contract, tempDir);
            Assert.Equal(OutcomeVerdict.Failed, classification.Verdict);
            Assert.NotNull(classification.Reason);
            Assert.Contains("non-zero code", classification.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempDir);
        }
    }

    [Fact]
    public async Task MissingBinary_throws_or_fails_loudly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "baton_cmd_missing_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var logPath = Path.Combine(tempDir, "flow.jsonl");
        try
        {
            var contract = new WorkerContract("cmd", [], [new ProducedOutput("out.txt")], []);
            var prompt = "[\"non_existent_binary_xyz_98765\"]";
            var target = Adapter.Resolve(new WorkerInvocation(prompt), contract);

            await using var writer = new FlowEventLogWriter(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);

            var request = MakeRequest("exec1", tempDir, ["out.txt"]);

            var ex = await Assert.ThrowsAsync<BatonException>(
                async () => await dispatcher.DispatchAsync(request, target, TestContext.Current.CancellationToken));
            // BatonProcessRunner's own SpawnFailed wrapper text (#1474) -- stable regardless of the
            // OS-specific message Win32Exception carries ("The system cannot find the file
            // specified.", etc.), the same reason the deleted Rust wrapper text this replaced was
            // pinned rather than the raw OS message it wrapped.
            Assert.Contains("Failed to start", ex.Message);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempDir);
        }
    }

    [Fact]
    public async Task GitDiff_shaped_smoke_case_in_temp_repo_fixture()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "baton_cmd_gitrepo_" + Guid.NewGuid().ToString("N"));
        var repoDir = Path.Combine(tempDir, "repo");
        var outputDir = Path.Combine(tempDir, "output");
        var logPath = Path.Combine(outputDir, "flow.jsonl");
        Directory.CreateDirectory(repoDir);
        Directory.CreateDirectory(outputDir);

        try
        {
            // Initialize git repo fixture
            RunProcess("git", "init", repoDir);
            RunProcess("git", "config user.name TestUser", repoDir);
            RunProcess("git", "config user.email test@example.com", repoDir);

            var file1 = Path.Combine(repoDir, "file.txt");
            await File.WriteAllTextAsync(file1, "line 1\n", TestContext.Current.CancellationToken);
            RunProcess("git", "add file.txt", repoDir);
            RunProcess("git", "commit -m initial", repoDir);

            // Make an uncommitted change
            await File.AppendAllTextAsync(file1, "line 2\n", TestContext.Current.CancellationToken);

            var contract = new WorkerContract("capture", [], [new ProducedOutput("branch.diff")], []);
            var prompt = "[\"git\", \"diff\", \"HEAD\"]";
            var target = Adapter.Resolve(new WorkerInvocation(prompt, WorkingDirectory: repoDir), contract);

            await using var writer = new FlowEventLogWriter(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);

            var request = MakeRequest("exec1", outputDir, ["branch.diff"]);

            var result = await dispatcher.DispatchAsync(request, target, TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);

            var diffPath = Path.Combine(outputDir, "branch.diff");
            Assert.True(File.Exists(diffPath), "branch.diff should exist");
            var diffText = await File.ReadAllTextAsync(diffPath, TestContext.Current.CancellationToken);
            Assert.Contains("+line 2", diffText);

            var classification = OutcomeClassifier.Classify(result, contract, outputDir);
            Assert.Equal(OutcomeVerdict.Succeeded, classification.Verdict);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempDir);
        }
    }

    [Fact]
    public async Task Empty_stdout_success_still_creates_the_declared_artifact_and_satisfies_the_contract()
    {
        // #887 review (medium finding): zero stdout chunks must still leave the declared artifact
        // on disk, verdict Succeeded. CoreDispatcher's eager-create comment carries the full why.
        var tempDir = Path.Combine(Path.GetTempPath(), "baton_cmd_empty_" + Guid.NewGuid().ToString("N"));
        var repoDir = Path.Combine(tempDir, "repo");
        var outputDir = Path.Combine(tempDir, "output");
        var logPath = Path.Combine(outputDir, "flow.jsonl");
        Directory.CreateDirectory(repoDir);
        Directory.CreateDirectory(outputDir);

        try
        {
            RunProcess("git", "init", repoDir);
            RunProcess("git", "config user.name TestUser", repoDir);
            RunProcess("git", "config user.email test@example.com", repoDir);
            var file1 = Path.Combine(repoDir, "file.txt");
            await File.WriteAllTextAsync(file1, "line 1\n", TestContext.Current.CancellationToken);
            RunProcess("git", "add file.txt", repoDir);
            RunProcess("git", "commit -m initial", repoDir);

            // Clean tree: `git diff HEAD` exits 0 with zero bytes of stdout.
            var contract = new WorkerContract("cmd", [], [new ProducedOutput("empty.diff")], []);
            var target = Adapter.Resolve(
                new WorkerInvocation("[\"git\", \"diff\", \"HEAD\"]", WorkingDirectory: repoDir), contract);

            await using var writer = new FlowEventLogWriter(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);
            var request = MakeRequest("exec1", outputDir, ["empty.diff"]);

            var result = await dispatcher.DispatchAsync(request, target, TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            var artifactPath = Path.Combine(outputDir, "empty.diff");
            Assert.True(File.Exists(artifactPath), "The declared artifact must exist even when stdout was empty.");
            Assert.Equal(0, new FileInfo(artifactPath).Length);

            var classification = OutcomeClassifier.Classify(result, contract, outputDir);
            Assert.Equal(OutcomeVerdict.Succeeded, classification.Verdict);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempDir);
        }
    }

    private static ExecutionRequest MakeRequest(string execId, string outputDir, IReadOnlyList<string> outputs)
    {
        return new ExecutionRequest(
            new ExecutionId(execId),
            new WorkflowId("wf-1"),
            new StepId("step-1"),
            "cmd",
            Inputs: [],
            Outputs: outputs,
            Timeout: TimeSpan.FromMinutes(5),
            Environment: [new EnvironmentVariable.BatonComputed("BATON_OUTPUT_DIR", outputDir)],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());
    }

    private static void RunProcess(string program, string args, string cwd)
    {
        var psi = new ProcessStartInfo(program, args)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var proc = global::Baton.Core.ProcessLaunch.Start(psi);
        proc!.WaitForExit();
        Assert.Equal(0, proc.ExitCode);
    }
}
