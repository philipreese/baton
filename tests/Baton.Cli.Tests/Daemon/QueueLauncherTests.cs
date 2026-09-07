using System.Text.Json;
using Baton.Cli.Daemon;
using Baton.Cli.Mcp;
using Baton.Cli.Tests.TestSupport;
using Baton.Domain;
using Baton.Queue;
using Baton.Status;
using Baton.Tests.Shared;
using Baton.Store;
using Baton.Templates;
using Baton.Vendors;
using Xunit;

namespace Baton.Cli.Tests.Daemon;

/// <summary>
/// The launcher's post-launch settle path (#1939 review) —
/// <see cref="QueueLauncher.SettleFinishedPumpAsync"/>'s three pump outcomes and
/// <see cref="QueueLauncher.RecordPostLaunchFaultAsync"/>'s own arms, whose remarks say what each is
/// for. Before them, the item this lane belonged to stayed launched forever with only a daemon stderr
/// line to say otherwise — and, in round 2's finding, the record that did land erased what the lane
/// had actually done.
/// </summary>
public sealed class QueueLauncherTests : IDisposable
{
    private readonly IsolatedBatonHome _batonHome = new();

    public void Dispose() => _batonHome.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> NoOpAdapters =
        new Dictionary<string, IWorkerAdapter> { [NoOpWorkerAdapter.AdapterName] = new NoOpWorkerAdapter() };

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "baton_queue_launcher_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public async Task A_lane_that_faults_after_launch_leaves_the_room_a_failed_sentinel_the_scheduler_can_read()
    {
        var root = CreateTempRoot();
        try
        {
            // The pre-ledger control for the projecting arm below: a room with nothing to project still
            // gets its sentinel, because the write is what resolves the item.
            var room = Path.Combine(root, "queue-t1-abcd");
            Directory.CreateDirectory(room);

            await QueueLauncher.RecordPostLaunchFaultAsync("t1", room, "the pump threw BatonFlowException");

            var sentinel = await TerminalSentinelWriter.TryReadAsync(room, Ct);
            Assert.NotNull(sentinel);
            Assert.Equal(WorkflowOutcome.Failed, sentinel.State);
            Assert.Contains("did not complete after launch", sentinel.Error!, StringComparison.Ordinal);
            Assert.Contains("BatonFlowException", sentinel.Error, StringComparison.Ordinal);
            Assert.Empty(sentinel.Steps);

            // The whole point: the classifier the scheduler runs over this file now fails the item.
            Assert.Equal(QueueItemState.Failed, QueueSchedulerService.ClassifyTerminal(sentinel, room).State);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    /// <summary>
    /// Round 2's MEDIUM, at the property spec/baton.md §13's post-launch bullet states: this sentinel
    /// carries the room's own projected record, because <c>fleet_status</c> returns the file verbatim
    /// and re-projects nothing.
    /// </summary>
    [Fact]
    public async Task A_faulted_lane_with_a_real_ledger_keeps_the_steps_that_ran_and_the_outputs_they_produced()
    {
        var root = CreateTempRoot();
        try
        {
            var room = await RunTwoStepRoomAsync(root);

            // The pump's own run wrote no sentinel: TerminalSettleRecorder is what does, and this lane
            // is the one that never reached it.
            Assert.Null(await TerminalSentinelWriter.TryReadAsync(room, Ct));

            await QueueLauncher.RecordPostLaunchFaultAsync("t2", room, "the pump threw BatonFlowException");

            var sentinel = await TerminalSentinelWriter.TryReadAsync(room, Ct);
            Assert.NotNull(sentinel);

            // The fault is still the terminal word and the terminal reason...
            Assert.Equal(WorkflowOutcome.Failed, sentinel.State);
            Assert.Contains("did not complete after launch", sentinel.Error!, StringComparison.Ordinal);
            Assert.Equal(QueueItemState.Failed, QueueSchedulerService.ClassifyTerminal(sentinel, room).State);

            // ...and the room's own record survives underneath it, which is what the reader lost.
            Assert.Equal(["a", "b"], sentinel.Steps.Select(step => step.Id).Order().ToArray());
            Assert.All(sentinel.Steps, step => Assert.Equal(nameof(StepStatus.Succeeded), step.State));
            Assert.Contains(sentinel.Outputs, path => path.EndsWith("out_a", StringComparison.Ordinal));
            Assert.Contains(sentinel.Outputs, path => path.EndsWith("out_b", StringComparison.Ordinal));

            // Read back off the bytes, not just the parse: fleet_status's fast path returns this file.
            var onDisk = JsonSerializer.Deserialize<WorkflowStatusView>(
                await File.ReadAllTextAsync(Path.Combine(room, "terminal.json"), Ct));
            Assert.Equal(2, onDisk!.Steps.Count);
            Assert.Equal(2, onDisk.Outputs.Count);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    /// <summary>
    /// The shape the fault path actually meets — a step still in flight when the pump threw. Its
    /// recorded <c>Running</c> is kept and the projection's live <c>liveness</c> probe is dropped —
    /// see spec/baton.md §13 (the same bullet the arm above cites) for the argument separating them.
    /// </summary>
    [Fact]
    public async Task A_step_still_in_flight_keeps_its_recorded_state_but_freezes_no_liveness_claim()
    {
        var root = CreateTempRoot();
        try
        {
            var room = await CreateRunningRoomAsync(root);

            await QueueLauncher.RecordPostLaunchFaultAsync("t3", room, "the pump threw BatonFlowException");

            var sentinel = await TerminalSentinelWriter.TryReadAsync(room, Ct);
            Assert.NotNull(sentinel);
            Assert.Equal(WorkflowOutcome.Failed, sentinel.State);

            var step = Assert.Single(sentinel.Steps);
            Assert.Equal("step-a", step.Id);
            Assert.Equal(nameof(StepStatus.Running), step.State);
            Assert.Null(step.Liveness);

            // The consumer that a frozen Running step could otherwise have wedged: the scan
            // QueueSchedulerService.CountLiveWeightAsync walks skips a room the moment it carries a
            // sentinel, so a settled lane never holds the queue's concurrency cap open.
            Assert.Null(await FleetStatusTool.ProcessRoomAsync(room, includeTerminal: false, Ct));

            // The control for the assertion above: the same projection, unfrozen, DOES claim liveness
            // for this step — so the null is this code path dropping it, not the projector never
            // populating it.
            using var statusJson = new StringWriter();
            await StatusCommand.ExecuteAsync(new StatusOptions(room, Json: true), statusJson, Ct);
            var live = JsonSerializer.Deserialize<WorkflowStatusView>(statusJson.ToString());
            Assert.NotNull(Assert.Single(live!.Steps).Liveness);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public async Task A_fault_before_the_room_was_provisioned_manufactures_no_room()
    {
        var root = CreateTempRoot();
        try
        {
            // The discriminating half of the pair above — QueueLauncher.RecordPostLaunchFaultAsync's
            // own remarks have the argument for why nothing is written here.
            var room = Path.Combine(root, "queue-t1-never-made");

            await QueueLauncher.RecordPostLaunchFaultAsync("t1", room, "refused before provisioning");

            Assert.False(Directory.Exists(room));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public async Task A_room_that_already_recorded_its_own_verdict_keeps_it()
    {
        var root = CreateTempRoot();
        try
        {
            var room = Path.Combine(root, "queue-t1-refused");
            await TerminalSentinelWriter.WriteValidationRefusedAsync(
                room, "spec file 'x.md' does not exist.", Ct, tryInvocation: "pass an existing file to --spec");

            await QueueLauncher.RecordPostLaunchFaultAsync("t1", room, "some later throw");

            // Both fields of the dispatch's own record survive, which is the point of not replacing it.
            var sentinel = await TerminalSentinelWriter.TryReadAsync(room, Ct);
            Assert.Equal("spec file 'x.md' does not exist.", sentinel!.Error);
            Assert.Equal("pass an existing file to --spec", sentinel.Try);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    /// <summary>
    /// #1951, the arm the retry exists for: a ledger held only for a moment — by an exclusive opener
    /// from outside Baton, which is the only holder class a read open can lose to (see
    /// <see cref="QueueLauncher"/>'s <c>HeldLedgerRetryDelay</c> remark) — is projected once the holder
    /// lets go, rather than degrading to a bare sentinel that erases what the lane did. Its polarity arm
    /// is the never-released test below; alone, this one would pass with no retry at all if the release
    /// happened to land first, which is why the two are read together.
    /// <para>
    /// The hold is a Windows <see cref="FileShare"/> fact — see <see cref="FlowJournalHeldException"/>'s
    /// own doc for why it is not one on Unix — and this suite runs only there (#1405), the same reason
    /// <c>TerminalSentinelEndToEndTests</c>' held arm is unconditional.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_lane_whose_ledger_is_momentarily_held_is_projected_once_the_holder_releases()
    {
        var root = CreateTempRoot();
        try
        {
            var room = await RunTwoStepRoomAsync(root);
            var ledgerPath = Path.Combine(room, "flow.jsonl");
            var holder = new FileStream(ledgerPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            try
            {
                // The control that makes the assertions below about THIS hold: while the handle is open,
                // the read the projection makes is the one that fails, and fails as held.
                await Assert.ThrowsAsync<FlowJournalHeldException>(
                    () => new FlowEventLogReader(ledgerPath).ReadAllEntriesWithTimestampsAsync(Ct));

                var release = Task.Run(
                    async () =>
                    {
                        // This is not a wait for anything: it IS the holder's lifetime, the stimulus
                        // under test. The retry bound above (60 x 100ms) is what waits, and it is
                        // twenty-four times this.
                        // wait-ok: the hold's duration, not a wait on it — the 6s retry bound is the wait.
                        await Task.Delay(TimeSpan.FromMilliseconds(250), Ct);
                        await holder.DisposeAsync();
                    },
                    Ct);

                // A bound of its own rather than the production one: the release lands on the machine's
                // timing, so the wait has to outlast it by a margin no default needs to carry.
                await QueueLauncher.RecordPostLaunchFaultAsync(
                    "t6", room, "the pump threw BatonFlowException",
                    heldAttempts: 60, heldRetryDelay: TimeSpan.FromMilliseconds(100));
                await release;
            }
            finally
            {
                await holder.DisposeAsync();
            }

            var sentinel = await TerminalSentinelWriter.TryReadAsync(room, Ct);
            Assert.NotNull(sentinel);
            Assert.Equal(WorkflowOutcome.Failed, sentinel.State);

            // The room's own record survived the hold: this is the projected sentinel, not the bare one.
            Assert.Equal(["a", "b"], sentinel.Steps.Select(step => step.Id).Order().ToArray());
            Assert.DoesNotContain("carries no steps or outputs", sentinel.Error!, StringComparison.Ordinal);
            Assert.DoesNotContain("held open", sentinel.Error!, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    /// <summary>
    /// #1951's polarity arm: a ledger nobody releases — an exclusive holder that outlasts any bound this
    /// path could carry — degrades after the bound rather than retrying forever, and the bare sentinel
    /// says the hold is what produced it. Without this arm the test above proves nothing about the retry.
    /// </summary>
    [Fact]
    public async Task A_lane_whose_ledger_stays_held_degrades_to_a_bare_sentinel_that_names_the_hold()
    {
        var root = CreateTempRoot();
        try
        {
            var room = await RunTwoStepRoomAsync(root);
            using var holder = new FileStream(
                Path.Combine(room, "flow.jsonl"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            await QueueLauncher.RecordPostLaunchFaultAsync(
                "t7", room, "the pump threw BatonFlowException",
                heldAttempts: 3, heldRetryDelay: TimeSpan.FromMilliseconds(120));
            elapsed.Stop();

            // Three attempts means two pauses were actually taken — a single attempt would return in
            // milliseconds, so this is what says the held case was retried rather than degraded on sight.
            Assert.True(
                elapsed.Elapsed >= TimeSpan.FromMilliseconds(200),
                $"the held projection returned in {elapsed.ElapsedMilliseconds}ms, too fast to have retried");

            var sentinel = await TerminalSentinelWriter.TryReadAsync(room, Ct);
            Assert.NotNull(sentinel);
            Assert.Equal(WorkflowOutcome.Failed, sentinel.State);
            Assert.Empty(sentinel.Steps);

            // Which of the two degradations produced this record, on the record itself.
            Assert.Contains("held open by another process", sentinel.Error!, StringComparison.Ordinal);
            Assert.Contains("still held after 3 attempt", sentinel.Error!, StringComparison.Ordinal);

            // Degraded is still resolved: the item does not stay launched because the ledger was busy.
            Assert.Equal(QueueItemState.Failed, QueueSchedulerService.ClassifyTerminal(sentinel, room).State);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    /// <summary>
    /// The window the retry opens, closed (#1951, found in review): the "does this room already carry a
    /// verdict" guard used to sit next to the write with one synchronous projection between them, and
    /// the backoff put seconds there instead. Whatever else is running in the room can settle it during
    /// those seconds, and without a re-read this path overwrites that settle with a <c>Failed</c> one.
    /// This arm measures the in-loop re-read; the guard immediately before the write covers the exit
    /// iteration, which no arm here reaches — the projection fails in milliseconds while the hold lasts,
    /// so there is no window to write into without a seam this suite does not have.
    /// The static shape of the same rule is
    /// <see cref="A_room_that_already_recorded_its_own_verdict_keeps_it"/>; this is it mid-backoff.
    /// </summary>
    [Fact]
    public async Task A_verdict_that_lands_while_the_ledger_is_held_is_kept_rather_than_overwritten()
    {
        var root = CreateTempRoot();
        try
        {
            var room = await RunTwoStepRoomAsync(root);
            var holder = new FileStream(
                Path.Combine(room, "flow.jsonl"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            try
            {
                var settle = Task.Run(
                    async () =>
                    {
                        // wait-ok: the moment the racing writer lands, not a wait on one.
                        await Task.Delay(TimeSpan.FromMilliseconds(200), Ct);
                        await TerminalSentinelWriter.WriteValidationRefusedAsync(
                            room, "the engine's own verdict", Ct, tryInvocation: "read this room");
                        await holder.DisposeAsync();
                    },
                    Ct);

                await QueueLauncher.RecordPostLaunchFaultAsync(
                    "t9", room, "the pump threw BatonFlowException",
                    heldAttempts: 60, heldRetryDelay: TimeSpan.FromMilliseconds(100));
                await settle;
            }
            finally
            {
                await holder.DisposeAsync();
            }

            // The room's own record survives untouched — both fields, as in the static arm.
            var sentinel = await TerminalSentinelWriter.TryReadAsync(room, Ct);
            Assert.Equal("the engine's own verdict", sentinel!.Error);
            Assert.Equal("read this room", sentinel.Try);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    /// <summary>
    /// #1951's other half: a room whose snapshot is corrupt is not something waiting helps, so it
    /// degrades on the first read — and the reason reaches the file a reader actually gets, both the
    /// sentinel bytes <c>fleet_status</c> returns verbatim and the item error the queue records.
    /// </summary>
    [Fact]
    public async Task A_room_with_a_corrupt_snapshot_degrades_without_retrying_and_the_record_on_disk_says_why()
    {
        var root = CreateTempRoot();
        try
        {
            var room = await RunTwoStepRoomAsync(root);
            await File.WriteAllTextAsync(Path.Combine(room, "snapshot.json"), "{ this is not a snapshot", Ct);

            // A retry bound no corrupt room may pay: retried even once, this call would take five
            // seconds, and all three attempts would take fifteen.
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            await QueueLauncher.RecordPostLaunchFaultAsync(
                "t8", room, "the pump threw BatonFlowException",
                heldAttempts: 3, heldRetryDelay: TimeSpan.FromSeconds(5));
            elapsed.Stop();
            Assert.True(
                elapsed.Elapsed < TimeSpan.FromSeconds(4),
                $"the corrupt room took {elapsed.ElapsedMilliseconds}ms, so it was retried like a held one");

            var sentinel = await TerminalSentinelWriter.TryReadAsync(room, Ct);
            Assert.NotNull(sentinel);
            Assert.Equal(WorkflowOutcome.Failed, sentinel.State);
            Assert.Empty(sentinel.Steps);
            Assert.Contains("could not be read", sentinel.Error!, StringComparison.Ordinal);
            Assert.DoesNotContain("held open", sentinel.Error!, StringComparison.Ordinal);

            // Read back off the bytes, not just the parse: fleet_status's fast path returns this file.
            var onDisk = JsonSerializer.Deserialize<WorkflowStatusView>(
                await File.ReadAllTextAsync(Path.Combine(room, "terminal.json"), Ct));
            Assert.Contains("could not be read", onDisk!.Error!, StringComparison.Ordinal);

            // ...and it survives the classifier, so the reason lands on the queued item too.
            var classified = QueueSchedulerService.ClassifyTerminal(onDisk, room);
            Assert.Equal(QueueItemState.Failed, classified.State);
            Assert.Contains("could not be read", classified.Error!, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    /// <summary>
    /// #1951 review's LOW: the held predicate covers <c>snapshot.json</c> too. A hold on it used to be
    /// reported as unreadable and degraded on sight — a room a single retry would have projected,
    /// recorded as one nothing could help; <c>QueueLauncher</c>'s own catch arm has why it arrived that
    /// way. Same stimulus as the ledger arm above, on the other file.
    /// </summary>
    [Fact]
    public async Task A_lane_whose_snapshot_is_momentarily_held_is_projected_once_the_holder_releases()
    {
        var root = CreateTempRoot();
        try
        {
            var room = await RunTwoStepRoomAsync(root);
            var snapshotPath = Path.Combine(room, "snapshot.json");
            var holder = new FileStream(snapshotPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            try
            {
                // The control: while this handle is open, the snapshot read IS the one that fails, and
                // fails as a sharing violation rather than as anything the loader translates.
                var blocked = await Assert.ThrowsAnyAsync<IOException>(
                    () => SnapshotBinder.LoadFromFileAsync(snapshotPath, Ct));
                Assert.True(
                    FileHolderProbe.IsSharingViolation(blocked),
                    $"the hold produced {blocked.GetType().Name} (HResult 0x{blocked.HResult:x8}), not a sharing violation");

                var release = Task.Run(
                    async () =>
                    {
                        // wait-ok: the hold's own duration, not a wait on it — the 6s retry bound waits.
                        await Task.Delay(TimeSpan.FromMilliseconds(250), Ct);
                        await holder.DisposeAsync();
                    },
                    Ct);

                await QueueLauncher.RecordPostLaunchFaultAsync(
                    "t10", room, "the pump threw BatonFlowException",
                    heldAttempts: 60, heldRetryDelay: TimeSpan.FromMilliseconds(100));
                await release;
            }
            finally
            {
                await holder.DisposeAsync();
            }

            var sentinel = await TerminalSentinelWriter.TryReadAsync(room, Ct);
            Assert.NotNull(sentinel);
            Assert.Equal(WorkflowOutcome.Failed, sentinel.State);

            // Projected, not degraded: the hold was waited out rather than called unreadable.
            Assert.Equal(["a", "b"], sentinel.Steps.Select(step => step.Id).Order().ToArray());
            Assert.DoesNotContain("could not be read", sentinel.Error!, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    /// <summary>
    /// #1951 review's ambiguous pair, both answered on the record rather than left to a reader: a
    /// zero-length <c>flow.jsonl</c> and one that is gone are both MISSING, never corrupt, and neither
    /// is retried. Zero-length is <see cref="RoomLedgerProbe"/>'s documented refusal shape — the writer
    /// creates the file on open, before the lock can throw — so "corrupt" would be a false accusation
    /// against a room where nothing was ever recorded. The <c>could not be read</c> polarity assertion
    /// is what separates the two vocabularies; the corrupt-snapshot arm above holds the other pole.
    /// Two stimuli, one branch on purpose: <see cref="RoomLedgerProbe.HasLedger"/> answers false for
    /// both, and this asserts they stay one answer rather than drifting into two vocabularies later.
    /// <para>
    /// The narrower race the same catch covers — a ledger vanishing between <c>FlowEventLogReader</c>'s
    /// own <c>File.Exists</c> and its open — is microseconds wide and not steerable from outside, so it
    /// is asserted by construction (the arm returns the same answer) and not measured here.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_ledger_that_is_empty_or_gone_is_recorded_as_missing_rather_than_unreadable(bool zeroLength)
    {
        var root = CreateTempRoot();
        try
        {
            var room = await RunTwoStepRoomAsync(root);
            var ledgerPath = Path.Combine(room, "flow.jsonl");
            if (zeroLength)
            {
                await File.WriteAllTextAsync(ledgerPath, string.Empty, Ct);
            }
            else
            {
                FileCleanup.EnsureDeleted(ledgerPath);
            }

            // A retry bound no absent room may pay, the same discriminator the corrupt arm uses.
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            await QueueLauncher.RecordPostLaunchFaultAsync(
                "t11", room, "the pump threw BatonFlowException",
                heldAttempts: 3, heldRetryDelay: TimeSpan.FromSeconds(5));
            elapsed.Stop();
            Assert.True(
                elapsed.Elapsed < TimeSpan.FromSeconds(4),
                $"the ledger-less room took {elapsed.ElapsedMilliseconds}ms, so it was retried like a held one");

            var sentinel = await TerminalSentinelWriter.TryReadAsync(room, Ct);
            Assert.NotNull(sentinel);
            Assert.Empty(sentinel.Steps);

            // Which file, and in which vocabulary: missing, not damaged, and not held.
            Assert.Contains("carries no ledger of its own", sentinel.Error!, StringComparison.Ordinal);
            Assert.DoesNotContain("could not be read", sentinel.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("held open", sentinel.Error, StringComparison.Ordinal);
            Assert.Equal(QueueItemState.Failed, QueueSchedulerService.ClassifyTerminal(sentinel, room).State);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    /// <summary>
    /// The other half of the split the reason text used to collapse (#1951 review): a room that HAS a
    /// ledger and lost its snapshot ran and lost its binding, where the arm above never started — and
    /// one disjunctive sentence left an operator walking the directory by hand to tell them apart.
    /// </summary>
    [Fact]
    public async Task A_room_whose_snapshot_is_gone_says_so_rather_than_naming_the_ledger()
    {
        var root = CreateTempRoot();
        try
        {
            var room = await RunTwoStepRoomAsync(root);
            FileCleanup.EnsureDeleted(Path.Combine(room, "snapshot.json"));

            await QueueLauncher.RecordPostLaunchFaultAsync("t12", room, "the pump threw BatonFlowException");

            var sentinel = await TerminalSentinelWriter.TryReadAsync(room, Ct);
            Assert.NotNull(sentinel);
            Assert.Contains("no bound snapshot", sentinel.Error!, StringComparison.Ordinal);
            Assert.DoesNotContain("carries no ledger of its own", sentinel.Error, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    /// <summary>
    /// Round 2's LOW: a pump whose <see cref="OperationCanceledException"/> escapes ends
    /// <see cref="TaskStatus.Canceled"/>, not faulted. The old <c>IsFaulted</c>-only gate sent it to the
    /// settle branch, where <c>completed.Result</c> throws unobserved and the item wedges in
    /// <see cref="QueueItemState.Launched"/>.
    /// </summary>
    [Fact]
    public async Task A_cancelled_pump_settles_the_room_rather_than_wedging_the_item_in_launched()
    {
        var root = CreateTempRoot();
        try
        {
            var room = Path.Combine(root, "queue-t4-cancelled");
            Directory.CreateDirectory(room);

            await QueueLauncher.SettleFinishedPumpAsync(
                Task.FromCanceled<CommandResult>(new CancellationToken(canceled: true)), "t4", room);

            var sentinel = await TerminalSentinelWriter.TryReadAsync(room, Ct);
            Assert.NotNull(sentinel);
            Assert.Equal(WorkflowOutcome.Failed, sentinel.State);
            Assert.Contains("cancelled after launch", sentinel.Error!, StringComparison.Ordinal);
            Assert.Equal(QueueItemState.Failed, QueueSchedulerService.ClassifyTerminal(sentinel, room).State);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    /// <summary>
    /// The polarity arm of the two above: a pump that completed is settled by
    /// <see cref="TerminalSettleRecorder"/> on its own terms, and this path fabricates nothing over it.
    /// A non-terminal result records nothing at all, so the absence here is that recorder's own contract
    /// rather than a fault sentinel that failed to write.
    /// </summary>
    [Fact]
    public async Task A_pump_that_completed_gets_no_fault_sentinel()
    {
        var root = CreateTempRoot();
        try
        {
            var room = Path.Combine(root, "queue-t5-completed");
            Directory.CreateDirectory(room);

            var snapshotId = new WorkflowDefinitionSnapshotId("done");
            var result = new CommandResult(
                new FlowState(snapshotId, [], WorkflowStatus.Running),
                new WorkflowDefinitionSnapshot(snapshotId, new WorkflowTemplateId("done"), 1, []),
                RoomDirectoryPath: room);

            await QueueLauncher.SettleFinishedPumpAsync(Task.FromResult(result), "t5", room);

            Assert.Null(await TerminalSentinelWriter.TryReadAsync(room, Ct));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    /// <summary>
    /// A room carried to Terminal through the real pump: two succeeded steps, each with a declared
    /// output, and the snapshot/ledger pair a projection needs. No terminal sentinel — writing that is
    /// <see cref="TerminalSettleRecorder"/>'s job, and the lane under test is the one that never reaches it.
    /// </summary>
    private static async Task<string> RunTwoStepRoomAsync(string root)
    {
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("queue-two-step"), 1,
            [
                new WorkflowStepDefinition(new StepId("a"), "a", [], ["out_a"], [], new RetryPolicy(1)),
                new WorkflowStepDefinition(new StepId("b"), "b", [], ["out_b"], [new StepId("a")], new RetryPolicy(1)),
            ]);
        var workflowPath = Path.Combine(root, "workflow.json");
        await File.WriteAllTextAsync(workflowPath, JsonSerializer.Serialize(definition), Ct);

        var bindings = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["a"] = new WorkerBindingConfigEntry(
                NoOpWorkerAdapter.AdapterName, new WorkerContract("a", [], [new ProducedOutput("out_a")], []),
                PromptTemplate: "unused-by-noop", TimeSpan.FromSeconds(30)),
            ["b"] = new WorkerBindingConfigEntry(
                NoOpWorkerAdapter.AdapterName, new WorkerContract("b", [], [new ProducedOutput("out_b")], []),
                PromptTemplate: "unused-by-noop", TimeSpan.FromSeconds(30)),
        };
        var bindingsPath = Path.Combine(root, "bindings.json");
        await File.WriteAllTextAsync(bindingsPath, JsonSerializer.Serialize(bindings), Ct);

        var room = Path.Combine(root, "queue-t2-ledgered");
        var result = await RunCommand.ExecuteAsync(
            new RunOptions(workflowPath, bindingsPath, room), NoOpAdapters, cancellationToken: Ct);

        Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
        return room;
    }

    /// <summary>
    /// A room with one step still Running under a live engine identity — the in-flight shape a lane that
    /// throws twenty minutes in leaves behind. Built from the events directly (rather than through a
    /// pump) because a pump that stops mid-step is exactly what a test cannot hold still; the same
    /// fixture shape <c>FleetProjectionWriterTests.CreateRunningRoomAsync</c> uses.
    /// </summary>
    private static async Task<string> CreateRunningRoomAsync(string root)
    {
        var room = Path.Combine(root, "queue-t3-running");
        Directory.CreateDirectory(room);

        var stepDef = new WorkflowStepDefinition(new StepId("step-a"), "architect", [], [], [], new RetryPolicy(1));
        var snapshot = SnapshotBinder.Bind(new WorkflowDefinition(new WorkflowTemplateId("wf"), 1, [stepDef]));
        await SnapshotBinder.PersistAsync(snapshot, Path.Combine(room, "snapshot.json"), Ct);

        var request = new ExecutionRequest(
            new ExecutionId("exec-running"), new WorkflowId("wf"), stepDef.StepId, stepDef.Worker,
            [], [], TimeSpan.FromMinutes(5), [], new Dictionary<StepId, ExecutionId>(), Adapter: NoOpWorkerAdapter.AdapterName);

        var self = System.Diagnostics.Process.GetCurrentProcess();
        var logWriter = new FlowEventLogWriter(Path.Combine(room, "flow.jsonl"));
        await logWriter.AppendAsync(
            new FlowEvent.ExecutionRequestAccepted(
                request,
                EnginePid: Environment.ProcessId,
                EngineStartTime: new DateTimeOffset(self.StartTime).ToUniversalTime()),
            Ct);
        await logWriter.DisposeAsync();

        return room;
    }
}
