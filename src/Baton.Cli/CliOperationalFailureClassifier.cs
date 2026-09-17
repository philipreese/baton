namespace Baton.Cli;

internal static class CliOperationalFailureClassifier
{
    internal static bool IsOperational(Exception exception) =>
        exception is IOException or UnauthorizedAccessException;
}
