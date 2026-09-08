using Baton.Status;
using Baton.Vendors;

namespace Baton.Architecture.Tests;

/// <summary>
/// #2114 (contract: <c>spec/baton.md</c> §9, §11 C-11): <b>no lane role's shell grant reaches the
/// daemon's write verbs.</b> The invariant this serves (ruled 2026-09-08, recorded once at
/// spec/baton.md §9's #2114 paragraph): the page's write gate authenticates REMOTE callers by their
/// network identity and trusts anything arriving over loopback as the operator; a worker process runs
/// on that same machine, so from the gate's side a lane and the operator are indistinguishable. Only
/// the lane's ROLE GRANT separates them, which is why the containment is enforced HERE, on
/// <c>WorkerRoles.json</c>, before any dispatch reads it.
/// </summary>
/// <remarks>
/// <para>
/// Two families are probed. The CLI write verbs (<c>baton queue</c>, <c>resolve</c>, <c>cancel</c>,
/// <c>dispatch</c>, <c>redispatch</c>, <c>trust</c>, <c>memory add</c>) each mutate the queue, a room,
/// the trust register, or the memory store — the same writes the daemon's eventual <c>POST</c> routes
/// perform. The HTTP clients (<c>curl</c>, <c>wget</c>, PowerShell's <c>Invoke-WebRequest</c>/
/// <c>Invoke-RestMethod</c> and their <c>iwr</c>/<c>irm</c> aliases, <c>python -c</c>/<c>python -m
/// http</c>, <c>node -e</c>) are the spellings a lane would reach the daemon port with from its shell.
/// Reads stay admitted: <c>baton status</c>, <c>git diff</c>, <c>dotnet build</c> are the positive
/// controls below, so a role that admits nothing at all cannot pass this test by accident.
/// </para>
/// <para>
/// Measured against the real matcher, not a reimplementation: each probe is judged by
/// <see cref="ShellCommandPatternMatcher.EvaluateChainedCommand"/> exactly as <c>HookCheckCommand</c>,
/// <c>AgyHookCheckCommand</c> and <c>CodexDynamicToolPolicy</c> judge a live command under the same
/// grant, so a pattern that LOOKS like a deny but does not match under that matcher's tokenized-head
/// grammar (spec/baton.md §9, #1731) fails here rather than passing on its spelling. A role with
/// <c>run_shell_commands: false</c> admits nothing and passes trivially; a role with a non-empty
/// allowlist admits only what the allowlist matches; an unscoped role admits everything its deny
/// list does not name — which is why <c>implement</c> and <c>janitor</c> are the roles this test
/// exists to bind.
/// </para>
/// <para>
/// <b>What this does not close, stated so the reader's prior does not fill it in.</b> A deny pattern
/// binds a spelling: <c>curl.exe</c>, <c>python3 -c</c>, <c>py -c</c>, <c>node --eval</c>, or an HTTP
/// call from inside a test the lane compiles and runs are all past it. An <c>implement</c> lane running
/// arbitrary test code can open a socket to the daemon port regardless; that exposure is the same one
/// it has through the CLI on its own machine, and spec/baton.md §9 records it as accepted rather than
/// closed. This test is a tripwire on the casual path, not a wall.
/// </para>
/// Reads the repo's own <c>src/Baton.Vendors/WorkerRoles.json</c> (via the catalog's path override,
/// pinned to the source file rather than the copy next to the assembly) so an edit to the source is
/// what this test measures.
/// </remarks>
public sealed class LaneRoleDaemonWriteVerbTests
{
    /// <summary>
    /// Commands that WRITE — through the CLI or over HTTP to the daemon port. A lane role admitting any
    /// of these under its shell grant fails the test. Each entry is one concrete command line, judged
    /// through the real matcher; the deny patterns that close them live in <c>WorkerRoles.json</c>.
    /// </summary>
    public static TheoryData<string> DaemonWriteCommands =>
    [
        "baton queue add fix-2114 --role implement --spec brief.md --issue 2114",
        "baton resolve room-1 --decision approve",
        "baton cancel room-1",
        "baton dispatch --role implement --spec brief.md",
        "baton redispatch room-1",
        "baton trust C:/repo",
        "baton memory add --text x",
        "curl -X POST http://127.0.0.1:7777/cancel",
        "wget --post-data x http://127.0.0.1:7777/cancel",
        "Invoke-WebRequest -Method Post http://127.0.0.1:7777/cancel",
        "Invoke-RestMethod -Method Post http://127.0.0.1:7777/cancel",
        "iwr -Method Post http://127.0.0.1:7777/cancel",
        "irm -Method Post http://127.0.0.1:7777/cancel",
        "python -c \"import urllib.request; urllib.request.urlopen('http://127.0.0.1:7777/cancel')\"",
        "python -m http.client 127.0.0.1 7777",
        "node -e \"require('http').request({port:7777,method:'POST'}).end()\"",
    ];

    /// <summary>
    /// Reads a lane legitimately runs. Each shell-granting role must still admit at least one of these,
    /// which is what makes the harness discriminate: a grant that admits nothing would pass the
    /// theory above for the wrong reason.
    /// </summary>
    private static readonly string[] ReadControls =
    [
        "baton status room-1",
        "git diff --stat",
        "dotnet build -warnaserror",
    ];

    [Theory]
    [MemberData(nameof(DaemonWriteCommands))]
    public void No_lane_role_shell_grant_admits_a_daemon_write_verb(string command)
    {
        using var env = ShippedCatalogFromSource();

        var admitting = WorkerRoleCatalog.All
            .Where(role => Admits(role.Grant, command))
            .Select(role => role.Id)
            .ToList();

        Assert.True(
            admitting.Count == 0,
            $"Role(s) [{string.Join(", ", admitting)}] admit `{command}` under their shell grant. "
            + "A lane is a loopback caller the daemon's write gate trusts as the operator (spec/baton.md "
            + "§9, #2114), so the role grant is the only thing containing it: add a deny pattern to the "
            + "role's denied_shell_command_patterns in src/Baton.Vendors/WorkerRoles.json rather than "
            + "widening this test.");
    }

    [Fact]
    public void Every_shell_granting_role_still_admits_a_read()
    {
        using var env = ShippedCatalogFromSource();

        var readless = WorkerRoleCatalog.All
            .Where(role => role.Grant.RunShellCommands)
            .Where(role => !ReadControls.Any(read => Admits(role.Grant, read)))
            .Select(role => role.Id)
            .ToList();

        Assert.True(
            readless.Count == 0,
            $"Role(s) [{string.Join(", ", readless)}] grant a shell but admit none of the read controls "
            + $"[{string.Join(", ", ReadControls)}]. The write-verb theory above would then pass for "
            + "them vacuously; the list is verbs that WRITE, and reads stay allowed (#2114).");
    }

    /// <summary>
    /// Whether <paramref name="grant"/> lets <paramref name="command"/> run, judged the way the hooks
    /// judge it: no shell at all admits nothing; otherwise the segmented allow/deny pass, then the
    /// position-independent option-token deny.
    /// </summary>
    private static bool Admits(PermissionGrant grant, string command)
    {
        if (!grant.RunShellCommands)
        {
            return false;
        }

        var result = ShellCommandPatternMatcher.EvaluateChainedCommand(
            command, grant.ShellCommandPatterns, grant.DeniedShellCommandPatterns);
        if (!result.IsAllowed)
        {
            return false;
        }

        return !ShellCommandPatternMatcher.IsDeniedByOptionToken(command, grant.DeniedShellOptionTokens);
    }

    private static IDisposable ShippedCatalogFromSource()
    {
        var vendors = Path.Combine(RepoRoot(), "src", "Baton.Vendors");
        return BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with
        {
            WorkerTiersPathOverride = Path.Combine(vendors, "WorkerTiers.json"),
            WorkerRolesPathOverride = Path.Combine(vendors, "WorkerRoles.json"),
        });
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
