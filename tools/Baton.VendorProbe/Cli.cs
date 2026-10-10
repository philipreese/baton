using System.Diagnostics;
using System.Text;

namespace Baton.VendorProbe;

/// <summary>
/// Runs a vendor CLI. Deliberately <b>not</b> through a shell.
/// </summary>
/// <remarks>
/// <para>
/// Arguments go into <see cref="ProcessStartInfo.ArgumentList"/>, so nothing between this code and
/// the CLI interprets them. That is not tidiness — it is the fix for a real failure. Probing
/// <c>claude -p "/usage"</c> through Git Bash on Windows produced a confident wrong answer, because
/// MSYS path conversion rewrote the leading <c>/usage</c> into <c>C:/Program Files/Git/usage</c>
/// <em>before the CLI saw it</em>. The model then answered about that path, which reads exactly like
/// "the command does not exist". A shell-free invocation cannot have that class of bug.
/// </para>
/// <para>
/// The environment is scrubbed of every <c>CLAUDE*</c> variable. A nested <c>claude</c> inherits the
/// parent session's tool set and MCP servers, which no daemon-spawned worker ever has — an earlier
/// probe stripped only <c>CLAUDE_CODE_*</c>, missed <c>CLAUDECODE</c>, <c>CLAUDE_EFFORT</c>,
/// <c>CLAUDE_PID</c> and <c>CLAUDE_JOB_DIR</c>, and produced a result we nearly wrote down as fact.
/// </para>
/// </remarks>
public static class Cli
{
    public sealed record Run(int ExitCode, string StdOut, string StdErr, bool TimedOut)
    {
        public string All => StdOut + "\n" + StdErr;
    }

    public static Run Invoke(string program, IEnumerable<string> args, TimeSpan? timeout = null, string? workingDirectory = null)
    {
        var psi = StartInfo(program, args, workingDirectory);

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { stdout.AppendLine(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { stderr.AppendLine(e.Data); } };

        try
        {
            global::Baton.Core.ProcessLaunch.Start(process);
        }
        catch (Exception ex)
        {
            // A missing CLI is a legitimate probe outcome, not a crash: the matrix should say the
            // vendor was not installed rather than the suite falling over on a machine that has one.
            return new Run(-1, string.Empty, $"could not start '{program}': {ex.Message}", TimedOut: false);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var limit = timeout ?? TimeSpan.FromMinutes(3);
        if (!process.WaitForExit((int)limit.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already gone */ }
            return new Run(-1, stdout.ToString(), stderr.ToString(), TimedOut: true);
        }

        process.WaitForExit(); // flush the async readers
        return new Run(process.ExitCode, stdout.ToString(), stderr.ToString(), TimedOut: false);
    }

    /// <summary>
    /// Runs a newline-delimited request/response protocol until the caller recognises a complete
    /// response set. Unlike <see cref="Invoke"/>, this deliberately terminates the long-lived server
    /// after the evidence has arrived; an app-server is not expected to exit after one request.
    /// </summary>
    public static Run InvokeProtocol(
        string program,
        IEnumerable<string> args,
        IEnumerable<string> inputLines,
        Func<string, bool> isComplete,
        TimeSpan? timeout = null,
        string? workingDirectory = null)
    {
        var psi = StartInfo(program, args, workingDirectory);
        psi.RedirectStandardInput = true;

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        using var completed = new ManualResetEventSlim();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            stdout.AppendLine(e.Data);
            if (isComplete(e.Data))
            {
                completed.Set();
            }
        };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { stderr.AppendLine(e.Data); } };

        try
        {
            global::Baton.Core.ProcessLaunch.Start(process);
        }
        catch (Exception ex)
        {
            return new Run(-1, string.Empty, $"could not start '{program}': {ex.Message}", TimedOut: false);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            foreach (var line in inputLines)
            {
                process.StandardInput.WriteLine(line);
            }
            process.StandardInput.Flush();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // A server that rejects its argv can close stdin before the request set is written. Its
            // stderr and exit code below are better evidence than turning the probe into an exception.
        }

        var limit = timeout ?? TimeSpan.FromSeconds(45);
        var deadline = DateTimeOffset.UtcNow + limit;
        while (!completed.IsSet && !process.HasExited && DateTimeOffset.UtcNow < deadline)
        {
            completed.Wait(TimeSpan.FromMilliseconds(50));
        }

        var observed = completed.IsSet;
        var timedOut = !observed && !process.HasExited && DateTimeOffset.UtcNow >= deadline;
        try
        {
            process.StandardInput.Close();
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The server raced the cleanup and exited normally.
        }

        if (!process.WaitForExit(milliseconds: 5000))
        {
            timedOut = true;
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(milliseconds: 5000);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Keep the probe bounded even when the server cannot be reaped cleanly.
            }
        }

        // A completed async read is flushed by WaitForExit. If the process resisted cleanup, report
        // an unavailable exit code instead of blocking forever or reading ExitCode while it is live.
        observed = observed || completed.IsSet;
        var exitCode = observed ? 0 : process.HasExited ? process.ExitCode : -1;
        return new Run(exitCode, stdout.ToString(), stderr.ToString(), timedOut);
    }

    private static ProcessStartInfo StartInfo(
        string program,
        IEnumerable<string> args,
        string? workingDirectory)
    {
        var psi = new ProcessStartInfo
        {
            FileName = program,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Path.GetTempPath(),
        };

        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        ScrubParentSession(psi);
        return psi;
    }

    private static void ScrubParentSession(ProcessStartInfo psi)
    {
        foreach (var key in psi.Environment.Keys
                     .Where(IsCredentialOrParentSessionVariable)
                     .ToList())
        {
            psi.Environment.Remove(key);
        }
    }

    internal static bool IsCredentialOrParentSessionVariable(string key) =>
        key.StartsWith("CLAUDE", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("AZURE_OPENAI_", StringComparison.OrdinalIgnoreCase)
        || key.Equals("OPENAI_API_KEY", StringComparison.OrdinalIgnoreCase)
        || key.Equals("OPENAI_API_BASE", StringComparison.OrdinalIgnoreCase)
        || key.Equals("OPENAI_BASE_URL", StringComparison.OrdinalIgnoreCase)
        || key.Equals("OPENAI_ORG_ID", StringComparison.OrdinalIgnoreCase)
        || key.Equals("OPENAI_ORGANIZATION", StringComparison.OrdinalIgnoreCase)
        || key.Equals("OPENAI_PROJECT", StringComparison.OrdinalIgnoreCase)
        || key.Equals("CODEX_API_KEY", StringComparison.OrdinalIgnoreCase)
        || key.Equals("CODEX_BASE_URL", StringComparison.OrdinalIgnoreCase);

    public static bool IsInstalled(string program) =>
        Invoke(program, ["--version"], TimeSpan.FromSeconds(30)).ExitCode == 0;

    /// <summary>The version a finding is attributed to. Results expire when this moves.</summary>
    public static string? Version(string program)
    {
        var run = Invoke(program, ["--version"], TimeSpan.FromSeconds(30));
        if (run.ExitCode != 0)
        {
            return null;
        }

        var line = run.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        return string.IsNullOrWhiteSpace(line) ? null : line;
    }

    /// <summary>
    /// A throwaway working directory, removed afterwards. The suite must never mutate the operator's
    /// environment — no writes to <c>~/.claude</c>, <c>~/.gemini</c>, or any settings file.
    /// </summary>
    public static void InScratch(Action<string> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "aer-vendor-probe-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            body(dir);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }
}
