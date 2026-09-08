using System.Diagnostics;
using System.Text.Json;
using Baton.Cli.Tests.TestSupport;
using Baton.Concurrency;
using Baton.Domain;
using Baton.Projection;
using Baton.Status;
using Baton.Store;
using Baton.Templates;
using Baton.Vendors;
using static Baton.Cli.Tests.TestSupport.ProcessIdentityFixture;

namespace Baton.Cli.Tests;

/// <summary>
/// #2073 (slice two of #1530): <c>baton cancel</c>'s three paths against a fake pump — the
/// pump-answers path, the no-pump kill path (with the revert-failing assertion that the intent fact
/// precedes the kill), and the already-terminal path. No live process beyond the fixture: the "pump"
/// is this test holding <c>flow.lock</c> and appending the events a real pump's poller would, and the
/// one real child spawned (<c>ping</c>) exists only to be the pid the kill path is aimed at.
/// </summary>
[Collection(ConsoleOutCaptureCollection.Name)]
public sealed class CancelCommandArrestTests : IDisposable
{
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter> { ["shell"] = new ShellCommandWorkerAdapter() };

    private static readonly StepId TheStep = new("implement");
    private static readonly TimeSpan FixtureWait = TimeSpan.FromSeconds(20);

    private readonly IsolatedBatonHome _home = new();

    public void Dispose() => _home.Dispose();

    // ---- the pump-answers path -------------------------------------------------------------

    [Fact]
    public async Task When_a_live_pump_answers_the_request_the_intent_is_on_record_first_and_nothing_is_killed()
    {
        var roomDir = NewRoomDir();
        var executionId = await WriteOpenRoomAsync(roomDir, workerPid: 4242, workerStartUtc: DateTime.UtcNow);
        var logPath = Path.Combine(roomDir, BatonPaths.FlowLogFileName);
        var roomLogPath = Path.Combine(roomDir, BatonPaths.RoomLogFileName);

        using (ConcurrencyGuard.Acquire(roomDir, "test holder simulating a live pump"))
        {
            var cancel = CancelCommand.ExecuteAsync(
                new CancelOptions(roomDir, ExecutionId: null, BindingsFilePath: "ignored", Reason: "lane is looping"),
                Adapters,
                TestContext.Current.CancellationToken,
                pumpAnswerWindow: FixtureWait,
                killWorker: (_, _) => throw new InvalidOperationException("the pump answered, so the kill path must never run"));

            // The fake pump: what CancelRequestPoller + the registry do on a real pump, minus the
            // process. It also checks the ordering claim from the pump's side of the fence -- by the
            // time cancel.request exists, the intent fact must already be in room.jsonl.
            var requestPath = CancelRequestFile.GetPath(roomDir);
            await WaitUntilAsync(() => File.Exists(requestPath), "cancel.request to be written");
            var intentsWhenRequestAppeared = (await new RoomEventLogReader(roomLogPath).ReadAllRoomEventsAsync(TestContext.Current.CancellationToken))
                .OfType<RoomEvent.ArrestIntentRecorded>().ToList();
            Assert.Single(intentsWhenRequestAppeared);

            await using (var pumpWriter = new FlowEventLogWriter(logPath))
            {
                await pumpWriter.AppendAsync(new FlowEvent.CancellationRequested(executionId, CancellationOrigin.Operator), TestContext.Current.CancellationToken);
                await pumpWriter.AppendAsync(new FlowEvent.ExecutionCancelled(executionId), TestContext.Current.CancellationToken);
            }

            CancelRequestFile.Consume(requestPath);

            var result = await cancel;

            Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
            Assert.Equal(StepStatus.Cancelled, result.State.Steps.Single().Status);
            Assert.False(result.CancellationQueued, "the pump applied it; this is not the queued arm");
            Assert.False(result.CancelWasNoOp);
        }

        var intent = Assert.Single((await new RoomEventLogReader(roomLogPath).ReadAllRoomEventsAsync(TestContext.Current.CancellationToken))
            .OfType<RoomEvent.ArrestIntentRecorded>());
        Assert.Equal(executionId.Value, intent.Target);
        Assert.Equal(CancelCommand.OperatorRequestedBy, intent.RequestedBy);
        Assert.Equal("lane is looping", intent.Reason);

        // The pump's own facts are the settle; baton cancel wrote no terminal fact of its own.
        var events = await new FlowEventLogReader(logPath).ReadAllAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(events, e => e is FlowEvent.ExecutionFailed);
        Assert.DoesNotContain(events, e => e is FlowEvent.StepRetryForeclosed);
    }

    // ---- the no-pump kill path -------------------------------------------------------------

    /// <summary>
    /// The revert-failing ordering assertion. The kill seam reads <c>room.jsonl</c> at the instant it
    /// is invoked; moving the intent append below the kill in <c>CancelCommand.ExecuteAsync</c> makes
    /// <c>intentOnRecordAtKill</c> false and this test red, with nothing else changed.
    /// </summary>
    [Fact]
    public async Task With_no_pump_the_intent_fact_is_written_before_the_worker_is_killed_and_the_room_settles_terminal()
    {
        var roomDir = NewRoomDir();
        var workerStart = DateTime.UtcNow.AddMinutes(-3);
        var executionId = await WriteOpenRoomAsync(roomDir, workerPid: 31337, workerStartUtc: workerStart);
        var roomLogPath = Path.Combine(roomDir, BatonPaths.RoomLogFileName);

        var killCalls = new List<(uint Pid, DateTimeOffset? StartTime, bool IntentOnRecord)>();
        var result = await CancelCommand.ExecuteAsync(
            new CancelOptions(roomDir, ExecutionId: executionId.Value, BindingsFilePath: "ignored", Reason: "looping"),
            Adapters,
            TestContext.Current.CancellationToken,
            pumpAnswerWindow: TimeSpan.FromMilliseconds(200),
            killWorker: (pid, startTime) =>
            {
                var intentOnRecord = File.Exists(roomLogPath)
                    && File.ReadAllText(roomLogPath).Contains("arrestIntentRecorded", StringComparison.Ordinal);
                killCalls.Add((pid, startTime, intentOnRecord));
                return new WorkerKillResult(WorkerKillOutcome.Killed, $"fake kill of pid {pid}");
            });

        var call = Assert.Single(killCalls);
        Assert.True(call.IntentOnRecord, "the intent fact must be in room.jsonl BEFORE the kill is attempted");
        Assert.Equal(31337u, call.Pid);
        Assert.NotNull(call.StartTime);
        Assert.Equal(workerStart, call.StartTime.Value.UtcDateTime, TimeSpan.FromSeconds(1));

        Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
        Assert.False(result.CancellationQueued);
        Assert.False(result.CancelWasNoOp);

        var failed = Assert.Single((await ReadEventsAsync(roomDir)).OfType<FlowEvent.ExecutionFailed>());
        Assert.Equal(executionId, failed.ExecutionId);
        // Permanent, never crash recovery's Retryable: a Retryable fact would hand the room back to a
        // pump that reschedules the step -- the redispatch-on-cancel defect this verb retired.
        Assert.Equal(FailureClassification.Permanent, failed.FailureClassification);
        Assert.StartsWith(CancelCommand.ArrestReasonPrefix, failed.Reason, StringComparison.Ordinal);
        Assert.Contains("looping", failed.Reason, StringComparison.Ordinal);
        Assert.Contains("fake kill of pid 31337", failed.Reason, StringComparison.Ordinal);

        // The ledger reads the intent as Delivered off the terminal fact that followed it.
        var roomEvents = await new RoomEventLogReader(roomLogPath).ReadAllRoomEventsAsync(TestContext.Current.CancellationToken);
        var entries = await new FlowEventLogReader(Path.Combine(roomDir, BatonPaths.FlowLogFileName))
            .ReadAllEntriesWithTimestampsAsync(TestContext.Current.CancellationToken);
        var ledgerEntry = Assert.Single(ArrestLedgerProjector.Project(entries, roomEvents));
        Assert.Equal(ArrestOutcome.Delivered, ledgerEntry.Outcome);
        Assert.Equal("looping", ledgerEntry.Reason);
    }

    [Fact]
    public async Task With_no_pump_a_real_worker_process_is_killed_through_the_liveness_probe()
    {
        var roomDir = NewRoomDir();
        var psi = new ProcessStartInfo("ping.exe", "-t 127.0.0.1") { CreateNoWindow = true };
        using var worker = Process.Start(psi)!;
        try
        {
            var workerStartUtc = worker.StartTime.ToUniversalTime();
            var executionId = await WriteOpenRoomAsync(roomDir, workerPid: (uint)worker.Id, workerStartUtc: workerStartUtc);

            var result = await CancelCommand.ExecuteAsync(
                new CancelOptions(roomDir, ExecutionId: null, BindingsFilePath: "ignored"),
                Adapters,
                TestContext.Current.CancellationToken,
                pumpAnswerWindow: TimeSpan.FromMilliseconds(200));

            Assert.True(worker.WaitForExit(TimeSpan.FromSeconds(10)), "the recorded worker pid must have been killed"); // wait-ok: bounding a post-kill exit (#1804)
            Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
            var failed = Assert.Single((await ReadEventsAsync(roomDir)).OfType<FlowEvent.ExecutionFailed>());
            Assert.Equal(executionId, failed.ExecutionId);
            Assert.Contains("killed", failed.Reason, StringComparison.Ordinal);
        }
        finally
        {
            if (!worker.HasExited)
            {
                worker.Kill(entireProcessTree: true);
            }
        }
    }

    /// <summary>
    /// The probe is the gate, and its two non-Alive verdicts must both leave the process alone. The
    /// control here is deliberately a LIVE process with a mismatched start time: if the start-time
    /// discriminator were ignored, this would kill a real process that merely shares a recycled pid.
    /// </summary>
    [Fact]
    public void WorkerProcessArrest_kills_only_on_a_confirmed_Alive_verdict()
    {
        var (deadPid, deadStart) = DeadProcessIdentity();
        Assert.Equal(WorkerKillOutcome.AlreadyExited, WorkerProcessArrest.Kill((uint)deadPid, deadStart).Outcome);

        var psi = new ProcessStartInfo("ping.exe", "-t 127.0.0.1") { CreateNoWindow = true };
        using var bystander = Process.Start(psi)!;
        try
        {
            var realStart = new DateTimeOffset(bystander.StartTime).ToUniversalTime();

            var noStartTime = WorkerProcessArrest.Kill((uint)bystander.Id, processStartTime: null);
            Assert.Equal(WorkerKillOutcome.NotConfirmed, noStartTime.Outcome);
            Assert.False(bystander.HasExited, "Unknown must not kill");

            var recycledPid = WorkerProcessArrest.Kill((uint)bystander.Id, realStart.AddHours(-1));
            Assert.Equal(WorkerKillOutcome.AlreadyExited, recycledPid.Outcome);
            Assert.False(bystander.HasExited, "a start-time mismatch reads Dead (recycled pid) and must not kill the bystander");

            var confirmed = WorkerProcessArrest.Kill((uint)bystander.Id, realStart);
            Assert.Equal(WorkerKillOutcome.Killed, confirmed.Outcome);
            Assert.True(bystander.WaitForExit(TimeSpan.FromSeconds(10))); // wait-ok: bounding a post-kill exit (#1804)
        }
        finally
        {
            if (!bystander.HasExited)
            {
                bystander.Kill(entireProcessTree: true);
            }
        }
    }

    // ---- the already-terminal path ---------------------------------------------------------

    [Fact]
    public async Task A_room_that_is_already_terminal_is_reported_as_such_writes_nothing_and_exits_0()
    {
        var testRoot = NewRoomDir();
        var roomDir = Path.Combine(testRoot, "task");
        var workflowFilePath = await WriteOneQuickStepWorkflowAsync(testRoot);
        var bindingsFilePath = await WriteOneQuickStepBindingsAsync(testRoot);
        var finalState = (await RunCommand.ExecuteAsync(new RunOptions(workflowFilePath, bindingsFilePath, roomDir), Adapters, cancellationToken: TestContext.Current.CancellationToken)).State;
        Assert.Equal(WorkflowStatus.Terminal, finalState.Status);

        var logPath = Path.Combine(roomDir, BatonPaths.FlowLogFileName);
        var journalBefore = await File.ReadAllTextAsync(logPath, TestContext.Current.CancellationToken);

        var originalOut = Console.Out;
        var capturedOut = new StringWriter();
        Console.SetOut(capturedOut);
        CommandResult result;
        try
        {
            result = await CancelCommand.ExecuteAsync(
                new CancelOptions(roomDir, ExecutionId: null, bindingsFilePath), Adapters, TestContext.Current.CancellationToken);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        Assert.True(result.CancelWasNoOp);
        Assert.Contains("already terminal", capturedOut.ToString(), StringComparison.Ordinal);
        Assert.Equal(MutationExitCodeResolver.Success, MutationExitCodeResolver.Resolve(result));
        Assert.Equal(journalBefore, await File.ReadAllTextAsync(logPath, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(roomDir, BatonPaths.RoomLogFileName)), "no intent fact for a room with nothing to arrest");
        Assert.False(File.Exists(CancelRequestFile.GetPath(roomDir)));
    }

    // ---- fixtures --------------------------------------------------------------------------

    private string NewRoomDir()
    {
        var dir = Path.Combine(_home.Path, "rooms", $"room-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>One accepted, never-settled execution — a Running step whose worker the journal names.</summary>
    private static async Task<ExecutionId> WriteOpenRoomAsync(string roomDir, uint? workerPid, DateTime? workerStartUtc)
    {
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("cancel-arrest"),
            1,
            [new WorkflowStepDefinition(TheStep, "implement", [], ["out"], [], new RetryPolicy(3))]);
        var snapshot = SnapshotBinder.Bind(definition);
        await SnapshotBinder.PersistAsync(snapshot, Path.Combine(roomDir, BatonPaths.SnapshotFileName), TestContext.Current.CancellationToken);

        var executionId = new ExecutionId($"exec-{Guid.NewGuid():N}");
        var request = new ExecutionRequest(
            executionId,
            new WorkflowId("wf-cancel-arrest"),
            TheStep,
            "implement",
            Inputs: [],
            Outputs: [],
            Timeout: TimeSpan.FromMinutes(30),
            Environment: [],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

        await using var writer = new FlowEventLogWriter(Path.Combine(roomDir, BatonPaths.FlowLogFileName));
        await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(request, EnginePid: 999_999, EngineStartTime: null), TestContext.Current.CancellationToken);
        await writer.AppendAsync(new FlowEvent.ExecutionAttemptStarted(executionId, new string('0', 40)), TestContext.Current.CancellationToken);
        if (workerPid is { } pid)
        {
            await writer.AppendAsync(new CoreEvent.ExecutionStarted(executionId, pid, workerStartUtc), TestContext.Current.CancellationToken);
        }

        return executionId;
    }

    private static async Task<IReadOnlyList<FlowEvent>> ReadEventsAsync(string roomDir) =>
        await new FlowEventLogReader(Path.Combine(roomDir, BatonPaths.FlowLogFileName)).ReadAllAsync(TestContext.Current.CancellationToken);

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var started = Stopwatch.GetTimestamp();
        while (!condition())
        {
            if (Stopwatch.GetElapsedTime(started) > FixtureWait)
            {
                Assert.Fail($"Timed out after {FixtureWait} waiting for {what}.");
            }

            await Task.Delay(50, TestContext.Current.CancellationToken); // wait-ok: bounded by FixtureWait above (#1804)
        }
    }

    private static async Task<string> WriteOneQuickStepWorkflowAsync(string directory)
    {
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("one-quick-step-cancel-noop"), 1,
            [new WorkflowStepDefinition(new StepId("a"), "a", [], ["out"], [], new RetryPolicy(1))]);
        var path = Path.Combine(directory, "workflow.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(definition), TestContext.Current.CancellationToken);
        return path;
    }

    private static async Task<string> WriteOneQuickStepBindingsAsync(string directory)
    {
        const string writeCommand = "echo done>%BATON_OUTPUT_DIR%\\out";
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["a"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("a", [], [new ProducedOutput("out")], []), writeCommand, TimeSpan.FromSeconds(30)),
        };
        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config), TestContext.Current.CancellationToken);
        return path;
    }
}
