using Baton.Accounting;
using Baton.Memory;

namespace Baton.Cli;

/// <summary>
/// Parses <c>baton memory audit</c> (#1852 phase A). Same shape as
/// <see cref="LedgerViewOptionsParser"/> — one <see cref="CliArgumentException"/> per malformed
/// invocation, never a bare <see cref="InvalidOperationException"/>.
/// </summary>
public static class MemoryAuditOptionsParser
{
    public const string Usage = "Usage: baton memory audit [--repository <id>|fleet] [--format text|json] [--help]";

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
        "It also inventories the NON-CLAUDE memory roots -- Codex markdown and sqlite (including the",
        "Baton-managed ~/.baton/codex-home), Antigravity brain/knowledge, Antigravity .pbtxt -- under a",
        "separate heading, with NO findings attached. Those roots are per-machine, so they map to no",
        "repository and every finding kind below would be a statement about a mapping they do not have.",
        "A named-artifact selector per family bounds the walk; one family (Antigravity brain) is counted",
        "and never opened. What each format was measured to BE is in docs/vendor-doc-audit.md, not here.",
        "",
        "It also lists every RETRACTION in Baton's own canonical stores (~/.baton/<repo-slug>/memory/",
        "retractions.jsonl) with its reason and author -- the only report of what 'baton memory retract'",
        "has declared no longer true. Not a finding: a retraction is a recorded decision, not a question",
        "left open. The retracted entry's own row is still in its store; history is never deleted.",
        "",
        "It also lists Baton's own CANONICAL STORES under ~/.baton/<slug>/memory/ -- the reserved FLEET",
        "store first (operator and machine facts that belong to no repository, #2112), then one per",
        "repository -- with each store's entry count. Rows are counted, never printed.",
        "",
        "  --repository <id>|fleet",
        "                      Report only this canonical store in the CANONICAL STORES section. The root",
        "                      inventory above it is machine-wide and is NOT filtered: hiding a root would",
        "                      hide its findings.",
        "  --format json       One object: {claudeHome, userHome, roots, findings, counts, vendorRoots,",
        "                      retractions, canonicalStores}. Field names are the report record's own; an",
        "                      absent field is absent, never null. vendorRoots, retractions and",
        "                      canonicalStores are separate from roots and are not counted in counts.",
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
        string? repository = null;

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
                case "--repository":
                    repository = ParseRepository(RequireValue(args, i));
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

        return new MemoryAuditOptions(format, help, repository);
    }

    /// <summary>
    /// The store selector: the reserved word, or a canonical identity — the same read-path half
    /// <see cref="MemorySyncOptionsParser"/> applies, for the same reason (a raw casing would name a
    /// store that does not exist and report it as empty).
    /// </summary>
    private static string ParseRepository(string value) =>
        FleetMemory.IsFleet(value)
            ? FleetMemory.Slug
            : RepositoryIdentity.TryCanonicalize(value) is { Length: > 0 } canonical
            ? canonical
            : throw new CliArgumentException(
                $"'{value.Trim()}' is not a repository identity or '{FleetMemory.Slug}': it has no " +
                $"host-and-path to canonicalize, so no store could be named for it. {Usage}",
                "pass a canonical identity such as 'github.com/owner/repo', or 'fleet'.");

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
