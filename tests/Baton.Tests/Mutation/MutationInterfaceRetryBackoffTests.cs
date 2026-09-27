using System.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Mutation;
using Baton.Outcomes;
using Baton.Projection;
using Baton.Scheduling;
using Baton.Store;
using Baton.Tests.TestSupport;
using static Baton.Tests.TestSupport.ShellWorkerCommands;

namespace Baton.Tests.Mutation;

public class MutationInterfaceRetryBackoffTests
{
    private static readonly StepId StepA = new("step-a");
    private static readonly StepId StepB = new("step-b");

    // Every fake-clock advance below happens only AFTER the event that proves the pump committed
    // to a deadline is visible in the log. Advancing on a wall-clock guess (`await Task.Delay(100)`
    // then Advance) is a race: under load the advance can land before the first attempt has even
    // failed, the deferral deadline then lands after the already-spent advance, and the pump waits
    // on a fake instant nobody will ever reach — the test hangs rather than fails. The poll below
    // is the positive signal; the WaitAsync timeouts on the pump awaits are the backstop that turns
    // any future reintroduction of the race into a red test instead of a hung suite.
    private static async Task<T> WaitForEventAsync<T>(FlowEventLogReader reader, Task pumpTask, CancellationToken cancellationToken)
        where T : FlowEvent
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            var events = await reader.ReadAllAsync(cancellationToken);
            if (events.OfType<T>().FirstOrDefault() is { } found)
            {
                return found;
            }

            if (pumpTask.IsCompleted)
            {
                await pumpTask; // surfaces the pump's own exception if it faulted
                Assert.Fail($"Pump completed without appending {typeof(T).Name}.");
            }

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(60), $"Timed out waiting for {typeof(T).Name}.");
            await Task.Delay(10, cancellationToken);
        }
    }

    private static readonly TimeSpan PumpCompletionTimeout = TimeSpan.FromSeconds(30);

    // A single Advance is not enough to release the pump, even after the deferral event is visible:
    // the pump reads the clock and then creates its relative Task.Delay in two steps, so an advance
    // landing in that gap starts the timer from the already-advanced clock — due at deadline +
    // delay, an instant nothing will ever advance to. Harmless under a real clock (time keeps
    // moving and the pump re-checks readiness on every wake, so it just wakes late); a strand only
    // a fake clock can produce. Advancing repeatedly until the pump returns guarantees some advance
    // lands after the timer exists. Overshooting the deadline is safe — readiness is `now >=
    // notBefore`, never an exact-instant match.
    private static async Task<FlowState> AdvanceUntilPumpCompletesAsync(
        FakeTimeProvider fakeTime, Task<FlowState> pumpTask, TimeSpan step)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!pumpTask.IsCompleted)
        {
            Assert.True(
                stopwatch.Elapsed < PumpCompletionTimeout,
                "Pump did not complete while the clock kept advancing past every deferral deadline.");
            fakeTime.Advance(step);
            await Task.Delay(10);
        }

        return await pumpTask;
    }

    // 1. Fails on a zero-delay retry (Test 1)
    // Mutation control note: Zeroing the delay in GetRetryObligations causes test 1 to fail (dispatch occurs at t+0) while test 2 remains green.
    [Fact]
    public async Task Test1_Fails_on_zero_delay_retry_steady_backoff_defers_execution()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var markerPath = Path.Combine(roomDirectory, "attempt-marker");
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));
        var expectedLimits = new ExecutionLimitEvidence(
            TimeSpan.FromSeconds(30), TokenBudget: 5000, MaxToolSteps: 25, BilledRateLimit: 1200,
            ChosenKey: "template-retry-1/worker-a");

        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-retry-1"),
                new WorkflowTemplateId("template-retry-1"),
                WorkflowTemplateVersion: 1,
                Steps:
                [
                    new WorkflowStepDefinition(
                        StepA,
                        "worker-a",
                        Inputs: [],
                        Outputs: ["out.txt"],
                        DependsOn: [],
                        RetryPolicy: new RetryPolicy(MaxAttempts: 2, Backoff: BackoffPolicy.Steady))
                ]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["worker-a"] = new WorkerBinding.Process(
                    new WorkerContract("worker-a", [], [new ProducedOutput("out.txt")], []),
                    FailOnFirstAttemptThenSucceed(markerPath, "out.txt", "content"),
                    TimeSpan.FromSeconds(30))
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);

            // Run pump in background
            bindings["worker-a"] = ((WorkerBinding.Process)bindings["worker-a"]) with
            {
                TokenBudget = 5000,
                MaxToolSteps = 25,
                BilledRateLimit = 1200,
                LimitEvidence = expectedLimits,
            };
            var pumpTask = MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-1"),
                roomDirectory,
                snapshot,
                bindings,
                artifactsRoot,
                reader,
                writer,
                dispatcher,
                timeProvider: fakeTime,
                jitterSource: () => 0.0, // floor sample: Steady initial 1s * 0.5 = 500ms
                cancellationToken: TestContext.Current.CancellationToken);

            // Positive signal that attempt 1 failed and the pump committed to a deadline.
            var retryEvent = await WaitForEventAsync<FlowEvent.StepRetryScheduled>(reader, pumpTask, TestContext.Current.CancellationToken);

            Assert.True(retryEvent.RetryDelayMs >= 500, $"Expected DelayMs >= 500, got {retryEvent.RetryDelayMs}");

            // No second attempt at t+0. This is the assertion the mutation control keys on: with the
            // delay zeroed, attempt 2 dispatches in real time before any advance, and this reads 2.
            var eventsMid = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Single(eventsMid.OfType<FlowEvent.ExecutionRequestAccepted>());

            // Advance to t + DelayMs - 1ms: still no second attempt. Best-effort as a negative (the
            // grace period can only catch a dispatch that happens promptly); the exact boundary
            // semantics are pinned deterministically by DependencyResolverTests' clamp tests.
            fakeTime.Advance(TimeSpan.FromMilliseconds(retryEvent.RetryDelayMs - 1));
            await Task.Delay(50, TestContext.Current.CancellationToken);
            var eventsBefore = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Single(eventsBefore.OfType<FlowEvent.ExecutionRequestAccepted>());

            // Advance to t + DelayMs and beyond: second attempt dispatches and succeeds
            var finalState = await AdvanceUntilPumpCompletesAsync(
                fakeTime, pumpTask, TimeSpan.FromMilliseconds(retryEvent.RetryDelayMs));

            Assert.Equal(StepStatus.Succeeded, finalState.Steps.Single(s => s.StepId == StepA).Status);

            var eventsFinal = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var acceptedFinal = eventsFinal.OfType<FlowEvent.ExecutionRequestAccepted>().ToList();
            Assert.Equal(2, acceptedFinal.Count);
            Assert.All(acceptedFinal, accepted => Assert.Equal(expectedLimits, accepted.Request.Limits));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    // 2. Polarity (Test 2)
    [Fact]
    public async Task Test2_Backoff_none_dispatches_retry_immediately_at_t0()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var markerPath = Path.Combine(roomDirectory, "attempt-marker");
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));

        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-retry-2"),
                new WorkflowTemplateId("template-retry-2"),
                WorkflowTemplateVersion: 1,
                Steps:
                [
                    new WorkflowStepDefinition(
                        StepA,
                        "worker-a",
                        Inputs: [],
                        Outputs: ["out.txt"],
                        DependsOn: [],
                        RetryPolicy: new RetryPolicy(MaxAttempts: 2, Backoff: BackoffPolicy.None))
                ]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["worker-a"] = new WorkerBinding.Process(
                    new WorkerContract("worker-a", [], [new ProducedOutput("out.txt")], []),
                    FailOnFirstAttemptThenSucceed(markerPath, "out.txt", "content"),
                    TimeSpan.FromSeconds(30))
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-2"),
                roomDirectory,
                snapshot,
                bindings,
                artifactsRoot,
                reader,
                writer,
                dispatcher,
                timeProvider: fakeTime,
                jitterSource: () => 0.0,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(StepStatus.Succeeded, finalState.Steps.Single(s => s.StepId == StepA).Status);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var retryEvent = events.OfType<FlowEvent.StepRetryScheduled>().Single();
            Assert.Equal(0, retryEvent.RetryDelayMs);

            var accepted = events.OfType<FlowEvent.ExecutionRequestAccepted>().ToList();
            Assert.Equal(2, accepted.Count);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    // 4. Replay determinism, falsifiable (Test 4)
    [Fact]
    public void Test4_Replay_determinism_under_throwing_time_provider_and_jitter_source()
    {
        var execId1 = new ExecutionId("exec-1");
        var now = new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
        var notBefore = now.AddMilliseconds(500);

        var events = new List<FlowEvent>
        {
            new FlowEvent.ExecutionRequestAccepted(new ExecutionRequest(execId1, new WorkflowId("wf-4"), StepA, "worker-a", [], ["out.txt"], null, [], new Dictionary<StepId, ExecutionId>())),
            new FlowEvent.ExecutionFailed(execId1, FailureClassification.Retryable, "Transient error"),
            new FlowEvent.StepRetryScheduled(StepA, execId1, notBefore, 500)
        };

        var snapshot = new WorkflowDefinitionSnapshot(
            new WorkflowDefinitionSnapshotId("snapshot-retry-4"),
            new WorkflowTemplateId("template-retry-4"),
            WorkflowTemplateVersion: 1,
            Steps:
            [
                new WorkflowStepDefinition(StepA, "worker-a", [], ["out.txt"], DependsOn: [], RetryPolicy: new RetryPolicy(MaxAttempts: 2, Backoff: BackoffPolicy.Steady))
            ]);

        // StateProjector.Project is pure and does not consult any time provider or jitter source
        var state = StateProjector.Project(events, snapshot);

        var stepState = Assert.Single(state.Steps);
        Assert.Equal(StepStatus.Failed, stepState.Status);
        Assert.Equal(notBefore, stepState.RetryNotBefore);
        Assert.Equal(500, stepState.RetryDelayMs);
        Assert.Equal(execId1, stepState.RetryScheduledForExecutionId);
        Assert.Equal(WorkflowStatus.Running, state.Status);
    }

    // 7. Abandoned-crash corner (Test 7)
    [Fact]
    public async Task Test7_Abandoned_crash_recovery_execution_failed_gets_retry_scheduled()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));

        try
        {
            var execId = new ExecutionId("abandoned-exec-1");
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-retry-7"),
                new WorkflowTemplateId("template-retry-7"),
                WorkflowTemplateVersion: 1,
                Steps:
                [
                    new WorkflowStepDefinition(StepA, "worker-a", [], ["out.txt"], DependsOn: [], RetryPolicy: new RetryPolicy(MaxAttempts: 2, Backoff: BackoffPolicy.Steady))
                ]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["worker-a"] = new WorkerBinding.Process(
                    new WorkerContract("worker-a", [], [new ProducedOutput("out.txt")], []),
                    ExitWithFailureCode(),
                    TimeSpan.FromSeconds(30))
            };

            // Simulate a crash after the process spawned: an ExecutionRequestAccepted plus the
            // Core half's ExecutionStarted, with no ExecutionExited. Both live in the one
            // flow.jsonl — Core events are LogEntry-wrapped lines in the same file, so they
            // go through the writer's own CoreEvent overload, never a hand-built sidecar file.
            await using (var writerInit = new FlowEventLogWriter(logPath))
            {
                var request = new ExecutionRequest(execId, new WorkflowId("wf-7"), StepA, "worker-a", [], ["out.txt"], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId>());
                await writerInit.AppendAsync(new FlowEvent.ExecutionRequestAccepted(request), TestContext.Current.CancellationToken);
                await writerInit.AppendAsync(new CoreEvent.ExecutionStarted(execId, Pid: 12345), TestContext.Current.CancellationToken);
            }

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);

            var pumpTask = MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-7"),
                roomDirectory,
                snapshot,
                bindings,
                artifactsRoot,
                reader,
                writer,
                dispatcher,
                timeProvider: fakeTime,
                jitterSource: () => 0.0,
                cancellationToken: TestContext.Current.CancellationToken);

            // StepRetryScheduled is appended after the abandonment's ExecutionFailed, so its
            // presence proves both halves of the recovery happened.
            var retryEvent = await WaitForEventAsync<FlowEvent.StepRetryScheduled>(reader, pumpTask, TestContext.Current.CancellationToken);
            Assert.Equal(execId, retryEvent.ForExecutionId);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Contains(events, e => e is FlowEvent.ExecutionFailed f && f.ExecutionId == execId && f.Reason!.Contains("Abandoned"));

            await AdvanceUntilPumpCompletesAsync(fakeTime, pumpTask, TimeSpan.FromSeconds(10));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    // 8a. Polarity floor for append-exactly-once: at MaxAttempts = 1 MayRetry is never true, so no
    // StepRetryScheduled may appear at all — and the pump must reach terminal without deferring.
    [Fact]
    public async Task Test8_No_StepRetryScheduled_when_retry_budget_is_exhausted_on_first_failure()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));

        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-retry-8"),
                new WorkflowTemplateId("template-retry-8"),
                WorkflowTemplateVersion: 1,
                Steps:
                [
                    new WorkflowStepDefinition(StepA, "worker-a", [], ["out.txt"], DependsOn: [], RetryPolicy: new RetryPolicy(MaxAttempts: 1, Backoff: BackoffPolicy.None))
                ]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["worker-a"] = new WorkerBinding.Process(
                    new WorkerContract("worker-a", [], [new ProducedOutput("out.txt")], []),
                    ExitWithFailureCode(),
                    TimeSpan.FromSeconds(30))
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-8"),
                roomDirectory,
                snapshot,
                bindings,
                artifactsRoot,
                reader,
                writer,
                dispatcher,
                timeProvider: fakeTime,
                jitterSource: () => 0.0,
                cancellationToken: TestContext.Current.CancellationToken);

            // The await itself carries half the claim: fakeTime never advances, so if a deferral
            // were wrongly scheduled for the budget-exhausted step, the pump would never return.
            Assert.Equal(StepStatus.Failed, finalState.Steps.Single(s => s.StepId == StepA).Status);
            Assert.Equal(WorkflowStatus.Terminal, finalState.Status);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Empty(events.OfType<FlowEvent.StepRetryScheduled>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task Test8_StepRetryScheduled_appended_exactly_once_per_failed_attempt()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));

        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-retry-8b"),
                new WorkflowTemplateId("template-retry-8b"),
                WorkflowTemplateVersion: 1,
                Steps:
                [
                    new WorkflowStepDefinition(StepA, "worker-a", [], ["out.txt"], DependsOn: [], RetryPolicy: new RetryPolicy(MaxAttempts: 2, Backoff: BackoffPolicy.None))
                ]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["worker-a"] = new WorkerBinding.Process(
                    new WorkerContract("worker-a", [], [new ProducedOutput("out.txt")], []),
                    ExitWithFailureCode(), // Always fails
                    TimeSpan.FromSeconds(30))
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);

            await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-8b"),
                roomDirectory,
                snapshot,
                bindings,
                artifactsRoot,
                reader,
                writer,
                dispatcher,
                timeProvider: fakeTime,
                jitterSource: () => 0.0,
                cancellationToken: TestContext.Current.CancellationToken);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var retryEvents = events.OfType<FlowEvent.StepRetryScheduled>().ToList();

            // Attempt 1 fails -> 1 StepRetryScheduled. Attempt 2 fails -> MaxAttempts 2 reached, no more retries.
            Assert.Single(retryEvents);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    // 9. Operator RetryWithRevision is not deferred (Test 9)
    [Fact]
    public async Task Test9_Operator_RetryWithRevision_dispatches_immediately_clearing_deadline()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var markerPath = Path.Combine(roomDirectory, "attempt-marker");
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));

        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-retry-9"),
                new WorkflowTemplateId("template-retry-9"),
                WorkflowTemplateVersion: 1,
                Steps:
                [
                    new WorkflowStepDefinition(
                        StepA,
                        "worker-a",
                        Inputs: [],
                        Outputs: ["out.txt"],
                        DependsOn: [],
                        RetryPolicy: new RetryPolicy(MaxAttempts: 1, Backoff: BackoffPolicy.Patient),
                        PausePoint: new PausePoint([]))
                ]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["worker-a"] = new WorkerBinding.Process(
                    new WorkerContract("worker-a", [], [new ProducedOutput("out.txt")], []),
                    FailOnFirstAttemptThenSucceed(markerPath, "out.txt", "content"),
                    TimeSpan.FromSeconds(30))
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);

            // Attempt 1 runs and fails, pause point triggers WorkflowPaused
            var pausedState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-9"),
                roomDirectory,
                snapshot,
                bindings,
                artifactsRoot,
                reader,
                writer,
                dispatcher,
                timeProvider: fakeTime,
                jitterSource: () => 0.0,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Paused, pausedState.Status);

            var failedExecId = pausedState.Steps.Single(s => s.StepId == StepA).LatestExecutionId!.Value;

            // Operator issues RetryWithRevision
            var finalState = await MutationInterface.RecordDecisionAsync(
                new WorkflowId("wf-9"),
                roomDirectory,
                snapshot,
                bindings,
                artifactsRoot,
                reader,
                writer,
                dispatcher,
                referencedExecutionId: failedExecId,
                decisionType: DecisionType.RetryWithRevision,
                timeProvider: fakeTime,
                jitterSource: () => 0.0,
                cancellationToken: TestContext.Current.CancellationToken);

            // The await above returning is itself the claim: fakeTime never advances, so if the
            // operator's retry had been machine-deferred (Patient's initial delay is minutes), the
            // pump would still be waiting. The step lands Paused again rather than Succeeded — its
            // PausePoint pauses after every outcome, success included, same shape as Test11's
            // StepB — so the immediacy reads off the log: a second accepted execution, its
            // success, and no StepRetryScheduled ever appended for the operator-initiated attempt.
            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, events.OfType<FlowEvent.ExecutionRequestAccepted>().Count());
            Assert.Single(events.OfType<FlowEvent.ExecutionSucceeded>());
            Assert.Empty(events.OfType<FlowEvent.StepRetryScheduled>());

            var stepState = finalState.Steps.Single(s => s.StepId == StepA);
            Assert.NotEqual(failedExecId, stepState.LatestExecutionId);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    // 10. Not Terminal while deferred (Test 10)
    [Fact]
    public async Task Test10_WorkflowStatus_remains_Running_while_step_is_deferred()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));

        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-retry-10"),
                new WorkflowTemplateId("template-retry-10"),
                WorkflowTemplateVersion: 1,
                Steps:
                [
                    new WorkflowStepDefinition(StepA, "worker-a", [], ["out.txt"], DependsOn: [], RetryPolicy: new RetryPolicy(MaxAttempts: 2, Backoff: BackoffPolicy.Patient))
                ]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["worker-a"] = new WorkerBinding.Process(
                    new WorkerContract("worker-a", [], [new ProducedOutput("out.txt")], []),
                    ExitWithFailureCode(),
                    TimeSpan.FromSeconds(30))
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);

            var pumpTask = MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-10"),
                roomDirectory,
                snapshot,
                bindings,
                artifactsRoot,
                reader,
                writer,
                dispatcher,
                timeProvider: fakeTime,
                jitterSource: () => 0.0,
                cancellationToken: TestContext.Current.CancellationToken);

            var retryEvent = await WaitForEventAsync<FlowEvent.StepRetryScheduled>(reader, pumpTask, TestContext.Current.CancellationToken);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var projectedState = StateProjector.Project(events, snapshot);

            Assert.Equal(WorkflowStatus.Running, projectedState.Status);

            // Step by the recorded delay rather than a guessed 20 minutes — the event is the
            // authority on how long Patient actually deferred.
            var finalState = await AdvanceUntilPumpCompletesAsync(
                fakeTime, pumpTask, TimeSpan.FromMilliseconds(retryEvent.RetryDelayMs));
            Assert.Equal(WorkflowStatus.Terminal, finalState.Status);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    // 11. Paused sibling keeps baton decide reachable (Test 11)
    [Fact]
    public async Task Test11_Paused_sibling_keeps_baton_decide_reachable_pump_returns_paused()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));

        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-retry-11"),
                new WorkflowTemplateId("template-retry-11"),
                WorkflowTemplateVersion: 1,
                Steps:
                [
                    new WorkflowStepDefinition(StepA, "worker-a", [], ["outA.txt"], DependsOn: [], RetryPolicy: new RetryPolicy(MaxAttempts: 2, Backoff: BackoffPolicy.Patient)),
                    new WorkflowStepDefinition(StepB, "worker-b", [], ["outB.txt"], DependsOn: [], RetryPolicy: new RetryPolicy(MaxAttempts: 1), PausePoint: new PausePoint([]))
                ]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["worker-a"] = new WorkerBinding.Process(
                    new WorkerContract("worker-a", [], [new ProducedOutput("outA.txt")], []),
                    ExitWithFailureCode(),
                    TimeSpan.FromSeconds(30)),
                ["worker-b"] = new WorkerBinding.Process(
                    new WorkerContract("worker-b", [], [new ProducedOutput("outB.txt")], []),
                    WriteFile("outB.txt", "contentB"),
                    TimeSpan.FromSeconds(30))
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);

            // StepA fails and defers; StepB succeeds and pauses.
            // Pump should return WorkflowStatus.Paused immediately without blocking on StepA's deferral wait.
            var state = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-11"),
                roomDirectory,
                snapshot,
                bindings,
                artifactsRoot,
                reader,
                writer,
                dispatcher,
                timeProvider: fakeTime,
                jitterSource: () => 0.0,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Paused, state.Status);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    // 13. An expired deferral whose step is blocked on a terminally failed dependency is a fixed
    // point. Before the future-deadline filter in the idle wait, this state was a zero-delay spin:
    // nothing ready (the dependency is not Succeeded), nothing in flight, and a deadline in the
    // past producing delay <= 0 -> continue -> re-project -> repeat, forever, at full CPU.
    [Fact]
    public async Task Test13_Expired_deferral_blocked_on_failed_dependency_is_a_fixed_point_not_a_spin()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));

        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-retry-13"),
                new WorkflowTemplateId("template-retry-13"),
                WorkflowTemplateVersion: 1,
                Steps:
                [
                    new WorkflowStepDefinition(StepA, "worker-a", [], ["outA.txt"], DependsOn: [], RetryPolicy: new RetryPolicy(MaxAttempts: 1)),
                    new WorkflowStepDefinition(StepB, "worker-b", [], ["outB.txt"], DependsOn: [StepA], RetryPolicy: new RetryPolicy(MaxAttempts: 2, Backoff: BackoffPolicy.Steady))
                ]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["worker-a"] = new WorkerBinding.Process(
                    new WorkerContract("worker-a", [], [new ProducedOutput("outA.txt")], []),
                    ExitWithFailureCode(),
                    TimeSpan.FromSeconds(30)),
                ["worker-b"] = new WorkerBinding.Process(
                    new WorkerContract("worker-b", [], [new ProducedOutput("outB.txt")], []),
                    ExitWithFailureCode(),
                    TimeSpan.FromSeconds(30))
            };

            // The stranded shape, written as history: A succeeded, B failed and was deferred, then
            // A reran (a supersede consequence) and failed permanently -- all before this pump
            // starts, with B's deadline already in the past.
            var aFirst = new ExecutionId("a-1");
            var bAttempt = new ExecutionId("b-1");
            var aRerun = new ExecutionId("a-2");
            await using (var writerInit = new FlowEventLogWriter(logPath))
            {
                var ct = TestContext.Current.CancellationToken;
                await writerInit.AppendAsync(new FlowEvent.ExecutionRequestAccepted(
                    new ExecutionRequest(aFirst, new WorkflowId("wf-13"), StepA, "worker-a", [], ["outA.txt"], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId>())), ct);
                await writerInit.AppendAsync(new FlowEvent.ExecutionSucceeded(aFirst), ct);
                await writerInit.AppendAsync(new FlowEvent.ExecutionRequestAccepted(
                    new ExecutionRequest(bAttempt, new WorkflowId("wf-13"), StepB, "worker-b", [], ["outB.txt"], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId> { [StepA] = aFirst })), ct);
                await writerInit.AppendAsync(new FlowEvent.ExecutionFailed(bAttempt, FailureClassification.Retryable, "boom"), ct);
                await writerInit.AppendAsync(new FlowEvent.StepRetryScheduled(
                    StepB, bAttempt, fakeTime.GetUtcNow().AddSeconds(-10), 500), ct);
                await writerInit.AppendAsync(new FlowEvent.ExecutionRequestAccepted(
                    new ExecutionRequest(aRerun, new WorkflowId("wf-13"), StepA, "worker-a", [], ["outA.txt"], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId>())), ct);
                await writerInit.AppendAsync(new FlowEvent.ExecutionFailed(aRerun, FailureClassification.Permanent, "dead"), ct);
            }

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);

            // Nothing ever advances fakeTime: the pump must return on its own, promptly.
            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-13"),
                roomDirectory,
                snapshot,
                bindings,
                artifactsRoot,
                reader,
                writer,
                dispatcher,
                timeProvider: fakeTime,
                jitterSource: () => 0.0,
                cancellationToken: TestContext.Current.CancellationToken)
                .WaitAsync(PumpCompletionTimeout, TestContext.Current.CancellationToken);

            Assert.Equal(StepStatus.Failed, finalState.Steps.Single(s => s.StepId == StepA).Status);
            Assert.Equal(StepStatus.Failed, finalState.Steps.Single(s => s.StepId == StepB).Status);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    // 12. Host stop during a deferral wait (Test 12)
    [Fact]
    public async Task Test12_Host_stop_during_deferral_wait_returns_promptly()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));
        using var cts = new CancellationTokenSource();

        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-retry-12"),
                new WorkflowTemplateId("template-retry-12"),
                WorkflowTemplateVersion: 1,
                Steps:
                [
                    new WorkflowStepDefinition(StepA, "worker-a", [], ["out.txt"], DependsOn: [], RetryPolicy: new RetryPolicy(MaxAttempts: 2, Backoff: BackoffPolicy.Patient))
                ]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["worker-a"] = new WorkerBinding.Process(
                    new WorkerContract("worker-a", [], [new ProducedOutput("out.txt")], []),
                    ExitWithFailureCode(),
                    TimeSpan.FromSeconds(30))
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);

            var pumpTask = MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-12"),
                roomDirectory,
                snapshot,
                bindings,
                artifactsRoot,
                reader,
                writer,
                dispatcher,
                timeProvider: fakeTime,
                jitterSource: () => 0.0,
                cancellationToken: cts.Token);

            // Wait until the deferral is committed, so the stop provably lands during (or after
            // entering) the wait rather than before the first attempt even ran.
            await WaitForEventAsync<FlowEvent.StepRetryScheduled>(reader, pumpTask, TestContext.Current.CancellationToken);

            // Signal host stop while pump is waiting on the Patient deferral
            cts.Cancel();

            var finalState = await pumpTask.WaitAsync(PumpCompletionTimeout, TestContext.Current.CancellationToken);
            Assert.NotNull(finalState);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task ExhaustedUntil_failure_RetryNotBefore_equals_reset_moment_while_ordinary_retryable_follows_backoff()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var now = new DateTimeOffset(2026, 7, 30, 15, 0, 0, TimeSpan.Zero);
        var fakeTime = new FakeTimeProvider(now);
        var resetMoment = now.AddMinutes(45);

        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);

            var execIdExhausted = new ExecutionId("exec-exhausted");
            var execIdRetryable = new ExecutionId("exec-retryable");

            // Append ExhaustedUntil failure with explicit reset moment
            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(
                new ExecutionRequest(execIdExhausted, new WorkflowId("wf-ex"), StepA, "worker-a", [], [], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId>())), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new FlowEvent.ExecutionFailed(execIdExhausted, FailureClassification.ExhaustedUntil, "quota exhausted", resetMoment), TestContext.Current.CancellationToken);

            var snapshotA = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snap-1"),
                new WorkflowTemplateId("tpl-1"),
                1,
                [new WorkflowStepDefinition(StepA, "worker-a", [], [], [], RetryPolicy: new RetryPolicy(MaxAttempts: 3, Backoff: BackoffPolicy.Steady))]);

            var eventsExhausted = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var stateExhausted = StateProjector.Project(eventsExhausted, snapshotA);

            var getObligationsMethod = typeof(MutationInterface).GetMethod("GetRetryObligations", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            var obligationsExhausted = (IEnumerable<object>)getObligationsMethod.Invoke(null, [stateExhausted, snapshotA, fakeTime, (Func<double>)(() => 0.0), false, null, new Dictionary<ExecutionId, ExecutionRequest>()])!;
            var exhaustedObligation = obligationsExhausted.Single();

            var notBeforeProperty = exhaustedObligation.GetType().GetProperty("RetryNotBefore")!;
            var exhaustedNotBefore = (DateTimeOffset)notBeforeProperty.GetValue(exhaustedObligation)!;

            Assert.Equal(resetMoment, exhaustedNotBefore);

            // Append ordinary Retryable failure (no reset moment)
            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(
                new ExecutionRequest(execIdRetryable, new WorkflowId("wf-retry"), StepB, "worker-b", [], [], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId>())), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new FlowEvent.ExecutionFailed(execIdRetryable, FailureClassification.Retryable, "ordinary failure"), TestContext.Current.CancellationToken);

            var snapshotB = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snap-2"),
                new WorkflowTemplateId("tpl-2"),
                1,
                [new WorkflowStepDefinition(StepB, "worker-b", [], [], [], RetryPolicy: new RetryPolicy(MaxAttempts: 3, Backoff: BackoffPolicy.Steady))]);

            var eventsRetryable = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var stateRetryable = StateProjector.Project(eventsRetryable, snapshotB);

            var obligationsRetryable = (IEnumerable<object>)getObligationsMethod.Invoke(null, [stateRetryable, snapshotB, fakeTime, (Func<double>)(() => 0.0), false, null, new Dictionary<ExecutionId, ExecutionRequest>()])!;
            var retryableObligation = obligationsRetryable.Single();
            var retryableNotBefore = (DateTimeOffset)notBeforeProperty.GetValue(retryableObligation)!;

            // Steady backoff with 0.0 jitter sample is 500ms delay
            Assert.Equal(now.AddMilliseconds(500), retryableNotBefore);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    // #1115 review must-fix: claude's typed credits_required match honestly carries NO reset
    // instant (0026 §5) — and that combination fell through to ordinary backoff with
    // ConsecutiveFailureCount frozen at 0, i.e. a ~1s fabricated-instant retry loop against a
    // known-dead quota, forever. An unknown-instant exhaustion gets NO obligation: nothing
    // wakes up; a person resumes it. The non-null arm above is this test's polarity.
    [Fact]
    public async Task ExhaustedUntil_with_unknown_reset_instant_schedules_no_obligation_at_all()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var now = new DateTimeOffset(2026, 8, 12, 15, 0, 0, TimeSpan.Zero);
        var fakeTime = new FakeTimeProvider(now);

        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);

            var execId = new ExecutionId("exec-exhausted-unknown");
            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(
                new ExecutionRequest(execId, new WorkflowId("wf-exu"), StepA, "worker-a", [], [], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId>())), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new FlowEvent.ExecutionFailed(execId, FailureClassification.ExhaustedUntil, "quota exhausted", RetryNotBefore: null), TestContext.Current.CancellationToken);

            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snap-exu"),
                new WorkflowTemplateId("tpl-exu"),
                1,
                [new WorkflowStepDefinition(StepA, "worker-a", [], [], [], RetryPolicy: new RetryPolicy(MaxAttempts: 3, Backoff: BackoffPolicy.Steady))]);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var state = StateProjector.Project(events, snapshot);

            var getObligationsMethod = typeof(MutationInterface).GetMethod("GetRetryObligations", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            var obligations = (IEnumerable<object>)getObligationsMethod.Invoke(null, [state, snapshot, fakeTime, (Func<double>)(() => 0.0), false, null, new Dictionary<ExecutionId, ExecutionRequest>()])!;

            Assert.Empty(obligations);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    // #1184 / 0026 §4 discriminator pair:
    // When settleOnVendorExhaustion is false (unattended workflow step), an ExhaustedUntil step carrying a
    // KNOWN future reset instant DOES get the paced obligation (unchanged).
    // When settleOnVendorExhaustion is true (attended interactive session turn), the exact same step gets NO obligation.
    [Fact]
    public async Task ExhaustedUntil_with_known_reset_instant_gets_no_obligation_when_settleOnVendorExhaustion_is_true_and_gets_obligation_when_false()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var now = new DateTimeOffset(2026, 8, 13, 15, 0, 0, TimeSpan.Zero);
        var fakeTime = new FakeTimeProvider(now);
        var resetMoment = now.AddHours(2);

        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);

            var execId = new ExecutionId("exec-exhausted-known");
            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(
                new ExecutionRequest(execId, new WorkflowId("wf-ex-known"), StepA, "worker-a", [], [], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId>())), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new FlowEvent.ExecutionFailed(execId, FailureClassification.ExhaustedUntil, "quota exhausted", resetMoment), TestContext.Current.CancellationToken);

            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snap-ex-known"),
                new WorkflowTemplateId("tpl-ex-known"),
                1,
                [new WorkflowStepDefinition(StepA, "worker-a", [], [], [], RetryPolicy: new RetryPolicy(MaxAttempts: 3, Backoff: BackoffPolicy.Steady))]);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var state = StateProjector.Project(events, snapshot);

            var getObligationsMethod = typeof(MutationInterface).GetMethod("GetRetryObligations", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

            // When settleOnVendorExhaustion = false (unattended): DOES get paced obligation
            var obligationsUnattended = (IEnumerable<object>)getObligationsMethod.Invoke(null, [state, snapshot, fakeTime, (Func<double>)(() => 0.0), false, null, new Dictionary<ExecutionId, ExecutionRequest>()])!;
            var obligation = obligationsUnattended.Single();
            var notBeforeProperty = obligation.GetType().GetProperty("RetryNotBefore")!;
            Assert.Equal(resetMoment, (DateTimeOffset)notBeforeProperty.GetValue(obligation)!);

            // When settleOnVendorExhaustion = true (attended): gets NO obligation (settles immediately)
            var obligationsAttended = (IEnumerable<object>)getObligationsMethod.Invoke(null, [state, snapshot, fakeTime, (Func<double>)(() => 0.0), true, null, new Dictionary<ExecutionId, ExecutionRequest>()])!;
            Assert.Empty(obligationsAttended);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    // #815: the Test9 scenario (operator RetryWithRevision dispatches immediately, clearing the
    // deadline), but for a step #594's classification quota-parked and never paused — the lane-
    // workflow shape #815 measured live, where the step declares no PausePoint at all. Starts from
    // a fabricated Failed + StepRetryScheduled(far-future) history, exactly like Test13's manual
    // event log, so the far-future deadline proves the operator's decision — not fakeTime, which
    // never advances — is what releases the step.
    [Fact]
    public async Task Test815_Operator_RetryWithRevision_against_a_quota_parked_never_paused_step_dispatches_immediately()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var now = new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);
        var fakeTime = new FakeTimeProvider(now);
        var farFutureResetMoment = now.AddHours(2);

        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-retry-815"),
                new WorkflowTemplateId("template-retry-815"),
                WorkflowTemplateVersion: 1,
                Steps:
                [
                    new WorkflowStepDefinition(
                        StepA,
                        "worker-a",
                        Inputs: [],
                        Outputs: [],
                        DependsOn: [],
                        RetryPolicy: new RetryPolicy(MaxAttempts: 2, Backoff: BackoffPolicy.Steady))
                    // No PausePoint: the exact lane-workflow shape #815 measured live — nothing
                    // could ever have paused this step.
                ]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["worker-a"] = new WorkerBinding.Process(
                    new WorkerContract("worker-a", [], [], []),
                    ExitCleanlyWithoutWriting(),
                    TimeSpan.FromSeconds(30))
            };

            // The quota-parked history, written directly (Test13's pattern): a first attempt
            // failed with an ExhaustedUntil-shaped classification and a scheduled retry hours in
            // the future -- no WorkflowPaused, because this step has no PausePoint to trigger one.
            var firstAttempt = new ExecutionId("a-1");
            await using (var writerInit = new FlowEventLogWriter(logPath))
            {
                var ct = TestContext.Current.CancellationToken;
                await writerInit.AppendAsync(new FlowEvent.ExecutionRequestAccepted(
                    new ExecutionRequest(firstAttempt, new WorkflowId("wf-815"), StepA, "worker-a", [], [], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId>())), ct);
                await writerInit.AppendAsync(new FlowEvent.ExecutionFailed(
                    firstAttempt, FailureClassification.ExhaustedUntil, "quota exhausted", farFutureResetMoment), ct);
                await writerInit.AppendAsync(new FlowEvent.StepRetryScheduled(
                    StepA, firstAttempt, farFutureResetMoment, RetryDelayMs: (int)TimeSpan.FromHours(2).TotalMilliseconds), ct);
            }

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);

            // Operator issues RetryWithRevision against the parked (never-paused) execution.
            // Nothing ever advances fakeTime: if the decision had been machine-deferred back to
            // farFutureResetMoment instead of dispatching now, this would hang until the
            // WaitAsync timeout.
            var finalState = await MutationInterface.RecordDecisionAsync(
                new WorkflowId("wf-815"),
                roomDirectory,
                snapshot,
                bindings,
                artifactsRoot,
                reader,
                writer,
                dispatcher,
                referencedExecutionId: firstAttempt,
                decisionType: DecisionType.RetryWithRevision,
                timeProvider: fakeTime,
                jitterSource: () => 0.0,
                cancellationToken: TestContext.Current.CancellationToken)
                .WaitAsync(PumpCompletionTimeout, TestContext.Current.CancellationToken);

            var stepState = finalState.Steps.Single(s => s.StepId == StepA);
            Assert.Equal(StepStatus.Succeeded, stepState.Status);
            Assert.NotEqual(firstAttempt, stepState.LatestExecutionId);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, events.OfType<FlowEvent.ExecutionRequestAccepted>().Count());
            Assert.Single(events.OfType<FlowEvent.ExecutionSucceeded>());
            // Only the fabricated first attempt's deferral -- the operator-triggered attempt was
            // never machine-paced, matching Test9's "no StepRetryScheduled for the operator
            // attempt" assertion.
            Assert.Single(events.OfType<FlowEvent.StepRetryScheduled>());
            Assert.Empty(events.OfType<FlowEvent.WorkflowPaused>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    // #1094: shared seeding for the two polarity tests below — a fabricated parked history (the #815
    // test's pattern) whose classification the caller picks. fakeTime starts before the reset so the
    // step is not yet ready; each test then chooses how to release the pump deterministically.
    // #1183: resetOffset defaults to the original 2-hour gap; RetryDelayMs is always derived from it
    // (never a fixed 2-hour literal) so a caller-supplied longer offset stays consistent with
    // DependencyResolver's #712 clamp instead of tripping it.
    private static async Task<(string Room, string Artifacts, string Log, WorkflowDefinitionSnapshot Snapshot,
        Dictionary<string, WorkerBinding> Bindings, FakeTimeProvider FakeTime, DateTimeOffset Reset)>
        SeedParkedStepAsync(FailureClassification classification, TimeSpan? resetOffset = null)
    {
        var room = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var log = Path.Combine(room, "flow.jsonl");
        var now = new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);
        var offset = resetOffset ?? TimeSpan.FromHours(2);
        var reset = now + offset;
        Directory.CreateDirectory(room);

        var snapshot = new WorkflowDefinitionSnapshot(
            new WorkflowDefinitionSnapshotId("snap-1094"),
            new WorkflowTemplateId("tmpl-1094"),
            WorkflowTemplateVersion: 1,
            Steps: [new WorkflowStepDefinition(StepA, "worker-a", [], [], DependsOn: [], RetryPolicy: new RetryPolicy(MaxAttempts: 2, Backoff: BackoffPolicy.Steady))]);

        var bindings = new Dictionary<string, WorkerBinding>
        {
            ["worker-a"] = new WorkerBinding.Process(
                new WorkerContract("worker-a", [], [], []), ExitCleanlyWithoutWriting(), TimeSpan.FromSeconds(30))
        };

        var firstAttempt = new ExecutionId("a-1");
        var ct = TestContext.Current.CancellationToken;
        await using var seed = new FlowEventLogWriter(log);
        await seed.AppendAsync(new FlowEvent.ExecutionRequestAccepted(new ExecutionRequest(
            firstAttempt, new WorkflowId("wf-1094"), StepA, "worker-a", [], [], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId>())), ct);
        await seed.AppendAsync(new FlowEvent.ExecutionFailed(firstAttempt, classification, "seeded park", reset), ct);
        await seed.AppendAsync(new FlowEvent.StepRetryScheduled(
            StepA, firstAttempt, reset, RetryDelayMs: (int)offset.TotalMilliseconds), ct);

        return (room, Path.Combine(room, "artifacts"), log, snapshot, bindings, new FakeTimeProvider(now), reset);
    }

    [Fact]
    public async Task A_vendor_quota_park_surfaces_the_reset_instant_to_the_foreground()
    {
        var s = await SeedParkedStepAsync(FailureClassification.ExhaustedUntil);
        try
        {
            await using var writer = new FlowEventLogWriter(s.Log);
            var ct = TestContext.Current.CancellationToken;
            DateTimeOffset? captured = null;
            var noticed = new TaskCompletionSource();
            using var cts = new CancellationTokenSource();

            // fakeTime is never advanced, so the pump enters and stays in the deferral wait: the notice
            // fires deterministically (no wall-clock grace), then the host stop releases the pump.
            var pump = MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-1094"), s.Room, s.Snapshot, s.Bindings, s.Artifacts,
                new FlowEventLogReader(s.Log), writer, new CoreDispatcher(writer, writer),
                timeProvider: s.FakeTime, jitterSource: () => 0.0, cancellationToken: cts.Token,
                onVendorQuotaPark: instant => { captured = instant; noticed.TrySetResult(); });

            await noticed.Task.WaitAsync(PumpCompletionTimeout, ct);
            await cts.CancelAsync();
            await pump.WaitAsync(PumpCompletionTimeout, ct);

            Assert.Equal(s.Reset, captured);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(s.Room);
        }
    }

    [Fact]
    public async Task An_ordinary_retry_backoff_does_not_surface_a_vendor_quota_notice()
    {
        var s = await SeedParkedStepAsync(FailureClassification.Retryable);
        try
        {
            await using var writer = new FlowEventLogWriter(s.Log);
            DateTimeOffset? captured = null;

            // Advancing the clock past the reset drives the parked step's retry to a terminal state —
            // deterministic, no fixed grace. Whether or not the pump paused in the backoff wait first,
            // an ordinary Retryable park must never fire the vendor-quota notice.
            var pump = MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-1094"), s.Room, s.Snapshot, s.Bindings, s.Artifacts,
                new FlowEventLogReader(s.Log), writer, new CoreDispatcher(writer, writer),
                timeProvider: s.FakeTime, jitterSource: () => 0.0,
                cancellationToken: TestContext.Current.CancellationToken,
                onVendorQuotaPark: instant => captured = instant);

            await AdvanceUntilPumpCompletesAsync(s.FakeTime, pump, TimeSpan.FromMinutes(30));

            Assert.Null(captured);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(s.Room);
        }
    }

    // #1183: red-proven against the pre-fix code -- with GetRetryObligations trusting an absurd
    // reset instant wholesale, DependencyResolver's #712 backwards-clock-jump clamp (`remaining >
    // maxDelay`) misfires on the saturated RetryDelayMs and marks the step ready, so this same round
    // redispatches it. The busy-wait section reached immediately afterwards still reads the PRE-
    // reprojection `state.Steps` snapshot -- which still carries the far-future RetryNotBefore for the
    // step just redispatched -- into `pendingRetryDeadlines`, and calls
    // `Task.Delay(wakeDelay, timeProvider, ioCancellationToken)` directly on a ~2-year `wakeDelay`,
    // which throws `ArgumentOutOfRangeException` synchronously (confirmed empirically against .NET
    // 10's TimeProvider overload, which enforces the same ~49.7-day ceiling as the plain TimeSpan
    // overload). Fixed: GetRetryObligations caps the obligation to MaxExhaustionParkHorizon, keeping
    // RetryNotBefore/RetryDelayMs consistent so the clamp never misfires, and both Task.Delay sites
    // additionally clamp to MaxParkWaitChunk regardless.
    [Fact]
    public async Task Test1183_Far_future_ExhaustedUntil_reset_instant_does_not_crash_the_pump()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var now = new DateTimeOffset(2026, 8, 13, 15, 0, 0, TimeSpan.Zero);
        var fakeTime = new FakeTimeProvider(now);
        var farFutureResetMoment = now.AddYears(2);
        using var cts = new CancellationTokenSource();

        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-1183-far"),
                new WorkflowTemplateId("template-1183-far"),
                WorkflowTemplateVersion: 1,
                Steps:
                [
                    new WorkflowStepDefinition(StepA, "worker-a", [], [], DependsOn: [], RetryPolicy: new RetryPolicy(MaxAttempts: 2, Backoff: BackoffPolicy.Steady))
                ]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["worker-a"] = new WorkerBinding.Process(
                    new WorkerContract("worker-a", [], [], []), ExitCleanlyWithoutWriting(), TimeSpan.FromSeconds(30))
            };

            var firstAttempt = new ExecutionId("a-1");
            await using (var writerInit = new FlowEventLogWriter(logPath))
            {
                var ct = TestContext.Current.CancellationToken;
                await writerInit.AppendAsync(new FlowEvent.ExecutionRequestAccepted(
                    new ExecutionRequest(firstAttempt, new WorkflowId("wf-1183-far"), StepA, "worker-a", [], [], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId>())), ct);
                await writerInit.AppendAsync(new FlowEvent.ExecutionFailed(
                    firstAttempt, FailureClassification.ExhaustedUntil, "quota exhausted", farFutureResetMoment), ct);
                // No StepRetryScheduled seeded: the live pump schedules its own obligation on the
                // first round, exercising GetRetryObligations against the far-future instant for real.
            }

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);

            var pumpTask = MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-1183-far"),
                roomDirectory,
                snapshot,
                bindings,
                artifactsRoot,
                reader,
                writer,
                dispatcher,
                timeProvider: fakeTime,
                jitterSource: () => 0.0,
                cancellationToken: cts.Token);

            // fakeTime never advances. Give the pump a real-time window to run every synchronous
            // round it can reach on its own (scheduling the obligation, resolving readiness, entering
            // whichever wait branch) -- pre-fix this window is enough for the crash above to surface
            // as a faulted task; post-fix the pump is legitimately still parked, waiting.
            var settleDeadline = DateTimeOffset.UtcNow.AddSeconds(2);
            while (!pumpTask.IsCompleted && DateTimeOffset.UtcNow < settleDeadline)
            {
                await Task.Delay(20, TestContext.Current.CancellationToken); // wait-ok: poll interval inside a bounded settle loop, not an expected-duration wait
            }

            Assert.False(pumpTask.IsFaulted, pumpTask.IsFaulted ? pumpTask.Exception!.ToString() : "");
            Assert.False(pumpTask.IsCompleted);

            // Positive anchor, not just "didn't crash": the settle window above is long enough for a
            // loaded machine to pass vacuously (pump never even reaching GetRetryObligations) unless
            // something confirms the obligation was actually scheduled and actually capped.
            var eventsSoFar = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var scheduled = eventsSoFar.OfType<FlowEvent.StepRetryScheduled>().SingleOrDefault(e => e.StepId == StepA);
            Assert.NotNull(scheduled);
            Assert.Equal(now + TimeSpan.FromDays(14), scheduled.RetryNotBefore);

            // Host stop releases the still-parked pump cleanly, proving it never threw internally
            // and stayed a well-behaved wait the whole time.
            await cts.CancelAsync();
            var finalState = await pumpTask.WaitAsync(PumpCompletionTimeout, TestContext.Current.CancellationToken);
            Assert.NotNull(finalState);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    // #1183: GetRetryObligations must not trust a vendor-reported reset instant more than
    // MaxExhaustionParkHorizon out -- polarity control is the pre-existing
    // ExhaustedUntil_failure_RetryNotBefore_equals_reset_moment... test above, which asserts an
    // ordinary 45-minute-out reset is carried through UNCHANGED (left unmodified by this issue).
    [Fact]
    public async Task Test1183_Far_future_reset_instant_is_capped_to_the_horizon_not_trusted_wholesale()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var now = new DateTimeOffset(2026, 8, 13, 15, 0, 0, TimeSpan.Zero);
        var fakeTime = new FakeTimeProvider(now);
        var farFutureResetMoment = now.AddYears(2);

        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);

            var execId = new ExecutionId("exec-far-future");
            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(
                new ExecutionRequest(execId, new WorkflowId("wf-cap"), StepA, "worker-a", [], [], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId>())), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new FlowEvent.ExecutionFailed(execId, FailureClassification.ExhaustedUntil, "quota exhausted", farFutureResetMoment), TestContext.Current.CancellationToken);

            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snap-cap"),
                new WorkflowTemplateId("tpl-cap"),
                1,
                [new WorkflowStepDefinition(StepA, "worker-a", [], [], [], RetryPolicy: new RetryPolicy(MaxAttempts: 2, Backoff: BackoffPolicy.Steady))]);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var state = StateProjector.Project(events, snapshot);

            var getObligationsMethod = typeof(MutationInterface).GetMethod("GetRetryObligations", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            var obligations = (IEnumerable<object>)getObligationsMethod.Invoke(null, [state, snapshot, fakeTime, (Func<double>)(() => 0.0), false, null, new Dictionary<ExecutionId, ExecutionRequest>()])!;
            var obligation = obligations.Single();

            var notBefore = (DateTimeOffset)obligation.GetType().GetProperty("RetryNotBefore")!.GetValue(obligation)!;
            var delayMs = (int)obligation.GetType().GetProperty("RetryDelayMs")!.GetValue(obligation)!;

            // Capped to the sane horizon, not the raw 2-year vendor value.
            Assert.Equal(now.Add(TimeSpan.FromDays(14)), notBefore);
            Assert.True(delayMs is > 0 and < int.MaxValue, $"Expected a sane positive delayMs, got {delayMs}");
            // notBefore and delayMs stay mutually consistent -- DependencyResolver's #712 clamp
            // (`remaining > maxDelay`) must not be fooled into marking this step ready early.
            Assert.Equal(delayMs, (int)Math.Round((notBefore - now).TotalMilliseconds));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    // #1183: an ExhaustedUntil reset instant already at or before now must not collapse to a
    // zero-delay retry -- see PastResetInstantRetryFloor's own doc comment (MutationInterface.cs)
    // for why that machine-guns. Polarity: the future-but-imminent case below the floor still needs
    // pacing too (this branch does not distinguish "already past" from "about to hit").
    [Fact]
    public async Task Test1183_Past_reset_instant_is_paced_to_a_floor_not_an_immediate_retry()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var now = new DateTimeOffset(2026, 8, 13, 15, 0, 0, TimeSpan.Zero);
        var fakeTime = new FakeTimeProvider(now);
        var staleResetMoment = now.AddMinutes(-5);

        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);

            var execId = new ExecutionId("exec-stale");
            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(
                new ExecutionRequest(execId, new WorkflowId("wf-stale"), StepA, "worker-a", [], [], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId>())), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new FlowEvent.ExecutionFailed(execId, FailureClassification.ExhaustedUntil, "quota exhausted", staleResetMoment), TestContext.Current.CancellationToken);

            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snap-stale"),
                new WorkflowTemplateId("tpl-stale"),
                1,
                [new WorkflowStepDefinition(StepA, "worker-a", [], [], [], RetryPolicy: new RetryPolicy(MaxAttempts: 2, Backoff: BackoffPolicy.Steady))]);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var state = StateProjector.Project(events, snapshot);

            var getObligationsMethod = typeof(MutationInterface).GetMethod("GetRetryObligations", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            var obligations = (IEnumerable<object>)getObligationsMethod.Invoke(null, [state, snapshot, fakeTime, (Func<double>)(() => 0.0), false, null, new Dictionary<ExecutionId, ExecutionRequest>()])!;
            var obligation = obligations.Single();

            var notBefore = (DateTimeOffset)obligation.GetType().GetProperty("RetryNotBefore")!.GetValue(obligation)!;
            var delayMs = (int)obligation.GetType().GetProperty("RetryDelayMs")!.GetValue(obligation)!;

            Assert.True(notBefore > now, $"Expected a floor-paced notBefore strictly after now, got {notBefore} (now={now})");
            Assert.True(delayMs >= 1000, $"Expected a floor of at least 1000ms, got {delayMs}");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    // #1183 / #1094: the park notice must still fire exactly once per distinct (capped) instant even
    // though the wait to reach it is now chunked into several Task.Delay calls -- a chunk boundary
    // completing is not a new park and must not read as one.
    [Fact]
    public async Task Test1183_Vendor_quota_park_notifies_once_across_multiple_chunked_wait_boundaries()
    {
        var s = await SeedParkedStepAsync(FailureClassification.ExhaustedUntil, resetOffset: TimeSpan.FromDays(3));
        try
        {
            await using var writer = new FlowEventLogWriter(s.Log);
            var ct = TestContext.Current.CancellationToken;
            var notifications = new List<DateTimeOffset>();
            var firstNoticed = new TaskCompletionSource();
            using var cts = new CancellationTokenSource();

            var pump = MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-1094"), s.Room, s.Snapshot, s.Bindings, s.Artifacts,
                new FlowEventLogReader(s.Log), writer, new CoreDispatcher(writer, writer),
                timeProvider: s.FakeTime, jitterSource: () => 0.0, cancellationToken: cts.Token,
                onVendorQuotaPark: instant =>
                {
                    notifications.Add(instant);
                    firstNoticed.TrySetResult();
                });

            await firstNoticed.Task.WaitAsync(PumpCompletionTimeout, ct);

            // MaxParkWaitChunk is 1 day; the 3-day reset needs multiple internal Task.Delay chunks to
            // reach. Advance across two chunk boundaries while still short of the reset itself, so any
            // chunk-boundary wakeup that wrongly re-notified would already have shown up here.
            s.FakeTime.Advance(TimeSpan.FromDays(1));
            await Task.Delay(50, ct); // wait-ok: settle time for an in-process async continuation, not an external wait
            s.FakeTime.Advance(TimeSpan.FromDays(1));
            await Task.Delay(50, ct); // wait-ok: settle time for an in-process async continuation, not an external wait

            Assert.Single(notifications);
            Assert.Equal(s.Reset, notifications[0]);

            await cts.CancelAsync();
            await pump.WaitAsync(PumpCompletionTimeout, ct);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(s.Room);
        }
    }

    // #1183 MED-1(a): the dedupe key is the RAW vendor instant, not the paced one -- two genuinely
    // distinct vendor refusals must each surface their own notice, not collapse into one. Red
    // against a dedupe keyed on `minNotBefore` (the pre-commit-2 shape): both notices still carry a
    // distinct paced value here too, so that mutation alone would not have caught this; what it
    // catches is a *broader* regression -- any future dedupe that keys off anything other than
    // `LatestExecutionFailedRetryNotBefore` (e.g. reverting to the `?? minNotBefore` fallback path,
    // or a per-step dedupe that forgets to reset between distinct instants).
    [Fact]
    public async Task Test1183_Distinct_raw_vendor_instants_each_notify_once()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var now = new DateTimeOffset(2026, 8, 13, 15, 0, 0, TimeSpan.Zero);
        var fakeTime = new FakeTimeProvider(now);
        var reset1 = now + TimeSpan.FromHours(2);
        var reset2 = reset1 + TimeSpan.FromHours(3);

        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snap-distinct"),
                new WorkflowTemplateId("tmpl-distinct"),
                1,
                [new WorkflowStepDefinition(StepA, "worker-a", [], [], [], RetryPolicy: new RetryPolicy(MaxAttempts: 3, Backoff: BackoffPolicy.Steady))]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["worker-a"] = new WorkerBinding.Process(
                    new WorkerContract("worker-a", [], [], []), ExitCleanlyWithoutWriting(), TimeSpan.FromSeconds(30))
            };

            var execA = new ExecutionId("exec-distinct-a");
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var ct = TestContext.Current.CancellationToken;

            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(
                new ExecutionRequest(execA, new WorkflowId("wf-distinct"), StepA, "worker-a", [], [], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId>())), ct);
            await writer.AppendAsync(new FlowEvent.ExecutionFailed(execA, FailureClassification.ExhaustedUntil, "quota exhausted", reset1), ct);

            var notifications = new List<DateTimeOffset>();
            var firstNoticed = new TaskCompletionSource();
            using var cts = new CancellationTokenSource();

            var pump = MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-distinct"), roomDirectory, snapshot, bindings, Path.Combine(roomDirectory, "artifacts"),
                reader, writer, new CoreDispatcher(writer, writer),
                timeProvider: fakeTime, jitterSource: () => 0.0, cancellationToken: cts.Token,
                onVendorQuotaPark: instant =>
                {
                    lock (notifications) { notifications.Add(instant); }
                    firstNoticed.TrySetResult();
                });

            await firstNoticed.Task.WaitAsync(PumpCompletionTimeout, ct);
            Assert.Equal([reset1], notifications);

            // A second, DISTINCT raw instant for a fresh execution, injected on the SAME writer
            // before the first obligation's wait completes -- the pump picks it up as a brand-new
            // obligation the moment it wakes from that wait, never actually redispatching execA's
            // retry for real (the step is never "ready": by the time the clock reaches reset1, execB
            // is already the latest failure, with its own future RetryNotBefore).
            var execB = new ExecutionId("exec-distinct-b");
            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(
                new ExecutionRequest(execB, new WorkflowId("wf-distinct"), StepA, "worker-a", [], [], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId>())), ct);
            await writer.AppendAsync(new FlowEvent.ExecutionFailed(execB, FailureClassification.ExhaustedUntil, "quota exhausted", reset2), ct);

            fakeTime.Advance(TimeSpan.FromHours(2)); // wakes the pump at reset1

            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < PumpCompletionTimeout)
            {
                if (notifications.Count >= 2)
                {
                    break;
                }

                if (pump.IsCompleted)
                {
                    await pump;
                    Assert.Fail("Pump completed without a second, distinct-instant notification.");
                }

                await Task.Delay(10, ct); // wait-ok: poll interval inside a bounded settle loop, not an expected-duration wait
            }

            Assert.Equal([reset1, reset2], notifications);

            await cts.CancelAsync();
            await pump.WaitAsync(PumpCompletionTimeout, ct);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    // #1183 MED-1(b): the polarity twin of the test above -- a SECOND retry that reports the exact
    // same raw stale instant as the first must NOT re-notify, even though PastResetInstantRetryFloor
    // gives each of those two retries its own, different PACED notBefore (a fresh now+1s apiece).
    // Red against a dedupe keyed on the paced value (`minNotBefore`, the pre-commit-2 shape): that
    // key differs across the two retries here even though the raw instant does not, so it would
    // re-notify once per retry forever.
    [Fact]
    public async Task Test1183_Repeating_stale_raw_instant_across_two_retries_notifies_once()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var now = new DateTimeOffset(2026, 8, 13, 15, 0, 0, TimeSpan.Zero);
        var fakeTime = new FakeTimeProvider(now);
        var staleReset = now.AddMinutes(-5); // stale, in the past, unchanged across both retries

        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snap-repeat"),
                new WorkflowTemplateId("tmpl-repeat"),
                1,
                [new WorkflowStepDefinition(StepA, "worker-a", [], [], [], RetryPolicy: new RetryPolicy(MaxAttempts: 3, Backoff: BackoffPolicy.Steady))]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["worker-a"] = new WorkerBinding.Process(
                    new WorkerContract("worker-a", [], [], []), ExitCleanlyWithoutWriting(), TimeSpan.FromSeconds(30))
            };

            var execA = new ExecutionId("exec-repeat-a");
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var ct = TestContext.Current.CancellationToken;

            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(
                new ExecutionRequest(execA, new WorkflowId("wf-repeat"), StepA, "worker-a", [], [], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId>())), ct);
            await writer.AppendAsync(new FlowEvent.ExecutionFailed(execA, FailureClassification.ExhaustedUntil, "quota exhausted", staleReset), ct);

            var notifications = new List<DateTimeOffset>();
            var firstNoticed = new TaskCompletionSource();
            using var cts = new CancellationTokenSource();

            var pump = MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-repeat"), roomDirectory, snapshot, bindings, Path.Combine(roomDirectory, "artifacts"),
                reader, writer, new CoreDispatcher(writer, writer),
                timeProvider: fakeTime, jitterSource: () => 0.0, cancellationToken: cts.Token,
                onVendorQuotaPark: instant =>
                {
                    lock (notifications) { notifications.Add(instant); }
                    firstNoticed.TrySetResult();
                });

            await firstNoticed.Task.WaitAsync(PumpCompletionTimeout, ct);
            // The notice itself carries the PACED value (minNotBefore), not the raw instant -- the
            // floor put it at now + 1s. Only the dedupe KEY is the raw stale instant; that's what
            // this test is actually pinning.
            var firstPacedNotice = now + TimeSpan.FromSeconds(1);
            Assert.Equal([firstPacedNotice], notifications);

            // A second retry reporting the SAME raw stale instant, unchanged -- mirrors a vendor that
            // keeps echoing back an already-past reset on every refusal. Its own paced value will
            // differ from the first (a fresh now+1s off a later `now`), which is exactly why deduping
            // on the paced value would re-notify here and deduping on the raw value must not.
            var execB = new ExecutionId("exec-repeat-b");
            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(
                new ExecutionRequest(execB, new WorkflowId("wf-repeat"), StepA, "worker-a", [], [], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId>())), ct);
            await writer.AppendAsync(new FlowEvent.ExecutionFailed(execB, FailureClassification.ExhaustedUntil, "quota exhausted", staleReset), ct);

            fakeTime.Advance(TimeSpan.FromSeconds(1)); // wakes the pump past its 1s floor wait
            await Task.Delay(100, ct); // wait-ok: settle time for the pump's re-projection and re-obligation, not an external wait

            Assert.Single(notifications);
            Assert.Equal(firstPacedNotice, notifications[0]);

            await cts.CancelAsync();
            await pump.WaitAsync(PumpCompletionTimeout, ct);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    // #1183 MED-1(c): a not-before whose millisecond fraction rounds DOWN under Math.Round (here
    // .4ms) must still yield a delayMs at or above the true gap, or DependencyResolver's #712 clamp
    // (`remaining > maxDelay`) could see a maxDelay a whole millisecond short of `remaining` and
    // misfire. Red under Math.Round: (int)Math.Round(5000.4) == 5000, one below the 5001 this test
    // asserts.
    [Fact]
    public async Task Test1183_Ceiling_not_Round_keeps_delayMs_at_or_above_a_sub_millisecond_gap()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var now = new DateTimeOffset(2026, 8, 13, 15, 0, 0, TimeSpan.Zero);
        var fakeTime = new FakeTimeProvider(now);
        // 5000.4ms out: Math.Round(5000.4) == 5000 (rounds down), Math.Ceiling(5000.4) == 5001.
        var resetMoment = now.AddMilliseconds(5000).AddTicks(4000);

        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);

            var execId = new ExecutionId("exec-subms");
            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(
                new ExecutionRequest(execId, new WorkflowId("wf-subms"), StepA, "worker-a", [], [], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId>())), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new FlowEvent.ExecutionFailed(execId, FailureClassification.ExhaustedUntil, "quota exhausted", resetMoment), TestContext.Current.CancellationToken);

            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snap-subms"),
                new WorkflowTemplateId("tpl-subms"),
                1,
                [new WorkflowStepDefinition(StepA, "worker-a", [], [], [], RetryPolicy: new RetryPolicy(MaxAttempts: 2, Backoff: BackoffPolicy.Steady))]);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var state = StateProjector.Project(events, snapshot);

            var getObligationsMethod = typeof(MutationInterface).GetMethod("GetRetryObligations", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            var obligations = (IEnumerable<object>)getObligationsMethod.Invoke(null, [state, snapshot, fakeTime, (Func<double>)(() => 0.0), false, null, new Dictionary<ExecutionId, ExecutionRequest>()])!;
            var obligation = obligations.Single();

            var notBefore = (DateTimeOffset)obligation.GetType().GetProperty("RetryNotBefore")!.GetValue(obligation)!;
            var delayMs = (int)obligation.GetType().GetProperty("RetryDelayMs")!.GetValue(obligation)!;

            Assert.Equal(resetMoment, notBefore);
            Assert.True(
                delayMs >= (notBefore - now).TotalMilliseconds,
                $"Expected delayMs ({delayMs}) at or above the true gap ({(notBefore - now).TotalMilliseconds}ms) so the #712 clamp cannot misfire.");
            Assert.Equal(5001, delayMs);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    // Always reports the SAME fixed instant as ExhaustedUntil, regardless of stderr/stdout content --
    // stands in for a vendor CLI that never advances its reported quota reset, for MED-2 below.
    private sealed class AlwaysStaleQuotaClassifier(DateTimeOffset staleInstant) : IFailureClassifier
    {
        public bool TryClassifyFailure(
            string? stderrTail,
            string? stdoutTail,
            TimeProvider timeProvider,
            out FailureClassification? classification,
            out DateTimeOffset? retryNotBefore)
        {
            classification = FailureClassification.ExhaustedUntil;
            retryNotBefore = staleInstant;
            return true;
        }
    }

    // #1183 MED-2: the floor must bound the pump's own redispatch RATE end to end, not just the
    // arithmetic of a single obligation -- a stub adapter refuses every real dispatch with the SAME
    // stale (past) instant, standing in for a vendor that never advances its reported reset. Driven
    // across ten 1-second fake-clock advances (10s total), the pump must not redispatch more than
    // once per second of fake-clock time.
    [Fact]
    public async Task Test1183_Stale_repeating_instant_paces_pump_dispatch_rate_not_just_one_obligation()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var now = new DateTimeOffset(2026, 8, 13, 15, 0, 0, TimeSpan.Zero);
        var fakeTime = new FakeTimeProvider(now);
        var staleReset = now.AddMinutes(-5);

        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snap-machinegun"),
                new WorkflowTemplateId("tmpl-machinegun"),
                1,
                [new WorkflowStepDefinition(StepA, "worker-a", [], ["out.txt"], [], RetryPolicy: new RetryPolicy(MaxAttempts: 2, Backoff: BackoffPolicy.Steady))]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                // Exits 0 but never writes the declared output, so every attempt fails contract
                // validation and falls into ReadOrClassifyFailure -- which the stub classifier below
                // always answers with the same stale ExhaustedUntil instant.
                ["worker-a"] = new WorkerBinding.Process(
                    new WorkerContract("worker-a", [], [new ProducedOutput("out.txt")], []),
                    ExitWithFailureCode(),
                    TimeSpan.FromSeconds(30),
                    FailureClassifier: new AlwaysStaleQuotaClassifier(staleReset))
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);
            using var cts = new CancellationTokenSource();
            var ct = TestContext.Current.CancellationToken;

            var pump = MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-machinegun"), roomDirectory, snapshot, bindings, artifactsRoot,
                reader, writer, dispatcher,
                timeProvider: fakeTime, jitterSource: () => 0.0, cancellationToken: cts.Token);

            // The very first attempt dispatches immediately -- nothing paces it. Wait for it to
            // actually land before driving the clock, so the first advance never lands before there
            // is anything to wake.
            await WaitForEventAsync<FlowEvent.ExecutionRequestAccepted>(reader, pump, ct);

            const int advances = 10;
            for (var i = 0; i < advances; i++)
            {
                fakeTime.Advance(TimeSpan.FromSeconds(1));
                await Task.Delay(30, ct); // wait-ok: settle time for the pump's redispatch continuation, not an external wait
            }

            // The bound above must hold independent of any FURTHER real time elapsing without a
            // matching fake-clock advance: floor-paced, the pump is asleep on a future fake instant
            // this window never reaches, so nothing here should move the count at all. This is what
            // actually discriminates the floor from real dispatch overhead alone -- without it, a
            // machine-gunning pump gated only by real process-spawn cost (tens of ms/attempt) could
            // still stay under a loose count bound purely because the settle windows above are short,
            // not because anything is pacing it.
            await Task.Delay(2000, ct); // wait-ok: extra real-time settle proving the bound holds independent of wall-clock, not an external wait

            var events = await reader.ReadAllAsync(ct);
            var acceptedCount = events.OfType<FlowEvent.ExecutionRequestAccepted>().Count();

            Assert.True(
                acceptedCount <= advances + 1,
                $"Expected at most {advances + 1} ExecutionRequestAccepted events (floor-paced, one per second plus the first attempt), got {acceptedCount} -- the pump machine-gunned dispatch against a stale repeating instant.");

            await cts.CancelAsync();
            await pump.WaitAsync(PumpCompletionTimeout, ct);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    // #1577: pins MutationInterface.PumpToFixedPointAsync's engineStampedStepIds renewal (its own
    // remarks have why) at the pump level -- a fresh StepRetryScheduled, this process's own pid,
    // before the wait on the (unchanged) deadline actually starts.
    [Fact]
    public async Task Test1577_Revived_pump_stamps_its_own_identity_on_a_backoff_it_did_not_create()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var now = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
        var fakeTime = new FakeTimeProvider(now);
        var retryNotBefore = now.AddMinutes(30);

        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-retry-1577"),
                new WorkflowTemplateId("template-retry-1577"),
                WorkflowTemplateVersion: 1,
                Steps:
                [
                    new WorkflowStepDefinition(StepA, "worker-a", [], ["out.txt"], DependsOn: [], RetryPolicy: new RetryPolicy(MaxAttempts: 2, Backoff: BackoffPolicy.Steady))
                ]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["worker-a"] = new WorkerBinding.Process(
                    new WorkerContract("worker-a", [], [new ProducedOutput("out.txt")], []),
                    ExitWithFailureCode(),
                    TimeSpan.FromSeconds(30))
            };

            // The stranded shape: a prior pump (crashed, or a plain exit) already scheduled this
            // step's backoff and recorded no engine identity for it (the legacy/foreign-pump shape --
            // whether never stamped or stamped by a since-dead process reads identically here, since
            // this pump has no memory of having written it either way).
            var firstAttempt = new ExecutionId("a-1");
            await using (var writerInit = new FlowEventLogWriter(logPath))
            {
                var ct = TestContext.Current.CancellationToken;
                await writerInit.AppendAsync(new FlowEvent.ExecutionRequestAccepted(
                    new ExecutionRequest(firstAttempt, new WorkflowId("wf-1577"), StepA, "worker-a", [], ["out.txt"], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId>())), ct);
                await writerInit.AppendAsync(new FlowEvent.ExecutionFailed(firstAttempt, FailureClassification.Retryable, "boom"), ct);
                await writerInit.AppendAsync(new FlowEvent.StepRetryScheduled(StepA, firstAttempt, retryNotBefore, RetryDelayMs: 1_800_000), ct);
            }

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);

            var pumpTask = MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-1577"),
                roomDirectory,
                snapshot,
                bindings,
                artifactsRoot,
                reader,
                writer,
                dispatcher,
                timeProvider: fakeTime,
                jitterSource: () => 0.0,
                cancellationToken: TestContext.Current.CancellationToken);

            // Positive signal that this revived pump is the one now waiting: a SECOND
            // StepRetryScheduled for the same step/execution/deadline, carrying THIS process's own
            // identity (the pump runs in-process in this test, so its pid is the test's own).
            var stopwatch = Stopwatch.StartNew();
            FlowEvent.StepRetryScheduled? renewal = null;
            while (renewal is null)
            {
                var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
                renewal = events.OfType<FlowEvent.StepRetryScheduled>()
                    .FirstOrDefault(e => e.ForExecutionId == firstAttempt && e.EnginePid is not null);

                if (renewal is null)
                {
                    if (pumpTask.IsCompleted)
                    {
                        await pumpTask;
                        Assert.Fail("Pump completed without renewing the revived step's engine identity.");
                    }

                    Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(60), "Timed out waiting for the identity renewal.");
                    await Task.Delay(10, TestContext.Current.CancellationToken); // wait-ok: short poll interval, bounded by the 60s stopwatch above
                }
            }

            Assert.Equal(StepA, renewal.StepId);
            Assert.Equal(retryNotBefore, renewal.RetryNotBefore);
            Assert.Equal(Environment.ProcessId, renewal.EnginePid);
            Assert.NotNull(renewal.EngineStartTime);

            // Only one renewal for the whole wait -- not once per MaxParkWaitChunk re-arm.
            var eventsMid = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Single(eventsMid.OfType<FlowEvent.StepRetryScheduled>(), e => e.EnginePid is not null);

            var finalState = await AdvanceUntilPumpCompletesAsync(fakeTime, pumpTask, TimeSpan.FromMinutes(5));
            Assert.Equal(StepStatus.Failed, finalState.Steps.Single(s => s.StepId == StepA).Status);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }
}
