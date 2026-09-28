using Baton.Dispatch;
using Baton.Accounting;
using Baton.Domain;
using Baton.Mutation;
using Baton.Outcomes;
using Baton.Projection;
using Baton.Status;
using Baton.Store;
using Baton.Tests.TestSupport;

namespace Baton.Tests.EndToEnd;

/// <summary>
/// #2134: both polarities of <c>spec/baton.md</c> §3's "The grace turn" (its own account governs, not
/// restated here). Real git, fake dispatcher — same split <c>TimeoutOnMutatedWorkspaceEndToEndTests</c>
/// uses and for the same reason: the dirty-tree probe shells out to real git, so only a real tree can
/// discriminate its answer, while the vendor CLI itself is stood in for so each polarity is
/// deterministic rather than dependent on an actual model run.
/// </summary>
public sealed class GraceTurnEndToEndTests
{
    private static readonly StepId Implement = new("implement");
    private static readonly StepId SiblingOne = new("sibling-one");
    private static readonly StepId SiblingTwo = new("sibling-two");
    private static readonly StepId SiblingThree = new("sibling-three");

    // #1706: the billed component on claude is cache_creation -- mirrors
    // MutationInterfaceTests.StartWorkflowAsync_arrests_an_execution_that_crosses_its_token_budget's own
    // fixture line.
    private const string PrimaryArrestingUsageLine =
        """{"type":"assistant","message":{"usage":{"input_tokens":2,"cache_creation_input_tokens":700000,"cache_read_input_tokens":500000,"output_tokens":3}}}""";

    // Well above GraceTurn.TokenBudget (30,000) so the grace dispatch's own, far smaller monitor
    // arrests it in turn.
    private const string GraceExceedingUsageLine =
        """{"type":"assistant","message":{"usage":{"input_tokens":2,"cache_creation_input_tokens":40000,"cache_read_input_tokens":0,"output_tokens":3}}}""";

    [Fact]
    public async Task A_grace_turn_that_commits_is_recorded_clean_and_the_room_still_settles_Indeterminate()
    {
        var run = await RunArrestedLaneAsync(graceShouldCommit: true);
        try
        {
            var step = run.FinalState.Steps.Single(s => s.StepId == Implement);

            // Never Succeeded, whatever the grace turn did -- the arrest it precedes is the one thing
            // that decides the room, unchanged from every other budget arrest.
            Assert.Equal(WorkflowOutcome.Indeterminate, WorkflowOutcome.Describe(run.FinalState));
            Assert.Empty(run.Events.OfType<FlowEvent.ExecutionSucceeded>());
            Assert.Single(run.Events.OfType<FlowEvent.ExecutionArrested>());

            var claim = Assert.Single(run.Events.OfType<FlowEvent.GraceTurnClaimed>());
            var completion = Assert.Single(run.Events.OfType<FlowEvent.GraceTurnCompleted>());
            var safety = Assert.Single(run.Events.OfType<FlowEvent.GraceTurnSafetyRecorded>());
            Assert.True(safety.WorkspaceCleanAfter);
            Assert.Equal(CoreExitReason.Natural, completion.ExitReason);
            Assert.Null(completion.ArrestReason);
            Assert.Equal(claim.GraceExecutionId, completion.GraceExecutionId);
            Assert.Equal(claim.GraceExecutionId, safety.GraceExecutionId);
            Assert.NotEqual(claim.ParentExecutionId, claim.GraceExecutionId);
            Assert.Equal("grace-turn", claim.Request.Limits?.TimeoutSource);
            var orderedEvents = run.Events.ToList();
            Assert.True(orderedEvents.IndexOf(claim) < orderedEvents.IndexOf(completion));
            Assert.True(orderedEvents.IndexOf(completion) < orderedEvents.IndexOf(safety));
            Assert.True(orderedEvents.IndexOf(safety) < orderedEvents.FindIndex(e => e is FlowEvent.ExecutionArrested));

            Assert.Equal(2, run.Dispatcher.CallCount);
            Assert.Equal(2, run.Dispatcher.Requests.Count);
            Assert.NotNull(run.Dispatcher.Requests[0].Limits);
            Assert.NotEqual(run.Dispatcher.Requests[0].ExecutionId, run.Dispatcher.Requests[1].ExecutionId);
            Assert.NotNull(run.Dispatcher.Requests[1].Limits);
            Assert.True(RepositoryIsClean(run.Workspace));
        }
        finally
        {
            run.Cleanup();
        }
    }

    [Fact]
    public async Task A_grace_turn_that_exceeds_its_own_cap_is_arrested_with_the_dirty_tree_recorded()
    {
        var run = await RunArrestedLaneAsync(graceShouldCommit: false);
        try
        {
            var step = run.FinalState.Steps.Single(s => s.StepId == Implement);

            Assert.Equal(WorkflowOutcome.Indeterminate, WorkflowOutcome.Describe(run.FinalState));
            Assert.Empty(run.Events.OfType<FlowEvent.ExecutionSucceeded>());
            Assert.Single(run.Events.OfType<FlowEvent.ExecutionArrested>());

            var claim = Assert.Single(run.Events.OfType<FlowEvent.GraceTurnClaimed>());
            var completion = Assert.Single(run.Events.OfType<FlowEvent.GraceTurnCompleted>());
            var safety = Assert.Single(run.Events.OfType<FlowEvent.GraceTurnSafetyRecorded>());
            Assert.False(safety.WorkspaceCleanAfter);
            Assert.Equal(CoreExitReason.CancelRequested, completion.ExitReason);
            Assert.Equal(ArrestReason.TokenBudget, completion.ArrestReason);
            Assert.Equal(claim.GraceExecutionId, completion.GraceExecutionId);

            Assert.Equal(2, run.Dispatcher.CallCount);
            Assert.False(RepositoryIsClean(run.Workspace));
        }
        finally
        {
            run.Cleanup();
        }
    }

    [Fact]
    public async Task No_grace_turn_runs_for_a_read_shaped_role_even_with_a_dirty_workspace()
    {
        // #2029's own split, extended -- the ONE gate this test isolates from the two above, which
        // both leave VerifiesWorkspace at its Process default (true).
        var run = await RunArrestedLaneAsync(graceShouldCommit: true, verifiesWorkspace: false);
        try
        {
            Assert.Empty(run.Events.OfType<FlowEvent.GraceTurnClaimed>());
            Assert.Single(run.Events.OfType<FlowEvent.ExecutionArrested>());
            // The fake dispatcher's second call is what a grace turn would have made -- it never came.
            Assert.Equal(1, run.Dispatcher.CallCount);
            Assert.False(RepositoryIsClean(run.Workspace));
        }
        finally
        {
            run.Cleanup();
        }
    }

    [Fact]
    public async Task A_grace_turn_spawn_failure_is_unresolved_and_is_never_retried()
    {
        var run = await RunArrestedLaneAsync(graceShouldCommit: false, graceSpawnFails: true);
        try
        {
            Assert.Equal(WorkflowOutcome.Running, WorkflowOutcome.Describe(run.FinalState));
            Assert.Empty(run.Events.OfType<FlowEvent.ExecutionArrested>());

            var claim = Assert.Single(run.Events.OfType<FlowEvent.GraceTurnClaimed>());
            Assert.Empty(run.Events.OfType<FlowEvent.GraceTurnCompleted>());
            Assert.Empty(run.Events.OfType<FlowEvent.GraceTurnSafetyRecorded>());
            Assert.NotEqual(claim.ParentExecutionId, claim.GraceExecutionId);
            Assert.Equal("grace-turn", claim.Request.Limits?.TimeoutSource);

            Assert.Equal(2, run.Dispatcher.CallCount);
            Assert.False(RepositoryIsClean(run.Workspace));

            // Restart must discover the durable spend claim even though no Core lifecycle was
            // recorded for the child. A missing child completion is unresolved spend, not permission
            // to launch the original parent or another grace child.
            var durableSnapshot = await new FlowEventLogReader(Path.Combine(run.RoomDirectory, "flow.jsonl"))
                .ReadSnapshotAsync(TestContext.Current.CancellationToken);
            var projection = StateProjector.ProjectAndCheckpoint(
                durableSnapshot.FlowEvents, run.Snapshot, logByteOffset: durableSnapshot.ByteOffset);
            var (started, exited) = CoreEventAggregation.Merge(
                projection.Checkpoint.State.CoreStartedExecutionIds,
                projection.Checkpoint.State.CoreExitedByExecutionId,
                durableSnapshot.CoreEvents);
            ProjectionCheckpointStore.Save(run.RoomDirectory, projection.Checkpoint with
            {
                ByteOffset = durableSnapshot.ByteOffset,
                State = projection.Checkpoint.State with
                {
                    CoreStartedExecutionIds = started,
                    CoreExitedByExecutionId = exited,
                },
            });
            var logPath = Path.Combine(run.RoomDirectory, "flow.jsonl");
            var completeJournal = await File.ReadAllBytesAsync(logPath, TestContext.Current.CancellationToken);
            var newline = Array.IndexOf(completeJournal, (byte)'\n');
            Assert.True(newline >= 0);
            var shortenedJournal = completeJournal[..(newline + 1)];
            await File.WriteAllBytesAsync(logPath, shortenedJournal, TestContext.Current.CancellationToken);
            var shortenedBytesBeforeRefusal = await File.ReadAllBytesAsync(logPath, TestContext.Current.CancellationToken);
            var shortenedWriter = new FlowEventLogWriter(logPath);
            var shortenedDispatcher = new GraceTurnCoreDispatcher(
                run.Workspace, PrimaryArrestingUsageLine, graceShouldCommit: true,
                GraceExceedingUsageLine, graceSpawnFails: false);
            await Assert.ThrowsAsync<FlowEventLogReadException>(() => MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-2134"), run.RoomDirectory, run.Snapshot, run.Bindings, run.ArtifactsRoot,
                new FlowEventLogReader(logPath), shortenedWriter, shortenedDispatcher,
                cancellationToken: TestContext.Current.CancellationToken));
            await shortenedWriter.DisposeAsync();
            Assert.Equal(shortenedBytesBeforeRefusal,
                await File.ReadAllBytesAsync(logPath, TestContext.Current.CancellationToken));
            Assert.Equal(0, shortenedDispatcher.CallCount);
            await File.WriteAllBytesAsync(logPath, completeJournal, TestContext.Current.CancellationToken);

            var recoveryWriter = new FlowEventLogWriter(Path.Combine(run.RoomDirectory, "flow.jsonl"));
            var recoveryDispatcher = new GraceTurnCoreDispatcher(
                run.Workspace, PrimaryArrestingUsageLine, graceShouldCommit: true,
                GraceExceedingUsageLine, graceSpawnFails: false);
            var recoveredState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-2134"), run.RoomDirectory, run.Snapshot, run.Bindings, run.ArtifactsRoot,
                new FlowEventLogReader(Path.Combine(run.RoomDirectory, "flow.jsonl")), recoveryWriter, recoveryDispatcher,
                cancellationToken: TestContext.Current.CancellationToken);
            await recoveryWriter.DisposeAsync();
            Assert.Equal(WorkflowOutcome.Running, WorkflowOutcome.Describe(recoveredState));
            Assert.Equal(0, recoveryDispatcher.CallCount);

            var recoveredReader = new FlowEventLogReader(logPath);
            var recoveredEvents = await recoveredReader.ReadAllAsync(TestContext.Current.CancellationToken);
            var recoveredEntries = await recoveredReader.ReadAllEntriesWithTimestampsAsync(TestContext.Current.CancellationToken);
            var unresolved = Assert.Single(recoveredEvents.OfType<FlowEvent.GraceTurnSpendUnresolved>());
            Assert.Equal(claim.ParentExecutionId, unresolved.ParentExecutionId);
            Assert.Equal(claim.GraceExecutionId, unresolved.GraceExecutionId);
            var repository = RepositoryIdentity.From("https://github.com/example/grace.git", null)!;
            var unresolvedQuota = Assert.Single(QuotaLedgerStore.BuildEntries(recoveredEntries, run.RoomDirectory),
                item => item.Execution == claim.GraceExecutionId.Value);
            var unresolvedCost = Assert.Single(CostLedgerStore.BuildEntries(recoveredEntries, run.RoomDirectory, repository),
                item => item.Execution == claim.GraceExecutionId.Value);
            Assert.Equal("Unresolved", unresolvedQuota.Outcome);
            Assert.Equal("Unresolved", unresolvedCost.Outcome);
            Assert.Null(unresolvedQuota.WallClockMs);
            Assert.Null(unresolvedCost.WallClockMs);
            Assert.Null(unresolvedCost.TokensIn);
            Assert.Null(unresolvedCost.ApiEquivalentUsd);

            var repeatedWriter = new FlowEventLogWriter(logPath);
            var repeatedDispatcher = new GraceTurnCoreDispatcher(
                run.Workspace, PrimaryArrestingUsageLine, graceShouldCommit: true,
                GraceExceedingUsageLine, graceSpawnFails: false);
            _ = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-2134"), run.RoomDirectory, run.Snapshot, run.Bindings, run.ArtifactsRoot,
                new FlowEventLogReader(logPath), repeatedWriter, repeatedDispatcher,
                cancellationToken: TestContext.Current.CancellationToken);
            await repeatedWriter.DisposeAsync();
            Assert.Equal(0, repeatedDispatcher.CallCount);
            Assert.Single((await new FlowEventLogReader(logPath).ReadAllAsync(TestContext.Current.CancellationToken))
                .OfType<FlowEvent.GraceTurnSpendUnresolved>());
            completeJournal = await File.ReadAllBytesAsync(logPath, TestContext.Current.CancellationToken);

            await File.AppendAllTextAsync(Path.Combine(run.RoomDirectory, "flow.jsonl"), "{\"eventType\":\"graceTurnClaimed\"",
                TestContext.Current.CancellationToken);
            var bytesBeforeTornClaimReplay = await File.ReadAllBytesAsync(
                Path.Combine(run.RoomDirectory, "flow.jsonl"), TestContext.Current.CancellationToken);
            var workspaceBeforeTornClaimReplay = await File.ReadAllBytesAsync(
                Path.Combine(run.Workspace, "left-behind.txt"), TestContext.Current.CancellationToken);
            var tornClaimWriter = new FlowEventLogWriter(Path.Combine(run.RoomDirectory, "flow.jsonl"));
            var tornClaimDispatcher = new GraceTurnCoreDispatcher(
                run.Workspace, PrimaryArrestingUsageLine, graceShouldCommit: true,
                GraceExceedingUsageLine, graceSpawnFails: false);
            await Assert.ThrowsAsync<FlowEventLogReadException>(() => MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-2134"), run.RoomDirectory, run.Snapshot, run.Bindings, run.ArtifactsRoot,
                new FlowEventLogReader(Path.Combine(run.RoomDirectory, "flow.jsonl")), tornClaimWriter, tornClaimDispatcher,
                cancellationToken: TestContext.Current.CancellationToken));
            await tornClaimWriter.DisposeAsync();
            Assert.Equal(bytesBeforeTornClaimReplay, await File.ReadAllBytesAsync(
                Path.Combine(run.RoomDirectory, "flow.jsonl"), TestContext.Current.CancellationToken));
            Assert.Equal(workspaceBeforeTornClaimReplay, await File.ReadAllBytesAsync(
                Path.Combine(run.Workspace, "left-behind.txt"), TestContext.Current.CancellationToken));
            Assert.Equal(0, tornClaimDispatcher.CallCount);
            await File.WriteAllBytesAsync(Path.Combine(run.RoomDirectory, "flow.jsonl"), completeJournal,
                TestContext.Current.CancellationToken);

            var tornCompletion = "{\"eventType\":\"graceTurnCompleted\"";
            await File.AppendAllTextAsync(Path.Combine(run.RoomDirectory, "flow.jsonl"), tornCompletion,
                TestContext.Current.CancellationToken);
            var bytesBeforeRejectedReplay = await File.ReadAllBytesAsync(
                Path.Combine(run.RoomDirectory, "flow.jsonl"), TestContext.Current.CancellationToken);
            var rejectedWriter = new FlowEventLogWriter(Path.Combine(run.RoomDirectory, "flow.jsonl"));
            var rejectedDispatcher = new GraceTurnCoreDispatcher(
                run.Workspace, PrimaryArrestingUsageLine, graceShouldCommit: true,
                GraceExceedingUsageLine, graceSpawnFails: false);
            await Assert.ThrowsAsync<FlowEventLogReadException>(() => MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-2134"), run.RoomDirectory, run.Snapshot, run.Bindings, run.ArtifactsRoot,
                new FlowEventLogReader(Path.Combine(run.RoomDirectory, "flow.jsonl")), rejectedWriter, rejectedDispatcher,
                cancellationToken: TestContext.Current.CancellationToken));
            await rejectedWriter.DisposeAsync();
            Assert.Equal(bytesBeforeRejectedReplay, await File.ReadAllBytesAsync(
                Path.Combine(run.RoomDirectory, "flow.jsonl"), TestContext.Current.CancellationToken));
            Assert.Equal(0, rejectedDispatcher.CallCount);
        }
        finally
        {
            run.Cleanup();
        }
    }

    [Fact]
    public async Task Live_grace_claim_does_not_end_the_pump_or_race_a_parent_arrest()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var workspace = Path.Combine(roomDirectory, "lane");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        Directory.CreateDirectory(workspace);
        TempGitRepository.InitWithEverythingCommitted(workspace);
        var remote = TempGitRepository.InitBareRepository(Path.Combine(roomDirectory, "origin.git"));
        TempGitRepository.AddRemote(workspace, "origin", remote);
        TempGitRepository.Push(workspace, "origin", "HEAD:refs/heads/main");
        RunGit(workspace, "branch", "--set-upstream-to", "origin/main");
        Assert.NotNull(await Baton.Workspaces.WorktreeProvisioner.CaptureGraceCheckpointAsync(
            workspace, TestContext.Current.CancellationToken));

        var snapshot = new WorkflowDefinitionSnapshot(
            new WorkflowDefinitionSnapshotId("snapshot-grace-live"),
            new WorkflowTemplateId("template-grace-live"),
            WorkflowTemplateVersion: 1,
            Steps:
            [
                new WorkflowStepDefinition(Implement, "implement", [], [], DependsOn: [], RetryPolicy: new RetryPolicy(1)),
                new WorkflowStepDefinition(SiblingOne, "sibling-one", [], [], DependsOn: [], RetryPolicy: new RetryPolicy(1)),
                new WorkflowStepDefinition(SiblingTwo, "sibling-two", [], [], DependsOn: [], RetryPolicy: new RetryPolicy(1)),
                new WorkflowStepDefinition(SiblingThree, "sibling-three", [], [], DependsOn: [SiblingTwo], RetryPolicy: new RetryPolicy(1)),
            ]);
        var bindings = new Dictionary<string, WorkerBinding>(StringComparer.Ordinal)
        {
            ["implement"] = new WorkerBinding.Process(
                new WorkerContract("implement", [], [], []),
                new CoreDispatchTarget("implement-cli", ["-p", "original brief"], WorkingDirectory: workspace, PromptText: "original brief"),
                TimeSpan.FromSeconds(30), Adapter: "claude", TokenBudget: 1000, MaxToolSteps: 1,
                ChangesTree: true, VerifiesWorkspace: true),
            ["sibling-one"] = ReadOnlySibling("sibling-one", workspace),
            ["sibling-two"] = ReadOnlySibling("sibling-two", workspace),
            ["sibling-three"] = ReadOnlySibling("sibling-three", workspace),
        };

        var dispatcher = new LiveGraceConcurrencyDispatcher(workspace, PrimaryArrestingUsageLine);
        await using var innerWriter = new FlowEventLogWriter(logPath);
        var writer = new BlockingParentArrestWriter(innerWriter);
        var reader = new FlowEventLogReader(logPath);
        var registry = new InFlightExecutionRegistry();
        try
        {
            var workflowTask = MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-grace-live"), roomDirectory, snapshot, bindings, artifactsRoot,
                reader, writer, dispatcher, registry, cancellationToken: TestContext.Current.CancellationToken);

            await dispatcher.PrimaryStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await dispatcher.SiblingOneStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await dispatcher.SiblingTwoStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            var graceOrClaimOrArrest = await Task.WhenAny(
                    dispatcher.GraceStarted.Task, writer.GraceClaimWritten.Task,
                    writer.ParentArrestBlocked.Task)
                .WaitAsync(TimeSpan.FromSeconds(25), TestContext.Current.CancellationToken);
            if (ReferenceEquals(graceOrClaimOrArrest, writer.ParentArrestBlocked.Task))
            {
                var earlyEvents = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
                Assert.Fail($"Parent terminal append began before grace claim; durable events: {string.Join(", ", earlyEvents.Select(item => item.GetType().Name))}.");
            }
            if (ReferenceEquals(graceOrClaimOrArrest, writer.GraceClaimWritten.Task))
            {
                await dispatcher.GraceStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            }

            var liveEntries = await reader.ReadAllEntriesWithTimestampsAsync(TestContext.Current.CancellationToken);
            var liveClaim = Assert.Single(liveEntries.OfType<LogEntry.FlowLogEntry>()
                .Select(item => item.Event).OfType<FlowEvent.GraceTurnClaimed>());
            Assert.Empty(liveEntries.OfType<LogEntry.FlowLogEntry>()
                .Select(item => item.Event).OfType<FlowEvent.GraceTurnSpendUnresolved>());
            var liveQuotaPath = Path.Combine(roomDirectory, "grace-live-quota.jsonl");
            var liveCostPath = Path.Combine(roomDirectory, "grace-live-cost.jsonl");
            var liveQuotaRows = QuotaLedgerStore.BuildEntries(liveEntries, roomDirectory);
            var liveCostRows = CostLedgerStore.BuildEntries(
                liveEntries, roomDirectory, RepositoryIdentity.From("https://github.com/example/grace.git", null)!);
            Assert.DoesNotContain(liveClaim.GraceExecutionId.Value, liveQuotaRows.Select(item => item.Execution));
            Assert.DoesNotContain(liveClaim.GraceExecutionId.Value, liveCostRows.Select(item => item.Execution));
            await QuotaLedgerStore.RebuildAsync(liveQuotaRows, liveQuotaPath, TestContext.Current.CancellationToken);
            await QuotaLedgerStore.RebuildAsync(liveQuotaRows, liveQuotaPath, TestContext.Current.CancellationToken);
            await CostLedgerStore.AppendAsync(liveCostRows, liveCostPath, TestContext.Current.CancellationToken);
            await CostLedgerStore.AppendAsync(liveCostRows, liveCostPath, TestContext.Current.CancellationToken);
            Assert.DoesNotContain(liveClaim.GraceExecutionId.Value,
                (await QuotaLedgerStore.ReadAllAsync(liveQuotaPath, TestContext.Current.CancellationToken)).Select(item => item.Execution));
            Assert.DoesNotContain(liveClaim.GraceExecutionId.Value,
                (await CostLedgerStore.ReadAllAsync(liveCostPath, TestContext.Current.CancellationToken)).Select(item => item.Execution));

            dispatcher.ReleaseSiblingOne.TrySetResult();
            await writer.FirstSiblingSucceeded.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.False(workflowTask.IsCompleted, "a live child claim must not return the pump while the child is blocked");
            Assert.Equal(1, dispatcher.GraceCallCount);

            dispatcher.ReleaseGrace.TrySetResult();
            await writer.ParentArrestBlocked.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            var afterSafety = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Single(afterSafety.OfType<FlowEvent.GraceTurnSafetyRecorded>(), item => item.ParentExecutionId == dispatcher.ParentExecutionId);
            dispatcher.ReleaseSiblingTwo.TrySetResult();
            await writer.SecondSiblingSucceeded.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await dispatcher.SiblingThreeStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.False(workflowTask.IsCompleted, "the live dispatch still owns its pending parent terminal append");
            Assert.Equal(1, writer.ParentArrestAttempts);

            writer.ReleaseParentArrest.TrySetResult();
            dispatcher.ReleaseSiblingThree.TrySetResult();
            var state = await workflowTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Single(events.OfType<FlowEvent.GraceTurnClaimed>(), item => item.ParentExecutionId == dispatcher.ParentExecutionId);
            Assert.Empty(events.OfType<FlowEvent.GraceTurnSpendUnresolved>());
            Assert.Empty(events.OfType<FlowEvent.ExecutionFailed>());
            Assert.Single(events.OfType<FlowEvent.ExecutionArrested>(), item => item.ExecutionId == dispatcher.ParentExecutionId);
            var completedEntries = await reader.ReadAllEntriesWithTimestampsAsync(TestContext.Current.CancellationToken);
            var completedQuotaRows = QuotaLedgerStore.BuildEntries(completedEntries, roomDirectory);
            var completedCostRows = CostLedgerStore.BuildEntries(
                completedEntries, roomDirectory, RepositoryIdentity.From("https://github.com/example/grace.git", null)!);
            Assert.DoesNotContain(completedQuotaRows, item => item.Execution == liveClaim.GraceExecutionId.Value
                && item.Outcome == "Unresolved");
            Assert.DoesNotContain(completedCostRows, item => item.Execution == liveClaim.GraceExecutionId.Value
                && item.Outcome == "Unresolved");
            await QuotaLedgerStore.RebuildAsync(completedQuotaRows, liveQuotaPath,
                TestContext.Current.CancellationToken);
            await CostLedgerStore.AppendAsync(completedCostRows, liveCostPath, TestContext.Current.CancellationToken);
            Assert.DoesNotContain(await QuotaLedgerStore.ReadDistinctByExecutionAsync(
                liveQuotaPath, TestContext.Current.CancellationToken), item => item.Execution == liveClaim.GraceExecutionId.Value
                    && item.Outcome == "Unresolved");
            Assert.DoesNotContain(await CostLedgerStore.ReadAllAsync(
                liveCostPath, TestContext.Current.CancellationToken), item => item.Execution == liveClaim.GraceExecutionId.Value
                    && item.Outcome == "Unresolved");
            Assert.Equal(StepStatus.Succeeded, state.Steps.Single(step => step.StepId == SiblingOne).Status);
            Assert.Equal(StepStatus.Succeeded, state.Steps.Single(step => step.StepId == SiblingTwo).Status);
            Assert.Equal(StepStatus.Succeeded, state.Steps.Single(step => step.StepId == SiblingThree).Status);
            Assert.Equal(1, dispatcher.GraceCallCount);
        }
        finally
        {
            writer.ReleaseParentArrest.TrySetResult();
            dispatcher.ReleaseGrace.TrySetResult();
            dispatcher.ReleaseSiblingOne.TrySetResult();
            dispatcher.ReleaseSiblingTwo.TrySetResult();
            dispatcher.ReleaseSiblingThree.TrySetResult();
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task A_timeout_with_a_grace_commit_preserves_FinishedDuringTeardown_success()
    {
        var run = await RunArrestedLaneAsync(graceShouldCommit: true, primaryTimesOut: true);
        try
        {
            var step = Assert.Single(run.FinalState.Steps, item => item.StepId == Implement);
            var claim = Assert.Single(run.Events.OfType<FlowEvent.GraceTurnClaimed>());
            var completion = Assert.Single(run.Events.OfType<FlowEvent.GraceTurnCompleted>());
            var safety = Assert.Single(run.Events.OfType<FlowEvent.GraceTurnSafetyRecorded>());

            Assert.Equal(WorkflowOutcome.FinishedDuringTeardown, WorkflowOutcome.Describe(run.FinalState));
            Assert.True(step.FinishedDuringTeardown);
            Assert.True(safety.WorkspaceCleanAfter);
            Assert.Equal(CoreExitReason.Natural, completion.ExitReason);
            Assert.NotEqual(claim.ParentExecutionId, claim.GraceExecutionId);
            Assert.Single(run.Events.OfType<FlowEvent.ExecutionSucceeded>(), item =>
                item.ExecutionId == claim.ParentExecutionId && item.FinishedDuringTeardown);
            Assert.Empty(run.Events.OfType<FlowEvent.ExecutionArrested>());
            Assert.Equal(2, run.Dispatcher.CallCount);
        }
        finally
        {
            run.Cleanup();
        }
    }

    [Fact]
    public async Task Replay_after_grace_completion_without_safety_records_safety_without_redispatch()
    {
        var run = await RunArrestedLaneAsync(graceShouldCommit: false);
        try
        {
            var claim = Assert.Single(run.Events.OfType<FlowEvent.GraceTurnClaimed>());
            Assert.Single(run.Events.OfType<FlowEvent.GraceTurnCompleted>());
            await RewriteFlowJournalAsync(run.RoomDirectory, run.Snapshot,
                run.Events.Where(item => item is not FlowEvent.GraceTurnSafetyRecorded
                    && item is not FlowEvent.ExecutionArrested).ToArray());

            await using var writer = new FlowEventLogWriter(Path.Combine(run.RoomDirectory, "flow.jsonl"));
            var dispatcher = new GraceTurnCoreDispatcher(
                run.Workspace, PrimaryArrestingUsageLine, graceShouldCommit: true,
                GraceExceedingUsageLine, graceSpawnFails: false);
            var state = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-2134"), run.RoomDirectory, run.Snapshot, run.Bindings, run.ArtifactsRoot,
                new FlowEventLogReader(Path.Combine(run.RoomDirectory, "flow.jsonl")), writer, dispatcher,
                cancellationToken: TestContext.Current.CancellationToken);
            var replayed = await new FlowEventLogReader(Path.Combine(run.RoomDirectory, "flow.jsonl"))
                .ReadAllAsync(TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowOutcome.Indeterminate, WorkflowOutcome.Describe(state));
            Assert.Single(replayed.OfType<FlowEvent.GraceTurnSafetyRecorded>(), item => item.GraceExecutionId == claim.GraceExecutionId);
            Assert.Single(replayed.OfType<FlowEvent.ExecutionArrested>(), item => item.ExecutionId == claim.ParentExecutionId);
            Assert.Empty(replayed.OfType<FlowEvent.ExecutionFailed>());
            Assert.Equal(0, dispatcher.CallCount);
        }
        finally
        {
            run.Cleanup();
        }
    }

    [Fact]
    public async Task Replay_after_grace_safety_without_parent_arrest_appends_the_monitor_arrest_once()
    {
        var run = await RunArrestedLaneAsync(graceShouldCommit: false);
        try
        {
            var claim = Assert.Single(run.Events.OfType<FlowEvent.GraceTurnClaimed>());
            Assert.Single(run.Events.OfType<FlowEvent.GraceTurnSafetyRecorded>());
            await RewriteFlowJournalAsync(run.RoomDirectory, run.Snapshot,
                run.Events.Where(item => item is not FlowEvent.ExecutionArrested).ToArray());

            await using var writer = new FlowEventLogWriter(Path.Combine(run.RoomDirectory, "flow.jsonl"));
            var dispatcher = new GraceTurnCoreDispatcher(
                run.Workspace, PrimaryArrestingUsageLine, graceShouldCommit: true,
                GraceExceedingUsageLine, graceSpawnFails: false);
            var state = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-2134"), run.RoomDirectory, run.Snapshot, run.Bindings, run.ArtifactsRoot,
                new FlowEventLogReader(Path.Combine(run.RoomDirectory, "flow.jsonl")), writer, dispatcher,
                cancellationToken: TestContext.Current.CancellationToken);
            var replayed = await new FlowEventLogReader(Path.Combine(run.RoomDirectory, "flow.jsonl"))
                .ReadAllAsync(TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowOutcome.Indeterminate, WorkflowOutcome.Describe(state));
            Assert.Single(replayed.OfType<FlowEvent.GraceTurnSafetyRecorded>(), item => item.GraceExecutionId == claim.GraceExecutionId);
            Assert.Single(replayed.OfType<FlowEvent.ExecutionArrested>(), item => item.ExecutionId == claim.ParentExecutionId);
            Assert.Empty(replayed.OfType<FlowEvent.ExecutionFailed>());
            Assert.Equal(0, dispatcher.CallCount);
        }
        finally
        {
            run.Cleanup();
        }
    }

    private static WorkerBinding.Process ReadOnlySibling(string worker, string workspace) =>
        new(new WorkerContract(worker, [], [], []), new CoreDispatchTarget(worker, [], WorkingDirectory: workspace),
            TimeSpan.FromSeconds(30), VerifiesWorkspace: false);

    private static async Task WaitForCancellationAsync(CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => completion.TrySetResult());
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
    }

    private sealed class LiveGraceConcurrencyDispatcher(string workspace, string primaryUsageLine) : ICoreDispatcher
    {
        private int graceCallCount;
        public TaskCompletionSource PrimaryStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource GraceStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SiblingOneStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SiblingTwoStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SiblingThreeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseGrace { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSiblingOne { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSiblingTwo { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSiblingThree { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ExecutionId ParentExecutionId { get; private set; }
        public int GraceCallCount => Volatile.Read(ref graceCallCount);

        public async Task<CoreDispatchResult> DispatchAsync(
            ExecutionRequest request, CoreDispatchTarget target, CancellationToken cancellationToken = default)
        {
            if (request.StepId == Implement)
            {
                if (request.ExecutionId.Value.StartsWith("grace-", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref graceCallCount);
                    GraceStarted.TrySetResult();
                    await ReleaseGrace.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
                    return new CoreDispatchResult(0, CoreExitReason.Natural);
                }

                ParentExecutionId = request.ExecutionId;
                File.WriteAllText(Path.Combine(workspace, "left-behind.txt"), "unfinished parent work");
                PrimaryStarted.TrySetResult();
                target.OnStdoutLine?.Invoke(primaryUsageLine);
                target.OnStdoutLine?.Invoke(
                    """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash"},{"type":"tool_use","name":"Bash"}]}}""");
                if (!cancellationToken.IsCancellationRequested)
                {
                    throw new InvalidOperationException(
                        $"The test usage stimulus did not arrest the live parent (adapter={request.Adapter ?? "null"}, limits={request.Limits}).");
                }
                await WaitForCancellationAsync(cancellationToken).ConfigureAwait(false);
                return new CoreDispatchResult(-1, CoreExitReason.CancelRequested);
            }

            var release = request.StepId switch
            {
                var stepId when stepId == SiblingOne => ReleaseSiblingOne,
                var stepId when stepId == SiblingTwo => ReleaseSiblingTwo,
                _ => ReleaseSiblingThree,
            };
            (request.StepId switch
            {
                var stepId when stepId == SiblingOne => SiblingOneStarted,
                var stepId when stepId == SiblingTwo => SiblingTwoStarted,
                _ => SiblingThreeStarted,
            }).TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            return new CoreDispatchResult(0, CoreExitReason.Natural);
        }
    }

    private sealed class BlockingParentArrestWriter(IEventLogWriter inner) : IEventLogWriter
    {
        private int parentArrestAttempts;
        private int siblingSuccesses;
        public TaskCompletionSource ParentArrestBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource GraceClaimWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseParentArrest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstSiblingSucceeded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondSiblingSucceeded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ThirdSiblingSucceeded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ParentArrestAttempts => Volatile.Read(ref parentArrestAttempts);

        public async Task AppendAsync(FlowEvent flowEvent, CancellationToken cancellationToken = default)
        {
            if (flowEvent is FlowEvent.ExecutionArrested)
            {
                if (Interlocked.Increment(ref parentArrestAttempts) == 1)
                {
                    ParentArrestBlocked.TrySetResult();
                    await ReleaseParentArrest.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
                }
            }

            await inner.AppendAsync(flowEvent, cancellationToken);
            if (flowEvent is FlowEvent.GraceTurnClaimed)
            {
                GraceClaimWritten.TrySetResult();
            }
            if (flowEvent is FlowEvent.ExecutionSucceeded)
            {
                if (Interlocked.Increment(ref siblingSuccesses) == 1)
                {
                    FirstSiblingSucceeded.TrySetResult();
                }
                else if (siblingSuccesses == 2)
                {
                    SecondSiblingSucceeded.TrySetResult();
                }
                else
                {
                    ThirdSiblingSucceeded.TrySetResult();
                }
            }
        }
    }

    [Fact]
    public async Task A_grace_turn_preserves_the_binding_stdout_sink()
    {
        var receivedLines = new List<string>();
        var run = await RunArrestedLaneAsync(
            graceShouldCommit: true,
            onStdoutLine: receivedLines.Add);
        try
        {
            Assert.Contains("grace turn output", receivedLines);
        }
        finally
        {
            run.Cleanup();
        }
    }

    private sealed record LaneRun(
        FlowState FinalState,
        IReadOnlyList<FlowEvent> Events,
        string Workspace,
        string RoomDirectory,
        string ArtifactsRoot,
        WorkflowDefinitionSnapshot Snapshot,
        IReadOnlyDictionary<string, WorkerBinding> Bindings,
        GraceTurnCoreDispatcher Dispatcher,
        Action Cleanup);

    private static async Task<LaneRun> RunArrestedLaneAsync(
        bool graceShouldCommit,
        bool verifiesWorkspace = true,
        bool graceSpawnFails = false,
        Action<string>? onStdoutLine = null,
        bool primaryTimesOut = false)
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var workspace = Path.Combine(roomDirectory, "lane");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");

        Directory.CreateDirectory(workspace);
        TempGitRepository.InitWithEverythingCommitted(workspace);
        var remote = TempGitRepository.InitBareRepository(Path.Combine(roomDirectory, "origin.git"));
        TempGitRepository.AddRemote(workspace, "origin", remote);
        TempGitRepository.Push(workspace, "origin", "HEAD:refs/heads/main");
        RunGit(workspace, "branch", "--set-upstream-to", "origin/main");

        var snapshot = new WorkflowDefinitionSnapshot(
            new WorkflowDefinitionSnapshotId("snapshot-2134"),
            new WorkflowTemplateId("template-2134"),
            WorkflowTemplateVersion: 1,
            Steps: [new WorkflowStepDefinition(Implement, "implement", [], ["pr.md"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]);

        var bindings = new Dictionary<string, WorkerBinding>
        {
            ["implement"] = new WorkerBinding.Process(
                new WorkerContract("implement", [], [new ProducedOutput("pr.md")], []),
                new CoreDispatchTarget(
                    "vendor-cli", ["-p", "ORIGINAL BRIEF"], WorkingDirectory: workspace,
                    OnStdoutLine: onStdoutLine, PromptText: "ORIGINAL BRIEF"),
                TimeSpan.FromSeconds(30),
                Adapter: "claude",
                TokenBudget: 1000,
                ChangesTree: true,
                VerifiesWorkspace: verifiesWorkspace),
        };

        var dispatcher = new GraceTurnCoreDispatcher(
            workspace, PrimaryArrestingUsageLine, graceShouldCommit, GraceExceedingUsageLine, graceSpawnFails,
            artifactsRoot, primaryTimesOut);

        await using var writer = new FlowEventLogWriter(logPath);
        var reader = new FlowEventLogReader(logPath);

        var finalState = await MutationInterface.StartWorkflowAsync(
            new WorkflowId("wf-2134"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher,
            cancellationToken: TestContext.Current.CancellationToken);

        var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);

        return new LaneRun(finalState, events, workspace, roomDirectory, artifactsRoot, snapshot, bindings, dispatcher,
            () => DirectoryCleanup.DeleteRecursively(roomDirectory));
    }

    private static async Task RewriteFlowJournalAsync(
        string roomDirectory, WorkflowDefinitionSnapshot workflowSnapshot, IReadOnlyList<FlowEvent> events)
    {
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        await File.WriteAllBytesAsync(logPath, [], TestContext.Current.CancellationToken);
        await using var writer = new FlowEventLogWriter(logPath);
        foreach (var flowEvent in events)
        {
            await writer.AppendAsync(flowEvent, TestContext.Current.CancellationToken);
        }

        var durable = await new FlowEventLogReader(logPath).ReadSnapshotAsync(TestContext.Current.CancellationToken);
        var projection = StateProjector.ProjectAndCheckpoint(
            durable.FlowEvents, workflowSnapshot, logByteOffset: durable.ByteOffset);
        var (started, exited) = CoreEventAggregation.Merge(
            projection.Checkpoint.State.CoreStartedExecutionIds,
            projection.Checkpoint.State.CoreExitedByExecutionId,
            durable.CoreEvents);
        ProjectionCheckpointStore.Save(roomDirectory, projection.Checkpoint with
        {
            ByteOffset = durable.ByteOffset,
            State = projection.Checkpoint.State with
            {
                CoreStartedExecutionIds = started,
                CoreExitedByExecutionId = exited,
            },
        });
    }

    private static bool RepositoryIsClean(string workspace) =>
        string.IsNullOrWhiteSpace(RunGitCapturingOutput(workspace, "status", "--porcelain"));

    private static string RunGitCapturingOutput(string workingDirectory, params string[] args)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("git could not be started.");
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return stdout;
    }

    private static void RunGit(string workingDirectory, params string[] args)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("git could not be started.");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} exited {process.ExitCode}: {process.StandardError.ReadToEnd()}");
        }
    }

    /// <summary>
    /// First call stands in for the primary, arrested execution: leaves one uncommitted file behind,
    /// then feeds a usage line that crosses the binding's own 1,000-token budget and waits for the
    /// resulting <c>TokenBudgetMonitor.ArrestRequested</c> cancellation to land — the same pattern
    /// <c>MutationInterfaceTests.ArrestingCoreDispatcher</c> uses, mirrored here because this dispatcher
    /// also has to drive the SECOND, grace call. That call either commits the left-behind file (and
    /// returns <see cref="CoreExitReason.Natural"/>) or feeds a usage line that crosses the grace
    /// dispatch's own, far smaller budget and waits for ITS cancellation (returning
    /// <see cref="CoreExitReason.CancelRequested"/>, tree still dirty) — <c>graceShouldCommit</c> picks
    /// which.
    /// </summary>
    private sealed class GraceTurnCoreDispatcher(
        string workspace,
        string primaryUsageLine,
        bool graceShouldCommit,
        string graceExceedingUsageLine,
        bool graceSpawnFails,
        string? artifactsRoot = null,
        bool primaryTimesOut = false) : ICoreDispatcher
    {
        public int CallCount { get; private set; }
        public List<ExecutionRequest> Requests { get; } = [];

        public async Task<CoreDispatchResult> DispatchAsync(
            ExecutionRequest request, CoreDispatchTarget target, CancellationToken cancellationToken = default)
        {
            CallCount++;
            Requests.Add(request);
            if (CallCount == 1)
            {
                File.WriteAllText(Path.Combine(workspace, "left-behind.txt"), "written, never committed before the arrest");
                if (primaryTimesOut)
                {
                    var outputDirectory = Baton.Artifacts.ArtifactManager.ResolveOutputDirectory(
                        artifactsRoot ?? throw new InvalidOperationException("The timeout fixture needs an artifact root."),
                        request.ExecutionId);
                    Directory.CreateDirectory(outputDirectory);
                    File.WriteAllText(Path.Combine(outputDirectory, "pr.md"), "declared output was written before teardown");
                    return new CoreDispatchResult(-1, CoreExitReason.TimedOut);
                }

                target.OnStdoutLine?.Invoke(primaryUsageLine);
                await WaitForCancellationAsync(cancellationToken).ConfigureAwait(false);
                return new CoreDispatchResult(-1, CoreExitReason.CancelRequested);
            }

            if (graceSpawnFails)
            {
                throw new InvalidOperationException("grace worker could not be spawned");
            }

            if (graceShouldCommit)
            {
                target.OnStdoutLine?.Invoke("grace turn output");
                RunGit(workspace, "add", ".");
                RunGit(workspace, "commit", "-m", "fix: commit incomplete work under grace turn");
                RunGit(workspace, "push", "origin", "HEAD:refs/heads/main");
                return new CoreDispatchResult(0, CoreExitReason.Natural);
            }

            target.OnStdoutLine?.Invoke(graceExceedingUsageLine);
            await WaitForCancellationAsync(cancellationToken).ConfigureAwait(false);
            return new CoreDispatchResult(-1, CoreExitReason.CancelRequested);
        }

        private static async Task WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource();
            await using var registration = cancellationToken.Register(() => tcs.TrySetResult());
            // Not a timing expectation -- the ceiling only stops a regression from hanging the suite.
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(120), cancellationToken: CancellationToken.None);
        }
    }
}
