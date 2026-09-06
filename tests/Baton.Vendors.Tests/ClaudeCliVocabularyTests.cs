using Baton.Vendors;

namespace Baton.Vendors.Tests;

/// <summary>
/// Pins every <see cref="ClaudeCliVocabulary"/> member to the literal string claude's CLI actually
/// reads (#1918) — see that class's own summary for why a value there is not free to change.
/// <para>
/// Every literal is written out here rather than referenced from the constants, which is the whole
/// point: a test asserting <c>ClaudeCliVocabulary.BashTool == ClaudeCliVocabulary.BashTool</c> would
/// pass through any retyping at all. The adapter's own suites (<c>ClaudeWorkerAdapterTests</c>,
/// <c>ChannelPopulationTests</c>, <c>WriteFamilyContractTests</c>) keep asserting the literals on the
/// emitted wire shape for the same reason; this class pins the source they emit from.
/// </para>
/// </summary>
public class ClaudeCliVocabularyTests
{
    [Fact]
    public void ToolNamesAreClaudesOwnSpelling()
    {
        Assert.Equal("Read", ClaudeCliVocabulary.ReadTool);
        Assert.Equal("Edit", ClaudeCliVocabulary.EditTool);
        Assert.Equal("Write", ClaudeCliVocabulary.WriteTool);
        Assert.Equal("NotebookEdit", ClaudeCliVocabulary.NotebookEditTool);
        Assert.Equal("Bash", ClaudeCliVocabulary.BashTool);
        Assert.Equal("WebFetch", ClaudeCliVocabulary.WebFetchTool);
        Assert.Equal("WebSearch", ClaudeCliVocabulary.WebSearchTool);
        Assert.Equal("Agent", ClaudeCliVocabulary.AgentTool);
        Assert.Equal("Task", ClaudeCliVocabulary.TaskTool);
    }

    [Fact]
    public void PermissionFlagsAreClaudesOwnSpelling()
    {
        Assert.Equal("--allowedTools", ClaudeCliVocabulary.AllowedToolsFlag);
        Assert.Equal("--disallowedTools", ClaudeCliVocabulary.DisallowedToolsFlag);
    }

    /// <summary>
    /// The lengths are asserted alongside the text because
    /// <c>ClaudeWorkerAdapter.TryExtractBalancedBashClauseInner</c> indexes clauses by them (see its
    /// own comment): a value change that kept the prefix parseable would still slice a scoped
    /// pattern at the wrong character, which the text assertion alone would not catch.
    /// </summary>
    [Fact]
    public void BashGrantPrefixPinsBothTheTextAndTheOffsetsDerivedFromIt()
    {
        Assert.Equal("Bash(", ClaudeCliVocabulary.BashGrantPrefix);
        Assert.Equal(4, ClaudeCliVocabulary.BashTool.Length);
        Assert.Equal(5, ClaudeCliVocabulary.BashGrantPrefix.Length);
    }
}
