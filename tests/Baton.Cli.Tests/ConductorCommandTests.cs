using System.Diagnostics;
using System.Text.Json;
using Baton.Accounting;
using Baton.Conductor;
using Baton.Status;
using Baton.Tests.Shared;
using Baton.Vendors;

namespace Baton.Cli.Tests;

public sealed class ConductorCommandTests
{
    private static readonly RepositoryIdentity Repo1 =
        RepositoryIdentity.From("https://github.com/philipreese/repo-1.git", null)!;

    private static readonly RepositoryIdentity Repo2 =
        RepositoryIdentity.From("https://github.com/philipreese/repo-2.git", null)!;

    [Fact]
    public async Task Claim_Success_And_Idempotent_And_Conflict()
    {
        var temp = NewTempDirectory();
        try
        {
            var stdout = new StringWriter();
            var options = new ConductorOptions(ConductorVerb.Claim, Holder: "conductor-1", Workspace: "/dummy/repo1");

            var exitCode = await ConductorCommand.ExecuteAsync(
                options, stdout, batonRoot: temp,
                repositoryResolver: (_, _) => Task.FromResult<RepositoryIdentity?>(Repo1),
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            Assert.Contains($"Claimed repository '{Repo1.Value}' for conductor 'conductor-1'.", stdout.ToString());

            // Idempotent claim by same holder
            stdout = new StringWriter();
            exitCode = await ConductorCommand.ExecuteAsync(
                options, stdout, batonRoot: temp,
                repositoryResolver: (_, _) => Task.FromResult<RepositoryIdentity?>(Repo1),
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            Assert.Contains($"Repository '{Repo1.Value}' is already claimed by conductor 'conductor-1'.", stdout.ToString());

            // Conflict by another holder
            var conflictOptions = new ConductorOptions(ConductorVerb.Claim, Holder: "conductor-2", Workspace: "/dummy/repo1");
            var ex = await Assert.ThrowsAsync<ConductorClaimException>(() => ConductorCommand.ExecuteAsync(
                conflictOptions, TextWriter.Null, batonRoot: temp,
                repositoryResolver: (_, _) => Task.FromResult<RepositoryIdentity?>(Repo1),
                cancellationToken: TestContext.Current.CancellationToken));

            Assert.Contains("conductor-1", ex.Message);
            Assert.Contains("baton conductor takeover conductor-2 --reason <text>", ex.Message);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(temp);
        }
    }

    [Fact]
    public async Task Takeover_And_Release_Roundtrip()
    {
        var temp = NewTempDirectory();
        try
        {
            // First claim
            await ConductorCommand.ExecuteAsync(
                new ConductorOptions(ConductorVerb.Claim, Holder: "conductor-1", Workspace: "/dummy/repo1"),
                TextWriter.Null, batonRoot: temp,
                repositoryResolver: (_, _) => Task.FromResult<RepositoryIdentity?>(Repo1),
                cancellationToken: TestContext.Current.CancellationToken);

            // Takeover by conductor-2
            var takeoverStdout = new StringWriter();
            var takeoverOptions = new ConductorOptions(
                ConductorVerb.Takeover, Holder: "conductor-2", Workspace: "/dummy/repo1", Reason: "urgent priority");

            var exitCode = await ConductorCommand.ExecuteAsync(
                takeoverOptions, takeoverStdout, batonRoot: temp,
                repositoryResolver: (_, _) => Task.FromResult<RepositoryIdentity?>(Repo1),
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            Assert.Contains("Conductor 'conductor-2' took over repository", takeoverStdout.ToString());
            Assert.Contains("from 'conductor-1'", takeoverStdout.ToString());
            Assert.Contains("Reason: urgent priority", takeoverStdout.ToString());

            // Release by conductor-2
            var releaseStdout = new StringWriter();
            var releaseOptions = new ConductorOptions(
                ConductorVerb.Release, Holder: "conductor-2", Workspace: "/dummy/repo1", Reason: "finished batch");

            exitCode = await ConductorCommand.ExecuteAsync(
                releaseOptions, releaseStdout, batonRoot: temp,
                repositoryResolver: (_, _) => Task.FromResult<RepositoryIdentity?>(Repo1),
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            Assert.Contains("Released repository", releaseStdout.ToString());
            Assert.Contains("from conductor 'conductor-2'", releaseStdout.ToString());

            // List is now empty
            var listStdout = new StringWriter();
            await ConductorCommand.ExecuteAsync(
                new ConductorOptions(ConductorVerb.List), listStdout, batonRoot: temp,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Contains("No active conductor claims.", listStdout.ToString());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(temp);
        }
    }

    [Fact]
    public async Task List_DeterministicTextAndJson_AndExposesProvenance()
    {
        var temp = NewTempDirectory();
        try
        {
            // Empty list
            var emptyText = new StringWriter();
            await ConductorCommand.ExecuteAsync(
                new ConductorOptions(ConductorVerb.List, Json: false), emptyText, batonRoot: temp,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Contains("No active conductor claims.", emptyText.ToString());

            var emptyJson = new StringWriter();
            await ConductorCommand.ExecuteAsync(
                new ConductorOptions(ConductorVerb.List, Json: true), emptyJson, batonRoot: temp,
                cancellationToken: TestContext.Current.CancellationToken);
            var parsedEmpty = JsonSerializer.Deserialize<List<ConductorClaimSummary>>(emptyJson.ToString());
            Assert.NotNull(parsedEmpty);
            Assert.Empty(parsedEmpty);

            // Populate repo-2 first, then repo-1 to test deterministic ordering (repo-1 < repo-2)
            await ConductorCommand.ExecuteAsync(
                new ConductorOptions(ConductorVerb.Claim, Holder: "conductor-b", Workspace: "/dummy/repo2"),
                TextWriter.Null, batonRoot: temp,
                repositoryResolver: (_, _) => Task.FromResult<RepositoryIdentity?>(Repo2),
                cancellationToken: TestContext.Current.CancellationToken);

            await ConductorCommand.ExecuteAsync(
                new ConductorOptions(ConductorVerb.Claim, Holder: "conductor-a", Workspace: "/dummy/repo1"),
                TextWriter.Null, batonRoot: temp,
                repositoryResolver: (_, _) => Task.FromResult<RepositoryIdentity?>(Repo1),
                cancellationToken: TestContext.Current.CancellationToken);

            // Takeover on repo-1 to verify takeover provenance
            await ConductorCommand.ExecuteAsync(
                new ConductorOptions(ConductorVerb.Takeover, Holder: "conductor-c", Workspace: "/dummy/repo1", Reason: "lane failover"),
                TextWriter.Null, batonRoot: temp,
                repositoryResolver: (_, _) => Task.FromResult<RepositoryIdentity?>(Repo1),
                cancellationToken: TestContext.Current.CancellationToken);

            // Text list
            var populatedText = new StringWriter();
            await ConductorCommand.ExecuteAsync(
                new ConductorOptions(ConductorVerb.List, Json: false), populatedText, batonRoot: temp,
                cancellationToken: TestContext.Current.CancellationToken);

            var textOutput = populatedText.ToString();
            var repo1Index = textOutput.IndexOf(Repo1.Value, StringComparison.Ordinal);
            var repo2Index = textOutput.IndexOf(Repo2.Value, StringComparison.Ordinal);
            Assert.True(repo1Index >= 0 && repo2Index >= 0 && repo1Index < repo2Index, "List must be sorted deterministically");
            Assert.Contains("holder: conductor-c", textOutput);
            Assert.Contains("takeover: from conductor-a", textOutput);
            Assert.Contains("reason: lane failover", textOutput);

            // JSON list
            var populatedJson = new StringWriter();
            await ConductorCommand.ExecuteAsync(
                new ConductorOptions(ConductorVerb.List, Json: true), populatedJson, batonRoot: temp,
                cancellationToken: TestContext.Current.CancellationToken);

            var summaries = JsonSerializer.Deserialize<List<ConductorClaimSummary>>(populatedJson.ToString());
            Assert.NotNull(summaries);
            Assert.Equal(2, summaries.Count);
            Assert.Equal(Repo1.Value, summaries[0].Repository);
            Assert.Equal("conductor-c", summaries[0].Holder);
            Assert.NotNull(summaries[0].Takeover);
            Assert.Equal("conductor-a", summaries[0].Takeover!.DisplacedHolder);
            Assert.Equal("lane failover", summaries[0].Takeover!.Reason);

            Assert.Equal(Repo2.Value, summaries[1].Repository);
            Assert.Equal("conductor-b", summaries[1].Holder);
            Assert.Null(summaries[1].Takeover);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(temp);
        }
    }

    [Fact]
    public async Task Refuses_Workspace_That_Cannot_Produce_Canonical_Repository_Identity()
    {
        var temp = NewTempDirectory();
        try
        {
            var options = new ConductorOptions(ConductorVerb.Claim, Holder: "conductor-1", Workspace: "/invalid/path");
            var ex = await Assert.ThrowsAsync<CliArgumentException>(() => ConductorCommand.ExecuteAsync(
                options, TextWriter.Null, batonRoot: temp,
                repositoryResolver: (_, _) => Task.FromResult<RepositoryIdentity?>(null),
                cancellationToken: TestContext.Current.CancellationToken));

            Assert.Contains("does not resolve to a canonical repository identity", ex.Message);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(temp);
        }
    }

    [Fact]
    public async Task Canonical_Identity_Across_Two_Worktrees_Of_Same_Repository()
    {
        var root = NewTempDirectory();
        var batonRoot = Path.Combine(root, "baton-root");
        Directory.CreateDirectory(batonRoot);
        try
        {
            var main = Path.Combine(root, "main");
            var linked = Path.Combine(root, "linked");
            await InitGitRepoAsync(main);
            await RunGitAsync(main, "worktree", "add", "-q", "-b", "side", linked);

            // Verify both worktrees resolve to the same canonical identity with the real resolver
            var fromMain = await RepositoryIdentityResolver.TryResolveAsync(main, TestContext.Current.CancellationToken);
            var fromLinked = await RepositoryIdentityResolver.TryResolveAsync(linked, TestContext.Current.CancellationToken);
            Assert.NotNull(fromMain);
            Assert.NotNull(fromLinked);
            Assert.Equal(fromMain.Value, fromLinked.Value);

            // Claim using workspace = main
            var stdout = new StringWriter();
            var claimMainOptions = new ConductorOptions(ConductorVerb.Claim, Holder: "conductor-1", Workspace: main);
            var code1 = await ConductorCommand.ExecuteAsync(
                claimMainOptions, stdout, batonRoot: batonRoot,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(0, code1);
            Assert.Contains($"Claimed repository '{fromMain.Value}' for conductor 'conductor-1'.", stdout.ToString());

            // A second conductor attempting to claim via the linked worktree MUST be refused
            var claimLinkedOptions = new ConductorOptions(ConductorVerb.Claim, Holder: "conductor-2", Workspace: linked);
            var ex = await Assert.ThrowsAsync<ConductorClaimException>(() => ConductorCommand.ExecuteAsync(
                claimLinkedOptions, TextWriter.Null, batonRoot: batonRoot,
                cancellationToken: TestContext.Current.CancellationToken));

            Assert.Contains("conductor-1", ex.Message);
            Assert.Contains("baton conductor takeover conductor-2 --reason <text>", ex.Message);

            // Same conductor claiming from linked worktree is idempotent
            stdout = new StringWriter();
            var claimSameOptions = new ConductorOptions(ConductorVerb.Claim, Holder: "conductor-1", Workspace: linked);
            var code2 = await ConductorCommand.ExecuteAsync(
                claimSameOptions, stdout, batonRoot: batonRoot,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(0, code2);
            Assert.Contains($"Repository '{fromMain.Value}' is already claimed by conductor 'conductor-1'.", stdout.ToString());

            // List shows exactly one held claim under the canonical repository identity
            var listJson = new StringWriter();
            await ConductorCommand.ExecuteAsync(
                new ConductorOptions(ConductorVerb.List, Json: true), listJson, batonRoot: batonRoot,
                cancellationToken: TestContext.Current.CancellationToken);

            var list = JsonSerializer.Deserialize<List<ConductorClaimSummary>>(listJson.ToString());
            Assert.NotNull(list);
            var single = Assert.Single(list);
            Assert.Equal(fromMain.Value, single.Repository);
            Assert.Equal("conductor-1", single.Holder);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public async Task Two_independent_CLI_processes_racing_one_repository_leave_exactly_one_owner()
    {
        var root = NewTempDirectory();
        try
        {
            var workspace = Path.Combine(root, "repository");
            var batonRoot = Path.Combine(root, "baton-home");
            await InitGitRepoAsync(workspace);

            using var first = StartClaimProcess(workspace, batonRoot, "conductor-alpha");
            using var second = StartClaimProcess(workspace, batonRoot, "conductor-beta");
            first.Start();
            second.Start();
            var outputs = await Task.WhenAll(
                BoundedProcessWait.RunToExitAsync(first, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken),
                BoundedProcessWait.RunToExitAsync(second, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

            Assert.True(new[] { first.ExitCode, second.ExitCode }.Count(code => code == 0) == 1,
                string.Join(Environment.NewLine, outputs.Select(output => $"stdout: {output.Stdout}{Environment.NewLine}stderr: {output.Stderr}")));
            Assert.Equal(1, new[] { first.ExitCode, second.ExitCode }.Count(code => code != 0));
            var identity = await RepositoryIdentityResolver.TryResolveAsync(workspace, TestContext.Current.CancellationToken);
            Assert.NotNull(identity);
            var claim = await ConductorClaimStore.GetClaimAsync(identity, batonRoot, TestContext.Current.CancellationToken);
            Assert.NotNull(claim);
            Assert.Contains(claim.Holder, new[] { "conductor-alpha", "conductor-beta" });
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    private static Process StartClaimProcess(string workspace, string batonRoot, string holder)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workspace,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(typeof(ConductorCommand).Assembly.Location);
        startInfo.ArgumentList.Add("conductor");
        startInfo.ArgumentList.Add("claim");
        startInfo.ArgumentList.Add(holder);
        startInfo.ArgumentList.Add("--workspace");
        startInfo.ArgumentList.Add(workspace);
        startInfo.Environment["BATON_HOME"] = batonRoot;
        return new Process { StartInfo = startInfo };
    }

    private static string NewTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"baton-conductor-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task InitGitRepoAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        await RunGitAsync(directory, "init", "-q");
        await RunGitAsync(
            directory, "-c", "user.email=test@example.invalid", "-c", "user.name=Test",
            "commit", "--allow-empty", "-q", "-m", "base");
    }

    private static async Task RunGitAsync(string workingDirectory, params string[] args)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start git — is it on PATH? These tests need git.");
        var (stdout, stderr) = await BoundedProcessWait.RunToExitAsync(
            process, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stdout} {stderr.Trim()}");
        }
    }
}
