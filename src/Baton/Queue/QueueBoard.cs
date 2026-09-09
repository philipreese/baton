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
    /// <param name="verdictDecision">Reads an item's last recorded verdict; null when there is none.
    /// A delegate rather than a field on the item, because a verdict is a file on disk and this
    /// function does no I/O.</param>
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

        // The scheduler's OWN pick, called rather than re-spelled -- QueueScheduler.Candidate's remarks
        // are why it is exposed at all. The row the panel marks `next` is therefore the row the
        // scheduler would launch by construction, not by two copies of a predicate agreeing today.
        // Two answers off the one picker (#2136): the head, whose own ledger reason is what a person
        // wants explained, and the item that will actually go next -- the head, or the one review
        // spec/baton.md §13 lets pass a head blocked on slots alone. The live tally handed in is the
        // unrounded sum, the same arithmetic the scheduler's gate sees; `slots.Live` is the display copy.
        var head = QueueScheduler.Candidate(items);
        var next = QueueScheduler.Candidate(items, liveLanes.Sum(l => l.Weight), freeGb, slots.FloorGb, settings, held);

        var pending = new List<QueuePendingView>();
        foreach (var item in OrderTwinsAdjacent(items))
        {
            // Queued, OR halted. The second half is not a convenience: the lifecycle only ever writes
            // `Halted` together with `QueueItemState.Failed` (WorkItemAdvancer's own fail arm), so a
            // filter on Queued alone would put every item the queue has given up on nowhere at all
            // unless it happened to have a PR open -- and an implement lane halted before it opened one
            // is exactly the item a person has to act on. It is listed here rather than launched: what
            // admits an item to a dispatch is QueueScheduler.Candidate above, which this filter neither
            // is nor widens.
            //
            // The `ready` exclusion is QueueScheduler.IsReady, called rather than re-spelled -- a ready
            // item is a PR row below and never a pending one, and that is the SAME rule that keeps it
            // off the candidate row, not a second rule resembling it.
            var stuck = item.Halted;
            if ((item.State != QueueItemState.Queued && !stuck) || QueueScheduler.IsReady(item))
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
                Reason: WaitReasonFor(item, head, next, held, lastDecision, briefExists),
                IsNext: ReferenceEquals(item, next),
                External: item.External,
                Halted: item.Halted,
                Arm: ArmLabel(item),
                TwinIssue: null));
        }

        // Issue membership AND a stage, both -- the stage read is not redundant with TwinIssues. That
        // set is computed over stage-bearing items only (its own summary says why), so a THIRD item on
        // the same issue with no stage would otherwise pick up a pair highlight it is not half of:
        // OrderTwinsAdjacent gates on a stage too and refuses to pull it alongside the arms, leaving a
        // row marked as a pair sitting away from the pair. Same gate the PR table already applies by
        // requiring a stage to build a row at all.
        var twinIssues = TwinIssues(items);
        for (var i = 0; i < pending.Count; i++)
        {
            if (pending[i].Stage is not null && pending[i].Issue is { } issue && twinIssues.Contains(issue))
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
    /// <b>Only the head and the item going next get the scheduler's own verdict.</b> Those are the only
    /// rows the scheduler evaluates — the head always, and the one review spec/baton.md §13 lets pass a
    /// slot-blocked head (#2136), which is usually the head itself — so reporting a gate token on any other row
    /// would be a verdict nothing produced. <see cref="QueueBoardWaitReasons.Behind"/> is the honest
    /// word. The head keeps its own reason while a passer goes ahead of it: the ledger row about the
    /// head is still about the head.
    /// </para>
    /// <para>
    /// The three answers ahead of the ledger's are ones the ledger cannot carry: <c>halted</c> and
    /// <c>brief-missing</c> are properties of the item rather than of any evaluation, and <c>external</c>
    /// marks a lane the operator runs outside baton, which the scheduler never considers at all.
    /// </para>
    /// </remarks>
    internal static string WaitReasonFor(
        QueueItem item,
        QueueItem? head,
        QueueItem? next,
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

        if (!ReferenceEquals(item, head) && !ReferenceEquals(item, next))
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
    /// <summary>The head or the item going next, with no gate shut against it as of the last recorded evaluation.</summary>
    public const string Next = "next";

    /// <summary>Queued behind the head and not the item going next — see <c>QueueBoard.WaitReasonFor</c>'s remarks.</summary>
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
/// <param name="Verdict">Whatever the caller's <c>verdictDecision</c> delegate returned — its own
/// implementation is where the reading rule lives (<c>FleetProjectionWriter.ReadVerdictDecision</c>).</param>
/// <param name="Checks">Copied verbatim off <see cref="QueueItem.Checks"/>, whose remarks are the
/// register for what it means and why it must never be rendered without
/// <paramref name="ChecksObservedAt"/>.</param>
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
