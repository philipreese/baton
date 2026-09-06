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
        var writer = new StringWriter();
        var exitCode = await MemoryImportCommand.ExecuteAsync(
            MemoryImportOptionsParser.Parse(args),
            writer,
            ClaudeHome,
            TestContext.Current.CancellationToken,
            UserHome);

        Assert.Equal(0, exitCode);
        return writer.ToString();
    }

    /// <summary>Every entry in one repository's canonical store.</summary>
    private static Task<IReadOnlyList<MemoryEntry>> StoreAsync(string repository) =>
        MemoryStore.ReadAllAsync(
            BatonPaths.MemoryEntriesFile(RepositoryIdentity.FileSlugFor(repository)),
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
            .ToDictionary(
                p => p,
                p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))),
                StringComparer.OrdinalIgnoreCase);

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
    [Fact]
    public async Task Every_source_file_is_byte_identical_after_an_import()
    {
        await BuildStandardFixtureAsync();

        var claudeBefore = DigestTree(ClaudeHome);
        var homeBefore = DigestTree(UserHome);

        await RunAsync();

        Assert.Equal(claudeBefore, DigestTree(ClaudeHome));
        Assert.Equal(homeBefore, DigestTree(UserHome));

        // The control: the run must actually have done something, or the assertion above is vacuous.
        Assert.NotEmpty(await StoreAsync("github.com/philipreese/baton"));
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

        // Polarity: an EDITED source file is a new entry rather than a silent overwrite, so the
        // no-op above is a statement about unchanged content and not about the store being closed.
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
        Assert.False(Directory.Exists(Path.Combine(BatonPaths.Root, "github.com-philipreese-rescued")));

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
        var selected = Path.Combine(ClaudeHome, "projects", "C--baton", "memory");

        await RunAsync("--root", selected);
        Assert.Equal(2, (await StoreAsync("github.com/philipreese/baton")).Count);

        var undiscovered = Path.Combine(_root, "not-a-root");
        Directory.CreateDirectory(undiscovered);
        var refused = await Assert.ThrowsAsync<CliArgumentException>(() => RunAsync("--root", undiscovered));
        Assert.Contains("matches no discovered memory root", refused.Message, StringComparison.Ordinal);
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
            ["--dry-run", "--root", "r", "--assert", @"C:\a=b/c", "--asserted-by", "me"]);
        Assert.True(ok.DryRun);
        Assert.Equal(["r"], ok.Roots);
        Assert.Equal(new MemoryImportAssertion(@"C:\a", "b/c"), Assert.Single(ok.Assertions));
        Assert.Equal("me", ok.AssertedBy);

        Assert.Throws<CliArgumentException>(() => MemoryImportOptionsParser.Parse(["--frobnicate"]));
        Assert.Throws<CliArgumentException>(() => MemoryImportOptionsParser.Parse(["--root"]));
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
