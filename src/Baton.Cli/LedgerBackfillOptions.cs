namespace Baton.Cli;

/// <summary>
/// The parsed form of <c>baton ledger backfill</c> (#1901 C2). <see cref="LedgerBackfillOptionsParser"/>
/// is the one producer; <see cref="LedgerBackfillCommand"/> the one consumer.
/// </summary>
/// <param name="DryRun">
/// Print what would be written and write nothing. The whole walk still runs — the counts a dry run
/// reports are the counts the real run would act on, not an estimate of them.
/// </param>
/// <param name="RoomsRoot">
/// One directory whose immediate children are the rooms to walk. <see langword="null"/> — the default —
/// is the union <see cref="LedgerBackfillCommand.ResolveRoomDirectoriesAsync"/> takes over
/// <c>BatonPaths.Rooms</c> and the room registry, so a room registered outside the default root is not
/// silently invisible to the backfill.
/// </param>
/// <param name="Since">
/// UTC. Filters the ROOM half on each row's <c>endedAt</c>, through <c>LedgerQuery</c> itself so the
/// window means exactly what <c>baton ledger --since</c> means — that type's own remarks are the
/// definition, including what it does with a row carrying no <c>endedAt</c>. The GitHub half always
/// has a floor — see
/// <see cref="LedgerBackfillCommand.DefaultGithubSince"/> — because <c>gh</c> needs a date to search
/// from; this overrides it when set.
/// </param>
public sealed record LedgerBackfillOptions(
    bool DryRun = false,
    string? RoomsRoot = null,
    DateTime? Since = null,
    bool Help = false);
