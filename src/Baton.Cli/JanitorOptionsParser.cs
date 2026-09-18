namespace Baton.Cli;

/// <summary>Parses the deterministic operator-only janitor entry point.</summary>
public static class JanitorOptionsParser
{
    public const string Usage = "Usage: baton janitor now";

    public static JanitorOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count != 1 || !string.Equals(args[0], "now", StringComparison.Ordinal))
        {
            throw new CliArgumentException($"'baton janitor' takes exactly 'now'. {Usage}");
        }

        return new JanitorOptions();
    }
}
