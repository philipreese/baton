using System.Text.Json;
using Baton.Vendors;
using Baton.Cli.Tests.TestSupport;
using Baton.Domain;
using Baton.Mutation;
using Baton.Status;
using Baton.Store;
using Baton.Templates;

namespace Baton.Cli.Tests;

/// <summary>
/// <c>baton resolve</c> (#1608), driven through the real <see cref="ResolveCommand.ExecuteAsync"/>
/// entry point — the exact call <c>Program.cs</c> makes — mirroring
/// <see cref="DecideCommandEndToEndTests"/>'s discipline. The resolution mutation itself (writing the
/// declared output, appending <see cref="FlowEvent.CaptureResolved"/>) is proven at the
/// <c>MutationInterface</c> layer (<c>MutationInterfaceCaptureResolutionTests</c>); this proves the
/// CLI wires room-level targeting and never loads bindings to reach it.
/// <para>
/// Nothing in <c>src/</c> can make <see cref="ShellCommandWorkerAdapter"/> (a no-op
/// <c>IWorkerResponseParser</c>) actually capture a response, so every fixture here runs a step to an
/// ordinary Failed (declared output never written) via a real <c>baton run</c>, then appends one more
/// <see cref="FlowEvent.ExecutionIndeterminate"/> for that same execution id directly — the same
/// "fabricate the terminal shape" pattern <c>WorkflowOutcomeAndExitCodeTests</c> already uses for
/// this exact value, since no producer existed before this issue.
/// </para>
/// </summary>
[Collection(ConsoleErrorCaptureCollection.Name)]
public class ResolveCommandEndToEndTests
{
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter> { ["shell"] = new ShellCommandWorkerAdapter() };

    [Fact]
    public async Task Accepting_the_sole_candidate_with_no_execution_given_writes_the_output_and_settles_Succeeded()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-resolve-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var executionId = await SeedIndeterminateRoomAsync(testRoot, roomDirectory, "advice.md", "the worker's real answer");

            var result = await ResolveCommand.ExecuteAsync(
                new ResolveOptions(roomDirectory, ExecutionId: null, Accept: true),
                TestContext.Current.CancellationToken);

            var step = Assert.Single(result.State.Steps);
            Assert.Equal(StepStatus.Succeeded, step.Status);
            Assert.Equal(WorkflowOutcome.Succeeded, WorkflowOutcome.Describe(result.State));

            var outputPath = Path.Combine(roomDirectory, "artifacts", $"execution_{executionId.Value}", "advice.md");
            Assert.Equal("the worker's real answer", await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Rejecting_with_an_explicit_execution_and_reason_leaves_the_room_Failed_not_Indeterminate()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-resolve-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var executionId = await SeedIndeterminateRoomAsync(testRoot, roomDirectory, "advice.md", "not honest advice.md");

            var result = await ResolveCommand.ExecuteAsync(
                new ResolveOptions(roomDirectory, executionId.Value, Accept: false, Reason: "does not honestly satisfy advice.md"),
                TestContext.Current.CancellationToken);

            var step = Assert.Single(result.State.Steps);
            Assert.Equal(StepStatus.Failed, step.Status);
            Assert.Equal(WorkflowOutcome.Failed, WorkflowOutcome.Describe(result.State));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// F1 (#1593 review): admits exactly one verb for a ContractFailure producer, per
    /// <c>ResolveCommand.ResolveExplicitExecutionAsync</c>'s own admission logic. Distinct from a
    /// VerifyFailed/Arrested producer
    /// (<c>MutationInterfaceCaptureResolutionTests.A_verify_failed_Indeterminate_step_is_refused_by_baton_resolve</c>),
    /// which admits neither.
    /// </summary>
    [Fact]
    public async Task A_ContractFailure_step_refuses_accept_capture_but_admits_reject()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-resolve-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var executionId = await SeedContractFailureRoomAsync(testRoot, roomDirectory, "advice.md");

            var acceptEx = await Assert.ThrowsAsync<CliArgumentException>(() => ResolveCommand.ExecuteAsync(
                new ResolveOptions(roomDirectory, executionId.Value, Accept: true),
                TestContext.Current.CancellationToken));
            Assert.Contains("no captured response to accept", acceptEx.Message, StringComparison.Ordinal);
            Assert.Contains("--reject --reason", acceptEx.TryInvocation, StringComparison.Ordinal);

            var result = await ResolveCommand.ExecuteAsync(
                new ResolveOptions(roomDirectory, executionId.Value, Accept: false, Reason: "workspace inspected, redispatching"),
                TestContext.Current.CancellationToken);

            var step = Assert.Single(result.State.Steps);
            Assert.Equal(StepStatus.Failed, step.Status);
            Assert.False(step.IndeterminateAwaitingResolution);
            Assert.Equal(WorkflowOutcome.Failed, WorkflowOutcome.Describe(result.State));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// F1 (#1593 review): <c>ResolveSingleCandidateAsync</c> (no <c>--execution</c> given) must give
    /// the SAME discriminated refusal an explicit <c>--execution</c> gets, rather than silently
    /// selecting the sole candidate and letting <c>MutationInterface.RecordCaptureResolutionAsync</c>
    /// refuse it two layers deeper with the generic "has no unresolved indeterminate capture" message.
    /// </summary>
    [Fact]
    public async Task Resolving_the_sole_candidate_with_accept_capture_against_a_ContractFailure_step_gives_the_discriminated_refusal()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-resolve-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            await SeedContractFailureRoomAsync(testRoot, roomDirectory, "advice.md");

            var ex = await Assert.ThrowsAsync<CliArgumentException>(() => ResolveCommand.ExecuteAsync(
                new ResolveOptions(roomDirectory, ExecutionId: null, Accept: true),
                TestContext.Current.CancellationToken));

            Assert.Contains("no captured response to accept", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("has no unresolved indeterminate capture", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Accepting_a_capture_in_a_multi_step_DAG_leaves_the_downstream_step_dispatchable_and_baton_run_picks_it_up()
    {
        // #1608 review finding 4: `baton resolve --accept-capture` never re-drives the DAG itself.
        // In a single-step room that settles the whole room Terminal/Succeeded, same as every other
        // fixture in this file. In a multi-step room, accepting settles step "a" but leaves step "b"
        // (which depends on it) newly deliverable -- DeriveWorkflowStatus reads that as Running, not
        // Terminal, so nothing rewrites terminal.json and nothing dispatches "b" on its own. This
        // proves both halves of the fix: the room really is left non-Terminal (not silently stuck
        // Terminal-but-wrong), and a follow-up `baton run --room-dir` (simulated here in-process,
        // the same resumption RunCommand.ExecuteAsync gives a real `--room-dir` invocation) genuinely
        // drives "b" to completion -- "the room must be driven forward... and baton run picks it up".
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-resolve-multistep-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteTwoStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteTwoStepBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);

            var firstRun = await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);
            var stepA = firstRun.State.Steps.Single(step => step.StepId == new StepId("a"));
            var stepB = firstRun.State.Steps.Single(step => step.StepId == new StepId("b"));
            Assert.Equal(StepStatus.Failed, stepA.Status);
            Assert.Equal(StepStatus.Pending, stepB.Status);
            Assert.Equal(WorkflowStatus.Terminal, firstRun.State.Status);

            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            await using (var writer = new FlowEventLogWriter(logPath))
            {
                await writer.AppendAsync(
                    new FlowEvent.ExecutionIndeterminate(
                        stepA.LatestExecutionId!.Value, "captured, awaiting conductor resolution",
                        Baton.Outcomes.OutputMaterializer.CapturedResponseFileName, ["advice.md"]),
                    TestContext.Current.CancellationToken);
            }

            var outputDirectory = Path.Combine(roomDirectory, "artifacts", $"execution_{stepA.LatestExecutionId!.Value.Value}");
            Directory.CreateDirectory(outputDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(outputDirectory, Baton.Outcomes.OutputMaterializer.CapturedResponseFileName),
                Baton.Outcomes.OutputMaterializer.CapturedResponseHeader + "\n\nthe worker's real answer",
                TestContext.Current.CancellationToken);

            var resolveResult = await ResolveCommand.ExecuteAsync(
                new ResolveOptions(roomDirectory, stepA.LatestExecutionId!.Value.Value, Accept: true),
                TestContext.Current.CancellationToken);

            var resolvedA = resolveResult.State.Steps.Single(step => step.StepId == new StepId("a"));
            var resolvedB = resolveResult.State.Steps.Single(step => step.StepId == new StepId("b"));
            Assert.Equal(StepStatus.Succeeded, resolvedA.Status);
            Assert.Equal(StepStatus.Pending, resolvedB.Status);
            Assert.Equal(
                WorkflowStatus.Running, resolveResult.State.Status);
            // The sentinel side of this (Program.cs deletes a stale terminal.json when `resolve`
            // leaves the room non-Terminal) is a generic post-command step, unconditional on
            // accept/reject -- already pinned against the real binary by
            // TerminalSentinelEndToEndTests.A_real_CLI_resolve_reject_with_retry_budget_remaining_invalidates_the_stale_sentinel.
            // ResolveCommand.ExecuteAsync alone (called in-process here) never touches the sentinel,
            // so asserting on it at this layer would pass regardless of whether that step exists.

            var followUpRun = await RunCommand.ExecuteAsync(
                new RunOptions(WorkflowFilePath: null, bindingsFilePath, roomDirectory),
                Adapters, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, followUpRun.State.Status);
            Assert.All(followUpRun.State.Steps, FlowAssert.Succeeded);
            var bOutputPath = Path.Combine(
                roomDirectory, "artifacts",
                $"execution_{followUpRun.State.Steps.Single(step => step.StepId == new StepId("b")).LatestExecutionId!.Value.Value}",
                "b.md");
            Assert.Equal("done", (await File.ReadAllTextAsync(bOutputPath, TestContext.Current.CancellationToken)).Trim());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Two_simultaneously_indeterminate_steps_with_no_execution_given_refuses_to_guess()
    {
        // F9 (#1608 review): ResolveSingleCandidateAsync's >1-candidate refusal (as opposed to the
        // 0-candidate arm every "no pending capture" test above already reaches) had no test at all --
        // two INDEPENDENT steps (no DependsOn between them, so both can genuinely be indeterminate at
        // once) each get their own ExecutionIndeterminate.
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-resolve-ambiguous-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteTwoIndependentStepsWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteTwoIndependentStepsBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);

            var firstRun = await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);
            var stepA = firstRun.State.Steps.Single(step => step.StepId == new StepId("a"));
            var stepB = firstRun.State.Steps.Single(step => step.StepId == new StepId("b"));
            Assert.Equal(StepStatus.Failed, stepA.Status);
            Assert.Equal(StepStatus.Failed, stepB.Status);

            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            await using (var writer = new FlowEventLogWriter(logPath))
            {
                await writer.AppendAsync(
                    new FlowEvent.ExecutionIndeterminate(
                        stepA.LatestExecutionId!.Value, "captured", Baton.Outcomes.OutputMaterializer.CapturedResponseFileName, ["out_a"]),
                    TestContext.Current.CancellationToken);
                await writer.AppendAsync(
                    new FlowEvent.ExecutionIndeterminate(
                        stepB.LatestExecutionId!.Value, "captured", Baton.Outcomes.OutputMaterializer.CapturedResponseFileName, ["out_b"]),
                    TestContext.Current.CancellationToken);
            }

            var ex = await Assert.ThrowsAsync<CliArgumentException>(() => ResolveCommand.ExecuteAsync(
                new ResolveOptions(roomDirectory, ExecutionId: null, Accept: true),
                TestContext.Current.CancellationToken));
            Assert.Contains("2 steps", ex.Message, StringComparison.Ordinal);
            Assert.Contains(stepA.LatestExecutionId!.Value.Value, ex.Message, StringComparison.Ordinal);
            Assert.Contains(stepB.LatestExecutionId!.Value.Value, ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Resolving_with_no_pending_capture_refuses_to_guess()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-resolve-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            await RunOrdinaryFailureAsync(testRoot, roomDirectory);

            var ex = await Assert.ThrowsAsync<CliArgumentException>(() => ResolveCommand.ExecuteAsync(
                new ResolveOptions(roomDirectory, ExecutionId: null, Accept: true),
                TestContext.Current.CancellationToken));
            Assert.Contains("no unresolved indeterminate capture", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task An_explicit_execution_naming_no_unresolved_capture_throws()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-resolve-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            await RunOrdinaryFailureAsync(testRoot, roomDirectory);

            await Assert.ThrowsAsync<CliArgumentException>(() => ResolveCommand.ExecuteAsync(
                new ResolveOptions(roomDirectory, "no-such-execution", Accept: true),
                TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task An_explicit_execution_naming_an_already_accepted_capture_is_admitted_and_re_materializes_the_missing_output()
    {
        // #1608 re-review finding 4: ResolveCommand's repair-admission clause (the `isRepairableAccepted`
        // half of its explicit-`--execution` gate) is the ONLY operator-reachable route into the crash
        // repair this PR's "fact then files" order depends on -- and both MutationInterface-layer crash
        // tests call RecordCaptureResolutionAsync directly, so deleting the clause left the whole suite
        // green while the repair became unreachable from the CLI. This drives the real
        // ResolveCommand.ExecuteAsync, so removing the clause turns it red.
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-resolve-repair-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var executionId = await SeedAcceptedButUnwrittenAsync(testRoot, roomDirectory);

            var result = await ResolveCommand.ExecuteAsync(
                new ResolveOptions(roomDirectory, executionId.Value, Accept: true),
                TestContext.Current.CancellationToken);

            Assert.Equal(StepStatus.Succeeded, Assert.Single(result.State.Steps).Status);
            var outputPath = Path.Combine(roomDirectory, "artifacts", $"execution_{executionId.Value}", "advice.md");
            Assert.Equal(
                "the worker's real answer",
                await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task A_reject_naming_that_same_already_accepted_execution_is_still_refused()
    {
        // Polarity partner of the repair admission above, one condition apart (--reject rather than
        // --accept-capture): the clause's `accepted &&` half must not let a rejection reinterpret
        // someone else's earlier accept as a repair. Untested at either layer before this.
        // Scope: this reaches ResolveCommand's own gate only -- MutationInterface's identical gate
        // (RecordCaptureResolutionAsync's `target is not null && accepted`) is unreachable from here
        // because the CLI refuses first, and stays covered by inspection rather than by a test.
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-resolve-repair-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var executionId = await SeedAcceptedButUnwrittenAsync(testRoot, roomDirectory);

            var ex = await Assert.ThrowsAsync<CliArgumentException>(() => ResolveCommand.ExecuteAsync(
                new ResolveOptions(roomDirectory, executionId.Value, Accept: false, Reason: "changed my mind"),
                TestContext.Current.CancellationToken));
            Assert.Contains("no unresolved indeterminate capture", ex.Message, StringComparison.Ordinal);

            var outputPath = Path.Combine(roomDirectory, "artifacts", $"execution_{executionId.Value}", "advice.md");
            Assert.False(File.Exists(outputPath), "a refused reject must not have re-materialized anything.");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Resolving_a_room_with_no_bound_snapshot_throws_SnapshotLoadException()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"cli-resolve-{Guid.NewGuid():N}");
        try
        {
            await Assert.ThrowsAsync<SnapshotLoadException>(() => ResolveCommand.ExecuteAsync(
                new ResolveOptions(roomDirectory, ExecutionId: null, Accept: true),
                TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    private static async Task<ExecutionId> SeedIndeterminateRoomAsync(
        string testRoot, string roomDirectory, string outputName, string capturedBody)
    {
        var executionId = await RunOrdinaryFailureAsync(testRoot, roomDirectory, outputName);

        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        await using (var writer = new FlowEventLogWriter(logPath))
        {
            await writer.AppendAsync(
                new FlowEvent.ExecutionIndeterminate(
                    executionId, "captured, awaiting conductor resolution",
                    Baton.Outcomes.OutputMaterializer.CapturedResponseFileName, [outputName]),
                TestContext.Current.CancellationToken);
        }

        var outputDirectory = Path.Combine(roomDirectory, "artifacts", $"execution_{executionId.Value}");
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, Baton.Outcomes.OutputMaterializer.CapturedResponseFileName),
            Baton.Outcomes.OutputMaterializer.CapturedResponseHeader + "\n\n" + capturedBody,
            TestContext.Current.CancellationToken);

        return executionId;
    }

    /// <summary>
    /// F1 (#1593 review): fabricates a room settled Indeterminate by the #1593 contract-failure
    /// producer — same <see cref="FlowEvent.ExecutionIndeterminate"/> shape
    /// <see cref="SeedIndeterminateRoomAsync"/> uses, deliberately with a null
    /// <see cref="FlowEvent.ExecutionIndeterminate.CapturedResponseFile"/> so the projector's
    /// <see cref="Domain.IndeterminateProducer.ContractFailure"/> discriminant fires instead of
    /// <see cref="Domain.IndeterminateProducer.CapturedResponse"/>.
    /// </summary>
    private static async Task<ExecutionId> SeedContractFailureRoomAsync(
        string testRoot, string roomDirectory, string outputName)
    {
        var executionId = await RunOrdinaryFailureAsync(testRoot, roomDirectory, outputName);

        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        await using (var writer = new FlowEventLogWriter(logPath))
        {
            await writer.AppendAsync(
                new FlowEvent.ExecutionIndeterminate(
                    executionId, "Contract not satisfied — worker exited 0 with work possibly on disk; awaiting conductor resolution.",
                    CapturedResponseFile: null, UnsatisfiedOutputNames: [outputName]),
                TestContext.Current.CancellationToken);
        }

        return executionId;
    }

    /// <summary>
    /// #1622 (d)/#1700: fabricates a room settled Indeterminate by the #1623 verify producer — same
    /// <see cref="FlowEvent.VerifyFailed"/> shape <see cref="Baton.Tests.Mutation.MutationInterfaceCaptureResolutionTests"/>'s
    /// own <c>SeedVerifyFailedRoomAsync</c> uses, at this end-to-end layer.
    /// </summary>
    private static async Task<ExecutionId> SeedVerifyFailedRoomAsync(
        string testRoot, string roomDirectory, string outputName)
    {
        var executionId = await RunOrdinaryFailureAsync(testRoot, roomDirectory, outputName);

        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        await using (var writer = new FlowEventLogWriter(logPath))
        {
            await writer.AppendAsync(
                new FlowEvent.VerifyFailed(executionId, ["fmt-check"], "GATES: FAIL 1 of 25 -- fmt-check"),
                TestContext.Current.CancellationToken);
        }

        return executionId;
    }

    /// <summary>
    /// #1971: the conductor's own measured mistake on <c>dispatch-implement-1b79802b</c> — a
    /// plausible-looking <c>--execution exec-1</c> that named no execution at all. The old refusal sent
    /// them to <c>baton status --json</c> to "confirm 'state' reads Indeterminate", which it did, so the
    /// wrong ID survived the check it pointed at. The refusal now carries the room's real id(s) with the
    /// step and state beside each, so the caller can fix the id from the refusal alone.
    /// </summary>
    [Fact]
    public async Task An_execution_id_naming_no_step_is_refused_with_the_rooms_real_execution_ids_listed()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-resolve-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var executionId = await SeedVerifyFailedRoomAsync(testRoot, roomDirectory, "advice.md");

            var ex = await Assert.ThrowsAsync<CliArgumentException>(() => ResolveCommand.ExecuteAsync(
                ResolveOptionsParser.Parse([roomDirectory, "--execution", "exec-1", "--close", "--reason", "flake"]),
                TestContext.Current.CancellationToken));

            // The whole point: the real id is IN the refusal, not one status read away.
            Assert.Contains(executionId.Value, ex.Message, StringComparison.Ordinal);
            Assert.Contains("awaiting conductor resolution", ex.Message, StringComparison.Ordinal);
            Assert.Contains(StepStatus.Failed.ToString(), ex.Message, StringComparison.Ordinal);
            // Polarity: the generic refusal's advice ("confirm 'state' reads Indeterminate") is exactly
            // the check this caller already passed, so this arm must NOT fall into it.
            Assert.DoesNotContain("has no unresolved indeterminate capture", ex.Message, StringComparison.Ordinal);

            // Control: the id the refusal named is genuinely the one that works.
            var result = await ResolveCommand.ExecuteAsync(
                ResolveOptionsParser.Parse(
                    [roomDirectory, "--execution", executionId.Value, "--close", "--reason", "flake"]),
                TestContext.Current.CancellationToken);
            Assert.False(Assert.Single(result.State.Steps).IndeterminateAwaitingResolution);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// #1622 (d)/#1700: end-to-end through the real CLI parser and command, the same round trip every
    /// other fixture in this file proves — `--close --reason <text>` on a VerifyFailed-producer room
    /// settles Failed, clears the "awaiting conductor resolution" text, and marks `rejected`/`resolvedBy`.
    /// </summary>
    [Fact]
    public async Task Closing_a_verify_failed_room_through_the_real_CLI_parser_settles_Failed_and_reports_resolved_by_conductor()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-resolve-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var executionId = await SeedVerifyFailedRoomAsync(testRoot, roomDirectory, "advice.md");

            var options = ResolveOptionsParser.Parse(
                [roomDirectory, "--execution", executionId.Value, "--close", "--reason", "overlap flake, work already landed"]);
            var result = await ResolveCommand.ExecuteAsync(options, TestContext.Current.CancellationToken);

            var step = Assert.Single(result.State.Steps);
            Assert.Equal(StepStatus.Failed, step.Status);
            Assert.False(step.IndeterminateAwaitingResolution);
            Assert.Equal(WorkflowOutcome.Failed, WorkflowOutcome.Describe(result.State));

            var view = WorkflowStatusProjector.Project(result.State, result.Snapshot, roomDirectory);
            Assert.DoesNotContain("awaiting conductor resolution", view.Error, StringComparison.Ordinal);
            Assert.Contains("Resolved by the conductor", view.Error, StringComparison.Ordinal);
            // F11 (#1720 review, conductor ruling): a `--close` is an administrative settlement, not
            // a refusal -- `resolvedBy` carries it, `rejected` stays false. spec/baton.md §3.
            Assert.False(view.Rejected);
            Assert.Equal("conductor", view.ResolvedBy);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// F4 (#1720 review): the transition nothing covered, and which the refusal text asserted the
    /// opposite of — after a <c>--close</c>, <c>baton redispatch</c> no longer refuses this room. The
    /// room reaches Terminal/Failed, so <c>Program.cs</c>'s post-command block rewrites
    /// <c>terminal.json</c> from the fresh view (the two calls this test makes verbatim after
    /// <c>ResolveCommand</c> returns, since <c>Program.Main</c> is not callable from a test), and
    /// <c>RedispatchCommand</c>'s gate — which reads <c>State == Indeterminate</c> from that file —
    /// stops firing. The refusal now says so.
    /// </summary>
    [Fact]
    public async Task Redispatch_no_longer_refuses_a_verify_failed_room_once_it_has_been_closed()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-resolve-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        var originalError = Console.Error;
        try
        {
            var executionId = await SeedVerifyFailedRoomAsync(testRoot, roomDirectory, "advice.md");

            // (The refusal text itself is pinned in RedispatchCommandEndToEndTests, which can
            // fabricate the Indeterminate sentinel this room only reaches through the engine.)
            var closed = await ResolveCommand.ExecuteAsync(
                ResolveOptionsParser.Parse(
                    [roomDirectory, "--execution", executionId.Value, "--close", "--reason", "overlap flake, work already landed"]),
                TestContext.Current.CancellationToken);
            Assert.Equal(WorkflowStatus.Terminal, closed.State.Status);

            // `baton dispatch` writes bindings.json INTO the room; this room was made by `baton run`
            // (the only way these fixtures can reach a real Indeterminate), which keeps its bindings
            // file beside the workflow instead, so redispatch's own lookup needs a copy in the room.
            File.Copy(
                Path.Combine(testRoot, "bindings.json"),
                Baton.Status.BatonPaths.RoomBindingsFile(roomDirectory));
            File.Copy(
                Path.Combine(testRoot, "workflow.json"),
                Path.Combine(roomDirectory, "workflow.json"), overwrite: true);

            // Program.cs's own post-command sentinel write, verbatim.
            await TerminalSentinelWriter.WriteAsync(
                roomDirectory,
                WorkflowStatusProjector.Project(closed.State, closed.Snapshot, roomDirectory),
                TestContext.Current.CancellationToken);

            using var stderr = new StringWriter();
            Console.SetError(stderr);

            var childRoom = Path.Combine(testRoot, "child-after");
            var result = await RedispatchCommand.ExecuteAsync(
                new RedispatchOptions(roomDirectory, childRoom), Adapters, TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
            Assert.True(Directory.Exists(childRoom));
            Assert.Contains("did not succeed", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(originalError);
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// The discriminating control: <c>--reject</c> (not <c>--close</c>) against the identical
    /// VerifyFailed-producer room still gets #1700's own refusal shape, now pointing at <c>--close</c>
    /// as the remedy instead of a dead end.
    /// </summary>
    [Fact]
    public async Task Rejecting_a_verify_failed_room_still_refuses_but_now_names_close_as_the_remedy()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-resolve-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var executionId = await SeedVerifyFailedRoomAsync(testRoot, roomDirectory, "advice.md");

            var ex = await Assert.ThrowsAsync<CliArgumentException>(() => ResolveCommand.ExecuteAsync(
                new ResolveOptions(roomDirectory, executionId.Value, Accept: false, Reason: "not my problem"),
                TestContext.Current.CancellationToken));

            Assert.Contains("nothing for '--accept-capture'/'--reject' to accept or reject", ex.Message, StringComparison.Ordinal);
            Assert.Contains("--close", ex.TryInvocation, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// The other direction of the same discrimination: <c>--close</c> against a `ContractFailure`
    /// room -- one `--reject`/`--accept-capture` already admit -- must still refuse, through
    /// <see cref="ResolveCommand"/>'s own <c>ThrowDiscriminatedRefusal</c> `close` branch, not just
    /// <c>MutationInterface</c>'s guard one layer down.
    /// </summary>
    [Fact]
    public async Task Closing_a_contract_failure_room_through_the_real_CLI_parser_still_refuses()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-resolve-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var executionId = await SeedContractFailureRoomAsync(testRoot, roomDirectory, "advice.md");

            var ex = await Assert.ThrowsAsync<CliArgumentException>(() => ResolveCommand.ExecuteAsync(
                new ResolveOptions(roomDirectory, executionId.Value, Accept: false, Reason: "not my problem", Close: true),
                TestContext.Current.CancellationToken));

            Assert.Contains("--close", ex.Message, StringComparison.Ordinal);
            Assert.Contains("--reject", ex.TryInvocation, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// The durable shape a crash between "fact" and "files" leaves behind — an accepted
    /// <see cref="FlowEvent.CaptureResolved"/> whose declared output is not on disk — constructed
    /// directly, since what these fixtures test is the CLI's admission of that shape as a repair
    /// request rather than the crash mechanics that produce it (<c>MutationInterfaceCaptureResolutionTests</c>
    /// makes the same choice, for the same reason, one layer down).
    /// </summary>
    private static async Task<ExecutionId> SeedAcceptedButUnwrittenAsync(string testRoot, string roomDirectory)
    {
        var executionId = await SeedIndeterminateRoomAsync(testRoot, roomDirectory, "advice.md", "the worker's real answer");

        await using (var writer = new FlowEventLogWriter(Path.Combine(roomDirectory, "flow.jsonl")))
        {
            await writer.AppendAsync(
                new FlowEvent.CaptureResolved(new StepId("a"), executionId, Accepted: true, ResolvedOutputNames: ["advice.md"]),
                TestContext.Current.CancellationToken);
        }

        Assert.False(
            File.Exists(Path.Combine(roomDirectory, "artifacts", $"execution_{executionId.Value}", "advice.md")),
            "the fixture must reproduce fact-present/file-missing, not an ordinary accept.");

        return executionId;
    }

    /// <summary>Runs a single step to an ordinary Failed (exit 1).</summary>
    private static async Task<ExecutionId> RunOrdinaryFailureAsync(
        string testRoot, string roomDirectory, string outputName = "advice.md")
    {
        var workflowFilePath = await WriteSingleStepWorkflowAsync(testRoot, outputName);
        var bindingsFilePath = await WriteSingleStepBindingsAsync(testRoot, outputName);
        var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);

        var result = await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);
        var step = Assert.Single(result.State.Steps);
        Assert.Equal(StepStatus.Failed, step.Status);

        return step.LatestExecutionId!.Value;
    }

    private static async Task<string> WriteSingleStepWorkflowAsync(string directory, string outputName)
    {
        Directory.CreateDirectory(directory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("resolve-test"),
            1,
            [new WorkflowStepDefinition(new StepId("a"), "a", [], [outputName], [], new RetryPolicy(1))]);

        var path = Path.Combine(directory, "workflow.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(definition));
        return path;
    }

    private static async Task<string> WriteSingleStepBindingsAsync(string directory, string outputName)
    {
        Directory.CreateDirectory(directory);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["a"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("a", [], [new ProducedOutput(outputName)], []),
                PromptTemplate: "exit 1", TimeSpan.FromSeconds(30)),
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }

    private static async Task<string> WriteTwoStepWorkflowAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("resolve-multistep-test"),
            1,
            [
                new WorkflowStepDefinition(new StepId("a"), "a", [], ["advice.md"], [], new RetryPolicy(1)),
                new WorkflowStepDefinition(new StepId("b"), "b", [], ["b.md"], [new StepId("a")], new RetryPolicy(1)),
            ]);

        var path = Path.Combine(directory, "workflow.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(definition));
        return path;
    }

    private static async Task<string> WriteTwoStepBindingsAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        const string writeBCommand = "echo done>%BATON_OUTPUT_DIR%\\b.md";
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["a"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("a", [], [new ProducedOutput("advice.md")], []),
                PromptTemplate: "exit 0", TimeSpan.FromSeconds(30)),
            ["b"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("b", [], [new ProducedOutput("b.md")], []),
                PromptTemplate: writeBCommand, TimeSpan.FromSeconds(30)),
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }

    private static async Task<string> WriteTwoIndependentStepsWorkflowAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("resolve-ambiguous-test"),
            1,
            [
                new WorkflowStepDefinition(new StepId("a"), "a", [], ["out_a"], [], new RetryPolicy(1)),
                new WorkflowStepDefinition(new StepId("b"), "b", [], ["out_b"], [], new RetryPolicy(1)),
            ]);

        var path = Path.Combine(directory, "workflow.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(definition));
        return path;
    }

    private static async Task<string> WriteTwoIndependentStepsBindingsAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["a"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("a", [], [new ProducedOutput("out_a")], []),
                PromptTemplate: "exit 0", TimeSpan.FromSeconds(30)),
            ["b"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("b", [], [new ProducedOutput("out_b")], []),
                PromptTemplate: "exit 0", TimeSpan.FromSeconds(30)),
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }
}
