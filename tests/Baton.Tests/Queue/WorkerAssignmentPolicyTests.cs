using Baton.Domain;
using Baton.Queue;

namespace Baton.Tests.Queue;

public sealed class WorkerAssignmentPolicyTests
{
    private static readonly IReadOnlySet<DeclaredTaskSize> Sizes = new HashSet<DeclaredTaskSize> { DeclaredTaskSize.Small, DeclaredTaskSize.Medium, DeclaredTaskSize.Large };
    private static WorkerCandidate Candidate(string adapter, CapabilityBand band) =>
        new(adapter, "model", "medium", band, Sizes, new HashSet<string>(), new HashSet<string>());
    private static AssignmentRequest Request(DeclaredTaskSize size = DeclaredTaskSize.Medium) =>
        new("implement", "implement", "tooling", size, new HashSet<string>());
    private static FleetFacts Facts(params (WorkerCandidate Candidate, FleetCandidateFacts Facts)[] values) =>
        new(values.ToDictionary(v => FleetFacts.Key(v.Candidate), v => v.Facts));

    [Fact]
    public void A_legacy_single_candidate_does_not_require_a_declared_size()
    {
        var candidate = Candidate("codex", CapabilityBand.Standard);
        var decision = WorkerAssignmentPolicy.Select(Request(DeclaredTaskSize.Unknown), [candidate],
            Facts((candidate, new(UsageEvidence.Fresh, false, 2))));
        Assert.Equal(candidate, decision.Candidate);
    }

    [Fact]
    public void A_multi_candidate_pool_refuses_an_unknown_size()
    {
        var first = Candidate("codex", CapabilityBand.Standard);
        var decision = WorkerAssignmentPolicy.Select(Request(DeclaredTaskSize.Unknown), [first, Candidate("claude", CapabilityBand.Frontier)], Facts());
        Assert.Equal(AssignmentReason.DeclaredSizeRequired, decision.Reason);
        Assert.Null(decision.Candidate);
    }

    [Fact]
    public void Capability_precedes_weekly_runway_and_roster_order_breaks_ties()
    {
        var capable = Candidate("codex", CapabilityBand.Standard);
        var excessive = Candidate("claude", CapabilityBand.Frontier);
        var tied = Candidate("agy", CapabilityBand.Standard);
        var decision = WorkerAssignmentPolicy.Select(Request(), [capable, excessive, tied], Facts(
            (capable, new(UsageEvidence.Fresh, false, 1)),
            (excessive, new(UsageEvidence.Fresh, false, 100)),
            (tied, new(UsageEvidence.Fresh, false, 1))));
        Assert.Equal(capable, decision.Candidate);
    }

    [Fact]
    public void Fresh_evidence_and_account_wide_hold_fail_closed()
    {
        var candidate = Candidate("codex", CapabilityBand.Standard);
        var decision = WorkerAssignmentPolicy.Select(Request(), [candidate], Facts(
            (candidate, new(UsageEvidence.Fresh, false, 3, AccountWideHeld: true))));
        Assert.Equal(CandidateRejectionReason.AccountWideRunwayHeld, decision.RejectedCandidates.Single().Reason);
    }
}
