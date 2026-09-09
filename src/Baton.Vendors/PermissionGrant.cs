namespace Baton.Vendors;

/// <summary>
/// A structured, vendor-neutral permission grant (M21 Phase 1) — the builder-UI alternative to a
/// hand-typed <see cref="WorkerInvocation.PermissionScope"/> string. Composable categories; each
/// <see cref="IPermissionGrantTranslator"/> translates the requested set into its own vendor's
/// actual flag syntax, refusing rather than approximating whenever it cannot express the request
/// exactly in either direction — granting more than asked is exactly as wrong as granting less.
/// <para>
/// Precedence when both are set on the same <see cref="WorkerInvocation"/>/<see cref="WorkerBindingConfigEntry"/>:
/// a non-null <see cref="WorkerInvocation.PermissionGrant"/> always wins over
/// <see cref="WorkerInvocation.PermissionScope"/> — see those types' own docs. The bindings editor
/// UI never authors both on the same entry; this only matters for a hand-edited config file.
/// </para>
/// </summary>
/// <param name="ReadFiles">Grants reading files beyond the worker's declared contract inputs.</param>
/// <param name="WriteFiles">Grants creating and editing files.</param>
/// <param name="RunShellCommands">
/// Grants shell/tool command execution. When <paramref name="ShellCommandPatterns"/> is non-empty,
/// vendors that support pattern-scoped shell grants (e.g. Claude's <c>Bash(git:*)</c>) restrict to
/// those patterns; an empty list means "any command" — not every vendor can express the
/// pattern-scoped form (see each <see cref="IPermissionGrantTranslator"/>'s own notes).
/// </param>
/// <param name="ShellCommandPatterns">Command-pattern allowlist (e.g. <c>"git:*"</c>) — only meaningful when <see cref="RunShellCommands"/> is set.</param>
/// <param name="NetworkAccess">Grants outbound network access (web fetch/search tools).</param>
/// <param name="DeniedShellCommandPatterns">
/// 0022's <c>DenyAlways</c> rung (M-Phase-6 #390) — a standing "never" list in the same
/// <c>ShellCommandPatternMatcher</c> glob form as <see cref="ShellCommandPatterns"/>, but subtractive:
/// a match here is refused regardless of <see cref="RunShellCommands"/> or a matching entry in
/// <see cref="ShellCommandPatterns"/>. A closed "no" is not reopened by a wider later grant. Enforced
/// next turn on both vendors — claude via <c>--disallowedTools Bash(pattern)</c>
/// (<c>ClaudeWorkerAdapter.BuildDisallowedTools</c>, which the CLI applies with precedence over
/// <c>--allowedTools</c>), agy via its <c>PreToolUse</c> hook's <c>EvaluateChainedCommand</c> check
/// (segment-level since #1685; agy has no
/// vendor flag that can express a command family). Historically written when the operator answered
/// the DenyAlways rung — that write path (0022's mid-lane ask/answer machinery) was retired in #1417
/// (spec/baton.md §5); this field remains enforced on both vendors for any grant that already
/// carries entries, e.g. from a hand-edited <c>bindings.json</c>.
/// </param>
/// <param name="ShellCommandsAreReadOnly">
/// Asserts that every pattern in <see cref="ShellCommandPatterns"/> is read-only: none of them can
/// write a file, mutate git/gh state, or reach network beyond what the specific named command
/// inherently needs (e.g. <c>gh pr view</c> reaching github.com). This is a claim made by the grant's
/// author, not a fact <see cref="CategoriesDefeatedByTheShell(bool, IReadOnlySet{string})"/> derives by parsing the patterns — a
/// pattern list that actually can write or mutate, with this set true, is the author's mistake, not
/// something this type catches. Exists so a role author can compose a genuinely read-only, narrowly
/// scoped shell (spec/baton.md §9, #1456) without widening <see cref="WriteFiles"/>/
/// <see cref="NetworkAccess"/> just to satisfy the coherence check below — the general field #1387
/// wants for a future scoped-shell-without-network grant. False (the default) leaves every existing
/// grant's coherence check exactly as conservative as it was before this field existed.
/// </param>
/// <param name="DeniedShellOptionTokens">
/// #1683 F2's deny rung for an <em>option</em> rather than a command family — literal token prefixes
/// (e.g. <c>"--output"</c>), subtractive like <see cref="DeniedShellCommandPatterns"/> above and
/// beating any allow, but matched by a different rule and enforced somewhere else. Both of those
/// differences, why the field exists at all, and what it deliberately over-matches are stated once on
/// <c>ShellCommandPatternMatcher.IsDeniedByOptionToken</c>; read that, not a paraphrase here. The one
/// consequence a reader of THIS type needs: it is the field standing between an author's
/// <see cref="ShellCommandsAreReadOnly"/> assertion and a shell that can in fact write a file.
/// </param>
/// <param name="DeniedShellCommandExceptions">
/// #2114: verbs excepted from the head deny (today: reads only; #2100 widens this to deliberately
/// granted writes), carved out of <see cref="DeniedShellCommandPatterns"/> — same
/// glob form, opposite sign, and only meaningful beside a deny it narrows. This is what lets a role
/// deny a whole command head (<c>baton *</c>, so a verb that does not exist yet is already refused)
/// while still admitting the reads it names (<c>baton status*</c>). Which of the two wins when both
/// match, and why an exception's spelling is matched more strictly than a deny's, is stated once on
/// <c>ShellCommandPatternMatcher.IsDeniedByTokenizedHead</c>. Enforced by the two <c>PreToolUse</c>
/// hooks and the codex policy through <c>EvaluateChainedCommand</c>; on claude, any deny an
/// exception narrows stays off the vendor flag and rests on the hook —
/// <c>ClaudeWorkerAdapter.StandingShellDenials</c> records that trade.
/// </param>
public sealed record PermissionGrant(
    bool ReadFiles = false,
    bool WriteFiles = false,
    bool RunShellCommands = false,
    IReadOnlyList<string>? ShellCommandPatterns = null,
    bool NetworkAccess = false,
    IReadOnlyList<string>? DeniedShellCommandPatterns = null,
    bool ShellCommandsAreReadOnly = false,
    IReadOnlyList<string>? DeniedShellOptionTokens = null,
    IReadOnlyList<string>? DeniedShellCommandExceptions = null)
{
    /// <summary>
    /// True when every category is unset — the structured equivalent of a blank
    /// <see cref="WorkerInvocation.PermissionScope"/> string, which callers collapse to
    /// <see langword="null"/> rather than persisting an explicit "nothing" record (see
    /// <c>WorkerBindingEntryViewModel.TryBuildEntry</c>'s decision of record).
    /// </summary>
    public bool IsEmpty => !ReadFiles && !WriteFiles && !RunShellCommands && !NetworkAccess
        && (ShellCommandPatterns is null || ShellCommandPatterns.Count == 0)
        && (DeniedShellCommandPatterns is null || DeniedShellCommandPatterns.Count == 0)
        && (DeniedShellOptionTokens is null || DeniedShellOptionTokens.Count == 0);

    /// <summary>
    /// The categories this grant WITHHOLDS that a granted shell reaches anyway — empty when the
    /// grant is coherent. #529.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A granted shell reaches three of the four categories, and both places AER enforces a grant
    /// decide by tool <em>name</em>: <c>ClaudeWorkerAdapter.BuildDisallowedTools</c> emits
    /// <c>--disallowedTools</c>, and the <c>PreToolUse</c> hook check inspects the tool name. Neither
    /// can tell <c>Bash("cat x")</c> from <c>Read("x")</c>. The hook additionally reads a write's
    /// target path (#649), which exempts the outbox and reaches nothing inside a shell command. So a
    /// grant withholding any of these while granting the shell does not actually withhold it.
    /// </para>
    /// <para>
    /// <see cref="ShellCommandPatterns"/> alone is deliberately <em>not</em> an exemption. A pattern
    /// list only reaches the <c>--allowedTools</c> string, and
    /// <c>gate.allowedtools-is-preapproval-not-ceiling</c> measured that list to be pre-approval
    /// rather than a ceiling for CROSS-tool substitution (a withheld category reached through a
    /// different, granted tool). <see cref="ShellCommandsAreReadOnly"/> is the explicit, named escape
    /// hatch for the narrower claim that actually holds — same-tool Bash pattern denial is a real,
    /// measured ceiling with deny-over-allow precedence; the measurement and its negative control are
    /// stated canonically in spec/baton.md §9 — so an author asserting the allowed patterns cannot
    /// write or mutate is not defeating a withheld category by the mechanism #529 measured. Without
    /// that assertion this stays exactly as conservative as before: a pattern list changes what is
    /// pre-approved, never (on its own) what is reachable.
    /// </para>
    /// <para>
    /// <b>This lives here, on the grant, because three surfaces need the same answer and #645 was
    /// filed about two of them getting it late or not at all.</b> The engine refuses at bind time
    /// (<c>WorkerBindingResolver</c>), which is the right choke point for execution and the wrong one
    /// for learning: an operator authoring in the bindings editor found out only when a workflow they
    /// had already committed to failed to start. A rule restated per surface is a rule that drifts on
    /// all but one of them, so every surface calls this and none re-derives the conditions.
    /// </para>
    /// </remarks>
    /// <param name="honorReadOnlyAssertion">
    /// Whether <see cref="ShellCommandsAreReadOnly"/> may exempt <see cref="WriteFiles"/>/
    /// <see cref="NetworkAccess"/> at all. Defaults to <see langword="true"/>, preserving #1456's
    /// behaviour for every caller that does not pass this explicitly.
    /// </param>
    /// <param name="strictCategories">
    /// #1784: names (via <c>nameof</c>) among <see cref="WriteFiles"/>/<see cref="NetworkAccess"/> for
    /// which the exemption is withheld regardless of <paramref name="honorReadOnlyAssertion"/>.
    /// <see cref="ProjectCeilingGate"/> is the one populating caller; spec/baton.md §9 is canonical for
    /// what it populates and why. <see langword="null"/> (every other caller) strictens nothing.
    /// </param>
    public IReadOnlyList<string> CategoriesDefeatedByTheShell(
        bool honorReadOnlyAssertion = true, IReadOnlySet<string>? strictCategories = null)
    {
        if (!RunShellCommands)
        {
            return [];
        }

        List<string> withheld = [];
        if (!ReadFiles)
        {
            withheld.Add(nameof(ReadFiles));
        }

        // The read-only assertion is a claim about a SPECIFIC, NAMED set of patterns — against
        // an unscoped shell (null/empty ShellCommandPatterns means "any command") it is
        // meaningless, so it is honored only alongside a populated pattern list (#1456
        // second-reader finding 1: without this guard, RunShellCommands + the bare flag
        // certified an unscoped shell as coherent and claude translated it to bare Bash).
        var readOnlyPatternedShell =
            honorReadOnlyAssertion && ShellCommandsAreReadOnly && ShellCommandPatterns is { Count: > 0 };

        // A read-only-asserted shell still performs reads (that is the whole reason it is useful),
        // so ReadFiles is never exempted — only WriteFiles/NetworkAccess, the two categories the
        // assertion actually claims the patterns cannot reach. #1784: strictCategories overrides this
        // exemption per name; see that parameter's own doc for what a caller puts in the set and why.
        if (!WriteFiles && !(readOnlyPatternedShell && !(strictCategories?.Contains(nameof(WriteFiles)) ?? false)))
        {
            withheld.Add(nameof(WriteFiles));
        }

        if (!NetworkAccess && !(readOnlyPatternedShell && !(strictCategories?.Contains(nameof(NetworkAccess)) ?? false)))
        {
            withheld.Add(nameof(NetworkAccess));
        }

        return withheld;
    }

    /// <summary>
    /// True when this grant can reach the network — categorically via <see cref="NetworkAccess"/>, or
    /// through a granted shell that <see cref="CategoriesDefeatedByTheShell(bool, IReadOnlySet{string})"/> does not list
    /// <see cref="NetworkAccess"/> as withheld from (the <see cref="ShellCommandsAreReadOnly"/> exemption
    /// above, or an unscoped shell, both reach it the same way a shell reaches every other ungranted
    /// category). Single source for the reachability question, derived from
    /// <see cref="CategoriesDefeatedByTheShell(bool, IReadOnlySet{string})"/> rather than re-deriving its conditions — a surface
    /// that instead re-checks <see cref="RunShellCommands"/>/<see cref="ShellCommandsAreReadOnly"/>/
    /// <see cref="ShellCommandPatterns"/> by hand drifts the moment that property's own condition changes
    /// (#1387's open follow-up), which is exactly what this property exists to prevent (#1500
    /// second-reader MED-1).
    /// </summary>
    public bool NetworkReachable =>
        NetworkAccess || (RunShellCommands && !CategoriesDefeatedByTheShell().Contains(nameof(NetworkAccess)));
}
