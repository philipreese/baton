using System.Diagnostics;
using System.Text.Json;
using Baton.Vendors;
using Baton.Cli.Tests.TestSupport;
using Baton.Concurrency;
using Baton.Domain;
using Baton.Mutation;
using Baton.Store;
using Baton.Templates;

namespace Baton.Cli.Tests;

/// <summary>
/// M12 Phase 3's completion gate for <c>baton decide</c> (issue #97): pause → each of the four
/// decision types → fixed point, driven through the real <see cref="DecideCommand.ExecuteAsync"/>
/// entry point — the exact call <c>Program.cs</c> makes — mirroring
/// <see cref="RunCommandEndToEndTests"/>'s discipline of never mocking <c>Baton.Core</c> itself.
/// Decision semantics stay proven at the <c>MutationInterface</c> layer
/// (<c>PauseDecisionSupersedeHumanEndToEndTests</c>, M9); this only proves the CLI reaches it.
/// </summary>
public class DecideCommandEndToEndTests
{
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter> { ["shell"] = new ShellCommandWorkerAdapter() };

    [Fact]
    public async Task An_approval_gate_pauses_A_then_baton_decide_Resume_runs_B_to_the_fixed_point()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-decide-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteApprovalGateWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteApprovalGateBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);

            var pausedResult = await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(WorkflowStatus.Paused, pausedResult.State.Status);
            var pausedExecutionId = pausedResult.State.Steps.Single(s => s.StepId.Value == "a").LatestExecutionId!.Value;

            var decideOptions = new DecideOptions(
                roomDirectory, pausedExecutionId.Value, DecisionType.Resume, TargetStepId: null,
                SupplementaryExecutionId: null, bindingsFilePath);

            var finalResult = await DecideCommand.ExecuteAsync(decideOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, finalResult.State.Status);
            Assert.All(finalResult.State.Steps, FlowAssert.Succeeded);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Deciding_with_an_omitted_Codex_model_refuses_before_mutating_or_launching()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-decide-conductor-model-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var observingCodex = new ResumeObservingWorkerAdapter();
            var adapters = new Dictionary<string, IWorkerAdapter>
            {
                ["shell"] = new ShellCommandWorkerAdapter(),
                ["codex"] = observingCodex,
            };
            var workflowFilePath = await WriteApprovalGateWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteApprovalGateBindingsAsync(testRoot);

            var paused = await RunCommand.ExecuteAsync(
                new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory), adapters,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(WorkflowStatus.Paused, paused.State.Status);
            var executionId = paused.State.Steps.Single(step => step.StepId.Value == "a").LatestExecutionId!.Value;

            // Model is deliberately omitted: Codex's recorded default is Astra, a conductor-only
            // model. The decision would otherwise make b ready and dispatch it in the settling pump.
            var conductorConfig = new Dictionary<string, WorkerBindingConfigEntry>
            {
                ["a"] = new WorkerBindingConfigEntry(
                    "shell", new WorkerContract("a", [], [new ProducedOutput("out_a")], []),
                    WriteFileCommand("out_a", "a-out"), TimeSpan.FromSeconds(30)),
                ["b"] = new WorkerBindingConfigEntry(
                    "codex", new WorkerContract("b", ["out_a"], [new ProducedOutput("out_b")], []),
                    "must not launch", TimeSpan.FromSeconds(30)),
            };
            await File.WriteAllTextAsync(
                bindingsFilePath, JsonSerializer.Serialize(conductorConfig), TestContext.Current.CancellationToken);
            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            var flowBeforeDecision = await File.ReadAllTextAsync(logPath, TestContext.Current.CancellationToken);

            var thrown = await Assert.ThrowsAsync<ConductorOnlyWorkerModelException>(
                () => DecideCommand.ExecuteAsync(
                    new DecideOptions(roomDirectory, executionId.Value, DecisionType.Resume, null, null, bindingsFilePath),
                    adapters, cancellationToken: TestContext.Current.CancellationToken));

            Assert.Contains("Astra", thrown.Message, StringComparison.Ordinal);
            Assert.Equal(flowBeforeDecision, await File.ReadAllTextAsync(logPath, TestContext.Current.CancellationToken));
            Assert.Empty(observingCodex.ObservedInvocations);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Deciding_against_a_task_whose_journal_is_held_open_by_another_process_throws_FlowJournalHeldException_not_a_raw_IOException()
    {
        // #816's measured crash, decide's half; see FlowEventLogWriterTests for the mechanism.
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-decide-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteApprovalGateWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteApprovalGateBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);

            var pausedResult = await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);
            var pausedExecutionId = pausedResult.State.Steps.Single(s => s.StepId.Value == "a").LatestExecutionId!.Value;

            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            using var liveEngineHolder = new FileStream(
                logPath, FileMode.Append, FileAccess.Write, FileShare.Read, bufferSize: 1, useAsync: true);

            var decideOptions = new DecideOptions(
                roomDirectory, pausedExecutionId.Value, DecisionType.Resume, TargetStepId: null,
                SupplementaryExecutionId: null, bindingsFilePath);

            await Assert.ThrowsAsync<FlowJournalHeldException>(
                () => DecideCommand.ExecuteAsync(decideOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// #1650 F1, the journal arm. #1646's own fix did not close the window it was filed for: it stopped
    /// <c>WorktreeWorkspaces.Walk</c> contending <c>flow.lock</c>, but a live <c>baton run --wait</c>
    /// pump releases that lock and only <em>then</em> disposes its <c>FlowEventLogWriter</c>, so the
    /// journal handle is the LAST resource to clear and a concurrent <c>decide</c> was refused over
    /// exactly the same interval — with a different exception name. This holds the journal the way the
    /// pump does and releases it well inside <see cref="RoutineHoldBudget"/>, deterministically injecting
    /// the interleaving CI hit under load; <c>decide</c> must land, not be renamed-and-refused.
    /// <para>
    /// Discriminating against the fix's other half: this test leaves <c>flow.lock</c> entirely free, so
    /// only the bounded <c>FlowEventLogWriter</c> open can make it pass. Its sibling below does the
    /// converse.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Decide_lands_when_the_journal_is_released_shortly_after_it_starts_waiting()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-decide-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteApprovalGateWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteApprovalGateBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);

            var pausedResult = await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: cancellationToken);
            Assert.Equal(WorkflowStatus.Paused, pausedResult.State.Status);
            var pausedExecutionId = pausedResult.State.Steps.Single(s => s.StepId.Value == "a").LatestExecutionId!.Value;

            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            var liveEngineHolder = new FileStream(
                logPath, FileMode.Append, FileAccess.Write, FileShare.Read, bufferSize: 1, useAsync: true);
            // wait-ok: injected interleaving, not a wait on anything external — see RoutineHoldBudget.
            var releaseAfterDelay = Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken)
                .ContinueWith(_ => liveEngineHolder.Dispose(), cancellationToken);

            var decideOptions = new DecideOptions(
                roomDirectory, pausedExecutionId.Value, DecisionType.Resume, TargetStepId: null,
                SupplementaryExecutionId: null, bindingsFilePath);

            // Task.Run: the bounded open blocks its own thread (Thread.Sleep, matching ConcurrencyGuard),
            // so the release above must not be sitting behind this call on the same one.
            var result = await Task.Run(
                () => DecideCommand.ExecuteAsync(decideOptions, Adapters, cancellationToken: cancellationToken),
                cancellationToken);
            await releaseAfterDelay;

            Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
            Assert.All(result.State.Steps, FlowAssert.Succeeded);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// #1650 F1, the flow-lock arm — the converse control for the journal test above. The pump releases
    /// <c>flow.lock</c> BEFORE its journal handle, so a <c>decide</c> arriving early enough loses on the
    /// lock instead, at <c>MutationInterface.RecordDecisionAsync</c>'s own guard. Here the journal is
    /// left entirely free, so only that guard being bounded can make this pass.
    /// </summary>
    [Fact]
    public async Task Decide_lands_when_the_flow_lock_is_released_shortly_after_it_starts_waiting()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-decide-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteApprovalGateWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteApprovalGateBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);

            var pausedResult = await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: cancellationToken);
            Assert.Equal(WorkflowStatus.Paused, pausedResult.State.Status);
            var pausedExecutionId = pausedResult.State.Steps.Single(s => s.StepId.Value == "a").LatestExecutionId!.Value;

            var guard = ConcurrencyGuard.Acquire(roomDirectory, "baton run pump");
            // wait-ok: as in the sibling above, aimed at the lock's release point instead.
            var releaseAfterDelay = Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken)
                .ContinueWith(_ => guard.Dispose(), cancellationToken);

            var decideOptions = new DecideOptions(
                roomDirectory, pausedExecutionId.Value, DecisionType.Resume, TargetStepId: null,
                SupplementaryExecutionId: null, bindingsFilePath);

            var result = await Task.Run(
                () => DecideCommand.ExecuteAsync(decideOptions, Adapters, cancellationToken: cancellationToken),
                cancellationToken);
            await releaseAfterDelay;

            Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
            Assert.All(result.State.Steps, FlowAssert.Succeeded);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Decide_against_a_held_open_journal_through_the_real_CLI_process_exits_1_with_one_line_and_no_stack_trace()
    {
        // The full claim #816 makes is about Program.cs's top-level exception handling and the
        // process's actual exit code / stderr bytes -- neither of which the in-process
        // DecideCommand.ExecuteAsync tests above can observe, since Program.cs's top-level
        // statements aren't otherwise reachable from a unit test. This spawns the real built
        // Baton.Cli executable, the same way an operator would invoke 'baton decide'.
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-decide-proc-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            // Uses the real WorkerAdapterRegistry.Default (the "noop" bookkeeping adapter), not
            // this file's test-only "shell" adapter -- the spawned subprocess below resolves
            // through the real registry, same as an operator's actual invocation, so setup must
            // produce a room directory that registry can also resolve.
            var workflowFilePath = await WriteApprovalGateWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteNoOpApprovalGateBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);

            var pausedResult = await RunCommand.ExecuteAsync(runOptions, WorkerAdapterRegistry.Default, cancellationToken: TestContext.Current.CancellationToken);
            var pausedExecutionId = pausedResult.State.Steps.Single(s => s.StepId.Value == "a").LatestExecutionId!.Value;

            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            using var liveEngineHolder = new FileStream(
                logPath, FileMode.Append, FileAccess.Write, FileShare.Read, bufferSize: 1, useAsync: true);

            var startInfo = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("exec");
            startInfo.ArgumentList.Add(typeof(DecideCommand).Assembly.Location);
            startInfo.ArgumentList.Add("decide");
            startInfo.ArgumentList.Add(roomDirectory);
            startInfo.ArgumentList.Add("--execution");
            startInfo.ArgumentList.Add(pausedExecutionId.Value);
            startInfo.ArgumentList.Add("--type");
            startInfo.ArgumentList.Add("resume");
            startInfo.ArgumentList.Add("--bindings");
            startInfo.ArgumentList.Add(bindingsFilePath);

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start 'baton decide'.");
            var (stdout, stderr) = await BoundedProcessWait.RunToExitAsync(
                process, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.Equal(1, process.ExitCode);
            Assert.Contains("held open by another process", stderr);
            Assert.DoesNotContain("Unhandled exception", stderr);
            Assert.DoesNotContain("FlowEventLogWriter", stderr);
            Assert.DoesNotContain("   at ", stderr);
            Assert.Empty(stdout);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Reject_on_a_successful_outcome_projects_A_terminally_failed_and_B_never_dispatches()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-decide-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteApprovalGateWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteApprovalGateBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);

            var pausedResult = await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);
            var pausedExecutionId = pausedResult.State.Steps.Single(s => s.StepId.Value == "a").LatestExecutionId!.Value;

            var decideOptions = new DecideOptions(
                roomDirectory, pausedExecutionId.Value, DecisionType.Reject, TargetStepId: null,
                SupplementaryExecutionId: null, bindingsFilePath);

            var finalResult = await DecideCommand.ExecuteAsync(decideOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, finalResult.State.Status);
            Assert.Equal(StepStatus.Rejected, finalResult.State.Steps.Single(s => s.StepId.Value == "a").Status);
            Assert.Equal(StepStatus.Pending, finalResult.State.Steps.Single(s => s.StepId.Value == "b").Status);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Exhaustion_then_baton_supply_then_baton_decide_RetryWithRevision_succeeds_and_downstream_runs()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-decide-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteRetryWithRevisionWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteRetryWithRevisionBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);

            var pausedResult = await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(WorkflowStatus.Paused, pausedResult.State.Status);
            var flakyPausedState = pausedResult.State.Steps.Single(s => s.StepId.Value == "flaky");
            Assert.Equal(StepStatus.Failed, flakyPausedState.PausedOutcome);
            var pausedExecutionId = flakyPausedState.LatestExecutionId!.Value;

            var revisionFilePath = Path.Combine(testRoot, "revised.md");
            await File.WriteAllTextAsync(revisionFilePath, "revised-result", TestContext.Current.CancellationToken);
            var supplyOptions = new SupplyOptions(roomDirectory, "human", "revision", revisionFilePath, bindingsFilePath);
            var supplyResult = await SupplyCommand.ExecuteAsync(supplyOptions, Adapters, TestContext.Current.CancellationToken);
            Assert.Empty(supplyResult.Command.State.StepLessExecutions);

            var decideOptions = new DecideOptions(
                roomDirectory, pausedExecutionId.Value, DecisionType.RetryWithRevision, TargetStepId: null,
                supplyResult.ExecutionId.Value, bindingsFilePath);
            var retriedResult = await DecideCommand.ExecuteAsync(decideOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            var flakyAfterRetry = retriedResult.State.Steps.Single(s => s.StepId.Value == "flaky");
            Assert.Equal(StepStatus.Paused, flakyAfterRetry.Status);
            Assert.Equal(StepStatus.Succeeded, flakyAfterRetry.PausedOutcome);
            Assert.NotEqual(pausedExecutionId, flakyAfterRetry.LatestExecutionId);

            var resumeOptions = new DecideOptions(
                roomDirectory, flakyAfterRetry.LatestExecutionId!.Value.Value, DecisionType.Resume, TargetStepId: null,
                SupplementaryExecutionId: null, bindingsFilePath);
            var finalResult = await DecideCommand.ExecuteAsync(resumeOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, finalResult.State.Status);
            FlowAssert.Succeeded(finalResult.State.Steps.Single(s => s.StepId.Value == "flaky"));
            FlowAssert.Succeeded(finalResult.State.Steps.Single(s => s.StepId.Value == "downstream"));

            var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
            var downstreamOutput = Path.Combine(
                artifactsRoot,
                $"execution_{finalResult.State.Steps.Single(s => s.StepId.Value == "downstream").LatestExecutionId}",
                "final");
            Assert.Equal("revised-result", (await File.ReadAllTextAsync(downstreamOutput, TestContext.Current.CancellationToken)).Trim());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Baton_supply_then_baton_decide_Supersede_reruns_the_target_step_and_a_final_Resume_reaches_terminal()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-decide-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteSupersedeWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteSupersedeBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);

            var firstPauseResult = await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(WorkflowStatus.Paused, firstPauseResult.State.Status);
            var reviewerExecutionId1 = firstPauseResult.State.Steps.Single(s => s.StepId.Value == "reviewer").LatestExecutionId!.Value;
            var sourceExecutionId1 = firstPauseResult.State.Steps.Single(s => s.StepId.Value == "source").LatestExecutionId!.Value;

            var revisionFilePath = Path.Combine(testRoot, "revision.txt");
            await File.WriteAllTextAsync(revisionFilePath, "revised-plan", TestContext.Current.CancellationToken);
            var supplyOptions = new SupplyOptions(roomDirectory, "human", "revision", revisionFilePath, bindingsFilePath);
            var supplyResult = await SupplyCommand.ExecuteAsync(supplyOptions, Adapters, TestContext.Current.CancellationToken);

            var supersedeOptions = new DecideOptions(
                roomDirectory, reviewerExecutionId1.Value, DecisionType.Supersede, new StepId("source"),
                supplyResult.ExecutionId.Value, bindingsFilePath);
            var secondPauseResult = await DecideCommand.ExecuteAsync(supersedeOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Paused, secondPauseResult.State.Status);
            var sourceExecutionId2 = secondPauseResult.State.Steps.Single(s => s.StepId.Value == "source").LatestExecutionId!.Value;
            var reviewerExecutionId2 = secondPauseResult.State.Steps.Single(s => s.StepId.Value == "reviewer").LatestExecutionId!.Value;
            Assert.NotEqual(sourceExecutionId1, sourceExecutionId2);
            Assert.NotEqual(reviewerExecutionId1, reviewerExecutionId2);

            var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
            var sourceOutput2 = Path.Combine(artifactsRoot, $"execution_{sourceExecutionId2}", "plan");
            Assert.Equal("revised-plan", (await File.ReadAllTextAsync(sourceOutput2, TestContext.Current.CancellationToken)).Trim());

            var resumeOptions = new DecideOptions(
                roomDirectory, reviewerExecutionId2.Value, DecisionType.Resume, TargetStepId: null,
                SupplementaryExecutionId: null, bindingsFilePath);
            var finalResult = await DecideCommand.ExecuteAsync(resumeOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, finalResult.State.Status);
            FlowAssert.Succeeded(finalResult.State.Steps.Single(s => s.StepId.Value == "source"));
            FlowAssert.Succeeded(finalResult.State.Steps.Single(s => s.StepId.Value == "reviewer"));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task A_decision_against_a_non_paused_execution_throws_a_typed_error_and_appends_nothing()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-decide-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteApprovalGateWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteApprovalGateBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);

            var pausedResult = await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);
            var pausedExecutionId = pausedResult.State.Steps.Single(s => s.StepId.Value == "a").LatestExecutionId!.Value;

            var invalidOptions = new DecideOptions(
                roomDirectory, "not-a-real-execution-id", DecisionType.Resume, TargetStepId: null,
                SupplementaryExecutionId: null, bindingsFilePath);
            await Assert.ThrowsAsync<InvalidExternalDecisionException>(() => DecideCommand.ExecuteAsync(invalidOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken));

            // The paused workflow is still perfectly resolvable by a valid decision afterward.
            var validOptions = new DecideOptions(
                roomDirectory, pausedExecutionId.Value, DecisionType.Resume, TargetStepId: null,
                SupplementaryExecutionId: null, bindingsFilePath);
            var finalResult = await DecideCommand.ExecuteAsync(validOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(WorkflowStatus.Terminal, finalResult.State.Status);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Deciding_against_a_room_directory_with_no_snapshot_throws_a_typed_error()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-decide-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            Directory.CreateDirectory(testRoot);
            var bindingsFilePath = await WriteApprovalGateBindingsAsync(testRoot);
            var options = new DecideOptions(
                roomDirectory, "exec-1", DecisionType.Resume, TargetStepId: null, SupplementaryExecutionId: null, bindingsFilePath);

            await Assert.ThrowsAsync<SnapshotLoadException>(() => DecideCommand.ExecuteAsync(options, Adapters, cancellationToken: TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    private static async Task<string> WriteApprovalGateWorkflowAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("approval-gate"),
            1,
            [
                new WorkflowStepDefinition(
                    new StepId("a"), "a", [], ["out_a"], [], new RetryPolicy(1), new PausePoint([])),
                new WorkflowStepDefinition(
                    new StepId("b"), "b", ["out_a"], ["out_b"], [new StepId("a")], new RetryPolicy(1)),
            ]);

        var path = Path.Combine(directory, "workflow.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(definition));
        return path;
    }

    private static async Task<string> WriteApprovalGateBindingsAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["a"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("a", [], [new ProducedOutput("out_a")], []),
                WriteFileCommand("out_a", "a-out"), TimeSpan.FromSeconds(30)),
            ["b"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("b", ["out_a"], [new ProducedOutput("out_b")], []),
                CopyFirstInputCommand("out_b"), TimeSpan.FromSeconds(30)),
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }

    /// <summary>Same approval-gate workflow, bound through the production "noop" adapter
    /// (<see cref="WorkerAdapterRegistry.Default"/>) instead of this file's test-only "shell"
    /// adapter -- for the one test that spawns the real <c>baton</c> executable, which only has the
    /// production registry available.</summary>
    private static async Task<string> WriteNoOpApprovalGateBindingsAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["a"] = new WorkerBindingConfigEntry(
                NoOpWorkerAdapter.AdapterName, new WorkerContract("a", [], [new ProducedOutput("out_a")], []),
                PromptTemplate: "unused-by-noop", TimeSpan.FromSeconds(30)),
            ["b"] = new WorkerBindingConfigEntry(
                NoOpWorkerAdapter.AdapterName, new WorkerContract("b", ["out_a"], [new ProducedOutput("out_b")], []),
                PromptTemplate: "unused-by-noop", TimeSpan.FromSeconds(30)),
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }

    private static async Task<string> WriteRetryWithRevisionWorkflowAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("retry-with-revision"),
            1,
            [
                new WorkflowStepDefinition(
                    new StepId("flaky"), "flaky", [], ["result"], [], new RetryPolicy(1), new PausePoint([])),
                new WorkflowStepDefinition(
                    new StepId("downstream"), "downstream", ["result"], ["final"], [new StepId("flaky")], new RetryPolicy(1)),
            ]);

        var path = Path.Combine(directory, "workflow.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(definition));
        return path;
    }

    private static async Task<string> WriteRetryWithRevisionBindingsAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["flaky"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("flaky", [], [new ProducedOutput("result")], []),
                ConsumeSupplementaryInputElseFailCommand("result", "revision"), TimeSpan.FromSeconds(30)),
            ["downstream"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("downstream", ["result"], [new ProducedOutput("final")], []),
                CopyFirstInputCommand("final"), TimeSpan.FromSeconds(30)),
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }

    private static async Task<string> WriteSupersedeWorkflowAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("supersede"),
            1,
            [
                new WorkflowStepDefinition(new StepId("source"), "source", [], ["plan"], [], new RetryPolicy(1)),
                new WorkflowStepDefinition(
                    new StepId("reviewer"), "reviewer", ["plan"], ["verdict"], [new StepId("source")],
                    new RetryPolicy(1), new PausePoint([new StepId("source")])),
            ]);

        var path = Path.Combine(directory, "workflow.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(definition));
        return path;
    }

    private static async Task<string> WriteSupersedeBindingsAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["source"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("source", [], [new ProducedOutput("plan")], []),
                ConsumeSupplementaryInputElseWriteCommand("plan", "revision", "original-plan"), TimeSpan.FromSeconds(30)),
            ["reviewer"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("reviewer", ["plan"], [new ProducedOutput("verdict")], []),
                CopyFirstInputCommand("verdict"), TimeSpan.FromSeconds(30)),
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }

    private static string WriteFileCommand(string outputName, string content) =>
        $"echo {content}>%BATON_OUTPUT_DIR%\\{outputName}";

    private static string CopyFirstInputCommand(string outputName) =>
        $"type %BATON_INPUT_0% >%BATON_OUTPUT_DIR%\\{outputName}";

    private static string ConsumeSupplementaryInputElseFailCommand(string outputName, string supplementaryFileName) =>
        $"if defined BATON_SUPPLEMENTARY_INPUT (copy /y %BATON_SUPPLEMENTARY_INPUT%\\{supplementaryFileName} %BATON_OUTPUT_DIR%\\{outputName} >nul) else (exit /b 1)";

    private static string ConsumeSupplementaryInputElseWriteCommand(string outputName, string supplementaryFileName, string baseContent) =>
        $"if defined BATON_SUPPLEMENTARY_INPUT (copy /y %BATON_SUPPLEMENTARY_INPUT%\\{supplementaryFileName} %BATON_OUTPUT_DIR%\\{outputName} >nul) else (echo {baseContent}>%BATON_OUTPUT_DIR%\\{outputName})";
}
