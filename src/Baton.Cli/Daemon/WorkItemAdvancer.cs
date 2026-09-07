using System.Globalization;
using System.Text.Json;
using Baton.Accounting;
using Baton.Domain;
using Baton.Queue;
using Baton.Status;

namespace Baton.Cli.Daemon;

/// <summary>
/// The I/O half of #1934 slice 2: for every settled work item, read what its room, its verdict and its
/// PR say, ask <see cref="WorkItemLifecycle"/> what that means, and write the next round back onto the
/// queue with one recorded fact naming the evidence.
/// </summary>
/// <remarks>
/// <para>
/// <b>All the policy is in <see cref="WorkItemLifecycle.Decide"/>, which is pure</b> — the same split
/// <see cref="QueueSchedulerService"/> has with <c>QueueScheduler</c>. This class reads files, spawns
/// <c>gh</c> and mutates the queue; it decides nothing.
/// </para>
/// <para>
/// <b>No new process-spawn site.</b> <c>gh</c> goes through <see cref="IGhCliRunner"/>, the seam
/// <see cref="DeliveryPoller"/> already owns, and the workspace head through
/// <see cref="WorkspaceHead.CaptureAsync"/>, the <c>git</c> spawn the CLI already has.
/// <c>VendorSpawnGateTests</c>'s population is unchanged by design, not by luck.
/// </para>
/// <para>
/// <b>A <see cref="WorkStage.Ready"/> item is parked in <see cref="QueueItemState.Queued"/>, not
/// marked done</b> — spec/baton.md §13 has the ruling and what it buys. The consequence for this file:
/// nothing here stops such an item launching, because <c>QueueScheduler.Decide</c> does, and a second
/// guard here would quietly become the one that mattered.
/// </para>
/// </remarks>
public sealed class WorkItemAdvancer
{
    private readonly IGhCliRunner _gh;
    private readonly Func<string, CancellationToken, Task<string?>> _workspaceHead;

    public WorkItemAdvancer()
        : this(null, null)
    {
    }

    /// <summary>Test seam (Baton.Cli.Tests): both spawns are delegates, so every transition runs
    /// against a fixture room with no <c>gh</c>, no <c>git</c> and no network.</summary>
    internal WorkItemAdvancer(
        IGhCliRunner? gh, Func<string, CancellationToken, Task<string?>>? workspaceHead)
    {
        _gh = gh ?? new GhCliRunner();
        _workspaceHead = workspaceHead ?? ReadWorkspaceHeadAsync;
    }

    /// <summary>
    /// Advances every work item whose lane has settled. Returns the facts to record, in the order they
    /// happened — the caller appends them, because the ledger's collapse key is the scheduler's to
    /// carry across evaluations (<c>QueueDecisionLedgerStore.AppendAsync</c> says why).
    /// </summary>
    public async Task<IReadOnlyList<QueueDecisionEntry>> AdvanceAsync(
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var snapshot = await QueueStore.LoadAsync(BatonPaths.QueueFile, cancellationToken).ConfigureAwait(false);

        // Settled, staged, not already ready, and not one this advance has already given up on. State
        // rather than the sentinel: QueueSchedulerService's own done detection has already read the room
        // this tick and is the one thing that moves an item out of `launched`, so re-deriving settledness
        // here would be a second reader of the same file that can disagree with the first. The
        // `Halted` half is what stops a NeedsOperator item being re-observed on every tick forever —
        // see QueueItem.Halted for what that cost.
        var candidates = snapshot.Items
            .Where(i => i.Stage is { } stage && !WorkStages.IsTerminal(stage)
                && i.State is QueueItemState.Done or QueueItemState.Failed
                && !i.Halted
                && i.RoomDirectory is { Length: > 0 })
            .ToList();
        if (candidates.Count == 0)
        {
            return [];
        }

        var facts = new List<QueueDecisionEntry>();
        foreach (var item in candidates)
        {
            var fact = await AdvanceOneAsync(item, now, cancellationToken).ConfigureAwait(false);
            if (fact is not null)
            {
                facts.Add(fact);
            }
        }

        return facts;
    }

    private async Task<QueueDecisionEntry?> AdvanceOneAsync(
        QueueItem item, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var stage = item.Stage!.Value;
        var room = item.RoomDirectory!;
        var sentinel = await TerminalSentinelWriter.TryReadAsync(room, cancellationToken).ConfigureAwait(false);

        // A room with no sentinel that the scheduler has nonetheless resolved is one it failed for
        // never having been created (its own roomless sweep). "Failed with no outcome word" is exactly
        // what the lifecycle's not-succeeded arm reads, so the item still advances rather than sticking.
        var outcome = sentinel?.State ?? WorkflowOutcome.Failed;
        var verdictPath = FindVerdict(sentinel);
        var verdict = verdictPath is null ? null : TryReadVerdict(verdictPath);

        var pr = await ReadPullRequestAsync(item, cancellationToken).ConfigureAwait(false);
        var head = await _workspaceHead(item.Workspace, cancellationToken).ConfigureAwait(false);

        var transition = WorkItemLifecycle.Decide(new WorkItemObservation(
            stage, item.Round, item.Branch, outcome, verdict, pr.Number, pr.HeadSha, head));

        return transition.Kind switch
        {
            WorkItemTransitionKind.None => null,
            WorkItemTransitionKind.NeedsOperator =>
                await FailAsync(item, stage, transition, now, room).ConfigureAwait(false),
            WorkItemTransitionKind.Stop =>
                await StopAsync(item, stage, transition, pr, verdictPath, now, room).ConfigureAwait(false),
            WorkItemTransitionKind.Dispatch =>
                await QueueNextRoundAsync(item, stage, transition, pr, verdict, verdictPath, now, room, cancellationToken)
                    .ConfigureAwait(false),
            _ => null,
        };
    }

    /// <summary>
    /// The next round: the brief is rendered FIRST, then the item is written. A render that throws must
    /// not leave an item queued against the previous round's brief, which is the failure mode of writing
    /// the state first.
    /// </summary>
    private static async Task<QueueDecisionEntry> QueueNextRoundAsync(
        QueueItem item,
        WorkStage from,
        WorkItemTransition transition,
        (int? Number, string? HeadSha) pr,
        ReviewVerdict? verdict,
        string? verdictPath,
        DateTimeOffset now,
        string room,
        CancellationToken cancellationToken)
    {
        var next = transition.NextStage!.Value;

        // The findings travel as TEXT, never the verdict's path -- QueueBriefTemplates' own remarks
        // have the mechanism and spec/baton.md §13 the ruling.
        var findings = verdict is null ? null : QueueBriefTemplates.RenderFindings(verdict);

        // The issue's own instructions, which a continuation still needs — read off the ITEM, because
        // the brief file this would otherwise be parsed out of is rewritten every round
        // (QueueItem.Instructions' remarks).
        var brief = QueueBriefTemplates.Compose(next, item, new QueueBriefTemplates.BriefContext(
            Title: $"Implement #{item.Issue}",
            Do: item.Instructions ?? string.Empty,
            PullRequest: pr.Number ?? item.PullRequest,
            HeadSha: pr.HeadSha,
            Round: transition.Round,
            Findings: findings));

        Directory.CreateDirectory(BatonPaths.QueueSpecsDirectory);
        await File.WriteAllTextAsync(item.SpecFile, brief, cancellationToken).ConfigureAwait(false);

        await MarkAsync(item.Tag, existing => existing with
        {
            Stage = next,
            Role = WorkStages.RoleFor(next),
            Round = transition.Round,
            PullRequest = pr.Number ?? existing.PullRequest,
            LastVerdict = verdictPath ?? existing.LastVerdict,
            State = QueueItemState.Queued,
            RoomDirectory = null,
            LaunchedAt = null,
            Error = null,
        }).ConfigureAwait(false);

        return Fact(item, from, next, transition, now, room);
    }

    private static async Task<QueueDecisionEntry> StopAsync(
        QueueItem item,
        WorkStage from,
        WorkItemTransition transition,
        (int? Number, string? HeadSha) pr,
        string? verdictPath,
        DateTimeOffset now,
        string room)
    {
        await MarkAsync(item.Tag, existing => existing with
        {
            Stage = WorkStage.Ready,
            PullRequest = pr.Number ?? existing.PullRequest,
            LastVerdict = verdictPath ?? existing.LastVerdict,
            State = QueueItemState.Queued,
            RoomDirectory = null,
            LaunchedAt = null,
            Error = null,
        }).ConfigureAwait(false);

        return Fact(item, from, WorkStage.Ready, transition, now, room);
    }

    private static async Task<QueueDecisionEntry> FailAsync(
        QueueItem item,
        WorkStage from,
        WorkItemTransition transition,
        DateTimeOffset now,
        string room)
    {
        // Failed, not silently left: every arm that reaches here is one where the queue would have to
        // guess, and a guess dispatches a lane against evidence nobody checked. The reason is on the
        // item, so `baton queue list` is where the operator finds it — and `Halted` is what makes this
        // the LAST tick that reads this item, rather than the first of an unbounded run of identical
        // ones (QueueItem.Halted's own remarks). The room and the stage are left on the item, for the
        // reason spec/baton.md §13 gives.
        await MarkAsync(item.Tag, existing => existing with
        {
            Stage = from,
            State = QueueItemState.Failed,
            Error = transition.Reason,
            Halted = true,
        }).ConfigureAwait(false);

        return new QueueDecisionEntry(
            now, item.Tag, QueueDecisionEntry.Failed, transition.Reason,
            LiveWeight: 0, FreeGb: null, FloorGb: 0, Room: room);
    }

    /// <summary>
    /// The transition fact. <b>The reason carries the stage pair</b> as well as the lifecycle's own
    /// evidence, because the ledger's collapse key is <c>decision|reason|tag</c> — see
    /// <c>QueueDecisionEntry.Advanced</c>.
    /// </summary>
    private static QueueDecisionEntry Fact(
        QueueItem item, WorkStage from, WorkStage to, WorkItemTransition transition,
        DateTimeOffset now, string room) =>
        new(now, item.Tag, QueueDecisionEntry.Advanced,
            $"{WorkStages.Token(from)} → {WorkStages.Token(to)}: {transition.Reason}",
            LiveWeight: 0, FreeGb: null, FloorGb: 0, Room: room);

    private static Task MarkAsync(string tag, Func<QueueItem, QueueItem> update) =>
        QueueStore.MutateAsync(
            BatonPaths.QueueFile,
            s => s with
            {
                Items = s.Items
                    .Select(i => string.Equals(i.Tag, tag, StringComparison.Ordinal) ? update(i) : i)
                    .ToList(),
            },
            CancellationToken.None);

    /// <summary>
    /// <c>gh pr view &lt;branch&gt; --json number,headRefOid</c>, run in the item's own worktree. Every
    /// failure — no <c>gh</c>, not authenticated, no PR on the branch — is <c>(null, null)</c>, which the
    /// lifecycle reads as "no PR": that routes a stalled lane to <see cref="WorkStage.Continue"/> rather
    /// than to a review of a PR that may not exist.
    /// </summary>
    /// <remarks>
    /// <b>Exactly the two fields something reads.</b> <c>mergeStateStatus</c> was requested and never
    /// parsed (#2004 review); the queue never merges (spec/baton.md §13), so nothing here has a question
    /// mergeability answers, and a requested-but-unread field reads to the next person as one that is
    /// load-bearing somewhere.
    /// </remarks>
    private async Task<(int? Number, string? HeadSha)> ReadPullRequestAsync(
        QueueItem item, CancellationToken cancellationToken)
    {
        if (item.Branch is not { Length: > 0 } branch || !Directory.Exists(item.Workspace))
        {
            return (null, null);
        }

        var result = await _gh.RunAsync(
            item.Workspace, ["pr", "view", branch, "--json", "number,headRefOid"], cancellationToken)
            .ConfigureAwait(false);
        if (!result.Started || result.ExitCode != 0)
        {
            return (null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(result.Stdout);
            var root = document.RootElement;
            var number = root.TryGetProperty("number", out var n) && n.ValueKind == JsonValueKind.Number
                ? n.GetInt32()
                : (int?)null;
            var headSha = root.TryGetProperty("headRefOid", out var h) ? h.GetString() : null;
            return (number, headSha);
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine(
                $"WorkItemAdvancer: could not read 'gh pr view {branch}' output as JSON for '{item.Tag}': {ex.Message}");
            return (null, null);
        }
    }

    /// <summary>
    /// The verdict this room produced, if any: the sentinel's own resolved <c>Outputs</c> searched for
    /// <c>verdict.json</c>, exactly as <c>WatchFireService.BuildPayload</c> does — the engine already
    /// owns that path, so nothing here re-derives an artifacts directory.
    /// </summary>
    private static string? FindVerdict(WorkflowStatusView? sentinel)
    {
        return sentinel?.Outputs.FirstOrDefault(p => string.Equals(
            Path.GetFileName(p), CostLedgerStore.VerdictOutputName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The verdict, through <see cref="ReviewVerdictSchema.TryParse"/> and no second reader. A file that
    /// does not satisfy that one definition is null — which the lifecycle treats as "the review said
    /// nothing", never as an approval.
    /// </summary>
    private static ReviewVerdict? TryReadVerdict(string path)
    {
        try
        {
            return ReviewVerdictSchema.TryParse(File.ReadAllBytes(path), out var verdict, out _) ? verdict : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The workspace's own HEAD, or null when it cannot be read — a worktree the operator
    /// removed, or one with no commits. Null reads as "not pushed".</summary>
    private static async Task<string?> ReadWorkspaceHeadAsync(string workspace, CancellationToken cancellationToken)
    {
        try
        {
            return await WorkspaceHead.CaptureAsync(workspace, cancellationToken).ConfigureAwait(false);
        }
        catch (CliArgumentException)
        {
            return null;
        }
    }
}
