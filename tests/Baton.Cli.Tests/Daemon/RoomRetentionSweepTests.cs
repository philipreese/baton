using System.Text.Json;
using Baton.Cli.Daemon;
using Baton.Artifacts;
using Baton.Concurrency;
using Baton.Domain;
using Baton.Status;
using Baton.Store;
using Baton.Templates;
using Baton.Cli.Tests.TestSupport;
using Xunit;

namespace Baton.Cli.Tests.Daemon;

/// <remarks>
/// #1524: same <see cref="BatonEnvironmentSnapshot.BeginScope"/> isolation as
/// <c>Baton.Vendors.Tests.WorkerRoleCatalogTests</c>.
/// </remarks>
[Collection(ConsoleErrorCaptureCollection.Name)]
public class RoomRetentionSweepTests
{
    private static readonly StepId StepA = new("stepA");
    private static readonly StepId StepB = new("stepB");

    private static WorkflowDefinitionSnapshot SingleStepSnapshot(bool review = false) => new(
        new WorkflowDefinitionSnapshotId("snapshot-1"),
        new WorkflowTemplateId("single-step"),
        WorkflowTemplateVersion: 1,
        Steps:
        [
            new WorkflowStepDefinition(StepA, review ? "reviewer" : "worker", [],
                [review ? "verdict.json" : "output.txt"], DependsOn: [], RetryPolicy: new RetryPolicy(1)),
        ]);

    private static ExecutionRequest TestRequest(ExecutionId execId, bool review = false) => new(
        execId,
        new WorkflowId("wf-1"),
        StepA,
        review ? "reviewer" : "worker",
        Inputs: [],
        Outputs: [review ? "verdict.json" : "output.txt"],
        Timeout: TimeSpan.FromMinutes(1),
        Environment: [],
        UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>(),
        ProducedOutputs: [new ProducedOutput(review ? "verdict.json" : "output.txt",
            Schema: review ? OutputSchema.ReviewVerdict : OutputSchema.None)]
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
        string parentDir, string roomName, ExecutionId execId, DateTime? terminalAtUtc = null,
        bool review = false)
    {
        var roomDir = Path.Combine(parentDir, roomName);
        Directory.CreateDirectory(roomDir);

        // A real completed workflow has acquired flow.lock. Archive capture must open that
        // existing lock and must refuse rooms where it cannot prove the writer protocol.
        await File.WriteAllTextAsync(Path.Combine(roomDir, ConcurrencyGuard.FlowLockFileName), "",
            TestContext.Current.CancellationToken);

        var snapshotPath = Path.Combine(roomDir, "snapshot.json");
        var logPath = Path.Combine(roomDir, "flow.jsonl");

        await SnapshotBinder.PersistAsync(SingleStepSnapshot(review), snapshotPath, TestContext.Current.CancellationToken);

        FlowEvent[] events =
        [
            new FlowEvent.ExecutionRequestAccepted(TestRequest(execId, review)),
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
        await File.WriteAllTextAsync(Path.Combine(execDir, review ? "verdict.json" : "output.txt"),
            review ? ModelWrittenVerdictFixture.Json : "artifact-data", TestContext.Current.CancellationToken);

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
        using var home = new IsolatedBatonHome();
        var tempRoot = Path.Combine(Path.GetTempPath(), "baton_sweep_retention_hold_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var roomDir = await CreateTerminalRoomWithArtifactsAsync(tempRoot, "old-room", new ExecutionId("exec-1"));
            var terminalSentinelPath = await WriteRoomTerminalSentinelAsync(roomDir);
            File.SetLastWriteTimeUtc(terminalSentinelPath, DateTime.UtcNow.AddDays(-30));

            var sweep = new RoomRetentionSweep(new Baton.Vendors.DaemonSettings { RoomsRetentionDays = 1 });
            var originalError = Console.Error;
            using var warning = new StringWriter();
            Console.SetError(warning);
            int deletedCount;
            try
            {
                deletedCount = await sweep.ExecuteAutomaticRoomsRetentionPruneAsync();
            }
            finally
            {
                Console.SetError(originalError);
            }

            Assert.Equal(0, deletedCount);
            Assert.Contains(RoomRetentionSweep.AutomaticPruneHoldReason, warning.ToString());
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
    public async Task AutomaticHeldSweep_CapturesImmutableEvidence_WithoutDeletingRoomOrRegistry()
    {
        using var home = new IsolatedBatonHome();
        var tempRoot = home.Path;
        var registryPath = BatonPaths.RoomRegistryFile;
        string? roomDir = null;
        try
        {
            roomDir = await CreateTerminalRoomWithArtifactsAsync(tempRoot, "old-room", new ExecutionId("exec-evidence"));
            var terminalSentinelPath = await WriteRoomTerminalSentinelAsync(roomDir);
            File.SetLastWriteTimeUtc(terminalSentinelPath, DateTime.UtcNow.AddDays(-15));
            await Baton.Vendors.RoomRegistryStore.AppendAsync(
                roomDir, tempRoot, registryPath, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);

            var sweep = new RoomRetentionSweep(new Baton.Vendors.DaemonSettings { RoomsRetentionDays = 14 });
            var result = await sweep.ExecuteAutomaticRoomsRetentionPruneAsync();

            Assert.Equal(0, result);
            Assert.True(Directory.Exists(roomDir));
            var roomKey = RoomRetentionEvidenceStore.RoomKey(roomDir);
            var leaves = Directory.Exists(Path.Combine(BatonPaths.RoomRetentionEvidence, roomKey))
                ? Directory.GetFiles(Path.Combine(BatonPaths.RoomRetentionEvidence, roomKey), "*.json")
                : [];
            var leafPath = Assert.Single(leaves);
            var generation = Path.GetFileNameWithoutExtension(leafPath);
            var evidence = await RoomRetentionEvidenceStore.ReadAsync(roomKey, generation, TestContext.Current.CancellationToken);
            Assert.NotNull(evidence);
            Assert.Equal(RoomRetentionEvidenceStore.NotApplicableExpectation, evidence.VerdictExpectation);
            Assert.True(evidence.Terminal.IsTerminal);
        }
        finally
        {
            if (roomDir is not null) DirectoryCleanup.DeleteRecursively(roomDir);
        }
    }

    [Fact]
    public async Task HeldSweep_ArchivesEligibleReviewExactly_AndLeavesYoungerRoomAndRegistry()
    {
        using var home = new IsolatedBatonHome();
        var old = await CreateTerminalRoomWithArtifactsAsync(home.Path, "old-review",
            new ExecutionId("exec-review"), review: true);
        var young = await CreateTerminalRoomWithArtifactsAsync(home.Path, "young-review",
            new ExecutionId("exec-young"), review: true);
        var usage = new ExecutionUsageView(TokensIn: 0, TokensOut: 7);
        var status = new WorkflowStatusView(WorkflowOutcome.Succeeded,
            [new WorkflowStatusStepView("stepA", "Succeeded", "exec-review", Usage: usage)], [], null);
        File.SetLastWriteTimeUtc(await WriteRoomTerminalSentinelAsync(old, status), DateTime.UtcNow.AddDays(-15));
        File.SetLastWriteTimeUtc(await WriteRoomTerminalSentinelAsync(young), DateTime.UtcNow.AddDays(-13));
        foreach (var room in new[] { old, young })
            await Baton.Vendors.RoomRegistryStore.AppendAsync(room, home.Path, BatonPaths.RoomRegistryFile,
                explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);
        var registry = await File.ReadAllBytesAsync(BatonPaths.RoomRegistryFile, TestContext.Current.CancellationToken);

        var sweep = new RoomRetentionSweep(new Baton.Vendors.DaemonSettings { RoomsRetentionDays = 14 });
        Assert.Equal(0, await sweep.ExecuteAutomaticRoomsRetentionPruneAsync());
        Assert.True(Directory.Exists(old));
        Assert.True(Directory.Exists(young));
        Assert.Equal(registry, await File.ReadAllBytesAsync(BatonPaths.RoomRegistryFile, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(Path.Combine(BatonPaths.RoomRetentionEvidence, RoomRetentionEvidenceStore.RoomKey(young))));
        var key = RoomRetentionEvidenceStore.RoomKey(old);
        var leaf = Assert.Single(Directory.GetFiles(Path.Combine(BatonPaths.RoomRetentionEvidence, key), "*.json"));
        var record = await RoomRetentionEvidenceStore.ReadAsync(key, Path.GetFileNameWithoutExtension(leaf),
            TestContext.Current.CancellationToken);
        Assert.NotNull(record);
        Assert.Equal(RoomRetentionEvidenceStore.ReviewExpectation, record.VerdictExpectation);
        var archivedVerdict = Assert.Single(record.Verdicts);
        Assert.Equal(ModelWrittenVerdictFixture.Json, archivedVerdict.Text);
        Assert.Equal(Path.GetFileNameWithoutExtension(leaf), record.GenerationSha256);
        static string Sha(string path) => Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        Assert.Equal(Sha(Path.Combine(old, BatonPaths.SnapshotFileName)), record.Sources.SnapshotSha256);
        Assert.Equal(Sha(Path.Combine(old, BatonPaths.FlowLogFileName)), record.Sources.JournalSha256);
        Assert.Equal(Sha(Path.Combine(old, TerminalSentinelWriter.TerminalSentinelFileName)), record.Sources.SentinelSha256);
        Assert.Equal(Sha(archivedVerdict.SourceIdentity), archivedVerdict.Sha256);
        Assert.NotNull(record.Sources.VerdictsSha256);
        Assert.Equal("exec-review", archivedVerdict.ExecutionId);
        Assert.EndsWith("verdict.json", archivedVerdict.SourceIdentity);
        Assert.Equal("Succeeded", record.Terminal.SentinelState);
        Assert.True(record.Terminal.IsTerminal);
        Assert.Equal(0, record.KnownUsage!["exec-review"].TokensIn);
        Assert.Equal(7, record.KnownUsage["exec-review"].TokensOut);
        Assert.Null(record.KnownUsage["exec-review"].WallClockMs);
        Assert.Equal("reviewer", Assert.Single(record.Provenance).Worker);
        Assert.Null(Assert.Single(record.Provenance).Adapter);
        Assert.Null(Assert.Single(record.Provenance).Model);
        var serialized = await File.ReadAllTextAsync(leaf, TestContext.Current.CancellationToken);
        Assert.Contains("\"tokensOut\": 7", serialized);
        await File.WriteAllTextAsync(leaf, serialized.Replace("\"tokensOut\": 7", "\"tokensOut\": 8",
            StringComparison.Ordinal), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<RoomRetentionEvidenceRefusalException>(() =>
            RoomRetentionEvidenceStore.ReadAsync(key, Path.GetFileNameWithoutExtension(leaf),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Capture_PrunnedReviewAndRefusalPaths_AreFailClosed()
    {
        using var home = new IsolatedBatonHome();
        var room = await CreateTerminalRoomWithArtifactsAsync(home.Path, "pruned-review",
            new ExecutionId("exec-pruned"), review: true);
        await WriteRoomTerminalSentinelAsync(room);
        var artifacts = Path.Combine(room, ArtifactManager.ArtifactsDirectoryName);
        var active = Path.Combine(ArtifactManager.ResolveOutputDirectory(artifacts, new ExecutionId("exec-pruned")), "verdict.json");
        var pruned = Path.Combine(ArtifactManager.ResolvePrunedOutputDirectory(artifacts, new ExecutionId("exec-pruned")), "verdict.json");
        Directory.CreateDirectory(Path.GetDirectoryName(pruned)!);
        File.Move(active, pruned);
        var key = RoomRetentionEvidenceStore.RoomKey(room);
        var first = await RoomRetentionEvidenceStore.CaptureAsync(room, key, NewCaptureBudget(), TestContext.Current.CancellationToken);
        Assert.NotNull(first);
        Assert.Equal(pruned, Assert.Single(first.Value.Record.Verdicts).SourceIdentity);
        await Assert.ThrowsAsync<RoomRetentionEvidenceRefusalException>(() =>
            RoomRetentionEvidenceStore.CaptureAsync(room, key, NewCaptureBudget(), TestContext.Current.CancellationToken,
                point =>
                {
                    if (point == RoomRetentionCapturePoint.BeforePublish) File.WriteAllText(active, "{}");
                }));
        await Assert.ThrowsAsync<RoomRetentionEvidenceRefusalException>(() =>
            RoomRetentionEvidenceStore.CaptureAsync(room, key, NewCaptureBudget(), TestContext.Current.CancellationToken));
        FileCleanup.EnsureDeleted(active);
        FileCleanup.EnsureDeleted(pruned);
        await Assert.ThrowsAsync<RoomRetentionEvidenceRefusalException>(() =>
            RoomRetentionEvidenceStore.CaptureAsync(room, key, NewCaptureBudget(), TestContext.Current.CancellationToken));
        await File.AppendAllTextAsync(Path.Combine(room, BatonPaths.FlowLogFileName), "{", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<RoomRetentionEvidenceRefusalException>(() =>
            RoomRetentionEvidenceStore.CaptureAsync(room, key, NewCaptureBudget(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Capture_TwoReviewExecutions_BindsEveryVerdictAndRefusesEarlierEvidenceChanges()
    {
        using var home = new IsolatedBatonHome();
        var room = Path.Combine(home.Path, "two-reviews");
        Directory.CreateDirectory(room);
        await File.WriteAllTextAsync(Path.Combine(room, ConcurrencyGuard.FlowLockFileName), "",
            TestContext.Current.CancellationToken);
        var firstId = new ExecutionId("exec-first-review");
        var secondId = new ExecutionId("exec-second-review");
        var snapshot = new WorkflowDefinitionSnapshot(
            new WorkflowDefinitionSnapshotId("two-reviews"), new WorkflowTemplateId("two-reviews"), 1,
            [
                new WorkflowStepDefinition(StepA, "reviewer", [], ["verdict.json"], DependsOn: [], RetryPolicy: new RetryPolicy(1)),
                new WorkflowStepDefinition(StepB, "reviewer", [], ["verdict.json"], DependsOn: [], RetryPolicy: new RetryPolicy(1)),
            ]);
        await SnapshotBinder.PersistAsync(snapshot, Path.Combine(room, BatonPaths.SnapshotFileName),
            TestContext.Current.CancellationToken);
        await WriteLogEventsAsync(Path.Combine(room, BatonPaths.FlowLogFileName),
            new FlowEvent.ExecutionRequestAccepted(TestRequest(firstId, review: true)),
            new FlowEvent.ExecutionSucceeded(firstId),
            new FlowEvent.ExecutionRequestAccepted(TestRequest(secondId, review: true) with { StepId = StepB }),
            new FlowEvent.ExecutionSucceeded(secondId));
        await WriteRoomTerminalSentinelAsync(room);
        var artifacts = Path.Combine(room, ArtifactManager.ArtifactsDirectoryName);
        var first = Path.Combine(ArtifactManager.AllocateOutputDirectory(artifacts, firstId), "verdict.json");
        var second = Path.Combine(ArtifactManager.AllocateOutputDirectory(artifacts, secondId), "verdict.json");
        await File.WriteAllTextAsync(first, ModelWrittenVerdictFixture.Json, TestContext.Current.CancellationToken);
        var secondText = ModelWrittenVerdictFixture.Json.Replace("all good", "second review", StringComparison.Ordinal);
        await File.WriteAllTextAsync(second, secondText, TestContext.Current.CancellationToken);
        var secondPruned = Path.Combine(ArtifactManager.ResolvePrunedOutputDirectory(artifacts, secondId), "verdict.json");
        Directory.CreateDirectory(Path.GetDirectoryName(secondPruned)!);
        File.Move(second, secondPruned);

        var key = RoomRetentionEvidenceStore.RoomKey(room);
        var original = await RoomRetentionEvidenceStore.CaptureAsync(room, key, NewCaptureBudget(),
            TestContext.Current.CancellationToken);
        Assert.NotNull(original);
        Assert.Equal([firstId.ToString(), secondId.ToString()],
            original.Value.Record.Verdicts.Select(item => item.ExecutionId));
        Assert.Equal([ModelWrittenVerdictFixture.Json, secondText],
            original.Value.Record.Verdicts.Select(item => item.Text));
        Assert.Equal([first, secondPruned], original.Value.Record.Verdicts.Select(item => item.SourceIdentity));
        var archived = await RoomRetentionEvidenceStore.ReadAsync(key, original.Value.Record.GenerationSha256,
            TestContext.Current.CancellationToken);
        Assert.Equal(original.Value.Record.Verdicts.Select(item => (item.ExecutionId, item.SourceIdentity, item.Text, item.Sha256)),
            archived!.Verdicts.Select(item => (item.ExecutionId, item.SourceIdentity, item.Text, item.Sha256)));
        Assert.All(archived.Verdicts, item =>
            Assert.True(JsonElement.DeepEquals(JsonDocument.Parse(item.Text).RootElement, item.Verdict)));

        FileCleanup.EnsureDeleted(first);
        await Assert.ThrowsAsync<RoomRetentionEvidenceRefusalException>(() =>
            RoomRetentionEvidenceStore.CaptureAsync(room, key, NewCaptureBudget(), TestContext.Current.CancellationToken));
        await File.WriteAllTextAsync(first, ModelWrittenVerdictFixture.Json, TestContext.Current.CancellationToken);
        var changedText = ModelWrittenVerdictFixture.Json.Replace("all good", "changed first review", StringComparison.Ordinal);
        await Assert.ThrowsAsync<RoomRetentionEvidenceRefusalException>(() =>
            RoomRetentionEvidenceStore.CaptureAsync(room, key, NewCaptureBudget(), TestContext.Current.CancellationToken,
                point =>
                {
                    if (point == RoomRetentionCapturePoint.BeforePublish) File.WriteAllText(first, changedText);
                }));
        var changed = await RoomRetentionEvidenceStore.CaptureAsync(room, key, NewCaptureBudget(),
            TestContext.Current.CancellationToken);
        Assert.NotNull(changed);
        Assert.NotEqual(original.Value.Record.GenerationSha256, changed.Value.Record.GenerationSha256);
        Assert.Equal(changedText, changed.Value.Record.Verdicts[0].Text);
        Assert.Equal(ModelWrittenVerdictFixture.Json, original.Value.Record.Verdicts[0].Text);

        var pruned = Path.Combine(ArtifactManager.ResolvePrunedOutputDirectory(artifacts, firstId), "verdict.json");
        Directory.CreateDirectory(Path.GetDirectoryName(pruned)!);
        await File.WriteAllTextAsync(pruned, ModelWrittenVerdictFixture.Json, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<RoomRetentionEvidenceRefusalException>(() =>
            RoomRetentionEvidenceStore.CaptureAsync(room, key, NewCaptureBudget(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SamePreparerHint_TracksEarlierReviewAndAbsentCounterpart()
    {
        using var home = new IsolatedBatonHome();
        var room = Path.Combine(home.Path, "hint-two-reviews");
        Directory.CreateDirectory(room);
        await File.WriteAllTextAsync(Path.Combine(room, ConcurrencyGuard.FlowLockFileName), "",
            TestContext.Current.CancellationToken);
        var firstId = new ExecutionId("exec-hint-first");
        var secondId = new ExecutionId("exec-hint-second");
        var snapshot = new WorkflowDefinitionSnapshot(
            new WorkflowDefinitionSnapshotId("hint-two-reviews"), new WorkflowTemplateId("hint-two-reviews"), 1,
            [
                new WorkflowStepDefinition(StepA, "reviewer", [], ["verdict.json"], DependsOn: [], RetryPolicy: new RetryPolicy(1)),
                new WorkflowStepDefinition(StepB, "reviewer", [], ["verdict.json"], DependsOn: [], RetryPolicy: new RetryPolicy(1)),
            ]);
        await SnapshotBinder.PersistAsync(snapshot, Path.Combine(room, BatonPaths.SnapshotFileName),
            TestContext.Current.CancellationToken);
        await WriteLogEventsAsync(Path.Combine(room, BatonPaths.FlowLogFileName),
            new FlowEvent.ExecutionRequestAccepted(TestRequest(firstId, review: true)),
            new FlowEvent.ExecutionSucceeded(firstId),
            new FlowEvent.ExecutionRequestAccepted(TestRequest(secondId, review: true) with { StepId = StepB }),
            new FlowEvent.ExecutionSucceeded(secondId));
        File.SetLastWriteTimeUtc(await WriteRoomTerminalSentinelAsync(room), DateTime.UtcNow.AddDays(-15));
        var artifacts = Path.Combine(room, ArtifactManager.ArtifactsDirectoryName);
        var first = Path.Combine(ArtifactManager.AllocateOutputDirectory(artifacts, firstId), "verdict.json");
        var second = Path.Combine(ArtifactManager.AllocateOutputDirectory(artifacts, secondId), "verdict.json");
        await File.WriteAllTextAsync(first, ModelWrittenVerdictFixture.Json, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(second, ModelWrittenVerdictFixture.Json, TestContext.Current.CancellationToken);
        await Baton.Vendors.RoomRegistryStore.AppendAsync(room, home.Path, BatonPaths.RoomRegistryFile,
            explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);

        var stablePaths = new[] { BatonPaths.SnapshotFileName, BatonPaths.FlowLogFileName,
            TerminalSentinelWriter.TerminalSentinelFileName }.Select(name => Path.Combine(room, name)).ToArray();
        var stable = stablePaths.Select(path => (new FileInfo(path).Length, new FileInfo(path).LastWriteTimeUtc)).ToArray();
        var preparer = new RoomRetentionEvidencePreparer();
        var key = RoomRetentionEvidenceStore.RoomKey(room);
        var leavesDirectory = Path.Combine(BatonPaths.RoomRetentionEvidence, key);
        Assert.Equal(1, await preparer.PrepareAsync(BatonPaths.RoomRegistryFile, 14, TestContext.Current.CancellationToken));
        var originalLeaf = Assert.Single(Directory.GetFiles(leavesDirectory, "*.json"));
        Assert.Equal(0, await preparer.PrepareAsync(BatonPaths.RoomRegistryFile, 14, TestContext.Current.CancellationToken));

        var changed = ModelWrittenVerdictFixture.Json.Replace("all good", "earlier review changed", StringComparison.Ordinal);
        await File.WriteAllTextAsync(first, changed, TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(first, DateTime.UtcNow.AddSeconds(3));
        Assert.Equal(1, await preparer.PrepareAsync(BatonPaths.RoomRegistryFile, 14, TestContext.Current.CancellationToken));
        var leaves = Directory.GetFiles(leavesDirectory, "*.json");
        Assert.Equal(2, leaves.Length);
        var newer = await RoomRetentionEvidenceStore.ReadAsync(key, Path.GetFileNameWithoutExtension(
            leaves.Single(path => path != originalLeaf)), TestContext.Current.CancellationToken);
        Assert.Equal(changed, newer!.Verdicts[0].Text);

        async Task<string> RefusalAsync()
        {
            using var errors = new StringWriter();
            var previous = Console.Error;
            Console.SetError(errors);
            try
            {
                Assert.Equal(0, await preparer.PrepareAsync(BatonPaths.RoomRegistryFile, 14,
                    TestContext.Current.CancellationToken));
            }
            finally { Console.SetError(previous); }
            return errors.ToString();
        }

        FileCleanup.EnsureDeleted(first);
        Assert.Contains("missing or invalid", await RefusalAsync());
        await File.WriteAllTextAsync(first, changed, TestContext.Current.CancellationToken);
        var pruned = Path.Combine(ArtifactManager.ResolvePrunedOutputDirectory(artifacts, firstId), "verdict.json");
        Directory.CreateDirectory(Path.GetDirectoryName(pruned)!);
        await File.WriteAllTextAsync(pruned, ModelWrittenVerdictFixture.Json, TestContext.Current.CancellationToken);
        Assert.Contains("copies conflict", await RefusalAsync());
        FileCleanup.EnsureDeleted(pruned);
        Assert.Equal(1, await new RoomRetentionEvidencePreparer().PrepareAsync(BatonPaths.RoomRegistryFile, 14,
            TestContext.Current.CancellationToken));
        Assert.Equal(2, Directory.GetFiles(leavesDirectory, "*.json").Length);
        Assert.Equal(stable, stablePaths.Select(path =>
            (new FileInfo(path).Length, new FileInfo(path).LastWriteTimeUtc)).ToArray());
    }

    [Theory]
    [InlineData("flow")]
    [InlineData("room")]
    [InlineData("future-owner")]
    public async Task Capture_UnknownJournalEntryAfterTerminal_RefusesCompleteLeaf(string owner)
    {
        using var home = new IsolatedBatonHome();
        var room = await CreateTerminalRoomWithArtifactsAsync(home.Path, $"unknown-{owner}-after-terminal",
            new ExecutionId("exec-unknown"));
        await WriteRoomTerminalSentinelAsync(room);
        var unknownLine = owner switch
        {
            "flow" => "{\"owner\":\"flow\",\"Event\":{\"eventType\":\"future-event\"},\"WriterUtcTimestamp\":null}\n",
            "room" => "{\"owner\":\"room\",\"Event\":{\"eventType\":\"future-event\"},\"WriterUtcTimestamp\":null}\n",
            _ => "{\"owner\":\"future-owner\"}\n"
        };
        await File.AppendAllTextAsync(Path.Combine(room, BatonPaths.FlowLogFileName),
            unknownLine,
            TestContext.Current.CancellationToken);
        var key = RoomRetentionEvidenceStore.RoomKey(room);
        var refusal = await Assert.ThrowsAsync<RoomRetentionEvidenceRefusalException>(() =>
            RoomRetentionEvidenceStore.CaptureAsync(room, key, NewCaptureBudget(), TestContext.Current.CancellationToken));
        Assert.Contains("unknown event", refusal.Message);
        Assert.False(Directory.Exists(Path.Combine(BatonPaths.RoomRetentionEvidence, key)));
    }

    [Fact]
    public async Task Capture_ReplayReusesLeaf_AndMalformedLeafNeverOverwrites()
    {
        using var home = new IsolatedBatonHome();
        var room = await CreateTerminalRoomWithArtifactsAsync(home.Path, "replay", new ExecutionId("exec-replay"));
        await WriteRoomTerminalSentinelAsync(room);
        var key = RoomRetentionEvidenceStore.RoomKey(room);
        var first = await RoomRetentionEvidenceStore.CaptureAsync(room, key, NewCaptureBudget(), TestContext.Current.CancellationToken);
        Assert.NotNull(first);
        var leaf = RoomRetentionEvidenceStore.LeafPath(key, first.Value.Record.GenerationSha256);
        var original = await File.ReadAllBytesAsync(leaf, TestContext.Current.CancellationToken);
        Assert.NotNull(await RoomRetentionEvidenceStore.CaptureAsync(room, key, NewCaptureBudget(), TestContext.Current.CancellationToken));
        Assert.Equal(original, await File.ReadAllBytesAsync(leaf, TestContext.Current.CancellationToken));
        await File.WriteAllTextAsync(leaf, "{}", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<RoomRetentionEvidenceRefusalException>(() =>
            RoomRetentionEvidenceStore.CaptureAsync(room, key, NewCaptureBudget(), TestContext.Current.CancellationToken));
        Assert.Equal("{}", await File.ReadAllTextAsync(leaf, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PublishedLeaf_RejectsResignedUnrelatedIdentitiesAndInvalidProvenance()
    {
        using var home = new IsolatedBatonHome();
        var execution = new ExecutionId("exec-identity");
        var room = await CreateTerminalRoomWithArtifactsAsync(home.Path, "leaf-identity", execution, review: true);
        await WriteRoomTerminalSentinelAsync(room);
        var key = RoomRetentionEvidenceStore.RoomKey(room);
        var captured = await RoomRetentionEvidenceStore.CaptureAsync(room, key, NewCaptureBudget(),
            TestContext.Current.CancellationToken);
        Assert.NotNull(captured);
        var original = captured.Value.Record;

        static string InvokeDigest(string method, object value) =>
            (string)typeof(RoomRetentionEvidenceStore).GetMethod(method,
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .Invoke(null, [value])!;
        static RoomRetentionEvidenceRecord Resign(RoomRetentionEvidenceRecord record)
        {
            var sources = record.Sources with { VerdictsSha256 = InvokeDigest("VerdictsDigest", record.Verdicts) };
            var unsigned = record with
            {
                Sources = sources,
                GenerationSha256 = InvokeDigest("GenerationDigest", sources),
                PayloadSha256 = null
            };
            return unsigned with { PayloadSha256 = InvokeDigest("PayloadDigest", unsigned) };
        }
        async Task RejectAsync(RoomRetentionEvidenceRecord altered)
        {
            var signed = Resign(altered);
            var leaf = RoomRetentionEvidenceStore.LeafPath(key, signed.GenerationSha256);
            var prior = File.Exists(leaf) ? await File.ReadAllBytesAsync(leaf, TestContext.Current.CancellationToken) : null;
            try
            {
                await File.WriteAllBytesAsync(leaf,
                    JsonSerializer.SerializeToUtf8Bytes(signed, new JsonSerializerOptions { WriteIndented = true }),
                    TestContext.Current.CancellationToken);
                await Assert.ThrowsAsync<RoomRetentionEvidenceRefusalException>(() =>
                    RoomRetentionEvidenceStore.ReadAsync(key, signed.GenerationSha256, TestContext.Current.CancellationToken));
            }
            finally
            {
                if (prior is null) FileCleanup.EnsureDeleted(leaf);
                else await File.WriteAllBytesAsync(leaf, prior, TestContext.Current.CancellationToken);
            }
        }

        await RejectAsync(original with { RoomIdentity = Path.Combine(home.Path, "other-room") });
        var verdict = Assert.Single(original.Verdicts);
        var artifacts = Path.Combine(room, ArtifactManager.ArtifactsDirectoryName);
        var wrongExecution = Path.Combine(ArtifactManager.ResolveOutputDirectory(artifacts,
            new ExecutionId("exec-other")), "verdict.json");
        var wrongRoom = Path.Combine(ArtifactManager.ResolveOutputDirectory(
            Path.Combine(home.Path, "other-room", ArtifactManager.ArtifactsDirectoryName), execution), "verdict.json");
        foreach (var path in new[] { "relative/verdict.json", wrongExecution, wrongRoom,
            Path.Combine(room, "artifacts", "..", "verdict.json") })
            await RejectAsync(original with { Verdicts = [verdict with { SourceIdentity = path }] });
        await RejectAsync(original with { Provenance = [Assert.Single(original.Provenance) with { Worker = " " }] });
        await RejectAsync(original with { Provenance = [Assert.Single(original.Provenance) with { StepId = "" }] });

        DirectoryCleanup.DeleteRecursively(room);
        var historical = await RoomRetentionEvidenceStore.ReadAsync(key, original.GenerationSha256,
            TestContext.Current.CancellationToken);
        Assert.Equal(verdict.Text, Assert.Single(historical!.Verdicts).Text);
    }

    [Fact]
    public async Task ArchiveSurvivesTwoObservedOperationalLogRotationsAndFreshPreparer()
    {
        using var home = new IsolatedBatonHome();
        var room = await CreateTerminalRoomWithArtifactsAsync(home.Path, "rotation-review",
            new ExecutionId("exec-rotation"), review: true);
        await WriteRoomTerminalSentinelAsync(room);
        var key = RoomRetentionEvidenceStore.RoomKey(room);
        var captured = await RoomRetentionEvidenceStore.CaptureAsync(room, key, NewCaptureBudget(),
            TestContext.Current.CancellationToken);
        Assert.NotNull(captured);
        File.SetLastWriteTimeUtc(Path.Combine(room, TerminalSentinelWriter.TerminalSentinelFileName),
            DateTime.UtcNow.AddDays(-15));
        await Baton.Vendors.RoomRegistryStore.AppendAsync(room, home.Path, BatonPaths.RoomRegistryFile,
            explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);
        var live = Path.Combine(home.Path, "fleet-events.jsonl");
        var rollover = Path.Combine(home.Path, "fleet-events.1.jsonl");
        var log = new FleetEventLog(live, rollover, maxLiveBytes: 1);
        async Task AppendAsync(int index) => await log.Append(new FleetEventDraft(FleetEventKind.DaemonStarted,
            $"rotation-control:{index}", DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        await AppendAsync(0);
        Assert.False(File.Exists(rollover));
        await AppendAsync(1);
        Assert.Contains("rotation-control:0", await File.ReadAllTextAsync(rollover, TestContext.Current.CancellationToken));
        await AppendAsync(2);
        var secondRollover = await File.ReadAllTextAsync(rollover, TestContext.Current.CancellationToken);
        Assert.Contains("rotation-control:1", secondRollover);
        Assert.DoesNotContain("rotation-control:0", secondRollover);
        Assert.Contains("rotation-control:2", await File.ReadAllTextAsync(live, TestContext.Current.CancellationToken));
        log = null!;
        var freshPreparer = new RoomRetentionEvidencePreparer();
        Assert.Equal(1, await freshPreparer.PrepareAsync(BatonPaths.RoomRegistryFile, 14,
            TestContext.Current.CancellationToken));
        var archived = await RoomRetentionEvidenceStore.ReadAsync(key, captured.Value.Record.GenerationSha256,
            TestContext.Current.CancellationToken);
        Assert.NotNull(archived);
        Assert.Equal(ModelWrittenVerdictFixture.Json, Assert.Single(archived.Verdicts).Text);
        Assert.Equal(captured.Value.Record.Terminal, archived.Terminal);
    }

    [Fact]
    public async Task ConcurrentIdenticalCapture_LeavesOneValidatedGeneration()
    {
        using var home = new IsolatedBatonHome();
        var room = await CreateTerminalRoomWithArtifactsAsync(home.Path, "concurrent",
            new ExecutionId("exec-concurrent"));
        await WriteRoomTerminalSentinelAsync(room);
        var key = RoomRetentionEvidenceStore.RoomKey(room);
        async Task<bool> AttemptAsync()
        {
            try
            {
                return await RoomRetentionEvidenceStore.CaptureAsync(room, key, NewCaptureBudget(),
                    TestContext.Current.CancellationToken) is not null;
            }
            catch (RoomRetentionEvidenceRefusalException) { return false; }
        }
        var results = await Task.WhenAll(AttemptAsync(), AttemptAsync());
        Assert.Contains(true, results);
        var leaf = Assert.Single(Directory.GetFiles(Path.Combine(BatonPaths.RoomRetentionEvidence, key), "*.json"));
        Assert.NotNull(await RoomRetentionEvidenceStore.ReadAsync(key, Path.GetFileNameWithoutExtension(leaf),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SourceRaces_BeforeGuardAndDuringRead_CannotPublishMixedGeneration()
    {
        using var home = new IsolatedBatonHome();
        var reopened = await CreateTerminalRoomWithArtifactsAsync(home.Path, "reopened", new ExecutionId("exec-reopened"));
        await WriteRoomTerminalSentinelAsync(reopened);
        var reopenLog = Path.Combine(reopened, BatonPaths.FlowLogFileName);
        var firstLine = (await File.ReadAllLinesAsync(reopenLog, TestContext.Current.CancellationToken))[0];
        await Assert.ThrowsAsync<RoomRetentionEvidenceRefusalException>(() =>
            RoomRetentionEvidenceStore.CaptureAsync(reopened, RoomRetentionEvidenceStore.RoomKey(reopened),
                NewCaptureBudget(), TestContext.Current.CancellationToken, point =>
                {
                    if (point == RoomRetentionCapturePoint.BeforeGuard) File.WriteAllText(reopenLog, firstLine + "\n");
                }));
        Assert.False(Directory.Exists(Path.Combine(BatonPaths.RoomRetentionEvidence,
            RoomRetentionEvidenceStore.RoomKey(reopened))));

        var changing = await CreateTerminalRoomWithArtifactsAsync(home.Path, "changing-during-read",
            new ExecutionId("exec-race"));
        await WriteRoomTerminalSentinelAsync(changing);
        var journal = Path.Combine(changing, BatonPaths.FlowLogFileName);
        await Assert.ThrowsAsync<RoomRetentionEvidenceRefusalException>(() =>
            RoomRetentionEvidenceStore.CaptureAsync(changing, RoomRetentionEvidenceStore.RoomKey(changing),
                NewCaptureBudget(), TestContext.Current.CancellationToken, point =>
                {
                    if (point == RoomRetentionCapturePoint.AfterJournalRead)
                    {
                        var original = File.ReadAllText(journal);
                        File.WriteAllText(journal, original.Replace("\":", "\" : ", StringComparison.Ordinal));
                    }
                }));
        Assert.False(Directory.Exists(Path.Combine(BatonPaths.RoomRetentionEvidence,
            RoomRetentionEvidenceStore.RoomKey(changing))));
    }

    [Fact]
    public async Task Capture_DeletedBeforeGuard_DoesNotRecreateRoom()
    {
        using var home = new IsolatedBatonHome();
        var room = await CreateTerminalRoomWithArtifactsAsync(home.Path, "deleted-before-guard",
            new ExecutionId("exec-deleted"));
        await WriteRoomTerminalSentinelAsync(room);
        var key = RoomRetentionEvidenceStore.RoomKey(room);
        await Assert.ThrowsAsync<RoomRetentionEvidenceRefusalException>(() =>
            RoomRetentionEvidenceStore.CaptureAsync(room, key, NewCaptureBudget(),
                TestContext.Current.CancellationToken, point =>
                {
                    if (point == RoomRetentionCapturePoint.BeforeGuard) DirectoryCleanup.DeleteRecursively(room);
                }));
        Assert.False(Directory.Exists(room));
        Assert.False(Directory.Exists(Path.Combine(BatonPaths.RoomRetentionEvidence, key)));
    }

    [Fact]
    public async Task Capture_HeldFlowLock_RefusesWithoutPublishing()
    {
        using var home = new IsolatedBatonHome();
        var room = await CreateTerminalRoomWithArtifactsAsync(home.Path, "held-flow",
            new ExecutionId("exec-held"));
        await WriteRoomTerminalSentinelAsync(room);
        using var held = ConcurrencyGuard.Acquire(room, "held capture control");
        var key = RoomRetentionEvidenceStore.RoomKey(room);
        await Assert.ThrowsAsync<RoomRetentionEvidenceRefusalException>(() =>
            RoomRetentionEvidenceStore.CaptureAsync(room, key, NewCaptureBudget(),
                TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(Path.Combine(BatonPaths.RoomRetentionEvidence, key)));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("custom-verdict")]
    [InlineData("non-review")]
    public async Task Capture_FrozenOutputContract_DeterminesVerdictExpectation(string contract)
    {
        using var home = new IsolatedBatonHome();
        var room = await CreateTerminalRoomWithArtifactsAsync(home.Path, "contract-" + contract,
            new ExecutionId("exec-contract"));
        await WriteRoomTerminalSentinelAsync(room);
        var original = TestRequest(new ExecutionId("exec-contract"));
        var request = original with
        {
            Worker = contract == "non-review" ? "preview" : "audit",
            Outputs = contract == "non-review" ? ["output.txt"] : ["findings.json"],
            ProducedOutputs = contract switch
            {
                "unknown" => null,
                "custom-verdict" => [new ProducedOutput("findings.json", Schema: OutputSchema.ReviewVerdict)],
                _ => [new ProducedOutput("output.txt", Schema: OutputSchema.None)]
            }
        };
        var journal = Path.Combine(room, BatonPaths.FlowLogFileName);
        await File.WriteAllTextAsync(journal, "", TestContext.Current.CancellationToken);
        await WriteLogEventsAsync(journal, new FlowEvent.ExecutionRequestAccepted(request),
            new FlowEvent.ExecutionSucceeded(request.ExecutionId));
        var key = RoomRetentionEvidenceStore.RoomKey(room);
        if (contract == "non-review")
        {
            var captured = await RoomRetentionEvidenceStore.CaptureAsync(room, key, NewCaptureBudget(),
                TestContext.Current.CancellationToken);
            Assert.Equal(RoomRetentionEvidenceStore.NotApplicableExpectation, captured!.Value.Record.VerdictExpectation);
        }
        else
        {
            await Assert.ThrowsAsync<RoomRetentionEvidenceRefusalException>(() =>
                RoomRetentionEvidenceStore.CaptureAsync(room, key, NewCaptureBudget(),
                    TestContext.Current.CancellationToken));
            Assert.False(Directory.Exists(Path.Combine(BatonPaths.RoomRetentionEvidence, key)));
        }
    }

    [Fact]
    public async Task Capture_CancellationDuringReadAndBetweenRooms_StopsPublication()
    {
        using var home = new IsolatedBatonHome();
        var first = await CreateTerminalRoomWithArtifactsAsync(home.Path, "cancel-first",
            new ExecutionId("exec-first"));
        var second = await CreateTerminalRoomWithArtifactsAsync(home.Path, "cancel-second",
            new ExecutionId("exec-second"));
        foreach (var room in new[] { first, second })
        {
            File.SetLastWriteTimeUtc(await WriteRoomTerminalSentinelAsync(room), DateTime.UtcNow.AddDays(-15));
            await Baton.Vendors.RoomRegistryStore.AppendAsync(room, home.Path, BatonPaths.RoomRegistryFile,
                explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);
        }

        using (var duringRead = new CancellationTokenSource())
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                RoomRetentionEvidenceStore.CaptureAsync(first, RoomRetentionEvidenceStore.RoomKey(first),
                    new RoomRetentionEvidenceStore.SweepBudget(System.Diagnostics.Stopwatch.GetTimestamp(), duringRead.Token),
                    duringRead.Token, journalReadProgress: _ => duringRead.Cancel()));
            Assert.False(Directory.Exists(Path.Combine(BatonPaths.RoomRetentionEvidence,
                RoomRetentionEvidenceStore.RoomKey(first))));
        }

        using var between = new CancellationTokenSource();
        var preparer = new RoomRetentionEvidencePreparer();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparer.PrepareAsync(
            BatonPaths.RoomRegistryFile, 14, between.Token, candidate =>
            {
                if (candidate == 2) between.Cancel();
            }));
        Assert.True(Directory.Exists(Path.Combine(BatonPaths.RoomRetentionEvidence,
            RoomRetentionEvidenceStore.RoomKey(first))));
        Assert.False(Directory.Exists(Path.Combine(BatonPaths.RoomRetentionEvidence,
            RoomRetentionEvidenceStore.RoomKey(second))));
    }

    [Fact]
    public async Task Capture_MultipleRooms_DoNotChargeEarlierRoomsToLaterRoom()
    {
        using var home = new IsolatedBatonHome();
        var first = await CreateTerminalRoomWithArtifactsAsync(home.Path, "budget-first",
            new ExecutionId("exec-first"));
        var second = await CreateTerminalRoomWithArtifactsAsync(home.Path, "budget-second",
            new ExecutionId("exec-second"));
        await WriteRoomTerminalSentinelAsync(first);
        await WriteRoomTerminalSentinelAsync(second);
        var sweep = NewCaptureBudget();
        Assert.NotNull(await RoomRetentionEvidenceStore.CaptureAsync(first,
            RoomRetentionEvidenceStore.RoomKey(first), sweep, TestContext.Current.CancellationToken));
        var earlierSelection = System.Diagnostics.Stopwatch.GetTimestamp() -
            (long)(3 * System.Diagnostics.Stopwatch.Frequency);
        Assert.NotNull(await RoomRetentionEvidenceStore.CaptureAsync(second,
            RoomRetentionEvidenceStore.RoomKey(second), sweep, TestContext.Current.CancellationToken,
            selectionStarted: earlierSelection, selectionElapsed: TimeSpan.FromMilliseconds(1)));
    }

    [Fact]
    public async Task Discovery_MalformedCompleteLine_DoesNotStarveValidRoom()
    {
        using var home = new IsolatedBatonHome();
        var room = await CreateTerminalRoomWithArtifactsAsync(home.Path, "after-malformed",
            new ExecutionId("exec-valid"));
        File.SetLastWriteTimeUtc(await WriteRoomTerminalSentinelAsync(room), DateTime.UtcNow.AddDays(-15));
        var registry = BatonPaths.RoomRegistryFile;
        Directory.CreateDirectory(Path.GetDirectoryName(registry)!);
        await File.WriteAllTextAsync(registry, "{malformed}\n" +
            JsonSerializer.Serialize(new Baton.Vendors.RoomRegistryEntry(room, home.Path, DateTime.UtcNow)) +
            "\n", TestContext.Current.CancellationToken);
        var preparer = new RoomRetentionEvidencePreparer();
        Assert.Equal(1, await preparer.PrepareAsync(registry, 14, TestContext.Current.CancellationToken));
        Assert.Equal(0, preparer.Cursor);
    }

    [Fact]
    public async Task Capture_AliasRoomPath_PublishesCanonicalIdentities()
    {
        using var home = new IsolatedBatonHome();
        var room = await CreateTerminalRoomWithArtifactsAsync(home.Path, "canonical-room",
            new ExecutionId("exec-canonical"), review: true);
        await WriteRoomTerminalSentinelAsync(room);
        var alias = room.Replace('\\', '/');
        var key = RoomRetentionEvidenceStore.RoomKey(alias);
        Assert.Equal(RoomRetentionEvidenceStore.RoomKey(room.ToUpperInvariant()), key);
        var captured = await RoomRetentionEvidenceStore.CaptureAsync(alias, key, NewCaptureBudget(),
            TestContext.Current.CancellationToken);
        Assert.NotNull(captured);
        Assert.Equal(BatonPaths.RecordKey(room), captured.Value.Record.RoomIdentity);
        Assert.StartsWith(Path.Combine(room, ArtifactManager.ArtifactsDirectoryName),
            Assert.Single(captured.Value.Record.Verdicts).SourceIdentity);
        Assert.NotNull(await RoomRetentionEvidenceStore.ReadAsync(key, captured.Value.Record.GenerationSha256,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PublishedLeaf_MalformedFieldsRefuseWithTypedEvidenceError()
    {
        using var home = new IsolatedBatonHome();
        var room = await CreateTerminalRoomWithArtifactsAsync(home.Path, "malformed-leaf",
            new ExecutionId("exec-leaf"), review: true);
        await WriteRoomTerminalSentinelAsync(room);
        var key = RoomRetentionEvidenceStore.RoomKey(room);
        var captured = await RoomRetentionEvidenceStore.CaptureAsync(room, key, NewCaptureBudget(),
            TestContext.Current.CancellationToken);
        Assert.NotNull(captured);
        var leaf = RoomRetentionEvidenceStore.LeafPath(key, captured.Value.Record.GenerationSha256);
        var original = await File.ReadAllTextAsync(leaf, TestContext.Current.CancellationToken);
        try
        {
            foreach (var changed in new[]
            {
                original.Replace(captured.Value.Record.Sources.JournalSha256, "null", StringComparison.Ordinal),
                original.Replace("\"verdict\": {", "\"verdictMissing\": {", StringComparison.Ordinal),
                original.Replace("\"knownUsage\": null", "\"knownUsage\": {\"other\": {}}",
                    StringComparison.Ordinal)
            })
            {
                await File.WriteAllTextAsync(leaf, changed, TestContext.Current.CancellationToken);
                await Assert.ThrowsAsync<RoomRetentionEvidenceRefusalException>(() =>
                    RoomRetentionEvidenceStore.ReadAsync(key, captured.Value.Record.GenerationSha256,
                        TestContext.Current.CancellationToken));
            }
        }
        finally
        {
            await File.WriteAllTextAsync(leaf, original, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task GrowingJournalDuringChargedSourceRead_RefusesWithinRoomBudget()
    {
        using var home = new IsolatedBatonHome();
        var room = await CreateTerminalRoomWithArtifactsAsync(home.Path, "growing-journal",
            new ExecutionId("exec-growing"));
        await WriteRoomTerminalSentinelAsync(room);
        var journal = Path.Combine(room, BatonPaths.FlowLogFileName);
        var key = RoomRetentionEvidenceStore.RoomKey(room);
        var budget = NewCaptureBudget();
        var grew = false;
        var refusal = await Assert.ThrowsAsync<RoomRetentionEvidenceRefusalException>(() =>
            RoomRetentionEvidenceStore.CaptureAsync(room, key, budget, TestContext.Current.CancellationToken,
                journalReadProgress: chargedBytes =>
                {
                    if (grew) return;
                    Assert.True(chargedBytes > 0);
                    grew = true;
                    File.AppendAllText(journal, new string('x', (int)RoomRetentionEvidenceLimits.MaxSourceBytesPerRoom));
                }));
        Assert.True(grew);
        Assert.Contains("bounded read budget", refusal.Message);
        Assert.InRange(RoomRetentionEvidenceLimits.MaxSourceBytesPerSweep - budget.Remaining,
            1, RoomRetentionEvidenceLimits.MaxSourceBytesPerRoom);
        Assert.False(Directory.Exists(Path.Combine(BatonPaths.RoomRetentionEvidence, key)));
    }

    [Theory]
    [InlineData((int)RoomRetentionCapturePoint.BeforePublish, false)]
    [InlineData((int)RoomRetentionCapturePoint.AfterTemporaryFlush, false)]
    [InlineData((int)RoomRetentionCapturePoint.AfterPublication, true)]
    public async Task PublicationCrashPoints_ReplayConvergesWithoutReplacing(
        int crashPointValue, bool published)
    {
        var crashPoint = (RoomRetentionCapturePoint)crashPointValue;
        using var home = new IsolatedBatonHome();
        var room = await CreateTerminalRoomWithArtifactsAsync(home.Path, "crash", new ExecutionId("exec-crash"));
        await WriteRoomTerminalSentinelAsync(room);
        var key = RoomRetentionEvidenceStore.RoomKey(room);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RoomRetentionEvidenceStore.CaptureAsync(room, key, NewCaptureBudget(),
                TestContext.Current.CancellationToken, point =>
                {
                    if (point == crashPoint) throw new InvalidOperationException("simulated process stop");
                }));
        var directory = Path.Combine(BatonPaths.RoomRetentionEvidence, key);
        Assert.Equal(published ? 1 : 0, Directory.Exists(directory) ? Directory.GetFiles(directory, "*.json").Length : 0);
        if (Directory.Exists(directory))
            await File.WriteAllTextAsync(Path.Combine(directory, "orphan.tmp"), "incomplete",
                TestContext.Current.CancellationToken);
        var recovered = await RoomRetentionEvidenceStore.CaptureAsync(room, key, NewCaptureBudget(),
            TestContext.Current.CancellationToken);
        Assert.NotNull(recovered);
        Assert.Single(Directory.GetFiles(directory, "*.json"));
        Assert.NotNull(await RoomRetentionEvidenceStore.ReadAsync(key, recovered.Value.Record.GenerationSha256,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Preparation_UsesUnchangedHintButChangedJournalGetsNewGeneration()
    {
        using var home = new IsolatedBatonHome();
        var room = await CreateTerminalRoomWithArtifactsAsync(home.Path, "changing", new ExecutionId("exec-changing"));
        File.SetLastWriteTimeUtc(await WriteRoomTerminalSentinelAsync(room), DateTime.UtcNow.AddDays(-15));
        await Baton.Vendors.RoomRegistryStore.AppendAsync(room, home.Path, BatonPaths.RoomRegistryFile,
            explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);
        var preparer = new RoomRetentionEvidencePreparer();
        Assert.Equal(1, await preparer.PrepareAsync(BatonPaths.RoomRegistryFile, 14, TestContext.Current.CancellationToken));
        var key = RoomRetentionEvidenceStore.RoomKey(room);
        var firstLeaf = Assert.Single(Directory.GetFiles(Path.Combine(BatonPaths.RoomRetentionEvidence, key), "*.json"));
        Assert.Equal(0, await preparer.PrepareAsync(BatonPaths.RoomRegistryFile, 14, TestContext.Current.CancellationToken));
        var journal = Path.Combine(room, BatonPaths.FlowLogFileName);
        var text = await File.ReadAllTextAsync(journal, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(journal, text.Replace("\":", "\" : ", StringComparison.Ordinal),
            TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(journal, DateTime.UtcNow.AddSeconds(2));
        Assert.Equal(1, await preparer.PrepareAsync(BatonPaths.RoomRegistryFile, 14, TestContext.Current.CancellationToken));
        var leaves = Directory.GetFiles(Path.Combine(BatonPaths.RoomRetentionEvidence, key), "*.json");
        Assert.Equal(2, leaves.Length);
        var newest = leaves.Single(path => path != firstLeaf);
        await File.WriteAllTextAsync(newest, "{}", TestContext.Current.CancellationToken);
        var restarted = new RoomRetentionEvidencePreparer();
        Assert.Equal(0, await restarted.PrepareAsync(BatonPaths.RoomRegistryFile, 14, TestContext.Current.CancellationToken));
        Assert.Equal("{}", await File.ReadAllTextAsync(newest, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Capture_UnsupportedIndeterminatePreledgerAndOversizedHistoriesRefuse()
    {
        using var home = new IsolatedBatonHome();
        var kept = await CreateTerminalRoomWithArtifactsAsync(home.Path, "kept", new ExecutionId("exec-kept-evidence"));
        await WriteRoomTerminalSentinelAsync(kept);
        await KeepMarker.MarkKeepAsync(kept, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<RoomRetentionEvidenceRefusalException>(() =>
            RoomRetentionEvidenceStore.CaptureAsync(kept, RoomRetentionEvidenceStore.RoomKey(kept),
                NewCaptureBudget(), TestContext.Current.CancellationToken));
        var conductor = await CreateTerminalRoomWithArtifactsAsync(home.Path, "conductor",
            new ExecutionId("exec-conductor-evidence"));
        await WriteRoomTerminalSentinelAsync(conductor);
        await Assert.ThrowsAsync<RoomRetentionEvidenceRefusalException>(() =>
            RoomRetentionEvidenceStore.CaptureAsync(conductor, RoomRetentionEvidenceStore.RoomKey(conductor),
                NewCaptureBudget(), TestContext.Current.CancellationToken));
        var preledger = Path.Combine(home.Path, "preledger");
        await WriteRoomTerminalSentinelAsync(preledger);
        await Assert.ThrowsAsync<RoomRetentionEvidenceRefusalException>(() =>
            RoomRetentionEvidenceStore.CaptureAsync(preledger, RoomRetentionEvidenceStore.RoomKey(preledger),
                NewCaptureBudget(), TestContext.Current.CancellationToken));

        var room = await CreateTerminalRoomWithArtifactsAsync(home.Path, "unsupported", new ExecutionId("exec-unsupported"));
        await WriteRoomTerminalSentinelAsync(room,
            new WorkflowStatusView(WorkflowOutcome.Indeterminate, [], [], null));
        var key = RoomRetentionEvidenceStore.RoomKey(room);
        await Assert.ThrowsAsync<RoomRetentionEvidenceRefusalException>(() =>
            RoomRetentionEvidenceStore.CaptureAsync(room, key, NewCaptureBudget(), TestContext.Current.CancellationToken));
        await WriteRoomTerminalSentinelAsync(room, new WorkflowStatusView(WorkflowOutcome.Failed, [], [], null));
        await Assert.ThrowsAsync<RoomRetentionEvidenceRefusalException>(() =>
            RoomRetentionEvidenceStore.CaptureAsync(room, key, NewCaptureBudget(), TestContext.Current.CancellationToken));
        await WriteRoomTerminalSentinelAsync(room);
        await Assert.ThrowsAsync<RoomRetentionEvidenceRefusalException>(() =>
            RoomRetentionEvidenceStore.CaptureAsync(room, key, NewCaptureBudget(),
                TestContext.Current.CancellationToken,
                selectionElapsed: TimeSpan.FromSeconds(3)));
        await Assert.ThrowsAsync<RoomRetentionEvidenceRefusalException>(() =>
            RoomRetentionEvidenceStore.CaptureAsync(room, key, NewCaptureBudget(),
                TestContext.Current.CancellationToken,
                selectionBytes: RoomRetentionEvidenceLimits.MaxSourceBytesPerRoom - 1));
        await using (var locked = new FileStream(Path.Combine(room, BatonPaths.FlowLogFileName),
            FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAsync<RoomRetentionEvidenceRefusalException>(() =>
                RoomRetentionEvidenceStore.CaptureAsync(room, key, NewCaptureBudget(), TestContext.Current.CancellationToken));
        }
        await File.AppendAllTextAsync(Path.Combine(room, BatonPaths.FlowLogFileName),
            new string('x', (int)RoomRetentionEvidenceLimits.MaxSourceBytesPerRoom + 1),
            TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<RoomRetentionEvidenceRefusalException>(() =>
            RoomRetentionEvidenceStore.CaptureAsync(room, key, NewCaptureBudget(), TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(Path.Combine(BatonPaths.RoomRetentionEvidence, key)));
    }

    [Fact]
    public async Task Discovery_BoundedSelectionExhaustion_AdvancesToLaterRoom()
    {
        using var home = new IsolatedBatonHome();
        var registry = BatonPaths.RoomRegistryFile;
        var lines = new System.Text.StringBuilder();
        var old = DateTime.UtcNow.AddDays(-15);
        var padding = new string('x', (int)RoomRetentionEvidenceLimits.MaxSelectionFileBytes - 200);
        var largeSentinel = "{\"state\":\"Succeeded\",\"steps\":[],\"outputs\":[],\"error\":null,\"padding\":\"" +
            padding + "\"}";
        var largeMetadata = "{\"padding\":\"" + padding + "\"}";
        string? ninthRoom = null;
        for (var index = 0; index < 12; index++)
        {
            var room = Path.Combine(home.Path, $"budget-room-{index:0000}");
            if (index == 8)
                ninthRoom = await CreateTerminalRoomWithArtifactsAsync(home.Path, $"budget-room-{index:0000}",
                    new ExecutionId("exec-budget-ninth"));
            else
                Directory.CreateDirectory(room);
            var sentinel = Path.Combine(room, TerminalSentinelWriter.TerminalSentinelFileName);
            await File.WriteAllTextAsync(sentinel, index < 8 ? largeSentinel :
                "{\"state\":\"Succeeded\",\"steps\":[],\"outputs\":[],\"error\":null}",
                TestContext.Current.CancellationToken);
            File.SetLastWriteTimeUtc(sentinel, old);
            if (index < 8)
                await File.WriteAllTextAsync(Path.Combine(room, BatonPaths.RoomMetadataFileName),
                    largeMetadata, TestContext.Current.CancellationToken);
            lines.AppendLine(JsonSerializer.Serialize(new Baton.Vendors.RoomRegistryEntry(room, home.Path, old)));
        }
        await File.WriteAllTextAsync(registry, lines.ToString(), TestContext.Current.CancellationToken);
        var preparer = new RoomRetentionEvidencePreparer();
        Assert.Equal(0, await preparer.PrepareAsync(registry, 14, TestContext.Current.CancellationToken));
        Assert.Equal(8, preparer.Cursor);
        Assert.Equal(1, await preparer.PrepareAsync(registry, 14, TestContext.Current.CancellationToken));
        Assert.Equal(4, preparer.Cursor);
        var key = RoomRetentionEvidenceStore.RoomKey(ninthRoom!);
        Assert.Single(Directory.GetFiles(Path.Combine(BatonPaths.RoomRetentionEvidence, key), "*.json"));
    }

    [Fact]
    public async Task Discovery_950LargeEligibleSentinels_AdvancesBeyondPrefixAcrossSweeps()
    {
        using var home = new IsolatedBatonHome();
        var registry = BatonPaths.RoomRegistryFile;
        var lines = new System.Text.StringBuilder();
        var old = DateTime.UtcNow.AddDays(-15);
        var largeSentinel = "{\"state\":\"Succeeded\",\"steps\":[],\"outputs\":[],\"error\":null,\"padding\":\"" +
            new string('x', 20 * 1024) + "\"}";
        string? ninthRoom = null;
        for (var index = 0; index < 950; index++)
        {
            var room = Path.Combine(home.Path, $"large-room-{index:0000}");
            if (index == 8)
            {
                ninthRoom = await CreateTerminalRoomWithArtifactsAsync(home.Path, $"large-room-{index:0000}",
                    new ExecutionId("exec-ninth"));
            }
            else
            {
                Directory.CreateDirectory(room);
            }
            var sentinel = Path.Combine(room, TerminalSentinelWriter.TerminalSentinelFileName);
            await File.WriteAllTextAsync(sentinel, largeSentinel, TestContext.Current.CancellationToken);
            File.SetLastWriteTimeUtc(sentinel, old);
            lines.AppendLine(JsonSerializer.Serialize(new Baton.Vendors.RoomRegistryEntry(room, home.Path, old)));
        }
        await File.WriteAllTextAsync(registry, lines.ToString(), TestContext.Current.CancellationToken);
        Assert.True(950L * 20 * 1024 > RoomRetentionEvidenceLimits.MaxSourceBytesPerSweep);
        var options = new RoomsPruneOptions(Terminal: true, OlderThanDays: 14, State: null, DryRun: true, Yes: false);
        var budget = NewCaptureBudget();
        var page = await RoomsPruneCommand.DiscoverRetentionCandidatesAsync(registry, options, budget,
            TestContext.Current.CancellationToken);
        Assert.Equal(8, page.Candidates.Count);
        Assert.Equal(8, page.NextCursor);
        Assert.All(page.Candidates, candidate =>
        {
            Assert.False(candidate.SelectionRefused);
            Assert.InRange(candidate.SelectionBytes, 1, RoomRetentionEvidenceLimits.MaxSourceBytesPerRoom);
        });
        Assert.InRange(RoomRetentionEvidenceLimits.MaxSourceBytesPerSweep - budget.Remaining, 1,
            RoomRetentionEvidenceLimits.MaxSourceBytesPerSweep);

        var preparer = new RoomRetentionEvidencePreparer();
        Assert.Equal(0, await preparer.PrepareAsync(registry, 14, TestContext.Current.CancellationToken));
        Assert.Equal(8, preparer.Cursor);
        Assert.Equal(1, await preparer.PrepareAsync(registry, 14, TestContext.Current.CancellationToken));
        Assert.Equal(16, preparer.Cursor);
        var key = RoomRetentionEvidenceStore.RoomKey(ninthRoom!);
        Assert.Single(Directory.GetFiles(Path.Combine(BatonPaths.RoomRetentionEvidence, key), "*.json"));
        Assert.True(Directory.Exists(ninthRoom));
    }

    [Fact]
    public async Task Discovery_950Rooms_MetersWholeSweep_AndAdvancesPastRefusals()
    {
        using var home = new IsolatedBatonHome();
        var registry = BatonPaths.RoomRegistryFile;
        var lines = new System.Text.StringBuilder();
        var old = DateTime.UtcNow.AddDays(-15);
        for (var index = 0; index < 950; index++)
        {
            var room = Path.Combine(home.Path, $"room-{index:0000}");
            Directory.CreateDirectory(room);
            var sentinel = Path.Combine(room, TerminalSentinelWriter.TerminalSentinelFileName);
            await File.WriteAllTextAsync(sentinel,
                index < 8 ? new string('x', (int)RoomRetentionEvidenceLimits.MaxSelectionFileBytes + 1) :
                "{\"state\":\"Succeeded\",\"steps\":[],\"outputs\":[],\"error\":null}",
                TestContext.Current.CancellationToken);
            File.SetLastWriteTimeUtc(sentinel, old);
            lines.AppendLine(JsonSerializer.Serialize(new Baton.Vendors.RoomRegistryEntry(room, home.Path, old)));
        }
        await File.WriteAllTextAsync(registry, lines.ToString(), TestContext.Current.CancellationToken);
        var options = new RoomsPruneOptions(Terminal: true, OlderThanDays: 14, State: null, DryRun: true, Yes: false);
        var budget = NewCaptureBudget();
        var page = await RoomsPruneCommand.DiscoverRetentionCandidatesAsync(registry, options, budget,
            TestContext.Current.CancellationToken);
        Assert.Equal(8, page.Candidates.Count);
        Assert.Equal(8, page.Candidates.Count(candidate => candidate.SelectionRefused));
        Assert.Equal(8, page.NextCursor);
        Assert.InRange(RoomRetentionEvidenceLimits.MaxSourceBytesPerSweep - budget.Remaining, 1,
            RoomRetentionEvidenceLimits.MaxSourceBytesPerSweep);

        var preparer = new RoomRetentionEvidencePreparer();
        Assert.Equal(0, await preparer.PrepareAsync(registry, 14, TestContext.Current.CancellationToken));
        Assert.Equal(8, preparer.Cursor);
        Assert.Equal(0, await preparer.PrepareAsync(registry, 14, TestContext.Current.CancellationToken));
        Assert.Equal(16, preparer.Cursor);
        var tooSmall = new RoomRetentionEvidenceStore.SweepBudget(
            System.Diagnostics.Stopwatch.GetTimestamp(), TestContext.Current.CancellationToken, bytes: 1024);
        await Assert.ThrowsAsync<RoomRetentionEvidenceRefusalException>(() =>
            RoomsPruneCommand.DiscoverRetentionCandidatesAsync(registry, options, tooSmall,
                TestContext.Current.CancellationToken));
        var expired = new RoomRetentionEvidenceStore.SweepBudget(
            System.Diagnostics.Stopwatch.GetTimestamp(), TestContext.Current.CancellationToken,
            deadline: TimeSpan.Zero);
        await Assert.ThrowsAsync<RoomRetentionEvidenceRefusalException>(() =>
            RoomsPruneCommand.DiscoverRetentionCandidatesAsync(registry, options, expired,
                TestContext.Current.CancellationToken));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancelledBudget = new RoomRetentionEvidenceStore.SweepBudget(
            System.Diagnostics.Stopwatch.GetTimestamp(), cancelled.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RoomsPruneCommand.DiscoverRetentionCandidatesAsync(registry, options, cancelledBudget, cancelled.Token));
    }

    [Fact]
    public async Task Discovery_TornRegistryAndMalformedSelectionNeverCertify()
    {
        using var home = new IsolatedBatonHome();
        var room = Path.Combine(home.Path, "bad-selection");
        Directory.CreateDirectory(room);
        var sentinel = Path.Combine(room, TerminalSentinelWriter.TerminalSentinelFileName);
        await File.WriteAllTextAsync(sentinel, "{bad}", TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(sentinel, DateTime.UtcNow.AddDays(-15));
        var registry = BatonPaths.RoomRegistryFile;
        Directory.CreateDirectory(Path.GetDirectoryName(registry)!);
        var line = JsonSerializer.Serialize(new Baton.Vendors.RoomRegistryEntry(room, home.Path, DateTime.UtcNow));
        await File.WriteAllTextAsync(registry, line, TestContext.Current.CancellationToken);
        var options = new RoomsPruneOptions(Terminal: true, OlderThanDays: 14, State: null, DryRun: true, Yes: false);
        await Assert.ThrowsAsync<RoomRetentionEvidenceRefusalException>(() =>
            RoomsPruneCommand.DiscoverRetentionCandidatesAsync(registry, options, NewCaptureBudget(),
                TestContext.Current.CancellationToken));
        await File.WriteAllTextAsync(registry, line + "\n", TestContext.Current.CancellationToken);
        var page = await RoomsPruneCommand.DiscoverRetentionCandidatesAsync(registry, options,
            NewCaptureBudget(), TestContext.Current.CancellationToken);
        Assert.True(Assert.Single(page.Candidates).SelectionRefused);
        Assert.False(Directory.Exists(Path.Combine(BatonPaths.RoomRetentionEvidence,
            RoomRetentionEvidenceStore.RoomKey(room))));
    }

    private static RoomRetentionEvidenceStore.SweepBudget NewCaptureBudget() =>
        new(System.Diagnostics.Stopwatch.GetTimestamp(), TestContext.Current.CancellationToken);

    private static async Task<string> WriteRoomTerminalSentinelAsync(string roomDir, WorkflowStatusView? view = null)
    {
        view ??= new Baton.Status.WorkflowStatusView(Baton.Status.WorkflowOutcome.Succeeded, [], [], null);
        await Baton.Status.TerminalSentinelWriter.WriteAsync(roomDir, view, TestContext.Current.CancellationToken);
        return Path.Combine(roomDir, Baton.Status.TerminalSentinelWriter.TerminalSentinelFileName);
    }
}
