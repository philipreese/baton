using System.Text.Json;
using Baton.Vendors;
using Baton.Cli.Tests.TestSupport;
using Baton.Concurrency;
using Baton.Domain;
using Baton.Status;
using Baton.Store;
using Baton.Templates;

namespace Baton.Cli.Tests;

/// <summary>
/// M11 Phase 3's completion gate: the project → resolve → dispatch → await loop
/// <c>Baton.Tests.EndToEnd.WorkflowEndToEndTests</c> has exercised since M7, now reached through
/// <c>RunCommand.ExecuteAsync</c> — the exact call <c>Program.cs</c> makes — with a real
/// <see cref="IWorkerAdapter"/> resolving a real worker-binding config file, not a
/// <see cref="Baton.Mutation.WorkerBinding"/> constructed directly by the test. The shell-stub
/// adapter (<see cref="ShellCommandWorkerAdapter"/>) keeps every dispatch CI-safe while still
/// going through the real, managed <c>BatonTask</c> engine, same as <c>WorkflowEndToEndTests</c> itself.
/// </summary>
/// <remarks>
/// In <see cref="WorkingDirectoryCollection"/> because the #882 echo tests swap the process-global
/// <c>Console.Out</c> — the same category of hazard the collection already serializes for
/// <c>Directory.SetCurrentDirectory</c>.
/// </remarks>
[Collection(WorkingDirectoryCollection.Name)]
public class RunCommandEndToEndTests
{
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter> { ["shell"] = new ShellCommandWorkerAdapter() };

    private static readonly TimeSpan PumpCompletionTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_three_step_linear_workflow_runs_to_completion_through_RunCommand()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteThreeStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteThreeStepBindingsAsync(testRoot);
            var options = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);

            var finalState = (await RunCommand.ExecuteAsync(options, Adapters, cancellationToken: TestContext.Current.CancellationToken)).State;

            Assert.Equal(WorkflowStatus.Terminal, finalState.Status);
            Assert.Equal(3, finalState.Steps.Count);
            Assert.All(finalState.Steps, FlowAssert.Succeeded);

            var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
            var stepStateById = finalState.Steps.ToDictionary(s => s.StepId);
            await AssertOutputAsync(artifactsRoot, stepStateById[new StepId("architect")], "plan", "the-plan");
            await AssertOutputAsync(artifactsRoot, stepStateById[new StepId("critic")], "review", "the-plan");
            await AssertOutputAsync(artifactsRoot, stepStateById[new StepId("publisher")], "summary", "the-plan");

            // WorkflowId defaults to the bound snapshot's WorkflowTemplateId when not given.
            var reader = new FlowEventLogReader(Path.Combine(roomDirectory, "flow.jsonl"));
            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var requests = events.OfType<FlowEvent.ExecutionRequestAccepted>().Select(e => e.Request).ToList();
            Assert.Equal(3, requests.Count);
            Assert.All(requests, request => Assert.Equal("three-step-linear", request.WorkflowId.Value));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// spec/baton.md §8's writer, exercised through the exact call <c>Program.cs</c> makes for a bare
    /// <c>baton run</c> (no <c>--workspace</c> concept, unlike <c>baton dispatch</c>): the registration uses
    /// whatever <see cref="RunOptions.ProjectRootDirectory"/> was resolved to (the process cwd in
    /// production; passed explicitly here rather than mutating the shared process cwd for one test).
    /// <c>Register: true</c> here stands in for <c>--register</c> (#1657): the room directory below
    /// sits under the temp directory purely for test isolation, which is otherwise exactly the shape
    /// <see cref="RunOptions.Register"/>'s absence would now skip — <see cref="Running_a_bare_workflow_without_register_skips_the_temp_room_registration"/>
    /// covers that default.
    /// </summary>
    [Fact]
    public async Task Running_registers_the_room_into_the_multi_project_registry()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteThreeStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteThreeStepBindingsAsync(testRoot);
            var options = new RunOptions(
                workflowFilePath, bindingsFilePath, roomDirectory, ProjectRootDirectory: testRoot, Register: true);

            await RunCommand.ExecuteAsync(options, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            var entries = await RoomRegistryStore.ReadDistinctByRoomAsync(
                BatonPaths.RoomRegistryFile, TestContext.Current.CancellationToken);
            var entry = Assert.Single(entries, e => e.RoomPath == BatonPaths.RecordKey(roomDirectory));
            Assert.Equal(BatonPaths.RecordKey(testRoot), entry.ProjectRoot);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// #1657: the room-registry half of the fix at the exact call <c>Program.cs</c> makes for a bare
    /// <c>baton run</c> with no <c>--register</c> — a room under the temp directory (or, in real usage,
    /// a project's own <c>.scratch*</c>/<c>.baton</c> path) is a throwaway repro by default and never
    /// reaches the registry, even though the run itself still completes normally.
    /// </summary>
    [Fact]
    public async Task Running_a_bare_workflow_without_register_skips_the_temp_room_registration()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteThreeStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteThreeStepBindingsAsync(testRoot);
            var options = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory, ProjectRootDirectory: testRoot);

            var finalState = (await RunCommand.ExecuteAsync(
                options, Adapters, cancellationToken: TestContext.Current.CancellationToken)).State;

            Assert.Equal(WorkflowStatus.Terminal, finalState.Status);
            var entries = await RoomRegistryStore.ReadDistinctByRoomAsync(
                BatonPaths.RoomRegistryFile, TestContext.Current.CancellationToken);
            Assert.DoesNotContain(entries, e => e.RoomPath == BatonPaths.RecordKey(roomDirectory));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Running_again_against_the_same_room_directory_resumes_without_redispatching()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteThreeStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteThreeStepBindingsAsync(testRoot);
            var options = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);

            var firstRun = (await RunCommand.ExecuteAsync(options, Adapters, cancellationToken: TestContext.Current.CancellationToken)).State;
            Assert.All(firstRun.Steps, FlowAssert.Succeeded);

            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            var eventCountAfterFirstRun = (await new FlowEventLogReader(logPath).ReadAllAsync(TestContext.Current.CancellationToken)).Count;

            var secondRun = (await RunCommand.ExecuteAsync(options, Adapters, cancellationToken: TestContext.Current.CancellationToken)).State;

            Assert.Equal(WorkflowStatus.Terminal, secondRun.Status);
            Assert.All(secondRun.Steps, FlowAssert.Succeeded);

            var eventCountAfterSecondRun = (await new FlowEventLogReader(logPath).ReadAllAsync(TestContext.Current.CancellationToken)).Count;
            Assert.Equal(eventCountAfterFirstRun, eventCountAfterSecondRun);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task A_malformed_workflow_file_throws_a_typed_validation_exception()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(testRoot);
            var workflowFilePath = Path.Combine(testRoot, "workflow.json");
            await File.WriteAllTextAsync(workflowFilePath, "{ not valid json", TestContext.Current.CancellationToken);
            var bindingsFilePath = await WriteThreeStepBindingsAsync(testRoot);
            var options = new RunOptions(workflowFilePath, bindingsFilePath, Path.Combine(testRoot, "task"));

            await Assert.ThrowsAsync<WorkflowDefinitionValidationException>(
                () => RunCommand.ExecuteAsync(options, Adapters, cancellationToken: TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// A hand-authored bindings file reaches <c>baton run</c> without DispatchCommand.  It must reject
    /// Astra before a worktree or worker process can be provisioned, even when the supplied adapter
    /// registry would otherwise reject the unknown adapter later.
    /// </summary>
    [Fact]
    public async Task A_direct_run_refuses_an_Astra_binding_before_any_room_side_effect()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteThreeStepWorkflowAsync(testRoot);
            var bindingsFilePath = Path.Combine(testRoot, "bindings.json");
            var config = new Dictionary<string, WorkerBindingConfigEntry>
            {
                ["architect"] = new WorkerBindingConfigEntry(
                    "codex",
                    new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                    "must never run",
                    TimeSpan.FromSeconds(30),
                    Model: "gpt-6-astra"),
            };
            await File.WriteAllTextAsync(bindingsFilePath, JsonSerializer.Serialize(config), TestContext.Current.CancellationToken);

            var ex = await Assert.ThrowsAsync<ConductorOnlyWorkerModelException>(
                () => RunCommand.ExecuteAsync(
                    new RunOptions(
                        workflowFilePath, bindingsFilePath, roomDirectory, ProjectRootDirectory: testRoot, Register: true),
                    Adapters,
                    cancellationToken: TestContext.Current.CancellationToken));

            Assert.Contains("Astra is conductor-only", ex.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(roomDirectory));
            Assert.False(File.Exists(Path.Combine(roomDirectory, BatonPaths.SnapshotFileName)));
            Assert.False(Directory.Exists(Path.Combine(roomDirectory, "worktrees")));
            Assert.False(File.Exists(Path.Combine(roomDirectory, "flow.jsonl")));
            var registrations = await RoomRegistryStore.ReadDistinctByRoomAsync(
                BatonPaths.RoomRegistryFile, TestContext.Current.CancellationToken);
            Assert.DoesNotContain(registrations, entry => entry.RoomPath == BatonPaths.RecordKey(roomDirectory));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task A_malformed_bindings_file_throws_a_typed_config_exception()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        try
        {
            var workflowFilePath = await WriteThreeStepWorkflowAsync(testRoot);
            var bindingsFilePath = Path.Combine(testRoot, "bindings.json");
            await File.WriteAllTextAsync(bindingsFilePath, "{ not valid json", TestContext.Current.CancellationToken);
            var options = new RunOptions(workflowFilePath, bindingsFilePath, Path.Combine(testRoot, "task"));

            await Assert.ThrowsAsync<WorkerBindingConfigException>(() => RunCommand.ExecuteAsync(options, Adapters, cancellationToken: TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task A_bindings_entry_naming_an_unregistered_adapter_throws()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        try
        {
            var workflowFilePath = await WriteThreeStepWorkflowAsync(testRoot);
            var bindingsFilePath = Path.Combine(testRoot, "bindings.json");
            var config = new Dictionary<string, WorkerBindingConfigEntry>
            {
                ["architect"] = new WorkerBindingConfigEntry(
                    "not-registered",
                    new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                    "irrelevant",
                    TimeSpan.FromSeconds(30)),
            };
            await File.WriteAllTextAsync(bindingsFilePath, JsonSerializer.Serialize(config), TestContext.Current.CancellationToken);
            var options = new RunOptions(workflowFilePath, bindingsFilePath, Path.Combine(testRoot, "task"));

            await Assert.ThrowsAsync<UnknownWorkerAdapterException>(() => RunCommand.ExecuteAsync(options, Adapters, cancellationToken: TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    // ---------------------------------------------------------------------------------------
    // #628 — the named workflow file is not read when the room directory is already bound.
    // Resuming is intended (M15 Phase 1, #137); resuming a *different* template silently is not.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Resuming_a_room_directory_bound_to_a_different_template_is_refused()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var bindingsFilePath = await WriteThreeStepBindingsAsync(testRoot);
            var boundWorkflowPath = await WriteThreeStepWorkflowAsync(testRoot);
            await RunCommand.ExecuteAsync(
                new RunOptions(boundWorkflowPath, bindingsFilePath, roomDirectory),
                Adapters,
                cancellationToken: TestContext.Current.CancellationToken);

            var otherWorkflowPath = await WriteThreeStepWorkflowAsync(
                Path.Combine(testRoot, "other"), templateId: "some-other-task");

            var thrown = await Assert.ThrowsAsync<ResumedTemplateMismatchException>(
                () => RunCommand.ExecuteAsync(
                    new RunOptions(otherWorkflowPath, bindingsFilePath, roomDirectory),
                    Adapters,
                    cancellationToken: TestContext.Current.CancellationToken));

            Assert.Equal("three-step-linear", thrown.BoundTemplateId);
            Assert.Equal("some-other-task", thrown.NamedTemplateId);
            Assert.Equal(roomDirectory, thrown.RoomDirectoryPath);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task The_refusal_happens_before_anything_is_dispatched()
    {
        // The room directory is bound but has never run — the exact state `baton run` leaves behind
        // when it persists the snapshot (before the bindings file is even parsed) and then throws on
        // a malformed one. That state is what makes this test discriminate on ORDER: every step is
        // still pending, so a refusal placed after the mutation surface would dispatch the whole
        // workflow and leave a full log behind before raising. Bound-and-already-terminal, which is
        // the obvious way to write this, cannot tell the two placements apart — a terminal flow
        // dispatches nothing wherever the check sits.
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var bindingsFilePath = await WriteThreeStepBindingsAsync(testRoot);
            Directory.CreateDirectory(roomDirectory);
            var bound = SnapshotBinder.Bind(
                await WorkflowDefinitionParser.LoadFromFileAsync(
                    await WriteThreeStepWorkflowAsync(testRoot), TestContext.Current.CancellationToken));
            var snapshotPath = Path.Combine(roomDirectory, "snapshot.json");
            await SnapshotBinder.PersistAsync(bound, snapshotPath, TestContext.Current.CancellationToken);
            var snapshotBefore = await File.ReadAllTextAsync(snapshotPath, TestContext.Current.CancellationToken);

            var otherWorkflowPath = await WriteThreeStepWorkflowAsync(
                Path.Combine(testRoot, "other"), templateId: "some-other-task");
            await Assert.ThrowsAsync<ResumedTemplateMismatchException>(
                () => RunCommand.ExecuteAsync(
                    new RunOptions(otherWorkflowPath, bindingsFilePath, roomDirectory),
                    Adapters,
                    cancellationToken: TestContext.Current.CancellationToken));

            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            Assert.True(
                !File.Exists(logPath)
                    || (await new FlowEventLogReader(logPath).ReadAllAsync(TestContext.Current.CancellationToken)).Count == 0,
                "The refusal dispatched work before raising.");
            Assert.Equal(
                snapshotBefore,
                await File.ReadAllTextAsync(snapshotPath, TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task A_resume_naming_a_nonexistent_workflow_file_throws_validation_exception()
    {
        // Issue #653: The desktop no longer populates its workflow-file path (#1215 lifted that out of
        // a header TextBox into MainWindowViewModel.WorkflowTemplateFilePath) with bare template IDs.
        // A resume supplying a WorkflowFilePath to a file that does not exist throws WorkflowDefinitionValidationException
        // (an BatonFlowException) rather than silently skipping the template mismatch check.
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var bindingsFilePath = await WriteThreeStepBindingsAsync(testRoot);
            var boundWorkflowPath = await WriteThreeStepWorkflowAsync(testRoot);
            await RunCommand.ExecuteAsync(
                new RunOptions(boundWorkflowPath, bindingsFilePath, roomDirectory),
                Adapters,
                cancellationToken: TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<WorkflowDefinitionValidationException>(
                () => RunCommand.ExecuteAsync(
                    new RunOptions("three-step-linear", bindingsFilePath, roomDirectory),
                    Adapters,
                    cancellationToken: TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Resuming_with_the_same_template_from_a_different_file_still_succeeds()
    {
        // The control, and the polarity mirror of the refusal above: the two runs differ only in
        // whether the second file's template id matches. Without it, the refusal passes just as well
        // on a check keyed to the file path, which would break every legitimate resume from a copied
        // or regenerated workflow file.
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var bindingsFilePath = await WriteThreeStepBindingsAsync(testRoot);
            var boundWorkflowPath = await WriteThreeStepWorkflowAsync(testRoot);
            await RunCommand.ExecuteAsync(
                new RunOptions(boundWorkflowPath, bindingsFilePath, roomDirectory),
                Adapters,
                cancellationToken: TestContext.Current.CancellationToken);

            var samePath = await WriteThreeStepWorkflowAsync(Path.Combine(testRoot, "elsewhere"));

            var result = await RunCommand.ExecuteAsync(
                new RunOptions(samePath, bindingsFilePath, roomDirectory),
                Adapters,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
            Assert.All(result.State.Steps, FlowAssert.Succeeded);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task An_in_process_resume_that_names_no_workflow_file_is_unaffected()
    {
        // The second control. RunOptions.WorkflowFilePath is nullable precisely so an in-process
        // caller resuming a known room directory need not produce one (M15 Phase 1, #137) — nothing
        // was named, so there is no disagreement to refuse.
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var bindingsFilePath = await WriteThreeStepBindingsAsync(testRoot);
            var boundWorkflowPath = await WriteThreeStepWorkflowAsync(testRoot);
            await RunCommand.ExecuteAsync(
                new RunOptions(boundWorkflowPath, bindingsFilePath, roomDirectory),
                Adapters,
                cancellationToken: TestContext.Current.CancellationToken);

            var result = await RunCommand.ExecuteAsync(
                new RunOptions(WorkflowFilePath: null, bindingsFilePath, roomDirectory),
                Adapters,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task A_run_reports_whether_it_bound_the_named_template_or_resumed_a_snapshot()
    {
        // Refusing a mismatch leaves the matching resume still silent about which template ran, and
        // a terminal replay of an already-finished task is otherwise indistinguishable from a fresh
        // one: same status line, same exit code, no new events. This flag is what FlowStateReporter
        // says it with.
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var bindingsFilePath = await WriteThreeStepBindingsAsync(testRoot);
            var workflowFilePath = await WriteThreeStepWorkflowAsync(testRoot);
            var options = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);

            var fresh = await RunCommand.ExecuteAsync(options, Adapters, cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(fresh.ResumedFromSnapshot);

            var resumed = await RunCommand.ExecuteAsync(options, Adapters, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(resumed.ResumedFromSnapshot);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// #1605 review F1 (HIGH): the single-parked-lane shape — a quota-parked room resumed through
    /// <c>baton run</c>, with no other step in flight — settles a cancel and returns Terminal well
    /// inside <see cref="CancelRequestPoller.DefaultPollInterval"/> (2s) of the mark, so the finally
    /// block used to cancel the poller before its own next tick could ever consume the pending
    /// <c>cancel.request</c> file — a pending file left behind in a room whose cancel actually
    /// SUCCEEDED. Drives this through the real <see cref="RunCommand.ExecuteAsync"/> entry point
    /// (not <c>MutationInterface</c> directly, and not <c>InFlightExecutionRegistry.MarkArrestIntent</c>
    /// called in-process, which <c>QuotaParkCancelArrestTests</c> already covers) — the file channel
    /// end to end is this test's own scope.
    /// </summary>
    [Fact]
    public async Task A_cancel_request_against_a_resumed_parked_room_is_consumed_when_settling_the_park_terminates_the_run()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var (bindingsFilePath, executionId) = await WriteParkedRoomFixtureAsync(roomDirectory);
            var options = new RunOptions(WorkflowFilePath: null, bindingsFilePath, roomDirectory);

            var pumpTask = RunCommand.ExecuteAsync(options, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            // Wait for MutationInterface's own ConcurrencyGuard (flow.lock) to be held BY THE PUMP
            // ITSELF, not merely held by someone: a generic ConcurrencyGuard.IsHeld probe cannot tell
            // this pump's own long-lived hold apart from WorktreeWorkspaces.Provision's transient
            // acquire-then-release of the SAME lock file a few statements earlier in
            // RunCommand.ExecuteAsync (WorktreeWorkspaces.cs's own "worktree provisioning" holder
            // description). Pre-#1649, IsHeld could observe THAT hold, race ahead, and write the
            // request file before RunCommand's own CancelRequestFile.DeleteStalePendingRequestAsync
            // sweep (further down the same method, but still before this pump's real acquire) had run,
            // so the (then-unconditional) sweep deleted the just-written request out from under this
            // test (this is what made
            // A_cancel_request_against_a_resumed_parked_room_is_consumed_when_settling_the_park_terminates_the_run
            // ~40% flaky, misread once as an #1607 F1 regression — it reproduced unchanged at #1607's
            // own merge-base). Checking the holder sidecar's description instead of the bare lock
            // discriminates the two: it is only ever "baton run pump (pid N)" once THIS pump's own
            // acquire — which happens strictly after the sweep — has landed. #1649 (see
            // A_cancel_request_written_in_the_window_between_provisioning_and_the_stale_sweep_survives_it
            // below) separately made the production-side sweep itself discriminate a live concurrent
            // write from a genuinely stale one, so this wait is now belt-and-braces rather than the
            // only thing standing between this test and the flake.
            var lockDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (!(ConcurrencyGuard.ReadHolderInfo(roomDirectory).HolderDescription ?? string.Empty)
                .StartsWith("baton run pump", StringComparison.Ordinal))
            {
                Assert.True(DateTime.UtcNow < lockDeadline, "Timed out waiting for the pump to acquire flow.lock.");
                Assert.False(pumpTask.IsCompleted, "expected the pump to still be running (parked) when this check runs");
                await Task.Delay(20, TestContext.Current.CancellationToken); // wait-ok: poll interval inside a 20s-bounded loop, not the wait ceiling itself
            }

            await CancelRequestFile.WriteAsync(roomDirectory, executionId.Value, TestContext.Current.CancellationToken);

            var result = await pumpTask.WaitAsync(PumpCompletionTimeout, TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
            Assert.Equal(StepStatus.Cancelled, result.State.Steps.Single().Status);

            var requestPath = CancelRequestFile.GetPath(roomDirectory);
            Assert.False(File.Exists(requestPath), "expected the pending cancel.request to be consumed, not left behind");
            Assert.True(
                File.Exists($"{requestPath}.consumed"),
                "expected the final tick in RunCommand's finally block to consume the request once the park settled");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// #1649: the production race itself, closed. A concurrent <c>baton cancel</c> that lands its
    /// <c>cancel.request</c> write in the narrow window between <c>WorktreeWorkspaces.Provision</c> and
    /// <c>RunCommand</c>'s own stale-request sweep must be CONSUMED, not swept as if it were a leftover
    /// from a crashed prior pump. Hooks the internal test-only seam
    /// (<see cref="RunCommand.ExecuteAsync(RunOptions, IReadOnlyDictionary{string, IWorkerAdapter}, InFlightExecutionRegistry?, CancellationToken, Action{string, string}?, Func{Task}?)"/>)
    /// to write into that exact window deterministically, rather than racing real process timing to
    /// hit it the way <see cref="A_cancel_request_against_a_resumed_parked_room_is_consumed_when_settling_the_park_terminates_the_run"/>
    /// has to. Red on pre-#1649 code: the old unconditional sweep renamed the file to <c>.swept</c>
    /// before the pump's own poller ever ticked, so this assertion block (no pending file, a
    /// <c>.consumed</c> sibling, <see cref="StepStatus.Cancelled"/>) failed with the request found at
    /// <c>.swept</c> instead.
    /// </summary>
    [Fact]
    public async Task A_cancel_request_written_in_the_window_between_provisioning_and_the_stale_sweep_survives_it()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var (bindingsFilePath, executionId) = await WriteParkedRoomFixtureAsync(roomDirectory);
            var options = new RunOptions(WorkflowFilePath: null, bindingsFilePath, roomDirectory);
            var ct = TestContext.Current.CancellationToken;

            var result = await RunCommand.ExecuteAsync(
                options,
                Adapters,
                inFlightExecutions: null,
                cancellationToken: ct,
                onWorkerStdoutLine: null,
                testOnlyAfterProvisionBeforeStaleSweepAsync: () => CancelRequestFile.WriteAsync(roomDirectory, executionId.Value, ct))
                .WaitAsync(PumpCompletionTimeout, ct);

            Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
            Assert.Equal(StepStatus.Cancelled, result.State.Steps.Single().Status);

            var requestPath = CancelRequestFile.GetPath(roomDirectory);
            Assert.False(File.Exists(requestPath), "expected the cancel.request written mid-window to be gone");
            Assert.False(File.Exists($"{requestPath}.swept"), "expected the live request NOT to be swept as stale (#1649)");
            Assert.True(File.Exists($"{requestPath}.consumed"), "expected the live request to be consumed by the poller instead");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// Hand-writes a bound snapshot plus a quota-parked <c>ExecutionFailed</c>/<c>StepRetryScheduled</c>
    /// history directly to <c>flow.jsonl</c> — same shape as
    /// <c>StatusCommandEndToEndTests.WriteParkedStepFixtureAsync</c> — with a real (not faked)
    /// <see cref="DateTimeOffset.UtcNow"/>-based <c>RetryNotBefore</c> far enough out (1 hour) that
    /// the idle-deferral wait's own delay could never account for this test completing on its own;
    /// only the cancel mark can make it converge inside the 30s bound below.
    /// </summary>
    private static async Task<(string BindingsFilePath, ExecutionId ExecutionId)> WriteParkedRoomFixtureAsync(string roomDirectory)
    {
        Directory.CreateDirectory(roomDirectory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("parked-probe"),
            1,
            [new WorkflowStepDefinition(new StepId("implement"), "implement", [], ["out"], [], new RetryPolicy(3))]);
        var snapshot = SnapshotBinder.Bind(definition);
        await SnapshotBinder.PersistAsync(
            snapshot, Path.Combine(roomDirectory, "snapshot.json"), TestContext.Current.CancellationToken);

        var executionId = new ExecutionId("exec-parked-runcommand");
        var request = new ExecutionRequest(
            executionId,
            new WorkflowId("wf-parked"),
            new StepId("implement"),
            "implement",
            Inputs: [],
            Outputs: [],
            Timeout: TimeSpan.FromSeconds(30),
            Environment: [],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());
        var retryNotBefore = DateTimeOffset.UtcNow.AddHours(1);

        await using (var writer = new FlowEventLogWriter(Path.Combine(roomDirectory, "flow.jsonl")))
        {
            var ct = TestContext.Current.CancellationToken;
            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(request), ct);
            await writer.AppendAsync(
                new FlowEvent.ExecutionFailed(executionId, FailureClassification.ExhaustedUntil, "quota exhausted", retryNotBefore), ct);
            await writer.AppendAsync(
                new FlowEvent.StepRetryScheduled(new StepId("implement"), executionId, retryNotBefore, RetryDelayMs: (int)TimeSpan.FromHours(1).TotalMilliseconds), ct);
        }

        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["implement"] = new WorkerBindingConfigEntry(
                "shell",
                new WorkerContract("implement", [], [new ProducedOutput("out")], []),
                WriteFileCommand("out", "unused"),
                TimeSpan.FromSeconds(30)),
        };
        var bindingsPath = Path.Combine(roomDirectory, "bindings.json");
        await File.WriteAllTextAsync(bindingsPath, JsonSerializer.Serialize(config));

        return (bindingsPath, executionId);
    }

    private static async Task<string> WriteThreeStepWorkflowAsync(
        string directory, string templateId = "three-step-linear")
    {
        Directory.CreateDirectory(directory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId(templateId),
            1,
            [
                new WorkflowStepDefinition(new StepId("architect"), "architect", [], ["plan"], [], new RetryPolicy(1)),
                new WorkflowStepDefinition(new StepId("critic"), "critic", ["plan"], ["review"], [new StepId("architect")], new RetryPolicy(1)),
                new WorkflowStepDefinition(new StepId("publisher"), "publisher", ["review"], ["summary"], [new StepId("critic")], new RetryPolicy(1)),
            ]);

        var path = Path.Combine(directory, "workflow.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(definition));
        return path;
    }

    private static async Task<string> WriteThreeStepBindingsAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry(
                "shell",
                new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                WriteFileCommand("plan", "the-plan"),
                TimeSpan.FromSeconds(30)),
            ["critic"] = new WorkerBindingConfigEntry(
                "shell",
                new WorkerContract("critic", ["plan"], [new ProducedOutput("review")], []),
                CopyFirstInputCommand("review"),
                TimeSpan.FromSeconds(30)),
            ["publisher"] = new WorkerBindingConfigEntry(
                "shell",
                new WorkerContract("publisher", ["review"], [new ProducedOutput("summary")], []),
                CopyFirstInputCommand("summary"),
                TimeSpan.FromSeconds(30)),
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }

    private static string WriteFileCommand(string outputName, string content) =>
        $"echo {content}>%BATON_OUTPUT_DIR%\\{outputName}";

    private static string CopyFirstInputCommand(string outputName) =>
        $"type %BATON_INPUT_0% >%BATON_OUTPUT_DIR%\\{outputName}";

    private static async Task AssertOutputAsync(string artifactsRoot, StepState stepState, string outputName, string expectedContent)
    {
        var outputPath = Path.Combine(artifactsRoot, $"execution_{stepState.LatestExecutionId}", outputName);
        Assert.True(File.Exists(outputPath));
        Assert.Equal(expectedContent, (await File.ReadAllTextAsync(outputPath)).Trim());
    }

    [Fact]
    public async Task RunCommand_reporting_prints_output_artifact_paths_for_succeeded_runs_and_omits_failed_steps()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            Directory.CreateDirectory(testRoot);
            var definition = new WorkflowDefinition(
                new WorkflowTemplateId("two-step"),
                1,
                [
                    new WorkflowStepDefinition(new StepId("succ_step"), "succ_worker", [], ["plan"], [], new RetryPolicy(1)),
                    new WorkflowStepDefinition(new StepId("fail_step"), "fail_worker", [], ["fail_out"], [], new RetryPolicy(1)),
                ]);

            var workflowFilePath = Path.Combine(testRoot, "workflow.json");
            await File.WriteAllTextAsync(workflowFilePath, JsonSerializer.Serialize(definition), TestContext.Current.CancellationToken);

            var config = new Dictionary<string, WorkerBindingConfigEntry>
            {
                ["succ_worker"] = new WorkerBindingConfigEntry(
                    "shell",
                    new WorkerContract("succ_worker", [], [new ProducedOutput("plan")], []),
                    WriteFileCommand("plan", "the-plan"),
                    TimeSpan.FromSeconds(30)),
                ["fail_worker"] = new WorkerBindingConfigEntry(
                    "shell",
                    new WorkerContract("fail_worker", [], [new ProducedOutput("fail_out")], []),
                    "exit 1",
                    TimeSpan.FromSeconds(30)),
            };

            var bindingsFilePath = Path.Combine(testRoot, "bindings.json");
            await File.WriteAllTextAsync(bindingsFilePath, JsonSerializer.Serialize(config), TestContext.Current.CancellationToken);

            var options = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);

            var result = await RunCommand.ExecuteAsync(options, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            using var stringWriter = new StringWriter();
            FlowStateReporter.Report(stringWriter, result);
            var reportOutput = stringWriter.ToString();

            var succStepState = result.State.Steps.Single(s => s.StepId.Value == "succ_step");
            var failStepState = result.State.Steps.Single(s => s.StepId.Value == "fail_step");

            FlowAssert.Succeeded(succStepState);
            Assert.Equal(StepStatus.Failed, failStepState.Status);

            var expectedPlanPath = Path.GetFullPath(Path.Combine(roomDirectory, "artifacts", $"execution_{succStepState.LatestExecutionId}", "plan"));
            var unexpectedFailPath = Path.GetFullPath(Path.Combine(roomDirectory, "artifacts", $"execution_{failStepState.LatestExecutionId}", "fail_out"));

            Assert.Contains($"plan -> {expectedPlanPath}", reportOutput);
            Assert.True(File.Exists(expectedPlanPath));

            Assert.DoesNotContain("fail_out ->", reportOutput);
            Assert.DoesNotContain(unexpectedFailPath, reportOutput);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task When_echo_worker_flag_is_set_worker_stdout_is_written_to_console()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-echo-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        var originalOut = Console.Out;
        try
        {
            var workflowFilePath = await WriteThreeStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteEchoBindingsAsync(testRoot, "hello-live-worker-stdout");
            var options = RunOptionsParser.Parse(
                [workflowFilePath, "--bindings", bindingsFilePath, "--room-dir", roomDirectory, "--echo-worker"]);

            using var consoleOutput = new StringWriter();
            Console.SetOut(consoleOutput);

            var finalState = (await RunCommand.ExecuteAsync(options, Adapters, cancellationToken: TestContext.Current.CancellationToken)).State;
            Assert.Equal(WorkflowStatus.Terminal, finalState.Status);

            var captured = consoleOutput.ToString();
            Assert.Contains("hello-live-worker-stdout", captured);
        }
        finally
        {
            Console.SetOut(originalOut);
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task When_echo_worker_flag_is_not_set_worker_stdout_is_not_written_to_console()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-noecho-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        var originalOut = Console.Out;
        try
        {
            var workflowFilePath = await WriteThreeStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteEchoBindingsAsync(testRoot, "hello-live-worker-stdout");
            var options = RunOptionsParser.Parse(
                [workflowFilePath, "--bindings", bindingsFilePath, "--room-dir", roomDirectory]);

            using var consoleOutput = new StringWriter();
            Console.SetOut(consoleOutput);

            var finalState = (await RunCommand.ExecuteAsync(options, Adapters, cancellationToken: TestContext.Current.CancellationToken)).State;
            Assert.Equal(WorkflowStatus.Terminal, finalState.Status);

            var captured = consoleOutput.ToString();
            Assert.DoesNotContain("hello-live-worker-stdout", captured);
        }
        finally
        {
            Console.SetOut(originalOut);
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    private static async Task<string> WriteEchoBindingsAsync(string directory, string echoMessage)
    {
        Directory.CreateDirectory(directory);
        var echoCmd = $"echo {echoMessage} & echo the-plan>%BATON_OUTPUT_DIR%\\plan";

        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry(
                "shell",
                new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                echoCmd,
                TimeSpan.FromSeconds(60)),
            ["critic"] = new WorkerBindingConfigEntry(
                "shell",
                new WorkerContract("critic", ["plan"], [new ProducedOutput("review")], []),
                CopyFirstInputCommand("review"),
                TimeSpan.FromSeconds(60)),
            ["publisher"] = new WorkerBindingConfigEntry(
                "shell",
                new WorkerContract("publisher", ["review"], [new ProducedOutput("summary")], []),
                CopyFirstInputCommand("summary"),
                TimeSpan.FromSeconds(60)),
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }
}

