using Baton.Domain;
using Baton.Mutation;
using Baton.Status;
using Baton.Store;

namespace Baton.Cli.Tests;

/// <summary>
/// #1495 / PR #1528: unit-level tests against <see cref="CancelRequestPoller.TickAsync"/> and
/// <see cref="CancelRequestPoller.RunAsync"/>. Covers successful delivery, the false-but-settled
/// consume path, the false-but-still-running retry and deferred delivery path, retry exhaustion
/// rejecting with the #1530 reason, poller reject branches for <c>latest</c> with reason in body,
/// and <see cref="CancelRequestPoller.RunAsync"/>'s own resilience contract.
/// </summary>
[Collection(ConsoleErrorCaptureCollection.Name)]
public class CancelRequestPollerTests
{
    private static readonly WorkflowDefinitionSnapshot Snapshot = new(
        new WorkflowDefinitionSnapshotId("snapshot-1"),
        new WorkflowTemplateId("poller-test"),
        WorkflowTemplateVersion: 1,
        Steps: [new WorkflowStepDefinition(new StepId("a"), "a", [], ["out"], [], new RetryPolicy(1))]);

    private static readonly WorkflowDefinitionSnapshot TwoStepSnapshot = new(
        new WorkflowDefinitionSnapshotId("snapshot-2"),
        new WorkflowTemplateId("poller-test-2"),
        WorkflowTemplateVersion: 1,
        Steps:
        [
            new WorkflowStepDefinition(new StepId("a"), "a", [], ["out_a"], [], new RetryPolicy(1)),
            new WorkflowStepDefinition(new StepId("b"), "b", [], ["out_b"], [], new RetryPolicy(1)),
        ]);

    private static ExecutionRequest MakeRequest(ExecutionId executionId, StepId stepId)
        => new(
            executionId,
            new WorkflowId("poller-test"),
            stepId,
            "worker",
            Inputs: [],
            Outputs: [],
            Timeout: TimeSpan.FromMinutes(10),
            Environment: [],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

    private static ExecutionRequest MakeStepLessRequest(ExecutionId executionId)
        => new(
            executionId,
            new WorkflowId("poller-test"),
            StepId: null,
            "worker",
            Inputs: [],
            Outputs: [],
            Timeout: null,
            Environment: [],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

    [Fact]
    public async Task Successful_delivery_when_registry_holds_target_delivers_and_consumes()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"cancel-request-poller-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDirectory);
        try
        {
            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            await using (var writer = new FlowEventLogWriter(logPath))
            {
                var execId = new ExecutionId("exec-1");
                await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(MakeRequest(execId, new StepId("a"))), TestContext.Current.CancellationToken);

                var registry = new InFlightExecutionRegistry();
                registry.Bind(writer);
                var token = registry.Register(execId);

                await CancelRequestFile.WriteAsync(roomDirectory, "exec-1", TestContext.Current.CancellationToken);

                await CancelRequestPoller.TickAsync(
                    roomDirectory, logPath, Snapshot, registry, TestContext.Current.CancellationToken);

                var requestPath = CancelRequestFile.GetPath(roomDirectory);
                Assert.False(File.Exists(requestPath), "expected the request to be consumed");
                Assert.True(File.Exists($"{requestPath}.consumed"), "expected .consumed sibling to exist");
                Assert.True(token.IsCancellationRequested, "expected registry to signal cancellation");
            }
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task A_request_naming_an_execution_not_currently_registered_and_not_running_is_consumed_as_a_too_late_no_op()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"cancel-request-poller-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDirectory);
        try
        {
            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            var execId = new ExecutionId("exec-settled");
            // Kept open for the whole test (not disposed before TickAsync) so the registry bound to it
            // below can actually append the durable rejection the assertions at the bottom pin.
            await using var writer = new FlowEventLogWriter(logPath);
            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(MakeRequest(execId, new StepId("a"))), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new FlowEvent.ExecutionSucceeded(execId), TestContext.Current.CancellationToken);

            await CancelRequestFile.WriteAsync(roomDirectory, "exec-settled", TestContext.Current.CancellationToken);

            // Not in flight, but also no longer projecting Running -> genuinely settled. Bound so the
            // #1916 fix round 2 durable CancellationRejected append below is reachable -- an unbound
            // registry would silently no-op it (InFlightExecutionRegistry.RecordCancellationRejectedAsync).
            var registry = new InFlightExecutionRegistry();
            registry.Bind(writer);

            await CancelRequestPoller.TickAsync(
                roomDirectory, logPath, Snapshot, registry, TestContext.Current.CancellationToken);

            var requestPath = CancelRequestFile.GetPath(roomDirectory);
            Assert.False(File.Exists(requestPath), "expected the request to be consumed, not left pending");
            Assert.True(File.Exists($"{requestPath}.consumed"));

            var reader = new FlowEventLogReader(logPath);
            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var cancellationRejected = Assert.Single(events.OfType<FlowEvent.CancellationRejected>());
            Assert.Equal(execId, cancellationRejected.ExecutionId);
            Assert.Contains("too late (it already settled)", cancellationRejected.Reason, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    // #1916 fix round 2 / polarity control for the test above: this target was never accepted by
    // this room AT ALL (a typo'd or stale literal id) -- "too late (it already settled)" is a false
    // claim for it, since no real execution ever existed to settle. See TickAsync's own remarks on
    // its everAccepted check for why this lands on ArrestRequestUnresolvable instead.
    [Fact]
    public async Task A_request_naming_an_execution_id_never_accepted_by_this_room_is_unresolvable_not_too_late()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"cancel-request-poller-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDirectory);
        var originalError = Console.Error;
        try
        {
            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            var roomLogPath = Path.Combine(roomDirectory, "room.jsonl");
            await File.WriteAllTextAsync(logPath, string.Empty, TestContext.Current.CancellationToken);

            await CancelRequestFile.WriteAsync(roomDirectory, "never-accepted", TestContext.Current.CancellationToken);

            var registry = new InFlightExecutionRegistry();

            using var stderr = new StringWriter();
            Console.SetError(stderr);

            await CancelRequestPoller.TickAsync(
                roomDirectory, logPath, Snapshot, registry, TestContext.Current.CancellationToken, roomLogPath);

            var requestPath = CancelRequestFile.GetPath(roomDirectory);
            Assert.False(File.Exists(requestPath), "expected the request to be consumed, not left pending");
            Assert.True(File.Exists($"{requestPath}.consumed"));
            Assert.DoesNotContain("too late", stderr.ToString(), StringComparison.Ordinal);
            Assert.Contains("no accepted request named this execution", stderr.ToString(), StringComparison.Ordinal);

            var reader = new FlowEventLogReader(logPath);
            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain(events, e => e is FlowEvent.CancellationRejected);

            var roomReader = new RoomEventLogReader(roomLogPath);
            var roomEvents = await roomReader.ReadAllRoomEventsAsync(TestContext.Current.CancellationToken);
            var unresolvable = Assert.Single(roomEvents.OfType<RoomEvent.ArrestRequestUnresolvable>());
            Assert.Equal("never-accepted", unresolvable.Target);
            Assert.Contains("no accepted request named this execution", unresolvable.Reason, StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(originalError);
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    // D2 (#1530 seam design, #1556 PR 1 fix): a step-less supplementary execution has no StepState of
    // its own, so the pre-collapse settle re-check — which read FlowState.Steps only — could never
    // find it and always fell through to "too late (it already settled)", even while the execution
    // was still pending. ArrestableExecutions.Find now sees it via FlowState.StepLessExecutions. Its
    // control is the pre-existing settled-and-registered-nowhere test above (both report the target
    // as not delivered; only a step-less-but-still-pending one must be told "still pending", not
    // "too late" — one condition apart, per the v-and-v polarity requirement).
    [Fact]
    public async Task A_step_less_execution_still_pending_is_left_pending_not_declared_too_late()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"cancel-request-poller-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDirectory);
        var originalError = Console.Error;
        try
        {
            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            var execId = new ExecutionId("exec-step-less");

            await using (var writer = new FlowEventLogWriter(logPath))
            {
                await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(MakeStepLessRequest(execId)), TestContext.Current.CancellationToken);
            }

            await CancelRequestFile.WriteAsync(roomDirectory, "exec-step-less", TestContext.Current.CancellationToken);

            // Not in the registry (no live process behind a step-less execution ever registers) —
            // delivery fails, so the settle re-check is what decides whether this is too-late or
            // still-pending.
            var registry = new InFlightExecutionRegistry();

            using var stderr = new StringWriter();
            Console.SetError(stderr);

            await CancelRequestPoller.TickAsync(
                roomDirectory, logPath, Snapshot, registry, TestContext.Current.CancellationToken);

            var requestPath = CancelRequestFile.GetPath(roomDirectory);
            Assert.True(File.Exists(requestPath), "request must remain pending — the target has not settled");
            Assert.False(File.Exists($"{requestPath}.consumed"));
            Assert.False(File.Exists($"{requestPath}.rejected"));
            Assert.DoesNotContain("too late", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(originalError);
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    // Polarity control for the test above, one condition apart: the step-less target has ALREADY been
    // cancelled by the time this tick re-checks (the seam settled it a moment before, not "despite"
    // this request). Pre-fix this read Steps-only and always reported "too late" here, because a
    // step-less execution has no StepState to read Cancelled off — the exact false claim #802's F7
    // finding named, now reachable because #1556 PR 2 lets the poller mark a step-less target at all.
    [Fact]
    public async Task A_step_less_execution_the_seam_just_cancelled_is_reported_arrested_not_too_late()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"cancel-request-poller-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDirectory);
        var originalError = Console.Error;
        try
        {
            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            var execId = new ExecutionId("exec-step-less-cancelled");

            await using (var writer = new FlowEventLogWriter(logPath))
            {
                await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(MakeStepLessRequest(execId)), TestContext.Current.CancellationToken);
                await writer.AppendAsync(new FlowEvent.CancellationRequested(execId, CancellationOrigin.Operator), TestContext.Current.CancellationToken);
                await writer.AppendAsync(new FlowEvent.ExecutionCancelled(execId), TestContext.Current.CancellationToken);
            }

            await CancelRequestFile.WriteAsync(roomDirectory, "exec-step-less-cancelled", TestContext.Current.CancellationToken);

            var registry = new InFlightExecutionRegistry();

            using var stderr = new StringWriter();
            Console.SetError(stderr);

            await CancelRequestPoller.TickAsync(
                roomDirectory, logPath, Snapshot, registry, TestContext.Current.CancellationToken);

            var requestPath = CancelRequestFile.GetPath(roomDirectory);
            Assert.False(File.Exists(requestPath), "a settled target consumes the request file");
            Assert.True(File.Exists($"{requestPath}.consumed"));
            Assert.Contains("arrested by this request", stderr.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("too late", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(originalError);
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task False_but_still_running_execution_is_left_pending_then_delivered_on_later_tick_after_registration()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"cancel-request-poller-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDirectory);
        try
        {
            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            var execId = new ExecutionId("exec-racing");

            await using var writer = new FlowEventLogWriter(logPath);
            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(MakeRequest(execId, new StepId("a"))), TestContext.Current.CancellationToken);

            await CancelRequestFile.WriteAsync(roomDirectory, "exec-racing", TestContext.Current.CancellationToken);

            var registry = new InFlightExecutionRegistry();
            registry.Bind(writer);

            var requestPath = CancelRequestFile.GetPath(roomDirectory);

            // Tick 1: Not yet registered in registry, but STILL Running in projection -> left pending.
            await CancelRequestPoller.TickAsync(
                roomDirectory, logPath, Snapshot, registry, TestContext.Current.CancellationToken);

            Assert.True(File.Exists(requestPath), "request must remain pending while target still projects Running");
            Assert.False(File.Exists($"{requestPath}.consumed"));
            Assert.False(File.Exists($"{requestPath}.rejected"));

            // Register the execution now (simulating registration closing the race gap).
            var token = registry.Register(execId);

            // Tick 2: Now registered -> delivered and consumed!
            await CancelRequestPoller.TickAsync(
                roomDirectory, logPath, Snapshot, registry, TestContext.Current.CancellationToken);

            Assert.False(File.Exists(requestPath), "request must be consumed on successful retry");
            Assert.True(File.Exists($"{requestPath}.consumed"));
            Assert.True(token.IsCancellationRequested);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task Retry_exhaustion_after_5_still_running_ticks_rejects_with_reason_in_body()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"cancel-request-poller-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDirectory);
        try
        {
            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            var execId = new ExecutionId("exec-non-process");

            await using var writer = new FlowEventLogWriter(logPath);
            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(MakeRequest(execId, new StepId("a"))), TestContext.Current.CancellationToken);

            await CancelRequestFile.WriteAsync(roomDirectory, "exec-non-process", TestContext.Current.CancellationToken);

            var registry = new InFlightExecutionRegistry();
            registry.Bind(writer);

            var requestPath = CancelRequestFile.GetPath(roomDirectory);

            // Ticks 1 to 4: Left pending.
            for (var i = 1; i <= 4; i++)
            {
                await CancelRequestPoller.TickAsync(
                    roomDirectory, logPath, Snapshot, registry, TestContext.Current.CancellationToken);
                Assert.True(File.Exists(requestPath), $"request must remain pending on tick {i}");
            }

            // Tick 5: Reaches 5th still-running tick -> rejected!
            await CancelRequestPoller.TickAsync(
                roomDirectory, logPath, Snapshot, registry, TestContext.Current.CancellationToken);

            Assert.False(File.Exists(requestPath), "request must not remain pending after 5 retries");
            var rejectedPath = $"{requestPath}.rejected";
            Assert.True(File.Exists(rejectedPath), "expected .rejected sibling to exist");

            var rejected = await CancelRequestFile.TryReadRejectedAsync(rejectedPath, TestContext.Current.CancellationToken);
            Assert.NotNull(rejected);
            Assert.Equal("exec-non-process", rejected.Target);
            Assert.Contains("arrest requested (#1556) but not yet confirmed settled after 5 polls", rejected.Reason);

            // #2045: the file channel gives up (above); the journal records NOTHING, because every
            // target that reaches this ceiling was marked on the registry and the mark is a delivery
            // guarantee (#1825) the pump can still honour. This assertion previously read
            // Assert.Single(...CancellationRejected) with the same reason -- see
            // A_marked_target_the_pump_settles_after_the_ceiling_leaves_no_rejection_in_the_ledger
            // for the operator-visible defect that pairing produced, and
            // A_request_naming_an_execution_not_currently_registered_and_not_running_is_consumed_as_a_too_late_no_op
            // for the polarity control: a target ArrestableExecutions.Find no longer admits DOES
            // still journal a CancellationRejected, and that test asserts exactly that.
            var reader = new FlowEventLogReader(logPath);
            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain(events, e => e is FlowEvent.CancellationRejected);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>
    /// #2045: the operator-visible half of the assertion above, read off the ledger an operator
    /// actually sees (<c>baton status</c>'s <c>Arrests:</c> block / <c>--json arrests</c>) rather than
    /// off the raw journal. The sequence is the one #1825 made reachable: the poller marks a target
    /// <see cref="Baton.Projection.ArrestableExecutions.Find"/> still admits, the ceiling fires five
    /// ticks later, and the pump then honours the SAME mark. Because a rejection reuses an already-open
    /// builder rather than opening its own (<see cref="Baton.Status.ArrestLedgerProjector.Project"/>),
    /// journalling one at the ceiling produced a single entry reading <c>Delivered</c> while still
    /// carrying the ceiling's rejection <c>Reason</c> — a shape
    /// <see cref="Baton.Status.ArrestLedgerEntry"/>'s own <c>Reason</c> doc ("populated only for
    /// Rejected") says cannot exist, and one an operator reads as "the arrest was refused, and also
    /// it happened".
    /// </summary>
    [Fact]
    public async Task A_marked_target_the_pump_settles_after_the_ceiling_leaves_no_rejection_in_the_ledger()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"cancel-request-poller-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDirectory);
        try
        {
            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            var execId = new ExecutionId("exec-marked-then-settled");

            await using (var writer = new FlowEventLogWriter(logPath))
            {
                await writer.AppendAsync(
                    new FlowEvent.ExecutionRequestAccepted(MakeRequest(execId, new StepId("a"))), TestContext.Current.CancellationToken);

                await CancelRequestFile.WriteAsync(roomDirectory, execId.Value, TestContext.Current.CancellationToken);

                // BOUND, unlike the parked-mark tests: an unbound registry no-ops
                // RecordCancellationRejectedAsync outright, which would let this test pass against the
                // pre-#2045 code for a reason that has nothing to do with the property under test.
                var registry = new InFlightExecutionRegistry();
                registry.Bind(writer);

                for (var tick = 1; tick <= 5; tick++)
                {
                    await CancelRequestPoller.TickAsync(
                        roomDirectory, logPath, Snapshot, registry, TestContext.Current.CancellationToken);
                }

                // The mark the whole property rests on: the ceiling fired against a target the
                // registry took, so the pump still owes this request a delivery.
                Assert.Contains(execId, registry.DrainArrestIntents().Select(intent => intent.ExecutionId));

                // The pump wakes on that mark and settles it — SettleArrestIntentsAsync's own
                // CancellationRequested append, then the round's derived obligation finalizing it.
                // Written here directly rather than by driving MutationInterface: this test is about
                // what the ledger renders for the pair, not about the settle path itself.
                await writer.AppendAsync(
                    new FlowEvent.CancellationRequested(execId, CancellationOrigin.Operator), TestContext.Current.CancellationToken);
                await writer.AppendAsync(new FlowEvent.ExecutionCancelled(execId), TestContext.Current.CancellationToken);
            }

            var entries = await new FlowEventLogReader(logPath)
                .ReadAllEntriesWithTimestampsAsync(TestContext.Current.CancellationToken);
            var ledger = ArrestLedgerProjector.Project(entries, []);

            var entry = Assert.Single(ledger);
            Assert.Equal(execId, entry.ExecutionId);
            Assert.Equal(ArrestOutcome.Delivered, entry.Outcome);
            Assert.Null(entry.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>
    /// #2045 fix round: the polarity twin of
    /// <see cref="A_marked_target_the_pump_settles_after_the_ceiling_leaves_no_rejection_in_the_ledger"/>
    /// — that one is the arm where the pump DOES honour the mark; this is the arm where it never can,
    /// the unregistered-target case <c>CancelRequestPoller.TickAsync</c>'s remarks at the ceiling name
    /// and explain (the drain records nothing and clears the intent map; the settled request file is
    /// gone, so the next tick cannot re-mark). What the operator is left with is what this test pins:
    /// nothing in the ledger at all, from either log.
    /// </summary>
    [Fact]
    public async Task A_marked_target_the_pump_never_settles_is_abandoned_with_no_ledger_entry()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"cancel-request-poller-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDirectory);
        var originalError = Console.Error;
        try
        {
            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            var roomLogPath = Path.Combine(roomDirectory, "room.jsonl");
            var execId = new ExecutionId("exec-never-registers");
            var requestPath = CancelRequestFile.GetPath(roomDirectory);
            using var stderr = new StringWriter();
            Console.SetError(stderr);

            await using (var writer = new FlowEventLogWriter(logPath))
            {
                await writer.AppendAsync(
                    new FlowEvent.ExecutionRequestAccepted(MakeRequest(execId, new StepId("a"))), TestContext.Current.CancellationToken);

                await CancelRequestFile.WriteAsync(roomDirectory, execId.Value, TestContext.Current.CancellationToken);

                // BOUND, for the same reason the twin above is: an unbound registry no-ops
                // RecordCancellationRejectedAsync, so an "empty ledger" assertion would hold against a
                // ceiling that still journalled, for a reason unrelated to the property under test.
                // roomLogPath is passed for the mirror-image reason — a fix that recorded
                // RoomEvent.ArrestRequestUnresolvable here instead would otherwise be invisible.
                var registry = new InFlightExecutionRegistry();
                registry.Bind(writer);

                for (var tick = 1; tick <= 5; tick++)
                {
                    await CancelRequestPoller.TickAsync(
                        roomDirectory, logPath, Snapshot, registry, TestContext.Current.CancellationToken, roomLogPath);
                }

                Assert.False(File.Exists(requestPath), "the ceiling must have settled the request as .rejected");
                Assert.True(File.Exists($"{requestPath}.rejected"));

                // The line itself, not just the behaviour behind it: what the review found was a
                // stderr sentence promising a delivery this shape never gets. Both directions, since
                // the old wording and the new one are one clause apart.
                Assert.DoesNotContain("stays marked for delivery", stderr.ToString(), StringComparison.Ordinal);
                Assert.Contains("nothing records this request", stderr.ToString(), StringComparison.Ordinal);

                // The pump's own drain, standing in for SettleArrestIntentsAsync: it takes the mark and
                // records NOTHING for this shape, and the map is cleared by the drain itself.
                Assert.Contains(execId, registry.DrainArrestIntents().Select(intent => intent.ExecutionId));
                Assert.False(registry.HasPendingArrestIntents());

                // Tick 6: the poller cannot re-mark, because the pending file it would read is gone —
                // which is what turns "the pump re-marks next tick" from a premise into a dead end.
                await CancelRequestPoller.TickAsync(
                    roomDirectory, logPath, Snapshot, registry, TestContext.Current.CancellationToken, roomLogPath);
                Assert.False(registry.HasPendingArrestIntents(), "a settled request must not re-mark an arrest intent");
            }

            var entries = await new FlowEventLogReader(logPath)
                .ReadAllEntriesWithTimestampsAsync(TestContext.Current.CancellationToken);
            var roomEvents = await new RoomEventLogReader(roomLogPath).ReadAllRoomEventsAsync(TestContext.Current.CancellationToken);
            Assert.Empty(roomEvents);
            Assert.Empty(ArrestLedgerProjector.Project(entries, roomEvents));
        }
        finally
        {
            Console.SetError(originalError);
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    // #1563 (S0 of the quota design, #802): a step Failed with a scheduled RetryNotBefore — the
    // shape the idle-deferral park leaves behind once its worker process has already exited — is
    // neither "still running" (so the old bounded-retry-until-registered path never fires) nor
    // "already settled" (so the pre-#1563 code told the operator "too late", a false claim #802's
    // "three independent locks" finding identified — see CancelRequestPoller.cs's own comment on
    // that finding, F7 #1605 review, for the ASSUMED/code-derived confidence it actually carries).
    // It must be marked on the registry's wake latch and left pending, not consumed, until the pump
    // this registry is bound to actually drains it.
    [Fact]
    public async Task A_quota_parked_target_is_marked_on_the_registry_and_left_pending_not_declared_too_late()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"cancel-request-poller-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDirectory);
        try
        {
            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            var execId = new ExecutionId("exec-parked");
            var reset = DateTimeOffset.UtcNow.AddHours(2);

            await using (var writer = new FlowEventLogWriter(logPath))
            {
                await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(MakeRequest(execId, new StepId("a"))), TestContext.Current.CancellationToken);
                await writer.AppendAsync(new FlowEvent.ExecutionFailed(execId, FailureClassification.ExhaustedUntil, "quota exhausted", reset), TestContext.Current.CancellationToken);
                await writer.AppendAsync(new FlowEvent.StepRetryScheduled(new StepId("a"), execId, reset, RetryDelayMs: (int)TimeSpan.FromHours(2).TotalMilliseconds), TestContext.Current.CancellationToken);
            }

            await CancelRequestFile.WriteAsync(roomDirectory, "exec-parked", TestContext.Current.CancellationToken);

            // Not bound to any pump — mirrors production, where the poller only ever holds the
            // in-process handle to whatever pump started it; marking must not require a live process.
            var registry = new InFlightExecutionRegistry();

            await CancelRequestPoller.TickAsync(
                roomDirectory, logPath, Snapshot, registry, TestContext.Current.CancellationToken);

            var requestPath = CancelRequestFile.GetPath(roomDirectory);
            Assert.True(File.Exists(requestPath), "request must remain pending until the pump actually settles the park");
            Assert.False(File.Exists($"{requestPath}.consumed"));
            Assert.False(File.Exists($"{requestPath}.rejected"));
            Assert.Contains(execId, registry.DrainArrestIntents().Select(intent => intent.ExecutionId));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>
    /// #1607: the same parked shape as the test above, but requested via the <c>latest</c> literal
    /// rather than the execution id spelled out explicitly — proving the widened
    /// <see cref="RunningExecutionResolver"/> is what makes a bare <c>baton cancel &lt;room&gt;</c>
    /// reach a parked lane through this poller's <c>latest</c> resolution at
    /// <see cref="CancelRequestPoller"/>'s own line above. Everything past resolution (the
    /// <c>isParked</c> re-check and <c>MarkArrestIntent</c> call) is identical to the explicit-id
    /// test — this test's own value is entirely in reaching that machinery via <c>latest</c> at all,
    /// which the pre-#1607 resolver could never do for a parked-only room.
    /// </summary>
    [Fact]
    public async Task A_latest_request_against_a_quota_parked_only_room_is_marked_on_the_registry()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"cancel-request-poller-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDirectory);
        try
        {
            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            var execId = new ExecutionId("exec-parked-latest");
            var reset = DateTimeOffset.UtcNow.AddHours(2);

            await using (var writer = new FlowEventLogWriter(logPath))
            {
                await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(MakeRequest(execId, new StepId("a"))), TestContext.Current.CancellationToken);
                await writer.AppendAsync(new FlowEvent.ExecutionFailed(execId, FailureClassification.ExhaustedUntil, "quota exhausted", reset), TestContext.Current.CancellationToken);
                await writer.AppendAsync(new FlowEvent.StepRetryScheduled(new StepId("a"), execId, reset, RetryDelayMs: (int)TimeSpan.FromHours(2).TotalMilliseconds), TestContext.Current.CancellationToken);
            }

            await CancelRequestFile.WriteAsync(roomDirectory, CancelRequestFile.LatestTarget, TestContext.Current.CancellationToken);

            var registry = new InFlightExecutionRegistry();

            await CancelRequestPoller.TickAsync(
                roomDirectory, logPath, Snapshot, registry, TestContext.Current.CancellationToken);

            var requestPath = CancelRequestFile.GetPath(roomDirectory);
            Assert.True(File.Exists(requestPath), "request must remain pending until the pump actually settles the park");
            Assert.False(File.Exists($"{requestPath}.consumed"));
            Assert.False(File.Exists($"{requestPath}.rejected"));
            Assert.Contains(execId, registry.DrainArrestIntents().Select(intent => intent.ExecutionId));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    // Second-reader review finding, explained once beside the fix at CancelRequestPoller.cs's
    // `isParked` early-return above the bounded-retry counter: ticks well past 5 with no pump ever
    // draining the mark, to prove the request survives indefinitely rather than being rejected on a
    // ceiling sized for a different failure mode.
    [Fact]
    public async Task A_quota_parked_target_survives_past_the_bounded_retry_ceiling_without_being_rejected()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"cancel-request-poller-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDirectory);
        try
        {
            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            var execId = new ExecutionId("exec-parked-slow");
            var reset = DateTimeOffset.UtcNow.AddHours(2);

            await using (var writer = new FlowEventLogWriter(logPath))
            {
                await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(MakeRequest(execId, new StepId("a"))), TestContext.Current.CancellationToken);
                await writer.AppendAsync(new FlowEvent.ExecutionFailed(execId, FailureClassification.ExhaustedUntil, "quota exhausted", reset), TestContext.Current.CancellationToken);
                await writer.AppendAsync(new FlowEvent.StepRetryScheduled(new StepId("a"), execId, reset, RetryDelayMs: (int)TimeSpan.FromHours(2).TotalMilliseconds), TestContext.Current.CancellationToken);
            }

            await CancelRequestFile.WriteAsync(roomDirectory, "exec-parked-slow", TestContext.Current.CancellationToken);

            // No pump is ever started against this registry — the mark is drained by nobody, for
            // as many ticks as the old "still running" ceiling (5) would have tolerated and beyond.
            var registry = new InFlightExecutionRegistry();

            for (var i = 0; i < 8; i++)
            {
                await CancelRequestPoller.TickAsync(
                    roomDirectory, logPath, Snapshot, registry, TestContext.Current.CancellationToken);
            }

            var requestPath = CancelRequestFile.GetPath(roomDirectory);
            Assert.True(File.Exists(requestPath), "a parked mark must never be rejected on a tick ceiling — only the pump's own settle consumes it");
            Assert.False(File.Exists($"{requestPath}.consumed"));
            Assert.False(File.Exists($"{requestPath}.rejected"));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    // Once the pump this registry is bound to has actually processed the mark and settled the park
    // as Cancelled, the poller's own consume branch must say so honestly rather than repeat the
    // generic "too late" text — that text is what #802's "three independent locks" finding
    // identified as a false claim once an arrest is what actually ended the park (see
    // CancelRequestPoller.cs's own comment for that finding's actual confidence — F7, #1605 review).
    [Fact]
    public async Task Once_the_pump_settles_a_marked_park_as_Cancelled_the_poller_reports_arrested_not_too_late()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"cancel-request-poller-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDirectory);
        var originalError = Console.Error;
        try
        {
            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            var execId = new ExecutionId("exec-arrested");
            var reset = DateTimeOffset.UtcNow.AddHours(2);

            await using (var writer = new FlowEventLogWriter(logPath))
            {
                await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(MakeRequest(execId, new StepId("a"))), TestContext.Current.CancellationToken);
                await writer.AppendAsync(new FlowEvent.ExecutionFailed(execId, FailureClassification.ExhaustedUntil, "quota exhausted", reset), TestContext.Current.CancellationToken);
                await writer.AppendAsync(new FlowEvent.StepRetryScheduled(new StepId("a"), execId, reset, RetryDelayMs: (int)TimeSpan.FromHours(2).TotalMilliseconds), TestContext.Current.CancellationToken);
                // Simulates the pump having already drained a prior mark and settled the park —
                // this test isolates the poller's own message branch from the pump's wake wiring,
                // which QuotaParkCancelArrestTests (Baton.Tests) covers end to end.
                await writer.AppendAsync(new FlowEvent.CancellationRequested(execId), TestContext.Current.CancellationToken);
                await writer.AppendAsync(new FlowEvent.ExecutionCancelled(execId), TestContext.Current.CancellationToken);
            }

            await CancelRequestFile.WriteAsync(roomDirectory, "exec-arrested", TestContext.Current.CancellationToken);

            var registry = new InFlightExecutionRegistry();

            using var stderr = new StringWriter();
            Console.SetError(stderr);

            await CancelRequestPoller.TickAsync(
                roomDirectory, logPath, Snapshot, registry, TestContext.Current.CancellationToken);

            var requestPath = CancelRequestFile.GetPath(roomDirectory);
            Assert.False(File.Exists(requestPath), "expected the request to be consumed once settled");
            Assert.True(File.Exists($"{requestPath}.consumed"));
            Assert.Contains("arrested by this request", stderr.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("too late", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(originalError);
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task Latest_requested_with_zero_running_rejects_with_reason_in_body()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"cancel-request-poller-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDirectory);
        try
        {
            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            // Empty log: no running steps.
            await File.WriteAllTextAsync(logPath, string.Empty, TestContext.Current.CancellationToken);
            await CancelRequestFile.WriteAsync(roomDirectory, CancelRequestFile.LatestTarget, TestContext.Current.CancellationToken);

            var registry = new InFlightExecutionRegistry();

            await CancelRequestPoller.TickAsync(
                roomDirectory, logPath, Snapshot, registry, TestContext.Current.CancellationToken);

            var requestPath = CancelRequestFile.GetPath(roomDirectory);
            Assert.False(File.Exists(requestPath));
            var rejectedPath = $"{requestPath}.rejected";
            Assert.True(File.Exists(rejectedPath));

            var rejected = await CancelRequestFile.TryReadRejectedAsync(rejectedPath, TestContext.Current.CancellationToken);
            Assert.NotNull(rejected);
            Assert.Equal(CancelRequestFile.LatestTarget, rejected.Target);
            // #1607: the full wording, not just a prefix -- "Running" widened to "Running or
            // quota-parked" and a prefix-only assertion here would pass unchanged against the
            // pre-widening message too, which would defeat the point of this test.
            Assert.Contains("'latest' requested, but no execution is currently Running or quota-parked", rejected.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task Latest_requested_with_two_running_rejects_with_reason_in_body()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"cancel-request-poller-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDirectory);
        try
        {
            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            await using (var writer = new FlowEventLogWriter(logPath))
            {
                var execA = new ExecutionId("exec-a");
                var execB = new ExecutionId("exec-b");
                await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(MakeRequest(execA, new StepId("a"))), TestContext.Current.CancellationToken);
                await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(MakeRequest(execB, new StepId("b"))), TestContext.Current.CancellationToken);
            }

            await CancelRequestFile.WriteAsync(roomDirectory, CancelRequestFile.LatestTarget, TestContext.Current.CancellationToken);

            var registry = new InFlightExecutionRegistry();

            await CancelRequestPoller.TickAsync(
                roomDirectory, logPath, TwoStepSnapshot, registry, TestContext.Current.CancellationToken);

            var requestPath = CancelRequestFile.GetPath(roomDirectory);
            Assert.False(File.Exists(requestPath));
            var rejectedPath = $"{requestPath}.rejected";
            Assert.True(File.Exists(rejectedPath));

            var rejected = await CancelRequestFile.TryReadRejectedAsync(rejectedPath, TestContext.Current.CancellationToken);
            Assert.NotNull(rejected);
            Assert.Equal(CancelRequestFile.LatestTarget, rejected.Target);
            Assert.Contains("2 executions are currently Running or quota-parked", rejected.Reason);
            Assert.Contains("ambiguous", rejected.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    // #1530: the two rejection shapes above (malformed content, ambiguous 'latest') never resolve an
    // ExecutionId, so they have nothing to key a FlowEvent.CancellationRejected on -- room.jsonl,
    // reachable without ever touching flow.lock (the poller's whole premise, BatonPaths.RoomLogFileName's
    // own remarks), is their only durable home beyond the ephemeral .rejected file body.
    [Fact]
    public async Task Ambiguous_latest_records_an_unresolvable_arrest_to_room_jsonl_when_a_room_log_path_is_given()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"cancel-request-poller-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDirectory);
        try
        {
            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            var roomLogPath = Path.Combine(roomDirectory, "room.jsonl");
            await using (var writer = new FlowEventLogWriter(logPath))
            {
                var execA = new ExecutionId("exec-a");
                var execB = new ExecutionId("exec-b");
                await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(MakeRequest(execA, new StepId("a"))), TestContext.Current.CancellationToken);
                await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(MakeRequest(execB, new StepId("b"))), TestContext.Current.CancellationToken);
            }

            await CancelRequestFile.WriteAsync(roomDirectory, CancelRequestFile.LatestTarget, TestContext.Current.CancellationToken);

            var registry = new InFlightExecutionRegistry();

            await CancelRequestPoller.TickAsync(
                roomDirectory, logPath, TwoStepSnapshot, registry, TestContext.Current.CancellationToken, roomLogPath);

            var roomReader = new RoomEventLogReader(roomLogPath);
            var roomEvents = await roomReader.ReadAllRoomEventsAsync(TestContext.Current.CancellationToken);
            var unresolvable = Assert.Single(roomEvents.OfType<RoomEvent.ArrestRequestUnresolvable>());
            Assert.Equal(CancelRequestFile.LatestTarget, unresolvable.Target);
            Assert.Contains("ambiguous", unresolvable.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task Malformed_content_records_an_unresolvable_arrest_to_room_jsonl_when_a_room_log_path_is_given()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"cancel-request-poller-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDirectory);
        try
        {
            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            var roomLogPath = Path.Combine(roomDirectory, "room.jsonl");
            await File.WriteAllTextAsync(logPath, string.Empty, TestContext.Current.CancellationToken);

            var requestPath = CancelRequestFile.GetPath(roomDirectory);
            await File.WriteAllTextAsync(requestPath, "not valid json", TestContext.Current.CancellationToken);

            var registry = new InFlightExecutionRegistry();

            await CancelRequestPoller.TickAsync(
                roomDirectory, logPath, Snapshot, registry, TestContext.Current.CancellationToken, roomLogPath);

            var roomReader = new RoomEventLogReader(roomLogPath);
            var roomEvents = await roomReader.ReadAllRoomEventsAsync(TestContext.Current.CancellationToken);
            var unresolvable = Assert.Single(roomEvents.OfType<RoomEvent.ArrestRequestUnresolvable>());
            Assert.Equal(string.Empty, unresolvable.Target);
            Assert.Contains("malformed content", unresolvable.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    // Polarity control for both arms above: no roomLogPath given (every caller predating this
    // feature) must not throw and must not write room.jsonl at all.
    [Fact]
    public async Task Ambiguous_latest_with_no_room_log_path_given_does_not_write_room_jsonl()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"cancel-request-poller-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDirectory);
        try
        {
            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            await using (var writer = new FlowEventLogWriter(logPath))
            {
                var execA = new ExecutionId("exec-a");
                var execB = new ExecutionId("exec-b");
                await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(MakeRequest(execA, new StepId("a"))), TestContext.Current.CancellationToken);
                await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(MakeRequest(execB, new StepId("b"))), TestContext.Current.CancellationToken);
            }

            await CancelRequestFile.WriteAsync(roomDirectory, CancelRequestFile.LatestTarget, TestContext.Current.CancellationToken);

            var registry = new InFlightExecutionRegistry();

            await CancelRequestPoller.TickAsync(
                roomDirectory, logPath, TwoStepSnapshot, registry, TestContext.Current.CancellationToken);

            Assert.False(File.Exists(Path.Combine(roomDirectory, "room.jsonl")));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task RunAsync_survives_a_tick_that_throws_and_keeps_polling_until_cancelled()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"cancel-request-poller-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDirectory);
        try
        {
            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            // A corrupt flow.jsonl: FlowEventLogReader.ReadAllAsync throws FlowEventLogReadException on
            // a malformed complete line (Baton/Store/FlowEventLogReader.cs) -- the poller's "latest"
            // branch hits this on every tick for as long as the request stays pending.
            await File.WriteAllTextAsync(logPath, "{ not valid json }\n", TestContext.Current.CancellationToken);
            await CancelRequestFile.WriteAsync(roomDirectory, CancelRequestFile.LatestTarget, TestContext.Current.CancellationToken);

            var registry = new InFlightExecutionRegistry();
            using var pollCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            pollCancellation.CancelAfter(TimeSpan.FromMilliseconds(1200));

            // Would throw FlowEventLogReadException out of RunAsync itself if a tick's fault were not
            // caught -- the assertion is simply that this completes at all (via the CancelAfter timeout)
            // rather than propagating.
            await CancelRequestPoller.RunAsync(
                roomDirectory, logPath, Snapshot, registry, TimeSpan.FromMilliseconds(150), pollCancellation.Token);

            // Still pending: every tick faulted before ever reaching Consume/Reject.
            Assert.True(File.Exists(CancelRequestFile.GetPath(roomDirectory)));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }
}
