using Baton.Queue;
using Baton.Domain;

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
            await QueueStore.MutateAsync(path, s => s with
            {
                Items = [Item("a") with { Skills = ["house-style", "thorough-review"] }, Item("b")],
                Held = true,
            }, Ct);
            var read = await QueueStore.LoadAsync(path, Ct);

            Assert.Equal(["a", "b"], read.Items.Select(i => i.Tag));
            Assert.True(read.Held);
            Assert.Equal("engine", read.Items[0].ScopeClass);
            Assert.Equal(QueueItemState.Queued, read.Items[0].State);
            Assert.Equal(["house-style", "thorough-review"], read.Items[0].Skills);
            Assert.Null(read.Items[1].Skills);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task Worktree_cleanup_claim_is_exact_path_exclusive_and_receipted()
    {
        var path = TempQueuePath();
        try
        {
            var claim = await QueueStore.TryClaimWorktreeCleanupAsync(
                path, @"C:\repos\worktrees\retired", "github.com/example/repo", "2151-lane", "abc123", Ct);
            var duplicate = await QueueStore.TryClaimWorktreeCleanupAsync(
                path, @"C:\repos\worktrees\retired", "github.com/example/repo", "2151-lane", "abc123", Ct);

            var acquired = Assert.IsType<QueueWorktreeCleanupClaim>(claim);
            Assert.Null(duplicate);
            Assert.True(await QueueStore.HasActiveWorktreeCleanupClaimAsync(path, acquired.Path, Ct));

            await QueueStore.CompleteWorktreeCleanupAsync(path, acquired, "refused", "final-recheck-not-candidate", Ct);
            var read = await QueueStore.LoadAsync(path, Ct);

            Assert.False(await QueueStore.HasActiveWorktreeCleanupClaimAsync(path, acquired.Path, Ct));
            Assert.Single(read.WorktreeCleanupClaims!);
            Assert.Single(read.WorktreeCleanupReceipts!);
            Assert.Equal("final-recheck-not-candidate", read.WorktreeCleanupReceipts![0].ReasonCode);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task Legacy_cleanup_claims_and_receipts_reload_with_explicit_observation_defaults()
    {
        var path = TempQueuePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            await File.WriteAllTextAsync(path, """
                {"items":[],"worktreeCleanupClaims":[{"Id":"claim-1","Path":"C:\\repos\\w1","Repository":"github.com/example/repo","Branch":"2151-lane","Head":"abc","ClaimedAt":"2026-09-17T12:00:00+00:00"}],"worktreeCleanupReceipts":[{"ClaimId":"claim-0","Path":"C:\\repos\\w0","Disposition":"retained","ReasonCode":"git-worktree-remove-failed","CompletedAt":"2026-09-17T11:00:00+00:00"}]}
                """, Ct);

            var snapshot = await QueueStore.LoadAsync(path, Ct);
            var claim = Assert.Single(snapshot.WorktreeCleanupClaims!);
            Assert.Equal(string.Empty, claim.QueueRevision);
            Assert.Equal("candidate", claim.Classification);
            var receipt = Assert.Single(snapshot.WorktreeCleanupReceipts!);
            Assert.Equal(string.Empty, receipt.Repository);
            Assert.Equal(receipt.CompletedAt, receipt.StartedAt);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task A_non_success_receipt_does_not_permanently_fence_a_path()
    {
        var path = TempQueuePath();
        try
        {
            var first = Assert.IsType<QueueWorktreeCleanupClaim>(await QueueStore.TryClaimWorktreeCleanupAsync(
                path, @"C:\repos\worktrees\retired", "github.com/example/repo", "2151-lane", "abc123", Ct));
            await QueueStore.CompleteWorktreeCleanupAsync(path, first, "retained", "git-worktree-remove-failed", Ct);
            var next = await QueueStore.TryClaimWorktreeCleanupAsync(
                path, @"C:\repos\worktrees\retired", "github.com/example/repo", "2151-lane", "abc123", Ct);

            Assert.NotNull(next);
            Assert.NotEqual(first.Id, next.Id);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Cleanup_revision_observes_full_queue_row_provenance()
    {
        var original = Item("owned") with { WorkspaceOrigin = WorkspaceOrigins.IssueProvisioned };
        var changed = original with { Repository = "github.com/example/repo", Branch = "2151-lane" };

        Assert.NotEqual(QueueStore.ComputeRevision([original]), QueueStore.ComputeRevision([changed]));
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
    public async Task An_absent_or_null_legacy_declaration_reloads_and_resaves_as_explicit_unknown()
    {
        var path = TempQueuePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            await File.WriteAllTextAsync(path, """
                {"items":[{"Tag":"absent","Role":"implement","Workspace":"C:\\repos\\w1","SpecFile":"C:\\baton\\queue\\specs\\t.md"},{"Tag":"null","Role":"implement","Workspace":"C:\\repos\\w2","SpecFile":"C:\\baton\\queue\\specs\\t.md","DeclaredTaskSize":null}],"held":false}
                """, Ct);

            var reloaded = await QueueStore.LoadAsync(path, Ct);
            Assert.Equal(2, reloaded.Items.Count);
            Assert.All(reloaded.Items, item => Assert.Equal(DeclaredTaskSize.Unknown, item.DeclaredTaskSize.Size));
            Assert.All(reloaded.Items, item => Assert.Null(item.DeclaredTaskSize.Rationale));

            await QueueStore.MutateAsync(path, snapshot => snapshot, Ct);
            var json = await File.ReadAllTextAsync(path, Ct);
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var persistedItems = document.RootElement.GetProperty("items").EnumerateArray().ToList();
            Assert.All(persistedItems, persisted =>
            {
                var declaration = persisted.GetProperty("DeclaredTaskSize");
                Assert.Equal("unknown", declaration.GetProperty("size").GetString());
                Assert.False(declaration.TryGetProperty("rationale", out _));
            });
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"size\":\"small\"}")]
    [InlineData("{\"size\":\"small\",\"rationale\":\" \"}")]
    [InlineData("{\"size\":1,\"rationale\":\"one cluster\"}")]
    [InlineData("{\"size\":\"small\",\"rationale\":42}")]
    [InlineData("{\"size\":\"unknown\",\"rationale\":\"guessed\"}")]
    [InlineData("{\"size\":\"unknown\",\"rationale\":\" \"}")]
    public async Task A_malformed_current_declaration_is_refused_as_invalid_queue_json(string declarationJson)
    {
        var path = TempQueuePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            var json = """
                {"items":[{"Tag":"malformed","Role":"implement","Workspace":"C:\\repos\\w1","SpecFile":"C:\\baton\\queue\\specs\\t.md","DeclaredTaskSize":DECLARATION}],"held":false}
                """.Replace("DECLARATION", declarationJson, StringComparison.Ordinal);
            await File.WriteAllTextAsync(path, json, Ct);

            await Assert.ThrowsAsync<QueueStoreException>(() => QueueStore.LoadAsync(path, Ct));
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

    [Fact]
    public async Task A_record_runs_on_the_queue_mutex_owner_thread()
    {
        var path = TempQueuePath();
        try
        {
            var mutationThread = 0;
            var recordThread = 0;
            await QueueStore.MutateAndRecordAsync(
                path,
                snapshot =>
                {
                    mutationThread = Environment.CurrentManagedThreadId;
                    return snapshot with { Items = [Item("ordered")] };
                },
                () => recordThread = Environment.CurrentManagedThreadId,
                Ct);

            Assert.NotEqual(0, mutationThread);
            Assert.Equal(mutationThread, recordThread);
        }
        finally
        {
            Cleanup(path);
        }
    }

    private static void Cleanup(string path) => DirectoryCleanup.DeleteRecursively(Path.GetDirectoryName(path)!);
}
