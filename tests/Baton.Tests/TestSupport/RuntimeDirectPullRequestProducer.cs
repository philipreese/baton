using System.Text.Json;
using Baton.Domain;
using Baton.Vendors;

namespace Baton.Tests.TestSupport;

internal sealed class RuntimeDirectPullRequestProducer : IDisposable
{
    private readonly string _producerId = Guid.NewGuid().ToString("N");
    private readonly string _root;
    private readonly string _workspace;
    private readonly string _outputDirectory;
    private readonly CodexDynamicToolPolicy _policy;

    public RuntimeDirectPullRequestProducer(string workspace, string outputDirectory)
    {
        _root = Path.Combine(Path.GetTempPath(), $"baton-runtime-pr-{Guid.NewGuid():N}");
        _workspace = workspace;
        _outputDirectory = outputDirectory;
        Directory.CreateDirectory(_root);

        GhPullRequestCreateProvenance provenance;
        IReadOnlyList<string> prefix;
        if (OperatingSystem.IsWindows())
        {
            var script = Path.Combine(_root, "fake-gh.ps1");
            File.WriteAllText(script, "Write-Output 'created'\r\n");
            provenance = new GhPullRequestCreateProvenance(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "System32", "WindowsPowerShell", "v1.0", "powershell.exe"),
                "aer-works/baton", "runtime-direct-test");
            prefix = ["-NoProfile", "-NonInteractive", "-File", script];
        }
        else
        {
            var script = Path.Combine(_root, "fake-gh.sh");
            File.WriteAllText(script, "printf '%s\\n' created\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            provenance = new GhPullRequestCreateProvenance(
                "/bin/sh", "aer-works/baton", "runtime-direct-test");
            prefix = [script];
        }

        _policy = new CodexDynamicToolPolicy(
            new PermissionGrant(ReadFiles: true, WriteFiles: true, RunShellCommands: true,
                ShellCommandPatterns: ["gh pr create*"]),
            workspace,
            outputDirectory,
            [],
            ["changes.md"],
            commandCeiling: null,
            timeProvider: null,
            commandCaptureStreamFactory: null,
            pullRequestCreateProvenance: provenance,
            directGhPrefixArguments: prefix);
    }

    public async Task ProduceAsync()
    {
        Directory.CreateDirectory(_outputDirectory);
        await File.WriteAllTextAsync(Path.Combine(_outputDirectory, "changes.md"), "handoff");
        await File.WriteAllTextAsync(Path.Combine(_workspace, $"production-{_producerId}.txt"), "product change\n");
        await File.WriteAllTextAsync(Path.Combine(_workspace, "pr-body.md"), $"generated body {_producerId}\n");

        using var arguments = JsonDocument.Parse(
            "{\"command\":\"gh pr create --draft --body-file pr-body.md\"}");
        var result = await _policy.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool,
            arguments.RootElement,
            TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Text);
    }

    public void Dispose()
    {
        DirectoryCleanup.DeleteRecursively(_root);
    }
}
