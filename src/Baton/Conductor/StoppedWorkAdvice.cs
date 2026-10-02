using System.Text.Json.Serialization;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Baton.Accounting;
using Baton.Domain;
using Baton.Queue;

namespace Baton.Conductor;

public static class StoppedWorkAdviceEvidence
{
    public static StoppedWorkAdviceContext Context(StoppedWorkJudgment source) => new(
        source.Repository, source.Tag, source.AttemptId!.Value, source.Stage, source.ObservedAt,
        source.PullRequestHead, source.AttemptBaseRevision, source.TerminalOutcome,
        source.TerminalEvidenceAvailable, source.Checks, source.ChecksObservedAt, source.HaltCause,
        source.RepairAllowance, source.VerdictAvailable, source.RequiredChecks, source.State);

    public static string Hash(StoppedWorkAdviceContext context) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(context)))).ToLowerInvariant();
}

/// <summary>The exact immutable request presented to a stopped-work advice provider.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StoppedWorkAdviceRequest(
    [property: JsonRequired] string ObligationId,
    [property: JsonRequired] string Repository,
    [property: JsonRequired] string Tag,
    [property: JsonRequired] FleetAttemptId AttemptId,
    [property: JsonRequired] WorkStage Stage,
    [property: JsonRequired] string ContextSha256,
    [property: JsonRequired] DateTimeOffset ObservedAt,
    string? PullRequestHead,
    string? AttemptBaseRevision,
    string Holder,
    StoppedWorkHaltCause HaltCause = StoppedWorkHaltCause.Other,
    string? RepairAllowance = null,
    bool? VerdictAvailable = null,
    string? RequiredChecks = null,
    StoppedWorkJudgmentState State = StoppedWorkJudgmentState.Pending);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StoppedWorkAdviceContext(
    [property: JsonRequired] string Repository,
    [property: JsonRequired] string Tag,
    [property: JsonRequired] FleetAttemptId AttemptId,
    [property: JsonRequired] WorkStage Stage,
    [property: JsonRequired] DateTimeOffset ObservedAt,
    string? PullRequestHead,
    string? AttemptBaseRevision,
    string? TerminalOutcome,
    bool? TerminalEvidenceAvailable,
    string? Checks,
    DateTimeOffset? ChecksObservedAt,
    StoppedWorkHaltCause HaltCause = StoppedWorkHaltCause.Other,
    string? RepairAllowance = null,
    bool? VerdictAvailable = null,
    string? RequiredChecks = null,
    StoppedWorkJudgmentState State = StoppedWorkJudgmentState.Pending);

public enum StoppedWorkAdviceChoice
{
    Hold,
    Recommend,
}

/// <summary>Advice only; this response carries no action authority.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StoppedWorkAdviceDecision(
    [property: JsonRequired] string ObligationId,
    [property: JsonRequired] string Repository,
    [property: JsonRequired] string Tag,
    [property: JsonRequired] string AttemptId,
    [property: JsonRequired] string ContextSha256,
    [property: JsonRequired] StoppedWorkAdviceChoice Choice,
    [property: JsonRequired] string Explanation);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StoppedWorkAdviceUsage(
    long? InputTokens,
    long? OutputTokens,
    long? CachedInputTokens,
    long? CacheCreationInputTokens = null,
    decimal? ApiEquivalentCostUsd = null);

/// <summary>The actual admitted transport, model and effort; settings cannot change this evidence.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StoppedWorkAdviceProviderDescriptor(
    [property: JsonRequired] string Adapter,
    [property: JsonRequired] string Model,
    [property: JsonRequired] string Effort)
{
    public static readonly StoppedWorkAdviceProviderDescriptor Codex =
        new("codex-subscription-cli", "gpt-5.6-luna", "low");
    public static readonly StoppedWorkAdviceProviderDescriptor Claude =
        new("claude-subscription-cli", "claude-haiku-4-5-20251001", "low");

    [JsonIgnore]
    public bool IsSupported => this == Codex || this == Claude;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RetainedStoppedWorkAdviceResponse(
    [property: JsonRequired] StoppedWorkAdviceDecision Decision,
    [property: JsonRequired] string Adapter,
    [property: JsonRequired] string Model,
    [property: JsonRequired] string Effort,
    [property: JsonRequired] DateTimeOffset CompletedAt,
    StoppedWorkAdviceUsage? Usage = null);
