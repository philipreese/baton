namespace Baton.Cli;

/// <summary>
/// Parses <c>baton resolve</c>'s arguments: <c>baton resolve &lt;room-dir&gt;
/// [--execution &lt;execution-id&gt;] --accept-capture | --reject --reason &lt;text&gt; | --close --reason
/// &lt;text&gt;</c>. Never throws a
/// bare <see cref="InvalidOperationException"/> for a malformed invocation — every failure here is a
/// <see cref="CliArgumentException"/> (<see href="../../../docs/agents/developing-baton.md"/>), mirroring
/// <see cref="DecideOptionsParser"/>. Every validity rule beyond "is this a recognized flag" stays
/// <c>Mutation.MutationInterface.RecordCaptureResolutionAsync</c>'s (e.g. whether the named execution
/// actually has an unresolved capture) — this parser adds no vocabulary of its own beyond the
/// accept/reject grammar and the reject-requires-reason rule the ruling's own literal grammar states.
/// </summary>
public static class ResolveOptionsParser
{
    private const string Usage =
        "Usage: baton resolve <room-dir> [--execution <execution-id>] --accept-capture | --reject --reason <text> | --close --reason <text>";

    public static ResolveOptions Parse(IReadOnlyList<string> args)
    {
        string? roomDirectoryPath = null;
        string? executionId = null;
        bool? accept = null;
        bool close = false;
        string? reason = null;

        var i = 0;
        while (i < args.Count)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--execution":
                    executionId = RequireValue(args, ref i, arg);
                    break;
                case "--accept-capture":
                    RefuseConflictingVerb(accept, close, arg);
                    accept = true;
                    i++;
                    break;
                case "--reject":
                    RefuseConflictingVerb(accept, close, arg);
                    accept = false;
                    i++;
                    break;
                case "--close":
                    // #1622 (d)/#1700: --close is its own verb, not --reject in disguise -- it admits
                    // a different producer set (VerifyFailed/ExecutionArrested/no-producer, none of
                    // which ever had a capture to accept or reject), so it is tracked as its own flag
                    // rather than collapsed into `accept: false` here. ResolveCommand/MutationInterface
                    // read `close` to widen admission; `accept` stays false either way.
                    RefuseConflictingVerb(accept, close, arg);
                    accept = false;
                    close = true;
                    i++;
                    break;
                case "--reason":
                    reason = RequireValue(args, ref i, arg);
                    break;
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new CliArgumentException($"Unknown option '{arg}'. {Usage}");
                    }

                    if (roomDirectoryPath is not null)
                    {
                        throw new CliArgumentException($"Unexpected extra argument '{arg}'. {Usage}");
                    }

                    roomDirectoryPath = arg;
                    i++;
                    break;
            }
        }

        if (roomDirectoryPath is null)
        {
            throw new CliArgumentException($"Missing required <room-dir> argument. {Usage}");
        }

        if (accept is null)
        {
            throw new CliArgumentException(
                $"Missing required option: one of '--accept-capture', '--reject', or '--close'. {Usage}",
                "pass --accept-capture (the capture honestly satisfies its declared output(s)), " +
                "--reject --reason <text>, or --close --reason <text>.");
        }

        if (accept == false && string.IsNullOrWhiteSpace(reason))
        {
            throw new CliArgumentException(
                close
                    ? $"'--close' requires '--reason <text>'. {Usage}"
                    : $"'--reject' requires '--reason <text>'. {Usage}",
                close
                    ? "pass --reason naming why the conductor is closing this without redoing the work."
                    : "pass --reason naming why the capture cannot honestly become the declared output(s). "
                      + "#1877: a rejection settles the step terminally — re-dispatch explicitly if you want a retry.");
        }

        return new ResolveOptions(
            RoomDirectoryPath.Resolve(roomDirectoryPath),
            executionId,
            accept.Value,
            reason,
            close);
    }

    /// <summary>
    /// The three verbs (<c>--accept-capture</c>/<c>--reject</c>/<c>--close</c>) are mutually
    /// exclusive; refuses a second one once any has already been seen, naming both the flag already
    /// parsed and the conflicting one.
    /// </summary>
    private static void RefuseConflictingVerb(bool? accept, bool close, string incomingArg)
    {
        if (accept is null)
        {
            return;
        }

        var already = close ? "--close" : accept.Value ? "--accept-capture" : "--reject";
        if (already == incomingArg)
        {
            return;
        }

        throw new CliArgumentException(
            $"Cannot pass both '{already}' and '{incomingArg}'. {Usage}",
            "pass exactly one of --accept-capture, --reject, or --close.");
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
