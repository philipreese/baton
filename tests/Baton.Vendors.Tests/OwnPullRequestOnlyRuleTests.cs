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
    // ...and `gh pr list` never becomes allowed: it names no PR, so it is the sibling enumeration
    // whatever this room owns. This is the call the contaminated lane made first.
    [InlineData("gh pr list", 2005)]
    [InlineData("gh pr list --state open", 2005)]
    // A chained or piped call reaches the rule the same way -- the measured lane chained exactly so.
    [InlineData("git status && gh pr view 1994", 2005)]
    [InlineData("gh pr list | head -20", 2005)]
    [InlineData("git branch -a | grep 1943 ; gh pr view 1994", 2005)]
    // A non-numeric argument cannot be shown to be this room's PR, so it fails closed.
    [InlineData("gh pr view 1943-a-claude", 2005)]
    [InlineData("gh pr diff --repo aer-works/baton", 2005)]
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

    [Fact]
    public void Output_of_a_command_that_is_not_gh_pr_create_teaches_the_room_nothing()
    {
        var rule = new OwnPullRequestOnlyRule();

        // The polarity arm for the learning step: the same URL, arriving from a read rather than a
        // create, must not open the gate -- otherwise one `gh pr view 1994` would authorize itself.
        rule.ObserveCommandOutput("gh pr view 1994", "https://github.com/aer-works/baton/pull/1994");

        Assert.Null(rule.OwnPullRequest);
        Assert.NotNull(rule.Refuse("gh pr view 1994"));
    }

    /// <summary>
    /// The two ends of <see cref="OwnPullRequestOnlyRule.AppliesTo"/>, asserted against the REAL role
    /// catalog rather than a hand-built grant, so a catalog edit that widened or narrowed either
    /// role's shell patterns fails here. That method states why the two differ.
    /// </summary>
    [Fact]
    public void The_rule_governs_implement_and_exempts_review()
    {
        Assert.True(OwnPullRequestOnlyRule.AppliesTo(WorkerRoleCatalog.For("implement").Grant));
        Assert.False(OwnPullRequestOnlyRule.AppliesTo(WorkerRoleCatalog.For("review").Grant));
    }

    [Fact]
    public void A_grant_with_no_shell_at_all_is_not_governed()
    {
        Assert.False(OwnPullRequestOnlyRule.AppliesTo(new PermissionGrant(ReadFiles: true)));
    }
}
