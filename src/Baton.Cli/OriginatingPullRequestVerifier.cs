using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Baton.Domain;
using Baton.Queue;
using Baton.Status;
using Baton.Store;
using Baton.Vendors;

namespace Baton.Cli;

/// <summary>Conductor verification for the sole originating pull-request authority a room can carry.</summary>
internal static class OriginatingPullRequestVerifier
{
    private static readonly TimeSpan GhVerificationTimeout = TimeSpan.FromSeconds(20);

    public static async Task<OriginatingPullRequestOwnership> VerifyAsync(
        string reference, string workspace, CancellationToken cancellationToken, string? expectedBranch = null,
        string? expectedHead = null)
    {
        var (repository, number) = ParseReference(reference);
        if (expectedHead is not null && !IsCanonicalSha(expectedHead))
        {
            throw new CliArgumentException(
                "The preserved continuation PR head is missing or malformed; originating PR ownership was refused before launch.");
        }

        var identity = GhPullRequestCreateProvenanceResolver.TryCaptureIdentity(workspace);
        var launchHead = await WorkspaceHead.TryCaptureAsync(workspace, cancellationToken).ConfigureAwait(false);
        if (identity is null || launchHead is null || identity.Repository != repository)
            throw new CliArgumentException("The workspace repository and launch HEAD must be readable before originating PR ownership can be granted.");
        ValidateExpectedBranch(identity, expectedBranch);
        if (expectedHead is not null)
        {
            await ValidatePreservedContinuationAsync(
                workspace, expectedHead, launchHead, cancellationToken).ConfigureAwait(false);
        }

        var gh = ResolveExecutable(workspace, Environment.GetEnvironmentVariable("PATH"), OperatingSystem.IsWindows());
        var start = ChildProcessStartInfo.Create(gh, info =>
        {
            info.WorkingDirectory = workspace;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.StandardOutputEncoding = Encoding.UTF8;
            info.StandardErrorEncoding = Encoding.UTF8;
        });
        foreach (var argument in new[]
        {
            "pr", "view", number.ToString(), "--repo", repository, "--json",
            "state,headRefName,headRefOid,isCrossRepository",
        }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new CliArgumentException("Could not start gh to verify '--originating-pr'.");
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bound.CancelAfter(GhVerificationTimeout);
        using var killOnCancellation = bound.Token.Register(() =>
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The child won the exit race; its exit status below remains authoritative.
            }
        });
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(bound.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (bound.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            throw new CliArgumentException("Timed out or was cancelled while verifying '--originating-pr'.");
        }
        var output = await stdoutTask.ConfigureAwait(false);
        _ = await stderrTask.ConfigureAwait(false);
        return ValidateResponse(repository, number, identity, launchHead, process.ExitCode, output, expectedHead);
    }

    internal static string ResolveExecutable(string workspace, string? searchPath, bool isWindows) =>
        OutsideWorkspaceExecutableResolver.TryResolve(searchPath, workspace, "gh", isWindows)
        ?? throw new CliArgumentException(
            "Could not resolve an absolute, link-free gh executable outside the worker workspace; '--originating-pr' was not verified.");

    internal static void ValidateExpectedBranch(GhPullRequestCreateIdentity identity, string? expectedBranch)
    {
        if (expectedBranch is not null
            && !string.Equals(identity.HeadBranch, expectedBranch, StringComparison.Ordinal))
        {
            throw new CliArgumentException(
                "The queue's recorded branch does not match the workspace branch; originating PR ownership was refused before launch.");
        }
    }

    internal static (string Repository, int Number) ParseReference(string reference)
    {
        var separator = reference.LastIndexOf('#');
        var repository = separator > 0 ? CanonicalRepository(reference[..separator]) : null;
        if (repository is null || !int.TryParse(reference[(separator + 1)..], out var number) || number <= 0)
            throw new CliArgumentException("'--originating-pr' must be owner/repository#number.");
        return (repository, number);
    }

    internal static string CanonicalReference(string repositoryIdentity, int number)
    {
        var repository = repositoryIdentity.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase)
            ? repositoryIdentity["github.com/".Length..]
            : repositoryIdentity;
        var canonical = CanonicalRepository(repository);
        if (canonical is null || number <= 0)
        {
            throw new CliArgumentException(
                "An originating pull request requires a canonical GitHub repository and positive PR number.");
        }

        return $"{canonical}#{number}";
    }

    internal static async Task<string?> ResolveRecoveryExpectedHeadAsync(
        DispatchOptions options, string workspace, CancellationToken cancellationToken)
    {
        if (options.OriginatingPullRequest is null || options.OriginatingPullRequestBranch is null)
        {
            return null;
        }

        try
        {
            var snapshot = await QueueStore.LoadAsync(BatonPaths.QueueFile, cancellationToken).ConfigureAwait(false);
            var recoveryRows = snapshot.Items
                .Where(item => RecoveryIdentityMatches(options, workspace, item))
                .ToArray();
            if (recoveryRows.Length == 0)
            {
                return null;
            }
            if (recoveryRows.Length != 1
                || !RecoveryEvidenceMatches(options, workspace, recoveryRows[0])
                || recoveryRows[0].OriginatingPullRequestRecoveryProofDigest is null
                || recoveryRows[0].OriginatingPullRequestRecoveryClaim is not null)
            {
                throw new CliArgumentException(
                    "No unused queue-owned recovery admission matches the launched continuation room; ownership was refused before launch.");
            }

            var proof = await Console.In.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (proof is null)
            {
                throw new CliArgumentException(
                    "The queue recovery admission proof was absent; originating PR ownership was refused before launch.");
            }

            string? claimedHead = null;
            await QueueStore.MutateAsync(BatonPaths.QueueFile, current =>
            {
                var matches = current.Items
                    .Where(item => RecoveryEvidenceMatches(options, workspace, item)
                        && item.OriginatingPullRequestRecoveryProofDigest is { } digest
                        && FixedTimeEquals(digest, proof)
                        && item.OriginatingPullRequestRecoveryClaim is null)
                    .ToArray();
                if (matches.Length != 1)
                {
                    return current;
                }

                var item = matches[0];
                if (!RecoveryEvidenceMatches(options, workspace, item)
                    || item.OriginatingPullRequestRecoveryClaim is not null)
                {
                    return current;
                }

                claimedHead = item.ExpectedOriginatingPullRequestHead;
                var claimed = item with
                {
                    OriginatingPullRequestRecoveryClaim = item.AttemptId,
                    OriginatingPullRequestRecoveryProofDigest = null,
                };
                return current with
                {
                    Items = current.Items
                        .Select(candidate => ReferenceEquals(candidate, item) ? claimed : candidate)
                        .ToArray(),
                };
            }, cancellationToken).ConfigureAwait(false);
            if (claimedHead is null)
            {
                throw new CliArgumentException(
                    "No unused queue-owned recovery admission matches the launched continuation room; ownership was refused before launch.");
            }

            return claimedHead;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new CliArgumentException(
                $"The durable queue evidence for preserved continuation ownership could not be read: {ex.Message}");
        }

    }

    internal static string ValidateRecoveryEvidence(
        DispatchOptions options, string workspace, QueueSnapshot snapshot)
    {
        if (options.OriginatingPullRequest is null || options.OriginatingPullRequestBranch is null)
        {
            throw new CliArgumentException(
                "The preserved continuation PR recovery identity is incomplete; ownership was refused before launch.");
        }

        var matches = snapshot.Items.Where(item => RecoveryEvidenceMatches(options, workspace, item)).ToArray();
        if (matches.Length != 1)
        {
            throw new CliArgumentException(
                "No exact durable queue item owns the preserved continuation PR recovery identity; ownership was refused before launch.");
        }

        var item = matches[0];
        if (item.OriginatingPullRequestRecoveryProofDigest is null
            || item.OriginatingPullRequestRecoveryClaim is not null)
        {
            throw new CliArgumentException(
                "The durable queue item does not match an unused launched continuation PR recovery attempt and room; ownership was refused before launch.");
        }

        return item.ExpectedOriginatingPullRequestHead!;
    }

    private static bool RecoveryEvidenceMatches(DispatchOptions options, string workspace, QueueItem item)
    {
        return item.State == QueueItemState.Launched && RecoveryIdentityMatches(options, workspace, item);
    }

    private static bool RecoveryIdentityMatches(DispatchOptions options, string workspace, QueueItem item)
    {
        if (options.OriginatingPullRequest is null
            || options.OriginatingPullRequestBranch is null
            || item.Stage != WorkStage.Continue
            || item.AttemptId is not { } currentAttempt
            || item.AttemptEnvelope is not { } envelope
            || !string.Equals(currentAttempt.Value, envelope.AttemptId.Value, StringComparison.Ordinal)
            || envelope.Stage != WorkStage.Continue
            || (item.RoomDirectory ?? envelope.RoomDirectory) is not { Length: > 0 } recordedRoom
            || !SameWorkspace(recordedRoom, options.RoomDirectoryPath)
            || item.ExpectedOriginatingPullRequestHead is not { } expectedHead
            || !IsCanonicalSha(expectedHead)
            || item.PullRequest is not { } pullRequest
            || envelope.PullRequest != pullRequest
            || item.Repository is not { Length: > 0 } repository
            || !string.Equals(CanonicalReference(repository, pullRequest), options.OriginatingPullRequest, StringComparison.Ordinal)
            || !string.Equals(item.Branch, options.OriginatingPullRequestBranch, StringComparison.Ordinal))
        {
            return false;
        }

        return SameWorkspace(item.Workspace, workspace);
    }

    private static bool FixedTimeEquals(string digest, string proof)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(digest),
                SHA256.HashData(Encoding.UTF8.GetBytes(proof)));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    internal static OriginatingPullRequestOwnership ValidateResponse(
        string repository,
        int number,
        GhPullRequestCreateIdentity identity,
        string launchHead,
        int exitCode,
        string output,
        string? expectedHead = null)
    {
        if (expectedHead is not null && !IsCanonicalSha(expectedHead))
        {
            throw new CliArgumentException(
                "The preserved continuation PR head is missing or malformed; originating PR ownership was refused before launch.");
        }

        if (exitCode != 0)
        {
            throw new CliArgumentException("The originating pull request could not be read.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(output);
        }
        catch (JsonException)
        {
            throw new CliArgumentException("The originating pull request returned an unreadable response.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("state", out var stateValue)
                || !root.TryGetProperty("headRefName", out var branchValue)
                || !root.TryGetProperty("headRefOid", out var headValue)
                || stateValue.ValueKind != JsonValueKind.String
                || branchValue.ValueKind != JsonValueKind.String
                || headValue.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("isCrossRepository", out var crossRepositoryValue)
                || crossRepositoryValue.ValueKind != JsonValueKind.False)
            {
                throw new CliArgumentException(
                    "The originating pull request response must include isCrossRepository as the JSON boolean false.");
            }

            var state = stateValue.GetString();
            var branch = branchValue.GetString();
            var head = headValue.GetString();
            var requiredHead = expectedHead ?? launchHead;
            if (!string.Equals(state, "OPEN", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(branch, identity.HeadBranch, StringComparison.Ordinal)
                || !string.Equals(head, requiredHead, StringComparison.OrdinalIgnoreCase))
            {
                throw new CliArgumentException(
                    expectedHead is null
                        ? "The originating pull request must be open and match the workspace repository, branch, and pre-dispatch HEAD."
                        : "The originating pull request must be open at the retained continuation head and match the workspace repository and branch.");
            }

            return new OriginatingPullRequestOwnership(repository, number, branch!, launchHead);
        }
    }

    private static async Task ValidatePreservedContinuationAsync(
        string workspace, string expectedHead, string launchHead, CancellationToken cancellationToken)
    {
        if (!IsCanonicalSha(launchHead))
        {
            throw new CliArgumentException(
                "The current workspace HEAD is missing or malformed; preserved continuation ownership was refused before launch.");
        }

        var git = OutsideWorkspaceExecutableResolver.TryResolve(
            Environment.GetEnvironmentVariable("PATH"), workspace, "git", OperatingSystem.IsWindows())
            ?? throw new CliArgumentException(
                "Could not resolve an absolute, link-free git executable outside the worker workspace; preserved continuation ownership was refused before launch.");

        var status = await RunGitAsync(
            git, workspace, ["status", "--porcelain=v1", "--untracked-files=all"], cancellationToken)
            .ConfigureAwait(false);
        if (!status.Started || status.ExitCode != 0 || status.Stdout.Trim().Length > 0)
        {
            throw new CliArgumentException(
                "The preserved continuation workspace must be clean and readable; ownership was refused before launch.");
        }

        var ancestry = await RunGitAsync(
            git, workspace, ["merge-base", "--is-ancestor", expectedHead, launchHead], cancellationToken)
            .ConfigureAwait(false);
        if (!ancestry.Started || ancestry.ExitCode != 0)
        {
            throw new CliArgumentException(
                "The current workspace HEAD is not a readable descendant of the retained continuation head; ownership was refused before launch.");
        }
    }

    private static bool IsCanonicalSha(string value) =>
        value.Length == 40 && value.All(char.IsAsciiHexDigit);

    internal static bool SameWorkspace(string recorded, string canonicalWorkspace)
    {
        try
        {
            var recordedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(recorded));
            var requestedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(canonicalWorkspace));
            if (!HasLinkFreeExistingPath(recordedPath) || !HasLinkFreeExistingPath(requestedPath))
            {
                return false;
            }

            return string.Equals(
                recordedPath,
                requestedPath,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException
            or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool HasLinkFreeExistingPath(string path)
    {
        if (File.Exists(path))
        {
            return false;
        }

        for (DirectoryInfo? current = new(path); current is not null; current = current.Parent)
        {
            current.Refresh();
            if (File.Exists(current.FullName) || current.LinkTarget is not null)
            {
                return false;
            }

            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<(bool Started, int ExitCode, string Stdout, string Stderr)> RunGitAsync(
        string executable, string workspace, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = ChildProcessStartInfo.Create(executable, info =>
        {
            info.WorkingDirectory = workspace;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.StandardOutputEncoding = Encoding.UTF8;
            info.StandardErrorEncoding = Encoding.UTF8;
        });
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (false, -1, string.Empty, ex.Message);
        }

        if (process is null)
        {
            return (false, -1, string.Empty, "git did not start.");
        }

        using (process)
        using (var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            bound.CancelAfter(GhVerificationTimeout);
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(bound.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (bound.IsCancellationRequested)
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) { }

                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
                return (false, -1, string.Empty, "git verification timed out or was cancelled.");
            }

            return (
                true,
                process.ExitCode,
                await stdoutTask.ConfigureAwait(false),
                await stderrTask.ConfigureAwait(false));
        }
    }

    private static string? CanonicalRepository(string value)
    {
        var parts = value.Trim().Replace('\\', '/').Trim('/').Split('/');
        return parts.Length == 2 && parts.All(part => part.Length > 0 && part.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))
            ? string.Join('/', parts).ToLowerInvariant() : null;
    }
}
