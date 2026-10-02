using Baton.Accounting;
using Baton.Artifacts;
using Baton.Cli.Tests.TestSupport;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Mutation;
using Baton.Projection;
using Baton.Status;
using Baton.Vendors;
using System.Text.Json;

namespace Baton.Cli.Tests;

/// <summary>
/// #2559's exclusion and parity controls. These deliberately stay on the supported status/projector
/// path: the new field is useful only if the same captured bytes cannot alter accounting, budgets, or
/// incomplete/running status semantics around it.
/// </summary>
public sealed class ObservedBilledTokenFloorParityTests
{
    private static readonly DateTime Start = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void An_identical_message_id_across_rollover_is_counted_once()
    {
        var fixture = CreateFixture(
            currentLines: [ClaudeAssistantLine("same", 700)],
            rolledLines: [ClaudeAssistantLine("same", 700)]);
        try
        {
            var view = Project(fixture);

            Assert.Equal(700, view.ObservedBilledTokenFloor?.Tokens);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Theory]
    [InlineData("write-marker")]
    [InlineData("rollover-marker")]
    [InlineData("stdout-journal")]
    public void A_loss_appearing_after_an_eligible_memoized_read_invalidates_the_floor_without_rewriting_stdout(
        string loss)
    {
        var fixture = CreateFixture(currentLines: [ClaudeAssistantLine("memo", 700)]);
        try
        {
            var first = Project(fixture);
            Assert.Equal(700, first.ObservedBilledTokenFloor?.Tokens);
            var capturedBytes = File.ReadAllBytes(fixture.StdoutPath);

            IReadOnlyList<LogEntry> entries = fixture.Entries;
            switch (loss)
            {
                case "write-marker":
                    File.WriteAllBytes(Path.Combine(fixture.OutputDirectory, ExecutionStreamLogger.StdoutWriteFailureMarkerFileName), []);
                    break;
                case "rollover-marker":
                    File.WriteAllBytes(Path.Combine(fixture.OutputDirectory, ExecutionStreamLogger.StdoutTruncationMarkerFileName), []);
                    break;
                case "stdout-journal":
                    entries = [
                        .. fixture.Entries,
                        new LogEntry.FlowLogEntry(
                            new FlowEvent.StreamLogLossDeclared(
                                fixture.ExecutionId,
                                ExecutionStreamLogger.StdoutStreamName,
                                ExecutionUsageView.StreamTruncatedByWriteFailureReason,
                                BytesSurrendered: 1,
                                MarkerLanded: false)),
                    ];
                    break;
            }

            var second = Project(fixture, entries);

            Assert.Equal(capturedBytes, File.ReadAllBytes(fixture.StdoutPath));
            Assert.Null(second.ObservedBilledTokenFloor);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public void Missing_current_capture_has_no_floor()
    {
        var fixture = CreateFixture(currentLines: null);
        try
        {
            Assert.Null(Project(fixture).ObservedBilledTokenFloor);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public void An_empty_capture_has_no_floor()
    {
        var fixture = CreateFixture(currentLines: []);
        try
        {
            Assert.Null(Project(fixture).ObservedBilledTokenFloor);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Theory]
    [InlineData("{\"type\":\"system\",\"subtype\":\"init\"}")]
    [InlineData("{\"type\":\"assistant\",\"message\":{\"id\":\"placeholder\",\"usage\":{\"input_tokens\":2,\"output_tokens\":3,\"cache_read_input_tokens\":0}}}")]
    [InlineData("{\"type\":\"assistant\",\"message\":{\"id\":\"read-zero\",\"usage\":{\"cache_read_input_tokens\":0}}}")]
    public void Non_usage_placeholder_only_and_cache_read_zero_are_not_measured_billed_zero(string line)
    {
        var fixture = CreateFixture(currentLines: [line]);
        try
        {
            Assert.Null(Project(fixture).ObservedBilledTokenFloor);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public void An_unreadable_current_capture_has_no_floor()
    {
        var fixture = CreateFixture(currentLines: [ClaudeAssistantLine("locked", 700)]);
        try
        {
            using var locked = new FileStream(fixture.StdoutPath, FileMode.Open, FileAccess.Read, FileShare.None);

            Assert.Null(Project(fixture).ObservedBilledTokenFloor);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public void A_rollover_marker_has_no_floor()
    {
        var fixture = CreateFixture(
            currentLines: [ClaudeAssistantLine("tail", 500)],
            rolledLines: [ClaudeAssistantLine("head", 400)]);
        try
        {
            File.WriteAllBytes(Path.Combine(fixture.OutputDirectory, ExecutionStreamLogger.StdoutTruncationMarkerFileName), []);

            Assert.Null(Project(fixture).ObservedBilledTokenFloor);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public void Terminal_authority_and_loss_keep_the_floor_absent()
    {
        var fixture = CreateFixture(currentLines: [ClaudeAssistantLine("terminal", 700), ClaudeTerminalLine]);
        try
        {
            File.WriteAllBytes(Path.Combine(fixture.OutputDirectory, ExecutionStreamLogger.StdoutWriteFailureMarkerFileName), []);

            var view = Project(fixture);

            Assert.Equal(ExecutionUsageView.StreamTruncatedByWriteFailureReason, view.BilledReconciliationUnavailable);
            Assert.Null(view.BilledTokens);
            Assert.Null(view.ObservedBilledTokenFloor);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public void Codex_usage_never_acquires_the_claude_floor()
    {
        var fixture = CreateFixture(
            adapter: "codex",
            currentLines: ["""{"type":"turn.usage","usage":{"input_tokens":10,"cached_input_tokens":3,"output_tokens":4,"round_trip":1}}"""]);
        try
        {
            Assert.Null(Project(fixture).ObservedBilledTokenFloor);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public void Floor_eligibility_does_not_change_either_serialized_ledger_entry()
    {
        var fixture = CreateFixture(currentLines: [
            ClaudeAssistantLine("first", 700),
            ClaudeAssistantLine("second", 300),
        ]);
        try
        {
            Assert.Equal(1_000, Project(fixture).ObservedBilledTokenFloor?.Tokens);
            var beforeCost = CostLedgerStore.BuildEntries(fixture.Entries, fixture.RoomDirectory, repository: null);
            var beforeQuota = QuotaLedgerStore.BuildEntries(fixture.Entries, fixture.RoomDirectory);

            var conflictingReadPresence = """{"type":"assistant","message":{"id":"first","usage":{"cache_creation_input_tokens":700}}}""";
            IReadOnlyList<LogEntry> afterEntries = fixture.Entries;
            var monitor = new TokenBudgetMonitor(
                budget: null,
                maxToolSteps: null,
                billedRateLimit: null,
                new ClaudeUsageParser());
            monitor.OnStdoutLine(ClaudeAssistantLine("first", 700));
            var firstMonitorReading = monitor.SnapshotUsage().BilledTokens;
            monitor.OnStdoutLine(conflictingReadPresence);
            Assert.Equal(firstMonitorReading, monitor.SnapshotUsage().BilledTokens);
            File.AppendAllText(fixture.StdoutPath, conflictingReadPresence + Environment.NewLine);

            var afterCost = CostLedgerStore.BuildEntries(afterEntries, fixture.RoomDirectory, repository: null);
            var afterQuota = QuotaLedgerStore.BuildEntries(afterEntries, fixture.RoomDirectory);

            Assert.Equal(beforeCost, afterCost);
            Assert.Equal(beforeQuota, afterQuota);
            Assert.Equal(JsonSerializer.Serialize(beforeCost), JsonSerializer.Serialize(afterCost));
            Assert.Equal(JsonSerializer.Serialize(beforeQuota), JsonSerializer.Serialize(afterQuota));
            Assert.Null(Project(fixture, afterEntries).ObservedBilledTokenFloor);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public void The_real_monitor_still_reports_positive_usage_and_triggers_only_at_the_budget()
    {
        var monitor = new TokenBudgetMonitor(
            budget: 1_000,
            maxToolSteps: null,
            billedRateLimit: null,
            new ClaudeUsageParser());

        monitor.OnStdoutLine(ClaudeAssistantLine("under", 700));
        Assert.False(monitor.Arrested);
        Assert.Equal(700, monitor.SnapshotUsage().BilledTokens);

        monitor.OnStdoutLine(ClaudeAssistantLine("over", 300));

        Assert.True(monitor.Arrested);
        Assert.Equal(ArrestReason.TokenBudget, monitor.ArrestReasonValue);
        Assert.Equal(1_000, monitor.SnapshotUsage().BilledTokens);
    }

    [Fact]
    public void Running_status_does_not_expose_a_floor()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"usage-2559-running-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("running-2559");
            var snapshot = OneStepSnapshot();
            var accepted = new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId));
            var entries = new List<LogEntry>
            {
                new LogEntry.FlowLogEntry(accepted),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(executionId, Pid: 1), Start),
            };
            var outputDirectory = ArtifactManager.ResolveOutputDirectory(
                Path.Combine(roomDirectory, ArtifactManager.ArtifactsDirectoryName), executionId);
            Directory.CreateDirectory(outputDirectory);
            File.WriteAllLines(
                Path.Combine(outputDirectory, ExecutionStreamLogger.StdoutLogFileName),
                [ClaudeAssistantLine("running", 700)]);

            var state = StateProjector.Project([accepted], snapshot);
            var view = WorkflowStatusProjector.Project(state, snapshot, roomDirectory, entries);
            var step = Assert.Single(view.Steps);

            Assert.Null(step.Usage);
            Assert.DoesNotContain("observedBilledTokenFloor", JsonSerializer.Serialize(view), StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public void Unresolved_grace_status_does_not_expose_a_floor()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"usage-2559-grace-{Guid.NewGuid():N}");
        try
        {
            var snapshot = OneStepSnapshot();
            var parentId = new ExecutionId("parent-2559");
            var childId = new ExecutionId("child-2559");
            var accepted = new FlowEvent.ExecutionRequestAccepted(MakeRequest(parentId));
            var childRequest = MakeRequest(childId) with
            {
                Limits = GraceTurn.CreateLimitEvidence(monitorInputsKnown: false),
            };
            var baseline = new GraceCheckpointEvidence(
                "head", "refs/heads/main", "origin", "refs/heads/main", "tip", "endpoint", "config", "workspace");
            var pendingParent = new GraceParentRecoveryEvidence(
                true, -1, CoreExitReason.CancelRequested, false, false,
                new FlowEvent.ExecutionArrested(parentId, Reason: ArrestReason.TokenBudget));
            var entries = new List<LogEntry>
            {
                new LogEntry.FlowLogEntry(accepted),
                new LogEntry.FlowLogEntry(new FlowEvent.GraceTurnClaimed(parentId, childId, childRequest, baseline, pendingParent)),
                new LogEntry.FlowLogEntry(new FlowEvent.GraceTurnSpendUnresolved(parentId, childId)),
            };
            var childOutputDirectory = ArtifactManager.ResolveOutputDirectory(
                Path.Combine(roomDirectory, ArtifactManager.ArtifactsDirectoryName), childId);
            Directory.CreateDirectory(childOutputDirectory);
            File.WriteAllLines(
                Path.Combine(childOutputDirectory, ExecutionStreamLogger.StdoutLogFileName),
                [ClaudeAssistantLine("unresolved", 700)]);

            var state = StateProjector.Project([accepted], snapshot);
            var view = WorkflowStatusProjector.Project(state, snapshot, roomDirectory, entries);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(view));
            var row = json.RootElement.GetProperty("unresolvedGraceChildren").GetProperty(childId.Value);

            Assert.Equal("Unresolved", row.GetProperty("outcome").GetString());
            Assert.False(row.TryGetProperty("observedBilledTokenFloor", out _));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    private static ExecutionUsageView Project(Fixture fixture, IReadOnlyList<LogEntry>? entries = null) =>
        Assert.Single(ExecutionUsageProjector.BuildByExecutionId(
            entries ?? fixture.Entries,
            fixture.ArtifactsRootDirectory,
            WorkerAdapterRegistry.Default,
            fixture.RoomDirectory)).Value;

    private static Fixture CreateFixture(
        string adapter = "claude",
        IReadOnlyList<string>? currentLines = null,
        IReadOnlyList<string>? rolledLines = null)
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"usage-2559-parity-{Guid.NewGuid():N}");
        var executionId = new ExecutionId($"exec-2559-{Guid.NewGuid():N}");
        WriteBindings(roomDirectory, ("plan", adapter));
        var entries = new List<LogEntry>
        {
            new LogEntry.FlowLogEntry(new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, adapter))),
            new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(executionId, Pid: 1), Start),
            new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(executionId, 0, CoreExitReason.Natural), Start.AddSeconds(1)),
        };

        var artifactsRootDirectory = Path.Combine(roomDirectory, ArtifactManager.ArtifactsDirectoryName);
        var outputDirectory = ArtifactManager.ResolveOutputDirectory(artifactsRootDirectory, executionId);
        Directory.CreateDirectory(outputDirectory);
        if (rolledLines is not null)
        {
            File.WriteAllLines(Path.Combine(outputDirectory, ExecutionStreamLogger.StdoutRolloverFileName), rolledLines);
        }

        if (currentLines is not null)
        {
            File.WriteAllLines(Path.Combine(outputDirectory, ExecutionStreamLogger.StdoutLogFileName), currentLines);
        }

        return new Fixture(roomDirectory, artifactsRootDirectory, outputDirectory, executionId, entries);
    }

    private static string ClaudeAssistantLine(string messageId, long cacheCreation) =>
        "{\"type\":\"assistant\",\"message\":{\"id\":\"" + messageId
        + "\",\"usage\":{\"input_tokens\":2,\"cache_creation_input_tokens\":" + cacheCreation
        + ",\"cache_read_input_tokens\":0,\"output_tokens\":3}}}";

    private const string ClaudeTerminalLine =
        """{"type":"result","num_turns":2,"usage":{"input_tokens":1,"output_tokens":2,"cache_creation_input_tokens":3},"modelUsage":{"claude-opus-5":{"inputTokens":1000,"outputTokens":500,"cacheReadInputTokens":9000,"cacheCreationInputTokens":4000}}}""";

    private static ExecutionRequest MakeRequest(ExecutionId executionId, string adapter = "claude") => new(
        executionId, new WorkflowId("wf-2559"), new StepId("plan"), "plan",
        Inputs: [], Outputs: [], Timeout: TimeSpan.FromMinutes(10), Environment: [],
        UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>(), Adapter: adapter);

    private static WorkflowDefinitionSnapshot OneStepSnapshot() => new(
        new WorkflowDefinitionSnapshotId("snapshot-2559"),
        new WorkflowTemplateId("usage-2559"),
        WorkflowTemplateVersion: 1,
        Steps: [new WorkflowStepDefinition(
            new StepId("plan"), "plan", [], ["out"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]);

    private static void WriteBindings(string roomDirectory, (string Step, string Adapter) binding)
    {
        Directory.CreateDirectory(roomDirectory);
        File.WriteAllText(
            Path.Combine(roomDirectory, "bindings.json"),
            "{\"plan\":{\"Adapter\":\"" + binding.Adapter + "\"}}");
    }

    private sealed class Fixture(
        string roomDirectory,
        string artifactsRootDirectory,
        string outputDirectory,
        ExecutionId executionId,
        IReadOnlyList<LogEntry> entries) : IDisposable
    {
        public string RoomDirectory { get; } = roomDirectory;
        public string ArtifactsRootDirectory { get; } = artifactsRootDirectory;
        public string OutputDirectory { get; } = outputDirectory;
        public ExecutionId ExecutionId { get; } = executionId;
        public IReadOnlyList<LogEntry> Entries { get; } = entries;
        public string StdoutPath => Path.Combine(OutputDirectory, ExecutionStreamLogger.StdoutLogFileName);

        public void Dispose() => DirectoryCleanup.DeleteRecursively(RoomDirectory);
    }
}
