namespace Baton.Queue;

/// <summary>
/// The one definition of what a lane weighs against <c>QueueSettings.MaxLiveWeight</c> (#1934 slice 1,
/// item 2). ONE function, called twice: once over the running rooms to build the live tally, once for
/// the candidate item. Two copies of "review is 0, everything else is 1" would drift, and the drift
/// would be invisible — the cap would still look enforced.
/// </summary>
/// <remarks>
/// <b>Review is two behaviours, not one.</b> A review lane weighs nothing (it is mostly waiting on a
/// verify step and a read) <em>and</em> bypasses the cap entirely, so a review can launch when the
/// fleet is already at <c>MaxLiveWeight</c>. <see cref="BypassesCap"/> is the second half, kept
/// separate from the zero weight because zero alone would not admit it: the scheduler's
/// <c>live + candidate &lt;= max</c> test fails at <c>live == max</c> even when the candidate adds
/// nothing.
/// </remarks>
/// <remarks>
/// No adapter gets a lighter weight than any other mutating lane (operator ruling, #2163) — see
/// spec/baton.md's "Lane weights" section for why. A prior 0.5 weight for the codex adapter was
/// removed here.
/// </remarks>
public static class QueueWeights
{
    /// <summary>An ordinary mutating lane, on any adapter.</summary>
    public const double Implement = 1.0;

    /// <summary>A review lane. See the type remarks for why zero is only half the rule.</summary>
    public const double Review = 0.0;

    /// <summary>
    /// What a lane of <paramref name="role"/> weighs. Adapter no longer matters (#2163) — every
    /// mutating lane weighs the same regardless of vendor. A null role — a running room whose bindings
    /// could not be read — weighs <see cref="Implement"/>, the conservative reading: an unidentified
    /// live lane counts against the cap rather than being free.
    /// </summary>
    public static double For(string? role, string? adapter)
    {
        return string.Equals(role, QueueTierTable.ReviewRole, StringComparison.OrdinalIgnoreCase)
            ? Review
            : Implement;
    }

    /// <summary>Whether a lane of <paramref name="role"/> is admitted regardless of the live tally.</summary>
    public static bool BypassesCap(string? role) =>
        string.Equals(role, QueueTierTable.ReviewRole, StringComparison.OrdinalIgnoreCase);
}
