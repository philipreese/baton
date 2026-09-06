using System.Text.Json;

namespace Baton.Cli;

/// <summary>
/// <c>baton agy-hook-check</c> (#554): agy's counterpart to <see cref="HookCheckCommand"/>, same role
/// but registered by <see cref="Baton.Vendors.AgyWorkerAdapter"/> into a spawned agy worker's
/// <c>.agents/hooks.json</c> instead. Not an operator-facing subcommand — <c>agy</c> invokes it
/// itself, on every matched tool call, and it receives the event JSON on stdin.
/// <para>
/// <b>Whether agy invokes it at all depends on how the command string is spelled, which is not this
/// command's business but is its precondition.</b> This code was correct throughout #710 and never
/// ran on Windows: agy passes the command to <c>cmd /c</c>, the assembly path was quoted, and
/// <c>dotnet</c> could not find it. See <c>AgyWorkerAdapter.HookAssemblyToken</c>, which owns
/// every constraint on that spelling and is where a change to it belongs.
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// <b>This is deliberately not a shared implementation with <see cref="HookCheckCommand"/>, and the
/// two must not be merged.</b> The vendors agree on the idea and on nothing else: agy nests the tool
/// name at <c>toolCall.name</c> in camelCase where claude puts <c>tool_name</c> at the root, and agy
/// signals a verdict by writing a <c>decision</c> field to <b>stdout</b> where claude signals denial
/// by <b>exiting 2</b> with stderr as the reason. Running claude's command under agy would exit 2,
/// print nothing agy parses, and — per the measured behaviour below — be read as an <i>allow</i>.
/// </para>
/// <para>
/// <b>Fail-closed, and it got here first.</b> <see cref="HookCheckCommand"/> used to fail open,
/// arguing that <c>--disallowedTools</c> independently covered the same tool names. That was never
/// available here: permission rules on agy are global-only (<c>agy.permissions-are-global-only</c>,
/// decision 0029), so this hook is the <i>only</i> per-worker gate and anything it lets through is
/// ungated outright. #649 voided the claude-side argument too — the write tools left that flag so the
/// hook could allow an outbox write — so both commands now deny on every payload they cannot judge.
/// </para>
/// <para>
/// <b>The specific failure that shapes this code</b> is measured by
/// <c>agy.hook-malformed-stdout-fails-open</c>: agy <i>allows</i> when hook stdout is unparseable or
/// absent, and <i>denies</i> when it parses but carries an unrecognised <c>decision</c> value. So a
/// crash, an unhandled exception writing a stack trace, or a silent exit is an <b>allow</b> — while
/// merely getting the verdict string wrong is safe. Everything here is therefore arranged so that a
/// syntactically valid JSON object reaches stdout on every path this process can control, including
/// the catch-all handler, and nothing else is ever written to stdout. <b>The honest exception:</b>
/// <see cref="StackOverflowException"/> cannot be caught in .NET, so that one path dies with empty
/// stdout and agy allows. Unavoidable in-process, and stated rather than papered over.
/// </para>
/// <para>
/// Like its claude sibling this enforces category denial by tool name, and since #679 reads one
/// argument — a granted write's target, to bound where it may land. It still does not attempt to
/// close the substitution gap (#529's claude-side analogue; #596 is the agy-side over-grant this hook
/// is a prerequisite for fixing): withholding a category while granting <c>run_command</c> leaves
/// that category reachable through the shell, and a granted shell reaches a write this bound refuses.
/// </para>
/// </remarks>
public static class AgyHookCheckCommand
{
    /// <summary>
    /// The environment variable carrying this invocation's denied-tool list — the same contract
    /// <see cref="HookCheckCommand.DeniedToolsEnvironmentVariable"/> uses, deliberately reused
    /// rather than duplicated per vendor: a worker is only ever one vendor, so the <i>values</i>
    /// differ (agy's <c>run_command</c> versus claude's <c>Bash</c>) while the channel need not.
    /// </summary>
    /// <remarks>
    /// That this channel works at all on agy is measured, not assumed:
    /// <c>agy.hook-env-inherited</c> (a sentinel) confirms an agy hook subprocess inherits the
    /// environment agy was spawned with. agy's own documentation
    /// (<c>.vendor-survey/corpus/agy__hooks.md</c>) is silent on inheritance — claude's states it
    /// explicitly — so carrying claude's answer across would have been exactly the population-scope
    /// mistake CLAUDE.md gate `claim-scope` names.
    /// </remarks>
    public const string DeniedToolsEnvironmentVariable =
        HookCheckCommand.DeniedToolsEnvironmentVariable;

    /// <summary>
    /// The environment variable carrying this invocation's shell pattern list (#659).
    /// </summary>
    public const string ShellPatternsEnvironmentVariable =
        HookCheckCommand.ShellPatternsEnvironmentVariable;

    /// <summary>
    /// Mirror-name for <see cref="Baton.Vendors.AgyWorkerAdapter.DeniedShellPatternsVariable"/> — the one
    /// canonical literal, owned there because that adapter emits it, read here. See it for what the
    /// channel is and why agy needs it.
    /// </summary>
    public const string DeniedShellPatternsEnvironmentVariable =
        Baton.Vendors.AgyWorkerAdapter.DeniedShellPatternsVariable;

    /// <summary>
    /// Mirror-name for <see cref="Baton.Vendors.AgyWorkerAdapter.DeniedShellOptionTokensVariable"/>
    /// (#1683 F2) — owned there because that adapter emits it, read here. See it for what the channel
    /// is and why neither vendor has a flag half for it.
    /// </summary>
    public const string DeniedShellOptionTokensEnvironmentVariable =
        Baton.Vendors.AgyWorkerAdapter.DeniedShellOptionTokensVariable;

    /// <summary>
    /// Mirror-name for <see cref="Baton.Vendors.AgyWorkerAdapter.VerdictLedgerVariable"/> (#1680) —
    /// owned there because that adapter emits it, read here. See it and
    /// <see cref="Baton.Vendors.AgyHookVerdictLedger"/> for what the ledger is for.
    /// </summary>
    public const string VerdictLedgerEnvironmentVariable =
        Baton.Vendors.AgyWorkerAdapter.VerdictLedgerVariable;

    /// <summary>
    /// agy reads the verdict from stdout and the process exit code carries no gating meaning — a
    /// denial and an allow both exit 0. Compare <see cref="HookCheckCommand.DeniedExitCode"/>, where
    /// the exit code <i>is</i> the signal.
    /// </summary>
    public const int ExitCode = 0;

    private const string AllowJson = """{"decision":"allow"}""";

    /// <summary>
    /// The catch-all denial, preallocated as a constant so the handler that writes it performs no
    /// allocation, no serialization, and no string formatting.
    /// </summary>
    /// <remarks>
    /// An earlier version serialized a fresh object here, including the exception type name. An
    /// independent reviewer pointed out the two ways that defeats itself: under
    /// <see cref="OutOfMemoryException"/> — which <c>catch (Exception)</c> does catch — the
    /// serialization allocates and is liable to rethrow from inside the handler, leaving stdout
    /// empty; and if the first write had already emitted part of an object, a second serialized
    /// object concatenates onto it into invalid JSON. Both outcomes are an
    /// <b>allow</b> on this vendor (<c>agy.hook-malformed-stdout-fails-open</c>). Losing the
    /// exception type from the reason is worth that.
    /// </remarks>
    private const string FallbackDenyJson =
        """{"decision":"deny","reason":"AER: the permission gate failed internally and denied this call rather than allowing it unchecked."}""";

    /// <summary>
    /// Runs the check, writing exactly one JSON object to <paramref name="stdout"/>. Takes its
    /// streams and the raw env value as parameters rather than touching <see cref="Console"/> or
    /// <see cref="Environment"/> directly, so the decision logic is testable without a subprocess.
    /// </summary>
    public static int Execute(
        TextReader stdin, TextWriter stdout, string? deniedToolsRaw, string? shellPatternsRaw = null,
        string? outboxDirectory = null, string? workspaceDirectory = null,
        string? deniedShellPatternsRaw = null, string? deniedShellOptionTokensRaw = null,
        string? verdictLedgerPath = null)
    {
        ArgumentNullException.ThrowIfNull(stdin);
        ArgumentNullException.ThrowIfNull(stdout);

        try
        {
            stdout.Write(Decide(
                stdin, deniedToolsRaw, shellPatternsRaw, outboxDirectory, workspaceDirectory,
                deniedShellPatternsRaw, deniedShellOptionTokensRaw));
        }
        catch
        {
            // Reaching here means a defect in Decide -- every payload shape agy can send is answered
            // there, including a non-string toolCall.name, which is checked at the guard rather than
            // left to reach this handler by way of GetString's InvalidOperationException. But an
            // exception escaping this method would
            // print a .NET stack trace to stderr, leave stdout empty, and be read by agy as an ALLOW
            // (`agy.hook-malformed-stdout-fails-open`). On the vendor where this hook is the only
            // gate, a bug must not silently widen the grant it was installed to narrow.
            //
            // A preallocated constant, and the exception is deliberately not inspected: see
            // FallbackDenyJson's own remarks. Under memory pressure, formatting a message here is
            // the thing most likely to rethrow and leave stdout empty -- the failure this handler
            // exists to prevent.
            stdout.Write(FallbackDenyJson);
        }
        finally
        {
            // #1680's first-verdict canary: one line per verdict this process actually reached stdout
            // for, allow or deny (including the fallback deny above -- it is still a verdict, just an
            // internally-forced one). Deliberately in `finally`, not inside the try above: a ledger
            // write must never compete with the stdout write for the same exception handling, and its
            // own failure (below) is swallowed rather than risking a SECOND exception mid-handler.
            AppendVerdictLedgerLine(verdictLedgerPath);
        }

        return ExitCode;
    }

    /// <summary>
    /// Best-effort and silently so: a ledger write that throws must never turn an already-written
    /// stdout verdict into a process that also crashes, and its failure mode (the canary undercounts
    /// verdicts) is fail-closed on the read side — see <c>AgyHookVerdictLedger</c>'s own remarks.
    /// </summary>
    private static void AppendVerdictLedgerLine(string? verdictLedgerPath)
    {
        if (string.IsNullOrWhiteSpace(verdictLedgerPath))
        {
            return;
        }

        try
        {
            File.AppendAllText(verdictLedgerPath, DateTimeOffset.UtcNow.ToString("O") + Environment.NewLine);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (ArgumentException)
        {
            // e.g. a path with characters this filesystem rejects -- unreachable with AER's own
            // ledger path today, but this method must never throw, whatever the input.
        }
    }

    private static string Decide(
        TextReader stdin, string? deniedToolsRaw, string? shellPatternsRaw,
        string? outboxDirectory, string? workspaceDirectory, string? deniedShellPatternsRaw,
        string? deniedShellOptionTokensRaw)
    {
        // Drain stdin first and unconditionally: agy is the writer on the other end of this pipe,
        // and exiting before reading its full payload risks a blocked write on its side for any
        // tool_input large enough to fill the pipe buffer.
        string input;
        try
        {
            input = stdin.ReadToEnd();
        }
        catch (IOException)
        {
            // Could not read the payload, so the tool name is unknowable and this cannot be judged.
            // The claude sibling allows here; this must not.
            return DenyJson("AER: the permission gate could not read the hook payload and denied " +
                            "this call rather than allowing it unchecked.");
        }

        var deniedList = DeniedToolList.Parse(deniedToolsRaw, VendorTag);
        if (deniedList.Status != DeniedToolListStatus.Present)
        {
            // #600: absent, or claude's list rather than agy's. Either way this gate cannot say what
            // is withheld. It used to allow, which made a channel that had stopped working look
            // exactly like one that was — the failure `agy.hook-env-inherited` is a sentinel for. On
            // this vendor there is no backstop under --dangerously-skip-permissions, so the safe
            // direction is the only defensible one.
            return DenyJson(
                deniedList.Status == DeniedToolListStatus.Absent
                    ? "AER: the permission gate did not receive its denied-tool list and denied this " +
                      "call rather than allowing it unchecked."
                    : "AER: the permission gate received another vendor's denied-tool list, whose tool " +
                      "names it cannot judge, and denied this call rather than allowing it unchecked.");
        }

        // Parsed here; judged inside the run_command branch below. This channel gates exactly one
        // tool, so its state is not allowed to decide the verdict for any other tool — a non-Present
        // list denies run_command (see there) but leaves view_file et al. to the denied-tool channel.
        var shellPatternList = ShellPatternList.Parse(shellPatternsRaw, VendorTag);

        // The DenyAlways channel (#390), parsed the same way and judged in the same branch. Deny beats
        // allow, so it is checked BEFORE the allow-narrowing below: a standing "never" refuses the
        // command even when the shell is otherwise (or unscoped-ly) granted.
        var deniedShellPatternList = ShellPatternList.Parse(deniedShellPatternsRaw, VendorTag);

        // #679 removed the early allow for an empty list here as on claude; see
        // HookCheckCommand.Decide for why, and for what an empty list cannot be told apart from.
        var denied = deniedList.Tools;

        if (string.IsNullOrWhiteSpace(input))
        {
            return DenyJson("AER: the permission gate received an empty hook payload and denied " +
                            "this call rather than allowing it unchecked.");
        }

        string? toolName;
        string? commandLine = null;
        string? writeTarget = null;
        // Captured here because the JsonDocument is disposed before the denial below is built, and
        // a denial that cannot name what the payload carried is the misdirection #708 was made of.
        var argKeys = "no args object at all";
        try
        {
            using var doc = JsonDocument.Parse(input);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("toolCall", out var toolCall) ||
                toolCall.ValueKind != JsonValueKind.Object ||
                !toolCall.TryGetProperty("name", out var nameProp) ||
                nameProp.ValueKind != JsonValueKind.String)
            {
                return DenyJson("AER: the permission gate could not find toolCall.name in the hook " +
                                "payload and denied this call rather than allowing it unchecked.");
            }

            toolName = nameProp.GetString();
            if (toolName == "run_command" && toolCall.TryGetProperty("args", out var args) &&
                args.ValueKind == JsonValueKind.Object &&
                args.TryGetProperty("CommandLine", out var cmdProp) &&
                cmdProp.ValueKind == JsonValueKind.String)
            {
                commandLine = cmdProp.GetString();
            }

            writeTarget = ReadWriteTarget(toolCall, toolName);
            argKeys = DescribeArgKeys(toolCall);
        }
        catch (JsonException)
        {
            return DenyJson("AER: the permission gate could not parse the hook payload and denied " +
                            "this call rather than allowing it unchecked.");
        }

        if (string.IsNullOrEmpty(toolName))
        {
            return DenyJson("AER: the permission gate read an empty tool name from the hook payload " +
                            "and denied this call rather than allowing it unchecked.");
        }

        if (IsWithheld(denied, toolName))
        {
            return DenyJson($"AER: the '{toolName}' tool is withheld by this session's permission grant.");
        }

        if (toolName == "run_command")
        {
            // The shell channel gates this tool. A non-Present list means the gate cannot judge the
            // command's scope, so it denies rather than let an unjudged shell through. Absent = the
            // channel broke: AgyWorkerAdapter always emits this variable alongside the denied-tool
            // one, so its absence is the same fail-open #679 closed for denied tools, NOT an unscoped
            // grant — an unscoped grant is Present with an empty pattern set ("agy:"). WrongVendor =
            // another vendor's patterns this gate cannot read. Either way, deny run_command.
            if (shellPatternList.Status != ShellPatternListStatus.Present)
            {
                return DenyJson(
                    shellPatternList.Status == ShellPatternListStatus.Absent
                        ? "AER: the permission gate did not receive its shell pattern list and denied this " +
                          "run_command call rather than allowing it unchecked."
                        : "AER: the permission gate received another vendor's shell pattern list, whose " +
                          "patterns it cannot judge, and denied this run_command call rather than allowing " +
                          "it unchecked.");
            }

            // The DenyAlways channel is emitted alongside the allow one, so a non-Present state is the
            // same broken-channel failure #679 closed for denied tools — fail closed rather than skip a
            // standing "never" we cannot read.
            if (deniedShellPatternList.Status != ShellPatternListStatus.Present)
            {
                return DenyJson(
                    deniedShellPatternList.Status == ShellPatternListStatus.Absent
                        ? "AER: the permission gate did not receive its denied shell pattern list and denied " +
                          "this run_command call rather than allowing it unchecked."
                        : "AER: the permission gate received another vendor's denied shell pattern list, whose " +
                          "patterns it cannot judge, and denied this run_command call rather than allowing it " +
                          "unchecked.");
            }

            // Deny beats allow (0022, #390): a standing "never" refuses the command BEFORE any
            // allow-narrowing, so an unscoped grant (empty allow list) or a matching allow pattern
            // cannot reopen a family the operator has closed.
            //
            // #1685: routed through the same top-level chained-command segmentation the claude hook
            // uses (ShellCommandPatternMatcher.EvaluateChainedCommand, shared rather than copied —
            // see its own remarks for the segmentation rule and what it fails closed on). Evaluating
            // the whole line as one string here used to mean the metacharacter scan inside
            // IsDenied/IsAllowed rejected a chained line (`git push --force && true`) before either
            // pattern list was even consulted, so a standing 'never' silently stopped applying the
            // moment a command carried a chain operator.
            //
            // Matching the claude hook (HookCheckCommand.Decide, the toolName == "Bash" branch, #1733):
            // both hooks engage this rung whenever either list is non-empty, neither nested under the
            // other's allow list. Agy's remaining difference from claude is that it has no
            // --disallowedTools-level backstop for the unchained case, so this hook is its only
            // enforcement of the DenyAlways rung, and it is an anchored-prefix one — on an unscoped
            // grant no allow list stands behind it (see this matcher's class comment on what a deny
            // pattern cannot bind). spec/baton.md §9 is the canonical record of why that gap is safe.
            if (deniedShellPatternList.Patterns.Count > 0 || shellPatternList.Patterns.Count > 0)
            {
                if (commandLine is null)
                {
                    return DenyJson(
                        "AER: the 'run_command' tool is gated by this session's shell command patterns " +
                        "(allowed, denied, or both), but this gate could not read toolCall.args.CommandLine " +
                        "in the hook payload and denied this call rather than allowing it unchecked.");
                }

                var result = Baton.Vendors.ShellCommandPatternMatcher.EvaluateChainedCommand(
                    commandLine, shellPatternList.Patterns, deniedShellPatternList.Patterns);

                if (!result.IsAllowed)
                {
                    // #1920: same append as the claude hook's Bash rung — the matcher states the rule,
                    // this site knows the vendor and names agy's own read/search tools, suppressed
                    // when this session withheld them, and (see there) only on a scoped grant, since
                    // an unscoped one reaches this rung for a write-shaped standing deny.
                    var alternative = shellPatternList.Patterns.Count > 0
                        ? Baton.Vendors.GrantedReadToolHint.ForAgy(tool => IsWithheld(denied, tool))
                        : null;
                    return DenyJson(
                        $"AER: the command line '{commandLine}' is denied under this session's shell " +
                        $"grant — {result.Reason}." +
                        (alternative is null ? string.Empty : $" To proceed, {alternative}."));
                }
            }

            // #1683 F3: fails closed on a non-Present status, matching its two sibling channels above
            // rather than skipping silently. Both adapters emit this variable unconditionally alongside
            // the other two ("agy:"/"claude:" at minimum), so today the three arrive or break together
            // and this is a no-op change in practice -- but the prior wording asserted that togetherness
            // as a guarantee the code did not have, in the fail-open direction, and this is the one rung
            // with no vendor-flag backstop on either vendor to catch a silent drift.
            var deniedOptionTokenList = ShellPatternList.Parse(deniedShellOptionTokensRaw, VendorTag);
            if (deniedOptionTokenList.Status != ShellPatternListStatus.Present)
            {
                return DenyJson(
                    deniedOptionTokenList.Status == ShellPatternListStatus.Absent
                        ? "AER: the permission gate did not receive its denied option token list and denied " +
                          "this run_command call rather than allowing it unchecked."
                        : "AER: the permission gate received another vendor's denied option token list, whose " +
                          "tokens it cannot judge, and denied this run_command call rather than allowing it " +
                          "unchecked.");
            }

            if (deniedOptionTokenList.Patterns.Count > 0)
            {
                if (commandLine is null)
                {
                    return DenyJson(
                        "AER: the 'run_command' tool has standing denied option tokens, but this gate could " +
                        "not read toolCall.args.CommandLine in the hook payload and denied this call rather " +
                        "than allowing it unchecked.");
                }

                if (Baton.Vendors.ShellCommandPatternMatcher.IsDeniedByOptionToken(
                        commandLine, deniedOptionTokenList.Patterns))
                {
                    return DenyJson(
                        $"AER: the command line '{commandLine}' carries an option this session's grant " +
                        "denies outright (a standing option-token 'never', matched anywhere on the line " +
                        "rather than at its start) and was refused.");
                }
            }
        }

        // #679, as on claude -- see HookCheckCommand's equivalent. It matters more here: nothing agy
        // offers bounds a write (`agy.plan-mode-does-not-deny-writes`), so this gate is the only one.
        if (WriteFamilyTools.Contains(toolName))
        {
            if (OutboxPath.IsInside(writeTarget, workspaceDirectory) ||
                OutboxPath.IsInside(writeTarget, outboxDirectory))
            {
                return AllowJson;
            }

            // Two DIFFERENT failures, and reporting them as one is why #708 needed a dedicated
            // audit pass to notice rather than the deny report pointing straight at itself.
            // "Your target is outside the outbox" is actionable. "Your target is outside
            // the outbox" when no target was ever read sends the reader to inspect their outbox
            // configuration, which is fine, and their write path, which is fine, while the real
            // cause is that this gate does not know which argument of this tool carries a path.
            //
            // So when the target is unreadable, name the argument keys that WERE present. The next
            // tool whose payload shape AER has not seen then self-diagnoses on first contact,
            // instead of presenting as a mysterious always-denied capability.
            if (writeTarget is null)
            {
                return DenyJson(
                    $"AER: the '{toolName}' tool is granted, but this gate could not read the path it " +
                    "writes to, so it cannot confirm the write lands in this worker's workspace or " +
                    $"outbox. Tried {FormatFields(toolName)}; the call carried {argKeys}. " +
                    "If one of those names the target, add it to AgyHookCheckCommand.WriteTargetFields.");
            }

            return DenyJson(
                $"AER: the '{toolName}' tool is granted, but its target ({writeTarget}) resolves " +
                "outside both this worker's workspace and its outbox. A grant decides whether a " +
                "worker may write, not where.");
        }

        return AllowJson;
    }

    /// <summary>
    /// The absolute path an agy write-family call targets, or <see langword="null"/> when this gate
    /// cannot read one — in which case the caller denies rather than allowing an unbounded write.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>toolCall.args.TargetFile</c>, PascalCase, measured by
    /// <c>agy.hook-payload-carries-write-path</c> against a live <c>write_to_file</c> call rather than
    /// read off agy's documentation — which <c>docs/vendor-doc-audit.md</c> records as wrong in two
    /// other places about this same CLI.
    /// </para>
    /// <para>
    /// <b>The field is per-tool, and assuming it was not cost a granted capability (#708).</b> This
    /// read <c>TargetFile</c> for every write-family tool. Three of the four carry that field;
    /// <c>generate_image</c> does not — its arguments are <c>Prompt</c>/<c>ImageName</c>/
    /// <c>ImagePaths</c> — so it resolved to <see langword="null"/> every time and the caller denied
    /// it unconditionally, including when the operator had granted writes. The denial even blamed the
    /// target for resolving outside the outbox, when the truth was that no target had been read.
    /// Fail-closed, which is why it survived unnoticed.
    /// </para>
    /// <para>
    /// <see cref="WriteTargetFields"/> is therefore explicit per tool, and
    /// <c>AgyHookCheckCommandTests</c> holds every <see cref="WriteFamilyTools"/> member to having an
    /// entry — so adding a write tool without saying which argument names its target is red rather
    /// than silently always-denied.
    /// </para>
    /// <para>
    /// <b>Both entries are payload-measured, not read off documentation.</b> <c>TargetFile</c> came
    /// from a live <c>write_to_file</c> call (<c>agy.hook-payload-carries-write-path</c>).
    /// <c>generate_image</c> was captured the same way on 2026-07-29 — a logging hook over a real
    /// image-generation call — and its arguments arrived as exactly
    /// <c>{ ImageName, Prompt }</c>: <b>no <c>TargetFile</c>, and no <c>ImagePaths</c></b> despite the
    /// corpus listing one. The corpus happened to be right about <c>ImageName</c>; it was still worth
    /// measuring, because this CLI's documentation is recorded wrong twice in
    /// <c>docs/vendor-doc-audit.md</c> and "the docs agreed" is not a reason to believe the third.
    /// </para>
    /// </remarks>
    private static string? ReadWriteTarget(JsonElement toolCall, string? toolName)
    {
        if (toolName is null || !WriteTargetFields.TryGetValue(toolName, out var fields))
        {
            return null;
        }

        if (!toolCall.TryGetProperty("args", out var args) || args.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var field in fields)
        {
            if (args.TryGetProperty(field, out var target) && target.ValueKind == JsonValueKind.String
                && target.GetString() is { Length: > 0 } value)
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>The argument names this gate tried for a tool, for a denial reason a person can act on.</summary>
    private static string FormatFields(string toolName) =>
        WriteTargetFields.TryGetValue(toolName, out var fields)
            ? string.Join(", ", fields)
            : "(no argument names are configured for this tool)";

    /// <summary>
    /// The argument names a payload actually carried. Names only — a value could hold a prompt or
    /// file content, and a denial reason is not a place to echo either back.
    /// </summary>
    private static string DescribeArgKeys(JsonElement toolCall)
    {
        if (!toolCall.TryGetProperty("args", out var args) || args.ValueKind != JsonValueKind.Object)
        {
            return "no args object at all";
        }

        var keys = args.EnumerateObject().Select(p => p.Name).ToArray();
        return keys.Length == 0 ? "an empty args object" : string.Join(", ", keys);
    }

    /// <summary>
    /// For each write-family tool, the argument names that can carry the path it writes to, in
    /// priority order. #708.
    /// </summary>
    /// <remarks>
    /// Keyed by tool because agy's payloads are not uniform: the three text-editing tools name their
    /// target <c>TargetFile</c> and <c>generate_image</c> does not carry that field at all. A single
    /// field name for the whole family reads as a tidy simplification and is how #708 happened.
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, string[]> WriteTargetFields =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["write_to_file"] = ["TargetFile"],
            ["replace_file_content"] = ["TargetFile"],
            ["multi_replace_file_content"] = ["TargetFile"],
            // Measured: a real call carried exactly { ImageName, Prompt }. TargetFile is kept as a
            // fallback only because it costs nothing and agy has renamed payload fields before.
            ["generate_image"] = ["ImageName", "TargetFile"],
        };

    /// <summary>
    /// Mirrors <c>AgyWorkerAdapter.WriteTools</c> — the agy tools whose target #679 bounds.
    /// </summary>
    /// <remarks>
    /// A mirror contract like <see cref="DeniedToolsEnvironmentVariable"/>: <c>Baton.Vendors</c> cannot
    /// reference <c>Baton.Cli</c>. A name missing here is a genuine hole rather than a broken run — the
    /// write would be granted and unbounded — which is the opposite polarity from the claude side's
    /// equivalent, and the reason a test derives this list from a real <c>Resolve</c>.
    /// </remarks>
    public static readonly IReadOnlySet<string> WriteFamilyTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "write_to_file", "replace_file_content", "multi_replace_file_content", "generate_image",
    };

    /// <summary>
    /// Builds the denial object through <see cref="JsonSerializer"/> rather than string
    /// interpolation, so no reason text can produce output agy cannot parse — which it would read as
    /// an allow.
    /// </summary>
    private static string DenyJson(string reason) =>
        JsonSerializer.Serialize(new { decision = "deny", reason });

    /// <summary>Mirrors <c>AgyWorkerAdapter.DeniedToolsVendorTag</c>; see it for why (#600).</summary>
    private const string VendorTag = "agy";

    /// <summary>
    /// True when <paramref name="toolName"/> is withheld: either named exactly, or matched by an
    /// entry ending in <c>*</c> as a prefix.
    /// </summary>
    /// <remarks>
    /// Prefix support exists for one measured-shaped reason. agy's corpus offers
    /// <c>"browser_.*"</c> as a matcher example — "Match any tool starting with <c>browser_</c>" —
    /// while enumerating no such tools in its Supported Tools list, so a family exists whose members
    /// cannot be written down. Exact-match-only would silently withhold none of them. Deliberately a
    /// trailing <c>*</c> rather than full regex: the input is AER's own adapter output, not operator
    /// text, and a regex engine here would be a larger surface than the problem.
    /// </remarks>
    private static bool IsWithheld(IReadOnlySet<string> denied, string toolName)
    {
        if (denied.Contains(toolName))
        {
            return true;
        }

        foreach (var entry in denied)
        {
            if (entry.Length > 1 && entry[^1] == '*' &&
                toolName.StartsWith(entry[..^1], StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
