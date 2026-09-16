using System.Security.Cryptography;
using System.Text;

namespace Baton.Queue;

/// <summary>
/// The immutable worker choice made before a queue item is eligible to launch.  Scheduler code may
/// hold this choice through the runway gate, but it must never resolve the tier again to substitute a
/// different vendor after the operator has seen it.
/// </summary>
public sealed record FrozenWorkerAssignment(
    string DecisionId,
    string Adapter,
    string? Model,
    string? Effort,
    string PoolHash,
    string ClosedReason,
    string Explanation,
    DateTimeOffset DecidedAt,
    IReadOnlyList<string>? ConsultedUsageSnapshots = null,
    IReadOnlyList<string>? RejectedCandidates = null,
    IReadOnlyList<string>? Supersedes = null)
{
    /// <summary>Creates the compatibility decision for a resolved one-candidate tier. Multi-candidate
    /// selection is deliberately owned by <see cref="WorkerAssignmentPolicy"/> and must be supplied
    /// by the queue command with its observed fleet facts.</summary>
    public static FrozenWorkerAssignment ForLegacyTier(QueueTierResolution tier, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tier);
        var tuple = $"{tier.Adapter}|{tier.Model}|{tier.Effort}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tuple))).ToLowerInvariant();
        return new FrozenWorkerAssignment(
            Guid.NewGuid().ToString("n"), tier.Adapter ?? string.Empty, tier.Model, tier.Effort, hash,
            "legacy-single-candidate", "Legacy one-triple tier frozen before launch.", now,
            ConsultedUsageSnapshots: [], RejectedCandidates: [], Supersedes: []);
    }
}
