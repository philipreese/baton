using Baton.Domain;
using Baton.Status;

namespace Baton.Vendors.Tests;

/// <summary>
/// #1151 S1: the half that makes the rest real — a name declared on a binding must reach the vendor's
/// realization. The load-bearing arm is <see cref="A_skill_from_the_account_library_reaches_both_vendors_realizations"/>:
/// a package that exists ONLY on a resolver rung, absent from the workspace, has to show up in the
/// claude projection plan and in the agy prompt. Without it, <c>--skill</c> would resolve, validate,
/// persist — and hand the worker nothing, which is #1512's failure wearing a new hat.
/// </summary>
public sealed class SkillBindingRealizationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"skill-binding-{Guid.NewGuid():N}");
    private readonly string _home;
    private readonly string _homeSkills;
    private readonly string _workspace;

    public SkillBindingRealizationTests()
    {
        _home = Path.Combine(_root, "home");
        _homeSkills = Path.Combine(_home, "skills");
        _workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(_homeSkills);
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose() => Baton.Tests.Shared.DirectoryCleanup.DeleteRecursively(_root);

    private void WriteAccountPackage(string name, string body, string? manifest = null)
    {
        var dir = Path.Combine(_homeSkills, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), body);
        if (manifest is not null)
        {
            File.WriteAllText(Path.Combine(dir, "skill.json"), manifest);
        }
    }

    /// <summary>
    /// Redirects the account library at <c>{BatonPaths.Root}/skills/</c> onto this test's own temp home
    /// — scoped from <c>Current</c>, not <c>Blank</c>, for the reason
    /// <see cref="BatonEnvironmentSnapshot"/>'s own remarks give — and records a project ceiling for the
    /// workspace, since <c>ProjectCeilingGate</c> fails closed on an untrusted directory before any
    /// adapter reads a grant. The ceiling is written INSIDE the scope: its default path is derived from
    /// the redirected home.
    /// </summary>
    private IDisposable AccountLibraryScope()
    {
        var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Current with { SkillsPathOverride = null, HomeOverride = _home });
        ProjectCeilingStore.Set(_workspace, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);
        return scope;
    }

    private static WorkerContract Contract => new("worker", [], [], []);

    [Fact]
    public void A_skill_from_the_account_library_reaches_both_vendors_realizations()
    {
        WriteAccountPackage("house-style", "# House style\nUse short sentences.");
        using var scope = AccountLibraryScope();

        // Nothing under <workspace>/skills/ -- the package is reachable only through the resolver.
        Assert.Empty(SkillPackageReader.DiscoverPackages(_workspace));

        var entry = new WorkerBindingConfigEntry(
            Adapter: "claude", Contract: Contract, PromptTemplate: "Do the work.",
            Timeout: TimeSpan.FromMinutes(5), WorkingDirectory: _workspace,
            Skills: ["house-style"]);

        var claudeBinding = WorkerBindingResolver.Resolve(
            new Dictionary<string, WorkerBindingConfigEntry> { ["worker"] = entry },
            new Dictionary<string, IWorkerAdapter> { ["claude"] = new ClaudeWorkerAdapter() });
        var claudeTarget = Assert.IsType<Baton.Mutation.WorkerBinding.Process>(claudeBinding["worker"]).Target;
        Assert.Contains(
            claudeTarget.SeedCopies ?? [],
            copy => copy.Group == "house-style" && copy.PathTemplate.Contains(".claude", StringComparison.Ordinal));

        var agyBinding = WorkerBindingResolver.Resolve(
            new Dictionary<string, WorkerBindingConfigEntry> { ["worker"] = entry with { Adapter = "agy" } },
            new Dictionary<string, IWorkerAdapter> { ["agy"] = new AgyWorkerAdapter() });
        var agyTarget = Assert.IsType<Baton.Mutation.WorkerBinding.Process>(agyBinding["worker"]).Target;
        Assert.Contains(agyTarget.Args, arg => arg.Contains("# Skill: house-style", StringComparison.Ordinal));
    }

    [Fact]
    public void A_binding_that_names_no_skills_still_discovers_the_workspace_overlay()
    {
        // The control arm for the test above, and the #1929 behaviour this slice must not regress.
        var workspaceSkills = Path.Combine(_workspace, "skills");
        Directory.CreateDirectory(Path.Combine(workspaceSkills, "repo-skill"));
        File.WriteAllText(Path.Combine(workspaceSkills, "repo-skill", "SKILL.md"), "# From the repo");
        using var scope = AccountLibraryScope();

        var entry = new WorkerBindingConfigEntry(
            Adapter: "agy", Contract: Contract, PromptTemplate: "Do the work.",
            Timeout: TimeSpan.FromMinutes(5), WorkingDirectory: _workspace);

        var bindings = WorkerBindingResolver.Resolve(
            new Dictionary<string, WorkerBindingConfigEntry> { ["worker"] = entry },
            new Dictionary<string, IWorkerAdapter> { ["agy"] = new AgyWorkerAdapter() });

        var target = Assert.IsType<Baton.Mutation.WorkerBinding.Process>(bindings["worker"]).Target;
        Assert.Contains(target.Args, arg => arg.Contains("# Skill: repo-skill", StringComparison.Ordinal));
    }

    [Fact]
    public void A_declared_skill_set_replaces_the_workspace_scan_rather_than_adding_to_it()
    {
        var workspaceSkills = Path.Combine(_workspace, "skills");
        Directory.CreateDirectory(Path.Combine(workspaceSkills, "repo-skill"));
        File.WriteAllText(Path.Combine(workspaceSkills, "repo-skill", "SKILL.md"), "# From the repo");
        WriteAccountPackage("house-style", "# House style");
        using var scope = AccountLibraryScope();

        var entry = new WorkerBindingConfigEntry(
            Adapter: "agy", Contract: Contract, PromptTemplate: "Do the work.",
            Timeout: TimeSpan.FromMinutes(5), WorkingDirectory: _workspace, Skills: ["house-style"]);

        var bindings = WorkerBindingResolver.Resolve(
            new Dictionary<string, WorkerBindingConfigEntry> { ["worker"] = entry },
            new Dictionary<string, IWorkerAdapter> { ["agy"] = new AgyWorkerAdapter() });

        var target = Assert.IsType<Baton.Mutation.WorkerBinding.Process>(bindings["worker"]).Target;
        Assert.Contains(target.Args, arg => arg.Contains("# Skill: house-style", StringComparison.Ordinal));
        Assert.DoesNotContain(target.Args, arg => arg.Contains("# Skill: repo-skill", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unknown_skill_on_a_hand_authored_binding_refuses_at_resolve()
    {
        using var scope = AccountLibraryScope();

        var entry = new WorkerBindingConfigEntry(
            Adapter: "agy", Contract: Contract, PromptTemplate: "Do the work.",
            Timeout: TimeSpan.FromMinutes(5), WorkingDirectory: _workspace, Skills: ["not-a-skill"]);

        Assert.Throws<UnknownSkillPackageException>(() => WorkerBindingResolver.Resolve(
            new Dictionary<string, WorkerBindingConfigEntry> { ["worker"] = entry },
            new Dictionary<string, IWorkerAdapter> { ["agy"] = new AgyWorkerAdapter() }));
    }

    [Fact]
    public void A_requirement_the_grant_withholds_refuses_at_resolve_and_a_satisfied_one_does_not()
    {
        WriteAccountPackage("needs-shell", "# Body", """{ "description": "d", "requires": { "run_shell_commands": true } }""");
        using var scope = AccountLibraryScope();

        var readOnly = new WorkerBindingConfigEntry(
            Adapter: "agy", Contract: Contract, PromptTemplate: "Do the work.",
            Timeout: TimeSpan.FromMinutes(5), WorkingDirectory: _workspace,
            PermissionGrant: new PermissionGrant(ReadFiles: true), Skills: ["needs-shell"]);

        var ex = Assert.Throws<SkillRequirementUnsatisfiedException>(() => WorkerBindingResolver.Resolve(
            new Dictionary<string, WorkerBindingConfigEntry> { ["worker"] = readOnly },
            new Dictionary<string, IWorkerAdapter> { ["agy"] = new AgyWorkerAdapter() }));
        Assert.Equal("needs-shell", ex.SkillName);
        Assert.Equal(["RunShellCommands"], ex.MissingCategories.ToArray());

        // Polarity: the identical package binds when the grant carries the category.
        var granted = readOnly with
        {
            PermissionGrant = new PermissionGrant(ReadFiles: true, WriteFiles: true, RunShellCommands: true, NetworkAccess: true),
        };
        var bindings = WorkerBindingResolver.Resolve(
            new Dictionary<string, WorkerBindingConfigEntry> { ["worker"] = granted },
            new Dictionary<string, IWorkerAdapter> { ["agy"] = new AgyWorkerAdapter() });
        Assert.Single(bindings);
    }

    [Fact]
    public void ToBinding_resolves_checks_and_records_the_names_and_refuses_a_typo()
    {
        WriteAccountPackage("house-style", "# House style");
        using var scope = AccountLibraryScope();

        var role = WorkerRoleCatalog.For("review");

        var entry = RoleDispatch.ToBinding(
            role, "Review the diff.", workingDirectory: _workspace, skills: ["house-style"]);

        Assert.Equal(["house-style"], entry.Skills!.ToArray());

        // A typo refuses HERE -- before DispatchCommand creates a room directory.
        Assert.Throws<UnknownSkillPackageException>(() => RoleDispatch.ToBinding(
            role, "Review the diff.", workingDirectory: _workspace, skills: ["huose-style"]));
    }

    [Fact]
    public void ToBinding_checks_a_requirement_against_the_roles_own_catalog_grant()
    {
        // review is read-only in the catalog; a package demanding writes must refuse rather than ride
        // the audited write-widening ToBinding applies for outbox reachability.
        WriteAccountPackage("needs-write", "# Body", """{ "description": "d", "requires": { "write_files": true } }""");
        using var scope = AccountLibraryScope();

        var role = WorkerRoleCatalog.For("review");
        Assert.False(role.Grant.WriteFiles);

        var ex = Assert.Throws<SkillRequirementUnsatisfiedException>(() => RoleDispatch.ToBinding(
            role, "Review the diff.", workingDirectory: _workspace, skills: ["needs-write"]));
        Assert.Equal(["WriteFiles"], ex.MissingCategories.ToArray());
    }

    [Fact]
    public void The_skills_field_round_trips_through_the_bindings_parser_and_writer()
    {
        var entry = new WorkerBindingConfigEntry(
            Adapter: "claude", Contract: Contract, PromptTemplate: "Do the work.",
            Timeout: TimeSpan.FromMinutes(5), Skills: ["alpha", "beta"]);

        // Through the production writer, not a bare JsonSerializer call: the writer owns its own
        // JsonSerializerOptions, so a raw serialize would assert a shape no dispatch actually emits.
        var json = WorkerBindingConfigWriter.Serialize(
            new Dictionary<string, WorkerBindingConfigEntry> { ["worker"] = entry });
        var parsed = WorkerBindingConfigParser.Parse(json);

        Assert.Equal(["alpha", "beta"], parsed["worker"].Skills!.ToArray());

        // And an entry authored before the field existed still parses, with Skills null rather than
        // an empty list standing in for "declared none".
        var legacy = WorkerBindingConfigParser.Parse(
            """
            { "worker": { "Adapter": "claude", "Contract": { "WorkerName": "worker", "RequiredInputs": [], "ProducedOutputs": [], "OptionalMetadata": [] }, "PromptTemplate": "p", "Timeout": "00:05:00" } }
            """);
        Assert.Null(legacy["worker"].Skills);
    }

    [Fact]
    public void Native_preferred_is_recorded_and_still_realized_as_the_floor()
    {
        // #1151 deliverable 7: the field parses and is carried, and behaves exactly as floor until
        // S3/S4. The discriminating observation is that the realization is byte-identical to a
        // floor-declaring package's.
        WriteAccountPackage("opt-in", "# Body", """{ "description": "d", "realization": "native-preferred" }""");
        WriteAccountPackage("plain", "# Body", """{ "description": "d", "realization": "floor" }""");
        using var scope = AccountLibraryScope();

        var optIn = SkillPackageResolver.Resolve("opt-in", null);
        var plain = SkillPackageResolver.Resolve("plain", null);
        Assert.Equal(SkillRealization.NativePreferred, optIn.Realization);
        Assert.Equal(SkillRealization.Floor, plain.Realization);

        Assert.Equal(
            AgyWorkerAdapter.InlineSkills("Brief.", null, [plain]).Replace("plain", "opt-in", StringComparison.Ordinal),
            AgyWorkerAdapter.InlineSkills("Brief.", null, [optIn]));
    }
}
