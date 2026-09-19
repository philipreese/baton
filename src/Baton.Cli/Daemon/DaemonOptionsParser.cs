namespace Baton.Cli.Daemon;

/// <summary>Parses the deliberately small option surface of <c>baton daemon</c>.</summary>
internal static class DaemonOptionsParser
{
    public const string Usage =
        "Usage: baton daemon [--no-mutex] (the daemon is a background process with no 'status' subcommand; " +
        "monitor activity via 'baton queue list' or Fleet Glass)";

    public const string ObservabilityTryInvocation = "baton queue list";

    public static bool Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var noMutex = false;
        foreach (var arg in args)
        {
            if (arg == "--no-mutex")
            {
                if (noMutex)
                {
                    throw new CliArgumentException($"Option '--no-mutex' may only be specified once. {Usage}");
                }

                noMutex = true;
                continue;
            }

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                throw new CliArgumentException($"Unknown option '{arg}'. {Usage}", ObservabilityTryInvocation);
            }

            throw new CliArgumentException($"Unexpected argument '{arg}'. {Usage}", ObservabilityTryInvocation);
        }

        return noMutex;
    }
}
