using System.Text.Json;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Mutation;
using Baton.Status;

namespace Baton.Vendors;

internal sealed record AgyHostEnvelope(int Version, string Kind, string ExecutionId, string Incarnation,
    int Turn, JsonElement? Native, FinalExpectedTurnCompletion? Completion);

/// <summary>The sole decoder for host-framed AGY streams; raw one-shot objects pass through.</summary>
public static class AgyHostDecoder
{
    internal static void Initialize()
    {
        AgyUsageParser.DecodeStreamLine = Decode;
        AgyUsageParser.ReadObservedStream = ReadUsage;
        AgyUsageParser.DecorateIncrementalUsage = (line, sample) => ReadEnvelope(line) is { Kind: "native", Native: { } native }
            && native.TryGetProperty("step_update", out var step) && step.ValueKind == JsonValueKind.Object
            && step.TryGetProperty("conversation_id", out var conversation) && conversation.ValueKind == JsonValueKind.String
            && step.TryGetProperty("step_index", out var index)
                ? sample with { MessageId = conversation.GetString() + ":" + index, BilledIsFloor = true } : sample;
    }

    public static string Decode(string line)
    {
        if (!line.Contains("\"Version\"", StringComparison.Ordinal)) return line;
        var envelope = ReadEnvelope(line);
        if (envelope is null || envelope.Native is not { ValueKind: JsonValueKind.Object } native) return "{}";
        if (!AgyHostUsage.TryRead(native, out _, out _)) return "{}";
        if (envelope.Kind == "native")
            return native.TryGetProperty("event", out var eventName) && eventName.GetString() == "result" ? "{}" : native.GetRawText();
        return IsValidCompletion(envelope)
                ? native.GetRawText() : "{}";
    }

    private static bool IsBoundCompletion(AgyHostEnvelope envelope) => envelope.Completion is { Version: 1 } completion
            && completion.Transport == AgyStreamingHost.Transport
            && completion.ExecutionId == envelope.ExecutionId && completion.Incarnation == envelope.Incarnation
            && completion.ExpectedTurn == envelope.Turn && completion.CompletedTurn >= 0 && completion.CompletedTurn <= envelope.Turn
            && completion.ChildPid > 0 && completion.ChildStartUtc.Kind == DateTimeKind.Utc && completion.ChildStartUtc != default
            && !string.IsNullOrWhiteSpace(completion.ConversationId);

    private static bool IsValidCompletion(AgyHostEnvelope envelope) => IsBoundCompletion(envelope) && envelope.Completion is { } completion
            && completion.HasValidEvidence(AgyStreamingHost.Transport)
            && completion.ExecutionId == envelope.ExecutionId && completion.Incarnation == envelope.Incarnation
            && completion.ExpectedTurn == envelope.Turn && completion.CompletedTurn == envelope.Turn
            && envelope.Native is { ValueKind: JsonValueKind.Object } native
            && native.TryGetProperty("event", out var eventName) && eventName.ValueKind == JsonValueKind.String && eventName.GetString() == "result"
            && native.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("conversation_id", out var conversation) && conversation.ValueKind == JsonValueKind.String
            && conversation.GetString() == completion.ConversationId
            && result.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String
            && completion.Successful == (status.GetString() == "SUCCESS");

    public static string? DecodeTail(string? tail)
    {
        if (tail is null || !tail.Contains("\"Version\"", StringComparison.Ordinal)) return tail;
        var decoded = new System.Text.StringBuilder();
        // Core's bounded diagnostic tail flattens line breaks. Only root-frame boundaries are
        // candidates; scanning every opening brace would promote a torn frame's nested result.
        foreach (var line in System.Text.RegularExpressions.Regex.Split(tail, """(?<=})\s+(?=\{"Version":)"""))
        {
            // A clipped or corrupt host frame never grants its nested native object independent authority.
            if (ReadEnvelope(line) is not null) decoded.AppendLine(Decode(line));
        }
        return decoded.ToString();
    }

    internal static AgyHostEnvelope? ReadEnvelope(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !UniqueProperties(document.RootElement)) return null;
            var envelope = JsonSerializer.Deserialize<AgyHostEnvelope>(line, AgyCorrectionStore.JsonOptions);
            return envelope is { Version: 1, Turn: 1 or 2, Kind: "native" or "completion" }
                && !string.IsNullOrWhiteSpace(envelope.ExecutionId) && Guid.TryParseExact(envelope.Incarnation, "N", out _)
                && (envelope.Kind != "native" || envelope.Completion is null) ? envelope : null;
        }
        catch (JsonException) { return null; }
    }

    private static ObservedStreamUsage ReadUsage(IReadOnlyList<string> lines)
    {
        var monitor = new TokenBudgetMonitor(null, null, null, new AgyUsageParser());
        var samples = new List<WorkerUsage>();
        var usageValidator = new AgyHostUsage();
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        string? execution = null, incarnation = null;
        string? conversationId = null;
        var valid = true;
        var footerSeen = false;
        WorkerUsage? reported = null;
        foreach (var line in lines)
        {
            var envelope = ReadEnvelope(line);
            if (envelope is null || footerSeen) { valid = false; continue; }
            execution ??= envelope.ExecutionId;
            incarnation ??= envelope.Incarnation;
            if (execution != envelope.ExecutionId || incarnation != envelope.Incarnation) valid = false;
            if (envelope.Kind == "completion")
            {
                footerSeen = true;
                if (!IsValidCompletion(envelope) && !(IsBoundCompletion(envelope)
                    && envelope.Completion is { EvidenceValid: false, CaptureIntegrityValid: true })) valid = false;
                new AgyUsageParser().TryParseFinalUsage(line, out reported);
                continue;
            }
            if (envelope.Native is not { ValueKind: JsonValueKind.Object } native) { valid = false; continue; }
            if (!native.TryGetProperty("event", out var eventName) || eventName.ValueKind != JsonValueKind.String) { valid = false; continue; }
            if (eventName.GetString() == "init")
            {
                if (conversationId is not null || !native.TryGetProperty("conversation_id", out var initConversation)
                    || initConversation.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(initConversation.GetString())) valid = false;
                else conversationId = initConversation.GetString();
            }
            if (conversationId is null) valid = false;
            if (native.TryGetProperty("step_update", out var step) && step.ValueKind == JsonValueKind.Object
                && step.TryGetProperty("state", out var state) && state.GetString() is "DONE" or "ERROR")
            {
                if (!step.TryGetProperty("conversation_id", out var stepConversation) || stepConversation.ValueKind != JsonValueKind.String
                    || stepConversation.GetString() != conversationId) valid = false;
                if (!step.TryGetProperty("step_index", out var index)
                    || index.ValueKind is not (JsonValueKind.Number or JsonValueKind.String)) { valid = false; continue; }
                var key = index.ToString();
                var hash = AgyCorrectionStore.Digest(step.GetRawText());
                if (seen.TryGetValue(key, out var prior))
                {
                    if (prior != hash) valid = false;
                    continue;
                }
                seen.Add(key, hash);
                if (step.TryGetProperty("step_type", out var stepType) && stepType.GetString() == "agent_response"
                    && step.TryGetProperty("usage", out var usage))
                {
                    if (usage.ValueKind != JsonValueKind.Object) valid = false;
                    else foreach (var dimension in usage.EnumerateObject())
                        if (dimension.Value.ValueKind != JsonValueKind.Number || !dimension.Value.TryGetInt64(out var count) || count < 0) valid = false;
                }
            }
            if (!usageValidator.Admit(native)) { valid = false; continue; }
            monitor.OnStdoutLine(line);
            if (new AgyUsageParser().TryParseIncrementalUsage(line, out var sample) && sample is not null) samples.Add(sample);
            if (monitor.SnapshotUsage().BilledTokens is < 0) valid = false;
        }
        WorkerUsage? observed = null;
        try
        {
            if (valid) observed = monitor.SnapshotUsage() with
            {
                TokensIn = Sum(samples, sample => sample.TokensIn),
                TokensOut = Sum(samples, sample => sample.TokensOut),
                CacheReadTokens = Sum(samples, sample => sample.CacheReadTokens),
                CacheCreationTokens = Sum(samples, sample => sample.CacheCreationTokens),
                ThinkingTokens = Sum(samples, sample => sample.ThinkingTokens),
                BilledIsFloor = true,
            };
        }
        catch (OverflowException) { valid = false; }
        return new(observed, reported, valid ? null : "invalid-or-conflicting-host-usage-evidence", execution);
    }

    private static long? Sum(IReadOnlyList<WorkerUsage> samples, Func<WorkerUsage, long?> dimension) =>
        samples.Any(sample => dimension(sample) is not null) ? samples.Sum(sample => dimension(sample) ?? 0) : null;

    internal static bool UniqueProperties(JsonElement node) => node.ValueKind switch
    {
        JsonValueKind.Object => node.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count()
            == node.EnumerateObject().Count() && node.EnumerateObject().All(p => UniqueProperties(p.Value)),
        JsonValueKind.Array => node.EnumerateArray().All(UniqueProperties),
        _ => true,
    };
}
