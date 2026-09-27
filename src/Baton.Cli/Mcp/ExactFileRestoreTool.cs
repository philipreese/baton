using System.Security.Cryptography;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Baton;
using Baton.Status;
using Baton.Vendors;

namespace Baton.Cli.Mcp;

/// <summary>
/// Baton's one-file restore primitive. The source revision is supplied by the dispatch-captured
/// binding, never by the worker call. The final write is a handle-anchored compare-and-swap on
/// Windows; platforms without the required identity-anchored mutation primitives fail closed.
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

            var status = await RunGitAsync(
                ["--literal-pathspecs", "status", "--porcelain=v1", "-z", "--untracked-files=all", "--", relativePath],
                cancellationToken).ConfigureAwait(false);
            if (!status.Succeeded)
            {
                return GitRefusal(
                    "could not determine whether the named file is dirty",
                    status);
            }

            var isDirty = status.RawStdout.Length > 0;
            if (isDirty && !acknowledgement.GetBoolean())
            {
                return Refusal(
                    "the named file is dirty; set 'acknowledgeDirtyFile' to true to acknowledge this exact file");
            }

            // Capture after status so a dirty original is opened with the platform's writer-exclusion
            // primitive and retained through the terminal transition. Unix has no equivalent and the
            // transaction refuses an existing dirty leaf before mutation.
            var before = transaction.Capture(isDirty);

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
            var transactionId = Guid.NewGuid().ToString("N");
            ExactFileRecoveryNames? recoveryNames = null;

            var result = await Task.Run(
                () => MutexGuardedFileLock.RunUnderLock(
                    auditPath,
                    "baton-exact-file-restore",
                    AuditLockTimeout,
                    () =>
                    {
                        RefuseUnresolvedTransaction(auditPath, repositoryRoot, relativePath);
                        return transaction.Commit(
                        before,
                        source.RawStdout,
                        names =>
                        {
                            recoveryNames = names;
                            AppendAudit(auditPath, CreateAudit(
                                ExactFileRestoreAuditState.Prepared, transactionId, repositoryRoot,
                                relativePath, before.Blob, baseRevision, sourceBlobId, restoredBlob,
                                names, null));
                        },
                        () => AppendAudit(
                            auditPath,
                            CreateAudit(
                                ExactFileRestoreAuditState.Committed, transactionId, repositoryRoot,
                                relativePath, before.Blob, baseRevision, sourceBlobId, restoredBlob,
                                recoveryNames,
                                null)),
                        () => AppendAudit(
                            auditPath,
                            CreateAudit(
                                ExactFileRestoreAuditState.CleanupCompleted, transactionId, repositoryRoot,
                                relativePath, before.Blob, baseRevision, sourceBlobId, restoredBlob,
                                recoveryNames,
                                null)),
                        () => AppendAudit(
                            auditPath,
                            CreateAudit(
                                ExactFileRestoreAuditState.RolledBack, transactionId, repositoryRoot,
                                relativePath, before.Blob, baseRevision, sourceBlobId, restoredBlob,
                                recoveryNames,
                                null)),
                        (reason, quarantine) => AppendRecoveryAudit(
                            auditPath,
                            CreateAudit(
                                ExactFileRestoreAuditState.RecoveryRequired, transactionId, repositoryRoot,
                                relativePath, before.Blob, baseRevision, sourceBlobId, restoredBlob,
                                recoveryNames is null ? null : recoveryNames with { Quarantine = quarantine },
                                reason)),
                        _testHooks.BeforeCompareAndSwap,
                        _testHooks.BeforeRollbackRestore,
                        _testHooks.BeforeDurableCommit,
                        _testHooks.BeforeDelete);
                    }),
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
        string transactionId,
        string repositoryRoot,
        string path,
        string? beforeBlob,
        string sourceRevision,
        string sourceBlob,
        string restoredBlob,
        ExactFileRecoveryNames? recoveryNames,
        string? reason) =>
        new(
            Version: 3,
            State: state.ToString(),
            ExecutionId: _executionId,
            Path: path,
            BeforeBlob: beforeBlob,
            SourceRevision: sourceRevision,
            SourceBlob: sourceBlob,
            RestoredBlob: restoredBlob,
            RecordedAtUtc: DateTimeOffset.UtcNow,
            Quarantine: recoveryNames?.Quarantine,
            Reason: reason,
            TransactionId: transactionId,
            RepositoryRoot: repositoryRoot,
            Temporary: recoveryNames?.Temporary,
            Uncommitted: recoveryNames?.Uncommitted);

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
        DurableDirectory.Flush(Path.GetDirectoryName(auditPath)!);
    }

    private void AppendRecoveryAudit(string auditPath, ExactFileRestoreAudit audit)
    {
        try
        {
            AppendAudit(auditPath, audit);
        }
        catch (Exception appendFailure) when (appendFailure is IOException
            or UnauthorizedAccessException or InvalidOperationException)
        {
            var markerPath = $"{auditPath}.{audit.TransactionId}.recovery-required";
            using var stream = new FileStream(
                markerPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096,
                FileOptions.WriteThrough);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true);
            writer.Write(JsonSerializer.Serialize(audit with
            {
                Reason = $"{audit.Reason}; journal append failed: {appendFailure.Message}",
            }));
            writer.Flush();
            stream.Flush(flushToDisk: true);
            DurableDirectory.Flush(Path.GetDirectoryName(auditPath)!);
        }
    }

    private static void RefuseUnresolvedTransaction(
        string auditPath,
        string repositoryRoot,
        string relativePath)
    {
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var states = new Dictionary<string, ExactFileRestoreAudit>(StringComparer.Ordinal);

        if (File.Exists(auditPath))
        {
            foreach (var line in File.ReadLines(auditPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var audit = ReadAudit(line, "journal");
                if (!string.Equals(audit.Path, relativePath, StringComparison.Ordinal)
                    || !comparer.Equals(Path.GetFullPath(audit.RepositoryRoot!), repositoryRoot))
                {
                    continue;
                }

                states[audit.TransactionId!] = audit;
            }
        }

        var directory = Path.GetDirectoryName(auditPath)!;
        var markerPattern = $"{Path.GetFileName(auditPath)}.*.recovery-required";
        foreach (var marker in Directory.EnumerateFiles(directory, markerPattern))
        {
            var audit = ReadAudit(File.ReadAllText(marker), "recovery marker");
            if (string.Equals(audit.Path, relativePath, StringComparison.Ordinal)
                && comparer.Equals(Path.GetFullPath(audit.RepositoryRoot!), repositoryRoot))
            {
                throw new IOException(
                    $"unresolved exact-file restore transaction '{audit.TransactionId}' "
                    + "requires recovery before this path can be restored again");
            }
        }

        var unresolved = states.Values.FirstOrDefault(audit =>
            audit.State is nameof(ExactFileRestoreAuditState.Prepared)
                or nameof(ExactFileRestoreAuditState.Committed)
                or nameof(ExactFileRestoreAuditState.RecoveryRequired));
        if (unresolved is not null)
        {
            throw new IOException(
                $"unresolved exact-file restore transaction '{unresolved.TransactionId}' "
                + $"is {unresolved.State} and requires recovery before this path can be restored again");
        }
    }

    private static ExactFileRestoreAudit ReadAudit(string json, string source)
    {
        try
        {
            var audit = JsonSerializer.Deserialize<ExactFileRestoreAudit>(json)
                ?? throw new IOException($"the exact-file restore {source} contains a null record");
            ValidateAudit(audit, source);
            return audit;
        }
        catch (JsonException ex)
        {
            throw new IOException($"the exact-file restore {source} is malformed", ex);
        }
    }

    private static void ValidateAudit(ExactFileRestoreAudit audit, string source)
    {
        static bool Missing(string? value) => string.IsNullOrWhiteSpace(value);
        void Malformed(string field) =>
            throw new IOException($"the exact-file restore {source} has malformed required field '{field}'");

        if (audit.Version != 3) Malformed(nameof(audit.Version));
        if (Missing(audit.State)
            || !Enum.TryParse<ExactFileRestoreAuditState>(audit.State, ignoreCase: false, out var state)
            || !Enum.IsDefined(state)) Malformed(nameof(audit.State));
        if (Missing(audit.ExecutionId)) Malformed(nameof(audit.ExecutionId));
        if (Missing(audit.Path)
            || !TryNormalizeLiteralPath(audit.Path, out var normalizedPath, out _, out _)
            || !string.Equals(audit.Path, normalizedPath, StringComparison.Ordinal)) Malformed(nameof(audit.Path));
        if (Missing(audit.TransactionId)) Malformed(nameof(audit.TransactionId));
        if (audit.RepositoryRoot is not { } repositoryRoot || !IsValidAbsolutePath(repositoryRoot))
            Malformed(nameof(audit.RepositoryRoot));
        if (!IsCanonicalRevision(audit.SourceRevision)) Malformed(nameof(audit.SourceRevision));
        if (!IsCanonicalRevision(audit.SourceBlob)) Malformed(nameof(audit.SourceBlob));
        if (!IsCanonicalRevision(audit.RestoredBlob)) Malformed(nameof(audit.RestoredBlob));
        if (audit.BeforeBlob is not null && !IsCanonicalRevision(audit.BeforeBlob))
            Malformed(nameof(audit.BeforeBlob));
        if (audit.RecordedAtUtc == default) Malformed(nameof(audit.RecordedAtUtc));
        if (!IsValidRecoveryLeaf(audit.Temporary)) Malformed(nameof(audit.Temporary));
        if (!IsValidRecoveryLeaf(audit.Uncommitted)) Malformed(nameof(audit.Uncommitted));
        if (audit.BeforeBlob is not null && !IsValidRecoveryLeaf(audit.Quarantine))
            Malformed(nameof(audit.Quarantine));
        if (state == ExactFileRestoreAuditState.RecoveryRequired && Missing(audit.Reason))
            Malformed(nameof(audit.Reason));
    }

    private static bool IsValidAbsolutePath(string path)
    {
        try
        {
            return Path.IsPathFullyQualified(path) && string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), path, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsValidRecoveryLeaf(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value is not "." and not ".."
        && value.IndexOf('/') < 0
        && (!OperatingSystem.IsWindows() || value.IndexOf('\\') < 0);

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
                startInfo.Environment["GIT_NO_LAZY_FETCH"] = "1";
                startInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
                if (_testHooks.GitEnvironment is not null)
                {
                    foreach (var (name, value) in _testHooks.GitEnvironment)
                    {
                        startInfo.Environment[name] = value;
                    }
                }
                // These queries need repository metadata, but never repository-configured programs.
                // In particular status and ls-files otherwise honor core.fsmonitor. The empty hook
                // root and disabled lazy fetch keep every query local even in hostile repository
                // configuration; the explicit -c values outrank local/global/system config.
                startInfo.ArgumentList.Add("-c");
                startInfo.ArgumentList.Add("core.fsmonitor=false");
                startInfo.ArgumentList.Add("-c");
                startInfo.ArgumentList.Add("core.untrackedCache=false");
                startInfo.ArgumentList.Add("-c");
                startInfo.ArgumentList.Add($"core.hooksPath={Path.Combine(_roomDirectoryPath, ".baton", "disabled-git-hooks")}");
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

        var windowsDrive = OperatingSystem.IsWindows()
            && input.Length >= 2
            && char.IsAsciiLetter(input[0])
            && input[1] == ':';
        var windowsRooted = OperatingSystem.IsWindows()
            ? input.StartsWith('\\') || input.StartsWith('/')
            : input.StartsWith('/');
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

    private static bool IsCanonicalRevision(string? value) =>
        value is { Length: 40 } && value.All(char.IsAsciiHexDigit);

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

    private static class DurableDirectory
    {
        internal static void Flush(string path)
        {
            using var handle = OperatingSystem.IsWindows()
                ? OpenWindows(path)
                : OpenUnix(path);
            if (OperatingSystem.IsWindows())
            {
                var status = NtFlushBuffersFile(handle, out _);
                if (status < 0)
                {
                    throw new IOException(
                        $"flush audit directory failed with NTSTATUS 0x{status:x8}");
                }
            }
            else if (fsync(checked((int)handle.DangerousGetHandle())) != 0)
            {
                throw Error("flush audit directory");
            }
        }

        private static SafeFileHandle OpenWindows(string path)
        {
            var handle = CreateFileW(path, 0xC0000000, 0x7, 0, 3, 0x02000000, 0);
            return handle.IsInvalid ? throw Error("open audit directory") : handle;
        }

        private static SafeFileHandle OpenUnix(string path)
        {
            var directoryFlag = OperatingSystem.IsMacOS() ? 0x100000 : 0x10000;
            var descriptor = open(path, directoryFlag, 0);
            return descriptor < 0
                ? throw Error("open audit directory")
                : new SafeFileHandle(descriptor, ownsHandle: true);
        }

        private static IOException Error(string operation) =>
            new($"{operation} failed: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFileW(
            string fileName, uint desiredAccess, uint shareMode, nint securityAttributes,
            uint creationDisposition, uint flagsAndAttributes, nint templateFile);

        [StructLayout(LayoutKind.Sequential)]
        private struct IoStatusBlock
        {
            internal nint Status;
            internal nuint Information;
        }

        [DllImport("ntdll.dll")]
        private static extern int NtFlushBuffersFile(
            SafeFileHandle fileHandle,
            out IoStatusBlock ioStatusBlock);

        [DllImport("libc", SetLastError = true)]
        private static extern int open(string pathname, int flags, uint mode);

        [DllImport("libc", SetLastError = true)]
        private static extern int fsync(int fileDescriptor);
    }
}

internal enum ExactFileRestoreAuditState
{
    Prepared,
    Committed,
    CleanupCompleted,
    RolledBack,
    RecoveryRequired,
}

internal sealed record ExactFileRestoreTestHooks(
    Action? BeforeCompareAndSwap = null,
    Action? BeforeRollbackRestore = null,
    Func<ExactFileRestoreAuditState, Exception?>? AuditFailure = null,
    string GitFileName = "git",
    TimeSpan? GitTimeout = null,
    IReadOnlyDictionary<string, string?>? GitEnvironment = null,
    Action? BeforeDurableCommit = null,
    Action? BeforeDelete = null);

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
    string? Reason,
    string? TransactionId = null,
    string? RepositoryRoot = null,
    string? Temporary = null,
    string? Uncommitted = null);
