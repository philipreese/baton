using Baton.Artifacts;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Mutation;
using Baton.Store;
using Baton.Tests.TestSupport;

namespace Baton.Tests.Mutation;

/// <summary>
/// Integration coverage for #1788's own wiring into <see cref="MutationInterface"/>: the delivery
/// check runs through the REAL pump against a real git workspace (no mocking of
/// <see cref="Baton.Core.BatonTask"/>, the same M7 Phase 7 acceptance criteria
/// <c>MutationInterfaceTests</c> holds itself to), gated on <see cref="WorkerBinding.Process.DeliversBranch"/>.
/// <see cref="DeliveryVerifierTests"/> owns <see cref="DeliveryVerifier"/>'s own unit coverage; this
/// file only proves the gate and the event wiring.
/// </summary>
public sealed class MutationInterfaceDeliveryVerificationTests
{
    private static readonly StepId Implementer = new("implementer");

    [Fact]
    public async Task A_delivers_branch_role_that_advanced_and_pushed_cleanly_settles_Succeeded()
    {
        var (workspace, origin) = CreatePushedWorkspace("lane-pass");
        var (roomDirectory, artifactsRoot, logPath) = CreateRoomPaths();
        try
        {
            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["implementer"] = new WorkerBinding.Process(
                    new WorkerContract("implementer", [], [new ProducedOutput("changes.md")], []),
                    new CoreDispatchTarget(
                        "cmd",
                        ["/c", "echo delivered>delivery.txt && git add delivery.txt && git commit -m delivered -q && git push -q origin HEAD && echo done>%BATON_OUTPUT_DIR%\\changes.md"],
                        WorkingDirectory: workspace),
                    TimeSpan.FromSeconds(30),
                    DeliversBranch: true,
                    ExpectPr: false),
            };

            var finalState = await RunSingleStepPumpAsync(roomDirectory, artifactsRoot, logPath, bindings);

            Assert.Equal(StepStatus.Succeeded, finalState.Steps.Single(s => s.StepId == Implementer).Status);
            var events = await new FlowEventLogReader(logPath).ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Empty(events.OfType<FlowEvent.VerifyFailed>());
            Assert.Empty(events.OfType<FlowEvent.VerifyNotRun>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
            DirectoryCleanup.DeleteRecursively(workspace);
            DirectoryCleanup.DeleteRecursively(origin);
        }
    }

    [Fact]
    public async Task A_dirty_workspace_with_unchanged_HEAD_fails_delivery_and_keeps_the_lane_indeterminate()
    {
        var (workspace, origin) = CreatePushedWorkspace("lane-dirty-unchanged");
        var (roomDirectory, artifactsRoot, logPath) = CreateRoomPaths();
        try
        {
            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["implementer"] = new WorkerBinding.Process(
                    new WorkerContract("implementer", [], [new ProducedOutput("changes.md")], []),
                    new CoreDispatchTarget(
                        "cmd",
                        ["/c", "echo changed>>README.md && echo stray>untracked.txt && echo done>%BATON_OUTPUT_DIR%\\changes.md"],
                        WorkingDirectory: workspace),
                    TimeSpan.FromSeconds(30),
                    ChangesTree: true,
                    DeliversBranch: true,
                    ExpectPr: false),
            };

            var finalState = await RunSingleStepPumpAsync(roomDirectory, artifactsRoot, logPath, bindings);
            var step = Assert.Single(finalState.Steps);
            var events = await new FlowEventLogReader(logPath).ReadAllAsync(TestContext.Current.CancellationToken);
            var failed = Assert.Single(events.OfType<FlowEvent.VerifyFailed>());

            Assert.True(step.IndeterminateAwaitingResolution);
            Assert.Equal(VerifyFailedKind.DeliveryFailed, failed.Kind);
            Assert.Equal(["revision-not-created"], failed.FailingMembers);
            Assert.Contains("tracked", failed.Tail, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("untracked", failed.Tail, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(events.OfType<FlowEvent.ExecutionSucceeded>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
            DirectoryCleanup.DeleteRecursively(workspace);
            DirectoryCleanup.DeleteRecursively(origin);
        }
    }

    [Fact]
    public async Task A_clean_workspace_with_unchanged_HEAD_fails_delivery_instead_of_manufacturing_success()
    {
        var (workspace, origin) = CreatePushedWorkspace("lane-clean-unchanged");
        var (roomDirectory, artifactsRoot, logPath) = CreateRoomPaths();
        try
        {
            var finalState = await RunSingleStepPumpAsync(
                roomDirectory, artifactsRoot, logPath,
                DeliveryBinding(workspace, "echo done>%BATON_OUTPUT_DIR%\\changes.md"));
            var step = Assert.Single(finalState.Steps);
            var events = await new FlowEventLogReader(logPath).ReadAllAsync(TestContext.Current.CancellationToken);
            var failed = Assert.Single(events.OfType<FlowEvent.VerifyFailed>());

            Assert.True(step.IndeterminateAwaitingResolution);
            Assert.Equal(VerifyFailedKind.DeliveryFailed, failed.Kind);
            Assert.Equal(["revision-not-created"], failed.FailingMembers);
            Assert.Contains("clean", failed.Tail, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(events.OfType<FlowEvent.ExecutionSucceeded>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
            DirectoryCleanup.DeleteRecursively(workspace);
            DirectoryCleanup.DeleteRecursively(origin);
        }
    }

    [Fact]
    public async Task A_missing_final_delivery_observation_followed_by_a_passing_gate_still_fails_delivery()
    {
        var (workspace, origin) = CreatePushedWorkspace("lane-missing-final-observation");
        var (roomDirectory, artifactsRoot, logPath) = CreateRoomPaths();
        try
        {
            var finalState = await RunSingleStepPumpAsync(
                roomDirectory, artifactsRoot, logPath,
                DeliveryBinding(workspace,
                    "echo delivered>delivery.txt && git add delivery.txt && git commit -m delivered -q && git push -q origin HEAD && echo done>%BATON_OUTPUT_DIR%\\changes.md",
                    "rmdir /s /q .git & exit 0"));
            var events = await new FlowEventLogReader(logPath).ReadAllAsync(TestContext.Current.CancellationToken);

            Assert.True(Assert.Single(finalState.Steps).IndeterminateAwaitingResolution);
            Assert.Single(events.OfType<FlowEvent.VerifyStarted>());
            Assert.Single(events.OfType<FlowEvent.VerifyPassed>());
            var failed = Assert.Single(events.OfType<FlowEvent.VerifyFailed>());
            Assert.Equal(VerifyFailedKind.DeliveryFailed, failed.Kind);
            Assert.Contains("delivery", failed.Tail, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(events.OfType<FlowEvent.ExecutionSucceeded>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
            DirectoryCleanup.DeleteRecursively(workspace);
            DirectoryCleanup.DeleteRecursively(origin);
        }
    }

    [Fact]
    public async Task A_notrun_delivery_preflight_still_runs_verify_before_the_final_notrun_settles_the_lane()
    {
        var (workspace, origin) = CreatePushedWorkspace("lane-notrun-before-verify");
        var (roomDirectory, artifactsRoot, logPath) = CreateRoomPaths();
        try
        {
            DirectoryCleanup.DeleteRecursively(origin);
            var finalState = await RunSingleStepPumpAsync(
                roomDirectory, artifactsRoot, logPath,
                DeliveryBinding(workspace,
                    "git commit --allow-empty -m delivered -q && echo done>%BATON_OUTPUT_DIR%\\changes.md",
                    "echo verified>verify-ran.txt & exit 0"));
            var events = await new FlowEventLogReader(logPath).ReadAllAsync(TestContext.Current.CancellationToken);

            Assert.True(Assert.Single(finalState.Steps).IndeterminateAwaitingResolution);
            Assert.Single(events.OfType<FlowEvent.VerifyStarted>());
            Assert.Single(events.OfType<FlowEvent.VerifyPassed>());
            Assert.True(File.Exists(Path.Combine(workspace, "verify-ran.txt")));
            Assert.Single(events.OfType<FlowEvent.DeliveryObservationRecorded>());
            Assert.Empty(events.OfType<FlowEvent.VerifyNotRun>());
            Assert.Equal(VerifyFailedKind.DeliveryNotRun,
                Assert.Single(events.OfType<FlowEvent.VerifyFailed>()).Kind);
            Assert.Empty(events.OfType<FlowEvent.ExecutionSucceeded>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
            DirectoryCleanup.DeleteRecursively(workspace);
            DirectoryCleanup.DeleteRecursively(origin);
        }
    }

    [Fact]
    public async Task A_delivers_branch_role_that_commits_without_pushing_settles_Indeterminate_with_branch_not_pushed()
    {
        var (workspace, origin) = CreatePushedWorkspace("lane-fail");
        var (roomDirectory, artifactsRoot, logPath) = CreateRoomPaths();
        try
        {
            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["implementer"] = new WorkerBinding.Process(
                    new WorkerContract("implementer", [], [new ProducedOutput("changes.md")], []),
                    new CoreDispatchTarget(
                        "cmd",
                        ["/c", "git commit --allow-empty -m more -q && echo done>%BATON_OUTPUT_DIR%\\changes.md"],
                        WorkingDirectory: workspace),
                    TimeSpan.FromSeconds(30),
                    DeliversBranch: true,
                    ExpectPr: false),
            };

            var finalState = await RunSingleStepPumpAsync(roomDirectory, artifactsRoot, logPath, bindings);
            var stepState = finalState.Steps.Single(s => s.StepId == Implementer);

            // Indeterminate projects StepStatus.Failed with the awaiting-resolution flag set -- the
            // room-level word, not StepStatus itself, is what changes (spec/baton.md's Indeterminate
            // register entry).
            Assert.Equal(StepStatus.Failed, stepState.Status);
            Assert.True(stepState.IndeterminateAwaitingResolution);
            Assert.Equal(IndeterminateProducer.VerifyFailed, stepState.IndeterminateProducer);

            var events = await new FlowEventLogReader(logPath).ReadAllAsync(TestContext.Current.CancellationToken);
            var verifyFailed = Assert.Single(events.OfType<FlowEvent.VerifyFailed>());
            Assert.Equal(VerifyFailedKind.DeliveryFailed, verifyFailed.Kind);
            Assert.Equal(["branch-not-pushed"], verifyFailed.FailingMembers);
            Assert.Contains("branch-not-pushed", stepState.IndeterminateReason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
            DirectoryCleanup.DeleteRecursively(workspace);
            DirectoryCleanup.DeleteRecursively(origin);
        }
    }

    [Fact]
    public async Task An_unpushed_delivery_failure_short_circuits_before_workspace_verify()
    {
        var (workspace, origin) = CreatePushedWorkspace("lane-delivery-before-verify");
        var (roomDirectory, artifactsRoot, logPath) = CreateRoomPaths();
        try
        {
            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["implementer"] = new WorkerBinding.Process(
                    new WorkerContract("implementer", [], [new ProducedOutput("changes.md")], []),
                    new CoreDispatchTarget(
                        "cmd",
                        ["/c", "git commit --allow-empty -m unpushed -q && echo done>%BATON_OUTPUT_DIR%\\changes.md"],
                        WorkingDirectory: workspace),
                    TimeSpan.FromSeconds(30),
                    VerifyCommandOverride: "exit 0",
                    DeliversBranch: true,
                    ExpectPr: false),
            };

            var finalState = await RunSingleStepPumpAsync(roomDirectory, artifactsRoot, logPath, bindings);
            var stepState = Assert.Single(finalState.Steps);
            var events = await new FlowEventLogReader(logPath).ReadAllAsync(TestContext.Current.CancellationToken);

            Assert.True(stepState.IndeterminateAwaitingResolution);
            Assert.Empty(events.OfType<FlowEvent.VerifyStarted>());
            Assert.Empty(events.OfType<FlowEvent.VerifyPassed>());
            var failed = Assert.Single(events.OfType<FlowEvent.VerifyFailed>());
            Assert.Equal(VerifyFailedKind.DeliveryFailed, failed.Kind);
            Assert.Equal(["branch-not-pushed"], failed.FailingMembers);
            Assert.Single(events.OfType<FlowEvent.DeliveryObservationRecorded>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
            DirectoryCleanup.DeleteRecursively(workspace);
            DirectoryCleanup.DeleteRecursively(origin);
        }
    }

    [Fact]
    public async Task A_pushed_delivery_still_runs_workspace_verify_before_success()
    {
        var (workspace, origin) = CreatePushedWorkspace("lane-delivery-then-verify");
        var (roomDirectory, artifactsRoot, logPath) = CreateRoomPaths();
        try
        {
            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["implementer"] = new WorkerBinding.Process(
                    new WorkerContract("implementer", [], [new ProducedOutput("changes.md")], []),
                    new CoreDispatchTarget(
                        "cmd",
                        ["/c", "echo delivered>delivery.txt && git add delivery.txt && git commit -m delivered -q && git push -q origin HEAD && echo done>%BATON_OUTPUT_DIR%\\changes.md"],
                        WorkingDirectory: workspace),
                    TimeSpan.FromSeconds(30),
                    VerifyCommandOverride: "exit 0",
                    DeliversBranch: true,
                    ExpectPr: false),
            };

            var finalState = await RunSingleStepPumpAsync(roomDirectory, artifactsRoot, logPath, bindings);
            var events = await new FlowEventLogReader(logPath).ReadAllAsync(TestContext.Current.CancellationToken);

            var step = Assert.Single(finalState.Steps);
            Assert.False(step.IndeterminateAwaitingResolution, step.IndeterminateReason ?? "unexpected indeterminate settle");
            Assert.Equal(StepStatus.Succeeded, step.Status);
            Assert.Single(events.OfType<FlowEvent.VerifyStarted>());
            Assert.Single(events.OfType<FlowEvent.VerifyPassed>());
            Assert.Empty(events.OfType<FlowEvent.VerifyFailed>());
            Assert.Single(events.OfType<FlowEvent.DeliveryObservationRecorded>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
            DirectoryCleanup.DeleteRecursively(workspace);
            DirectoryCleanup.DeleteRecursively(origin);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_build_lock_blocked_verify_records_and_enforces_the_final_delivery_head(bool mutatesHead)
    {
        var (workspace, origin) = CreatePushedWorkspace("lane-delivery-build-lock-busy-" + mutatesHead);
        var (roomDirectory, artifactsRoot, logPath) = CreateRoomPaths();
        try
        {
            var verifyCommand = mutatesHead
                ? "git commit --allow-empty -m verify-mutated-head -q && echo GATES: BLOCKED 1 of 1 -- build & exit 3"
                : "echo GATES: BLOCKED 1 of 1 -- build & exit 3";
            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["implementer"] = new WorkerBinding.Process(
                    new WorkerContract("implementer", [], [new ProducedOutput("changes.md")], []),
                    new CoreDispatchTarget(
                        "cmd",
                        ["/c", "echo delivered>delivery.txt && git add delivery.txt && git commit -m delivered -q && git push -q origin HEAD && echo done>%BATON_OUTPUT_DIR%\\changes.md"],
                        WorkingDirectory: workspace),
                    TimeSpan.FromSeconds(30),
                    VerifyCommandOverride: verifyCommand,
                    DeliversBranch: true,
                    ExpectPr: false),
            };

            var finalState = await RunSingleStepPumpAsync(roomDirectory, artifactsRoot, logPath, bindings);
            var events = await new FlowEventLogReader(logPath).ReadAllAsync(TestContext.Current.CancellationToken);

            Assert.True(Assert.Single(finalState.Steps).IndeterminateAwaitingResolution);
            Assert.Single(events.OfType<FlowEvent.VerifyStarted>());
            Assert.True(Assert.Single(events.OfType<FlowEvent.VerifyNotRun>()).BuildLockBusy);
            var observation = Assert.Single(events.OfType<FlowEvent.DeliveryObservationRecorded>());
            if (mutatesHead)
            {
                Assert.Equal("Failed", observation.Verification);
                Assert.Contains("delivery heads changed", observation.VerificationReason, StringComparison.Ordinal);
                Assert.Equal(VerifyFailedKind.DeliveryFailed, Assert.Single(events.OfType<FlowEvent.VerifyFailed>()).Kind);
            }
            else
            {
                Assert.Equal("Passed", observation.Verification);
                Assert.Equal(observation.LocalHead, observation.RemoteHead);
                Assert.Empty(events.OfType<FlowEvent.VerifyFailed>());
            }
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
            DirectoryCleanup.DeleteRecursively(workspace);
            DirectoryCleanup.DeleteRecursively(origin);
        }
    }

    [Fact]
    public async Task A_role_that_does_not_deliver_a_branch_never_runs_the_check_even_when_unpushed()
    {
        var (workspace, origin) = CreatePushedWorkspace("lane-readonly");
        TempGitRepository.CommitAll(workspace, "unpushed, but this role never gets checked for it");
        var (roomDirectory, artifactsRoot, logPath) = CreateRoomPaths();
        try
        {
            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["implementer"] = new WorkerBinding.Process(
                    new WorkerContract("implementer", [], [new ProducedOutput("changes.md")], []),
                    new CoreDispatchTarget("cmd", ["/c", "echo done>%BATON_OUTPUT_DIR%\\changes.md"], WorkingDirectory: workspace),
                    TimeSpan.FromSeconds(30),
                    DeliversBranch: false),
            };

            var finalState = await RunSingleStepPumpAsync(roomDirectory, artifactsRoot, logPath, bindings);

            Assert.Equal(StepStatus.Succeeded, finalState.Steps.Single(s => s.StepId == Implementer).Status);
            var events = await new FlowEventLogReader(logPath).ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Empty(events.OfType<FlowEvent.VerifyFailed>());
            Assert.Empty(events.OfType<FlowEvent.VerifyNotRun>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
            DirectoryCleanup.DeleteRecursively(workspace);
            DirectoryCleanup.DeleteRecursively(origin);
        }
    }

    [Fact]
    public async Task An_early_negative_handoff_is_separate_from_a_later_pushed_delivery_stamp()
    {
        var (workspace, origin) = CreatePushedWorkspace("lane-negative-then-pushed");
        var (roomDirectory, artifactsRoot, logPath) = CreateRoomPaths();
        try
        {
            var bindings = DeliveryBinding(workspace,
                "echo no push yet>%BATON_OUTPUT_DIR%\\changes.md && git commit --allow-empty -m later -q && git push -q origin HEAD");
            var finalState = await RunSingleStepPumpAsync(roomDirectory, artifactsRoot, logPath, bindings);
            var outputDirectory = await OutputDirectoryAsync(logPath, artifactsRoot);
            var evidence = (await DeliveryVerifier.ReadEvidenceAsync(outputDirectory, TestContext.Current.CancellationToken)).Evidence;

            Assert.Equal(StepStatus.Succeeded, finalState.Steps.Single().Status);
            Assert.Equal("no push yet", (await File.ReadAllTextAsync(Path.Combine(outputDirectory, "changes.md"), TestContext.Current.CancellationToken)).Trim());
            Assert.NotNull(evidence);
            Assert.Equal(DeliveryCheckStatus.Passed, evidence!.Verification);
            Assert.Equal(evidence.LocalHead, evidence.RemoteHead);
        }
        finally { DirectoryCleanup.DeleteRecursively(roomDirectory); DirectoryCleanup.DeleteRecursively(workspace); DirectoryCleanup.DeleteRecursively(origin); }
    }

    [Fact]
    public async Task A_positive_handoff_cannot_override_an_unpushed_delivery_stamp()
    {
        var (workspace, origin) = CreatePushedWorkspace("lane-positive-then-unpushed");
        var (roomDirectory, artifactsRoot, logPath) = CreateRoomPaths();
        try
        {
            var finalState = await RunSingleStepPumpAsync(roomDirectory, artifactsRoot, logPath, DeliveryBinding(workspace,
                "echo push succeeded>%BATON_OUTPUT_DIR%\\changes.md && git commit --allow-empty -m unpushed -q"));
            var outputDirectory = await OutputDirectoryAsync(logPath, artifactsRoot);
            var evidence = (await DeliveryVerifier.ReadEvidenceAsync(outputDirectory, TestContext.Current.CancellationToken)).Evidence;

            Assert.True(finalState.Steps.Single().IndeterminateAwaitingResolution);
            Assert.Equal("push succeeded", (await File.ReadAllTextAsync(Path.Combine(outputDirectory, "changes.md"), TestContext.Current.CancellationToken)).Trim());
            Assert.NotNull(evidence);
            Assert.Equal(DeliveryCheckStatus.Failed, evidence!.Verification);
            Assert.Equal(["branch-not-pushed"], evidence.FailingMembers);
        }
        finally { DirectoryCleanup.DeleteRecursively(roomDirectory); DirectoryCleanup.DeleteRecursively(workspace); DirectoryCleanup.DeleteRecursively(origin); }
    }

    [Fact]
    public async Task A_worker_placed_passing_stamp_cannot_replace_post_exit_delivery_verification()
    {
        var (workspace, origin) = CreatePushedWorkspace("lane-preplaced-stamp");
        var (roomDirectory, artifactsRoot, logPath) = CreateRoomPaths();
        try
        {
            Directory.CreateDirectory(roomDirectory);
            var fixturePath = Path.Combine(roomDirectory, "fake-stamp.json");
            await File.WriteAllTextAsync(fixturePath,
                """{"observedAt":"2026-09-15T00:00:00Z","localHead":"0123456789abcdef0123456789abcdef01234567","branch":"lane-preplaced-stamp","remoteHead":"0123456789abcdef0123456789abcdef01234567","verification":"Passed"}""",
                TestContext.Current.CancellationToken);
            var command = "$ErrorActionPreference = 'Stop'; "
                + "Set-Content -LiteralPath (Join-Path $env:BATON_OUTPUT_DIR 'changes.md') -Value 'claimed push'; "
                + $"Copy-Item -LiteralPath '{fixturePath}' -Destination (Join-Path $env:BATON_OUTPUT_DIR '{DeliveryVerifier.DeliveryEvidenceFileName}'); "
                + "git commit --allow-empty -m unpushed -q; exit $LASTEXITCODE";
            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["implementer"] = new WorkerBinding.Process(
                    new WorkerContract("implementer", [], [new ProducedOutput("changes.md")], []),
                    new CoreDispatchTarget("pwsh", ["-NoProfile", "-NonInteractive", "-Command", command], WorkingDirectory: workspace),
                    TimeSpan.FromSeconds(30), DeliversBranch: true, ExpectPr: false),
            };

            var finalState = await RunSingleStepPumpAsync(roomDirectory, artifactsRoot, logPath,
                bindings);
            var step = finalState.Steps.Single();
            var events = await new FlowEventLogReader(logPath).ReadAllAsync(TestContext.Current.CancellationToken);
            var outputDirectory = await OutputDirectoryAsync(logPath, artifactsRoot);
            Assert.Equal("claimed push", (await File.ReadAllTextAsync(
                Path.Combine(outputDirectory, "changes.md"), TestContext.Current.CancellationToken)).Trim());
            Assert.True(File.Exists(Path.Combine(outputDirectory, DeliveryVerifier.DeliveryEvidenceFileName)),
                "the worker process did not place its stamp file");
            var actualDelivery = await DeliveryVerifier.CheckAsync(workspace, expectPr: false, TestContext.Current.CancellationToken);

            Assert.Equal(DeliveryCheckStatus.Failed, actualDelivery.Status);
            Assert.Equal(["branch-not-pushed"], actualDelivery.FailingMembers);

            Assert.True(step.IndeterminateAwaitingResolution);
            var failed = Assert.Single(events.OfType<FlowEvent.VerifyFailed>());
            Assert.Equal(VerifyFailedKind.DeliveryFailed, failed.Kind);
            Assert.Equal(["branch-not-pushed"], failed.FailingMembers);
        }
        finally { DirectoryCleanup.DeleteRecursively(roomDirectory); DirectoryCleanup.DeleteRecursively(workspace); DirectoryCleanup.DeleteRecursively(origin); }
    }

    [Theory]
    [InlineData("after-artifact")]
    [InlineData("after-commit")]
    [InlineData("after-push-before-pr")]
    public async Task A_budget_arrest_records_non_certifying_heads_at_each_delivery_boundary(string boundary)
    {
        const string branch = "lane-arrest-boundary";
        var (workspace, origin) = CreatePushedWorkspace(branch);
        var (roomDirectory, artifactsRoot, logPath) = CreateRoomPaths();
        try
        {
            var before = await DeliveryVerifier.ObserveAsync(workspace, expectPr: false,
                new DeliveryCheckOutcome(DeliveryCheckStatus.NotRun, NotRunReason: "pre-worker control"),
                TestContext.Current.CancellationToken);
            Assert.Equal(before.LocalHead, before.RemoteHead);
            const string toolStepLine = """{"event":"step_update","step_update":{"state":"DONE","step_type":"tool","tool_name":"run_command"}}""";
            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["implementer"] = new WorkerBinding.Process(
                    new WorkerContract("implementer", [], [new ProducedOutput("changes.md")], []),
                    new CoreDispatchTarget("cmd", ["/c", "exit 0"], WorkingDirectory: workspace),
                    TimeSpan.FromSeconds(30), Adapter: "agy", MaxToolSteps: 2,
                    ChangesTree: true, DeliversBranch: true, ExpectPr: false, VerifiesWorkspace: false),
            };
            var dispatcher = new ArrestAtBoundaryDispatcher(workspace, artifactsRoot, branch, boundary, toolStepLine);
            var state = await RunSingleStepPumpAsync(roomDirectory, artifactsRoot, logPath, bindings, dispatcher);
            Assert.NotEqual(StepStatus.Succeeded, Assert.Single(state.Steps).Status);

            var reader = new FlowEventLogReader(logPath);
            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Single(events.OfType<FlowEvent.ExecutionArrested>());
            Assert.Empty(events.OfType<FlowEvent.ExecutionSucceeded>());
            var observation = Assert.Single(events.OfType<FlowEvent.DeliveryObservationRecorded>());
            Assert.Equal("NotRun", observation.Verification);
            Assert.Contains("arrested", observation.VerificationReason, StringComparison.Ordinal);
            Assert.Equal(branch, observation.Branch);
            Assert.Equal(before.LocalHead == observation.LocalHead, boundary == "after-artifact");
            Assert.Equal(observation.LocalHead == observation.RemoteHead, boundary != "after-commit");
            if (boundary == "after-commit") Assert.Equal(before.RemoteHead, observation.RemoteHead);

            var outputDirectory = await OutputDirectoryAsync(logPath, artifactsRoot);
            var handoffPath = Path.Combine(outputDirectory, "changes.md");
            var handoff = await File.ReadAllTextAsync(handoffPath, TestContext.Current.CancellationToken);
            Assert.Contains("early account", handoff, StringComparison.Ordinal);
            if (boundary == "after-commit") TempGitRepository.Push(workspace, "origin", branch);
            await RunSingleStepPumpAsync(roomDirectory, artifactsRoot, logPath, bindings);
            var replayed = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Equal(observation, Assert.Single(replayed.OfType<FlowEvent.DeliveryObservationRecorded>()));
            Assert.Equal(handoff, await File.ReadAllTextAsync(handoffPath, TestContext.Current.CancellationToken));
            Assert.Empty(replayed.OfType<FlowEvent.ExecutionSucceeded>());
        }
        finally { DirectoryCleanup.DeleteRecursively(roomDirectory); DirectoryCleanup.DeleteRecursively(workspace); DirectoryCleanup.DeleteRecursively(origin); }
    }

    [Fact]
    public async Task A_negative_handoff_does_not_hide_an_already_durable_remote_head()
    {
        var (workspace, origin) = CreatePushedWorkspace("lane-negative-with-durable-head");
        var (roomDirectory, artifactsRoot, logPath) = CreateRoomPaths();
        try
        {
            await RunSingleStepPumpAsync(roomDirectory, artifactsRoot, logPath, DeliveryBinding(workspace, "echo no push>%BATON_OUTPUT_DIR%\\changes.md"));
            var outputDirectory = await OutputDirectoryAsync(logPath, artifactsRoot);
            var evidencePath = Path.Combine(outputDirectory, DeliveryVerifier.DeliveryEvidenceFileName);
            var handoffPath = Path.Combine(outputDirectory, "changes.md");
            var evidenceBefore = await File.ReadAllTextAsync(evidencePath, TestContext.Current.CancellationToken);
            var handoffBefore = await File.ReadAllTextAsync(handoffPath, TestContext.Current.CancellationToken);
            var reread = await DeliveryVerifier.ReadEvidenceAsync(outputDirectory, TestContext.Current.CancellationToken);

            Assert.Equal(DeliveryCheckStatus.Failed, reread.Evidence!.Verification);
            Assert.Equal(["revision-not-created"], reread.Evidence.FailingMembers);
            Assert.Equal(reread.Evidence.LocalHead, reread.Evidence.RemoteHead);
            Assert.Equal(evidenceBefore, await File.ReadAllTextAsync(evidencePath, TestContext.Current.CancellationToken));
            Assert.Equal(handoffBefore, await File.ReadAllTextAsync(handoffPath, TestContext.Current.CancellationToken));
        }
        finally { DirectoryCleanup.DeleteRecursively(roomDirectory); DirectoryCleanup.DeleteRecursively(workspace); DirectoryCleanup.DeleteRecursively(origin); }
    }

    private static IReadOnlyDictionary<string, WorkerBinding> DeliveryBinding(
        string workspace, string command, string? verifyCommandOverride = null) =>
        new Dictionary<string, WorkerBinding>
        {
            ["implementer"] = new WorkerBinding.Process(
                new WorkerContract("implementer", [], [new ProducedOutput("changes.md")], []),
                new CoreDispatchTarget("cmd", ["/c", command], WorkingDirectory: workspace), TimeSpan.FromSeconds(30),
                VerifyCommandOverride: verifyCommandOverride,
                DeliversBranch: true, ExpectPr: false),
        };

    private static async Task<string> OutputDirectoryAsync(string logPath, string artifactsRoot)
    {
        var events = await new FlowEventLogReader(logPath).ReadAllAsync(TestContext.Current.CancellationToken);
        var executionId = Assert.Single(events.OfType<FlowEvent.ExecutionRequestAccepted>()).Request.ExecutionId;
        return ArtifactManager.ResolveOutputDirectory(artifactsRoot, executionId);
    }

    private static async Task<FlowState> RunSingleStepPumpAsync(
        string roomDirectory, string artifactsRoot, string logPath, IReadOnlyDictionary<string, WorkerBinding> bindings,
        ICoreDispatcher? dispatcherOverride = null)
    {
        var snapshot = new WorkflowDefinitionSnapshot(
            new WorkflowDefinitionSnapshotId("snapshot-delivery"),
            new WorkflowTemplateId("implementer-only"),
            WorkflowTemplateVersion: 1,
            Steps: [new WorkflowStepDefinition(Implementer, "implementer", [], ["changes.md"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]);

        await using var writer = new FlowEventLogWriter(logPath);
        var reader = new FlowEventLogReader(logPath);
        var dispatcher = dispatcherOverride ?? new CoreDispatcher(writer, writer);

        return await MutationInterface.StartWorkflowAsync(
            new WorkflowId("wf-delivery"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher,
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private static (string RoomDirectory, string ArtifactsRoot, string LogPath) CreateRoomPaths()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-delivery-{Guid.NewGuid():N}");
        return (roomDirectory, Path.Combine(roomDirectory, "artifacts"), Path.Combine(roomDirectory, "flow.jsonl"));
    }

    private sealed class ArrestAtBoundaryDispatcher(
        string workspace, string artifactsRoot, string branch, string boundary, string toolStepLine) : ICoreDispatcher
    {
        public async Task<CoreDispatchResult> DispatchAsync(ExecutionRequest request, CoreDispatchTarget target,
            CancellationToken cancellationToken = default)
        {
            var outputDirectory = ArtifactManager.ResolveOutputDirectory(artifactsRoot, request.ExecutionId);
            Directory.CreateDirectory(outputDirectory);
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "changes.md"), "early account");
            if (boundary != "after-artifact")
            {
                TempGitRepository.CommitAll(workspace, "commit after handoff");
                if (boundary == "after-push-before-pr") TempGitRepository.Push(workspace, "origin", branch);
            }
            for (var i = 0; i < 3; i++) target.OnStdoutLine?.Invoke(toolStepLine);
            var arrested = new TaskCompletionSource();
            using var registration = cancellationToken.Register(() => arrested.TrySetResult());
            await arrested.Task.WaitAsync(TimeSpan.FromSeconds(120), CancellationToken.None);
            return new CoreDispatchResult(-1, CoreExitReason.CancelRequested);
        }
    }

    private static (string Workspace, string Origin) CreatePushedWorkspace(string branch)
    {
        var origin = TempGitRepository.InitBareRepository(Path.Combine(Path.GetTempPath(), $"miv-origin-{Guid.NewGuid():N}"));
        var workspace = Path.Combine(Path.GetTempPath(), $"miv-ws-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        TempGitRepository.InitWithEverythingCommitted(workspace);
        TempGitRepository.AddRemote(workspace, "origin", origin);
        TempGitRepository.CreateAndCheckoutBranch(workspace, branch);
        TempGitRepository.CommitAll(workspace, "lane work");
        TempGitRepository.Push(workspace, "origin", branch);
        return (workspace, origin);
    }
}
