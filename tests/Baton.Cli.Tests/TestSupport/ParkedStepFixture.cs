using Baton.Domain;
using Baton.Store;
using Baton.Templates;

namespace Baton.Cli.Tests.TestSupport;

/// <summary>
/// Hand-writes a snapshot plus an <c>ExecutionFailed</c>(<see cref="FailureClassification.ExhaustedUntil"/>)
/// / <c>StepRetryScheduled</c> pair directly to <c>flow.jsonl</c> — the shape #594's retry scheduling
/// actually records for a quota park — rather than driving it through <c>RunCommand</c>, whose
/// <c>ShellCommandWorkerAdapter</c> has no way to report a quota classification.
/// <c>StatusCommandEndToEndTests</c>'s human-rendering assertions and
/// <c>CancelCommandDeadHolderTests</c>'s #1586 dead-holder gate both drive off this one fixture,
/// rather than each hand-rolling its own copy of the identical parked-with-a-pending-retry shape.
/// </summary>
public static class ParkedStepFixture
{
    public static async Task<(string SnapshotPath, string LogPath, ExecutionId ExecutionId, DateTimeOffset RetryNotBefore)>
        WriteParkedStepFixtureAsync(
            string testRoot,
            string roomDirectory,
            FailureClassification classification = FailureClassification.ExhaustedUntil,
            TimeSpan? retryIn = null,
            int? enginePid = null,
            DateTimeOffset? engineStartTime = null)
    {
        Directory.CreateDirectory(roomDirectory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("parked-probe"),
            1,
            [new WorkflowStepDefinition(new StepId("implement"), "implement", [], ["out"], [], new RetryPolicy(3))]);
        var snapshot = SnapshotBinder.Bind(definition);
        var snapshotPath = Path.Combine(roomDirectory, "snapshot.json");
        await SnapshotBinder.PersistAsync(snapshot, snapshotPath, TestContext.Current.CancellationToken);

        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var executionId = new ExecutionId("exec-parked-1");
        var request = new ExecutionRequest(
            executionId,
            new WorkflowId("wf-parked"),
            new StepId("implement"),
            "implement",
            Inputs: [],
            Outputs: [],
            Timeout: TimeSpan.FromSeconds(30),
            Environment: [],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

        var retryNotBefore = DateTimeOffset.UtcNow.Add(retryIn ?? TimeSpan.FromMinutes(45));

        await using (var writer = new FlowEventLogWriter(logPath))
        {
            await writer.AppendAsync(
                new FlowEvent.ExecutionRequestAccepted(request, EnginePid: enginePid, EngineStartTime: engineStartTime),
                TestContext.Current.CancellationToken);
            await writer.AppendAsync(
                new FlowEvent.ExecutionFailed(executionId, classification, "attempt failed", retryNotBefore),
                TestContext.Current.CancellationToken);
            await writer.AppendAsync(
                new FlowEvent.StepRetryScheduled(new StepId("implement"), executionId, retryNotBefore, 2_700_000),
                TestContext.Current.CancellationToken);
        }

        return (snapshotPath, logPath, executionId, retryNotBefore);
    }

    /// <summary>
    /// An <see cref="FailureClassification.ExhaustedUntil"/> park with no retry obligation: the
    /// offline shape <c>MutationInterface</c> records when neither a vendor reset instant nor a
    /// fallback is available. It deliberately appends no <see cref="FlowEvent.StepRetryScheduled"/>
    /// and never fabricates a reset time.
    /// </summary>
    public static async Task<(string SnapshotPath, string LogPath, ExecutionId ExecutionId)>
        WriteUnknownResetParkedStepFixtureAsync(string testRoot, string roomDirectory)
    {
        Directory.CreateDirectory(roomDirectory);
        var stepId = new StepId("implement");
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("unknown-reset-parked-probe"),
            1,
            [new WorkflowStepDefinition(stepId, "implement", [], ["out"], [], new RetryPolicy(3))]);
        var snapshot = SnapshotBinder.Bind(definition);
        var snapshotPath = Path.Combine(roomDirectory, "snapshot.json");
        await SnapshotBinder.PersistAsync(snapshot, snapshotPath, TestContext.Current.CancellationToken);

        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var executionId = new ExecutionId("exec-unknown-reset-parked-1");
        var request = new ExecutionRequest(
            executionId,
            new WorkflowId("wf-unknown-reset-parked"),
            stepId,
            "implement",
            Inputs: [],
            Outputs: [],
            Timeout: TimeSpan.FromSeconds(30),
            Environment: [],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

        await using var writer = new FlowEventLogWriter(logPath);
        await writer.AppendAsync(
            new FlowEvent.ExecutionRequestAccepted(request),
            TestContext.Current.CancellationToken);
        await writer.AppendAsync(
            new FlowEvent.ExecutionFailed(executionId, FailureClassification.ExhaustedUntil, "quota reset unknown"),
            TestContext.Current.CancellationToken);

        return (snapshotPath, logPath, executionId);
    }
}
