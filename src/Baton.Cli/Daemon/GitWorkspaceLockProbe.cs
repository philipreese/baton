namespace Baton.Cli.Daemon;

/// <summary>
/// Finds the Git lock files that make a queued workspace unsafe to reuse. A lock file carries no
/// owner identity, so observing one can justify refusing a launch but never deleting it (#2115).
/// </summary>
internal static class GitWorkspaceLockProbe
{
    private static readonly string[] KnownLockNames = ["index.lock", "HEAD.lock"];

    /// <summary>
    /// Returns existing known locks in stable order. For a linked worktree, <c>.git</c> is a gitfile;
    /// its relative target is resolved from the workspace, as Git does.
    /// </summary>
    internal static IReadOnlyList<string> FindExisting(string workspace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);

        var dotGit = Path.Combine(Path.GetFullPath(workspace), ".git");
        string? gitDirectory;
        if (Directory.Exists(dotGit))
        {
            gitDirectory = dotGit;
        }
        else if (File.Exists(dotGit))
        {
            var declaration = File.ReadLines(dotGit).FirstOrDefault();
            const string prefix = "gitdir:";
            if (declaration is null || !declaration.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Git metadata file '{dotGit}' has no gitdir declaration.");
            }

            var target = declaration[prefix.Length..].Trim();
            if (target.Length == 0)
            {
                throw new InvalidDataException($"Git metadata file '{dotGit}' has an empty gitdir declaration.");
            }

            gitDirectory = Path.GetFullPath(target, Path.GetDirectoryName(dotGit)!);
        }
        else
        {
            return [];
        }

        return KnownLockNames
            .Select(name => Path.Combine(gitDirectory, name))
            .Where(File.Exists)
            .ToList();
    }
}
