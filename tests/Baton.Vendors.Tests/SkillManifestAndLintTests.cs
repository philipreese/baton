namespace Baton.Vendors.Tests;

/// <summary>
/// #1151 S1: the <c>skill.json</c> manifest parse, the SKILL.md-only fallback that keeps #1929's
/// packages working, and one red arm per format-lint rule with a green control beside it — a lint that
/// only ever refuses is indistinguishable from one that always refuses.
/// </summary>
public sealed class SkillManifestAndLintTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"skill-manifest-{Guid.NewGuid():N}");

    public SkillManifestAndLintTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private string Package(string name, string instructions, string? manifest = null, string? assetRelativePath = null)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), instructions);
        if (manifest is not null)
        {
            File.WriteAllText(Path.Combine(dir, "skill.json"), manifest);
        }

        if (assetRelativePath is not null)
        {
            var assetPath = Path.Combine(dir, assetRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(assetPath)!);
            File.WriteAllText(assetPath, "echo hi");
        }

        return dir;
    }

    [Fact]
    public void A_manifest_supplies_description_realization_and_requirements()
    {
        var dir = Package(
            "thorough-review",
            "# Review checklist",
            """
            {
              "name": "thorough-review",
              "version": 3,
              "description": "Adversarial review checklist.",
              "requires": { "read_files": true, "run_shell_commands": false },
              "realization": "native-preferred"
            }
            """);

        var package = SkillPackageReader.LoadPackage(dir);

        Assert.Equal("thorough-review", package.Name);
        Assert.Equal("Adversarial review checklist.", package.Description);
        Assert.Equal(3, package.Manifest!.Version);
        Assert.Equal(SkillRealization.NativePreferred, package.Realization);
        Assert.True(package.Requires.ReadFiles);
        Assert.False(package.Requires.RunShellCommands);
        Assert.Null(package.Requires.NetworkAccess);
    }

    [Fact]
    public void A_manifest_can_name_a_different_instructions_file()
    {
        var dir = Package("split", "# unused SKILL.md",
            """
            { "description": "Splits its instructions out.", "instructions": "INSTRUCTIONS.md" }
            """);
        File.WriteAllText(Path.Combine(dir, "INSTRUCTIONS.md"), "# the real body");

        var package = SkillPackageReader.LoadPackage(dir);

        Assert.Contains("the real body", package.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void A_manifest_less_package_still_loads_with_floor_defaults_and_no_declared_requirements()
    {
        // The #1929 shape. It must keep working: nothing here declares anything, so nothing can refuse.
        var dir = Package(
            "legacy",
            """
            ---
            description: A skill from before the manifest existed
            ---
            # Body
            """);

        var package = SkillPackageReader.LoadPackage(dir);

        Assert.Null(package.Manifest);
        Assert.Equal("A skill from before the manifest existed", package.Description);
        Assert.Equal(SkillRealization.Floor, package.Realization);
        Assert.Empty(package.Requires.MissingFrom(new PermissionGrant(false, false, false, [], false)));
    }

    [Fact]
    public void A_manifest_with_no_description_refuses()
    {
        var dir = Package("nameless", "# Body", """{ "version": 1 }""");

        var ex = Assert.Throws<SkillPackageFormatException>(() => SkillPackageReader.LoadPackage(dir));

        Assert.Equal(SkillPackageReader.ManifestRule, ex.Rule);
    }

    [Fact]
    public void An_unknown_realization_word_refuses_rather_than_defaulting_to_floor()
    {
        var dir = Package("odd", "# Body", """{ "description": "d", "realization": "magic" }""");

        var ex = Assert.Throws<SkillPackageFormatException>(() => SkillPackageReader.LoadPackage(dir));

        Assert.Equal(SkillPackageReader.ManifestRule, ex.Rule);
        Assert.Contains("magic", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Lint_refuses_a_vendor_placeholder_and_accepts_the_baton_one()
    {
        var offending = Package("vendorish", "Read ${CLAUDE_SKILL_DIR}/checklist.md first.");
        var ex = Assert.Throws<SkillPackageFormatException>(() => SkillPackageReader.LoadPackage(offending));
        Assert.Equal(SkillPackageLint.VendorPlaceholderRule, ex.Rule);
        Assert.Contains("${CLAUDE_SKILL_DIR}", ex.Message, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(offending, "SKILL.md"), ex.Message, StringComparison.Ordinal);

        // Polarity: the SAME sentence with Baton's own placeholder loads.
        var clean = Package("portable", "Read ${BATON_SKILL_DIR}/checklist.md first.");
        Assert.Equal("portable", SkillPackageReader.LoadPackage(clean).Name);
    }

    [Fact]
    public void Lint_refuses_bash_injection_syntax_and_accepts_a_plain_backtick_span()
    {
        var offending = Package("injected", "Run this: !`git status --porcelain`");
        var ex = Assert.Throws<SkillPackageFormatException>(() => SkillPackageReader.LoadPackage(offending));
        Assert.Equal(SkillPackageLint.BashInjectionRule, ex.Rule);

        // Polarity: a backtick span with no bang before it is ordinary markdown and must still load.
        var clean = Package("quoting", "Run `git status --porcelain` and read the result.");
        Assert.Equal("quoting", SkillPackageReader.LoadPackage(clean).Name);
    }

    [Fact]
    public void Lint_refuses_an_executable_asset_only_when_the_manifest_explicitly_withholds_the_shell()
    {
        var offending = Package(
            "scripted", "# Body",
            """{ "description": "d", "requires": { "run_shell_commands": false } }""",
            assetRelativePath: Path.Combine("scripts", "validate.ps1"));
        var ex = Assert.Throws<SkillPackageFormatException>(() => SkillPackageReader.LoadPackage(offending));
        Assert.Equal(SkillPackageLint.ExecutableAssetWithoutShellRule, ex.Rule);
        Assert.Contains("validate.ps1", ex.Message, StringComparison.Ordinal);

        // Polarity 1: the same script with the shell declared is coherent.
        var granted = Package(
            "scripted-ok", "# Body",
            """{ "description": "d", "requires": { "run_shell_commands": true } }""",
            assetRelativePath: Path.Combine("scripts", "validate.ps1"));
        Assert.Equal("scripted-ok", SkillPackageReader.LoadPackage(granted).Name);

        // Polarity 2, the regression this rule could have caused: a manifest-LESS package bundling the
        // same script declares nothing, so it is not incoherent and must still load (#1929 packages).
        var manifestLess = Package("scripted-legacy", "# Body", manifest: null, assetRelativePath: Path.Combine("scripts", "validate.ps1"));
        Assert.Equal("scripted-legacy", SkillPackageReader.LoadPackage(manifestLess).Name);
    }

    [Fact]
    public void Discovery_skips_a_linted_package_rather_than_refusing_the_whole_scan()
    {
        // The tolerant path: an operator who named nothing must not lose an unrelated dispatch to one
        // bad package. The strict path (LoadPackage, above) is what refuses.
        var skillsDir = Path.Combine(_root, "workspace", "skills");
        Directory.CreateDirectory(skillsDir);
        Directory.CreateDirectory(Path.Combine(skillsDir, "good"));
        File.WriteAllText(Path.Combine(skillsDir, "good", "SKILL.md"), "# fine");
        Directory.CreateDirectory(Path.Combine(skillsDir, "bad"));
        File.WriteAllText(Path.Combine(skillsDir, "bad", "SKILL.md"), "Read ${CLAUDE_SKILL_DIR}/x.md");

        var packages = SkillPackageReader.DiscoverPackages(Path.Combine(_root, "workspace"));

        Assert.Equal(["good"], packages.Select(p => p.Name).ToArray());
    }

    [Fact]
    public void MissingFrom_names_only_the_categories_the_grant_withholds()
    {
        var requires = new SkillRequirements(ReadFiles: true, WriteFiles: true, RunShellCommands: true, NetworkAccess: true);
        var readOnlyGrant = new PermissionGrant(ReadFiles: true, WriteFiles: false, RunShellCommands: false, ShellCommandPatterns: [], NetworkAccess: false);

        var missing = requires.MissingFrom(readOnlyGrant);

        Assert.Equal(["WriteFiles", "RunShellCommands", "NetworkAccess"], missing.ToArray());
        Assert.Empty(requires.MissingFrom(new PermissionGrant(true, true, true, [], true)));

        // A null grant is the raw PermissionScope escape hatch: nothing structured to check against.
        Assert.Empty(requires.MissingFrom(null));
    }
}
