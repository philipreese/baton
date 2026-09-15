using System.Text.Json;
using Baton.Artifacts;
using Baton.Cli;
using Baton.Cli.Tests.TestSupport;
using Baton.Domain;
using Baton.Mutation;
using Baton.Projection;
using Baton.Status;
using Baton.Store;
using Baton.Templates;

namespace Baton.Cli.Tests;

/// <summary>
/// #1701: <c>baton status --json</c> must surface a verify failure's own per-member output
/// (<see cref="Baton.Domain.StepState.IndeterminateVerifyTail"/>), not only the one-line
/// member-name summary -- this is the last hop of that fix (<c>StepState</c> to
/// <see cref="WorkflowStatusStepView.VerifyTail"/>'s JSON shape), which the projection-level tests in
/// <c>StateProjectorTests</c> do not reach.
/// </summary>
public sealed class WorkflowStatusProjectorVerifyTailTests
{
    private static readonly StepId StepId = new("implement");
    private static readonly WorkflowId WorkflowId = new("wf-1701");

    private static WorkflowDefinitionSnapshot OneStepSnapshot() => new(
        new WorkflowDefinitionSnapshotId("snapshot-1701"),
        new WorkflowTemplateId("verify-tail"),
        WorkflowTemplateVersion: 1,
        Steps: [new WorkflowStepDefinition(StepId, "implement", [], ["out"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]);

    private static ExecutionRequest MakeRequest(ExecutionId executionId) => new(
        executionId, WorkflowId, StepId, "implement",
        Inputs: [], Outputs: [], Timeout: TimeSpan.FromMinutes(10), Environment: [],
        UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>(), Adapter: "claude");

    [Fact]
    public void A_VerifyFailed_steps_own_output_reaches_the_verifyTail_JSON_field()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"verify-tail-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("exec-1701");
            var events = new FlowEvent[]
            {
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId)),
                new FlowEvent.VerifyFailed(
                    executionId, ["tool-refresh-selftest"],
                    "FAILED: could not write current pointer ... [WinError 5] Access is denied"),
            };

            var state = StateProjector.Project(events, OneStepSnapshot());
            var view = WorkflowStatusProjector.Project(state, OneStepSnapshot(), roomDirectory);

            var step = Assert.Single(view.Steps);
            Assert.Equal("VerifyFailed", step.IndeterminateProducerKind);
            Assert.Equal(
                "FAILED: could not write current pointer ... [WinError 5] Access is denied",
                step.VerifyTail);

            // F2 (#1711 review): the claim is about the `verifyTail` JSON key, not just the CLR
            // property -- a rename of [JsonPropertyName("verifyTail")] must fail this test.
            var json = System.Text.Json.JsonSerializer.Serialize(step);
            Assert.Contains(
                "\"verifyTail\":\"FAILED: could not write current pointer ... [WinError 5] Access is denied\"",
                json);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public void An_Arrested_step_carries_no_verifyTail_nothing_truncated_to_recover()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"verify-tail-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("exec-1701-arrest");
            var events = new FlowEvent[]
            {
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId)),
                new FlowEvent.ExecutionArrested(executionId, new WorkerUsage(TokensIn: 500_000, TokensOut: 120_000), ["manage_task"]),
            };

            var state = StateProjector.Project(events, OneStepSnapshot());
            var view = WorkflowStatusProjector.Project(state, OneStepSnapshot(), roomDirectory);

            var step = Assert.Single(view.Steps);
            Assert.Equal("Arrested", step.IndeterminateProducerKind);
            Assert.Null(step.VerifyTail);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task Delivery_status_keeps_the_earlier_handoff_separate_and_the_later_stamp_authoritative_in_status_and_sentinel()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"delivery-status-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("exec-delivery-status");
            var snapshot = OneStepSnapshot();
            Directory.CreateDirectory(roomDirectory);
            await SnapshotBinder.PersistAsync(
                snapshot, Path.Combine(roomDirectory, BatonPaths.SnapshotFileName), TestContext.Current.CancellationToken);
            await using (var writer = new FlowEventLogWriter(Path.Combine(roomDirectory, BatonPaths.FlowLogFileName)))
            {
                await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId)), TestContext.Current.CancellationToken);
                await writer.AppendAsync(new FlowEvent.ExecutionSucceeded(executionId), TestContext.Current.CancellationToken);
            }

            var outputDirectory = ArtifactManager.ResolveOutputDirectory(
                Path.Combine(roomDirectory, ArtifactManager.ArtifactsDirectoryName), executionId);
            Directory.CreateDirectory(outputDirectory);
            // Deliberately conflicting prose. Status exposes it only as an artifact link; it does
            // not parse this sentence or let it outrank the later machine observation.
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "changes.md"), "Worker handoff: push failed.", TestContext.Current.CancellationToken);
            var observedAt = DateTimeOffset.UtcNow.AddMinutes(1).ToString("O");
            await File.WriteAllTextAsync(
                Path.Combine(outputDirectory, DeliveryVerifier.DeliveryEvidenceFileName),
                $$"""{"observedAt":"{{observedAt}}","localHead":"later-head","branch":"lane","remoteHead":"later-head","pullRequestNumber":2309,"verification":"Passed","failingMembers":null,"verificationReason":null,"observationProblem":null}""",
                TestContext.Current.CancellationToken);

            var reader = new FlowEventLogReader(Path.Combine(roomDirectory, BatonPaths.FlowLogFileName));
            var entries = await reader.ReadAllEntriesWithTimestampsAsync(TestContext.Current.CancellationToken);
            var state = StateProjector.Project(entries.OfType<LogEntry.FlowLogEntry>().Select(entry => entry.Event).ToList(), snapshot);
            var view = await WorkflowStatusProjector.WithDeliveryEvidenceAsync(
                WorkflowStatusProjector.Project(state, snapshot, roomDirectory, entries), entries, roomDirectory,
                TestContext.Current.CancellationToken);
            await TerminalSentinelWriter.WriteAsync(roomDirectory, view, TestContext.Current.CancellationToken);

            var projected = Assert.Single(view.Delivery!);
            Assert.Equal("passed", projected.AuthoritativeObservation.State);
            Assert.Equal("later-head", projected.AuthoritativeObservation.RemoteHead);
            Assert.NotNull(projected.Handoff);
            Assert.Equal(Path.Combine("artifacts", $"execution_{executionId.Value}", "changes.md"), projected.Handoff!.Artifact);
            Assert.True(DateTimeOffset.Parse(projected.Handoff.AsOf) < DateTimeOffset.Parse(projected.AuthoritativeObservation.ObservedAt!));

            using var output = new StringWriter();
            await StatusCommand.ExecuteAsync(new StatusOptions(roomDirectory, Json: true), output, TestContext.Current.CancellationToken);
            var status = JsonSerializer.Deserialize<WorkflowStatusView>(output.ToString());
            Assert.Equal(view.Delivery, status!.Delivery);
            var sentinel = await TerminalSentinelWriter.TryReadAsync(roomDirectory, TestContext.Current.CancellationToken);
            Assert.Equal(view.Delivery, sentinel!.Delivery);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task Missing_or_corrupt_delivery_evidence_is_unknown_without_a_status_reprobe()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"delivery-status-unknown-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("exec-delivery-unknown");
            var snapshot = OneStepSnapshot();
            var entries = new LogEntry[]
            {
                new LogEntry.FlowLogEntry(new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId)), DateTime.UtcNow),
            };
            var outputDirectory = ArtifactManager.ResolveOutputDirectory(
                Path.Combine(roomDirectory, ArtifactManager.ArtifactsDirectoryName), executionId);
            Directory.CreateDirectory(outputDirectory);
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "changes.md"), "handoff only", TestContext.Current.CancellationToken);
            var baseView = WorkflowStatusProjector.Project(StateProjector.Project([((LogEntry.FlowLogEntry)entries[0]).Event], snapshot), snapshot, roomDirectory, entries);

            var missing = await WorkflowStatusProjector.WithDeliveryEvidenceAsync(baseView, entries, roomDirectory, TestContext.Current.CancellationToken);
            Assert.Equal("unknown", Assert.Single(missing.Delivery!).AuthoritativeObservation.State);
            Assert.Contains("absent", missing.Delivery![0].AuthoritativeObservation.Reason, StringComparison.Ordinal);

            await File.WriteAllTextAsync(Path.Combine(outputDirectory, DeliveryVerifier.DeliveryEvidenceFileName), "not json", TestContext.Current.CancellationToken);
            var corrupt = await WorkflowStatusProjector.WithDeliveryEvidenceAsync(baseView, entries, roomDirectory, TestContext.Current.CancellationToken);
            Assert.Equal("unknown", Assert.Single(corrupt.Delivery!).AuthoritativeObservation.State);
            Assert.Contains("unreadable", corrupt.Delivery![0].AuthoritativeObservation.Reason, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }
}
