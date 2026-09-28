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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_live_parent_or_child_does_not_persist_an_unresolved_spend_fact(bool registerChild)
    {
        var claim = CreateClaim();
        var writer = new RecordingWriter();
        var registered = new HashSet<ExecutionId> { registerChild ? ChildId : ParentId };

        var result = await MutationInterface.ReconcileGraceClaimsAsync(
            [new FlowEvent.ExecutionRequestAccepted(CreateRequest(ParentId, limits: null)), claim],
            new Dictionary<string, WorkerBinding>(StringComparer.Ordinal),
            registered,
            writer,
            TestContext.Current.CancellationToken);

        Assert.Equal(MutationInterface.GraceReconciliationResult.None, result);
        Assert.Empty(writer.Appended);
    }

    [Fact]
    public async Task An_orphan_claim_appends_one_idempotent_unresolved_spend_fact()
    {
        var claim = CreateClaim();
        var accepted = new FlowEvent.ExecutionRequestAccepted(CreateRequest(ParentId, limits: null));
        var writer = new RecordingWriter();
        var orphan = new List<FlowEvent> { accepted, claim };

        var first = await MutationInterface.ReconcileGraceClaimsAsync(
            orphan, new Dictionary<string, WorkerBinding>(StringComparer.Ordinal),
            new HashSet<ExecutionId>(), writer, TestContext.Current.CancellationToken);

        Assert.Equal(MutationInterface.GraceReconciliationResult.Appended, first);
        var unresolved = Assert.IsType<FlowEvent.GraceTurnSpendUnresolved>(Assert.Single(writer.Appended));
        Assert.Equal(ParentId, unresolved.ParentExecutionId);
        Assert.Equal(ChildId, unresolved.GraceExecutionId);

        orphan.Add(unresolved);
        var repeated = await MutationInterface.ReconcileGraceClaimsAsync(
            orphan, new Dictionary<string, WorkerBinding>(StringComparer.Ordinal),
            new HashSet<ExecutionId>(), writer, TestContext.Current.CancellationToken);

        Assert.Equal(MutationInterface.GraceReconciliationResult.Unresolved, repeated);
        Assert.Single(writer.Appended);
    }

    [Fact]
    public async Task An_unresolved_fact_without_a_claim_is_rejected_before_any_recovery_append()
    {
        var writer = new RecordingWriter();

        await Assert.ThrowsAsync<FlowEventLogReadException>(() => MutationInterface.ReconcileGraceClaimsAsync(
            [new FlowEvent.GraceTurnSpendUnresolved(ParentId, ChildId)],
            new Dictionary<string, WorkerBinding>(StringComparer.Ordinal),
            new HashSet<ExecutionId>(), writer, TestContext.Current.CancellationToken));

        Assert.Empty(writer.Appended);
    }

    [Fact]
    public async Task A_child_cannot_be_both_unresolved_and_completed()
    {
        var claim = CreateClaim();
        var completion = new FlowEvent.GraceTurnCompleted(ChildId, CoreExitReason.Natural, null);
        var unresolved = new FlowEvent.GraceTurnSpendUnresolved(ParentId, ChildId);

        await AssertRejectedWithoutWritesAsync([claim, completion, unresolved]);
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
