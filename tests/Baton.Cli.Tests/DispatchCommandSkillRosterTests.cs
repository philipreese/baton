using Baton.Cli;
using Baton.Cli.Tests.TestSupport;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Vendors;
using Xunit;

namespace Baton.Cli.Tests;

public sealed class DispatchCommandSkillRosterTests
{
    private sealed class DelegatingDiscoveryWorkerAdapter(
        IWorkerAdapter discoveryDelegate,
        bool satisfyOutputs = true) : IWorkerAdapter
    {
        public Task<WorkerCapabilities> DiscoverCapabilitiesAsync(string? workingDirectory = null, CancellationToken cancellationToken = default) =>
            discoveryDelegate.DiscoverCapabilitiesAsync(workingDirectory, cancellationToken);

        public CoreDispatchTarget Resolve(WorkerInvocation invocation, WorkerContract contract)
        {
            var script = satisfyOutputs && contract.ProducedOutputs.Count > 0
                ? string.Join(" & ", contract.ProducedOutputs.Select(o => $"echo x>%BATON_OUTPUT_DIR%\\{o.Name}"))
                : "exit 0";

            return new CoreDispatchTarget("cmd", ["/c", script], invocation.WorkingDirectory);
        }
    }

    [Fact]
    public async Task Dispatch_PrintsSameSkillListForBothVendorsWithRespectiveRealization()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-roster-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testRoot);
        var originalOut = Console.Out;
        try
        {
            var workspace = Path.Combine(testRoot, "workspace");
            Directory.CreateDirectory(workspace);

            var skill1Dir = Path.Combine(workspace, "skills", "alpha-skill");
            Directory.CreateDirectory(skill1Dir);
            File.WriteAllText(Path.Combine(skill1Dir, "SKILL.md"), "description: Alpha skill instructions");

            var skill2Dir = Path.Combine(workspace, "skills", "beta-skill");
            Directory.CreateDirectory(skill2Dir);
            File.WriteAllText(Path.Combine(skill2Dir, "SKILL.md"), "description: Beta skill instructions");

            var specPath = await WriteSpecAsync(testRoot, "Review the change.");

            // 1. Claude dispatch
            var claudeRoom = Path.Combine(testRoot, "room-claude");
            var claudeOptions = new DispatchOptions("review", specPath, claudeRoom, Adapter: "claude-worker", WorkspaceDirectory: workspace);
            var claudeAdapters = new Dictionary<string, IWorkerAdapter>
            {
                ["claude-worker"] = new DelegatingDiscoveryWorkerAdapter(new ClaudeWorkerAdapter()),
            };

            using var claudeOutput = new StringWriter();
            Console.SetOut(claudeOutput);
            await DispatchCommand.ExecuteAsync(claudeOptions, claudeAdapters, TestContext.Current.CancellationToken);
            Console.SetOut(originalOut);

            var claudeText = claudeOutput.ToString();
            Assert.Contains("Skills: alpha-skill (projected), beta-skill (projected)", claudeText);

            // 2. Agy dispatch
            var agyRoom = Path.Combine(testRoot, "room-agy");
            var agyOptions = new DispatchOptions("review", specPath, agyRoom, Adapter: "agy-worker", WorkspaceDirectory: workspace);
            var agyAdapters = new Dictionary<string, IWorkerAdapter>
            {
                ["agy-worker"] = new DelegatingDiscoveryWorkerAdapter(new AgyWorkerAdapter()),
            };

            using var agyOutput = new StringWriter();
            Console.SetOut(agyOutput);
            await DispatchCommand.ExecuteAsync(agyOptions, agyAdapters, TestContext.Current.CancellationToken);
            Console.SetOut(originalOut);

            var agyText = agyOutput.ToString();
            Assert.Contains("Skills: alpha-skill (inlined), beta-skill (inlined)", agyText);
        }
        finally
        {
            Console.SetOut(originalOut);
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Dispatch_Control_WhenNoSkills_PrintsNoneDiscoveredForBothVendors()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-roster-empty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testRoot);
        var originalOut = Console.Out;
        try
        {
            var workspace = Path.Combine(testRoot, "empty-workspace");
            Directory.CreateDirectory(workspace);

            var specPath = await WriteSpecAsync(testRoot, "Review the change.");

            // Claude dispatch with empty workspace
            var claudeRoom = Path.Combine(testRoot, "room-claude");
            var claudeOptions = new DispatchOptions("review", specPath, claudeRoom, Adapter: "claude-worker", WorkspaceDirectory: workspace);
            var claudeAdapters = new Dictionary<string, IWorkerAdapter>
            {
                ["claude-worker"] = new DelegatingDiscoveryWorkerAdapter(new ClaudeWorkerAdapter()),
            };

            using var claudeOutput = new StringWriter();
            Console.SetOut(claudeOutput);
            await DispatchCommand.ExecuteAsync(claudeOptions, claudeAdapters, TestContext.Current.CancellationToken);
            Console.SetOut(originalOut);

            Assert.Contains("Skills: none discovered", claudeOutput.ToString());

            // Agy dispatch with empty workspace
            var agyRoom = Path.Combine(testRoot, "room-agy");
            var agyOptions = new DispatchOptions("review", specPath, agyRoom, Adapter: "agy-worker", WorkspaceDirectory: workspace);
            var agyAdapters = new Dictionary<string, IWorkerAdapter>
            {
                ["agy-worker"] = new DelegatingDiscoveryWorkerAdapter(new AgyWorkerAdapter()),
            };

            using var agyOutput = new StringWriter();
            Console.SetOut(agyOutput);
            await DispatchCommand.ExecuteAsync(agyOptions, agyAdapters, TestContext.Current.CancellationToken);
            Console.SetOut(originalOut);

            Assert.Contains("Skills: none discovered", agyOutput.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    private static async Task<string> WriteSpecAsync(string directory, string content)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "spec.md");
        await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
        return path;
    }
}
