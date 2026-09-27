using System.Text.Json;
using Baton.Domain;
using Baton.Status;
using Baton.Store;
using Baton.Tests.Shared;

namespace Baton.Tests.Status;

public sealed class ExecutionLimitEvidenceTests
{
    private static readonly ExecutionId ExecutionId = new("execution-1");
    private static readonly WorkflowId WorkflowId = new("workflow-1");
    private static readonly StepId StepId = new("step-1");

    [Fact]
    public void Accepted_event_round_trip_preserves_known_unlimited_brakes_and_sources()
    {
        var evidence = new ExecutionLimitEvidence(
            TimeSpan.FromMinutes(5),
            TokenBudget: null,
            MaxToolSteps: null,
            BilledRateLimit: 1000,
            ChosenKey: "claude/sonnet/review/medium",
            TimeoutSource: "profile",
            TokenBudgetSource: "role-default",
            MaxToolStepsSource: "role-default");
        var original = new FlowEvent.ExecutionRequestAccepted(Request(evidence));

        var json = JsonSerializer.Serialize<FlowEvent>(original, FlowEventLogJson.Options);
        var roundTripped = Assert.IsType<FlowEvent.ExecutionRequestAccepted>(
            JsonSerializer.Deserialize<FlowEvent>(json, FlowEventLogJson.Options));

        Assert.Equal(evidence, roundTripped.Request.Limits);
        Assert.Contains("TokenBudget", json, StringComparison.Ordinal);
        Assert.Contains("MaxToolSteps", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Legacy_request_without_evidence_remains_unknown()
    {
        var original = new FlowEvent.ExecutionRequestAccepted(Request());
        var json = JsonSerializer.Serialize<FlowEvent>(original, FlowEventLogJson.Options);
        var roundTripped = Assert.IsType<FlowEvent.ExecutionRequestAccepted>(
            JsonSerializer.Deserialize<FlowEvent>(json, FlowEventLogJson.Options));

        Assert.Null(roundTripped.Request.Limits);
    }

    [Fact]
    public void Rebound_resolution_projects_latest_limits_without_borrowing_another_execution()
    {
        var first = new ExecutionLimitEvidence(TimeSpan.FromMinutes(5), 1000, 10, null);
        var latest = first with { TokenBudget = 2000 };
        var other = new ExecutionId("execution-2");
        var entries = new List<LogEntry>
        {
            new LogEntry.FlowLogEntry(new FlowEvent.ExecutionRequestAccepted(Request(first))),
            new LogEntry.FlowLogEntry(new FlowEvent.ExecutionRequestAccepted(Request(other, first))),
            new LogEntry.FlowLogEntry(new FlowEvent.StepRebound(
                StepId, ExecutionId, "claude", "sonnet", "claude", "sonnet",
                "changed monitor inputs", first, latest)),
        };

        var resolved = ExecutionBindingResolver.Resolve(entries);

        Assert.Equal(latest, resolved[ExecutionId.Value].Limits);
        Assert.Equal(first, resolved[other.Value].Limits);
    }

    [Fact]
    public void Status_projection_preserves_immutable_limits_updates_on_rebound_and_keeps_legacy_null()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"limit-status-{Guid.NewGuid():N}");
        try
        {
            var first = new ExecutionLimitEvidence(TimeSpan.FromMinutes(5), 1000, 10, null);
            var latest = first with { TokenBudget = 2000 };
            var legacyId = new ExecutionId("legacy-1");
            var start = DateTime.UtcNow;

            var entries = new List<LogEntry>
            {
                new LogEntry.FlowLogEntry(new FlowEvent.ExecutionRequestAccepted(Request(ExecutionId, first))),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(ExecutionId, 1), start),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(ExecutionId, 0, CoreExitReason.Natural), start.AddSeconds(1)),

                new LogEntry.FlowLogEntry(new FlowEvent.ExecutionRequestAccepted(Request(legacyId, limits: null))),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(legacyId, 2), start),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(legacyId, 0, CoreExitReason.Natural), start.AddSeconds(1)),

                new LogEntry.FlowLogEntry(new FlowEvent.StepRebound(
                    StepId, ExecutionId, "claude", "sonnet", "claude", "sonnet",
                    "changed monitor inputs", first, latest)),
            };

            var views = ExecutionUsageProjector.BuildByExecutionId(entries, tempDir);

            Assert.Equal(latest, views[ExecutionId.Value].Limits);
            Assert.Null(views[legacyId.Value].Limits);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempDir);
        }
    }

    [Fact]
    public void Status_projection_isolates_checkpoint_child_limits_from_predecessor()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"limit-chk-{Guid.NewGuid():N}");
        try
        {
            var parentLimits = new ExecutionLimitEvidence(TimeSpan.FromMinutes(5), 1000, 10, null);
            var checkpointId = new ExecutionId("checkpoint-1");
            var start = DateTime.UtcNow;

            var checkpointRequest = Request(checkpointId, limits: null);

            var entries = new List<LogEntry>
            {
                new LogEntry.FlowLogEntry(new FlowEvent.ExecutionRequestAccepted(Request(ExecutionId, parentLimits))),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(ExecutionId, 1), start),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(ExecutionId, -1, CoreExitReason.CancelRequested), start.AddSeconds(1)),
                new LogEntry.FlowLogEntry(new FlowEvent.ArtifactCheckpointAttempted(
                    checkpointId, ExecutionId, ["report.md"], checkpointRequest)),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(checkpointId, 2), start.AddSeconds(1)),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(checkpointId, 0, CoreExitReason.Natural), start.AddSeconds(2)),
                new LogEntry.FlowLogEntry(new FlowEvent.ArtifactCheckpointCompleted(
                    checkpointId, CoreExitReason.Natural)),
            };

            var views = ExecutionUsageProjector.BuildByExecutionId(entries, tempDir);

            Assert.Equal(parentLimits, views[ExecutionId.Value].Limits);
            Assert.Null(views[checkpointId.Value].Limits);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempDir);
        }
    }

    private static ExecutionRequest Request(ExecutionLimitEvidence? limits = null) =>
        Request(ExecutionId, limits);

    private static ExecutionRequest Request(ExecutionId executionId, ExecutionLimitEvidence? limits) =>
        new(executionId, WorkflowId, StepId, "worker", [], [], TimeSpan.FromMinutes(5), [],
            new Dictionary<StepId, ExecutionId>(), Adapter: "claude", Model: "sonnet", Limits: limits);
}
