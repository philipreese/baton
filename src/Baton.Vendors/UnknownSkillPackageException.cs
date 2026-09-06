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
        : base($"No canonical skill package named '{skillName}'.")
    {
        SkillName = skillName;
        TryInvocation = rungsSearched.Count == 0
            ? null
            : $"create '{Path.Combine(rungsSearched[0], skillName)}' holding a SKILL.md (searched, in precedence order: "
              + $"{string.Join(", ", rungsSearched)}).";
    }
}
