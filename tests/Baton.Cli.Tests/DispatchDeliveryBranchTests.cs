using System.Diagnostics;
using Baton.Cli.Tests.TestSupport;
using Baton.Status;
using Baton.Tests.Shared;
using Baton.Vendors;

namespace Baton.Cli.Tests;

/// <summary>
/// #1944 end to end, against real git checkouts: <c>baton dispatch</c> records the workspace's branch as
/// the room's delivery branch without the lane declaring anything, and records nothing when the
/// workspace is on trunk.
/// </summary>
/// <remarks>
/// <b>Real repositories rather than a stubbed branch probe, because the claim is about the probe.</b>
/// <see cref="RoomDeliveryBranchTests"/> already owns the write policy in isolation; what is unproven
/// without git is that dispatch reaches that policy at all and hands it what
/// <c>git rev-parse --abbrev-ref HEAD</c> actually answers in the workspace it was pointed at. A double
/// here would assert the wiring against the same assumption that wrote it. Each repository is a fresh
/// <c>git init</c> under <c>%TEMP%</c> with one commit — the checkouts are throwaway and touch no
/// network.
/// </remarks>
[Collection(SerializedEnvironmentCollection.Name)]
public sealed class DispatchDeliveryBranchTests : IDisposable
{
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter> { ["fake"] = new ContractOutputWorkerAdapter(satisfyOutputs: true) };

    private readonly IsolatedBatonHome _batonHome = new();
    private readonly IDisposable _catalogScope;
    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), $"baton-1944-dispatch-{Guid.NewGuid():N}");

    // The same catalog pinning DispatchCommandEndToEndTests documents: without it these resolve through
    // an operator's local role/template overrides on a machine that has them.
    public DispatchDeliveryBranchTests() =>
        _catalogScope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Current with
        {
            WorkerRolesPathOverride = Path.Combine(AppContext.BaseDirectory, "WorkerRoles.json"),
            WorkerTiersPathOverride = Path.Combine(AppContext.BaseDirectory, "WorkerTiers.json"),
            WorkflowTemplatesPathOverride = Path.Combine(AppContext.BaseDirectory, "WorkflowTemplates.json"),
        });

    public void Dispose()
    {
        _catalogScope.Dispose();
        _batonHome.Dispose();
        ForceDeleteSandbox();
    }

    /// <summary>
    /// The two arms are one test because each is only meaningful against the other: an assertion that
    /// trunk records nothing passes trivially in a build where dispatch records nothing at all.
    /// </summary>
    [Theory]
    [InlineData("1944-lane", true)]
    [InlineData("main", false)]
    public async Task A_dispatch_records_its_workspace_branch_unless_the_workspace_is_on_trunk(
        string branch, bool recorded)
    {
        var workspace = await InitRepositoryAsync(branch);
        var roomDirectory = Path.Combine(_sandbox, $"room-{branch.Replace('/', '-')}");
        var specPath = Path.Combine(_sandbox, $"spec-{branch.Replace('/', '-')}.md");
        await File.WriteAllTextAsync(specPath, "## Do\n\nWeigh the options for X.\n", TestContext.Current.CancellationToken);

        var options = new DispatchOptions(
            "advise", specPath, roomDirectory, Adapter: "fake", WorkspaceDirectory: workspace);
        await DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken);

        Assert.Equal(recorded, File.Exists(Path.Combine(roomDirectory, DeliveryReferenceOutputNames.Branch)));
        Assert.Equal(recorded ? branch : null, RoomDeliveryBranch.TryRead(roomDirectory));
    }

    /// <summary>
    /// The detached-<c>HEAD</c> guard, which nothing else in the suite discriminates.
    /// </summary>
    /// <remarks>
    /// <c>git rev-parse --abbrev-ref HEAD</c> answers the literal string <c>HEAD</c> for a detached
    /// checkout, and <c>RoomDeliveryBranch.IsDeliveryBranch("HEAD")</c> is <see langword="true"/> —
    /// <c>HEAD</c> is not a trunk name — so the single clause in
    /// <c>WorkspaceHead.TryReadBranchAsync</c> is the only thing standing between a detached workspace
    /// and a room recording <c>HEAD</c> as a join key no pull request's head ref can match. Delete that
    /// clause and this is the only test that goes red. Detached checkouts are a shape this system
    /// routinely provisions rather than a contrivance: <c>RoleDispatch.ToBinding</c> builds an audited
    /// role's worktree as <c>new WorktreeWorkspace(workingDirectory, "HEAD")</c>.
    /// </remarks>
    [Fact]
    public async Task A_dispatch_whose_workspace_is_on_a_detached_head_records_nothing()
    {
        var workspace = await InitRepositoryAsync("1944-detached");
        await RunGitAsync(workspace, "checkout", "--detach");

        var roomDirectory = Path.Combine(_sandbox, "room-detached");
        var specPath = Path.Combine(_sandbox, "spec-detached.md");
        await File.WriteAllTextAsync(specPath, "## Do\n\nWeigh the options for X.\n", TestContext.Current.CancellationToken);

        var options = new DispatchOptions(
            "advise", specPath, roomDirectory, Adapter: "fake", WorkspaceDirectory: workspace);
        await DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken);

        Assert.False(File.Exists(Path.Combine(roomDirectory, DeliveryReferenceOutputNames.Branch)));
        Assert.Null(RoomDeliveryBranch.TryRead(roomDirectory));
    }

    /// <summary>A fresh repository on <paramref name="branch"/> with one commit, so <c>HEAD</c> resolves.</summary>
    private async Task<string> InitRepositoryAsync(string branch)
    {
        var repository = Path.Combine(_sandbox, $"repo-{branch.Replace('/', '-')}");
        Directory.CreateDirectory(repository);

        await RunGitAsync(repository, "init", "--initial-branch", branch);
        await File.WriteAllTextAsync(
            Path.Combine(repository, "README.md"), "fixture", TestContext.Current.CancellationToken);
        await RunGitAsync(repository, "add", "README.md");
        await RunGitAsync(
            repository,
            "-c", "user.name=Baton Test", "-c", "user.email=test@example.invalid",
            "commit", "-m", "fixture");
        return repository;
    }

    private static async Task RunGitAsync(string workingDirectory, params string[] args)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)!;
        var (_, stderr) = await BoundedProcessWait.RunToExitAsync(process, TimeSpan.FromSeconds(30));
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stderr}");
    }

    /// <summary>
    /// git leaves committed object files read-only, which Windows' own recursive delete refuses
    /// regardless of the parent directory's permissions — the same clearing pass
    /// <c>WorkingDirectoryEndToEndTests</c> documents for its own git-repo fixture.
    /// </summary>
    private void ForceDeleteSandbox()
    {
        if (!Directory.Exists(_sandbox))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(_sandbox, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        DirectoryCleanup.DeleteRecursively(_sandbox);
    }
}
