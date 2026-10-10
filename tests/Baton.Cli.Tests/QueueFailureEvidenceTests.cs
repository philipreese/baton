using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Baton.Domain;
using Baton.Queue;
using Baton.Tests.Shared;

namespace Baton.Cli.Tests;

/// <summary>
/// Controls for the #2515 test-only passive capture: <see cref="QueueFailureEvidence"/> must retain
/// bounded, post-unwind evidence only for the real held-destination <see cref="QueueStoreException"/>
/// family, never touch a path it was not explicitly handed, never mask its own failures, and never
/// alter the original exception it is capturing around.
/// </summary>
public sealed class QueueFailureEvidenceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewFixtureRoot(string prefix) =>
        Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");

    private static QueueItem Item(string tag) => new()
    {
        Tag = tag,
        Role = "implement",
        Workspace = @"C:\repos\w1",
        SpecFile = @"C:\baton\queue\specs\t.md",
        ScopeClass = "engine",
        AddedAt = new DateTimeOffset(2026, 9, 5, 23, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public async Task A_real_held_destination_failure_is_retained_survives_fixture_cleanup_and_leaves_the_original_exception_untouched()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows share-mode exclusion is required");

        var fixtureRoot = NewFixtureRoot("baton_qfe_fixture");
        var retentionRoot = NewFixtureRoot("baton_qfe_retention");
        Directory.CreateDirectory(fixtureRoot);
        Directory.CreateDirectory(retentionRoot);
        var queuePath = Path.Combine(fixtureRoot, "queue.json");
        string? retained = null;
        try
        {
            await QueueStore.MutateAsync(queuePath, s => s with { Items = [Item("original")] }, Ct);
            var originalBytes = await File.ReadAllBytesAsync(queuePath, Ct);

            using (new FileStream(queuePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var ex = await Assert.ThrowsAsync<QueueStoreException>(() =>
                    QueueStore.MutateAsync(queuePath, s => s with { Items = [Item("replacement")] }, Ct));

                Assert.True(QueueFailureEvidence.IsCapturable(ex));
                retained = QueueFailureEvidence.Retain(ex, nameof(A_real_held_destination_failure_is_retained_survives_fixture_cleanup_and_leaves_the_original_exception_untouched),
                    fixtureRoot, [queuePath], retentionRoot);

                // The capture never alters the exception the caller is unwinding.
                Assert.Contains("replace", ex.Message, StringComparison.OrdinalIgnoreCase);
                Assert.True(ex.InnerException is IOException or UnauthorizedAccessException);
            }

            Assert.NotNull(retained);
            var manifestText = await File.ReadAllTextAsync(Path.Combine(retained!, "manifest.txt"), Ct);
            Assert.Contains("status=retained", manifestText, StringComparison.Ordinal);
            Assert.Contains("Operation: queue replacement", manifestText, StringComparison.Ordinal);
            Assert.Contains(
                "QueueStore has already attempted its own staged-temp cleanup", manifestText, StringComparison.Ordinal);
            var expectedHash = Convert.ToHexString(SHA256.HashData(originalBytes)).ToLowerInvariant();
            Assert.Contains(expectedHash, manifestText, StringComparison.OrdinalIgnoreCase);

            // The original queue bytes themselves were never touched by the failed replacement.
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(queuePath, Ct));

            // The fixture's own cleanup now runs (as the two real fixtures' `finally` does) — the
            // retained copy must survive it untouched.
            DirectoryCleanup.DeleteRecursively(fixtureRoot);
            Assert.False(Directory.Exists(fixtureRoot));
            var retainedBytes = await File.ReadAllBytesAsync(
                Directory.GetFiles(retained!, "*queue.json").Single(), Ct);
            Assert.Equal(originalBytes, retainedBytes);
        }
        finally
        {
            if (Directory.Exists(fixtureRoot)) DirectoryCleanup.DeleteRecursively(fixtureRoot);
            if (Directory.Exists(retentionRoot)) DirectoryCleanup.DeleteRecursively(retentionRoot);
        }
    }

    [Fact]
    public async Task Without_capture_the_same_held_destination_failures_evidence_is_lost_to_fixture_cleanup()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows share-mode exclusion is required");

        // This is the problem #2515 exists to narrow: an isolated fixture's own `finally` destroys the
        // only evidence of a real held-destination failure before anything can inspect it.
        var fixtureRoot = NewFixtureRoot("baton_qfe_nocapture");
        Directory.CreateDirectory(fixtureRoot);
        var queuePath = Path.Combine(fixtureRoot, "queue.json");
        try
        {
            await QueueStore.MutateAsync(queuePath, s => s with { Items = [Item("original")] }, Ct);
            using (new FileStream(queuePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await Assert.ThrowsAsync<QueueStoreException>(() =>
                    QueueStore.MutateAsync(queuePath, s => s with { Items = [Item("replacement")] }, Ct));
                // No QueueFailureEvidence.Retain call here — the old behavior.
            }
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(fixtureRoot);
        }

        Assert.False(Directory.Exists(fixtureRoot));
    }

    [Fact]
    public async Task Bounds_cap_retained_files_at_eight_and_record_the_remainder_as_file_limited()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows share-mode exclusion is required");

        var fixtureRoot = NewFixtureRoot("baton_qfe_bounds");
        var retentionRoot = NewFixtureRoot("baton_qfe_bounds_retention");
        Directory.CreateDirectory(fixtureRoot);
        Directory.CreateDirectory(retentionRoot);
        var queuePath = Path.Combine(fixtureRoot, "queue.json");
        try
        {
            await QueueStore.MutateAsync(queuePath, s => s with { Items = [Item("original")] }, Ct);

            // Ten tiny siblings plus the queue file itself: eleven candidates, well over the eight-file
            // cap, but each small enough that the byte budget is never the limiting factor.
            for (var i = 0; i < 10; i++)
            {
                await File.WriteAllTextAsync(Path.Combine(fixtureRoot, $"sibling-{i}.tmp"), "x", Ct);
            }

            string? retained;
            using (new FileStream(queuePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var ex = await Assert.ThrowsAsync<QueueStoreException>(() =>
                    QueueStore.MutateAsync(queuePath, s => s with { Items = [Item("replacement")] }, Ct));
                retained = QueueFailureEvidence.Retain(ex,
                    nameof(Bounds_cap_retained_files_at_eight_and_record_the_remainder_as_file_limited),
                    fixtureRoot, [queuePath], retentionRoot);
            }

            Assert.NotNull(retained);
            var retainedFiles = Directory.GetFiles(retained!).Where(f => !f.EndsWith("manifest.txt", StringComparison.Ordinal)).ToList();
            Assert.Equal(QueueFailureEvidence.MaxRetainedFiles, retainedFiles.Count);

            var totalBytes = retainedFiles.Sum(f => new FileInfo(f).Length)
                + new FileInfo(Path.Combine(retained!, "manifest.txt")).Length;
            Assert.True(totalBytes <= QueueFailureEvidence.MaxTotalBytes,
                $"Expected at most {QueueFailureEvidence.MaxTotalBytes} total bytes, got {totalBytes}");

            var manifestText = await File.ReadAllTextAsync(Path.Combine(retained!, "manifest.txt"), Ct);
            Assert.Contains("file-limit", manifestText, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(fixtureRoot);
            DirectoryCleanup.DeleteRecursively(retentionRoot);
        }
    }

    [Fact]
    public async Task A_sibling_larger_than_the_remaining_budget_is_retained_as_a_labeled_partial_prefix()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows share-mode exclusion is required");

        var fixtureRoot = NewFixtureRoot("baton_qfe_partial");
        var retentionRoot = NewFixtureRoot("baton_qfe_partial_retention");
        Directory.CreateDirectory(fixtureRoot);
        Directory.CreateDirectory(retentionRoot);
        var queuePath = Path.Combine(fixtureRoot, "queue.json");
        try
        {
            await QueueStore.MutateAsync(queuePath, s => s with { Items = [Item("original")] }, Ct);
            var oversized = new byte[2 * 1024 * 1024];
            Random.Shared.NextBytes(oversized);
            await File.WriteAllBytesAsync(Path.Combine(fixtureRoot, "oversized.tmp"), oversized, Ct);

            string? retained;
            using (new FileStream(queuePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var ex = await Assert.ThrowsAsync<QueueStoreException>(() =>
                    QueueStore.MutateAsync(queuePath, s => s with { Items = [Item("replacement")] }, Ct));
                retained = QueueFailureEvidence.Retain(ex,
                    nameof(A_sibling_larger_than_the_remaining_budget_is_retained_as_a_labeled_partial_prefix),
                    fixtureRoot, [queuePath], retentionRoot);
            }

            Assert.NotNull(retained);
            var totalBytes = Directory.GetFiles(retained!).Sum(f => new FileInfo(f).Length);
            Assert.True(totalBytes <= QueueFailureEvidence.MaxTotalBytes,
                $"Expected at most {QueueFailureEvidence.MaxTotalBytes} total bytes, got {totalBytes}");

            var manifestText = await File.ReadAllTextAsync(Path.Combine(retained!, "manifest.txt"), Ct);
            Assert.Contains("partial-prefix=True", manifestText, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(fixtureRoot);
            DirectoryCleanup.DeleteRecursively(retentionRoot);
        }
    }

    [Fact]
    public async Task An_unreadable_sibling_is_recorded_as_unreadable_with_its_hresult()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows share-mode exclusion is required");

        var fixtureRoot = NewFixtureRoot("baton_qfe_unreadable");
        var retentionRoot = NewFixtureRoot("baton_qfe_unreadable_retention");
        Directory.CreateDirectory(fixtureRoot);
        Directory.CreateDirectory(retentionRoot);
        var queuePath = Path.Combine(fixtureRoot, "queue.json");
        var sibling = Path.Combine(fixtureRoot, "locked.tmp");
        try
        {
            await QueueStore.MutateAsync(queuePath, s => s with { Items = [Item("original")] }, Ct);
            await File.WriteAllTextAsync(sibling, "locked", Ct);

            string? retained;
            using (new FileStream(queuePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (new FileStream(sibling, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var ex = await Assert.ThrowsAsync<QueueStoreException>(() =>
                    QueueStore.MutateAsync(queuePath, s => s with { Items = [Item("replacement")] }, Ct));
                retained = QueueFailureEvidence.Retain(ex,
                    nameof(An_unreadable_sibling_is_recorded_as_unreadable_with_its_hresult),
                    fixtureRoot, [queuePath], retentionRoot);
            }

            Assert.NotNull(retained);
            var manifestText = await File.ReadAllTextAsync(Path.Combine(retained!, "manifest.txt"), Ct);
            Assert.Contains("locked.tmp", manifestText, StringComparison.Ordinal);
            Assert.Contains("status=unreadable", manifestText, StringComparison.Ordinal);
            Assert.Contains("HResult=0x", manifestText, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(fixtureRoot);
            DirectoryCleanup.DeleteRecursively(retentionRoot);
        }
    }

    [Fact]
    public async Task An_outside_file_pointed_to_by_queue_json_fields_is_never_read_or_copied()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows share-mode exclusion is required");

        var fixtureRoot = NewFixtureRoot("baton_qfe_outside_fixture");
        var retentionRoot = NewFixtureRoot("baton_qfe_outside_retention");
        var outsideRoot = NewFixtureRoot("baton_qfe_outside_sentinel");
        Directory.CreateDirectory(fixtureRoot);
        Directory.CreateDirectory(retentionRoot);
        Directory.CreateDirectory(outsideRoot);
        var queuePath = Path.Combine(fixtureRoot, "queue.json");
        var sentinel = Path.Combine(outsideRoot, "never-read.md");
        const string marker = "do-not-read-this-marker-2515";
        try
        {
            await File.WriteAllTextAsync(sentinel, marker, Ct);
            var sentinelBefore = await File.ReadAllTextAsync(sentinel, Ct);
            var sentinelWriteBefore = File.GetLastWriteTimeUtc(sentinel);

            // One actual fixture item deliberately carries an outside pointer the queue JSON itself
            // names — Workspace/SpecFile here are the attacker-shaped data the capture must never follow.
            await QueueStore.MutateAsync(queuePath, s => s with
            {
                Items = [Item("original") with { Workspace = outsideRoot, SpecFile = sentinel }],
            }, Ct);

            string? retained;
            using (new FileStream(queuePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var ex = await Assert.ThrowsAsync<QueueStoreException>(() =>
                    QueueStore.MutateAsync(queuePath, s => s with { Items = [Item("replacement")] }, Ct));
                retained = QueueFailureEvidence.Retain(ex,
                    nameof(An_outside_file_pointed_to_by_queue_json_fields_is_never_read_or_copied),
                    fixtureRoot, [queuePath], retentionRoot);
            }

            Assert.NotNull(retained);
            Assert.Equal(sentinelBefore, await File.ReadAllTextAsync(sentinel, Ct));
            Assert.Equal(sentinelWriteBefore, File.GetLastWriteTimeUtc(sentinel));
            Assert.DoesNotContain("never-read.md", Directory.GetFiles(retained!).Select(Path.GetFileName));
            var manifestText = await File.ReadAllTextAsync(Path.Combine(retained!, "manifest.txt"), Ct);
            Assert.DoesNotContain(marker, manifestText, StringComparison.Ordinal);
            Assert.DoesNotContain("never-read.md", manifestText, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(fixtureRoot);
            DirectoryCleanup.DeleteRecursively(retentionRoot);
            DirectoryCleanup.DeleteRecursively(outsideRoot);
        }
    }

    [Fact]
    public void A_fixture_root_that_is_the_bare_temp_directory_is_refused()
    {
        var ex = new QueueStoreException("Could not perform queue replacement for the queue at 'x': boom",
            new IOException("boom"));
        var retained = QueueFailureEvidence.Retain(ex, nameof(A_fixture_root_that_is_the_bare_temp_directory_is_refused),
            Path.GetTempPath(), [Path.Combine(Path.GetTempPath(), "queue.json")]);
        Assert.Null(retained);
    }

    [Fact]
    public void A_queue_path_that_escapes_the_fixture_root_is_refused_and_leaves_the_retention_root_empty()
    {
        var fixtureRoot = NewFixtureRoot("baton_qfe_escape_fixture");
        var retentionRoot = NewFixtureRoot("baton_qfe_escape_retention");
        Directory.CreateDirectory(fixtureRoot);
        Directory.CreateDirectory(retentionRoot);
        try
        {
            var outside = Path.Combine(Path.GetDirectoryName(fixtureRoot)!, "escaped-queue.json");
            File.WriteAllText(outside, "{}");
            var ex = new QueueStoreException("Could not perform queue replacement for the queue at 'x': boom",
                new IOException("boom"));

            var retained = QueueFailureEvidence.Retain(ex,
                nameof(A_queue_path_that_escapes_the_fixture_root_is_refused_and_leaves_the_retention_root_empty),
                fixtureRoot, [outside], retentionRoot);

            Assert.Null(retained);
            Assert.Empty(Directory.GetFileSystemEntries(retentionRoot));
            FileCleanup.Delete(outside);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(fixtureRoot);
            DirectoryCleanup.DeleteRecursively(retentionRoot);
        }
    }

    [Fact]
    public void A_queue_path_that_is_itself_a_reparse_point_targeting_outside_the_fixture_is_refused()
    {
        var fixtureRoot = NewFixtureRoot("baton_qfe_reparse_queue_fixture");
        var retentionRoot = NewFixtureRoot("baton_qfe_reparse_queue_retention");
        var outsideRoot = NewFixtureRoot("baton_qfe_reparse_queue_outside");
        Directory.CreateDirectory(fixtureRoot);
        Directory.CreateDirectory(retentionRoot);
        Directory.CreateDirectory(outsideRoot);
        var outsideFile = Path.Combine(outsideRoot, "secret.json");
        File.WriteAllText(outsideFile, "{\"secret\":true}");
        var linkedQueuePath = Path.Combine(fixtureRoot, "queue.json");
        try
        {
            try
            {
                File.CreateSymbolicLink(linkedQueuePath, outsideFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Assert.Skip($"this host refuses unprivileged symlink creation: {ex.Message}");
            }

            var ex2 = new QueueStoreException("Could not perform queue replacement for the queue at 'x': boom",
                new IOException("boom"));

            var retained = QueueFailureEvidence.Retain(ex2,
                nameof(A_queue_path_that_is_itself_a_reparse_point_targeting_outside_the_fixture_is_refused),
                fixtureRoot, [linkedQueuePath], retentionRoot);

            // The lexical escape check alone would have passed this path — it never leaves the fixture
            // root as a string. Only the reparse-point check rejects it.
            Assert.Null(retained);
            Assert.Empty(Directory.GetFileSystemEntries(retentionRoot));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(fixtureRoot);
            DirectoryCleanup.DeleteRecursively(retentionRoot);
            DirectoryCleanup.DeleteRecursively(outsideRoot);
        }
    }

    [Fact]
    public async Task A_sibling_reparse_point_targeting_outside_the_fixture_is_skipped_and_never_copied()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows share-mode exclusion is required");

        var fixtureRoot = NewFixtureRoot("baton_qfe_reparse_sibling_fixture");
        var retentionRoot = NewFixtureRoot("baton_qfe_reparse_sibling_retention");
        var outsideRoot = NewFixtureRoot("baton_qfe_reparse_sibling_outside");
        Directory.CreateDirectory(fixtureRoot);
        Directory.CreateDirectory(retentionRoot);
        Directory.CreateDirectory(outsideRoot);
        var queuePath = Path.Combine(fixtureRoot, "queue.json");
        var outsideFile = Path.Combine(outsideRoot, "never-read.md");
        const string marker = "do-not-read-this-marker-reparse-sibling";
        var linkedSibling = Path.Combine(fixtureRoot, "sibling.tmp");
        try
        {
            await File.WriteAllTextAsync(outsideFile, marker, Ct);
            try
            {
                File.CreateSymbolicLink(linkedSibling, outsideFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Assert.Skip($"this host refuses unprivileged symlink creation: {ex.Message}");
            }

            await QueueStore.MutateAsync(queuePath, s => s with { Items = [Item("original")] }, Ct);

            string? retained;
            using (new FileStream(queuePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var ex = await Assert.ThrowsAsync<QueueStoreException>(() =>
                    QueueStore.MutateAsync(queuePath, s => s with { Items = [Item("replacement")] }, Ct));
                retained = QueueFailureEvidence.Retain(ex,
                    nameof(A_sibling_reparse_point_targeting_outside_the_fixture_is_skipped_and_never_copied),
                    fixtureRoot, [queuePath], retentionRoot);
            }

            // The real queue path is still retained; only the reparse-point sibling is refused — the
            // Directory.Exists filter that already screens out directory-typed reparse points does not
            // catch this file-typed one, so it depends entirely on the explicit reparse-point check.
            Assert.NotNull(retained);
            Assert.DoesNotContain("sibling.tmp", Directory.GetFiles(retained!).Select(Path.GetFileName));
            var manifestText = await File.ReadAllTextAsync(Path.Combine(retained!, "manifest.txt"), Ct);
            Assert.DoesNotContain(marker, manifestText, StringComparison.Ordinal);
            Assert.Contains("sibling-reparse-point", manifestText, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(fixtureRoot);
            DirectoryCleanup.DeleteRecursively(retentionRoot);
            DirectoryCleanup.DeleteRecursively(outsideRoot);
        }
    }

    [Fact]
    public async Task A_junction_ancestor_inside_the_fixture_tree_is_refused_and_its_outside_target_is_never_read()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "junctions are Windows-only");

        var fixtureRoot = NewFixtureRoot("baton_qfe_junction_fixture");
        var retentionRoot = NewFixtureRoot("baton_qfe_junction_retention");
        var outsideRoot = NewFixtureRoot("baton_qfe_junction_outside");
        Directory.CreateDirectory(fixtureRoot);
        Directory.CreateDirectory(retentionRoot);
        Directory.CreateDirectory(outsideRoot);
        var junction = Path.Combine(fixtureRoot, "queue");
        const string marker = "do-not-read-this-marker-junction";
        var outsideQueueFile = Path.Combine(outsideRoot, "queue.json");
        try
        {
            await File.WriteAllTextAsync(outsideQueueFile, marker, Ct);

            var startInfo = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{outsideRoot}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = global::Baton.Core.ProcessLaunch.Start(startInfo);
            if (process is null)
            {
                Assert.Skip("this host could not start cmd.exe to create a junction");
            }

            await BoundedProcessWait.WaitForExitAsync(process, TimeSpan.FromSeconds(60), Ct);
            if (process.ExitCode != 0)
            {
                Assert.Skip("this host refused to create a junction");
            }

            // The leaf itself ("queue.json") is a real file -- only "queue", an ancestor of the
            // caller-supplied queue path, is the reparse point. The leaf-only check this PR's review
            // round found missing would have passed this straight through.
            var linkedQueuePath = Path.Combine(junction, "queue.json");
            var ex = new QueueStoreException("Could not perform queue replacement for the queue at 'x': boom",
                new IOException("boom"));

            var retained = QueueFailureEvidence.Retain(ex,
                nameof(A_junction_ancestor_inside_the_fixture_tree_is_refused_and_its_outside_target_is_never_read),
                fixtureRoot, [linkedQueuePath], retentionRoot);

            Assert.Null(retained);
            Assert.Empty(Directory.GetFileSystemEntries(retentionRoot));
        }
        finally
        {
            // Unlink the junction (non-recursive) before the fixture root's own recursive delete --
            // deleting through a live junction would otherwise reach into outsideRoot and delete the
            // sentinel this test depends on existing to prove it was never read.
            if (Directory.Exists(junction))
            {
                Directory.Delete(junction, recursive: false);
            }

            DirectoryCleanup.DeleteRecursively(fixtureRoot);
            DirectoryCleanup.DeleteRecursively(retentionRoot);
            DirectoryCleanup.DeleteRecursively(outsideRoot);
        }
    }

    [Fact]
    public void A_fixture_root_outside_the_machine_temp_directory_is_refused()
    {
        var retentionRoot = NewFixtureRoot("baton_qfe_nontemp_retention");
        Directory.CreateDirectory(retentionRoot);

        // A synthetic root under the test binary's own output directory -- never a real user/vendor
        // home -- stands in for "anywhere that is not an owned temp fixture", per the issue's
        // explicit "reject ... user/vendor/production homes" bound.
        var nonTempRoot = Path.Combine(AppContext.BaseDirectory, $"baton_qfe_nontemp_fixture_{Guid.NewGuid():N}");
        Directory.CreateDirectory(nonTempRoot);
        var queuePath = Path.Combine(nonTempRoot, "queue.json");
        File.WriteAllText(queuePath, "{}");
        try
        {
            var ex = new QueueStoreException("Could not perform queue replacement for the queue at 'x': boom",
                new IOException("boom"));

            var retained = QueueFailureEvidence.Retain(ex,
                nameof(A_fixture_root_outside_the_machine_temp_directory_is_refused),
                nonTempRoot, [queuePath], retentionRoot);

            Assert.Null(retained);
            Assert.Empty(Directory.GetFileSystemEntries(retentionRoot));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(nonTempRoot);
            DirectoryCleanup.DeleteRecursively(retentionRoot);
        }
    }

    [Fact]
    public void TruncateUtf8_backs_off_to_a_character_boundary_instead_of_splitting_one_or_throwing()
    {
        // Each character here is a 3-byte UTF-8 codepoint, so byte count is 3x character count --
        // well past a byte limit a naive character-indexed slice would treat as safely under length,
        // and not a multiple of 3, so a naive byte cut lands mid-character.
        var text = new string('\u2603', 1000);
        var bytes = Encoding.UTF8.GetBytes(text);
        Assert.True(bytes.Length > text.Length);

        var truncated = QueueFailureEvidence.TruncateUtf8(bytes, 100);

        Assert.True(truncated.Length <= 100);
        // A result that split a character would fail to round-trip through decode/re-encode.
        Assert.Equal(truncated, Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(truncated)));
    }

    [Fact]
    public void ReportRetained_swallows_a_throwing_writer_instead_of_letting_it_propagate()
    {
        var thrown = Record.Exception(() =>
            QueueFailureEvidence.ReportRetained("C:\\fake\\retained\\path", _ => throw new InvalidOperationException("boom")));

        Assert.Null(thrown);
    }

    [Fact]
    public void Caller_queue_paths_stop_at_a_bounded_prefix_and_label_exhaustion()
    {
        var fixtureRoot = NewFixtureRoot("baton_qfe_caller_bound");
        var retentionRoot = NewFixtureRoot("baton_qfe_caller_retention");
        Directory.CreateDirectory(fixtureRoot);
        Directory.CreateDirectory(retentionRoot);
        var queuePath = Path.Combine(fixtureRoot, "queue.json");
        File.WriteAllText(queuePath, "{}");
        var paths = new QueuePathsWithOverreadTrap(queuePath);
        try
        {
            var original = new QueueStoreException("queue replacement failed", new IOException("boom"));
            var retained = QueueFailureEvidence.Retain(original, nameof(Caller_queue_paths_stop_at_a_bounded_prefix_and_label_exhaustion),
                fixtureRoot, paths, retentionRoot);

            Assert.NotNull(retained);
            Assert.Equal(64, paths.Reads);
            Assert.Contains("caller-path-limit-exhausted", File.ReadAllText(Path.Combine(retained!, "manifest.txt")), StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(fixtureRoot);
            DirectoryCleanup.DeleteRecursively(retentionRoot);
        }
    }

    [Fact]
    public void A_nonascii_overflow_manifest_is_labeled_valid_utf8_and_included_in_the_total_byte_bound()
    {
        var fixtureRoot = NewFixtureRoot("baton_qfe_manifest_bound");
        var retentionRoot = NewFixtureRoot("baton_qfe_manifest_retention");
        Directory.CreateDirectory(fixtureRoot);
        Directory.CreateDirectory(retentionRoot);
        var queuePath = Path.Combine(fixtureRoot, "queue.json");
        File.WriteAllBytes(queuePath, new byte[checked((int)QueueFailureEvidence.MaxTotalBytes)]);
        // These lexical escape candidates are never opened. Their non-ASCII metadata exceeds
        // the manifest reserve without creating or reading any real path outside this fixture.
        var escapedPath = Path.Combine(retentionRoot, new string('\u2603', 2048));
        var paths = new[] { queuePath }.Concat(Enumerable.Repeat(escapedPath, 32)).ToArray();
        try
        {
            var original = new QueueStoreException("queue replacement failed", new IOException("boom"));
            var retained = QueueFailureEvidence.Retain(original,
                nameof(A_nonascii_overflow_manifest_is_labeled_valid_utf8_and_included_in_the_total_byte_bound),
                fixtureRoot, paths, retentionRoot);

            Assert.NotNull(retained);
            var manifestBytes = File.ReadAllBytes(Path.Combine(retained!, "manifest.txt"));
            Assert.True(manifestBytes.LongLength <= QueueFailureEvidence.ManifestReserveBytes);
            var text = new UTF8Encoding(false, true).GetString(manifestBytes);
            Assert.Contains("manifest-truncated", text, StringComparison.Ordinal);
            Assert.True(Directory.GetFiles(retained!).Sum(path => new FileInfo(path).Length) <= QueueFailureEvidence.MaxTotalBytes);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(fixtureRoot);
            DirectoryCleanup.DeleteRecursively(retentionRoot);
        }
    }

    private sealed class QueuePathsWithOverreadTrap(string path) : IReadOnlyList<string>
    {
        public int Count => 65;
        public int Reads { get; private set; }
        public string this[int index]
        {
            get
            {
                if (index >= 64) throw new InvalidOperationException("The caller-path prefix was exceeded.");
                Reads++;
                return path;
            }
        }

        public IEnumerator<string> GetEnumerator()
        {
            for (var index = 0; index < Count; index++) yield return this[index];
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void A_retention_destination_that_cannot_be_created_fails_closed_without_throwing()
    {
        var fixtureRoot = NewFixtureRoot("baton_qfe_destfail_fixture");
        Directory.CreateDirectory(fixtureRoot);
        var queuePath = Path.Combine(fixtureRoot, "queue.json");
        File.WriteAllText(queuePath, "{}");
        var retentionRootThatIsActuallyAFile = NewFixtureRoot("baton_qfe_destfail_retention");
        File.WriteAllText(retentionRootThatIsActuallyAFile, "not a directory");
        try
        {
            var ex = new QueueStoreException("Could not perform queue replacement for the queue at 'x': boom",
                new IOException("boom"));

            var retained = QueueFailureEvidence.Retain(ex,
                nameof(A_retention_destination_that_cannot_be_created_fails_closed_without_throwing),
                fixtureRoot, [queuePath], retentionRootThatIsActuallyAFile);

            Assert.Null(retained);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(fixtureRoot);
            FileCleanup.Delete(retentionRootThatIsActuallyAFile);
        }
    }

    [Theory]
    [InlineData("{ not json at all")]
    public async Task A_malformed_json_queue_store_exception_is_not_capturable(string malformed)
    {
        var fixtureRoot = NewFixtureRoot("baton_qfe_malformed");
        Directory.CreateDirectory(fixtureRoot);
        var queuePath = Path.Combine(fixtureRoot, "queue.json");
        try
        {
            await File.WriteAllTextAsync(queuePath, malformed, Ct);
            var ex = await Assert.ThrowsAsync<QueueStoreException>(() => QueueStore.LoadAsync(queuePath, Ct));

            Assert.False(QueueFailureEvidence.IsCapturable(ex));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(fixtureRoot);
        }
    }

    [Fact]
    public void A_queue_store_exception_with_no_inner_exception_is_not_capturable()
    {
        var ex = new QueueStoreException("Import refused: item 'x' names no role.");
        Assert.False(QueueFailureEvidence.IsCapturable(ex));
    }

    [Fact]
    public async Task A_successful_mutation_creates_no_capture_artifact_because_nothing_is_thrown_to_capture()
    {
        var fixtureRoot = NewFixtureRoot("baton_qfe_success");
        var retentionRoot = NewFixtureRoot("baton_qfe_success_retention");
        Directory.CreateDirectory(fixtureRoot);
        Directory.CreateDirectory(retentionRoot);
        var queuePath = Path.Combine(fixtureRoot, "queue.json");
        try
        {
            await QueueStore.MutateAsync(queuePath, s => s with { Items = [Item("ok")] }, Ct);
            Assert.Empty(Directory.GetFileSystemEntries(retentionRoot));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(fixtureRoot);
            DirectoryCleanup.DeleteRecursively(retentionRoot);
        }
    }
}
