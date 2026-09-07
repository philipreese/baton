namespace Baton.Domain;

/// <summary>
/// Which per-command-class ceiling one shell command line runs under (#1998). The ceilings themselves
/// live on <see cref="ShellCommandCeilings"/>, which is their one home; this type only names the
/// classes and <see cref="ShellCommandClassifier"/> only decides which one a command line falls in.
/// </summary>
public enum ShellCommandClass
{
    /// <summary>Everything not named by the other two — the ceiling every command had before #1998.</summary>
    Other,

    /// <summary>
    /// A gate task. Known to be progressing while it runs (it is holding the shared build lock, or
    /// compiling, or running the suite), so a ceiling sized to a fast command would only kill work
    /// that was going to finish.
    /// </summary>
    Gate,

    /// <summary>
    /// A shipping command — the push or the PR that transfers a lane's finished work out of the
    /// workspace. On this repository a push runs the pre-push hook, so a shipping command's wall clock
    /// is a gate's plus the transfer.
    /// </summary>
    Shipping,
}

/// <summary>
/// Sorts one shell command line into a <see cref="ShellCommandClass"/> (#1998, operator ruling
/// 2026-09-06: the ceiling is per-command-class).
/// <para>
/// <b>Vendor-neutral on purpose, and enforced on exactly one path today</b>
/// (<c>Baton.Vendors.CodexDynamicToolPolicy</c>). Which path that is and why only that one:
/// <see cref="ShellCommandCeilings"/>'s own remark, and the register entry it cites. Living in the
/// engine is what makes the next adapter to enforce a ceiling read this table rather than grow one.
/// </para>
/// <para>
/// <b>Deliberately not shared with <c>Baton.Vendors.ShellCommandPatternMatcher</c></b>, which does its
/// own quote-aware segmentation. That one answers "is this command line ALLOWED", and is fail-closed:
/// a character it will not trust a boundary decision around aborts the whole evaluation and the
/// command is refused. This one answers "how long may it RUN", where the fail-closed direction is the
/// opposite — an unreadable line falls through to <see cref="ShellCommandClass.Other"/>, the shortest
/// ceiling, which is what every command had before this existed. Sharing one splitter would force one
/// of the two answers onto the other's failure direction. (They are also in different projects:
/// <c>Baton.Vendors</c> → <c>Baton</c>, never the reverse.)
/// </para>
/// </summary>
public static class ShellCommandClassifier
{
    /// <summary>
    /// The table, and the whole of it: the command shapes #1998's ruling names, as leading-token
    /// prefixes. A token ending in <c>*</c> matches any segment token starting with the part before it
    /// (<c>audit-*</c> covers <c>audit-recordonce</c>, <c>audit-completeness</c>, …); every other token
    /// must match whole, case-insensitively, with <c>\</c> read as <c>/</c> so a Windows spelling of a
    /// script path classifies the same as a POSIX one.
    /// <para>
    /// <b>Leading tokens, never a substring search.</b> <c>echo "git push"</c> leads with <c>echo</c>
    /// and is <see cref="ShellCommandClass.Other"/>; <c>git status</c> does not match the two-token key
    /// <c>git push</c> and is <see cref="ShellCommandClass.Other"/>. A substring test would get both
    /// wrong and then need exclusions.
    /// </para>
    /// <para>
    /// <b>The <c>pixi run</c> spellings are the ones a lane here is REQUIRED to use</b> (CLAUDE.md,
    /// "Running tasks": always <c>pixi run &lt;task&gt;</c>, never <c>dotnet</c> directly), so they are
    /// the first thing the table has to name — without them the forbidden spelling got the gate ceiling
    /// and the mandated one did not. The keys are read off <c>pixi.toml</c> rather than guessed at:
    /// <c>test*</c> is <c>test</c>/<c>test-flow</c>/<c>test-other</c>/<c>test-no-build</c>,
    /// <c>gates*</c> is every <c>gates…</c> runner including <c>gates-fast</c> and
    /// <c>gates-fast-cover</c>, and <c>lint</c>/<c>build</c>/<c>audit-*</c> are the tasks of those
    /// names. Each is a <c>python tools/buildlock.py</c> line, which the entry above already recognises
    /// when a lane spells it that way. The bare <c>dotnet test</c>/<c>dotnet build</c> entries stay for
    /// the same reason: a lane that ran one directly ran the same work.
    /// </para>
    /// <para>
    /// This is where a newly-added gate task goes. It is not widened by guesswork: a command class is a
    /// claim that the command is progressing while it runs, and only a named task supports it — a pixi
    /// task not listed here (<c>fmt-check</c>, say) takes <see cref="ShellCommandClass.Other"/> until
    /// someone names it.
    /// </para>
    /// </summary>
    private static readonly (string[] Tokens, ShellCommandClass Class)[] Table =
    [
        (["git", "push"], ShellCommandClass.Shipping),
        (["gh", "pr", "create"], ShellCommandClass.Shipping),
        (["python", "tools/buildlock.py"], ShellCommandClass.Gate),
        (["pixi", "run", "audit-*"], ShellCommandClass.Gate),
        (["pixi", "run", "gates*"], ShellCommandClass.Gate),
        (["pixi", "run", "test*"], ShellCommandClass.Gate),
        (["pixi", "run", "lint"], ShellCommandClass.Gate),
        (["pixi", "run", "build"], ShellCommandClass.Gate),
        (["dotnet", "test"], ShellCommandClass.Gate),
        (["dotnet", "build"], ShellCommandClass.Gate),
    ];

    /// <summary>
    /// The class <paramref name="commandLine"/> runs under. A chained line takes the <b>highest</b>
    /// class any of its segments takes — <c>git add -A &amp;&amp; git push</c> is
    /// <see cref="ShellCommandClass.Shipping"/> — because the ceiling has to cover the whole line, and
    /// the expensive segment is the one that decides how long that is.
    /// </summary>
    public static ShellCommandClass Classify(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return ShellCommandClass.Other;
        }

        var highest = ShellCommandClass.Other;
        foreach (var segment in Segments(commandLine))
        {
            var segmentClass = ClassifySegment(segment);
            if (segmentClass > highest)
            {
                highest = segmentClass;
            }
        }

        return highest;
    }

    private static ShellCommandClass ClassifySegment(string segment)
    {
        var tokens = segment.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        foreach (var (keyTokens, commandClass) in Table)
        {
            if (tokens.Length >= keyTokens.Length && MatchesLeadingTokens(tokens, keyTokens))
            {
                return commandClass;
            }
        }

        return ShellCommandClass.Other;
    }

    private static bool MatchesLeadingTokens(string[] tokens, string[] keyTokens)
    {
        for (var i = 0; i < keyTokens.Length; i++)
        {
            var token = tokens[i].Replace('\\', '/');
            var key = keyTokens[i];
            var matched = key.EndsWith('*')
                ? token.StartsWith(key[..^1], StringComparison.OrdinalIgnoreCase)
                : token.Equals(key, StringComparison.OrdinalIgnoreCase);
            if (!matched)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Quote-aware split at top-level <c>;</c>, <c>&amp;&amp;</c>, <c>||</c>, <c>|</c>, a lone
    /// <c>&amp;</c>, and a newline. Quoted text is carried into its segment untouched and never opens a
    /// new one, which is what keeps a <c>git push</c> inside a string literal off the shipping ceiling.
    /// An unterminated quote simply runs to the end of the line: the worst that costs is one segment too
    /// few, which can only classify DOWN to <see cref="ShellCommandClass.Other"/>.
    /// </summary>
    private static IEnumerable<string> Segments(string commandLine)
    {
        var current = new System.Text.StringBuilder();
        var inSingleQuote = false;
        var inDoubleQuote = false;

        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];
            if (inSingleQuote)
            {
                current.Append(c);
                inSingleQuote = c != '\'';
                continue;
            }

            if (inDoubleQuote)
            {
                if (c == '\\' && i + 1 < commandLine.Length)
                {
                    current.Append(c).Append(commandLine[++i]);
                    continue;
                }

                current.Append(c);
                inDoubleQuote = c != '"';
                continue;
            }

            switch (c)
            {
                case '\'':
                    inSingleQuote = true;
                    current.Append(c);
                    continue;
                case '"':
                    inDoubleQuote = true;
                    current.Append(c);
                    continue;
                case ';' or '\n' or '\r':
                    yield return current.ToString();
                    current.Clear();
                    continue;
                case '&' or '|':
                    if (i + 1 < commandLine.Length && commandLine[i + 1] == c)
                    {
                        i++;
                    }
                    yield return current.ToString();
                    current.Clear();
                    continue;
                default:
                    current.Append(c);
                    continue;
            }
        }

        yield return current.ToString();
    }
}
