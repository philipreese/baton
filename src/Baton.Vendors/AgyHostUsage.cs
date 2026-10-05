using System.Text.Json;

namespace Baton.Vendors;

/// <summary>Checks selected native usage before it can reach Core's live arithmetic.</summary>
internal sealed class AgyHostUsage
{
    private readonly Dictionary<string, long> _totals = new(StringComparer.Ordinal);
    private long _billed;

    internal static bool TryRead(JsonElement native, out Dictionary<string, long>? dimensions, out long billed)
    {
        dimensions = null;
        billed = 0;
        if (!TryReadTerminalStepIdentity(native, out _)) return false;
        if (!native.TryGetProperty("event", out var kind) || kind.ValueKind != JsonValueKind.String) return false;
        var name = kind.GetString() == "result" ? "result" : "step_update";
        if (!native.TryGetProperty(name, out var node) || node.ValueKind != JsonValueKind.Object) return true;
        if (name == "step_update"
            && (!node.TryGetProperty("step_type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "agent_response")) return true;
        if (!node.TryGetProperty("usage", out var usage)) return true;
        if (usage.ValueKind != JsonValueKind.Object) return false;
        dimensions = new(StringComparer.Ordinal);
        foreach (var property in usage.EnumerateObject())
        {
            if (property.Name is not ("input_tokens" or "output_tokens" or "cache_read_tokens" or "thinking_tokens" or "total_tokens")
                || property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt64(out var count) || count < 0
                || !dimensions.TryAdd(property.Name, count)) return false;
        }
        try
        {
            billed = checked(dimensions.GetValueOrDefault("input_tokens") + dimensions.GetValueOrDefault("output_tokens"));
            _ = checked(dimensions.GetValueOrDefault("input_tokens") + dimensions.GetValueOrDefault("cache_read_tokens"));
            return true;
        }
        catch (OverflowException) { return false; }
    }

    internal static bool TryReadTerminalStepIdentity(JsonElement native, out string? identity)
    {
        identity = null;
        if (!native.TryGetProperty("event", out var kind) || kind.ValueKind != JsonValueKind.String
            || kind.GetString() != "step_update") return true;
        if (!native.TryGetProperty("step_update", out var step) || step.ValueKind != JsonValueKind.Object) return true;
        if (!step.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String
            || state.GetString() is not ("DONE" or "ERROR")) return true;
        if (!step.TryGetProperty("step_index", out var index)
            || index.ValueKind is not (JsonValueKind.Number or JsonValueKind.String)) return false;
        identity = index.ToString();
        return true;
    }

    internal bool Admit(JsonElement native)
    {
        // Invalid dimensions or any overflowing delta/aggregate never reach the emergency
        // monitor. A result is a restatement, so only distinct terminal steps accumulate.
        if (!TryRead(native, out var dimensions, out var billed)) return false;
        if (dimensions is null || native.GetProperty("event").GetString() != "step_update") return true;
        var step = native.GetProperty("step_update");
        if (!step.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String || state.GetString() != "DONE") return true;
        try
        {
            var nextBilled = checked(_billed + billed);
            var next = dimensions.ToDictionary(pair => pair.Key, pair => checked(_totals.GetValueOrDefault(pair.Key) + pair.Value), StringComparer.Ordinal);
            foreach (var pair in next) _totals[pair.Key] = pair.Value;
            _billed = nextBilled;
            return true;
        }
        catch (OverflowException) { return false; }
    }
}
