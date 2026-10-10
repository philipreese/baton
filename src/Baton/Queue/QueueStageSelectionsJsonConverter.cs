using System.Text.Json;
using System.Text.Json.Serialization;

namespace Baton.Queue;

/// <summary>The property seam that keeps historical arrays and fences the exact 0.68 reader.</summary>
public sealed class QueueStageSelectionsJsonConverter : JsonConverter<IReadOnlyList<QueueStageSelection>>
{
    public override IReadOnlyList<QueueStageSelection>? Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        if (value.ValueKind == JsonValueKind.Array)
        {
            var legacy = value.Deserialize<List<QueueStageSelection>>(options)!;
            if (legacy.Any(selection => selection is not null && selection.EnableAgyCorrection))
                throw new JsonException("AGY correction requires versioned stage selections.");
            return legacy;
        }

        if (value.ValueKind != JsonValueKind.Object
            || value.EnumerateObject().Count() != 2
            || !value.TryGetProperty("version", out var version)
            || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out var number) || number != 1
            || !value.TryGetProperty("selections", out var selections)
            || selections.ValueKind != JsonValueKind.Array)
            throw new JsonException("Unsupported or malformed stage selections representation; use a compatible release.");

        var retained = selections.Deserialize<List<QueueStageSelection>>(options)!;
        ValidateProtected(retained);
        return retained;
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<QueueStageSelection> value,
        JsonSerializerOptions options)
    {
        if (!value.Any(selection => selection is not null && selection.EnableAgyCorrection))
        {
            JsonSerializer.Serialize(writer, value.ToArray(), options);
            return;
        }

        ValidateProtected(value);
        writer.WriteStartObject();
        writer.WriteNumber("version", 1);
        writer.WritePropertyName("selections");
        JsonSerializer.Serialize(writer, value.ToArray(), options);
        writer.WriteEndObject();
    }

    private static void ValidateProtected(IReadOnlyList<QueueStageSelection> selections)
    {
        var stages = new HashSet<WorkStage>();
        if (!selections.Any(selection => selection is not null && selection.EnableAgyCorrection))
            throw new JsonException("Versioned stage selections must retain an explicit AGY correction declaration.");
        foreach (var selection in selections)
        {
            if (selection is null || !Enum.IsDefined(selection.Stage) || WorkStages.IsTerminal(selection.Stage)
                || !stages.Add(selection.Stage))
                throw new JsonException("Versioned stage selections require unique valid dispatch stages.");
        }
    }
}
