using System.Linq;
using System.Text.Json;

namespace Baton.Vendors;


/// <summary>
/// Evaluates a shell command line against a pattern allowlist using claude-compatible
/// <c>Bash(pattern)</c> glob semantics, enforcing strict shell metacharacter rejection (#659)
/// and word-boundary matching for trailing-wildcard patterns (#1679).
/// <para>
/// <b>A trailing-<c>*</c> pattern <c>P*</c> is matched by two branches on whether <c>P</c> itself ends
/// in whitespace, five conditions total</b> — the full accepting set, stated the way the code branches
/// rather than as a summary of it. #1683's second round found the first correction still false in both
/// copies of this rule (here and spec/baton.md §9): its three cases silently assumed <c>P</c> never
/// ends in whitespace, so it mis-described the one branch (<c>:204</c>) that exists precisely because
/// some patterns do — including <c>git merge *</c> and <c>git -c *</c>, the two live deny patterns that
/// take it. No condition below spans both branches; that is how the wording drifted twice.
/// <list type="number">
/// <item><b><c>P</c> ends in whitespace</b> (e.g. <c>"git merge *"</c>):
/// <list type="bullet">
/// <item>the trimmed line <b>equals</b> <c>P</c> with its own trailing whitespace trimmed — bare
/// <c>git merge</c>;</item>
/// <item>the trimmed line <b>starts with <c>P</c></b> — the word boundary is already inside <c>P</c>,
/// so the next character may be anything — <c>git merge origin/main</c>. This is <em>not</em> "starts
/// with <c>P</c> followed by whitespace": a second space is not required.</item>
/// </list>
/// </item>
/// <item><b><c>P</c> does not end in whitespace</b> (e.g. <c>"git diff*"</c>):
/// <list type="bullet">
/// <item>the trimmed line <b>equals</b> <c>P</c> — <c>git log</c>;</item>
/// <item>the line starts with <c>P</c> and the next character is <b>whitespace</b> — the word boundary
/// (<c>git diff*</c> matches <c>git diff --stat</c>, never <c>git difftool</c> or <c>git diff-index</c>;
/// <c>git merge*</c>, unlike <c>git merge *</c> above, never matches <c>git merge-base</c>) —
/// <c>git log --oneline</c>;</item>
/// <item>the line starts with <c>P</c>, <c>P</c>'s last space-delimited token is <b>flag-shaped</b>
/// (starts with <c>-</c>), and the next character is anything at all — the attached-argument branch
/// (<c>git grep -O*</c> matches <c>git grep -Ocalc</c>, <c>git grep --open-files-in-pager*</c> matches
/// <c>…-pager=calc</c>).</item>
/// </list>
/// </item>
/// </list>
/// The last condition is what makes <c>=</c> accept. Before #1683 the <c>=</c> accept sat <em>above</em>
/// the flag-shape test and applied to every non-whitespace-terminated prefix, so <c>git log*</c> matched
/// <c>git log=x</c> — a widening nothing documented and nothing gated. It is now inside that condition.
/// </para>
/// <para>
/// <b>Prefix matching is anchored at the start of the line, so it cannot bound an option that can
/// move.</b> A deny pattern only ever catches the spelling and position it was written in, and
/// <c>git</c> accepts neither constraint (short-flag clustering <c>-nOcalc</c>, reordering, unambiguous
/// long-option abbreviation on any <c>parse-options</c> subcommand, doubled spaces). Bounding an
/// <em>option</em> therefore needs <see cref="IsDeniedByOptionToken"/>, not a deny pattern — see that
/// method (#1683 F1/F2).
/// </para>
/// </summary>
public static class ShellCommandPatternMatcher
{
    /// <summary>
    /// The claude/agy tool names a shell command line can be read from — claude's <c>Bash</c> and
    /// agy's <c>run_command</c>. The one canonical list (record-once): the grant amender's
    /// pattern derivation and the gate UI's command display both gate on this rather than each
    /// restating the pair, and any other tool name reads back no command line at all.
    /// </summary>
    public static readonly string[] ShellToolNames = ["Bash", "run_command"];

    /// <summary>
    /// Reads the raw shell command line (e.g. <c>"rm -rf build/"</c>) out of a shell tool's asked
    /// input, or returns <see langword="false"/> when <paramref name="toolName"/> isn't a recognized
    /// shell tool (<see cref="ShellToolNames"/>) or the input JSON can't be parsed. This is the
    /// display/derivation seam only: callers that need a scoped <em>pattern</em> pass the result
    /// through <see cref="ExtractCommandFamily"/> themselves, which is where the fail-closed
    /// metacharacter rule lives.
    /// </summary>
    /// <param name="toolName">The originally-asked tool name (e.g. <c>"Bash"</c>).</param>
    /// <param name="toolInputJson">The originally-asked tool input JSON.</param>
    /// <param name="commandLine">The read command line, or <see langword="null"/> on any miss.</param>
    public static bool TryReadCommandLine(string toolName, string toolInputJson, out string? commandLine)
    {
        commandLine = null;
        if (toolName is null || toolInputJson is null || !ShellToolNames.Contains(toolName, StringComparer.Ordinal))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(toolInputJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            // "command" is claude's Bash tool_input key; "CommandLine" is agy's run_command arg key
            // (AgyHookCheckCommand reads the same name for the same tool).
            if (doc.RootElement.TryGetProperty("command", out var commandProp) &&
                commandProp.ValueKind == JsonValueKind.String)
            {
                commandLine = commandProp.GetString();
            }
            else if (doc.RootElement.TryGetProperty("CommandLine", out var commandLineProp) &&
                commandLineProp.ValueKind == JsonValueKind.String)
            {
                commandLine = commandLineProp.GetString();
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return commandLine is not null;
    }

    /// <summary>
    /// Returns <see langword="true"/> iff <paramref name="commandLine"/> contains no unquoted shell
    /// metacharacters and matches at least one pattern in <paramref name="patterns"/>.
    /// </summary>
    /// <param name="commandLine">The command line to evaluate.</param>
    /// <param name="patterns">The pattern allowlist (e.g. <c>["git *"]</c>).</param>
    public static bool IsAllowed(string? commandLine, IReadOnlyList<string>? patterns)
    {
        if (string.IsNullOrWhiteSpace(commandLine) || patterns is null || patterns.Count == 0)
        {
            return false;
        }

        bool inSingleQuote = false;
        bool inDoubleQuote = false;

        for (int i = 0; i < commandLine.Length; i++)
        {
            char c = commandLine[i];

            if (inSingleQuote)
            {
                // Single quotes are fully literal in POSIX shells — no expansion of any kind,
                // not even a backslash escape — so nothing inside them can execute. The only
                // character that matters is the closing quote. Do not add substitution checks here.
                if (c == '\'')
                {
                    inSingleQuote = false;
                }
                continue;
            }

            if (inDoubleQuote)
            {
                if (c == '\\')
                {
                    // A backslash inside double quotes escapes the next character (bash does this
                    // for $ ` " \ and newline). Skipping it is what keeps `"\$(x)"` — an escaped,
                    // non-executing literal — allowed, while `"\a$(x)"` still trips the $( below.
                    i++;
                    continue;
                }
                // Command substitution and parameter expansion STILL fire inside double quotes:
                // `"$(cmd)"`, "`cmd`", and `"${x}"` all execute or expand. The unquoted branch's
                // metacharacter scan never runs in here, so these must be rejected explicitly or a
                // scoped grant is escaped through a quoted substitution (the first cut missed this).
                if (c == '`')
                {
                    return false;
                }
                if (c == '$' && i + 1 < commandLine.Length && commandLine[i + 1] is '(' or '{')
                {
                    return false;
                }
                if (c == '"')
                {
                    inDoubleQuote = false;
                }
                continue;
            }

            if (c == '\'')
            {
                inSingleQuote = true;
                continue;
            }

            if (c == '"')
            {
                inDoubleQuote = true;
                continue;
            }

            // Unquoted metacharacters: ; & | ` $ < > ( ) \n \r \
            // A bare unquoted '$' is denied outright, not merely '$(' / '${'. Besides command
            // substitution and expansion, a bare '$' before a quote opens ANSI-C quoting ($'...'),
            // whose backslash-escaped quote (\') is a NON-terminating escape in bash but closes this
            // scanner's escape-free single-quote branch one character early. A later stray ' rebalances
            // the parity, hiding a live ';' inside a region the scanner still believes is quoted -- a
            // confirmed escape from a scoped grant: `git $'\''; rm -rf / #'` executes rm outside `git *`.
            // Denying '$' outright also covers $VAR, ${...}, $((...)) and $[...]. A scoped command needs
            // none of these unquoted; a literal dollar can be quoted ("$5") to pass.
            if (c is ';' or '&' or '|' or '`' or '$' or '<' or '>' or '(' or ')' or '\n' or '\r' or '\\')
            {
                return false;
            }
        }

        if (inSingleQuote || inDoubleQuote)
        {
            return false;
        }

        string trimmed = commandLine.Trim();

        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                continue;
            }

            if (pattern.EndsWith('*'))
            {
                string prefix = pattern[..^1];
                if (prefix.Length > 0 && char.IsWhiteSpace(prefix[^1]))
                {
                    if (trimmed.Equals(prefix.TrimEnd(), StringComparison.Ordinal) ||
                        trimmed.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
                else
                {
                    if (trimmed.Equals(prefix, StringComparison.Ordinal))
                    {
                        return true;
                    }

                    if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        if (trimmed.Length > prefix.Length)
                        {
                            char next = trimmed[prefix.Length];
                            if (char.IsWhiteSpace(next))
                            {
                                return true;
                            }

                            // Flag-driven prefixes (e.g. "git grep -O*" or "git grep --open-files-in-pager*")
                            // where the last whitespace-delimited token in the prefix starts with '-'
                            // match option arguments attached directly without whitespace -- both the
                            // bare-attached form (-Ocalc) and the '=' form (--open-files-in-pager=calc).
                            //
                            // #1683 F6: '=' used to accept ABOVE this test, ungated by flag shape, so
                            // every trailing-'*' pattern whose prefix did not end in whitespace also
                            // matched an '='-suffixed continuation -- `git log*` matched `git log=x`.
                            // Nothing in the current lists is exploitable through that, but it was an
                            // unstated widening on the branch a future allow pattern would trip over, so
                            // the accept now sits under the same flag-shape gate as the branch it
                            // belongs to. A non-flag prefix accepts on the word boundary alone.
                            var lastSpace = prefix.LastIndexOf(' ');
                            var lastToken = lastSpace >= 0 ? prefix[(lastSpace + 1)..] : prefix;
                            if (lastToken.StartsWith('-'))
                            {
                                return true;
                            }
                        }
                    }
                }
            }
            else
            {
                if (trimmed.Equals(pattern, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The <c>DenyAlways</c> rung's standing-"never" check (0022, M-Phase-6 #390) — reused by
    /// <see cref="EvaluateChainedCommand"/> (segment-level since #1685) to refuse a <c>run_command</c>
    /// whose command line matches a persisted
    /// <see cref="PermissionGrant.DeniedShellCommandPatterns"/> entry, deny-beats-allow. (claude also
    /// carries this rung on <c>--disallowedTools</c>, but that flag only matches an unchained command
    /// line — #1731 — so <c>EvaluateChainedCommand</c> reaches this on both vendors, on both a scoped
    /// and an unscoped grant.) Returns
    /// <see langword="true"/> iff <paramref name="commandLine"/> matches at least one pattern in
    /// <paramref name="deniedPatterns"/>. Same glob shape and the same metacharacter fail-closed rules as
    /// <see cref="IsAllowed"/> (deliberately reuses it): a command this scanner cannot parse safely is
    /// not matched against the deny list either, since whatever else grants it (categorical
    /// <see cref="PermissionGrant.RunShellCommands"/> or an allow pattern) already refuses it on the
    /// same unparseable-metacharacter grounds.
    /// </summary>
    public static bool IsDenied(string? commandLine, IReadOnlyList<string>? deniedPatterns) =>
        IsAllowed(commandLine, deniedPatterns);

    /// <summary>
    /// The <b>position-independent</b> half of the deny side (#1683 F2): returns <see langword="true"/>
    /// iff any whitespace-separated token of <paramref name="commandLine"/> starts with any entry in
    /// <paramref name="deniedOptionTokens"/> (<see cref="PermissionGrant.DeniedShellOptionTokens"/>).
    /// Entries are literal token <em>prefixes</em>, so <c>"--output"</c> catches <c>--output=C:/x</c>,
    /// the separated <c>--output C:/x</c>, and <c>--output-indicator-new=x</c> alike.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a deny pattern cannot do this job.</b> <see cref="IsDenied"/> prefix-matches the line
    /// anchored at its start, so a deny entry binds one option in one position and one spelling.
    /// <c>git log --output=&lt;file&gt; --format=format:&lt;bytes&gt;</c> is an arbitrary file write
    /// admitted by the <c>review</c> role's own <c>git log*</c> allow pattern — no metacharacter, no
    /// redirection, so #659's scan never sees it — and adding <c>git log --output*</c> to the deny list
    /// would be walked past by reordering the option, doubling a space, or (on any <c>parse-options</c>
    /// subcommand) abbreviating it. Matching every token instead binds the option wherever it sits.
    /// </para>
    /// <para>
    /// <b>It over-matches, deliberately, in the fail-closed direction.</b> A denied prefix appearing as
    /// a token of a quoted argument (<c>git log --format="x --output=y"</c> splits to a token starting
    /// <c>--output</c>) denies, and so does a read-only sibling option sharing the prefix
    /// (<c>--output-indicator-new</c>). Both cost a reviewer a formatting flag; the alternative — a full
    /// argv parse per vendor subcommand — is the sort of thing that is wrong quietly.
    /// </para>
    /// <para>
    /// <b>Every quote character is removed from a token before the prefix test, not just a leading
    /// one.</b> A shell splits words BEFORE removing quotes, so a quote can sit anywhere inside an
    /// option name and the command still arrives at <c>git</c> as one unquoted word:
    /// <c>git log --outpu"t"=C:/x</c> and <c>git log -"-"output=C:/x</c> both reach it as
    /// <c>--output=C:/x</c>. Stripping only the leading quote left both matching nothing — the same
    /// "walked past by another spelling" defect this method exists to fix, inside the fix (found by
    /// this PR's second reader). Removing them all is safe for exactly the reason the caller contract
    /// below states: the metacharacter scan has already run, so no substitution can be hiding in the
    /// quotes, and dropping them is precisely what the shell itself does. It does not widen the deny
    /// to a quoted VALUE — <c>git log --grep="--output"</c> normalizes to <c>--grep=--output</c>, which
    /// does not START with the entry and stays allowed.
    /// </para>
    /// <para>
    /// <b>Not expressible on <c>--disallowedTools</c> — this channel is hook-only, on both vendors.</b>
    /// claude's <c>Bash(pattern)</c> matching is against the whole command line and anchored
    /// (<c>docs/vendor-capabilities.md</c>'s #1461 subsection measured <c>Bash(git log*)</c> denying
    /// <c>git log</c>, and measured nothing about a mid-line token), so what could be written there is
    /// another positional pattern — the defect F1/F2 document, not the fix. Whether that flag can
    /// express a mid-line token deny at all is <b>unmeasured</b>, and this states the gap rather than
    /// asserting claude cannot. <c>ClaudeWorkerAdapter.BuildDisallowedTools</c> therefore emits nothing
    /// from this field, and both hooks enforce it themselves.
    /// </para>
    /// <para>
    /// Caller contract: run this <b>after</b> the deny/allow pattern pass, which is what has already
    /// applied <see cref="IsAllowed"/>'s metacharacter scan to the line. Deny wins over any allow.
    /// </para>
    /// </remarks>
    public static bool IsDeniedByOptionToken(string? commandLine, IReadOnlyList<string>? deniedOptionTokens)
    {
        if (string.IsNullOrWhiteSpace(commandLine) || deniedOptionTokens is null ||
            deniedOptionTokens.Count == 0)
        {
            return false;
        }

        foreach (var rawToken in commandLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var token = rawToken.Replace("\"", string.Empty, StringComparison.Ordinal)
                .Replace("'", string.Empty, StringComparison.Ordinal);
            foreach (var deniedToken in deniedOptionTokens)
            {
                if (string.IsNullOrWhiteSpace(deniedToken))
                {
                    continue;
                }

                if (token.StartsWith(deniedToken, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Derives a command's family (its first whitespace-delimited token, e.g. <c>"rm"</c> out of
    /// <c>"rm -rf build/"</c>) for scoping a new <see cref="PermissionGrant.ShellCommandPatterns"/> or
    /// <see cref="PermissionGrant.DeniedShellCommandPatterns"/> entry (0022's <c>AllowCommandInRoom</c>
    /// / <c>DenyAlways</c> rungs, M-Phase-6 #390). Returns <see langword="null"/> — never a guess — when
    /// <paramref name="commandLine"/> is empty or its first token opens with a shell metacharacter this
    /// matcher already treats as unsafe to reason about (<see cref="IsAllowed"/>'s own set): persisting
    /// a pattern derived from an unparseable head would scope a standing permission to something this same
    /// matcher could not evaluate consistently later.
    /// </summary>
    public static string? ExtractCommandFamily(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        var trimmed = commandLine.TrimStart();
        var end = 0;
        while (end < trimmed.Length && !char.IsWhiteSpace(trimmed[end]) && Array.IndexOf(MetaCharacters, trimmed[end]) < 0)
        {
            end++;
        }

        return end == 0 ? null : trimmed[..end];
    }

    private static readonly char[] MetaCharacters =
        [';', '&', '|', '`', '$', '<', '>', '(', ')', '\n', '\r', '\\', '\'', '"'];

    /// <summary>
    /// The overall result of <see cref="EvaluateChainedCommand"/> — <see cref="Allowed"/> when every
    /// chained segment independently matched an allowed pattern and none matched a denied one,
    /// <see cref="DeniedSegment"/> when a specific segment failed that test, and
    /// <see cref="Unparseable"/> when the scanner would not trust its own segment boundaries at all.
    /// </summary>
    public enum ScopedShellVerdict
    {
        Allowed,
        DeniedSegment,
        Unparseable,
    }

    /// <param name="Verdict">The overall decision.</param>
    /// <param name="Segment">
    /// The offending segment, for <see cref="ScopedShellVerdict.DeniedSegment"/> only. An
    /// <see cref="ScopedShellVerdict.Unparseable"/> command has no segment boundary this scanner
    /// trusts, and an <see cref="ScopedShellVerdict.Allowed"/> command has nothing to name.
    /// </param>
    /// <param name="Reason">A denial reason a person can act on; <see langword="null"/> when allowed.</param>
    public readonly record struct ScopedShellResult(ScopedShellVerdict Verdict, string? Segment, string? Reason)
    {
        public bool IsAllowed => Verdict == ScopedShellVerdict.Allowed;
    }

    /// <summary>
    /// The hook-side second enforcement layer for a scoped shell grant (#1459, #1461's measured
    /// hole). <see cref="IsAllowed"/> matches <paramref name="commandLine"/> as one whole string — the
    /// same thing claude's own <c>Bash(pattern)</c> matching does — so an unlisted command riding a
    /// <c>;</c>/<c>&amp;&amp;</c>/<c>||</c>/<c>|</c> chain after an allowed prefix matches too (`git
    /// diff; echo escaped` and `git diff | grep baseline` both ran, unblocked, under a
    /// <c>Bash(git diff*)</c> grant — see <c>docs/vendor-capabilities.md</c>'s #1461 subsection).
    /// This method splits the command at top-level (unquoted) chain boundaries first and requires
    /// EVERY resulting segment to independently satisfy the grant: match at least one allowed
    /// pattern, and match no denied one.
    /// </summary>
    /// <remarks>
    /// On a SCOPED grant (a non-empty <paramref name="allowedPatterns"/>), fails closed to
    /// <see cref="ScopedShellVerdict.Unparseable"/> on anything this scanner will not guess a
    /// boundary for — backticks, <c>$(...)</c>/<c>${...}</c>/a bare <c>$</c>, <c>&lt;</c>/<c>&gt;</c>
    /// redirection, subshell parens, an embedded newline, or an unterminated quote — rather than
    /// segment around it and risk a hidden command riding through. Once split, each segment is itself
    /// checked through <see cref="IsAllowed"/>'s own quote-tracking scan, so a segment that somehow
    /// still carries a bare metacharacter denies through the same path <see cref="IsAllowed"/>
    /// already has. On an UNSCOPED grant with a standing deny list, this unconditional fail-closed
    /// behaviour does not hold — see the whole-line fold in <see cref="TrySegmentChainedCommand"/>
    /// (its <c>permissiveMetacharacters</c> parameter) and the ruling recorded once at
    /// spec/baton.md §9.
    /// </remarks>
    /// <param name="commandLine">The full shell command line as claude's <c>Bash</c> tool received it.</param>
    /// <param name="allowedPatterns">
    /// The grant's allowed patterns. An empty/null list is the unscoped-shell case: every segment
    /// passes the allow half of the check unconditionally, and only <paramref name="deniedPatterns"/>
    /// (if any) can still refuse it — the segmenter's own fail-closed
    /// <see cref="ScopedShellVerdict.Unparseable"/> verdict is a SCOPED-grant-only refusal (see the
    /// remarks above); on this scope the fold takes over instead. Callers with a non-empty allow list
    /// get the original narrowing — every segment must match one of these patterns.
    /// </param>
    /// <param name="deniedPatterns">The grant's standing-deny patterns, or empty/null when none apply.</param>
    public static ScopedShellResult EvaluateChainedCommand(
        string? commandLine, IReadOnlyList<string>? allowedPatterns, IReadOnlyList<string>? deniedPatterns)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return new ScopedShellResult(
                ScopedShellVerdict.Unparseable, null, "unparseable (empty command line)");
        }

        // #1731 operator ruling, recorded once at spec/baton.md §9: fail-closed metacharacter
        // rejection stays exactly as-is for a SCOPED grant; an UNSCOPED grant with a deny list takes
        // the permissive path below instead.
        bool unscopedWithDeny = allowedPatterns is not { Count: > 0 } && deniedPatterns is { Count: > 0 };

        bool folded = false;

        if (!TrySegmentChainedCommand(commandLine, unscopedWithDeny, out var segments, out var unparseableReason))
        {
            if (!unscopedWithDeny)
            {
                return new ScopedShellResult(ScopedShellVerdict.Unparseable, null, unparseableReason!);
            }

            // Never Unparseable-deny on this scope (spec/baton.md §9): a boundary this scanner will
            // not guess for is folded into one segment rather than refused. #1748 F2: because the
            // line's OWN segmentation already failed, the deny match below scans every token offset
            // in this one folded segment rather than only its head -- see IsDeniedByTokenizedHead's
            // anyOffset param doc.
            segments = [commandLine.Trim()];
            folded = true;
        }

        foreach (var segment in segments)
        {
            if (deniedPatterns is { Count: > 0 })
            {
                bool segmentDenied = unscopedWithDeny
                    ? IsDeniedByTokenizedHead(segment, deniedPatterns, anyOffset: folded)
                    : IsAllowed(segment, deniedPatterns);

                if (segmentDenied)
                {
                    // #1920: a deny is standing, so the useful thing to say is that retrying a
                    // variant of the same family cannot work — the measured lane spent a step on
                    // `git remote -v` and had nothing to go on afterwards.
                    return new ScopedShellResult(
                        ScopedShellVerdict.DeniedSegment, segment,
                        $"segment '{segment}' matches this session's standing deny list, which is "
                        + "permanently closed for this role — a variant of the same command will be "
                        + $"denied too{RenderGrantedPatternSuffix(allowedPatterns)}");
                }
            }

            if (allowedPatterns is { Count: > 0 } && !IsAllowed(segment, allowedPatterns))
            {
                return new ScopedShellResult(
                    ScopedShellVerdict.DeniedSegment, segment,
                    $"segment '{segment}' does not match any pattern this session's grant allows"
                    + RenderGrantedPatternSuffix(allowedPatterns));
            }
        }

        return new ScopedShellResult(ScopedShellVerdict.Allowed, null, null);
    }

    /// <summary>
    /// #1920: the granted allow list, rendered whole and in catalog order. Deliberately NOT ranked or
    /// filtered by similarity to the refused segment — nothing here computes such a ranking, and a
    /// message calling three of thirteen patterns the "closest" ones taught the model that the grant
    /// was those three. The cap exists only so a pathologically long list cannot swamp the refusal,
    /// and it says so when it bites rather than truncating silently. Empty on an unscoped grant,
    /// where there is no allow list to name.
    /// </summary>
    private static string RenderGrantedPatternSuffix(IReadOnlyList<string>? allowedPatterns)
    {
        var patterns = (allowedPatterns ?? [])
            .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
            .ToArray();
        if (patterns.Length == 0)
        {
            return string.Empty;
        }

        return patterns.Length <= MaxRenderedGrantedPatterns
            ? $"; this session's granted shell patterns are: {string.Join(", ", patterns)}"
            : $"; this session's granted shell patterns include: "
              + $"{string.Join(", ", patterns.Take(MaxRenderedGrantedPatterns))} "
              + $"({MaxRenderedGrantedPatterns} of {patterns.Length} shown)";
    }

    private const int MaxRenderedGrantedPatterns = 24;

    /// <summary>
    /// The unscoped-grant deny match (#1731): compares a deny pattern's whitespace-tokenized head
    /// (<c>"gh label*"</c> → <c>["gh", "label"]</c>) against <paramref name="anyOffset"/>-controlled
    /// tokens of <paramref name="segment"/>, exact and ordinal. Deliberately not a substring/prefix
    /// scan like <see cref="IsAllowed"/> — the accepted cost of that choice is recorded once at
    /// spec/baton.md §9, not restated here. This grammar also diverges from <see cref="IsAllowed"/>'s
    /// on the two points that matter for writing a new deny entry: a pattern with no trailing
    /// <c>*</c> matches a token PREFIX here (widening, fail-closed direction) rather than requiring
    /// whole-line equality, and a trailing <c>*</c> never reaches inside a token (narrowing —
    /// <c>"gh label*"</c> does not deny <c>gh labelfoo</c>) rather than word-boundary matching a
    /// continuation.
    /// </summary>
    /// <param name="anyOffset">
    /// #1748 F2: when <see langword="true"/> (the whole-line fold path, where
    /// <see cref="TrySegmentChainedCommand"/> could not find a trustworthy boundary), the pattern's
    /// token sequence is matched starting at EVERY token offset in <paramref name="segment"/>, not
    /// only offset 0 — a denied command need not be in head position once the line's own
    /// segmentation is already untrustworthy. This over-denies (e.g. <c>echo gh label</c>), which is
    /// the accepted fail-closed direction on this path; see spec/baton.md §9.
    /// </param>
    private static bool IsDeniedByTokenizedHead(
        string segment, IReadOnlyList<string> deniedPatterns, bool anyOffset = false)
    {
        var tokens = segment.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (int t = 0; t < tokens.Length; t++)
        {
            tokens[t] = StripWrapperCharacters(tokens[t]);
        }

        if (tokens.Length == 0)
        {
            return false;
        }

        foreach (var pattern in deniedPatterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                continue;
            }

            var patternBody = pattern.EndsWith('*') ? pattern[..^1] : pattern;
            var patternTokens = patternBody.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (patternTokens.Length == 0 || patternTokens.Length > tokens.Length)
            {
                continue;
            }

            int maxStart = anyOffset ? tokens.Length - patternTokens.Length : 0;
            for (int start = 0; start <= maxStart; start++)
            {
                bool matches = true;
                for (int i = 0; i < patternTokens.Length; i++)
                {
                    if (!tokens[start + i].Equals(patternTokens[i], StringComparison.Ordinal))
                    {
                        matches = false;
                        break;
                    }
                }

                if (matches)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Strips a leading backtick, <c>$(</c>, bare <c>(</c>, or quote character off a token (#1748 F2)
    /// — the wrapper characters a fold-path token can carry when the line's own segmentation already
    /// failed to find a boundary (<c>` `gh</c> from `` `gh label create x` ``, <c>$(gh</c> from
    /// <c>$(gh label create x)</c>). Only strips from the front: the token's own text past that point
    /// is compared unchanged.
    /// </summary>
    private static string StripWrapperCharacters(string token)
    {
        int start = 0;
        while (start < token.Length)
        {
            if (token[start] is '`' or '(' or '\'' or '"')
            {
                start++;
                continue;
            }

            if (token[start] == '$' && start + 1 < token.Length && token[start + 1] == '(')
            {
                start += 2;
                continue;
            }

            break;
        }

        return start == 0 ? token : token[start..];
    }

    /// <summary>
    /// Splits <paramref name="commandLine"/> at top-level (unquoted) <c>;</c>, <c>&amp;&amp;</c>,
    /// <c>||</c>, <c>|</c> and a lone <c>&amp;</c> boundaries. Returns <see langword="false"/> the
    /// moment it meets a character it will not trust a boundary decision around; see
    /// <see cref="EvaluateChainedCommand"/>'s own remarks for the exact set and why.
    /// </summary>
    /// <param name="permissiveMetacharacters">
    /// #1731: when <see langword="true"/> (an unscoped grant with a standing deny list),
    /// <c>$</c>, <c>&lt;</c>, <c>&gt;</c> and <c>\</c> are ordinary characters instead of fatal ones
    /// -- routine build-tooling syntax (redirection, env-var references, Windows paths) no longer
    /// denies outright. An unquoted <c>\n</c>/<c>\r</c> becomes a segment BOUNDARY instead (#1748
    /// F1) -- closed the same way <c>;</c> is, not left as an ordinary character -- since a top-level
    /// newline really is a bash command separator, so treating it as one is more correct than folding
    /// it into the segment. Backtick, subshell parens and an unterminated quote stay fatal to a
    /// boundary decision either way; the caller falls back to evaluating the whole line as one segment
    /// rather than returning Unparseable when this flag is set.
    /// </param>
    private static bool TrySegmentChainedCommand(
        string commandLine, bool permissiveMetacharacters, out IReadOnlyList<string> segments,
        out string? unparseableReason)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inSingleQuote = false;
        bool inDoubleQuote = false;
        segments = Array.Empty<string>();

        for (int i = 0; i < commandLine.Length; i++)
        {
            char c = commandLine[i];

            if (inSingleQuote)
            {
                current.Append(c);
                if (c == '\'')
                {
                    inSingleQuote = false;
                }
                continue;
            }

            if (inDoubleQuote)
            {
                if (c == '\\' && i + 1 < commandLine.Length)
                {
                    current.Append(c);
                    current.Append(commandLine[++i]);
                    continue;
                }

                if (c == '`' || (c == '$' && i + 1 < commandLine.Length && commandLine[i + 1] is '(' or '{'))
                {
                    unparseableReason =
                        "unparseable under scoped grant (command substitution inside a quoted segment)";
                    return false;
                }

                current.Append(c);
                if (c == '"')
                {
                    inDoubleQuote = false;
                }
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
                case '$' or '<' or '>' or '\\' when permissiveMetacharacters:
                    current.Append(c);
                    continue;
                case '`' or '$' or '<' or '>' or '(' or ')' or '\\':
                    unparseableReason = c == '\\'
                        ? "unparseable under scoped grant (unsupported character '\\'); use forward slashes"
                        : $"unparseable under scoped grant (unsupported character '{c}')";
                    return false;
                case '\n' or '\r' when permissiveMetacharacters:
                    // #1748 F1: an unquoted top-level newline IS a command separator in bash, so on
                    // this scope it closes the current segment exactly as ';' does, rather than
                    // folding the whole line together. See spec/baton.md §9 for why this is the
                    // fail-closed direction (over-splits heredoc bodies / backslash continuations,
                    // which over-denies) rather than a relaxation.
                    result.Add(current.ToString());
                    current.Clear();
                    continue;
                case '\n' or '\r':
                    unparseableReason = "unparseable under scoped grant (embedded newline)";
                    return false;
                case ';':
                    result.Add(current.ToString());
                    current.Clear();
                    continue;
                case '&':
                    if (i + 1 < commandLine.Length && commandLine[i + 1] == '&')
                    {
                        i++;
                    }
                    result.Add(current.ToString());
                    current.Clear();
                    continue;
                case '|':
                    if (i + 1 < commandLine.Length && commandLine[i + 1] == '|')
                    {
                        i++;
                    }
                    result.Add(current.ToString());
                    current.Clear();
                    continue;
                default:
                    current.Append(c);
                    continue;
            }
        }

        if (inSingleQuote || inDoubleQuote)
        {
            unparseableReason = "unparseable under scoped grant (unterminated quote)";
            return false;
        }

        result.Add(current.ToString());
        var trimmed = result.Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

        if (trimmed.Count == 0)
        {
            unparseableReason = "unparseable under scoped grant (no command found)";
            return false;
        }

        segments = trimmed;
        unparseableReason = null;
        return true;
    }
}
