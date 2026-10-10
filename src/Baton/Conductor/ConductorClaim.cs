using System.Text.Json.Serialization;

namespace Baton.Conductor;

/// <summary>
/// The provenance of an explicit conductor takeover (#2296): who was displaced, why, and when.
/// </summary>
public sealed record ConductorTakeoverProvenance(
    [property: JsonPropertyName("displacedHolder")] string DisplacedHolder,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("takenOverAt")] DateTime TakenOverAt);

/// <summary>
/// The transition kind of an auditable conductor claim event.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConductorClaimTransitionKind
{
    Claim,
    Takeover,
    Release,
    Stop,
}

/// <summary>Exact operator input and its durable, atomic authority result (spec/baton.md §14).</summary>
public sealed record ConductorHostedControlRequest(
    string Repository, string Holder, string ClaimGeneration, string AttachmentId,
    string RequestId, string Reason, string? DestinationHolder = null, string? DestinationAddress = null);

public sealed record ConductorHostedControlReceipt(
    ConductorHostedControlRequest Request, string Issuer, string Operation,
    string ResultHolder, string ResultGeneration, DateTime AppliedAt);

/// <summary>
/// One transition in a repository's claim audit trail (#2296).
/// </summary>
public sealed record ConductorClaimTransition(
    [property: JsonPropertyName("kind")] ConductorClaimTransitionKind Kind,
    [property: JsonPropertyName("holder")] string Holder,
    [property: JsonPropertyName("displacedHolder")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? DisplacedHolder,
    [property: JsonPropertyName("reason")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Reason,
    [property: JsonPropertyName("timestamp")] DateTime Timestamp,
    [property: JsonPropertyName("generation")] string? Generation = null,
    [property: JsonPropertyName("control")] ConductorHostedControlReceipt? Control = null);

/// <summary>
/// The durable, per-repository conductor claim document stored at
/// <c>{BatonPaths.Root}/&lt;repository-slug&gt;/conductor-claim.json</c> (#2296).
/// </summary>
public sealed record ConductorClaimRecord(
    [property: JsonPropertyName("repository")] string Repository,
    [property: JsonPropertyName("repositorySlug")] string RepositorySlug,
    [property: JsonPropertyName("holder")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Holder = null,
    [property: JsonPropertyName("acquiredAt")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTime? AcquiredAt = null,
    [property: JsonPropertyName("takeover")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ConductorTakeoverProvenance? Takeover = null,
    [property: JsonPropertyName("transitions")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<ConductorClaimTransition>? Transitions = null,
    [property: JsonPropertyName("claimGeneration")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ClaimGeneration = null,
    [property: JsonPropertyName("stopped")] bool Stopped = false,
    [property: JsonPropertyName("destinationAddress")] string? DestinationAddress = null);

/// <summary>
/// The public projection of an actively held repository claim for <c>baton conductor list</c>
/// and Fleet Glass consumption (#2296).
/// </summary>
public sealed record ConductorClaimSummary(
    [property: JsonPropertyName("repository")] string Repository,
    [property: JsonPropertyName("repositorySlug")] string RepositorySlug,
    [property: JsonPropertyName("holder")] string Holder,
    [property: JsonPropertyName("acquiredAt")] DateTime AcquiredAt,
    [property: JsonPropertyName("takeover")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ConductorTakeoverProvenance? Takeover = null,
    [property: JsonPropertyName("stopped")] bool Stopped = false,
    [property: JsonPropertyName("destinationAddress")] string? DestinationAddress = null);
