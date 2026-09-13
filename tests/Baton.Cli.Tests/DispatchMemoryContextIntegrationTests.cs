using System.Security.Cryptography;
using System.Text;
using Baton.Cli.Tests.TestSupport;
using Baton.Memory;
using Baton.Runway;
using Baton.Status;
using Baton.Vendors;

namespace Baton.Cli.Tests;

[Collection(SerializedEnvironmentCollection.Name)]
public sealed class DispatchMemoryContextIntegrationTests : IDisposable
{
    private const string Repository = "example/repository";
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter> { ["fake"] = new ContractOutputWorkerAdapter(satisfyOutputs: true) };
    private readonly IsolatedBatonHome _batonHome = new();
    private readonly IDisposable _catalogScope;

    public DispatchMemoryContextIntegrationTests()
    {
        _catalogScope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Current with
        {
            WorkerRolesPathOverride = Path.Combine(AppContext.BaseDirectory, "WorkerRoles.json"),
            WorkerTiersPathOverride = Path.Combine(AppContext.BaseDirectory, "WorkerTiers.json"),
            WorkflowTemplatesPathOverride = Path.Combine(AppContext.BaseDirectory, "WorkflowTemplates.json"),
        });
    }

    public void Dispose()
    {
        _catalogScope.Dispose();
        _batonHome.Dispose();
    }

    [Fact]
    public async Task Memory_context_dispatch_materializes_and_writes_exactly_one_reserved_attachment()
    {
        var root = CreateRoot();
        try
        {
            await AppendCanonicalEntryAsync();
            var options = await BuildOptionsAsync(root, memoryContextRepository: Repository);

            await DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken, evaluateRunway: Admit);

            var attachments = Path.Combine(options.RoomDirectoryPath, "artifacts", "attachments");
            var attachment = Path.Combine(attachments, RoleSpecMaterializer.MemoryContextIndexFileName);
            Assert.Equal([RoleSpecMaterializer.MemoryContextIndexFileName], Directory.GetFiles(attachments).Select(Path.GetFileName));
            var text = await File.ReadAllTextAsync(attachment, TestContext.Current.CancellationToken);
            Assert.Contains(MemoryContextIndex.FormatMarker, text, StringComparison.Ordinal);
            Assert.Contains("entries=1", text, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public async Task Reserved_user_attachment_collision_refuses_before_room_mutation()
    {
        var root = CreateRoot();
        try
        {
            var attachment = Path.Combine(root, RoleSpecMaterializer.MemoryContextIndexFileName);
            await File.WriteAllTextAsync(attachment, "operator input", TestContext.Current.CancellationToken);
            var options = (await BuildOptionsAsync(root, memoryContextRepository: Repository)) with { Attachments = [attachment] };

            var refusal = await Assert.ThrowsAsync<CliArgumentException>(() => DispatchCommand.ExecuteAsync(
                options, Adapters, TestContext.Current.CancellationToken, evaluateRunway: Admit));

            Assert.Contains("Baton-reserved attachment name", refusal.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(options.RoomDirectoryPath));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public async Task Malformed_canonical_memory_refuses_before_the_worker_or_room_starts()
    {
        var root = CreateRoot();
        try
        {
            var entriesPath = BatonPaths.MemoryEntriesFile(FleetMemory.SlugFor(Repository));
            Directory.CreateDirectory(Path.GetDirectoryName(entriesPath)!);
            await File.WriteAllTextAsync(entriesPath, "not json", TestContext.Current.CancellationToken);
            var options = await BuildOptionsAsync(root, memoryContextRepository: Repository);

            var refusal = await Assert.ThrowsAsync<CliArgumentException>(() => DispatchCommand.ExecuteAsync(
                options, Adapters, TestContext.Current.CancellationToken, evaluateRunway: Admit));

            Assert.Contains("Could not read canonical memory", refusal.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(options.RoomDirectoryPath));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public async Task Null_memory_context_keeps_the_ordinary_dispatch_attachment_path_unchanged()
    {
        var root = CreateRoot();
        try
        {
            var options = await BuildOptionsAsync(root, memoryContextRepository: null);

            await DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken, evaluateRunway: Admit);

            Assert.False(Directory.Exists(Path.Combine(options.RoomDirectoryPath, "artifacts", "attachments")));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    private static RunwayDecision Admit(string vendor) => new(vendor, RunwayDisposition.Admit, null, []);

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "dispatch-memory-context-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task<DispatchOptions> BuildOptionsAsync(string root, string? memoryContextRepository)
    {
        var spec = Path.Combine(root, "spec.md");
        await File.WriteAllTextAsync(spec, "Advise on the next step.", TestContext.Current.CancellationToken);
        return new DispatchOptions("advise", spec, Path.Combine(root, "room"), Adapter: "fake", MemoryContextRepository: memoryContextRepository);
    }

    private static async Task AppendCanonicalEntryAsync()
    {
        const string text = "canonical body which must not reach the generated index";
        const string source = "C:/fixture/canonical.md";
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        var entry = new MemoryEntry(MemoryEntry.Derive(Repository, source, digest), Repository,
            MemoryKind.DurableFact, MemoryKindSource.Declared, text, digest, source, "fixture", VendorMemoryScope.Vendor,
            default, default);
        await MemoryStore.AppendAsync([entry], BatonPaths.MemoryEntriesFile(FleetMemory.SlugFor(Repository)), TestContext.Current.CancellationToken);
    }
}
