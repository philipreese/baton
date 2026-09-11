using Baton.Vendors;
using Baton.Domain;

namespace Baton.Vendors.Tests;

public sealed class DirectGhPullRequestCreateTests
{
    private static readonly GhPullRequestCreateProvenance Provenance = new(
        OperatingSystem.IsWindows() ? @"C:\Program Files\GitHub CLI\gh.exe" : "/usr/bin/gh",
        "aer-works/baton",
        "2190-verified-pr-ownership");

    [Fact]
    public void Literal_quoted_title_is_one_exact_argument_and_trusted_selectors_are_injected()
    {
        var compiled = DirectGhPullRequestCreate.Compile(
            "gh pr create --draft --title \"fix(codex): Example [proof]\" --body-file body.md",
            Provenance);

        Assert.True(compiled.IsCreateCommand);
        Assert.Null(compiled.Refusal);
        Assert.Equal(
            ["pr", "create", "--draft", "--title", "fix(codex): Example [proof]", "--body-file",
             "body.md", "--repo", "aer-works/baton", "--head", "2190-verified-pr-ownership"],
            compiled.Arguments);
    }

    [Theory]
    [InlineData("gh pr create --title \"$(whoami)\"")]
    [InlineData("gh pr create --title \"`whoami`\"")]
    [InlineData("gh pr create --fill | tee pr.txt")]
    [InlineData("gh pr create --fill > pr.txt")]
    [InlineData("gh pr create --fill && echo done")]
    [InlineData("git push && gh pr create --fill")]
    [InlineData(".\\gh pr create --fill")]
    [InlineData("./gh pr create --fill")]
    [InlineData("gh pr create --title 'single quoted'")]
    [InlineData("gh pr create --title fix(codex)")]
    [InlineData("gh pr create --repo other/repo")]
    [InlineData("gh pr create -R other/repo")]
    [InlineData("gh pr create --head another-branch")]
    [InlineData("gh pr create --label ready")]
    [InlineData("GH_REPO=other/repo gh pr create --fill")]
    [InlineData("env GH_REPO=other/repo gh pr create --fill")]
    [InlineData("sh -c \"gh pr create --fill\"")]
    [InlineData("cmd /c \"gh pr create --fill\"")]
    [InlineData("pwsh -Command \"gh pr create --fill\"")]
    public void Ambiguous_or_out_of_scope_create_is_refused(string commandLine)
    {
        var compiled = DirectGhPullRequestCreate.Compile(commandLine, Provenance);

        Assert.True(compiled.IsCreateCommand);
        Assert.Null(compiled.Arguments);
        Assert.NotNull(compiled.Refusal);
    }

    [Fact]
    public void A_quoted_mention_is_not_mistaken_for_a_create_invocation()
    {
        var compiled = DirectGhPullRequestCreate.Compile("echo \"gh pr create\"", Provenance);

        Assert.False(compiled.IsCreateCommand);
    }

    [Fact]
    public void Missing_trusted_provenance_refuses_with_an_actionable_remedy()
    {
        var compiled = DirectGhPullRequestCreate.Compile("gh pr create --draft", provenance: null);

        Assert.True(compiled.IsCreateCommand);
        Assert.Contains("Install gh", compiled.Refusal, StringComparison.Ordinal);
        Assert.Contains("named branch", compiled.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolver_skips_a_workspace_local_fake_and_selects_an_outside_native_candidate()
    {
        var root = Path.Combine(Path.GetTempPath(), $"baton-gh-resolver-{Guid.NewGuid():N}");
        var workspace = Path.Combine(root, "workspace");
        var trusted = Path.Combine(root, "host-bin");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(trusted);
        var name = OperatingSystem.IsWindows() ? "gh.exe" : "gh";
        File.WriteAllText(Path.Combine(workspace, name), "workspace fake");
        var expected = Path.Combine(trusted, name);
        File.WriteAllText(expected, "host fixture");
        MakeExecutable(expected);
        try
        {
            var resolved = OutsideWorkspaceExecutableResolver.TryResolve(
                workspace + Path.PathSeparator + trusted,
                workspace,
                "gh",
                OperatingSystem.IsWindows());

            Assert.Equal(expected, resolved);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public void Identity_capture_skips_a_workspace_PATH_shadowed_git()
    {
        var root = Path.Combine(Path.GetTempPath(), $"baton-gh-provenance-{Guid.NewGuid():N}");
        var workspace = Path.Combine(root, "workspace");
        var hostBin = Path.Combine(root, "host-bin");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(hostBin);
        var name = OperatingSystem.IsWindows() ? "git.exe" : "git";
        File.WriteAllText(Path.Combine(workspace, name), "workspace fake");
        var trustedGit = Path.Combine(hostBin, name);
        File.WriteAllText(trustedGit, "host fixture");
        MakeExecutable(trustedGit);
        try
        {
            var identity = GhPullRequestCreateProvenanceResolver.TryCaptureIdentity(
                workspace,
                workspace + Path.PathSeparator + hostBin,
                OperatingSystem.IsWindows(),
                (executable, directory, arguments) =>
                {
                    Assert.Equal(trustedGit, executable);
                    Assert.Equal(workspace, directory);
                    return arguments[0] == "config"
                        ? "git@github.com:AER-Works/Baton.git"
                        : "2190-verified-pr-ownership";
                });

            Assert.Equal("aer-works/baton", identity?.Repository);
            Assert.Equal("2190-verified-pr-ownership", identity?.HeadBranch);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public void Identity_capture_normalizes_a_relative_conductor_workspace_before_probing()
    {
        var workspace = Directory.GetCurrentDirectory();
        var hostBin = Path.Combine(Path.GetTempPath(), $"baton-gh-relative-{Guid.NewGuid():N}");
        Directory.CreateDirectory(hostBin);
        var trustedGit = Path.Combine(hostBin, OperatingSystem.IsWindows() ? "git.exe" : "git");
        File.WriteAllText(trustedGit, "host fixture");
        MakeExecutable(trustedGit);
        try
        {
            var identity = GhPullRequestCreateProvenanceResolver.TryCaptureIdentity(
                ".",
                hostBin,
                OperatingSystem.IsWindows(),
                (executable, directory, arguments) =>
                {
                    Assert.Equal(trustedGit, executable);
                    Assert.Equal(workspace, directory);
                    return arguments[0] == "config"
                        ? "https://github.com/aer-works/baton.git"
                        : "2190-verified-pr-ownership";
                });

            Assert.Equal("aer-works/baton", identity?.Repository);
            Assert.Equal("2190-verified-pr-ownership", identity?.HeadBranch);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(hostBin);
        }
    }

    [Fact]
    public void A_codex_exhaustion_fallback_captures_identity_for_a_non_codex_primary()
    {
        var expected = new GhPullRequestCreateIdentity("aer-works/baton", "fallback-branch");
        var entry = new WorkerBindingConfigEntry(
            "claude",
            new WorkerContract("implement", [], [], []),
            "prompt",
            TimeSpan.FromMinutes(1),
            PermissionGrant: WorkerRoleCatalog.For("implement").Grant,
            FallbackOnExhaustion: new FallbackBinding("codex"));

        var captured = GhPullRequestCreateProvenanceResolver.CaptureIdentityFor(
            entry,
            ".",
            directory =>
            {
                Assert.Equal(".", directory);
                return expected;
            });

        Assert.Equal(expected, captured.PullRequestCreateIdentity);
        Assert.Equal(expected, WorkerBindingResolver.ToFallbackEntry(
            captured, captured.FallbackOnExhaustion!).PullRequestCreateIdentity);
    }

    [Fact]
    public void Resolver_rejects_an_outside_directory_alias_into_the_workspace()
    {
        var root = Path.Combine(Path.GetTempPath(), $"baton-gh-alias-{Guid.NewGuid():N}");
        var workspace = Path.Combine(root, "workspace");
        var workspaceBin = Path.Combine(workspace, "bin");
        var outsideAlias = Path.Combine(root, "host-bin");
        Directory.CreateDirectory(workspaceBin);
        var name = OperatingSystem.IsWindows() ? "gh.exe" : "gh";
        var workspaceGh = Path.Combine(workspaceBin, name);
        File.WriteAllText(workspaceGh, "workspace fake");
        MakeExecutable(workspaceGh);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                CreateWindowsJunction(outsideAlias, workspaceBin);
            }
            else
            {
                Directory.CreateSymbolicLink(outsideAlias, workspaceBin);
            }

            Assert.Null(OutsideWorkspaceExecutableResolver.TryResolve(
                outsideAlias, workspace, "gh", OperatingSystem.IsWindows()));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public void Resumed_binding_uses_persisted_identity_after_remote_metadata_changes()
    {
        var root = Path.Combine(Path.GetTempPath(), $"baton-gh-resume-{Guid.NewGuid():N}");
        var workspace = Path.Combine(root, "workspace");
        var hostBin = Path.Combine(root, "host-bin");
        Directory.CreateDirectory(Path.Combine(workspace, ".git"));
        Directory.CreateDirectory(hostBin);
        var gh = Path.Combine(hostBin, OperatingSystem.IsWindows() ? "gh.exe" : "gh");
        File.WriteAllText(gh, "host fixture");
        MakeExecutable(gh);
        try
        {
            var binding = new WorkerBindingConfigEntry(
                "codex",
                new WorkerContract("implement", [], [], []),
                "prompt",
                TimeSpan.FromMinutes(1),
                PullRequestCreateIdentity: new(
                    "aer-works/baton", "2190-verified-pr-ownership"));
            var serialized = WorkerBindingConfigWriter.Serialize(
                new Dictionary<string, WorkerBindingConfigEntry> { ["implement"] = binding });
            var resumed = WorkerBindingConfigParser.Parse(serialized)["implement"];

            // Worker-controlled shared Git metadata now claims a foreign repository. Resolution on
            // resume must not execute Git or read this file.
            File.WriteAllText(
                Path.Combine(workspace, ".git", "config"),
                "[remote \"origin\"]\nurl = https://github.com/other/repo.git\n");

            var provenance = GhPullRequestCreateProvenanceResolver.TryResolve(
                workspace, resumed.PullRequestCreateIdentity, hostBin, OperatingSystem.IsWindows());

            Assert.Equal(gh, provenance?.ExecutablePath);
            Assert.Equal("aer-works/baton", provenance?.Repository);
            Assert.Equal("2190-verified-pr-ownership", provenance?.HeadBranch);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    private static void MakeExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute);
        }
    }

    private static void CreateWindowsJunction(string junction, string target)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo(
            Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            Arguments = $"/d /c mklink /J \"{junction}\" \"{target}\"",
        };
        using var process = System.Diagnostics.Process.Start(startInfo);
        Assert.NotNull(process);
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0,
            $"Windows junction fixture is required but mklink exited {process.ExitCode}: {stdout}{stderr}");
        Assert.True((new DirectoryInfo(junction).Attributes & FileAttributes.ReparsePoint) != 0,
            "mklink did not produce a junction/reparse point.");
    }
}
