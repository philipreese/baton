using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Baton.Accounting;
using Baton.Memory;
using Baton.Status;
using Baton.Tests.Shared;

namespace Baton.Cli.Tests;

/// <summary>
/// #1852 phase B's verb, driven end to end over a fixture Claude home, a fixture user home, a fixture
/// Baton root, and real git checkouts.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every tree here is synthetic.</b> Nothing under the operator's own home is read, written or
/// hashed by this file: the Claude home, the vendor homes and the Baton root are all fixture
/// directories under one temp root, and <c>BatonEnvironmentSnapshot.BeginScope</c> is what points
/// <see cref="BatonPaths.Root"/> at the last of them. The first import of any real memory is the
/// operator's to run.
/// </para>
/// <para>
/// The checkouts are real <c>git init</c> trees with real remotes and a real <c>git worktree add</c>,
/// for the reason <see cref="RepositoryIdentityResolverTests"/> gives: the "two worktrees of one
/// repository import into one store" claim rests entirely on what git answers, and a stubbed probe
/// would assert this file's own expectation back at itself.
/// </para>
/// </remarks>
public sealed class MemoryImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"baton-1852b-{Guid.NewGuid():N}");
    private readonly IDisposable _scope;

    public MemoryImportTests() =>
        _scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = Path.Combine(_root, "baton-root") });

    public void Dispose()
    {
        _scope.Dispose();
        DirectoryCleanup.DeleteRecursively(_root);
    }

    private string ClaudeHome => Path.Combine(_root, "claude");

    private string UserHome => Path.Combine(_root, "home");

    private string Checkout(string name) => Path.Combine(_root, "checkouts", name);

    private async Task<string> RunAsync(params string[] args)
    {
        var (exitCode, text) = await RunRawAsync(args);

        Assert.Equal(0, exitCode);
        return text;
    }

    /// <summary>
    /// The same run without the exit-code assertion, for the arms whose claim IS the exit code. Kept
    /// separate so every other test still fails loudly on a non-zero one rather than reading its output
    /// and never noticing.
    /// </summary>
    private async Task<(int ExitCode, string Output)> RunRawAsync(params string[] args)
    {
        var writer = new StringWriter();
        var exitCode = await MemoryImportCommand.ExecuteAsync(
            MemoryImportOptionsParser.Parse(args),
            writer,
            ClaudeHome,
            TestContext.Current.CancellationToken,
            UserHome);

        return (exitCode, writer.ToString());
    }

    /// <summary>
    /// Every entry in one repository's canonical store, with supersession resolved from its links file
    /// — the read a consumer of a memory performs.
    /// </summary>
    private static Task<IReadOnlyList<MemoryEntry>> StoreAsync(string repository)
    {
        var slug = RepositoryIdentity.FileSlugFor(repository);
        return MemoryStore.ReadResolvedAsync(
            BatonPaths.MemoryEntriesFile(slug),
            BatonPaths.MemoryLinksFile(slug),
            TestContext.Current.CancellationToken);
    }

    /// <summary>The raw supersession rows, for the arms that are about the link file itself.</summary>
    private static Task<IReadOnlyList<MemorySupersessionLink>> LinksAsync(string repository) =>
        MemoryStore.ReadLinksAsync(
            BatonPaths.MemoryLinksFile(RepositoryIdentity.FileSlugFor(repository)),
            TestContext.Current.CancellationToken);

    private void WriteClaudeRoot(string projectDirectoryName, string? cwd, params (string Name, string Content)[] files)
    {
        var project = Path.Combine(ClaudeHome, "projects", projectDirectoryName);
        var memory = Path.Combine(project, "memory");
        Directory.CreateDirectory(memory);

        if (cwd is { Length: > 0 })
        {
            File.WriteAllText(
                Path.Combine(project, "session.jsonl"),
                JsonSerializer.Serialize(new { type = "summary", cwd }) + "\n");
        }

        foreach (var (name, content) in files)
        {
            File.WriteAllText(Path.Combine(memory, name), content);
        }
    }

    private string WriteArchivedRoot(string name, params (string Name, string Content)[] files)
    {
        var root = Path.Combine(ClaudeHome, "memory-archive", "2026-09-03", name);
        Directory.CreateDirectory(root);
        foreach (var (fileName, content) in files)
        {
            File.WriteAllText(Path.Combine(root, fileName), content);
        }

        return root;
    }

    /// <summary>
    /// Path to SHA-256, over every file under <paramref name="directory"/>. The instrument for the
    /// non-destructive claim: it is a statement about bytes, so it is measured in bytes rather than
    /// inferred from the absence of a write call.
    /// </summary>
    private static Dictionary<string, string> DigestTree(string directory) =>
        !Directory.Exists(directory)
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .ToDictionary(p => p, Digest, StringComparer.OrdinalIgnoreCase);

    /// <summary>One file's SHA-256, for the arms whose claim is about a single file's bytes.</summary>
    private static string Digest(string filePath) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(filePath)));

    /// <summary>
    /// The manifest a run wrote, read out of the run's own report rather than by listing the imports
    /// directory: manifest file names are stamped to the millisecond, so picking "the newest file"
    /// would silently pair an undo with the wrong import if two runs ever landed in one tick.
    /// </summary>
    private static string ManifestPathFrom(string output) =>
        output
            .Split('\n')
            .Single(line => line.StartsWith("Manifest: ", StringComparison.Ordinal))["Manifest: ".Length..]
            .Trim();

    // ---------------------------------------------------------------------------------------------
    // The acceptance lines.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// #1852: "Two checkouts of the same repository resolve to the same canonical private memory; an
    /// unrelated repository with the same folder name does not."
    /// </summary>
    /// <remarks>
    /// Both halves in one fixture, because either alone passes on a broken implementation: keying on
    /// the checkout path splits the worktrees (failing the first half while passing the second), and
    /// keying on the folder name merges the two unrelated <c>widget</c>s (passing the first while
    /// failing the second). The two <c>widget</c> directories are deliberately spelled identically.
    /// </remarks>
    [Fact]
    public async Task Two_worktrees_of_one_repository_import_into_one_store_and_a_same_named_unrelated_repository_does_not()
    {
        var main = Checkout("widget");
        var linked = Path.Combine(_root, "linked", "widget");
        await InitGitRepoAsync(main, "https://github.com/philipreese/widget.git");
        await RunGitAsync(main, "worktree", "add", "-q", "-b", "side", linked);

        var unrelated = Path.Combine(_root, "elsewhere", "widget");
        await InitGitRepoAsync(unrelated, "https://github.com/someone-else/widget.git");

        WriteClaudeRoot("C--main", main, ("user_who.md", "from the main checkout"));
        WriteClaudeRoot("C--linked", linked, ("project_plan.md", "from the linked worktree"));
        WriteClaudeRoot("C--unrelated", unrelated, ("user_who.md", "a different project entirely"));

        await RunAsync();

        var ours = await StoreAsync("github.com/philipreese/widget");
        Assert.Equal(2, ours.Count);
        Assert.Equal(
            ["from the linked worktree", "from the main checkout"],
            ours.Select(e => e.Text).Order(StringComparer.Ordinal));

        var theirs = await StoreAsync("github.com/someone-else/widget");
        var single = Assert.Single(theirs);
        Assert.Equal("a different project entirely", single.Text);

        // The negative, stated as bytes rather than as a count: neither store holds the other's text.
        Assert.DoesNotContain(ours, e => e.Text.Contains("different project", StringComparison.Ordinal));
        Assert.DoesNotContain(theirs, e => e.Text.Contains("checkout", StringComparison.Ordinal));
    }

    /// <summary>
    /// #1852: "Migration leaves every original memory file intact." Hashed before and after, over the
    /// whole fixture Claude home and user home — not just the files that produced entries.
    /// </summary>
    /// <remarks>
    /// <b>The population is the point, and it is four kinds of file on purpose.</b> Live Claude roots
    /// (imported), an ARCHIVED root (imported, under an assertion, as historical notes), Codex markdown
    /// (imported from the other home), and a Codex <c>memories_*.sqlite</c> — the last being exactly
    /// where "located, digested, never opened" is the load-bearing claim, and the one an earlier
    /// version of this test did not cover at all. Both homes are asserted non-empty before the
    /// comparison, because comparing an empty dictionary to an empty dictionary asserts nothing and is
    /// what the vendor-home arm was previously doing.
    /// </remarks>
    [Fact]
    public async Task Every_source_file_is_byte_identical_after_an_import()
    {
        await BuildStandardFixtureAsync();
        var archived = WriteArchivedRoot("c--baton-memory", ("user_who.md", "the older who"));

        var memories = Path.Combine(UserHome, ".codex", "memories");
        Directory.CreateDirectory(memories);
        File.WriteAllText(Path.Combine(memories, "raw_memories.md"), "a codex memory");
        File.WriteAllText(Path.Combine(UserHome, ".codex", "memories_1.sqlite"), "synthetic-not-a-database");

        var claudeBefore = DigestTree(ClaudeHome);
        var homeBefore = DigestTree(UserHome);
        Assert.NotEmpty(claudeBefore);
        Assert.NotEmpty(homeBefore);
        Assert.Contains(homeBefore.Keys, p => p.EndsWith("memories_1.sqlite", StringComparison.Ordinal));

        await RunAsync(
            "--assert", $"{archived}=github.com/philipreese/baton",
            "--assert", $"{memories}=github.com/philipreese/baton",
            "--asserted-by", "the-test");

        Assert.Equal(claudeBefore, DigestTree(ClaudeHome));
        Assert.Equal(homeBefore, DigestTree(UserHome));

        // The controls: the run must actually have carried the archived root and the Codex markdown,
        // or the assertions above are a statement about an import that did nothing.
        var store = await StoreAsync("github.com/philipreese/baton");
        Assert.Contains(store, e => e.Kind == MemoryKind.HistoricalNote);
        Assert.Contains(store, e => e.SourceVendor == "codex");
    }

    /// <summary>#1852: re-running the import over an unchanged tree appends nothing.</summary>
    [Fact]
    public async Task Re_importing_an_unchanged_tree_is_a_no_op()
    {
        await BuildStandardFixtureAsync();

        await RunAsync();
        var afterFirst = await StoreAsync("github.com/philipreese/baton");

        var second = await RunAsync();
        var afterSecond = await StoreAsync("github.com/philipreese/baton");

        Assert.Equal(afterFirst.Select(e => e.Id), afterSecond.Select(e => e.Id));
        Assert.Contains($"appended: 0", second, StringComparison.Ordinal);
        Assert.Contains($"already present: {afterFirst.Count}", second, StringComparison.Ordinal);

        // Polarity: editing a source produces one more row, so the no-op above is a statement about
        // unchanged content rather than about a store that has stopped accepting anything.
        File.WriteAllText(
            Path.Combine(ClaudeHome, "projects", "C--baton", "memory", "user_who.md"), "edited");
        await RunAsync();
        Assert.Equal(afterFirst.Count + 1, (await StoreAsync("github.com/philipreese/baton")).Count);
    }

    /// <summary>
    /// <c>--dry-run</c> writes nothing at all — no entry AND no manifest — with the control arm
    /// asserting the same fixture writes both without it.
    /// </summary>
    [Fact]
    public async Task A_dry_run_writes_no_entry_and_no_manifest()
    {
        await BuildStandardFixtureAsync();

        var text = await RunAsync("--dry-run");

        Assert.Contains("NOTHING WAS WRITTEN", text, StringComparison.Ordinal);
        Assert.Contains("would append: 3", text, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(BatonPaths.Root, BatonPaths.MemoryImportsDirectoryName)));
        Assert.Empty(await StoreAsync("github.com/philipreese/baton"));

        // The control arm. Without it "wrote nothing" would also pass on an import that found nothing.
        await RunAsync();
        Assert.Equal(3, (await StoreAsync("github.com/philipreese/baton")).Count);
        Assert.Single(Directory.GetFiles(Path.Combine(BatonPaths.Root, BatonPaths.MemoryImportsDirectoryName)));
    }

    /// <summary>
    /// #1852: "emits a reversible import manifest". The discriminating shape is two imports and one
    /// undo — an undo that merely emptied the store file would pass a single-import test and destroy
    /// the earlier import's entries here.
    /// </summary>
    [Fact]
    public async Task The_manifest_replays_to_a_full_undo_and_leaves_an_earlier_imports_entries_alone()
    {
        await BuildStandardFixtureAsync();
        var firstRoot = Path.Combine(ClaudeHome, "projects", "C--baton", "memory");
        var secondRoot = Path.Combine(ClaudeHome, "projects", "C--baton-worktree", "memory");

        await RunAsync("--root", firstRoot);
        var afterFirst = (await StoreAsync("github.com/philipreese/baton")).Select(e => e.Id).ToList();

        await RunAsync("--root", secondRoot);
        var afterSecond = await StoreAsync("github.com/philipreese/baton");
        Assert.True(afterSecond.Count > afterFirst.Count);

        var manifests = Directory
            .GetFiles(Path.Combine(BatonPaths.Root, BatonPaths.MemoryImportsDirectoryName))
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(2, manifests.Count);

        var claudeBefore = DigestTree(ClaudeHome);
        var undone = await RunAsync("--undo", manifests[1]);

        Assert.Contains("No source memory file was touched", undone, StringComparison.Ordinal);
        Assert.Equal(claudeBefore, DigestTree(ClaudeHome));
        Assert.Equal(afterFirst, (await StoreAsync("github.com/philipreese/baton")).Select(e => e.Id));

        // And the undone import can be replayed: the store returns to its post-second-import state,
        // which is what "reversible" has to mean if it is not to mean "deleted".
        await RunAsync("--root", secondRoot);
        Assert.Equal(
            afterSecond.Select(e => e.Id).Order(StringComparer.Ordinal),
            (await StoreAsync("github.com/philipreese/baton")).Select(e => e.Id).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Q2 (operator, 2026-09-05): archived roots import as historical notes, linked to the live entry
    /// that supersedes them where a filename is shared and the content differs.
    /// </summary>
    /// <remarks>
    /// Three arms, because the link is a claim in three parts and each half alone passes on a broken
    /// rule: a shared name with DIFFERENT content links, a shared name with IDENTICAL content does
    /// not (that is one fact stored twice, not a replacement), and a shared name under a DIFFERENT
    /// repository does not (which is the cross-subject leak the per-repository layout exists to
    /// prevent, reintroduced by a name match).
    /// </remarks>
    [Fact]
    public async Task An_archived_root_imports_as_historical_notes_linked_only_to_a_live_entry_of_the_same_subject()
    {
        await BuildStandardFixtureAsync();

        var archived = WriteArchivedRoot(
            "c--baton-memory",
            ("user_who.md", "the older who"),          // same name, different content -> superseded
            ("project_plan.md", "the plan"),           // same name, IDENTICAL content -> not superseded
            ("feedback_style.md", "archived only"));   // no live counterpart at all

        var otherArchive = WriteArchivedRoot("c--other-memory", ("user_who.md", "someone else's older who"));

        await RunAsync(
            "--assert", $"{archived}=github.com/philipreese/baton",
            "--assert", $"{otherArchive}=github.com/philipreese/other",
            "--asserted-by", "the-test");

        var store = await StoreAsync("github.com/philipreese/baton");
        var notes = store.Where(e => e.Kind == MemoryKind.HistoricalNote).ToList();
        Assert.Equal(3, notes.Count);
        Assert.All(notes, n => Assert.Equal(MemoryKindSource.InferredFromArchive, n.KindSource));

        var supersededNote = Assert.Single(notes, n => n.Text == "the older who");
        var liveWho = Assert.Single(store, e => e.Text == "who we are");
        Assert.Equal([liveWho.Id], supersededNote.SupersededBy);
        Assert.Equal([supersededNote.Id], liveWho.Supersedes);

        // Identical content: one fact stored twice, so no supersession either way.
        Assert.Null(Assert.Single(notes, n => n.Text == "the plan").SupersededBy);
        Assert.Null(Assert.Single(store, e => e.Text == "the plan" && e.Kind != MemoryKind.HistoricalNote).Supersedes);

        // Cross-subject: same filename, different repository, no link in either direction.
        var other = Assert.Single(await StoreAsync("github.com/philipreese/other"));
        Assert.Null(other.SupersededBy);
        Assert.DoesNotContain(store, e => e.Supersedes?.Contains(other.Id) == true);
    }

    /// <summary>
    /// Q2's link lands when the two halves arrive in SEPARATE imports — the shape the PR body itself
    /// prescribes (import, see the archived roots reported unfiled, then re-run with <c>--assert</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The discriminating shape is two <c>RunAsync</c> calls, not one. A single combined run passes on
    /// an implementation that computes links over one run's plan and writes them onto the entry rows:
    /// the live entry's id does not change when it supersedes something, so an append-only store skips
    /// the recomputed row and the live half of the link is discarded forever. Both directions are
    /// asserted, because the archived half lands in run 2 either way and only the live half is lost.
    /// </para>
    /// <para>
    /// The third run is the idempotence arm: recomputing an already-recorded link must append no second
    /// row, which is the property the link's id (the pair) buys and the one a naive append would fail.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_supersession_link_lands_when_the_live_root_and_the_archive_are_imported_in_separate_runs()
    {
        await BuildStandardFixtureAsync();
        var archived = WriteArchivedRoot("c--baton-memory", ("user_who.md", "the older who"));

        // Run 1: the live roots only. The archive has no derivable subject yet, so it is unfiled.
        var first = await RunAsync();
        Assert.Contains("Unfiled: 1", first, StringComparison.Ordinal);
        Assert.Empty(await LinksAsync("github.com/philipreese/baton"));

        // Run 2: the archive, under an assertion. The live entries are already in the store and are
        // skipped by the append -- which is exactly why the link cannot live on their rows.
        var second = await RunAsync(
            "--assert", $"{archived}=github.com/philipreese/baton", "--asserted-by", "the-test");
        Assert.Contains("Supersession links: 1   recorded: 1", second, StringComparison.Ordinal);

        var store = await StoreAsync("github.com/philipreese/baton");
        var note = Assert.Single(store, e => e.Text == "the older who");
        var live = Assert.Single(store, e => e.Text == "who we are");
        Assert.Equal([live.Id], note.SupersededBy);
        Assert.Equal([note.Id], live.Supersedes);

        // Run 3: re-importing everything recomputes the same link and appends nothing.
        var third = await RunAsync();
        Assert.Contains("Supersession links: 1   recorded: 0   already recorded: 1", third, StringComparison.Ordinal);
        var link = Assert.Single(await LinksAsync("github.com/philipreese/baton"));
        Assert.Equal(live.Id, link.SupersedingId);
        Assert.Equal(note.Id, link.SupersededId);

        // Polarity, on the same store: the archived file with no live counterpart is linked to nothing,
        // so the assertions above are about a matched pair rather than about every archived entry.
        Assert.All(
            store.Where(e => e.Kind == MemoryKind.HistoricalNote && e.Text != "the older who"),
            e => Assert.Null(e.SupersededBy));
    }

    /// <summary>
    /// The second failure shape the review names: one <c>--root</c> run per root, where NEITHER half of
    /// the link is in the other's plan.
    /// </summary>
    /// <remarks>
    /// Distinct from the test above, which imports everything in run 2 and so still had both entries in
    /// one plan — enough to expose the append skip, but not enough to prove where the link population
    /// comes from. Here run 2's plan holds the archived entry and nothing else, so the live half can
    /// only come from the store. That is the arm that discriminates between "the union is right" and
    /// "the union happened to be sufficient".
    /// </remarks>
    [Fact]
    public async Task A_supersession_link_lands_when_each_root_is_imported_under_its_own_root_flag()
    {
        await BuildStandardFixtureAsync();
        var archived = WriteArchivedRoot("c--baton-memory", ("user_who.md", "the older who"));

        await RunAsync("--root", Path.Combine(ClaudeHome, "projects", "C--baton", "memory"));
        await RunAsync(
            "--root", archived, "--assert", $"{archived}=github.com/philipreese/baton",
            "--asserted-by", "the-test");

        var store = await StoreAsync("github.com/philipreese/baton");
        var note = Assert.Single(store, e => e.Text == "the older who");
        var live = Assert.Single(store, e => e.Text == "who we are");
        Assert.Equal([live.Id], note.SupersededBy);
        Assert.Equal([note.Id], live.Supersedes);
    }

    /// <summary>
    /// An undo that removed nothing exits non-zero and says so, and one replayed against a different
    /// storage root refuses before touching anything.
    /// </summary>
    /// <remarks>
    /// Both arms carry a control in the same fixture: the undo that DOES reverse its import exits 0
    /// with no INCOMPLETE line, so neither refusal is a command that has simply stopped succeeding.
    /// </remarks>
    [Fact]
    public async Task An_undo_that_reversed_nothing_fails_and_one_against_a_different_storage_root_refuses()
    {
        await BuildStandardFixtureAsync();
        await RunAsync();

        var manifestPath = Assert.Single(
            Directory.GetFiles(Path.Combine(BatonPaths.Root, BatonPaths.MemoryImportsDirectoryName)));

        // Arm 1: the manifest says it was written under another storage root. Every store path in it is
        // absolute under that root, so replaying it here could only remove nothing and report success.
        var elsewhere = Path.Combine(_root, "some-other-baton-root");
        var moved = ImportManifest.Read(manifestPath) with { BatonRoot = elsewhere };
        moved.Write(manifestPath + ".moved.json");

        var (refusedCode, refusedText) = await RunRawAsync("--undo", manifestPath + ".moved.json");
        Assert.Equal(1, refusedCode);
        Assert.Contains("REFUSED", refusedText, StringComparison.Ordinal);
        Assert.Contains(elsewhere, refusedText, StringComparison.Ordinal);
        Assert.Equal(3, (await StoreAsync("github.com/philipreese/baton")).Count);

        // Arm 2 (control): the real manifest, against the root it was written under, reverses in full.
        var (okCode, okText) = await RunRawAsync("--undo", manifestPath);
        Assert.Equal(0, okCode);
        Assert.DoesNotContain("INCOMPLETE", okText, StringComparison.Ordinal);
        Assert.Empty(await StoreAsync("github.com/philipreese/baton"));

        // Arm 3: replaying the same manifest a second time now removes nothing -- the store file is
        // still there and simply no longer holds those rows. Success-shaped output with exit 0 is what
        // this used to print.
        var (againCode, againText) = await RunRawAsync("--undo", manifestPath);
        Assert.Equal(1, againCode);
        Assert.Contains("INCOMPLETE", againText, StringComparison.Ordinal);
        Assert.Contains("expected 3, removed 0", againText, StringComparison.Ordinal);
    }

    /// <summary>
    /// #1947: an undo removes exactly the supersession rows ITS import appended — an earlier import's
    /// link survives it — and a manifest that appended no link at all leaves <c>links.jsonl</c>
    /// byte-identical.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The discriminating shape is a links file holding TWO rows recorded by two different imports,
    /// undone through the manifest of the second. Run 2 is deliberately unfiltered so its manifest
    /// carries the earlier link as <c>alreadyPresent</c> beside the one it appended: that is what makes
    /// <c>ImportManifest.AppendedLinks</c>'s filter load-bearing, and an undo iterating <c>Links</c>
    /// instead would tear out an earlier import's link while still passing any single-link test. The
    /// manifest's own shape is asserted first, because without that already-present row the removal
    /// arm below would be measuring nothing.
    /// </para>
    /// <para>
    /// The control is the other polarity, and carries a positive control of its own: the run appends
    /// entries and no link, its undo exits 0 with no INCOMPLETE line, and the entries it appended are
    /// gone from the store afterwards — so "<c>links.jsonl</c> is byte-identical" is a statement about
    /// a real undo rather than about one that did nothing. It runs while the links file already holds
    /// a row, because a digest of an absent file compared to an absent file asserts nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task An_undo_removes_only_the_supersession_links_its_own_import_appended()
    {
        const string repository = "github.com/philipreese/baton";
        await BuildStandardFixtureAsync();
        var archivedWho = WriteArchivedRoot("c--baton-memory", ("user_who.md", "the older who"));
        var archivedPlan = WriteArchivedRoot("c--plan-memory", ("project_plan.md", "an older plan"));

        // Run 1: the live roots plus the first archive -- link A, from an import that is never undone.
        var first = await RunAsync(
            "--assert", $"{archivedWho}={repository}", "--asserted-by", "the-test");
        Assert.Contains("Supersession links: 1   recorded: 1", first, StringComparison.Ordinal);
        var linkA = Assert.Single(await LinksAsync(repository));

        // Run 2: the second archive as well. Unfiltered, so link A is recomputed and lands in this
        // manifest marked already-present -- the row the undo must leave alone.
        var second = await RunAsync(
            "--assert", $"{archivedWho}={repository}",
            "--assert", $"{archivedPlan}={repository}",
            "--asserted-by", "the-test");
        Assert.Contains(
            "Supersession links: 2   recorded: 1   already recorded: 1", second, StringComparison.Ordinal);

        var manifestPath = ManifestPathFrom(second);
        var manifest = ImportManifest.Read(manifestPath);
        Assert.Equal(2, (manifest.Links ?? []).Count);
        Assert.Equal(linkA.Id, Assert.Single(manifest.Links!, l => l.AlreadyPresent).LinkId);
        var linkB = Assert.Single(manifest.AppendedLinks);
        Assert.Equal(2, (await LinksAsync(repository)).Count);

        var undone = await RunAsync("--undo", manifestPath);
        Assert.DoesNotContain("INCOMPLETE", undone, StringComparison.Ordinal);

        // Exactly one row removed, and it is link B: link A is still there, whole and unchanged. The
        // file is read before the report line is, so this is what goes red on an undo that removed the
        // wrong row rather than a report that miscounted a correct one.
        var remaining = Assert.Single(await LinksAsync(repository));
        Assert.Equal(linkA.Id, remaining.Id);
        Assert.Equal(linkA.SupersedingId, remaining.SupersedingId);
        Assert.Equal(linkA.SupersededId, remaining.SupersededId);
        Assert.NotEqual(linkB.LinkId, remaining.Id);
        Assert.Contains("1 supersession link(s)", undone, StringComparison.Ordinal);

        // And link A still resolves on both sides, which is what "removed nothing else" has to mean
        // for a reader of the store rather than for a reader of the links file.
        var store = await StoreAsync(repository);
        var note = Assert.Single(store, e => e.Text == "the older who");
        var live = Assert.Single(store, e => e.Text == "who we are");
        Assert.Equal([live.Id], note.SupersededBy);
        Assert.Equal([note.Id], live.Supersedes);
        Assert.DoesNotContain(store, e => e.Text == "an older plan");

        // ---- The control: an import that appended no link leaves links.jsonl byte-identical. ----
        var linksFile = BatonPaths.MemoryLinksFile(RepositoryIdentity.FileSlugFor(repository));
        var linksBefore = Digest(linksFile);

        var archivedNotes = WriteArchivedRoot("c--extra-memory", ("notes_extra.md", "no live counterpart"));
        var third = await RunAsync(
            "--root", archivedNotes, "--assert", $"{archivedNotes}={repository}", "--asserted-by", "the-test");
        var controlManifest = ImportManifest.Read(ManifestPathFrom(third));
        Assert.Empty(controlManifest.AppendedLinks);
        var appendedIds = controlManifest.Appended.Select(r => r.EntryId).ToList();
        Assert.NotEmpty(appendedIds);

        var (controlCode, controlText) = await RunRawAsync("--undo", ManifestPathFrom(third));
        Assert.Equal(0, controlCode);
        Assert.DoesNotContain("INCOMPLETE", controlText, StringComparison.Ordinal);
        Assert.Contains("0 supersession link(s)", controlText, StringComparison.Ordinal);

        // The positive control: that undo really did remove its entries...
        Assert.DoesNotContain(
            await StoreAsync(repository), e => appendedIds.Contains(e.Id, StringComparer.Ordinal));

        // ...and the links file it had no business touching is unchanged, byte for byte.
        Assert.Equal(linksBefore, Digest(linksFile));
    }

    /// <summary>
    /// Two spellings of one repository asserted across two roots produce ONE store file, because the
    /// asserted identity goes through the same canonicalization a git probe's answer does.
    /// </summary>
    [Fact]
    public async Task Two_spellings_of_one_asserted_repository_resolve_to_one_store()
    {
        var first = WriteArchivedRoot("c--one-memory", ("user_who.md", "from the first root"));
        var second = WriteArchivedRoot("c--two-memory", ("project_plan.md", "from the second root"));

        await RunAsync(
            "--assert", $"{first}=GitHub.com/PhilipReese/Widget",
            "--assert", $"{second}=https://github.com/philipreese/widget.git",
            "--asserted-by", "the-test");

        Assert.Equal(2, (await StoreAsync("github.com/philipreese/widget")).Count);

        // The negative, as bytes on disk: one repository directory under the storage root, not two.
        var stores = Directory
            .EnumerateFiles(BatonPaths.Root, BatonPaths.MemoryEntriesFileName, SearchOption.AllDirectories)
            .ToList();
        Assert.Single(stores);
    }

    /// <summary>
    /// The evening ruling of 2026-09-05: the Codex family imported is the MARKDOWN memories directory;
    /// the sqlite store is machinery, recorded for provenance and never read as a memory source.
    /// </summary>
    [Fact]
    public async Task Codex_markdown_imports_under_an_asserted_subject_and_the_sqlite_store_is_machinery_only()
    {
        var memories = Path.Combine(UserHome, ".codex", "memories");
        Directory.CreateDirectory(memories);
        File.WriteAllText(Path.Combine(memories, "raw_memories.md"), "a codex memory");
        File.WriteAllText(Path.Combine(UserHome, ".codex", "memories_1.sqlite"), "synthetic-not-a-database");

        await RunAsync("--assert", $"{memories}=github.com/philipreese/baton", "--asserted-by", "the-test");

        var imported = Assert.Single(await StoreAsync("github.com/philipreese/baton"));
        Assert.Equal("a codex memory", imported.Text);
        Assert.Equal("codex", imported.SourceVendor);

        var manifest = ImportManifest.Read(
            Directory.GetFiles(Path.Combine(BatonPaths.Root, BatonPaths.MemoryImportsDirectoryName)).Single());

        // The sqlite store is accounted for, with a digest, and produced no entry. Both halves matter:
        // absent from the manifest it would look unseen, and present among the entries it would have
        // been imported as though its bytes were a memory.
        var machinery = Assert.Single(manifest.Machinery);
        Assert.EndsWith("memories_1.sqlite", machinery.SourcePath, StringComparison.Ordinal);
        Assert.NotEmpty(machinery.Sha256);
        Assert.DoesNotContain(manifest.Entries, e => e.SourcePath.EndsWith(".sqlite", StringComparison.Ordinal));
    }

    /// <summary>
    /// A Baton projection sitting in an importable root is skipped, and the skip SURVIVES THE MANIFEST
    /// — read back off disk, the same round trip <see cref="ImportManifest.Machinery"/> gets, because a
    /// population that only exists in a live object is one an undo or an audit can never see.
    /// </summary>
    /// <remarks>
    /// The behaviour end to end (sync writes it, import refuses it, store bytes unchanged across two
    /// cycles) is <c>MemoryProjectionTests</c>'s, which owns the pair of verbs. What this arm adds is
    /// the serialization: the row, its digest, and the two negatives beside it — no entry from the
    /// file, and nothing in <c>unfiled</c>, which is a different population meaning a different thing.
    /// </remarks>
    [Fact]
    public async Task A_projection_in_an_importable_root_is_skipped_and_the_manifest_records_it()
    {
        WriteClaudeRoot(
            "C--projected", Checkout("projected"),
            ("user_real.md", "a memory a person wrote"),
            (ClaudeProjectionTarget.ProjectionFileName, MemoryProjection.FormatMarker + "\n# a cache\n"));
        var rootDirectory = Path.Combine(ClaudeHome, "projects", "C--projected", "memory");

        var text = await RunAsync(
            "--assert", $"{rootDirectory}=github.com/philipreese/projected", "--asserted-by", "the-test");

        Assert.Contains("projection-skipped: 1", text, StringComparison.Ordinal);
        Assert.Contains("Unfiled: 0", text, StringComparison.Ordinal);

        var manifest = ImportManifest.Read(
            Directory.GetFiles(Path.Combine(BatonPaths.Root, BatonPaths.MemoryImportsDirectoryName)).Single());

        var skipped = Assert.Single(manifest.ProjectionsSkipped!);
        Assert.EndsWith(ClaudeProjectionTarget.ProjectionFileName, skipped.SourcePath, StringComparison.Ordinal);
        Assert.NotEmpty(skipped.Sha256);
        Assert.Empty(manifest.Unfiled);

        // The control that keeps this from passing over an import that read nothing at all: the
        // ordinary memory in the SAME root did import.
        Assert.Equal(
            "a memory a person wrote",
            Assert.Single(await StoreAsync("github.com/philipreese/projected")).Text);
        Assert.DoesNotContain(
            manifest.Entries,
            e => e.SourcePath.EndsWith(ClaudeProjectionTarget.ProjectionFileName, StringComparison.Ordinal));
    }

    /// <summary>
    /// The marker is a test on CONTENT: a projection carrying an ordinary memory's filename is skipped
    /// all the same. Nothing else in either suite discriminates this from a filename comparison, since
    /// every other fixture writes the projection under
    /// <see cref="ClaudeProjectionTarget.ProjectionFileName"/> — so a rewrite of
    /// <see cref="MemoryProjection.IsProjectedFile"/> into a name test would pass them and fail here.
    /// </summary>
    /// <remarks>
    /// The case is not hypothetical: an operator who copies or renames a projection (or a backup tool
    /// that does) reintroduces the feedback loop under any name-based rule, and the rule's own remarks
    /// claim this coverage in three places.
    /// </remarks>
    [Fact]
    public async Task A_projection_under_an_ordinary_filename_is_skipped_on_its_marker()
    {
        WriteClaudeRoot(
            "C--renamed", Checkout("renamed"),
            ("user_real.md", "a memory a person wrote"),
            ("notes.md", MemoryProjection.FormatMarker + "\n# a cache someone renamed\n"));
        var rootDirectory = Path.Combine(ClaudeHome, "projects", "C--renamed", "memory");

        var text = await RunAsync(
            "--assert", $"{rootDirectory}=github.com/philipreese/renamed", "--asserted-by", "the-test");

        Assert.Contains("projection-skipped: 1", text, StringComparison.Ordinal);

        var manifest = ImportManifest.Read(
            Directory.GetFiles(Path.Combine(BatonPaths.Root, BatonPaths.MemoryImportsDirectoryName)).Single());

        var skipped = Assert.Single(manifest.ProjectionsSkipped!);
        Assert.EndsWith("notes.md", skipped.SourcePath, StringComparison.Ordinal);
        Assert.DoesNotContain(
            manifest.Entries, e => e.SourcePath.EndsWith("notes.md", StringComparison.Ordinal));

        // The control, without which "no entry from notes.md" is indistinguishable from an import that
        // read nothing at all: the ordinary memory in the SAME root did import.
        Assert.Equal(
            "a memory a person wrote",
            Assert.Single(await StoreAsync("github.com/philipreese/renamed")).Text);
    }

    /// <summary>
    /// A root with no derivable repository is reported unfiled and imported nowhere — and the same
    /// root WITH an assertion imports, which is what makes the first half a refusal rather than a
    /// blind spot.
    /// </summary>
    [Fact]
    public async Task A_root_with_no_derivable_repository_is_unfiled_until_an_operator_asserts_one()
    {
        WriteClaudeRoot("C--gone", Checkout("never-created"), ("user_who.md", "orphaned"));
        var rootDirectory = Path.Combine(ClaudeHome, "projects", "C--gone", "memory");

        var text = await RunAsync();
        Assert.Contains("Unfiled: 1", text, StringComparison.Ordinal);
        Assert.Contains("the checkout this memory belongs to is gone", text, StringComparison.Ordinal);
        Assert.Contains("--assert", text, StringComparison.Ordinal);

        // "Imported nowhere", stated as the store itself. The earlier spelling of this control asserted
        // a directory path with no digest suffix, which no slug can ever produce (RepositoryIdentity
        // .BuildFileSlug), so it could not have gone red on a wrong import either.
        Assert.Empty(await StoreAsync("github.com/philipreese/rescued"));

        await RunAsync("--assert", $"{rootDirectory}=github.com/philipreese/rescued", "--asserted-by", "the-test");

        Assert.Equal("orphaned", Assert.Single(await StoreAsync("github.com/philipreese/rescued")).Text);

        // The assertion is durable: a later run needs no flag, because the alias store recorded it.
        var aliases = await MemoryAliasStore.ReadAllAsync(
            BatonPaths.MemoryAliasFile, TestContext.Current.CancellationToken);
        Assert.Equal("the-test", Assert.Single(aliases).AssertedBy);
    }

    /// <summary>
    /// An assertion may never displace a repository git actually answered for — <c>MemoryAliasStore</c>'s
    /// central rule, and the one that keeps a row's <c>repository</c> readable as a measurement.
    /// </summary>
    [Fact]
    public async Task An_assertion_cannot_override_a_repository_git_answered_for()
    {
        var checkout = Checkout("probed");
        await InitGitRepoAsync(checkout, "https://github.com/philipreese/probed.git");
        WriteClaudeRoot("C--probed", checkout, ("user_who.md", "measured, not asserted"));
        var rootDirectory = Path.Combine(ClaudeHome, "projects", "C--probed", "memory");

        await RunAsync("--assert", $"{rootDirectory}=github.com/philipreese/hijacked", "--asserted-by", "the-test");

        Assert.Equal("measured, not asserted", Assert.Single(await StoreAsync("github.com/philipreese/probed")).Text);
        Assert.Empty(await StoreAsync("github.com/philipreese/hijacked"));
    }

    /// <summary>
    /// Kind is declared, else inferred from the filename prefix and labelled as inferred, else
    /// unknown — and never read out of the body.
    /// </summary>
    [Fact]
    public async Task An_entrys_kind_is_declared_then_inferred_from_its_prefix_then_unknown()
    {
        var checkout = Checkout("kinds");
        await InitGitRepoAsync(checkout, "https://github.com/philipreese/kinds.git");
        WriteClaudeRoot(
            "C--kinds",
            checkout,
            ("feedback_declared.md", "---\nname: x\nmetadata:\n  type: reference\n---\n\nbody"),
            ("feedback_inferred.md", "no front matter here"),
            ("MEMORY.md", "an index, whose name is no prefix at all"),
            // The body says "kind: operator-preference" and the front matter does not exist, so a
            // reader that scanned the text rather than a front-matter block would mislabel this.
            ("notes.md", "kind: operator-preference\n"));

        await RunAsync();
        var store = await StoreAsync("github.com/philipreese/kinds");

        var declared = Assert.Single(store, e => e.SourcePath.EndsWith("feedback_declared.md", StringComparison.Ordinal));
        Assert.Equal(MemoryKindSource.Declared, declared.KindSource);
        Assert.Equal(MemoryKind.DurableFact, declared.Kind); // the DECLARATION wins over the feedback_ prefix

        var inferred = Assert.Single(store, e => e.SourcePath.EndsWith("feedback_inferred.md", StringComparison.Ordinal));
        Assert.Equal(MemoryKindSource.InferredFromPrefix, inferred.KindSource);
        Assert.Equal(MemoryKind.OperatorPreference, inferred.Kind);

        foreach (var name in new[] { "MEMORY.md", "notes.md" })
        {
            var unknown = Assert.Single(store, e => e.SourcePath.EndsWith(name, StringComparison.Ordinal));
            Assert.Equal(MemoryKindSource.Unknown, unknown.KindSource);
            Assert.Equal(MemoryKind.Unknown, unknown.Kind);
        }
    }

    /// <summary>
    /// <c>--root</c> selects from the discovered population and cannot add to it — the property that
    /// keeps <c>MemoryRootInventory</c> the single definition of "a memory root".
    /// </summary>
    [Fact]
    public async Task Root_selects_a_discovered_root_and_refuses_an_undiscovered_directory()
    {
        await BuildStandardFixtureAsync();
        Directory.CreateDirectory(Path.Combine(UserHome, ".codex"));
        File.WriteAllText(Path.Combine(UserHome, ".codex", "memories_1.sqlite"), "synthetic-not-a-database");
        var selected = Path.Combine(ClaudeHome, "projects", "C--baton", "memory");

        await RunAsync("--root", selected);
        Assert.Equal(2, (await StoreAsync("github.com/philipreese/baton")).Count);

        // The filter narrows the manifest's MACHINERY rows too. Unfiltered, a run asked to look at one
        // Claude root recorded every Codex sqlite file on the machine as though it had looked at those.
        var manifest = ImportManifest.Read(Assert.Single(
            Directory.GetFiles(Path.Combine(BatonPaths.Root, BatonPaths.MemoryImportsDirectoryName))));
        Assert.Empty(manifest.Machinery);

        // Control: the same fixture, unfiltered, does record it -- so the assertion above is about the
        // filter rather than about a machinery row that never existed.
        await RunAsync();
        var unfiltered = Directory
            .GetFiles(Path.Combine(BatonPaths.Root, BatonPaths.MemoryImportsDirectoryName))
            .Order(StringComparer.Ordinal)
            .Select(ImportManifest.Read)
            .Last();
        Assert.Single(unfiltered.Machinery);

        var undiscovered = Path.Combine(_root, "not-a-root");
        Directory.CreateDirectory(undiscovered);
        var refused = await Assert.ThrowsAsync<CliArgumentException>(() => RunAsync("--root", undiscovered));
        Assert.Contains("matches no memory root this verb can import", refused.Message, StringComparison.Ordinal);
        Assert.Contains("Antigravity", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Help_states_the_import_is_non_destructive_and_names_what_it_will_not_guess()
    {
        var text = await RunAsync("--help");

        Assert.Contains("NON-DESTRUCTIVE BY CONSTRUCTION", text, StringComparison.Ordinal);
        Assert.Contains("READ-ONLY", text, StringComparison.Ordinal);
        Assert.Contains("it will not guess a subject", text, StringComparison.Ordinal);
        Assert.Contains("NOT a memory source", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_parser_refuses_undo_combined_with_anything_else_and_a_malformed_assertion()
    {
        var combined = Assert.Throws<CliArgumentException>(
            () => MemoryImportOptionsParser.Parse(["--undo", "m.json", "--dry-run"]));
        Assert.Contains("takes no other options", combined.Message, StringComparison.Ordinal);

        var malformed = Assert.Throws<CliArgumentException>(
            () => MemoryImportOptionsParser.Parse(["--assert", "no-equals-sign"]));
        Assert.Contains("is not a '<path>=<repository>' pair", malformed.Message, StringComparison.Ordinal);

        // Control: the well-formed spellings parse, so the arms above are keyed on what they claim.
        var ok = MemoryImportOptionsParser.Parse(
            ["--dry-run", "--root", "r", "--assert", @"C:\a=github.com/b/c", "--asserted-by", "me"]);
        Assert.True(ok.DryRun);
        Assert.Equal(["r"], ok.Roots);
        Assert.Equal(new MemoryImportAssertion(@"C:\a", "github.com/b/c"), Assert.Single(ok.Assertions));
        Assert.Equal("me", ok.AssertedBy);

        Assert.Throws<CliArgumentException>(() => MemoryImportOptionsParser.Parse(["--frobnicate"]));
        Assert.Throws<CliArgumentException>(() => MemoryImportOptionsParser.Parse(["--root"]));
    }

    /// <summary>
    /// An asserted repository is canonicalized at the parser, so every spelling of one repository is
    /// one identity — and a string with no identity in it is refused rather than made into a store.
    /// </summary>
    [Theory]
    [InlineData("github.com/owner/repo")]
    [InlineData("GitHub.com/Owner/Repo")]
    [InlineData("https://GitHub.com/Owner/Repo.git")]
    [InlineData("git@github.com:Owner/Repo.git")]
    [InlineData(" github.com/owner/repo/ ")]
    public void An_asserted_repository_is_canonicalized_to_one_identity(string spelling)
    {
        var parsed = MemoryImportOptionsParser.Parse(["--assert", $@"C:\root={spelling}"]);

        Assert.Equal("github.com/owner/repo", Assert.Single(parsed.Assertions).Repository);
    }

    [Fact]
    public void An_asserted_repository_that_canonicalizes_to_nothing_is_refused()
    {
        var refused = Assert.Throws<CliArgumentException>(
            () => MemoryImportOptionsParser.Parse(["--assert", @"C:\root=hello world"]));
        Assert.Contains("is not a repository identity", refused.Message, StringComparison.Ordinal);

        // The gitdir: derivation is a canonical identity too, and its own colon must not be read as an
        // scp separator -- 'gitdir/c:/...' would be a second store for a repository with no remote.
        var gitdir = MemoryImportOptionsParser.Parse(["--assert", @"C:\root=gitdir:C:\repos\x\.git"]);
        Assert.Equal("gitdir:c:/repos/x/.git", Assert.Single(gitdir.Assertions).Repository);
    }

    /// <summary>
    /// A bare <c>owner/repo</c> is refused (#1949). It is a SEPARATE arm from the one above because it
    /// does not canonicalize to nothing — it canonicalizes to the well-formed <c>owner/repo</c>, host
    /// <c>owner</c>, which is exactly why the earlier refusal never fired on it: no default forge host
    /// is assumed, so storing it would name a store the git probe can never reach.
    /// </summary>
    [Theory]
    [InlineData("owner/repo")]
    [InlineData("philipreese/baton")]
    [InlineData("Owner/Repo/extra")]
    // The polarity pair for the arm below: this identity is accepted the moment a scheme states that
    // 'internal' is the host, so the scheme is the operative condition rather than the dotless host.
    [InlineData("internal/owner/repo")]
    // A colon that arrives AFTER the first separator declares no host, so presence of ':' cannot be
    // what exempts a value -- reading it that way let this spelling through.
    [InlineData("owner/repo:main")]
    public void A_bare_owner_repo_with_no_forge_host_is_refused(string spelling)
    {
        var refused = Assert.Throws<CliArgumentException>(
            () => MemoryImportOptionsParser.Parse(["--assert", $@"C:\root={spelling}"]));

        Assert.Contains("names no host", refused.Message, StringComparison.Ordinal);
        Assert.Contains("github.com/", refused.TryInvocation ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// The control arm for the refusal above, in both directions: a full identity is UNCHANGED by it,
    /// and a dotless host stays assertable when the operator spelled the host out — otherwise the
    /// refusal would be a ban on intranet remotes rather than on a missing host.
    /// </summary>
    [Theory]
    [InlineData("github.com/owner/repo", "github.com/owner/repo")]
    [InlineData("https://internal/owner/repo", "internal/owner/repo")]
    [InlineData("git@internal:owner/repo.git", "internal/owner/repo")]
    // A UNC remote states its host in an authority rather than a scheme, and Windows is the only
    // platform this ships on (#1405), so the refusal must not swallow one.
    [InlineData(@"\\server\share\repo.git", "server/share/repo")]
    public void A_repository_that_names_its_host_is_accepted_unchanged(string spelling, string expected)
    {
        var parsed = MemoryImportOptionsParser.Parse(["--assert", $@"C:\root={spelling}"]);

        Assert.Equal(expected, Assert.Single(parsed.Assertions).Repository);
    }

    [Fact]
    public async Task Help_states_that_no_default_forge_host_is_assumed()
    {
        var text = await RunAsync("--help");

        Assert.Contains("NAME THE FORGE HOST", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// An operator path that <c>Path.GetFullPath</c> cannot take surfaces as this verb's own refusal,
    /// not as a bare <see cref="ArgumentException"/> — for both flags that take one.
    /// </summary>
    [Fact]
    public async Task An_unusable_assert_or_root_path_is_a_cli_argument_exception()
    {
        var badPath = "bad\0path";

        var asserted = await Assert.ThrowsAsync<CliArgumentException>(
            () => RunAsync("--assert", $"{badPath}=github.com/owner/repo", "--asserted-by", "the-test"));
        Assert.Contains("does not name a usable path", asserted.Message, StringComparison.Ordinal);

        var rooted = await Assert.ThrowsAsync<CliArgumentException>(() => RunAsync("--root", badPath));
        Assert.Contains("does not name a usable path", rooted.Message, StringComparison.Ordinal);

        // Control: a well-formed path of each reaches the verb's own refusal instead, so the arms above
        // are about the path being unusable rather than about the flags refusing everything.
        var undiscovered = Path.Combine(_root, "not-a-root");
        var refused = await Assert.ThrowsAsync<CliArgumentException>(() => RunAsync("--root", undiscovered));
        Assert.Contains("matches no memory root this verb can import", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// #1948: a source rewritten between the inventory walk and the read is recorded as one consistent
    /// (mtime, digest, size) triple, all three describing the bytes that were actually read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The stale row IS the inventory.</b> <see cref="MemoryImportCommand.ReadSourceFiles"/>'s own
    /// remarks say why it is reached directly rather than through the verb; what the seam is used for is
    /// this: the file row handed in carries the values the walk took from the old contents, which is
    /// exactly what it would hold had the file been rewritten in the window. Asserting against a file
    /// nobody touched cannot discriminate — with the defect present, an unchanged file's walk mtime and
    /// its read mtime are the same value.
    /// </para>
    /// <para>
    /// <b>The mtime difference is forced, not hoped for.</b> The old mtime is a distinctive sentinel a
    /// year in the past rather than whatever a quick rewrite happens to produce: filesystem timestamp
    /// granularity can make a rewritten file's before and after equal, and this arm would then pass with
    /// the defect intact. Size and digest are asserted against the current bytes too, so a later change
    /// that refreshes the mtime alone still fails here.
    /// </para>
    /// <para>
    /// <b>What this arm does not pin.</b> Its oracle is the mtime by path, so it cannot tell a read from
    /// the open handle apart from a second <c>stat</c> — nothing moves between the two here, and both
    /// answer alike. #1948's window is what is covered; the same-handle property beside it is a
    /// narrower claim the code makes and this does not measure. The <c>Kind</c> assertion is not
    /// decoration: <c>Assert.Equal</c> on two <see cref="DateTime"/>s compares ticks and ignores
    /// <see cref="DateTimeKind"/>, so slipping to the local-time overload would shift every recorded
    /// mtime by the machine's offset and read as green on a UTC runner.
    /// </para>
    /// </remarks>
    [Fact]
    public void RereadsMtimeWithTheDigestWhenTheSourceChangedSinceTheWalk()
    {
        var directory = Path.Combine(_root, "rewritten");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "user_who.md");

        var staleBytes = System.Text.Encoding.UTF8.GetBytes("the memory the inventory walk saw");
        var staleMtime = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var staleRow = new MemoryImportFile(
            path,
            "user_who.md",
            Text: string.Empty,
            Sha256: Convert.ToHexString(SHA256.HashData(staleBytes)).ToLowerInvariant(),
            ModifiedUtc: staleMtime,
            SizeBytes: staleBytes.Length);

        // The rewrite: different bytes, a different length, and a last-write time that provably is not
        // the walk's.
        var currentBytes = System.Text.Encoding.UTF8.GetBytes("the memory that is actually on disk now");
        File.WriteAllBytes(path, currentBytes);
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 6, 7, 8, 9, 10, DateTimeKind.Utc));
        var currentMtime = File.GetLastWriteTimeUtc(path);
        Assert.NotEqual(staleMtime, currentMtime);
        Assert.NotEqual(staleRow.SizeBytes, currentBytes.Length);

        var read = Assert.Single(MemoryImportCommand.ReadSourceFiles(new MemoryImportSource(
            directory,
            MemoryRootInventory.ClaudeVendor,
            VendorMemoryScope.Vendor,
            Archived: false,
            "github.com/philipreese/baton",
            UnfiledReason: null,
            [staleRow])));

        Assert.Equal("the memory that is actually on disk now", read.Text);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(currentBytes)).ToLowerInvariant(), read.Sha256);
        Assert.Equal(currentBytes.Length, read.SizeBytes);
        Assert.Equal(currentMtime, read.ModifiedUtc);
        Assert.Equal(DateTimeKind.Utc, read.ModifiedUtc.Kind);
    }

    /// <summary>
    /// Two Claude roots resolving to one repository through a real worktree, plus a live entry the
    /// archive tests supersede. Three files, all under <c>github.com/philipreese/baton</c>.
    /// </summary>
    private async Task BuildStandardFixtureAsync()
    {
        var main = Checkout("baton");
        var linked = Path.Combine(_root, "worktrees", "baton");
        await InitGitRepoAsync(main, "https://github.com/philipreese/baton.git");
        await RunGitAsync(main, "worktree", "add", "-q", "-b", "side", linked);

        WriteClaudeRoot("C--baton", main, ("user_who.md", "who we are"), ("project_plan.md", "the plan"));
        WriteClaudeRoot("C--baton-worktree", linked, ("feedback_style.md", "how we work"));
    }

    private static async Task InitGitRepoAsync(string directory, string originUrl)
    {
        Directory.CreateDirectory(directory);
        await RunGitAsync(directory, "init", "-q");
        await RunGitAsync(directory, "remote", "add", "origin", originUrl);
    }

    private static async Task RunGitAsync(string workingDirectory, params string[] args)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in args)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        // Bounded, per #1804: an unbounded wait here would hold the machine-wide build lock if git
        // ever hung on a credential or filesystem prompt.
        await BoundedProcessWait.RunToExitAsync(
            process, TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Assert.Equal(0, process.ExitCode);
    }
}
