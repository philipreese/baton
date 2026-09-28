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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Grace_factory_records_only_its_fixed_policy_and_actual_monitor_availability(bool monitorInputsKnown)
    {
        var evidence = GraceTurn.CreateLimitEvidence(monitorInputsKnown);

        Assert.Equal(GraceTurn.WallClockTimeout, evidence.Timeout);
        Assert.Equal(GraceTurn.LimitSource, evidence.TimeoutSource);
        Assert.Null(evidence.ChosenKey);
        Assert.Null(evidence.BilledRateLimit);
        Assert.Equal(monitorInputsKnown, evidence.MonitorInputsKnown);
        Assert.Equal(monitorInputsKnown ? GraceTurn.TokenBudget : null, evidence.TokenBudget);
        Assert.Equal(monitorInputsKnown ? GraceTurn.MaxToolSteps : null, evidence.MaxToolSteps);
        Assert.Equal(monitorInputsKnown ? GraceTurn.LimitSource : null, evidence.TokenBudgetSource);
        Assert.Equal(monitorInputsKnown ? GraceTurn.LimitSource : null, evidence.MaxToolStepsSource);
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
    public void Grace_child_status_and_cost_keep_claim_limits_and_parent_link_after_parent_rebound()
    {
        var room = Path.Combine(Path.GetTempPath(), $"grace-limits-{Guid.NewGuid():N}");
        Directory.CreateDirectory(room);
        try
        {
            var parentLimits = new ExecutionLimitEvidence(
                TimeSpan.FromMinutes(2), 1000, 10, null, ChosenKey: "profile-a",
                TimeoutSource: "profile", TokenBudgetSource: "profile",
                MaxToolStepsSource: "profile", MonitorInputsKnown: true);
            var changedParentLimits = parentLimits with { TokenBudget = 9999, ChosenKey = "profile-b" };
            var childId = new ExecutionId("grace-child");
            var parentRequest = Request(ExecutionId, parentLimits);
            var childRequest = Request(childId, GraceTurn.CreateLimitEvidence(monitorInputsKnown: true));
            var baseline = new GraceCheckpointEvidence(
                "head", "refs/heads/main", "origin", "refs/heads/main", "tip", "endpoint-hash", "config-hash", "workspace-hash");
            var pendingParent = new GraceParentRecoveryEvidence(
                true, -1, CoreExitReason.CancelRequested, false, false,
                new FlowEvent.ExecutionArrested(ExecutionId, Reason: ArrestReason.TokenBudget));
            var startedAt = DateTime.UtcNow;
            var entries = new List<LogEntry>
            {
                new LogEntry.FlowLogEntry(new FlowEvent.ExecutionRequestAccepted(parentRequest)),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(ExecutionId, 1), startedAt),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(ExecutionId, -1, CoreExitReason.CancelRequested), startedAt.AddMilliseconds(500)),
                new LogEntry.FlowLogEntry(new FlowEvent.GraceTurnClaimed(ExecutionId, childId, childRequest, baseline, pendingParent)),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(childId, 2), startedAt),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(childId, 0, CoreExitReason.Natural), startedAt.AddSeconds(1)),
                new LogEntry.FlowLogEntry(new FlowEvent.GraceTurnCompleted(childId, CoreExitReason.Natural, null)),
                new LogEntry.FlowLogEntry(new FlowEvent.GraceTurnSafetyRecorded(ExecutionId, childId, true)),
                new LogEntry.FlowLogEntry(new FlowEvent.StepRebound(
                    StepId, ExecutionId, "worker", "sonnet", "worker", "sonnet",
                    "later parent binding", parentLimits, changedParentLimits)),
            };

            var status = ExecutionUsageProjector.BuildByExecutionId(entries, room);
            Assert.Equal(changedParentLimits, status[ExecutionId.Value].Limits);
            Assert.Equal(childRequest.Limits, status[childId.Value].Limits);
            Assert.Equal(ExecutionId.Value, status[childId.Value].PredecessorExecutionId);
            Assert.Equal("NaturalExit", status[childId.Value].Outcome);

            var repository = RepositoryIdentity.From("https://github.com/example/grace.git", null)!;
            var cost = Assert.Single(CostLedgerStore.BuildEntries(entries, room, repository),
                entry => entry.Execution == childId.Value);
            Assert.Equal(childId.Value, cost.Execution);
            Assert.Equal(ExecutionId.Value, cost.PredecessorExecution);
            Assert.Equal(childRequest.Limits, cost.Limits);
            Assert.Equal("NaturalExit", cost.Outcome);

            var resolved = ExecutionBindingResolver.Resolve(entries);
            Assert.Equal(changedParentLimits, resolved[ExecutionId.Value].Limits);
            Assert.Equal(childRequest.Limits, resolved[childId.Value].Limits);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
        }
    }

    [Fact]
    public async Task Only_a_recovery_classified_orphan_claim_projects_an_unknown_child_row()
    {
        var room = Path.Combine(Path.GetTempPath(), $"grace-unresolved-{Guid.NewGuid():N}");
        Directory.CreateDirectory(room);
        try
        {
            var childId = new ExecutionId("grace-child-unresolved");
            var parentRequest = Request(ExecutionId, null);
            var childLimits = GraceTurn.CreateLimitEvidence(monitorInputsKnown: true);
            var childRequest = Request(childId, childLimits);
            var baseline = new GraceCheckpointEvidence(
                "head", "refs/heads/main", "origin", "refs/heads/main", "tip", "endpoint-hash", "config-hash", "workspace-hash");
            var pendingParent = new GraceParentRecoveryEvidence(
                true, -1, CoreExitReason.CancelRequested, false, false,
                new FlowEvent.ExecutionArrested(ExecutionId, Reason: ArrestReason.TokenBudget));
            var claim = new FlowEvent.GraceTurnClaimed(ExecutionId, childId, childRequest, baseline, pendingParent);
            var accepted = new FlowEvent.ExecutionRequestAccepted(parentRequest);
            var childStartedAt = DateTime.UtcNow;
            var entries = new List<LogEntry>
            {
                new LogEntry.FlowLogEntry(accepted),
                new LogEntry.FlowLogEntry(claim),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(childId, 42), childStartedAt),
            };

            // While the dispatch is live, ordinary status/ledger projection must not freeze an
            // append-only unknown row that would suppress the child's eventual observed completion.
            var liveProjection = ExecutionUsageProjector.BuildByExecutionId(entries, room);
            Assert.DoesNotContain(childId.Value, liveProjection.Keys);
            Assert.DoesNotContain(childId.Value, QuotaLedgerStore.BuildEntries(entries, room).Select(row => row.Execution));

            var liveCostRows = CostLedgerStore.BuildEntries(
                entries, room, RepositoryIdentity.From("https://github.com/example/grace.git", null)!);
            Assert.DoesNotContain(childId.Value, liveCostRows.Select(row => row.Execution));

            var completedEntries = entries.ToList();
            completedEntries.Add(new LogEntry.FlowLogEntry(
                new FlowEvent.GraceTurnCompleted(childId, CoreExitReason.Natural, new WorkerUsage(TokensIn: 4))));
            completedEntries.Add(new LogEntry.CoreLogEntry(
                new CoreEvent.ExecutionExited(childId, 0, CoreExitReason.Natural), DateTime.UtcNow.AddMilliseconds(1250)));
            Assert.Equal("NaturalExit",
                ExecutionUsageProjector.BuildByExecutionId(completedEntries, room)[childId.Value].Outcome);

            entries.Add(new LogEntry.FlowLogEntry(
                new FlowEvent.GraceTurnSpendUnresolved(ExecutionId, childId)));
            var orphanProjection = ExecutionUsageProjector.BuildByExecutionId(entries, room);
            var child = orphanProjection[childId.Value];
            Assert.Null(child.WallClockMs);
            Assert.Equal("Unresolved", child.Outcome);
            Assert.Equal(ExecutionId.Value, child.PredecessorExecutionId);
            Assert.Equal(childLimits, child.Limits);
            Assert.Null(child.TokensIn);
            Assert.Null(child.TokensOut);
            Assert.Null(child.BilledTokens);
            Assert.Null(child.ExitReason);
            Assert.Null(child.ArrestReason);

            var quota = Assert.Single(QuotaLedgerStore.BuildEntries(entries, room), row => row.Execution == childId.Value);
            Assert.Equal("Unresolved", quota.Outcome);
            Assert.Equal(ExecutionId.Value, quota.PredecessorExecution);
            Assert.Null(quota.At);
            Assert.Null(quota.WallClockMs);
            Assert.Null(quota.TokensIn);
            Assert.Null(quota.TokensOut);

            var repository = RepositoryIdentity.From("https://github.com/example/grace.git", null)!;
            var cost = Assert.Single(CostLedgerStore.BuildEntries(entries, room, repository), row => row.Execution == childId.Value);
            Assert.Equal("Unresolved", cost.Outcome);
            Assert.Equal(ExecutionId.Value, cost.PredecessorExecution);
            Assert.Equal(childStartedAt, cost.StartedAt);
            Assert.Null(cost.EndedAt);
            Assert.Null(cost.WallClockMs);
            Assert.Null(cost.TokensIn);
            Assert.Null(cost.TokensOut);
            Assert.Null(cost.ApiEquivalentUsd);
            Assert.Null(cost.PlanMeterEstimateUsd);

            var quotaPath = Path.Combine(room, "quota-ledger.jsonl");
            var costPath = Path.Combine(room, "cost-ledger.jsonl");
            await QuotaLedgerStore.RebuildAsync(
                QuotaLedgerStore.BuildEntries(entries, room), quotaPath, TestContext.Current.CancellationToken);
            await QuotaLedgerStore.RebuildAsync(
                QuotaLedgerStore.BuildEntries(entries, room), quotaPath, TestContext.Current.CancellationToken);
            await CostLedgerStore.AppendAsync(
                CostLedgerStore.BuildEntries(entries, room, repository), costPath, TestContext.Current.CancellationToken);
            await CostLedgerStore.AppendAsync(
                CostLedgerStore.BuildEntries(entries, room, repository), costPath, TestContext.Current.CancellationToken);
            Assert.Single(await QuotaLedgerStore.ReadDistinctByExecutionAsync(quotaPath, TestContext.Current.CancellationToken),
                row => row.Execution == childId.Value);
            Assert.Single(await CostLedgerStore.ReadAllAsync(costPath, TestContext.Current.CancellationToken),
                row => row.Execution == childId.Value);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
        }
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
