namespace Baton.Cli;

/// <summary>
/// Parses <c>baton status</c>'s arguments: <c>baton status &lt;room-dir&gt; [--follow]</c>. Never
/// throws a bare <see cref="InvalidOperationException"/> for a malformed invocation — every
/// failure here is a <see cref="CliArgumentException"/> (<see href="../../../docs/agents/developing-baton.md"/>),
/// mirroring <see cref="RunOptionsParser"/>/<see cref="CancelOptionsParser"/>.
/// </summary>
public static class StatusOptionsParser
{
    public const string Usage = "Usage: baton status <room-dir> [--follow] [--json] [--repo <checkout-dir>]";

    public static StatusOptions Parse(IReadOnlyList<string> args)
    {
        string? roomDirectoryPath = null;
        var follow = false;
        var json = false;
        string? repoPath = null;

        var i = 0;
        while (i < args.Count)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--follow":
                    follow = true;
                    i++;
                    break;
                case "--json":
                    json = true;
                    i++;
                    break;
                case "--repo":
                    if (i + 1 >= args.Count)
                    {
                        throw new CliArgumentException(
                            $"Option '--repo' requires a value. {Usage}",
                            "pass a value after '--repo', e.g. --repo <checkout-dir>.");
                    }

                    repoPath = args[i + 1];
                    i += 2;
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

        if (follow && json)
        {
            throw new CliArgumentException(
                $"'--follow' and '--json' are incompatible: --json prints exactly one object and returns, --follow " +
                $"never stops printing on its own. {Usage}",
                $"baton status {roomDirectoryPath} --json");
        }

        return new StatusOptions(
            RoomDirectoryPath.Resolve(roomDirectoryPath), follow, json,
            repoPath is null ? null : Path.GetFullPath(repoPath));
    }
}
