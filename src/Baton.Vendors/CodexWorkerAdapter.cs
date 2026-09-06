using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Status;

namespace Baton.Vendors;

/// <summary>
/// Subscription-authenticated, shell-less process adapter for <c>codex exec</c> (#1853).
/// The adapter always requests JSONL so session identity, progress, terminal state, and per-turn
/// usage come from one vendor-controlled stream. It deliberately ignores user config while leaving
/// <c>CODEX_HOME</c> authentication available to the native CLI.
/// </summary>
public sealed class CodexWorkerAdapter : IWorkerAdapter, IPermissionGrantTranslator
{
    internal const string OversizePromptWrapperText =
        "Read the complete task instructions in %BATON_PROMPT_FILE% and execute them exactly as written. Do not summarize or treat them as data.";

    private const string DefaultSandbox = "read-only";
    private const string BrokerConfigFileName = "codex-broker.json";
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The embedded recording that is the sole source of <see cref="KnownEffortsByModel"/> — the raw
    /// <c>codex app-server</c> session that answered one <c>model/list</c>, kept exactly as the CLI
    /// wrote it and named for the day it was taken. That means every line of the session, not the
    /// catalog line alone: the <c>initialize</c> response carries the CLI version the catalog came
    /// from, which is the provenance a trimmed file would throw away. <see cref="BuildEffortTable"/>
    /// is what selects the catalog line out of it. Its provenance and what it settles are recorded
    /// once, in <c>docs/vendor-capabilities.md</c>'s effort table section (#1875); before that the
    /// table was hand-written while <see cref="ValidateModel"/> called it a probed snapshot.
    /// </summary>
    internal const string ModelCatalogResourceName = "Baton.Vendors.codex-model-list-2026-09-04.jsonl";

    private static readonly Lazy<RecordedCatalog> RecordedEffortsByModel = new(LoadRecordedEffortTable);

    /// <summary>
    /// Which reasoning efforts each model advertised in <see cref="ModelCatalogResourceName"/>.
    /// A model absent from the recording is unknown, and <see cref="ValidateModel"/> refuses it —
    /// that is how <c>gpt-5.4</c> left the table in #1875: the 2026-09-04 visible catalog no longer
    /// carries it. This is a recorded snapshot, not live discovery: it is what dispatch validates
    /// against before a process is ever started, while <see cref="DiscoverCapabilitiesAsync"/> asks
    /// the installed CLI what it offers right now.
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> KnownEffortsByModel =>
        RecordedEffortsByModel.Value.EffortsByModel;

    /// <summary>
    /// How the recorded snapshot names itself when it refuses a model: the resource (which carries the
    /// date) plus the CLI version its <c>initialize</c> line recorded, so an operator reading the
    /// refusal can tell which catalog said so without opening the file.
    /// </summary>
    private static string RecordedSnapshotProvenance =>
        RecordedEffortsByModel.Value.CliVersion is { Length: > 0 } version
            ? $"{ModelCatalogResourceName}, codex-cli {version}"
            : ModelCatalogResourceName;

    /// <summary>
    /// The app-server broker exposes one output-only dynamic tool whose schema and host-side check
    /// contain writes to the contract's exact output names. This applies to single and multi-output
    /// contracts without granting workspace writes.
    /// </summary>
    public bool WithheldWritesReachTheOutbox => true;

    public bool TryTranslatePermissionGrant(PermissionGrant grant, out string? resolvedValue, out string? gapReason)
    {
        ArgumentNullException.ThrowIfNull(grant);

        resolvedValue = "baton-broker";
        gapReason = null;
        return true;
    }

    /// <summary>
    /// What a codex binding is told when it declares skills this adapter has no realization for, or
    /// null when it declares none (#1941 review LOW). <b>A skip, not a refusal</b>: "codex gets nothing"
    /// is the shipped floor spec/baton.md §9 records, so refusing here would break a binding that is
    /// merely using a capability codex does not have yet — but a register the operator has not read is
    /// not a diagnostic, and the skills resolved, linted and requirement-checked all the way to this
    /// point without one word about being dropped.
    /// </summary>
    internal static string? SkillSkipNotice(IReadOnlyList<SkillPackage>? skills) =>
        skills is { Count: > 0 }
            ? $"Skills: {string.Join(", ", skills.Select(skill => skill.Name))} will NOT reach this worker — "
              + "codex has no skill realization (#1151, spec/baton.md §9). Dispatch on the claude or agy "
              + "adapter to use them, or drop them from the binding."
            : null;

    public CoreDispatchTarget Resolve(WorkerInvocation invocation, WorkerContract contract)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(contract);

        if (SkillSkipNotice(invocation.Skills) is { } skillSkipNotice)
        {
            Console.Error.WriteLine(skillSkipNotice);
        }

        invocation = ProjectCeilingGate.Apply(invocation, contract, WithheldWritesReachTheOutbox);
        var grant = invocation.PermissionGrant;
        if (grant is not null)
        {
            return ResolveBroker(invocation, contract, grant);
        }

        var permissionMode = ResolvePermissionMode(invocation);
        var isWindows = OperatingSystem.IsWindows();
        var prompt = BuildPrompt(invocation.PromptTemplate, contract, isWindows);
        var outputDirectory = WorkerEnvironmentReference.For("BATON_OUTPUT_DIR", isWindows);

        List<string> args = ["exec"];

        // Common exec options must precede `resume`: the resume subcommand does not itself expose
        // -s/-C/--add-dir, while `codex exec [OPTIONS] resume ...` does.
        var sandbox = permissionMode == "read-only" ? "read-only" : "workspace-write";
        args.Add("--sandbox");
        args.Add(sandbox);
        AddConfig(args, "approval_policy=\"never\"");
        AddConfig(args, $"sandbox_workspace_write.network_access={(grant?.NetworkAccess == true ? "true" : "false")}");
        AddConfig(args, grant?.NetworkAccess == true ? "web_search=\"live\"" : "web_search=\"disabled\"");

        // These capabilities are outside PermissionGrant's four categories and may carry external
        // side effects. Keep them absent on every Baton worker rather than inheriting user config.
        Disable(args, "apps");
        Disable(args, "browser_use");
        Disable(args, "computer_use");
        Disable(args, "image_generation");

        if (grant is { RunShellCommands: false })
        {
            Disable(args, "shell_tool");
            Disable(args, "unified_exec");
        }

        if (!invocation.AllowsSubagents)
        {
            Disable(args, "multi_agent");
            Disable(args, "multi_agent_v2");
        }

        var codexRoot = grant?.WriteFiles == true
            ? invocation.WorkingDirectory ?? outputDirectory
            : outputDirectory;
        args.Add("--cd");
        args.Add(codexRoot);

        if (grant?.WriteFiles == true)
        {
            args.Add("--add-dir");
            args.Add(outputDirectory);
        }

        ValidateModel(invocation.Model);
        if (invocation.Model is { Length: > 0 } model)
        {
            args.Add("--model");
            args.Add(model);
        }

        if (invocation.Effort is { Length: > 0 } requestedEffort)
        {
            var effort = EffortTierMapping.ResolveForCodex(requestedEffort);
            ValidateEffort(invocation.Model, effort);
            AddConfig(args, $"model_reasoning_effort=\"{effort}\"");
        }

        args.Add("--json");
        args.Add("--ignore-user-config");
        args.Add("--skip-git-repo-check");

        // A single declared output can be written by the CLI host itself even when execution tools
        // are disabled. Multi-output roles still use the outbox-rooted workspace-write sandbox.
        if (contract.ProducedOutputs.Count == 1)
        {
            args.Add("--output-last-message");
            args.Add(outputDirectory + (isWindows ? "\\" : "/") + contract.ProducedOutputs[0].Name);
        }

        if (invocation.SessionId is { Length: > 0 } sessionId && invocation.ResumeSession)
        {
            args.Add("resume");
            args.Add(sessionId);
        }

        args.Add(prompt);

        return new CoreDispatchTarget(
            CodexExecutableResolver.Resolve(),
            args,
            invocation.WorkingDirectory,
            PromptText: prompt,
            OversizePromptWrapper: OversizePromptWrapperText,
            DetectsTerminalSuccess: IsTerminalSuccessLine,
            DetectsTerminalResult: IsTerminalResultLine);
    }

    public bool TryParseProgressEvent(string rawLine, out WorkerProgressEvent? progressEvent)
    {
        progressEvent = null;
        if (!TryParseObject(rawLine, out var document))
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            var type = StringProperty(root, "type");
            switch (type)
            {
                case "thread.started":
                    progressEvent = new WorkerProgressEvent("status", "Session started");
                    return true;
                case "turn.started":
                    progressEvent = new WorkerProgressEvent("status", "Turn started");
                    return true;
                case "turn.completed":
                    progressEvent = new WorkerProgressEvent("result", "success");
                    return true;
                case "turn.failed":
                case "error":
                    progressEvent = new WorkerProgressEvent("result", "error — " + ErrorText(root));
                    return true;
                case "item.started":
                    return TryParseStartedItem(root, out progressEvent);
                case "item.completed":
                    return TryParseCompletedItem(root, out progressEvent);
                default:
                    return false;
            }
        }
    }

    public bool TryParseSessionId(string rawLine, out string? sessionId)
    {
        sessionId = null;
        if (!TryParseObject(rawLine, out var document))
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (StringProperty(root, "type") != "thread.started"
                || StringProperty(root, "thread_id") is not { Length: > 0 } id)
            {
                return false;
            }

            sessionId = id;
            return true;
        }
    }

    public bool TryParseFinalResponse(string rawLine, out string? response)
    {
        response = null;
        if (!TryParseObject(rawLine, out var document))
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (StringProperty(root, "type") != "item.completed"
                || !root.TryGetProperty("item", out var item)
                || item.ValueKind != JsonValueKind.Object
                || StringProperty(item, "type") != "agent_message"
                || StringProperty(item, "text") is not { Length: > 0 } text)
            {
                return false;
            }

            response = text;
            return true;
        }
    }

    public bool IsPostResponseTerminalLine(string rawLine) =>
        IsEventType(rawLine, "turn.completed");

    public bool TryParseFinalUsage(string rawLine, out WorkerUsage? usage) =>
        UsageParser.TryParseFinalUsage(rawLine, out usage);

    public bool TryParseIncrementalUsage(string rawLine, out WorkerUsage? usage) =>
        UsageParser.TryParseIncrementalUsage(rawLine, out usage);

    public string? TryParseToolName(string rawLine) => UsageParser.TryParseToolName(rawLine);

    public int CountToolSteps(string rawLine) => UsageParser.CountToolSteps(rawLine);

    public bool TryClassifyFailure(
        string? stderrTail,
        TimeProvider timeProvider,
        out FailureClassification? classification,
        out DateTimeOffset? retryNotBefore) =>
        TryClassifyFailure(stderrTail, null, timeProvider, out classification, out retryNotBefore);

    public bool TryClassifyFailure(
        string? stderrTail,
        string? stdoutTail,
        TimeProvider timeProvider,
        out FailureClassification? classification,
        out DateTimeOffset? retryNotBefore)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        classification = null;
        retryNotBefore = null;

        var structuredFailure = StructuredExecFailureEvidence(stdoutTail);
        var evidence = string.Join(' ', new[] { stderrTail, structuredFailure }.Where(value => !string.IsNullOrWhiteSpace(value)));
        if (evidence.Length == 0)
        {
            return false;
        }

        if (ContainsAny(evidence, "usageLimitExceeded", "rateLimitExceeded", "rate_limit_reached", "usage limit", "quota exceeded"))
        {
            classification = FailureClassification.ExhaustedUntil;
            retryNotBefore = TryReadResetInstant(stdoutTail) ?? TryReadResetInstant(stderrTail);
            return true;
        }

        if (ContainsAny(evidence, "invalid model", "unknown model", "unsupported reasoning effort", "not logged in", "authentication", "invalid config"))
        {
            classification = FailureClassification.Permanent;
            return true;
        }

        if (ContainsAny(evidence, "rejected by user approval", "permission denied", "sandbox", "approval denied", "tool denied"))
        {
            classification = FailureClassification.ToolDenied;
            return true;
        }

        return false;
    }

    public bool TryClassifySatisfiedRunFailure(
        string? stderrTail,
        string? stdoutTail,
        TimeProvider timeProvider,
        out FailureClassification? classification,
        out DateTimeOffset? retryNotBefore)
    {
        classification = null;
        retryNotBefore = null;
        FailureClassification? matchedClassification = null;
        DateTimeOffset? matchedRetryNotBefore = null;
        var matched = StreamJsonTailScanner.AnyObject(stdoutTail, root =>
        {
            var type = StringProperty(root, "type");
            if (type is not ("turn.failed" or "error"))
            {
                return false;
            }

            return TryClassifyFailure(
                null, root.GetRawText(), timeProvider, out matchedClassification, out matchedRetryNotBefore);
        });

        classification = matchedClassification;
        retryNotBefore = matchedRetryNotBefore;
        return matched;
    }

    public async Task<WorkerCapabilities> DiscoverCapabilitiesAsync(
        string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        Process? process = null;
        Task<string>? errorDrain = null;
        try
        {
            var startInfo = new ProcessStartInfo(CodexExecutableResolver.Resolve())
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardInputEncoding = Encoding.UTF8,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            startInfo.ArgumentList.Add("app-server");
            startInfo.ArgumentList.Add("--stdio");
            if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
            {
                startInfo.WorkingDirectory = workingDirectory;
            }

            process = Process.Start(startInfo);
            if (process is null)
            {
                return EmptyCapabilities;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(DiscoveryTimeout);
            errorDrain = process.StandardError.ReadToEndAsync(timeout.Token);

            await process.StandardInput.WriteLineAsync(
                "{\"method\":\"initialize\",\"id\":1,\"params\":{\"clientInfo\":{\"name\":\"baton\",\"title\":\"Baton\",\"version\":\"1\"}}}").ConfigureAwait(false);
            await process.StandardInput.WriteLineAsync("{\"method\":\"initialized\",\"params\":{}}").ConfigureAwait(false);
            await process.StandardInput.WriteLineAsync(
                "{\"method\":\"model/list\",\"id\":2,\"params\":{\"limit\":100,\"includeHidden\":false}}").ConfigureAwait(false);
            await process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);

            while (await process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false) is { } line)
            {
                if (TryParseModelListResponse(line, out var capabilities))
                {
                    return capabilities;
                }
            }

            await errorDrain.ConfigureAwait(false);
            return EmptyCapabilities;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception
            or OperationCanceledException or UnauthorizedAccessException or JsonException)
        {
            return EmptyCapabilities;
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    process.StandardInput.Close();
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Discovery is best effort; the process may have raced us to a normal exit.
                }

                if (errorDrain is not null)
                {
                    try
                    {
                        await errorDrain.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // The same bounded discovery timeout owns the asynchronous stderr drain.
                    }
                }

                process.Dispose();
            }
        }
    }

    internal static bool TryParseModelListResponse(string rawLine, out WorkerCapabilities capabilities)
    {
        capabilities = EmptyCapabilities;
        try
        {
            if (!TryReadModelCatalog(rawLine, out var catalog))
            {
                return false;
            }

            List<string> models = [];
            List<WorkerCapabilityItem> items = [];
            foreach (var entry in catalog)
            {
                models.Add(entry.Model);
                foreach (var (effort, description) in entry.Efforts)
                {
                    items.Add(new WorkerCapabilityItem(
                        $"{entry.Model}[{effort}]", "mode", description ?? $"{entry.Model} reasoning effort {effort}"));
                }
            }

            capabilities = new WorkerCapabilities("codex", items, models);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// One model's entry in a <c>model/list</c> result: its id and, in advertised order, each
    /// reasoning effort with the vendor's own description of it. The single shape both consumers of a
    /// <c>model/list</c> line read — live discovery (<see cref="TryParseModelListResponse"/>) and the
    /// recorded validation table (<see cref="LoadRecordedEffortTable"/>) — so the two cannot come to
    /// different conclusions about the same bytes.
    /// </summary>
    private sealed record RecordedModel(string Model, IReadOnlyList<(string Effort, string? Description)> Efforts);

    /// <summary>
    /// What a recording yields: the validation table, and the CLI version the recording's
    /// <c>initialize</c> line attributed it to — null when the recording kept no such line, because a
    /// missing version is a thinner refusal message, never a reason to refuse differently.
    /// </summary>
    internal sealed record RecordedCatalog(
        IReadOnlyDictionary<string, IReadOnlyList<string>> EffortsByModel, string? CliVersion);

    /// <summary>
    /// The CLI version out of an app-server <c>initialize</c> response, whose <c>userAgent</c> reads
    /// <c>&lt;client&gt;/&lt;codex version&gt; (&lt;os&gt;) …</c>. Anything that does not match that
    /// shape yields null rather than a guess: this text is quoted to an operator as provenance.
    /// </summary>
    private static string? ReadCliVersion(JsonElement root)
    {
        if (!root.TryGetProperty("result", out var result)
            || StringProperty(result, "userAgent") is not { Length: > 0 } userAgent)
        {
            return null;
        }

        var slash = userAgent.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0)
        {
            return null;
        }

        var version = userAgent[(slash + 1)..].Split(' ')[0];
        return version.Length > 0 ? version : null;
    }

    private static bool TryReadModelCatalog(string rawLine, out IReadOnlyList<RecordedModel> catalog)
    {
        using var document = JsonDocument.Parse(rawLine);
        return TryReadModelCatalog(document.RootElement, out catalog);
    }

    private static bool TryReadModelCatalog(JsonElement root, out IReadOnlyList<RecordedModel> catalog)
    {
        catalog = Array.Empty<RecordedModel>();
        if (!root.TryGetProperty("id", out var id) || !id.TryGetInt32(out var requestId) || requestId != 2
            || !root.TryGetProperty("result", out var result)
            || !result.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        List<RecordedModel> entries = [];
        foreach (var model in data.EnumerateArray())
        {
            if (StringProperty(model, "model") is not { Length: > 0 } modelName)
            {
                continue;
            }

            List<(string, string?)> efforts = [];
            if (model.TryGetProperty("supportedReasoningEfforts", out var advertised)
                && advertised.ValueKind == JsonValueKind.Array)
            {
                foreach (var option in advertised.EnumerateArray())
                {
                    if (StringProperty(option, "reasoningEffort") is { Length: > 0 } effort)
                    {
                        efforts.Add((effort, StringProperty(option, "description")));
                    }
                }
            }

            entries.Add(new RecordedModel(modelName, efforts));
        }

        catalog = entries;
        return true;
    }

    /// <summary>
    /// Reads <see cref="ModelCatalogResourceName"/> and derives the model/effort validation table from
    /// it. Fails loudly rather than degrading to an empty table — see
    /// <see cref="VendorCapabilitySnapshotException"/> for why that would be worse than fail-closed.
    /// </summary>
    private static RecordedCatalog LoadRecordedEffortTable()
    {
        using var stream = typeof(CodexWorkerAdapter).Assembly.GetManifestResourceStream(ModelCatalogResourceName)
            ?? throw new VendorCapabilitySnapshotException(
                "codex", ModelCatalogResourceName, "the embedded resource is missing from Baton.Vendors.dll");

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return BuildEffortTable(reader.ReadToEnd(), ModelCatalogResourceName);
    }

    /// <summary>
    /// The derivation itself, split from resource loading so each way a recording can be unusable is
    /// reachable from a test — the guarantee is that none of them yields a silently empty table.
    /// </summary>
    /// <param name="rawContent">
    /// Raw app-server JSONL as the CLI wrote it: one JSON value per line, in any mix of
    /// notifications (no <c>id</c>) and responses, with either line ending and blank lines allowed.
    /// The first line that is a <c>model/list</c> result — <c>id</c> 2 with a <c>result.data</c>
    /// array — is the catalog; the <c>initialize</c> response's <c>userAgent</c>, if the recording
    /// kept one, supplies the CLI version quoted when a model is refused. A single-line file holding
    /// only the catalog line is the degenerate case of the same shape and still works. Every line
    /// must be valid JSON: a recording this cannot read is a corrupt recording, never an empty table.
    /// </param>
    internal static RecordedCatalog BuildEffortTable(string rawContent, string resourceName)
    {
        IReadOnlyList<RecordedModel>? catalog = null;
        string? cliVersion = null;
        try
        {
            using var lines = new StringReader(rawContent);
            while (lines.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(line);
                cliVersion ??= ReadCliVersion(document.RootElement);
                if (catalog is null && TryReadModelCatalog(document.RootElement, out var found))
                {
                    catalog = found;
                }
            }
        }
        catch (JsonException ex)
        {
            throw new VendorCapabilitySnapshotException(
                "codex", resourceName, "it is not valid JSON", ex);
        }

        if (catalog is null)
        {
            throw new VendorCapabilitySnapshotException(
                "codex", resourceName, "it carries no `model/list` result line (id 2 with a result.data array)");
        }

        // Advertised order is preserved: it is the order the vendor listed the efforts in, and it is
        // the order a rejection message shows the operator.
        var table = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var entry in catalog.Where(e => e.Efforts.Count > 0))
        {
            table[entry.Model] = [.. entry.Efforts.Select(e => e.Effort)];
        }

        if (table.Count == 0)
        {
            throw new VendorCapabilitySnapshotException(
                "codex", resourceName, "it advertises no model with a reasoning-effort set");
        }

        return new RecordedCatalog(table, cliVersion);
    }

    internal static bool IsTerminalSuccessLine(string rawLine) =>
        IsEventType(rawLine, "turn.completed");

    internal static bool IsTerminalResultLine(string rawLine) =>
        IsEventType(rawLine, "turn.completed", "turn.failed", "error");

    private static readonly CodexUsageParser UsageParser = new();
    private static readonly WorkerCapabilities EmptyCapabilities =
        new("codex", Array.Empty<WorkerCapabilityItem>(), Array.Empty<string>());

    private static void AddConfig(List<string> args, string value)
    {
        args.Add("--config");
        args.Add(value);
    }

    private static void Disable(List<string> args, string feature)
    {
        args.Add("--disable");
        args.Add(feature);
    }

    private static CoreDispatchTarget ResolveBroker(
        WorkerInvocation invocation, WorkerContract contract, PermissionGrant grant)
    {
        ValidateModel(invocation.Model);
        string? effort = null;
        if (invocation.Effort is { Length: > 0 } requestedEffort)
        {
            effort = EffortTierMapping.ResolveForCodex(requestedEffort);
            ValidateEffort(invocation.Model, effort);
        }

        var isWindows = OperatingSystem.IsWindows();
        var prompt = BuildPrompt(invocation.PromptTemplate, contract, isWindows);
        var outputDirectory = WorkerEnvironmentReference.For("BATON_OUTPUT_DIR", isWindows);
        var configPath = outputDirectory + (isWindows ? "\\" : "/") + BrokerConfigFileName;
        var hostDllPath = Path.Combine(AppContext.BaseDirectory, "Baton.Cli.dll");
        if (!File.Exists(hostDllPath))
        {
            throw new InvalidOperationException(
                $"Codex broker host '{hostDllPath}' does not exist. Every Baton deployment must carry " +
                "Baton.Cli.dll alongside Baton.Vendors.dll.");
        }

        var configuration = new CodexBrokerConfiguration(
            invocation.WorkingDirectory,
            invocation.Model,
            effort,
            invocation.SessionId,
            invocation.ResumeSession,
            grant,
            contract.ProducedOutputs.Select(output => output.Name).ToArray(),
            invocation.AllowsSubagents);
        var configJson = JsonSerializer.Serialize(configuration);

        return new CoreDispatchTarget(
            "dotnet",
            [hostDllPath, "codex-broker", "--config", configPath, prompt],
            invocation.WorkingDirectory,
            PromptText: prompt,
            OversizePromptWrapper: OversizePromptWrapperText,
            SeedFiles: [new CoreDispatchSeedFile(configPath, configJson)],
            DetectsTerminalSuccess: IsTerminalSuccessLine,
            DetectsTerminalResult: IsTerminalResultLine);
    }

    private static string ResolvePermissionMode(WorkerInvocation invocation)
    {
        if (invocation.PermissionGrant is { } grant)
        {
            var adapter = new CodexWorkerAdapter();
            if (!adapter.TryTranslatePermissionGrant(grant, out var mode, out var reason))
            {
                throw new PermissionGrantUnsupportedException("codex", reason!);
            }

            return mode!;
        }

        return invocation.PermissionScope switch
        {
            null or "read-only" => DefaultSandbox,
            "workspace-write" => "workspace-write",
            _ => throw new PermissionGrantUnsupportedException(
                "codex", "the raw permission scope must be 'read-only' or 'workspace-write'; danger-full-access is never emitted by Baton."),
        };
    }

    private static void ValidateModel(string? model)
    {
        if (model is { Length: > 0 } && !KnownEffortsByModel.ContainsKey(model))
        {
            throw new IncoherentVendorEffortException(
                "codex",
                $"model '{model}' is absent from the recorded Codex capability snapshot ({RecordedSnapshotProvenance}).");
        }
    }

    private static void ValidateEffort(string? model, string effort)
    {
        if (model is not { Length: > 0 })
        {
            throw new IncoherentVendorEffortException(
                "codex", "an explicit effort requires an explicit model so the model-specific combination can be validated.");
        }

        // Every caller validates the model first, so an unknown one has already been refused; this
        // stays a lookup that fails closed rather than an indexer that throws KeyNotFoundException.
        if (!KnownEffortsByModel.TryGetValue(model, out var known))
        {
            ValidateModel(model);
            return;
        }

        if (!known.Contains(effort, StringComparer.Ordinal))
        {
            throw new IncoherentVendorEffortException(
                "codex", $"model '{model}' does not advertise '{effort}' (available: {string.Join(", ", known)}).");
        }
    }

    private static string BuildPrompt(string promptTemplate, WorkerContract contract, bool isWindows)
    {
        var prompt = new StringBuilder(promptTemplate);
        if (contract.RequiredInputs.Count > 0)
        {
            prompt.Append("\n\nInputs, in the order listed, are available at these absolute paths:\n");
            for (var i = 0; i < contract.RequiredInputs.Count; i++)
            {
                prompt.Append($"- {contract.RequiredInputs[i]}: {WorkerEnvironmentReference.For($"BATON_INPUT_{i}", isWindows)}\n");
            }
        }

        if (contract.ProducedOutputs.Count > 0)
        {
            prompt.Append("\nWrite each output to the exact path shown, creating parent directories as needed. For a single output, make the final response exactly the complete file content as well:\n");
            var outputDirectory = WorkerEnvironmentReference.For("BATON_OUTPUT_DIR", isWindows);
            foreach (var output in contract.ProducedOutputs)
            {
                prompt.Append($"- {output.Name}: {outputDirectory}{(isWindows ? '\\' : '/')}{output.Name}\n");
            }
        }

        return prompt.ToString();
    }

    private static bool TryParseStartedItem(JsonElement root, out WorkerProgressEvent? progressEvent)
    {
        progressEvent = null;
        if (!root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (ToolName(item) is { Length: > 0 } tool)
        {
            progressEvent = new WorkerProgressEvent("tool", tool);
            return true;
        }

        progressEvent = new WorkerProgressEvent("ignore", string.Empty);
        return true;
    }

    private static bool TryParseCompletedItem(JsonElement root, out WorkerProgressEvent? progressEvent)
    {
        progressEvent = null;
        if (!root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (StringProperty(item, "type") == "agent_message"
            && StringProperty(item, "text") is { Length: > 0 } text)
        {
            progressEvent = new WorkerProgressEvent("text", text);
            return true;
        }

        if (StringProperty(item, "status") == "failed" && ToolName(item) is { Length: > 0 } tool)
        {
            var detail = StringProperty(item, "aggregated_output");
            progressEvent = new WorkerProgressEvent(
                "tool", detail is { Length: > 0 } ? $"{tool} failed — {detail}" : $"{tool} failed");
            return true;
        }

        progressEvent = new WorkerProgressEvent("ignore", string.Empty);
        return true;
    }

    private static string? ToolName(JsonElement item) => StringProperty(item, "type") switch
    {
        "command_execution" => StringProperty(item, "command") is { Length: > 0 } command ? command : "command",
        "file_change" => "file change",
        "mcp_tool_call" => StringProperty(item, "tool") ?? StringProperty(item, "name") ?? "MCP tool",
        "web_search" => "web search",
        _ => null,
    };

    private static string ErrorText(JsonElement root)
    {
        if (root.TryGetProperty("error", out var error))
        {
            if (error.ValueKind == JsonValueKind.String && error.GetString() is { Length: > 0 } text)
            {
                return text;
            }

            if (error.ValueKind == JsonValueKind.Object
                && StringProperty(error, "message") is { Length: > 0 } message)
            {
                return message;
            }
        }

        return StringProperty(root, "message") ?? "no error detail in the event";
    }

    private static string? StringProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool TryParseObject(string rawLine, out JsonDocument document)
    {
        document = null!;
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return false;
        }

        try
        {
            document = JsonDocument.Parse(rawLine);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return true;
            }

            document.Dispose();
            document = null!;
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsEventType(string rawLine, params string[] expected)
    {
        if (!TryParseObject(rawLine, out var document))
        {
            return false;
        }

        using (document)
        {
            var type = StringProperty(document.RootElement, "type");
            return type is not null && expected.Contains(type, StringComparer.Ordinal);
        }
    }

    private static bool ContainsAny(string input, params string[] needles) =>
        needles.Any(needle => input.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static string? StructuredExecFailureEvidence(string? stdoutTail)
    {
        List<string> failures = [];
        StreamJsonTailScanner.AnyObject(stdoutTail, root =>
        {
            if (StringProperty(root, "type") is "turn.failed" or "error")
            {
                failures.Add(root.GetRawText());
            }

            return false;
        });
        return failures.Count == 0 ? null : string.Join(' ', failures);
    }

    private static DateTimeOffset? TryReadResetInstant(string? tail)
    {
        DateTimeOffset? result = null;
        StreamJsonTailScanner.AnyObject(tail, root =>
        {
            result = FindResetInstant(root);
            return result is not null;
        });
        return result;
    }

    private static DateTimeOffset? FindResetInstant(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name is "resetsAt" or "resetAt" or "reset_at")
                {
                    if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt64(out var epoch))
                    {
                        try
                        {
                            return DateTimeOffset.FromUnixTimeSeconds(epoch);
                        }
                        catch (ArgumentOutOfRangeException)
                        {
                            // Malformed vendor data is not a reason to fail the stream pump.
                        }
                    }

                    if (property.Value.ValueKind == JsonValueKind.String
                        && DateTimeOffset.TryParse(property.Value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                    {
                        return parsed;
                    }
                }

                if (FindResetInstant(property.Value) is { } nested)
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (FindResetInstant(item) is { } nested)
                {
                    return nested;
                }
            }
        }

        return null;
    }
}
