using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Vendors;
using Baton.Domain;
using Baton.Projection;
using Baton.Runway;
using Baton.Status;
using Baton.Store;
using Baton.Templates;

namespace Baton.Cli.Mcp;

/// <summary>
/// The <c>fleet_status</c> read-only MCP tool (Spike 1, #1392): scans rooms across the fleet,
/// leveraging the terminal sentinel fast-path for terminal rooms and projecting active rooms from
/// bound snapshots and Flow event logs. Returns a structured JSON array of per-room status.
/// </summary>
/// <remarks>
/// spec/baton.md §8: the directory scan (<see cref="BatonPaths.Rooms"/> plus caller-supplied
/// <c>roots</c>) is unioned with <see cref="RoomRegistryStore"/>'s registrations, so a room
/// dispatched into a project directory nobody passed as a <c>roots</c> entry is still found. The
/// union only ever adds rooms — a stale or unreadable registry falls back to exactly what the scan
/// alone would have returned, never fewer.
/// </remarks>
public sealed class FleetStatusTool : IMcpTool
{
    // #1513: NOT a WorkflowOutcome member -- deliberately a fleet_status-only display word, so it
    // can never be confused for a ledger outcome by a consumer that already switches on
    // WorkflowOutcome's own members (enumerated in spec/baton.md §3; deliberately no count here --
    // #1945 made the previous one stale the day it added a member). Distinct
    // from "Failed": a stalled room is not
    // permanently done -- a fresh `baton run` against the room can revive it (`baton resume` cannot;
    // #1582 review found it refuses every room this reaches -- spec/baton.md §3 has the full
    // refusal chain) -- this says "nothing is currently making progress", not "this cannot succeed".
    private const string StalledDisplayState = "Stalled";

    // #1513: confirms EVERY step whose liveness this projection probes reads "dead" -- not merely
    // "none alive". Liveness is only ever populated (WorkflowStatusProjector.Project) for steps
    // keeping the workflow un-terminal (a Running step, or a Failed step still carrying a
    // RetryNotBefore at all, expired or not -- see spec/baton.md §3; a sentinel-frozen Running step
    // carries none, §13, and so cannot count as "dead" here), so this is already scoped to
    // the steps whose promise this room's Running reading rests on. Requiring "all dead" rather than
    // "none alive" matters for a multi-step DAG: a sibling step whose own liveness probe comes back
    // "unknown" (a pre-#1375 ledger with no recorded identity, or a Win32Exception probing a PID this
    // process cannot inspect) must not let an unrelated sibling's confirmed-dead engine downgrade the
    // whole room -- "none alive" alone would. Fail-closed the OTHER way here: uncertain (any
    // "unknown", or no gated steps at all) stays "Running" rather than risk a false "Stalled" an
    // operator would wrongly abandon.
    private static bool IsConfirmedStalled(IReadOnlyList<FleetStepStatusView> steps)
    {
        var gated = steps.Where(s => s.Liveness is not null).ToList();
        return gated.Count > 0 && gated.All(s => s.Liveness == "dead");
    }

    // internal, not private: spec/baton.md §7's daemon-written fleet projection file (#1557)
    // serializes each room with these SAME options, so the wire shape stays one construction site
    // rather than a second copy drifting from this one.
    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    public string Name => "fleet_status";

    public string Description =>
        "Read-only snapshot of room statuses across the fleet, including state, timestamps, usage, and outputs.";

    public string? AnnotationsJson => """{"readOnlyHint": true}""";

    public string InputSchemaJson => """
        {
          "type": "object",
          "properties": {
            "roots": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Optional extra directories containing rooms to scan."
            },
            "include_terminal": {
              "type": "boolean",
              "description": "Whether to include terminal rooms in the output. Defaults to true."
            }
          },
          "additionalProperties": false
        }
        """;

    public McpToolCallResult Call(JsonElement arguments) =>
        CallAsync(arguments, CancellationToken.None).GetAwaiter().GetResult();

    public async Task<McpToolCallResult> CallAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var includeTerminal = true;
        var extraRoots = new List<string>();

        if (arguments.ValueKind == JsonValueKind.Object)
        {
            if (arguments.TryGetProperty("include_terminal", out var includeTerminalElem)
                && (includeTerminalElem.ValueKind == JsonValueKind.True || includeTerminalElem.ValueKind == JsonValueKind.False))
            {
                includeTerminal = includeTerminalElem.GetBoolean();
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

        var discovered = await DiscoverRoomsAsync(extraRoots, cancellationToken).ConfigureAwait(false);
        var results = new List<FleetRoomStatusView>();

        foreach (var room in discovered)
        {
            var roomStatus = await ProcessRoomAsync(room.RoomDir, includeTerminal, cancellationToken).ConfigureAwait(false);
            if (roomStatus is not null)
            {
                results.Add(room.Project is null ? roomStatus : roomStatus with { Project = room.Project });
            }
        }

        // #1391: vendors[] rides the same call as an advisory sibling of rooms[] -- never a second
        // harvest, never a live vendor spawn from this read-only tool. Reads whatever the daemon's
        // VendorUsageHarvester last persisted; absent entirely until a harvest has run at least once.
        var liveLanesByVendor = VendorUsageProjectionReader.CountLiveLanesByVendor(results);
        var vendors = VendorUsageProjectionReader.ReadAll(liveLanesByVendor);

        var json = JsonSerializer.Serialize(new FleetStatusResponse(results, vendors), SerializerOptions);
        return new McpToolCallResult(json);
    }

    /// <summary>
    /// One room directory, plus its project (spec/baton.md §8), the way the scan-plus-registry union
    /// below resolves it — the discovery half of <see cref="ProcessRoomAsync"/>, factored out so #1557's
    /// daemon-side fleet projection writer can walk the SAME set of rooms in the SAME order without a
    /// second, drifting copy of the registry-union logic.
    /// </summary>
    internal readonly record struct DiscoveredRoom(string RoomDir, string? Project);

    /// <summary>
    /// Resolves every room this tool's scan-plus-registry union would find (spec/baton.md §8), without
    /// processing any of them — <see cref="CallAsync"/> and <c>Baton.Cli.Daemon.FleetProjectionWriter</c>
    /// (#1557) both walk this same list and call <see cref="ProcessRoomAsync"/> themselves, so the
    /// discovery rule (which directories count, how a registry entry decorates one with its project) is
    /// stated once.
    /// </summary>
    internal static async Task<IReadOnlyList<DiscoveredRoom>> DiscoverRoomsAsync(
        IReadOnlyList<string> extraRoots, CancellationToken cancellationToken)
    {
        var searchRoots = new List<string>();
        if (Directory.Exists(BatonPaths.Rooms))
        {
            searchRoots.Add(BatonPaths.Rooms);
        }

        // #1619 LOW-3: BatonPaths.ByWorkstream, and everything under it, is nothing but junctions back
        // into rooms BatonPaths.Rooms (or another caller-supplied root) already scans by their real
        // path -- walking it too would double-count every room in it under a second, junction-derived
        // path key, since RecordKey/seenRooms dedupe on the path string, not the resolved target.
        var byWorkstreamKey = BatonPaths.RecordKey(BatonPaths.ByWorkstream);
        foreach (var extraRoot in extraRoots)
        {
            if (!Directory.Exists(extraRoot))
            {
                continue;
            }

            var extraRootKey = BatonPaths.RecordKey(extraRoot);
            if (BatonPaths.RecordKeyComparer.Equals(extraRootKey, byWorkstreamKey)
                || extraRootKey.StartsWith(byWorkstreamKey + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            searchRoots.Add(extraRoot);
        }

        // spec/baton.md §8: the registry's project-root map, keyed the same way seenRooms/roomDir
        // comparisons already are, so a room found by BOTH the directory scan below AND a registry
        // entry (the common case — a room dispatched under the default BatonPaths.Rooms location still
        // gets registered) is decorated with its project, not just rooms the registry alone finds. A
        // registry entry whose directory no longer exists is dropped here rather than surfacing as a
        // phantom room or a spurious project label.
        IReadOnlyList<RoomRegistryEntry> registryEntries;
        try
        {
            registryEntries = await RoomRegistryStore.ReadDistinctByRoomAsync(BatonPaths.RoomRegistryFile, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Defense-in-depth for the registry's only-ever-adds-coverage contract: the store's own
            // catch list should make this unreachable, but if any exception shape slips it, losing
            // the whole call (directory-scan results included) to the host's generic catch-all would
            // be strictly worse than answering scan-only.
            registryEntries = [];
        }
        var projectByRoom = new Dictionary<string, string>(BatonPaths.RecordKeyComparer);
        foreach (var entry in registryEntries)
        {
            if (Directory.Exists(entry.RoomPath))
            {
                projectByRoom[entry.RoomPath] = entry.ProjectRoot;
            }
        }

        var seenRooms = new HashSet<string>(BatonPaths.RecordKeyComparer);
        var discovered = new List<DiscoveredRoom>();

        foreach (var searchRoot in searchRoots)
        {
            string[] subDirs;
            try
            {
                subDirs = Directory.GetDirectories(searchRoot);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            Array.Sort(subDirs, StringComparer.OrdinalIgnoreCase);

            foreach (var roomDir in subDirs)
            {
                var recordKey = BatonPaths.RecordKey(roomDir);
                if (!seenRooms.Add(recordKey))
                {
                    continue;
                }

                projectByRoom.TryGetValue(recordKey, out var project);
                discovered.Add(new DiscoveredRoom(roomDir, project));
            }
        }

        // The registry's whole point (spec/baton.md §8): a room dispatched into a project directory never passed as
        // a scan root above is still invisible to the loop that just ran — pick up whatever the
        // registry names that the scan did not already cover.
        foreach (var (roomPath, projectRoot) in projectByRoom)
        {
            if (!seenRooms.Add(roomPath))
            {
                continue;
            }

            discovered.Add(new DiscoveredRoom(roomPath, projectRoot));
        }

        return discovered;
    }

    internal static async Task<FleetRoomStatusView?> ProcessRoomAsync(
        string roomDir, bool includeTerminal, CancellationToken cancellationToken)
    {
        var roomName = Path.GetFileName(Path.TrimEndingDirectorySeparator(roomDir));

        // 1. Fast-path: check terminal sentinel
        var sentinel = await TerminalSentinelWriter.TryReadAsync(roomDir, cancellationToken).ConfigureAwait(false);
        if (sentinel is not null)
        {
            if (!includeTerminal)
            {
                return null;
            }

            // #1522 review finding 4: `terminal.json` is a frozen WorkflowStatusView snapshot, never
            // re-derived once written (TerminalSentinelWriter). A room that went terminal before
            // #1522 carries its old ConsecutiveFailureCount-derived Attempt/MaxAttempts forever,
            // by design -- this fast-path copies s.Attempt/s.MaxAttempts verbatim rather than
            // re-projecting, so it has no way to upgrade a stale sentinel's semantics after the fact.
            var sentinelSteps = sentinel.Steps.Select(s => new FleetStepStatusView(
                s.Id,
                s.State,
                s.Execution,
                s.LinkedFrom,
                Timestamp: null,
                s.Usage,
                s.LinkedFromUsage,
                Liveness: s.Liveness,
                Attempt: s.Attempt,
                MaxAttempts: s.MaxAttempts,
                FailureKind: s.FailureKind,
                RetryEligible: s.RetryEligible,
                ExhaustedUntil: s.ExhaustedUntil,
                WorkspaceChanged: s.WorkspaceChanged,
                Hollow: s.Hollow,
                HollowReason: s.HollowReason,
                Verify: s.Verify,
                VerifyReason: s.VerifyReason
            )).ToList();

            // #1613 item 3: terminal.json (the sentinel) is a frozen WorkflowStatusView -- it never
            // carried role/adapter/model/effort/timeoutMs, so this fast path used to fall straight
            // through to the all-null defaults below. bindings.json is still sitting right next to
            // it (the same file the label read already loads), so this reads it once and reuses it
            // for both -- one construction site with TryLoadBindingsAsync, same fail-open posture
            // (WorkerBindingConfigParser funnels every data-driven failure into
            // WorkerBindingConfigException, which TryLoadBindingsAsync catches and swallows) that
            // already covered the label alone.
            var terminalBindings = await TryLoadBindingsAsync(roomDir, cancellationToken).ConfigureAwait(false);
            var terminalBinding = ConductorRoomDetector.TryResolveSoleBinding(terminalBindings);
            var terminalFields = ProjectBindingFields(terminalBinding);
            var terminalLineage = await TryReadLineageAsync(roomDir, cancellationToken).ConfigureAwait(false);

            return new FleetRoomStatusView(
                Name: roomName,
                Path: roomDir,
                State: sentinel.State,
                Steps: sentinelSteps,
                Outputs: sentinel.Outputs,
                Error: sentinel.Error,
                Try: sentinel.Try,
                Rejected: sentinel.Rejected,
                ResolvedBy: sentinel.ResolvedBy,
                Role: terminalFields.Role,
                Adapter: terminalFields.Adapter,
                Model: terminalFields.Model,
                Effort: terminalFields.Effort,
                TimeoutMs: terminalFields.TimeoutMs,
                ModelSource: terminalFields.ModelSource,
                EffortSource: terminalFields.EffortSource,
                Label: ExtractRoomLabel(terminalBindings),
                Workstream: ExtractRoomWorkstream(terminalBindings),
                ParentRoomPath: terminalLineage.ParentRoomDirectoryPath,
                ParentExecutionId: terminalLineage.ParentExecutionId,
                ContinuedSessionId: terminalLineage.ContinuedSessionId,
                TerminalAt: sentinel.TerminalAt,
                Delivery: await TryResolveDeliveryAsync(roomDir, sentinel.Outputs, cancellationToken).ConfigureAwait(false),
                Runway: ExtractRoomRunway(terminalBindings));
        }

        // 2. Active room: load snapshot + flow events and project
        var snapshotPath = Path.Combine(roomDir, BatonPaths.SnapshotFileName);
        if (!File.Exists(snapshotPath))
        {
            var bindings = await TryLoadBindingsAsync(roomDir, cancellationToken).ConfigureAwait(false);
            var soleBinding = ConductorRoomDetector.TryResolveSoleBinding(bindings);
            var (role, adapter, model, effort, timeoutMs, modelSource, effortSource) = ProjectBindingFields(soleBinding);
            if (string.Equals(role, "conductor", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(roomName, "conductor", StringComparison.OrdinalIgnoreCase))
            {
                return new FleetRoomStatusView(
                    Name: roomName,
                    Path: roomDir,
                    Role: role ?? "conductor",
                    Adapter: adapter,
                    Model: model,
                    Effort: effort,
                    TimeoutMs: timeoutMs,
                    ModelSource: modelSource,
                    EffortSource: effortSource,
                    Label: ExtractRoomLabel(bindings),
                    Workstream: ExtractRoomWorkstream(bindings),
                    Runway: ExtractRoomRunway(bindings));
            }

            return new FleetRoomStatusView(
                Name: roomName,
                Path: roomDir,
                Error: $"Room directory '{roomDir}' has no bound snapshot.");
        }

        try
        {
            var snapshot = await SnapshotBinder.LoadFromFileAsync(snapshotPath, cancellationToken).ConfigureAwait(false);
            var logPath = Path.Combine(roomDir, BatonPaths.FlowLogFileName);
            var reader = new FlowEventLogReader(logPath);
            var entries = await reader.ReadAllEntriesWithTimestampsAsync(cancellationToken).ConfigureAwait(false);

            var events = new List<FlowEvent>(entries.Count);
            foreach (var entry in entries)
            {
                if (entry is LogEntry.FlowLogEntry flowLogEntry)
                {
                    events.Add(flowLogEntry.Event);
                }
            }

            var checkpoint = ProjectionCheckpointStore.Load(roomDir);
            var state = StateProjector.Project(events, snapshot, checkpoint);

            if (!includeTerminal && state.Status == WorkflowStatus.Terminal)
            {
                return null;
            }

            var outcome = WorkflowOutcome.Describe(state);
            // #1530: same two-log read StatusCommand's own JSON path does -- room.jsonl for the two
            // rejection shapes with no ExecutionId to key a flow.jsonl fact on, flow.jsonl (via
            // `entries`, already read above) for every shape that does.
            IReadOnlyList<ArrestLedgerEntry> arrestLedger;
            try
            {
                var roomLogPath = Path.Combine(roomDir, BatonPaths.RoomLogFileName);
                var roomEvents = await new RoomEventLogReader(roomLogPath).ReadAllRoomEventsAsync(cancellationToken).ConfigureAwait(false);
                arrestLedger = ArrestLedgerProjector.Project(entries, roomEvents);
            }
            catch (FlowEventLogReadException)
            {
                // #1916 fix round 2: a room.jsonl line this build's RoomEventLogReader cannot
                // deserialize (an unknown $type from a version-skew write) used to escape uncaught
                // this far into the room's projection, so the broad catch below collapsed the WHOLE
                // row -- steps, status, delivery, ledger -- into {name, path, error}, even though only
                // the ledger read failed. Degrade just the ledger instead, the same posture
                // StatusCommand's own text/JSON paths take for this identical read.
                arrestLedger = [];
            }

            // Explicit for readability -- a null/omitted registry now falls back to this same
            // StandardWorkerUsageParsers.Default internally (#1590), so this argument is redundant
            // rather than load-bearing, but names the parser set the tool's usage figures depend on.
            var view = WorkflowStatusProjector.Project(
                state, snapshot, roomDir, entries, StandardWorkerUsageParsers.Default, arrestLedger);
            var eventTimestamps = WorkflowStatusProjector.ExtractEventTimestamps(entries);

            var steps = new List<FleetStepStatusView>(view.Steps.Count);
            foreach (var stepView in view.Steps)
            {
                string? timestamp = stepView.Execution is not null && eventTimestamps.TryGetValue(stepView.Execution, out var dt)
                    ? dt.ToString("O")
                    : null;

                steps.Add(new FleetStepStatusView(
                    stepView.Id,
                    stepView.State,
                    stepView.Execution,
                    stepView.LinkedFrom,
                    timestamp,
                    stepView.Usage,
                    stepView.LinkedFromUsage,
                    stepView.Liveness,
                    stepView.Attempt,
                    stepView.MaxAttempts,
                    stepView.FailureKind,
                    stepView.RetryEligible,
                    stepView.ExhaustedUntil,
                    stepView.WorkspaceChanged,
                    stepView.Hollow,
                    stepView.HollowReason,
                    stepView.Verify,
                    stepView.VerifyReason));
            }

            var bindings = await TryLoadBindingsAsync(roomDir, cancellationToken).ConfigureAwait(false);
            var binding = TryResolveRunningBinding(bindings, steps, events);
            var (role, adapter, model, effort, timeoutMs, modelSource, effortSource) = ProjectBindingFields(binding);
            var lineage = await TryReadLineageAsync(roomDir, cancellationToken).ConfigureAwait(false);

            // #1513: the ledger's own `Running` (WorkflowOutcome.Describe/DeriveWorkflowStatus) means
            // "not terminal, and something could still make progress" -- true whether that something
            // is an in-flight process or a Failed step's still-unexpired RetryNotBefore. Neither
            // promise is backed by anything once the ONE process that would act on it is confirmed
            // dead -- spec/baton.md §7 has why there is nothing else to fall back on. This downgrade
            // is display-only, scoped to the fleet-facing view an operator actually reads (the
            // reported symptom -- "the room reads RUNNING forever on the fleet view"): it never
            // touches `outcome`/`state.Status` itself, so RunExitCodeResolver, TerminalSentinelWriter,
            // and every other WorkflowOutcome consumer keep reading exactly what they always did.
            var displayState = outcome == WorkflowOutcome.Running && IsConfirmedStalled(steps) && !string.Equals(role, "conductor", StringComparison.OrdinalIgnoreCase)
                ? StalledDisplayState
                : outcome;

            return new FleetRoomStatusView(
                Name: roomName,
                Path: roomDir,
                State: displayState,
                Steps: steps,
                Outputs: view.Outputs,
                Error: view.Error,
                Try: view.Try,
                Rejected: view.Rejected,
                ResolvedBy: view.ResolvedBy,
                Role: role,
                Adapter: adapter,
                Model: model,
                Effort: effort,
                TimeoutMs: timeoutMs,
                ModelSource: modelSource,
                EffortSource: effortSource,
                Label: ExtractRoomLabel(bindings),
                Workstream: ExtractRoomWorkstream(bindings),
                ParentRoomPath: lineage.ParentRoomDirectoryPath,
                ParentExecutionId: lineage.ParentExecutionId,
                ContinuedSessionId: lineage.ContinuedSessionId,
                // #1157: view.TerminalAt is null on every non-terminal room by construction
                // (WorkflowStatusProjector.Project gates it on WorkflowStatus.Terminal), so this needs
                // no gate of its own here -- including on the #1513 `Stalled` display downgrade above,
                // which never turns a terminal room into a Running one.
                TerminalAt: view.TerminalAt,
                Delivery: await TryResolveDeliveryAsync(roomDir, view.Outputs, cancellationToken).ConfigureAwait(false),
                Arrests: view.Arrests,
                Runway: ExtractRoomRunway(bindings));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Per-room isolation: one unreadable room becomes its own error entry.
            // Cancellation is NOT a room defect — it propagates so the scan stops
            // instead of running to completion accumulating spurious errors.
            return new FleetRoomStatusView(
                Name: roomName,
                Path: roomDir,
                Error: ex.Message);
        }
    }

    /// <summary>
    /// spec/baton.md §6 schema states the field and its absence rule; this is the read side.
    /// Gated on <see cref="DeliveryReferenceResolver"/> resolving a PR number specifically (not merely
    /// a branch) — the same gate <c>DeliveryPoller.PollRoomAsync</c> itself uses, so a branch-only
    /// room (which the poller never touches either) never pays the extra <c>flow.jsonl</c> read below.
    /// </summary>
    private static async Task<DeliveryStatusView?> TryResolveDeliveryAsync(
        string roomDir, IReadOnlyList<string>? outputs, CancellationToken cancellationToken)
    {
        if (DeliveryReferenceResolver.Resolve(outputs)?.PullRequestNumber is null)
        {
            return null;
        }

        var logPath = Path.Combine(roomDir, BatonPaths.FlowLogFileName);
        if (!File.Exists(logPath))
        {
            return null;
        }

        try
        {
            var events = await new FlowEventLogReader(logPath).ReadAllAsync(cancellationToken).ConfigureAwait(false);
            DeliveryStatusView? latest = null;
            foreach (var flowEvent in events)
            {
                latest = flowEvent switch
                {
                    FlowEvent.DeliveryPrOpened opened => new DeliveryStatusView(opened.PullRequestNumber, "Opened"),
                    FlowEvent.DeliveryChecksGreen green => new DeliveryStatusView(green.PullRequestNumber, "ChecksGreen"),
                    FlowEvent.DeliveryChecksRed red => new DeliveryStatusView(red.PullRequestNumber, "ChecksRed"),
                    FlowEvent.DeliveryMerged merged => new DeliveryStatusView(merged.PullRequestNumber, merged.Merged ? "Merged" : "Closed"),
                    _ => latest,
                };
            }

            return latest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FlowEventLogReadException)
        {
            return null;
        }
    }

    /// <summary>
    /// Loads and parses <c>bindings.json</c> if present, degrading to <c>null</c> on any missing file
    /// or load/parse error (fail-open display metadata contract, spec/baton.md §6 schema).
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, WorkerBindingConfigEntry>?> TryLoadBindingsAsync(
        string roomDir, CancellationToken cancellationToken)
    {
        var bindingsPath = BatonPaths.RoomBindingsFile(roomDir);
        if (!File.Exists(bindingsPath))
        {
            return null;
        }

        try
        {
            return await WorkerBindingConfigParser.LoadFromFileAsync(bindingsPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WorkerBindingConfigException or IOException or UnauthorizedAccessException)
        {
            // spec/baton.md §6 schema states the contract this degrades to.
            return null;
        }
    }

    /// <summary>
    /// Thin wrapper around <see cref="InteractiveSessionMaterializer.ReadLineageAsync"/> (issue
    /// #1620, spec/baton.md §6 schema) that additionally degrades to
    /// <see cref="InteractiveSessionMaterializer.RoomLineage.None"/> on an I/O fault at this call
    /// site -- the same fail-open display-metadata contract <see cref="TryLoadBindingsAsync"/>
    /// already applies to <c>bindings.json</c>.
    /// </summary>
    private static async Task<InteractiveSessionMaterializer.RoomLineage> TryReadLineageAsync(
        string roomDir, CancellationToken cancellationToken)
    {
        try
        {
            return await InteractiveSessionMaterializer.ReadLineageAsync(roomDir, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return InteractiveSessionMaterializer.RoomLineage.None;
        }
    }

    /// <summary>
    /// Resolves the worker-binding config entry (issue #1503) and recorded request (issue #1584)
    /// for whichever step this room's projection currently calls <c>"Running"</c> — the same worker
    /// a caller would see live if they tailed <c>room_detail</c> right now. Picks the first Running
    /// step when a workflow has more than one in flight at once; a room row carries one binding, not
    /// a list. See spec/baton.md §6 schema for when this comes back absent and why.
    /// </summary>
    private static (string Role, WorkerBindingConfigEntry Entry, ExecutionRequest Request)? TryResolveRunningBinding(
        IReadOnlyDictionary<string, WorkerBindingConfigEntry>? bindings,
        IReadOnlyList<FleetStepStatusView> steps,
        IReadOnlyList<FlowEvent> events)
    {
        var runningExecution = steps.FirstOrDefault(s => s.State == "Running" && s.Execution is not null)?.Execution;
        if (runningExecution is null)
        {
            return null;
        }

        ExecutionRequest? runningRequest = null;
        foreach (var evt in events)
        {
            if (evt is FlowEvent.ExecutionRequestAccepted accepted && accepted.Request.ExecutionId.Value == runningExecution)
            {
                runningRequest = accepted.Request;
                break;
            }
        }

        if (runningRequest is null)
        {
            return null;
        }

        if (bindings is null)
        {
            return null;
        }

        return bindings.TryGetValue(runningRequest.Worker, out var entry)
            ? (runningRequest.Worker, entry, runningRequest)
            : null;
    }

    /// <summary>
    /// One construction site (the #1590/#1597 lesson) for turning a resolved binding into the five
    /// wire fields -- both the active-room path (<see cref="TryResolveRunningBinding"/>) and the
    /// terminal-sentinel fast path (<see cref="ConductorRoomDetector.TryResolveSoleBinding"/>) resolve WHICH role
    /// differently and share this same projection, but the active-room overload additionally prefers
    /// a recorded <see cref="ExecutionRequest"/>'s Adapter/Model over the resolved
    /// <c>(Role, Entry)</c> pair's own values (issue #1584) -- the terminal path has no recorded
    /// request to prefer, so its Adapter/Model always come from the pair itself (spec/baton.md §6
    /// schema).
    /// </summary>
    private static BindingFields ProjectBindingFields(
        (string Role, WorkerBindingConfigEntry Entry)? binding,
        ExecutionRequest? recordedRequest = null) =>
        binding is { } resolved
            ? new BindingFields(
               resolved.Role,
               recordedRequest?.Adapter ?? resolved.Entry.Adapter,
               // #1927: the requested model still wins -- what an operator asked for is what they
               // should see. ModelResolved is the LAST rung, reached only when nobody asked, which is
               // precisely the dispatch that used to render a bare vendor here.
               recordedRequest?.Model ?? resolved.Entry.Model ?? resolved.Entry.ModelResolved,
               resolved.Entry.Effort ?? resolved.Entry.EffortResolved,
               (long?)resolved.Entry.Timeout.TotalMilliseconds,
               // The stamp travels verbatim, absent and all: a hand-authored bindings.json (baton
               // run/resume) carries no source, and a surface must render "no mark" for that rather
               // than asserting the value was requested.
               resolved.Entry.ModelSource,
               resolved.Entry.EffortSource)
            : new BindingFields(null, null, null, null, null, null, null);

    private static BindingFields ProjectBindingFields(
        (string Role, WorkerBindingConfigEntry Entry, ExecutionRequest Request)? binding) =>
        binding is { } resolved
            ? ProjectBindingFields((resolved.Role, resolved.Entry), resolved.Request)
            : new BindingFields(null, null, null, null, null, null, null);

    /// <summary>
    /// What <see cref="ProjectBindingFields"/> yields — a named record rather than a tuple since #1927
    /// took it past five members, where positional destructuring stops being readable at the call site.
    /// </summary>
    private sealed record BindingFields(
        string? Role,
        string? Adapter,
        string? Model,
        string? Effort,
        long? TimeoutMs,
        string? ModelSource,
        string? EffortSource);

    /// <summary>
    /// Extracts a room's <c>--label</c> (#1499) off its loaded <c>bindings.json</c> dictionary.
    /// </summary>
    private static string? ExtractRoomLabel(IReadOnlyDictionary<string, WorkerBindingConfigEntry>? bindings) =>
        bindings?.Values.Select(entry => entry.Label).FirstOrDefault(label => label is not null);

    /// <summary>
    /// Extracts a room's <c>--workstream</c> (#1619) off its loaded <c>bindings.json</c> dictionary —
    /// same shape as <see cref="ExtractRoomLabel"/>, since both are room-level facts stamped onto
    /// every entry at dispatch time, not scoped to one worker's Running step.
    /// </summary>
    private static string? ExtractRoomWorkstream(IReadOnlyDictionary<string, WorkerBindingConfigEntry>? bindings) =>
        bindings?.Values.Select(entry => entry.Workstream).FirstOrDefault(workstream => workstream is not null);

    /// <summary>
    /// Extracts a room's runway admissions (#1896) off its loaded <c>bindings.json</c> — read the same way
    /// <see cref="ExtractRoomLabel"/> reads its own room-level stamp, but kept as a LIST: unlike a label,
    /// this is decided per vendor, so a composed template spanning two of them has two answers and
    /// reporting either one alone is wrong in precisely the case the batch decision exists for (#1932
    /// review). <c>baton status</c> shows the same list off the same field — it is not a surface with a
    /// finer answer to defer to. <see cref="RunwayAdmissionView.AllFrom"/> owns the dedupe and ordering.
    /// </summary>
    private static IReadOnlyList<RunwayAdmissionView>? ExtractRoomRunway(
        IReadOnlyDictionary<string, WorkerBindingConfigEntry>? bindings) =>
        RunwayAdmissionView.AllFrom(bindings?.Values.Select(entry => entry.RunwayAdmission));
}

/// <summary>
/// Status of a single room within a fleet status report.
/// </summary>
public sealed record FleetRoomStatusView(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("project")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Project = null,
    [property: JsonPropertyName("state")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? State = null,
    [property: JsonPropertyName("steps")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<FleetStepStatusView>? Steps = null,
    [property: JsonPropertyName("outputs")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? Outputs = null,
    [property: JsonPropertyName("error")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Error = null,
    [property: JsonPropertyName("try")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Try = null,
    // spec/baton.md §3/§6: the same WorkflowStatusView.Rejected FleetStatusTool already reads off
    // the shared projection (sentinel.Rejected / view.Rejected) -- copied, never re-derived. Omitted
    // (not emitted false) so its mere presence already answers "did a human reject a step here",
    // the same presence-signals-meaning convention Liveness below uses.
    [property: JsonPropertyName("rejected")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    bool Rejected = false,
    // F10/F11 (#1720 review): the room-level WorkflowStatusView.ResolvedBy, copied the same way
    // Rejected above is. Needed BECAUSE F11 scoped `rejected` to `--reject`: without this the glass
    // has no signal at all for a conductor `baton resolve --close`, which settles a room Failed with
    // a recorded ruling rather than a crash. The per-step resolvedByConductor flag stays
    // deliberately omitted -- WHICH step is a one-room `baton status --json` question.
    [property: JsonPropertyName("resolvedBy")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ResolvedBy = null,
    // #1503, extended by #1584: worker role/adapter/model/effort/timeout for this room's Running step,
    // read via TryResolveRunningBinding -- see spec/baton.md §6 schema for resolution rules and gating.
    [property: JsonPropertyName("role")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Role = null,
    [property: JsonPropertyName("adapter")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Adapter = null,
    [property: JsonPropertyName("model")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Model = null,
    [property: JsonPropertyName("effort")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Effort = null,
    [property: JsonPropertyName("timeoutMs")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? TimeoutMs = null,
    // #1499: read via ExtractRoomLabel off each path's own loaded bindings.json, spec/baton.md §6 schema.
    [property: JsonPropertyName("label")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Label = null,
    // #1619: read via ExtractRoomWorkstream off each path's own loaded bindings.json, same fail-open
    // convention as Label immediately above -- spec/baton.md §6 schema.
    [property: JsonPropertyName("workstream")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Workstream = null,
    // #1441/#1620: redispatch lineage -- see spec/baton.md §6 schema for the read side and the
    // absence rules.
    [property: JsonPropertyName("parentRoomPath")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ParentRoomPath = null,
    [property: JsonPropertyName("parentExecutionId")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ParentExecutionId = null,
    // #1381: see RoomLineage.ContinuedSessionId's own doc / spec/baton.md §6 for what this
    // distinguishes and why.
    [property: JsonPropertyName("continuedSessionId")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ContinuedSessionId = null,
    // #1157: the room-level WorkflowStatusView.TerminalAt, copied the same way Rejected/ResolvedBy
    // above are -- never re-derived here. A terminal room reports when its run ENDED; before this
    // field the fleet reported no terminal instant at all and a consumer wanting one had to stat a
    // file. Why this surface omits the field on a legacy room where the retention sweep instead falls
    // back to a mtime is part of spec/baton.md §3's absence rules.
    [property: JsonPropertyName("terminalAt")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? TerminalAt = null,
    // #734: spec/baton.md §6 schema states this field's shape and its absence rule -- see there.
    [property: JsonPropertyName("delivery")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DeliveryStatusView? Delivery = null,
    // #1530: the room-level WorkflowStatusView.Arrests, copied the same way every other
    // room-level field above is (Rejected/ResolvedBy/TerminalAt) -- absent-safe, so the glass can
    // render a room with no cancel.request history exactly as it does today.
    [property: JsonPropertyName("arrests")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<ArrestLedgerEntryView>? Arrests = null,
    // #1896's RunwayAdmission (its own remarks are the register), read off this room's bindings.json
    // exactly the way Label/Workstream above are -- so it costs no extra file read here, and is absent by
    // construction on a room dispatched before it shipped. One entry per vendor the dispatch gated
    // (#1932 review), matching WorkflowStatusView.Runway element for element. The daemon's fleet
    // projection (#1557) serializes through this same record, which is how the glass gets it.
    [property: JsonPropertyName("runway")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<RunwayAdmissionView>? Runway = null,
    // #1927: WorkerBindingConfigEntry.ModelSource/EffortSource, carried verbatim off the same
    // bindings.json read -- that field's own doc states the vocabulary and what an absent value means.
    [property: JsonPropertyName("modelSource")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ModelSource = null,
    [property: JsonPropertyName("effortSource")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? EffortSource = null);

/// <summary>
/// Status of a single workflow step within a fleet room status report.
/// </summary>
public sealed record FleetStepStatusView(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("execution")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Execution = null,
    [property: JsonPropertyName("linkedFrom")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? LinkedFrom = null,
    [property: JsonPropertyName("timestamp")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Timestamp = null,
    [property: JsonPropertyName("usage")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ExecutionUsageView? Usage = null,
    [property: JsonPropertyName("linkedFromUsage")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ExecutionUsageView? LinkedFromUsage = null,
    // spec/baton.md §3/§6: the same WorkflowStatusStepView.Liveness FleetStatusTool already reads
    // off the shared projection (sentinel step's Liveness / stepView.Liveness) -- copied, never a
    // second EngineLivenessProbe call. Present per WorkflowStatusProjector.Project, except for
    // sentinel-frozen steps — see spec/baton.md §3 for the presence rule and its sentinel exception.
    [property: JsonPropertyName("liveness")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Liveness = null,
    // #1509/#1522: copied verbatim from WorkflowStatusStepView.Attempt/.MaxAttempts -- see that record's
    // remarks for the derivation (lifetime execution count from StateProjector).
    [property: JsonPropertyName("attempt")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? Attempt = null,
    [property: JsonPropertyName("maxAttempts")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? MaxAttempts = null,
    // #1510: copied verbatim from WorkflowStatusStepView.FailureKind/.RetryEligible -- the engine's
    // own FailureClassification enum member name and RetryEngine.MayRetry's verdict, never
    // re-derived here.
    [property: JsonPropertyName("failureKind")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? FailureKind = null,
    [property: JsonPropertyName("retryEligible")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? RetryEligible = null,
    // #1551: copied verbatim from WorkflowStatusStepView.ExhaustedUntil -- see that record's own
    // remarks for the gating rule (ExhaustedUntil classification with a recorded RetryNotBefore
    // only). A future instant by construction while parked; a past one once the park's own
    // reset time has elapsed and nothing repopulates it (#1513 Stalled) -- the fleet_status caller
    // renders that honestly, this field never re-derives or clears it.
    [property: JsonPropertyName("exhaustedUntil")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ExhaustedUntil = null,
    // #1622/#1390: copied verbatim from WorkflowStatusStepView.WorkspaceChanged/.Hollow/.HollowReason
    // -- present only for a tree-changing role's Succeeded settle, per that record's own remarks. The
    // glass badge #1390 asks for reads this rather than probing the worktree itself.
    [property: JsonPropertyName("workspaceChanged")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? WorkspaceChanged = null,
    [property: JsonPropertyName("hollow")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? Hollow = null,
    [property: JsonPropertyName("hollowReason")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? HollowReason = null,
    // #1702: copied verbatim from WorkflowStatusStepView.Verify/.VerifyReason -- "not-run" plus the
    // pre-flight reason, so a fleet_status caller (and Fleet Glass) can render "unverified" for a step
    // that ran but was never checked, distinct from an ordinary Succeeded step.
    [property: JsonPropertyName("verify")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Verify = null,
    [property: JsonPropertyName("verifyReason")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? VerifyReason = null);
