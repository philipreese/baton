using System.Text.Json.Serialization;
using Baton.Domain;

namespace Baton.Queue;

[JsonConverter(typeof(JsonStringEnumConverter<QueueReplacementReviewOrigin>))]
public enum QueueReplacementReviewOrigin
{
    Manual,
    Automatic,
}

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
    string? PausedReason = null);
