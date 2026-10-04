using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Artifacts;
using Baton.Cli;
using Baton.Concurrency;
using Baton.Domain;
using Baton.Projection;
using Baton.Status;
using Baton.Store;
using Baton.Vendors;

namespace Baton.Cli.Daemon;

/// <summary>
/// The bounded, archive-only preparation used by <see cref="RoomRetentionSweep"/>. A successful
/// record is immutable as-of history. It is never a deletion proof, accounting ledger, event, receipt,
/// or assertion that the current room is still terminal.
/// </summary>
internal sealed class RoomRetentionEvidencePreparer
{
    private readonly Dictionary<string, SourceHint> _hints = new(BatonPaths.RecordKeyComparer);
    private int _cursor;

    public async Task<int> PrepareAsync(
        string registryFilePath,
        int retentionDays,
        CancellationToken cancellationToken = default)
    {
        var candidates = await RoomsPruneCommand.DiscoverRetentionCandidatesAsync(
            registryFilePath,
            new RoomsPruneOptions(Terminal: true, OlderThanDays: retentionDays, State: null, DryRun: true, Yes: false),
            cancellationToken).ConfigureAwait(false);

        if (candidates.Count == 0)
        {
            _cursor = 0;
            return 0;
        }

        var started = Stopwatch.GetTimestamp();
        var sweepBudget = new RoomRetentionEvidenceStore.SweepBudget(started, cancellationToken);
        var captured = 0;
        var attempts = Math.Min(RoomRetentionEvidenceLimits.MaxAttemptsPerSweep, candidates.Count);
        for (var offset = 0; offset < attempts; offset++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sweepBudget.Expired)
            {
                break;
            }

            var index = (_cursor + offset) % candidates.Count;
            var candidate = candidates[index];
            var roomKey = RoomRetentionEvidenceStore.RoomKey(candidate.RoomDirectoryPath);
            _cursor = (index + 1) % candidates.Count;

            if (candidate.SelectionRefused)
            {
                Console.Error.WriteLine($"RoomRetentionEvidence: retained '{candidate.RoomDirectoryPath}': {candidate.SelectionRefusalReason}");
                continue;
            }

            if (TryReadHint(candidate.RoomDirectoryPath, out var hint) && hint.MatchesCurrentCheapIdentity())
            {
                continue;
            }

            try
            {
                var result = await RoomRetentionEvidenceStore.CaptureAsync(
                    candidate.RoomDirectoryPath,
                    roomKey,
                    sweepBudget,
                    cancellationToken).ConfigureAwait(false);
                if (result is not null)
                {
                    captured++;
                    _hints[candidate.RoomDirectoryPath] = result.Value.Hint;
                }
            }
            catch (RoomRetentionEvidenceRefusalException ex)
            {
                Console.Error.WriteLine($"RoomRetentionEvidence: retained '{candidate.RoomDirectoryPath}': {ex.Message}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"RoomRetentionEvidence: retained '{candidate.RoomDirectoryPath}': {ex.Message}");
            }
        }

        return captured;
    }

    private bool TryReadHint(string roomPath, out SourceHint hint)
    {
        if (_hints.TryGetValue(roomPath, out hint!))
        {
            return true;
        }

        hint = null!;
        return false;
    }
}

internal static class RoomRetentionEvidenceLimits
{
    public const int MaxAttemptsPerSweep = 8;
    public const long MaxSourceBytesPerSweep = 16 * 1024 * 1024;
    public const long MaxSourceBytesPerRoom = 8 * 1024 * 1024;
    public static readonly TimeSpan SweepDeadline = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan RoomDeadline = TimeSpan.FromSeconds(2);
    public const long MaxSelectionFileBytes = 1 * 1024 * 1024;
}

internal sealed class RoomRetentionEvidenceRefusalException(string message) : Exception(message);

internal sealed record SourceHint(string RoomPath, long SentinelLength, DateTime SentinelWriteUtc, long JournalLength,
    DateTime JournalWriteUtc, long SnapshotLength, DateTime SnapshotWriteUtc)
{
    public bool MatchesCurrentCheapIdentity()
    {
        return RoomRetentionEvidenceStore.TryGetCheapIdentity(
            RoomPath, out var current) &&
            SentinelLength == current.SentinelLength && SentinelWriteUtc == current.SentinelWriteUtc &&
            JournalLength == current.JournalLength && JournalWriteUtc == current.JournalWriteUtc &&
            SnapshotLength == current.SnapshotLength && SnapshotWriteUtc == current.SnapshotWriteUtc;
    }
}

internal sealed record CheapSourceIdentity(long SentinelLength, DateTime SentinelWriteUtc, long JournalLength,
    DateTime JournalWriteUtc, long SnapshotLength, DateTime SnapshotWriteUtc);

public sealed record RoomRetentionEvidenceSources(
    [property: JsonPropertyName("snapshotSha256")] string SnapshotSha256,
    [property: JsonPropertyName("journalSha256")] string JournalSha256,
    [property: JsonPropertyName("sentinelSha256")] string SentinelSha256,
    [property: JsonPropertyName("verdictSha256")] string? VerdictSha256);

public sealed record RoomRetentionTerminalFact(
    [property: JsonPropertyName("projectionStatus")] string ProjectionStatus,
    [property: JsonPropertyName("sentinelState")] string SentinelState,
    [property: JsonPropertyName("terminalAtUtc")] DateTime? TerminalAtUtc,
    [property: JsonPropertyName("isTerminal")] bool IsTerminal);

public sealed record RoomRetentionExecutionProvenance(
    [property: JsonPropertyName("executionId")] string ExecutionId,
    [property: JsonPropertyName("stepId")] string? StepId,
    [property: JsonPropertyName("worker")] string Worker,
    [property: JsonPropertyName("adapter")] string? Adapter,
    [property: JsonPropertyName("model")] string? Model);

public sealed record RoomRetentionEvidenceRecord(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("roomKey")] string RoomKey,
    [property: JsonPropertyName("roomIdentity")] string RoomIdentity,
    [property: JsonPropertyName("capturedAtUtc")] DateTimeOffset CapturedAtUtc,
    [property: JsonPropertyName("sourceKind")] string SourceKind,
    [property: JsonPropertyName("generationSha256")] string GenerationSha256,
    [property: JsonPropertyName("sources")] RoomRetentionEvidenceSources Sources,
    [property: JsonPropertyName("terminal")] RoomRetentionTerminalFact Terminal,
    [property: JsonPropertyName("verdictExpectation")] string VerdictExpectation,
    [property: JsonPropertyName("verdictExecutionId")] string? VerdictExecutionId,
    [property: JsonPropertyName("verdictSourceIdentity")] string? VerdictSourceIdentity,
    [property: JsonPropertyName("verdictText")] string? VerdictText,
    [property: JsonPropertyName("verdict")] JsonElement? Verdict,
    [property: JsonPropertyName("knownUsage")] IReadOnlyDictionary<string, ExecutionUsageView>? KnownUsage,
    [property: JsonPropertyName("provenance")] IReadOnlyList<RoomRetentionExecutionProvenance> Provenance);

/// <summary>The one feature-specific immutable JSON leaf store for retention evidence.</summary>
public static class RoomRetentionEvidenceStore
{
    public const int SchemaVersion = 1;
    public const string SourceKind = "room-retention-as-of";
    public const string ReviewExpectation = "review";
    public const string NotApplicableExpectation = "not-applicable";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string RoomKey(string roomDirectoryPath)
    {
        var normalized = BatonPaths.RecordKey(roomDirectoryPath);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }

    public static string LeafPath(string roomKey, string generationSha256)
    {
        if (!IsSha256(roomKey) || !IsSha256(generationSha256))
        {
            throw new ArgumentException("Room and generation keys must be lowercase or uppercase SHA-256 hex.");
        }

        return Path.Combine(BatonPaths.RoomRetentionEvidence, roomKey, $"{generationSha256}.json");
    }

    public static async Task<RoomRetentionEvidenceRecord?> ReadAsync(
        string roomKey, string generationSha256, CancellationToken cancellationToken = default)
    {
        var path = LeafPath(roomKey, generationSha256);
        if (!File.Exists(path))
        {
            return null;
        }

        var bytes = await ReadBoundedFileAsync(path, RoomRetentionEvidenceLimits.MaxSourceBytesPerRoom,
            Stopwatch.GetTimestamp(), RoomRetentionEvidenceLimits.RoomDeadline, cancellationToken).ConfigureAwait(false);
        return ValidatePublished(bytes, roomKey, generationSha256);
    }

    internal static bool TryGetCheapIdentity(string roomPath, out CheapSourceIdentity identity)
    {
        identity = null!;
        try
        {
            var sentinel = new FileInfo(Path.Combine(roomPath, TerminalSentinelWriter.TerminalSentinelFileName));
            var journal = new FileInfo(Path.Combine(roomPath, BatonPaths.FlowLogFileName));
            var snapshot = new FileInfo(Path.Combine(roomPath, BatonPaths.SnapshotFileName));
            if (!sentinel.Exists || !journal.Exists || !snapshot.Exists)
            {
                return false;
            }

            identity = new CheapSourceIdentity(sentinel.Length, sentinel.LastWriteTimeUtc, journal.Length,
                journal.LastWriteTimeUtc, snapshot.Length, snapshot.LastWriteTimeUtc);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static async Task<(RoomRetentionEvidenceRecord Record, SourceHint Hint)?> CaptureAsync(
        string roomDirectoryPath,
        string roomKey,
        SweepBudget sweepBudget,
        CancellationToken cancellationToken)
    {
        using var guard = AcquireGuard(roomDirectoryPath);
        var roomStarted = Stopwatch.GetTimestamp();
        var budget = new ReadBudget(sweepBudget, roomStarted, cancellationToken);

        if (Path.GetFileName(Path.TrimEndingDirectorySeparator(roomDirectoryPath))
                .Equals(ConductorRoomDetector.ConductorRole, StringComparison.OrdinalIgnoreCase) ||
            KeepMarker.IsKept(roomDirectoryPath))
        {
            throw new RoomRetentionEvidenceRefusalException("the room is conductor-owned or kept");
        }

        SourceRead? bindingsRead = null;
        var bindingsPath = BatonPaths.RoomBindingsFile(roomDirectoryPath);
        if (File.Exists(bindingsPath))
        {
            if (new FileInfo(bindingsPath).Length > RoomRetentionEvidenceLimits.MaxSelectionFileBytes)
            {
                throw new RoomRetentionEvidenceRefusalException("bindings.json exceeds the bounded selection size");
            }

            bindingsRead = await ReadSourceAsync(bindingsPath, budget).ConfigureAwait(false);
            try
            {
                var bindings = WorkerBindingConfigParser.Parse(Encoding.UTF8.GetString(bindingsRead.Bytes), bindingsPath);
                var sole = ConductorRoomDetector.TryResolveSoleBinding(bindings);
                if (sole is { Role: ConductorRoomDetector.ConductorRole })
                {
                    throw new RoomRetentionEvidenceRefusalException("the room is conductor-owned");
                }
            }
            catch (RoomRetentionEvidenceRefusalException)
            {
                throw;
            }
            catch (WorkerBindingConfigException ex)
            {
                throw new RoomRetentionEvidenceRefusalException($"bindings.json is malformed ({ex.Message})");
            }
        }

        var snapshot = await ReadSourceAsync(Path.Combine(roomDirectoryPath, BatonPaths.SnapshotFileName), budget).ConfigureAwait(false);
        var journal = await ReadSourceAsync(Path.Combine(roomDirectoryPath, BatonPaths.FlowLogFileName), budget).ConfigureAwait(false);
        var sentinel = await ReadSourceAsync(Path.Combine(roomDirectoryPath, TerminalSentinelWriter.TerminalSentinelFileName), budget).ConfigureAwait(false);

        if (journal.Bytes.Length == 0 || journal.Bytes[^1] != (byte)'\n')
        {
            throw new RoomRetentionEvidenceRefusalException("the workflow journal has a torn or incomplete tail");
        }

        var status = Deserialize<WorkflowStatusView>(sentinel.Bytes, "terminal sentinel");
        if (status.State.Equals(WorkflowOutcome.Indeterminate, StringComparison.OrdinalIgnoreCase))
        {
            throw new RoomRetentionEvidenceRefusalException("Indeterminate histories are unsupported");
        }

        var snapshotModel = Deserialize<WorkflowDefinitionSnapshot>(snapshot.Bytes, "workflow snapshot", SnapshotJson.Options);
        var entries = ParseJournal(journal.Bytes);
        var flowEvents = entries.OfType<LogEntry.FlowLogEntry>().Select(entry => entry.Event).ToList();
        if (flowEvents.OfType<FlowEvent.ExecutionRequestAccepted>().Any() is false)
        {
            throw new RoomRetentionEvidenceRefusalException("the history has no post-ledger execution evidence");
        }

        var projected = StateProjector.Project(flowEvents, snapshotModel);
        if (projected.Status != WorkflowStatus.Terminal || projected.Steps.Any(step => step.IndeterminateAwaitingResolution))
        {
            throw new RoomRetentionEvidenceRefusalException("the guarded projection is live or unresolved");
        }

        var instant = TerminalInstantResolver.Resolve(entries, snapshotModel);
        var expectation = DetermineExpectation(flowEvents);
        var provenance = flowEvents.OfType<FlowEvent.ExecutionRequestAccepted>()
            .Select(accepted => new RoomRetentionExecutionProvenance(
                accepted.Request.ExecutionId.ToString(), accepted.Request.StepId?.ToString(), accepted.Request.Worker,
                accepted.Request.Adapter, accepted.Request.Model))
            .ToArray();

        SourceRead? verdictRead = null;
        string? verdictExecutionId = null;
        string? verdictSourceIdentity = null;
        string? verdictText = null;
        JsonElement? verdict = null;
        if (expectation == ReviewExpectation)
        {
            var review = flowEvents.OfType<FlowEvent.ExecutionRequestAccepted>()
                .Where(IsReviewRequest).LastOrDefault()
                ?? throw new RoomRetentionEvidenceRefusalException("review expectation has no identified execution");
            verdictExecutionId = review.Request.ExecutionId.ToString();
            var artifactsRoot = Path.Combine(roomDirectoryPath, ArtifactManager.ArtifactsDirectoryName);
            var activePath = Path.Combine(ArtifactManager.ResolveOutputDirectory(artifactsRoot, review.Request.ExecutionId), "verdict.json");
            var prunedPath = Path.Combine(ArtifactManager.ResolvePrunedOutputDirectory(artifactsRoot, review.Request.ExecutionId), "verdict.json");
            var active = await TryReadOptionalSourceAsync(activePath, budget).ConfigureAwait(false);
            var pruned = await TryReadOptionalSourceAsync(prunedPath, budget).ConfigureAwait(false);
            if (active is not null && pruned is not null && !active.Bytes.AsSpan().SequenceEqual(pruned.Bytes))
            {
                throw new RoomRetentionEvidenceRefusalException("active and pruned verdict copies conflict");
            }

            verdictRead = active ?? pruned;
            string? verdictError = null;
            var verdictValid = verdictRead is not null &&
                ReviewVerdictSchema.TryParse(verdictRead.Bytes, out _, out verdictError);
            if (!verdictValid)
            {
                throw new RoomRetentionEvidenceRefusalException(
                    $"the expected review verdict is missing or invalid ({verdictError ?? "unavailable"})");
            }

            var selectedVerdict = verdictRead ??
                throw new RoomRetentionEvidenceRefusalException("the expected review verdict is missing");
            verdictSourceIdentity = active is not null ? activePath : prunedPath;
            verdictText = Encoding.UTF8.GetString(selectedVerdict.Bytes);
            verdict = JsonDocument.Parse(selectedVerdict.Bytes).RootElement.Clone();
        }

        budget.Check();
        var latest = new[] { bindingsRead, snapshot, journal, sentinel, verdictRead }
            .Where(source => source is not null).Cast<SourceRead>().ToArray();
        if (latest.Any(source => source.ChangedSinceRead()))
        {
            throw new RoomRetentionEvidenceRefusalException("a guarded source changed during capture");
        }

        if (!TryGetCheapIdentity(roomDirectoryPath, out var identity))
        {
            throw new RoomRetentionEvidenceRefusalException("room source identity disappeared during capture");
        }

        var sources = new RoomRetentionEvidenceSources(
            Digest(snapshot.Bytes), Digest(journal.Bytes), Digest(sentinel.Bytes),
            verdictRead is null ? null : Digest(verdictRead.Bytes));
        var generation = GenerationDigest(sources);
        var usage = status.Steps
            .Where(step => step.Execution is not null && step.Usage is not null)
            .ToDictionary(step => step.Execution!, step => step.Usage!, StringComparer.Ordinal);
        var record = new RoomRetentionEvidenceRecord(
            SchemaVersion, roomKey, BatonPaths.RecordKey(roomDirectoryPath), DateTimeOffset.UtcNow, SourceKind, generation,
            sources,
            new RoomRetentionTerminalFact(projected.Status.ToString(), status.State, instant.AtUtc, true),
            expectation, verdictExecutionId, verdictSourceIdentity, verdictText, verdict, usage.Count == 0 ? null : usage,
            provenance);

        await PublishAsync(record, cancellationToken).ConfigureAwait(false);
        var hint = new SourceHint(roomDirectoryPath, identity.SentinelLength, identity.SentinelWriteUtc,
            identity.JournalLength, identity.JournalWriteUtc, identity.SnapshotLength, identity.SnapshotWriteUtc);
        return (record, hint);
    }

    private static ConcurrencyGuard AcquireGuard(string roomDirectoryPath)
    {
        try
        {
            return ConcurrencyGuard.Acquire(roomDirectoryPath, "room retention evidence");
        }
        catch (WorkflowLockedException ex)
        {
            throw new RoomRetentionEvidenceRefusalException($"the room is live or guarded ({ex.Message})");
        }
        catch (IOException ex)
        {
            throw new RoomRetentionEvidenceRefusalException($"the room guard could not be acquired ({ex.Message})");
        }
    }

    private static string DetermineExpectation(IReadOnlyList<FlowEvent> events)
    {
        var requests = events.OfType<FlowEvent.ExecutionRequestAccepted>().ToArray();
        if (requests.Any(request => request.Request.Worker.Contains("unknown", StringComparison.OrdinalIgnoreCase)))
        {
            throw new RoomRetentionEvidenceRefusalException("review expectation is unknown");
        }

        return requests.Any(IsReviewRequest) ? ReviewExpectation : NotApplicableExpectation;
    }

    private static bool IsReviewRequest(FlowEvent.ExecutionRequestAccepted accepted) =>
        accepted.Request.Worker.Contains("review", StringComparison.OrdinalIgnoreCase) ||
        accepted.Request.Outputs.Any(output => string.Equals(output, "verdict.json", StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<LogEntry> ParseJournal(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var entries = new List<LogEntry>(lines.Length);
        foreach (var line in lines)
        {
            try
            {
                var entry = FlowEventLogJson.DeserializeLine(line);
                if (entry.GetType().Name.Contains("Unknown", StringComparison.Ordinal))
                {
                    throw new RoomRetentionEvidenceRefusalException("the workflow journal contains an unknown event");
                }

                entries.Add(entry);
            }
            catch (RoomRetentionEvidenceRefusalException)
            {
                throw;
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                throw new RoomRetentionEvidenceRefusalException($"the workflow journal is malformed ({ex.Message})");
            }
        }

        return entries;
    }

    private static T Deserialize<T>(byte[] bytes, string source, JsonSerializerOptions? options = null)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, options) ??
                throw new RoomRetentionEvidenceRefusalException($"the {source} is empty");
        }
        catch (RoomRetentionEvidenceRefusalException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new RoomRetentionEvidenceRefusalException($"the {source} is malformed ({ex.Message})");
        }
    }

    private static async Task PublishAsync(RoomRetentionEvidenceRecord record, CancellationToken cancellationToken)
    {
        var leaf = LeafPath(record.RoomKey, record.GenerationSha256);
        Directory.CreateDirectory(Path.GetDirectoryName(leaf)!);
        var serialized = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
        if (File.Exists(leaf))
        {
            var existing = ValidatePublished(await ReadBoundedFileAsync(leaf, RoomRetentionEvidenceLimits.MaxSourceBytesPerRoom,
                Stopwatch.GetTimestamp(), RoomRetentionEvidenceLimits.RoomDeadline, cancellationToken).ConfigureAwait(false),
                record.RoomKey, record.GenerationSha256);
            if (!EquivalentCapture(existing, record))
            {
                throw new RoomRetentionEvidenceRefusalException("the authoritative leaf conflicts with this capture");
            }

            return;
        }

        var temp = $"{leaf}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await stream.WriteAsync(serialized, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temp, leaf, overwrite: false);
            }
            catch (IOException)
            {
                if (!File.Exists(leaf))
                {
                    throw;
                }

                var existing = ValidatePublished(await ReadBoundedFileAsync(leaf, RoomRetentionEvidenceLimits.MaxSourceBytesPerRoom,
                    Stopwatch.GetTimestamp(), RoomRetentionEvidenceLimits.RoomDeadline, cancellationToken).ConfigureAwait(false),
                    record.RoomKey, record.GenerationSha256);
                if (!EquivalentCapture(existing, record))
                {
                    throw new RoomRetentionEvidenceRefusalException("a concurrent authoritative leaf conflicts with this capture");
                }
            }
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        _ = ValidatePublished(await ReadBoundedFileAsync(leaf, RoomRetentionEvidenceLimits.MaxSourceBytesPerRoom,
            Stopwatch.GetTimestamp(), RoomRetentionEvidenceLimits.RoomDeadline, cancellationToken).ConfigureAwait(false),
            record.RoomKey, record.GenerationSha256);
    }

    private static RoomRetentionEvidenceRecord ValidatePublished(byte[] bytes, string roomKey, string generation)
    {
        RoomRetentionEvidenceRecord? record;
        try
        {
            record = JsonSerializer.Deserialize<RoomRetentionEvidenceRecord>(bytes);
        }
        catch (JsonException ex)
        {
            throw new RoomRetentionEvidenceRefusalException($"the published leaf is malformed ({ex.Message})");
        }

        if (record is null || record.SchemaVersion != SchemaVersion || record.SourceKind != SourceKind ||
            record.RoomKey != roomKey || record.GenerationSha256 != generation ||
            !IsSha256(record.Sources.SnapshotSha256) || !IsSha256(record.Sources.JournalSha256) ||
            !IsSha256(record.Sources.SentinelSha256) ||
            (record.Sources.VerdictSha256 is not null && !IsSha256(record.Sources.VerdictSha256)) ||
            (record.VerdictExpectation == ReviewExpectation && string.IsNullOrEmpty(record.VerdictText)))
        {
            throw new RoomRetentionEvidenceRefusalException("the published leaf has an invalid shape or digest");
        }

        if (GenerationDigest(record.Sources) != record.GenerationSha256)
        {
            throw new RoomRetentionEvidenceRefusalException("the published leaf generation digest is inconsistent");
        }

        if (record.VerdictText is not null)
        {
            var verdictBytes = Encoding.UTF8.GetBytes(record.VerdictText);
            if (record.Sources.VerdictSha256 != Digest(verdictBytes) ||
                !ReviewVerdictSchema.TryParse(verdictBytes, out _, out _))
            {
                throw new RoomRetentionEvidenceRefusalException("the published verdict payload is invalid");
            }
        }

        return record;
    }

    private static bool EquivalentCapture(RoomRetentionEvidenceRecord left, RoomRetentionEvidenceRecord right)
    {
        // Capture time is the observation instant, not generation identity. Concurrent/replayed
        // identical captures therefore converge on the first valid authoritative leaf.
        var leftBytes = JsonSerializer.SerializeToUtf8Bytes(left with { CapturedAtUtc = default }, JsonOptions);
        var rightBytes = JsonSerializer.SerializeToUtf8Bytes(right with { CapturedAtUtc = default }, JsonOptions);
        return leftBytes.AsSpan().SequenceEqual(rightBytes);
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string GenerationDigest(RoomRetentionEvidenceSources sources)
    {
        using var stream = new MemoryStream();
        AppendTyped(stream, "snapshot", Encoding.UTF8.GetBytes(sources.SnapshotSha256));
        AppendTyped(stream, "complete-journal", Encoding.UTF8.GetBytes(sources.JournalSha256));
        AppendTyped(stream, "terminal-sentinel", Encoding.UTF8.GetBytes(sources.SentinelSha256));
        AppendTyped(stream, "selected-verdict",
            sources.VerdictSha256 is null ? null : Encoding.UTF8.GetBytes(sources.VerdictSha256));
        return Digest(stream.ToArray());
    }

    private static void AppendTyped(Stream stream, string type, byte[]? bytes)
    {
        var typeBytes = Encoding.UTF8.GetBytes(type);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, typeBytes.Length);
        stream.Write(length);
        stream.Write(typeBytes);
        BinaryPrimitives.WriteInt32BigEndian(length, bytes?.Length ?? -1);
        stream.Write(length);
        if (bytes is not null) stream.Write(bytes);
    }

    private static async Task<SourceRead?> TryReadOptionalSourceAsync(string path, ReadBudget budget)
    {
        if (!File.Exists(path)) return null;
        return await ReadSourceAsync(path, budget).ConfigureAwait(false);
    }

    private static async Task<SourceRead> ReadSourceAsync(string path, ReadBudget budget)
    {
        budget.Check();
        FileInfo before;
        try { before = new FileInfo(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new RoomRetentionEvidenceRefusalException($"source '{path}' is unreadable"); }
        if (!before.Exists) throw new RoomRetentionEvidenceRefusalException($"source '{path}' is missing");
        if (before.Length > budget.RemainingRoomBytes || before.Length > budget.RemainingSweepBytes)
            throw new RoomRetentionEvidenceRefusalException($"source '{path}' exceeds the bounded read budget");

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var output = new MemoryStream(capacity: checked((int)Math.Min(before.Length, int.MaxValue)));
        var buffer = new byte[64 * 1024];
        while (true)
        {
            budget.Check();
            if (stream.Position >= before.Length)
            {
                break;
            }

            var allowed = (int)Math.Min(buffer.Length, Math.Min(budget.RemainingRoomBytes, budget.RemainingSweepBytes));
            if (allowed <= 0)
            {
                throw new RoomRetentionEvidenceRefusalException($"source '{path}' exceeds the bounded read budget");
            }

            var read = await stream.ReadAsync(buffer.AsMemory(0, allowed), budget.CancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            budget.Consume(read);
            await output.WriteAsync(buffer.AsMemory(0, read), budget.CancellationToken).ConfigureAwait(false);
        }

        var after = new FileInfo(path);
        if (after.Length != before.Length || after.LastWriteTimeUtc != before.LastWriteTimeUtc || output.Length != before.Length)
            throw new RoomRetentionEvidenceRefusalException($"source '{path}' changed while it was read");
        return new SourceRead(path, output.ToArray(), before.Length, before.LastWriteTimeUtc);
    }

    private static async Task<byte[]> ReadBoundedFileAsync(string path, long maxBytes, long started, TimeSpan deadline,
        CancellationToken cancellationToken)
    {
        var budget = new ReadBudget(new SweepBudget(started, cancellationToken, maxBytes, deadline), started,
            cancellationToken, maxBytes, deadline);
        return (await ReadSourceAsync(path, budget).ConfigureAwait(false)).Bytes;
    }

    private sealed class ReadBudget
    {
        private long _roomBytes;
        private readonly SweepBudget _sweepBudget;
        private readonly long _roomStarted;
        private readonly TimeSpan _deadline;
        public CancellationToken CancellationToken { get; }
        public long RemainingRoomBytes => _roomBytes;
        public long RemainingSweepBytes => _sweepBudget.Remaining;

        public ReadBudget(SweepBudget sweepBudget, long roomStarted, CancellationToken cancellationToken,
            long? roomBytes = null, TimeSpan? deadline = null)
        {
            _sweepBudget = sweepBudget;
            _roomStarted = roomStarted;
            _roomBytes = roomBytes ?? RoomRetentionEvidenceLimits.MaxSourceBytesPerRoom;
            _deadline = deadline ?? RoomRetentionEvidenceLimits.RoomDeadline;
            CancellationToken = cancellationToken;
        }

        public void Consume(int bytes)
        {
            _roomBytes -= bytes;
            _sweepBudget.Consume(bytes);
        }

        public void Check()
        {
            CancellationToken.ThrowIfCancellationRequested();
            _sweepBudget.Check();
            if (_roomBytes < 0 || Stopwatch.GetElapsedTime(_roomStarted) >= _deadline)
                throw new RoomRetentionEvidenceRefusalException("the cooperative retention-evidence bound was exceeded");
        }
    }

    internal sealed class SweepBudget
    {
        private long _remaining;
        private readonly long _started;
        private readonly TimeSpan _deadline;
        private readonly CancellationToken _cancellationToken;

        public SweepBudget(long started, CancellationToken cancellationToken,
            long? bytes = null, TimeSpan? deadline = null)
        {
            _started = started;
            _cancellationToken = cancellationToken;
            _remaining = bytes ?? RoomRetentionEvidenceLimits.MaxSourceBytesPerSweep;
            _deadline = deadline ?? RoomRetentionEvidenceLimits.SweepDeadline;
        }

        public long Remaining => _remaining;
        public bool Expired => Stopwatch.GetElapsedTime(_started) >= _deadline;
        public void Consume(int bytes) => _remaining -= bytes;

        public void Check()
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (_remaining < 0 || Expired)
                throw new RoomRetentionEvidenceRefusalException("the cooperative retention-evidence bound was exceeded");
        }
    }

    private sealed record SourceRead(string Path, byte[] Bytes, long Length, DateTime LastWriteUtc)
    {
        public bool ChangedSinceRead()
        {
            var info = new FileInfo(Path);
            return !info.Exists || info.Length != Length || info.LastWriteTimeUtc != LastWriteUtc;
        }
    }
}
