using System.Text;
using Baton.Accounting;
using Baton.Cli.Daemon;
using Baton.Memory;
using Baton.Status;
using Baton.Tests.Shared;

namespace Baton.Cli.Tests;

/// <summary>#2138 controls. Every home and vendor root in this file is a disposable fixture.</summary>
public sealed class MemoryAutomaticProjectionTests : IDisposable
{
    private const string Repository = "github.com/philipreese/baton";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"baton-2138-{Guid.NewGuid():N}");
    private readonly IDisposable _scope;

    public MemoryAutomaticProjectionTests() =>
        _scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = Path.Combine(_root, "baton") });

    private string ClaudeHome => Path.Combine(_root, "claude");
    private string UserHome => Path.Combine(_root, "home");
    private static string Slug => RepositoryIdentity.FileSlugFor(Repository);

    public void Dispose()
    {
        _scope.Dispose();
        DirectoryCleanup.DeleteRecursively(_root);
    }

    [Fact]
    public async Task Add_retract_and_import_each_project_the_successful_canonical_write()
    {
        var root = await CreateTargetAsync();
        var target = Path.Combine(root, ClaudeProjectionTarget.ProjectionFileName);

        var addOutput = new StringWriter();
        var addExit = await MemoryAddCommand.ExecuteAsync(
            MemoryAddOptionsParser.Parse([
                "--repository", Repository, "--kind", "durable-fact", "--text", "added fact",
            ]),
            addOutput,
            assertedByOverride: "test",
            cancellationToken: TestContext.Current.CancellationToken,
            claudeHomeOverride: ClaudeHome,
            userHomeOverride: UserHome);
        Assert.Equal(0, addExit);
        Assert.Contains("added fact", File.ReadAllText(target), StringComparison.Ordinal);
        Assert.Contains("does not establish that a vendor loaded or consumed", addOutput.ToString(), StringComparison.Ordinal);

        var entry = Assert.Single(await MemoryStore.ReadAllAsync(
            BatonPaths.MemoryEntriesFile(Slug), TestContext.Current.CancellationToken));
        var retractOutput = new StringWriter();
        var retractExit = await MemoryRetractCommand.ExecuteAsync(
            MemoryRetractOptionsParser.Parse([
                entry.Id, "--repository", Repository, "--reason", "fixture retraction",
            ]),
            retractOutput,
            retractedByOverride: "test",
            cancellationToken: TestContext.Current.CancellationToken,
            claudeHomeOverride: ClaudeHome,
            userHomeOverride: UserHome);
        Assert.Equal(0, retractExit);
        Assert.DoesNotContain("added fact", File.ReadAllText(target), StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(root, "project_imported.md"), "imported fact");
        var importOutput = new StringWriter();
        var importExit = await MemoryImportCommand.ExecuteAsync(
            MemoryImportOptionsParser.Parse(["--root", root]),
            importOutput,
            ClaudeHome,
            TestContext.Current.CancellationToken,
            UserHome);
        Assert.Equal(0, importExit);
        Assert.Contains("imported fact", File.ReadAllText(target), StringComparison.Ordinal);
        Assert.Contains("AUTOMATIC PROJECTION", importOutput.ToString(), StringComparison.Ordinal);

        var manifestPath = importOutput.ToString().Split('\n')
            .Select(line => line.Trim())
            .First(line => line.StartsWith("Manifest: ", StringComparison.Ordinal))["Manifest: ".Length..];
        var undoOutput = new StringWriter();
        Assert.Equal(0, await MemoryImportCommand.ExecuteAsync(
            MemoryImportOptionsParser.Parse(["--undo", manifestPath]),
            undoOutput,
            ClaudeHome,
            TestContext.Current.CancellationToken,
            UserHome));
        Assert.DoesNotContain("imported fact", File.ReadAllText(target), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Safety_sweep_is_mtime_idempotent_and_missing_root_is_not_invented()
    {
        var root = await CreateTargetAsync();
        await MemoryStore.AppendAsync(
            [Entry("fixture fact")], BatonPaths.MemoryEntriesFile(Slug), TestContext.Current.CancellationToken);

        var first = new StringWriter();
        Assert.Equal(0, await MemorySyncCommand.ExecuteAsync(
            MemorySyncOptionsParser.Parse(["--repository", Repository, "--apply"]),
            first,
            ClaudeHome,
            TestContext.Current.CancellationToken,
            UserHome));

        var target = Path.Combine(root, ClaudeProjectionTarget.ProjectionFileName);
        Assert.True(File.Exists(target)); // Existing root + missing generated file means create it.
        var pinnedMtime = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(target, pinnedMtime);

        var sweep = new MemoryProjectionSweep(() => DateTime.UtcNow, ClaudeHome, UserHome, null);
        await sweep.SweepOnceAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(pinnedMtime, File.GetLastWriteTimeUtc(target));

        DirectoryCleanup.DeleteRecursively(root);
        await sweep.SweepOnceAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(Directory.Exists(root)); // A missing vendor root is never recreated.
    }

    [Fact]
    public async Task Denied_projection_keeps_the_canonical_commit_and_a_fresh_sweep_recovers_it()
    {
        var root = await CreateTargetAsync();
        var now = new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);
        var output = new StringWriter();

        var exit = await MemoryAddCommand.ExecuteAsync(
            MemoryAddOptionsParser.Parse([
                "--repository", Repository, "--kind", "durable-fact", "--text", "survives denial",
            ]),
            output,
            assertedByOverride: "test",
            cancellationToken: TestContext.Current.CancellationToken,
            claudeHomeOverride: ClaudeHome,
            userHomeOverride: UserHome,
            projectionWriterOverride: (_, _) => throw new UnauthorizedAccessException("fixture denied"));

        Assert.Equal(0, exit); // Canonical success is not changed into a failed write result.
        Assert.Single(await MemoryStore.ReadAllAsync(
            BatonPaths.MemoryEntriesFile(Slug), TestContext.Current.CancellationToken));
        Assert.Contains("CANONICAL COMMIT SUCCEEDED; PROJECTION IS PENDING", output.ToString(), StringComparison.Ordinal);

        var pending = Assert.IsType<MemoryProjectionObligation>(
            await MemoryProjectionObligationStore.ReadAsync(Slug, TestContext.Current.CancellationToken));
        Assert.Equal(MemoryProjectionObligationStatus.Pending, pending.Status);
        Assert.Equal(1, pending.FailedAttempts);

        // A new service instance models daemon restart; the only recovery input is the durable file.
        now = pending.NextAttemptUtc!.Value;
        var restarted = new MemoryProjectionSweep(() => now, ClaudeHome, UserHome, projectionWriter: null);
        await restarted.SweepOnceAsync(cancellationToken: TestContext.Current.CancellationToken);

        var target = Path.Combine(root, ClaudeProjectionTarget.ProjectionFileName);
        Assert.Contains("survives denial", File.ReadAllText(target), StringComparison.Ordinal);
        Assert.Null(await MemoryProjectionObligationStore.ReadAsync(Slug, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Repeated_failure_backs_off_then_escalates_and_stops_automatic_attempts()
    {
        await CreateTargetAsync();
        await MemoryStore.AppendAsync(
            [Entry("persistent failure")], BatonPaths.MemoryEntriesFile(Slug), TestContext.Current.CancellationToken);

        var now = new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);
        var writes = 0;
        Action<string, byte[]> denied = (_, _) =>
        {
            writes++;
            throw new UnauthorizedAccessException("fixture denied repeatedly");
        };

        var initial = new StringWriter();
        Assert.Equal(1, await MemorySyncCommand.ExecuteAsync(
            MemorySyncOptionsParser.Parse(["--repository", Repository, "--apply"]),
            initial,
            ClaudeHome,
            TestContext.Current.CancellationToken,
            UserHome,
            projectionWriterOverride: denied,
            utcNowOverride: () => now));

        var diagnostics = new StringWriter();
        for (var attempt = 2; attempt <= MemoryProjectionObligationStore.EscalationAttemptCount; attempt++)
        {
            var pending = Assert.IsType<MemoryProjectionObligation>(
                await MemoryProjectionObligationStore.ReadAsync(Slug, TestContext.Current.CancellationToken));
            Assert.Equal(
                MemoryProjectionObligationStore.BackoffAfter(pending.FailedAttempts),
                pending.NextAttemptUtc!.Value - pending.LastAttemptUtc!.Value);
            now = pending.NextAttemptUtc!.Value;
            var restarted = new MemoryProjectionSweep(() => now, ClaudeHome, UserHome, denied);
            await restarted.SweepOnceAsync(diagnostics, TestContext.Current.CancellationToken);
        }

        var escalated = Assert.IsType<MemoryProjectionObligation>(
            await MemoryProjectionObligationStore.ReadAsync(Slug, TestContext.Current.CancellationToken));
        Assert.Equal(MemoryProjectionObligationStatus.Escalated, escalated.Status);
        Assert.Equal(MemoryProjectionObligationStore.EscalationAttemptCount, escalated.FailedAttempts);
        Assert.Null(escalated.NextAttemptUtc);
        Assert.Contains("memory sync --repository", escalated.NextAction, StringComparison.Ordinal);
        Assert.Contains("ESCALATED", diagnostics.ToString(), StringComparison.Ordinal);

        var attemptsAtEscalation = writes;
        var later = new MemoryProjectionSweep(() => now.AddDays(1), ClaudeHome, UserHome, denied);
        await later.SweepOnceAsync(diagnostics, TestContext.Current.CancellationToken);
        Assert.Equal(attemptsAtEscalation, writes);
    }

    [Fact]
    public async Task A_concurrent_canonical_append_cannot_be_lost_behind_an_older_projection()
    {
        var root = await CreateTargetAsync();
        var entriesFile = BatonPaths.MemoryEntriesFile(Slug);
        await MemoryStore.AppendAsync([Entry("older snapshot")], entriesFile, TestContext.Current.CancellationToken);

        using var writerEntered = new ManualResetEventSlim();
        using var releaseWriter = new ManualResetEventSlim();
        var first = MemorySyncCommand.ExecuteAsync(
            MemorySyncOptionsParser.Parse(["--repository", Repository, "--apply"]),
            TextWriter.Null,
            ClaudeHome,
            TestContext.Current.CancellationToken,
            UserHome,
            projectionWriterOverride: (path, bytes) =>
            {
                writerEntered.Set();
                releaseWriter.Wait(TestContext.Current.CancellationToken);
                File.WriteAllBytes(path, bytes);
            });

        Assert.True(writerEntered.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        var append = MemoryStore.AppendAsync(
            [Entry("newer canonical write")], entriesFile, TestContext.Current.CancellationToken);
        Assert.False(append.IsCompleted); // Publication holds the store snapshot boundary.

        releaseWriter.Set();
        Assert.Equal(0, await first);
        await append;

        Assert.Equal(0, await MemorySyncCommand.ExecuteAsync(
            MemorySyncOptionsParser.Parse(["--repository", Repository, "--apply"]),
            TextWriter.Null,
            ClaudeHome,
            TestContext.Current.CancellationToken,
            UserHome));

        var projected = File.ReadAllText(Path.Combine(root, ClaudeProjectionTarget.ProjectionFileName));
        Assert.Contains("older snapshot", projected, StringComparison.Ordinal);
        Assert.Contains("newer canonical write", projected, StringComparison.Ordinal);
    }

    private async Task<string> CreateTargetAsync()
    {
        var root = Path.Combine(ClaudeHome, "projects", "c--fixture", "memory");
        Directory.CreateDirectory(root);
        await MemoryAliasStore.AppendAsync(
            [new MemoryAliasEntry(BatonPaths.RecordKey(root), Repository, "test", default)],
            BatonPaths.MemoryAliasFile,
            TestContext.Current.CancellationToken);
        return root;
    }

    private static MemoryEntry Entry(string text)
    {
        var path = $"C:/fixture/{Guid.NewGuid():N}.md";
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)))
            .ToLowerInvariant();
        return new MemoryEntry(
            MemoryEntry.Derive(Repository, path, digest),
            Repository,
            MemoryKind.DurableFact,
            MemoryKindSource.Declared,
            text,
            digest,
            path,
            MemoryRootInventory.ClaudeVendor,
            VendorMemoryScope.Vendor,
            default,
            default);
    }
}
