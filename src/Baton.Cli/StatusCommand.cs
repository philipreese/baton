using System.Text.Json;
using Baton.Vendors;
using Baton.Artifacts;
using Baton.Domain;
using Baton.Outcomes;
using Baton.Projection;
using Baton.Runway;
using Baton.Status;
using Baton.Store;
using Baton.Templates;

namespace Baton.Cli;

/// <summary>
/// <c>baton status</c> (#730): a read-only projection of a room directory's recorded events —
/// "this session's workaround was hand-rolled monitors polling PIDs and tailing <c>flow.jsonl</c>
/// by path", which this replaces with the product's own register. Every field printed comes from
/// <see cref="StateProjector.Project"/> — the same projection <see cref="RunCommand"/> and
/// <see cref="CancelCommand"/> already call — so there is exactly one place "what does this event
/// log mean" is computed, never a second reader of the format here.
/// <para>
/// Deliberately never takes <see cref="Baton.Concurrency.ConcurrencyGuard"/>'s lock and never
/// constructs a <see cref="FlowEventLogWriter"/>: this is the one command in <c>Baton.Cli</c> that can
/// run concurrently with a live <c>baton run</c> pump on the same room directory, which is the whole
/// point of a status/watch command. It also never resolves a worker binding (no <c>--bindings</c>
/// option exists on <see cref="StatusOptions"/> at all) — nothing here dispatches, so there is
/// nothing to bind.
/// </para>
/// <para>
/// #1356's one exception to "every field comes from <see cref="StateProjector.Project"/>": a room
/// with no <c>flow.jsonl</c> yet has nothing for that projection to read, so a pre-ledger failure is
/// answered from its terminal sentinel (<see cref="TerminalSentinelWriter"/>) instead — see the
/// early branch in <see cref="ExecuteAsync"/>. <c>--json</c> emits <see cref="WorkflowStatusView"/>
/// (<see cref="WorkflowStatusProjector"/>), built from that same projected state in the normal case.
/// </para>
/// </summary>
public static class StatusCommand
{
    /// <summary>
    /// How often <c>--follow</c> re-checks <c>flow.jsonl</c>'s length for growth. A modest,
    /// fixed interval rather than a <see cref="FileSystemWatcher"/> — file-system change
    /// notifications are unreliable across platforms (missed events on some network/CI
    /// filesystems, duplicate events on others), where a length poll on a plain
    /// <see cref="FileInfo"/> always tells the truth.
    /// </summary>
    private const int PollIntervalMs = 500;

    /// <exception cref="SnapshotLoadException">
    /// The room directory has no persisted snapshot — a nonexistent directory and an existing one
    /// that was never started via <c>baton run</c> fail identically here (both are just "no
    /// <c>snapshot.json</c> at this path"), or the persisted snapshot is malformed.
    /// </exception>
    /// <remarks>
    /// Cancellation is two contracts (#999): under <see cref="StatusOptions.Follow"/> it is how a
    /// follow ends, so this method returns cleanly; a cancelled one-shot probe throws
    /// <see cref="OperationCanceledException"/> instead — see the catch below for why.
    /// </remarks>
    public static async Task ExecuteAsync(
        StatusOptions options, TextWriter output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);

        // #1645 item 2: same non-fatal drift WARN DispatchCommand prints, once per invocation (never
        // repeated inside a --follow loop's poll cycle, which lives further down this method).
        if (InstalledVersionDrift
            .Evaluate(options.RepoPath, VersionInfo.GetVersion(System.Reflection.Assembly.GetExecutingAssembly()))
            .WarnLine() is { } statusDriftWarning)
        {
            Console.Error.WriteLine(statusDriftWarning);
        }

        var snapshotPath = Path.Combine(options.RoomDirectoryPath, BatonPaths.SnapshotFileName);
        var logPath = Path.Combine(options.RoomDirectoryPath, BatonPaths.FlowLogFileName);

        // #1356 point 3: a room that fails during provisioning/validation may never get a
        // flow.jsonl (bindings/workflow validation can fail before snapshot.json exists too, e.g. a
        // dispatch materialization error) or may get one only much later. Its terminal sentinel is
        // then the only queryable answer, and it wins over the ledger precisely because there is no
        // ledger to be authoritative instead — once the room has a REAL ledger (RoomLedgerProbe,
        // #1374 F1 -- a zero-byte flow.jsonl left by a room-held refusal does not count), this branch
        // never runs again and the ledger (the system of record) is read below as usual.
        if (!RoomLedgerProbe.HasLedger(options.RoomDirectoryPath))
        {
            var sentinel = await TerminalSentinelWriter.TryReadAsync(options.RoomDirectoryPath, cancellationToken).ConfigureAwait(false);
            if (sentinel is not null)
            {
                PrintSentinel(output, options.Json, sentinel);
                return;
            }
        }

        // Never Directory.CreateDirectory here (unlike RunCommand): a status probe against a room
        // that was never started must report the same typed failure, not conjure the directory
        // into existence as a side effect of looking at it.
        if (!File.Exists(snapshotPath))
        {
            throw new SnapshotLoadException(
                $"Room directory '{options.RoomDirectoryPath}' has no bound snapshot — 'baton status' " +
                "projects a room 'baton run' has already started, and never binds one fresh.");
        }

        try
        {
            var snapshot = await SnapshotBinder.LoadFromFileAsync(snapshotPath, cancellationToken).ConfigureAwait(false);
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

            var checkpoint = ProjectionCheckpointStore.Load(options.RoomDirectoryPath);
            var state = StateProjector.Project(events, snapshot, checkpoint);

            // #1530: the room-side arrest ledger — room.jsonl for the two rejection shapes that
            // never resolve an ExecutionId, flow.jsonl (via `entries`, already read above; no second
            // ledger read) for every shape that does. See ArrestLedgerProjector's own remarks for why
            // both logs are read rather than a third, parallel ledger store. `baton status` never
            // read room.jsonl before this feature, so a version-skew corruption there (a RoomEvent
            // discriminator this build does not know, per RoomEventLogReader's fail-loud replay
            // contract) must degrade the ledger, not turn a probe that used to succeed into a hard
            // failure -- FleetStatusTool's own read of the same file (#1916 fix round 2) now degrades
            // the same way, rather than the broad per-room catch it used to fall through to, which
            // collapsed the whole row instead of just the ledger.
            IReadOnlyList<ArrestLedgerEntry> arrestLedger;
            string? arrestLedgerUnavailableReason = null;
            try
            {
                var roomLogPath = Path.Combine(options.RoomDirectoryPath, BatonPaths.RoomLogFileName);
                var roomEvents = await new RoomEventLogReader(roomLogPath).ReadAllRoomEventsAsync(cancellationToken).ConfigureAwait(false);
                arrestLedger = ArrestLedgerProjector.Project(entries, roomEvents);
            }
            catch (FlowEventLogReadException ex)
            {
                arrestLedger = [];
                arrestLedgerUnavailableReason = ex.Message;
            }

            if (arrestLedgerUnavailableReason is not null)
            {
                // Nothing else in this method's --json mode writes to stderr, but stdout is
                // exclusively the serialized view in that mode (#1356 point 1) -- this is
                // diagnostic-only, the same channel every other best-effort ledger fault in this
                // feature already uses (CancelRequestPoller's own tick-fault line).
                try
                {
                    Console.Error.WriteLine(
                        $"Arrest ledger unavailable for '{options.RoomDirectoryPath}': {arrestLedgerUnavailableReason}");
                }
                catch (IOException)
                {
                    // F6-equivalent: a broken stderr pipe must not itself fault the probe.
                }
            }

            // #1896: the room's own admission record, off the same bindings.json --follow already
            // reads below. One read, both renderings, and fail-open like every other display read
            // here — a room with no bindings.json (or one this build cannot parse) simply has no
            // runway line, exactly as every room dispatched before #1896 does.
            var runway = RunwayAdmissionView.From(
                (await RoomAdapterLookup.TryLoadBindingsAsync(options.RoomDirectoryPath, cancellationToken)
                    .ConfigureAwait(false))
                .Values.Select(entry => entry.RunwayAdmission).FirstOrDefault(admission => admission is not null));

            if (options.Json)
            {
                // #1356 point 1: the SAME state just projected above, not a second read of the
                // ledger — one derivation, two renderings. Nothing else reaches stdout in this mode.
                // #1360: entries is the same list already read above, not a second ledger read.
                var view = WorkflowStatusProjector.Project(
                    state, snapshot, options.RoomDirectoryPath, entries, WorkerAdapterRegistry.Default, arrestLedger,
                    arrestLedgerUnavailableReason);
                output.WriteLine(JsonSerializer.Serialize(view with { Runway = runway }));
                return;
            }

            PrintState(output, state, logPath, events, entries, options.RoomDirectoryPath);
            PrintArrestLedger(output, arrestLedger, arrestLedgerUnavailableReason);
            PrintRunway(output, runway);

            var streamOffsets = new Dictionary<string, long>(StringComparer.Ordinal);
            var lineAssemblers = new Dictionary<string, StreamLineAssembler>(StringComparer.Ordinal);

            if (options.Follow)
            {
                var artifactsDir = Path.Combine(options.RoomDirectoryPath, Baton.Artifacts.ArtifactManager.ArtifactsDirectoryName);
                var initialBindings = await RoomAdapterLookup.TryLoadBindingsAsync(options.RoomDirectoryPath, cancellationToken).ConfigureAwait(false);
                var initialAdapterNames = RoomAdapterLookup.BuildAdapterNameByExecutionId(events, initialBindings);
                TailStreams(
                    output,
                    artifactsDir,
                    streamOffsets,
                    lineAssemblers,
                    executionId => RoomAdapterLookup.ResolveAdapter(executionId, initialAdapterNames, WorkerAdapterRegistry.Default),
                    // Already Terminal means FollowAsync below never runs, so this is the only/last
                    // tail this room will ever get -- flush its pending partial line now (#1574).
                    flushPending: state.Status == WorkflowStatus.Terminal);
            }

            if (!options.Follow || state.Status == WorkflowStatus.Terminal)
            {
                return;
            }

            // The initial tail above already advanced streamOffsets/lineAssemblers past whatever it
            // printed -- carrying the SAME dictionaries into the follow loop (rather than the loop
            // building its own from scratch) is what makes its first poll resume from there instead
            // of offset 0, which re-tailed and printed the initial content a second time (#1721).
            await FollowAsync(
                output, reader, snapshot, events.Count, logPath, options.RoomDirectoryPath,
                streamOffsets, lineAssemblers, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (options.Follow && cancellationToken.IsCancellationRequested)
        {
            // #999: cancelling a follow is the normal way to stop it, whichever await the token
            // interrupts — FollowAsync's own delay-loop catch only covered the poll's Task.Delay,
            // so cancellation landing inside a journal read escaped as TaskCanceledException. A
            // cancelled NON-follow probe still throws: it produced no answer, and returning as if
            // it had would be fail-open.
        }
    }

    /// <summary>
    /// Polls <paramref name="logPath"/>'s length for growth, printing every event newer than
    /// <paramref name="printedEventCount"/> as it appears, until re-projecting reaches
    /// <see cref="WorkflowStatus.Terminal"/> or <paramref name="cancellationToken"/> is cancelled.
    /// Tails stdout/stderr streams of running executions interleaved with event lines.
    /// <para>
    /// <paramref name="streamOffsets"/> and <paramref name="lineAssemblers"/> are the SAME instances
    /// the caller's initial <see cref="TailStreams"/> call already advanced, never fresh ones built
    /// here -- resuming from those offsets is what stops the loop's first poll from re-tailing (and
    /// reprinting) whatever the initial tail already printed (#1721).
    /// </para>
    /// <para>
    /// Every exit path flushes <paramref name="lineAssemblers"/>' pending partial lines exactly once
    /// (#1574 second-reader finding 2): the <c>justWentTerminal</c> branch below already flushes as
    /// part of its normal, non-cancelled return, so the outer <c>finally</c> skips it there via
    /// <c>flushedFinal</c>; every OTHER way out -- Ctrl-C during <see cref="Task.Delay"/>, or an
    /// <see cref="OperationCanceledException"/> from <see cref="FlowEventLogReader.ReadAllAsync"/>/
    /// <see cref="FlowEventLogReader.ReadAllEntriesWithTimestampsAsync"/> escaping this method entirely
    /// -- previously returned (or propagated) with no flush at all, silently dropping whatever partial
    /// line the assembler was already holding. Pre-#1574 raw tailing never buffered, so it never had
    /// anything to lose on cancellation; this restores that guarantee for the buffered renderer.
    /// </para>
    /// </summary>
    private static async Task FollowAsync(
        TextWriter output,
        FlowEventLogReader reader,
        WorkflowDefinitionSnapshot snapshot,
        int printedEventCount,
        string logPath,
        string roomDirectoryPath,
        Dictionary<string, long> streamOffsets,
        Dictionary<string, StreamLineAssembler> lineAssemblers,
        CancellationToken cancellationToken)
    {
        var lastObservedLength = -1L;
        var artifactsDir = Path.Combine(roomDirectoryPath, Baton.Artifacts.ArtifactManager.ArtifactsDirectoryName);
        var bindings = await RoomAdapterLookup.TryLoadBindingsAsync(roomDirectoryPath, cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<string, string> adapterNameByExecutionId = new Dictionary<string, string>(StringComparer.Ordinal);
        IWorkerAdapter? ResolveAdapter(string executionId) =>
            RoomAdapterLookup.ResolveAdapter(executionId, adapterNameByExecutionId, WorkerAdapterRegistry.Default);
        var flushedFinal = false;

        try
        {
            while (true)
            {
                try
                {
                    await Task.Delay(PollIntervalMs, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                var logFile = new FileInfo(logPath);
                var currentLength = logFile.Exists ? logFile.Length : 0;

                if (currentLength != lastObservedLength)
                {
                    lastObservedLength = currentLength;

                    var events = await reader.ReadAllAsync(cancellationToken).ConfigureAwait(false);
                    for (var i = printedEventCount; i < events.Count; i++)
                    {
                        output.WriteLine(events[i]);
                    }

                    printedEventCount = events.Count;
                    adapterNameByExecutionId = RoomAdapterLookup.BuildAdapterNameByExecutionId(events, bindings);

                    var checkpoint = ProjectionCheckpointStore.Load(roomDirectoryPath);
                    var state = StateProjector.Project(events, snapshot, checkpoint);
                    var justWentTerminal = state.Status == WorkflowStatus.Terminal;
                    TailStreams(output, artifactsDir, streamOffsets, lineAssemblers, ResolveAdapter, flushPending: justWentTerminal);
                    flushedFinal = justWentTerminal;

                    if (justWentTerminal)
                    {
                        output.WriteLine($"Workflow status: {state.Status}");

                        // #1360 F5 (review): the one invocation shape where a human is actually watching
                        // for what a run cost never re-rendered the roll-up PrintState prints before a
                        // follow starts -- a fresh read here (once, at follow's own exit, not per poll)
                        // is cheaper than restructuring the loop above to carry timestamped LogEntry
                        // alongside the plain FlowEvent list it already tracks.
                        var finalEntries = await reader.ReadAllEntriesWithTimestampsAsync(cancellationToken).ConfigureAwait(false);
                        var artifactsRootPath = Path.Combine(roomDirectoryPath, ArtifactManager.ArtifactsDirectoryName);
                        var usageByExecutionId = ExecutionUsageProjector.BuildByExecutionId(
                            finalEntries, artifactsRootPath, WorkerAdapterRegistry.Default, roomDirectoryPath);
                        output.WriteLine(FormatUsageSummary(usageByExecutionId));
                        return;
                    }
                }
                else
                {
                    TailStreams(output, artifactsDir, streamOffsets, lineAssemblers, ResolveAdapter);
                }
            }
        }
        finally
        {
            if (!flushedFinal)
            {
                TailStreams(output, artifactsDir, streamOffsets, lineAssemblers, ResolveAdapter, flushPending: true);
            }
        }
    }

    /// <summary>
    /// Tails every running/completed execution's stdout and stderr, rendering each complete line
    /// through <see cref="WorkerStreamLineRenderer"/> (#1574) -- a claude/agy stream-json envelope
    /// renders as prose via <paramref name="resolveAdapter"/>'s adapter, everything else keeps
    /// <see cref="EscapeNonPrintable"/>'s existing safety net. <paramref name="lineAssemblers"/> holds
    /// one <see cref="StreamLineAssembler"/> per log file, keyed the same way as
    /// <paramref name="streamOffsets"/> -- see that type's own doc comment for what holding one buys.
    /// Public as a test seam, matching FormatStepStatus and EscapeNonPrintable: the reader-side
    /// rollover behavior is asserted directly (the workflow review's medium finding).
    /// </summary>
    public static void TailStreams(
        TextWriter output,
        string artifactsDir,
        Dictionary<string, long> streamOffsets,
        Dictionary<string, StreamLineAssembler> lineAssemblers,
        Func<string, IWorkerAdapter?> resolveAdapter,
        bool flushPending = false)
    {
        if (!Directory.Exists(artifactsDir))
        {
            return;
        }

        foreach (var execDir in Directory.GetDirectories(artifactsDir, "execution_*"))
        {
            var executionDirName = Path.GetFileName(Path.TrimEndingDirectorySeparator(execDir));
            var executionId = executionDirName.StartsWith("execution_", StringComparison.Ordinal)
                ? executionDirName["execution_".Length..]
                : executionDirName;
            var adapter = resolveAdapter(executionId);

            TailStreamFile(
                output,
                Path.Combine(execDir, Baton.Dispatch.ExecutionStreamLogger.StdoutLogFileName),
                Path.Combine(execDir, Baton.Dispatch.ExecutionStreamLogger.StdoutRolloverFileName),
                streamOffsets, lineAssemblers, adapter, flushPending);

            TailStreamFile(
                output,
                Path.Combine(execDir, Baton.Dispatch.ExecutionStreamLogger.StderrLogFileName),
                Path.Combine(execDir, Baton.Dispatch.ExecutionStreamLogger.StderrRolloverFileName),
                streamOffsets, lineAssemblers, adapter, flushPending);
        }
    }

    private static void TailStreamFile(
        TextWriter output,
        string logPath,
        string rolloverPath,
        Dictionary<string, long> streamOffsets,
        Dictionary<string, StreamLineAssembler> lineAssemblers,
        IWorkerAdapter? adapter,
        bool flushPending)
    {
        if (!File.Exists(logPath))
        {
            return;
        }

        streamOffsets.TryGetValue(logPath, out var offset);
        if (!lineAssemblers.TryGetValue(logPath, out var assembler))
        {
            assembler = new StreamLineAssembler();
            lineAssemblers[logPath] = assembler;
        }

        // Rollover detection keys on the rollover FILE'S identity (its mtime advances every time
        // the writer rolls), never on a length comparison: a fresh file whose length equals the
        // stored offset made `length < offset` miss the rollover entirely and silently drop the
        // new content -- found by the reader-side test the workflow review demanded. The rollover
        // path doubles as its own dict key; log and rollover paths are distinct strings. The rolled
        // file and the fresh file are one continuous logical stream, so both reads below share the
        // SAME assembler (keyed by logPath, never rolloverPath) rather than starting a new one.
        if (File.Exists(rolloverPath))
        {
            streamOffsets.TryGetValue(rolloverPath, out var seenRolloverTicks);
            var rolloverFi = new FileInfo(rolloverPath);
            var ticks = rolloverFi.LastWriteTimeUtc.Ticks;
            if (ticks != seenRolloverTicks)
            {
                // The rolled file IS the previous current file: emit its unseen tail, then the
                // fresh file reads from the start.
                if (rolloverFi.Length > offset)
                {
                    ReadAndRenderBytes(output, rolloverPath, offset, rolloverFi.Length - offset, assembler, adapter);
                }

                offset = 0;
                streamOffsets[rolloverPath] = ticks;
            }
        }

        var fi = new FileInfo(logPath);
        if (fi.Length > offset)
        {
            var bytesRead = ReadAndRenderBytes(output, logPath, offset, fi.Length - offset, assembler, adapter);
            offset += bytesRead;
        }

        streamOffsets[logPath] = offset;

        // #1574: once the caller knows no further poll will read this file (the workflow just went
        // Terminal), flush whatever partial trailing line the assembler is still holding -- otherwise
        // a worker's final, newline-less write is silently lost with no future poll left to complete
        // it, unlike the pre-#1574 raw tail which always emitted every byte it read.
        if (flushPending && assembler.Flush() is { Length: > 0 } finalPartialLine)
        {
            var rendered = new StringWriter { NewLine = "\n" };
            WorkerStreamLineRenderer.RenderLine(finalPartialLine, adapter, rendered);
            output.Write(rendered.ToString());
        }
    }

    private static long ReadAndRenderBytes(
        TextWriter output, string path, long offset, long count, StreamLineAssembler assembler, IWorkerAdapter? adapter)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(offset, SeekOrigin.Begin);
            var buffer = new byte[count];
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = fs.Read(buffer, totalRead, (int)(count - totalRead));
                if (read <= 0) break;
                totalRead += read;
            }

            if (totalRead > 0)
            {
                var lines = assembler.Append(buffer.AsSpan(0, totalRead));
                if (lines.Count > 0)
                {
                    var rendered = new StringWriter { NewLine = "\n" };
                    foreach (var line in lines)
                    {
                        WorkerStreamLineRenderer.RenderLine(line, adapter, rendered);
                    }

                    output.Write(rendered.ToString());
                }
            }

            return totalRead;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    public static string EscapeNonPrintable(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return string.Empty;
        }

        var sb = new System.Text.StringBuilder(bytes.Length);
        var decoder = System.Text.Encoding.UTF8.GetDecoder();
        var chars = new char[2];

        for (int i = 0; i < bytes.Length;)
        {
            int bytesUsed, charsUsed;
            bool completed;
            decoder.Convert(bytes.Slice(i, 1).ToArray(), 0, 1, chars, 0, 2, false, out bytesUsed, out charsUsed, out completed);

            if (charsUsed > 0)
            {
                for (int c = 0; c < charsUsed; c++)
                {
                    var ch = chars[c];
                    if (ch is '\n' or '\t' || IsPrintable(ch))
                    {
                        sb.Append(ch);
                    }
                    else
                    {
                        var code = (ushort)ch;
                        if (code <= 0xFF)
                        {
                            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"\\x{code:x2}");
                        }
                        else
                        {
                            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"\\x{code:x4}");
                        }
                    }
                }

                i += bytesUsed;
            }
            else
            {
                // charsUsed == 0 with the byte consumed means the decoder BUFFERED a valid-so-far
                // lead/continuation byte of a multi-byte sequence -- not an invalid byte. Emitting
                // an escape here duplicated every non-ASCII character as \xNN + the decoded char
                // (the workflow review's high finding). Advance silently; the decoder produces the
                // character when the sequence completes, and the flush below drains a sequence
                // truncated at end-of-input as U+FFFD (genuinely invalid bytes already surface as
                // U+FFFD through the decoder's replacement fallback).
                i++;
            }
        }

        var flushed = new char[2];
        decoder.Convert([], 0, 0, flushed, 0, 2, flush: true, out _, out var flushedChars, out _);
        for (int c = 0; c < flushedChars; c++)
        {
            sb.Append(flushed[c]);
        }

        return sb.ToString();
    }

    private static bool IsPrintable(char ch)
    {
        if (ch == ' ') return true;
        var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
        return cat is not (System.Globalization.UnicodeCategory.Control
            or System.Globalization.UnicodeCategory.Format
            or System.Globalization.UnicodeCategory.Surrogate
            or System.Globalization.UnicodeCategory.PrivateUse
            or System.Globalization.UnicodeCategory.OtherNotAssigned
            or System.Globalization.UnicodeCategory.LineSeparator
            or System.Globalization.UnicodeCategory.ParagraphSeparator
            or System.Globalization.UnicodeCategory.SpaceSeparator);
    }

    /// <summary>
    /// Renders a room whose only queryable record is its terminal sentinel (no <c>flow.jsonl</c> —
    /// see the pre-ledger branch in <see cref="ExecuteAsync"/>). Mirrors <see cref="PrintState"/>'s
    /// first line in human mode; in <c>--json</c> mode re-serializes the already-parsed
    /// <paramref name="sentinel"/> rather than trusting its on-disk bytes verbatim. Only ever called
    /// with a sentinel <see cref="TerminalSentinelWriter.TryReadAsync"/> already parsed successfully —
    /// a malformed <c>terminal.json</c> comes back <c>null</c> from that call and is handled by the
    /// caller before this method is reached, not passed in here.
    /// </summary>
    private static void PrintSentinel(TextWriter output, bool json, WorkflowStatusView sentinel)
    {
        if (json)
        {
            output.WriteLine(JsonSerializer.Serialize(sentinel));
            return;
        }

        output.WriteLine($"Workflow status: {sentinel.State}");
        if (!string.IsNullOrWhiteSpace(sentinel.Error))
        {
            output.WriteLine($"  {sentinel.Error}");
        }
    }

    private static void PrintState(
        TextWriter output, FlowState state, string logPath, IReadOnlyList<FlowEvent> events, IReadOnlyList<LogEntry> entries,
        string roomDirectoryPath)
    {
        output.WriteLine($"Workflow status: {state.Status}");
        output.WriteLine($"Log last updated: {ResolveLogUpdatedAt(logPath)}");

        var eventTimestamps = WorkflowStatusProjector.ExtractEventTimestamps(entries);

        foreach (var step in state.Steps)
        {
            var executionText = step.LatestExecutionId?.ToString() ?? "none";
            var statusText = FormatStepStatus(step, events);
            var timeText = step.LatestExecutionId is not null && eventTimestamps.TryGetValue(step.LatestExecutionId.Value.Value, out var time)
                ? $" @ {time:O}"
                : string.Empty;
            output.WriteLine($"  {step.StepId}: {statusText} (execution={executionText}{timeText})");
        }

        foreach (var stepLess in state.StepLessExecutions)
        {
            output.WriteLine($"  (supplementary) {stepLess.Worker}: execution={stepLess.ExecutionId} pending");
        }

        // #1360: one rolled-up line for the whole room, never per step here -- a machine consumer
        // wanting per-execution figures already has them from `--json`'s usage/linkedFromUsage.
        var artifactsRootPath = Path.Combine(roomDirectoryPath, ArtifactManager.ArtifactsDirectoryName);
        var usageByExecutionId = ExecutionUsageProjector.BuildByExecutionId(
            entries, artifactsRootPath, WorkerAdapterRegistry.Default, roomDirectoryPath);
        output.WriteLine(FormatUsageSummary(usageByExecutionId));
    }

    /// <summary>
    /// #1530: the room's arrest history, one of three states. Silent (no header, no blank line) for
    /// a room that never saw a <c>cancel.request</c> — the same "absent means nothing to say" posture
    /// <c>Arrests</c>' own <c>--json</c> field takes. The entry list, rendered one line per outcome,
    /// for a room whose ledger read cleanly. <c>Arrests: ledger unavailable (&lt;reason&gt;)</c>
    /// (#1916 fix round) when <paramref name="unavailableReason"/> is non-null — room.jsonl existed
    /// but a version-skew build could not read it; <c>--json</c> carries the same reason on
    /// <see cref="Baton.Status.WorkflowStatusView.ArrestLedgerUnavailableReason"/> rather than
    /// collapsing to the same absent <c>Arrests</c> a clean empty ledger produces.
    /// </summary>
    /// <summary>
    /// The text rendering of #1896's <see cref="RunwayAdmission"/> (its own remarks are the register).
    /// Silent for a room carrying none — the same "absent means nothing to say" posture
    /// <see cref="PrintArrestLedger"/> takes. The fleet-wide record, refusals included, is
    /// <see cref="BatonPaths.RunwayAdmissionLedgerFile"/>.
    /// </summary>
    private static void PrintRunway(TextWriter output, RunwayAdmissionView? runway)
    {
        if (runway is null)
        {
            return;
        }

        var because = runway.Reason is { Length: > 0 } reason ? $" — {reason}" : string.Empty;
        output.WriteLine($"Runway ({runway.Vendor}): {runway.Decision} by {runway.DecidedBy}{because}");

        if (runway.Counters is { Count: > 0 } counters)
        {
            var rendered = counters.Select(c => c.PercentUsed is { } pct ? $"{c.Window} {pct}%" : $"{c.Window} unknown");
            output.WriteLine($"  Counters: {string.Join(", ", rendered)}");
        }

        if (runway.WeekHoldPct is { } week && runway.SessionHoldPct is { } session)
        {
            output.WriteLine($"  Thresholds: week {week}%, session {session}%");
        }

        // One line, and only when the reservation arm actually ran: an admission taken with no
        // readable counters has no headroom to report and reserved nothing against it.
        if (runway.HeadroomPoints is { } headroom)
        {
            var estimate = runway.EstimatedBurnPoints is { } points
                ? $", estimated {Points(points)} for this room ({runway.EstimateSource})"
                : string.Empty;
            output.WriteLine(
                $"  Headroom: {Points(headroom)} points, "
                + $"{Points(runway.OutstandingReservationPoints ?? 0)} outstanding{estimate}");
        }
    }

    /// <summary>Invariant, two decimals at most — the same rendering the ledger's own refusal text uses.</summary>
    private static string Points(double value) =>
        value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    private static void PrintArrestLedger(TextWriter output, IReadOnlyList<ArrestLedgerEntry> arrestLedger, string? unavailableReason = null)
    {
        if (unavailableReason is not null)
        {
            output.WriteLine($"Arrests: ledger unavailable ({unavailableReason})");
            return;
        }

        if (arrestLedger.Count == 0)
        {
            return;
        }

        output.WriteLine("Arrests:");
        foreach (var entry in arrestLedger)
        {
            var outcomeText = entry.Outcome switch
            {
                ArrestOutcome.Delivered => "delivered",
                ArrestOutcome.Rejected => $"rejected ({entry.Reason})",
                ArrestOutcome.Expired => "expired",
                _ => "requested (pending)",
            };
            output.WriteLine($"  {entry.Target} requested by {entry.RequestedBy} @ {entry.RequestedAtUtc:O} — {outcomeText}");
        }
    }

    /// <summary>
    /// The room-wide roll-up (#1360's "one rolled-up line in human baton status"). Sums the per-execution
    /// <see cref="ExecutionUsageView.WallClockMs"/> figures across every execution with both a start
    /// and exit event, since that half is always derivable; a token/turn figure is summed and its
    /// reporting count disclosed only when at least one execution actually carried it — an adapter (or
    /// a text-mode dispatch) that reports none is silence, not a printed zero.
    /// <para>
    /// Labelled "execution time", not "wall-clock" (#1360 F4, review): parallel steps' executions
    /// overlap in real time, so this sum can exceed the room's own actual elapsed time — it is
    /// aggregate execution time, the same quantity <see cref="ExecutionUsageView.WallClockMs"/> names
    /// per execution, not a claim about how long the room itself took end to end.
    /// </para>
    /// </summary>
    /// <remarks>
    /// #1581: extends the roll-up past the three fields #1569 added to the JSON contract
    /// (<c>CacheReadTokens</c>/<c>CacheCreationTokens</c>/<c>ThinkingTokens</c>) without also
    /// touching this human-readable line. <c>BilledTokens</c> — the authoritative billed figure per
    /// spec/baton.md §3 — leads the line when at least one execution reports it, ahead of the raw
    /// execution count/time; the rest follow in the order: billed, tokens in, tokens out, cache read,
    /// cache creation, thinking, turns. Each part is independently omitted (via
    /// <see cref="AppendTokenPart"/>) when no execution reports that figure, so a plain-text-stdout
    /// room's line is unchanged from before this change.
    /// </remarks>
    internal static string FormatUsageSummary(IReadOnlyDictionary<string, ExecutionUsageView> usageByExecutionId)
    {
        if (usageByExecutionId.Count == 0)
        {
            return "Usage: no completed executions yet.";
        }

        var totalExecutionSeconds = usageByExecutionId.Values.Sum(u => u.WallClockMs) / 1000.0;
        var parts = new List<string>
        {
            $"{usageByExecutionId.Count} execution(s)",
            $"{totalExecutionSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}s execution time",
        };

        AppendTokenPart(parts, usageByExecutionId, u => u.BilledTokens, "billed tokens");
        AppendTokenPart(parts, usageByExecutionId, u => u.TokensIn, "tokens in");
        AppendTokenPart(parts, usageByExecutionId, u => u.TokensOut, "tokens out");
        AppendTokenPart(parts, usageByExecutionId, u => u.CacheReadTokens, "cache read tokens");
        AppendTokenPart(parts, usageByExecutionId, u => u.CacheCreationTokens, "cache creation tokens");
        AppendTokenPart(parts, usageByExecutionId, u => u.ThinkingTokens, "thinking tokens");

        var turnsReporting = usageByExecutionId.Values.Where(u => u.Turns is not null).ToList();
        if (turnsReporting.Count > 0)
        {
            parts.Add($"{turnsReporting.Sum(u => u.Turns!.Value)} turns ({turnsReporting.Count}/{usageByExecutionId.Count} reporting)");
        }

        return "Usage: " + string.Join(", ", parts);
    }

    private static void AppendTokenPart(
        List<string> parts,
        IReadOnlyDictionary<string, ExecutionUsageView> usageByExecutionId,
        Func<ExecutionUsageView, long?> selector,
        string label)
    {
        var reporting = usageByExecutionId.Values.Where(u => selector(u) is not null).ToList();
        if (reporting.Count == 0)
        {
            return;
        }

        var total = reporting.Sum(u => selector(u)!.Value);
        parts.Add($"{total} {label} ({reporting.Count}/{usageByExecutionId.Count} reporting)");
    }

    public static string FormatStepStatus(StepState step, IReadOnlyList<FlowEvent> events)
    {
        // A Failed step carrying a RetryNotBefore has a StepRetryScheduled recorded for it (#594)
        // -- the machine's own paced wait, whether an ordinary backoff or a quota park (#817).
        // StateProjector clears RetryNotBefore the moment a fresh ExecutionRequestAccepted lands
        // for the step, so latest-state-wins here for free: a step that has since retried or
        // succeeded never reaches this branch.
        // Post-#1115 / 0026 §5 (#1116): an un-obligated ExhaustedUntil step (null RetryNotBefore;
        // see MutationInterface.GetRetryObligations) renders
        // "parked (vendor quota) — reset unknown".
        // #1586 S1: RetryForeclosed excluded from that branch -- a FlowEvent.StepRetryForeclosed
        // also clears RetryNotBefore while leaving LatestFailureClassification at ExhaustedUntil
        // (StateProjector), so without this guard a foreclosed step (settled Terminal, nothing will
        // ever dispatch it again) would render as still waiting on an unknown vendor reset -- the
        // exact opposite of what foreclosure means, and the same misreport class #1513/#1582 were
        // paid for. Falls through to plain "Failed" below; a foreclosure-specific rendering is S2's
        // to add once a verb produces one in practice.
        if (step.Status == StepStatus.Failed)
        {
            if (step.LatestFailureClassification == FailureClassification.ExhaustedUntil
                && step.RetryNotBefore is null
                && !step.RetryForeclosed)
            {
                // #802: reaching this branch at all means no declared FallbackOnExhaustion rescued
                // the park (one would have redispatched immediately instead of sitting here with a
                // RetryNotBefore never scheduled) -- never silent: name the decision the operator owes.
                return "parked (vendor quota) — reset unknown; "
                    + $"no fallback declared — {RecoveryGuidance.RedispatchAdapterInstruction}, or wait for the operator to resume it";
            }

            if (step.RetryNotBefore is not null)
            {
                return FormatParkedStatus(step, events);
            }
        }

        // #1622 (b)/#1390: the room word stays "Succeeded" (reclassifying a hollow success is the
        // operator's own design call, not this fix's -- spec/baton.md §3) but the human rendering
        // must not read identically to a real one when the engine has the evidence it wasn't.
        if (step.Status == StepStatus.Succeeded && step.Hollow == true)
        {
            return $"Succeeded — hollow: {step.HollowReason}";
        }

        // Probe ONLY steps claiming a live engine. Paused is a mask over an already-terminal
        // outcome (StateProjector) -- its engine has legitimately exited, and probing it stamped
        // every healthy paused step "crash recovery will classify" (the workflow review's high
        // finding). Pending has no execution yet, so no liveness claim applies there either.
        if (step.Status is not StepStatus.Running)
        {
            // #1702: the one human-prose surface for StepState.VerifyNotRunReason (see
            // WorkflowStatusStepView.Verify's remarks for the machine-readable shape; spec/baton.md §3
            // for the full contract). Checked only below the Running guard, not above it, so a step
            // that crashed mid-verify still reaches the liveness probe's own report instead of a
            // permanently-stuck "Running (unverified)".
            return step.VerifyNotRunReason is not null
                ? $"{step.Status} (unverified — {step.VerifyNotRunReason})"
                : step.Status.ToString();
        }

        if (step.LatestExecutionId is null)
        {
            return step.Status.ToString();
        }

        var accepted = events.OfType<FlowEvent.ExecutionRequestAccepted>()
            .FirstOrDefault(e => e.Request.ExecutionId == step.LatestExecutionId);

        var probeResult = EngineLivenessProbe.Probe(accepted?.EnginePid, accepted?.EngineStartTime);

        return probeResult.Status switch
        {
            EngineLivenessStatus.Alive => step.Status.ToString(),
            EngineLivenessStatus.Dead => $"{step.Status} — engine not alive; crash recovery will classify on next pump",
            EngineLivenessStatus.Unknown => $"liveness unknown ({probeResult.Why})",
            _ => $"liveness unknown ({probeResult.Why})",
        };
    }

    /// <summary>
    /// An operator reading status wants "when does work resume", not a UTC instant to convert by
    /// hand (#817) -- <see cref="StepState.RetryNotBefore"/> is rendered in local time, date
    /// always included: the dominant real park is a plan-cap wait that can cross midnight or span
    /// days (0026), where a bare clock time is ambiguous. A constant format also keeps rendering
    /// independent of when status is run, which a same-day/other-day fork would not. The
    /// classification is <see cref="StepState.LatestFailureClassification"/> as recorded on the
    /// attempt <see cref="FlowEvent.StepRetryScheduled"/> is pacing, mapped to the operator-facing
    /// word: <see cref="FailureClassification.ExhaustedUntil"/> is the vendor-quota wait 0026
    /// introduced; everything else eligible to reach here (<see cref="FailureClassification.Retryable"/>
    /// or absent, per <see cref="Baton.Scheduling.RetryEngine.MayRetry"/>) is an ordinary
    /// backoff.
    /// </summary>
    private static string FormatParkedStatus(StepState step, IReadOnlyList<FlowEvent> events)
    {
        var classification = step.LatestFailureClassification == FailureClassification.ExhaustedUntil
            ? "vendor quota"
            : "retryable";
        var localRetryTime = step.RetryNotBefore!.Value.ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);

        // #1513: "retries HH:MM" reads as a promise the ledger cannot back on its own -- see
        // spec/baton.md §7 for why. Same probe, same identity source as the Running branch below --
        // confirm dead before saying so, so a merely slow (or Unknown-liveness) pump is never
        // misreported as abandoned.
        EngineLivenessResult? probeResult = null;
        if (step.LatestExecutionId is { } latestExecutionId)
        {
            // #1577: mirrors WorkflowStatusView's engineIdentityByExecutionId loop -- newest stamp
            // across both event kinds wins, so this human rendering never disagrees with fleet_status.
            int? enginePid = null;
            DateTimeOffset? engineStartTime = null;
            foreach (var evt in events)
            {
                if (evt is FlowEvent.ExecutionRequestAccepted accepted && accepted.Request.ExecutionId == latestExecutionId)
                {
                    (enginePid, engineStartTime) = (accepted.EnginePid, accepted.EngineStartTime);
                }
                else if (evt is FlowEvent.StepRetryScheduled { EnginePid: not null } retryScheduled && retryScheduled.ForExecutionId == latestExecutionId)
                {
                    (enginePid, engineStartTime) = (retryScheduled.EnginePid, retryScheduled.EngineStartTime);
                }
            }

            probeResult = EngineLivenessProbe.Probe(enginePid, engineStartTime);
            if (probeResult.Status == EngineLivenessStatus.Dead)
            {
                // #1582 review (HIGH-1): `baton resume`/`baton redispatch` both refuse a room in this
                // shape, for two different reasons -- spec/baton.md §3 has the refusal chain and why
                // a fresh `baton run --room-dir` is the recovery below instead.
                return $"parked ({classification}) — retries {localRetryTime}, but the engine that scheduled " +
                    "this retry is no longer alive and nothing else will act on it; this needs manual " +
                    $"intervention — {RecoveryGuidance.RunRoomDirInstruction}, and leave it running until " +
                    $"{localRetryTime} or nothing fires (see spec/baton.md §3)";
            }
        }

        // #802: reaching here for a "vendor quota" classification means no declared
        // FallbackOnExhaustion applied (one would have redispatched immediately rather than pacing
        // to localRetryTime) — never silent: name the decision the operator owes instead of only the
        // clock. An ordinary "retryable" backoff names nothing extra; it is the machine's own pacing,
        // not a vendor decision.
        if (classification != "vendor quota")
        {
            return $"parked ({classification}) — retries {localRetryTime}";
        }

        // #1838: the still-Dead engine already returned above, so reaching here with a live engine
        // (or one whose identity was never recorded / came back Unknown) means `baton redispatch`
        // would refuse for want of a terminal sentinel -- `baton cancel` first is the verb that
        // actually settles the room. Only a confirmed-Alive read gets the two-step wording; Unknown
        // stays on the plain instruction rather than guessing a cancel is needed (or safe) when the
        // liveness read itself could not tell.
        var instruction = probeResult?.Status == EngineLivenessStatus.Alive
            ? RecoveryGuidance.CancelThenRedispatchAdapterInstruction
            : RecoveryGuidance.RedispatchAdapterInstruction;

        return $"parked ({classification}) — retries {localRetryTime}; "
            + $"no fallback declared — {instruction}, or wait";
    }

    /// <summary>
    /// <c>flow.jsonl</c>'s own last-write time (UTC), append-only so this is exactly "when the
    /// last event landed" — the closest honest answer available. Per-step timestamps are sourced
    /// from <see cref="LogEntry.WriterUtcTimestamp"/> instead, which stamps each envelope at write
    /// time (#745). Printed once here at the whole-log grain, per-step times are rendered in
    /// <c>PrintState</c> via <c>ExtractEventTimestamps</c>.
    /// </summary>
    private static string ResolveLogUpdatedAt(string logPath) => File.Exists(logPath)
        ? File.GetLastWriteTimeUtc(logPath).ToString("O")
        : "never (no ledger yet)";
}

