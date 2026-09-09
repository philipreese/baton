using Baton.Accounting;
using Baton.Memory;
using Baton.Status;
using Baton.Tests.Shared;

namespace Baton.Cli.Tests;

/// <summary>
/// The reserved <c>fleet</c> slug (#2112): where it is accepted, where it is refused, and that it
/// names the one directory it claims. The end-to-end halves — add filing under it, sync merging it,
/// audit reporting it — live beside their verbs in <see cref="MemoryAddTests"/>,
/// <see cref="MemoryProjectionTests"/> and <see cref="MemoryAuditCommandTests"/>.
/// </summary>
public sealed class FleetMemoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"baton-2112-{Guid.NewGuid():N}");
    private readonly IDisposable _scope;

    public FleetMemoryTests() =>
        _scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = Path.Combine(_root, "baton-root") });

    public void Dispose()
    {
        _scope.Dispose();
        DirectoryCleanup.DeleteRecursively(_root);
    }

    /// <summary>
    /// The slug IS the word, and the store sits at <c>{Root}/fleet/memory/</c>. The control is a git
    /// identity, whose slug carries a digest — which is what makes the bare word safe to reserve: no
    /// identity can ever slug to it.
    /// </summary>
    [Fact]
    public void The_fleet_slug_is_the_bare_word_and_no_git_identity_can_slug_to_it()
    {
        Assert.Equal("fleet", FleetMemory.SlugFor("fleet"));
        Assert.Equal("fleet", FleetMemory.SlugFor("FLEET"));
        Assert.Equal(
            Path.Combine(BatonPaths.Root, "fleet", "memory", "entries.jsonl"),
            FleetMemory.EntriesFile);

        var repositorySlug = FleetMemory.SlugFor("github.com/owner/fleet");
        Assert.Equal(RepositoryIdentity.FileSlugFor("github.com/owner/fleet"), repositorySlug);
        Assert.NotEqual("fleet", repositorySlug);
        Assert.False(FleetMemory.IsFleet("github.com/owner/fleet"));

        // The word canonicalizes to nothing as a git identity, so the special case is the only way in.
        Assert.Null(RepositoryIdentity.TryCanonicalize("fleet"));
    }

    [Fact]
    public void Add_sync_audit_and_import_assert_accept_the_fleet_slug()
    {
        Assert.Equal("fleet", MemoryAddOptionsParser.Parse(["--text", "x", "--kind", "durable-fact", "--repository", "fleet"]).Repository);
        Assert.Equal("fleet", MemorySyncOptionsParser.Parse(["--repository", "Fleet"]).Repository);
        Assert.Equal("fleet", MemoryAuditOptionsParser.Parse(["--repository", "fleet"]).Repository);

        var assertion = Assert.Single(
            MemoryImportOptionsParser.Parse(["--assert", @"C:\Users\me\.codex\memories=fleet"]).Assertions);
        Assert.Equal(@"C:\Users\me\.codex\memories", assertion.Path);
        Assert.Equal("fleet", assertion.Repository);

        // Control: the same parsers still canonicalize a git identity, so the word is a special case
        // and not a loosening of the host rule.
        Assert.Equal(
            "github.com/owner/repo",
            MemoryAuditOptionsParser.Parse(["--repository", "https://github.com/Owner/Repo.git"]).Repository);
        Assert.Throws<CliArgumentException>(
            () => MemoryAddOptionsParser.Parse(["--text", "x", "--kind", "durable-fact", "--repository", "owner/repo"]));
    }

    /// <summary>
    /// Where a git identity is required, the word is refused by name rather than slugged into a file
    /// nothing wrote. The control arm on each parser is the same flag with a real identity.
    /// </summary>
    [Fact]
    public void The_fleet_slug_is_refused_wherever_a_git_identity_is_expected()
    {
        var view = Assert.Throws<CliArgumentException>(
            () => LedgerViewOptionsParser.Parse(["--repo-identity", "fleet"]));
        Assert.Contains("reserved memory slug", view.Message, StringComparison.Ordinal);
        Assert.Equal("github.com/owner/repo", LedgerViewOptionsParser.Parse(["--repo-identity", "github.com/owner/repo"]).RepositoryIdentityKey);

        var export = Assert.Throws<CliArgumentException>(
            () => LedgerExportOptionsParser.Parse(["--to", _root, "--repo-identity", "FLEET"]));
        Assert.Contains("reserved memory slug", export.Message, StringComparison.Ordinal);
        Assert.Equal("github.com/owner/repo", LedgerExportOptionsParser.Parse(["--to", _root, "--repo-identity", "github.com/owner/repo"]).RepositoryIdentityKey);

        var facts = Assert.Throws<CliArgumentException>(
            () => MemorySyncOptionsParser.Parse(["--repository", "fleet", "--repository-facts", _root]));
        Assert.Contains("the fleet store never holds", facts.Message, StringComparison.Ordinal);
        Assert.Equal(_root, MemorySyncOptionsParser.Parse(["--repository", "github.com/owner/repo", "--repository-facts", _root]).RepositoryFactsDirectory);
    }

    /// <summary>
    /// The store enumeration lists the fleet store first and skips a directory with no store file —
    /// the property both <c>sync</c>'s walk and <c>audit</c>'s section rest on.
    /// </summary>
    [Fact]
    public async Task The_store_inventory_lists_the_fleet_store_first_and_skips_storeless_directories()
    {
        var repository = "github.com/owner/repo";
        Directory.CreateDirectory(Path.Combine(BatonPaths.Root, "rooms"));
        await MemoryStore.AppendAsync(
            [AuthoredMemory.Create(repository, "repo fact", MemoryKind.DurableFact, AuthoredMemory.Operator, default)],
            BatonPaths.MemoryEntriesFile(FleetMemory.SlugFor(repository)),
            TestContext.Current.CancellationToken);
        await MemoryStore.AppendAsync(
            [AuthoredMemory.Create("fleet", "device fact", MemoryKind.OperatorPreference, AuthoredMemory.Operator, default)],
            FleetMemory.EntriesFile,
            TestContext.Current.CancellationToken);

        var stores = CanonicalStoreInventory.Scan(BatonPaths.Root);

        Assert.Equal(2, stores.Count);
        Assert.True(stores[0].IsFleet);
        Assert.Equal("fleet", stores[0].Slug);
        Assert.Equal(FleetMemory.EntriesFile, stores[0].EntriesFile);
        Assert.False(stores[1].IsFleet);
        Assert.Equal(FleetMemory.SlugFor(repository), stores[1].Slug);
    }
}
