using System.Globalization;

namespace Baton.Cli;

/// <summary>
/// Parses <c>baton ledger export</c> (#1901 C3). Same shape as <see cref="LedgerBackfillOptionsParser"/>
/// — one <see cref="CliArgumentException"/> per malformed invocation — and <c>--as-of</c> is a plain
/// <c>yyyy-MM-dd</c> rather than <see cref="LedgerViewOptionsParser.ParseInstant"/>'s instant, because
/// this value names a FILE and a file name has no time-of-day half to get wrong.
/// </summary>
public static class LedgerExportOptionsParser
{
    public const string Usage =
        "Usage: baton ledger export --to <dir> [--as-of <yyyy-MM-dd>] [--repo-identity <key>] [--help]";

    /// <summary>
    /// What <c>--help</c> prints under <see cref="Usage"/>. Each line states a negative a reader's prior
    /// fills in wrongly otherwise (CLAUDE.md, "Writing documentation"): that the date does not window
    /// the rows, that this format alone is redacted, and that the store is never written.
    /// </summary>
    public static IReadOnlyList<string> HelpLines { get; } =
    [
        "",
        "Writes <dir>/<yyyy-MM-dd>.csv -- byte-for-byte what 'baton ledger --format csv' prints over the",
        "WHOLE repository-keyed cost ledger -- and updates <dir>/README.md's table with that file's row",
        "count, the schema version it was written under, and the newest endedAt in it. READ-ONLY over the",
        "ledger: this verb never appends, repairs or prunes a row.",
        "",
        "  --to             Where the export lands. Required, and normally a checked-out working tree:",
        "                   the point of the verb is that an analysis is reproducible from the repository",
        "                   rather than from one machine's ~/.baton (#1901 C3).",
        "  --as-of          The date the file is NAMED for; today (local) by default. It does not window",
        "                   the rows -- an export is always the whole store as it stands at write time.",
        "                   Re-running for a date already exported REWRITES that day's file and updates",
        "                   its existing README row in place; it never appends a second row.",
        "  --repo-identity  Export another repository's ledger: its canonical identity",
        "                   ('github.com/owner/repo') or the ledger file's own stem.",
        "",
        "The CSV is REDACTED and the other two formats are not: room/parentRoom are reduced to their",
        "basename, and a cell that still looks like a filesystem path or carries this machine's OS account",
        "name refuses the whole export. That asymmetry exists because this is the format that gets",
        "committed to a public repository. Only the ledger is exported -- nothing else under ~/.baton",
        "(rooms, streams, transcripts, memory) is read or written by this verb.",
    ];

    public static LedgerExportOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? targetDirectoryPath = null;
        string? repositoryIdentityKey = null;
        DateTime? asOf = null;
        var help = false;

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
                case "--to":
                    targetDirectoryPath = RequireValue(args, i);
                    i += 2;
                    break;
                case "--as-of":
                    asOf = ParseDate(RequireValue(args, i));
                    i += 2;
                    break;
                case "--repo-identity":
                    repositoryIdentityKey = RequireValue(args, i);
                    i += 2;
                    break;
                default:
                    throw new CliArgumentException(
                        arg.StartsWith("--", StringComparison.Ordinal)
                            ? $"Unknown option '{arg}'. {Usage}"
                            : $"Unexpected argument '{arg}'. {Usage}");
            }
        }

        if (!help && targetDirectoryPath is not { Length: > 0 })
        {
            throw new CliArgumentException(
                $"'baton ledger export' requires '--to <dir>': there is no default destination, because "
                + $"the destination is a repository working tree. {Usage}",
                "baton ledger export --to benchmarks/ledger");
        }

        return new LedgerExportOptions(targetDirectoryPath, asOf, repositoryIdentityKey, help);
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

    /// <summary>
    /// A calendar date, exactly <c>yyyy-MM-dd</c>. Deliberately NOT
    /// <see cref="LedgerViewOptionsParser.ParseInstant"/>: that one accepts an instant and resolves a
    /// bare date to local midnight, which is the right answer for a filter on <c>endedAt</c> and the
    /// wrong one here — this value is a file name, so accepting '2026-09-05T14:00:00Z' would silently
    /// discard the half the operator typed.
    /// </summary>
    private static DateTime ParseDate(string value)
    {
        if (DateTime.TryParseExact(
                value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date;
        }

        throw new CliArgumentException(
            $"Option '--as-of' needs a 'yyyy-MM-dd' date, not '{value}'. It names the exported FILE, not a "
            + $"window over the rows, so it carries no time of day. {Usage}",
            "--as-of 2026-09-05");
    }
}
