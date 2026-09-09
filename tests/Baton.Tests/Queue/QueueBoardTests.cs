using Baton.Queue;

namespace Baton.Tests.Queue;

/// <summary>
/// #1912 slice 1: <see cref="QueueBoard.Project"/>, the pure conductor-board projection, and
/// <see cref="PullRequestChecks.Summarize"/> beside it.
/// </summary>
public sealed class QueueBoardTests
{
    private static readonly DateTime Noon = new(2026, 9, 7, 12, 0, 0, DateTimeKind.Local);

    private static QueueItem Item(
        string tag,
        WorkStage? stage = null,
        int? issue = null,
        QueueItemState state = QueueItemState.Queued,
        int round = 0,
        int? pr = null,
        bool halted = false,
        bool external = false,
        string? adapter = null,
        string? model = null,
        string? checks = null,
        DateTimeOffset? checksObservedAt = null) => new()
        {
            Tag = tag,
            Role = stage is { } s && !WorkStages.IsTerminal(s) ? WorkStages.RoleFor(s) : "implement",
            Stage = stage,
            Issue = issue,
            State = state,
            Round = round,
            PullRequest = pr,
            Halted = halted,
            External = external,
            Adapter = adapter,
            Model = model,
            Checks = checks,
            ChecksObservedAt = checksObservedAt,
            Workspace = @"C:\w",
            SpecFile = @"C:\w\spec.md",
        };

    private static QueueBoardView Project(
        IReadOnlyList<QueueItem> items,
        bool held = false,
        IReadOnlyList<QueueLiveLane>? lanes = null,
        double? freeGb = 6.44,
        QueueDecisionEntry? lastDecision = null,
        Func<QueueItem, bool>? briefExists = null,
        Func<QueueItem, string?>? verdict = null,
        QueueSettings? settings = null) =>
        QueueBoard.Project(
            items, held, settings ?? new QueueSettings(), lanes ?? [], freeGb, Noon, lastDecision,
            briefExists ?? (_ => true), verdict ?? (_ => null));

    [Fact]
    public void Slots_report_the_cap_the_live_total_and_the_floor_beside_the_reading()
    {
        var board = Project(
            [],
            lanes:
            [
                new QueueLiveLane("/r/a", "impl", "implement", "claude", QueueWeights.For("implement", "claude")),
                new QueueLiveLane("/r/b", "codex", "implement", "codex", QueueWeights.For("implement", "codex")),
                new QueueLiveLane("/r/c", "rev", "review", "claude", QueueWeights.For("review", "claude")),
            ]);

        Assert.Equal(QueueSettings.DefaultMaxLiveWeight, board.Slots.Cap);
        Assert.Equal(2, board.Slots.Live);
        Assert.Equal(3, board.Slots.Lanes.Count);

        // Rounded, not raw: the projection file is compared byte-for-byte against the pusher's own
        // derivation and is re-read by a page every few seconds; a full-precision reading would differ
        // on every tick for no reader's benefit.
        Assert.Equal(6.4, board.Slots.FreeGb);
        Assert.Equal(QueueSettings.DefaultFloorGbDay, board.Slots.FloorGb);
        Assert.False(board.Slots.NightBand);
    }

    [Fact]
    public void An_unmeasured_free_memory_reading_stays_absent_rather_than_becoming_a_number()
    {
        Assert.Null(Project([], freeGb: null).Slots.FreeGb);

        // Control: the same call with a reading present does report one, so the assertion above is
        // about the null and not about the field never being written.
        Assert.NotNull(Project([], freeGb: 3.0).Slots.FreeGb);
    }

    [Fact]
    public void The_night_band_floor_is_reported_with_the_band_that_produced_it()
    {
        var night = QueueBoard.Project(
            [], false, new QueueSettings(), [], 1.0,
            new DateTime(2026, 9, 7, 23, 0, 0, DateTimeKind.Local), null, _ => true, _ => null);

        Assert.True(night.Slots.NightBand);
        Assert.Equal(QueueSettings.DefaultFloorGbNight, night.Slots.FloorGb);
    }

    [Fact]
    public void Every_work_stage_and_the_halted_flag_render_distinctly()
    {
        // Enumerated, never listed: a seventh WorkStage reds this test rather than rendering blank.
        var stages = Enum.GetValues<WorkStage>();
        var items = stages
            .Select(s => Item($"tag-{WorkStages.Token(s)}", stage: s, round: (int)s))
            // Halted the way the lifecycle actually writes it -- WorkItemAdvancer sets `Halted` and
            // `Failed` together, never `Halted` on a still-queued item, so a fixture that left the
            // state Queued would be asserting about a shape only the fixture can build.
            .Append(Item("tag-halted", stage: WorkStage.Fix, halted: true, state: QueueItemState.Failed))
            .ToList();

        var board = Project(items);

        foreach (var stage in stages)
        {
            var token = WorkStages.Token(stage);
            if (WorkStages.IsTerminal(stage))
            {
                // `ready` is deliberately NOT a pending row: QueueScheduler.Decide excludes it from
                // candidacy, so rendering it as queued would show work waiting that nothing will ever
                // pick up.
                Assert.DoesNotContain(board.Pending, p => p.Tag == $"tag-{token}");
                continue;
            }

            var row = Assert.Single(board.Pending, p => p.Tag == $"tag-{token}");
            Assert.Equal(token, row.Stage);
            Assert.Equal((int)stage, row.Round);
        }

        var halted = Assert.Single(board.Pending, p => p.Tag == "tag-halted");
        Assert.True(halted.Halted);
        Assert.Equal(QueueBoardWaitReasons.Halted, halted.Reason);

        // Control: the flag is what separates the two, not the stage -- the un-halted `fix` row is in
        // the same table with a different reason.
        var fix = Assert.Single(board.Pending, p => p.Tag == "tag-fix");
        Assert.False(fix.Halted);
        Assert.NotEqual(QueueBoardWaitReasons.Halted, fix.Reason);
    }

    [Fact]
    public void Only_the_candidate_carries_the_schedulers_own_verdict_and_it_is_marked_as_next()
    {
        var head = Item("head", stage: WorkStage.Implement);
        var behind = Item("behind", stage: WorkStage.Implement);
        var ledger = new QueueDecisionEntry(
            DateTimeOffset.UtcNow, "head", QueueDecisionEntry.Waited,
            QueueWaitReasons.Token(QueueWaitReason.Slots), LiveWeight: 4, FreeGb: 8, FloorGb: 2);

        var board = Project([head, behind], lastDecision: ledger);

        Assert.Equal("slots", board.Pending[0].Reason);
        Assert.True(board.Pending[0].IsNext);

        // Control: the row the scheduler never evaluated does NOT inherit its verdict.
        Assert.Equal(QueueBoardWaitReasons.Behind, board.Pending[1].Reason);
        Assert.False(board.Pending[1].IsNext);
    }

    /// <summary>
    /// #2136: at a full cap the review behind an implement head is what launches next, and the board
    /// says so — while the head keeps the `slots` reason the ledger recorded against it.
    /// </summary>
    [Fact]
    public void The_next_mark_moves_to_the_review_a_slot_blocked_head_lets_pass_and_the_head_keeps_its_reason()
    {
        var head = Item("head", stage: WorkStage.Implement, issue: 1);
        var review = Item("rev", stage: WorkStage.Review, issue: 2);
        var ledger = new QueueDecisionEntry(
            DateTimeOffset.UtcNow, "head", QueueDecisionEntry.Waited,
            QueueWaitReasons.Token(QueueWaitReason.Slots), LiveWeight: 4, FreeGb: 8, FloorGb: 2);

        var board = Project([head, review], lanes: AtCap(), lastDecision: ledger);

        Assert.Equal("slots", board.Pending[0].Reason);
        Assert.False(board.Pending[0].IsNext);
        Assert.True(board.Pending[1].IsNext);
        Assert.Equal(QueueBoardWaitReasons.Next, board.Pending[1].Reason);

        // Control: below the cap the head is next and the review is merely behind it.
        var roomy = Project([head, review], lastDecision: ledger);
        Assert.True(roomy.Pending[0].IsNext);
        Assert.False(roomy.Pending[1].IsNext);
        Assert.Equal(QueueBoardWaitReasons.Behind, roomy.Pending[1].Reason);

        // With nothing in the ledger about the head, its reason is still `slots` — the token `next`
        // belongs to the passer alone (the rule and its reason live at QueueBoard.WaitReasonFor).
        var unlogged = Project([head, review], lanes: AtCap(), lastDecision: null);
        Assert.Equal("slots", unlogged.Pending[0].Reason);
        Assert.False(unlogged.Pending[0].IsNext);
        Assert.Equal(QueueBoardWaitReasons.Next, unlogged.Pending[1].Reason);
        Assert.True(unlogged.Pending[1].IsNext);
    }

    [Theory]
    [InlineData("implement-behind")]
    [InlineData("memory-blocked")]
    [InlineData("held")]
    public void The_next_mark_stays_on_the_head_when_nothing_may_pass_it(string shape)
    {
        var head = Item("head", stage: WorkStage.Implement, issue: 1);
        var behind = shape == "implement-behind"
            ? Item("second", stage: WorkStage.Implement, issue: 2)
            : Item("rev", stage: WorkStage.Review, issue: 2);

        var board = Project(
            [head, behind],
            lanes: AtCap(),
            freeGb: shape == "memory-blocked" ? 0.1 : 8.0,
            held: shape == "held");

        Assert.True(board.Pending[0].IsNext);
        Assert.False(board.Pending[1].IsNext);
    }

    /// <summary>Live lanes summing exactly to the shipped cap, so a full implement lane is over it.</summary>
    private static QueueLiveLane[] AtCap() =>
        Enumerable.Range(0, (int)QueueSettings.DefaultMaxLiveWeight)
            .Select(i => new QueueLiveLane($"/r/{i}", $"impl-{i}", "implement", "claude", QueueWeights.For("implement", "claude")))
            .ToArray();

    [Fact]
    public void A_ledger_row_about_another_item_or_another_kind_never_becomes_this_items_reason()
    {
        var head = Item("head", stage: WorkStage.Implement);

        var otherTag = new QueueDecisionEntry(
            DateTimeOffset.UtcNow, "someone-else", QueueDecisionEntry.Waited, "memory", 0, null, 2);
        Assert.Equal(QueueBoardWaitReasons.Next, Project([head], lastDecision: otherTag).Pending[0].Reason);

        var notAWait = new QueueDecisionEntry(
            DateTimeOffset.UtcNow, "head", QueueDecisionEntry.Advanced, "review → fix: blocked", 0, null, 2);
        Assert.Equal(QueueBoardWaitReasons.Next, Project([head], lastDecision: notAWait).Pending[0].Reason);

        // Control: the same tag on a `waited` row IS read, so the two assertions above are about the
        // filters rather than about the ledger never being consulted.
        var mine = new QueueDecisionEntry(
            DateTimeOffset.UtcNow, "head", QueueDecisionEntry.Waited, "runway-held", 0, null, 2);
        Assert.Equal("runway-held", Project([head], lastDecision: mine).Pending[0].Reason);
    }

    [Fact]
    public void A_missing_brief_a_hold_and_an_external_lane_each_get_their_own_word()
    {
        var item = Item("a", stage: WorkStage.Implement);

        Assert.Equal(
            QueueBoardWaitReasons.BriefMissing,
            Project([item], briefExists: _ => false).Pending[0].Reason);
        Assert.Equal(
            QueueWaitReasons.Token(QueueWaitReason.Hold),
            Project([item], held: true).Pending[0].Reason);
        Assert.Equal(
            QueueBoardWaitReasons.External,
            Project([Item("x", stage: WorkStage.Implement, external: true)]).Pending[0].Reason);
    }

    [Fact]
    public void Twins_are_the_work_items_sharing_an_issue_and_they_render_adjacent()
    {
        var items = new[]
        {
            Item("1530-opus", stage: WorkStage.Implement, issue: 1530, adapter: "claude", model: "opus"),
            Item("unrelated", stage: WorkStage.Implement, issue: 1600),
            Item("1530-sonnet", stage: WorkStage.Implement, issue: 1530, adapter: "claude", model: "sonnet"),
        };

        var board = Project(items);

        Assert.Equal(["1530-opus", "1530-sonnet", "unrelated"], board.Pending.Select(p => p.Tag));
        Assert.Equal(1530, board.Pending[0].TwinIssue);
        Assert.Equal(1530, board.Pending[1].TwinIssue);
        Assert.Equal("claude opus", board.Pending[0].Arm);
        Assert.Equal("claude sonnet", board.Pending[1].Arm);

        // Control: the unrelated item is not marked, and reordering did not cost it its place.
        Assert.Null(board.Pending[2].TwinIssue);

        // The candidate is still the queue's own head, not the row that happens to be printed first.
        Assert.True(board.Pending[0].IsNext);
    }

    [Fact]
    public void Two_stage_less_dispatch_requests_on_one_issue_are_not_comparator_arms()
    {
        var board = Project(
        [
            Item("a", issue: 1530),
            Item("b", issue: 1530),
        ]);

        Assert.All(board.Pending, p => Assert.Null(p.TwinIssue));

        // Control: give the same two items a stage and they DO pair, so the assertion above is about
        // the stage predicate rather than about twins never being detected.
        var staged = Project(
        [
            Item("a", stage: WorkStage.Implement, issue: 1530),
            Item("b", stage: WorkStage.Implement, issue: 1530),
        ]);
        Assert.All(staged.Pending, p => Assert.Equal(1530, p.TwinIssue));
    }

    /// <summary>
    /// #1912 fix round: the mixed shape the all-stage-less case above does not reach. A one-shot
    /// <c>baton dispatch --issue 1530</c> alongside a real A/B on 1530 is an ordinary thing to have, and
    /// marking it as half of the pair would render a "twin" highlight on a row
    /// <c>OrderTwinsAdjacent</c> refuses to pull next to the pair — a pairing claim about a lane that is
    /// not part of the experiment, sitting away from it.
    /// </summary>
    [Fact]
    public void A_stage_less_item_sharing_an_issue_with_a_real_pair_is_not_marked_as_one_of_them()
    {
        var board = Project(
        [
            Item("1530-opus", stage: WorkStage.Implement, issue: 1530),
            Item("1530-sonnet", stage: WorkStage.Implement, issue: 1530),
            Item("one-shot", issue: 1530),
        ]);

        var oneShot = board.Pending.Single(p => p.Tag == "one-shot");
        Assert.Null(oneShot.TwinIssue);

        // Control, opposite polarity on the same fixture: the two real arms ARE marked, so the null
        // above is the stage gate and not twin detection failing outright on this shape.
        Assert.Equal(1530, board.Pending.Single(p => p.Tag == "1530-opus").TwinIssue);
        Assert.Equal(1530, board.Pending.Single(p => p.Tag == "1530-sonnet").TwinIssue);

        // The mark and the ordering have to agree: an unmarked row must also be the one left where it
        // was, since a marked-but-not-adjacent row is exactly the defect this covers.
        Assert.Equal(["1530-opus", "1530-sonnet", "one-shot"], board.Pending.Select(p => p.Tag));
    }

    /// <summary>
    /// #1912 fix round: <c>isNext</c> is <see cref="QueueScheduler"/>'s answer, not a second one that
    /// resembles it. The board used to re-spell the candidate predicate, so this runs both over the same
    /// fixtures and requires the same item — including the shapes that separate the predicate's terms
    /// (a <c>ready</c> head, an external head, a non-queued head).
    /// </summary>
    [Theory]
    [InlineData("plain")]
    [InlineData("ready-head")]
    [InlineData("external-head")]
    [InlineData("failed-head")]
    [InlineData("slot-blocked-head")]
    [InlineData("nothing-eligible")]
    public void The_row_marked_next_is_the_item_the_scheduler_itself_would_pick(string shape)
    {
        // #2136: the one shape where next is not the head. The live tally is handed to both sides
        // the same way the daemon and the projection writer would hand it.
        var lanes = shape == "slot-blocked-head" ? AtCap() : [];
        QueueItem[] items = shape switch
        {
            "slot-blocked-head" =>
            [
                Item("head", stage: WorkStage.Implement, issue: 1),
                Item("rev", stage: WorkStage.Review, issue: 2),
            ],
            "plain" =>
            [
                Item("a", stage: WorkStage.Implement, issue: 1),
                Item("b", stage: WorkStage.Implement, issue: 2),
            ],
            "ready-head" =>
            [
                Item("ready", stage: WorkStage.Ready, issue: 1, pr: 2028),
                Item("b", stage: WorkStage.Implement, issue: 2),
            ],
            "external-head" =>
            [
                Item("ext", stage: WorkStage.Implement, issue: 1, external: true),
                Item("b", stage: WorkStage.Implement, issue: 2),
            ],
            "failed-head" =>
            [
                Item("halted", stage: WorkStage.Implement, issue: 1, state: QueueItemState.Failed, halted: true),
                Item("b", stage: WorkStage.Implement, issue: 2),
            ],
            _ =>
            [
                Item("ready", stage: WorkStage.Ready, issue: 1, pr: 2028),
                Item("ext", stage: WorkStage.Implement, issue: 2, external: true),
            ],
        };

        var decision = QueueScheduler.Decide(
            DateTimeOffset.UtcNow, items, liveWeight: lanes.Sum(l => l.Weight), freeGb: 64, new QueueSettings(),
            lastLaunchAt: null, held: false);

        var marked = Project(items, lanes: lanes).Pending.SingleOrDefault(p => p.IsNext);

        Assert.Equal(decision.Item?.Tag, marked?.Tag);
        if (shape == "slot-blocked-head")
        {
            Assert.Equal("rev", marked?.Tag);
        }

        // Control: the fixture set actually exercises both outcomes, so the equality above is not
        // trivially satisfied by every shape having (or lacking) a candidate.
        if (shape == "nothing-eligible")
        {
            Assert.Null(decision.Item);
        }
        else
        {
            Assert.NotNull(decision.Item);
        }
    }

    [Fact]
    public void A_pr_row_exists_per_work_item_with_a_pull_request_and_carries_its_stage_verdict_and_checks()
    {
        var observedAt = DateTimeOffset.Parse("2026-09-07T11:00:00Z");
        var items = new[]
        {
            Item("a", stage: WorkStage.Review, issue: 10, pr: 2028, round: 1, checks: PullRequestChecks.Failing,
                checksObservedAt: observedAt),
            Item("b", stage: WorkStage.Ready, issue: 11, pr: 2035, round: 3, state: QueueItemState.Queued),
            Item("no-pr", stage: WorkStage.Implement, issue: 12),
        };

        var board = Project(items, verdict: i => i.Tag == "a" ? "block" : null);

        Assert.Equal([2028, 2035], board.PullRequests.Select(p => p.PullRequest));

        var first = board.PullRequests[0];
        Assert.Equal("review", first.Stage);
        Assert.Equal("block", first.Verdict);
        Assert.Equal(PullRequestChecks.Failing, first.Checks);
        Assert.Equal(observedAt, first.ChecksObservedAt);

        // A `ready` item leaves the pending table (nothing will dispatch it) but MUST stay on the PR
        // table -- it is exactly the row the conductor is looking for.
        Assert.Equal("ready", board.PullRequests[1].Stage);
        Assert.DoesNotContain(board.Pending, p => p.Tag == "b");

        // Control: an item with no PR yet produces no PR row rather than one reading `#0`.
        Assert.DoesNotContain(board.PullRequests, p => p.Tag == "no-pr");

        // Never observed stays absent on both halves together, so a reader cannot render a word with
        // no age or an age with no word.
        Assert.Null(board.PullRequests[1].Checks);
        Assert.Null(board.PullRequests[1].ChecksObservedAt);
    }

    [Fact]
    public void A_launched_item_is_not_a_pending_row()
    {
        var board = Project(
        [
            Item("running", stage: WorkStage.Implement, state: QueueItemState.Launched),
            Item("waiting", stage: WorkStage.Implement),
        ]);

        Assert.Equal(["waiting"], board.Pending.Select(p => p.Tag));
    }

    /// <summary>
    /// The board's whole point is showing what needs a person, and the item that most needs one is a
    /// halted implement lane with no PR — it has no PR row to appear on, so if the pending filter
    /// excluded it, it would be on the board nowhere at all.
    /// </summary>
    [Fact]
    public void A_halted_item_with_no_pull_request_is_still_on_the_board()
    {
        var board = Project(
            [Item("stuck", stage: WorkStage.Implement, halted: true, state: QueueItemState.Failed)]);

        var row = Assert.Single(board.Pending);
        Assert.Equal("stuck", row.Tag);
        Assert.Equal(QueueBoardWaitReasons.Halted, row.Reason);
        Assert.False(row.IsNext);
        Assert.Empty(board.PullRequests);

        // Control: an item that merely FAILED without being halted is not a pending row -- so the arm
        // above is about the flag and not about `Failed` items being listed generally. (The lifecycle
        // re-reads exactly that item on its next tick; it is not waiting on anybody.)
        Assert.Empty(Project(
            [Item("retryable", stage: WorkStage.Implement, state: QueueItemState.Failed)]).Pending);
    }

    [Theory]
    [InlineData("""[]""", PullRequestChecks.None)]
    [InlineData("""[{"name":"ci","status":"COMPLETED","conclusion":"SUCCESS"}]""", PullRequestChecks.Passing)]
    [InlineData("""[{"name":"ci","status":"COMPLETED","conclusion":"FAILURE"}]""", PullRequestChecks.Failing)]
    [InlineData("""[{"name":"ci","status":"IN_PROGRESS"}]""", PullRequestChecks.Pending)]
    [InlineData("""[{"name":"ci","status":"COMPLETED","conclusion":"SKIPPED"}]""", PullRequestChecks.Passing)]
    [InlineData("""[{"context":"legacy/status","state":"SUCCESS"}]""", PullRequestChecks.Passing)]
    [InlineData("""[{"context":"legacy/status","state":"PENDING"}]""", PullRequestChecks.Pending)]
    [InlineData("""[{"context":"legacy/status","state":"ERROR"}]""", PullRequestChecks.Failing)]
    [InlineData("""[{"name":"a","conclusion":"SUCCESS"},{"name":"b","conclusion":"FAILURE"}]""", PullRequestChecks.Failing)]
    [InlineData("""[{"name":"a","conclusion":"SUCCESS"},{"name":"b","status":"QUEUED"}]""", PullRequestChecks.Pending)]
    public void Checks_summarize_to_one_word_across_both_rollup_element_shapes(string json, string expected) =>
        Assert.Equal(expected, PullRequestChecks.Summarize(System.Text.Json.JsonDocument.Parse(json).RootElement));

    [Fact]
    public void A_rerun_supersedes_the_earlier_run_of_the_SAME_check_rather_than_being_added_to_it()
    {
        // Two elements cut from the rollup PullRequestChecks' own remarks record measuring. That type
        // is where the measurement lives, and where what it would cost to reduce naively is argued.
        const string json = """
        [
          {"name":"diff-shape","completedAt":"2026-09-07T10:17:29Z","conclusion":"FAILURE"},
          {"name":"diff-shape","completedAt":"2026-09-07T13:42:24Z","conclusion":"SUCCESS"}
        ]
        """;
        Assert.Equal(
            PullRequestChecks.Passing,
            PullRequestChecks.Summarize(System.Text.Json.JsonDocument.Parse(json).RootElement));

        // Control, opposite polarity: the same two entries with the timestamps swapped DO reduce to
        // failing, so the assertion above is about the ordering and not about FAILURE being ignored.
        const string reversed = """
        [
          {"name":"diff-shape","completedAt":"2026-09-07T13:42:24Z","conclusion":"FAILURE"},
          {"name":"diff-shape","completedAt":"2026-09-07T10:17:29Z","conclusion":"SUCCESS"}
        ]
        """;
        Assert.Equal(
            PullRequestChecks.Failing,
            PullRequestChecks.Summarize(System.Text.Json.JsonDocument.Parse(reversed).RootElement));
    }

    [Fact]
    public void An_absent_or_unreadable_rollup_is_no_answer_never_a_fabricated_one()
    {
        Assert.Null(PullRequestChecks.Summarize(null));
        Assert.Null(PullRequestChecks.Summarize(System.Text.Json.JsonDocument.Parse("null").RootElement));
        Assert.Null(PullRequestChecks.Summarize(System.Text.Json.JsonDocument.Parse("{}").RootElement));
    }
}
