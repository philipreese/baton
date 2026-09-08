using System.Text.RegularExpressions;
using Baton.Status;
using Baton.Vendors;

namespace Baton.Architecture.Tests;

/// <summary>
/// #2114 (contract: <c>spec/baton.md</c> §9's #2114 paragraph, which is the ruling's record; §11 C-11
/// points there): <b>no lane role's shell grant reaches the daemon's write verbs.</b> The invariant
/// this serves (ruled 2026-09-08, console decision round, recorded once at that paragraph): the page's
/// write gate authenticates REMOTE callers by their network identity and trusts anything arriving over
/// loopback as the operator; a worker process runs on that same machine, so from the gate's side a
/// lane and the operator are indistinguishable. Only the lane's ROLE GRANT separates them, which is
/// why the containment is enforced HERE, on <c>WorkerRoles.json</c>, before any dispatch reads it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The grant denies the entire <c>baton</c> head and names the reads that stay open</b>
/// (<c>denied_shell_command_patterns: "baton *"</c> plus <c>denied_shell_command_exceptions</c>), rather
/// than listing write verbs: tomorrow's verb needs no edit anywhere to be refused, and a read is open
/// only because someone wrote it down. The probes below still enumerate every write verb — that is what makes
/// a wrong deny spelling, a missing re-deny beneath an exception (<c>baton ledger --rebuild</c> under
/// <c>baton ledger*</c>), or a too-wide exception go red rather than pass on the shape — and
/// <see cref="Every_cli_verb_is_classified_by_this_test"/> reads the CLI's own verb table so a new verb
/// that no probe covers fails here too. Three families: the CLI verbs; the HTTP clients a lane would
/// reach the daemon port with from its shell (<c>curl</c>, <c>wget</c>, PowerShell's
/// <c>Invoke-WebRequest</c>/<c>Invoke-RestMethod</c> and their <c>iwr</c>/<c>irm</c> aliases,
/// <c>python -c</c>/<c>python -m http</c>, <c>node -e</c>); and the SHELL WRAPPERS that carry either
/// (the heads <c>ShellCommandPatternMatcher.ShellWrapperHeads</c> names), whose bodies the matcher
/// re-matches (#2128 review H3). Reads stay admitted, each pinned individually:
/// <see cref="UnscopedRoleReadControls"/> below.
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
/// binds a spelling: <c>baton.exe</c>, <c>curl.exe</c>, <c>python3 -c</c>, <c>py -c</c>,
/// <c>node --eval</c>, a script file run through a wrapper (<c>pwsh -File x.ps1</c>, pinned admitted
/// below), or an HTTP call from inside a test the lane compiles and runs are all past it. An
/// <c>implement</c> lane running arbitrary test code can open a socket to the daemon port regardless;
/// that exposure is the same one it has through the CLI on its own machine, and spec/baton.md §9
/// records it as accepted rather than closed. This test is a tripwire on the casual path, not a wall.
/// Case is not on that list any more: deny heads compare case-insensitively (<c>IWR</c>, <c>CURL</c>),
/// and exception heads ordinally, for the reason on <c>ShellCommandPatternMatcher.IsDeniedByTokenizedHead</c>
/// — <c>baton Status</c> is the CLI's default arm, i.e. <c>supply</c>, and is probed as a write below.
/// </para>
/// Reads the repo's own <c>src/Baton.Vendors/WorkerRoles.json</c> (via the catalog's path override,
/// pinned to the source file rather than the copy next to the assembly) so an edit to the source is
/// what this test measures.
/// </remarks>
public sealed class LaneRoleDaemonWriteVerbTests
{
    /// <summary>
    /// Commands that WRITE — through the CLI, over HTTP to the daemon port, or either one inside a shell
    /// wrapper. A lane role admitting any of these under its shell grant fails the test. Each entry is
    /// one concrete command line, judged through the real matcher; the deny that closes them lives in
    /// <c>WorkerRoles.json</c>. Every mutating verb in <c>Program.cs</c>'s table appears at least once,
    /// so a verb-list regression is a red row, not a missing one.
    /// </summary>
    public static TheoryData<string> DaemonWriteCommands =>
    [
        // The CLI's mutating verbs, one probe per verb (or per mutating sub-verb of a noun group).
        "baton run C:/rooms/other-room --workflow wf.json",
        "baton dispatch --role implement --spec brief.md",
        "baton redispatch room-1",
        "baton cancel room-1",
        "baton decide C:/rooms/other-room --execution e1 --type resume --bindings b.json",
        "baton resolve room-1 --accept-capture",
        "baton supply room-1 --worker implement --output changes.md --file x.md --bindings b.json",
        "baton resume room-1",
        "baton watch room-1 --command notify.exe",
        "baton deliver --title x --file y.md",
        "baton keep room-1",
        "baton unkeep room-1",
        "baton trust C:/repo",
        "baton trust C:/repo --revoke",
        "baton room delete room-1",
        "baton rooms prune",
        "baton ledger --rebuild",
        "baton ledger backfill",
        "baton ledger export C:/repo",
        "baton memory import",
        "baton memory sync --apply",
        "baton memory add --text x",
        "baton queue add fix-2114 --role implement --spec brief.md --issue 2114",
        "baton queue hold",
        "baton mcp --memory-proposal-tool",
        "baton daemon",
        // Vendor subprocess endpoints, not operator verbs, but reachable from a shell all the same.
        "baton hook-check",
        "baton agy-hook-check",
        "baton codex-broker",
        // Program.cs dispatches ordinally: `Status` is not `status`, it is the default arm — supply.
        "baton Status room-1 --worker implement --output x.md --file y.md --bindings b.json",
        // Case on the deny side: the shell resolves these to the same program.
        "Baton cancel room-1",
        "BATON DECIDE C:/rooms/other-room --execution e1 --type resume --bindings b.json",
        // HTTP clients a lane would reach the daemon port with, lowercase and as the shell also takes them.
        "curl -X POST http://127.0.0.1:7777/cancel",
        "CURL -X POST http://127.0.0.1:7777/cancel",
        "wget --post-data x http://127.0.0.1:7777/cancel",
        "Invoke-WebRequest -Method Post http://127.0.0.1:7777/cancel",
        "Invoke-Webrequest -Method Post http://127.0.0.1:7777/cancel",
        "Invoke-RestMethod -Method Post http://127.0.0.1:7777/cancel",
        "iwr -Method Post http://127.0.0.1:7777/cancel",
        "IWR -Method Post http://127.0.0.1:7777/cancel",
        "irm -Method Post http://127.0.0.1:7777/cancel",
        "python -c \"import urllib.request; urllib.request.urlopen('http://127.0.0.1:7777/cancel')\"",
        "python -m http.client 127.0.0.1 7777",
        "node -e \"require('http').request({port:7777,method:'POST'}).end()\"",
        // Shell wrappers (#2128 review H3): a well-formed wrapper segments cleanly, so before the fold
        // every probe above walked past on head `pwsh`/`cmd`/`bash`.
        "pwsh -c \"baton cancel room-1\"",
        "pwsh -NoProfile -Command \"baton decide C:/rooms/other-room --execution e1 --type resume --bindings b.json\"",
        "powershell -Command baton dispatch --role implement --spec brief.md",
        "powershell.exe -c 'baton queue hold'",
        "cmd /c baton resolve room-1 --accept-capture",
        "cmd.exe /C \"baton room delete room-1\"",
        "bash -c \"baton redispatch room-1\"",
        "bash -lc 'curl -X POST http://127.0.0.1:7777/cancel'",
        "sh -c 'irm -Method Post http://127.0.0.1:7777/cancel'",
        "pwsh -c \"cmd /c baton cancel room-1\"",
        // A read in front does not launder the write behind it inside one wrapper body.
        "pwsh -c \"baton status room-1; baton cancel room-1\"",
        // A wrapper whose body the matcher cannot read is denied outright, and says so.
        "pwsh -EncodedCommand YmF0b24gY2FuY2VsIHJvb20tMQ==",
        "pwsh -e YmF0b24gY2FuY2VsIHJvb20tMQ==",
        "powershell -enc YmF0b24gY2FuY2VsIHJvb20tMQ==",
        "pwsh",
        "cmd",
    ];

    /// <summary>
    /// Reads an UNSCOPED shell role (<c>implement</c>, <c>janitor</c>) legitimately runs — every
    /// <c>baton</c> read verb in <c>Program.cs</c>'s table, plus the ordinary build reads. Each is
    /// pinned individually per role (#2128 review L5), so a blunt <c>baton *</c> deny with no read
    /// allowlist fails here on <c>baton status</c> specifically rather than passing on <c>git diff</c>.
    /// The two wrapper rows pin the fold's own negative: a wrapper carrying a read, and a script file
    /// the matcher cannot see into, both stay admitted (the second is the accepted exposure).
    /// </summary>
    public static TheoryData<string> UnscopedRoleReadControls =>
    [
        "baton status room-1",
        "baton status C:/rooms/other-room --json",
        "baton templates --json",
        "baton audit lanes --since 1d",
        "baton memory audit",
        "baton ledger",
        "baton ledger --since 2026-09-01 --vendor claude",
        "baton trust --list",
        "baton --version",
        "pwsh -c \"baton status room-1\"",
        "pwsh -File build.ps1",
        "bash tools/check.sh",
        "git diff --stat",
        "dotnet build -warnaserror",
    ];

    /// <summary>
    /// The read every shell-granting role, scoped or not, must admit — the discriminating control for
    /// the scoped roles (<c>review</c>, <c>consolidate</c>), whose allowlists never carried
    /// <c>baton</c> at all.
    /// </summary>
    private const string EveryShellRoleReadControl = "git diff --stat";

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
            + "§9, #2114), so the role grant is the only thing containing it: the role's "
            + "denied_shell_command_patterns in src/Baton.Vendors/WorkerRoles.json must deny the `baton` "
            + "head (with reads carved out in denied_shell_command_exceptions) and the HTTP clients, "
            + "rather than this test widening.");
    }

    [Theory]
    [MemberData(nameof(UnscopedRoleReadControls))]
    public void Every_unscoped_shell_role_still_admits_each_read(string read)
    {
        using var env = ShippedCatalogFromSource();

        var readless = UnscopedShellRoles()
            .Where(role => !Admits(role.Grant, read))
            .Select(role => role.Id)
            .ToList();

        Assert.True(
            readless.Count == 0,
            $"Role(s) [{string.Join(", ", readless)}] grant an unscoped shell but refuse `{read}`. The "
            + "deny is on the whole `baton` head, so each read a lane needs is named in "
            + "denied_shell_command_exceptions (#2114); a read missing there is a lane that cannot see "
            + "its own room.");
    }

    [Fact]
    public void Every_shell_granting_role_still_admits_a_git_read()
    {
        using var env = ShippedCatalogFromSource();

        var readless = WorkerRoleCatalog.All
            .Where(role => role.Grant.RunShellCommands)
            .Where(role => !Admits(role.Grant, EveryShellRoleReadControl))
            .Select(role => role.Id)
            .ToList();

        Assert.True(
            readless.Count == 0,
            $"Role(s) [{string.Join(", ", readless)}] grant a shell but refuse `{EveryShellRoleReadControl}`. "
            + "The write-verb theory above would then pass for them vacuously; the list is verbs that "
            + "WRITE, and reads stay allowed (#2114).");
    }

    /// <summary>
    /// The control arm for the read theory (#2128 review L5): the blunt fix — <c>baton *</c> with no
    /// read allowlist — refuses <c>baton status</c> through the same matcher, so the read control
    /// above is what stands between that shape and a green run. And the pre-#2128 shape — the seven
    /// named verbs — admits <c>baton decide</c>, which is the H2 finding reproduced against the real
    /// matcher rather than remembered.
    /// </summary>
    [Fact]
    public void The_read_controls_discriminate_a_blunt_deny_and_the_probes_discriminate_the_old_verb_list()
    {
        var blunt = new PermissionGrant(RunShellCommands: true, DeniedShellCommandPatterns: ["baton *"]);
        Assert.False(Admits(blunt, "baton status room-1"));

        var sevenVerbs = new PermissionGrant(
            RunShellCommands: true,
            DeniedShellCommandPatterns:
            [
                "baton queue*", "baton resolve*", "baton cancel*", "baton dispatch*", "baton redispatch*",
                "baton trust*", "baton memory add*",
            ]);
        Assert.True(Admits(sevenVerbs, "baton decide C:/rooms/other-room --execution e1 --type resume --bindings b.json"));
        Assert.True(Admits(sevenVerbs, "baton room delete room-1"));
    }

    /// <summary>
    /// Reads <c>Program.cs</c>'s own verb table (<c>knownSubcommands</c>) and requires every verb in it
    /// to be the second token of at least one write probe or one read control above. A verb added to
    /// the CLI without a row here fails this rather than silently riding whichever side the deny
    /// happens to put it on.
    /// </summary>
    [Fact]
    public void Every_cli_verb_is_classified_by_this_test()
    {
        var program = File.ReadAllText(Path.Combine(RepoRoot.Locate(), "src", "Baton.Cli", "Program.cs"));
        var table = Regex.Match(program, @"knownSubcommands\s*=\s*new\[\]\s*\{([^}]*)\}");
        Assert.True(table.Success, "Program.cs no longer declares `knownSubcommands` as an inline array.");

        var verbs = Regex.Matches(table.Groups[1].Value, "\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value)
            .ToList();
        Assert.NotEmpty(verbs);

        var classified = BatonVerbsIn(DaemonWriteCommands)
            .Concat(BatonVerbsIn(UnscopedRoleReadControls))
            .ToHashSet(StringComparer.Ordinal);

        var unclassified = verbs.Where(verb => !classified.Contains(verb)).ToList();
        Assert.True(
            unclassified.Count == 0,
            $"CLI verb(s) [{string.Join(", ", unclassified)}] have no probe in {nameof(DaemonWriteCommands)} "
            + $"and no control in {nameof(UnscopedRoleReadControls)}. Classify each: a write gets a probe "
            + "(the `baton *` deny already closes it), a read gets a control AND an entry in both roles' "
            + "denied_shell_command_exceptions.");
    }

    private static IEnumerable<string> BatonVerbsIn(TheoryData<string> commands) =>
        commands
            .Select(row => row.Data)
            .Select(command => command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Where(tokens => tokens.Length > 1 && tokens[0] == "baton")
            .Select(tokens => tokens[1]);

    private static IEnumerable<WorkerRole> UnscopedShellRoles() =>
        WorkerRoleCatalog.All
            .Where(role => role.Grant.RunShellCommands)
            .Where(role => role.Grant.ShellCommandPatterns is not { Count: > 0 });

    /// <summary>
    /// Whether <paramref name="grant"/> lets <paramref name="command"/> run, judged the way the hooks
    /// judge it: no shell at all admits nothing; otherwise the segmented allow/deny pass (with the
    /// grant's read allowlist), then the position-independent option-token deny.
    /// </summary>
    private static bool Admits(PermissionGrant grant, string command)
    {
        if (!grant.RunShellCommands)
        {
            return false;
        }

        var result = ShellCommandPatternMatcher.EvaluateChainedCommand(
            command, grant.ShellCommandPatterns, grant.DeniedShellCommandPatterns,
            grant.DeniedShellCommandExceptions);
        if (!result.IsAllowed)
        {
            return false;
        }

        return !ShellCommandPatternMatcher.IsDeniedByOptionToken(command, grant.DeniedShellOptionTokens);
    }

    private static IDisposable ShippedCatalogFromSource()
    {
        var vendors = Path.Combine(RepoRoot.Locate(), "src", "Baton.Vendors");
        return BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with
        {
            WorkerTiersPathOverride = Path.Combine(vendors, "WorkerTiers.json"),
            WorkerRolesPathOverride = Path.Combine(vendors, "WorkerRoles.json"),
        });
    }
}
