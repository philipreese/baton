namespace Baton.Queue;

/// <summary>An exact forge-declared context; null AppId permits any source.</summary>
public sealed record RequiredCheckRequirement(string Context, long? AppId);

/// <summary>A current-head, source-qualified result copied from a bounded forge response.</summary>
public sealed record RequiredCheckWitness(
    string Context, long? AppId, string Source, long Id, string Verdict, DateTimeOffset? StartedAt);

/// <summary>Compact as-of proof. Older receipts without this field are not completeness proof.</summary>
public sealed record RequiredCheckEvidence(
    string Repository, string BaseBranch, string HeadSha,
    string PolicySha256, string WitnessesSha256, DateTimeOffset ObservedAt);
