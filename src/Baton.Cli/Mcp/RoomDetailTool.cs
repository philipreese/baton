using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Baton.Vendors;
using Baton;
using Baton.Artifacts;
using Baton.Cli;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Status;
using Baton.Store;

namespace Baton.Cli.Mcp;

/// <summary>
/// The <c>room_detail</c> read-only MCP tool (#1427): <c>fleet_status</c>'s level-two drill-down,
/// scoped to a single room, for debugging one lane. Returns that room's <c>stdout</c> tail (from its
/// most recently written execution, or a caller-pinned one via the optional <c>execution</c>
/// argument) and a bounded projection of its <c>flow.jsonl</c> timeline (event type and timestamp per
/// line — never the raw event payloads, which stay MCP-unfriendly at fleet scale).
/// A sibling tool in the same host rather than a parameter on <c>fleet_status</c>: it answers a
/// different question (one room, deep) than a scan (every room, shallow), and the host already
/// composes sibling tools this way (<see cref="FleetStatusTool"/> plus <see cref="MemoryProposalTool"/>
/// and <see cref="YieldTool"/>, one CLI flag per tool in <c>Program.cs</c>) — the mailbox's own
/// <c>deliverables_list</c>/<c>deliverable_read</c> split (<c>tools/fleet-glass/worker.js</c>) is the
/// same precedent one level up the stack. Reads room files directly and reports whatever subset it
/// could get — spec/baton.md §6 pins the degradation contract (partial view over any throw).
/// </summary>
public sealed class RoomDetailTool : IMcpTool
{
    /// <summary>
    /// The cap on <em>rendered</em> text tailed from the end of the most recently written execution's
    /// <c>.stdout.log</c> -- UTF-16 characters of the prose <see cref="WorkerStreamLineRenderer"/>
    /// produces, not raw log bytes (#1574): a stream-json log's JSON envelopes are read and rendered
    /// in full before this cap is applied, so the number itself still names a byte-ish budget (64 KiB
    /// comfortably holds the last several dozen lines of typical CLI output, including a stack trace,
    /// while staying well clear of the token budget an MCP client spends rendering one tool result)
    /// but now measures the shorter, human-readable output rather than the raw stream.
    /// </summary>
    public const int DefaultStdoutTailBytes = 64 * 1024;

    /// <summary>
    /// Log lines tailed from the end of <c>flow.jsonl</c>'s projected timeline. A long-lived room can
    /// accumulate thousands of retry/step events; without a cap the timeline half of the result would
    /// grow unbounded while the stdout half stays capped at <see cref="DefaultStdoutTailBytes"/> —
    /// the same MCP-response-size reasoning applies to both halves.
    /// </summary>
    public const int DefaultTimelineTailEntries = 500;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>
    /// A throwaway options instance used only to read <see cref="JsonDerivedTypeAttribute"/> metadata
    /// via <see cref="JsonSerializerOptions.GetTypeInfo(Type)"/> — that API requires an explicit
    /// resolver and does not auto-attach the reflection-based default the way
    /// <see cref="JsonSerializer"/>'s own static methods do. Deliberately not
    /// <see cref="FlowEventLogJson.Options"/>: that type's own remarks forbid constructing a second
    /// *wire* options instance, but this one never (de)serializes a payload — it only asks the type
    /// system what discriminator each event's <see cref="JsonDerivedTypeAttribute"/> declares.
    /// </summary>
    private static readonly JsonSerializerOptions TypeMetadataOptions = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private static readonly IReadOnlyDictionary<Type, string> FlowEventTags = BuildEventTagMap<FlowEvent>();
    private static readonly IReadOnlyDictionary<Type, string> CoreEventTags = BuildEventTagMap<CoreEvent>();
    private static readonly IReadOnlyDictionary<Type, string> RoomEventTags = BuildEventTagMap<RoomEvent>();

    public string Name => "room_detail";

    public string Description =>
        "Read-only drill-down into one room: its most recent (or a pinned) execution's stdout tail " +
        "and a bounded " + BatonPaths.FlowLogFileName + " timeline projection (event type + timestamp per line), for " +
        "debugging one lane.";

    public string? AnnotationsJson => """{"readOnlyHint": true}""";

    public string InputSchemaJson => """
        {
          "type": "object",
          "properties": {
            "room": {
              "type": "string",
              "description": "Room name (resolved under the default rooms root and any extra roots) or an absolute room directory path."
            },
            "roots": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Optional extra directories containing rooms to search when 'room' is a name."
            },
            "execution": {
              "type": "string",
              "description": "Optional specific execution id whose stdout to read. Defaults to the most recently written execution's stdout, which is a heuristic: after a retry, the newest execution is not necessarily the one whose lane you meant."
            }
          },
          "required": ["room"],
          "additionalProperties": false
        }
        """;

    public McpToolCallResult Call(JsonElement arguments) =>
        CallAsync(arguments, CancellationToken.None).GetAwaiter().GetResult();

    public async Task<McpToolCallResult> CallAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        string? room = null;
        string? executionId = null;
        var extraRoots = new List<string>();

        if (arguments.ValueKind == JsonValueKind.Object)
        {
            if (arguments.TryGetProperty("room", out var roomElem) && roomElem.ValueKind == JsonValueKind.String)
            {
                room = roomElem.GetString();
            }

            if (arguments.TryGetProperty("execution", out var execElem) && execElem.ValueKind == JsonValueKind.String)
            {
                executionId = execElem.GetString();
            }

            if (arguments.TryGetProperty("roots", out var rootsElem) && rootsElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var rootItem in rootsElem.EnumerateArray())
                {
                    if (rootItem.ValueKind == JsonValueKind.String && rootItem.GetString() is { } rootPath && !string.IsNullOrWhiteSpace(rootPath))
                    {
                        extraRoots.Add(rootPath);
                    }
                }
            }
        }

        if (string.IsNullOrWhiteSpace(room))
        {
            return new McpToolCallResult("'room' is required.", IsError: true);
        }

        var roomDir = ResolveRoomDirectory(room, extraRoots);
        if (roomDir is null)
        {
            var notFound = new RoomDetailView(Name: room, Error: $"No room named '{room}' was found under the known roots.");
            return new McpToolCallResult(JsonSerializer.Serialize(notFound, SerializerOptions));
        }

        var roomName = Path.GetFileName(Path.TrimEndingDirectorySeparator(roomDir));
        var stdout = await ReadStdoutTailAsync(roomDir, executionId, cancellationToken).ConfigureAwait(false);
        var timeline = await ReadTimelineAsync(roomDir, cancellationToken).ConfigureAwait(false);

        string? note = null;
        if (stdout is null && timeline is null)
        {
            note = $"Room exists but has no captured stdout and no {BatonPaths.FlowLogFileName} yet.";
        }
        else if (stdout is null)
        {
            note = "No captured stdout yet for this room.";
        }
        else if (timeline is null)
        {
            note = $"Room has no {BatonPaths.FlowLogFileName} yet (pre-ledger).";
        }

        var view = new RoomDetailView(
            Name: roomName,
            Path: roomDir,
            Stdout: stdout,
            Timeline: timeline,
            Note: note);

        return new McpToolCallResult(JsonSerializer.Serialize(view, SerializerOptions));
    }

    private static string? ResolveRoomDirectory(string room, IReadOnlyList<string> extraRoots)
    {
        if (Path.IsPathFullyQualified(room) && Directory.Exists(room))
        {
            return room;
        }

        var searchRoots = new List<string>();
        if (Directory.Exists(BatonPaths.Rooms))
        {
            searchRoots.Add(BatonPaths.Rooms);
        }

        foreach (var extraRoot in extraRoots)
        {
            if (Directory.Exists(extraRoot))
            {
                searchRoots.Add(extraRoot);
            }
        }

        foreach (var searchRoot in searchRoots)
        {
            var candidate = Path.Combine(searchRoot, room);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static async Task<RoomStdoutTailView?> ReadStdoutTailAsync(
        string roomDir, string? executionId, CancellationToken cancellationToken)
    {
        (string Source, string StdoutPath, string ExecutionId)? found;
        try
        {
            found = executionId is not null
                ? FindStdoutFileForExecution(roomDir, executionId)
                : FindLatestStdoutFile(roomDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Discovery itself can race RoomRetentionSweep moving execution_* into pruned/ between
            // the existence check and the enumeration -- degrade like a failed read, never throw.
            return new RoomStdoutTailView(
                Text: string.Empty,
                Truncated: false,
                TotalBytes: 0,
                Source: "artifacts",
                ReadError: ex.Message);
        }

        if (found is null)
        {
            return null;
        }

        var (source, stdoutPath, resolvedExecutionId) = found.Value;

        byte[] bytes;
        long totalLength;
        try
        {
            await using var stream = new FileStream(stdoutPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            totalLength = stream.Length;

            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
            bytes = memory.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new RoomStdoutTailView(
                Text: string.Empty,
                Truncated: false,
                TotalBytes: 0,
                Source: source,
                ReadError: ex.Message);
        }

        // #1574: the cap below applies to RENDERED text, not raw bytes, so a claude/agy stream-json
        // log's dozens of JSON envelopes still yield the same window of prose an operator would have
        // gotten of raw JSON before -- reading the whole file (bounded by ExecutionStreamLogger's own
        // 8 MiB rollover cap) and truncating the rendered output is what makes that true; a byte-tail
        // read first would cut the raw window well before rendering had a chance to shrink it.
        var adapter = await ResolveAdapterForExecutionAsync(roomDir, resolvedExecutionId, cancellationToken).ConfigureAwait(false);

        var assembler = new StreamLineAssembler();
        var lines = assembler.Append(bytes);
        var rendered = new StringWriter { NewLine = "\n" };
        foreach (var line in lines)
        {
            WorkerStreamLineRenderer.RenderLine(line, adapter, rendered);
        }

        // A one-shot reader at EOF never sees a "next poll" that would complete a trailing line with
        // no newline yet (a crashed/killed worker, or simply a still-in-flight write) -- flush it
        // rather than silently dropping the exact content an operator debugging a crash wants most.
        if (assembler.Flush() is { Length: > 0 } finalPartialLine)
        {
            WorkerStreamLineRenderer.RenderLine(finalPartialLine, adapter, rendered);
        }

        var renderedText = rendered.ToString();
        var truncated = renderedText.Length > DefaultStdoutTailBytes;
        var text = renderedText;
        if (truncated)
        {
            text = renderedText[^DefaultStdoutTailBytes..];

            // A char-count slice of a decoded string can land inside a surrogate pair -- drop a lone
            // leading low surrogate rather than emit it broken.
            if (text.Length > 0 && char.IsLowSurrogate(text[0]))
            {
                text = text[1..];
            }

            // Drop a possibly-partial leading line so the tail starts clean, best-effort -- the same
            // approach the pre-#1574 raw-byte tail used, just applied to the rendered text.
            var firstNewline = text.IndexOf('\n');
            if (firstNewline >= 0 && firstNewline + 1 < text.Length)
            {
                text = text[(firstNewline + 1)..];
            }
        }

        return new RoomStdoutTailView(
            Text: text,
            Truncated: truncated,
            TotalBytes: totalLength,
            Source: source);
    }

    /// <summary>
    /// #1574 architectural constraint 1: resolves the adapter that produced this execution's stdout,
    /// via <see cref="RoomAdapterLookup"/> (see that type's doc comment for the resolution source and
    /// priority). A second, independent read of <c>flow.jsonl</c> from the one
    /// <see cref="ReadTimelineAsync"/> already does -- acceptable here
    /// since <c>room_detail</c> is an on-demand debug call, not a poll loop, and any failure fails
    /// open to a null adapter (raw JSON passthrough), never a throw.
    /// </summary>
    private static async Task<IWorkerAdapter?> ResolveAdapterForExecutionAsync(
        string roomDir, string executionId, CancellationToken cancellationToken)
    {
        var flowLogPath = Path.Combine(roomDir, BatonPaths.FlowLogFileName);
        if (!File.Exists(flowLogPath))
        {
            return null;
        }

        IReadOnlyList<FlowEvent> events;
        try
        {
            events = await new FlowEventLogReader(flowLogPath).ReadAllAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is BatonFlowException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var bindings = await RoomAdapterLookup.TryLoadBindingsAsync(roomDir, cancellationToken).ConfigureAwait(false);
        var adapterNameByExecutionId = RoomAdapterLookup.BuildAdapterNameByExecutionId(events, bindings);
        return RoomAdapterLookup.ResolveAdapter(executionId, adapterNameByExecutionId, WorkerAdapterRegistry.Default);
    }

    /// <summary>
    /// Finds the most recently written <c>.stdout.log</c> under the room's <c>artifacts</c>
    /// directory — the execution currently or most recently in flight, which is "the lane" a caller
    /// debugging one room means. Falls back to <c>artifacts/pruned</c> (#973) the same way
    /// <see cref="Baton.Status.ExecutionUsageView"/> does, so a retention-swept room still answers.
    /// </summary>
    private static (string Source, string StdoutPath, string ExecutionId)? FindLatestStdoutFile(string roomDir)
    {
        var artifactsRoot = Path.Combine(roomDir, ArtifactManager.ArtifactsDirectoryName);
        if (!Directory.Exists(artifactsRoot))
        {
            return null;
        }

        string? bestPath = null;
        var bestSource = string.Empty;
        var bestExecutionId = string.Empty;
        var bestTime = DateTime.MinValue;

        void Consider(string containerDir, bool pruned)
        {
            if (!Directory.Exists(containerDir))
            {
                return;
            }

            foreach (var executionDir in Directory.GetDirectories(containerDir, "execution_*"))
            {
                var stdoutPath = Path.Combine(executionDir, ExecutionStreamLogger.StdoutLogFileName);
                if (!File.Exists(stdoutPath))
                {
                    continue;
                }

                var lastWrite = File.GetLastWriteTimeUtc(stdoutPath);
                if (lastWrite >= bestTime)
                {
                    bestTime = lastWrite;
                    bestPath = stdoutPath;
                    var executionName = Path.GetFileName(Path.TrimEndingDirectorySeparator(executionDir));
                    bestSource = pruned ? $"{executionName} (pruned)" : executionName;
                    bestExecutionId = executionName.StartsWith("execution_", StringComparison.Ordinal)
                        ? executionName["execution_".Length..]
                        : executionName;
                }
            }
        }

        Consider(artifactsRoot, pruned: false);
        Consider(Path.Combine(artifactsRoot, ArtifactManager.PrunedDirectoryName), pruned: true);

        return bestPath is null ? null : (bestSource, bestPath, bestExecutionId);
    }

    /// <summary>
    /// Resolves a caller-pinned execution id directly, the same fallback order
    /// <see cref="Baton.Status.ExecutionUsageView"/> uses: the live output directory, then
    /// <c>artifacts/pruned</c> for a retention-swept execution. Exists because
    /// <see cref="FindLatestStdoutFile"/>'s "newest write wins" heuristic disagrees with the caller
    /// whenever a retried step's later execution has written more recently than the one being
    /// debugged.
    /// </summary>
    private static (string Source, string StdoutPath, string ExecutionId)? FindStdoutFileForExecution(string roomDir, string executionId)
    {
        var artifactsRoot = Path.Combine(roomDir, ArtifactManager.ArtifactsDirectoryName);
        var id = new ExecutionId(executionId);

        var liveDir = ArtifactManager.ResolveOutputDirectory(artifactsRoot, id);
        var liveStdout = Path.Combine(liveDir, ExecutionStreamLogger.StdoutLogFileName);
        if (File.Exists(liveStdout))
        {
            return (Path.GetFileName(Path.TrimEndingDirectorySeparator(liveDir)), liveStdout, executionId);
        }

        var prunedDir = ArtifactManager.ResolvePrunedOutputDirectory(artifactsRoot, id);
        var prunedStdout = Path.Combine(prunedDir, ExecutionStreamLogger.StdoutLogFileName);
        return File.Exists(prunedStdout)
            ? ($"{Path.GetFileName(Path.TrimEndingDirectorySeparator(prunedDir))} (pruned)", prunedStdout, executionId)
            : null;
    }

    /// <summary>
    /// One room's projected <c>flow.jsonl</c> timeline, or <see langword="null"/> where the room has no
    /// ledger at all. <c>internal</c> rather than private since #1902: <see cref="Baton.Cli.Daemon.FleetProjectionWriter"/>
    /// projects the SAME entries into the fleet projection file's <c>timelines</c> map, in-process,
    /// rather than reaching this tool over MCP the way <c>pusher.py</c>'s derive path does — one
    /// producer of timeline entries, two consumers.
    /// </summary>
    internal static async Task<RoomTimelineView?> ReadTimelineAsync(string roomDir, CancellationToken cancellationToken)
    {
        var logPath = Path.Combine(roomDir, BatonPaths.FlowLogFileName);
        if (!File.Exists(logPath))
        {
            return null;
        }

        var reader = new FlowEventLogReader(logPath);
        IReadOnlyList<LogEntry> entries;
        try
        {
            entries = await reader.ReadAllEntriesWithTimestampsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is BatonFlowException or IOException or UnauthorizedAccessException)
        {
            // BatonFlowException covers both a malformed line (FlowEventLogReadException) and a live
            // writer holding the ledger open (FlowJournalHeldException) -- neither is a room defect
            // this tool should throw over; both read as "can't show the timeline right now".
            return new RoomTimelineView(
                [new RoomTimelineEntryView(Type: "unreadable", Timestamp: null, Detail: ex.Message)],
                Truncated: false,
                TotalEntries: 1);
        }

        var totalEntries = entries.Count;
        var startIndex = Math.Max(0, totalEntries - DefaultTimelineTailEntries);

        var timeline = new List<RoomTimelineEntryView>(totalEntries - startIndex);
        for (var i = startIndex; i < totalEntries; i++)
        {
            var (type, timestamp, stepId, exitCode) = DescribeEntry(entries[i]);
            timeline.Add(new RoomTimelineEntryView(type, timestamp, stepId, exitCode));
        }

        return new RoomTimelineView(timeline, Truncated: startIndex > 0, TotalEntries: totalEntries);
    }

    private static (string Type, string? Timestamp, string? StepId, int? ExitCode) DescribeEntry(LogEntry entry)
    {
        return entry switch
        {
            LogEntry.FlowLogEntry flowEntry => (
                $"flow.{EventTypeTag(flowEntry.Event, FlowEventTags)}",
                flowEntry.WriterUtcTimestamp?.ToString("O"),
                FlowEventStepId(flowEntry.Event),
                null),
            LogEntry.CoreLogEntry coreEntry => (
                $"core.{EventTypeTag(coreEntry.Event, CoreEventTags)}",
                coreEntry.WriterUtcTimestamp?.ToString("O"),
                null,
                coreEntry.Event is CoreEvent.ExecutionExited exited ? exited.ExitCode : null),
            LogEntry.RoomLogEntry roomEntry => (
                $"room.{EventTypeTag(roomEntry.Event, RoomEventTags)}",
                roomEntry.WriterUtcTimestamp?.ToString("O"),
                roomEntry.Event is RoomEvent.RuntimePermissionAsked asked ? asked.StepId.Value : null,
                null),
            _ => ("unknown", null, null, null),
        };
    }

    /// <summary>
    /// #1613 item 4: step id where the FLOW event itself carries one directly. Why this stays a
    /// direct read rather than a cross-referenced lookup: spec/baton.md §6's room_detail schema
    /// entry.
    /// </summary>
    private static string? FlowEventStepId(FlowEvent @event) => @event switch
    {
        FlowEvent.ExecutionRequestAccepted accepted => accepted.Request.StepId?.Value,
        FlowEvent.WorkflowPaused paused => paused.StepId.Value,
        FlowEvent.ExternalDecisionRecorded decision => decision.TargetStepId?.Value,
        FlowEvent.StepRetryScheduled retry => retry.StepId.Value,
        FlowEvent.StepRebound rebound => rebound.StepId.Value,
        // #1608 review finding 10: carries StepId explicitly (FlowEvent.cs's own remarks on this
        // param stress that it does, precisely so a stale resolution is a guarded no-op rather than
        // misattributed) -- was falling to `_ => null` and losing that attribution here.
        FlowEvent.CaptureResolved resolved => resolved.StepId.Value,
        _ => null,
    };

    private static string EventTypeTag(object @event, IReadOnlyDictionary<Type, string> tags) =>
        tags.TryGetValue(@event.GetType(), out var tag) ? tag : "unknown";

    /// <summary>
    /// Builds a <c>CLR type -&gt; wire discriminator</c> map once per event base type, straight from
    /// the same <see cref="JsonDerivedTypeAttribute"/> declarations that
    /// <see cref="FlowEvent"/>/<see cref="CoreEvent"/>/<see cref="RoomEvent"/> already carry, so the
    /// tag can never drift from what the journal actually writes without also reflecting a literal
    /// restated here. Cheaper than round-tripping every log line through
    /// <see cref="JsonSerializer.SerializeToElement"/> just to read one string back off it.
    /// </summary>
    private static IReadOnlyDictionary<Type, string> BuildEventTagMap<TBase>()
    {
        var typeInfo = TypeMetadataOptions.GetTypeInfo(typeof(TBase));
        var map = new Dictionary<Type, string>();
        if (typeInfo.PolymorphismOptions is not null)
        {
            foreach (var derived in typeInfo.PolymorphismOptions.DerivedTypes)
            {
                if (derived.TypeDiscriminator is string tag)
                {
                    map[derived.DerivedType] = tag;
                }
            }
        }

        return map;
    }
}

/// <summary>Level-two drill-down for a single room (#1427): its stdout tail and event timeline.</summary>
public sealed record RoomDetailView(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("path")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Path = null,
    [property: JsonPropertyName("stdout")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    RoomStdoutTailView? Stdout = null,
    [property: JsonPropertyName("timeline")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    RoomTimelineView? Timeline = null,
    [property: JsonPropertyName("error")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Error = null,
    [property: JsonPropertyName("note")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Note = null);

/// <summary>The bounded tail of one room's most recently written execution's stdout.</summary>
public sealed record RoomStdoutTailView(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("totalBytes")] long TotalBytes,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("readError")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ReadError = null);

/// <summary>
/// The bounded (<see cref="RoomDetailTool.DefaultTimelineTailEntries"/>) tail of one room's
/// <c>flow.jsonl</c>, oldest-of-the-tail first.
/// </summary>
public sealed record RoomTimelineView(
    [property: JsonPropertyName("entries")] IReadOnlyList<RoomTimelineEntryView> Entries,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("totalEntries")] int TotalEntries);

/// <summary>
/// One <c>flow.jsonl</c> line projected to its event type and writer-stamped timestamp, plus
/// <see cref="StepId"/>/<see cref="ExitCode"/> where the underlying event carries one directly
/// (#1613 item 4 -- the content ruling that admits these two fields is spec/baton.md §6, not
/// restated here; <see cref="Detail"/> stays under the original content-free rule).
/// </summary>
public sealed record RoomTimelineEntryView(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("timestamp")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Timestamp = null,
    [property: JsonPropertyName("stepId")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? StepId = null,
    [property: JsonPropertyName("exitCode")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? ExitCode = null,
    [property: JsonPropertyName("detail")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Detail = null);
