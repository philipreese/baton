using System.Diagnostics;
using Baton.Artifacts;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Mutation;
using Baton.Store;
using Baton.Tests.TestSupport;
using Baton.Workspaces;

namespace Baton.Tests.Mutation;

/// <summary>
/// #1933: a classification produced on the CRASH-RECOVERY path must subtract AER's own dispatch-time
/// writes from the worker's work-product evidence, exactly as the live path does — see
/// <see cref="FlowEvent.EngineFilesPlaced"/>'s own remarks for the mechanism (the journaled fact, read
/// back through the projection). Hand-authored log lines against a real provisioned worktree, the same
/// fixture style as <see cref="CrashRecoveredTimeoutMutationBaseTests"/>.
/// </summary>
/// <remarks>
/// The control arm is asserted first and is the whole point: WITHOUT the journaled fact the same tree,
/// the same recorded exit, and the same untracked <c>.claude/skills/</c> files must read
/// <c>workspaceChanged: true</c> — which is the defect this closes, and which a fixture that forgot to
/// put the files on disk could not produce. Only then does the second arm's <c>false</c> mean anything.
/// </remarks>
public class CrashRecoveredEnginePlacedPathsTests
{
    private static readonly StepId Implement = new("implement");
    private static readonly WorkerContract Contract = new("skill-worker", [], [], []);

    [Fact]
    public async Task A_crash_recovered_exit_with_no_placement_fact_counts_the_projection_as_the_workers_work()
    {
        var run = await RunAsync(journalPlacement: false);
        try
        {
            var stepState = run.FinalState.Steps.Single(s => s.StepId == Implement);

            Assert.Equal(StepStatus.Succeeded, stepState.Status);
            Assert.True(stepState.WorkspaceChanged);
            Assert.False(stepState.Hollow);
        }
        finally
        {
            run.Cleanup();
        }
    }

    [Fact]
    public async Task A_crash_recovered_exit_subtracts_the_paths_the_room_records_AER_itself_placed()
    {
        var run = await RunAsync(journalPlacement: true);
        try
        {
            var stepState = run.FinalState.Steps.Single(s => s.StepId == Implement);

            Assert.Equal(StepStatus.Succeeded, stepState.Status);
            Assert.False(stepState.WorkspaceChanged);
            Assert.True(stepState.Hollow);
        }
        finally
        {
            run.Cleanup();
        }
    }

    private sealed record CrashRun(FlowState FinalState, Action Cleanup);

    private static async Task<CrashRun> RunAsync(bool journalPlacement)
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var origin = Path.Combine(roomDirectory, "origin");
        var workspace = Path.Combine(roomDirectory, "workspace");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");

        // A provisioned worktree rather than a plain checkout, for the reason
        // WorktreeProvisionerTests.Both_workspace_readers_subtract_paths_AER_itself_placed states in
        // full: a plain checkout cannot produce the measured `workspaceChanged: false` this arm asserts.
        Directory.CreateDirectory(origin);
        RunGit(origin, "init");
        RunGit(origin, "config", "user.email", "test@example.com");
        RunGit(origin, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(origin, "seeded.txt"), "base");
        RunGit(origin, "add", ".");
        RunGit(origin, "commit", "-m", "base");
        var reference = RunGitCapture(origin, "rev-parse", "HEAD").Trim();
        WorktreeProvisioner.Provision(workspace, origin, reference);

        // What the claude adapter's canonical-skill projection actually leaves behind: untracked files
        // under <workspace>/.claude/skills/<name>/, ABSOLUTE paths in the journaled fact (#1151).
        var projectedDirectory = Path.Combine(workspace, ".claude", "skills", "x");
        Directory.CreateDirectory(Path.Combine(projectedDirectory, "reference"));
        var placedPaths = new[]
        {
            Path.Combine(projectedDirectory, "SKILL.md"),
            Path.Combine(projectedDirectory, "reference", "notes.md"),
        };
        foreach (var path in placedPaths)
        {
            File.WriteAllText(path, "projected content");
        }

        // Each fact carries the digest of the bytes as placed, exactly as CoreDispatcher journals it —
        // subtraction is conditional on the file still holding them (EnginePlacedFile).
        EnginePlacedFile[] placed = [.. placedPaths.Select(p => new EnginePlacedFile(p, EnginePlacedFile.TryDigest(p)))];

        var workflowId = new WorkflowId("wf-1933");
        var snapshot = new WorkflowDefinitionSnapshot(
            new WorkflowDefinitionSnapshotId("snapshot-1933"),
            new WorkflowTemplateId("template-1933"),
            WorkflowTemplateVersion: 1,
            Steps:
            [
                new WorkflowStepDefinition(
                    Implement, "skill-worker", Inputs: [], Outputs: [], DependsOn: [],
                    RetryPolicy: new RetryPolicy(MaxAttempts: 1, Backoff: BackoffPolicy.None)),
            ]);
        var target = new CoreDispatchTarget("skill-worker-cli", [], WorkingDirectory: workspace);
        var bindings = new Dictionary<string, WorkerBinding>
        {
            // The real shape of a tree-changing role: ChangesTree true, IsWorktree false (F4 —
            // WorkerBinding.Process.IsWorktree's own remarks), so classification reads
            // changesTreeWorkingDirectory with no base ref of its own.
            ["skill-worker"] = new WorkerBinding.Process(
                Contract, target, TimeSpan.FromMinutes(60), ChangesTree: true),
        };

        ExecutionId executionId;
        await using (var writer = new FlowEventLogWriter(logPath))
        {
            executionId = await AcceptRequestAsync(writer, workflowId, artifactsRoot, Implement);

            // Before CoreEvent.ExecutionStarted below, which is the ordering production now produces:
            // CoreDispatcher raises CoreDispatchTarget.OnEngineFilesPlaced at placement time and awaits
            // the append before spawning (#1929 review round 3, LOW). Pinned as an ordering by
            // MutationInterfaceEngineFilesPlacedOrderingTests, not asserted here — this fixture is about
            // the reader, which is order-independent.
            if (journalPlacement)
            {
                await writer.AppendAsync(
                    new FlowEvent.EngineFilesPlaced(executionId, placed, ["x"]),
                    TestContext.Current.CancellationToken);
            }

            // The crash window: the worker really ran and really exited 0, and Flow went down before it
            // could classify that exit.
            await writer.AppendAsync(new CoreEvent.ExecutionStarted(executionId, Pid: 4343), TestContext.Current.CancellationToken);
            await writer.AppendAsync(
                new CoreEvent.ExecutionExited(executionId, ExitCode: 0, CoreExitReason.Natural), TestContext.Current.CancellationToken);
        }

        await using var recoveryWriter = new FlowEventLogWriter(logPath);
        var reader = new FlowEventLogReader(logPath);
        var stub = new StubCoreDispatcher();

        var finalState = await MutationInterface.StartWorkflowAsync(
            workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, recoveryWriter, stub,
            cancellationToken: TestContext.Current.CancellationToken);

        return new CrashRun(finalState, () => DirectoryCleanup.DeleteRecursively(roomDirectory));
    }

    private static async Task<ExecutionId> AcceptRequestAsync(
        FlowEventLogWriter writer, WorkflowId workflowId, string artifactsRoot, StepId stepId)
    {
        var executionId = new ExecutionId(Guid.NewGuid().ToString("n"));
        var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, executionId);
        var request = new ExecutionRequest(
            executionId,
            workflowId,
            stepId,
            "skill-worker",
            Inputs: [],
            Outputs: [],
            TimeSpan.FromMinutes(60),
            ArtifactManager.BuildEnvironment([], outputDirectory, artifactsRoot),
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

        await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(request), TestContext.Current.CancellationToken);
        return executionId;
    }

    private static string RunGitCapture(string workingDirectory, params string[] args)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("git could not be started.");
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
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

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("git could not be started.");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} failed ({process.ExitCode}): {process.StandardError.ReadToEnd()}");
        }
    }
}
