using Baton.Vendors;

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
            var resolved = GhExecutableResolver.TryResolve(
                workspace + Path.PathSeparator + trusted, workspace, OperatingSystem.IsWindows());

            Assert.Equal(expected, resolved);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public void Provenance_uses_the_binding_source_for_repository_and_workspace_for_branch()
    {
        var root = Path.Combine(Path.GetTempPath(), $"baton-gh-provenance-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source");
        var workspace = Path.Combine(root, "workspace");
        var hostBin = Path.Combine(root, "host-bin");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(hostBin);
        var executable = Path.Combine(hostBin, OperatingSystem.IsWindows() ? "gh.exe" : "gh");
        File.WriteAllText(executable, "fixture");
        MakeExecutable(executable);
        try
        {
            var provenance = GhPullRequestCreateProvenanceResolver.TryResolve(
                workspace,
                source,
                hostBin,
                OperatingSystem.IsWindows(),
                (directory, arguments) => arguments[0] == "config"
                    ? directory == source ? "git@github.com:AER-Works/Baton.git" : null
                    : directory == workspace ? "2190-verified-pr-ownership" : null);

            Assert.Equal(executable, provenance?.ExecutablePath);
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
}
