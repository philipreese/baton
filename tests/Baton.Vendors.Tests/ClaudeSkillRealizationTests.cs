using Baton.Dispatch;
using Baton.Domain;
using Baton.Status;
using Baton.Tests.Shared;

namespace Baton.Vendors.Tests;

/// <summary>
/// #1151's claude floor realization, and the #1929 review's HIGH: the projection must place nothing
/// while a binding is merely being resolved, must ride on the returned dispatch target instead, and must
/// never overwrite content the operator already has.
/// </summary>
/// <remarks>
/// <see cref="LaunchConfigCollection"/> for the same reason <c>ClaudeWorkerAdapterTests</c> needs it —
/// driving <c>Resolve</c> writes launch config under the assembly's shared <c>BATON_HOME</c> and touches
/// the shared project-ceiling store.
/// </remarks>
[Collection(LaunchConfigCollection.Name)]
public sealed class ClaudeSkillRealizationTests
{
    private static readonly WorkerContract ArchitectContract = new(
        "architect", ["goal"], [new ProducedOutput("plan.md")], []);

    private static string MakeWorkspaceWithSkill(string prefix, string skillName, string skillBody, string? extraFileBody = null)
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        var skillDir = Path.Combine(workspace, "skills", skillName);
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), skillBody);
        if (extraFileBody is not null)
        {
            File.WriteAllText(Path.Combine(skillDir, "rules.json"), extraFileBody);
        }

        return workspace;
    }

    /// <summary>
    /// The #1929 HIGH stated directly: <c>Resolve</c> is reached once per worker-binding config entry
    /// from <c>baton decide</c>/<c>run</c>/<c>resume</c>, for bindings that may never dispatch, so it
    /// must not write into the operator's working directory. Asserts on the filesystem, because a test
    /// of the planning function alone cannot see a stray write.
    /// </summary>
    [Fact]
    public void Resolve_WithCanonicalPackages_WritesNothingIntoTheWorkingDirectory()
    {
        var workspace = MakeWorkspaceWithSkill("claude-resolve-nowrite", "linter-skill", "description: Linting skill", "{}");
        try
        {
            ProjectCeilingStore.Set(workspace, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);

            new ClaudeWorkerAdapter().Resolve(
                new WorkerInvocation("Draft a plan.", WorkingDirectory: workspace), ArchitectContract);

            Assert.False(Directory.Exists(Path.Combine(workspace, ".claude")));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(workspace);
        }
    }

    /// <summary>
    /// The other half of the same fix: the projection is not dropped, it rides on the returned target as
    /// verbatim seed copies the dispatcher places when an execution actually starts.
    /// </summary>
    [Fact]
    public void Resolve_WithCanonicalPackages_DeclaresOneSeedCopyPerPackageFile()
    {
        var workspace = MakeWorkspaceWithSkill("claude-resolve-seed", "linter-skill", "description: Linting skill", "{\"rule\": true}");
        try
        {
            ProjectCeilingStore.Set(workspace, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);

            var target = new ClaudeWorkerAdapter().Resolve(
                new WorkerInvocation("Draft a plan.", WorkingDirectory: workspace), ArchitectContract);

            var copies = Assert.IsAssignableFrom<IReadOnlyList<CoreDispatchSeedCopy>>(target.SeedCopies);
            var skillTarget = Path.Combine(workspace, ".claude", "skills", "linter-skill");
            Assert.Equal(2, copies.Count);
            Assert.Contains(copies, c => c.PathTemplate == Path.Combine(skillTarget, "SKILL.md")
                && c.SourcePath == Path.Combine(workspace, "skills", "linter-skill", "SKILL.md"));
            Assert.Contains(copies, c => c.PathTemplate == Path.Combine(skillTarget, "rules.json"));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(workspace);
        }
    }

    /// <summary>Control for both arms above: no canonical packages, so nothing is declared.</summary>
    [Fact]
    public void Resolve_Control_WhenNoCanonicalSkills_DeclaresNoSeedCopies()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"claude-resolve-empty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        try
        {
            ProjectCeilingStore.Set(workspace, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);

            var target = new ClaudeWorkerAdapter().Resolve(
                new WorkerInvocation("Draft a plan.", WorkingDirectory: workspace), ArchitectContract);

            Assert.True(target.SeedCopies is null or { Count: 0 });
            Assert.False(Directory.Exists(Path.Combine(workspace, ".claude")));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(workspace);
        }
    }

    /// <summary>
    /// The kept-file arm — the no-clobber rule <c>docs/dispatch.md</c> states, from the planning side:
    /// the file is not scheduled for a copy, and it is counted so the roster can report it.
    /// </summary>
    [Fact]
    public void PlanSkillProjection_WithDifferingExistingFile_KeepsItAndExcludesItFromTheCopies()
    {
        var workspace = MakeWorkspaceWithSkill("claude-plan-kept", "review-checklist", "description: Checklist\nCanonical body.", "{\"a\": 1}");
        try
        {
            var destination = Path.Combine(workspace, ".claude", "skills", "review-checklist");
            Directory.CreateDirectory(destination);
            File.WriteAllText(Path.Combine(destination, "SKILL.md"), "The operator's own hand-written checklist.");

            var plan = ClaudeWorkerAdapter.PlanSkillProjection(workspace);

            var entry = Assert.Single(plan.Entries);
            Assert.Equal(1, entry.KeptFileCount);
            Assert.DoesNotContain(entry.Files, f => f.DestinationPath.EndsWith("SKILL.md", StringComparison.Ordinal));
            Assert.Contains(entry.Files, f => f.DestinationPath.EndsWith("rules.json", StringComparison.Ordinal));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(workspace);
        }
    }

    /// <summary>
    /// Polarity control for the arm above: an identical destination is not "kept" — it is rewritten
    /// harmlessly, so a re-dispatch stays idempotent rather than silently reporting a conflict.
    /// </summary>
    [Fact]
    public void PlanSkillProjection_Control_WithIdenticalExistingFile_KeepsNothing()
    {
        var body = "description: Checklist\nCanonical body.";
        var workspace = MakeWorkspaceWithSkill("claude-plan-same", "review-checklist", body);
        try
        {
            var destination = Path.Combine(workspace, ".claude", "skills", "review-checklist");
            Directory.CreateDirectory(destination);
            File.WriteAllText(Path.Combine(destination, "SKILL.md"), body);

            var plan = ClaudeWorkerAdapter.PlanSkillProjection(workspace);

            var entry = Assert.Single(plan.Entries);
            Assert.Equal(0, entry.KeptFileCount);
            Assert.Contains(entry.Files, f => f.DestinationPath.EndsWith("SKILL.md", StringComparison.Ordinal));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(workspace);
        }
    }

    /// <summary>
    /// #1941 review HIGH, the second unusable-destination shape: a working directory that is SET but
    /// absent from disk. Same reasoning as an unset one — the projection has nowhere to land — and the
    /// refusal says which of the two it hit, since the remedies differ.
    /// </summary>
    [Fact]
    public void PlanSkillProjection_WithDeclaredSkillsAndAMissingWorkingDirectory_Refuses()
    {
        var packageRoot = MakeWorkspaceWithSkill("claude-plan-unplaceable", "house-style", "description: House style");
        try
        {
            var package = SkillPackageReader.LoadPackage(Path.Combine(packageRoot, "skills", "house-style"));
            var absent = Path.Combine(Path.GetTempPath(), $"claude-absent-{Guid.NewGuid():N}");

            var ex = Assert.Throws<SkillProjectionUnplaceableException>(
                () => ClaudeWorkerAdapter.PlanSkillProjection(absent, [package]));
            Assert.Equal(["house-style"], ex.SkillNames.ToArray());
            Assert.Contains(absent, ex.Message, StringComparison.Ordinal);

            // Polarity: the SAME missing directory with nothing declared is not a refusal, it is simply
            // no scan -- #1929's behaviour, which this fix must not turn into an error.
            var plan = ClaudeWorkerAdapter.PlanSkillProjection(absent);
            Assert.Empty(plan.Entries);
            Assert.Empty(ClaudeWorkerAdapter.PlanSkillProjection(null).Entries);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(packageRoot);
        }
    }

    [Fact]
    public async Task DiscoverCapabilities_WithCanonicalSkills_ReportsAsProjected()
    {
        var tempWorkspace = MakeWorkspaceWithSkill("claude-disc", "audit-tool", "description: Audit tool skill");
        var emptyUserHome = Path.Combine(Path.GetTempPath(), $"claude-home-{Guid.NewGuid():N}");
        Directory.CreateDirectory(emptyUserHome);
        try
        {
            var adapter = new ClaudeWorkerAdapter();
            var caps = await adapter.DiscoverCapabilitiesAsync(
                workingDirectory: tempWorkspace,
                userHomeDirectory: emptyUserHome,
                configRootDirectory: string.Empty,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Contains(caps.Items, i => i.Name == "audit-tool (to be projected)" && i.Kind == "skill" && i.Description == "Audit tool skill");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempWorkspace);
            DirectoryCleanup.DeleteRecursively(emptyUserHome);
        }
    }

    /// <summary>
    /// #1929 re-review LOW: a symlinked FILE inside a package is skipped rather than dereferenced, and
    /// so is a linked subdirectory — both halves in one run. What that closes is on
    /// <c>SkillProjection.EnumerateProjectableFiles</c>.
    /// </summary>
    /// <remarks>
    /// Skipped on a host that refuses symlink creation (unprivileged Windows without Developer Mode),
    /// the same capability check <c>CodexDynamicToolPolicyTests</c> uses. The plain files beside the
    /// links are the control: a plan that placed nothing would satisfy the negative assertions alone.
    /// </remarks>
    [Fact]
    public void PlanSkillProjection_SkipsLinkedFilesAndLinkedDirectories()
    {
        var workspace = MakeWorkspaceWithSkill("claude-plan-links", "audit-tool", "description: Audit tool skill");
        var outside = Path.Combine(Path.GetTempPath(), $"claude-link-outside-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outside);
            var outsideFile = Path.Combine(outside, "secret.md");
            File.WriteAllText(outsideFile, "content from outside the repository");

            var packageDirectory = Path.Combine(workspace, "skills", "audit-tool");
            var linkedFile = Path.Combine(packageDirectory, "notes.md");
            var linkedDirectory = Path.Combine(packageDirectory, "reference");
            try
            {
                File.CreateSymbolicLink(linkedFile, outsideFile);
                Directory.CreateSymbolicLink(linkedDirectory, outside);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                return;
            }

            var plan = ClaudeWorkerAdapter.PlanSkillProjection(workspace);
            var entry = Assert.Single(plan.Entries);
            var placed = entry.Files.Select(f => Path.GetFileName(f.SourcePath)).ToList();

            Assert.Equal(["SKILL.md"], placed);
            Assert.DoesNotContain("notes.md", placed);
            Assert.DoesNotContain("secret.md", placed);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(workspace);
            DirectoryCleanup.DeleteRecursively(outside);
        }
    }

    /// <summary>The kept-file arm surfaced where the operator actually reads it.</summary>
    [Fact]
    public async Task DiscoverCapabilities_WithDifferingExistingFile_ReportsTheKeptCount()
    {
        var tempWorkspace = MakeWorkspaceWithSkill("claude-disc-kept", "audit-tool", "description: Audit tool skill");
        var emptyUserHome = Path.Combine(Path.GetTempPath(), $"claude-home-kept-{Guid.NewGuid():N}");
        Directory.CreateDirectory(emptyUserHome);
        try
        {
            var destination = Path.Combine(tempWorkspace, ".claude", "skills", "audit-tool");
            Directory.CreateDirectory(destination);
            await File.WriteAllTextAsync(
                Path.Combine(destination, "SKILL.md"), "Operator's own file.", TestContext.Current.CancellationToken);

            var caps = await new ClaudeWorkerAdapter().DiscoverCapabilitiesAsync(
                workingDirectory: tempWorkspace,
                userHomeDirectory: emptyUserHome,
                configRootDirectory: string.Empty,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Contains(caps.Items, i => i.Name == "audit-tool (to be projected, 1 file(s) to be kept)");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempWorkspace);
            DirectoryCleanup.DeleteRecursively(emptyUserHome);
        }
    }

    /// <summary>
    /// #1929 review MEDIUM: the #1575 precedence pinned in <c>ClaudeWorkerAdapter</c>'s own
    /// <c>DiscoverCapabilitiesCore</c> block decides this collision, and the roster must state it rather
    /// than dedup the live entry away behind a canonical package of the same name.
    /// </summary>
    [Fact]
    public async Task DiscoverCapabilities_UnderConfigRoot_ReportsTheConfigRootCopyAndMarksTheProjectionShadowed()
    {
        var tempWorkspace = MakeWorkspaceWithSkill("claude-disc-shadow", "audit-tool", "description: Canonical audit tool");
        var configRoot = Path.Combine(Path.GetTempPath(), $"claude-cfgroot-{Guid.NewGuid():N}");
        var configRootSkill = Path.Combine(configRoot, "skills", "audit-tool");
        Directory.CreateDirectory(configRootSkill);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(configRootSkill, "SKILL.md"), "description: Config root audit tool", TestContext.Current.CancellationToken);

            var caps = await new ClaudeWorkerAdapter().DiscoverCapabilitiesAsync(
                workingDirectory: tempWorkspace,
                userHomeDirectory: null,
                configRootDirectory: configRoot,
                cancellationToken: TestContext.Current.CancellationToken);

            // The copy the CLI actually loads is present, unsuppressed...
            Assert.Contains(caps.Items, i => i.Name == "audit-tool" && i.Description == "Config root audit tool");
            // ...and the projected one says it is not the live entry.
            Assert.Contains(caps.Items, i => i.Name == "audit-tool (to be projected, shadowed by the config root)");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempWorkspace);
            DirectoryCleanup.DeleteRecursively(configRoot);
        }
    }

    /// <summary>
    /// Polarity control for the arm above: with no config root set, the project arm IS where the
    /// projection lands, so a same-named native project skill is deduped and nothing is called shadowed.
    /// </summary>
    [Fact]
    public async Task DiscoverCapabilities_Control_WithoutConfigRoot_DedupsTheProjectArmAndMarksNothingShadowed()
    {
        var tempWorkspace = MakeWorkspaceWithSkill("claude-disc-noshadow", "audit-tool", "description: Canonical audit tool");
        var emptyUserHome = Path.Combine(Path.GetTempPath(), $"claude-home-noshadow-{Guid.NewGuid():N}");
        Directory.CreateDirectory(emptyUserHome);
        try
        {
            var projectSkill = Path.Combine(tempWorkspace, ".claude", "skills", "audit-tool");
            Directory.CreateDirectory(projectSkill);
            await File.WriteAllTextAsync(
                Path.Combine(projectSkill, "SKILL.md"), "description: Canonical audit tool", TestContext.Current.CancellationToken);

            var caps = await new ClaudeWorkerAdapter().DiscoverCapabilitiesAsync(
                workingDirectory: tempWorkspace,
                userHomeDirectory: emptyUserHome,
                configRootDirectory: string.Empty,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.DoesNotContain(caps.Items, i => i.Name == "audit-tool");
            Assert.Contains(caps.Items, i => i.Name == "audit-tool (to be projected)");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempWorkspace);
            DirectoryCleanup.DeleteRecursively(emptyUserHome);
        }
    }

    [Fact]
    public async Task DiscoverCapabilities_Control_WithoutSkills_ReportsNoSkills()
    {
        var tempWorkspace = Path.Combine(Path.GetTempPath(), $"claude-disc-empty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempWorkspace);
        var emptyUserHome = Path.Combine(Path.GetTempPath(), $"claude-home-empty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(emptyUserHome);
        try
        {
            var adapter = new ClaudeWorkerAdapter();
            var caps = await adapter.DiscoverCapabilitiesAsync(
                workingDirectory: tempWorkspace,
                userHomeDirectory: emptyUserHome,
                configRootDirectory: string.Empty,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.DoesNotContain(caps.Items, i => i.Kind == "skill");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempWorkspace);
            DirectoryCleanup.DeleteRecursively(emptyUserHome);
        }
    }
}
