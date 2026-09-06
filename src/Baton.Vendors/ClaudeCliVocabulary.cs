namespace Baton.Vendors;

/// <summary>
/// The claude CLI's own wire vocabulary — the tool names and permission flag names
/// <see cref="ClaudeWorkerAdapter"/> emits — named once so the compiler can catch a typo the CLI
/// would otherwise absorb silently (#1918). A mistyped <c>--disallowedTools</c> entry does not fail
/// loudly: the CLI simply never matches it, and the withheld category stays reachable. That is the
/// specific defect this class exists to make impossible.
/// <para>
/// <b>These values are claude's spelling, not AER's, so they are not free to rename.</b> Renaming a
/// member here is a refactor; changing a <em>value</em> here changes what reaches the vendor
/// process. <c>ClaudeCliVocabularyTests</c> pins every value to its literal for exactly that reason,
/// and the adapter's own tests keep asserting the literal rather than the constant so a renamed
/// member cannot silently move the wire shape.
/// </para>
/// </summary>
public static class ClaudeCliVocabulary
{
    /// <summary>The file-read tool name.</summary>
    public const string ReadTool = "Read";

    /// <summary>The in-place file-edit tool name.</summary>
    public const string EditTool = "Edit";

    /// <summary>The whole-file write tool name.</summary>
    public const string WriteTool = "Write";

    /// <summary>The notebook-cell edit tool name — the third member of the write family.</summary>
    public const string NotebookEditTool = "NotebookEdit";

    /// <summary>
    /// The shell tool name. Shared with <see cref="ShellCommandPatternMatcher.ShellToolNames"/>,
    /// which holds this same name alongside agy's <c>run_command</c>; that pairing is the one
    /// canonical cross-vendor list, and this constant is the one canonical spelling of claude's half.
    /// </summary>
    public const string BashTool = "Bash";

    /// <summary>The URL-fetch tool name.</summary>
    public const string WebFetchTool = "WebFetch";

    /// <summary>The web-search tool name.</summary>
    public const string WebSearchTool = "WebSearch";

    /// <summary>
    /// The subagent-spawn tool name (#1802). Withheld together with <see cref="TaskTool"/>.
    /// </summary>
    public const string AgentTool = "Agent";

    /// <summary>
    /// <see cref="AgentTool"/>'s older name, still honoured by the CLI, so both must be withheld to
    /// withhold subagents at all (docs/vendor-capabilities.md's canonical ceiling).
    /// </summary>
    public const string TaskTool = "Task";

    /// <summary>
    /// The prefix a scoped shell grant clause opens with — <c>Bash(git diff*)</c>. Load-bearing
    /// beyond its text: <c>ClaudeWorkerAdapter.TryExtractBalancedBashClauseInner</c> derives both of
    /// its clause offsets from lengths in this family, so the pinned value is what keeps those
    /// offsets pointing at the paren rather than into the pattern.
    /// </summary>
    public const string BashGrantPrefix = BashTool + "(";

    /// <summary>
    /// The pre-approval flag. Not a sandbox and not a ceiling (#331): it stops a tool prompting, it
    /// does not put an omitted tool out of reach — which is why <see cref="DisallowedToolsFlag"/>
    /// exists as a separate channel rather than this one being enough on its own.
    /// </summary>
    public const string AllowedToolsFlag = "--allowedTools";

    /// <summary>
    /// The active-denial flag, which takes precedence over <see cref="AllowedToolsFlag"/>. This is
    /// the one whose entries fail silently when misspelled — the CLI matches nothing and refuses
    /// nothing — and so the reason this class is worth having.
    /// </summary>
    public const string DisallowedToolsFlag = "--disallowedTools";
}
