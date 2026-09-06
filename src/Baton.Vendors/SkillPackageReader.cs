namespace Baton.Vendors;

/// <summary>
/// Reader for canonical skill packages — <c>skills/&lt;name&gt;/SKILL.md</c> under the working directory a
/// binding dispatches into. Extracts metadata and instructions without a YAML parser dependency, shared
/// across worker adapters (#1151).
/// </summary>
/// <remarks>
/// <b>This is the deferred repo-local overlay, not the ratified resolver.</b> Operator ruling Q3 on #1151
/// (2026-09-01) scoped slice 1 to account-wide packages — a <c>BATON_SKILLS_PATH</c> override, then
/// <c>{BatonPaths.Root}/skills/</c>, then a shipped default beside the assembly — and deferred the
/// repo-local overlay because it raises a precedence question. What ships here is the floor realization
/// alone, reading only the working directory; none of the three rungs exists yet, and the precedence rule
/// this implies is written down in <c>docs/dispatch.md</c> rather than ratified. #1151 stays open.
/// </remarks>
public static class SkillPackageReader
{
    public const string SkillsDirectoryName = "skills";

    /// <summary>
    /// Reads a single canonical skill package from the specified package directory (e.g. <c>skills/&lt;name&gt;</c>).
    /// Returns null if the directory does not exist, is not named usably, does not contain
    /// <c>SKILL.md</c>, or cannot be read.
    /// </summary>
    public static SkillPackage? ReadPackage(string? packageDirectory)
    {
        if (string.IsNullOrWhiteSpace(packageDirectory) || !Directory.Exists(packageDirectory))
        {
            return null;
        }

        // The package NAME becomes a path segment in every realization (claude's
        // .claude/skills/<name>/, agy's "# Skill: <name>" header). Today's only caller derives it from
        // Directory.GetDirectories, where "." / ".." / an embedded separator cannot arise -- but this
        // method is public over a caller-supplied directory, so the safety belongs to the reader rather
        // than to one call site (#1929 review LOW).
        var name = Path.GetFileName(packageDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!IsUsablePackageName(name))
        {
            return null;
        }

        var skillFilePath = Path.Combine(packageDirectory, SkillScanner.SkillFileName);
        if (!File.Exists(skillFilePath))
        {
            return null;
        }

        try
        {
            var content = File.ReadAllText(skillFilePath);
            var description = SkillScanner.ParseDescriptionFromFrontmatter(content) ?? $"Skill in {name}";
            return new SkillPackage(name, description, packageDirectory, skillFilePath, content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether <paramref name="name"/> is safe to use as a single path segment: non-empty, not a
    /// relative-path token, carrying no directory separator, no drive colon, and no character the
    /// platform rejects in a file name.
    /// </summary>
    internal static bool IsUsablePackageName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name != "."
        && name != ".."
        && name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, Path.VolumeSeparatorChar]) < 0
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    /// <summary>
    /// Discovers all canonical skill packages under <c>&lt;rootDirectory&gt;/skills/</c>.
    /// Returns packages sorted by <see cref="SkillPackage.Name"/> ordinal.
    /// </summary>
    public static IReadOnlyList<SkillPackage> DiscoverPackages(string? rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
        {
            return Array.Empty<SkillPackage>();
        }

        var skillsDir = Path.Combine(rootDirectory, SkillsDirectoryName);
        if (!Directory.Exists(skillsDir))
        {
            return Array.Empty<SkillPackage>();
        }

        string[] subDirectories;
        try
        {
            subDirectories = Directory.GetDirectories(skillsDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Console.Error.WriteLine($"Warning: could not enumerate canonical skills directory '{skillsDir}': {ex.Message}");
            return Array.Empty<SkillPackage>();
        }

        var packages = new List<SkillPackage>();
        foreach (var subDir in subDirectories)
        {
            // Per-entry, deliberately: wrapping the whole loop made one unreadable package abort
            // discovery of every alphabetically later one, so the roster under-reported silently
            // (#1929 review LOW (e)). ReadPackage already answers null for its own I/O failures.
            var pkg = ReadPackage(subDir);
            if (pkg is not null)
            {
                packages.Add(pkg);
            }
        }

        packages.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
        return packages;
    }
}
