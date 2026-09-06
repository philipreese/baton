using System.Text.Json.Serialization;

namespace Baton.Queue;

/// <summary>
/// One item in the conductor's queue — either shape (spec/baton.md §13). A <b>dispatch request</b>
/// (#1934 slice 1, Q2 answer (a)) is a spec plus the parameters <c>baton dispatch</c> needs. A
/// <b>work item</b> (slice 2, Q2 answer (b)) is that plus the lifecycle fields below, anchored on an
/// issue, whose next dispatch the scheduler derives from the PR's and the last verdict's state.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Stage"/> is the discriminator, and it is null for a dispatch request.</b> There is no
/// second "kind" field: two fields answering one question is two answers to disagree with each other,
/// and every reader that cares asks the same one — <c>Stage is null</c> means the operator asked for
/// exactly one lane and the queue advances nothing.
/// </para>
/// <para>
/// The rule this puts on whoever edits this record, unchanged from slice 1 and now satisfied rather
/// than avoided: a field belongs here only if something reads or writes it. A field carrying a
/// lifecycle no code advances reads to every consumer as a capability the product has —
/// <c>WorkItemLifecycle</c> is the code that advances these.
/// </para>
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
    /// the room can be traced back; a work item (<see cref="Stage"/> non-null) is <em>anchored</em> on
    /// it — every brief it renders and every PR it looks for is that issue's.</summary>
    public int? Issue { get; init; }

    /// <summary>
    /// Where this item is in the lifecycle, or <see langword="null"/> for a slice-1 dispatch request —
    /// see this record's own remarks for why that null is the whole discriminator.
    /// </summary>
    public WorkStage? Stage { get; init; }

    /// <summary>
    /// The lane's branch — <c>&lt;issue&gt;-lane</c>, what <c>IssueWorktreeProvisioner</c> created.
    /// Recorded rather than re-derived at read time so an item whose branch was renamed by hand still
    /// says which branch its PR was looked for on.
    /// </summary>
    public string? Branch { get; init; }

    /// <summary>The pull request the lifecycle is tracking, once one is open on <see cref="Branch"/>.</summary>
    public int? PullRequest { get; init; }

    /// <summary>
    /// The verdict the last review produced, as an absolute path to that room's <c>verdict.json</c>.
    /// <b>Recorded, never inlined into the next brief from here</b> — the brief carries the findings'
    /// text, and spec/baton.md §13 says why a room path must not travel into one.
    /// </summary>
    public string? LastVerdict { get; init; }

    /// <summary>The fix round: 0 until the first BLOCK, then one per fix. Names nothing on disk; it is
    /// what a brief's header and the transition fact count.</summary>
    public int Round { get; init; }

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

/// <summary>
/// Where an item is with respect to <em>launching</em>. Four states, and deliberately still four: the
/// lifecycle a work item moves through is <see cref="WorkStage"/>, a separate axis, because "queued"
/// and "fix round 2" are answers to different questions and folding them into one enum would make
/// every state check ask both.
/// </summary>
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
