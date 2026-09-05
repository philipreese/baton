using Baton.Vendors;

namespace Baton.Vendors.Tests;

public sealed class AgySkillRealizationTests
{
    [Fact]
    public void InlineSkills_CanonicalPackages_InlinesWithOneLineHeaderPerSkill()
    {
        var tempWorkspace = Path.Combine(Path.GetTempPath(), $"agy-realize-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempWorkspace);
        try
        {
            var skill1Dir = Path.Combine(tempWorkspace, "skills", "first-skill");
            Directory.CreateDirectory(skill1Dir);
            File.WriteAllText(
                Path.Combine(skill1Dir, "SKILL.md"),
                """
                ---
                description: First skill
                ---
                First skill body.
                """);

            var skill2Dir = Path.Combine(tempWorkspace, "skills", "second-skill");
            Directory.CreateDirectory(skill2Dir);
            File.WriteAllText(
                Path.Combine(skill2Dir, "SKILL.md"),
                """
                Second skill body.
                """);

            var prompt = "Base prompt instructions.";
            var inlined = AgyWorkerAdapter.InlineSkills(prompt, tempWorkspace);

            Assert.Contains("Base prompt instructions.", inlined);
            Assert.Contains("\n\n# Skill: first-skill\n", inlined);
            Assert.Contains("First skill body.", inlined);
            Assert.Contains("\n\n# Skill: second-skill\n", inlined);
            Assert.Contains("Second skill body.", inlined);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempWorkspace);
        }
    }

    [Fact]
    public void InlineSkills_Control_WhenNoCanonicalSkills_LeavesPromptUnchanged()
    {
        var tempWorkspace = Path.Combine(Path.GetTempPath(), $"agy-realize-empty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempWorkspace);
        try
        {
            var prompt = "Original prompt untouched.";
            var inlined = AgyWorkerAdapter.InlineSkills(prompt, tempWorkspace);

            Assert.Equal(prompt, inlined);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempWorkspace);
        }
    }

    [Fact]
    public async Task DiscoverCapabilities_WithCanonicalSkills_ReportsAsInlined()
    {
        var tempWorkspace = Path.Combine(Path.GetTempPath(), $"agy-disc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempWorkspace);
        try
        {
            var skillDir = Path.Combine(tempWorkspace, "skills", "summarizer");
            Directory.CreateDirectory(skillDir);
            File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "description: Summarize changes");

            var adapter = new AgyWorkerAdapter();
            var caps = await adapter.DiscoverCapabilitiesAsync(
                workingDirectory: tempWorkspace,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Contains(caps.Items, i => i.Name == "summarizer (inlined)" && i.Kind == "skill" && i.Description == "Summarize changes");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempWorkspace);
        }
    }

    [Fact]
    public async Task DiscoverCapabilities_Control_WithoutSkills_ReportsNoSkills()
    {
        var tempWorkspace = Path.Combine(Path.GetTempPath(), $"agy-disc-empty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempWorkspace);
        try
        {
            var adapter = new AgyWorkerAdapter();
            var caps = await adapter.DiscoverCapabilitiesAsync(
                workingDirectory: tempWorkspace,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.DoesNotContain(caps.Items, i => i.Kind == "skill");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempWorkspace);
        }
    }
}
