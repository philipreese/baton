using Baton.Artifacts;
using Baton.Cli.Tests.TestSupport;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Projection;
using Baton.Status;
using Baton.Vendors;
using System.Text.Json;

namespace Baton.Cli.Tests;

/// <summary>
/// #1590: <see cref="WorkflowStatusProjector.Project"/>'s no-adapters overload forwarded a null
/// registry straight into the generic <c>Project&lt;TParser&gt;</c>, whose body calls
/// <see cref="ExecutionUsageProjector.BuildByExecutionId{TParser}"/> directly -- bypassing the
/// non-generic <see cref="ExecutionUsageProjector.BuildByExecutionId"/> overload's
/// <c>?? StandardWorkerUsageParsers.Default</c> fallback, because overload resolution against an
/// unbound <c>TParser</c> is performed once and can never bind to a non-generic overload
/// (<c>IReadOnlyDictionary</c> is invariant). <c>Program.cs</c>'s <c>terminal.json</c> write calls
/// exactly this no-adapters overload; <c>StatusCommand</c>'s <c>baton status --json</c> path passes
/// an explicit registry and was unaffected -- so the two surfaces spec/baton.md §3 rules one contract
/// silently diverged.
/// </summary>
public sealed class WorkflowStatusProjectorUsageRoutingTests
{
    private static readonly StepId StepId = new("plan");
    private static readonly WorkflowId WorkflowId = new("wf-1590");

    private static WorkflowDefinitionSnapshot OneStepSnapshot() => new(
        new WorkflowDefinitionSnapshotId("snapshot-1590"),
        new WorkflowTemplateId("usage-routing"),
        WorkflowTemplateVersion: 1,
        Steps: [new WorkflowStepDefinition(StepId, "plan", [], ["out"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]);

    private static ExecutionRequest MakeRequest(ExecutionId executionId) => new(
        executionId, WorkflowId, StepId, "plan",
        Inputs: [], Outputs: [], Timeout: TimeSpan.FromMinutes(10), Environment: [],
        UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>(), Adapter: "claude");

    [Fact]
    public void Status_json_exposes_only_recovery_classified_unknown_grace_children()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"usage-routing-grace-{Guid.NewGuid():N}");
        try
        {
            var parentId = new ExecutionId("parent-grace-status");
            var childId = new ExecutionId("child-grace-status");
            var parentRequest = MakeRequest(parentId);
            var accepted = new FlowEvent.ExecutionRequestAccepted(parentRequest);
            var childRequest = MakeRequest(childId) with
            {
                Limits = Baton.Mutation.GraceTurn.CreateLimitEvidence(monitorInputsKnown: false),
            };
            var baseline = new GraceCheckpointEvidence(
                "head", "refs/heads/main", "origin", "refs/heads/main", "tip", "endpoint", "config", "workspace");
            var pendingParent = new GraceParentRecoveryEvidence(
                true, -1, CoreExitReason.CancelRequested, false, false,
                new FlowEvent.ExecutionArrested(parentId, Reason: ArrestReason.TokenBudget));
            var claim = new FlowEvent.GraceTurnClaimed(parentId, childId, childRequest, baseline, pendingParent);
            var entries = new List<LogEntry>
            {
                new LogEntry.FlowLogEntry(accepted),
                new LogEntry.FlowLogEntry(claim),
                new LogEntry.FlowLogEntry(new FlowEvent.GraceTurnSpendUnresolved(parentId, childId)),
            };
            var state = StateProjector.Project([accepted], OneStepSnapshot());

            var view = WorkflowStatusProjector.Project(state, OneStepSnapshot(), roomDirectory, entries);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(view));
            var row = json.RootElement.GetProperty("unresolvedGraceChildren").GetProperty(childId.Value);

            Assert.Equal("Unresolved", row.GetProperty("outcome").GetString());
            Assert.Equal(parentId.Value, row.GetProperty("predecessorExecutionId").GetString());
            Assert.False(row.TryGetProperty("wallClockMs", out _));
            Assert.False(row.TryGetProperty("tokensIn", out _));
            Assert.False(row.TryGetProperty("billedTokens", out _));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public void Terminal_json_call_shape_reports_the_same_token_usage_status_json_does()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"usage-routing-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("exec-1590");
            var accepted = new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId));
            var start = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

            var entries = new List<LogEntry>
            {
                new LogEntry.FlowLogEntry(accepted),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(executionId, Pid: 1), start),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(executionId, 0, CoreExitReason.Natural), start.AddSeconds(4)),
            };

            var artifactsRoot = Path.Combine(roomDirectory, ArtifactManager.ArtifactsDirectoryName);
            var execDir = ArtifactManager.ResolveOutputDirectory(artifactsRoot, executionId);
            Directory.CreateDirectory(execDir);
            File.WriteAllText(
                Path.Combine(execDir, ExecutionStreamLogger.StdoutLogFileName),
                """{"type":"result","num_turns":6,"usage":{"input_tokens":12,"output_tokens":9}}""" + "\n");

            var state = StateProjector.Project([accepted], OneStepSnapshot());

            // Exactly Program.cs:247's terminal.json call -- no adapters argument, the shape that
            // silently dropped usage before this fix.
            var terminalJsonView = WorkflowStatusProjector.Project(state, OneStepSnapshot(), roomDirectory, entries);

            // Control: identical fixture, explicit registry -- what StatusCommand's `baton status
            // --json` path passes. Must produce tokens, or a fixture that cannot is at fault, not the
            // no-adapters routing under test.
            var statusJsonView = WorkflowStatusProjector.Project(
                state, OneStepSnapshot(), roomDirectory, entries, WorkerAdapterRegistry.Default);

            var statusUsage = Assert.Single(statusJsonView.Steps).Usage;
            Assert.Equal(12, statusUsage?.TokensIn);
            Assert.Equal(9, statusUsage?.TokensOut);

            var terminalUsage = Assert.Single(terminalJsonView.Steps).Usage;
            Assert.Equal(12, terminalUsage?.TokensIn);
            Assert.Equal(9, terminalUsage?.TokensOut);
            Assert.Equal(6, terminalUsage?.Turns);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public void Explicit_adapter_less_registry_yields_no_usage()
    {
        // Negative half of the #1590 fix's contract (WorkflowStatusView.cs's own doc comment): an
        // *omitted*/null registry falls back to StandardWorkerUsageParsers.Default, but an
        // *explicitly-passed*, empty one is not null and must not fall back -- it still yields none.
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"usage-routing-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("exec-1590-negative");
            var accepted = new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId));
            var start = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

            var entries = new List<LogEntry>
            {
                new LogEntry.FlowLogEntry(accepted),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(executionId, Pid: 1), start),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(executionId, 0, CoreExitReason.Natural), start.AddSeconds(4)),
            };

            var artifactsRoot = Path.Combine(roomDirectory, ArtifactManager.ArtifactsDirectoryName);
            var execDir = ArtifactManager.ResolveOutputDirectory(artifactsRoot, executionId);
            Directory.CreateDirectory(execDir);
            File.WriteAllText(
                Path.Combine(execDir, ExecutionStreamLogger.StdoutLogFileName),
                """{"type":"result","num_turns":6,"usage":{"input_tokens":12,"output_tokens":9}}""" + "\n");

            var state = StateProjector.Project([accepted], OneStepSnapshot());

            var view = WorkflowStatusProjector.Project(
                state, OneStepSnapshot(), roomDirectory, entries, new Dictionary<string, IWorkerUsageParser>());

            // WallClockMs is always derived from the ledger's own start/exit timestamps (unrelated to
            // the parser registry), so the step's Usage view is still present -- only the
            // parser-sourced fields are absent.
            var usage = Assert.Single(view.Steps).Usage;
            Assert.NotNull(usage);
            Assert.Null(usage.TokensIn);
            Assert.Null(usage.TokensOut);
            Assert.Null(usage.Turns);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }
}
