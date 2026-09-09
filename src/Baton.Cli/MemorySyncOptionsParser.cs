using Baton.Accounting;
using Baton.Memory;

namespace Baton.Cli;

/// <summary>
/// Parses <c>baton memory sync</c> (#1852 phase C). Same shape as
/// <see cref="MemoryImportOptionsParser"/> — one <see cref="CliArgumentException"/> per malformed
/// invocation, never a bare <see cref="InvalidOperationException"/>.
/// </summary>
/// <remarks>
/// The format enum is <see cref="MemoryAuditOutputFormat"/>, shared with <c>audit</c> rather than
/// duplicated: it is the same two-valued choice with the same meaning, and a second enum spelling
/// <c>text|json</c> would be a second definition of one thing (CLAUDE.md, <c>record-once</c>).
/// </remarks>
public static class MemorySyncOptionsParser
{
    public const string Usage =
        "Usage: baton memory sync [--repository <id>|fleet] [--apply | --check] [--format text|json] " +
        "[--repository-facts <dir>] [--help]";

    /// <summary>
    /// What <c>--help</c> prints under <see cref="Usage"/>. Every line states a negative a reader's
    /// prior would otherwise fill in wrongly: what is written and what provably is not, which vendor
    /// surfaces are targets and which are excluded by ruling, why there is no timestamp in the output,
    /// and where the conflict rule's population comes from.
    /// </summary>
    public static IReadOnlyList<string> HelpLines { get; } =
    [
        "",
        "Projects Baton's canonical memory store into the vendor memory roots that already exist, as a",
        "CACHE. It reads the canonical store; it never reads a projection back, and it never edits the",
        "canonical store. Without --apply it writes NOTHING -- no file, and no directory either.",
        "",
        "And NEITHER DOES 'baton memory import', which is the half of that claim that needed a mechanism:",
        "sync writes into a root that is import's own population, so import recognises a projection by the",
        "marker on its first line and reports it as projection-skipped rather than filing it. Without that",
        "the pair would be a loop feeding the store its own contents once per cycle.",
        "",
        $"Each target root receives exactly one file, '{ClaudeProjectionTarget.ProjectionFileName}', overwritten in full.",
        "No other file in a vendor root is touched -- not MEMORY.md, not the vendor's own memories. The",
        "honest consequence: a vendor that surfaces only the memories it has indexed may not read this",
        "file until something points at it, because Baton will not edit an index the operator owns.",
        "",
        "The output carries NO timestamp, on purpose: an unchanged store projects byte-identical bytes,",
        "so any diff means the store changed. The header carries a content hash where a generated-at",
        "stamp would otherwise be, names itself a cache, names the canonical store file it came from,",
        "and back-points every section to the canonical entry id it was projected from.",
        "",
        "TARGETS ARE MARKDOWN ONLY, and they are DISCOVERED rather than constructed. The Claude roots",
        "({claude-home}/projects/<encoded-path>/memory) that resolve to the repository being synced, and",
        "the Codex MARKDOWN roots (~/.codex/memories, ~/.baton/codex-home/memories) an operator has",
        "asserted a repository for with 'baton memory import --assert'. Codex's memories_*.sqlite stores",
        "and every Antigravity store (sqlite, protobuf) are NOT projected and are not written at all: a",
        "sqlite+WAL target is a different problem with a different idempotence instrument, and Q4",
        "(operator, 2026-09-05) confined this phase to markdown. A repository with no discovered root is",
        "reported as having no target rather than having one created for it.",
        "",
        "ARCHIVED CLAUDE ROOTS (~/.claude/memory-archive/<label>/) ARE NEVER TARGETS EITHER, including",
        "ones you have asserted a repository for -- which is how an archive is imported, so the case is",
        "reachable rather than theoretical. An archive is a record of what was; a projection is the current",
        "reading. Every discovered root that is not a target is listed in the report with the reason.",
        "",
        "  --repository <id>   Sync only this repository, e.g. 'github.com/owner/repo'. Canonicalized the",
        "                      same way a git probe's answer is, so a clone URL is accepted. Default: every",
        "                      repository that has a canonical store, and the fleet store.",
        "  --repository fleet  Sync only the reserved FLEET store's own targets: the roots an operator",
        "                      asserted to 'fleet'. EVERY repository's projection already carries the fleet",
        "                      store's entries -- merged in ahead of the repository's own, neither shadowing",
        "                      the other (spec/baton.md §12 states the rule once) -- so this selects the",
        "                      fleet's own file, not whether fleet facts reach a repository's.",
        "  --apply             Write the projections. Without it, nothing is written and the report says",
        "                      what would change. Writes happen under the canonical store's own lock, so a",
        "                      concurrent 'baton memory import' cannot land mid-projection.",
        "  --check             Write nothing and EXIT 1 if any discovered target's disposition is not",
        "                      'unchanged', so a CI step can gate on 'the projections match the store'.",
        "                      Refused together with --apply: one asks whether the projections are current",
        "                      and the other makes them current, and a run that repaired what it was",
        "                      measuring could never report anything but success.",
        "                      ITS POPULATION IS THE TARGETS THAT WERE DISCOVERED. A repository whose store",
        "                      holds memories but whose machine holds no matching root has nothing that",
        "                      could be rewritten, so it exits 0 -- 'nothing is stale' and 'nowhere to",
        "                      write' are different answers, and the report prints the second one either",
        "                      way. Use 'baton memory audit' to ask which roots exist.",
        "  --format text|json  Report format. Default text.",
        "  --repository-facts <dir>",
        "                      A directory of checked-in repository facts (*.md) to weigh against the",
        "                      vendor-sourced ones. Baton imposes NO convention about where these live in a",
        "                      checkout and creates nothing: you name the directory. Requires --repository,",
        "                      since a fact has to be filed under a subject. Omit it and the conflict rule",
        "                      has an empty population, which the report states rather than leaving to be",
        "                      read out of a silent zero.",
        "",
        "CONFLICTS ARE DECIDED BY PRECEDENCE, NEVER MERGED. A checked-in repository fact and a vendor",
        "memory fact conflict when they share a subject and a source FILENAME -- the same key phase B",
        "uses for supersession. Repository truth is projected; the vendor entry is listed in the report as",
        "overridden, with its canonical id, and is left untouched in the canonical store. Nothing compares",
        "the two beyond their digests: what a memory SAYS is never read to decide anything.",
        "",
        "A superseded entry is OMITTED from the projection and NAMED in the report -- a cache carries the",
        "current reading, and the store still holds every row. Entries that do not fit the projection",
        "budget are named too: truncation stops at the first entry that does not fit and drops the rest of",
        "the (repository, kind, id) order, so the drop set is a suffix an operator can predict.",
    ];

    public static MemorySyncOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? repository = null;
        string? repositoryFacts = null;
        var apply = false;
        var check = false;
        var help = false;
        var format = MemoryAuditOutputFormat.Text;

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
                case "--apply":
                    apply = true;
                    i++;
                    break;
                case "--check":
                    check = true;
                    i++;
                    break;
                case "--repository":
                    repository = ParseRepository(RequireValue(args, i));
                    i += 2;
                    break;
                case "--repository-facts":
                    repositoryFacts = RequireValue(args, i);
                    i += 2;
                    break;
                case "--format":
                    format = ParseFormat(RequireValue(args, i));
                    i += 2;
                    break;
                default:
                    throw new CliArgumentException(
                        arg.StartsWith("--", StringComparison.Ordinal)
                            ? $"Unknown option '{arg}'. {Usage}"
                            : $"Unexpected argument '{arg}'. {Usage}");
            }
        }

        // Refused rather than defaulted: repository facts are filed under a subject, and the obvious
        // default -- "whatever repository the command ran in" -- would key one directory's facts to
        // whichever checkout happened to be current, which is the guess this whole surface refuses to
        // make (spec/baton.md §12).
        if (repositoryFacts is { Length: > 0 } && repository is not { Length: > 0 } && !help)
        {
            throw new CliArgumentException(
                $"'--repository-facts' needs '--repository <id>': a checked-in fact is filed under a " +
                $"subject, and this verb will not infer one from the current directory. {Usage}",
                "add '--repository <id>' naming the repository those facts belong to.");
        }

        // Refused rather than given an empty population that reads as "no conflicts found": the fleet
        // store's boundary (FleetMemory's remarks; spec/baton.md §12) puts checked-in facts on the
        // repository side of it.
        if (repositoryFacts is { Length: > 0 } && FleetMemory.IsFleet(repository) && !help)
        {
            throw new CliArgumentException(
                FleetMemory.GitIdentityRefusal(
                    "--repository-facts with --repository",
                    "checked-in facts are the repository's own, which the fleet store never holds.") +
                $" {Usage}",
                "name the repository the facts are checked into.");
        }

        // Refused rather than ordered (#2040). '--check' asks whether the projections are already
        // current and '--apply' makes them current, so a run carrying both would rewrite the very
        // targets it was measuring and could never exit anything but 0 -- a gate that cannot fail. The
        // '&& !help' is the same posture the guard below it takes: '--help' prints usage rather than
        // adjudicating an invocation nobody is asking to run.
        if (check && apply && !help)
        {
            throw new CliArgumentException(
                $"'--check' and '--apply' are mutually exclusive: --check asks whether the projections " +
                $"match the canonical store, and --apply would make them match while it asked. {Usage}",
                "run '--check' to gate, then '--apply' to repair what it reported.");
        }

        return new MemorySyncOptions(repository, apply, check, format, repositoryFacts, help);
    }

    /// <summary>
    /// The repository half, canonicalized through <see cref="RepositoryIdentity.TryCanonicalize"/> —
    /// on the same reasoning <see cref="MemoryImportOptionsParser"/> gives for <c>--assert</c>. Here it
    /// is a read rather than a write, so the cost of leaving it raw is different and just as bad:
    /// <c>GitHub.com/Owner/Repo</c> would slug to a store file that does not exist and the verb would
    /// report the repository as having no memories at all.
    /// </summary>
    /// <remarks>
    /// <b>Only that half is shared.</b> <c>--assert</c> carries a SECOND refusal — a value that
    /// canonicalizes fine but whose first segment reads as no host (<c>owner/repo</c>) — which is not
    /// applied here and deliberately so: that one guards the WRITE path from inventing a store, and its
    /// rule and reasoning live in one place, <c>MemoryImportOptionsParser.RequireAHostThatAProbeCouldAnswer</c>.
    /// On this read path <c>--repository owner/repo</c> is accepted and reports no memories, which is the
    /// honest answer for a store that does not exist.
    /// </remarks>
    private static string ParseRepository(string value) =>
        FleetMemory.IsFleet(value)
            ? FleetMemory.Slug
            : RepositoryIdentity.TryCanonicalize(value) is { Length: > 0 } canonical
            ? canonical
            : throw new CliArgumentException(
                $"'{value.Trim()}' is not a repository identity: it has no host-and-path to " +
                $"canonicalize, so no store file could be named for it. {Usage}",
                "pass a canonical identity, for example 'github.com/owner/repo' — a clone URL " +
                "('https://github.com/owner/repo.git', 'git@github.com:owner/repo.git') is accepted " +
                "and normalised to one.");

    private static MemoryAuditOutputFormat ParseFormat(string value) => value switch
    {
        "text" => MemoryAuditOutputFormat.Text,
        "json" => MemoryAuditOutputFormat.Json,
        _ => throw new CliArgumentException(
            $"'--format {value}' is not a format. {Usage}", "pass 'text' or 'json'."),
    };

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
