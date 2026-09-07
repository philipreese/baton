using System.Text.RegularExpressions;

namespace Baton.Architecture.Tests;

/// <summary>
/// #2030 review (MEDIUM): every spawn site in <c>src/</c> that goes through
/// <c>ChildProcessStartInfo.Create</c> must redirect BOTH stdout and stderr, or be a named exception
/// here. This became a real invariant of the codebase the moment <c>StandardHandleInheritance</c>
/// started clearing <c>HANDLE_FLAG_INHERIT</c> on Baton's own standard handles, and nothing pinned it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why an unredirected stream is now a silent data loss.</b> .NET's
/// <c>Process.StartWithCreateProcess</c> sets <c>STARTF_USESTDHANDLES</c> whenever any stream is
/// redirected, and fills the streams the caller did NOT redirect with this process's own
/// <c>GetStdHandle</c> values. Those handles are non-inheritable from <c>Program.cs</c>'s clear
/// onward, so a child handed one cannot inherit it and its writes on that stream go nowhere — no
/// exception, no exit code, just a missing line in a lane log. Before #2030 the same spawn silently
/// worked, which is exactly why this needs a check rather than a comment.
/// </para>
/// <para>
/// <b>The invariant is held together by two files that do not reference each other</b> — the verb
/// allowlist in <c>Program.cs</c> (<c>IsLaneVerb</c>) decides when the clear happens, and every
/// <c>Create</c> site decides whether it survives it. This test is the connection between them, and
/// it deliberately does not try to work out which sites a lane verb can reach: that is a runtime
/// property no file scan can honestly assert, and the reachable set is one refactor away from
/// changing. It asserts the stronger, checkable thing — no site anywhere leaves an output stream
/// un-redirected — so a new spawn cannot lose its output whatever path reaches it.
/// </para>
/// <para>
/// <b>Its false negatives, named rather than left to be discovered</b>, matching
/// <see cref="RedirectedProcessEncodingTests"/>, whose counting shape this reuses. A redirect
/// assigned a non-literal (<c>= someFlag</c>) is not counted at all; counts are per file, so two
/// spawn sites in one file could balance a missing redirect against a redundant one; and a match
/// inside a string or comment inflates only the REQUIRED count, which fails noisily rather than
/// passing quietly. Every current site assigns literal <c>true</c>, one site per file.
/// </para>
/// </remarks>
public class SpawnOutputRedirectionTests
{
    /// <summary>
    /// Spawn sites permitted to leave an output stream un-redirected, with why that is not a lost
    /// output. Same deliberate-act shape as <c>VendorSpawnGateTests.ApprovedSpawnSites</c>: adding a
    /// line here is a claim that this child's stdout/stderr genuinely does not need to reach Baton,
    /// AND that no lane verb can reach it — because after #2030 it will not reach anything else
    /// either.
    /// </summary>
    private static readonly Dictionary<string, string> UnredirectedOutputSites = new()
    {
        ["src/Baton.Cli/WatchNotifier.cs"] =
            "#1488: redirects stdin ONLY, on purpose -- the operator's --notify command writes straight to "
            + "the console by inheritance, and redirecting its output would leave an undrained pipe that "
            + "blocks the command at 4 KB. Safe under #2030 because it is unreachable from a lane verb: its "
            + "only callers are WatchCommand (verb `watch`) and Daemon/WatchSweep (verb `daemon`), and "
            + "neither is in Program.cs's IsLaneVerb set, so the inherit flag is still set when it spawns.",
    };

    private const string SeamCall = "ChildProcessStartInfo.Create";

    private static readonly Regex CreateCall = new(@"ChildProcessStartInfo\.Create\s*\(");
    private static readonly Regex RedirectOut = new(@"RedirectStandardOutput\s*=\s*true");
    private static readonly Regex RedirectErr = new(@"RedirectStandardError\s*=\s*true");

    [Fact]
    public void Every_spawn_site_in_src_redirects_both_stdout_and_stderr()
    {
        var root = RepoRoot();
        var offenders = new List<string>();

        foreach (var (relativePath, content) in SourceFiles(root))
        {
            var creates = CreateCall.Matches(content).Count;
            if (creates == 0 || UnredirectedOutputSites.ContainsKey(relativePath))
            {
                continue;
            }

            if (RedirectOut.Matches(content).Count < creates)
            {
                offenders.Add($"{relativePath} (stdout: {creates} spawn site(s))");
            }

            if (RedirectErr.Matches(content).Count < creates)
            {
                offenders.Add($"{relativePath} (stderr: {creates} spawn site(s))");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "A spawn site leaves an output stream un-redirected:\n  " + string.Join("\n  ", offenders)
            + "\n\nSince #2030, Program.cs clears HANDLE_FLAG_INHERIT on Baton's own stdout and stderr before "
            + "a lane's first spawn, so an un-redirected stream hands the child a handle it cannot inherit and "
            + "its writes are silently discarded. Set RedirectStandardOutput and RedirectStandardError at the "
            + "spawn site, or add the file to UnredirectedOutputSites with the reason no lane verb can reach it.");
    }

    // The other direction, so the exception list cannot rot: an entry that no longer spawns anything,
    // or that has since started redirecting both streams, is dead weight that reads as a live carve-out.
    [Fact]
    public void The_unredirected_exception_list_still_describes_real_spawn_sites()
    {
        var root = RepoRoot();
        var stale = new List<string>();

        foreach (var (relativePath, reason) in UnredirectedOutputSites)
        {
            var fullPath = Path.Combine(root, relativePath);
            if (!File.Exists(fullPath))
            {
                stale.Add($"{relativePath} -- file no longer exists");
                continue;
            }

            var content = File.ReadAllText(fullPath);
            var creates = CreateCall.Matches(content).Count;
            if (creates == 0)
            {
                stale.Add($"{relativePath} -- no longer calls {SeamCall}");
                continue;
            }

            if (RedirectOut.Matches(content).Count >= creates && RedirectErr.Matches(content).Count >= creates)
            {
                stale.Add(
                    $"{relativePath} -- now redirects both output streams at every spawn site, so the "
                    + $"exception is no longer needed (recorded reason: {reason})");
            }
        }

        Assert.True(
            stale.Count == 0,
            "UnredirectedOutputSites names entries that no longer describe an un-redirected spawn site:\n  "
            + string.Join("\n  ", stale)
            + "\n\nRemove the entry: a carve-out nobody uses still reads as permission to leave output "
            + "un-redirected there.");
    }

    // Guards against a vacuous pass of both tests above: if the seam is renamed and the regex stops
    // matching, every file scans as "0 spawn sites" and nothing fails.
    [Fact]
    public void The_scan_still_finds_the_spawn_sites_it_is_scanning_for()
    {
        var matched = SourceFiles(RepoRoot()).Count(entry => CreateCall.IsMatch(entry.Content));
        Assert.True(
            matched >= 10,
            $"Only {matched} file(s) under src/ match '{SeamCall}'. Either the shared start-info seam was "
            + "renamed (re-point CreateCall) or the spawn sites moved -- as written, this test suite would "
            + "now pass without checking anything.");
    }

    private static IEnumerable<(string RelativePath, string Content)> SourceFiles(string root)
    {
        var srcDir = Path.Combine(root, "src");
        foreach (var filePath in Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories))
        {
            var segments = filePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Any(s =>
                    s.Equals("bin", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("obj", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            yield return (
                Path.GetRelativePath(root, filePath).Replace('\\', '/'),
                File.ReadAllText(filePath));
        }
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
