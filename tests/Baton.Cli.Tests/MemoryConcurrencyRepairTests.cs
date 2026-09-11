using System.Text.Json;
using Baton.Cli.Daemon;
using Baton.Memory;
using Baton.Status;

namespace Baton.Cli.Tests;

public sealed partial class MemoryAutomaticProjectionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Exact_alias_conflicts_fail_before_planning_dependent_entries(bool dryRun, bool sameBatch)
    {
        var root = Path.Combine(ClaudeHome, "projects", "c--conflict", "memory");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "user_fact.md"), "fixture alias fact");
        var args = new List<string> { "--root", root };
        if (dryRun) args.Add("--dry-run");
        if (sameBatch)
            args.AddRange(["--assert", $"{root}={Repository}"]);
        else
            await MemoryAliasStore.AppendAsync([new(BatonPaths.RecordKey(root), Repository, "test", default)],
                BatonPaths.MemoryAliasFile, TestContext.Current.CancellationToken);
        args.AddRange(["--assert", $"{root.ToUpperInvariant()}={OtherRepository}"]);
        await Assert.ThrowsAsync<CliArgumentException>(() => MemoryImportCommand.ExecuteAsync(
            MemoryImportOptionsParser.Parse(args.ToArray()), TextWriter.Null, ClaudeHome,
            TestContext.Current.CancellationToken, UserHome));
        Assert.False(Directory.Exists(Path.Combine(BatonPaths.Root, BatonPaths.MemoryImportsDirectoryName)));
        Assert.False(File.Exists(BatonPaths.MemoryEntriesFile(Slug)));
        Assert.False(File.Exists(BatonPaths.MemoryEntriesFile(FleetMemory.SlugFor(OtherRepository))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Settled_import_invalidates_an_inflight_snapshot_before_its_projection_trigger(bool fleet)
    {
        var root = await CreateTargetAsync();
        await MemoryStore.AppendAsync([Entry("repository baseline")], BatonPaths.MemoryEntriesFile(Slug), TestContext.Current.CancellationToken);
        await MemoryStore.AppendAsync([Entry("fleet baseline", FleetMemory.Slug)], FleetMemory.EntriesFile, TestContext.Current.CancellationToken);
        var target = Path.Combine(root, ClaudeProjectionTarget.ProjectionFileName);
        File.WriteAllText(target, "last published sentinel");
        var operation = Guid.NewGuid().ToString("N");
        var entry = Entry("newly committed fact", fleet ? FleetMemory.Slug : Repository) with { ImportOperationId = operation };
        var intent = Intent(operation, entry);
        var path = BatonPaths.MemoryImportManifestFile(operation);
        intent.Write(path);

        using var snapshotRead = new ManualResetEventSlim();
        using var releaseSnapshot = new ManualResetEventSlim();
        MemorySyncCommand.SnapshotObserver = () =>
        {
            snapshotRead.Set();
            Assert.True(releaseSnapshot.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        };
        var projection = Task.Run(() => MemorySyncCommand.ExecuteAsync(
            MemorySyncOptionsParser.Parse(["--repository", Repository, "--apply"]), TextWriter.Null,
            ClaudeHome, TestContext.Current.CancellationToken, UserHome), TestContext.Current.CancellationToken);
        MemorySyncCommand.SnapshotObserver = null;
        try
        {
            Assert.True(snapshotRead.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
            MemoryImportOperationStore.BoundaryObserver = boundary =>
            {
                if (boundary == "settled") throw new IOException("fixture stop before projection trigger");
            };
            await Assert.ThrowsAsync<IOException>(() => MemoryImportOperationStore.ApplyAsync(path, intent, TestContext.Current.CancellationToken));
            Assert.Equal(ImportOperationState.Settled, ImportManifest.Read(path).OperationState);
            Assert.Empty(MemoryImportOperationHealth.Scan(BatonPaths.Root));
        }
        finally
        {
            MemoryImportOperationStore.BoundaryObserver = null;
            releaseSnapshot.Set();
        }
        Assert.Equal(1, await projection);
        Assert.Equal("last published sentinel", File.ReadAllText(target));
        Assert.NotNull(await MemoryProjectionObligationStore.ReadAsync(Slug, TestContext.Current.CancellationToken));

        // A fresh sweep receives only fixture homes and reads durable settlement/generation authority.
        await new MemoryProjectionSweep(() => DateTime.UtcNow.AddDays(1), ClaudeHome, UserHome, null)
            .SweepOnceAsync(TextWriter.Null, TestContext.Current.CancellationToken);
        Assert.Contains("newly committed fact", File.ReadAllText(target), StringComparison.Ordinal);
        Assert.Null(await MemoryProjectionObligationStore.ReadAsync(Slug, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_alias_replay_accounts_for_the_accepted_winner_before_dependent_entries(bool conflict)
    {
        var root = Path.Combine(ClaudeHome, "projects", "c--race", "memory");
        Directory.CreateDirectory(root);
        var firstId = Guid.NewGuid().ToString("N");
        var secondId = Guid.NewGuid().ToString("N");
        var winner = new MemoryAliasEntry(BatonPaths.RecordKey(root), Repository, "winner", DateTime.UtcNow,
            ImportOperationId: firstId);
        var first = Intent(firstId) with { PlannedAliases = [winner] };
        var secondEntry = Entry("dependent fact", conflict ? OtherRepository : Repository) with { ImportOperationId = secondId };
        var second = Intent(secondId, secondEntry) with
        {
            PlannedAliases = [winner with { Repository = secondEntry.Repository, ImportOperationId = secondId }],
        };
        var firstPath = BatonPaths.MemoryImportManifestFile(firstId);
        var secondPath = BatonPaths.MemoryImportManifestFile(secondId);
        first.Write(firstPath);
        second.Write(secondPath);

        using var winnerAppended = new ManualResetEventSlim();
        using var releaseWinner = new ManualResetEventSlim();
        MemoryImportOperationStore.BoundaryObserver = boundary =>
        {
            if (boundary != "aliases") return;
            winnerAppended.Set();
            Assert.True(releaseWinner.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        };
        var firstApply = MemoryImportOperationStore.ApplyAsync(firstPath, first, TestContext.Current.CancellationToken);
        MemoryImportOperationStore.BoundaryObserver = null;
        try
        {
            Assert.True(winnerAppended.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
            if (conflict)
            {
                await Assert.ThrowsAsync<BatonMemoryException>(() => MemoryImportOperationStore.ApplyAsync(secondPath, second, TestContext.Current.CancellationToken));
                Assert.Equal(ImportOperationState.Intent, ImportManifest.Read(secondPath).OperationState);
                Assert.False(File.Exists(BatonPaths.MemoryEntriesFile(FleetMemory.SlugFor(OtherRepository))));
            }
            else
            {
                var settled = await MemoryImportOperationStore.ApplyAsync(secondPath, second, TestContext.Current.CancellationToken);
                Assert.Equal(winner, Assert.Single(settled.AcceptedAliases!));
                Assert.Single(settled.Appended);
            }
        }
        finally
        {
            releaseWinner.Set();
        }
        await firstApply;
        Assert.Equal(winner, Assert.Single(await MemoryAliasStore.ReadAllAsync(BatonPaths.MemoryAliasFile, TestContext.Current.CancellationToken)));
        if (conflict)
        {
            await new MemoryProjectionSweep(() => DateTime.UtcNow, ClaudeHome, UserHome, null)
                .SweepOnceAsync(TextWriter.Null, TestContext.Current.CancellationToken);
            Assert.Equal(ImportOperationState.Intent, ImportManifest.Read(secondPath).OperationState);
            Assert.True(MemoryImportOperationStore.BlocksProjection(FleetMemory.SlugFor(OtherRepository)));
            Assert.False(File.Exists(BatonPaths.MemoryEntriesFile(FleetMemory.SlugFor(OtherRepository))));
        }
        else
        {
            Assert.Equal(winner, Assert.Single(ImportManifest.Read(secondPath).AcceptedAliases!));
            Assert.Empty(MemoryImportOperationHealth.Scan(BatonPaths.Root));
            var damaged = ImportManifest.Read(secondPath) with { AcceptedAliases = [] };
            damaged.Write(secondPath);
            Assert.Contains(MemoryImportOperationHealth.Scan(BatonPaths.Root), p => p.State == "corrupt");
        }
    }

    [Theory]
    [InlineData("text", false)]
    [InlineData("json", false)]
    [InlineData("json", true)]
    public async Task Audit_revalidates_counts_when_an_import_starts_after_inventory(string format, bool settle)
    {
        await MemoryStore.AppendAsync([Entry("baseline")], BatonPaths.MemoryEntriesFile(Slug), TestContext.Current.CancellationToken);
        using var inventoryRead = new ManualResetEventSlim();
        using var releaseCount = new ManualResetEventSlim();
        MemoryAuditCommand.CountObserver = () =>
        {
            inventoryRead.Set();
            Assert.True(releaseCount.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        };
        var output = new StringWriter();
        var audit = Task.Run(() => MemoryAuditCommand.ExecuteAsync(
            MemoryAuditOptionsParser.Parse(["--repository", Repository, "--format", format]), output,
            ClaudeHome, TestContext.Current.CancellationToken, UserHome, BatonPaths.Root), TestContext.Current.CancellationToken);
        MemoryAuditCommand.CountObserver = null;
        try
        {
            Assert.True(inventoryRead.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
            var operation = Guid.NewGuid().ToString("N");
            var first = Entry("partial group") with { ImportOperationId = operation };
            var second = Entry("other group", OtherRepository) with { ImportOperationId = operation };
            var intent = Intent(operation, first, second);
            var path = BatonPaths.MemoryImportManifestFile(operation);
            intent.Write(path);
            await MemoryStore.AppendAsync([first], BatonPaths.MemoryEntriesFile(Slug), TestContext.Current.CancellationToken);
            if (settle) await MemoryImportOperationStore.ApplyAsync(path, intent, TestContext.Current.CancellationToken);
        }
        finally
        {
            releaseCount.Set();
        }
        Assert.Equal(0, await audit);
        if (format == "json")
        {
            using var json = JsonDocument.Parse(output.ToString());
            Assert.All(json.RootElement.GetProperty("canonicalStores").EnumerateArray(),
                store => Assert.False(store.TryGetProperty("entryCount", out _)));
            Assert.Equal(!settle, json.RootElement.GetProperty("importOperations").EnumerateArray().Any(p => p.GetProperty("state").GetString() == "pending"));
        }
        else
        {
            Assert.Contains("PENDING", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("entries=2", output.ToString(), StringComparison.Ordinal);
        }
    }
}
