using System.Text.Json;
using Baton.Cli.Daemon;
using Baton.Memory;
using Baton.Status;

namespace Baton.Cli.Tests;

public sealed partial class MemoryAutomaticProjectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Assertion_is_not_written_when_import_fails_before_intent(bool failIntentWrite)
    {
        var root = Path.Combine(ClaudeHome, "projects", "c--assertion", "memory");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "project_fact.md"), "fixture fact");
        if (failIntentWrite)
        {
            Directory.CreateDirectory(BatonPaths.Root);
            File.WriteAllText(Path.Combine(BatonPaths.Root, BatonPaths.MemoryImportsDirectoryName), "fixture denied directory");
        }
        var options = MemoryImportOptionsParser.Parse([
            "--assert", $"{root}={Repository}", "--root", failIntentWrite ? root : Path.Combine(_root, "undiscovered"),
        ]);
        Task<int> Import() => MemoryImportCommand.ExecuteAsync(
            options, TextWriter.Null, ClaudeHome, TestContext.Current.CancellationToken, UserHome);
        if (failIntentWrite)
            await Assert.ThrowsAsync<IOException>(Import);
        else
            await Assert.ThrowsAsync<CliArgumentException>(Import);
        Assert.False(File.Exists(BatonPaths.MemoryAliasFile));
        Assert.False(File.Exists(BatonPaths.MemoryEntriesFile(Slug)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restart_recovers_assertion_only_intent_and_projects_existing_store(bool aliasAlreadyAppended)
    {
        var root = Path.Combine(ClaudeHome, "projects", "c--assertion", "memory");
        Directory.CreateDirectory(root);
        await MemoryStore.AppendAsync([Entry("existing fact")], BatonPaths.MemoryEntriesFile(Slug), TestContext.Current.CancellationToken);
        var path = await SeedAliasIntentAsync(root, aliasAlreadyAppended);
        var diagnostics = new StringWriter();
        await new MemoryProjectionSweep(() => DateTime.UtcNow, ClaudeHome, UserHome, null)
            .SweepOnceAsync(diagnostics, TestContext.Current.CancellationToken);
        Assert.Equal(ImportOperationState.Settled, ImportManifest.Read(path).OperationState);
        Assert.Single(await MemoryAliasStore.ReadAllAsync(BatonPaths.MemoryAliasFile, TestContext.Current.CancellationToken));
        Assert.Contains("existing fact", File.ReadAllText(Path.Combine(root, ClaudeProjectionTarget.ProjectionFileName)), StringComparison.Ordinal);
    }

    private static async Task<string> SeedAliasIntentAsync(string root, bool append)
    {
        var operation = Guid.NewGuid().ToString("N");
        var alias = new MemoryAliasEntry(BatonPaths.RecordKey(root), Repository, "fixture", DateTime.UtcNow,
            ImportOperationId: operation);
        var intent = Intent(operation) with { PlannedAliases = [alias] };
        var path = BatonPaths.MemoryImportManifestFile($"fixture-{operation}");
        intent.Write(path);
        if (append)
            await MemoryAliasStore.AppendAsync([alias], BatonPaths.MemoryAliasFile, TestContext.Current.CancellationToken);
        return path;
    }

    [Theory]
    [InlineData("missing-accounting")]
    [InlineData("missing-plan")]
    [InlineData("duplicate-accounting")]
    [InlineData("duplicate-plan")]
    [InlineData("wrong-repository")]
    [InlineData("missing-link-accounting")]
    [InlineData("duplicate-link-plan")]
    public async Task Partial_intent_is_rejected_before_replay(string corruption)
    {
        var operation = Guid.NewGuid().ToString("N");
        var first = Entry("first") with { ImportOperationId = operation };
        var second = Entry("second", OtherRepository) with { ImportOperationId = operation };
        var link = MemorySupersessionLink.Create(first.Id, second.Id, Repository, DateTime.UtcNow)
        with
        { ImportOperationId = operation };
        var intent = IntentWithLinks(operation, [first, second], [link]);
        intent = corruption switch
        {
            "missing-accounting" => intent with { Entries = [intent.Entries[0]] },
            "missing-plan" => intent with { PlannedEntries = [first] },
            "duplicate-accounting" => intent with { Entries = [.. intent.Entries, intent.Entries[0]] },
            "duplicate-plan" => intent with { PlannedEntries = [first, second, first] },
            "wrong-repository" => intent with
            {
                Entries = [intent.Entries[0] with
                {
                    Repository = OtherRepository,
                    EntriesFilePath = BatonPaths.MemoryEntriesFile(FleetMemory.SlugFor(OtherRepository)),
                }, intent.Entries[1]],
            },
            "missing-link-accounting" => intent with { Links = [] },
            _ => intent with { PlannedLinks = [link, link] },
        };
        var path = BatonPaths.MemoryImportManifestFile($"fixture-{operation}");
        intent.Write(path);
        var before = File.ReadAllBytes(path);
        var diagnostics = new StringWriter();
        await new MemoryProjectionSweep(() => DateTime.UtcNow, ClaudeHome, UserHome, null)
            .SweepOnceAsync(diagnostics, TestContext.Current.CancellationToken);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(File.Exists(BatonPaths.MemoryEntriesFile(Slug)));
        Assert.Contains("corrupt", diagnostics.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.True(MemoryImportOperationStore.BlocksProjection(Slug));
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("partial")]
    [InlineData("settled-unaccounted")]
    [InlineData("orphan-link")]
    [InlineData("duplicate-record")]
    public async Task Audit_and_publication_expose_incomplete_operation_state(string state)
    {
        var targetRoot = await CreateTargetAsync();
        var operation = Guid.NewGuid().ToString("N");
        var first = Entry("first") with { ImportOperationId = operation };
        var second = Entry("second", OtherRepository) with { ImportOperationId = operation };
        var path = BatonPaths.MemoryImportManifestFile($"fixture-{operation}");
        var intent = Intent(operation, first, second);
        intent.Write(path);
        await MemoryStoreMetadataStore.EnsureAsync(Repository, Slug, TestContext.Current.CancellationToken);
        await MemoryStore.AppendAsync([first], BatonPaths.MemoryEntriesFile(Slug), TestContext.Current.CancellationToken);
        await MemoryStoreMetadataStore.CompleteInitializationAsync(Repository, Slug, TestContext.Current.CancellationToken);
        if (state == "missing") File.Delete(path);
        if (state == "corrupt") File.WriteAllText(path, "{torn");
        if (state == "partial") (intent with { Entries = [intent.Entries[0]] }).Write(path);
        if (state == "duplicate-record") intent.Write(path + ".duplicate.json");
        if (state == "settled-unaccounted")
        {
            (intent with { OperationState = ImportOperationState.Settled, PlannedEntries = null, Entries = [] }).Write(path);
        }
        if (state == "orphan-link")
        {
            var link = MemorySupersessionLink.Create(first.Id, second.Id, Repository, DateTime.UtcNow)
            with
            { ImportOperationId = "lost-link-operation" };
            await MemoryStore.AppendLinksAsync([link], BatonPaths.MemoryLinksFile(Slug), TestContext.Current.CancellationToken);
            (intent with
            {
                OperationState = ImportOperationState.Settled,
                PlannedEntries = null,
                Entries = [intent.Entries[0] with { AlreadyPresent = false }],
            }).Write(path);
        }

        var audit = new StringWriter();
        await MemoryAuditCommand.ExecuteAsync(MemoryAuditOptionsParser.Parse(["--repository", Repository]),
            audit, ClaudeHome, TestContext.Current.CancellationToken, UserHome, BatonPaths.Root);
        var expected = state is "missing" or "orphan-link" ? "orphan" : state == "pending" ? "pending" : "corrupt";
        Assert.Contains(expected, audit.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("entries=1", audit.ToString(), StringComparison.Ordinal);
        var jsonOutput = new StringWriter();
        await MemoryAuditCommand.ExecuteAsync(MemoryAuditOptionsParser.Parse(["--repository", Repository, "--format", "json"]),
            jsonOutput, ClaudeHome, TestContext.Current.CancellationToken, UserHome, BatonPaths.Root);
        using var json = JsonDocument.Parse(jsonOutput.ToString());
        Assert.Contains(json.RootElement.GetProperty("importOperations").EnumerateArray(),
            problem => problem.GetProperty("state").GetString() == expected);
        Assert.All(json.RootElement.GetProperty("canonicalStores").EnumerateArray(),
            store => Assert.False(store.TryGetProperty("entryCount", out _)));
        Assert.True(MemoryImportOperationStore.BlocksProjection(Slug));
        Assert.Equal(1, await MemorySyncCommand.ExecuteAsync(
            MemorySyncOptionsParser.Parse(["--repository", Repository, "--apply"]), TextWriter.Null,
            ClaudeHome, TestContext.Current.CancellationToken, UserHome));
        Assert.False(File.Exists(Path.Combine(targetRoot, ClaudeProjectionTarget.ProjectionFileName)));
        if (state != "pending")
        {
            var diagnostics = new StringWriter();
            await new MemoryProjectionSweep(() => DateTime.UtcNow, ClaudeHome, UserHome, null)
                .SweepOnceAsync(diagnostics, TestContext.Current.CancellationToken);
            Assert.Contains(expected, diagnostics.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(targetRoot, ClaudeProjectionTarget.ProjectionFileName)));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Restarted_daemon_recovers_disk_only_plan_at_every_boundary(int boundary)
    {
        // Seed persisted bytes directly: ApplyAsync and BoundaryObserver have never seen this plan.
        // The newly constructed daemon is handed only fixture homes, exactly like service startup.
        var path = await SeedInterruptedImportAsync(boundary);
        var diagnostics = new StringWriter();
        await new MemoryProjectionSweep(() => DateTime.UtcNow, ClaudeHome, UserHome, null)
            .SweepOnceAsync(diagnostics, TestContext.Current.CancellationToken);
        var settled = ImportManifest.Read(path);
        Assert.Equal(ImportOperationState.Settled, settled.OperationState);
        Assert.Equal(3, settled.Appended.Count());
        Assert.Single(settled.AppendedLinks);
        Assert.False(MemoryImportOperationStore.BlocksProjection(Slug));
        Assert.Equal(0, await MemoryImportCommand.ExecuteAsync(
            MemoryImportOptionsParser.Parse(["--undo", path]), TextWriter.Null,
            ClaudeHome, TestContext.Current.CancellationToken, UserHome));
        Assert.Empty(await MemoryStore.ReadAllAsync(BatonPaths.MemoryEntriesFile(Slug), TestContext.Current.CancellationToken));
        Assert.Empty(await MemoryStore.ReadAllAsync(BatonPaths.MemoryEntriesFile(FleetMemory.SlugFor(OtherRepository)), TestContext.Current.CancellationToken));
        Assert.Empty(await MemoryStore.ReadLinksAsync(BatonPaths.MemoryLinksFile(Slug), TestContext.Current.CancellationToken));
    }

    private static async Task<string> SeedInterruptedImportAsync(int boundary)
    {
        var operation = Guid.NewGuid().ToString("N");
        var first = Entry("live") with { ImportOperationId = operation };
        var archive = Entry("archive") with { ImportOperationId = operation };
        var other = Entry("other", OtherRepository) with { ImportOperationId = operation };
        var link = MemorySupersessionLink.Create(first.Id, archive.Id, Repository, DateTime.UtcNow)
        with
        { ImportOperationId = operation };
        var intent = IntentWithLinks(operation, [first, archive, other, first], [link, link]);
        var path = BatonPaths.MemoryImportManifestFile($"fixture-{operation}");
        intent.Write(path);
        if (boundary >= 1)
        {
            await MemoryStoreMetadataStore.EnsureAsync(Repository, Slug, TestContext.Current.CancellationToken);
            await MemoryStore.AppendAsync([first, archive], BatonPaths.MemoryEntriesFile(Slug), TestContext.Current.CancellationToken);
        }
        if (boundary >= 2)
        {
            var slug = FleetMemory.SlugFor(OtherRepository);
            await MemoryStoreMetadataStore.EnsureAsync(OtherRepository, slug, TestContext.Current.CancellationToken);
            await MemoryStore.AppendAsync([other], BatonPaths.MemoryEntriesFile(slug), TestContext.Current.CancellationToken);
        }
        if (boundary >= 3)
            await MemoryStore.AppendLinksAsync([link], BatonPaths.MemoryLinksFile(Slug), TestContext.Current.CancellationToken);
        if (boundary >= 4)
        {
            await MemoryStoreMetadataStore.CompleteInitializationAsync(Repository, Slug, TestContext.Current.CancellationToken);
            await MemoryStoreMetadataStore.CompleteInitializationAsync(OtherRepository, FleetMemory.SlugFor(OtherRepository), TestContext.Current.CancellationToken);
        }
        return path;
    }

    [Fact]
    public async Task Undo_preserves_unrelated_malformed_bytes_in_both_ledgers()
    {
        var operation = Guid.NewGuid().ToString("N");
        var entry = Entry("owned") with { ImportOperationId = operation };
        var link = MemorySupersessionLink.Create(entry.Id, "archive", Repository, DateTime.UtcNow)
        with
        { ImportOperationId = operation };
        var entries = BatonPaths.MemoryEntriesFile(Slug);
        var links = BatonPaths.MemoryLinksFile(Slug);
        await MemoryStore.AppendAsync([entry], entries, TestContext.Current.CancellationToken);
        await MemoryStore.AppendLinksAsync([link], links, TestContext.Current.CancellationToken);
        byte[] evidence = [123, 34, 116, 111, 114, 110, 255, 13, 10];
        foreach (var file in new[] { entries, links })
        {
            using var stream = new FileStream(file, FileMode.Append);
            stream.Write(evidence);
        }
        var survivor = Entry("survivor");
        var survivingLink = MemorySupersessionLink.Create("unrelated", "older", Repository, DateTime.UtcNow);
        await MemoryStore.AppendAsync([survivor], entries, TestContext.Current.CancellationToken);
        await MemoryStore.AppendLinksAsync([survivingLink], links, TestContext.Current.CancellationToken);
        var expectedEntries = File.ReadAllBytes(entries).AsSpan(File.ReadAllBytes(entries).AsSpan().IndexOf((byte)'\n') + 1).ToArray();
        var expectedLinks = File.ReadAllBytes(links).AsSpan(File.ReadAllBytes(links).AsSpan().IndexOf((byte)'\n') + 1).ToArray();
        Assert.Equal(1, await MemoryStore.RemoveOwnedAsync([entry.Id], operation, entries, TestContext.Current.CancellationToken));
        Assert.Equal(1, await MemoryStore.RemoveOwnedLinksAsync([link.Id], operation, links, TestContext.Current.CancellationToken));
        Assert.Equal(expectedEntries, File.ReadAllBytes(entries));
        Assert.Equal(expectedLinks, File.ReadAllBytes(links));
    }
}
