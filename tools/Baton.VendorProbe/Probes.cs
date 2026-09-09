using System.Text.Json;
using System.Text.RegularExpressions;
using Baton.Vendors;

namespace Baton.VendorProbe;

/// <summary>
/// The capability checks. Each one names the surfaces it consulted, so an incomplete probe is
/// visibly incomplete rather than silently reported as an absence.
/// </summary>
public static class Probes
{
    public static IReadOnlyList<Finding> RunAll(string vendor)
    {
        var program = ProgramName(vendor);
        var version = Cli.Version(program);
        if (version is null)
        {
            return [Finding.Absent(
                "the CLI itself", vendor, [Surfaces.Help],
                $"'{vendor} --version' did not succeed — the CLI is not installed or not on PATH. "
                + "Every other row for this vendor is unknown, not absent.", null)];
        }

        if (IsCodex(vendor))
        {
            return RunCodex(vendor, program, version);
        }

        // Established once and shared: what this CLI does with a flag it has never heard of. Without
        // it, an exit code means nothing, and reading an exit code as though it meant something is
        // how --permission-prompt-tool came to be recorded as absent.
        var baseline = FlagProbe.Baseline(vendor);

        return
        [
            PlanUsage(vendor, version),
            PerTurnCost(vendor, version),
            StructuredOutput(vendor, version),
            PermissionPromptTool(vendor, version, baseline),
            Effort(vendor, version),
            AddDir(vendor, version),
        ];
    }

    private static string Help(string vendor) => Cli.Invoke(vendor, ["--help"], TimeSpan.FromSeconds(45)).All;

    /// <summary>
    /// The row that was wrong twice. It is absent from <c>--help</c> and from the subcommand list on
    /// both vendors, and on <c>claude</c> it works perfectly as a slash command — which is why the
    /// slash surface is probed here rather than inferred from the other two.
    /// </summary>
    private static Finding PlanUsage(string vendor, string version)
    {
        const string cap = "plan usage & reset";
        string[] surfaces = [Surfaces.Help, Surfaces.Subcommands, Surfaces.SlashCommand];
        var help = Help(vendor);

        // Ask the CLI directly. No shell, so a leading slash survives intact.
        var run = Cli.Invoke(vendor, ["-p", "/usage"], TimeSpan.FromMinutes(2));
        var text = run.All;

        // A real usage report states a percentage against a window. Prose *about* usage does not —
        // and on one vendor the model answers conversationally, which must not read as a capability.
        var percent = Regex.Match(text, @"(\d{1,3})%\s*used", RegexOptions.IgnoreCase);
        var reset = Regex.Match(text, @"resets?\s+([^\r\n·]+)", RegexOptions.IgnoreCase);

        if (percent.Success)
        {
            var detail = $"`{vendor} -p \"/usage\"` reported a live percentage"
                + (reset.Success ? $" and a reset instant (\"{reset.Groups[1].Value.Trim()}\")" : ", but no reset instant")
                + ". Note the vendor's own caveat where present: the figure may be machine-local.";
            return Finding.Seen(cap, vendor, $"`/usage` — {percent.Groups[1].Value}% used"
                + (reset.Success ? ", with reset times" : ", no reset time"), surfaces, detail, version);
        }

        return Finding.Absent(cap, vendor, surfaces,
            $"`--help` carries no usage/quota flag, no such subcommand exists, and `{vendor} -p \"/usage\"` "
            + "produced no percentage — the model answered conversationally rather than the CLI reporting. "
            + $"Help mentioned 'usage' {Regex.Matches(help, "usage", RegexOptions.IgnoreCase).Count} time(s), all of them the synopsis line.",
            version);
    }

    private static bool IsAgy(string vendor) => string.Equals(vendor, "agy", StringComparison.OrdinalIgnoreCase);
    private static bool IsCodex(string vendor) => string.Equals(vendor, "codex", StringComparison.OrdinalIgnoreCase);
    internal static string ProgramName(string vendor) =>
        IsCodex(vendor) ? CodexExecutableResolver.Resolve() : vendor;

    /// <summary>
    /// The stream-json invocation for THIS vendor. The two CLIs disagree on the grammar, and passing
    /// claude's argv to agy is what recorded agy's structured output as absent across three versions
    /// (#1088): agy's <c>-p</c> is <b>flag-value</b> (#491) — the prompt is the value of <c>-p</c>, so
    /// <c>["-p", "--output-format", …]</c> makes agy read <c>--output-format</c> as the prompt and
    /// stream-json never engages — and agy <b>rejects</b> claude's <c>--verbose</c> (exit 2, "flags
    /// provided but not defined: -verbose"). claude's <c>-p</c> is a boolean with a positional prompt
    /// and does take <c>--verbose</c>. Measured live 2026-08-10; the corrected agy form streams
    /// <c>{"event":"init"} → {"event":"step_update"} → {"event":"result",…,"usage":{…}}</c> on stdout.
    /// </summary>
    internal static string[] StreamJsonArgs(string vendor, string prompt) =>
        IsCodex(vendor)
            ? CodexProbe.ExecJsonArgs(prompt)
            : IsAgy(vendor)
            ? ["-p", prompt, "--output-format", "stream-json"]
            : ["-p", "--output-format", "stream-json", "--verbose", prompt];

    /// <summary>
    /// Whether stdout is a stream-json event stream, recognising <b>either</b> vendor's envelope: the
    /// first non-empty line is a JSON object carrying a <c>type</c> (claude) or <c>event</c> (agy)
    /// discriminator. Keying only on claude's <c>type</c> is the second half of the #1088 false
    /// negative — even a correctly-invoked agy stream read as "not structured" because its lines say
    /// <c>"event"</c>, not <c>"type"</c>. Structural (parses the line) rather than a substring, so a
    /// vendor merely mentioning the word "type" in prose cannot masquerade as a stream.
    /// </summary>
    internal static bool LooksLikeStreamJson(string stdout)
    {
        var firstLine = stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0);
        if (firstLine is null || !firstLine.StartsWith('{'))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(firstLine);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && (doc.RootElement.TryGetProperty("type", out _) || doc.RootElement.TryGetProperty("event", out _));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static IReadOnlyList<Finding> RunCodex(string vendor, string program, string version)
    {
        var execHelp = Cli.Invoke(program, ["exec", "--help"], TimeSpan.FromSeconds(45)).All;
        var auth = Cli.Invoke(program, ["login", "status"], TimeSpan.FromSeconds(45));
        var subscriptionAuth = CodexProbe.IsChatGptSubscriptionAuth(auth.All);
        var turn = subscriptionAuth
            ? Cli.Invoke(
                program,
                CodexProbe.ExecJsonArgs("Reply with exactly: ok"),
                TimeSpan.FromMinutes(2))
            : new Cli.Run(
                -1, string.Empty,
                "paid turn skipped because ChatGPT subscription authentication was not confirmed",
                TimedOut: false);
        var resume = subscriptionAuth && CodexProbe.TryReadThreadId(turn.StdOut, out var sessionId)
            ? Cli.Invoke(
                program,
                CodexProbe.ResumeJsonArgs(sessionId, "Reply with exactly: resumed-ok"),
                TimeSpan.FromMinutes(2))
            : new Cli.Run(
                -1, string.Empty,
                "resume probe skipped because no authenticated initial thread id was captured",
                TimedOut: false);

        var seenResponses = new HashSet<int>();
        var appServer = Cli.InvokeProtocol(
            program,
            CodexProbe.AppServerArgs(),
            CodexProbe.AppServerRequests(),
            line =>
            {
                if (CodexProbe.IsRequestedResponse(line, CodexProbe.ModelListRequestId))
                {
                    seenResponses.Add(CodexProbe.ModelListRequestId);
                }

                if (CodexProbe.IsRequestedResponse(line, CodexProbe.RateLimitsRequestId))
                {
                    seenResponses.Add(CodexProbe.RateLimitsRequestId);
                }

                return seenResponses.Count == 2;
            },
            TimeSpan.FromSeconds(45));

        return
        [
            CodexAuthentication(vendor, version, auth),
            CodexPlanUsage(vendor, version, appServer),
            CodexPerTurnCost(vendor, version, turn),
            CodexStructuredOutput(vendor, version, turn),
            CodexResume(vendor, version, turn, resume),
            CodexPermissionControl(vendor, version, execHelp),
            CodexEffort(vendor, version, appServer),
            CodexAddDir(vendor, version, execHelp),
            CodexModels(vendor, version, appServer),
        ];
    }

    private static Finding CodexResume(string vendor, string version, Cli.Run initial, Cli.Run resume)
    {
        const string cap = "resume & per-turn usage";
        string[] surfaces = [Surfaces.StructuredOutput];
        var initialUsage = CodexProbe.HasTurnUsage(initial.StdOut);
        var resumedUsage = CodexProbe.HasTurnUsage(resume.StdOut);
        return CodexProbe.TryReadThreadId(initial.StdOut, out var initialId)
               && CodexProbe.TryReadThreadId(resume.StdOut, out var resumedId)
               && string.Equals(initialId, resumedId, StringComparison.Ordinal)
               && initialUsage
               && resumedUsage
            ? Finding.Seen(
                cap, vendor, "`codex exec resume` — same thread id, usage on both turns", surfaces,
                "The second tiny Luna/low turn resumed the captured thread id and emitted its own `turn.completed.usage` object. Raw identifiers are not written into the finding.",
                version)
            : Finding.Absent(
                cap, vendor, surfaces,
                "The probe did not observe the same thread id and a terminal usage object on both the initial and resumed turns. "
                + $"Initial exit {initial.ExitCode}, resume exit {resume.ExitCode}; timeouts: {initial.TimedOut}/{resume.TimedOut}.",
                version);
    }

    private static Finding CodexAuthentication(string vendor, string version, Cli.Run auth)
    {
        const string cap = "subscription authentication";
        string[] surfaces = [Surfaces.Subcommands];
        return CodexProbe.IsChatGptSubscriptionAuth(auth.All)
            ? Finding.Seen(
                cap, vendor, "`codex login status` — ChatGPT subscription", surfaces,
                "The probe scrubbed API-key and provider-override environment variables before the CLI confirmed ChatGPT authentication.",
                version)
            : Finding.Absent(
                cap, vendor, surfaces,
                "`codex login status` did not confirm ChatGPT authentication. The paid exec probe was skipped; API-key fallback is never allowed.",
                version);
    }

    private static Finding CodexPlanUsage(string vendor, string version, Cli.Run appServer)
    {
        const string cap = "plan usage & reset";
        string[] surfaces = [Surfaces.AppServer];
        if (CodexProbe.TryDescribeRateLimits(appServer.StdOut, out var summary))
        {
            return Finding.Seen(
                cap,
                vendor,
                $"`account/rateLimits/read` — {summary}",
                surfaces,
                "The initialized app-server returned structured used-percent windows. Reset timestamps are "
                + "reported only where the summary includes one; the probe does not infer how tokens debit a window.",
                version);
        }

        return Finding.Absent(
            cap,
            vendor,
            surfaces,
            "The initialized app-server's `account/rateLimits/read` response did not contain a window with "
            + "both recognizable usage fields and semantics. This is unknown rather than zero remaining. "
            + $"Protocol timeout: {appServer.TimedOut}.",
            version);
    }

    private static Finding CodexPerTurnCost(string vendor, string version, Cli.Run turn)
    {
        const string cap = "per-turn cost";
        string[] surfaces = [Surfaces.StructuredOutput];
        var hasUsage = CodexProbe.HasTurnUsage(turn.StdOut);
        return Finding.Absent(
            cap,
            vendor,
            surfaces,
            "No dollar-denominated cost field was found in the `codex exec --json` stream. "
            + (hasUsage
                ? "The completed turn did carry per-turn token usage, so the native evidence is token-denominated; any API-equivalent cost requires a versioned external price table."
                : "The run did not yield a recognized `turn.completed.usage` object, so token usage is also unverified for this version."),
            version);
    }

    private static Finding CodexStructuredOutput(string vendor, string version, Cli.Run turn)
    {
        const string cap = "structured output";
        string[] surfaces = [Surfaces.ExecHelp, Surfaces.StructuredOutput];
        if (CodexProbe.LooksLikeExecJson(turn.StdOut))
        {
            return Finding.Seen(
                cap,
                vendor,
                "`codex exec --json` JSONL",
                surfaces,
                $"The trivial turn emitted {turn.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length} JSONL event line(s). "
                + "Model and quota discovery separately exercise initialized app-server JSON-RPC.",
                version);
        }

        return Finding.Absent(
            cap,
            vendor,
            surfaces,
            $"`codex exec --json` produced no recognized typed JSONL event. Exit {turn.ExitCode}; timeout: {turn.TimedOut}.",
            version);
    }

    private static Finding CodexPermissionControl(string vendor, string version, string execHelp)
    {
        const string cap = "--permission-prompt-tool";
        string[] surfaces = [Surfaces.ExecHelp];
        var hasSandbox = execHelp.Contains("--sandbox", StringComparison.OrdinalIgnoreCase);
        return Finding.Absent(
            cap,
            vendor,
            surfaces,
            "Codex exposes no `--permission-prompt-tool` equivalent on the inspected surfaces. "
            + (hasSandbox
                ? "Its distinct control surface is `codex exec --sandbox` plus approval-policy configuration; that is not represented as an equivalent callback."
                : "The expected `--sandbox` control was also absent from `codex exec --help`, so permissions need re-probing before adapter use."),
            version);
    }

    private static Finding CodexEffort(string vendor, string version, Cli.Run appServer)
    {
        const string cap = "effort";
        string[] surfaces = [Surfaces.AppServer];
        return CodexProbe.TryDescribeModels(appServer.StdOut, out var summary)
            ? Finding.Seen(
                cap,
                vendor,
                "model-specific reasoning efforts from `model/list`",
                surfaces,
                $"The visible model catalog reported these model/effort pairs: {summary}.",
                version)
            : Finding.Absent(
                cap,
                vendor,
                surfaces,
                "The initialized app-server returned no parseable visible model/effort catalog.",
                version);
    }

    private static Finding CodexAddDir(string vendor, string version, string execHelp)
    {
        const string cap = "extra directories";
        string[] surfaces = [Surfaces.ExecHelp];
        return execHelp.Contains("--add-dir", StringComparison.OrdinalIgnoreCase)
            ? Finding.Read(
                cap,
                vendor,
                "`codex exec --add-dir`",
                surfaces,
                "Read from `codex exec --help`; the adapter must still verify that managed host policy honors the requested writable roots.",
                version)
            : Finding.Absent(cap, vendor, surfaces, "No `--add-dir` in `codex exec --help`.", version);
    }

    private static Finding CodexModels(string vendor, string version, Cli.Run appServer)
    {
        const string cap = "models";
        string[] surfaces = [Surfaces.AppServer];
        if (!CodexProbe.TryDescribeModels(appServer.StdOut, out var summary))
        {
            return Finding.Absent(
                cap,
                vendor,
                surfaces,
                "No visible model catalog was parsed from the initialized app-server `model/list` response.",
                version);
        }

        var detail = $"The account-sensitive app-server catalog reported: {summary}.";

        if (CodexProbe.TryParseLiveCatalog(appServer.StdOut, out var liveCatalog))
        {
            var drift = CodexProbe.CompareCatalog(
                liveCatalog,
                CodexWorkerAdapter.RecordedEfforts,
                CodexWorkerAdapter.RecordedSnapshotResourceName);

            if (drift.HasDrift)
            {
                detail += $" WARN RECORDING-DRIFT: {drift.Explain()}";
            }
        }

        return Finding.Seen(cap, vendor, "visible models from `model/list`", surfaces, detail, version);
    }

    /// <summary>API-equivalent cost per turn — the "what would this have cost on a key" number.</summary>
    private static Finding PerTurnCost(string vendor, string version)
    {
        const string cap = "per-turn cost";
        string[] surfaces = [Surfaces.StructuredOutput, Surfaces.Help];

        var run = Cli.Invoke(vendor, StreamJsonArgs(vendor, "Reply with exactly: ok"), TimeSpan.FromMinutes(2));
        var cost = Regex.Match(run.StdOut, @"""total_cost_usd""\s*:\s*([0-9.]+)");
        if (cost.Success)
        {
            var tokens = Regex.Matches(run.StdOut, @"""(input_tokens|output_tokens|cache_creation_input_tokens|cache_read_input_tokens)""\s*:\s*\d+")
                .Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x, StringComparer.Ordinal);
            return Finding.Seen(cap, vendor, "`total_cost_usd` in every result event", surfaces,
                $"Observed ${cost.Groups[1].Value} on a trivial turn, alongside {string.Join(", ", tokens)}. "
                + "The CLI computes the figure, so there is no price table to maintain and no drift to chase.",
                version);
        }

        // A stream-json run that carries token usage but no dollar figure is a real, distinct state —
        // 0023/#479 spend is shown against a subscription's own limits, never faked into dollars. Say
        // which of the two absences this is, so "none" is not misread as "produced nothing".
        var streamedUsage = LooksLikeStreamJson(run.StdOut) && run.StdOut.Contains("\"usage\"", StringComparison.Ordinal);
        return Finding.Absent(cap, vendor, surfaces,
            "No `total_cost_usd` in a `stream-json` run, and no cost flag in `--help`. "
            + (run.StdOut.Length == 0
                ? "The structured-output run produced nothing at all."
                : streamedUsage
                    ? "The run streamed a `result` event carrying per-turn **token** usage (the `usage` object), but no dollar cost field — token-denominated, not dollars."
                    : "The run produced output, but carried no cost field."),
            version);
    }

    private static Finding StructuredOutput(string vendor, string version)
    {
        const string cap = "structured output";
        string[] surfaces = [Surfaces.Help, Surfaces.StructuredOutput, Surfaces.LocalServer];

        var run = Cli.Invoke(vendor, StreamJsonArgs(vendor, "Reply with exactly: ok"), TimeSpan.FromMinutes(2));
        if (LooksLikeStreamJson(run.StdOut))
        {
            var flagShown = IsAgy(vendor) ? "`--output-format stream-json`" : "`--output-format stream-json --verbose`";
            return Finding.Seen(cap, vendor, flagShown, surfaces,
                $"Emitted {run.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length} JSON lines on a trivial turn.",
                version);
        }

        // stdout is not the only place structured events can live. `agy` starts a local RPC server
        // on every run and prints its ports to the log — a surface the first probe never looked at,
        // which is why this row read "not found" while a reachable gRPC endpoint was running.
        var server = LocalRpcServer(vendor);
        if (server is not null)
        {
            return Finding.Read(cap, vendor, "a local RPC server (ports in `--log-file`)", surfaces,
                $"No structured stdout: `--output-format stream-json` exited {run.ExitCode}. But the CLI starts a "
                + $"local server on every run — \"{server}\" — so this is **not found on stdout**, not an absence "
                + "of structured events. The service and method names are not yet enumerated; until they are, "
                + "treat this as the highest-value open probe rather than a settled negative.",
                version);
        }

        return Finding.Absent(cap, vendor, surfaces,
            "`--output-format stream-json` was rejected or produced non-JSON, and no local RPC server was "
            + $"announced in the log. Exit {run.ExitCode}. First stderr line: "
            + (run.StdErr.Split('\n').FirstOrDefault()?.Trim() is { Length: > 0 } e ? e : "(none)"),
            version);
    }

    /// <summary>
    /// Whether a run announces a local RPC server, read out of the CLI's own log file.
    /// </summary>
    private static string? LocalRpcServer(string vendor)
    {
        string? announcement = null;

        Cli.InScratch(dir =>
        {
            var log = Path.Combine(dir, "probe.log");
            Cli.Invoke(vendor, ["--log-file", log, "-p", "Reply with exactly: ok"], TimeSpan.FromMinutes(2));
            if (!File.Exists(log))
            {
                return;
            }

            var m = Regex.Match(
                File.ReadAllText(log),
                @"listening on random port at (\d+) for (\S+)",
                RegexOptions.IgnoreCase);
            if (m.Success)
            {
                announcement = m.Value;
            }
        });

        return announcement;
    }

    /// <summary>
    /// Load-bearing. If this is wrong the way <c>/usage</c> was wrong, decision 0015's mechanism
    /// inverted to a blocking MCP tool on a false premise.
    /// </summary>
    private static Finding PermissionPromptTool(string vendor, string version, FlagProbe.Behaviour baseline)
    {
        const string cap = "--permission-prompt-tool";
        const string flag = "--permission-prompt-tool";
        string[] surfaces = [Surfaces.Help, Surfaces.StdErr, Surfaces.ControlFlag, Surfaces.StructuredOutput];
        var help = Help(vendor);
        var documented = help.Contains(flag, StringComparison.OrdinalIgnoreCase);

        var provenance = documented
            ? "Documented in `--help`."
            : "**Undocumented in `--help`** — which is why help text alone was never enough.";

        var (parses, parseDetail) = FlagProbe.IsAccepted(vendor, baseline, flag, "noop", "-p", "hi");
        if (parses is not true)
        {
            return Finding.Absent(cap, vendor, parses is null ? [Surfaces.Help] : surfaces,
                $"{provenance} {parseDetail}", version);
        }

        // Parsing is not honouring, and the distinction is the whole reason this row was wrong
        // before. A prompt that triggers no tool call can never reach a permission decision, so it
        // cannot tell the two apart. Force a tool call and watch for the CLI naming the tool it
        // tried to consult.
        var honoured = Finding.Absent(cap, vendor, surfaces,
            $"{provenance} {parseDetail} But no permission consultation was observed on a turn that "
            + "does require one, so the flag parses without evidence that it is honoured.", version);

        Cli.InScratch(dir =>
        {
            var run = Cli.Invoke(
                vendor,
                [flag, ProbeToolName, "-p", "--output-format", "stream-json", "--verbose",
                 "Use the Write tool to create a file named x.txt containing BANANA in the current directory."],
                TimeSpan.FromMinutes(3),
                workingDirectory: dir);

            // The tell: the CLI names the tool it was told to consult, in its own words. A name we
            // invented cannot appear in the output unless the flag reached the permission path.
            var consulted = Regex.Match(
                run.All,
                @$"{Regex.Escape(ProbeToolName)}[^""]*?\(passed via {Regex.Escape(flag)}\)",
                RegexOptions.IgnoreCase);

            if (consulted.Success)
            {
                honoured = Finding.Seen(cap, vendor,
                    "`--permission-prompt-tool <mcp-tool>` — consulted for permission decisions", surfaces,
                    $"{provenance} Honoured, not merely parsed: on a turn requiring a Write permission the CLI "
                    + $"reported \"{consulted.Value}\", naming the tool it tried to consult. The flag therefore "
                    + "routes permission decisions to an **MCP tool**, which is the same mechanism 0015 already "
                    + "chose — but as the vendor's designated entry point, consulted for every decision, rather "
                    + $"than a tool the model must elect to call. Established with a name (`{ProbeToolName}`) that "
                    + "exists nowhere, so it could not have come from anywhere but the flag.",
                    version);
            }
        });

        return honoured;
    }

    /// <summary>
    /// A tool name that exists nowhere, so seeing it echoed back proves the flag reached the
    /// permission path rather than being parsed and discarded.
    /// </summary>
    private const string ProbeToolName = "aer_probe_no_such_tool";

    private static Finding Effort(string vendor, string version)
    {
        const string cap = "effort";
        string[] surfaces = [Surfaces.Help];
        var help = Help(vendor);

        // Take the option's whole entry, not its first line. Help output wraps, and `claude` puts the
        // accepted value list on the continuation line — a single-line match reported "no values
        // documented" while "(low, medium, high, xhigh, max)" sat one line below the match.
        var m = Regex.Match(help, @"--effort(?:[^\r\n]*)(?:\r?\n[ \t]{4,}[^\r\n]*)*", RegexOptions.IgnoreCase);
        if (!m.Success)
        {
            return Finding.Absent(cap, vendor, surfaces, "No `--effort` in help text.", version);
        }

        var entry = Regex.Replace(m.Value, @"\s+", " ").Trim();
        // Both separators, because the vendors disagree: `claude` writes "(low, medium, high, xhigh,
        // max)" and `agy` writes "(low|medium|high)". A comma-only pattern read agy's documented set
        // as undocumented — the same one-surface mistake in miniature.
        var values = Regex.Match(entry, @"\(([a-z]+(?:\s*[,|]\s*[a-z]+)+)\)");

        return Finding.Read(cap, vendor,
            values.Success ? $"`--effort` — {values.Groups[1].Value}" : "`--effort` (no values documented)",
            surfaces,
            $"Read from help: \"{entry}\". "
            + (values.Success
                ? "Help names the accepted values, but naming is not behaviour: 0023 declines to assert a "
                  + "mapping until each value is shown to be accepted AND to behave distinctly."
                : "The option is documented without its values, so the accepted set is unknown, not empty."),
            version);
    }

    private static Finding AddDir(string vendor, string version)
    {
        const string cap = "extra directories";
        string[] surfaces = [Surfaces.Help];
        var help = Help(vendor);
        var m = Regex.Match(help, @"--add-dir[^\r\n]*", RegexOptions.IgnoreCase);
        return m.Success
            ? Finding.Read(cap, vendor, "`--add-dir`", surfaces,
                "Read from help. On `agy` this is load-bearing rather than optional: `-p` ignores the "
                + "process working directory entirely (#491), so the room's folder must be bound explicitly.",
                version)
            : Finding.Absent(cap, vendor, surfaces, "No `--add-dir` in help text.", version);
    }
}
