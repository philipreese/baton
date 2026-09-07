using System.Text.Json.Serialization;

namespace Baton.Queue;

/// <summary>
/// The conductor's own rows, as one pure projection (#1912 slice 1): weighted slots, the dispatch
/// queue in order, per-PR stages, and which work items are comparator arms of the same issue. What
/// <c>FleetProjectionWriter</c> puts under the projection file's <c>queue</c> key and
/// <c>glass.html</c> renders as a table under the fleet row.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure, for the same reason <see cref="QueueScheduler.Decide"/> is</b> — the clock, the free-memory
/// reading, the live room walk and the ledger's newest row are all arguments, so every row kind is
/// drivable in a test with no daemon and no rooms. The writer around it does I/O and nothing else.
/// </para>
/// <para>
/// <b>This decides nothing.</b> It never re-derives a wait reason of its own: the scheduler is the
/// only thing that evaluates the policy, and the token it recorded is read back off the ledger here
/// (<see cref="QueueBoardWaitReasons"/> names the three tokens this projection adds on top, none of
/// which is a scheduling verdict). A second evaluator would be a second answer to disagree with the
/// one that actually gated the launch.
/// </para>
/// </remarks>
public static class QueueBoard
{
    /// <summary>
    /// Builds the board.
    /// </summary>
    /// <param name="items">The queue file's items, in operator order.</param>
    /// <param name="held">The <c>baton queue hold</c> flag off the same snapshot.</param>
    /// <param name="settings">The queue settings block — the cap and the floor bands come from here.</param>
    /// <param name="liveLanes">
    /// One entry per room the projection's own walk saw Running. The caller weighs each with
    /// <see cref="QueueWeights.For"/>; this function sums what it is given rather than re-weighing, so
    /// the panel's total and the scheduler's tally are the same arithmetic over the same walk.
    /// </param>
    /// <param name="freeGb">Free physical memory in GiB, or null when unmeasured — never a stand-in
    /// number, the same posture <see cref="QueueScheduler.Decide"/> takes.</param>
    /// <param name="localNow">Local wall clock, which is what picks the floor's hour band
    /// (<see cref="QueueSettings.FloorGbAt"/> has why it must not be UTC).</param>
    /// <param name="lastDecision">The newest row of the queue's own decision ledger, or null when it
    /// has none. Only its <c>reason</c> and <c>at</c> are read — the counters beside it belong to the
    /// evaluation that wrote it, which can be hours old, while everything else here is this tick's.</param>
    /// <param name="briefExists">Whether an item's <see cref="QueueItem.SpecFile"/> is readable. The
    /// caller stats the file; a queued item whose brief is gone can never dispatch, and that is
    /// invisible in the queue file itself.</param>
    /// <param name="verdictDecision">The <c>decision</c> word of an item's last recorded verdict, or
    /// null when there is none or it no longer parses.</param>
    public static QueueBoardView Project(
        IReadOnlyList<QueueItem> items,
        bool held,
        QueueSettings settings,
        IReadOnlyList<QueueLiveLane> liveLanes,
        double? freeGb,
        DateTime localNow,
        QueueDecisionEntry? lastDecision,
        Func<QueueItem, bool> briefExists,
        Func<QueueItem, string?> verdictDecision)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(liveLanes);
        ArgumentNullException.ThrowIfNull(briefExists);
        ArgumentNullException.ThrowIfNull(verdictDecision);

        var slots = new QueueSlotsView(
            Cap: settings.EffectiveMaxLiveWeight,
            Live: Math.Round(liveLanes.Sum(l => l.Weight), 2),
            FloorGb: settings.FloorGbAt(localNow),
            FreeGb: freeGb is { } gb ? Math.Round(gb, 1) : null,
            NightBand: settings.IsNightBand(localNow),
            Lanes: liveLanes);

        // The candidate is picked the way QueueScheduler.Decide picks it and no other way -- same
        // predicate, so the row the panel marks `next` is the row the scheduler would launch. The
        // `ready` exclusion is part of that predicate, which is why a ready item is a PR row below and
        // never a pending one.
        var candidate = items.FirstOrDefault(i =>
            i.State == QueueItemState.Queued && !i.External
            && !(i.Stage is { } s && WorkStages.IsTerminal(s)));

        var pending = new List<QueuePendingView>();
        foreach (var item in OrderTwinsAdjacent(items))
        {
            if (item.State != QueueItemState.Queued || (item.Stage is { } s && WorkStages.IsTerminal(s)))
            {
                continue;
            }

            pending.Add(new QueuePendingView(
                Tag: item.Tag,
                Stage: item.Stage is { } stage ? WorkStages.Token(stage) : null,
                Round: item.Round,
                Issue: item.Issue,
                Role: item.Role,
                Adapter: item.Adapter,
                Model: item.Model,
                Reason: WaitReasonFor(item, candidate, held, lastDecision, briefExists),
                IsNext: ReferenceEquals(item, candidate),
                External: item.External,
                Halted: item.Halted,
                Arm: ArmLabel(item),
                TwinIssue: null));
        }

        var twinIssues = TwinIssues(items);
        for (var i = 0; i < pending.Count; i++)
        {
            if (pending[i].Issue is { } issue && twinIssues.Contains(issue))
            {
                pending[i] = pending[i] with { TwinIssue = issue };
            }
        }

        var pullRequests = new List<QueuePullRequestView>();
        foreach (var item in OrderTwinsAdjacent(items))
        {
            if (item.Stage is not { } stage || item.PullRequest is not { } pr)
            {
                continue;
            }

            pullRequests.Add(new QueuePullRequestView(
                Tag: item.Tag,
                PullRequest: pr,
                Stage: WorkStages.Token(stage),
                Round: item.Round,
                Issue: item.Issue,
                Branch: item.Branch,
                Verdict: verdictDecision(item),
                Checks: item.Checks,
                ChecksObservedAt: item.ChecksObservedAt,
                Halted: item.Halted,
                Arm: ArmLabel(item),
                TwinIssue: item.Issue is { } prIssue && twinIssues.Contains(prIssue) ? prIssue : null));
        }

        return new QueueBoardView(
            Held: held,
            Slots: slots,
            LastDecisionAt: lastDecision?.At,
            Pending: pending,
            PullRequests: pullRequests);
    }

    /// <summary>
    /// Why one pending item is not running, in the order the answers actually foreclose each other.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only the candidate gets the scheduler's own verdict.</b> Every other queued item is behind it
    /// by construction — the scheduler launches at most one item per evaluation and always the first
    /// eligible one — so reporting a gate token on a row the scheduler never evaluated would be a
    /// verdict nothing produced. <see cref="QueueBoardWaitReasons.Behind"/> is the honest word.
    /// </para>
    /// <para>
    /// The three answers ahead of the ledger's are ones the ledger cannot carry: <c>halted</c> and
    /// <c>brief-missing</c> are properties of the item rather than of any evaluation, and <c>external</c>
    /// marks a lane the operator runs outside baton, which the scheduler never considers at all.
    /// </para>
    /// </remarks>
    internal static string WaitReasonFor(
        QueueItem item,
        QueueItem? candidate,
        bool held,
        QueueDecisionEntry? lastDecision,
        Func<QueueItem, bool> briefExists)
    {
        if (item.Halted)
        {
            return QueueBoardWaitReasons.Halted;
        }

        if (item.External)
        {
            return QueueBoardWaitReasons.External;
        }

        if (!briefExists(item))
        {
            return QueueBoardWaitReasons.BriefMissing;
        }

        if (held)
        {
            return QueueWaitReasons.Token(QueueWaitReason.Hold);
        }

        if (!ReferenceEquals(item, candidate))
        {
            return QueueBoardWaitReasons.Behind;
        }

        // The ledger's row is used only when it is about THIS item. A `hold` or `no-items` row carries
        // no tag and cannot describe a candidate; an `advanced` or `failed` row is not a wait verdict at
        // all. Anything else and the panel says nothing rather than guessing -- the scheduler's next
        // tick is what fills it in.
        if (lastDecision is { Decision: QueueDecisionEntry.Waited, Reason: { Length: > 0 } reason }
            && string.Equals(lastDecision.Tag, item.Tag, StringComparison.Ordinal))
        {
            return reason;
        }

        return QueueBoardWaitReasons.Next;
    }

    /// <summary>
    /// Every issue carrying more than one <em>work item</em> — the comparator arms of #1912's ask.
    /// <b>Stage-bearing items only</b>: two stage-less dispatch requests naming one issue are two
    /// one-shot lanes, not an A/B, and calling them twins would invent an experiment.
    /// </summary>
    internal static IReadOnlySet<int> TwinIssues(IReadOnlyList<QueueItem> items) =>
        items
            .Where(i => i.Stage is not null && i.Issue is not null)
            .GroupBy(i => i.Issue!.Value)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet();

    /// <summary>
    /// The queue's own order, with twins pulled adjacent: an issue's items follow the first of them,
    /// and everything else keeps its relative position. <b>This is a display order, not a launch
    /// order</b> — which is why <see cref="QueuePendingView.IsNext"/> exists rather than the top row
    /// being allowed to imply it.
    /// </summary>
    internal static IEnumerable<QueueItem> OrderTwinsAdjacent(IReadOnlyList<QueueItem> items)
    {
        var twins = TwinIssues(items);
        if (twins.Count == 0)
        {
            return items;
        }

        var emitted = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<QueueItem>(items.Count);
        foreach (var item in items)
        {
            if (!emitted.Add(item.Tag))
            {
                continue;
            }

            ordered.Add(item);
            if (item.Stage is null || item.Issue is not { } issue || !twins.Contains(issue))
            {
                continue;
            }

            foreach (var sibling in items)
            {
                if (sibling.Stage is not null && sibling.Issue == issue && emitted.Add(sibling.Tag))
                {
                    ordered.Add(sibling);
                }
            }
        }

        return ordered;
    }

    /// <summary>
    /// What tells two arms of one issue apart, from fields the item already carries: the vendor axes
    /// are what an A/B varies. Null when the item names none — the tag is already on the row, so a
    /// blank arm cell is the honest reading rather than a label repeating the tag.
    /// </summary>
    internal static string? ArmLabel(QueueItem item)
    {
        var parts = new[] { item.Adapter, item.Model, item.Effort }.Where(p => p is { Length: > 0 }).ToList();
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }
}

/// <summary>
/// The three reasons a board row can carry that no scheduling evaluation produces, plus the word for
/// the row that is about to launch. <b>Deliberately not members of <see cref="QueueWaitReason"/></b>:
/// that enum is the vocabulary of the ledger and of the policy, and a token that never appears in a
/// recorded decision does not belong in it.
/// </summary>
public static class QueueBoardWaitReasons
{
    /// <summary>The candidate, with no gate shut against it as of the last recorded evaluation.</summary>
    public const string Next = "next";

    /// <summary>Queued behind the candidate — see <c>QueueBoard.WaitReasonFor</c>'s remarks.</summary>
    public const string Behind = "behind";

    /// <summary>The lifecycle gave up on this item and a person has to act (<see cref="QueueItem.Halted"/>).</summary>
    public const string Halted = "halted";

    /// <summary>The item's brief is no longer on disk, so no dispatch it authorizes could render.</summary>
    public const string BriefMissing = "brief-missing";

    /// <summary>An operator-run lane recorded only so its weight counts; the scheduler never launches it.</summary>
    public const string External = "external";
}

/// <summary>One room the projection's walk saw Running, and what it weighs against the cap.</summary>
/// <param name="Room">The room directory — the same <c>path</c> key the projection's <c>rooms[]</c> uses.</param>
/// <param name="Label">The room's own label, for a row a person can read.</param>
/// <param name="Role">The worker role, or null when the room's bindings could not be read.</param>
/// <param name="Adapter">The vendor adapter, same caveat.</param>
/// <param name="Weight">What <see cref="QueueWeights.For"/> makes of the pair — never spelled by a caller.</param>
public sealed record QueueLiveLane(
    [property: JsonPropertyName("room")] string Room,
    [property: JsonPropertyName("label")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Label,
    [property: JsonPropertyName("role")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Role,
    [property: JsonPropertyName("adapter")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Adapter,
    [property: JsonPropertyName("weight")] double Weight);

/// <summary>The weighted-slot row: the cap, what is live against it, and the memory floor beside the
/// reading it is compared with.</summary>
/// <param name="NightBand">Which of <see cref="QueueSettings"/>' two floor bands
/// <paramref name="FloorGb"/> came from — otherwise a floor that changes at 20:00 reads as a bug.</param>
public sealed record QueueSlotsView(
    [property: JsonPropertyName("cap")] double Cap,
    [property: JsonPropertyName("live")] double Live,
    [property: JsonPropertyName("floorGb")] double FloorGb,
    [property: JsonPropertyName("freeGb")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    double? FreeGb,
    [property: JsonPropertyName("nightBand")] bool NightBand,
    [property: JsonPropertyName("lanes")] IReadOnlyList<QueueLiveLane> Lanes);

/// <summary>One pending row.</summary>
/// <param name="Reason">A <see cref="QueueWaitReasons"/> token or a <see cref="QueueBoardWaitReasons"/> one.</param>
/// <param name="IsNext">The item the scheduler would launch next. The row's POSITION does not say this
/// — twins are pulled adjacent for display (<c>QueueBoard.OrderTwinsAdjacent</c>).</param>
/// <param name="TwinIssue">Set when this item shares its issue with another work item; null otherwise.</param>
public sealed record QueuePendingView(
    [property: JsonPropertyName("tag")] string Tag,
    [property: JsonPropertyName("stage")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Stage,
    [property: JsonPropertyName("round")] int Round,
    [property: JsonPropertyName("issue")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? Issue,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("adapter")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Adapter,
    [property: JsonPropertyName("model")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Model,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("isNext")] bool IsNext,
    [property: JsonPropertyName("external")] bool External,
    [property: JsonPropertyName("halted")] bool Halted,
    [property: JsonPropertyName("arm")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Arm,
    [property: JsonPropertyName("twinIssue")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? TwinIssue);

/// <summary>One PR row: where the loop has got to on a work item that has a pull request open.</summary>
/// <param name="Verdict">The <c>decision</c> word of the last verdict this item recorded; null when
/// no review has produced one yet.</param>
/// <param name="Checks">
/// The PR's checks as the advancer last observed them (<c>PullRequestChecks</c>'s tokens). <b>Never
/// this instant's</b>: the advancer only reads a work item whose lane has settled, so
/// <paramref name="ChecksObservedAt"/> is what qualifies the word and the page renders its age beside
/// it, the same way a vendor block's <c>harvestedAt</c> qualifies a percentage.
/// </param>
public sealed record QueuePullRequestView(
    [property: JsonPropertyName("tag")] string Tag,
    [property: JsonPropertyName("pr")] int PullRequest,
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("round")] int Round,
    [property: JsonPropertyName("issue")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? Issue,
    [property: JsonPropertyName("branch")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Branch,
    [property: JsonPropertyName("verdict")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Verdict,
    [property: JsonPropertyName("checks")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Checks,
    [property: JsonPropertyName("checksObservedAt")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? ChecksObservedAt,
    [property: JsonPropertyName("halted")] bool Halted,
    [property: JsonPropertyName("arm")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Arm,
    [property: JsonPropertyName("twinIssue")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? TwinIssue);

/// <summary>The whole <c>queue</c> section of the fleet projection.</summary>
/// <param name="LastDecisionAt">When the scheduler last recorded a decision. <b>Not this tick</b> —
/// the ledger collapses a repeated verdict (<c>QueueDecisionEntry.VerdictKey</c>), so a standing
/// "slots" wait can be hours old and still current; the page renders the age rather than letting the
/// reason read as fresh.</param>
public sealed record QueueBoardView(
    [property: JsonPropertyName("held")] bool Held,
    [property: JsonPropertyName("slots")] QueueSlotsView Slots,
    [property: JsonPropertyName("lastDecisionAt")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? LastDecisionAt,
    [property: JsonPropertyName("pending")] IReadOnlyList<QueuePendingView> Pending,
    [property: JsonPropertyName("pullRequests")] IReadOnlyList<QueuePullRequestView> PullRequests);
