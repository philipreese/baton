using System.Diagnostics;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Mutation;
using Baton.Status;
using Baton.Store;
using Baton.Tests.TestSupport;

namespace Baton.Tests.EndToEnd;

/// <summary>
/// #1373 end to end, through the real pump, against a real git workspace: a timed-out attempt that
/// left work behind settles Indeterminate and is never retried, and one that left nothing behind is
/// retried with a continuation brief in the argument its worker is actually spawned with.
/// <para>
/// The three arms are the shapes the 2026-09-01 measurement recorded (spec/baton.md §3, #1373): a
/// finished commit (#1580/#1584), uncommitted edits (#1619 with 18, #1183 with 2), and the
/// genuinely-cold case retrying was always right for.
/// </para>
/// <para>
/// Real git, fake dispatcher, and both halves deliberate. The probe shells out to git, so only a real
/// tree can discriminate its answer — a temp directory with a fabricated status would test the
/// fixture. The dispatcher is faked because what has to be observed is the <c>CoreDispatchTarget</c>
/// the engine handed it: a real shell worker carries no <c>PromptText</c>, so the retry's brief would
/// have nowhere to land, and asserting on the execution's <c>prompt.txt</c> instead would certify the
/// archival copy rather than what the worker was invoked with. <see cref="CoreExitReason.TimedOut"/>
/// is the exact result a real kill produces (<c>CoreDispatcher</c> maps <c>BatonExitReason.TimedOut</c>
/// to it), which <c>CoreDispatcherTests</c> already pins against a real spawned process.
/// </para>
/// </summary>
public sealed class TimeoutOnMutatedWorkspaceEndToEndTests
{
    private static readonly StepId Implement = new("implement");
    private const string OriginalBrief = "ORIGINAL BRIEF: implement the issue.";

    [Fact]
    public async Task A_timeout_on_a_workspace_carrying_a_commit_settles_Indeterminate_and_is_never_retried()
    {
        var run = await RunTimedOutLaneAsync(workspace =>
        {
            File.WriteAllText(Path.Combine(workspace, "delivered.txt"), "the work attempt 1 finished");
            RunGit(workspace, "add", ".");
            RunGit(workspace, "commit", "-m", "attempt 1's finished work");
        });

        try
        {
            var step = run.FinalState.Steps.Single(s => s.StepId == Implement);

            Assert.Equal(WorkflowOutcome.Indeterminate, WorkflowOutcome.Describe(run.FinalState));
            Assert.True(step.IndeterminateAwaitingResolution);
            // ContractFailure is the producer whose resolve grammar this shape needs: nothing captured
            // to accept, and a conductor's judgement after inspecting the workspace IS a rejectable
            // thing (spec/baton.md §3's settle-shape table).
            Assert.Equal(IndeterminateProducer.ContractFailure, step.IndeterminateProducer);
            // #1978: real git, and this fixture's `git init` repo has no remote at all — so the commit
            // half falls back to the delta against the attempt's start sha and SAYS it did. The one arm
            // of #1978's split that a real tree can discriminate here; the pushed/ahead-of-upstream arms
            // are pinned against the classifier in OutcomeClassifierTests, which needs no origin.
            Assert.Contains("1 new commit(s) (no upstream)", step.LatestFailureReason!, StringComparison.Ordinal);

            // A committed work product leaves a CLEAN tree. This is the arm a status-only probe would
            // have read as "nothing here" and retried straight over.
            Assert.Contains("0 changed/untracked path(s)", step.LatestFailureReason!, StringComparison.Ordinal);

            Assert.Empty(run.Events.OfType<FlowEvent.StepRetryScheduled>());
            Assert.Single(run.Events.OfType<FlowEvent.ExecutionRequestAccepted>());
            Assert.Single(run.DispatchedTargets);
        }
        finally
        {
            run.Cleanup();
        }
    }

    [Fact]
    public async Task A_timeout_on_a_workspace_carrying_uncommitted_work_settles_Indeterminate_and_is_never_retried()
    {
        var run = await RunTimedOutLaneAsync(workspace =>
        {
            File.WriteAllText(Path.Combine(workspace, "seeded.txt"), "edited, not committed");
            File.WriteAllText(Path.Combine(workspace, "brand-new.txt"), "written, never added");
        });

        try
        {
            var step = run.FinalState.Steps.Single(s => s.StepId == Implement);

            Assert.Equal(WorkflowOutcome.Indeterminate, WorkflowOutcome.Describe(run.FinalState));
            Assert.True(step.IndeterminateAwaitingResolution);
            Assert.Contains("2 changed/untracked path(s)", step.LatestFailureReason!, StringComparison.Ordinal);

            Assert.Empty(run.Events.OfType<FlowEvent.StepRetryScheduled>());
            Assert.Single(run.DispatchedTargets);
        }
        finally
        {
            run.Cleanup();
        }
    }

    [Fact]
    public async Task A_timeout_on_an_untouched_workspace_still_retries_and_the_retry_carries_the_continuation_brief()
    {
        var run = await RunTimedOutLaneAsync(mutateWorkspace: null);

        try
        {
            // The discriminating control for both arms above: identical lane, identical timeout, the
            // workspace the only difference. Without it, an unconditional Indeterminate settlement
            // would pass them both.
            Assert.Single(run.Events.OfType<FlowEvent.StepRetryScheduled>());
            Assert.Equal(2, run.Events.OfType<FlowEvent.ExecutionRequestAccepted>().Count());
            Assert.Equal(2, run.DispatchedTargets.Count);
            Assert.False(run.FinalState.Steps.Single(s => s.StepId == Implement).IndeterminateAwaitingResolution);

            // What the worker is actually spawned with, not what was archived for display.
            var firstAttemptPrompt = run.DispatchedTargets[0].Args.Single(arg => arg.Contains(OriginalBrief, StringComparison.Ordinal));
            var retryPrompt = run.DispatchedTargets[1].Args.Single(arg => arg.Contains(OriginalBrief, StringComparison.Ordinal));

            Assert.Equal(OriginalBrief, firstAttemptPrompt);
            Assert.StartsWith("[baton] CONTINUATION BRIEF", retryPrompt, StringComparison.Ordinal);
            Assert.Contains("This is attempt 2 of 2.", retryPrompt, StringComparison.Ordinal);
            Assert.Contains("FINISH what attempt 1 started", retryPrompt, StringComparison.Ordinal);
            Assert.EndsWith(OriginalBrief, retryPrompt, StringComparison.Ordinal);

            // PromptText is kept identical to the argument — the invariant CoreDispatcher's #748
            // oversize swap finds the prompt argument by.
            Assert.Equal(retryPrompt, run.DispatchedTargets[1].PromptText);
        }
        finally
        {
            run.Cleanup();
        }
    }

    private sealed record LaneRun(
        FlowState FinalState,
        IReadOnlyList<FlowEvent> Events,
        IReadOnlyList<CoreDispatchTarget> DispatchedTargets,
        Action Cleanup);

    /// <summary>
    /// Runs one lane whose only attempt(s) are killed by the dispatch timeout, having first run
    /// <paramref name="mutateWorkspace"/> against the lane's real git workspace — the fake dispatcher's
    /// stand-in for a worker that did some work and then ran out of clock.
    /// </summary>
    private static async Task<LaneRun> RunTimedOutLaneAsync(Action<string>? mutateWorkspace)
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var workspace = Path.Combine(roomDirectory, "lane");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");

        Directory.CreateDirectory(workspace);
        RunGit(workspace, "init");
        RunGit(workspace, "config", "user.email", "test@example.com");
        RunGit(workspace, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(workspace, "seeded.txt"), "already here before the lane started");
        RunGit(workspace, "add", ".");
        RunGit(workspace, "commit", "-m", "lane base");

        var snapshot = new WorkflowDefinitionSnapshot(
            new WorkflowDefinitionSnapshotId("snapshot-1373"),
            new WorkflowTemplateId("template-1373"),
            WorkflowTemplateVersion: 1,
            Steps:
            [
                new WorkflowStepDefinition(
                    Implement,
                    "implement",
                    Inputs: [],
                    Outputs: ["pr.md"],
                    DependsOn: [],
                    // Backoff.None so the retry dispatches at t+0 and this test needs no fake clock.
                    RetryPolicy: new RetryPolicy(MaxAttempts: 2, Backoff: BackoffPolicy.None)),
            ]);

        var bindings = new Dictionary<string, WorkerBinding>
        {
            ["implement"] = new WorkerBinding.Process(
                new WorkerContract("implement", [], [new ProducedOutput("pr.md")], []),
                new CoreDispatchTarget(
                    "vendor-cli",
                    ["-p", OriginalBrief],
                    WorkingDirectory: workspace,
                    PromptText: OriginalBrief),
                TimeSpan.FromMinutes(60),
                // A tree-changing role: write + shell, so no isolated worktree is provisioned and the
                // lane's own directory is what carries the work — the shape every implement lane in the
                // 2026-09-01 measurement had, and the one a worktree-only probe would never see.
                ChangesTree: true),
        };

        var dispatcher = new TimingOutCoreDispatcher(workspace, mutateWorkspace);

        await using var writer = new FlowEventLogWriter(logPath);
        var reader = new FlowEventLogReader(logPath);

        var finalState = await MutationInterface.StartWorkflowAsync(
            new WorkflowId("wf-1373"),
            roomDirectory,
            snapshot,
            bindings,
            artifactsRoot,
            reader,
            writer,
            dispatcher,
            cancellationToken: TestContext.Current.CancellationToken);

        var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);

        return new LaneRun(
            finalState,
            events,
            dispatcher.DispatchedTargets,
            () => DirectoryCleanup.DeleteRecursively(roomDirectory));
    }

    /// <summary>
    /// Mutates the workspace on its FIRST dispatch only — a second attempt stands in for a worker that
    /// was killed before it managed anything — and always reports the exit a real timeout kill
    /// produces. Records every target it was handed, which is the only place the argument a worker
    /// would have been spawned with can be read.
    /// </summary>
    private sealed class TimingOutCoreDispatcher(string workspace, Action<string>? mutateOnFirstDispatch) : ICoreDispatcher
    {
        private readonly List<CoreDispatchTarget> _targets = [];

        public IReadOnlyList<CoreDispatchTarget> DispatchedTargets => _targets;

        public Task<CoreDispatchResult> DispatchAsync(
            ExecutionRequest request, CoreDispatchTarget target, CancellationToken cancellationToken = default)
        {
            if (_targets.Count == 0)
            {
                mutateOnFirstDispatch?.Invoke(workspace);
            }

            _targets.Add(target);
            return Task.FromResult(new CoreDispatchResult(0, CoreExitReason.TimedOut));
        }
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

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("git could not be started.");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} failed ({process.ExitCode}): {process.StandardError.ReadToEnd()}");
        }
    }
}
