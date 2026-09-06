using Baton.Tests.TestSupport;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Mutation;
using Baton.Store;
using static Baton.Tests.TestSupport.ShellWorkerCommands;

namespace Baton.Tests.Mutation;

/// <summary>
/// Integration tests: these spawn real processes through the managed <c>BatonTask</c> engine
/// (M7 Phase 7's acceptance criteria — a three-step linear workflow runs end-to-end through
/// <see cref="MutationInterface.StartWorkflowAsync"/>). No mocking of Baton.Core itself. A clean
/// exit-0 with no output classifies <c>ExecutionIndeterminate</c>, not <c>ExecutionFailed</c>
/// (#1593) — see <see cref="StartWorkflowAsync_classifies_a_clean_exit_with_no_output_as_ExecutionIndeterminate"/>.
/// </summary>
public class MutationInterfaceTests
{
    private static readonly StepId Architect = new("architect");
    private static readonly StepId Critic = new("critic");
    private static readonly StepId Publisher = new("publisher");

    [Fact]
    public async Task StartWorkflowAsync_runs_a_three_step_linear_workflow_to_completion()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-1"),
                new WorkflowTemplateId("architect-critic-publisher"),
                WorkflowTemplateVersion: 1,
                Steps:
                [
                    new WorkflowStepDefinition(Architect, "architect", [], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(1)),
                    new WorkflowStepDefinition(Critic, "critic", ["plan"], ["review"], DependsOn: [Architect], RetryPolicy: new RetryPolicy(1)),
                    new WorkflowStepDefinition(Publisher, "publisher", ["review"], ["summary"], DependsOn: [Critic], RetryPolicy: new RetryPolicy(1)),
                ]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["architect"] = new WorkerBinding.Process(
                    new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                    WriteFile("plan", "architect"),
                    TimeSpan.FromSeconds(30)),
                ["critic"] = new WorkerBinding.Process(
                    new WorkerContract("critic", ["plan"], [new ProducedOutput("review")], []),
                    CopyFirstInputTo("review"),
                    TimeSpan.FromSeconds(30)),
                ["publisher"] = new WorkerBinding.Process(
                    new WorkerContract("publisher", ["review"], [new ProducedOutput("summary")], []),
                    CopyFirstInputTo("summary"),
                    TimeSpan.FromSeconds(30)),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-1"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            Assert.All(finalState.Steps, step => Assert.Equal(StepStatus.Succeeded, step.Status));

            var publisherExecutionId = finalState.Steps.Single(s => s.StepId == Publisher).LatestExecutionId!.Value;
            var summaryPath = Path.Combine(artifactsRoot, $"execution_{publisherExecutionId}", "summary");
            Assert.True(File.Exists(summaryPath));
            Assert.Equal("architect", (await File.ReadAllTextAsync(summaryPath, TestContext.Current.CancellationToken)).Trim());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_retries_a_step_that_fails_once_then_succeeds()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var markerFilePath = Path.Combine(roomDirectory, "attempt-marker");
        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-3"),
                new WorkflowTemplateId("flaky-architect-critic"),
                WorkflowTemplateVersion: 1,
                Steps:
                [
                    new WorkflowStepDefinition(Architect, "architect", [], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(MaxAttempts: 2)),
                    new WorkflowStepDefinition(Critic, "critic", ["plan"], ["review"], DependsOn: [Architect], RetryPolicy: new RetryPolicy(1)),
                ]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["architect"] = new WorkerBinding.Process(
                    new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                    FailOnFirstAttemptThenSucceed(markerFilePath, "plan", "architect"),
                    TimeSpan.FromSeconds(30)),
                ["critic"] = new WorkerBinding.Process(
                    new WorkerContract("critic", ["plan"], [new ProducedOutput("review")], []),
                    CopyFirstInputTo("review"),
                    TimeSpan.FromSeconds(30)),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-3"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            Assert.All(finalState.Steps, step => Assert.Equal(StepStatus.Succeeded, step.Status));
            Assert.Equal(0, finalState.Steps.Single(s => s.StepId == Architect).ConsecutiveFailureCount);

            // The history shape: two distinct ExecutionIds for Architect, the first failed and
            // the second succeeded — neither event mutated or removed.
            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var architectAttempts = events
                .OfType<FlowEvent.ExecutionRequestAccepted>()
                .Where(e => e.Request.StepId == Architect)
                .Select(e => e.Request.ExecutionId)
                .ToList();
            Assert.Equal(2, architectAttempts.Count);
            Assert.Equal(architectAttempts.Distinct().Count(), architectAttempts.Count);
            Assert.Contains(events, e => e is FlowEvent.ExecutionFailed failed && architectAttempts.Contains(failed.ExecutionId));
            Assert.Contains(events, e => e is FlowEvent.ExecutionSucceeded succeeded && architectAttempts.Contains(succeeded.ExecutionId));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_classifies_a_clean_exit_with_no_output_as_ExecutionIndeterminate()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        try
        {
            var stepId = new StepId("silent-step");
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-2"),
                new WorkflowTemplateId("silent"),
                WorkflowTemplateVersion: 1,
                Steps: [new WorkflowStepDefinition(stepId, "silent", [], ["output.txt"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["silent"] = new WorkerBinding.Process(
                    new WorkerContract("silent", [], [new ProducedOutput("output.txt")], []),
                    ExitCleanlyWithoutWriting(),
                    TimeSpan.FromSeconds(30)),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-2"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            var stepState = Assert.Single(finalState.Steps);
            Assert.Equal(StepStatus.Failed, stepState.Status);
            Assert.True(stepState.IndeterminateAwaitingResolution);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var indeterminateEvent = events.OfType<FlowEvent.ExecutionIndeterminate>().Single();
            Assert.NotNull(indeterminateEvent.Reason);
            Assert.Contains("output.txt", indeterminateEvent.Reason);
            Assert.Contains("work possibly on disk", indeterminateEvent.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_appends_the_ZeroOutputsDespiteSubstantialWork_tripwire_through_the_live_dispatch_path()
    {
        // #1586 S1 (the #1594 ruling's tripwire), the wiring OutcomeClassifierTests cannot reach: that
        // suite pins SubstantialWorkNoOutputsEvidence at OutcomeClassifier.Classify's unit level with a
        // fake usage parser; nothing exercised MutationInterface's own
        // AppendZeroOutputsTripwireIfAnyAsync call site actually appending the event to a real journal
        // off a real dispatch's own ExecutionStreamLogger-captured .stdout.log. This is that proof, for
        // the live-dispatch call site specifically (the crash-recovery ToClassify call site is the
        // other one MutationInterface.cs wires this from; that one is exercised by the projection-level
        // tests, not a second live process here).
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var scriptDirectory = Path.Combine(roomDirectory, "scripts");
        try
        {
            var stepId = new StepId("substantial-but-silent");
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-tripwire"),
                new WorkflowTemplateId("tripwire"),
                WorkflowTemplateVersion: 1,
                Steps: [new WorkflowStepDefinition(stepId, "silent", [], ["output.txt"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["silent"] = new WorkerBinding.Process(
                    new WorkerContract("silent", [], [new ProducedOutput("output.txt")], []),
                    EmitSubstantialUsageThenExitWithoutWriting(scriptDirectory),
                    TimeSpan.FromSeconds(30),
                    Adapter: "agy"),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-tripwire"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            var stepState = Assert.Single(finalState.Steps);
            Assert.Equal(StepStatus.Failed, stepState.Status);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var tripwire = Assert.Single(events.OfType<FlowEvent.ZeroOutputsDespiteSubstantialWork>());
            Assert.Contains("4 turn", tripwire.Evidence);
            Assert.Contains("500", tripwire.Evidence);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_records_a_Retryable_ExecutionFailed_when_the_OS_itself_refuses_the_spawn()
    {
        // The refusal family's generic member (#747's review): BatonException, not the typed guard.
        // Retryable — not Permanent — because an OS refusal is not proven deterministic; a stuck
        // cause terminates through RetryPolicy exhaustion instead. Polarity partner to the
        // Permanent assert in the CommandLineTooLongException test below.
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        try
        {
            var stepId = new StepId("os-refused-step");
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-os-refusal"),
                new WorkflowTemplateId("os-refusal"),
                WorkflowTemplateVersion: 1,
                Steps: [new WorkflowStepDefinition(stepId, "os-refused", [], ["out.txt"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["os-refused"] = new WorkerBinding.Process(
                    new WorkerContract("os-refused", [], [new ProducedOutput("out.txt")], []),
                    new CoreDispatchTarget("dummy", []),
                    TimeSpan.FromSeconds(30)),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-os-refusal"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer,
                new OsRefusingCoreDispatcher(), cancellationToken: TestContext.Current.CancellationToken);

            var stepState = Assert.Single(finalState.Steps);
            Assert.Equal(StepStatus.Failed, stepState.Status);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var failedEvent = Assert.Single(events.OfType<FlowEvent.ExecutionFailed>());
            Assert.Equal(FailureClassification.Retryable, failedEvent.FailureClassification);
            Assert.StartsWith("Spawn refused:", failedEvent.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_records_ExecutionFailed_when_dispatch_throws_CommandLineTooLongException()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        try
        {
            var stepId = new StepId("long-cmd-step");
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-refusal"),
                new WorkflowTemplateId("refusal"),
                WorkflowTemplateVersion: 1,
                Steps: [new WorkflowStepDefinition(stepId, "long-cmd", [], ["out.txt"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["long-cmd"] = new WorkerBinding.Process(
                    new WorkerContract("long-cmd", [], [new ProducedOutput("out.txt")], []),
                    new CoreDispatchTarget("dummy", []),
                    TimeSpan.FromSeconds(30)),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var refusalMessage = "Command line length 40000 exceeds maximum allowable length of 32767.";
            var dispatcher = new RefusingCoreDispatcher(refusalMessage);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-refusal"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            var stepState = Assert.Single(finalState.Steps);
            Assert.Equal(StepStatus.Failed, stepState.Status);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var failedEvent = Assert.Single(events.OfType<FlowEvent.ExecutionFailed>());
            Assert.Equal(FailureClassification.Permanent, failedEvent.FailureClassification);
            Assert.NotNull(failedEvent.Reason);
            Assert.Contains(refusalMessage, failedEvent.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_runs_the_engine_verify_step_and_settles_Succeeded_when_it_passes()
    {
        // #1623/#1702: the real end-to-end path through a REAL pixi subprocess -- MutationInterface's
        // own gating (Verdict == Succeeded && a resolved verify command) plus the real
        // VerifyRunner.RunProcessAsync's "pixi" spawn, not a fake. `buildlock-selftest` is an existing,
        // already-fast (a few seconds), already-deterministic pixi task (tools/buildlock.py's own
        // control arm) -- reused as the fixture rather than adding a new pixi.toml entry just for this
        // test. The FAIL half is covered by VerifyRunnerTests against a fake command instead of a real
        // gates failure, which would be slow and not actually more informative about this wiring.
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-verify"),
                new WorkflowTemplateId("verify"),
                WorkflowTemplateVersion: 1,
                Steps: [new WorkflowStepDefinition(Architect, "architect", [], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["architect"] = new WorkerBinding.Process(
                    new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                    WriteFile("plan", "architect") with { WorkingDirectory = RoleDefaultVerifyWorkspace() },
                    TimeSpan.FromSeconds(30),
                    VerifyPixiTask: "buildlock-selftest"),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-verify"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            var architect = Assert.Single(finalState.Steps);
            Assert.Equal(StepStatus.Succeeded, architect.Status);
            Assert.Null(architect.IndeterminateReason);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Single(events.OfType<FlowEvent.VerifyStarted>());
            Assert.Single(events.OfType<FlowEvent.VerifyPassed>());
            Assert.Empty(events.OfType<FlowEvent.VerifyFailed>());
            Assert.Single(events.OfType<FlowEvent.ExecutionSucceeded>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_settles_Succeeded_with_VerifyNotRun_when_the_role_task_is_absent()
    {
        // #1702 (the measured defect this test replaces #1623/F6's own "VerifyPixiTask fails" test
        // with): a role's baked-in task the workspace's own `pixi task list` does not contain is a
        // distinct not-run outcome, never a gate failure -- the ExecutionSucceeded classification
        // decides the room word unassisted, and the report the worker wrote is still delivered
        // (DispatchCommand.CopyPrimaryOutputToOverride's own #1702 fix covers the CLI-level half of
        // that; this test covers the engine-level settle).
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-verify-not-run"),
                new WorkflowTemplateId("verify-not-run"),
                WorkflowTemplateVersion: 1,
                Steps: [new WorkflowStepDefinition(Architect, "architect", [], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["architect"] = new WorkerBinding.Process(
                    new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                    WriteFile("plan", "architect") with { WorkingDirectory = RoleDefaultVerifyWorkspace() },
                    TimeSpan.FromSeconds(30),
                    VerifyPixiTask: "this-task-definitely-does-not-exist"),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-verify-not-run"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            var architect = Assert.Single(finalState.Steps);
            Assert.Equal(StepStatus.Succeeded, architect.Status);
            Assert.Null(architect.IndeterminateReason);
            Assert.False(architect.IndeterminateAwaitingResolution);
            Assert.Equal("task absent: this-task-definitely-does-not-exist", architect.VerifyNotRunReason);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Empty(events.OfType<FlowEvent.VerifyStarted>());
            Assert.Empty(events.OfType<FlowEvent.VerifyFailed>());
            Assert.Empty(events.OfType<FlowEvent.VerifyPassed>());
            var notRun = Assert.Single(events.OfType<FlowEvent.VerifyNotRun>());
            Assert.Equal("task absent: this-task-definitely-does-not-exist", notRun.Reason);
            Assert.Single(events.OfType<FlowEvent.ExecutionSucceeded>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>
    /// #1708 H1, red-first end to end: a worker writes its own <c>.baton/verify</c> saying <c>exit 0</c>
    /// during its execution, into the very workspace it was dispatched against. The engine must run the
    /// workspace's COMMITTED declaration instead — which here goes red — and journal
    /// <see cref="FlowEvent.VerifyDeclarationIgnored"/> naming both sides. Against the pre-fix code the
    /// worker's file wins, verify exits 0, and the step settles <c>Succeeded</c> with its gate skipped.
    /// </summary>
    [Fact]
    public async Task StartWorkflowAsync_ignores_a_verify_declaration_the_worker_wrote_and_runs_the_committed_one()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var workspace = Path.Combine(Path.GetTempPath(), $"workspace-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        try
        {
            // A workspace whose committed declaration fails. Nothing else in this test can make verify
            // go red, so a red settle proves the committed line is what ran.
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(Path.Combine(workspace, ".baton"));
            await File.WriteAllTextAsync(
                Path.Combine(workspace, ".baton", "verify"),
                "python -c \"import sys; sys.exit(1)\"\n",
                TestContext.Current.CancellationToken);
            TempGitRepository.InitWithEverythingCommitted(workspace);

            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-verify-worker-authored"),
                new WorkflowTemplateId("verify-worker-authored"),
                WorkflowTemplateVersion: 1,
                Steps: [new WorkflowStepDefinition(Architect, "architect", [], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]);

            // The worker overwrites .baton/verify with a verifier that always passes, then satisfies its
            // own output contract and exits 0 -- an ordinary clean run that would settle Succeeded.
            var worker = new CoreDispatchTarget(
                "cmd",
                ["/c", "echo exit 0 >.baton\\verify & echo plan>%BATON_OUTPUT_DIR%\\plan"])
            {
                WorkingDirectory = workspace,
            };

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["architect"] = new WorkerBinding.Process(
                    new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                    worker,
                    TimeSpan.FromSeconds(30)),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-verify-worker-authored"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            // The worker really did write the file -- otherwise this test proves nothing about ignoring it.
            Assert.Equal(
                "exit 0",
                Baton.Mutation.VerifyCommandResolver.ReadWorkingTreeRepoDeclaration(workspace));

            var architect = Assert.Single(finalState.Steps);
            Assert.Equal(StepStatus.Failed, architect.Status);
            Assert.NotNull(architect.IndeterminateReason);
            Assert.Null(architect.VerifyNotRunReason);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Single(events.OfType<FlowEvent.VerifyStarted>());
            Assert.Single(events.OfType<FlowEvent.VerifyFailed>());
            Assert.Empty(events.OfType<FlowEvent.VerifyPassed>());

            var ignored = Assert.Single(events.OfType<FlowEvent.VerifyDeclarationIgnored>());
            Assert.Equal(
                VerifyCommandResolver.DeclarationDigest("python -c \"import sys; sys.exit(1)\""),
                ignored.CommittedDigest);
            Assert.Equal(VerifyCommandResolver.DeclarationDigest("exit 0"), ignored.WorkingTreeDigest);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
            DirectoryCleanup.DeleteRecursively(workspace);
        }
    }

    /// <summary>
    /// #1708 L1, red-first: the drift record is appended whenever the working-tree declaration differs
    /// from the one that graded the run — <b>not only on a Succeeded execution</b>. Here the worker
    /// writes <c>.baton/verify</c> and then exits NON-ZERO, so no verify ever runs; spec/baton.md §3
    /// states why that operator question still deserves an answer. Against the pre-fix code this event
    /// was inside the <c>Verdict == Succeeded</c> branch and the assertion below finds nothing.
    /// </summary>
    [Fact]
    public async Task StartWorkflowAsync_journals_a_drifted_verify_declaration_even_when_the_execution_FAILS()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var workspace = Path.Combine(Path.GetTempPath(), $"workspace-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        try
        {
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(Path.Combine(workspace, ".baton"));
            await File.WriteAllTextAsync(
                Path.Combine(workspace, ".baton", "verify"),
                "python -c \"import sys; sys.exit(1)\"\n",
                TestContext.Current.CancellationToken);
            TempGitRepository.InitWithEverythingCommitted(workspace);
            TempGitRepository.SetReviewedBaselineAtHead(workspace);

            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-verify-drift-on-failure"),
                new WorkflowTemplateId("verify-drift-on-failure"),
                WorkflowTemplateVersion: 1,
                Steps: [new WorkflowStepDefinition(Architect, "architect", [], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]);

            // Writes the declaration, then fails. Nothing here can reach the verify block at all.
            var worker = new CoreDispatchTarget(
                "cmd",
                ["/c", "echo exit 0 >.baton\\verify & exit 7"])
            {
                WorkingDirectory = workspace,
            };

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["architect"] = new WorkerBinding.Process(
                    new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                    worker,
                    TimeSpan.FromSeconds(30)),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-verify-drift-on-failure"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            // The premise: the execution really did NOT succeed, so this is the branch the pre-fix code
            // skipped -- and the worker really did write the file.
            var architect = Assert.Single(finalState.Steps);
            Assert.NotEqual(StepStatus.Succeeded, architect.Status);
            Assert.Equal("exit 0", VerifyCommandResolver.ReadWorkingTreeRepoDeclaration(workspace));

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Empty(events.OfType<FlowEvent.VerifyStarted>());

            var ignored = Assert.Single(events.OfType<FlowEvent.VerifyDeclarationIgnored>());
            Assert.Equal(
                VerifyCommandResolver.DeclarationDigest("python -c \"import sys; sys.exit(1)\""),
                ignored.CommittedDigest);
            Assert.Equal(VerifyCommandResolver.DeclarationDigest("exit 0"), ignored.WorkingTreeDigest);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
            DirectoryCleanup.DeleteRecursively(workspace);
        }
    }

    /// <summary>
    /// #1708 M1's fallback, end to end: a workspace with no <c>origin/main</c> still runs its committed
    /// declaration, and the journal announces the narrower boundary spec/baton.md §3 scopes. Pins that
    /// <see cref="FlowEvent.VerifyDeclarationUnreviewed"/> has a real producer on the live path — a
    /// serialization round-trip alone would not.
    /// </summary>
    [Fact]
    public async Task StartWorkflowAsync_journals_an_unreviewed_declaration_when_the_workspace_has_no_origin_main()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var workspace = Path.Combine(Path.GetTempPath(), $"workspace-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        try
        {
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(Path.Combine(workspace, ".baton"));
            await File.WriteAllTextAsync(
                Path.Combine(workspace, ".baton", "verify"),
                "exit 0\n",
                TestContext.Current.CancellationToken);
            // No SetReviewedBaselineAtHead: this repo has no origin/main, which is the whole fixture.
            TempGitRepository.InitWithEverythingCommitted(workspace);

            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-verify-unreviewed"),
                new WorkflowTemplateId("verify-unreviewed"),
                WorkflowTemplateVersion: 1,
                Steps: [new WorkflowStepDefinition(Architect, "architect", [], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]);

            var worker = new CoreDispatchTarget("cmd", ["/c", "echo plan>%BATON_OUTPUT_DIR%\\plan"])
            {
                WorkingDirectory = workspace,
            };

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["architect"] = new WorkerBinding.Process(
                    new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                    worker,
                    TimeSpan.FromSeconds(30)),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-verify-unreviewed"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            // The HEAD declaration really did take effect -- it ran, and it passed.
            var architect = Assert.Single(finalState.Steps);
            Assert.Equal(StepStatus.Succeeded, architect.Status);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Single(events.OfType<FlowEvent.VerifyPassed>());

            var unreviewed = Assert.Single(events.OfType<FlowEvent.VerifyDeclarationUnreviewed>());
            Assert.Equal(VerifyCommandResolver.DeclarationDigest("exit 0"), unreviewed.Digest);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
            DirectoryCleanup.DeleteRecursively(workspace);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_verify_override_wins_over_the_role_default_and_settles_Indeterminate_when_it_runs_red()
    {
        // #1702 item 5's discriminating control (spec/baton.md §3 states the general rule this
        // pins). The role default here (buildlock-selftest) would pass, proving the override -- not
        // the role default -- is what actually ran.
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-verify-override-red"),
                new WorkflowTemplateId("verify-override-red"),
                WorkflowTemplateVersion: 1,
                Steps: [new WorkflowStepDefinition(Architect, "architect", [], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["architect"] = new WorkerBinding.Process(
                    new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                    WriteFile("plan", "architect") with { WorkingDirectory = RoleDefaultVerifyWorkspace() },
                    TimeSpan.FromSeconds(30),
                    VerifyPixiTask: "buildlock-selftest",
                    VerifyCommandOverride: "python -c \"import sys; sys.exit(1)\""),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-verify-override-red"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            var architect = Assert.Single(finalState.Steps);
            Assert.Equal(StepStatus.Failed, architect.Status);
            Assert.NotNull(architect.IndeterminateReason);
            Assert.True(architect.RetryForeclosed);
            Assert.Null(architect.VerifyNotRunReason);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Single(events.OfType<FlowEvent.VerifyStarted>());
            var verifyFailed = Assert.Single(events.OfType<FlowEvent.VerifyFailed>());
            Assert.Equal(VerifyFailedKind.GatesFailed, verifyFailed.Kind);
            Assert.Empty(events.OfType<FlowEvent.VerifyPassed>());
            Assert.Empty(events.OfType<FlowEvent.VerifyNotRun>());
            Assert.Empty(events.OfType<FlowEvent.ExecutionSucceeded>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_skips_verify_when_execution_classification_is_failed()
    {
        // #1623 / F6: a failed worker never triggers verify. #1593 (found-while-fixing, this PR): a
        // clean exit-0 with a missing declared output no longer classifies ExecutionFailed -- it
        // settles Indeterminate instead, so ExitCleanlyWithoutWriting() no longer produces the "an
        // ordinary Failed worker" shape this test needs. Swapped for ExitWithFailureCode(), the same
        // migration the review found legitimate across MutationInterfaceRetryBackoffTests,
        // PumpCheckpointCarryTests, LiveCancellationEndToEndTests and ResolveCommandEndToEndTests --
        // missed here originally since this file wasn't in that sweep.
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-verify-skip"),
                new WorkflowTemplateId("verify-skip"),
                WorkflowTemplateVersion: 1,
                Steps: [new WorkflowStepDefinition(Architect, "architect", [], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["architect"] = new WorkerBinding.Process(
                    new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                    ExitWithFailureCode() with { WorkingDirectory = RoleDefaultVerifyWorkspace() },
                    TimeSpan.FromSeconds(30),
                    VerifyPixiTask: "buildlock-selftest"),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-verify-skip"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            var architect = Assert.Single(finalState.Steps);
            Assert.Equal(StepStatus.Failed, architect.Status);
            Assert.Null(architect.IndeterminateReason);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Empty(events.OfType<FlowEvent.VerifyStarted>());
            Assert.Empty(events.OfType<FlowEvent.VerifyPassed>());
            Assert.Empty(events.OfType<FlowEvent.VerifyFailed>());
            Assert.Single(events.OfType<FlowEvent.ExecutionFailed>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_does_not_spawn_verify_when_role_has_no_VerifyPixiTask()
    {
        // #1623 / F6: a role without VerifyPixiTask does not spawn verify
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-verify-none"),
                new WorkflowTemplateId("verify-none"),
                WorkflowTemplateVersion: 1,
                Steps: [new WorkflowStepDefinition(Architect, "architect", [], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["architect"] = new WorkerBinding.Process(
                    new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                    WriteFile("plan", "architect"),
                    TimeSpan.FromSeconds(30),
                    VerifyPixiTask: null),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-verify-none"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            var architect = Assert.Single(finalState.Steps);
            Assert.Equal(StepStatus.Succeeded, architect.Status);
            Assert.Null(architect.IndeterminateReason);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Empty(events.OfType<FlowEvent.VerifyStarted>());
            Assert.Single(events.OfType<FlowEvent.ExecutionSucceeded>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_operator_cancel_during_verify_settles_Cancelled_not_Indeterminate()
    {
        // #1623 re-review N3: the operator's own cancel landing inside the verify window is journalled
        // as ExecutionCancelled, not VerifyFailed/Indeterminate -- see MutationInterface.cs's own
        // comment on the branch under test for why. Foreclosing retry here would leave no discharge
        // verb (U1). VerifyStarted still survives as the diagnostic record that verify was running
        // when the cancel landed.
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-verify-cancel"),
                new WorkflowTemplateId("verify-cancel"),
                WorkflowTemplateVersion: 1,
                Steps: [new WorkflowStepDefinition(Architect, "architect", [], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["architect"] = new WorkerBinding.Process(
                    new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                    WriteFile("plan", "architect") with { WorkingDirectory = RoleDefaultVerifyWorkspace() },
                    TimeSpan.FromSeconds(30),
                    VerifyPixiTask: "buildlock-selftest"),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);

            using var cts = new CancellationTokenSource();
            var dispatcher = new CancellingAtCompletionDispatcher(writer, cts);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-verify-cancel"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: cts.Token);

            var architect = Assert.Single(finalState.Steps);
            Assert.Equal(StepStatus.Cancelled, architect.Status);
            Assert.Null(architect.IndeterminateReason);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Single(events.OfType<FlowEvent.VerifyStarted>());
            Assert.Empty(events.OfType<FlowEvent.VerifyFailed>());
            Assert.Single(events.OfType<FlowEvent.ExecutionCancelled>());
            Assert.Empty(events.OfType<FlowEvent.ExecutionSucceeded>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "pixi.toml")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate pixi.toml above " + AppContext.BaseDirectory);
    }

    /// <summary>
    /// The working directory every verify test in this class dispatches against: a committed
    /// SUBDIRECTORY of the repo, never the repo root.
    /// <para>
    /// #1958 gave this repo its own <c>.baton/verify</c>, and a repo declaration outranks the role's
    /// <c>VerifyPixiTask</c> (spec/baton.md §3, "Verify command resolution"). A test dispatched at the
    /// repo ROOT therefore no longer exercises the role-default arm it was written for — it resolves
    /// the repo declaration and runs Baton's own gate set inside the test process. That is what turned
    /// the two role-default tests below red on CI, where <c>origin/main</c> does not resolve
    /// (<c>actions/checkout</c> at its default depth) and
    /// <see cref="VerifyCommandResolver.ReadCommittedRepoDeclarationAsync"/> falls back to <c>HEAD</c>,
    /// which carries the declaration. Locally the merge-base predates it, so the arm silently differs
    /// between the two environments — which is why the premise below is asserted rather than assumed.
    /// </para>
    /// <para>
    /// A subdirectory is what makes the role default resolve again: that read resolves the path against
    /// the WORKSPACE (the load-bearing <c>./</c> in its <c>git show</c> revision), so <c>tools</c>
    /// carries no declaration of its own, while pixi's ancestor walk still finds the repo manifest — so
    /// <c>pixi task list</c> and <c>pixi run &lt;task&gt;</c> both behave exactly as they did at the root.
    /// </para>
    /// </summary>
    private static string RoleDefaultVerifyWorkspace()
    {
        var root = RepoRoot();
        var workspace = Path.Combine(root, "tools");

        // Read first, and in both directions: the root really does declare one (or this fixture is
        // guarding against nothing), and this workspace really does not (or it is the root's case
        // again, under another name). A `tools/.baton/verify` added later fails HERE, in milliseconds,
        // rather than by quietly spending minutes running the fast gate set inside a unit test.
        Assert.NotNull(VerifyCommandResolver.ReadWorkingTreeRepoDeclaration(root));
        Assert.Null(VerifyCommandResolver.ReadWorkingTreeRepoDeclaration(workspace));

        return workspace;
    }

    [Fact]
    public async Task StartWorkflowAsync_arrests_an_execution_that_crosses_its_token_budget()
    {
        // #1623 ruling addendum: exercises the real MutationInterface wiring (the linked
        // CancellationTokenSource, the OnStdoutLine composition, the ExecutionArrested append instead
        // of an ordinary outcome) against a fake ICoreDispatcher that never spawns a real process --
        // TokenBudgetMonitorTests already pins the accumulation logic in isolation, and this is the
        // "wired correctly, not just correct in isolation" proof, the same split
        // StartWorkflowAsync_appends_the_ZeroOutputsDespiteSubstantialWork_tripwire... above already
        // uses for OutcomeClassifier's own tripwire.
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-arrest"),
                new WorkflowTemplateId("arrest"),
                WorkflowTemplateVersion: 1,
                Steps: [new WorkflowStepDefinition(Architect, "architect", [], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(3))]);

            // #1706: the billed component on claude is cache_creation -- the input/output columns on a
            // mid-stream `assistant` line are placeholders and are no longer read at all.
            const string usageLine = """{"type":"assistant","message":{"usage":{"input_tokens":2,"cache_creation_input_tokens":700000,"cache_read_input_tokens":500000,"output_tokens":3}}}""";
            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["architect"] = new WorkerBinding.Process(
                    new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                    new CoreDispatchTarget("cmd", ["/c", "exit 0"]),
                    TimeSpan.FromSeconds(30),
                    Adapter: "claude",
                    TokenBudget: 1000),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new ArrestingCoreDispatcher(usageLine);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-arrest"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            var architect = Assert.Single(finalState.Steps);
            Assert.Equal(StepStatus.Failed, architect.Status);
            Assert.NotNull(architect.IndeterminateReason);
            Assert.True(architect.RetryForeclosed);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Empty(events.OfType<FlowEvent.ExecutionSucceeded>());
            Assert.Empty(events.OfType<FlowEvent.ExecutionCancelled>());
            var arrested = Assert.Single(events.OfType<FlowEvent.ExecutionArrested>());
            Assert.Null(arrested.Usage?.TokensIn);
            Assert.Null(arrested.Usage?.TokensOut);
            // #1682: billed (what the budget actually arrested on) is 700,000 for this single line,
            // crossing the 1,000 budget -- and the reason is recorded on the wire, not just inferred
            // from Arrested being true. #1706: the floor flag rides the same event, so a reader of the
            // ledger can tell a claude arrest figure from a complete one without knowing the vendor.
            Assert.Equal(700000, arrested.Usage?.BilledTokens);
            Assert.True(arrested.Usage?.BilledIsFloor);
            Assert.Equal(ArrestReason.TokenBudget, arrested.Reason);
            // #1745: the live adapter this execution actually ran on, so StateProjector's arrest text
            // can name which vendor's (possibly per-adapter) budget figure fired.
            Assert.Equal("claude", arrested.Adapter);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_stamps_PeakBilledInWindow_on_ExecutionSucceeded_when_a_live_budget_monitor_watched_the_run()
    {
        // #1709: the wiring the arrest test above proves for ExecutionArrested, mirrored for the
        // ordinary Succeeded case an arrest never reaches -- exercises the real MutationInterface
        // wiring (a real TokenBudgetMonitor built from binding.TokenBudget, live through the real
        // BatonTask/CoreDispatcher pipeline, never crossing its budget) so ToOutcomeEvent's
        // budgetMonitor.SnapshotPeakBilledInWindow() call is proven live, not only at
        // FlowEventSerializationTests'/ExecutionUsageProjectorTests' unit level.
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var scriptDirectory = Path.Combine(roomDirectory, "scripts");
        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-peak-succeeded"),
                new WorkflowTemplateId("peak-succeeded"),
                WorkflowTemplateVersion: 1,
                Steps: [new WorkflowStepDefinition(Architect, "architect", [], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["architect"] = new WorkerBinding.Process(
                    new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                    EmitUsageLineThenWriteFile(scriptDirectory, cacheCreationInputTokens: 5000, "plan", "architect"),
                    TimeSpan.FromSeconds(30),
                    Adapter: "claude",
                    // Well above the 5,000 the usage line carries -- this must complete normally,
                    // never arrest, so the peak reaches ExecutionSucceeded rather than ExecutionArrested.
                    TokenBudget: 1_000_000),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-peak-succeeded"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            Assert.All(finalState.Steps, step => Assert.Equal(StepStatus.Succeeded, step.Status));

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Empty(events.OfType<FlowEvent.ExecutionArrested>());
            var succeeded = Assert.Single(events.OfType<FlowEvent.ExecutionSucceeded>());
            Assert.Equal(5000, succeeded.PeakBilledInWindow);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_stamps_a_null_PeakBilledInWindow_on_ExecutionSucceeded_when_the_run_never_admitted_a_usage_sample()
    {
        // #1709 review: the monitor is IN SCOPE (binding.TokenBudget is set, so budgetMonitor is
        // constructed and watching) but the worker's stdout never carries a single usage-bearing
        // line before it exits 0 -- exactly the population the HIGH finding named as reachable on
        // both vendors. Proves SnapshotPeakBilledInWindow() reports null, not a fabricated 0, when
        // that happens.
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-peak-no-sample"),
                new WorkflowTemplateId("peak-no-sample"),
                WorkflowTemplateVersion: 1,
                Steps: [new WorkflowStepDefinition(Architect, "architect", [], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["architect"] = new WorkerBinding.Process(
                    new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                    WriteFile("plan", "no usage line ever written"),
                    TimeSpan.FromSeconds(30),
                    Adapter: "claude",
                    // A monitor is constructed and watching solely because this is set -- the script
                    // above never crosses it, and never emits anything the usage parser reads at all.
                    TokenBudget: 1_000_000),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-peak-no-sample"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            Assert.All(finalState.Steps, step => Assert.Equal(StepStatus.Succeeded, step.Status));

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Empty(events.OfType<FlowEvent.ExecutionArrested>());
            var succeeded = Assert.Single(events.OfType<FlowEvent.ExecutionSucceeded>());
            Assert.Null(succeeded.PeakBilledInWindow);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_arrests_an_execution_that_crosses_its_tool_step_cap_with_zero_usage_lines()
    {
        // #1682: the SECOND, independent producer -- exercises the real MutationInterface wiring the
        // same way the token-budget test above does, but with NO TokenBudget set at all and a stream
        // that never parses as usage, proving the cap fires "independent of usage parsing" through the
        // live dispatch path, not just in TokenBudgetMonitorTests' isolation.
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-toolcap"),
                new WorkflowTemplateId("toolcap"),
                WorkflowTemplateVersion: 1,
                Steps: [new WorkflowStepDefinition(Architect, "architect", [], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(3))]);

            // #1686 review F2: DONE, not ACTIVE -- AgyUsageParser.CountToolSteps's own doc has the fixed unit.
            const string toolStepLine = """{"event":"step_update","step_update":{"state":"DONE","step_type":"tool","tool_name":"run_command"}}""";
            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["architect"] = new WorkerBinding.Process(
                    new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                    new CoreDispatchTarget("cmd", ["/c", "exit 0"]),
                    TimeSpan.FromSeconds(30),
                    Adapter: "agy",
                    MaxToolSteps: 2),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new ArrestingCoreDispatcher(toolStepLine, repeatCount: 3);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-toolcap"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            var architect = Assert.Single(finalState.Steps);
            Assert.Equal(StepStatus.Failed, architect.Status);
            Assert.True(architect.RetryForeclosed);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var arrested = Assert.Single(events.OfType<FlowEvent.ExecutionArrested>());
            Assert.Equal(ArrestReason.ToolStepCap, arrested.Reason);
            Assert.Equal(3, arrested.ToolStepCount);
            Assert.Null(arrested.Usage?.BilledTokens);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>
    /// A fake dispatch whose stdout is <paramref name="repeatCount"/> copies of a single,
    /// arrest-triggering line — mirrors a real worker process being torn down once
    /// <see cref="TokenBudgetMonitor"/>'s own linked cancellation fires, per
    /// <c>ICoreDispatcher.DispatchAsync</c>'s documented "cancellation comes back as a normal
    /// CoreDispatchResult" contract (never <see cref="OperationCanceledException"/>).
    /// </summary>
    private sealed class ArrestingCoreDispatcher(string usageLine, int repeatCount = 1) : ICoreDispatcher
    {
        public async Task<CoreDispatchResult> DispatchAsync(ExecutionRequest request, CoreDispatchTarget target, CancellationToken cancellationToken = default)
        {
            for (var i = 0; i < repeatCount; i++)
            {
                target.OnStdoutLine?.Invoke(usageLine);
            }

            var tcs = new TaskCompletionSource();
            await using var registration = cancellationToken.Register(() => tcs.TrySetResult());
            // Not a timing expectation: the arrest cancels this token in milliseconds. The ceiling only
            // stops a regression from hanging the suite forever, so it is set well above any plausible
            // real wait rather than tuned to the expected one.
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(120), cancellationToken: CancellationToken.None);

            return new CoreDispatchResult(-1, CoreExitReason.CancelRequested);
        }
    }

    private sealed class RefusingCoreDispatcher(string refusalMessage) : ICoreDispatcher
    {
        public Task<CoreDispatchResult> DispatchAsync(ExecutionRequest request, CoreDispatchTarget target, CancellationToken cancellationToken = default)
        {
            throw new CommandLineTooLongException(refusalMessage);
        }
    }

    private sealed class OsRefusingCoreDispatcher : ICoreDispatcher
    {
        public Task<CoreDispatchResult> DispatchAsync(ExecutionRequest request, CoreDispatchTarget target, CancellationToken cancellationToken = default)
        {
            // The binding's own exception type, the shape a missing binary or a bad working
            // directory actually surfaces as (#747's review, finding 3).
            throw new Baton.Core.BatonException(Baton.Core.BatonErrorCode.SpawnFailed);
        }
    }

    private sealed class CancellingAtCompletionDispatcher(FlowEventLogWriter writer, CancellationTokenSource cts) : ICoreDispatcher
    {
        private readonly CoreDispatcher _inner = new(writer, writer);

        public async Task<CoreDispatchResult> DispatchAsync(ExecutionRequest request, CoreDispatchTarget target, CancellationToken cancellationToken = default)
        {
            var result = await _inner.DispatchAsync(request, target, cancellationToken).ConfigureAwait(false);
            cts.Cancel();
            return result;
        }
    }
}


