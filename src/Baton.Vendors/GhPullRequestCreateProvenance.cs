using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Baton.Accounting;

namespace Baton.Vendors;

/// <summary>
/// Supervisor-resolved inputs for the broker's one direct GitHub CLI operation. Resolution happens
/// while a binding is built, before the worker can change its workspace, current directory, or PATH.
/// </summary>
public sealed record GhPullRequestCreateProvenance(
    string ExecutablePath,
    string Repository,
    string HeadBranch);

internal static class GhPullRequestCreateProvenanceResolver
{
    private static readonly TimeSpan GitProbeTimeout = TimeSpan.FromSeconds(10);

    public static GhPullRequestCreateProvenance? TryResolve(
        string? workingDirectory, string? repositorySourceDirectory) =>
        TryResolve(
            workingDirectory,
            repositorySourceDirectory,
            Environment.GetEnvironmentVariable("PATH"),
            OperatingSystem.IsWindows(),
            (directory, arguments) => RunGit(directory, [.. arguments]));

    internal static GhPullRequestCreateProvenance? TryResolve(
        string? workingDirectory,
        string? repositorySourceDirectory,
        string? searchPath,
        bool isWindows,
        Func<string, IReadOnlyList<string>, string?> gitProbe)
    {
        ArgumentNullException.ThrowIfNull(gitProbe);
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return null;
        }

        var executable = GhExecutableResolver.TryResolve(
            searchPath, workingDirectory, isWindows);
        if (executable is null)
        {
            return null;
        }

        var repositoryDirectory = !string.IsNullOrWhiteSpace(repositorySourceDirectory)
            ? repositorySourceDirectory
            : workingDirectory;
        var origin = gitProbe(repositoryDirectory!, ["config", "--get", "remote.origin.url"]);
        var identity = RepositoryIdentity.From(origin, gitCommonDirectoryPath: null)?.RemoteValue;
        var repository = identity is not null
            && identity.StartsWith("github.com/", StringComparison.Ordinal)
                ? GitHubRepository.TryCanonicalize(identity)
                : null;
        var branch = gitProbe(workingDirectory, ["rev-parse", "--abbrev-ref", "HEAD"])?.Trim();
        if (repository is null || string.IsNullOrWhiteSpace(branch)
            || branch.Equals("HEAD", StringComparison.Ordinal))
        {
            return null;
        }

        return new GhPullRequestCreateProvenance(executable, repository, branch);
    }

    private static string? RunGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = ChildProcessStartInfo.Create("git", info =>
        {
            info.WorkingDirectory = workingDirectory;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.StandardOutputEncoding = Encoding.UTF8;
            info.StandardErrorEncoding = Encoding.UTF8;
        });
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var timeout = new CancellationTokenSource(GitProbeTimeout);
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
            _ = stderr.GetAwaiter().GetResult();
            var value = stdout.GetAwaiter().GetResult().Trim();
            return process.ExitCode == 0 && value.Length > 0 ? value : null;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException
            or OperationCanceledException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

internal static class GhExecutableResolver
{
    public static string? TryResolve(string? searchPath, string workspaceRoot, bool isWindows)
    {
        if (string.IsNullOrWhiteSpace(searchPath))
        {
            return null;
        }

        var workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
        foreach (var rawDirectory in searchPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = rawDirectory.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(directory))
            {
                continue;
            }

            var candidate = Path.GetFullPath(Path.Combine(directory, isWindows ? "gh.exe" : "gh"));
            if (!File.Exists(candidate) || IsWithin(workspace, candidate)
                || !isWindows && !HasUnixExecuteBit(candidate))
            {
                continue;
            }

            // A PATH entry outside the workspace can still be a symlink back into it. Select the
            // final target, and apply the same boundary to that identity before trusting it.
            string target;
            try
            {
                target = new FileInfo(candidate).ResolveLinkTarget(returnFinalTarget: true)?.FullName
                    ?? candidate;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            if (IsWithin(workspace, target))
            {
                continue;
            }

            return Path.GetFullPath(target);
        }

        return null;
    }

    private static bool HasUnixExecuteBit(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            const UnixFileMode execute = UnixFileMode.UserExecute
                | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            return (File.GetUnixFileMode(path) & execute) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or PlatformNotSupportedException)
        {
            return false;
        }
    }

    internal static bool IsWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative.Equals(".", StringComparison.Ordinal)
            || (!relative.Equals("..", StringComparison.Ordinal)
                && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !Path.IsPathFullyQualified(relative));
    }
}
