using Baton.Queue;
using Baton.Domain;
using Baton.Accounting;
using Baton.Conductor;
using Baton.Status;

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
    public async Task Current_claim_excludes_takeover_through_actual_queue_replacement()
    {
        var path = TempQueuePath();
        var root = Path.GetDirectoryName(path)!;
        var identity = RepositoryIdentity.From("https://github.com/example/claim-persistence", null)!;
        var beforeReplace = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var afterReplace = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var allowReplace = new ManualResetEventSlim();
        using var allowReturn = new ManualResetEventSlim();
        var originalMove = QueueStore.MoveOverwriting;
        Task<QueueSnapshot>? mutation = null;
        Task<(ConductorClaimRecord Record, string DisplacedHolder)>? takeover = null;
        try
        {
            var original = await ConductorClaimStore.ClaimAsync(identity, "holder", root, cancellationToken: Ct);
            await QueueStore.MutateAsync(path, snapshot => snapshot with { Items = [Item("a")] }, Ct);
            QueueStore.MoveOverwriting = (source, destination) =>
            {
                if (destination != path) { originalMove(source, destination); return; }
                beforeReplace.TrySetResult();
                Assert.True(allowReplace.Wait(TimeSpan.FromSeconds(60), Ct));
                originalMove(source, destination);
                afterReplace.TrySetResult();
                Assert.True(allowReturn.Wait(TimeSpan.FromSeconds(60), Ct));
            };
            mutation = QueueStore.MutateWithCurrentClaimAsync(path, identity, root, (snapshot, claim) =>
            {
                Assert.NotNull(claim);
                Assert.Equal(original.Holder, claim.Holder);
                Assert.Equal(ConductorClaimStore.GetClaimGeneration(original), ConductorClaimStore.GetClaimGeneration(claim));
                return snapshot with
                {
                    Items = [snapshot.Items[0] with { LaunchMayHaveBegunAt = DateTimeOffset.UtcNow }],
                };
            }, Ct);
            await beforeReplace.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);
            Assert.DoesNotContain("LaunchMayHaveBegunAt", await File.ReadAllTextAsync(path, Ct), StringComparison.Ordinal);
            var claimPath = Path.Combine(root, identity.FileSlug, BatonPaths.ConductorClaimFileName);
            using var claimMutex = new Mutex(false,
                MutexGuardedFileLock.BuildMutexName(claimPath, ConductorClaimStore.LockNamePrefix));
            AssertClaimExcluded(claimMutex);
            takeover = ConductorClaimStore.TakeoverAsync(identity, "other", "fixture cutover", root, cancellationToken: Ct);
            allowReplace.Set();
            await afterReplace.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);
            Assert.Contains("LaunchMayHaveBegunAt", await File.ReadAllTextAsync(path, Ct), StringComparison.Ordinal);
            AssertClaimExcluded(claimMutex);
            allowReturn.Set();
            await mutation.WaitAsync(TimeSpan.FromSeconds(60), Ct);
            Assert.Equal("other", (await takeover.WaitAsync(TimeSpan.FromSeconds(60), Ct)).Record.Holder);
            Assert.NotNull(Assert.Single((await QueueStore.LoadAsync(path, Ct)).Items).LaunchMayHaveBegunAt);
        }
        finally
        {
            allowReplace.Set();
            allowReturn.Set();
            try
            {
                if (mutation is not null) await mutation.WaitAsync(TimeSpan.FromSeconds(60), Ct);
                if (takeover is not null) await takeover.WaitAsync(TimeSpan.FromSeconds(60), Ct);
            }
            finally
            {
                QueueStore.MoveOverwriting = originalMove;
                Cleanup(path);
            }
        }
    }

    private static void AssertClaimExcluded(Mutex mutex)
    {
        var acquired = mutex.WaitOne(TimeSpan.Zero);
        if (acquired) mutex.ReleaseMutex();
        Assert.False(acquired);
    }

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
    public async Task Explicit_repeat_cap_survives_queue_storage_round_trip()
    {
        var path = TempQueuePath();
        try
        {
            await QueueStore.MutateAsync(path, snapshot => snapshot with
            {
                Items = [Item("capped") with { MaxRepeatedToolSteps = 75 }],
            }, Ct);

            var read = await QueueStore.LoadAsync(path, Ct);
            Assert.Equal(75, Assert.Single(read.Items).MaxRepeatedToolSteps);
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
    public async Task Worktree_cleanup_operation_lease_is_exclusive_and_crash_recoverable()
    {
        var path = TempQueuePath();
        const string workspace = @"C:\repos\worktrees\retired";
        try
        {
            using var first = await QueueStore.TryAcquireWorktreeCleanupOperationAsync(path, workspace, Ct);
            Assert.NotNull(first);
            Assert.Null(await QueueStore.TryAcquireWorktreeCleanupOperationAsync(path, workspace, Ct));

            first.Dispose();
            using var recovered = await QueueStore.TryAcquireWorktreeCleanupOperationAsync(path, workspace, Ct);
            Assert.NotNull(recovered);
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
    public async Task A_held_queue_replacement_preserves_bytes_and_failure_diagnostics()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows share-mode exclusion is required");

        var path = TempQueuePath();
        try
        {
            await QueueStore.MutateAsync(path, snapshot => snapshot with { Items = [Item("original")] }, Ct);
            var originalBytes = await File.ReadAllBytesAsync(path, Ct);

            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var exception = await Assert.ThrowsAsync<QueueStoreException>(() =>
                    QueueStore.MutateAsync(path, snapshot => snapshot with { Items = [Item("replacement")] }, Ct));

                var cause = Assert.IsAssignableFrom<Exception>(exception.InnerException);
                Assert.True(cause is IOException or UnauthorizedAccessException,
                    $"Expected IOException or UnauthorizedAccessException, got {cause.GetType()}");
                Assert.Contains("replace", exception.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Contains(cause.GetType().Name, exception.Message, StringComparison.Ordinal);
                Assert.Contains($"0x{cause.HResult:X8}", exception.Message, StringComparison.OrdinalIgnoreCase);
            }

            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(path, Ct));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
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

    [Fact]
    public async Task Explicit_unknown_with_rationale_round_trips_only_on_an_owned_task()
    {
        var path = TempQueuePath();
        try
        {
            var at = new DateTimeOffset(2026, 9, 30, 18, 0, 0, TimeSpan.Zero);
            var declaration = new TaskSizeDeclaration(DeclaredTaskSize.Unknown, "scope still being measured");
            await QueueStore.MutateAsync(path, snapshot => snapshot with
            {
                Items = [Item("task") with
                {
                    DeclaredTaskSize = declaration,
                    OwnedTask = new OwnedTaskSubmission("task-id", "github.com/example/repo", 48,
                        "digest", "conductor", at),
                }],
            }, Ct);
            var read = Assert.Single((await QueueStore.LoadAsync(path, Ct)).Items);
            Assert.Equal(declaration, read.DeclaredTaskSize);
            Assert.NotNull(read.OwnedTask);
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
