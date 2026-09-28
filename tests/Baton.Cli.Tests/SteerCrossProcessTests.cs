using System.Diagnostics;
using Baton.Domain;
using Baton.Status;
using Baton.Store;
using Baton.Tests.Shared;

namespace Baton.Cli.Tests;

public sealed class SteerCrossProcessTests
{
    [Fact]
    public async Task Two_processes_reserve_one_message_and_conflicting_payload_fails()
    {
        var room = Path.Combine(Path.GetTempPath(), $"baton-steer-process-{Guid.NewGuid():N}");
        Directory.CreateDirectory(room);
        var gate = Path.Combine(room, "go");
        Process? first = null, second = null, conflict = null;
        try
        {
            first = Start(room, gate, "same");
            second = Start(room, gate, "same");
            await WaitReadyAsync(gate, 2);
            await File.WriteAllTextAsync(gate, "go", TestContext.Current.CancellationToken);
            await WaitExitAsync(first);
            await WaitExitAsync(second);
            Assert.Equal(0, first.ExitCode);
            Assert.Equal(0, second.ExitCode);
            var events = await new RoomEventLogReader(Path.Combine(room, BatonPaths.RoomLogFileName))
                .ReadAllRoomEventsAsync(TestContext.Current.CancellationToken);
            Assert.Single(events.OfType<RoomEvent.SteeringRequested>());

            conflict = Start(room, gate, "changed");
            await WaitExitAsync(conflict);
            Assert.Equal(4, conflict.ExitCode);
            var after = await new RoomEventLogReader(Path.Combine(room, BatonPaths.RoomLogFileName))
                .ReadAllRoomEventsAsync(TestContext.Current.CancellationToken);
            Assert.Single(after.OfType<RoomEvent.SteeringRequested>());
        }
        finally
        {
            foreach (var process in new[] { first, second, conflict })
            {
                if (process is { HasExited: false }) process.Kill();
                process?.Dispose();
            }
            DirectoryCleanup.DeleteRecursively(room);
        }
    }

    private static Process Start(string room, string gate, string payload)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(typeof(Baton.CrashTestHost.Scenarios).Assembly.Location);
        foreach (var arg in new[] { "steering-reserve", room, gate, "message-1", payload })
            start.ArgumentList.Add(arg);
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start steering reservation probe.");
    }

    private static async Task WaitReadyAsync(string gate, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (Directory.GetFiles(Path.GetDirectoryName(gate)!, "go.*.ready").Length < count
               && DateTime.UtcNow < deadline)
            await Task.Delay(20, TestContext.Current.CancellationToken); // wait-ok: bounded process rendezvous poll within 15s deadline
        Assert.Equal(count, Directory.GetFiles(Path.GetDirectoryName(gate)!, "go.*.ready").Length);
    }

    private static async Task WaitExitAsync(Process process)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        bound.CancelAfter(TimeSpan.FromSeconds(15));
        await process.WaitForExitAsync(bound.Token);
    }
}
