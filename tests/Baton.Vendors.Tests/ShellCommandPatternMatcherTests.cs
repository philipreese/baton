using Baton.Vendors;
using Xunit;

namespace Baton.Vendors.Tests;

public class ShellCommandPatternMatcherTests
{
    [Theory]
    [InlineData("git status")]
    [InlineData("git commit -m \"msg\"")]
    [InlineData("git commit -m \"a;b\"")] // quoted metacharacter ';' is literal
    [InlineData("git commit -m 'c&&d'")] // quoted metacharacter '&&' is literal
    [InlineData("git commit -m \"cost: \\$5\"")] // escaped '$' in double quotes does not expand; still allowed
    [InlineData("git commit -m \"a $HOME b\"")] // bare $VAR in double quotes is not word-split; no injection
    [InlineData("git push --force")]
    public void Allowed_commands_matching_patterns_pass(string commandLine)
    {
        string[] patterns = ["git *"];
        Assert.True(ShellCommandPatternMatcher.IsAllowed(commandLine, patterns));
    }

    [Theory]
    [InlineData("npm install")] // non-git
    [InlineData("git status; whoami")] // chained semicolon
    [InlineData("git status && rm -rf x")] // chained logical AND
    [InlineData("git log | sh")] // pipe
    [InlineData("git log $(whoami)")] // command substitution $(...)
    [InlineData("git log `whoami`")] // backtick command substitution
    [InlineData("git log \"$(whoami)\"")] // command substitution EXECUTES inside double quotes
    [InlineData("git log \"`whoami`\"")] // backtick substitution EXECUTES inside double quotes
    [InlineData("git log \"${USER}\"")] // parameter expansion inside double quotes
    [InlineData("git log \"$((1+1))\"")] // arithmetic expansion inside double quotes ($( prefix)
    [InlineData("git show > /etc/x")] // output redirection
    [InlineData("git log < /etc/passwd")] // input redirection
    [InlineData("(git status)")] // subshell
    [InlineData("git status & disown")] // backgrounding
    [InlineData("git status\nwhoami")] // newline
    [InlineData("git status\rwhoami")] // CR
    [InlineData("git status \\")] // line continuation backslash outside quotes
    [InlineData("git log ${USER}")] // variable expansion ${...}
    [InlineData("git log $HOME")] // bare unquoted $VAR is denied (quote it for a literal)
    [InlineData("git $'\\''; rm -rf / #'")] // ANSI-C $'...' escape: bash runs `git '` then a live `; rm -rf /`
    [InlineData("git $'\\x3b' whoami")] // ANSI-C $'...' with an escaped byte -- denied outright via bare $
    public void Security_controls_deny_unquoted_metacharacters_and_non_matching_commands(string commandLine)
    {
        string[] patterns = ["git *"];
        Assert.False(ShellCommandPatternMatcher.IsAllowed(commandLine, patterns));
    }

    [Theory]
    [InlineData("'git status")] // unclosed single quote
    [InlineData("\"git status")] // unclosed double quote
    public void Unclosed_quotes_are_denied(string commandLine)
    {
        string[] patterns = ["git *"];
        Assert.False(ShellCommandPatternMatcher.IsAllowed(commandLine, patterns));
    }

    [Fact]
    public void Pattern_precision_allows_exact_and_denies_partial_or_other_subcommands()
    {
        string[] patterns = ["git status"];
        Assert.True(ShellCommandPatternMatcher.IsAllowed("git status", patterns));
        Assert.False(ShellCommandPatternMatcher.IsAllowed("git statusfoo", patterns));
        Assert.False(ShellCommandPatternMatcher.IsAllowed("git push", patterns));
    }

    [Theory]
    [InlineData("git diff", true)]
    [InlineData("git diff --stat", true)]
    [InlineData("git diff HEAD~1", true)]
    [InlineData("git difftool", false)]
    [InlineData("git difftool --extcmd=calc", false)]
    [InlineData("git diff-index", false)]
    public void Trailing_star_matches_on_word_boundaries_never_arbitrary_continuation(
        string commandLine, bool expectedAllowed)
    {
        string[] patterns = ["git diff*"];
        Assert.Equal(expectedAllowed, ShellCommandPatternMatcher.IsAllowed(commandLine, patterns));
    }

    [Theory]
    [InlineData("git merge", true)]
    [InlineData("git merge origin/main", true)]
    [InlineData("git merge-base", false)]
    [InlineData("git merge-base --is-ancestor a b", false)]
    public void Trailing_star_word_boundary_does_not_shadow_hyphenated_subcommands(
        string commandLine, bool expectedAllowed)
    {
        string[] patterns = ["git merge*"];
        Assert.Equal(expectedAllowed, ShellCommandPatternMatcher.IsAllowed(commandLine, patterns));
    }

    [Theory]
    [InlineData("git merge", true)] // equals P with its own trailing whitespace trimmed
    [InlineData("git merge origin/main", true)] // starts with P; boundary is already inside P
    [InlineData("git merge-base x", false)] // does not start with "git merge " at all
    public void Whitespace_terminated_prefix_matches_bare_and_continuation_but_not_a_hyphenated_sibling(
        string commandLine, bool expectedAllowed)
    {
        string[] patterns = ["git merge *"];
        Assert.Equal(expectedAllowed, ShellCommandPatternMatcher.IsAllowed(commandLine, patterns));
    }

    [Fact]
    public void Whitespace_terminated_flag_prefix_matches_a_continuation_with_no_second_space()
    {
        string[] patterns = ["git -c *"];
        Assert.True(ShellCommandPatternMatcher.IsAllowed("git -c core.pager=calc log", patterns));
    }

    [Theory]
    [InlineData("git grep -Ocalc foo", true)]
    [InlineData("git grep -O calc foo", true)]
    [InlineData("git grep -O", true)]
    [InlineData("git grep --open-files-in-pager=calc foo", true)]
    [InlineData("git grep --open-files-in-pager calc foo", true)]
    [InlineData("git grep --open-files-in-pager", true)]
    [InlineData("git -c alias.x=!calc x", true)]
    [InlineData("git -c core.pager=calc log", true)]
    public void Flag_driven_escape_patterns_match_attached_and_separated_arguments(string commandLine, bool expectedAllowed)
    {
        string[] patterns = ["git grep -O*", "git grep --open-files-in-pager*", "git -c *"];
        Assert.Equal(expectedAllowed, ShellCommandPatternMatcher.IsAllowed(commandLine, patterns));
    }

    [Theory]
    [InlineData("git status")]
    [InlineData("anything")]
    public void Empty_or_null_patterns_deny_everything(string commandLine)
    {
        Assert.False(ShellCommandPatternMatcher.IsAllowed(commandLine, []));
        Assert.False(ShellCommandPatternMatcher.IsAllowed(commandLine, null));
    }

    [Fact]
    public void Empty_or_whitespace_commandLine_returns_false()
    {
        string[] patterns = ["git *"];
        Assert.False(ShellCommandPatternMatcher.IsAllowed("", patterns));
        Assert.False(ShellCommandPatternMatcher.IsAllowed("   ", patterns));
        Assert.False(ShellCommandPatternMatcher.IsAllowed(null, patterns));
    }

    // --- EvaluateChainedCommand (#1459): the hook-side second layer ---------------------------------

    [Theory]
    [InlineData("git diff; echo escaped", "echo escaped")] // #1461's measured escape row 1
    [InlineData("git diff | grep baseline", "grep baseline")] // #1461's measured escape row 2
    public void Regression_the_measured_escape_rows_are_denied_naming_the_offending_segment(
        string commandLine, string expectedSegment)
    {
        // #1461 measured both of these as executing, unblocked, under `--allowedTools "Bash(git diff*)"`
        // -- claude's own pattern match is against the WHOLE command line, so the unlisted second half
        // rode the allowed prefix past it. This is the hole the hook-side segment check exists to close.
        string[] allowed = ["git diff*"];

        var result = ShellCommandPatternMatcher.EvaluateChainedCommand(commandLine, allowed, null);

        Assert.Equal(ShellCommandPatternMatcher.ScopedShellVerdict.DeniedSegment, result.Verdict);
        Assert.Equal(expectedSegment, result.Segment);
    }

    [Fact]
    public void A_single_command_matching_an_allowed_pattern_passes()
    {
        string[] allowed = ["git diff*"];

        var result = ShellCommandPatternMatcher.EvaluateChainedCommand("git diff", allowed, null);

        Assert.True(result.IsAllowed);
        Assert.Equal(ShellCommandPatternMatcher.ScopedShellVerdict.Allowed, result.Verdict);
    }

    [Theory]
    [InlineData("git diff && git status")]
    [InlineData("git diff && git status && git log")]
    [InlineData("git diff; git status")]
    [InlineData("git diff || git status")]
    public void A_chain_whose_every_segment_matches_an_allowed_pattern_passes(string commandLine)
    {
        // The capability the segment-level check adds over a blanket "any metacharacter denies": a
        // genuinely scoped chain of allowed reads is allowed, not just refused outright.
        string[] allowed = ["git diff*", "git status*", "git log*"];

        var result = ShellCommandPatternMatcher.EvaluateChainedCommand(commandLine, allowed, null);

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void A_segment_matching_nothing_allowed_denies_naming_that_segment_even_mid_chain()
    {
        string[] allowed = ["git diff*", "git status*"];

        var result = ShellCommandPatternMatcher.EvaluateChainedCommand("git diff && npm install", allowed, null);

        Assert.Equal(ShellCommandPatternMatcher.ScopedShellVerdict.DeniedSegment, result.Verdict);
        Assert.Equal("npm install", result.Segment);
    }

    [Fact]
    public void A_segment_matching_a_denied_pattern_denies_even_when_it_also_matches_an_allowed_one()
    {
        // Deny beats allow (0022, #390): "git push" would itself match a hypothetical "git *" allow,
        // but the standing deny list refuses it regardless of what widens the allow side.
        string[] allowed = ["git diff*", "git push*"];
        string[] denied = ["git push*"];

        var result = ShellCommandPatternMatcher.EvaluateChainedCommand("git diff && git push", allowed, denied);

        Assert.Equal(ShellCommandPatternMatcher.ScopedShellVerdict.DeniedSegment, result.Verdict);
        Assert.Equal("git push", result.Segment);
    }

    [Theory]
    [InlineData("git diff $(whoami)")] // command substitution
    [InlineData("git diff `whoami`")] // backtick substitution
    [InlineData("git diff > out.txt")] // output redirection
    [InlineData("git diff < in.txt")] // input redirection
    [InlineData("(git diff)")] // subshell
    [InlineData("git diff\nrm -rf /")] // embedded newline
    [InlineData("git diff \\")] // trailing backslash
    [InlineData("'git diff")] // unterminated quote
    public void Unparseable_shapes_fail_closed_rather_than_guessing_a_boundary(string commandLine)
    {
        string[] allowed = ["git diff*"];

        var result = ShellCommandPatternMatcher.EvaluateChainedCommand(commandLine, allowed, null);

        Assert.Equal(ShellCommandPatternMatcher.ScopedShellVerdict.Unparseable, result.Verdict);
        Assert.Contains("unparseable under scoped grant", result.Reason, StringComparison.Ordinal);
        Assert.Null(result.Segment);
    }

    [Theory]
    [InlineData("git merge-base --is-ancestor a b", true)]
    [InlineData("git diff --stat", true)]
    [InlineData("git status", true)]
    [InlineData("git difftool --extcmd=calc -y HEAD~1 HEAD", false)]
    [InlineData("git grep -Ocalc foo", false)]
    [InlineData("git grep --open-files-in-pager=calc foo", false)]
    [InlineData("git -c alias.x=!calc x", false)]
    [InlineData("git push --dry-run", false)]
    [InlineData("gh api repos/x", false)]
    [InlineData("gh pr view 1", true)]
    // #1683 F1: the four spellings that walked past the two `git grep -O*`/`--open-files-in-pager*`
    // deny entries -- three of them measured spawning a pager, `git grep -nOcalc` decisively. The
    // matcher's own class comment says why no deny entry could have caught them. They are denied BY
    // ABSENCE now: `git grep*` left the allow list, so nothing admits them and the two dead deny
    // entries are gone. That is why these rows read through the same allow/deny evaluation as every
    // row above rather than asserting a deny match -- the point is that nothing needs to match.
    [InlineData("git grep -nOcalc foo", false)]
    [InlineData("git grep --ignore-case -Ocalc foo", false)]
    [InlineData("git grep --open-files=calc foo", false)]
    [InlineData("git grep  -Ocalc foo", false)]
    [InlineData("git grep pattern", false)] // the plain read too: the whole family left the ceiling
    // #1683 F3: `git merge*` became `git merge *`, correct under this matcher AND under a plain
    // prefix matcher, so it no longer depends on claude's own unmeasured Bash(pattern) collision
    // behaviour. Both polarities, one condition apart.
    [InlineData("git merge origin/main", false)]
    public void Review_role_command_allow_deny_polarities_evaluated_directly(string command, bool expectedAllowed)
    {
        var review = WorkerRoleCatalog.For("review");
        var allowed = review.Grant.ShellCommandPatterns;
        var denied = review.Grant.DeniedShellCommandPatterns;

        var result = ShellCommandPatternMatcher.EvaluateChainedCommand(command, allowed, denied);

        Assert.Equal(expectedAllowed, result.IsAllowed);
    }

    // --- #1731: implement/janitor are the first unscoped roles (empty allow list) to carry
    // denied_shell_command_patterns, which routes every command through this segmenter for the first
    // time. The operator ruling and its reasoning live at spec/baton.md §9, not restated here; these
    // rows pin the resulting behaviour. A SCOPED grant (review) is unaffected and stays fail-closed.
    [Theory]
    [InlineData("dotnet test > out.txt")] // bare output redirection
    [InlineData("dotnet test 2>&1")] // fd redirection
    [InlineData("echo $PATH")] // bare variable reference
    [InlineData("echo ${PATH}")] // braced parameter expansion, no nested substitution
    [InlineData("dotnet test < input.txt")] // bare input redirection
    [InlineData(@"git add C:\Users\worker\repo\file.txt")] // Windows absolute path, pre-existing (#659)
    [InlineData(@"cd C:\Users\pbree\source\repos\w1 && dotnet build")] // backslash + chain operator
    public void An_unscoped_deny_only_grant_permits_ordinary_redirection_variable_references_and_paths(
        string command)
    {
        var implement = WorkerRoleCatalog.For("implement");

        var result = ShellCommandPatternMatcher.EvaluateChainedCommand(
            command, implement.Grant.ShellCommandPatterns, implement.Grant.DeniedShellCommandPatterns);

        Assert.True(result.IsAllowed, result.Reason);
    }

    [Fact]
    public void An_unscoped_deny_only_grant_still_permits_an_ordinary_chain_with_no_metacharacters()
    {
        var implement = WorkerRoleCatalog.For("implement");

        var result = ShellCommandPatternMatcher.EvaluateChainedCommand(
            "true && gh pr create --title x", implement.Grant.ShellCommandPatterns,
            implement.Grant.DeniedShellCommandPatterns);

        Assert.True(result.IsAllowed, result.Reason);
    }

    [Fact]
    public void Leading_redirection_ahead_of_a_denied_command_is_the_accepted_bypass_on_an_unscoped_grant()
    {
        // The ruling's named cost, pinned rather than hidden (full reasoning at spec/baton.md §9, not
        // restated here): a leading redirection moves `gh` out of head position and evades
        // `IsDeniedByTokenizedHead`. A SCOPED grant would still deny this outright as Unparseable (the
        // metacharacter is still fatal there); this test is specifically about the unscoped case.
        var implement = WorkerRoleCatalog.For("implement");

        var result = ShellCommandPatternMatcher.EvaluateChainedCommand(
            ">out.txt gh label create x", implement.Grant.ShellCommandPatterns,
            implement.Grant.DeniedShellCommandPatterns);

        Assert.True(result.IsAllowed, result.Reason);
    }

    [Theory]
    [InlineData("gh $'\\''; gh label create x #'")] // the documented ANSI-C-quote chain-hiding escape
    [InlineData("$(gh label create x)")] // command substitution
    [InlineData("`gh label create x`")] // backtick substitution
    [InlineData("(gh label create x)")] // subshell grouping
    [InlineData("echo $(gh label create x)")] // substitution nested in an otherwise-ordinary command
    public void An_unscoped_deny_only_grant_never_returns_unparseable_even_around_hiding_constructs(
        string command)
    {
        // #1731's ruling (spec/baton.md §9): a segment boundary this scanner will not guess for is no
        // longer refused outright on this scope. Backtick/subshell/ANSI-C-quote forms still defeat the
        // segmenter's own boundary tracking, so these rows fold to a single whole-line check and may
        // come out Allowed -- an accepted cost, same shape as the redirection bypass above -- but the
        // verdict must never be Unparseable here.
        var implement = WorkerRoleCatalog.For("implement");

        var result = ShellCommandPatternMatcher.EvaluateChainedCommand(
            command, implement.Grant.ShellCommandPatterns, implement.Grant.DeniedShellCommandPatterns);

        Assert.NotEqual(ShellCommandPatternMatcher.ScopedShellVerdict.Unparseable, result.Verdict);
    }

    [Theory]
    // #1748 F6: the two rows take different verdicts -- asserting only IsAllowed==false cannot
    // discriminate a future leak of the permissive (unscoped) path into this scoped one, since both
    // Unparseable and DeniedSegment satisfy that weaker assertion.
    [InlineData("git $'\\''; rm -rf / #'", ShellCommandPatternMatcher.ScopedShellVerdict.Unparseable)] // the documented ANSI-C-quote chain-hiding escape
    [InlineData("git diff; echo escaped", ShellCommandPatternMatcher.ScopedShellVerdict.DeniedSegment)] // an ordinary chain past the allowed prefix
    public void A_scoped_grant_still_fails_closed_exactly_as_before(
        string command, ShellCommandPatternMatcher.ScopedShellVerdict expectedVerdict)
    {
        // The control for the ruling: a SCOPED grant (a non-empty allow list, like review's) never
        // takes the permissive path above -- it is unaffected by this PR and its fail-closed behaviour
        // is unchanged.
        string[] allowed = ["git diff*"];

        var result = ShellCommandPatternMatcher.EvaluateChainedCommand(command, allowed, null);

        Assert.False(result.IsAllowed);
        Assert.Equal(expectedVerdict, result.Verdict);
    }

    [Fact]
    public void An_embedded_newline_ahead_of_a_denied_command_is_denied_not_folded_past()
    {
        // #1748 F1: the #1731 incident command with one ordinary line in front of it. The reasoning
        // and the segment-boundary mechanism that now catches it are recorded once at spec/baton.md §9.
        var implement = WorkerRoleCatalog.For("implement");

        var result = ShellCommandPatternMatcher.EvaluateChainedCommand(
            "git status\ngh label create operator-merge", implement.Grant.ShellCommandPatterns,
            implement.Grant.DeniedShellCommandPatterns);

        Assert.False(result.IsAllowed);
        Assert.Equal(ShellCommandPatternMatcher.ScopedShellVerdict.DeniedSegment, result.Verdict);
    }

    [Theory]
    [InlineData("`gh label create x`")] // backtick substitution wraps the head token
    [InlineData("$(gh label create x)")] // command substitution wraps the head token
    [InlineData("(gh label create x)")] // subshell grouping wraps the head token
    [InlineData("echo $(date) && gh label create x")] // denied command in a genuine segment, missed only because an unrelated '(' folded the whole line
    public void A_denied_command_is_caught_on_the_whole_line_fold_regardless_of_wrapper_or_offset(
        string command)
    {
        // #1748 F2: the fold (TrySegmentChainedCommand could not find a trustworthy boundary) used to
        // check only the folded segment's head token, so a denied command survived by riding inside a
        // hiding construct or sitting after an unrelated one elsewhere on the line. The fold path now
        // scans every token offset and strips leading wrapper/quote characters off each token.
        var implement = WorkerRoleCatalog.For("implement");

        var result = ShellCommandPatternMatcher.EvaluateChainedCommand(
            command, implement.Grant.ShellCommandPatterns, implement.Grant.DeniedShellCommandPatterns);

        Assert.False(result.IsAllowed);
        Assert.Equal(ShellCommandPatternMatcher.ScopedShellVerdict.DeniedSegment, result.Verdict);
    }

    [Theory]
    [InlineData("gh\\ label create x")] // escaped space: the head token is `gh\`, never `gh`
    [InlineData("gh $'\\''; gh label create x #'")] // a balanced quote span hides the `;`, so segmentation succeeds with `gh` off the head
    public void The_remaining_named_bypasses_on_an_unscoped_grant_allow_and_are_pinned_as_accepted(string command)
    {
        // #1748 re-review: two more members of the accepted family spec/baton.md §9 names. Each is
        // pinned so that a future change closing one shows up as this test going red, rather than the
        // register silently going stale.
        var implement = WorkerRoleCatalog.For("implement");

        var result = ShellCommandPatternMatcher.EvaluateChainedCommand(
            command, implement.Grant.ShellCommandPatterns, implement.Grant.DeniedShellCommandPatterns);

        Assert.True(result.IsAllowed, result.Reason);
    }

    [Fact]
    public void Word_splitting_via_IFS_ahead_of_a_denied_command_is_the_other_accepted_bypass_on_an_unscoped_grant()
    {
        // #1748 F8: the second bypass spec/baton.md §9 names as accepted had no dedicated test -- only
        // the leading-redirection bypass above did, despite both the PR body and the lane report
        // claiming both were pinned. See spec/baton.md §9 for why `${IFS}` still evades this scanner.
        var implement = WorkerRoleCatalog.For("implement");

        var result = ShellCommandPatternMatcher.EvaluateChainedCommand(
            "gh${IFS}label create x", implement.Grant.ShellCommandPatterns,
            implement.Grant.DeniedShellCommandPatterns);

        Assert.True(result.IsAllowed, result.Reason);
    }

    [Theory]
    [InlineData("git merge origin/main", true)]
    [InlineData("git merge --no-ff x", true)]
    [InlineData("git merge-base --is-ancestor a b", false)]
    [InlineData("git merge-tree a b", false)]
    public void The_review_deny_for_merge_is_spelled_so_a_plain_prefix_matcher_agrees_too(
        string command, bool expectedDenied)
    {
        // #1683 F3. #1679 already fixed this at the hook layer (word-boundary matching), but the same
        // deny list ALSO reaches claude as `--disallowedTools "Bash(git merge*)"`, and whether that
        // flag's own matcher collides it onto `git merge-base` was never measured. If it does,
        // #1679's second defect was still open in production despite green tests -- a claim about a
        // vendor is not verified by a unit test.
        // Spelling the entry `git merge *` makes it correct under BOTH readings with no live run.
        // This arm is the pessimistic one: a plain, unconditional prefix test, no word boundary.
        var deniedEntry = WorkerRoleCatalog.For("review").Grant.DeniedShellCommandPatterns!
            .Single(p => p.StartsWith("git merge", StringComparison.Ordinal));
        var plainPrefix = deniedEntry.TrimEnd('*');

        Assert.Equal(expectedDenied, command.StartsWith(plainPrefix, StringComparison.Ordinal));
        // ... and this matcher agrees on the same rows, so the two enforcement layers cannot disagree
        // on any of them whichever way claude's own matching turns out to work.
        Assert.Equal(
            expectedDenied,
            ShellCommandPatternMatcher.IsDenied(
                command, WorkerRoleCatalog.For("review").Grant.DeniedShellCommandPatterns));
    }

    [Fact]
    public void The_merge_deny_still_covers_the_bare_subcommand_on_this_matcher()
    {
        // The one row where the two layers legitimately differ, stated rather than hidden: this
        // matcher's whitespace branch accepts a line EQUAL to the trimmed prefix, so bare `git merge`
        // is denied here; a plain prefix test on "git merge " would not reach it. Harmless -- bare
        // `git merge` matches no allow pattern either, so it is denied by absence on both layers --
        // but it is why the theory above does not carry that row.
        Assert.True(ShellCommandPatternMatcher.IsDenied(
            "git merge", WorkerRoleCatalog.For("review").Grant.DeniedShellCommandPatterns));
    }

    [Theory]
    [InlineData("git log=x")]
    [InlineData("git status=x")]
    public void An_equals_continuation_does_not_match_a_non_flag_pattern(string commandLine)
    {
        // #1683 F6, the widening the matcher's own class comment describes: before this, no allow
        // pattern needed to be flag-shaped to admit an '='-suffixed continuation.
        string[] patterns = ["git log*", "git status*"];
        Assert.False(ShellCommandPatternMatcher.IsAllowed(commandLine, patterns));
    }

    [Fact]
    public void An_equals_continuation_still_matches_a_flag_shaped_pattern()
    {
        // F6's polarity control: gating the '=' accept on flag shape must not break the branch it
        // belongs to. Without this arm, deleting the accept outright would also pass the test above.
        string[] patterns = ["git grep --open-files-in-pager*"];
        Assert.True(ShellCommandPatternMatcher.IsAllowed("git grep --open-files-in-pager=calc foo", patterns));
    }

    // --- IsDeniedByOptionToken (#1683 F2): the position-independent deny ----------------------------

    [Theory]
    [InlineData("git log -1 --output=C:/x --format=format:y", true)] // the measured write escape
    [InlineData("git log --format=format:y --output=C:/x", true)] // reordered
    [InlineData("git show --output C:/x", true)] // separated form
    [InlineData("git diff  --output=C:/x", true)] // doubled space
    [InlineData("git log \"--output=C:/x\"", true)] // quoted: git still sees --output=
    // Quote removal happens AFTER word splitting, so a quote can sit anywhere inside the option name
    // and the shell still hands git one `--output=C:/x` word. Stripping only the leading quote left
    // both of these matching nothing -- the same walked-past-by-a-spelling defect F1/F2 document,
    // inside the fix for it. Found by the second reader on this PR.
    [InlineData("git log -1 --outpu\"t\"=C:/x --format=format:y", true)]
    [InlineData("git log -1 -\"-\"output=C:/x --format=format:y", true)]
    [InlineData("git log --grep=\"--output\"", false)] // the control: not a token START, still allowed
    [InlineData("git log --oneline -5", false)] // the near-miss that must stay allowed
    [InlineData("git log -1 --format=format:y", false)]
    [InlineData("git status", false)]
    public void Denied_option_tokens_match_anywhere_on_the_line_and_spare_their_near_misses(
        string commandLine, bool expectedDenied)
    {
        string[] tokens = ["--output"];
        Assert.Equal(expectedDenied, ShellCommandPatternMatcher.IsDeniedByOptionToken(commandLine, tokens));
    }

    [Theory]
    [InlineData("git log --output=C:/x")]
    [InlineData("git log --oneline")]
    public void An_empty_or_null_option_token_list_denies_nothing(string commandLine)
    {
        // The control that makes the theory above a measurement of the TOKENS rather than of the
        // command lines: with no tokens configured, every one of them passes.
        Assert.False(ShellCommandPatternMatcher.IsDeniedByOptionToken(commandLine, []));
        Assert.False(ShellCommandPatternMatcher.IsDeniedByOptionToken(commandLine, null));
    }

    [Theory]
    [InlineData("git log -1 --output=C:/x --format=format:y", true)]
    [InlineData("git log -1 --ou=C:/x", false)]
    [InlineData("git show --output C:/x", true)]
    [InlineData("git log --oneline -5", false)]
    public void Review_role_denied_option_tokens_from_catalog(string command, bool expectedDenied)
    {
        // Reads the real catalog, not synthetic tokens: what ships is what is pinned.
        //
        // `--ou=` is false on purpose and it is the honest row. #1683's brief assumed git accepts
        // unambiguous long-option abbreviation for --output and asked for `--ou` in the deny list. It
        // does not: --output on log/show/diff is hand-parsed in the diff code, not parse-options, and
        // `git log -1 --ou=<f>`, `--outp=`, `--out=`, `--o` all return `fatal: unrecognized argument`
        // (git 2.54.0.windows.1, run in this worktree; full transcript in the PR body). Only the exact
        // spelling parses, so only the exact spelling is denied -- adding `--ou` would be a deny entry
        // for a command git refuses to run, which is exactly the dead belt-and-braces F1 objects to.
        // `git grep --open-files=` DOES abbreviate, because grep goes through parse-options; that is
        // why F1's escapes are real and this one is not. Same repo, two argument parsers.
        var review = WorkerRoleCatalog.For("review");

        Assert.Equal(
            expectedDenied,
            ShellCommandPatternMatcher.IsDeniedByOptionToken(command, review.Grant.DeniedShellOptionTokens));
    }

    // #2114 / #2128 review H3: before the wrapper rung, a wrapped write was judged on its first token
    // alone (`pwsh`), since a well-formed wrapper never trips the fold. Synthetic deny list, so the
    // theory is about the matcher and not about whichever spelling WorkerRoles.json carries today.
    private static readonly string[] WrapperDeny = ["baton *", "curl*"];

    [Theory]
    [InlineData("pwsh -c \"baton cancel room-1\"")]
    [InlineData("pwsh -NoProfile -Command 'baton cancel room-1'")]
    [InlineData("powershell -Command baton cancel room-1")]
    [InlineData("PowerShell.exe -c \"baton cancel room-1\"")]
    [InlineData("cmd /c baton cancel room-1")]
    [InlineData("cmd.exe /C \"baton cancel room-1\"")]
    [InlineData("bash -c \"baton cancel room-1\"")]
    [InlineData("bash -lc 'curl -X POST http://127.0.0.1:7777/cancel'")]
    [InlineData("sh -c 'baton cancel room-1'")]
    [InlineData("/bin/sh -c 'baton cancel room-1'")]
    [InlineData("zsh -c 'baton cancel room-1'")]
    [InlineData("pwsh -c \"cmd /c baton cancel room-1\"")] // nested: the every-offset scan needs no recursion
    [InlineData("pwsh -c \"echo hi; baton cancel room-1\"")] // a chain inside the body
    [InlineData("pwsh -c \"& baton cancel room-1\"")] // PowerShell's call operator in front of the head
    public void A_shell_wrapper_has_its_body_rematched_against_the_deny_list(string command)
    {
        var result = ShellCommandPatternMatcher.EvaluateChainedCommand(command, null, WrapperDeny);

        Assert.False(result.IsAllowed);
        Assert.Equal(ShellCommandPatternMatcher.ScopedShellVerdict.DeniedSegment, result.Verdict);
    }

    [Theory]
    [InlineData("pwsh -EncodedCommand YmF0b24gY2FuY2Vs")]
    [InlineData("pwsh -e YmF0b24gY2FuY2Vs")]
    [InlineData("pwsh -ec YmF0b24gY2FuY2Vs")]
    [InlineData("powershell -enc YmF0b24gY2FuY2Vs")]
    [InlineData("powershell -EncodedComm YmF0b24gY2FuY2Vs")] // an unambiguous prefix, which pwsh accepts
    [InlineData("pwsh")] // nothing to read at all
    [InlineData("cmd")]
    [InlineData("bash")]
    public void A_wrapper_whose_body_the_matcher_cannot_read_is_denied_outright(string command)
    {
        var result = ShellCommandPatternMatcher.EvaluateChainedCommand(command, null, WrapperDeny);

        Assert.False(result.IsAllowed);
    }

    [Theory]
    [InlineData("pwsh -File build.ps1")] // a script file: the accepted compiled-code exposure, not a spelling
    [InlineData("pwsh -ExecutionPolicy Bypass -File build.ps1")] // `-ex` is not `-e`
    [InlineData("pwsh -c \"git status\"")]
    [InlineData("bash tools/check.sh")]
    [InlineData("sh ./configure")]
    [InlineData("cmd /c dir")]
    public void A_wrapper_with_a_readable_clean_body_stays_admitted(string command)
    {
        var result = ShellCommandPatternMatcher.EvaluateChainedCommand(command, null, WrapperDeny);

        Assert.True(result.IsAllowed, result.Reason);
    }

    // #2114 / #2128 review M4: the mixed-case spellings, pinned. Why the deny side ignores case and
    // the exception side does not is IsDeniedByTokenizedHead's own paragraph, not restated here.
    [Theory]
    [InlineData("IWR -Method Post http://127.0.0.1:7777/cancel")]
    [InlineData("Invoke-Webrequest -Method Post http://127.0.0.1:7777/cancel")]
    [InlineData("CURL -X POST http://127.0.0.1:7777/cancel")]
    [InlineData("Baton cancel room-1")]
    public void Deny_heads_compare_case_insensitively_on_an_unscoped_grant(string command)
    {
        string[] deny = ["iwr*", "Invoke-WebRequest*", "curl*", "baton *"];

        var result = ShellCommandPatternMatcher.EvaluateChainedCommand(command, null, deny);

        Assert.False(result.IsAllowed);
    }

    // #2114: the read allowlist carved out of a deny. Longest tokenized match decides, deny winning a
    // tie; exception heads are ORDINAL because the CLI they name dispatches ordinally (`baton Status`
    // is Program.cs's default arm, i.e. `supply`).
    private static readonly string[] ExceptionDeny =
        ["baton *", "baton ledger --rebuild*", "baton ledger export*"];

    private static readonly string[] ExceptionAllow =
        ["baton status*", "baton ledger*", "baton trust --list*", "baton --version*"];

    [Theory]
    [InlineData("baton status room-1")]
    [InlineData("baton ledger")]
    [InlineData("baton ledger --since 2026-09-01")]
    [InlineData("baton trust --list")]
    [InlineData("baton --version")]
    [InlineData("pwsh -c \"baton status room-1\"")] // the exception holds inside a wrapper body too
    [InlineData("git diff && baton status room-1")]
    public void An_exception_admits_the_read_it_names_beneath_a_head_deny(string command)
    {
        var result = ShellCommandPatternMatcher.EvaluateChainedCommand(command, null, ExceptionDeny, ExceptionAllow);

        Assert.True(result.IsAllowed, result.Reason);
    }

    [Theory]
    [InlineData("baton cancel room-1")] // no exception reaches it
    [InlineData("baton ledger --rebuild")] // a longer re-deny beneath the `baton ledger*` exception
    [InlineData("baton ledger export C:/repo")]
    [InlineData("baton trust C:/repo")] // `baton trust --list*` is three tokens; this matches two of them
    [InlineData("baton Status room-1")] // ordinal on the exception side: this is the default arm, a write
    [InlineData("baton status room-1; baton cancel room-1")]
    [InlineData("pwsh -c \"baton status room-1; baton cancel room-1\"")]
    public void An_exception_does_not_reach_past_its_own_tokens(string command)
    {
        var result = ShellCommandPatternMatcher.EvaluateChainedCommand(command, null, ExceptionDeny, ExceptionAllow);

        Assert.False(result.IsAllowed);
    }

    [Fact]
    public void A_deny_and_an_exception_of_equal_length_resolve_to_the_deny()
    {
        var result = ShellCommandPatternMatcher.EvaluateChainedCommand(
            "baton status room-1", null, ["baton status*"], ["baton status*"]);

        Assert.False(result.IsAllowed);
    }

    [Fact]
    public void With_no_exceptions_every_deny_stands_exactly_as_before()
    {
        var denied = ShellCommandPatternMatcher.EvaluateChainedCommand("baton status room-1", null, ["baton *"]);
        var deniedEmpty = ShellCommandPatternMatcher.EvaluateChainedCommand("baton status room-1", null, ["baton *"], []);

        Assert.False(denied.IsAllowed);
        Assert.False(deniedEmpty.IsAllowed);
    }
}
