using Baton.Accounting;
using Baton.Memory;
using Baton.Status;
using Baton.Tests.Shared;

namespace Baton.Cli.Tests;

/// <summary>
/// <c>baton memory add</c> (#2071), driven end to end over a fixture Baton root.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every byte here is synthetic.</b> The store is a fixture directory under one temp root, pointed
/// at by <c>BatonEnvironmentSnapshot.BeginScope</c>; nothing under the operator's own
/// <c>~/.baton</c> is read or written, and no fixture text is a real memory (#2071: "fixtures only in
/// tests; never real memory content").
/// </para>
/// <para>
/// <b><c>assertedBy</c> is passed in rather than left to the environment</b>, because this suite may
/// itself be running inside a lane — <c>BATON_ARTIFACTS_ROOT</c> is inherited by anything a worker
/// spawns, so "no lane" is not a state a test can reach by declining to set it.
/// <see cref="MemoryLaneAssertionTests"/> is where that resolution is exercised.
/// </para>
/// </remarks>
public sealed class MemoryAddTests : IDisposable
{
    private const string Repository = "github.com/owner/repo";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"baton-2071-{Guid.NewGuid():N}");
    private readonly IDisposable _scope;

    public MemoryAddTests() =>
        _scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = Path.Combine(_root, "baton-root") });

    public void Dispose()
    {
        _scope.Dispose();
        DirectoryCleanup.DeleteRecursively(_root);
    }

    private static string EntriesFile => BatonPaths.MemoryEntriesFile(RepositoryIdentity.FileSlugFor(Repository));

    private async Task<(int ExitCode, string Output)> RunAsync(string assertedBy, params string[] args)
    {
        var writer = new StringWriter();
        var exitCode = await MemoryAddCommand.ExecuteAsync(
            MemoryAddOptionsParser.Parse(args),
            writer,
            assertedBy,
            cancellationToken: TestContext.Current.CancellationToken);

        return (exitCode, writer.ToString());
    }

    private Task<IReadOnlyList<MemoryEntry>> StoredAsync() =>
        MemoryStore.ReadAllAsync(EntriesFile, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Add_appends_one_declared_entry_asserted_by_the_operator()
    {
        var (exitCode, output) = await RunAsync(
            AuthoredMemory.Operator,
            "--text", "fixture memory alpha", "--kind", "durable-fact", "--repository", Repository);

        Assert.Equal(0, exitCode);

        var entry = Assert.Single(await StoredAsync());
        Assert.Equal("fixture memory alpha", entry.Text);
        Assert.Equal(MemoryKind.DurableFact, entry.Kind);
        Assert.Equal(MemoryKindSource.Declared, entry.KindSource);
        Assert.Equal(AuthoredMemory.Operator, entry.AssertedBy);
        Assert.Equal(Repository, entry.Repository);
        Assert.True(AuthoredMemory.IsAuthored(entry));
        Assert.Contains(entry.Id, output, StringComparison.Ordinal);

        // The stand-in path is a key, never a place: nothing was created at it or under its directory.
        Assert.False(File.Exists(entry.SourcePath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(entry.SourcePath)!));
    }

    /// <summary>
    /// The polarity arm for the refusal below: a DIFFERENT text under the same subject is a second
    /// entry, so the refusal is about byte-identity rather than about adding twice.
    /// </summary>
    [Fact]
    public async Task A_different_text_is_a_second_entry()
    {
        await RunAsync(AuthoredMemory.Operator, "--text", "fixture alpha", "--kind", "durable-fact", "--repository", Repository);
        var (exitCode, _) = await RunAsync(
            AuthoredMemory.Operator, "--text", "fixture beta", "--kind", "hypothesis", "--repository", Repository);

        Assert.Equal(0, exitCode);
        var stored = await StoredAsync();
        Assert.Equal(2, stored.Count);
        Assert.Equal(2, stored.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count());

        // 'hypothesis' has no import-time producer; add is the writer that reaches it.
        Assert.Contains(stored, e => e.Kind == MemoryKind.Hypothesis);
    }

    [Fact]
    public async Task A_byte_identical_entry_is_refused_and_the_store_keeps_one_row()
    {
        var first = await RunAsync(
            AuthoredMemory.Operator, "--text", "fixture alpha", "--kind", "durable-fact", "--repository", Repository);
        Assert.Equal(0, first.ExitCode);

        var (exitCode, output) = await RunAsync(
            AuthoredMemory.Operator, "--text", "fixture alpha", "--kind", "durable-fact", "--repository", Repository);

        Assert.Equal(1, exitCode);
        Assert.Contains("REFUSED", output, StringComparison.Ordinal);
        Assert.Contains(Assert.Single(await StoredAsync()).Id, output, StringComparison.Ordinal);
        Assert.Single(await StoredAsync());
    }

    [Fact]
    public async Task Dry_run_writes_no_store_row_and_no_manifest()
    {
        var (exitCode, output) = await RunAsync(
            AuthoredMemory.Operator,
            "--text", "fixture alpha", "--kind", "operator-preference", "--repository", Repository, "--dry-run");

        Assert.Equal(0, exitCode);
        Assert.Contains("DRY RUN", output, StringComparison.Ordinal);
        Assert.False(File.Exists(EntriesFile));
        Assert.False(Directory.Exists(Path.Combine(BatonPaths.Root, BatonPaths.MemoryImportsDirectoryName)));
        Assert.False(Directory.Exists(Path.GetDirectoryName(
            AuthoredMemory.SourcePathFor(Repository, AuthoredMemory.Digest("fixture alpha")))!));
    }

    /// <summary>
    /// A dry run of a duplicate refuses too — otherwise it would preview a success the real run would
    /// not produce, which is the one thing a preview must not do.
    /// </summary>
    [Fact]
    public async Task Dry_run_of_a_duplicate_refuses()
    {
        await RunAsync(AuthoredMemory.Operator, "--text", "fixture alpha", "--kind", "durable-fact", "--repository", Repository);

        var (exitCode, output) = await RunAsync(
            AuthoredMemory.Operator,
            "--text", "fixture alpha", "--kind", "durable-fact", "--repository", Repository, "--dry-run");

        Assert.Equal(1, exitCode);
        Assert.Contains("REFUSED", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task It_names_the_command_that_regenerates_the_projections_and_does_not_run_it()
    {
        var (_, output) = await RunAsync(
            AuthoredMemory.Operator, "--text", "fixture alpha", "--kind", "durable-fact", "--repository", Repository);

        Assert.Contains($"baton memory sync --repository {Repository} --apply", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The reuse claim that justifies writing an <c>ImportManifest</c> rather than a second reversal
    /// format: the verb an operator already has undoes an add.
    /// </summary>
    [Fact]
    public async Task Import_undo_reverses_an_add_through_its_manifest()
    {
        var (_, output) = await RunAsync(
            AuthoredMemory.Operator, "--text", "fixture alpha", "--kind", "durable-fact", "--repository", Repository);

        var manifestPath = output
            .Split('\n')
            .Select(line => line.Trim())
            .First(line => line.StartsWith("MANIFEST ", StringComparison.Ordinal))["MANIFEST ".Length..]
            .Trim();

        Assert.True(File.Exists(manifestPath));
        Assert.Single(await StoredAsync());

        var undoWriter = new StringWriter();
        var undoExit = await MemoryImportCommand.ExecuteAsync(
            MemoryImportOptionsParser.Parse(["--undo", manifestPath]),
            undoWriter,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, undoExit);
        Assert.Empty(await StoredAsync());
    }

    [Fact]
    public async Task A_lane_assertion_is_recorded_as_written()
    {
        await RunAsync(
            "implement/claude/queue-2071b",
            "--text", "fixture alpha", "--kind", "durable-fact", "--repository", Repository);

        Assert.Equal("implement/claude/queue-2071b", Assert.Single(await StoredAsync()).AssertedBy);
    }

    [Fact]
    public void Unknown_is_not_a_kind_this_verb_accepts()
    {
        Assert.DoesNotContain("unknown", MemoryAddOptionsParser.Kinds);

        var refusal = Assert.Throws<CliArgumentException>(
            () => MemoryAddOptionsParser.Parse(["--text", "x", "--kind", "unknown"]));
        Assert.Contains("absence of a kind", refusal.TryInvocation ?? string.Empty, StringComparison.Ordinal);

        // Control: a real kind parses, so the refusal above is about 'unknown' and not about --kind.
        Assert.Equal(
            MemoryKind.HistoricalNote,
            MemoryAddOptionsParser.Parse(["--text", "x", "--kind", "historical-note"]).Kind);
    }

    [Fact]
    public void Text_and_kind_are_both_required()
    {
        Assert.Throws<CliArgumentException>(() => MemoryAddOptionsParser.Parse(["--kind", "durable-fact"]));
        Assert.Throws<CliArgumentException>(() => MemoryAddOptionsParser.Parse(["--text", "x"]));
        Assert.Throws<CliArgumentException>(() => MemoryAddOptionsParser.Parse(["--text", "   ", "--kind", "durable-fact"]));
    }

    /// <summary>
    /// The same write-path refusal <c>--assert</c> makes, applied here rather than copied — a store
    /// filed under an identity no probe can answer for is one nothing finds again.
    /// </summary>
    [Fact]
    public void A_repository_naming_no_host_is_refused()
    {
        Assert.Throws<CliArgumentException>(
            () => MemoryAddOptionsParser.Parse(["--text", "x", "--kind", "durable-fact", "--repository", "owner/repo"]));

        // Control: the same value with a host parses, so the refusal is the host rule.
        Assert.Equal(
            "github.com/owner/repo",
            MemoryAddOptionsParser.Parse(
                ["--text", "x", "--kind", "durable-fact", "--repository", "github.com/owner/repo"]).Repository);
    }

    [Fact]
    public async Task Help_writes_nothing_and_names_the_write_path()
    {
        var (exitCode, output) = await RunAsync(AuthoredMemory.Operator, "--help");

        Assert.Equal(0, exitCode);
        Assert.Contains("ongoing write path", output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(BatonPaths.Root));
    }
}
