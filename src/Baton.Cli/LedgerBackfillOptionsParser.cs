namespace Baton.Cli;

/// <summary>
/// Parses <c>baton ledger backfill</c> (#1901 C2). Same shape as <see cref="LedgerViewOptionsParser"/>
/// — one <see cref="CliArgumentException"/> per malformed invocation — and it reuses that parser's own
/// <c>--since</c> instant reader rather than growing a second spelling of "what an operator means by a
/// date".
/// </summary>
public static class LedgerBackfillOptionsParser
{
    public const string Usage =
        "Usage: baton ledger backfill [--dry-run] [--rooms-root <dir>] [--since <instant>] [--help]";

    /// <summary>
    /// What <c>--help</c> prints under <see cref="Usage"/>. Every line states a negative a reader's
    /// prior would otherwise fill in wrongly (CLAUDE.md, "Writing documentation"): which rows a run
    /// adds, which it can never add, what the two halves do when they cannot attribute something, and
    /// that a second run is a no-op rather than a duplicate.
    /// </summary>
    public static IReadOnlyList<string> HelpLines { get; } =
    [
        "",
        "Recovers rows the repository-keyed COST ledger (~/.baton/ledger/<repository>.jsonl) never got:",
        "",
        "  Rooms on disk    Every settled execution in a room that has no ledger row yet -- tokens from",
        "                   the captured stream through the same projector a settle uses, and the same",
        "                   row builder, so a recovered row is indistinguishable from one written at",
        "                   settle. A room whose stream is truncated yields completeness: partial.",
        "  Merged PRs       One 'github-backfill' row per merged pull request, carrying mergedAt, the",
        "                   diff shape GitHub reports, the commit count, the review count and the issue",
        "                   it closed. These rows carry NO token dimension and no estimate: nothing ran.",
        "                   'baton ledger --source-kind baton-execution' is the filter that excludes",
        "                   them from a spend reading.",
        "",
        "  --dry-run        Walk everything, write nothing, and print what a real run would write plus",
        "                   how many rooms and PRs could not be attributed and why.",
        "  --rooms-root     Walk this directory's immediate children instead of the default, which is",
        "                   ~/.baton/rooms UNIONED with the room registry (so a room registered",
        "                   elsewhere is not silently skipped).",
        "  --since          Same window as 'baton ledger --since': on each row's endedAt, inclusive, and",
        "                   a row with no endedAt is excluded rather than assumed in. Also the date the",
        "                   merged-PR search starts from; unset, that search starts at the 2026-08-28",
        "                   reset #1901 names.",
        "",
        "Idempotent: rows are deduplicated on execution id (a PR row's id is 'github-pr-<n>'), so a",
        "second run writes nothing. It can only ADD rows -- this ledger is append-only and its rows are",
        "immutable, so an execution already ledgered keeps whatever its row recorded at the time,",
        "including fields a later phase learned how to fill.",
    ];

    public static LedgerBackfillOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var dryRun = false;
        var help = false;
        string? roomsRoot = null;
        DateTime? since = null;

        var i = 0;
        while (i < args.Count)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--help":
                case "-h":
                    help = true;
                    i++;
                    break;
                case "--dry-run":
                    dryRun = true;
                    i++;
                    break;
                case "--rooms-root":
                    roomsRoot = RequireValue(args, i);
                    i += 2;
                    break;
                case "--since":
                    since = LedgerViewOptionsParser.ParseInstant(RequireValue(args, i), "--since");
                    i += 2;
                    break;
                default:
                    throw new CliArgumentException(
                        arg.StartsWith("--", StringComparison.Ordinal)
                            ? $"Unknown option '{arg}'. {Usage}"
                            : $"Unexpected argument '{arg}'. {Usage}");
            }
        }

        return new LedgerBackfillOptions(dryRun, roomsRoot, since, help);
    }

    private static string RequireValue(IReadOnlyList<string> args, int index)
    {
        if (index + 1 >= args.Count)
        {
            throw new CliArgumentException(
                $"Option '{args[index]}' requires a value. {Usage}",
                $"pass a value after '{args[index]}'.");
        }

        return args[index + 1];
    }
}
