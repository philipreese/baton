using System.Diagnostics;
using Baton.Vendors;
using Baton.Cli.Tests.TestSupport;
using Baton.Domain;
using Baton.Status;

namespace Baton.Cli.Tests;

/// <summary>
/// R1 (#1354/#1380, finding 9's item 1+2): the acceptance path the prior PR's own tests never
/// exercised — a real audited role dispatch against a real git workspace, not a bare temp directory
/// (<c>RoleDispatchTests</c>' old worktree test built one with <c>Directory.CreateDirectory</c> and
/// could never actually provision) and not a happy-path adapter that never enters the audited branch at
/// all (<c>DispatchCommandEndToEndTests</c>' <c>--output</c> test dispatches to an adapter the registry
/// does not know, so the grant never flips). <see cref="ContractOutputWorkerAdapter"/> is registered
/// under the key <c>"agy"</c> here so <c>RoleDispatch.ToBinding</c>'s
/// <c>WorkerAdapterRegistry.Default</c> lookup resolves the real <c>AgyWorkerAdapter</c>'s
/// <c>WithheldWritesReachTheOutbox</c> (false) and flips the grant to <c>AuditedNotEnforced</c>, while
/// the process actually dispatched is still this file's fake — no live vendor needed.
/// </summary>
// #1524: enrolled for Console.Out only now, per SerializedEnvironmentCollection's remarks.
[Collection(SerializedEnvironmentCollection.Name)]
public sealed class DispatchAuditedWorktreeAcceptanceTests : IDisposable
{
    private readonly IsolatedBatonHome _batonHome = new();
    private readonly IDisposable _catalogScope;

    // Catalog pinning: same #1524 BeginScope pattern as DispatchCommandEndToEndTests' own ctor.
    public DispatchAuditedWorktreeAcceptanceTests()
    {
        _catalogScope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Current with
        {
            WorkerRolesPathOverride = Path.Combine(AppContext.BaseDirectory, "WorkerRoles.json"),
            WorkerTiersPathOverride = Path.Combine(AppContext.BaseDirectory, "WorkerTiers.json"),
        });
    }

    public void Dispose()
    {
        _catalogScope.Dispose();
        _batonHome.Dispose();
    }

    [Fact]
    public async Task Dispatching_fact_check_on_agy_against_a_real_git_workspace_auto_provisions_and_satisfies_the_contract_with_output()
    {
        // #1456: this file used "review" for the flat write_files:false/run_shell_commands:false/
        // network_access:false shape every read-only role carried before that change. review no
        // longer has it (a scoped shell now, refused outright on agy — exercised against the real
        // adapter in AgyWorkerAdapterTests' scoped-shell refusal fact, not in this file, whose fakes
        // always translate grants successfully); fact-check still does, and is what this R1
        // acceptance path (audited-write worktree provisioning) is actually about -- a shape any
        // read-only, write-widened-on-agy role exercises identically.
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-agy-e2e-{Guid.NewGuid():N}");
        try
        {
            var workspace = Path.Combine(testRoot, "workspace");
            await InitGitRepoAsync(workspace);

            var specPath = await WriteSpecAsync(testRoot, "Confirm the facts.");
            var roomDirectory = Path.Combine(testRoot, "task");
            var outputPath = Path.Combine(testRoot, "findings-out.md");
            var adapters = await AgyFakeAdaptersAsync(testRoot);

            var options = new DispatchOptions(
                "fact-check", specPath, roomDirectory, Adapter: "agy", WorkspaceDirectory: workspace, OutputPath: outputPath);

            var result = await DispatchCommand.ExecuteAsync(options, adapters, TestContext.Current.CancellationToken, evaluateRunway: RunwayTestGate.Admit);

            Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
            var step = Assert.Single(result.State.Steps);
            Assert.Equal(StepStatus.Succeeded, step.Status);
            Assert.True(File.Exists(outputPath), "the --output copy of findings.md should have landed");

            // The binding that actually ran was audited and provisioned, not enforced against the
            // caller's own workspace directly — the whole point of R1.
            var bindingsPath = Path.Combine(roomDirectory, "bindings.json");
            Assert.Contains(
                "AuditedNotEnforced", await File.ReadAllTextAsync(bindingsPath, TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Dispatching_fact_check_on_agy_prints_the_audited_write_grant_not_a_bare_write()
    {
        // #1355: the printed grant line has to name the audited-not-enforced write it actually
        // resolved to, not just "write" -- otherwise an invoking agent relaying the line to its own
        // permission layer under-reports what the run really carried. #1456: fact-check stands in for
        // review here now -- see the fact-check test above this class's own note.
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-agy-grant-line-{Guid.NewGuid():N}");
        var originalOut = Console.Out;
        try
        {
            var workspace = Path.Combine(testRoot, "workspace");
            await InitGitRepoAsync(workspace);

            var specPath = await WriteSpecAsync(testRoot, "Confirm the facts.");
            var roomDirectory = Path.Combine(testRoot, "task");
            var adapters = await AgyFakeAdaptersAsync(testRoot, translatesGrants: true);

            var options = new DispatchOptions("fact-check", specPath, roomDirectory, Adapter: "agy", WorkspaceDirectory: workspace);

            using var consoleOutput = new StringWriter();
            Console.SetOut(consoleOutput);
            await DispatchCommand.ExecuteAsync(options, adapters, TestContext.Current.CancellationToken, evaluateRunway: RunwayTestGate.Admit);
            Console.SetOut(originalOut);

            Assert.Contains(
                "Grant: read, write (workspace-wide inside an isolated worktree; audited against declared outputs after the run), no-shell, no-network",
                consoleOutput.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    // #1456: review's own shell-grant refusal on agy is asserted where it is real -- this file's
    // fakes implement IPermissionGrantTranslator to always succeed (GrantConsumingContractOutput-
    // WorkerAdapter.TryTranslatePermissionGrant, deliberately, for the two grant-line tests above),
    // so dispatching "review" through them here would prove nothing about the real AgyWorkerAdapter.
    // The real adapter's refusal is exercised directly in AgyWorkerAdapterTests (its scoped-shell
    // refusal fact).

    [Fact]
    public async Task Dispatching_fact_check_on_agy_against_a_workspace_that_is_itself_a_worktree_with_an_untracked_file_still_succeeds()
    {
        // The red test for finding 1/R1: before this fix, IsWorktree(workspace) == true routed the
        // caller's OWN directory in as WorkingDirectory (stamped IsWorktree: true without this run
        // having provisioned it), so the post-run audit inspected the caller's own untracked file and
        // failed Permanent. R1 provisions a fresh worktree regardless of the caller's directory shape,
        // so the caller's own dirt must be irrelevant to the outcome. #1456: fact-check stands in for
        // review -- see this class's top-of-file test for why.
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-agy-worktree-e2e-{Guid.NewGuid():N}");
        try
        {
            var mainRepo = Path.Combine(testRoot, "main-repo");
            await InitGitRepoAsync(mainRepo);

            var workspace = Path.Combine(testRoot, "caller-worktree");
            await RunGitAsync(mainRepo, "worktree", "add", "--detach", workspace, "HEAD");
            // The caller's own uncommitted dirt — untracked, never staged or committed. Under the old
            // behaviour this alone was enough to fail the post-run audit.
            await File.WriteAllTextAsync(
                Path.Combine(workspace, "operators-scratch-file.txt"), "not the worker's business",
                TestContext.Current.CancellationToken);

            var specPath = await WriteSpecAsync(testRoot, "Confirm the facts.");
            var roomDirectory = Path.Combine(testRoot, "task");
            var adapters = await AgyFakeAdaptersAsync(testRoot);

            var options = new DispatchOptions("fact-check", specPath, roomDirectory, Adapter: "agy", WorkspaceDirectory: workspace);

            var result = await DispatchCommand.ExecuteAsync(options, adapters, TestContext.Current.CancellationToken, evaluateRunway: RunwayTestGate.Admit);

            Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
            var step = Assert.Single(result.State.Steps);
            Assert.Equal(StepStatus.Succeeded, step.Status);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Dispatching_an_audited_role_prints_a_skill_roster_that_names_the_repo_it_scanned_not_the_worktree()
    {
        // #1512 H1 (second-reader finding) -- see DispatchCommand.cs's skill-roster block for why a
        // worktree-provisioned binding's roster can only honestly describe the source repo it scans.
        // Before this fix the label was a bare "Skills:" that claimed no less than an ordinary
        // roster would. Pins the fix: the label now discloses the scope, and the scan target itself
        // is still the source repo (proven via the fake's own LastDiscoverCapabilitiesWorkingDirectory).
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-agy-h1-{Guid.NewGuid():N}");
        var originalOut = Console.Out;
        try
        {
            var workspace = Path.Combine(testRoot, "workspace");
            await InitGitRepoAsync(workspace);

            // Untracked by construction -- exactly the case H1 describes: `git ls-files` never sees
            // it, so the worker's fresh worktree checkout at HEAD will not have it either.
            var untrackedSkillDir = Path.Combine(workspace, ".claude", "skills", "untracked-skill");
            Directory.CreateDirectory(untrackedSkillDir);
            await File.WriteAllTextAsync(
                Path.Combine(untrackedSkillDir, "SKILL.md"), "description: Untracked skill",
                TestContext.Current.CancellationToken);

            var specPath = await WriteSpecAsync(testRoot, "Confirm the facts.");
            var roomDirectory = Path.Combine(testRoot, "task");
            var fakeAdapter = new ContractOutputWorkerAdapter(
                satisfyOutputs: true,
                capabilities: new List<WorkerCapabilityItem> { new("untracked-skill", "skill", "Untracked skill") });
            var adapters = new Dictionary<string, IWorkerAdapter> { ["agy"] = fakeAdapter };

            var options = new DispatchOptions("fact-check", specPath, roomDirectory, Adapter: "agy", WorkspaceDirectory: workspace);

            using var consoleOutput = new StringWriter();
            Console.SetOut(consoleOutput);
            await DispatchCommand.ExecuteAsync(options, adapters, TestContext.Current.CancellationToken, evaluateRunway: RunwayTestGate.Admit);
            Console.SetOut(originalOut);

            var output = consoleOutput.ToString();
            Assert.Contains(
                $"Skills (from {workspace}; the worker runs in a fresh worktree at HEAD): untracked-skill", output);
            // Never the bare, non-scoped label an ordinary (non-worktree) dispatch prints -- that
            // would claim more than this dispatch actually knows.
            Assert.DoesNotContain("Skills: untracked-skill", output);
            Assert.Equal(workspace, fakeAdapter.LastDiscoverCapabilitiesWorkingDirectory);
        }
        finally
        {
            Console.SetOut(originalOut);
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// #1941 re-review LOW. The test above pins the SCAN-derived form of H1's worktree disclosure; the
    /// declared-set branch (<c>DispatchCommand</c>'s skill-roster block) prints its own copy and nothing
    /// asserted it, so deleting the clause was a silent regression once already. The polarity partner is
    /// <c>DispatchCommandSkillRosterTests</c>' plain <c>"Skills (declared): house-style"</c> arm, which
    /// runs on a binding the registry never widens and so never gives a worktree.
    /// </summary>
    [Fact]
    public async Task Dispatching_an_audited_role_with_a_declared_skill_discloses_the_worktree_on_the_declared_roster_line()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-agy-declared-skill-{Guid.NewGuid():N}");
        var originalOut = Console.Out;
        try
        {
            var workspace = Path.Combine(testRoot, "workspace");
            await InitGitRepoAsync(workspace);

            var library = Path.Combine(testRoot, "library");
            var declaredPackage = Path.Combine(library, "house-style");
            Directory.CreateDirectory(declaredPackage);
            await File.WriteAllTextAsync(
                Path.Combine(declaredPackage, "SKILL.md"), "description: House style",
                TestContext.Current.CancellationToken);

            using var skillsScope = BatonEnvironmentSnapshot.BeginScope(
                BatonEnvironmentSnapshot.Current with { SkillsPathOverride = library });

            var specPath = await WriteSpecAsync(testRoot, "Confirm the facts.");
            var roomDirectory = Path.Combine(testRoot, "task");
            var adapters = new Dictionary<string, IWorkerAdapter> { ["agy"] = new ContractOutputWorkerAdapter(satisfyOutputs: true) };

            var options = new DispatchOptions(
                "fact-check", specPath, roomDirectory, Adapter: "agy", WorkspaceDirectory: workspace,
                Skills: ["house-style"]);

            using var consoleOutput = new StringWriter();
            Console.SetOut(consoleOutput);
            await DispatchCommand.ExecuteAsync(options, adapters, TestContext.Current.CancellationToken, evaluateRunway: RunwayTestGate.Admit);
            Console.SetOut(originalOut);

            var output = consoleOutput.ToString();
            Assert.Contains(
                "Skills (declared; a <workspace>/skills/ name re-resolves in the worker's fresh worktree at HEAD): house-style",
                output,
                StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(originalOut);
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <param name="translatesGrants">
    /// F2/F3: the printed-grant-line test needs the bound "agy" adapter to actually consume a grant
    /// (<see cref="IPermissionGrantTranslator"/>) or <see cref="DispatchCommand"/> now prints nothing
    /// for it. The other two tests here assert on run outcome, not the grant line, so they keep the
    /// plain <see cref="ContractOutputWorkerAdapter"/> that sits outside that population -- narrower
    /// than opting every acceptance test here into WorkerBindingResolver's grant-consuming refusal
    /// checks for no reason.
    /// </param>
    private static async Task<IReadOnlyDictionary<string, IWorkerAdapter>> AgyFakeAdaptersAsync(
        string testRoot, bool translatesGrants = false)
    {
        // A minimal conforming ReviewVerdict (decision 0043: the engine checks only that it PARSES as
        // one — ReviewedRef required, empty Findings valid).
        var verdictFixture = Path.Combine(testRoot, "verdict-fixture.json");
        Directory.CreateDirectory(testRoot);
        await File.WriteAllTextAsync(
            verdictFixture, """{"reviewedRef":"HEAD","findings":[]}""", TestContext.Current.CancellationToken);

        var outputFixtures = new Dictionary<string, string> { ["verdict.json"] = verdictFixture };
        IWorkerAdapter agyAdapter = translatesGrants
            ? new GrantConsumingContractOutputWorkerAdapter(satisfyOutputs: true, outputFixtures)
            : new ContractOutputWorkerAdapter(satisfyOutputs: true, outputFixtures);

        return new Dictionary<string, IWorkerAdapter> { ["agy"] = agyAdapter };
    }

    private static async Task<string> WriteSpecAsync(string directory, string content)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "spec.md");
        await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
        return path;
    }

    private static async Task InitGitRepoAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        await RunGitAsync(directory, "init", "-q");
        // -c identity keeps the commit independent of any (absent) global git config on the runner.
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
