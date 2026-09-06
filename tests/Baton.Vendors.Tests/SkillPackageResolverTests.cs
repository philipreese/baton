using Baton.Status;

namespace Baton.Vendors.Tests;

/// <summary>
/// #1151 S1: the four resolution rungs, their precedence, and the two refusals a named package can
/// raise. Each rung gets its own arm AND a discriminating negative — a test that only asserts "the
/// package resolved" cannot tell a working rung from a lower one that happened to hold the same name,
/// which is exactly the confusion the ladder exists to remove.
/// </summary>
public sealed class SkillPackageResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"skill-resolver-{Guid.NewGuid():N}");

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
            // A leaked temp directory is not worth failing a test over.
        }
    }

    private string WritePackage(string skillsDirectory, string name, string body = "# Do the thing", string? manifest = null)
    {
        var packageDir = Path.Combine(skillsDirectory, name);
        Directory.CreateDirectory(packageDir);
        File.WriteAllText(Path.Combine(packageDir, "SKILL.md"), body);
        if (manifest is not null)
        {
            File.WriteAllText(Path.Combine(packageDir, "skill.json"), manifest);
        }

        return packageDir;
    }

    private string NewDirectory(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void Env_override_is_the_highest_rung_and_the_home_rung_is_not_consulted_when_it_matches()
    {
        var envSkills = NewDirectory("env-skills");
        var home = NewDirectory("home");
        Directory.CreateDirectory(Path.Combine(home, "skills"));
        WritePackage(envSkills, "shared", "# from the env override");
        WritePackage(Path.Combine(home, "skills"), "shared", "# from the account library");

        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Current with { SkillsPathOverride = envSkills, HomeOverride = home });

        var package = SkillPackageResolver.Resolve("shared", workspaceDirectory: null);

        Assert.Contains("from the env override", package.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Account_wide_rung_wins_when_the_env_override_does_not_hold_the_name()
    {
        // The control arm for the test above: with the SAME env override in force but not carrying
        // this name, resolution must fall to the account library rather than fail.
        var envSkills = NewDirectory("env-skills");
        var home = NewDirectory("home");
        var homeSkills = Path.Combine(home, "skills");
        Directory.CreateDirectory(homeSkills);
        WritePackage(envSkills, "unrelated");
        WritePackage(homeSkills, "shared", "# from the account library");

        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Current with { SkillsPathOverride = envSkills, HomeOverride = home });

        var package = SkillPackageResolver.Resolve("shared", workspaceDirectory: null);

        Assert.Contains("from the account library", package.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Workspace_overlay_is_the_lowest_rung_and_is_shadowed_by_the_account_library()
    {
        // The negative a reader's prior gets wrong: everywhere else project scope beats user scope.
        // Here it does not, and this is the arm that pins it.
        var home = NewDirectory("home");
        var homeSkills = Path.Combine(home, "skills");
        Directory.CreateDirectory(homeSkills);
        var workspace = NewDirectory("workspace");
        var workspaceSkills = Path.Combine(workspace, "skills");
        Directory.CreateDirectory(workspaceSkills);

        WritePackage(homeSkills, "shared", "# from the account library");
        WritePackage(workspaceSkills, "shared", "# from the repository");

        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Current with { SkillsPathOverride = null, HomeOverride = home });

        var package = SkillPackageResolver.Resolve("shared", workspace);

        Assert.Contains("from the account library", package.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Workspace_overlay_still_resolves_a_name_no_higher_rung_carries()
    {
        // The discriminating control for the test above: the workspace rung is LOWEST, not disabled.
        var home = NewDirectory("home");
        Directory.CreateDirectory(Path.Combine(home, "skills"));
        var workspace = NewDirectory("workspace");
        var workspaceSkills = Path.Combine(workspace, "skills");
        Directory.CreateDirectory(workspaceSkills);
        WritePackage(workspaceSkills, "repo-only", "# from the repository");

        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Current with { SkillsPathOverride = null, HomeOverride = home });

        var package = SkillPackageResolver.Resolve("repo-only", workspace);

        Assert.Contains("from the repository", package.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_name_refuses_and_names_every_rung_searched()
    {
        var home = NewDirectory("home");
        var workspace = NewDirectory("workspace");
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Current with { SkillsPathOverride = null, HomeOverride = home });

        var ex = Assert.Throws<UnknownSkillPackageException>(() => SkillPackageResolver.Resolve("typo", workspace));

        Assert.Equal("typo", ex.SkillName);
        Assert.Contains(Path.Combine(home, "skills"), ex.TryInvocation, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(workspace, "skills"), ex.TryInvocation, StringComparison.Ordinal);
    }

    [Fact]
    public void A_broken_package_on_a_higher_rung_refuses_rather_than_falling_through_to_a_lower_one()
    {
        // The substitution this feature exists to make impossible: an operator who authored a broken
        // package meant THAT one, and silently resolving a same-named package from elsewhere would
        // hand the worker content nobody asked for.
        var home = NewDirectory("home");
        var homeSkills = Path.Combine(home, "skills");
        Directory.CreateDirectory(homeSkills);
        var workspace = NewDirectory("workspace");
        var workspaceSkills = Path.Combine(workspace, "skills");
        Directory.CreateDirectory(workspaceSkills);

        WritePackage(homeSkills, "shared", "Assets live at ${CLAUDE_SKILL_DIR}/x.md");
        WritePackage(workspaceSkills, "shared", "# a perfectly good fallback");

        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Current with { SkillsPathOverride = null, HomeOverride = home });

        var ex = Assert.Throws<SkillPackageFormatException>(() => SkillPackageResolver.Resolve("shared", workspace));

        Assert.Equal(SkillPackageLint.VendorPlaceholderRule, ex.Rule);
    }

    [Fact]
    public void ResolveAll_preserves_order_drops_duplicates_and_answers_empty_for_no_names()
    {
        var home = NewDirectory("home");
        var homeSkills = Path.Combine(home, "skills");
        Directory.CreateDirectory(homeSkills);
        WritePackage(homeSkills, "alpha");
        WritePackage(homeSkills, "beta");

        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Current with { SkillsPathOverride = null, HomeOverride = home });

        var resolved = SkillPackageResolver.ResolveAll(["beta", "alpha", "beta"], workspaceDirectory: null);

        Assert.Equal(["beta", "alpha"], resolved.Select(p => p.Name).ToArray());
        Assert.Empty(SkillPackageResolver.ResolveAll(null, workspaceDirectory: null));
        Assert.Empty(SkillPackageResolver.ResolveAll([], workspaceDirectory: null));
    }
}
