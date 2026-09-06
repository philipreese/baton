using System.Text.Json.Serialization;

namespace Baton.Queue;

/// <summary>
/// One dispatch request in the conductor's queue (#1934 slice 1, Q2 answer (a)): a spec plus the
/// parameters <c>baton dispatch</c> needs, and the state the scheduler has moved it through.
/// </summary>
/// <remarks>
/// <b>Deliberately not an issue-anchored work item</b> (Q2; spec/baton.md §13). The rule this puts on
/// whoever edits this record: a field belongs here only if something in slice 1 reads or writes it.
/// A field carrying a lifecycle no code advances reads to every consumer as a capability the product
/// has.
/// </remarks>
public sealed record QueueItem
{
    /// <summary>The operator's own name for this piece of work — also the spec filename under
    /// <c>BatonPaths.QueueSpecsDirectory</c> and the room label, so it is constrained to a slug by
    /// <see cref="QueueTag.IsValid"/> (<see cref="QueueTag.Rule"/> is that rule in words).</summary>
    public required string Tag { get; init; }

    /// <summary>The worker role to dispatch (<c>implement</c>, <c>review</c>, …) — resolved against
    /// the role catalog by <c>baton dispatch</c> itself, not validated here.</summary>
    public required string Role { get; init; }

    /// <summary>The scope class for the tier table (<see cref="QueueTierTable.ScopeClasses"/>), or
    /// null when the item names its axes explicitly instead.</summary>
    public string? ScopeClass { get; init; }

    public string? Adapter { get; init; }

    public string? Model { get; init; }

    public string? Effort { get; init; }

    /// <summary>Wall-clock ceiling forwarded as <c>--timeout</c>; null keeps the role's tier timeout.</summary>
    public int? TimeoutMinutes { get; init; }

    public int? MaxToolSteps { get; init; }

    public long? TokenBudget { get; init; }

    /// <summary>The audited runway-hold bypass forwarded as <c>--override-runway</c>. Null means the
    /// hold applies, and a held vendor makes this item WAIT rather than fail — <c>QueueScheduler</c>'s
    /// <see cref="QueueWaitReason.RunwayHeld"/> arm.</summary>
    public string? OverrideRunwayReason { get; init; }

    /// <summary>Why this item's axes differ from its tier (<c>--reason</c>). Mandatory at add time
    /// when any axis is overridden; recorded on the launch fact and on the room's bindings.</summary>
    public string? Reason { get; init; }

    /// <summary>The GitHub issue this item's worktree was provisioned from, when it was. Recorded so
    /// the room can be traced back; slice 1 derives no dispatch from it.</summary>
    public int? Issue { get; init; }

    /// <summary>The directory the worker runs in. Always set by the time an item is queued — an
    /// <c>--issue</c> item gets it from the worktree provisioned at add time.</summary>
    public required string Workspace { get; init; }

    /// <summary>Baton's own copy of the spec (<c>BatonPaths.QueueSpecFile</c>). Absolute, so a
    /// relocated <c>~/.baton</c> is a re-add rather than a silently missing file.</summary>
    public required string SpecFile { get; init; }

    public QueueItemState State { get; init; } = QueueItemState.Queued;

    /// <summary>The room this item launched into; null until it does. Present on
    /// <see cref="QueueItemState.Failed"/> too, which is what makes a failure investigable.</summary>
    public string? RoomDirectory { get; init; }

    public DateTimeOffset? AddedAt { get; init; }

    public DateTimeOffset? LaunchedAt { get; init; }

    /// <summary>Why this item is <see cref="QueueItemState.Failed"/>; null otherwise.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// True for an item the operator ran outside baton and recorded here only so its weight counts.
    /// Imported from the scratchpad shape's own <c>external</c> flag; never launched by the scheduler.
    /// </summary>
    public bool External { get; init; }

    /// <summary>
    /// The scratchpad runner's <c>pinModel</c>: this item's model is the operator's deliberate
    /// choice and must not be replaced by a tier or an adapter default. Kept as a distinct flag from
    /// "the item names a model" because an imported item can name a model it would have been happy to
    /// have upgraded; <see cref="QueueTierTable"/> never upgrades either way, so this is recorded
    /// rather than enforced — the enforcement is that no code path substitutes a model at all.
    /// </summary>
    public bool PinModel { get; init; }
}

/// <summary>Where an item is. Four states, no lifecycle — see <see cref="QueueItem"/>'s remarks.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<QueueItemState>))]
public enum QueueItemState
{
    /// <summary>Not launched yet; the scheduler will consider it.</summary>
    Queued,

    /// <summary>Dispatched into <see cref="QueueItem.RoomDirectory"/> and not yet terminal.</summary>
    Launched,

    /// <summary>Its room reached a terminal state cleanly.</summary>
    Done,

    /// <summary>
    /// Either the dispatch itself refused, or the room did not settle cleanly. A terminal state: no
    /// code path moves an item out of it, so clearing one is an operator action.
    /// </summary>
    Failed,
}
