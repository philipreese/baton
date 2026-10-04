using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
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
/// Missing, torn, conflicting, changed or bounded-out sources cannot produce a complete leaf.
/// Hints carry no authority. Automatic deletion remains held until separately proven final
/// removal and a rerun fence govern deletion.
/// </summary>
internal sealed class RoomRetentionEvidencePreparer
{
    private readonly Dictionary<string, SourceHint> _hints = new(BatonPaths.RecordKeyComparer);
    private int _cursor;
    internal int Cursor => _cursor;

    public async Task<int> PrepareAsync(
        string registryFilePath,
        int retentionDays,
        CancellationToken cancellationToken = default,
        Action<int>? beforeCandidate = null)
    {
        var started = Stopwatch.GetTimestamp();
        var sweepBudget = new RoomRetentionEvidenceStore.SweepBudget(started, cancellationToken);
        var discovery = await RoomsPruneCommand.DiscoverRetentionCandidatesAsync(
            registryFilePath,
            new RoomsPruneOptions(Terminal: true, OlderThanDays: retentionDays, State: null, DryRun: true, Yes: false),
            sweepBudget, cancellationToken, _cursor).ConfigureAwait(false);
        _cursor = discovery.NextCursor;
        var candidates = discovery.Candidates;

        if (candidates.Count == 0)
        {
            return 0;
        }

        var captured = 0;
        var attempted = 0;
        foreach (var candidate in candidates)
        {
            beforeCandidate?.Invoke(++attempted);
            cancellationToken.ThrowIfCancellationRequested();
            if (sweepBudget.Expired)
            {
                break;
            }

            var roomKey = RoomRetentionEvidenceStore.RoomKey(candidate.RoomDirectoryPath);

            if (candidate.SelectionRefused)
            {
                Console.Error.WriteLine($"RoomRetentionEvidence: retained '{candidate.RoomDirectoryPath}': {candidate.SelectionRefusalReason}");
                continue;
            }

            var elapsedBeforeCapture = candidate.SelectionElapsed;
            if (TryReadHint(candidate.RoomDirectoryPath, out var hint))
            {
                var hintStarted = Stopwatch.GetTimestamp();
                if (hint.MatchesCurrentCheapIdentity(sweepBudget, candidate.SelectionBytes,
                    elapsedBeforeCapture, cancellationToken))
                    continue;
                elapsedBeforeCapture += Stopwatch.GetElapsedTime(hintStarted);
            }

            try
            {
                var result = await RoomRetentionEvidenceStore.CaptureAsync(
                    candidate.RoomDirectoryPath,
                    roomKey,
                    sweepBudget,
                    cancellationToken,
                    selectionBytes: candidate.SelectionBytes,
                    selectionElapsed: elapsedBeforeCapture).ConfigureAwait(false);
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

internal enum RoomRetentionCapturePoint
{
    BeforeGuard,
    AfterJournalRead,
    BeforePublish,
    AfterTemporaryFlush,
    AfterPublication
}

internal sealed record CheapVerdictIdentity(string Path, bool Present, long Length, DateTime WriteUtc);

internal sealed record SourceHint(string RoomPath, long SentinelLength, DateTime SentinelWriteUtc, long JournalLength,
    DateTime JournalWriteUtc, long SnapshotLength, DateTime SnapshotWriteUtc,
    IReadOnlyList<CheapVerdictIdentity> Verdicts)
{
    public bool MatchesCurrentCheapIdentity(RoomRetentionEvidenceStore.SweepBudget sweep,
        long selectionBytes, TimeSpan selectionElapsed, CancellationToken cancellationToken)
    {
        var budget = new RoomRetentionEvidenceStore.ReadBudget(sweep, Stopwatch.GetTimestamp(), cancellationToken,
            RoomRetentionEvidenceLimits.MaxSourceBytesPerRoom - selectionBytes,
            RoomRetentionEvidenceLimits.RoomDeadline - selectionElapsed);
        try { budget.Check(); }
        catch (RoomRetentionEvidenceRefusalException) { return false; }
        if (!RoomRetentionEvidenceStore.TryGetCheapIdentity(RoomPath, out var current) ||
            SentinelLength != current.SentinelLength || SentinelWriteUtc != current.SentinelWriteUtc ||
            JournalLength != current.JournalLength || JournalWriteUtc != current.JournalWriteUtc ||
            SnapshotLength != current.SnapshotLength || SnapshotWriteUtc != current.SnapshotWriteUtc)
            return false;
        foreach (var verdict in Verdicts)
        {
            try { budget.Check(); }
            catch (RoomRetentionEvidenceRefusalException) { return false; }
            if (!RoomRetentionEvidenceStore.TryGetCheapVerdictIdentity(verdict.Path, out var currentVerdict) ||
                verdict != currentVerdict)
                return false;
        }
        try { budget.Check(); }
        catch (RoomRetentionEvidenceRefusalException) { return false; }
        return true;
    }
}

internal sealed record CheapSourceIdentity(long SentinelLength, DateTime SentinelWriteUtc, long JournalLength,
    DateTime JournalWriteUtc, long SnapshotLength, DateTime SnapshotWriteUtc);

public sealed record RoomRetentionEvidenceSources(
    [property: JsonPropertyName("snapshotSha256")] string SnapshotSha256,
    [property: JsonPropertyName("journalSha256")] string JournalSha256,
    [property: JsonPropertyName("sentinelSha256")] string SentinelSha256,
    [property: JsonPropertyName("verdictsSha256")] string? VerdictsSha256);

public sealed record RoomRetentionVerdictEvidence(
    [property: JsonPropertyName("executionId")] string ExecutionId,
    [property: JsonPropertyName("sourceIdentity")] string SourceIdentity,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("verdict")] JsonElement Verdict,
    [property: JsonPropertyName("sha256")] string Sha256);

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
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("producesVerdict")] bool ProducesVerdict);

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
    [property: JsonPropertyName("verdicts")] IReadOnlyList<RoomRetentionVerdictEvidence> Verdicts,
    [property: JsonPropertyName("knownUsage")] IReadOnlyDictionary<string, ExecutionUsageView>? KnownUsage,
    [property: JsonPropertyName("provenance")] IReadOnlyList<RoomRetentionExecutionProvenance> Provenance,
    [property: JsonPropertyName("payloadSha256")] string? PayloadSha256);

/// <summary>The one feature-specific immutable JSON leaf store for retention evidence.</summary>
public static class RoomRetentionEvidenceStore
{
    public const int SchemaVersion = 2;
    public const string SourceKind = "room-retention-as-of";
    public const string ReviewExpectation = "review";
    public const string NotApplicableExpectation = "not-applicable";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string RoomKey(string roomDirectoryPath)
    {
        var normalized = BatonPaths.RecordKey(roomDirectoryPath).ToUpperInvariant();
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

    internal static bool TryGetCheapVerdictIdentity(string path, out CheapVerdictIdentity identity)
    {
        identity = null!;
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0) return false;
            var file = new FileInfo(path);
            identity = new CheapVerdictIdentity(path, true, file.Length, file.LastWriteTimeUtc);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            identity = new CheapVerdictIdentity(path, false, 0, default);
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
        CancellationToken cancellationToken,
        Action<RoomRetentionCapturePoint>? probe = null,
        long selectionStarted = 0,
        long selectionBytes = 0,
        Action<long>? journalReadProgress = null,
        TimeSpan selectionElapsed = default)
    {
        var roomStarted = Stopwatch.GetTimestamp();
        roomDirectoryPath = BatonPaths.RecordKey(roomDirectoryPath);
        sweepBudget.Check();
        probe?.Invoke(RoomRetentionCapturePoint.BeforeGuard);
        if (!Directory.Exists(roomDirectoryPath) ||
            !IsPresentOrRefuse(Path.Combine(roomDirectoryPath, TerminalSentinelWriter.TerminalSentinelFileName)))
            throw new RoomRetentionEvidenceRefusalException("the selected room or sentinel disappeared");
        using var guard = AcquireGuard(roomDirectoryPath);
        var budget = new ReadBudget(sweepBudget, roomStarted, cancellationToken,
            RoomRetentionEvidenceLimits.MaxSourceBytesPerRoom - selectionBytes,
            RoomRetentionEvidenceLimits.RoomDeadline - selectionElapsed);
        budget.Check();
        if (!Directory.Exists(roomDirectoryPath) ||
            !IsPresentOrRefuse(Path.Combine(roomDirectoryPath, TerminalSentinelWriter.TerminalSentinelFileName)))
            throw new RoomRetentionEvidenceRefusalException("the guarded room or sentinel disappeared");

        if (Path.GetFileName(Path.TrimEndingDirectorySeparator(roomDirectoryPath))
                .Equals(ConductorRoomDetector.ConductorRole, StringComparison.OrdinalIgnoreCase) ||
            IsPresentOrRefuse(KeepMarker.MarkerFilePath(roomDirectoryPath)))
        {
            throw new RoomRetentionEvidenceRefusalException("the room is conductor-owned or kept");
        }

        SourceRead? bindingsRead = null;
        var bindingsPath = BatonPaths.RoomBindingsFile(roomDirectoryPath);
        if (IsPresentOrRefuse(bindingsPath))
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
                if (ConductorRoomDetector.IsConductorRole(sole))
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
        var journal = await ReadSourceAsync(Path.Combine(roomDirectoryPath, BatonPaths.FlowLogFileName), budget,
            afterChunk: journalReadProgress).ConfigureAwait(false);
        probe?.Invoke(RoomRetentionCapturePoint.AfterJournalRead);
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
        if (!string.Equals(status.State, WorkflowOutcome.Describe(projected), StringComparison.Ordinal))
        {
            throw new RoomRetentionEvidenceRefusalException("the terminal sentinel conflicts with the guarded projection");
        }

        var instant = TerminalInstantResolver.Resolve(entries, snapshotModel);
        var expectation = DetermineExpectation(flowEvents);
        var provenance = flowEvents.OfType<FlowEvent.ExecutionRequestAccepted>()
            .Select(accepted => new RoomRetentionExecutionProvenance(
                accepted.Request.ExecutionId.ToString(), accepted.Request.StepId?.ToString(), accepted.Request.Worker,
                accepted.Request.Adapter, accepted.Request.Model, IsReviewRequest(accepted)))
            .ToArray();

        var verdictReads = new List<SourceRead>();
        var absentVerdictPaths = new List<string>();
        var verdictSources = new List<(string Path, SourceRead? Read)>();
        var verdicts = new List<RoomRetentionVerdictEvidence>();
        if (expectation == ReviewExpectation)
        {
            var artifactsRoot = Path.Combine(roomDirectoryPath, ArtifactManager.ArtifactsDirectoryName);
            foreach (var review in flowEvents.OfType<FlowEvent.ExecutionRequestAccepted>().Where(IsReviewRequest))
            {
                var activePath = Path.Combine(ArtifactManager.ResolveOutputDirectory(artifactsRoot, review.Request.ExecutionId), "verdict.json");
                var prunedPath = Path.Combine(ArtifactManager.ResolvePrunedOutputDirectory(artifactsRoot, review.Request.ExecutionId), "verdict.json");
                var active = await TryReadOptionalSourceAsync(activePath, budget).ConfigureAwait(false);
                var pruned = await TryReadOptionalSourceAsync(prunedPath, budget).ConfigureAwait(false);
                verdictSources.Add((activePath, active));
                verdictSources.Add((prunedPath, pruned));
                if (active is not null && pruned is not null && !active.Bytes.AsSpan().SequenceEqual(pruned.Bytes))
                    throw new RoomRetentionEvidenceRefusalException("active and pruned verdict copies conflict");

                var selected = active ?? pruned;
                string? verdictError = null;
                if (selected is null || !ReviewVerdictSchema.TryParse(selected.Bytes, out _, out verdictError))
                    throw new RoomRetentionEvidenceRefusalException(
                        $"the expected review verdict is missing or invalid ({verdictError ?? "unavailable"})");

                var verdictText = Encoding.UTF8.GetString(selected.Bytes);
                if (!Encoding.UTF8.GetBytes(verdictText).AsSpan().SequenceEqual(selected.Bytes))
                    throw new RoomRetentionEvidenceRefusalException("the expected review verdict is not exact UTF-8");
                verdictReads.Add(selected);
                if (active is not null && pruned is not null) verdictReads.Add(pruned);
                if (active is null) absentVerdictPaths.Add(activePath);
                if (pruned is null) absentVerdictPaths.Add(prunedPath);
                verdicts.Add(new RoomRetentionVerdictEvidence(review.Request.ExecutionId.ToString(),
                    active is not null ? activePath : prunedPath, verdictText,
                    JsonDocument.Parse(selected.Bytes).RootElement.Clone(), Digest(selected.Bytes)));
            }
        }

        budget.Check();
        probe?.Invoke(RoomRetentionCapturePoint.BeforePublish);
        var latest = new[] { bindingsRead, snapshot, journal, sentinel }
            .Where(source => source is not null).Cast<SourceRead>().Concat(verdictReads).ToArray();
        foreach (var source in latest)
        {
            await VerifySourceAsync(source, budget).ConfigureAwait(false);
        }
        if (IsPresentOrRefuse(KeepMarker.MarkerFilePath(roomDirectoryPath)) ||
            (bindingsRead is null && IsPresentOrRefuse(bindingsPath)) ||
            absentVerdictPaths.Any(IsPresentOrRefuse))
            throw new RoomRetentionEvidenceRefusalException("a guarded selection source changed during capture");

        if (!TryGetCheapIdentity(roomDirectoryPath, out var identity))
        {
            throw new RoomRetentionEvidenceRefusalException("room source identity disappeared during capture");
        }

        var verdictIdentities = new List<CheapVerdictIdentity>(verdictSources.Count);
        foreach (var (path, read) in verdictSources)
        {
            budget.Check();
            if (!TryGetCheapVerdictIdentity(path, out var cheap) ||
                cheap.Present != (read is not null) ||
                (read is not null && (cheap.Length != read.Length || cheap.WriteUtc != read.LastWriteUtc)))
                throw new RoomRetentionEvidenceRefusalException("a verdict identity changed during capture");
            verdictIdentities.Add(cheap);
        }

        var sources = new RoomRetentionEvidenceSources(
            Digest(snapshot.Bytes), Digest(journal.Bytes), Digest(sentinel.Bytes),
            verdicts.Count == 0 ? null : VerdictsDigest(verdicts));
        var generation = GenerationDigest(sources);
        var usage = status.Steps
            .Where(step => step.Execution is not null && step.Usage is not null)
            .ToDictionary(step => step.Execution!, step => step.Usage!, StringComparer.Ordinal);
        var unsignedRecord = new RoomRetentionEvidenceRecord(
            SchemaVersion, roomKey, roomDirectoryPath, DateTimeOffset.UtcNow, SourceKind, generation,
            sources,
            new RoomRetentionTerminalFact(projected.Status.ToString(), status.State, instant.AtUtc, true),
            expectation, verdicts, usage.Count == 0 ? null : usage,
            provenance, null);
        var record = unsignedRecord with { PayloadSha256 = PayloadDigest(unsignedRecord) };

        await PublishAsync(record, budget, cancellationToken, probe).ConfigureAwait(false);
        var hint = new SourceHint(roomDirectoryPath, identity.SentinelLength, identity.SentinelWriteUtc,
            identity.JournalLength, identity.JournalWriteUtc, identity.SnapshotLength, identity.SnapshotWriteUtc,
            verdictIdentities);
        return (record, hint);
    }

    private static ConcurrencyGuard AcquireGuard(string roomDirectoryPath)
    {
        try
        {
            return ConcurrencyGuard.AcquireExisting(roomDirectoryPath, "room retention evidence");
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
        if (requests.Any(request => request.Request.ProducedOutputs is null &&
            !request.Request.Outputs.Any(output => string.Equals(output, "verdict.json", StringComparison.OrdinalIgnoreCase))))
        {
            throw new RoomRetentionEvidenceRefusalException("review expectation is unknown");
        }

        // An undefined frozen schema cannot prove whether a review verdict was required.
        if (requests.Any(request => request.Request.ProducedOutputs?.Any(output =>
            !Enum.IsDefined(output.Schema)) == true))
            throw new RoomRetentionEvidenceRefusalException("review expectation uses an unknown output schema");

        if (requests.Any(request => request.Request.ProducedOutputs?.Any(output =>
            output.Schema == OutputSchema.ReviewVerdict &&
            !string.Equals(output.Name, "verdict.json", StringComparison.OrdinalIgnoreCase)) == true))
            throw new RoomRetentionEvidenceRefusalException("a review verdict uses an unsupported output name");
        return requests.Any(IsReviewRequest) ? ReviewExpectation : NotApplicableExpectation;
    }

    private static bool IsReviewRequest(FlowEvent.ExecutionRequestAccepted accepted) =>
        accepted.Request.ProducedOutputs?.Any(output => output.Schema == OutputSchema.ReviewVerdict) == true ||
        (accepted.Request.ProducedOutputs is null &&
            accepted.Request.Outputs.Any(output => string.Equals(output, "verdict.json", StringComparison.OrdinalIgnoreCase)));

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
                if (!typeof(LogEntry).GetCustomAttributes<JsonDerivedTypeAttribute>()
                        .Any(known => known.DerivedType == entry.GetType()) ||
                    entry is LogEntry.FlowLogEntry flow &&
                    !typeof(FlowEvent).GetCustomAttributes<JsonDerivedTypeAttribute>()
                        .Any(known => known.DerivedType == flow.Event.GetType()) ||
                    entry is LogEntry.RoomLogEntry room &&
                    !typeof(RoomEvent).GetCustomAttributes<JsonDerivedTypeAttribute>()
                        .Any(known => known.DerivedType == room.Event.GetType()))
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

    private static async Task PublishAsync(RoomRetentionEvidenceRecord record, ReadBudget budget,
        CancellationToken cancellationToken, Action<RoomRetentionCapturePoint>? probe)
    {
        budget.Check();
        var leaf = LeafPath(record.RoomKey, record.GenerationSha256);
        Directory.CreateDirectory(Path.GetDirectoryName(leaf)!);
        var serialized = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
        _ = ValidatePublished(serialized, record.RoomKey, record.GenerationSha256);
        if (File.Exists(leaf))
        {
            var existing = ValidatePublished((await ReadSourceAsync(leaf, budget).ConfigureAwait(false)).Bytes,
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
            probe?.Invoke(RoomRetentionCapturePoint.AfterTemporaryFlush);

            try
            {
                budget.Check();
                File.Move(temp, leaf, overwrite: false);
                probe?.Invoke(RoomRetentionCapturePoint.AfterPublication);
            }
            catch (IOException)
            {
                if (!File.Exists(leaf))
                {
                    throw;
                }

                var existing = ValidatePublished((await ReadSourceAsync(leaf, budget).ConfigureAwait(false)).Bytes,
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

        _ = ValidatePublished((await ReadSourceAsync(leaf, budget).ConfigureAwait(false)).Bytes,
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

        if (record is null || record.Sources is null || record.Terminal is null || record.Provenance is null ||
            record.Verdicts is null || record.Verdicts.Any(item => item is null) ||
            record.Provenance.Any(item => item is null) ||
            record.SchemaVersion != SchemaVersion || record.SourceKind != SourceKind ||
            record.RoomKey != roomKey || record.GenerationSha256 != generation ||
            record.PayloadSha256 is null || !IsSha256(record.PayloadSha256) ||
            !IsCanonicalRoomIdentity(record.RoomIdentity, roomKey) || record.CapturedAtUtc == default ||
            !record.Terminal.IsTerminal || record.Terminal.ProjectionStatus != WorkflowStatus.Terminal.ToString() ||
            record.Terminal.SentinelState is null ||
            (record.VerdictExpectation != ReviewExpectation && record.VerdictExpectation != NotApplicableExpectation) ||
            (record.VerdictExpectation == NotApplicableExpectation &&
                (record.Verdicts.Count != 0 || record.Sources.VerdictsSha256 is not null)) ||
            (record.VerdictExpectation == ReviewExpectation &&
                (record.Verdicts.Count == 0 || record.Sources.VerdictsSha256 is null)) ||
            !IsSha256(record.Sources.SnapshotSha256) || !IsSha256(record.Sources.JournalSha256) ||
            !IsSha256(record.Sources.SentinelSha256) ||
            (record.Sources.VerdictsSha256 is not null && !IsSha256(record.Sources.VerdictsSha256)) ||
            record.Provenance.Any(item => !IsValidProvenance(item)) ||
            (record.KnownUsage is not null && record.KnownUsage.Any(item =>
                item.Value is null || !record.Provenance.Any(source => source.ExecutionId == item.Key))) ||
            record.Verdicts.Any(item => item.Verdict.ValueKind == JsonValueKind.Undefined ||
                item.Verdict.ValueKind == JsonValueKind.Null))
        {
            throw new RoomRetentionEvidenceRefusalException("the published leaf has an invalid shape or digest");
        }

        if (GenerationDigest(record.Sources) != record.GenerationSha256)
        {
            throw new RoomRetentionEvidenceRefusalException("the published leaf generation digest is inconsistent");
        }
        if (PayloadDigest(record) != record.PayloadSha256)
            throw new RoomRetentionEvidenceRefusalException("the published leaf payload digest is inconsistent");
        if (!bytes.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions)))
            throw new RoomRetentionEvidenceRefusalException("the published leaf has noncanonical or extra payload");

        foreach (var item in record.Verdicts)
        {
            if (!IsCanonicalVerdictSource(record.RoomIdentity, item) ||
                string.IsNullOrEmpty(item.Text) || !IsSha256(item.Sha256))
                throw new RoomRetentionEvidenceRefusalException("the published verdict has an invalid identity");
            var verdictBytes = Encoding.UTF8.GetBytes(item.Text);
            if (item.Sha256 != Digest(verdictBytes) ||
                !ReviewVerdictSchema.TryParse(verdictBytes, out _, out _))
            {
                throw new RoomRetentionEvidenceRefusalException("the published verdict payload is invalid");
            }
            try
            {
                using var parsed = JsonDocument.Parse(verdictBytes);
                if (!JsonElement.DeepEquals(parsed.RootElement, item.Verdict))
                    throw new RoomRetentionEvidenceRefusalException("the published verdict differs from its text");
            }
            catch (JsonException)
            {
                throw new RoomRetentionEvidenceRefusalException("the published verdict payload is malformed");
            }
        }

        if (record.Sources.VerdictsSha256 !=
            (record.Verdicts.Count == 0 ? null : VerdictsDigest(record.Verdicts)))
            throw new RoomRetentionEvidenceRefusalException("the published verdict set digest is inconsistent");
        if (!record.Provenance.Where(item => item.ProducesVerdict).Select(item => item.ExecutionId)
                .SequenceEqual(record.Verdicts.Select(item => item.ExecutionId)))
            throw new RoomRetentionEvidenceRefusalException("the published verdict execution set is incomplete");

        return record;
    }

    private static bool IsCanonicalRoomIdentity(string? identity, string roomKey)
    {
        if (string.IsNullOrWhiteSpace(identity)) return false;
        try
        {
            return Path.IsPathFullyQualified(identity) &&
                string.Equals(identity, BatonPaths.RecordKey(identity), StringComparison.Ordinal) &&
                (RoomKey(identity) == roomKey || LegacyRoomKey(identity) == roomKey);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsValidExecutionId(string? executionId) =>
        !string.IsNullOrWhiteSpace(executionId) && executionId != "." && executionId != ".." &&
        !executionId.Contains('/') && !executionId.Contains('\\') &&
        executionId.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static bool IsValidProvenance(RoomRetentionExecutionProvenance item) =>
        IsValidExecutionId(item.ExecutionId) &&
        (item.StepId is null || !string.IsNullOrWhiteSpace(item.StepId)) &&
        !string.IsNullOrWhiteSpace(item.Worker) &&
        (item.Adapter is null || !string.IsNullOrWhiteSpace(item.Adapter)) &&
        (item.Model is null || !string.IsNullOrWhiteSpace(item.Model));

    private static bool IsCanonicalVerdictSource(string roomIdentity, RoomRetentionVerdictEvidence item)
    {
        if (!IsValidExecutionId(item.ExecutionId) || string.IsNullOrWhiteSpace(item.SourceIdentity)) return false;
        try
        {
            if (!Path.IsPathFullyQualified(item.SourceIdentity)) return false;
            var artifacts = Path.Combine(roomIdentity, ArtifactManager.ArtifactsDirectoryName);
            var executionId = new ExecutionId(item.ExecutionId);
            var active = Path.Combine(ArtifactManager.ResolveOutputDirectory(artifacts, executionId), "verdict.json");
            var pruned = Path.Combine(ArtifactManager.ResolvePrunedOutputDirectory(artifacts, executionId), "verdict.json");
            return string.Equals(item.SourceIdentity, active, StringComparison.Ordinal) ||
                string.Equals(item.SourceIdentity, pruned, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool EquivalentCapture(RoomRetentionEvidenceRecord left, RoomRetentionEvidenceRecord right)
    {
        // Capture time is the observation instant, not generation identity. Concurrent/replayed
        // identical captures therefore converge on the first valid authoritative leaf.
        var leftBytes = JsonSerializer.SerializeToUtf8Bytes(left with { CapturedAtUtc = default, PayloadSha256 = null }, JsonOptions);
        var rightBytes = JsonSerializer.SerializeToUtf8Bytes(right with { CapturedAtUtc = default, PayloadSha256 = null }, JsonOptions);
        return leftBytes.AsSpan().SequenceEqual(rightBytes);
    }

    private static string PayloadDigest(RoomRetentionEvidenceRecord record) =>
        Digest(JsonSerializer.SerializeToUtf8Bytes(record with { PayloadSha256 = null }, JsonOptions));

    private static bool IsSha256(string? value) => value?.Length == 64 && value.All(Uri.IsHexDigit);

    private static string LegacyRoomKey(string roomDirectoryPath) =>
        Digest(Encoding.UTF8.GetBytes(BatonPaths.RecordKey(roomDirectoryPath)));

    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string GenerationDigest(RoomRetentionEvidenceSources sources)
    {
        using var stream = new MemoryStream();
        AppendTyped(stream, "snapshot", Encoding.UTF8.GetBytes(sources.SnapshotSha256));
        AppendTyped(stream, "complete-journal", Encoding.UTF8.GetBytes(sources.JournalSha256));
        AppendTyped(stream, "terminal-sentinel", Encoding.UTF8.GetBytes(sources.SentinelSha256));
        AppendTyped(stream, "selected-verdicts",
            sources.VerdictsSha256 is null ? null : Encoding.UTF8.GetBytes(sources.VerdictsSha256));
        return Digest(stream.ToArray());
    }

    private static string VerdictsDigest(IReadOnlyList<RoomRetentionVerdictEvidence> verdicts)
    {
        using var stream = new MemoryStream();
        foreach (var item in verdicts)
        {
            AppendTyped(stream, "execution", Encoding.UTF8.GetBytes(item.ExecutionId));
            AppendTyped(stream, "source", Encoding.UTF8.GetBytes(item.SourceIdentity));
            AppendTyped(stream, "verdict", Encoding.UTF8.GetBytes(item.Text));
        }
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

    internal static bool IsPresentOrRefuse(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RoomRetentionEvidenceRefusalException($"source '{path}' cannot be checked ({ex.Message})");
        }
    }

    internal static async Task<SourceRead?> TryReadOptionalSourceAsync(string path, ReadBudget budget,
        long? maxFileBytes = null)
    {
        if (!IsPresentOrRefuse(path)) return null;
        return await ReadSourceAsync(path, budget, maxFileBytes).ConfigureAwait(false);
    }

    internal static async Task<SourceRead> ReadSourceAsync(string path, ReadBudget budget, long? maxFileBytes = null,
        Action<long>? afterChunk = null)
    {
        budget.Check();
        FileInfo before;
        try { before = new FileInfo(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new RoomRetentionEvidenceRefusalException($"source '{path}' is unreadable"); }
        if (!before.Exists) throw new RoomRetentionEvidenceRefusalException($"source '{path}' is missing");
        if (before.Length > budget.RemainingRoomBytes || before.Length > budget.RemainingSweepBytes ||
            (maxFileBytes is { } max && before.Length > max))
            throw new RoomRetentionEvidenceRefusalException($"source '{path}' exceeds the bounded read budget");

        FileStream opened;
        try
        {
            opened = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RoomRetentionEvidenceRefusalException($"source '{path}' is unreadable ({ex.Message})");
        }
        await using var stream = opened;
        using var output = new MemoryStream(capacity: checked((int)Math.Min(before.Length, int.MaxValue)));
        var buffer = new byte[64 * 1024];
        while (true)
        {
            budget.Check();
            var allowed = (int)Math.Min(buffer.Length, Math.Min(budget.RemainingRoomBytes, budget.RemainingSweepBytes));
            if (maxFileBytes is { } limit)
                allowed = (int)Math.Min(allowed, limit + 1 - output.Length);
            if (allowed <= 0)
            {
                throw new RoomRetentionEvidenceRefusalException($"source '{path}' exceeds the bounded read budget");
            }

            int read;
            try { read = await stream.ReadAsync(buffer.AsMemory(0, allowed), budget.CancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new RoomRetentionEvidenceRefusalException($"source '{path}' became unreadable ({ex.Message})");
            }
            if (read == 0) break;
            budget.Consume(read);
            afterChunk?.Invoke(output.Length + read);
            if (maxFileBytes is { } bound && output.Length + read > bound)
                throw new RoomRetentionEvidenceRefusalException($"source '{path}' grew beyond its bounded size");
            await output.WriteAsync(buffer.AsMemory(0, read), budget.CancellationToken).ConfigureAwait(false);
        }

        FileInfo after;
        try { after = new FileInfo(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RoomRetentionEvidenceRefusalException($"source '{path}' cannot be rechecked ({ex.Message})");
        }
        if (after.Length != before.Length || after.LastWriteTimeUtc != before.LastWriteTimeUtc || output.Length != before.Length)
            throw new RoomRetentionEvidenceRefusalException($"source '{path}' changed while it was read");
        return new SourceRead(path, output.ToArray(), before.Length, before.LastWriteTimeUtc);
    }

    internal static ReadBudget DiscoveryBudget(SweepBudget sweep, CancellationToken cancellationToken,
        bool registry = false, long? started = null) =>
        new(sweep, started ?? Stopwatch.GetTimestamp(), cancellationToken,
            registry ? RoomRetentionEvidenceLimits.MaxSourceBytesPerSweep : RoomRetentionEvidenceLimits.MaxSourceBytesPerRoom,
            registry ? RoomRetentionEvidenceLimits.SweepDeadline : RoomRetentionEvidenceLimits.RoomDeadline);

    private static async Task VerifySourceAsync(SourceRead source, ReadBudget budget)
    {
        var reread = await ReadSourceAsync(source.Path, budget).ConfigureAwait(false);
        if (!source.Bytes.AsSpan().SequenceEqual(reread.Bytes) ||
            source.Length != reread.Length || source.LastWriteUtc != reread.LastWriteUtc)
            throw new RoomRetentionEvidenceRefusalException("a guarded source changed during capture");
    }

    private static async Task<byte[]> ReadBoundedFileAsync(string path, long maxBytes, long started, TimeSpan deadline,
        CancellationToken cancellationToken)
    {
        var budget = new ReadBudget(new SweepBudget(started, cancellationToken, maxBytes, deadline), started,
            cancellationToken, maxBytes, deadline);
        return (await ReadSourceAsync(path, budget).ConfigureAwait(false)).Bytes;
    }

    internal sealed class ReadBudget
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

    internal sealed record SourceRead(string Path, byte[] Bytes, long Length, DateTime LastWriteUtc);
}
