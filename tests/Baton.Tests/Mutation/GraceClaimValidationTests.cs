using Baton.Domain;
using Baton.Mutation;
using Baton.Store;

namespace Baton.Tests.Mutation;

public sealed class GraceClaimValidationTests
{
    private static readonly ExecutionId ParentId = new("parent-execution");
    private static readonly ExecutionId ChildId = new("grace-child");

    [Fact]
    public async Task Duplicate_claim_is_rejected_before_recovery_write()
    {
        var claim = CreateClaim();
        await AssertRejectedWithoutWritesAsync([claim, claim]);
    }

    [Fact]
    public async Task Duplicate_completion_is_rejected_before_safety_write()
    {
        var claim = CreateClaim();
        var completion = new FlowEvent.GraceTurnCompleted(ChildId, CoreExitReason.Natural, null);
        await AssertRejectedWithoutWritesAsync([claim, completion, completion]);
    }

    [Fact]
    public async Task Safety_record_for_another_parent_is_rejected_before_parent_arrest_write()
    {
        var claim = CreateClaim();
        var completion = new FlowEvent.GraceTurnCompleted(ChildId, CoreExitReason.Natural, null);
        var safety = new FlowEvent.GraceTurnSafetyRecorded(new ExecutionId("other-parent"), ChildId, true);
        await AssertRejectedWithoutWritesAsync([claim, completion, safety]);
    }

    [Fact]
    public async Task Monitor_claim_without_pending_arrest_is_rejected_before_safety_write()
    {
        var claim = CreateClaim(includePendingArrest: false);
        var completion = new FlowEvent.GraceTurnCompleted(ChildId, CoreExitReason.Natural, null);
        await AssertRejectedWithoutWritesAsync([claim, completion]);
    }

    private static async Task AssertRejectedWithoutWritesAsync(IReadOnlyList<FlowEvent> suffix)
    {
        var events = new List<FlowEvent>
        {
            new FlowEvent.ExecutionRequestAccepted(CreateRequest(ParentId, limits: null)),
        };
        events.AddRange(suffix);
        var writer = new RecordingWriter();

        await Assert.ThrowsAsync<FlowEventLogReadException>(() => MutationInterface.ReconcileGraceClaimsAsync(
            events,
            new Dictionary<string, WorkerBinding>(StringComparer.Ordinal),
            new HashSet<ExecutionId>(),
            writer,
            TestContext.Current.CancellationToken));

        Assert.Empty(writer.Appended);
    }

    private static FlowEvent.GraceTurnClaimed CreateClaim(bool includePendingArrest = true)
    {
        var request = CreateRequest(ChildId, GraceTurn.CreateLimitEvidence(monitorInputsKnown: true));
        var pending = includePendingArrest
            ? new FlowEvent.ExecutionArrested(ParentId, new WorkerUsage(TokensIn: 100), Reason: ArrestReason.TokenBudget)
            : null;
        return new FlowEvent.GraceTurnClaimed(
            ParentId,
            ChildId,
            request,
            new GraceCheckpointEvidence("head", "refs/heads/main", "origin", "refs/heads/main", "tip", "endpoint", "config", "workspace"),
            new GraceParentRecoveryEvidence(true, -1, CoreExitReason.CancelRequested, false, false, pending));
    }

    private static ExecutionRequest CreateRequest(ExecutionId executionId, ExecutionLimitEvidence? limits) =>
        new(
            executionId,
            new WorkflowId("workflow"),
            new StepId("implement"),
            "worker",
            [],
            [],
            TimeSpan.FromMinutes(3),
            [],
            new Dictionary<StepId, ExecutionId>(),
            Adapter: "adapter",
            Model: "model",
            Limits: limits);

    private sealed class RecordingWriter : IEventLogWriter
    {
        public List<FlowEvent> Appended { get; } = [];

        public Task AppendAsync(FlowEvent flowEvent, CancellationToken cancellationToken = default)
        {
            Appended.Add(flowEvent);
            return Task.CompletedTask;
        }
    }
}
