using Baton.Memory;
using Baton.Tests.Shared;

namespace Baton.Tests.Memory;

/// <summary>
/// #1852 phase A2: the non-Claude memory roots, against a fixture user home built out of SYNTHETIC
/// files only. Nothing here is a real Codex database or a real Antigravity annotation — the contract
/// under test is path/size/mtime/sha256 and the selector's bound, none of which depends on a file's
/// bytes meaning anything.
/// </summary>
public sealed class VendorMemoryRootTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"baton-1852-vendor-{Guid.NewGuid():N}");

    public void Dispose() => DirectoryCleanup.DeleteRecursively(_home);

    private void Write(string relativePath, string content = "synthetic")
        => WriteAt(Path.Combine(_home, relativePath.Replace('/', Path.DirectorySeparatorChar)), content);

    private static void WriteAt(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>
    /// The fixture Baton root, deliberately NOT <c>{home}/.baton</c>: a Baton-managed family resolves
    /// against <c>BatonPaths.Root</c>, which <c>BATON_HOME</c> can point anywhere, and a fixture that
    /// nested it under the user home would pass just as happily against the wrong resolution.
    /// </summary>
    private string BatonRoot => Path.Combine(_home, "relocated-baton-root");

    private VendorMemoryRoot Root(string family, string absoluteDirectory) =>
        Assert.Single(
            MemoryRootInventory.ScanVendorRoots(_home, BatonRoot),
            r => r.Family == family && r.DirectoryPath == absoluteDirectory);

    private string UnderHome(string relative) =>
        Path.Combine(_home, relative.Replace('/', Path.DirectorySeparatorChar));

    private string UnderBatonRoot(string relative) =>
        Path.Combine(BatonRoot, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// The bound, with the arm that discriminates it. A Codex home holds its memory store beside logs,
    /// queues and thread histories — two of them over 100 MB on the machine this was measured on — so
    /// the selector takes <c>memories_*.sqlite</c> from the top level and nothing else.
    /// </summary>
    /// <remarks>
    /// The sibling files are the whole point: without them "enumerates the Codex sqlite root" passes
    /// just as happily on a recursive walk of the entire home, which is the defect. The WAL sidecar is
    /// the second arm — it shares the store's own stem, so a stem-only match would take it.
    /// </remarks>
    [Fact]
    public void The_codex_sqlite_selector_takes_the_memory_store_and_not_its_siblings()
    {
        Write(".codex/memories_1.sqlite", "store");
        Write(".codex/memories_1.sqlite-wal", "write-ahead log");
        Write(".codex/logs_2.sqlite", "a hundred megabytes of logs, in spirit");
        Write(".codex/state_5.sqlite");
        Write(".codex/sessions/nested/memories_9.sqlite", "not top level");

        var root = Root("codex-sqlite", UnderHome(".codex"));

        Assert.Equal(VendorMemoryPresence.Populated, root.Presence);
        Assert.Equal(["memories_1.sqlite"], root.Files.Select(f => f.RelativePath));
        Assert.Equal(1, root.FileCount);
    }

    /// <summary>
    /// Q5 (operator, 2026-09-05): Baton's own Codex home is its first beta, inventoried with
    /// <c>sourceVendor: codex</c> and <c>sourceScope: baton-managed</c>. Two negatives are pinned
    /// here, and the second is the one a reader would not expect.
    /// </summary>
    /// <remarks>
    /// First: the two stores are NOT collapsed into one row, because they diverged and merging them
    /// would destroy the evidence of how. Second, and the reason the fixture is shaped oddly: the
    /// Baton-managed row is placed where only a correct base can find it, with a decoy left at the
    /// default location that must stay invisible. <see cref="VendorMemoryFamily.RelativeDirectory"/>
    /// states what the wrong base costs; this arm is what fails if it is ever used.
    /// </remarks>
    [Fact]
    public void Batons_own_codex_home_is_a_distinct_row_resolved_against_batons_own_root()
    {
        Write(".codex/memories_1.sqlite", "the vendor's");
        WriteAt(Path.Combine(UnderBatonRoot("codex-home"), "memories_1.sqlite"), "baton's");

        // The decoy: what a user-home-relative resolution would have found instead.
        Write(".baton/codex-home/memories_1.sqlite", "a store at the DEFAULT path, not this root");

        var vendor = Root("codex-sqlite", UnderHome(".codex"));
        var managed = Root("codex-sqlite", UnderBatonRoot("codex-home"));

        Assert.Equal(VendorMemoryScope.Vendor, vendor.SourceScope);
        Assert.Equal(VendorMemoryScope.BatonManaged, managed.SourceScope);
        Assert.Equal("codex", managed.SourceVendor);
        Assert.NotEqual(vendor.Files.Single().Sha256, managed.Files.Single().Sha256);

        // The decoy is in no row at all: exactly one Baton-managed root exists, and it is the one
        // the supplied Baton root names.
        var managedRows = MemoryRootInventory.ScanVendorRoots(_home, BatonRoot)
            .Where(r => r.SourceScope == VendorMemoryScope.BatonManaged)
            .ToList();
        Assert.Equal([UnderBatonRoot("codex-home")], managedRows.Select(r => r.DirectoryPath));
    }

    /// <summary>
    /// Absent, empty and populated are three states, asserted as three — see
    /// <see cref="VendorMemoryPresence"/> for the misreading that collapsing the middle one produces.
    /// The Antigravity <c>knowledge</c> pair is the live shape: one root holding nothing but a
    /// zero-byte lock file, one root that does not exist at all.
    /// </summary>
    [Fact]
    public void Absent_empty_and_populated_are_three_distinct_states()
    {
        Directory.CreateDirectory(Path.Combine(_home, ".gemini", "antigravity", "knowledge"));
        Write(".gemini/antigravity-cli/knowledge/knowledge.lock", string.Empty);
        Write(".codex/memories/raw_memories.md", "a fact");

        // Absent: no such directory. Empty: the directory is there and the selector matched nothing.
        // The pair is deliberately across two different families, because the second state is the one
        // that only exists when the directory does -- .codex here is present (its markdown root put it
        // there) and holds no memories_*.sqlite, which is `empty` rather than `absent` too.
        Assert.Equal(VendorMemoryPresence.Absent, Root("codex-sqlite", UnderBatonRoot("codex-home")).Presence);
        Assert.Equal(VendorMemoryPresence.Empty, Root("codex-sqlite", UnderHome(".codex")).Presence);
        Assert.Equal(VendorMemoryPresence.Empty, Root("antigravity-knowledge", UnderHome(".gemini/antigravity/knowledge")).Presence);

        // A zero-byte file is a file: the directory is POPULATED, not empty. Reading it the other way
        // is what would report a shipped-but-unused vendor surface as one the vendor does not have.
        var lockRoot = Root("antigravity-knowledge", UnderHome(".gemini/antigravity-cli/knowledge"));
        Assert.Equal(VendorMemoryPresence.Populated, lockRoot.Presence);
        Assert.Equal(0, lockRoot.TotalBytes);

        Assert.Equal(VendorMemoryPresence.Populated, Root("codex-markdown", UnderHome(".codex/memories")).Presence);
    }

    /// <summary>
    /// The counted-not-opened family, in both directions. <c>brain</c> reports a count with no file
    /// rows; a family that IS inventoried reports rows for the same shape of tree. Without the second
    /// arm an empty <c>Files</c> list would be indistinguishable from an empty directory, which is
    /// the reading <see cref="VendorMemoryRoot.Inventoried"/> exists to make impossible.
    /// </summary>
    [Fact]
    public void The_brain_family_is_counted_and_never_opened_while_its_neighbours_are_digested()
    {
        Write(".gemini/antigravity-cli/brain/a-conversation/scratch/notes.txt", "scratch");
        Write(".gemini/antigravity-cli/brain/a-conversation/steps.jsonl", "steps");
        Write(".gemini/antigravity-cli/annotations/a-conversation.pbtxt", "title: \"synthetic\"");

        var brain = Root("antigravity-brain", UnderHome(".gemini/antigravity-cli/brain"));
        Assert.False(brain.Inventoried);
        Assert.Equal(2, brain.FileCount);
        Assert.Empty(brain.Files);
        Assert.Equal(VendorMemoryPresence.Populated, brain.Presence);

        var annotations = Root("antigravity-pbtxt", UnderHome(".gemini/antigravity-cli/annotations"));
        Assert.True(annotations.Inventoried);
        Assert.Equal(1, annotations.FileCount);
        Assert.Equal("a-conversation.pbtxt", annotations.Files.Single().RelativePath);
        Assert.NotEmpty(annotations.Files.Single().Sha256);
    }

    /// <summary>
    /// Every family gets a row on a home that has none of them, so "this machine has no Codex memory"
    /// is something the report SAYS rather than something a reader infers from a missing line.
    /// </summary>
    [Fact]
    public void A_home_with_no_vendor_roots_reports_every_family_as_absent()
    {
        Directory.CreateDirectory(_home);

        var roots = MemoryRootInventory.ScanVendorRoots(_home, BatonRoot);

        Assert.Equal(VendorMemoryRootTable.Families.Count, roots.Count);
        Assert.All(roots, r =>
        {
            Assert.Equal(VendorMemoryPresence.Absent, r.Presence);
            Assert.Equal(0, r.FileCount);
            Assert.Empty(r.Files);
            Assert.Null(r.NewestModifiedUtc);
        });
    }

    /// <summary>Markdown is taken recursively, and only markdown — the family's own selector.</summary>
    [Fact]
    public void The_codex_markdown_selector_is_recursive_and_extension_bounded()
    {
        Write(".codex/memories/raw_memories.md", "a");
        Write(".codex/memories/extensions/ad_hoc/instructions.md", "b");
        // Excluded by the EXTENSION, not by being under .git -- the selector has no notion of a git
        // directory, and a `.md` committed inside one would be taken. That is deliberate here (git
        // stores blobs, not `.md` files, so it cannot arise from git itself) and stated rather than
        // left for a reader to infer a `.git` exclusion this selector does not have.
        Write(".codex/memories/.git/HEAD", "not memory");

        var root = Root("codex-markdown", UnderHome(".codex/memories"));

        Assert.Equal(
            ["extensions/ad_hoc/instructions.md", "raw_memories.md"],
            root.Files.Select(f => f.RelativePath));
    }
}
