using System.Text.Json.Serialization;

namespace Baton.Queue;

/// <summary>
/// Where an issue-anchored work item is in the lifecycle the conductor ran by hand (#1934 slice 2,
/// Q2 answer (b); spec/baton.md §13 "Work items"): implement → review → fix round → re-review, with
/// a continuation for a lane that stopped without pushing, and <see cref="Ready"/> as the one stage
/// the queue never leaves on its own.
/// </summary>
/// <remarks>
/// <b>A stage is not a state.</b> <see cref="QueueItemState"/> still says whether the item is queued,
/// launched, done or failed; the stage says what the NEXT dispatch is for. A slice-1 dispatch request
/// carries no stage at all (<see cref="QueueItem.Stage"/> is null), which is the discriminator between
/// the two item shapes — there is deliberately no second "kind" field for a reader to disagree with.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<WorkStage>))]
public enum WorkStage
{
    /// <summary>Write the change and open the PR. The stage an item is added at.</summary>
    Implement,

    /// <summary>An independent review of the PR the implement lane opened.</summary>
    Review,

    /// <summary>A fix round against the last verdict's findings.</summary>
    Fix,

    /// <summary>A review of the PR again, at its new head, carrying the prior findings.</summary>
    ReReview,

    /// <summary>
    /// Finish and push work a lane left uncommitted or unpushed. Distinct from <see cref="Fix"/>
    /// because nothing was reviewed: there are no findings to carry, only work to complete.
    /// </summary>
    Continue,

    /// <summary>
    /// The reviewer approved. <b>The queue stops here</b> — it never merges (spec/baton.md §13) and
    /// never dispatches a <see cref="Ready"/> item again; the conductor merges or resolves it.
    /// </summary>
    Ready,
}

/// <summary>
/// The one place a stage is turned into the things a dispatch needs — its role, its tier scope, and
/// the token a ledger row and <c>baton queue list</c> print. Stated once so a stage renamed here
/// cannot leave a second spelling behind.
/// </summary>
public static class WorkStages
{
    /// <summary>The mutating role every non-review stage dispatches.</summary>
    public const string ImplementRole = "implement";

    /// <summary>Lower-case token for a ledger row and the listing. Never <c>ToString()</c> at a call
    /// site: the ledger is grep'd across machines and a casing change would split its history.</summary>
    public static string Token(WorkStage stage) => stage switch
    {
        WorkStage.Implement => "implement",
        WorkStage.Review => "review",
        WorkStage.Fix => "fix",
        WorkStage.ReReview => "re-review",
        WorkStage.Continue => "continue",
        WorkStage.Ready => "ready",
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unknown work stage."),
    };

    /// <summary>
    /// The worker role <paramref name="stage"/> dispatches. <see cref="WorkStage.Ready"/> has none —
    /// it is the stage that does not dispatch — so asking for one is a caller bug rather than a
    /// resolvable question.
    /// </summary>
    public static string RoleFor(WorkStage stage) => stage switch
    {
        WorkStage.Review or WorkStage.ReReview => QueueTierTable.ReviewRole,
        WorkStage.Implement or WorkStage.Fix or WorkStage.Continue => ImplementRole,
        _ => throw new ArgumentOutOfRangeException(
            nameof(stage), stage, "A 'ready' item does not dispatch, so it has no role."),
    };

    /// <summary>True for the stage the queue records and then stops at.</summary>
    public static bool IsTerminal(WorkStage stage) => stage == WorkStage.Ready;
}
