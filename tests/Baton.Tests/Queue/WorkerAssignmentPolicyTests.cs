using Baton.Domain;
using Baton.Queue;

namespace Baton.Tests.Queue;

public sealed class WorkerAssignmentPolicyTests
{
    private static readonly IReadOnlySet<DeclaredTaskSize> Sizes = new HashSet<DeclaredTaskSize> { DeclaredTaskSize.Small, DeclaredTaskSize.Medium, DeclaredTaskSize.Large };
    private static WorkerCandidate Candidate(string adapter, CapabilityBand band) =>
        new(adapter, "model", "medium", band, Sizes, new HashSet<string>(), new HashSet<string>());
    private static AssignmentRequest Request(
        DeclaredTaskSize size = DeclaredTaskSize.Medium,
        CapabilityBand requiredCapabilityBand = CapabilityBand.Standard) =>
        new("implement", "implement", "tooling", size, new HashSet<string>(), requiredCapabilityBand);
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
    public void A_valid_exact_override_is_checked_before_the_multi_candidate_size_refusal()
    {
        var chosen = Candidate("codex", CapabilityBand.Standard);
        var other = Candidate("claude", CapabilityBand.Frontier);
        var request = Request(DeclaredTaskSize.Unknown) with
        {
            Override = new AssignmentOverride(chosen.Adapter, chosen.Model, chosen.Effort, "operator pin"),
        };

        var decision = WorkerAssignmentPolicy.Select(request, [chosen, other], Facts(
            (chosen, new(UsageEvidence.Fresh, false, 2))));

        Assert.Equal(chosen, decision.Candidate);
        Assert.Equal(AssignmentReason.OperatorOverride, decision.Reason);
    }

    [Fact]
    public void A_candidate_below_the_required_capability_floor_loses_despite_abundant_runway()
    {
        var incapable = Candidate("agy", CapabilityBand.Minimal);
        var capable = Candidate("codex", CapabilityBand.Standard);
        var decision = WorkerAssignmentPolicy.Select(Request(), [incapable, capable], Facts(
            (incapable, new(UsageEvidence.Fresh, false, 100)),
            (capable, new(UsageEvidence.Fresh, false, 1))));

        Assert.Equal(capable, decision.Candidate);
        Assert.Contains(decision.RejectedCandidates,
            rejected => rejected.Candidate == incapable && rejected.Reason == CandidateRejectionReason.BelowCapabilityFloor);
    }

    [Fact]
    public void Weekly_runway_and_roster_order_rank_only_the_lowest_sufficient_band()
    {
        var first = Candidate("codex", CapabilityBand.Standard);
        var frontier = Candidate("claude", CapabilityBand.Frontier);
        var second = Candidate("agy", CapabilityBand.Standard);
        var decision = WorkerAssignmentPolicy.Select(Request(), [first, frontier, second], Facts(
            (first, new(UsageEvidence.Fresh, false, 1)),
            (frontier, new(UsageEvidence.Fresh, false, 100)),
            (second, new(UsageEvidence.Fresh, false, 2))));

        Assert.Equal(second, decision.Candidate);
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
