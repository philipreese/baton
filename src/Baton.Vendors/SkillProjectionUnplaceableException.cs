using Baton;

namespace Baton.Vendors;

/// <summary>
/// A binding declares canonical skill packages that claude's floor realization has nowhere to place
/// (#1941 review HIGH, #1151, spec/baton.md §9). The claude realization is a <b>projection</b> —
/// package files copied into <c>&lt;workingDirectory&gt;/.claude/skills/</c> — so with no usable working
/// directory there is no destination, and the only two honest answers are a refusal or a silent drop.
/// <b>It refuses</b>: a silently dropped skill is the exact failure class #1512 exists to remove, and
/// spec/baton.md §9's ruling is that this surface is realized rather than recorded-and-ignored.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope: claude only, and every caller of <c>Resolve</c>.</b> agy's realization inlines the package
/// body into the prompt and writes nothing, so a declared set genuinely does not need a working
/// directory there (<c>AgyWorkerAdapter.InlineSkills</c> states that reasoning). Because
/// <c>ClaudeWorkerAdapter.Resolve</c> is reached from <c>baton decide</c>/<c>run</c>/<c>resume</c> for
/// bindings that may never dispatch, this refusal fires on those verbs too — which is the point: a
/// binding whose skills cannot be placed is broken whether or not anyone dispatches it today.
/// </para>
/// <para>
/// An EMPTY declared set is untouched: with nothing declared, no working directory simply means no
/// workspace scan, which is #1929's behaviour and refuses nothing.
/// </para>
/// </remarks>
public sealed class SkillProjectionUnplaceableException : BatonFlowException
{
    /// <summary>The declared package names that had nowhere to go, in the order the binding declared them.</summary>
    public IReadOnlyList<string> SkillNames { get; }

    public SkillProjectionUnplaceableException(IReadOnlyList<string> skillNames, string? workingDirectory)
        : base($"The binding declares skill(s) {string.Join(", ", skillNames.Select(name => $"'{name}'"))}, "
               + "which the claude realization projects into '<working directory>/.claude/skills/' — but "
               + (string.IsNullOrWhiteSpace(workingDirectory)
                   ? "the binding sets no working directory, so there is nowhere to place them."
                   : $"its working directory '{workingDirectory}' does not exist, so there is nowhere to place them."))
    {
        SkillNames = skillNames;
        TryInvocation = string.IsNullOrWhiteSpace(workingDirectory)
            ? "set the binding's WorkingDirectory to the directory the worker runs in, or drop its Skills."
            : $"create '{workingDirectory}' before dispatching, point the binding's WorkingDirectory at the "
              + "directory the worker runs in, or drop its Skills.";
    }
}
