using Baton.Artifacts;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Mutation;
using Baton.Store;
using Baton.Tests.TestSupport;
using static Baton.Tests.TestSupport.ShellWorkerCommands;

namespace Baton.Tests.Mutation;

/// <summary>
/// Issue #1359's <c>MutationInterface.RecordResumeAsync</c>: dispatching a new, linked execution
/// against an already-dispatched step's worker, and the refusals that keep it from doing so on a
/// step it should not touch (never dispatched, still running, ambiguous, or non-process). The
/// resume-shaped binding override itself (<c>ResumeSession</c>/<c>SessionId</c>/the message as
/// <c>PromptTemplate</c>) is <c>Baton.Cli.ResumeCommand</c>'s job — exercised at that layer by
/// <c>ResumeCommandEndToEndTests</c> — so these tests pass a plain <see cref="WorkerBinding.Process"/>
/// directly, the same way <see cref="MutationInterfaceCrashRecoveryTests"/> does for its own
/// mechanics-only coverage.
/// </summary>
public class MutationInterfaceResumeTests
{
    private static readonly StepId Solo = new("solo");
    private static readonly WorkerContract Contract = new("solo-worker", [], [new ProducedOutput("plan")], []);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task RecordResumeAsync_dispatches_a_new_execution_linked_to_the_steps_prior_latest()
    {
        var snapshot = MakeSnapshot(Step(Solo, worker: "solo-worker"));
        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["solo-worker"] = new WorkerBinding.Process(Contract, WriteFile("plan", "first"), Timeout),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);
            var workflowId = new WorkflowId("wf-resume");

            var firstState = await MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher,
                cancellationToken: TestContext.Current.CancellationToken);
            var firstExecutionId = firstState.Steps.Single().LatestExecutionId!.Value;
            Assert.Equal(StepStatus.Succeeded, firstState.Steps.Single().Status);
            Assert.Null(firstState.Steps.Single().LinkedFromExecutionId);

            var (resumedState, resumedExecutionId) = await MutationInterface.RecordResumeAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, "solo-worker",
                reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            var resumedStep = resumedState.Steps.Single();
            Assert.Equal(StepStatus.Succeeded, resumedStep.Status);
            Assert.Equal(resumedExecutionId, resumedStep.LatestExecutionId);
            Assert.NotEqual(firstExecutionId, resumedExecutionId);
            Assert.Equal(firstExecutionId, resumedStep.LinkedFromExecutionId);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task RecordResumeAsync_captures_effective_limit_evidence_on_accepted_request()
    {
        var snapshot = MakeSnapshot(Step(Solo, worker: "solo-worker"));
        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            var expectedLimits = new ExecutionLimitEvidence(
                Timeout,
                TokenBudget: 5000,
                MaxToolSteps: 25,
                BilledRateLimit: 1200,
                ChosenKey: "solo/model/profile",
                TimeoutSource: "profile",
                TokenBudgetSource: "profile",
                MaxToolStepsSource: "profile",
                MonitorInputsKnown: true);
            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["solo-worker"] = new WorkerBinding.Process(
                    Contract,
                    WriteFile("plan", "first"),
                    Timeout,
                    TokenBudget: 5000,
                    MaxToolSteps: 25,
                    BilledRateLimit: 1200,
                    Adapter: "claude",
                    LimitEvidence: expectedLimits),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);
            var workflowId = new WorkflowId("wf-resume-limits");

            await MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher,
                cancellationToken: TestContext.Current.CancellationToken);

            var (resumedState, resumedExecutionId) = await MutationInterface.RecordResumeAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, "solo-worker",
                reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var acceptedRequests = events.OfType<FlowEvent.ExecutionRequestAccepted>().ToList();
            Assert.Equal(2, acceptedRequests.Count);

            var resumedAccept = Assert.Single(acceptedRequests, a => a.Request.ExecutionId == resumedExecutionId);
            Assert.Equal(expectedLimits, resumedAccept.Request.Limits);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task RecordResumeAsync_dispatches_a_linked_execution_for_a_Paused_step()
    {
        // #1388 review F10: a Paused step is the other resume target InvalidResumeException's own
        // doc names (alongside terminal) -- previously untested. Paused is neither Pending nor
        // Running, so RecordResumeAsync's own checks must let it through.
        var snapshot = MakeSnapshot(Step(Solo, worker: "solo-worker", pausePoint: new PausePoint([])));
        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["solo-worker"] = new WorkerBinding.Process(Contract, WriteFile("plan", "first"), Timeout),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);
            var workflowId = new WorkflowId("wf-resume-paused");

            var firstState = await MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher,
                cancellationToken: TestContext.Current.CancellationToken);
            var firstExecutionId = firstState.Steps.Single().LatestExecutionId!.Value;
            Assert.Equal(StepStatus.Paused, firstState.Steps.Single().Status);

            var (resumedState, resumedExecutionId) = await MutationInterface.RecordResumeAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, "solo-worker",
                reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            var resumedStep = resumedState.Steps.Single();
            Assert.Equal(resumedExecutionId, resumedStep.LatestExecutionId);
            Assert.NotEqual(firstExecutionId, resumedExecutionId);
            Assert.Equal(firstExecutionId, resumedStep.LinkedFromExecutionId);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task RecordResumeAsync_refuses_when_no_step_names_the_worker()
    {
        var snapshot = MakeSnapshot(Step(Solo, worker: "solo-worker"));
        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["solo-worker"] = new WorkerBinding.Process(Contract, WriteFile("plan", "x"), Timeout),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);

            await Assert.ThrowsAsync<InvalidResumeException>(() => MutationInterface.RecordResumeAsync(
                new WorkflowId("wf"), roomDirectory, snapshot, bindings, artifactsRoot, "no-such-worker",
                reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task RecordResumeAsync_refuses_an_ambiguous_worker_bound_to_more_than_one_step()
    {
        var stepA = new StepId("a");
        var stepB = new StepId("b");
        var snapshot = MakeSnapshot(Step(stepA, worker: "shared-worker"), Step(stepB, worker: "shared-worker"));
        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["shared-worker"] = new WorkerBinding.Process(Contract, WriteFile("plan", "x"), Timeout),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);

            await Assert.ThrowsAsync<InvalidResumeException>(() => MutationInterface.RecordResumeAsync(
                new WorkflowId("wf"), roomDirectory, snapshot, bindings, artifactsRoot, "shared-worker",
                reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task RecordResumeAsync_refuses_a_step_that_has_never_run()
    {
        var snapshot = MakeSnapshot(Step(Solo, worker: "solo-worker"));
        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["solo-worker"] = new WorkerBinding.Process(Contract, WriteFile("plan", "x"), Timeout),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);

            // No prior events at all -- the step projects as Pending.
            await Assert.ThrowsAsync<InvalidResumeException>(() => MutationInterface.RecordResumeAsync(
                new WorkflowId("wf"), roomDirectory, snapshot, bindings, artifactsRoot, "solo-worker",
                reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task RecordResumeAsync_refuses_a_step_whose_latest_attempt_is_still_running()
    {
        var snapshot = MakeSnapshot(Step(Solo, worker: "solo-worker"));
        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["solo-worker"] = new WorkerBinding.Process(Contract, WriteFile("plan", "x"), Timeout),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);
            var workflowId = new WorkflowId("wf");

            // A request accepted with no terminal event: the same shape a genuinely-live dispatch
            // and a crashed-mid-flight one are indistinguishable -- both project Running.
            var executionId = new ExecutionId(Guid.NewGuid().ToString("n"));
            var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, executionId);
            var request = new ExecutionRequest(
                executionId, workflowId, Solo, "solo-worker", Inputs: [], Outputs: [], Timeout,
                ArtifactManager.BuildEnvironment([], outputDirectory, artifactsRoot),
                UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());
            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(request), TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<InvalidResumeException>(() => MutationInterface.RecordResumeAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, "solo-worker",
                reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task RecordResumeAsync_refuses_a_non_process_binding()
    {
        var snapshot = MakeSnapshot(Step(Solo, worker: "human"));
        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["human"] = new WorkerBinding.Process(Contract, WriteFile("plan", "x"), Timeout),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);
            var workflowId = new WorkflowId("wf");

            var firstState = await MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(StepStatus.Succeeded, firstState.Steps.Single().Status);

            // Only NOW swap in a NonProcess binding for the resume call -- a worker with a session to
            // resume must be a Process binding; nothing here should ever reach a live dispatch.
            var nonProcessBindings = new Dictionary<string, WorkerBinding>
            {
                ["human"] = new WorkerBinding.NonProcess(Contract),
            };

            await Assert.ThrowsAsync<InvalidResumeException>(() => MutationInterface.RecordResumeAsync(
                workflowId, roomDirectory, snapshot, nonProcessBindings, artifactsRoot, "human",
                reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task A_resume_of_a_resume_refuses_when_the_bindings_files_SessionId_disagrees_with_the_recorded_one()
    {
        // Issue #1359 F6: once a resume itself records a session id, a LATER resume of that same
        // execution must be refused if the bindings file's SessionId no longer matches -- an
        // operator edit (or a re-run of `baton dispatch` rewriting bindings.json) must not silently
        // record a continuity the ledger's own history contradicts.
        var snapshot = MakeSnapshot(Step(Solo, worker: "solo-worker"));
        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["solo-worker"] = new WorkerBinding.Process(Contract, WriteFile("plan", "first"), Timeout),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);
            var workflowId = new WorkflowId("wf-resume-session");

            await MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher,
                cancellationToken: TestContext.Current.CancellationToken);

            // First resume records session "sess-1".
            await MutationInterface.RecordResumeAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, "solo-worker",
                reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken,
                sessionId: "sess-1");

            // A resume-of-that-resume naming a DIFFERENT session id refuses.
            var thrown = await Assert.ThrowsAsync<InvalidResumeException>(() => MutationInterface.RecordResumeAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, "solo-worker",
                reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken,
                sessionId: "sess-2"));

            Assert.Contains("sess-1", thrown.Message, StringComparison.Ordinal);
            Assert.Contains("sess-2", thrown.Message, StringComparison.Ordinal);
            Assert.NotNull(thrown.TryInvocation);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task A_resume_of_a_resume_proceeds_when_the_bindings_files_SessionId_still_agrees()
    {
        // Polarity partner of the refusal above -- the SAME recorded session id, one resume later,
        // must not be refused.
        var snapshot = MakeSnapshot(Step(Solo, worker: "solo-worker"));
        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["solo-worker"] = new WorkerBinding.Process(Contract, WriteFile("plan", "first"), Timeout),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);
            var workflowId = new WorkflowId("wf-resume-session-agree");

            await MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher,
                cancellationToken: TestContext.Current.CancellationToken);

            await MutationInterface.RecordResumeAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, "solo-worker",
                reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken,
                sessionId: "sess-1");

            var (secondResumeState, _) = await MutationInterface.RecordResumeAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, "solo-worker",
                reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken,
                sessionId: "sess-1");

            Assert.Equal(StepStatus.Succeeded, secondResumeState.Steps.Single().Status);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task A_failed_resume_is_never_auto_retried_by_the_settling_pump_and_its_link_survives()
    {
        // Issue #1359 F4: the settling pump must never spend a resume's own step against
        // MaxAttempts, however much budget remains, and LinkedFromExecutionId must still point at
        // the ORIGINAL execution afterward, not get cleared by a retry-minted request.
        var snapshot = MakeSnapshot(Step(Solo, worker: "solo-worker", maxAttempts: 3));
        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["solo-worker"] = new WorkerBinding.Process(Contract, WriteFile("plan", "first"), Timeout),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);
            var workflowId = new WorkflowId("wf-resume-retry");

            var firstState = await MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher,
                cancellationToken: TestContext.Current.CancellationToken);
            var firstExecutionId = firstState.Steps.Single().LatestExecutionId!.Value;
            Assert.Equal(StepStatus.Succeeded, firstState.Steps.Single().Status);

            // The resumed attempt exits cleanly but never writes the contract's required "plan"
            // output -- an ordinary Retryable contract failure, the shape a real vendor timeout or
            // a no-op follow-up would also produce.
            var failingBindings = new Dictionary<string, WorkerBinding>
            {
                ["solo-worker"] = new WorkerBinding.Process(Contract, ExitCleanlyWithoutWriting(), Timeout),
            };

            var (resumedState, resumedExecutionId) = await MutationInterface.RecordResumeAsync(
                workflowId, roomDirectory, snapshot, failingBindings, artifactsRoot, "solo-worker",
                reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            var resumedStep = resumedState.Steps.Single();
            Assert.Equal(StepStatus.Failed, resumedStep.Status);
            Assert.Equal(firstExecutionId, resumedStep.LinkedFromExecutionId);

            // The settling pump this command runs next (ResumeCommand's own two-call sequence) must
            // not auto-dispatch a further attempt, even though MaxAttempts(3) leaves budget.
            var settledState = await MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, failingBindings, artifactsRoot, reader, writer, dispatcher,
                cancellationToken: TestContext.Current.CancellationToken);

            var settledStep = settledState.Steps.Single();
            Assert.Equal(StepStatus.Failed, settledStep.Status);
            Assert.Equal(resumedExecutionId, settledStep.LatestExecutionId);
            Assert.Equal(firstExecutionId, settledStep.LinkedFromExecutionId);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task RecordResumeAsync_arms_the_replay_canary_from_its_own_recorded_request_when_todays_binding_refuses_to_resolve()
    {
        // #1753 review F1: RecordResumeAsync builds its OWN ExecutionRequest -- a second, distinct
        // Process-dispatch site from PrepareExecutionAsync's, which #1741's fix originally covered.
        // This pins that a `baton resume` dispatch also journals HookCanaryArmed/
        // HookVerdictLedgerFileName from the resolved binding (spec/baton.md §9), so a resumed
        // execution that crashes before its outcome is recorded, whose binding then refuses to
        // resolve at replay, still arms the canary from the recorded request instead of fail-open
        // Succeeded -- the same #1741 bug, reopened on this second site.
        var snapshot = MakeSnapshot(Step(Solo, worker: "solo-worker"));
        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["solo-worker"] = new WorkerBinding.Process(Contract, WriteFile("plan", "first"), Timeout),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);
            var workflowId = new WorkflowId("wf-resume-canary");

            var firstState = await MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(StepStatus.Succeeded, firstState.Steps.Single().Status);

            // The resume's binding is armed for agy sole-hook narrowing (a live CountHookVerdicts
            // delegate) -- what AgyWorkerAdapter.Resolve wires up for that shape.
            var armedTarget = WriteFile("plan", "resumed") with
            {
                CountHookVerdicts = _ => 0,
                HookVerdictLedgerFileName = "verdicts.ndjson",
            };
            var armedBindings = new Dictionary<string, WorkerBinding>
            {
                ["solo-worker"] = new WorkerBinding.Process(Contract, armedTarget, Timeout, Adapter: "agy"),
            };

            // Simulates the real crash window: Core durably records the start and exit, then the
            // engine dies -- an uncaught exception, mirroring DispatchAndRecordOutcomeAsync's own "no
            // local catch" contract -- before Flow ever appends the outcome event.
            var crashingDispatcher = new CrashAfterCoreExitDispatcher(writer, artifactsRoot);

            await Assert.ThrowsAsync<InvalidOperationException>(() => MutationInterface.RecordResumeAsync(
                workflowId, roomDirectory, snapshot, armedBindings, artifactsRoot, "solo-worker",
                reader, writer, crashingDispatcher, cancellationToken: TestContext.Current.CancellationToken));

            var resumedExecutionId = crashingDispatcher.LastExecutionId!.Value;

            // The fix, checked directly: the journaled request already carries the arming fact, not
            // the null/null the pre-fix RecordResumeAsync left it at.
            var eventsAfterCrash = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var resumedRequest = eventsAfterCrash.OfType<FlowEvent.ExecutionRequestAccepted>()
                .Single(e => e.Request.ExecutionId == resumedExecutionId).Request;
            Assert.True(resumedRequest.HookCanaryArmed);
            Assert.Equal("verdicts.ndjson", resumedRequest.HookVerdictLedgerFileName);

            // Replay against a binding that now REFUSES to resolve (#710 shape). Before the fix,
            // request.HookCanaryArmed was null, so the replay fell back to re-deriving from today's
            // binding, found nothing to derive from (the catch at MutationInterface.cs ~1085), and
            // settled Succeeded -- the exact fail-open #1741 closed, reopened for this site. After the
            // fix it arms from the recorded request and settles Indeterminate instead.
            var replayedState = await MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, new RefusingSoloWorkerBindings(), artifactsRoot, reader, writer,
                new StubCoreDispatcher(), cancellationToken: TestContext.Current.CancellationToken);

            var resumedStep = replayedState.Steps.Single();
            Assert.True(resumedStep.IndeterminateAwaitingResolution);
            var finalEvents = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain(finalEvents.OfType<FlowEvent.ExecutionSucceeded>(), e => e.ExecutionId == resumedExecutionId);
            Assert.Single(finalEvents, e => e is FlowEvent.ExecutionIndeterminate ei && ei.ExecutionId == resumedExecutionId);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>
    /// A dispatcher double standing in for a real Core process for the F1 crash test above: it
    /// writes exactly the files and Core-half events a real agy dispatch that then crashed would
    /// have left durable (a stdout log with one tool call, an empty verdict ledger, a recorded
    /// start and exit), then throws instead of returning -- Core recorded the exit; Flow never got
    /// to append the outcome.
    /// </summary>
    private sealed class CrashAfterCoreExitDispatcher(ICoreEventLogWriter coreEventLogWriter, string artifactsRootPath) : ICoreDispatcher
    {
        public ExecutionId? LastExecutionId { get; private set; }

        public async Task<CoreDispatchResult> DispatchAsync(
            ExecutionRequest request, CoreDispatchTarget target, CancellationToken cancellationToken = default)
        {
            LastExecutionId = request.ExecutionId;
            var outputDirectory = ArtifactManager.ResolveOutputDirectory(artifactsRootPath, request.ExecutionId);
            File.WriteAllText(
                Path.Combine(outputDirectory, ExecutionStreamLogger.StdoutLogFileName),
                """{"event":"step_update","step_update":{"step_type":"tool","tool_name":"run_command","state":"DONE"}}""" + "\n");
            File.WriteAllText(Path.Combine(outputDirectory, "verdicts.ndjson"), string.Empty);

            await coreEventLogWriter.AppendAsync(new CoreEvent.ExecutionStarted(request.ExecutionId, Pid: 4242), cancellationToken)
                .ConfigureAwait(false);
            await coreEventLogWriter.AppendAsync(
                    new CoreEvent.ExecutionExited(request.ExecutionId, ExitCode: 0, CoreExitReason.Natural), cancellationToken)
                .ConfigureAwait(false);

            throw new InvalidOperationException("Simulated engine crash after Core durably recorded the exit.");
        }
    }

    /// <summary>
    /// The #710 shape: the binding is present but resolution refuses, mirroring
    /// <c>MutationInterfaceCrashRecoveryTests.RefusingBindings</c> but keyed to this file's own
    /// "solo-worker" so the F1 crash test above can replay against it.
    /// </summary>
    private sealed class RefusingSoloWorkerBindings : IReadOnlyDictionary<string, WorkerBinding>
    {
        private sealed class TestResolutionRefusal(string message) : BatonFlowException(message);

        public WorkerBinding this[string key] => throw new TestResolutionRefusal($"No adapter for '{key}'.");
        public bool TryGetValue(string key, out WorkerBinding value) => throw new TestResolutionRefusal($"No adapter for '{key}'.");
        public bool ContainsKey(string key) => true;
        public int Count => 1;
        public IEnumerable<string> Keys => ["solo-worker"];
        public IEnumerable<WorkerBinding> Values => throw new TestResolutionRefusal("enumerated");
        public IEnumerator<KeyValuePair<string, WorkerBinding>> GetEnumerator() => throw new TestResolutionRefusal("enumerated");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => throw new TestResolutionRefusal("enumerated");
    }

    private static WorkflowStepDefinition Step(StepId stepId, string worker, int maxAttempts = 1, PausePoint? pausePoint = null) =>
        new(stepId, worker, [], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(maxAttempts), PausePoint: pausePoint);

    private static WorkflowDefinitionSnapshot MakeSnapshot(params WorkflowStepDefinition[] steps) => new(
        new WorkflowDefinitionSnapshotId($"snapshot-{Guid.NewGuid():N}"),
        new WorkflowTemplateId("resume-test"),
        WorkflowTemplateVersion: 1,
        Steps: steps);

    private static (string RoomDirectory, string ArtifactsRoot, string LogPath) MakeTaskPaths()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-resume-{Guid.NewGuid():N}");
        return (roomDirectory, Path.Combine(roomDirectory, "artifacts"), Path.Combine(roomDirectory, "flow.jsonl"));
    }
}
