using Baton.Domain;

namespace Baton.Queue;

/// <summary>Pure, pre-launch worker selection. Runway arithmetic remains outside this policy.</summary>
public static class WorkerAssignmentPolicy
{
    public static AssignmentDecision Select(
        AssignmentRequest request, IReadOnlyList<WorkerCandidate> roster, FleetFacts fleetFacts)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(fleetFacts);

        var rejected = new List<CandidateRejection>();
        if (roster.Count == 0)
        {
            return AssignmentDecision.Refused(AssignmentReason.NoEligibleCandidate, "The candidate pool is empty.", rejected);
        }

        var candidates = roster.Select((candidate, order) => (candidate, order)).ToList();
        if (request.Override is { } overrideChoice)
        {
            var match = candidates.FirstOrDefault(x => x.candidate.Matches(overrideChoice));
            if (match.candidate is null)
            {
                return AssignmentDecision.Refused(AssignmentReason.InvalidOverride, "The operator override is not in this candidate pool.", rejected);
            }

            // An exact, valid operator choice is authoritative.  The declared-size requirement
            // protects automatic selection between a pool; it must not mask validation of an
            // explicit triple (or turn an invalid override into a size error).
            if (Eligible(match.candidate, request, fleetFacts, allowRunwayOverride: request.OverrideRunwayReason is not null, allowUnknownSize: true, out var reason))
            {
                return AssignmentDecision.Selected(match.candidate, AssignmentReason.OperatorOverride, request.Override.Reason, rejected);
            }

            rejected.Add(new CandidateRejection(match.candidate, reason));
            return AssignmentDecision.Refused(AssignmentReason.InvalidOverride, "The operator override is not eligible.", rejected);
        }

        if (roster.Count > 1 && request.DeclaredSize == DeclaredTaskSize.Unknown)
        {
            return AssignmentDecision.Refused(
                AssignmentReason.DeclaredSizeRequired,
                "A declared task size is required when a role has multiple worker candidates.",
                roster.Select(c => new CandidateRejection(c, CandidateRejectionReason.DeclaredSizeMissing)).ToList());
        }

        var eligible = new List<(WorkerCandidate Candidate, int Order, FleetCandidateFacts Facts)>();
        foreach (var entry in candidates)
        {
            if (Eligible(entry.candidate, request, fleetFacts, allowRunwayOverride: false, allowUnknownSize: roster.Count == 1, out var reason))
            {
                eligible.Add((entry.candidate, entry.order, fleetFacts.For(entry.candidate)));
            }
            else
            {
                rejected.Add(new CandidateRejection(entry.candidate, reason));
            }
        }

        if (eligible.Count == 0)
        {
            return AssignmentDecision.Refused(AssignmentReason.NoEligibleCandidate, "No candidate has sufficient pre-launch evidence.", rejected);
        }

        var floor = eligible.Min(c => c.Candidate.CapabilityBand);
        var selected = eligible.Where(c => c.Candidate.CapabilityBand == floor)
            .OrderByDescending(c => c.Facts.UsableWeeklyRunway)
            .ThenBy(c => c.Order)
            .First();
        return AssignmentDecision.Selected(selected.Candidate, AssignmentReason.Automatic, "Eligible candidate with the lowest sufficient capability band and greatest usable weekly runway.", rejected);
    }

    private static bool Eligible(WorkerCandidate candidate, AssignmentRequest request, FleetFacts fleetFacts,
        bool allowRunwayOverride, bool allowUnknownSize, out CandidateRejectionReason rejection)
    {
        if (candidate.ConductorOnly) { rejection = CandidateRejectionReason.ConductorOnly; return false; }
        if (candidate.CapabilityBand < request.RequiredCapabilityBand) { rejection = CandidateRejectionReason.BelowCapabilityFloor; return false; }
        if (!candidate.RequiredGrants.IsSupersetOf(request.RequiredGrants)) { rejection = CandidateRejectionReason.UnsupportedGrant; return false; }
        if (candidate.ScopeClasses.Count > 0 && (request.ScopeClass is null || !candidate.ScopeClasses.Contains(request.ScopeClass))) { rejection = CandidateRejectionReason.ScopeIneligible; return false; }
        if (candidate.TaskSizes.Count > 0 && !(allowUnknownSize && request.DeclaredSize == DeclaredTaskSize.Unknown) && !candidate.TaskSizes.Contains(request.DeclaredSize)) { rejection = CandidateRejectionReason.SizeIneligible; return false; }
        var facts = fleetFacts.For(candidate);
        if (facts.Evidence is not UsageEvidence.Fresh and not UsageEvidence.IntentionallyUnmeasured) { rejection = CandidateRejectionReason.UsageEvidenceUnavailable; return false; }
        if (facts.AccountWideHeld) { rejection = CandidateRejectionReason.AccountWideRunwayHeld; return false; }
        if (facts.SpecializedBucketHeld) { rejection = CandidateRejectionReason.SpecializedRunwayHeld; return false; }
        if (facts.RunwayHeld && !allowRunwayOverride) { rejection = CandidateRejectionReason.RunwayHeld; return false; }
        rejection = default;
        return true;
    }
}

public enum CapabilityBand { Minimal, Cheap, Standard, Frontier }
public enum UsageEvidence { Fresh, Missing, Stale, Malformed, IntentionallyUnmeasured }
public enum AssignmentReason { Automatic, OperatorOverride, DeclaredSizeRequired, InvalidOverride, NoEligibleCandidate }
public enum CandidateRejectionReason { ConductorOnly, BelowCapabilityFloor, UnsupportedGrant, ScopeIneligible, SizeIneligible, DeclaredSizeMissing, UsageEvidenceUnavailable, RunwayHeld, AccountWideRunwayHeld, SpecializedRunwayHeld }

public sealed record WorkerCandidate(
    string Adapter, string? Model, string? Effort, CapabilityBand CapabilityBand,
    IReadOnlySet<DeclaredTaskSize> TaskSizes, IReadOnlySet<string> ScopeClasses, IReadOnlySet<string> RequiredGrants,
    bool ConductorOnly = false)
{
    public bool Matches(AssignmentOverride choice) =>
        string.Equals(Adapter, choice.Adapter, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Model, choice.Model, StringComparison.Ordinal)
        && string.Equals(Effort, choice.Effort, StringComparison.Ordinal);
}

public sealed record AssignmentRequest(
    string Role, string? Stage, string? ScopeClass, DeclaredTaskSize DeclaredSize,
    IReadOnlySet<string> RequiredGrants, CapabilityBand RequiredCapabilityBand,
    AssignmentOverride? Override = null, string? OverrideRunwayReason = null);
public sealed record AssignmentOverride(string Adapter, string? Model, string? Effort, string Reason);
public sealed record FleetCandidateFacts(UsageEvidence Evidence, bool RunwayHeld, double UsableWeeklyRunway,
    bool AccountWideHeld = false, bool SpecializedBucketHeld = false);
public sealed record FleetFacts(IReadOnlyDictionary<string, FleetCandidateFacts> Candidates)
{
    public FleetCandidateFacts For(WorkerCandidate candidate) =>
        Candidates.TryGetValue(Key(candidate), out var facts)
            ? facts
            : new FleetCandidateFacts(UsageEvidence.Missing, true, 0);
    public static string Key(WorkerCandidate candidate) => $"{candidate.Adapter}|{candidate.Model}|{candidate.Effort}";
}
public sealed record CandidateRejection(WorkerCandidate Candidate, CandidateRejectionReason Reason);
public sealed record AssignmentDecision(WorkerCandidate? Candidate, AssignmentReason Reason, string Explanation,
    IReadOnlyList<CandidateRejection> RejectedCandidates)
{
    public static AssignmentDecision Selected(WorkerCandidate candidate, AssignmentReason reason, string explanation,
        IReadOnlyList<CandidateRejection> rejected) => new(candidate, reason, explanation, rejected);
    public static AssignmentDecision Refused(AssignmentReason reason, string explanation,
        IReadOnlyList<CandidateRejection> rejected) => new(null, reason, explanation, rejected);
}
