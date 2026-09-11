using System.Diagnostics;
using System.Security.Cryptography;
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
    internal const string ReadCommandOutputTool = "baton_read_command_output";
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

    // #2206: provisional engineering policy, intentionally conservative rather than presented as a
    // measured optimum. Metadata shares this budget so a recovery pointer cannot push the response
    // back over the ceiling it describes.
    private const int MaxDiscoveryResponseCharacters = 12_000;
    private const int MaxReadRangeCharacters = 12_000;
    private const int MaxListedFiles = 1_000;
    private const int MaxSearchMatches = 500;
    private const int MaxSearchLineSnippetCharacters = 500;
    private const int MaxSearchFooterCharacters = 320;
    // A command preview carries both channel heads and tails plus the recovery reference. The
    // 12k response ceiling is exercised against the synthetic failure fixture in
    // CodexDynamicToolPolicyTests: start/end diagnostics stay in the preview, middle diagnostics
    // are recovered through ReadCommandOutputTool, and the command runs once. This is a bounded
    // response default, not a claim that every command has the same useful context size.
    private const int MaxCommandResponseCharacters = 12_000;
    private const int MaxCommandChannelPreviewCharacters = 5_000;
    private const int MaxCommandChannelPreviewEdgeCharacters = 2_300;

    // Retention is deliberately separate from display. Each redirected channel is drained in
    // bounded buffers while its first one million UTF-16 characters are written under this
    // execution's output root. Larger output is still drained and counted, but the response says
    // exactly how much crossed this limit and is permanently unavailable. A bounded display alone
    // would not bound process memory; CaptureCommandChannelAsync is the memory bound.
    private const int MaxRetainedCommandChannelCharacters = 1_000_000;

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    private readonly PermissionGrant _grant;
    private readonly string? _workspaceRoot;
    private readonly string _outputRoot;
    private readonly IReadOnlyList<string> _inputRoots;
    private readonly HashSet<string> _declaredOutputs;
    private readonly Func<ShellCommandClass, TimeSpan> _commandCeiling;
    private readonly Func<string, Stream> _commandCaptureStreamFactory;
    private readonly Action<CancellationToken>? _beforeCommandTimeoutStartsForTest;
    private readonly GhPullRequestCreateProvenance? _pullRequestCreateProvenance;
    private readonly IReadOnlyList<string> _directGhPrefixArguments;
    private readonly Dictionary<string, RetainedCommandOutput> _commandOutputs =
        new(StringComparer.Ordinal);

    /// <summary>
    /// #2002 rules 2/2b. One ledger per policy object, i.e. per dispatch, which is what makes "per
    /// room" true without any disk state. See <see cref="RepeatedToolCallLedger"/> for the two
    /// predicates and for how the hooks' vendors reach the same rung over a persisted file.
    /// </summary>
    private readonly RepeatedToolCallLedger _repeats;

    /// <summary>
    /// #2001: null when this grant is not governed — <see cref="OwnPullRequestOnlyRule.AppliesTo"/>
    /// states the condition. One instance per policy, because the rule carries the room's own PR
    /// number and learns it from a command this policy ran.
    /// </summary>
    private readonly OwnPullRequestOnlyRule? _ownPullRequestOnly;

    /// <param name="commandCeiling">
    /// How long one <c>baton_run_command</c> of a given class may run before Baton kills its process
    /// tree; null is <see cref="ShellCommandCeilings.For"/>, the production table. A delegate rather
    /// than constants so the timeout arm is exercisable in a second instead of minutes — a test that
    /// cannot reach it is how the timeout came to be reported as a grant refusal (#1921 review HIGH) —
    /// and per-CLASS since #1998, so a test can also show the classes actually differ rather than only
    /// that one of them fires.
    /// </param>
    /// <param name="timeProvider">
    /// #2002: the clock the repeat window is measured against. Same reason as
    /// <paramref name="commandCeiling"/> — a 60-second window a test cannot advance is a rule no test
    /// can falsify.
    /// </param>
    public CodexDynamicToolPolicy(
        PermissionGrant grant,
        string? workingDirectory,
        string outputDirectory,
        IEnumerable<string> inputPaths,
        IEnumerable<string> producedOutputNames,
        Func<ShellCommandClass, TimeSpan>? commandCeiling = null,
        TimeProvider? timeProvider = null,
        GhPullRequestCreateProvenance? pullRequestCreateProvenance = null)
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
        _commandCaptureStreamFactory = CreateCommandCaptureStream;
        _beforeCommandTimeoutStartsForTest = null;
        _pullRequestCreateProvenance = ValidatePullRequestCreateProvenance(
            pullRequestCreateProvenance, _workspaceRoot);
        _directGhPrefixArguments = [];
        _repeats = new RepeatedToolCallLedger(timeProvider);
        _ownPullRequestOnly = OwnPullRequestOnlyRule.AppliesTo(grant) ? new OwnPullRequestOnlyRule() : null;
    }

    /// <summary>
    /// Test-only seams for a capture destination that fails while the child is running and for a
    /// rendezvous immediately before the real command timeout is armed. Production uses neither.
    /// </summary>
    internal CodexDynamicToolPolicy(
        PermissionGrant grant,
        string? workingDirectory,
        string outputDirectory,
        IEnumerable<string> inputPaths,
        IEnumerable<string> producedOutputNames,
        Func<ShellCommandClass, TimeSpan>? commandCeiling,
        TimeProvider? timeProvider,
        Func<string, Stream>? commandCaptureStreamFactory,
        Action<CancellationToken>? beforeCommandTimeoutStartsForTest = null,
        GhPullRequestCreateProvenance? pullRequestCreateProvenance = null,
        IReadOnlyList<string>? directGhPrefixArguments = null)
        : this(
            grant, workingDirectory, outputDirectory, inputPaths, producedOutputNames,
            commandCeiling, timeProvider, pullRequestCreateProvenance)
    {
        _commandCaptureStreamFactory = commandCaptureStreamFactory ?? CreateCommandCaptureStream;
        _beforeCommandTimeoutStartsForTest = beforeCommandTimeoutStartsForTest;
        _directGhPrefixArguments = directGhPrefixArguments ?? [];
        if (_directGhPrefixArguments.Count > 0)
        {
            // Explicit fixture seam: production construction has no prefix and still requires the
            // resolved executable itself to be named gh/gh.exe. Tests may put a script interpreter
            // here so argv can be recorded without installing or invoking a real CLI.
            _pullRequestCreateProvenance = ValidatePullRequestCreateProvenance(
                pullRequestCreateProvenance, _workspaceRoot, requireGhFileName: false);
        }
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
            tools.Add(Function(
                ReadTextTool,
                "Read an allowed file by bounded character range. Path-only calls remain valid; "
                + "incomplete results name the next range.",
                ReadTextSchema()));
        }

        if (_grant.ReadFiles)
        {
            tools.Add(Function(ListFilesTool,
                "List UTF-8 source files below an allowed directory, skipping .git/bin/obj and binary files.",
                StringSchema("path", "Absolute or workspace-relative directory path.")));
            tools.Add(Function(SearchTextTool,
                "Search allowed UTF-8 source files for a literal string with bounded results; recursive "
                + "searches skip .git/bin/obj and binary files. Incomplete results name the next "
                + "zero-based matching-line position; positions are deterministic for the current "
                + "path-ordered contents and may shift when files change.",
                SearchTextSchema()));
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
            tools.Add(Function(
                ReadCommandOutputTool,
                "Read a scalar-safe bounded range from stdout or stderr retained by a command in this "
                + "authorized execution. The command is not replayed. Retention is limited to the first "
                + $"{MaxRetainedCommandChannelCharacters} characters per channel; command results report "
                + "any permanent loss beyond that limit.",
                ReadCommandOutputSchema()));
        }

        return tools;
    }

    /// <summary>
    /// #2008: the one IDENTIFYING argument of a dynamic-tool call, as a short string —
    /// <see cref="CodexAppServerBroker"/> stamps it on the room's <c>mcp_tool_call</c> items under
    /// <see cref="CodexUsageParser.ArgumentsIdentityField"/>, and
    /// <see cref="CodexUsageParser.ShellCommandLines"/> reads it back. It lives HERE rather than in the
    /// broker because this class declares the argument schema in
    /// <see cref="BuildToolDefinitions"/> and consumes it in <see cref="ExecuteAsync"/>; a third copy
    /// of "which key names the target" in the broker is exactly the drift <c>record-once</c> forbids.
    /// <para>
    /// <b>Which key, per tool, and nothing else</b>: the command line for
    /// <see cref="RunCommandTool"/>, the opaque reference for <see cref="ReadCommandOutputTool"/>,
    /// the path for the read/list/search/write-text tools, the declared output name for
    /// <see cref="WriteOutputTool"/>, and the patched paths for
    /// <see cref="ApplyPatchTool"/> (whose sole argument is the whole envelope, so the paths are read
    /// out of it with the same parser that applies it). <c>content</c> and the patch body are NEVER
    /// emitted: the stream-size constraint that made the sibling field a DIGEST rather than the
    /// arguments applies unchanged here, and it is stated once beside that field in
    /// <c>CodexAppServerBroker.Describe</c>.
    /// </para>
    /// <para>
    /// <b>Truncated at <see cref="MaxIdentityCharacters"/></b> with a trailing <c>…</c>, because a
    /// command line has no bound of its own and this field must not become the thing the digest exists
    /// to avoid. That cap costs the dominant-shape reading it feeds nothing:
    /// <c>Status.CommandShape.Normalize</c> reads the whole string but caps its own output at
    /// <c>CommandShape.MaxShapeLength</c> (80), so anything this truncates was already past that
    /// consumer's own cut. Equality comparisons use the digest, never this. Null — the field is then
    /// simply absent — for a tool with no identifying key, for an argument object that carries none,
    /// and for an <see cref="ApplyPatchTool"/> envelope this parser cannot read: an absent field is
    /// honest, an invented one is not.
    /// </para>
    /// <para>
    /// <b>The <c>_ =&gt; null</c> arm is reachable and is the deliberate limit of the claim.</b> A tool
    /// name Baton implements nowhere — the hallucinated or stale name <see cref="DescribeUnknownTool"/>
    /// answers, five of which #1920 measured on one arm — is announced as an <c>mcp_tool_call</c> pair
    /// before <see cref="ExecuteAsync"/> ever rejects it, and those two items carry a digest and no
    /// identity. Scraping one out of raw arguments for a name with no known schema would reintroduce
    /// exactly the whole-file-into-the-stream bound this method exists to hold.
    /// </para>
    /// </summary>
    internal static string? InputIdentity(string toolName, JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var identity = toolName switch
        {
            RunCommandTool => OptionalString(arguments, "command"),
            ReadTextTool or ListFilesTool or SearchTextTool or WriteTextTool =>
                OptionalString(arguments, "path"),
            ReadCommandOutputTool => OptionalString(arguments, "reference"),
            WriteOutputTool => OptionalString(arguments, "name"),
            ApplyPatchTool => PatchedPaths(OptionalString(arguments, "input")),
            _ => null,
        };

        return identity is not { Length: > 0 }
            ? null
            : identity.Length <= MaxIdentityCharacters
                ? identity
                : identity[..MaxIdentityCharacters] + "…";
    }

    /// <summary>
    /// The paths an <c>apply_patch</c> envelope touches, in envelope order, space-separated. Parsed
    /// with <see cref="CodexApplyPatch.Parse"/> — the same reader <see cref="ApplyPatch"/> applies the
    /// envelope with, so the recorded identity cannot name a path the write did not touch. That parser
    /// throws <see cref="ArgumentException"/> on a malformed envelope, which here is not an error: the
    /// call will fail on its own in <see cref="ExecuteAsync"/>, and the item simply carries no identity
    /// rather than a guess scraped out of unparseable text.
    /// </summary>
    private static string? PatchedPaths(string? input)
    {
        if (input is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            return string.Join(' ', CodexApplyPatch.Parse(input).Select(operation => operation.Path));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string? OptionalString(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private const int MaxIdentityCharacters = 512;

    public async Task<CodexDynamicToolResult> ExecuteAsync(
        string toolName, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            return toolName switch
            {
                ReadTextTool => ReadText(
                    RequiredString(arguments, "path"),
                    OptionalInteger(arguments, "offset"),
                    OptionalInteger(arguments, "length")),
                ListFilesTool => ListFiles(RequiredString(arguments, "path")),
                SearchTextTool => SearchText(
                    RequiredString(arguments, "path"),
                    RequiredString(arguments, "query"),
                    OptionalInteger(arguments, "start")),
                ReadCommandOutputTool => ReadCommandOutput(
                    RequiredString(arguments, "reference"),
                    RequiredString(arguments, "channel"),
                    OptionalInteger(arguments, "offset"),
                    OptionalInteger(arguments, "length")),
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
            return CodexDynamicToolResult.Refused(ex.Message, ex.Rule);
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
    // direction. Pinned since #1972 by
    // CodexDynamicToolPolicyTests.Only_a_write_shaped_unknown_tool_name_is_answered_about_the_write_path,
    // so re-adding either fragment fails a test rather than only contradicting this line.
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

    private CodexDynamicToolResult ReadText(string requestedPath, int? requestedOffset, int? requestedLength)
    {
        if (!_grant.ReadFiles && _inputRoots.Count == 0)
        {
            return CodexDynamicToolResult.Refused(
                "This Baton role does not grant file reads.", GrantRules.WithheldTool);
        }

        var path = ResolveAllowedRead(requestedPath);
        if (!File.Exists(path))
        {
            // A missing file is a failure of an ALLOWED read, not a refusal: the grant let this path
            // through and the workspace had nothing there. Same distinction at every Failed below.
            return CodexDynamicToolResult.Failed($"File '{requestedPath}' does not exist.");
        }

        EnsureNoReparsePoint(path);

        var offset = requestedOffset ?? 0;
        var length = requestedLength ?? MaxReadRangeCharacters;
        if (offset < 0)
        {
            return CodexDynamicToolResult.Failed("Read range offset must be zero or greater.");
        }
        if (length is < 1 or > MaxReadRangeCharacters)
        {
            return CodexDynamicToolResult.Failed(
                $"Read range length must be between 1 and {MaxReadRangeCharacters} characters.");
        }

        // #2002 rule 2b. Stat BEFORE serving, which is the whole predicate — see
        // RepeatedToolCallLedger for why a read is judged on the stat pair and a command on a clock.
        // The population: the measured agy rooms re-opened their own `task-N.log` 22-25 times.
        var info = new FileInfo(path);
        var text = File.ReadAllText(path, Encoding.UTF8);
        if (offset > text.Length)
        {
            return CodexDynamicToolResult.Failed(
                $"Read range offset {offset} is past the file's {text.Length} characters.");
        }
        if (IsBetweenSurrogates(text, offset))
        {
            return CodexDynamicToolResult.Failed(
                $"Read range offset {offset} is not a Unicode scalar boundary.");
        }

        var take = (int)Math.Min((long)length, text.Length - (long)offset);
        if (IsBetweenSurrogates(text, offset + take))
        {
            // A requested UTF-16 character count can land between a surrogate pair. Include the
            // scalar's low surrogate and report that actual end in the continuation footer.
            take++;
        }

        var replayPreamble = $"[{RepeatedToolCallLedger.ReadReplayPreamble}]\n";
        // Repeat identity is the window actually served, not the wider request that arrived. Reserve
        // replay metadata even on the first answer so adding it on a replay cannot move that window;
        // requests that converge to the same bounded, scalar-safe content are then one question.
        var responseBudget = MaxDiscoveryResponseCharacters - replayPreamble.Length;
        string? footer = null;
        if (offset + take < text.Length || take > responseBudget)
        {
            // The footer's digits can change when `take` is reduced, so converge once more if needed.
            while (true)
            {
                var end = offset + take;
                var nextLength = Math.Min(MaxReadRangeCharacters, text.Length - end);
                footer = $"[incomplete: returned file characters {offset}..{end - 1} of {text.Length}; "
                         + $"next range: offset={end}, length={nextLength}]";
                var boundedTake = Math.Min(
                    take, Math.Max(0, responseBudget - footer.Length - 1));
                if (IsBetweenSurrogates(text, offset + boundedTake))
                {
                    boundedTake--;
                }
                if (boundedTake == take)
                {
                    break;
                }
                take = boundedTake;
            }
        }

        var repeat = _repeats.ClassifyRead(
            path, info.LastWriteTimeUtc, info.Length, $"offset={offset};end={offset + take}");
        if (repeat.Verdict == RepeatVerdict.Refuse)
        {
            return CodexDynamicToolResult.Refused(repeat.Reason!, GrantRules.Repeat);
        }

        Debug.Assert(repeat.Verdict != RepeatVerdict.Replay
                     || repeat.Preamble == RepeatedToolCallLedger.ReadReplayPreamble);
        var preamble = repeat.Verdict == RepeatVerdict.Replay ? replayPreamble : string.Empty;
        var range = text.Substring(offset, take);
        var displayed = preamble + (footer is null ? range : range + '\n' + footer);
        Debug.Assert(displayed.Length <= MaxDiscoveryResponseCharacters);

        // The replay preamble rides bytes this call has just taken off disk; RepeatedToolCallLedger's
        // remarks say why a read entry deliberately holds no copy of them.
        return CodexDynamicToolResult.Allowed(displayed);
    }

    private CodexDynamicToolResult ListFiles(string requestedPath)
    {
        if (!_grant.ReadFiles)
        {
            return CodexDynamicToolResult.Refused(
                "This Baton role does not grant workspace file listing.", GrantRules.WithheldTool);
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

    private CodexDynamicToolResult SearchText(string requestedPath, string query, int? requestedStart)
    {
        if (!_grant.ReadFiles)
        {
            return CodexDynamicToolResult.Refused(
                "This Baton role does not grant workspace text search.", GrantRules.WithheldTool);
        }
        if (query.Length == 0)
        {
            return CodexDynamicToolResult.Failed("Search query must not be empty.");
        }
        var start = requestedStart ?? 0;
        if (start < 0)
        {
            return CodexDynamicToolResult.Failed("Search start must be zero or greater.");
        }

        var path = ResolveWithinWorkspace(requestedPath);
        EnsureNoReparsePoint(path);
        IEnumerable<string> files = File.Exists(path)
            ? IsUtf8TextFile(path) ? [path] : []
            : Directory.Exists(path)
                ? EnumerateContentFiles(path, SafeEnumerationOptions())
                : throw new ArgumentException($"Search path '{requestedPath}' does not exist.");
        files = files.OrderBy(file => file, PathComparer);

        var matches = new StringBuilder(MaxDiscoveryResponseCharacters);
        var shown = 0;
        var matchIndex = 0;
        var truncatedSnippets = 0;
        string? incompleteReason = null;
        int? nextStart = null;
        foreach (var file in files)
        {
            try
            {
                var text = File.ReadAllText(file, StrictUtf8);
                foreach (var line in EnumerateLines(text))
                {
                    var lineText = text.AsSpan(line.Start, line.Length);
                    var matchOffset = lineText.IndexOf(query.AsSpan(), StringComparison.Ordinal);
                    if (matchOffset < 0)
                    {
                        continue;
                    }
                    if (matchIndex < start)
                    {
                        matchIndex++;
                        continue;
                    }
                    if (shown >= MaxSearchMatches)
                    {
                        incompleteReason = $"match limit of {MaxSearchMatches} reached";
                        nextStart = matchIndex;
                        break;
                    }

                    var rendered = RenderSearchMatch(file, text, line, matchOffset);
                    var separatorLength = matches.Length == 0 ? 0 : 1;
                    if (matches.Length + separatorLength + rendered.Length + MaxSearchFooterCharacters
                        > MaxDiscoveryResponseCharacters)
                    {
                        incompleteReason =
                            $"response capped at {MaxDiscoveryResponseCharacters} characters after {shown} matches";
                        nextStart = matchIndex;
                        break;
                    }

                    if (matches.Length > 0)
                    {
                        matches.Append('\n');
                    }
                    matches.Append(rendered);
                    shown++;
                    matchIndex++;
                    truncatedSnippets += line.Length > MaxSearchLineSnippetCharacters ? 1 : 0;
                }
            }
            catch (DecoderFallbackException)
            {
                // A binary or non-UTF-8 file is not a match, not a reason to abort the whole search.
            }

            if (incompleteReason is not null)
            {
                break;
            }
        }

        var footer = incompleteReason is not null
            ? $"[incomplete: {incompleteReason}; next search: start={nextStart}. "
              + "The position applies to the current path-ordered contents; changed files may shift it.]"
            : truncatedSnippets > 0
                ? $"[complete search: {shown} matches; {truncatedSnippets} line snippets truncated. "
                  + "Use the baton_read_text ranges shown to retrieve omitted text.]"
                : $"[complete search: {shown} matches]";
        Debug.Assert(footer.Length <= MaxSearchFooterCharacters);
        if (matches.Length > 0)
        {
            matches.Append('\n');
        }
        matches.Append(footer);
        Debug.Assert(matches.Length <= MaxDiscoveryResponseCharacters);
        return CodexDynamicToolResult.Allowed(matches.ToString());
    }

    private string RenderSearchMatch(
        string file, string text, (int Number, int Start, int Length) line, int matchOffset)
    {
        var relative = Path.GetRelativePath(_workspaceRoot!, file).Replace('\\', '/');
        if (line.Length <= MaxSearchLineSnippetCharacters)
        {
            return $"{relative}:{line.Number}:{text.Substring(line.Start, line.Length)}";
        }

        var snippetStart = Math.Max(0, matchOffset - MaxSearchLineSnippetCharacters / 4);
        snippetStart = Math.Min(snippetStart, line.Length - MaxSearchLineSnippetCharacters);
        var fileOffset = line.Start + snippetStart;
        if (IsBetweenSurrogates(text, fileOffset))
        {
            fileOffset++;
            snippetStart++;
        }
        var snippetEnd = Math.Min(line.Start + line.Length, fileOffset + MaxSearchLineSnippetCharacters);
        if (IsBetweenSurrogates(text, snippetEnd))
        {
            snippetEnd--;
        }
        var snippet = text.Substring(fileOffset, snippetEnd - fileOffset);
        return $"{relative}:{line.Number}:[snippet truncated: zero-based line characters "
               + $"{snippetStart}..{snippetStart + snippet.Length - 1} of {line.Length}; "
               + $"read range: offset={fileOffset}, length={snippet.Length}] {snippet}";
    }

    private static bool IsBetweenSurrogates(string text, int offset) =>
        offset > 0 && offset < text.Length
        && char.IsHighSurrogate(text[offset - 1]) && char.IsLowSurrogate(text[offset]);

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
        _repeats.ForgetRead(path);
        _repeats.ForgetAllCommands();
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

        // #2002 rule 2b: the room's own write invalidates its own read, and not merely as
        // belt-and-braces on the stat check — RepeatedToolCallLedger.ForgetRead states the case that
        // check cannot see.
        _repeats.ForgetRead(path);

        // #2002 review HIGH: and it invalidates every remembered COMMAND output too. A build that
        // failed before this write is not the answer after it; replaying that failure and then
        // refusing with "the previous run is still the answer" was a plausible wrong answer, which is
        // the worst shape a defect can take. See ForgetAllCommands for the rule and its one exception.
        _repeats.ForgetAllCommands();
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

        // #2002 re-review HIGH: the same eviction WriteText does, on the tool codex is told to edit
        // WITH. Without it, a `dotnet build` that failed before the patch is replayed after it and
        // then refused with "nothing this room did since could have changed it" — a plausible wrong
        // answer, and this is the primary write path, not the secondary one.
        //
        // BEFORE the write loop, not after: this method's summary says an I/O error or the leaf
        // re-check can throw partway through, leaving earlier files on disk, and eviction after the
        // loop would then be skipped on exactly the tree that DID change. Evicting for a patch that
        // then fails costs a re-run, which is the direction ForgetAllCommands' own doc takes.
        // A Delete invalidates a cached read of that path as surely as a write does.
        foreach (var (_, path, _) in planned)
        {
            _repeats.ForgetRead(path);
        }
        _repeats.ForgetAllCommands();

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
        CodexDynamicToolResult.Refused(
            $"{WithheldWorkspaceWrite} {DescribeWritePath(DeclaredToolNames())}", GrantRules.WithheldTool);

    private CodexDynamicToolResult ReadCommandOutput(
        string reference, string channelName, int? requestedOffset, int? requestedLength)
    {
        if (!_grant.RunShellCommands)
        {
            return CodexDynamicToolResult.Refused(
                "This Baton role does not grant shell commands or their retained output.",
                GrantRules.WithheldTool);
        }

        if (!_commandOutputs.TryGetValue(reference, out var output))
        {
            // One answer for malformed, expired and foreign references: disclosing whether another
            // room owns a token would turn this recovery surface into a cross-room oracle.
            return CodexDynamicToolResult.Failed(
                "This command output reference is not available in the current authorized execution.");
        }

        var channel = channelName switch
        {
            "stdout" => output.Stdout,
            "stderr" => output.Stderr,
            _ => null,
        };
        if (channel is null)
        {
            return CodexDynamicToolResult.Failed("Command output channel must be 'stdout' or 'stderr'.");
        }

        var offset = requestedOffset ?? 0;
        var length = requestedLength ?? MaxReadRangeCharacters;
        if (offset < 0)
        {
            return CodexDynamicToolResult.Failed("Command output range offset must be zero or greater.");
        }
        if (length is < 1 or > MaxReadRangeCharacters)
        {
            return CodexDynamicToolResult.Failed(
                $"Command output range length must be between 1 and {MaxReadRangeCharacters} characters.");
        }

        if (offset > channel.RetainedCharacters)
        {
            return CodexDynamicToolResult.Failed(
                $"Command output range offset {offset} is past the retained {channelName} length "
                + $"of {channel.RetainedCharacters} characters.");
        }

        var take = (int)Math.Min((long)length, channel.RetainedCharacters - (long)offset);
        var windowStart = Math.Max(0, offset - 1);
        var windowEnd = Math.Min(channel.RetainedCharacters, offset + take + 1);
        var window = ReadVerifiedCommandWindow(channel, windowStart, windowEnd - windowStart);
        var localOffset = offset - windowStart;
        if (IsBetweenSurrogates(window, localOffset))
        {
            return CodexDynamicToolResult.Failed(
                $"Command output range offset {offset} is not a Unicode scalar boundary.");
        }
        if (IsBetweenSurrogates(window, localOffset + take))
        {
            take++;
        }

        var header = $"[command output {reference}; channel={channelName}; "
                     + $"retained={channel.RetainedCharacters} of produced={channel.TotalCharacters} characters]";
        var replayPreamble = $"[{RepeatedToolCallLedger.ReadReplayPreamble}]\n";
        var responseBudget = MaxCommandResponseCharacters - replayPreamble.Length;
        string? footer = null;
        while (true)
        {
            var end = offset + take;
            if (end < channel.RetainedCharacters)
            {
                var nextLength = Math.Min(MaxReadRangeCharacters, channel.RetainedCharacters - end);
                footer = $"[incomplete: returned retained {channelName} characters {offset}..{end - 1} "
                         + $"of {channel.RetainedCharacters}; next range: reference={reference}, channel={channelName}, "
                         + $"offset={end}, length={nextLength}]";
            }
            else if (channel.TotalCharacters > channel.RetainedCharacters)
            {
                footer = $"[retention loss: {channel.TotalCharacters - channel.RetainedCharacters} "
                         + $"{channelName} characters exceeded Baton's "
                         + $"{MaxRetainedCommandChannelCharacters}-character per-channel limit and "
                         + "cannot be recovered]";
            }
            else
            {
                footer = null;
            }

            var metadataLength = header.Length + 1 + (footer is null ? 0 : footer.Length + 1);
            var boundedTake = Math.Min(take, Math.Max(0, responseBudget - metadataLength));
            if (IsBetweenSurrogates(window, localOffset + boundedTake))
            {
                boundedTake--;
            }
            if (boundedTake == take)
            {
                break;
            }
            take = boundedTake;
        }

        var repeat = _repeats.ClassifyRead(
            channel.Path,
            DateTimeOffset.UnixEpoch,
            channel.ByteLength,
            $"reference={reference};channel={channelName};offset={offset};end={offset + take}");
        if (repeat.Verdict == RepeatVerdict.Refuse)
        {
            return CodexDynamicToolResult.Refused(repeat.Reason!, GrantRules.Repeat);
        }

        var range = window.Substring(localOffset, take);
        var body = header + '\n' + range + (footer is null ? string.Empty : "\n" + footer);
        var rendered = repeat.Verdict == RepeatVerdict.Replay ? replayPreamble + body : body;
        Debug.Assert(rendered.Length <= MaxCommandResponseCharacters);
        return CodexDynamicToolResult.Allowed(rendered);
    }

    private async Task<CodexDynamicToolResult> RunCommandAsync(
        string commandLine, CancellationToken cancellationToken)
    {
        if (!_grant.RunShellCommands)
        {
            return CodexDynamicToolResult.Refused(
                "This Baton role does not grant shell commands.", GrantRules.WithheldTool);
        }

        var decision = ShellCommandPatternMatcher.EvaluateChainedCommand(
            commandLine, _grant.ShellCommandPatterns, _grant.DeniedShellCommandPatterns,
            _grant.DeniedShellCommandExceptions);
        if (!decision.IsAllowed)
        {
            // #1920: the matcher's reason states the rule; this site knows the vendor, so it is where
            // the granted alternative gets named (see GrantedReadToolHint). #1972 replaced the
            // scoped/unscoped gate with the rung flag on all three producing sites at once.
            // HookCheckCommand's copy of this condition is where that ruling is recorded; codex is not
            // a bystander to it, since this vendor runs the same WorkerRoles.json review role.
            var reason = decision.Reason ?? "Baton denied the command line.";
            var declared = DeclaredToolNames();
            var alternative = decision.MatchedStandingDeny
                ? null
                : GrantedReadToolHint.Clause(
                    declared.Contains(ReadTextTool) ? ReadTextTool : null,
                    declared.Contains(SearchTextTool) ? SearchTextTool : null);
            return CodexDynamicToolResult.Refused(
                alternative is null ? reason : $"{reason}. {char.ToUpperInvariant(alternative[0])}{alternative[1..]}.",
                GrantRules.ShellPattern);
        }
        if (ShellCommandPatternMatcher.IsDeniedByOptionToken(commandLine, _grant.DeniedShellOptionTokens))
        {
            return CodexDynamicToolResult.Refused(
                "The command contains an option token denied by this Baton role.",
                GrantRules.DeniedOptionToken);
        }
        // #2001: last of the three command checks, because it is the narrowest — a `gh pr` read of a
        // pull request this room did not open. Refused rather than Failed so it lands in the same
        // refusal count as every other grant decision on this path.
        if (_ownPullRequestOnly?.Refuse(commandLine) is { } siblingPullRequestRefusal)
        {
            return CodexDynamicToolResult.Refused(
                siblingPullRequestRefusal, GrantRules.OwnPullRequestOnly);
        }

        // #2190: only this compiler can select the direct process path. Any create shape it
        // recognizes either becomes exact argv or is refused; it never falls through to the shell.
        var directCreate = DirectGhPullRequestCreate.Compile(commandLine, _pullRequestCreateProvenance);
        if (directCreate.Refusal is { } createRefusal)
        {
            return CodexDynamicToolResult.Refused(createRefusal, GrantRules.OwnPullRequestOnly);
        }
        var directCreateArguments = directCreate.Arguments;

        // #1998: the ceiling is per command CLASS. A shipping or gate command is known to be progressing
        // while it runs — a `git push` here spends most of its wall clock inside the repository's own
        // pre-push gate — so the flat ceiling killed finished work rather than runaway work. The classes,
        // the table that sorts a line into one, and every ceiling are in the engine; nothing about them
        // is restated on this path (spec/baton.md §9). Computed before the rule-1 refusal below because
        // that sentence quotes THIS command's ceiling, and quoting some other class's would be a figure
        // the model cannot act on.
        var commandClass = ShellCommandClassifier.Classify(commandLine);
        var ceiling = _commandCeiling(commandClass);

        // #2002 rule 1. After the grant decisions and before anything is spawned, so a refused
        // backgrounding attempt starts no process at all. The ceiling clause is composed here because
        // this is the path that enforces one: the figure is read off the delegate that kills the tree,
        // never transcribed (the timeout arm below reports the same value).
        // NativeShell, because the branch below spawns COMSPEC on Windows and /bin/sh elsewhere —
        // the same condition, read from the one place that states what each family does with a bare
        // `&` (#2002 review LOW).
        if (directCreateArguments is null
            && BackgroundingShapeDetector.Detect(commandLine, BackgroundingShapeDetector.NativeShell)
            is { } backgroundingShape)
        {
            return CodexDynamicToolResult.Refused(BackgroundingShapeDetector.Refusal(
                backgroundingShape,
                $"Baton kills this command's process tree only at its "
                + $"{ceiling.TotalMinutes:0.##}-minute tool limit, so a long build or test run "
                + "has room to finish in the foreground."),
                GrantRules.Backgrounding);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // #2002 rule 2. Refused rather than Failed on the third ask: this step bought no information
        // because Baton declined it, which is the population the refusal marker counts, and a lane
        // that spends five of six steps re-asking is exactly what that count exists to make visible.
        var repeat = _repeats.ClassifyCommand(commandLine);
        switch (repeat.Verdict)
        {
            case RepeatVerdict.Replay:
                var replayed = $"[{repeat.Preamble}]\n{repeat.ReplayedOutput}";
                return repeat.ReplayedSuccess
                    ? CodexDynamicToolResult.Allowed(replayed)
                    : CodexDynamicToolResult.Failed(replayed);
            case RepeatVerdict.Refuse:
                return CodexDynamicToolResult.Refused(repeat.Reason!, GrantRules.Repeat);
            case RepeatVerdict.Execute:
            default:
                break;
        }

        var startInfo = ChildProcessStartInfo.Create(
            directCreateArguments is not null
                ? _pullRequestCreateProvenance!.ExecutablePath
                : OperatingSystem.IsWindows()
                    ? Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe"
                    : "/bin/sh",
            info =>
        {
            info.WorkingDirectory = _workspaceRoot ?? _outputRoot;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.StandardOutputEncoding = Encoding.UTF8;
            info.StandardErrorEncoding = Encoding.UTF8;
        });
        if (directCreateArguments is not null)
        {
            foreach (var argument in _directGhPrefixArguments.Concat(directCreateArguments))
            {
                startInfo.ArgumentList.Add(argument);
            }
        }
        else if (OperatingSystem.IsWindows())
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

        var reference = "command-" + Guid.NewGuid().ToString("N");
        var stdoutPath = ResolveWithinRoot(_outputRoot, $".{reference}.stdout.log");
        var stderrPath = ResolveWithinRoot(_outputRoot, $".{reference}.stderr.log");
        EnsureNoReparsePoint(stdoutPath, includeLeaf: false);
        EnsureNoReparsePoint(stderrPath, includeLeaf: false);

        // Both durable sinks exist before spawn. If opening either fails, the command has not run and
        // an identical retry is safe. Faults after spawn take the recorded-failure path below.
        var stdoutDestination = _commandCaptureStreamFactory(stdoutPath);
        Stream stderrDestination;
        try
        {
            stderrDestination = _commandCaptureStreamFactory(stderrPath);
        }
        catch
        {
            await stdoutDestination.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new IOException("Baton could not start the granted command.");
        }
        catch
        {
            await stdoutDestination.DisposeAsync().ConfigureAwait(false);
            await stderrDestination.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        using var processLifetime = process;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var testRendezvous = _beforeCommandTimeoutStartsForTest;
        if (testRendezvous is null)
        {
            // Keep the production ordering unchanged: its ceiling is armed before capture starts.
            timeout.CancelAfter(ceiling);
        }
        var stdout = CaptureCommandChannelAsync(
            process.StandardOutput, stdoutPath, "stdout", stdoutDestination);
        var stderr = CaptureCommandChannelAsync(
            process.StandardError, stderrPath, "stderr", stderrDestination);
        // The callback is an internal fixture seam: waiting for a child-process marker here proves
        // the timer below begins after that marker, rather than merely hoping the child starts before
        // a timer that is already running. Capture begins first so this test-only seam shares the
        // command's post-spawn kill, drain, disposal and outcome-recording boundary. Production
        // construction always leaves the callback null and follows the unchanged arm above.
        string? timeoutFailure = null;
        var callerCancelled = false;
        Exception? captureFailure = null;
        Exception? rendezvousFailure = null;
        var rendezvousIsRunning = false;
        try
        {
            if (testRendezvous is not null)
            {
                rendezvousIsRunning = true;
                testRendezvous(cancellationToken);
                rendezvousIsRunning = false;
                timeout.CancelAfter(ceiling);
            }
            await WaitForExitOrCaptureFailureAsync(process, timeout.Token, stdout, stderr)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (
            rendezvousIsRunning && !cancellationToken.IsCancellationRequested)
        {
            KillProcessTree(process);
            rendezvousFailure = ex;
        }
        catch (OperationCanceledException) when (
            !rendezvousIsRunning && !cancellationToken.IsCancellationRequested)
        {
            KillProcessTree(process);
            // A timeout is a failure of a command the grant ALLOWED and Baton RAN. It costs the step
            // and carries its retained diagnostics, but nothing here declined it, so it carries no
            // refusal marker.
            timeoutFailure = ShellCommandCeilings.DescribeTimeout(commandClass, ceiling);
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
            callerCancelled = true;
        }
        catch (Exception ex) when (rendezvousIsRunning)
        {
            KillProcessTree(process);
            rendezvousFailure = ex;
        }
        catch (Exception ex) when (IsCommandCaptureFailure(ex))
        {
            KillProcessTree(process);
            captureFailure = ex;
        }

        RetainedCommandOutput? retained = null;
        string? stdoutText = null;
        string? stderrText = null;
        if (captureFailure is null)
        {
            try
            {
                retained = new RetainedCommandOutput(
                    reference,
                    await stdout.ConfigureAwait(false),
                    await stderr.ConfigureAwait(false));
                stdoutText = ReadVerifiedCommandWindow(
                    retained.Stdout, 0, retained.Stdout.RetainedCharacters);
                stderrText = ReadVerifiedCommandWindow(
                    retained.Stderr, 0, retained.Stderr.RetainedCharacters);
            }
            catch (Exception ex) when (IsCommandCaptureFailure(ex))
            {
                KillProcessTree(process);
                captureFailure = ex;
            }
        }

        if (captureFailure is not null)
        {
            await ObserveCaptureCompletionAsync(stdout, stderr).ConfigureAwait(false);
            var failedCapture = RenderCommandCaptureFailure(captureFailure);
            RecordExecutedCommandOutcome(commandLine, failedCapture, toolSucceeded: false);
            if (callerCancelled)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            return CodexDynamicToolResult.Failed(failedCapture);
        }

        _commandOutputs.Add(reference, retained!);

        if (directCreateArguments is not null
            && !callerCancelled && timeoutFailure is null && process.ExitCode == 0)
        {
            // The URL is attributable to the one preselected executable and exact argv above.
            // General-shell output never reaches this evidence producer.
            _ownPullRequestOnly?.Observe(PullRequestOwnershipEvidence.FromAttributedCreateOutput(
                _pullRequestCreateProvenance!.Repository, stdoutText + stderrText));
        }

        var status = callerCancelled
            ? "Command was cancelled after it started."
            : rendezvousFailure is not null
                ? RenderTestRendezvousFailure(rendezvousFailure)
                : timeoutFailure ?? $"Command exited {process.ExitCode}.";
        var displayed = RenderCommandResult(status, retained!, stdoutText!, stderrText!);
        if (callerCancelled)
        {
            RecordExecutedCommandOutcome(commandLine, displayed, toolSucceeded: false);
            throw new OperationCanceledException(cancellationToken);
        }
        if (timeoutFailure is not null)
        {
            RecordExecutedCommandOutcome(commandLine, displayed, toolSucceeded: false);
            return CodexDynamicToolResult.Failed(displayed);
        }
        if (rendezvousFailure is not null)
        {
            RecordExecutedCommandOutcome(commandLine, displayed, toolSucceeded: false);
            return CodexDynamicToolResult.Failed(displayed);
        }
        // #2002: a command is the broker's other write path, and the loud one -- see ForgetAllReads
        // and ForgetAllCommands. Completion says that capture is trustworthy; it says neither that
        // the exit was successful nor that a freshness-exempt command was mutation-free. Record the
        // actual tool disposition so a non-zero exit replays as a failure, and evict unrelated state
        // for every command that ran. The command's own failed entry remains available so a retry
        // cannot repeat side effects.
        RecordExecutedCommandOutcome(commandLine, displayed, toolSucceeded: process.ExitCode == 0);

        // A non-zero exit is the command's own answer, with bounded channel previews and a recovery
        // reference — `pixi run test` with three failing tests is the case that matters, and its
        // retained output IS the information the step bought.
        // Failed rather than Refused: stamping the refusal marker here counted every failing allowed
        // command as budget the grant declined (#1921 review HIGH).
        return process.ExitCode == 0
            ? CodexDynamicToolResult.Allowed(displayed)
            : CodexDynamicToolResult.Failed(displayed);
    }

    private async Task<RetainedCommandChannel> CaptureCommandChannelAsync(
        StreamReader reader, string path, string channel, Stream destination)
    {
        var buffer = new char[4096];
        long total = 0;
        var retained = 0;
        var retentionClosed = false;
        await using (var writer = new StreamWriter(
                         destination,
                         new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                         bufferSize: 4096))
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                total += read;
                if (retentionClosed)
                {
                    continue;
                }

                var available = MaxRetainedCommandChannelCharacters - retained;
                var take = Math.Min(available, read);
                if (take == available && take > 0 && char.IsHighSurrogate(buffer[take - 1]))
                {
                    // Stop before the scalar rather than persist half of it at the retention boundary.
                    take--;
                }
                if (take > 0)
                {
                    await writer.WriteAsync(buffer.AsMemory(0, take)).ConfigureAwait(false);
                    retained += take;
                }
                retentionClosed = take < read || retained >= MaxRetainedCommandChannelCharacters;
            }

            await writer.FlushAsync().ConfigureAwait(false);
        }

        var info = new FileInfo(path);
        if (info.Length > MaxRetainedCommandChannelCharacters * 3L)
        {
            throw new IOException("Retained command output exceeded its bounded UTF-8 storage size.");
        }
        using var stored = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096,
            FileOptions.SequentialScan);
        var digest = SHA256.HashData(stored);
        return new RetainedCommandChannel(channel, path, retained, total, info.Length, digest);
    }

    private static Stream CreateCommandCaptureStream(string path) => new FileStream(
        path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, bufferSize: 4096,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static GhPullRequestCreateProvenance? ValidatePullRequestCreateProvenance(
        GhPullRequestCreateProvenance? provenance, string? workspaceRoot, bool requireGhFileName = true)
    {
        if (provenance is null || workspaceRoot is null
            || OutsideWorkspaceExecutableResolver.TryValidateAbsolute(
                provenance.ExecutablePath,
                workspaceRoot,
                "gh",
                OperatingSystem.IsWindows(),
                requireGhFileName) is not { } executable
            || GitHubRepository.TryCanonicalize(provenance.Repository) is not { } repository
            || string.IsNullOrWhiteSpace(provenance.HeadBranch)
            || provenance.HeadBranch.Equals("HEAD", StringComparison.Ordinal))
        {
            return null;
        }

        return provenance with
        {
            ExecutablePath = executable,
            Repository = repository,
        };
    }

    private static async Task WaitForExitOrCaptureFailureAsync(
        Process process,
        CancellationToken cancellationToken,
        Task stdout,
        Task stderr)
    {
        var exit = process.WaitForExitAsync(cancellationToken);
        var pendingCaptures = new List<Task> { stdout, stderr };
        while (pendingCaptures.Count > 0)
        {
            var completed = await Task.WhenAny(pendingCaptures.Append(exit)).ConfigureAwait(false);
            if (completed == exit)
            {
                await exit.ConfigureAwait(false);
                return;
            }

            await completed.ConfigureAwait(false);
            pendingCaptures.Remove(completed);
        }

        await exit.ConfigureAwait(false);
    }

    private static async Task ObserveCaptureCompletionAsync(params Task[] captures)
    {
        foreach (var capture in captures)
        {
            try
            {
                await capture.ConfigureAwait(false);
            }
            catch (Exception ex) when (IsCommandCaptureFailure(ex))
            {
                // The first capture fault is already the recorded failure. This only observes its
                // sibling after the process tree has been killed so no task is left unobserved.
            }
        }
    }

    private static bool IsCommandCaptureFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or ObjectDisposedException
            or NotSupportedException or EncoderFallbackException
            or System.Security.SecurityException;

    private static string RenderCommandCaptureFailure(Exception failure)
    {
        var detail = RenderBoundedExceptionDetail(failure);
        return "Baton ran the command, but retaining its output failed. The command's outcome cannot "
               + "be recovered, and Baton will not run an identical retry because its side effects "
               + $"may already have happened. Capture failure: {detail}";
    }

    private static string RenderTestRendezvousFailure(Exception failure) =>
        "Baton ran the command, but its test-only pre-timeout rendezvous failed. Baton stopped the "
        + "process tree and will not run an identical retry because its side effects may already "
        + $"have happened. Rendezvous failure: {RenderBoundedExceptionDetail(failure)}";

    private static string RenderBoundedExceptionDetail(Exception failure)
    {
        var detail = $"{failure.GetType().Name}: {failure.Message}";
        const int maxDetailCharacters = 1_000;
        if (detail.Length > maxDetailCharacters)
        {
            var end = maxDetailCharacters - 1;
            if (char.IsHighSurrogate(detail[end - 1]))
            {
                end--;
            }
            detail = detail[..end] + "…";
        }
        return detail;
    }

    private void RecordExecutedCommandOutcome(string commandLine, string output, bool toolSucceeded)
    {
        // Reaching this method means Process.Start succeeded. Even a normally completed volatile
        // observation may have invoked a mutating helper, so freshness preference is never evidence
        // that unrelated cached answers remain safe. This deliberately pays one re-read/re-run after
        // every actual execution instead of maintaining a fragile command-syntax read-only list.
        _repeats.ForgetAllCommands(exceptCommandLine: commandLine);
        _repeats.ForgetAllReads();
        _repeats.RecordCommandOutput(commandLine, output, toolSucceeded);
    }

    private static string ReadVerifiedCommandWindow(
        RetainedCommandChannel channel, int windowStart, int windowLength)
    {
        EnsureNoReparsePoint(channel.Path);
        if (windowStart < 0 || windowLength < 0
            || (long)windowStart + windowLength > channel.RetainedCharacters)
        {
            throw new IOException("Retained command output metadata is invalid.");
        }

        using var stream = new FileStream(
            channel.Path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096,
            FileOptions.SequentialScan);
        if (stream.Length != channel.ByteLength
            || stream.Length > MaxRetainedCommandChannelCharacters * 3L)
        {
            throw new IOException("Retained command output changed after capture and is unavailable.");
        }

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var decoder = StrictUtf8.GetDecoder();
        var bytes = new byte[4096];
        var characters = new char[4098];
        var captured = new StringBuilder(windowLength);
        var charactersSeen = 0;
        var windowEnd = windowStart + windowLength;

        void Consume(ReadOnlySpan<char> decoded)
        {
            foreach (var character in decoded)
            {
                if (charactersSeen >= windowStart && charactersSeen < windowEnd)
                {
                    captured.Append(character);
                }
                charactersSeen++;
            }
        }

        int read;
        while ((read = stream.Read(bytes, 0, bytes.Length)) > 0)
        {
            hasher.AppendData(bytes, 0, read);
            var decoded = decoder.GetChars(bytes.AsSpan(0, read), characters, flush: false);
            Consume(characters.AsSpan(0, decoded));
        }
        var flushed = decoder.GetChars(ReadOnlySpan<byte>.Empty, characters, flush: true);
        Consume(characters.AsSpan(0, flushed));

        var actualDigest = hasher.GetHashAndReset();
        if (charactersSeen != channel.RetainedCharacters
            || !CryptographicOperations.FixedTimeEquals(actualDigest, channel.Sha256Digest))
        {
            throw new IOException("Retained command output changed after capture and is unavailable.");
        }
        return captured.ToString();
    }

    private static string RenderCommandResult(
        string status,
        RetainedCommandOutput output,
        string stdout,
        string stderr)
    {
        var rendered = new StringBuilder(MaxCommandResponseCharacters);
        rendered.AppendLine(status);
        rendered.Append("Command output reference: ").AppendLine(output.Reference);
        rendered.Append("Retrieve omitted retained output with ").Append(ReadCommandOutputTool)
            .AppendLine("; the command is not replayed.");
        AppendCommandChannelPreview(rendered, output.Reference, output.Stdout, stdout);
        AppendCommandChannelPreview(rendered, output.Reference, output.Stderr, stderr);
        Debug.Assert(rendered.Length <= MaxCommandResponseCharacters);
        return rendered.ToString().TrimEnd('\r', '\n');
    }

    private static void AppendCommandChannelPreview(
        StringBuilder rendered,
        string reference,
        RetainedCommandChannel channel,
        string text)
    {
        rendered.Append(channel.Name).Append(": ").Append(channel.TotalCharacters)
            .Append(" characters produced; ").Append(channel.RetainedCharacters).Append(" retained");
        if (channel.TotalCharacters > channel.RetainedCharacters)
        {
            rendered.Append("; ").Append(channel.TotalCharacters - channel.RetainedCharacters)
                .Append(" exceeded the ").Append(MaxRetainedCommandChannelCharacters)
                .Append("-character per-channel retention limit and are permanently unavailable");
        }
        rendered.AppendLine(".");

        if (text.Length == 0)
        {
            rendered.AppendLine("[no output]");
            return;
        }
        if (text.Length <= MaxCommandChannelPreviewCharacters)
        {
            rendered.AppendLine(text);
            return;
        }

        var headLength = Math.Min(MaxCommandChannelPreviewEdgeCharacters, text.Length);
        if (IsBetweenSurrogates(text, headLength))
        {
            headLength--;
        }
        var tailStart = Math.Max(headLength, text.Length - MaxCommandChannelPreviewEdgeCharacters);
        if (IsBetweenSurrogates(text, tailStart))
        {
            tailStart++;
        }
        var omitted = tailStart - headLength;
        rendered.Append(text.AsSpan(0, headLength)).AppendLine();
        rendered.Append("[... ").Append(omitted).Append(' ').Append(channel.Name)
            .Append(" characters omitted from display; retrieve from reference=")
            .Append(reference).Append(", channel=").Append(channel.Name)
            .Append(", offset=").Append(headLength).Append(", length=")
            .Append(Math.Min(MaxReadRangeCharacters, omitted)).AppendLine(" ...]");
        rendered.AppendLine(text[tailStart..]);
    }

    private sealed record RetainedCommandOutput(
        string Reference, RetainedCommandChannel Stdout, RetainedCommandChannel Stderr);

    private sealed record RetainedCommandChannel(
        string Name,
        string Path,
        int RetainedCharacters,
        long TotalCharacters,
        long ByteLength,
        byte[] Sha256Digest);

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
            + "inline instead.",
            GrantRules.PathOutsideRoots);
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
            throw new CodexGrantRefusedException(
                "This Baton worker has no workspace root.", GrantRules.PathOutsideRoots);
        }
        var candidate = ResolveCandidate(requestedPath);
        if (!IsWithin(candidate, _workspaceRoot))
        {
            throw new CodexGrantRefusedException(
                $"Path '{requestedPath}' is outside this Baton's workspace root.",
                GrantRules.PathOutsideRoots);
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
            throw new CodexGrantRefusedException(
                $"Path '{relativePath}' escapes its Baton root.", GrantRules.PathOutsideRoots);
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
                throw new CodexGrantRefusedException(
                    $"Path '{path}' crosses a symbolic link or reparse point.", GrantRules.ReparsePoint);
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

    private static IEnumerable<string> EnumerateContentFiles(string path, EnumerationOptions options)
    {
        if (IsGeneratedDirectory(Path.GetFileName(Path.TrimEndingDirectorySeparator(path))))
        {
            return [];
        }

        return Directory.EnumerateFiles(path, "*", options)
            .Where(file => !Path.GetRelativePath(path, file)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(IsGeneratedDirectory))
            .Where(IsUtf8TextFile);
    }

    private static bool IsGeneratedDirectory(string segment) =>
        segment.Equals(".git", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("obj", StringComparison.OrdinalIgnoreCase);

    private static bool IsUtf8TextFile(string path)
    {
        try
        {
            using var reader = new StreamReader(path, StrictUtf8, detectEncodingFromByteOrderMarks: false);
            Span<char> buffer = stackalloc char[4_096];
            int read;
            while ((read = reader.Read(buffer)) > 0)
            {
                foreach (var character in buffer[..read])
                {
                    if (character == '\0'
                        || (char.IsControl(character) && character is not ('\t' or '\r' or '\n' or '\f')))
                    {
                        return false;
                    }
                }
            }
            return true;
        }
        catch (DecoderFallbackException)
        {
            // Invalid UTF-8 is binary/non-source content for recursive discovery. Direct reads still
            // use the grant-checked ReadText path and deliberately retain its replacement decoding.
            return false;
        }
    }

    private static IEnumerable<(int Number, int Start, int Length)> EnumerateLines(string text)
    {
        var number = 1;
        var start = 0;
        while (start < text.Length)
        {
            var relativeEnd = text.AsSpan(start).IndexOfAny('\r', '\n');
            if (relativeEnd < 0)
            {
                yield return (number, start, text.Length - start);
                yield break;
            }

            var end = start + relativeEnd;
            yield return (number++, start, end - start);
            start = end + (text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n' ? 2 : 1);
        }
    }

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

    private static JsonObject ReadTextSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Absolute or workspace-relative file path.",
            },
            ["offset"] = new JsonObject
            {
                ["type"] = "integer",
                ["minimum"] = 0,
                ["description"] = "Optional zero-based character offset; defaults to 0.",
            },
            ["length"] = new JsonObject
            {
                ["type"] = "integer",
                ["minimum"] = 1,
                ["maximum"] = MaxReadRangeCharacters,
                ["description"] = $"Optional character count, 1..{MaxReadRangeCharacters}; defaults to the maximum.",
            },
        },
        ["required"] = new JsonArray("path"),
        ["additionalProperties"] = false,
    };

    private static JsonObject SearchTextSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Directory or file to search.",
            },
            ["query"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Literal text to find.",
            },
            ["start"] = new JsonObject
            {
                ["type"] = "integer",
                ["minimum"] = 0,
                ["description"] = "Optional zero-based matching-line position; defaults to 0.",
            },
        },
        ["required"] = new JsonArray("path", "query"),
        ["additionalProperties"] = false,
    };

    private static JsonObject ReadCommandOutputSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["reference"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Opaque reference returned by baton_run_command in this execution.",
            },
            ["channel"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("stdout", "stderr"),
                ["description"] = "Retained command channel to read.",
            },
            ["offset"] = new JsonObject
            {
                ["type"] = "integer",
                ["minimum"] = 0,
                ["description"] = "Optional zero-based retained-character offset; defaults to 0.",
            },
            ["length"] = new JsonObject
            {
                ["type"] = "integer",
                ["minimum"] = 1,
                ["maximum"] = MaxReadRangeCharacters,
                ["description"] = $"Optional character count, 1..{MaxReadRangeCharacters}; defaults to the maximum.",
            },
        },
        ["required"] = new JsonArray("reference", "channel"),
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

    private static int? OptionalInteger(JsonElement arguments, string name)
    {
        if (!arguments.TryGetProperty(name, out var value))
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result))
        {
            throw new ArgumentException($"Dynamic tool argument '{name}' must be an integer when provided.");
        }
        return result;
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
/// <param name="rule">
/// #2009: which <see cref="GrantRules"/> member decided, carried on the exception because the resolver
/// that throws is the only party that knows and <c>ExecuteAsync</c>'s catch is where the refusal is
/// built. Deriving it at that catch instead — from the method name, or from the message text — would
/// label every path boundary identically, which is the plausible-but-wrong answer this field exists to
/// avoid.
/// </param>
internal sealed class CodexGrantRefusedException(string message, GrantRule rule) : Exception(message)
{
    public GrantRule Rule { get; } = rule;
}

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
/// <param name="Rule">
/// #2009: which <see cref="GrantRules"/> member decided this call, for the <see cref="GrantDecision"/>
/// line <c>CodexAppServerBroker</c> writes into the room's captured stream. <see cref="Allowed"/> and
/// <see cref="Failed"/> both carry <see cref="GrantRules.Allowed"/>, which is the same distinction the
/// paragraph above draws: a failing allowed command is not a grant decision against it.
/// </param>
public sealed record CodexDynamicToolResult(bool Success, string Text, GrantRule Rule)
{
    public static CodexDynamicToolResult Allowed(string text) => new(true, text, GrantRules.Allowed);

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
    public static CodexDynamicToolResult Refused(string text, GrantRule rule) =>
        new(false, GrantRefusal.Stamp(text), rule);

    /// <summary>
    /// A tool call no grant decision answered, and that did not succeed: a non-zero exit, the command
    /// timeout, a missing file or directory, a malformed argument, an output name outside the worker
    /// contract, an I/O error, and the unknown-tool fallthrough — a name Baton implements nowhere, so
    /// there is no grant that could have offered or withheld it. Unsuccessful and <b>unmarked</b> — its
    /// payload is its reason, so it is neither a refusal nor an empty result, and it must not be counted
    /// as either.
    /// </summary>
    public static CodexDynamicToolResult Failed(string text) => new(false, text, GrantRules.Allowed);
}
