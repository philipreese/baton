using System.Text.Json;
using Baton.Cli.Daemon;
using Baton.Artifacts;
using Baton.Concurrency;
using Baton.Domain;
using Baton.Status;
using Baton.Store;
using Baton.Templates;
using Xunit;

namespace Baton.Cli.Tests.Daemon;

/// <remarks>
/// #1524: same <see cref="BatonEnvironmentSnapshot.BeginScope"/> isolation as
/// <c>Baton.Vendors.Tests.WorkerRoleCatalogTests</c>.
/// </remarks>
public class RoomRetentionSweepTests
{
    private static readonly StepId StepA = new("stepA");

    private static WorkflowDefinitionSnapshot SingleStepSnapshot() => new(
        new WorkflowDefinitionSnapshotId("snapshot-1"),
        new WorkflowTemplateId("single-step"),
        WorkflowTemplateVersion: 1,
        Steps:
        [
            new WorkflowStepDefinition(StepA, "worker", [], ["output.txt"], DependsOn: [], RetryPolicy: new RetryPolicy(1)),
        ]);

    private static ExecutionRequest TestRequest(ExecutionId execId) => new(
        execId,
        new WorkflowId("wf-1"),
        StepA,
        "worker",
        Inputs: [],
        Outputs: ["output.txt"],
        Timeout: TimeSpan.FromMinutes(1),
        Environment: [],
        UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>()
    );

    private static async Task WriteLogEventsAsync(string logPath, params FlowEvent[] events)
    {
        await using var writer = new FlowEventLogWriter(logPath);
        foreach (var @event in events)
        {
            await writer.AppendAsync(@event, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// #1157: appends journal lines carrying a CHOSEN writer stamp, which
    /// <see cref="FlowEventLogWriter"/> cannot do — it stamps <c>DateTime.UtcNow</c>, so every room a
    /// test builds through it ends "just now" and no test could distinguish a terminal instant from
    /// the moment the fixture was written. Same wire contract
    /// (<see cref="FlowEventLogJson.Options"/>) and the same one-complete-line-per-append shape, so
    /// what is read back is a real journal, not a shape only this test understands. Pass
    /// <paramref name="writerUtcTimestamp"/> as <c>null</c> to produce a pre-#745 legacy line.
    /// </summary>
    private static async Task AppendStampedLogEventsAsync(
        string logPath, DateTime? writerUtcTimestamp, params FlowEvent[] events)
    {
        var text = string.Concat(events.Select(@event =>
            JsonSerializer.Serialize(
                (LogEntry)new LogEntry.FlowLogEntry(@event, writerUtcTimestamp),
                typeof(LogEntry),
                FlowEventLogJson.Options) + "\n"));

        await File.AppendAllTextAsync(logPath, text, TestContext.Current.CancellationToken);
    }

    /// <param name="terminalAtUtc">
    /// #1157: when the run ENDED, stamped onto the journal lines themselves. <c>null</c> keeps the
    /// original behaviour (<see cref="FlowEventLogWriter"/>'s own "now"); <see cref="LegacyJournal"/>
    /// writes the same two events with no writer stamps at all, the pre-#745 shape the retention
    /// fallback exists for.
    /// </param>
    private static async Task<string> CreateTerminalRoomWithArtifactsAsync(
        string parentDir, string roomName, ExecutionId execId, DateTime? terminalAtUtc = null)
    {
        var roomDir = Path.Combine(parentDir, roomName);
        Directory.CreateDirectory(roomDir);

        var snapshotPath = Path.Combine(roomDir, "snapshot.json");
        var logPath = Path.Combine(roomDir, "flow.jsonl");

        await SnapshotBinder.PersistAsync(SingleStepSnapshot(), snapshotPath, TestContext.Current.CancellationToken);

        FlowEvent[] events =
        [
            new FlowEvent.ExecutionRequestAccepted(TestRequest(execId)),
            new FlowEvent.ExecutionSucceeded(execId),
        ];

        if (terminalAtUtc == LegacyJournal)
        {
            await AppendStampedLogEventsAsync(logPath, writerUtcTimestamp: null, events);
        }
        else if (terminalAtUtc is { } instant)
        {
            await AppendStampedLogEventsAsync(logPath, instant, events);
        }
        else
        {
            await WriteLogEventsAsync(logPath, events);
        }

        var artifactsRoot = Path.Combine(roomDir, ArtifactManager.ArtifactsDirectoryName);
        var execDir = ArtifactManager.AllocateOutputDirectory(artifactsRoot, execId);
        await File.WriteAllTextAsync(Path.Combine(execDir, "output.txt"), "artifact-data", TestContext.Current.CancellationToken);

        return roomDir;
    }

    /// <summary>
    /// Sentinel for <see cref="CreateTerminalRoomWithArtifactsAsync"/>'s <c>terminalAtUtc</c> meaning
    /// "write a pre-#745 journal with no writer stamps". <see cref="DateTime.MinValue"/> is not a
    /// plausible real stamp, and a separate bool parameter would have made the two mutually exclusive
    /// options independently settable.
    /// </summary>
    private static readonly DateTime LegacyJournal = DateTime.MinValue;

    private static async Task<string> CreateRoomWithEventsAsync(string parentDir, string roomName, params RoomEvent[] events)
    {
        var roomDir = Path.Combine(parentDir, roomName);
        Directory.CreateDirectory(roomDir);

        var roomLogPath = Path.Combine(roomDir, "room.jsonl");
        await using (var writer = new RoomEventLogWriter(roomLogPath))
        {
            foreach (var evt in events)
            {
                await writer.AppendAsync(evt, TestContext.Current.CancellationToken);
            }
        }

        return roomDir;
    }

    [Fact]
    public async Task PerRoomResilience_RoomFailure_DoesNotStopSweepFromCompactingNextRoom()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "baton_sweep_test_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            // Room 1: Corrupt room log that will throw on read/compaction
            var room1Dir = Path.Combine(tempRoot, "room-1-corrupt");
            Directory.CreateDirectory(room1Dir);
            await File.WriteAllTextAsync(Path.Combine(room1Dir, "room.jsonl"), "INVALID_JSON_CORRUPT_CONTENT\n", TestContext.Current.CancellationToken);

            // Room 2: Valid room with a resolved run that needs compaction
            var refCompleted = new HeldWorkRef("run-completed");
            var refLive = new HeldWorkRef("run-live");
            var dispatchCompleted = new RoomEvent.HeldWorkDispatched(refCompleted, "shape", TimeSpan.FromMinutes(5), "human");
            var resolveCompleted = new RoomEvent.HeldWorkResolved(refCompleted, new HeldWorkCitation("Resolved", "ok"));
            var dispatchLive = new RoomEvent.HeldWorkDispatched(refLive, "shape", TimeSpan.FromMinutes(5), "human");

            var room2Dir = await CreateRoomWithEventsAsync(tempRoot, "room-2-valid", dispatchCompleted, resolveCompleted, dispatchLive);

            var sweep = new RoomRetentionSweep();

            // Run sweep with 0 byte threshold so size doesn't skip room 2
            var (compactedCount, _) = await sweep.ExecuteSingleSweepAsync(
                roomsDirectoryOverride: tempRoot,
                thresholdBytesOverride: 0,
                cancellationToken: TestContext.Current.CancellationToken);

            // Room 1 threw, Room 2 was compacted successfully
            Assert.Equal(1, compactedCount);

            var reader2 = new RoomEventLogReader(Path.Combine(room2Dir, "room.jsonl"));
            var room2Events = await reader2.ReadAllRoomEventsAsync(TestContext.Current.CancellationToken);
            Assert.Single(room2Events);
            Assert.Equal(refLive, ((RoomEvent.HeldWorkDispatched)room2Events[0]).Ref);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    [Fact]
    public async Task ExecuteSingleSweepAsync_SkipsRoomsBelowThreshold()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "baton_sweep_test_thresh_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var refCompleted = new HeldWorkRef("run-completed");
            var dispatchCompleted = new RoomEvent.HeldWorkDispatched(refCompleted, "shape", TimeSpan.FromMinutes(5), "human");
            var resolveCompleted = new RoomEvent.HeldWorkResolved(refCompleted, new HeldWorkCitation("Resolved", "ok"));

            var roomDir = await CreateRoomWithEventsAsync(tempRoot, "room-small", dispatchCompleted, resolveCompleted);

            var fileInfo = new FileInfo(Path.Combine(roomDir, "room.jsonl"));
            var fileSize = fileInfo.Length;

            var sweep = new RoomRetentionSweep();

            // Threshold set higher than file size -> should skip
            var (countSkipped, _) = await sweep.ExecuteSingleSweepAsync(
                roomsDirectoryOverride: tempRoot,
                thresholdBytesOverride: fileSize + 1000,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(0, countSkipped);

            // Threshold set lower than file size -> should compact
            var (countCompacted, _) = await sweep.ExecuteSingleSweepAsync(
                roomsDirectoryOverride: tempRoot,
                thresholdBytesOverride: 0,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(1, countCompacted);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    [Fact]
    public void EnvironmentVariables_DefaultsAndOverrides()
    {
        Assert.False(RoomRetentionSweep.IsEnabled());
        Assert.Equal(RoomRetentionSweep.PlaceholderDefaultInterval, RoomRetentionSweep.GetInterval());
        Assert.Equal(RoomRetentionSweep.PlaceholderDefaultThresholdBytes, RoomRetentionSweep.GetThresholdBytes());
    }

    [Fact]
    public void GetInterval_ClampsPathologicalValue_InsteadOfOverflowing()
    {
        // Pins the clamp (RoomRetentionSweep.MaxInterval documents why it exists): a value whose
        // seconds would overflow TimeSpan.FromSeconds must collapse to MaxInterval, never throw.
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { RetentionSweepIntervalSecondsOverride = "1e300" });

        var interval = RoomRetentionSweep.GetInterval();
        Assert.Equal(RoomRetentionSweep.MaxInterval, interval);
    }

    [Fact]
    public void GetInterval_LiftsSubSecondValue_ToMinInterval()
    {
        // Pins the lower clamp (RoomRetentionSweep.MinInterval documents the rationale): a value below
        // one second must lift to MinInterval rather than pass through near-zero.
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { RetentionSweepIntervalSecondsOverride = "1e-9" });

        Assert.Equal(RoomRetentionSweep.MinInterval, RoomRetentionSweep.GetInterval());
    }

    [Fact]
    public async Task ExecuteSingleSweepAsync_PropagatesCancellation_InsteadOfSwallowingItAsAPerRoomError()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "baton_sweep_test_cancel_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var refCompleted = new HeldWorkRef("run-completed");
            var dispatchCompleted = new RoomEvent.HeldWorkDispatched(refCompleted, "shape", TimeSpan.FromMinutes(5), "human");
            var resolveCompleted = new RoomEvent.HeldWorkResolved(refCompleted, new HeldWorkCitation("Resolved", "ok"));
            await CreateRoomWithEventsAsync(tempRoot, "room-cancel", dispatchCompleted, resolveCompleted);

            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            var sweep = new RoomRetentionSweep();

            // A pre-cancelled token makes CompactAsync throw OperationCanceledException. The per-room catch
            // must rethrow it so shutdown unwinds the whole sweep — not log it as a compaction error and
            // march to the next room, which would swallow the cancellation. Without the rethrow clause this
            // returns a count instead of throwing.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                sweep.ExecuteSingleSweepAsync(
                    roomsDirectoryOverride: tempRoot,
                    thresholdBytesOverride: 0,
                    cancellationToken: cts.Token));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    [Fact]
    public async Task PruneRoomAsync_GraceWindow_PrunesOnlyWhenGraceElapsed()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "baton_sweep_prune_grace_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            // #1157: the grace window is measured from the run's terminal instant, which lives in the
            // journal -- so that is what these two fixtures differ in. Backdating flow.jsonl's mtime
            // (what this test used to do) no longer ages a room, which is the point of the change.
            var exec1 = new ExecutionId("exec-1");
            var room1Dir = await CreateTerminalRoomWithArtifactsAsync(
                tempRoot, "room-1-old", exec1, DateTime.UtcNow.AddHours(-2));

            var exec2 = new ExecutionId("exec-2");
            var room2Dir = await CreateTerminalRoomWithArtifactsAsync(
                tempRoot, "room-2-new", exec2, DateTime.UtcNow);

            var graceThreshold = TimeSpan.FromHours(1);
            var sweep = new RoomRetentionSweep();

            var (_, prunedCount) = await sweep.ExecuteSingleSweepAsync(
                roomsDirectoryOverride: tempRoot,
                graceOverride: graceThreshold,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(1, prunedCount);

            var room1Artifacts = Path.Combine(room1Dir, ArtifactManager.ArtifactsDirectoryName);
            var room1PrunedDir = ArtifactManager.ResolvePrunedOutputDirectory(room1Artifacts, exec1);
            Assert.True(Directory.Exists(room1PrunedDir));

            var room2Artifacts = Path.Combine(room2Dir, ArtifactManager.ArtifactsDirectoryName);
            var room2PrunedDir = ArtifactManager.ResolvePrunedOutputDirectory(room2Artifacts, exec2);
            Assert.False(Directory.Exists(room2PrunedDir));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    /// <summary>
    /// #1157, the headline: a run that ended two hours ago whose journal was appended to a moment ago
    /// is still two hours old. Under the retired <c>flow.jsonl</c>-mtime proxy the late append reset
    /// the grace window, so this room was kept — and kept again on every subsequent sweep for as long
    /// as anything kept touching the file.
    /// </summary>
    [Fact]
    public async Task PruneRoomAsync_OldTerminalInstant_ButFreshlyAppendedJournal_IsStillPruned()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "baton_sweep_prune_lateappend_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var execId = new ExecutionId("exec-late-append");
            var roomDir = await CreateTerminalRoomWithArtifactsAsync(
                tempRoot, "room-late-append", execId, DateTime.UtcNow.AddHours(-2));

            // The late append: a diagnostic StateProjector gives no StepState consequence, so the room
            // stays terminal and only the file's mtime (and its last line's stamp) move forward.
            var flowLogPath = Path.Combine(roomDir, "flow.jsonl");
            await AppendStampedLogEventsAsync(
                flowLogPath,
                DateTime.UtcNow,
                new FlowEvent.ZeroOutputsDespiteSubstantialWork(execId, "late diagnostic"));

            // The discriminating control, read BEFORE the assertion: the retired proxy would have kept
            // this room. Without it a passing test below could just mean the fixture never looked
            // fresh to begin with, which is the arm that decides whether this test is about anything.
            Assert.True(
                DateTime.UtcNow - File.GetLastWriteTimeUtc(flowLogPath) < TimeSpan.FromHours(1),
                "fixture is not exercising the defect: flow.jsonl's mtime must be INSIDE the grace window");

            var sweep = new RoomRetentionSweep();
            var (_, prunedCount) = await sweep.ExecuteSingleSweepAsync(
                roomsDirectoryOverride: tempRoot,
                graceOverride: TimeSpan.FromHours(1),
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(1, prunedCount);

            var artifactsRoot = Path.Combine(roomDir, ArtifactManager.ArtifactsDirectoryName);
            Assert.True(Directory.Exists(ArtifactManager.ResolvePrunedOutputDirectory(artifactsRoot, execId)));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    /// <summary>
    /// #1157 / spec/baton.md §3: a room with no terminal event has not ended, and the sweep may not
    /// invent an instant for it — including the crash window, where the journal simply stops. Pinned
    /// with a grace of zero so nothing but the missing terminal instant can be what refuses it.
    /// </summary>
    [Fact]
    public async Task PruneRoomAsync_NonTerminalRoom_HasNoTerminalInstantAndIsNotPruned()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "baton_sweep_prune_nonterminal_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var execId = new ExecutionId("exec-running");
            var roomDir = Path.Combine(tempRoot, "room-running");
            Directory.CreateDirectory(roomDir);

            await SnapshotBinder.PersistAsync(
                SingleStepSnapshot(),
                Path.Combine(roomDir, "snapshot.json"),
                TestContext.Current.CancellationToken);

            // Accepted but never settled -- the shape a journal has when the engine died mid-execution.
            await AppendStampedLogEventsAsync(
                Path.Combine(roomDir, "flow.jsonl"),
                DateTime.UtcNow.AddHours(-2),
                new FlowEvent.ExecutionRequestAccepted(TestRequest(execId)));

            var artifactsRoot = Path.Combine(roomDir, ArtifactManager.ArtifactsDirectoryName);
            var execDir = ArtifactManager.AllocateOutputDirectory(artifactsRoot, execId);
            await File.WriteAllTextAsync(
                Path.Combine(execDir, "output.txt"), "artifact-data", TestContext.Current.CancellationToken);

            var pruned = await RoomRetentionSweep.PruneRoomAsync(
                roomDir, TimeSpan.Zero, TestContext.Current.CancellationToken);

            Assert.False(pruned);
            Assert.True(Directory.Exists(execDir));
            Assert.False(Directory.Exists(ArtifactManager.ResolvePrunedOutputDirectory(artifactsRoot, execId)));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    /// <summary>
    /// #1157's legacy arm: a pre-#745 journal carries no writer stamps, so there is no terminal instant
    /// to read and the grace window falls back to <c>flow.jsonl</c>'s mtime — announced once per room,
    /// not once per sweep, so a daemon at the five-minute placeholder cadence does not emit 288 copies
    /// a day per room.
    /// </summary>
    [Fact]
    public async Task PruneRoomAsync_LegacyJournalWithNoWriterStamps_FallsBackToMtimeAndWarnsOncePerRoom()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "baton_sweep_prune_legacy_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var execId = new ExecutionId("exec-legacy");
            var roomDir = await CreateTerminalRoomWithArtifactsAsync(tempRoot, "room-legacy", execId, LegacyJournal);
            var flowLogPath = Path.Combine(roomDir, "flow.jsonl");

            // Only the mtime can age this room -- its journal carries no instant at all. Inside the
            // grace window first: the fallback has to be a real read of the mtime, not a blanket
            // "no instant, prune anyway".
            File.SetLastWriteTimeUtc(flowLogPath, DateTime.UtcNow);

            var warnings = new StringWriter();
            var keptInsideGrace = await RoomRetentionSweep.PruneRoomAsync(
                roomDir, TimeSpan.FromHours(1), TestContext.Current.CancellationToken, warnings);

            Assert.False(keptInsideGrace);

            File.SetLastWriteTimeUtc(flowLogPath, DateTime.UtcNow.AddHours(-2));

            var prunedOutsideGrace = await RoomRetentionSweep.PruneRoomAsync(
                roomDir, TimeSpan.FromHours(1), TestContext.Current.CancellationToken, warnings);

            Assert.True(prunedOutsideGrace);

            var lines = warnings.ToString()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var line = Assert.Single(lines);
            Assert.Contains(roomDir, line, StringComparison.Ordinal);
            Assert.Contains("predates writer timestamps", line, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    /// <summary>
    /// #1157, second reader: the warning above must name the CAUSE, not merely appear. The second of
    /// spec/baton.md §3's absence cases also reaches the mtime fallback, and the first version of this
    /// code told the operator such a room predated #745 — about a journal written seconds earlier, a
    /// wrong census in the one place that reports it.
    /// <para>
    /// This is the discriminating arm for
    /// <see cref="PruneRoomAsync_LegacyJournalWithNoWriterStamps_FallsBackToMtimeAndWarnsOncePerRoom"/>:
    /// that test asserts the #745 sentence, and without this one nothing would notice it being said
    /// about every absent instant rather than about a legacy journal specifically.
    /// </para>
    /// </summary>
    [Fact]
    public async Task PruneRoomAsync_ZeroStepWorkflow_FallsBackWithoutBlamingTheLegacyJournalCause()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "baton_sweep_prune_zerostep_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var roomDir = Path.Combine(tempRoot, "room-zero-step");
            Directory.CreateDirectory(roomDir);

            var zeroStepSnapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-empty"),
                new WorkflowTemplateId("zero-step"),
                WorkflowTemplateVersion: 1,
                Steps: []);
            await SnapshotBinder.PersistAsync(
                zeroStepSnapshot,
                Path.Combine(roomDir, "snapshot.json"),
                TestContext.Current.CancellationToken);

            // The shape RunCommand leaves behind: FlowEventLogWriter creates the journal on construction,
            // so the file exists and is empty. A zero-step snapshot projects terminal off that.
            var flowLogPath = Path.Combine(roomDir, "flow.jsonl");
            await File.WriteAllTextAsync(flowLogPath, string.Empty, TestContext.Current.CancellationToken);
            File.SetLastWriteTimeUtc(flowLogPath, DateTime.UtcNow.AddHours(-2));

            var warnings = new StringWriter();
            await RoomRetentionSweep.PruneRoomAsync(
                roomDir, TimeSpan.FromHours(1), TestContext.Current.CancellationToken, warnings);

            var line = Assert.Single(warnings.ToString()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            Assert.Contains(roomDir, line, StringComparison.Ordinal);
            Assert.DoesNotContain("predates writer timestamps", line, StringComparison.Ordinal);
            Assert.Contains("no journal line ever transitioned it to terminal", line, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    [Fact]
    public async Task PruneRoomAsync_KeepMarkedRoom_IsNotPruned()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "baton_sweep_prune_keep_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var execId = new ExecutionId("exec-keep");
            var roomDir = await CreateTerminalRoomWithArtifactsAsync(
                tempRoot, "room-keep", execId, DateTime.UtcNow.AddHours(-2));

            await KeepMarker.MarkKeepAsync(roomDir, TestContext.Current.CancellationToken);

            var sweep = new RoomRetentionSweep();
            var (_, prunedCount) = await sweep.ExecuteSingleSweepAsync(
                roomsDirectoryOverride: tempRoot,
                graceOverride: TimeSpan.Zero,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(0, prunedCount);

            var artifactsRoot = Path.Combine(roomDir, ArtifactManager.ArtifactsDirectoryName);
            var activeExecDir = ArtifactManager.ResolveOutputDirectory(artifactsRoot, execId);
            Assert.True(Directory.Exists(activeExecDir));

            var prunedDir = ArtifactManager.ResolvePrunedOutputDirectory(artifactsRoot, execId);
            Assert.False(Directory.Exists(prunedDir));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    [Fact]
    public async Task PerRoomResilience_RoomPruneFailure_DoesNotStopSweepFromPruningNextRoom()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "baton_sweep_prune_resilience_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            // Room 1: a genuinely terminal, prunable room whose ConcurrencyGuard is held by someone else, so
            // ArtifactPruner.PruneAsync (which self-acquires that guard fail-fast) throws WorkflowLockedException.
            // A corrupt flow.jsonl would NOT exercise the catch: FlowEventLogReader swallows a malformed line,
            // leaving the room merely non-terminal so PruneAsync returns false without throwing. Lock contention
            // is both the realistic per-room prune failure and one that actually reaches the catch.
            var exec1 = new ExecutionId("exec-1");
            var room1Dir = await CreateTerminalRoomWithArtifactsAsync(
                tempRoot, "room-1-locked", exec1, DateTime.UtcNow.AddHours(-2));

            // Room 2: an equally terminal, prunable room the sweep must still reach after room 1 throws.
            var exec2 = new ExecutionId("exec-2");
            var room2Dir = await CreateTerminalRoomWithArtifactsAsync(
                tempRoot, "room-2-valid", exec2, DateTime.UtcNow.AddHours(-2));

            var sweep = new RoomRetentionSweep();

            int prunedCount;
            using (ConcurrencyGuard.Acquire(room1Dir, "test holds room-1 lock"))
            {
                (_, prunedCount) = await sweep.ExecuteSingleSweepAsync(
                    roomsDirectoryOverride: tempRoot,
                    graceOverride: TimeSpan.FromHours(1),
                    cancellationToken: TestContext.Current.CancellationToken);
            }

            // Room 1 threw WorkflowLockedException (logged, skipped); room 2 was still pruned.
            Assert.Equal(1, prunedCount);

            var room1PrunedDir = ArtifactManager.ResolvePrunedOutputDirectory(
                Path.Combine(room1Dir, ArtifactManager.ArtifactsDirectoryName), exec1);
            Assert.False(Directory.Exists(room1PrunedDir));

            var room2PrunedDir = ArtifactManager.ResolvePrunedOutputDirectory(
                Path.Combine(room2Dir, ArtifactManager.ArtifactsDirectoryName), exec2);
            Assert.True(Directory.Exists(room2PrunedDir));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    [Fact]
    public void EnvironmentVariables_PruneDefaultsAndOverrides()
    {
        using (BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank))
        {
            Assert.False(RoomRetentionSweep.IsPruneEnabled());
            Assert.Equal(RoomRetentionSweep.PlaceholderDefaultPruneGrace, RoomRetentionSweep.GetPruneGrace());
        }

        using (BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { RetentionPruneEnabledOverride = "true" }))
        {
            Assert.True(RoomRetentionSweep.IsPruneEnabled());
        }

        using (BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { RetentionPruneEnabledOverride = "1" }))
        {
            Assert.True(RoomRetentionSweep.IsPruneEnabled());
        }

        using (BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { RetentionPruneGraceSecondsOverride = "1800" }))
        {
            Assert.Equal(TimeSpan.FromSeconds(1800), RoomRetentionSweep.GetPruneGrace());
        }
    }

    [Fact]
    public void GetPruneGrace_ClampsPathologicalValue_ToMaxPruneGrace()
    {
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { RetentionPruneGraceSecondsOverride = "1e300" });

        var grace = RoomRetentionSweep.GetPruneGrace();
        Assert.Equal(RoomRetentionSweep.MaxPruneGrace, grace);
    }

    [Fact]
    public void GetPruneGrace_LiftsSubSecondValue_ToMinPruneGrace()
    {
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { RetentionPruneGraceSecondsOverride = "1e-9" });

        var grace = RoomRetentionSweep.GetPruneGrace();
        Assert.Equal(RoomRetentionSweep.MinPruneGrace, grace);
    }

    // #1659: the retention hook -- RoomRetentionSweep may call `baton rooms prune --terminal` behind
    // DaemonSettings.RoomsRetentionDays. #2111 changed the settings default to 30, but a
    // RoomRetentionSweep constructed with no DaemonSettings at all (this constructor's own shape --
    // every unit test above this one, and the real daemon before its first settings load) still
    // resolves to off: there is no DaemonSettings instance here for a default to live on.
    [Fact]
    public void ResolveRoomsRetentionDays_NoSettingsAndNoOverride_IsNull()
    {
        var sweep = new RoomRetentionSweep();
        Assert.Null(sweep.ResolveRoomsRetentionDays());
    }

    // #2111: the settings default itself, read through the sweep the way the real daemon does once
    // DaemonSettingsStore.LoadAsync has actually loaded a fresh (or absent) settings.json.
    [Fact]
    public void ResolveRoomsRetentionDays_DefaultDaemonSettings_ResolvesToTheDefault()
    {
        var sweep = new RoomRetentionSweep(new Baton.Vendors.DaemonSettings());
        Assert.Equal(Baton.Vendors.DaemonSettings.DefaultRoomsRetentionDays, sweep.ResolveRoomsRetentionDays());
    }

    [Fact]
    public void ResolveRoomsRetentionDays_SettingsValue_IsUsedWhenNoOverride()
    {
        var sweep = new RoomRetentionSweep(new Baton.Vendors.DaemonSettings { RoomsRetentionDays = 5 });
        Assert.Equal(5, sweep.ResolveRoomsRetentionDays());
    }

    [Fact]
    public void ResolveRoomsRetentionDays_NonPositiveSettingsValue_IsTreatedAsOff()
    {
        var sweep = new RoomRetentionSweep(new Baton.Vendors.DaemonSettings { RoomsRetentionDays = 0 });
        Assert.Null(sweep.ResolveRoomsRetentionDays());
    }

    [Fact]
    public async Task ExecuteRoomsRetentionPruneAsync_NoRetentionConfigured_IsANoOp()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "baton_sweep_retention_test_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var roomDir = await CreateTerminalRoomWithArtifactsAsync(tempRoot, "old-room", new ExecutionId("exec-1"));
            await WriteRoomTerminalSentinelAsync(roomDir);
            var registryPath = Path.Combine(tempRoot, "room-registry.jsonl");
            await Baton.Vendors.RoomRegistryStore.AppendAsync(
                roomDir, tempRoot, registryPath, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);

            var sweep = new RoomRetentionSweep(); // no DaemonSettings -> RoomsRetentionDays unset
            var deletedCount = await sweep.ExecuteRoomsRetentionPruneAsync(
                registryFilePathOverride: registryPath, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(0, deletedCount);
            Assert.True(Directory.Exists(roomDir));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    [Fact]
    public async Task ExecuteRoomsRetentionPruneAsync_ConfiguredRetentionDays_DeletesAnOldTerminalRoom()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "baton_sweep_retention_test_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var roomDir = await CreateTerminalRoomWithArtifactsAsync(tempRoot, "old-room", new ExecutionId("exec-1"));
            var terminalSentinelPath = await WriteRoomTerminalSentinelAsync(roomDir);
            // Backdate the sentinel so a 1-day retention window finds it eligible.
            File.SetLastWriteTimeUtc(terminalSentinelPath, DateTime.UtcNow.AddDays(-30));

            var registryPath = Path.Combine(tempRoot, "room-registry.jsonl");
            await Baton.Vendors.RoomRegistryStore.AppendAsync(
                roomDir, tempRoot, registryPath, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);

            var sweep = new RoomRetentionSweep(new Baton.Vendors.DaemonSettings { RoomsRetentionDays = 1 });
            var deletedCount = await sweep.ExecuteRoomsRetentionPruneAsync(
                registryFilePathOverride: registryPath, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(1, deletedCount);
            Assert.False(Directory.Exists(roomDir));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    /// <summary>
    /// #2111: the issue's three-arm acceptance test, all through the exact path
    /// <see cref="RoomRetentionSweep.ExecuteRoomsRetentionPruneAsync"/> now uses. The kept room's sibling
    /// (an identically old, identically terminal, unkept room) is the discriminating control read
    /// first per the v-and-v gate: without it, a broken fixture producing zero candidates at all would
    /// also pass the kept assertion.
    /// </summary>
    [Fact]
    public async Task ExecuteRoomsRetentionPruneAsync_KeptRoomSurvives_UnkeptTerminalRoomOlderThanWindowGoes_NonTerminalRoomNeverTouched()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "baton_sweep_retention_arms_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var keptRoomDir = await CreateTerminalRoomWithArtifactsAsync(tempRoot, "kept-room", new ExecutionId("exec-kept"));
            var keptSentinelPath = await WriteRoomTerminalSentinelAsync(keptRoomDir);
            File.SetLastWriteTimeUtc(keptSentinelPath, DateTime.UtcNow.AddDays(-30));
            await KeepMarker.MarkKeepAsync(keptRoomDir, TestContext.Current.CancellationToken);

            var unkeptRoomDir = await CreateTerminalRoomWithArtifactsAsync(tempRoot, "unkept-room", new ExecutionId("exec-unkept"));
            var unkeptSentinelPath = await WriteRoomTerminalSentinelAsync(unkeptRoomDir);
            File.SetLastWriteTimeUtc(unkeptSentinelPath, DateTime.UtcNow.AddDays(-30));

            var nonTerminalRoomDir = await CreateTerminalRoomWithArtifactsAsync(tempRoot, "non-terminal-room", new ExecutionId("exec-open"));
            // No terminal sentinel written: RoomsPruneCommand's candidate discovery skips any room
            // TerminalSentinelWriter.TryReadAsync reads back null for, terminal.json age or not.

            var registryPath = Path.Combine(tempRoot, "room-registry.jsonl");
            foreach (var roomDir in new[] { keptRoomDir, unkeptRoomDir, nonTerminalRoomDir })
            {
                await Baton.Vendors.RoomRegistryStore.AppendAsync(
                    roomDir, tempRoot, registryPath, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);
            }

            var sweep = new RoomRetentionSweep(new Baton.Vendors.DaemonSettings { RoomsRetentionDays = 1 });
            var deletedCount = await sweep.ExecuteRoomsRetentionPruneAsync(
                registryFilePathOverride: registryPath, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(1, deletedCount);
            Assert.True(Directory.Exists(keptRoomDir), "a room marked keep must survive rooms-retention prune.");
            Assert.False(Directory.Exists(unkeptRoomDir), "the discriminating control: an unkept, old, terminal room must still go.");
            Assert.True(Directory.Exists(nonTerminalRoomDir), "a non-terminal room must never be touched.");
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    /// <summary>
    /// #2111: see <see cref="RoomRetentionSweep.AutomaticPruneHoldReason"/> for why. This is the
    /// discriminating pair to
    /// <see cref="ExecuteRoomsRetentionPruneAsync_ConfiguredRetentionDays_DeletesAnOldTerminalRoom"/>:
    /// same fixture, same retention setting, but reached through the automatic wrapper
    /// <see cref="RoomRetentionSweep.ExecuteAsync"/> itself calls, and it must delete nothing.
    /// </summary>
    [Fact]
    public async Task ExecuteAutomaticRoomsRetentionPruneAsync_ConfiguredRetentionDays_DeletesNothing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "baton_sweep_retention_hold_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var roomDir = await CreateTerminalRoomWithArtifactsAsync(tempRoot, "old-room", new ExecutionId("exec-1"));
            var terminalSentinelPath = await WriteRoomTerminalSentinelAsync(roomDir);
            File.SetLastWriteTimeUtc(terminalSentinelPath, DateTime.UtcNow.AddDays(-30));

            var sweep = new RoomRetentionSweep(new Baton.Vendors.DaemonSettings { RoomsRetentionDays = 1 });
            var deletedCount = await sweep.ExecuteAutomaticRoomsRetentionPruneAsync();

            Assert.Equal(0, deletedCount);
            Assert.True(Directory.Exists(roomDir));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    private static async Task<string> WriteRoomTerminalSentinelAsync(string roomDir)
    {
        var view = new Baton.Status.WorkflowStatusView(Baton.Status.WorkflowOutcome.Succeeded, [], [], null);
        await Baton.Status.TerminalSentinelWriter.WriteAsync(roomDir, view, TestContext.Current.CancellationToken);
        return Path.Combine(roomDir, Baton.Status.TerminalSentinelWriter.TerminalSentinelFileName);
    }
}
