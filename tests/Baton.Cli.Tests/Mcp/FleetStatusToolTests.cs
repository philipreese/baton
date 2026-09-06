using System.Diagnostics;
using System.Text.Json;
using Baton.Cli;
using Baton.Vendors;
using Baton.Domain;
using Baton.Status;
using Baton.Store;
using Baton.Templates;
using Baton.Cli.Daemon;
using Baton.Cli.Mcp;
using static Baton.Cli.Tests.TestSupport.ProcessIdentityFixture;

namespace Baton.Cli.Tests.Mcp;

/// <summary>
/// Unit and integration coverage for <see cref="FleetStatusTool"/> (#1392 Spike 1).
/// Validates root enumeration, terminal sentinel fast path, active room projection,
/// filtering, and graceful error handling on malformed rooms.
/// </summary>
// #1496: BatonPaths.Root now resolves through BatonEnvironmentSnapshot.Current, which is captured
// once per process and never re-reads the environment -- so an Environment.SetEnvironmentVariable
// here would no longer be observed. BeginScope supplies an isolated root explicitly instead, which
// needs no SerializedEnvironmentCollection enrollment (nothing mutates process state) and runs
// parallel-safe with everything else.
public sealed class FleetStatusToolTests : IDisposable
{
    private readonly string _tempHome;
    private readonly IDisposable _scope;

    public FleetStatusToolTests()
    {
        _tempHome = Path.Combine(Path.GetTempPath(), $"baton-fleet-test-home-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempHome);
        _scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = _tempHome });
    }

    public void Dispose()
    {
        _scope.Dispose();
        if (Directory.Exists(_tempHome))
        {
            DirectoryCleanup.DeleteRecursively(_tempHome);
        }
    }

    [Fact]
    public async Task Enumeration_IncludesExtraRoots_AndDiscoversRoomsAcrossRoots()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room1 = Path.Combine(defaultRoomsDir, "room-default");
        var extraRoot = Path.Combine(Path.GetTempPath(), $"baton-fleet-test-extra-{Guid.NewGuid():N}");
        var room2 = Path.Combine(extraRoot, "room-extra");

        try
        {
            Directory.CreateDirectory(room1);
            Directory.CreateDirectory(room2);

            var sentinel1 = new WorkflowStatusView("Succeeded", [], [], null, null);
            var sentinel2 = new WorkflowStatusView("Failed", [], [], "Test failure", null);

            await TerminalSentinelWriter.WriteAsync(room1, sentinel1, TestContext.Current.CancellationToken);
            await TerminalSentinelWriter.WriteAsync(room2, sentinel2, TestContext.Current.CancellationToken);

            var tool = new FleetStatusTool();
            var escapedExtraRoot = extraRoot.Replace("\\", "\\\\");
            var result = await tool.CallAsync(Parse($$"""{ "roots": ["{{escapedExtraRoot}}"] }"""), TestContext.Current.CancellationToken);

            Assert.False(result.IsError);
            var rooms = JsonSerializer.Deserialize<FleetStatusResponse>(result.Text)!.Rooms;
            Assert.NotNull(rooms);
            Assert.Equal(2, rooms!.Count);

            var names = rooms.Select(r => r.Name).OrderBy(n => n).ToList();
            Assert.Equal(["room-default", "room-extra"], names);
        }
        finally
        {
            if (Directory.Exists(extraRoot))
            {
                DirectoryCleanup.DeleteRecursively(extraRoot);
            }
        }
    }

    /// <summary>
    /// #1619 LOW-3: a harness that ever passes a by-workstream slug directory as a <c>roots</c> entry
    /// must not double-count the room already found by the default <see cref="BatonPaths.Rooms"/>
    /// scan -- everything under <see cref="BatonPaths.ByWorkstream"/> is a junction back into a room
    /// the default scan already found by its real path, and <c>seenRooms</c> dedupes on the path
    /// string (<see cref="BatonPaths.RecordKey"/>), not the resolved target.
    /// </summary>
    [Fact]
    public async Task Enumeration_SkipsAByWorkstreamRoot_SoAJunctionedRoomIsNotDoubleCounted()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "room-grouped");
        Directory.CreateDirectory(room);

        var sentinel = new WorkflowStatusView("Succeeded", [], [], null, null);
        await TerminalSentinelWriter.WriteAsync(room, sentinel, TestContext.Current.CancellationToken);

        WorkstreamJunctionLinker.CreateIfRequested("w1619", room);
        var slugDir = Path.Combine(BatonPaths.ByWorkstream, "w1619");
        try
        {
            var tool = new FleetStatusTool();
            var escapedSlugDir = slugDir.Replace("\\", "\\\\");
            var result = await tool.CallAsync(Parse($$"""{ "roots": ["{{escapedSlugDir}}"] }"""), TestContext.Current.CancellationToken);

            Assert.False(result.IsError);
            var rooms = JsonSerializer.Deserialize<FleetStatusResponse>(result.Text)!.Rooms;
            Assert.NotNull(rooms);
            Assert.Single(rooms!);
        }
        finally
        {
            // Unlink before Dispose() tears down _tempHome and the room the junction points at.
            var linkPath = WorkstreamJunctionLinker.ResolveLinkPath("w1619", room);
            if (Directory.Exists(linkPath))
            {
                Directory.Delete(linkPath, recursive: false);
            }
        }
    }

    [Fact]
    public async Task TerminalFastPath_UsesSentinelWithoutReadingSnapshotOrLedger()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "terminal-room");
        Directory.CreateDirectory(room);

        var step = new WorkflowStatusStepView("step-a", "Succeeded", "exec-1", null, null, null);
        var sentinel = new WorkflowStatusView("Succeeded", [step], ["/tmp/out.txt"], null, null);
        await TerminalSentinelWriter.WriteAsync(room, sentinel, TestContext.Current.CancellationToken);

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<FleetStatusResponse>(result.Text)!.Rooms;
        Assert.NotNull(rooms);
        var singleRoom = Assert.Single(rooms!);
        Assert.Equal("terminal-room", singleRoom.Name);
        Assert.Equal("Succeeded", singleRoom.State);
        Assert.NotNull(singleRoom.Steps);
        var singleStep = Assert.Single(singleRoom.Steps!);
        Assert.Equal("step-a", singleStep.Id);
        Assert.Equal("Succeeded", singleStep.State);
        Assert.Equal("exec-1", singleStep.Execution);
        Assert.Null(singleStep.Timestamp);
        Assert.Equal(["/tmp/out.txt"], singleRoom.Outputs);
        Assert.Null(singleRoom.Error);
    }

    /// <summary>
    /// #734: `delivery` surfaces the room's latest journaled `FlowEvent.Delivery*` fact even though
    /// the room's own workflow is Terminal -- the poller keeps tracking the PR after the room's own
    /// DAG finishes, so this must read `flow.jsonl` rather than trust the frozen terminal sentinel.
    /// </summary>
    [Fact]
    public async Task TerminalRoom_SurfacesTheLatestDeliveryFact_FromFlowJsonlNotTheFrozenSentinel()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "delivery-room");
        Directory.CreateDirectory(room);

        var prPath = Path.Combine(room, DeliveryReferenceOutputNames.PullRequest);
        await File.WriteAllTextAsync(prPath, "99", TestContext.Current.CancellationToken);

        var sentinel = new WorkflowStatusView("Succeeded", [], [prPath], null, null);
        await TerminalSentinelWriter.WriteAsync(room, sentinel, TestContext.Current.CancellationToken);

        var logPath = Path.Combine(room, BatonPaths.FlowLogFileName);
        await using (var writer = new FlowEventLogWriter(logPath))
        {
            await writer.AppendAsync(new FlowEvent.DeliveryPrOpened(99, "734-lane"), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new FlowEvent.DeliveryChecksGreen(99), TestContext.Current.CancellationToken);
        }

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<FleetStatusResponse>(result.Text)!.Rooms;
        var singleRoom = Assert.Single(rooms!);
        Assert.NotNull(singleRoom.Delivery);
        Assert.Equal(99, singleRoom.Delivery!.Pr);
        Assert.Equal("ChecksGreen", singleRoom.Delivery.State);
    }

    /// <summary>The control: a room with no declared delivery output surfaces no `delivery` field at all.</summary>
    [Fact]
    public async Task ARoomWithNoDeclaredDeliveryOutput_SurfacesNoDeliveryField()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "no-delivery-room");
        Directory.CreateDirectory(room);

        var sentinel = new WorkflowStatusView("Succeeded", [], ["/tmp/plan.md"], null, null);
        await TerminalSentinelWriter.WriteAsync(room, sentinel, TestContext.Current.CancellationToken);

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        var rooms = JsonSerializer.Deserialize<FleetStatusResponse>(result.Text)!.Rooms;
        Assert.Null(Assert.Single(rooms!).Delivery);
    }

    [Fact]
    public async Task TerminalFastPath_PassesThroughAnIndeterminateSentinelVerbatim()
    {
        // #1586 S1: WorkflowOutcome.Indeterminate's own remarks explain why this fabricates the shape
        // directly rather than deriving it. The terminal fast path copies sentinel.State verbatim
        // (FleetStatusTool.ProcessRoomAsync, never re-deriving it via WorkflowOutcome.Describe), so
        // this proves the glass-facing pipeline round-trips the value rather than dropping or renaming
        // it.
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "indeterminate-room");
        Directory.CreateDirectory(room);

        var sentinel = new WorkflowStatusView("Indeterminate", [], [], null, null);
        await TerminalSentinelWriter.WriteAsync(room, sentinel, TestContext.Current.CancellationToken);

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<FleetStatusResponse>(result.Text)!.Rooms;
        var singleRoom = Assert.Single(rooms!);
        Assert.Equal("Indeterminate", singleRoom.State);
    }

    [Fact]
    public async Task ActiveRoom_ProjectsFromSnapshotAndEvents()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "active-room");
        Directory.CreateDirectory(room);

        var stepDef = new WorkflowStepDefinition(new StepId("step-active"), "agent-worker", [], ["plan.md"], [], new RetryPolicy(1));
        var def = new WorkflowDefinition(
            new WorkflowTemplateId("active-wf"),
            1,
            [stepDef]);
        var snapshot = SnapshotBinder.Bind(def);
        var snapshotPath = Path.Combine(room, "snapshot.json");
        await SnapshotBinder.PersistAsync(snapshot, snapshotPath, TestContext.Current.CancellationToken);

        var logPath = Path.Combine(room, "flow.jsonl");
        var writer = new FlowEventLogWriter(logPath);
        var execId = new ExecutionId("exec-active-1");
        var req = new ExecutionRequest(
            execId,
            new WorkflowId("active-wf"),
            stepDef.StepId,
            stepDef.Worker,
            [],
            [],
            TimeSpan.FromSeconds(30),
            [],
            new Dictionary<StepId, ExecutionId>());

        await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(req), TestContext.Current.CancellationToken);
        await writer.AppendAsync(new CoreEvent.ExecutionStarted(execId, Pid: 4242), TestContext.Current.CancellationToken);
        await writer.DisposeAsync();

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<FleetStatusResponse>(result.Text)!.Rooms;
        Assert.NotNull(rooms);
        var singleRoom = Assert.Single(rooms!);
        Assert.Equal("active-room", singleRoom.Name);
        Assert.Equal("Running", singleRoom.State);
        Assert.NotNull(singleRoom.Steps);
        var singleStep = Assert.Single(singleRoom.Steps!);
        Assert.Equal("step-active", singleStep.Id);
        Assert.Equal("Running", singleStep.State);
        Assert.Equal("exec-active-1", singleStep.Execution);
        Assert.NotNull(singleStep.Timestamp);
        // #1522: attempt is derived from lifetime execution count (1 on first execution), while
        // failure fields (failureKind, retryEligible) stay omitted for a step that hasn't failed.
        Assert.Equal(1, singleStep.Attempt);
        Assert.Equal(1, singleStep.MaxAttempts);
        Assert.Null(singleStep.FailureKind);
        Assert.Null(singleStep.RetryEligible);
        var wire = JsonSerializer.Serialize(singleRoom);
        Assert.Contains("\"attempt\":1", wire);
        Assert.Contains("\"maxAttempts\":1", wire);
        Assert.DoesNotContain("\"failureKind\"", wire);
        Assert.DoesNotContain("\"retryEligible\"", wire);
    }

    [Fact]
    public async Task ExhaustedUntilFailure_SurfacesCorrectAttemptOrdinal()
    {
        // #1522: StateProjector persists a lifetime execution counter incremented on every
        // ExecutionRequestAccepted, so an ExhaustedUntil failure correctly renders its execution
        // ordinal (here, attempt 1 of 3) even though ConsecutiveFailureCount is not incremented.
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "exhausted-room");
        Directory.CreateDirectory(room);

        var stepDef = new WorkflowStepDefinition(new StepId("step-exhausted"), "agent-worker", [], ["plan.md"], [], new RetryPolicy(3));
        var def = new WorkflowDefinition(new WorkflowTemplateId("exhausted-wf"), 1, [stepDef]);
        var snapshot = SnapshotBinder.Bind(def);
        await SnapshotBinder.PersistAsync(snapshot, Path.Combine(room, "snapshot.json"), TestContext.Current.CancellationToken);

        var logPath = Path.Combine(room, "flow.jsonl");
        var writer = new FlowEventLogWriter(logPath);
        var execId = new ExecutionId("exec-exhausted-1");
        var req = new ExecutionRequest(
            execId, new WorkflowId("exhausted-wf"), stepDef.StepId, stepDef.Worker,
            [], [], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId>());

        await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(req), TestContext.Current.CancellationToken);
        await writer.AppendAsync(
            new FlowEvent.ExecutionFailed(execId, FailureClassification.ExhaustedUntil, "quota exhausted"),
            TestContext.Current.CancellationToken);
        await writer.DisposeAsync();

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<FleetStatusResponse>(result.Text)!.Rooms;
        var singleStep = Assert.Single(Assert.Single(rooms!).Steps!);
        Assert.Equal("ExhaustedUntil", singleStep.FailureKind);
        Assert.True(singleStep.RetryEligible);
        Assert.Equal(1, singleStep.Attempt);
        Assert.Equal(3, singleStep.MaxAttempts);
        // #1551: no StepRetryScheduled was recorded here -- an un-obligated ExhaustedUntil park
        // ("reset unknown" on the human path) must not fabricate a reset instant.
        Assert.Null(singleStep.ExhaustedUntil);
    }

    [Fact]
    public async Task ExhaustedUntilFailure_WithRecordedObligation_SurfacesResetInstant()
    {
        // #1551: the reset instant a StepRetryScheduled actually recorded reaches fleet_status
        // verbatim, through the active-room projection path (not the terminal sentinel).
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "exhausted-parked-room");
        Directory.CreateDirectory(room);

        var stepDef = new WorkflowStepDefinition(new StepId("step-parked"), "agent-worker", [], ["plan.md"], [], new RetryPolicy(3));
        var def = new WorkflowDefinition(new WorkflowTemplateId("exhausted-parked-wf"), 1, [stepDef]);
        var snapshot = SnapshotBinder.Bind(def);
        await SnapshotBinder.PersistAsync(snapshot, Path.Combine(room, "snapshot.json"), TestContext.Current.CancellationToken);

        var logPath = Path.Combine(room, "flow.jsonl");
        var writer = new FlowEventLogWriter(logPath);
        var execId = new ExecutionId("exec-parked-1");
        var req = new ExecutionRequest(
            execId, new WorkflowId("exhausted-parked-wf"), stepDef.StepId, stepDef.Worker,
            [], [], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId>());
        var resetInstant = new DateTimeOffset(2026, 9, 1, 21, 59, 0, TimeSpan.Zero);

        await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(req), TestContext.Current.CancellationToken);
        await writer.AppendAsync(
            new FlowEvent.ExecutionFailed(execId, FailureClassification.ExhaustedUntil, "quota exhausted", RetryNotBefore: resetInstant),
            TestContext.Current.CancellationToken);
        await writer.AppendAsync(
            new FlowEvent.StepRetryScheduled(stepDef.StepId, execId, resetInstant, RetryDelayMs: 0),
            TestContext.Current.CancellationToken);
        await writer.DisposeAsync();

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<FleetStatusResponse>(result.Text)!.Rooms;
        var singleStep = Assert.Single(Assert.Single(rooms!).Steps!);
        Assert.Equal("ExhaustedUntil", singleStep.FailureKind);
        Assert.Equal(resetInstant.ToString("O"), singleStep.ExhaustedUntil);
    }

    [Fact]
    public async Task RetryWithRevision_MaintainsLifetimeExecutionOrdinalAcrossResume()
    {
        // #1522: Issue #1509 named failure mode: a step that failed twice, was revised via
        // RetryWithRevision, and is now on its 3rd real execution must surface attempt 3 of 3
        // (rather than resetting to attempt 1 or null).
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "revision-retry-room");
        Directory.CreateDirectory(room);

        var stepDef = new WorkflowStepDefinition(new StepId("step-revision"), "agent-worker", [], ["plan.md"], [], new RetryPolicy(3));
        var def = new WorkflowDefinition(new WorkflowTemplateId("revision-wf"), 1, [stepDef]);
        var snapshot = SnapshotBinder.Bind(def);
        await SnapshotBinder.PersistAsync(snapshot, Path.Combine(room, "snapshot.json"), TestContext.Current.CancellationToken);

        var logPath = Path.Combine(room, "flow.jsonl");
        var writer = new FlowEventLogWriter(logPath);

        // Attempt 1: accepted and failed
        var firstExecId = new ExecutionId("exec-rev-1");
        var firstReq = new ExecutionRequest(
            firstExecId, new WorkflowId("revision-wf"), stepDef.StepId, stepDef.Worker,
            [], [], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId>());
        await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(firstReq), TestContext.Current.CancellationToken);
        await writer.AppendAsync(
            new FlowEvent.ExecutionFailed(firstExecId, FailureClassification.Retryable, "first crash"),
            TestContext.Current.CancellationToken);

        // Attempt 2: accepted, failed, and paused
        var secondExecId = new ExecutionId("exec-rev-2");
        var secondReq = firstReq with { ExecutionId = secondExecId };
        await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(secondReq), TestContext.Current.CancellationToken);
        await writer.AppendAsync(
            new FlowEvent.ExecutionFailed(secondExecId, FailureClassification.Retryable, "second crash"),
            TestContext.Current.CancellationToken);
        await writer.AppendAsync(new FlowEvent.WorkflowPaused(secondExecId, stepDef.StepId), TestContext.Current.CancellationToken);

        // Operator decision: RetryWithRevision
        var decId = new DecisionId("dec-rev-1");
        await writer.AppendAsync(
            new FlowEvent.ExternalDecisionRecorded(decId, secondExecId, DecisionType.RetryWithRevision, null, null),
            TestContext.Current.CancellationToken);
        await writer.AppendAsync(new FlowEvent.WorkflowResumed(decId), TestContext.Current.CancellationToken);

        // Attempt 3: accepted (running)
        var thirdExecId = new ExecutionId("exec-rev-3");
        var thirdReq = firstReq with { ExecutionId = thirdExecId };
        await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(thirdReq), TestContext.Current.CancellationToken);
        await writer.DisposeAsync();

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<FleetStatusResponse>(result.Text)!.Rooms;
        var singleStep = Assert.Single(Assert.Single(rooms!).Steps!);
        Assert.Equal("Running", singleStep.State);
        Assert.Equal("exec-rev-3", singleStep.Execution);
        Assert.Equal(3, singleStep.Attempt);
        Assert.Equal(3, singleStep.MaxAttempts);
    }

    [Fact]
    public async Task FailedStep_SurfacesFailureKindAndRetryEligible_FromEngineClassification()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "failed-room");
        Directory.CreateDirectory(room);

        var stepDef = new WorkflowStepDefinition(new StepId("step-failed"), "agent-worker", [], ["plan.md"], [], new RetryPolicy(3));
        var def = new WorkflowDefinition(new WorkflowTemplateId("failed-wf"), 1, [stepDef]);
        var snapshot = SnapshotBinder.Bind(def);
        await SnapshotBinder.PersistAsync(snapshot, Path.Combine(room, "snapshot.json"), TestContext.Current.CancellationToken);

        var logPath = Path.Combine(room, "flow.jsonl");
        var writer = new FlowEventLogWriter(logPath);
        var execId = new ExecutionId("exec-failed-1");
        var req = new ExecutionRequest(
            execId, new WorkflowId("failed-wf"), stepDef.StepId, stepDef.Worker,
            [], [], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId>());

        await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(req), TestContext.Current.CancellationToken);
        await writer.AppendAsync(
            new FlowEvent.ExecutionFailed(execId, FailureClassification.Retryable, "worker crashed"),
            TestContext.Current.CancellationToken);
        await writer.DisposeAsync();

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<FleetStatusResponse>(result.Text)!.Rooms;
        var singleStep = Assert.Single(Assert.Single(rooms!).Steps!);
        Assert.Equal("Failed", singleStep.State);
        // One consecutive failure recorded -> this failed execution WAS attempt 1, out of 3 allowed.
        Assert.Equal(1, singleStep.Attempt);
        Assert.Equal(3, singleStep.MaxAttempts);
        Assert.Equal("Retryable", singleStep.FailureKind);
        Assert.True(singleStep.RetryEligible);
    }

    [Fact]
    public async Task PermanentFailure_SurfacesRetryEligibleFalse()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "permanent-fail-room");
        Directory.CreateDirectory(room);

        var stepDef = new WorkflowStepDefinition(new StepId("step-permanent"), "agent-worker", [], ["plan.md"], [], new RetryPolicy(3));
        var def = new WorkflowDefinition(new WorkflowTemplateId("permanent-wf"), 1, [stepDef]);
        var snapshot = SnapshotBinder.Bind(def);
        await SnapshotBinder.PersistAsync(snapshot, Path.Combine(room, "snapshot.json"), TestContext.Current.CancellationToken);

        var logPath = Path.Combine(room, "flow.jsonl");
        var writer = new FlowEventLogWriter(logPath);
        var execId = new ExecutionId("exec-permanent-1");
        var req = new ExecutionRequest(
            execId, new WorkflowId("permanent-wf"), stepDef.StepId, stepDef.Worker,
            [], [], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId>());

        await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(req), TestContext.Current.CancellationToken);
        await writer.AppendAsync(
            new FlowEvent.ExecutionFailed(execId, FailureClassification.Permanent, "invalid config"),
            TestContext.Current.CancellationToken);
        await writer.DisposeAsync();

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<FleetStatusResponse>(result.Text)!.Rooms;
        var singleStep = Assert.Single(Assert.Single(rooms!).Steps!);
        Assert.Equal("Permanent", singleStep.FailureKind);
        Assert.False(singleStep.RetryEligible);
    }

    [Fact]
    public async Task RunningStep_SurfacesAttemptOrdinalAfterAPriorFailure()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "retrying-room");
        Directory.CreateDirectory(room);

        var stepDef = new WorkflowStepDefinition(new StepId("step-retrying"), "agent-worker", [], ["plan.md"], [], new RetryPolicy(3));
        var def = new WorkflowDefinition(new WorkflowTemplateId("retrying-wf"), 1, [stepDef]);
        var snapshot = SnapshotBinder.Bind(def);
        await SnapshotBinder.PersistAsync(snapshot, Path.Combine(room, "snapshot.json"), TestContext.Current.CancellationToken);

        var logPath = Path.Combine(room, "flow.jsonl");
        var writer = new FlowEventLogWriter(logPath);
        var firstExecId = new ExecutionId("exec-retrying-1");
        var firstReq = new ExecutionRequest(
            firstExecId, new WorkflowId("retrying-wf"), stepDef.StepId, stepDef.Worker,
            [], [], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId>());
        await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(firstReq), TestContext.Current.CancellationToken);
        await writer.AppendAsync(
            new FlowEvent.ExecutionFailed(firstExecId, FailureClassification.Retryable, "transient"),
            TestContext.Current.CancellationToken);

        var secondExecId = new ExecutionId("exec-retrying-2");
        var secondReq = firstReq with { ExecutionId = secondExecId };
        await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(secondReq), TestContext.Current.CancellationToken);
        await writer.DisposeAsync();

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<FleetStatusResponse>(result.Text)!.Rooms;
        var singleStep = Assert.Single(Assert.Single(rooms!).Steps!);
        Assert.Equal("Running", singleStep.State);
        Assert.Equal("exec-retrying-2", singleStep.Execution);
        // One prior consecutive failure -> this running execution is attempt 2 of 3.
        Assert.Equal(2, singleStep.Attempt);
        Assert.Equal(3, singleStep.MaxAttempts);
        Assert.Null(singleStep.FailureKind);
        Assert.Null(singleStep.RetryEligible);
    }

    [Fact]
    public async Task TerminalSentinel_CarriesAttemptAndFailureKindThroughVerbatim()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "sentinel-retry-room");
        Directory.CreateDirectory(room);

        var step = new WorkflowStatusStepView(
            "step-a", "Failed", "exec-1", null, null, null, null, Attempt: 2, MaxAttempts: 3,
            FailureKind: "Permanent", RetryEligible: false);
        var sentinel = new WorkflowStatusView("Failed", [step], [], "invalid config", null);
        await TerminalSentinelWriter.WriteAsync(room, sentinel, TestContext.Current.CancellationToken);

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<FleetStatusResponse>(result.Text)!.Rooms;
        var singleStep = Assert.Single(Assert.Single(rooms!).Steps!);
        Assert.Equal(2, singleStep.Attempt);
        Assert.Equal(3, singleStep.MaxAttempts);
        Assert.Equal("Permanent", singleStep.FailureKind);
        Assert.False(singleStep.RetryEligible);
    }

    [Fact]
    public async Task TerminalSentinel_CarriesExhaustedUntilThroughVerbatim()
    {
        // #1551: pins the fast path's copy-through of a frozen ExhaustedUntil sentinel step's
        // recorded reset instant, same as Attempt/FailureKind, never re-derived. #1598 review F4:
        // the shape this once described (a sibling Permanent failure alongside a still-parked step)
        // does not arise today -- StateProjector keeps any step still carrying a RetryNotBefore
        // eligible, so the room stays Running rather than going terminal around it. This test still
        // guards the #1590/#1597 divergence class (a sentinel copying a field through unchanged vs.
        // re-deriving it), independent of whether that particular terminal shape is reachable.
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "sentinel-exhausted-room");
        Directory.CreateDirectory(room);

        var resetInstant = new DateTimeOffset(2026, 9, 1, 21, 59, 0, TimeSpan.Zero);
        var step = new WorkflowStatusStepView(
            "step-a", "Failed", "exec-1", null, null, null, null, Attempt: 1, MaxAttempts: 3,
            FailureKind: "ExhaustedUntil", RetryEligible: true, ExhaustedUntil: resetInstant.ToString("O"));
        var sentinel = new WorkflowStatusView("Failed", [step], [], "quota exhausted", null);
        await TerminalSentinelWriter.WriteAsync(room, sentinel, TestContext.Current.CancellationToken);

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<FleetStatusResponse>(result.Text)!.Rooms;
        var singleStep = Assert.Single(Assert.Single(rooms!).Steps!);
        Assert.Equal("ExhaustedUntil", singleStep.FailureKind);
        Assert.Equal(resetInstant.ToString("O"), singleStep.ExhaustedUntil);
    }

    /// <summary>
    /// #1462: `fleet_status` must inherit `WorkflowStatusStepView.Liveness` off the SAME
    /// <see cref="WorkflowStatusProjector"/> projection `status --json` reads (spec/baton.md §3/§6) --
    /// never a second <see cref="Baton.Outcomes.EngineLivenessProbe"/> call. A fleet caller reading a
    /// "Running" step whose engine was SIGKILLed must be able to tell a dead engine from a merely slow
    /// one without a second, per-room `status --json` call.
    /// </summary>
    [Fact]
    public async Task ActiveRoom_RunningStepWithDeadEngine_ReportsDeadLivenessThroughFleetStatus()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "dead-engine-room");
        Directory.CreateDirectory(room);

        var stepDef = new WorkflowStepDefinition(new StepId("step-dead"), "agent-worker", [], [], [], new RetryPolicy(1));
        var def = new WorkflowDefinition(new WorkflowTemplateId("dead-wf"), 1, [stepDef]);
        var snapshot = SnapshotBinder.Bind(def);
        var snapshotPath = Path.Combine(room, "snapshot.json");
        await SnapshotBinder.PersistAsync(snapshot, snapshotPath, TestContext.Current.CancellationToken);

        var (deadPid, deadStartTime) = DeadProcessIdentity();

        var logPath = Path.Combine(room, "flow.jsonl");
        var writer = new FlowEventLogWriter(logPath);
        var execId = new ExecutionId("exec-dead-1");
        var req = new ExecutionRequest(
            execId,
            new WorkflowId("dead-wf"),
            stepDef.StepId,
            stepDef.Worker,
            [],
            [],
            TimeSpan.FromSeconds(30),
            [],
            new Dictionary<StepId, ExecutionId>());

        await writer.AppendAsync(
            new FlowEvent.ExecutionRequestAccepted(req, EnginePid: deadPid, EngineStartTime: deadStartTime),
            TestContext.Current.CancellationToken);
        await writer.DisposeAsync();

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<FleetStatusResponse>(result.Text)!.Rooms;
        Assert.NotNull(rooms);
        var singleRoom = Assert.Single(rooms!);
        // #1513: superseding the pre-#1513 expectation here (room-level State stayed "Running", with
        // only the per-step Liveness naming the dead engine). #1513's whole complaint is that this
        // room reads RUNNING forever on the fleet view with nothing behind it -- the per-step signal
        // alone was never enough for an operator glancing at the room list, only for a caller already
        // reading Steps[].Liveness. The room-level State is now downgraded whenever nothing keeping it
        // non-terminal is confirmed alive; the step's own State token is untouched (still "Running" --
        // this is a display-layer override of FleetRoomStatusView.State only, never StepStatus).
        Assert.Equal("Stalled", singleRoom.State);
        var singleStep = Assert.Single(singleRoom.Steps!);
        Assert.Equal("Running", singleStep.State);
        Assert.Equal("dead", singleStep.Liveness);
    }

    /// <summary>
    /// Polarity arm for the same #1462 fix, opposite direction: a step whose engine is genuinely
    /// alive must read "alive" (or be omitted entirely once non-Running), never silently coincide
    /// with the "dead" arm above -- proving `fleet_status` carries the probe's actual verdict rather
    /// than a hardcoded string.
    /// </summary>
    [Fact]
    public async Task ActiveRoom_RunningStepWithAliveEngine_ReportsAliveLivenessThroughFleetStatus()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "alive-engine-room");
        Directory.CreateDirectory(room);

        var stepDef = new WorkflowStepDefinition(new StepId("step-alive"), "agent-worker", [], [], [], new RetryPolicy(1));
        var def = new WorkflowDefinition(new WorkflowTemplateId("alive-wf"), 1, [stepDef]);
        var snapshot = SnapshotBinder.Bind(def);
        var snapshotPath = Path.Combine(room, "snapshot.json");
        await SnapshotBinder.PersistAsync(snapshot, snapshotPath, TestContext.Current.CancellationToken);

        var livePid = Environment.ProcessId;
        var liveStartTime = new DateTimeOffset(Process.GetCurrentProcess().StartTime).ToUniversalTime();

        var logPath = Path.Combine(room, "flow.jsonl");
        var writer = new FlowEventLogWriter(logPath);
        var execId = new ExecutionId("exec-alive-1");
        var req = new ExecutionRequest(
            execId,
            new WorkflowId("alive-wf"),
            stepDef.StepId,
            stepDef.Worker,
            [],
            [],
            TimeSpan.FromSeconds(30),
            [],
            new Dictionary<StepId, ExecutionId>());

        await writer.AppendAsync(
            new FlowEvent.ExecutionRequestAccepted(req, EnginePid: livePid, EngineStartTime: liveStartTime),
            TestContext.Current.CancellationToken);
        await writer.DisposeAsync();

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<FleetStatusResponse>(result.Text)!.Rooms;
        Assert.NotNull(rooms);
        var singleRoom = Assert.Single(rooms!);
        // #1513 polarity: a genuinely alive engine must never trip the Stalled downgrade.
        Assert.Equal("Running", singleRoom.State);
        var singleStep = Assert.Single(singleRoom.Steps!);
        Assert.Equal("alive", singleStep.Liveness);
    }

    /// <summary>
    /// #1513: the signature this issue actually reports live -- a Failed step still carrying a
    /// RetryNotBefore (a scheduled backoff/retry), whose engine is confirmed dead. Unlike the Running
    /// case above, NOTHING probed this state before #1513: `WorkflowStatusProjector.Project`'s
    /// liveness gate covered only Running steps, so a room stuck exactly like this reported no
    /// liveness signal at all and read as plain "Running" with no way to tell it apart from a healthy
    /// paced backoff. Why that pump dying is fatal to the retry: spec/baton.md §7.
    /// </summary>
    [Fact]
    public async Task ActiveRoom_FailedStepWithPendingRetryAndDeadEngine_ProjectsAsStalledNotRunning()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "dead-pump-parked-retry-room");
        Directory.CreateDirectory(room);

        var stepDef = new WorkflowStepDefinition(new StepId("step-parked"), "agent-worker", [], [], [], new RetryPolicy(3));
        var def = new WorkflowDefinition(new WorkflowTemplateId("parked-wf"), 1, [stepDef]);
        var snapshot = SnapshotBinder.Bind(def);
        var snapshotPath = Path.Combine(room, "snapshot.json");
        await SnapshotBinder.PersistAsync(snapshot, snapshotPath, TestContext.Current.CancellationToken);

        var (deadPid, deadStartTime) = DeadProcessIdentity();

        var logPath = Path.Combine(room, "flow.jsonl");
        var writer = new FlowEventLogWriter(logPath);
        var execId = new ExecutionId("exec-parked-1");
        var req = new ExecutionRequest(
            execId,
            new WorkflowId("parked-wf"),
            stepDef.StepId,
            stepDef.Worker,
            [],
            [],
            TimeSpan.FromSeconds(30),
            [],
            new Dictionary<StepId, ExecutionId>());

        await writer.AppendAsync(
            new FlowEvent.ExecutionRequestAccepted(req, EnginePid: deadPid, EngineStartTime: deadStartTime),
            TestContext.Current.CancellationToken);
        await writer.AppendAsync(
            new CoreEvent.ExecutionStarted(execId, (uint)deadPid), TestContext.Current.CancellationToken);
        await writer.AppendAsync(
            new CoreEvent.ExecutionExited(execId, 1, CoreExitReason.Natural, null), TestContext.Current.CancellationToken);
        var retryNotBefore = DateTimeOffset.UtcNow.AddHours(4);
        await writer.AppendAsync(
            new FlowEvent.ExecutionFailed(execId, FailureClassification.Retryable, "Worker exited with non-zero code 1.", retryNotBefore),
            TestContext.Current.CancellationToken);
        await writer.AppendAsync(
            new FlowEvent.StepRetryScheduled(stepDef.StepId, execId, retryNotBefore, 14_400_000),
            TestContext.Current.CancellationToken);
        await writer.DisposeAsync();

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<FleetStatusResponse>(result.Text)!.Rooms;
        Assert.NotNull(rooms);
        var singleRoom = Assert.Single(rooms!);
        Assert.NotEqual("Running", singleRoom.State);
        Assert.Equal("Stalled", singleRoom.State);
        var singleStep = Assert.Single(singleRoom.Steps!);
        Assert.Equal("Failed", singleStep.State);
        Assert.Equal("dead", singleStep.Liveness);
    }

    /// <summary>
    /// #1582 review (MED-4): on a single-step room, `gated.All(dead)` and `!gated.Any(alive)` are
    /// indistinguishable -- every other test in this class is single-step, so none of them would
    /// catch <see cref="FleetStatusTool"/>'s predicate being written the wrong way. Two independent
    /// (no DependsOn) gated steps -- one confirmed dead, one "unknown" (no recorded engine identity,
    /// same shape <see cref="EngineLivenessProbeTests.Probe_failure_arm_returns_unknown_when_identity_is_missing_or_invalid"/>
    /// exercises directly) -- discriminates them: `All(dead)` correctly stays "Running" (one gated
    /// step is not confirmed dead), while `!Any(alive)` would wrongly read "Stalled" (neither gated
    /// step is confirmed alive). This is the exact false-`"Stalled"` the predicate's own comment says
    /// it exists to prevent.
    /// </summary>
    [Fact]
    public async Task ActiveRoom_OneDeadGatedStepAndOneUnknownGatedStep_StaysRunning()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "dead-plus-unknown-gated-steps-room");
        Directory.CreateDirectory(room);

        var deadStepDef = new WorkflowStepDefinition(new StepId("step-dead"), "agent-worker", [], [], [], new RetryPolicy(3));
        var unknownStepDef = new WorkflowStepDefinition(new StepId("step-unknown"), "agent-worker", [], [], [], new RetryPolicy(3));
        var def = new WorkflowDefinition(new WorkflowTemplateId("dead-plus-unknown-wf"), 1, [deadStepDef, unknownStepDef]);
        var snapshot = SnapshotBinder.Bind(def);
        var snapshotPath = Path.Combine(room, "snapshot.json");
        await SnapshotBinder.PersistAsync(snapshot, snapshotPath, TestContext.Current.CancellationToken);

        var (deadPid, deadStartTime) = DeadProcessIdentity();

        var logPath = Path.Combine(room, "flow.jsonl");
        var writer = new FlowEventLogWriter(logPath);

        var deadExecId = new ExecutionId("exec-dead-1");
        var deadReq = new ExecutionRequest(
            deadExecId, new WorkflowId("dead-plus-unknown-wf"), deadStepDef.StepId, deadStepDef.Worker,
            [], [], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId>());
        await writer.AppendAsync(
            new FlowEvent.ExecutionRequestAccepted(deadReq, EnginePid: deadPid, EngineStartTime: deadStartTime),
            TestContext.Current.CancellationToken);
        await writer.AppendAsync(
            new CoreEvent.ExecutionStarted(deadExecId, (uint)deadPid), TestContext.Current.CancellationToken);
        await writer.AppendAsync(
            new CoreEvent.ExecutionExited(deadExecId, 1, CoreExitReason.Natural, null), TestContext.Current.CancellationToken);
        var deadRetryNotBefore = DateTimeOffset.UtcNow.AddHours(4);
        await writer.AppendAsync(
            new FlowEvent.ExecutionFailed(deadExecId, FailureClassification.Retryable, "Worker exited with non-zero code 1.", deadRetryNotBefore),
            TestContext.Current.CancellationToken);
        await writer.AppendAsync(
            new FlowEvent.StepRetryScheduled(deadStepDef.StepId, deadExecId, deadRetryNotBefore, 14_400_000),
            TestContext.Current.CancellationToken);

        // No EnginePid/EngineStartTime recorded -- the pre-#1375-ledger shape EngineLivenessProbe
        // reports "unknown" for (missing identity), not "dead".
        var unknownExecId = new ExecutionId("exec-unknown-1");
        var unknownReq = new ExecutionRequest(
            unknownExecId, new WorkflowId("dead-plus-unknown-wf"), unknownStepDef.StepId, unknownStepDef.Worker,
            [], [], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId>());
        await writer.AppendAsync(
            new FlowEvent.ExecutionRequestAccepted(unknownReq, EnginePid: null, EngineStartTime: null),
            TestContext.Current.CancellationToken);
        await writer.AppendAsync(
            new CoreEvent.ExecutionStarted(unknownExecId, 999999), TestContext.Current.CancellationToken);
        await writer.AppendAsync(
            new CoreEvent.ExecutionExited(unknownExecId, 1, CoreExitReason.Natural, null), TestContext.Current.CancellationToken);
        var unknownRetryNotBefore = DateTimeOffset.UtcNow.AddHours(4);
        await writer.AppendAsync(
            new FlowEvent.ExecutionFailed(unknownExecId, FailureClassification.Retryable, "Worker exited with non-zero code 1.", unknownRetryNotBefore),
            TestContext.Current.CancellationToken);
        await writer.AppendAsync(
            new FlowEvent.StepRetryScheduled(unknownStepDef.StepId, unknownExecId, unknownRetryNotBefore, 14_400_000),
            TestContext.Current.CancellationToken);
        await writer.DisposeAsync();

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<FleetStatusResponse>(result.Text)!.Rooms;
        Assert.NotNull(rooms);
        var singleRoom = Assert.Single(rooms!);
        Assert.Equal("Running", singleRoom.State);
        Assert.Equal(2, singleRoom.Steps!.Count);
        Assert.Equal("dead", singleRoom.Steps!.Single(s => s.Id == "step-dead").Liveness);
        Assert.Equal("unknown", singleRoom.Steps!.Single(s => s.Id == "step-unknown").Liveness);
    }

    /// <summary>
    /// #1513 polarity, opposite direction: the identical parked-retry shape, but the engine that
    /// scheduled it is genuinely alive -- an ordinary healthy backoff must keep reading "Running", or
    /// the fix would just be trading one false reading for another (every paced retry, alive or not,
    /// misreported as stuck).
    /// </summary>
    [Fact]
    public async Task ActiveRoom_FailedStepWithPendingRetryAndAliveEngine_StaysRunning()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "alive-pump-parked-retry-room");
        Directory.CreateDirectory(room);

        var stepDef = new WorkflowStepDefinition(new StepId("step-parked"), "agent-worker", [], [], [], new RetryPolicy(3));
        var def = new WorkflowDefinition(new WorkflowTemplateId("parked-wf"), 1, [stepDef]);
        var snapshot = SnapshotBinder.Bind(def);
        var snapshotPath = Path.Combine(room, "snapshot.json");
        await SnapshotBinder.PersistAsync(snapshot, snapshotPath, TestContext.Current.CancellationToken);

        var livePid = Environment.ProcessId;
        var liveStartTime = new DateTimeOffset(Process.GetCurrentProcess().StartTime).ToUniversalTime();

        var logPath = Path.Combine(room, "flow.jsonl");
        var writer = new FlowEventLogWriter(logPath);
        var execId = new ExecutionId("exec-parked-alive-1");
        var req = new ExecutionRequest(
            execId,
            new WorkflowId("parked-wf"),
            stepDef.StepId,
            stepDef.Worker,
            [],
            [],
            TimeSpan.FromSeconds(30),
            [],
            new Dictionary<StepId, ExecutionId>());

        await writer.AppendAsync(
            new FlowEvent.ExecutionRequestAccepted(req, EnginePid: livePid, EngineStartTime: liveStartTime),
            TestContext.Current.CancellationToken);
        await writer.AppendAsync(
            new CoreEvent.ExecutionStarted(execId, (uint)livePid), TestContext.Current.CancellationToken);
        await writer.AppendAsync(
            new CoreEvent.ExecutionExited(execId, 1, CoreExitReason.Natural, null), TestContext.Current.CancellationToken);
        var retryNotBefore = DateTimeOffset.UtcNow.AddHours(4);
        await writer.AppendAsync(
            new FlowEvent.ExecutionFailed(execId, FailureClassification.Retryable, "Worker exited with non-zero code 1.", retryNotBefore),
            TestContext.Current.CancellationToken);
        await writer.AppendAsync(
            new FlowEvent.StepRetryScheduled(stepDef.StepId, execId, retryNotBefore, 14_400_000),
            TestContext.Current.CancellationToken);
        await writer.DisposeAsync();

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<FleetStatusResponse>(result.Text)!.Rooms;
        Assert.NotNull(rooms);
        var singleRoom = Assert.Single(rooms!);
        Assert.Equal("Running", singleRoom.State);
        var singleStep = Assert.Single(singleRoom.Steps!);
        Assert.Equal("alive", singleStep.Liveness);
    }

    /// <summary>
    /// #1513 `right-instrument`: the claim under test is about what an operator sees projected off a
    /// REAL room this bug was caught in, not only a synthetic fixture. Copies one of four live
    /// operator-killed-pump rooms this fix was verified against (`dispatch-implement-a0c38801` --
    /// `flow.jsonl` ends in `stepRetryScheduled` with no `terminal.json`; the room #1513's own body
    /// names, `dispatch-implement-2c5dcd8d`, is NOT this shape -- its engine was in fact still alive
    /// and it finished naturally, `terminal.json: Succeeded`, so it is deliberately not used as a
    /// positive fixture here) into an isolated fleet root read-only (never mutates the original room)
    /// and asserts the fix changes what an operator actually sees for it. Skips (not silently passes)
    /// if this machine does not have that room under `~/.baton/rooms` -- the room is local live
    /// evidence, not a checked-in fixture.
    /// </summary>
    /// <remarks>
    /// #1582 review (MED-3): this used to skip on directory existence alone, which hard-FAILS (not
    /// skips) the instant the operator recovers the room per spec/baton.md §3's own recovery path --
    /// the directory still exists, now with a terminal.json, so `Assert.Equal("Stalled", ...)` below
    /// would fail against a room that has since gone `Succeeded`. Guarding on shape instead (no
    /// terminal.json, and the one step is still Failed with a liveness verdict recorded) makes
    /// recovery a skip, not a red suite.
    /// </remarks>
    [Fact]
    public async Task ActiveRoom_RealZombieRoomFromIssue1513_ProjectsAsStalledNotRunning()
    {
        var realRoomsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".baton", "rooms");
        var sourceRoom = Path.Combine(realRoomsDir, "dispatch-implement-a0c38801");
        if (!Directory.Exists(sourceRoom))
        {
            Assert.Skip("this machine has no ~/.baton/rooms/dispatch-implement-a0c38801 -- local live evidence, not a checked-in fixture");
        }

        if (File.Exists(Path.Combine(sourceRoom, TerminalSentinelWriter.TerminalSentinelFileName)))
        {
            Assert.Skip("dispatch-implement-a0c38801 has since reached a terminal state (recovered, e.g. via a fresh " +
                "`baton run` per spec/baton.md §3) -- no longer the parked-retry-with-dead-engine shape this test targets");
        }

        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var copiedRoom = Path.Combine(defaultRoomsDir, "dispatch-implement-a0c38801");
        Directory.CreateDirectory(defaultRoomsDir);
        CopyRoomReadOnly(sourceRoom, copiedRoom);

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<FleetStatusResponse>(result.Text)!.Rooms;
        Assert.NotNull(rooms);
        var singleRoom = Assert.Single(rooms!);
        var singleStep = Assert.Single(singleRoom.Steps!);
        if (singleStep.State != "Failed" || singleStep.Liveness is null)
        {
            Assert.Skip("dispatch-implement-a0c38801's step no longer reads Failed-with-a-liveness-verdict -- " +
                "no longer the parked-retry-with-dead-engine shape this test targets");
        }

        Assert.NotEqual("Running", singleRoom.State);
        Assert.Equal("Stalled", singleRoom.State);
    }

    private static void CopyRoomReadOnly(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            // artifacts/ can be large and is irrelevant to the projection under test; skip it.
            if (Path.GetFileName(dir) == "artifacts")
            {
                continue;
            }

            CopyRoomReadOnly(dir, Path.Combine(destDir, Path.GetFileName(dir)));
        }
    }

    /// <summary>
    /// #1462: `fleet_status` must inherit `WorkflowStatusView.Rejected` off the same projection
    /// `status --json` reads (spec/baton.md §3/§6) -- copied from the terminal sentinel, since the
    /// sentinel already IS a <see cref="WorkflowStatusView"/>. A rejected room must read distinctly
    /// from an ordinary crashed one: both settle as `"state": "Failed"`, and `rejected` is the only
    /// structural fact telling them apart.
    /// </summary>
    [Fact]
    public async Task TerminalSentinel_RejectedRoom_ReportsRejectedTrue_DistinctFromOrdinaryFailure()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var rejectedRoom = Path.Combine(defaultRoomsDir, "rejected-room");
        var crashedRoom = Path.Combine(defaultRoomsDir, "crashed-room");
        Directory.CreateDirectory(rejectedRoom);
        Directory.CreateDirectory(crashedRoom);

        var rejectedSentinel = new WorkflowStatusView("Failed", [], [], "a step was rejected", null, Rejected: true);
        var crashedSentinel = new WorkflowStatusView("Failed", [], [], "the worker crashed", null, Rejected: false);
        await TerminalSentinelWriter.WriteAsync(rejectedRoom, rejectedSentinel, TestContext.Current.CancellationToken);
        await TerminalSentinelWriter.WriteAsync(crashedRoom, crashedSentinel, TestContext.Current.CancellationToken);

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<FleetStatusResponse>(result.Text)!.Rooms;
        Assert.NotNull(rooms);
        Assert.Equal(2, rooms!.Count);

        var rejected = rooms.First(r => r.Name == "rejected-room");
        Assert.Equal("Failed", rejected.State);
        Assert.True(rejected.Rejected);

        var crashed = rooms.First(r => r.Name == "crashed-room");
        Assert.Equal("Failed", crashed.State);
        Assert.False(crashed.Rejected);
        // Wire-level: a non-rejected room must OMIT the key, not emit "rejected": false -- the
        // omission rests on JsonIgnoreCondition.WhenWritingDefault, and only a serialized assertion
        // catches that attribute breaking.
        Assert.DoesNotContain("\"rejected\"", JsonSerializer.Serialize(crashed));
    }

    /// <summary>
    /// F10/F11 (#1720 review): a conductor `baton resolve --close` settles a room Failed with
    /// `resolvedBy: "conductor"` and `rejected` unset (spec/baton.md §3), so without mirroring
    /// `ResolvedBy` the glass could not tell that room from an ordinary crash at all. Three rooms one
    /// field apart, which is what makes this discriminate rather than merely pass.
    /// </summary>
    [Fact]
    public async Task TerminalSentinel_ConductorClosedRoom_ReportsResolvedBy_WithoutRejected()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var closedRoom = Path.Combine(defaultRoomsDir, "closed-room");
        var rejectedRoom = Path.Combine(defaultRoomsDir, "conductor-rejected-room");
        var crashedRoom = Path.Combine(defaultRoomsDir, "plain-crashed-room");
        Directory.CreateDirectory(closedRoom);
        Directory.CreateDirectory(rejectedRoom);
        Directory.CreateDirectory(crashedRoom);

        await TerminalSentinelWriter.WriteAsync(
            closedRoom,
            new WorkflowStatusView("Failed", [], [], "Resolved by the conductor: overlap flake", null, Rejected: false, ResolvedBy: "conductor"),
            TestContext.Current.CancellationToken);
        await TerminalSentinelWriter.WriteAsync(
            rejectedRoom,
            new WorkflowStatusView("Failed", [], [], "Resolved by the conductor: not honest work", null, Rejected: true, ResolvedBy: "conductor"),
            TestContext.Current.CancellationToken);
        await TerminalSentinelWriter.WriteAsync(
            crashedRoom,
            new WorkflowStatusView("Failed", [], [], "the worker crashed", null),
            TestContext.Current.CancellationToken);

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<FleetStatusResponse>(result.Text)!.Rooms;
        Assert.NotNull(rooms);

        var closed = rooms!.First(r => r.Name == "closed-room");
        Assert.Equal("conductor", closed.ResolvedBy);
        Assert.False(closed.Rejected);

        var rejected = rooms.First(r => r.Name == "conductor-rejected-room");
        Assert.Equal("conductor", rejected.ResolvedBy);
        Assert.True(rejected.Rejected);

        var crashed = rooms.First(r => r.Name == "plain-crashed-room");
        Assert.Null(crashed.ResolvedBy);
        // Wire-level, same reasoning as the `rejected` omission above.
        Assert.DoesNotContain("\"resolvedBy\"", JsonSerializer.Serialize(crashed));
        Assert.Contains("\"resolvedBy\":\"conductor\"", JsonSerializer.Serialize(closed));
    }

    [Fact]
    public async Task IncludeTerminalFalse_FiltersOutTerminalRooms()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var roomTerminal = Path.Combine(defaultRoomsDir, "room-term");
        var roomActive = Path.Combine(defaultRoomsDir, "room-act");
        Directory.CreateDirectory(roomTerminal);
        Directory.CreateDirectory(roomActive);

        var sentinel = new WorkflowStatusView("Succeeded", [], [], null, null);
        await TerminalSentinelWriter.WriteAsync(roomTerminal, sentinel, TestContext.Current.CancellationToken);

        var stepDef = new WorkflowStepDefinition(new StepId("step-active"), "agent-worker", [], [], [], new RetryPolicy(1));
        var def = new WorkflowDefinition(
            new WorkflowTemplateId("active-wf"),
            1,
            [stepDef]);
        var snapshot = SnapshotBinder.Bind(def);
        var snapshotPath = Path.Combine(roomActive, "snapshot.json");
        await SnapshotBinder.PersistAsync(snapshot, snapshotPath, TestContext.Current.CancellationToken);

        var logPath = Path.Combine(roomActive, "flow.jsonl");
        var writer = new FlowEventLogWriter(logPath);
        var execId = new ExecutionId("exec-active-1");
        var req = new ExecutionRequest(
            execId,
            new WorkflowId("active-wf"),
            stepDef.StepId,
            stepDef.Worker,
            [],
            [],
            TimeSpan.FromSeconds(30),
            [],
            new Dictionary<StepId, ExecutionId>());

        await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(req), TestContext.Current.CancellationToken);
        await writer.AppendAsync(new CoreEvent.ExecutionStarted(execId, Pid: 5555), TestContext.Current.CancellationToken);
        await writer.DisposeAsync();

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("""{ "include_terminal": false }"""), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<FleetStatusResponse>(result.Text)!.Rooms;
        Assert.NotNull(rooms);
        var singleRoom = Assert.Single(rooms!);
        Assert.Equal("room-act", singleRoom.Name);
    }

    /// <summary>
    /// #1503: the Running step's role/adapter/model/effort/timeout pass through from the room's real
    /// <c>bindings.json</c>, keyed by the same worker name <c>FlowEvent.ExecutionRequestAccepted</c>
    /// names for the Running step's execution.
    /// </summary>
    [Fact]
    public async Task ActiveRoom_RunningStepWithBindings_ReportsRoleAdapterModelEffortAndTimeoutFromBindingsJson()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "bound-room");
        Directory.CreateDirectory(room);

        var stepDef = new WorkflowStepDefinition(new StepId("step-bound"), "architect", [], [], [], new RetryPolicy(1));
        var def = new WorkflowDefinition(new WorkflowTemplateId("bound-wf"), 1, [stepDef]);
        var snapshot = SnapshotBinder.Bind(def);
        var snapshotPath = Path.Combine(room, "snapshot.json");
        await SnapshotBinder.PersistAsync(snapshot, snapshotPath, TestContext.Current.CancellationToken);

        var bindings = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry(
                "claude",
                new WorkerContract("architect", RequiredInputs: [], ProducedOutputs: [], OptionalMetadata: []),
                "Draft a plan.",
                TimeSpan.FromMinutes(5),
                Model: "claude-opus-4",
                Effort: "high"),
        };
        await WorkerBindingConfigWriter.SaveToFileAsync(
            bindings, BatonPaths.RoomBindingsFile(room), TestContext.Current.CancellationToken);

        var logPath = Path.Combine(room, "flow.jsonl");
        var writer = new FlowEventLogWriter(logPath);
        var execId = new ExecutionId("exec-bound-1");
        var req = new ExecutionRequest(
            execId,
            new WorkflowId("bound-wf"),
            stepDef.StepId,
            stepDef.Worker,
            [],
            [],
            TimeSpan.FromMinutes(5),
            [],
            new Dictionary<StepId, ExecutionId>());

        await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(req), TestContext.Current.CancellationToken);
        await writer.AppendAsync(new CoreEvent.ExecutionStarted(execId, Pid: 6001), TestContext.Current.CancellationToken);
        await writer.DisposeAsync();

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<FleetStatusResponse>(result.Text)!.Rooms;
        var singleRoom = Assert.Single(rooms!);
        Assert.Equal("Running", singleRoom.State);
        Assert.Equal("architect", singleRoom.Role);
        Assert.Equal("claude", singleRoom.Adapter);
        Assert.Equal("claude-opus-4", singleRoom.Model);
        Assert.Equal("high", singleRoom.Effort);
        Assert.Equal((long)TimeSpan.FromMinutes(5).TotalMilliseconds, singleRoom.TimeoutMs);
    }

    /// <summary>
    /// #1927 (and its review's MEDIUM): the room the issue was filed about — <c>baton dispatch
    /// --adapter agy</c> with no <c>--model</c> and no <c>--effort</c>, whose binding therefore carries
    /// null in both dispatch-input fields and the resolved stamps beside them. The acceptance line is
    /// <c>&lt;vendor&gt; · &lt;model&gt; · &lt;effort&gt;</c>, so this asserts on the projection a
    /// render surface actually reads rather than on the binding: the effort segment was empty until the
    /// suffix rung existed, because <c>EffortResolved</c> was an exact duplicate of <c>Effort</c> and
    /// this fallback was unreachable by construction.
    /// </summary>
    [Fact]
    public async Task ActiveRoom_DispatchedWithNoModelOrEffort_ProjectsTheResolvedStampsAndMarksThemResolved()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "resolved-room");
        Directory.CreateDirectory(room);

        var stepDef = new WorkflowStepDefinition(new StepId("step-resolved"), "architect", [], [], [], new RetryPolicy(1));
        var def = new WorkflowDefinition(new WorkflowTemplateId("resolved-wf"), 1, [stepDef]);
        var snapshot = SnapshotBinder.Bind(def);
        await SnapshotBinder.PersistAsync(
            snapshot, Path.Combine(room, "snapshot.json"), TestContext.Current.CancellationToken);

        // Exactly what RoleDispatch.ToBinding writes for that dispatch: the two dispatch inputs stay
        // null (spec/baton.md §2's display-only invariant) and the stamps carry the answer.
        var entry = RoleDispatch.ToBinding(
            WorkerRoleCatalog.For("review"), "Draft a plan.", adapterOverride: "agy", workerName: "architect");
        Assert.Null(entry.Model);
        Assert.Null(entry.Effort);

        await WorkerBindingConfigWriter.SaveToFileAsync(
            new Dictionary<string, WorkerBindingConfigEntry> { ["architect"] = entry },
            BatonPaths.RoomBindingsFile(room), TestContext.Current.CancellationToken);

        var writer = new FlowEventLogWriter(Path.Combine(room, "flow.jsonl"));
        var execId = new ExecutionId("exec-resolved-1");
        await writer.AppendAsync(
            new FlowEvent.ExecutionRequestAccepted(new ExecutionRequest(
                execId, new WorkflowId("resolved-wf"), stepDef.StepId, stepDef.Worker,
                [], [], TimeSpan.FromMinutes(5), [], new Dictionary<StepId, ExecutionId>())),
            TestContext.Current.CancellationToken);
        await writer.AppendAsync(new CoreEvent.ExecutionStarted(execId, Pid: 6011), TestContext.Current.CancellationToken);
        await writer.DisposeAsync();

        var result = await new FleetStatusTool().CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var singleRoom = Assert.Single(JsonSerializer.Deserialize<FleetStatusResponse>(result.Text)!.Rooms!);
        Assert.Equal("agy", singleRoom.Adapter);
        Assert.Equal("gemini-3.8-flash-high", singleRoom.Model);
        // The segment the MEDIUM was about: present, and marked as Baton's resolution rather than the
        // operator's own choice on both axes.
        Assert.Equal("high", singleRoom.Effort);
        Assert.Equal(BindingValueSource.ResolvedDefault, singleRoom.ModelSource);
        Assert.Equal(BindingValueSource.ResolvedDefault, singleRoom.EffortSource);
    }

    /// <summary>
    /// #1584: after a failover rebind, <see cref="FleetRoomStatusView.Adapter"/> and
    /// <see cref="FleetRoomStatusView.Model"/> prefer the running step's recorded-at-accept
    /// <see cref="ExecutionRequest"/> values rather than the room's current <c>bindings.json</c>,
    /// agreeing with <see cref="ExecutionUsageProjector"/>'s usage attribution.
    /// </summary>
    [Fact]
    public async Task ActiveRoom_RunningStepWithRecordedAdapterAndModel_PrefersRecordedValuesOverReboundBindingsJson()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "rebound-running-room");
        Directory.CreateDirectory(room);

        var stepDef = new WorkflowStepDefinition(new StepId("step-rebound"), "architect", [], [], [], new RetryPolicy(1));
        var def = new WorkflowDefinition(new WorkflowTemplateId("rebound-wf"), 1, [stepDef]);
        var snapshot = SnapshotBinder.Bind(def);
        var snapshotPath = Path.Combine(room, "snapshot.json");
        await SnapshotBinder.PersistAsync(snapshot, snapshotPath, TestContext.Current.CancellationToken);

        // Rebound bindings.json (current state after failover to claude):
        var bindings = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry(
                "claude",
                new WorkerContract("architect", RequiredInputs: [], ProducedOutputs: [], OptionalMetadata: []),
                "Draft a plan.",
                TimeSpan.FromMinutes(5),
                Model: "claude-opus-4",
                Effort: "high"),
        };
        await WorkerBindingConfigWriter.SaveToFileAsync(
            bindings, BatonPaths.RoomBindingsFile(room), TestContext.Current.CancellationToken);

        // Recorded execution request accepted earlier under agy:
        var logPath = Path.Combine(room, "flow.jsonl");
        var writer = new FlowEventLogWriter(logPath);
        var execId = new ExecutionId("exec-rebound-1");
        var req = new ExecutionRequest(
            execId,
            new WorkflowId("rebound-wf"),
            stepDef.StepId,
            stepDef.Worker,
            [],
            [],
            TimeSpan.FromMinutes(5),
            [],
            new Dictionary<StepId, ExecutionId>(),
            Adapter: "agy",
            Model: "gemini-3-flash");

        await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(req), TestContext.Current.CancellationToken);
        await writer.AppendAsync(new CoreEvent.ExecutionStarted(execId, Pid: 6002), TestContext.Current.CancellationToken);
        await writer.DisposeAsync();

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<FleetStatusResponse>(result.Text)!.Rooms;
        var singleRoom = Assert.Single(rooms!);
        Assert.Equal("Running", singleRoom.State);
        Assert.Equal("architect", singleRoom.Role);
        // Recorded request values win over rebound bindings.json:
        Assert.Equal("agy", singleRoom.Adapter);
        Assert.Equal("gemini-3-flash", singleRoom.Model);
        // Effort and TimeoutMs come from the resolved binding:
        Assert.Equal("high", singleRoom.Effort);
        Assert.Equal((long)TimeSpan.FromMinutes(5).TotalMilliseconds, singleRoom.TimeoutMs);
    }

    /// <summary>
    /// #1584: when the recorded request carries an <see cref="ExecutionRequest.Adapter"/> but no explicit
    /// <see cref="ExecutionRequest.Model"/> (e.g. vendor swap defaulting model), the adapter comes from
    /// the recorded request while model falls back to the binding.
    /// </summary>
    [Fact]
    public async Task ActiveRoom_RunningStepWithRecordedAdapterAndNullModel_PrefersRecordedAdapterAndFallsBackToBindingModel()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "rebound-null-model-room");
        Directory.CreateDirectory(room);

        var stepDef = new WorkflowStepDefinition(new StepId("step-partial"), "architect", [], [], [], new RetryPolicy(1));
        var def = new WorkflowDefinition(new WorkflowTemplateId("partial-wf"), 1, [stepDef]);
        var snapshot = SnapshotBinder.Bind(def);
        var snapshotPath = Path.Combine(room, "snapshot.json");
        await SnapshotBinder.PersistAsync(snapshot, snapshotPath, TestContext.Current.CancellationToken);

        var bindings = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry(
                "claude",
                new WorkerContract("architect", RequiredInputs: [], ProducedOutputs: [], OptionalMetadata: []),
                "Draft a plan.",
                TimeSpan.FromMinutes(5),
                Model: "claude-opus-4",
                Effort: "high"),
        };
        await WorkerBindingConfigWriter.SaveToFileAsync(
            bindings, BatonPaths.RoomBindingsFile(room), TestContext.Current.CancellationToken);

        var logPath = Path.Combine(room, "flow.jsonl");
        var writer = new FlowEventLogWriter(logPath);
        var execId = new ExecutionId("exec-partial-1");
        var req = new ExecutionRequest(
            execId,
            new WorkflowId("partial-wf"),
            stepDef.StepId,
            stepDef.Worker,
            [],
            [],
            TimeSpan.FromMinutes(5),
            [],
            new Dictionary<StepId, ExecutionId>(),
            Adapter: "agy",
            Model: null);

        await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(req), TestContext.Current.CancellationToken);
        await writer.AppendAsync(new CoreEvent.ExecutionStarted(execId, Pid: 6003), TestContext.Current.CancellationToken);
        await writer.DisposeAsync();

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<FleetStatusResponse>(result.Text)!.Rooms;
        var singleRoom = Assert.Single(rooms!);
        Assert.Equal("Running", singleRoom.State);
        Assert.Equal("architect", singleRoom.Role);
        Assert.Equal("agy", singleRoom.Adapter);
        Assert.Equal("claude-opus-4", singleRoom.Model);
        Assert.Equal("high", singleRoom.Effort);
        Assert.Equal((long)TimeSpan.FromMinutes(5).TotalMilliseconds, singleRoom.TimeoutMs);
    }

    /// <summary>
    /// #1503 fail-open arm: a room with no <c>bindings.json</c> at all (pre-#153, or simply never
    /// written for this room) must still render its row -- role/adapter/model/effort/timeout are
    /// just absent, never a thrown error or a missing room.
    /// </summary>
    [Fact]
    public async Task ActiveRoom_RunningStepWithNoBindingsFile_OmitsBindingFieldsButStillRendersRow()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "unbound-room");
        Directory.CreateDirectory(room);

        var stepDef = new WorkflowStepDefinition(new StepId("step-unbound"), "agent-worker", [], [], [], new RetryPolicy(1));
        var def = new WorkflowDefinition(new WorkflowTemplateId("unbound-wf"), 1, [stepDef]);
        var snapshot = SnapshotBinder.Bind(def);
        var snapshotPath = Path.Combine(room, "snapshot.json");
        await SnapshotBinder.PersistAsync(snapshot, snapshotPath, TestContext.Current.CancellationToken);

        var logPath = Path.Combine(room, "flow.jsonl");
        var writer = new FlowEventLogWriter(logPath);
        var execId = new ExecutionId("exec-unbound-1");
        var req = new ExecutionRequest(
            execId,
            new WorkflowId("unbound-wf"),
            stepDef.StepId,
            stepDef.Worker,
            [],
            [],
            TimeSpan.FromSeconds(30),
            [],
            new Dictionary<StepId, ExecutionId>());

        await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(req), TestContext.Current.CancellationToken);
        await writer.DisposeAsync();

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<FleetStatusResponse>(result.Text)!.Rooms;
        var singleRoom = Assert.Single(rooms!);
        Assert.Equal("Running", singleRoom.State);
        Assert.Null(singleRoom.Role);
        Assert.Null(singleRoom.Adapter);
        Assert.Null(singleRoom.Model);
        Assert.Null(singleRoom.Effort);
        Assert.Null(singleRoom.TimeoutMs);
        AssertBindingFieldsAbsentFromWire(singleRoom);
    }

    /// <summary>
    /// Wire-level "absent, not emitted null" for all five binding fields — object-level
    /// <c>Assert.Null</c> cannot distinguish an omitted key from a serialized <c>"field": null</c>
    /// round-tripped back, which is exactly what a dropped <c>JsonIgnore(WhenWritingNull)</c> would
    /// ship silently (PR #1504 review finding A).
    /// </summary>
    private static void AssertBindingFieldsAbsentFromWire(FleetRoomStatusView room)
    {
        var wire = JsonSerializer.Serialize(room);
        Assert.DoesNotContain("\"role\"", wire);
        Assert.DoesNotContain("\"adapter\"", wire);
        Assert.DoesNotContain("\"model\"", wire);
        Assert.DoesNotContain("\"effort\"", wire);
        Assert.DoesNotContain("\"timeoutMs\"", wire);
    }

    /// <summary>
    /// #1503 fail-open arm, opposite corruption mode: a <c>bindings.json</c> that exists but is not
    /// valid JSON must degrade the same way a missing file does -- the room row still renders with
    /// everything else intact, only the binding fields absent.
    /// </summary>
    [Fact]
    public async Task ActiveRoom_RunningStepWithCorruptBindingsFile_OmitsBindingFieldsButStillRendersRow()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "corrupt-bindings-room");
        Directory.CreateDirectory(room);

        var stepDef = new WorkflowStepDefinition(new StepId("step-corrupt"), "agent-worker", [], [], [], new RetryPolicy(1));
        var def = new WorkflowDefinition(new WorkflowTemplateId("corrupt-wf"), 1, [stepDef]);
        var snapshot = SnapshotBinder.Bind(def);
        var snapshotPath = Path.Combine(room, "snapshot.json");
        await SnapshotBinder.PersistAsync(snapshot, snapshotPath, TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(
            BatonPaths.RoomBindingsFile(room), "{ not valid json", TestContext.Current.CancellationToken);

        var logPath = Path.Combine(room, "flow.jsonl");
        var writer = new FlowEventLogWriter(logPath);
        var execId = new ExecutionId("exec-corrupt-1");
        var req = new ExecutionRequest(
            execId,
            new WorkflowId("corrupt-wf"),
            stepDef.StepId,
            stepDef.Worker,
            [],
            [],
            TimeSpan.FromSeconds(30),
            [],
            new Dictionary<StepId, ExecutionId>());

        await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(req), TestContext.Current.CancellationToken);
        await writer.DisposeAsync();

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<FleetStatusResponse>(result.Text)!.Rooms;
        var singleRoom = Assert.Single(rooms!);
        Assert.Equal("Running", singleRoom.State);
        Assert.Null(singleRoom.Error);
        Assert.Null(singleRoom.Role);
        Assert.Null(singleRoom.Adapter);
        Assert.Null(singleRoom.Model);
        Assert.Null(singleRoom.Effort);
        Assert.Null(singleRoom.TimeoutMs);
        AssertBindingFieldsAbsentFromWire(singleRoom);
    }

    /// <summary>
    /// #1503 fail-open arm three (PR #1504 review finding B): a VALID <c>bindings.json</c> whose
    /// dictionary simply lacks the Running step's worker role degrades identically to a missing
    /// file — display metadata fails open where <c>ResumeCommand</c> treats the same situation as a
    /// hard error, because a fleet row without chips beats a fleet call that throws.
    /// </summary>
    [Fact]
    public async Task ActiveRoom_ValidBindingsWithoutTheRunningRolesKey_OmitsBindingFieldsButStillRendersRow()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "role-missing-room");
        Directory.CreateDirectory(room);

        var stepDef = new WorkflowStepDefinition(new StepId("step-role-missing"), "agent-worker", [], [], [], new RetryPolicy(1));
        var def = new WorkflowDefinition(new WorkflowTemplateId("role-missing-wf"), 1, [stepDef]);
        var snapshot = SnapshotBinder.Bind(def);
        await SnapshotBinder.PersistAsync(snapshot, Path.Combine(room, "snapshot.json"), TestContext.Current.CancellationToken);

        var bindings = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["some-other-role"] = new WorkerBindingConfigEntry(
                "claude",
                new WorkerContract("some-other-role", RequiredInputs: [], ProducedOutputs: [], OptionalMetadata: []),
                "Do something else.",
                TimeSpan.FromMinutes(5),
                Model: "claude-opus-4",
                Effort: "high"),
        };
        await WorkerBindingConfigWriter.SaveToFileAsync(
            bindings, BatonPaths.RoomBindingsFile(room), TestContext.Current.CancellationToken);

        var logPath = Path.Combine(room, "flow.jsonl");
        var writer = new FlowEventLogWriter(logPath);
        var execId = new ExecutionId("exec-role-missing-1");
        var req = new ExecutionRequest(
            execId,
            new WorkflowId("role-missing-wf"),
            stepDef.StepId,
            stepDef.Worker,
            [],
            [],
            TimeSpan.FromSeconds(30),
            [],
            new Dictionary<StepId, ExecutionId>());

        await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(req), TestContext.Current.CancellationToken);
        await writer.DisposeAsync();

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<FleetStatusResponse>(result.Text)!.Rooms;
        var singleRoom = Assert.Single(rooms!);
        Assert.Equal("Running", singleRoom.State);
        Assert.Null(singleRoom.Error);
        AssertBindingFieldsAbsentFromWire(singleRoom);
    }

    [Fact]
    public async Task MalformedRoom_ReturnsErrorEntryWithoutFailingWholeResponse()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var healthyRoom = Path.Combine(defaultRoomsDir, "healthy-room");
        var brokenRoom = Path.Combine(defaultRoomsDir, "broken-room");
        Directory.CreateDirectory(healthyRoom);
        Directory.CreateDirectory(brokenRoom);

        var sentinel = new WorkflowStatusView("Succeeded", [], [], null, null);
        await TerminalSentinelWriter.WriteAsync(healthyRoom, sentinel, TestContext.Current.CancellationToken);

        // Broken room has corrupt snapshot
        await File.WriteAllTextAsync(Path.Combine(brokenRoom, "snapshot.json"), "{ invalid json", TestContext.Current.CancellationToken);

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<FleetStatusResponse>(result.Text)!.Rooms;
        Assert.NotNull(rooms);
        Assert.Equal(2, rooms!.Count);

        var healthy = rooms.First(r => r.Name == "healthy-room");
        Assert.Equal("Succeeded", healthy.State);
        Assert.Null(healthy.Error);

        var broken = rooms.First(r => r.Name == "broken-room");
        Assert.NotNull(broken.Error);
        Assert.Null(broken.State);
    }

    [Fact]
    public async Task Call_SynchronousOverload_ReturnsSameShape()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "sync-room");
        Directory.CreateDirectory(room);

        var sentinel = new WorkflowStatusView("Succeeded", [], [], null, null);
        await TerminalSentinelWriter.WriteAsync(room, sentinel, TestContext.Current.CancellationToken);

        var tool = new FleetStatusTool();
        var result = tool.Call(Parse("{}"));

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<FleetStatusResponse>(result.Text)!.Rooms;
        Assert.NotNull(rooms);
        var singleRoom = Assert.Single(rooms!);
        Assert.Equal("sync-room", singleRoom.Name);
    }

    /// <summary>
    /// spec/baton.md §8's named invariant, as a regression test rather than a design note: a room
    /// registered under a project root the caller never passes as a <c>roots</c> entry is still found.
    /// The room directory here sits outside both <see cref="BatonPaths.Rooms"/> and any scanned root —
    /// only the registry names it — so this fails the moment the union degrades back to a bare
    /// directory scan.
    /// </summary>
    [Fact]
    public async Task RegistryEntry_OutsideEveryScannedRoot_IsStillFoundByFleetStatus()
    {
        var unlistedProjectDir = Path.Combine(Path.GetTempPath(), $"baton-fleet-unlisted-project-{Guid.NewGuid():N}");
        var room = Path.Combine(unlistedProjectDir, ".baton", "rooms", "registry-only-room");

        try
        {
            Directory.CreateDirectory(room);
            var sentinel = new WorkflowStatusView("Succeeded", [], [], null, null);
            await TerminalSentinelWriter.WriteAsync(room, sentinel, TestContext.Current.CancellationToken);

            await RoomRegistryStore.AppendAsync(
                room, unlistedProjectDir, BatonPaths.RoomRegistryFile,
                explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);

            var tool = new FleetStatusTool();
            // Deliberately no "roots" entry for unlistedProjectDir -- the whole point of the test.
            var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

            Assert.False(result.IsError);
            var rooms = JsonSerializer.Deserialize<FleetStatusResponse>(result.Text)!.Rooms;
            Assert.NotNull(rooms);
            var found = Assert.Single(rooms!, r => r.Name == "registry-only-room");
            Assert.Equal("Succeeded", found.State);
            Assert.Equal(BatonPaths.RecordKey(unlistedProjectDir), found.Project);
        }
        finally
        {
            if (Directory.Exists(unlistedProjectDir))
            {
                DirectoryCleanup.DeleteRecursively(unlistedProjectDir);
            }
        }
    }

    [Fact]
    public async Task RegistryEntry_WhoseRoomDirectoryWasDeleted_IsSkippedRatherThanErroring()
    {
        var deletedRoomProjectDir = Path.Combine(Path.GetTempPath(), $"baton-fleet-deleted-project-{Guid.NewGuid():N}");
        var deletedRoom = Path.Combine(deletedRoomProjectDir, "rooms", "gone-room");
        try
        {
            Directory.CreateDirectory(deletedRoom);
            await RoomRegistryStore.AppendAsync(
                deletedRoom, deletedRoomProjectDir, BatonPaths.RoomRegistryFile,
                explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);
            DirectoryCleanup.DeleteRecursively(deletedRoom);

            var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
            var healthyRoom = Path.Combine(defaultRoomsDir, "healthy-registry-room");
            Directory.CreateDirectory(healthyRoom);
            var sentinel = new WorkflowStatusView("Succeeded", [], [], null, null);
            await TerminalSentinelWriter.WriteAsync(healthyRoom, sentinel, TestContext.Current.CancellationToken);

            var tool = new FleetStatusTool();
            var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

            Assert.False(result.IsError);
            var rooms = JsonSerializer.Deserialize<FleetStatusResponse>(result.Text)!.Rooms;
            Assert.NotNull(rooms);
            Assert.DoesNotContain(rooms!, r => r.Name == "gone-room");
            Assert.Contains(rooms!, r => r.Name == "healthy-registry-room");
        }
        finally
        {
            if (Directory.Exists(deletedRoomProjectDir))
            {
                DirectoryCleanup.DeleteRecursively(deletedRoomProjectDir);
            }
        }
    }

    [Fact]
    public async Task MalformedRegistry_IsToleratedAndFallsBackToTheDirectoryScan()
    {
        await File.WriteAllTextAsync(
            BatonPaths.RoomRegistryFile, "{ not valid json\n", TestContext.Current.CancellationToken);

        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "scanned-room");
        Directory.CreateDirectory(room);
        var sentinel = new WorkflowStatusView("Succeeded", [], [], null, null);
        await TerminalSentinelWriter.WriteAsync(room, sentinel, TestContext.Current.CancellationToken);

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<FleetStatusResponse>(result.Text)!.Rooms;
        Assert.NotNull(rooms);
        var singleRoom = Assert.Single(rooms!);
        Assert.Equal("scanned-room", singleRoom.Name);
        Assert.Null(singleRoom.Project);
    }

    /// <summary>
    /// A real I/O failure, not just malformed content (#1447 review finding): the registry path
    /// occupied by a DIRECTORY makes every open attempt throw. The only-ever-adds-coverage
    /// contract means the scan's rooms must still come back with no error — losing the whole call
    /// to a registry read failure would be strictly worse than answering scan-only.
    /// </summary>
    [Fact]
    public async Task RegistryPathOccupiedByADirectory_StillAnswersFromTheScanAlone()
    {
        Directory.CreateDirectory(BatonPaths.RoomRegistryFile);

        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "scanned-room");
        Directory.CreateDirectory(room);
        var sentinel = new WorkflowStatusView("Succeeded", [], [], null, null);
        await TerminalSentinelWriter.WriteAsync(room, sentinel, TestContext.Current.CancellationToken);

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<FleetStatusResponse>(result.Text)!.Rooms;
        Assert.NotNull(rooms);
        var singleRoom = Assert.Single(rooms!);
        Assert.Equal("scanned-room", singleRoom.Name);
        Assert.Null(singleRoom.Project);
    }

    /// <summary>#1499, spec/baton.md §6 schema: the terminal-sentinel fast path still surfaces a label.</summary>
    [Fact]
    public async Task TerminalFastPath_WithLabelInBindings_ReportsLabel()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "labeled-terminal-room");
        Directory.CreateDirectory(room);

        var sentinel = new WorkflowStatusView("Succeeded", [], [], null, null);
        await TerminalSentinelWriter.WriteAsync(room, sentinel, TestContext.Current.CancellationToken);

        var bindings = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["advise"] = new WorkerBindingConfigEntry(
                "claude",
                new WorkerContract("advise", RequiredInputs: [], ProducedOutputs: [], OptionalMetadata: []),
                "Weigh the options.",
                TimeSpan.FromMinutes(5),
                Label: "env-snapshot lane"),
        };
        await WorkerBindingConfigWriter.SaveToFileAsync(
            bindings, BatonPaths.RoomBindingsFile(room), TestContext.Current.CancellationToken);

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<FleetStatusResponse>(result.Text)!.Rooms;
        var singleRoom = Assert.Single(rooms!);
        Assert.Equal("Succeeded", singleRoom.State);
        Assert.Equal("env-snapshot lane", singleRoom.Label);
        // #1613 item 3: bindings.json names exactly one role here, so the terminal fast path now
        // carries it through -- this used to be the reported bug (role/adapter vanish on terminal
        // rooms even though the bindings file that would answer them is sitting right there).
        Assert.Equal("advise", singleRoom.Role);
        Assert.Equal("claude", singleRoom.Adapter);
    }

    /// <summary>
    /// #1613 item 3's own guard against the "first entry" trap -- rationale is spec/baton.md §6.
    /// </summary>
    [Fact]
    public async Task TerminalFastPath_WithMultipleBindingsRoles_OmitsBindingFieldsRatherThanGuess()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "multi-role-terminal-room");
        Directory.CreateDirectory(room);

        var sentinel = new WorkflowStatusView("Succeeded", [], [], null, null);
        await TerminalSentinelWriter.WriteAsync(room, sentinel, TestContext.Current.CancellationToken);

        var bindings = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry(
                "claude",
                new WorkerContract("architect", RequiredInputs: [], ProducedOutputs: [], OptionalMetadata: []),
                "Design it.",
                TimeSpan.FromMinutes(5),
                Model: "claude-opus-4"),
            ["reviewer"] = new WorkerBindingConfigEntry(
                "agy",
                new WorkerContract("reviewer", RequiredInputs: [], ProducedOutputs: [], OptionalMetadata: []),
                "Check it.",
                TimeSpan.FromMinutes(5),
                Model: "gemini-3-pro"),
        };
        await WorkerBindingConfigWriter.SaveToFileAsync(
            bindings, BatonPaths.RoomBindingsFile(room), TestContext.Current.CancellationToken);

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<FleetStatusResponse>(result.Text)!.Rooms;
        var singleRoom = Assert.Single(rooms!);
        Assert.Equal("Succeeded", singleRoom.State);
        Assert.Null(singleRoom.Role);
        Assert.Null(singleRoom.Adapter);
        Assert.Null(singleRoom.Model);
        Assert.Null(singleRoom.Effort);
        Assert.Null(singleRoom.TimeoutMs);
    }

    /// <summary>
    /// The fail-open half of #1499's own claim: an unparseable <c>bindings.json</c> on the
    /// terminal-sentinel path (which has no enclosing try/catch of its own around the label read)
    /// must degrade to an absent label, not an exception that drops the row.
    /// </summary>
    [Fact]
    public async Task TerminalFastPath_WithCorruptBindingsFile_OmitsLabelButStillRendersRow()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "corrupt-bindings-terminal-room");
        Directory.CreateDirectory(room);

        var sentinel = new WorkflowStatusView("Succeeded", [], [], null, null);
        await TerminalSentinelWriter.WriteAsync(room, sentinel, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            BatonPaths.RoomBindingsFile(room), "{ not valid json", TestContext.Current.CancellationToken);

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<FleetStatusResponse>(result.Text)!.Rooms;
        var singleRoom = Assert.Single(rooms!);
        Assert.Equal("Succeeded", singleRoom.State);
        Assert.Null(singleRoom.Label);
    }

    [Fact]
    public async Task TerminalFastPath_WithNoBindingsFile_OmitsLabelFromTheWire()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "unlabeled-terminal-room");
        Directory.CreateDirectory(room);

        var sentinel = new WorkflowStatusView("Succeeded", [], [], null, null);
        await TerminalSentinelWriter.WriteAsync(room, sentinel, TestContext.Current.CancellationToken);

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        // Asserts on the actual MCP payload (result.Text), not a re-serialized deserialized copy --
        // the real wire text is the thing a JsonIgnore regression would actually change.
        Assert.DoesNotContain("\"label\"", result.Text);
        var rooms = JsonSerializer.Deserialize<FleetStatusResponse>(result.Text)!.Rooms;
        Assert.Null(Assert.Single(rooms!).Label);
    }

    /// <summary>#1619, spec/baton.md §6 schema: the terminal-sentinel fast path still surfaces a workstream.</summary>
    [Fact]
    public async Task TerminalFastPath_WithWorkstreamInBindings_ReportsWorkstream()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "workstream-terminal-room");
        Directory.CreateDirectory(room);

        var sentinel = new WorkflowStatusView("Succeeded", [], [], null, null);
        await TerminalSentinelWriter.WriteAsync(room, sentinel, TestContext.Current.CancellationToken);

        var bindings = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["advise"] = new WorkerBindingConfigEntry(
                "claude",
                new WorkerContract("advise", RequiredInputs: [], ProducedOutputs: [], OptionalMetadata: []),
                "Weigh the options.",
                TimeSpan.FromMinutes(5),
                Workstream: "w1619"),
        };
        await WorkerBindingConfigWriter.SaveToFileAsync(
            bindings, BatonPaths.RoomBindingsFile(room), TestContext.Current.CancellationToken);

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<FleetStatusResponse>(result.Text)!.Rooms;
        var singleRoom = Assert.Single(rooms!);
        Assert.Equal("Succeeded", singleRoom.State);
        Assert.Equal("w1619", singleRoom.Workstream);
    }

    [Fact]
    public async Task TerminalFastPath_WithNoBindingsFile_OmitsWorkstreamFromTheWire()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "ungrouped-terminal-room");
        Directory.CreateDirectory(room);

        var sentinel = new WorkflowStatusView("Succeeded", [], [], null, null);
        await TerminalSentinelWriter.WriteAsync(room, sentinel, TestContext.Current.CancellationToken);

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        // Asserts on the actual MCP payload (result.Text), not a re-serialized deserialized copy --
        // the real wire text is the thing a JsonIgnore regression would actually change.
        Assert.DoesNotContain("\"workstream\"", result.Text);
        var rooms = JsonSerializer.Deserialize<FleetStatusResponse>(result.Text)!.Rooms;
        Assert.Null(Assert.Single(rooms!).Workstream);
    }

    /// <summary>#1441/#1620, spec/baton.md §6 schema: the terminal-sentinel fast path surfaces redispatch lineage.</summary>
    [Fact]
    public async Task TerminalFastPath_WithLineageMarker_ReportsParentRoomPathAndExecutionId()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "redispatched-terminal-room");
        Directory.CreateDirectory(room);

        var sentinel = new WorkflowStatusView("Succeeded", [], [], null, null);
        await TerminalSentinelWriter.WriteAsync(room, sentinel, TestContext.Current.CancellationToken);

        var parentRoomPath = Path.Combine(defaultRoomsDir, "parent-room");
        await InteractiveSessionMaterializer.WriteWorkflowRoomMarkerAsync(
            room, parentRoomDirectoryPath: parentRoomPath, parentExecutionId: "exec-parent-1",
            cancellationToken: TestContext.Current.CancellationToken);

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<FleetStatusResponse>(result.Text)!.Rooms;
        var singleRoom = Assert.Single(rooms!);
        Assert.Equal(parentRoomPath, singleRoom.ParentRoomPath);
        Assert.Equal("exec-parent-1", singleRoom.ParentExecutionId);
    }

    /// <summary>An ordinary `baton dispatch` room writes no marker at all -- both lineage fields stay absent from the wire.</summary>
    [Fact]
    public async Task TerminalFastPath_WithNoRoomMarker_OmitsLineageFieldsFromTheWire()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "ordinary-dispatch-room");
        Directory.CreateDirectory(room);

        var sentinel = new WorkflowStatusView("Succeeded", [], [], null, null);
        await TerminalSentinelWriter.WriteAsync(room, sentinel, TestContext.Current.CancellationToken);

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        Assert.DoesNotContain("\"parentRoomPath\"", result.Text);
        Assert.DoesNotContain("\"parentExecutionId\"", result.Text);
        var rooms = JsonSerializer.Deserialize<FleetStatusResponse>(result.Text)!.Rooms;
        var singleRoom = Assert.Single(rooms!);
        Assert.Null(singleRoom.ParentRoomPath);
        Assert.Null(singleRoom.ParentExecutionId);
        Assert.DoesNotContain("\"continuedSessionId\"", result.Text);
        Assert.Null(singleRoom.ContinuedSessionId);
    }

    /// <summary>
    /// #1381, spec/baton.md §6 schema: `continuedSessionId` is the one field that tells a `--continue`
    /// dispatch's lineage apart from an ordinary `baton redispatch`'s, on the identical
    /// `parentRoomPath`/`parentExecutionId` read path <see cref="TerminalFastPath_WithLineageMarker_ReportsParentRoomPathAndExecutionId"/>
    /// already pins for redispatch (which never sets this field — see that test's own marker call).
    /// </summary>
    [Fact]
    public async Task TerminalFastPath_WithContinuationMarker_ReportsContinuedSessionId()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "continued-terminal-room");
        Directory.CreateDirectory(room);

        var sentinel = new WorkflowStatusView("Succeeded", [], [], null, null);
        await TerminalSentinelWriter.WriteAsync(room, sentinel, TestContext.Current.CancellationToken);

        var parentRoomPath = Path.Combine(defaultRoomsDir, "veteran-room");
        await InteractiveSessionMaterializer.WriteWorkflowRoomMarkerAsync(
            room, parentRoomDirectoryPath: parentRoomPath, parentExecutionId: "exec-parent-1",
            continuedSessionId: "sess-abc-123", cancellationToken: TestContext.Current.CancellationToken);

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<FleetStatusResponse>(result.Text)!.Rooms;
        var singleRoom = Assert.Single(rooms!);
        Assert.Equal(parentRoomPath, singleRoom.ParentRoomPath);
        Assert.Equal("exec-parent-1", singleRoom.ParentExecutionId);
        Assert.Equal("sess-abc-123", singleRoom.ContinuedSessionId);
    }

    /// <summary>
    /// The fail-open half of #1620's own claim: a corrupt <c>.baton/room.json</c> marker (no enclosing
    /// try/catch of its own around the lineage read on the terminal fast path) must degrade to absent
    /// lineage, not an exception that drops the row -- the same posture #1499's label read already has.
    /// </summary>
    [Fact]
    public async Task TerminalFastPath_WithCorruptRoomMarker_OmitsLineageButStillRendersRow()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "corrupt-marker-terminal-room");
        Directory.CreateDirectory(room);

        var sentinel = new WorkflowStatusView("Succeeded", [], [], null, null);
        await TerminalSentinelWriter.WriteAsync(room, sentinel, TestContext.Current.CancellationToken);

        var markerDir = Path.Combine(room, ".baton");
        Directory.CreateDirectory(markerDir);
        await File.WriteAllTextAsync(
            Path.Combine(markerDir, BatonPaths.RoomMetadataFileName), "{ not valid json",
            TestContext.Current.CancellationToken);

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<FleetStatusResponse>(result.Text)!.Rooms;
        var singleRoom = Assert.Single(rooms!);
        Assert.Equal("Succeeded", singleRoom.State);
        Assert.Null(singleRoom.ParentRoomPath);
        Assert.Null(singleRoom.ParentExecutionId);
    }

    /// <summary>#1441/#1620: the active-room path reports lineage too -- it is written once at redispatch and never depends on run progress.</summary>
    [Fact]
    public async Task ActiveRoom_WithLineageMarker_ReportsParentRoomPathAndExecutionId()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "redispatched-active-room");
        Directory.CreateDirectory(room);

        var stepDef = new WorkflowStepDefinition(new StepId("step-pending"), "advise", [], [], [], new RetryPolicy(1));
        var def = new WorkflowDefinition(new WorkflowTemplateId("pending-wf"), 1, [stepDef]);
        var snapshot = SnapshotBinder.Bind(def);
        var snapshotPath = Path.Combine(room, "snapshot.json");
        await SnapshotBinder.PersistAsync(snapshot, snapshotPath, TestContext.Current.CancellationToken);

        var parentRoomPath = Path.Combine(defaultRoomsDir, "parent-room");
        await InteractiveSessionMaterializer.WriteWorkflowRoomMarkerAsync(
            room, parentRoomDirectoryPath: parentRoomPath, parentExecutionId: "exec-parent-2",
            cancellationToken: TestContext.Current.CancellationToken);

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("""{ "include_terminal": false }"""), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<FleetStatusResponse>(result.Text)!.Rooms;
        var singleRoom = Assert.Single(rooms!);
        Assert.Equal(parentRoomPath, singleRoom.ParentRoomPath);
        Assert.Equal("exec-parent-2", singleRoom.ParentExecutionId);
    }

    /// <summary>#1619: a Pending room (no Running step) still reports its workstream on the active path.</summary>
    [Fact]
    public async Task ActiveRoom_WithNoRunningStep_StillReportsWorkstream()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "pending-workstream-room");
        Directory.CreateDirectory(room);

        var stepDef = new WorkflowStepDefinition(new StepId("step-pending"), "advise", [], [], [], new RetryPolicy(1));
        var def = new WorkflowDefinition(new WorkflowTemplateId("pending-wf"), 1, [stepDef]);
        var snapshot = SnapshotBinder.Bind(def);
        var snapshotPath = Path.Combine(room, "snapshot.json");
        await SnapshotBinder.PersistAsync(snapshot, snapshotPath, TestContext.Current.CancellationToken);

        var bindings = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["advise"] = new WorkerBindingConfigEntry(
                "claude",
                new WorkerContract("advise", RequiredInputs: [], ProducedOutputs: [], OptionalMetadata: []),
                "Weigh the options.",
                TimeSpan.FromMinutes(5),
                Workstream: "w1619"),
        };
        await WorkerBindingConfigWriter.SaveToFileAsync(
            bindings, BatonPaths.RoomBindingsFile(room), TestContext.Current.CancellationToken);

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("""{ "include_terminal": false }"""), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<FleetStatusResponse>(result.Text)!.Rooms;
        var singleRoom = Assert.Single(rooms!);
        Assert.Equal("w1619", singleRoom.Workstream);
    }

    /// <summary>#1499: a Pending room (no <c>flow.jsonl</c>, so no step is Running) still reports its label.</summary>
    [Fact]
    public async Task ActiveRoom_WithNoRunningStep_StillReportsLabelButNotTheRunningStepQuartet()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "pending-labeled-room");
        Directory.CreateDirectory(room);

        var stepDef = new WorkflowStepDefinition(new StepId("step-pending"), "advise", [], [], [], new RetryPolicy(1));
        var def = new WorkflowDefinition(new WorkflowTemplateId("pending-wf"), 1, [stepDef]);
        var snapshot = SnapshotBinder.Bind(def);
        var snapshotPath = Path.Combine(room, "snapshot.json");
        await SnapshotBinder.PersistAsync(snapshot, snapshotPath, TestContext.Current.CancellationToken);

        var bindings = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["advise"] = new WorkerBindingConfigEntry(
                "claude",
                new WorkerContract("advise", RequiredInputs: [], ProducedOutputs: [], OptionalMetadata: []),
                "Weigh the options.",
                TimeSpan.FromMinutes(5),
                Label: "env-snapshot lane"),
        };
        await WorkerBindingConfigWriter.SaveToFileAsync(
            bindings, BatonPaths.RoomBindingsFile(room), TestContext.Current.CancellationToken);

        // No flow.jsonl at all -- FlowEventLogReader.ReadAllEntriesWithTimestampsAsync treats a
        // missing log as zero entries, so the STEP projects Pending, never Running. (The room's own
        // top-level `state` still reads "Running" either way -- WorkflowOutcome.Describe reports the
        // overall WorkflowStatus, which starts Running before any step's own state does; the gate that
        // matters here is per-step.)

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("""{ "include_terminal": false }"""), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<FleetStatusResponse>(result.Text)!.Rooms;
        var singleRoom = Assert.Single(rooms!);
        Assert.DoesNotContain(singleRoom.Steps ?? [], s => s.State == "Running");
        Assert.Equal("env-snapshot lane", singleRoom.Label);
        Assert.Null(singleRoom.Role);
        Assert.Null(singleRoom.Adapter);
    }

    /// <summary>
    /// #1708 L1: <see cref="FleetStatusTool"/> hand-copies <see cref="WorkflowStatusStepView"/> into
    /// <see cref="FleetStepStatusView"/> at two separate sites, and nothing until now noticed when the
    /// two field sets drifted — the next field added would silently reach <c>status --json</c> and not
    /// <c>fleet_status</c>. Same guard shape as
    /// <c>FlowEventLogJsonTests.Every_FlowEvent_variant_is_covered_by_these_tests</c>: a new property on
    /// the source view fails here until it is either mirrored or listed below as a deliberate omission.
    /// <para>
    /// This checks the two records' VOCABULARY, not that either copy site assigns correctly — the copy
    /// sites are private, and their values are asserted by the field-level tests above. The measured
    /// failure was a missing field, not a mis-assigned one.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_step_view_property_is_either_mirrored_onto_the_fleet_view_or_deliberately_omitted()
    {
        // Deliberate omissions, each with its reason. fleet_status is a fleet-wide glance, not a
        // resolution surface: the first three exist to drive `baton resolve`'s admission test against
        // ONE room, which a caller reads from `baton status --json` on that room (spec/baton.md §3).
        // VerifyTail (#1701) is omitted for the same reason plus a size one: it is a failing gate
        // member's own captured output, bounded at VerifyRunner.MaxTailChars (4000 chars) PER STEP, so
        // mirroring it would put a multi-kilobyte diagnostic blob into every room's entry of a
        // fleet-wide listing. The short verify/verifyReason tokens are mirrored; the blob is read from
        // `baton status --json` on the one room being diagnosed.
        // ResolvedByConductor (#1622 (c)/(d)) is omitted for the same "fleet glance, not a resolution
        // surface" reason: FleetRoomStatusView carries the room-level Rejected/ResolvedBy pair this
        // mirrors coarsely (F10, #1720 review: `ResolvedBy` was added to that view in the same change
        // this comment was corrected in -- it previously cited a field the fleet view did not have),
        // and WHICH step was resolved is a `baton status --json` question on the one room a caller is
        // already diagnosing, same as the other three above.
        var deliberatelyOmitted = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(WorkflowStatusStepView.CapturedResponseFile),
            nameof(WorkflowStatusStepView.UnsatisfiedOutputs),
            nameof(WorkflowStatusStepView.IndeterminateProducerKind),
            nameof(WorkflowStatusStepView.VerifyTail),
            nameof(WorkflowStatusStepView.ResolvedByConductor),
        };

        var source = typeof(WorkflowStatusStepView).GetProperties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var mirrored = typeof(FleetStepStatusView).GetProperties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        var unmirrored = source.Except(mirrored).Except(deliberatelyOmitted).OrderBy(n => n, StringComparer.Ordinal);
        Assert.Empty(unmirrored);

        // And the omission list itself cannot go stale: a name removed from (or renamed on) the source
        // view has to leave this list too, rather than sit here excusing a field that no longer exists.
        Assert.Empty(deliberatelyOmitted.Except(source).OrderBy(n => n, StringComparer.Ordinal));
    }

    /// <summary>
    /// #1157: a terminal room reports when its run ENDED, off the journal's own writer stamps — not
    /// when anything last touched a file. This drives the projection path (no sentinel), where a room
    /// whose journal was written hours ago but whose directory was created seconds ago is the clearest
    /// separation of the two answers available.
    /// </summary>
    [Fact]
    public async Task TerminalRoom_ReportsTheTerminalInstant_NotTheJournalsTouchTime()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "ended-room");
        Directory.CreateDirectory(room);

        var stepDef = new WorkflowStepDefinition(new StepId("step-done"), "agent-worker", [], [], [], new RetryPolicy(1));
        var snapshot = SnapshotBinder.Bind(new WorkflowDefinition(new WorkflowTemplateId("ended-wf"), 1, [stepDef]));
        await SnapshotBinder.PersistAsync(snapshot, Path.Combine(room, "snapshot.json"), TestContext.Current.CancellationToken);

        var execId = new ExecutionId("exec-ended-1");
        var req = new ExecutionRequest(
            execId, new WorkflowId("ended-wf"), stepDef.StepId, stepDef.Worker,
            [], [], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId>());

        var endedAt = new DateTime(2026, 8, 20, 9, 30, 0, DateTimeKind.Utc);
        var logPath = Path.Combine(room, "flow.jsonl");
        await WriteStampedJournalAsync(
            logPath,
            (new FlowEvent.ExecutionRequestAccepted(req), endedAt.AddMinutes(-10)),
            (new FlowEvent.ExecutionSucceeded(execId), endedAt));

        // The control that makes this discriminate: the file itself was written just now, so anything
        // reading a touch time would report today rather than the recorded ending.
        Assert.True(DateTime.UtcNow - File.GetLastWriteTimeUtc(logPath) < TimeSpan.FromMinutes(5));

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{\"include_terminal\": true}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<FleetStatusResponse>(result.Text)!.Rooms;
        var ended = Assert.Single(rooms!);
        Assert.Equal("Succeeded", ended.State);
        Assert.Equal(endedAt, DateTime.Parse(ended.TerminalAt!, null, System.Globalization.DateTimeStyles.RoundtripKind));
    }

    /// <summary>
    /// #1157's legacy-sentinel arm, and the polarity opposite of the test above: pins the
    /// omit-rather-than-back-fill clause of spec/baton.md §3.
    /// </summary>
    [Fact]
    public async Task TerminalSentinelWrittenBeforeTheField_OmitsTerminalAt_RatherThanUsingItsMtime()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var legacyRoom = Path.Combine(defaultRoomsDir, "legacy-sentinel-room");
        var currentRoom = Path.Combine(defaultRoomsDir, "current-sentinel-room");
        Directory.CreateDirectory(legacyRoom);
        Directory.CreateDirectory(currentRoom);

        await TerminalSentinelWriter.WriteAsync(
            legacyRoom,
            new WorkflowStatusView("Succeeded", [], [], null),
            TestContext.Current.CancellationToken);
        await TerminalSentinelWriter.WriteAsync(
            currentRoom,
            new WorkflowStatusView("Succeeded", [], [], null, TerminalAt: "2026-08-20T09:30:00.0000000Z"),
            TestContext.Current.CancellationToken);

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{\"include_terminal\": true}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<FleetStatusResponse>(result.Text)!.Rooms;

        Assert.NotNull(rooms);
        var legacy = rooms!.First(r => r.Name == "legacy-sentinel-room");
        Assert.Null(legacy.TerminalAt);
        // Wire-level: omitted, never emitted null -- the omission rests on JsonIgnoreCondition and
        // only a serialized assertion catches that attribute breaking.
        Assert.DoesNotContain("\"terminalAt\"", JsonSerializer.Serialize(legacy));

        // The arm that proves the assertion above is about the ABSENT field and not about the fast
        // path never carrying one.
        var current = rooms.First(r => r.Name == "current-sentinel-room");
        Assert.Equal("2026-08-20T09:30:00.0000000Z", current.TerminalAt);
    }

    /// <summary>
    /// Writes journal lines carrying chosen writer stamps — <see cref="FlowEventLogWriter"/> stamps
    /// <c>DateTime.UtcNow</c>, so no test built through it can tell a run's ending apart from the
    /// moment its fixture was written. Same wire contract, same one-complete-line-per-entry shape.
    /// </summary>
    private static async Task WriteStampedJournalAsync(
        string logPath, params (FlowEvent Event, DateTime Stamp)[] entries)
    {
        var text = string.Concat(entries.Select(entry =>
            JsonSerializer.Serialize(
                (LogEntry)new LogEntry.FlowLogEntry(entry.Event, entry.Stamp),
                typeof(LogEntry),
                FlowEventLogJson.Options) + "\n"));

        await File.WriteAllTextAsync(logPath, text, TestContext.Current.CancellationToken);
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    /// <summary>
    /// Issue #1391 fast-path test: a persisted claude snapshot with NO live rooms at all projects a
    /// <c>vendors[]</c> entry with <c>liveLanes: 0</c> -- proving the block is not gated on a room
    /// existing, unlike every field ABOVE this one in the shape that reads a Running room's own
    /// bindings.
    /// </summary>
    [Fact]
    public async Task CallAsync_PersistedSnapshotNoLiveRooms_ProjectsVendorsEntryWithZeroLiveLanes()
    {
        var snapshot = new VendorUsageSnapshot(
            "agy",
            new DateTimeOffset(2026, 8, 28, 20, 0, 0, TimeSpan.Zero),
            Caveat: null,
            // Name carries the sense of the number (#1869 review) -- agy's own "Remaining" survives
            // only in rawLine, which the projection passes through verbatim.
            [new VendorUsageWindow("Gemini Models · Weekly Limit", 28, new DateTimeOffset(2026, 8, 29, 19, 34, 12, TimeSpan.Zero), "Gemini Models\tWeekly Limit Remaining\t72%\t2026-08-29T19:34:12Z")]);
        var snapshotPath = BatonPaths.VendorUsageSnapshotFile("agy");
        Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
        await File.WriteAllTextAsync(snapshotPath, JsonSerializer.Serialize(snapshot), TestContext.Current.CancellationToken);

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        var response = JsonSerializer.Deserialize<FleetStatusResponse>(result.Text)!;
        Assert.Empty(response.Rooms);
        var agyEntry = Assert.Single(response.Vendors!);
        Assert.Equal("agy", agyEntry.Adapter);
        Assert.Equal(0, agyEntry.LiveLanes);
        Assert.Equal(28, agyEntry.Windows[0].PercentUsed);
    }

    /// <summary>No snapshot has ever been harvested -- `vendors` absent, never an empty array.</summary>
    [Fact]
    public async Task CallAsync_NoHarvestedSnapshot_OmitsVendorsKey()
    {
        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.DoesNotContain("\"vendors\"", result.Text);
    }

    /// <summary>The description a conductor reads is the tool's only account of what `stale` means, so
    /// the tick count in it has to be the one the daemon actually uses -- hard-coded, it would lie the
    /// day that constant changed. The second assertion is the control: it fails if the number were
    /// spelled out in words or transcribed rather than interpolated.</summary>
    [Fact]
    public void Description_InterpolatesTheStalenessThreshold_RatherThanSpellingItOut()
    {
        var description = new FleetStatusTool().Description;

        Assert.Contains($"{FleetProjectionWriter.StaleAfterTicks} of its tick intervals", description);
        Assert.DoesNotContain("three of its tick intervals", description);
    }
}
