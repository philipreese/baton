using System.Diagnostics;
using System.Text.Json;
using Baton.Concurrency;
using Baton.Domain;
using Baton.Queue;
using Baton.Status;
using Baton.Tests.Shared;
using Baton.Vendors;

namespace Baton.Cli.Tests;

/// <summary>
/// #2318's deletion-adjacent classifier is exercised against real Git repositories. A string fake
/// cannot prove registration, attached-ref, HEAD, dirtiness, or the promise that observing them does
/// not mutate Git metadata.
/// </summary>
public sealed class QueueWorktreeReportTests
{
    private const string Repository = "github.com/example/retained-worktrees";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Mixed_fixture_fails_closed_and_text_and_json_project_the_same_report()
    {
        var sandbox = Temp("mixed");
        var home = Path.Combine(sandbox, "home");
        var root = Path.Combine(sandbox, "worktrees");
        Directory.CreateDirectory(root);
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var candidate = await RepoAsync(root, "candidate");
            await File.WriteAllTextAsync(Path.Combine(candidate.Path, "local.txt"), "local only", Ct);
            await GitAsync(candidate.Path, "add", "local.txt");
            await CommitAsync(candidate.Path, "local-only commit");

            var cancelled = await RepoAsync(root, "cancelled-before-launch");
            var operatorRepo = await RepoAsync(root, "operator");
            var imported = await RepoAsync(root, "imported");
            var historical = await RepoAsync(root, "historical");
            var active = await RepoAsync(root, "active");
            var failed = await RepoAsync(root, "failed");
            var halted = await RepoAsync(root, "halted");
            var dirty = await RepoAsync(root, "dirty");
            Directory.CreateDirectory(Path.Combine(dirty.Path, ".claude"));
            await File.WriteAllTextAsync(Path.Combine(dirty.Path, ".claude", "state.json"), "substantive", Ct);
            var detached = await RepoAsync(root, "detached");
            await GitAsync(detached.Path, "checkout", "--detach", "-q");
            var missingRef = await RepoAsync(root, "missing-ref");
            var wrongRepository = await RepoAsync(root, "wrong-repository");
            var outside = await RepoAsync(Path.Combine(sandbox, "outside"), "outside-root");
            var stale = await StaleLinkedWorktreeAsync(sandbox, root);
            var oversized = await RepoAsync(root, "oversized");
            await using (var stream = new FileStream(Path.Combine(oversized.Path, "sparse.bin"), FileMode.CreateNew, FileAccess.Write))
                stream.SetLength((1L << 30) + 1);

            var items = new List<QueueItem>
            {
                Item(candidate, "candidate"),
                Item(cancelled, "cancelled", state: QueueItemState.Cancelled, retired: false),
                Item(operatorRepo, "operator", origin: WorkspaceOrigins.OperatorSupplied),
                Item(imported, "imported", origin: WorkspaceOrigins.ImportedUnknown),
                Item(historical, "historical", origin: null),
                Item(active, "active", retired: false),
                Item(failed, "failed", state: QueueItemState.Failed),
                Item(halted, "halted", halted: true),
                Item(dirty, "dirty"),
                Item(detached, "detached"),
                Item(missingRef with { Branch = "does-not-exist" }, "missing-ref"),
                Item(wrongRepository with { Repository = "github.com/example/somewhere-else" }, "wrong-repository"),
                Item(outside, "outside-root"),
                Item(stale, "stale-registration"),
                Item(oversized, "oversized"),
                new QueueItem
                {
                    Tag = "missing-directory", Role = "implement", Workspace = Path.Combine(root, "gone"),
                    SpecFile = Path.Combine(home, "missing.md"), WorkspaceOrigin = WorkspaceOrigins.IssueProvisioned,
                    Repository = Repository, Branch = "missing-directory", State = QueueItemState.Queued,
                    Retirement = Retired(),
                },
            };

            var report = await QueueWorktreeReport.CreateAsync(items, root, Ct);

            var candidateEntry = Find(report, "candidate");
            Assert.True(candidateEntry.Classification == "candidate",
                $"candidate was {candidateEntry.Classification}: {string.Join(", ", candidateEntry.ReasonCodes)}");
            Assert.Equal("candidate", Find(report, "cancelled-before-launch").Classification);
            AssertReason(report, "operator", "origin-not-owned", "retain");
            AssertReason(report, "imported", "origin-not-owned", "retain");
            AssertReason(report, "historical", "origin-not-owned", "unknown");
            AssertReason(report, "active", "active-or-unretired-row", "retain");
            AssertReason(report, "failed", "failed-or-halted-row", "retain");
            AssertReason(report, "halted", "failed-or-halted-row", "retain");
            AssertReason(report, "dirty", "substantive-uncommitted-content", "retain");
            AssertReason(report, "detached", "detached-head", "retain");
            AssertReason(report, "missing-ref", "expected-branch-probe-unavailable", "unknown");
            AssertReason(report, "wrong-repository", "wrong-repository", "retain");
            AssertReason(report, "outside-root", "outside-configured-root", "retain");
            AssertReason(report, "stale-registration", "stale-registration", "retain");
            AssertReason(report, "oversized", "size-observation-unavailable", "unknown");
            AssertReason(report, "gone", "missing-directory", "unknown");
            Assert.Contains(".claude/state.json", Find(report, "dirty").Git.RawStatus!.Replace('\\', '/'), StringComparison.Ordinal);

            using var json = JsonDocument.Parse(report.ToJson());
            var jsonRows = json.RootElement.GetProperty("Workspaces").EnumerateArray().ToList();
            Assert.Equal(report.Workspaces.Count, jsonRows.Count);
            var text = report.ToText();
            foreach (var entry in report.Workspaces)
            {
                var projected = Assert.Single(jsonRows, row => row.GetProperty("Path").GetString() == entry.Path);
                Assert.Equal(entry.Classification, projected.GetProperty("Classification").GetString());
                Assert.Equal(entry.ReasonCodes, projected.GetProperty("ReasonCodes").EnumerateArray().Select(value => value.GetString()));
                Assert.Contains(entry.Path, text, StringComparison.Ordinal);
                Assert.Contains(entry.Classification, text, StringComparison.Ordinal);
                foreach (var reason in entry.ReasonCodes) Assert.Contains(reason, text, StringComparison.Ordinal);
                foreach (var row in entry.Rows)
                {
                    Assert.Contains(row.Origin, text, StringComparison.Ordinal);
                    Assert.Contains(row.Repository!, text, StringComparison.Ordinal);
                    Assert.Contains(row.Branch!, text, StringComparison.Ordinal);
                    Assert.Contains($"retired={row.Retired}", text, StringComparison.Ordinal);
                }
            }
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(sandbox);
        }
    }

    [Fact]
    public async Task Unavailable_repository_probe_stays_unknown()
    {
        var sandbox = Temp("repository-probe-unavailable");
        var home = Path.Combine(sandbox, "home");
        var root = Path.Combine(sandbox, "worktrees");
        Directory.CreateDirectory(root);
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var repo = await RepoAsync(root, "repository-probe-unavailable");
            var report = await QueueWorktreeReport.CreateAsync(
                [Item(repo, "repository-probe-unavailable")],
                root,
                Ct,
                (_, _) => Task.FromResult<Baton.Accounting.RepositoryIdentity?>(null));

            AssertReason(report, "repository-probe-unavailable", "repository-probe-unavailable", "unknown");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(sandbox);
        }
    }

    [Fact]
    public async Task Raw_status_is_bounded_without_hiding_dirtiness()
    {
        var sandbox = Temp("bounded-status");
        var home = Path.Combine(sandbox, "home");
        var root = Path.Combine(sandbox, "worktrees");
        Directory.CreateDirectory(root);
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var repo = await RepoAsync(root, "many-untracked");
            for (var index = 0; index < 400; index++)
                await File.WriteAllTextAsync(Path.Combine(repo.Path, $"untracked-{index:D4}-{new string('x', 40)}.txt"), "x", Ct);

            var entry = Find(await QueueWorktreeReport.CreateAsync([Item(repo, "many-untracked")], root, Ct), "many-untracked");

            Assert.Equal("dirty", entry.Git.SubstantiveCleanliness);
            Assert.True(entry.Git.RawStatusTruncated);
            Assert.NotNull(entry.Git.RawStatus);
            Assert.True(entry.Git.RawStatus!.Length <= 16_384);
            Assert.Equal("retain", entry.Classification);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(sandbox);
        }
    }

    [Fact]
    public async Task Repeated_inventory_does_not_mutate_workspace_git_room_queue_or_ledger_bytes()
    {
        var sandbox = Temp("read-only");
        var home = Path.Combine(sandbox, "home");
        var root = Path.Combine(sandbox, "worktrees");
        Directory.CreateDirectory(root);
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var repo = await RepoAsync(root, "stable");
            var room = Path.Combine(BatonPaths.Rooms, "settled-room");
            Directory.CreateDirectory(room);
            await File.WriteAllTextAsync(Path.Combine(room, BatonPaths.FlowLockFileName), "released lock file", Ct);
            Directory.CreateDirectory(Path.GetDirectoryName(BatonPaths.QueueFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(BatonPaths.QueueDecisionLedgerFile)!);
            await File.WriteAllTextAsync(BatonPaths.QueueFile, "queue sentinel", Ct);
            await File.WriteAllTextAsync(BatonPaths.QueueDecisionLedgerFile, "ledger sentinel", Ct);
            var item = Item(repo, "stable");

            var beforeFiles = SnapshotRegularFiles(repo.Path);
            var beforeRefs = await GitOutputAsync(repo.Path, "for-each-ref", "--format=%(refname) %(objectname)");
            var beforeRegistration = await GitOutputAsync(repo.Path, "worktree", "list", "--porcelain");
            var beforeState = SnapshotRegularFiles(home);

            var first = await QueueWorktreeReport.CreateAsync([item], root, Ct);
            var second = await QueueWorktreeReport.CreateAsync([item], root, Ct);

            Assert.Equal(first.ToJson(), second.ToJson());
            Assert.Equal(beforeFiles, SnapshotRegularFiles(repo.Path));
            Assert.Equal(beforeRefs, await GitOutputAsync(repo.Path, "for-each-ref", "--format=%(refname) %(objectname)"));
            Assert.Equal(beforeRegistration, await GitOutputAsync(repo.Path, "worktree", "list", "--porcelain"));
            Assert.Equal(beforeState, SnapshotRegularFiles(home));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(sandbox);
        }
    }

    [Fact]
    public async Task Live_continuation_room_is_an_active_reference_but_a_released_lock_file_is_not()
    {
        var sandbox = Temp("active-room");
        var home = Path.Combine(sandbox, "home");
        var root = Path.Combine(sandbox, "worktrees");
        Directory.CreateDirectory(root);
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var repo = await RepoAsync(root, "referenced");
            var room = Path.Combine(BatonPaths.Rooms, "continuation-room");
            Directory.CreateDirectory(room);
            await WorkerBindingConfigWriter.SaveToFileAsync(
                new Dictionary<string, WorkerBindingConfigEntry>
                {
                    ["implement"] = new(
                        "shell",
                        new WorkerContract("implement", [], [], []),
                        PromptTemplate: "echo fixture",
                        Timeout: TimeSpan.FromMinutes(1),
                        WorkingDirectory: repo.Path),
                },
                BatonPaths.RoomBindingsFile(room),
                Ct);
            await InteractiveSessionMaterializer.WriteWorkflowRoomMarkerAsync(
                room,
                parentRoomDirectoryPath: Path.Combine(BatonPaths.Rooms, "parent"),
                parentExecutionId: "parent-exec",
                continuedSessionId: "vendor-session",
                cancellationToken: Ct);

            using (ConcurrencyGuard.Acquire(room, "classifier fixture"))
            {
                var held = Find(await QueueWorktreeReport.CreateAsync([Item(repo, "referenced")], root, Ct), "referenced");
                Assert.Contains("room:continuation-room", held.ActiveReferences, StringComparison.Ordinal);
                Assert.Contains("continuation:continuation-room", held.ActiveReferences, StringComparison.Ordinal);
                Assert.Equal("retain", held.Classification);
            }

            var released = Find(await QueueWorktreeReport.CreateAsync([Item(repo, "referenced")], root, Ct), "referenced");
            Assert.Equal("none-known", released.ActiveReferences);
            Assert.Equal("candidate", released.Classification);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(sandbox);
        }
    }

    [Fact]
    public async Task Live_room_owns_its_recorded_branch_even_from_another_clone_path()
    {
        var sandbox = Temp("branch-owner");
        var home = Path.Combine(sandbox, "home");
        var root = Path.Combine(sandbox, "worktrees");
        Directory.CreateDirectory(root);
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var candidate = await RepoAsync(root, "candidate");
            var otherClone = await RepoAsync(root, "2333-lane");
            var room = Path.Combine(BatonPaths.Rooms, "branch-owner-room");
            Directory.CreateDirectory(room);
            await WorkerBindingConfigWriter.SaveToFileAsync(
                new Dictionary<string, WorkerBindingConfigEntry>
                {
                    ["implement"] = new(
                        "shell", new WorkerContract("implement", [], [], []),
                        PromptTemplate: "echo fixture", Timeout: TimeSpan.FromMinutes(1),
                        WorkingDirectory: otherClone.Path),
                },
                BatonPaths.RoomBindingsFile(room), Ct);
            await File.WriteAllTextAsync(RoomDeliveryBranch.PathFor(room), "2333-lane", Ct);

            using (ConcurrencyGuard.Acquire(room, "branch ownership fixture"))
            {
                var index = await QueueWorktreeReferenceIndex.CreateAsync(
                    [Item(candidate with { Branch = "2333-lane" }, "candidate")], Ct);

                Assert.True(index.Complete);
                Assert.Contains("room:branch-owner-room", index.ForBranch("2333-lane"));
                Assert.Empty(index.For(candidate.Path));
            }
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(sandbox);
        }
    }

    [Fact]
    public async Task Live_room_branch_is_probed_when_the_optional_branch_record_is_absent()
    {
        var sandbox = Temp("branch-probe");
        var home = Path.Combine(sandbox, "home");
        var root = Path.Combine(sandbox, "worktrees");
        Directory.CreateDirectory(root);
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var candidate = await RepoAsync(root, "candidate");
            var otherClone = await RepoAsync(root, "2333-lane");
            var room = Path.Combine(BatonPaths.Rooms, "unrecorded-branch-room");
            Directory.CreateDirectory(room);
            await WorkerBindingConfigWriter.SaveToFileAsync(
                new Dictionary<string, WorkerBindingConfigEntry>
                {
                    ["implement"] = new(
                        "shell", new WorkerContract("implement", [], [], []),
                        PromptTemplate: "echo fixture", Timeout: TimeSpan.FromMinutes(1),
                        WorkingDirectory: otherClone.Path),
                },
                BatonPaths.RoomBindingsFile(room), Ct);
            Assert.False(File.Exists(RoomDeliveryBranch.PathFor(room)));

            using (ConcurrencyGuard.Acquire(room, "missing branch record fixture"))
            {
                var index = await QueueWorktreeReferenceIndex.CreateAsync(
                    [Item(candidate with { Branch = "2333-lane" }, "candidate")], Ct);

                Assert.True(index.Complete);
                Assert.Contains("room:unrecorded-branch-room", index.ForBranch("2333-lane"));
                Assert.Empty(index.For(candidate.Path));
            }
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(sandbox);
        }
    }

    [Fact]
    public async Task Live_room_branch_disagreement_is_incomplete_and_indexes_both_claims()
    {
        var sandbox = Temp("branch-disagreement");
        var home = Path.Combine(sandbox, "home");
        var root = Path.Combine(sandbox, "worktrees");
        Directory.CreateDirectory(root);
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var candidate = await RepoAsync(root, "candidate");
            var switchedClone = await RepoAsync(root, "2333-lane");
            var room = Path.Combine(BatonPaths.Rooms, "switched-branch-room");
            Directory.CreateDirectory(room);
            await WorkerBindingConfigWriter.SaveToFileAsync(
                new Dictionary<string, WorkerBindingConfigEntry>
                {
                    ["implement"] = new(
                        "shell", new WorkerContract("implement", [], [], []),
                        PromptTemplate: "echo fixture", Timeout: TimeSpan.FromMinutes(1),
                        WorkingDirectory: switchedClone.Path),
                },
                BatonPaths.RoomBindingsFile(room), Ct);
            await File.WriteAllTextAsync(RoomDeliveryBranch.PathFor(room), "original-branch", Ct);

            using (ConcurrencyGuard.Acquire(room, "branch disagreement fixture"))
            {
                var index = await QueueWorktreeReferenceIndex.CreateAsync(
                    [Item(candidate with { Branch = "2333-lane" }, "candidate")], Ct);

                Assert.False(index.Complete);
                Assert.Contains("room:switched-branch-room", index.ForBranch("2333-lane"));
                Assert.Contains("room:switched-branch-room", index.ForBranch("original-branch"));
            }
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(sandbox);
        }
    }

    [Fact]
    public async Task Unreadable_rooms_root_makes_the_liveness_index_incomplete()
    {
        var sandbox = Temp("rooms-root-denied");
        var home = Path.Combine(sandbox, "home");
        var root = Path.Combine(sandbox, "worktrees");
        Directory.CreateDirectory(root);
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var candidate = await RepoAsync(root, "candidate");
            var probe = QueueWorktreeLivenessProbe.Default with
            {
                EnumerateDirectories = _ => throw new UnauthorizedAccessException("fixture denied"),
                BuildLockPath = Path.Combine(sandbox, "no-build-lock"),
            };

            var index = await QueueWorktreeReferenceIndex.CreateAsync(
                [Item(candidate, "candidate")], Ct, probe);

            Assert.False(index.Complete);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(sandbox);
        }
    }

    [Fact]
    public async Task Unreadable_build_lock_sidecar_makes_the_liveness_index_incomplete()
    {
        var sandbox = Temp("build-lock-denied");
        var home = Path.Combine(sandbox, "home");
        var root = Path.Combine(sandbox, "worktrees");
        var lockPath = Path.Combine(sandbox, "build.lock");
        Directory.CreateDirectory(root);
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var candidate = await RepoAsync(root, "candidate");
            var infoPath = lockPath + ".info";
            var probe = QueueWorktreeLivenessProbe.Default with
            {
                BuildLockPath = lockPath,
                ProbeBuildLock = _ => BuildLockProbeResult.Held,
                ReadAllText = path => string.Equals(path, infoPath, StringComparison.OrdinalIgnoreCase)
                    ? throw new UnauthorizedAccessException("fixture denied")
                    : File.ReadAllText(path),
            };

            var index = await QueueWorktreeReferenceIndex.CreateAsync(
                [Item(candidate, "candidate")], Ct, probe);

            Assert.False(index.Complete);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(sandbox);
        }
    }

    [Fact]
    public async Task Live_build_lock_owns_its_branch_even_from_another_clone_path()
    {
        var sandbox = Temp("build-lock-branch-owner");
        var home = Path.Combine(sandbox, "home");
        var root = Path.Combine(sandbox, "worktrees");
        var lockPath = Path.Combine(sandbox, "build.lock");
        Directory.CreateDirectory(root);
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var candidate = await RepoAsync(root, "candidate");
            var otherClone = await RepoAsync(root, "2333-lane");
            await File.WriteAllTextAsync(
                lockPath + ".info",
                JsonSerializer.Serialize(new { pid = 42, cwd = otherClone.Path }),
                Ct);
            var probe = QueueWorktreeLivenessProbe.Default with
            {
                BuildLockPath = lockPath,
                ProbeBuildLock = _ => BuildLockProbeResult.Held,
                IsLiveProcess = _ => true,
            };

            var index = await QueueWorktreeReferenceIndex.CreateAsync(
                [Item(candidate with { Branch = "2333-lane" }, "candidate")], Ct, probe);

            Assert.True(index.Complete);
            Assert.Contains("build-lock", index.ForBranch("2333-lane"));
            Assert.Empty(index.For(candidate.Path));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(sandbox);
        }
    }

    [Fact]
    public async Task Held_build_lock_without_a_sidecar_makes_the_liveness_index_incomplete()
    {
        var sandbox = Temp("build-lock-no-sidecar");
        var home = Path.Combine(sandbox, "home");
        var root = Path.Combine(sandbox, "worktrees");
        var lockPath = Path.Combine(sandbox, "build.lock");
        Directory.CreateDirectory(root);
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var candidate = await RepoAsync(root, "candidate");
            var probe = QueueWorktreeLivenessProbe.Default with
            {
                BuildLockPath = lockPath,
                ProbeBuildLock = _ => BuildLockProbeResult.Held,
            };

            var index = await QueueWorktreeReferenceIndex.CreateAsync(
                [Item(candidate, "candidate")], Ct, probe);

            Assert.False(index.Complete);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(sandbox);
        }
    }

    [Fact]
    public async Task Free_build_lock_ignores_a_stale_sidecar()
    {
        var sandbox = Temp("build-lock-stale-sidecar");
        var home = Path.Combine(sandbox, "home");
        var root = Path.Combine(sandbox, "worktrees");
        var lockPath = Path.Combine(sandbox, "build.lock");
        Directory.CreateDirectory(root);
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var candidate = await RepoAsync(root, "candidate");
            await File.WriteAllTextAsync(
                lockPath + ".info",
                JsonSerializer.Serialize(new { pid = 42, cwd = candidate.Path }),
                Ct);
            var probe = QueueWorktreeLivenessProbe.Default with
            {
                BuildLockPath = lockPath,
                ProbeBuildLock = _ => BuildLockProbeResult.Free,
                IsLiveProcess = _ => true,
            };

            var index = await QueueWorktreeReferenceIndex.CreateAsync(
                [Item(candidate, "candidate")], Ct, probe);

            Assert.True(index.Complete);
            Assert.Empty(index.For(candidate.Path));
            Assert.Empty(index.ForBranch(candidate.Branch));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(sandbox);
        }
    }

    [Fact]
    public async Task Shared_liveness_deadline_cancels_a_stuck_branch_probe_and_fails_closed()
    {
        var sandbox = Temp("branch-probe-deadline");
        var home = Path.Combine(sandbox, "home");
        var root = Path.Combine(sandbox, "worktrees");
        Directory.CreateDirectory(root);
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var candidate = await RepoAsync(root, "candidate");
            var room = Path.Combine(BatonPaths.Rooms, "stuck-branch-room");
            Directory.CreateDirectory(room);
            await WorkerBindingConfigWriter.SaveToFileAsync(
                new Dictionary<string, WorkerBindingConfigEntry>
                {
                    ["implement"] = new(
                        "shell", new WorkerContract("implement", [], [], []),
                        PromptTemplate: "echo fixture", Timeout: TimeSpan.FromMinutes(1),
                        WorkingDirectory: candidate.Path),
                },
                BatonPaths.RoomBindingsFile(room), Ct);
            var probe = QueueWorktreeLivenessProbe.Default with
            {
                BuildLockPath = Path.Combine(sandbox, "no-build-lock"),
                Timeout = TimeSpan.FromMilliseconds(50),
                ReadBranchAsync = async (_, cancellationToken) =>
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
                    return "unreachable";
                },
            };

            using (ConcurrencyGuard.Acquire(room, "stuck branch fixture"))
            {
                var started = Stopwatch.StartNew();
                var index = await QueueWorktreeReferenceIndex.CreateAsync(
                    [Item(candidate, "candidate")], Ct, probe);

                Assert.False(index.Complete);
                Assert.True(started.Elapsed < TimeSpan.FromSeconds(2), $"probe took {started.Elapsed}");
            }
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(sandbox);
        }
    }

    [Fact]
    public async Task Caller_cancellation_is_not_converted_into_an_incomplete_snapshot()
    {
        var sandbox = Temp("branch-probe-caller-cancelled");
        var home = Path.Combine(sandbox, "home");
        var root = Path.Combine(sandbox, "worktrees");
        Directory.CreateDirectory(root);
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var candidate = await RepoAsync(root, "candidate");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var probe = QueueWorktreeLivenessProbe.Default with
            {
                BuildLockPath = Path.Combine(sandbox, "no-build-lock"),
            };

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                QueueWorktreeReferenceIndex.CreateAsync(
                    [Item(candidate, "candidate")], cancelled.Token, probe));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(sandbox);
        }
    }

    private static QueueItem Item(
        RepoFixture repo,
        string tag,
        string? origin = WorkspaceOrigins.IssueProvisioned,
        QueueItemState state = QueueItemState.Queued,
        bool retired = true,
        bool halted = false) => new()
        {
            Tag = tag,
            Role = "implement",
            Workspace = repo.Path,
            SpecFile = Path.Combine(repo.Path, "brief.md"),
            WorkspaceOrigin = origin,
            Repository = repo.Repository,
            Branch = repo.Branch,
            State = state,
            Halted = halted,
            Retirement = retired ? Retired() : null,
        };

    private static QueueRetirement Retired() =>
        new(QueueRetirement.Operator, DateTimeOffset.Parse("2026-09-16T00:00:00Z"), "fixture retired");

    private static QueueWorktreeEntry Find(QueueWorktreeReport report, string directoryName) =>
        Assert.Single(report.Workspaces, entry => Path.GetFileName(entry.Path) == directoryName);

    private static void AssertReason(
        QueueWorktreeReport report,
        string directoryName,
        string reason,
        string classification)
    {
        var entry = Find(report, directoryName);
        Assert.Contains(reason, entry.ReasonCodes);
        Assert.Equal(classification, entry.Classification);
    }

    private static async Task<RepoFixture> RepoAsync(string parent, string name)
    {
        var path = Path.Combine(parent, name);
        Directory.CreateDirectory(path);
        await GitAsync(path, "init", "-q", "--initial-branch", name);
        await GitAsync(path, "remote", "add", "origin", "https://github.com/example/retained-worktrees.git");
        await File.WriteAllTextAsync(Path.Combine(path, "README.md"), name, Ct);
        await GitAsync(path, "add", "README.md");
        await CommitAsync(path, "base");
        return new RepoFixture(path, name, Repository);
    }

    private static async Task<RepoFixture> StaleLinkedWorktreeAsync(string sandbox, string root)
    {
        var main = await RepoAsync(sandbox, "stale-main");
        var oldPath = Path.Combine(root, "stale-old");
        var movedPath = Path.Combine(root, "stale-registration");
        await GitAsync(main.Path, "worktree", "add", "-q", "-b", "stale-registration", oldPath);
        Directory.Move(oldPath, movedPath);
        return new RepoFixture(movedPath, "stale-registration", Repository);
    }

    private static async Task CommitAsync(string path, string message) =>
        await GitAsync(path, "-c", "user.name=Baton Test", "-c", "user.email=test@example.invalid",
            "commit", "-q", "-m", message);

    private static async Task GitAsync(string path, params string[] arguments)
    {
        var (_, stderr, exitCode) = await RunGitAsync(path, arguments);
        Assert.True(exitCode == 0, $"git {string.Join(' ', arguments)} failed: {stderr}");
    }

    private static async Task<string> GitOutputAsync(string path, params string[] arguments)
    {
        var (stdout, stderr, exitCode) = await RunGitAsync(path, arguments);
        Assert.True(exitCode == 0, $"git {string.Join(' ', arguments)} failed: {stderr}");
        return stdout;
    }

    private static async Task<(string Stdout, string Stderr, int ExitCode)> RunGitAsync(
        string path,
        IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start git.");
        var (stdout, stderr) = await BoundedProcessWait.RunToExitAsync(process, TimeSpan.FromSeconds(30), Ct);
        return (stdout, stderr, process.ExitCode);
    }

    private static IReadOnlyDictionary<string, string> SnapshotRegularFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Contains(".git", StringComparer.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToDictionary(
                path => Path.GetRelativePath(root, path),
                path => Convert.ToBase64String(File.ReadAllBytes(path)),
                StringComparer.Ordinal);

    private static string Temp(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), $"queue-worktrees-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed record RepoFixture(string Path, string Branch, string Repository);
}
