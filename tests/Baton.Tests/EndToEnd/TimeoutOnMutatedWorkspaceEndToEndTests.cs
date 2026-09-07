using System.Diagnostics;
using Baton.Artifacts;
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
            // half falls back to the delta against the attempt's start sha and SAYS it did, naming the
            // missing measurement rather than guessing which of its three causes produced it. The
            // pushed arm is A_timeout_on_a_pushed_workspace_behind_an_open_PR... below, against a real
            // origin.
            Assert.Contains(
                "1 new commit(s) (not measured against a remote)",
                step.LatestFailureReason!,
                StringComparison.Ordinal);

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

    /// <summary>
    /// #1978 fix round: the seam the PR body disclosed as unpinned. Every classifier arm injects
    /// <c>openPullRequest</c> straight into <c>OutcomeClassifier.Classify</c>, so dropping
    /// <c>MutationInterface</c>'s own argument at that call site left all of them green while restoring
    /// the bug. This runs the real pump over a real pushed workspace with a real origin, a
    /// <c>DeliversBranch</c> binding and a fake <c>gh</c>, and reads the settled reason — the one path
    /// that fails if the argument is dropped.
    /// <para>
    /// It is also the dirty-delivered arm end to end: the branch is on origin behind PR #1974, and one
    /// uncommitted path is still in the workspace, so the summary owes that residue and must not say
    /// nothing is owed.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_timeout_on_a_pushed_workspace_behind_an_open_PR_names_the_PR_and_still_owes_the_residue()
    {
        var run = await RunTimedOutLaneAsync(
            workspace => File.WriteAllText(Path.Combine(workspace, "left-behind.txt"), "written, never committed"),
            pushToOrigin: true,
            deliversBranch: true,
            openPullRequestJson: """[{"number":1974}]""",
            satisfyContract: true);

        try
        {
            var step = run.FinalState.Steps.Single(s => s.StepId == Implement);
            var reason = step.LatestFailureReason!;

            // The verdict is untouched by #1978 — only the text moves.
            Assert.Equal(WorkflowOutcome.Indeterminate, WorkflowOutcome.Describe(run.FinalState));
            Assert.True(step.IndeterminateAwaitingResolution);

            // Real git against a real origin: the commit half is the REMOTE reading, and this workspace
            // pushed everything it committed. The fallback phrase must not appear at all here — that is
            // the arm the no-remote fixture above pins.
            Assert.Contains("0 unpushed commit(s) and 1 changed/untracked path(s)", reason, StringComparison.Ordinal);
            Assert.DoesNotContain("not measured against a remote", reason, StringComparison.Ordinal);

            // The seam itself: this fact can only have come from MutationInterface's own `gh` spawn
            // being handed to Classify.
            Assert.Contains("PR #1974 is open", reason, StringComparison.Ordinal);
            Assert.Contains("already delivered", reason, StringComparison.Ordinal);
            Assert.DoesNotContain("redispatch", reason, StringComparison.OrdinalIgnoreCase);

            // And the residue is still owed: one path never left the workspace.
            Assert.Contains(
                "the 1 changed/untracked path(s) in the workspace are not delivered", reason, StringComparison.Ordinal);
            Assert.DoesNotContain("no follow-on brief is owed", reason, StringComparison.Ordinal);

            Assert.Empty(run.Events.OfType<FlowEvent.StepRetryScheduled>());
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
    private static async Task<LaneRun> RunTimedOutLaneAsync(
        Action<string>? mutateWorkspace,
        bool pushToOrigin = false,
        bool deliversBranch = false,
        string? openPullRequestJson = null,
        bool satisfyContract = false)
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

        if (pushToOrigin)
        {
            // A real bare origin and a real `push -u`, because `@{upstream}..HEAD` is what is under
            // test: a fabricated ref (TempGitRepository.SetReviewedBaselineAtHead's shortcut) sets no
            // tracking branch, so the probe would read null and this arm would silently become the
            // no-remote one.
            var origin = Path.Combine(roomDirectory, "origin.git");
            Directory.CreateDirectory(origin);
            RunGit(origin, "init", "--bare");
            RunGit(workspace, "remote", "add", "origin", origin);
            RunGit(workspace, "push", "-u", "origin", "HEAD");
        }

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
                ChangesTree: true,
                // #1978 gates the PR lookup on this, exactly as WorkerRoles.json sets it for `implement`
                // and nothing else.
                DeliversBranch: deliversBranch),
        };

        var dispatcher = new TimingOutCoreDispatcher(
            workspace,
            mutateWorkspace,
            satisfyContract ? artifactsRoot : null);

        await using var writer = new FlowEventLogWriter(logPath);
        var reader = new FlowEventLogReader(logPath);

        // The fake `gh` lives OUTSIDE the workspace: a .cmd dropped inside it would be one more
        // untracked path and would move the count this test asserts on.
        using var ghScope = openPullRequestJson is null
            ? null
            : MutationInterface.BeginOpenPullRequestGhProgramScope(WriteFakeGh(roomDirectory, openPullRequestJson));

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
    /// <param name="artifactsRootForDeclaredOutput">
    /// Non-null for the one arm that needs a SATISFIED contract: the declared <c>pr.md</c> is written to
    /// the execution's artifacts directory, which is where a worker writes it and deliberately not the
    /// worktree this test's mutation probe reads — the two are separate evidence, which is the whole
    /// reason the classifier's "delivered" wording is gated on the contract as well as the push.
    /// </param>
    private sealed class TimingOutCoreDispatcher(
        string workspace,
        Action<string>? mutateOnFirstDispatch,
        string? artifactsRootForDeclaredOutput = null) : ICoreDispatcher
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

            if (artifactsRootForDeclaredOutput is { } artifactsRoot)
            {
                var outputDirectory = ArtifactManager.ResolveOutputDirectory(artifactsRoot, request.ExecutionId);
                Directory.CreateDirectory(outputDirectory);
                File.WriteAllText(Path.Combine(outputDirectory, "pr.md"), "the declared output, written");
            }

            _targets.Add(target);
            return Task.FromResult(new CoreDispatchResult(0, CoreExitReason.TimedOut));
        }
    }

    /// <summary>
    /// A <c>gh</c> that answers one fixed JSON body and exits 0 — the same shape
    /// <c>DeliveryVerifierTests</c> uses, kept local because that one is private to its own class and
    /// this fixture needs the file outside the workspace it writes into.
    /// </summary>
    private static string WriteFakeGh(string directory, string jsonOutput)
    {
        var path = Path.Combine(directory, $"fake-gh-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(path, $"@echo off\necho {jsonOutput}\nexit /b 0\n");
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
