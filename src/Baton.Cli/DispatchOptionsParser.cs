using System.Text.RegularExpressions;

namespace Baton.Cli;

/// <summary>
/// Parses <c>baton dispatch</c>'s arguments — see <see cref="Usage"/> for the full, authoritative flag
/// list (not restated here, record-once), and spec/baton.md's dispatch entry for why the spec has
/// three sources (#1518). None is required here because whether one is required at all depends on
/// whether <c>&lt;name&gt;</c> resolves to a role (needs one) or a workflow template (rejects all
/// three) — a catalog question <see cref="DispatchCommand"/> answers, not the parser. Every malformed
/// invocation is a <see cref="CliArgumentException"/> (CLAUDE.md's error-handling rules), never a bare
/// <see cref="InvalidOperationException"/>.
/// </summary>
public static class DispatchOptionsParser
{
    /// <summary>The one copy of <c>baton dispatch</c>'s usage line, printed here on error and by <c>Program</c>.</summary>
    public const string Usage =
        "Usage: baton dispatch <name> [--spec <spec-file> | --spec - | --spec-text <text>] [--attach <file>] [--skill <name>] [--adapter <name>] [--model <name>] [--effort <name>] [--room-dir <dir>] [--workspace <dir>] [--workflow-id <id>] [--output <path>] [--timeout <minutes>] [--token-budget <n>] [--max-tool-steps <n>] [--billed-rate-limit <n>] [--verify <cmd>] [--verify-cmd <cmd>] [--verify-timeout <minutes>] [--expect-pr <true|false>] [--continue <room-dir>] [--override-runway <reason>] [--label <text>] [--workstream <slug>] [--repo <checkout-dir>] [--list-capabilities]";

    /// <summary>
    /// <c>--label</c>'s cap (#1499) — a Fleet Glass room title, not a description; long enough for "the
    /// #1496 env-snapshot lane" and short enough to stay legible in a lane card next to the state chips.
    /// </summary>
    public const int MaxLabelLength = 60;

    /// <summary>
    /// <c>--workstream</c>'s cap (#1619) — matches <see cref="MaxLabelLength"/> so a workstream group
    /// heading never dwarfs the label it sits beside in Fleet Glass.
    /// </summary>
    public const int MaxWorkstreamLength = 60;

    /// <summary>
    /// <c>--workstream</c>'s slug grammar (#1619): starts with a letter or digit, then any run of
    /// letters, digits, <c>.</c>, <c>_</c>, or <c>-</c>. Unlike <c>--label</c>'s free text, this value
    /// is later used verbatim as a Windows directory name
    /// (<see cref="WorkstreamJunctionLinker"/>, under <c>BatonPaths.ByWorkstream</c>), so it is
    /// restricted to characters safe as one path segment rather than sanitized/folded like a label —
    /// the allowlist also rules out <c>.</c>/<c>..</c> and every character cmd.exe or the filesystem
    /// would treat specially.
    /// </summary>
    private static readonly Regex WorkstreamSlugPattern = new("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.Compiled);

    /// <summary>
    /// The hard ceiling <c>--timeout</c> refuses outright (#1442) — why refuse rather than confirm:
    /// spec/baton.md §2.
    /// </summary>
    public const int MaxTimeoutMinutes = 24 * 60;

    /// <summary>
    /// The caution threshold <c>--timeout</c> accepts but flags — <see cref="Baton.Cli.DispatchCommand"/>
    /// prints the stderr warning above this; why warn rather than refuse: spec/baton.md §2.
    /// </summary>
    public const int WarnTimeoutMinutes = 120;

    public static DispatchOptions Parse(IReadOnlyList<string> args)
    {
        string? name = null;
        string? specFilePath = null;
        string? specText = null;
        var specFromStdin = false;
        string? adapter = null;
        string? model = null;
        string? effort = null;
        string? roomDirectoryPath = null;
        string? workspaceDirectory = null;
        string? workflowId = null;
        string? outputPath = null;
        TimeSpan? timeout = null;
        long? tokenBudget = null;
        int? maxToolSteps = null;
        long? billedRateLimit = null;
        string? verifyCommand = null;
        var verifyCommands = new List<string>();
        TimeSpan? verifyTimeout = null;
        bool? expectPr = null;
        string? label = null;
        string? workstream = null;
        string? repoPath = null;
        string? continueFromRoomDirectoryPath = null;
        string? overrideRunwayReason = null;
        var attachments = new List<string>();
        var skills = new List<string>();
        var listCapabilities = false;

        var i = 0;
        while (i < args.Count)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--spec":
                    var specValue = RequireValue(args, ref i, arg);
                    if (specValue == "-")
                    {
                        specFromStdin = true;
                        specFilePath = null;
                    }
                    else
                    {
                        specFromStdin = false;
                        specFilePath = specValue;
                    }

                    break;
                case "--spec-text":
                    var specTextValue = RequireValue(args, ref i, arg);
                    if (specTextValue.Trim().Length == 0)
                    {
                        throw new CliArgumentException(
                            $"'--spec-text' is blank — pass the task prompt text inline, or use --spec "
                            + $"<spec-file> for a brief that already lives in a file. {Usage}",
                            "pass a non-blank string after --spec-text.");
                    }

                    specText = specTextValue;
                    break;
                case "--attach":
                    attachments.Add(RequireValue(args, ref i, arg));
                    break;
                case "--skill":
                    skills.Add(RequireValue(args, ref i, arg));
                    break;
                case "--adapter":
                    adapter = RequireValue(args, ref i, arg);
                    break;
                case "--model":
                    model = RequireValue(args, ref i, arg);
                    break;
                case "--effort":
                    effort = RequireValue(args, ref i, arg);
                    break;
                case "--room-dir":
                    roomDirectoryPath = RequireValue(args, ref i, arg);
                    break;
                case "--workspace":
                    workspaceDirectory = RequireValue(args, ref i, arg);
                    break;
                case "--workflow-id":
                    workflowId = RequireValue(args, ref i, arg);
                    break;
                case "--output":
                    outputPath = RequireValue(args, ref i, arg);
                    break;
                case "--timeout":
                    timeout = ParseTimeout(RequireValue(args, ref i, arg));
                    break;
                case "--token-budget":
                    tokenBudget = ParseTokenBudget(RequireValue(args, ref i, arg));
                    break;
                case "--max-tool-steps":
                    maxToolSteps = ParseMaxToolSteps(RequireValue(args, ref i, arg));
                    break;
                case "--billed-rate-limit":
                    billedRateLimit = ParseBilledRateLimit(RequireValue(args, ref i, arg));
                    break;
                case "--verify":
                    verifyCommand = RequireValue(args, ref i, arg);
                    break;
                case "--verify-cmd":
                    verifyCommands.Add(ParseVerifyCommand(RequireValue(args, ref i, arg)));
                    break;
                case "--verify-timeout":
                    verifyTimeout = ParseVerifyTimeout(RequireValue(args, ref i, arg));
                    break;
                case "--expect-pr":
                    expectPr = ParseExpectPr(RequireValue(args, ref i, arg));
                    break;
                case "--label":
                    label = SanitizeLabel(RequireValue(args, ref i, arg));
                    break;
                case "--workstream":
                    workstream = SanitizeWorkstream(RequireValue(args, ref i, arg));
                    break;
                case "--repo":
                    repoPath = RequireValue(args, ref i, arg);
                    break;
                case "--continue":
                    continueFromRoomDirectoryPath = RequireValue(args, ref i, arg);
                    break;
                case "--override-runway":
                    overrideRunwayReason = RequireOverrideRunwayReason(RequireValue(args, ref i, arg));
                    break;
                case "--list-capabilities":
                    listCapabilities = true;
                    i++;
                    break;
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new CliArgumentException($"Unknown option '{arg}'. {Usage}");
                    }

                    if (name is not null)
                    {
                        throw new CliArgumentException($"Unexpected extra argument '{arg}'. {Usage}");
                    }

                    name = arg;
                    i++;
                    break;
            }
        }

        // #1500 second-reader MED-5: --list-capabilities prints and returns before anything is
        // validated, created, or dispatched (DispatchCommand.ExecuteAsync's early return) — passed
        // alongside a real <name> it silently discards that dispatch and still exits 0, which is
        // exactly the case #1356's truthful exit-code table exists to prevent. Refuse the combination
        // up front, the same way --attach on a template is refused, rather than let a fat-fingered or
        // templated invocation read as success for work that never happened.
        if (listCapabilities && name is not null)
        {
            throw new CliArgumentException(
                $"'--list-capabilities' does not take a role or template name — it prints adapter/model/"
                + $"effort/timebox info and exits, dispatching nothing. {Usage}",
                $"run 'baton dispatch --list-capabilities' on its own, or drop the flag to dispatch '{name}'.");
        }

        if (name is null && !listCapabilities)
        {
            throw new CliArgumentException(
                $"Missing required <name> argument. {Usage}",
                "run 'baton templates' to see available role and template names.");
        }

        // #1518: --spec <file>, --spec -, and --spec-text are three sources for the same one task
        // prompt — a repeat of the SAME flag is last-wins (specFilePath/specFromStdin above already
        // implement that for --spec), but naming two DIFFERENT sources is very likely a mistake with no
        // sane resolution (which one did the operator mean?), so it is refused outright rather than
        // silently picking a winner. Whether at least one is required at all is a catalog question
        // (a template takes none) DispatchCommand answers, not the parser — same reason --spec alone
        // was already optional here before this issue.
        if ((specFilePath is not null || specFromStdin) && specText is not null)
        {
            throw new CliArgumentException(
                $"Pass at most one of --spec <spec-file>, --spec -, or --spec-text <text> — not more "
                + $"than one source for the same task prompt. {Usage}",
                "drop --spec-text to use the file/stdin source, or drop --spec to use --spec-text.");
        }

        // Fresh and unique per invocation unless pinned: a dispatch is one-shot, and deriving a stable
        // directory from the name (the way `baton run` derives one from the workflow file) would make a
        // second `baton dispatch review` resume — and so replay — the first's terminal snapshot rather
        // than run again. The per-execution artifact dir already keeps outputs collision-free (#897);
        // this keeps the *task* fresh so the orchestrator's repeated self-dispatch (#778) actually reruns.
        //
        // R2 (#1354/#1380): the default lives OUTSIDE the workspace, under BatonPaths.Rooms
        // ($BATON_HOME/rooms, default ~/.baton/rooms) — never under the audited tree itself. A room dropped
        // inside the workspace it audits shows up as `?? .baton/` on that tree's own `git status`, which
        // fails the audit even on an otherwise-pristine workspace (finding 2).
        if (roomDirectoryPath is null)
        {
            var uniqueName = $"dispatch-{name ?? "capabilities"}-{Guid.NewGuid().ToString("N")[..8]}";
            roomDirectoryPath = Path.Combine(Baton.Status.BatonPaths.Rooms, uniqueName);
        }

        return new DispatchOptions(
            name ?? string.Empty, specFilePath, RoomDirectoryPath.Resolve(roomDirectoryPath), adapter, workflowId,
            workspaceDirectory is null ? null : Path.GetFullPath(workspaceDirectory),
            model, effort,
            outputPath is null ? null : Path.GetFullPath(outputPath),
            timeout, label, workstream,
            attachments.Count > 0 ? attachments : null,
            listCapabilities,
            tokenBudget,
            repoPath is null ? null : Path.GetFullPath(repoPath),
            maxToolSteps,
            billedRateLimit,
            verifyCommand,
            specText,
            specFromStdin,
            expectPr,
            continueFromRoomDirectoryPath is null ? null : RoomDirectoryPath.Resolve(continueFromRoomDirectoryPath),
            verifyCommands.Count > 0 ? verifyCommands : null,
            verifyTimeout,
            overrideRunwayReason,
            NormalizeSkills(skills));
    }

    /// <summary>
    /// #1151: the repeatable <c>--skill</c> flag's shared normalization, used by this parser and by
    /// <see cref="RedispatchOptionsParser"/> so the two verbs cannot diverge on what a value means.
    /// <list type="bullet">
    /// <item>a blank value is the <b>clear</b> token (<c>--skill ""</c>), which matters only on
    ///   redispatch — there it discards the parent's list, and the caller distinguishes "cleared" from
    ///   "never passed" by whether the flag appeared at all, not by this method's result;</item>
    /// <item>mixing a blank with a real name is <b>refused</b>, not silently resolved. It is the same
    ///   shape as passing two different spec sources: there is no reading of
    ///   <c>--skill "" --skill review</c> that is more likely than the other, so guessing one would be
    ///   the silent-wrong-capability failure this feature exists to remove;</item>
    /// <item>duplicates collapse, order is preserved — the resolver would drop them anyway, and the
    ///   binding should record what the operator meant rather than what they typed twice.</item>
    /// </list>
    /// Returns null when nothing usable was passed, which is exactly "attach no skills".
    /// </summary>
    internal static IReadOnlyList<string>? NormalizeSkills(IReadOnlyList<string> rawValues)
    {
        var named = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sawBlank = false;
        foreach (var value in rawValues)
        {
            var trimmed = value.Trim();
            if (trimmed.Length == 0)
            {
                sawBlank = true;
                continue;
            }

            if (seen.Add(trimmed))
            {
                named.Add(trimmed);
            }
        }

        if (sawBlank && named.Count > 0)
        {
            throw new CliArgumentException(
                "'--skill \"\"' clears the skill list, so passing it alongside a named skill "
                + $"({string.Join(", ", named)}) says two contradictory things at once.",
                "pass only the skills you want attached, or pass --skill \"\" alone to attach none.");
        }

        return named.Count > 0 ? named : null;
    }

    /// <summary>
    /// Validates one <c>--verify-cmd</c> value against the shape allowlist (#1882,
    /// <see cref="Baton.Mutation.VerifyStepCommandParser"/>) and returns it verbatim. Refused at PARSE
    /// time rather than at run time, and naming the offending command: several of these flags can be
    /// passed at once, and "one of your verify commands is not allowed" would leave the operator
    /// guessing which. The stored value is the raw text, not the tokenized argv — the argv is re-derived
    /// where it is spawned, so bindings.json and the results file both carry what was actually typed.
    /// </summary>
    private static string ParseVerifyCommand(string rawValue)
    {
        if (!Baton.Mutation.VerifyStepCommandParser.TryParse(rawValue, out _, out var error))
        {
            throw new CliArgumentException(
                $"{error} {Usage}",
                "pass an allowlisted verify command, e.g. --verify-cmd \"dotnet build -warnaserror\".");
        }

        return rawValue.Trim();
    }

    /// <summary>
    /// Parses <c>--verify-timeout</c>'s minutes value (#1882): a positive whole number, capped by the
    /// same <see cref="MaxTimeoutMinutes"/> ceiling <see cref="ParseTimeout"/> applies, for the same
    /// non-interactive-CLI reason — this bound is per verify COMMAND, and the step runs before the
    /// worker even starts, so a typo'd value strands a lane before it has done anything at all.
    /// </summary>
    private static TimeSpan ParseVerifyTimeout(string rawValue)
    {
        if (!int.TryParse(rawValue, out var minutes) || minutes <= 0)
        {
            throw new CliArgumentException(
                $"'--verify-timeout {rawValue}' is not a positive whole number of minutes. {Usage}",
                "pass a positive integer, e.g. --verify-timeout 10.");
        }

        if (minutes > MaxTimeoutMinutes)
        {
            throw new CliArgumentException(
                $"'--verify-timeout {rawValue}' exceeds the {MaxTimeoutMinutes}-minute (24h) ceiling.",
                $"pass a value at or below {MaxTimeoutMinutes}, e.g. --verify-timeout 10.");
        }

        return TimeSpan.FromMinutes(minutes);
    }

    /// <summary>
    /// #1848: <c>--override-runway</c>'s reason is mandatory and non-blank. The flag is the ONLY bypass
    /// of the runway hold and every use of it is written to the room record, so a blank reason would
    /// leave an audit row that records nothing — refused here rather than accepted and stored empty.
    /// </summary>
    private static string RequireOverrideRunwayReason(string rawValue)
    {
        if (rawValue.Trim().Length == 0)
        {
            throw new CliArgumentException(
                $"'--override-runway' needs a reason — it is the audited bypass of the runway hold, and a "
                + $"blank reason records nothing. {Usage}",
                """pass the reason inline, e.g. --override-runway "conductor lane, week resets in 2h".""");
        }

        return rawValue.Trim();
    }

    /// <summary>
    /// Parses <c>--expect-pr</c>'s value (#1788): a literal <c>true</c>/<c>false</c>, case-insensitive.
    /// Unlike most escape hatches here this is not a free-form value — the delivery check's PR half is a
    /// binary switch, and a typo'd value (e.g. a stray <c>1</c>) failing loudly beats it silently
    /// resolving to whichever of true/false <see cref="bool.TryParse(string?, out bool)"/> happens not to throw for.
    /// </summary>
    private static bool ParseExpectPr(string rawValue)
    {
        if (!bool.TryParse(rawValue, out var expectPr))
        {
            throw new CliArgumentException(
                $"'--expect-pr {rawValue}' is not 'true' or 'false'. {Usage}",
                "pass --expect-pr true or --expect-pr false.");
        }

        return expectPr;
    }

    /// <summary>
    /// Parses <c>--token-budget</c>'s value (#1623): a positive whole number of tokens. No ceiling like
    /// <c>--timeout</c>'s <see cref="MaxTimeoutMinutes"/> — an operator raising their own role's budget
    /// is not the runaway-consumption failure mode this issue exists to arrest; only a role with no
    /// budget declared and no override runs unwatched.
    /// </summary>
    private static long ParseTokenBudget(string rawValue)
    {
        if (!long.TryParse(rawValue, out var tokens) || tokens <= 0)
        {
            throw new CliArgumentException(
                $"'--token-budget {rawValue}' is not a positive whole number of tokens. {Usage}",
                "pass a positive integer, e.g. --token-budget 600000.");
        }

        return tokens;
    }

    /// <summary>
    /// Parses <c>--max-tool-steps</c>'s value (#1686 review F11): a positive whole number of real tool
    /// calls (the fixed cross-vendor unit, spec/baton.md §3), same shape and no-ceiling rationale as
    /// <see cref="ParseTokenBudget"/>.
    /// </summary>
    private static int ParseMaxToolSteps(string rawValue)
    {
        if (!int.TryParse(rawValue, out var steps) || steps <= 0)
        {
            throw new CliArgumentException(
                $"'--max-tool-steps {rawValue}' is not a positive whole number of tool calls. {Usage}",
                "pass a positive integer, e.g. --max-tool-steps 100.");
        }

        return steps;
    }

    /// <summary>
    /// Parses <c>--billed-rate-limit</c>'s value (#1691): a positive whole number of billed tokens per
    /// trailing <c>Baton.Mutation.TokenBudgetMonitor.BilledRateWindow</c> (5 minutes — the window is
    /// fixed, only the ceiling is an argument), same shape and no-ceiling rationale as
    /// <see cref="ParseTokenBudget"/>. spec/baton.md §3 states why this flag is the only source a rate
    /// limit ever has.
    /// </summary>
    private static long ParseBilledRateLimit(string rawValue)
    {
        if (!long.TryParse(rawValue, out var tokens) || tokens <= 0)
        {
            throw new CliArgumentException(
                $"'--billed-rate-limit {rawValue}' is not a positive whole number of billed tokens per 5 minutes. {Usage}",
                "pass a positive integer, e.g. --billed-rate-limit 250000.");
        }

        return tokens;
    }

    /// <summary>
    /// <c>--label</c>'s sanitization (#1499): trimmed, embedded newlines folded to spaces (display
    /// text renders on one line — a Fleet Glass lane card, never a paragraph), then capped at
    /// <see cref="MaxLabelLength"/> without splitting a surrogate pair
    /// (<see cref="Baton.Outcomes.ContractValidator.TrimWithoutSplittingSurrogatePair"/> — the one file
    /// that rule lives in; reused here rather than re-cut by hand). Nothing here needs to escape JSON
    /// or path characters: the label is never written into a path (the hex room name stays the
    /// on-disk identity) and <see cref="System.Text.Json.JsonSerializer"/> already escapes whatever
    /// the string contains when it lands in <c>bindings.json</c>/the MCP payload. A blank result after
    /// trimming (an operator passing <c>--label ""</c> or all-whitespace) is treated the same as never
    /// passing the flag at all, rather than a typed refusal — there is no meaningful invocation this
    /// could be correcting. Shared with <see cref="RedispatchOptionsParser"/>, which parses the
    /// identical flag.
    /// </summary>
    internal static string? SanitizeLabel(string rawValue)
    {
        var folded = rawValue.Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();

        return folded.Length == 0
            ? null
            : Baton.Outcomes.ContractValidator.TrimWithoutSplittingSurrogatePair(folded, MaxLabelLength);
    }

    /// <summary>
    /// <c>--workstream</c>'s sanitization (#1619): trimmed, then checked against
    /// <see cref="WorkstreamSlugPattern"/> and <see cref="MaxWorkstreamLength"/>. A blank result after
    /// trimming is treated the same as never passing the flag, matching <see cref="SanitizeLabel"/>'s
    /// convention — but unlike a label, a non-blank value that fails the slug grammar or exceeds the
    /// cap is refused outright rather than silently folded/truncated: this value is a grouping *key*
    /// (two different long slugs truncated to the same prefix would silently merge two workstreams)
    /// and later becomes a literal directory name under <c>BatonPaths.ByWorkstream</c>
    /// (<see cref="WorkstreamJunctionLinker"/>), so a value the filesystem can't use as one path
    /// segment must fail loud at parse time, the same non-interactive-CLI doctrine
    /// <see cref="ParseTimeout"/>'s ceiling rests on. Folded to lowercase after the grammar check
    /// passes — spec/baton.md §2 has the NTFS-vs-Fleet-Glass rationale. Shared with
    /// <see cref="RedispatchOptionsParser"/>, which parses the identical flag.
    /// </summary>
    internal static string? SanitizeWorkstream(string rawValue)
    {
        var trimmed = rawValue.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (trimmed.Length > MaxWorkstreamLength || !WorkstreamSlugPattern.IsMatch(trimmed))
        {
            throw new CliArgumentException(
                $"'--workstream {rawValue}' is not a valid slug. It becomes a Windows directory name "
                + $"under '~/.baton/by-workstream/', so it must be 1-{MaxWorkstreamLength} characters, start "
                + "with a letter or digit, and contain only letters, digits, '.', '_', or '-'.",
                "pass a short slug, e.g. --workstream 1619 or --workstream w1619.");
        }

        return trimmed.ToLowerInvariant();
    }

    /// <summary>
    /// Parses <c>--timeout</c>'s minutes value: rejects anything that isn't a positive whole number,
    /// and rejects (rather than merely warns on) anything above <see cref="MaxTimeoutMinutes"/> — the
    /// issue's proposed &gt;2h interactive confirmation has no non-interactive equivalent, so the
    /// simplest honest substitute is a hard ceiling here plus a caution-only warning printed by
    /// <see cref="Baton.Cli.DispatchCommand"/> above <see cref="WarnTimeoutMinutes"/>.
    /// </summary>
    private static TimeSpan ParseTimeout(string rawValue)
    {
        if (!int.TryParse(rawValue, out var minutes) || minutes <= 0)
        {
            throw new CliArgumentException(
                $"'--timeout {rawValue}' is not a positive whole number of minutes. {Usage}",
                "pass a positive integer, e.g. --timeout 90.");
        }

        if (minutes > MaxTimeoutMinutes)
        {
            throw new CliArgumentException(
                $"'--timeout {rawValue}' exceeds the {MaxTimeoutMinutes}-minute (24h) ceiling. A "
                + "non-interactive dispatch cannot ask for confirmation, so a value this large is "
                + "refused outright rather than risk a typo stranding a lane for a full day.",
                $"pass a value at or below {MaxTimeoutMinutes}, e.g. --timeout 120.");
        }

        return TimeSpan.FromMinutes(minutes);
    }

    private static string RequireValue(IReadOnlyList<string> args, ref int index, string optionName)
    {
        if (index + 1 >= args.Count)
        {
            throw new CliArgumentException(
                $"Option '{optionName}' requires a value. {Usage}",
                $"pass a value after '{optionName}', e.g. {optionName} <value>.");
        }

        var value = args[index + 1];
        index += 2;
        return value;
    }
}
