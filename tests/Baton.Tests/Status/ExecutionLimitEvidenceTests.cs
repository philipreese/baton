using System.Text.Json;
using Baton.Domain;
using Baton.Status;
using Baton.Store;

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

    private static ExecutionRequest Request(ExecutionLimitEvidence? limits = null) =>
        Request(ExecutionId, limits);

    private static ExecutionRequest Request(ExecutionId executionId, ExecutionLimitEvidence? limits) =>
        new(executionId, WorkflowId, StepId, "worker", [], [], TimeSpan.FromMinutes(5), [],
            new Dictionary<StepId, ExecutionId>(), Adapter: "claude", Model: "sonnet", Limits: limits);
}
