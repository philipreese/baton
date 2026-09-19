using Baton.Artifacts;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Mutation;
using Baton.Outcomes;
using Baton.Status;
using Baton.Store;
using Baton.Tests.TestSupport;

namespace Baton.Tests.Mutation;

/// <summary>
/// M10 Phase 3 (full crash-recovery robustness): mutation-level tests proving each of the four crash states a
/// process-bound step's unfinalized latest attempt can be in is reconciled correctly by reading back
/// the Core half of the log. Every fixture manufactures the crash window directly — appending
/// exactly the <see cref="LogEntry"/> lines a real crash would leave behind via
/// <see cref="FlowEventLogWriter"/>'s <see cref="IEventLogWriter"/>/<see cref="ICoreEventLogWriter"/>
/// halves — rather than actually killing a process (Phase 4's job).
/// </summary>
public class MutationInterfaceCrashRecoveryTests
{
    private static readonly StepId A = new("a");
    private static readonly StepId C = new("c");

    private static readonly WorkerContract ProcessContract = new("stub-worker", [], [], []);
    private static readonly CoreDispatchTarget Target = new("stub", []);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static readonly CoreDispatchResult Succeeded = new(0, CoreExitReason.Natural);

    [Fact]
    public async Task StartWorkflowAsync_resubmits_an_execution_with_no_recorded_ExecutionStarted_under_the_same_ExecutionId()
    {
        var snapshot = MakeSnapshot(Step(A, dependsOn: []));

        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings();
            var workflowId = new WorkflowId("wf");

            // The named safe pre-spawn crash state: the intent is durable, but Core never got a
            // chance to run it (crash between accept-fsync and spawn).
            var executionId = await AcceptRequestAsync(writer, workflowId, artifactsRoot, A);

            var stub = new StubCoreDispatcher();
            var aResult = stub.EnqueueResult(A);

            var runTask = MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(A, await ReadNextDispatchAsync(stub));
            aResult.SetResult(Succeeded);
            var state = await runTask;

            Assert.Equal(StepStatus.Succeeded, state.Steps.Single().Status);
            Assert.Equal(executionId, state.Steps.Single().LatestExecutionId);
            Assert.Equal(0, state.Steps.Single().ConsecutiveFailureCount);

            // The same attempt, not a retry: no new ExecutionRequestAccepted for this step.
            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Single(events.OfType<FlowEvent.ExecutionRequestAccepted>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_finalizes_an_unfulfilled_cancellation_for_a_never_started_execution_and_never_dispatches_it()
    {
        var snapshot = MakeSnapshot(Step(A, dependsOn: []), Step(C, dependsOn: [A]));

        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings();
            var workflowId = new WorkflowId("wf");

            var executionId = await AcceptRequestAsync(writer, workflowId, artifactsRoot, A);
            await writer.AppendAsync(new FlowEvent.CancellationRequested(executionId), TestContext.Current.CancellationToken);

            // Nothing enqueued: if the cancel didn't win, StartWorkflowAsync would try to dispatch A
            // (or, worse, C) and StubCoreDispatcher would throw.
            var stub = new StubCoreDispatcher();

            var state = await MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(StepStatus.Cancelled, state.Steps.Single(s => s.StepId == A).Status);
            Assert.Equal(StepStatus.Pending, state.Steps.Single(s => s.StepId == C).Status);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_classifies_a_recorded_exit_with_no_outcome_as_if_the_completion_had_just_arrived()
    {
        var snapshot = MakeSnapshot(Step(A, dependsOn: []), Step(C, dependsOn: [A]));

        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings();
            var workflowId = new WorkflowId("wf");

            var executionId = await AcceptRequestAsync(writer, workflowId, artifactsRoot, A);

            // Ran to a natural, successful exit while Flow was down — Core recorded both
            // lifecycle events, but no Flow-side outcome was ever appended for them.
            await writer.AppendAsync(new CoreEvent.ExecutionStarted(executionId, Pid: 4242), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new CoreEvent.ExecutionExited(executionId, ExitCode: 0, CoreExitReason.Natural), TestContext.Current.CancellationToken);

            var stub = new StubCoreDispatcher();
            var cResult = stub.EnqueueResult(C);

            // Nothing enqueued for A: it must be classified from the recorded exit, never dispatched.
            var runTask = MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(C, await ReadNextDispatchAsync(stub));
            cResult.SetResult(Succeeded);
            var state = await runTask;

            Assert.Equal(StepStatus.Succeeded, state.Steps.Single(s => s.StepId == A).Status);
            Assert.Equal(StepStatus.Succeeded, state.Steps.Single(s => s.StepId == C).Status);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Single(events, e => e is FlowEvent.ExecutionSucceeded es && es.ExecutionId == executionId);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_classifies_a_recorded_nonzero_exit_as_Failed_and_retries_it()
    {
        var snapshot = MakeSnapshot(Step(A, dependsOn: [], maxAttempts: 2));

        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings();
            var workflowId = new WorkflowId("wf");

            var executionId = await AcceptRequestAsync(writer, workflowId, artifactsRoot, A);
            await writer.AppendAsync(new CoreEvent.ExecutionStarted(executionId, Pid: 4242), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new CoreEvent.ExecutionExited(executionId, ExitCode: 1, CoreExitReason.Natural), TestContext.Current.CancellationToken);

            var stub = new StubCoreDispatcher();
            var retryResult = stub.EnqueueResult(A);

            var runTask = MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(A, await ReadNextDispatchAsync(stub));
            retryResult.SetResult(Succeeded);
            var state = await runTask;

            Assert.Equal(StepStatus.Succeeded, state.Steps.Single().Status);

            // Two attempts total: the crash-recovered classification (Failed) plus the retry (Succeeded).
            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, events.OfType<FlowEvent.ExecutionRequestAccepted>().Count());
            Assert.Single(events, e => e is FlowEvent.ExecutionFailed ef && ef.ExecutionId == executionId);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_classifies_a_recorded_nonzero_exit_with_stderr_as_Failed_with_stderr_reason()
    {
        var snapshot = MakeSnapshot(Step(A, dependsOn: [], maxAttempts: 2));

        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings();
            var workflowId = new WorkflowId("wf");

            var executionId = await AcceptRequestAsync(writer, workflowId, artifactsRoot, A);
            await writer.AppendAsync(new CoreEvent.ExecutionStarted(executionId, Pid: 4242), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new CoreEvent.ExecutionExited(executionId, ExitCode: 1, CoreExitReason.Natural, StderrTail: "crash-recovered-stderr-fragment"), TestContext.Current.CancellationToken);

            var stub = new StubCoreDispatcher();
            var retryResult = stub.EnqueueResult(A);

            var runTask = MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(A, await ReadNextDispatchAsync(stub));
            retryResult.SetResult(Succeeded);
            var state = await runTask;

            Assert.Equal(StepStatus.Succeeded, state.Steps.Single().Status);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var failedEvent = Assert.Single(events.OfType<FlowEvent.ExecutionFailed>());
            Assert.Equal(executionId, failedEvent.ExecutionId);
            Assert.NotNull(failedEvent.Reason);
            Assert.Contains("crash-recovered-stderr-fragment", failedEvent.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_finalizes_an_orphan_as_a_retryable_failed_attempt_and_retries_it()
    {
        var snapshot = MakeSnapshot(Step(A, dependsOn: [], maxAttempts: 2));

        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings();
            var workflowId = new WorkflowId("wf");

            var orphanExecutionId = await AcceptRequestAsync(writer, workflowId, artifactsRoot, A);

            // The third crash state: Core recorded the start, but this pump's predecessor died
            // before an exit was ever recorded — nothing to classify against.
            await writer.AppendAsync(new CoreEvent.ExecutionStarted(orphanExecutionId, Pid: 4242), TestContext.Current.CancellationToken);

            var stub = new StubCoreDispatcher();
            var retryResult = stub.EnqueueResult(A);

            // Nothing enqueued for the orphan's own ExecutionId: it must never be dispatched again.
            var runTask = MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(A, await ReadNextDispatchAsync(stub));
            retryResult.SetResult(Succeeded);
            var state = await runTask;

            Assert.Equal(StepStatus.Succeeded, state.Steps.Single().Status);
            Assert.NotEqual(orphanExecutionId, state.Steps.Single().LatestExecutionId);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var abandoned = Assert.Single(events.OfType<FlowEvent.ExecutionFailed>());
            Assert.Equal(orphanExecutionId, abandoned.ExecutionId);
            Assert.Equal(FailureClassification.Retryable, abandoned.FailureClassification);

            // The orphaned attempt's own artifact directory is untouched by the retry, which
            // gets its own fresh directory under the new ExecutionId.
            Assert.True(Directory.Exists(ArtifactManager.ResolveOutputDirectory(artifactsRoot, orphanExecutionId)));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>
    /// Spawns a long-sleeping child, captures its pid/start time while provably alive, then kills it
    /// -- an OS-confirmed-dead identity rather than a fabricated integer that might coincidentally
    /// collide with something else running on the host. Mirrors
    /// <c>Baton.Cli.Tests.TestSupport.ProcessIdentityFixture.DeadProcessIdentity</c> (that fixture
    /// lives in a sibling test project this one has no reference to, per #843's own reasoning for why
    /// a genuinely-dead identity beats a guessed one).
    /// </summary>
    private static (int Pid, DateTimeOffset StartTime) SpawnAndKillProcess()
    {
        var psi = new System.Diagnostics.ProcessStartInfo("ping.exe", "-n 30 127.0.0.1") { CreateNoWindow = true };
        using var process = System.Diagnostics.Process.Start(psi)!;
        try
        {
            return (process.Id, new DateTimeOffset(process.StartTime).ToUniversalTime());
        }
        finally
        {
            process.Kill();
            if (!process.WaitForExit(TimeSpan.FromSeconds(10))) // wait-ok: bounding a post-Kill() exit, expected in milliseconds (#1804)
            {
                throw new TimeoutException($"killed process {process.Id} did not exit within 10s");
            }
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_names_the_worker_pid_and_the_dead_engine_pid_when_finalizing_an_orphan()
    {
        // #1530: the 2026-09-01 janitor-sweep measurement's complaint was that an abandoned room's
        // terminal ExecutionFailed said nothing an operator could act on without digging a PID out by
        // hand -- specifically, whether the WORKER (not baton's own pump) was the thing that died.
        // Both pids here are genuinely dead (spawned and killed), matching the realistic shape: crash
        // recovery reaches this branch only after a prior engine already released flow.lock without
        // recording an exit, so pinning the wording against an artificially-still-alive engine (as
        // the prior version of this test did) canonized a misleading rendering no real crash-recovery
        // run would ever produce (#1530 review finding, LOW: "engine pid N is alive").
        var snapshot = MakeSnapshot(Step(A, dependsOn: [], maxAttempts: 2));

        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings();
            var workflowId = new WorkflowId("wf");

            var (enginePid, engineStartTime) = SpawnAndKillProcess();
            var (workerPid, _) = SpawnAndKillProcess();

            var executionId = new ExecutionId(Guid.NewGuid().ToString("n"));
            var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, executionId);
            var request = new ExecutionRequest(
                executionId, workflowId, A, "stub-worker", Inputs: [], Outputs: [], Timeout,
                ArtifactManager.BuildEnvironment([], outputDirectory, artifactsRoot),
                UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

            await writer.AppendAsync(
                new FlowEvent.ExecutionRequestAccepted(request, EnginePid: enginePid, EngineStartTime: engineStartTime),
                TestContext.Current.CancellationToken);

            // The third crash state: Core recorded the start under the WORKER's own pid, but this
            // pump's predecessor (the ENGINE) died before an exit was ever recorded — nothing to
            // classify against.
            await writer.AppendAsync(new CoreEvent.ExecutionStarted(executionId, Pid: (uint)workerPid), TestContext.Current.CancellationToken);

            var stub = new StubCoreDispatcher();
            var retryResult = stub.EnqueueResult(A);

            var runTask = MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(A, await ReadNextDispatchAsync(stub));
            retryResult.SetResult(Succeeded);
            await runTask;

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var abandoned = Assert.Single(events.OfType<FlowEvent.ExecutionFailed>());
            Assert.Equal(executionId, abandoned.ExecutionId);
            // The worker pid is named with no liveness claim attached -- see
            // MutationInterface.StartWorkflowAsync's "Abandoned during crash recovery" append for why.
            Assert.Contains($"worker pid {workerPid}", abandoned.Reason);
            Assert.Contains($"engine pid {enginePid} is dead", abandoned.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_finalizes_an_orphan_as_terminally_failed_once_its_retry_budget_is_exhausted()
    {
        var snapshot = MakeSnapshot(Step(A, dependsOn: [], maxAttempts: 1));

        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings();
            var workflowId = new WorkflowId("wf");

            var orphanExecutionId = await AcceptRequestAsync(writer, workflowId, artifactsRoot, A);
            await writer.AppendAsync(new CoreEvent.ExecutionStarted(orphanExecutionId, Pid: 4242), TestContext.Current.CancellationToken);

            // Nothing enqueued at all: MaxAttempts: 1 forecloses the retry, so the pump must reach
            // its fixed point without dispatching anything.
            var stub = new StubCoreDispatcher();

            var state = await MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(StepStatus.Failed, state.Steps.Single().Status);
            Assert.Equal(orphanExecutionId, state.Steps.Single().LatestExecutionId);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task Crash_recovery_classification_uses_the_live_contracts_OutputCondition_when_the_binding_resolves()
    {
        // The #724 review's gap: nothing drove a condition-bearing contract through this path. The
        // live binding resolves here, so its OutputCondition must bite — a recorded clean exit
        // whose artifact does NOT satisfy the condition classifies as failure, proving the
        // request-derived fallback (names only, no conditions) is NOT what runs when the live
        // contract is available.
        var snapshot = MakeSnapshot(Step(A, dependsOn: [], worker: "conditioned-worker"));
        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var workflowId = new WorkflowId("wf");
            var executionId = new ExecutionId(Guid.NewGuid().ToString("n"));
            var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, executionId);
            var request = new ExecutionRequest(
                executionId, workflowId, A, "conditioned-worker", Inputs: [], Outputs: ["verdict"], Timeout,
                ArtifactManager.BuildEnvironment([], outputDirectory, artifactsRoot),
                UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(request), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new CoreEvent.ExecutionStarted(executionId, Pid: 4242), TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(outputDirectory, "verdict"), """{"status":"rejected"}""", TestContext.Current.CancellationToken);
            await writer.AppendAsync(new CoreEvent.ExecutionExited(executionId, ExitCode: 0, CoreExitReason.Natural), TestContext.Current.CancellationToken);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["conditioned-worker"] = new WorkerBinding.Process(
                    new WorkerContract(
                        "conditioned-worker", [],
                        [new ProducedOutput("verdict", new OutputCondition("/status", new JsonScalar.String("approved")))],
                        []),
                    Target, Timeout),
            };

            var state = await MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer,
                new StubCoreDispatcher(), cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(StepStatus.Failed, state.Steps.Single(s => s.StepId == A).Status);
            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Single(events, e => e is FlowEvent.ExecutionIndeterminate ei && ei.ExecutionId == executionId);
            Assert.DoesNotContain(events, e => e is FlowEvent.ExecutionSucceeded);
            Assert.DoesNotContain(events, e => e is FlowEvent.ExecutionFailed);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_classifies_crash_recovery_candidate_when_its_worker_binding_refuses_to_resolve()
    {
        // Same recorded history as the absent-worker arm below, but the binding is present and
        // REFUSING (see RefusingBindings) — the classify path must fall back to the recorded
        // request's contract, not surface the refusal.
        var snapshot = MakeSnapshot(Step(A, dependsOn: [], worker: "unresolvable-worker"));
        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var workflowId = new WorkflowId("wf");
            var executionId = new ExecutionId(Guid.NewGuid().ToString("n"));
            var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, executionId);
            var request = new ExecutionRequest(
                executionId, workflowId, A, "unresolvable-worker", Inputs: [], Outputs: [], Timeout,
                ArtifactManager.BuildEnvironment([], outputDirectory, artifactsRoot),
                UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(request), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new CoreEvent.ExecutionStarted(executionId, Pid: 4242), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new CoreEvent.ExecutionExited(executionId, ExitCode: 0, CoreExitReason.Natural), TestContext.Current.CancellationToken);

            var state = await MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, new RefusingBindings(), artifactsRoot, reader, writer,
                new StubCoreDispatcher(), cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(StepStatus.Succeeded, state.Steps.Single(s => s.StepId == A).Status);
            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Single(events, e => e is FlowEvent.ExecutionSucceeded es && es.ExecutionId == executionId);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_classifies_crash_recovery_candidate_when_its_worker_binding_is_unresolvable()
    {
        var snapshot = MakeSnapshot(Step(A, dependsOn: [], worker: "unresolvable-worker"));

        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var emptyBindings = new Dictionary<string, WorkerBinding>();
            var workflowId = new WorkflowId("wf");

            var executionId = new ExecutionId(Guid.NewGuid().ToString("n"));
            var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, executionId);
            var request = new ExecutionRequest(
                executionId,
                workflowId,
                A,
                "unresolvable-worker",
                Inputs: [],
                Outputs: [],
                Timeout,
                ArtifactManager.BuildEnvironment([], outputDirectory, artifactsRoot),
                UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(request), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new CoreEvent.ExecutionStarted(executionId, Pid: 4242), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new CoreEvent.ExecutionExited(executionId, ExitCode: 0, CoreExitReason.Natural), TestContext.Current.CancellationToken);

            var stub = new StubCoreDispatcher();

            var state = await MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, emptyBindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(StepStatus.Succeeded, state.Steps.Single(s => s.StepId == A).Status);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Single(events, e => e is FlowEvent.ExecutionSucceeded es && es.ExecutionId == executionId);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_journals_StepRebound_when_resubmitting_through_a_divergent_adapter()
    {
        // Issue #1583 (operator ruling 2026-09-01): when an unstarted accepted execution is resubmitted
        // after crash-recovery with a binding whose Adapter differs from the request's recorded Adapter,
        // Flow must journal FlowEvent.StepRebound (old->new) before dispatching.
        var snapshot = MakeSnapshot(Step(A, dependsOn: []));

        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings(adapter: "claude", model: "sonnet");
            var workflowId = new WorkflowId("wf");

            var executionId = await AcceptRequestAsync(writer, workflowId, artifactsRoot, A, adapter: "agy", model: "gemini-3-pro");

            var stub = new StubCoreDispatcher();
            var aResult = stub.EnqueueResult(A);

            var runTask = MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(A, await ReadNextDispatchAsync(stub));
            aResult.SetResult(Succeeded);
            var state = await runTask;

            Assert.Equal(StepStatus.Succeeded, state.Steps.Single().Status);
            Assert.Equal(executionId, state.Steps.Single().LatestExecutionId);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var rebound = Assert.Single(events.OfType<FlowEvent.StepRebound>());
            Assert.Equal(A, rebound.StepId);
            Assert.Equal(executionId, rebound.ForExecutionId);
            Assert.Equal("agy", rebound.PreviousAdapter);
            Assert.Equal("gemini-3-pro", rebound.PreviousModel);
            Assert.Equal("claude", rebound.NewAdapter);
            Assert.Equal("sonnet", rebound.NewModel);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_journals_StepRebound_when_resubmitting_through_a_divergent_model()
    {
        // Issue #1583: model divergence (same adapter, different model) also journals StepRebound.
        var snapshot = MakeSnapshot(Step(A, dependsOn: []));

        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings(adapter: "claude", model: "opus");
            var workflowId = new WorkflowId("wf");

            var executionId = await AcceptRequestAsync(writer, workflowId, artifactsRoot, A, adapter: "claude", model: "sonnet");

            var stub = new StubCoreDispatcher();
            var aResult = stub.EnqueueResult(A);

            var runTask = MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(A, await ReadNextDispatchAsync(stub));
            aResult.SetResult(Succeeded);
            var state = await runTask;

            Assert.Equal(StepStatus.Succeeded, state.Steps.Single().Status);
            Assert.Equal(executionId, state.Steps.Single().LatestExecutionId);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var rebound = Assert.Single(events.OfType<FlowEvent.StepRebound>());
            Assert.Equal(A, rebound.StepId);
            Assert.Equal(executionId, rebound.ForExecutionId);
            Assert.Equal("claude", rebound.PreviousAdapter);
            Assert.Equal("sonnet", rebound.PreviousModel);
            Assert.Equal("claude", rebound.NewAdapter);
            Assert.Equal("opus", rebound.NewModel);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_does_not_journal_StepRebound_for_a_legacy_request_with_no_recorded_Adapter()
    {
        // #1583 MEDIUM: a request accepted before #1567 added ExecutionRequest.Adapter/Model has both
        // fields null -- absence of a record, not absence of an adapter (ExecutionRequest.Adapter's own
        // doc). Comparing null against the current binding's "claude" must not read as a divergence:
        // nobody rebound anything, and journaling StepRebound(null->claude) would durably assert a
        // failover that never happened.
        var snapshot = MakeSnapshot(Step(A, dependsOn: []));

        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings(adapter: "claude", model: "sonnet");
            var workflowId = new WorkflowId("wf");

            var executionId = await AcceptRequestAsync(writer, workflowId, artifactsRoot, A, adapter: null, model: null);

            var stub = new StubCoreDispatcher();
            var aResult = stub.EnqueueResult(A);

            var runTask = MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(A, await ReadNextDispatchAsync(stub));
            aResult.SetResult(Succeeded);
            var state = await runTask;

            Assert.Equal(StepStatus.Succeeded, state.Steps.Single().Status);
            Assert.Equal(executionId, state.Steps.Single().LatestExecutionId);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Empty(events.OfType<FlowEvent.StepRebound>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_journals_a_second_StepRebound_when_a_rebind_reverts_after_a_pre_spawn_crash()
    {
        // #1583 HIGH, review scenario B: pump P1 accepted on "claude"; pump P2 rebound to "agy" and
        // journaled StepRebound(claude->agy) but crashed pre-spawn (never appended a Core outcome), so
        // this fixture manufactures exactly that log tail directly, the same way every other fixture
        // in this file manufactures its crash window. Pump P3 (this call) sees the binding reverted
        // back to "claude". Without StateProjector projecting the first StepRebound as an override on
        // AcceptedRequestByExecutionId, P3's replay still reads request.Adapter == "claude" (the
        // original accept), sees no divergence against the current "claude" binding, and journals
        // nothing -- silently reintroducing the exact misattribution #1583 exists to fix. With the
        // projection arm in place, P3 sees the request as bound to "agy" (the first StepRebound's
        // override), detects divergence against the current "claude" binding, and journals a SECOND
        // StepRebound naming agy->claude.
        var snapshot = MakeSnapshot(Step(A, dependsOn: []));

        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings(adapter: "claude", model: null);
            var workflowId = new WorkflowId("wf");

            var executionId = await AcceptRequestAsync(writer, workflowId, artifactsRoot, A, adapter: "claude", model: null);
            await writer.AppendAsync(
                new FlowEvent.StepRebound(A, executionId, PreviousAdapter: "claude", PreviousModel: null, NewAdapter: "agy", NewModel: null),
                TestContext.Current.CancellationToken);

            var stub = new StubCoreDispatcher();
            var aResult = stub.EnqueueResult(A);

            var runTask = MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(A, await ReadNextDispatchAsync(stub));
            aResult.SetResult(Succeeded);
            var state = await runTask;

            Assert.Equal(StepStatus.Succeeded, state.Steps.Single().Status);
            Assert.Equal(executionId, state.Steps.Single().LatestExecutionId);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var rebounds = events.OfType<FlowEvent.StepRebound>().ToList();
            Assert.Equal(2, rebounds.Count);
            var second = rebounds[1];
            Assert.Equal(executionId, second.ForExecutionId);
            Assert.Equal("agy", second.PreviousAdapter);
            Assert.Equal("claude", second.NewAdapter);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_dispatches_a_divergent_resubmit_with_the_rebound_Adapter_and_Model_in_the_same_round()
    {
        // #1583 MEDIUM: MutationInterface.cs's in-memory `request with { Adapter, Model }` update
        // (kept alongside the StateProjector override as the same-round fast path) has to actually
        // reach Core -- DispatchAndRecordOutcomeAsync's live classification reads prepared.Request.Adapter,
        // not the binding, so a dispatch still carrying the pre-crash Adapter/Model would pick the wrong
        // usage parser for this round's own outcome classification even though the journaled event is
        // correct. Deleting the in-memory update (MutationInterface.cs:907-912) makes this fail.
        var snapshot = MakeSnapshot(Step(A, dependsOn: []));

        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings(adapter: "claude", model: "sonnet");
            var workflowId = new WorkflowId("wf");

            var executionId = await AcceptRequestAsync(writer, workflowId, artifactsRoot, A, adapter: "agy", model: "gemini-3-pro");

            var stub = new StubCoreDispatcher();
            var aResult = stub.EnqueueResult(A);

            var runTask = MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(A, await ReadNextDispatchAsync(stub));
            aResult.SetResult(Succeeded);
            await runTask;

            Assert.NotNull(stub.LastDispatchedRequest);
            Assert.Equal("claude", stub.LastDispatchedRequest!.Adapter);
            Assert.Equal("sonnet", stub.LastDispatchedRequest.Model);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_does_not_journal_StepRebound_when_resubmitting_through_an_identical_binding()
    {
        // Control / non-divergent case: when the binding matches the request's recorded Adapter and Model,
        // no FlowEvent.StepRebound is emitted.
        var snapshot = MakeSnapshot(Step(A, dependsOn: []));

        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings(adapter: "claude", model: "sonnet");
            var workflowId = new WorkflowId("wf");

            var executionId = await AcceptRequestAsync(writer, workflowId, artifactsRoot, A, adapter: "claude", model: "sonnet");

            var stub = new StubCoreDispatcher();
            var aResult = stub.EnqueueResult(A);

            var runTask = MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(A, await ReadNextDispatchAsync(stub));
            aResult.SetResult(Succeeded);
            var state = await runTask;

            Assert.Equal(StepStatus.Succeeded, state.Steps.Single().Status);
            Assert.Equal(executionId, state.Steps.Single().LatestExecutionId);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Empty(events.OfType<FlowEvent.StepRebound>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_settles_unmatched_VerifyStarted_Indeterminate_across_engine_restart()
    {
        // #1623 F2 crash recovery arm: see MutationInterface.cs ToClassify reconciliation.
        var snapshot = MakeSnapshot(Step(A, dependsOn: []));
        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings();
            var workflowId = new WorkflowId("wf");

            var executionId = await AcceptRequestAsync(writer, workflowId, artifactsRoot, A);
            await writer.AppendAsync(new CoreEvent.ExecutionStarted(executionId, Pid: 4242), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new CoreEvent.ExecutionExited(executionId, ExitCode: 0, CoreExitReason.Natural), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new FlowEvent.VerifyStarted(executionId), TestContext.Current.CancellationToken);

            var stub = new StubCoreDispatcher();

            var state = await MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);

            var stepState = Assert.Single(state.Steps);
            Assert.Equal(StepStatus.Failed, stepState.Status);
            Assert.NotNull(stepState.IndeterminateReason);
            Assert.Contains("engine restart", stepState.IndeterminateReason);
            Assert.True(stepState.RetryForeclosed);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Empty(events.OfType<FlowEvent.ExecutionSucceeded>());
            var verifyFailed = Assert.Single(events.OfType<FlowEvent.VerifyFailed>());
            Assert.Equal(VerifyFailedKind.EngineRestart, verifyFailed.Kind);
            Assert.Equal("verify did not complete across an engine restart", verifyFailed.Tail);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Theory]
    [InlineData("missing", false)]
    [InlineData("passed", true)]
    [InlineData("failed", false)]
    [InlineData("not-run", false)]
    [InlineData("invalid", false)]
    public async Task A_replayed_delivery_exit_uses_only_its_journalled_observation(string observationCase, bool expectSuccess)
    {
        var snapshot = MakeSnapshot(Step(A, dependsOn: []));
        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var workflowId = new WorkflowId("wf-delivery-replay");
            var executionId = await AcceptRequestAsync(writer, workflowId, artifactsRoot, A, deliversBranch: true);
            var outputDirectory = ArtifactManager.ResolveOutputDirectory(artifactsRoot, executionId);
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, DeliveryVerifier.DeliveryEvidenceFileName),
                """{"observedAt":"2026-09-15T00:00:00Z","verification":"Passed","localHead":"0123456789abcdef0123456789abcdef01234567","branch":"lane","remoteHead":"0123456789abcdef0123456789abcdef01234567"}""",
                TestContext.Current.CancellationToken);
            await writer.AppendAsync(new CoreEvent.ExecutionStarted(executionId, Pid: 4242), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new CoreEvent.ExecutionExited(executionId, ExitCode: 0, CoreExitReason.Natural), TestContext.Current.CancellationToken);

            const string head = "0123456789abcdef0123456789abcdef01234567";
            var observation = observationCase switch
            {
                "passed" => new FlowEvent.DeliveryObservationRecorded(executionId, DateTimeOffset.UtcNow.ToString("O"),
                    head, "lane", head, null, "Passed"),
                "failed" => new FlowEvent.DeliveryObservationRecorded(executionId, DateTimeOffset.UtcNow.ToString("O"),
                    head, "lane", null, null, "Failed", ["branch-not-pushed"], "branch-not-pushed"),
                "not-run" => new FlowEvent.DeliveryObservationRecorded(executionId, DateTimeOffset.UtcNow.ToString("O"),
                    head, "lane", null, null, "NotRun", null, "delivery check unavailable"),
                "invalid" => new FlowEvent.DeliveryObservationRecorded(executionId, DateTimeOffset.UtcNow.ToString("O"),
                    null, null, null, null, "Invalid"),
                _ => null,
            };
            if (observation is not null) await writer.AppendAsync(observation, TestContext.Current.CancellationToken);

            // No worker or Git probe is available in recovery. Even a passing file in its output
            // cannot decide this exit; only the flow-ledger observation above may do that.
            var state = await MutationInterface.StartWorkflowAsync(workflowId, roomDirectory, snapshot,
                MakeBindings(), artifactsRoot, reader, writer, new StubCoreDispatcher(),
                cancellationToken: TestContext.Current.CancellationToken);
            var step = Assert.Single(state.Steps);
            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            if (expectSuccess)
            {
                Assert.Equal(StepStatus.Succeeded, step.Status);
                Assert.Single(events.OfType<FlowEvent.ExecutionSucceeded>());
                Assert.Empty(events.OfType<FlowEvent.VerifyFailed>());
            }
            else
            {
                Assert.True(step.IndeterminateAwaitingResolution);
                Assert.Empty(events.OfType<FlowEvent.ExecutionSucceeded>());
                if (observationCase == "not-run")
                {
                    var failure = Assert.Single(events.OfType<FlowEvent.VerifyFailed>());
                    Assert.Equal(VerifyFailedKind.DeliveryNotRun, failure.Kind);
                    Assert.Equal("delivery check unavailable", failure.Tail);
                }
                else
                {
                    var failure = Assert.Single(events.OfType<FlowEvent.VerifyFailed>());
                    Assert.Equal(observationCase == "failed"
                            ? VerifyFailedKind.DeliveryFailed
                            : VerifyFailedKind.EngineRestart,
                        failure.Kind);
                }
            }
        }
        finally { DirectoryCleanup.DeleteRecursively(roomDirectory); }
    }

    [Fact]
    public async Task A_replayed_pass_cannot_survive_when_the_recorded_HEAD_equals_attempt_start()
    {
        var snapshot = MakeSnapshot(Step(A, dependsOn: []));
        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var workflowId = new WorkflowId("wf-delivery-revision-replay");
            var executionId = await AcceptRequestAsync(writer, workflowId, artifactsRoot, A, deliversBranch: true);
            const string head = "0123456789abcdef0123456789abcdef01234567";
            await writer.AppendAsync(new FlowEvent.ExecutionAttemptStarted(executionId, head), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new CoreEvent.ExecutionStarted(executionId, Pid: 4242), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new CoreEvent.ExecutionExited(executionId, ExitCode: 0, CoreExitReason.Natural), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new FlowEvent.DeliveryObservationRecorded(
                executionId, DateTimeOffset.UtcNow.ToString("O"), head, "lane", head, null, "Passed"),
                TestContext.Current.CancellationToken);

            var state = await MutationInterface.StartWorkflowAsync(workflowId, roomDirectory, snapshot,
                MakeBindings(), artifactsRoot, reader, writer, new StubCoreDispatcher(),
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(Assert.Single(state.Steps).IndeterminateAwaitingResolution);
            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Empty(events.OfType<FlowEvent.ExecutionSucceeded>());
            var failed = Assert.Single(events.OfType<FlowEvent.VerifyFailed>());
            Assert.Equal(VerifyFailedKind.DeliveryFailed, failed.Kind);
            Assert.Equal(["revision-not-created"], failed.FailingMembers);
        }
        finally { DirectoryCleanup.DeleteRecursively(roomDirectory); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_later_remote_push_cannot_upgrade_a_missing_or_failed_delivery_observation_on_restart(bool recordedFailure)
    {
        var origin = TempGitRepository.InitBareRepository(Path.Combine(Path.GetTempPath(), $"replay-origin-{Guid.NewGuid():N}"));
        var workspace = Path.Combine(Path.GetTempPath(), $"replay-ws-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        var snapshot = MakeSnapshot(Step(A, dependsOn: []));
        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            TempGitRepository.InitWithEverythingCommitted(workspace);
            TempGitRepository.AddRemote(workspace, "origin", origin);
            TempGitRepository.CreateAndCheckoutBranch(workspace, "replay-lane");
            TempGitRepository.CommitAll(workspace, "pushed base");
            TempGitRepository.Push(workspace, "origin", "replay-lane");
            TempGitRepository.CommitAll(workspace, "not yet pushed at observation");
            var localHead = TempGitRepository.TagHeadWithItsOwnSha(workspace);

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var workflowId = new WorkflowId("wf-remote-replay");
            var executionId = await AcceptRequestAsync(writer, workflowId, artifactsRoot, A, deliversBranch: true);
            var outputDirectory = ArtifactManager.ResolveOutputDirectory(artifactsRoot, executionId);
            var handoffPath = Path.Combine(outputDirectory, "changes.md");
            await File.WriteAllTextAsync(handoffPath, "push succeeded (worker claim)", TestContext.Current.CancellationToken);
            var handoffBefore = await File.ReadAllBytesAsync(handoffPath, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, DeliveryVerifier.DeliveryEvidenceFileName),
                """{"observedAt":"2026-09-15T00:00:00Z","verification":"Passed","localHead":"0123456789abcdef0123456789abcdef01234567","branch":"replay-lane","remoteHead":"0123456789abcdef0123456789abcdef01234567"}""",
                TestContext.Current.CancellationToken);
            await writer.AppendAsync(new CoreEvent.ExecutionStarted(executionId, Pid: 4242), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new CoreEvent.ExecutionExited(executionId, ExitCode: 0, CoreExitReason.Natural), TestContext.Current.CancellationToken);
            if (recordedFailure)
            {
                await writer.AppendAsync(new FlowEvent.DeliveryObservationRecorded(executionId,
                    DateTimeOffset.UtcNow.ToString("O"), localHead, "replay-lane", null, null, "Failed",
                    ["branch-not-pushed"], "branch-not-pushed"), TestContext.Current.CancellationToken);
            }

            // The remote changes AFTER this execution's observation/crash window.
            TempGitRepository.Push(workspace, "origin", "replay-lane");
            Assert.Equal(DeliveryCheckStatus.Passed,
                (await DeliveryVerifier.CheckAsync(workspace, expectPr: false, TestContext.Current.CancellationToken)).Status);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["stub-worker"] = new WorkerBinding.Process(ProcessContract,
                    new CoreDispatchTarget("stub", [], WorkingDirectory: workspace), Timeout,
                    DeliversBranch: true, ExpectPr: false),
            };
            var state = await MutationInterface.StartWorkflowAsync(workflowId, roomDirectory, snapshot,
                bindings, artifactsRoot, reader, writer, new StubCoreDispatcher(),
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(Assert.Single(state.Steps).IndeterminateAwaitingResolution);
            Assert.Empty((await reader.ReadAllAsync(TestContext.Current.CancellationToken)).OfType<FlowEvent.ExecutionSucceeded>());
            Assert.Equal(handoffBefore, await File.ReadAllBytesAsync(handoffPath, TestContext.Current.CancellationToken));

            var entries = await reader.ReadAllEntriesWithTimestampsAsync(TestContext.Current.CancellationToken);
            var projected = await WorkflowStatusProjector.WithDeliveryEvidenceAsync(
                WorkflowStatusProjector.Project(state, snapshot, roomDirectory, entries), entries, roomDirectory,
                TestContext.Current.CancellationToken);
            var delivery = Assert.Single(projected.Delivery!);
            Assert.NotNull(delivery.Handoff);
            Assert.Equal(recordedFailure ? "failed" : "unknown", delivery.AuthoritativeObservation.State);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
            DirectoryCleanup.DeleteRecursively(workspace);
            DirectoryCleanup.DeleteRecursively(origin);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_settles_a_replayed_natural_exit0_with_tool_calls_and_an_empty_ledger_Indeterminate()
    {
        // #1732 review N3 acceptance test -- see MutationInterface.cs's own comment at the
        // crash-recovery classify call for the ruling this pins. A natural exit 0, contract satisfied
        // (ProcessContract declares no outputs), at least one tool call recorded in the rolled-forward
        // stdout log, and a hook verdict ledger reporting zero must settle Indeterminate across an
        // engine restart exactly as it would live.
        var snapshot = MakeSnapshot(Step(A, dependsOn: []));
        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);

            var target = new CoreDispatchTarget("stub", [], CountHookVerdicts: _ => 0);
            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["stub-worker"] = new WorkerBinding.Process(ProcessContract, target, Timeout),
            };
            var workflowId = new WorkflowId("wf");

            var executionId = await AcceptRequestAsync(writer, workflowId, artifactsRoot, A, adapter: "agy");
            var outputDirectory = ArtifactManager.ResolveOutputDirectory(artifactsRoot, executionId);
            File.WriteAllText(
                Path.Combine(outputDirectory, ExecutionStreamLogger.StdoutLogFileName),
                """{"event":"step_update","step_update":{"step_type":"tool","tool_name":"run_command","state":"DONE"}}""" + "\n");

            await writer.AppendAsync(new CoreEvent.ExecutionStarted(executionId, Pid: 4242), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new CoreEvent.ExecutionExited(executionId, ExitCode: 0, CoreExitReason.Natural), TestContext.Current.CancellationToken);

            var stub = new StubCoreDispatcher();

            var state = await MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);

            var stepState = Assert.Single(state.Steps);
            Assert.Equal(StepStatus.Failed, stepState.Status);
            Assert.True(stepState.IndeterminateAwaitingResolution);
            Assert.Equal(IndeterminateProducer.ContractFailure, stepState.IndeterminateProducer);
            // ContractFailure is the producer whose diagnostic lives on LatestFailureReason, not
            // IndeterminateReason (FlowState.cs's own doc on IndeterminateReason: only the
            // VerifyFailed/Arrested producers populate that field).
            Assert.NotNull(stepState.LatestFailureReason);
            Assert.Contains("PreToolUse hook recorded zero verdicts", stepState.LatestFailureReason);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Empty(events.OfType<FlowEvent.ExecutionSucceeded>());
            Assert.Single(events, e => e is FlowEvent.ExecutionIndeterminate ei && ei.ExecutionId == executionId);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_counts_a_tool_step_that_only_survives_in_the_rolled_over_stdout_segment()
    {
        // #1732 review N4: ExecutionStreamLogger performs a single 8 MiB rollover into
        // .stdout.log.1 -- a long run's earliest tool steps live there, not in the current
        // .stdout.log tail. This writes the one tool-step line ONLY into the rollover file (an
        // empty current .stdout.log, as a real post-roll run would have between rolls) and asserts
        // the canary still counts it and fires -- proving CountToolCallsFromStdoutLog does not miss
        // the rolled segment, on the same crash-recovery path N3 wires it into.
        var snapshot = MakeSnapshot(Step(A, dependsOn: []));
        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);

            var target = new CoreDispatchTarget("stub", [], CountHookVerdicts: _ => 0);
            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["stub-worker"] = new WorkerBinding.Process(ProcessContract, target, Timeout),
            };
            var workflowId = new WorkflowId("wf");

            var executionId = await AcceptRequestAsync(writer, workflowId, artifactsRoot, A, adapter: "agy");
            var outputDirectory = ArtifactManager.ResolveOutputDirectory(artifactsRoot, executionId);
            File.WriteAllText(Path.Combine(outputDirectory, ExecutionStreamLogger.StdoutLogFileName), string.Empty);
            File.WriteAllText(
                Path.Combine(outputDirectory, ExecutionStreamLogger.StdoutRolloverFileName),
                """{"event":"step_update","step_update":{"step_type":"tool","tool_name":"run_command","state":"DONE"}}""" + "\n");

            await writer.AppendAsync(new CoreEvent.ExecutionStarted(executionId, Pid: 4242), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new CoreEvent.ExecutionExited(executionId, ExitCode: 0, CoreExitReason.Natural), TestContext.Current.CancellationToken);

            var stub = new StubCoreDispatcher();

            var state = await MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);

            // The canary firing at all is the discriminating signal: it only fires when toolCallCount
            // > 0, so if the rolled-over segment were skipped this would settle Succeeded instead.
            var stepState = Assert.Single(state.Steps);
            Assert.Equal(StepStatus.Failed, stepState.Status);
            Assert.Equal(IndeterminateProducer.ContractFailure, stepState.IndeterminateProducer);
            Assert.NotNull(stepState.LatestFailureReason);
            Assert.Contains("PreToolUse hook recorded zero verdicts across 1 tool call", stepState.LatestFailureReason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_arms_the_replay_canary_from_the_recorded_request_when_todays_binding_refuses_to_resolve()
    {
        // #1741 acceptance test: the RECORDED HookCanaryArmed fact -- not today's binding
        // resolution -- decides whether the crash-recovery replay's first-verdict canary can fire.
        // The binding here REFUSES to resolve on restart (RefusingBindings, the #710 shape
        // ExecutionRequest.HookCanaryArmed's own doc names), which before this fix un-armed the
        // canary entirely and settled Succeeded (spec/baton.md §9's former residual). The recorded
        // request already says this execution ran under sole-hook narrowing, so the replay still
        // counts and still settles Indeterminate.
        var snapshot = MakeSnapshot(Step(A, dependsOn: [], worker: "unresolvable-worker"));
        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var workflowId = new WorkflowId("wf");
            var executionId = new ExecutionId(Guid.NewGuid().ToString("n"));
            var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, executionId);
            File.WriteAllText(
                Path.Combine(outputDirectory, ExecutionStreamLogger.StdoutLogFileName),
                """{"event":"step_update","step_update":{"step_type":"tool","tool_name":"run_command","state":"DONE"}}""" + "\n");
            // The empty ledger: the hook's own verdict channel recorded nothing for this execution.
            File.WriteAllText(Path.Combine(outputDirectory, "verdicts.ndjson"), string.Empty);

            var request = new ExecutionRequest(
                executionId, workflowId, A, "unresolvable-worker", Inputs: [], Outputs: [], Timeout,
                ArtifactManager.BuildEnvironment([], outputDirectory, artifactsRoot),
                UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>(),
                Adapter: "agy",
                HookCanaryArmed: true,
                HookVerdictLedgerFileName: "verdicts.ndjson");

            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(request), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new CoreEvent.ExecutionStarted(executionId, Pid: 4242), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new CoreEvent.ExecutionExited(executionId, ExitCode: 0, CoreExitReason.Natural), TestContext.Current.CancellationToken);

            var state = await MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, new RefusingBindings(), artifactsRoot, reader, writer,
                new StubCoreDispatcher(), cancellationToken: TestContext.Current.CancellationToken);

            var stepState = Assert.Single(state.Steps);
            Assert.Equal(StepStatus.Failed, stepState.Status);
            Assert.True(stepState.IndeterminateAwaitingResolution);
            Assert.Equal(IndeterminateProducer.ContractFailure, stepState.IndeterminateProducer);
            Assert.NotNull(stepState.LatestFailureReason);
            Assert.Contains("PreToolUse hook recorded zero verdicts", stepState.LatestFailureReason);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Empty(events.OfType<FlowEvent.ExecutionSucceeded>());
            Assert.Single(events, e => e is FlowEvent.ExecutionIndeterminate ei && ei.ExecutionId == executionId);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_settles_succeeded_when_the_recorded_canary_is_armed_and_the_ledger_is_healthy()
    {
        // Companion to the arm-from-the-recorded-request test above: same recorded arming fact,
        // same refusing binding (no dependency on today's binding resolving either way), but a
        // HEALTHY ledger -- at least one recorded verdict -- so the canary does not fire and the
        // naturally-exited, contract-satisfied run settles Succeeded exactly as it would live.
        //
        // #1753 review F3: this is a forward regression guard, not a test that discriminates pre-#1741
        // code from post-#1741 code -- under a REFUSING binding, pre-#1741 code takes the
        // `countHookVerdicts is not null` branch, which BatonFlowException's catch above it leaves
        // permanently null regardless of ledger content, so it settles Succeeded unconditionally here
        // too. The discrimination between the two code states is carried entirely by the sibling test
        // above (StartWorkflowAsync_arms_the_replay_canary_...), which flips Succeeded -> Indeterminate
        // across the fix; this test only pins that a HEALTHY ledger keeps settling Succeeded once armed.
        var snapshot = MakeSnapshot(Step(A, dependsOn: [], worker: "unresolvable-worker"));
        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var workflowId = new WorkflowId("wf");
            var executionId = new ExecutionId(Guid.NewGuid().ToString("n"));
            var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, executionId);
            File.WriteAllText(
                Path.Combine(outputDirectory, ExecutionStreamLogger.StdoutLogFileName),
                """{"event":"step_update","step_update":{"step_type":"tool","tool_name":"run_command","state":"DONE"}}""" + "\n");
            File.WriteAllText(Path.Combine(outputDirectory, "verdicts.ndjson"), "{\"decision\":\"allow\"}\n");

            var request = new ExecutionRequest(
                executionId, workflowId, A, "unresolvable-worker", Inputs: [], Outputs: [], Timeout,
                ArtifactManager.BuildEnvironment([], outputDirectory, artifactsRoot),
                UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>(),
                Adapter: "agy",
                HookCanaryArmed: true,
                HookVerdictLedgerFileName: "verdicts.ndjson");

            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(request), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new CoreEvent.ExecutionStarted(executionId, Pid: 4242), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new CoreEvent.ExecutionExited(executionId, ExitCode: 0, CoreExitReason.Natural), TestContext.Current.CancellationToken);

            var state = await MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, new RefusingBindings(), artifactsRoot, reader, writer,
                new StubCoreDispatcher(), cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(StepStatus.Succeeded, state.Steps.Single(s => s.StepId == A).Status);
            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Single(events, e => e is FlowEvent.ExecutionSucceeded es && es.ExecutionId == executionId);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task Restart_late_failure_uses_the_recorded_output_contract_and_settles_once()
    {
        var snapshot = MakeSnapshot(Step(A, dependsOn: []));
        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var workflowId = new WorkflowId("wf-late-failure-restart");
            // late-failure replay fixture
            var executionId = new ExecutionId(Guid.NewGuid().ToString("n"));
            var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, executionId);
            var contract = new WorkerContract(
                "stub-worker", [], [new ProducedOutput("report.md", Schema: OutputSchema.NonEmptyText)], []);
            var request = new ExecutionRequest(
                executionId,
                workflowId,
                A,
                "stub-worker",
                Inputs: [],
                Outputs: ["report.md"],
                Timeout,
                ArtifactManager.BuildEnvironment([], outputDirectory, artifactsRoot),
                UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>(),
                ProducedOutputs: contract.ProducedOutputs);

            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(request), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new CoreEvent.ExecutionStarted(executionId, Pid: 4242), TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "report.md"), "complete report", TestContext.Current.CancellationToken);
            await writer.AppendAsync(
                new CoreEvent.ExecutionExited(
                    executionId,
                    ExitCode: 1,
                    CoreExitReason.Natural,
                    StderrTail: "late capacity failure",
                    TerminalSuccessObserved: false,
                    TerminalResultObserved: true),
                TestContext.Current.CancellationToken);

            var state = await MutationInterface.StartWorkflowAsync(
                workflowId,
                roomDirectory,
                snapshot,
                new Dictionary<string, WorkerBinding>
                {
                    ["stub-worker"] = new WorkerBinding.Process(
                        contract, Target, Timeout, FailureClassifier: new LateCapacityClassifier()),
                },
                artifactsRoot,
                reader,
                writer,
                new StubCoreDispatcher(),
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(StepStatus.Succeeded, Assert.Single(state.Steps).Status);
            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Single(events.OfType<FlowEvent.ExecutionSucceededWithLateFailure>());
            Assert.DoesNotContain(events, e => e is FlowEvent.ExecutionSucceeded);
            Assert.DoesNotContain(events, e => e is FlowEvent.ExecutionFailed);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task Restart_late_failure_without_delivery_evidence_does_not_settle()
    {
        var snapshot = MakeSnapshot(new WorkflowStepDefinition(
            A, "stub-worker", [], ["report.md"], [], new RetryPolicy(1)));
        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var workflowId = new WorkflowId("wf-late-failure-missing-delivery");
            var executionId = new ExecutionId(Guid.NewGuid().ToString("n"));
            var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, executionId);
            var contract = new WorkerContract(
                "stub-worker", [], [new ProducedOutput("report.md", Schema: OutputSchema.NonEmptyText)], []);
            var request = new ExecutionRequest(
                executionId,
                workflowId,
                A,
                "stub-worker",
                Inputs: [],
                Outputs: ["report.md"],
                Timeout,
                ArtifactManager.BuildEnvironment([], outputDirectory, artifactsRoot),
                UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>(),
                DeliversBranch: true,
                ProducedOutputs: contract.ProducedOutputs);

            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(request), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new CoreEvent.ExecutionStarted(executionId, Pid: 4242), TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "report.md"), "complete report", TestContext.Current.CancellationToken);
            await writer.AppendAsync(
                new CoreEvent.ExecutionExited(
                    executionId,
                    ExitCode: 1,
                    CoreExitReason.Natural,
                    StderrTail: "late capacity failure",
                    TerminalResultObserved: true),
                TestContext.Current.CancellationToken);

            var state = await MutationInterface.StartWorkflowAsync(
                workflowId,
                roomDirectory,
                snapshot,
                new Dictionary<string, WorkerBinding>
                {
                    ["stub-worker"] = new WorkerBinding.Process(
                        contract, Target, Timeout, FailureClassifier: new LateCapacityClassifier(), DeliversBranch: true),
                },
                artifactsRoot,
                reader,
                writer,
                new StubCoreDispatcher(),
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(StepStatus.Failed, Assert.Single(state.Steps).Status);
            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Empty(events.OfType<FlowEvent.ExecutionSucceededWithLateFailure>());
            var failure = Assert.Single(events.OfType<FlowEvent.VerifyFailed>());
            Assert.Equal(VerifyFailedKind.EngineRestart, failure.Kind);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task Restart_crash_window_consumes_runtime_direct_body_file_provenance()
    {
        var snapshot = MakeSnapshot(Step(A, dependsOn: []));
        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        var origin = TempGitRepository.InitBareRepository(
            Path.Combine(Path.GetTempPath(), $"restart-runtime-origin-{Guid.NewGuid():N}"));
        var workspace = Path.Combine(Path.GetTempPath(), $"restart-runtime-workspace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        try
        {
            TempGitRepository.InitWithEverythingCommitted(workspace);
            TempGitRepository.AddRemote(workspace, "origin", origin);
            TempGitRepository.CreateAndCheckoutBranch(workspace, "restart-runtime-provenance");
            TempGitRepository.CommitAll(workspace, "lane baseline");
            TempGitRepository.Push(workspace, "origin", "restart-runtime-provenance");

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var workflowId = new WorkflowId("wf-restart-runtime-provenance");
            var executionId = new ExecutionId(Guid.NewGuid().ToString("n"));
            var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, executionId);
            var contract = new WorkerContract(
                "stub-worker", [], [new ProducedOutput("changes.md", Schema: OutputSchema.NonEmptyText)], []);
            var request = new ExecutionRequest(
                executionId,
                workflowId,
                A,
                "stub-worker",
                Inputs: [],
                Outputs: ["changes.md"],
                Timeout,
                ArtifactManager.BuildEnvironment([], outputDirectory, artifactsRoot),
                UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>(),
                DeliversBranch: true,
                ProducedOutputs: contract.ProducedOutputs,
                DeliveryGeneratedPaths: [],
                DeliveryAuthorizedPaths: []);

            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(request), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new CoreEvent.ExecutionStarted(executionId, Pid: 4242), TestContext.Current.CancellationToken);
            var attemptStart = TempGitRepository.Head(workspace);
            await writer.AppendAsync(new FlowEvent.ExecutionAttemptStarted(executionId, attemptStart), TestContext.Current.CancellationToken);
            using (var producer = new RuntimeDirectPullRequestProducer(workspace, outputDirectory))
            {
                await producer.ProduceAsync();
            }
            TempGitRepository.CommitAll(workspace, "deliver product change");
            TempGitRepository.Push(workspace, "origin", "restart-runtime-provenance");
            await writer.AppendAsync(
                new CoreEvent.ExecutionExited(executionId, ExitCode: 0, CoreExitReason.Natural),
                TestContext.Current.CancellationToken);

            var state = await MutationInterface.StartWorkflowAsync(
                workflowId,
                roomDirectory,
                snapshot,
                new Dictionary<string, WorkerBinding>
                {
                    ["stub-worker"] = new WorkerBinding.Process(
                        contract,
                        new CoreDispatchTarget("stub", [], WorkingDirectory: workspace),
                        Timeout,
                        DeliversBranch: true),
                },
                artifactsRoot,
                reader,
                writer,
                new StubCoreDispatcher(),
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(Assert.Single(state.Steps).IndeterminateAwaitingResolution);
            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var failure = Assert.Single(events.OfType<FlowEvent.VerifyFailed>());
            Assert.Equal(VerifyFailedKind.DeliveryFailed, failure.Kind);
            Assert.Equal(["generated-files-forbidden"], failure.FailingMembers);
            Assert.Contains("pr-body.md", failure.Tail, StringComparison.Ordinal);
            Assert.Contains("runtime direct pull-request creation body-file request", failure.Tail, StringComparison.Ordinal);
            Assert.DoesNotContain(events, e => e is FlowEvent.VerifyFailed { Kind: VerifyFailedKind.EngineRestart });
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
            DirectoryCleanup.DeleteRecursively(workspace);
            DirectoryCleanup.DeleteRecursively(origin);
        }
    }

    [Fact]
    public async Task Restart_late_failure_does_not_accept_a_malformed_recorded_artifact()
    {
        var snapshot = MakeSnapshot(Step(A, dependsOn: []));
        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var workflowId = new WorkflowId("wf-late-failure-malformed");
            var executionId = new ExecutionId(Guid.NewGuid().ToString("n"));
            var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, executionId);
            var contract = new WorkerContract(
                "unresolvable-worker", [], [new ProducedOutput("verdict.json", Schema: OutputSchema.ReviewVerdict)], []);
            var request = new ExecutionRequest(
                executionId,
                workflowId,
                A,
                "unresolvable-worker",
                Inputs: [],
                Outputs: ["verdict.json"],
                Timeout,
                ArtifactManager.BuildEnvironment([], outputDirectory, artifactsRoot),
                UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>(),
                ProducedOutputs: contract.ProducedOutputs);

            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(request), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new CoreEvent.ExecutionStarted(executionId, Pid: 4242), TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "verdict.json"), "not a verdict", TestContext.Current.CancellationToken);
            await writer.AppendAsync(
                new CoreEvent.ExecutionExited(
                    executionId,
                    ExitCode: 1,
                    CoreExitReason.Natural,
                    TerminalSuccessObserved: false,
                    TerminalResultObserved: true),
                TestContext.Current.CancellationToken);

            var state = await MutationInterface.StartWorkflowAsync(
                workflowId,
                roomDirectory,
                snapshot,
                new Dictionary<string, WorkerBinding>(),
                artifactsRoot,
                reader,
                writer,
                new StubCoreDispatcher(),
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(StepStatus.Failed, Assert.Single(state.Steps).Status);
            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Empty(events.OfType<FlowEvent.ExecutionSucceededWithLateFailure>());
            Assert.DoesNotContain(events, e => e is FlowEvent.ExecutionSucceeded);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    private static async Task<ExecutionId> AcceptRequestAsync(
        FlowEventLogWriter writer,
        WorkflowId workflowId,
        string artifactsRoot,
        StepId stepId,
        string? adapter = null,
        string? model = null,
        bool? deliversBranch = null)
    {
        var executionId = new ExecutionId(Guid.NewGuid().ToString("n"));
        var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, executionId);
        var request = new ExecutionRequest(
            executionId,
            workflowId,
            stepId,
            "stub-worker",
            Inputs: [],
            Outputs: [],
            Timeout,
            ArtifactManager.BuildEnvironment([], outputDirectory, artifactsRoot),
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>(),
            Adapter: adapter,
            Model: model,
            DeliversBranch: deliversBranch);

        await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(request));
        return executionId;
    }

    private static WorkflowStepDefinition Step(
        StepId stepId,
        IReadOnlyList<StepId> dependsOn,
        int maxAttempts = 1,
        string worker = "stub-worker") =>
        new(stepId, worker, [], [], dependsOn, new RetryPolicy(maxAttempts));

    private static WorkflowDefinitionSnapshot MakeSnapshot(params WorkflowStepDefinition[] steps) => new(
        new WorkflowDefinitionSnapshotId($"snapshot-{Guid.NewGuid():N}"),
        new WorkflowTemplateId("crash-recovery-test"),
        WorkflowTemplateVersion: 1,
        Steps: steps);

    private static Dictionary<string, WorkerBinding> MakeBindings(string? adapter = null, string? model = null) => new()
    {
        ["stub-worker"] = new WorkerBinding.Process(ProcessContract, Target, Timeout, Adapter: adapter, Model: model),
    };

    private sealed class LateCapacityClassifier : IFailureClassifier
    {
        public bool TryClassifyFailure(
            string? stderrTail,
            TimeProvider timeProvider,
            out FailureClassification? classification,
            out DateTimeOffset? retryNotBefore)
        {
            classification = stderrTail == "late capacity failure" ? FailureClassification.Retryable : null;
            retryNotBefore = null;
            return classification is not null;
        }
    }

    /// <summary>
    /// #724's title case, distinct from the absent-worker arm above: the binding is PRESENT but
    /// resolution refuses (a lazily-resolved entry whose adapter is gone, #662). Every lookup
    /// throws, the way <c>WorkerBindingResolver.ResolveLazily</c>'s entries do.
    /// </summary>
    private sealed class RefusingBindings : IReadOnlyDictionary<string, WorkerBinding>
    {
        private sealed class TestResolutionRefusal(string message) : BatonFlowException(message);

        public WorkerBinding this[string key] => throw new TestResolutionRefusal($"No adapter for '{key}'.");
        public bool TryGetValue(string key, out WorkerBinding value) => throw new TestResolutionRefusal($"No adapter for '{key}'.");
        public bool ContainsKey(string key) => true;
        public int Count => 1;
        public IEnumerable<string> Keys => ["unresolvable-worker"];
        public IEnumerable<WorkerBinding> Values => throw new TestResolutionRefusal("enumerated");
        public IEnumerator<KeyValuePair<string, WorkerBinding>> GetEnumerator() => throw new TestResolutionRefusal("enumerated");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => throw new TestResolutionRefusal("enumerated");
    }

    private static (string RoomDirectory, string ArtifactsRoot, string LogPath) MakeTaskPaths()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        return (roomDirectory, Path.Combine(roomDirectory, "artifacts"), Path.Combine(roomDirectory, "flow.jsonl"));
    }

    private static async Task<StepId> ReadNextDispatchAsync(StubCoreDispatcher stub)
    {
        var readTask = stub.DispatchStarted.ReadAsync().AsTask();
        var completed = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(60)));
        Assert.Same(readTask, completed);
        return await readTask;
    }
}
