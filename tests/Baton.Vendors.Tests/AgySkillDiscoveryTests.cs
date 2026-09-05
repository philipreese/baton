using Baton.Vendors;

namespace Baton.Vendors.Tests;

public sealed class AgySkillDiscoveryTests
{
    [Fact]
    public async Task DiscoverCapabilities_WorkspaceArm_FindsCanonicalSkillsAsInlined()
    {
        var tempWorkspace = Path.Combine(Path.GetTempPath(), $"agy-workspace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempWorkspace);
        try
        {
            var skillsDir = Path.Combine(tempWorkspace, "skills", "agy-test-skill");
            Directory.CreateDirectory(skillsDir);
            File.WriteAllText(Path.Combine(skillsDir, "SKILL.md"), "description: Agy skill in workspace");

            var adapter = new AgyWorkerAdapter();
            var caps = await adapter.DiscoverCapabilitiesAsync(
                workingDirectory: tempWorkspace,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal("agy", caps.Vendor);
            Assert.Contains(caps.Items, i => i.Name == "agy-test-skill (inlined)" && i.Kind == "skill" && i.Description == "Agy skill in workspace");
            Assert.Contains(caps.Items, i => i.Name == "/compact" && i.Kind == "command");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempWorkspace);
        }
    }

    [Fact]
    public async Task DiscoverCapabilities_DoesNotDiscoverFromAgentsSkillsDirectory()
    {
        // #1572: the vendor ignores .agents/skills during execution, so capability discovery skips that tree.
        var tempWorkspace = Path.Combine(Path.GetTempPath(), $"agy-workspace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempWorkspace);
        try
        {
            var unreadSkillsDir = Path.Combine(tempWorkspace, ".agents", "skills", "sentinel-1572");
            Directory.CreateDirectory(unreadSkillsDir);
            File.WriteAllText(Path.Combine(unreadSkillsDir, "SKILL.md"), "description: Sentinel skill");

            var adapter = new AgyWorkerAdapter();
            var caps = await adapter.DiscoverCapabilitiesAsync(
                workingDirectory: tempWorkspace,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.DoesNotContain(caps.Items, i => i.Name.Contains("sentinel-1572", StringComparison.Ordinal));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempWorkspace);
        }
    }

    [Fact]
    public async Task DiscoverCapabilities_NullOrEmptyWorkspace_ReturnsStandardCapabilitiesWithoutSkills()
    {
        var adapter = new AgyWorkerAdapter();
        var caps = await adapter.DiscoverCapabilitiesAsync(
            workingDirectory: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("agy", caps.Vendor);
        Assert.DoesNotContain(caps.Items, i => i.Kind == "skill");
        Assert.Contains(caps.Items, i => i.Name == "/compact" && i.Kind == "command");
    }
}
