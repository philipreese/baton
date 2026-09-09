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
    /// An overflowing budget keeps a prefix of the total order and names EXACTLY the ids it left out —
    /// asserted as the precise set, not as "something was dropped", because a run leaving out a
    /// different subset each time would pass the weaker assertion and fail the claim.
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

        var resolved = MemoryStore.Resolve([live, archived], links, []);
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
            MemoryStore.Resolve([live, archived], [], []).Select(Vendor).ToList(),
            ProjectionBudget.Default);
        Assert.Equal(2, unlinked.ProjectedEntryIds.Count);
        Assert.Empty(unlinked.Superseded);
        Assert.Contains("older", Encoding.UTF8.GetString(unlinked.Bytes), StringComparison.Ordinal);
    }

    /// <summary>
    /// Pins spec/baton.md §12's retraction-ordering paragraph (#2113) against
    /// <see cref="MemoryStore.Resolve"/> directly. Both link directions are asserted, since the
    /// underlying check tests each endpoint independently.
    /// </summary>
    [Fact]
    public void Resolve_omits_a_link_when_either_side_of_it_is_retracted()
    {
        var live = Entry("feedback_a.md", "current");
        var archived = Entry("archived/feedback_a.md", "older");
        var link = new MemorySupersessionLink(
            MemorySupersessionLink.Derive(live.Id, archived.Id), live.Id, archived.Id, Repository, default);

        // Retracted target (the superseded side): the link is dropped, and archived is gone entirely
        // (retraction already removed it), so only `live` survives, unlinked.
        var targetRetracted = new MemoryRetraction(archived.Id, Repository, "fixture retraction", "test", default);
        var resolvedTargetRetracted = MemoryStore.Resolve([live, archived], [link], [targetRetracted]);
        var survivorA = Assert.Single(resolvedTargetRetracted);
        Assert.Equal(live.Id, survivorA.Id);
        Assert.Empty(survivorA.Supersedes ?? []);

        // Retracted source (the superseding side): symmetric -- only `archived` survives, unlinked.
        var sourceRetracted = new MemoryRetraction(live.Id, Repository, "fixture retraction", "test", default);
        var resolvedSourceRetracted = MemoryStore.Resolve([live, archived], [link], [sourceRetracted]);
        var survivorB = Assert.Single(resolvedSourceRetracted);
        Assert.Equal(archived.Id, survivorB.Id);
        Assert.Empty(survivorB.SupersededBy ?? []);

        // Control: with neither endpoint retracted, the link applies as MemoryProjectionTests' own
        // superseded test already pins -- both entries present, archived carries the SupersededBy link.
        var resolvedNeitherRetracted = MemoryStore.Resolve([live, archived], [link], []);
        Assert.Equal(2, resolvedNeitherRetracted.Count);
        Assert.Contains(live.Id, resolvedNeitherRetracted.Single(e => e.Id == archived.Id).SupersededBy ?? []);
    }

    /// <summary>
    /// A checked-in repository fact and a vendor-memory fact of the same name: the checked-in text is
    /// what reaches the bytes, the losing row comes back in the report carrying its canonical id, and
    /// the two are not combined. The control is a vendor entry nothing collides with — it survives, which is
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

    /// <summary>
    /// Two checked-in facts whose names differ only in case do not throw out of the public projector —
    /// the state a case-sensitive filesystem makes reachable. Both reach the bytes — precedence only
    /// ever removes a vendor copy — and the answer is stable: the same inputs in the other order give
    /// the same bytes and pick the same winner for the colliding vendor copy.
    /// </summary>
    [Fact]
    public void Two_repository_facts_differing_only_in_case_project_without_throwing()
    {
        var upper = Entry("Rules.md", "the upper-case copy", sourceDirectory: "C:/checkout/facts");
        var lower = Entry("rules.md", "the lower-case copy", sourceDirectory: "C:/checkout/facts");
        var vendorCopy = Entry("rules.md", "the vendor's copy");

        MemoryProjectionCandidate[] candidates =
        [
            new(upper, MemoryFactOrigin.Repository),
            new(lower, MemoryFactOrigin.Repository),
            Vendor(vendorCopy),
        ];

        var projection = MemoryProjection.Build(
            Repository, "store.jsonl", candidates, ProjectionBudget.Default);

        Assert.Equal(2, projection.ProjectedEntryIds.Count);
        Assert.Contains(upper.Id, projection.ProjectedEntryIds);
        Assert.Contains(lower.Id, projection.ProjectedEntryIds);

        // The vendor copy still loses to one of them — the key really is case-insensitive, so this arm
        // is not passing because the collision quietly stopped being a collision.
        var loser = Assert.Single(projection.Overridden);
        Assert.Equal(vendorCopy.Id, loser.EntryId);

        var reversed = MemoryProjection.Build(
            Repository, "store.jsonl", candidates.Reverse().ToList(), ProjectionBudget.Default);
        Assert.Equal(projection.Bytes, reversed.Bytes);
        Assert.Equal(loser.Reason, Assert.Single(reversed.Overridden).Reason);
    }

    /// <summary>
    /// An archived-origin entry IS projected, and it is labelled twice over — the <c>baton:entry</c>
    /// comment a machine reads and the sentence a person reads both carry <c>historical-note</c>. The
    /// ruling is the projector's remarks', glossed in spec/baton.md §12. The control is a live entry in
    /// the same projection, whose own sections carry neither.
    /// </summary>
    [Fact]
    public void An_archived_origin_entry_projects_and_is_labelled_historical()
    {
        var historical = Entry("feedback_archived.md", "what was true then") with
        {
            Kind = MemoryKind.HistoricalNote,
            KindSource = MemoryKindSource.InferredFromArchive,
        };
        var current = Entry("feedback_rules.md", "what is true now");

        var projection = MemoryProjection.Build(
            Repository, "store.jsonl", [Vendor(historical), Vendor(current)], ProjectionBudget.Default);
        var text = Encoding.UTF8.GetString(projection.Bytes);

        Assert.Equal(2, projection.ProjectedEntryIds.Count);
        Assert.Contains($"<!-- baton:entry id={historical.Id} kind=historical-note", text, StringComparison.Ordinal);
        Assert.Contains($"`{historical.Id}` (historical-note)", text, StringComparison.Ordinal);

        // Control: the live entry in the same projection carries its own kind, not this one.
        Assert.DoesNotContain($"<!-- baton:entry id={current.Id} kind=historical-note", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>canonicalStorePath</c> is a projector INPUT, not incidental: one store rendered against two
    /// store paths gives two different files. Pinned because spec/baton.md §12 used to gloss the
    /// signature without it, and because it is half of why byte-identity is scoped to one machine.
    /// </summary>
    [Fact]
    public void The_canonical_store_path_is_an_input_that_changes_the_bytes()
    {
        var candidates = new[] { Vendor(Entry("feedback_a.md", "alpha")) };

        var here = MemoryProjection.Build(Repository, "C:/one/entries.jsonl", candidates, ProjectionBudget.Default);
        var there = MemoryProjection.Build(Repository, "D:/other/entries.jsonl", candidates, ProjectionBudget.Default);

        Assert.NotEqual(here.Bytes, there.Bytes);

        // …and only the header moved: the body hash, which is what the idempotence claim rests on, is
        // the same. That is the split the projector's remarks describe, measured rather than asserted.
        Assert.Equal(here.BodySha256, there.BodySha256);
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

    /// <summary>
    /// The JSON report is a parseable contract AND it carries the omissions by id — asserted against a
    /// fixture that actually produces one, because a report shape checked over an empty
    /// <c>overridden</c> array would pass whether or not the field is ever populated.
    /// </summary>
    [Fact]
    public async Task The_json_report_carries_every_omission_by_name()
    {
        await SeedStoreAndClaudeRootAsync();
        var facts = Path.Combine(_root, "json-facts");
        Directory.CreateDirectory(facts);
        File.WriteAllText(Path.Combine(facts, "feedback_rules.md"), "the checked-in copy");

        var output = await RunAsync(
            "--repository", Repository, "--repository-facts", facts, "--format", "json");

        using var document = JsonDocument.Parse(output);
        Assert.False(document.RootElement.GetProperty("apply").GetBoolean());
        Assert.Equal(1, document.RootElement.GetProperty("repositoryFactsConsidered").GetInt32());

        var repository = document.RootElement.GetProperty("repositories")[0];
        Assert.Equal(Repository, repository.GetProperty("repository").GetString());
        Assert.NotEmpty(repository.GetProperty("bodySha256").GetString()!);

        var overridden = Assert.Single(repository.GetProperty("overridden").EnumerateArray().ToList());
        Assert.Equal(
            Entry("feedback_rules.md", "the vendor's copy").Id,
            overridden.GetProperty("entryId").GetString());
        Assert.Equal("feedback_rules.md", overridden.GetProperty("sourceFileName").GetString());
        Assert.NotEmpty(overridden.GetProperty("reason").GetString()!);
    }

    /// <summary>
    /// A repository-origin section does not claim to be a canonical store row, because its id is not
    /// one — <see cref="MemoryProjection"/>'s section remarks carry why the two ids differ. The
    /// control is the vendor section in the same file, which DOES carry the canonical wording and whose
    /// id is asserted to resolve in the store — without it this arm would pass over a projection that
    /// back-pointed nothing at all.
    /// </summary>
    [Fact]
    public async Task A_repository_fact_section_does_not_claim_a_canonical_store_row()
    {
        var root = await SeedStoreAndClaudeRootAsync();
        var facts = Path.Combine(_root, "provenance-facts");
        Directory.CreateDirectory(facts);
        var factPath = Path.Combine(facts, "feedback_only_checked_in.md");
        File.WriteAllText(factPath, "a fact that lives only in the checkout");

        await RunAsync("--repository", Repository, "--repository-facts", facts, "--apply");
        var text = File.ReadAllText(Path.Combine(root, ClaudeProjectionTarget.ProjectionFileName));

        var stored = await MemoryStore.ReadAllAsync(
            BatonPaths.MemoryEntriesFile(Slug), TestContext.Current.CancellationToken);
        var storedIds = stored.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);

        // Every back-pointed id either resolves in the canonical store, or its section says outright
        // that it does not. Nothing in between.
        foreach (var match in System.Text.RegularExpressions.Regex.Matches(text, @"<!-- baton:entry id=(\w+) [^>]*origin=(\w+)"))
        {
            var id = ((System.Text.RegularExpressions.Match)match).Groups[1].Value;
            var origin = ((System.Text.RegularExpressions.Match)match).Groups[2].Value;
            if (origin == "repository")
            {
                Assert.DoesNotContain(id, storedIds);
                Assert.Contains($"Checked-in repository fact `{id}`", text, StringComparison.Ordinal);
                Assert.Contains("**Not a canonical store row**", text, StringComparison.Ordinal);
            }
            else
            {
                Assert.Contains(id, storedIds);
                Assert.Contains($"Canonical entry `{id}`", text, StringComparison.Ordinal);
            }
        }

        // Non-vacuity: the loop above saw both kinds, not zero sections of one of them.
        Assert.Contains("origin=repository", text, StringComparison.Ordinal);
        Assert.Contains("origin=vendor", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A dry run against a repository with no canonical store creates nothing under Baton's own root.
    /// The state is reachable by a typo (<c>MemorySyncCommand</c>'s own comment says why), which is
    /// exactly when "nothing was written" has to still be true. <b>What
    /// this pins is the property, not any one guard</b>: the arm was run with
    /// <c>MemorySyncCommand</c>'s early <c>File.Exists</c> check removed and still passed, which is
    /// recorded here rather than left implied — that check is defence, and the comment beside it says
    /// so.
    /// </summary>
    [Fact]
    public async Task A_repository_with_no_store_creates_nothing_under_the_baton_root()
    {
        const string absent = "github.com/philipreese/no-such-repository";
        var directory = BatonPaths.RepositoryDirectory(RepositoryIdentity.FileSlugFor(absent));

        var output = await RunAsync("--repository", absent);

        Assert.Contains("No repository has a canonical memory store yet", output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(directory));
    }

    /// <summary>
    /// <b>The no-feedback-loop acceptance.</b> <c>sync --apply</c> writes into a root that is
    /// <c>import</c>'s own population, so a projection re-ingested as a memory would feed the store its
    /// own contents once per cycle. The claim is a FIXED POINT measured in store bytes: with the root's
    /// real memory already imported, a sync-then-import cycle changes nothing, and a second cycle
    /// changes nothing either.
    /// </summary>
    /// <remarks>
    /// Two non-vacuity checks and one control, because "the bytes did not change" passes for a great
    /// many wrong reasons: the projection file must actually exist on disk, the import must report the
    /// skip by name and under its own heading rather than the <c>unfiled</c> one (<c>MemoryImportPlan</c>
    /// says what the two populations mean), and an ordinary <c>.md</c> dropped into the same root
    /// between cycles MUST change the store — otherwise the comparison is measuring an import that
    /// stopped importing.
    /// </remarks>
    [Fact]
    public async Task Importing_a_root_sync_wrote_into_adds_nothing_and_reports_the_projection_skipped()
    {
        var root = await SeedStoreAndClaudeRootAsync();
        var entriesFile = BatonPaths.MemoryEntriesFile(Slug);

        // Cycle 0: the root's own memory becomes canonical, so what follows is about what SYNC added.
        await ImportAsync();
        var settled = File.ReadAllBytes(entriesFile);

        // Cycle 1.
        await RunAsync("--repository", Repository, "--apply");
        var projection = Path.Combine(root, ClaudeProjectionTarget.ProjectionFileName);
        Assert.True(File.Exists(projection));

        var imported = await ImportAsync();
        Assert.Equal(settled, File.ReadAllBytes(entriesFile));
        Assert.Contains("projection-skipped: 1", imported, StringComparison.Ordinal);
        Assert.Contains(projection, imported, StringComparison.Ordinal);
        Assert.DoesNotContain("Unfiled -- read, digested", imported, StringComparison.Ordinal);

        // Cycle 2: the same pair again. Store bytes unchanged, and the skip count is still exactly the
        // number of files sync projected — one target, one skip, not one more per cycle.
        await RunAsync("--repository", Repository, "--apply");
        var again = await ImportAsync();
        Assert.Equal(settled, File.ReadAllBytes(entriesFile));
        Assert.Contains("projection-skipped: 1", again, StringComparison.Ordinal);

        // No entry in the store was ever sourced from the projection file.
        var stored = await MemoryStore.ReadAllAsync(entriesFile, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(stored, e => e.SourcePath.Contains(
            ClaudeProjectionTarget.ProjectionFileName, StringComparison.OrdinalIgnoreCase));

        // Control: an ordinary memory dropped into the same root DOES reach the store. Without this the
        // three assertions above would pass over an import that had simply stopped working.
        File.WriteAllText(Path.Combine(root, "user_new.md"), "a memory a person wrote");
        await ImportAsync();
        Assert.NotEqual(settled, File.ReadAllBytes(entriesFile));
    }

    /// <summary>
    /// An archived root is never a projection write target — not even with a repository asserted for it,
    /// which is exactly how Q2 says an archive gets imported, so the case is reachable rather than
    /// theoretical. The control is the same alias on a LIVE root, which does receive a file: that is
    /// what proves the refusal is the root's kind and not the fixture failing to resolve.
    /// </summary>
    /// <remarks>
    /// The arm carries the other two non-target classes too, because they are one report and one
    /// printer: a live Claude root nothing resolves, and an unassigned per-machine Codex root. All four
    /// reasons are report output, and report output nothing asserts is a claim with no instrument.
    /// </remarks>
    [Fact]
    public async Task An_archived_root_is_refused_as_a_target_even_with_a_repository_asserted()
    {
        await SeedStoreAsync();

        var archived = Path.Combine(ClaudeHome, "memory-archive", "2026-09-03", "c--fixture-checkout-memory");
        Directory.CreateDirectory(archived);
        File.WriteAllText(Path.Combine(archived, "feedback_rules.md"), "the archived copy");
        await AssertAliasAsync(archived);

        // Two more roots that exist and are not targets, for two different reasons.
        Directory.CreateDirectory(Path.Combine(ClaudeHome, "projects", "c--unclaimed", "memory"));
        Directory.CreateDirectory(Path.Combine(UserHome, ".codex", "memories"));

        var output = await RunAsync("--repository", Repository, "--apply");

        Assert.Contains("NO TARGET", output, StringComparison.Ordinal);
        Assert.Contains("READ-ONLY historical root (memory-archive/2026-09-03)", output, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(archived, "baton-*"));

        // The unresolvable Claude root and the unassigned Codex root are named with their own reasons,
        // not folded into the archive's or left out of the report entirely.
        Assert.Contains("no repository identity resolves for this root", output, StringComparison.Ordinal);
        Assert.Contains("UNASSIGNED rather than handed whichever repository", output, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(Path.Combine(UserHome, ".codex", "memories"), "baton-*"));

        // Control: a live root under the same asserted repository IS written into, in the same run shape.
        var live = Path.Combine(ClaudeHome, "projects", "c--fixture-checkout", "memory");
        Directory.CreateDirectory(live);
        await AssertAliasAsync(live);

        var second = await RunAsync("--repository", Repository, "--apply");
        Assert.Contains("[created]", second, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(live, ClaudeProjectionTarget.ProjectionFileName)));

        // …and the archive is still untouched after the run that did write something.
        Assert.Empty(Directory.GetFiles(archived, "baton-*"));
    }

    /// <summary>
    /// The Codex markdown target end to end — written, byte-identical on a second run — beside the
    /// negative four documents assert and nothing used to check: a Codex sqlite root is discovered and
    /// NEVER projected, with the report saying why. Both in one arm because the two are one condition
    /// apart: a family filter that stopped discriminating would fail this in both directions at once.
    /// </summary>
    [Fact]
    public async Task The_codex_markdown_root_is_projected_and_a_sqlite_root_never_is()
    {
        await SeedStoreAsync();

        var codexHome = Path.Combine(UserHome, ".codex");
        var markdownRoot = Path.Combine(codexHome, "memories");
        Directory.CreateDirectory(markdownRoot);

        // The sqlite store sits in the SAME Codex home, and is asserted to the same repository — so the
        // only thing keeping it out of the targets is the family test.
        File.WriteAllBytes(Path.Combine(codexHome, "memories_main.sqlite"), [0x53, 0x51, 0x4C, 0x69]);
        await AssertAliasAsync(markdownRoot);
        await AssertAliasAsync(codexHome);

        var output = await RunAsync("--repository", Repository, "--apply");
        var target = Path.Combine(markdownRoot, CodexProjectionTarget.ProjectionFileName);

        Assert.Contains("[created] codex:", output, StringComparison.Ordinal);
        Assert.True(File.Exists(target));

        var first = File.ReadAllBytes(target);
        Assert.StartsWith(MemoryProjection.FormatMarker, Encoding.UTF8.GetString(first), StringComparison.Ordinal);

        // The sqlite root: discovered, named, and not written into. The Codex home holds exactly the
        // sqlite file and the memories directory it had before the run.
        Assert.Contains("codex-sqlite", output, StringComparison.Ordinal);
        Assert.Contains("is not a markdown surface", output, StringComparison.Ordinal);
        Assert.Equal(["memories_main.sqlite"], Directory.GetFiles(codexHome).Select(Path.GetFileName).ToArray());

        // Byte-identical on a second apply, at the Codex target rather than only the Claude one.
        var second = await RunAsync("--repository", Repository, "--apply");
        Assert.Contains("[unchanged] codex:", second, StringComparison.Ordinal);
        Assert.Equal(first, File.ReadAllBytes(target));
    }

    /// <summary>
    /// #2040's acceptance line, as a polarity pair rather than two arms: after <c>--apply</c> the
    /// projections match the store and <c>--check</c> exits 0; append ONE entry to the canonical store
    /// and the same invocation exits 1. The exit-0 half alone passes vacuously if <c>--check</c> gates
    /// on nothing at all, so the appended entry is the control that makes the 0 mean something.
    /// </summary>
    /// <remarks>
    /// The failing arm also asserts that <c>--check</c> is a read: the target file it reports as stale
    /// is still byte-for-byte what <c>--apply</c> left, so a gate cannot repair what it measures.
    /// </remarks>
    [Fact]
    public async Task Check_exits_zero_when_the_projections_match_and_one_when_the_store_moves_ahead()
    {
        var root = await SeedStoreAndClaudeRootAsync();
        var target = Path.Combine(root, ClaudeProjectionTarget.ProjectionFileName);

        await RunAsync("--repository", Repository, "--apply");
        var applied = File.ReadAllBytes(target);

        var (clean, cleanOutput) = await RunForExitAsync("--repository", Repository, "--check");
        Assert.Equal(0, clean);
        Assert.Contains("CHECK PASSED (exit 0)", cleanOutput, StringComparison.Ordinal);
        Assert.Contains("[unchanged]", cleanOutput, StringComparison.Ordinal);

        // The control: one more entry in the canonical store, and the same command must now fail.
        await MemoryStore.AppendAsync(
            [Entry("user_new.md", "a new memory")],
            BatonPaths.MemoryEntriesFile(Slug),
            TestContext.Current.CancellationToken);

        var (stale, staleOutput) = await RunForExitAsync("--repository", Repository, "--check");
        Assert.Equal(1, stale);
        Assert.Contains("CHECK FAILED (exit 1) -- 1 discovered target(s)", staleOutput, StringComparison.Ordinal);
        Assert.Contains("would rewrite", staleOutput, StringComparison.Ordinal);
        Assert.Equal(applied, File.ReadAllBytes(target));

        // The same failing state under --format json, because the verdict prose is deliberately text-only:
        // the JSON report has to stay ONE parseable document under --check (a verdict line appended after
        // the serializer would have made it two), and the 'check' field has to say which run it was. Both
        // are asserted here rather than argued for in a comment.
        var (staleJsonExit, staleJson) = await RunForExitAsync(
            "--repository", Repository, "--check", "--format", "json");
        Assert.Equal(1, staleJsonExit);

        using var document = JsonDocument.Parse(staleJson);
        Assert.True(document.RootElement.GetProperty("check").GetBoolean());
        Assert.False(document.RootElement.GetProperty("apply").GetBoolean());
        Assert.Equal(
            "would rewrite",
            document.RootElement.GetProperty("repositories")[0].GetProperty("targets")[0]
                .GetProperty("disposition").GetString());
    }

    /// <summary>
    /// Every stored entry retracted (#2113): <c>sync</c> must not crash reading `resolved[0]` (empty,
    /// since Resolve drops every retracted entry) when it needs the repository name -- the fix reads
    /// `stored[0]` instead. Report-shape checks ride along: the text report names both retractions under
    /// "omitted as RETRACTED" with their reasons, the JSON `retracted` array carries both with the
    /// `retracted` property name, and the projected file itself is empty of both entries' text.
    /// </summary>
    [Fact]
    public async Task Sync_over_an_entirely_retracted_store_reports_both_retractions_and_projects_nothing()
    {
        var root = await SeedStoreAndClaudeRootAsync();
        var stored = await MemoryStore.ReadAllAsync(BatonPaths.MemoryEntriesFile(Slug), TestContext.Current.CancellationToken);
        Assert.Equal(2, stored.Count);

        await MemoryStore.AppendRetractionsAsync(
            [
                new MemoryRetraction(stored[0].Id, Repository, "first fixture retraction", "test", default),
                new MemoryRetraction(stored[1].Id, Repository, "second fixture retraction", "test", default),
            ],
            BatonPaths.MemoryRetractionsFile(Slug),
            TestContext.Current.CancellationToken);

        var (exitCode, output) = await RunForExitAsync("--repository", Repository, "--apply");

        Assert.Equal(0, exitCode);
        Assert.Contains("omitted as RETRACTED", output, StringComparison.Ordinal);
        Assert.Contains("first fixture retraction", output, StringComparison.Ordinal);
        Assert.Contains("second fixture retraction", output, StringComparison.Ordinal);

        var target = Path.Combine(root, ClaudeProjectionTarget.ProjectionFileName);
        var projected = File.ReadAllText(target);
        Assert.DoesNotContain("the vendor's copy", projected, StringComparison.Ordinal);
        Assert.DoesNotContain("where this is going", projected, StringComparison.Ordinal);

        var (jsonExit, json) = await RunForExitAsync("--repository", Repository, "--check", "--format", "json");
        Assert.Equal(0, jsonExit);
        using var document = JsonDocument.Parse(json);
        var retracted = document.RootElement.GetProperty("repositories")[0].GetProperty("retracted");
        Assert.Equal(2, retracted.GetArrayLength());
    }

    /// <summary>
    /// A repository whose store has memories but whose machine has no matching root does NOT fail the
    /// check: there is no target that could be rewritten. Pinned rather than left to the help text,
    /// because "check passed" and "there was nothing to check" are the pair a reader most easily
    /// conflates — the report still prints NO TARGET on the passing exit code.
    /// </summary>
    [Fact]
    public async Task Check_passes_for_a_repository_with_no_discovered_root()
    {
        await SeedStoreAsync();
        Directory.CreateDirectory(Path.Combine(ClaudeHome, "projects"));

        var (exitCode, output) = await RunForExitAsync("--repository", Repository, "--check");

        Assert.Equal(0, exitCode);
        Assert.Contains("NO TARGET", output, StringComparison.Ordinal);
        Assert.Contains("CHECK PASSED (exit 0)", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>--check --apply</c> is refused at the parser: a run that repaired the targets it was
    /// measuring could never report anything but success.
    /// </summary>
    [Fact]
    public void Check_together_with_apply_is_refused()
    {
        var exception = Assert.Throws<CliArgumentException>(
            () => MemorySyncOptionsParser.Parse(["--check", "--apply"]));

        Assert.Contains("mutually exclusive", exception.Message, StringComparison.Ordinal);

        // Polarity: each flag on its own parses, so the refusal is about the PAIR and not about either
        // spelling being unknown.
        Assert.True(MemorySyncOptionsParser.Parse(["--check"]).Check);
        Assert.True(MemorySyncOptionsParser.Parse(["--apply"]).Apply);
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
        var (exitCode, output) = await RunForExitAsync(args);

        Assert.Equal(0, exitCode);
        return output;
    }

    /// <summary>
    /// The same run, with the exit code kept rather than asserted away — what the <c>--check</c> arms
    /// need, since the exit code is the whole of what <c>--check</c> adds (#2040).
    /// </summary>
    private async Task<(int ExitCode, string Output)> RunForExitAsync(params string[] args)
    {
        var writer = new StringWriter();
        var exitCode = await MemorySyncCommand.ExecuteAsync(
            MemorySyncOptionsParser.Parse(args),
            writer,
            ClaudeHome,
            TestContext.Current.CancellationToken,
            UserHome);

        return (exitCode, writer.ToString());
    }

    /// <summary>
    /// <c>baton memory import</c> over the same two fixture homes — the other half of the pair the
    /// feedback-loop arm is about. Driven through the real command rather than the plan, because the
    /// loop is between two VERBS and a pure-plan test could not see the file sync wrote.
    /// </summary>
    private async Task<string> ImportAsync()
    {
        var writer = new StringWriter();
        var exitCode = await MemoryImportCommand.ExecuteAsync(
            MemoryImportOptionsParser.Parse([]),
            writer,
            ClaudeHome,
            TestContext.Current.CancellationToken,
            UserHome);

        Assert.Equal(0, exitCode);
        return writer.ToString();
    }

    /// <summary>An operator assertion filing <paramref name="rootDirectory"/> under <see cref="Repository"/>.</summary>
    private static Task AssertAliasAsync(string rootDirectory) =>
        MemoryAliasStore.AppendAsync(
            [new MemoryAliasEntry(BatonPaths.RecordKey(rootDirectory), Repository, "test", default)],
            BatonPaths.MemoryAliasFile,
            TestContext.Current.CancellationToken);

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

        await AssertAliasAsync(root);

        return root;
    }

    private static MemoryProjectionCandidate Vendor(MemoryEntry entry) =>
        new(entry, MemoryFactOrigin.Vendor);

    /// <summary>
    /// An authored entry (#2071) names its asserter where an imported one names its file. The imported
    /// arm is the control: without it this would pass on a projector that printed no provenance at all,
    /// and the claim is that the two READ differently, not that one of them is quiet.
    /// </summary>
    [Fact]
    public void An_authored_entry_names_its_asserter_and_never_its_stand_in_path()
    {
        var authored = AuthoredMemory.Create(
            Repository, "fixture alpha", MemoryKind.DurableFact, "implement/claude/queue-2071", addedAtUtc: default);
        var imported = Entry("feedback_a.md", "fixture beta");

        var text = Encoding.UTF8.GetString(MemoryProjection.Build(
                Repository, "store.jsonl", [Vendor(authored), Vendor(imported)], ProjectionBudget.Default)
            .Bytes);

        Assert.Contains($"authored through `baton memory add` by `implement/claude/queue-2071`", text, StringComparison.Ordinal);
        Assert.DoesNotContain(authored.SourcePath, text, StringComparison.Ordinal);
        Assert.Contains(authored.Id, text, StringComparison.Ordinal);

        // Control: an imported entry still prints the real file it was projected from.
        Assert.Contains($"projected from `{imported.SourcePath}`", text, StringComparison.Ordinal);
    }

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
