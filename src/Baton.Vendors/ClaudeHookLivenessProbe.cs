using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace Baton.Vendors;

/// <summary>
/// #532: the claude-side counterpart to <see cref="IAgyHookLivenessProbe"/>. #649 makes the
/// mandatory <c>PreToolUse</c> hook the SOLE location bound on every write-family tool claude
/// spawns -- <c>Edit</c>/<c>Write</c>/<c>NotebookEdit</c> are always pre-approved on
/// <c>--allowedTools</c> and never named on <c>--disallowedTools</c>
/// (<see cref="ClaudeWorkerAdapter.BuildDisallowedTools"/>), so nothing but the hook stops a write
/// from landing outside <c>BATON_OUTPUT_DIR</c>/the workspace. A hook file that exists
/// (<see cref="ClaudeWorkerAdapter"/>'s own <c>File.Exists</c> guard) but cannot actually execute --
/// truncated, wrong runtime, edited between vendor-verify's build-time check and this spawn -- fails
/// open exactly the way <c>gate.broken-hook-fails-open</c> measures, and existence alone cannot tell
/// the two apart.
/// </summary>
/// <remarks>
/// Injectable so <see cref="ClaudeWorkerAdapter"/>'s unit tests drive it without spawning a real
/// <c>dotnet</c> process, as required by the <c>right-instrument</c> gate
/// (<see href="../../../docs/agents/developing-baton.md"/>).
/// </remarks>
public interface IClaudeHookLivenessProbe
{
    /// <summary>
    /// Runs the hook assembly at <paramref name="hookAssemblyPath"/> the same way claude would --
    /// <c>dotnet &lt;dll&gt; hook-check</c>, exec form, no shell hop (<see cref="ClaudeWorkerAdapter"/>'s
    /// settings.json ships it that way) -- against a synthetic <c>PreToolUse</c> payload for a
    /// withheld <c>Write</c> call, and reports whether a denying exit code came back within
    /// <paramref name="timeout"/>.
    /// </summary>
    ClaudeHookLivenessResult Probe(string hookAssemblyPath, TimeSpan timeout);
}

/// <param name="IsLive">True only when the hook process exited with the deny code (2).</param>
/// <param name="Detail">
/// What actually happened -- the hook path's non-existence, a timeout, an unexpected exit code, or
/// stderr's own denial reason on success. Always populated, so a refusal built from it names
/// something concrete rather than "the probe failed".
/// </param>
public sealed record ClaudeHookLivenessResult(bool IsLive, string Detail);

/// <summary>The production <see cref="IClaudeHookLivenessProbe"/>.</summary>
internal sealed class ProcessClaudeHookLivenessProbe : IClaudeHookLivenessProbe
{
    /// <summary>
    /// A synthetic <c>PreToolUse</c> payload for a <c>Write</c> call, shaped exactly as
    /// <c>HookCheckCommand.Decide</c> reads it (<c>tool_name</c> + a <c>file_path</c> under
    /// <c>tool_input</c>). The target path is deliberately outside any real outbox/workspace, and the
    /// probe strips <c>BATON_OUTPUT_DIR</c>/<c>BATON_WORKSPACE_DIR</c> below so nothing this process
    /// inherited can make it read as an exempt write.
    /// </summary>
    private const string SyntheticWriteToolCall =
        """{"tool_name":"Write","tool_input":{"file_path":"aer-hook-liveness-probe.txt"}}""";

    /// <summary>
    /// #543/#649: claude tags a withheld write with this vendor's own tag -- same literal as
    /// <c>ClaudeWorkerAdapter.DeniedToolsVariable</c>/<c>DeniedToolsVendorTag</c> (record-once).
    /// </summary>
    private const string DeniedToolsValue = "claude:Write";

    /// <summary>
    /// <c>HookCheckCommand.DeniedExitCode</c>'s literal, mirrored here because
    /// <c>Baton.Vendors</c> cannot reference <c>Baton.Cli</c> (the CLI depends on the adapters, never
    /// the reverse) -- same mirroring shape as every other hook-channel constant this vendor already
    /// carries a literal copy of.
    /// </summary>
    private const int DeniedExitCode = 2;

    private static readonly string[] EnvironmentVariablesToStrip =
    [
        ClaudeWorkerAdapter.DeniedToolsVariable,
        ClaudeWorkerAdapter.ShellPatternsVariable,
        ClaudeWorkerAdapter.DeniedShellPatternsVariable,
        ClaudeWorkerAdapter.DeniedShellOptionTokensVariable,
        ClaudeWorkerAdapter.DeniedShellExceptionsVariable,
        WorkerEnvironment.WorkspaceVariable,
        "BATON_OUTPUT_DIR",
    ];

    /// <summary>
    /// A live result for a given <paramref name="hookAssemblyPath"/> cannot meaningfully change
    /// within one short-lived CLI invocation -- same reasoning, same shape, as
    /// <c>ProcessAgyHookLivenessProbe.LiveResultCache</c>. A failed probe is deliberately NOT cached.
    /// </summary>
    private static readonly ConcurrentDictionary<string, ClaudeHookLivenessResult> LiveResultCache = new();

    internal static int SpawnCountForTesting;

    internal static void ResetCacheForTesting()
    {
        LiveResultCache.Clear();
        SpawnCountForTesting = 0;
    }

    public ClaudeHookLivenessResult Probe(string hookAssemblyPath, TimeSpan timeout)
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

    private static ClaudeHookLivenessResult ProbeUncached(string hookAssemblyPath, TimeSpan timeout)
    {
        Interlocked.Increment(ref SpawnCountForTesting);
        if (!File.Exists(hookAssemblyPath))
        {
            return new ClaudeHookLivenessResult(false, $"'{hookAssemblyPath}' does not exist");
        }

        try
        {
            var startInfo = ChildProcessStartInfo.Create("dotnet", startInfo =>
            {
                startInfo.RedirectStandardInput = true;
                startInfo.RedirectStandardOutput = true;
                startInfo.RedirectStandardError = true;
                startInfo.StandardOutputEncoding = System.Text.Encoding.UTF8;
                startInfo.StandardErrorEncoding = System.Text.Encoding.UTF8;
            });
            startInfo.ArgumentList.Add(hookAssemblyPath);
            startInfo.ArgumentList.Add("hook-check");
            foreach (var name in EnvironmentVariablesToStrip)
            {
                startInfo.Environment.Remove(name);
            }

            startInfo.Environment[ClaudeWorkerAdapter.DeniedToolsVariable] = DeniedToolsValue;

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new ClaudeHookLivenessResult(false, "the hook process did not start");
            }

            process.StandardInput.Write(SyntheticWriteToolCall);
            process.StandardInput.Close();

            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Already exited between the WaitForExit timeout and this Kill -- the timeout
                    // verdict below still stands.
                }

                return new ClaudeHookLivenessResult(
                    false, $"the hook did not respond within {timeout.TotalSeconds:0.#}s (timed out)");
            }

            if (process.ExitCode == DeniedExitCode)
            {
                var stderr = process.StandardError.ReadToEnd();
                return new ClaudeHookLivenessResult(true, string.IsNullOrWhiteSpace(stderr) ? "deny" : stderr.Trim());
            }

            return new ClaudeHookLivenessResult(
                false, $"the hook exited {process.ExitCode} instead of the deny code ({DeniedExitCode})");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new ClaudeHookLivenessResult(false, $"the hook process could not be run: {ex.Message}");
        }
    }
}
