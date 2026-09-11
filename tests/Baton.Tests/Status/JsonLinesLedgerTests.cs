using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Status;

namespace Baton.Tests.Status;

/// <summary>
/// The shared append-only JSONL store behind both the burn ledger (<see cref="QuotaLedgerStore"/>,
/// spec/baton.md §7) and the cost ledger (<c>CostLedgerStore</c>) since #1884 — dedupe, malformed-line
/// tolerance, the concurrency contract, and the empty/missing-file cases, pinned once here rather than
/// once per store. Each store keeps one smoke test of its own pinning the lock-name prefix and the
/// dedupe key it constructs this type with; what the wrappers add on top of the store
/// (<c>BuildEntries</c>, pricing, the read-time fold, <see cref="QuotaLedgerStore.RebuildAsync"/>) stays
/// in their own files.
/// </summary>
public sealed class JsonLinesLedgerTests
{
    private sealed record TestEntry(
        [property: JsonPropertyName("execution")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Execution = null,
        [property: JsonPropertyName("tokensIn")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        long? TokensIn = null);

    /// <summary>The production wiring: dedupe on the entry's execution id, as both real stores do.</summary>
    private static JsonLinesLedger<TestEntry> KeyedLedger() =>
        new("baton-test-ledger", "test ledger", entry => entry.Execution);

    /// <summary>
    /// The control arm for every dedupe assertion below: identical in every respect except that its
    /// selector reports no key for any entry. If the keyed ledger's dedupe were mis-wired — a selector
    /// returning the wrong field, or nothing — this ledger and that one would behave alike, and the
    /// pair of tests that read against each other is what discriminates.
    /// </summary>
    private static JsonLinesLedger<TestEntry> KeylessLedger() =>
        new("baton-test-ledger-keyless", "test ledger", _ => null);

    private static string TempLedgerPath() =>
        Path.Combine(Path.GetTempPath(), $"baton-jsonl-ledger-{Guid.NewGuid():N}.jsonl");

    [Fact]
    public async Task An_execution_id_already_in_the_file_is_not_appended_a_second_time()
    {
        var ledger = KeyedLedger();
        var path = TempLedgerPath();
        try
        {
            await ledger.AppendAsync([new TestEntry("exec-a", 10)], path, TestContext.Current.CancellationToken);
            await ledger.AppendAsync([new TestEntry("exec-a", 10)], path, TestContext.Current.CancellationToken);

            var all = await ledger.ReadAllAsync(path, TestContext.Current.CancellationToken);

            Assert.Single(all);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task With_no_dedupe_key_the_same_entry_appended_twice_is_two_lines()
    {
        // Polarity, the other direction: the skip above is the selector's doing, not the file's.
        var ledger = KeylessLedger();
        var path = TempLedgerPath();
        try
        {
            await ledger.AppendAsync([new TestEntry("exec-a", 10)], path, TestContext.Current.CancellationToken);
            await ledger.AppendAsync([new TestEntry("exec-a", 10)], path, TestContext.Current.CancellationToken);

            var all = await ledger.ReadAllAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(2, all.Count);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task An_unrelated_execution_is_still_appended_alongside_an_already_recorded_one()
    {
        var ledger = KeyedLedger();
        var path = TempLedgerPath();
        try
        {
            await ledger.AppendAsync([new TestEntry("exec-a", 10)], path, TestContext.Current.CancellationToken);
            await ledger.AppendAsync(
                [new TestEntry("exec-a", 10), new TestEntry("exec-b", 20)],
                path,
                TestContext.Current.CancellationToken);

            var all = await ledger.ReadAllAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(2, all.Count);
            Assert.Contains(all, e => e.Execution == "exec-a");
            Assert.Contains(all, e => e.Execution == "exec-b");
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task An_entry_carrying_no_execution_id_is_always_appended()
    {
        // It cannot be deduplicated against anything, so the dedupe filter must pass it through rather
        // than treat "no key" as "already present" -- both stores' fields are independently absent.
        var ledger = KeyedLedger();
        var path = TempLedgerPath();
        try
        {
            await ledger.AppendAsync([new TestEntry(Execution: null, TokensIn: 1)], path, TestContext.Current.CancellationToken);
            await ledger.AppendAsync([new TestEntry(Execution: null, TokensIn: 2)], path, TestContext.Current.CancellationToken);

            var all = await ledger.ReadAllAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(2, all.Count);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task A_malformed_line_is_skipped_rather_than_failing_the_whole_read()
    {
        var ledger = KeyedLedger();
        var path = TempLedgerPath();
        try
        {
            var good = JsonSerializer.Serialize(new TestEntry("exec-a", 10));
            var alsoGood = JsonSerializer.Serialize(new TestEntry("exec-b", 20));
            await File.WriteAllTextAsync(
                path, good + "\n{not json at all\n" + alsoGood + "\n", TestContext.Current.CancellationToken);

            var all = await ledger.ReadAllAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(2, all.Count);
            Assert.Contains(all, e => e.Execution == "exec-a");
            Assert.Contains(all, e => e.Execution == "exec-b");
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task A_malformed_line_does_not_hide_its_execution_id_from_the_dedupe_check()
    {
        // The read tolerance and the dedupe read are the same read: a line that will not parse is a
        // line whose id the append path cannot see, so the entry lands again rather than being skipped.
        var ledger = KeyedLedger();
        var path = TempLedgerPath();
        try
        {
            await File.WriteAllTextAsync(path, "{\"execution\": \"exec-a\", broken\n", TestContext.Current.CancellationToken);

            await ledger.AppendAsync([new TestEntry("exec-a", 10)], path, TestContext.Current.CancellationToken);
            var all = await ledger.ReadAllAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal("exec-a", Assert.Single(all).Execution);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task Append_terminates_a_torn_tail_before_writing_the_recovery_row()
    {
        var ledger = KeyedLedger();
        var path = TempLedgerPath();
        try
        {
            await File.WriteAllTextAsync(
                path, "{\"execution\":\"torn", TestContext.Current.CancellationToken);

            await ledger.AppendAsync(
                [new TestEntry("exec-recovered", 10)], path, TestContext.Current.CancellationToken);

            var recovered = Assert.Single(await ledger.ReadAllAsync(
                path, TestContext.Current.CancellationToken));
            Assert.Equal("exec-recovered", recovered.Execution);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task An_empty_file_reads_as_no_entries()
    {
        var ledger = KeyedLedger();
        var path = TempLedgerPath();
        try
        {
            await File.WriteAllTextAsync(path, string.Empty, TestContext.Current.CancellationToken);

            Assert.Empty(await ledger.ReadAllAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task A_missing_file_reads_as_no_entries_rather_than_throwing()
    {
        var ledger = KeyedLedger();
        var path = TempLedgerPath();

        Assert.False(File.Exists(path));
        Assert.Empty(await ledger.ReadAllAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Appending_creates_a_parent_directory_that_does_not_exist_yet()
    {
        var ledger = KeyedLedger();
        var directory = Path.Combine(Path.GetTempPath(), $"baton-jsonl-ledger-dir-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "nested", "ledger.jsonl");
        try
        {
            await ledger.AppendAsync([new TestEntry("exec-a", 10)], path, TestContext.Current.CancellationToken);

            Assert.Single(await ledger.ReadAllAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public async Task Appending_nothing_never_creates_the_file()
    {
        var ledger = KeyedLedger();
        var path = TempLedgerPath();
        try
        {
            await ledger.AppendAsync([], path, TestContext.Current.CancellationToken);

            Assert.False(File.Exists(path));
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task Concurrent_appends_from_many_tasks_lose_no_entries()
    {
        var ledger = KeyedLedger();
        var path = TempLedgerPath();
        try
        {
            const int writerCount = 12;
            var executionIds = Enumerable.Range(0, writerCount).Select(i => $"exec-concurrent-{i}").ToList();

            await Task.WhenAll(executionIds.Select(id => Task.Run(() =>
                ledger.AppendAsync([new TestEntry(id, 1)], path, TestContext.Current.CancellationToken))));

            var all = await ledger.ReadAllAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(writerCount, all.Count);
            var found = all.Select(e => e.Execution).ToHashSet(StringComparer.Ordinal);
            Assert.All(executionIds, id => Assert.Contains(id, found));
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task AppendAsync_throws_a_sanctioned_exception_when_the_ledger_path_is_itself_a_directory()
    {
        // The fail-open contract's own instrument: each store's settle-time call site catches exactly
        // IOException/UnauthorizedAccessException/WaitHandleCannotBeOpenedException and swallows them.
        // Pointing the "file" path at a real directory forces the FileStream open to throw one of those
        // with no mock or injected writer -- proving the exception this store's contract promises is
        // one the caller's catch clause actually reaches.
        var ledger = KeyedLedger();
        var path = Path.Combine(Path.GetTempPath(), $"baton-jsonl-ledger-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        try
        {
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => ledger.AppendAsync(
                [new TestEntry("exec-a", 1)], path, TestContext.Current.CancellationToken));

            Assert.True(
                ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException,
                $"Expected one of the three sanctioned fail-open exceptions, got {ex.GetType()}: {ex.Message}");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(path);
        }
    }

    [Fact]
    public async Task ReadAllAsync_fails_open_to_an_empty_list_when_the_path_is_a_directory()
    {
        var ledger = KeyedLedger();
        var path = Path.Combine(Path.GetTempPath(), $"baton-jsonl-ledger-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        try
        {
            Assert.Empty(await ledger.ReadAllAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(path);
        }
    }

    [Fact]
    public async Task Entries_are_read_back_in_write_order()
    {
        var ledger = KeyedLedger();
        var path = TempLedgerPath();
        try
        {
            await ledger.AppendAsync([new TestEntry("exec-a", 1)], path, TestContext.Current.CancellationToken);
            await ledger.AppendAsync([new TestEntry("exec-b", 2)], path, TestContext.Current.CancellationToken);
            await ledger.AppendAsync([new TestEntry("exec-c", 3)], path, TestContext.Current.CancellationToken);

            var all = await ledger.ReadAllAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(["exec-a", "exec-b", "exec-c"], all.Select(e => e.Execution));
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public void Two_ledgers_with_different_prefixes_never_share_a_lock_on_the_same_file()
    {
        // What the per-store prefix buys, and the reason each store's own smoke test pins its literal.
        var path = TempLedgerPath();

        Assert.NotEqual(
            MutexGuardedFileLock.BuildMutexName(path, KeyedLedger().LockNamePrefix),
            MutexGuardedFileLock.BuildMutexName(path, KeylessLedger().LockNamePrefix));
    }
}
