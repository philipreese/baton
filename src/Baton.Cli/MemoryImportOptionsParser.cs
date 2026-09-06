using Baton.Status;

namespace Baton.Cli;

/// <summary>
/// Parses <c>baton memory import</c> (#1852 phase B). Same shape as
/// <see cref="MemoryAuditOptionsParser"/> — one <see cref="CliArgumentException"/> per malformed
/// invocation, never a bare <see cref="InvalidOperationException"/>.
/// </summary>
public static class MemoryImportOptionsParser
{
    public const string Usage =
        "Usage: baton memory import [--dry-run] [--root <dir>]... | baton memory import --undo <manifest> [--help]";

    /// <summary>
    /// What <c>--help</c> prints under <see cref="Usage"/>. Every line is a place a reader's prior
    /// fills the gap wrongly if the negative is not stated (CLAUDE.md, "Writing documentation"): what
    /// this writes and what it provably does not, which roots it reads, how an entry's kind is decided
    /// and what it is never decided from, and which population it cannot file without help.
    /// </summary>
    public static IReadOnlyList<string> HelpLines { get; } =
    [
        "",
        "NON-DESTRUCTIVE BY CONSTRUCTION. Every source file is opened READ-ONLY and left byte-identical:",
        "this verb copies memory text into Baton's own store and never moves, edits, truncates or deletes",
        "a vendor's file. It writes in exactly two places, both under Baton's storage root -- the",
        "per-repository store and one import manifest -- and nowhere else on the machine.",
        "",
        "The store is per repository IDENTITY, not per checkout: {BATON_HOME}/<repo-slug>/memory/entries.jsonl.",
        "Two worktrees of one repository therefore import into one store, and two unrelated repositories",
        "that merely share a folder name do not.",
        "",
        "Population: every root 'baton memory audit' inventories -- the live Claude roots, the archived",
        "roots, and the Codex MARKDOWN memories. Codex's memories_*.sqlite stores are NOT a memory source:",
        "they are the pipeline that produces that markdown, and are recorded in the manifest for",
        "provenance (path, size, mtime, SHA-256) with nothing read out of them.",
        "",
        "  --dry-run           Compute everything and write NOTHING -- no entries, and no manifest either.",
        "  --root <dir>        Import only this discovered root; repeatable. It SELECTS from what discovery",
        "                      found and cannot add a directory discovery did not; an unmatched path is an",
        "                      error rather than a new root.",
        "  --undo <manifest>   Remove exactly the entries a previous run appended, per its manifest. Source",
        "                      files are untouched, because the import never touched them either. Entries an",
        "                      EARLIER import had already written are not removed.",
        "",
        "An entry's kind is declared by the file's own front-matter, else inferred from its filename prefix",
        "and recorded as inferred, else 'unknown'. It is NEVER inferred from what the memory says. Entries",
        "from an archived root are historical notes whatever they declare, and are linked to the live entry",
        "that supersedes them when the two share a repository and a filename and differ in content.",
        "",
        "What this verb cannot do: it will not guess a subject. A root whose checkout is gone, and a",
        "per-machine root like ~/.codex/memories that encodes no checkout at all, are reported UNFILED and",
        $"imported nowhere until an operator asserts their repository in {BatonPaths.MemoryAliasFileName}.",
        "This phase ships no writer for that file -- an assertion is added by hand, and is never inferred",
        "from a filename or allowed to override a repository git actually answered for.",
    ];

    public static MemoryImportOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var dryRun = false;
        var help = false;
        string? undo = null;
        var roots = new List<string>();

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
                case "--root":
                    roots.Add(RequireValue(args, i));
                    i += 2;
                    break;
                case "--undo":
                    undo = RequireValue(args, i);
                    i += 2;
                    break;
                default:
                    throw new CliArgumentException(
                        arg.StartsWith("--", StringComparison.Ordinal)
                            ? $"Unknown option '{arg}'. {Usage}"
                            : $"Unexpected argument '{arg}'. {Usage}");
            }
        }

        // Refused rather than quietly ordered, because both readings of the combination are plausible
        // and they are opposites: "undo, but only the roots I name" is a partial reversal this verb
        // does not offer, and "show me what the undo would do" is a mode it does not have either.
        if (undo is { Length: > 0 } && (dryRun || roots.Count > 0))
        {
            throw new CliArgumentException(
                $"'--undo' takes no other options: it replays one manifest in full. {Usage}",
                "run '--undo <manifest>' on its own.");
        }

        return new MemoryImportOptions(dryRun, roots, undo, help);
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
