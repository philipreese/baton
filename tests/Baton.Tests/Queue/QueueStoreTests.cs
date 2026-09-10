using Baton.Queue;

namespace Baton.Tests.Queue;

/// <summary>
/// The queue file's own contract: round-trip, the read-modify-write critical section, and the
/// asymmetry between an absent file (empty queue) and a malformed one (a refusal, never a silent
/// wipe of the operator's work list).
/// </summary>
public sealed class QueueStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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
        var snapshot = await QueueStore.LoadAsync(TempQueuePath(), Ct);

        Assert.Empty(snapshot.Items);
        Assert.False(snapshot.Held);
    }

    [Fact]
    public async Task Items_and_the_hold_flag_round_trip_through_the_file()
    {
        var path = TempQueuePath();
        try
        {
            await QueueStore.MutateAsync(path, s => s with { Items = [Item("a"), Item("b")], Held = true }, Ct);
            var read = await QueueStore.LoadAsync(path, Ct);

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
    public async Task Lifecycle_selection_intent_and_the_legacy_compatibility_shape_survive_a_restart()
    {
        var path = TempQueuePath();
        try
        {
            var staged = Item("staged") with
            {
                Stage = WorkStage.Review,
                Repository = "github.com/aer-works/baton",
                StageSelections =
                [
                    new QueueStageSelection
                    {
                        Stage = WorkStage.Review,
                        Model = "gpt-5.6-sol",
                        Reason = "independent review",
                    },
                ],
                TokenBudget = 600_000,
            };
            // Null is the on-disk shape an item written before #2181 has: its stored axis retains
            // the old all-stage interpretation rather than being reclassified on load.
            var legacy = Item("legacy") with { Stage = WorkStage.Review, Model = "sonnet", StageSelections = null };
            await QueueStore.MutateAsync(path, s => s with { Items = [staged, legacy] }, Ct);

            var read = await QueueStore.LoadAsync(path, Ct);
            Assert.Equal("gpt-5.6-sol", read.Items[0].StageSelections!.Single().Model);
            Assert.Equal(600_000, read.Items[0].TokenBudget);
            Assert.Equal("github.com/aer-works/baton", read.Items[0].Repository);
            Assert.Null(read.Items[1].Repository);
            var (_, source) = QueueTierTable.SelectionForStage(read.Items[1], WorkStage.ReReview);
            Assert.Equal(QueueSelectionSource.PersistedLifecycleCompatibility, source);
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
            await QueueStore.MutateAsync(path, s => s with { Items = [Item("a")] }, Ct);
            await QueueStore.MutateAsync(path, s => s with { Items = s.Items.Append(Item("b")).ToList() }, Ct);

            var read = await QueueStore.LoadAsync(path, Ct);
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
        await File.WriteAllTextAsync(path, "{ not json at all", Ct);
        try
        {
            // The control arm is the absent-file test above: absent reads empty, malformed throws. If
            // both behaved the same, a hand-mangled queue would silently become an empty one.
            await Assert.ThrowsAsync<QueueStoreException>(() => QueueStore.LoadAsync(path, Ct));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void The_lock_prefix_is_this_stores_own_and_not_a_ledgers()
    {
        // Pinned as a literal, not read back from the constant — the string itself is the contract
        // with every other baton build on this machine, so an assertion against the constant would
        // pass through any rename.
        Assert.Equal("baton-queue", QueueStore.LockNamePrefix);
    }

    private static void Cleanup(string path) => DirectoryCleanup.DeleteRecursively(Path.GetDirectoryName(path)!);
}
