namespace Baton.Vendors;

/// <summary>
/// A canonical skill package — a directory whose name is the package's identity, holding a typed
/// <c>skill.json</c> manifest (optional) beside a SKILL.md-compatible instructions file, per operator
/// ruling Q1 on #1151 settling on a canonical-first, SKILL.md-compatible package shape, with no persona
/// layer per decision 0033. <see cref="SkillPackageResolver"/> is where a name becomes one of these,
/// and states the rung precedence.
/// </summary>
/// <param name="Name">The skill name — the directory name, which is the package's identity.</param>
/// <param name="Description">
/// The manifest's <c>description</c> when there is a manifest, otherwise the <c>description:</c> line
/// scraped from the instructions file's front matter, otherwise a fallback. See
/// <see cref="SkillManifest"/>'s own <c>Description</c> for what this field is FOR.
/// </param>
/// <param name="DirectoryPath">The absolute path to the skill package directory.</param>
/// <param name="SkillFilePath">The absolute path to the instructions file inside the package (<c>SKILL.md</c> unless the manifest names another).</param>
/// <param name="Content">The full text content of the instructions file.</param>
/// <param name="Manifest">
/// The parsed <c>skill.json</c>, or null for a manifest-less package — the shape #1929 shipped, which
/// still resolves and still realizes. Null is <em>not</em> a manifest of all-defaults: it is what the
/// <c>executable-asset-without-shell</c> lint and the bind-time requirement check both read as "this
/// package declares nothing", which is why neither can newly refuse a package that worked before.
/// </param>
public sealed record SkillPackage(
    string Name,
    string Description,
    string DirectoryPath,
    string SkillFilePath,
    string Content,
    SkillManifest? Manifest = null)
{
    /// <summary>
    /// The realization this package asks for — <see cref="SkillRealization.Floor"/> unless a manifest
    /// says otherwise. See <see cref="SkillRealization"/> for why both values behave identically in
    /// this slice.
    /// </summary>
    public SkillRealization Realization => Manifest?.Realization ?? SkillRealization.Floor;

    /// <summary>What this package requires of the worker's grant. Never null; a manifest-less package requires nothing.</summary>
    public SkillRequirements Requires => Manifest?.Requires ?? SkillRequirements.None;
}
