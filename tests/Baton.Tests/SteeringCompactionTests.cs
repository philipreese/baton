using Baton.Concurrency;
using Baton.Domain;
using Baton.Projection;
using Baton.Status;
using Baton.Steering;
using Baton.Store;
using Baton.Tests.Shared;

namespace Baton.Tests.Steering;

public sealed class SteeringCompactionTests
{
    private static readonly DateTimeOffset RequestedAt = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Room_events_lock_serializes_steering_append_with_compaction_and_preserves_request_send_answer()
    {
        var room = Path.Combine(Path.GetTempPath(), "baton-steering-compaction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(room);
        var roomLog = Path.Combine(room, BatonPaths.RoomLogFileName);

        try
        {
            var completedRef = new HeldWorkRef("resolved-lane");
            await using (var setup = new RoomEventLogWriter(roomLog))
            {
                await setup.AppendAsync(new RoomEvent.HeldWorkDispatched(
                    completedRef, "shape", TimeSpan.FromMinutes(1), "test"), TestContext.Current.CancellationToken);
                await setup.AppendAsync(new RoomEvent.HeldWorkResolved(
                    completedRef, new HeldWorkCitation("Resolved", "done")), TestContext.Current.CancellationToken);
            }

            var store = new SteeringMessageStore(room);
            var retainedRequest = Request("steer-before-compaction");
            store.Reserve(retainedRequest, targetLive: true);
            Assert.True(store.TryStartSend(retainedRequest.MessageId, retainedRequest.PayloadSha256,
                retainedRequest.ExecutionId, retainedRequest.BrokerIncarnation,
                retainedRequest.ThreadId, retainedRequest.TurnId));
            store.RecordAnswer(retainedRequest.MessageId, accepted: true, receipt: "ack-before", reason: null);

            var compactorReachedReplace = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var allowReplace = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var compaction = RoomJournalCompactor.CompactAsync(room, TestContext.Current.CancellationToken, async () =>
            {
                // The compactor already read the journal and wrote its replacement file. Holding
                // here creates the exact window in which an uncoordinated append would be erased.
                compactorReachedReplace.TrySetResult(true);
                await allowReplace.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken); // wait-ok: fixture release is guaranteed in finally
            });

            var requestDuringCompaction = Request("steer-during-compaction");
            var mutationStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<SteeringReceipt>? reserveTask = null;
            var completedWhileCompactorHeld = false;
            try
            {
                await compactorReachedReplace.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); // wait-ok: deterministic fixture barrier
                reserveTask = Task.Factory.StartNew(() =>
                {
                    mutationStarted.TrySetResult(true);
                    return new SteeringMessageStore(room).Reserve(requestDuringCompaction, targetLive: true);
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                await mutationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); // wait-ok: deterministic fixture barrier

                // The barrier above guarantees the compactor holds room-events.lock at the
                // read/replace seam, and the long-running task has started the store call. The
                // bounded window lets an unguarded append finish here while a guarded one waits.
                completedWhileCompactorHeld = await Task.WhenAny(
                    reserveTask, Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken)) == reserveTask; // wait-ok: short negative-observation window, then release in finally
            }
            finally
            {
                allowReplace.TrySetResult(true);
            }

            Assert.True(await compaction.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)); // wait-ok: fixture completion after release
            Assert.NotNull(reserveTask);
            var duringReservation = await reserveTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); // wait-ok: fixture completion after release
            Assert.False(completedWhileCompactorHeld,
                "The steering append must wait until compaction has replaced the room journal and released its shared lock.");
            Assert.Equal(SteeringReceiptState.Queued, duringReservation.State);

            Assert.True(store.TryStartSend(requestDuringCompaction.MessageId, requestDuringCompaction.PayloadSha256,
                requestDuringCompaction.ExecutionId, requestDuringCompaction.BrokerIncarnation,
                requestDuringCompaction.ThreadId, requestDuringCompaction.TurnId));
            store.RecordAnswer(requestDuringCompaction.MessageId, accepted: true, receipt: "ack-after", reason: null);

            var events = await new RoomEventLogReader(roomLog).ReadAllRoomEventsAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain(events, fact => fact is RoomEvent.HeldWorkDispatched or RoomEvent.HeldWorkResolved);
            Assert.Equal(2, events.OfType<RoomEvent.SteeringRequested>().Count());
            Assert.Equal(2, events.OfType<RoomEvent.SteeringSendStarted>().Count());
            Assert.Equal(2, events.OfType<RoomEvent.SteeringTransportAnswered>().Count());

            var reloaded = new SteeringMessageStore(room);
            var retained = reloaded.Query(retainedRequest.MessageId, exactTurnLive: false);
            var appended = reloaded.Query(requestDuringCompaction.MessageId, exactTurnLive: false);
            Assert.NotNull(retained);
            Assert.NotNull(appended);
            Assert.Equal(SteeringReceiptState.TransportAcknowledged, retained.State);
            Assert.Equal("ack-before", retained.Receipt);
            Assert.Equal(SteeringReceiptState.TransportAcknowledged, appended.State);
            Assert.Equal("ack-after", appended.Receipt);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
        }
    }

    private static RoomEvent.SteeringRequested Request(string messageId) => new(
        MessageId: messageId,
        ExecutionId: "execution-1",
        PayloadSha256: $"hash-{messageId}",
        BrokerIncarnation: "broker-1",
        ThreadId: "thread-1",
        TurnId: "turn-1",
        OsPrincipal: "test-user",
        RequestedAtUtc: RequestedAt);
}
