using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Baton;
using Baton.Vendors;

namespace Baton.Cli.Mcp;

/// <summary>
/// Baton's one-file restore primitive. The source revision is supplied by the dispatch-captured
/// binding, never by the worker call. Every request is validated as one literal tracked path before
/// the tool writes anything; the only acknowledgement accepted is the explicit dirty-file boolean.
/// </summary>
public sealed class ExactFileRestoreTool(
    string workspaceDirectory,
    string admittedBaseRevision,
    string executionId,
    string roomDirectoryPath) : IMcpTool
{
    public const string AuditFileName = "exact-file-restores.jsonl";
    public const string ToolName = "restore-exact-file";

    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(30);

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
        JsonElement arguments, CancellationToken cancellationToken = default)
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
            || acknowledgement.ValueKind != JsonValueKind.True && acknowledgement.ValueKind != JsonValueKind.False)
        {
            return Refusal("'acknowledgeDirtyFile' is required and must be a boolean");
        }

        var requestedPath = pathElement.GetString()!;
        if (!TryNormalizeLiteralPath(requestedPath, out var relativePath, out var pathRefusal))
        {
            return Refusal(pathRefusal!);
        }

        if (!Path.IsPathFullyQualified(workspaceDirectory) || !Directory.Exists(workspaceDirectory))
        {
            return Refusal("the admitted worker workspace is not an existing absolute directory");
        }

        var baseRevision = admittedBaseRevision.Trim();
        if (!IsCanonicalRevision(baseRevision))
        {
            return Refusal("the dispatch-admitted base revision is not a canonical commit SHA");
        }

        try
        {
            var rootResult = await RunGitAsync(["rev-parse", "--show-toplevel"], cancellationToken)
                .ConfigureAwait(false);
            if (!rootResult.Succeeded)
            {
                return Refusal("the worker workspace is not a readable git repository");
            }

            var repositoryRoot = Path.GetFullPath(rootResult.Stdout.Trim());
            var targetPath = Path.GetFullPath(Path.Combine(
                repositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsInside(repositoryRoot, targetPath) || HasReparsePointBetween(repositoryRoot, targetPath))
            {
                return Refusal("'path' must resolve inside the repository without a symlink or reparse-point escape");
            }

            if (Directory.Exists(targetPath))
            {
                return Refusal("'path' names a directory; exactly one file is required");
            }

            var resolvedBase = await RunGitAsync(["rev-parse", "--verify", $"{baseRevision}^{{commit}}"], cancellationToken)
                .ConfigureAwait(false);
            if (!resolvedBase.Succeeded || !string.Equals(resolvedBase.Stdout.Trim(), baseRevision, StringComparison.Ordinal))
            {
                return Refusal("the requested source revision is not the dispatch-admitted commit");
            }

            var head = await RunGitAsync(["rev-parse", "--verify", "HEAD^{commit}"], cancellationToken)
                .ConfigureAwait(false);
            var ancestry = await RunGitAsync(["merge-base", "--is-ancestor", baseRevision, "HEAD"], cancellationToken)
                .ConfigureAwait(false);
            if (!head.Succeeded || !ancestry.Succeeded)
            {
                return Refusal("the admitted base revision is not an ancestor of the current workspace HEAD");
            }

            var tracked = await RunGitAsync(["ls-files", "--error-unmatch", "--full-name", "--", relativePath], cancellationToken)
                .ConfigureAwait(false);
            if (!tracked.Succeeded || !string.Equals(tracked.Stdout.Trim(), relativePath, StringComparison.Ordinal))
            {
                return Refusal("'path' is not exactly one tracked file in the current repository");
            }

            var sourceBlob = await RunGitAsync(["rev-parse", "--verify", $"{baseRevision}:{relativePath}"], cancellationToken)
                .ConfigureAwait(false);
            if (!sourceBlob.Succeeded || !IsCanonicalRevision(sourceBlob.Stdout.Trim()))
            {
                return Refusal("the named file does not exist as a file in the admitted base commit");
            }

            var status = await RunGitAsync(["status", "--porcelain=v1", "--untracked-files=all", "--", relativePath], cancellationToken)
                .ConfigureAwait(false);
            if (!status.Succeeded)
            {
                return Refusal("could not determine whether the named file is dirty");
            }

            if (status.Stdout.Length > 0 && !acknowledgement.GetBoolean())
            {
                return Refusal("the named file is dirty; set 'acknowledgeDirtyFile' to true to acknowledge this exact file");
            }

            var source = await RunGitAsync(["show", $"{baseRevision}:{relativePath}"], cancellationToken)
                .ConfigureAwait(false);
            if (!source.Succeeded)
            {
                return Refusal("could not read the admitted base blob");
            }

            var beforeBlobResult = await RunGitAsync(["hash-object", "--", relativePath], cancellationToken)
                .ConfigureAwait(false);
            var beforeBlob = beforeBlobResult.Succeeded ? beforeBlobResult.Stdout.Trim() : null;

            if (File.Exists(targetPath) && HasReparsePoint(targetPath))
            {
                return Refusal("the named file is a symlink or reparse point");
            }

            var auditPath = Path.Combine(roomDirectoryPath, ".baton", AuditFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(auditPath)!);
            await using var auditStream = new FileStream(
                auditPath, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
            await using var auditWriter = new StreamWriter(auditStream, new UTF8Encoding(false));

            var parent = Path.GetDirectoryName(targetPath)!;
            Directory.CreateDirectory(parent);
            var temporaryPath = Path.Combine(parent, $".{Path.GetFileName(targetPath)}.baton-restore-{Guid.NewGuid():N}.tmp");
            try
            {
                await File.WriteAllBytesAsync(temporaryPath, source.RawStdout, cancellationToken).ConfigureAwait(false);
                File.Move(temporaryPath, targetPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    FileCleanupBestEffort(temporaryPath);
                }
            }

            var restoredBlob = GitBlobId(source.RawStdout);
            var sourceBlobId = sourceBlob.Stdout.Trim();
            if (!string.Equals(restoredBlob, sourceBlobId, StringComparison.Ordinal))
            {
                return Refusal("the restored bytes did not match the admitted base blob");
            }

            var audit = new ExactFileRestoreAudit(
                executionId, relativePath, beforeBlob, baseRevision, sourceBlobId, restoredBlob,
                DateTimeOffset.UtcNow);
            await auditWriter.WriteLineAsync(JsonSerializer.Serialize(audit)).ConfigureAwait(false);
            await auditWriter.FlushAsync(cancellationToken).ConfigureAwait(false);

            return new McpToolCallResult(
                $"Restored exactly '{relativePath}' from {baseRevision}; before blob {beforeBlob ?? "missing"}, "
                + $"restored blob {restoredBlob}. Audit: {auditPath}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Refusal($"restore refused before completion: {ex.Message}");
        }
    }

    private static McpToolCallResult Refusal(string reason) =>
        new($"Exact-file restore refused: {reason}.", IsError: true);

    private static bool TryNormalizeLiteralPath(string input, out string normalized, out string? refusal)
    {
        normalized = string.Empty;
        refusal = null;
        if (input.IndexOfAny(['*', '?', '[', ']']) >= 0)
        {
            refusal = "'path' must name one literal file and may not contain glob characters";
            return false;
        }

        if (input.Contains(':') || input.StartsWith('/') || input.StartsWith('\\'))
        {
            refusal = "'path' must be repository-relative, not absolute or drive-qualified";
            return false;
        }

        var parts = input.Replace('\\', '/').Split('/');
        if (parts.Any(part => part.Length == 0 || part is "." or ".."))
        {
            refusal = "'path' must contain no empty, '.', or '..' segments";
            return false;
        }

        normalized = string.Join('/', parts);
        return true;
    }

    private static bool IsCanonicalRevision(string value) =>
        value.Length == 40 && value.All(char.IsAsciiHexDigit);

    private static bool IsInside(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathFullyQualified(relative);
    }

    private static bool HasReparsePointBetween(string root, string candidate)
    {
        var current = root;
        var relative = Path.GetRelativePath(root, candidate);
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (Directory.Exists(current) && HasReparsePoint(current))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasReparsePoint(string path) =>
        File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

    private static void FileCleanupBestEffort(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string GitBlobId(byte[] content)
    {
        var header = Encoding.UTF8.GetBytes($"blob {content.Length}\0");
        var all = new byte[header.Length + content.Length];
        Buffer.BlockCopy(header, 0, all, 0, header.Length);
        Buffer.BlockCopy(content, 0, all, header.Length, content.Length);
        return Convert.ToHexString(SHA1.HashData(all)).ToLowerInvariant();
    }

    private async Task<GitResult> RunGitAsync(
        IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(GitTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var startInfo = ChildProcessStartInfo.Create("git", startInfo =>
        {
            startInfo.WorkingDirectory = workspaceDirectory;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.StandardOutputEncoding = Encoding.UTF8;
            startInfo.StandardErrorEncoding = Encoding.UTF8;
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        });

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("git did not start");
        using var stdoutBuffer = new MemoryStream();
        var stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(stdoutBuffer, linked.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(linked.Token);
        await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        var stdout = stdoutBuffer.ToArray();
        return new GitResult(
            process.ExitCode == 0, Encoding.UTF8.GetString(stdout), stderr, stdout);
    }

    private sealed record GitResult(bool Succeeded, string Stdout, string Stderr, byte[] RawStdout);
}

public sealed record ExactFileRestoreAudit(
    string ExecutionId,
    string Path,
    string? BeforeBlob,
    string SourceRevision,
    string SourceBlob,
    string RestoredBlob,
    DateTimeOffset RecordedAtUtc);
