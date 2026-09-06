using Baton.Queue;

namespace Baton.Tests.Queue;

/// <summary>
/// The queue file's own contract: round-trip, the read-modify-write critical section, and the
/// asymmetry between an absent file (empty queue) and a malformed one (a refusal, never a silent
/// wipe of the operator's work list).
/// </summary>
public sealed class QueueStoreTests
{
    private static string TempQueuePath() =>
        Path.Combine(Path.GetTempPath(), $"baton-queue-test-{Guid.NewGuid():N}", "queue.json");

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
    public async Task An_absent_file_reads_as_an_empty_queue()
    {
        var snapshot = await QueueStore.LoadAsync(TempQueuePath());

        Assert.Empty(snapshot.Items);
        Assert.False(snapshot.Held);
    }

    [Fact]
    public async Task Items_and_the_hold_flag_round_trip_through_the_file()
    {
        var path = TempQueuePath();
        try
        {
            await QueueStore.MutateAsync(path, s => s with { Items = [Item("a"), Item("b")], Held = true });
            var read = await QueueStore.LoadAsync(path);

            Assert.Equal(["a", "b"], read.Items.Select(i => i.Tag));
            Assert.True(read.Held);
            Assert.Equal("engine", read.Items[0].ScopeClass);
            Assert.Equal(QueueItemState.Queued, read.Items[0].State);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task A_mutation_sees_what_the_previous_one_wrote()
    {
        var path = TempQueuePath();
        try
        {
            await QueueStore.MutateAsync(path, s => s with { Items = [Item("a")] });
            await QueueStore.MutateAsync(path, s => s with { Items = s.Items.Append(Item("b")).ToList() });

            var read = await QueueStore.LoadAsync(path);
            Assert.Equal(["a", "b"], read.Items.Select(i => i.Tag));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task A_malformed_file_is_refused_rather_than_read_as_an_empty_queue()
    {
        var path = TempQueuePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{ not json at all");
        try
        {
            // The control arm is the absent-file test above: absent reads empty, malformed throws. If
            // both behaved the same, a hand-mangled queue would silently become an empty one.
            await Assert.ThrowsAsync<QueueStoreException>(() => QueueStore.LoadAsync(path));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void The_lock_prefix_is_this_stores_own_and_not_a_ledgers()
    {
        // Pinned literally: renaming it would let an older and a newer baton take two different
        // mutexes against the same file, and sharing a ledger's would make the two contend.
        Assert.Equal("baton-queue", QueueStore.LockNamePrefix);
    }

    private static void Cleanup(string path)
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A temp directory that will not delete is not this test's subject.
        }
    }
}
