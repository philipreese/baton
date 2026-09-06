using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Baton.Artifacts;
using Baton.Cli.Mcp;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Mutation;
using Baton.Outcomes;
using Baton.Status;
using Baton.Store;
using Microsoft.Extensions.Hosting;

namespace Baton.Cli.Daemon;

/// <summary>
/// #1557: the daemon's fourth kept responsibility (spec/baton.md §7) — periodically writes
/// <see cref="BatonPaths.FleetProjectionFile"/>; that property's own doc has the cadence and why.
/// Mirrors <see cref="RoomRetentionSweep"/>'s <see cref="BackgroundService"/> shape and
/// env-var-configurable-interval-with-clamped-bounds pattern.
/// </summary>
/// <remarks>
/// <para>
/// Reuses <see cref="FleetStatusTool.DiscoverRoomsAsync"/>/<see cref="FleetStatusTool.ProcessRoomAsync"/>
/// in-process (same assembly) rather than going through the MCP tool's JSON-in/JSON-out wrapper — the
/// exact room list and per-room projection <c>fleet_status</c> itself would return, serialized with the
/// SAME <see cref="FleetStatusTool.SerializerOptions"/>. This PR (PR-A) adds no pusher.py change: both
/// paths run side by side until #1557's own PR-B.
/// </para>
/// <para>
/// <b>PR-A2 (#1557)</b> added <c>rooms[].live.stdoutTail</c> — <see cref="StdoutTailRenderer"/>'s own
/// doc comment is the port record for that field. <b>Still not in this PR (see the tracking issue's PR
/// slicing):</b> pending-outputs status — grepped <see cref="Status.StepOutputResolver"/> first, per
/// spec/baton.md §6's own remark on why that grep came up empty.
/// </para>
/// <para>
/// <b>#1902</b> added the top-level <c>timelines</c> map (room path → entries), the last field the
/// <c>file</c> source was missing relative to <c>derive</c> — <see cref="ResolveTimelineAsync"/> and
/// <see cref="ProjectTimeline"/> carry the policy and the content projection.
/// </para>
/// </remarks>
public sealed class FleetProjectionWriter : BackgroundService
{
    public const string IntervalSecondsEnvironmentVariable = "BATON_FLEET_PROJECTION_INTERVAL_SECONDS";

    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(30);

    // Same reasoning as RoomRetentionSweep.MinInterval/MaxInterval: bounded so a pathological env value
    // can neither overflow TimeSpan.FromSeconds nor hot-loop ExecuteAsync.
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxInterval = TimeSpan.FromDays(1);

    // spec/baton.md §6: `LAST_ACTIVITY_BUCKET_SECONDS` in pusher.py -- floors a Running room's stdout
    // mtime to this bucket before it enters the payload, so a continuously-streaming lane's every-chunk
    // mtime advance does not itself change the file every cycle.
    private const int LastActivityBucketSeconds = 90;

    // spec/baton.md §6 (#1155): newest N pruned execution dirs surfaced per room.
    private const int PrunedItemsCap = 20;

    // #1902: `TIMELINE_CAP` in pusher.py -- the newest N timeline entries kept per room. Named here
    // rather than repeated as a literal for the same reason LastActivityBucketSeconds is: the two
    // implementations project the same field and a silent divergence is invisible in the pushed body.
    private const int TimelineCap = 30;

    private readonly Dictionary<string, ExecutionLiveState> _liveCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PrunedCacheEntry> _prunedCache = new(StringComparer.Ordinal);

    // #1902: the terminal-room cache ResolveTimelineAsync's own remarks describe -- pusher.py's
    // `terminal_timeline_cache` is its counterpart. In-memory only: a restart self-heals. Plain CLR
    // entries, not JsonNode: a JsonNode has a single parent, so a cached node re-attached on the next
    // tick would throw (ComputePrunedInfo's DeepClone is the other way out of the same trap).
    private readonly Dictionary<string, IReadOnlyList<ProjectedTimelineEntry>> _terminalTimelineCache =
        new(StringComparer.Ordinal);

    private bool _loggedMissingSecretPatterns;

    public static TimeSpan GetInterval()
    {
        var val = BatonEnvironmentSnapshot.Current.FleetProjectionIntervalSecondsOverride;
        if (!string.IsNullOrWhiteSpace(val) &&
            double.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out var seconds) &&
            seconds > 0)
        {
            return TimeSpan.FromSeconds(Math.Clamp(seconds, MinInterval.TotalSeconds, MaxInterval.TotalSeconds));
        }

        return DefaultInterval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await WriteOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FleetProjectionWriter: iteration failed: {ex.Message}");
            }

            try
            {
                await Task.Delay(GetInterval(), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One tick's worth of work — public entry point for tests, and what <see cref="ExecuteAsync"/> loops.</summary>
    internal async Task WriteOnceAsync(CancellationToken cancellationToken = default)
    {
        var json = await BuildProjectionJsonAsync(cancellationToken).ConfigureAwait(false);
        WriteAtomic(BatonPaths.FleetProjectionFile, json);
    }

    /// <param name="diagnostics">Sink for the one-per-process missing-denylist log line (#1816) —
    /// defaults to <see cref="Console.Error"/>; a test supplies its own <see cref="TextWriter"/> rather
    /// than mutating the process-global <see cref="Console.Error"/>, which xunit's parallel test
    /// collections would otherwise race on.</param>
    internal async Task<string> BuildProjectionJsonAsync(CancellationToken cancellationToken, TextWriter? diagnostics = null)
    {
        diagnostics ??= Console.Error;
        var discovered = await FleetStatusTool.DiscoverRoomsAsync([], cancellationToken).ConfigureAwait(false);
        var roomsArray = new JsonArray();
        var timelines = new JsonObject();
        var liveKeysThisTick = new HashSet<string>(StringComparer.Ordinal);
        var liveLanesByVendor = new Dictionary<string, int>(StringComparer.Ordinal);

        // pusher.py's main() loop reloads its secret-gate denylist every cycle (not once at startup),
        // so an operator's edit to the patterns file takes effect on the NEXT tick rather than needing
        // a daemon restart -- matched here rather than caching across ticks.
        var secretPatterns = StdoutTailRenderer.LoadSecretPatterns(BatonPaths.SecretPatternsFile);
        if (secretPatterns is null && !_loggedMissingSecretPatterns)
        {
            // #1816: LoadSecretPatterns' fail-closed null withholds every stdoutTail line -- that stays
            // fail-closed, but silently was how the daemon and the pusher drifted onto two different
            // paths in the first place. Logged once per process (not every ~30s tick) since a missing
            // denylist is an operator setup gap, not a per-tick event worth repeating.
            _loggedMissingSecretPatterns = true;
            diagnostics.WriteLine(
                $"FleetProjectionWriter: secret-gate denylist not found at {BatonPaths.SecretPatternsFile} -- WITHHOLDING EVERY stdoutTail line (fail closed)");
        }

        foreach (var room in discovered)
        {
            var view = await FleetStatusTool.ProcessRoomAsync(room.RoomDir, includeTerminal: true, cancellationToken)
                .ConfigureAwait(false);
            if (view is null)
            {
                continue;
            }

            if (room.Project is not null)
            {
                view = view with { Project = room.Project };
            }

            var node = JsonSerializer.SerializeToNode(view, FleetStatusTool.SerializerOptions)!.AsObject();

            var pruned = ComputePrunedInfo(view.Path);
            if (pruned is not null)
            {
                node["pruned"] = pruned;
            }

            // #1513: a Running STEP whose engine is confirmed dead downgrades the room-level State to
            // "Stalled" (FleetStatusTool's own display-only override) -- the step's own State stays
            // "Running" regardless. processAlive/stdout_last_write_ago_sec/elapsed exist precisely to
            // diagnose that case, so they gate on the step, never on the room's display state. `live`
            // (spec/baton.md §6, pre-existing pusher.py contract) stays narrower -- gated inside
            // AttachLiveFieldsAsync on the room's displayed State being exactly "Running", matching
            // pusher's own "never a live section a dead process cannot honestly back" rule.
            if (view.Steps?.Any(s => s.State == "Running" && s.Execution is not null) == true)
            {
                await AttachLiveFieldsAsync(node, view, secretPatterns, liveKeysThisTick, cancellationToken).ConfigureAwait(false);
            }

            // #1391: same "Running room's own adapter" tally VendorUsageProjectionReader.CountLiveLanesByVendor
            // computes from FleetStatusTool's own results -- inlined here since this loop already holds
            // `view` (ProcessRoomAsync has already applied #1513's Stalled display override to
            // view.State by this point, so this reads identically to that helper).
            if (view.State == "Running" && view.Adapter is { } adapter)
            {
                liveLanesByVendor[adapter] = liveLanesByVendor.GetValueOrDefault(adapter) + 1;
            }

            var timelineEntries = await ResolveTimelineAsync(view.Path, diagnostics, cancellationToken)
                .ConfigureAwait(false);
            if (timelineEntries.Count > 0)
            {
                timelines[view.Path] = RenderTimeline(timelineEntries);
            }

            roomsArray.Add(node);
        }

        PruneLiveCache(liveKeysThisTick);
        PrunePrunedCache(discovered);
        PruneTerminalTimelineCache(discovered);

        var root = new JsonObject
        {
            ["derived_at"] = DateTimeOffset.UtcNow.ToString("O"),
            ["rooms"] = roomsArray,

            // #1902: room path -> timeline entries, the field pusher.py's `file` path reads straight
            // through (`projection_data["timelines"]`) instead of spending a `room_detail` MCP call per
            // room per cycle. Always present, `{}` when no room has a readable timeline -- the absent
            // key is what the pre-#1902 file looked like, and the pusher treats the two the same.
            ["timelines"] = timelines,
        };

        // #1391: same vendors[] block fleet_status returns, using the liveLanesByVendor tally this
        // tick's own room loop already built above -- no second room scan.
        var vendors = VendorUsageProjectionReader.ReadAll(liveLanesByVendor);
        if (vendors is not null)
        {
            root["vendors"] = JsonSerializer.SerializeToNode(vendors, FleetStatusTool.SerializerOptions);
        }

        return root.ToJsonString(FleetStatusTool.SerializerOptions);
    }

    /// <summary>
    /// spec/baton.md §6's <c>rooms[].live</c> (now including <c>stdoutTail</c>, #1557 PR-A2) /
    /// <c>processAlive</c>/<c>stdout_last_write_ago_sec</c>/<c>elapsed</c> — everything that needs the
    /// Running step's own execution id. Absent (never a fabricated reading) whenever a Running room's
    /// steps carry no Running execution id, its stdout has not been captured yet, or the step's own
    /// timestamp is unreadable.
    /// </summary>
    private async Task AttachLiveFieldsAsync(
        JsonObject node,
        FleetRoomStatusView view,
        IReadOnlyList<Regex>? secretPatterns,
        HashSet<string> liveKeysThisTick,
        CancellationToken cancellationToken)
    {
        var runningStep = view.Steps?.FirstOrDefault(s => s.State == "Running" && s.Execution is not null);
        if (runningStep is null)
        {
            return;
        }

        var executionId = runningStep.Execution!;
        var liveKey = $"{view.Path}::{executionId}";
        liveKeysThisTick.Add(liveKey);

        var (stdoutPath, rolloverPath) = FindStdoutPaths(view.Path, executionId);
        if (stdoutPath is not null)
        {
            var state = GetOrCreateLiveState(liveKey, view.Adapter);
            ReadIncrementalInto(state, stdoutPath, rolloverPath);

            var liveNode = new JsonObject();
            if (state.Monitor is not null)
            {
                var usage = state.Monitor.SnapshotUsage();
                liveNode["toolCalls"] = state.Monitor.SnapshotToolStepCount();
                if (usage.BilledTokens is { } billedTokens)
                {
                    liveNode["billedTokens"] = billedTokens;
                }

                if (usage.BilledIsFloor)
                {
                    liveNode["billedIsFloor"] = true;
                }

                if (usage.Turns is { } turns)
                {
                    liveNode["turns"] = turns;
                }

                if (usage.ContextLevelTokens is { } contextTokens)
                {
                    liveNode["contextTokens"] = contextTokens;
                }

                // #1812: the LATEST line's reading (WorkerUsage.CacheReadLevelTokens), not the running
                // Σ TokenBudgetMonitor also tracks (WorkerUsage.CacheReadTokens, display-only per that
                // field's own doc) -- pusher.py's derive path replaces this value per turn rather than
                // summing it, so the projection has to report the same level or the compare's identity
                // check reads a structural sum-vs-level mismatch as an ~8x drift.
                if (usage.CacheReadLevelTokens is { } cacheReadTokens)
                {
                    liveNode["cacheReadTokens"] = cacheReadTokens;
                }
            }

            DateTime mtimeUtc;
            try
            {
                mtimeUtc = File.GetLastWriteTimeUtc(stdoutPath);
            }
            catch (IOException)
            {
                mtimeUtc = DateTime.UtcNow;
            }

            liveNode["lastActivityAt"] = QuantizeActivity(mtimeUtc);

            // #1557 PR-A2: same stdoutPath FindStdoutPaths already resolved above -- never a second
            // way of finding it. A snapshot read from EOF every tick (not fed by the incremental
            // offset ReadIncrementalInto tracks), matching pusher.py's own "the tail is a snapshot of
            // now, not an accumulator" design.
            var stdoutTail = StdoutTailRenderer.ComputeTail(stdoutPath, secretPatterns);
            if (stdoutTail is not null)
            {
                liveNode["stdoutTail"] = stdoutTail;
            }

            // #1793: same stdoutPath, one more read of the SAME tail window -- StdoutTailRenderer's
            // own doc comment on ComputeDoingNow is the port record.
            var doingNow = StdoutTailRenderer.ComputeDoingNow(stdoutPath, secretPatterns);
            if (doingNow is not null)
            {
                liveNode["doingNow"] = doingNow;
            }

            // spec/baton.md §6 (pre-existing pusher.py contract): `live` itself stays gated on the
            // room's DISPLAYED state being exactly "Running" -- never a live section for a room #1513
            // already downgraded to "Stalled" once its engine is confirmed dead, matching pusher's own
            // "never a live section a dead process cannot honestly back" rule. processAlive below is
            // deliberately NOT behind this gate -- it is the diagnostic that explains a Stalled room.
            if (view.State == "Running")
            {
                node["live"] = liveNode;
            }

            var agoSec = Math.Max(0, (DateTime.UtcNow - mtimeUtc).TotalSeconds);
            node["stdout_last_write_ago_sec"] = Math.Round(agoSec, 1);
        }

        if (runningStep.Timestamp is { } timestamp &&
            DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var startedAt))
        {
            var elapsedSec = Math.Max(0, (DateTimeOffset.UtcNow - startedAt).TotalSeconds);
            node["elapsed"] = Math.Round(elapsedSec, 1);
        }

        var (pid, startTime) = await TryGetEngineIdentityAsync(view.Path, executionId, cancellationToken).ConfigureAwait(false);
        var probe = EngineLivenessProbe.Probe(pid, startTime);
        node["processAlive"] = probe.Status switch
        {
            EngineLivenessStatus.Alive => "alive",
            EngineLivenessStatus.Dead => "dead",
            _ => "unknown",
        };
    }

    private ExecutionLiveState GetOrCreateLiveState(string liveKey, string? adapter)
    {
        if (_liveCache.TryGetValue(liveKey, out var existing))
        {
            return existing;
        }

        var parser = adapter is not null && StandardWorkerUsageParsers.Default.TryGetValue(adapter, out var resolved)
            ? resolved
            : null;

        var state = new ExecutionLiveState
        {
            // #1682: pure accumulation, no arrest triggers -- the daemon only ever reads, never cancels
            // a room's own dispatch process.
            Monitor = parser is not null ? new TokenBudgetMonitor(budget: null, maxToolSteps: null, billedRateLimit: null, parser) : null,
        };
        _liveCache[liveKey] = state;
        return state;
    }

    private void PruneLiveCache(HashSet<string> liveKeysThisTick)
    {
        foreach (var staleKey in _liveCache.Keys.Where(k => !liveKeysThisTick.Contains(k)).ToList())
        {
            _liveCache.Remove(staleKey);
        }
    }

    private void PrunePrunedCache(IReadOnlyList<FleetStatusTool.DiscoveredRoom> discovered)
    {
        var roomPaths = new HashSet<string>(discovered.Select(r => r.RoomDir), StringComparer.Ordinal);
        foreach (var staleKey in _prunedCache.Keys.Where(k => !roomPaths.Contains(k)).ToList())
        {
            _prunedCache.Remove(staleKey);
        }
    }

    private void PruneTerminalTimelineCache(IReadOnlyList<FleetStatusTool.DiscoveredRoom> discovered)
    {
        var roomPaths = new HashSet<string>(discovered.Select(r => r.RoomDir), StringComparer.Ordinal);
        foreach (var staleKey in _terminalTimelineCache.Keys.Where(k => !roomPaths.Contains(k)).ToList())
        {
            _terminalTimelineCache.Remove(staleKey);
        }
    }

    /// <summary>
    /// #1902 — one room's timeline entries for this tick, the daemon-side counterpart of pusher.py's
    /// <c>resolve_room_timeline</c>. A room is terminal once its <c>terminal.json</c> exists (the same
    /// sentinel pusher's <c>is_terminal_room</c> keys on, not the displayed state): a terminal room's
    /// ledger is frozen, so it is read once and served from <see cref="_terminalTimelineCache"/>
    /// afterwards, while a non-terminal room's still-growing timeline is re-read every tick.
    /// <para>
    /// Empty for a room with no <c>flow.jsonl</c>, none it could read, or no projectable entry — the
    /// caller then writes no <c>timelines</c> key for it at all, matching derive's own <c>if entries:</c>.
    /// <b>Deliberate divergence from <c>extract_timeline</c>:</b> that function keeps
    /// <see cref="RoomDetailTool.ReadTimelineAsync"/>'s synthetic <c>unreadable</c> marker as a
    /// type-only "something is wrong here" entry; the daemon omits the room instead. The projection
    /// file is a cache of facts read off disk, and the fail-closed answer for a fact this tick could
    /// not read is to say nothing rather than to publish a marker that would then persist in
    /// <c>glass.html</c>'s localStorage long after the transient lock that produced it cleared.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<ProjectedTimelineEntry>> ResolveTimelineAsync(
        string roomPath, TextWriter diagnostics, CancellationToken cancellationToken)
    {
        var isTerminal = File.Exists(Path.Combine(roomPath, TerminalSentinelWriter.TerminalSentinelFileName));
        if (isTerminal && _terminalTimelineCache.TryGetValue(roomPath, out var cached))
        {
            return cached;
        }

        IReadOnlyList<ProjectedTimelineEntry> entries;
        try
        {
            entries = ProjectTimeline(await RoomDetailTool.ReadTimelineAsync(roomPath, cancellationToken)
                .ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One room's timeline must never sink the tick -- the same per-room guard derive keeps
            // around its own room_detail call. ReadTimelineAsync already absorbs the expected read
            // failures into its `unreadable` marker, so reaching here means something unforeseen.
            diagnostics.WriteLine($"FleetProjectionWriter: timeline read failed for {roomPath}: {ex.Message}");
            return [];
        }

        if (isTerminal && entries.Count > 0)
        {
            _terminalTimelineCache[roomPath] = entries;
        }

        return entries;
    }

    /// <summary>
    /// pusher.py's <c>extract_timeline</c> content projection, in C#: KEEP ONLY <c>type</c>,
    /// <c>timestamp</c>, <c>stepId</c> and <c>exitCode</c> off each entry, capped at the newest
    /// <see cref="TimelineCap"/>. Like that function it enumerates what it KEEPS rather than what it
    /// drops, so a future <see cref="RoomTimelineEntryView"/> field cannot leak into the projection by
    /// this code failing to name it — which is also why the view is projected by hand here instead of
    /// serialized, since serializing it would carry <c>detail</c> (an exception message) straight out.
    /// No event type is filtered, deliberately (#1537): the vocabulary is whatever the engine journals.
    /// </summary>
    private static IReadOnlyList<ProjectedTimelineEntry> ProjectTimeline(RoomTimelineView? timeline)
    {
        if (timeline is null)
        {
            return [];
        }

        var projected = new List<ProjectedTimelineEntry>(timeline.Entries.Count);
        foreach (var entry in timeline.Entries)
        {
            if (entry.Type == "unreadable")
            {
                // See ResolveTimelineAsync's remarks: the marker is dropped rather than published.
                return [];
            }

            projected.Add(new ProjectedTimelineEntry(entry.Type, entry.Timestamp, entry.StepId, entry.ExitCode));
        }

        return projected.Count > TimelineCap
            ? projected.GetRange(projected.Count - TimelineCap, TimelineCap)
            : projected;
    }

    private static JsonArray RenderTimeline(IReadOnlyList<ProjectedTimelineEntry> entries)
    {
        var array = new JsonArray();
        foreach (var entry in entries)
        {
            var node = new JsonObject { ["type"] = entry.Type };
            if (entry.Timestamp is not null)
            {
                node["timestamp"] = entry.Timestamp;
            }

            if (entry.StepId is not null)
            {
                node["stepId"] = entry.StepId;
            }

            if (entry.ExitCode is { } exitCode)
            {
                node["exitCode"] = exitCode;
            }

            array.Add(node);
        }

        return array;
    }

    /// <summary>
    /// Two-location fallback for a Running execution's own captured stream — the SAME addressing
    /// <c>ExecutionUsageProjector</c>'s terminal-usage read already uses
    /// (<see cref="ArtifactManager.ResolveOutputDirectory"/>, falling back to
    /// <see cref="ArtifactManager.ResolvePrunedOutputDirectory"/> for a retention-swept execution), not
    /// a reimplementation of pusher.py's own path logic.
    /// </summary>
    private static (string? StdoutPath, string? RolloverPath) FindStdoutPaths(string roomPath, string executionId)
    {
        var artifactsRootPath = Path.Combine(roomPath, ArtifactManager.ArtifactsDirectoryName);
        var id = new ExecutionId(executionId);

        foreach (var directory in new[]
                 {
                     ArtifactManager.ResolveOutputDirectory(artifactsRootPath, id),
                     ArtifactManager.ResolvePrunedOutputDirectory(artifactsRootPath, id),
                 })
        {
            var candidate = Path.Combine(directory, ExecutionStreamLogger.StdoutLogFileName);
            if (File.Exists(candidate))
            {
                var rollover = Path.Combine(directory, ExecutionStreamLogger.StdoutRolloverFileName);
                return (candidate, File.Exists(rollover) ? rollover : null);
            }
        }

        return (null, null);
    }

    /// <summary>
    /// Byte-offset incremental read plus rollover detection — the daemon's own port of pusher.py's
    /// <c>_read_new_lines</c>/rollover heuristic (no C# precedent existed; <c>ExecutionStreamLogger</c>
    /// writes the rollover, nothing before this read it back incrementally). A size DECREASE since the
    /// offset last read is the rollover signal: <c>.stdout.log</c> rolls to <c>.stdout.log.1</c> at 8
    /// MiB and resets to empty.
    /// </summary>
    private static void ReadIncrementalInto(ExecutionLiveState state, string stdoutPath, string? rolloverPath)
    {
        long currentSize;
        try
        {
            currentSize = new FileInfo(stdoutPath).Length;
        }
        catch (IOException)
        {
            return;
        }

        if (currentSize < state.StdoutOffset)
        {
            state.RolloverOffset = Math.Max(state.RolloverOffset, state.StdoutOffset);
            state.StdoutOffset = 0;
        }

        if (rolloverPath is not null)
        {
            var (rolloverLines, newRolloverOffset) = ReadNewLines(rolloverPath, state.RolloverOffset);
            state.RolloverOffset = newRolloverOffset;
            foreach (var line in rolloverLines)
            {
                state.Monitor?.OnStdoutLine(line);
            }
        }

        var (newLines, newStdoutOffset) = ReadNewLines(stdoutPath, state.StdoutOffset);
        state.StdoutOffset = newStdoutOffset;
        foreach (var line in newLines)
        {
            state.Monitor?.OnStdoutLine(line);
        }
    }

    /// <summary>
    /// Complete lines appended to <paramref name="path"/> since byte <paramref name="offset"/>, and the
    /// new offset positioned right after the last complete line consumed. A trailing partial line (the
    /// vendor CLI mid-flush, no newline yet) is left unconsumed so it is read whole next cycle instead
    /// of split across two parses — mirrors pusher.py's <c>_read_new_lines</c>.
    /// </summary>
    private static (List<string> Lines, long NewOffset) ReadNewLines(string path, long offset)
    {
        byte[] chunk;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var seekOffset = offset > stream.Length ? 0 : offset;
            stream.Seek(seekOffset, SeekOrigin.Begin);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            chunk = buffer.ToArray();
            offset = seekOffset;
        }
        catch (IOException)
        {
            return ([], offset);
        }

        if (chunk.Length == 0)
        {
            return ([], offset);
        }

        var text = Encoding.UTF8.GetString(chunk);
        var lastNewline = text.LastIndexOf('\n');
        if (lastNewline == -1)
        {
            return ([], offset);
        }

        var complete = text[..lastNewline];
        var consumed = Encoding.UTF8.GetByteCount(complete) + 1;
        return ([.. complete.Split('\n')], offset + consumed);
    }

    private static string QuantizeActivity(DateTime mtimeUtc)
    {
        var epochSeconds = new DateTimeOffset(DateTime.SpecifyKind(mtimeUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();
        var bucketed = epochSeconds / LastActivityBucketSeconds * LastActivityBucketSeconds;
        return DateTimeOffset.FromUnixTimeSeconds(bucketed).ToString("O");
    }

    /// <summary>
    /// The Running execution's own recorded engine identity — the same
    /// <c>FlowEvent.ExecutionRequestAccepted.EnginePid</c>/<c>EngineStartTime</c>
    /// <see cref="EngineLivenessProbe"/>'s every other caller reads (<c>StatusCommand</c>,
    /// <c>MutationInterface</c>). <see cref="FleetStatusTool.ProcessRoomAsync"/> already reads
    /// <c>flow.jsonl</c> once for its own projection but does not return the raw events, so this is a
    /// second, Running-room-only read of the same file rather than a threaded-through return value —
    /// cheap next to the 30s cadence, and it keeps <c>ProcessRoomAsync</c>'s own signature untouched.
    /// </summary>
    private static async Task<(int? Pid, DateTimeOffset? StartTime)> TryGetEngineIdentityAsync(
        string roomDir, string executionId, CancellationToken cancellationToken)
    {
        try
        {
            var logPath = Path.Combine(roomDir, BatonPaths.FlowLogFileName);
            if (!File.Exists(logPath))
            {
                return (null, null);
            }

            var reader = new FlowEventLogReader(logPath);
            var entries = await reader.ReadAllEntriesWithTimestampsAsync(cancellationToken).ConfigureAwait(false);

            // Last match, not first -- MutationInterface.RecordResumeAsync's own identical
            // EnginePid/EngineStartTime lookup uses .LastOrDefault. No execution id gets a second
            // ExecutionRequestAccepted under current code (every dispatch/resume mints a fresh
            // ExecutionId), so this cannot currently change the result -- matching the established
            // idiom rather than depending on that invariant is the point.
            FlowEvent.ExecutionRequestAccepted? match = null;
            foreach (var entry in entries)
            {
                if (entry is LogEntry.FlowLogEntry { Event: FlowEvent.ExecutionRequestAccepted accepted } &&
                    accepted.Request.ExecutionId.Value == executionId)
                {
                    match = accepted;
                }
            }

            if (match is not null)
            {
                return (match.EnginePid, match.EngineStartTime);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, null);
        }

        return (null, null);
    }

    /// <summary>
    /// spec/baton.md §6 (#1155) — port of pusher.py's <c>pruned_info_for_room</c>: present only for a
    /// room whose <c>artifacts/pruned/</c> directory is non-empty, capped at the
    /// <see cref="PrunedItemsCap"/> newest entries by <c>prunedAt</c>. Cached per room path, keyed on
    /// the pruned directory's own (mtime, child count) — a room with an unchanged pruned/ directory
    /// skips the walk entirely, mirroring pusher's own cache.
    /// </summary>
    private JsonObject? ComputePrunedInfo(string roomPath)
    {
        var prunedRoot = Path.Combine(roomPath, ArtifactManager.ArtifactsDirectoryName, "pruned");
        if (!Directory.Exists(prunedRoot))
        {
            return null;
        }

        DateTime dirMtimeUtc;
        string[] children;
        try
        {
            dirMtimeUtc = Directory.GetLastWriteTimeUtc(prunedRoot);
            children = Directory.GetFileSystemEntries(prunedRoot);
        }
        catch (IOException)
        {
            return null;
        }

        if (_prunedCache.TryGetValue(roomPath, out var cached)
            && cached.DirMtimeUtc == dirMtimeUtc && cached.ChildCount == children.Length)
        {
            return cached.Result is null ? null : (JsonObject)cached.Result.DeepClone();
        }

        var entries = new List<(string Name, long Bytes, DateTime PrunedAtUtc)>();
        foreach (var child in children)
        {
            try
            {
                long size;
                DateTime prunedAt;
                if (Directory.Exists(child))
                {
                    // Unfiltered, matching pusher.py's pruned_info_for_room: this field answers "how
                    // much did pruning reclaim", and stream logs were reclaimed too, so their bytes
                    // count. #1351's filter is about hiding stream logs from artifact LISTINGS; it does
                    // not apply to this reclaimed-bytes total.
                    size = new DirectoryInfo(child)
                        .EnumerateFiles("*", SearchOption.AllDirectories)
                        .Sum(f => f.Length);
                    prunedAt = Directory.GetLastWriteTimeUtc(child);
                }
                else
                {
                    var fileInfo = new FileInfo(child);
                    size = fileInfo.Length;
                    prunedAt = fileInfo.LastWriteTimeUtc;
                }

                entries.Add((Path.GetFileName(child), size, prunedAt));
            }
            catch (IOException)
            {
                // Best-effort, same as pusher.py's own try/except OSError per child.
            }
        }

        JsonObject? result = null;
        if (entries.Count > 0)
        {
            entries.Sort((a, b) => b.PrunedAtUtc.CompareTo(a.PrunedAtUtc));
            var items = new JsonArray();
            foreach (var entry in entries.Take(PrunedItemsCap))
            {
                items.Add(new JsonObject
                {
                    ["name"] = entry.Name,
                    ["bytes"] = entry.Bytes,
                    ["prunedAt"] = entry.PrunedAtUtc.ToString("O"),
                });
            }

            result = new JsonObject { ["count"] = entries.Count, ["items"] = items };
        }

        _prunedCache[roomPath] = new PrunedCacheEntry(dirMtimeUtc, children.Length, result);
        return result is null ? null : (JsonObject)result.DeepClone();
    }

    /// <summary>Bounded attempt count, not AtomicLaunchConfigWriter's wall-clock budget: that type is
    /// internal to Baton.Vendors with no InternalsVisibleTo grant for Baton.Cli, so this is a local
    /// port of its retry shape (`src/Baton.Vendors/AtomicLaunchConfigWriter.cs`) rather than a call
    /// into it. #1782: a concurrent reader (a fleet-glass poller, a future room-watcher) can hold the
    /// target open with <see cref="FileShare.Read"/> only, which makes <see cref="File.Move(string, string, bool)"/>'s
    /// overwrite throw a transient <see cref="UnauthorizedAccessException"/> or <see cref="IOException"/>
    /// sharing violation on Windows while the reader's handle is open. This tick must never throw out of
    /// the hosted service for that -- the next ~30s tick already self-heals a skipped write, so a
    /// reader that never lets go is logged and skipped rather than crashing the loop.</summary>
    internal static void WriteAtomic(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tempPath, content);

        const int maxAttempts = 5;
        var backoffMs = 20.0;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                File.Move(tempPath, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == maxAttempts)
                {
                    Console.Error.WriteLine(
                        $"FleetProjectionWriter: skipped a write to {path} after {maxAttempts} attempts -- {ex.Message}");
                    TryDeleteTemp(tempPath);
                    return;
                }

                Thread.Sleep(TimeSpan.FromMilliseconds(backoffMs));
                backoffMs = Math.Min(backoffMs * 2, 200);
            }
            catch
            {
                TryDeleteTemp(tempPath);
                throw;
            }
        }
    }

    private static void TryDeleteTemp(string tempPath)
    {
        try
        {
            File.Delete(tempPath);
        }
        catch
        {
            // Best-effort cleanup only, same posture as AtomicLaunchConfigWriter's own.
        }
    }

    private sealed class ExecutionLiveState
    {
        public long StdoutOffset;
        public long RolloverOffset;
        public TokenBudgetMonitor? Monitor;
    }

    private sealed record PrunedCacheEntry(DateTime DirMtimeUtc, int ChildCount, JsonObject? Result);

    /// <summary>One timeline entry, already reduced to the four fields the projection publishes.</summary>
    private sealed record ProjectedTimelineEntry(string Type, string? Timestamp, string? StepId, int? ExitCode);
}
