using System.Text.Json;
using System.Text.Json.Serialization;

namespace Baton.Domain;

/// <summary>The conductor's declared routing size. Definitions live in spec/baton.md §13.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DeclaredTaskSize>))]
public enum DeclaredTaskSize
{
    [JsonStringEnumMemberName("unknown")] Unknown,
    [JsonStringEnumMemberName("small")] Small,
    [JsonStringEnumMemberName("medium")] Medium,
    [JsonStringEnumMemberName("large")] Large,
}

/// <summary>Immutable conductor input recorded before a worker is launched.</summary>
[JsonConverter(typeof(TaskSizeDeclarationJsonConverter))]
public readonly record struct TaskSizeDeclaration(
    [property: JsonPropertyName("size")] DeclaredTaskSize Size,
    [property: JsonPropertyName("rationale")] string? Rationale)
{
    public const string Usage = "small|medium|large";

    /// <summary>The explicit projection for records written before declarations existed.</summary>
    public static TaskSizeDeclaration Unknown { get; } = new(DeclaredTaskSize.Unknown, null);

    public static TaskSizeDeclaration Parse(string size, string rationale)
    {
        if (!Enum.TryParse<DeclaredTaskSize>(size, true, out var parsed)
            || !Enum.IsDefined(parsed)
            || parsed == DeclaredTaskSize.Unknown
            || !string.Equals(size, parsed.ToString(), StringComparison.OrdinalIgnoreCase))
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

/// <summary>
/// Preserves queue compatibility with both declaration shapes written before the field existed:
/// an absent property (the record default) and an explicit JSON null (normalized here to unknown).
/// </summary>
public sealed class TaskSizeDeclarationJsonConverter : JsonConverter<TaskSizeDeclaration>
{
    public override bool HandleNull => true;

    public override TaskSizeDeclaration Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return TaskSizeDeclaration.Unknown;
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("A task-size declaration must be an object or null.");
        }

        string? size = null;
        string? rationale = null;
        var sawSize = false;
        var sawRationale = false;
        var ended = false;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                ended = true;
                break;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("A task-size declaration contains invalid JSON.");
            }

            var propertyName = reader.GetString();
            if (!reader.Read())
            {
                throw new JsonException("A task-size declaration ended before its property value.");
            }

            switch (propertyName)
            {
                case "size":
                    if (sawSize || reader.TokenType != JsonTokenType.String)
                    {
                        throw new JsonException("A task-size declaration requires one textual size.");
                    }
                    sawSize = true;
                    size = reader.GetString();
                    break;
                case "rationale":
                    if (sawRationale || reader.TokenType is not (JsonTokenType.String or JsonTokenType.Null))
                    {
                        throw new JsonException("A task-size declaration permits one textual rationale or null.");
                    }
                    sawRationale = true;
                    rationale = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        if (!ended || !sawSize || size is null)
        {
            throw new JsonException("A task-size declaration object requires a size.");
        }

        if (string.Equals(size, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            if (rationale is not null)
            {
                throw new JsonException("An unknown legacy task-size declaration cannot carry a rationale.");
            }
            return TaskSizeDeclaration.Unknown;
        }

        try
        {
            return TaskSizeDeclaration.Parse(size, rationale ?? string.Empty);
        }
        catch (ArgumentException ex)
        {
            throw new JsonException(ex.Message, ex);
        }
    }

    public override void Write(
        Utf8JsonWriter writer, TaskSizeDeclaration value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("size");
        JsonSerializer.Serialize(writer, value.Size, options);
        if (value.Rationale is not null)
        {
            writer.WriteString("rationale", value.Rationale);
        }
        writer.WriteEndObject();
    }
}
