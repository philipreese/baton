using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Baton.Accounting;

namespace Baton.Vendors;

/// <summary>
/// The repository/head authority captured by the conductor before a worker receives its workspace.
/// It is persisted in the room binding and reused unchanged by resume and redispatch.
/// </summary>
public sealed record GhPullRequestCreateIdentity(string Repository, string HeadBranch);

/// <summary>
/// Supervisor-resolved inputs for the broker's one direct GitHub CLI operation.
/// </summary>
public sealed record GhPullRequestCreateProvenance(
    string ExecutablePath,
    string Repository,
    string HeadBranch);

public static class GhPullRequestCreateProvenanceResolver
{
    private static readonly TimeSpan GitProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Captures immutable repository/head authority for a fresh conductor dispatch. The Git executable
    /// is resolved absolutely outside the workspace before either local probe is run.
    /// </summary>
    public static GhPullRequestCreateIdentity? TryCaptureIdentity(string? repositoryDirectory) =>
        TryCaptureIdentity(
            repositoryDirectory,
            Environment.GetEnvironmentVariable("PATH"),
            OperatingSystem.IsWindows(),
            (executable, directory, arguments) => RunGit(executable, directory, [.. arguments]));

    internal static GhPullRequestCreateIdentity? TryCaptureIdentity(
        string? repositoryDirectory,
        string? searchPath,
        bool isWindows,
        Func<string, string, IReadOnlyList<string>, string?> gitProbe)
    {
        ArgumentNullException.ThrowIfNull(gitProbe);
        var normalizedDirectory = TryNormalizeExistingDirectory(repositoryDirectory);
        if (normalizedDirectory is null)
        {
            return null;
        }

        var git = OutsideWorkspaceExecutableResolver.TryResolve(
            searchPath, normalizedDirectory, "git", isWindows);
        if (git is null)
        {
            return null;
        }

        var origin = gitProbe(git, normalizedDirectory, ["config", "--get", "remote.origin.url"]);
        var identity = RepositoryIdentity.From(origin, gitCommonDirectoryPath: null)?.RemoteValue;
        var repository = identity is not null
            && identity.StartsWith("github.com/", StringComparison.Ordinal)
                ? GitHubRepository.TryCanonicalize(identity)
                : null;
        var branch = gitProbe(git, normalizedDirectory, ["rev-parse", "--abbrev-ref", "HEAD"])?.Trim();
        return repository is not null && !string.IsNullOrWhiteSpace(branch)
            && !branch.Equals("HEAD", StringComparison.Ordinal)
                ? new GhPullRequestCreateIdentity(repository, branch)
                : null;
    }

    /// <summary>
    /// Resolves only the machine-local GitHub CLI. Repository/head authority must already be present
    /// in the durable binding; mutable workspace Git configuration is never consulted here.
    /// </summary>
    public static GhPullRequestCreateProvenance? TryResolve(
        string? workingDirectory, GhPullRequestCreateIdentity? expectedIdentity) =>
        TryResolve(
            workingDirectory,
            expectedIdentity,
            Environment.GetEnvironmentVariable("PATH"),
            OperatingSystem.IsWindows());

    internal static GhPullRequestCreateProvenance? TryResolve(
        string? workingDirectory,
        GhPullRequestCreateIdentity? expectedIdentity,
        string? searchPath,
        bool isWindows)
    {
        var normalizedDirectory = TryNormalizeExistingDirectory(workingDirectory);
        if (normalizedDirectory is null || expectedIdentity is null)
        {
            return null;
        }

        var repository = GitHubRepository.TryCanonicalize(expectedIdentity.Repository);
        var branch = expectedIdentity.HeadBranch.Trim();
        if (repository is null || string.IsNullOrWhiteSpace(branch)
            || branch.Equals("HEAD", StringComparison.Ordinal))
        {
            return null;
        }

        var executable = OutsideWorkspaceExecutableResolver.TryResolve(
            searchPath, normalizedDirectory, "gh", isWindows);
        return executable is null
            ? null
            : new GhPullRequestCreateProvenance(executable, repository, branch);
    }

    /// <summary>
    /// Stamps a fresh or explicitly workspace-moved binding whenever its primary or declared
    /// exhaustion fallback can reach the Codex direct-create broker. Other bindings retain their
    /// existing value without probing Git.
    /// </summary>
    public static WorkerBindingConfigEntry CaptureIdentityFor(
        WorkerBindingConfigEntry entry, string? repositoryDirectory) =>
        CaptureIdentityFor(entry, repositoryDirectory, TryCaptureIdentity);

    internal static WorkerBindingConfigEntry CaptureIdentityFor(
        WorkerBindingConfigEntry entry,
        string? repositoryDirectory,
        Func<string?, GhPullRequestCreateIdentity?> captureIdentity)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(captureIdentity);
        var hasCodexConsumer = entry.Adapter.Equals("codex", StringComparison.OrdinalIgnoreCase)
            || entry.FallbackOnExhaustion?.Adapter.Equals(
                "codex", StringComparison.OrdinalIgnoreCase) == true;
        if (!hasCodexConsumer
            || !RequiresTrustedIdentity(entry.PermissionGrant))
        {
            return entry;
        }

        return entry with { PullRequestCreateIdentity = captureIdentity(repositoryDirectory) };
    }

    private static string? TryNormalizeExistingDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }
        try
        {
            var fullPath = Path.GetFullPath(directory);
            return Directory.Exists(fullPath) ? fullPath : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException
            or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static bool RequiresTrustedIdentity(PermissionGrant? grant) =>
        grant is not null
        && OwnPullRequestOnlyRule.AppliesTo(grant)
        && ShellCommandPatternMatcher.EvaluateChainedCommand(
            "gh pr create --draft",
            grant.ShellCommandPatterns,
            grant.DeniedShellCommandPatterns,
            grant.DeniedShellCommandExceptions).IsAllowed;

    private static string? RunGit(string executable, string workingDirectory, params string[] arguments)
    {
        var startInfo = ChildProcessStartInfo.Create(executable, info =>
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

internal static class OutsideWorkspaceExecutableResolver
{
    internal static string? TryValidateAbsolute(
        string candidate, string workspaceRoot, string executableName, bool isWindows,
        bool requireFileName = true)
    {
        var workspace = TryGetLinkFreeFullPath(workspaceRoot);
        var resolved = TryGetLinkFreeFullPath(candidate);
        var expectedFileName = isWindows ? executableName + ".exe" : executableName;
        return workspace is null || resolved is null || IsWithin(workspace, resolved)
            || requireFileName && !Path.GetFileName(resolved).Equals(
                expectedFileName, StringComparison.OrdinalIgnoreCase)
            || !isWindows && !HasUnixExecuteBit(resolved)
                ? null
                : resolved;
    }

    public static string? TryResolve(
        string? searchPath, string workspaceRoot, string executableName, bool isWindows)
    {
        if (string.IsNullOrWhiteSpace(searchPath) || string.IsNullOrWhiteSpace(workspaceRoot)
            || string.IsNullOrWhiteSpace(executableName))
        {
            return null;
        }

        var workspace = TryGetLinkFreeFullPath(workspaceRoot);
        if (workspace is null)
        {
            return null;
        }

        var fileName = isWindows ? executableName + ".exe" : executableName;
        foreach (var rawDirectory in searchPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = rawDirectory.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(directory))
            {
                continue;
            }

            string candidate;
            try
            {
                candidate = Path.GetFullPath(Path.Combine(directory, fileName));
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
            {
                continue;
            }

            var resolved = TryGetLinkFreeFullPath(candidate);
            if (resolved is null || IsWithin(workspace, resolved)
                || !isWindows && !HasUnixExecuteBit(resolved))
            {
                continue;
            }

            return resolved;
        }

        return null;
    }

    /// <summary>
    /// Returns an absolute identity only when every existing component from the volume root through
    /// the leaf is link-free. Rejecting reparse ancestry avoids treating an outside-looking alias as
    /// stronger provenance than its worker-controlled target.
    /// </summary>
    private static string? TryGetLinkFreeFullPath(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path))
            {
                return null;
            }

            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root))
            {
                return null;
            }

            var current = root;
            foreach (var component in Path.GetRelativePath(root, full).Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, component);
                var isDirectory = Directory.Exists(current);
                if (!isDirectory && !File.Exists(current))
                {
                    return null;
                }

                FileSystemInfo info = isDirectory ? new DirectoryInfo(current) : new FileInfo(current);
                info.Refresh();
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null)
                {
                    return null;
                }
            }

            return full;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException
            or UnauthorizedAccessException)
        {
            return null;
        }
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
