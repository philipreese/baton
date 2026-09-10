using Baton.Domain;
using Baton.Mutation;
using Baton.Tests.Shared;
using Baton.Vendors.Tests.TestSupport;
using Xunit;

namespace Baton.Vendors.Tests;

/// <summary>
/// #1759: the C# home for the assertion <c>tools/audit-completeness/selfcheck.py</c>'s
/// <c>_templates_are_dispatchable</c> made in Python before <c>tools/baton-agy-loop/dispatch.py</c> was
/// retired — every role in the catalog must actually be dispatchable, not merely internally consistent.
/// <see cref="WorkflowTemplateCatalog"/>'s only shipped template (<c>implement-review</c>) composes its
/// phases entirely from <see cref="WorkerRoleCatalog"/> roles (each phase's <c>RoleId</c> resolves
/// through it, enforced at catalog load), so iterating every catalog role covers every ROLE a built-in
/// template can name. It does not cover the composed-template dispatch path itself:
/// <see cref="WorkflowTemplateComposer"/> materializes phases with worktree auto-provisioning withheld,
/// so <c>baton dispatch implement-review --adapter agy</c> reaches the write-withheld <c>review</c>
/// phase without the provisioned worktree the audited-write widening demands and is refused there —
/// a pre-existing shape neither this walk nor the retired Python check modelled (#1765 review).
///
/// <para>
/// <b>The production path, not just the coherence rule.</b> <c>grant_refusal</c>'s Python check asked
/// only "is this grant self-consistent" (mirrored in C# by <c>WorkerBindingResolverTests</c>'s
/// <see cref="IncoherentPermissionGrantException"/>/<see cref="UnsatisfiableOutputContractException"/>
/// arms — not re-duplicated here). This class asks the question dispatch.py could not: does the role
/// actually run, through the SAME three calls <c>baton dispatch &lt;role&gt;</c> makes
/// (<c>RunCommand.cs</c>/<c>DispatchCommand.cs</c>) — <see cref="RoleDispatch.ToBinding"/>, then
/// <see cref="WorktreeWorkspaces.Provision"/> (a real <c>git worktree add</c> against a throwaway repo,
/// not a double), then <see cref="WorkerBindingResolver.Resolve"/> against the real
/// <see cref="WorkerAdapterRegistry.Default"/>. Skipping the middle call would be dishonest: every role
/// whose catalog entry withholds <c>WriteFiles</c> — stated as the rule rather than a count, because
/// the count here was already short by one before #2043 added a role to the set — is tiered to a
/// vendor other than <c>agy</c>, so forcing those roles onto <c>agy</c>
/// (#1759's own "for each real adapter") makes <see cref="RoleDispatch.ToBinding"/> widen the grant to
/// <see cref="GrantAuditMode.AuditedNotEnforced"/> (#901) — a mode <see cref="WorkerBindingResolver"/>
/// refuses outright (<see cref="UnisolatedGrantAuditException"/>) unless the worktree it demands was
/// actually provisioned, not merely declared. A test that stopped at <c>ToBinding</c> would report a
/// false refusal on exactly the shape #1386 exists to un-refuse.
/// </para>
/// </summary>
/// <remarks>
/// #1524/#1491: same collection as <see cref="RoleDispatchTests"/> and
/// <see cref="WorkflowTemplateComposerTests"/> — a non-mutating reader of the shipped catalog off
/// whatever <c>BatonEnvironmentSnapshot.Current</c> holds, so it must not race a test that repoints it.
/// </remarks>
[Collection(WorkerRoleCatalogCollection.Name)]
public sealed class TemplateDispatchabilityTests : IDisposable
{
    private static readonly string[] RealAdapters = ["claude", "agy"];

    private readonly string _sourceRepo =
        Path.Combine(Path.GetTempPath(), "baton-tdt-repo-" + Guid.NewGuid().ToString("N"));

    private readonly string _room =
        Path.Combine(Path.GetTempPath(), "baton-tdt-room-" + Guid.NewGuid().ToString("N"));

    // #1166 review finding H1: this class's own [Collection(WorkerRoleCatalogCollection.Name)]
    // serialises it only against the other two, non-mutating catalog readers in that collection --
    // LaunchConfigCollection is a DIFFERENT collection, so a class in that one (e.g.
    // ClaudeWorkerAdapterTests) still runs concurrently against this one, and both write through
    // AtomicLaunchConfigWriter to project-ceilings.json. xUnit collections are one-per-class, so
    // joining LaunchConfigCollection instead was not available without leaving this one -- an
    // isolated BATON_HOME sidesteps the conflict entirely: this class keeps the catalog collection
    // (its own doc comment explains why it needs that one) and gets its own store file instead of
    // racing anyone on the shared one.
    private readonly IsolatedBatonHome _home = new();

    public TemplateDispatchabilityTests()
    {
        Directory.CreateDirectory(_sourceRepo);
        InitGitRepository(_sourceRepo);
        Directory.CreateDirectory(_room);

        // #1166: this class dispatches real claude/agy adapters against _sourceRepo -- trust it
        // unrestricted so the ceiling gate is not what this class's own refusal assertions measure.
        ProjectCeilingStore.Set(_sourceRepo, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);
    }

    /// <summary>
    /// The ported assertion's positive half. Every catalog role, dispatched to each real adapter through
    /// the exact three-call production sequence, must resolve to a runnable
    /// <see cref="WorkerBinding.Process"/> rather than throw.
    /// </summary>
    [Fact]
    public void Every_catalog_role_dispatches_on_every_real_adapter_without_refusal()
    {
        var roles = WorkerRoleCatalog.All;
        Assert.NotEmpty(roles); // this compared nothing otherwise

        foreach (var role in roles)
        {
            foreach (var adapter in RealAdapters)
            {
                var workerName = $"{role.Id}-{adapter}";
                var binding = RoleDispatch.ToBinding(
                    role, "-- the operator's own prompt --", adapterOverride: adapter,
                    workerName: workerName, workingDirectory: _sourceRepo);

                var config = new Dictionary<string, WorkerBindingConfigEntry> { [workerName] = binding };
                // #1166 review finding A: a worktree-isolated role's WorkingDirectory becomes a fresh
                // directory under _room per worker -- ProjectCeilingGate keys the ceiling on
                // WorktreeSourceRepository (== _sourceRepo, trusted in the constructor) instead of that
                // ephemeral path for exactly this reason, so no per-worker trust registration is needed
                // here. If that wiring ever regresses, this loop is what goes red.
                var (provisioned, _) = WorktreeWorkspaces.Provision(config, _room);

                var resolved = WorkerBindingResolver.Resolve(provisioned, WorkerAdapterRegistry.Default);
                Assert.IsType<WorkerBinding.Process>(resolved[workerName]);
            }
        }
    }

    [Fact]
    public void Every_catalog_role_dispatches_through_the_codex_policy_broker()
    {
        var roles = WorkerRoleCatalog.All;

        foreach (var role in roles)
        {
            var workerName = $"{role.Id}-codex";
            var binding = RoleDispatch.ToBinding(
                role, "-- the operator's own prompt --", adapterOverride: "codex",
                workerName: workerName, workingDirectory: _sourceRepo);
            var config = new Dictionary<string, WorkerBindingConfigEntry> { [workerName] = binding };
            var (provisioned, _) = WorktreeWorkspaces.Provision(config, _room);

            var resolved = WorkerBindingResolver.Resolve(provisioned, WorkerAdapterRegistry.Default);
            var process = Assert.IsType<WorkerBinding.Process>(resolved[workerName]);
            Assert.Equal("dotnet", process.Target.Program);
            Assert.Equal("codex-broker", process.Target.Args[1]);
        }
    }

    /// <summary>
    /// The discriminating control the loop above needs to mean anything (docs/agents/developing-baton.md gate <c>v-and-v</c>):
    /// without it, a <see cref="WorkerBindingResolver.Resolve"/> call whose result nobody checked, or a
    /// refusal rule that had stopped firing entirely, would leave the assertion above green for the
    /// wrong reason. #1386's own "read-shaped grant on agy" shape (<c>write_files: false</c>) is NOT
    /// this control — under the real production path above it is exactly the shape #901's audited-write
    /// widening exists to un-refuse, and stays green (proven by the loop above actually exercising
    /// <c>review</c>/<c>patch</c>/<c>fact-check</c>/<c>orchestrate</c> forced onto <c>agy</c>, all of
    /// which take that shape). What still refuses even after widening and real worktree isolation is
    /// #529's shell-defeats-a-withheld-category rule — <c>RunShellCommands</c> granted without
    /// <c>NetworkAccess</c> and no read-only-shell exemption — asserted here on a synthetic role so the
    /// shipped catalog need not carry a broken fixture.
    /// </summary>
    [Fact]
    public void A_grant_the_shell_defeats_is_still_refused_after_agy_write_widening_and_real_worktree_isolation()
    {
        var broken = new WorkerRole(
            Id: "broken-shell-grant", Tier: "synthetic", Adapter: "claude", Model: null, Effort: null,
            Grant: new PermissionGrant(
                ReadFiles: true, WriteFiles: false, RunShellCommands: true, NetworkAccess: false),
            Timeout: TimeSpan.FromMinutes(5), ProducesVerdict: false, Purpose: "fixture",
            Outputs: [new WorkerRoleOutput("out.md", OutputSchema.None, "write out.md")]);

        const string workerName = "broken-shell-grant-agy";
        var binding = RoleDispatch.ToBinding(
            broken, "spec", adapterOverride: "agy", workerName: workerName, workingDirectory: _sourceRepo);

        var config = new Dictionary<string, WorkerBindingConfigEntry> { [workerName] = binding };
        var (provisioned, _) = WorktreeWorkspaces.Provision(config, _room);

        Assert.Throws<IncoherentPermissionGrantException>(
            () => WorkerBindingResolver.Resolve(provisioned, WorkerAdapterRegistry.Default));
    }

    private static void InitGitRepository(string path)
    {
        RunGitProcess(path, "init");
        RunGitProcess(path, "config", "user.name", "Test");
        RunGitProcess(path, "config", "user.email", "test@test.com");
        File.WriteAllText(Path.Combine(path, "README.md"), "init");
        RunGitProcess(path, "add", ".");
        RunGitProcess(path, "commit", "-m", "initial");
    }

    private static void RunGitProcess(string cwd, params string[] args)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }
        using var proc = System.Diagnostics.Process.Start(startInfo);
        proc?.WaitForExit();
    }

    public void Dispose()
    {
        if (Directory.Exists(_room))
        {
            DirectoryCleanup.DeleteRecursively(_room);
        }
        if (Directory.Exists(_sourceRepo))
        {
            DirectoryCleanup.DeleteRecursively(_sourceRepo);
        }
        _home.Dispose();
    }
}
