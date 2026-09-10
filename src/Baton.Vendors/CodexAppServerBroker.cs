using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Baton.Domain;
using Baton.Status;

namespace Baton.Vendors;

/// <summary>
/// Bidirectional bridge between Baton's one-process JSONL dispatch seam and Codex app-server.
/// App-server's native mutation/tool surfaces are disabled; server-initiated dynamic tool calls are
/// answered only through <see cref="CodexDynamicToolPolicy"/>.
/// <para>
/// <b>Usage is emitted per model round-trip, not once per turn (#2020 review HIGH).</b> app-server
/// sends <c>thread/tokenUsage/updated</c> after every round-trip, carrying <c>tokenUsage.last</c>
/// (that round-trip) and <c>tokenUsage.total</c> (cumulative). This broker latched <c>last</c> into a
/// local and emitted it once at turn end, so a 110-step lane's captured stream reported the FINAL
/// round-trip's figures as the execution's usage and every earlier one was discarded before a byte
/// was written — no fold downstream could recover them. It now writes one
/// <c>{"type":"turn.usage"}</c> line per notification carrying <c>last</c>, and
/// <see cref="CodexUsageParser.ParseExecutionUsage"/> sums them.
/// </para>
/// <para>
/// <b>Why <c>last</c> summed rather than <c>total</c> read off the final notification</b>, which is the
/// simpler shape: <c>total</c> is an unmeasured key here. In-tree it appears only in this class's own
/// test transcript, where a single-update fixture makes it trivially equal to <c>last</c> — consistent
/// with a cumulative reading, not evidence of one — and no recorded app-server event grammar states
/// whether it restarts across a <c>thread/resume</c>. Summing <c>last</c> asserts no new vendor fact
/// beyond the one this class already relies on. Reading <c>total</c> needs a probe that records the
/// payload first (spec/baton.md §7's ledger row is where such a measurement lands).
/// </para>
/// <para>
/// The terminal <c>turn.completed</c> keeps carrying the final round-trip's usage unchanged — it is
/// what <c>Outcomes.OutcomeClassifier</c>'s last-line substantial-work evidence reads — so on a
/// current stream that figure appears TWICE, once on its own <c>turn.usage</c> line and once on the
/// terminal one. Both lines therefore carry
/// <see cref="CodexUsageParser.RoundTripField"/>, a 1-based index of the round-trip they report, and
/// the terminal line repeats the last one's. That index is what lets a Σ over the stream drop the
/// restatement (<c>Mutation.TokenBudgetMonitor</c>'s existing repeated-id rule) without the reader
/// needing to know which line types it has seen — and a stream captured before this emitter carries
/// no index at all, so it keeps accumulating exactly as it did.
/// </para>
/// </summary>
public static class CodexAppServerBroker
{
    private const int InitializeRequestId = 1;
    private const int ThreadRequestId = 2;
    private const int TurnRequestId = 3;
    private const int RateLimitsRequestId = 2;
    internal static readonly TimeSpan RateLimitsSourceBound = TimeSpan.FromSeconds(45);
    internal static readonly TimeSpan RateLimitsCleanupReserve = TimeSpan.FromSeconds(5);
    internal static readonly Encoding JsonLineEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static async Task<int> RunAsync(
        CodexBrokerConfiguration configuration,
        string prompt,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var outputDirectory = Environment.GetEnvironmentVariable("BATON_OUTPUT_DIR");
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            await error.WriteLineAsync("Codex broker requires BATON_OUTPUT_DIR.").ConfigureAwait(false);
            return 1;
        }

        string isolatedHome;
        try
        {
            isolatedHome = CodexIsolatedHome.Prepare(BatonPaths.Root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            await error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 1;
        }

        var inputPaths = Environment.GetEnvironmentVariables().Keys.Cast<object>()
            .Select(key => key.ToString())
            .Where(key => key?.StartsWith("BATON_INPUT_", StringComparison.Ordinal) == true)
            .Order(StringComparer.Ordinal)
            .Select(key => Environment.GetEnvironmentVariable(key!)!)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        var policy = new CodexDynamicToolPolicy(
            configuration.PermissionGrant,
            configuration.WorkingDirectory,
            outputDirectory,
            inputPaths,
            configuration.ProducedOutputNames);

        using var process = StartAppServer(configuration, isolatedHome);
        if (process is null)
        {
            await error.WriteLineAsync("Baton could not start codex app-server.").ConfigureAwait(false);
            return 1;
        }

        var stderrDrain = DrainStderrAsync(process.StandardError, error, cancellationToken);
        try
        {
            return await RunProtocolAsync(
                configuration, prompt, policy, process.StandardInput, process.StandardOutput,
                output, error, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException
            or UnauthorizedAccessException)
        {
            await EmitAsync(output, new JsonObject
            {
                ["type"] = "error",
                ["message"] = ex.Message,
            }).ConfigureAwait(false);
            return 1;
        }
        finally
        {
            process.StandardInput.Close();
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // The process raced the broker to a normal exit.
                }
            }
            await stderrDrain.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads authenticated account limits through the broker's isolated home and app-server
    /// lifecycle, stopping before any thread or model turn is started. The whole source owns a
    /// 45-second managed bound: forty seconds shared by isolated-home preparation, process start,
    /// initialize, and read, with five reserved for kill, exit, and stderr drain. A synchronous OS
    /// call is run off the harvester thread so the managed deadline can return; .NET cannot forcibly
    /// stop an arbitrary blocked OS call, so late process creation is observed and cleaned up when it
    /// eventually returns.
    /// </summary>
    internal static Task<JsonObject?> ReadRateLimitsAsync(CancellationToken cancellationToken) =>
        ReadRateLimitsAsync(
            () =>
            {
                var isolatedHome = CodexIsolatedHome.Prepare(BatonPaths.Root);
                return StartAppServer(Environment.CurrentDirectory, allowsSubagents: false, isolatedHome);
            },
            Console.Error,
            RateLimitsSourceBound,
            RateLimitsCleanupReserve,
            cancellationToken);

    /// <summary>
    /// Process-creation seam for the source contract. Creation belongs inside the same ordinary-failure
    /// boundary as protocol errors: an absent or unstartable executable is a logged null harvest, not an
    /// exception escaping <see cref="IVendorUsageSource.ReadAsync"/>.
    /// </summary>
    internal static async Task<JsonObject?> ReadRateLimitsAsync(
        Func<Process?> startAppServer,
        TextWriter error,
        TimeSpan sourceBound,
        TimeSpan cleanupReserve,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startAppServer);
        ArgumentNullException.ThrowIfNull(error);
        if (sourceBound <= cleanupReserve || cleanupReserve <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceBound));
        }
        cancellationToken.ThrowIfCancellationRequested();

        var responseBound = sourceBound - cleanupReserve;
        using var responseTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        responseTimeout.CancelAfter(responseBound);

        var startTask = Task.Run(startAppServer, CancellationToken.None);
        Process? started;
        try
        {
            started = await startTask.WaitAsync(responseTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = StopLateRateLimitsProcessAsync(startTask);
            throw;
        }
        catch (OperationCanceledException) when (responseTimeout.IsCancellationRequested)
        {
            _ = StopLateRateLimitsProcessAsync(startTask);
            await error.WriteLineAsync(
                $"Codex rate-limit harvest did not start within {responseBound.TotalSeconds:0}s.")
                .ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException
            or ArgumentException or InvalidOperationException)
        {
            await error.WriteLineAsync($"Codex rate-limit harvest failed: {ex.Message}").ConfigureAwait(false);
            return null;
        }

        if (started is null)
        {
            await error.WriteLineAsync("Codex rate-limit harvest could not start codex app-server.")
                .ConfigureAwait(false);
            return null;
        }

        var process = started;
        Task stderrDrain = Task.CompletedTask;
        return await ReadRateLimitsWithinBoundsAsync(
            token =>
            {
                // Drain until process exit. The cleanup's own WaitAsync supplies the bound; coupling the
                // drain to the response token would make a normal response timeout look like failed cleanup.
                stderrDrain = DrainStderrAsync(process.StandardError, error, CancellationToken.None);
                return ReadRateLimitsProtocolAsync(
                    process.StandardInput, process.StandardOutput, error, token);
            },
            token => StopRateLimitsProcessAsync(process, stderrDrain, token),
            error,
            responseTimeout,
            responseBound,
            cleanupReserve,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One bounded lifecycle around response and cleanup. <see cref="Task.WaitAsync(CancellationToken)"/>
    /// enforces both bounds even when an underlying stream or cleanup operation ignores its token.
    /// A phase that outlives its wait is observed asynchronously: its late exception is diagnostic and
    /// cannot become an unobserved task or extend the foreground deadline.
    /// </summary>
    internal static async Task<JsonObject?> ReadRateLimitsWithinBoundsAsync(
        Func<CancellationToken, Task<JsonObject>> read,
        Func<CancellationToken, Task> cleanup,
        TextWriter error,
        TimeSpan responseBound,
        TimeSpan cleanupBound,
        CancellationToken cancellationToken)
    {
        using var responseTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        responseTimeout.CancelAfter(responseBound);
        return await ReadRateLimitsWithinBoundsAsync(
            read, cleanup, error, responseTimeout, responseBound, cleanupBound, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<JsonObject?> ReadRateLimitsWithinBoundsAsync(
        Func<CancellationToken, Task<JsonObject>> read,
        Func<CancellationToken, Task> cleanup,
        TextWriter error,
        CancellationTokenSource responseTimeout,
        TimeSpan responseBound,
        TimeSpan cleanupBound,
        CancellationToken cancellationToken)
    {
        var responseToken = responseTimeout.Token;
        Task<JsonObject>? readTask = null;
        try
        {
            readTask = Task.Run(() =>
            {
                responseToken.ThrowIfCancellationRequested();
                return read(responseToken);
            }, CancellationToken.None);
            return await readTask.WaitAsync(responseToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = ObserveLatePhaseAsync(readTask, error, "read");
            throw;
        }
        catch (OperationCanceledException) when (responseTimeout.IsCancellationRequested)
        {
            _ = ObserveLatePhaseAsync(readTask, error, "read");
            await error.WriteLineAsync(
                $"Codex rate-limit harvest did not answer within {responseBound.TotalSeconds:0}s.")
                .ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException
            or UnauthorizedAccessException)
        {
            await error.WriteLineAsync($"Codex rate-limit harvest failed: {ex.Message}").ConfigureAwait(false);
            return null;
        }
        finally
        {
            using var cleanupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cleanupTimeout.CancelAfter(cleanupBound);
            var cleanupToken = cleanupTimeout.Token;
            Task? cleanupTask = null;
            try
            {
                cleanupTask = Task.Run(() => cleanup(cleanupToken), CancellationToken.None);
                await cleanupTask.WaitAsync(cleanupToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _ = ObserveLatePhaseAsync(cleanupTask, error, "cleanup");
                // Cleanup was dispatched before cancellation is propagated. The production delegate
                // owns the process until its kill/exit/drain attempt finishes, even if the caller no
                // longer waits for it.
                throw;
            }
            catch (OperationCanceledException) when (cleanupTimeout.IsCancellationRequested)
            {
                _ = ObserveLatePhaseAsync(cleanupTask, error, "cleanup");
                await error.WriteLineAsync(
                    $"Codex rate-limit harvest cleanup did not finish within {cleanupBound.TotalSeconds:0}s.")
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
            {
                await error.WriteLineAsync($"Codex rate-limit harvest cleanup failed: {ex.Message}")
                    .ConfigureAwait(false);
            }
        }
    }

    private static async Task ObserveLatePhaseAsync(Task? task, TextWriter error, string phase)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is the expected late outcome after the phase's token was cancelled.
        }
        catch (Exception ex)
        {
            try
            {
                await error.WriteLineAsync(
                    $"Codex rate-limit harvest {phase} failed after its deadline: {ex.Message}")
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The caller may have disposed its diagnostic writer after the foreground returned.
                // The phase exception has still been observed, and this observer must never fault too.
            }
        }
    }

    internal static async Task StopRateLimitsProcessAsync(
        Process process, Task stderrDrain, CancellationToken cancellationToken)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await stderrDrain.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            process.Dispose();
        }
    }

    /// <summary>
    /// Observes a preparation/start call that outlived its caller. If it eventually creates a
    /// process, that process still enters the production process-tree cleanup path rather than
    /// becoming an orphan. The wait itself is intentionally detached: the caller's managed deadline
    /// has already elapsed, and managed cancellation cannot stop arbitrary synchronous OS startup.
    /// </summary>
    private static async Task StopLateRateLimitsProcessAsync(Task<Process?> startTask)
    {
        try
        {
            if (await startTask.ConfigureAwait(false) is { } process)
            {
                await StopRateLimitsProcessAsync(process, Task.CompletedTask, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // The foreground path has already reported timeout or cancellation. This continuation
            // exists only to observe every late startup/cleanup outcome and make a best-effort
            // process-tree cleanup. It must never become a second unobserved task.
        }
    }

    internal static async Task<int> RunProtocolAsync(
        CodexBrokerConfiguration configuration,
        string prompt,
        CodexDynamicToolPolicy policy,
        TextWriter serverInput,
        TextReader serverOutput,
        TextWriter batonOutput,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(serverInput, serverOutput, error, cancellationToken).ConfigureAwait(false);

        var threadParams = BuildThreadParams(configuration, policy);
        await SendAsync(serverInput, new JsonObject
        {
            ["method"] = configuration.ResumeSession ? "thread/resume" : "thread/start",
            ["id"] = ThreadRequestId,
            ["params"] = threadParams,
        }, cancellationToken).ConfigureAwait(false);
        var threadResponse = await ReadResponseAsync(
            serverOutput, ThreadRequestId, error, cancellationToken).ConfigureAwait(false);
        var threadId = threadResponse["result"]?["thread"]?["id"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Codex app-server did not return a thread ID.");

        await EmitAsync(batonOutput, new JsonObject
        {
            ["type"] = "thread.started",
            ["thread_id"] = threadId,
        }).ConfigureAwait(false);

        await SendAsync(serverInput, new JsonObject
        {
            ["method"] = "turn/start",
            ["id"] = TurnRequestId,
            ["params"] = new JsonObject
            {
                ["threadId"] = threadId,
                ["input"] = new JsonArray(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = prompt,
                }),
                ["approvalPolicy"] = "never",
                ["sandboxPolicy"] = new JsonObject { ["type"] = "readOnly" },
                ["environments"] = new JsonArray(),
            },
        }, cancellationToken).ConfigureAwait(false);
        await ReadResponseAsync(serverOutput, TurnRequestId, error, cancellationToken).ConfigureAwait(false);
        await EmitAsync(batonOutput, new JsonObject { ["type"] = "turn.started" }).ConfigureAwait(false);

        JsonObject? lastUsage = null;
        var roundTrip = 0;
        while (await serverOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (!TryParseObject(line, out var message))
            {
                await error.WriteLineAsync($"Ignored non-JSON app-server output: {line}").ConfigureAwait(false);
                continue;
            }

            if (message["id"] is not null && message["method"]?.GetValue<string>() == "item/tool/call")
            {
                await HandleDynamicToolCallAsync(
                    message, policy, serverInput, batonOutput, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var method = message["method"]?.GetValue<string>();
            switch (method)
            {
                case "thread/tokenUsage/updated":
                    lastUsage = message["params"]?["tokenUsage"]?["last"]?.AsObject().DeepClone().AsObject();
                    // #2020: written as it arrives, so an execution the host arrests mid-turn still
                    // carries every round-trip it had already paid for. See the class remark.
                    // It also makes a mid-turn arrest REACHABLE on codex rather than merely survivable:
                    // Baton.Mutation.TokenBudgetMonitor now sees a reading per round-trip instead of one
                    // at turn end, so its budget and billed-rate rungs can fire part-way through a turn
                    // exactly as they do on claude and agy. That remark states the decision.
                    if (lastUsage is not null)
                    {
                        roundTrip++;
                        await EmitAsync(batonOutput, new JsonObject
                        {
                            ["type"] = CodexUsageParser.TurnUsageEventType,
                            ["usage"] = ToBatonUsage(lastUsage, roundTrip),
                        }).ConfigureAwait(false);
                    }
                    break;
                case "item/completed":
                    await EmitCompletedItemAsync(message, batonOutput).ConfigureAwait(false);
                    break;
                case "error":
                    if (message["params"]?["willRetry"]?.GetValue<bool>() != true)
                    {
                        await EmitAsync(batonOutput, new JsonObject
                        {
                            ["type"] = "error",
                            ["error"] = message["params"]?["error"]?.DeepClone(),
                        }).ConfigureAwait(false);
                    }
                    break;
                case "turn/completed":
                    return await EmitTerminalTurnAsync(message, lastUsage, roundTrip, batonOutput).ConfigureAwait(false);
            }
        }

        throw new IOException("Codex app-server closed stdout before a terminal turn event.");
    }

    internal static async Task<JsonObject> ReadRateLimitsProtocolAsync(
        TextWriter serverInput,
        TextReader serverOutput,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(serverInput, serverOutput, error, cancellationToken).ConfigureAwait(false);
        await SendAsync(serverInput, new JsonObject
        {
            ["method"] = "account/rateLimits/read",
            ["id"] = RateLimitsRequestId,
            ["params"] = new JsonObject(),
        }, cancellationToken).ConfigureAwait(false);
        var response = await ReadResponseAsync(
            serverOutput, RateLimitsRequestId, error, cancellationToken).ConfigureAwait(false);
        return response["result"] as JsonObject
            ?? throw new InvalidOperationException("Codex app-server returned no rate-limit result.");
    }

    private static async Task InitializeAsync(
        TextWriter serverInput,
        TextReader serverOutput,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        await SendAsync(serverInput, new JsonObject
        {
            ["method"] = "initialize",
            ["id"] = InitializeRequestId,
            ["params"] = new JsonObject
            {
                ["clientInfo"] = new JsonObject
                {
                    ["name"] = "baton",
                    ["title"] = "Baton Codex broker",
                    ["version"] = "1",
                },
                ["capabilities"] = new JsonObject { ["experimentalApi"] = true },
            },
        }, cancellationToken).ConfigureAwait(false);
        await ReadResponseAsync(serverOutput, InitializeRequestId, error, cancellationToken).ConfigureAwait(false);
        await SendAsync(serverInput, new JsonObject
        {
            ["method"] = "initialized",
            ["params"] = new JsonObject(),
        }, cancellationToken).ConfigureAwait(false);
    }

    private static Process? StartAppServer(CodexBrokerConfiguration configuration, string isolatedHome) =>
        StartAppServer(configuration.WorkingDirectory, configuration.AllowsSubagents, isolatedHome);

    private static Process? StartAppServer(string? workingDirectory, bool allowsSubagents, string isolatedHome)
    {
        var startInfo = ChildProcessStartInfo.Create(CodexExecutableResolver.Resolve(), startInfo =>
        {
            startInfo.RedirectStandardInput = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            // app-server consumes one JSON-RPC object per line. Encoding.UTF8's preamble becomes
            // the first bytes on redirected stdin on Windows, and app-server rejects that BOM as
            // "expected value at line 1 column 1" before initialize. Pin every redirected stream
            // to BOM-less UTF-8; stdout/stderr use the same encoding for symmetry.
            startInfo.StandardInputEncoding = JsonLineEncoding;
            startInfo.StandardOutputEncoding = JsonLineEncoding;
            startInfo.StandardErrorEncoding = JsonLineEncoding;
            startInfo.WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory;
        });
        startInfo.ArgumentList.Add("app-server");
        startInfo.ArgumentList.Add("--stdio");
        foreach (var feature in DisabledFeatures(allowsSubagents))
        {
            startInfo.ArgumentList.Add("--disable");
            startInfo.ArgumentList.Add(feature);
        }
        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add("mcp_servers={}");
        startInfo.Environment["CODEX_HOME"] = isolatedHome;
        startInfo.Environment["CODEX_SQLITE_HOME"] = isolatedHome;
        foreach (var name in startInfo.Environment.Keys
            .Where(name => name.StartsWith("BATON_", StringComparison.Ordinal)).ToArray())
        {
            startInfo.Environment.Remove(name);
        }
        return Process.Start(startInfo);
    }

    internal static IReadOnlyList<string> DisabledFeatures(bool allowsSubagents)
    {
        List<string> features =
        [
            "shell_tool", "unified_exec", "apps", "browser_use",
            "computer_use", "image_generation",
        ];
        // Current Codex routes tool-backed work through Code Mode. Its runtime receives only the
        // nested tool definitions assembled for this turn; with the native tools above disabled,
        // those are precisely Baton's grant-derived dynamic tools. Disabling code_mode_host makes
        // the model attempt a tool path that is guaranteed to fail before any dynamic call reaches
        // Baton, so the host is an orchestration mechanism here, not an additional authority.
        // Dynamic tools are not inherited automatically by subagents. Keep delegation absent until
        // that inheritance has been measured; AllowsSubagents is a ceiling, not a requirement.
        features.Add("multi_agent");
        features.Add("multi_agent_v2");
        return features;
    }

    internal static JsonObject BuildThreadParams(
        CodexBrokerConfiguration configuration, CodexDynamicToolPolicy policy)
    {
        var tools = policy.BuildToolDefinitions();
        // #1996 re-review MEDIUM: the instruction used to say "only the provided baton_* dynamic
        // tools", a glob that by its own wording excluded the edit tool sitting in the very manifest
        // it constrains — the model obeying it lands back on "I cannot edit this workspace", which is
        // what #1996 measured. The read-only native sandbox applies to the disabled native tools;
        // Baton's declared dynamic tools instead operate under this role's actual grant. It names the
        // edit tool from the policy's own constant, and only when this thread actually declares it, so
        // it stays a constraint on the list rather than a second copy of it.
        var declaresEditTool = tools.Any(
            tool => tool?["name"]?.GetValue<string>() == CodexDynamicToolPolicy.ApplyPatchTool);
        var result = new JsonObject
        {
            ["cwd"] = configuration.WorkingDirectory,
            ["approvalPolicy"] = "never",
            ["sandbox"] = "read-only",
            ["ephemeral"] = false,
            ["model"] = configuration.Model,
            ["dynamicTools"] = tools,
            ["environments"] = new JsonArray(),
            ["developerInstructions"] =
                "You are a Baton worker. Use only the dynamic tools declared on this thread, whatever "
                + "their names."
                + " The read-only native sandbox applies to disabled native tools; declared Baton tools "
                + "operate under this role's actual grant."
                + (declaresEditTool
                    ? $" {CodexDynamicToolPolicy.ApplyPatchTool} is this thread's edit tool."
                    : string.Empty)
                + " A denied tool result is a hard permission boundary; do not seek another route.",
            ["config"] = new JsonObject
            {
                ["mcp_servers"] = new JsonObject(),
                ["web_search"] = "disabled",
                ["features"] = new JsonObject
                {
                    ["shell_tool"] = false,
                    ["unified_exec"] = false,
                    ["apps"] = false,
                    ["browser_use"] = false,
                    ["computer_use"] = false,
                    ["image_generation"] = false,
                    ["multi_agent"] = false,
                    ["multi_agent_v2"] = false,
                },
            },
        };
        if (configuration.Effort is { Length: > 0 })
        {
            result["config"]!["model_reasoning_effort"] = configuration.Effort;
        }
        if (configuration.ResumeSession)
        {
            result.Clear();
            result["threadId"] = configuration.SessionId;
            result["cwd"] = configuration.WorkingDirectory;
            result["approvalPolicy"] = "never";
            result["sandbox"] = "read-only";
            result["model"] = configuration.Model;
        }
        return result;
    }

    private static async Task HandleDynamicToolCallAsync(
        JsonObject message,
        CodexDynamicToolPolicy policy,
        TextWriter serverInput,
        TextWriter batonOutput,
        CancellationToken cancellationToken)
    {
        var tool = message["params"]?["tool"]?.GetValue<string>() ?? "unknown";
        var arguments = message["params"]?["arguments"];
        using var argumentsDocument = JsonDocument.Parse(arguments?.ToJsonString() ?? "{}");
        // #2008: computed ONCE and stamped on both of this call's items. Two computations of the same
        // two fields is how the started and the completed item would come to disagree about a call
        // they both name.
        var digest = ArgumentsDigest(argumentsDocument.RootElement);
        var identity = CodexDynamicToolPolicy.InputIdentity(tool, argumentsDocument.RootElement);
        await EmitAsync(batonOutput, new JsonObject
        {
            ["type"] = "item.started",
            ["item"] = Describe(tool, digest, identity),
        }).ConfigureAwait(false);
        var result = await policy.ExecuteAsync(tool, argumentsDocument.RootElement, cancellationToken)
            .ConfigureAwait(false);
        await SendAsync(serverInput, new JsonObject
        {
            ["id"] = message["id"]!.DeepClone(),
            ["result"] = new JsonObject
            {
                ["success"] = result.Success,
                ["contentItems"] = new JsonArray(new JsonObject
                {
                    ["type"] = "inputText",
                    ["text"] = result.Text,
                }),
            },
        }, cancellationToken).ConfigureAwait(false);
        var completed = Describe(tool, digest, identity);
        completed["status"] = result.Success ? "completed" : "failed";
        completed["aggregated_output"] = result.Text;
        await EmitAsync(batonOutput, new JsonObject
        {
            ["type"] = "item.completed",
            ["item"] = completed,
        }).ConfigureAwait(false);

        // #2009: the grant decision itself, as its own structured line. Emitted through the same
        // EmitAsync every other room fact uses, so it lands in this execution's captured `.stdout.log`
        // beside the call it judged — this broker IS that stream's writer, which is why the codex
        // enforcement point needs no file of its own (the two hooks' does: GrantDecisionLog).
        //
        // The decision is read off `result.Rule`, never off the refusal text: a Failed result is an
        // ALLOWED call that did not succeed (a non-zero exit, a missing file), and counting it as a
        // refusal is the exact over-count CodexDynamicToolResult's own remarks were written to end.
        // The digest is the one already emitted on item.started above, from the same function, so the
        // two lines describing one call carry the same identity.
        var allowed = result.Rule == GrantRules.Allowed;
        await EmitAsync(batonOutput, new GrantDecision(
            VendorTag, tool, allowed, result.Rule, allowed ? null : result.Text,
            ArgumentsDigest(argumentsDocument.RootElement), DateTimeOffset.UtcNow).ToJsonNode())
            .ConfigureAwait(false);
    }

    /// <summary>
    /// This broker's vendor, as a <see cref="GrantDecision"/> line spells it — the same lowercase
    /// spelling <c>CodexWorkerAdapter</c> reports in its capabilities and the two hooks use for their
    /// own env-var vendor tags.
    /// </summary>
    private const string VendorTag = "codex";

    /// <summary>
    /// #2008: one <c>mcp_tool_call</c> item's identifying fields, stamped on BOTH lifecycle items of a
    /// call so every such item in a room answers the same two questions. Before this, the digest sat on
    /// <c>item.started</c> alone — half of every codex room's <c>mcp_tool_call</c> lines — so an audit
    /// reading the completed items (the ones that carry the outcome) could not tell two of them apart.
    /// <para>
    /// <b>Stamping the completed item counts nothing twice.</b> The two readers that tally are anchored
    /// on one lifecycle line each and stay there:
    /// <c>Status.CodexUsageParser.ToolInvocationKeys</c>, <c>ShellCommandLines</c> and
    /// <c>CountToolSteps</c> (via <c>TryParseToolName</c>) read <c>item.started</c> only;
    /// <c>CountRefusedToolSteps</c>/<c>CountEmptyToolResults</c> read <c>item.completed</c> only. A reader that stopped gating on the
    /// envelope's <c>type</c> would double every codex figure at once, which is what those methods'
    /// tests pin.
    /// </para>
    /// </summary>
    private static JsonObject Describe(string tool, string digest, string? identity)
    {
        var item = new JsonObject
        {
            ["type"] = "mcp_tool_call",
            ["tool"] = tool,
            // #1921: what makes "this lane re-issued a call it had already made" countable on codex.
            // Codex's own item.started names the tool and never its arguments, so without this the
            // repeat count has nothing to key on and CodexUsageParser.ToolInvocationKeys reports
            // none -- which is what it still does for a stream captured before this landed.
            //
            // A DIGEST rather than the arguments: a write_text call's arguments carry a whole file,
            // and a repeat count only ever asks two keys whether they are equal. Emitting the
            // arguments themselves would put a file's contents into the captured stream a second
            // time to answer a question 16 hex characters answer, and every byte of .stdout.log is
            // a byte the projector re-reads at settle and the rollover threshold counts.
            [CodexUsageParser.ArgumentsDigestField] = digest,
        };
        // #2008: absent rather than null when the tool has no identifying argument -- a null would read
        // as "this call had no target", which is a different claim from "this shape names none".
        // CodexDynamicToolPolicy.InputIdentity states which key each tool's is and what it excludes.
        if (identity is { Length: > 0 })
        {
            item[CodexUsageParser.ArgumentsIdentityField] = identity;
        }

        return item;
    }

    /// <summary>
    /// #1921: a short, stable fingerprint of one dynamic-tool call's arguments, as codex serialized
    /// them. Not a security boundary and not reversible-by-design — it exists only so two calls can be
    /// compared for equality without carrying their payloads through the stream. SHA-256 truncated to
    /// 16 hex characters: a collision would merge two distinct calls into one repeat, which costs an
    /// over-count of at most one on a diagnostic figure, and 64 bits makes that not worth the wider
    /// field.
    /// <para>
    /// The RAW text is hashed rather than a canonical re-serialization, so this fingerprint carries the
    /// same no-normalisation caveat <c>Status.ClaudeUsageParser.ToolInvocationKeys</c> states once for
    /// every vendor's key — that comment names what goes uncounted and why none of it is normalised.
    /// </para>
    /// </summary>
    /// <remarks>
    /// #2009 moved the construction itself to <see cref="GrantDecision.Identify"/>, which is where the
    /// same fingerprint is now taken of a hook's command line or path — one digest rule for every
    /// enforcement point, so an <c>item.started</c> and the grant line beside it agree by construction
    /// rather than by two copies of a hash.
    /// </remarks>
    private static string ArgumentsDigest(JsonElement arguments) =>
        GrantDecision.Identify(arguments.GetRawText());

    private static async Task EmitCompletedItemAsync(JsonObject message, TextWriter output)
    {
        var item = message["params"]?["item"];
        if (item?["type"]?.GetValue<string>() != "agentMessage")
        {
            return;
        }
        await EmitAsync(output, new JsonObject
        {
            ["type"] = "item.completed",
            ["item"] = new JsonObject
            {
                ["type"] = "agent_message",
                ["text"] = item["text"]?.GetValue<string>() ?? string.Empty,
            },
        }).ConfigureAwait(false);
    }

    private static async Task<int> EmitTerminalTurnAsync(
        JsonObject message, JsonObject? usage, int roundTrip, TextWriter output)
    {
        var turn = message["params"]?["turn"];
        var status = turn?["status"]?.GetValue<string>();
        if (status == "completed")
        {
            var terminal = new JsonObject { ["type"] = "turn.completed" };
            if (usage is not null)
            {
                terminal["usage"] = ToBatonUsage(usage, roundTrip);
            }
            await EmitAsync(output, terminal).ConfigureAwait(false);
            return 0;
        }

        await EmitAsync(output, new JsonObject
        {
            ["type"] = "turn.failed",
            ["error"] = turn?["error"]?.DeepClone() ?? new JsonObject
            {
                ["message"] = $"Codex turn ended with status '{status ?? "unknown"}'.",
            },
        }).ConfigureAwait(false);
        return 1;
    }

    /// <summary>
    /// One app-server <c>tokenUsage</c> object in the snake_case shape
    /// <see cref="CodexUsageParser"/> reads, tagged with the 1-based round-trip it reports. Shared by
    /// the per-round-trip <c>turn.usage</c> line and the terminal <c>turn.completed</c> so the two
    /// cannot drift into naming the same dimensions differently — and so the terminal restatement
    /// carries the SAME tag as the round-trip it restates, which is what makes it droppable from a Σ.
    /// </summary>
    private static JsonObject ToBatonUsage(JsonObject usage, int roundTrip) => new()
    {
        ["input_tokens"] = usage["inputTokens"]?.DeepClone(),
        ["cached_input_tokens"] = usage["cachedInputTokens"]?.DeepClone(),
        ["cache_write_input_tokens"] = usage["cacheWriteInputTokens"]?.DeepClone(),
        ["output_tokens"] = usage["outputTokens"]?.DeepClone(),
        ["reasoning_output_tokens"] = usage["reasoningOutputTokens"]?.DeepClone(),
        [CodexUsageParser.RoundTripField] = roundTrip,
    };

    private static async Task<JsonObject> ReadResponseAsync(
        TextReader reader, int expectedId, TextWriter error, CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (!TryParseObject(line, out var message))
            {
                await error.WriteLineAsync($"Ignored non-JSON app-server output: {line}").ConfigureAwait(false);
                continue;
            }
            if (message["id"]?.GetValue<int>() != expectedId)
            {
                continue;
            }
            if (message["error"] is { } responseError)
            {
                throw new InvalidOperationException(
                    responseError["message"]?.GetValue<string>() ?? $"Codex app-server request {expectedId} failed.");
            }
            return message;
        }
        throw new IOException($"Codex app-server closed stdout before response {expectedId}.");
    }

    private static async Task SendAsync(
        TextWriter writer, JsonObject message, CancellationToken cancellationToken)
    {
        await writer.WriteLineAsync(message.ToJsonString().AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EmitAsync(TextWriter writer, JsonObject message)
    {
        await writer.WriteLineAsync(message.ToJsonString()).ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
    }

    private static bool TryParseObject(string line, out JsonObject message)
    {
        message = null!;
        try
        {
            message = JsonNode.Parse(line)?.AsObject()!;
            return message is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task DrainStderrAsync(
        TextReader source, TextWriter target, CancellationToken cancellationToken)
    {
        while (await source.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            await target.WriteLineAsync(line).ConfigureAwait(false);
        }
    }
}
