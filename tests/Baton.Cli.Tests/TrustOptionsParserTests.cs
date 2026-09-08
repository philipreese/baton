using Baton.Vendors;

namespace Baton.Cli.Tests;

/// <summary>
/// <c>baton trust</c>'s argument parser (#1166) — three shapes: <c>&lt;project-path&gt; --ceiling
/// &lt;categories&gt;</c>, <c>--list</c>, and <c>&lt;project-path&gt; --revoke</c>.
/// </summary>
public sealed class TrustOptionsParserTests
{
    [Fact]
    public void Parse_ProjectPathAndCeilingAll_ParsesAsRegisterUnrestricted()
    {
        var options = TrustOptionsParser.Parse(["/repo", "--ceiling", "all"]);

        Assert.Equal(TrustMode.Register, options.Mode);
        Assert.Equal("/repo", options.ProjectPath);
        Assert.Equal(ProjectCeiling.Unrestricted, options.Ceiling);
    }

    [Fact]
    public void Parse_CeilingNone_ParsesAsEveryCategoryClosed()
    {
        var options = TrustOptionsParser.Parse(["/repo", "--ceiling", "none"]);

        Assert.Equal(new ProjectCeiling(false, false, false, false), options.Ceiling);
    }

    [Fact]
    public void Parse_CeilingCommaSeparatedCategories_ParsesOnlyThoseCategoriesOpen()
    {
        var options = TrustOptionsParser.Parse(["/repo", "--ceiling", "ReadFiles,WriteFiles"]);

        Assert.Equal(new ProjectCeiling(true, true, false, false), options.Ceiling);
    }

    /// <summary>#1166 review finding H: category tokens are case-insensitive, matching 'all'/'none'.</summary>
    [Fact]
    public void Parse_CeilingCategoriesAreCaseInsensitive()
    {
        var options = TrustOptionsParser.Parse(["/repo", "--ceiling", "readfiles,WRITEFILES"]);

        Assert.Equal(new ProjectCeiling(true, true, false, false), options.Ceiling);
    }

    [Fact]
    public void Parse_List_ParsesAsListWithNoOtherFields()
    {
        var options = TrustOptionsParser.Parse(["--list"]);

        Assert.Equal(TrustMode.List, options.Mode);
        Assert.Null(options.ProjectPath);
        Assert.Null(options.Ceiling);
    }

    [Fact]
    public void Parse_ProjectPathAndRevoke_ParsesAsRevokeWithNoCeiling()
    {
        var options = TrustOptionsParser.Parse(["/repo", "--revoke"]);

        Assert.Equal(TrustMode.Revoke, options.Mode);
        Assert.Equal("/repo", options.ProjectPath);
        Assert.Null(options.Ceiling);
    }

    /// <summary>#2121: <c>--forget</c> is its own mode, and cannot ride along with either of the other two path-taking shapes.</summary>
    [Fact]
    public void Parse_ProjectPathAndForget_ParsesAsForgetWithNoCeiling()
    {
        var options = TrustOptionsParser.Parse(["/repo", "--forget"]);

        Assert.Equal(TrustMode.Forget, options.Mode);
        Assert.Equal("/repo", options.ProjectPath);
        Assert.Null(options.Ceiling);
    }

    [Fact]
    public void Parse_ForgetCombinedWithRevokeOrCeiling_Throws()
    {
        var withRevoke = Assert.Throws<CliArgumentException>(() => TrustOptionsParser.Parse(["/repo", "--revoke", "--forget"]));
        var withCeiling = Assert.Throws<CliArgumentException>(() => TrustOptionsParser.Parse(["/repo", "--ceiling", "all", "--forget"]));

        Assert.Contains("'--revoke' cannot be combined with '--forget'", withRevoke.Message, StringComparison.Ordinal);
        Assert.Contains("'--forget' cannot be combined with '--ceiling'", withCeiling.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_MissingProjectPath_Throws()
    {
        var ex = Assert.Throws<CliArgumentException>(() => TrustOptionsParser.Parse(["--ceiling", "all"]));

        Assert.Contains("Missing required <project-path>", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_MissingCeilingAndRevoke_Throws()
    {
        var ex = Assert.Throws<CliArgumentException>(() => TrustOptionsParser.Parse(["/repo"]));

        Assert.Contains("Missing required '--ceiling", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_CeilingAndRevokeCombined_Throws()
    {
        var ex = Assert.Throws<CliArgumentException>(
            () => TrustOptionsParser.Parse(["/repo", "--ceiling", "all", "--revoke"]));

        Assert.Contains("cannot be combined", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_UnknownCeilingToken_Throws()
    {
        var ex = Assert.Throws<CliArgumentException>(
            () => TrustOptionsParser.Parse(["/repo", "--ceiling", "ReadFiles,Bogus"]));

        Assert.Contains("Unknown ceiling category 'Bogus'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_UnknownOption_Throws()
    {
        var ex = Assert.Throws<CliArgumentException>(
            () => TrustOptionsParser.Parse(["/repo", "--ceiling", "all", "--bogus"]));

        Assert.Contains("Unknown option '--bogus'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ExtraPositionalArgument_Throws()
    {
        var ex = Assert.Throws<CliArgumentException>(
            () => TrustOptionsParser.Parse(["/repo", "/other", "--ceiling", "all"]));

        Assert.Contains("Unexpected extra argument '/other'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ListCombinedWithOtherArguments_Throws()
    {
        var ex = Assert.Throws<CliArgumentException>(() => TrustOptionsParser.Parse(["/repo", "--list"]));

        Assert.Contains("'--list' cannot be combined", ex.Message, StringComparison.Ordinal);
    }
}
