namespace Baton.Queue;

/// <summary>The issue reservation is written before any brief, branch, trust, or worktree mutation.
/// Only Prepared permits a worker launch. An abandoned preparation remains visible and blocked;
/// absence of a workspace is not evidence that a branch was never created.</summary>
public enum TaskPreparationState
{
    Preparing,
    Prepared,
    Blocked,
}

public sealed record QueueIssuePreparation(
    TaskPreparationState State,
    DateTimeOffset ReservedAt,
    string? Reason = null,
    string? ExpectedBranch = null,
    string? ExpectedWorkspace = null,
    int? ProcessId = null,
    DateTimeOffset? ProcessStartedAt = null,
    string? ReservationId = null);

/// <summary>Immutable caller input and judgment owner for a task-owned lifecycle.</summary>
public sealed record OwnedTaskSubmission(
    string Id,
    string Repository,
    int Issue,
    string InputDigest,
    string ConductorHolder,
    DateTimeOffset SubmittedAt,
    TaskReadyReceipt? Ready = null,
    TaskBlockedDisposition? Blocked = null);

/// <summary>Evidence for one exact reviewed head. A later head needs its own proof.</summary>
public sealed record TaskReadyReceipt(
    string Id,
    string TaskId,
    string Repository,
    int Issue,
    int PullRequest,
    string HeadSha,
    string ReviewAttemptId,
    string VerdictSha256,
    string RequiredChecks,
    string ChecksObservationId,
    DateTimeOffset ChecksObservedAt,
    DateTimeOffset ReadyObservedAt);

public sealed record TaskBlockedDisposition(
    string ReasonCode,
    string Evidence,
    DateTimeOffset ObservedAt,
    string? ObligationKey);
