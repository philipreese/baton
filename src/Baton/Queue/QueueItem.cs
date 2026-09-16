using System.Text.Json.Serialization;
using Baton.Domain;

namespace Baton.Queue;

/// <summary>
/// One item in the conductor's queue, in either of the two shapes spec/baton.md §13 defines — a
/// slice-1 dispatch request, or a slice-2 issue-anchored work item, which is the same record plus the
/// lifecycle fields below.
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

    private TaskSizeDeclaration _declaredTaskSize = TaskSizeDeclaration.Unknown;

    /// <summary>The frozen conductor routing declaration; absent or null legacy JSON is projected explicitly as unknown.</summary>
    public TaskSizeDeclaration DeclaredTaskSize
    {
        get => _declaredTaskSize;
        init => _declaredTaskSize = value;
    }

    /// <summary>
    /// Normalized explicit skill package names for an ordinary single-dispatch item. Null is the
    /// backward-compatible shape for entries written before queue skill declarations existed.
    /// Lifecycle items refuse this field at admission until a stage policy is explicitly defined.
    /// </summary>
    public IReadOnlyList<string>? Skills { get; init; }

    /// <summary>
    /// Explicit capabilities this task needs. Null is a compatibility row whose requirements are
    /// unknown; an empty list is an explicit declaration of none. <see cref="TaskRequirements"/>
    /// owns the vocabulary and normalization.
    /// </summary>
    public IReadOnlyList<string>? Requirements { get; init; }

    /// <summary>
    /// The most recent local admission comparison. Kept on the item as well as the append-only queue
    /// ledger so the current queue row stays inspectable after the role catalog changes.
    /// </summary>
    public TaskRequirementAdmission? LastAdmission { get; init; }

    /// <summary>
    /// Per-stage axes for a lifecycle item. Null, rather than an empty list, is significant for a
    /// pre-stage-selection persisted item; see <see cref="QueueTierTable.ResolveForStage"/>.
    /// </summary>
    public IReadOnlyList<QueueStageSelection>? StageSelections { get; init; }

    /// <summary>
    /// An explicit whole-lifecycle pin. When true, <see cref="Adapter"/>, <see cref="Model"/> and
    /// <see cref="Effort"/> apply to every lifecycle stage. It is distinct from merely naming axes
    /// for the initial implement stage.
    /// </summary>
    public bool LifecyclePin { get; init; }

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
    /// The exact branch selected by <c>IssueWorktreeProvisioner</c> (first lane
    /// <c>&lt;issue&gt;-lane</c>, or its collision suffix). Recorded rather than re-derived at read time
    /// so PR discovery follows the provisioned lane.
    /// </summary>
    public string? Branch { get; init; }

    /// <summary>
    /// The immutable canonical <c>host/owner/repo</c> identity that owns an issue-provisioned
    /// workspace, captured from the source repository when the item is added. Null on explicit-workspace
    /// requests and on historical lifecycle rows; the latter is uncertainty, never permission to infer
    /// an owner from mutable workspace or CLI context. Lifecycle-only readers also require
    /// <see cref="Stage"/>; repository provenance does not opt a dispatch request into that lifecycle.
    /// </summary>
    public string? Repository { get; init; }

    /// <summary>The pull request the lifecycle is tracking, once one is open on <see cref="Branch"/>.</summary>
    public int? PullRequest { get; init; }

    /// <summary>
    /// Opaque ownership token for a readiness reconciliation that has crossed its local linearization
    /// point. While present, cancellation and same-tag replacement must not supersede the row: the
    /// already-authorized GitHub mutation is allowed to finish and commit its observation first.
    /// </summary>
    /// <remarks>
    /// The token is durable so a daemon restart can atomically replace an orphaned claim and recover
    /// after re-observing GitHub. It is not a lease and no network call runs while the queue lock is
    /// held. <c>WorkItemAdvancer</c> is the sole writer; <c>QueueCommand</c> is the command-side reader.
    /// </remarks>
    public string? ReadinessMutationClaim { get; init; }

    /// <summary>A retained disposition, atomically recording kind, time, and reason.</summary>
    public QueueRetirement? Retirement { get; init; }

    /// <summary>
    /// Legacy single-operation shape. New writers retain every committed operation in
    /// <see cref="DispositionOperations"/>; this remains readable for queue files written before the
    /// ordered outbox existed.
    /// </summary>
    public QueueDispositionOperation? DispositionOperation { get; init; }

    /// <summary>
    /// Ordered durable outbox of committed retirement and restoration facts. Operations are retained
    /// after acknowledgement: a queue CAS must never make an earlier append failure unrecoverable
    /// when a later disposition is requested.
    /// </summary>
    public IReadOnlyList<QueueDispositionOperation>? DispositionOperations { get; init; }

    /// <summary>All durable disposition facts, including the backward-compatible single-operation shape.</summary>
    [JsonIgnore]
    public IReadOnlyList<QueueDispositionOperation> DispositionOutbox =>
        DispositionOperations ?? (DispositionOperation is null ? [] : [DispositionOperation]);

    /// <summary>
    /// The verdict the last review produced, as an absolute path to that room's <c>verdict.json</c>.
    /// <b>Recorded, never inlined into the next brief from here</b> — the brief carries the findings'
    /// text, and spec/baton.md §13 says why a room path must not travel into one.
    /// </summary>
    public string? LastVerdict { get; init; }

    /// <summary>
    /// How many rounds the queue has run for this item: 0 at add time, and <c>WorkItemLifecycle</c>
    /// raises it whenever it queues another, whatever the stage (spec/baton.md §13 has the counting rule
    /// and why it is not per-fix). Names nothing on disk; it is what a brief's header and the transition
    /// fact print, and what <see cref="WorkStages.MaxRounds"/> bounds.
    /// </summary>
    public int Round { get; init; }

    /// <summary>
    /// Whether this item has used the one automatic fix the conductor queue may dispatch after a
    /// blocking review. <see langword="false"/> is recorded for every newly-added lifecycle item;
    /// <see langword="true"/> is written atomically with that first fix dispatch. A null value is a
    /// pre-#2131 item with no trustworthy fix-budget history and must fail closed if a review blocks.
    /// </summary>
    /// <remarks>
    /// This is intentionally separate from <see cref="Round"/>. Continuations and re-review retries
    /// consume rounds too, so their count cannot prove whether the automatic fix was already used.
    /// <see cref="WorkItemLifecycle"/> is the sole policy reader and <c>WorkItemAdvancer</c> is the
    /// sole lifecycle writer.
    /// </remarks>
    public bool? AutomaticFixUsed { get; init; }

    /// <summary>
    /// The issue's own instructions — the text that became the implement brief's "## Do" section,
    /// captured once at add time.
    /// </summary>
    /// <remarks>
    /// <b>Kept on the item rather than re-read from the brief.</b> <see cref="SpecFile"/> is REWRITTEN
    /// every round, so parsing "## Do" back out of it returns the fix brief's own instructions from
    /// round 2 onward — the issue's would survive exactly one round, and a continuation would be handed
    /// "address each finding above" as if it were the work. Absent on an imported item and on every
    /// item added before this field existed; a continuation renders without it rather than failing.
    /// </remarks>
    public string? Instructions { get; init; }

    /// <summary>
    /// What the PR's checks were doing the last time the advancer looked (<see cref="PullRequestChecks"/>'s
    /// tokens). Null until one has.
    /// </summary>
    /// <remarks>
    /// <b>Recorded rather than read at display time</b>, and it is deliberately only the advancer's
    /// latest observation: settled lanes and ready-item reconciliation can refresh it, but no display
    /// read spawns GitHub. <see cref="ChecksObservedAt"/> is what makes that legible, and no reader may
    /// render one without the other — a stale green with no age beside it is a mechanism reading as a
    /// guarantee. Nothing gates on this field; readiness uses a separate required-check observation.
    /// This field exists because #1912's board row asks "what is this PR waiting on" and a checks word
    /// is half that answer.
    /// </remarks>
    public string? Checks { get; init; }

    /// <summary>When <see cref="Checks"/> was read. Absent whenever that is.</summary>
    public DateTimeOffset? ChecksObservedAt { get; init; }

    /// <summary>
    /// The PR head commit that <see cref="Checks"/> describes. Null on historical rows written before
    /// this evidence was retained; readers must say "commit unknown" rather than associating those
    /// checks with a later observed head.
    /// </summary>
    public string? ChecksHeadSha { get; init; }

    /// <summary>A bounded wait for GitHub to materialize required-check evidence for an open PR head.</summary>
    public RequiredCheckEvidenceWait? RequiredCheckEvidenceWait { get; init; }

    /// <summary>The directory the worker runs in. Always set by the time an item is queued — an
    /// <c>--issue</c> item gets it from the worktree provisioned at add time.</summary>
    public required string Workspace { get; init; }

    /// <summary>
    /// Durable workspace origin: <see cref="WorkspaceOrigins.IssueProvisioned"/>,
    /// <see cref="WorkspaceOrigins.OperatorSupplied"/>, <see cref="WorkspaceOrigins.ImportedUnknown"/>,
    /// or <see langword="null"/> for historical unknown rows (#2318). Never inferred from names, paths,
    /// issue links, or Git state; historical null is interpreted as unknown.
    /// </summary>
    public string? WorkspaceOrigin { get; init; }

    /// <summary>Baton's own copy of the spec (<c>BatonPaths.QueueSpecFile</c>). Absolute, so a
    /// relocated <c>~/.baton</c> is a re-add rather than a silently missing file.</summary>
    public required string SpecFile { get; init; }

    public QueueItemState State { get; init; } = QueueItemState.Queued;

    /// <summary>The room this item launched into; null until it does. Present on
    /// <see cref="QueueItemState.Failed"/> too, which is what makes a failure investigable.</summary>
    public string? RoomDirectory { get; init; }

    public DateTimeOffset? AddedAt { get; init; }

    public DateTimeOffset? LaunchedAt { get; init; }

    /// <summary>
    /// Stable fleet-history identity for the current launch attempt. Generated in the same queue
    /// mutation that claims the launch, before a room or vendor process exists; null on historical
    /// rows and while no attempt has been claimed.
    /// </summary>
    public FleetAttemptId? AttemptId { get; init; }

    /// <summary>
    /// The workspace HEAD captured immediately before the current launch was claimed. It is the
    /// producer-owned baseline used to decide whether this attempt actually produced a revision;
    /// null on historical rows, review attempts, and whenever the local git probe had no answer.
    /// </summary>
    public string? AttemptBaseRevision { get; init; }

    /// <summary>The immediately preceding lifecycle attempt, when this row was queued from one.</summary>
    public FleetAttemptId? ParentAttemptId { get; init; }

    /// <summary>When an operator cancelled this request before launch. Its item and spec remain in the
    /// queue history; cancellation is a fact, not deletion.</summary>
    public DateTimeOffset? CancelledAt { get; init; }

    /// <summary>Why this item is <see cref="QueueItemState.Failed"/>; null otherwise.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// The one halted lifecycle recovery that may re-observe an exact open PR. This is a closed,
    /// persisted discriminator; <see cref="Error"/> is an explanation, not scheduler state.
    /// Null on historical rows and on all other operator halts.
    /// </summary>
    public QueueReconciliationKind? ReconciliationKind { get; init; }

    /// <summary>
    /// True once the lifecycle has failed this work item with a reason a person has to act on —
    /// <c>WorkItemLifecycle</c>'s <c>NeedsOperator</c> arms, including the
    /// <see cref="WorkStages.MaxRounds"/> ceiling. <b>The flag the advance's candidate filter reads.</b>
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="QueueItemState.Failed"/> deliberately: a room that settled badly leaves
    /// the item failed too, and THAT item is exactly the one the advance still has to read (it is how a
    /// timed-out lane reaches its continue round). Without a flag telling the two apart, an item the
    /// queue had already given up on matched the candidate filter on every tick forever — same stage,
    /// same failed state, same recorded room — re-spawning <c>gh</c> and <c>git</c> and rewriting
    /// <c>queue.json</c> each time, and invisibly, for the reason spec/baton.md §13 gives (#2004
    /// review). Only a typed <see cref="ReconciliationKind"/> permits a later exact PR observation;
    /// every other halted row remains terminal until explicit retirement.
    /// </remarks>
    public bool Halted { get; init; }

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

/// <summary>The closed set of automatic reconciliations permitted after an operator halt.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<QueueReconciliationKind>))]
public enum QueueReconciliationKind
{
    /// <summary>A settled incomplete lane lacked an exact open PR; a later verified PR may resume it.</summary>
    AwaitingVerifiedPullRequest,
}

/// <summary>Why a lifecycle row is retained as history rather than active attention.</summary>
public sealed record QueueRetirement(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("at")] DateTimeOffset At,
    [property: JsonPropertyName("reason")] string Reason)
{
    public const string Merged = "merged";
    public const string Operator = "operator";
}

/// <summary>One queue-committed retirement or restoration awaiting (or retaining) its ledger fact.</summary>
public sealed record QueueDispositionOperation(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("at")] DateTimeOffset At,
    [property: JsonPropertyName("decision")] string Decision,
    [property: JsonPropertyName("reason")] string Reason);

/// <summary>Durable retry history for an open PR head whose required checks have not appeared yet.</summary>
public sealed record RequiredCheckEvidenceWait(
    [property: JsonPropertyName("headSha")] string HeadSha,
    [property: JsonPropertyName("firstUnreadableAt")] DateTimeOffset FirstUnreadableAt,
    [property: JsonPropertyName("latestObservationAt")] DateTimeOffset LatestObservationAt,
    [property: JsonPropertyName("attemptCount")] int AttemptCount,
    [property: JsonPropertyName("reason")] string Reason);

/// <summary>
/// Where an item is with respect to <em>launching</em>. Five states: cancellation is terminal, while the
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

    /// <summary>The operator cancelled this request before the scheduler claimed its launch.</summary>
    Cancelled,
}

/// <summary>Durable workspace origin values persisted on queue items (#2318).</summary>
public static class WorkspaceOrigins
{
    /// <summary>Baton queue add with an issue positively provisioned or reused the workspace.</summary>
    public const string IssueProvisioned = "issue-provisioned";

    /// <summary>The operator supplied an explicit workspace path.</summary>
    public const string OperatorSupplied = "operator-supplied";

    /// <summary>A queue import supplied a workspace without Baton creation evidence.</summary>
    public const string ImportedUnknown = "imported-unknown";

    /// <summary>Interpretation token for historical null provenance.</summary>
    public const string Unknown = "unknown";
}
