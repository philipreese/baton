using System.Text;
using Baton.Vendors;

namespace Baton.Cli;

/// <summary>
/// Formats and prints supported adapters, models, efforts, and role defaults (issue #1500).
/// <see cref="WorkerRoleCatalog.All"/> is the same catalog <c>ModelAndEffortValidationTests</c>
/// reads directly. The role and effort sections cannot drift from what dispatch actually accepts, but
/// that is single-source construction, not test coverage: this printer and
/// the vendor adapters all read the same
/// <see cref="EffortTierMapping"/> statics, so they cannot disagree regardless of what any test
/// exercises. <c>ModelAndEffortValidationTests</c> only exercises <c>AgyRawValues</c> end to end —
/// it never hands Claude an <c>--effort</c> at all. Codex has separate registration and adapter
/// coverage for its raw and canonical values; neither suite validates <c>ClaudeRawValues</c> or
/// Claude's canonical table (#1500 second-reader MED-3).
/// <see cref="ClaudeWorkerAdapter.ModelAliases"/> is read live too, but that list has no validation
/// surface of its own (every alias always resolves to a vendor-current model, so nothing rejects one)
/// and is not exercised by that suite either. agy has no equivalent model-alias catalog (its models
/// are suffix-parametrized, not enumerated), so its printed model examples are illustrative text, not
/// a sourced table. Codex's printed line deliberately points to dynamic app-server discovery instead
/// of freezing the account-sensitive model list into this display.
/// </summary>
public static class DispatchCapabilitiesPrinter
{
    public static string BuildText() => BuildText(new CodexWorkerAdapter());

    /// <summary>
    /// The same text, with Codex's grant translator injected so a test can drive both branches of the
    /// printed sentence. The shipped translator accepts every built-in grant today; the refusing
    /// branch is not dead text, it is what the sentence must say the day an adapter's translator
    /// starts refusing one again (#1875).
    /// </summary>
    internal static string BuildText(IPermissionGrantTranslator translator)
    {
        ArgumentNullException.ThrowIfNull(translator);
        var sb = new StringBuilder();
        sb.AppendLine("Adapters, Models & Efforts:");

        // Claude
        sb.AppendLine("  claude:");
        sb.AppendLine($"    Models:     {string.Join(", ", ClaudeWorkerAdapter.ModelAliases)} (aliases), or full ID (e.g. claude-opus-4-8)");
        var claudeCanonical = string.Join(
            ", ", EffortTierMapping.CanonicalWords.Select(w => $"{w} (-> {EffortTierMapping.ClaudeByCanonical[w]})"));
        sb.AppendLine($"    Canonical:  {claudeCanonical}");
        sb.AppendLine($"    Raw Effort: {string.Join(", ", EffortTierMapping.ClaudeRawValues)}");

        // Agy
        sb.AppendLine("  agy:");
        sb.AppendLine("    Models:     gemini-3.8-flash-high, gemini-3.8-flash-medium, gemini-3.8-flash-low, etc.");
        var agyCanonical = string.Join(
            ", ", EffortTierMapping.CanonicalWords.Select(w => $"{w} (-> {EffortTierMapping.AgyByCanonical[w]})"));
        sb.AppendLine($"    Canonical:  {agyCanonical}");
        sb.AppendLine($"    Raw Effort: {string.Join(", ", EffortTierMapping.AgyRawValues)}");
        sb.AppendLine("    Note:       On agy, model suffix (-low, -medium, -high) and --effort must agree.");

        // Codex
        sb.AppendLine("  codex:");
        sb.AppendLine("    Models:     discovered dynamically from codex app-server model/list");
        var codexCanonical = string.Join(
            ", ", EffortTierMapping.CanonicalWords.Select(w => $"{w} (-> {EffortTierMapping.CodexByCanonical[w]})"));
        sb.AppendLine($"    Canonical:  {codexCanonical}");
        sb.AppendLine($"    Raw Effort: {string.Join(", ", EffortTierMapping.CodexRawValues)}");
        sb.AppendLine($"    Note:       Efforts are model-specific. {CodexGrantSentence(translator)}");

        sb.AppendLine();
        sb.AppendLine("Role Timebox Defaults:");
        foreach (var role in WorkerRoleCatalog.All)
        {
            var timebox = $"{(int)role.Timeout.TotalMinutes}m";
            var modelPart = role.Model is not null ? $", model: {role.Model}" : "";
            var effortPart = role.Effort is not null ? $", effort: {role.Effort}" : "";
            // #1802: surface the subagent-withholding default per role -- the printer's whole job is
            // to make what dispatch actually does visible without reading the catalog source.
            var subagentsPart = $", subagents: {(role.AllowsSubagents ? "allowed" : "withheld")}";
            sb.AppendLine($"  {role.Id,-12} {timebox,4}  (tier: {role.Tier}, adapter: {role.Adapter}{modelPart}{effortPart}{subagentsPart})");
        }

        sb.AppendLine();
        sb.AppendLine("Pre-turn verify step (--verify-cmd, review role only):");
        sb.AppendLine("  Repeatable. The ENGINE runs each command before the reviewer's first turn, with no model");
        sb.AppendLine("  involved, in the review workspace and wrapped in `python tools/buildlock.py`, then writes");
        sb.AppendLine($"  {Baton.Mutation.VerifyStepReport.ResultsFileName} into the room's artifacts and copies the commands");
        sb.AppendLine("  onto verdict.json's `instruments`. A non-zero exit does not abort the review.");
        sb.AppendLine("  Allowed shapes: dotnet build ... | dotnet test ... | python <script under tools/ or");
        sb.AppendLine("                  benchmarks/> --check.../--selftest...");
        sb.AppendLine($"  Bound:          --verify-timeout <minutes> per command (default {(int)Baton.Mutation.VerifyStepRunner.DefaultTimeout.TotalMinutes}).");
        sb.AppendLine("  NOT --verify:   --verify overrides the POST-exit verify command (a role's verify_pixi_task,");
        sb.AppendLine("                  e.g. implement's) that decides whether a mutating execution settles.");
        sb.AppendLine("                  --verify-cmd runs BEFORE the worker and decides nothing.");

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Asks <paramref name="translator"/> about every built-in role's own grant and reports which
    /// roles Codex can express exactly. Derived rather than written down: the previous sentence said
    /// no built-in role's grant could be expressed and all failed closed, which #1871 made false —
    /// the broker translator accepts the canonical grant, and the built-in <c>patch</c> role reached a
    /// real model turn through it — and nothing in the printer noticed (#1875).
    /// </summary>
    private static string CodexGrantSentence(IPermissionGrantTranslator translator)
    {
        List<string> expressible = [];
        List<string> failClosed = [];
        foreach (var role in WorkerRoleCatalog.All)
        {
            var accepted = translator.TryTranslatePermissionGrant(role.Grant, out _, out _);
            (accepted ? expressible : failClosed).Add(role.Id);
        }

        if (expressible.Count == 0)
        {
            return "No built-in role has a grant Codex can express exactly; all fail closed.";
        }

        var sentence = $"Codex expresses these built-in roles' grants exactly: {string.Join(", ", expressible)}.";
        return failClosed.Count == 0
            ? sentence
            : $"{sentence} Fails closed for: {string.Join(", ", failClosed)}.";
    }

    public static void Print(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteLine(BuildText());
    }
}
