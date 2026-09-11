using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Baton;
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

/// <summary>The only two code-authorship claims a lifecycle attempt may make.</summary>
[JsonConverter(typeof(FleetRevisionKindJsonConverter))]
public enum FleetRevisionKind
{
    Implementation,
    Repair,
}

internal sealed class FleetRevisionKindJsonConverter : JsonConverter<FleetRevisionKind>
{
    public override FleetRevisionKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var token = reader.GetString() ?? throw new JsonException("Expected a revision kind string.");
        foreach (var kind in Enum.GetValues<FleetRevisionKind>())
        {
            if (string.Equals(token, JsonNamingPolicy.CamelCase.ConvertName(kind.ToString()), StringComparison.Ordinal))
            {
                return kind;
            }
        }

        throw new JsonException($"Unknown revision kind '{token}'.");
    }

    public override void Write(Utf8JsonWriter writer, FleetRevisionKind value, JsonSerializerOptions options)
    {
        if (!Enum.IsDefined(value))
        {
            throw new JsonException($"Unknown revision kind numeric value '{(int)value}'.");
        }

        writer.WriteStringValue(JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));
    }
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
public readonly record struct FleetCheckRunId(string Value)
{
    public override string ToString() => Value;

    private sealed class Converter : StringIdJsonConverter<FleetCheckRunId>
    {
        protected override FleetCheckRunId Create(string value) => new(value);
        protected override string GetValue(FleetCheckRunId id) => id.Value;
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
    IReadOnlyList<string>? ArtifactReferences = null,
    FleetRevisionKind? RevisionKind = null,
    FleetCheckRunId? CheckRunId = null,
    string? CheckName = null,
    string? CheckStatus = null,
    DateTimeOffset? CheckStartedAt = null,
    DateTimeOffset? CheckCompletedAt = null);

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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? ArtifactReferences = null,
    [property: JsonPropertyName("revisionKind")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FleetRevisionKind? RevisionKind = null,
    [property: JsonPropertyName("checkRunId")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FleetCheckRunId? CheckRunId = null,
    [property: JsonPropertyName("checkName")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CheckName = null,
    [property: JsonPropertyName("checkStatus")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CheckStatus = null,
    [property: JsonPropertyName("checkStartedAt")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? CheckStartedAt = null,
    [property: JsonPropertyName("checkCompletedAt")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? CheckCompletedAt = null)
{
    internal static FleetEvent From(long id, FleetEventDraft draft) => new(
        id, draft.OccurredAt.ToUniversalTime(), draft.Kind, draft.DedupeKey, draft.AttemptId,
        draft.ParentAttemptId, draft.WorkId, draft.RoomId, draft.ExecutionId, draft.IssueId,
        draft.PullRequestId, draft.RevisionId, draft.ReviewRoundId, draft.Vendor, draft.Model,
        draft.Effort, draft.DeclaredRole, draft.EffectiveGrant, draft.RequestedRequirements,
        draft.MissingCapabilities, draft.AdmissionDecision, draft.Outcome, draft.OutcomeDetail, draft.ReviewVerdict,
        draft.CheckConclusion, draft.ElapsedMilliseconds, draft.LastMeaningfulProgressAt?.ToUniversalTime(),
        draft.Usage, draft.ArtifactReferences, draft.RevisionKind, draft.CheckRunId, draft.CheckName,
        draft.CheckStatus, draft.CheckStartedAt?.ToUniversalTime(), draft.CheckCompletedAt?.ToUniversalTime());
}

/// <summary>
/// A complete fleet-event row could not be read. Only a syntactically incomplete final row without
/// its newline is recoverable; every complete malformed row is durable-source corruption.
/// </summary>
public sealed class FleetEventLogReadException : BatonFlowException
{
    public FleetEventLogReadException(string filePath, int lineNumber, Exception innerException)
        : base(
            $"Fleet event log '{filePath}' contains a malformed complete row at line {lineNumber}: "
            + innerException.Message,
            innerException)
    {
        FilePath = filePath;
        LineNumber = lineNumber;
    }

    public string FilePath { get; }

    public int LineNumber { get; }
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

    internal string LivePath => _livePath;

    internal static FleetEventLog OpenOperational() =>
        new(
            BatonPaths.FleetEventsFile,
            BatonPaths.FleetEventsRolloverFile,
            RoomRetentionSweep.GetThresholdBytes());

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
            () => (IReadOnlyList<FleetEvent>)Read(_livePath).Events.Where(e => e.Id > cursor).ToList()), cancellationToken);
    }

    internal static string Serialize(FleetEvent entry) => JsonSerializer.Serialize(entry, Json);

    private FleetEvent? AppendLocked(FleetEventDraft draft)
    {
        var rollover = Read(_rolloverPath);
        var live = Read(_livePath);
        RemoveTornTail(_rolloverPath, rollover.TornTailOffset);
        RemoveTornTail(_livePath, live.TornTailOffset);

        var retained = rollover.Events.Concat(live.Events).ToList();
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

    private static FleetEventReadResult Read(string path)
    {
        if (!File.Exists(path))
        {
            return new([], null);
        }

        var result = new List<FleetEvent>();
        var bytes = File.ReadAllBytes(path);
        var offset = 0;
        var lineNumber = 0;
        while (offset < bytes.Length)
        {
            lineNumber++;
            var newline = Array.IndexOf(bytes, (byte)'\n', offset);
            var terminated = newline >= 0;
            var end = terminated ? newline : bytes.Length;
            if (end > offset && bytes[end - 1] == '\r')
            {
                end--;
            }

            var row = bytes.AsSpan(offset, end - offset);
            if (IsJsonWhitespace(row))
            {
                offset = terminated ? newline + 1 : bytes.Length;
                continue;
            }

            try
            {
                result.Add(ParseCompleteRow(row));
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException)
            {
                if (ex is JsonException && !terminated && IsIncompleteJsonPrefix(row))
                {
                    return new(result, offset);
                }

                throw new FleetEventLogReadException(path, lineNumber, ex);
            }

            offset = terminated ? newline + 1 : bytes.Length;
        }

        return new(result, null);
    }

    private static FleetEvent ParseCompleteRow(ReadOnlySpan<byte> row)
    {
        using var document = JsonDocument.Parse(row.ToArray());
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number
            || !id.TryGetInt64(out var parsedId) || parsedId <= 0
            || !root.TryGetProperty("at", out var at) || at.ValueKind != JsonValueKind.String
            || !at.TryGetDateTimeOffset(out _)
            || !root.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("dedupeKey", out var dedupeKey)
            || dedupeKey.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(dedupeKey.GetString()))
        {
            throw new JsonException("Expected id, at, kind, and non-empty dedupeKey fields.");
        }

        return root.Deserialize<FleetEvent>(Json)
            ?? throw new JsonException("Expected a fleet event object.");
    }

    private static bool IsIncompleteJsonPrefix(ReadOnlySpan<byte> row)
    {
        try
        {
            using var _ = JsonDocument.Parse(row.ToArray());
            return false;
        }
        catch (JsonException)
        {
            try
            {
                var reader = new Utf8JsonReader(row, isFinalBlock: false, state: default);
                while (reader.Read())
                {
                }

                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }

    private static bool IsJsonWhitespace(ReadOnlySpan<byte> row)
    {
        foreach (var value in row)
        {
            if (value != ' ' && value != '\t' && value != '\r')
            {
                return false;
            }
        }

        return true;
    }

    private static void RemoveTornTail(string path, long? offset)
    {
        if (offset is not { } truncateAt)
        {
            return;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
        stream.SetLength(truncateAt);
        stream.Flush(flushToDisk: true);
    }

    private sealed record FleetEventReadResult(IReadOnlyList<FleetEvent> Events, long? TornTailOffset);
}
