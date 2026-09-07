using Baton.Domain;
using Baton.Store;

namespace Baton.Status;

/// <summary>
/// #1530: the room-side arrest ledger's outcome tier — <c>requested</c> is not a distinct value here
/// because a still-pending request has not yet reached one of these three; see
/// <see cref="ArrestLedgerEntry.ResolvedAtUtc"/>'s own remarks for how "still requested" renders.
/// </summary>
public enum ArrestOutcome
{
    Delivered,
    Rejected,
    Expired,
}

/// <summary>
/// One entry in a room's arrest history: an operator (or the pump's own host-stop wind-down) asked
/// for <paramref name="ExecutionId"/> (or the raw <paramref name="Target"/> string, for the two
/// shapes that never resolved one) to be arrested, and this is how it was settled.
/// </summary>
/// <param name="Target">
/// The literal target as written — <see cref="Domain.ExecutionId.Value"/> for every entry that
/// resolved one, or the raw <c>cancel.request</c> <c>Target</c> field (including
/// <c>CancelRequestFile.LatestTarget</c>) for the two room-event-sourced shapes that never did.
/// </param>
/// <param name="ExecutionId">
/// Null only for <see cref="RoomEvent.ArrestRequestUnresolvable"/>/<see cref="RoomEvent.ArrestRequestExpired"/>
/// entries — the two shapes with nothing to key a <see cref="FlowEvent.CancellationRequested"/> on.
/// </param>
/// <param name="Outcome">Absent (this property is null) while the request is still pending settlement.</param>
/// <param name="RequestedBy">
/// <see cref="CancellationOrigin.Operator"/> or <see cref="CancellationOrigin.HostStop"/>, rendered
/// lower-case; <c>"operator"</c> for a pre-#1762 line carrying no <see cref="CancellationOrigin"/> at
/// all (that field's own default), for both room-event-sourced shapes, and for a
/// <see cref="FlowEvent.CancellationRejected"/> with no preceding <see cref="FlowEvent.CancellationRequested"/>
/// to read an origin off (<see cref="ArrestLedgerProjector.Project"/>'s synthesized-orphan branch) —
/// every one of these is only ever written from an operator's own <c>cancel.request</c>, since
/// <see cref="Mutation.InFlightExecutionRegistry.MarkArrestIntent"/> (the only caller that can
/// produce an orphaned rejection) has exactly one caller, itself the operator's own request. There is
/// no distinct "glass" origin: glass only ever hands an operator a <c>baton cancel</c> command to
/// copy and run themselves, so every arrest this ledger can see was, from the engine's perspective,
/// requested by the CLI.
/// </param>
/// <param name="Reason">Populated only for <see cref="ArrestOutcome.Rejected"/>.</param>
/// <param name="ResolvedAtUtc">Null while <see cref="Outcome"/> is null (still pending).</param>
public sealed record ArrestLedgerEntry(
    string Target,
    ExecutionId? ExecutionId,
    ArrestOutcome? Outcome,
    string RequestedBy,
    string? Reason,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? ResolvedAtUtc);

/// <summary>
/// Projects a room's full arrest history from the two logs a <c>cancel.request</c> can durably land
/// on — <c>flow.jsonl</c> for every request that resolved a target <see cref="ExecutionId"/>
/// (<see cref="FlowEvent.CancellationRequested"/>/<see cref="FlowEvent.ExecutionCancelled"/>/
/// <see cref="FlowEvent.CancellationRejected"/>), and <c>room.jsonl</c> for the two shapes that never
/// did (<see cref="RoomEvent.ArrestRequestUnresolvable"/>/<see cref="RoomEvent.ArrestRequestExpired"/>
/// — see that type's own remarks for why the second log is the only durable home available). Reading
/// both existing logs rather than inventing a third, parallel ledger store is exactly what CLAUDE.md's
/// <c>record-once</c> gate asks for.
/// </summary>
public static class ArrestLedgerProjector
{
    public static IReadOnlyList<ArrestLedgerEntry> Project(
        IReadOnlyList<LogEntry> flowLogEntries, IReadOnlyList<RoomEvent> roomEvents)
    {
        ArgumentNullException.ThrowIfNull(flowLogEntries);
        ArgumentNullException.ThrowIfNull(roomEvents);

        // Builder keyed by ExecutionId, in first-CancellationRequested-seen order — an ExecutionId is
        // never reused across two distinct cancel.request lifecycles (a fresh request always names
        // either 'latest', re-resolved at delivery time, or the SAME still-Running execution it
        // already named), so one builder per id is exactly one ledger entry per request.
        var order = new List<ExecutionId>();
        var builders = new Dictionary<ExecutionId, (string RequestedBy, string? Reason, ArrestOutcome? Outcome, DateTimeOffset RequestedAtUtc, DateTimeOffset? ResolvedAtUtc)>();

        foreach (var entry in flowLogEntries)
        {
            if (entry is not LogEntry.FlowLogEntry flowLogEntry)
            {
                continue;
            }

            var timestamp = flowLogEntry.WriterUtcTimestamp is { } stamped
                ? new DateTimeOffset(DateTime.SpecifyKind(stamped, DateTimeKind.Utc))
                : (DateTimeOffset?)null;

            switch (flowLogEntry.Event)
            {
                case FlowEvent.CancellationRequested requested:
                    if (!builders.ContainsKey(requested.ExecutionId))
                    {
                        order.Add(requested.ExecutionId);
                        builders[requested.ExecutionId] = (
                            RenderRequestedBy(requested.Origin), Reason: null, Outcome: null,
                            RequestedAtUtc: timestamp ?? DateTimeOffset.UnixEpoch, ResolvedAtUtc: null);
                    }

                    break;

                // #1556's non-process seam and #1563's park-drain both settle by appending
                // ExecutionCancelled off the SAME projected fact CancelRequestPoller.cs's own
                // "arrestedByThisRequest" check reads — reused here rather than restated, since a
                // Process-bound arrest's own CancellationDelivered is a signal-reached-a-token
                // intermediate fact, not the terminal one this ledger reports.
                case FlowEvent.ExecutionCancelled cancelled when builders.TryGetValue(cancelled.ExecutionId, out var pending):
                    builders[cancelled.ExecutionId] = pending with { Outcome = ArrestOutcome.Delivered, ResolvedAtUtc = timestamp };
                    break;

                case FlowEvent.CancellationRejected rejected:
                    // #1530 fix: a rejection can land with no preceding CancellationRequested at
                    // all -- InFlightExecutionRegistry.RequestCancellationAsync returns false (and
                    // records nothing) for a target that never registered in-flight, so
                    // RecordCancellationRejectedAsync's CancellationRejected is the ONLY event this
                    // lifecycle ever produces (the poller's "too late (it already settled)" path).
                    // Reusing an existing builder when one is already open still takes the
                    // `TryGetValue` branch -- SettleArrestIntentsAsync's dropped-intent rejection can
                    // land against a lifecycle some earlier CancellationRequested already opened; a
                    // rejection with nothing open synthesizes its own single-entry lifecycle instead
                    // of being silently dropped. #2045 removed the third producer this comment used
                    // to name (the poller's bounded-retry ceiling): a rejection for a target still
                    // admitted by ArrestableExecutions.Find was the one shape that could reopen as a
                    // Delivered entry still carrying a rejection Reason, which the entry's own
                    // `Reason` doc forbids.
                    if (builders.TryGetValue(rejected.ExecutionId, out var pendingRejection))
                    {
                        builders[rejected.ExecutionId] = pendingRejection with
                        {
                            Outcome = ArrestOutcome.Rejected,
                            Reason = rejected.Reason,
                            ResolvedAtUtc = timestamp,
                        };
                    }
                    else
                    {
                        order.Add(rejected.ExecutionId);
                        builders[rejected.ExecutionId] = (
                            RequestedBy: "operator", Reason: rejected.Reason, Outcome: ArrestOutcome.Rejected,
                            RequestedAtUtc: timestamp ?? DateTimeOffset.UnixEpoch, ResolvedAtUtc: timestamp);
                    }

                    break;
            }
        }

        var results = new List<ArrestLedgerEntry>(order.Count + roomEvents.Count);
        foreach (var executionId in order)
        {
            var b = builders[executionId];
            results.Add(new ArrestLedgerEntry(executionId.Value, executionId, b.Outcome, b.RequestedBy, b.Reason, b.RequestedAtUtc, b.ResolvedAtUtc));
        }

        foreach (var roomEvent in roomEvents)
        {
            switch (roomEvent)
            {
                case RoomEvent.ArrestRequestUnresolvable unresolvable:
                    results.Add(new ArrestLedgerEntry(
                        unresolvable.Target, ExecutionId: null, ArrestOutcome.Rejected, RequestedBy: "operator",
                        unresolvable.Reason, unresolvable.RequestedAtUtc, unresolvable.RecordedAtUtc));
                    break;

                case RoomEvent.ArrestRequestExpired expired:
                    results.Add(new ArrestLedgerEntry(
                        expired.Target, ExecutionId: null, ArrestOutcome.Expired, RequestedBy: "operator",
                        Reason: null, expired.RequestedAtUtc, expired.RecordedAtUtc));
                    break;
            }
        }

        return results.OrderBy(e => e.RequestedAtUtc).ToList();
    }

    private static string RenderRequestedBy(CancellationOrigin? origin) =>
        origin switch
        {
            CancellationOrigin.HostStop => "host-stop",
            _ => "operator",
        };
}
