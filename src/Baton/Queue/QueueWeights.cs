namespace Baton.Queue;

/// <summary>
/// The one definition of what a lane weighs against <c>QueueSettings.MaxLiveWeight</c> (#1934 slice 1,
/// item 2). ONE function, called twice: once over the running rooms to build the live tally, once for
/// the candidate item. Two copies of "review is 0, codex is 0.5" would drift, and the drift would be
/// invisible — the cap would still look enforced.
/// </summary>
/// <remarks>
/// <b>Review is two behaviours, not one.</b> A review lane weighs nothing (it is mostly waiting on a
/// verify step and a read) <em>and</em> bypasses the cap entirely, so a review can launch when the
/// fleet is already at <c>MaxLiveWeight</c>. <see cref="BypassesCap"/> is the second half, kept
/// separate from the zero weight because zero alone would not admit it: the scheduler's
/// <c>live + candidate &lt;= max</c> test fails at <c>live == max</c> even when the candidate adds
/// nothing.
/// </remarks>
public static class QueueWeights
{
    /// <summary>An ordinary mutating lane.</summary>
    public const double Implement = 1.0;

    /// <summary>A lane on the codex adapter — half, because it is a different vendor process with a
    /// materially smaller resident footprint than a claude lane.</summary>
    public const double Codex = 0.5;

    /// <summary>A review lane. See the type remarks for why zero is only half the rule.</summary>
    public const double Review = 0.0;

    /// <summary>The codex adapter tag, matched case-insensitively the same way the tier table matches
    /// an adapter an operator typed.</summary>
    public const string CodexAdapter = "codex";

    /// <summary>
    /// What a lane of <paramref name="role"/> on <paramref name="adapter"/> weighs. Role wins over
    /// adapter: a review lane on codex is 0, not 0.5, because the reason review weighs nothing (it is
    /// not competing for the memory the floor protects) does not stop applying on another vendor.
    /// A null role or adapter — a running room whose bindings could not be read — weighs
    /// <see cref="Implement"/>, the conservative reading: an unidentified live lane counts against the
    /// cap rather than being free.
    /// </summary>
    public static double For(string? role, string? adapter)
    {
        if (string.Equals(role, QueueTierTable.ReviewRole, StringComparison.OrdinalIgnoreCase))
        {
            return Review;
        }

        return string.Equals(adapter, CodexAdapter, StringComparison.OrdinalIgnoreCase) ? Codex : Implement;
    }

    /// <summary>Whether a lane of <paramref name="role"/> is admitted regardless of the live tally.</summary>
    public static bool BypassesCap(string? role) =>
        string.Equals(role, QueueTierTable.ReviewRole, StringComparison.OrdinalIgnoreCase);
}
