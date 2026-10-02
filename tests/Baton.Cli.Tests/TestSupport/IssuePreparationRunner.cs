namespace Baton.Cli.Tests.TestSupport;

internal static class IssuePreparationRunner
{
    internal static Task<(int ExitCode, string Output)> NoCollisions(
        string file, IReadOnlyList<string> arguments, string directory, CancellationToken token) =>
        Task.FromResult(arguments switch
        {
            ["show-ref", "--verify", "--quiet", ..] => (1, string.Empty),
            ["ls-remote", "--heads", "origin", ..] => (0, string.Empty),
            _ => throw new InvalidOperationException($"Unexpected preparation probe: {file} {string.Join(' ', arguments)}"),
        });
}
