using System.Text;
using Baton.Dispatch;

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
    /// <b>Every body goes in whole, and the two arms answer that cost differently (#2044 review
    /// MEDIUM).</b> A <i>scanned</i> package is uncapped, and what covers it is disclosure: the roster
    /// prints a per-package <c>(inlined, &lt;size&gt;)</c> measured on this same string
    /// (<c>AgyWorkerAdapter.DiscoverCapabilitiesAsync</c> is where that is built; docs/dispatch.md's
    /// realization table is the operator-facing register). A <i>declared</i> set gets no such line — the
    /// roster prints declared skills by name alone — so it is <b>bounded</b> instead: reaching
    /// <see cref="CoreDispatcher.OversizePromptThreshold"/> refuses with
    /// <see cref="SkillInliningOversizeException"/>, naming each package and its size. Fail closed, and
    /// deliberately that number rather than a second one: #748's threshold is this project's only
    /// existing statement of how long a prompt stops being ordinary. Reaching it is not itself a failure
    /// — <c>OversizePromptWrapper</c> swaps the inline prompt for a <c>BATON_PROMPT_FILE</c> reference far
    /// below the platform ceiling, so neither arm has an argv hazard — it is the one ceiling on record,
    /// borrowed here for a cost that is undisclosed rather than merely large. The predicate counts
    /// characters, the unit the threshold is in; the message renders bytes, the unit the roster prints. A
    /// per-package budget of its own belongs with the manifest #1151's slice 1 still owes, not here.
    /// </remarks>
    /// <param name="workingDirectory">
    /// The workspace scanned for <c>skills/&lt;name&gt;/</c> when no set is declared. A caller that
    /// realizes <b>declared skills only</b> passes null — codex does, for the reason
    /// <c>CodexWorkerAdapter.BuildPrompt</c>'s <c>declaredSkills</c> parameter states canonically.
    /// </param>
    /// <param name="declaredSkills">
    /// #1151: the binding's own declared skill set — <see cref="WorkerInvocation.Skills"/> is the
    /// register for what it is and why it replaces rather than augments the scan. Null or empty keeps
    /// #1929's discovery behaviour, including the working-directory precondition below, which a declared
    /// set does not need: those packages may come from the account-wide library, and inlining writes
    /// nothing anywhere.
    /// </param>
    /// <exception cref="SkillInliningOversizeException">
    /// The declared set alone reaches the bound above. A scan cannot raise this: see the remark.
    /// </exception>
    public static string InlineSkills(
        string prompt, string? workingDirectory, IReadOnlyList<SkillPackage>? declaredSkills = null)
    {
        IReadOnlyList<SkillPackage> packages;
        if (declaredSkills is { Count: > 0 })
        {
            packages = declaredSkills;
            RefuseOversizeDeclaredSet(packages);
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

    /// <summary>
    /// The declared arm's bound, stated in the remark above and enforced here only.
    /// </summary>
    private static void RefuseOversizeDeclaredSet(IReadOnlyList<SkillPackage> declaredSkills)
    {
        var bodies = declaredSkills.Select(InlinedSkillBody).ToList();
        var inlinedLength = bodies.Sum(body => body.Length);
        if (inlinedLength < CoreDispatcher.OversizePromptThreshold)
        {
            return;
        }

        throw new SkillInliningOversizeException(
            declaredSkills.Select(package => package.Name).ToList(),
            declaredSkills
                .Select((package, i) => $"'{package.Name}' ({DescribeSize(Encoding.UTF8.GetByteCount(bodies[i]))})")
                .ToList(),
            inlinedLength);
    }

    /// <summary>
    /// A byte count rendered for an operator — whole bytes below 1 KiB, one decimal above, so a short
    /// skill does not read as <c>0.0 KB</c>. One renderer, because the roster's <c>(inlined, &lt;size&gt;)</c>
    /// suffix and the oversize refusal above state the same quantity to the same reader.
    /// </summary>
    internal static string DescribeSize(int byteCount) =>
        byteCount < 1024
            ? $"{byteCount} B"
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{byteCount / 1024.0:0.0} KB");
}
