using Baton.Vendors;

namespace Baton.Cli.Tests;

[Collection(SerializedEnvironmentCollection.Name)]
public class DispatchCapabilitiesTests
{
    [Fact]
    public void Capabilities_text_contains_adapters_models_and_efforts()
    {
        var text = DispatchCapabilitiesPrinter.BuildText();

        // Adapters
        Assert.Contains("claude:", text);
        Assert.Contains("agy:", text);
        Assert.Contains("codex:", text);

        // Claude model aliases and full ID example — the exact joined line, not a per-alias loose
        // Contains: "opus" alone is a substring of the hardcoded "claude-opus-4-8" example. Note what
        // this exact-line check catches and what it doesn't (#1500 second-reader LOW-3): the expected
        // string is itself computed from ClaudeWorkerAdapter.ModelAliases, the same static the printer
        // reads, so it cannot catch that list going stale — printer and expectation go stale together.
        // What it does catch: a printer hardcoding a list instead of reading the live one, a format
        // regression, or an alias reordering a loose per-value check would miss.
        Assert.Contains(
            $"Models:     {string.Join(", ", ClaudeWorkerAdapter.ModelAliases)} (aliases), or full ID (e.g. claude-opus-4-8)",
            text);

        // Claude raw efforts — the exact joined line. Per-value loose checks are vacuous here too:
        // every one of "low"/"medium"/"high" also appears in the agy section regardless of what
        // Claude's own list contains.
        Assert.Contains($"Raw Effort: {string.Join(", ", EffortTierMapping.ClaudeRawValues)}", text);
        Assert.Contains($"Raw Effort: {string.Join(", ", EffortTierMapping.AgyRawValues)}", text);
        Assert.Contains($"Raw Effort: {string.Join(", ", EffortTierMapping.CodexRawValues)}", text);

        // Canonical efforts, both vendors — exact "word (-> vendor-value)" pairs.
        foreach (var word in EffortTierMapping.CanonicalWords)
        {
            Assert.Contains($"{word} (-> {EffortTierMapping.ClaudeByCanonical[word]})", text);
            Assert.Contains($"{word} (-> {EffortTierMapping.AgyByCanonical[word]})", text);
            Assert.Contains($"{word} (-> {EffortTierMapping.CodexByCanonical[word]})", text);
        }

        // agy models (illustrative only — agy has no alias catalog to source from)
        // #1863: docs/dispatch.md's tier paragraph records which family is current and why.
        Assert.Contains("gemini-3.8-flash-high", text);
        // Deliberately over the WHOLE printed block, not just the line above: the Role Timebox
        // Defaults section below prints each role's resolved tier model too, so these two arms pin
        // both halves at once -- no retired name may reach an operator's screen as an illustration
        // OR as a live tier pin.
        Assert.DoesNotContain("gemini-3.6-flash", text);
        Assert.DoesNotContain("gemini-3.1-pro", text);

        // Role timebox defaults — the exact formatted line per role, so two roles sharing a timebox
        // (25m) can't let one role's missing/mismatched line hide behind another's.
        foreach (var role in WorkerRoleCatalog.All)
        {
            var timebox = $"{(int)role.Timeout.TotalMinutes}m";
            var modelPart = role.Model is not null ? $", model: {role.Model}" : "";
            var effortPart = role.Effort is not null ? $", effort: {role.Effort}" : "";
            var subagentsPart = $", subagents: {(role.AllowsSubagents ? "allowed" : "withheld")}";
            Assert.Contains(
                $"{role.Id,-12} {timebox,4}  (tier: {role.Tier}, adapter: {role.Adapter}{modelPart}{effortPart}{subagentsPart})",
                text);
        }
    }

    /// <summary>
    /// #1875: the Codex note claimed "No built-in role currently has a grant Codex can express
    /// exactly; all fail closed." #1871 made that false (see `DispatchCapabilitiesPrinter`) and no test
    /// noticed, because nothing tied the sentence to the adapter. It is derived now, and this pins the
    /// derived half against the shipped translator.
    /// </summary>
    [Fact]
    public void Codex_note_names_the_built_in_roles_whose_grants_the_shipped_translator_accepts()
    {
        var text = DispatchCapabilitiesPrinter.BuildText();

        Assert.DoesNotContain("No built-in role", text);
        // The exact list, not a loose "patch" substring: a role id that merely contains another's name
        // would satisfy the loose form, and the sentence's job is to name every role that translates.
        var expressible = WorkerRoleCatalog.All.Select(r => r.Id);
        Assert.Contains(
            $"Codex expresses these built-in roles' grants exactly: {string.Join(", ", expressible)}.",
            RoleSentence(text));
        Assert.Contains("patch", WorkerRoleCatalog.All.Select(r => r.Id));
    }

    /// <summary>
    /// The control arm: with a translator that refuses, the same code must print the fail-closed half
    /// instead. Without it the assertion above passes for a printer that hardcodes the new sentence
    /// just as it did the old one — the shipped translator accepts every grant today, so the accepting
    /// branch alone cannot discriminate.
    /// </summary>
    [Fact]
    public void Codex_note_reports_every_role_as_fail_closed_when_the_translator_refuses()
    {
        var text = DispatchCapabilitiesPrinter.BuildText(new RefusingTranslator());

        Assert.Contains("No built-in role has a grant Codex can express exactly; all fail closed.", text);
        Assert.DoesNotContain("Codex expresses these built-in roles' grants exactly:", text);
    }

    /// <summary>
    /// The partition itself, on a translator that accepts some grants and refuses others: each role
    /// has to land on the side its own grant puts it on, so a printer that lists every role on one
    /// side (as the old sentence did) fails here.
    /// </summary>
    [Fact]
    public void Codex_note_separates_the_roles_that_translate_from_the_roles_that_fail_closed()
    {
        var text = DispatchCapabilitiesPrinter.BuildText(new WriteGrantOnlyTranslator());

        var sentence = RoleSentence(text);
        var split = sentence.IndexOf("Fails closed for:", StringComparison.Ordinal);
        Assert.True(split > 0, sentence);
        var accepted = sentence[..split];
        var refused = sentence[split..];

        var writers = WorkerRoleCatalog.All.Where(r => r.Grant.WriteFiles).Select(r => r.Id).ToList();
        var others = WorkerRoleCatalog.All.Where(r => !r.Grant.WriteFiles).Select(r => r.Id).ToList();
        Assert.NotEmpty(writers);
        Assert.NotEmpty(others);
        Assert.Contains($"Codex expresses these built-in roles' grants exactly: {string.Join(", ", writers)}.", accepted);
        Assert.Contains($"Fails closed for: {string.Join(", ", others)}.", refused);
    }

    private static string RoleSentence(string text) =>
        text.Split('\n').Single(line => line.Contains("Efforts are model-specific.", StringComparison.Ordinal));

    private sealed class RefusingTranslator : IPermissionGrantTranslator
    {
        public bool TryTranslatePermissionGrant(PermissionGrant grant, out string? resolvedValue, out string? gapReason)
        {
            resolvedValue = null;
            gapReason = "synthetic refusal";
            return false;
        }
    }

    private sealed class WriteGrantOnlyTranslator : IPermissionGrantTranslator
    {
        public bool TryTranslatePermissionGrant(PermissionGrant grant, out string? resolvedValue, out string? gapReason)
        {
            if (grant.WriteFiles)
            {
                resolvedValue = "baton-broker";
                gapReason = null;
                return true;
            }

            resolvedValue = null;
            gapReason = "synthetic refusal";
            return false;
        }
    }

    [Fact]
    public void Print_writes_capabilities_to_writer()
    {
        using var sw = new StringWriter();
        DispatchCapabilitiesPrinter.Print(sw);

        var output = sw.ToString();
        Assert.NotEmpty(output);
        Assert.Contains("Adapters, Models & Efforts:", output);
        Assert.Contains("Role Timebox Defaults:", output);
    }
}
