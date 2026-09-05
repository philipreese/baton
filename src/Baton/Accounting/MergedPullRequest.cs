namespace Baton.Accounting;

/// <summary>
/// The value <c>Baton.Cli.MergedPullRequestReader</c> parses out of <c>gh pr list --json …</c> and
/// <see cref="CostLedgerStore.BuildGithubBackfillRow"/> records (#1901 C2's GitHub half) — one merged
/// pull request, reduced to the facts a cost reading joins on. Each member's meaning on the row is the
/// corresponding <see cref="CostLedgerEntry"/> field's own doc; nothing is restated here.
/// </summary>
/// <remarks>
/// <para>
/// <b>A value, not a probe</b> — the same boundary <see cref="WorkspaceDelivery"/> keeps, for the same
/// reason: the engine layer spawns no process and reaches no network, so a ledger unit test exercises
/// these fields without a <c>gh</c> install. The CLI owns the spawn and the JSON.
/// </para>
/// <para>
/// <b>Every member except <paramref name="Number"/> is independently absent</b>, on the row's own
/// doctrine: a field <c>gh</c> did not report is omitted rather than zeroed. <paramref name="Number"/>
/// is required because it is the dedupe key — a PR with no number cannot be recorded idempotently and
/// is refused by the reader rather than written under a synthesised id.
/// </para>
/// </remarks>
/// <param name="Room">
/// The <c>BatonPaths.RecordKey</c> of the room whose work produced this PR, when one could be joined to
/// it by branch name. <b>Absent means "no room on disk declares this branch"</b> — a room already swept
/// by retention, a PR opened by hand, or a lane whose workflow declares no <c>delivery-branch.txt</c>.
/// The row is written either way; the dry run reports how many landed unattributed and why.
/// </param>
public sealed record MergedPullRequest(
    int Number,
    string? HeadRefName = null,
    DateTime? MergedAt = null,
    int? FilesChanged = null,
    long? Additions = null,
    long? Deletions = null,
    int? Commits = null,
    int? ReviewCount = null,
    string? Issue = null,
    string? Room = null);
