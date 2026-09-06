using Baton;

namespace Baton.Vendors;

/// <summary>
/// A canonical skill package's declared <c>requires</c> names a capability the worker's resolved grant
/// withholds (#1151, decision 0033, spec/baton.md §9). <b>The grant is never widened to satisfy a
/// skill</b> — attaching one can only refuse, which is the property that lets an operator attach a
/// package without re-reading what it might quietly turn on.
/// </summary>
/// <remarks>
/// Raised from <c>RoleDispatch.ToBinding</c> — before a room directory exists — and again from
/// <see cref="WorkerBindingResolver"/>, the seam <c>baton run</c> also crosses, so a hand-authored
/// <c>bindings.json</c> naming skills is checked too rather than only the verb that wrote them.
/// </remarks>
public sealed class SkillRequirementUnsatisfiedException : BatonFlowException
{
    public string SkillName { get; }

    /// <summary>The <see cref="PermissionGrant"/> members the package requires and the grant withholds.</summary>
    public IReadOnlyList<string> MissingCategories { get; }

    public SkillRequirementUnsatisfiedException(string workerName, string skillName, IReadOnlyList<string> missingCategories)
        : base($"Skill '{skillName}' requires {string.Join(", ", missingCategories)}, which worker "
               + $"'{workerName}''s grant withholds. Attaching a skill never widens a grant.")
    {
        SkillName = skillName;
        MissingCategories = missingCategories;
        TryInvocation =
            $"dispatch a role whose grant already carries {string.Join(" and ", missingCategories)}, or drop --skill {skillName}.";
    }
}
