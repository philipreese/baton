using Baton.Vendors;

namespace Baton.Vendors.Tests;

public sealed class ClaudeSkillRealizationTests
{
    [Fact]
    public void ProjectSkills_CanonicalPackages_ProjectsIntoDotClaudeSkills()
    {
        var tempWorkspace = Path.Combine(Path.GetTempPath(), $"claude-realize-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempWorkspace);
        try
        {
            var skillDir = Path.Combine(tempWorkspace, "skills", "linter-skill");
            Directory.CreateDirectory(skillDir);
            File.WriteAllText(
                Path.Combine(skillDir, "SKILL.md"),
                """
                ---
                description: Linting skill
                ---
                Run the linter.
                """);
            File.WriteAllText(Path.Combine(skillDir, "rules.json"), "{\"rule\": true}");

            var projected = ClaudeWorkerAdapter.ProjectSkills(tempWorkspace);

            Assert.NotEmpty(projected);
            var targetSkillFile = Path.Combine(tempWorkspace, ".claude", "skills", "linter-skill", "SKILL.md");
            var targetConfigFile = Path.Combine(tempWorkspace, ".claude", "skills", "linter-skill", "rules.json");

            Assert.True(File.Exists(targetSkillFile));
            Assert.True(File.Exists(targetConfigFile));
            Assert.Contains("Run the linter.", File.ReadAllText(targetSkillFile));
            Assert.Contains("{\"rule\": true}", File.ReadAllText(targetConfigFile));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempWorkspace);
        }
    }

    [Fact]
    public void ProjectSkills_Control_WhenNoCanonicalSkills_ProjectsNothing()
    {
        var tempWorkspace = Path.Combine(Path.GetTempPath(), $"claude-realize-empty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempWorkspace);
        try
        {
            var projected = ClaudeWorkerAdapter.ProjectSkills(tempWorkspace);

            Assert.Empty(projected);
            var dotClaudeSkills = Path.Combine(tempWorkspace, ".claude", "skills");
            Assert.False(Directory.Exists(dotClaudeSkills));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempWorkspace);
        }
    }

    [Fact]
    public async Task DiscoverCapabilities_WithCanonicalSkills_ReportsAsProjected()
    {
        var tempWorkspace = Path.Combine(Path.GetTempPath(), $"claude-disc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempWorkspace);
        var emptyUserHome = Path.Combine(Path.GetTempPath(), $"claude-home-{Guid.NewGuid():N}");
        Directory.CreateDirectory(emptyUserHome);
        try
        {
            var skillDir = Path.Combine(tempWorkspace, "skills", "audit-tool");
            Directory.CreateDirectory(skillDir);
            File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "description: Audit tool skill");

            var adapter = new ClaudeWorkerAdapter();
            var caps = await adapter.DiscoverCapabilitiesAsync(
                workingDirectory: tempWorkspace,
                userHomeDirectory: emptyUserHome,
                configRootDirectory: string.Empty,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Contains(caps.Items, i => i.Name == "audit-tool (projected)" && i.Kind == "skill" && i.Description == "Audit tool skill");
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
