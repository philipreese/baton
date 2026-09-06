using Baton.Status;

namespace Baton.Vendors;

/// <summary>
/// Where a canonical skill package NAME becomes a package (operator ruling Q3 on #1151, 2026-09-01).
/// The precedence is stated here and nowhere else; spec/baton.md §9 cites this type rather than
/// restating the ladder.
/// </summary>
/// <remarks>
/// Resolution order, highest precedence first — <b>first match by name wins</b>, and a package found on
/// a higher rung is used whole rather than merged with a lower one:
/// <list type="number">
/// <item>the <c>BATON_SKILLS_PATH</c> environment override, when set — a one-off experiment. Folded into
///   <see cref="BatonEnvironmentSnapshot"/> (#1524) like every other baton-config variable: resolved
///   once per process, or per active <see cref="BatonEnvironmentSnapshot.BeginScope"/> in a test, never
///   re-read per access. The value IS the skills directory (packages sit directly under it), not a root
///   with a <c>skills/</c> inside;</item>
/// <item><c>{BatonPaths.Root}/skills/</c> — the operator's durable, rebuild-free, account-wide library.
///   This is what Q3 ratified for slice 1;</item>
/// <item><c>{AppContext.BaseDirectory}/skills/</c> — packages shipped next to the assembly. Nothing
///   ships there today; the rung exists so a future starter library has a home that needs no operator
///   setup;</item>
/// <item><c>{workspace}/skills/</c> — <b>the repo-local overlay, and it is the LOWEST rung.</b> That
///   inverts the prior a reader brings, so spec/baton.md §9 states the negative and the reason; the one
///   consequence for a caller here is that a package in the account library is never shadowed by
///   whatever sits in a checked-out repository.</item>
/// </list>
/// <para>
/// Mirrors <see cref="WorkerRoleCatalog"/>'s own three-rung shape rather than inventing a fourth
/// resolution idiom, with the workspace overlay appended below all three.
/// </para>
/// </remarks>
public static class SkillPackageResolver
{
    /// <summary>The environment override's name. Mirrored as <see cref="BatonEnvironmentSnapshot.SkillsPathOverride"/>.</summary>
    public const string SkillsPathEnvironmentVariable = "BATON_SKILLS_PATH";

    /// <summary>
    /// Every skills directory a name is searched in, in precedence order (highest first). Includes rungs
    /// that do not exist on disk: an error message that names only the directories that happen to exist
    /// tells the operator nothing about where to PUT a package.
    /// </summary>
    public static IReadOnlyList<string> Rungs(string? workspaceDirectory)
    {
        var rungs = new List<string>(4);

        if (BatonEnvironmentSnapshot.Current.SkillsPathOverride is { } envOverride && !string.IsNullOrWhiteSpace(envOverride))
        {
            rungs.Add(envOverride);
        }

        rungs.Add(Path.Combine(BatonPaths.Root, SkillPackageReader.SkillsDirectoryName));
        rungs.Add(Path.Combine(AppContext.BaseDirectory, SkillPackageReader.SkillsDirectoryName));

        if (!string.IsNullOrWhiteSpace(workspaceDirectory))
        {
            rungs.Add(Path.Combine(workspaceDirectory, SkillPackageReader.SkillsDirectoryName));
        }

        return rungs;
    }

    /// <summary>
    /// Resolves one package by name through <see cref="Rungs"/>.
    /// </summary>
    /// <exception cref="UnknownSkillPackageException">No rung holds a package with that name.</exception>
    /// <exception cref="SkillPackageFormatException">
    /// A rung holds a directory with that name that is not a valid package. Fail fast rather than fall
    /// through to a lower rung: an operator who authored a broken package meant that one, and silently
    /// resolving a same-named package from somewhere else is the substitution this whole feature exists
    /// to make impossible (#1151).
    /// </exception>
    public static SkillPackage Resolve(string name, string? workspaceDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var rungs = Rungs(workspaceDirectory);
        if (!SkillPackageReader.IsUsablePackageName(name))
        {
            throw new UnknownSkillPackageException(name, rungs);
        }

        foreach (var rung in rungs)
        {
            var candidate = Path.Combine(rung, name);
            if (!Directory.Exists(candidate))
            {
                continue;
            }

            return SkillPackageReader.LoadPackage(candidate);
        }

        throw new UnknownSkillPackageException(name, rungs);
    }

    /// <summary>
    /// Resolves every name in <paramref name="names"/>, preserving order and dropping exact duplicates.
    /// Null or empty resolves to an empty list — the case for every dispatch that names no skill.
    /// </summary>
    public static IReadOnlyList<SkillPackage> ResolveAll(IReadOnlyList<string>? names, string? workspaceDirectory)
    {
        if (names is not { Count: > 0 })
        {
            return Array.Empty<SkillPackage>();
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var packages = new List<SkillPackage>(names.Count);
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name) || !seen.Add(name))
            {
                continue;
            }

            packages.Add(Resolve(name, workspaceDirectory));
        }

        return packages;
    }
}
