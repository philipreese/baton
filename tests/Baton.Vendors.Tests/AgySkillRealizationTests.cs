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

            // #1929 review LOW: the realizer emits the header it controls; the operator's YAML front
            // matter never reaches the prompt as instructions. Both polarities, one condition apart --
            // first-skill has a fence and loses it, second-skill has none and keeps its whole body.
            Assert.DoesNotContain("description: First skill", inlined);
            Assert.DoesNotContain("---", inlined);
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

            // #1929 review LOW: inlining is uncapped, so the roster discloses what each package costs
            // the worker's context. Measured on the inlined body (front matter stripped), not the file.
            Assert.Contains(caps.Items, i => i.Name == "summarizer (inlined, 30 B)" && i.Kind == "skill" && i.Description == "Summarize changes");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempWorkspace);
        }
    }

    /// <summary>
    /// The size the roster prints is the size the prompt actually gains — measured on the same string
    /// <see cref="AgyWorkerAdapter.InlineSkills"/> appends, so front matter cannot inflate it.
    /// </summary>
    [Fact]
    public async Task DiscoverCapabilities_SizeExcludesTheStrippedFrontmatter()
    {
        var tempWorkspace = Path.Combine(Path.GetTempPath(), $"agy-disc-size-{Guid.NewGuid():N}");
        var skillDir = Path.Combine(tempWorkspace, "skills", "summarizer");
        Directory.CreateDirectory(skillDir);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(skillDir, "SKILL.md"),
                "---\ndescription: Summarize changes\n---\nSummarize changes.",
                TestContext.Current.CancellationToken);

            var caps = await new AgyWorkerAdapter().DiscoverCapabilitiesAsync(
                workingDirectory: tempWorkspace,
                cancellationToken: TestContext.Current.CancellationToken);

            // "Summarize changes." is 18 bytes; the 40-byte file is not what gets inlined.
            Assert.Contains(caps.Items, i => i.Name == "summarizer (inlined, 18 B)");
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
