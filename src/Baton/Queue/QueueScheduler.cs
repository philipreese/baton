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
    /// non-<see cref="QueueItem.External"/> item is the candidate; nothing reorders or prioritizes.</param>
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

        // The `ready` exclusion is HERE rather than left to the advancer that sets the stage (#1934
        // slice 2). This is the only function that picks a candidate, so a guard anywhere else would
        // be a second reader of the same rule with the launch still coming through this one.
        var candidate = items.FirstOrDefault(i =>
            i.State == QueueItemState.Queued && !i.External && !IsReady(i));
        if (candidate is null)
        {
            return QueueDecision.Wait(QueueWaitReason.NoItems, null, liveWeight, freeGb, floorGb);
        }

        if (lastLaunchAt is { } last && now - last < TimeSpan.FromSeconds(settings.EffectiveGapSeconds))
        {
            return QueueDecision.Wait(QueueWaitReason.Gap, candidate, liveWeight, freeGb, floorGb);
        }

        // One predicate for both bypasses (spec/baton.md §13), hoisted above the floor rather than
        // repeated in each condition, so the two can never diverge into a lane that skips one gate and
        // not the other.
        var bypasses = QueueWeights.BypassesCap(candidate.Role);

        if (!bypasses && freeGb is { } free && free < floorGb)
        {
            return QueueDecision.Wait(QueueWaitReason.Memory, candidate, liveWeight, freeGb, floorGb);
        }

        var candidateWeight = QueueWeights.For(candidate.Role, candidate.Adapter);
        if (!bypasses && liveWeight + candidateWeight > settings.EffectiveMaxLiveWeight)
        {
            return QueueDecision.Wait(QueueWaitReason.Slots, candidate, liveWeight, freeGb, floorGb);
        }

        return new QueueDecision(QueueDecisionKind.Launch, null, candidate, liveWeight, freeGb, floorGb);
    }

    /// <summary>
    /// A work item the reviewer approved. <b>Never launched</b>: spec/baton.md §13's "the queue records
    /// ready and does nothing until the conductor merges or resolves it". A stage-less dispatch request
    /// can never be one, which is why this is a stage read and not a state read.
    /// </summary>
    private static bool IsReady(QueueItem item) =>
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
