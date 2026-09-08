using Baton.Domain;
using Baton.Status;

namespace Baton.Tests.Status;

/// <summary>
/// #1530: <see cref="ArrestLedgerProjector.Project"/> against fabricated flow.jsonl/room.jsonl
/// entries directly. Both real call sites (<c>Baton.Cli.StatusCommand</c>,
/// <c>Baton.Cli.Mcp.FleetStatusTool</c>) read their own logs and call this method directly with
/// readers they already own; the file-reading half of that is exercised end-to-end by
/// <c>Baton.Cli.Tests.StatusCommandEndToEndTests</c>'s arrest-ledger fixture.
/// </summary>
public class ArrestLedgerViewTests
{
    private static readonly ExecutionId ExecA = new("exec-a");
    private static readonly DateTime T1 = new(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T2 = new(2026, 9, 1, 10, 0, 2, DateTimeKind.Utc);
    private static readonly DateTime T3 = new(2026, 9, 1, 10, 0, 4, DateTimeKind.Utc);

    private static LogEntry.FlowLogEntry Flow(FlowEvent e, DateTime at) => new(e, at);

    [Fact]
    public void A_request_followed_by_ExecutionCancelled_reports_Delivered()
    {
        var entries = new LogEntry[]
        {
            Flow(new FlowEvent.CancellationRequested(ExecA, CancellationOrigin.Operator), T1),
            Flow(new FlowEvent.ExecutionCancelled(ExecA), T2),
        };

        var ledger = ArrestLedgerProjector.Project(entries, []);

        var entry = Assert.Single(ledger);
        Assert.Equal(ExecA, entry.ExecutionId);
        Assert.Equal(ArrestOutcome.Delivered, entry.Outcome);
        Assert.Equal("operator", entry.RequestedBy);
        Assert.Equal(T1, entry.RequestedAtUtc.UtcDateTime);
        Assert.Equal(T2, entry.ResolvedAtUtc!.Value.UtcDateTime);
    }

    [Fact]
    public void A_request_followed_by_CancellationRejected_reports_Rejected_with_reason()
    {
        var entries = new LogEntry[]
        {
            Flow(new FlowEvent.CancellationRequested(ExecA), T1),
            Flow(new FlowEvent.CancellationRejected(ExecA, "not yet confirmed settled"), T2),
        };

        var ledger = ArrestLedgerProjector.Project(entries, []);

        var entry = Assert.Single(ledger);
        Assert.Equal(ArrestOutcome.Rejected, entry.Outcome);
        Assert.Equal("not yet confirmed settled", entry.Reason);
    }

    // Polarity control for the two arms above: a request with no terminal follow-up yet must report
    // no Outcome at all, not default to either Delivered or Rejected.
    [Fact]
    public void A_request_with_no_terminal_event_yet_reports_no_outcome()
    {
        var entries = new LogEntry[]
        {
            Flow(new FlowEvent.CancellationRequested(ExecA), T1),
        };

        var ledger = ArrestLedgerProjector.Project(entries, []);

        var entry = Assert.Single(ledger);
        Assert.Null(entry.Outcome);
        Assert.Null(entry.ResolvedAtUtc);
    }

    [Fact]
    public void HostStop_origin_renders_as_host_stop_not_operator()
    {
        var entries = new LogEntry[] { Flow(new FlowEvent.CancellationRequested(ExecA, CancellationOrigin.HostStop), T1) };

        var entry = Assert.Single(ArrestLedgerProjector.Project(entries, []));

        Assert.Equal("host-stop", entry.RequestedBy);
    }

    [Fact]
    public void ArrestRequestUnresolvable_room_event_becomes_a_Rejected_entry_with_no_ExecutionId()
    {
        var roomEvents = new RoomEvent[]
        {
            new RoomEvent.ArrestRequestUnresolvable("latest", "ambiguous — 2 candidates", T1, T2),
        };

        var entry = Assert.Single(ArrestLedgerProjector.Project([], roomEvents));

        Assert.Equal("latest", entry.Target);
        Assert.Null(entry.ExecutionId);
        Assert.Equal(ArrestOutcome.Rejected, entry.Outcome);
        Assert.Equal("ambiguous — 2 candidates", entry.Reason);
    }

    [Fact]
    public void ArrestRequestExpired_room_event_becomes_an_Expired_entry()
    {
        var roomEvents = new RoomEvent[] { new RoomEvent.ArrestRequestExpired("exec-x", T1, T2) };

        var entry = Assert.Single(ArrestLedgerProjector.Project([], roomEvents));

        Assert.Equal(ArrestOutcome.Expired, entry.Outcome);
        Assert.Null(entry.Reason);
    }

    // #2073: the operator's intent fact (RoomEvent.ArrestIntentRecorded), three arms.

    [Fact]
    public void An_intent_with_a_later_arrest_shaped_terminal_fact_reports_Delivered_with_the_operator_reason()
    {
        var flowEntries = new LogEntry[]
        {
            Flow(new FlowEvent.ExecutionFailed(ExecA, FailureClassification.Permanent, "Arrested: operator cancel"), T2),
        };
        var roomEvents = new RoomEvent[] { new RoomEvent.ArrestIntentRecorded(ExecA.Value, "operator", "looping", T1) };

        var entry = Assert.Single(ArrestLedgerProjector.Project(flowEntries, roomEvents));

        Assert.Equal(ExecA, entry.ExecutionId);
        Assert.Equal(ArrestOutcome.Delivered, entry.Outcome);
        Assert.Equal("operator", entry.RequestedBy);
        Assert.Equal("looping", entry.Reason);
        Assert.Equal(T1, entry.RequestedAtUtc.UtcDateTime);
        Assert.Equal(T2, entry.ResolvedAtUtc!.Value.UtcDateTime);
    }

    // Polarity: a terminal fact that PRECEDES the intent is not this intent's delivery, and an intent
    // with nothing after it is pending — neither may read Delivered.
    [Fact]
    public void An_intent_whose_only_terminal_fact_predates_it_reports_no_outcome()
    {
        var flowEntries = new LogEntry[]
        {
            Flow(new FlowEvent.ExecutionFailed(ExecA, FailureClassification.Permanent, "earlier"), T1),
        };
        var roomEvents = new RoomEvent[] { new RoomEvent.ArrestIntentRecorded(ExecA.Value, "operator", null, T2) };

        var entry = Assert.Single(ArrestLedgerProjector.Project(flowEntries, roomEvents));

        Assert.Null(entry.Outcome);
        Assert.Null(entry.ResolvedAtUtc);
        Assert.Null(entry.Reason);
    }

    // #2104: the designed happy path -- `baton cancel --reason` against a live pump that answers.
    // Both facts exist for one execution; the ONE entry keeps the operator's reason and request time,
    // and the pump's own CancellationRequested stamp moves to ForwardedAtUtc. Pre-#2104 this arm
    // asserted the pump's stamp as RequestedAtUtc and never looked at Reason.
    [Fact]
    public void An_intent_the_pump_answered_merges_into_one_entry_keeping_the_operator_reason_and_time()
    {
        var flowEntries = new LogEntry[]
        {
            Flow(new FlowEvent.CancellationRequested(ExecA, CancellationOrigin.Operator), T2),
            Flow(new FlowEvent.ExecutionCancelled(ExecA), T3),
        };
        var roomEvents = new RoomEvent[] { new RoomEvent.ArrestIntentRecorded(ExecA.Value, "operator", "lane is looping", T1) };

        var entry = Assert.Single(ArrestLedgerProjector.Project(flowEntries, roomEvents));

        Assert.Equal(ArrestOutcome.Delivered, entry.Outcome);
        Assert.Equal("lane is looping", entry.Reason);
        Assert.Equal(T1, entry.RequestedAtUtc.UtcDateTime);
        Assert.Equal(T2, entry.ForwardedAtUtc!.Value.UtcDateTime);
        Assert.Equal(T3, entry.ResolvedAtUtc!.Value.UtcDateTime);
    }

    // The one merge where the operator's reason does NOT win: a Rejected entry renders its Reason as
    // `rejected (<Reason>)`, so the rejection's own explanation stays. The operator's stamp still wins.
    [Fact]
    public void An_intent_the_pump_rejected_keeps_the_rejection_reason_but_the_operator_time()
    {
        var flowEntries = new LogEntry[]
        {
            Flow(new FlowEvent.CancellationRejected(ExecA, "too late (it already settled)"), T2),
        };
        var roomEvents = new RoomEvent[] { new RoomEvent.ArrestIntentRecorded(ExecA.Value, "operator", "lane is looping", T1) };

        var entry = Assert.Single(ArrestLedgerProjector.Project(flowEntries, roomEvents));

        Assert.Equal(ArrestOutcome.Rejected, entry.Outcome);
        Assert.Equal("too late (it already settled)", entry.Reason);
        Assert.Equal(T1, entry.RequestedAtUtc.UtcDateTime);
        Assert.Null(entry.ForwardedAtUtc);
    }

    // Polarity control for the merge: with only the flow-side fact (a pre-#2073 line, or a host-stop),
    // the entry renders as before -- the pump's stamp is both RequestedAtUtc and ForwardedAtUtc, and
    // there is no reason to carry.
    [Fact]
    public void A_flow_side_request_with_no_intent_fact_still_renders_the_pump_stamp_as_requested_at()
    {
        var flowEntries = new LogEntry[]
        {
            Flow(new FlowEvent.CancellationRequested(ExecA, CancellationOrigin.Operator), T2),
            Flow(new FlowEvent.ExecutionCancelled(ExecA), T3),
        };

        var entry = Assert.Single(ArrestLedgerProjector.Project(flowEntries, []));

        Assert.Equal(ArrestOutcome.Delivered, entry.Outcome);
        Assert.Null(entry.Reason);
        Assert.Equal(T2, entry.RequestedAtUtc.UtcDateTime);
        Assert.Equal(T2, entry.ForwardedAtUtc!.Value.UtcDateTime);
    }

    [Fact]
    public void Entries_are_ordered_by_RequestedAtUtc_across_both_logs()
    {
        var flowEntries = new LogEntry[] { Flow(new FlowEvent.CancellationRequested(ExecA), T2) };
        var roomEvents = new RoomEvent[] { new RoomEvent.ArrestRequestExpired("exec-x", T1, T1) };

        var ledger = ArrestLedgerProjector.Project(flowEntries, roomEvents);

        Assert.Equal(2, ledger.Count);
        Assert.Equal("exec-x", ledger[0].Target);
        Assert.Equal(ExecA.Value, ledger[1].Target);
    }
}
