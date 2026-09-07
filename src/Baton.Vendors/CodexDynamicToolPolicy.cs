using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Baton.Domain;
using Baton.Status;

namespace Baton.Vendors;

/// <summary>
/// Baton's enforcement boundary for Codex app-server dynamic tools. Codex receives no native shell,
/// file-mutation, MCP, app, browser, or computer tool; every capability it can invoke is defined and
/// executed here from the canonical <see cref="PermissionGrant"/>.
/// </summary>
public sealed class CodexDynamicToolPolicy
{
    internal const string ReadTextTool = "baton_read_text";
    internal const string ListFilesTool = "baton_list_files";
    internal const string SearchTextTool = "baton_search_text";
    internal const string WriteOutputTool = "baton_write_output";
    internal const string WriteTextTool = "baton_write_text";
    /// <summary>Named once in the engine (<see cref="CodexUsageParser.RunCommandToolName"/>) — see there for why.</summary>
    internal const string RunCommandTool = CodexUsageParser.RunCommandToolName;

    /// <summary>
    /// Codex's own edit verb, declared under its native name (#1996). A write-granted codex worker had
    /// a write tool — <see cref="WriteTextTool"/> — and still concluded its workspace was read-only,
    /// because what it reached for was <c>apply_patch</c> and app-server offered nothing by that name.
    /// The grant applies here exactly as it does to <see cref="WriteTextTool"/>: same workspace root,
    /// same resolver, same refusal text. No new permission concept, and no separate write path — the
    /// envelope is parsed by <see cref="CodexApplyPatch"/> and written by <see cref="WriteFile"/>.
    /// <para>
    /// <b>Not <c>baton_apply_patch</c>, and the evidence for that is scoped.</b> The probe of
    /// 2026-09-04 (CLI 0.153.x, one host — <c>docs/vendor-codex-probe-2026-09-04.md</c>, "Baton role
    /// mediation through app-server") recorded that with this broker's disabled-feature list applied
    /// and Code Mode enabled, the nested tool inventory the model saw held ONLY Baton's
    /// grant-generated dynamic tools: no native verb shared that namespace for this name to collide
    /// with, and codex's own apply_patch rides on the execution surfaces
    /// (<c>shell_tool</c>, <c>unified_exec</c>) that <c>CodexAppServerBroker.DisabledFeatures</c>
    /// turns off. What that observation does NOT cover is the write-granted inventory, which the same
    /// document lists as unmeasured. So the native name is kept — it is the one a codex model reaches
    /// for, which is the whole of #1996 — and the first live write-granted lane's declared inventory
    /// is what would falsify it. A collision would show up as this tool never reaching
    /// <see cref="ExecuteAsync"/>.
    /// </para>
    /// </summary>
    internal const string ApplyPatchTool = "apply_patch";

    /// <summary>
    /// The one sentence every withheld-workspace-write refusal opens with, stated once because two
    /// tools raise it (<see cref="WriteText"/> and <see cref="ApplyPatch"/>) and a third would
    /// otherwise re-type it.
    /// </summary>
    private const string WithheldWorkspaceWrite = "This Baton role does not grant workspace writes.";

    private const int MaxReadCharacters = 200_000;
    private const int MaxListedFiles = 1_000;
    private const int MaxSearchMatches = 500;
    private const int MaxCommandOutputCharacters = 200_000;

    private readonly PermissionGrant _grant;
    private readonly string? _workspaceRoot;
    private readonly string _outputRoot;
    private readonly IReadOnlyList<string> _inputRoots;
    private readonly HashSet<string> _declaredOutputs;
    private readonly Func<ShellCommandClass, TimeSpan> _commandCeiling;

    /// <param name="commandCeiling">
    /// How long one <c>baton_run_command</c> of a given class may run before Baton kills its process
    /// tree; null is <see cref="ShellCommandCeilings.For"/>, the production table. A delegate rather
    /// than constants so the timeout arm is exercisable in a second instead of minutes — a test that
    /// cannot reach it is how the timeout came to be reported as a grant refusal (#1921 review HIGH) —
    /// and per-CLASS since #1998, so a test can also show the classes actually differ rather than only
    /// that one of them fires.
    /// </param>
    public CodexDynamicToolPolicy(
        PermissionGrant grant,
        string? workingDirectory,
        string outputDirectory,
        IEnumerable<string> inputPaths,
        IEnumerable<string> producedOutputNames,
        Func<ShellCommandClass, TimeSpan>? commandCeiling = null)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(inputPaths);
        ArgumentNullException.ThrowIfNull(producedOutputNames);

        _grant = grant;
        _workspaceRoot = string.IsNullOrWhiteSpace(workingDirectory) ? null : NormalizeRoot(workingDirectory);
        _outputRoot = NormalizeRoot(outputDirectory);
        _inputRoots = inputPaths.Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath).Distinct(PathComparer).ToArray();
        _declaredOutputs = producedOutputNames.Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(NormalizeRelativeOutput).ToHashSet(PathComparer);
        _commandCeiling = commandCeiling ?? ShellCommandCeilings.For;
    }

    /// <summary>
    /// The model's only specification of the format, so it states the subset and the exclusions rather
    /// than the verb alone — an <c>apply_patch</c> that silently accepted less than codex's own dialect
    /// would read as a guarantee it does not provide.
    /// </summary>
    private const string ApplyPatchDescription =
        "Edit files under Baton's granted workspace root with a codex patch envelope. This is the "
        + "edit tool: the workspace is not writable through the shell. Supported inside "
        + "'*** Begin Patch' / '*** End Patch': '*** Add File: <path>' with every body line prefixed "
        + "'+', '*** Delete File: <path>', and '*** Update File: <path>' with '@@' section markers and "
        + "' ' context, '-' removed and '+' added lines. Context must match the file exactly — there "
        + "is no fuzzy matching — and must match in exactly ONE place: a hunk whose context fits twice "
        + "is refused rather than placed at the first fit. Narrow it with more context lines, or with "
        + "a '@@ <line>' locator above the hunk that reproduces one earlier line of the file, whose "
        + "indentation is ignored — the search then starts there. One '@@' line per hunk, every hunk "
        + "needs at least one context "
        + "or removed line, one header per path (merge a file's hunks into a single block), and "
        + "'*** Move to:' is not supported. Baton checks every path and places every hunk before it "
        + "writes anything, so a refused path or an unplaceable hunk changes no file. To replace a "
        + "file's entire contents instead, use " + WriteTextTool + ".";

    private const string ApplyPatchInputDescription =
        "The complete patch envelope, from '*** Begin Patch' to '*** End Patch'.";

    /// <summary>The exact dynamic-tool declarations supplied on <c>thread/start</c>.</summary>
    public JsonArray BuildToolDefinitions()
    {
        var tools = new JsonArray();
        if (_grant.ReadFiles || _inputRoots.Count > 0)
        {
            tools.Add(Function(ReadTextTool, "Read UTF-8 text from a path allowed by Baton's role grant.",
                StringSchema("path", "Absolute or workspace-relative file path.")));
        }

        if (_grant.ReadFiles)
        {
            tools.Add(Function(ListFilesTool, "List files below a directory allowed by Baton's role grant.",
                StringSchema("path", "Absolute or workspace-relative directory path.")));
            tools.Add(Function(SearchTextTool, "Search allowed UTF-8 text files for a literal string.",
                TwoStringSchema("path", "Directory or file to search.", "query", "Literal text to find.")));
        }

        if (_declaredOutputs.Count > 0)
        {
            var outputSchema = TwoStringSchema("name", "One declared output name.", "content", "Complete UTF-8 file content.");
            ((JsonObject)((JsonObject)outputSchema["properties"]!)["name"]!)["enum"] =
                new JsonArray(_declaredOutputs.Order(PathComparer)
                    .Select(name => (JsonNode?)JsonValue.Create(name)).ToArray());
            tools.Add(Function(WriteOutputTool, "Write one exact output declared by the Baton worker contract.", outputSchema));
        }

        if (_grant.WriteFiles)
        {
            // Declared FIRST of the two writes, and named as the edit tool, because it is the verb a
            // codex model reaches for; baton_write_text stays for whole-file replacement.
            tools.Add(Function(ApplyPatchTool, ApplyPatchDescription, StringSchema("input", ApplyPatchInputDescription)));
            tools.Add(Function(WriteTextTool, "Write complete UTF-8 text under Baton's granted workspace root.",
                TwoStringSchema("path", "Absolute or workspace-relative destination.", "content", "Complete UTF-8 file content.")));
        }

        if (_grant.RunShellCommands)
        {
            tools.Add(Function(RunCommandTool, "Run one command line after Baton's canonical command policy approves it.",
                StringSchema("command", "Command line to evaluate and run.")));
        }

        return tools;
    }

    public async Task<CodexDynamicToolResult> ExecuteAsync(
        string toolName, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            return toolName switch
            {
                ReadTextTool => ReadText(RequiredString(arguments, "path")),
                ListFilesTool => ListFiles(RequiredString(arguments, "path")),
                SearchTextTool => SearchText(
                    RequiredString(arguments, "path"), RequiredString(arguments, "query")),
                WriteOutputTool => WriteOutput(
                    RequiredString(arguments, "name"), RequiredString(arguments, "content")),
                WriteTextTool => WriteText(
                    RequiredString(arguments, "path"), RequiredString(arguments, "content")),
                ApplyPatchTool => ApplyPatch(RequiredString(arguments, "input")),
                RunCommandTool => await RunCommandAsync(
                    RequiredString(arguments, "command"), cancellationToken).ConfigureAwait(false),
                // Not a refusal (#1921 re-review): each of the seven implemented names has its own case
                // above and does its own grant check there, so a tool a grant WITHHELD never reaches
                // here. What reaches here is a name Baton implements nowhere — a hallucinated or stale
                // one — which is a malformed call, the same population as an empty search query. No
                // grant declined it because no grant offers it.
                _ => CodexDynamicToolResult.Failed(DescribeUnknownTool(toolName)),
            };
        }
        catch (CodexGrantRefusedException ex)
        {
            // The boundary decisions the path resolvers take — outside the readable roots, outside the
            // workspace root, escaping an output root, crossing a reparse point. Its own type, and
            // caught before the filter below, because that filter's members (an IOException from a
            // locked file, an ArgumentException from a malformed tool argument) are FAILURES of an
            // allowed call and must not be stamped as refusals.
            return CodexDynamicToolResult.Refused(ex.Message);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException
            or NotSupportedException or System.Security.SecurityException)
        {
            return CodexDynamicToolResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// #1920 ask 2: what an unrecognised tool name is told. The measured dominant case on this arm was
    /// codex reaching for its native <c>apply_patch</c> five times, so a write-shaped attempt is
    /// answered about the WRITE path rather than handed two read tools — the extra step that issue
    /// existed to remove. #1996 then implemented <see cref="ApplyPatchTool"/> itself, so that exact
    /// name no longer arrives here at all: what does is the rest of the write-shaped population (an
    /// editor tool from another vendor's namespace, a hallucinated name), on any grant. Every clause is
    /// derived from <see cref="DeclaredToolNames"/> rather than re-deriving the grant conditions in
    /// <see cref="BuildToolDefinitions"/>, so a role that declares no search tool is never told to
    /// search.
    /// </summary>
    private string DescribeUnknownTool(string toolName)
    {
        var declared = DeclaredToolNames();
        var known = declared.Count > 0
            ? $"This role's tools are: {string.Join(", ", declared)}."
            : "This role declares no dynamic tools.";
        var guidance = LooksLikeWriteAttempt(toolName)
            ? DescribeWritePath(declared)
            : DescribeReadPath(declared);

        return $"Tool '{toolName}' is not present in this Baton role grant. {known}"
            + (guidance is null ? string.Empty : $" {guidance}");
    }

    /// <summary>
    /// The tool names this dispatch actually declared, read back from the single declaration site so
    /// the two cannot drift.
    /// </summary>
    private IReadOnlyCollection<string> DeclaredToolNames() =>
        BuildToolDefinitions()
            .Select(tool => tool?["name"]?.GetValue<string>())
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .ToArray();

    /// <summary>
    /// A name a model reaches for when it means to change a file — <c>apply_patch</c> was the measured
    /// one (#1920), and is a declared tool since #1996, so what these fragments still catch is every
    /// other write-shaped name. Deliberately a name test only: nothing here inspects arguments.
    /// </summary>
    private static bool LooksLikeWriteAttempt(string toolName) =>
        WriteAttemptFragments.Any(fragment => toolName.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    // Deliberately short of "update"/"insert": codex's own `update_plan` is not a file write, and
    // answering it about the write path would be the same non-responsive guidance in the other
    // direction.
    private static readonly string[] WriteAttemptFragments = ["patch", "write", "edit", "apply"];

    private static string? DescribeReadPath(IReadOnlyCollection<string> declared) =>
        (declared.Contains(ReadTextTool), declared.Contains(SearchTextTool)) switch
        {
            (true, true) => $"Read with {ReadTextTool} and search with {SearchTextTool}.",
            (true, false) => $"Read with {ReadTextTool}.",
            (false, true) => $"Search with {SearchTextTool}.",
            _ => null,
        };

    private string DescribeWritePath(IReadOnlyCollection<string> declared)
    {
        if (declared.Contains(ApplyPatchTool))
        {
            return $"Edit with {ApplyPatchTool}, which takes a '*** Begin Patch' envelope, or replace a "
                + $"file whole with {WriteTextTool}.";
        }

        if (declared.Contains(WriteTextTool))
        {
            return $"Write with {WriteTextTool}, which takes a path and the file's complete new content.";
        }

        if (declared.Contains(WriteOutputTool))
        {
            return "This role cannot edit workspace files. Its only write is "
                + $"{WriteOutputTool}, for one of its declared outputs: "
                + $"{string.Join(", ", _declaredOutputs.Order(PathComparer))}.";
        }

        return "This role has no write tool at all: it cannot create or edit any file.";
    }

    private CodexDynamicToolResult ReadText(string requestedPath)
    {
        if (!_grant.ReadFiles && _inputRoots.Count == 0)
        {
            return CodexDynamicToolResult.Refused("This Baton role does not grant file reads.");
        }

        var path = ResolveAllowedRead(requestedPath);
        if (!File.Exists(path))
        {
            // A missing file is a failure of an ALLOWED read, not a refusal: the grant let this path
            // through and the workspace had nothing there. Same distinction at every Failed below.
            return CodexDynamicToolResult.Failed($"File '{requestedPath}' does not exist.");
        }

        EnsureNoReparsePoint(path);
        var text = File.ReadAllText(path, Encoding.UTF8);
        if (text.Length > MaxReadCharacters)
        {
            text = text[..MaxReadCharacters] + $"\n[truncated by Baton at {MaxReadCharacters} characters]";
        }
        return CodexDynamicToolResult.Allowed(text);
    }

    private CodexDynamicToolResult ListFiles(string requestedPath)
    {
        if (!_grant.ReadFiles)
        {
            return CodexDynamicToolResult.Refused("This Baton role does not grant workspace file listing.");
        }

        var path = ResolveWithinWorkspace(requestedPath);
        if (!Directory.Exists(path))
        {
            return CodexDynamicToolResult.Failed($"Directory '{requestedPath}' does not exist.");
        }

        EnsureNoReparsePoint(path);
        var options = SafeEnumerationOptions();
        var files = EnumerateContentFiles(path, options).Take(MaxListedFiles + 1).ToArray();
        bool truncated = files.Length > MaxListedFiles;
        var rendered = files.Take(MaxListedFiles)
            .Select(file => Path.GetRelativePath(_workspaceRoot!, file).Replace('\\', '/'));
        return CodexDynamicToolResult.Allowed(
            string.Join('\n', rendered) + (truncated ? $"\n[truncated by Baton at {MaxListedFiles} files]" : string.Empty));
    }

    private CodexDynamicToolResult SearchText(string requestedPath, string query)
    {
        if (!_grant.ReadFiles)
        {
            return CodexDynamicToolResult.Refused("This Baton role does not grant workspace text search.");
        }
        if (query.Length == 0)
        {
            return CodexDynamicToolResult.Failed("Search query must not be empty.");
        }

        var path = ResolveWithinWorkspace(requestedPath);
        EnsureNoReparsePoint(path);
        IEnumerable<string> files = File.Exists(path)
            ? [path]
            : Directory.Exists(path)
                ? EnumerateContentFiles(path, SafeEnumerationOptions())
                : throw new ArgumentException($"Search path '{requestedPath}' does not exist.");

        List<string> matches = [];
        foreach (var file in files)
        {
            if (matches.Count >= MaxSearchMatches)
            {
                break;
            }
            try
            {
                int lineNumber = 0;
                foreach (var line in File.ReadLines(file, Encoding.UTF8))
                {
                    lineNumber++;
                    if (line.Contains(query, StringComparison.Ordinal))
                    {
                        matches.Add($"{Path.GetRelativePath(_workspaceRoot!, file).Replace('\\', '/')}:{lineNumber}:{line}");
                        if (matches.Count >= MaxSearchMatches)
                        {
                            break;
                        }
                    }
                }
            }
            catch (DecoderFallbackException)
            {
                // A binary or non-UTF-8 file is not a match, not a reason to abort the whole search.
            }
        }

        return CodexDynamicToolResult.Allowed(
            string.Join('\n', matches) + (matches.Count >= MaxSearchMatches
                ? $"\n[truncated by Baton at {MaxSearchMatches} matches]" : string.Empty));
    }

    private CodexDynamicToolResult WriteOutput(string outputName, string content)
    {
        var normalized = NormalizeRelativeOutput(outputName);
        if (!_declaredOutputs.Contains(normalized))
        {
            // Not a refusal: the declared outputs come from the WORKER CONTRACT, not from
            // PermissionGrant — a read-only role is offered this tool — so a name outside the list is
            // a call that did not match the contract rather than one the grant declined.
            return CodexDynamicToolResult.Failed($"'{outputName}' is not a declared output for this Baton worker.");
        }

        var path = ResolveWithinRoot(_outputRoot, normalized);
        WriteFile(path, content);
        return CodexDynamicToolResult.Allowed($"Wrote declared output '{normalized}'.");
    }

    private CodexDynamicToolResult WriteText(string requestedPath, string content)
    {
        if (!_grant.WriteFiles)
        {
            return RefuseWorkspaceWrite();
        }

        var path = ResolveWithinWorkspace(requestedPath);
        WriteFile(path, content);
        return CodexDynamicToolResult.Allowed($"Wrote '{path}'.");
    }

    /// <summary>
    /// #1996. Every path in the envelope is resolved and grant-checked — outside the workspace root,
    /// and crossing a reparse point, on all three kinds — and every new content computed, BEFORE the
    /// first byte is written: a two-file patch whose second path is outside the workspace leaves the
    /// first file untouched. A patch is one edit, and half of one applied is a workspace no reader —
    /// model or human — can reason about. It is not a transaction, and the tool description does not
    /// offer one: partway through the write loop below, an I/O error (a locked file) can still leave an
    /// earlier file written, and so can <see cref="WriteFile"/>'s own re-check of the leaf, which is
    /// there to catch a path that BECAME a link between this planning pass and the write. Both are the
    /// same exposure <see cref="WriteText"/> has; neither is a grant decision taken on a path this
    /// method could have checked first.
    /// </summary>
    private CodexDynamicToolResult ApplyPatch(string input)
    {
        if (!_grant.WriteFiles)
        {
            // Reached, rather than the unknown-tool fallthrough it used to hit, now that this name is
            // implemented. That makes it a real grant refusal — marked and counted like every other —
            // so it carries the #1920 guidance the fallthrough used to add, from the same single site.
            return RefuseWorkspaceWrite();
        }

        var operations = CodexApplyPatch.Parse(input);
        List<(CodexPatchOperationKind Kind, string Path, string Content)> planned = [];
        // #1996 re-review LOW: the parser's one-path-one-header check sees path TEXT, so the aliases
        // only a resolver can see get past it — './a.txt' against 'a.txt', and on Windows 'A.txt'
        // against 'a.txt'. Each operation is planned from what is on DISK, so two headers reaching one
        // file write it twice and the last one silently discards the first's hunks while the result
        // still says Allowed. Keyed on the resolved path with the same platform-aware comparer the
        // rest of this class uses, and here in the plan loop rather than the write loop, so it refuses
        // before any byte is written.
        Dictionary<string, string> resolved = new(PathComparer);
        foreach (var operation in operations)
        {
            // ResolveWithinWorkspace, not a resolver of this method's own: the refusal a path outside
            // the workspace gets here is character-for-character the one baton_write_text gives.
            var path = ResolveWithinWorkspace(operation.Path);
            if (resolved.TryGetValue(path, out var first))
            {
                return CodexDynamicToolResult.Failed(
                    $"'{operation.Path}' and '{first}' are the same file, so this patch has two "
                    + "headers for one path; merge the hunks into one Update File block.");
            }
            resolved.Add(path, operation.Path);
            switch (operation.Kind)
            {
                case CodexPatchOperationKind.Add:
                    // includeLeaf: false because the leaf is what this operation creates — the parents
                    // are what can already be a junction. Planned here rather than left to WriteFile
                    // (#1996 re-review HIGH): the sibling arms check at plan time, and a check that
                    // first runs inside the write loop refuses AFTER an earlier file is on disk.
                    EnsureNoReparsePoint(path, includeLeaf: false);
                    if (File.Exists(path))
                    {
                        return CodexDynamicToolResult.Failed(
                            $"'{operation.Path}' already exists; use '*** Update File:' to change it.");
                    }
                    planned.Add((operation.Kind, path, CodexApplyPatch.AddedContent(operation)));
                    break;
                case CodexPatchOperationKind.Delete:
                    if (!File.Exists(path))
                    {
                        return CodexDynamicToolResult.Failed($"'{operation.Path}' does not exist.");
                    }
                    EnsureNoReparsePoint(path);
                    planned.Add((operation.Kind, path, string.Empty));
                    break;
                default:
                    if (!File.Exists(path))
                    {
                        return CodexDynamicToolResult.Failed(
                            $"'{operation.Path}' does not exist; use '*** Add File:' to create it.");
                    }
                    EnsureNoReparsePoint(path);
                    planned.Add((operation.Kind, path,
                        CodexApplyPatch.ApplyUpdate(File.ReadAllText(path, Encoding.UTF8), operation)));
                    break;
            }
        }

        foreach (var (kind, path, content) in planned)
        {
            if (kind == CodexPatchOperationKind.Delete)
            {
                File.Delete(path);
                continue;
            }
            WriteFile(path, content);
        }
        return CodexDynamicToolResult.Allowed(
            "Applied the patch: " + string.Join(", ", planned.Select(p => $"{p.Kind} '{p.Path}'")) + ".");
    }

    /// <summary>
    /// The single site both workspace-write tools refuse from. It names the write path the role does
    /// have, because a role reaching for an edit tool it was not granted is exactly the case #1920's
    /// unknown-tool guidance was written for — and adding <see cref="ApplyPatchTool"/> to the switch is
    /// what moved this population out of that fallthrough.
    /// </summary>
    private CodexDynamicToolResult RefuseWorkspaceWrite() =>
        CodexDynamicToolResult.Refused($"{WithheldWorkspaceWrite} {DescribeWritePath(DeclaredToolNames())}");

    private async Task<CodexDynamicToolResult> RunCommandAsync(
        string commandLine, CancellationToken cancellationToken)
    {
        if (!_grant.RunShellCommands)
        {
            return CodexDynamicToolResult.Refused("This Baton role does not grant shell commands.");
        }

        var decision = ShellCommandPatternMatcher.EvaluateChainedCommand(
            commandLine, _grant.ShellCommandPatterns, _grant.DeniedShellCommandPatterns);
        if (!decision.IsAllowed)
        {
            // #1920: the matcher's reason states the rule; this site knows the vendor, so it is where
            // the granted alternative gets named (see GrantedReadToolHint). Covers every shell refusal
            // shape the matcher produces — unparseable, standing-deny and not-in-grant alike.
            // Scoped grants only, matching both hooks: on an unscoped grant this refusal is a
            // standing deny (a write-shaped command), and read tools are no answer to one.
            var reason = decision.Reason ?? "Baton denied the command line.";
            var declared = DeclaredToolNames();
            var alternative = _grant.ShellCommandPatterns is { Count: > 0 }
                ? GrantedReadToolHint.Clause(
                    declared.Contains(ReadTextTool) ? ReadTextTool : null,
                    declared.Contains(SearchTextTool) ? SearchTextTool : null)
                : null;
            return CodexDynamicToolResult.Refused(
                alternative is null ? reason : $"{reason}. {char.ToUpperInvariant(alternative[0])}{alternative[1..]}.");
        }
        if (ShellCommandPatternMatcher.IsDeniedByOptionToken(commandLine, _grant.DeniedShellOptionTokens))
        {
            return CodexDynamicToolResult.Refused("The command contains an option token denied by this Baton role.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe" : "/bin/sh",
            WorkingDirectory = _workspaceRoot ?? _outputRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (OperatingSystem.IsWindows())
        {
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(commandLine);
        }
        else
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(commandLine);
        }

        // #1998: the ceiling is per command CLASS. A shipping or gate command is known to be progressing
        // while it runs — a `git push` here spends most of its wall clock inside the repository's own
        // pre-push gate — so the flat ceiling killed finished work rather than runaway work. The classes,
        // the table that sorts a line into one, and every ceiling are in the engine; nothing about them
        // is restated on this path (spec/baton.md §9).
        var commandClass = ShellCommandClassifier.Classify(commandLine);
        var ceiling = _commandCeiling(commandClass);

        using var process = Process.Start(startInfo)
            ?? throw new IOException("Baton could not start the granted command.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ceiling);
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            KillProcessTree(process);
            await DrainAfterKillAsync(stdout, stderr).ConfigureAwait(false);
            // A timeout is a failure of a command the grant ALLOWED and Baton RAN. It costs the step
            // and reports no output, but nothing here declined it, so it carries no refusal marker.
            return CodexDynamicToolResult.Failed(ShellCommandCeilings.DescribeTimeout(commandClass, ceiling));
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
            await DrainAfterKillAsync(stdout, stderr).ConfigureAwait(false);
            throw;
        }

        var combined = (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false));
        if (combined.Length > MaxCommandOutputCharacters)
        {
            combined = combined[^MaxCommandOutputCharacters..] +
                $"\n[leading output truncated by Baton at {MaxCommandOutputCharacters} characters]";
        }
        // A non-zero exit is the command's own answer, carried back whole — `pixi run test` with three
        // failing tests is the case that matters, and its output IS the information the step bought.
        // Failed rather than Refused: stamping the refusal marker here counted every failing allowed
        // command as budget the grant declined (#1921 review HIGH).
        return process.ExitCode == 0
            ? CodexDynamicToolResult.Allowed(combined)
            : CodexDynamicToolResult.Failed($"Command exited {process.ExitCode}.\n{combined}");
    }

    private string ResolveAllowedRead(string requestedPath)
    {
        var candidate = ResolveCandidate(requestedPath);
        if (_grant.ReadFiles && _workspaceRoot is not null && IsWithin(candidate, _workspaceRoot))
        {
            return candidate;
        }
        if (IsWithin(candidate, _outputRoot))
        {
            return candidate;
        }
        if (_inputRoots.Any(input => File.Exists(input)
                ? candidate.Equals(input, PathComparison)
                : IsWithin(candidate, NormalizeRoot(input))))
        {
            return candidate;
        }
        // #1920 (table row 1, the conductor-brief case): the refusal names the roots it checked and
        // the remedy, because the measured failure was a worker handed another room's path and left
        // to guess. Another Baton room is never readable from here, however the path was obtained.
        throw new CodexGrantRefusedException(
            $"Path '{requestedPath}' is outside this Baton's readable roots. "
            + $"Readable here: {DescribeReadableRoots()}. Files under another Baton room are never "
            + "readable from this worker — if a brief pointed at one, ask for its content quoted "
            + "inline instead.");
    }

    /// <summary>
    /// The roots <see cref="ResolveAllowedRead"/> just checked, in the order it checked them — the
    /// workspace only when reads are granted, since that is the condition the check itself carries.
    /// </summary>
    private string DescribeReadableRoots()
    {
        List<string> roots = [];
        if (_grant.ReadFiles && _workspaceRoot is not null)
        {
            roots.Add($"the workspace ({_workspaceRoot})");
        }
        roots.Add($"this worker's outbox ({_outputRoot})");
        roots.AddRange(_inputRoots.Select(input => $"the declared input '{input}'"));
        return string.Join("; ", roots);
    }

    private string ResolveWithinWorkspace(string requestedPath)
    {
        if (_workspaceRoot is null)
        {
            throw new CodexGrantRefusedException("This Baton worker has no workspace root.");
        }
        var candidate = ResolveCandidate(requestedPath);
        if (!IsWithin(candidate, _workspaceRoot))
        {
            throw new CodexGrantRefusedException($"Path '{requestedPath}' is outside this Baton's workspace root.");
        }
        return candidate;
    }

    private string ResolveCandidate(string requestedPath) => Path.GetFullPath(
        Path.IsPathRooted(requestedPath) ? requestedPath : Path.Combine(_workspaceRoot ?? _outputRoot, requestedPath));

    private static string ResolveWithinRoot(string root, string relativePath)
    {
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!IsWithin(candidate, root))
        {
            throw new CodexGrantRefusedException($"Path '{relativePath}' escapes its Baton root.");
        }
        return candidate;
    }

    private static void WriteFile(string path, string content)
    {
        EnsureNoReparsePoint(path, includeLeaf: false);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Re-check the complete destination after creating parents. An existing leaf can itself be
        // a symlink; checking only its parents would let File.WriteAllText follow it outside the
        // granted root.
        EnsureNoReparsePoint(path);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The subprocess raced cancellation to a natural exit.
        }
    }

    private static async Task DrainAfterKillAsync(Task<string> stdout, Task<string> stderr)
    {
        try
        {
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The read tasks share the cancelled timeout token; the process tree is already gone.
        }
    }

    private static void EnsureNoReparsePoint(string path, bool includeLeaf = true)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var current = root;
        var parts = full[root.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (var i = 0; i < parts.Length; i++)
        {
            current = Path.Combine(current, parts[i]);
            if (!includeLeaf && i == parts.Length - 1)
            {
                break;
            }
            if ((File.Exists(current) || Directory.Exists(current))
                && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new CodexGrantRefusedException($"Path '{path}' crosses a symbolic link or reparse point.");
            }
        }
    }

    private static EnumerationOptions SafeEnumerationOptions() => new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false,
    };

    private static IEnumerable<string> EnumerateContentFiles(string path, EnumerationOptions options) =>
        Directory.EnumerateFiles(path, "*", options)
            .Where(file => !Path.GetRelativePath(path, file)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment.Equals(".git", StringComparison.OrdinalIgnoreCase)));

    private static JsonObject Function(string name, string description, JsonObject inputSchema) => new()
    {
        ["type"] = "function",
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = inputSchema,
    };

    private static JsonObject StringSchema(string name, string description) => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            [name] = new JsonObject { ["type"] = "string", ["description"] = description },
        },
        ["required"] = new JsonArray(name),
        ["additionalProperties"] = false,
    };

    private static JsonObject TwoStringSchema(
        string firstName, string firstDescription, string secondName, string secondDescription) => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                [firstName] = new JsonObject { ["type"] = "string", ["description"] = firstDescription },
                [secondName] = new JsonObject { ["type"] = "string", ["description"] = secondDescription },
            },
            ["required"] = new JsonArray(firstName, secondName),
            ["additionalProperties"] = false,
        };

    private static string RequiredString(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(value.GetString()))
        {
            throw new ArgumentException($"Dynamic tool argument '{name}' must be a non-empty string.");
        }
        return value.GetString()!;
    }

    private static string NormalizeRoot(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static string NormalizeRelativeOutput(string name)
    {
        if (Path.IsPathRooted(name))
        {
            throw new ArgumentException($"Declared output '{name}' must be relative.");
        }
        var normalized = name.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (normalized.Split(Path.DirectorySeparatorChar).Any(part => part is "" or "." or ".."))
        {
            throw new ArgumentException($"Declared output '{name}' is not a safe relative path.");
        }
        return normalized;
    }

    private static bool IsWithin(string candidate, string root) =>
        candidate.Equals(root, PathComparison)
        || candidate.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}

/// <summary>
/// A decision this policy's GRANT took, raised where a path is resolved so
/// <see cref="CodexDynamicToolPolicy.ExecuteAsync"/> can map it to
/// <see cref="CodexDynamicToolResult.Refused"/> while every other exception maps to
/// <see cref="CodexDynamicToolResult.Failed"/>. Its own type rather than
/// <see cref="UnauthorizedAccessException"/>, which the filesystem also throws for an allowed path this
/// process simply cannot open — catching that as a refusal is the over-count this split exists to end.
/// </summary>
internal sealed class CodexGrantRefusedException(string message) : Exception(message);

/// <summary>
/// One dynamic-tool call's answer, as <c>CodexAppServerBroker</c> hands it back to codex and copies it
/// into the room's captured stream.
/// <para>
/// <b>Three outcomes, two of them unsuccessful and only one of them a refusal</b> (#1921 review HIGH).
/// <see cref="Success"/> answers "did the call produce what it was asked for"; the marker on
/// <see cref="Refused"/> answers the different question "did Baton's grant decline it", which is the one
/// <c>Status.CodexUsageParser.CountRefusedToolSteps</c> and the ledger's <c>refusedToolSteps</c> report.
/// A single unsuccessful factory conflated the two and stamped every failing allowed command as budget
/// the grant had declined.
/// </para>
/// </summary>
public sealed record CodexDynamicToolResult(bool Success, string Text)
{
    public static CodexDynamicToolResult Allowed(string text) => new(true, text);

    /// <summary>
    /// A GRANT REFUSAL, carrying <see cref="GrantRefusal.Marker"/> (#1921) — the definition
    /// <see cref="GrantRefusal"/> states and this file does not restate.
    /// <para>
    /// <b>The single funnel for every refusal on the codex path</b> — the six "this Baton role does not
    /// grant …" arms, the command matcher's own verdict and the denied option token, and
    /// <c>ExecuteAsync</c>'s mapping of
    /// <see cref="CodexGrantRefusedException"/> (outside the readable roots, outside the workspace root,
    /// escaping an output root, crossing a reparse point). Stamping here rather than at each of those
    /// call sites is what makes the next one impossible to add without the marker.
    /// </para>
    /// <para>
    /// Idempotent through <see cref="GrantRefusal.Stamp"/>, which matters for the one text that arrives
    /// already stamped: <c>ShellCommandPatternMatcher</c>'s own reason, passed through by the run-command
    /// handler.
    /// </para>
    /// </summary>
    public static CodexDynamicToolResult Refused(string text) => new(false, GrantRefusal.Stamp(text));

    /// <summary>
    /// A tool call no grant decision answered, and that did not succeed: a non-zero exit, the command
    /// timeout, a missing file or directory, a malformed argument, an output name outside the worker
    /// contract, an I/O error, and the unknown-tool fallthrough — a name Baton implements nowhere, so
    /// there is no grant that could have offered or withheld it. Unsuccessful and <b>unmarked</b> — its
    /// payload is its reason, so it is neither a refusal nor an empty result, and it must not be counted
    /// as either.
    /// </summary>
    public static CodexDynamicToolResult Failed(string text) => new(false, text);
}
