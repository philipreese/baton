using System.Text;

namespace Baton.Vendors;

/// <summary>
/// The floor realization of a canonical skill package for every adapter that has no native one: the
/// package's instructions appended to the dispatch prompt (#1151, spec/baton.md §9). <b>One
/// implementation, and this remark is where the format is described</b> — agy has realized skills this
/// way since #1929 and codex since #2044, and a second copy of the header/strip/order rules is how the
/// two would drift into producing different prompts from the same package (record-once).
/// </summary>
public static class SkillInlining
{
    /// <summary>
    /// The exact text one canonical skill package contributes to a dispatch prompt: the
    /// <c>SKILL.md</c> body with its YAML front matter stripped (#1151, #1929 review LOW).
    /// </summary>
    /// <remarks>
    /// One function so the roster's per-package size and the prompt's actual content are measured on the
    /// same string — a size computed off the raw file would over-report by the front matter this drops.
    /// </remarks>
    public static string InlinedSkillBody(SkillPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return SkillScanner.StripFrontmatter(package.Content).Trim();
    }

    /// <summary>
    /// Inlines canonical skill packages (<c>skills/&lt;name&gt;/SKILL.md</c>) directly into the
    /// dispatch prompt with a one-line header per skill (<c># Skill: &lt;name&gt;</c>), in declaration
    /// (or discovery) order (#1151). Returns the prompt unchanged if no canonical skill packages exist.
    /// </summary>
    /// <remarks>
    /// <b>Uncapped, and the roster is what discloses it.</b> Every package's body goes in whole, so the
    /// worker's context budget is spent in proportion to what the repository carries, and one non-trivial
    /// package pushes the dispatch onto #748's oversize-prompt path. There is no argv hazard there
    /// (<c>OversizePromptWrapper</c> swaps the inline prompt for a <c>BATON_PROMPT_FILE</c> reference far
    /// below the platform ceiling), so the cost is context rather than failure — which is why the agy
    /// adapter discloses a per-package size in the dispatch roster instead of silently truncating. A cap
    /// belongs with the manifest #1151's slice 1 still owes, not here.
    /// </remarks>
    /// <param name="workingDirectory">
    /// The workspace scanned for <c>skills/&lt;name&gt;/</c> when no set is declared. A caller that
    /// realizes <b>declared skills only</b> passes null — codex does, because its roster reports no
    /// <c>skill</c> items, and inlining what a scan found while the roster says "none discovered" is the
    /// silent context cost the per-package size line exists to prevent.
    /// </param>
    /// <param name="declaredSkills">
    /// #1151: the binding's own declared skill set — <see cref="WorkerInvocation.Skills"/> is the
    /// register for what it is and why it replaces rather than augments the scan. Null or empty keeps
    /// #1929's discovery behaviour, including the working-directory precondition below, which a declared
    /// set does not need: those packages may come from the account-wide library, and inlining writes
    /// nothing anywhere.
    /// </param>
    public static string InlineSkills(
        string prompt, string? workingDirectory, IReadOnlyList<SkillPackage>? declaredSkills = null)
    {
        IReadOnlyList<SkillPackage> packages;
        if (declaredSkills is { Count: > 0 })
        {
            packages = declaredSkills;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
            {
                return prompt;
            }

            packages = SkillPackageReader.DiscoverPackages(workingDirectory);
        }

        if (packages.Count == 0)
        {
            return prompt;
        }

        var sb = new StringBuilder(prompt);
        foreach (var package in packages)
        {
            sb.Append($"\n\n# Skill: {package.Name}\n{InlinedSkillBody(package)}");
        }
        return sb.ToString();
    }
}
