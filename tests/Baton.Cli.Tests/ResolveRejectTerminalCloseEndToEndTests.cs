using System.Text.Json;
using Baton.Cli.Tests.TestSupport;
using Baton.Domain;
using Baton.Projection;
using Baton.Status;
using Baton.Store;
using Baton.Templates;
using Baton.Vendors;

namespace Baton.Cli.Tests;

/// <summary>
/// #1877: a rejected capture must be closable without a redispatch. Two claims, kept apart on
/// purpose because one of them would otherwise satisfy the other silently:
/// <list type="number">
/// <item>a fresh <c>--reject</c> settles the room terminally (the projector half), and</item>
/// <item><c>baton resolve --close</c> admits a capture ALREADY rejected under the pre-#1877 rule
/// (the verb half) — the shape the evidence room `codex-1853-readonly-20260904-02` was stuck in,
/// which every verb refused.</item>
/// </list>
/// Every room here is built with <c>RetryPolicy(3)</c> and asserted to have retry budget left, so a
/// Terminal reading can only come from the foreclosure under test — with <c>RetryPolicy(1)</c> (what
/// the other resolve fixtures use) budget exhaustion alone would settle the room and none of these
/// tests could tell the fix from the fixture.
/// </summary>
public class ResolveRejectTerminalCloseEndToEndTests
{
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter> { ["shell"] = new ShellCommandWorkerAdapter() };

    private const string OutputName = "findings.md";

    [Fact]
    public async Task Rejecting_a_captured_response_with_retry_budget_remaining_settles_the_room_terminally()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"resolve-1877-reject-{Guid.NewGuid():N}");
        try
        {
            var (snapshot, executionId) = await SeedIndeterminateRoomAsync(roomDirectory);

            var options = ResolveOptionsParser.Parse(
                [roomDirectory, "--execution", executionId.Value, "--reject", "--reason", "prose, not the declared findings.md"]);
            var result = await ResolveCommand.ExecuteAsync(options, TestContext.Current.CancellationToken);

            var step = Assert.Single(result.State.Steps);
            Assert.Equal(StepStatus.Failed, step.Status);
            Assert.False(step.IndeterminateAwaitingResolution);

            // The room really did have budget left: Terminal below is the foreclosure's doing, not
            // an exhausted RetryPolicy the fixture would have produced with or without this fix.
            Assert.Equal(1, step.ConsecutiveFailureCount);
            Assert.True(step.RetryForeclosed);
            Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
            Assert.Equal(WorkflowOutcome.Failed, WorkflowOutcome.Describe(result.State));

            var view = WorkflowStatusProjector.Project(result.State, result.Snapshot, roomDirectory);
            Assert.False(Assert.Single(view.Steps).RetryEligible);
            Assert.True(view.Rejected);
            Assert.Equal("conductor", view.ResolvedBy);
            Assert.Contains("prose, not the declared findings.md", view.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("awaiting conductor resolution", view.Error, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>
    /// The discriminating control for the test above, one condition apart: the same room resolved
    /// with <c>--accept-capture</c> instead. Without it, "the room is Terminal and the step is
    /// foreclosed" would pass equally against a projector that foreclosed on ANY resolution — which
    /// would wrongly pin an accepted step too, and would make the whole retry story a lie rather
    /// than a narrowing.
    /// </summary>
    [Fact]
    public async Task Accepting_the_same_capture_settles_Succeeded_and_forecloses_nothing()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"resolve-1877-accept-{Guid.NewGuid():N}");
        try
        {
            var (_, executionId) = await SeedIndeterminateRoomAsync(roomDirectory, writeCapturedResponse: true);

            var options = ResolveOptionsParser.Parse([roomDirectory, "--execution", executionId.Value, "--accept-capture"]);
            var result = await ResolveCommand.ExecuteAsync(options, TestContext.Current.CancellationToken);

            var step = Assert.Single(result.State.Steps);
            Assert.Equal(StepStatus.Succeeded, step.Status);
            Assert.False(step.RetryForeclosed);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>
    /// The retroactive half, claimed and tested separately from the verb below: a room whose
    /// rejection was journaled under the PRE-#1877 rule re-projects terminal from its own journal
    /// alone, with no new event and no <c>resolve</c> invocation. This is what unsticks rooms that
    /// already exist — the evidence room among them.
    /// </summary>
    [Fact]
    public async Task A_room_rejected_under_the_pre_1877_rule_reprojects_terminal_from_its_journal_alone()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"resolve-1877-replay-{Guid.NewGuid():N}");
        try
        {
            var (snapshot, executionId) = await SeedIndeterminateRoomAsync(roomDirectory);
            var reader = new FlowEventLogReader(Path.Combine(roomDirectory, BatonPaths.FlowLogFileName));

            // Control arm, read first: WITHOUT the rejection this journal is Terminal-but-Indeterminate
            // (awaiting resolution) rather than settled -- so the assertions after the append below
            // are about the rejection, not about a fixture that was already in the target state.
            var beforeState = StateProjector.Project(
                await reader.ReadAllAsync(TestContext.Current.CancellationToken), snapshot);
            Assert.True(Assert.Single(beforeState.Steps).IndeterminateAwaitingResolution);
            Assert.Equal(WorkflowOutcome.Indeterminate, WorkflowOutcome.Describe(beforeState));

            await AppendPreFixRejectionAsync(roomDirectory, executionId);

            var afterState = StateProjector.Project(
                await reader.ReadAllAsync(TestContext.Current.CancellationToken), snapshot);
            var step = Assert.Single(afterState.Steps);
            Assert.Equal(1, step.ConsecutiveFailureCount);
            Assert.True(step.RetryForeclosed);
            Assert.Equal(WorkflowStatus.Terminal, afterState.Status);

            var view = WorkflowStatusProjector.Project(afterState, snapshot, roomDirectory);
            Assert.False(Assert.Single(view.Steps).RetryEligible);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>
    /// The verb half: `baton resolve --close` on the dangling shape, room-level (no `--execution`) —
    /// the invocation the issue reported as refusing with "no unresolved indeterminate capture" while
    /// naming the only step there was to close.
    /// </summary>
    [Fact]
    public async Task Closing_an_already_rejected_capture_succeeds_and_records_the_administrative_foreclosure()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"resolve-1877-close-{Guid.NewGuid():N}");
        try
        {
            var (_, executionId) = await SeedIndeterminateRoomAsync(roomDirectory);
            await AppendPreFixRejectionAsync(roomDirectory, executionId);

            var options = ResolveOptionsParser.Parse(
                [roomDirectory, "--close", "--reason", "intentional control probe; closing the evidence room"]);
            var result = await ResolveCommand.ExecuteAsync(options, TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
            Assert.Equal(StepStatus.Failed, Assert.Single(result.State.Steps).Status);

            var events = await new FlowEventLogReader(Path.Combine(roomDirectory, BatonPaths.FlowLogFileName))
                .ReadAllAsync(TestContext.Current.CancellationToken);
            var foreclosure = Assert.Single(events.OfType<FlowEvent.StepRetryForeclosed>());
            Assert.Equal(executionId, foreclosure.ForExecutionId);
            Assert.Equal("resolve --close", foreclosure.ForeclosedBy);
            Assert.Contains("intentional control probe", foreclosure.Reason, StringComparison.Ordinal);

            // Exactly-once still holds for the resolution fact itself: --close records a foreclosure,
            // never a second CaptureResolved (FlowEvent.CaptureResolved's own remarks).
            Assert.Single(events.OfType<FlowEvent.CaptureResolved>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>
    /// The discriminating control for the admission above: <c>--reject</c> against the same dangling
    /// room still refuses. Without this arm, the widened <c>--close</c> admission would pass equally
    /// against a guard that had simply stopped checking anything.
    /// </summary>
    [Fact]
    public async Task Rejecting_an_already_rejected_capture_still_refuses()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"resolve-1877-rereject-{Guid.NewGuid():N}");
        try
        {
            var (_, executionId) = await SeedIndeterminateRoomAsync(roomDirectory);
            await AppendPreFixRejectionAsync(roomDirectory, executionId);

            var options = ResolveOptionsParser.Parse(
                [roomDirectory, "--execution", executionId.Value, "--reject", "--reason", "again"]);
            var ex = await Assert.ThrowsAsync<CliArgumentException>(
                () => ResolveCommand.ExecuteAsync(options, TestContext.Current.CancellationToken));

            Assert.Contains("no unresolved indeterminate capture", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>
    /// The third verb the issue found refusing, asserted here as the CORRECT outcome rather than a
    /// defect: once the room is settled there is genuinely nothing for <c>baton cancel</c> to target,
    /// and it says so. Since #2073 "says so" is the idempotent no-op arm (exit 0, nothing written)
    /// rather than a refusal — pinned so a later widening of `cancel`'s targeting cannot quietly start
    /// journaling a cancellation against a settled room.
    /// </summary>
    [Fact]
    public async Task Cancelling_the_settled_room_reports_that_there_is_nothing_to_cancel()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"resolve-1877-cancel-{Guid.NewGuid():N}");
        try
        {
            var (_, executionId) = await SeedIndeterminateRoomAsync(roomDirectory);
            await AppendPreFixRejectionAsync(roomDirectory, executionId);
            var bindingsFilePath = await WriteBindingsAsync(roomDirectory);
            var journalBefore = await File.ReadAllTextAsync(Path.Combine(roomDirectory, "flow.jsonl"), TestContext.Current.CancellationToken);

            var result = await CancelCommand.ExecuteAsync(
                new CancelOptions(roomDirectory, ExecutionId: null, bindingsFilePath),
                Adapters,
                TestContext.Current.CancellationToken);

            Assert.True(result.CancelWasNoOp);
            Assert.Equal(MutationExitCodeResolver.Success, MutationExitCodeResolver.Resolve(result));
            Assert.Equal(journalBefore, await File.ReadAllTextAsync(Path.Combine(roomDirectory, "flow.jsonl"), TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>
    /// The other side of the widened <c>--close</c> admission, pinned because it is one predicate
    /// clause away from admitting: an ACCEPTED capture is not a dangling rejection.
    /// <see cref="ResolveCommand.IsDanglingRejected"/>'s <c>Status: Failed</c> clause and its
    /// <c>Accepted: false</c> read both have to hold for this to keep refusing — drop either and
    /// <c>--close</c> would foreclose a step whose work actually shipped.
    /// </summary>
    [Fact]
    public async Task Closing_an_accepted_capture_refuses()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"resolve-1877-close-accepted-{Guid.NewGuid():N}");
        try
        {
            var (_, executionId) = await SeedIndeterminateRoomAsync(roomDirectory, writeCapturedResponse: true);

            var accept = ResolveOptionsParser.Parse([roomDirectory, "--execution", executionId.Value, "--accept-capture"]);
            var accepted = await ResolveCommand.ExecuteAsync(accept, TestContext.Current.CancellationToken);
            // Read first: the step really is Succeeded, so the refusal below is about the accept, not
            // about a fixture that never resolved at all.
            Assert.Equal(StepStatus.Succeeded, Assert.Single(accepted.State.Steps).Status);

            var options = ResolveOptionsParser.Parse(
                [roomDirectory, "--execution", executionId.Value, "--close", "--reason", "closing work that shipped"]);
            var ex = await Assert.ThrowsAsync<CliArgumentException>(
                () => ResolveCommand.ExecuteAsync(options, TestContext.Current.CancellationToken));

            Assert.Contains("no unresolved indeterminate capture", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>
    /// Room-level <c>--close</c> while an execution is still LIVE: refused, with the original
    /// "refuses to guess" message. The #1877 dangling-rejection search runs on exactly the same
    /// zero-candidate path this room takes, so without this arm a search that stopped checking
    /// <see cref="StepStatus"/> would silently start settling rooms with a worker still running in
    /// them.
    /// </summary>
    [Fact]
    public async Task Room_level_close_while_an_execution_is_live_refuses()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"resolve-1877-close-live-{Guid.NewGuid():N}");
        try
        {
            var (snapshot, _) = await SeedLiveRoomAsync(roomDirectory);
            var events = await new FlowEventLogReader(Path.Combine(roomDirectory, BatonPaths.FlowLogFileName))
                .ReadAllAsync(TestContext.Current.CancellationToken);
            // Control arm, read first: the step really is Running, so the refusal is about liveness.
            Assert.Equal(StepStatus.Running, Assert.Single(StateProjector.Project(events, snapshot).Steps).Status);

            var options = ResolveOptionsParser.Parse([roomDirectory, "--close", "--reason", "closing a live room"]);
            var ex = await Assert.ThrowsAsync<CliArgumentException>(
                () => ResolveCommand.ExecuteAsync(options, TestContext.Current.CancellationToken));

            Assert.Contains("no unresolved indeterminate capture to resolve", ex.Message, StringComparison.Ordinal);
            Assert.Contains("refuses to guess", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>
    /// The same room as <see cref="SeedIndeterminateRoomAsync"/> stopped one event in: a worker
    /// accepted and started, nothing settled — the step reads <c>Running</c>.
    /// </summary>
    private static async Task<(WorkflowDefinitionSnapshot Snapshot, ExecutionId ExecutionId)> SeedLiveRoomAsync(
        string roomDirectory)
    {
        Directory.CreateDirectory(roomDirectory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("dispatch-fact-check"), 1,
            [new WorkflowStepDefinition(new StepId("fact-check"), "fact-check", [], [OutputName], [], new RetryPolicy(3))]);
        var snapshot = SnapshotBinder.Bind(definition);
        await SnapshotBinder.PersistAsync(
            snapshot, Path.Combine(roomDirectory, BatonPaths.SnapshotFileName), TestContext.Current.CancellationToken);

        var executionId = new ExecutionId($"exec-{Guid.NewGuid():N}");
        await using (var writer = new FlowEventLogWriter(Path.Combine(roomDirectory, BatonPaths.FlowLogFileName)))
        {
            await writer.AppendAsync(
                new FlowEvent.ExecutionRequestAccepted(new ExecutionRequest(
                    executionId, new WorkflowId("dispatch-fact-check"), new StepId("fact-check"), "fact-check", [], [],
                    TimeSpan.FromMinutes(20), [], new Dictionary<StepId, ExecutionId>())),
                TestContext.Current.CancellationToken);
            await writer.AppendAsync(
                new CoreEvent.ExecutionStarted(executionId, Pid: 28620), TestContext.Current.CancellationToken);
        }

        return (snapshot, executionId);
    }

    /// <summary>
    /// The evidence room's journal shape, sanitized and rebuilt event-for-event: a request accepted,
    /// a worker that started and exited 0, an <see cref="FlowEvent.ExecutionIndeterminate"/> carrying
    /// a captured response for the declared output it never wrote, and the
    /// <see cref="FlowEvent.ZeroOutputsDespiteSubstantialWork"/> tripwire alongside it. No vendor is
    /// involved and no adapter runs — the point is the journal, which is all the projector reads.
    /// </summary>
    private static async Task<(WorkflowDefinitionSnapshot Snapshot, ExecutionId ExecutionId)> SeedIndeterminateRoomAsync(
        string roomDirectory, bool writeCapturedResponse = false)
    {
        Directory.CreateDirectory(roomDirectory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("dispatch-fact-check"), 1,
            [new WorkflowStepDefinition(new StepId("fact-check"), "fact-check", [], [OutputName], [], new RetryPolicy(3))]);
        var snapshot = SnapshotBinder.Bind(definition);
        await SnapshotBinder.PersistAsync(
            snapshot, Path.Combine(roomDirectory, BatonPaths.SnapshotFileName), TestContext.Current.CancellationToken);

        var executionId = new ExecutionId($"exec-{Guid.NewGuid():N}");
        var logPath = Path.Combine(roomDirectory, BatonPaths.FlowLogFileName);
        await using (var writer = new FlowEventLogWriter(logPath))
        {
            await writer.AppendAsync(
                new FlowEvent.ExecutionRequestAccepted(new ExecutionRequest(
                    executionId, new WorkflowId("dispatch-fact-check"), new StepId("fact-check"), "fact-check", [], [],
                    TimeSpan.FromMinutes(20), [], new Dictionary<StepId, ExecutionId>())),
                TestContext.Current.CancellationToken);
            // The core half of the real journal, not decoration: ExecutionExited is what populates
            // CoreExitedByExecutionId, one of the aggregates ProjectionCheckpointStore.Load
            // fail-closes on — a fixture without it would replay a journal shape the evidence room
            // does not have.
            await writer.AppendAsync(
                new CoreEvent.ExecutionStarted(executionId, Pid: 28620), TestContext.Current.CancellationToken);
            await writer.AppendAsync(
                new CoreEvent.ExecutionExited(executionId, ExitCode: 0, CoreExitReason.Natural),
                TestContext.Current.CancellationToken);
            await writer.AppendAsync(
                new FlowEvent.ExecutionIndeterminate(
                    executionId,
                    $"Contract not satisfied: '{OutputName}' is missing. Response captured to "
                    + $"'{Baton.Outcomes.OutputMaterializer.CapturedResponseFileName}' — awaiting conductor resolution.",
                    Baton.Outcomes.OutputMaterializer.CapturedResponseFileName, [OutputName]),
                TestContext.Current.CancellationToken);
            await writer.AppendAsync(
                new FlowEvent.ZeroOutputsDespiteSubstantialWork(
                    executionId, "the worker's own final usage line reports 1 turn(s) and 101 output token(s)"),
                TestContext.Current.CancellationToken);
        }

        if (writeCapturedResponse)
        {
            var outputDirectory = Path.Combine(roomDirectory, "artifacts", $"execution_{executionId.Value}");
            Directory.CreateDirectory(outputDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(outputDirectory, Baton.Outcomes.OutputMaterializer.CapturedResponseFileName),
                Baton.Outcomes.OutputMaterializer.CapturedResponseHeader + "\n\nthe worker's prose answer",
                TestContext.Current.CancellationToken);
        }

        return (snapshot, executionId);
    }

    /// <summary>
    /// The rejection exactly as the evidence room already holds it — appended directly rather than
    /// through <c>baton resolve</c>, because the point is a room resolved BEFORE this fix, whose
    /// journal carries no foreclosure of its own.
    /// </summary>
    private static async Task AppendPreFixRejectionAsync(string roomDirectory, ExecutionId executionId)
    {
        await using var writer = new FlowEventLogWriter(Path.Combine(roomDirectory, BatonPaths.FlowLogFileName));
        await writer.AppendAsync(
            new FlowEvent.CaptureResolved(
                new StepId("fact-check"), executionId, Accepted: false,
                "Superseded control probe: intentional Code Mode-disabled run, prose only.", [OutputName]),
            TestContext.Current.CancellationToken);
    }

    private static async Task<string> WriteBindingsAsync(string roomDirectory)
    {
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["fact-check"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("fact-check", [], [new ProducedOutput(OutputName)], []),
                PromptTemplate: "exit 1", TimeSpan.FromSeconds(30)),
        };

        var path = Path.Combine(roomDirectory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config), TestContext.Current.CancellationToken);
        return path;
    }
}
