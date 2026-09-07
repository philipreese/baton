using Baton;
using Baton.Dispatch;

namespace Baton.Vendors;

/// <summary>
/// A binding's <b>declared</b> canonical skill packages would together inline a prompt reaching #748's
/// <see cref="CoreDispatcher.OversizePromptThreshold"/> (#2044 review MEDIUM). The bound, why it is that
/// number, and why only the declared arm carries one are stated once on
/// <see cref="SkillInlining.InlineSkills"/> — this type is the refusal, not a second copy of the rule.
/// </summary>
/// <remarks>
/// <b>Refuses rather than truncates.</b> A truncated skill is a skill whose last instruction silently
/// went missing, which is the failure class #1512 exists to remove; a refusal names every package and
/// its size, so the operator picks what to drop.
/// </remarks>
public sealed class SkillInliningOversizeException : BatonFlowException
{
    /// <summary>The declared package names, in the order the binding declared them.</summary>
    public IReadOnlyList<string> SkillNames { get; }

    /// <summary>The inlined length of the whole declared set, in characters — the unit the threshold is in.</summary>
    public int InlinedLength { get; }

    /// <param name="sizedNames">
    /// Each declared package as <c>'name' (size)</c>, sizes rendered in bytes to match the roster's own
    /// <c>(inlined, &lt;size&gt;)</c> suffix, while <paramref name="inlinedLength"/> stays in characters
    /// because that is what the threshold measures.
    /// </param>
    public SkillInliningOversizeException(
        IReadOnlyList<string> skillNames, IReadOnlyList<string> sizedNames, int inlinedLength)
        : base($"The binding declares skill(s) {string.Join(", ", sizedNames)}, which the agy and codex "
               + $"realizations inline into the dispatch prompt whole — {inlinedLength} characters together, "
               + $"at or over the {CoreDispatcher.OversizePromptThreshold}-character point this project stops "
               + "treating a prompt as ordinary (#748). The dispatch roster prints declared skills by name "
               + "with no size, so nothing would disclose that cost to you.")
    {
        SkillNames = skillNames;
        InlinedLength = inlinedLength;
        TryInvocation = "drop the largest package from the binding's Skills, or shorten its SKILL.md, so the "
            + $"declared set inlines under {CoreDispatcher.OversizePromptThreshold} characters.";
    }
}
