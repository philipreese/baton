using Baton.Accounting;
using Baton.Memory;
using Baton.Status;
using Baton.Tests.Shared;

namespace Baton.Cli.Tests;

/// <summary>
/// <c>baton memory retract</c> (#2113), driven end to end over a fixture Baton root.
/// </summary>
/// <remarks>
/// <b>Every byte here is synthetic</b>, the same posture <see cref="MemoryAddTests"/> states: a fixture
/// directory under one temp root, nothing under the operator's own <c>~/.baton</c> touched.
/// </remarks>
public sealed class MemoryRetractTests : IDisposable
{
    private const string Repository = "github.com/owner/repo";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"baton-2113-{Guid.NewGuid():N}");
    private readonly IDisposable _scope;

    public MemoryRetractTests() =>
        _scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = Path.Combine(_root, "baton-root") });

    public void Dispose()
    {
        _scope.Dispose();
        DirectoryCleanup.DeleteRecursively(_root);
    }

    private static string Slug => RepositoryIdentity.FileSlugFor(Repository);
    private static string EntriesFile => BatonPaths.MemoryEntriesFile(Slug);
    private static string RetractionsFile => BatonPaths.MemoryRetractionsFile(Slug);

    private async Task<(int ExitCode, string Output, MemoryEntry Entry)> AddAsync(string text)
    {
        var writer = new StringWriter();
        var exitCode = await MemoryAddCommand.ExecuteAsync(
            MemoryAddOptionsParser.Parse(["--text", text, "--kind", "durable-fact", "--repository", Repository]),
            writer,
            AuthoredMemory.Operator,
            cancellationToken: TestContext.Current.CancellationToken,
            claudeHomeOverride: Path.Combine(_root, "claude"),
            userHomeOverride: Path.Combine(_root, "home"));

        Assert.Equal(0, exitCode);
        var entry = Assert.Single(await MemoryStore.ReadAllAsync(EntriesFile, TestContext.Current.CancellationToken));
        return (exitCode, writer.ToString(), entry);
    }

    private Task<(int ExitCode, string Output)> RetractAsync(string entryId, string reason, string assertedBy = AuthoredMemory.Operator) =>
        RunRetractAsync(assertedBy, entryId, "--reason", reason, "--repository", Repository);

    private async Task<(int ExitCode, string Output)> RunRetractAsync(string retractedBy, params string[] args)
    {
        var writer = new StringWriter();
        var exitCode = await MemoryRetractCommand.ExecuteAsync(
            MemoryRetractOptionsParser.Parse(args),
            writer,
            retractedBy,
            cancellationToken: TestContext.Current.CancellationToken,
            claudeHomeOverride: Path.Combine(_root, "claude"),
            userHomeOverride: Path.Combine(_root, "home"));

        return (exitCode, writer.ToString());
    }

    [Fact]
    public async Task Retract_appends_a_row_that_the_resolved_view_excludes_while_the_raw_file_keeps_both()
    {
        var (_, _, entry) = await AddAsync("fixture memory alpha");

        var (exitCode, output) = await RetractAsync(entry.Id, "the smoke tasks moved into Baton.slnx");

        Assert.Equal(0, exitCode);
        Assert.Contains("RETRACTED", output, StringComparison.Ordinal);
        Assert.Contains(entry.Id, output, StringComparison.Ordinal);

        // Resolved view excludes it.
        var resolved = await MemoryStore.ReadResolvedAsync(
            EntriesFile, BatonPaths.MemoryLinksFile(Slug), RetractionsFile, TestContext.Current.CancellationToken);
        Assert.Empty(resolved);

        // Raw entries file still holds the original row, byte-identical -- history is never deleted.
        var stored = await MemoryStore.ReadAllAsync(EntriesFile, TestContext.Current.CancellationToken);
        var storedEntry = Assert.Single(stored);
        Assert.Equal(entry.Id, storedEntry.Id);
        Assert.Equal(entry.Text, storedEntry.Text);

        // The retraction row exists in its own file.
        var retractions = await MemoryStore.ReadRetractionsAsync(RetractionsFile, TestContext.Current.CancellationToken);
        var retraction = Assert.Single(retractions);
        Assert.Equal(entry.Id, retraction.EntryId);
        Assert.Equal("the smoke tasks moved into Baton.slnx", retraction.Reason);
        Assert.Equal(AuthoredMemory.Operator, retraction.RetractedBy);
    }

    [Fact]
    public async Task Retracting_an_unknown_id_is_refused_and_writes_nothing()
    {
        var (exitCode, output) = await RetractAsync("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcd", "no such entry");

        Assert.Equal(1, exitCode);
        Assert.Contains("REFUSED", output, StringComparison.Ordinal);
        Assert.False(File.Exists(RetractionsFile));
    }

    [Fact]
    public async Task Retracting_an_already_retracted_id_is_refused_and_names_the_earlier_retraction()
    {
        var (_, _, entry) = await AddAsync("fixture memory alpha");
        await RetractAsync(entry.Id, "first reason");

        var (exitCode, output) = await RetractAsync(entry.Id, "second reason");

        Assert.Equal(1, exitCode);
        Assert.Contains("REFUSED", output, StringComparison.Ordinal);
        Assert.Contains("first reason", output, StringComparison.Ordinal);
        Assert.Contains("already retracted", output, StringComparison.Ordinal);

        // Nothing written the second time: still exactly one retraction row.
        Assert.Single(await MemoryStore.ReadRetractionsAsync(RetractionsFile, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Audit_names_the_retraction_with_its_reason()
    {
        var (_, _, entry) = await AddAsync("fixture memory alpha");
        await RetractAsync(entry.Id, "the smoke tasks moved into Baton.slnx");

        var writer = new StringWriter();
        var exitCode = await MemoryAuditCommand.ExecuteAsync(
            MemoryAuditOptionsParser.Parse([]),
            writer,
            userHomeOverride: Path.Combine(_root, "user-home"),
            batonRootOverride: BatonPaths.Root,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        var output = writer.ToString();
        Assert.Contains(entry.Id, output, StringComparison.Ordinal);
        Assert.Contains("the smoke tasks moved into Baton.slnx", output, StringComparison.Ordinal);
        Assert.Contains(AuthoredMemory.Operator, output, StringComparison.Ordinal);
    }

    [Fact]
    public void An_entry_id_is_required()
    {
        Assert.Throws<CliArgumentException>(() => MemoryRetractOptionsParser.Parse(["--reason", "x"]));
    }

    [Fact]
    public void A_reason_is_required_and_cannot_be_blank()
    {
        Assert.Throws<CliArgumentException>(() => MemoryRetractOptionsParser.Parse(["some-id"]));
        Assert.Throws<CliArgumentException>(() => MemoryRetractOptionsParser.Parse(["some-id", "--reason", "   "]));
    }

    [Fact]
    public async Task Help_writes_nothing_and_names_that_nothing_is_deleted()
    {
        var (exitCode, output) = await RunRetractAsync(AuthoredMemory.Operator, "--help");

        Assert.Equal(0, exitCode);
        Assert.Contains("NOTHING IS DELETED", output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(BatonPaths.Root));
    }
}
