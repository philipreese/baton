using System.Text.RegularExpressions;
using Baton.Status;
using Baton.Vendors;
using Xunit;

namespace Baton.Vendors.Tests;

/// <summary>
/// #2110: the four arms spec/baton.md §2 ("Role default skills") states — attached first, added to by
/// <c>--skill</c>, removed by the opt-out, and the unresolvable-default refusal that names the role.
/// The shipped three packages are pinned here too — loadable, lint-clean, bounded in size, and
/// carrying nothing task-specific — because a default that fails to load turns every dispatch of that
/// role into a refusal.
/// </summary>
/// <remarks>
/// Reads the shipped catalog off <c>BatonEnvironmentSnapshot.Current</c> the way <see cref="RoleDispatchTests"/>
/// does, so it joins the same non-parallel collection. The shipped packages resolve from the
/// next-to-the-assembly rung (<c>Baton.Vendors.csproj</c> copies its <c>Skills/</c> tree there), which
/// is the rung a role default has to be found on for a dispatch against any workspace.
/// </remarks>
[Collection(WorkerRoleCatalogCollection.Name)]
public sealed class RoleDefaultSkillsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"role-default-skills-{Guid.NewGuid():N}");

    public RoleDefaultSkillsTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Baton.Tests.Shared.DirectoryCleanup.DeleteRecursively(_root);

    private static readonly IReadOnlyDictionary<string, string> ShippedDefaults = new Dictionary<string, string>
    {
        ["implement"] = "baton-implement",
        ["review"] = "baton-review",
        ["advise"] = "baton-advise",
    };

    private static string ShippedSkillsDirectory => Path.Combine(AppContext.BaseDirectory, "skills");

    [Theory]
    [InlineData("implement")]
    [InlineData("review")]
    [InlineData("advise")]
    public void A_roles_default_skill_is_attached_with_no_skill_flag_at_all(string roleId)
    {
        var binding = RoleDispatch.ToBinding(WorkerRoleCatalog.For(roleId), "Do the work.");

        Assert.Equal([ShippedDefaults[roleId]], binding.Skills!.ToArray());
    }

    [Fact]
    public void Every_other_shipped_role_declares_no_default()
    {
        foreach (var role in WorkerRoleCatalog.All.Where(r => !ShippedDefaults.ContainsKey(r.Id)))
        {
            Assert.Empty(role.DefaultSkills);
            Assert.Null(RoleDispatch.ToBinding(role, "Do the work.").Skills);
        }
    }

    [Fact]
    public void A_named_skill_is_added_after_the_default_rather_than_replacing_it()
    {
        using var scope = ExtraPackageScope("house-style");

        var binding = RoleDispatch.ToBinding(WorkerRoleCatalog.For("review"), "Review it.", skills: ["house-style"]);

        Assert.Equal(["baton-review", "house-style"], binding.Skills!.ToArray());
    }

    [Fact]
    public void Naming_the_default_again_attaches_it_once_in_the_defaults_slot()
    {
        using var scope = ExtraPackageScope("house-style");

        var binding = RoleDispatch.ToBinding(
            WorkerRoleCatalog.For("review"), "Review it.", skills: ["house-style", "baton-review"]);

        Assert.Equal(["baton-review", "house-style"], binding.Skills!.ToArray());
    }

    [Fact]
    public void Opting_out_removes_the_default_and_keeps_what_the_operator_named()
    {
        using var scope = ExtraPackageScope("house-style");
        var review = WorkerRoleCatalog.For("review");

        Assert.Null(RoleDispatch.ToBinding(review, "Review it.", attachDefaultSkills: false).Skills);
        Assert.Equal(
            ["house-style"],
            RoleDispatch.ToBinding(review, "Review it.", skills: ["house-style"], attachDefaultSkills: false).Skills!.ToArray());
    }

    [Fact]
    public void A_template_phase_carries_its_roles_default_and_the_opt_out_reaches_every_phase()
    {
        var template = WorkflowTemplateCatalog.All.First(t => t.Phases.Any(p => p.RoleId == "review"));
        var reviewPhase = template.Phases.First(p => p.RoleId == "review").Name;

        var (_, attached) = WorkflowTemplateComposer.Materialize(template);
        Assert.Equal(["baton-review"], attached[reviewPhase].Skills!.ToArray());

        var (_, optedOut) = WorkflowTemplateComposer.Materialize(template, attachDefaultSkills: false);
        Assert.All(optedOut.Values, binding => Assert.Null(binding.Skills));
    }

    [Fact]
    public void An_unknown_default_refuses_at_bind_time_naming_the_role_and_the_package()
    {
        const string tiers = """{"t":{"adapter":"claude","model":null,"effort":null}}""";
        const string roles = """
            [{"id":"r","tier":"t","read_files":true,"write_files":false,"run_shell_commands":false,
              "network_access":false,"timeout_minutes":5,"verdict_schema":false,"purpose":"p",
              "default_skills":["no-such-skill-2110"],
              "outputs":[{"name":"out.md","schema":"none","instruction":"Write out.md."}]}]
            """;
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with
        {
            WorkerTiersPathOverride = Write("tiers.json", tiers),
            WorkerRolesPathOverride = Write("roles.json", roles),
            HomeOverride = Path.Combine(_root, "home"),
        });
        var role = WorkerRoleCatalog.For("r");
        Assert.Equal(["no-such-skill-2110"], role.DefaultSkills.ToArray());

        var ex = Assert.Throws<UnknownSkillPackageException>(() => RoleDispatch.ToBinding(role, "Do it."));

        Assert.Equal("no-such-skill-2110", ex.SkillName);
        Assert.Equal("r", ex.DeclaredByRole);
        Assert.Contains("'no-such-skill-2110'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("role 'r'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("--no-default-skills", ex.TryInvocation, StringComparison.Ordinal);
        // The opt-out is the remedy the message names, so it has to actually bind.
        Assert.Null(RoleDispatch.ToBinding(role, "Do it.", attachDefaultSkills: false).Skills);
    }

    [Fact]
    public void A_blank_default_entry_fails_loudly_at_catalog_load()
    {
        const string tiers = """{"t":{"adapter":"claude","model":null,"effort":null}}""";
        const string roles = """
            [{"id":"r","tier":"t","read_files":true,"write_files":false,"run_shell_commands":false,
              "network_access":false,"timeout_minutes":5,"verdict_schema":false,"purpose":"p",
              "default_skills":["baton-review",""],
              "outputs":[{"name":"out.md","schema":"none","instruction":"Write out.md."}]}]
            """;
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with
        {
            WorkerTiersPathOverride = Write("tiers.json", tiers),
            WorkerRolesPathOverride = Write("roles.json", roles),
        });

        var ex = Assert.Throws<InvalidOperationException>(() => WorkerRoleCatalog.For("r"));

        Assert.Contains("'r'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("default_skills", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("baton-implement")]
    [InlineData("baton-review")]
    [InlineData("baton-advise")]
    public void Each_shipped_package_loads_lint_clean_from_the_next_to_the_assembly_rung(string name)
    {
        var package = SkillPackageReader.LoadPackage(Path.Combine(ShippedSkillsDirectory, name));

        Assert.Equal(name, package.Name);
        Assert.Null(SkillPackageLint.Check(package));
        Assert.NotNull(package.Manifest);
        Assert.False(string.IsNullOrWhiteSpace(package.Description));
    }

    /// <summary>
    /// The size bound the issue set (~120 lines) and the "nothing task-specific" rule, both pinned
    /// mechanically: an issue or PR number in a standing package is the signature of a brief's task
    /// text having been pasted in. The one reference a package legitimately makes is to a spec
    /// register entry (<c>C-15</c>), which carries no <c>#</c>.
    /// </summary>
    [Theory]
    [InlineData("baton-implement")]
    [InlineData("baton-review")]
    [InlineData("baton-advise")]
    public void Each_shipped_package_stays_short_and_carries_nothing_task_specific(string name)
    {
        var package = SkillPackageReader.LoadPackage(Path.Combine(ShippedSkillsDirectory, name));
        var lines = package.Content.Split('\n');

        Assert.InRange(lines.Length, 1, 120);
        Assert.DoesNotMatch(new Regex(@"#\d{3,}"), package.Content);
        Assert.DoesNotMatch(new Regex(@"\bPR #|\bissue #", RegexOptions.IgnoreCase), package.Content);
        // The inlining vendors bound a DECLARED set at CoreDispatcher.OversizePromptThreshold (the
        // SkillInlining remark is the register): a default over it turns every codex/agy dispatch of
        // the role into a refusal, and headroom under it is what a `--skill` addition has to fit in.
        Assert.InRange(SkillInlining.InlinedSkillBody(package).Length, 1, Baton.Dispatch.CoreDispatcher.OversizePromptThreshold - 400);
    }

    /// <summary>
    /// The two code-facing packages cite <c>AGENTS.md</c> rather than restating it: the check names
    /// the file and states the lane-side form. <c>baton-advise</c> runs no code change and so carries
    /// neither check — asserted as the negative so a paste of the implement block into it is loud.
    /// </summary>
    [Fact]
    public void The_two_code_facing_packages_cite_AGENTS_md_and_the_advise_package_does_not()
    {
        string Content(string name) => SkillPackageReader.LoadPackage(Path.Combine(ShippedSkillsDirectory, name)).Content;

        Assert.Contains("AGENTS.md", Content("baton-implement"), StringComparison.Ordinal);
        Assert.Contains("AGENTS.md", Content("baton-review"), StringComparison.Ordinal);
        Assert.DoesNotContain("AGENTS.md", Content("baton-advise"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("baton-implement", "read_files")]
    [InlineData("baton-implement", "write_files")]
    [InlineData("baton-implement", "run_shell_commands")]
    public void The_implement_package_requires_what_the_implement_role_grants(string name, string requirement)
    {
        var package = SkillPackageReader.LoadPackage(Path.Combine(ShippedSkillsDirectory, name));
        var grant = WorkerRoleCatalog.For("implement").Grant;

        Assert.Empty(package.Requires.MissingFrom(grant));
        var declared = requirement switch
        {
            "read_files" => package.Requires.ReadFiles,
            "write_files" => package.Requires.WriteFiles,
            _ => package.Requires.RunShellCommands,
        };
        Assert.True(declared);
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>
    /// A second package reachable through the override rung, scoped from <c>Current</c> so the shipped
    /// catalog and the next-to-the-assembly rung both stay visible underneath it.
    /// </summary>
    private IDisposable ExtraPackageScope(string name)
    {
        var library = Path.Combine(_root, "library");
        Directory.CreateDirectory(Path.Combine(library, name));
        File.WriteAllText(Path.Combine(library, name, "SKILL.md"), $"---\ndescription: {name}\n---\n# {name}\nBe brief.");
        return BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Current with { SkillsPathOverride = library });
    }
}
