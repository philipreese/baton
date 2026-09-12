namespace Baton.Cli.Daemon;

/// <summary>
/// Finds known Git locks before queue workspace reuse; the policy is in spec/baton.md §13 (#2115).
/// </summary>
internal static class GitWorkspaceLockProbe
{
    private static readonly string[] KnownLockNames = ["index.lock", "HEAD.lock"];

    /// <summary>
    /// Returns existing known locks in stable order. For a linked worktree, <c>.git</c> is a gitfile;
    /// its relative target is resolved from the workspace, as Git does.
    /// </summary>
    internal static IReadOnlyList<string> FindExisting(
        string workspace,
        Func<string, FileAttributes>? getAttributes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        getAttributes ??= File.GetAttributes;

        var dotGit = Path.Combine(Path.GetFullPath(workspace), ".git");
        if (!TryGetAttributes(dotGit, getAttributes, out var dotGitAttributes))
        {
            return [];
        }

        string gitDirectory;
        if ((dotGitAttributes & FileAttributes.Directory) != 0)
        {
            gitDirectory = dotGit;
        }
        else
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
            var gitDirectoryAttributes = getAttributes(gitDirectory);
            if ((gitDirectoryAttributes & FileAttributes.Directory) == 0)
            {
                throw new InvalidDataException($"Git directory target '{gitDirectory}' is not a directory.");
            }
        }
        return KnownLockNames
            .Select(name => Path.Combine(gitDirectory, name))
            .Where(path => TryGetAttributes(path, getAttributes, out _))
            .ToList();
    }

    /// <summary>
    /// Unlike <see cref="File.Exists(string?)"/>, preserves access and metadata errors so the caller
    /// can fail closed. Only a path that the filesystem says is absent returns <see langword="false"/>.
    /// </summary>
    private static bool TryGetAttributes(
        string path,
        Func<string, FileAttributes> getAttributes,
        out FileAttributes attributes)
    {
        try
        {
            attributes = getAttributes(path);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }
}
