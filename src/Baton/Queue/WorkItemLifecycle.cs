using Baton.Domain;

namespace Baton.Queue;

/// <summary>
/// What a settled work-item lane means for the item's next dispatch (#1934 slice 2), as one pure
/// function. Every input is an argument — the room's terminal outcome word, the review's verdict, the
/// PR's head and the workspace's head — so all five arms are drivable with no room, no <c>gh</c> and
/// no daemon.
/// </summary>
/// <remarks>
/// <para>
/// <b>The lifecycle this encodes is the conductor's, run by hand ~40 times in the week before it was
/// written</b> (spec/baton.md §13 "Work items"): implement → PR → review → APPROVE ⇒ ready (the
/// conductor merges; the queue never does) | BLOCK ⇒ fix → re-review → …
/// </para>
/// <para>
/// <b>Architecture Rule 1 and where this sits with respect to it.</b> The Flow engine may never route
/// on what a worker wrote; <see cref="ReviewVerdict"/>'s own doc says severity and status are evidence
/// for a person, never inputs to routing. This is not the Flow engine, and spec/baton.md §13 carries
/// that distinction as an explicit ruling rather than leaving a reader to reconcile the two. What the
/// ruling buys is paid for here in one way that matters:
/// <see cref="IsBlocking"/> reads <b>structured, enumerated fields only</b>. Nothing here reads
/// <see cref="ReviewVerdict.Summary"/> or a finding's <c>detail</c> — parsing model prose to pick a
/// branch is the thing Rule 1 actually forbids, and it stays forbidden.
/// </para>
/// <para>
/// <b>Pushed-ness, not the timeout word, is what discriminates re-review from continue</b> —
/// spec/baton.md §13 has the argument. What it means for the code below: nothing reads
/// <c>WorkflowOutcome</c> beyond <see cref="Status.WorkflowOutcome.IsSucceededShaped"/> — the membership
/// test that owns both succeeded-shaped words — and <see cref="IsPushed"/> is the whole discriminator
/// for everything else.
/// </para>
/// <para>
/// <b>Every dispatch is counted and bounded</b> (<see cref="WorkStages.MaxRounds"/>). Two of the arms
/// below can repeat with nothing to end them — spec/baton.md §13 names which and why the ceiling is
/// where a person gets asked.
/// </para>
/// </remarks>
public static class WorkItemLifecycle
{
    /// <summary>
    /// What the item should do next, given what its settled room and its PR say.
    /// </summary>
    /// <param name="observation">The facts read off disk and off <c>gh</c>; see its own doc.</param>
    public static WorkItemTransition Decide(WorkItemObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        // Ready is checked FIRST, ahead of even "has it settled": an approved item must not be
        // re-derived from a room that is re-read every tick forever. QueueScheduler's own IsReady is
        // the other half of this rule, and spec/baton.md §13 is where the rule itself lives.
        if (WorkStages.IsTerminal(observation.Stage))
        {
            return WorkItemTransition.None("the item is ready — the conductor merges or resolves it");
        }

        if (string.IsNullOrWhiteSpace(observation.TerminalOutcome))
        {
            return WorkItemTransition.None("the room has not settled yet");
        }

        // The SUCCEEDED-shaped SET, never one word: #1945's FinishedDuringTeardown is a room that
        // finished and pushed, and reading it as a failure here re-reviewed a PR whose verdict was
        // already on disk. WorkflowOutcome owns the membership test (spec/baton.md §3).
        if (!Status.WorkflowOutcome.IsSucceededShaped(observation.TerminalOutcome))
        {
            return DecideAfterIncompleteLane(observation);
        }

        return observation.Stage switch
        {
            WorkStage.Review or WorkStage.ReReview => DecideFromVerdict(observation),
            _ => DecideAfterMutatingLane(observation),
        };
    }

    /// <summary>
    /// A mutating lane that reached Terminal cleanly: its PR is what the next reviewer reads, so the
    /// absence of one is the one thing this cannot invent.
    /// </summary>
    private static WorkItemTransition DecideAfterMutatingLane(WorkItemObservation observation)
    {
        if (observation.PullRequest is not { } pr)
        {
            return WorkItemTransition.NeedsOperator(
                $"the {WorkStages.Token(observation.Stage)} lane settled succeeded but no pull request is open on "
                + $"'{observation.Branch}' — the queue will not open a PR; {Recovery(observation.Stage)}");
        }

        return Dispatch(
            observation, WorkStage.Review,
            $"the {WorkStages.Token(observation.Stage)} lane succeeded and PR #{pr} is open at "
            + $"{Short(observation.PullRequestHeadSha)}");
    }

    /// <summary>
    /// A review lane that reached Terminal cleanly. <b>The verdict decides, and only its structured
    /// fields do</b> — see the type's remarks.
    /// </summary>
    private static WorkItemTransition DecideFromVerdict(WorkItemObservation observation)
    {
        if (observation.Verdict is not { } verdict)
        {
            // Not treated as an approval. A review that produced no readable verdict has said nothing,
            // and reading silence as APPROVE would merge on the strength of a missing file.
            return WorkItemTransition.NeedsOperator(
                $"the {WorkStages.Token(observation.Stage)} lane settled succeeded but wrote no readable "
                + $"verdict.json — read the room's report.md and decide the round by hand; {Recovery(observation.Stage)}");
        }

        if (!IsBlocking(verdict))
        {
            return WorkItemTransition.Stop(
                WorkStage.Ready,
                $"the review approved: no confirmed high-severity finding in {verdict.Findings.Count} finding(s)");
        }

        return Dispatch(
            observation, WorkStage.Fix,
            $"the review blocked: {CountBlocking(verdict)} confirmed high-severity finding(s)");
    }

    /// <summary>
    /// A lane that did not reach a clean Terminal — timed out, faulted, was cancelled, or settled
    /// indeterminate. spec/baton.md §13's two arms, and the type's remarks say why the discriminator is
    /// pushed-ness rather than the outcome word.
    /// </summary>
    private static WorkItemTransition DecideAfterIncompleteLane(WorkItemObservation observation)
    {
        // A review lane has nothing to push, so "unpushed work" is not a reading its failure can have:
        // the remedy for a review that did not finish is the review again, at whatever head the PR
        // carries now.
        if (observation.Stage is WorkStage.Review or WorkStage.ReReview)
        {
            return observation.PullRequest is { } reviewPr
                ? Dispatch(
                    observation, WorkStage.ReReview,
                    $"the review lane settled {observation.TerminalOutcome} without a verdict; PR #{reviewPr} is "
                    + $"still open at {Short(observation.PullRequestHeadSha)}")
                : WorkItemTransition.NeedsOperator(
                    $"the review lane settled {observation.TerminalOutcome} and no pull request is open on "
                    + $"'{observation.Branch}' — there is nothing left to review; {Recovery(observation.Stage)}");
        }

        if (IsPushed(observation))
        {
            return Dispatch(
                observation, WorkStage.ReReview,
                $"the {WorkStages.Token(observation.Stage)} lane settled {observation.TerminalOutcome} with its work "
                + $"pushed — PR #{observation.PullRequest} head {Short(observation.PullRequestHeadSha)} is the "
                + "workspace head, so the round is re-review rather than fix");
        }

        return Dispatch(
            observation, WorkStage.Continue,
            $"the {WorkStages.Token(observation.Stage)} lane settled {observation.TerminalOutcome} with work that "
            + $"never reached the PR ({DescribeUnpushed(observation)}) — finish and push it");
    }

    /// <summary>
    /// <b>Every dispatch this type issues goes through here</b> — the round is incremented in one place
    /// and <see cref="WorkStages.MaxRounds"/> is checked in one place. Five call sites each writing
    /// <c>observation.Round + 1</c> is five places to forget one, which is how the cycle this bound
    /// exists to stop was reachable at all: only the BLOCK arm counted, so re-review → re-review and
    /// continue → continue ran forever at a full frontier lane apiece (#2004 review).
    /// </summary>
    /// <remarks>
    /// Over the bound it is <see cref="WorkItemTransitionKind.NeedsOperator"/> rather than a longer
    /// wait: the reason names the count and the stage pair it stopped at, and lands on the item where
    /// <c>baton queue list</c> shows it.
    /// </remarks>
    private static WorkItemTransition Dispatch(WorkItemObservation observation, WorkStage next, string reason)
    {
        var round = observation.Round + 1;
        if (round > WorkStages.MaxRounds)
        {
            return WorkItemTransition.NeedsOperator(
                $"the queue has already dispatched {observation.Round} automatic round(s) for this item — its "
                + $"ceiling is {WorkStages.MaxRounds} — and the {WorkStages.Token(observation.Stage)} lane wants a "
                + $"{WorkStages.Token(next)} round again ({reason}); {Recovery(observation.Stage)}");
        }

        return WorkItemTransition.Dispatch(next, round, reason);
    }

    /// <summary>
    /// What actually un-sticks a work item the queue has failed, said once because every
    /// <see cref="WorkItemTransitionKind.NeedsOperator"/> reason ends with it.
    /// </summary>
    /// <remarks>
    /// <b>Written against what the code does, not what would be convenient.</b> A failed item is out of
    /// the advance candidate set for good (<c>WorkItemAdvancer.AdvanceAsync</c>): opening the missing PR
    /// by hand no longer makes the next tick pick it up, which is what the pre-#2004 wording promised.
    /// <c>baton queue</c> has <c>add</c>, <c>list</c>, <c>hold</c>, <c>resume</c> and <c>import</c> and
    /// no verb that clears a failure, and <c>QueueCommand.RefuseIfNotReplaceable</c> refuses a re-add for
    /// any item past <see cref="WorkStage.Implement"/> — so past implement the only recovery in the
    /// product is the operator's own hand on <c>queue.json</c>.
    /// </remarks>
    private static string Recovery(WorkStage stage) =>
        stage == WorkStage.Implement
            ? "the item is still at implement, so 'baton queue add' with the same tag replaces it once you "
                + "have fixed what it needs"
            : $"no 'baton queue' verb reopens a failed item past implement — carry the round by hand, or edit "
                + $"this item's stage/state/round in {Status.BatonPaths.QueueFileName} yourself";

    /// <summary>
    /// <b>The one predicate that turns a verdict into a round</b>: a finding that is both
    /// <see cref="ReviewFindingSeverity.High"/> and <see cref="ReviewFindingStatus.Confirmed"/> blocks;
    /// anything else — medium, low, refuted, unverified, or no findings at all — approves.
    /// </summary>
    /// <remarks>
    /// Two enumerated fields, deliberately. <c>status</c> alone would let an unverified suspicion open
    /// a fix round, and <c>severity</c> alone would let a refuted high-severity claim — one the
    /// reviewer investigated and found untrue, which the schema keeps precisely because a refutation is
    /// evidence — do the same. Both fields are non-null on every verdict a reader ever sees:
    /// <see cref="ReviewVerdictSchema.TryParse"/> refuses a document missing either, which is what
    /// makes the null arms below unreachable rather than lenient.
    /// </remarks>
    public static bool IsBlocking(ReviewVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        return CountBlocking(verdict) > 0;
    }

    private static int CountBlocking(ReviewVerdict verdict) =>
        verdict.Findings?.Count(f =>
            f is { Severity: ReviewFindingSeverity.High, Status: ReviewFindingStatus.Confirmed }) ?? 0;

    /// <summary>
    /// Whether the lane's work reached the PR. Both halves must be known: an unknown workspace head or
    /// an absent PR reads as NOT pushed, which routes to <see cref="WorkStage.Continue"/> — a
    /// continuation that finds nothing to finish costs one lane, where a re-review of work that was
    /// never pushed reviews the previous round's diff and reports it clean.
    /// </summary>
    public static bool IsPushed(WorkItemObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        return observation.PullRequest is not null
            && observation.PullRequestHeadSha is { Length: > 0 } prHead
            && observation.WorkspaceHeadSha is { Length: > 0 } workspaceHead
            && string.Equals(prHead, workspaceHead, StringComparison.OrdinalIgnoreCase);
    }

    private static string DescribeUnpushed(WorkItemObservation observation) =>
        observation.PullRequest is null
            ? $"no pull request is open on '{observation.Branch}'"
            : $"PR #{observation.PullRequest} head {Short(observation.PullRequestHeadSha)} ≠ workspace head "
                + Short(observation.WorkspaceHeadSha);

    /// <summary>A sha as a person reads one. "unknown" rather than an empty gap, so a reason that names
    /// no sha says so out loud.</summary>
    private static string Short(string? sha) =>
        sha is { Length: > 0 } value ? value[..Math.Min(8, value.Length)] : "unknown";
}

/// <summary>
/// Everything <see cref="WorkItemLifecycle.Decide"/> is allowed to look at. A record rather than six
/// parameters so a new fact is a compile error at every construction site rather than a silently
/// defaulted argument.
/// </summary>
/// <param name="Stage">The stage the item is at now.</param>
/// <param name="Round">
/// How many rounds the queue has already dispatched for it — 0 before the first, and one per dispatch
/// of any stage after that. <see cref="WorkStages.MaxRounds"/> is the ceiling.
/// </param>
/// <param name="Branch">The lane's branch, for the reasons this produces. Never used to decide.</param>
/// <param name="TerminalOutcome">
/// The settled room's own outcome word (<c>WorkflowOutcome</c>'s vocabulary), or null when the room
/// has not settled. Null is "not yet", never "failed".
/// </param>
/// <param name="Verdict">
/// The review's <c>verdict.json</c>, parsed through <c>ReviewVerdictSchema.TryParse</c> and null when
/// the room wrote none or wrote one that does not parse. Read only for a review stage.
/// </param>
/// <param name="PullRequest">The open PR's number on <paramref name="Branch"/>, or null when there is none.</param>
/// <param name="PullRequestHeadSha"><c>gh pr view --json headRefOid</c>.</param>
/// <param name="WorkspaceHeadSha">The worktree's own <c>HEAD</c> sha.</param>
public sealed record WorkItemObservation(
    WorkStage Stage,
    int Round,
    string? Branch,
    string? TerminalOutcome,
    ReviewVerdict? Verdict,
    int? PullRequest,
    string? PullRequestHeadSha,
    string? WorkspaceHeadSha);

/// <summary>What the queue does with a work item next.</summary>
/// <param name="Kind">Which of the three shapes below.</param>
/// <param name="NextStage">The stage to move to; null for <see cref="WorkItemTransitionKind.None"/> and
/// <see cref="WorkItemTransitionKind.NeedsOperator"/>, which both leave the stage where it is.</param>
/// <param name="Round">The fix round the next dispatch runs in.</param>
/// <param name="Reason">Why, naming the evidence — recorded verbatim on the JSONL fact and on the item.</param>
public sealed record WorkItemTransition(
    WorkItemTransitionKind Kind,
    WorkStage? NextStage,
    int Round,
    string Reason)
{
    internal static WorkItemTransition None(string reason) =>
        new(WorkItemTransitionKind.None, null, 0, reason);

    internal static WorkItemTransition Dispatch(WorkStage stage, int round, string reason) =>
        new(WorkItemTransitionKind.Dispatch, stage, round, reason);

    internal static WorkItemTransition Stop(WorkStage stage, string reason) =>
        new(WorkItemTransitionKind.Stop, stage, 0, reason);

    internal static WorkItemTransition NeedsOperator(string reason) =>
        new(WorkItemTransitionKind.NeedsOperator, null, 0, reason);
}

/// <summary>The three shapes of <see cref="WorkItemTransition"/>, plus the do-nothing one.</summary>
public enum WorkItemTransitionKind
{
    /// <summary>Nothing to do: the room has not settled, or the item is already ready.</summary>
    None,

    /// <summary>Queue the next round — a brief is rendered and the item goes back to queued.</summary>
    Dispatch,

    /// <summary>Move to <see cref="WorkStage.Ready"/> and stop. The conductor merges; the queue never does.</summary>
    Stop,

    /// <summary>
    /// Nothing derivable. The item fails with the reason, which is what puts it in front of a person in
    /// <c>baton queue list</c> — deliberately not a silent retry, since every arm that reaches here is
    /// one where guessing would dispatch a lane against the wrong evidence.
    /// </summary>
    NeedsOperator,
}
