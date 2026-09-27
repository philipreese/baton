using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Baton.Cli.Mcp;
using Baton.CrashTestHost;
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

            Assert.False(result.IsError, result.Text);
            Assert.Equal("base\n", await File.ReadAllTextAsync(Path.Combine(root, "target.txt"), Ct));
            Assert.Equal("unrelated-edit\n", await File.ReadAllTextAsync(Path.Combine(root, "unrelated.txt"), Ct));

            var auditPath = Path.Combine(room, ".baton", ExactFileRestoreTool.AuditFileName);
            var audit = await File.ReadAllTextAsync(auditPath, Ct);
            Assert.Contains("execution-1", audit);
            Assert.Contains("target.txt", audit);
            Assert.Contains(baseSha, audit);
            Assert.Contains("BeforeBlob", audit);
            Assert.Contains("RestoredBlob", audit);
            var records = audit.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonSerializer.Deserialize<ExactFileRestoreAudit>(line)!)
                .ToArray();
            Assert.Equal(
                ["Prepared", "Committed", "CleanupCompleted"],
                records.Select(record => record.State).ToArray());
            Assert.All(records, record => Assert.Equal(3, record.Version));
            Assert.All(records, record => Assert.False(string.IsNullOrWhiteSpace(record.TransactionId)));
            Assert.False(string.IsNullOrWhiteSpace(records[0].Temporary));
            Assert.False(string.IsNullOrWhiteSpace(records[0].Uncommitted));
            Assert.False(string.IsNullOrWhiteSpace(records[0].Quarantine));
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

    [Fact]
    public async Task Rejects_a_canonical_base_that_is_not_an_ancestor()
    {
        var root = await CreateRepositoryAsync();
        try
        {
            var main = await GitAsync(root, "rev-parse", "HEAD");
            await GitAsync(root, "checkout", "--orphan", "unrelated");
            await GitAsync(root, "rm", "-rf", ".");
            await File.WriteAllTextAsync(Path.Combine(root, "other.txt"), "other\n", Ct);
            await GitAsync(root, "add", "-A");
            await GitAsync(root, "commit", "-m", "unrelated");
            var unrelated = await GitAsync(root, "rev-parse", "HEAD");
            await GitAsync(root, "checkout", main);

            var result = await new ExactFileRestoreTool(
                    root, unrelated, "execution-1", Path.Combine(root, "room"))
                .CallAsync(Args("target.txt", acknowledgeDirtyFile: true), Ct);

            Assert.True(result.IsError);
            Assert.Contains("ancestor", result.Text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rejects_existing_and_dangling_leaf_links(bool dangling)
    {
        var root = await CreateRepositoryAsync();
        try
        {
            var target = Path.Combine(root, "target.txt");
            var referent = Path.Combine(root, dangling ? "missing.txt" : "referent.txt");
            if (!dangling)
            {
                await File.WriteAllTextAsync(referent, "referent\n", Ct);
            }

            FileCleanup.EnsureDeleted(target);
            try
            {
                File.CreateSymbolicLink(target, referent);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Assert.Skip($"this host cannot create file symbolic links: {ex.Message}");
            }

            var result = await (await NewToolAsync(root))
                .CallAsync(Args("target.txt", acknowledgeDirtyFile: true), Ct);

            Assert.True(result.IsError);
            Assert.Contains(
                OperatingSystem.IsWindows() ? "reparse" : "symbolic",
                result.Text,
                StringComparison.OrdinalIgnoreCase);
            if (!dangling)
            {
                Assert.Equal("referent\n", await File.ReadAllTextAsync(referent, Ct));
            }
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public async Task Rejects_an_intermediate_directory_link()
    {
        var root = await CreateRepositoryAsync("nested/target.txt");
        try
        {
            var nested = Path.Combine(root, "nested");
            var real = Path.Combine(root, "real-nested");
            Directory.Move(nested, real);
            try
            {
                Directory.CreateSymbolicLink(nested, real);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Assert.Skip($"this host cannot create directory links: {ex.Message}");
            }

            var result = await (await NewToolAsync(root))
                .CallAsync(Args("nested/target.txt", acknowledgeDirtyFile: true), Ct);

            Assert.True(result.IsError);
            Assert.Equal("base\n", await File.ReadAllTextAsync(Path.Combine(real, "target.txt"), Ct));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public async Task Rejects_a_Windows_junction_ancestor()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "junctions are Windows-only");
        var root = await CreateRepositoryAsync("nested/target.txt");
        try
        {
            var nested = Path.Combine(root, "nested");
            var real = Path.Combine(root, "real-nested");
            Directory.Move(nested, real);
            var startInfo = new ProcessStartInfo("cmd", $"/c mklink /J \"{nested}\" \"{real}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                Assert.Skip("this host could not start cmd.exe to create a junction");
            }

            await process.WaitForExitAsync(Ct);
            if (process.ExitCode != 0)
            {
                Assert.Skip("this host refused to create a junction");
            }

            var result = await (await NewToolAsync(root))
                .CallAsync(Args("nested/target.txt", acknowledgeDirtyFile: true), Ct);
            Assert.True(result.IsError);
            Assert.Contains("reparse", result.Text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public async Task Refuses_when_an_ancestor_is_swapped_at_the_commit_checkpoint()
    {
        var root = await CreateRepositoryAsync("nested/target.txt");
        try
        {
            var nested = Path.Combine(root, "nested");
            var moved = Path.Combine(root, "moved-nested");
            var tool = await NewToolAsync(
                root,
                new ExactFileRestoreTestHooks(BeforeCompareAndSwap: () =>
                {
                    Directory.Move(nested, moved);
                    Directory.CreateDirectory(nested);
                    File.WriteAllText(Path.Combine(nested, "target.txt"), "decoy\n");
                }));

            var result = await tool.CallAsync(
                Args("nested/target.txt", acknowledgeDirtyFile: true), Ct);

            Assert.True(result.IsError);
            if (OperatingSystem.IsWindows())
            {
                Assert.True(Directory.Exists(nested));
                Assert.Equal("base\n", await File.ReadAllTextAsync(Path.Combine(nested, "target.txt"), Ct));
            }
            else
            {
                Assert.Contains("ancestor", result.Text, StringComparison.OrdinalIgnoreCase);
                Assert.Equal("decoy\n", await File.ReadAllTextAsync(Path.Combine(nested, "target.txt"), Ct));
                Assert.Equal("base\n", await File.ReadAllTextAsync(Path.Combine(moved, "target.txt"), Ct));
            }
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Final_CAS_rejects_in_place_and_atomic_changes_with_either_acknowledgement(
        bool atomicReplacement,
        bool acknowledgement)
    {
        var root = await CreateRepositoryAsync();
        try
        {
            var target = Path.Combine(root, "target.txt");
            var tool = await NewToolAsync(
                root,
                new ExactFileRestoreTestHooks(BeforeCompareAndSwap: () =>
                {
                    if (atomicReplacement)
                    {
                        var replacement = Path.Combine(root, "replacement.txt");
                        File.WriteAllText(replacement, "concurrent\n");
                        File.Move(replacement, target, overwrite: true);
                    }
                    else
                    {
                        File.WriteAllText(target, "concurrent\n");
                    }
                }));

            var result = await tool.CallAsync(Args("target.txt", acknowledgement), Ct);

            Assert.True(result.IsError);
            if (OperatingSystem.IsWindows())
            {
                Assert.Equal("base\n", await File.ReadAllTextAsync(target, Ct));
            }
            else
            {
                Assert.Contains("changed", result.Text, StringComparison.OrdinalIgnoreCase);
                Assert.Equal("concurrent\n", await File.ReadAllTextAsync(target, Ct));
            }
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public async Task Final_CAS_never_overwrites_a_missing_to_present_target()
    {
        var root = await CreateRepositoryAsync();
        try
        {
            var target = Path.Combine(root, "target.txt");
            FileCleanup.EnsureDeleted(target);
            var tool = await NewToolAsync(
                root,
                new ExactFileRestoreTestHooks(
                    BeforeCompareAndSwap: () => File.WriteAllText(target, "concurrent\n")));

            var result = await tool.CallAsync(Args("target.txt", acknowledgeDirtyFile: true), Ct);

            Assert.True(result.IsError);
            Assert.Equal("concurrent\n", await File.ReadAllTextAsync(target, Ct));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public async Task Prepared_audit_failure_leaves_the_target_untouched()
    {
        var root = await CreateRepositoryAsync();
        try
        {
            var target = Path.Combine(root, "target.txt");
            await File.WriteAllTextAsync(target, "damaged\n", Ct);
            var tool = await NewToolAsync(
                root,
                new ExactFileRestoreTestHooks(
                    AuditFailure: state => state == ExactFileRestoreAuditState.Prepared
                        ? new IOException("prepared fault")
                        : null));

            var result = await tool.CallAsync(Args("target.txt", acknowledgeDirtyFile: true), Ct);

            Assert.True(result.IsError);
            Assert.Equal("damaged\n", await File.ReadAllTextAsync(target, Ct));
            Assert.False(File.Exists(AuditPath(root)));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public async Task Committed_audit_failure_rolls_back_and_records_the_rollback()
    {
        var root = await CreateRepositoryAsync();
        try
        {
            var target = Path.Combine(root, "target.txt");
            await File.WriteAllTextAsync(target, "damaged\n", Ct);
            var tool = await NewToolAsync(
                root,
                new ExactFileRestoreTestHooks(
                    AuditFailure: state => state == ExactFileRestoreAuditState.Committed
                        ? new IOException("committed fault")
                        : null));

            var result = await tool.CallAsync(Args("target.txt", acknowledgeDirtyFile: true), Ct);

            Assert.True(result.IsError);
            Assert.Equal("damaged\n", await File.ReadAllTextAsync(target, Ct));
            var audit = await File.ReadAllTextAsync(AuditPath(root), Ct);
            Assert.Contains("\"State\":\"Prepared\"", audit);
            Assert.Contains("\"State\":\"RolledBack\"", audit);
            Assert.DoesNotContain("\"State\":\"Committed\"", audit);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public async Task Post_commit_cleanup_failure_preserves_original_and_blocks_restart()
    {
        var root = await CreateRepositoryAsync();
        try
        {
            var target = Path.Combine(root, "target.txt");
            await File.WriteAllTextAsync(target, "damaged\n", Ct);
            var tool = await NewToolAsync(
                root,
                new ExactFileRestoreTestHooks(
                    BeforeDelete: () => throw new IOException("cleanup fault")));

            var result = await tool.CallAsync(Args("target.txt", acknowledgeDirtyFile: true), Ct);

            Assert.True(result.IsError);
            Assert.Equal("base\n", await File.ReadAllTextAsync(target, Ct));
            var quarantine = Assert.Single(Directory.GetFiles(root, ".target.txt.baton-quarantine-*"));
            Assert.Equal("damaged\n", await File.ReadAllTextAsync(quarantine, Ct));
            var audit = await File.ReadAllTextAsync(AuditPath(root), Ct);
            Assert.Contains("\"State\":\"Committed\"", audit);
            Assert.Contains("\"State\":\"RecoveryRequired\"", audit);

            var restart = await (await NewToolAsync(root))
                .CallAsync(Args("target.txt", acknowledgeDirtyFile: true), Ct);
            Assert.True(restart.IsError);
            Assert.Contains("unresolved", restart.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("base\n", await File.ReadAllTextAsync(target, Ct));
            Assert.Equal("damaged\n", await File.ReadAllTextAsync(quarantine, Ct));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public async Task Failed_rollback_preserves_quarantine_and_records_RecoveryRequired()
    {
        var root = await CreateRepositoryAsync();
        try
        {
            var target = Path.Combine(root, "target.txt");
            await File.WriteAllTextAsync(target, "damaged\n", Ct);
            var tool = await NewToolAsync(
                root,
                new ExactFileRestoreTestHooks(
                    BeforeRollbackRestore: () => File.WriteAllText(target, "blocker\n"),
                    AuditFailure: state => state == ExactFileRestoreAuditState.Committed
                        ? new IOException("committed fault")
                        : null));

            var result = await tool.CallAsync(Args("target.txt", acknowledgeDirtyFile: true), Ct);

            Assert.True(result.IsError);
            Assert.Contains("recovery", result.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("blocker\n", await File.ReadAllTextAsync(target, Ct));
            var quarantine = Directory.GetFiles(root, ".target.txt.baton-quarantine-*");
            Assert.Single(quarantine);
            Assert.Equal("damaged\n", await File.ReadAllTextAsync(quarantine[0], Ct));
            Assert.Contains(
                "\"State\":\"RecoveryRequired\"",
                await File.ReadAllTextAsync(AuditPath(root), Ct));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public async Task RecoveryRequired_append_failure_writes_fallback_and_blocks_restart()
    {
        var root = await CreateRepositoryAsync();
        try
        {
            var target = Path.Combine(root, "target.txt");
            await File.WriteAllTextAsync(target, "damaged\n", Ct);
            var tool = await NewToolAsync(
                root,
                new ExactFileRestoreTestHooks(
                    BeforeRollbackRestore: () => File.WriteAllText(target, "concurrent\n"),
                    AuditFailure: state => state is ExactFileRestoreAuditState.Committed
                        or ExactFileRestoreAuditState.RecoveryRequired
                            ? new IOException($"{state} fault")
                            : null));

            var result = await tool.CallAsync(Args("target.txt", acknowledgeDirtyFile: true), Ct);

            Assert.True(result.IsError);
            Assert.Equal("concurrent\n", await File.ReadAllTextAsync(target, Ct));
            Assert.Single(Directory.GetFiles(
                Path.GetDirectoryName(AuditPath(root))!,
                ExactFileRestoreTool.AuditFileName + ".*.recovery-required"));

            var restart = await (await NewToolAsync(root))
                .CallAsync(Args("target.txt", acknowledgeDirtyFile: true), Ct);
            Assert.True(restart.IsError);
            Assert.Contains("unresolved", restart.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("concurrent\n", await File.ReadAllTextAsync(target, Ct));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public async Task Prepared_journal_record_blocks_a_new_restore_for_the_same_root_and_path()
    {
        var root = await CreateRepositoryAsync();
        try
        {
            var target = Path.Combine(root, "target.txt");
            await File.WriteAllTextAsync(target, "damaged\n", Ct);
            var auditPath = AuditPath(root);
            Directory.CreateDirectory(Path.GetDirectoryName(auditPath)!);
            var record = new ExactFileRestoreAudit(
                3, nameof(ExactFileRestoreAuditState.Prepared), "old-execution", "target.txt",
                null, new string('a', 40), new string('b', 40), new string('b', 40),
                DateTimeOffset.UtcNow, ".target.txt.baton-quarantine-old", null,
                "old-transaction", root, ".target.txt.baton-restore-old.tmp",
                ".target.txt.baton-uncommitted-old");
            await File.WriteAllTextAsync(auditPath, JsonSerializer.Serialize(record) + Environment.NewLine, Ct);

            var result = await (await NewToolAsync(root))
                .CallAsync(Args("target.txt", acknowledgeDirtyFile: true), Ct);

            Assert.True(result.IsError);
            Assert.Contains("unresolved", result.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("damaged\n", await File.ReadAllTextAsync(target, Ct));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public async Task Committed_without_cleanup_completion_blocks_restart()
    {
        var root = await CreateRepositoryAsync();
        try
        {
            var target = Path.Combine(root, "target.txt");
            await File.WriteAllTextAsync(target, "damaged\n", Ct);
            var auditPath = AuditPath(root);
            Directory.CreateDirectory(Path.GetDirectoryName(auditPath)!);
            var record = ValidAudit(root) with { State = nameof(ExactFileRestoreAuditState.Committed) };
            await File.WriteAllTextAsync(
                auditPath, JsonSerializer.Serialize(record) + Environment.NewLine, Ct);

            var result = await (await NewToolAsync(root))
                .CallAsync(Args("target.txt", acknowledgeDirtyFile: true), Ct);

            Assert.True(result.IsError);
            Assert.Contains("Committed", result.Text, StringComparison.Ordinal);
            Assert.Contains("unresolved", result.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("damaged\n", await File.ReadAllTextAsync(target, Ct));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Theory]
    [InlineData("Version")]
    [InlineData("State")]
    [InlineData("ExecutionId")]
    [InlineData("Path")]
    [InlineData("SourceRevision")]
    [InlineData("SourceBlob")]
    [InlineData("RestoredBlob")]
    [InlineData("RecordedAtUtc")]
    [InlineData("Quarantine")]
    [InlineData("TransactionId")]
    [InlineData("RepositoryRoot")]
    [InlineData("Temporary")]
    [InlineData("Uncommitted")]
    public async Task Missing_required_journal_field_fails_closed_before_path_filtering(string field)
    {
        var root = await CreateRepositoryAsync();
        try
        {
            var target = Path.Combine(root, "target.txt");
            await File.WriteAllTextAsync(target, "damaged\n", Ct);
            var record = JsonSerializer.SerializeToNode(ValidAudit(root) with { Path = "other.txt" })!.AsObject();
            Assert.True(record.Remove(field));
            var auditPath = AuditPath(root);
            Directory.CreateDirectory(Path.GetDirectoryName(auditPath)!);
            await File.WriteAllTextAsync(auditPath, record.ToJsonString() + Environment.NewLine, Ct);

            var result = await (await NewToolAsync(root))
                .CallAsync(Args("target.txt", acknowledgeDirtyFile: true), Ct);

            Assert.True(result.IsError);
            Assert.True(result.Text.Contains(field, StringComparison.Ordinal),
                $"Expected required field '{field}' in refusal. Actual refusal: {result.Text}");
            Assert.Equal("damaged\n", await File.ReadAllTextAsync(target, Ct));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Theory]
    [InlineData("Path", "../outside.txt")]
    [InlineData("RepositoryRoot", "relative-root")]
    public async Task Malformed_journal_path_or_root_fails_closed(string field, string value)
    {
        var root = await CreateRepositoryAsync();
        try
        {
            var target = Path.Combine(root, "target.txt");
            await File.WriteAllTextAsync(target, "damaged\n", Ct);
            var record = JsonSerializer.SerializeToNode(ValidAudit(root))!.AsObject();
            record[field] = value;
            var auditPath = AuditPath(root);
            Directory.CreateDirectory(Path.GetDirectoryName(auditPath)!);
            await File.WriteAllTextAsync(auditPath, record.ToJsonString() + Environment.NewLine, Ct);

            var result = await (await NewToolAsync(root))
                .CallAsync(Args("target.txt", acknowledgeDirtyFile: true), Ct);

            Assert.True(result.IsError);
            Assert.Contains(field, result.Text, StringComparison.Ordinal);
            Assert.Equal("damaged\n", await File.ReadAllTextAsync(target, Ct));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public async Task Windows_retained_replacement_handle_excludes_post_hash_writer()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows share-mode exclusion is required");
        var root = await CreateRepositoryAsync();
        try
        {
            var target = Path.Combine(root, "target.txt");
            await File.WriteAllTextAsync(target, "damaged\n", Ct);
            var writerWasDenied = false;
            var tool = await NewToolAsync(
                root,
                new ExactFileRestoreTestHooks(BeforeDurableCommit: () =>
                {
                    try
                    {
                        File.WriteAllText(target, "post-hash\n");
                    }
                    catch (IOException)
                    {
                        writerWasDenied = true;
                    }
                }));

            var result = await tool.CallAsync(Args("target.txt", acknowledgeDirtyFile: true), Ct);

            Assert.False(result.IsError, result.Text);
            Assert.True(writerWasDenied);
            Assert.Equal("base\n", await File.ReadAllTextAsync(target, Ct));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unix_refuses_existing_and_missing_target_transitions_before_mutation(bool targetExists)
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "Unix transaction semantics are required");
        var root = await CreateRepositoryAsync();
        try
        {
            var target = Path.Combine(root, "target.txt");
            if (!targetExists)
            {
                FileCleanup.EnsureDeleted(target);
            }
            var compareAndSwapReached = false;
            var tool = await NewToolAsync(
                root,
                new ExactFileRestoreTestHooks(
                    BeforeCompareAndSwap: () => compareAndSwapReached = true));

            var result = await tool.CallAsync(Args("target.txt", acknowledgeDirtyFile: true), Ct);

            Assert.True(result.IsError);
            Assert.Contains("identity-anchored", result.Text, StringComparison.OrdinalIgnoreCase);
            Assert.False(compareAndSwapReached);
            Assert.Equal(targetExists, File.Exists(target));
            Assert.Empty(Directory.GetFiles(root, ".target.txt.baton-*"));
            Assert.False(File.Exists(AuditPath(root)));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public async Task Unix_accepts_literal_colon_and_backslash_names_but_refuses_mutation()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "Unix filename semantics are required");
        var root = TempDir();
        try
        {
            Directory.CreateDirectory(root);
            await GitAsync(root, "init");
            await GitAsync(root, "config", "user.name", "Test");
            await GitAsync(root, "config", "user.email", "test@test.com");
            var names = new[] { "C:relative.txt", "\\rooted.txt", "colon:name.txt", "back\\slash.txt" };
            foreach (var name in names)
            {
                await File.WriteAllTextAsync(Path.Combine(root, name), $"base-{name}\n", Ct);
            }

            await GitAsync(root, "add", "-A");
            await GitAsync(root, "commit", "-m", "base");
            foreach (var name in names)
            {
                FileCleanup.EnsureDeleted(Path.Combine(root, name));
                var result = await (await NewToolAsync(root))
                    .CallAsync(Args(name, acknowledgeDirtyFile: true), Ct);
                Assert.True(result.IsError);
                Assert.Contains("identity-anchored", result.Text, StringComparison.OrdinalIgnoreCase);
                Assert.False(File.Exists(Path.Combine(root, name)));
            }
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Theory]
    [InlineData("C:relative.txt")]
    [InlineData("C:/rooted.txt")]
    [InlineData("\\\\server\\share\\file.txt")]
    [InlineData("\\\\?\\C:\\device.txt")]
    [InlineData("\\rooted.txt")]
    public async Task Rejects_Windows_rooted_or_drive_paths_on_Windows(string path)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows path grammar is required");
        var root = await CreateRepositoryAsync();
        try
        {
            var result = await (await NewToolAsync(root))
                .CallAsync(Args(path, acknowledgeDirtyFile: true), Ct);
            Assert.True(result.IsError);
            Assert.Contains("repository-relative", result.Text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public async Task Hardened_git_queries_never_launch_the_repository_fsmonitor_helper()
    {
        var root = await CreateRepositoryAsync();
        try
        {
            var marker = Path.Combine(root, "helper-escaped.txt");
            var helper = Path.Combine(root, OperatingSystem.IsWindows()
                ? "fsmonitor-helper.cmd"
                : "fsmonitor-helper.sh");
            var helperBody = OperatingSystem.IsWindows()
                ? $"@echo off{Environment.NewLine}echo escaped>\"{marker}\"{Environment.NewLine}exit /b 0{Environment.NewLine}"
                : $"#!/bin/sh{Environment.NewLine}printf escaped > '{marker}'{Environment.NewLine}exit 0{Environment.NewLine}";
            await File.WriteAllTextAsync(helper, helperBody, Ct);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    helper,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            await GitAsync(root, "config", "core.fsmonitor", helper);
            await File.WriteAllTextAsync(Path.Combine(root, "target.txt"), "damaged\n", Ct);

            var result = await (await NewToolAsync(root))
                .CallAsync(Args("target.txt", acknowledgeDirtyFile: true), Ct);

            if (OperatingSystem.IsWindows())
            {
                Assert.False(result.IsError, result.Text);
            }
            else
            {
                Assert.Contains("identity-anchored", result.Text, StringComparison.OrdinalIgnoreCase);
            }
            Assert.False(File.Exists(marker));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Theory]
    [InlineData("clean", "worktree")]
    [InlineData("clean", "index")]
    [InlineData("clean", "info")]
    [InlineData("clean", "global")]
    [InlineData("process", "worktree")]
    [InlineData("process", "index")]
    [InlineData("process", "info")]
    [InlineData("process", "global")]
    public async Task Configured_filters_are_refused_before_a_marker_helper_can_spawn(
        string filterKind,
        string attributeSource)
    {
        var root = await CreateRepositoryAsync();
        try
        {
            var helper = CrashHostExecutable();
            var marker = Path.Combine(root, "filter-helper-launched.txt");
            var descendantMarker = Path.Combine(root, "filter-descendant-launched.txt");
            var attributeFile = attributeSource switch
            {
                "worktree" => Path.Combine(root, ".gitattributes"),
                "index" => Path.Combine(root, ".gitattributes"),
                "info" => Path.Combine(root, ".git", "info", "attributes"),
                "global" => Path.Combine(root, "global.attributes"),
                _ => throw new ArgumentOutOfRangeException(nameof(attributeSource)),
            };
            Directory.CreateDirectory(Path.GetDirectoryName(attributeFile)!);
            await File.WriteAllTextAsync(
                attributeFile,
                "target.txt filter=restore-check\n",
                Ct);
            if (attributeSource == "index")
            {
                await GitAsync(root, "add", ".gitattributes");
            }
            else if (attributeSource == "global")
            {
                await GitAsync(root, "config", "core.attributesFile", attributeFile);
            }

            var filterCommand = $"\"{helper}\" filter-helper";
            await GitAsync(root, "config", $"filter.restore-check.{filterKind}", filterCommand);
            await File.WriteAllTextAsync(Path.Combine(root, "target.txt"), "damaged\n", Ct);

            var result = await (await NewToolAsync(
                    root,
                    new ExactFileRestoreTestHooks(
                        GitEnvironment: new Dictionary<string, string?>
                        {
                            ["BATON_EXACT_RESTORE_FILTER_HELPER_MARKER"] = marker,
                            ["BATON_EXACT_RESTORE_FILTER_DESCENDANT_MARKER"] = descendantMarker,
                        })))
                .CallAsync(Args("target.txt", acknowledgeDirtyFile: true), Ct);

            Assert.True(result.IsError);
            Assert.Contains("filter", result.Text, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(marker));
            Assert.False(File.Exists(descendantMarker));
            Assert.Equal("damaged\n", await File.ReadAllTextAsync(Path.Combine(root, "target.txt"), Ct));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Noisy_git_timeout_and_cancellation_are_bounded_and_kill_the_child(
        bool callerCancellation)
    {
        var root = await CreateRepositoryAsync();
        try
        {
            var pidFile = Path.Combine(root, "git.pid");
            var executable = CrashHostExecutable();
            var hooks = new ExactFileRestoreTestHooks(
                GitFileName: executable,
                GitTimeout: callerCancellation ? TimeSpan.FromSeconds(10) : TimeSpan.FromMilliseconds(250), // wait-ok: deliberately short production timeout under test.
                GitEnvironment: new Dictionary<string, string?>
                {
                    ["BATON_EXACT_RESTORE_GIT_MODE"] = "noisy",
                    ["BATON_EXACT_RESTORE_GIT_PID_FILE"] = pidFile,
                });
            var baseSha = await GitAsync(root, "rev-parse", "HEAD");
            var tool = new ExactFileRestoreTool(
                root, baseSha, "execution-1", Path.Combine(root, "room"), hooks);
            using var cancellation = callerCancellation
                ? new CancellationTokenSource(TimeSpan.FromMilliseconds(250)) // wait-ok: deliberately short caller-cancellation trigger under test.
                : new CancellationTokenSource();
            var started = Stopwatch.StartNew();

            var result = await tool.CallAsync(
                Args("target.txt", acknowledgeDirtyFile: true), cancellation.Token);

            Assert.True(result.IsError);
            Assert.Contains(
                callerCancellation ? "cancelled" : "timed out",
                result.Text,
                StringComparison.OrdinalIgnoreCase);
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(8), $"teardown took {started.Elapsed}");
            Assert.True(File.Exists(pidFile), "the noisy native child did not reach its PID checkpoint");
            var pid = int.Parse(await File.ReadAllTextAsync(pidFile, Ct));
            await AssertProcessExitedAsync(pid);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public async Task Nonzero_git_exit_is_an_MCP_refusal()
    {
        var root = await CreateRepositoryAsync();
        try
        {
            var tool = new ExactFileRestoreTool(
                root,
                await GitAsync(root, "rev-parse", "HEAD"),
                "execution-1",
                Path.Combine(root, "room"),
                new ExactFileRestoreTestHooks(
                    GitFileName: CrashHostExecutable(),
                    GitEnvironment: new Dictionary<string, string?>
                    {
                        ["BATON_EXACT_RESTORE_GIT_MODE"] = "exit",
                    }));

            var result = await tool.CallAsync(Args("target.txt", acknowledgeDirtyFile: true), Ct);

            Assert.True(result.IsError);
            Assert.Contains("code 23", result.Text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public async Task Git_start_failure_is_an_MCP_refusal()
    {
        var root = await CreateRepositoryAsync();
        try
        {
            var tool = new ExactFileRestoreTool(
                root,
                await GitAsync(root, "rev-parse", "HEAD"),
                "execution-1",
                Path.Combine(root, "room"),
                new ExactFileRestoreTestHooks(
                    GitFileName: Path.Combine(root, "missing-git-executable")));

            var result = await tool.CallAsync(Args("target.txt", acknowledgeDirtyFile: true), Ct);

            Assert.True(result.IsError);
            Assert.Contains("could not start", result.Text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    private static async Task<string> CreateRepositoryAsync(string relativePath = "target.txt")
    {
        var root = TempDir();
        Directory.CreateDirectory(root);
        await GitAsync(root, "init");
        await GitAsync(root, "config", "user.name", "Test");
        await GitAsync(root, "config", "user.email", "test@test.com");
        var target = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, "base\n", Ct);
        await GitAsync(root, "add", "-A");
        await GitAsync(root, "commit", "-m", "base");
        return root;
    }

    private static async Task<ExactFileRestoreTool> NewToolAsync(string root) =>
        new(root, await GitAsync(root, "rev-parse", "HEAD"), "execution-1", Path.Combine(root, "room"));

    private static async Task<ExactFileRestoreTool> NewToolAsync(
        string root,
        ExactFileRestoreTestHooks hooks) =>
        new(
            root,
            await GitAsync(root, "rev-parse", "HEAD"),
            "execution-1",
            Path.Combine(root, "room"),
            hooks);

    private static string AuditPath(string root) =>
        Path.Combine(root, "room", ".baton", ExactFileRestoreTool.AuditFileName);

    private static ExactFileRestoreAudit ValidAudit(string root) =>
        new(
            3,
            nameof(ExactFileRestoreAuditState.Prepared),
            "old-execution",
            "target.txt",
            new string('a', 40),
            new string('b', 40),
            new string('c', 40),
            new string('c', 40),
            DateTimeOffset.UtcNow,
            ".target.txt.baton-quarantine-old",
            null,
            "old-transaction",
            Path.GetFullPath(root),
            ".target.txt.baton-restore-old.tmp",
            ".target.txt.baton-uncommitted-old");

    private static string CrashHostExecutable()
    {
        var directory = Path.GetDirectoryName(typeof(Scenarios).Assembly.Location)!;
        return Path.Combine(
            directory,
            "Baton.CrashTestHost" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
    }

    private static async Task AssertProcessExitedAsync(int pid)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                return;
            }

            await Task.Delay(
                50, // wait-ok: bounded process-death polling, not an operation ceiling.
                Ct);
        }

        Assert.Fail($"contained git child {pid} survived teardown");
    }

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
