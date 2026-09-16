namespace Baton.Queue;

/// <summary>
/// The queue's whole scheduling policy, as one pure function (#1934 slice 1, item 2). Everything that
/// varies — the clock, the free-memory reading, the live tally, the settings — is an argument, so
/// every arm is drivable in a test with no daemon, no room, and no process spawn. The daemon service
/// around it does I/O and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>The order the gates are written in below is the order spec/baton.md §13 fixes</b>, and it is not
/// an implementation detail: it decides which reason a caller records when more than one gate is shut,
/// so reordering these <c>if</c>s changes the ledger.
/// </para>
/// <para>
/// <b>The runway hold is not evaluated here (Q5).</b> This function can therefore return
/// <see cref="QueueDecisionKind.Launch"/> for an item <c>baton dispatch</c> will still refuse; the
/// caller feeds that back as <see cref="QueueWaitReason.RunwayHeld"/>. Designed, not missing.
/// </para>
/// </remarks>
public static class QueueScheduler
{
    /// <summary>
    /// Decides what the queue should do at <paramref name="now"/>.
    /// </summary>
    /// <param name="now">
    /// The current instant. Its <see cref="DateTimeOffset.LocalDateTime"/> — read exactly once, here —
    /// is what picks the memory floor's hour band; <c>QueueSettings.FloorGbAt</c>'s own remarks state
    /// why that must not be UTC.
    /// </param>
    /// <param name="items">The queue in operator order. Existing-lifecycle transitions may pass a
    /// new-work head; operator order holds within each priority band (spec/baton.md §13).</param>
    /// <param name="liveWeight">The tally over rooms already running, built with <see cref="QueueWeights.For"/>.</param>
    /// <param name="freeGb">
    /// Free physical memory in GiB. <b>Null does not block</b> — the floor is skipped and the null is
    /// carried through to <see cref="QueueDecision.FreeGb"/> so the caller records it absent. The
    /// posture and its justification are spec/baton.md §13's; <c>RunwayGate</c>'s unmeasured admission
    /// is the precedent it follows.
    /// </param>
    /// <param name="lastLaunchAt">When the scheduler last launched, or null if it has not this process.</param>
    /// <param name="held">The <c>baton queue hold</c> flag.</param>
    public static QueueDecision Decide(
        DateTimeOffset now,
        IReadOnlyList<QueueItem> items,
        double liveWeight,
        double? freeGb,
        QueueSettings settings,
        DateTimeOffset? lastLaunchAt,
        bool held)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(settings);

        var localNow = now.LocalDateTime;
        var floorGb = settings.FloorGbAt(localNow);
        var portfolio = QueuePortfolio.From(items);
        var consumingTags = items.Where(IsActiveLifecycle)
            .OrderBy(item => item.LaunchedAt ?? DateTimeOffset.MaxValue)
            .Select(item => item.Tag).ToList();
        var noSelectionContext = new QueueDecisionContext(portfolio, null, false, consumingTags, null);

        if (held)
        {
            return QueueDecision.Wait(QueueWaitReason.Hold, null, liveWeight, freeGb, floorGb, noSelectionContext);
        }

        var head = Candidate(items);
        if (head is null)
        {
            return QueueDecision.Wait(QueueWaitReason.NoItems, null, liveWeight, freeGb, floorGb, noSelectionContext);
        }

        var candidate = Candidate(items, liveWeight, freeGb, floorGb, settings, held)!;
        var selectedBand = PriorityBandFor(candidate);
        var passedNewWorkHead = !ReferenceEquals(head, candidate)
            && PriorityBandFor(head) == QueuePriorityBand.NewWork;
        QueueWaitReason? newWorkHeadCap = IsNewLifecycle(head)
            ? portfolio.ActiveLifecycles >= settings.EffectiveMaxActiveLifecycles
                ? QueueWaitReason.LifecycleCap
                : portfolio.PrePullRequestLifecycles >= settings.EffectiveMaxPrePullRequestLifecycles
                    ? QueueWaitReason.PrePullRequestCap
                    : null
            : null;
        var decisionContext = new QueueDecisionContext(portfolio, selectedBand, passedNewWorkHead,
            consumingTags, newWorkHeadCap);

        if (lastLaunchAt is { } last && now - last < TimeSpan.FromSeconds(settings.EffectiveGapSeconds))
        {
            return QueueDecision.Wait(QueueWaitReason.Gap, candidate, liveWeight, freeGb, floorGb, decisionContext);
        }

        // One predicate for both bypasses (spec/baton.md §13), hoisted above the floor rather than
        // repeated in each condition, so the two can never diverge into a lane that skips one gate and
        // not the other.
        var bypasses = QueueWeights.BypassesCap(candidate.Role);

        if (IsNewLifecycle(candidate) && portfolio.ActiveLifecycles >= settings.EffectiveMaxActiveLifecycles)
        {
            return QueueDecision.Wait(QueueWaitReason.LifecycleCap, candidate, liveWeight, freeGb, floorGb, decisionContext);
        }

        if (IsNewLifecycle(candidate) && portfolio.PrePullRequestLifecycles >= settings.EffectiveMaxPrePullRequestLifecycles)
        {
            return QueueDecision.Wait(QueueWaitReason.PrePullRequestCap, candidate, liveWeight, freeGb, floorGb, decisionContext);
        }

        if (IsReview(candidate) && portfolio.LiveReviews >= settings.EffectiveMaxLiveReviews)
        {
            return QueueDecision.Wait(QueueWaitReason.ReviewCap, candidate, liveWeight, freeGb, floorGb, decisionContext);
        }

        if (!bypasses && BelowFloor(freeGb, floorGb))
        {
            return QueueDecision.Wait(QueueWaitReason.Memory, candidate, liveWeight, freeGb, floorGb, decisionContext);
        }

        if (!bypasses && OverCap(candidate, liveWeight, settings))
        {
            return QueueDecision.Wait(QueueWaitReason.Slots, candidate, liveWeight, freeGb, floorGb, decisionContext);
        }

        return new QueueDecision(QueueDecisionKind.Launch, null, candidate, liveWeight, freeGb, floorGb, decisionContext);
    }

    /// <summary>
    /// The item selected by finish-first flow priority, or the operator-ordered new-work head when
    /// no eligible existing-lifecycle transition is waiting. A held queue names only its head.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The finish-first bands and new-work FIFO are specified in spec/baton.md §13.</b> Two consequences
    /// are worth reading off the code, and they are deliberately asymmetric: a HELD queue returns the
    /// head, so the board keeps marking the head — a hold is indefinite and operator-set, and naming a
    /// passer that is not going anywhere either would be a promise; the GAP is seconds and is not an
    /// input here (<see cref="Decide"/> returns on it before consulting this), so during a gap the board
    /// already names the item that launches when the gap elapses.
    /// </para>
    /// <para>
    /// This extends the one-argument head picker, rather than repeating eligibility beside it, for
    /// the #1912 reason the one-argument form is <c>internal</c>: <c>QueueBoard.Project</c> marks
    /// <c>isNext</c> by calling this, so the row a person sees marked is the row the scheduler launches,
    /// by construction. The head stays the head for FIFO and reason projection; this only says which
    /// item goes next.
    /// </para>
    /// </remarks>
    internal static QueueItem? Candidate(
        IReadOnlyList<QueueItem> items,
        double liveWeight,
        double? freeGb,
        double floorGb,
        QueueSettings settings,
        bool held)
    {
        if (held)
        {
            return Candidate(items);
        }

        var portfolio = QueuePortfolio.From(items);
        var finishFirst = items.FirstOrDefault(i => IsEligible(i)
                && PriorityBandFor(i) == QueuePriorityBand.Review
                && portfolio.LiveReviews < settings.EffectiveMaxLiveReviews)
            ?? items.FirstOrDefault(i => IsEligible(i) && PriorityBandFor(i) == QueuePriorityBand.Repair)
            ?? items.FirstOrDefault(i => IsEligible(i) && PriorityBandFor(i) == QueuePriorityBand.Transition);
        if (finishFirst is not null)
        {
            return finishFirst;
        }

        // New work remains in operator order. A WIP-cap-blocked round-zero head cannot be passed by
        // later new work; only the finish-first bands above may pass it.
        return Candidate(items);
    }

    /// <summary>The floor gate for <see cref="Decide"/>.</summary>
    private static bool BelowFloor(double? freeGb, double floorGb) =>
        freeGb is { } free && free < floorGb;

    /// <summary>The slot gate for <see cref="Decide"/>. The cap
    /// is a ceiling, not a strict bound: landing exactly on it fits.</summary>
    private static bool OverCap(QueueItem item, double liveWeight, QueueSettings settings) =>
        liveWeight + QueueWeights.For(item.Role, item.Adapter) > settings.EffectiveMaxLiveWeight;

    /// <summary>
    /// The item the queue would launch next, or null when nothing is eligible: the first queued,
    /// non-external, non-<c>ready</c> item in operator order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The `ready` exclusion is HERE rather than left to the advancer that sets the stage (#1934 slice
    /// 2). <b>This is the only function that picks a candidate</b>, so a guard anywhere else would be a
    /// second reader of the same rule with the launch still coming through this one.
    /// </para>
    /// <para>
    /// It is <c>internal</c> rather than folded into <see cref="Decide"/> for exactly that reason
    /// (#1912 fix round): <c>QueueBoard.Project</c> has to mark the row a person will see launch next,
    /// and re-spelling the predicate there made the invariant above false — the board went on marking
    /// <c>isNext</c> from its own copy, which would drift silently the first time a term was added
    /// here. Calling this is what keeps the two answers one answer. Nothing but the pick lives here:
    /// the gap, memory, slot and hold gates stay inside <see cref="Decide"/>, which is still the only
    /// thing that authorizes a launch.
    /// </para>
    /// <para>
    /// This is the <b>head</b>. The item that launches next is usually the head and is sometimes the
    /// an existing-lifecycle transition selected first — the full overload is that answer, and it is the same picker
    /// extended, not a second one.
    /// </para>
    /// </remarks>
    internal static QueueItem? Candidate(IReadOnlyList<QueueItem> items) =>
        items.FirstOrDefault(IsEligible);

    /// <summary>The sole started/unretired lifecycle predicate used by scheduling and pre-launch
    /// cancellation. A historical malformed Cancelled row with prior-launch proof still occupies WIP;
    /// cancellation only releases a slot when it was truly before the first launch.</summary>
    public static bool IsActiveLifecycle(QueueItem item) =>
        item.Stage is not null
        && item.Retirement is null
        && (string.Equals(item.LifecycleGraphVersion, QueueItem.AttemptGraphVersion, StringComparison.Ordinal)
            ? item.LifecycleGraphActive == true
            : item.AttemptId is not null
            || item.ParentAttemptId is not null
            // Compatibility evidence for rows written before attempt identities existed.
            || item.RoomDirectory is { Length: > 0 }
            || item.PullRequest is not null
            || item.Round != 0
            || item.State is QueueItemState.Launched or QueueItemState.Done or QueueItemState.Failed);

    /// <summary>
    /// Records the WIP *after* a launch claim without changing the policy decision that authorized
    /// it. The selected band and head-cap explanation remain decision-time facts; counts and
    /// occupants describe the queue state committed by the claim.
    /// </summary>
    public static QueueDecisionContext ContextAfterLaunchClaim(
        QueueDecisionContext selected, IReadOnlyList<QueueItem> claimedItems)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(claimedItems);
        return selected with
        {
            Portfolio = QueuePortfolio.From(claimedItems),
            ConsumingLifecycleTags = claimedItems.Where(IsActiveLifecycle)
                .OrderBy(item => item.LaunchedAt ?? DateTimeOffset.MaxValue)
                .Select(item => item.Tag).ToList(),
        };
    }

    internal static bool IsNewLifecycle(QueueItem item) => item.Stage is not null && !IsActiveLifecycle(item);

    private static bool IsReview(QueueItem item) => item.Stage is WorkStage.Review or WorkStage.ReReview;

    internal static QueuePriorityBand PriorityBandFor(QueueItem item)
    {
        if (!IsActiveLifecycle(item))
        {
            return QueuePriorityBand.NewWork;
        }

        return item.Stage switch
        {
            WorkStage.Review or WorkStage.ReReview => QueuePriorityBand.Review,
            WorkStage.Fix or WorkStage.Continue => QueuePriorityBand.Repair,
            _ => QueuePriorityBand.Transition,
        };
    }

    /// <summary>The one candidacy predicate: queued, not external, not <c>ready</c>. Both
    /// <see cref="Candidate(IReadOnlyList{QueueItem})"/> and the pass-through pick read it.</summary>
    private static bool IsEligible(QueueItem item) =>
        item.State == QueueItemState.Queued && !item.External && item.Retirement is null && !IsReady(item);

    /// <summary>
    /// A work item the reviewer approved. <b>Never launched</b>: spec/baton.md §13's "the queue records
    /// ready and does nothing until the conductor merges or resolves it". A stage-less dispatch request
    /// can never be one, which is why this is a stage read and not a state read.
    /// <para>
    /// <c>internal</c> for the same #1912 reason <see cref="Candidate"/> is: <c>QueueBoard</c> keeps a
    /// <c>ready</c> item out of the pending table and off the candidate row, and both of those are this
    /// rule rather than a second one that resembles it.
    /// </para>
    /// </summary>
    internal static bool IsReady(QueueItem item) =>
        item.Stage is { } stage && WorkStages.IsTerminal(stage);
}

/// <summary>What <see cref="QueueScheduler.Decide"/> concluded.</summary>
/// <param name="Kind">Launch or wait. There is no third outcome: a failure is something the LAUNCH produced, not a decision.</param>
/// <param name="WaitReason">Why nothing launched; null for <see cref="QueueDecisionKind.Launch"/>.</param>
/// <param name="Item">The candidate this decision is about, or null when there was none (<see cref="QueueWaitReason.NoItems"/>, <see cref="QueueWaitReason.Hold"/>).</param>
/// <param name="LiveWeight">The tally the decision was made against — recorded, so a wait is explicable after the fact.</param>
/// <param name="FreeGb">The free-memory reading, or null when unmeasured.</param>
/// <param name="FloorGb">The floor in force for this evaluation's hour band.</param>
public sealed record QueueDecision(
    QueueDecisionKind Kind,
    QueueWaitReason? WaitReason,
    QueueItem? Item,
    double LiveWeight,
    double? FreeGb,
    double FloorGb,
    QueueDecisionContext? Context = null)
{
    internal static QueueDecision Wait(
        QueueWaitReason reason, QueueItem? item, double liveWeight, double? freeGb, double floorGb,
        QueueDecisionContext? context = null) =>
        new(QueueDecisionKind.Wait, reason, item, liveWeight, freeGb, floorGb, context);
}

public sealed record QueueDecisionContext(
    QueuePortfolio Portfolio,
    QueuePriorityBand? SelectedBand,
    bool PassedNewWorkHead,
    IReadOnlyList<string> ConsumingLifecycleTags,
    QueueWaitReason? NewWorkHeadCap)
{
    public string? OldestOccupyingLifecycleTag => ConsumingLifecycleTags.FirstOrDefault();
    public string? NewWorkHeadCapToken => NewWorkHeadCap is { } reason
        ? QueueWaitReasons.Token(reason) : null;
}

public enum QueueDecisionKind
{
    Wait,
    Launch,
}

/// <summary>
/// Why the queue waited. The vocabulary #1934 item 4 fixes, and what a ledger row's <c>reason</c>
/// carries verbatim (lower-cased, hyphenated) — <see cref="QueueWaitReasons.Token"/> is the one
/// translation.
/// </summary>
public enum QueueWaitReason
{
    /// <summary>Nothing queued.</summary>
    NoItems,

    /// <summary><c>baton queue hold</c> is in force.</summary>
    Hold,

    /// <summary>Less than <c>QueueSettings.GapSeconds</c> since the last launch.</summary>
    Gap,

    /// <summary>Free memory is below the hour band's floor.</summary>
    Memory,

    /// <summary>The candidate's weight would exceed <c>QueueSettings.MaxLiveWeight</c>.</summary>
    Slots,

    LifecycleCap,

    PrePullRequestCap,

    ReviewCap,

    /// <summary>
    /// <c>baton dispatch</c>'s own runway gate held the vendor (Q5). Never produced by
    /// <see cref="QueueScheduler.Decide"/> — only by the launch attempt it authorized — and the item
    /// stays <see cref="QueueItemState.Queued"/> for the next gap.
    /// </summary>
    RunwayHeld,
}

/// <summary>The ledger tokens for <see cref="QueueWaitReason"/>. Stated once here; nothing else
/// spells them.</summary>
public static class QueueWaitReasons
{
    public static string Token(QueueWaitReason reason) => reason switch
    {
        QueueWaitReason.NoItems => "no-items",
        QueueWaitReason.Hold => "hold",
        QueueWaitReason.Gap => "gap",
        QueueWaitReason.Memory => "memory",
        QueueWaitReason.Slots => "slots",
        QueueWaitReason.LifecycleCap => "lifecycle-cap",
        QueueWaitReason.PrePullRequestCap => "pre-pr-cap",
        QueueWaitReason.ReviewCap => "review-cap",
        QueueWaitReason.RunwayHeld => "runway-held",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown queue wait reason."),
    };
}

public enum QueuePriorityBand
{
    Review,
    Repair,
    Transition,
    NewWork,
}

public sealed record QueuePortfolio(int ActiveLifecycles, int PrePullRequestLifecycles, int LiveReviews)
{
    internal static QueuePortfolio From(IReadOnlyList<QueueItem> items)
    {
        var active = items.Where(QueueScheduler.IsActiveLifecycle).ToList();
        return new(
            active.Count,
            active.Count(item => string.Equals(item.LifecycleGraphVersion, QueueItem.AttemptGraphVersion, StringComparison.Ordinal)
                ? item.LifecycleGraphPrePullRequest == true
                : item.PullRequest is null),
            items.Count(item => string.Equals(item.LifecycleGraphVersion, QueueItem.AttemptGraphVersion, StringComparison.Ordinal)
                ? item.LifecycleGraphLiveReview == true
                : item.State == QueueItemState.Launched
                    && item.Stage is WorkStage.Review or WorkStage.ReReview));
    }
}
