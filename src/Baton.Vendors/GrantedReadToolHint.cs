namespace Baton.Vendors;

/// <summary>
/// #1920: the one clause a shell refusal appends so a worker does not have to discover the granted
/// read path by trial. Measured on a codex review lane: <c>rg -n …</c> was re-issued four times
/// before the model reached <c>baton_search_text</c>, and a claude review lane spent 46 refusals on
/// <c>cd</c>/<c>cat</c>/<c>head</c>/<c>git grep</c> before falling back to Read/Grep.
/// </summary>
/// <remarks>
/// <see cref="ShellCommandPatternMatcher"/> produces the refusal <em>reason</em> and is deliberately
/// vendor-agnostic — it knows patterns, not tool names — so it cannot name a granted alternative.
/// Each producing site knows its own vendor and appends this clause to the reason it surfaces:
/// <c>HookCheckCommand</c> (claude), <c>AgyHookCheckCommand</c> (agy), and
/// <c>CodexDynamicToolPolicy.RunCommandAsync</c> (codex, which passes names derived from the tools
/// it actually declared). The per-vendor names live here once rather than at each site.
/// <para>
/// The clause is emitted only when BOTH names are available. A half-clause naming a search tool on a
/// role whose reads are withheld would teach a way around the grant, which is the opposite of the
/// failure this exists to fix.
/// </para>
/// </remarks>
public static class GrantedReadToolHint
{
    /// <summary>Claude's own read/search tools — the pair the measured claude review lane fell back to.</summary>
    private const string ClaudeReadTool = "Read";
    private const string ClaudeSearchTool = "Grep";

    /// <summary>
    /// Agy's read/search tools, two of the four names <c>AgyWorkerAdapter</c>'s <c>ReadTools</c>
    /// withholds together when a grant denies reads.
    /// </summary>
    private const string AgyReadTool = "view_file";
    private const string AgySearchTool = "grep_search";

    /// <summary>
    /// The claude clause, or <see langword="null"/> when this session withheld the Read tool.
    /// <paramref name="isWithheld"/> is the calling hook's own withheld-tool test, not a second copy
    /// of it, so the clause never names a tool this dispatch took away.
    /// </summary>
    public static string? ForClaude(Func<string, bool> isWithheld)
    {
        ArgumentNullException.ThrowIfNull(isWithheld);

        // Grep is not a name claude's grant translation ever withholds (ClaudeWorkerAdapter's
        // WithheldToolNames withholds Read on !ReadFiles and nothing else read-shaped), so the pair
        // stands or falls on Read: a role that may not read is told nothing rather than pointed at a
        // search tool, which would be teaching a way around the grant.
        return isWithheld(ClaudeReadTool) ? null : Clause(ClaudeReadTool, ClaudeSearchTool);
    }

    /// <summary>The agy clause, or <see langword="null"/> when either agy read tool is withheld.</summary>
    public static string? ForAgy(Func<string, bool> isWithheld)
    {
        ArgumentNullException.ThrowIfNull(isWithheld);

        return Clause(
            isWithheld(AgyReadTool) ? null : AgyReadTool,
            isWithheld(AgySearchTool) ? null : AgySearchTool);
    }

    /// <summary>
    /// The clause for a vendor whose tool set is decided per dispatch (codex): the caller passes the
    /// names it declared, or <see langword="null"/> for a tool it did not declare.
    /// </summary>
    public static string? Clause(string? readTool, string? searchTool) =>
        string.IsNullOrWhiteSpace(readTool) || string.IsNullOrWhiteSpace(searchTool)
            ? null
            : $"read files with {readTool} and search them with {searchTool} instead — "
              + "neither goes through this shell grant";
}
