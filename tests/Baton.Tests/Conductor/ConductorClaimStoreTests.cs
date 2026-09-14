using Baton.Accounting;
using Baton.Conductor;
using Baton.Status;
using Baton.Tests.Shared;

namespace Baton.Tests.Conductor;

public sealed class ConductorClaimStoreTests
{
    private static readonly RepositoryIdentity RepoA =
        RepositoryIdentity.From("https://github.com/philipreese/repo-a.git", null)!;

    private static readonly RepositoryIdentity RepoB =
        RepositoryIdentity.From("https://github.com/philipreese/repo-b.git", null)!;

    [Fact]
    public async Task First_claim_succeeds_and_survives_fresh_read()
    {
        var temp = NewTempDir();
        try
        {
            var now = new DateTime(2026, 9, 14, 10, 0, 0, DateTimeKind.Utc);
            var record = await ConductorClaimStore.ClaimAsync(RepoA, "conductor-1", batonRoot: temp, now: now, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal("conductor-1", record.Holder);
            Assert.Equal(RepoA.Value, record.Repository);
            Assert.Equal(RepoA.FileSlug, record.RepositorySlug);
            Assert.Equal(now, record.AcquiredAt);
            Assert.Null(record.Takeover);

            // Fresh read from disk
            var loaded = await ConductorClaimStore.GetClaimAsync(RepoA, batonRoot: temp, cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotNull(loaded);
            Assert.Equal("conductor-1", loaded.Holder);
            Assert.Equal(now, loaded.AcquiredAt);
            Assert.Null(loaded.Takeover);

            var held = await ConductorClaimStore.ListHeldClaimsAsync(batonRoot: temp, cancellationToken: TestContext.Current.CancellationToken);
            var single = Assert.Single(held);
            Assert.Equal(RepoA.Value, single.Repository);
            Assert.Equal("conductor-1", single.Holder);
            Assert.Equal(now, single.AcquiredAt);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(temp);
        }
    }

    [Fact]
    public async Task Same_holder_claiming_same_repository_is_idempotent()
    {
        var temp = NewTempDir();
        try
        {
            var t1 = new DateTime(2026, 9, 14, 10, 0, 0, DateTimeKind.Utc);
            var t2 = new DateTime(2026, 9, 14, 11, 0, 0, DateTimeKind.Utc);

            var first = await ConductorClaimStore.ClaimAsync(RepoA, "conductor-1", batonRoot: temp, now: t1, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(t1, first.AcquiredAt);

            // Same holder claiming again later
            var second = await ConductorClaimStore.ClaimAsync(RepoA, "conductor-1", batonRoot: temp, now: t2, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal("conductor-1", second.Holder);
            Assert.Equal(t1, second.AcquiredAt); // Does NOT invent a second acquisition timestamp
            Assert.Single(second.Transitions ?? []);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(temp);
        }
    }

    [Fact]
    public async Task Different_holder_is_refused_and_diagnostic_names_current_holder_plus_explicit_takeover()
    {
        var temp = NewTempDir();
        try
        {
            await ConductorClaimStore.ClaimAsync(RepoA, "conductor-alpha", batonRoot: temp, cancellationToken: TestContext.Current.CancellationToken);

            var ex = await Assert.ThrowsAsync<ConductorClaimException>(() =>
                ConductorClaimStore.ClaimAsync(RepoA, "conductor-beta", batonRoot: temp, cancellationToken: TestContext.Current.CancellationToken));

            Assert.Contains("conductor-alpha", ex.Message);
            Assert.Contains("baton conductor takeover conductor-beta --reason <text>", ex.Message);
            Assert.NotNull(ex.TryInvocation);
            Assert.Contains("conductor-beta", ex.TryInvocation);

            // Ownership unchanged
            var current = await ConductorClaimStore.GetClaimAsync(RepoA, batonRoot: temp, cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotNull(current);
            Assert.Equal("conductor-alpha", current.Holder);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(temp);
        }
    }

    [Fact]
    public async Task Two_concurrent_claims_for_same_repository_yield_exactly_one_owner()
    {
        var temp = NewTempDir();
        try
        {
            var barrier = new Barrier(2);
            var holder1Task = Task.Run(async () =>
            {
                barrier.SignalAndWait();
                try
                {
                    await ConductorClaimStore.ClaimAsync(RepoA, "conductor-1", batonRoot: temp, cancellationToken: TestContext.Current.CancellationToken);
                    return (Success: true, Holder: "conductor-1", Error: (Exception?)null);
                }
                catch (Exception ex)
                {
                    return (Success: false, Holder: "conductor-1", Error: ex);
                }
            });

            var holder2Task = Task.Run(async () =>
            {
                barrier.SignalAndWait();
                try
                {
                    await ConductorClaimStore.ClaimAsync(RepoA, "conductor-2", batonRoot: temp, cancellationToken: TestContext.Current.CancellationToken);
                    return (Success: true, Holder: "conductor-2", Error: (Exception?)null);
                }
                catch (Exception ex)
                {
                    return (Success: false, Holder: "conductor-2", Error: ex);
                }
            });

            var results = await Task.WhenAll(holder1Task, holder2Task);
            var successes = results.Where(r => r.Success).ToList();
            var failures = results.Where(r => !r.Success).ToList();

            Assert.Single(successes);
            Assert.Single(failures);
            Assert.IsType<ConductorClaimException>(failures[0].Error);

            var winner = successes[0].Holder;
            var current = await ConductorClaimStore.GetClaimAsync(RepoA, batonRoot: temp, cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotNull(current);
            Assert.Equal(winner, current.Holder);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(temp);
        }
    }

    [Fact]
    public async Task Claims_for_two_repositories_both_succeed_concurrently()
    {
        var temp = NewTempDir();
        try
        {
            var barrier = new Barrier(2);
            var claimATask = Task.Run(async () =>
            {
                barrier.SignalAndWait();
                return await ConductorClaimStore.ClaimAsync(RepoA, "conductor-a", batonRoot: temp, cancellationToken: TestContext.Current.CancellationToken);
            });

            var claimBTask = Task.Run(async () =>
            {
                barrier.SignalAndWait();
                return await ConductorClaimStore.ClaimAsync(RepoB, "conductor-b", batonRoot: temp, cancellationToken: TestContext.Current.CancellationToken);
            });

            var results = await Task.WhenAll(claimATask, claimBTask);
            Assert.Equal("conductor-a", results[0].Holder);
            Assert.Equal("conductor-b", results[1].Holder);

            var list = await ConductorClaimStore.ListHeldClaimsAsync(batonRoot: temp, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(2, list.Count);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(temp);
        }
    }

    [Fact]
    public async Task Release_by_wrong_holder_fails_closed_without_changing_ownership()
    {
        var temp = NewTempDir();
        try
        {
            await ConductorClaimStore.ClaimAsync(RepoA, "conductor-1", batonRoot: temp, cancellationToken: TestContext.Current.CancellationToken);

            var ex = await Assert.ThrowsAsync<ConductorClaimException>(() =>
                ConductorClaimStore.ReleaseAsync(RepoA, "conductor-2", "not my repo", batonRoot: temp, cancellationToken: TestContext.Current.CancellationToken));

            Assert.Contains("conductor-1", ex.Message);
            Assert.Contains("conductor-2", ex.Message);

            // Claim remains with conductor-1
            var current = await ConductorClaimStore.GetClaimAsync(RepoA, batonRoot: temp, cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotNull(current);
            Assert.Equal("conductor-1", current.Holder);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(temp);
        }
    }

    [Fact]
    public async Task Takeover_without_nonblank_reason_fails_closed_without_changing_ownership()
    {
        var temp = NewTempDir();
        try
        {
            await ConductorClaimStore.ClaimAsync(RepoA, "conductor-1", batonRoot: temp, cancellationToken: TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<ConductorClaimException>(() =>
                ConductorClaimStore.TakeoverAsync(RepoA, "conductor-2", "   ", batonRoot: temp, cancellationToken: TestContext.Current.CancellationToken));

            await Assert.ThrowsAsync<ConductorClaimException>(() =>
                ConductorClaimStore.TakeoverAsync(RepoA, "conductor-2", "", batonRoot: temp, cancellationToken: TestContext.Current.CancellationToken));

            // Claim remains with conductor-1
            var current = await ConductorClaimStore.GetClaimAsync(RepoA, batonRoot: temp, cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotNull(current);
            Assert.Equal("conductor-1", current.Holder);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(temp);
        }
    }

    [Fact]
    public async Task Explicit_takeover_records_old_holder_new_holder_reason_time_and_reload_projects_new_holder()
    {
        var temp = NewTempDir();
        try
        {
            var t1 = new DateTime(2026, 9, 14, 10, 0, 0, DateTimeKind.Utc);
            var t2 = new DateTime(2026, 9, 14, 10, 30, 0, DateTimeKind.Utc);

            await ConductorClaimStore.ClaimAsync(RepoA, "conductor-1", batonRoot: temp, now: t1, cancellationToken: TestContext.Current.CancellationToken);

            var (record, displaced) = await ConductorClaimStore.TakeoverAsync(
                RepoA, "conductor-2", "session timed out", batonRoot: temp, now: t2, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal("conductor-1", displaced);
            Assert.Equal("conductor-2", record.Holder);
            Assert.Equal(t2, record.AcquiredAt);
            Assert.NotNull(record.Takeover);
            Assert.Equal("conductor-1", record.Takeover.DisplacedHolder);
            Assert.Equal("session timed out", record.Takeover.Reason);
            Assert.Equal(t2, record.Takeover.TakenOverAt);

            // Fresh reload from disk projects the new holder and provenance
            var reloaded = await ConductorClaimStore.GetClaimAsync(RepoA, batonRoot: temp, cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotNull(reloaded);
            Assert.Equal("conductor-2", reloaded.Holder);
            Assert.Equal(t2, reloaded.AcquiredAt);
            Assert.NotNull(reloaded.Takeover);
            Assert.Equal("conductor-1", reloaded.Takeover.DisplacedHolder);
            Assert.Equal("session timed out", reloaded.Takeover.Reason);

            var list = await ConductorClaimStore.ListHeldClaimsAsync(batonRoot: temp, cancellationToken: TestContext.Current.CancellationToken);
            var item = Assert.Single(list);
            Assert.Equal("conductor-2", item.Holder);
            Assert.NotNull(item.Takeover);
            Assert.Equal("conductor-1", item.Takeover.DisplacedHolder);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(temp);
        }
    }

    [Fact]
    public async Task Malformed_durable_record_produces_handled_domain_error_and_preserves_file()
    {
        var temp = NewTempDir();
        try
        {
            var filePath = Path.Combine(temp, RepoA.FileSlug, BatonPaths.ConductorClaimFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            const string malformedContent = "{ not valid json at all ... ";
            await File.WriteAllTextAsync(filePath, malformedContent, TestContext.Current.CancellationToken);

            var exClaim = await Assert.ThrowsAsync<ConductorClaimException>(() =>
                ConductorClaimStore.ClaimAsync(RepoA, "conductor-1", batonRoot: temp, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Contains("not valid JSON", exClaim.Message);

            var exGet = await Assert.ThrowsAsync<ConductorClaimException>(() =>
                ConductorClaimStore.GetClaimAsync(RepoA, batonRoot: temp, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Contains("not valid JSON", exGet.Message);

            var exList = await Assert.ThrowsAsync<ConductorClaimException>(() =>
                ConductorClaimStore.ListHeldClaimsAsync(batonRoot: temp, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Contains("not valid JSON", exList.Message);

            // The file is preserved byte-for-byte!
            Assert.Equal(malformedContent, await File.ReadAllTextAsync(filePath, TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(temp);
        }
    }

    [Theory]
    [InlineData("{\"repository\":\"github.com/philipreese/repo-a\",\"repositorySlug\":\"wrong\",\"holder\":\"conductor-1\",\"acquiredAt\":\"2026-09-14T10:00:00Z\",\"transitions\":[{\"kind\":\"Claim\",\"holder\":\"conductor-1\",\"timestamp\":\"2026-09-14T10:00:00Z\"}]}")]
    [InlineData("{\"repository\":\"github.com/philipreese/repo-a\",\"repositorySlug\":\"github-com-philipreese-repo-a-00000000\",\"holder\":\"\",\"acquiredAt\":\"0001-01-01T00:00:00\",\"transitions\":[]}")]
    [InlineData("{\"repository\":\"github.com/philipreese/repo-a\",\"repositorySlug\":\"github-com-philipreese-repo-a-00000000\",\"holder\":\"conductor-2\",\"acquiredAt\":\"2026-09-14T10:00:00Z\",\"transitions\":[{\"kind\":99,\"holder\":\"conductor-2\",\"timestamp\":\"2026-09-14T10:00:00Z\"}]}")]
    public async Task Parseable_semantic_corruption_fails_closed_and_preserves_file(string corruptContent)
    {
        var temp = NewTempDir();
        try
        {
            var filePath = Path.Combine(temp, RepoA.FileSlug, BatonPaths.ConductorClaimFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            corruptContent = corruptContent.Replace("github-com-philipreese-repo-a-00000000", RepoA.FileSlug, StringComparison.Ordinal);
            await File.WriteAllTextAsync(filePath, corruptContent, TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<ConductorClaimException>(() => ConductorClaimStore.ClaimAsync(RepoA, "conductor-3", batonRoot: temp, cancellationToken: TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<ConductorClaimException>(() => ConductorClaimStore.GetClaimAsync(RepoA, batonRoot: temp, cancellationToken: TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<ConductorClaimException>(() => ConductorClaimStore.ListHeldClaimsAsync(batonRoot: temp, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(corruptContent, await File.ReadAllTextAsync(filePath, TestContext.Current.CancellationToken));
        }
        finally { DirectoryCleanup.DeleteRecursively(temp); }
    }

    [Fact]
    public async Task Directory_at_claim_file_path_fails_closed_for_get_list_and_claim_without_overwriting_state()
    {
        var temp = NewTempDir();
        try
        {
            var filePath = Path.Combine(temp, RepoA.FileSlug, BatonPaths.ConductorClaimFileName);
            // A directory at the file path is the deterministic, account-independent false-negative
            // for an existence probe: File.Exists returns false, while opening it as a file fails.
            // The old probe-first implementation therefore projected this indeterminate state as
            // absent; the read-first implementation must classify it as an access failure.
            Directory.CreateDirectory(filePath);

            var exGet = await Assert.ThrowsAsync<ConductorClaimException>(() =>
                ConductorClaimStore.GetClaimAsync(RepoA, batonRoot: temp, cancellationToken: TestContext.Current.CancellationToken));
            var exList = await Assert.ThrowsAsync<ConductorClaimException>(() =>
                ConductorClaimStore.ListHeldClaimsAsync(batonRoot: temp, cancellationToken: TestContext.Current.CancellationToken));
            var exClaim = await Assert.ThrowsAsync<ConductorClaimException>(() =>
                ConductorClaimStore.ClaimAsync(RepoA, "conductor-2", batonRoot: temp, cancellationToken: TestContext.Current.CancellationToken));

            Assert.Contains("Could not read", exGet.Message);
            Assert.Contains("Could not read", exList.Message);
            Assert.Contains("Could not read", exClaim.Message);
            Assert.True(Directory.Exists(filePath));
            Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(filePath)!, "*.tmp"));
        }
        finally { DirectoryCleanup.DeleteRecursively(temp); }
    }

    [Fact]
    public async Task Failed_atomic_replacement_preserves_the_old_durable_record_and_cleans_the_temp_file()
    {
        var temp = NewTempDir();
        try
        {
            await ConductorClaimStore.ClaimAsync(RepoA, "conductor-1", batonRoot: temp, cancellationToken: TestContext.Current.CancellationToken);
            var filePath = Path.Combine(temp, RepoA.FileSlug, BatonPaths.ConductorClaimFileName);
            var original = await File.ReadAllTextAsync(filePath, TestContext.Current.CancellationToken);
            ConductorClaimStore.MoveOverwriting = (_, _) => throw new IOException("injected replacement failure");
            await Assert.ThrowsAsync<ConductorClaimException>(() => ConductorClaimStore.TakeoverAsync(RepoA, "conductor-2", "operator approved", batonRoot: temp, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(original, await File.ReadAllTextAsync(filePath, TestContext.Current.CancellationToken));
            Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(filePath)!, "*.tmp"));
        }
        finally { ConductorClaimStore.ResetFileOperations(); DirectoryCleanup.DeleteRecursively(temp); }
    }

    [Fact]
    public async Task Failed_temporary_file_cleanup_preserves_the_old_durable_record_and_surfaces_the_write_error()
    {
        var temp = NewTempDir();
        try
        {
            await ConductorClaimStore.ClaimAsync(RepoA, "conductor-1", batonRoot: temp, cancellationToken: TestContext.Current.CancellationToken);
            var filePath = Path.Combine(temp, RepoA.FileSlug, BatonPaths.ConductorClaimFileName);
            var original = await File.ReadAllTextAsync(filePath, TestContext.Current.CancellationToken);
            ConductorClaimStore.MoveOverwriting = (_, _) => throw new IOException("injected replacement failure");
            ConductorClaimStore.DeleteFile = _ => throw new IOException("injected cleanup failure");
            var ex = await Assert.ThrowsAsync<ConductorClaimException>(() => ConductorClaimStore.TakeoverAsync(RepoA, "conductor-2", "operator approved", batonRoot: temp, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Contains("Could not write", ex.Message);
            Assert.Equal(original, await File.ReadAllTextAsync(filePath, TestContext.Current.CancellationToken));
            Assert.Single(Directory.EnumerateFiles(Path.GetDirectoryName(filePath)!, "*.tmp"));
        }
        finally { ConductorClaimStore.ResetFileOperations(); DirectoryCleanup.DeleteRecursively(temp); }
    }

    [Fact]
    public async Task Release_requires_nonblank_reason_and_leaves_unheld_state()
    {
        var temp = NewTempDir();
        try
        {
            await ConductorClaimStore.ClaimAsync(RepoA, "conductor-1", batonRoot: temp, cancellationToken: TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<ConductorClaimException>(() =>
                ConductorClaimStore.ReleaseAsync(RepoA, "conductor-1", "  ", batonRoot: temp, cancellationToken: TestContext.Current.CancellationToken));

            var released = await ConductorClaimStore.ReleaseAsync(RepoA, "conductor-1", "work complete", batonRoot: temp, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Null(released.Holder);
            Assert.Null(released.AcquiredAt);

            var list = await ConductorClaimStore.ListHeldClaimsAsync(batonRoot: temp, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Empty(list);

            // Can be claimed again by another conductor after release
            var newClaim = await ConductorClaimStore.ClaimAsync(RepoA, "conductor-2", batonRoot: temp, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal("conductor-2", newClaim.Holder);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(temp);
        }
    }

    private static string NewTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "baton-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
