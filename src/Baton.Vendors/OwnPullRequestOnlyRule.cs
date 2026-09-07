using System.Text.RegularExpressions;

namespace Baton.Vendors;

/// <summary>
/// #2001 part 2. An implement lane may read the pull request <em>it</em> opened, and no other one.
/// <para>
/// The measured failure: comparator arms for one issue shared a clone and a repository, so an arm-A
/// lane ran <c>gh pr list</c> and then <c>gh pr view</c> on a sibling arm's open PR and committed a
/// diff two lines from it. Per-arm clones (part 1, the conductor recipe recorded in
/// <c>benchmarks/comparator.md</c>) hide sibling <em>branches</em>; they cannot hide sibling
/// <em>PRs</em>, because <c>gh</c> talks to GitHub rather than to the clone. This rule is the half
/// that has to live in Baton.
/// </para>
/// <para>
/// Vendor-neutral on purpose — nothing here knows about codex. Today its only caller is the codex
/// broker's run-command path (<see cref="CodexDynamicToolPolicy"/>), which is the one enforcement
/// point that can also <em>learn</em> the room's PR number, because it sees a command's output.
/// Claude's and agy's <c>PreToolUse</c> hooks decide before a command runs and never see
/// <c>gh pr create</c>'s stdout, so they cannot maintain <see cref="OwnPullRequest"/> on their own;
/// wiring them needs a durable source for that number and is not done here.
/// </para>
/// </summary>
public sealed class OwnPullRequestOnlyRule
{
    /// <summary>The rule every refusal from this type names, verbatim.</summary>
    public const string Rule = "an implement lane reads its own PR only";

    /// <summary>
    /// The <c>gh pr</c> sub-commands that read a pull request the room may not own. <c>create</c>,
    /// <c>comment</c>, <c>edit</c> and the rest are absent deliberately: this rule is about READING a
    /// sibling, and the write-shaped ones are already governed by each role's own deny list
    /// (<c>WorkerRoles.json</c>). <c>gh issue view</c> is untouched — issues are the shared context a
    /// lane is dispatched against, and the measured contamination came through PRs.
    /// </summary>
    private static readonly string[] GovernedSubCommands = ["view", "diff", "checkout", "list"];

    // The two shapes a PR argument comes in, matching what Status.DeliveryReferenceResolver pins for
    // `delivery-pr.txt` -- a bare number or a github.com pull URL. Anchored here, because an argument
    // is a whole token; the scanning twin below is what reads `gh pr create`'s stdout.
    private static readonly Regex PullRequestArgument = new(
        @"^#?(?:https://github\.com/[\w.-]+/[\w.-]+/pull/)?(\d+)/?$", RegexOptions.Compiled);

    private static readonly Regex CreatedPullRequestUrl = new(
        @"https://github\.com/[\w.-]+/[\w.-]+/pull/(\d+)", RegexOptions.Compiled);

    /// <summary>
    /// The pull request this room opened, once a <c>gh pr create</c> it ran has reported one; null
    /// before that, which refuses every governed read. In-memory and per-run: it shadows
    /// <c>Status.DeliveryReferenceOutputNames.PullRequest</c> (<c>delivery-pr.txt</c>), which is the
    /// durable record of the same fact but is written by the worker at the end of its run, far too
    /// late to gate the reads this rule gates.
    /// </summary>
    public int? OwnPullRequest { get; private set; }

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
        if (!grant.RunShellCommands)
        {
            return false;
        }

        return !(grant.ShellCommandPatterns ?? []).Any(
            pattern => pattern.TrimStart().StartsWith("gh pr ", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Refusal text for <paramref name="commandLine"/>, or null when it is allowed.</summary>
    public string? Refuse(string? commandLine) => RefusalFor(commandLine, OwnPullRequest);

    /// <summary>
    /// Records the room's own PR number from a <c>gh pr create</c> that succeeded. Call this only
    /// with the output of a command that exited zero: <c>gh</c> prints the new PR's URL on stdout,
    /// and that URL is the only thing here that can open the rule's gate.
    /// <para>
    /// Known and accepted: a <c>gh pr create</c> that fails with "a pull request for branch X already
    /// exists" names the room's real PR and exits NON-zero, so the room never learns its number and
    /// stays locked out of reading its own PR for the rest of the run. The failure direction is
    /// refusal, which is the safe one. Widening the caller to parse a failed command's output is how
    /// a sibling's URL quoted in an error message would become this room's "own" PR.
    /// </para>
    /// </summary>
    public void ObserveCommandOutput(string? commandLine, string? output)
    {
        if (output is null || !MentionsGhPr(commandLine, "create"))
        {
            return;
        }

        // The LAST url in the output: `gh pr create` can print progress lines mentioning an earlier
        // PR, and the one it created is the one it prints last.
        var matches = CreatedPullRequestUrl.Matches(output);
        if (matches.Count > 0 && int.TryParse(matches[^1].Groups[1].Value, out var number))
        {
            OwnPullRequest = number;
        }
    }

    /// <summary>
    /// The detector. Pure, so the table-driven test is the whole specification of the rule.
    /// <para>
    /// Tokenizes the WHOLE command line on whitespace and looks for <c>gh</c> <c>pr</c>
    /// <c>&lt;sub-command&gt;</c> at any offset, rather than only at the head of the line. That is
    /// what makes <c>git status &amp;&amp; gh pr view 1994</c> and <c>gh pr list | head</c> reach the
    /// rule — the measured lane chained exactly this way — and it needs no second command-line
    /// splitter beside <c>ShellCommandPatternMatcher</c>'s. It over-matches a mention of the words
    /// inside a quoted string (a commit message saying "gh pr view 1994" is refused); that is the
    /// fail-closed direction, and the refusal names the rule so the worker can rephrase.
    /// </para>
    /// </summary>
    /// <param name="ownPullRequest">The room's own PR number, or null before it has opened one.</param>
    public static string? RefusalFor(string? commandLine, int? ownPullRequest)
    {
        // FIRST, and independent of everything below: this rule reads a command line, and the shell
        // that runs it expands `$(...)`, a backtick, `$VAR` and cmd's `%i` AFTERWARDS. Any of those
        // can supply the sub-command or the number that the scan below would have judged, so a line
        // carrying one is refused outright rather than judged on what it says now. Same fail-closed
        // posture ShellCommandPatternMatcher.EvaluateChainedCommand takes on a scoped grant, and
        // narrowed to lines that already say `gh pr` so ordinary `$`-carrying build commands are
        // untouched. Deliberately NOT resting on "a non-numeric argument is refused": that branch is
        // the one a later reader is most likely to relax.
        if (MentionsGhPr(commandLine) && ContainsShellExpansion(commandLine!))
        {
            return Refusal("this `gh pr` command line contains a shell expansion Baton cannot judge "
                + "before the shell resolves it", ownPullRequest);
        }

        foreach (var (subCommand, argument) in GovernedInvocations(commandLine))
        {
            if (subCommand == "list")
            {
                // No number to compare: `gh pr list` IS the sibling enumeration, whatever this room
                // owns. This is the call the contaminated lane made first.
                return Refusal($"`gh pr list` enumerates pull requests this room does not own", ownPullRequest);
            }

            if (ownPullRequest is null)
            {
                return Refusal($"this room has not opened a pull request yet", null);
            }

            if (argument is null)
            {
                // The bare form reads the PR of the branch the room is standing on, which is its own.
                continue;
            }

            var match = PullRequestArgument.Match(argument);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var requested)
                && requested == ownPullRequest)
            {
                continue;
            }

            return Refusal($"`gh pr {subCommand} {argument}` is not this room's pull request", ownPullRequest);
        }

        return null;
    }

    private static string Refusal(string what, int? ownPullRequest)
    {
        var own = ownPullRequest is { } number
            ? $"This room opened #{number}; that is the only pull request it may read."
            : "No `gh pr` read is allowed until this room's own `gh pr create` reports one.";
        return $"Baton refuses this command: {what} — {Rule}. {own} "
            + "`gh issue view` is unaffected.";
    }

    /// <summary>
    /// Every <c>gh pr &lt;governed&gt;</c> occurrence in <paramref name="commandLine"/>, paired with
    /// its first non-flag argument (null when it has none). A flag's own value is skipped past rather
    /// than read as the PR, so <c>gh pr view -w 2005</c> finds 2005.
    /// </summary>
    private static IEnumerable<(string SubCommand, string? Argument)> GovernedInvocations(string? commandLine)
    {
        var tokens = Tokenize(commandLine);
        for (var i = 0; i + 2 < tokens.Count; i++)
        {
            if (!IsGh(tokens[i]) || !tokens[i + 1].Equals("pr", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var subCommand = tokens[i + 2].ToLowerInvariant();
            if (!GovernedSubCommands.Contains(subCommand))
            {
                continue;
            }

            string? argument = null;
            for (var j = i + 3; j < tokens.Count; j++)
            {
                if (tokens[j].StartsWith('-'))
                {
                    continue;
                }
                argument = tokens[j];
                break;
            }

            yield return (subCommand, argument);
        }
    }

    /// <summary>
    /// Whether <c>gh</c> <c>pr</c> appears adjacent anywhere in the line, optionally followed by
    /// <paramref name="subCommand"/>. Null asks the wider question — <em>this line drives
    /// <c>gh pr</c> at all</em> — which is what the expansion guard needs, since an expansion can
    /// supply the sub-command itself.
    /// </summary>
    private static bool MentionsGhPr(string? commandLine, string? subCommand = null)
    {
        var tokens = Tokenize(commandLine);
        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            if (!IsGh(tokens[i]) || !tokens[i + 1].Equals("pr", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (subCommand is null)
            {
                return true;
            }
            if (i + 2 < tokens.Count && tokens[i + 2].Equals(subCommand, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    // `$(`/`${`/a bare `$` (sh), a backtick (both), and `%` (cmd's %VAR% and a `for /f` loop
    // variable). Over-broad on purpose: every one of them is a character whose VALUE at run time is
    // not in the string being judged.
    private static bool ContainsShellExpansion(string commandLine) =>
        commandLine.Contains('`', StringComparison.Ordinal)
        || commandLine.Contains('$', StringComparison.Ordinal)
        || commandLine.Contains('%', StringComparison.Ordinal);

    // `gh`, `gh.exe`, and a path to either -- the head token is not trusted to be bare.
    private static bool IsGh(string token)
    {
        var name = token.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        return name.Equals("gh", StringComparison.OrdinalIgnoreCase)
            || name.Equals("gh.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> Tokenize(string? commandLine) =>
        string.IsNullOrWhiteSpace(commandLine)
            ? []
            : commandLine
                .Split([' ', '\t', '\n', '\r', ';', '|', '&', '(', ')', '`'], StringSplitOptions.RemoveEmptyEntries)
                .Select(token => token.Trim('"', '\''))
                .Where(token => token.Length > 0)
                .ToArray();
}
