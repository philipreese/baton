using Baton.Domain;
using Baton.Status;
using Baton.Steering;
using Baton.Store;

namespace Baton.Tests.Steering;

public sealed class SteeringMessageStoreTests
{
    private static readonly DateTimeOffset RequestedAt = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Reserve_is_idempotent_for_identical_request_and_rejects_conflicting_immutable_payload()
    {
        var room = NewRoom();
        try
        {
            var store = new SteeringMessageStore(room);
            var request = Request();

            var first = store.Reserve(request, targetLive: true);
            var duplicate = store.Reserve(request, targetLive: true);

            Assert.Equal(SteeringReceiptState.Queued, first.State);
            Assert.Equal(first, duplicate);
            Assert.Throws<InvalidOperationException>(() =>
                store.Reserve(request with { PayloadSha256 = "different-payload-hash" }, targetLive: true));
            Assert.Single(await Events(room));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
        }
    }

    [Fact]
    public async Task Concurrent_store_instances_claim_one_send_start()
    {
        var room = NewRoom();
        try
        {
            var request = Request();
            new SteeringMessageStore(room).Reserve(request, targetLive: true);
            using var barrier = new Barrier(2);

            // This is a deterministic same-process race across separate store instances. It does
            // not establish cross-process lock behavior; that belongs to the process-level tests.
            var attempts = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
            {
                Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10)), "Both send claimants should reach the race together.");
                return new SteeringMessageStore(room).TryStartSend(
                    request.MessageId, request.PayloadSha256, request.ExecutionId,
                    request.BrokerIncarnation, request.ThreadId, request.TurnId);
            })));

            Assert.Single(attempts, started => started);
            Assert.Single(attempts, started => !started);
            Assert.Equal(2, (await Events(room)).Count);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
        }
    }

    [Fact]
    public async Task Send_start_fact_is_readable_when_claim_returns_and_answer_survives_store_reconstruction()
    {
        var room = NewRoom();
        try
        {
            var request = Request();
            var store = new SteeringMessageStore(room);
            store.Reserve(request, targetLive: true);

            Assert.True(store.TryStartSend(request.MessageId, request.PayloadSha256, request.ExecutionId,
                request.BrokerIncarnation, request.ThreadId, request.TurnId));

            var afterClaim = await Events(room);
            Assert.Collection(afterClaim,
                fact => Assert.Equal(request, Assert.IsType<RoomEvent.SteeringRequested>(fact)),
                fact => Assert.Equal(request.MessageId, Assert.IsType<RoomEvent.SteeringSendStarted>(fact).MessageId));

            var answered = store.RecordAnswer(request.MessageId, accepted: true, receipt: "ack-1", reason: null);
            Assert.Equal(SteeringReceiptState.TransportAcknowledged, answered.State);

            var reconstructed = new SteeringMessageStore(room);
            var projected = reconstructed.Query(request.MessageId, exactTurnLive: false);
            Assert.NotNull(projected);
            Assert.Equal(SteeringReceiptState.TransportAcknowledged, projected.State);
            Assert.Equal("ack-1", projected.Receipt);
            Assert.Null(projected.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
        }
    }

    [Fact]
    public async Task Send_without_answer_is_inflight_while_live_and_unknown_after_turn_ends_without_resend()
    {
        var room = NewRoom();
        try
        {
            var request = Request();
            var store = new SteeringMessageStore(room);
            store.Reserve(request, targetLive: true);
            Assert.True(store.TryStartSend(request.MessageId, request.PayloadSha256, request.ExecutionId,
                request.BrokerIncarnation, request.ThreadId, request.TurnId));

            Assert.Equal(SteeringReceiptState.InFlight, store.Query(request.MessageId, exactTurnLive: true)!.State);
            var ended = store.Query(request.MessageId, exactTurnLive: false);
            Assert.NotNull(ended);
            Assert.Equal(SteeringReceiptState.OutcomeUnknown, ended.State);
            Assert.Contains("without a retained semantic response", ended.Reason ?? string.Empty);
            Assert.False(store.TryStartSend(request.MessageId, request.PayloadSha256, request.ExecutionId,
                request.BrokerIncarnation, request.ThreadId, request.TurnId));
            Assert.Equal(2, (await Events(room)).Count);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
        }
    }

    [Fact]
    public async Task Dead_target_cannot_be_reserved()
    {
        var room = NewRoom();
        try
        {
            var store = new SteeringMessageStore(room);
            var request = Request();

            Assert.Throws<InvalidOperationException>(() => store.Reserve(request, targetLive: false));
            Assert.Null(store.Query(request.MessageId, exactTurnLive: false));
            Assert.Empty(await Events(room));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
        }
    }

    [Fact]
    public void A_second_reserved_message_exposes_the_unresolved_native_rpc_lockout()
    {
        var room = NewRoom();
        try
        {
            var store = new SteeringMessageStore(room);
            var first = Request();
            var second = first with { MessageId = "steer-2", PayloadSha256 = "payload-hash-2" };
            store.Reserve(first, targetLive: true);
            Assert.True(store.TryStartSend(first.MessageId, first.PayloadSha256, first.ExecutionId,
                first.BrokerIncarnation, first.ThreadId, first.TurnId));
            store.Reserve(second, targetLive: true);

            var queued = store.Query(second.MessageId, exactTurnLive: true);
            Assert.NotNull(queued);
            Assert.Equal(SteeringReceiptState.Queued, queued.State);
            Assert.Contains("unresolved", queued.Reason);
            Assert.Contains("has not been sent", queued.Reason);
        }
        finally { DirectoryCleanup.DeleteRecursively(room); }
    }

    [Fact]
    public async Task Previously_reserved_request_is_rejected_after_exact_target_turn_ends()
    {
        var room = NewRoom();
        try
        {
            var request = Request();
            var store = new SteeringMessageStore(room);
            store.Reserve(request, targetLive: true);

            var receipt = store.Query(request.MessageId, exactTurnLive: false);

            Assert.NotNull(receipt);
            Assert.Equal(SteeringReceiptState.Rejected, receipt.State);
            Assert.Contains("ended before native send began", receipt.Reason ?? string.Empty);
            Assert.Single(await Events(room));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
        }
    }

    [Fact]
    public async Task Answer_without_durable_send_start_is_rejected()
    {
        var room = NewRoom();
        try
        {
            var request = Request();
            var store = new SteeringMessageStore(room);
            store.Reserve(request, targetLive: true);

            Assert.Throws<InvalidOperationException>(() =>
                store.RecordAnswer(request.MessageId, accepted: true, receipt: "ack-1", reason: null));
            Assert.Single(await Events(room));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
        }
    }

    [Fact]
    public async Task Invalid_known_event_transition_fails_loudly_during_projection()
    {
        var room = NewRoom();
        try
        {
            var messageId = "steer-1";
            await using (var writer = new RoomEventLogWriter(Path.Combine(room, BatonPaths.RoomLogFileName)))
            {
                await writer.AppendAsync(new RoomEvent.SteeringSendStarted(messageId, "broker-1", RequestedAt),
                    TestContext.Current.CancellationToken);
            }

            Assert.Throws<InvalidOperationException>(() =>
                new SteeringMessageStore(room).Query(messageId, exactTurnLive: false));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
        }
    }

    [Fact]
    public async Task Malformed_room_event_line_fails_loudly_instead_of_appearing_as_no_request()
    {
        var room = NewRoom();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(room, BatonPaths.RoomLogFileName), "{ broken json\n",
                TestContext.Current.CancellationToken);

            Assert.Throws<FlowEventLogReadException>(() =>
                new SteeringMessageStore(room).Query("steer-1", exactTurnLive: false));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
        }
    }

    private static RoomEvent.SteeringRequested Request() => new(
        MessageId: "steer-1",
        ExecutionId: "execution-1",
        PayloadSha256: "payload-hash-1",
        BrokerIncarnation: "broker-1",
        ThreadId: "thread-1",
        TurnId: "turn-1",
        OsPrincipal: "test-user",
        RequestedAtUtc: RequestedAt);

    private static string NewRoom()
    {
        var path = Path.Combine(Path.GetTempPath(), "baton-steering-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static Task<IReadOnlyList<RoomEvent>> Events(string room) =>
        new RoomEventLogReader(Path.Combine(room, BatonPaths.RoomLogFileName)).ReadAllRoomEventsAsync();
}
