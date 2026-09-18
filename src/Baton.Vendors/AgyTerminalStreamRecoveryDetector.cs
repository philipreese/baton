using System.Text;
using System.Text.Json;
using Baton.Dispatch;

namespace Baton.Vendors;

/// <summary>
/// Interprets agy's captured stream for #2002. This is deliberately local to the adapter layer:
/// the engine receives only <see cref="OutstandingToolAtTerminalSuccess"/>.
/// </summary>
internal static class AgyTerminalStreamRecoveryDetector
{
    public static OutstandingToolAtTerminalSuccess? Detect(string? stdoutTail)
    {
        if (string.IsNullOrWhiteSpace(stdoutTail))
        {
            return null;
        }

        var active = new Dictionary<string, ActiveTool>(StringComparer.Ordinal);
        foreach (var root in ReadObjects(stdoutTail))
        {
            if (!root.TryGetProperty("event", out var eventProperty)
                || eventProperty.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            if (eventProperty.GetString() == "step_update"
                && root.TryGetProperty("step_update", out var step)
                && step.ValueKind == JsonValueKind.Object
                && step.TryGetProperty("step_type", out var typeProperty)
                && typeProperty.GetString() == "tool"
                && step.TryGetProperty("state", out var stateProperty)
                && stateProperty.ValueKind == JsonValueKind.String
                && step.TryGetProperty("tool_name", out var nameProperty)
                && nameProperty.ValueKind == JsonValueKind.String
                && nameProperty.GetString() is { Length: > 0 } toolName)
            {
                var key = StepKey(step, toolName);
                var state = stateProperty.GetString();
                if (state == "ACTIVE")
                {
                    active[key] = new ActiveTool(toolName, CommandLine(step));
                }
                else if (state is "DONE" or "ERROR")
                {
                    if (!active.Remove(key))
                    {
                        foreach (var candidate in active
                                     .Where(pair => string.Equals(pair.Value.ToolName, toolName, StringComparison.Ordinal))
                                     .Select(pair => pair.Key)
                                     .ToList())
                        {
                            active.Remove(candidate);
                        }
                    }
                }

                continue;
            }

            if (eventProperty.GetString() == "result"
                && root.TryGetProperty("result", out var result)
                && result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("status", out var statusProperty)
                && statusProperty.ValueKind == JsonValueKind.String
                && statusProperty.GetString() == "SUCCESS"
                && active.Values.FirstOrDefault() is { } outstanding)
            {
                return new OutstandingToolAtTerminalSuccess(outstanding.ToolName, outstanding.CommandLine);
            }
        }

        return null;
    }

    private static string StepKey(JsonElement step, string toolName) =>
        step.TryGetProperty("step_index", out var index) && index.ValueKind is JsonValueKind.Number or JsonValueKind.String
            ? index.ToString()
            : toolName;

    private static string? CommandLine(JsonElement step)
    {
        if (!step.TryGetProperty("tool_info", out var info) || info.ValueKind != JsonValueKind.Object
            || !info.TryGetProperty("parameters", out var parameters) || parameters.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var propertyName in new[] { "CommandLine", "commandLine", "command" })
        {
            if (parameters.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }

    private static IReadOnlyList<JsonElement> ReadObjects(string text)
    {
        var objects = new List<JsonElement>();
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '{' || (index > 0 && !char.IsWhiteSpace(text[index - 1]) && text[index - 1] != '}'))
            {
                continue;
            }

            var bytes = Encoding.UTF8.GetBytes(text[index..]);
            var reader = new Utf8JsonReader(bytes, isFinalBlock: true, state: default);
            if (!JsonDocument.TryParseValue(ref reader, out var document))
            {
                continue;
            }

            using (document)
            {
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    objects.Add(document.RootElement.Clone());
                }
            }

            var consumed = (int)reader.BytesConsumed;
            index += Math.Max(Encoding.UTF8.GetCharCount(bytes, 0, consumed) - 1, 0);
        }

        return objects;
    }

    private sealed record ActiveTool(string ToolName, string? CommandLine);
}
