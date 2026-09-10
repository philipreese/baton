using System.Text;
using System.Text.Json;
using Baton.Cli.Daemon;
using Baton.Cli.Mcp;
using Baton.Cli.Tests.TestSupport;
using Baton.Concurrency;
using Baton.Domain;
using Baton.Projection;
using Baton.Status;
using Baton.Store;
using Baton.Templates;
using Baton.Vendors;

namespace Baton.Cli.Tests.Daemon;

/// <summary>
/// #2072: <see cref="DeadPumpProbe"/>'s room-level probe — spec/baton.md §7's <c>DeadPumpProbe</c>
/// bullet is the contract these pin.
/// <para>
/// Fixtures hand-write <c>flow.jsonl</c> lines with a chosen <c>WriterUtcTimestamp</c> instead of going
/// through <see cref="FlowEventLogWriter"/>, which stamps <see cref="DateTime.UtcNow"/> — a room that is
/// quiet by hours is the whole predicate under test and cannot be produced any other way without making
/// the tests wait.
/// </para>
/// </summary>
public sealed class DeadPumpProbeTests : IDisposable
{
    private readonly IsolatedBatonHome _home = new();

    public void Dispose() => _home.Dispose();

    private static readonly StepId TheStep = new("implement");

    private static DeadPumpProbe Probe(int? quietMinutes = null) =>
        new(new DaemonSettings { DeadPumpQuietMinutes = quietMinutes });

    /// <summary>
    /// A room with one accepted, never-settled execution: exactly what a pump killed mid-step leaves
    /// behind. <paramref name="quietFor"/> ages every line and the file itself.
    /// </summary>
    private async Task<(string RoomDir, ExecutionId ExecutionId, DateTime LastEventUtc)> WriteOpenRoomAsync(
        TimeSpan quietFor, uint? workerPid = 4242)
    {
        var roomDir = Path.Combine(_home.Path, "rooms", $"room-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDir);

        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("dead-pump-probe"),
            1,
            [new WorkflowStepDefinition(TheStep, "implement", [], ["out"], [], new RetryPolicy(3))]);
        var snapshot = SnapshotBinder.Bind(definition);
        await SnapshotBinder.PersistAsync(
            snapshot, Path.Combine(roomDir, BatonPaths.SnapshotFileName), TestContext.Current.CancellationToken);

        var executionId = new ExecutionId("exec-dead-pump-1");
        var request = new ExecutionRequest(
            executionId,
            new WorkflowId("wf-dead-pump"),
            TheStep,
            "implement",
            Inputs: [],
            Outputs: [],
            Timeout: TimeSpan.FromMinutes(30),
            Environment: [],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

        var lastEventUtc = DateTime.UtcNow - quietFor;
        var lines = new List<LogEntry>
        {
            new LogEntry.FlowLogEntry(
                new FlowEvent.ExecutionRequestAccepted(request, EnginePid: 999_999, EngineStartTime: null),
                lastEventUtc.AddMinutes(-2)),
            new LogEntry.FlowLogEntry(
                new FlowEvent.ExecutionAttemptStarted(executionId, "0000000000000000000000000000000000000000"),
                lastEventUtc.AddMinutes(-2)),
        };

        if (workerPid is { } pid)
        {
            lines.Add(new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(executionId, pid), lastEventUtc));
        }

        var logPath = Path.Combine(roomDir, BatonPaths.FlowLogFileName);
        var text = new StringBuilder();
        foreach (var entry in lines)
        {
            text.Append(JsonSerializer.Serialize(entry, typeof(LogEntry), FlowEventLogJson.Options)).Append('\n');
        }

        await File.WriteAllTextAsync(logPath, text.ToString(), TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(logPath, lastEventUtc);

        return (roomDir, executionId, lastEventUtc);
    }

    /// <summary>
    /// The other shape <see cref="ArrestableExecutions.All"/> admits (#1607): the latest execution has
    /// already settled <see cref="FailureClassification.ExhaustedUntil"/> and a retry is scheduled that
    /// nothing will now drive — a quota-parked step, which is precisely the #1513 "StepRetryScheduled
    /// wait in a dead process" the probe exists for.
    /// </summary>
    private async Task<(string RoomDir, ExecutionId ExecutionId)> WriteQuotaParkedRoomAsync(TimeSpan quietFor)
    {
        var (roomDir, executionId, lastEventUtc) = await WriteOpenRoomAsync(quietFor);
        var logPath = Path.Combine(roomDir, BatonPaths.FlowLogFileName);
        var text = new StringBuilder();
        foreach (var entry in new LogEntry[]
        {
            new LogEntry.FlowLogEntry(
                new FlowEvent.ExecutionFailed(executionId, FailureClassification.ExhaustedUntil, "quota"), lastEventUtc),
            new LogEntry.FlowLogEntry(
                new FlowEvent.StepRetryScheduled(
                    TheStep, executionId, DateTimeOffset.UtcNow.AddHours(4), RetryDelayMs: 600_000),
                lastEventUtc),
        })
        {
            text.Append(JsonSerializer.Serialize(entry, typeof(LogEntry), FlowEventLogJson.Options)).Append('\n');
        }

        await File.AppendAllTextAsync(logPath, text.ToString(), TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(logPath, lastEventUtc);
        return (roomDir, executionId);
    }

    [Fact]
    public async Task An_unknown_reset_quota_park_without_a_pump_is_left_parked()
    {
        var (roomDir, executionId, lastEventUtc) = await WriteOpenRoomAsync(TimeSpan.FromHours(3));
        var logPath = Path.Combine(roomDir, BatonPaths.FlowLogFileName);
        var entry = new LogEntry.FlowLogEntry(
            new FlowEvent.ExecutionFailed(executionId, FailureClassification.ExhaustedUntil, "quota reset unknown"),
            lastEventUtc);
        await File.AppendAllTextAsync(logPath,
            JsonSerializer.Serialize(entry, typeof(LogEntry), FlowEventLogJson.Options) + "\n",
            TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(logPath, lastEventUtc);
        var before = await File.ReadAllTextAsync(logPath, TestContext.Current.CancellationToken);
        var probe = Probe();

        Assert.Equal(0, await probe.ProbeRoomAsync(roomDir, TestContext.Current.CancellationToken, TextWriter.Null));
        Assert.Equal(0, await probe.ProbeRoomAsync(roomDir, TestContext.Current.CancellationToken, TextWriter.Null));
        Assert.Equal(before, await File.ReadAllTextAsync(logPath, TestContext.Current.CancellationToken));
        Assert.Equal(WorkflowOutcome.Running, await DescribeOutcomeAsync(roomDir));
        Assert.Empty((await ReadEventsAsync(roomDir)).OfType<FlowEvent.StepRetryForeclosed>());
    }
    private static async Task<IReadOnlyList<FlowEvent>> ReadEventsAsync(string roomDir) =>
        await new FlowEventLogReader(Path.Combine(roomDir, BatonPaths.FlowLogFileName))
            .ReadAllAsync(TestContext.Current.CancellationToken);

    private static async Task<FlowState> ProjectAsync(string roomDir)
    {
        var snapshot = await SnapshotBinder.LoadFromFileAsync(
            Path.Combine(roomDir, BatonPaths.SnapshotFileName), TestContext.Current.CancellationToken);
        return StateProjector.Project(await ReadEventsAsync(roomDir), snapshot);
    }

    private static async Task<string> DescribeOutcomeAsync(string roomDir) =>
        WorkflowOutcome.Describe(await ProjectAsync(roomDir));

    [Fact]
    public async Task A_free_lock_over_a_quiet_open_execution_records_the_arrest()
    {
        var (roomDir, executionId, lastEventUtc) = await WriteOpenRoomAsync(TimeSpan.FromHours(3));

        var appended = await Probe().ProbeRoomAsync(roomDir, TestContext.Current.CancellationToken, TextWriter.Null);

        Assert.Equal(1, appended);
        var failed = Assert.Single((await ReadEventsAsync(roomDir)).OfType<FlowEvent.ExecutionFailed>());
        Assert.Equal(executionId, failed.ExecutionId);

        // Permanent, not Retryable: the polarity test below is what proves this is load-bearing rather
        // than a preference.
        Assert.Equal(FailureClassification.Permanent, failed.FailureClassification);

        // The three things the fact has to name (#2072): the cause, the last event timestamp, the pid.
        Assert.Contains("pump dead", failed.Reason);
        Assert.Contains(new DateTimeOffset(DateTime.SpecifyKind(lastEventUtc, DateTimeKind.Utc)).ToString("O"), failed.Reason);
        Assert.Contains("worker pid 4242", failed.Reason);
    }

    /// <summary>
    /// The instrument that actually answers "is the arrest operator-visible?" — a fact that lands and
    /// leaves the room reading Running would be the `right-instrument` failure this issue exists to fix.
    /// Both directions asserted, so a Retryable classification (which
    /// <c>StateProjector.DeriveWorkflowStatus</c> keeps reading Running) fails this test.
    /// </summary>
    [Fact]
    public async Task The_room_reads_Running_before_the_probe_and_Failed_after_it()
    {
        var (roomDir, _, _) = await WriteOpenRoomAsync(TimeSpan.FromHours(3));

        Assert.Equal(WorkflowOutcome.Running, await DescribeOutcomeAsync(roomDir));

        await Probe().ProbeRoomAsync(roomDir, TestContext.Current.CancellationToken, TextWriter.Null);

        Assert.Equal(WorkflowOutcome.Failed, await DescribeOutcomeAsync(roomDir));
    }

    /// <summary>
    /// The same question one reader further out. <c>FleetStatusTool.ProcessRoomAsync</c> is what
    /// <c>fleet_status</c>, <c>FleetProjectionWriter</c> and therefore the glass all read a room
    /// through, and its terminal fast path keys on <c>terminal.json</c> — which the probe deliberately
    /// does not write. This pins that the fact is still visible out there with no sentinel beside it.
    /// </summary>
    [Fact]
    public async Task The_fleet_row_reads_Running_before_the_probe_and_Failed_after_it()
    {
        var (roomDir, _, _) = await WriteOpenRoomAsync(TimeSpan.FromHours(3));

        var before = await FleetStatusTool.ProcessRoomAsync(roomDir, includeTerminal: true, TestContext.Current.CancellationToken);
        Assert.Equal(WorkflowOutcome.Running, before!.State);

        await Probe().ProbeRoomAsync(roomDir, TestContext.Current.CancellationToken, TextWriter.Null);

        var after = await FleetStatusTool.ProcessRoomAsync(roomDir, includeTerminal: true, TestContext.Current.CancellationToken);
        Assert.Equal(WorkflowOutcome.Failed, after!.State);
        var step = Assert.Single(after.Steps!);
        Assert.Equal(nameof(FailureClassification.Permanent), step.FailureKind);
        Assert.Equal(false, step.RetryEligible);
    }

    /// <summary>#2072 requirement (3): never twice for the same room. Not argued from the projection —
    /// run twice and count.</summary>
    [Fact]
    public async Task A_second_probe_of_the_same_room_appends_nothing()
    {
        var (roomDir, _, _) = await WriteOpenRoomAsync(TimeSpan.FromHours(3));
        var probe = Probe();

        Assert.Equal(1, await probe.ProbeRoomAsync(roomDir, TestContext.Current.CancellationToken, TextWriter.Null));

        // The journal's mtime is now, so age the file back past the window again: without this the
        // second pass would be refused by the prefilter and would prove nothing about idempotence.
        var logPath = Path.Combine(roomDir, BatonPaths.FlowLogFileName);
        File.SetLastWriteTimeUtc(logPath, DateTime.UtcNow - TimeSpan.FromHours(3));

        Assert.Equal(0, await probe.ProbeRoomAsync(roomDir, TestContext.Current.CancellationToken, TextWriter.Null));
        Assert.Single((await ReadEventsAsync(roomDir)).OfType<FlowEvent.ExecutionFailed>());
    }

    /// <summary>
    /// The third shape <see cref="ArrestableExecutions.All"/> admits: a step-less supplementary
    /// execution (an <see cref="ExecutionRequest"/> carrying no <c>StepId</c>). It is arrested on the
    /// same <see cref="FlowEvent.ExecutionFailed"/> arm as a Running step, and — the half a single-step
    /// fixture cannot see — it stops being a target afterwards, because
    /// <c>StateProjector</c> records <c>TerminalStatusByExecutionId</c> before it ever looks for a
    /// <c>StepId</c>.
    /// </summary>
    [Fact]
    public async Task A_step_less_execution_is_arrested_once_and_not_again()
    {
        var (roomDir, _, lastEventUtc) = await WriteOpenRoomAsync(TimeSpan.FromHours(3));
        var stepLessId = new ExecutionId("exec-dead-pump-step-less");
        var logPath = Path.Combine(roomDir, BatonPaths.FlowLogFileName);
        await File.AppendAllTextAsync(
            logPath,
            JsonSerializer.Serialize(
                (LogEntry)new LogEntry.FlowLogEntry(
                    new FlowEvent.ExecutionRequestAccepted(
                        new ExecutionRequest(
                            stepLessId,
                            new WorkflowId("wf-dead-pump"),
                            StepId: null,
                            "implement",
                            Inputs: [],
                            Outputs: [],
                            Timeout: TimeSpan.FromMinutes(30),
                            Environment: [],
                            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>()),
                        EnginePid: 999_999,
                        EngineStartTime: null),
                    lastEventUtc),
                typeof(LogEntry),
                FlowEventLogJson.Options) + "\n",
            TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(logPath, lastEventUtc);

        var probe = Probe();

        // Both open executions — the step-tied one and the step-less one.
        Assert.Equal(2, await probe.ProbeRoomAsync(roomDir, TestContext.Current.CancellationToken, TextWriter.Null));
        Assert.Contains(
            (await ReadEventsAsync(roomDir)).OfType<FlowEvent.ExecutionFailed>(), f => f.ExecutionId == stepLessId);

        File.SetLastWriteTimeUtc(logPath, DateTime.UtcNow - TimeSpan.FromHours(3));
        Assert.Equal(0, await probe.ProbeRoomAsync(roomDir, TestContext.Current.CancellationToken, TextWriter.Null));
        Assert.Equal(2, (await ReadEventsAsync(roomDir)).OfType<FlowEvent.ExecutionFailed>().Count());
    }

    /// <summary>
    /// The quota-parked arm, both halves of what a second <see cref="FlowEvent.ExecutionFailed"/> would
    /// have got wrong: the room must actually STOP reading Running (a parked step's
    /// <c>RetryNotBefore</c> keeps <c>StateProjector.DeriveWorkflowStatus</c> deliverable until the
    /// foreclosure clears it), and the next tick must append nothing.
    /// </summary>
    [Fact]
    public async Task A_quota_parked_step_is_foreclosed_once_and_the_room_stops_reading_Running()
    {
        var (roomDir, executionId) = await WriteQuotaParkedRoomAsync(TimeSpan.FromHours(3));
        var probe = Probe();

        Assert.Equal(WorkflowOutcome.Running, await DescribeOutcomeAsync(roomDir));

        Assert.Equal(1, await probe.ProbeRoomAsync(roomDir, TestContext.Current.CancellationToken, TextWriter.Null));

        var foreclosed = Assert.Single((await ReadEventsAsync(roomDir)).OfType<FlowEvent.StepRetryForeclosed>());
        Assert.Equal(executionId, foreclosed.ForExecutionId);
        Assert.Equal(DeadPumpProbe.DiagnosticName, foreclosed.ForeclosedBy);
        Assert.Contains("pump dead", foreclosed.Reason);

        // The arrest is only operator-visible if the room leaves Running. Polarity, not shape.
        Assert.Equal(WorkflowOutcome.Failed, await DescribeOutcomeAsync(roomDir));

        // The reader-level half the ExecutionFailed arm's fleet test already has: `baton status`'s
        // step line must name the pump, not the quota park the foreclosure settled.
        var rendered = StatusCommand.FormatStepStatus(
            Assert.Single((await ProjectAsync(roomDir)).Steps), await ReadEventsAsync(roomDir));
        Assert.Contains("pump dead", rendered);
        Assert.DoesNotContain("quota", rendered);

        // No second ExecutionFailed piled onto an execution that already settled ExhaustedUntil.
        Assert.Single((await ReadEventsAsync(roomDir)).OfType<FlowEvent.ExecutionFailed>());

        var logPath = Path.Combine(roomDir, BatonPaths.FlowLogFileName);
        File.SetLastWriteTimeUtc(logPath, DateTime.UtcNow - TimeSpan.FromHours(3));
        Assert.Equal(0, await probe.ProbeRoomAsync(roomDir, TestContext.Current.CancellationToken, TextWriter.Null));
        Assert.Single((await ReadEventsAsync(roomDir)).OfType<FlowEvent.StepRetryForeclosed>());
    }

    /// <summary>
    /// The discriminating control: identical fixture, identical age, lock HELD. Without it, every
    /// assertion above would pass just as well for a probe that never looked at the lock at all —
    /// see <see cref="DeadPumpProbe.ProbeRoomAsync"/>'s acquire comment for why that is the worst
    /// outcome available to this probe.
    /// </summary>
    [Fact]
    public async Task A_held_flow_lock_records_nothing()
    {
        var (roomDir, _, _) = await WriteOpenRoomAsync(TimeSpan.FromHours(3));

        using (ConcurrencyGuard.Acquire(roomDir, "a live pump"))
        {
            var appended = await Probe().ProbeRoomAsync(roomDir, TestContext.Current.CancellationToken, TextWriter.Null);
            Assert.Equal(0, appended);
        }

        Assert.Empty((await ReadEventsAsync(roomDir)).OfType<FlowEvent.ExecutionFailed>());
    }

    /// <summary>The threshold's other polarity: same free lock, same open execution, journal written
    /// inside the window.</summary>
    [Fact]
    public async Task A_journal_quiet_for_less_than_the_window_records_nothing()
    {
        var (roomDir, _, _) = await WriteOpenRoomAsync(DeadPumpProbe.DefaultQuietWindow - TimeSpan.FromMinutes(2));

        var appended = await Probe().ProbeRoomAsync(roomDir, TestContext.Current.CancellationToken, TextWriter.Null);

        Assert.Equal(0, appended);
        Assert.Empty((await ReadEventsAsync(roomDir)).OfType<FlowEvent.ExecutionFailed>());
    }

    /// <summary>A settled room is not a dead pump — the sentinel is the same one FleetStatusTool's fast
    /// path keys on.</summary>
    [Fact]
    public async Task A_room_carrying_a_terminal_sentinel_records_nothing()
    {
        var (roomDir, _, _) = await WriteOpenRoomAsync(TimeSpan.FromHours(3));
        await TerminalSentinelWriter.WriteAsync(
            roomDir, new WorkflowStatusView("Succeeded", [], [], null, null), TestContext.Current.CancellationToken);

        Assert.Equal(0, await Probe().ProbeRoomAsync(roomDir, TestContext.Current.CancellationToken, TextWriter.Null));
        Assert.Empty((await ReadEventsAsync(roomDir)).OfType<FlowEvent.ExecutionFailed>());
    }

    /// <summary>The journal records no worker pid (a non-process dispatch, or a kill before Core
    /// reported one): the fact still lands, and says nothing it cannot back.</summary>
    [Fact]
    public async Task A_room_with_no_recorded_worker_pid_still_records_the_arrest_without_naming_one()
    {
        var (roomDir, _, _) = await WriteOpenRoomAsync(TimeSpan.FromHours(3), workerPid: null);

        Assert.Equal(1, await Probe().ProbeRoomAsync(roomDir, TestContext.Current.CancellationToken, TextWriter.Null));
        var failed = Assert.Single((await ReadEventsAsync(roomDir)).OfType<FlowEvent.ExecutionFailed>());
        Assert.Contains("pump dead", failed.Reason);
        Assert.DoesNotContain("worker pid", failed.Reason);
    }

    [Fact]
    public async Task The_fleet_sweep_reaches_a_discovered_room()
    {
        var (roomDir, _, _) = await WriteOpenRoomAsync(TimeSpan.FromHours(3));

        var appended = await Probe().ProbeOnceAsync(TestContext.Current.CancellationToken, TextWriter.Null);

        Assert.Equal(1, appended);
        Assert.Single((await ReadEventsAsync(roomDir)).OfType<FlowEvent.ExecutionFailed>());
    }

    [Fact]
    public void An_absent_or_nonsensical_quiet_setting_falls_back_to_the_default()
    {
        Assert.Equal(DeadPumpProbe.DefaultQuietWindow, DeadPumpProbe.ResolveQuietWindow(new DaemonSettings()));
        Assert.Equal(
            DeadPumpProbe.DefaultQuietWindow,
            DeadPumpProbe.ResolveQuietWindow(new DaemonSettings { DeadPumpQuietMinutes = 0 }));
        Assert.Equal(
            DeadPumpProbe.DefaultQuietWindow,
            DeadPumpProbe.ResolveQuietWindow(new DaemonSettings { DeadPumpQuietMinutes = -5 }));
        Assert.Equal(
            TimeSpan.FromMinutes(90),
            DeadPumpProbe.ResolveQuietWindow(new DaemonSettings { DeadPumpQuietMinutes = 90 }));
    }

    /// <summary>An operator's widened window is honoured, not merely parsed — the same fixture the
    /// default arrests is left alone by a longer one.</summary>
    [Fact]
    public async Task A_widened_quiet_window_holds_the_probe_off_a_room_the_default_would_arrest()
    {
        var (roomDir, _, _) = await WriteOpenRoomAsync(TimeSpan.FromHours(3));

        Assert.Equal(
            0, await Probe(quietMinutes: 600).ProbeRoomAsync(roomDir, TestContext.Current.CancellationToken, TextWriter.Null));
        Assert.Equal(
            1, await Probe().ProbeRoomAsync(roomDir, TestContext.Current.CancellationToken, TextWriter.Null));
    }
}
