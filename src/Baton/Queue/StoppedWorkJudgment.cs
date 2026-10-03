using System.Text.Json.Serialization;
using Baton.Accounting;
using Baton.Domain;

namespace Baton.Queue;

/// <summary>The retained state of one stopped-work advisory request.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<StoppedWorkJudgmentState>))]
public enum StoppedWorkJudgmentState
{
    Pending,
    Running,
    Available,
    Uncertain,
    Blocked,
    Unsupported,
    Stale,
}

[JsonConverter(typeof(JsonStringEnumConverter<StoppedWorkHaltCause>))]
public enum StoppedWorkHaltCause
{
    ExhaustedRepairAllowance,
    MissingVerdict,
    UnavailableCheckEvidence,
    UnavailableSourceEvidence,
    Other,
}

/// <summary>
/// Immutable, typed evidence captured at the lifecycle halt. It is deliberately an as-of snapshot:
/// no prose, command line, exception, transcript, credential, or arbitrary file content belongs here.
/// </summary>
public sealed record StoppedWorkJudgment(
    [property: JsonPropertyName("key")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Key,
    [property: JsonPropertyName("repository")] string Repository,
    [property: JsonPropertyName("tag")] string Tag,
    [property: JsonPropertyName("attemptId")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    FleetAttemptId? AttemptId,
    [property: JsonPropertyName("stage")] WorkStage Stage,
    [property: JsonPropertyName("observedAt")] DateTimeOffset ObservedAt,
    [property: JsonPropertyName("holder")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Holder,
    [property: JsonPropertyName("pullRequest")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? PullRequest,
    [property: JsonPropertyName("pullRequestHead")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? PullRequestHead,
    [property: JsonPropertyName("attemptBaseRevision")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? AttemptBaseRevision,
    [property: JsonPropertyName("terminalOutcome")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? TerminalOutcome,
    [property: JsonPropertyName("terminalEvidenceAvailable")]
    bool? TerminalEvidenceAvailable,
    [property: JsonPropertyName("checks")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Checks,
    [property: JsonPropertyName("checksObservedAt")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? ChecksObservedAt,
    [property: JsonPropertyName("contextSha256")] string ContextSha256,
    [property: JsonPropertyName("haltCause")] StoppedWorkHaltCause HaltCause = StoppedWorkHaltCause.Other,
    [property: JsonPropertyName("repairAllowance")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? RepairAllowance = null,
    [property: JsonPropertyName("verdictAvailable")]
    bool? VerdictAvailable = null,
    [property: JsonPropertyName("requiredChecks")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? RequiredChecks = null,
    [property: JsonPropertyName("state")] StoppedWorkJudgmentState State = StoppedWorkJudgmentState.Pending,
    [property: JsonPropertyName("reason")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Reason = null,
    [property: JsonPropertyName("completedAt")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? CompletedAt = null,
    [property: JsonPropertyName("choice")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Choice = null,
    [property: JsonPropertyName("explanation")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Explanation = null,
    [property: JsonPropertyName("automaticMissingVerdictReplacementReviewEligible")]
    bool AutomaticMissingVerdictReplacementReviewEligible = false);
