using System.Text;
using System.Text.Json;
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
    private const string OtherRepository = "github.com/example/other";
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
    public async Task Obligation_creation_failure_reports_unrecorded_commit_and_inventory_recovers_it()
    {
        var root = await CreateTargetAsync();
        var obligationPath = BatonPaths.MemorySyncPendingFile(Slug);
        Directory.CreateDirectory(obligationPath); // A directory at the file path makes claim publication fail.

        var output = new StringWriter();
        var exit = await MemoryAddCommand.ExecuteAsync(
            MemoryAddOptionsParser.Parse([
                "--repository", Repository, "--kind", "durable-fact", "--text", "recoverable commit",
            ]),
            output,
            assertedByOverride: "test",
            cancellationToken: TestContext.Current.CancellationToken,
            claudeHomeOverride: ClaudeHome,
            userHomeOverride: UserHome);

        Assert.Equal(0, exit);
        Assert.Single(await MemoryStore.ReadAllAsync(
            BatonPaths.MemoryEntriesFile(Slug), TestContext.Current.CancellationToken));
        Assert.Contains("COMMITTED BUT UNPROJECTED", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("PROJECTION IS UNRECORDED", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("PROJECTION IS PENDING", output.ToString(), StringComparison.Ordinal);
        Assert.True(File.Exists(BatonPaths.MemoryStoreMetadataFile(Slug)));

        Directory.Delete(obligationPath);
        var restarted = new MemoryProjectionSweep(() => DateTime.UtcNow, ClaudeHome, UserHome, null);
        await restarted.SweepOnceAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(
            "recoverable commit",
            File.ReadAllText(Path.Combine(root, ClaudeProjectionTarget.ProjectionFileName)),
            StringComparison.Ordinal);
        Assert.Null(await MemoryProjectionObligationStore.ReadAsync(Slug, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Empty_store_identity_survives_undo_and_restart_without_a_pending_claim()
    {
        var root = await CreateTargetAsync();
        var target = Path.Combine(root, ClaudeProjectionTarget.ProjectionFileName);
        File.WriteAllText(Path.Combine(root, "user_only.md"), "the only canonical row");

        var importOutput = new StringWriter();
        Assert.Equal(0, await MemoryImportCommand.ExecuteAsync(
            MemoryImportOptionsParser.Parse(["--root", root]),
            importOutput,
            ClaudeHome,
            TestContext.Current.CancellationToken,
            UserHome));
        var manifestPath = importOutput.ToString().Split('\n')
            .Select(line => line.Trim())
            .First(line => line.StartsWith("Manifest: ", StringComparison.Ordinal))["Manifest: ".Length..];

        Assert.Equal(0, await MemoryImportCommand.ExecuteAsync(
            MemoryImportOptionsParser.Parse(["--undo", manifestPath]),
            TextWriter.Null,
            ClaudeHome,
            TestContext.Current.CancellationToken,
            UserHome));
        Assert.Empty(await MemoryStore.ReadAllAsync(
            BatonPaths.MemoryEntriesFile(Slug), TestContext.Current.CancellationToken));
        Assert.Null(await MemoryProjectionObligationStore.ReadAsync(Slug, TestContext.Current.CancellationToken));
        var location = Assert.Single(CanonicalStoreInventory.Scan(BatonPaths.Root), l => l.Slug == Slug);
        Assert.Equal(Repository, location.Repository);

        var auditOutput = new StringWriter();
        Assert.Equal(0, await MemoryAuditCommand.ExecuteAsync(
            new MemoryAuditOptions(),
            auditOutput,
            ClaudeHome,
            TestContext.Current.CancellationToken,
            UserHome,
            BatonPaths.Root));
        Assert.Contains(
            $"repository={Repository} slug={Slug} entries=0",
            auditOutput.ToString(),
            StringComparison.Ordinal);

        File.WriteAllText(target, "stale projection bytes");
        var restarted = new MemoryProjectionSweep(() => DateTime.UtcNow, ClaudeHome, UserHome, null);
        await restarted.SweepOnceAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain("stale projection bytes", File.ReadAllText(target), StringComparison.Ordinal);
        Assert.Contains(MemoryProjection.FormatMarker, File.ReadAllText(target), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Duplicate_alias_assertion_does_not_replace_an_escalated_obligation()
    {
        var root = await CreateTargetAsync();
        var now = new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);
        var obligation = await MemoryProjectionObligationStore.ReplaceAsync(
            Repository, Slug, now, TestContext.Current.CancellationToken);
        for (var attempt = 0; attempt < MemoryProjectionObligationStore.EscalationAttemptCount; attempt++)
        {
            Assert.True(await MemoryProjectionObligationStore.FailAsync(
                obligation,
                new UnauthorizedAccessException("fixture escalation"),
                now.AddMinutes(attempt),
                TestContext.Current.CancellationToken));
        }

        var before = Assert.IsType<MemoryProjectionObligation>(
            await MemoryProjectionObligationStore.ReadAsync(Slug, TestContext.Current.CancellationToken));
        Assert.Equal(MemoryProjectionObligationStatus.Escalated, before.Status);

        var output = new StringWriter();
        var caseVariantRoot = root.ToUpperInvariant();
        Assert.NotEqual(root, caseVariantRoot, StringComparer.Ordinal);
        Assert.Equal(0, await MemoryImportCommand.ExecuteAsync(
            MemoryImportOptionsParser.Parse([
                "--assert", $"{caseVariantRoot}={Repository}", "--asserted-by", "test",
            ]),
            output,
            ClaudeHome,
            TestContext.Current.CancellationToken,
            UserHome));

        var after = Assert.IsType<MemoryProjectionObligation>(
            await MemoryProjectionObligationStore.ReadAsync(Slug, TestContext.Current.CancellationToken));
        Assert.Equal(before, after);
        Assert.DoesNotContain("AUTOMATIC PROJECTION", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Case_variant_alias_is_also_a_noop_in_dry_run_resolution()
    {
        var root = await CreateTargetAsync();
        File.WriteAllText(Path.Combine(root, "user_fact.md"), "fixture alias fact");
        var caseVariantRoot = root.ToUpperInvariant();
        Assert.NotEqual(root, caseVariantRoot, StringComparer.Ordinal);

        var output = new StringWriter();
        Assert.Equal(0, await MemoryImportCommand.ExecuteAsync(
            MemoryImportOptionsParser.Parse([
                "--dry-run",
                "--root", root,
                "--assert", $"{caseVariantRoot}={OtherRepository}",
                "--asserted-by", "test",
            ]),
            output,
            ClaudeHome,
            TestContext.Current.CancellationToken,
            UserHome));

        Assert.Contains($"repository={Repository}", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain($"repository={OtherRepository}", output.ToString(), StringComparison.Ordinal);
        Assert.Single(await MemoryAliasStore.ReadAllAsync(
            BatonPaths.MemoryAliasFile, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Dry_run_same_batch_aliases_keep_the_first_case_equivalent_assertion()
    {
        var root = Path.Combine(ClaudeHome, "projects", "c--unresolved", "memory");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "user_fact.md"), "fixture alias fact");
        var caseVariantRoot = root.ToUpperInvariant();
        Assert.NotEqual(root, caseVariantRoot, StringComparer.Ordinal);

        var output = new StringWriter();
        Assert.Equal(0, await MemoryImportCommand.ExecuteAsync(
            MemoryImportOptionsParser.Parse([
                "--dry-run",
                "--root", root,
                "--assert", $"{root}={Repository}",
                "--assert", $"{caseVariantRoot}={OtherRepository}",
                "--asserted-by", "test",
            ]),
            output,
            ClaudeHome,
            TestContext.Current.CancellationToken,
            UserHome));

        Assert.Contains($"repository={Repository}", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain($"repository={OtherRepository}", output.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(BatonPaths.MemoryAliasFile));
    }

    [Fact]
    public void Custom_root_inventory_reads_metadata_from_the_scanned_root()
    {
        var customRoot = Path.Combine(_root, "custom-baton");
        var customMetadata = Path.Combine(
            customRoot, Slug, BatonPaths.MemoryDirectoryName, BatonPaths.MemoryStoreMetadataFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(customMetadata)!);
        File.WriteAllText(
            customMetadata,
            JsonSerializer.Serialize(
                new MemoryStoreMetadata(MemoryStoreMetadata.CurrentVersion, Repository, Slug),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var processMetadata = BatonPaths.MemoryStoreMetadataFile(Slug);
        Directory.CreateDirectory(Path.GetDirectoryName(processMetadata)!);
        File.WriteAllText(processMetadata, "{ invalid process-root metadata");

        var location = Assert.Single(CanonicalStoreInventory.Scan(customRoot));
        Assert.Equal(Repository, location.Repository);
        Assert.Equal(Slug, location.Slug);
    }

    [Fact]
    public async Task Store_metadata_refuses_a_foreign_row_before_any_projection()
    {
        var foreignRoot = await CreateTargetAsync(OtherRepository, "c--foreign");
        var foreignTarget = Path.Combine(foreignRoot, ClaudeProjectionTarget.ProjectionFileName);
        await MemoryStoreMetadataStore.EnsureAsync(
            Repository, Slug, TestContext.Current.CancellationToken);
        await MemoryStore.AppendAsync(
            [Entry("foreign row", OtherRepository)],
            BatonPaths.MemoryEntriesFile(Slug),
            TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => MemorySyncCommand.ExecuteAsync(
            MemorySyncOptionsParser.Parse(["--repository", Repository, "--apply"]),
            TextWriter.Null,
            ClaudeHome,
            TestContext.Current.CancellationToken,
            UserHome));

        Assert.Contains(Repository, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(OtherRepository, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(foreignTarget));
        Assert.Null(await MemoryProjectionObligationStore.ReadAsync(Slug, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Invalid_or_inaccessible_metadata_never_falls_back_to_legacy_rows()
    {
        var root = await CreateTargetAsync();
        var target = Path.Combine(root, ClaudeProjectionTarget.ProjectionFileName);
        await MemoryStore.AppendAsync(
            [Entry("legacy-looking row")],
            BatonPaths.MemoryEntriesFile(Slug),
            TestContext.Current.CancellationToken);
        var metadataPath = BatonPaths.MemoryStoreMetadataFile(Slug);

        File.WriteAllText(metadataPath, "{ not valid json");
        await Assert.ThrowsAsync<InvalidDataException>(() => MemorySyncCommand.ExecuteAsync(
            MemorySyncOptionsParser.Parse(["--repository", Repository, "--apply"]),
            TextWriter.Null,
            ClaudeHome,
            TestContext.Current.CancellationToken,
            UserHome));
        Assert.False(File.Exists(target));

        FileCleanup.EnsureDeleted(metadataPath);
        Directory.CreateDirectory(metadataPath);
        var inaccessible = await Assert.ThrowsAnyAsync<Exception>(() => MemorySyncCommand.ExecuteAsync(
            MemorySyncOptionsParser.Parse(["--repository", Repository, "--apply"]),
            TextWriter.Null,
            ClaudeHome,
            TestContext.Current.CancellationToken,
            UserHome));
        Assert.True(inaccessible is IOException or UnauthorizedAccessException, inaccessible.ToString());
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task Mismatched_obligation_is_refused_before_any_projection()
    {
        var root = await CreateTargetAsync();
        var target = Path.Combine(root, ClaudeProjectionTarget.ProjectionFileName);
        await MemoryStoreMetadataStore.EnsureAsync(
            Repository, Slug, TestContext.Current.CancellationToken);
        await MemoryStore.AppendAsync(
            [Entry("owned row")],
            BatonPaths.MemoryEntriesFile(Slug),
            TestContext.Current.CancellationToken);
        var mismatched = new MemoryProjectionObligation(
            "fixture-attempt",
            OtherRepository,
            Slug,
            MemoryProjectionObligationStatus.Pending,
            0,
            DateTime.UtcNow,
            null,
            DateTime.UtcNow,
            null,
            null);

        await Assert.ThrowsAsync<InvalidDataException>(() => MemorySyncCommand.ExecuteAsync(
            MemorySyncOptionsParser.Parse(["--repository", Repository, "--apply"]),
            TextWriter.Null,
            ClaudeHome,
            TestContext.Current.CancellationToken,
            UserHome,
            new Dictionary<string, MemoryProjectionObligation>(StringComparer.OrdinalIgnoreCase)
            {
                [Slug] = mismatched,
            }));

        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task Undefined_and_stalled_obligation_states_are_rejected_on_read()
    {
        var now = DateTime.UtcNow;
        var invalid = new[]
        {
            new MemoryProjectionObligation(
                "undefined", Repository, Slug, (MemoryProjectionObligationStatus)99,
                0, now, null, now, null, null),
            new MemoryProjectionObligation(
                "pending-without-due", Repository, Slug, MemoryProjectionObligationStatus.Pending,
                0, now, null, null, null, null),
            new MemoryProjectionObligation(
                "escalated-with-due", Repository, Slug, MemoryProjectionObligationStatus.Escalated,
                MemoryProjectionObligationStore.EscalationAttemptCount, now, now, now, "failure", "repair"),
        };
        var path = BatonPaths.MemorySyncPendingFile(Slug);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        foreach (var obligation in invalid)
        {
            File.WriteAllText(
                path,
                JsonSerializer.Serialize(obligation, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            await Assert.ThrowsAsync<InvalidDataException>(() => MemoryProjectionObligationStore.ReadAsync(
                Slug, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Cancellation_after_commit_reports_pending_without_reporting_projection_finished()
    {
        await CreateTargetAsync();
        using var cancellation = new CancellationTokenSource();
        var output = new StringWriter();

        var exit = await MemoryAddCommand.ExecuteAsync(
            MemoryAddOptionsParser.Parse([
                "--repository", Repository, "--kind", "durable-fact", "--text", "committed before cancellation",
            ]),
            output,
            assertedByOverride: "test",
            cancellationToken: cancellation.Token,
            claudeHomeOverride: ClaudeHome,
            userHomeOverride: UserHome,
            projectionWriterOverride: (_, _) =>
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            });

        Assert.Equal(0, exit);
        Assert.Single(await MemoryStore.ReadAllAsync(
            BatonPaths.MemoryEntriesFile(Slug), TestContext.Current.CancellationToken));
        Assert.NotNull(await MemoryProjectionObligationStore.ReadAsync(
            Slug, TestContext.Current.CancellationToken));
        Assert.Contains("CANONICAL COMMIT SUCCEEDED; PROJECTION IS PENDING", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("AUTOMATIC PROJECTION FINISHED", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_while_metadata_initialization_waits_does_not_create_a_store()
    {
        var metadataPath = BatonPaths.MemoryStoreMetadataFile(Slug);
        using var holderReady = new ManualResetEventSlim();
        using var releaseHolder = new ManualResetEventSlim();
        using var ensureStarted = new ManualResetEventSlim();
        var holder = Task.Run(
            () =>
            {
                using var mutex = new Mutex(
                    initiallyOwned: false,
                    MutexGuardedFileLock.BuildMutexName(metadataPath, MemoryStoreMetadataStore.LockNamePrefix));
                Assert.True(mutex.WaitOne(TimeSpan.FromMinutes(1)));
                try
                {
                    holderReady.Set();
                    releaseHolder.Wait(TestContext.Current.CancellationToken);
                }
                finally
                {
                    mutex.ReleaseMutex();
                }
            },
            TestContext.Current.CancellationToken);
        Assert.True(holderReady.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

        using var cancellation = new CancellationTokenSource();
        MemoryStoreMetadataStore.EnsureOperationObserver = path =>
        {
            if (string.Equals(path, metadataPath, StringComparison.OrdinalIgnoreCase))
            {
                ensureStarted.Set();
            }
        };
        var add = MemoryAddCommand.ExecuteAsync(
            MemoryAddOptionsParser.Parse([
                "--repository", Repository, "--kind", "durable-fact", "--text", "must not commit",
            ]),
            TextWriter.Null,
            assertedByOverride: "test",
            cancellationToken: cancellation.Token,
            claudeHomeOverride: ClaudeHome,
            userHomeOverride: UserHome);

        try
        {
            Assert.True(ensureStarted.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
            cancellation.Cancel();
            releaseHolder.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => add);
            await holder;
        }
        finally
        {
            MemoryStoreMetadataStore.EnsureOperationObserver = null;
            releaseHolder.Set();
        }

        Assert.False(File.Exists(BatonPaths.MemoryEntriesFile(Slug)));
        Assert.False(File.Exists(metadataPath));
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
    public async Task A_superseded_attempt_cannot_publish_its_stale_snapshot()
    {
        var repositoryRoot = await CreateTargetAsync();
        var fleetRoot = await CreateFleetTargetAsync();
        await MemoryStoreMetadataStore.EnsureAsync(
            Repository, Slug, TestContext.Current.CancellationToken);
        await MemoryStoreMetadataStore.EnsureAsync(
            FleetMemory.Slug, FleetMemory.Slug, TestContext.Current.CancellationToken);
        await MemoryStore.AppendAsync(
            [Entry("stale repository snapshot")],
            BatonPaths.MemoryEntriesFile(Slug),
            TestContext.Current.CancellationToken);
        await MemoryStore.AppendAsync(
            [Entry("fleet pause", FleetMemory.Slug)],
            FleetMemory.EntriesFile,
            TestContext.Current.CancellationToken);

        var repositoryTarget = Path.Combine(repositoryRoot, ClaudeProjectionTarget.ProjectionFileName);
        var fleetTarget = Path.Combine(fleetRoot, ClaudeProjectionTarget.ProjectionFileName);
        File.WriteAllText(repositoryTarget, "newer projection sentinel");

        using var writerEntered = new ManualResetEventSlim();
        using var releaseWriter = new ManualResetEventSlim();
        var first = MemorySyncCommand.ExecuteAsync(
            MemorySyncOptionsParser.Parse(["--apply"]),
            TextWriter.Null,
            ClaudeHome,
            TestContext.Current.CancellationToken,
            UserHome,
            projectionWriterOverride: (path, bytes) =>
            {
                if (string.Equals(path, fleetTarget, StringComparison.OrdinalIgnoreCase))
                {
                    writerEntered.Set();
                    releaseWriter.Wait(TestContext.Current.CancellationToken);
                }

                File.WriteAllBytes(path, bytes);
            });

        Assert.True(writerEntered.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        var oldRepositoryAttempt = Assert.IsType<MemoryProjectionObligation>(
            await MemoryProjectionObligationStore.ReadAsync(Slug, TestContext.Current.CancellationToken));
        var newerRepositoryAttempt = await MemoryProjectionObligationStore.ReplaceAsync(
            Repository, Slug, DateTime.UtcNow, TestContext.Current.CancellationToken);
        Assert.NotEqual(oldRepositoryAttempt.AttemptId, newerRepositoryAttempt.AttemptId);

        releaseWriter.Set();
        Assert.Equal(0, await first);
        Assert.Equal("newer projection sentinel", File.ReadAllText(repositoryTarget));
        Assert.Equal(
            newerRepositoryAttempt,
            await MemoryProjectionObligationStore.ReadAsync(Slug, TestContext.Current.CancellationToken));
    }

    private async Task<string> CreateTargetAsync(
        string repository = Repository,
        string directoryName = "c--fixture")
    {
        var root = Path.Combine(ClaudeHome, "projects", directoryName, "memory");
        Directory.CreateDirectory(root);
        await MemoryAliasStore.AppendAsync(
            [new MemoryAliasEntry(BatonPaths.RecordKey(root), repository, "test", default)],
            BatonPaths.MemoryAliasFile,
            TestContext.Current.CancellationToken);
        return root;
    }

    private async Task<string> CreateFleetTargetAsync()
    {
        var root = Path.Combine(ClaudeHome, "projects", "c--fleet", "memory");
        Directory.CreateDirectory(root);
        await MemoryAliasStore.AppendAsync(
            [new MemoryAliasEntry(BatonPaths.RecordKey(root), FleetMemory.Slug, "test", default)],
            BatonPaths.MemoryAliasFile,
            TestContext.Current.CancellationToken);
        return root;
    }

    private static MemoryEntry Entry(string text, string repository = Repository)
    {
        var path = $"C:/fixture/{Guid.NewGuid():N}.md";
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)))
            .ToLowerInvariant();
        return new MemoryEntry(
            MemoryEntry.Derive(repository, path, digest),
            repository,
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
