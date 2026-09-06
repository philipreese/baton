namespace Baton.Vendors;

/// <summary>
/// The ONE shipped <see cref="IVendorUsageSource"/> population (#1923). The daemon's harvester and
/// the runway hold's on-demand harvest both read it, so "which vendors can be harvested" is stated
/// once rather than in two hardcoded lists that can drift apart — a vendor added to only one of them
/// would be harvested on a cadence and never on demand, or the reverse, with nothing red anywhere.
/// </summary>
/// <remarks>
/// A new list per call, deliberately: each source owns a spawn (<c>BatonTask</c>) per read and is
/// cheap to construct, and sharing one instance across a background service and a CLI dispatch would
/// make lifetime a question nobody here needs to answer.
/// </remarks>
public static class VendorUsageSources
{
    /// <summary>Every vendor Baton can harvest a usage snapshot from, in no significant order.</summary>
    public static IReadOnlyList<IVendorUsageSource> Default =>
        [new ClaudeUsageSlashCommandSource(), new AgyUsageSlashCommandSource(), new CodexUsageSource()];
}
