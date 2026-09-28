using Baton.Domain;
using Baton.Status;
using Baton.Store;

namespace Baton.Steering;

public enum SteeringReceiptState { Queued, InFlight, TransportAcknowledged, Rejected, OutcomeUnknown }

/// <summary>A native acknowledgement is a transport receipt, never proof of worker consumption.</summary>
public sealed record SteeringReceipt(RoomEvent.SteeringRequested Request, SteeringReceiptState State,
    string? Receipt = null, string? Reason = null);

/// <summary>Room-owned steering transitions under one cross-process lock, distinct from flow.lock.</summary>
public sealed class SteeringMessageStore
{
    private const string LockPrefix = "baton-room-steering";
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);
    private readonly string _logPath;

    public SteeringMessageStore(string roomDirectoryPath)
    {
        var room = BatonPaths.RecordKey(roomDirectoryPath);
        if (!Directory.Exists(room))
        {
            throw new DirectoryNotFoundException($"Steering room '{room}' does not exist.");
        }
        _logPath = Path.Combine(room, BatonPaths.RoomLogFileName);
    }

    public SteeringReceipt Reserve(RoomEvent.SteeringRequested request, bool targetLive) => UnderLock(() =>
    {
        Validate(request);
        var prior = Project(request.MessageId, ReadEvents());
        if (prior is not null)
        {
            if (prior.Request.ExecutionId != request.ExecutionId
                || prior.Request.PayloadSha256 != request.PayloadSha256
                || prior.Request.BrokerIncarnation != request.BrokerIncarnation
                || prior.Request.ThreadId != request.ThreadId || prior.Request.TurnId != request.TurnId
                || prior.Request.OsPrincipal != request.OsPrincipal)
            {
                throw new InvalidOperationException($"Steering message ID '{request.MessageId}' conflicts with its immutable request.");
            }
            return ReceiptFor(prior, targetLive);
        }
        if (!targetLive)
        {
            throw new InvalidOperationException("The exact broker turn is no longer live; no steering request was reserved.");
        }
        Append(request);
        return new SteeringReceipt(request, SteeringReceiptState.Queued);
    });

    /// <summary>Fsyncs send-started before the caller may write any native request byte.</summary>
    public bool TryStartSend(string messageId, string payloadSha256, string executionId,
        string brokerIncarnation, string threadId, string turnId) => UnderLock(() =>
    {
        var prior = Project(messageId, ReadEvents())
            ?? throw new InvalidOperationException($"Steering message '{messageId}' was never reserved.");
        var request = prior.Request;
        if (request.PayloadSha256 != payloadSha256 || request.ExecutionId != executionId
            || request.BrokerIncarnation != brokerIncarnation || request.ThreadId != threadId
            || request.TurnId != turnId)
        {
            throw new InvalidOperationException("Steering target or payload differs from the durable request.");
        }
        if (prior.Sent || prior.Answer is not null)
        {
            return false;
        }
        Append(new RoomEvent.SteeringSendStarted(messageId, brokerIncarnation, DateTimeOffset.UtcNow));
        return true;
    });

    /// <summary>Retains the semantic response before an accepted result can be returned.</summary>
    public SteeringReceipt RecordAnswer(string messageId, bool accepted, string? receipt, string? reason) => UnderLock(() =>
    {
        var prior = Project(messageId, ReadEvents())
            ?? throw new InvalidOperationException($"Steering message '{messageId}' was never reserved.");
        if (!prior.Sent)
        {
            throw new InvalidOperationException("Steering response has no durable send-started fact.");
        }
        if (prior.Answer is { } existing)
        {
            if (existing.Accepted != accepted || existing.Receipt != receipt || existing.Reason != reason)
            {
                throw new InvalidOperationException("Steering response conflicts with retained evidence.");
            }
        }
        else
        {
            Append(new RoomEvent.SteeringTransportAnswered(messageId, accepted, receipt, reason, DateTimeOffset.UtcNow));
        }
        return new SteeringReceipt(prior.Request,
            accepted ? SteeringReceiptState.TransportAcknowledged : SteeringReceiptState.Rejected, receipt, reason);
    });

    public SteeringReceipt? Query(string messageId, bool exactTurnLive) => UnderLock(() =>
    {
        var events = ReadEvents();
        var prior = Project(messageId, events);
        if (prior is null) return null;
        var receipt = ReceiptFor(prior, exactTurnLive);
        if (receipt.State == SteeringReceiptState.Queued
            && HasOtherUnresolvedSend(prior.Request, events))
        {
            return receipt with
            {
                Reason = "Another native steering RPC for this broker turn is unresolved; this request has not been sent.",
            };
        }
        return receipt;
    });

    private static bool HasOtherUnresolvedSend(RoomEvent.SteeringRequested request, IReadOnlyList<RoomEvent> events)
    {
        var requests = events.OfType<RoomEvent.SteeringRequested>()
            .ToDictionary(fact => fact.MessageId, StringComparer.Ordinal);
        var answered = events.OfType<RoomEvent.SteeringTransportAnswered>()
            .Select(fact => fact.MessageId).ToHashSet(StringComparer.Ordinal);
        return events.OfType<RoomEvent.SteeringSendStarted>().Any(send =>
            send.MessageId != request.MessageId && !answered.Contains(send.MessageId)
            && requests.TryGetValue(send.MessageId, out var other)
            && other.BrokerIncarnation == request.BrokerIncarnation
            && other.ThreadId == request.ThreadId && other.TurnId == request.TurnId);
    }

    private T UnderLock<T>(Func<T> action) =>
        MutexGuardedFileLock.RunUnderLock(_logPath, LockPrefix, LockTimeout, action);

    private IReadOnlyList<RoomEvent> ReadEvents() =>
        new RoomEventLogReader(_logPath).ReadAllRoomEventsAsync().GetAwaiter().GetResult();

    private void Append(RoomEvent fact)
    {
        var writer = new RoomEventLogWriter(_logPath);
        try
        {
            writer.AppendAsync(fact).GetAwaiter().GetResult();
        }
        finally
        {
            writer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static void Validate(RoomEvent.SteeringRequested request)
    {
        if (string.IsNullOrWhiteSpace(request.MessageId) || request.MessageId.Length > 128
            || string.IsNullOrWhiteSpace(request.ExecutionId) || string.IsNullOrWhiteSpace(request.PayloadSha256)
            || string.IsNullOrWhiteSpace(request.BrokerIncarnation) || string.IsNullOrWhiteSpace(request.ThreadId)
            || string.IsNullOrWhiteSpace(request.TurnId) || string.IsNullOrWhiteSpace(request.OsPrincipal))
        {
            throw new ArgumentException("A steering request requires a bounded ID and exact target identity.");
        }
    }

    private static SteeringReceipt ReceiptFor(Projected prior, bool live)
    {
        if (prior.Answer is { } answer)
        {
            return new(prior.Request,
                answer.Accepted ? SteeringReceiptState.TransportAcknowledged : SteeringReceiptState.Rejected,
                answer.Receipt, answer.Reason);
        }
        if (prior.Sent)
        {
            return new(prior.Request, live ? SteeringReceiptState.InFlight : SteeringReceiptState.OutcomeUnknown,
                Reason: live ? null : "Native send began without a retained semantic response.");
        }
        return new(prior.Request, live ? SteeringReceiptState.Queued : SteeringReceiptState.Rejected,
            Reason: live ? null : "The exact broker turn ended before native send began.");
    }

    private sealed record Projected(RoomEvent.SteeringRequested Request, bool Sent,
        RoomEvent.SteeringTransportAnswered? Answer);

    private static Projected? Project(string messageId, IReadOnlyList<RoomEvent> events)
    {
        RoomEvent.SteeringRequested? request = null;
        var sent = false;
        RoomEvent.SteeringTransportAnswered? answer = null;
        foreach (var fact in events)
        {
            switch (fact)
            {
                case RoomEvent.SteeringRequested current when current.MessageId == messageId:
                    if (request is not null) throw new InvalidOperationException($"Duplicate steering request '{messageId}'.");
                    request = current;
                    break;
                case RoomEvent.SteeringSendStarted current when current.MessageId == messageId:
                    if (request is null || sent || answer is not null) throw new InvalidOperationException($"Invalid steering send transition '{messageId}'.");
                    sent = true;
                    break;
                case RoomEvent.SteeringTransportAnswered current when current.MessageId == messageId:
                    if (request is null || !sent || answer is not null) throw new InvalidOperationException($"Invalid steering answer transition '{messageId}'.");
                    answer = current;
                    break;
            }
        }
        return request is null ? null : new Projected(request, sent, answer);
    }
}
