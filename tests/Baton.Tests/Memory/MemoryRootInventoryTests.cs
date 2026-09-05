using System.Security.Cryptography;
using System.Text;
using Baton.Memory;
using Baton.Tests.Shared;

namespace Baton.Tests.Memory;

/// <summary>
/// #1852 phase A: the population. Both halves of it — the live roots under
/// <c>projects/*/memory</c> and the archived roots under <c>memory-archive/&lt;label&gt;</c> — against a
/// fixture Claude home, never this machine's.
/// </summary>
public sealed class MemoryRootInventoryTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"baton-1852-inv-{Guid.NewGuid():N}");

    public void Dispose() => DirectoryCleanup.DeleteRecursively(_home);

    private void WriteMemoryFile(string rootRelativePath, string content)
    {
        var path = Path.Combine(_home, rootRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void Both_populations_are_walked_and_an_emptied_live_root_is_still_a_root()
    {
        WriteMemoryFile("projects/C--Users-pbree-source-repos-alpaca-agent-bot/memory/project_baton_direction.md", "a");
        WriteMemoryFile("projects/C--Users-pbree-source-repos-alpaca-agent-bot/memory/MEMORY.md", "index");
        WriteMemoryFile("memory-archive/2026-09-03/c--Users-pbree-source-repos-baton-memory/user_who.md", "b");

        // The live baton root, drained by the archive above: present, empty, and the evidence that a
        // migration happened. An inventory that skipped it would report a machine with no Baton memory.
        Directory.CreateDirectory(Path.Combine(_home, "projects", "c--Users-pbree-source-repos-baton", "memory"));

        // A project directory with no memory/ at all is not a root -- the discriminating negative,
        // without which "walks projects/*" would pass on a walk of every project directory.
        Directory.CreateDirectory(Path.Combine(_home, "projects", "c--Users-pbree-source-repos-specimen"));

        var roots = MemoryRootInventory.Scan(_home);

        Assert.Equal(3, roots.Count);

        var alpaca = Assert.Single(roots, r => r.DirectoryName == "C--Users-pbree-source-repos-alpaca-agent-bot");
        Assert.Equal(MemoryRootKind.Live, alpaca.Kind);
        Assert.Null(alpaca.ArchiveLabel);
        Assert.Equal(Path.Combine(_home, "projects", "C--Users-pbree-source-repos-alpaca-agent-bot"), alpaca.SessionDirectoryPath);
        Assert.Equal(["MEMORY.md", "project_baton_direction.md"], alpaca.Files.Select(f => f.RelativePath));

        var drained = Assert.Single(roots, r => r.DirectoryName == "c--Users-pbree-source-repos-baton");
        Assert.Empty(drained.Files);

        var archived = Assert.Single(roots, r => r.Kind == MemoryRootKind.Archive);
        Assert.Equal("2026-09-03", archived.ArchiveLabel);
        Assert.Null(archived.SessionDirectoryPath);
        Assert.Single(archived.Files);
    }

    [Fact]
    public void Every_file_carries_its_size_its_mtime_and_the_sha256_of_its_bytes()
    {
        const string Content = "one durable fact";
        WriteMemoryFile("projects/C--x/memory/feedback_a.md", Content);

        var file = Assert.Single(Assert.Single(MemoryRootInventory.Scan(_home)).Files);

        Assert.Equal(Encoding.UTF8.GetByteCount(Content), file.SizeBytes);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Content))).ToLowerInvariant(),
            file.Sha256);
        Assert.Equal(DateTimeKind.Utc, file.ModifiedUtc.Kind);
        Assert.True(file.ModifiedUtc > DateTime.UtcNow.AddMinutes(-5));

        // The control on the digest: different bytes, different hash. Without it the assertion above
        // passes on a hash of the PATH, which is constant per file and would look identical here.
        WriteMemoryFile("projects/C--y/memory/feedback_a.md", "a different durable fact");
        var other = MemoryRootInventory.Scan(_home).Single(r => r.DirectoryName == "C--y").Files.Single();
        Assert.NotEqual(file.Sha256, other.Sha256);
    }

    [Fact]
    public void Nested_files_are_walked_and_keyed_by_their_relative_path()
    {
        WriteMemoryFile("projects/C--x/memory/nested/deeper/reference_r.md", "r");

        var file = Assert.Single(Assert.Single(MemoryRootInventory.Scan(_home)).Files);
        Assert.Equal("nested/deeper/reference_r.md", file.RelativePath);
    }

    [Fact]
    public void A_claude_home_that_does_not_exist_is_an_empty_inventory_not_a_throw()
        => Assert.Empty(MemoryRootInventory.Scan(Path.Combine(_home, "never-created")));
}
