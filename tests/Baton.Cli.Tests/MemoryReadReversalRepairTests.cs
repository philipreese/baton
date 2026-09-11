using System.Text.Json;
using Baton.Cli.Daemon;
using Baton.Memory;
using Baton.Status;

namespace Baton.Cli.Tests;

public sealed partial class MemoryAutomaticProjectionTests
{
    [Theory]
    [InlineData("identity")]
    [InlineData("entries")]
    [InlineData("links")]
    [InlineData("retractions")]
    [InlineData("fleet-entries")]
    [InlineData("fleet-links")]
    [InlineData("fleet-retractions")]
    [InlineData("aliases")]
    public async Task Projection_retains_failure_of_each_canonical_input_after_reads_recover(string input)
    {
        var root = await CreateTargetAsync();
        await SeedProjectionInputsAsync();
        var target = Path.Combine(root, ClaudeProjectionTarget.ProjectionFileName);
        File.WriteAllText(target, "published sentinel");
        var armed = false;
        var failures = 0;
        var path = input switch
        {
            "aliases" => BatonPaths.MemoryAliasFile,
            "links" => BatonPaths.MemoryLinksFile(Slug),
            "retractions" => BatonPaths.MemoryRetractionsFile(Slug),
            "fleet-entries" => FleetMemory.EntriesFile,
            "fleet-links" => FleetMemory.LinksFile,
            "fleet-retractions" => FleetMemory.RetractionsFile,
            _ => BatonPaths.MemoryEntriesFile(Slug),
        };
        IDisposable? DenyOnce(string candidate)
        {
            if (!armed || !BatonPaths.RecordKeyComparer.Equals(candidate, path)) return null;
            armed = false;
            failures++;
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        }
        MemorySyncCommand.CanonicalReadObserver = boundary =>
        {
            if (boundary == (input == "identity" ? "identity" : input == "aliases" ? "discovery" : "snapshot"))
                armed = failures == 0;
        };
        JsonLinesLedger<MemoryEntry>.ReadScopeOverride = DenyOnce;
        JsonLinesLedger<MemorySupersessionLink>.ReadScopeOverride = DenyOnce;
        JsonLinesLedger<MemoryRetraction>.ReadScopeOverride = DenyOnce;
        JsonLinesLedger<MemoryAliasEntry>.ReadScopeOverride = DenyOnce;
        var output = new StringWriter();
        try
        {
            Assert.Equal(1, await MemorySyncCommand.ExecuteAsync(
                MemorySyncOptionsParser.Parse(["--repository", Repository, "--apply"]), output,
                ClaudeHome, TestContext.Current.CancellationToken, UserHome));
        }
        finally
        {
            ResetReadControls();
        }
        Assert.Equal(1, failures);
        Assert.Equal("published sentinel", File.ReadAllText(target));
        Assert.Contains("IOException", output.ToString(), StringComparison.Ordinal);
        var pending = await MemoryProjectionObligationStore.ReadAsync(Slug, TestContext.Current.CancellationToken);
        Assert.NotNull(pending);
        Assert.True(pending.FailedAttempts > 0);
        Assert.Empty(MemoryImportOperationHealth.Scan(BatonPaths.Root));

        // All inputs are healthy again. A fresh sweep must finish the retained obligation.
        await new MemoryProjectionSweep(() => DateTime.UtcNow.AddDays(1), ClaudeHome, UserHome, null)
            .SweepOnceAsync(TextWriter.Null, TestContext.Current.CancellationToken);
        Assert.Null(await MemoryProjectionObligationStore.ReadAsync(Slug, TestContext.Current.CancellationToken));
        Assert.Contains("repository visible", File.ReadAllText(target), StringComparison.Ordinal);
        Assert.Contains("fleet visible", File.ReadAllText(target), StringComparison.Ordinal);
        Assert.DoesNotContain("retracted secret", File.ReadAllText(target), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Automatic_projection_does_not_report_finished_after_a_transient_retraction_read_failure()
    {
        var root = await CreateTargetAsync();
        await SeedProjectionInputsAsync();
        var target = Path.Combine(root, ClaudeProjectionTarget.ProjectionFileName);
        File.WriteAllText(target, "published sentinel");
        var armed = false;
        MemorySyncCommand.CanonicalReadObserver = boundary => armed = boundary == "snapshot";
        JsonLinesLedger<MemoryRetraction>.ReadScopeOverride = path =>
        {
            if (!armed) return null;
            armed = false;
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        };
        var output = new StringWriter();
        try
        {
            await MemoryProjectionTrigger.ProjectAfterCanonicalWriteAsync(
                Repository, output, ClaudeHome, UserHome, cancellationToken: TestContext.Current.CancellationToken);
        }
        finally { ResetReadControls(); }
        Assert.Contains("PROJECTION IS PENDING", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("PROJECTION FINISHED", output.ToString(), StringComparison.Ordinal);
        Assert.Equal("published sentinel", File.ReadAllText(target));
    }

    [Theory]
    [InlineData("json", true)]
    [InlineData("text", true)]
    [InlineData("json", false)]
    [InlineData("text", false)]
    public async Task Audit_count_read_failure_remains_unavailable_after_healthy_final_sample(string format, bool deny)
    {
        await MemoryStoreMetadataStore.EnsureAsync(Repository, Slug, TestContext.Current.CancellationToken);
        await MemoryStore.AppendAsync([Entry("counted fact")], BatonPaths.MemoryEntriesFile(Slug), TestContext.Current.CancellationToken);
        await MemoryStoreMetadataStore.CompleteInitializationAsync(Repository, Slug, TestContext.Current.CancellationToken);
        var armed = false;
        var reads = 0;
        MemoryAuditCommand.CountObserver = () => armed = true;
        JsonLinesLedger<MemoryEntry>.ReadScopeOverride = path =>
        {
            if (!armed || path != BatonPaths.MemoryEntriesFile(Slug)) return null;
            armed = false;
            reads++;
            return deny ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None) : null;
        };
        var output = new StringWriter();
        try
        {
            Assert.Equal(0, await MemoryAuditCommand.ExecuteAsync(
                MemoryAuditOptionsParser.Parse(["--repository", Repository, "--format", format]), output,
                ClaudeHome, TestContext.Current.CancellationToken, UserHome, BatonPaths.Root));
        }
        finally { ResetReadControls(); }
        Assert.Equal(1, reads);
        Assert.Empty(MemoryImportOperationHealth.Scan(BatonPaths.Root));
        if (format == "json")
        {
            using var json = JsonDocument.Parse(output.ToString());
            var store = Assert.Single(json.RootElement.GetProperty("canonicalStores").EnumerateArray());
            Assert.Equal(!deny, store.TryGetProperty("entryCount", out var count));
            if (deny) Assert.Contains("IOException", store.GetProperty("countError").GetString(), StringComparison.Ordinal);
            else Assert.Equal(1, count.GetInt32());
            Assert.Empty(json.RootElement.GetProperty("importOperations").EnumerateArray());
        }
        else
        {
            Assert.Contains(deny ? "entries=(unavailable" : "entries=1", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("entries=0", output.ToString(), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Paused_replay_and_undo_share_operation_exclusion(bool undoFirst)
    {
        await CreateTargetAsync();
        var operation = Guid.NewGuid().ToString("N");
        var entry = Entry("must stay undone") with { ImportOperationId = operation };
        var intent = Intent(operation, entry);
        var path = BatonPaths.MemoryImportManifestFile(operation);
        intent.Write(path);
        if (undoFirst) await MemoryImportOperationStore.ApplyAsync(path, intent, TestContext.Current.CancellationToken);

        using var paused = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var contenderStarted = new ManualResetEventSlim();
        MemoryImportOperationStore.BoundaryObserver = boundary =>
        {
            if (boundary != (undoFirst ? "reversal-intent" : "aliases")) return;
            paused.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        };
        Task<int> Undo() => MemoryImportCommand.ExecuteAsync(
            MemoryImportOptionsParser.Parse(["--undo", path]), TextWriter.Null,
            ClaudeHome, TestContext.Current.CancellationToken, UserHome);
        Task first = undoFirst ? Undo() : MemoryImportOperationStore.ApplyAsync(path, intent, TestContext.Current.CancellationToken);
        MemoryImportOperationStore.BoundaryObserver = null;
        Task? contender = null;
        Task<int>? undo = undoFirst ? (Task<int>)first : null;
        try
        {
            Assert.True(paused.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
            // Probe the real OS mutex without a timing-based assertion about an unfinished task.
            Assert.Throws<IOException>(() => MutexGuardedFileLock.RunUnderLock(
                Path.Combine(BatonPaths.Root, BatonPaths.MemoryImportsDirectoryName, operation),
                "baton-memory-operation", TimeSpan.Zero, () => { }));
            MemoryImportOperationStore.BoundaryObserver = boundary =>
            {
                if (boundary == "before-operation-lock") contenderStarted.Set();
            };
            if (undoFirst)
                contender = MemoryImportOperationStore.ApplyAsync(path, intent, TestContext.Current.CancellationToken);
            else
                contender = undo = Undo();
            Assert.True(contenderStarted.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        }
        finally
        {
            MemoryImportOperationStore.BoundaryObserver = null;
            release.Set();
            await first;
            if (contender is not null) await contender;
        }
        Assert.NotNull(contender);
        Assert.Equal(0, await undo!);
        Assert.Equal(ImportOperationState.Reversed, ImportManifest.Read(path).OperationState);
        Assert.Empty(await MemoryStore.ReadAllStrictAsync(BatonPaths.MemoryEntriesFile(Slug), TestContext.Current.CancellationToken));
        Assert.Empty(MemoryImportOperationHealth.Scan(BatonPaths.Root));

        // The caller's retained Intent cannot override the reversal read back from disk.
        var generation = MemoryCanonicalGeneration.Capture(BatonPaths.Root);
        Assert.Equal(ImportOperationState.Reversed,
            (await MemoryImportOperationStore.ApplyAsync(path, intent, TestContext.Current.CancellationToken)).OperationState);
        Assert.Equal(generation, MemoryCanonicalGeneration.Capture(BatonPaths.Root));
        await new MemoryProjectionSweep(() => DateTime.UtcNow.AddDays(1), ClaudeHome, UserHome, null)
            .SweepOnceAsync(TextWriter.Null, TestContext.Current.CancellationToken);
        Assert.Empty(await MemoryStore.ReadAllStrictAsync(BatonPaths.MemoryEntriesFile(Slug), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Replay_paused_before_exclusion_rereads_durable_reversal_after_undo()
    {
        var operation = Guid.NewGuid().ToString("N");
        var entry = Entry("stale plan") with { ImportOperationId = operation };
        var intent = Intent(operation, entry);
        var path = BatonPaths.MemoryImportManifestFile(operation);
        intent.Write(path);
        using var paused = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        MemoryImportOperationStore.BoundaryObserver = boundary =>
        {
            if (boundary != "before-operation-lock") return;
            paused.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        };
        var stale = MemoryImportOperationStore.ApplyAsync(path, intent, TestContext.Current.CancellationToken);
        MemoryImportOperationStore.BoundaryObserver = null;
        try
        {
            Assert.True(paused.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
            await MemoryImportOperationStore.ApplyAsync(path, intent, TestContext.Current.CancellationToken);
            Assert.Equal(0, await MemoryImportCommand.ExecuteAsync(
                MemoryImportOptionsParser.Parse(["--undo", path]), TextWriter.Null,
                ClaudeHome, TestContext.Current.CancellationToken, UserHome));
        }
        finally { release.Set(); }
        Assert.Equal(ImportOperationState.Reversed, (await stale).OperationState);
        Assert.Empty(await MemoryStore.ReadAllStrictAsync(BatonPaths.MemoryEntriesFile(Slug), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("reversal-intent")]
    [InlineData("entries")]
    [InlineData("links")]
    public async Task Restart_continues_durable_reversal_without_replaying_removed_rows(string stop)
    {
        var root = await CreateTargetAsync();
        var operation = Guid.NewGuid().ToString("N");
        var first = Entry("reversed first") with { ImportOperationId = operation };
        var second = Entry("reversed second") with { ImportOperationId = operation };
        var foreign = Entry("foreign survivor") with { ImportOperationId = null };
        var link = MemorySupersessionLink.Create(first.Id, second.Id, Repository, DateTime.UtcNow)
            with
        { ImportOperationId = operation };
        var intent = IntentWithLinks(operation, [first, second], [link]);
        var path = BatonPaths.MemoryImportManifestFile(operation);
        intent.Write(path);
        await MemoryImportOperationStore.ApplyAsync(path, intent, TestContext.Current.CancellationToken);
        await MemoryStore.AppendAsync([foreign], BatonPaths.MemoryEntriesFile(Slug), TestContext.Current.CancellationToken);
        MemoryImportOperationStore.BoundaryObserver = boundary =>
        {
            if (boundary == (stop == "entries" ? $"reversed-entries:{Slug}" : stop == "links" ? $"reversed-links:{Slug}" : stop))
                throw new IOException("fixture interruption during reversal");
        };
        try
        {
            await Assert.ThrowsAsync<IOException>(() => MemoryImportOperationStore.ReverseAsync(path, TestContext.Current.CancellationToken));
        }
        finally { MemoryImportOperationStore.BoundaryObserver = null; }
        Assert.Equal(ImportOperationState.Reversing, ImportManifest.Read(path).OperationState);
        Assert.True(MemoryImportOperationStore.BlocksProjection(Slug));
        Assert.Equal(ImportOperationState.Reversing,
            (await MemoryImportOperationStore.ApplyAsync(path, intent, TestContext.Current.CancellationToken)).OperationState);
        await new MemoryProjectionSweep(() => DateTime.UtcNow.AddDays(1), ClaudeHome, UserHome, null)
            .SweepOnceAsync(TextWriter.Null, TestContext.Current.CancellationToken);
        Assert.Equal(ImportOperationState.Reversed, ImportManifest.Read(path).OperationState);
        Assert.Equal(foreign, Assert.Single(await MemoryStore.ReadAllStrictAsync(
            BatonPaths.MemoryEntriesFile(Slug), TestContext.Current.CancellationToken)));
        Assert.Empty(await MemoryStore.ReadLinksStrictAsync(BatonPaths.MemoryLinksFile(Slug), TestContext.Current.CancellationToken));
        Assert.Empty(MemoryImportOperationHealth.Scan(BatonPaths.Root));
        var projection = File.ReadAllText(Path.Combine(root, ClaudeProjectionTarget.ProjectionFileName));
        Assert.Contains("foreign survivor", projection, StringComparison.Ordinal);
        Assert.DoesNotContain("reversed first", projection, StringComparison.Ordinal);

        // A reversed operation can no longer legitimize resurrected rows, even after restart.
        await MemoryStore.AppendAsync([first], BatonPaths.MemoryEntriesFile(Slug), TestContext.Current.CancellationToken);
        Assert.Contains(MemoryImportOperationHealth.Scan(BatonPaths.Root), p => p.State == "corrupt");
    }

    private static async Task SeedProjectionInputsAsync()
    {
        foreach (var repository in new[] { Repository, FleetMemory.Slug })
        {
            var slug = FleetMemory.SlugFor(repository);
            var visible = Entry(repository == Repository ? "repository visible" : "fleet visible", repository);
            var retracted = Entry("retracted secret", repository);
            await MemoryStoreMetadataStore.EnsureAsync(repository, slug, TestContext.Current.CancellationToken);
            await MemoryStore.AppendAsync([visible, retracted], BatonPaths.MemoryEntriesFile(slug), TestContext.Current.CancellationToken);
            await MemoryStoreMetadataStore.CompleteInitializationAsync(repository, slug, TestContext.Current.CancellationToken);
            await MemoryStore.AppendLinksAsync([MemorySupersessionLink.Create(visible.Id, retracted.Id, repository, DateTime.UtcNow)],
                BatonPaths.MemoryLinksFile(slug), TestContext.Current.CancellationToken);
            await MemoryStore.AppendRetractionsAsync([MemoryRetraction.Create(retracted.Id, repository, "fixture", "test", DateTime.UtcNow)],
                BatonPaths.MemoryRetractionsFile(slug), TestContext.Current.CancellationToken);
        }
    }

    private static void ResetReadControls()
    {
        MemorySyncCommand.CanonicalReadObserver = null;
        MemoryAuditCommand.CountObserver = null;
        JsonLinesLedger<MemoryEntry>.ReadScopeOverride = null;
        JsonLinesLedger<MemorySupersessionLink>.ReadScopeOverride = null;
        JsonLinesLedger<MemoryRetraction>.ReadScopeOverride = null;
        JsonLinesLedger<MemoryAliasEntry>.ReadScopeOverride = null;
    }
}
