using System.Diagnostics;
using System.Text.Json;
using Baton.Cli.Mcp;
using Baton.Tests.Shared;

namespace Baton.Cli.Tests.Mcp;

public sealed class ExactFileRestoreToolTests
{
    [Fact]
    public async Task Restores_one_tracked_file_preserves_unrelated_edits_and_journals_blobs()
    {
        var root = TempDir();
        try
        {
            Directory.CreateDirectory(root);
            await GitAsync(root, "init");
            await GitAsync(root, "config", "user.name", "Test");
            await GitAsync(root, "config", "user.email", "test@test.com");
            await File.WriteAllTextAsync(Path.Combine(root, "target.txt"), "base\n", Ct);
            await File.WriteAllTextAsync(Path.Combine(root, "unrelated.txt"), "unrelated-base\n", Ct);
            await GitAsync(root, "add", "-A");
            await GitAsync(root, "commit", "-m", "base");
            var baseSha = await GitAsync(root, "rev-parse", "HEAD");

            await File.WriteAllTextAsync(Path.Combine(root, "target.txt"), "damaged\n", Ct);
            await File.WriteAllTextAsync(Path.Combine(root, "unrelated.txt"), "unrelated-edit\n", Ct);
            var room = Path.Combine(root, "room");
            var result = await new ExactFileRestoreTool(root, baseSha, "execution-1", room)
                .CallAsync(Args("target.txt", acknowledgeDirtyFile: true), Ct);

            Assert.False(result.IsError);
            Assert.Equal("base\n", await File.ReadAllTextAsync(Path.Combine(root, "target.txt"), Ct));
            Assert.Equal("unrelated-edit\n", await File.ReadAllTextAsync(Path.Combine(root, "unrelated.txt"), Ct));

            var auditPath = Path.Combine(room, ".baton", ExactFileRestoreTool.AuditFileName);
            var audit = await File.ReadAllTextAsync(auditPath, Ct);
            Assert.Contains("execution-1", audit);
            Assert.Contains("target.txt", audit);
            Assert.Contains(baseSha, audit);
            Assert.Contains("BeforeBlob", audit);
            Assert.Contains("RestoredBlob", audit);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Theory]
    [InlineData("*.txt")]
    [InlineData("target.txt/child")]
    [InlineData("../target.txt")]
    [InlineData("C:\\outside.txt")]
    public async Task Rejects_non_literal_or_outside_paths_without_writing(string path)
    {
        var root = await CreateRepositoryAsync();
        try
        {
            var before = await File.ReadAllTextAsync(Path.Combine(root, "target.txt"), Ct);
            var result = await (await NewToolAsync(root)).CallAsync(Args(path, acknowledgeDirtyFile: true), Ct);

            Assert.True(result.IsError);
            Assert.Contains("refused", result.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(before, await File.ReadAllTextAsync(Path.Combine(root, "target.txt"), Ct));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public async Task Rejects_untracked_file_and_dirty_file_without_acknowledgement()
    {
        var root = await CreateRepositoryAsync();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "untracked.txt"), "untracked\n", Ct);
            var untracked = await (await NewToolAsync(root)).CallAsync(Args("untracked.txt", acknowledgeDirtyFile: true), Ct);
            Assert.True(untracked.IsError);

            await File.WriteAllTextAsync(Path.Combine(root, "target.txt"), "damaged\n", Ct);
            var dirty = await (await NewToolAsync(root)).CallAsync(Args("target.txt", acknowledgeDirtyFile: false), Ct);
            Assert.True(dirty.IsError);
            Assert.Contains("dirty", dirty.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("damaged\n", await File.ReadAllTextAsync(Path.Combine(root, "target.txt"), Ct));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public async Task Rejects_a_base_that_is_not_a_canonical_admitted_commit()
    {
        var root = await CreateRepositoryAsync();
        try
        {
            var result = await new ExactFileRestoreTool(
                    root, new string('0', 40), "execution-1", Path.Combine(root, "room"))
                .CallAsync(Args("target.txt", acknowledgeDirtyFile: true), Ct);

            Assert.True(result.IsError);
            Assert.Contains("admitted", result.Text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    private static async Task<string> CreateRepositoryAsync()
    {
        var root = TempDir();
        Directory.CreateDirectory(root);
        await GitAsync(root, "init");
        await GitAsync(root, "config", "user.name", "Test");
        await GitAsync(root, "config", "user.email", "test@test.com");
        await File.WriteAllTextAsync(Path.Combine(root, "target.txt"), "base\n", Ct);
        await GitAsync(root, "add", "-A");
        await GitAsync(root, "commit", "-m", "base");
        return root;
    }

    private static async Task<ExactFileRestoreTool> NewToolAsync(string root) =>
        new(root, await GitAsync(root, "rev-parse", "HEAD"), "execution-1", Path.Combine(root, "room"));

    private static JsonElement Args(string path, bool acknowledgeDirtyFile)
    {
        using var document = JsonDocument.Parse(
            $"{{\"path\":{JsonSerializer.Serialize(path)},\"acknowledgeDirtyFile\":{acknowledgeDirtyFile.ToString().ToLowerInvariant()}}}");
        return document.RootElement.Clone();
    }

    private static async Task<string> GitAsync(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync(Ct);
        var stderr = process.StandardError.ReadToEndAsync(Ct);
        await BoundedProcessWait.WaitForExitAsync(process, TimeSpan.FromSeconds(60), Ct);
        var output = await stdout;
        var error = await stderr;
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}");
        return output.Trim();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string TempDir() => Path.Combine(Path.GetTempPath(), $"baton-exact-restore-{Guid.NewGuid():N}");
}
