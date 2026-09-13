using System.Text;
using Baton.Memory;

namespace Baton.Cli.Tests;

public sealed class MemoryContextIndexTests
{
    [Fact]
    public void Build_is_body_free_fleet_first_and_prefix_truncated()
    {
        var fleet = Entry("fleet", "fleet body that must not be rendered");
        var repository = Entry("repository", "repository body that must not be rendered");

        var first = MemoryContextIndex.Build(
            "example/repository",
            [new(repository, MemoryFactOrigin.Vendor), new(fleet, MemoryFactOrigin.Fleet)],
            new ProjectionBudget(10_000, 1));
        var second = MemoryContextIndex.Build(
            "example/repository",
            [new(fleet, MemoryFactOrigin.Fleet), new(repository, MemoryFactOrigin.Vendor)],
            new ProjectionBudget(10_000, 1));

        var text = Encoding.UTF8.GetString(first.Bytes);
        Assert.Equal(first.Bytes, second.Bytes);
        Assert.Contains($"id={fleet.Id}", text, StringComparison.Ordinal);
        Assert.Contains("provenance=unlabelled-non-semantic", text, StringComparison.Ordinal);
        Assert.DoesNotContain(fleet.Text, text, StringComparison.Ordinal);
        Assert.DoesNotContain(repository.Text, text, StringComparison.Ordinal);
        Assert.Single(first.Omitted);
        Assert.Equal(repository.Id, first.Omitted[0].EntryId);
        Assert.Contains($"id={repository.Id} reason=beyond the memory-context budget", text, StringComparison.Ordinal);
    }

    private static MemoryEntry Entry(string name, string text)
    {
        var repository = "example/repository";
        var sourcePath = $"C:/fixture/{name}.md";
        var sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        return new MemoryEntry(
            MemoryEntry.Derive(repository, sourcePath, sha256), repository, MemoryKind.DurableFact,
            MemoryKindSource.Declared, text, sha256, sourcePath, "fixture", VendorMemoryScope.Vendor,
            SourceMtimeUtc: default, ImportedAtUtc: default);
    }
}
