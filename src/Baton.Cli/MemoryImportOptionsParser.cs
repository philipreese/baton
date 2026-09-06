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
        "                      NAME THE FORGE HOST: a bare 'owner/repo' is refused too, because no default",
        "                      host is assumed for it. Write a scheme ('https://internal/repo') or an scp",
        "                      remote ('git@internal:owner/repo') to assert a host that carries no dot.",
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
    /// <para>
    /// <b>A bare <c>owner/repo</c> is refused as well, and it is a SECOND refusal</b> because it
    /// canonicalizes fine — <see cref="RepositoryIdentity.TryCanonicalize"/> supplies no host, as its
    /// own remarks explain, so <c>owner/repo</c> becomes an identity no git probe can ever answer.
    /// Storing it would give the operator a store that looks right, that the probe path never reaches,
    /// and that no error ever mentions again. See <see cref="RequireAHostThatAProbeCouldAnswer"/> for
    /// the discriminator.
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
        if (RepositoryIdentity.TryCanonicalize(repository) is not { Length: > 0 } canonical)
        {
            throw new CliArgumentException(
                $"'{repository.Trim()}' is not a repository identity: it has no host-and-path to " +
                $"canonicalize, so nothing could name a store file for it. {Usage}",
                "assert a canonical identity, for example 'github.com/owner/repo' — a clone URL " +
                "('https://github.com/owner/repo.git', 'git@github.com:owner/repo.git') is accepted " +
                "and normalised to one.");
        }

        RequireAHostThatAProbeCouldAnswer(repository, canonical);

        return new MemoryImportAssertion(value[..separator].Trim(), canonical);
    }

    /// <summary>
    /// Refuses a schemeless repository whose host half names no host — <c>owner/repo</c>, which
    /// canonicalizes to a well-formed identity that git can never produce.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The test is applied only where the operator did not say where the host is.</b> A value whose
    /// FIRST path separator is preceded by a <c>:</c> has declared one — a scheme
    /// (<c>https://internal/repo</c>), an scp remote (<c>git@internal:owner/repo</c>), or the
    /// <c>gitdir:</c> tag — as has a UNC authority (<c>\\server\share\repo.git</c>), and a dotless host
    /// is a real answer in all of those, so refusing them would make an intranet remote unassertable.
    /// It is the position of the colon rather than its presence that decides: <c>owner/repo:main</c>
    /// carries one and still declares no host, and reading presence alone would let that spelling
    /// through.
    /// </para>
    /// <para>
    /// A dot in the host is a heuristic and is knowingly one — a schemeless <c>my.group/repo</c> passes
    /// it. It is sized to the failure: what this refuses is the ONE spelling an operator reaches for by
    /// habit and Baton cannot mean, not every identity that could be wrong.
    /// </para>
    /// <para>
    /// It deliberately does NOT live in <see cref="RepositoryIdentity"/>: that type canonicalizes what a
    /// <i>probe</i> answered as well as what an operator typed, and a probe reading
    /// <c>git@internal:repo</c> legitimately yields the dotless <c>internal/repo</c>. The ambiguity is a
    /// property of operator input, so the refusal belongs on the operator's entry path.
    /// </para>
    /// </remarks>
    private static void RequireAHostThatAProbeCouldAnswer(string repository, string canonical)
    {
        var raw = repository.Trim();
        if (DeclaresWhereItsHostIs(raw))
        {
            return;
        }

        var host = canonical[..canonical.IndexOf('/')];
        if (host.Contains('.', StringComparison.Ordinal))
        {
            return;
        }

        throw new CliArgumentException(
            $"'{raw}' names no host: it canonicalizes to '{canonical}', and Baton assumes no default " +
            $"forge, so that store is one no git probe could ever reach. {Usage}",
            $"name the host too, as 'github.com/{raw}' — or write a scheme " +
            "('https://internal/owner/repo') for a host with no dot in it.");
    }

    /// <summary>
    /// Whether <paramref name="raw"/> says where its host is, per the positional rule in
    /// <see cref="RequireAHostThatAProbeCouldAnswer"/>'s remarks: a <c>:</c> before the first path
    /// separator, or a UNC authority.
    /// </summary>
    private static bool DeclaresWhereItsHostIs(string raw)
    {
        if (raw.StartsWith(@"\\", StringComparison.Ordinal) || raw.StartsWith("//", StringComparison.Ordinal))
        {
            return true;
        }

        var colon = raw.IndexOf(':');
        if (colon < 0)
        {
            return false;
        }

        var separator = raw.AsSpan().IndexOfAny('/', '\\');
        return separator < 0 || colon < separator;
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
