namespace Baton.Vendors;

/// <summary>
/// A canonical skill package (<c>skills/&lt;name&gt;/SKILL.md</c> at the repo root, citing operator
/// ruling Q1 on #1151 settling on canonical-first, SKILL.md-compatible package shape, with no persona layer
/// per decision 0033).
/// </summary>
/// <param name="Name">The skill name (the directory name under <c>skills/</c>).</param>
/// <param name="Description">The skill description extracted from <c>SKILL.md</c> frontmatter, or fallback.</param>
/// <param name="DirectoryPath">The absolute path to the skill package directory.</param>
/// <param name="SkillFilePath">The absolute path to the <c>SKILL.md</c> file inside the package.</param>
/// <param name="Content">The full text content of <c>SKILL.md</c>.</param>
public sealed record SkillPackage(
    string Name,
    string Description,
    string DirectoryPath,
    string SkillFilePath,
    string Content);
