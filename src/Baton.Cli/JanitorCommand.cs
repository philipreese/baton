namespace Baton.Cli;

/// <summary>
/// Runs the deterministic, repository-scoped janitor checker. It never dispatches a model or wakes a
/// conductor; the retained-worktree cleanup transaction remains owned by <see cref="QueueCommand"/>.
/// </summary>
public static class JanitorCommand
{
    public static Task<int> ExecuteAsync(
        JanitorOptions options,
        TextWriter output,
        CancellationToken cancellationToken = default,
        string? repositoryDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        return QueueCommand.ExecuteJanitorNowAsync(output, cancellationToken, repositoryDirectory);
    }
}
