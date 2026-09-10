namespace Baton.Cli;

/// <summary>
/// Parses <c>baton run</c>'s arguments: <c>baton run &lt;workflow-file&gt; --bindings &lt;bindings-file&gt;
/// [--room-dir &lt;dir&gt;] [--workflow-id &lt;id&gt;] [--echo-worker] [--register]</c>. Never throws a bare
/// <see cref="InvalidOperationException"/> for a malformed invocation — every failure here is a
/// <see cref="CliArgumentException"/> (<see href="../../../docs/agents/developing-baton.md"/>).
/// </summary>
public static class RunOptionsParser
{
    /// <summary>
    /// The one copy of <c>baton run</c>'s usage line, printed both from here on an argument error and
    /// by <c>Program</c> in the full command list.
    /// </summary>
    public const string Usage =
        "Usage: baton run <workflow-file> --bindings <bindings-file> [--room-dir <dir>] [--workflow-id <id>] [--echo-worker] [--register] [--wait] [--wait-timeout <minutes>]";

    /// <summary>
    /// #628: <c>&lt;workflow-file&gt;</c> reads as "this is what runs", and under
    /// <c>--room-dir</c> it often is not. Printed wherever <see cref="Usage"/> is, since a reader
    /// who needs the arguments spelled out is exactly the reader whose prior this corrects.
    /// </summary>
    public const string ResumeNote =
        "baton run resumes a --room-dir that already holds a snapshot, running the workflow that " +
        "directory was first bound to rather than <workflow-file>. It refuses when the two are " +
        "different templates. Use a fresh --room-dir to start different work.";

    public static RunOptions Parse(IReadOnlyList<string> args)
    {
        string? workflowFilePath = null;
        string? bindingsFilePath = null;
        string? roomDirectoryPath = null;
        string? workflowId = null;
        var echoWorker = false;
        var wait = false;
        var register = false;
        TimeSpan? waitTimeout = null;

        var i = 0;
        while (i < args.Count)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--bindings":
                    bindingsFilePath = RequireValue(args, ref i, arg);
                    break;
                case "--room-dir":
                    roomDirectoryPath = RequireValue(args, ref i, arg);
                    break;
                case "--workflow-id":
                    workflowId = RequireValue(args, ref i, arg);
                    break;
                case "--echo-worker":
                    echoWorker = true;
                    i++;
                    break;
                case "--wait":
                    wait = true;
                    i++;
                    break;
                case "--register":
                    register = true;
                    i++;
                    break;
                case "--wait-timeout":
                    waitTimeout = ParseWaitTimeout(RequireValue(args, ref i, arg));
                    break;
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new CliArgumentException($"Unknown option '{arg}'. {Usage} {ResumeNote}");
                    }

                    if (workflowFilePath is not null)
                    {
                        throw new CliArgumentException($"Unexpected extra argument '{arg}'. {Usage} {ResumeNote}");
                    }

                    workflowFilePath = arg;
                    i++;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(workflowFilePath))
        {
            throw new CliArgumentException($"Missing required <workflow-file> argument. {Usage} {ResumeNote}");
        }

        if (bindingsFilePath is null)
        {
            throw new CliArgumentException(
                $"Missing required option '--bindings <bindings-file>'. {Usage} {ResumeNote}",
                "pass --bindings <path-to-bindings.json> to configure the workers for this run, or use 'baton dispatch' to auto-generate them.");
        }

        // Derived from the workflow file's own name when not given, so `baton run workflow.json`
        // twice in the same directory naturally resumes the same room rather than each
        // invocation needing its own explicit --room-dir.
        roomDirectoryPath ??= Path.Combine(
            Directory.GetCurrentDirectory(), ".baton", Path.GetFileNameWithoutExtension(workflowFilePath));

        return new RunOptions(
            workflowFilePath, bindingsFilePath, RoomDirectoryPath.Resolve(roomDirectoryPath), workflowId, echoWorker,
            Wait: wait, WaitTimeout: waitTimeout, Register: register);
    }

    /// <summary>
    /// #1378: rejects anything that isn't a positive whole number of minutes — a zero or negative
    /// bound would either spin the poll loop uselessly or time out before the room could ever settle,
    /// so both are refused loudly rather than silently coerced. No upper ceiling: unlike
    /// <c>dispatch</c>/<c>redispatch</c>'s <c>--timeout</c> (#1442), this bounds a caller's own poll
    /// loop rather than committing a worker's live vendor spend, so there is no day-long-lane risk to
    /// guard against here.
    /// </summary>
    private static TimeSpan ParseWaitTimeout(string rawValue)
    {
        if (!int.TryParse(rawValue, out var minutes) || minutes <= 0)
        {
            throw new CliArgumentException(
                $"'--wait-timeout {rawValue}' is not a positive whole number of minutes. {Usage}",
                "pass a positive integer, e.g. --wait-timeout 30.");
        }

        return TimeSpan.FromMinutes(minutes);
    }

    private static string RequireValue(IReadOnlyList<string> args, ref int index, string optionName)
    {
        if (index + 1 >= args.Count)
        {
            throw new CliArgumentException(
                $"Option '{optionName}' requires a value. {Usage} {ResumeNote}",
                $"pass a value after '{optionName}', e.g. {optionName} <value>.");
        }

        var value = args[index + 1];
        index += 2;
        return value;
    }
}
