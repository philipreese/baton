using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;

namespace Baton.Vendors;

/// <summary>
/// #1680: whether a `deny` decision actually reaches stdout, checked once at resolve time for any
/// agy grant that relies on <c>AgyWorkerAdapter</c>'s <c>PreToolUse</c> hook as its <b>sole</b>
/// narrowing (<see cref="AgyWorkerAdapter.RequiresHookAsSoleNarrowing"/>). On this vendor an absent
/// or unparseable hook response is read as an ALLOW (<c>agy.hook-malformed-stdout-fails-open</c>,
/// <c>tools/vendor-verify/verify.py</c>), so a hook that cannot start turns the most-restricted role
/// into an unscoped shell with network and unbounded writes rather than failing loudly — #710 is the
/// measured incident: a quoting bug meant the gate never fired on a Windows agy worker for a whole
/// release, and nothing checked per invocation that it had.
/// </summary>
/// <remarks>
/// Injectable so <see cref="AgyWorkerAdapter"/>'s unit tests drive it without spawning a real
/// process (CLAUDE.md's <c>right-instrument</c> gate, and this task's own "no live agy" constraint).
/// </remarks>
public interface IAgyHookLivenessProbe
{
    /// <summary>
    /// Runs the hook assembly at <paramref name="hookAssemblyPath"/> the same way agy would --
    /// <c>cmd /c</c>/<c>sh -c</c> over the identical command STRING <c>AgyWorkerAdapter.BuildHooksJson</c>
    /// writes into <c>hooks.json</c>, not a structural respawn of its parts (#1732 review F6) --
    /// against a synthetic payload that must be denied, and reports whether a <c>deny</c> decision
    /// came back on stdout within <paramref name="timeout"/>.
    /// </summary>
    AgyHookLivenessResult Probe(string hookAssemblyPath, TimeSpan timeout);
}

/// <param name="IsLive">True only when the hook answered with an explicit <c>deny</c> decision.</param>
/// <param name="Detail">
/// What actually happened — the hook path's non-existence, a timeout, malformed stdout, an
/// unexpected <c>decision</c> value, or the literal <c>deny</c> reason on success. Always populated,
/// so a refusal built from it names something concrete rather than "the probe failed".
/// </param>
public sealed record AgyHookLivenessResult(bool IsLive, string Detail);

/// <summary>
/// The production <see cref="IAgyHookLivenessProbe"/>. #1732 review F6: this used to spawn the hook
/// assembly directly via <c>dotnet</c> with an explicit <see cref="ProcessStartInfo.ArgumentList"/>,
/// sidestepping the shell hop entirely -- sound for a path-with-space, but structurally incapable of
/// reproducing #710, the measured incident (a command-STRING spelling defect: the binary was fine, the
/// shell could not resolve the command, and agy read the silence as an ALLOW) that is this probe's own
/// stated motivation. Now spawns <c>cmd /c</c> (Windows) / <c>sh -c</c> (Unix) over
/// <see cref="AgyWorkerAdapter.BuildHookCommand"/> -- the SAME function <see
/// cref="AgyWorkerAdapter.BuildHooksJson"/> calls to write <c>hooks.json</c> (#1732 review N1: no
/// longer two independent interpolations of the same string) -- so the same shell, parsing the same
/// string, is what answers. <see cref="AgyWorkerAdapter.HookAssemblyToken"/>'s escaping rules exist
/// to survive exactly this hop, and now the probe takes it too.
/// </summary>
internal sealed class ProcessAgyHookLivenessProbe : IAgyHookLivenessProbe
{
    /// <summary>
    /// #1732 review "Probe cost" (ruled ahead of #1731): a live result for a given
    /// <paramref name="hookAssemblyPath"/> cannot meaningfully change within one short-lived CLI
    /// invocation, so a second resolve in the same process (e.g. two agy roles under one <c>baton
    /// run</c> once #1731 widens the eager-resolve population) reuses the first spawn instead of
    /// paying for another cold <c>cmd /c dotnet …</c> start. A failed probe is deliberately NOT
    /// cached -- a transient failure (a slow machine, a momentary timeout) must not permanently wedge
    /// every later resolve in the same process into refusing dispatch.
    /// </summary>
    private static readonly ConcurrentDictionary<string, AgyHookLivenessResult> LiveResultCache = new();

    /// <summary>
    /// A <c>run_command</c> call that must be denied. Deliberately sent with <b>no</b>
    /// <c>BATON_HOOK_*</c> environment variables present (see <see cref="Probe"/>) -- #1732 review
    /// N7: with all four channels stripped, three independent guards in
    /// <c>AgyHookCheckCommand.Decide</c> each force the deny on their own -- the denied-tool list
    /// being absent, the shell-pattern list being absent, and the denied-shell-pattern list being
    /// absent -- so the deny is deliberately over-determined rather than resting on any single one.
    /// That is what lets this payload measure process liveness through the real shell (did the binary
    /// start and answer at all) rather than any one channel's own logic; the real-binary test this
    /// drives pins the deny happening at all, not the denied-tool guard specifically -- removing or
    /// reordering that one guard alone leaves the other two still denying. The <c>git push</c> command
    /// line is illustrative payload shape, not what drives the verdict.
    /// </summary>
    private const string SyntheticDeniedToolCall =
        """{"toolCall":{"name":"run_command","args":{"CommandLine":"git push"}}}""";

    private static readonly string[] EnvironmentVariablesToStrip =
    [
        AgyWorkerAdapter.DeniedToolsVariable,
        AgyWorkerAdapter.ShellPatternsVariable,
        AgyWorkerAdapter.DeniedShellPatternsVariable,
        AgyWorkerAdapter.DeniedShellOptionTokensVariable,
        AgyWorkerAdapter.DeniedShellExceptionsVariable,
        // #1732 review F7: a dogfooding outer lane's own hook may have BATON_HOOK_VERDICT_LEDGER set
        // in this process's environment (this probe's subprocess inherits it by default) -- without
        // this the probe's own guaranteed-deny verdict would append a line to the OUTER lane's ledger,
        // inflating that lane's hookVerdictCount with a verdict its own hook never produced. A
        // liveness probe's verdict is not a worker's verdict and does not belong in a worker's ledger.
        AgyWorkerAdapter.VerdictLedgerVariable,
    ];

    /// <summary>
    /// Test-only counter of <see cref="ProbeUncached"/> invocations -- incremented before the
    /// existence check, so it also counts a probe that never actually spawns a process (a
    /// nonexistent path). What it lets a test assert is the thing the cache is FOR: a second
    /// <see cref="Probe"/> call for an already-live path does not re-enter this method at all.
    /// </summary>
    internal static int SpawnCountForTesting;

    /// <summary>
    /// Test-only: <see cref="LiveResultCache"/> is process-wide and persists across every test in
    /// the same run, so a test asserting "the first call spawns, the second doesn't" needs a known-
    /// empty starting point rather than whatever an earlier, unrelated test already warmed for the
    /// same real assembly path.
    /// </summary>
    internal static void ResetCacheForTesting()
    {
        LiveResultCache.Clear();
        SpawnCountForTesting = 0;
    }

    public AgyHookLivenessResult Probe(string hookAssemblyPath, TimeSpan timeout)
    {
        if (LiveResultCache.TryGetValue(hookAssemblyPath, out var cached))
        {
            return cached;
        }

        var result = ProbeUncached(hookAssemblyPath, timeout);
        if (result.IsLive)
        {
            LiveResultCache[hookAssemblyPath] = result;
        }

        return result;
    }

    private static AgyHookLivenessResult ProbeUncached(string hookAssemblyPath, TimeSpan timeout)
    {
        Interlocked.Increment(ref SpawnCountForTesting);
        if (!File.Exists(hookAssemblyPath))
        {
            return new AgyHookLivenessResult(false, $"'{hookAssemblyPath}' does not exist");
        }

        string command;
        try
        {
            // #1732 review N1: shares AgyWorkerAdapter.BuildHookCommand with BuildHooksJson rather
            // than re-interpolating -- the whole point of F6 was spawning the identical string, and a
            // second independent interpolation is a place that identity could silently drift.
            command = AgyWorkerAdapter.BuildHookCommand(hookAssemblyPath);
        }
        catch (InvalidOperationException ex)
        {
            // HookAssemblyToken's own refusal (no clean token reachable, e.g. a spaced path with no
            // directory to 8.3-shorten) -- surfacing it as a dead probe rather than letting the
            // exception escape keeps this method's contract ("always returns a result") intact, and a
            // refused token IS a dead gate: agy would refuse to write a usable command either.
            return new AgyHookLivenessResult(false, $"the hook command could not be built: {ex.Message}");
        }

        try
        {
            var isWindows = OperatingSystem.IsWindows();
            var startInfo = ChildProcessStartInfo.Create(isWindows ? "cmd" : "sh", startInfo =>
            {
                startInfo.RedirectStandardInput = true;
                startInfo.RedirectStandardOutput = true;
                startInfo.RedirectStandardError = true;
                startInfo.StandardOutputEncoding = System.Text.Encoding.UTF8;
                startInfo.StandardErrorEncoding = System.Text.Encoding.UTF8;
            });
            startInfo.ArgumentList.Add(isWindows ? "/c" : "-c");
            startInfo.ArgumentList.Add(command);
            foreach (var name in EnvironmentVariablesToStrip)
            {
                startInfo.Environment.Remove(name);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new AgyHookLivenessResult(false, "the hook process did not start");
            }

            process.StandardInput.Write(SyntheticDeniedToolCall);
            process.StandardInput.Close();

            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Already exited between the WaitForExit timeout and this Kill — the timeout
                    // verdict below still stands; whether it exited a moment late does not un-time-out
                    // the probe.
                }

                return new AgyHookLivenessResult(
                    false, $"the hook did not respond within {timeout.TotalSeconds:0.#}s (timed out)");
            }

            var stdout = process.StandardOutput.ReadToEnd();
            return Evaluate(stdout);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new AgyHookLivenessResult(false, $"the hook process could not be run: {ex.Message}");
        }
    }

    /// <summary>
    /// Parses the hook's stdout the same way agy itself would (<c>agy.hook-malformed-stdout-fails-open</c>):
    /// a syntactically valid <c>{"decision":"deny",...}</c> object is the only accepted shape. Pulled out
    /// as its own method so a unit test can drive every stdout shape without spawning a process.
    /// </summary>
    internal static AgyHookLivenessResult Evaluate(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return new AgyHookLivenessResult(false, "the hook produced no stdout");
        }

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("decision", out var decisionProp)
                && decisionProp.ValueKind == JsonValueKind.String)
            {
                var decision = decisionProp.GetString();
                return decision == "deny"
                    ? new AgyHookLivenessResult(true, "deny")
                    : new AgyHookLivenessResult(false, $"the hook returned decision '{decision}' instead of 'deny'");
            }

            return new AgyHookLivenessResult(false, $"the hook's stdout carried no 'decision' field: {stdout.Trim()}");
        }
        catch (JsonException)
        {
            return new AgyHookLivenessResult(false, $"the hook's stdout was not valid JSON: {stdout.Trim()}");
        }
    }
}
