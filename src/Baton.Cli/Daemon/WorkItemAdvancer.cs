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

        // Settled, staged, and not one this advance has already given up on. Ready items are included
        // even though they have no room: their persisted verdict must be reconciled against a later
        // GitHub head/check change. State
        // rather than the sentinel: QueueSchedulerService's own done detection has already read the room
        // this tick and is the one thing that moves an item out of `launched`, so re-deriving settledness
        // here would be a second reader of the same file that can disagree with the first. The
        // `Halted` half is what stops a NeedsOperator item being re-observed on every tick forever —
        // see QueueItem.Halted for what that cost.
        var candidates = snapshot.Items
            .Where(i => i.Stage is { } stage && !i.Halted
                && (stage == WorkStage.Ready
                    ? i.State == QueueItemState.Queued
                    : i.State is QueueItemState.Done or QueueItemState.Failed
                        && i.RoomDirectory is { Length: > 0 }))
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
        var room = item.RoomDirectory;
        var sentinel = room is null
            ? null
            : await TerminalSentinelWriter.TryReadAsync(room, cancellationToken).ConfigureAwait(false);

        // A room with no sentinel that the scheduler has nonetheless resolved is one it failed for
        // never having been created (its own roomless sweep). "Failed with no outcome word" is exactly
        // what the lifecycle's not-succeeded arm reads, so the item still advances rather than sticking.
        var outcome = item.Stage == WorkStage.Ready
            ? WorkflowOutcome.Succeeded
            : sentinel?.State ?? WorkflowOutcome.Failed;
        var verdictPath = item.Stage == WorkStage.Ready ? item.LastVerdict : FindVerdict(sentinel);
        var verdict = verdictPath is null ? null : TryReadVerdict(verdictPath);

        var pr = await ReadPullRequestAsync(item, cancellationToken).ConfigureAwait(false);
        var head = await _workspaceHead(item.Workspace, cancellationToken).ConfigureAwait(false);

        WorkItemObservation Observation(PullRequestObservation reading) => new(
            stage, item.Round, item.AutomaticFixUsed, item.Branch, outcome, verdict,
            reading.Number, reading.HeadSha, head, reading.Succeeded, reading.IsOpen,
            reading.IsDraft, reading.RequiredChecks);

        var transition = WorkItemLifecycle.Decide(Observation(pr));
        var readinessMutated = false;

        // A readiness mutation is never trusted from the command receipt. Re-observe the PR after
        // every attempt, then ask the pure lifecycle again. The bounded loop covers the one real
        // race: a head changes while a mark-ready is in flight, so the post-read requests mark-draft.
        // A third requested action means GitHub never converged; retain the obligation for next tick.
        for (var attempt = 0; transition.PullRequestAction != PullRequestReadinessAction.None && attempt < 3; attempt++)
        {
            if (pr.Number is not { } pullRequest)
            {
                return await RetainReconciliationAsync(
                    item, transition.Reason + " (no exact PR number was available for the mutation)",
                    pr, now, room, recordFailure: true).ConfigureAwait(false);
            }

            var args = transition.PullRequestAction == PullRequestReadinessAction.MarkDraft
                ? new[] { "pr", "ready", pullRequest.ToString(CultureInfo.InvariantCulture), "--undo" }
                : ["pr", "ready", pullRequest.ToString(CultureInfo.InvariantCulture)];
            var mutation = await _gh.RunAsync(item.Workspace, args, cancellationToken).ConfigureAwait(false);

            var after = await ReadPullRequestAsync(item, cancellationToken).ConfigureAwait(false);
            var desiredDraft = transition.PullRequestAction == PullRequestReadinessAction.MarkDraft;
            var reachedDesiredState = after.Succeeded
                && after.Number == pullRequest
                && after.IsOpen == true
                && after.IsDraft == desiredDraft;
            if (!reachedDesiredState)
            {
                var receipt = !mutation.Started
                    ? "gh did not start"
                    : $"gh exited {mutation.ExitCode}";
                var observationError = after.Error is { Length: > 0 } error ? $"; {error}" : string.Empty;
                return await RetainReconciliationAsync(
                    item,
                    $"{transition.PullRequestAction} for PR #{pullRequest} was not confirmed ({receipt}{observationError}); "
                    + "the readiness obligation remains",
                    after,
                    now,
                    room,
                    recordFailure: true).ConfigureAwait(false);
            }

            readinessMutated = true;
            pr = after;
            transition = WorkItemLifecycle.Decide(Observation(pr));
        }

        if (transition.PullRequestAction != PullRequestReadinessAction.None)
        {
            return await RetainReconciliationAsync(
                item, "GitHub readiness did not converge after three confirmed observations; the obligation remains",
                pr, now, room, recordFailure: true).ConfigureAwait(false);
        }

        // A current, green, already-ready PR and a closed/merged PR are stable terminal observations.
        // Do not rewrite queue.json or emit another transition fact on every daemon tick.
        if (stage == WorkStage.Ready
            && transition.Kind == WorkItemTransitionKind.None
            && pr.Succeeded
            && (pr.IsOpen == false
                || pr.IsOpen == true && pr.IsDraft == false
                    && pr.RequiredChecks == PullRequestChecks.Passing))
        {
            if (readinessMutated || item.Error is not null)
            {
                await MarkAsync(item.Tag, existing => existing with
                {
                    PullRequest = pr.Number ?? existing.PullRequest,
                    Checks = pr.Checks ?? existing.Checks,
                    ChecksObservedAt = pr.Checks is null ? existing.ChecksObservedAt : now,
                    Error = null,
                }).ConfigureAwait(false);
            }

            return null;
        }

        return transition.Kind switch
        {
            WorkItemTransitionKind.None =>
                await RetainReconciliationAsync(
                    item, transition.Reason, pr, now, room,
                    recordFailure: !pr.Succeeded).ConfigureAwait(false),
            WorkItemTransitionKind.NeedsOperator =>
                await FailAsync(item, stage, transition, verdictPath, now, room).ConfigureAwait(false),
            WorkItemTransitionKind.Stop =>
                await StopAsync(item, stage, transition, pr, verdictPath, now, room).ConfigureAwait(false),
            WorkItemTransitionKind.Dispatch =>
                await QueueNextRoundAsync(item, stage, transition, pr, verdict, verdictPath, now, room, cancellationToken)
                    .ConfigureAwait(false),
            _ => null,
        };
    }

    /// <summary>
    /// Keeps a settled lane eligible for the next reconciliation tick while recording the current
    /// PR/check observation and an actionable reason on the item. A failed GitHub attempt also emits
    /// a ledger fact; an ordinary pending-check wait does not pretend to be a failure.
    /// </summary>
    private static async Task<QueueDecisionEntry?> RetainReconciliationAsync(
        QueueItem item, string reason, PullRequestObservation pr, DateTimeOffset now, string? room,
        bool recordFailure)
    {
        await MarkAsync(item.Tag, existing => existing with
        {
            PullRequest = pr.Number ?? existing.PullRequest,
            Checks = pr.Checks ?? existing.Checks,
            ChecksObservedAt = pr.Checks is null ? existing.ChecksObservedAt : now,
            Error = reason,
        }).ConfigureAwait(false);

        return recordFailure
            ? new QueueDecisionEntry(
                now, item.Tag, QueueDecisionEntry.Failed, reason,
                LiveWeight: 0, FreeGb: null, FloorGb: 0, Room: room)
            : null;
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
        PullRequestObservation pr,
        ReviewVerdict? verdict,
        string? verdictPath,
        DateTimeOffset now,
        string? room,
        CancellationToken cancellationToken)
    {
        var next = transition.NextStage!.Value;

        // The findings travel as TEXT, never the verdict's path -- QueueBriefTemplates' own remarks
        // have the mechanism and spec/baton.md §13 the ruling.
        //
        // The LAST REVIEW's verdict, not the room that just settled: a fix lane produces none, so a
        // re-review dispatched straight after one rendered "(no findings were recorded)" and asked the
        // reviewer to say whether a new head closed findings it could not see (#2004 review round 1).
        // `item.LastVerdict` is the path the previous review round already recorded here, read back
        // through the same single reader below.
        var findingsVerdict = verdict ?? ReadLastVerdict(item);
        var findings = findingsVerdict is null ? null : QueueBriefTemplates.RenderFindings(findingsVerdict);

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
            // Coalesced, never assigned: a `gh` that did not run (missing, unauthenticated, no PR on
            // the branch) reports null, and overwriting a real observation with that would turn "no
            // answer this tick" into "no checks", which is a different claim. The stamp moves only when
            // the word does, so QueueItem.Checks' own "never render one without the other" rule cannot
            // be satisfied by an age that outlives its reading.
            Checks = pr.Checks ?? existing.Checks,
            ChecksObservedAt = pr.Checks is null ? existing.ChecksObservedAt : now,
            LastVerdict = verdictPath ?? existing.LastVerdict,
            // The lifecycle is the one authority that says a BLOCK may spend this budget. Preserve
            // false, true, and legacy-null through every other transition so retries and
            // continuations cannot manufacture a fix history from their shared round count.
            AutomaticFixUsed = transition.UsesAutomaticFix ? true : existing.AutomaticFixUsed,
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
        PullRequestObservation pr,
        string? verdictPath,
        DateTimeOffset now,
        string? room)
    {
        await MarkAsync(item.Tag, existing => existing with
        {
            Stage = WorkStage.Ready,
            PullRequest = pr.Number ?? existing.PullRequest,
            Checks = pr.Checks ?? existing.Checks,
            ChecksObservedAt = pr.Checks is null ? existing.ChecksObservedAt : now,
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
        string? verdictPath,
        DateTimeOffset now,
        string? room)
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
            LastVerdict = verdictPath ?? existing.LastVerdict,
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
        DateTimeOffset now, string? room) =>
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
    /// Reads the branch's PR from an all-state, head-scoped list, then reads required checks for the
    /// exact open PR and repeats the list read. The second snapshot is the stability fence: check
    /// evidence is accepted only when number, head and draft state still describe the same open PR.
    /// Command failure and malformed output are explicit failed observations, never "no PR".
    /// </summary>
    /// <remarks>
    /// Closed and merged matches are returned as such and never passed to a readiness command. An empty
    /// successful list is distinct from a failed list. The queue never merges and never reopens a PR.
    /// </remarks>
    private async Task<PullRequestObservation> ReadPullRequestAsync(
        QueueItem item, CancellationToken cancellationToken)
    {
        if (item.Branch is not { Length: > 0 } branch)
        {
            return PullRequestObservation.NoPullRequest;
        }

        if (!Directory.Exists(item.Workspace))
        {
            return PullRequestObservation.Failed($"workspace '{item.Workspace}' is unavailable");
        }

        var before = await ReadPullRequestSnapshotAsync(item.Workspace, branch, cancellationToken).ConfigureAwait(false);
        if (!before.Succeeded || before.Number is null || before.IsOpen != true)
        {
            return before;
        }

        var requiredResult = await _gh.RunAsync(
            item.Workspace,
            ["pr", "checks", before.Number.Value.ToString(CultureInfo.InvariantCulture), "--required", "--json", "bucket,name,state,workflow"],
            cancellationToken).ConfigureAwait(false);
        var required = requiredResult.Started
            ? PullRequestChecks.TrySummarizeRequired(requiredResult.Stdout)
            : null;
        if (required is null)
        {
            return PullRequestObservation.Failed(
                !requiredResult.Started
                    ? $"gh pr checks did not start for PR #{before.Number}"
                    : $"gh pr checks returned unreadable required-check evidence for PR #{before.Number}",
                before);
        }

        var after = await ReadPullRequestSnapshotAsync(item.Workspace, branch, cancellationToken).ConfigureAwait(false);
        if (!after.Succeeded)
        {
            return after;
        }

        if (after.Number != before.Number || after.IsOpen != true || after.HeadSha != before.HeadSha
            || after.IsDraft != before.IsDraft)
        {
            return PullRequestObservation.Failed(
                $"PR #{before.Number} changed while its required checks were being observed", after);
        }

        return after with { RequiredChecks = required };
    }

    private async Task<PullRequestObservation> ReadPullRequestSnapshotAsync(
        string workspace, string branch, CancellationToken cancellationToken)
    {
        var result = await _gh.RunAsync(
            workspace,
            ["pr", "list", "--head", branch, "--state", "all", "--limit", "100", "--json", "number,state,isDraft,headRefOid,statusCheckRollup"],
            cancellationToken).ConfigureAwait(false);
        if (!result.Started || result.ExitCode != 0)
        {
            return PullRequestObservation.Failed(
                !result.Started ? "gh pr list did not start" : $"gh pr list exited {result.ExitCode}");
        }

        try
        {
            using var document = JsonDocument.Parse(result.Stdout);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return PullRequestObservation.Failed("gh pr list returned a non-array JSON value");
            }

            var candidates = document.RootElement.EnumerateArray().ToList();
            if (candidates.Count == 0)
            {
                return PullRequestObservation.NoPullRequest;
            }

            if (candidates.Any(e => e.ValueKind != JsonValueKind.Object))
            {
                return PullRequestObservation.Failed("gh pr list returned a non-object pull-request entry");
            }

            var root = candidates.FirstOrDefault(e => string.Equals(Text(e, "state"), "OPEN", StringComparison.OrdinalIgnoreCase));
            if (root.ValueKind == JsonValueKind.Undefined)
            {
                root = candidates[0];
            }

            var number = root.TryGetProperty("number", out var n) && n.ValueKind == JsonValueKind.Number
                ? n.GetInt32()
                : (int?)null;
            var state = Text(root, "state");
            var isOpen = state?.ToUpperInvariant() switch
            {
                "OPEN" => true,
                "CLOSED" or "MERGED" => false,
                _ => (bool?)null,
            };
            var isDraft = root.TryGetProperty("isDraft", out var d)
                && d.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? d.GetBoolean()
                    : (bool?)null;
            var headSha = Text(root, "headRefOid");
            var checks = PullRequestChecks.Summarize(
                root.TryGetProperty("statusCheckRollup", out var rollup) ? rollup : null);

            if (number is null)
            {
                return PullRequestObservation.Failed("gh pr list returned a PR without a number");
            }

            if (isOpen is null || isDraft is null || headSha is not { Length: > 0 })
            {
                return PullRequestObservation.Failed(
                    $"gh pr list returned incomplete state for PR #{number}");
            }

            return new PullRequestObservation(true, number, headSha, isOpen, isDraft, checks, null, null);
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine(
                $"WorkItemAdvancer: could not read 'gh pr list --head {branch}' output as JSON: {ex.Message}");
            return PullRequestObservation.Failed($"gh pr list returned malformed JSON: {ex.Message}");
        }
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private sealed record PullRequestObservation(
        bool Succeeded,
        int? Number,
        string? HeadSha,
        bool? IsOpen,
        bool? IsDraft,
        string? Checks,
        string? RequiredChecks,
        string? Error)
    {
        internal static PullRequestObservation NoPullRequest { get; } =
            new(true, null, null, false, null, null, null, null);

        internal static PullRequestObservation Failed(
            string error, PullRequestObservation? last = null) =>
            new(false, last?.Number, last?.HeadSha, last?.IsOpen, last?.IsDraft,
                last?.Checks, null, error);
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

    /// <summary>
    /// The verdict the item's last review round recorded, re-read off <see cref="QueueItem.LastVerdict"/>
    /// — null when there has been no review yet, when the operator moved that room, or when the file no
    /// longer parses. A brief renders without findings in that case rather than failing the round.
    /// </summary>
    private static ReviewVerdict? ReadLastVerdict(QueueItem item) =>
        item.LastVerdict is { Length: > 0 } path && File.Exists(path) ? TryReadVerdict(path) : null;

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
