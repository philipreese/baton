using System.Text.Json;
using Baton.Cli.Tests.TestSupport;
using Baton.CrashTestHost;
using Baton.Mutation;
using Baton.Status;
using Baton.Vendors;

namespace Baton.Cli.Tests;

[Collection(SerializedEnvironmentCollection.Name)]
public sealed class PullRequestCreateDispatchRouteTests : IDisposable
{
    private readonly IsolatedBatonHome _batonHome = new();
    private readonly IDisposable _catalogScope;

    public PullRequestCreateDispatchRouteTests()
    {
        _catalogScope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Current with
        {
            WorkerRolesPathOverride = Path.Combine(AppContext.BaseDirectory, "WorkerRoles.json"),
            WorkerTiersPathOverride = Path.Combine(AppContext.BaseDirectory, "WorkerTiers.json"),
            WorkflowTemplatesPathOverride = Path.Combine(AppContext.BaseDirectory, "WorkflowTemplates.json"),
        });
    }

    public void Dispose()
    {
        _catalogScope.Dispose();
        _batonHome.Dispose();
    }

    [Fact]
    public async Task Public_relative_workspace_dispatch_reaches_serialized_binding_and_codex_adapter_configuration()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dispatch-pr-provenance-{Guid.NewGuid():N}");
        var workspace = Path.Combine(root, "workspace");
        var room = Path.Combine(root, "room");
        var hostBin = Path.Combine(root, "host-bin");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(hostBin);

        CopyHermeticGitProbeHost(hostBin);
        var executableSuffix = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        var appHost = Path.Combine(hostBin, "Baton.CrashTestHost" + executableSuffix);
        var git = Path.Combine(hostBin, "git" + executableSuffix);
        var gh = Path.Combine(hostBin, "gh" + executableSuffix);
        File.Copy(appHost, git);
        File.Copy(appHost, gh);
        MakeExecutable(git);
        MakeExecutable(gh);

        var oldPath = Environment.GetEnvironmentVariable("PATH");
        var oldDirectory = Directory.GetCurrentDirectory();
        try
        {
            Environment.SetEnvironmentVariable(
                "PATH", hostBin + Path.PathSeparator + oldPath);
            Directory.SetCurrentDirectory(workspace);

            // Parse the public spelling, rather than constructing DispatchOptions with an already
            // absolute workspace. The fake adapter completes the lane without any live vendor.
            var options = DispatchOptionsParser.Parse(
                ["implement", "--spec-text", "Exercise provenance.", "--adapter", "codex",
                 "--room-dir", room, "--workspace", ".", "--expect-pr", "false",
                 "--no-default-skills", "--verify", "cmd /c exit 0"]);
            var dispatchAdapters = new Dictionary<string, IWorkerAdapter>
            {
                ["codex"] = new ContractOutputWorkerAdapter(satisfyOutputs: true),
            };

            _ = await DispatchCommand.ExecuteAsync(
                options,
                dispatchAdapters,
                TestContext.Current.CancellationToken,
                evaluateRunway: RunwayTestGate.Admit);

            var serialized = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(room, "bindings.json"), TestContext.Current.CancellationToken);
            var entry = serialized["implement"];
            Assert.Equal(
                new GhPullRequestCreateIdentity(
                    "aer-works/baton", "2190-verified-pr-ownership"),
                entry.PullRequestCreateIdentity);

            ProjectCeilingStore.Set(
                workspace, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);
            var resolved = WorkerBindingResolver.Resolve(
                serialized,
                new Dictionary<string, IWorkerAdapter>
                {
                    ["codex"] = new CodexWorkerAdapter(),
                },
                bindingsFileDirectory: room);
            var process = Assert.IsType<WorkerBinding.Process>(resolved["implement"]);
            var configuration = JsonSerializer.Deserialize<CodexBrokerConfiguration>(
                Assert.Single(process.Target.SeedFiles!).Content);

            Assert.NotNull(configuration);
            Assert.Equal(Path.GetFullPath(workspace), configuration.WorkingDirectory);
            Assert.Equal(gh, configuration.PullRequestCreateProvenance?.ExecutablePath);
            Assert.Equal("aer-works/baton", configuration.PullRequestCreateProvenance?.Repository);
            Assert.Equal(
                "2190-verified-pr-ownership",
                configuration.PullRequestCreateProvenance?.HeadBranch);
        }
        finally
        {
            Directory.SetCurrentDirectory(oldDirectory);
            Environment.SetEnvironmentVariable("PATH", oldPath);
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    private static void CopyHermeticGitProbeHost(string destination)
    {
        var sourceDirectory = Path.GetDirectoryName(typeof(Scenarios).Assembly.Location)!;
        var hostPrefix = "Baton.CrashTestHost";
        foreach (var source in Directory.EnumerateFiles(sourceDirectory))
        {
            var name = Path.GetFileName(source);
            if (name.StartsWith(hostPrefix, StringComparison.Ordinal)
                || name.Equals("Baton.dll", StringComparison.Ordinal))
            {
                File.Copy(source, Path.Combine(destination, name));
            }
        }

        var executableSuffix = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        Assert.True(
            File.Exists(Path.Combine(destination, hostPrefix + executableSuffix)),
            "The project-referenced hermetic probe apphost was not copied to the test output.");
    }

    private static void MakeExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute);
        }
    }
}

