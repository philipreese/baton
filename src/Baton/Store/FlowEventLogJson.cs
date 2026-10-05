using System.Collections.Frozen;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Domain;

namespace Baton.Store;

/// <summary>
/// The one <see cref="JsonSerializerOptions"/> every <c>flow.jsonl</c> line is written and read
/// with (#604). Shared rather than defaulted per call site, because a journal written under one
/// configuration and read under another is not a round trip — it is data loss discovered later.
/// </summary>
/// <remarks>
/// <para>
/// <b>Enums persist by name, not by ordinal.</b> Without <see cref="JsonStringEnumConverter"/>,
/// <c>FailureClassification.Permanent</c> was stored as <c>1</c> and <c>CoreExitReason.Natural</c>
/// as <c>0</c>. That makes every historical line's meaning depend on the declaration order of an
/// enum in this repo: insert a member above <c>TimedOut</c>, or swap two, and every journal already
/// on disk silently reinterprets — a stored <c>Permanent</c> reads back as <c>Retryable</c>, and
/// AER retries an execution a worker explicitly declared not worth retrying. Nothing in the type
/// system, the tests, or the persistence layer prevented that edit, and it would have produced no
/// error at any point. It is the quietest failure available to an event-sourced store.
/// </para>
/// <para>
/// <b>Reading is deliberately more permissive than writing, and only in this one direction.</b>
/// <see cref="JsonStringEnumConverter"/> accepts a number as well as a name, so journals written
/// before #604 — which carry ordinals — keep replaying unchanged. That is what makes this a
/// widening of the reader rather than a breaking change to durable data, and it is why no migration
/// step exists: there is nothing to migrate, because nothing stops being readable.
/// </para>
/// <para>
/// <b>A missing required member fails loudly.</b>
/// <see cref="JsonSerializerOptions.RespectRequiredConstructorParameters"/> makes a constructor
/// parameter with no default value genuinely required. Before this, a line that had lost its
/// <c>ExecutionId</c> — to a truncated write, a partial fsync, or a member renamed in a later
/// version — deserialized happily into an event for execution <c>""</c>, which then took part in
/// projection as though it were real. Silent state corruption is strictly worse than a loud
/// <see cref="JsonException"/> naming the bad line, because the loud one is recoverable;
/// <see cref="FlowEventLogReader"/> already wraps it into a
/// <see cref="FlowEventLogReadException"/> carrying the offending text.
/// </para>
/// <para>
/// <b>What stays permitted, and why the distinction is the whole point.</b> A trailing parameter
/// with a default is <i>optional</i>, so a line predating it still replays and the member defaults —
/// the additive compatibility #597 deliberately relied on when it added <c>Reason</c>. Required
/// means "declared with no default", which is orthogonal to being nullable: <c>ExecutionFailed</c>'s
/// <c>FailureClassification?</c> is a nullable type and a required member, and a line omitting it is
/// now rejected while a line omitting <c>Reason</c> is not. Adding a member is safe; removing or
/// renaming one is not; those were the same code path before #604 and neither threw.
/// </para>
/// <para>
/// <b>Do not set <see cref="JsonSerializerOptions.DefaultIgnoreCondition"/> on these options.</b> It
/// is left at <c>Never</c>, and that is the only reason a null required member is written at all —
/// <c>"FailureClassification":null</c> is emitted rather than omitted. Setting
/// <c>WhenWritingNull</c> here would stop the writer emitting it and the reader would immediately
/// reject the lines it had just written: a store that cannot read its own output. On a wire frame
/// absence and null mean the same thing, so <c>WhenWritingNull</c> is the natural choice there and
/// copy-pasting such options here looks harmless; here absence means damage. A test pins this
/// setting so the mistake fails loudly instead of corrupting the journal.
/// </para>
/// <para>
/// <b>An unknown discriminator is not the same failure as a known one with a bad shape (#1779).</b>
/// A <c>FlowEvent</c> <c>eventType</c> (or <c>LogEntry</c> <c>owner</c>) this binary has never heard of
/// is a newer writer, not damage — <see cref="DeserializeLine"/> returns an internal sentinel for that
/// case instead of throwing, and <see cref="FlowEventLogReader"/> skips and counts it. Every other case
/// above is unchanged: a recognized discriminator with a lost or renamed member still throws exactly as
/// this type's remarks describe.
/// </para>
/// </remarks>
public static class FlowEventLogJson
{
    /// <summary>The journal's wire contract. Never construct a second one — see this type's remarks.</summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        Converters = { new JsonStringEnumConverter() },
        RespectRequiredConstructorParameters = true,
    };

    private static readonly FrozenSet<string> KnownOwners = typeof(LogEntry)
        .GetCustomAttributes<JsonDerivedTypeAttribute>()
        .Select(attribute => (string)attribute.TypeDiscriminator!)
        .ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> KnownEventTypes = typeof(FlowEvent)
        .GetCustomAttributes<JsonDerivedTypeAttribute>()
        .Select(attribute => (string)attribute.TypeDiscriminator!)
        .ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> KnownRoomEventTypes = typeof(RoomEvent)
        .GetCustomAttributes<JsonDerivedTypeAttribute>()
        .Select(attribute => (string)attribute.TypeDiscriminator!)
        .ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// #1779: the tolerant entry point for reading one <c>flow.jsonl</c> line — every caller that reads
    /// the journal (<see cref="FlowEventLogReader"/>, and any test asserting the converter-level
    /// contract) uses this instead of calling <c>JsonSerializer.Deserialize&lt;LogEntry&gt;</c> directly.
    /// Peeks the <c>owner</c> discriminator (and, for a <c>flow</c> line, the nested <c>Event</c>'s own
    /// <c>eventType</c>) before committing to a shape: an unrecognized discriminator returns
    /// <see cref="LogEntry.UnknownLogEntry"/> or a <see cref="LogEntry.FlowLogEntry"/> wrapping
    /// <see cref="FlowEvent.UnknownFlowEvent"/> rather than throwing; a recognized one is deserialized
    /// through <see cref="Options"/> exactly as before, so a lost/renamed member on a KNOWN kind still
    /// throws <see cref="JsonException"/>.
    /// <para>
    /// <b>Why this isn't a <see cref="JsonConverter{T}"/> on <see cref="Options"/> itself.</b> The
    /// runtime refuses to combine a custom converter with a type that also declares
    /// <see cref="JsonPolymorphicAttribute"/>/<see cref="JsonDerivedTypeAttribute"/>: registering one for
    /// <see cref="FlowEvent"/> or <see cref="LogEntry"/> throws <c>NotSupportedException</c> ("the
    /// converter for derived type … does not support metadata writes or reads") the moment either type
    /// is resolved as a root type, not just when the converter would actually run. <see cref="Options"/>
    /// therefore stays exactly the attribute-driven built-in dispatch every other caller already depends
    /// on, and the peek-first logic lives here, one layer up, instead.
    /// </para>
    /// </summary>
    public static LogEntry DeserializeLine(string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        RejectAmbiguousSelectedAuthority(root);
        var owner = ReadDiscriminator(root, "owner");

        if (!KnownOwners.Contains(owner))
        {
            return new LogEntry.UnknownLogEntry(owner, root.GetRawText());
        }

        if (owner == "flow" && root.TryGetProperty("Event", out var eventElement))
        {
            var eventType = ReadDiscriminator(eventElement, "eventType");
            if (!KnownEventTypes.Contains(eventType))
            {
                var writerUtcTimestamp = root.TryGetProperty("WriterUtcTimestamp", out var timestampElement)
                    && timestampElement.ValueKind != JsonValueKind.Null
                        ? timestampElement.GetDateTime()
                        : (DateTime?)null;
                return new LogEntry.FlowLogEntry(
                    new FlowEvent.UnknownFlowEvent(eventType, eventElement.GetRawText()), writerUtcTimestamp);
            }
        }

        if (owner == "room" && root.TryGetProperty("Event", out var roomElement))
        {
            var eventType = ReadDiscriminator(roomElement, "eventType");
            if (!KnownRoomEventTypes.Contains(eventType))
            {
                var writerUtcTimestamp = root.TryGetProperty("WriterUtcTimestamp", out var timestampElement)
                    && timestampElement.ValueKind != JsonValueKind.Null
                        ? timestampElement.GetDateTime()
                        : (DateTime?)null;
                return new LogEntry.RoomLogEntry(
                    new RoomEvent.UnknownRoomEvent(eventType, roomElement.GetRawText()), writerUtcTimestamp);
            }
        }

        return JsonSerializer.Deserialize<LogEntry>(root.GetRawText(), Options)
            ?? throw new JsonException($"Line in the ledger deserialized to null: {line}");
    }

    // Selected completion authority must have exactly one interpretation. Neither identical
    // duplicates nor property order may let first-turn artifacts turn ambiguity into success.
    internal static void ValidateSelectedAuthorities(IReadOnlyList<string> lines)
    {
        var authorities = new List<JsonElement>();
        var selected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!IsCompletionAuthority(root)) continue;
            authorities.Add(root.Clone());
            if (Descendants(root, "ExactRunningTransport").Any(value => value.ValueKind == JsonValueKind.String))
                foreach (var id in Descendants(root, "ExecutionId").Where(value => value.ValueKind == JsonValueKind.String))
                    selected.Add(id.GetString()!);
        }
        foreach (var root in authorities)
            if (Descendants(root, "ExecutionId").Any(value => value.ValueKind == JsonValueKind.String && selected.Contains(value.GetString()!))
                && !UniqueProperties(root))
                throw new JsonException("Ambiguous selected transport accepted/start/exit completion authority.");
    }

    private static void RejectAmbiguousSelectedAuthority(JsonElement root)
    {
        if (IsCompletionAuthority(root)
            && Descendants(root, "ExactRunningTransport").Any(value => value.ValueKind == JsonValueKind.String)
            && !UniqueProperties(root))
            throw new JsonException("Ambiguous selected transport accepted/start/exit completion authority.");
    }

    private static bool IsCompletionAuthority(JsonElement root) => Descendants(root, "eventType")
        .Any(value => value.ValueKind == JsonValueKind.String
            && value.GetString() is "executionRequestAccepted" or "executionStarted" or "executionExited");

    private static IEnumerable<JsonElement> Descendants(JsonElement node, string name)
    {
        if (node.ValueKind == JsonValueKind.Object)
            foreach (var property in node.EnumerateObject())
            {
                if (property.Name == name) yield return property.Value;
                foreach (var value in Descendants(property.Value, name)) yield return value;
            }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var item in node.EnumerateArray())
                foreach (var value in Descendants(item, name)) yield return value;
    }

    private static bool UniqueProperties(JsonElement node) => node.ValueKind switch
    {
        JsonValueKind.Object => node.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count()
            == node.EnumerateObject().Count() && node.EnumerateObject().All(property => UniqueProperties(property.Value)),
        JsonValueKind.Array => node.EnumerateArray().All(UniqueProperties),
        _ => true,
    };

    private static string ReadDiscriminator(JsonElement element, string discriminatorPropertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"Expected a JSON object, got {element.ValueKind}: {element.GetRawText()}");
        }

        if (!element.TryGetProperty(discriminatorPropertyName, out var discriminatorProperty)
            || discriminatorProperty.ValueKind != JsonValueKind.String)
        {
            throw new JsonException(
                $"Missing or non-string '{discriminatorPropertyName}' discriminator: {element.GetRawText()}");
        }

        return discriminatorProperty.GetString()!;
    }
}
