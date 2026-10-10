using System.Diagnostics;
using Baton.Artifacts;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Mutation;
using Baton.Store;
using Baton.Tests.TestSupport;

namespace Baton.Tests.Mutation;

/// <summary>
/// See <see cref="FlowEvent.ExecutionAttemptStarted"/>'s own remarks (spec/baton.md §3) for the
/// change these fixtures pin: a SECOND-OR-LATER attempt's crash-recovered classification. Every
/// fixture here hand-authors the exact log lines a real crash mid-attempt-2 would leave behind,
/// matching <c>MutationInterfaceCrashRecoveryTests</c>' own style, against a real git worktree
/// (<c>TimeoutOnMutatedWorkspaceEndToEndTests</c>' own remarks explain why a real tree, not a
/// fabricated one).
/// </summary>
public class CrashRecoveredTimeoutMutationBaseTests
{
    private static readonly StepId Implement = new("implement");
    private static readonly WorkerContract Contract = new("worktree-worker", [], [], []);

    [Fact]
    public async Task A_crash_recovered_attempt_2_timeout_that_touched_nothing_since_ITS_own_start_is_not_judged_mutated()
    {
        var run = await RunAsync(mutateDuringAttempt2: null);
        try
        {
            var stepState = run.FinalState.Steps.Single(s => s.StepId == Implement);

            // Attempt 1's own commit is real history in the worktree, and predates attempt 2's start
            // -- exactly what would false-positive as "mutated" if classification fell back to the
            // worktree's provisioning base instead of the journaled per-attempt start sha.
            Assert.False(stepState.IndeterminateAwaitingResolution);
            Assert.Equal(StepStatus.Failed, stepState.Status);

            var events = await run.Reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Empty(events.OfType<FlowEvent.ExecutionIndeterminate>());
        }
        finally
        {
            run.Cleanup();
        }
    }

    [Fact]
    public async Task A_crash_recovered_attempt_2_timeout_that_committed_since_ITS_own_start_is_judged_mutated()
    {
        // Polarity control for the arm above: same history through attempt 2's start, but attempt 2
        // itself leaves a commit before the recorded (crashed) exit is classified.
        var run = await RunAsync(mutateDuringAttempt2: workspace =>
        {
            File.WriteAllText(Path.Combine(workspace, "attempt2.txt"), "attempt 2's own work");
            RunGit(workspace, "add", ".");
            RunGit(workspace, "commit", "-m", "attempt 2's work");
        });
        try
        {
            var stepState = run.FinalState.Steps.Single(s => s.StepId == Implement);

            Assert.True(stepState.IndeterminateAwaitingResolution);
            Assert.Equal(IndeterminateProducer.ContractFailure, stepState.IndeterminateProducer);
            Assert.Contains("1 new commit(s)", stepState.LatestFailureReason!, StringComparison.Ordinal);
        }
        finally
        {
            run.Cleanup();
        }
    }

    private sealed record CrashRun(FlowState FinalState, FlowEventLogReader Reader, Action Cleanup);

    private static async Task<CrashRun> RunAsync(Action<string>? mutateDuringAttempt2)
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var worktree = Path.Combine(roomDirectory, "worktree");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");

        Directory.CreateDirectory(worktree);
        RunGit(worktree, "init");
        RunGit(worktree, "config", "user.email", "test@example.com");
        RunGit(worktree, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(worktree, "seeded.txt"), "provisioning base");
        RunGit(worktree, "add", ".");
        RunGit(worktree, "commit", "-m", "worktree provisioning base");
        var provisioningBaseSha = ReadHeadSha(worktree);

        var workflowId = new WorkflowId("wf-1373-followup");
        var snapshot = new WorkflowDefinitionSnapshot(
            new WorkflowDefinitionSnapshotId("snapshot-1373-followup"),
            new WorkflowTemplateId("template-1373-followup"),
            WorkflowTemplateVersion: 1,
            Steps:
            [
                new WorkflowStepDefinition(
                    Implement, "worktree-worker", Inputs: [], Outputs: [], DependsOn: [],
                    RetryPolicy: new RetryPolicy(MaxAttempts: 2, Backoff: BackoffPolicy.None)),
            ]);
        var target = new CoreDispatchTarget("worktree-worker-cli", [], WorkingDirectory: worktree);
        var bindings = new Dictionary<string, WorkerBinding>
        {
            ["worktree-worker"] = new WorkerBinding.Process(
                Contract, target, TimeSpan.FromMinutes(60), IsWorktree: true, WorktreeBaseSha: provisioningBaseSha),
        };

        await using (var writer = new FlowEventLogWriter(logPath))
        {
            // Attempt 1: dispatched, and while it ran it left a real commit in the shared worktree --
            // the fact a provisioning-base fallback would misattribute to attempt 2. Recorded as an
            // ordinary retryable failure (not #1373's own mutated-timeout path) purely to justify a
            // second attempt existing; what this fixture measures is attempt 2's classification, not
            // attempt 1's.
            var attempt1 = await AcceptRequestAsync(writer, workflowId, artifactsRoot, Implement);
            File.WriteAllText(Path.Combine(worktree, "attempt1.txt"), "attempt 1's finished work");
            RunGit(worktree, "add", ".");
            RunGit(worktree, "commit", "-m", "attempt 1's finished work");
            await writer.AppendAsync(
                new FlowEvent.ExecutionFailed(attempt1, FailureClassification.Retryable, "stand-in ordinary failure"),
                TestContext.Current.CancellationToken);

            var attempt2StartSha = ReadHeadSha(worktree);

            // Attempt 2: a fresh dispatch over the SAME worktree (#1373's whole premise), its own
            // start sha journaled the way DispatchAndRecordOutcomeAsync now journals it before ever
            // calling Core.
            var attempt2 = await AcceptRequestAsync(writer, workflowId, artifactsRoot, Implement);
            await writer.AppendAsync(
                new FlowEvent.ExecutionAttemptStarted(attempt2, attempt2StartSha), TestContext.Current.CancellationToken);

            mutateDuringAttempt2?.Invoke(worktree);

            // The crash window: Core's own two lifecycle events are durable (a real kill really
            // happened and was really recorded), but Flow never got to classify the exit before the
            // pump went down.
            await writer.AppendAsync(new CoreEvent.ExecutionStarted(attempt2, Pid: 4343), TestContext.Current.CancellationToken);
            await writer.AppendAsync(
                new CoreEvent.ExecutionExited(attempt2, ExitCode: -1, CoreExitReason.TimedOut), TestContext.Current.CancellationToken);
        }

        // The recovery pump: a fresh StartWorkflowAsync call, exactly as if the operator restarted it
        // after the crash above.
        await using var recoveryWriter = new FlowEventLogWriter(logPath);
        var reader = new FlowEventLogReader(logPath);
        var stub = new StubCoreDispatcher();

        var finalState = await MutationInterface.StartWorkflowAsync(
            workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, recoveryWriter, stub,
            cancellationToken: TestContext.Current.CancellationToken);

        return new CrashRun(finalState, reader, () => DirectoryCleanup.DeleteRecursively(roomDirectory));
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
            "worktree-worker",
            Inputs: [],
            Outputs: [],
            TimeSpan.FromMinutes(60),
            ArtifactManager.BuildEnvironment([], outputDirectory, artifactsRoot),
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

        await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(request), TestContext.Current.CancellationToken);
        return executionId;
    }

    private static string ReadHeadSha(string workingDirectory)
    {
        var startInfo = new ProcessStartInfo("git", "rev-parse HEAD")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var process = global::Baton.Core.ProcessLaunch.Start(startInfo) ?? throw new InvalidOperationException("git could not be started.");
        var sha = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return sha;
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

        using var process = global::Baton.Core.ProcessLaunch.Start(startInfo) ?? throw new InvalidOperationException("git could not be started.");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} failed ({process.ExitCode}): {process.StandardError.ReadToEnd()}");
        }
    }
}
