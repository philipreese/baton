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

    public void Dispose() => Baton.Tests.Shared.DirectoryCleanup.DeleteRecursively(_root);

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

        // Polarity: the SAME sentence with Baton's own placeholder loads. That is ALL this asserts --
        // the token is reserved, not substituted (#1941 review HIGH; SkillPackageLint's own remarks and
        // spec/baton.md §9), so a package carrying it still ships the literal text to the model. The
        // remedy the rule prints is prose naming the file, and this arm is not evidence otherwise.
        var clean = Package("portable", "Read ${BATON_SKILL_DIR}/checklist.md first.");
        Assert.Equal("portable", SkillPackageReader.LoadPackage(clean).Name);
        Assert.Contains("no placeholder is substituted today", ex.TryInvocation!, StringComparison.Ordinal);
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

    /// <summary>
    /// #1941 review MEDIUM: <c>shell_command_patterns</c> parses, is documented as part of the
    /// comparison, and used to be read by nothing — so a package requiring <c>gh:*</c> bound cleanly to
    /// a grant scoped to <c>git:*</c> and had every <c>gh</c> call denied mid-lane instead. Each arm
    /// below is one rule of <c>UnsatisfiedShellPatterns</c>, with the polarity that discriminates it.
    /// </summary>
    [Fact]
    public void A_required_shell_pattern_the_grant_lacks_is_unsatisfied_and_one_it_carries_is_not()
    {
        var requires = new SkillRequirements(RunShellCommands: true, ShellCommandPatterns: ["gh:*"]);

        var scopedElsewhere = new PermissionGrant(RunShellCommands: true, ShellCommandPatterns: ["git:*"]);
        var missing = Assert.Single(requires.MissingFrom(scopedElsewhere));
        Assert.Equal("ShellCommandPatterns (gh:*)", missing);

        // Polarity 1: the identical requirement against a grant that lists the pattern.
        Assert.Empty(requires.MissingFrom(
            new PermissionGrant(RunShellCommands: true, ShellCommandPatterns: ["git:*", "gh:*"])));

        // Polarity 2: an UNSCOPED granted shell means "any command", so it satisfies the pattern.
        Assert.Empty(requires.MissingFrom(new PermissionGrant(RunShellCommands: true, ShellCommandPatterns: [])));

        // Deny beats allow, the standing rule of DeniedShellCommandPatterns: listing it both ways is
        // still a refusal, and the arm above is what shows the allowlist alone would have passed.
        Assert.Contains(
            "ShellCommandPatterns (gh:*)",
            requires.MissingFrom(new PermissionGrant(
                RunShellCommands: true, ShellCommandPatterns: ["gh:*"], DeniedShellCommandPatterns: ["gh:*"])));

        // Patterns declared with no run_shell_commands: refused rather than passing because the boolean
        // was absent -- rule 1 of UnsatisfiedShellPatterns, which states why.
        Assert.Equal(
            ["ShellCommandPatterns (gh:*)"],
            new SkillRequirements(ShellCommandPatterns: ["gh:*"])
                .MissingFrom(new PermissionGrant(ReadFiles: true)).ToArray());

        // And a package declaring no patterns is unaffected by any of it.
        Assert.Empty(new SkillRequirements(RunShellCommands: true).MissingFrom(scopedElsewhere));
    }

    /// <summary>
    /// #1941 re-review MEDIUM: exact membership is the safe direction on the allow half and INVERTS on
    /// the deny half — a deny entry that COVERS the required pattern is not equal to it, so the package
    /// bound and the gate denied every such command mid-lane. <c>UnsatisfiedShellPatterns</c>' remark
    /// states why the deny half now runs the gate's own predicate over the de-starred pattern; this pins
    /// the covering case red and two non-covering ones green, so "always refuse" cannot pass.
    /// </summary>
    [Fact]
    public void A_deny_pattern_covering_a_required_one_is_unsatisfied_while_a_narrower_deny_still_binds()
    {
        // The control, read first: the gate's predicate on the shortest command line the required
        // pattern admits. A green refusal below is about coverage only if this is true.
        Assert.True(ShellCommandPatternMatcher.IsDenied("dotnet build", ["dotnet *"]));

        var requires = new SkillRequirements(RunShellCommands: true, ShellCommandPatterns: ["dotnet build*"]);

        // The shipped shape (the implement role's grant, WorkerRoles.json): an UNSCOPED shell, which
        // satisfies the allow half outright, plus a covering deny entry. Exact membership passed this.
        Assert.Equal(
            ["ShellCommandPatterns (dotnet build*)"],
            requires.MissingFrom(new PermissionGrant(
                RunShellCommands: true, ShellCommandPatterns: [], DeniedShellCommandPatterns: ["dotnet *"])).ToArray());

        // Polarity 1: a deny entry NARROWER than the required pattern is not a standing "never" over the
        // family, so it still binds — the residual the remark names, not a case this check claims.
        Assert.Empty(requires.MissingFrom(new PermissionGrant(
            RunShellCommands: true, ShellCommandPatterns: [],
            DeniedShellCommandPatterns: ["dotnet build --no-restore*"])));

        // Polarity 2: an unrelated deny entry refuses nothing.
        Assert.Empty(requires.MissingFrom(new PermissionGrant(
            RunShellCommands: true, ShellCommandPatterns: [], DeniedShellCommandPatterns: ["gh *"])));
    }
}
