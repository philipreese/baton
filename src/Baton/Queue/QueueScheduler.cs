namespace Baton.Queue;

/// <summary>
/// The queue's whole scheduling policy, as one pure function (#1934 slice 1, item 2). Everything that
/// varies — the clock, the free-memory reading, the live tally, the settings — is an argument, so
/// every arm is drivable in a test with no daemon, no room, and no process spawn. The daemon service
/// around it does I/O and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>The gate order is the cheap-and-certain-first order</b>, and it is load-bearing for the recorded
/// fact: hold, then no-items, then gap, then memory, then slots. An operator who has held the queue
/// should read <c>hold</c> in the ledger, not <c>memory</c> — the hold is why nothing launched, and
/// whichever gate happens to also be closed is not.
/// </para>
/// <para>
/// <b>The runway hold is not evaluated here.</b> Q5: the queue never reads <c>/usage</c>;
/// <c>baton dispatch</c>'s own runway gate is the only one, so a hold is discovered by attempting the
/// launch and is fed back as <see cref="QueueWaitReason.RunwayHeld"/> by the caller. This function
/// therefore returns <see cref="QueueDecisionKind.Launch"/> for an item the vendor may still refuse;
/// that is the design, not a gap.
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
    /// Free physical memory in GiB, or null when it could not be measured. Null does NOT block: it is
    /// recorded as unmeasured and the floor is not applied, the same posture <c>RunwayGate</c> already
    /// takes for a vendor with no harvested snapshot. A gate that halts the whole queue on every
    /// non-Windows host, where no reading exists at all, would be a worse failure than the one it
    /// guards against.
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

        var candidate = items.FirstOrDefault(i => i.State == QueueItemState.Queued && !i.External);
        if (candidate is null)
        {
            return QueueDecision.Wait(QueueWaitReason.NoItems, null, liveWeight, freeGb, floorGb);
        }

        if (lastLaunchAt is { } last && now - last < TimeSpan.FromSeconds(settings.EffectiveGapSeconds))
        {
            return QueueDecision.Wait(QueueWaitReason.Gap, candidate, liveWeight, freeGb, floorGb);
        }

        // A review lane bypasses the memory floor for the same reason it bypasses the cap: it is not
        // what consumes the memory the floor protects. Checked before the floor rather than folded
        // into it so the two bypasses read as one rule with one predicate.
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
