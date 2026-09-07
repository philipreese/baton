using Baton.Vendors;
using Xunit;

namespace Baton.Vendors.Tests;

/// <summary>
/// #2001 part 2. The rule under test is "an implement lane reads its own PR only"; this table is its
/// whole specification.
/// </summary>
public class OwnPullRequestOnlyRuleTests
{
    // THE CONTROL ARM, read first: a detector that simply refused every `gh` command would pass every
    // refusal row below. These are the reads an implement lane keeps -- issues are the shared context
    // it is dispatched against, and opening its own PR is its job.
    [Theory]
    [InlineData("gh issue view 1994", null)]
    [InlineData("gh issue view 1994", 2005)]
    [InlineData("gh issue view 1994 --comments", 2005)]
    [InlineData("gh pr create --fill", null)]
    [InlineData("git status && gh issue view 1994", 2005)]
    [InlineData("git branch -a", null)]
    // The expansion arm is scoped to the SELECTOR position of a governed verb, so ordinary build
    // commands carrying a `$` or a `%` are untouched. Without these rows that arm could refuse
    // everything.
    [InlineData("pixi run test > $TMP/out.txt", 2005)]
    [InlineData("dotnet build -p:Version=$(cat version.txt)", 2005)]
    [InlineData("gh issue view $ISSUE", 2005)]
    // THE DELIVERY LINE, in the spelling WorkerRoles.json's output instruction teaches
    // (`$BATON_OUTPUT_DIR/...`) and in cmd's spelling of the same variable. `create` is ungoverned by
    // design (GovernedVerbs' remark says why) -- and a variable in a `--body-file` path is not
    // a pull-request selector, so the expansion arm has no business with either. A round-2 whole-line
    // scan refused all four of these, which refused the lane its own delivery.
    [InlineData("gh pr create --title x --body-file $BATON_OUTPUT_DIR/pr.md", null)]
    [InlineData("gh pr create --title x --body-file $BATON_OUTPUT_DIR/pr.md", 2005)]
    [InlineData("gh pr create --title x --body-file %BATON_OUTPUT_DIR%/pr.md", null)]
    [InlineData("GH_TOKEN=$T gh pr create --fill", null)]
    // A line that MENTIONS the words is not a line that runs them: only a segment whose command head
    // is `gh` is judged, so prose in an argument is out of scope. `--format=%H` is the second half of
    // the same finding -- it carries a `%`, and a whole-line scan refused it.
    [InlineData("echo \"gh pr view 3\"", 2005)]
    [InlineData("git log --grep \"gh pr create\" --format=%H", 2005)]
    [InlineData("git commit -m \"gh pr view 1994 is refused\"", 2005)]
    public void Reads_this_rule_does_not_govern_are_allowed(string commandLine, int? ownPullRequest)
    {
        Assert.Null(OwnPullRequestOnlyRule.RefusalFor(commandLine, ownPullRequest));
    }

    [Theory]
    // Before the room has opened anything, every governed read is refused -- there is no number it
    // could legitimately be asking for.
    [InlineData("gh pr view 1994", null)]
    [InlineData("gh pr view", null)]
    [InlineData("gh pr diff 1994", null)]
    [InlineData("gh pr checkout 1994", null)]
    [InlineData("gh pr list", null)]
    // After it has opened #2005, a sibling's number is still refused...
    [InlineData("gh pr view 1994", 2005)]
    [InlineData("gh pr diff 1994", 2005)]
    [InlineData("gh pr checkout 1994", 2005)]
    [InlineData("gh pr view https://github.com/aer-works/baton/pull/1994", 2005)]
    // ...and `gh pr list` never becomes allowed, for the reason EnumeratingSubCommands states.
    [InlineData("gh pr list", 2005)]
    [InlineData("gh pr list --state open", 2005)]
    // `gh pr status` is the same enumeration under another verb: every comparator arm authenticates
    // as one GitHub account, so "your" pull requests are all of them (second-reader finding, :36).
    [InlineData("gh pr status", null)]
    [InlineData("gh pr status", 2005)]
    // The write-shaped verbs are governed too -- commenting on a sibling's PR is worse than reading
    // one -- and a free-text body that names a number is refused with them (see FreeTextSubCommands).
    [InlineData("gh pr comment 1994 --body-file out.md", 2005)]
    [InlineData("gh pr edit 1994 --title x", 2005)]
    [InlineData("gh pr checks 1994", 2005)]
    // A chained or piped call reaches the rule the same way -- the measured lane chained exactly so.
    [InlineData("git status && gh pr view 1994", 2005)]
    [InlineData("gh pr list | head -20", 2005)]
    [InlineData("git branch -a | grep 1943 ; gh pr view 1994", 2005)]
    // A non-numeric POSITIONAL argument cannot be shown to be this room's PR, so it fails closed --
    // and a sibling arm's branch name is exactly that shape, which is why the positional test exists
    // beside the number-shape one (SelectorsIn states both).
    [InlineData("gh pr view 1943-a-claude", 2005)]
    [InlineData("gh pr view --repo aer-works/baton 1943-a-agy", 2005)]
    // An option written BEFORE the argument does not hide the argument (second-reader finding, :180).
    [InlineData("gh pr view --json title 1994", 2005)]
    [InlineData("gh pr diff --color never 1994", 2005)]
    // A valueless flag does not hide it either: the number shape is a selector wherever it sits.
    [InlineData("gh pr view -w 1994", 2005)]
    // A shell expansion is refused by its OWN arm, not by the non-numeric one above -- the shell
    // resolves it after this rule has read the line, so what it will say is not in the string. These
    // rows are what stops a later relaxation of the non-numeric branch from reopening substitution.
    [InlineData("gh pr view $(gh pr list --json number -q '.[0].number')", 2005)]
    [InlineData("gh pr view `cat n`", 2005)]
    [InlineData("P=view; gh pr $P 1994", 2005)]
    [InlineData("gh pr view %PRNUM%", 2005)]
    [InlineData("for /f %i in ('git branch -a') do gh pr view %i", 2005)]
    [InlineData("gh pr view ${OTHER}", 2005)]
    // ...including when the expansion would resolve to this room's own number: an expansion is
    // refused for being unreadable, never for what it happens to contain.
    [InlineData("gh pr view $MY_PR", 2005)]
    // An option written before the variable does not hide it, the same way it does not hide a literal
    // selector: the positional walk reaches `$N` past `--json title`.
    [InlineData("gh pr view --json title $N", 2005)]
    [InlineData("gh pr view -w ${OTHER}", 2005)]
    // The two routes that reach a pull request without saying `gh pr`, on THIS path too -- the broker
    // and the hooks refuse the same set. Which of the two a role's own deny pattern also closes, and
    // which this rule alone closes, is stated on ReadsPullRequestsWithoutSayingGhPr.
    [InlineData("gh search prs --state open --repo aer-works/baton", 2005)]
    [InlineData("gh search prs --state open --repo aer-works/baton", null)]
    [InlineData("gh api repos/aer-works/baton/pulls/1994", 2005)]
    public void Reading_a_pull_request_this_room_does_not_own_is_refused(string commandLine, int? ownPullRequest)
    {
        var refusal = OwnPullRequestOnlyRule.RefusalFor(commandLine, ownPullRequest);
        Assert.NotNull(refusal);
        Assert.Contains(OwnPullRequestOnlyRule.Rule, refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("gh pr view 2005", 2005)]
    [InlineData("gh pr view #2005", 2005)]
    [InlineData("gh pr diff 2005", 2005)]
    [InlineData("gh pr checkout 2005", 2005)]
    [InlineData("gh pr view -w 2005", 2005)] // the flag's own value is not mistaken for the PR
    [InlineData("gh pr view https://github.com/aer-works/baton/pull/2005", 2005)]
    [InlineData("gh pr view", 2005)] // the bare form reads the PR of the branch the room is on
    [InlineData("git diff && gh pr view 2005", 2005)]
    // The other polarity of the :180 finding: an OPTION'S VALUE is not read as the selector, so the
    // flag-first spelling of a read of the room's own PR is allowed. Every one of these was refused
    // before, which broke the lane's re-read-the-body-after-create habit.
    [InlineData("gh pr view --json body", 2005)]
    [InlineData("gh pr view --json title 2005", 2005)]
    [InlineData("gh pr diff --color never", 2005)]
    [InlineData("gh pr diff --repo aer-works/baton", 2005)]
    [InlineData("gh pr view --repo aer-works/baton 2005", 2005)]
    [InlineData("gh pr checks", 2005)]
    [InlineData("gh pr comment --body-file out.md", 2005)]
    // A separator ends the invocation's arguments: `tee` is not a pull request selector.
    [InlineData("gh pr view | tee pr.md", 2005)]
    public void Reading_the_pull_request_this_room_opened_is_allowed(string commandLine, int ownPullRequest)
    {
        Assert.Null(OwnPullRequestOnlyRule.RefusalFor(commandLine, ownPullRequest));
    }

    /// <summary>
    /// #2001's fixture stream, in order: the worker reads a sibling PR before it has created one of
    /// its own, then creates one, then reads both.
    /// </summary>
    [Fact]
    public void A_room_learns_its_own_pull_request_from_its_own_gh_pr_create()
    {
        var rule = new OwnPullRequestOnlyRule();

        Assert.Null(rule.OwnPullRequest);
        Assert.NotNull(rule.Refuse("gh pr view 1994"));

        rule.ObserveCommandOutput(
            "gh pr create --fill --body-file body.md",
            "Warning: 3 uncommitted changes\nhttps://github.com/aer-works/baton/pull/2005\n");

        Assert.Equal(2005, rule.OwnPullRequest);
        Assert.Null(rule.Refuse("gh pr view 2005"));
        Assert.NotNull(rule.Refuse("gh pr view 1994"));
    }

    [Theory]
    // The polarity arm for the learning step: the same URL, arriving from a read rather than a
    // create, must not open the gate -- otherwise one `gh pr view 1994` would authorize itself.
    [InlineData("gh pr view 1994")]
    // Second-reader finding (:91), the discriminating row for it: this line is allowed, mentions the
    // three words at a non-head offset, and prints a pull URL. `ObserveCommandOutput`'s remarks state
    // why the gate-opening side is anchored where the refusing side is not.
    [InlineData("echo \"gh pr create\" && curl -s https://api.github.com/repos/aer-works/baton/pulls")]
    [InlineData("gh issue view 1943 --comments")]
    [InlineData("git log --grep='gh pr create'")]
    // The fail-OPEN residual round 3 closed: a real create that is not the line's LAST segment, with
    // a sibling's `html_url` printed after it. ObserveCommandOutput's remark says why the whole
    // line's output cannot be attributed per segment; what this row pins is the consequence — such
    // a line teaches nothing.
    [InlineData("gh pr create --fill && curl -s https://api.github.com/repos/aer-works/baton/pulls")]
    public void Output_of_a_command_that_is_not_a_gh_pr_create_teaches_the_room_nothing(string commandLine)
    {
        var rule = new OwnPullRequestOnlyRule();

        rule.ObserveCommandOutput(
            commandLine,
            "see https://github.com/aer-works/baton/pull/1994 for #1994\n");

        Assert.Null(rule.OwnPullRequest);
        Assert.NotNull(rule.Refuse("gh pr view 1994"));
    }

    [Fact]
    public void A_gh_pr_create_at_the_head_of_a_later_segment_still_teaches_the_room()
    {
        // The discriminating control for the theory above: the same output, from a line whose SECOND
        // segment is a real create, does open the gate. Without this row the anchoring could be
        // "learns nothing, ever" and every refusal row would still pass.
        var rule = new OwnPullRequestOnlyRule();

        rule.ObserveCommandOutput(
            "git push -u origin HEAD && gh pr create --fill",
            "https://github.com/aer-works/baton/pull/2005\n");

        Assert.Equal(2005, rule.OwnPullRequest);
    }

    /// <summary>
    /// #2001's HIGH finding: the measured contamination was an agy lane, judged by a
    /// <c>PreToolUse</c> hook that cannot know the room's PR number. This entry point is what the two
    /// hooks call, and the rule it enforces is "no pull-request selector" rather than "this number".
    /// </summary>
    [Theory]
    // Allowed: no selector, so `gh` resolves the branch this room is standing on -- its own PR.
    [InlineData("gh pr view", null)]
    [InlineData("gh pr diff", null)]
    [InlineData("gh pr checks", null)]
    [InlineData("gh pr view --json number", null)]
    [InlineData("gh pr view --json title,body -q .body", null)]
    [InlineData("gh pr comment --body-file out.md", null)]
    [InlineData("gh pr edit --body-file body.md", null)]
    [InlineData("gh pr create --fill", null)]
    // The delivery line, on this path too: `create` is ungoverned and a variable in a `--body-file`
    // path is not a selector. A line that only MENTIONS the words is not judged at all.
    [InlineData("gh pr create --title x --body-file $BATON_OUTPUT_DIR/pr.md", null)]
    [InlineData("echo \"gh pr view 3\"", null)]
    [InlineData("gh issue view 1943", null)]
    [InlineData("gh issue view 1943 --comments", null)]
    [InlineData("git status && gh pr view", null)]
    [InlineData("gh pr view | tee pr.md", null)]
    // Refused: every selector, including a number that MIGHT be this room's own -- this path has no
    // number to compare against, which is the whole reason it exists.
    [InlineData("gh pr view 1994", "1994")]
    [InlineData("gh pr view 2016", "2016")]
    [InlineData("gh pr view #1994", "#1994")]
    [InlineData("gh pr view https://github.com/aer-works/baton/pull/1994", "pull/1994")]
    [InlineData("gh pr view 1943-a-agy", "1943-a-agy")]
    [InlineData("gh pr view --json title 2016", "2016")]
    [InlineData("gh pr view -w 1994", "1994")]
    [InlineData("gh pr diff 1994", "1994")]
    [InlineData("gh pr checks 1994", "1994")]
    [InlineData("gh pr comment 1994 --body-file out.md", "1994")]
    [InlineData("gh pr edit 1994 --title x", "1994")]
    [InlineData("git branch -a | grep 1943 ; gh pr view 1994", "1994")]
    // Refused whatever the selector: enumeration, a worktree move, an expansion, and the two routes
    // that reach a pull request without ever saying `gh pr`.
    [InlineData("gh pr list", "enumerates")]
    [InlineData("gh pr list --state open", "enumerates")]
    [InlineData("gh pr status", "enumerates")]
    [InlineData("gh pr checkout 1994", "checkout")]
    [InlineData("gh pr checkout", "checkout")]
    [InlineData("gh pr view $PR", "expansion")]
    [InlineData("gh pr view --json title $N", "expansion")]
    [InlineData("gh pr $VERB 1994", "expansion")]
    [InlineData("gh api repos/aer-works/baton/pulls/1994", "gh api")]
    [InlineData("gh search prs --state open", "gh search prs")]
    public void The_hook_entry_point_allows_only_the_selectorless_form(
        string commandLine, string? expectedInRefusal)
    {
        var refusal = OwnPullRequestOnlyRule.RefusalForOwnBranchOnly(commandLine);

        if (expectedInRefusal is null)
        {
            Assert.Null(refusal);
            return;
        }

        Assert.NotNull(refusal);
        Assert.Contains(OwnPullRequestOnlyRule.Rule, refusal, StringComparison.Ordinal);
        Assert.Contains("with no selector", refusal, StringComparison.Ordinal);
        Assert.Contains(expectedInRefusal, refusal, StringComparison.Ordinal);
    }

    /// <summary>
    /// The two ends of <see cref="OwnPullRequestOnlyRule.AppliesTo"/>, asserted against the REAL role
    /// catalog rather than a hand-built grant, so a catalog edit that widened or narrowed either
    /// role's shell patterns fails here. That method states why the two differ.
    /// </summary>
    [Fact]
    public void The_rule_governs_implement_and_janitor_and_exempts_review()
    {
        Assert.True(OwnPullRequestOnlyRule.AppliesTo(WorkerRoleCatalog.For("implement").Grant));
        // janitor is the third shell-bearing role, and it is governed DELIBERATELY -- second-reader
        // finding (:27), which found it governed with every statement about the rule saying
        // "implement". The class remarks state which way it is meant to go and what it costs.
        Assert.True(OwnPullRequestOnlyRule.AppliesTo(WorkerRoleCatalog.For("janitor").Grant));
        Assert.False(OwnPullRequestOnlyRule.AppliesTo(WorkerRoleCatalog.For("review").Grant));
    }

    [Fact]
    public void A_grant_with_no_shell_at_all_is_not_governed()
    {
        Assert.False(OwnPullRequestOnlyRule.AppliesTo(new PermissionGrant(ReadFiles: true)));
    }
}
