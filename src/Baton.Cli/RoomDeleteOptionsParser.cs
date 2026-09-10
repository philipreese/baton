namespace Baton.Cli;

/// <summary>
/// Parses <c>baton room delete</c>'s arguments: <c>baton room delete &lt;room-dir&gt;
/// [--force]</c>. Mirrors <see cref="KeepOptionsParser"/>'s shape — every failure
/// is a <see cref="CliArgumentException"/>, never a bare framework exception (docs/agents/developing-baton.md's
/// error-handling rules).
/// </summary>
public static class RoomDeleteOptionsParser
{
    public const string Usage = "Usage: baton room delete <room-dir> [--force]";

    public static RoomDeleteOptions Parse(IReadOnlyList<string> args)
    {
        string? roomDirectoryPath = null;
        var force = false;

        var i = 0;
        while (i < args.Count)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--force":
                    force = true;
                    i++;
                    continue;
            }

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
        }

        if (roomDirectoryPath is null)
        {
            throw new CliArgumentException($"Missing required <room-dir> argument. {Usage}");
        }

        return new RoomDeleteOptions(RoomDirectoryPath.Resolve(roomDirectoryPath), force);
    }
}
