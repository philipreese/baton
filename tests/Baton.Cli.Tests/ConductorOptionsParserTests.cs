namespace Baton.Cli.Tests;

public sealed class ConductorOptionsParserTests
{
    [Fact]
    public void Parse_EmptyArgs_ThrowsCliArgumentExceptionWithUsage()
    {
        var ex = Assert.Throws<CliArgumentException>(() => ConductorOptionsParser.Parse([]));
        Assert.Contains("'baton conductor' requires a sub-verb", ex.Message);
        Assert.Contains(ConductorOptionsParser.Usage, ex.Message);
    }

    [Fact]
    public void Parse_UnknownSubverb_ThrowsCliArgumentExceptionWithUsage()
    {
        var ex = Assert.Throws<CliArgumentException>(() => ConductorOptionsParser.Parse(["unknown"]));
        Assert.Contains("Unknown 'baton conductor' sub-verb 'unknown'", ex.Message);
        Assert.Contains(ConductorOptionsParser.Usage, ex.Message);
    }

    [Fact]
    public void Parse_Claim_ValidArgs()
    {
        var options = ConductorOptionsParser.Parse(["claim", "holder-1"]);
        Assert.Equal(ConductorVerb.Claim, options.Verb);
        Assert.Equal("holder-1", options.Holder);
        Assert.Null(options.Workspace);
        Assert.False(options.Json);
    }

    [Fact]
    public void Parse_Claim_WithWorkspace()
    {
        var options = ConductorOptionsParser.Parse(["claim", "holder-1", "--workspace", "my/workspace"]);
        Assert.Equal(ConductorVerb.Claim, options.Verb);
        Assert.Equal("holder-1", options.Holder);
        Assert.Equal("my/workspace", options.Workspace);
    }

    [Fact]
    public void Parse_Claim_MissingHolder_ThrowsCliArgumentException()
    {
        var ex = Assert.Throws<CliArgumentException>(() => ConductorOptionsParser.Parse(["claim"]));
        Assert.Contains("'baton conductor claim' requires a <holder> argument", ex.Message);
    }

    [Fact]
    public void Parse_Claim_MissingWorkspaceValue_ThrowsCliArgumentException()
    {
        var ex = Assert.Throws<CliArgumentException>(() => ConductorOptionsParser.Parse(["claim", "holder-1", "--workspace"]));
        Assert.Contains("'--workspace' requires a directory argument", ex.Message);
    }

    [Fact]
    public void Parse_Claim_UnknownOption_ThrowsCliArgumentException()
    {
        var ex = Assert.Throws<CliArgumentException>(() => ConductorOptionsParser.Parse(["claim", "holder-1", "--unknown"]));
        Assert.Contains("Unknown option '--unknown'", ex.Message);
    }

    [Fact]
    public void Parse_Claim_UnexpectedArgument_ThrowsCliArgumentException()
    {
        var ex = Assert.Throws<CliArgumentException>(() => ConductorOptionsParser.Parse(["claim", "holder-1", "extra"]));
        Assert.Contains("Unexpected argument 'extra'", ex.Message);
    }

    [Fact]
    public void Parse_List_Default()
    {
        var options = ConductorOptionsParser.Parse(["list"]);
        Assert.Equal(ConductorVerb.List, options.Verb);
        Assert.False(options.Json);
    }

    [Fact]
    public void Parse_List_Json()
    {
        var options = ConductorOptionsParser.Parse(["list", "--json"]);
        Assert.Equal(ConductorVerb.List, options.Verb);
        Assert.True(options.Json);
    }

    [Fact]
    public void Parse_List_UnknownOption_ThrowsCliArgumentException()
    {
        var ex = Assert.Throws<CliArgumentException>(() => ConductorOptionsParser.Parse(["list", "--other"]));
        Assert.Contains("Unknown option '--other'", ex.Message);
    }

    [Fact]
    public void Parse_Release_ValidArgs()
    {
        var options = ConductorOptionsParser.Parse(["release", "holder-1", "--reason", "completed work"]);
        Assert.Equal(ConductorVerb.Release, options.Verb);
        Assert.Equal("holder-1", options.Holder);
        Assert.Equal("completed work", options.Reason);
        Assert.Null(options.Workspace);
    }

    [Fact]
    public void Parse_Release_WithWorkspace()
    {
        var options = ConductorOptionsParser.Parse(["release", "holder-1", "--workspace", "dir/path", "--reason", "completed work"]);
        Assert.Equal(ConductorVerb.Release, options.Verb);
        Assert.Equal("holder-1", options.Holder);
        Assert.Equal("completed work", options.Reason);
        Assert.Equal("dir/path", options.Workspace);
    }

    [Fact]
    public void Parse_Release_MissingHolder_ThrowsCliArgumentException()
    {
        var ex = Assert.Throws<CliArgumentException>(() => ConductorOptionsParser.Parse(["release", "--reason", "why"]));
        Assert.Contains("'baton conductor release' requires a <holder> argument", ex.Message);
    }

    [Fact]
    public void Parse_Release_MissingReasonFlag_ThrowsCliArgumentException()
    {
        var ex = Assert.Throws<CliArgumentException>(() => ConductorOptionsParser.Parse(["release", "holder-1"]));
        Assert.Contains("'baton conductor release' requires a non-blank --reason", ex.Message);
    }

    [Fact]
    public void Parse_Release_MissingReasonValue_ThrowsCliArgumentException()
    {
        var ex = Assert.Throws<CliArgumentException>(() => ConductorOptionsParser.Parse(["release", "holder-1", "--reason"]));
        Assert.Contains("'--reason' requires an explanation text argument", ex.Message);
    }

    [Fact]
    public void Parse_Takeover_ValidArgs()
    {
        var options = ConductorOptionsParser.Parse(["takeover", "holder-2", "--reason", "holder-1 died"]);
        Assert.Equal(ConductorVerb.Takeover, options.Verb);
        Assert.Equal("holder-2", options.Holder);
        Assert.Equal("holder-1 died", options.Reason);
        Assert.Null(options.Workspace);
    }

    [Fact]
    public void Parse_Takeover_WithWorkspace()
    {
        var options = ConductorOptionsParser.Parse(["takeover", "holder-2", "--workspace", "other/dir", "--reason", "holder-1 died"]);
        Assert.Equal(ConductorVerb.Takeover, options.Verb);
        Assert.Equal("holder-2", options.Holder);
        Assert.Equal("holder-1 died", options.Reason);
        Assert.Equal("other/dir", options.Workspace);
    }

    [Fact]
    public void Parse_Takeover_MissingHolder_ThrowsCliArgumentException()
    {
        var ex = Assert.Throws<CliArgumentException>(() => ConductorOptionsParser.Parse(["takeover", "--reason", "why"]));
        Assert.Contains("'baton conductor takeover' requires a <holder> argument", ex.Message);
    }

    [Fact]
    public void Parse_Takeover_MissingReason_ThrowsCliArgumentException()
    {
        var ex = Assert.Throws<CliArgumentException>(() => ConductorOptionsParser.Parse(["takeover", "holder-2"]));
        Assert.Contains("'baton conductor takeover' requires a non-blank --reason", ex.Message);
    }
}
