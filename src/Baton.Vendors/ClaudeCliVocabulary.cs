namespace Baton.Vendors;

/// <summary>
/// The claude CLI's own vocabulary as <see cref="ClaudeWorkerAdapter"/> puts it on the wire: the tool
/// names it names in a permission scope, and the two permission flags those names ride on.
/// </summary>
/// <remarks>
/// <para>
/// These are the vendor's spellings, not AER's — every value here is fixed by the claude CLI and
/// changing one changes what the CLI is asked to permit or refuse. The point of naming them is the
/// deny direction: a typo'd entry on <see cref="DisallowedToolsFlag"/> silently fails to deny (the
/// CLI ignores a name it doesn't know), which no test of the adapter's own logic can catch, whereas
/// a mistyped constant does not compile.
/// </para>
/// <para>
/// Because the values are the wire shape, the tests that assert on them
/// (<c>ClaudeWorkerAdapterTests</c>, <c>ChannelPopulationTests</c>, <c>WriteFamilyContractTests</c>)
/// deliberately keep asserting the literal strings rather than these constants, so renaming a
/// constant cannot quietly change what claude is invoked with; <c>ClaudeCliVocabularyTests</c> pins
/// each constant to its literal.
/// </para>
/// </remarks>
public static class ClaudeCliVocabulary
{
    /// <summary>claude's shell tool. Also the name a <c>Bash(pattern)</c> grant clause opens with.</summary>
    public const string BashToolName = "Bash";

    /// <summary>claude's file-read tool.</summary>
    public const string ReadToolName = "Read";

    /// <summary>claude's in-place file-edit tool.</summary>
    public const string EditToolName = "Edit";

    /// <summary>claude's whole-file write tool.</summary>
    public const string WriteToolName = "Write";

    /// <summary>claude's notebook-cell edit tool — a member of the write family alongside
    /// <see cref="EditToolName"/> and <see cref="WriteToolName"/>.</summary>
    public const string NotebookEditToolName = "NotebookEdit";

    /// <summary>claude's URL-fetch tool.</summary>
    public const string WebFetchToolName = "WebFetch";

    /// <summary>claude's web-search tool.</summary>
    public const string WebSearchToolName = "WebSearch";

    /// <summary>claude's subagent-spawning tool.</summary>
    public const string AgentToolName = "Agent";

    /// <summary><see cref="AgentToolName"/>'s older name, still honoured by the CLI — so both are
    /// withheld together (docs/vendor-capabilities.md's canonical ceiling).</summary>
    public const string TaskToolName = "Task";

    /// <summary>
    /// The prefix a shell-pattern grant clause opens with. <see cref="BashToolName"/>'s length is
    /// therefore the index of the <c>'('</c> within such a clause.
    /// </summary>
    public const string BashGrantPrefix = $"{BashToolName}(";

    /// <summary>
    /// Both subagent names as one comma-joined <see cref="DisallowedToolsFlag"/> fragment.
    /// </summary>
    public const string SubagentToolNames = $"{AgentToolName},{TaskToolName}";

    /// <summary>
    /// The flag that <em>pre-approves</em> tools so they don't prompt — not a ceiling; see
    /// <see cref="ClaudeWorkerAdapter"/>'s class doc for what that does and does not withhold (#331).
    /// </summary>
    public const string AllowedToolsFlag = "--allowedTools";

    /// <summary>
    /// The flag that refuses tools, taking precedence over <see cref="AllowedToolsFlag"/>.
    /// </summary>
    public const string DisallowedToolsFlag = "--disallowedTools";
}
