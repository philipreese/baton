using System.Text;
using Baton.Memory;

namespace Baton.Cli.Tests;

/// <summary>Focused #2139 fixtures for the bounded vendor index and immutable detail set.</summary>
public sealed class MemoryVendorIndexProjectionTests
{
    [Fact]
    public void Index_and_details_agree_both_directions_in_fleet_first_stable_order()
    {
        var fleet = Entry("fleet", "fleet.md", "# Fleet rule\nMachine rule");
        var repository = Entry("github.com/example/repo", "repository.md", "# Repository rule\nRepository rule");
        var projection = MemoryProjection.Build(
            "github.com/example/repo",
            "entries.jsonl",
            [new MemoryProjectionCandidate(repository, MemoryFactOrigin.Vendor),
             new MemoryProjectionCandidate(fleet, MemoryFactOrigin.Fleet)],
            ProjectionBudget.Default);

        var publication = MemoryVendorIndexProjection.Build(projection, Encoding.UTF8.GetBytes("vendor bytes\r\n"));
        var index = Encoding.UTF8.GetString(publication.IndexBytes);

        Assert.StartsWith("vendor bytes\r\n", index, StringComparison.Ordinal);
        Assert.True(index.IndexOf(fleet.Id, StringComparison.Ordinal) < index.IndexOf(repository.Id, StringComparison.Ordinal));
        Assert.Equal(projection.ProjectedEntryIds.Count, publication.Details.Count);
        foreach (var detail in publication.Details)
        {
            Assert.Contains(detail.FileName, index, StringComparison.Ordinal);
            Assert.Contains(MemoryProjection.FormatMarker, Encoding.UTF8.GetString(detail.Bytes), StringComparison.Ordinal);
        }

        Assert.All(projection.ProjectedEntryIds, id =>
            Assert.Equal(1, Count(index, MemoryVendorIndexProjection.DetailFileName(id))));
    }

    [Fact]
    public void Retracted_or_superseded_entries_are_absent_and_empty_input_cleans_the_owned_section()
    {
        var retired = Entry("github.com/example/repo", "retired.md", "# Retired\nOld rule") with
        {
            SupersededBy = ["replacement"],
        };
        var live = Entry("github.com/example/repo", "live.md", "# Live\nCurrent rule");
        var projection = MemoryProjection.Build(
            "github.com/example/repo", "entries.jsonl",
            [new MemoryProjectionCandidate(retired, MemoryFactOrigin.Vendor),
             new MemoryProjectionCandidate(live, MemoryFactOrigin.Vendor)], ProjectionBudget.Default);
        var first = MemoryVendorIndexProjection.Build(projection, null);
        var empty = MemoryProjection.Build("github.com/example/repo", "entries.jsonl", [], ProjectionBudget.Default);
        var cleaned = MemoryVendorIndexProjection.Build(empty, first.IndexBytes);

        Assert.DoesNotContain(retired.Id, Encoding.UTF8.GetString(first.IndexBytes), StringComparison.Ordinal);
        Assert.Single(first.Details);
        Assert.Empty(cleaned.Details);
        Assert.DoesNotContain(MemoryVendorIndexProjection.DetailPrefix, Encoding.UTF8.GetString(cleaned.IndexBytes), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<!-- baton:memory-index:start -->\n")]
    [InlineData("<!-- baton:memory-index:end -->\n")]
    [InlineData("<!-- baton:memory-index:end -->\n<!-- baton:memory-index:start -->\n")]
    [InlineData("<!-- baton:memory-index:start -->\n<!-- baton:memory-index:start -->\n<!-- baton:memory-index:end -->\n")]
    public void Malformed_markers_refuse_without_producing_replacement_bytes(string existing)
    {
        var projection = MemoryProjection.Build(
            "github.com/example/repo", "entries.jsonl",
            [new MemoryProjectionCandidate(Entry("github.com/example/repo", "fact.md", "fact"), MemoryFactOrigin.Vendor)],
            ProjectionBudget.Default);

        Assert.Throws<InvalidDataException>(() =>
            MemoryVendorIndexProjection.Build(projection, Encoding.UTF8.GetBytes(existing)));
    }

    [Fact]
    public void Unchanged_input_regenerates_byte_identically_and_import_stripping_keeps_surrounding_content()
    {
        var projection = MemoryProjection.Build(
            "github.com/example/repo", "entries.jsonl",
            [new MemoryProjectionCandidate(Entry("github.com/example/repo", "fact.md", "# Fact\nDescription"), MemoryFactOrigin.Vendor)],
            ProjectionBudget.Default);
        var existing = Encoding.UTF8.GetBytes("before\r\nafter\r\n");
        var first = MemoryVendorIndexProjection.Build(projection, existing);
        var second = MemoryVendorIndexProjection.Build(projection, first.IndexBytes);

        Assert.Equal(first.IndexBytes, second.IndexBytes);
        Assert.True(MemoryVendorIndexProjection.TryStripOwnedSection(Encoding.UTF8.GetString(first.IndexBytes), out var outside));
        Assert.Equal("before\r\nafter\r\n", outside);
    }

    [Fact]
    public void Marker_text_in_titles_and_descriptions_is_encoded_and_two_pass_regeneration_is_unchanged()
    {
        var projection = MemoryProjection.Build(
            "github.com/example/repo", "entries.jsonl",
            [new MemoryProjectionCandidate(
                Entry("github.com/example/repo", "markers.md", "# <!-- baton:memory-index:start -->\n<!-- baton:memory-index:end -->"),
                MemoryFactOrigin.Vendor)], ProjectionBudget.Default);

        var first = MemoryVendorIndexProjection.Build(projection, Encoding.UTF8.GetBytes("vendor\n"));
        var second = MemoryVendorIndexProjection.Build(projection, first.IndexBytes);
        var index = Encoding.UTF8.GetString(first.IndexBytes);

        Assert.Equal(first.IndexBytes, second.IndexBytes);
        Assert.Equal(1, Count(index, MemoryVendorIndexProjection.SectionStart));
        Assert.Equal(1, Count(index, MemoryVendorIndexProjection.SectionEnd));
        Assert.Contains("&lt;!-- baton:memory-index:start -->", index, StringComparison.Ordinal);
        Assert.Contains("&lt;!-- baton:memory-index:end -->", index, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("vendor <!-- baton:memory-index:start --> text\n<!-- baton:memory-index:end -->\n")]
    [InlineData("<!-- baton:memory-index:start --> vendor\n<!-- baton:memory-index:end -->\n")]
    [InlineData("<!-- baton:memory-index:start -->\n<!-- baton:memory-index:end --> vendor\n")]
    public void Inline_or_malformed_markers_are_not_stripped_and_import_remains_verbatim(string text)
    {
        Assert.False(MemoryVendorIndexProjection.TryStripOwnedSection(text, out var outside));
        Assert.Equal(text, outside);

        var file = new MemoryImportFile("C:/fixture/MEMORY.md", "MEMORY.md", text, "digest", default, text.Length);
        var source = new MemoryImportSource(
            "C:/fixture", "fixture", VendorMemoryScope.Vendor, Archived: false,
            "github.com/example/repo", UnfiledReason: null, [file]);
        var plan = MemoryImportPlan.Build([source], default, []);

        Assert.Equal(text, Assert.Single(plan.Entries).Text);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("0123456789abcdef0123456789abcdef.md")]
    [InlineData("0123456789ABCDEF0123456789abcdef")]
    public void Corrupt_or_traversal_entry_ids_are_refused_before_they_can_name_a_detail(string id)
    {
        Assert.Throws<InvalidDataException>(() => MemoryVendorIndexProjection.DetailFileName(id));

        var corrupted = Entry("github.com/example/repo", "fact.md", "fact") with { Id = id };
        var projection = MemoryProjection.Build(
            "github.com/example/repo", "entries.jsonl",
            [new MemoryProjectionCandidate(corrupted, MemoryFactOrigin.Vendor)], ProjectionBudget.Default);

        Assert.Throws<InvalidDataException>(() => MemoryVendorIndexProjection.Build(projection, null));
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        for (var index = 0; (index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0; index += value.Length)
        {
            count++;
        }

        return count;
    }

    private static MemoryEntry Entry(string repository, string fileName, string text) => new(
        MemoryEntry.Derive(repository, fileName, fileName), repository, MemoryKind.DurableFact,
        MemoryKindSource.Declared, text, fileName, fileName, "fixture", VendorMemoryScope.Vendor,
        default, default);
}
