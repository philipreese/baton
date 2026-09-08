using System.Text.Json.Serialization;

namespace Baton.Domain;

/// <summary>
/// Core-originated lifecycle events (the <c>events.jsonl</c> half), recorded into the same
/// combined log Flow uses for its own events (M7 Phase 6's dual-log ownership decision
/// permits a single storage backend as long as per-event-type ownership still holds) but kept a
/// wholly separate type from <see cref="FlowEvent"/>. Only the Core Dispatcher writes these — it
/// mirrors the managed <c>BatonTask.EventRaised</c> callbacks — never any Flow-side mutation logic,
/// and vice versa. Deliberately minimal for M7: only the two lifecycle events this phase's acceptance
/// criteria need (<c>StdoutChunk</c>/<c>StderrChunk</c> capture is unused since M7 dispatches
/// without <c>WithCaptureOutput</c>).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "eventType")]
[JsonDerivedType(typeof(ExecutionStarted), "executionStarted")]
[JsonDerivedType(typeof(ExecutionExited), "executionExited")]
public abstract record CoreEvent
{
    private CoreEvent()
    {
    }

    /// <summary>The Core-managed process for this execution has started.</summary>
    /// <param name="ProcessStartTimeUtc">
    /// #2073: the worker process's own OS start time, read right after spawn — the ±1s pid-recycling
    /// discriminator <c>Baton.Outcomes.EngineLivenessProbe</c> needs before anyone may treat
    /// <paramref name="Pid"/> as still meaning this worker, which is what lets <c>baton cancel</c> kill
    /// it by pid rather than merely name it. Nullable: every line written before this field existed
    /// carries none, and a spawn whose start time could not be read (the child exited inside the read)
    /// records none either. A null here is exactly the probe's <c>Unknown</c>, and a caller must treat
    /// it as "do not kill", never as "not alive".
    /// </param>
    public sealed record ExecutionStarted(ExecutionId ExecutionId, uint Pid, DateTime? ProcessStartTimeUtc = null) : CoreEvent;

    /// <summary>The Core-managed process for this execution has exited.</summary>
    public sealed record ExecutionExited(ExecutionId ExecutionId, int ExitCode, CoreExitReason Reason, string? StderrTail = null) : CoreEvent;
}

/// <summary>
/// Mirrors <c>BatonTask</c>'s own <c>BatonExitReason</c> at Flow's event-log boundary, rather than
/// reusing that enum directly — this is the boundary the failure model (<c>NaturalExit</c> |
/// <c>TimedOut</c> | <c>CancelRequested</c>) is defined against, and it must serialize stably in
/// <c>flow.jsonl</c> independent of however <c>BatonExitReason</c>'s own declared values might later
/// be reordered or renumbered.
/// <para>
/// That independence is delivered by <see cref="Baton.Store.FlowEventLogJson"/> persisting this
/// by <i>name</i>, and it was not delivered before #604. Mirroring the enum decoupled the journal
/// from <c>BatonExitReason</c>'s numbering, but storing the mirror as an ordinal re-coupled it to a
/// numbering again — the declaration order immediately below. Inserting a member above <c>TimedOut</c> or
/// swapping two would have silently reinterpreted every line already on disk, which is precisely
/// what this mirror exists to prevent. The claim above now describes the code rather than the
/// intention behind it; <c>FlowEventLogJsonTests</c> asserts it so the two cannot part company again.
/// </para>
/// <para>
/// The member order is still load-bearing for journals written before #604, which carry ordinals the
/// reader still accepts — see that test class's ordinal-meaning arm. Reorder these and those lines
/// change meaning, name-persistence notwithstanding.
/// </para>
/// </summary>
public enum CoreExitReason
{
    Natural,
    TimedOut,
    CancelRequested,
}
