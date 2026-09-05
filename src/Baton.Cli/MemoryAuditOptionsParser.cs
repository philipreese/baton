namespace Baton.Cli;

/// <summary>
/// Parses <c>baton memory audit</c> (#1852 phase A). Same shape as
/// <see cref="LedgerViewOptionsParser"/> — one <see cref="CliArgumentException"/> per malformed
/// invocation, never a bare <see cref="InvalidOperationException"/>.
/// </summary>
public static class MemoryAuditOptionsParser
{
    public const string Usage = "Usage: baton memory audit [--format text|json] [--help]";

    /// <summary>
    /// What <c>--help</c> prints under <see cref="Usage"/>. Every line is a place a reader's prior
    /// fills the gap wrongly if the negative is not stated (CLAUDE.md, "Writing documentation"):
    /// whether this writes anything, whether it reads what a memory file SAYS, which roots it looks
    /// at, and — for each finding kind — what it does NOT claim.
    /// </summary>
    public static IReadOnlyList<string> HelpLines { get; } =
    [
        "",
        "READ-ONLY BY CONSTRUCTION. This verb writes nothing, moves nothing and deletes nothing --",
        "which is why it has NO --dry-run flag: there is no other mode for one to be the safe half of.",
        "It reads memory files only to digest them; no memory file's CONTENT is read into the report,",
        "printed, or used to decide anything. Session transcripts (not memory files) are read for the",
        "one field that says which directory a project ran in.",
        "",
        "Population: every ~/.claude/projects/<encoded-path>/memory root, AND every archived root under",
        "~/.claude/memory-archive/<label>/. The archive is not optional -- a live root can be empty",
        "precisely BECAUSE an undocumented migration drained it into one.",
        "",
        "  --format json       One object: {claudeHome, roots, findings, counts}. Field names are the",
        "                      report record's own; an absent field is absent, never null.",
        "",
        "Finding kinds, and what each one does NOT claim:",
        "  duplicate      One identical file (same SHA-256) in two or more roots. Not a ruling about",
        "                 which copy is canonical.",
        "  orphan         The checkout this memory belongs to is gone from this machine. The memory",
        "                 is intact; nothing here removes it.",
        "  stale          An archived root whose repository still has a live root, so its entries are",
        "                 supersession CANDIDATES. Which entry supersedes which needs the entries",
        "                 themselves and is not decided here.",
        "  no-provenance  No repository identity could be derived -- the path is not a git checkout,",
        "                 or no path could be decoded from the directory name at all.",
        "  ambiguous      Two candidates and no basis to choose: either the directory name decodes to",
        "                 several checkout paths, or the checkout's origin names one repository while",
        "                 the root's FILENAMES name another. Both candidates are printed and neither",
        "                 is selected -- deciding needs the entries' text, which is the import's job.",
    ];

    public static MemoryAuditOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var format = MemoryAuditOutputFormat.Text;
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
                case "--format":
                    format = ParseFormat(RequireValue(args, i));
                    i += 2;
                    break;
                // Named rather than left to the unknown-option branch below: an operator reaching for
                // --dry-run has concluded this verb can write, and "unknown option" would leave that
                // conclusion standing.
                case "--dry-run":
                    throw new CliArgumentException(
                        "'baton memory audit' has no '--dry-run': it is read-only by construction and " +
                        $"never writes, moves or deletes anything. {Usage}",
                        "drop the flag and run the command.");
                default:
                    throw new CliArgumentException(
                        arg.StartsWith("--", StringComparison.Ordinal)
                            ? $"Unknown option '{arg}'. {Usage}"
                            : $"Unexpected argument '{arg}'. {Usage}");
            }
        }

        return new MemoryAuditOptions(format, help);
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

    private static MemoryAuditOutputFormat ParseFormat(string value) => value.Trim().ToLowerInvariant() switch
    {
        "text" => MemoryAuditOutputFormat.Text,
        "json" => MemoryAuditOutputFormat.Json,
        _ => throw new CliArgumentException(
            $"Unknown --format '{value}'. Known formats: text, json. {Usage}"),
    };
}
