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
/// shapes that never resolved one) to be arrested, and this is how it was settled. Since #2073 an
/// operator's <c>baton cancel</c> is visible here even when no pump ever saw a request — the
/// <see cref="RoomEvent.ArrestIntentRecorded"/> arm in <see cref="ArrestLedgerProjector.Project"/>.
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
/// <param name="Reason">
/// The rejection reason for <see cref="ArrestOutcome.Rejected"/> — on the flow side a reason is
/// populated only for Rejected. For every other outcome, the operator's own <c>--reason</c> when a
/// <see cref="RoomEvent.ArrestIntentRecorded"/> (#2073) exists for this execution — whether the pump
/// answered it (#2104: the intent MERGES into the flow-side entry rather than being dropped) or not.
/// Null otherwise. A Rejected entry keeps the rejection's reason even when an intent carries one of
/// its own, because <c>baton status</c> renders this field as <c>rejected (&lt;Reason&gt;)</c> — the
/// explanation of the outcome, not of the request. Several intents for one execution (a second
/// <c>baton cancel</c> re-run while the first is still queued — <c>CancelCommand</c>'s idempotency
/// check only stops one against a target that already settled) merge last-write-wins into the one
/// entry — whether a pump answered or not: the latest intent's reason is the one kept, and so is its
/// stamp in <see cref="RequestedAtUtc"/>.
/// </param>
/// <param name="RequestedAtUtc">
/// When the operator asked: the <see cref="RoomEvent.ArrestIntentRecorded"/> stamp when one exists
/// for this execution — the LATEST such stamp when several do, last-write-wins on the merge, see
/// <see cref="Reason"/> — else the flow-side <see cref="FlowEvent.CancellationRequested"/> stamp
/// (the pump's own forwarding time, the only request-shaped instant a pre-#2073 line or a host-stop
/// has), else the rejection's own stamp for an orphaned <see cref="FlowEvent.CancellationRejected"/>.
/// </param>
/// <param name="ForwardedAtUtc">
/// #2104: the <see cref="FlowEvent.CancellationRequested"/> stamp — the instant Flow forwarded the
/// request toward Core (that event's own doc: it records only that forwarding; the
/// signal reaching a token is <see cref="FlowEvent.CancellationDelivered"/>, which this ledger does
/// not report, and the settlement is <see cref="ResolvedAtUtc"/>, a different fact's stamp — this
/// field is never that instant). Set exactly when such a line exists for this execution and carries a
/// writer timestamp (a pre-timestamp line leaves it null with <see cref="RequestedAtUtc"/> at the
/// epoch). Null on every other shape: an intent no pump ever forwarded (<c>baton cancel</c> settled
/// the room itself, or is still waiting), an orphaned <see cref="FlowEvent.CancellationRejected"/>
/// (the pump answered by refusing, and never forwarded anything), and the two room-event-sourced
/// shapes. When a stamped <see cref="FlowEvent.CancellationRequested"/> opened the entry and no
/// intent fact exists, <see cref="RequestedAtUtc"/> carries this same stamp, since the forwarding
/// is then the only request-shaped instant there is (the orphaned rejection, having no forwarding,
/// carries the rejection's own stamp there instead).
/// </param>
/// <param name="ResolvedAtUtc">Null while <see cref="Outcome"/> is null (still pending).</param>
public sealed record ArrestLedgerEntry(
    string Target,
    ExecutionId? ExecutionId,
    ArrestOutcome? Outcome,
    string RequestedBy,
    string? Reason,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? ResolvedAtUtc,
    DateTimeOffset? ForwardedAtUtc = null);

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
        var builders = new Dictionary<ExecutionId, (string RequestedBy, string? Reason, ArrestOutcome? Outcome, DateTimeOffset RequestedAtUtc, DateTimeOffset? ResolvedAtUtc, DateTimeOffset? ForwardedAtUtc)>();

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
                            RequestedAtUtc: timestamp ?? DateTimeOffset.UnixEpoch, ResolvedAtUtc: null,
                            ForwardedAtUtc: timestamp);
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
                    // of being silently dropped. This is the statement of record for that pairing:
                    // MutationInterface's own comment on the same append reads its early continue as
                    // proving "no CancellationRequested exists yet for this executionId", which is
                    // narrower than what the guard actually proves. FlowState projects
                    // CancellationRequestedExecutionIds as requested-minus-terminal
                    // (StateProjector.Project's `unfulfilledCancellationRequestExecutionIds`, and that
                    // property's own doc: an id leaves the list the moment any terminal event lands),
                    // so the guard proves only "no UNFULFILLED request" -- a CancellationRequested
                    // followed by a terminal event passes straight through it into the drop, and its
                    // rejection lands on a builder this projector already opened.
                    //
                    // #2045 removed the third producer this comment used
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
                            RequestedAtUtc: timestamp ?? DateTimeOffset.UnixEpoch, ResolvedAtUtc: timestamp,
                            ForwardedAtUtc: null);
                    }

                    break;
            }
        }

        var results = new List<ArrestLedgerEntry>(order.Count + roomEvents.Count);

        // #2073: the operator's intent fact. When the pump answered, a flow-side CancellationRequested
        // for the same id already opened a builder above, and the two are ONE request seen from both
        // sides: the flow side knows the outcome and when the pump took it, but its Reason is only
        // ever a rejection's and its stamp is the pump's, not the operator's (#2104 — the #2101 review's
        // M2: `baton cancel --reason` against a live pump rendered with no reason and the wrong time).
        // So the intent merges into that builder — its reason and its stamp win, the pump's stamp
        // moves to ForwardedAtUtc — rather than being listed twice or dropped. When no pump answered
        // (baton cancel settled the room itself, or is still waiting on a holder that never let go),
        // the intent is the only request-shaped fact there is, so it opens its own builder: Delivered
        // once any arrest-shaped terminal fact for that execution lands at or after the intent
        // (ExecutionCancelled, ExecutionFailed, or a StepRetryForeclosed naming it — whoever wrote
        // it), pending otherwise. Either way there is one builder per execution, so a repeat intent
        // merges into it (last-write-wins) instead of opening a second row.
        // "At or after" is what keeps a pre-existing settle from being credited to a later intent;
        // CancelCommand never writes an intent for an already-settled target, so in practice the
        // pending arm is the still-held-lock case and nothing else.
        var terminalStampsByExecutionId = new Dictionary<ExecutionId, List<DateTimeOffset>>();
        foreach (var entry in flowLogEntries)
        {
            if (entry is not LogEntry.FlowLogEntry { WriterUtcTimestamp: { } stampedUtc } flowLogEntry)
            {
                continue;
            }

            var settledId = flowLogEntry.Event switch
            {
                FlowEvent.ExecutionCancelled cancelled => cancelled.ExecutionId,
                FlowEvent.ExecutionFailed failed => failed.ExecutionId,
                FlowEvent.StepRetryForeclosed foreclosed => foreclosed.ForExecutionId,
                _ => (ExecutionId?)null,
            };
            if (settledId is { } id)
            {
                if (!terminalStampsByExecutionId.TryGetValue(id, out var stamps))
                {
                    stamps = [];
                    terminalStampsByExecutionId[id] = stamps;
                }

                stamps.Add(new DateTimeOffset(DateTime.SpecifyKind(stampedUtc, DateTimeKind.Utc)));
            }
        }

        foreach (var roomEvent in roomEvents)
        {
            switch (roomEvent)
            {
                case RoomEvent.ArrestIntentRecorded intent:
                    var intentExecutionId = new ExecutionId(intent.Target);
                    if (builders.TryGetValue(intentExecutionId, out var answered))
                    {
                        // A Rejected builder keeps the rejection's reason: that field renders as
                        // `rejected (<Reason>)`, the explanation of the outcome (ArrestLedgerEntry.Reason).
                        // A second intent for the same id (a re-run `baton cancel` while the first is
                        // still queued) lands here again and overwrites: last-write-wins, stated on
                        // ArrestLedgerEntry.Reason / RequestedAtUtc. It lands here whether the builder
                        // was opened by the pump's CancellationRequested or by the first intent below.
                        builders[intentExecutionId] = answered with
                        {
                            Reason = answered.Outcome == ArrestOutcome.Rejected ? answered.Reason : intent.Reason,
                            RequestedAtUtc = intent.RecordedAtUtc,
                        };
                        break;
                    }

                    // No pump answered: this intent opens the builder itself, so a repeat intent for
                    // the same id (no CancellationRequested ever landing) takes the merge above and
                    // yields ONE row, not two. Outcome is settled off the FIRST intent's stamp and is
                    // not recomputed on a repeat — CancelCommand never writes an intent for a target
                    // that already settled, so a repeat can only land while the first is still pending.
                    DateTimeOffset? settledAtUtc = terminalStampsByExecutionId.TryGetValue(intentExecutionId, out var candidates)
                        ? candidates.Where(stamp => stamp >= intent.RecordedAtUtc).Cast<DateTimeOffset?>().Min()
                        : null;
                    order.Add(intentExecutionId);
                    builders[intentExecutionId] = (
                        intent.RequestedBy, intent.Reason, settledAtUtc is null ? null : ArrestOutcome.Delivered,
                        RequestedAtUtc: intent.RecordedAtUtc, ResolvedAtUtc: settledAtUtc, ForwardedAtUtc: null);
                    break;

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

        // Built AFTER the room-event pass so a merged intent's reason and stamp are what lands here.
        foreach (var executionId in order)
        {
            var b = builders[executionId];
            results.Add(new ArrestLedgerEntry(
                executionId.Value, executionId, b.Outcome, b.RequestedBy, b.Reason, b.RequestedAtUtc, b.ResolvedAtUtc, b.ForwardedAtUtc));
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
