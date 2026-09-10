namespace Baton.Cli;

/// <summary>
/// Parses <c>baton keep</c>'s arguments: <c>baton keep &lt;room-dir&gt;</c>. Never throws a bare
/// <see cref="InvalidOperationException"/> for a malformed invocation — every failure here is a
/// <see cref="CliArgumentException"/> (<see href="../../../docs/agents/developing-baton.md"/>), mirroring
/// <see cref="StatusOptionsParser"/>. Its inverse, <see cref="UnkeepOptionsParser"/>, is a separate
/// class (not a shared flag) so <c>tools/audit-completeness/clitripwire.py</c> — which globs
/// <c>*OptionsParser.cs</c> and reads one verb per file's own <c>Usage</c> constant — can validate
/// both verbs independently, the same as every other parser pair in this namespace.
/// </summary>
public static class KeepOptionsParser
{
    public const string Usage = "Usage: baton keep <room-dir>";

    public static KeepOptions Parse(IReadOnlyList<string> args)
    {
        string? roomDirectoryPath = null;

        var i = 0;
        while (i < args.Count)
        {
            var arg = args[i];
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

        return new KeepOptions(RoomDirectoryPath.Resolve(roomDirectoryPath));
    }
}
