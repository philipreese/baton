using System.Text.RegularExpressions;
using Baton.Cli;
using Baton.Cli.Tests.TestSupport;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Status;
using Baton.Vendors;
using Xunit;

namespace Baton.Cli.Tests;

[Collection(ConsoleOutCaptureCollection.Name)]
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

    private const string SkillLinePrefix = "Skills: ";

    /// <summary>The payload of the roster line in <paramref name="output"/>.</summary>
    private static string SkillLine(string output) =>
        output
            .Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .First(l => l.StartsWith(SkillLinePrefix, StringComparison.Ordinal))[SkillLinePrefix.Length..]
            .Trim();

    /// <summary>
    /// Each realized entry on a roster line, split into its package name and its realization suffix.
    /// Parsed with a pattern rather than a comma split because a suffix may itself carry a comma
    /// (claude's kept-count suffix — <c>docs/dispatch.md</c> has the shape — and <c>(inlined, 30 B)</c>).
    /// </summary>
    private static IReadOnlyList<(string Name, string Realization)> RealizedSkills(string output) =>
    [
        .. RealizedEntry.Matches(SkillLine(output))
            .Select(m => (m.Groups["name"].Value, m.Groups["realization"].Value))
    ];

    private static readonly Regex RealizedEntry = new(
        @"(?:^|,\s*)(?<name>[^,()]+?)\s*\((?<realization>to be projected|inlined)[^)]*\)",
        RegexOptions.Compiled);

    private static IReadOnlyList<string> SkillNames(string output) =>
        [.. RealizedSkills(output).Select(entry => entry.Name)];

    /// <summary>
    /// #1929 review MEDIUM: this used to make two independent <c>Assert.Contains</c> calls against two
    /// separate outputs, so the "same package list for both vendors" in its own name was never checked —
    /// a real asymmetry between the rosters would have passed. It now compares the two lists.
    /// </summary>
    /// <remarks>
    /// <c>DispatchCommand</c> calls the two-argument <c>DiscoverCapabilitiesAsync</c>, so the claude arm
    /// would otherwise scan the running machine's real <c>~/.claude/skills</c> and a developer's personal
    /// skill would break an equality assertion for a reason having nothing to do with the claim. An empty
    /// <c>BATON_CLAUDE_CONFIG_ROOT</c> replaces that arm wholesale (#1512 M3), which is what makes the
    /// comparison safe. The scope is per-async-flow, not a process mutation, so no serialized-environment
    /// enrollment is needed. Both arms of the config-root <em>collision</em> rule live in
    /// <c>ClaudeSkillRealizationTests</c>, deliberately not here: an extra shadowed entry would break this
    /// equality for an unrelated reason.
    /// </remarks>
    [Fact]
    public async Task Dispatch_PrintsSameSkillListForBothVendorsWithRespectiveRealization()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-roster-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testRoot);
        var originalOut = Console.Out;
        var emptyConfigRoot = Path.Combine(testRoot, "claude-config-root");
        Directory.CreateDirectory(emptyConfigRoot);
        using var environmentScope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Current with { ClaudeConfigRootOverride = emptyConfigRoot });
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
            Assert.Equal("alpha-skill (to be projected), beta-skill (to be projected)", SkillLine(claudeText));

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

            // The claim in this test's name, actually checked: one package list, two realizations.
            Assert.Equal(SkillNames(claudeText), SkillNames(agyText));
            Assert.Equal(["alpha-skill", "beta-skill"], SkillNames(claudeText));
            Assert.All(RealizedSkills(claudeText), entry => Assert.Equal("to be projected", entry.Realization));
            Assert.All(RealizedSkills(agyText), entry => Assert.Equal("inlined", entry.Realization));
        }
        finally
        {
            Console.SetOut(originalOut);
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// The control arm for the test above, isolated the SAME way (#1929 re-review LOW). It asserts
    /// <c>none discovered</c>, which is strictly more brittle than an equality between two rosters: any
    /// personal skill on the running host — or an operator machine with <c>BATON_CLAUDE_CONFIG_ROOT</c>
    /// set per <c>docs/runbooks/claude-shared-config-root.md</c> — would otherwise fail it for a reason
    /// unrelated to the claim. It previously passed here only because this machine happens to have no
    /// <c>~/.claude/skills</c>. An empty config-root scope replaces the user-home arm wholesale
    /// (#1512 M3: <c>DiscoverCapabilitiesCore</c> takes the config-root branch instead of
    /// <c>Environment.GetFolderPath(UserProfile)</c>), so both ambient sources are closed by one scope.
    /// </summary>
    [Fact]
    public async Task Dispatch_Control_WhenNoSkills_PrintsNoneDiscoveredForBothVendors()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-roster-empty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testRoot);
        var originalOut = Console.Out;
        var emptyConfigRoot = Path.Combine(testRoot, "claude-config-root");
        Directory.CreateDirectory(emptyConfigRoot);
        using var environmentScope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Current with { ClaudeConfigRootOverride = emptyConfigRoot });
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

    /// <summary>
    /// #1941 review MEDIUM: a binding that declares its own skill set gets exactly that set — the
    /// declared set REPLACES the workspace scan — so a roster printed from the scan named a package the
    /// worker would not receive and omitted the one it would, inverting reality on the one dispatch
    /// where the operator was most explicit. Asserted on the whole line rather than through
    /// <see cref="RealizedSkills"/>, whose regex only knows the two scan-derived realization suffixes.
    /// </summary>
    [Fact]
    public async Task Dispatch_WithADeclaredSkill_PrintsTheDeclaredSetRatherThanTheWorkspaceScan()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-roster-declared-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testRoot);
        var originalOut = Console.Out;
        var emptyConfigRoot = Path.Combine(testRoot, "claude-config-root");
        var libraryDirectory = Path.Combine(testRoot, "library");
        Directory.CreateDirectory(emptyConfigRoot);
        var declaredPackage = Path.Combine(libraryDirectory, "house-style");
        Directory.CreateDirectory(declaredPackage);
        using var environmentScope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Current with
            {
                ClaudeConfigRootOverride = emptyConfigRoot,
                SkillsPathOverride = libraryDirectory,
            });
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(declaredPackage, "SKILL.md"), "description: House style",
                TestContext.Current.CancellationToken);

            // The repo-local package is the discriminator: it is what the scan would have reported, and
            // it is precisely what the worker will NOT get.
            var workspace = Path.Combine(testRoot, "workspace");
            var repoSkill = Path.Combine(workspace, "skills", "repo-thing");
            Directory.CreateDirectory(repoSkill);
            await File.WriteAllTextAsync(
                Path.Combine(repoSkill, "SKILL.md"), "description: Repo thing", TestContext.Current.CancellationToken);

            var specPath = await WriteSpecAsync(testRoot, "Review the change.");
            var options = new DispatchOptions(
                "review", specPath, Path.Combine(testRoot, "room"), Adapter: "claude-worker",
                WorkspaceDirectory: workspace, Skills: ["house-style"]);
            var adapters = new Dictionary<string, IWorkerAdapter>
            {
                ["claude-worker"] = new DelegatingDiscoveryWorkerAdapter(new ClaudeWorkerAdapter()),
            };

            using var output = new StringWriter();
            Console.SetOut(output);
            await DispatchCommand.ExecuteAsync(options, adapters, TestContext.Current.CancellationToken);
            Console.SetOut(originalOut);

            var text = output.ToString();
            Assert.Contains("Skills (declared): house-style", text, StringComparison.Ordinal);
            Assert.DoesNotContain("repo-thing", text, StringComparison.Ordinal);
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
