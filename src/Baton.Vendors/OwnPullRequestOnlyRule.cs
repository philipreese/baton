using System.Text.RegularExpressions;

namespace Baton.Vendors;

/// <summary>
/// #2001 part 2. A lane may read the pull request <em>its own room</em> is working on, and no other one.
/// <para>
/// The measured failure: comparator arms for one issue shared a clone and a repository, so an arm-A
/// lane ran <c>gh pr list</c> and then <c>gh pr view</c> on a sibling arm's open PR and committed a
/// diff two lines from it. Per-arm clones (part 1, the conductor recipe recorded in
/// <c>benchmarks/comparator.md</c>) hide sibling <em>branches</em>; they cannot hide sibling
/// <em>PRs</em>, because <c>gh</c> talks to GitHub rather than to the clone. This rule is the half
/// that has to live in Baton.
/// </para>
/// <para>
/// <b>Two entry points, one rule, because the two enforcement points know different things.</b>
/// <see cref="RefusalFor"/> is the EVIDENCE-aware one, used by the codex broker's run-command path
/// (<see cref="CodexDynamicToolPolicy"/>): only its broker-controlled direct-create path can supply
/// a verified repository/number pair. General shell output supplies nothing.
/// <see cref="RefusalForOwnBranchOnly"/> is the one a <c>PreToolUse</c> hook can use
/// (<c>HookCheckCommand</c> for claude, <c>AgyHookCheckCommand</c> for agy). A hook decides BEFORE a
/// command runs and never sees <c>gh pr create</c>'s stdout, so it can never learn the evidence — and
/// does not need it: a governed <c>gh pr</c> verb with NO pull-request selector is resolved by
/// <c>gh</c> from the branch the room is standing on, which is the room's own by construction. So the
/// hook rule is "no selector", and EVERY selector is refused, including the room's own number, which
/// the hook has no way to recognise. The measured offender — an agy lane running <c>gh pr list</c>
/// and then <c>gh pr view 1994</c> — is refused on that path by both of those.
/// </para>
/// <para>
/// <b>Routes this rule does not cover</b>, named so a later shell-bearing role does not inherit an
/// unwritten hole. <c>gh api repos/…/pulls/…</c> and <c>gh search prs</c> reach a sibling PR without
/// ever saying <c>gh pr</c>: BOTH entry points refuse both by name (<c>gh api*</c> is separately
/// denied by <c>implement</c>'s and <c>janitor</c>'s <c>denied_shell_command_patterns</c>, so it is
/// closed twice; <c>gh search prs</c> is in no deny list, on any role, and this rule is the only
/// thing refusing it). <c>curl</c> to the REST/GraphQL API, any other HTTP client, and a human
/// opening a browser are covered by NOTHING here — a granted shell with network access can fetch a
/// PR body directly. Nothing in Baton closes that, and the honest backstop is the per-arm
/// contamination check (<c>benchmarks/comparator.md</c> part 3), which reads the room's stream after
/// the fact and voids the arm. <c>git fetch origin pull/&lt;n&gt;/head</c> is unreachable for a
/// different reason: a per-arm single-branch clone has no such ref locally, and the fetch itself is
/// a network call this rule does not judge.
/// </para>
/// <para>
/// <b>What "this line runs <c>gh</c>" means here, and the limit that follows.</b> A segment is judged
/// only when its COMMAND HEAD is <c>gh</c> — after skipping environment assignments
/// (<c>GH_TOKEN=… gh pr view</c>) and the one keyword that introduces a command inside a loop in both
/// shells (<c>… do gh pr view %i</c>). So a line that merely MENTIONS the words is not a command and
/// is not judged: <c>echo "gh pr view 3"</c> and <c>git log --grep "gh pr create"</c> are allowed,
/// which is what makes the expansion arm below safe to scope rather than blanket. Stated once, since
/// it is the same limit either way: a head this rule cannot SEE is a head it cannot judge —
/// <c>$GH pr view 3</c>, <c>sh -c "gh pr view 1994"</c>, <c>xargs gh pr view</c> and
/// <c>timeout 30 gh pr view 1994</c> all pass. Every one of them needs a shell that is already
/// granted, and the backstop for all of them is the same contamination check named above.
/// </para>
/// <para>
/// <b>Which grants.</b> <see cref="AppliesTo"/> keys on the grant's own shell allowlist, so today it
/// governs <c>implement</c> AND <c>janitor</c> (both unscoped shells) and exempts <c>review</c>
/// (whose job is reading someone else's PR). Janitor being governed is deliberate: it runs on the
/// branch an implement lane left behind, so the selectorless spelling reaches the PR it is meant to
/// touch, and it has no business reading a sibling arm's. The cost is on the broker path only —
/// a janitor lane never runs <c>gh pr create</c>, so <see cref="OwnPullRequest"/> stays null for its
/// whole run and every governed verb is refused there, including its own. The refusal names the rule,
/// and janitor's outputs are files rather than PR comments.
/// </para>
/// </summary>
public sealed class OwnPullRequestOnlyRule
{
    /// <summary>The rule every refusal from this type names, verbatim.</summary>
    public const string Rule = "an implement lane reads its own PR only";

    /// <summary>
    /// The <c>gh pr</c> sub-commands that ENUMERATE pull requests. There is no selector to judge —
    /// the command is the sibling enumeration, whatever this room owns — so both entry points refuse
    /// them outright. <c>list</c> is the call the contaminated lane made first; <c>status</c> is the
    /// same enumeration under a different verb (every comparator arm authenticates as the same
    /// GitHub account, so "your" PRs are all of them).
    /// </summary>
    private static readonly string[] EnumeratingSubCommands = ["list", "status"];

    /// <summary>
    /// The <c>gh pr</c> sub-commands that name one pull request and carry no free text, so the whole
    /// argument list can be walked for a selector (see <see cref="SelectorsIn"/>).
    /// </summary>
    private static readonly string[] SelectorSubCommands = ["view", "diff", "checks", "checkout"];

    /// <summary>
    /// The governed sub-commands whose options carry FREE TEXT (<c>--body</c>, <c>--title</c>). A
    /// positional walk cannot discriminate there — every word of a body is a bare token — so only the
    /// shape test applies to these, and a body whose text contains a bare <c>2016</c> or <c>#1994</c>
    /// token is refused. Write <c>--body-file</c> instead, which is what these lanes already do.
    /// <para>
    /// They are governed at all because <c>gh pr comment 1994</c> WRITES on a sibling's pull request,
    /// which is worse than reading one; their being write-shaped is why each role's deny list also
    /// has an opinion, not a reason to leave them out here.
    /// </para>
    /// </summary>
    private static readonly string[] FreeTextSubCommands = ["edit", "comment"];

    /// <summary>
    /// Every governed verb. <c>create</c> is deliberately absent — opening its own PR is the lane's
    /// job, and <see cref="CodexDynamicToolPolicy"/> separately compiles its one supported spelling
    /// to a trusted executable and exact argv before offering verified evidence here.
    /// <c>gh issue view</c> is untouched: issues are the shared context a lane is dispatched against,
    /// and the measured contamination came through PRs.
    /// </summary>
    private static readonly string[] GovernedSubCommands =
        [.. EnumeratingSubCommands, .. SelectorSubCommands, .. FreeTextSubCommands];

    // The two shapes a PR selector comes in, matching what Status.DeliveryReferenceResolver pins for
    // `delivery-pr.txt` -- a bare number or a github.com pull URL. Anchored here, because a selector
    // is a whole token. A URL also carries the repository half of the comparison.
    private static readonly Regex PullRequestArgument = new(
        @"^#?(?:https://github\.com/(?<owner>[\w.-]+)/(?<repo>[\w.-]+)/pull/)?(?<number>\d+)/?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The verified repository and pull request this room opened; null before that, which refuses
    /// every governed read. In-memory and per-run: it shadows
    /// <c>Status.DeliveryReferenceOutputNames.PullRequest</c> (<c>delivery-pr.txt</c>), which is the
    /// durable record of the same fact but is written by the worker at the end of its run, far too
    /// late to gate the reads this rule gates.
    /// </summary>
    public PullRequestOwnershipEvidence? OwnPullRequest { get; private set; }

    /// <summary>
    /// Whether this rule governs a grant at all. It does <b>unless</b> the grant's own
    /// <see cref="PermissionGrant.ShellCommandPatterns"/> allowlists a <c>gh pr</c> read — which is
    /// exactly how the <c>review</c> role is declared, and reading someone else's PR is the whole of
    /// that role's job. Keyed on the allow list rather than on a role id so it needs no new field
    /// threaded through the dispatch hops, and so it fails CLOSED: an unscoped shell (implement's) is
    /// governed, and a role that genuinely needs sibling PRs opts out by saying so in its patterns.
    /// </summary>
    public static bool AppliesTo(PermissionGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        return grant.RunShellCommands && AppliesToShellPatterns(grant.ShellCommandPatterns);
    }

    /// <summary>
    /// The half of <see cref="AppliesTo"/> a hook can answer. A hook has no
    /// <see cref="PermissionGrant"/> — it receives the allow patterns as an environment channel — but
    /// it reaches this rung only on a shell tool call, which is the other half. Same predicate, one
    /// home, so widening the exemption cannot drift between the broker and the two hooks.
    /// <para>
    /// An ABSENT or unreadable pattern channel arrives here as an empty list, which reads as
    /// "unscoped shell" and is GOVERNED. That is the fail-closed direction for this rule and the
    /// opposite of how the same channel's absence reads for the pattern rung above it, where absence
    /// means "no scoped list was ever wired" and must not deny every unscoped role.
    /// </para>
    /// </summary>
    public static bool AppliesToShellPatterns(IEnumerable<string>? shellCommandPatterns) =>
        !(shellCommandPatterns ?? []).Any(
            pattern => pattern.TrimStart().StartsWith("gh pr ", StringComparison.OrdinalIgnoreCase));

    /// <summary>Refusal text for <paramref name="commandLine"/>, or null when it is allowed.</summary>
    public string? Refuse(string? commandLine) => RefusalFor(commandLine, OwnPullRequest);

    /// <summary>
    /// Accepts one already-verified ownership value. This class consumes evidence; it never derives
    /// evidence from a command line or general shell output. The direct-create broker path and the
    /// later verified-lineage path are compatible producers of the same immutable value.
    /// </summary>
    public void Observe(PullRequestOwnershipEvidence? evidence)
    {
        if (OwnPullRequest is null && evidence is not null)
        {
            OwnPullRequest = evidence;
        }
    }

    /// <summary>
    /// The number-aware detector, for an enforcement point that knows the room's own PR. Pure, so the
    /// table-driven test is the whole specification of the rule.
    /// <para>
    /// Tokenizes the command line into segments and reads the <c>gh pr &lt;sub-command&gt;</c> each
    /// segment INVOKES, rather than only the head of the line. That is what makes
    /// <c>git status &amp;&amp; gh pr view 1994</c> and <c>gh pr list | head</c> reach the rule — the
    /// measured lane chained exactly this way — and it needs no second command-line splitter beside
    /// <c>ShellCommandPatternMatcher</c>'s.
    /// </para>
    /// </summary>
    /// <param name="ownPullRequest">Verified repository-qualified evidence, or null before create.</param>
    public static string? RefusalFor(
        string? commandLine, PullRequestOwnershipEvidence? ownPullRequest)
    {
        if (ReadsPullRequestsWithoutSayingGhPr(commandLine) is { } route)
        {
            return Refusal(route, ownPullRequest);
        }

        foreach (var (subCommand, selectors, repositories, unjudgeable) in GhPrInvocations(commandLine))
        {
            if (unjudgeable)
            {
                // Ahead of everything else about this invocation: the shell expands `$(…)`, a
                // backtick, `$VAR` and cmd's `%i` AFTER this rule has read the line, and either the
                // verb or the selector may be one of those — so what the command will ask for is not
                // in the string being judged. Same fail-closed posture
                // ShellCommandPatternMatcher.EvaluateChainedCommand takes on a scoped grant.
                // Deliberately NOT resting on "a non-numeric argument is refused": that branch is the
                // one a later reader is most likely to relax. Scoped to the selector position rather
                // than scanned over the whole line, because the line the lane MUST run to deliver —
                // `gh pr create --body-file $BATON_OUTPUT_DIR/pr.md`, the spelling WorkerRoles.json's
                // output instruction teaches — carries a variable and names no pull request.
                return Refusal("this `gh pr` command line names its verb or its pull request through "
                    + "a shell expansion Baton cannot judge before the shell resolves it", ownPullRequest);
            }

            if (EnumeratingSubCommands.Contains(subCommand))
            {
                return Refusal(
                    $"`gh pr {subCommand}` enumerates pull requests this room does not own", ownPullRequest);
            }

            if (ownPullRequest is null)
            {
                // Ahead of the selectorless `continue` below, deliberately: a room that has not opened
                // a PR has nothing this verb could legitimately reach, and the bare form is refused
                // with it. On this path the accepted cost of that is a janitor lane (see the class
                // remarks); the hook path, which cannot know the number at all, decides differently.
                return Refusal("this room has not opened a pull request yet", null);
            }

            foreach (var requestedRepository in repositories)
            {
                if (GitHubRepository.TryCanonicalize(requestedRepository) != ownPullRequest.Repository)
                {
                    return Refusal(
                        $"`gh pr {subCommand}` targets repository `{requestedRepository}`, not "
                        + $"`{ownPullRequest.Repository}`", ownPullRequest);
                }
            }

            foreach (var selector in selectors)
            {
                var match = PullRequestArgument.Match(selector);
                var selectorRepository = match.Groups["owner"].Success
                    ? GitHubRepository.TryCanonicalize(
                        $"{match.Groups["owner"].Value}/{match.Groups["repo"].Value}")
                    : ownPullRequest.Repository;
                if (match.Success && int.TryParse(match.Groups["number"].Value, out var requested)
                    && requested == ownPullRequest.Number
                    && selectorRepository == ownPullRequest.Repository)
                {
                    continue;
                }

                return Refusal(
                    $"`gh pr {subCommand} {selector}` is not this room's pull request", ownPullRequest);
            }
        }

        return null;
    }

    /// <summary>
    /// The detector for an enforcement point that CANNOT know the room's PR number — both vendors'
    /// <c>PreToolUse</c> hooks. A governed verb is allowed only with no pull-request selector at all,
    /// because that is the form <c>gh</c> resolves from the current branch, which is the room's own.
    /// Every selector is refused, the room's own number included: this path has no number to compare
    /// against, and inventing a durable one is what the number-aware entry point above is for.
    /// </summary>
    public static string? RefusalForOwnBranchOnly(string? commandLine)
    {
        if (ReadsPullRequestsWithoutSayingGhPr(commandLine) is { } route)
        {
            return BranchOnlyRefusal(route);
        }

        foreach (var (subCommand, selectors, _, unjudgeable) in GhPrInvocations(commandLine))
        {
            if (unjudgeable)
            {
                // The expansion arm, stated once on RefusalFor above; both entry points refuse the
                // same shape for the same reason.
                return BranchOnlyRefusal("this `gh pr` command line names its verb or its pull "
                    + "request through a shell expansion Baton cannot judge before the shell "
                    + "resolves it");
            }

            if (EnumeratingSubCommands.Contains(subCommand))
            {
                return BranchOnlyRefusal($"`gh pr {subCommand}` enumerates pull requests this room does not own");
            }

            if (subCommand == "checkout")
            {
                // No selectorless form worth keeping: `gh pr checkout` exists to move the worktree
                // onto a NAMED pull request's branch, and the room is already standing on its own.
                return BranchOnlyRefusal("`gh pr checkout` moves this room onto another pull request's branch");
            }

            if (selectors.Count > 0)
            {
                return BranchOnlyRefusal(
                    $"`gh pr {subCommand} {selectors[0]}` names a pull request explicitly, and this gate "
                    + "runs before the command does, so it cannot tell this room's own number from a sibling's");
            }
        }

        return null;
    }

    private static string Refusal(string what, PullRequestOwnershipEvidence? ownPullRequest)
    {
        var own = ownPullRequest is { } evidence
            ? $"This room opened {evidence.Repository}#{evidence.Number}; that is the only pull request it may read."
            : "No `gh pr` read is allowed until this room's own `gh pr create` reports one.";
        return $"Baton refuses this command: {what} — {Rule}. {own} "
            + "`gh issue view` is unaffected.";
    }

    private static string BranchOnlyRefusal(string what) =>
        $"Baton refuses this command: {what} — {Rule}; run `gh pr view` with no selector and `gh` "
        + "resolves the pull request of the branch this room is on, which is its own. "
        + "`gh issue view` is unaffected.";

    /// <summary>
    /// The two ways to reach a pull request without the words <c>gh pr</c>, or null when the line
    /// takes neither. Named rather than left implicit: the class remarks list what is NOT covered,
    /// and these two are the ones that are. Called from BOTH entry points, so the broker and the two
    /// hooks refuse the same set — <c>gh api*</c> is separately denied by the governed roles' own
    /// patterns, but <c>gh search prs</c> is in no deny list on any role and this is all there is.
    /// </summary>
    private static string? ReadsPullRequestsWithoutSayingGhPr(string? commandLine)
    {
        foreach (var tokens in Segments(commandLine))
        {
            var i = CommandHeadGhIndex(tokens);
            if (i < 0 || i + 1 >= tokens.Count)
            {
                continue;
            }

            var verb = tokens[i + 1].ToLowerInvariant();
            if (verb == "api" && tokens.Skip(i + 2).Any(
                    token => token.Contains("pulls", StringComparison.OrdinalIgnoreCase)))
            {
                return "`gh api` reaches a pull request through the REST/GraphQL API";
            }

            if (verb == "search" && i + 2 < tokens.Count
                && tokens[i + 2].Equals("prs", StringComparison.OrdinalIgnoreCase))
            {
                return "`gh search prs` enumerates pull requests this room does not own";
            }
        }

        return null;
    }

    /// <summary>
    /// Every <c>gh pr &lt;sub-command&gt;</c> this line actually INVOKES — one per segment whose
    /// command head is <c>gh</c> — paired with the tokens that name a pull request (empty when it
    /// names none) and with whether the invocation is unjudgeable because the shell has yet to expand
    /// its verb or its selector. The sub-command is returned raw, including one that is a variable
    /// and one this rule does not govern, because those are the caller's two different answers.
    /// </summary>
    private static IEnumerable<(string SubCommand, IReadOnlyList<string> Selectors,
        IReadOnlyList<string> Repositories, bool Unjudgeable)>
        GhPrInvocations(string? commandLine)
    {
        foreach (var tokens in Segments(commandLine))
        {
            var i = CommandHeadGhIndex(tokens);
            if (i < 0 || i + 2 >= tokens.Count
                || !tokens[i + 1].Equals("pr", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var subCommand = tokens[i + 2].ToLowerInvariant();
            if (IsUnexpandedVariable(subCommand))
            {
                // The verb itself is unexpanded, so there is no sub-command to look up: this line
                // may be any of them, `gh pr view <a sibling>` included.
                yield return (subCommand, [], [], true);
                continue;
            }

            if (!GovernedSubCommands.Contains(subCommand))
            {
                continue;
            }

            var positional = SelectorSubCommands.Contains(subCommand);
            yield return (
                subCommand,
                SelectorsIn(tokens, i + 3, positional),
                RepositoriesIn(tokens, i + 3),
                HasUnjudgeableSelector(tokens, i + 3, positional));
        }
    }

    private static IReadOnlyList<string> RepositoriesIn(IReadOnlyList<string> tokens, int start)
    {
        var repositories = new List<string>();
        for (var i = start; i < tokens.Count; i++)
        {
            if (tokens[i].StartsWith("--repo=", StringComparison.OrdinalIgnoreCase))
            {
                repositories.Add(tokens[i]["--repo=".Length..]);
            }
            else if (tokens[i] is "--repo" or "-R")
            {
                repositories.Add(i + 1 < tokens.Count ? tokens[++i] : string.Empty);
            }
        }
        return repositories;
    }

    /// <summary>
    /// The tokens of one invocation that name a pull request. TWO tests, and neither alone is enough:
    /// <list type="number">
    /// <item>SHAPE — a bare number, <c>#n</c> or a pull URL is a selector wherever it sits, because
    /// nothing else in a governed invocation looks like one. This is what catches a selector hiding
    /// behind a valueless flag (<c>gh pr view -w 1994</c>).</item>
    /// <item>POSITION — a bare token whose predecessor is not an option is a positional argument, so
    /// it is a selector even when it is not number-shaped. This is what catches a BRANCH name
    /// (<c>gh pr view 1943-a-agy</c>), which is a selector <c>gh</c> accepts and no shape test sees.
    /// Skipped for the free-text verbs, where it would read a <c>--body</c>'s words as arguments.</item>
    /// </list>
    /// A token whose predecessor IS an option is passed over as that option's value, which is what
    /// makes <c>gh pr view --json body</c>, <c>gh pr diff --color never</c> and
    /// <c>gh pr view --repo owner/name</c> reads of the room's OWN pull request rather than refusals —
    /// the defect this replaced took the first non-flag token as the selector, so an option written
    /// before the argument made the room's own PR unreadable. It needs no table of which flags take a
    /// value: an option's value that is PR-shaped is still caught by test 1, and a real selector after
    /// an option's value is still caught by test 2.
    /// </summary>
    private static IReadOnlyList<string> SelectorsIn(
        IReadOnlyList<string> tokens, int start, bool positional)
    {
        var selectors = new List<string>();
        for (var j = start; j < tokens.Count; j++)
        {
            if (tokens[j].StartsWith('-'))
            {
                continue;
            }

            if (PullRequestArgument.IsMatch(tokens[j]))
            {
                selectors.Add(tokens[j]);
                continue;
            }

            if (positional && !tokens[j - 1].StartsWith('-'))
            {
                selectors.Add(tokens[j]);
            }
        }

        return selectors;
    }

    /// <summary>
    /// The index of the <c>gh</c> token when this segment's COMMAND is <c>gh</c>, or -1. Leading
    /// environment assignments (<c>GH_TOKEN=…</c>) and the loop keyword that introduces a command in
    /// both shells (<c>for … do gh pr view %i</c>, <c>for f in *; do gh pr view $f</c>) are stepped
    /// over; nothing else is. The class remarks state what this deliberately cannot see.
    /// </summary>
    private static int CommandHeadGhIndex(IReadOnlyList<string> tokens)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            if (IsGh(tokens[i]))
            {
                return i;
            }

            var isPrefix = (!tokens[i].StartsWith('-') && tokens[i].IndexOf('=', StringComparison.Ordinal) > 0)
                || CommandIntroducingKeywords.Contains(tokens[i].ToLowerInvariant());
            if (!isPrefix)
            {
                return -1;
            }
        }

        return -1;
    }

    private static readonly string[] CommandIntroducingKeywords = ["do", "then", "else"];

    // A token the shell has yet to expand: `$X`/`${X}`/the `$` a substitution collapses to (sh),
    // `%X%` and a `for /f` loop variable (cmd), or a leftover backtick. Read only in a SELECTOR
    // position (see HasUnjudgeableSelector), where every one of these is a character whose VALUE at
    // run time is not in the string being judged, and nothing legitimate needs one.
    private static bool IsUnexpandedVariable(string token) =>
        token.Contains('$', StringComparison.Ordinal)
        || token.Contains('%', StringComparison.Ordinal)
        || token.Contains('`', StringComparison.Ordinal);

    /// <summary>
    /// Whether this invocation carries an unexpanded variable where its pull-request SELECTOR could
    /// be — the whole of the expansion arm, and the reason it is not a whole-line scan.
    /// <para>
    /// For the positional verbs there is no free text, so every argument is a flag, a flag's value or
    /// a selector, and a variable in any of those positions could be the number:
    /// <c>gh pr view -w ${OTHER}</c> is a selector behind a valueless flag. For the free-text verbs
    /// only the LEADING argument is a selector position — <c>gh</c> takes the selector there or not
    /// at all — which is what keeps <c>gh pr comment --body-file $BATON_OUTPUT_DIR/out.md</c>, a
    /// command these lanes are instructed to run, allowed.
    /// </para>
    /// </summary>
    private static bool HasUnjudgeableSelector(IReadOnlyList<string> tokens, int start, bool positional)
    {
        for (var j = start; j < tokens.Count; j++)
        {
            if (!IsUnexpandedVariable(tokens[j]))
            {
                continue;
            }

            if (positional || (j == start && !tokens[j].StartsWith('-')))
            {
                return true;
            }
        }

        return false;
    }

    // `gh`, `gh.exe`, and a path to either -- the head token is not trusted to be bare.
    private static bool IsGh(string token)
    {
        var name = token.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        return name.Equals("gh", StringComparison.OrdinalIgnoreCase)
            || name.Equals("gh.exe", StringComparison.OrdinalIgnoreCase);
    }

    // A chain/pipe/redirection separator ends one command's arguments and starts the next word's
    // context, so the selector walk must not cross one -- `gh pr view | tee out.txt` names no PR, and
    // reading `tee` as its argument is the same class of defect as reading a flag's value as one. The
    // segmentation is this rule's own and deliberately cruder than ShellCommandPatternMatcher's: it
    // only has to bound an argument walk.
    private static readonly char[] SegmentSeparators =
        [';', '|', '&', '\n', '\r', '(', ')', '`', '<', '>'];

    private static readonly char[] TokenSeparators = [' ', '\t'];

    // A command substitution is ONE argument's worth of unexpanded text, but its own punctuation is
    // in the separator list above, so splitting first would scatter it -- `gh pr view `cat n`` would
    // lose its selector entirely, and the expansion arm would never see it. Collapsed to a bare `$`
    // BEFORE the split, which is a token IsUnexpandedVariable recognises and no other rung reads.
    private static readonly Regex CommandSubstitution = new(
        @"\$\([^)]*\)|`[^`]*`", RegexOptions.Compiled);

    private static IReadOnlyList<IReadOnlyList<string>> Segments(string? commandLine) =>
        string.IsNullOrWhiteSpace(commandLine)
            ? []
            : CommandSubstitution.Replace(commandLine, "$$")
                .Split(SegmentSeparators, StringSplitOptions.RemoveEmptyEntries)
                .Select(segment => (IReadOnlyList<string>)segment
                    .Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries)
                    .Select(token => token.Trim('"', '\''))
                    .Where(token => token.Length > 0)
                    .ToArray())
                .Where(tokens => tokens.Count > 0)
                .ToArray();
}
