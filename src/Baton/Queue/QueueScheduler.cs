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
    /// <param name="items">The queue, in operator order. The first <see cref="QueueItemState.Queued"/>,
    /// non-<see cref="QueueItem.External"/> item is the head; nothing reorders weighted items, and the one
    /// thing that may pass the head is stated on the <see cref="Candidate(IReadOnlyList{QueueItem}, double, double?, double, QueueSettings, bool)"/>
    /// overload (#2136).</param>
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

        if (held)
        {
            return QueueDecision.Wait(QueueWaitReason.Hold, null, liveWeight, freeGb, floorGb);
        }

        var head = Candidate(items);
        if (head is null)
        {
            return QueueDecision.Wait(QueueWaitReason.NoItems, null, liveWeight, freeGb, floorGb);
        }

        if (lastLaunchAt is { } last && now - last < TimeSpan.FromSeconds(settings.EffectiveGapSeconds))
        {
            return QueueDecision.Wait(QueueWaitReason.Gap, head, liveWeight, freeGb, floorGb);
        }

        // The head, or the one item spec/baton.md §13 lets pass it (#2136). The overload's remarks cite
        // where that rule lives; the gates below are then applied to whatever it picked, which for a
        // passer means neither shuts (it bypasses both by construction) and for the head means exactly
        // what they meant before.
        var candidate = Candidate(items, liveWeight, freeGb, floorGb, settings, held)!;

        // One predicate for both bypasses (spec/baton.md §13), hoisted above the floor rather than
        // repeated in each condition, so the two can never diverge into a lane that skips one gate and
        // not the other.
        var bypasses = QueueWeights.BypassesCap(candidate.Role);

        if (!bypasses && BelowFloor(freeGb, floorGb))
        {
            return QueueDecision.Wait(QueueWaitReason.Memory, candidate, liveWeight, freeGb, floorGb);
        }

        if (!bypasses && OverCap(candidate, liveWeight, settings))
        {
            return QueueDecision.Wait(QueueWaitReason.Slots, candidate, liveWeight, freeGb, floorGb);
        }

        return new QueueDecision(QueueDecisionKind.Launch, null, candidate, liveWeight, freeGb, floorGb);
    }

    /// <summary>
    /// The item that will actually launch once the hold and the gap clear: the head from
    /// <see cref="Candidate(IReadOnlyList{QueueItem})"/>, or — when <b>only the slot gate</b> is shut
    /// against that head — the earliest <see cref="IsEligible"/> item after it whose role
    /// <see cref="QueueWeights.BypassesCap"/>, falling back to the head when there is none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The rule — what may pass a blocked head and what may not — is spec/baton.md §13's "Operator
    /// order is the launch order" paragraph (#2136), stated there and not here.</b> Two consequences
    /// are worth reading off the code: a held queue picks the head, so the board goes on marking the
    /// head rather than a passer that is not going anywhere either; and <see cref="Decide"/> has already
    /// returned on the hold and the <see cref="QueueWaitReason.Gap"/> before it consults this.
    /// </para>
    /// <para>
    /// This is the same picker as the one-argument form, extended rather than a second predicate beside
    /// it, for the #1912 reason the one-argument form is <c>internal</c>: <c>QueueBoard.Project</c> marks
    /// <c>isNext</c> by calling this, so the row a person sees marked is the row the scheduler launches,
    /// by construction. The head stays the head — the board reads the head's own ledger reason off the
    /// one-argument form — and this only says which item goes next.
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
        var head = Candidate(items);
        if (head is null
            || held
            || QueueWeights.BypassesCap(head.Role)
            || BelowFloor(freeGb, floorGb)
            || !OverCap(head, liveWeight, settings))
        {
            return head;
        }

        return items
            .SkipWhile(i => !ReferenceEquals(i, head))
            .Skip(1)
            .FirstOrDefault(i => IsEligible(i) && QueueWeights.BypassesCap(i.Role))
            ?? head;
    }

    /// <summary>The floor gate, spelled once for <see cref="Decide"/> and the pass-through pick.</summary>
    private static bool BelowFloor(double? freeGb, double floorGb) =>
        freeGb is { } free && free < floorGb;

    /// <summary>The slot gate, spelled once for <see cref="Decide"/> and the pass-through pick. The cap
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
    /// one item allowed past it — the five-argument overload is that answer, and it is the same picker
    /// extended, not a second one.
    /// </para>
    /// </remarks>
    internal static QueueItem? Candidate(IReadOnlyList<QueueItem> items) =>
        items.FirstOrDefault(IsEligible);

    /// <summary>The one candidacy predicate: queued, not external, not <c>ready</c>. Both
    /// <see cref="Candidate(IReadOnlyList{QueueItem})"/> and the pass-through pick read it.</summary>
    private static bool IsEligible(QueueItem item) =>
        item.State == QueueItemState.Queued && !item.External && !IsReady(item);

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
    double FloorGb)
{
    internal static QueueDecision Wait(
        QueueWaitReason reason, QueueItem? item, double liveWeight, double? freeGb, double floorGb) =>
        new(QueueDecisionKind.Wait, reason, item, liveWeight, freeGb, floorGb);
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
        QueueWaitReason.RunwayHeld => "runway-held",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown queue wait reason."),
    };
}
