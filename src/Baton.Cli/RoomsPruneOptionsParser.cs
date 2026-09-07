using Baton.Status;

namespace Baton.Cli;

/// <summary>
/// Parses <c>baton rooms prune</c>'s arguments: <c>baton rooms prune --terminal [--older-than &lt;days&gt;]
/// [--state Succeeded|FinishedDuringTeardown|Failed|Cancelled|Indeterminate] [--dry-run] [--yes]</c>. Follows
/// <see cref="RoomDeleteOptionsParser"/>'s own error-handling contract — see its remarks.
/// <c>--dry-run</c> and <c>--yes</c> are mutually exclusive — passing both is rejected here rather than
/// silently letting <c>--yes</c> win, since a caller who typed <c>--dry-run</c> explicitly must not have
/// it silently discarded.
/// </summary>
public static class RoomsPruneOptionsParser
{
    public const string Usage =
        "Usage: baton rooms prune --terminal [--older-than <days>] [--state Succeeded|FinishedDuringTeardown|Failed|Cancelled|Indeterminate] [--dry-run] [--yes]";

    private static readonly IReadOnlyList<string> AllowedStates =
    [
        WorkflowOutcome.Succeeded,
        // #1945: a terminal word like any other here — a room that settled it is prunable, and
        // omitting it would leave one class of finished room unreachable by --state.
        WorkflowOutcome.FinishedDuringTeardown,
        WorkflowOutcome.Failed,
        WorkflowOutcome.Cancelled,
        WorkflowOutcome.Indeterminate,
    ];

    public static RoomsPruneOptions Parse(IReadOnlyList<string> args)
    {
        var terminal = false;
        int? olderThanDays = null;
        string? state = null;
        var dryRun = false;
        var yes = false;

        var i = 0;
        while (i < args.Count)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--terminal":
                    terminal = true;
                    i++;
                    continue;
                case "--dry-run":
                    dryRun = true;
                    i++;
                    continue;
                case "--yes":
                    yes = true;
                    i++;
                    continue;
                case "--older-than":
                    olderThanDays = ParseOlderThan(RequireValue(args, ref i, "--older-than"));
                    i++;
                    continue;
                case "--state":
                    state = ParseState(RequireValue(args, ref i, "--state"));
                    i++;
                    continue;
            }

            throw new CliArgumentException($"Unknown option '{arg}'. {Usage}");
        }

        if (!terminal)
        {
            throw new CliArgumentException($"Missing required --terminal flag. {Usage}");
        }

        if (dryRun && yes)
        {
            throw new CliArgumentException(
                $"--dry-run and --yes contradict each other. {Usage}",
                "pass only one of --dry-run or --yes.");
        }

        return new RoomsPruneOptions(terminal, olderThanDays, state, dryRun, yes);
    }

    private static int ParseOlderThan(string rawValue)
    {
        if (!int.TryParse(rawValue, out var days) || days <= 0)
        {
            throw new CliArgumentException(
                $"'--older-than {rawValue}' is not a positive whole number of days. {Usage}",
                "pass a positive integer, e.g. --older-than 7.");
        }

        return days;
    }

    private static string ParseState(string rawValue)
    {
        var match = AllowedStates.FirstOrDefault(s => string.Equals(s, rawValue, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            throw new CliArgumentException(
                $"'--state {rawValue}' is not one of {string.Join("|", AllowedStates)}. {Usage}",
                $"pass one of --state {string.Join(", --state ", AllowedStates)}.");
        }

        return match;
    }

    private static string RequireValue(IReadOnlyList<string> args, ref int index, string optionName)
    {
        if (index + 1 >= args.Count)
        {
            throw new CliArgumentException($"'{optionName}' requires a value. {Usage}");
        }

        index++;
        return args[index];
    }
}
