using System.Text.Json.Serialization;

namespace Baton.Queue;

/// <summary>
/// The explicit capabilities a queued task says it needs. These are task metadata, never a source of
/// authority: admission compares them with the role's effective grant and refuses a mismatch.
/// </summary>
/// <remarks>
/// <para>
/// A null list on <see cref="QueueItem.Requirements"/> is deliberately different from an empty list:
/// null is a row written before task requirements existed (unknown during migration), while an empty
/// list is a producer's explicit statement that this task needs none of this vocabulary.
/// </para>
/// <para>
/// <c>artifact:&lt;name&gt;</c> names an output the selected role must declare. It prevents a brief from
/// requiring a gate receipt or other durable handoff that the role contract cannot produce.
/// </para>
/// </remarks>
public static class TaskRequirements
{
    public const string RepositoryRead = "repository-read";
    public const string FileWrite = "file-write";
    public const string Shell = "shell";
    public const string Network = "network";
    public const string GitHubRead = "github-read";
    public const string GitHubWrite = "github-write";
    public const string ArtifactPrefix = "artifact:";

    public static readonly IReadOnlyList<string> Fixed =
    [RepositoryRead, FileWrite, Shell, Network, GitHubRead, GitHubWrite];

    public static IReadOnlyList<string> Normalize(IEnumerable<string> requirements)
    {
        ArgumentNullException.ThrowIfNull(requirements);

        var normalized = new List<string>();
        foreach (var raw in requirements)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                throw new ArgumentException("Task requirements cannot be blank.", nameof(requirements));
            }

            var requirement = raw.Trim().ToLowerInvariant();
            if (!IsKnown(requirement))
            {
                throw new ArgumentException(
                    $"Unknown task requirement '{raw}'. Use one of: {string.Join(", ", Fixed)}, or artifact:<output-name>.",
                    nameof(requirements));
            }

            if (!normalized.Contains(requirement, StringComparer.Ordinal))
            {
                normalized.Add(requirement);
            }
        }

        return normalized;
    }

    public static bool IsKnown(string requirement) =>
        Fixed.Contains(requirement, StringComparer.Ordinal)
        || (requirement.StartsWith(ArtifactPrefix, StringComparison.Ordinal)
            && requirement.Length > ArtifactPrefix.Length
            && !string.IsNullOrWhiteSpace(requirement[ArtifactPrefix.Length..])
            && !requirement[ArtifactPrefix.Length..].Any(char.IsWhiteSpace));

    /// <summary>Whether a legacy requirement-less row has execution authority that needs migration
    /// completion before it can safely remain unknown.</summary>
    public static bool IsExecutionBearing(bool fileWrite, bool shellCanMutate, bool network) =>
        fileWrite || shellCanMutate || network;
}

/// <summary>
/// The durable outcome of comparing a task declaration with the role catalog at admission time.
/// The capability names are retained rather than reconstructed later because the catalog can change
/// between the refusal and an operator investigating it.
/// </summary>
public sealed record TaskRequirementAdmission(
    [property: JsonPropertyName("requestedRequirements")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? Requested,
    [property: JsonPropertyName("effectiveGrant")]
    IReadOnlyList<string> EffectiveGrant,
    [property: JsonPropertyName("result")]
    string Result,
    [property: JsonPropertyName("missing")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? Missing = null,
    [property: JsonPropertyName("vendorUsage")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    double? VendorUsage = null)
{
    public const string Admitted = "admitted";
    public const string Unknown = "unknown";
    public const string Refused = "refused";
}
