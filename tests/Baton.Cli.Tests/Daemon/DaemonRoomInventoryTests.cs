using Baton.Cli.Daemon;
using Baton.Cli.Mcp;

namespace Baton.Cli.Tests.Daemon;

public sealed class DaemonRoomInventoryTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

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
