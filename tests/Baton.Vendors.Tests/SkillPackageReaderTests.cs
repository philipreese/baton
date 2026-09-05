using Baton.Vendors;

namespace Baton.Vendors.Tests;

public sealed class SkillPackageReaderTests
{
    [Fact]
    public void DiscoverPackages_OverFixtureTree_DiscoversAllCanonicalPackages()
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), $"skill-pkg-fixture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureRoot);
        try
        {
            var skillsDir = Path.Combine(fixtureRoot, "skills");
            Directory.CreateDirectory(skillsDir);

            // Valid package 1: with frontmatter description
            var alphaDir = Path.Combine(skillsDir, "alpha-skill");
            Directory.CreateDirectory(alphaDir);
            File.WriteAllText(
                Path.Combine(alphaDir, "SKILL.md"),
                """
                ---
                name: alpha-skill
                description: Alpha skill instructions
                ---
                # Alpha Instructions
                Do the alpha thing.
                """);

            // Valid package 2: without frontmatter description (fallback expected)
            var betaDir = Path.Combine(skillsDir, "beta-skill");
            Directory.CreateDirectory(betaDir);
            File.WriteAllText(
                Path.Combine(betaDir, "SKILL.md"),
                """
                # Beta Instructions
                Do the beta thing.
                """);

            // Non-skill subdirectory: missing SKILL.md
            var notSkillDir = Path.Combine(skillsDir, "ignored-dir");
            Directory.CreateDirectory(notSkillDir);
            File.WriteAllText(Path.Combine(notSkillDir, "README.md"), "Not a skill");

            // Another directory outside skills
            var outsideDir = Path.Combine(fixtureRoot, "other-dir", "gamma-skill");
            Directory.CreateDirectory(outsideDir);
            File.WriteAllText(Path.Combine(outsideDir, "SKILL.md"), "description: Should be ignored");

            var packages = SkillPackageReader.DiscoverPackages(fixtureRoot);

            Assert.Equal(2, packages.Count);
            Assert.Equal("alpha-skill", packages[0].Name);
            Assert.Equal("Alpha skill instructions", packages[0].Description);
            Assert.Contains("Do the alpha thing.", packages[0].Content);

            Assert.Equal("beta-skill", packages[1].Name);
            Assert.Equal("Skill in beta-skill", packages[1].Description);
            Assert.Contains("Do the beta thing.", packages[1].Content);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(fixtureRoot);
        }
    }

    [Fact]
    public void DiscoverPackages_Control_WhenNoSkillsDirectory_ReturnsEmpty()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"empty-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var packages = SkillPackageReader.DiscoverPackages(tempRoot);
            Assert.Empty(packages);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempRoot);
        }
    }

    [Fact]
    public void DiscoverPackages_NullOrEmptyDirectory_ReturnsEmpty()
    {
        Assert.Empty(SkillPackageReader.DiscoverPackages(null));
        Assert.Empty(SkillPackageReader.DiscoverPackages(string.Empty));
        Assert.Empty(SkillPackageReader.DiscoverPackages("   "));
    }

    [Fact]
    public void ReadPackage_ValidPackage_ReadsMetadataAndContent()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"single-pkg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var pkgDir = Path.Combine(tempDir, "sample-skill");
            Directory.CreateDirectory(pkgDir);
            var skillFile = Path.Combine(pkgDir, "SKILL.md");
            File.WriteAllText(
                skillFile,
                """
                ---
                description: Sample skill description
                ---
                Execute instructions.
                """);

            var pkg = SkillPackageReader.ReadPackage(pkgDir);

            Assert.NotNull(pkg);
            Assert.Equal("sample-skill", pkg.Name);
            Assert.Equal("Sample skill description", pkg.Description);
            Assert.Equal(pkgDir, pkg.DirectoryPath);
            Assert.Equal(skillFile, pkg.SkillFilePath);
            Assert.Contains("Execute instructions.", pkg.Content);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempDir);
        }
    }

    [Fact]
    public void ReadPackage_MissingSkillFile_ReturnsNull()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"missing-file-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var pkg = SkillPackageReader.ReadPackage(tempDir);
            Assert.Null(pkg);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempDir);
        }
    }
}
