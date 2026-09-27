using Microsoft.Extensions.Time.Testing;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Mutation;
using Baton.Store;
using static Baton.Tests.TestSupport.ShellWorkerCommands;

namespace Baton.Tests.Mutation;

/// <summary>
/// #802 (S6, the ratified quota design): a step parked on <see cref="FailureClassification.ExhaustedUntil"/>
/// with a declared fallback binding rebinds and redispatches immediately, rather than waiting out the
/// primary vendor's own reset instant. Mirrors <c>QuotaParkCancelArrestTests</c>' fabricated-park-history
/// fixture shape — a first attempt failed ExhaustedUntil, with a far-future reset that this file's own
/// clock (a <see cref="FakeTimeProvider"/>, never advanced) proves nothing here waited out.
/// </summary>
public class VendorExhaustionFallbackTests
{
    private static readonly StepId StepA = new("step-a");
    private static readonly TimeSpan PumpCompletionTimeout = TimeSpan.FromSeconds(30);

    private static WorkflowDefinitionSnapshot SingleStepSnapshot(string snapshotId, string templateId, int maxAttempts = 3) => new(
        new WorkflowDefinitionSnapshotId(snapshotId),
        new WorkflowTemplateId(templateId),
        WorkflowTemplateVersion: 1,
        Steps:
        [
            new WorkflowStepDefinition(
                StepA,
                "worker-a",
                Inputs: [],
                Outputs: [],
                DependsOn: [],
                RetryPolicy: new RetryPolicy(MaxAttempts: maxAttempts, Backoff: BackoffPolicy.Steady))
        ]);

    [Fact]
    public async Task Declared_fallback_redispatches_within_one_pump_round_without_advancing_the_clock()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var now = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
        var fakeTime = new FakeTimeProvider(now);
        var farFutureReset = now.AddDays(1);

        try
        {
            var snapshot = SingleStepSnapshot("snapshot-802a", "template-802a");

            var primaryBinding = new WorkerBinding.Process(
                new WorkerContract("worker-a", [], [], []),
                ExitCleanlyWithoutWriting(),
                TimeSpan.FromSeconds(30),
                Adapter: "agy",
                Model: "gemini-3-pro",
                TokenBudget: 1000,
                MaxToolSteps: 10,
                BilledRateLimit: 100);
            var primaryBindings = new Dictionary<string, WorkerBinding>
            {
                ["worker-a"] = primaryBinding,
            };
            var fallbackBinding = new WorkerBinding.Process(
                new WorkerContract("worker-a", [], [], []),
                ExitCleanlyWithoutWriting(),
                TimeSpan.FromSeconds(45),
                Adapter: "claude",
                Model: "sonnet",
                TokenBudget: 2000,
                MaxToolSteps: 20,
                BilledRateLimit: 200);
            var fallbackBindings = new Dictionary<string, WorkerBinding>
            {
                ["worker-a"] = fallbackBinding,
            };

            var firstAttempt = new ExecutionId("a-1");
            await using (var writerInit = new FlowEventLogWriter(logPath))
            {
                var ct = TestContext.Current.CancellationToken;
                await writerInit.AppendAsync(new FlowEvent.ExecutionRequestAccepted(
                    new ExecutionRequest(
                        firstAttempt, new WorkflowId("wf-802a"), StepA, "worker-a", [], [], TimeSpan.FromSeconds(30), [],
                        new Dictionary<StepId, ExecutionId>(), Adapter: "agy", Model: "gemini-3-pro",
                        Limits: primaryBinding.EffectiveLimitEvidence)), ct);
                await writerInit.AppendAsync(new FlowEvent.ExecutionFailed(
                    firstAttempt, FailureClassification.ExhaustedUntil, "quota exhausted", farFutureReset), ct);
            }

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);

            // fakeTime is NEVER advanced: if the fallback redispatch instead waited out
            // farFutureReset (a day out), this would hang until WaitAsync's own timeout below.
            var finalState = await MutationInterface.StartWorkflowAsync(
                    new WorkflowId("wf-802a"),
                    roomDirectory,
                    snapshot,
                    primaryBindings,
                    artifactsRoot,
                    reader,
                    writer,
                    dispatcher,
                    timeProvider: fakeTime,
                    jitterSource: () => 0.0,
                    cancellationToken: TestContext.Current.CancellationToken,
                    fallbackWorkerBindings: fallbackBindings)
                .WaitAsync(PumpCompletionTimeout, TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, finalState.Status);
            var stepState = finalState.Steps.Single(s => s.StepId == StepA);
            Assert.Equal(StepStatus.Succeeded, stepState.Status);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);

            // The redispatch used the fallback binding, not the primary's.
            var accepted = events.OfType<FlowEvent.ExecutionRequestAccepted>().ToList();
            Assert.Equal(2, accepted.Count);
            Assert.Equal("agy", accepted[0].Request.Adapter);
            Assert.Equal("claude", accepted[1].Request.Adapter);
            Assert.Equal("sonnet", accepted[1].Request.Model);
            Assert.Equal(primaryBinding.EffectiveLimitEvidence, accepted[0].Request.Limits);
            Assert.Equal(fallbackBinding.EffectiveLimitEvidence, accepted[1].Request.Limits);

            // The StepRebound journal line PrepareExecutionAsync's dispatch-loop caller appends.
            var rebound = Assert.Single(events.OfType<FlowEvent.StepRebound>());
            Assert.Equal(StepA, rebound.StepId);
            Assert.Equal(accepted[1].Request.ExecutionId, rebound.ForExecutionId);
            Assert.Equal("agy", rebound.PreviousAdapter);
            Assert.Equal("gemini-3-pro", rebound.PreviousModel);
            Assert.Equal("claude", rebound.NewAdapter);
            Assert.Equal("sonnet", rebound.NewModel);
            Assert.Equal(primaryBinding.EffectiveLimitEvidence, rebound.PreviousLimits);
            Assert.Equal(fallbackBinding.EffectiveLimitEvidence, rebound.NewLimits);
            Assert.Contains(farFutureReset.ToString("O"), rebound.Reason, StringComparison.Ordinal);

            // The retry/attempt counters treat the fallback dispatch as a new attempt of the same
            // step (#802's own ruling 3) -- ExecutionCount reflects both attempts.
            Assert.Equal(2, stepState.ExecutionCount);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task No_declared_fallback_keeps_the_step_parked_on_the_primary_vendors_reset()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var now = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
        var fakeTime = new FakeTimeProvider(now);
        var farFutureReset = now.AddDays(1);

        try
        {
            var snapshot = SingleStepSnapshot("snapshot-802b", "template-802b");
            var primaryBindings = new Dictionary<string, WorkerBinding>
            {
                ["worker-a"] = new WorkerBinding.Process(
                    new WorkerContract("worker-a", [], [], []),
                    ExitCleanlyWithoutWriting(),
                    TimeSpan.FromSeconds(30),
                    Adapter: "agy"),
            };

            var firstAttempt = new ExecutionId("a-1");
            await using (var writerInit = new FlowEventLogWriter(logPath))
            {
                var ct = TestContext.Current.CancellationToken;
                await writerInit.AppendAsync(new FlowEvent.ExecutionRequestAccepted(
                    new ExecutionRequest(
                        firstAttempt, new WorkflowId("wf-802b"), StepA, "worker-a", [], [], TimeSpan.FromSeconds(30), [],
                        new Dictionary<StepId, ExecutionId>(), Adapter: "agy")), ct);
                await writerInit.AppendAsync(new FlowEvent.ExecutionFailed(
                    firstAttempt, FailureClassification.ExhaustedUntil, "quota exhausted", farFutureReset), ct);
                await writerInit.AppendAsync(new FlowEvent.StepRetryScheduled(
                    StepA, firstAttempt, farFutureReset, RetryDelayMs: (int)TimeSpan.FromDays(1).TotalMilliseconds), ct);
            }

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);
            var registry = new InFlightExecutionRegistry();

            // No fallbackWorkerBindings supplied -- today's behaviour: the step paces to
            // farFutureReset. Proved without waiting a day out, the same way
            // QuotaParkCancelArrestTests proves it: mark an arrest intent, which only a genuine
            // (still-active) park can settle via ExecutionCancelled instead of a second dispatch.
            var pumpTask = MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-802b"),
                roomDirectory,
                snapshot,
                primaryBindings,
                artifactsRoot,
                reader,
                writer,
                dispatcher,
                inFlightExecutions: registry,
                timeProvider: fakeTime,
                jitterSource: () => 0.0,
                cancellationToken: TestContext.Current.CancellationToken,
                fallbackWorkerBindings: null);

            registry.MarkArrestIntent(firstAttempt, "test: no fallback declared, still parked");

            var finalState = await pumpTask.WaitAsync(PumpCompletionTimeout, TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, finalState.Status);
            Assert.Equal(StepStatus.Cancelled, finalState.Steps.Single(s => s.StepId == StepA).Status);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            // No redispatch, and no StepRebound -- the park was never rescued.
            Assert.Single(events.OfType<FlowEvent.ExecutionRequestAccepted>());
            Assert.Empty(events.OfType<FlowEvent.StepRebound>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task An_exhaustion_with_no_known_reset_instant_still_rescues_via_a_declared_fallback()
    {
        // 0026 §5's "no obligation at all" rule (a claude-shaped quota hit, no reset instant) is
        // rescued by a declared fallback the same way a known-instant park is -- a fallback needs no
        // reset instant to pace against, since it does not wait at all.
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var now = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
        var fakeTime = new FakeTimeProvider(now);

        try
        {
            var snapshot = SingleStepSnapshot("snapshot-802c", "template-802c");
            var primaryBindings = new Dictionary<string, WorkerBinding>
            {
                ["worker-a"] = new WorkerBinding.Process(
                    new WorkerContract("worker-a", [], [], []), ExitCleanlyWithoutWriting(), TimeSpan.FromSeconds(30), Adapter: "claude"),
            };
            var fallbackBindings = new Dictionary<string, WorkerBinding>
            {
                ["worker-a"] = new WorkerBinding.Process(
                    new WorkerContract("worker-a", [], [], []), ExitCleanlyWithoutWriting(), TimeSpan.FromSeconds(30), Adapter: "agy"),
            };

            var firstAttempt = new ExecutionId("a-1");
            await using (var writerInit = new FlowEventLogWriter(logPath))
            {
                var ct = TestContext.Current.CancellationToken;
                await writerInit.AppendAsync(new FlowEvent.ExecutionRequestAccepted(
                    new ExecutionRequest(
                        firstAttempt, new WorkflowId("wf-802c"), StepA, "worker-a", [], [], TimeSpan.FromSeconds(30), [],
                        new Dictionary<StepId, ExecutionId>(), Adapter: "claude")), ct);
                // No reset instant -- claude's own shape (ClaudeWorkerAdapter never sets one).
                await writerInit.AppendAsync(new FlowEvent.ExecutionFailed(
                    firstAttempt, FailureClassification.ExhaustedUntil, "credits_required", RetryNotBefore: null), ct);
            }

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);

            var finalState = await MutationInterface.StartWorkflowAsync(
                    new WorkflowId("wf-802c"),
                    roomDirectory,
                    snapshot,
                    primaryBindings,
                    artifactsRoot,
                    reader,
                    writer,
                    dispatcher,
                    timeProvider: fakeTime,
                    jitterSource: () => 0.0,
                    cancellationToken: TestContext.Current.CancellationToken,
                    fallbackWorkerBindings: fallbackBindings)
                .WaitAsync(PumpCompletionTimeout, TestContext.Current.CancellationToken);

            Assert.Equal(StepStatus.Succeeded, finalState.Steps.Single(s => s.StepId == StepA).Status);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var accepted = events.OfType<FlowEvent.ExecutionRequestAccepted>().ToList();
            Assert.Equal(2, accepted.Count);
            Assert.Equal("agy", accepted[1].Request.Adapter);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task A_second_exhaustion_on_the_fallback_itself_does_not_chain_a_further_fallback()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var now = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
        var fakeTime = new FakeTimeProvider(now);
        var farFutureReset = now.AddDays(1);

        try
        {
            var snapshot = SingleStepSnapshot("snapshot-802d", "template-802d");
            var primaryBindings = new Dictionary<string, WorkerBinding>
            {
                ["worker-a"] = new WorkerBinding.Process(
                    new WorkerContract("worker-a", [], [], []), ExitCleanlyWithoutWriting(), TimeSpan.FromSeconds(30), Adapter: "agy"),
            };
            var fallbackBindings = new Dictionary<string, WorkerBinding>
            {
                ["worker-a"] = new WorkerBinding.Process(
                    new WorkerContract("worker-a", [], [], []), ExitCleanlyWithoutWriting(), TimeSpan.FromSeconds(30), Adapter: "claude"),
            };

            // The step's LATEST execution already ran on the fallback ("claude") and parked again --
            // simulating the round after a first fallback rebind already happened.
            var firstAttempt = new ExecutionId("a-1");
            var fallbackAttempt = new ExecutionId("a-2");
            await using (var writerInit = new FlowEventLogWriter(logPath))
            {
                var ct = TestContext.Current.CancellationToken;
                await writerInit.AppendAsync(new FlowEvent.ExecutionRequestAccepted(
                    new ExecutionRequest(
                        firstAttempt, new WorkflowId("wf-802d"), StepA, "worker-a", [], [], TimeSpan.FromSeconds(30), [],
                        new Dictionary<StepId, ExecutionId>(), Adapter: "agy")), ct);
                await writerInit.AppendAsync(new FlowEvent.ExecutionFailed(
                    firstAttempt, FailureClassification.ExhaustedUntil, "quota exhausted", farFutureReset), ct);
                await writerInit.AppendAsync(new FlowEvent.ExecutionRequestAccepted(
                    new ExecutionRequest(
                        fallbackAttempt, new WorkflowId("wf-802d"), StepA, "worker-a", [], [], TimeSpan.FromSeconds(30), [],
                        new Dictionary<StepId, ExecutionId>(), Adapter: "claude")), ct);
                await writerInit.AppendAsync(new FlowEvent.ExecutionFailed(
                    fallbackAttempt, FailureClassification.ExhaustedUntil, "quota exhausted", farFutureReset), ct);
                await writerInit.AppendAsync(new FlowEvent.StepRetryScheduled(
                    StepA, fallbackAttempt, farFutureReset, RetryDelayMs: (int)TimeSpan.FromDays(1).TotalMilliseconds), ct);
            }

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);
            var registry = new InFlightExecutionRegistry();

            var pumpTask = MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-802d"),
                roomDirectory,
                snapshot,
                primaryBindings,
                artifactsRoot,
                reader,
                writer,
                dispatcher,
                inFlightExecutions: registry,
                timeProvider: fakeTime,
                jitterSource: () => 0.0,
                cancellationToken: TestContext.Current.CancellationToken,
                fallbackWorkerBindings: fallbackBindings);

            // If this parked normally (no chained fallback), an arrest intent settles it via
            // ExecutionCancelled -- the same discriminator the "no fallback declared" test uses.
            registry.MarkArrestIntent(fallbackAttempt, "test: fallback itself exhausted, no further hop");

            var finalState = await pumpTask.WaitAsync(PumpCompletionTimeout, TestContext.Current.CancellationToken);

            Assert.Equal(StepStatus.Cancelled, finalState.Steps.Single(s => s.StepId == StepA).Status);
            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            // Only the two fabricated attempts -- no third (chained) dispatch.
            Assert.Equal(2, events.OfType<FlowEvent.ExecutionRequestAccepted>().Count());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }
}
