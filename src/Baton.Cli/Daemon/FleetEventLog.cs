using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Domain;
using Baton.Status;

namespace Baton.Cli.Daemon;

/// <summary>The one named vocabulary for Baton's durable, fleet-wide work history (#2140).</summary>
[JsonConverter(typeof(FleetEventKindJsonConverter))]
public enum FleetEventKind
{
    WorkQueued,
    AdmissionDecided,
    AttemptStarted,
    AttemptProgressed,
    AttemptRefused,
    AttemptRetryScheduled,
    AttemptSettled,
    RevisionProduced,
    ReviewVerdictObserved,
    PullRequestBound,
    CheckObserved,
    MergeObserved,
    ReleaseObserved,
    DeploymentObserved,
    DaemonStarted,
    DaemonStopped,
}

internal sealed class FleetEventKindJsonConverter : JsonConverter<FleetEventKind>
{
    public override FleetEventKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var token = reader.GetString() ?? throw new JsonException("Expected a fleet event kind string.");
        foreach (var kind in FleetEventKinds.All)
        {
            if (string.Equals(token, JsonNamingPolicy.CamelCase.ConvertName(kind.ToString()), StringComparison.Ordinal))
            {
                return kind;
            }
        }

        throw new JsonException($"Unknown fleet event kind '{token}'.");
    }

    public override void Write(Utf8JsonWriter writer, FleetEventKind value, JsonSerializerOptions options) =>
        writer.WriteStringValue(JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));
}

/// <summary>
/// Closed enumeration used by source-level producer coverage tests. Writers accept the enum rather
/// than an arbitrary string, so an unknown kind cannot reach disk through <see cref="FleetEventLog"/>.
/// </summary>
public static class FleetEventKinds
{
    public static readonly IReadOnlySet<FleetEventKind> All = Enum.GetValues<FleetEventKind>().ToHashSet();
}

[JsonConverter(typeof(Converter))]
public readonly record struct FleetWorkId(string Value)
{
    public override string ToString() => Value;

    private sealed class Converter : StringIdJsonConverter<FleetWorkId>
    {
        protected override FleetWorkId Create(string value) => new(value);
        protected override string GetValue(FleetWorkId id) => id.Value;
    }
}

[JsonConverter(typeof(Converter))]
public readonly record struct FleetRoomId(string Value)
{
    public override string ToString() => Value;

    private sealed class Converter : StringIdJsonConverter<FleetRoomId>
    {
        protected override FleetRoomId Create(string value) => new(value);
        protected override string GetValue(FleetRoomId id) => id.Value;
    }
}

[JsonConverter(typeof(Converter))]
public readonly record struct FleetRevisionId(string Value)
{
    public override string ToString() => Value;

    private sealed class Converter : StringIdJsonConverter<FleetRevisionId>
    {
        protected override FleetRevisionId Create(string value) => new(value);
        protected override string GetValue(FleetRevisionId id) => id.Value;
    }
}

[JsonConverter(typeof(Converter))]
public readonly record struct FleetReviewRoundId(string Value)
{
    public override string ToString() => Value;

    private sealed class Converter : StringIdJsonConverter<FleetReviewRoundId>
    {
        protected override FleetReviewRoundId Create(string value) => new(value);
        protected override string GetValue(FleetReviewRoundId id) => id.Value;
    }
}

/// <summary>Nullable measurements copied from an execution's existing status projection.</summary>
public sealed record FleetEventUsage(
    [property: JsonPropertyName("inputTokens")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? InputTokens = null,
    [property: JsonPropertyName("outputTokens")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? OutputTokens = null,
    [property: JsonPropertyName("cacheReadTokens")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? CacheReadTokens = null,
    [property: JsonPropertyName("cacheWriteTokens")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? CacheWriteTokens = null,
    [property: JsonPropertyName("reasoningTokens")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? ReasoningTokens = null,
    [property: JsonPropertyName("turns")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Turns = null,
    [property: JsonPropertyName("toolSteps")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ToolSteps = null,
    [property: JsonPropertyName("refusedToolSteps")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? RefusedToolSteps = null,
    [property: JsonPropertyName("repeatedToolSteps")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? RepeatedToolSteps = null);

/// <summary>
/// A producer-owned fact before the log assigns its monotonic id. Every correlation value is a
/// separate nullable field; absence is unknown and no reader is invited to parse names or times.
/// </summary>
public sealed record FleetEventDraft(
    FleetEventKind Kind,
    string DedupeKey,
    DateTimeOffset OccurredAt,
    FleetAttemptId? AttemptId = null,
    FleetAttemptId? ParentAttemptId = null,
    FleetWorkId? WorkId = null,
    FleetRoomId? RoomId = null,
    ExecutionId? ExecutionId = null,
    int? IssueId = null,
    int? PullRequestId = null,
    FleetRevisionId? RevisionId = null,
    FleetReviewRoundId? ReviewRoundId = null,
    string? Vendor = null,
    string? Model = null,
    string? Effort = null,
    string? DeclaredRole = null,
    IReadOnlyList<string>? EffectiveGrant = null,
    IReadOnlyList<string>? RequestedRequirements = null,
    IReadOnlyList<string>? MissingCapabilities = null,
    string? AdmissionDecision = null,
    string? Outcome = null,
    string? OutcomeDetail = null,
    string? ReviewVerdict = null,
    string? CheckConclusion = null,
    long? ElapsedMilliseconds = null,
    DateTimeOffset? LastMeaningfulProgressAt = null,
    FleetEventUsage? Usage = null,
    IReadOnlyList<string>? ArtifactReferences = null);

/// <summary>One durable line in <c>fleet/events.jsonl</c>.</summary>
public sealed record FleetEvent(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("at")] DateTimeOffset At,
    [property: JsonPropertyName("kind")] FleetEventKind Kind,
    [property: JsonPropertyName("dedupeKey")] string DedupeKey,
    [property: JsonPropertyName("attemptId")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FleetAttemptId? AttemptId = null,
    [property: JsonPropertyName("parentAttemptId")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FleetAttemptId? ParentAttemptId = null,
    [property: JsonPropertyName("workId")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FleetWorkId? WorkId = null,
    [property: JsonPropertyName("roomId")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FleetRoomId? RoomId = null,
    [property: JsonPropertyName("executionId")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ExecutionId? ExecutionId = null,
    [property: JsonPropertyName("issueId")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? IssueId = null,
    [property: JsonPropertyName("pullRequestId")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? PullRequestId = null,
    [property: JsonPropertyName("revisionId")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FleetRevisionId? RevisionId = null,
    [property: JsonPropertyName("reviewRoundId")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FleetReviewRoundId? ReviewRoundId = null,
    [property: JsonPropertyName("vendor")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Vendor = null,
    [property: JsonPropertyName("model")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Model = null,
    [property: JsonPropertyName("effort")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Effort = null,
    [property: JsonPropertyName("declaredRole")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DeclaredRole = null,
    [property: JsonPropertyName("effectiveGrant")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? EffectiveGrant = null,
    [property: JsonPropertyName("requestedRequirements")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? RequestedRequirements = null,
    [property: JsonPropertyName("missingCapabilities")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? MissingCapabilities = null,
    [property: JsonPropertyName("admissionDecision")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AdmissionDecision = null,
    [property: JsonPropertyName("outcome")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Outcome = null,
    [property: JsonPropertyName("outcomeDetail")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OutcomeDetail = null,
    [property: JsonPropertyName("reviewVerdict")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReviewVerdict = null,
    [property: JsonPropertyName("checkConclusion")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CheckConclusion = null,
    [property: JsonPropertyName("elapsedMilliseconds")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? ElapsedMilliseconds = null,
    [property: JsonPropertyName("lastMeaningfulProgressAt")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? LastMeaningfulProgressAt = null,
    [property: JsonPropertyName("usage")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FleetEventUsage? Usage = null,
    [property: JsonPropertyName("artifactReferences")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? ArtifactReferences = null)
{
    internal static FleetEvent From(long id, FleetEventDraft draft) => new(
        id, draft.OccurredAt.ToUniversalTime(), draft.Kind, draft.DedupeKey, draft.AttemptId,
        draft.ParentAttemptId, draft.WorkId, draft.RoomId, draft.ExecutionId, draft.IssueId,
        draft.PullRequestId, draft.RevisionId, draft.ReviewRoundId, draft.Vendor, draft.Model,
        draft.Effort, draft.DeclaredRole, draft.EffectiveGrant, draft.RequestedRequirements,
        draft.MissingCapabilities, draft.AdmissionDecision, draft.Outcome, draft.OutcomeDetail, draft.ReviewVerdict,
        draft.CheckConclusion, draft.ElapsedMilliseconds, draft.LastMeaningfulProgressAt?.ToUniversalTime(),
        draft.Usage, draft.ArtifactReferences);
}

/// <summary>
/// The sole append/replay seam for #2140. IDs and durable dedupe are assigned under one cross-process
/// mutex; the last valid id is read from both live and rollover files so rotation cannot reset it.
/// </summary>
public sealed class FleetEventLog
{
    private const string LockNamePrefix = "baton-fleet-events";
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly string _livePath;
    private readonly string _rolloverPath;
    private readonly long _maxLiveBytes;

    public FleetEventLog(string livePath, string rolloverPath, long maxLiveBytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(livePath);
        ArgumentException.ThrowIfNullOrEmpty(rolloverPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLiveBytes, 1);
        _livePath = livePath;
        _rolloverPath = rolloverPath;
        _maxLiveBytes = maxLiveBytes;
    }

    public Task<FleetEvent?> Append(FleetEventDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrEmpty(draft.DedupeKey);
        if (!FleetEventKinds.All.Contains(draft.Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(draft), draft.Kind, "Unknown fleet event kind.");
        }

        return Task.Run(() => MutexGuardedFileLock.RunUnderLock(
            _livePath, LockNamePrefix, LockTimeout, () => AppendLocked(draft)), cancellationToken);
    }

    /// <summary>Reads only the retained live file; <c>events.1.jsonl</c> is not operational replay.</summary>
    public Task<IReadOnlyList<FleetEvent>> ReadAfter(long cursor, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(cursor, 0);
        return Task.Run(() => MutexGuardedFileLock.RunUnderLock(
            _livePath, LockNamePrefix, LockTimeout,
            () => (IReadOnlyList<FleetEvent>)Read(_livePath).Where(e => e.Id > cursor).ToList()), cancellationToken);
    }

    internal static string Serialize(FleetEvent entry) => JsonSerializer.Serialize(entry, Json);

    private FleetEvent? AppendLocked(FleetEventDraft draft)
    {
        var retained = Read(_rolloverPath).Concat(Read(_livePath)).ToList();
        if (retained.Any(e => string.Equals(e.DedupeKey, draft.DedupeKey, StringComparison.Ordinal)))
        {
            return null;
        }

        var nextId = checked(retained.Select(e => e.Id).DefaultIfEmpty(0).Max() + 1);
        var entry = FleetEvent.From(nextId, draft);
        var line = JsonSerializer.Serialize(entry, Json) + "\n";
        var bytes = Encoding.UTF8.GetBytes(line);

        var parent = Path.GetDirectoryName(_livePath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        if (File.Exists(_livePath))
        {
            var length = new FileInfo(_livePath).Length;
            if (length > 0 && length + bytes.Length > _maxLiveBytes)
            {
                File.Move(_livePath, _rolloverPath, overwrite: true);
            }
        }

        using var stream = new FileStream(
            _livePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, bufferSize: 4096, useAsync: false);
        if (stream.Length > 0)
        {
            stream.Seek(-1, SeekOrigin.End);
            if (stream.ReadByte() != '\n')
            {
                stream.Seek(0, SeekOrigin.End);
                stream.WriteByte((byte)'\n');
            }
        }
        stream.Seek(0, SeekOrigin.End);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
        return entry;
    }

    private static IReadOnlyList<FleetEvent> Read(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var result = new List<FleetEvent>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                if (JsonSerializer.Deserialize<FleetEvent>(line, Json) is { } entry)
                {
                    result.Add(entry);
                }
            }
            catch (JsonException)
            {
                // A torn tail is not a fact. The next append is a complete new line and remains readable.
            }
        }

        return result;
    }
}
