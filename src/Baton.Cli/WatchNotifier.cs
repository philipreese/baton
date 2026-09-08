using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Cli.Daemon;

namespace Baton.Cli;

/// <summary>
/// The JSON body a fired <c>baton watch</c> sends — identical whether the target is a command's
/// stdin/<c>BATON_WATCH_EVENT</c> or an HTTP POST body (spec/baton.md §2), so a consumer never has
/// to branch on which transport delivered it.
/// </summary>
public sealed record WatchNotifyPayload(
    [property: JsonPropertyName("room")] string Room,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("verdict")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    JsonElement? Verdict,
    [property: JsonPropertyName("outputs")] IReadOnlyList<string> Outputs,
    [property: JsonPropertyName("terminalAt")] DateTime TerminalAt);

/// <summary>Sends a fired watch's <see cref="WatchNotifyPayload"/> to its registered target — an
/// injectable seam so tests exercise the URL arm against a fake <see cref="HttpMessageHandler"/> and
/// the command arm against a real, harmless spawned process, neither one touching the network or a
/// production shell for real.</summary>
public interface IWatchNotifier
{
    Task NotifyAsync(string target, WatchNotifyPayload payload, CancellationToken cancellationToken);
}

/// <summary>
/// <c>--notify &lt;target&gt;</c>'s two shapes (spec/baton.md §2): an absolute <c>http(s)</c> URL is
/// POSTed the payload as its JSON body; anything else is a command line, spawned once through the
/// platform shell with the payload delivered only via its stdin stream and the
/// <see cref="NotifyEventEnvironmentVariable"/> variable — kept out of the argv/command text
/// entirely, so a room path with a space or an error message with a quote cannot reshape what the
/// operator's own command actually runs.
/// </summary>
/// <remarks>
/// <b>The command's stdout and stderr are redirected and drained, never inherited</b> (#2117 re-review,
/// finding 1). Until then this spawn redirected stdin only and let the command write to this
/// process's own console by inheritance, which is fine only while that console handle is
/// inheritable — and inside the daemon it stops being so at the first queue-lane launch, because
/// <see cref="Baton.Core.DetachedProcess"/> clears the inherit flag process-wide and for good (its
/// remarks say why). A command spawned after that with stdin-only redirection is handed a stdout it
/// cannot inherit and its writes vanish with no error. So both streams are piped and relayed line by
/// line into <see cref="Console.Out"/>/<see cref="Console.Error"/> (or the writers the constructor
/// was given), which is where the inherited output landed before — <c>daemon.log</c> under the
/// daemon, the operator's console under <c>baton watch</c>. The relay starts before the stdin write
/// and runs until the drain below ends, so a command that emits more than the pipe buffer (~4 KB)
/// is never blocked on an unread pipe — the reason redirecting was previously avoided, and the
/// reason the drain is not optional. <c>SpawnOutputRedirectionTests</c> is what keeps this site
/// redirecting both streams.
/// </remarks>
public sealed class WatchNotifier : IWatchNotifier
{
    public const string NotifyEventEnvironmentVariable = "BATON_WATCH_EVENT";

    /// <summary>How long a spawned notify command is given to exit before this stops waiting on it
    /// (and reports that on stderr) — it is not killed on THIS path, since "spawned once" (spec/baton.md
    /// §2) means exactly that: a command that exits on its own within the budget still runs to
    /// completion, this process just stops blocking on it (its output relay is closed at that point,
    /// so whatever it prints afterwards reaches nobody). Generous: a webhook-posting script or an
    /// ntfy curl call is the expected shape, never a long-running watcher of its own. The SAME budget
    /// also bounds the stdin write below, where the command IS killed on timeout — spec/baton.md §2
    /// (H1) states why the two branches differ.</summary>
    public static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions PayloadJsonOptions = new() { WriteIndented = false };

    /// <summary><c>AllowAutoRedirect = false</c>: a 3xx on the URL arm's POST must be logged as the
    /// non-success it is, not silently re-issued by <see cref="HttpClient"/> as a bodyless GET whose
    /// eventual 200 would read as delivery succeeding when the payload never arrived.</summary>
    private static readonly HttpClient SharedHttpClient = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    private readonly HttpClient _httpClient;
    private readonly TimeSpan _commandTimeout;
    private readonly TextWriter? _commandOutput;
    private readonly TextWriter? _commandError;

    /// <param name="httpClient">The URL arm's client; the shared one when null.</param>
    /// <param name="commandTimeout"><see cref="CommandTimeout"/> when null.</param>
    /// <param name="commandOutput">Where a spawned command's stdout lines are relayed;
    /// <see cref="Console.Out"/> when null, resolved at spawn time.</param>
    /// <param name="commandError">Where its stderr lines are relayed; <see cref="Console.Error"/> when
    /// null, resolved at spawn time.</param>
    public WatchNotifier(
        HttpClient? httpClient = null,
        TimeSpan? commandTimeout = null,
        TextWriter? commandOutput = null,
        TextWriter? commandError = null)
    {
        _httpClient = httpClient ?? SharedHttpClient;
        _commandTimeout = commandTimeout ?? CommandTimeout;
        _commandOutput = commandOutput;
        _commandError = commandError;
    }

    public async Task NotifyAsync(string target, WatchNotifyPayload payload, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(target);
        ArgumentNullException.ThrowIfNull(payload);

        var json = JsonSerializer.Serialize(payload, PayloadJsonOptions);

        if (IsHttpUrl(target))
        {
            await PostAsync(target, json, cancellationToken).ConfigureAwait(false);
            return;
        }

        await SpawnCommandAsync(target, json, _commandTimeout, cancellationToken).ConfigureAwait(false);
    }

    internal static bool IsHttpUrl(string target) =>
        Uri.TryCreate(target, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private async Task PostAsync(string url, string json, CancellationToken cancellationToken)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"baton watch: notify POST to '{url}' returned {(int)response.StatusCode}.");
        }
    }

    private async Task SpawnCommandAsync(
        string command, string json, TimeSpan commandTimeout, CancellationToken cancellationToken)
    {
        var psi = ChildProcessStartInfo.Create(string.Empty, psi =>
        {
            psi.RedirectStandardInput = true;

            // Both output streams, or the daemon's cleared inherit flag eats one of them -- the class
            // remarks. The decode is pinned for the same reason every other redirecting site pins it
            // (#466, RedirectedProcessEncodingTests).
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
        });

        if (OperatingSystem.IsWindows())
        {
            // cmd.exe re-parses its own raw command-line text rather than taking an argv array, so
            // the target must be handed through verbatim as `Arguments` -- .NET's default per-argument
            // escaping via ArgumentList (backslash-escaping embedded quotes, MSVCRT-style) is
            // meaningless to cmd.exe's own parser and corrupts any target that itself contains a
            // quoted piece (an operator's own `curl -H "X: Y"` inside --notify), which silently failed
            // to spawn anything at all under ArgumentList (caught by this type's own tests).
            psi.FileName = "cmd.exe";
            psi.Arguments = $"/c {command}";
        }
        else
        {
            psi.FileName = "/bin/sh";
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);
        }

        psi.Environment[NotifyEventEnvironmentVariable] = json;

        using var process = Process.Start(psi);
        if (process is null)
        {
            Console.Error.WriteLine($"baton watch: failed to spawn notify command '{command}'.");
            return;
        }

        // The relay is attached BEFORE the stdin write: a command that prints first and reads later
        // would otherwise fill its 4 KB output pipe and block, and the stdin write below would then
        // time out against a command that was never going to read -- a self-inflicted wedge.
        var output = _commandOutput ?? Console.Out;
        var error = _commandError ?? Console.Error;
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                output.WriteLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                error.WriteLine(e.Data);
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // The timeout is armed BEFORE the stdin write starts, and the write itself runs under it
        // (`.WaitAsync`) -- arming it only around WaitForExitAsync below, as this used to, guards
        // nothing (spec/baton.md §2 has the failure mode and why this is the fix, H1). `cancellationToken`
        // alone cannot cancel a blocked write: `StandardInput` wraps a synchronous `FileStream`, and the
        // token is only checked before the write begins.
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(commandTimeout);

        var stdinTimedOut = false;
        try
        {
            await process.StandardInput.WriteAsync(json.AsMemory(), cancellationToken)
                .WaitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stdinTimedOut = true;
            Console.Error.WriteLine(
                $"baton watch: notify command '{command}' did not drain stdin within {commandTimeout} " +
                "— killing it.");
            TryKillProcessTree(process);
        }

        if (stdinTimedOut)
        {
            return;
        }

        // Only reached once the write itself completed -- closing here (rather than in a `finally`
        // above) is deliberate: the write task above is still running against a blocked OS call on
        // timeout, and closing the stream out from under it is not a safe way to unblock it, which is
        // why the timeout branch kills the process tree instead.
        process.StandardInput.Close();

        // Two waits of different kinds, the shape QueueLauncher.SuperviseAsync's remarks explain: the
        // OS exit signal first, under the command's own budget, and only then the redirected streams,
        // under the supervisor's drain bound -- because WaitForExitAsync on a process with async readers
        // also waits for the pipes to reach EOF, which a grandchild the command left behind can hold
        // open long after the command itself has exited.
        try
        {
            await QueueLauncher.WaitForOsExitAsync(process).WaitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine(
                $"baton watch: notify command '{command}' did not exit within {commandTimeout} — leaving it running.");
            return;
        }

        try
        {
            using var drainBound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            drainBound.CancelAfter(QueueLauncher.StreamDrainBound);
            await process.WaitForExitAsync(drainBound.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine(
                $"baton watch: notify command '{command}' exited, but something it started still holds its output "
                + $"open; stopped reading it after {QueueLauncher.StreamDrainBound}.");
        }

        if (process.ExitCode != 0)
        {
            Console.Error.WriteLine($"baton watch: notify command '{command}' exited {process.ExitCode}.");
        }
    }

    /// <summary>Best-effort: the stdin-drain timeout means the command is never going to finish reading
    /// input it was supposed to consume, so there is nothing left to wait on — unlike the exit-timeout
    /// branch above, which leaves a command that DID see EOF free to keep running on its own.</summary>
    private static void TryKillProcessTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Already exited (race between the timeout firing and the command finishing on its own) or
            // the OS refused -- either way, the stderr message above already told the operator what
            // happened; there is nothing more this notifier can do.
        }
    }
}
