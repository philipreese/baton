using System.Security.Cryptography;
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
            File.Delete(outside);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(fixtureRoot);
            DirectoryCleanup.DeleteRecursively(retentionRoot);
        }
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
            File.Delete(retentionRootThatIsActuallyAFile);
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
