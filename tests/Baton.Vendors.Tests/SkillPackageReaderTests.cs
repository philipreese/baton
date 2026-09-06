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

    /// <summary>
    /// #1929 review LOW: <see cref="SkillPackageReader.ReadPackage"/> is public over a caller-supplied
    /// directory, and the guard beside <c>IsUsablePackageName</c> says why the name matters. A directory
    /// reached as <c>&lt;skills&gt;/..</c> names itself <c>..</c>, which must be refused by the reader
    /// rather than by the accident of today's single call site enumerating real subdirectories.
    /// </summary>
    [Fact]
    public void ReadPackage_TraversalPackageName_ReturnsNull()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"traversal-{Guid.NewGuid():N}");
        var nested = Path.Combine(tempDir, "skills");
        Directory.CreateDirectory(nested);
        try
        {
            // The parent holds a real SKILL.md, so the ONLY thing standing between this call and a
            // package named ".." is the name guard.
            File.WriteAllText(Path.Combine(tempDir, "SKILL.md"), "description: Reachable by traversal");

            Assert.Null(SkillPackageReader.ReadPackage(Path.Combine(nested, "..")));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempDir);
        }
    }

    /// <summary>
    /// The control that makes the arm above about the NAME rather than about the fixture: the identical
    /// directory, named normally, reads back as a package.
    /// </summary>
    [Fact]
    public void ReadPackage_Control_SameDirectoryNamedNormally_ReadsBack()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"traversal-control-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "SKILL.md"), "description: Reachable by traversal");

            var pkg = SkillPackageReader.ReadPackage(tempDir);

            Assert.NotNull(pkg);
            Assert.Equal("Reachable by traversal", pkg.Description);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempDir);
        }
    }

    /// <summary>
    /// #1929 review LOW (e): one unreadable or malformed package must cost only itself. A subdirectory
    /// with no <c>SKILL.md</c> sits alphabetically before a valid one; discovery must still return the
    /// later package rather than stopping at the first entry it cannot read.
    /// </summary>
    [Fact]
    public void DiscoverPackages_UnreadableEntry_DoesNotHideAlphabeticallyLaterPackages()
    {
        var root = Path.Combine(Path.GetTempPath(), $"skill-partial-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "skills", "aaa-not-a-package"));
            var valid = Path.Combine(root, "skills", "zzz-valid");
            Directory.CreateDirectory(valid);
            File.WriteAllText(Path.Combine(valid, "SKILL.md"), "description: Still discovered");

            var packages = SkillPackageReader.DiscoverPackages(root);

            var only = Assert.Single(packages);
            Assert.Equal("zzz-valid", only.Name);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }
}
