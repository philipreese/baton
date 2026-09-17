using System.Diagnostics;
using System.Text.Json;
using Baton.Accounting;
using Baton.Cli.Daemon;
using Baton.Cli.Tests.TestSupport;
using Baton.Domain;
using Baton.Queue;
using Baton.Status;
using Baton.Tests.Shared;

namespace Baton.Cli.Tests;

/// <summary>
/// Drives <c>queue worktrees --apply</c> through isolated, linked Git worktrees. These tests keep
/// the mutation and its final recheck together, because the protected cleanup invariant is about
/// that interval rather than the read-only classifier in <see cref="QueueWorktreeReportTests"/>.
/// </summary>
public sealed class QueueWorktreeApplyTests
{
    private const string Repository = "github.com/example/retained-worktrees";
    private const string Branch = "2151-lane";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(QueueItemState.Queued)]
    [InlineData(QueueItemState.Cancelled)]
    public async Task Apply_removes_clean_owned_registered_worktree_and_preserves_branch_and_receipt(QueueItemState state)
    {
        await using var fixture = ApplyFixture.Create();
        await fixture.InitializeAsync(state);

        var (exit, output) = await ExecuteAsync(fixture, apply: true);

        Assert.Equal(0, exit);
        Assert.False(Directory.Exists(fixture.Worktree), output);
        Assert.False(File.Exists(Path.Combine(fixture.Worktree, "README.md")));
        Assert.DoesNotContain(fixture.Worktree, await GitOutputAsync(fixture.Source, "worktree", "list", "--porcelain"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(fixture.Head, (await GitOutputAsync(fixture.Source, "rev-parse", "--verify", "refs/heads/" + Branch)).Trim());
        Assert.Equal("removed", CleanupDisposition(output, fixture.Worktree));

        var receipt = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).WorktreeCleanupReceipts!);
        Assert.Equal("removed", receipt.Disposition);
        Assert.Equal("removed", receipt.ReasonCode);
        Assert.Equal(fixture.Worktree, receipt.Path, ignoreCase: true);
        Assert.Equal(Repository, receipt.Repository);
        Assert.Equal(Branch, receipt.Branch);
        Assert.Equal(fixture.Head, receipt.Head);
        Assert.NotNull(receipt.ObservedBytes);
        Assert.NotEqual(default, receipt.StartedAt);
        Assert.NotEqual(default, receipt.CompletedAt);
        Assert.NotEqual(default, receipt.ClaimId);
    }

    [Fact]
    public async Task Apply_is_idempotent_and_does_not_receipt_a_successful_removal_twice()
    {
        await using var fixture = ApplyFixture.Create();
        await fixture.InitializeAsync();

        Assert.Equal(0, (await ExecuteAsync(fixture, apply: true)).ExitCode);
        var (exit, output) = await ExecuteAsync(fixture, apply: true);

        Assert.Equal(0, exit);
        Assert.DoesNotContain("\"CleanupDisposition\": \"removed\"", output, StringComparison.Ordinal);
        var snapshot = await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct);
        Assert.Single(snapshot.WorktreeCleanupClaims!);
        Assert.Single(snapshot.WorktreeCleanupReceipts!);
    }

    [Fact]
    public async Task Dry_run_and_apply_report_the_same_candidate_identity_before_apply_adds_disposition()
    {
        await using var fixture = ApplyFixture.Create();
        await fixture.InitializeAsync();

        var dryRun = await ExecuteAsync(fixture, apply: false);
        var applied = await ExecuteAsync(fixture, apply: true);

        Assert.Equal(0, dryRun.ExitCode);
        Assert.Equal(0, applied.ExitCode);
        Assert.Equal(CandidatePaths(dryRun.Output), CandidatePaths(applied.Output));
        Assert.Null(CleanupDispositionOrNull(dryRun.Output, fixture.Worktree));
        Assert.Equal("removed", CleanupDisposition(applied.Output, fixture.Worktree));
    }

    [Fact]
    public async Task Apply_recovers_an_abandoned_claim_only_after_a_fresh_safe_recheck()
    {
        await using var fixture = ApplyFixture.Create();
        await fixture.InitializeAsync();
        var abandoned = await QueueStore.TryClaimWorktreeCleanupAsync(
            BatonPaths.QueueFile, fixture.Worktree, Repository, Branch, fixture.Head, Ct);
        Assert.NotNull(abandoned);

        var (exit, output) = await ExecuteAsync(fixture, apply: true);

        Assert.Equal(0, exit);
        Assert.Equal("removed", CleanupDisposition(output, fixture.Worktree));
        var receipts = (await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).WorktreeCleanupReceipts!;
        Assert.Contains(receipts, receipt => receipt.ClaimId == abandoned!.Id
            && receipt.Disposition == "race-lost" && receipt.ReasonCode == "abandoned-claim-recovered");
        Assert.Contains(receipts, receipt => receipt.Disposition == "removed");
    }

    [Fact]
    public async Task Apply_refuses_an_abandoned_claim_when_its_fresh_recheck_changed()
    {
        await using var fixture = ApplyFixture.Create();
        await fixture.InitializeAsync();
        var abandoned = await QueueStore.TryClaimWorktreeCleanupAsync(
            BatonPaths.QueueFile, fixture.Worktree, Repository, Branch, fixture.Head, Ct);
        Assert.NotNull(abandoned);
        await File.WriteAllTextAsync(Path.Combine(fixture.Worktree, "changed-after-crash.txt"), "dirty", Ct);

        var (exit, output) = await ExecuteAsync(fixture, apply: true);

        Assert.Equal(1, exit);
        Assert.True(Directory.Exists(fixture.Worktree));
        Assert.Equal("refused", CleanupDisposition(output, fixture.Worktree));
        var receipt = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).WorktreeCleanupReceipts!);
        Assert.Equal(abandoned!.Id, receipt.ClaimId);
        Assert.Equal("refused", receipt.Disposition);
        Assert.Equal("abandoned-claim-final-recheck-not-candidate", receipt.ReasonCode);
    }

    [Theory]
    [InlineData("head")]
    [InlineData("dirtiness")]
    [InlineData("queue-ownership")]
    [InlineData("registration")]
    public async Task Apply_final_recheck_refuses_each_controlled_post_claim_change(string change)
    {
        await using var fixture = ApplyFixture.Create();
        await fixture.InitializeAsync();
        var hooks = new QueueCommand.WorktreeApplyTestHooks(AfterClaim: async (_, token) =>
        {
            switch (change)
            {
                case "head":
                    await GitAsync(fixture.Worktree, token, "checkout", "--detach", "-q");
                    break;
                case "dirtiness":
                    await File.WriteAllTextAsync(Path.Combine(fixture.Worktree, "uncommitted.txt"), "dirty", token);
                    break;
                case "queue-ownership":
                    await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with
                    {
                        Items = snapshot.Items.Select(item => item.Tag == fixture.Item.Tag
                            ? item with
                            {
                                State = QueueItemState.Queued,
                                Retirement = null,
                            }
                            : item).ToList(),
                    }, token);
                    break;
                case "registration":
                    await GitAsync(fixture.Source, token, "worktree", "move", fixture.Worktree, fixture.MovedWorktree);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(change));
            }
        });

        var (exit, output) = await ExecuteAsync(fixture, apply: true, hooks);

        Assert.Equal(1, exit);
        Assert.Equal("refused", CleanupDisposition(output, fixture.Worktree));
        Assert.True(Directory.Exists(change == "registration" ? fixture.MovedWorktree : fixture.Worktree));
        var receipt = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).WorktreeCleanupReceipts!);
        Assert.Equal("refused", receipt.Disposition);
        Assert.Equal("final-recheck-not-candidate", receipt.ReasonCode);
    }

    [Theory]
    [InlineData("remove")]
    [InlineData("postcondition")]
    public async Task Apply_records_controlled_git_and_postcondition_failures_without_force_or_recursive_removal(string failure)
    {
        await using var fixture = ApplyFixture.Create();
        await fixture.InitializeAsync();
        var calls = new List<string[]>();
        var hooks = new QueueCommand.WorktreeApplyTestHooks(RunProbeAsync: async (file, arguments, cwd, token) =>
        {
            calls.Add(arguments.ToArray());
            if (failure == "remove" && arguments.SequenceEqual(["worktree", "remove", fixture.Worktree]))
                return (1, "controlled remove failure");
            if (failure == "postcondition" && arguments.SequenceEqual(["worktree", "list", "--porcelain"]))
                return (1, "controlled postcondition failure");
            return await IssueWorktreeProvisioner.RunRetainedProbeAsync(file, arguments, cwd, token);
        });

        var (exit, output) = await ExecuteAsync(fixture, apply: true, hooks);

        Assert.Equal(1, exit);
        Assert.Equal("retained", CleanupDisposition(output, fixture.Worktree));
        Assert.All(calls, call => Assert.DoesNotContain("--force", call, StringComparer.Ordinal));
        Assert.DoesNotContain(calls, call => call.Contains("rm", StringComparer.OrdinalIgnoreCase));
        if (failure == "remove") Assert.True(Directory.Exists(fixture.Worktree));
        var receipt = Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).WorktreeCleanupReceipts!);
        Assert.Equal("retained", receipt.Disposition);
        Assert.Equal(failure == "remove" ? "git-worktree-remove-failed" : "postcondition-contradictory", receipt.ReasonCode);
    }

    private static async Task<(int ExitCode, string Output)> ExecuteAsync(
        ApplyFixture fixture,
        bool apply,
        QueueCommand.WorktreeApplyTestHooks? hooks = null)
    {
        var output = new StringWriter();
        hooks = (hooks ?? new QueueCommand.WorktreeApplyTestHooks()) with
        {
            LivenessProbe = QueueWorktreeLivenessProbe.Default with
            {
                BuildLockPath = Path.Combine(fixture.Home, "build.lock"),
            },
        };
        var exit = await QueueCommand.ExecuteAsync(
            new QueueOptions(QueueVerb.Worktrees, Format: QueueWorktreesOutputFormat.Json, Apply: apply),
            output, Ct, fixture.Source,
            RepositoryIdentityResolver.TryResolveAsync,
            NeverProvisionAsync,
            worktreeApplyTestHooks: hooks);
        return (exit, output.ToString());
    }

    private static IReadOnlyList<string> CandidatePaths(string output) => JsonDocument.Parse(output).RootElement
        .GetProperty("Workspaces").EnumerateArray()
        .Where(workspace => workspace.GetProperty("Classification").GetString() == "candidate")
        .Select(workspace => workspace.GetProperty("Path").GetString()!)
        .ToList();

    private static string CleanupDisposition(string output, string path) =>
        CleanupDispositionOrNull(output, path) ?? throw new Xunit.Sdk.XunitException("Cleanup disposition was absent.");

    private static string? CleanupDispositionOrNull(string output, string path) => JsonDocument.Parse(output).RootElement
        .GetProperty("Workspaces").EnumerateArray()
        .Single(workspace => string.Equals(workspace.GetProperty("Path").GetString(), path, StringComparison.OrdinalIgnoreCase))
        .TryGetProperty("CleanupDisposition", out var disposition) ? disposition.GetString() : null;

    private static Task<IssueWorktreeProvisioner.ProvisionedIssueWorktree> NeverProvisionAsync(
        int issue, string sourceRepository, string? worktreeRoot, string repository, bool lifecycle,
        TextWriter output, CancellationToken cancellationToken) =>
        Task.FromException<IssueWorktreeProvisioner.ProvisionedIssueWorktree>(new InvalidOperationException("not used by worktree cleanup"));

    private static async Task GitAsync(string directory, CancellationToken cancellationToken, params string[] arguments)
    {
        var (_, error, exitCode) = await RunGitAsync(directory, cancellationToken, arguments);
        Assert.True(exitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}");
    }

    private static async Task<string> GitOutputAsync(string directory, params string[] arguments)
    {
        var (output, error, exitCode) = await RunGitAsync(directory, Ct, arguments);
        Assert.True(exitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}");
        return output;
    }

    private static async Task<(string Output, string Error, int ExitCode)> RunGitAsync(
        string directory, CancellationToken cancellationToken, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start git.");
        await process.WaitForExitAsync(cancellationToken);
        return (await process.StandardOutput.ReadToEndAsync(cancellationToken), await process.StandardError.ReadToEndAsync(cancellationToken), process.ExitCode);
    }

    private sealed class ApplyFixture : IAsyncDisposable
    {
        private ApplyFixture(string home, IDisposable scope)
        {
            Home = home;
            _scope = scope;
        }

        private readonly IDisposable _scope;
        public string Home { get; }
        public string Source { get; private set; } = null!;
        public string Worktree { get; private set; } = null!;
        public string MovedWorktree { get; private set; } = null!;
        public string Head { get; private set; } = null!;
        public QueueItem Item { get; private set; } = null!;

        public static ApplyFixture Create()
        {
            var home = Path.Combine(Path.GetTempPath(), "baton_queue_apply_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(home);
            var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
            return new ApplyFixture(home, scope);
        }

        public async Task InitializeAsync(QueueItemState state = QueueItemState.Queued)
        {
            try
            {
                var source = Path.Combine(Home, "source");
                var worktree = Path.Combine(Home, "candidate");
                Directory.CreateDirectory(source);
                await GitAsync(source, Ct, "init", "-q", "--initial-branch", "main");
                await GitAsync(source, Ct, "remote", "add", "origin", "https://github.com/example/retained-worktrees.git");
                await File.WriteAllTextAsync(Path.Combine(source, "README.md"), "fixture", Ct);
                await GitAsync(source, Ct, "add", "README.md");
                await GitAsync(source, Ct, "-c", "user.name=Baton Test", "-c", "user.email=test@example.invalid", "commit", "-q", "-m", "base");
                await GitAsync(source, Ct, "worktree", "add", "-q", "-b", Branch, worktree);
                var head = (await GitOutputAsync(worktree, "rev-parse", "HEAD")).Trim();
                var item = new QueueItem
                {
                    Tag = "2151-lane",
                    Role = "implement",
                    Workspace = worktree,
                    SpecFile = Path.Combine(Home, "brief.md"),
                    WorkspaceOrigin = WorkspaceOrigins.IssueProvisioned,
                    Repository = Repository,
                    Branch = Branch,
                    State = state,
                    Retirement = state == QueueItemState.Cancelled ? null
                        : new QueueRetirement(QueueRetirement.Operator, DateTimeOffset.UtcNow, "fixture retired"),
                };
                await QueueStore.MutateAsync(BatonPaths.QueueFile, snapshot => snapshot with { Items = [item] }, Ct);
                Source = source;
                Worktree = worktree;
                MovedWorktree = Path.Combine(Home, "moved");
                Head = head;
                Item = item;
            }
            catch
            {
                _scope.Dispose();
                DirectoryCleanup.DeleteRecursively(Home);
                throw;
            }
        }

        public ValueTask DisposeAsync()
        {
            _scope.Dispose();
            DirectoryCleanup.DeleteRecursively(Home);
            return ValueTask.CompletedTask;
        }
    }
}
