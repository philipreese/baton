using System.Reflection;
using Baton.Vendors;

namespace Baton.Vendors.Tests;

/// <summary>
/// <see cref="ClaudeCliVocabulary"/>'s values are the claude CLI's own wire spellings, so renaming a
/// constant must never be able to change one. Every constant is pinned to its literal here, and the
/// completeness arm fails when a constant is added without a pin — a new deny-list name that nothing
/// checks is exactly the typo the vocabulary exists to make impossible.
/// </summary>
public class ClaudeCliVocabularyTests
{
    private static readonly Dictionary<string, string> PinnedLiterals = new(StringComparer.Ordinal)
    {
        [nameof(ClaudeCliVocabulary.BashToolName)] = "Bash",
        [nameof(ClaudeCliVocabulary.ReadToolName)] = "Read",
        [nameof(ClaudeCliVocabulary.EditToolName)] = "Edit",
        [nameof(ClaudeCliVocabulary.WriteToolName)] = "Write",
        [nameof(ClaudeCliVocabulary.NotebookEditToolName)] = "NotebookEdit",
        [nameof(ClaudeCliVocabulary.WebFetchToolName)] = "WebFetch",
        [nameof(ClaudeCliVocabulary.WebSearchToolName)] = "WebSearch",
        [nameof(ClaudeCliVocabulary.AgentToolName)] = "Agent",
        [nameof(ClaudeCliVocabulary.TaskToolName)] = "Task",
        [nameof(ClaudeCliVocabulary.BashGrantPrefix)] = "Bash(",
        [nameof(ClaudeCliVocabulary.SubagentToolNames)] = "Agent,Task",
        [nameof(ClaudeCliVocabulary.AllowedToolsFlag)] = "--allowedTools",
        [nameof(ClaudeCliVocabulary.DisallowedToolsFlag)] = "--disallowedTools",
    };

    public static TheoryData<string, string> EveryPin()
    {
        var data = new TheoryData<string, string>();
        foreach (var (name, literal) in PinnedLiterals)
        {
            data.Add(name, literal);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryPin))]
    public void ConstantCarriesItsWireLiteral(string constantName, string expectedLiteral)
    {
        var field = typeof(ClaudeCliVocabulary).GetField(constantName, BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(field);
        Assert.Equal(expectedLiteral, field.GetRawConstantValue());
    }

    [Fact]
    public void EveryConstantIsPinned()
    {
        var declared = typeof(ClaudeCliVocabulary)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral)
            .Select(field => field.Name)
            .OrderBy(name => name, StringComparer.Ordinal);

        Assert.Equal(PinnedLiterals.Keys.OrderBy(name => name, StringComparer.Ordinal), declared);
    }

    /// <summary>
    /// The offset <c>TryExtractBalancedBashClauseInner</c> starts its scan at: the paren that opens a
    /// grant clause sits at exactly <see cref="ClaudeCliVocabulary.BashToolName"/>'s length, which is
    /// one less than the grant prefix's — start a character later and depth never reaches 1.
    /// </summary>
    [Fact]
    public void GrantPrefixPutsTheOpeningParenAtTheToolNameLength()
    {
        Assert.Equal('(', ClaudeCliVocabulary.BashGrantPrefix[ClaudeCliVocabulary.BashToolName.Length]);
        Assert.Equal(ClaudeCliVocabulary.BashToolName.Length + 1, ClaudeCliVocabulary.BashGrantPrefix.Length);
    }
}
