using System.Diagnostics;
using Baton.Cli.Daemon;
using Baton.Cli.Tests.TestSupport;
using Baton.Domain;
using Baton.Outcomes;
using Baton.Queue;
using Baton.Status;
using Baton.Store;
using Baton.Templates;
using Xunit;

namespace Baton.Cli.Tests.Daemon;

/// <summary>
/// #2082 finding 2's re-adoption arm: on daemon start, every <see cref="QueueItemState.Launched"/>
/// row is probed through the room's own recorded engine identity and the ONE liveness probe
/// (<see cref="EngineLivenessProbe"/>), and an alive lane gets a supervisor again. The three answers
/// the probe can give are each pinned, plus the filter that keeps a non-launched row out of the walk.
/// </summary>
/// <remarks>
/// The alive arm uses a real sleeper as the "engine": its pid and start time are written into the
/// fixture room's journal exactly where <c>MutationInterface</c> stamps them, and killing it is what
/// proves supervision resumed — the fault sentinel it leaves is the same one a lane launched by this
/// daemon would get. The dead arm uses <see cref="ProcessIdentityFixture.DeadProcessIdentity"/> so the
/// probe answers <c>Dead</c> off an OS-confirmed exit rather than a made-up pid.
/// </remarks>
public sealed class QueueLaneAdoptionTests : IDisposable
{
    private static readonly StepId TheStep = new("implement");
    private static readonly TimeSpan SettleBound = TimeSpan.FromSeconds(30);

    private readonly IsolatedBatonHome _batonHome = new();

    public void Dispose() => _batonHome.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_alive_engine_is_re_adopted_and_supervised_until_it_exits()
    {
        var psi = new ProcessStartInfo("ping.exe", "-n 60 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false };
        using var engine = Process.Start(psi)!;
        try
        {
            var room = RoomPath("alive");
            await WriteOpenRoomAsync(room, engine.Id, new DateTimeOffset(engine.StartTime).ToUniversalTime());
            await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with { Items = [Launched("alive", room)] }, Ct);

            var adoptions = await QueueLauncher.AdoptLaunchedLanesAsync(Ct);

            var adoption = Assert.Single(adoptions);
            Assert.Equal(EngineLivenessStatus.Alive, adoption.Status);
            Assert.Equal(engine.Id, adoption.EnginePid);

            // Adoption changes no row and writes nothing: the lane is still running.
            Assert.Equal(QueueItemState.Launched, Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items).State);
            Assert.Null(await TerminalSentinelWriter.TryReadAsync(room, Ct));

            // The proof that supervision resumed: the engine dies without settling its room, and the
            // adopted supervisor records the same post-launch fault a freshly launched lane would get.
            engine.Kill();
            var sentinel = await WaitForSentinelAsync(room);
            Assert.Equal(WorkflowOutcome.Failed, sentinel.State);
            Assert.Contains("exited with", sentinel.Error!, StringComparison.Ordinal);
            Assert.Contains("without settling the room", sentinel.Error, StringComparison.Ordinal);

            // ...and done detection resolves the row off that sentinel, as for any settled room.
            await new QueueSchedulerService(
                    (_, _) => Task.FromResult(new QueueLaunchOutcome(null)), _ => Task.FromResult(0.0), () => 16.0, null)
                .ResolveFinishedItemsAsync(Ct);
            Assert.Equal(QueueItemState.Failed, Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items).State);
        }
        finally
        {
            if (!engine.HasExited)
            {
                engine.Kill();
            }
        }
    }

    [Fact]
    public async Task A_dead_engine_leaves_the_row_launched_and_writes_nothing()
    {
        var (deadPid, deadStart) = ProcessIdentityFixture.DeadProcessIdentity();
        var room = RoomPath("dead");
        await WriteOpenRoomAsync(room, deadPid, deadStart);
        await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with { Items = [Launched("dead", room)] }, Ct);

        var adoptions = await QueueLauncher.AdoptLaunchedLanesAsync(Ct);

        var adoption = Assert.Single(adoptions);
        Assert.Equal(EngineLivenessStatus.Dead, adoption.Status);
        Assert.Equal(deadPid, adoption.EnginePid);

        // Left for the dead-pump probe (#2094) — no sentinel fabricated, no state changed. The wait is
        // the control for the alive arm's "sentinel appears": here it must not.
        // wait-ok: a fixed window in which nothing may happen, not a ceiling on something expected.
        await Task.Delay(500, Ct);
        Assert.Null(await TerminalSentinelWriter.TryReadAsync(room, Ct));
        Assert.Equal(QueueItemState.Launched, Assert.Single((await QueueStore.LoadAsync(BatonPaths.QueueFile, Ct)).Items).State);
    }

    [Fact]
    public async Task A_room_with_no_recorded_engine_identity_is_left_as_it_is()
    {
        // The room exists but its journal does not: the previous daemon died while this lane was
        // still provisioning.
        var room = RoomPath("blank");
        Directory.CreateDirectory(room);
        await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with { Items = [Launched("blank", room)] }, Ct);

        var adoption = Assert.Single(await QueueLauncher.AdoptLaunchedLanesAsync(Ct));

        Assert.Equal(EngineLivenessStatus.Unknown, adoption.Status);
        Assert.Null(adoption.EnginePid);
        Assert.Contains("no ledger", adoption.Why!, StringComparison.Ordinal);
        Assert.Null(await TerminalSentinelWriter.TryReadAsync(room, Ct));
    }

    [Fact]
    public async Task Only_launched_rows_with_a_room_are_walked()
    {
        var (deadPid, deadStart) = ProcessIdentityFixture.DeadProcessIdentity();
        var launchedRoom = RoomPath("walked");
        await WriteOpenRoomAsync(launchedRoom, deadPid, deadStart);
        var doneRoom = RoomPath("done");
        await WriteOpenRoomAsync(doneRoom, deadPid, deadStart);
        await QueueStore.MutateAsync(BatonPaths.QueueFile, s => s with
        {
            Items =
            [
                Launched("walked", launchedRoom),
                Launched("done", doneRoom) with { State = QueueItemState.Done },
                Launched("queued", null) with { State = QueueItemState.Queued },
                // The imported launched item QueueImport's own remarks describe: no room at all.
                Launched("imported", null),
            ],
        }, Ct);

        var adoptions = await QueueLauncher.AdoptLaunchedLanesAsync(Ct);

        Assert.Equal("walked", Assert.Single(adoptions).Tag);
    }

    private string RoomPath(string tag) => Path.Combine(BatonPaths.Rooms, $"queue-{tag}-{Guid.NewGuid().ToString("N")[..8]}");

    private static QueueItem Launched(string tag, string? room) => new()
    {
        Tag = tag,
        Role = "implement",
        Workspace = @"C:\repos\w1",
        SpecFile = Path.Combine(Path.GetTempPath(), "never-read.md"),
        State = QueueItemState.Launched,
        RoomDirectory = room,
        LaunchedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>One accepted, never-settled execution, stamped with the given engine identity where
    /// <c>MutationInterface</c> stamps a real one.</summary>
    private static async Task WriteOpenRoomAsync(string room, int enginePid, DateTimeOffset engineStart)
    {
        Directory.CreateDirectory(room);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("queue-adopt"),
            1,
            [new WorkflowStepDefinition(TheStep, "implement", [], ["out"], [], new RetryPolicy(1))]);
        var snapshot = SnapshotBinder.Bind(definition);
        await SnapshotBinder.PersistAsync(snapshot, Path.Combine(room, BatonPaths.SnapshotFileName), Ct);

        var executionId = new ExecutionId($"exec-{Guid.NewGuid():N}");
        var request = new ExecutionRequest(
            executionId,
            new WorkflowId("wf-queue-adopt"),
            TheStep,
            "implement",
            Inputs: [],
            Outputs: [],
            Timeout: TimeSpan.FromMinutes(30),
            Environment: [],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

        await using var writer = new FlowEventLogWriter(Path.Combine(room, BatonPaths.FlowLogFileName));
        await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(request, EnginePid: enginePid, EngineStartTime: engineStart), Ct);
    }

    private static async Task<WorkflowStatusView> WaitForSentinelAsync(string room)
    {
        var deadline = DateTime.UtcNow + SettleBound;
        while (DateTime.UtcNow < deadline)
        {
            if (await TerminalSentinelWriter.TryReadAsync(room, Ct) is { } sentinel)
            {
                return sentinel;
            }

            // wait-ok: the poll interval under SettleBound's 30s ceiling, not the ceiling itself.
            await Task.Delay(100, Ct);
        }

        throw new TimeoutException($"no terminal sentinel appeared in '{room}' within {SettleBound} of the engine's death");
    }
}
