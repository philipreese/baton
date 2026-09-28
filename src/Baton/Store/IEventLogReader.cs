using Baton.Domain;

namespace Baton.Store;

/// <summary>
/// Reads Flow's exclusive half of the Event Store back into memory, in the order the
/// events were appended. This order is exactly the write order of a single append-only file — it
/// is not the causal-linking mechanism (that mechanism matches events across Flow's
/// and Core's logs by shared <see cref="ExecutionId"/>, never by position), but reading one log's
/// own lines back in the order it wrote them is simply what "append-only" means.
/// </summary>
public interface IEventLogReader
{
    /// <summary>
    /// Returns every complete event currently in the log. A line with no trailing newline — a
    /// write still in flight, or a crash mid-append — is not yet a complete event and is
    /// excluded rather than surfaced as a parse failure.
    /// </summary>
    Task<IReadOnlyList<FlowEvent>> ReadAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every complete Core-originated event currently in the log (the
    /// <c>events.jsonl</c> half, physically interleaved in the same file since M7 Phase 6's
    /// single-log decision) — the half <see cref="ReadAllAsync"/> deliberately excludes. M10 Phase 3
    /// reads this back to join Core's lifecycle facts (<c>ExecutionStarted</c>/<c>ExecutionExited</c>)
    /// to Flow's own intents by <see cref="Domain.ExecutionId"/> for crash reconciliation. Same
    /// completeness rule as <see cref="ReadAllAsync"/>: a torn trailing line is excluded, not
    /// surfaced as a parse failure.
    /// </summary>
    Task<IReadOnlyList<CoreEvent>> ReadAllCoreEventsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns both halves of the log from a single read pass. A caller that needs both — M10
    /// Phase 3's per-round crash reconciliation, which must consult Core's lifecycle facts
    /// alongside Flow's own projected state on every scheduling round — should use this instead of
    /// calling <see cref="ReadAllAsync"/> and <see cref="ReadAllCoreEventsAsync"/> separately, which
    /// would read and parse the same file twice for no new information.
    /// </summary>
    Task<EventLogSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns both halves of the log from a seek position (<paramref name="seekByteOffset"/>).
    /// Validates record boundary alignment (preceding '\n') and line deserialization.
    /// Falls back LOUDLY to full replay if validation fails.
    /// </summary>
    Task<EventLogSnapshot> ReadSnapshotFromOffsetAsync(long seekByteOffset, CancellationToken cancellationToken = default);
}

/// <summary>
/// The joined contents of a single log read (its two logical halves), from
/// <see cref="IEventLogReader.ReadSnapshotAsync"/> or <see cref="IEventLogReader.ReadSnapshotFromOffsetAsync"/>.
/// <see cref="UnknownEventCount"/> counts newer event kinds intentionally skipped by the tolerant
/// reader; fail-closed consumers can refuse to infer absence from an incomplete understood-event view.
/// </summary>
public sealed record EventLogSnapshot(
    IReadOnlyList<FlowEvent> FlowEvents,
    IReadOnlyList<CoreEvent> CoreEvents,
    long ByteOffset = 0,
    bool IsFallbackToFull = false,
    bool HasUnterminatedTail = false,
    int UnknownEventCount = 0);
