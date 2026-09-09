using Baton.Dispatch;
using Baton.Domain;
using Baton.Mutation;
using Baton.Status;
using Baton.Store;
using Baton.Tests.TestSupport;

namespace Baton.Tests.EndToEnd;

/// <summary>
/// #2134: both polarities of <c>spec/baton.md</c> §3's "The grace turn" (its own account governs, not
/// restated here). Real git, fake dispatcher — same split <c>TimeoutOnMutatedWorkspaceEndToEndTests</c>
/// uses and for the same reason: the dirty-tree probe shells out to real git, so only a real tree can
/// discriminate its answer, while the vendor CLI itself is stood in for so each polarity is
/// deterministic rather than dependent on an actual model run.
/// </summary>
public sealed class GraceTurnEndToEndTests
{
    private static readonly StepId Implement = new("implement");

    // #1706: the billed component on claude is cache_creation -- mirrors
    // MutationInterfaceTests.StartWorkflowAsync_arrests_an_execution_that_crosses_its_token_budget's own
    // fixture line.
    private const string PrimaryArrestingUsageLine =
        """{"type":"assistant","message":{"usage":{"input_tokens":2,"cache_creation_input_tokens":700000,"cache_read_input_tokens":500000,"output_tokens":3}}}""";

    // Well above GraceTurn.TokenBudget (30,000) so the grace dispatch's own, far smaller monitor
    // arrests it in turn.
    private const string GraceExceedingUsageLine =
        """{"type":"assistant","message":{"usage":{"input_tokens":2,"cache_creation_input_tokens":40000,"cache_read_input_tokens":0,"output_tokens":3}}}""";

    [Fact]
    public async Task A_grace_turn_that_commits_is_recorded_clean_and_the_room_still_settles_Indeterminate()
    {
        var run = await RunArrestedLaneAsync(graceShouldCommit: true);
        try
        {
            var step = run.FinalState.Steps.Single(s => s.StepId == Implement);

            // Never Succeeded, whatever the grace turn did -- the arrest it precedes is the one thing
            // that decides the room, unchanged from every other budget arrest.
            Assert.Equal(WorkflowOutcome.Indeterminate, WorkflowOutcome.Describe(run.FinalState));
            Assert.Empty(run.Events.OfType<FlowEvent.ExecutionSucceeded>());
            Assert.Single(run.Events.OfType<FlowEvent.ExecutionArrested>());

            var grace = Assert.Single(run.Events.OfType<FlowEvent.GraceTurnAttempted>());
            Assert.True(grace.WorkspaceCleanAfter);
            Assert.Equal(CoreExitReason.Natural, grace.ExitReason);
            Assert.Null(grace.ArrestReason);

            // GraceTurnAttempted is journaled before ExecutionArrested for the same execution -- a
            // reader replaying the ledger sees the courtesy turn's own outcome before the arrest that
            // follows it.
            var executionEvents = run.Events.Where(e => e is FlowEvent.GraceTurnAttempted or FlowEvent.ExecutionArrested).ToList();
            Assert.IsType<FlowEvent.GraceTurnAttempted>(executionEvents[0]);
            Assert.IsType<FlowEvent.ExecutionArrested>(executionEvents[1]);

            Assert.Equal(2, run.Dispatcher.CallCount);
            Assert.True(RepositoryIsClean(run.Workspace));
        }
        finally
        {
            run.Cleanup();
        }
    }

    [Fact]
    public async Task A_grace_turn_that_exceeds_its_own_cap_is_arrested_with_the_dirty_tree_recorded()
    {
        var run = await RunArrestedLaneAsync(graceShouldCommit: false);
        try
        {
            var step = run.FinalState.Steps.Single(s => s.StepId == Implement);

            Assert.Equal(WorkflowOutcome.Indeterminate, WorkflowOutcome.Describe(run.FinalState));
            Assert.Empty(run.Events.OfType<FlowEvent.ExecutionSucceeded>());
            Assert.Single(run.Events.OfType<FlowEvent.ExecutionArrested>());

            var grace = Assert.Single(run.Events.OfType<FlowEvent.GraceTurnAttempted>());
            Assert.False(grace.WorkspaceCleanAfter);
            Assert.Equal(CoreExitReason.CancelRequested, grace.ExitReason);
            Assert.Equal(ArrestReason.TokenBudget, grace.ArrestReason);

            Assert.Equal(2, run.Dispatcher.CallCount);
            Assert.False(RepositoryIsClean(run.Workspace));
        }
        finally
        {
            run.Cleanup();
        }
    }

    [Fact]
    public async Task No_grace_turn_runs_for_a_read_shaped_role_even_with_a_dirty_workspace()
    {
        // #2029's own split, extended -- the ONE gate this test isolates from the two above, which
        // both leave VerifiesWorkspace at its Process default (true).
        var run = await RunArrestedLaneAsync(graceShouldCommit: true, verifiesWorkspace: false);
        try
        {
            Assert.Empty(run.Events.OfType<FlowEvent.GraceTurnAttempted>());
            Assert.Single(run.Events.OfType<FlowEvent.ExecutionArrested>());
            // The fake dispatcher's second call is what a grace turn would have made -- it never came.
            Assert.Equal(1, run.Dispatcher.CallCount);
            Assert.False(RepositoryIsClean(run.Workspace));
        }
        finally
        {
            run.Cleanup();
        }
    }

    [Fact]
    public async Task A_grace_turn_spawn_failure_is_recorded_and_the_room_still_settles_Indeterminate()
    {
        var run = await RunArrestedLaneAsync(graceShouldCommit: false, graceSpawnFails: true);
        try
        {
            Assert.Equal(WorkflowOutcome.Indeterminate, WorkflowOutcome.Describe(run.FinalState));
            Assert.Single(run.Events.OfType<FlowEvent.ExecutionArrested>());

            var grace = Assert.Single(run.Events.OfType<FlowEvent.GraceTurnAttempted>());
            Assert.False(grace.WorkspaceCleanAfter);
            Assert.Equal(CoreExitReason.CancelRequested, grace.ExitReason);
            Assert.Null(grace.ArrestReason);

            Assert.Equal(2, run.Dispatcher.CallCount);
            Assert.False(RepositoryIsClean(run.Workspace));
        }
        finally
        {
            run.Cleanup();
        }
    }

    [Fact]
    public async Task A_grace_turn_preserves_the_binding_stdout_sink()
    {
        var receivedLines = new List<string>();
        var run = await RunArrestedLaneAsync(
            graceShouldCommit: true,
            onStdoutLine: receivedLines.Add);
        try
        {
            Assert.Contains("grace turn output", receivedLines);
        }
        finally
        {
            run.Cleanup();
        }
    }

    private sealed record LaneRun(
        FlowState FinalState,
        IReadOnlyList<FlowEvent> Events,
        string Workspace,
        GraceTurnCoreDispatcher Dispatcher,
        Action Cleanup);

    private static async Task<LaneRun> RunArrestedLaneAsync(
        bool graceShouldCommit,
        bool verifiesWorkspace = true,
        bool graceSpawnFails = false,
        Action<string>? onStdoutLine = null)
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var workspace = Path.Combine(roomDirectory, "lane");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");

        Directory.CreateDirectory(workspace);
        TempGitRepository.InitWithEverythingCommitted(workspace);

        var snapshot = new WorkflowDefinitionSnapshot(
            new WorkflowDefinitionSnapshotId("snapshot-2134"),
            new WorkflowTemplateId("template-2134"),
            WorkflowTemplateVersion: 1,
            Steps: [new WorkflowStepDefinition(Implement, "implement", [], ["pr.md"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]);

        var bindings = new Dictionary<string, WorkerBinding>
        {
            ["implement"] = new WorkerBinding.Process(
                new WorkerContract("implement", [], [new ProducedOutput("pr.md")], []),
                new CoreDispatchTarget(
                    "vendor-cli", ["-p", "ORIGINAL BRIEF"], WorkingDirectory: workspace,
                    OnStdoutLine: onStdoutLine, PromptText: "ORIGINAL BRIEF"),
                TimeSpan.FromSeconds(30),
                Adapter: "claude",
                TokenBudget: 1000,
                ChangesTree: true,
                VerifiesWorkspace: verifiesWorkspace),
        };

        var dispatcher = new GraceTurnCoreDispatcher(
            workspace, PrimaryArrestingUsageLine, graceShouldCommit, GraceExceedingUsageLine, graceSpawnFails);

        await using var writer = new FlowEventLogWriter(logPath);
        var reader = new FlowEventLogReader(logPath);

        var finalState = await MutationInterface.StartWorkflowAsync(
            new WorkflowId("wf-2134"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher,
            cancellationToken: TestContext.Current.CancellationToken);

        var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);

        return new LaneRun(finalState, events, workspace, dispatcher, () => DirectoryCleanup.DeleteRecursively(roomDirectory));
    }

    private static bool RepositoryIsClean(string workspace) =>
        string.IsNullOrWhiteSpace(RunGitCapturingOutput(workspace, "status", "--porcelain"));

    private static string RunGitCapturingOutput(string workingDirectory, params string[] args)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("git")
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

        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("git could not be started.");
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return stdout;
    }

    private static void RunGit(string workingDirectory, params string[] args)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("git")
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

        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("git could not be started.");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} exited {process.ExitCode}: {process.StandardError.ReadToEnd()}");
        }
    }

    /// <summary>
    /// First call stands in for the primary, arrested execution: leaves one uncommitted file behind,
    /// then feeds a usage line that crosses the binding's own 1,000-token budget and waits for the
    /// resulting <c>TokenBudgetMonitor.ArrestRequested</c> cancellation to land — the same pattern
    /// <c>MutationInterfaceTests.ArrestingCoreDispatcher</c> uses, mirrored here because this dispatcher
    /// also has to drive the SECOND, grace call. That call either commits the left-behind file (and
    /// returns <see cref="CoreExitReason.Natural"/>) or feeds a usage line that crosses the grace
    /// dispatch's own, far smaller budget and waits for ITS cancellation (returning
    /// <see cref="CoreExitReason.CancelRequested"/>, tree still dirty) — <c>graceShouldCommit</c> picks
    /// which.
    /// </summary>
    private sealed class GraceTurnCoreDispatcher(
        string workspace,
        string primaryUsageLine,
        bool graceShouldCommit,
        string graceExceedingUsageLine,
        bool graceSpawnFails) : ICoreDispatcher
    {
        public int CallCount { get; private set; }

        public async Task<CoreDispatchResult> DispatchAsync(
            ExecutionRequest request, CoreDispatchTarget target, CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (CallCount == 1)
            {
                File.WriteAllText(Path.Combine(workspace, "left-behind.txt"), "written, never committed before the arrest");
                target.OnStdoutLine?.Invoke(primaryUsageLine);
                await WaitForCancellationAsync(cancellationToken).ConfigureAwait(false);
                return new CoreDispatchResult(-1, CoreExitReason.CancelRequested);
            }

            if (graceSpawnFails)
            {
                throw new InvalidOperationException("grace worker could not be spawned");
            }

            if (graceShouldCommit)
            {
                target.OnStdoutLine?.Invoke("grace turn output");
                RunGit(workspace, "add", ".");
                RunGit(workspace, "commit", "-m", "fix: commit incomplete work under grace turn");
                return new CoreDispatchResult(0, CoreExitReason.Natural);
            }

            target.OnStdoutLine?.Invoke(graceExceedingUsageLine);
            await WaitForCancellationAsync(cancellationToken).ConfigureAwait(false);
            return new CoreDispatchResult(-1, CoreExitReason.CancelRequested);
        }

        private static async Task WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource();
            await using var registration = cancellationToken.Register(() => tcs.TrySetResult());
            // Not a timing expectation -- the ceiling only stops a regression from hanging the suite.
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(120), cancellationToken: CancellationToken.None);
        }
    }
}
