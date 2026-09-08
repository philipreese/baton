using Baton;

namespace Baton.Vendors;

/// <summary>
/// A named canonical skill package resolves in no rung (#1151, spec/baton.md §9). Mirrors
/// <see cref="UnknownWorkerAdapterException"/>: the message names the thing that was not found, and the
/// remedy names <b>every rung that was searched</b> — including rungs that do not exist on disk, which
/// is the half an operator actually needs, because the answer to "where do I put it?" is a directory
/// that is currently absent.
/// </summary>
/// <remarks>
/// Thrown before a room directory is created (<c>RoleDispatch.ToBinding</c>, ahead of
/// <c>DispatchCommand</c>'s own <c>Directory.CreateDirectory</c>), so a typo in <c>--skill</c> costs
/// nothing and leaves nothing behind. #1151's rule: fail fast on identity. Scoped to the account-wide
/// rungs — spec/baton.md §9 records why the bottom rung can instead refuse from
/// <see cref="WorkerBindingResolver"/>, after the room exists.
/// </remarks>
public sealed class UnknownSkillPackageException : BatonFlowException
{
    public string SkillName { get; }

    public UnknownSkillPackageException(string skillName, IReadOnlyList<string> rungsSearched)
        : this(skillName, rungsSearched, declaredByRole: null)
    {
    }

    /// <summary>
    /// #2110: the same refusal for a name that came from a ROLE's <c>default_skills</c> rather than
    /// from <c>--skill</c>. The message names the role: the command line carried no such name, so the
    /// remedy is the catalog entry or the missing package, and the opt-out is offered as the third.
    /// </summary>
    public UnknownSkillPackageException(string skillName, IReadOnlyList<string> rungsSearched, string? declaredByRole)
        : base(declaredByRole is null
            ? $"No canonical skill package named '{skillName}'."
            : $"No canonical skill package named '{skillName}', which worker role '{declaredByRole}' declares as a default skill.")
    {
        SkillName = skillName;
        DeclaredByRole = declaredByRole;
        TryInvocation = rungsSearched.Count == 0
            ? null
            : $"create '{Path.Combine(rungsSearched[0], skillName)}' holding a SKILL.md (searched, in precedence order: "
              + $"{string.Join(", ", rungsSearched)})"
              + (declaredByRole is null
                  ? "."
                  : ", or pass --no-default-skills to dispatch the role without its defaults.");
    }

    /// <summary>The role whose <c>default_skills</c> named the package, or null when <c>--skill</c> did.</summary>
    public string? DeclaredByRole { get; }
}
