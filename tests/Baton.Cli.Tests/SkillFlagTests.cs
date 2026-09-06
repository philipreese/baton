using Baton.Cli;
using Baton.Domain;
using Baton.Vendors;

namespace Baton.Cli.Tests;

/// <summary>
/// #1151 S1's dispatch surface: <c>--skill</c>'s parse on both verbs, and the
/// inherit/clear/replace-wholesale rule a redispatch applies. The inheritance arms are the ones that
/// matter — a redispatch that silently drops a lane's skills would reintroduce #1512 one verb over,
/// and #1686 review F2 is the recorded instance of exactly that bug on a different field.
/// </summary>
public sealed class SkillFlagTests
{
    private static WorkerBindingConfigEntry ParentEntry(IReadOnlyList<string>? skills) =>
        new(
            Adapter: "claude",
            Contract: new WorkerContract("review", [], [], []),
            PromptTemplate: "Review the diff.",
            Timeout: TimeSpan.FromMinutes(30),
            Skills: skills);

    private static RedispatchOptions RedispatchArgs(params string[] args) =>
        RedispatchOptionsParser.Parse([@"C:\rooms\parent", .. args]);

    [Fact]
    public void Dispatch_parses_a_repeated_skill_flag_in_order_and_collapses_duplicates()
    {
        var options = DispatchOptionsParser.Parse(
            ["review", "--spec-text", "look at it", "--skill", "house-style", "--skill", "thorough-review", "--skill", "house-style"]);

        Assert.Equal(["house-style", "thorough-review"], options.Skills!.ToArray());
    }

    [Fact]
    public void Dispatch_with_no_skill_flag_leaves_the_list_null()
    {
        var options = DispatchOptionsParser.Parse(["review", "--spec-text", "look at it"]);

        Assert.Null(options.Skills);
    }

    [Fact]
    public void A_blank_skill_alongside_a_named_one_is_refused_on_both_verbs()
    {
        Assert.Throws<CliArgumentException>(() => DispatchOptionsParser.Parse(
            ["review", "--spec-text", "x", "--skill", "", "--skill", "house-style"]));
        Assert.Throws<CliArgumentException>(() => RedispatchArgs("--skill", "house-style", "--skill", ""));
    }

    [Fact]
    public void Redispatch_distinguishes_a_clearing_flag_from_an_absent_one()
    {
        var absent = RedispatchArgs();
        Assert.False(absent.SkillsSpecified);
        Assert.Null(absent.Skills);

        var cleared = RedispatchArgs("--skill", "");
        Assert.True(cleared.SkillsSpecified);
        Assert.Null(cleared.Skills);

        var replaced = RedispatchArgs("--skill", "house-style");
        Assert.True(replaced.SkillsSpecified);
        Assert.Equal(["house-style"], replaced.Skills!.ToArray());
    }

    [Fact]
    public void Redispatch_inherits_the_parents_skills_when_the_flag_is_absent()
    {
        var inherited = RedispatchCommand.InheritBinding(ParentEntry(["alpha", "beta"]), RedispatchArgs());

        Assert.Equal(["alpha", "beta"], inherited.Skills!.ToArray());
    }

    [Fact]
    public void Redispatch_clears_the_parents_skills_on_an_empty_flag()
    {
        var cleared = RedispatchCommand.InheritBinding(ParentEntry(["alpha", "beta"]), RedispatchArgs("--skill", ""));

        Assert.Null(cleared.Skills);
    }

    [Fact]
    public void Redispatch_replaces_the_parents_skills_wholesale_rather_than_appending()
    {
        var replaced = RedispatchCommand.InheritBinding(
            ParentEntry(["alpha", "beta"]), RedispatchArgs("--skill", "gamma"));

        Assert.Equal(["gamma"], replaced.Skills!.ToArray());
    }

    [Fact]
    public void Both_verbs_advertise_the_flag_in_their_usage_line()
    {
        // The flag exists only if an operator can find it: a --skill that parses but is undocumented in
        // the usage line every parse error prints is a capability nobody discovers.
        Assert.Contains("--skill", DispatchOptionsParser.Usage, StringComparison.Ordinal);
        Assert.Contains("--skill", RedispatchOptionsParser.Usage, StringComparison.Ordinal);
    }
}
