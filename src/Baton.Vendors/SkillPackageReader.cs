using System.Text.Json;

namespace Baton.Vendors;

/// <summary>
/// Reader for one canonical skill package directory (#1151 §3): the typed <c>skill.json</c> manifest
/// when present, the SKILL.md-compatible instructions file beside it, and the format lint over both.
/// No YAML parser: the manifest is JSON, and the instructions file is markdown this type copies or
/// inlines but never interprets.
/// </summary>
/// <remarks>
/// <b>Two entry points, deliberately, and the difference is who named the package.</b>
/// <list type="bullet">
/// <item><see cref="LoadPackage"/> is strict: a malformed manifest or a lint violation throws
///   <see cref="SkillPackageFormatException"/>. Used when an operator named this package
///   (<c>--skill &lt;name&gt;</c>), where #1151 §4.6's rule is fail-fast on identity and format — a
///   typo, or a package that does not parse, must never produce a run that silently lacks the
///   capability.</item>
/// <item><see cref="ReadPackage"/>/<see cref="DiscoverPackages"/> are tolerant: they scan a directory
///   nobody named, so a single bad package warns on stderr and is skipped rather than refusing an
///   unrelated dispatch. Loud, not silent — the roster then omits it, and the warning says which file
///   and which rule.</item>
/// </list>
/// <para>
/// <b>Where packages live is <see cref="SkillPackageResolver"/>'s question, not this type's.</b> This
/// reader is handed a directory.
/// </para>
/// </remarks>
public static class SkillPackageReader
{
    public const string SkillsDirectoryName = "skills";

    /// <summary>
    /// Reads a canonical skill package from <paramref name="packageDirectory"/>, refusing loudly.
    /// </summary>
    /// <exception cref="SkillPackageFormatException">
    /// The directory is not a usable package (missing, unusably named, no instructions file), its
    /// <c>skill.json</c> does not parse, or the format lint refuses it.
    /// </exception>
    public static SkillPackage LoadPackage(string packageDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);

        var name = PackageNameOf(packageDirectory);
        if (!IsUsablePackageName(name))
        {
            throw new SkillPackageFormatException(
                name ?? packageDirectory, "package-name", packageDirectory,
                "the directory name is not usable as a single path segment, and the directory name is the package's identity.",
                "rename the package directory to a plain name, e.g. 'thorough-review'.");
        }

        if (!Directory.Exists(packageDirectory))
        {
            throw new SkillPackageFormatException(
                name!, "package-missing", packageDirectory, "the package directory does not exist.");
        }

        var manifest = ReadManifest(name!, packageDirectory);
        var instructionsFileName = manifest?.InstructionsFileName ?? SkillScanner.SkillFileName;
        var skillFilePath = Path.Combine(packageDirectory, instructionsFileName);
        if (!File.Exists(skillFilePath))
        {
            throw new SkillPackageFormatException(
                name!, "missing-instructions", skillFilePath,
                $"the package has no '{instructionsFileName}'.",
                manifest is null
                    ? $"add a '{SkillScanner.SkillFileName}', or a 'skill.json' naming a different instructions file."
                    : "point skill.json's \"instructions\" at a file that exists.");
        }

        string content;
        try
        {
            content = File.ReadAllText(skillFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new SkillPackageFormatException(
                name!, "unreadable-instructions", skillFilePath, $"it could not be read: {ex.Message}");
        }

        var package = Compose(name!, packageDirectory, skillFilePath, content, manifest);
        SkillPackageLint.Refuse(package);
        return package;
    }

    /// <summary>
    /// Reads a single canonical skill package from the specified package directory, tolerantly.
    /// Returns null if the directory does not exist, is not named usably, has no instructions file,
    /// cannot be read, or fails the manifest parse or the format lint — the last two warning on stderr
    /// first, naming the file and rule, so a skipped package is never silent.
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
        var name = PackageNameOf(packageDirectory);
        if (!IsUsablePackageName(name))
        {
            return null;
        }

        try
        {
            var package = LoadPackage(packageDirectory);
            return package;
        }
        catch (SkillPackageFormatException ex) when (IsSkippableOnDiscovery(ex))
        {
            Console.Error.WriteLine($"Warning: skipping canonical skill package '{name}': {ex.Message}");
            return null;
        }
        catch (SkillPackageFormatException)
        {
            // A directory with no instructions file at all is simply not a package -- the ordinary
            // shape of a `skills/` directory holding something else. Nothing to warn about.
            return null;
        }
    }

    /// <summary>
    /// Whether a strict-load refusal is worth a warning on the tolerant path. A malformed manifest or a
    /// lint violation is: the operator authored something that looks like a package and it is being
    /// dropped. A directory that simply is not a package is not.
    /// </summary>
    private static bool IsSkippableOnDiscovery(SkillPackageFormatException ex) =>
        ex.Rule is SkillPackageLint.VendorPlaceholderRule
            or SkillPackageLint.BashInjectionRule
            or SkillPackageLint.ExecutableAssetWithoutShellRule
            or ManifestRule
            or "unreadable-instructions";

    /// <summary>The rule slug a malformed or unreadable <c>skill.json</c> refuses under.</summary>
    public const string ManifestRule = "manifest-parse";

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
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            return Array.Empty<SkillPackage>();
        }

        return DiscoverPackagesIn(Path.Combine(rootDirectory, SkillsDirectoryName));
    }

    /// <summary>
    /// Discovers every canonical skill package whose directory sits directly under
    /// <paramref name="skillsDirectory"/> — which IS the skills directory, unlike
    /// <see cref="DiscoverPackages"/>, which appends <c>skills/</c> to a root. The resolver's rungs are
    /// already skills directories, so they call this one.
    /// </summary>
    public static IReadOnlyList<SkillPackage> DiscoverPackagesIn(string? skillsDirectory)
    {
        if (string.IsNullOrWhiteSpace(skillsDirectory) || !Directory.Exists(skillsDirectory))
        {
            return Array.Empty<SkillPackage>();
        }

        string[] subDirectories;
        try
        {
            subDirectories = Directory.GetDirectories(skillsDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Console.Error.WriteLine($"Warning: could not enumerate canonical skills directory '{skillsDirectory}': {ex.Message}");
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

    private static string? PackageNameOf(string packageDirectory) =>
        Path.GetFileName(packageDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    private static SkillPackage Compose(
        string name, string packageDirectory, string skillFilePath, string content, SkillManifest? manifest)
    {
        var description = manifest?.Description
            ?? SkillScanner.ParseDescriptionFromFrontmatter(content)
            ?? $"Skill in {name}";
        return new SkillPackage(name, description, packageDirectory, skillFilePath, content, manifest);
    }

    /// <summary>
    /// Parses <c>skill.json</c> if the package has one. Snake-case wire form, the same convention
    /// <see cref="WorkerRoleCatalog"/>'s own catalog files use; <c>realization</c> and the presence of a
    /// non-blank <c>description</c> are hand-validated so both name the offending package rather than
    /// silently defaulting.
    /// </summary>
    private static SkillManifest? ReadManifest(string name, string packageDirectory)
    {
        var manifestPath = Path.Combine(packageDirectory, SkillManifest.FileName);
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        RawManifest? raw;
        try
        {
            using var stream = File.OpenRead(manifestPath);
            raw = JsonSerializer.Deserialize<RawManifest>(stream, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new SkillPackageFormatException(
                name, ManifestRule, manifestPath, $"it does not parse: {ex.Message}",
                "skill.json is a JSON object with snake_case keys: name, version, description, instructions, assets, requires, realization.");
        }

        if (raw is null)
        {
            throw new SkillPackageFormatException(name, ManifestRule, manifestPath, "it parsed to null.");
        }

        if (string.IsNullOrWhiteSpace(raw.Description))
        {
            throw new SkillPackageFormatException(
                name, ManifestRule, manifestPath,
                "\"description\" is missing or blank — on both vendors it is what a model reads to judge "
                + "relevance, and what a roster renders, so a package without one is invisible in the way that matters.",
                "add a \"description\" saying what the skill does and when to use it.");
        }

        var realizationWord = string.IsNullOrWhiteSpace(raw.Realization) ? "floor" : raw.Realization;
        if (!SkillManifest.RealizationWords.TryGetValue(realizationWord, out var realization))
        {
            throw new SkillPackageFormatException(
                name, ManifestRule, manifestPath,
                $"\"realization\": \"{raw.Realization}\" is not a known value.",
                $"use one of: {string.Join(", ", SkillManifest.RealizationWords.Keys)}.");
        }

        if (raw.Instructions is not null && !IsUsablePackageName(raw.Instructions))
        {
            throw new SkillPackageFormatException(
                name, ManifestRule, manifestPath,
                $"\"instructions\": \"{raw.Instructions}\" is not a plain file name inside the package.",
                "name a file that sits directly in the package directory, e.g. \"INSTRUCTIONS.md\".");
        }

        return new SkillManifest(
            Name: raw.Name,
            Version: raw.Version ?? 1,
            Description: raw.Description,
            Instructions: raw.Instructions,
            Assets: raw.Assets,
            Requires: raw.Requires,
            Realization: realization);
    }

    // Plain JSON, snake_case keys -- the same wire convention WorkerRoles.json/WorkerTiers.json use, so
    // an operator authoring both files does not have to hold two casings in their head.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>
    /// The wire shape. Every field is optional at the deserializer, and the validation above is what
    /// turns an omitted-but-required one into a message naming the package — the same split
    /// <see cref="WorkerRoleCatalog"/>'s <c>RawRole</c>/<c>ResolveOutput</c> pair uses, and for the same
    /// reason: a converter attached to the type has no package to name.
    /// </summary>
    private sealed record RawManifest(
        string? Name = null,
        int? Version = null,
        string? Description = null,
        string? Instructions = null,
        IReadOnlyList<string>? Assets = null,
        SkillRequirements? Requires = null,
        string? Realization = null);
}
