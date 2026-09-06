using Baton.Dispatch;
using Baton.Domain;
using Baton.Mutation;
using Baton.Outcomes;
using Baton.Projection;
using Baton.Store;
using Baton.Tests.Shared;

namespace Baton.Tests.Projection;

[Collection(ConsoleErrorCaptureCollection.Name)]
public class ProjectionCheckpointTests
{
    private static readonly StepId Step1 = new("step1");
    private static readonly StepId Step2 = new("step2");
    private static readonly StepId Step3 = new("step3");
    private static readonly StepId Step4 = new("step4");
    private static readonly StepId Step5 = new("step5");
    private static readonly StepId Step6 = new("step6");
    private static readonly StepId Step7 = new("step7");
    private static readonly StepId Step8 = new("step8");

    private static WorkflowDefinitionSnapshot TestSnapshot() => new(
        new WorkflowDefinitionSnapshotId("snapshot-checkpoint-test"),
        new WorkflowTemplateId("template-1"),
        WorkflowTemplateVersion: 1,
        Steps:
        [
            new WorkflowStepDefinition(Step1, "worker1", [], ["output1"], DependsOn: [], RetryPolicy: new RetryPolicy(2)),
            new WorkflowStepDefinition(Step2, "worker2", ["output1"], ["output2"], DependsOn: [Step1], RetryPolicy: new RetryPolicy(2)),
        ]);

    private static WorkflowDefinitionSnapshot ComprehensiveTestSnapshot() => new(
        new WorkflowDefinitionSnapshotId("snapshot-comprehensive-checkpoint-test"),
        new WorkflowTemplateId("template-comprehensive"),
        WorkflowTemplateVersion: 1,
        Steps:
        [
            new WorkflowStepDefinition(Step1, "worker1", [], ["output1"], DependsOn: [], RetryPolicy: new RetryPolicy(2)),
            new WorkflowStepDefinition(Step2, "worker2", ["output1"], ["output2"], DependsOn: [Step1], RetryPolicy: new RetryPolicy(2)),
            new WorkflowStepDefinition(Step3, "worker3", [], [], DependsOn: [], RetryPolicy: new RetryPolicy(2)),
            new WorkflowStepDefinition(Step4, "worker4", [], [], DependsOn: [], RetryPolicy: new RetryPolicy(2)),
            new WorkflowStepDefinition(Step5, "worker5", [], [], DependsOn: [], RetryPolicy: new RetryPolicy(2)),
            new WorkflowStepDefinition(Step6, "worker6", [], [], DependsOn: [], RetryPolicy: new RetryPolicy(2)),
            new WorkflowStepDefinition(Step7, "worker7", [], [], DependsOn: [], RetryPolicy: new RetryPolicy(2)),
            new WorkflowStepDefinition(Step8, "worker8", [], [], DependsOn: [], RetryPolicy: new RetryPolicy(2)),
        ]);

    private static ExecutionRequest MakeRequest(
        ExecutionId executionId,
        StepId? stepId,
        Dictionary<StepId, ExecutionId>? upstreamExecutionIds = null) => new(
        executionId,
        new WorkflowId("wf-1"),
        stepId,
        "worker",
        Inputs: [],
        Outputs: [],
        Timeout: TimeSpan.FromMinutes(10),
        Environment: [],
        UpstreamExecutionIds: upstreamExecutionIds ?? new Dictionary<StepId, ExecutionId>());

    private static void AssertFlowStateEqual(FlowState expected, FlowState actual)
    {
        Assert.Equal(expected.WorkflowDefinitionSnapshotId, actual.WorkflowDefinitionSnapshotId);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.Steps.Count, actual.Steps.Count);
        for (int i = 0; i < expected.Steps.Count; i++)
        {
            AssertStepStateEqual(expected.Steps[i], actual.Steps[i]);
        }
        Assert.Equal(expected.StepLessExecutions.Count, actual.StepLessExecutions.Count);
        for (int i = 0; i < expected.StepLessExecutions.Count; i++)
        {
            Assert.Equal(expected.StepLessExecutions[i], actual.StepLessExecutions[i]);
        }
        Assert.Equal(expected.CancellationRequestedExecutionIds.Count, actual.CancellationRequestedExecutionIds.Count);
        for (int i = 0; i < expected.CancellationRequestedExecutionIds.Count; i++)
        {
            Assert.Equal(expected.CancellationRequestedExecutionIds[i], actual.CancellationRequestedExecutionIds[i]);
        }
        Assert.Equal(expected.UnmatchedVerifyExecutionIds.Count, actual.UnmatchedVerifyExecutionIds.Count);
        for (int i = 0; i < expected.UnmatchedVerifyExecutionIds.Count; i++)
        {
            Assert.Equal(expected.UnmatchedVerifyExecutionIds[i], actual.UnmatchedVerifyExecutionIds[i]);
        }
    }

    private static void AssertStepStateEqual(StepState expected, StepState actual)
    {
        Assert.Equal(expected.StepId, actual.StepId);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.LatestExecutionId, actual.LatestExecutionId);
        Assert.Equal(expected.ConsecutiveFailureCount, actual.ConsecutiveFailureCount);
        Assert.Equal(expected.LatestFailureClassification, actual.LatestFailureClassification);
        Assert.Equal(expected.LatestFailureReason, actual.LatestFailureReason);
        Assert.Equal(expected.PauseRecordedForLatestExecution, actual.PauseRecordedForLatestExecution);
        Assert.Equal(expected.PausedOutcome, actual.PausedOutcome);
        Assert.Equal(expected.PendingSupplementaryExecutionId, actual.PendingSupplementaryExecutionId);
        Assert.Equal(expected.IsPendingSupersedeTarget, actual.IsPendingSupersedeTarget);
        Assert.Equal(expected.RetryNotBefore, actual.RetryNotBefore);
        Assert.Equal(expected.RetryDelayMs, actual.RetryDelayMs);
        Assert.Equal(expected.RetryScheduledForExecutionId, actual.RetryScheduledForExecutionId);
        Assert.Equal(expected.LatestExecutionFailedRetryNotBefore, actual.LatestExecutionFailedRetryNotBefore);
        Assert.Equal(expected.LinkedFromExecutionId, actual.LinkedFromExecutionId);
        Assert.Equal(expected.ExecutionCount, actual.ExecutionCount);
        Assert.Equal(expected.LatestCapturedResponseFile, actual.LatestCapturedResponseFile);
        Assert.Equal(expected.LatestUnsatisfiedOutputNames, actual.LatestUnsatisfiedOutputNames);
        Assert.Equal(expected.RetryForeclosed, actual.RetryForeclosed);

        // Both halves of the Indeterminate state, not just the reason: the flag is what
        // WorkflowOutcome/RetryEngine actually read, so a checkpoint round-trip that dropped it while
        // preserving the diagnostic text would be invisible to this helper otherwise -- the exact
        // shape of the DeepCopy hazard ProjectionCheckpointState.RetryForeclosedStepIds documents.
        Assert.Equal(expected.IndeterminateAwaitingResolution, actual.IndeterminateAwaitingResolution);
        Assert.Equal(expected.IndeterminateReason, actual.IndeterminateReason);
        Assert.Equal(expected.UpstreamExecutionIds.Count, actual.UpstreamExecutionIds.Count);
        foreach (var (k, v) in expected.UpstreamExecutionIds)
        {
            Assert.True(actual.UpstreamExecutionIds.TryGetValue(k, out var actualVal));
            Assert.Equal(v, actualVal);
        }
    }

    private static void AssertObligationsEqual(ProcessCrashRecoveryObligations expected, ProcessCrashRecoveryObligations actual)
    {
        Assert.Equal(expected.ToResubmit, actual.ToResubmit);
        Assert.Equal(expected.ToFinalizeAsCancelled, actual.ToFinalizeAsCancelled);
        Assert.Equal(expected.ToClassify, actual.ToClassify);
        Assert.Equal(expected.ToFinalizeAsAbandoned, actual.ToFinalizeAsAbandoned);
    }

    [Fact]
    public void Equivalence_checkpoint_plus_tail_equals_full_replay()
    {
        var snapshot = ComprehensiveTestSnapshot();
        var exec1 = new ExecutionId("exec-1");
        var exec2 = new ExecutionId("exec-2");
        var exec3 = new ExecutionId("exec-3");
        var exec4 = new ExecutionId("exec-4");
        var exec5 = new ExecutionId("exec-5");
        var exec6 = new ExecutionId("exec-6");
        var exec7 = new ExecutionId("exec-7");
        var exec8 = new ExecutionId("exec-8");
        var execStepless = new ExecutionId("exec-stepless");
        var execRejected = new ExecutionId("exec-rejected");
        var execSupp = new ExecutionId("exec-supp");

        var decision1 = new DecisionId("dec-1");
        var now = DateTimeOffset.UtcNow;
        var retryNotBefore = now.AddMinutes(5);

        var midwayEvents = new List<FlowEvent>
        {
            // 1. ExecutionRequestAccepted & ExecutionSucceeded
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(exec1, Step1), 100, now),
            new FlowEvent.ExecutionSucceeded(exec1),

            // 2. ExecutionRequestAccepted with upstream execution IDs
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(exec2, Step2, new Dictionary<StepId, ExecutionId> { [Step1] = exec1 }), 101, now),

            // 3. ExecutionRequestAccepted & WorkflowPaused (remains paused)
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(exec3, Step3), 102, now),
            new FlowEvent.WorkflowPaused(exec3, Step3),

            // 4. WorkflowPaused, ExternalDecisionRecorded (Supersede), WorkflowResumed
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(exec4, Step4), 103, now),
            new FlowEvent.WorkflowPaused(exec4, Step4),
            new FlowEvent.ExternalDecisionRecorded(decision1, exec4, DecisionType.Supersede, TargetStepId: Step4, SupplementaryExecutionId: execSupp),
            new FlowEvent.WorkflowResumed(decision1),

            // 5. ExecutionFailed, with a captured response (#1606) folded onto the same event since
            // Step5 gets no further events in the tail below -- letting LatestCapturedResponseFile /
            // LatestUnsatisfiedOutputNames survive untouched to the final state too
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(exec5, Step5), 104, now),
            new FlowEvent.ExecutionFailed(
                exec5, FailureClassification.Permanent, "Contract not satisfied: 'advice.md' is missing.",
                CapturedResponseFile: ".captured-response.md", UnsatisfiedOutputNames: ["advice.md"]),

            // 5b. StepRetryScheduled & StepRetryForeclosed -- same "no further tail events" reasoning:
            // the foreclosure survives untouched to the final state, which is what lets
            // AssertStepStateEqual's RetryForeclosed comparison discriminate a checkpoint DeepCopy bug
            // rather than compare false==false.
            new FlowEvent.StepRetryScheduled(Step5, exec5, retryNotBefore, 5000),
            new FlowEvent.StepRetryForeclosed(Step5, exec5, "dead pump, unfireable park"),

            // 6. StepRetryScheduled
            new FlowEvent.StepRetryScheduled(Step6, exec6, retryNotBefore, 5000),

            // 7. ExecutionCancelled
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(exec7, Step7), 105, now),
            new FlowEvent.ExecutionCancelled(exec7),

            // 8. CancellationRequested
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(exec8, Step8), 106, now),
            new FlowEvent.CancellationRequested(exec8),

            // 9. Stepless ExecutionRequestAccepted
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(execStepless, null), 107, now),

            // 10. ExecutionRequestRejected
            new FlowEvent.ExecutionRequestRejected(execRejected, "Concurrency cap reached"),
        };

        var (midwayState, checkpointMidway) = StateProjector.ProjectAndCheckpoint(midwayEvents, snapshot);
        Assert.Equal(midwayEvents.Count, checkpointMidway.EventOffset);

        // Guard assertions: verify each of the 22 checkpoint collections is non-empty at checkpoint boundary
        Assert.NotEmpty(checkpointMidway.State.RetryForeclosedStepIds);
        Assert.NotEmpty(checkpointMidway.State.LatestExecutionIdByStepId);
        Assert.NotEmpty(checkpointMidway.State.UpstreamExecutionIdsByStepId);
        Assert.NotEmpty(checkpointMidway.State.TerminalStatusByExecutionId);
        Assert.NotEmpty(checkpointMidway.State.PausedExecutionIds);
        Assert.NotEmpty(checkpointMidway.State.EverPausedExecutionIds);
        Assert.NotEmpty(checkpointMidway.State.ReferencedExecutionIdByDecisionId);
        Assert.NotEmpty(checkpointMidway.State.DecisionTypeByDecisionId);
        Assert.NotEmpty(checkpointMidway.State.TargetStepIdByDecisionId);
        Assert.NotEmpty(checkpointMidway.State.SupplementaryExecutionIdByDecisionId);
        Assert.NotEmpty(checkpointMidway.State.StepIdByExecutionId);
        Assert.NotEmpty(checkpointMidway.State.ConsecutiveFailureCountByStepId);
        Assert.NotEmpty(checkpointMidway.State.LatestFailureClassificationByStepId);
        Assert.NotEmpty(checkpointMidway.State.LatestFailureReasonByStepId);
        Assert.NotEmpty(checkpointMidway.State.LatestExecutionFailedRetryNotBeforeByStepId);
        Assert.NotEmpty(checkpointMidway.State.CancellationRequestedExecutionIds);
        Assert.NotEmpty(checkpointMidway.State.StepLessExecutionsInOrder);
        Assert.NotEmpty(checkpointMidway.State.PendingSupplementaryExecutionIdByStepId);
        Assert.NotEmpty(checkpointMidway.State.PendingSupersedeTargetStepIds);
        Assert.NotEmpty(checkpointMidway.State.RetryNotBeforeByStepId);
        Assert.NotEmpty(checkpointMidway.State.RetryDelayMsByStepId);
        Assert.NotEmpty(checkpointMidway.State.RetryScheduledForExecutionIdByStepId);

        var allEvents = new List<FlowEvent>(midwayEvents)
        {
            // Fulfill the supersede supplementary execution on Step4
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(execSupp, Step4), 108, now),
            new FlowEvent.ExecutionSucceeded(execSupp),

            // Fulfill the scheduled retry on Step6
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(exec6, Step6), 109, now),
            new FlowEvent.ExecutionSucceeded(exec6),

            // Complete Step2 and Step8
            new FlowEvent.ExecutionSucceeded(exec2),
            new FlowEvent.ExecutionCancelled(exec8),
        };

        // Projected via checkpoint + tail replay
        var stateFromCheckpoint = StateProjector.Project(allEvents, snapshot, checkpointMidway);

        // Projected via full replay
        var stateFromFullReplay = StateProjector.Project(allEvents, snapshot, checkpoint: null);

        // Deep structural equality assertion
        AssertFlowStateEqual(stateFromFullReplay, stateFromCheckpoint);
    }

    [Fact]
    public void Polarity_corrupt_checkpoint_file_falls_back_to_full_replay_loudly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "baton_checkpoint_test_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var batonDir = Path.Combine(tempDir, ".baton");
            Directory.CreateDirectory(batonDir);
            var checkpointFile = Path.Combine(batonDir, "checkpoint.json");
            File.WriteAllText(checkpointFile, "{ corrupt json ... }}}");

            using var sw = new StringWriter();
            var originalErr = Console.Error;
            Console.SetError(sw);

            ProjectionCheckpoint? checkpoint = null;
            try
            {
                checkpoint = ProjectionCheckpointStore.Load(tempDir);
            }
            finally
            {
                Console.SetError(originalErr);
            }

            Assert.Null(checkpoint);
            var errOutput = sw.ToString();
            Assert.Contains("Fallback to full replay LOUDLY", errOutput);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempDir);
        }
    }

    [Fact]
    public void Polarity_checkpoint_offset_exceeds_log_length_falls_back_to_full_replay_loudly()
    {
        var snapshot = TestSnapshot();
        var exec1 = new ExecutionId("exec-1");
        var events = new List<FlowEvent>
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(exec1, Step1), 100, DateTimeOffset.UtcNow),
            new FlowEvent.ExecutionSucceeded(exec1),
        };

        var (_, validCheckpoint) = StateProjector.ProjectAndCheckpoint(events, snapshot);
        var invalidCheckpoint = new ProjectionCheckpoint(EventOffset: 999, validCheckpoint.State);

        using var sw = new StringWriter();
        var originalErr = Console.Error;
        Console.SetError(sw);

        FlowState state;
        try
        {
            state = StateProjector.Project(events, snapshot, invalidCheckpoint);
        }
        finally
        {
            Console.SetError(originalErr);
        }

        var errOutput = sw.ToString();
        Assert.Contains("Fallback to full replay LOUDLY", errOutput);

        var fullReplayState = StateProjector.Project(events, snapshot, checkpoint: null);
        AssertFlowStateEqual(fullReplayState, state);
    }

    [Fact]
    public void Stale_checkpoint_arm_replays_tail_events()
    {
        var snapshot = TestSnapshot();
        var exec1 = new ExecutionId("exec-1");
        var exec2 = new ExecutionId("exec-2");

        var eventsAtCheckpoint = new List<FlowEvent>
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(exec1, Step1), 100, DateTimeOffset.UtcNow),
            new FlowEvent.ExecutionSucceeded(exec1),
        };

        var (stateAtCheckpoint, checkpoint) = StateProjector.ProjectAndCheckpoint(eventsAtCheckpoint, snapshot);
        Assert.Equal(StepStatus.Pending, Assert.Single(stateAtCheckpoint.Steps, s => s.StepId == Step2).Status);

        var updatedEvents = new List<FlowEvent>(eventsAtCheckpoint)
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(exec2, Step2), 101, DateTimeOffset.UtcNow),
            new FlowEvent.ExecutionSucceeded(exec2),
        };

        var reopenedState = StateProjector.Project(updatedEvents, snapshot, checkpoint);

        // Tail events must be reflected in reopened state
        Assert.Equal(StepStatus.Succeeded, Assert.Single(reopenedState.Steps, s => s.StepId == Step2).Status);
        Assert.Equal(WorkflowStatus.Terminal, reopenedState.Status);
    }

    [Fact]
    public void Determinism_deleting_checkpoint_changes_nothing_except_open_cost()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "baton_checkpoint_det_test_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var snapshot = TestSnapshot();
            var exec1 = new ExecutionId("exec-1");
            var events = new List<FlowEvent>
            {
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(exec1, Step1), 100, DateTimeOffset.UtcNow),
                new FlowEvent.ExecutionSucceeded(exec1),
            };

            var (_, checkpoint) = StateProjector.ProjectAndCheckpoint(events, snapshot);
            ProjectionCheckpointStore.Save(tempDir, checkpoint);

            var loadedCheckpoint = ProjectionCheckpointStore.Load(tempDir);
            var stateWithCheckpoint = StateProjector.Project(events, snapshot, loadedCheckpoint);

            ProjectionCheckpointStore.Delete(tempDir);
            var checkpointAfterDelete = ProjectionCheckpointStore.Load(tempDir);
            Assert.Null(checkpointAfterDelete);

            var stateWithoutCheckpoint = StateProjector.Project(events, snapshot, checkpoint: null);

            AssertFlowStateEqual(stateWithoutCheckpoint, stateWithCheckpoint);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempDir);
        }
    }

    [Fact]
    public void Red_first_proof_skipping_tail_replay_fails_equivalence()
    {
        var snapshot = TestSnapshot();
        var exec1 = new ExecutionId("exec-1");
        var exec2 = new ExecutionId("exec-2");

        var midwayEvents = new List<FlowEvent>
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(exec1, Step1), 100, DateTimeOffset.UtcNow),
            new FlowEvent.ExecutionSucceeded(exec1),
        };

        var (_, checkpoint) = StateProjector.ProjectAndCheckpoint(midwayEvents, snapshot);

        var allEvents = new List<FlowEvent>(midwayEvents)
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(exec2, Step2), 101, DateTimeOffset.UtcNow),
            new FlowEvent.ExecutionSucceeded(exec2),
        };

        // Full replay reflects all events
        var fullReplayState = StateProjector.Project(allEvents, snapshot, checkpoint: null);

        // Simulated broken checkpoint load (skipping tail replay by building state strictly from checkpoint without processing tail events)
        var brokenStateFromCheckpointOnly = StateProjector.Project(midwayEvents, snapshot, checkpoint);

        // Proves that the equivalence test discriminates: broken state is NOT equal to full replay state
        Assert.NotEqual(fullReplayState.Status, brokenStateFromCheckpointOnly.Status);
        Assert.Equal(StepStatus.Pending, Assert.Single(brokenStateFromCheckpointOnly.Steps, s => s.StepId == Step2).Status);
        Assert.Equal(StepStatus.Succeeded, Assert.Single(fullReplayState.Steps, s => s.StepId == Step2).Status);
    }

    [Fact]
    public async Task Scope1b_AggregatesFromCheckpointAndTail_StructurallyEqualFullScan()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "baton_scope1b_aggregates_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var snapshot = TestSnapshot();
            var logPath = Path.Combine(tempDir, "flow.jsonl");
            var writer = new FlowEventLogWriter(logPath);

            var exec1 = new ExecutionId("exec-1");
            var exec2 = new ExecutionId("exec-2");

            // Step 1 events
            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(MakeRequest(exec1, Step1), 100, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new FlowEvent.ExecutionSucceeded(exec1), TestContext.Current.CancellationToken);

            var reader = new FlowEventLogReader(logPath);
            var snapshot1 = await reader.ReadSnapshotAsync(TestContext.Current.CancellationToken);
            var (_, checkpoint1) = StateProjector.ProjectAndCheckpoint(snapshot1.FlowEvents, snapshot, logByteOffset: snapshot1.ByteOffset);
            ProjectionCheckpointStore.Save(tempDir, checkpoint1);

            // Step 2 events appended after checkpoint
            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(MakeRequest(exec2, Step2), 101, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new FlowEvent.ExecutionSucceeded(exec2), TestContext.Current.CancellationToken);

            // 1. Seek-to-tail read + checkpoint projection
            var loadedCheckpoint = ProjectionCheckpointStore.Load(tempDir)!;
            var tailSnapshot = await reader.ReadSnapshotFromOffsetAsync(loadedCheckpoint.ByteOffset, TestContext.Current.CancellationToken);
            Assert.False(tailSnapshot.IsFallbackToFull);

            var (tailState, newCheckpoint) = StateProjector.ProjectAndCheckpoint(
                tailSnapshot.FlowEvents, snapshot, loadedCheckpoint, tailSnapshot.ByteOffset);

            // 2. Full scan from byte 0
            var fullSnapshot = await reader.ReadSnapshotFromOffsetAsync(0, TestContext.Current.CancellationToken);
            var (fullState, _) = StateProjector.ProjectAndCheckpoint(fullSnapshot.FlowEvents, snapshot, checkpoint: null);

            // Equivalence check: Projected FlowState structurally equal
            AssertFlowStateEqual(fullState, tailState);

            // Equivalence check: Checkpointed aggregates structurally equal
            var fullSucceeded = fullSnapshot.FlowEvents.OfType<FlowEvent.ExecutionSucceeded>().Select(e => e.ExecutionId).ToHashSet();
            var fullAccepted = fullSnapshot.FlowEvents.OfType<FlowEvent.ExecutionRequestAccepted>().ToDictionary(e => e.Request.ExecutionId, e => e.Request);

            Assert.Equal(fullSucceeded, newCheckpoint.State.SucceededExecutionIds);
            Assert.Equal(fullAccepted.Keys, newCheckpoint.State.AcceptedRequestByExecutionId.Keys);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempDir);
        }
    }

    [Fact]
    public async Task Scope1b_Polarity_CorruptBytePosition_FallsBackLoudly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "baton_scope1b_corrupt_seek_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var snapshot = TestSnapshot();
            var logPath = Path.Combine(tempDir, "flow.jsonl");
            var writer = new FlowEventLogWriter(logPath);

            var exec1 = new ExecutionId("exec-1");
            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(MakeRequest(exec1, Step1), 100, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new FlowEvent.ExecutionSucceeded(exec1), TestContext.Current.CancellationToken);

            var reader = new FlowEventLogReader(logPath);
            var snapshot1 = await reader.ReadSnapshotAsync(TestContext.Current.CancellationToken);
            var (_, checkpoint1) = StateProjector.ProjectAndCheckpoint(snapshot1.FlowEvents, snapshot, logByteOffset: snapshot1.ByteOffset);

            // Corrupt byte position: point 3 bytes past actual line boundary
            long corruptByteOffset = checkpoint1.ByteOffset + 3;

            using var sw = new StringWriter();
            var originalErr = Console.Error;
            Console.SetError(sw);

            EventLogSnapshot seekSnapshot;
            try
            {
                seekSnapshot = await reader.ReadSnapshotFromOffsetAsync(corruptByteOffset, TestContext.Current.CancellationToken);
            }
            finally
            {
                Console.SetError(originalErr);
            }

            var errOutput = sw.ToString();
            Assert.Contains("Fallback to full replay LOUDLY", errOutput);
            Assert.True(seekSnapshot.IsFallbackToFull);
            Assert.Equal(2, seekSnapshot.FlowEvents.Count);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempDir);
        }
    }

    [Fact]
    public async Task Scope1b_Polarity_CaughtUpOffsetOffARecordBoundary_FallsBackLoudly()
    {
        // The caught-up shape of the boundary check (#971's second reader): offset == file length
        // is the single most common call — nothing appended since the checkpoint — and it used to
        // return an empty snapshot trusting the offset before validating it. A checkpoint pointing
        // at the end of a file whose last record never got its terminator (crash mid-append after
        // an fsynced offset was recorded elsewhere) must replay, not silently confirm.
        var tempDir = Path.Combine(Path.GetTempPath(), "baton_scope1b_caughtup_misaligned_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var logPath = Path.Combine(tempDir, "flow.jsonl");
            var exec1 = new ExecutionId("exec-1");
            await using (var writer = new FlowEventLogWriter(logPath))
            {
                await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(MakeRequest(exec1, Step1), 100, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
                await writer.AppendAsync(new FlowEvent.ExecutionSucceeded(exec1), TestContext.Current.CancellationToken);
            }

            // A write cut off mid-record: the file now ends without '\n', and the offset under
            // test is exactly its length — the caught-up early-return shape.
            await File.AppendAllTextAsync(logPath, "{\"cut\":", TestContext.Current.CancellationToken);
            var misalignedLength = new FileInfo(logPath).Length;

            using var sw = new StringWriter();
            var originalErr = Console.Error;
            Console.SetError(sw);

            EventLogSnapshot seekSnapshot;
            try
            {
                seekSnapshot = await new FlowEventLogReader(logPath)
                    .ReadSnapshotFromOffsetAsync(misalignedLength, TestContext.Current.CancellationToken);
            }
            finally
            {
                Console.SetError(originalErr);
            }

            Assert.Contains("Fallback to full replay LOUDLY", sw.ToString());
            Assert.True(seekSnapshot.IsFallbackToFull);
            Assert.Equal(2, seekSnapshot.FlowEvents.Count);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempDir);
        }
    }

    [Fact]
    public async Task Scope1b_Polarity_StaleCheckpoint_ReplaysTail()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "baton_scope1b_stale_checkpoint_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var snapshot = TestSnapshot();
            var logPath = Path.Combine(tempDir, "flow.jsonl");
            var writer = new FlowEventLogWriter(logPath);

            var exec1 = new ExecutionId("exec-1");
            var exec2 = new ExecutionId("exec-2");

            // Checkpoint at step 1
            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(MakeRequest(exec1, Step1), 100, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new FlowEvent.ExecutionSucceeded(exec1), TestContext.Current.CancellationToken);

            var reader = new FlowEventLogReader(logPath);
            var snapshot1 = await reader.ReadSnapshotAsync(TestContext.Current.CancellationToken);
            var (_, checkpoint1) = StateProjector.ProjectAndCheckpoint(snapshot1.FlowEvents, snapshot, logByteOffset: snapshot1.ByteOffset);

            // Events appended after checkpoint
            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(MakeRequest(exec2, Step2), 101, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new FlowEvent.ExecutionSucceeded(exec2), TestContext.Current.CancellationToken);

            // Stale checkpoint read from saved ByteOffset
            var tailSnapshot = await reader.ReadSnapshotFromOffsetAsync(checkpoint1.ByteOffset, TestContext.Current.CancellationToken);
            Assert.False(tailSnapshot.IsFallbackToFull);
            Assert.Equal(2, tailSnapshot.FlowEvents.Count);

            var (tailState, newCheckpoint) = StateProjector.ProjectAndCheckpoint(
                tailSnapshot.FlowEvents, snapshot, checkpoint1, tailSnapshot.ByteOffset);

            Assert.Equal(StepStatus.Succeeded, Assert.Single(tailState.Steps, s => s.StepId == Step2).Status);
            Assert.Contains(exec2, newCheckpoint.State.SucceededExecutionIds);
            Assert.True(newCheckpoint.State.AcceptedRequestByExecutionId.ContainsKey(exec2));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempDir);
        }
    }

    [Fact]
    public async Task Scope1b_CoreAggregatesInCheckpoint_DivergenceFixture_AbandonedPreCheckpoint()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "baton_scope1b_abandoned_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var snapshot = TestSnapshot();
            var logPath = Path.Combine(tempDir, "flow.jsonl");
            var writer = new FlowEventLogWriter(logPath);

            var exec1 = new ExecutionId("exec-1");
            var now = DateTimeOffset.UtcNow;

            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(MakeRequest(exec1, Step1), 100, now), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new CoreEvent.ExecutionStarted(exec1, 1234), TestContext.Current.CancellationToken);

            var reader = new FlowEventLogReader(logPath);
            var snapshot1 = await reader.ReadSnapshotAsync(TestContext.Current.CancellationToken);
            var (state1, checkpoint1) = StateProjector.ProjectAndCheckpoint(snapshot1.FlowEvents, snapshot, logByteOffset: snapshot1.ByteOffset);

            var (started1, exited1) = CoreEventAggregation.Merge(null, null, snapshot1.CoreEvents);
            var (prunedStarted1, prunedExited1) = CoreEventAggregation.Prune(started1, exited1, state1);
            checkpoint1 = checkpoint1 with
            {
                State = checkpoint1.State with
                {
                    CoreStartedExecutionIds = prunedStarted1,
                    CoreExitedByExecutionId = prunedExited1
                }
            };
            ProjectionCheckpointStore.Save(tempDir, checkpoint1);

            var loadedCheckpoint = ProjectionCheckpointStore.Load(tempDir)!;
            var tailSnapshot = await reader.ReadSnapshotFromOffsetAsync(loadedCheckpoint.ByteOffset, TestContext.Current.CancellationToken);
            var (tailState, _) = StateProjector.ProjectAndCheckpoint(tailSnapshot.FlowEvents, snapshot, loadedCheckpoint, tailSnapshot.ByteOffset);

            var (tailMergedStarted, tailMergedExited) = CoreEventAggregation.Merge(
                loadedCheckpoint.State.CoreStartedExecutionIds,
                loadedCheckpoint.State.CoreExitedByExecutionId,
                tailSnapshot.CoreEvents);

            var workerContract = new WorkerContract("worker1", [], [], []);
            var coreTarget = new CoreDispatchTarget("stub", []);
            var timeout = TimeSpan.FromMinutes(1);
            var workerBindings = new Dictionary<string, WorkerBinding>
            {
                ["worker1"] = new WorkerBinding.Process(workerContract, coreTarget, timeout)
            };
            var inFlight = new HashSet<ExecutionId>();

            var tailObligations = ProcessCrashRecoveryDetector.GetObligations(
                tailState, snapshot, workerBindings, tailMergedStarted, tailMergedExited, inFlight);

            var fullSnapshot = await reader.ReadSnapshotFromOffsetAsync(0, TestContext.Current.CancellationToken);
            var (fullState, _) = StateProjector.ProjectAndCheckpoint(fullSnapshot.FlowEvents, snapshot, checkpoint: null);
            var (fullMergedStarted, fullMergedExited) = CoreEventAggregation.Merge(null, null, fullSnapshot.CoreEvents);
            var fullObligations = ProcessCrashRecoveryDetector.GetObligations(
                fullState, snapshot, workerBindings, fullMergedStarted, fullMergedExited, inFlight);

            AssertObligationsEqual(fullObligations, tailObligations);
            Assert.Single(tailObligations.ToFinalizeAsAbandoned, exec1);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempDir);
        }
    }

    [Fact]
    public async Task Scope1b_CoreAggregatesInCheckpoint_ExitPostCheckpoint_ToClassify()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "baton_scope1b_classify_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var snapshot = TestSnapshot();
            var logPath = Path.Combine(tempDir, "flow.jsonl");
            var writer = new FlowEventLogWriter(logPath);

            var exec1 = new ExecutionId("exec-1");
            var now = DateTimeOffset.UtcNow;

            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(MakeRequest(exec1, Step1), 100, now), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new CoreEvent.ExecutionStarted(exec1, 1234), TestContext.Current.CancellationToken);

            var reader = new FlowEventLogReader(logPath);
            var snapshot1 = await reader.ReadSnapshotAsync(TestContext.Current.CancellationToken);
            var (state1, checkpoint1) = StateProjector.ProjectAndCheckpoint(snapshot1.FlowEvents, snapshot, logByteOffset: snapshot1.ByteOffset);

            var (started1, exited1) = CoreEventAggregation.Merge(null, null, snapshot1.CoreEvents);
            var (prunedStarted1, prunedExited1) = CoreEventAggregation.Prune(started1, exited1, state1);
            checkpoint1 = checkpoint1 with
            {
                State = checkpoint1.State with
                {
                    CoreStartedExecutionIds = prunedStarted1,
                    CoreExitedByExecutionId = prunedExited1
                }
            };
            ProjectionCheckpointStore.Save(tempDir, checkpoint1);

            await writer.AppendAsync(new CoreEvent.ExecutionExited(exec1, 0, CoreExitReason.Natural), TestContext.Current.CancellationToken);

            var loadedCheckpoint = ProjectionCheckpointStore.Load(tempDir)!;
            var tailSnapshot = await reader.ReadSnapshotFromOffsetAsync(loadedCheckpoint.ByteOffset, TestContext.Current.CancellationToken);
            var (tailState, _) = StateProjector.ProjectAndCheckpoint(tailSnapshot.FlowEvents, snapshot, loadedCheckpoint, tailSnapshot.ByteOffset);

            var (tailMergedStarted, tailMergedExited) = CoreEventAggregation.Merge(
                loadedCheckpoint.State.CoreStartedExecutionIds,
                loadedCheckpoint.State.CoreExitedByExecutionId,
                tailSnapshot.CoreEvents);

            var workerContract = new WorkerContract("worker1", [], [], []);
            var coreTarget = new CoreDispatchTarget("stub", []);
            var timeout = TimeSpan.FromMinutes(1);
            var workerBindings = new Dictionary<string, WorkerBinding>
            {
                ["worker1"] = new WorkerBinding.Process(workerContract, coreTarget, timeout)
            };
            var inFlight = new HashSet<ExecutionId>();

            var tailObligations = ProcessCrashRecoveryDetector.GetObligations(
                tailState, snapshot, workerBindings, tailMergedStarted, tailMergedExited, inFlight);

            var fullSnapshot = await reader.ReadSnapshotFromOffsetAsync(0, TestContext.Current.CancellationToken);
            var (fullState, _) = StateProjector.ProjectAndCheckpoint(fullSnapshot.FlowEvents, snapshot, checkpoint: null);
            var (fullMergedStarted, fullMergedExited) = CoreEventAggregation.Merge(null, null, fullSnapshot.CoreEvents);
            var fullObligations = ProcessCrashRecoveryDetector.GetObligations(
                fullState, snapshot, workerBindings, fullMergedStarted, fullMergedExited, inFlight);

            AssertObligationsEqual(fullObligations, tailObligations);
            Assert.Single(tailObligations.ToClassify, item => item.ExecutionId == exec1);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempDir);
        }
    }

    [Fact]
    public async Task Scope1b_CoreAggregatesInCheckpoint_ControlArm_TailOnlyAggregates_FailsEquivalence()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "baton_scope1b_control_arm_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var snapshot = TestSnapshot();
            var logPath = Path.Combine(tempDir, "flow.jsonl");
            var writer = new FlowEventLogWriter(logPath);

            var exec1 = new ExecutionId("exec-1");
            var now = DateTimeOffset.UtcNow;

            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(MakeRequest(exec1, Step1), 100, now), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new CoreEvent.ExecutionStarted(exec1, 1234), TestContext.Current.CancellationToken);

            var reader = new FlowEventLogReader(logPath);
            var snapshot1 = await reader.ReadSnapshotAsync(TestContext.Current.CancellationToken);
            var (state1, checkpoint1) = StateProjector.ProjectAndCheckpoint(snapshot1.FlowEvents, snapshot, logByteOffset: snapshot1.ByteOffset);

            var (started1, exited1) = CoreEventAggregation.Merge(null, null, snapshot1.CoreEvents);
            var (prunedStarted1, prunedExited1) = CoreEventAggregation.Prune(started1, exited1, state1);
            checkpoint1 = checkpoint1 with
            {
                State = checkpoint1.State with
                {
                    CoreStartedExecutionIds = prunedStarted1,
                    CoreExitedByExecutionId = prunedExited1
                }
            };
            ProjectionCheckpointStore.Save(tempDir, checkpoint1);

            var loadedCheckpoint = ProjectionCheckpointStore.Load(tempDir)!;
            var tailSnapshot = await reader.ReadSnapshotFromOffsetAsync(loadedCheckpoint.ByteOffset, TestContext.Current.CancellationToken);
            var (tailState, _) = StateProjector.ProjectAndCheckpoint(tailSnapshot.FlowEvents, snapshot, loadedCheckpoint, tailSnapshot.ByteOffset);

            // CONTROL ARM: Merge deliberately dropped (passing tail-only core events)
            var (brokenStarted, brokenExited) = CoreEventAggregation.Merge(null, null, tailSnapshot.CoreEvents);

            var workerContract = new WorkerContract("worker1", [], [], []);
            var coreTarget = new CoreDispatchTarget("stub", []);
            var timeout = TimeSpan.FromMinutes(1);
            var workerBindings = new Dictionary<string, WorkerBinding>
            {
                ["worker1"] = new WorkerBinding.Process(workerContract, coreTarget, timeout)
            };
            var inFlight = new HashSet<ExecutionId>();

            var brokenTailObligations = ProcessCrashRecoveryDetector.GetObligations(
                tailState, snapshot, workerBindings, brokenStarted, brokenExited, inFlight);

            var fullSnapshot = await reader.ReadSnapshotFromOffsetAsync(0, TestContext.Current.CancellationToken);
            var (fullState, _) = StateProjector.ProjectAndCheckpoint(fullSnapshot.FlowEvents, snapshot, checkpoint: null);
            var (fullMergedStarted, fullMergedExited) = CoreEventAggregation.Merge(null, null, fullSnapshot.CoreEvents);
            var fullObligations = ProcessCrashRecoveryDetector.GetObligations(
                fullState, snapshot, workerBindings, fullMergedStarted, fullMergedExited, inFlight);

            // Control arm assertion: Without merge, tail obligations land in ToResubmit instead of ToFinalizeAsAbandoned
            Assert.NotEqual(fullObligations, brokenTailObligations);
            Assert.Single(fullObligations.ToFinalizeAsAbandoned, exec1);
            Assert.Empty(fullObligations.ToResubmit);
            Assert.Empty(brokenTailObligations.ToFinalizeAsAbandoned);
            Assert.Single(brokenTailObligations.ToResubmit, exec1);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempDir);
        }
    }

    /// <summary>
    /// N3 (#1664 re-review): a checkpoint written by the shipped release (Version 3, before
    /// <c>IndeterminateProducerByStepId</c> existed) must be rejected by <see cref="ProjectionCheckpointStore.Load"/>
    /// rather than accepted with that map silently empty — an empty map here is indistinguishable from
    /// "a producer no verb admits" (see <see cref="ProjectionCheckpoint.Version"/>'s own remarks), which
    /// would make an already-Indeterminate room permanently unresolvable after upgrade. A full replay
    /// off the untouched flow.jsonl is what repopulates the map correctly.
    /// </summary>
    [Fact]
    public async Task Version3_checkpoint_is_rejected_by_Load_and_the_room_full_replays_to_the_right_producer()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "baton_v3_checkpoint_test_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var snapshot = TestSnapshot();
            var logPath = Path.Combine(tempDir, "flow.jsonl");
            var writer = new FlowEventLogWriter(logPath);

            var exec1 = new ExecutionId("exec-1");

            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(MakeRequest(exec1, Step1), 100, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
            // Natural exit-0, no captured response: the ContractFailure producer (StateProjector's own
            // CapturedResponseFile-null read).
            await writer.AppendAsync(new FlowEvent.ExecutionIndeterminate(exec1, "Contract not satisfied: 'output1' is missing."), TestContext.Current.CancellationToken);

            var reader = new FlowEventLogReader(logPath);
            var fullSnapshot = await reader.ReadSnapshotAsync(TestContext.Current.CancellationToken);
            var (fullState, checkpoint) = StateProjector.ProjectAndCheckpoint(fullSnapshot.FlowEvents, snapshot, logByteOffset: fullSnapshot.ByteOffset);

            var step1Full = Assert.Single(fullState.Steps, s => s.StepId == Step1);
            Assert.True(step1Full.IndeterminateAwaitingResolution);
            Assert.Equal(IndeterminateProducer.ContractFailure, step1Full.IndeterminateProducer);

            // Simulate the shipped release's checkpoint by writing Version 3 straight to disk —
            // ProjectionCheckpointStore.Save always writes the current Version, so this bypasses it
            // deliberately to reproduce a file that predates IndeterminateProducerByStepId.
            var legacyCheckpoint = checkpoint with { Version = 3 };
            ProjectionCheckpointStore.Save(tempDir, legacyCheckpoint);

            using var sw = new StringWriter();
            var originalErr = Console.Error;
            Console.SetError(sw);

            ProjectionCheckpoint? loaded;
            try
            {
                loaded = ProjectionCheckpointStore.Load(tempDir);
            }
            finally
            {
                Console.SetError(originalErr);
            }

            Assert.Null(loaded);
            Assert.Contains("Fallback to full replay LOUDLY", sw.ToString());

            // With Load rejecting the stale checkpoint, the room re-derives state via a full replay off
            // the untouched flow.jsonl — the same path MutationInterface takes when Load returns null.
            var replayedSnapshot = await reader.ReadSnapshotFromOffsetAsync(loaded?.ByteOffset ?? 0, TestContext.Current.CancellationToken);
            var (replayedState, _) = StateProjector.ProjectAndCheckpoint(replayedSnapshot.FlowEvents, snapshot, loaded, replayedSnapshot.ByteOffset);

            var step1Replayed = Assert.Single(replayedState.Steps, s => s.StepId == Step1);
            Assert.True(step1Replayed.IndeterminateAwaitingResolution);
            Assert.Equal(IndeterminateProducer.ContractFailure, step1Replayed.IndeterminateProducer);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempDir);
        }
    }

    /// <summary>
    /// #1877: the arm the previous bump's test cannot cover. A Version-4 checkpoint was written under
    /// the OLD rule, where a rejected CapturedResponse capture stayed retry-eligible, so its
    /// <c>RetryForeclosedStepIds</c> is empty for a step the current projector forecloses. Serving it
    /// would leave exactly the symptom #1877 exists to end — a settled room reading Running forever —
    /// on precisely the rooms the fix was written for. Without this arm, reverting the gate to
    /// <c>&lt; 4</c> passes the whole suite while silently un-fixing every already-checkpointed room.
    /// </summary>
    [Fact]
    public async Task Version4_checkpoint_written_under_the_pre_1877_rule_is_rejected_and_replays_foreclosed()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "baton_v4_checkpoint_test_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var snapshot = TestSnapshot();
            var logPath = Path.Combine(tempDir, "flow.jsonl");
            var writer = new FlowEventLogWriter(logPath);
            var exec1 = new ExecutionId("exec-1");

            await writer.AppendAsync(
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(exec1, Step1), 100, DateTimeOffset.UtcNow),
                TestContext.Current.CancellationToken);
            await writer.AppendAsync(
                new FlowEvent.ExecutionIndeterminate(
                    exec1, "captured, awaiting conductor resolution.", OutputMaterializer.CapturedResponseFileName, ["output1"]),
                TestContext.Current.CancellationToken);
            await writer.AppendAsync(
                new FlowEvent.CaptureResolved(Step1, exec1, Accepted: false, "prose, not output1", ["output1"]),
                TestContext.Current.CancellationToken);

            var reader = new FlowEventLogReader(logPath);
            var fullSnapshot = await reader.ReadSnapshotAsync(TestContext.Current.CancellationToken);
            var (_, checkpoint) = StateProjector.ProjectAndCheckpoint(
                fullSnapshot.FlowEvents, snapshot, logByteOffset: fullSnapshot.ByteOffset);

            // The pre-#1877 file, reconstructed: same events, same offsets, Version 4, and the
            // foreclosure the old projector never recorded. Cleared explicitly rather than assumed --
            // this assertion is what makes the reject below discriminating rather than a tautology.
            var legacyCheckpoint = checkpoint with { Version = 4 };
            legacyCheckpoint.State.RetryForeclosedStepIds.Clear();
            Assert.Empty(legacyCheckpoint.State.RetryForeclosedStepIds);
            ProjectionCheckpointStore.Save(tempDir, legacyCheckpoint);

            using var sw = new StringWriter();
            var originalErr = Console.Error;
            Console.SetError(sw);
            ProjectionCheckpoint? loaded;
            try
            {
                loaded = ProjectionCheckpointStore.Load(tempDir);
            }
            finally
            {
                Console.SetError(originalErr);
            }

            Assert.Null(loaded);
            Assert.Contains("Fallback to full replay LOUDLY", sw.ToString());

            var replayedSnapshot = await reader.ReadSnapshotFromOffsetAsync(
                loaded?.ByteOffset ?? 0, TestContext.Current.CancellationToken);
            var (replayedState, _) = StateProjector.ProjectAndCheckpoint(
                replayedSnapshot.FlowEvents, snapshot, loaded, replayedSnapshot.ByteOffset);

            var step1 = Assert.Single(replayedState.Steps, s => s.StepId == Step1);
            Assert.Equal(1, step1.ConsecutiveFailureCount);
            Assert.True(step1.RetryForeclosed);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempDir);
        }
    }

    [Fact]
    public async Task Scope1b_CoreAggregatesInCheckpoint_Determinism_DeleteCheckpointYieldsIdenticalObligations()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "baton_scope1b_determinism_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var snapshot = TestSnapshot();
            var logPath = Path.Combine(tempDir, "flow.jsonl");
            var writer = new FlowEventLogWriter(logPath);

            var exec1 = new ExecutionId("exec-1");
            var now = DateTimeOffset.UtcNow;

            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(MakeRequest(exec1, Step1), 100, now), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new CoreEvent.ExecutionStarted(exec1, 1234), TestContext.Current.CancellationToken);

            var reader = new FlowEventLogReader(logPath);
            var snapshot1 = await reader.ReadSnapshotAsync(TestContext.Current.CancellationToken);
            var (state1, checkpoint1) = StateProjector.ProjectAndCheckpoint(snapshot1.FlowEvents, snapshot, logByteOffset: snapshot1.ByteOffset);

            var (started1, exited1) = CoreEventAggregation.Merge(null, null, snapshot1.CoreEvents);
            var (prunedStarted1, prunedExited1) = CoreEventAggregation.Prune(started1, exited1, state1);
            checkpoint1 = checkpoint1 with
            {
                State = checkpoint1.State with
                {
                    CoreStartedExecutionIds = prunedStarted1,
                    CoreExitedByExecutionId = prunedExited1
                }
            };
            ProjectionCheckpointStore.Save(tempDir, checkpoint1);

            var loadedCheckpoint = ProjectionCheckpointStore.Load(tempDir)!;
            var tailSnapshot = await reader.ReadSnapshotFromOffsetAsync(loadedCheckpoint.ByteOffset, TestContext.Current.CancellationToken);
            var (tailState, _) = StateProjector.ProjectAndCheckpoint(tailSnapshot.FlowEvents, snapshot, loadedCheckpoint, tailSnapshot.ByteOffset);

            var (tailMergedStarted, tailMergedExited) = CoreEventAggregation.Merge(
                loadedCheckpoint.State.CoreStartedExecutionIds,
                loadedCheckpoint.State.CoreExitedByExecutionId,
                tailSnapshot.CoreEvents);

            var workerContract = new WorkerContract("worker1", [], [], []);
            var coreTarget = new CoreDispatchTarget("stub", []);
            var timeout = TimeSpan.FromMinutes(1);
            var workerBindings = new Dictionary<string, WorkerBinding>
            {
                ["worker1"] = new WorkerBinding.Process(workerContract, coreTarget, timeout)
            };
            var inFlight = new HashSet<ExecutionId>();

            var tailObligations = ProcessCrashRecoveryDetector.GetObligations(
                tailState, snapshot, workerBindings, tailMergedStarted, tailMergedExited, inFlight);

            ProjectionCheckpointStore.Delete(tempDir);
            Assert.Null(ProjectionCheckpointStore.Load(tempDir));

            var fullSnapshot = await reader.ReadSnapshotFromOffsetAsync(0, TestContext.Current.CancellationToken);
            var (fullState, _) = StateProjector.ProjectAndCheckpoint(fullSnapshot.FlowEvents, snapshot, checkpoint: null);
            var (fullMergedStarted, fullMergedExited) = CoreEventAggregation.Merge(null, null, fullSnapshot.CoreEvents);
            var fullObligations = ProcessCrashRecoveryDetector.GetObligations(
                fullState, snapshot, workerBindings, fullMergedStarted, fullMergedExited, inFlight);

            AssertObligationsEqual(fullObligations, tailObligations);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempDir);
        }
    }

    /// <summary>
    /// #1933: <see cref="FlowEvent.EngineFilesPlaced"/> projects into
    /// <c>ProjectionCheckpointState.EnginePlacedFilesByExecutionId</c>, and that member survives both
    /// halves of a checkpointed resume — the positional <c>DeepCopy</c> every new member has to be added
    /// to by hand (its own remarks name the #1606 loss this reproduces), and the JSON save/load. Without
    /// both, the crash-recovery classification reads an empty placement list and counts AER's own writes
    /// as the worker's, which is the very defect this issue closed.
    /// </summary>
    [Fact]
    public async Task Scope1c_EnginePlacedPaths_SurviveDeepCopyAndCheckpointRoundTrip()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "baton_1933_placed_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var snapshot = TestSnapshot();
            var logPath = Path.Combine(tempDir, "flow.jsonl");
            var writer = new FlowEventLogWriter(logPath);

            var exec1 = new ExecutionId("exec-1");
            // The digest travels with the path, or the crash-recovery reader can no longer tell "still
            // AER's bytes" from "the worker edited it" — losing it across the round trip would silently
            // stop the subtraction rather than break it loudly.
            EnginePlacedFile[] placed =
            [
                new(@"C:\repo\.claude\skills\audit-tool\SKILL.md", "9f2b1c"),
                new(@"C:\repo\.claude\skills\audit-tool\reference\notes.md", null),
            ];

            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(MakeRequest(exec1, Step1)), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new FlowEvent.EngineFilesPlaced(exec1, placed, ["audit-tool"]), TestContext.Current.CancellationToken);

            var reader = new FlowEventLogReader(logPath);
            var logSnapshot = await reader.ReadSnapshotAsync(TestContext.Current.CancellationToken);
            var (_, checkpoint) = StateProjector.ProjectAndCheckpoint(
                logSnapshot.FlowEvents, snapshot, logByteOffset: logSnapshot.ByteOffset);

            // ProjectAndCheckpoint hands back state.DeepCopy(), so this arm already fails on a member
            // left out of that positional constructor.
            Assert.Equal(placed, checkpoint.State.EnginePlacedFilesByExecutionId[exec1]);

            ProjectionCheckpointStore.Save(tempDir, checkpoint);
            var loaded = ProjectionCheckpointStore.Load(tempDir)!;
            Assert.Equal(placed, loaded.State.EnginePlacedFilesByExecutionId[exec1]);

            // The list itself is copied, not shared: a resume that mutated the loaded copy must not
            // reach back into the checkpoint it came from.
            var copy = loaded.State.DeepCopy();
            copy.EnginePlacedFilesByExecutionId[exec1].Add(new EnginePlacedFile(@"C:\repo\not-placed.md", null));
            Assert.Equal(placed, loaded.State.EnginePlacedFilesByExecutionId[exec1]);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempDir);
        }
    }
}
