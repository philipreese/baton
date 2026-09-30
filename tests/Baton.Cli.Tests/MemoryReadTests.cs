using Baton.Accounting;
using Baton.Cli;
using Baton.Memory;
using Baton.Status;
using Baton.Tests.Shared;
using System.Text;
using System.Text.Json;

namespace Baton.Cli.Tests;

public sealed class MemoryReadTests : IDisposable
{
    private const string Repository = "github.com/owner/repo";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"baton-2507-{Guid.NewGuid():N}");
    private readonly IDisposable _scope;

    public MemoryReadTests() => _scope = BatonEnvironmentSnapshot.BeginScope(
        BatonEnvironmentSnapshot.Blank with { HomeOverride = Path.Combine(_root, "baton-root") });

    public void Dispose()
    {
        MemoryReadCommand.SnapshotObserver = null;
        _scope.Dispose();
        DirectoryCleanup.DeleteRecursively(_root);
    }

    private static string Slug(string repository) => FleetMemory.SlugFor(repository);
    private static string Entries(string repository) => BatonPaths.MemoryEntriesFile(Slug(repository));

    private static Task<int> ReadAsync(TextWriter output, string repository = Repository) =>
        MemoryReadCommand.ExecuteAsync(
            MemoryReadOptionsParser.Parse(["--repository", repository]), output,
            TestContext.Current.CancellationToken);

    private static MemoryEntry Entry(string repository, string text) =>
        AuthoredMemory.Create(repository, text, MemoryKind.DurableFact, AuthoredMemory.Operator, DateTime.UnixEpoch);

    [Fact]
    public async Task Malformed_canonical_rows_refuse_the_read_without_output()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Entries(Repository))!);
        File.WriteAllText(Entries(Repository), "not-json\n");

        var output = new StringWriter();
        var exitCode = await ReadAsync(output);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public async Task Null_canonical_rows_refuse_the_read_without_output()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Entries(Repository))!);
        File.WriteAllText(Entries(Repository), "null\n");

        var output = new StringWriter();
        var exitCode = await ReadAsync(output);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public async Task Wrong_subject_entry_refuses_the_read_without_output()
    {
        await MemoryStore.AppendAsync([Entry("github.com/owner/other", "fixture wrong subject")], Entries(Repository),
            TestContext.Current.CancellationToken);

        var output = new StringWriter();
        var exitCode = await ReadAsync(output);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public async Task Wrong_subject_link_and_retraction_refuse_the_read_without_output()
    {
        await MemoryStore.AppendAsync([Entry(Repository, "fixture canonical")], Entries(Repository),
            TestContext.Current.CancellationToken);
        var slug = Slug(Repository);
        var links = BatonPaths.MemoryLinksFile(slug);
        var retractions = BatonPaths.MemoryRetractionsFile(slug);
        await MemoryStore.AppendLinksAsync(
            [MemorySupersessionLink.Create("new-id", "old-id", "github.com/owner/other", DateTime.UnixEpoch)],
            links, TestContext.Current.CancellationToken);

        var output = new StringWriter();
        Assert.Equal(1, await ReadAsync(output));
        Assert.Equal(string.Empty, output.ToString());

        FileCleanup.EnsureDeleted(links);
        await MemoryStore.AppendRetractionsAsync(
            [MemoryRetraction.Create("unknown-id", "github.com/owner/other", "fixture reason", "operator", DateTime.UnixEpoch)],
            retractions, TestContext.Current.CancellationToken);
        output = new StringWriter();
        Assert.Equal(1, await ReadAsync(output));
        Assert.Equal(string.Empty, output.ToString());
    }

    [Theory]
    [InlineData("links", "not-json\n")]
    [InlineData("links", "null\n")]
    [InlineData("retractions", "not-json\n")]
    [InlineData("retractions", "null\n")]
    public async Task Corrupt_link_and_retraction_rows_refuse_without_output(string ledger, string content)
    {
        await MemoryStore.AppendAsync([Entry(Repository, "fixture canonical")], Entries(Repository),
            TestContext.Current.CancellationToken);
        var path = ledger == "links"
            ? BatonPaths.MemoryLinksFile(Slug(Repository))
            : BatonPaths.MemoryRetractionsFile(Slug(Repository));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);

        var output = new StringWriter();
        Assert.Equal(1, await ReadAsync(output));
        Assert.Equal(string.Empty, output.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_utf8_inside_a_retraction_id_refuses_without_resurrecting_the_entry(bool includeUtf8Bom)
    {
        var entry = Entry(Repository, "fixture withdrawn memory");
        await MemoryStore.AppendAsync([entry], Entries(Repository), TestContext.Current.CancellationToken);
        var retractions = BatonPaths.MemoryRetractionsFile(Slug(Repository));
        Directory.CreateDirectory(Path.GetDirectoryName(retractions)!);
        var prefix = Encoding.UTF8.GetBytes("{\"entryId\":\"");
        var suffix = Encoding.UTF8.GetBytes("\",\"repository\":\"github.com/owner/repo\",\"reason\":\"fixture\",\"retractedBy\":\"operator\",\"retractedAtUtc\":\"1970-01-01T00:00:00Z\"}\n");
        var bom = includeUtf8Bom ? new byte[] { 0xEF, 0xBB, 0xBF } : [];
        File.WriteAllBytes(retractions, [.. bom, .. prefix, 0xFF, .. suffix]);

        var output = new StringWriter();
        var exitCode = await ReadAsync(output);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public async Task Whitespace_only_retraction_rows_refuse_without_output()
    {
        await MemoryStore.AppendAsync([Entry(Repository, "fixture withdrawn memory")], Entries(Repository),
            TestContext.Current.CancellationToken);
        var retractions = BatonPaths.MemoryRetractionsFile(Slug(Repository));
        Directory.CreateDirectory(Path.GetDirectoryName(retractions)!);
        File.WriteAllText(retractions, " \t\n");

        var output = new StringWriter();
        var exitCode = await ReadAsync(output);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public async Task Empty_retraction_ledger_is_valid_and_keeps_the_entry_usable()
    {
        var entry = Entry(Repository, "fixture canonical memory");
        await MemoryStore.AppendAsync([entry], Entries(Repository), TestContext.Current.CancellationToken);
        var retractions = BatonPaths.MemoryRetractionsFile(Slug(Repository));
        Directory.CreateDirectory(Path.GetDirectoryName(retractions)!);
        File.WriteAllText(retractions, string.Empty);

        var output = new StringWriter();
        Assert.Equal(0, await ReadAsync(output));

        Assert.Contains("fixture canonical memory", output.ToString());
    }

    [Fact]
    public async Task Repository_read_is_fleet_first_and_applies_retraction_before_supersession()
    {
        var fleetEntry = Entry(FleetMemory.Slug, "fixture fleet context");
        var older = Entry(Repository, "fixture earlier memory");
        var replacement = Entry(Repository, "fixture replacement memory");
        await MemoryStore.AppendAsync([fleetEntry], Entries(FleetMemory.Slug), TestContext.Current.CancellationToken);
        await MemoryStore.AppendAsync([older, replacement], Entries(Repository), TestContext.Current.CancellationToken);
        var slug = Slug(Repository);
        await MemoryStore.AppendLinksAsync(
            [MemorySupersessionLink.Create(replacement.Id, older.Id, Repository, DateTime.UnixEpoch)],
            BatonPaths.MemoryLinksFile(slug), TestContext.Current.CancellationToken);
        await MemoryStore.AppendRetractionsAsync(
            [MemoryRetraction.Create(replacement.Id, Repository, "fixture correction", "operator", DateTime.UnixEpoch)],
            BatonPaths.MemoryRetractionsFile(slug), TestContext.Current.CancellationToken);

        var output = new StringWriter();
        Assert.Equal(0, await MemoryReadCommand.ExecuteAsync(
            MemoryReadOptionsParser.Parse(["--repository", Repository, "--format", "json"]), output,
            TestContext.Current.CancellationToken));
        var firstReport = output.ToString();
        using var json = JsonDocument.Parse(output.ToString());
        var stores = json.RootElement.GetProperty("stores");
        Assert.Equal(FleetMemory.Slug, stores[0].GetProperty("repository").GetString());
        Assert.Equal(Repository, stores[1].GetProperty("repository").GetString());
        Assert.Equal("fixture fleet context", stores[0].GetProperty("entries")[0].GetProperty("text").GetString());
        Assert.Contains(stores[1].GetProperty("entries").EnumerateArray(), entry =>
            entry.GetProperty("text").GetString() == "fixture earlier memory");
        Assert.DoesNotContain(stores[1].GetProperty("entries").EnumerateArray(), entry =>
            entry.GetProperty("text").GetString() == "fixture replacement memory");
        Assert.Contains(stores[1].GetProperty("omissions").EnumerateArray(), omission =>
            omission.GetProperty("kind").GetString() == "retracted"
            && omission.GetProperty("entryId").GetString() == replacement.Id);

        output = new StringWriter();
        Assert.Equal(0, await MemoryReadCommand.ExecuteAsync(
            MemoryReadOptionsParser.Parse(["--repository", Repository, "--format", "json"]), output,
            TestContext.Current.CancellationToken));
        Assert.Equal(firstReport, output.ToString());

        output = new StringWriter();
        Assert.Equal(0, await MemoryReadCommand.ExecuteAsync(
            MemoryReadOptionsParser.Parse(["--repository", "fleet", "--format", "json"]), output,
            TestContext.Current.CancellationToken));
        using var fleetOnly = JsonDocument.Parse(output.ToString());
        Assert.Single(fleetOnly.RootElement.GetProperty("stores").EnumerateArray());
    }

    [Fact]
    public async Task Repository_read_uses_one_global_fleet_first_entry_budget()
    {
        var fleetEntries = Enumerable.Range(0, ProjectionBudget.Default.MaxEntries + 1)
            .Select(index => Entry(FleetMemory.Slug, $"fixture fleet {index:D3}")).ToList();
        var repositoryEntry = Entry(Repository, "fixture repository after fleet budget");
        await MemoryStore.AppendAsync(fleetEntries, Entries(FleetMemory.Slug), TestContext.Current.CancellationToken);
        await MemoryStore.AppendAsync([repositoryEntry], Entries(Repository), TestContext.Current.CancellationToken);

        var output = new StringWriter();
        Assert.Equal(0, await MemoryReadCommand.ExecuteAsync(
            MemoryReadOptionsParser.Parse(["--repository", Repository, "--format", "json"]), output,
            TestContext.Current.CancellationToken));
        using var json = JsonDocument.Parse(output.ToString());
        var stores = json.RootElement.GetProperty("stores");
        Assert.Equal(ProjectionBudget.Default.MaxEntries, stores[0].GetProperty("entries").GetArrayLength());
        Assert.Empty(stores[1].GetProperty("entries").EnumerateArray());
        Assert.Contains(stores[1].GetProperty("omissions").EnumerateArray(), omission =>
            omission.GetProperty("kind").GetString() == "budget"
            && omission.GetProperty("entryId").GetString() == repositoryEntry.Id);
    }

    [Fact]
    public async Task Missing_stores_are_reported_without_creating_the_root()
    {
        var batonRoot = BatonPaths.Root;
        Assert.False(Directory.Exists(batonRoot));

        var output = new StringWriter();
        Assert.Equal(0, await ReadAsync(output));

        Assert.False(Directory.Exists(batonRoot));
        Assert.Contains("absent", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Canonical_change_after_snapshot_refuses_without_partial_output()
    {
        await MemoryStore.AppendAsync([Entry(Repository, "fixture before snapshot")], Entries(Repository),
            TestContext.Current.CancellationToken);
        MemoryReadCommand.SnapshotObserver = () => MemoryStore.AppendAsync(
            [Entry(Repository, "fixture concurrent update")], Entries(Repository),
            TestContext.Current.CancellationToken).GetAwaiter().GetResult();

        var output = new StringWriter();
        var exitCode = await ReadAsync(output);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, output.ToString());
    }
}
