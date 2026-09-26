using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Baton;
using Baton.Status;
using Baton.Vendors;

namespace Baton.Cli.Mcp;

/// <summary>
/// Baton's one-file restore primitive. The source revision is supplied by the dispatch-captured
/// binding, never by the worker call. The final write is a handle-anchored compare-and-swap.
/// </summary>
public sealed class ExactFileRestoreTool : IMcpTool
{
    public const string AuditFileName = "exact-file-restores.jsonl";
    public const string ToolName = "restore-exact-file";

    private static readonly TimeSpan DefaultGitTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan GitTeardownTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AuditLockTimeout = TimeSpan.FromSeconds(10);

    private readonly string _workspaceDirectory;
    private readonly string _admittedBaseRevision;
    private readonly string _executionId;
    private readonly string _roomDirectoryPath;
    private readonly ExactFileRestoreTestHooks _testHooks;

    public ExactFileRestoreTool(
        string workspaceDirectory,
        string admittedBaseRevision,
        string executionId,
        string roomDirectoryPath)
        : this(
            workspaceDirectory,
            admittedBaseRevision,
            executionId,
            roomDirectoryPath,
            new ExactFileRestoreTestHooks())
    {
    }

    internal ExactFileRestoreTool(
        string workspaceDirectory,
        string admittedBaseRevision,
        string executionId,
        string roomDirectoryPath,
        ExactFileRestoreTestHooks testHooks)
    {
        _workspaceDirectory = workspaceDirectory;
        _admittedBaseRevision = admittedBaseRevision;
        _executionId = executionId;
        _roomDirectoryPath = roomDirectoryPath;
        _testHooks = testHooks;
    }

    public string Name => ToolName;

    public string Description =>
        "Restore exactly one explicitly named tracked repository-relative file from the dispatch-admitted "
        + "base commit. This never accepts a revision, glob, directory, or multi-file path. Set "
        + "'acknowledgeDirtyFile' to true only when the named file's current edits may be discarded.";

    public string InputSchemaJson => """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string" },
            "acknowledgeDirtyFile": { "type": "boolean" }
          },
          "required": ["path", "acknowledgeDirtyFile"],
          "additionalProperties": false
        }
        """;

    public McpToolCallResult Call(JsonElement arguments) =>
        CallAsync(arguments).GetAwaiter().GetResult();

    public async Task<McpToolCallResult> CallAsync(
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return Refusal("arguments must be an object");
        }

        foreach (var property in arguments.EnumerateObject())
        {
            if (property.Name is not "path" and not "acknowledgeDirtyFile")
            {
                return Refusal($"unknown argument '{property.Name}' is not accepted");
            }
        }

        if (!arguments.TryGetProperty("path", out var pathElement)
            || pathElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(pathElement.GetString()))
        {
            return Refusal("'path' is required and must be a non-empty repository-relative string");
        }

        if (!arguments.TryGetProperty("acknowledgeDirtyFile", out var acknowledgement)
            || acknowledgement.ValueKind != JsonValueKind.True
                && acknowledgement.ValueKind != JsonValueKind.False)
        {
            return Refusal("'acknowledgeDirtyFile' is required and must be a boolean");
        }

        var requestedPath = pathElement.GetString()!;
        if (!TryNormalizeLiteralPath(
                requestedPath,
                out var relativePath,
                out var pathParts,
                out var pathRefusal))
        {
            return Refusal(pathRefusal!);
        }

        if (!Path.IsPathFullyQualified(_workspaceDirectory)
            || !Directory.Exists(_workspaceDirectory))
        {
            return Refusal("the admitted worker workspace is not an existing absolute directory");
        }

        var baseRevision = _admittedBaseRevision.Trim();
        if (!IsCanonicalRevision(baseRevision))
        {
            return Refusal("the dispatch-admitted base revision is not a canonical commit SHA");
        }

        try
        {
            var rootResult = await RunGitAsync(
                ["rev-parse", "--show-toplevel"],
                cancellationToken).ConfigureAwait(false);
            if (!rootResult.Succeeded)
            {
                return GitRefusal("the worker workspace is not a readable git repository", rootResult);
            }

            var repositoryRoot = Path.GetFullPath(rootResult.Stdout.Trim());
            using var transaction = ExactFileRestoreFileTransaction.Open(repositoryRoot, pathParts);

            var resolvedBase = await RunGitAsync(
                ["rev-parse", "--verify", $"{baseRevision}^{{commit}}"],
                cancellationToken).ConfigureAwait(false);
            if (!resolvedBase.Succeeded
                || !string.Equals(
                    resolvedBase.Stdout.Trim(),
                    baseRevision,
                    StringComparison.Ordinal))
            {
                return GitRefusal(
                    "the requested source revision is not the dispatch-admitted commit",
                    resolvedBase);
            }

            var ancestry = await RunGitAsync(
                ["merge-base", "--is-ancestor", baseRevision, "HEAD"],
                cancellationToken).ConfigureAwait(false);
            if (!ancestry.Succeeded)
            {
                return GitRefusal(
                    "the admitted base revision is not an ancestor of the current workspace HEAD",
                    ancestry);
            }

            var tracked = await RunGitAsync(
                ["--literal-pathspecs", "ls-files", "-z", "--error-unmatch", "--full-name", "--", relativePath],
                cancellationToken).ConfigureAwait(false);
            var expectedTracked = Encoding.UTF8.GetBytes(relativePath + "\0");
            if (!tracked.Succeeded
                || !tracked.RawStdout.AsSpan().SequenceEqual(expectedTracked))
            {
                return GitRefusal(
                    "'path' is not exactly one tracked file in the current repository",
                    tracked);
            }

            var sourceBlob = await RunGitAsync(
                ["rev-parse", "--verify", $"{baseRevision}:{relativePath}"],
                cancellationToken).ConfigureAwait(false);
            if (!sourceBlob.Succeeded
                || !IsCanonicalRevision(sourceBlob.Stdout.Trim()))
            {
                return GitRefusal(
                    "the named file does not exist as a file in the admitted base commit",
                    sourceBlob);
            }

            // The dirty acknowledgement is bound to this exact snapshot by the final transaction CAS.
            var before = transaction.Capture();

            var status = await RunGitAsync(
                ["--literal-pathspecs", "status", "--porcelain=v1", "-z", "--untracked-files=all", "--", relativePath],
                cancellationToken).ConfigureAwait(false);
            if (!status.Succeeded)
            {
                return GitRefusal(
                    "could not determine whether the named file is dirty",
                    status);
            }

            if (status.RawStdout.Length > 0 && !acknowledgement.GetBoolean())
            {
                return Refusal(
                    "the named file is dirty; set 'acknowledgeDirtyFile' to true to acknowledge this exact file");
            }

            var source = await RunGitAsync(
                ["cat-file", "blob", $"{baseRevision}:{relativePath}"],
                cancellationToken).ConfigureAwait(false);
            if (!source.Succeeded)
            {
                return GitRefusal("could not read the admitted base blob", source);
            }

            var sourceBlobId = sourceBlob.Stdout.Trim();
            var restoredBlob = GitBlobId(source.RawStdout);
            if (!string.Equals(restoredBlob, sourceBlobId, StringComparison.Ordinal))
            {
                return Refusal("the admitted base bytes did not match Git's recorded blob");
            }

            var auditPath = Path.Combine(
                _roomDirectoryPath,
                ".baton",
                AuditFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(auditPath)!);

            var result = await Task.Run(
                () => MutexGuardedFileLock.RunUnderLock(
                    auditPath,
                    "baton-exact-file-restore",
                    AuditLockTimeout,
                    () => transaction.Commit(
                        before,
                        source.RawStdout,
                        quarantine => AppendAudit(
                            auditPath,
                            CreateAudit(
                                ExactFileRestoreAuditState.Prepared,
                                relativePath,
                                before.Blob,
                                baseRevision,
                                sourceBlobId,
                                restoredBlob,
                                quarantine,
                                null)),
                        () => AppendAudit(
                            auditPath,
                            CreateAudit(
                                ExactFileRestoreAuditState.Committed,
                                relativePath,
                                before.Blob,
                                baseRevision,
                                sourceBlobId,
                                restoredBlob,
                                null,
                                null)),
                        () => AppendAudit(
                            auditPath,
                            CreateAudit(
                                ExactFileRestoreAuditState.RolledBack,
                                relativePath,
                                before.Blob,
                                baseRevision,
                                sourceBlobId,
                                restoredBlob,
                                null,
                                null)),
                        (reason, quarantine) => AppendAudit(
                            auditPath,
                            CreateAudit(
                                ExactFileRestoreAuditState.RecoveryRequired,
                                relativePath,
                                before.Blob,
                                baseRevision,
                                sourceBlobId,
                                restoredBlob,
                                quarantine,
                                reason)),
                        _testHooks.BeforeCompareAndSwap,
                        _testHooks.BeforeRollbackRestore)),
                CancellationToken.None).ConfigureAwait(false);

            if (!result.Succeeded)
            {
                var quarantine = result.Quarantine is null
                    ? string.Empty
                    : $" Preserved quarantine: {result.Quarantine}.";
                return Refusal($"{result.Reason}.{quarantine}".Trim());
            }

            return new McpToolCallResult(
                $"Restored exactly '{relativePath}' from {baseRevision}; before blob "
                + $"{before.Blob ?? "missing"}, restored blob {restoredBlob}. Audit: {auditPath}");
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or PlatformNotSupportedException)
        {
            return Refusal($"restore refused before completion: {ex.Message}");
        }
    }

    private ExactFileRestoreAudit CreateAudit(
        ExactFileRestoreAuditState state,
        string path,
        string? beforeBlob,
        string sourceRevision,
        string sourceBlob,
        string restoredBlob,
        string? quarantine,
        string? reason) =>
        new(
            Version: 2,
            State: state.ToString(),
            ExecutionId: _executionId,
            Path: path,
            BeforeBlob: beforeBlob,
            SourceRevision: sourceRevision,
            SourceBlob: sourceBlob,
            RestoredBlob: restoredBlob,
            RecordedAtUtc: DateTimeOffset.UtcNow,
            Quarantine: quarantine,
            Reason: reason);

    private void AppendAudit(string auditPath, ExactFileRestoreAudit audit)
    {
        if (_testHooks.AuditFailure?.Invoke(
                Enum.Parse<ExactFileRestoreAuditState>(audit.State)) is { } failure)
        {
            throw failure;
        }

        using var stream = new FileStream(
            auditPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            4096,
            FileOptions.WriteThrough);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true);
        writer.WriteLine(JsonSerializer.Serialize(audit));
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private async Task<GitResult> RunGitAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ChildProcessTree child;
        try
        {
            child = ChildProcessTree.Start(_testHooks.GitFileName, startInfo =>
            {
                startInfo.WorkingDirectory = _workspaceDirectory;
                startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
                startInfo.Environment["GCM_INTERACTIVE"] = "Never";
                startInfo.Environment["GIT_LITERAL_PATHSPECS"] = "1";
                if (_testHooks.GitEnvironment is not null)
                {
                    foreach (var (name, value) in _testHooks.GitEnvironment)
                    {
                        startInfo.Environment[name] = value;
                    }
                }
                foreach (var argument in arguments)
                {
                    startInfo.ArgumentList.Add(argument);
                }
            });
        }
        catch (Exception ex) when (ex is IOException
            or InvalidOperationException
            or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception)
        {
            return GitResult.StartFailure(ex.Message);
        }

        using (child)
        using (var stdoutBuffer = new MemoryStream())
        using (var stderrBuffer = new MemoryStream())
        using (var timeout = new CancellationTokenSource(
            _testHooks.GitTimeout ?? DefaultGitTimeout))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token))
        {
            var stdoutTask = child.StandardOutput.BaseStream.CopyToAsync(stdoutBuffer);
            var stderrTask = child.StandardError.BaseStream.CopyToAsync(stderrBuffer);
            try
            {
                await child.Process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
                await Task.WhenAll(stdoutTask, stderrTask)
                    .WaitAsync(GitTeardownTimeout)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                child.Terminate();
                await FinishGitTeardownAsync(child, stdoutTask, stderrTask).ConfigureAwait(false);
                return cancellationToken.IsCancellationRequested
                    ? GitResult.Cancelled(stdoutBuffer.ToArray(), stderrBuffer.ToArray())
                    : GitResult.TimedOut(stdoutBuffer.ToArray(), stderrBuffer.ToArray());
            }
            catch (TimeoutException)
            {
                child.Terminate();
                await FinishGitTeardownAsync(child, stdoutTask, stderrTask).ConfigureAwait(false);
                return GitResult.TeardownTimedOut(stdoutBuffer.ToArray(), stderrBuffer.ToArray());
            }

            var stdout = stdoutBuffer.ToArray();
            var stderr = stderrBuffer.ToArray();
            return child.Process.ExitCode == 0
                ? GitResult.Success(stdout, stderr)
                : GitResult.Nonzero(child.Process.ExitCode, stdout, stderr);
        }
    }

    private static async Task FinishGitTeardownAsync(
        ChildProcessTree child,
        Task stdoutTask,
        Task stderrTask)
    {
        using var bound = new CancellationTokenSource(GitTeardownTimeout);
        try
        {
            await Task.WhenAll(
                    child.Process.WaitForExitAsync(bound.Token),
                    stdoutTask.WaitAsync(bound.Token),
                    stderrTask.WaitAsync(bound.Token))
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException
            or IOException
            or ObjectDisposedException
            or InvalidOperationException)
        {
            // The bounded teardown has already killed the contained tree. The caller receives a refusal.
        }
    }

    private static McpToolCallResult GitRefusal(string context, GitResult result) =>
        Refusal(result.Failure is null ? context : $"{context}: {result.Failure}");

    private static McpToolCallResult Refusal(string reason) =>
        new($"Exact-file restore refused: {reason}.", IsError: true);

    private static bool TryNormalizeLiteralPath(
        string input,
        out string normalized,
        out IReadOnlyList<string> parts,
        out string? refusal)
    {
        normalized = string.Empty;
        parts = [];
        refusal = null;

        var windowsDrive = input.Length >= 2
            && char.IsAsciiLetter(input[0])
            && input[1] == ':';
        var windowsRooted = input.StartsWith('\\')
            || input.StartsWith('/')
            || input.StartsWith("//", StringComparison.Ordinal);
        if (windowsDrive || windowsRooted)
        {
            refusal = "'path' must be repository-relative, not rooted, UNC, device, or drive-qualified";
            return false;
        }

        if (input.IndexOfAny(['*', '?', '[', ']']) >= 0)
        {
            refusal = "'path' must name one literal file and may not contain glob characters";
            return false;
        }

        var separators = OperatingSystem.IsWindows() ? ['/', '\\'] : new[] { '/' };
        var split = input.Split(separators, StringSplitOptions.None);
        if (split.Any(part => part.Length == 0 || part is "." or ".."))
        {
            refusal = "'path' must contain no empty, '.', or '..' segments";
            return false;
        }

        normalized = string.Join('/', split);
        parts = split;
        return true;
    }

    private static bool IsCanonicalRevision(string value) =>
        value.Length == 40 && value.All(char.IsAsciiHexDigit);

    private static string GitBlobId(byte[] content)
    {
        var header = Encoding.UTF8.GetBytes($"blob {content.Length}\0");
        var all = new byte[header.Length + content.Length];
        Buffer.BlockCopy(header, 0, all, 0, header.Length);
        Buffer.BlockCopy(content, 0, all, header.Length, content.Length);
        return Convert.ToHexString(SHA1.HashData(all)).ToLowerInvariant();
    }

    private sealed record GitResult(
        bool Succeeded,
        byte[] RawStdout,
        byte[] RawStderr,
        string? Failure)
    {
        internal string Stdout => Encoding.UTF8.GetString(RawStdout);

        internal static GitResult Success(byte[] stdout, byte[] stderr) =>
            new(true, stdout, stderr, null);

        internal static GitResult StartFailure(string message) =>
            new(false, [], [], $"git could not start ({message})");

        internal static GitResult TimedOut(byte[] stdout, byte[] stderr) =>
            new(false, stdout, stderr, "git timed out and its contained process tree was terminated");

        internal static GitResult Cancelled(byte[] stdout, byte[] stderr) =>
            new(false, stdout, stderr, "git was cancelled and its contained process tree was terminated");

        internal static GitResult TeardownTimedOut(byte[] stdout, byte[] stderr) =>
            new(false, stdout, stderr, "git stream teardown exceeded its bound after termination");

        internal static GitResult Nonzero(int exitCode, byte[] stdout, byte[] stderr) =>
            new(
                false,
                stdout,
                stderr,
                $"git exited with code {exitCode}: {Encoding.UTF8.GetString(stderr).Trim()}");
    }
}

internal enum ExactFileRestoreAuditState
{
    Prepared,
    Committed,
    RolledBack,
    RecoveryRequired,
}

internal sealed record ExactFileRestoreTestHooks(
    Action? BeforeCompareAndSwap = null,
    Action? BeforeRollbackRestore = null,
    Func<ExactFileRestoreAuditState, Exception?>? AuditFailure = null,
    string GitFileName = "git",
    TimeSpan? GitTimeout = null,
    IReadOnlyDictionary<string, string?>? GitEnvironment = null);

public sealed record ExactFileRestoreAudit(
    int Version,
    string State,
    string ExecutionId,
    string Path,
    string? BeforeBlob,
    string SourceRevision,
    string SourceBlob,
    string RestoredBlob,
    DateTimeOffset RecordedAtUtc,
    string? Quarantine,
    string? Reason);
