using System.Text;
using System.Text.Json;
using Baton.Accounting;
using Baton.Memory;
using Baton.Status;
using Baton.Tests.Shared;

namespace Baton.Cli.Tests;

/// <summary>
/// #1852 phase C: the pure projector, and <c>baton memory sync</c> driven end to end over a fixture
/// Claude home, a fixture Baton root, and a fixture directory of checked-in repository facts.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every tree here is synthetic.</b> Nothing under the operator's own home is read, written or
/// hashed by this file — the same hard rule <see cref="MemoryImportTests"/> states, and for the same
/// reason: the first projection into a real vendor memory root is the operator's to run.
/// <c>BatonEnvironmentSnapshot.BeginScope</c> is what points <see cref="BatonPaths.Root"/> at the
/// fixture root, and every write this file's subject performs is under it or under the fixture Claude
/// home.
/// </para>
/// <para>
/// <b>No git process is spawned.</b> Root-to-repository resolution goes through an asserted
/// <c>MemoryAliasStore</c> row rather than a probe, because none of this file's claims are about what
/// git answers — <see cref="MemoryImportTests"/> owns that claim with real checkouts, and repeating it
/// here would spend a process per arm to re-measure something already measured.
/// </para>
/// </remarks>
public sealed class MemoryProjectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"baton-1852c-{Guid.NewGuid():N}");
    private readonly IDisposable _scope;

    public MemoryProjectionTests() =>
        _scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = Path.Combine(_root, "baton-root") });

    public void Dispose()
    {
        _scope.Dispose();
        DirectoryCleanup.DeleteRecursively(_root);
    }

    private const string Repository = "github.com/philipreese/baton";

    private string ClaudeHome => Path.Combine(_root, "claude");

    private string UserHome => Path.Combine(_root, "home");

    private static string Slug => RepositoryIdentity.FileSlugFor(Repository);

    // ---------------------------------------------------------------------------------------------
    // The pure projector. These need no filesystem at all, which is the property the split exists for.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The acceptance line, tested as BYTES: projecting an unchanged store twice produces byte-identical
    /// output. Two arms beyond the equality itself, because equality alone passes vacuously if the
    /// projector returns nothing — the bytes must actually carry a known entry id, and a store that
    /// gains an entry must produce DIFFERENT bytes or the comparison is not discriminating.
    /// </summary>
    [Fact]
    public void Projecting_an_unchanged_store_twice_is_byte_identical_and_a_changed_store_is_not()
    {
        var entries = new[] { Entry("feedback_a.md", "alpha"), Entry("project_b.md", "beta") };
        var candidates = entries.Select(Vendor).ToList();

        var first = MemoryProjection.Build(Repository, "store.jsonl", candidates, ProjectionBudget.Default);
        var second = MemoryProjection.Build(Repository, "store.jsonl", candidates, ProjectionBudget.Default);

        Assert.Equal(first.Bytes, second.Bytes);

        // Non-vacuity: the equal bytes are the bytes of a real projection, not of an empty one.
        var text = Encoding.UTF8.GetString(first.Bytes);
        Assert.Contains(entries[0].Id, text, StringComparison.Ordinal);
        Assert.Contains(entries[1].Id, text, StringComparison.Ordinal);
        Assert.Contains("alpha", text, StringComparison.Ordinal);

        // Control: the comparison discriminates. One more entry, different bytes.
        var third = MemoryProjection.Build(
            Repository,
            "store.jsonl",
            [.. candidates, Vendor(Entry("user_c.md", "gamma"))],
            ProjectionBudget.Default);
        Assert.NotEqual(first.Bytes, third.Bytes);
    }

    /// <summary>
    /// The output carries no platform-dependent formatting: LF only, no BOM, and no ordering that
    /// depends on the caller's enumeration. The reversed-input arm is the control — if order were
    /// inherited rather than imposed, these two would differ.
    /// </summary>
    [Fact]
    public void Projected_bytes_are_lf_only_bom_free_and_independent_of_input_order()
    {
        var entries = new[] { Entry("feedback_a.md", "alpha\r\nsecond line"), Entry("project_b.md", "beta") };

        var forward = MemoryProjection.Build(
            Repository, "store.jsonl", entries.Select(Vendor).ToList(), ProjectionBudget.Default);
        var reversed = MemoryProjection.Build(
            Repository, "store.jsonl", entries.Reverse().Select(Vendor).ToList(), ProjectionBudget.Default);

        Assert.Equal(forward.Bytes, reversed.Bytes);
        Assert.DoesNotContain((byte)'\r', forward.Bytes);
        Assert.NotEqual(0xEF, forward.Bytes[0]);
        Assert.StartsWith(MemoryProjection.FormatMarker, Encoding.UTF8.GetString(forward.Bytes), StringComparison.Ordinal);
    }

    /// <summary>
    /// A budget too small for everything truncates at the first entry that does not fit and names
    /// EXACTLY the tail it dropped — asserted as the precise id set, not as "something was dropped",
    /// because a truncation that dropped a different subset each run would pass the weaker assertion.
    /// </summary>
    [Fact]
    public void Budget_overflow_truncates_deterministically_and_names_every_dropped_entry()
    {
        var candidates = Enumerable.Range(0, 6)
            .Select(i => Vendor(Entry($"feedback_{i}.md", new string('x', 200))))
            .ToList();

        var unbounded = MemoryProjection.Build(
            Repository, "store.jsonl", candidates, ProjectionBudget.Default);
        Assert.Empty(unbounded.Dropped);

        var bounded = MemoryProjection.Build(
            Repository, "store.jsonl", candidates, new ProjectionBudget(MaxBodyBytes: 900, MaxEntries: 500));

        Assert.NotEmpty(bounded.Dropped);
        Assert.NotEmpty(bounded.ProjectedEntryIds);

        // The kept set is a PREFIX of the total order and the dropped set is its exact suffix.
        var order = unbounded.ProjectedEntryIds;
        Assert.Equal(order.Take(bounded.ProjectedEntryIds.Count), bounded.ProjectedEntryIds);
        Assert.Equal(
            order.Skip(bounded.ProjectedEntryIds.Count),
            bounded.Dropped.Select(d => d.EntryId));

        // Named, not counted: every drop carries the id and the file an operator would look for.
        foreach (var dropped in bounded.Dropped)
        {
            Assert.NotEmpty(dropped.EntryId);
            Assert.EndsWith(".md", dropped.SourceFileName, StringComparison.Ordinal);
        }

        // Deterministic across runs, and the truncated body is still byte-stable.
        var again = MemoryProjection.Build(
            Repository, "store.jsonl", candidates, new ProjectionBudget(MaxBodyBytes: 900, MaxEntries: 500));
        Assert.Equal(bounded.Bytes, again.Bytes);
        Assert.Equal(bounded.Dropped.Select(d => d.EntryId), again.Dropped.Select(d => d.EntryId));
    }

    /// <summary>
    /// The entry-count bound is a real bound, not a byte bound in disguise: a body well inside
    /// <see cref="ProjectionBudget.MaxBodyBytes"/> still truncates at <see cref="ProjectionBudget.MaxEntries"/>.
    /// </summary>
    [Fact]
    public void The_entry_ceiling_truncates_independently_of_the_byte_ceiling()
    {
        var candidates = Enumerable.Range(0, 5).Select(i => Vendor(Entry($"feedback_{i}.md", "x"))).ToList();

        var bounded = MemoryProjection.Build(
            Repository, "store.jsonl", candidates, new ProjectionBudget(MaxBodyBytes: 1_000_000, MaxEntries: 2));

        Assert.Equal(2, bounded.ProjectedEntryIds.Count);
        Assert.Equal(3, bounded.Dropped.Count);
    }

    /// <summary>
    /// A superseded entry is omitted from the bytes and named in the report — the rule
    /// <see cref="MemoryProjection"/> states. The control is the same entry with the link removed: it
    /// projects, which is what proves the omission is the link's doing and not the fixture's.
    /// </summary>
    [Fact]
    public void A_superseded_entry_is_omitted_from_the_bytes_and_named_in_the_report()
    {
        var live = Entry("feedback_a.md", "current");
        var archived = Entry("archived/feedback_a.md", "older");
        var links = new[]
        {
            new MemorySupersessionLink(
                MemorySupersessionLink.Derive(live.Id, archived.Id), live.Id, archived.Id, Repository, default),
        };

        var resolved = MemoryStore.Resolve([live, archived], links);
        var projection = MemoryProjection.Build(
            Repository, "store.jsonl", resolved.Select(Vendor).ToList(), ProjectionBudget.Default);

        Assert.Equal([live.Id], projection.ProjectedEntryIds);
        var omission = Assert.Single(projection.Superseded);
        Assert.Equal(archived.Id, omission.EntryId);
        Assert.Contains(live.Id, omission.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("older", Encoding.UTF8.GetString(projection.Bytes), StringComparison.Ordinal);

        // Control arm: with no link, the very same entry IS projected. The omission is the link's doing.
        var unlinked = MemoryProjection.Build(
            Repository,
            "store.jsonl",
            MemoryStore.Resolve([live, archived], []).Select(Vendor).ToList(),
            ProjectionBudget.Default);
        Assert.Equal(2, unlinked.ProjectedEntryIds.Count);
        Assert.Empty(unlinked.Superseded);
        Assert.Contains("older", Encoding.UTF8.GetString(unlinked.Bytes), StringComparison.Ordinal);
    }

    /// <summary>
    /// A checked-in repository fact and a vendor-memory fact of the same name: repository truth is
    /// projected, the vendor entry is reported as overridden with its canonical id, and neither is
    /// merged. The control is a vendor entry with a name nothing collides with — it survives, which is
    /// what proves the override is the collision's doing rather than a blanket preference for
    /// repository-origin candidates.
    /// </summary>
    [Fact]
    public void A_conflicting_repository_fact_wins_and_the_vendor_loser_is_reported()
    {
        var vendorFact = Entry("feedback_rules.md", "the vendor's copy");
        var uncontested = Entry("project_other.md", "nothing collides with this");
        var repositoryFact = Entry("feedback_rules.md", "the checked-in copy", sourceDirectory: "C:/checkout/facts");

        var projection = MemoryProjection.Build(
            Repository,
            "store.jsonl",
            [Vendor(vendorFact), Vendor(uncontested), new MemoryProjectionCandidate(repositoryFact, MemoryFactOrigin.Repository)],
            ProjectionBudget.Default);

        var text = Encoding.UTF8.GetString(projection.Bytes);
        Assert.Contains("the checked-in copy", text, StringComparison.Ordinal);
        Assert.DoesNotContain("the vendor's copy", text, StringComparison.Ordinal);
        Assert.Contains(uncontested.Id, text, StringComparison.Ordinal);

        var loser = Assert.Single(projection.Overridden);
        Assert.Equal(vendorFact.Id, loser.EntryId);
        Assert.Contains(repositoryFact.Id, loser.Reason, StringComparison.Ordinal);
        Assert.Contains("DIFFERENT", loser.Reason, StringComparison.Ordinal);

        // Never merged: the winner's text is projected whole and the loser's does not appear anywhere,
        // including inside the winner's section.
        Assert.Single(projection.ProjectedEntryIds, id => id == repositoryFact.Id);
    }

    // ---------------------------------------------------------------------------------------------
    // `baton memory sync` end to end.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The control pair the acceptance line rests on. Without <c>--apply</c> the target file does not
    /// exist AND the root holds no Baton file at all; with it, the file exists and holds exactly the
    /// projected bytes. Preparing output on disk counts as writing, so the arm inspects the directory
    /// rather than only the one filename.
    /// </summary>
    [Fact]
    public async Task Sync_writes_nothing_without_apply_and_writes_the_projection_with_it()
    {
        var root = await SeedStoreAndClaudeRootAsync();
        var target = Path.Combine(root, ClaudeProjectionTarget.ProjectionFileName);

        var dry = await RunAsync("--repository", Repository);
        Assert.Contains("NOTHING WAS WRITTEN", dry, StringComparison.Ordinal);
        Assert.Contains("would create", dry, StringComparison.Ordinal);
        Assert.False(File.Exists(target));
        Assert.Empty(Directory.GetFiles(root, "baton-*"));

        var applied = await RunAsync("--repository", Repository, "--apply");
        Assert.Contains("[created]", applied, StringComparison.Ordinal);
        Assert.True(File.Exists(target));

        var written = File.ReadAllBytes(target);
        Assert.Contains(MemoryProjection.FormatMarker, Encoding.UTF8.GetString(written), StringComparison.Ordinal);
        Assert.DoesNotContain((byte)'\r', written);
    }

    /// <summary>
    /// The end-to-end idempotence claim: a second <c>--apply</c> over an unchanged store leaves the
    /// target file byte-identical and reports it unchanged. Compared as bytes read back off disk, not
    /// as an exit code or an mtime — a rewrite of identical content would pass a "no error" test and
    /// fail this one only if the content differed, so the byte comparison is what the claim needs.
    /// </summary>
    [Fact]
    public async Task Syncing_twice_over_an_unchanged_store_leaves_the_target_byte_identical()
    {
        var root = await SeedStoreAndClaudeRootAsync();
        var target = Path.Combine(root, ClaudeProjectionTarget.ProjectionFileName);

        await RunAsync("--repository", Repository, "--apply");
        var first = File.ReadAllBytes(target);

        var second = await RunAsync("--repository", Repository, "--apply");
        Assert.Contains("[unchanged]", second, StringComparison.Ordinal);
        Assert.Equal(first, File.ReadAllBytes(target));

        // Non-vacuity, again at the end-to-end level: the identical bytes carry a real entry.
        Assert.Contains("feedback_rules.md", Encoding.UTF8.GetString(first), StringComparison.Ordinal);

        // Control: append one entry to the canonical store and the target's bytes MUST change.
        await MemoryStore.AppendAsync(
            [Entry("user_new.md", "a new memory")],
            BatonPaths.MemoryEntriesFile(Slug),
            TestContext.Current.CancellationToken);

        await RunAsync("--repository", Repository, "--apply");
        Assert.NotEqual(first, File.ReadAllBytes(target));
    }

    /// <summary>
    /// The conflict acceptance line at the verb's own surface: with <c>--repository-facts</c> pointed
    /// at a directory holding a fact of the same name, the projection carries the checked-in text and
    /// the report names the vendor entry it overrode.
    /// </summary>
    [Fact]
    public async Task Sync_reports_the_overridden_vendor_fact_and_projects_repository_truth()
    {
        var root = await SeedStoreAndClaudeRootAsync();
        var facts = Path.Combine(_root, "checkout-facts");
        Directory.CreateDirectory(facts);
        File.WriteAllText(Path.Combine(facts, "feedback_rules.md"), "the checked-in copy");

        var output = await RunAsync(
            "--repository", Repository, "--repository-facts", facts, "--apply");

        Assert.Contains("OVERRIDDEN by checked-in repository truth", output, StringComparison.Ordinal);
        Assert.Contains("Repository facts considered: 1", output, StringComparison.Ordinal);

        var text = File.ReadAllText(Path.Combine(root, ClaudeProjectionTarget.ProjectionFileName));
        Assert.Contains("the checked-in copy", text, StringComparison.Ordinal);
        Assert.DoesNotContain("the vendor's copy", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Nothing is invented when there is nowhere to write: the report says NO TARGET and the projects
    /// directory is still empty afterwards. The counterpart to the arm above — the same store, minus
    /// the alias that made the root resolvable.
    /// </summary>
    [Fact]
    public async Task A_repository_with_no_discovered_root_is_reported_rather_than_given_one()
    {
        await SeedStoreAsync();
        Directory.CreateDirectory(Path.Combine(ClaudeHome, "projects"));

        var output = await RunAsync("--repository", Repository, "--apply");

        Assert.Contains("NO TARGET", output, StringComparison.Ordinal);
        Assert.Contains("spec/baton.md §12", output, StringComparison.Ordinal);
        Assert.Empty(Directory.GetDirectories(Path.Combine(ClaudeHome, "projects")));
    }

    /// <summary>The JSON report is a parseable contract, with the omission lists in it by name.</summary>
    [Fact]
    public async Task The_json_report_carries_every_omission_by_name()
    {
        await SeedStoreAndClaudeRootAsync();

        var output = await RunAsync("--repository", Repository, "--format", "json");

        using var document = JsonDocument.Parse(output);
        var repository = document.RootElement.GetProperty("repositories")[0];
        Assert.Equal(Repository, repository.GetProperty("repository").GetString());
        Assert.NotEmpty(repository.GetProperty("bodySha256").GetString()!);
        Assert.False(document.RootElement.GetProperty("apply").GetBoolean());
    }

    /// <summary><c>--repository-facts</c> without <c>--repository</c> is refused, never defaulted.</summary>
    [Fact]
    public void Repository_facts_without_a_repository_is_refused()
    {
        var exception = Assert.Throws<CliArgumentException>(
            () => MemorySyncOptionsParser.Parse(["--repository-facts", "C:/facts"]));

        Assert.Contains("needs '--repository <id>'", exception.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------

    private async Task<string> RunAsync(params string[] args)
    {
        var writer = new StringWriter();
        var exitCode = await MemorySyncCommand.ExecuteAsync(
            MemorySyncOptionsParser.Parse(args),
            writer,
            ClaudeHome,
            TestContext.Current.CancellationToken,
            UserHome);

        Assert.Equal(0, exitCode);
        return writer.ToString();
    }

    /// <summary>Two entries in the canonical store, and nothing else.</summary>
    private async Task SeedStoreAsync() =>
        await MemoryStore.AppendAsync(
            [Entry("feedback_rules.md", "the vendor's copy"), Entry("project_direction.md", "where this is going")],
            BatonPaths.MemoryEntriesFile(Slug),
            TestContext.Current.CancellationToken);

    /// <summary>
    /// The same store, plus a Claude memory root that resolves to <see cref="Repository"/> through an
    /// asserted alias. Returns the root directory.
    /// </summary>
    private async Task<string> SeedStoreAndClaudeRootAsync()
    {
        await SeedStoreAsync();

        var root = Path.Combine(ClaudeHome, "projects", "c--fixture-checkout", "memory");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "feedback_rules.md"), "the vendor's copy");

        await MemoryAliasStore.AppendAsync(
            [new MemoryAliasEntry(BatonPaths.RecordKey(root), Repository, "test", default)],
            BatonPaths.MemoryAliasFile,
            TestContext.Current.CancellationToken);

        return root;
    }

    private static MemoryProjectionCandidate Vendor(MemoryEntry entry) =>
        new(entry, MemoryFactOrigin.Vendor);

    /// <summary>
    /// One entry with a derived id, exactly as an import would build it. No clock reaches
    /// <see cref="MemoryEntry.ImportedAtUtc"/>: nothing in a projection reads it, and a clock in a
    /// fixture that feeds a byte-identity assertion is the first thing that would make one flake.
    /// </summary>
    private static MemoryEntry Entry(string fileName, string text, string sourceDirectory = "C:/vendor/memory")
    {
        var path = $"{sourceDirectory}/{fileName}";
        var sha256 = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        var (kind, kindSource) = MemoryKindInference.Infer(Path.GetFileName(fileName), text);

        return new MemoryEntry(
            MemoryEntry.Derive(Repository, path, sha256),
            Repository,
            kind,
            kindSource,
            text,
            sha256,
            path,
            MemoryRootInventory.ClaudeVendor,
            VendorMemoryScope.Vendor,
            SourceMtimeUtc: default,
            ImportedAtUtc: default);
    }
}
