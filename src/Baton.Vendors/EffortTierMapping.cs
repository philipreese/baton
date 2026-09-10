namespace Baton.Vendors;

/// <summary>
/// 0023's canonical effort vocabulary (quick/standard/careful/exhaustive) translated to each
/// vendor's raw <c>--effort</c> value at dispatch (decision 0058's #1318 scope ruling 4). See
/// <see cref="WorkerInvocation.Effort"/> for what this field now carries and why; this is the only
/// place that resolves it to a vendor flag value (0023 constraint 1, docs/agents/developing-baton.md Architecture Rule 2).
/// Nothing outside this assembly sees a raw vendor effort string; the
/// mapping itself lives only here and is stated once, in <c>docs/vendor-capabilities.md</c>'s "The
/// canonical effort mapping" table — restated in this file's data, never in prose elsewhere.
/// </summary>
/// <remarks>
/// <b>Fail closed, not fail-forward.</b> <c>claude</c> silently ignores an unknown <c>--effort</c>
/// and runs at its default with exit 0 (measured, <c>docs/vendor-capabilities.md</c>) — so forwarding
/// a canonical word unresolved, or any other string neither vendor recognizes, would run silently at
/// the wrong effort with no signal at all. <see cref="ResolveForClaude"/>/<see cref="ResolveForAgy"/>
/// therefore never forward a string blind: a canonical word is translated, a value already in that
/// vendor's own raw set (the <c>#566</c> escape hatch) passes through untouched, and anything else is
/// refused before dispatch with <see cref="IncoherentVendorEffortException"/> — loud, before the
/// operator has waited for a run that could never have honoured the value requested.
/// </remarks>
public static class EffortTierMapping
{
    public const string Quick = "quick";
    public const string Standard = "standard";
    public const string Careful = "careful";
    public const string Exhaustive = "exhaustive";

    /// <summary>Every canonical effort word, in the order 0023 names them.</summary>
    public static readonly IReadOnlyList<string> CanonicalWords = [Quick, Standard, Careful, Exhaustive];

    public static readonly IReadOnlyDictionary<string, string> ClaudeByCanonical =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Quick] = "low",
            [Standard] = "medium",
            [Careful] = "high",
            [Exhaustive] = "max",
        };

    /// <summary>
    /// agy has no fourth level: <c>careful</c> and <c>exhaustive</c> both resolve to <c>high</c>, a
    /// disclosed collapse per 0023 constraint 2 — see <c>docs/vendor-capabilities.md</c>'s own table
    /// note. The collapse is stated here, once; nothing else re-derives it.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> AgyByCanonical =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Quick] = "low",
            [Standard] = "medium",
            [Careful] = "high",
            [Exhaustive] = "high",
        };

    public static readonly IReadOnlyDictionary<string, string> CodexByCanonical =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Quick] = "low",
            [Standard] = "medium",
            [Careful] = "high",
            [Exhaustive] = "max",
        };

    public static readonly IReadOnlySet<string> ClaudeRawValues =
        new HashSet<string>(StringComparer.Ordinal) { "low", "medium", "high", "xhigh", "max" };

    public static readonly IReadOnlySet<string> AgyRawValues =
        new HashSet<string>(StringComparer.Ordinal) { "low", "medium", "high" };

    public static readonly IReadOnlySet<string> CodexRawValues =
        new HashSet<string>(StringComparer.Ordinal) { "low", "medium", "high", "xhigh", "max", "ultra" };

    /// <summary>True for exactly the four words 0023 names — never a vendor's own raw value.</summary>
    public static bool IsCanonical(string effort) => ClaudeByCanonical.ContainsKey(effort);

    /// <summary>
    /// The value to hand claude's <c>--effort</c>: a canonical word translated, one of claude's own
    /// raw values (<c>low</c>/<c>medium</c>/<c>high</c>/<c>xhigh</c>/<c>max</c>) passed through
    /// untouched as the <c>#566</c> escape hatch, or a thrown <see cref="IncoherentVendorEffortException"/>
    /// for anything else.
    /// </summary>
    public static string ResolveForClaude(string effort) => Resolve("claude", effort, ClaudeByCanonical, ClaudeRawValues);

    /// <summary>
    /// The value to hand agy's <c>--effort</c>: a canonical word translated (with <c>careful</c> and
    /// <c>exhaustive</c> both landing on <c>high</c>), one of agy's own raw values passed through
    /// untouched, or a thrown <see cref="IncoherentVendorEffortException"/> for anything else. Callers
    /// still run the resolved value through the existing model-suffix reconciliation
    /// (<c>AgyWorkerAdapter.ReconcileAgyEffort</c>) — this method only resolves the vocabulary.
    /// </summary>
    public static string ResolveForAgy(string effort) => Resolve("agy", effort, AgyByCanonical, AgyRawValues);

    /// <summary>
    /// Resolves Baton's canonical vocabulary to Codex CLI reasoning effort. The app-server model
    /// catalog is the source for per-model availability; this method validates the stable vocabulary,
    /// while <see cref="CodexWorkerAdapter"/> rejects known model/effort mismatches and delegation-only
    /// <c>ultra</c> when the role withholds subagents.
    /// </summary>
    public static string ResolveForCodex(string effort) => Resolve("codex", effort, CodexByCanonical, CodexRawValues);

    private static string Resolve(
        string adapterName,
        string effort,
        IReadOnlyDictionary<string, string> byCanonical,
        IReadOnlySet<string> rawValues)
    {
        if (byCanonical.TryGetValue(effort, out var raw))
        {
            return raw;
        }

        if (rawValues.Contains(effort))
        {
            return effort;
        }

        throw new IncoherentVendorEffortException(
            adapterName,
            $"'{effort}' is neither a canonical effort word ({string.Join(", ", CanonicalWords)}) nor " +
            $"one of {adapterName}'s own raw effort values. Forwarding it unresolved risks the vendor " +
            "silently running at a different effort than requested rather than failing.");
    }
}
