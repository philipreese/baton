namespace Baton.Cli;

/// <summary>
/// Parses <c>baton redispatch</c>'s arguments: <c>baton redispatch &lt;room-dir&gt; [--spec &lt;spec-file&gt;]
/// [--adapter &lt;name&gt;] [--model &lt;name&gt;] [--effort &lt;name&gt;] [--workspace &lt;dir&gt;]
/// [--output &lt;path&gt;] [--timeout &lt;minutes&gt;]</c>. No <c>--room-dir</c> flag: the new room's
/// directory is always freshly generated (see <see cref="Parse"/>), the same never-reused rule
/// <see cref="DispatchOptionsParser"/> documents for <c>baton dispatch</c>. Every malformed invocation is
/// a <see cref="CliArgumentException"/> (CLAUDE.md's error-handling rules).
/// </summary>
public static class RedispatchOptionsParser
{
    /// <summary><c>baton redispatch</c>'s usage string, same role as <see cref="DispatchOptionsParser"/>'s own.</summary>
    public const string Usage =
        "Usage: baton redispatch <room-dir> [--spec <amended-brief>] [--attach <file>] [--adapter <name>] "
        + "[--model <name>] [--effort <name>] [--workspace <dir>] [--output <path>] [--timeout <minutes>] "
        + "[--token-budget <n>] [--max-tool-steps <n>] [--billed-rate-limit <n>] [--verify <cmd>] [--skill <name>] [--label <text>] [--workstream <slug>]";

    public static RedispatchOptions Parse(IReadOnlyList<string> args)
    {
        string? parentRoomDirectoryPath = null;
        string? specFilePath = null;
        string? adapter = null;
        string? model = null;
        string? effort = null;
        string? workspaceDirectory = null;
        string? outputPath = null;
        TimeSpan? timeout = null;
        long? tokenBudget = null;
        int? maxToolSteps = null;
        long? billedRateLimit = null;
        string? verifyCommand = null;
        string? label = null;
        var labelSpecified = false;
        string? workstream = null;
        var workstreamSpecified = false;
        var attachments = new List<string>();
        var skills = new List<string>();
        var skillsSpecified = false;

        var i = 0;
        while (i < args.Count)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--spec":
                    specFilePath = RequireValue(args, ref i, arg);
                    break;
                case "--attach":
                    attachments.Add(RequireValue(args, ref i, arg));
                    break;
                case "--skill":
                    // #1151: the flag APPEARING is what distinguishes "replace/clear" from "inherit the
                    // parent's list" -- mirroring --label/--workstream's own *Specified flags, and for
                    // the identical reason: an empty accumulated list is otherwise indistinguishable
                    // from the flag never being passed, so `--skill ""` could not clear anything.
                    skills.Add(RequireValue(args, ref i, arg));
                    skillsSpecified = true;
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
                case "--workspace":
                    workspaceDirectory = RequireValue(args, ref i, arg);
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
                case "--label":
                    label = DispatchOptionsParser.SanitizeLabel(RequireValue(args, ref i, arg));
                    labelSpecified = true;
                    break;
                case "--workstream":
                    workstream = DispatchOptionsParser.SanitizeWorkstream(RequireValue(args, ref i, arg));
                    workstreamSpecified = true;
                    break;
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new CliArgumentException($"Unknown option '{arg}'. {Usage}");
                    }

                    if (parentRoomDirectoryPath is not null)
                    {
                        throw new CliArgumentException($"Unexpected extra argument '{arg}'. {Usage}");
                    }

                    parentRoomDirectoryPath = arg;
                    i++;
                    break;
            }
        }

        if (parentRoomDirectoryPath is null)
        {
            throw new CliArgumentException(
                $"Missing required <room-dir> argument. {Usage}",
                "pass the terminal room directory to redispatch, e.g. baton redispatch <room-dir>.");
        }

        // Fresh and unique per invocation, never derived from the parent's name or path -- the same
        // rule DispatchOptionsParser documents: a redispatch is a NEW room, never a resume of one
        // (spec/baton.md §2).
        var uniqueName = $"redispatch-{Guid.NewGuid().ToString("N")[..8]}";
        var freshRoomDirectoryPath = Path.Combine(Baton.Status.BatonPaths.Rooms, uniqueName);

        return new RedispatchOptions(
            RoomDirectoryPath.Resolve(parentRoomDirectoryPath),
            RoomDirectoryPath.Resolve(freshRoomDirectoryPath),
            specFilePath,
            adapter, model, effort,
            workspaceDirectory is null ? null : Path.GetFullPath(workspaceDirectory),
            outputPath is null ? null : Path.GetFullPath(outputPath),
            timeout, label, labelSpecified, tokenBudget, workstream, workstreamSpecified,
            attachments.Count > 0 ? attachments : null, maxToolSteps, billedRateLimit, verifyCommand,
            DispatchOptionsParser.NormalizeSkills(skills), skillsSpecified);
    }

    /// <summary>Same shape and rationale as <see cref="DispatchOptionsParser"/>'s own <c>--token-budget</c> (#1623).</summary>
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

    /// <summary>Same shape and rationale as <see cref="DispatchOptionsParser"/>'s own <c>--max-tool-steps</c> (#1686 review F2).</summary>
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

    /// <summary>Same shape and rationale as <see cref="DispatchOptionsParser"/>'s own <c>--billed-rate-limit</c> (#1691).</summary>
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

    /// <summary>Same ceiling/warn thresholds and rationale as <see cref="DispatchOptionsParser"/>'s own <c>--timeout</c> (#1442).</summary>
    private static TimeSpan ParseTimeout(string rawValue)
    {
        if (!int.TryParse(rawValue, out var minutes) || minutes <= 0)
        {
            throw new CliArgumentException(
                $"'--timeout {rawValue}' is not a positive whole number of minutes. {Usage}",
                "pass a positive integer, e.g. --timeout 90.");
        }

        if (minutes > DispatchOptionsParser.MaxTimeoutMinutes)
        {
            throw new CliArgumentException(
                $"'--timeout {rawValue}' exceeds the {DispatchOptionsParser.MaxTimeoutMinutes}-minute (24h) "
                + "ceiling. A non-interactive dispatch cannot ask for confirmation, so a value this large is "
                + "refused outright rather than risk a typo stranding a lane for a full day.",
                $"pass a value at or below {DispatchOptionsParser.MaxTimeoutMinutes}, e.g. --timeout 120.");
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
