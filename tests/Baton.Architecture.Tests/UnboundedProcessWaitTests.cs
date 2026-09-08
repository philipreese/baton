using System.Text.RegularExpressions;

namespace Baton.Architecture.Tests;

/// <summary>
/// #1804 — see <see cref="Baton.Tests.Shared.BoundedProcessWait"/> for the mechanism and rationale.
/// Every child-process exit wait in <c>Baton.Cli.Tests</c> must be bounded by an elapsed-time
/// timeout, not merely by external run cancellation.
/// <para>
/// Widened past the original argument-less <c>WaitForExit()</c>/<c>WaitForExitAsync()</c> check
/// (#1828 review finding): a call passing only <c>TestContext.Current.CancellationToken</c> reads
/// as bounded but is not — that token fires solely on external test-run cancellation (Ctrl-C,
/// harness teardown), never on a per-test elapsed bound, so a hung child under load hangs exactly
/// like the bare-argument-less case this check originally caught. Every genuinely bounded wait goes
/// through <see cref="Baton.Tests.Shared.BoundedProcessWait.RunToExitAsync"/> instead, or arms its
/// own local <see cref="CancellationTokenSource"/> with <c>CancelAfter</c> (no site currently does
/// this, so the allow-list below is empty).
/// </para>
/// <para>
/// Scoped to <c>Baton.Cli.Tests</c>, not every test file: that is the assembly #1804 measured
/// hanging and the one this PR's fix actually covers (claim-scope). <c>Baton.Tests</c> and
/// <c>Baton.Vendors.Tests</c> carry the same bare-<c>WaitForExit()</c> shape in several places but
/// were never observed to hang and are out of scope here — widening this check to them is future
/// work, not a claim this PR makes. One arm over <c>src/</c> exists since #2117, scoped the same
/// way to the one directory where the hazard was found: <c>src/Baton.Cli/Daemon/</c>
/// (<see cref="No_daemon_process_wait_is_unbounded_and_uncancellable"/>).
/// </para>
/// </summary>
public class UnboundedProcessWaitTests
{
    // Files allowed to contain a flagged pattern despite it, each entry justified inline. Currently
    // empty: LiveCancelRequestChannelEndToEndTests.cs's one raw WaitForExitAsync call passes a
    // CancellationTokenSource armed with its own local CancelAfter, not
    // TestContext.Current.CancellationToken directly, so it never matches either regex below and
    // needs no entry here.
    private static readonly IReadOnlyDictionary<string, string> AllowedOffenders =
        new Dictionary<string, string>();

    [Fact]
    public void All_Baton_Cli_Tests_process_waits_carry_an_elapsed_time_bound()
    {
        var cliTestsDir = Path.Combine(RepoRoot(), "tests", "Baton.Cli.Tests");
        var argumentLessRegex = new Regex(@"\bWaitForExit(Async)?\s*\(\s*\)", RegexOptions.Singleline);
        var tokenOnlyRegex = new Regex(
            @"\bWaitForExitAsync\s*\(\s*TestContext\.Current\.CancellationToken\s*\)", RegexOptions.Singleline);
        var offenders = new List<string>();

        foreach (var filePath in Directory.EnumerateFiles(cliTestsDir, "*.cs", SearchOption.AllDirectories))
        {
            var segments = filePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Any(s => s.Equals("bin", StringComparison.OrdinalIgnoreCase) || s.Equals("obj", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(cliTestsDir, filePath).Replace('\\', '/');
            if (AllowedOffenders.ContainsKey(relativePath))
            {
                continue;
            }

            var content = File.ReadAllText(filePath);
            if (argumentLessRegex.IsMatch(content) || tokenOnlyRegex.IsMatch(content))
            {
                offenders.Add(relativePath);
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"Found child-process exit wait(s) with no elapsed-time bound in Baton.Cli.Tests file(s): {string.Join(", ", offenders)}. " +
            "Every process wait in a Baton.Cli.Tests file must be bounded by elapsed time — route it through " +
            "Baton.Tests.Shared.BoundedProcessWait.RunToExitAsync, or arm a local CancellationTokenSource with " +
            "CancelAfter and add an entry to AllowedOffenders with a justification (#1804, #1828).");
    }

    /// <summary>
    /// The same hazard in the one place in <c>src/</c> where it was measured (#2117 review, finding 4):
    /// the daemon's queue supervisor waited on a lane with <c>WaitForExitAsync(CancellationToken.None)</c>,
    /// which for a process with redirected streams and async readers does not return until those
    /// streams reach EOF — so a straggler holding a duplicated pipe end wedged the supervisor forever,
    /// the room never got its fault sentinel, and the daemon leaked one task per such lane. Scoped to
    /// <c>src/Baton.Cli/Daemon/</c>, the long-lived host where a leaked wait accumulates, and not to
    /// all of <c>src/</c>: the argument-less <c>WaitForExit()</c> calls elsewhere (the process runner,
    /// the worktree provisioner, the junction linker) sit under their own bounds or Job Objects and were
    /// not measured here, so widening past the daemon would be a claim this test cannot back.
    /// A wait with no elapsed bound and no cancellation is what this flags; a wait on the OS exit
    /// signal that is unbounded in time by design (a lane runs as long as its own <c>--timeout</c>
    /// allows) goes through <c>Process.Exited</c> instead and matches nothing here.
    /// </summary>
    [Fact]
    public void No_daemon_process_wait_is_unbounded_and_uncancellable()
    {
        var daemonDir = Path.Combine(RepoRoot(), "src", "Baton.Cli", "Daemon");
        var argumentLessRegex = new Regex(@"\bWaitForExit(Async)?\s*\(\s*\)", RegexOptions.Singleline);
        var noneTokenRegex = new Regex(@"\bWaitForExitAsync\s*\(\s*CancellationToken\.None\s*\)", RegexOptions.Singleline);
        var offenders = new List<string>();

        foreach (var filePath in Directory.EnumerateFiles(daemonDir, "*.cs", SearchOption.AllDirectories))
        {
            var segments = filePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Any(s => s.Equals("bin", StringComparison.OrdinalIgnoreCase) || s.Equals("obj", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var content = File.ReadAllText(filePath);
            if (argumentLessRegex.IsMatch(content) || noneTokenRegex.IsMatch(content))
            {
                offenders.Add(Path.GetRelativePath(daemonDir, filePath).Replace('\\', '/'));
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"Found child-process exit wait(s) with no elapsed bound and no cancellation in src/Baton.Cli/Daemon file(s): {string.Join(", ", offenders)}. " +
            "WaitForExitAsync also waits for redirected streams to reach EOF, which a straggler can postpone forever; " +
            "wait on Process.Exited for the OS exit signal and bound the stream drain separately (QueueLauncher.SuperviseAsync's remarks).");

        // The control: the supervisor file this was measured on is still there and still waits on
        // something, so an empty offender list is a pass rather than a scan of nothing.
        var supervisor = File.ReadAllText(Path.Combine(daemonDir, "QueueLauncher.cs"));
        Assert.Contains("WaitForExitAsync(", supervisor, StringComparison.Ordinal);
        Assert.Contains("process.Exited +=", supervisor, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Baton.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            "Could not locate the repo root (Baton.slnx) by walking up from " + AppContext.BaseDirectory);
    }
}
