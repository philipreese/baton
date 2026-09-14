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
}

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
    [property: JsonPropertyName("timestamp")] DateTime Timestamp);

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
    IReadOnlyList<ConductorClaimTransition>? Transitions = null);

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
    ConductorTakeoverProvenance? Takeover = null);
