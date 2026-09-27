using System.Text.Json;
using System.Text.Json.Nodes;
using Baton.Accounting;
using Baton.Domain;
using Baton.Mutation;
using Baton.Status;
using Baton.Store;
using Baton.Tests.Shared;

namespace Baton.Tests.Status;

public sealed class ExecutionLimitEvidenceTests
{
    private static readonly ExecutionId ExecutionId = new("execution-1");
    private static readonly WorkflowId WorkflowId = new("workflow-1");
    private static readonly StepId StepId = new("step-1");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Artifact_checkpoint_factory_records_only_its_policy_and_monitor_availability(bool monitorInputsKnown)
    {
        var evidence = ArtifactCheckpoint.CreateLimitEvidence(monitorInputsKnown);

        Assert.Equal(ArtifactCheckpoint.WallClockTimeout, evidence.Timeout);
        Assert.Equal(ArtifactCheckpoint.LimitSource, evidence.TimeoutSource);
        Assert.Null(evidence.ChosenKey);
        Assert.Null(evidence.BilledRateLimit);
        Assert.Equal(monitorInputsKnown, evidence.MonitorInputsKnown);
        Assert.Equal(monitorInputsKnown ? ArtifactCheckpoint.TokenBudget : null, evidence.TokenBudget);
        Assert.Equal(monitorInputsKnown ? ArtifactCheckpoint.MaxToolSteps : null, evidence.MaxToolSteps);
        Assert.Equal(monitorInputsKnown ? ArtifactCheckpoint.LimitSource : null, evidence.TokenBudgetSource);
        Assert.Equal(monitorInputsKnown ? ArtifactCheckpoint.LimitSource : null, evidence.MaxToolStepsSource);
    }

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
            MaxToolStepsSource: "role-default",
            MonitorInputsKnown: true);
        var original = new FlowEvent.ExecutionRequestAccepted(Request(evidence));

        var json = JsonSerializer.Serialize<FlowEvent>(original, FlowEventLogJson.Options);
        var roundTripped = Assert.IsType<FlowEvent.ExecutionRequestAccepted>(
            JsonSerializer.Deserialize<FlowEvent>(json, FlowEventLogJson.Options));

        Assert.Equal(evidence, roundTripped.Request.Limits);
        Assert.Contains("TokenBudget", json, StringComparison.Ordinal);
        Assert.Contains("MaxToolSteps", json, StringComparison.Ordinal);
        Assert.Contains("MonitorInputsKnown", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Monitor_availability_survives_wire_status_and_ledger(bool? wireValue, bool expectedKnown)
    {
        var room = Path.Combine(Path.GetTempPath(), $"limit-wire-{Guid.NewGuid():N}");
        Directory.CreateDirectory(room);
        try
        {
            var original = new FlowEvent.ExecutionRequestAccepted(Request(
                new ExecutionLimitEvidence(TimeSpan.FromMinutes(5), null, null, null, MonitorInputsKnown: true)));
            var node = JsonNode.Parse(JsonSerializer.Serialize<FlowEvent>(original, FlowEventLogJson.Options))!;
            var limits = node["Request"]!["Limits"]!.AsObject();
            if (wireValue is { } explicitValue)
                limits["MonitorInputsKnown"] = explicitValue;
            else
                limits.Remove("MonitorInputsKnown");

            var accepted = Assert.IsType<FlowEvent.ExecutionRequestAccepted>(
                JsonSerializer.Deserialize<FlowEvent>(node.ToJsonString(), FlowEventLogJson.Options));
            Assert.Equal(expectedKnown, accepted.Request.Limits!.MonitorInputsKnown);
            Assert.Equal(TimeSpan.FromMinutes(5), accepted.Request.Limits.Timeout);

            var now = DateTime.UtcNow;
            var entries = new List<LogEntry>
            {
                new LogEntry.FlowLogEntry(accepted),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(ExecutionId, 1), now),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(ExecutionId, 0, CoreExitReason.Natural), now.AddSeconds(1)),
            };
            Assert.Equal(expectedKnown,
                ExecutionUsageProjector.BuildByExecutionId(entries, room)[ExecutionId.Value].Limits!.MonitorInputsKnown);
            var repository = RepositoryIdentity.From("https://github.com/example/limits.git", null)!;
            var row = Assert.Single(CostLedgerStore.BuildEntries(entries, room, repository));
            Assert.Equal(expectedKnown, row.Limits!.MonitorInputsKnown);
            var ledgerPath = Path.Combine(room, "ledger.jsonl");
            await CostLedgerStore.AppendAsync([row], ledgerPath, TestContext.Current.CancellationToken);
            Assert.Equal(expectedKnown,
                Assert.Single(await CostLedgerStore.ReadAllAsync(ledgerPath, TestContext.Current.CancellationToken))
                    .Limits!.MonitorInputsKnown);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
        }
    }

    [Fact]
    public void Accepted_event_round_trip_preserves_unavailable_monitor_evidence()
    {
        var unavailable = new ExecutionLimitEvidence(
            TimeSpan.FromMinutes(5), null, null, null, MonitorInputsKnown: false);
        var json = JsonSerializer.Serialize<FlowEvent>(
            new FlowEvent.ExecutionRequestAccepted(Request(unavailable)), FlowEventLogJson.Options);
        var accepted = Assert.IsType<FlowEvent.ExecutionRequestAccepted>(
            JsonSerializer.Deserialize<FlowEvent>(json, FlowEventLogJson.Options));

        Assert.Equal(unavailable, accepted.Request.Limits);
        Assert.False(accepted.Request.Limits!.MonitorInputsKnown);
        Assert.NotEqual(
            new ExecutionLimitEvidence(TimeSpan.FromMinutes(5), null, null, null, MonitorInputsKnown: true),
            accepted.Request.Limits);
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
        var first = new ExecutionLimitEvidence(TimeSpan.FromMinutes(5), 1000, 10, null, MonitorInputsKnown: true);
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
            var first = new ExecutionLimitEvidence(TimeSpan.FromMinutes(5), 1000, 10, null, MonitorInputsKnown: true);
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
            var parentLimits = new ExecutionLimitEvidence(TimeSpan.FromMinutes(5), 1000, 10, null, MonitorInputsKnown: true);
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

    [Fact]
    public async Task Checkpoint_attempted_request_round_trip_projects_its_own_evidence()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"limit-chk-evidence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var logPath = Path.Combine(tempDir, "flow.jsonl");
        try
        {
            var parentLimits = new ExecutionLimitEvidence(
                TimeSpan.FromMinutes(5), 1000, 10, 700, ChosenKey: "parent-profile",
                TimeoutSource: "profile", TokenBudgetSource: "profile", MaxToolStepsSource: "profile",
                MonitorInputsKnown: true);
            var laterParentLimits = parentLimits with
            {
                Timeout = TimeSpan.FromMinutes(2),
                TokenBudget = 2500,
                MaxToolSteps = 25,
                ChosenKey = null,
                TimeoutSource = "dispatch-override",
                TokenBudgetSource = "dispatch-override",
                MaxToolStepsSource = "dispatch-override",
            };
            var childLimits = ArtifactCheckpoint.CreateLimitEvidence(monitorInputsKnown: true);
            var checkpointId = new ExecutionId("checkpoint-evidence");
            var checkpointRequest = Request(checkpointId, childLimits) with
            {
                Timeout = ArtifactCheckpoint.WallClockTimeout,
            };
            await using (var writer = new FlowEventLogWriter(logPath))
            {
                await writer.AppendAsync(
                    new FlowEvent.ExecutionRequestAccepted(Request(ExecutionId, parentLimits)),
                    TestContext.Current.CancellationToken);
                await writer.AppendAsync(new FlowEvent.ArtifactCheckpointAttempted(
                    checkpointId, ExecutionId, ["report.md"], checkpointRequest),
                    TestContext.Current.CancellationToken);
                await writer.AppendAsync(new FlowEvent.StepRebound(
                    StepId, ExecutionId,
                    PreviousAdapter: "codex", PreviousModel: "gpt-5.6-sol",
                    NewAdapter: "codex", NewModel: "gpt-5.6-sol",
                    Reason: "later settings changed the ordinary request",
                    PreviousLimits: parentLimits, NewLimits: laterParentLimits),
                    TestContext.Current.CancellationToken);
            }

            var journal = await new FlowEventLogReader(logPath).ReadAllAsync(TestContext.Current.CancellationToken);
            var attempted = Assert.Single(journal.OfType<FlowEvent.ArtifactCheckpointAttempted>());
            var attemptedRequest = Assert.IsType<ExecutionRequest>(attempted.Request);
            Assert.Equal(childLimits, attemptedRequest.Limits);
            Assert.Equal(checkpointId, attempted.CheckpointExecutionId);
            Assert.Equal(ExecutionId, attempted.PredecessorExecutionId);
            var now = DateTime.UtcNow;
            var entries = journal.Select(entry => (LogEntry)new LogEntry.FlowLogEntry(entry)).ToList();
            entries.Add(new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(ExecutionId, 1), now));
            entries.Add(new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(ExecutionId, -1, CoreExitReason.CancelRequested), now.AddSeconds(1)));
            entries.Add(new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(checkpointId, 2), now.AddSeconds(1)));
            entries.Add(new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(checkpointId, 0, CoreExitReason.Natural), now.AddSeconds(2)));

            var status = ExecutionUsageProjector.BuildByExecutionId(entries, tempDir);
            Assert.Equal(laterParentLimits, status[ExecutionId.Value].Limits);
            Assert.Equal(childLimits, status[checkpointId.Value].Limits);
            Assert.NotEqual(laterParentLimits, status[checkpointId.Value].Limits);

            // StatusCommand uses plain System.Text.Json serialization of these actual status DTOs.
            using var statusJson = JsonDocument.Parse(JsonSerializer.Serialize(status[checkpointId.Value]));
            Assert.Equal(childLimits,
                statusJson.RootElement.GetProperty("limits").Deserialize<ExecutionLimitEvidence>());

            var resolved = ExecutionBindingResolver.Resolve(entries);
            Assert.Equal(laterParentLimits, resolved[ExecutionId.Value].Limits);
            Assert.Equal(childLimits, resolved[checkpointId.Value].Limits);

            var rows = CostLedgerStore.BuildEntries(
                entries, tempDir,
                RepositoryIdentity.From("https://github.com/example/checkpoint-limits.git", null)!);
            var childRow = Assert.Single(rows, row => row.Execution == checkpointId.Value);
            Assert.Equal(childLimits, childRow.Limits);
            var correction = CostLedgerStore.BuildResolutionRow(
                [childRow], BatonPaths.RecordKey(tempDir), ConductorResolution.Reject, "checkpoint limits review");
            Assert.Equal(childLimits, correction!.Limits);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempDir);
        }
    }

    [Fact]
    public void Historical_checkpoint_attempts_without_request_or_limits_stay_unknown()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"limit-chk-legacy-{Guid.NewGuid():N}");
        try
        {
            var parentLimits = new ExecutionLimitEvidence(TimeSpan.FromMinutes(5), 1000, 10, null, MonitorInputsKnown: true);
            var laterParentLimits = parentLimits with
            {
                TokenBudget = 2500,
                TokenBudgetSource = "dispatch-override",
            };
            var missingRequestId = new ExecutionId("checkpoint-missing-request");
            var nullLimitsId = new ExecutionId("checkpoint-null-limits");
            var noRequest = new FlowEvent.ArtifactCheckpointAttempted(
                missingRequestId, ExecutionId, ["report.md"]);
            var wire = JsonSerializer.Serialize<FlowEvent>(noRequest, FlowEventLogJson.Options);
            var wireNode = JsonNode.Parse(wire)!.AsObject();
            wireNode.Remove("Request");
            var absentRequest = Assert.IsType<FlowEvent.ArtifactCheckpointAttempted>(
                JsonSerializer.Deserialize<FlowEvent>(wireNode.ToJsonString(), FlowEventLogJson.Options));
            Assert.Null(absentRequest.Request);
            var nullLimitsRequest = Request(nullLimitsId, limits: null);
            var start = DateTime.UtcNow;
            var entries = new List<LogEntry>
            {
                new LogEntry.FlowLogEntry(new FlowEvent.ExecutionRequestAccepted(Request(ExecutionId, parentLimits))),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(ExecutionId, 1), start),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(ExecutionId, -1, CoreExitReason.CancelRequested), start.AddSeconds(1)),
                new LogEntry.FlowLogEntry(absentRequest),
                new LogEntry.FlowLogEntry(new FlowEvent.ArtifactCheckpointAttempted(
                    nullLimitsId, ExecutionId, ["report.md"], nullLimitsRequest)),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(missingRequestId, 2), start.AddSeconds(1)),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(missingRequestId, 0, CoreExitReason.Natural), start.AddSeconds(2)),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(nullLimitsId, 3), start.AddSeconds(2)),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(nullLimitsId, 0, CoreExitReason.Natural), start.AddSeconds(3)),
                new LogEntry.FlowLogEntry(new FlowEvent.StepRebound(
                    StepId, ExecutionId, "codex", "gpt-5.6-sol", "codex", "gpt-5.6-sol",
                    "later binding settings changed", parentLimits, laterParentLimits)),
            };

            var status = ExecutionUsageProjector.BuildByExecutionId(entries, tempDir);
            Assert.Equal(laterParentLimits, status[ExecutionId.Value].Limits);
            Assert.Null(status[missingRequestId.Value].Limits);
            Assert.Null(status[nullLimitsId.Value].Limits);

            foreach (var historicalId in new[] { missingRequestId, nullLimitsId })
            {
                using var statusJson = JsonDocument.Parse(JsonSerializer.Serialize(status[historicalId.Value]));
                Assert.False(statusJson.RootElement.TryGetProperty("limits", out _));
            }

            var resolved = ExecutionBindingResolver.Resolve(entries);
            Assert.Equal(laterParentLimits, resolved[ExecutionId.Value].Limits);
            Assert.Null(resolved.GetValueOrDefault(missingRequestId.Value).Limits);
            Assert.Null(resolved[nullLimitsId.Value].Limits);
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
