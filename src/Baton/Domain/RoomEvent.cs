using System.Text.Json.Serialization;

namespace Baton.Domain;

/// <summary>
/// The <c>room.jsonl</c> event discriminated union (held-work reference lifecycle).
/// Owner tag is <c>"owner": "room"</c> on the <see cref="LogEntry"/> envelope.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "eventType")]
[JsonDerivedType(typeof(HeldWorkDispatched), "heldWorkDispatched")]
[JsonDerivedType(typeof(HeldWorkEscalated), "heldWorkEscalated")]
[JsonDerivedType(typeof(HeldWorkResolved), "heldWorkResolved")]
[JsonDerivedType(typeof(GrantRecorded), "grantRecorded")]
[JsonDerivedType(typeof(GrantAmended), "grantAmended")]
[JsonDerivedType(typeof(GrantRevoked), "grantRevoked")]
[JsonDerivedType(typeof(EscalationRaised), "escalationRaised")]
[JsonDerivedType(typeof(TurnHostDormancyEntered), "turnHostDormancyEntered")]
[JsonDerivedType(typeof(TurnHostDormancyCleared), "turnHostDormancyCleared")]
[JsonDerivedType(typeof(RuntimePermissionAsked), "runtimePermissionAsked")]
[JsonDerivedType(typeof(RuntimePermissionAnswered), "runtimePermissionAnswered")]
[JsonDerivedType(typeof(RuntimePermissionRevoked), "runtimePermissionRevoked")]
[JsonDerivedType(typeof(WorkflowSwitched), "workflowSwitched")]
[JsonDerivedType(typeof(StandingPermissionRevoked), "standingPermissionRevoked")]
[JsonDerivedType(typeof(WorkerJoined), "workerJoined")]
[JsonDerivedType(typeof(WorkerRenamed), "workerRenamed")]
[JsonDerivedType(typeof(OrchestratorAssigned), "orchestratorAssigned")]
[JsonDerivedType(typeof(ArrestRequestUnresolvable), "arrestRequestUnresolvable")]
[JsonDerivedType(typeof(ArrestRequestExpired), "arrestRequestExpired")]
public abstract record RoomEvent
{
    private RoomEvent()
    {
    }

    /// <summary>Records that a held work reference was dispatched into a workflow directory.</summary>
    public sealed record HeldWorkDispatched(
        HeldWorkRef Ref,
        string Shape,
        TimeSpan Budget,
        string DeciderIdentity) : RoomEvent;

    /// <summary>Records that held work was escalated.</summary>
    public sealed record HeldWorkEscalated(
        HeldWorkRef Ref,
        string ToWhom) : RoomEvent;

    /// <summary>Records that held work was resolved, citing the thing it was decided on.</summary>
    public sealed record HeldWorkResolved(
        HeldWorkRef Ref,
        HeldWorkCitation Citation) : RoomEvent;

    /// <summary>Records a grant given to a worker.</summary>
    public sealed record GrantRecorded(
        GrantId GrantId,
        WorkerId WorkerId,
        GrantLevel Level,
        GrantScope Scope,
        SpendBounds SpendBounds,
        string Grantor,
        DateTimeOffset Timestamp) : RoomEvent;

    /// <summary>Records an amendment to a grant.</summary>
    public sealed record GrantAmended(
        GrantId GrantId,
        GrantId AmendsGrantId,
        WorkerId WorkerId,
        GrantLevel Level,
        GrantScope Scope,
        SpendBounds SpendBounds,
        string Grantor,
        DateTimeOffset Timestamp) : RoomEvent;

    /// <summary>Records revocation of a grant.</summary>
    public sealed record GrantRevoked(
        GrantId GrantId,
        string Revoker,
        DateTimeOffset Timestamp,
        string Reason) : RoomEvent;

    /// <summary>Records an escalation raised by a worker.</summary>
    public sealed record EscalationRaised(
        WorkerId FromWorkerId,
        EscalationTrigger Trigger,
        EscalationSubject Subject,
        DateTimeOffset Timestamp) : RoomEvent;

    /// <summary>Records that the turn host entered dormancy due to consecutive failures.</summary>
    public sealed record TurnHostDormancyEntered(
        int ConsecutiveFailures,
        DateTimeOffset Timestamp) : RoomEvent
    {
        /// <summary>
        /// The <see cref="EscalationSubject.HostCondition"/> condition name a dormancy breaker
        /// raises alongside this event. Canonical here because the reader
        /// (<see cref="Projection.RoomProjector"/>, which pairs the escalation's detail onto the
        /// entered transition, #1178) keys on it, existing journals already contain it, and any
        /// writer of this event must agree on the name.
        /// </summary>
        public const string DormancyConditionName = "turn-host-dormancy";
    }

    /// <summary>Records that turn host dormancy was cleared.</summary>
    public sealed record TurnHostDormancyCleared(
        string ClearedBy,
        DateTimeOffset Timestamp) : RoomEvent;

    /// <summary>Records at ask-time when a worker's mid-turn tool call needs permission.</summary>
    public sealed record RuntimePermissionAsked(
        string PermissionRequestId,
        ExecutionId ExecutionId,
        StepId StepId,
        string WorkerId,
        string VendorTag,
        string VendorCorrelationId,
        string ToolName,
        string ToolInputJson,
        string Category,
        DateTimeOffset AskedAt) : RoomEvent;

    /// <summary>Records an answer to a runtime permission request.</summary>
    public sealed record RuntimePermissionAnswered(
        string PermissionRequestId,
        string DecisionKind,
        string? UpdatedInputJson,
        string? Reason,
        string DeciderIdentity,
        DateTimeOffset AnsweredAt) : RoomEvent;

    /// <summary>Records revocation of a runtime permission request.</summary>
    public sealed record RuntimePermissionRevoked(
        string PermissionRequestId,
        string Reason,
        DateTimeOffset RevokedAt) : RoomEvent;

    /// <summary>
    /// Records that a worker's standing permission (0055's object — a durable grant living in
    /// <c>bindings.json</c>, not an in-flight ask) was withdrawn (#1251). Deliberately a different
    /// family from <see cref="RuntimePermissionRevoked"/> above, which fires when a *pending ask*
    /// is revoked or times out — reusing that noun for a standing withdrawal would put two meanings
    /// on one event, which 0002 forbids.
    /// <para>
    /// Revoke-only: granting a standing permission is already journaled indirectly, by the decision
    /// that produced it (<see cref="RuntimePermissionAnswered"/>) — a second grant event here would
    /// restate that fact rather than record a new one.
    /// </para>
    /// </summary>
    public sealed record StandingPermissionRevoked(
        string WorkerName,
        string RevokeKind,
        string? ShellCommandPattern,
        string RevokedBy,
        DateTimeOffset RevokedAt) : RoomEvent;

    /// <summary>
    /// Records that the room's workflow was switched on or off (#1216). A room does not require a
    /// workflow (0001), and switching one off leaves every worker and skill in the room as free-form
    /// conversation partners — the design corpus calls it a "non-event", so nothing here deletes a
    /// shape, a journal, or a worker.
    /// </summary>
    /// <remarks>
    /// One event carrying <paramref name="IsOn"/>, deliberately NOT the Entered/Cleared pair
    /// <see cref="TurnHostDormancyEntered"/>/<see cref="TurnHostDormancyCleared"/> uses. That pair
    /// exists because dormancy's transitions <em>are</em> the record — <see cref="DormancyTransition"/>
    /// is surfaced to the person as history. Nothing reads a history of workflow switches, so a single
    /// case carries the same durable fact with one discriminator and one projector arm.
    ///
    /// Absence means ON. Every room that predates this event has no <c>WorkflowSwitched</c> in its
    /// journal and must keep its workflow, so <see cref="Projection.RoomState.IsWorkflowOff"/> defaults
    /// false rather than the state being written eagerly at room creation.
    /// </remarks>
    public sealed record WorkflowSwitched(
        bool IsOn,
        string SwitchedBy,
        DateTimeOffset Timestamp) : RoomEvent;

    /// <summary>Records that a <see cref="Participant"/> joined the room (0054 §1, #1305) — auto-named, with its initial vendor/model/effort.</summary>
    public sealed record WorkerJoined(
        WorkerId WorkerId,
        string Name,
        string Vendor,
        string? Model,
        string? Effort,
        DateTimeOffset Timestamp) : RoomEvent;

    /// <summary>Records a user rename of a participant (0054 §1).</summary>
    public sealed record WorkerRenamed(
        WorkerId WorkerId,
        string NewName,
        DateTimeOffset Timestamp) : RoomEvent;

    /// <summary>
    /// Records the room's orchestrator assignment — implicit at first join, or explicit reassignment
    /// thereafter (0054 §6, the control built in #592).
    /// </summary>
    /// <param name="AssignedBy">
    /// Ruling 4 (the #592 scoping pass): follows <see cref="WorkflowSwitched"/>'s <c>SwitchedBy</c>
    /// convention, hardcoded <c>"operator"</c> at the reassignment endpoint's call site. Trailing
    /// optional and null on the implicit first assignment (<c>InteractiveSessions.cs</c>'s
    /// materialization) — that assignment has no actor to name, and null doubles as the value every
    /// pre-#592 journal line deserializes to.
    /// </param>
    public sealed record OrchestratorAssigned(
        WorkerId WorkerId,
        DateTimeOffset Timestamp,
        string? AssignedBy = null) : RoomEvent;

    /// <summary>
    /// #1530: a <c>cancel.request</c> the poller rejected without ever reaching a
    /// <see cref="ExecutionId"/> a <see cref="FlowEvent.CancellationRejected"/> could be keyed on.
    /// THREE producers, all in <c>Baton.Cli.CancelRequestPoller.TickAsync</c> (#2045 — this doc said
    /// "neither shape" while the third had already shipped in #1916's fix round):
    /// <list type="bullet">
    /// <item>malformed content — no <c>Target</c> survives the parse at all, so the event's own
    /// <paramref name="Target"/> is the empty string;</item>
    /// <item>an ambiguous or absent <c>latest</c> candidate — a <c>Target</c> the resolver refused to
    /// turn into a single execution;</item>
    /// <item>a literal target id no <see cref="FlowEvent.ExecutionRequestAccepted"/> ever named (a
    /// typo'd or stale id). This one <em>does</em> resolve a syntactic
    /// <see cref="Domain.ExecutionId"/>, but no execution behind it ever existed — see
    /// <c>TickAsync</c>'s own <c>everAccepted</c> remarks for why "too late (it already settled)",
    /// the <see cref="FlowEvent.CancellationRejected"/> shape, would be a false claim for it.</item>
    /// </list>
    /// None of the three has a real execution to key a
    /// <see cref="FlowEvent.CancellationRejected"/> on, so without this, the room's own
    /// <c>room.jsonl</c> — <c>BatonPaths.RoomLogFileName</c>'s own remarks: a log the poller can
    /// append to without ever touching <c>flow.lock</c>, which is its whole premise — was the only
    /// durable home available; before this event, the only record was the ephemeral
    /// <c>.rejected</c> sibling file, overwritten by the next <c>cancel.request</c> write.
    /// </summary>
    public sealed record ArrestRequestUnresolvable(
        string Target,
        string Reason,
        DateTimeOffset RequestedAtUtc,
        DateTimeOffset RecordedAtUtc) : RoomEvent;

    /// <summary>
    /// #1530: a pending <c>cancel.request</c> swept at pump start
    /// (<c>CancelRequestFile.DeleteStalePendingRequestAsync</c>'s <c>.swept</c> outcome) because its
    /// recorded writer process is confirmed dead — a request neither delivered nor rejected, just
    /// aged out with the pump that would have serviced it gone. Same "no execution to key a
    /// <c>flow.jsonl</c> fact on" reasoning as <see cref="ArrestRequestUnresolvable"/> applies here
    /// too: a swept request may never have resolved a target at all.
    /// </summary>
    public sealed record ArrestRequestExpired(
        string Target,
        DateTimeOffset RequestedAtUtc,
        DateTimeOffset RecordedAtUtc) : RoomEvent;
}

