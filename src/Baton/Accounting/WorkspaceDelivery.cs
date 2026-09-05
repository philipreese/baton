namespace Baton.Accounting;

/// <summary>
/// The value <c>Baton.Cli.WorkspaceDeliveryProbe</c> resolves and this ledger records (#1901 C1 items
/// 1 and 3) — what one worker's WORKSPACE says about the work its attempt delivered. Each member's
/// meaning, and what its absence does and does not assert, is the corresponding
/// <see cref="CostLedgerEntry"/> field's own doc; nothing is restated here.
/// </summary>
/// <remarks>
/// <para>
/// <b>A value, not a probe.</b> Every field here is resolved by the settle site and handed in, the
/// same shape <see cref="CostLedgerStore.BuildEntries"/>'s <c>runwayOverrideReasonByWorker</c> already
/// uses and for the same two reasons: the engine layer stays git-agnostic and holds no
/// <c>Baton.Vendors</c> reference (so it cannot read a room's bindings to find the workspace at all),
/// and a ledger unit test must never spawn <c>git</c> or reach GitHub to exercise a field.
/// <c>Baton.Cli.WorkspaceDeliveryProbe</c> is the one production producer.
/// </para>
/// <para>
/// <b>Every member is independently absent</b>, on the row's own doctrine: an unresolvable fact is
/// omitted rather than zeroed. The four diff members are the one group that moves together — one
/// <c>git diff --numstat</c> produces all four or none — which their own docs on
/// <see cref="CostLedgerEntry.FilesChanged"/> state.
/// </para>
/// </remarks>
public sealed record WorkspaceDelivery(
    string? Issue = null,
    string? PullRequest = null,
    int? FilesChanged = null,
    long? Additions = null,
    long? Deletions = null,
    int? TestFilesChanged = null);
