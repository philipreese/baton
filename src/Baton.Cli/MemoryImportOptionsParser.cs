using Baton.Accounting;
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
        "Usage: baton memory import [--dry-run] [--root <dir>]... [--assert <path>=<repository>]... " +
        "[--asserted-by <who>] | baton memory import --undo <manifest> [--help]";

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
        "The store is per repository IDENTITY, not per checkout: {BATON_HOME}/<repo-slug>/memory/entries.jsonl,",
        "plus links.jsonl beside it for supersession. Two worktrees of one repository therefore import into",
        "one store, and two unrelated repositories that merely share a folder name do not.",
        "",
        "Population: every root 'baton memory audit' inventories -- the live Claude roots, the archived",
        "roots, and the Codex MARKDOWN memories. Codex's memories_*.sqlite stores are NOT a memory source:",
        "they are the pipeline that produces that markdown, and are recorded in the manifest for",
        "provenance (path, size, mtime, SHA-256) with nothing read out of them.",
        "",
        "BATON'S OWN PROJECTIONS ARE SKIPPED, not imported. 'baton memory sync --apply' writes a cache into",
        "the very roots this verb reads, so a projection is recognised by the format marker on its first",
        "line and reported as projection-skipped -- filing one would feed the store its own contents once",
        "per sync/import cycle. It is reported separately from 'unfiled' because the two mean different",
        "things: an unfiled file is one you can place with --assert, and a projection is never imported",
        "under any repository.",
        "",
        "  --dry-run           Compute everything and write NOTHING -- no entries, and no manifest either.",
        "  --root <dir>        Import only this discovered root; repeatable. It SELECTS from what discovery",
        "                      found and cannot add a directory discovery did not; an unmatched path is an",
        "                      error rather than a new root. It narrows the manifest's machinery rows too,",
        "                      so a filtered run accounts for the roots it looked at and no others.",
        "                      It selects IMPORTABLE roots only, which is a SMALLER set than the one",
        "                      'baton memory audit' reports: a Codex memories_*.sqlite root and every",
        "                      Antigravity root are audited but never imported, and naming one here is an",
        "                      error rather than a no-op run.",
        "  --assert <path>=<repository>",
        "                      Assert which repository a root belongs to, when git cannot answer -- an",
        "                      archived root, a root whose checkout is gone, a per-machine vendor root.",
        "                      Repeatable. <path> is the memory root directory or the checkout it came",
        $"                      from; the assertion is appended to {BatonPaths.MemoryAliasFileName} and reused by",
        "                      later runs. It is CONSULTED ONLY where the probe produced nothing and can",
        "                      never override a repository git actually answered for. <repository> is",
        "                      canonicalized the same way a git probe's answer is, so 'GitHub.com/Owner/Repo',",
        "                      'github.com/owner/repo' and 'https://github.com/Owner/Repo.git' are ONE store;",
        "                      a string with no host-and-path in it is refused rather than made into a store.",
        "  --asserted-by <who> Who is asserting. Defaults to this machine's user name.",
        "  --undo <manifest>   Remove exactly the entries a previous run appended, per its manifest. Source",
        "                      files are untouched, because the import never touched them either. Entries an",
        "                      EARLIER import had already written are not removed.",
        "",
        "An entry's kind is declared by the file's own front-matter, else inferred from its filename prefix",
        "and recorded as inferred, else 'unknown'. It is NEVER inferred from what the memory says. Entries",
        "from an archived root are historical notes whatever they declare, and are linked to the live entry",
        "that supersedes them when the two share a repository and a filename and differ in content. That link",
        "is its own row in links.jsonl rather than a field on an entry, so it lands whichever run discovers",
        "it -- importing the live roots today and the archive tomorrow gives the same links as one run would.",
        "",
        "What this verb cannot do: it will not guess a subject. A root whose checkout is gone, an archived",
        "root whose flattened name decodes to no checkout, and a per-machine root like ~/.codex/memories",
        "that encodes no checkout at all are all reported UNFILED and imported nowhere until an operator",
        "asserts their repository with --assert. A subject is never inferred from a filename: a checkout",
        "whose origin names one repository while its memory files are named after another is imported under",
        "the repository GIT answered for, and adjudicating it per entry would need the entries' text.",
    ];

    public static MemoryImportOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var dryRun = false;
        var help = false;
        string? undo = null;
        string? assertedBy = null;
        var roots = new List<string>();
        var assertions = new List<MemoryImportAssertion>();

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
                case "--assert":
                    assertions.Add(ParseAssertion(RequireValue(args, i)));
                    i += 2;
                    break;
                case "--asserted-by":
                    assertedBy = RequireValue(args, i).Trim();
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
        if (undo is { Length: > 0 } && (dryRun || roots.Count > 0 || assertions.Count > 0 || assertedBy is not null))
        {
            throw new CliArgumentException(
                $"'--undo' takes no other options: it replays one manifest in full. {Usage}",
                "run '--undo <manifest>' on its own.");
        }

        if (assertedBy is { Length: 0 })
        {
            throw new CliArgumentException(
                $"'--asserted-by' cannot be empty: an unattributed assertion is indistinguishable from a " +
                $"measurement. {Usage}");
        }

        return new MemoryImportOptions(dryRun, roots, assertions, assertedBy, undo, help);
    }

    /// <summary>
    /// Splits <c>&lt;path&gt;=&lt;repository&gt;</c> at its LAST <c>=</c>, and canonicalizes the
    /// repository half through <see cref="RepositoryIdentity.TryCanonicalize"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The split is at the last <c>=</c></b> because a repository identity never contains one and a
    /// Windows path can — splitting at the first would break a path that holds one, and the last
    /// separator is the one that always divides the pair correctly.
    /// </para>
    /// <para>
    /// <b>The repository half is canonicalized here, on the write path, and nowhere else.</b> An
    /// assertion is stored and then slugged into a store filename
    /// (<see cref="RepositoryIdentity.FileSlugFor"/>), so <c>GitHub.com/Owner/Repo</c> left as typed is
    /// a second store file for a repository git derives as <c>github.com/owner/repo</c> — one
    /// repository, two <c>entries.jsonl</c>, every entry in both, no error. Canonicalizing at the point
    /// the operator's string enters the system is what makes "one repository, one store file" a
    /// property rather than a convention. It is deliberately NOT also done when the alias file is read
    /// back: that file is append-only and hand-editable, this verb has never shipped, so no
    /// non-canonical row can exist in the wild, and a read-time rewrite would silently change what a
    /// row an operator can see says.
    /// </para>
    /// <para>
    /// A value that canonicalizes to nothing is refused rather than stored raw — the refusal is what
    /// keeps a store called <c>hello-world-&lt;digest&gt;</c> from existing.
    /// </para>
    /// </remarks>
    private static MemoryImportAssertion ParseAssertion(string value)
    {
        var separator = value.LastIndexOf('=');
        if (separator <= 0 || separator == value.Length - 1)
        {
            throw new CliArgumentException(
                $"'--assert {value}' is not a '<path>=<repository>' pair. {Usage}",
                "for example: --assert \"C:\\Users\\me\\.codex\\memories=github.com/owner/repo\".");
        }

        var repository = value[(separator + 1)..];
        if (RepositoryIdentity.TryCanonicalize(repository) is not { Length: > 0 } canonical ||
            !HasHostAndPath(canonical))
        {
            throw new CliArgumentException(
                $"'{repository.Trim()}' is not a repository identity: it has no host-and-path to " +
                $"canonicalize, so nothing could name a store file for it. {Usage}",
                "assert a canonical identity, for example 'github.com/owner/repo' — a clone URL " +
                "('https://github.com/owner/repo.git', 'git@github.com:owner/repo.git') is accepted " +
                "and normalised to one.");
        }

        return new MemoryImportAssertion(value[..separator].Trim(), canonical);
    }

    private static bool HasHostAndPath(string canonical)
    {
        if (canonical.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var slash = canonical.IndexOf('/');
        if (slash < 0)
        {
            return false;
        }

        var host = canonical[..slash];
        var path = canonical[(slash + 1)..];

        return host.Contains('.') || path.Contains('/');
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
