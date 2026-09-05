namespace Baton.Vendors;

/// <summary>
/// Reader for canonical skill packages (<c>skills/&lt;name&gt;/SKILL.md</c> at repository root or workspace).
/// Extracts metadata and instructions without a YAML parser dependency, shared across worker adapters (#1151).
/// </summary>
public static class SkillPackageReader
{
    public const string SkillsDirectoryName = "skills";

    /// <summary>
    /// Reads a single canonical skill package from the specified package directory (e.g. <c>skills/&lt;name&gt;</c>).
    /// Returns null if the directory does not exist, does not contain <c>SKILL.md</c>, or cannot be read.
    /// </summary>
    public static SkillPackage? ReadPackage(string? packageDirectory)
    {
        if (string.IsNullOrWhiteSpace(packageDirectory) || !Directory.Exists(packageDirectory))
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
            var name = Path.GetFileName(packageDirectory);
            var description = SkillScanner.ParseDescriptionFromFrontmatter(content) ?? $"Skill in {name}";
            return new SkillPackage(name, description, packageDirectory, skillFilePath, content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>
    /// Discovers all canonical skill packages in the given root directory (looking under <c>&lt;root&gt;/skills/</c>
    /// or in <paramref name="rootDirectory"/> directly if named <c>skills</c>).
    /// Returns packages sorted by <see cref="SkillPackage.Name"/> ordinal.
    /// </summary>
    public static IReadOnlyList<SkillPackage> DiscoverPackages(string? rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
        {
            return Array.Empty<SkillPackage>();
        }

        string skillsDir;
        var trimmed = rootDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(Path.GetFileName(trimmed), SkillsDirectoryName, StringComparison.OrdinalIgnoreCase))
        {
            skillsDir = rootDirectory;
        }
        else
        {
            skillsDir = Path.Combine(rootDirectory, SkillsDirectoryName);
            if (!Directory.Exists(skillsDir))
            {
                return Array.Empty<SkillPackage>();
            }
        }

        var packages = new List<SkillPackage>();
        try
        {
            foreach (var subDir in Directory.GetDirectories(skillsDir))
            {
                var pkg = ReadPackage(subDir);
                if (pkg is not null)
                {
                    packages.Add(pkg);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Console.Error.WriteLine($"Warning: could not enumerate canonical skills directory '{skillsDir}': {ex.Message}");
        }

        packages.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
        return packages;
    }
}
