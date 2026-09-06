namespace Baton.Architecture.Tests;

/// <summary>
/// #703's enforcement half. The invariant is one sentence — <b>AER must never spawn a vendor CLI
/// worker where its <c>PreToolUse</c> gate does not fire</b> (decision 0029) — and it was false on a
/// whole spawn path for months because nothing checked. Making it true once is worth little; this is
/// what makes a NEW ungated spawn fail the build instead of waiting for a review to notice.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it checks:</b> every site in <c>src/</c> that starts a process is on a reviewed list. It
/// deliberately does not try to decide whether a given site is gated — that is a property of the
/// arguments built at runtime, which no file scan can honestly assert. It asserts the weaker, real
/// thing: the set of places a process can be spawned does not grow silently.
/// </para>
/// <para>
/// <b>Its false negatives, named rather than left for someone to discover.</b> A check that reads as
/// enforcement while being trivially sidesteppable is worse than none.
/// </para>
/// <list type="number">
/// <item>Reflection or a delegate (<c>typeof(Process).GetMethod("Start")</c>) matches no text here.</item>
/// <item>An approved site spawning something that itself spawns a vendor CLI — a shell script, the
/// Go sidecar — is a grandchild this cannot see.</item>
/// <item>An approved site silently dropping its gate arguments — each adapter's own <c>Resolve</c>
/// tests cover the two shipped adapters; nothing covers a third that has not been written.</item>
/// </list>
/// <para>
/// Pure file reading over the repo, no project references, matching <see cref="ReferenceDirectionTests"/>.
/// </para>
/// </remarks>
public class VendorSpawnGateTests
{
    /// <summary>
    /// Every file permitted to start a process, with why it is not an ungated vendor spawn. Adding a
    /// line here is the deliberate act this test exists to force — and it is a review prompt, not a
    /// formality: if the new site spawns <c>claude</c> or <c>agy</c>, it needs the mandatory
    /// <c>PreToolUse</c> hook (decision 0029) wired the way each adapter's own <c>Resolve</c> wires it
    /// before it belongs on this list.
    /// </summary>
    private static readonly Dictionary<string, string> ApprovedSpawnSites = new()
    {
        ["src/Baton/Dispatch/CoreDispatcher.cs"] = "The gated dispatch path. Adapters build the gate into the target.",
        ["src/Baton/Core/Internal/BatonProcessRunner.cs"] = "The managed spawn primitive BatonTask.Run/RunAsync bottoms out into (#1474). Previously invisible to this scan -- the same spawn happened across the FFI boundary inside native/core's Rust Command::new -- now visible because the port is plain C#. Gating happens upstream: an adapter builds the PreToolUse gate into the CoreDispatchTarget before CoreDispatcher ever constructs a BatonTask, so this file spawns whatever CoreDispatcher hands it, already gated.",
        ["src/Baton.Vendors/AgyWorkerAdapter.cs"] = "Read-only agy registry queries (models/agent/plugin list) — no -p, no tool execution.",
        ["src/Baton.Vendors/CodexWorkerAdapter.cs"] = "Read-only Codex app-server model-list discovery — no exec turn, no model inference, no command/tool execution. Worker turns still flow through CoreDispatcher's gated dispatch path.",
        ["src/Baton.Vendors/CodexAppServerBroker.cs"] = "#1853: spawns Codex app-server only after the adapter has replaced every native mutation/tool surface with Baton-owned dynamic tools. Codex has no PreToolUse hook; CodexDynamicToolPolicy is the mandatory gate here. The broker disables shell/unified-exec/MCP/apps/browser/computer/image/multi-agent before starting a turn; Code Mode remains only as the constrained orchestrator over those Baton-owned nested tool definitions.",
        ["src/Baton.Vendors/CodexDynamicToolPolicy.cs"] = "#1853: spawns a model-requested command only from the broker's baton_run_command callback, after ShellCommandPatternMatcher has applied the canonical allow list, standing deny list, chain segmentation, and position-independent denied-option check. It never spawns a vendor CLI directly.",
        ["src/Baton.Cli/WorkspaceHead.cs"] = "Read-only 'git rev-parse HEAD' to capture a capture step's base ref — git, not a vendor CLI; no -p, no tool execution.",
        ["src/Baton.Cli/RepositoryIdentityResolver.cs"] = "#1849: read-only 'git config --get remote.origin.url' and 'git rev-parse --git-common-dir' to derive the cost ledger's canonical repository key — git, not a vendor CLI; no -p, no tool execution, spawns no vendor process. Same rationale as WorkspaceHead.cs above, and it resolves to null rather than throwing on any failure.",
        ["src/Baton/Workspaces/WorktreeProvisioner.cs"] = "'git worktree add/remove' plus 'git status' to provision and tear down a worker's workspace (#669) — git, not a vendor CLI; spawns no vendor process.",
        ["src/Baton/Mutation/VerifyRunner.cs"] = "#1623: the engine-run verify step. Spawns 'pixi run <task>' (e.g. gates-quiet) after a worker's own execution already exited 0 with a satisfied contract — never a vendor CLI, and never invoked from inside a worker's own turn.",
        ["src/Baton/Mutation/VerifyStepRunner.cs"] = "#1882: the zero-token verify step run BEFORE a review lane's first turn. Spawns 'python tools/buildlock.py <allowlisted command>' -- the shapes VerifyStepCommandParser admits are dotnet build/test and a repo --check/--selftest script, never a vendor CLI -- with no model in the loop and no worker turn in progress. The review role's own shell grant is untouched by it.",
        ["src/Baton.Cli/WorkstreamJunctionLinker.cs"] = "'cmd.exe /c mklink /J' to create a --workstream navigation link (#1619) — a Windows shell built-in, not a vendor CLI; no -p, no tool execution, spawns no vendor process.",
        ["src/Baton.Vendors/AgyHookLivenessProbe.cs"] = "#1680 (F6, #1732 review): spawns the platform shell ('cmd /c' on Windows, 'sh -c' on Unix) running the identical 'dotnet <Baton.Cli.dll> agy-hook-check' command string BuildHooksJson writes into hooks.json — AER's OWN hook binary, not a vendor CLI — once at resolve time, to confirm the PreToolUse gate itself is live before a worker whose only narrowing is that gate is ever dispatched. This IS the gate's own liveness check, run before the vendor spawn AgyWorkerAdapter.Resolve constructs; it never starts agy or any other vendor process.",
        ["src/Baton.Cli/WatchNotifier.cs"] = "#1488: spawns the platform shell to run an OPERATOR-authored '--notify <command>' target once a watched room reaches Terminal — never a vendor CLI, no dispatch, no PreToolUse surface. Payload delivery and the trust model are spec/baton.md §2's contract, not restated here.",
        ["src/Baton.Cli/Daemon/IGhCliRunner.cs"] = "#734: spawns 'gh pr view --json ...', a read-only forge query the delivery poller uses to record PR/checks/merge facts — gh, not a vendor CLI (claude/agy); no -p, no tool execution, spawns no vendor process.",
        ["src/Baton.Vendors/ClaudeHookLivenessProbe.cs"] = "#532: spawns 'dotnet <Baton.Cli.dll> hook-check' directly (exec form, matching the settings.json ClaudeWorkerAdapter writes) — AER's OWN hook binary, not a vendor CLI — once at resolve time, to confirm the mandatory PreToolUse gate itself is live before a worker whose writes rely solely on that gate is ever dispatched. This IS the gate's own liveness check, run before the vendor spawn ClaudeWorkerAdapter.Resolve constructs; it never starts claude or any other vendor process.",
        ["src/Baton.Vendors/ClaudeUsageSlashCommandSource.cs"] = "#1391: spawns 'claude -p \"/usage\" --output-format text' — a read-only headless slash command that reports the CLI's own plan usage. No tool execution is possible from a slash command's own reply, so decision 0029's PreToolUse gate has nothing to guard here, matching AgyWorkerAdapter's own read-only registry-query rationale above. Never goes through ClaudeWorkerAdapter.Resolve's gated worker dispatch.",
        ["src/Baton.Vendors/AgyUsageSlashCommandSource.cs"] = "#1391: spawns 'agy -p \"/usage\"' — the same read-only headless usage query as ClaudeUsageSlashCommandSource, for agy. No tool execution, no gate to guard; never goes through AgyWorkerAdapter.Resolve.",
        ["src/Baton.Cli/IssueWorktreeProvisioner.cs"] = "#1934 slice 1: spawns 'gh issue develop <n> --name <n>-lane' and 'git worktree add <root>/w<n> <n>-lane' from `baton queue add --issue`, to provision the workspace a queued item will run in — gh and git, not a vendor CLI (claude/agy/codex); no -p, no tool execution, no vendor process. The third step of the same provisioning, trusting the workspace, is a ProjectCeilingStore write in-process rather than a fourth spawn. Spawned at ADD time in the CLI, never from the daemon's scheduler, so no vendor-spawn surface moves into the background host. Hang safety is the TIME BOUND, not the environment: each spawn is abandoned and the child killed after IssueWorktreeProvisioner.SpawnTimeout (2 minutes) or when the caller's token fires. GIT_TERMINAL_PROMPT=0 and GCM_INTERACTIVE=never only make a credential prompt less likely, the same disclosed limit WorkspaceDeliveryProbe records.",
        ["src/Baton.Cli/WorkspaceDeliveryProbe.cs"] = "#1901: spawns 'git rev-parse', 'git diff --numstat' and 'gh pr list --json number' against a room's workspace at settle, to stamp issue, PR and diff shape on the cost-ledger row — git and gh, not a vendor CLI, the same read-only forge/repo questions IGhCliRunner and DeliveryVerifier already ask; every spawn goes through one injected CommandRunner and fails open. Hang safety is the TIME BOUND, not the environment: each spawn is abandoned and the child killed after WorkspaceDeliveryProbe.SpawnTimeout (20s, three spawns per distinct workspace) or when the host's own cancellation fires, so a Ctrl-C reaches it and a wedged 'gh' costs that workspace's facts rather than the settle. GIT_TERMINAL_PROMPT=0 and GCM_INTERACTIVE=never only make a credential prompt less likely — DeliveryVerifier's own doc records that they do not stop an OS credential manager, which is why the bound is what this line rests on.",
    };

    private static readonly string[] SpawnMarkers = ["new ProcessStartInfo", "Process.Start", "new BatonTask"];

    [Fact]
    public void No_unreviewed_site_in_src_can_start_a_process()
    {
        var root = RepoRoot();
        var found = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => SpawnMarkers.Any(marker => File.ReadAllText(path).Contains(marker, StringComparison.Ordinal)))
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        var unreviewed = found.Where(path => !ApprovedSpawnSites.ContainsKey(path)).ToList();
        Assert.True(
            unreviewed.Count == 0,
            "A new process-spawn site appeared in src/:\n  " + string.Join("\n  ", unreviewed)
            + "\n\nIf it can spawn a vendor CLI it needs the mandatory PreToolUse hook first — decision "
            + "0029 makes that hook mandatory on every worker AER spawns, and #703 is what happens when a "
            + "path skips it. Then add it to ApprovedSpawnSites with the reason it is safe.");

        // The other direction, so the list cannot rot into naming files that no longer spawn anything
        // and quietly stop meaning what it says.
        var stale = ApprovedSpawnSites.Keys.Where(path => !found.Contains(path)).ToList();
        Assert.True(
            stale.Count == 0,
            "ApprovedSpawnSites names files that no longer start a process:\n  " + string.Join("\n  ", stale));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "pixi.toml")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir.FullName;
    }
}
