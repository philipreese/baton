using Baton.Cli.Daemon;
using Baton.Cli.Mcp;
using Baton.Status;
using Baton.Store;
using Baton.Tests.Shared;

namespace Baton.Cli.Tests.Daemon;

public sealed class DaemonRoomInventoryTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Real_consumers_invalidate_one_changed_room_and_keep_its_rerun_active_on_the_third_cadence()
    {
        var home = Path.Combine(Path.GetTempPath(), $"baton-inventory-consumers-{Guid.NewGuid():N}");
        Directory.CreateDirectory(home);
        using var environment = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { HomeOverride = home });
        try
        {
            var now = DateTimeOffset.UtcNow;
            var discoveryCalls = 0;
            var versionCalls = 0;
            var observationCalls = 0;
            TaskCompletionSource? refreshEntered = null;
            TaskCompletionSource? releaseRefresh = null;
            var gateNextRefresh = 0;
            var activeRooms = new HashSet<string>(StringComparer.Ordinal);
            IReadOnlySet<string>? changes = null;
            var rooms = Enumerable.Range(0, 1_001)
                .Select(index => new FleetStatusTool.DiscoveredRoom($"room-{index}", null))
                .ToList();
            var inventory = new DaemonRoomInventory(
                _ =>
                {
                    Interlocked.Increment(ref discoveryCalls);
                    return Task.FromResult<IReadOnlyList<FleetStatusTool.DiscoveredRoom>>(rooms);
                },
                async (room, _, _) =>
                {
                    Interlocked.Increment(ref observationCalls);
                    if (Interlocked.Exchange(ref gateNextRefresh, 0) == 1)
                    {
                        refreshEntered!.TrySetResult();
                        await releaseRefresh!.Task.WaitAsync(Ct);
                    }

                    return View(room, activeRooms.Contains(room) ? "Running" : "Succeeded");
                },
                () => Version(1),
                room =>
                {
                    Interlocked.Increment(ref versionCalls);
                    return activeRooms.Contains(room) ? ActiveVersion(2) : TerminalVersion(1);
                },
                _ => true,
                () => now,
                _ =>
                {
                    var result = changes;
                    changes = new HashSet<string>(BatonPaths.RecordKeyComparer);
                    return result;
                });
            var scheduler = new QueueSchedulerService(inventory);
            var projection = new FleetProjectionWriter(() => 8, roomInventory: inventory);
            var delivery = new DeliveryPoller(inventory);
            var usage = new VendorUsageHarvester([], roomInventory: inventory);
            var memory = new MemoryProjectionSweep();

            async Task RunCadenceAsync()
            {
                refreshEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                releaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Volatile.Write(ref gateNextRefresh, 1);

                // The scheduler is the only Current reader. Hold its first room observation so the
                // LastComplete consumers overlap this cadence instead of racing to become its refresh.
                var schedulerTick = scheduler.TickOnceAsync(Ct);
                await refreshEntered.Task.WaitAsync(Ct);
                var otherConsumers = Task.WhenAll(
                    projection.BuildProjectionJsonAsync(Ct, TextWriter.Null),
                    delivery.PollOnceAsync(Ct),
                    usage.TickOnceAsync(now, Ct),
                    memory.SweepOnceAsync(cancellationToken: Ct));
                releaseRefresh.TrySetResult();
                await Task.WhenAll(schedulerTick, otherConsumers);
            }

            await RunCadenceAsync();
            activeRooms.Add("room-500");
            changes = new HashSet<string>(BatonPaths.RecordKeyComparer)
            {
                BatonPaths.RecordKey("room-500"),
            };
            now += DaemonRoomInventory.ReuseWindow + TimeSpan.FromSeconds(1);
            await RunCadenceAsync();
            activeRooms.Add("room-501");
            changes = new HashSet<string>(BatonPaths.RecordKeyComparer)
            {
                BatonPaths.RecordKey("room-501"),
            };
            now += DaemonRoomInventory.ReuseWindow + TimeSpan.FromSeconds(1);
            await RunCadenceAsync();

            Assert.Equal(1, discoveryCalls);
            Assert.Equal(1_004, observationCalls);
            Assert.Equal(1_004, versionCalls);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(home);
        }
    }

    [Fact]
    public async Task External_sibling_rooms_share_one_real_watcher_and_detect_reruns_across_three_cadences()
    {
        var root = Path.Combine(Path.GetTempPath(), $"baton-inventory-watchers-{Guid.NewGuid():N}");
        var externalRoot = Path.Combine(root, "external");
        Directory.CreateDirectory(externalRoot);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var observationCalls = 0;
            var rooms = Enumerable.Range(0, 1_001)
                .Select(index =>
                {
                    var room = Path.Combine(externalRoot, $"room-{index}");
                    Directory.CreateDirectory(room);
                    File.WriteAllText(
                        Path.Combine(room, TerminalSentinelWriter.TerminalSentinelFileName),
                        "{}");
                    return new FleetStatusTool.DiscoveredRoom(room, null);
                })
                .ToList();
            using var tracker = new DaemonRoomInventory.RoomChangeTracker(Path.Combine(root, "local-rooms"));
            var inventory = new DaemonRoomInventory(
                _ => Task.FromResult<IReadOnlyList<FleetStatusTool.DiscoveredRoom>>(rooms),
                (room, _, _) =>
                {
                    observationCalls++;
                    var isTerminal = File.Exists(
                        Path.Combine(room, TerminalSentinelWriter.TerminalSentinelFileName));
                    return Task.FromResult<FleetRoomStatusView?>(
                        View(room, isTerminal ? "Succeeded" : "Running"));
                },
                () => Version(1),
                room => new DaemonRoomInventory.RoomVersion(
                    DaemonRoomInventory.FileVersion.ReadFile(
                        Path.Combine(room, TerminalSentinelWriter.TerminalSentinelFileName)),
                    default,
                    default,
                    default),
                Directory.Exists,
                () => now,
                tracker.SnapshotChanges,
                tracker.AcceptChanges,
                Ct);

            await inventory.ObserveAsync(
                DaemonRoomInventory.InventoryScope.All,
                DaemonRoomInventory.InventoryFreshness.Current,
                Ct);

            Assert.Equal(1, tracker.WatcherCount);
            Assert.Equal(0, tracker.PolledRoomCount);

            var firstChange = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnFirstChange() => firstChange.TrySetResult();
            tracker.Changed += OnFirstChange;
            FileCleanup.EnsureDeleted(Path.Combine(
                rooms[500].RoomDir,
                TerminalSentinelWriter.TerminalSentinelFileName));
            await firstChange.Task.WaitAsync(Ct);
            tracker.Changed -= OnFirstChange;

            now += DaemonRoomInventory.ReuseWindow + TimeSpan.FromSeconds(1);
            var secondCadence = await inventory.ObserveAsync(
                DaemonRoomInventory.InventoryScope.Active,
                DaemonRoomInventory.InventoryFreshness.Current,
                Ct);
            Assert.Equal(rooms[500].RoomDir, Assert.Single(secondCadence).Room.RoomDir);

            var secondChange = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnSecondChange() => secondChange.TrySetResult();
            tracker.Changed += OnSecondChange;
            FileCleanup.EnsureDeleted(Path.Combine(
                rooms[501].RoomDir,
                TerminalSentinelWriter.TerminalSentinelFileName));
            await secondChange.Task.WaitAsync(Ct);
            tracker.Changed -= OnSecondChange;

            now += DaemonRoomInventory.ReuseWindow + TimeSpan.FromSeconds(1);
            var thirdCadence = await inventory.ObserveAsync(
                DaemonRoomInventory.InventoryScope.Active,
                DaemonRoomInventory.InventoryFreshness.Current,
                Ct);

            Assert.Equal(
                [rooms[500].RoomDir, rooms[501].RoomDir],
                thirdCadence.Select(room => room.Room.RoomDir));
            Assert.Equal(1_004, observationCalls);
            Assert.InRange(tracker.WatcherCount, 1, DaemonRoomInventory.RoomChangeTracker.MaximumWatcherCount);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public async Task Concurrent_consumers_share_one_cold_observation_pass()
    {
        var now = DateTimeOffset.UtcNow;
        var discoverCalls = 0;
        var observeCalls = 0;
        var observationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseObservation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rooms = Enumerable.Range(0, 1_000)
            .Select(index => new FleetStatusTool.DiscoveredRoom($"room-{index}", null))
            .ToList();
        var inventory = new DaemonRoomInventory(
            _ =>
            {
                Interlocked.Increment(ref discoverCalls);
                return Task.FromResult<IReadOnlyList<FleetStatusTool.DiscoveredRoom>>(rooms);
            },
            async (room, _, _) =>
            {
                Interlocked.Increment(ref observeCalls);
                observationEntered.TrySetResult();
                await releaseObservation.Task;
                return View(room, "Succeeded");
            },
            () => Version(1),
            _ => TerminalVersion(1),
            _ => true,
            () => now);

        var requests = new[]
        {
            inventory.ObserveAsync(
                DaemonRoomInventory.InventoryScope.All,
                DaemonRoomInventory.InventoryFreshness.Current,
                Ct),
            inventory.ObserveAsync(
                DaemonRoomInventory.InventoryScope.Active,
                DaemonRoomInventory.InventoryFreshness.Current,
                Ct),
            inventory.ObserveAsync(
                DaemonRoomInventory.InventoryScope.All,
                DaemonRoomInventory.InventoryFreshness.LastComplete,
                Ct),
            inventory.ObserveAsync(
                DaemonRoomInventory.InventoryScope.Active,
                DaemonRoomInventory.InventoryFreshness.LastComplete,
                Ct),
        };

        await observationEntered.Task.WaitAsync(Ct);
        Assert.All(requests, request => Assert.False(request.IsCompleted));
        releaseObservation.TrySetResult();
        var results = await Task.WhenAll(requests);

        Assert.Equal(1, discoverCalls);
        Assert.Equal(1_000, observeCalls);
        Assert.Equal(1_000, results[0].Count);
        Assert.Empty(results[1]);
        Assert.Equal(1_000, results[2].Count);
        Assert.Empty(results[3]);
    }

    [Fact]
    public async Task Unchanged_terminal_population_is_observed_once_across_consumers()
    {
        var now = DateTimeOffset.UtcNow;
        var discoverCalls = 0;
        var observeCalls = 0;
        var rooms = Enumerable.Range(0, 1_000)
            .Select(index => new FleetStatusTool.DiscoveredRoom($"room-{index}", null))
            .ToList();
        var inventory = Inventory(
            () => now,
            () => Version(1),
            _ => TerminalVersion(1),
            _ =>
            {
                observeCalls++;
                return View("terminal", "Succeeded");
            },
            () =>
            {
                discoverCalls++;
                return rooms;
            });

        var first = await inventory.ObserveAsync(
            DaemonRoomInventory.InventoryScope.All,
            DaemonRoomInventory.InventoryFreshness.Current,
            Ct);
        now += DaemonRoomInventory.ReuseWindow + TimeSpan.FromSeconds(1);
        var second = await inventory.ObserveAsync(
            DaemonRoomInventory.InventoryScope.All,
            DaemonRoomInventory.InventoryFreshness.Current,
            Ct);

        Assert.Equal(1_000, first.Count);
        Assert.Equal(1_000, second.Count);
        Assert.Equal(1, discoverCalls);
        Assert.Equal(1_000, observeCalls);
    }

    [Fact]
    public async Task Changed_new_and_removed_rooms_refresh_only_the_affected_observations()
    {
        var now = DateTimeOffset.UtcNow;
        var discoveryRevision = 1;
        var rooms = new List<FleetStatusTool.DiscoveredRoom>
        {
            new("room-a", null),
            new("room-b", null),
        };
        var roomRevisions = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["room-a"] = 1,
            ["room-b"] = 1,
            ["room-c"] = 1,
        };
        var calls = new Dictionary<string, int>(StringComparer.Ordinal);
        var inventory = Inventory(
            () => now,
            () => Version(discoveryRevision),
            room => TerminalVersion(roomRevisions[room]),
            room =>
            {
                calls[room] = calls.GetValueOrDefault(room) + 1;
                return View(room, "Succeeded");
            },
            () => rooms);

        await inventory.ObserveAsync(
            DaemonRoomInventory.InventoryScope.All,
            DaemonRoomInventory.InventoryFreshness.Current,
            Ct);

        now += DaemonRoomInventory.ReuseWindow + TimeSpan.FromSeconds(1);
        roomRevisions["room-a"] = 2;
        var changed = await inventory.ObserveAsync(
            DaemonRoomInventory.InventoryScope.All,
            DaemonRoomInventory.InventoryFreshness.Current,
            Ct);

        Assert.Equal(2, calls["room-a"]);
        Assert.Equal(1, calls["room-b"]);
        Assert.Equal(2, changed.Count);

        now += DaemonRoomInventory.ReuseWindow + TimeSpan.FromSeconds(1);
        discoveryRevision++;
        rooms = [new("room-a", null), new("room-c", null)];
        var reshaped = await inventory.ObserveAsync(
            DaemonRoomInventory.InventoryScope.All,
            DaemonRoomInventory.InventoryFreshness.Current,
            Ct);

        Assert.Equal(["room-a", "room-c"], reshaped.Select(room => room.Room.RoomDir));
        Assert.Equal(2, calls["room-a"]);
        Assert.Equal(1, calls["room-b"]);
        Assert.Equal(1, calls["room-c"]);

        now += DaemonRoomInventory.ReuseWindow + TimeSpan.FromSeconds(1);
        discoveryRevision++;
        rooms = [new("room-a", null), new("room-b", null), new("room-c", null)];
        await inventory.ObserveAsync(
            DaemonRoomInventory.InventoryScope.All,
            DaemonRoomInventory.InventoryFreshness.Current,
            Ct);

        Assert.Equal(2, calls["room-b"]);
    }

    [Fact]
    public async Task Changed_active_room_is_visible_on_the_next_refresh()
    {
        var now = DateTimeOffset.UtcNow;
        var revision = 1;
        var inventory = Inventory(
            () => now,
            () => Version(1),
            _ => ActiveVersion(revision),
            _ => View("room-a", revision == 1 ? "Running" : "Stalled"),
            () => [new FleetStatusTool.DiscoveredRoom("room-a", null)]);

        var initial = await inventory.ObserveAsync(
            DaemonRoomInventory.InventoryScope.Active,
            DaemonRoomInventory.InventoryFreshness.Current,
            Ct);
        Assert.Equal("Running", Assert.Single(initial).View.State);

        revision = 2;
        now += DaemonRoomInventory.ReuseWindow + TimeSpan.FromSeconds(1);
        var changed = await inventory.ObserveAsync(
            DaemonRoomInventory.InventoryScope.Active,
            DaemonRoomInventory.InventoryFreshness.Current,
            Ct);

        Assert.Equal("Stalled", Assert.Single(changed).View.State);
    }

    [Fact]
    public async Task Current_reader_detects_a_new_active_room_inside_the_last_complete_reuse_window()
    {
        var now = DateTimeOffset.UtcNow;
        var discoveryRevision = 1;
        var rooms = new List<FleetStatusTool.DiscoveredRoom>();
        var inventory = Inventory(
            () => now,
            () => Version(discoveryRevision),
            _ => ActiveVersion(1),
            room => View(room, "Running"),
            () => rooms);

        Assert.Empty(await inventory.ObserveAsync(
            DaemonRoomInventory.InventoryScope.Active,
            DaemonRoomInventory.InventoryFreshness.Current,
            Ct));

        discoveryRevision++;
        rooms = [new FleetStatusTool.DiscoveredRoom("room-a", null)];
        var current = await inventory.ObserveAsync(
            DaemonRoomInventory.InventoryScope.Active,
            DaemonRoomInventory.InventoryFreshness.Current,
            Ct);

        Assert.Equal("Running", Assert.Single(current).View.State);
    }

    [Fact]
    public async Task Terminal_room_rerun_is_visible_to_the_active_scope()
    {
        var now = DateTimeOffset.UtcNow;
        var terminal = true;
        var observeCalls = 0;
        var inventory = Inventory(
            () => now,
            () => Version(1),
            _ => terminal ? TerminalVersion(1) : ActiveVersion(2),
            _ =>
            {
                observeCalls++;
                return View("room-a", terminal ? "Succeeded" : "Running");
            },
            () => [new FleetStatusTool.DiscoveredRoom("room-a", null)]);

        var initial = await inventory.ObserveAsync(
            DaemonRoomInventory.InventoryScope.All,
            DaemonRoomInventory.InventoryFreshness.Current,
            Ct);
        Assert.Equal("Succeeded", Assert.Single(initial).View.State);

        terminal = false;
        now += DaemonRoomInventory.ReuseWindow + TimeSpan.FromSeconds(1);
        var rerun = await inventory.ObserveAsync(
            DaemonRoomInventory.InventoryScope.Active,
            DaemonRoomInventory.InventoryFreshness.Current,
            Ct);

        Assert.Equal("Running", Assert.Single(rerun).View.State);
        Assert.Equal(2, observeCalls);
    }

    [Fact]
    public async Task Empty_change_journal_revalidates_a_terminal_rerun_before_active_filtering()
    {
        var now = DateTimeOffset.UtcNow;
        var rerun = false;
        IReadOnlySet<string>? changes = null;
        var inventory = new DaemonRoomInventory(
            _ => Task.FromResult<IReadOnlyList<FleetStatusTool.DiscoveredRoom>>(
                [new FleetStatusTool.DiscoveredRoom("room-a", null)]),
            (room, _, _) => Task.FromResult<FleetRoomStatusView?>(
                View(room, rerun ? "Running" : "Succeeded")),
            () => Version(1),
            _ => rerun ? ActiveVersion(2) : TerminalVersion(1),
            _ => true,
            () => now,
            _ =>
            {
                var result = changes;
                changes = new HashSet<string>(BatonPaths.RecordKeyComparer);
                return result;
            });

        await inventory.ObserveAsync(
            DaemonRoomInventory.InventoryScope.All,
            DaemonRoomInventory.InventoryFreshness.Current,
            Ct);

        rerun = true;
        now += DaemonRoomInventory.ReuseWindow + TimeSpan.FromSeconds(1);
        var active = await inventory.ObserveAsync(
            DaemonRoomInventory.InventoryScope.Active,
            DaemonRoomInventory.InventoryFreshness.Current,
            Ct);

        Assert.Equal("room-a", Assert.Single(active).Room.RoomDir);
        Assert.Equal("Running", active[0].View.State);
    }

    [Fact]
    public async Task Change_journal_revalidates_only_the_rerun_terminal_room()
    {
        var now = DateTimeOffset.UtcNow;
        var rerun = false;
        var versionCalls = 0;
        var observationCalls = 0;
        IReadOnlySet<string>? changes = null;
        var rooms = Enumerable.Range(0, 1_001)
            .Select(index => new FleetStatusTool.DiscoveredRoom($"room-{index}", null))
            .ToList();
        var inventory = new DaemonRoomInventory(
            _ => Task.FromResult<IReadOnlyList<FleetStatusTool.DiscoveredRoom>>(rooms),
            (room, _, _) =>
            {
                observationCalls++;
                return Task.FromResult<FleetRoomStatusView?>(
                    View(room, rerun && room == "room-500" ? "Running" : "Succeeded"));
            },
            () => Version(1),
            room =>
            {
                versionCalls++;
                return rerun && room == "room-500" ? ActiveVersion(2) : TerminalVersion(1);
            },
            _ => true,
            () => now,
            _ =>
            {
                var result = changes;
                changes = new HashSet<string>(BatonPaths.RecordKeyComparer);
                return result;
            });

        await inventory.ObserveAsync(
            DaemonRoomInventory.InventoryScope.All,
            DaemonRoomInventory.InventoryFreshness.Current,
            Ct);

        rerun = true;
        changes = new HashSet<string>(BatonPaths.RecordKeyComparer)
        {
            BatonPaths.RecordKey("room-500"),
        };
        now += DaemonRoomInventory.ReuseWindow + TimeSpan.FromSeconds(1);
        var active = await inventory.ObserveAsync(
            DaemonRoomInventory.InventoryScope.Active,
            DaemonRoomInventory.InventoryFreshness.Current,
            Ct);

        Assert.Equal("room-500", Assert.Single(active).Room.RoomDir);
        Assert.Equal(1_002, versionCalls);
        Assert.Equal(1_002, observationCalls);
    }

    [Fact]
    public async Task Last_complete_reader_does_not_wait_behind_a_slow_refresh()
    {
        var now = DateTimeOffset.UtcNow;
        var revision = 1;
        var refreshEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inventory = new DaemonRoomInventory(
            _ => Task.FromResult<IReadOnlyList<FleetStatusTool.DiscoveredRoom>>(
                [new FleetStatusTool.DiscoveredRoom("room-a", null)]),
            async (room, _, _) =>
            {
                if (revision == 2)
                {
                    refreshEntered.TrySetResult();
                    await releaseRefresh.Task;
                }

                return View(room, revision == 1 ? "Succeeded" : "Failed");
            },
            () => Version(1),
            _ => TerminalVersion(revision),
            _ => true,
            () => now);

        await inventory.ObserveAsync(
            DaemonRoomInventory.InventoryScope.All,
            DaemonRoomInventory.InventoryFreshness.Current,
            Ct);

        revision = 2;
        now += DaemonRoomInventory.ReuseWindow + TimeSpan.FromSeconds(1);
        var current = inventory.ObserveAsync(
            DaemonRoomInventory.InventoryScope.All,
            DaemonRoomInventory.InventoryFreshness.Current,
            Ct);
        await refreshEntered.Task.WaitAsync(Ct);

        var stale = inventory.ObserveAsync(
            DaemonRoomInventory.InventoryScope.All,
            DaemonRoomInventory.InventoryFreshness.LastComplete,
            Ct);
        Assert.True(stale.IsCompletedSuccessfully);
        Assert.Equal("Succeeded", Assert.Single(await stale).View.State);

        releaseRefresh.TrySetResult();
        Assert.Equal("Failed", Assert.Single(await current).View.State);
    }

    [Fact]
    public async Task Slow_consumer_business_does_not_block_another_last_complete_reader()
    {
        var inventory = Inventory(
            () => DateTimeOffset.UtcNow,
            () => Version(1),
            _ => TerminalVersion(1),
            room => View(room, "Succeeded"),
            () => [new FleetStatusTool.DiscoveredRoom("room-a", null)]);
        var businessEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBusiness = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task SlowConsumerAsync()
        {
            await inventory.ObserveAsync(
                DaemonRoomInventory.InventoryScope.All,
                DaemonRoomInventory.InventoryFreshness.LastComplete,
                Ct);
            businessEntered.TrySetResult();
            await releaseBusiness.Task;
        }

        var slowConsumer = SlowConsumerAsync();
        await businessEntered.Task.WaitAsync(Ct);

        var independentReader = inventory.ObserveAsync(
            DaemonRoomInventory.InventoryScope.All,
            DaemonRoomInventory.InventoryFreshness.LastComplete,
            Ct);
        Assert.True(independentReader.IsCompletedSuccessfully);
        Assert.Equal("Succeeded", Assert.Single(await independentReader).View.State);

        releaseBusiness.TrySetResult();
        await slowConsumer;
    }

    [Fact]
    public async Task Current_reader_fails_closed_when_refresh_fails()
    {
        var inventory = new DaemonRoomInventory(
            _ => Task.FromException<IReadOnlyList<FleetStatusTool.DiscoveredRoom>>(new IOException("inventory unavailable")),
            (_, _, _) => Task.FromResult<FleetRoomStatusView?>(null),
            () => Version(1),
            _ => ActiveVersion(1),
            _ => true,
            () => DateTimeOffset.UtcNow);

        var error = await Assert.ThrowsAsync<IOException>(
            () => inventory.ObserveAsync(
                DaemonRoomInventory.InventoryScope.Active,
                DaemonRoomInventory.InventoryFreshness.Current,
                Ct));

        Assert.Contains("inventory unavailable", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Daemon_lifetime_cancels_a_shared_refresh()
    {
        using var lifetime = new CancellationTokenSource();
        var observationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observationCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inventory = new DaemonRoomInventory(
            _ => Task.FromResult<IReadOnlyList<FleetStatusTool.DiscoveredRoom>>(
                [new FleetStatusTool.DiscoveredRoom("room-a", null)]),
            async (_, _, cancellationToken) =>
            {
                observationEntered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return null;
                }
                catch (OperationCanceledException)
                {
                    observationCanceled.TrySetResult();
                    throw;
                }
            },
            () => Version(1),
            _ => ActiveVersion(1),
            _ => true,
            () => DateTimeOffset.UtcNow,
            lifetime.Token);

        var refresh = inventory.ObserveAsync(
            DaemonRoomInventory.InventoryScope.Active,
            DaemonRoomInventory.InventoryFreshness.Current,
            Ct);
        await observationEntered.Task.WaitAsync(Ct);

        lifetime.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        await observationCanceled.Task.WaitAsync(Ct);
    }

    private static DaemonRoomInventory Inventory(
        Func<DateTimeOffset> now,
        Func<DaemonRoomInventory.DiscoveryVersion> discoveryVersion,
        Func<string, DaemonRoomInventory.RoomVersion> roomVersion,
        Func<string, FleetRoomStatusView> observe,
        Func<IReadOnlyList<FleetStatusTool.DiscoveredRoom>> discover) =>
        new(
            _ => Task.FromResult(discover()),
            (room, _, _) => Task.FromResult<FleetRoomStatusView?>(observe(room)),
            discoveryVersion,
            roomVersion,
            _ => true,
            now);

    private static FleetRoomStatusView View(string room, string state) => new(room, room, State: state);

    private static DaemonRoomInventory.DiscoveryVersion Version(int revision) => new(
        new DaemonRoomInventory.FileVersion(true, 0, revision),
        default);

    private static DaemonRoomInventory.RoomVersion TerminalVersion(int revision) => new(
        new DaemonRoomInventory.FileVersion(true, revision, revision),
        default,
        new DaemonRoomInventory.FileVersion(true, revision, revision),
        default);

    private static DaemonRoomInventory.RoomVersion ActiveVersion(int revision) => new(
        default,
        default,
        new DaemonRoomInventory.FileVersion(true, revision, revision),
        default);
}
