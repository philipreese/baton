using System.Text.RegularExpressions;
using Baton.Cli;
using Baton.Cli.Tests.TestSupport;
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
/// re-matches (#2128 review H3), accepting false denials even for valid wrapper syntax
/// (spec/baton.md §9, #2114). Reads stay admitted, each pinned individually:
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
/// below), <c>eval</c>, <c>iex</c>/<c>Invoke-Expression</c> (arbitrary code, deliberately
/// unfolded under #2114's accepted exposure; containment concerns the CLI path), or an HTTP call from
/// inside a test the lane compiles and runs are all past it. An
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
    public static TheoryData<string> DaemonWriteCommands
    {
        get
        {
            var commands = new TheoryData<string>();
            foreach (var command in CliVerbTable.DaemonWriteCommands.Concat(OtherDeniedCommands.Select(row => row.Data)))
            {
                commands.Add(command);
            }

            return commands;
        }
    }

    private static IEnumerable<CliVerbTable.ReadOnlyVerb> ReadOnlyVerbs => CliVerbTable.ReadOnlyVerbs;

    // The accepted arbitrary-code exposure, recorded alongside the fold population (spec/baton.md §9, #2114).
    private static readonly string[] UnfoldedWrapperHeads = ["eval", "iex", "Invoke-Expression"];

    private static TheoryData<string> OtherDeniedCommands =>
    [
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
        "pwsh -c \"git log --grep baton\"",
        "bash -c \"dotnet test --filter baton\"",
        "baton trust --list-extra",
        "baton trust --list C:/repo",
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

    [Fact]
    public void Every_cli_verb_is_classified_by_this_test()
    {
        var program = File.ReadAllText(Path.Combine(RepoRoot.Locate(), "src", "Baton.Cli", "Program.cs"));
        Assert.Contains("var knownSubcommands = CliVerbTable.KnownSubcommands;", program);
        Assert.Equal(CliVerbTable.All.Count, CliVerbTable.All.Select(verb => verb.Name).Distinct().Count());
        var dispatched = Regex.Matches(program, """args\[0\] == "([^"]+)"|case "([^"]+)":""")
            .Select(match => match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value)
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(dispatched.SetEquals(CliVerbTable.All.Select(verb => verb.Name)),
            "Every Program.cs dispatch arm must be classified in CliVerbTable, including hidden verbs.");
        foreach (var verb in CliVerbTable.All)
        {
            Assert.True(verb.Reads.Length + verb.Writes.Length > 0,
                $"Classify {verb.Name} in the CLI table: writes get probes; reads get handler declarations and controls. "
                + "The exception field is for deliberately excepted verbs (today reads; #2100 widens it).");
            Assert.All(verb.Reads, read => Assert.StartsWith($"baton {verb.Name}", read.Pattern));
            Assert.All(verb.Writes, write => Assert.StartsWith($"baton {verb.Name} ", write + " "));
        }
    }

    [Fact]
    public void Every_excepted_verb_is_declared_read_only_by_the_cli()
    {
        using var env = ShippedCatalogFromSource();
        var declared = ReadOnlyVerbs.Select(read => read.Pattern).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(declared);
        foreach (var role in UnscopedShellRoles())
        {
            Assert.NotNull(role.Grant.DeniedShellCommandExceptions);
            foreach (var exception in role.Grant.DeniedShellCommandExceptions)
            {
                Assert.Contains(exception, declared);
                Assert.Contains(UnscopedRoleReadControls, row =>
                    ShellCommandPatternMatcher.IsAllowed(row.Data, [exception]));
            }
        }

        var program = File.ReadAllText(Path.Combine(RepoRoot.Locate(), "src", "Baton.Cli", "Program.cs"));
        foreach (var read in ReadOnlyVerbs)
        {
            // Bind the declaration to a handler actually called by the CLI, not a test-owned noun list.
            Assert.Matches($@"\b{read.Handler.Name}\s*\.", program);
        }

        foreach (var read in UnscopedRoleReadControls.Select(row => row.Data).Where(command => command.StartsWith("baton ", StringComparison.Ordinal)))
        {
            Assert.True(ShellCommandPatternMatcher.IsAllowed(read, declared.ToArray()),
                $"Read control '{read}' has no CLI read-only handler declaration.");
        }
    }

    /// <summary>
    /// Exercises each declared read against the CLI suites' isolated home and real parked room.
    /// File membership and bytes are compared after EACH invocation, including the room journal.
    /// </summary>
    /// <remarks>
    /// Sabotage: make TemplatesCommand's "install" argument write under BatonPaths.Root while
    /// retaining the baton templates* declaration. The install invocation below then fails the
    /// snapshot comparison. This probes that concrete sub-verb growth, not all possible arguments.
    /// </remarks>
    [Fact]
    public async Task Every_excepted_read_leaves_the_store_and_room_ledger_byte_identical()
    {
        using var home = new IsolatedBatonHome();
        var room = Path.Combine(home.Path, "rooms", "read-probe");
        var fixture = await ParkedStepFixture.WriteParkedStepFixtureAsync(home.Path, room);
        ProjectCeilingStore.Set(home.Path, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);
        var ledger = BatonPaths.CostLedgerFile("read-probe");
        Directory.CreateDirectory(Path.GetDirectoryName(ledger)!);
        File.WriteAllText(ledger, "");
        var before = SnapshotFiles(home.Path);
        Assert.NotEmpty(before);
        Assert.NotEmpty(File.ReadAllBytes(fixture.LogPath));
        var token = TestContext.Current.CancellationToken;

        foreach (var read in ReadOnlyVerbs)
        {
            using var output = new StringWriter();
            switch (read.Pattern)
            {
                case "baton status*":
                    await StatusCommand.ExecuteAsync(
                        StatusOptionsParser.Parse([room, "--json"]), output, token);
                    break;
                case "baton templates*":
                    Assert.Equal(0, await TemplatesCommand.ExecuteAsync(["--json"], output, token));
                    AssertUnchanged();
                    Assert.Equal(0, await TemplatesCommand.ExecuteAsync(["install"], output, token));
                    break;
                case "baton trust --list":
                    Assert.Equal(0, await TrustCommand.ExecuteAsync(
                        TrustOptionsParser.Parse(["--list"]), output, token));
                    Assert.Contains(ProjectCeilingStore.CanonicalKey(home.Path), output.ToString());
                    break;
                case "baton ledger*":
                    Assert.Equal(0, await LedgerViewCommand.ExecuteAsync(
                        LedgerViewOptionsParser.Parse(["--repo-identity", "read-probe"]),
                        output, cancellationToken: token));
                    break;
                case "baton memory audit*":
                    Assert.Equal(0, await MemoryAuditCommand.ExecuteAsync(
                        MemoryAuditOptionsParser.Parse([]), output,
                        Path.Combine(home.Path, "claude"), token,
                        Path.Combine(home.Path, "user"), home.Path));
                    break;
                case "baton audit lanes*":
                    Assert.Equal(0, await AuditLanesCommand.ExecuteAsync(
                        new AuditLanesOptions(RoomsRoot: Path.GetDirectoryName(room)),
                        output, cancellationToken: token));
                    break;
                case "baton --version*":
                    output.WriteLine(VersionInfo.GetVersion(typeof(VersionInfo).Assembly));
                    break;
                default:
                    Assert.Fail($"Add a behavioral invocation for CLI read '{read.Pattern}'.");
                    break;
            }

            Assert.False(string.IsNullOrWhiteSpace(output.ToString()), read.Pattern);
            AssertUnchanged();

            void AssertUnchanged()
            {
                var after = SnapshotFiles(home.Path);
                Assert.True(before.Keys.Order().SequenceEqual(after.Keys.Order()),
                    $"{read.Pattern} changed the store's file membership.");
                foreach (var (path, bytes) in before)
                {
                    Assert.True(bytes.AsSpan().SequenceEqual(after[path]),
                        $"{read.Pattern} changed store or room ledger bytes at {path}.");
                }
            }
        }
    }

    private static Dictionary<string, byte[]> SnapshotFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(root, path), File.ReadAllBytes);

    [Fact]
    public void Trust_sub_verbs_are_coupled_to_the_write_probes()
    {
        var modes = new HashSet<TrustMode> { TrustOptionsParser.Parse(["--list"]).Mode };
        foreach (var command in CliVerbTable.DaemonWriteCommands.Where(command => command.StartsWith("baton trust ", StringComparison.Ordinal)))
        {
            var args = command.Split(' ')[2..];
            try
            {
                modes.Add(TrustOptionsParser.Parse(args).Mode);
            }
            catch (CliArgumentException)
            {
                // Mixed --list/write probes are intentionally invalid today, but must still deny.
            }
        }

        Assert.Equal(Enum.GetValues<TrustMode>().Order(), modes.Order());
        var parser = File.ReadAllText(Path.Combine(RepoRoot.Locate(), "src", "Baton.Cli", "TrustOptionsParser.cs"));
        var options = Regex.Matches(parser, "case \"(--[^\"]+)\":")
            .Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var covered = CliVerbTable.DaemonWriteCommands.Where(command => command.StartsWith("baton trust ", StringComparison.Ordinal))
            .SelectMany(command => command.Split(' ')).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(options);
        Assert.All(options, option => Assert.Contains(option, covered));
        Assert.Throws<CliArgumentException>(() => TrustOptionsParser.Parse(["--list", "--revoke", "C:/repo"]));
        Assert.Throws<CliArgumentException>(() => TrustOptionsParser.Parse(["--list", "--ceiling", "all", "C:/repo"]));
    }

    [Fact]
    public void Folded_and_recorded_unfolded_wrappers_partition_the_spec_spellings()
    {
        var root = RepoRoot.Locate();
        var matcher = File.ReadAllText(Path.Combine(root, "src", "Baton.Vendors", "ShellCommandPatternMatcher.cs"));
        var table = Regex.Match(matcher, @"ShellWrapperHeads\s*=\s*\[([^\]]+)\]");
        Assert.True(table.Success);
        var folded = Regex.Matches(table.Groups[1].Value, "\"([^\"]+)\"")
            .Select(match => match.Groups[1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.NotEmpty(folded);
        Assert.Empty(folded.Intersect(UnfoldedWrapperHeads, StringComparer.OrdinalIgnoreCase));
        var spec = File.ReadAllText(Path.Combine(root, "spec", "baton.md"));
        var population = Regex.Match(spec, @"The wrapper spellings classified by the test are ([\s\S]+?);");
        Assert.True(population.Success);
        var named = Regex.Matches(population.Groups[1].Value, "`([^`]+)`")
            .Select(match => match.Groups[1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.True(named.SetEquals(folded.Concat(UnfoldedWrapperHeads)));
        using var env = ShippedCatalogFromSource();
        foreach (var head in UnfoldedWrapperHeads)
        {
            Assert.All(UnscopedShellRoles(), role => Assert.True(Admits(role.Grant, $"{head} 'baton cancel room-1'")));
        }
    }

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
