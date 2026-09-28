using System.Text.Json.Serialization;

namespace Baton.Conductor;

/// <summary>One caller-owned, exact-revision request. Evidence is supplied separately and bound by digest.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReadinessRequest(
    int SchemaVersion,
    string Key,
    string Repository,
    string Workspace,
    string Revision,
    string Holder,
    DateTimeOffset ObservedAt,
    string ContextSha256,
    string Adapter,
    string Model,
    string Effort);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReadinessEvidence(string Name, string Status, string Detail);

/// <summary>As-of caller evidence. It does not claim a fresh forge observation.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReadinessContext(
    int SchemaVersion,
    string Key,
    string Repository,
    string Workspace,
    string Revision,
    DateTimeOffset ObservedAt,
    IReadOnlyList<ReadinessEvidence> Evidence);

public enum ReadinessChoice
{
    Hold,
    Recommend,
}

/// <summary>Advice only; this schema has no action channel.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReadinessDecision(
    [property: JsonRequired] string ObligationId,
    [property: JsonRequired] string Repository,
    [property: JsonRequired] string Revision,
    [property: JsonRequired] string ContextSha256,
    [property: JsonRequired] ReadinessChoice Decision,
    [property: JsonRequired] string Explanation);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReadinessUsage(long? InputTokens, long? OutputTokens, long? CachedInputTokens);

/// <summary>Complete accepted response, retained before a transport acknowledgement.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RetainedReadinessResponse(
    [property: JsonRequired] ReadinessDecision Decision,
    [property: JsonRequired] ReadinessUsage? Usage,
    [property: JsonRequired] string Adapter,
    [property: JsonRequired] string Model,
    [property: JsonRequired] string Effort,
    [property: JsonRequired] DateTimeOffset CompletedAt);
