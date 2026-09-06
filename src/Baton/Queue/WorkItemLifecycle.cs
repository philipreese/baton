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
/// for a person, never inputs to routing. This is not the Flow engine — it is the conductor's queue,
/// the surface that was a PowerShell loop reading the same file with <c>jq</c> last week — and
/// spec/baton.md §13 carries that as an explicit ruling rather than leaving a reader to reconcile the
/// two. What the ruling buys is paid for here in one way that matters:
/// <see cref="IsBlocking"/> reads <b>structured, enumerated fields only</b>. Nothing here reads
/// <see cref="ReviewVerdict.Summary"/> or a finding's <c>detail</c> — parsing model prose to pick a
/// branch is the thing Rule 1 actually forbids, and it stays forbidden.
/// </para>
/// <para>
/// <b>Pushed-ness, not the timeout word, is what discriminates re-review from continue.</b> A lane
/// that runs out of wall clock settles as <c>Failed</c> — there is no distinct "timed out" outcome
/// word (<c>WorkflowOutcome</c>'s five), so keying on one would mean string-matching an error message.
/// The fact that actually decides is whether the work reached the PR: a PR head equal to the
/// workspace's head is work someone can review, and anything else is work to finish.
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
        // re-derived from a room that is re-read every tick forever. spec/baton.md §13's "the queue
        // records ready and does nothing" is this line.
        if (WorkStages.IsTerminal(observation.Stage))
        {
            return WorkItemTransition.None("the item is ready — the conductor merges or resolves it");
        }

        if (string.IsNullOrWhiteSpace(observation.TerminalOutcome))
        {
            return WorkItemTransition.None("the room has not settled yet");
        }

        var succeeded = string.Equals(
            observation.TerminalOutcome, Status.WorkflowOutcome.Succeeded, StringComparison.Ordinal);

        if (!succeeded)
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
                + $"'{observation.Branch}' — open one (or close the item) and re-add it; the queue will not open a PR");
        }

        return WorkItemTransition.Dispatch(
            WorkStage.Review, observation.Round,
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
                + "verdict.json — read the room's report.md and decide the round by hand");
        }

        if (!IsBlocking(verdict))
        {
            return WorkItemTransition.Stop(
                WorkStage.Ready,
                $"the review approved: no confirmed high-severity finding in {verdict.Findings.Count} finding(s)");
        }

        return WorkItemTransition.Dispatch(
            WorkStage.Fix, observation.Round + 1,
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
                ? WorkItemTransition.Dispatch(
                    WorkStage.ReReview, observation.Round,
                    $"the review lane settled {observation.TerminalOutcome} without a verdict; PR #{reviewPr} is "
                    + $"still open at {Short(observation.PullRequestHeadSha)}")
                : WorkItemTransition.NeedsOperator(
                    $"the review lane settled {observation.TerminalOutcome} and no pull request is open on "
                    + $"'{observation.Branch}' — there is nothing left to review");
        }

        if (IsPushed(observation))
        {
            return WorkItemTransition.Dispatch(
                WorkStage.ReReview, observation.Round,
                $"the {WorkStages.Token(observation.Stage)} lane settled {observation.TerminalOutcome} with its work "
                + $"pushed — PR #{observation.PullRequest} head {Short(observation.PullRequestHeadSha)} is the "
                + "workspace head, so the round is re-review rather than fix");
        }

        return WorkItemTransition.Dispatch(
            WorkStage.Continue, observation.Round,
            $"the {WorkStages.Token(observation.Stage)} lane settled {observation.TerminalOutcome} with work that "
            + $"never reached the PR ({DescribeUnpushed(observation)}) — finish and push it");
    }

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
/// <param name="Round">The fix round it is in — 0 until the first BLOCK.</param>
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
