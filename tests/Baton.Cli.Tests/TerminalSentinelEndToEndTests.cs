using System.Diagnostics;
using System.Text.Json;
using Baton.Vendors;
using Baton.Cli.Tests.TestSupport;
using Baton.Concurrency;
using Baton.Domain;
using Baton.Projection;
using Baton.Status;
using Baton.Store;
using Baton.Templates;

namespace Baton.Cli.Tests;

/// <summary>
/// #1356 points 2-4 and #1374's follow-up fixes: the terminal sentinel (<c>terminal.json</c>), the
/// pre-ledger Failed state a provisioning/validation failure must leave behind (but only when the
/// room is genuinely pre-ledger), the RoomHeld exit code a concurrency refusal gets instead, and the
/// exit codes <c>Program</c> derives from all of it. The exit-code CLASSIFICATION itself is
/// unit-tested directly in <see cref="WorkflowOutcomeAndExitCodeTests"/>; this file covers the
/// wiring — the real <c>Program.cs</c> catch/success paths, which are not otherwise reachable from a
/// test (top-level statements), so the process-spawn tests below follow the same real-process
/// pattern <c>DecideCommandEndToEndTests</c> established for exactly this reason.
/// </summary>
[Collection(WorkingDirectoryCollection.Name)]
public class TerminalSentinelEndToEndTests
{
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter> { ["shell"] = new ShellCommandWorkerAdapter() };

    [Fact]
    public async Task The_templates_subcommand_a_dispatch_Try_line_suggests_is_a_real_known_subcommand()
    {
        // #1382 F10.1: DispatchCommand's "run 'baton templates' to list available built-ins." Try line
        // is only true if 'templates' is actually one of Program.cs's knownSubcommands -- the prior
        // tests only pinned that the STRING was set (Assert.Contains against the literal), never that
        // the command it names is real. Round-tripped through the real binary since knownSubcommands
        // is a top-level-statement local with no other test seam.
        using var process = StartBatonProcess("templates");
        var (stdout, _) = await BoundedProcessWait.RunToExitAsync(
            process, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(0, process.ExitCode);
        Assert.Contains("Available built-in workflow templates", stdout);
    }

    [Fact]
    public async Task A_one_shot_operational_file_failure_exits_cleanly_without_a_stack_trace()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-operational-failure-{Guid.NewGuid():N}");
        var batonHome = Path.Combine(testRoot, "baton-home");
        try
        {
            Directory.CreateDirectory(testRoot);
            // A file where BATON_HOME must be a directory makes the real one-shot queue mutation
            // hit an ordinary IOException without relying on machine ACL configuration.
            await File.WriteAllTextAsync(batonHome, "not a directory", TestContext.Current.CancellationToken);

            using var process = StartBatonProcessInHome(batonHome, "queue", "hold");
            var (_, stderr) = await BoundedProcessWait.RunToExitAsync(
                process, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.NotEqual(0, process.ExitCode);
            Assert.DoesNotContain("Unhandled exception", stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("   at ", stderr, StringComparison.Ordinal);

            var stderrLines = stderr.Split(
                Environment.NewLine,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.Single(stderrLines);
            Assert.StartsWith("Baton command failed: ", stderrLines[0], StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task A_room_that_fails_before_a_ledger_exists_is_left_queryable_as_Failed()
    {
        // The task's own suggested fixture -- a bindings entry naming a model the vendor would
        // reject -- turns out NOT to be a local fail-fast check for the "claude" adapter: only a
        // narrow dot-vs-dash typo (ClaudeWorkerAdapter.RefuseDotDelimitedClaudeModelId) is refused
        // before dispatch; an arbitrary unknown model string is not, since claude ships no model
        // list to validate against. An unregistered adapter name IS refused locally and offline
        // (WorkerBindingResolver.Resolve, UnknownWorkerAdapterException) -- the same case
        // RunCommandEndToEndTests already proves throws -- so that is the fixture used here.
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-preledger-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteOneStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteUnregisteredAdapterBindingsAsync(testRoot);
            var options = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);

            var thrown = await Assert.ThrowsAsync<UnknownWorkerAdapterException>(
                () => RunCommand.ExecuteAsync(options, Adapters, cancellationToken: TestContext.Current.CancellationToken));

            // RunCommand.ExecuteAsync's own throw-on-validation-failure contract is unchanged (every
            // existing caller/test keeps working) -- Program's catch block is what records the
            // sentinel, so this reproduces exactly what that catch does.
            Assert.False(File.Exists(Path.Combine(roomDirectory, "flow.jsonl")), "A pre-ledger failure must not create a ledger.");
            await TerminalSentinelWriter.WriteValidationRefusedAsync(
                roomDirectory, thrown.Message, TestContext.Current.CancellationToken);

            using var humanOutput = new StringWriter();
            await StatusCommand.ExecuteAsync(new StatusOptions(roomDirectory), humanOutput, TestContext.Current.CancellationToken);
            Assert.Contains("Workflow status: Failed", humanOutput.ToString());
            Assert.Contains(thrown.Message, humanOutput.ToString());

            using var jsonOutput = new StringWriter();
            await StatusCommand.ExecuteAsync(new StatusOptions(roomDirectory, Json: true), jsonOutput, TestContext.Current.CancellationToken);
            var view = JsonSerializer.Deserialize<WorkflowStatusView>(jsonOutput.ToString());
            Assert.NotNull(view);
            Assert.Equal("Failed", view!.State);
            Assert.Empty(view.Steps);
            Assert.Empty(view.Outputs);
            Assert.Equal(thrown.Message, view.Error);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Retrying_a_pre_ledger_failure_with_corrected_bindings_invalidates_the_stale_sentinel()
    {
        // Without RunCommand's own stale-sentinel delete, a watcher polling for terminal.json during
        // the SECOND, genuinely-in-flight attempt would see the FIRST attempt's stale "Failed" and
        // read the retry as already done.
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-preledger-retry-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteOneStepWorkflowAsync(testRoot);
            var badBindingsFilePath = await WriteUnregisteredAdapterBindingsAsync(testRoot);
            var firstOptions = new RunOptions(workflowFilePath, badBindingsFilePath, roomDirectory);

            await Assert.ThrowsAsync<UnknownWorkerAdapterException>(
                () => RunCommand.ExecuteAsync(firstOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken));
            await TerminalSentinelWriter.WriteValidationRefusedAsync(
                roomDirectory, "first attempt failed", TestContext.Current.CancellationToken);
            var sentinelPath = Path.Combine(roomDirectory, "terminal.json");
            Assert.True(File.Exists(sentinelPath));

            var goodBindingsFilePath = await WriteOneStepBindingsAsync(testRoot, WriteFileCommand("plan", "the-plan"));
            var secondOptions = new RunOptions(WorkflowFilePath: null, goodBindingsFilePath, roomDirectory);
            var result = await RunCommand.ExecuteAsync(secondOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
            Assert.All(result.State.Steps, FlowAssert.Succeeded);
            // Nothing in this test calls TerminalSentinelWriter.WriteAsync for the second attempt --
            // if the file is still here, it is necessarily the FIRST attempt's stale content.
            Assert.False(File.Exists(sentinelPath), "RunCommand must invalidate a stale sentinel before a fresh dispatch.");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Resuming_an_already_Terminal_room_leaves_its_existing_sentinel_alone()
    {
        // The other polarity of the retry test above (#1374 F1): RunCommand's stale-sentinel delete
        // is now guarded on WorkflowTerminalProbe finding the room NOT already Terminal. This proves
        // the guard's SKIP branch -- a room whose ledger is already Terminal must keep its valid
        // sentinel through a second RunCommand.ExecuteAsync call, not have it deleted and left absent
        // (RunCommand itself never rewrites the sentinel -- only Program's shared post-pump step
        // does -- so if the old unconditional delete ran here, nothing in this test would restore it).
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-resume-terminal-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteOneStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteOneStepBindingsAsync(testRoot, WriteFileCommand("plan", "the-plan"));
            var options = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);

            var firstResult = await RunCommand.ExecuteAsync(options, Adapters, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(WorkflowStatus.Terminal, firstResult.State.Status);

            var view = WorkflowStatusProjector.Project(firstResult.State, firstResult.Snapshot, roomDirectory);
            await TerminalSentinelWriter.WriteAsync(roomDirectory, view, TestContext.Current.CancellationToken);
            var sentinelPath = Path.Combine(roomDirectory, "terminal.json");
            var originalSentinelJson = await File.ReadAllTextAsync(sentinelPath, TestContext.Current.CancellationToken);

            var secondOptions = new RunOptions(WorkflowFilePath: null, bindingsFilePath, roomDirectory);
            var secondResult = await RunCommand.ExecuteAsync(secondOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, secondResult.State.Status);
            Assert.True(File.Exists(sentinelPath), "RunCommand must not delete a sentinel for a room whose ledger is already Terminal.");
            Assert.Equal(originalSentinelJson, await File.ReadAllTextAsync(sentinelPath, TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task The_real_CLI_process_writes_the_sentinel_no_earlier_than_every_output_it_declares()
    {
        // #1374 F3: the prior version of this test asserted the sentinel's absence against
        // RunCommand.ExecuteAsync called directly -- code that never writes the sentinel at all, so
        // the assertion could not fail. Program's shared post-pump step (the thing that actually
        // writes terminal.json last) only exists in the real 'baton' binary (top-level statements
        // aren't otherwise reachable from a test), so the write-last guarantee needs the real
        // process, same as the exit-code tests below. Two steps, both via the production-registered
        // NoOpWorkerAdapter (the "shell" test double used elsewhere in this file only exists in the
        // in-process Adapters dictionary above, not WorkerAdapterRegistry.Default the real binary
        // resolves against), so there are two independently-declared outputs to check ordering against.
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-sentinel-order-proc-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteTwoStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteTwoStepNoOpBindingsAsync(testRoot);
            var sentinelPath = Path.Combine(roomDirectory, "terminal.json");

            using var process = StartBatonProcess(
                "run", workflowFilePath, "--bindings", bindingsFilePath, "--room-dir", roomDirectory);
            await BoundedProcessWait.RunToExitAsync(
                process, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.Equal(0, process.ExitCode);

            Assert.True(File.Exists(sentinelPath));
            var view = JsonSerializer.Deserialize<WorkflowStatusView>(await File.ReadAllTextAsync(sentinelPath, TestContext.Current.CancellationToken));
            Assert.Equal("Succeeded", view!.State);
            Assert.Equal(2, view.Outputs.Count);

            // The load-bearing assertion: every output the sentinel names actually exists, and the
            // sentinel itself was written no earlier than the newest of them -- the ordering #1356
            // point 4 exists to guarantee, checked against the real write, not a hand-reproduced one.
            var sentinelWrittenAtUtc = File.GetLastWriteTimeUtc(sentinelPath);
            foreach (var outputPath in view.Outputs)
            {
                Assert.True(File.Exists(outputPath), $"Declared output '{outputPath}' must exist once the sentinel is read.");
                Assert.True(
                    File.GetLastWriteTimeUtc(outputPath) <= sentinelWrittenAtUtc,
                    $"The sentinel must be written no earlier than declared output '{outputPath}'.");
            }
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task The_real_CLI_process_exits_2_for_a_pre_ledger_validation_failure_and_writes_the_sentinel()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-run-proc-validation-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteOneStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteUnregisteredAdapterBindingsAsync(testRoot);

            using var process = StartBatonProcess(
                "run", workflowFilePath, "--bindings", bindingsFilePath, "--room-dir", roomDirectory);
            var (_, stderr) = await BoundedProcessWait.RunToExitAsync(
                process, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.Equal(2, process.ExitCode);
            Assert.Contains("not-registered", stderr);

            var sentinelPath = Path.Combine(roomDirectory, "terminal.json");
            Assert.True(File.Exists(sentinelPath));
            var view = JsonSerializer.Deserialize<WorkflowStatusView>(await File.ReadAllTextAsync(sentinelPath, TestContext.Current.CancellationToken));
            Assert.Equal("Failed", view!.State);
            Assert.Contains("not-registered", view.Error);
            // #1382 F3: the sentinel/status--json channel must carry the same Try text stderr got,
            // not just the diagnosis -- an agent following invoking-baton.md's advice to watch
            // terminal.json instead of scraping stderr must still see it.
            Assert.NotNull(view.Try);
            Assert.Contains("\"Adapter\"", view.Try, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task The_real_CLI_process_exits_0_for_a_succeeded_run_and_writes_a_Succeeded_sentinel()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-run-proc-ok-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteOneStepWorkflowAsync(testRoot);
            // The real WorkerAdapterRegistry.Default, not this file's test-only "shell" adapter --
            // the spawned subprocess resolves through the real registry, same as an operator's
            // actual invocation (same reasoning DecideCommandEndToEndTests' process test uses).
            var bindingsFilePath = await WriteNoOpBindingsAsync(testRoot);

            using var process = StartBatonProcess(
                "run", workflowFilePath, "--bindings", bindingsFilePath, "--room-dir", roomDirectory);
            await BoundedProcessWait.RunToExitAsync(
                process, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.Equal(0, process.ExitCode);

            var sentinelPath = Path.Combine(roomDirectory, "terminal.json");
            Assert.True(File.Exists(sentinelPath));
            var view = JsonSerializer.Deserialize<WorkflowStatusView>(await File.ReadAllTextAsync(sentinelPath, TestContext.Current.CancellationToken));
            Assert.Equal("Succeeded", view!.State);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// A cancellation with no completed delivery-capable execution must settle from durable room
    /// facts alone. In particular, terminal ledger projection must not revive the retired live
    /// workspace probe merely to populate a cost row: that would observe a later remote state and
    /// make cancellation wait for an unrelated child process before its wrapper can reach EOF.
    /// </summary>
    [Fact]
    public async Task Cancelling_a_persisted_open_room_does_not_revive_a_live_delivery_probe_before_wrapper_eof()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-cancel-wrapper-exit-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workspaceDirectory = Path.Combine(testRoot, "workspace");
            Directory.CreateDirectory(workspaceDirectory);
            await WriteOpenSingleStepRoomAsync(roomDirectory);
            await WriteDeliveryProbeBindingsAsync(roomDirectory, workspaceDirectory);

            var markerPath = Path.Combine(testRoot, "wrapper-completed");
            var logPath = Path.Combine(testRoot, "wrapper.log");
            var fixtureBin = Path.Combine(testRoot, "fixture-bin");
            WriteLingeringGitFixture(fixtureBin);

            using var wrapper = StartRedirectingPowerShellWrapper(
                markerPath, logPath, fixtureBin, "cancel", roomDirectory);
            await BoundedProcessWait.RunToExitAsync(
                wrapper, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.Equal(0, wrapper.ExitCode);
            Assert.True(File.Exists(markerPath), "the wrapper did not observe Baton stdout/stderr reaching EOF after cancellation");
            var sleeperPidPath = Path.Combine(fixtureBin, "sleeper.pid");
            Assert.False(File.Exists(sleeperPidPath),
                "terminal settlement must read completed delivery evidence, not launch a new live workspace probe");
            var sentinelPath = Path.Combine(roomDirectory, "terminal.json");
            Assert.True(File.Exists(sentinelPath), "the terminal fact was not written before delivery-probe cleanup");
            var view = JsonSerializer.Deserialize<WorkflowStatusView>(await File.ReadAllTextAsync(sentinelPath, TestContext.Current.CancellationToken));
            Assert.Equal("Failed", view!.State);

            var releasedSentinelPath = Path.Combine(roomDirectory, "terminal.released.json");
            File.Move(sentinelPath, releasedSentinelPath);
            Assert.True(File.Exists(releasedSentinelPath), "the settled room's sentinel was not exclusively releasable");
        }
        finally
        {
            TryKillLingeringGitSleeper(Path.Combine(testRoot, "fixture-bin"));
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// The companion control to the cancellation no-reprobe test: this reaches the surviving
    /// <see cref="Baton.Mutation.DeliveryVerifier.CheckAsync"/> probe through the real CLI. The
    /// hermetic git.exe exits after starting a sleeper that inherited its output handle, so wrapper
    /// EOF and the dead sleeper prove production containment rather than merely proving no probe ran.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task A_real_CLI_delivery_probe_contains_an_inherited_handle_sleeper_before_wrapper_eof(
        bool missingFinalRemoteObservation, bool changedFinalRemoteObservation)
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-delivery-probe-containment-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workspaceDirectory = Path.Combine(testRoot, "workspace");
            Directory.CreateDirectory(workspaceDirectory);
            var workflowFilePath = await WriteOneStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteDeliveryProbeNoOpBindingsAsync(testRoot, workspaceDirectory);
            var markerPath = Path.Combine(testRoot, "wrapper-completed");
            var logPath = Path.Combine(testRoot, "wrapper.log");
            var fixtureBin = Path.Combine(testRoot, "fixture-bin");
            WriteLingeringGitFixture(fixtureBin);

            using var wrapper = StartRedirectingPowerShellWrapper(
                markerPath, logPath, fixtureBin, true, missingFinalRemoteObservation, changedFinalRemoteObservation,
                "run", workflowFilePath, "--bindings", bindingsFilePath, "--room-dir", roomDirectory);
            await BoundedProcessWait.RunToExitAsync(
                wrapper, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.Equal(missingFinalRemoteObservation || changedFinalRemoteObservation ? 1 : 0, wrapper.ExitCode);
            Assert.Equal(!missingFinalRemoteObservation && !changedFinalRemoteObservation, File.Exists(markerPath));
            var sleeperPidPath = Path.Combine(fixtureBin, "sleeper.pid");
            Assert.True(
                File.Exists(sleeperPidPath),
                $"the surviving DeliveryVerifier probe did not launch the hermetic git sleeper. Wrapper log: "
                + (File.Exists(logPath) ? await File.ReadAllTextAsync(logPath, TestContext.Current.CancellationToken) : "<absent>"));
            var sleeperPid = int.Parse(await File.ReadAllTextAsync(sleeperPidPath, TestContext.Current.CancellationToken));
            Assert.False(IsProcessAlive(sleeperPid), "the inherited-handle sleeper outlived terminal delivery settlement");

            var sentinel = JsonSerializer.Deserialize<WorkflowStatusView>(
                await File.ReadAllTextAsync(Path.Combine(roomDirectory, "terminal.json"), TestContext.Current.CancellationToken));
            var delivery = Assert.Single(sentinel!.Delivery!);
            Assert.Equal(changedFinalRemoteObservation ? "failed"
                : missingFinalRemoteObservation ? "unknown" : "passed",
                delivery.AuthoritativeObservation.State);
            if (missingFinalRemoteObservation || changedFinalRemoteObservation)
            {
                Assert.NotEqual("Succeeded", sentinel.State);
            }
        }
        finally
        {
            TryKillLingeringGitSleeper(Path.Combine(testRoot, "fixture-bin"));
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task A_second_real_CLI_run_against_an_already_completed_room_does_not_overwrite_its_sentinel()
    {
        // #1374 F1's second scenario: a room finishes, then a LATER invocation against that same
        // room fails validation (a typo'd --bindings, here). Before the fix, Program's catch wrote
        // a fresh Failed/no-outputs sentinel unconditionally, destroying the room's real terminal
        // record. The room already has a ledger (flow.jsonl from the first run), so the fix must
        // leave the sentinel untouched -- the second invocation still exits non-zero, it just must
        // not lie about what the room actually is.
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-run-proc-reledger-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteOneStepWorkflowAsync(testRoot);
            var goodBindingsFilePath = await WriteNoOpBindingsAsync(testRoot);

            using (var firstProcess = StartBatonProcess(
                "run", workflowFilePath, "--bindings", goodBindingsFilePath, "--room-dir", roomDirectory))
            {
                await BoundedProcessWait.RunToExitAsync(
                    firstProcess, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
                Assert.Equal(0, firstProcess.ExitCode);
            }

            var sentinelPath = Path.Combine(roomDirectory, "terminal.json");
            Assert.True(File.Exists(sentinelPath));
            var originalSentinelJson = await File.ReadAllTextAsync(sentinelPath, TestContext.Current.CancellationToken);
            var originalView = JsonSerializer.Deserialize<WorkflowStatusView>(originalSentinelJson);
            Assert.Equal("Succeeded", originalView!.State);

            // Same workflow file and room (the CLI always requires the positional <workflow-file>
            // argument, even on a resume -- RunOptionsParser.Parse's own contract), a bindings file
            // naming an unregistered adapter, same fixture as the pre-ledger test above -- except
            // this room already has a ledger and a real Succeeded terminal record behind it.
            var badBindingsFilePath = await WriteUnregisteredAdapterBindingsAsync(testRoot);
            using var secondProcess = StartBatonProcess(
                "run", workflowFilePath, "--bindings", badBindingsFilePath, "--room-dir", roomDirectory);
            var (_, stderr) = await BoundedProcessWait.RunToExitAsync(
                secondProcess, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.Equal((int)RunExitCode.ValidationRefused, secondProcess.ExitCode);
            Assert.Contains("not-registered", stderr);

            var sentinelJsonAfterSecondRun = await File.ReadAllTextAsync(sentinelPath, TestContext.Current.CancellationToken);
            Assert.Equal(originalSentinelJson, sentinelJsonAfterSecondRun);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task A_real_CLI_run_against_a_room_whose_lock_is_held_exits_RoomHeld_and_writes_no_sentinel()
    {
        // #1374 F1's first scenario, the concurrency family: WorkflowLockedException/
        // FlowJournalHeldException must map to a code distinct from ValidationRefused and must never
        // write a sentinel -- the room this exception fires against may be perfectly healthy. Holding
        // ConcurrencyGuard's own lock file from this test process is the same deterministic technique
        // WorktreeProvisioningCommandTests already uses for WorkflowLockedException, chosen over a
        // real two-process timing race so this test cannot flake on scheduling.
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-run-proc-locked-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteOneStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteNoOpBindingsAsync(testRoot);

            var sentinelPath = Path.Combine(roomDirectory, "terminal.json");
            Directory.CreateDirectory(roomDirectory);
            using (ConcurrencyGuard.Acquire(roomDirectory))
            {
                using var process = StartBatonProcess(
                    "run", workflowFilePath, "--bindings", bindingsFilePath, "--room-dir", roomDirectory);
                var (_, stderr) = await BoundedProcessWait.RunToExitAsync(
                    process, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

                Assert.Equal((int)RunExitCode.RoomHeld, process.ExitCode);
                Assert.Contains("already locked", stderr);
                Assert.False(File.Exists(sentinelPath), "A room-held refusal must not fabricate a terminal sentinel.");
            }

            // Releasing the lock and running again with the SAME (good) bindings proves the refusal
            // left nothing that stops this room from completing normally. It does NOT prove the room
            // directory is byte-for-byte as it was -- the refused attempt's own FlowEventLogWriter
            // construction creates a zero-byte flow.jsonl before ConcurrencyGuard.Acquire can throw
            // (see the next test, which pins that mechanism and the fix it requires).
            using var retryProcess = StartBatonProcess(
                "run", workflowFilePath, "--bindings", bindingsFilePath, "--room-dir", roomDirectory);
            await BoundedProcessWait.RunToExitAsync(
                retryProcess, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.Equal(0, retryProcess.ExitCode);
            Assert.True(File.Exists(sentinelPath));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task A_room_held_refusal_leaves_a_zero_byte_ledger_and_a_later_failure_still_gets_a_pre_ledger_sentinel()
    {
        // #1374 F1's own follow-up (found in second-reader review) -- exercises RoomLedgerProbe (see
        // its own doc comment for the rationale); would fail without that fix.
        //
        // #816's measured mechanism reproduces the "already open, empty" ledger deterministically:
        // holding an Append handle on flow.jsonl from THIS process (same technique
        // DecideCommandEndToEndTests uses for FlowJournalHeldException). The block is a Windows
        // FileShare fact -- see that exception type's own doc for why -- which is the only platform
        // this suite runs on, so the arm is unconditional.
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-run-proc-emptyledger-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteOneStepWorkflowAsync(testRoot);
            var goodBindingsFilePath = await WriteNoOpBindingsAsync(testRoot);
            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            var sentinelPath = Path.Combine(roomDirectory, "terminal.json");

            Directory.CreateDirectory(roomDirectory);
            using (var liveEngineHolder = new FileStream(
                logPath, FileMode.Append, FileAccess.Write, FileShare.Read, bufferSize: 1, useAsync: true))
            {
                using var refusedProcess = StartBatonProcess(
                    "run", workflowFilePath, "--bindings", goodBindingsFilePath, "--room-dir", roomDirectory);
                var (_, refusedStderr) = await BoundedProcessWait.RunToExitAsync(
                    refusedProcess, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
                Assert.True((int)RunExitCode.RoomHeld == refusedProcess.ExitCode, $"stderr: {refusedStderr}");
            }

            // Pin the exact mechanism the review found: a real, zero-byte flow.jsonl on disk --
            // not a hypothetical -- with no sentinel written for it.
            Assert.True(File.Exists(logPath));
            Assert.Equal(0, new FileInfo(logPath).Length);
            Assert.False(File.Exists(sentinelPath));

            // Now a genuine validation failure against that same, still-really-pre-ledger room.
            var badBindingsFilePath = await WriteUnregisteredAdapterBindingsAsync(testRoot);
            using var secondProcess = StartBatonProcess(
                "run", workflowFilePath, "--bindings", badBindingsFilePath, "--room-dir", roomDirectory);
            var (_, stderr) = await BoundedProcessWait.RunToExitAsync(
                secondProcess, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.Equal((int)RunExitCode.ValidationRefused, secondProcess.ExitCode);
            Assert.Contains("not-registered", stderr);

            Assert.True(File.Exists(sentinelPath), "The room must not be left stuck pre-ledger with no sentinel at all.");
            var view = JsonSerializer.Deserialize<WorkflowStatusView>(await File.ReadAllTextAsync(sentinelPath, TestContext.Current.CancellationToken));
            Assert.Equal("Failed", view!.State);

            using var jsonOutput = new StringWriter();
            await StatusCommand.ExecuteAsync(new StatusOptions(roomDirectory, Json: true), jsonOutput, TestContext.Current.CancellationToken);
            var statusView = JsonSerializer.Deserialize<WorkflowStatusView>(jsonOutput.ToString());
            Assert.Equal("Failed", statusView!.State);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task A_real_CLI_resolve_reject_with_retry_budget_remaining_keeps_the_room_terminal()
    {
        // #1877 (was A_real_CLI_resolve_reject_..._invalidates_the_stale_sentinel, #1608 review
        // finding 1): a reject with retry budget REMAINING used to re-open the step, turn the room
        // non-Terminal, and so require Program.cs to delete the now-stale terminal.json. It no longer
        // does -- StateProjector forecloses retry on every rejection -- so the polarity flips here:
        // the room stays Terminal, the sentinel stays on disk (rewritten with the resolved state),
        // and stdout must NOT tell the operator the room is incomplete. RetryPolicy(3) still matters:
        // every other resolve fixture uses RetryPolicy(1), where budget exhaustion alone would settle
        // the room and this test could not tell the fix from the fixture.
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-resolve-sentinel-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            Directory.CreateDirectory(roomDirectory);
            var definition = new WorkflowDefinition(
                new WorkflowTemplateId("resolve-sentinel-test"), 1,
                [new WorkflowStepDefinition(new StepId("a"), "a", [], ["advice.md"], [], new RetryPolicy(3))]);
            var snapshot = SnapshotBinder.Bind(definition);
            await SnapshotBinder.PersistAsync(
                snapshot, Path.Combine(roomDirectory, "snapshot.json"), TestContext.Current.CancellationToken);

            var executionId = new ExecutionId($"exec-{Guid.NewGuid():N}");
            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            await using (var writer = new FlowEventLogWriter(logPath))
            {
                await writer.AppendAsync(
                    new FlowEvent.ExecutionRequestAccepted(new ExecutionRequest(
                        executionId, new WorkflowId("wf"), new StepId("a"), "a", [], [], TimeSpan.FromSeconds(30), [],
                        new Dictionary<StepId, ExecutionId>())),
                    TestContext.Current.CancellationToken);
                await writer.AppendAsync(
                    new FlowEvent.ExecutionIndeterminate(
                        executionId, "captured, awaiting conductor resolution", ".captured-response.md", ["advice.md"]),
                    TestContext.Current.CancellationToken);
            }

            var reader = new FlowEventLogReader(logPath);
            var state = StateProjector.Project(await reader.ReadAllAsync(TestContext.Current.CancellationToken), snapshot);
            Assert.Equal(WorkflowStatus.Terminal, state.Status);
            var entries = await reader.ReadAllEntriesWithTimestampsAsync(TestContext.Current.CancellationToken);
            var view = WorkflowStatusProjector.Project(state, snapshot, roomDirectory, entries);
            await TerminalSentinelWriter.WriteAsync(roomDirectory, view, TestContext.Current.CancellationToken);
            var sentinelPath = Path.Combine(roomDirectory, "terminal.json");
            Assert.True(File.Exists(sentinelPath), "setup must leave a sentinel behind to invalidate.");

            using var process = StartBatonProcess(
                "resolve", roomDirectory, "--execution", executionId.Value, "--reject", "--reason", "not honest advice.md");
            var (stdout, stderr) = await BoundedProcessWait.RunToExitAsync(
                process, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.True(
                File.Exists(sentinelPath),
                $"#1877: 'baton resolve --reject' settles the room terminally, so the sentinel must "
                + $"still be on disk (rewritten with the resolved state). stderr: {stderr}");

            // The sentinel really is the RESOLVED one, not the stale pre-resolution file this test's
            // setup wrote: the discriminating read, without which "still on disk" would pass equally
            // against a resolve that did nothing at all.
            var sentinel = await File.ReadAllTextAsync(sentinelPath, TestContext.Current.CancellationToken);
            Assert.Contains("\"rejected\": true", sentinel, StringComparison.Ordinal);
            Assert.Contains("not honest advice.md", sentinel, StringComparison.Ordinal);

            // #1608 review finding 4, inverted by #1877: the room IS complete now, so the follow-up
            // instruction must not be printed — a harness told to run `baton run --room-dir` against a
            // settled room is the dead end this issue reported from the other side.
            Assert.DoesNotContain("Room is not yet complete", stdout, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task A_real_CLI_resolve_on_a_still_Paused_room_names_baton_decide_not_baton_run()
    {
        // #1608 re-review finding 1: the post-`resolve` guidance used to be unconditional over
        // "non-Terminal", which sends a harness in a circle on the Paused shape -- see Program.cs's
        // post-`resolve` step (and spec/baton.md §3) for why that verb cannot move this room.
        // Asserted against the REAL binary's stdout, because Program.cs is what branches: an assertion
        // at the projection layer would pass whether or not the branch exists at all.
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-resolve-paused-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            Directory.CreateDirectory(roomDirectory);
            var definition = new WorkflowDefinition(
                new WorkflowTemplateId("resolve-paused-test"), 1,
                [
                    new WorkflowStepDefinition(
                        new StepId("a"), "a", [], ["advice.md"], [], new RetryPolicy(1),
                        PausePoint: new PausePoint([])),
                ]);
            var snapshot = SnapshotBinder.Bind(definition);
            await SnapshotBinder.PersistAsync(
                snapshot, Path.Combine(roomDirectory, "snapshot.json"), TestContext.Current.CancellationToken);

            var executionId = new ExecutionId($"exec-{Guid.NewGuid():N}");
            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            await using (var writer = new FlowEventLogWriter(logPath))
            {
                await writer.AppendAsync(
                    new FlowEvent.ExecutionRequestAccepted(new ExecutionRequest(
                        executionId, new WorkflowId("wf"), new StepId("a"), "a", [], [], TimeSpan.FromSeconds(30), [],
                        new Dictionary<StepId, ExecutionId>())),
                    TestContext.Current.CancellationToken);
                await writer.AppendAsync(
                    new FlowEvent.ExecutionIndeterminate(
                        executionId, "captured, awaiting conductor resolution",
                        Baton.Outcomes.OutputMaterializer.CapturedResponseFileName, ["advice.md"]),
                    TestContext.Current.CancellationToken);
                await writer.AppendAsync(
                    new FlowEvent.WorkflowPaused(executionId, new StepId("a")),
                    TestContext.Current.CancellationToken);
            }

            var outputDirectory = Path.Combine(roomDirectory, "artifacts", $"execution_{executionId.Value}");
            Directory.CreateDirectory(outputDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(outputDirectory, Baton.Outcomes.OutputMaterializer.CapturedResponseFileName),
                Baton.Outcomes.OutputMaterializer.CapturedResponseHeader + "\n\nthe worker's real answer",
                TestContext.Current.CancellationToken);

            using var process = StartBatonProcess(
                "resolve", roomDirectory, "--execution", executionId.Value, "--accept-capture");
            var (stdout, stderr) = await BoundedProcessWait.RunToExitAsync(
                process, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.True(
                stdout.Contains("Workflow status: Paused", StringComparison.Ordinal),
                $"the fixture must leave a genuinely Paused room, or this pins nothing. stdout: {stdout} stderr: {stderr}");
            Assert.Contains("Room is not yet complete", stdout, StringComparison.Ordinal);
            Assert.Contains("baton decide", stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("--room-dir", stdout, StringComparison.Ordinal);
            // Every option DecideOptionsParser refuses without, spelled out: naming a verb whose
            // required arguments the operator cannot see is the same dead end review finding 1 hit.
            Assert.Contains("--type", stdout, StringComparison.Ordinal);
            Assert.Contains("--bindings", stdout, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    private static Process StartBatonProcess(params string[] args)
    {
        return StartBatonProcessCore(null, args);
    }

    private static Process StartBatonProcessInHome(string batonHome, params string[] args)
    {
        return StartBatonProcessCore(batonHome, args);
    }

    private static Process StartBatonProcessCore(string? batonHome, params string[] args)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (batonHome is not null)
        {
            startInfo.Environment["BATON_HOME"] = batonHome;
        }
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(typeof(RunCommand).Assembly.Location);
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        return Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start 'baton'.");
    }

    private static Process StartRedirectingPowerShellWrapper(
        string markerPath, string logPath, string fixtureBin, params string[] args) =>
        StartRedirectingPowerShellWrapper(markerPath, logPath, fixtureBin, triggerDeliveryProbeSleeper: false, args);

    private static Process StartRedirectingPowerShellWrapper(
        string markerPath, string logPath, string fixtureBin, bool triggerDeliveryProbeSleeper = false, params string[] args)
        => StartRedirectingPowerShellWrapper(markerPath, logPath, fixtureBin, triggerDeliveryProbeSleeper,
            missingFinalRemoteObservation: false, args);

    private static Process StartRedirectingPowerShellWrapper(
        string markerPath, string logPath, string fixtureBin, bool triggerDeliveryProbeSleeper,
        bool missingFinalRemoteObservation, params string[] args)
        => StartRedirectingPowerShellWrapper(markerPath, logPath, fixtureBin, triggerDeliveryProbeSleeper,
            missingFinalRemoteObservation, changedFinalRemoteObservation: false, args);

    private static Process StartRedirectingPowerShellWrapper(
        string markerPath, string logPath, string fixtureBin, bool triggerDeliveryProbeSleeper,
        bool missingFinalRemoteObservation, bool changedFinalRemoteObservation, params string[] args)
    {
        static string Quote(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

        var batonArguments = string.Join(' ', args.Select(Quote));
        var sleeperPidPath = Path.Combine(fixtureBin, "sleeper.pid");
        var script = $"$env:PATH = {Quote(fixtureBin)} + ';' + $env:PATH; "
            + $"$env:BATON_CRASH_TEST_SLEEPER_PID_FILE = {Quote(sleeperPidPath)}; "
            + (triggerDeliveryProbeSleeper ? "$env:BATON_CRASH_TEST_DELIVERY_PROBE_SLEEPER = '1'; " : string.Empty)
            + (missingFinalRemoteObservation ? "$env:BATON_CRASH_TEST_SECOND_REMOTE_OBSERVATION_MISSING = '1'; " : string.Empty)
            + (changedFinalRemoteObservation ? "$env:BATON_CRASH_TEST_SECOND_REMOTE_OBSERVATION_CHANGED = '1'; " : string.Empty)
            + $"& dotnet exec {Quote(typeof(RunCommand).Assembly.Location)} {batonArguments} *> {Quote(logPath)}; "
            + $"if ($LASTEXITCODE -eq 0) {{ New-Item -ItemType File -Path {Quote(markerPath)} | Out-Null; exit 0 }}; exit $LASTEXITCODE";
        var startInfo = new ProcessStartInfo("powershell")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);

        return Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start the PowerShell wrapper.");
    }

    private static void WriteLingeringGitFixture(string fixtureBin)
    {
        Directory.CreateDirectory(fixtureBin);
        var sourceDirectory = Path.GetDirectoryName(typeof(Baton.CrashTestHost.Scenarios).Assembly.Location)!;
        foreach (var source in Directory.EnumerateFiles(sourceDirectory))
        {
            var name = Path.GetFileName(source);
            if (name.StartsWith("Baton.CrashTestHost", StringComparison.Ordinal)
                || name.Equals("Baton.dll", StringComparison.Ordinal))
            {
                File.Copy(source, Path.Combine(fixtureBin, name));
            }
        }

        File.Copy(
            Path.Combine(fixtureBin, "Baton.CrashTestHost.exe"),
            Path.Combine(fixtureBin, "git.exe"));
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void TryKillLingeringGitSleeper(string fixtureBin)
    {
        var pidFile = Path.Combine(fixtureBin, "sleeper.pid");
        if (!File.Exists(pidFile)
            || !int.TryParse(File.ReadAllText(pidFile), out int pid))
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5_000);
        }
        catch (ArgumentException)
        {
            // Already exited; nothing remains for the fixture to clean up.
        }
    }

    private static async Task WriteOpenSingleStepRoomAsync(string roomDirectory)
    {
        Directory.CreateDirectory(roomDirectory);
        var step = new StepId("a");
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("one-open-step"), 1,
            [new WorkflowStepDefinition(step, "a", [], ["out"], [], new RetryPolicy(1))]);
        var snapshot = SnapshotBinder.Bind(definition);
        await SnapshotBinder.PersistAsync(
            snapshot, Path.Combine(roomDirectory, BatonPaths.SnapshotFileName), TestContext.Current.CancellationToken);

        await using var writer = new FlowEventLogWriter(Path.Combine(roomDirectory, BatonPaths.FlowLogFileName));
        await writer.AppendAsync(
            new FlowEvent.ExecutionRequestAccepted(new ExecutionRequest(
                new ExecutionId("exec-open-cancel-1"), new WorkflowId("wf-cancel"), step, "a",
                Inputs: [], Outputs: [], Timeout: TimeSpan.FromMinutes(5), Environment: [],
                UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>())),
            TestContext.Current.CancellationToken);
    }

    private static async Task WriteDeliveryProbeBindingsAsync(string roomDirectory, string workspaceDirectory)
    {
        var bindings = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["a"] = new WorkerBindingConfigEntry(
                NoOpWorkerAdapter.AdapterName,
                new WorkerContract("a", [], [new ProducedOutput("out")], []),
                PromptTemplate: "unused-by-noop", Timeout: TimeSpan.FromSeconds(30), WorkingDirectory: workspaceDirectory),
        };
        await File.WriteAllTextAsync(
            Path.Combine(roomDirectory, "bindings.json"), JsonSerializer.Serialize(bindings), TestContext.Current.CancellationToken);
    }

    private static async Task<string> WriteDeliveryProbeNoOpBindingsAsync(string directory, string workspaceDirectory)
    {
        var path = Path.Combine(directory, "delivery-bindings.json");
        var bindings = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["solo"] = new WorkerBindingConfigEntry(
                NoOpWorkerAdapter.AdapterName,
                new WorkerContract("solo", [], [new ProducedOutput("plan")], []),
                PromptTemplate: "unused-by-noop", Timeout: TimeSpan.FromSeconds(30),
                WorkingDirectory: workspaceDirectory, DeliversBranch: true, ExpectPr: false),
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(bindings), TestContext.Current.CancellationToken);
        return path;
    }

    private static async Task<string> WriteOneStepWorkflowAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("one-step"), 1,
            [new WorkflowStepDefinition(new StepId("solo"), "solo", [], ["plan"], [], new RetryPolicy(1))]);

        var path = Path.Combine(directory, "workflow.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(definition));
        return path;
    }

    private static async Task<string> WriteOneStepBindingsAsync(string directory, string command)
    {
        Directory.CreateDirectory(directory);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["solo"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("solo", [], [new ProducedOutput("plan")], []), command, TimeSpan.FromSeconds(30)),
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }

    private static async Task<string> WriteUnregisteredAdapterBindingsAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["solo"] = new WorkerBindingConfigEntry(
                "not-registered", new WorkerContract("solo", [], [new ProducedOutput("plan")], []),
                "irrelevant", TimeSpan.FromSeconds(30)),
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }

    private static async Task<string> WriteNoOpBindingsAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["solo"] = new WorkerBindingConfigEntry(
                NoOpWorkerAdapter.AdapterName, new WorkerContract("solo", [], [new ProducedOutput("plan")], []),
                PromptTemplate: "unused-by-noop", TimeSpan.FromSeconds(30)),
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }

    private static async Task<string> WriteTwoStepWorkflowAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("two-step-order"), 1,
            [
                new WorkflowStepDefinition(new StepId("a"), "a", [], ["out_a"], [], new RetryPolicy(1)),
                new WorkflowStepDefinition(new StepId("b"), "b", [], ["out_b"], [new StepId("a")], new RetryPolicy(1)),
            ]);

        var path = Path.Combine(directory, "workflow.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(definition));
        return path;
    }

    private static async Task<string> WriteTwoStepNoOpBindingsAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["a"] = new WorkerBindingConfigEntry(
                NoOpWorkerAdapter.AdapterName, new WorkerContract("a", [], [new ProducedOutput("out_a")], []),
                PromptTemplate: "unused-by-noop", TimeSpan.FromSeconds(30)),
            ["b"] = new WorkerBindingConfigEntry(
                NoOpWorkerAdapter.AdapterName, new WorkerContract("b", [], [new ProducedOutput("out_b")], []),
                PromptTemplate: "unused-by-noop", TimeSpan.FromSeconds(30)),
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }

    private static string WriteFileCommand(string outputName, string content) =>
        $"echo {content}>%BATON_OUTPUT_DIR%\\{outputName}";
}
