namespace Baton.Mutation;

/// <summary>
/// #2098: the <c>PATH</c> the engine-run verify command is spawned with. The contract — what is
/// added, when, and why a hard-coded install path is never used — is stated once in
/// <c>spec/baton.md</c> §3's engine-run verify section ("The verify process's PATH"); this class is
/// the single place it is applied, and <see cref="VerifyRunner.RunProcessAsync"/> is its only
/// production caller. The measurement behind it lives there too, not here.
/// </summary>
public static class VerifyProcessPath
{
    /// <summary>
    /// How many ancestors of the directory holding <c>git.exe</c> are searched for
    /// <c>usr\bin\sh.exe</c>. Git for Windows puts its launcher one level below the install root
    /// (<c>cmd\git.exe</c>, <c>bin\git.exe</c>) or two (<c>mingw64\bin\git.exe</c>); a third level is
    /// headroom for a relocated layout, not a search of the whole drive.
    /// </summary>
    private const int MaxAncestorsAboveGit = 3;

    private const string ShellFileName = "sh.exe";
    private const string GitFileName = "git.exe";

    /// <summary>
    /// The engine's own ambient <c>PATH</c>, composed for the verify spawn: unchanged when <c>sh.exe</c>
    /// already resolves on it or when no <c>git.exe</c> does, otherwise with Git for Windows' own
    /// <c>usr\bin</c> (located from that <c>git.exe</c>) prepended. Never adds an entry that does not
    /// hold an <c>sh.exe</c>, so a bare <c>git.exe</c> outside a Git for Windows tree changes nothing.
    /// </summary>
    public static string Compose(string? ambientPath)
    {
        var ambient = ambientPath ?? string.Empty;
        var entries = ambient
            .Split(Path.PathSeparator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        if (entries.Any(entry => ContainsFile(entry, ShellFileName)))
        {
            return ambient;
        }

        var gitDirectory = entries.FirstOrDefault(entry => ContainsFile(entry, GitFileName));
        if (gitDirectory is null)
        {
            return ambient;
        }

        var shellDirectory = FindShellDirectoryAbove(gitDirectory);
        return shellDirectory is null
            ? ambient
            : ambient.Length == 0 ? shellDirectory : shellDirectory + Path.PathSeparator + ambient;
    }

    /// <summary>
    /// Walks from <paramref name="gitDirectory"/> up through <see cref="MaxAncestorsAboveGit"/>
    /// ancestors and returns the first <c>&lt;ancestor&gt;\usr\bin</c> that holds an <c>sh.exe</c>.
    /// </summary>
    private static string? FindShellDirectoryAbove(string gitDirectory)
    {
        DirectoryInfo? dir;
        try
        {
            dir = new DirectoryInfo(Path.GetFullPath(gitDirectory)).Parent;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        for (var depth = 0; dir is not null && depth < MaxAncestorsAboveGit; depth++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "usr", "bin");
            if (ContainsFile(candidate, ShellFileName))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Only a fully qualified entry counts — a relative one resolves against the spawn's cwd, which
    /// for the verify run is the worker-writable workspace, and an unreadable or malformed entry is
    /// simply "does not hold the file".
    /// </summary>
    private static bool ContainsFile(string directory, string fileName)
    {
        try
        {
            return Path.IsPathFullyQualified(directory) && File.Exists(Path.Combine(directory, fileName));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
