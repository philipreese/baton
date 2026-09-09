using System.Text.Json;

namespace Baton.VendorProbe;

/// <summary>
/// Codex-specific non-interactive and app-server grammar. These helpers are pure so CI can catch a
/// regression without starting an authenticated CLI or spending subscription usage.
/// </summary>
internal static class CodexProbe
{
    internal const int ModelListRequestId = 2;
    internal const int RateLimitsRequestId = 3;

    internal static string[] ExecJsonArgs(string prompt) =>
        [.. CommonExecArgs(), prompt];

    internal static string[] ResumeJsonArgs(string sessionId, string prompt) =>
        [.. CommonExecArgs(), "resume", sessionId, prompt];

    private static string[] CommonExecArgs() =>
    [
        "exec",
        "--json",
        "--ignore-user-config",
        "--skip-git-repo-check",
        "--sandbox", "read-only",
        "--model", "gpt-5.6-luna",
        "--config", "model_reasoning_effort=\"low\"",
        "--config", "approval_policy=\"never\"",
        "--disable", "shell_tool",
        "--disable", "unified_exec",
        "--disable", "multi_agent",
        "--disable", "multi_agent_v2",
    ];

    internal static string[] AppServerArgs() => ["app-server", "--stdio"];

    internal static string[] AppServerRequests() =>
    [
        "{\"method\":\"initialize\",\"id\":1,\"params\":{\"clientInfo\":{\"name\":\"baton-vendor-probe\",\"title\":\"Baton Vendor Probe\",\"version\":\"1\"}}}",
        "{\"method\":\"initialized\",\"params\":{}}",
        $"{{\"method\":\"model/list\",\"id\":{ModelListRequestId},\"params\":{{\"limit\":100,\"includeHidden\":false}}}}",
        $"{{\"method\":\"account/rateLimits/read\",\"id\":{RateLimitsRequestId},\"params\":null}}",
    ];

    internal static bool IsRequestedResponse(string line, int requestId)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.TryGetProperty("id", out var id)
                && id.TryGetInt32(out var parsed)
                && parsed == requestId;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool IsChatGptSubscriptionAuth(string output) =>
        output.Contains("logged in using chatgpt", StringComparison.OrdinalIgnoreCase);

    internal static bool LooksLikeExecJson(string stdout)
    {
        foreach (var line in Lines(stdout))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("type", out var type)
                    && type.ValueKind == JsonValueKind.String
                    && type.GetString() is { Length: > 0 })
                {
                    return true;
                }
            }
            catch (JsonException)
            {
                // A diagnostic line must not make a later valid event invisible.
            }
        }

        return false;
    }

    internal static bool HasTurnUsage(string stdout)
    {
        foreach (var line in Lines(stdout))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("type", out var type)
                    && type.GetString() == "turn.completed"
                    && root.TryGetProperty("usage", out var usage)
                    && usage.ValueKind == JsonValueKind.Object)
                {
                    return true;
                }
            }
            catch (JsonException)
            {
                // Keep scanning the JSONL stream.
            }
        }

        return false;
    }

    internal static bool TryReadThreadId(string stdout, out string sessionId)
    {
        sessionId = string.Empty;
        foreach (var line in Lines(stdout))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("type", out var type)
                    && type.GetString() == "thread.started"
                    && root.TryGetProperty("thread_id", out var id)
                    && id.GetString() is { Length: > 0 } parsed)
                {
                    sessionId = parsed;
                    return true;
                }
            }
            catch (JsonException)
            {
                // Keep scanning the JSONL stream.
            }
        }

        return false;
    }

    internal static bool TryDescribeModels(string stdout, out string summary)
    {
        summary = string.Empty;
        foreach (var line in Lines(stdout))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!IsId(root, ModelListRequestId)
                    || !root.TryGetProperty("result", out var result)
                    || !result.TryGetProperty("data", out var data)
                    || data.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                List<string> models = [];
                foreach (var item in data.EnumerateArray())
                {
                    if (item.TryGetProperty("hidden", out var hidden)
                        && hidden.ValueKind is JsonValueKind.True)
                    {
                        continue;
                    }

                    if (!item.TryGetProperty("model", out var model)
                        || model.GetString() is not { Length: > 0 } name)
                    {
                        continue;
                    }

                    var efforts = item.TryGetProperty("supportedReasoningEfforts", out var supported)
                                  && supported.ValueKind == JsonValueKind.Array
                        ? supported.EnumerateArray()
                            .Select(e => e.TryGetProperty("reasoningEffort", out var effort)
                                ? effort.GetString()
                                : null)
                            .Where(e => !string.IsNullOrWhiteSpace(e))
                            .Select(e => e!)
                            .ToList()
                        : [];
                    models.Add(efforts.Count == 0
                        ? name
                        : $"{name}[{string.Join('/', efforts)}]");
                }

                summary = string.Join(", ", models);
                return models.Count > 0;
            }
            catch (JsonException)
            {
                // Keep scanning notifications and unrelated responses.
            }
        }

        return false;
    }

    internal enum CatalogDriftVerdict
    {
        Current,
        Drifted,
    }

    internal sealed record CatalogDrift(
        CatalogDriftVerdict Verdict,
        IReadOnlyList<string> DroppedModels,
        IReadOnlyList<string> GainedModels,
        IReadOnlyList<string> ChangedEfforts,
        string RecordingName)
    {
        public bool HasDrift => Verdict == CatalogDriftVerdict.Drifted;

        public string Explain()
        {
            if (!HasDrift)
            {
                return $"catalog matches embedded recording ({RecordingName}).";
            }

            var parts = new List<string>();
            if (DroppedModels.Count > 0)
            {
                parts.Add($"dropped: {string.Join(", ", DroppedModels)}");
            }
            if (GainedModels.Count > 0)
            {
                parts.Add($"gained: {string.Join(", ", GainedModels)}");
            }
            if (ChangedEfforts.Count > 0)
            {
                parts.Add($"effort changed: {string.Join(", ", ChangedEfforts)}");
            }

            return $"catalog has drifted from embedded recording ({RecordingName}): {string.Join("; ", parts)}. Re-record snapshot to restore alignment.";
        }
    }

    internal static bool TryParseLiveCatalog(
        string stdout,
        out Dictionary<string, IReadOnlyList<string>> liveCatalog)
    {
        liveCatalog = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var line in Lines(stdout))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!IsId(root, ModelListRequestId)
                    || !root.TryGetProperty("result", out var result)
                    || !result.TryGetProperty("data", out var data)
                    || data.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var item in data.EnumerateArray())
                {
                    if (item.TryGetProperty("hidden", out var hidden)
                        && hidden.ValueKind is JsonValueKind.True)
                    {
                        continue;
                    }

                    if (!item.TryGetProperty("model", out var model)
                        || model.GetString() is not { Length: > 0 } name)
                    {
                        continue;
                    }

                    var efforts = item.TryGetProperty("supportedReasoningEfforts", out var supported)
                                  && supported.ValueKind == JsonValueKind.Array
                        ? supported.EnumerateArray()
                            .Select(e => e.TryGetProperty("reasoningEffort", out var effort)
                                ? effort.GetString()
                                : null)
                            .Where(e => !string.IsNullOrWhiteSpace(e))
                            .Select(e => e!)
                            .ToList()
                        : [];

                    liveCatalog[name] = efforts;
                }

                return liveCatalog.Count > 0;
            }
            catch (JsonException)
            {
                // Keep scanning notifications and unrelated responses.
            }
        }

        return false;
    }

    internal static CatalogDrift CompareCatalog(
        IReadOnlyDictionary<string, IReadOnlyList<string>> live,
        IReadOnlyDictionary<string, IReadOnlyList<string>> recorded,
        string recordingName)
    {
        var dropped = recorded.Keys.Where(k => !live.ContainsKey(k)).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var gained = live.Keys.Where(k => !recorded.ContainsKey(k)).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var changed = new List<string>();

        foreach (var key in live.Keys.Where(recorded.ContainsKey))
        {
            var liveEfforts = live[key];
            var recordedEfforts = recorded[key];
            if (!liveEfforts.SequenceEqual(recordedEfforts, StringComparer.Ordinal))
            {
                changed.Add($"{key} (live: [{string.Join('/', liveEfforts)}], recorded: [{string.Join('/', recordedEfforts)}])");
            }
        }

        var verdict = dropped.Count > 0 || gained.Count > 0 || changed.Count > 0
            ? CatalogDriftVerdict.Drifted
            : CatalogDriftVerdict.Current;

        return new CatalogDrift(verdict, dropped, gained, changed, recordingName);
    }

    internal static bool TryDescribeRateLimits(string stdout, out string summary)
    {
        summary = string.Empty;
        foreach (var line in Lines(stdout))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!IsId(root, RateLimitsRequestId)
                    || !root.TryGetProperty("result", out var result))
                {
                    continue;
                }

                List<string> windows = [];
                CollectRateLimitWindows(result, windows);
                if (windows.Count > 0)
                {
                    summary = string.Join(", ", windows.Distinct(StringComparer.Ordinal));
                    return true;
                }

                return false;
            }
            catch (JsonException)
            {
                // Keep scanning notifications and unrelated responses.
            }
        }

        return false;
    }

    private static void CollectRateLimitWindows(JsonElement element, List<string> windows)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("usedPercent", out var used)
                && used.TryGetDouble(out var percent))
            {
                var reset = element.TryGetProperty("resetsAt", out var resetElement)
                    ? ResetText(resetElement)
                    : null;
                windows.Add(reset is null ? $"{percent:g}% used" : $"{percent:g}% used, resets {reset}");
            }

            foreach (var property in element.EnumerateObject())
            {
                CollectRateLimitWindows(property.Value, windows);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CollectRateLimitWindows(item, windows);
            }
        }
    }

    private static string? ResetText(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return element.GetString();
        }

        if (element.TryGetInt64(out var epochSeconds))
        {
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(epochSeconds).ToString("O");
            }
            catch (ArgumentOutOfRangeException)
            {
                return epochSeconds.ToString();
            }
        }

        return null;
    }

    private static bool IsId(JsonElement root, int requestId) =>
        root.TryGetProperty("id", out var id)
        && id.TryGetInt32(out var parsed)
        && parsed == requestId;

    private static IEnumerable<string> Lines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0);
}
