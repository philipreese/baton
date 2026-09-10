using Baton.Status;
using Baton.Vendors;

namespace Baton.Cli.Tests;

/// <summary>
/// <c>baton room delete</c> (#1659, ruling in full at spec/baton.md §8) — see
/// <see cref="RoomDeleteCommand"/>'s own remarks for exactly what it removes.
/// </summary>
[Collection(SerializedEnvironmentCollection.Name)]
public sealed class RoomDeleteCommandTests
{
    private static string CreateTempHome()
    {
        var tempHome = Path.Combine(Path.GetTempPath(), "baton_room_delete_test_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempHome);
        return tempHome;
    }

    private static async Task WriteTerminalSentinelAsync(string roomDirectoryPath, string state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(roomDirectoryPath);
        await TerminalSentinelWriter.WriteAsync(
            roomDirectoryPath, new WorkflowStatusView(state, [], [], null), cancellationToken);
    }

    [Fact]
    public async Task ExecuteAsync_NonTerminalRoom_RefusesWithoutForce_AndLeavesTheDirectoryInPlace()
    {
        var tempHome = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = tempHome });
        try
        {
            var roomDir = Path.Combine(tempHome, "room-not-terminal");
            Directory.CreateDirectory(roomDir); // no terminal.json -> not terminal

            var options = new RoomDeleteOptions(roomDir, Force: false);

            var ex = await Assert.ThrowsAsync<CliArgumentException>(
                () => RoomDeleteCommand.ExecuteAsync(options, TextWriter.Null, TestContext.Current.CancellationToken));

            Assert.Contains("has not reached a terminal state", ex.Message, StringComparison.Ordinal);
            Assert.True(Directory.Exists(roomDir));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempHome);
        }
    }

    [Fact]
    public async Task ExecuteAsync_NonTerminalRoom_WithForce_DeletesAnyway()
    {
        var tempHome = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = tempHome });
        try
        {
            var roomDir = Path.Combine(tempHome, "room-not-terminal-forced");
            Directory.CreateDirectory(roomDir);

            var options = new RoomDeleteOptions(roomDir, Force: true);
            var result = await RoomDeleteCommand.ExecuteAsync(options, TextWriter.Null, TestContext.Current.CancellationToken);

            Assert.True(result.DirectoryExisted);
            Assert.False(Directory.Exists(roomDir));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempHome);
        }
    }

    [Fact]
    public async Task ExecuteAsync_TerminalRoom_RemovesDirectory_AndRegistryLine()
    {
        var tempHome = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = tempHome });
        try
        {
            var roomDir = Path.Combine(tempHome, "room-terminal");
            await WriteTerminalSentinelAsync(roomDir, WorkflowOutcome.Succeeded, TestContext.Current.CancellationToken);
            await RoomRegistryStore.AppendAsync(
                roomDir, tempHome, BatonPaths.RoomRegistryFile, explicitRegister: true,
                cancellationToken: TestContext.Current.CancellationToken);

            var options = new RoomDeleteOptions(roomDir, Force: false);
            var result = await RoomDeleteCommand.ExecuteAsync(options, TextWriter.Null, TestContext.Current.CancellationToken);

            Assert.True(result.DirectoryExisted);
            Assert.Equal(1, result.RegistryLinesRemoved);
            Assert.False(Directory.Exists(roomDir));
            var remainingRegistryEntries = await RoomRegistryStore.ReadDistinctByRoomAsync(
                BatonPaths.RoomRegistryFile, TestContext.Current.CancellationToken);
            Assert.Empty(remainingRegistryEntries);

        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempHome);
        }
    }

    [Fact]
    public async Task ExecuteAsync_ConductorRoom_RefusesWithoutForce()
    {
        // F3 (2026-09-02 review): the conductor room must be refused outright, not merely because it
        // never carries a terminal.json.
        var tempHome = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = tempHome });
        try
        {
            var conductorRoom = Path.Combine(tempHome, "conductor");
            Directory.CreateDirectory(conductorRoom);

            var options = new RoomDeleteOptions(conductorRoom, Force: false);

            var ex = await Assert.ThrowsAsync<CliArgumentException>(
                () => RoomDeleteCommand.ExecuteAsync(options, TextWriter.Null, TestContext.Current.CancellationToken));

            Assert.Contains("conductor room", ex.Message, StringComparison.Ordinal);
            Assert.True(Directory.Exists(conductorRoom));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempHome);
        }
    }

    [Fact]
    public async Task ExecuteAsync_ConductorRoom_RefusesEvenWithForce()
    {
        // F3 (2026-09-02 review): --force must not be a way past this refusal -- unlike the
        // terminal-state refusal above, --force never overrides it.
        var tempHome = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = tempHome });
        try
        {
            var conductorRoom = Path.Combine(tempHome, "conductor");
            Directory.CreateDirectory(conductorRoom);

            var options = new RoomDeleteOptions(conductorRoom, Force: true);

            var ex = await Assert.ThrowsAsync<CliArgumentException>(
                () => RoomDeleteCommand.ExecuteAsync(options, TextWriter.Null, TestContext.Current.CancellationToken));

            Assert.Contains("conductor room", ex.Message, StringComparison.Ordinal);
            Assert.True(Directory.Exists(conductorRoom));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempHome);
        }
    }

    [Fact]
    public async Task ExecuteAsync_RoomDirectoryAlreadyGone_StillCleansUpTheRegistryLine()
    {
        var tempHome = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = tempHome });
        try
        {
            var roomDir = Path.Combine(tempHome, "room-already-gone");
            await RoomRegistryStore.AppendAsync(
                roomDir, tempHome, BatonPaths.RoomRegistryFile, explicitRegister: true,
                cancellationToken: TestContext.Current.CancellationToken);
            // Directory never created -> RefuseUnlessTerminalOrForced's absent-directory carve-out
            // (see its own remarks) applies, so this must not refuse.

            var options = new RoomDeleteOptions(roomDir, Force: false);
            var result = await RoomDeleteCommand.ExecuteAsync(options, TextWriter.Null, TestContext.Current.CancellationToken);

            Assert.False(result.DirectoryExisted);
            Assert.Equal(1, result.RegistryLinesRemoved);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempHome);
        }
    }
}
