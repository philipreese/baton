using Baton.Cli.Tests.TestSupport;
using Baton.Cli.Daemon;
using Baton.Status;

namespace Baton.Cli.Tests;

/// <summary>
/// <c>baton watch</c>'s end-to-end command surface (#1488): registration (including the "no lost
/// wake-up" immediate fire on an already-terminal room), <c>--list</c>, and <c>--clear-fired</c>.
/// </summary>
public sealed class WatchCommandTests
{
    private static string CreateRoomDirectory(string homePath, string name)
    {
        var roomDir = Path.Combine(homePath, "rooms", name);
        Directory.CreateDirectory(roomDir);
        return roomDir;
    }

    [Fact]
    public void Watch_liveness_probe_finds_the_daemon_mutex_for_its_storage_root()
    {
        using var home = new IsolatedBatonHome();
        using var heldDaemonMutex = new Mutex(true, DaemonHost.MutexName(BatonPaths.Root), out var createdNew);
        Assert.True(createdNew);

        Assert.True(WatchCommand.IsDaemonLikelyRunning());
    }

    [Fact]
    public async Task ExecuteAsync_Register_NonTerminalRoom_RegistersWithoutFiring()
    {
        using var home = new IsolatedBatonHome();
        var roomDir = CreateRoomDirectory(home.Path, "room-running");
        var output = new StringWriter();
        var options = new WatchOptions(WatchMode.Register, roomDir, "https://example.invalid/hook");

        var exitCode = await WatchCommand.ExecuteAsync(options, output, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("Registered watch", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("fired immediately", output.ToString(), StringComparison.Ordinal);

        var watches = await WatchStore.ListAsync(BatonPaths.Watches, TestContext.Current.CancellationToken);
        var watch = Assert.Single(watches);
        Assert.Equal(roomDir, watch.RoomDirectoryPath);
        Assert.Null(watch.FiredAt);
    }

    [Fact]
    public async Task ExecuteAsync_Register_AlreadyTerminalRoom_FiresImmediatelyAtRegistration()
    {
        using var home = new IsolatedBatonHome();
        var roomDir = CreateRoomDirectory(home.Path, "room-1");
        await TerminalSentinelWriter.WriteAsync(
            roomDir, new WorkflowStatusView(WorkflowOutcome.Succeeded, [], [], null), TestContext.Current.CancellationToken);
        var output = new StringWriter();
        var options = new WatchOptions(WatchMode.Register, roomDir, "https://example.invalid/hook");
        var notifier = new RecordingWatchNotifier();

        var exitCode = await WatchCommand.ExecuteAsync(options, output, TestContext.Current.CancellationToken, notifier);

        Assert.Equal(0, exitCode);
        Assert.Contains("fired immediately", output.ToString(), StringComparison.Ordinal);

        // The claim (M3 fix): assert the notifier was actually invoked with this watch's target, not
        // just that FiredAt got set -- FiredAt alone is the claim, not the notification, and would pass
        // identically even if NotifyAsync were deleted.
        var (target, payload) = Assert.Single(notifier.Calls);
        Assert.Equal(options.NotifyTarget, target);
        Assert.Equal(roomDir, payload.Room);

        var watches = await WatchStore.ListAsync(BatonPaths.Watches, TestContext.Current.CancellationToken);
        var watch = Assert.Single(watches);
        Assert.NotNull(watch.FiredAt);
    }

    [Fact]
    public async Task ExecuteAsync_Register_RoomDirectoryDoesNotExist_Throws()
    {
        using var home = new IsolatedBatonHome();
        var roomDir = Path.Combine(home.Path, "rooms", "never-created");
        var options = new WatchOptions(WatchMode.Register, roomDir, "https://example.invalid/hook");

        var ex = await Assert.ThrowsAsync<CliArgumentException>(
            () => WatchCommand.ExecuteAsync(options, TextWriter.Null, TestContext.Current.CancellationToken));

        Assert.Contains("does not exist", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_List_NoWatches_PrintsNone()
    {
        using var home = new IsolatedBatonHome();
        var output = new StringWriter();

        var exitCode = await WatchCommand.ExecuteAsync(
            new WatchOptions(WatchMode.List, null, null), output, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("No watches registered.", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_List_PrintsPendingAndFiredWatchesDistinctly()
    {
        using var home = new IsolatedBatonHome();
        var pendingRoom = CreateRoomDirectory(home.Path, "room-pending");
        var firedRoom = CreateRoomDirectory(home.Path, "room-fired");
        await WatchStore.WriteAsync(
            new WatchRecord("w-pending", pendingRoom, "cmd1", DateTime.UtcNow), BatonPaths.Watches, TestContext.Current.CancellationToken);
        await WatchStore.WriteAsync(
            new WatchRecord("w-fired", firedRoom, "cmd2", DateTime.UtcNow), BatonPaths.Watches, TestContext.Current.CancellationToken);
        await WatchStore.TryClaimAsync(BatonPaths.Watches, "w-fired", DateTime.UtcNow, TestContext.Current.CancellationToken);
        var output = new StringWriter();

        await WatchCommand.ExecuteAsync(
            new WatchOptions(WatchMode.List, null, null), output, TestContext.Current.CancellationToken);

        var text = output.ToString();
        Assert.Contains("w-pending", text, StringComparison.Ordinal);
        Assert.Contains("pending", text, StringComparison.Ordinal);
        Assert.Contains("w-fired", text, StringComparison.Ordinal);
        Assert.Contains("fired", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_ClearFired_RemovesOnlyFiredWatchesAndReportsTheCount()
    {
        using var home = new IsolatedBatonHome();
        var pendingRoom = CreateRoomDirectory(home.Path, "room-pending");
        var firedRoom = CreateRoomDirectory(home.Path, "room-fired");
        await WatchStore.WriteAsync(
            new WatchRecord("w-pending", pendingRoom, "cmd1", DateTime.UtcNow), BatonPaths.Watches, TestContext.Current.CancellationToken);
        await WatchStore.WriteAsync(
            new WatchRecord("w-fired", firedRoom, "cmd2", DateTime.UtcNow), BatonPaths.Watches, TestContext.Current.CancellationToken);
        await WatchStore.TryClaimAsync(BatonPaths.Watches, "w-fired", DateTime.UtcNow, TestContext.Current.CancellationToken);
        var output = new StringWriter();

        var exitCode = await WatchCommand.ExecuteAsync(
            new WatchOptions(WatchMode.ClearFired, null, null), output, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("Removed 1 fired watch(es).", output.ToString(), StringComparison.Ordinal);
        var remaining = await WatchStore.ListAsync(BatonPaths.Watches, TestContext.Current.CancellationToken);
        var single = Assert.Single(remaining);
        Assert.Equal("w-pending", single.WatchId);
    }
}
