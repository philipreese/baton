using System.Text.Json.Serialization;
using Baton.Domain;

namespace Baton.Queue;

[JsonConverter(typeof(JsonStringEnumConverter<QueueReplacementReviewOrigin>))]
public enum QueueReplacementReviewOrigin
{
    Manual,
    Automatic,
}

/// <summary>Historical admission evidence written with the worker marker; never reusable permission.</summary>
public sealed record QueueHostedIssuedAuthority(
    int SchemaVersion, string Repository, string Holder, string ClaimGeneration, string AttachmentId,
    string ObligationKey, FleetAttemptId SourceAttemptId, string HeadSha,
    string RequestSha256, string ConfigurationSha256, string SessionId, string ResponseSha256,
    FleetAttemptId AttemptId, string RoomDirectory, DateTimeOffset IssuedAt);

/// <summary>
/// Queue-owned, single-use action slot for one missing-verdict source. The mutable current attempt
/// fields on QueueItem are never used to reconstruct the replacement after advancement.
/// </summary>
public sealed record QueueReplacementReviewAction(
    string ObligationKey,
    string Holder,
    string AdviceDigest,
    string Repository,
    string Tag,
    FleetAttemptId SourceAttemptId,
    string SourceRoomDirectory,
    WorkStage SourceStage,
    int SourceRound,
    int PullRequest,
    string HeadSha,
    string Workspace,
    string Branch,
    DateTimeOffset AuthorizedAt,
    FleetAttemptId? ReplacementAttemptId = null,
    string? ReplacementRoomDirectory = null,
    string? CompletionProof = null,
    string? BlockedReason = null,
    string? NextTrigger = null,
    QueueReplacementReviewOrigin Origin = QueueReplacementReviewOrigin.Manual,
    string? PausedReason = null,
    string? EvidenceProvenance = null,
    string? EvidenceDigest = null,
    string? EvidenceDirectory = null,
    QueueHostedIssuedAuthority? IssuedAuthority = null,
    string? TerminalObservation = null,
    DateTimeOffset? TerminalObservedAt = null,
    DateTimeOffset? ActionObservedAt = null);
