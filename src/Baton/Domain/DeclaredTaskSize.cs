using System.Text.Json.Serialization;

namespace Baton.Domain;

/// <summary>The conductor's declared routing size. Definitions live in spec/baton.md §11 C-15.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DeclaredTaskSize>))]
public enum DeclaredTaskSize
{
    [JsonStringEnumMemberName("unknown")] Unknown,
    [JsonStringEnumMemberName("small")] Small,
    [JsonStringEnumMemberName("medium")] Medium,
    [JsonStringEnumMemberName("large")] Large,
}

/// <summary>Immutable conductor input recorded before a worker is launched.</summary>
public sealed record TaskSizeDeclaration(DeclaredTaskSize Size, string Rationale)
{
    public static readonly string Usage = "small|medium|large";

    public static TaskSizeDeclaration Parse(string size, string rationale)
    {
        if (!Enum.TryParse<DeclaredTaskSize>(size, true, out var parsed) || parsed == DeclaredTaskSize.Unknown)
        {
            throw new ArgumentException($"Declared task size must be one of {Usage}.", nameof(size));
        }

        if (string.IsNullOrWhiteSpace(rationale))
        {
            throw new ArgumentException("A declared task size requires a non-blank rationale.", nameof(rationale));
        }

        return new TaskSizeDeclaration(parsed, rationale.Trim());
    }
}
