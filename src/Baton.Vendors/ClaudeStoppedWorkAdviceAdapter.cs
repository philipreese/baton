using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Conductor;
using Microsoft.Win32;

namespace Baton.Vendors;

/// <summary>One bounded subscription CLI call for stopped-work advice, without action authority.</summary>
public sealed class ClaudeStoppedWorkAdviceAdapter
{
    public const string SupportedCliVersion = "2.1.283";
    public const int MaxAnswerBytes = 64 * 1024;
    public const int MaxStreamBytes = 1024 * 1024;
    public const int MaxTurns = 2;
    public const decimal MaxApiEquivalentCostUsd = 0.20m;
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(70);
    internal const string IsolationSettings = """
        {"pluginConfigs":{"agents-md@builtin":{"options":{"instructionFiles":"managed-only"}}},"autoMemoryEnabled":false}
        """;
    private readonly string _executable;
    private readonly TimeSpan _deadline;
    private readonly Func<FileStream, CancellationToken, Task>? _captureObserver;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    public ClaudeStoppedWorkAdviceAdapter() : this("claude", Deadline) { }

    // A direct inert executable replaces the CLI in transport tests; all preflight checks still run.
    internal ClaudeStoppedWorkAdviceAdapter(string executable, TimeSpan deadline,
        Func<FileStream, CancellationToken, Task>? captureObserver = null)
    {
        _executable = executable;
        _deadline = deadline;
        _captureObserver = captureObserver;
    }

    public async Task PreflightAsync(CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var working = AdviceWorkingDirectory.Create();
        await PreflightCoreAsync(working.Path, deadline.Token).ConfigureAwait(false);
    }

    private async Task PreflightCoreAsync(string workingDirectory, CancellationToken token)
    {
        ValidateIsolationSettings(IsolationSettings);
        ValidateWorkingDirectory(workingDirectory);
        RefuseUnverifiedManagedPolicy();
        var version = await RunFreeCommandAsync(workingDirectory, ["--version"], token).ConfigureAwait(false);
        if (version.Trim() != SupportedCliVersion + " (Claude Code)")
            throw new InvalidOperationException("Claude stopped-work advice requires the tested CLI version.");
        var help = await RunFreeCommandAsync(workingDirectory, ["--help"], token).ConfigureAwait(false);
        foreach (var flag in new[] { "--tools", "--strict-mcp-config", "--mcp-config", "--setting-sources",
            "--disable-slash-commands", "--settings", "--session-id", "--max-budget-usd",
            "--output-format", "--verbose", "--model", "--effort" })
            if (!help.Contains(flag, StringComparison.Ordinal))
                throw new InvalidOperationException("Claude stopped-work advice CLI flags are unavailable.");
        // --max-turns is hidden from 2.1.283 help. Its acceptance was measured in the
        // October 1 bounded advice invocation (#2540), not inferred for future versions.
        var auth = await RunFreeCommandAsync(workingDirectory, ["auth", "status"], token).ConfigureAwait(false);
        using var status = JsonDocument.Parse(auth);
        RequireUniqueProperties(status.RootElement);
        if (!status.RootElement.TryGetProperty("loggedIn", out var loggedIn) || loggedIn.ValueKind != JsonValueKind.True
            || !status.RootElement.TryGetProperty("authMethod", out var method) || method.GetString() != "claude.ai")
            throw new InvalidOperationException("Claude stopped-work advice requires an existing subscription login.");
    }

    public async Task<RetainedStoppedWorkAdviceResponse> DecideAsync(StoppedWorkAdviceRequest request,
        StoppedWorkAdviceContext context, string evidenceDirectory, CancellationToken cancellationToken = default)
    {
        var prompt = CodexReadinessDecisionAdapter.BuildStoppedWorkPrompt(request, context)
            + "\nReturn exactly one JSON object, with no prose, containing only obligationId, repository, tag, "
            + "attemptId, contextSha256, choice (hold or recommend), and explanation (1 to 4096 characters).";
        if (Encoding.UTF8.GetByteCount(prompt) > 24 * 1024)
            throw new InvalidOperationException("Claude stopped-work prompt exceeds the process argument limit.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_deadline);
        using var working = AdviceWorkingDirectory.Create();
        await PreflightCoreAsync(working.Path, deadline.Token).ConfigureAwait(false);
        Directory.CreateDirectory(evidenceDirectory);
        await using var stdout = new FileStream(System.IO.Path.Combine(evidenceDirectory, "claude.stdout.jsonl"),
            FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.WriteThrough);
        await using var stderr = new FileStream(System.IO.Path.Combine(evidenceDirectory, "claude.stderr.txt"),
            FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.WriteThrough);
        var session = Guid.NewGuid().ToString();
        using var child = Start(working.Path, BuildArguments(prompt, session));
        var output = CaptureAsync(child.StandardOutput.BaseStream, stdout, child, deadline.Token);
        var errors = CaptureAsync(child.StandardError.BaseStream, stderr, child, deadline.Token);
        try
        {
            await child.Process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            var bytes = await output.ConfigureAwait(false);
            _ = await errors.ConfigureAwait(false);
            if (child.Process.ExitCode != 0) throw new InvalidOperationException("Claude stopped-work advice exited unsuccessfully.");
            return ParseEvents(Utf8.GetString(bytes), session, request);
        }
        catch (Exception failure)
        {
            await TerminateAndDrainAsync(child, [output, errors], failure).ConfigureAwait(false);
            throw;
        }
    }

    internal static string[] BuildArguments(string prompt, string session) =>
    [
        "-p", prompt, "--tools", "", "--strict-mcp-config", "--mcp-config", "{\"mcpServers\":{}}",
        "--setting-sources", "", "--disable-slash-commands", "--settings", IsolationSettings,
        "--output-format", "stream-json", "--verbose", "--model", StoppedWorkAdviceProviderDescriptor.Claude.Model,
        "--effort", StoppedWorkAdviceProviderDescriptor.Claude.Effort,
        "--max-turns", "2", "--max-budget-usd", "0.20", "--session-id", session,
    ];

    private ChildProcessTree Start(string workingDirectory, IEnumerable<string> arguments) =>
        ChildProcessTree.Start(_executable, info =>
        {
            info.WorkingDirectory = workingDirectory;
            // Names only: never inspect or copy credential values. Keep config roots/user home.
            string[] names = ["PATH", "SYSTEMROOT", "WINDIR", "SYSTEMDRIVE", "COMSPEC", "PATHEXT", "TEMP", "TMP",
                "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "APPDATA", "LOCALAPPDATA", "PROGRAMDATA",
                "PROGRAMFILES", "PROGRAMFILES(X86)", "PROGRAMW6432", "CLAUDE_CONFIG_DIR"];
            foreach (var name in info.Environment.Keys.ToArray())
                if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) info.Environment.Remove(name);
            info.Environment[ClaudeWorkerAdapter.SimpleModeVariable] = "0";
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
        });

    private async Task<string> RunFreeCommandAsync(string workingDirectory, string[] arguments, CancellationToken token)
    {
        using var child = Start(workingDirectory, arguments);
        var stdout = CaptureAsync(child.StandardOutput.BaseStream, null, child, token);
        var stderr = CaptureAsync(child.StandardError.BaseStream, null, child, token);
        try
        {
            await child.Process.WaitForExitAsync(token).ConfigureAwait(false);
            var bytes = await stdout.ConfigureAwait(false);
            _ = await stderr.ConfigureAwait(false);
            if (child.Process.ExitCode != 0) throw new InvalidOperationException("Claude stopped-work advice preflight failed.");
            return Utf8.GetString(bytes);
        }
        catch (Exception failure)
        {
            await TerminateAndDrainAsync(child, [stdout, stderr], failure).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<byte[]> CaptureAsync(Stream stream, FileStream? evidence, ChildProcessTree child, CancellationToken token)
    {
        using var bytes = new MemoryStream();
        var buffer = new byte[64 * 1024];
        try
        {
            int count;
            while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                var remaining = MaxStreamBytes - checked((int)bytes.Length);
                var retained = Math.Min(count, remaining);
                if (retained > 0)
                {
                    bytes.Write(buffer, 0, retained);
                    if (evidence is not null)
                    {
                        await evidence.WriteAsync(buffer.AsMemory(0, retained), token).ConfigureAwait(false);
                        if (_captureObserver is not null) await _captureObserver(evidence, token).ConfigureAwait(false);
                    }
                }
                if (count > remaining) throw new InvalidOperationException("Claude output stream exceeded 1 MiB.");
            }
            if (evidence is not null)
            {
                await evidence.FlushAsync(token).ConfigureAwait(false);
                evidence.Flush(flushToDisk: true);
            }
            return bytes.ToArray();
        }
        catch
        {
            child.Terminate();
            throw;
        }
    }

    private static async Task TerminateAndDrainAsync(ChildProcessTree child, Task[] captures, Exception failure)
    {
        child.Terminate();
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await child.Process.WaitForExitAsync(cleanup.Token).ConfigureAwait(false);
            try { await Task.WhenAll(captures).WaitAsync(cleanup.Token).ConfigureAwait(false); }
            catch (Exception) when (captures.All(task => task.IsCompleted))
            {
                // Settled capture faults are secondary; the initiating failure is rethrown.
            }
        }
        catch (OperationCanceledException) when (cleanup.IsCancellationRequested)
        {
            throw new InvalidOperationException("Claude child or evidence capture did not settle within the cleanup bound.", failure);
        }
    }

    internal static RetainedStoppedWorkAdviceResponse ParseEvents(string stream, string session,
        StoppedWorkAdviceRequest request)
    {
        if (string.IsNullOrWhiteSpace(session) || Encoding.UTF8.GetByteCount(stream) > MaxStreamBytes)
            throw new InvalidOperationException("Claude session or native stream is invalid.");
        var initialized = false;
        JsonElement? terminal = null;
        foreach (var line in stream.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            RequireUniqueProperties(root);
            RefuseToolEvents(root);
            var type = root.GetProperty("type").GetString();
            if (terminal is not null) throw new InvalidOperationException("Claude emitted events after its terminal result.");
            if (type == "system" && root.GetProperty("subtype").GetString() == "init")
            {
                if (initialized || root.GetProperty("session_id").GetString() != session
                    || root.GetProperty("model").GetString() != StoppedWorkAdviceProviderDescriptor.Claude.Model
                    || root.GetProperty("tools").ValueKind != JsonValueKind.Array || root.GetProperty("tools").GetArrayLength() != 0
                    || root.GetProperty("mcp_servers").ValueKind != JsonValueKind.Array || root.GetProperty("mcp_servers").GetArrayLength() != 0)
                    throw new InvalidOperationException("Claude init identity or offered tools/MCP is invalid.");
                if (!root.TryGetProperty("skills", out var skills) || skills.ValueKind != JsonValueKind.Array || skills.GetArrayLength() != 0
                    || !root.TryGetProperty("slash_commands", out var commands) || commands.ValueKind != JsonValueKind.Array || commands.GetArrayLength() != 0)
                    throw new InvalidOperationException("Claude offered skills or slash commands.");
                initialized = true;
            }
            else if (type == "result")
            {
                if (!initialized || root.GetProperty("session_id").GetString() != session
                    || root.GetProperty("subtype").GetString() != "success"
                    || root.GetProperty("is_error").ValueKind != JsonValueKind.False)
                    throw new InvalidOperationException("Claude terminal result failed or session identity drifted.");
                terminal = root.Clone();
            }
            else if (initialized && type == "assistant")
            {
                if (!root.TryGetProperty("message", out var message)
                    || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException("Claude assistant event is malformed.");
                foreach (var block in content.EnumerateArray())
                    if (!block.TryGetProperty("type", out var blockType)
                        || blockType.GetString() is not ("text" or "thinking" or "redacted_thinking"))
                        throw new InvalidOperationException("Claude assistant emitted tool or unknown content.");
            }
            else throw new InvalidOperationException("Claude emitted an unexpected native event.");
        }
        if (terminal is not { } result) throw new InvalidOperationException("Claude emitted no successful terminal result.");
        var decision = ParseAnswer(result.GetProperty("result").GetString()!, request);
        StoppedWorkAdviceUsage? usage = null;
        if (result.TryGetProperty("usage", out var nativeUsage))
            usage = new(Token(nativeUsage, "input_tokens"), Token(nativeUsage, "output_tokens"),
                Token(nativeUsage, "cache_read_input_tokens"), Token(nativeUsage, "cache_creation_input_tokens"));
        if (result.TryGetProperty("total_cost_usd", out var cost))
        {
            if (!cost.TryGetDecimal(out var amount) || amount < 0 || amount > MaxApiEquivalentCostUsd)
                throw new InvalidOperationException("Claude API-equivalent cost exceeded its bound or is invalid.");
            usage = (usage ?? new(null, null, null)) with { ApiEquivalentCostUsd = amount };
        }
        if (result.TryGetProperty("num_turns", out var turns) && (!turns.TryGetInt32(out var count) || count < 1 || count > MaxTurns))
            throw new InvalidOperationException("Claude turn count exceeded its bound or is invalid.");
        if (result.TryGetProperty("modelUsage", out var modelUsage))
        {
            RequireUniqueProperties(modelUsage);
            foreach (var model in modelUsage.EnumerateObject())
                if (model.Name != StoppedWorkAdviceProviderDescriptor.Claude.Model)
                    throw new InvalidOperationException("Claude reported unexpected model usage.");
        }
        var provider = StoppedWorkAdviceProviderDescriptor.Claude;
        return new(decision, provider.Adapter, provider.Model, provider.Effort, DateTimeOffset.UtcNow, usage);
    }

    internal static StoppedWorkAdviceDecision ParseAnswer(string answer, StoppedWorkAdviceRequest request)
    {
        if (answer is null || Encoding.UTF8.GetByteCount(answer) > MaxAnswerBytes)
            throw new InvalidOperationException("Claude typed answer exceeded 64 KiB or is absent.");
        var text = answer.Trim();
        if (text.StartsWith("```json\n", StringComparison.Ordinal) || text.StartsWith("```json\r\n", StringComparison.Ordinal))
        {
            if (!text.EndsWith("\n```", StringComparison.Ordinal)) throw new JsonException("Incomplete JSON fence.");
            text = text[(text.IndexOf('\n') + 1)..^4].Trim();
        }
        using var document = JsonDocument.Parse(text);
        RequireUniqueProperties(document.RootElement);
        // Case-sensitive names and one object: the typed record rejects missing/extra keys.
        var names = new[] { "obligationId", "repository", "tag", "attemptId", "contextSha256", "choice", "explanation" };
        if (document.RootElement.EnumerateObject().Any(property => !names.Contains(property.Name, StringComparer.Ordinal)))
            throw new JsonException("Unexpected stopped-work answer property.");
        if (!document.RootElement.TryGetProperty("choice", out var choice)
            || choice.ValueKind != JsonValueKind.String || choice.GetString() is not ("hold" or "recommend"))
            throw new JsonException("Invalid stopped-work advice choice.");
        var decision = JsonSerializer.Deserialize<StoppedWorkAdviceDecision>(text, Json)
            ?? throw new JsonException("Null stopped-work answer.");
        if (decision.ObligationId != request.ObligationId || decision.Repository != request.Repository
            || decision.Tag != request.Tag || decision.AttemptId != request.AttemptId.Value
            || decision.ContextSha256 != request.ContextSha256 || !Enum.IsDefined(decision.Choice)
            || string.IsNullOrWhiteSpace(decision.Explanation) || decision.Explanation.Length > 4096)
            throw new InvalidOperationException("Claude stopped-work answer identity or schema is invalid.");
        return decision;
    }

    private static long? Token(JsonElement usage, string name) =>
        usage.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var count) && count >= 0 ? count : null;

    private static void RefuseToolEvents(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            RequireUniqueProperties(value);
            foreach (var property in value.EnumerateObject())
            {
                if (property.Name is "tool_use" or "tool_use_result"
                    || property.Name == "type" && property.Value.ValueKind == JsonValueKind.String
                        && property.Value.GetString() is "tool_use" or "tool_result" or "mcp_tool_use" or "server_tool_use")
                    throw new InvalidOperationException("Claude emitted a tool-use event.");
                RefuseToolEvents(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RefuseToolEvents(item);
    }

    private static void RequireUniqueProperties(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("Expected object.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject())
            if (!names.Add(property.Name)) throw new JsonException("Duplicate JSON property.");
    }

    internal static void ValidateIsolationSettings(string settings)
    {
        using var document = JsonDocument.Parse(settings);
        var root = document.RootElement;
        RequireUniqueProperties(root);
        if (root.GetProperty("autoMemoryEnabled").ValueKind != JsonValueKind.False
            || root.GetProperty("pluginConfigs").GetProperty("agents-md@builtin").GetProperty("options")
                .GetProperty("instructionFiles").GetString() != "managed-only")
            throw new InvalidOperationException("Claude invocation-local instruction isolation is invalid.");
    }

    internal static void ValidateWorkingDirectory(string path)
    {
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        for (var directory = new DirectoryInfo(System.IO.Path.GetFullPath(path)); directory is not null; directory = directory.Parent)
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Claude advice working directory has a redirected ancestor.");
            foreach (var name in new[] { "CLAUDE.md", "CLAUDE.local.md", "AGENTS.md" })
                if (File.Exists(System.IO.Path.Combine(directory.FullName, name)))
                    throw new InvalidOperationException("Claude advice working directory inherits project instructions.");
            // The exact vendor user-root directory is a user instruction source, not project
            // discovery. Preserve it; managed-only must exclude it (native proof is separate).
            if (string.Equals(directory.FullName, userHome, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var name in new[] { ".claude/CLAUDE.md", ".claude/CLAUDE.local.md" })
                if (File.Exists(System.IO.Path.Combine(directory.FullName, name)))
                    throw new InvalidOperationException("Claude advice working directory inherits project instructions.");
            if (Directory.Exists(System.IO.Path.Combine(directory.FullName, ".claude", "rules")))
                throw new InvalidOperationException("Claude advice working directory inherits project rules.");
        }
    }

    private static void RefuseUnverifiedManagedPolicy()
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("Claude stopped-work advice currently requires the tested Windows host contract.");
        var managed = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ClaudeCode");
        if (File.Exists(System.IO.Path.Combine(managed, "managed-settings.json"))
            || Directory.Exists(System.IO.Path.Combine(managed, "managed-settings.d")))
            throw new InvalidOperationException("Claude managed policy compatibility requires operator verification before advice admission.");
        using var machine = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\ClaudeCode");
        using var user = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Policies\ClaudeCode");
        if (machine is not null || user is not null)
            throw new InvalidOperationException("Claude managed policy compatibility requires operator verification before advice admission.");
    }

    private sealed class AdviceWorkingDirectory : IDisposable
    {
        public string Path { get; }
        private AdviceWorkingDirectory(string path) => Path = path;
        public static AdviceWorkingDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "baton-claude-advice-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new(path);
        }
        public void Dispose() => Directory.Delete(Path, recursive: false);
    }
}
