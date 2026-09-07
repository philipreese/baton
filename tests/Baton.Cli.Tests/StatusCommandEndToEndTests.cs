using System.Diagnostics;
using System.Text.Json;
using Baton.Vendors;
using Baton.Cli.Tests.TestSupport;
using Baton.Concurrency;
using Baton.Domain;
using Baton.Store;
using Baton.Templates;
using static Baton.Cli.Tests.TestSupport.ParkedStepFixture;
using static Baton.Cli.Tests.TestSupport.ProcessIdentityFixture;

namespace Baton.Cli.Tests;

public class StatusCommandEndToEndTests
{
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter> { ["shell"] = new ShellCommandWorkerAdapter() };

    [Fact]
    public async Task Status_of_a_terminal_workflow_reports_one_line_per_step_with_status_and_execution_id()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteThreeStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteThreeStepBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);

            var finalState = (await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken)).State;
            Assert.Equal(WorkflowStatus.Terminal, finalState.Status);

            var architectExecutionId = finalState.Steps.First(s => s.StepId.Value == "architect").LatestExecutionId!.Value.Value;

            var output = new StringWriter();
            await StatusCommand.ExecuteAsync(new StatusOptions(roomDirectory), output, TestContext.Current.CancellationToken);

            var text = output.ToString();
            Assert.Contains("Workflow status: Terminal", text);
            // The " @ " keeps this as tight as the old closing-paren form: nothing may sit
            // between the execution id and the timestamp the envelope now renders there.
            Assert.Contains($"architect: Succeeded (execution={architectExecutionId} @ ", text);
            Assert.Contains("critic: Succeeded", text);
            Assert.Contains("publisher: Succeeded", text);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Status_of_a_terminal_workflow_prints_one_rolled_up_usage_line_for_the_room()
    {
        // #1360: human `baton status` gets one room-wide roll-up line rather than a per-step usage
        // block -- this shell-stub room's stdout is plain text, so the line must report execution
        // time only, disclosing zero executions reporting tokens rather than a fabricated figure.
        // #1360 F4 (review): labelled "execution time", not "wall-clock" -- see
        // StatusCommand.FormatUsageSummary's own remarks for why.
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteThreeStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteThreeStepBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);
            await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            var output = new StringWriter();
            await StatusCommand.ExecuteAsync(new StatusOptions(roomDirectory), output, TestContext.Current.CancellationToken);

            var text = output.ToString();
            Assert.Contains("Usage: 3 execution(s)", text);
            Assert.Contains("execution time", text);
            Assert.DoesNotContain("tokens in", text);
            Assert.DoesNotContain("tokens out", text);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Status_against_a_nonexistent_room_directory_throws_a_typed_error_and_creates_nothing()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            Directory.CreateDirectory(testRoot);

            await Assert.ThrowsAsync<SnapshotLoadException>(
                () => StatusCommand.ExecuteAsync(new StatusOptions(roomDirectory), TextWriter.Null, TestContext.Current.CancellationToken));

            Assert.False(Directory.Exists(roomDirectory));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Status_against_an_existing_directory_with_no_snapshot_throws_the_same_typed_error()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            Directory.CreateDirectory(roomDirectory);

            await Assert.ThrowsAsync<SnapshotLoadException>(
                () => StatusCommand.ExecuteAsync(new StatusOptions(roomDirectory), TextWriter.Null, TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Status_succeeds_while_another_process_holds_the_workflow_lock_and_writes_nothing()
    {
        // The control that actually discriminates: if StatusCommand ever acquired
        // ConcurrencyGuard's lock itself, this call would throw WorkflowLockedException the moment
        // another holder (simulated here) already has it -- exactly the failure a live `baton run`
        // pump would trigger for a real operator running `baton status` alongside it.
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteThreeStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteThreeStepBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);
            await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            using var guard = ConcurrencyGuard.Acquire(roomDirectory);
            var filesBefore = Directory.GetFiles(roomDirectory).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList();

            var output = new StringWriter();
            await StatusCommand.ExecuteAsync(new StatusOptions(roomDirectory), output, TestContext.Current.CancellationToken);

            var filesAfter = Directory.GetFiles(roomDirectory).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList();
            Assert.Equal(filesBefore, filesAfter);
            Assert.Contains("Workflow status: Terminal", output.ToString());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Follow_on_an_already_terminal_workflow_prints_state_and_exits_without_hanging()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteThreeStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteThreeStepBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);
            await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            var output = new StringWriter();
            var statusTask = StatusCommand.ExecuteAsync(
                new StatusOptions(roomDirectory, Follow: true), output, TestContext.Current.CancellationToken);

            var completedFirst = await Task.WhenAny(
                statusTask, Task.Delay(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken));

            Assert.True(ReferenceEquals(statusTask, completedFirst), "baton status --follow hung on an already-terminal workflow instead of exiting.");
            await statusTask;

            Assert.Contains("Workflow status: Terminal", output.ToString());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// Regression test for the startup race documented on <c>StatusCommand.FollowAsync</c>'s own
    /// baseline-seeding comment (see it for the mechanism — a slow consumer applying backpressure
    /// to a piped <c>Console.Out</c> between the initial print and the tailing loop's baseline
    /// capture).
    /// <para>
    /// Reproduced deterministically with a <see cref="TextWriter"/> that blocks its first
    /// <c>WriteLine</c> call on a gate the test controls, rather than by racing real timing (an
    /// earlier version of this test appended the terminal event immediately after starting the
    /// follow task and found it usually landed before <c>ExecuteAsync</c>'s own initial read too —
    /// a false pass that would have looked identical whether or not the fix below existed).
    /// </para>
    /// </summary>
    [Fact]
    public async Task Follow_does_not_hang_when_the_workflow_finishes_while_the_initial_print_is_still_blocked_on_a_slow_consumer()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            Directory.CreateDirectory(roomDirectory);
            var definition = new WorkflowDefinition(
                new WorkflowTemplateId("race-probe"),
                1,
                [new WorkflowStepDefinition(new StepId("step-one"), "step-one", [], ["out"], [], new RetryPolicy(1))]);
            var snapshot = SnapshotBinder.Bind(definition);
            var snapshotPath = Path.Combine(roomDirectory, "snapshot.json");
            await SnapshotBinder.PersistAsync(snapshot, snapshotPath, TestContext.Current.CancellationToken);

            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            var executionId = new ExecutionId("exec-race-1");
            var request = new ExecutionRequest(
                executionId,
                new WorkflowId("wf-race"),
                new StepId("step-one"),
                "step-one",
                Inputs: [],
                Outputs: [],
                Timeout: TimeSpan.FromSeconds(30),
                Environment: [],
                UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

            await using (var writer = new FlowEventLogWriter(logPath))
            {
                await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(request), TestContext.Current.CancellationToken);
            }

            using var releaseGate = new ManualResetEventSlim(false);
            var blockingWriter = new BlockingOnFirstWriteLineTextWriter(releaseGate);

            // ExecuteAsync's synchronous prefix (the initial read, PrintState, and FollowAsync's
            // baseline capture) runs on whatever thread calls it; that prefix is about to block
            // inside `blockingWriter`'s first WriteLine, so it must run on its own thread rather
            // than this test's -- otherwise the block below would deadlock against itself.
            var statusTask = Task.Run(
                () => StatusCommand.ExecuteAsync(
                    new StatusOptions(roomDirectory, Follow: true), blockingWriter, TestContext.Current.CancellationToken),
                TestContext.Current.CancellationToken);

            Assert.True(
                blockingWriter.FirstWriteLineStarted.Wait(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken),
                "PrintState's first WriteLine never started -- the harness itself is broken, not the fix under test.");

            // The workflow finishes now, while ExecuteAsync is still blocked inside PrintState --
            // strictly before FollowAsync's baseline capture, which only runs once PrintState (and
            // therefore this blocked WriteLine call) returns.
            await using (var writer = new FlowEventLogWriter(logPath))
            {
                await writer.AppendAsync(new FlowEvent.ExecutionSucceeded(executionId), TestContext.Current.CancellationToken);
            }

            releaseGate.Set();

            var completedFirst = await Task.WhenAny(
                statusTask, Task.Delay(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken));
            Assert.True(
                ReferenceEquals(statusTask, completedFirst),
                "baton status --follow hung: the workflow finished while the initial print was still blocked, " +
                "before the tailing loop's own baseline capture.");
            await statusTask;

            Assert.Contains("Workflow status: Terminal", blockingWriter.ToString());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// Blocks its first <see cref="WriteLine(string?)"/> call on a caller-supplied gate, standing
    /// in for a piped <c>Console.Out</c> whose downstream reader is applying backpressure --
    /// deterministic where racing real wall-clock timing against the command's own internals is
    /// not (see the test this backs).
    /// </summary>
    private sealed class BlockingOnFirstWriteLineTextWriter(ManualResetEventSlim releaseGate) : TextWriter
    {
        private readonly StringWriter _inner = new();
        private bool _hasBlockedOnce;

        public ManualResetEventSlim FirstWriteLineStarted { get; } = new(false);

        public override System.Text.Encoding Encoding => _inner.Encoding;

        public override void WriteLine(string? value)
        {
            _inner.WriteLine(value);

            if (_hasBlockedOnce)
            {
                return;
            }

            _hasBlockedOnce = true;
            FirstWriteLineStarted.Set();
            releaseGate.Wait(TimeSpan.FromMinutes(2));
        }

        public override string ToString() => _inner.ToString();
    }

    private sealed class SignalingTextWriter(string signalFilePath) : TextWriter
    {
        private readonly StringWriter _inner = new();
        private bool _signaled;

        public override System.Text.Encoding Encoding => _inner.Encoding;

        public override void WriteLine(string? value)
        {
            _inner.WriteLine(value);
            Signal();
        }

        public override void Write(string? value)
        {
            _inner.Write(value);
            Signal();
        }

        private void Signal()
        {
            if (!_signaled)
            {
                _signaled = true;
                try
                {
                    File.WriteAllText(signalFilePath, "started");
                }
                catch
                {
                }
            }
        }

        public override string ToString() => _inner.ToString();
    }

    [Fact]
    public async Task Following_a_running_workflow_prints_new_events_as_they_land_and_exits_at_terminal()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        var signalFilePath = Path.Combine(testRoot, "status-started.flag");
        try
        {
            var workflowFilePath = await WriteThreeStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteThreeStepGatedBindingsAsync(testRoot, signalFilePath);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);

            var runTask = RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            while (!File.Exists(logPath) || new FileInfo(logPath).Length == 0)
            {
                await Task.Delay(25, TestContext.Current.CancellationToken);
            }

            var output = new SignalingTextWriter(signalFilePath);
            var statusTask = StatusCommand.ExecuteAsync(
                new StatusOptions(roomDirectory, Follow: true), output, TestContext.Current.CancellationToken);

            var runResult = await runTask.WaitAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
            Assert.Equal(WorkflowStatus.Terminal, runResult.State.Status);

            await statusTask.WaitAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);

            var text = output.ToString();
            Assert.Contains("ExecutionRequestAccepted", text);
            Assert.Contains("ExecutionSucceeded", text);
            Assert.Contains("Workflow status: Terminal", text);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Cancelling_a_follow_on_a_still_running_workflow_returns_cleanly_instead_of_throwing()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        var signalFilePath = Path.Combine(testRoot, "status-started.flag");
        try
        {
            var workflowFilePath = await WriteThreeStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteThreeStepGatedBindingsAsync(testRoot, signalFilePath);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);

            var runTask = RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            while (!File.Exists(logPath) || new FileInfo(logPath).Length == 0)
            {
                await Task.Delay(25, TestContext.Current.CancellationToken);
            }

            using var followCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            followCancellation.CancelAfter(TimeSpan.FromMilliseconds(300));

            var output = new SignalingTextWriter(signalFilePath);
            var statusTask = StatusCommand.ExecuteAsync(new StatusOptions(roomDirectory, Follow: true), output, followCancellation.Token);

            await statusTask.WaitAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);

            var exception = await Record.ExceptionAsync(() => statusTask);
            Assert.Null(exception);

            // Ensure the signal file is created so runTask can finish cleanly
            if (!File.Exists(signalFilePath))
            {
                File.WriteAllText(signalFilePath, "started");
            }

            await runTask.WaitAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Cancelling_a_follow_at_its_first_await_returns_cleanly_instead_of_throwing()
    {
        // #999 (mechanism recorded there and at StatusCommand.ExecuteAsync's catch): an
        // already-cancelled token interrupts the guarded region's FIRST awaited call — the
        // snapshot load, per the #999 reviewer, not the journal read the gates run caught —
        // deterministically instead of needing a loaded machine. The journal-read window is
        // covered by the same enclosing filter, not independently exercised here.
        // Red arm: with the follow-mode OperationCanceledException filter removed from
        // StatusCommand.ExecuteAsync, this throws.
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteThreeStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteThreeStepBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);
            await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();

            var exception = await Record.ExceptionAsync(() => StatusCommand.ExecuteAsync(
                new StatusOptions(roomDirectory, Follow: true), TextWriter.Null, cancelled.Token));
            Assert.Null(exception);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// #1574 second-reader finding 2: a `--follow` poll can read a worker's stdout mid-write and hold
    /// the newline-less tail in its line assembler for a later poll to complete -- pre-#1574 raw
    /// tailing never buffered anything, so Ctrl-C never had content to lose. This drives the SAME
    /// cancellation path an operator's Ctrl-C takes (a token cancelled mid-poll-loop, after the
    /// assembler has already buffered the partial line -- not the already-cancelled-before-the-first-
    /// await case the test above covers) and asserts the held content is flushed rather than
    /// silently dropped.
    /// </summary>
    [Fact]
    public async Task Cancelling_a_follow_after_it_has_buffered_a_partial_line_still_flushes_it()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            Directory.CreateDirectory(roomDirectory);
            var definition = new WorkflowDefinition(
                new WorkflowTemplateId("follow-cancel-probe"),
                1,
                [new WorkflowStepDefinition(new StepId("implement"), "implement", [], ["out"], [], new RetryPolicy(3))]);
            var snapshot = SnapshotBinder.Bind(definition);
            var snapshotPath = Path.Combine(roomDirectory, "snapshot.json");
            await SnapshotBinder.PersistAsync(snapshot, snapshotPath, TestContext.Current.CancellationToken);

            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            var executionId = new ExecutionId("exec-still-running");
            var request = new ExecutionRequest(
                executionId,
                new WorkflowId("wf-follow-cancel"),
                new StepId("implement"),
                "implement",
                Inputs: [],
                Outputs: [],
                Timeout: TimeSpan.FromMinutes(30),
                Environment: [],
                UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

            await using (var writer = new FlowEventLogWriter(logPath))
            {
                await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(request), TestContext.Current.CancellationToken);
                await writer.AppendAsync(new CoreEvent.ExecutionStarted(executionId, Pid: 4242), TestContext.Current.CancellationToken);
            }

            var artifactsRoot = Path.Combine(roomDirectory, Baton.Artifacts.ArtifactManager.ArtifactsDirectoryName);
            var executionDir = Baton.Artifacts.ArtifactManager.AllocateOutputDirectory(artifactsRoot, executionId);
            await File.WriteAllTextAsync(
                Path.Combine(executionDir, Baton.Dispatch.ExecutionStreamLogger.StdoutLogFileName),
                "partial progress with no trailing newline yet",
                TestContext.Current.CancellationToken);

            // Long enough to survive one full 500ms poll cycle (which buffers the partial line, held
            // unflushed) and then land the cancellation partway through the NEXT poll's Task.Delay --
            // the exact window the pre-fix code dropped.
            using var followCancellation = new CancellationTokenSource();
            followCancellation.CancelAfter(TimeSpan.FromMilliseconds(700));

            var output = new StringWriter();
            await StatusCommand.ExecuteAsync(new StatusOptions(roomDirectory, Follow: true), output, followCancellation.Token);

            Assert.Contains("partial progress with no trailing newline yet", output.ToString());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// #1721: the initial synchronous <c>TailStreams</c> call in <c>ExecuteAsync</c> and the follow
    /// loop's first poll must share offsets/assemblers, not each start from a fresh dictionary --
    /// otherwise the loop's first poll re-tails the whole stream from byte 0 and reprints exactly
    /// what the initial tail just printed. Asserts the initial content appears exactly ONCE across
    /// the run and that content appended between polls appears exactly once too. Every line here
    /// is complete and newline-terminated, so this exercises the shared OFFSETS only; the shared
    /// StreamLineAssembler's stitching of a line split across two TailStreams calls is pinned by
    /// WorkerStreamJsonRenderingTests, not by this test.
    /// </summary>
    [Fact]
    public async Task Following_a_still_running_workflow_does_not_reprint_the_initial_tail_on_the_first_poll()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            Directory.CreateDirectory(roomDirectory);
            var definition = new WorkflowDefinition(
                new WorkflowTemplateId("follow-double-tail-probe"),
                1,
                [new WorkflowStepDefinition(new StepId("implement"), "implement", [], ["out"], [], new RetryPolicy(3))]);
            var snapshot = SnapshotBinder.Bind(definition);
            var snapshotPath = Path.Combine(roomDirectory, "snapshot.json");
            await SnapshotBinder.PersistAsync(snapshot, snapshotPath, TestContext.Current.CancellationToken);

            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            var executionId = new ExecutionId("exec-still-running");
            var request = new ExecutionRequest(
                executionId,
                new WorkflowId("wf-follow-double-tail"),
                new StepId("implement"),
                "implement",
                Inputs: [],
                Outputs: [],
                Timeout: TimeSpan.FromMinutes(30),
                Environment: [],
                UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

            await using (var writer = new FlowEventLogWriter(logPath))
            {
                await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(request), TestContext.Current.CancellationToken);
                await writer.AppendAsync(new CoreEvent.ExecutionStarted(executionId, Pid: 4242), TestContext.Current.CancellationToken);
            }

            var artifactsRoot = Path.Combine(roomDirectory, Baton.Artifacts.ArtifactManager.ArtifactsDirectoryName);
            var executionDir = Baton.Artifacts.ArtifactManager.AllocateOutputDirectory(artifactsRoot, executionId);
            var stdoutPath = Path.Combine(executionDir, Baton.Dispatch.ExecutionStreamLogger.StdoutLogFileName);
            await File.WriteAllTextAsync(stdoutPath, "initial tail line\n", TestContext.Current.CancellationToken);

            // Long enough to survive the initial synchronous tail plus one full poll cycle
            // (PollIntervalMs=500) before the appended bytes land, then a second poll cycle to pick
            // those up, then cancel.
            using var followCancellation = new CancellationTokenSource();
            followCancellation.CancelAfter(TimeSpan.FromMilliseconds(1400));

            var output = new StringWriter();
            var statusTask = StatusCommand.ExecuteAsync(
                new StatusOptions(roomDirectory, Follow: true), output, followCancellation.Token);

            await Task.Delay(700, TestContext.Current.CancellationToken); // wait-ok: waits out one PollIntervalMs poll cycle so the append below lands strictly between two polls
            await File.AppendAllTextAsync(stdoutPath, "appended between polls\n", TestContext.Current.CancellationToken);

            await statusTask;

            var text = output.ToString();
            var occurrences = System.Text.RegularExpressions.Regex.Matches(text, "initial tail line").Count;
            Assert.Equal(1, occurrences);
            Assert.Contains("appended between polls", text);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Cancelling_a_one_shot_status_probe_still_throws_it_produced_no_answer()
    {
        // #999's polarity arm: without --follow there is no "stop following" semantic — a
        // cancelled probe returned nothing, and returning cleanly would report silence as
        // success (fail-open).
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteThreeStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteThreeStepBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);
            await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StatusCommand.ExecuteAsync(
                new StatusOptions(roomDirectory, Follow: false), TextWriter.Null, cancelled.Token));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Status_output_includes_per_step_timestamp_for_events_with_writer_timestamp()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteThreeStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteThreeStepBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);

            var finalState = (await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken)).State;
            Assert.Equal(WorkflowStatus.Terminal, finalState.Status);

            var output = new StringWriter();
            await StatusCommand.ExecuteAsync(new StatusOptions(roomDirectory), output, TestContext.Current.CancellationToken);

            var text = output.ToString();
            // Timestamp format is ISO 8601 (O format), which looks like "2026-01-15T12:34:56.1234567Z"
            // and appears in output as "@ 2026-01-15T12:34:56.1234567Z"
            Assert.Matches(@"@ \d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}Z", text);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// #1838: no engine identity was ever recorded for this execution (the fixture's default
    /// <c>enginePid: null</c>), so <c>EngineLivenessProbe</c> reads <c>Unknown</c> rather than
    /// <c>Alive</c> -- the plain single-verb redispatch instruction is what must render, pinned in
    /// full so a regression that silently widens the two-step wording to this shape is caught.
    /// </summary>
    [Fact]
    public async Task Status_of_a_quota_parked_step_renders_its_classification_and_local_retry_time()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var (_, _, _, retryNotBefore) = await WriteParkedStepFixtureAsync(testRoot, roomDirectory);

            var output = new StringWriter();
            await StatusCommand.ExecuteAsync(new StatusOptions(roomDirectory), output, TestContext.Current.CancellationToken);

            var text = output.ToString();
            var expectedLocalTime = retryNotBefore.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            Assert.Contains(
                $"implement: parked (vendor quota) — retries {expectedLocalTime}; no fallback declared — "
                + "`baton redispatch <room-dir> --adapter <vendor>` rebinds it now, or wait",
                text);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// #1838 (review HIGH-1 against #802's shipped wording): the owning engine is still alive --
    /// same <c>EngineLivenessProbe</c> read <see cref="Status_of_a_parked_step_with_a_dead_engine_names_it_and_says_manual_intervention_is_needed"/>
    /// uses a dead identity for, this uses <see cref="Process.GetCurrentProcess"/> for a
    /// confirmed-<c>Alive</c> one. <see cref="RecoveryGuidance.CancelThenRedispatchAdapterInstruction"/>'s
    /// own doc has why `baton redispatch` alone (the pre-fix wording) sent the operator into a refusal
    /// in exactly this shape -- not restated here.
    /// </summary>
    [Fact]
    public async Task Status_of_a_quota_parked_step_with_a_live_engine_names_cancel_before_redispatch()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var currentProcess = Process.GetCurrentProcess();
            var liveStartTime = new DateTimeOffset(currentProcess.StartTime).ToUniversalTime();
            var (_, _, _, retryNotBefore) = await WriteParkedStepFixtureAsync(
                testRoot, roomDirectory, enginePid: currentProcess.Id, engineStartTime: liveStartTime);

            var output = new StringWriter();
            await StatusCommand.ExecuteAsync(new StatusOptions(roomDirectory), output, TestContext.Current.CancellationToken);

            var text = output.ToString();
            var expectedLocalTime = retryNotBefore.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            Assert.Contains(
                $"implement: parked (vendor quota) — retries {expectedLocalTime}; no fallback declared — "
                + "`baton cancel <room-dir>`, then `baton redispatch <room-dir> --adapter <vendor>`, rebinds it, or wait",
                text);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// #1513: a parked step whose engine is provably dead is the exact live signature this issue was
    /// filed against -- "parked ... retries HH:MM" alone reads as a promise the ledger cannot back
    /// (spec/baton.md §7 has why). Uses <see cref="TestSupport.ProcessIdentityFixture.DeadProcessIdentity"/>
    /// for an OS-confirmed-dead PID -- see that method's own doc for why.
    /// </summary>
    [Fact]
    public async Task Status_of_a_parked_step_with_a_dead_engine_names_it_and_says_manual_intervention_is_needed()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var (deadPid, deadStartTime) = DeadProcessIdentity();
            var (_, _, _, retryNotBefore) = await WriteParkedStepFixtureAsync(
                testRoot, roomDirectory, FailureClassification.Retryable, enginePid: deadPid, engineStartTime: deadStartTime);

            var output = new StringWriter();
            await StatusCommand.ExecuteAsync(new StatusOptions(roomDirectory), output, TestContext.Current.CancellationToken);

            var text = output.ToString();
            var expectedLocalTime = retryNotBefore.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            Assert.Contains(
                $"implement: parked (retryable) — retries {expectedLocalTime}, but the engine that scheduled " +
                "this retry is no longer alive and nothing else will act on it; this needs manual " +
                "intervention — re-run `baton run` against this room's own workflow.json and " +
                $"bindings.json with --room-dir pointed at it, and leave it running until " +
                $"{expectedLocalTime} or nothing fires (see spec/baton.md §3)",
                text);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Status_of_an_unknown_instant_exhausted_step_renders_parked_vendor_quota_reset_unknown()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            await WriteUnknownInstantExhaustedStepFixtureAsync(testRoot, roomDirectory);

            var output = new StringWriter();
            await StatusCommand.ExecuteAsync(new StatusOptions(roomDirectory), output, TestContext.Current.CancellationToken);

            var text = output.ToString();
            Assert.Contains("implement: parked (vendor quota) — reset unknown", text);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Status_of_an_ordinary_backoff_park_days_away_renders_retryable_with_the_full_date()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var (_, _, _, retryNotBefore) = await WriteParkedStepFixtureAsync(
                testRoot,
                roomDirectory,
                FailureClassification.Retryable,
                retryIn: TimeSpan.FromDays(3));

            var output = new StringWriter();
            await StatusCommand.ExecuteAsync(new StatusOptions(roomDirectory), output, TestContext.Current.CancellationToken);

            var text = output.ToString();
            var expectedLocalTime = retryNotBefore.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            Assert.Contains($"implement: parked (retryable) — retries {expectedLocalTime}", text);
            Assert.DoesNotContain("parked (vendor quota)", text);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Status_of_a_step_that_retried_after_being_parked_no_longer_renders_parked()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var (_, logPath, _, _) = await WriteParkedStepFixtureAsync(testRoot, roomDirectory);

            var retriedExecutionId = new ExecutionId("exec-parked-2");
            var retriedRequest = new ExecutionRequest(
                retriedExecutionId,
                new WorkflowId("wf-parked"),
                new StepId("implement"),
                "implement",
                Inputs: [],
                Outputs: [],
                Timeout: TimeSpan.FromSeconds(30),
                Environment: [],
                UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

            await using (var writer = new FlowEventLogWriter(logPath))
            {
                await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(retriedRequest), TestContext.Current.CancellationToken);
                await writer.AppendAsync(new FlowEvent.ExecutionSucceeded(retriedExecutionId), TestContext.Current.CancellationToken);
            }

            var output = new StringWriter();
            await StatusCommand.ExecuteAsync(new StatusOptions(roomDirectory), output, TestContext.Current.CancellationToken);

            var text = output.ToString();
            Assert.DoesNotContain("implement: parked", text);
            Assert.Contains("implement: Succeeded", text);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// Hand-writes a snapshot plus a bare <c>ExecutionFailed</c>(<see cref="FailureClassification.ExhaustedUntil"/>,
    /// <c>RetryNotBefore: null</c>) — the "reset unknown" shape, distinct from <see cref="ParkedStepFixture.WriteParkedStepFixtureAsync"/>'s
    /// pending-<c>StepRetryScheduled</c> shape — directly to <c>flow.jsonl</c>, rather than driving it
    /// through <see cref="RunCommand"/>, whose <see cref="ShellCommandWorkerAdapter"/> has no way to
    /// report a quota classification.
    /// </summary>
    private static async Task<(string SnapshotPath, string LogPath, ExecutionId ExecutionId)>
        WriteUnknownInstantExhaustedStepFixtureAsync(
            string testRoot,
            string roomDirectory)
    {
        Directory.CreateDirectory(roomDirectory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("parked-probe"),
            1,
            [new WorkflowStepDefinition(new StepId("implement"), "implement", [], ["out"], [], new RetryPolicy(3))]);
        var snapshot = SnapshotBinder.Bind(definition);
        var snapshotPath = Path.Combine(roomDirectory, "snapshot.json");
        await SnapshotBinder.PersistAsync(snapshot, snapshotPath, TestContext.Current.CancellationToken);

        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var executionId = new ExecutionId("exec-parked-1");
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

        await using (var writer = new FlowEventLogWriter(logPath))
        {
            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(request), TestContext.Current.CancellationToken);
            await writer.AppendAsync(
                new FlowEvent.ExecutionFailed(executionId, FailureClassification.ExhaustedUntil, "quota exhausted", RetryNotBefore: null),
                TestContext.Current.CancellationToken);
        }

        return (snapshotPath, logPath, executionId);
    }

    /// <summary>
    /// #1530: one fixture room covering every <see cref="Baton.Status.ArrestOutcome"/> shape the
    /// ledger can render, plus the one shape that produces no <see cref="ArrestOutcome"/> at all
    /// (still pending) — Delivered, Rejected (both the ordinary request-then-reject pairing and the
    /// orphan rejection with no preceding <see cref="FlowEvent.CancellationRequested"/>, the
    /// InFlightExecutionRegistry.RequestCancellationAsync-returned-false shape the ledger used to
    /// drop silently), Expired (room.jsonl), and the room-event Rejected shape with no ExecutionId
    /// (ArrestRequestUnresolvable). <see cref="StatusCommand_text_and_json_report_every_arrest_outcome_kind_text"/>
    /// and its <c>_json</c> sibling both drive this SAME fixture, so the two renderings can never
    /// silently diverge over which entries exist.
    /// </summary>
    private static async Task<string> WriteFullArrestLedgerFixtureAsync(string roomDirectory)
    {
        Directory.CreateDirectory(roomDirectory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("arrest-ledger-probe"),
            1,
            [new WorkflowStepDefinition(new StepId("implement"), "implement", [], ["out"], [], new RetryPolicy(3))]);
        var snapshot = SnapshotBinder.Bind(definition);
        var snapshotPath = Path.Combine(roomDirectory, "snapshot.json");
        await SnapshotBinder.PersistAsync(snapshot, snapshotPath, TestContext.Current.CancellationToken);

        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var deliveredExecutionId = new ExecutionId("exec-delivered");
        var rejectedExecutionId = new ExecutionId("exec-rejected-paired");
        var orphanRejectedExecutionId = new ExecutionId("exec-rejected-orphan");

        await using (var writer = new FlowEventLogWriter(logPath))
        {
            await writer.AppendAsync(
                new FlowEvent.CancellationRequested(deliveredExecutionId, CancellationOrigin.Operator),
                TestContext.Current.CancellationToken);
            await writer.AppendAsync(new FlowEvent.ExecutionCancelled(deliveredExecutionId), TestContext.Current.CancellationToken);

            await writer.AppendAsync(
                new FlowEvent.CancellationRequested(rejectedExecutionId, CancellationOrigin.Operator),
                TestContext.Current.CancellationToken);
            await writer.AppendAsync(
                new FlowEvent.CancellationRejected(rejectedExecutionId, "arrest requested but not yet confirmed settled after 5 polls"),
                TestContext.Current.CancellationToken);

            // The orphan shape ArrestLedgerProjector.Project's own remarks on its CancellationRejected
            // case describe: no preceding CancellationRequested at all.
            await writer.AppendAsync(
                new FlowEvent.CancellationRejected(orphanRejectedExecutionId, "not currently in flight when this cancel.request was checked — too late (it already settled)"),
                TestContext.Current.CancellationToken);
        }

        var roomLogPath = Path.Combine(roomDirectory, "room.jsonl");
        await using (var roomWriter = new RoomEventLogWriter(roomLogPath))
        {
            var t1 = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);
            var t2 = new DateTimeOffset(2026, 9, 1, 10, 0, 2, TimeSpan.Zero);
            await roomWriter.AppendAsync(
                new RoomEvent.ArrestRequestUnresolvable("latest", "ambiguous — 2 candidates", t1, t2),
                TestContext.Current.CancellationToken);
            await roomWriter.AppendAsync(
                new RoomEvent.ArrestRequestExpired("exec-expired", t1, t2),
                TestContext.Current.CancellationToken);
        }

        return roomDirectory;
    }

    [Fact]
    public async Task StatusCommand_text_and_json_report_every_arrest_outcome_kind_text()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            await WriteFullArrestLedgerFixtureAsync(roomDirectory);

            var output = new StringWriter();
            await StatusCommand.ExecuteAsync(new StatusOptions(roomDirectory), output, TestContext.Current.CancellationToken);

            var text = output.ToString();
            Assert.Contains("Arrests:", text);
            Assert.Contains("exec-delivered requested by operator @ ", text);
            Assert.Contains("— delivered", text);
            Assert.Contains("exec-rejected-paired requested by operator @ ", text);
            Assert.Contains("rejected (arrest requested but not yet confirmed settled after 5 polls)", text);
            // The orphan rejection (no preceding CancellationRequested) must still render, not be
            // silently dropped -- the exact HIGH finding this fixture exists to close.
            Assert.Contains("exec-rejected-orphan requested by operator @ ", text);
            Assert.Contains("rejected (not currently in flight when this cancel.request was checked — too late (it already settled))", text);
            Assert.Contains("latest requested by operator @ ", text);
            Assert.Contains("rejected (ambiguous — 2 candidates)", text);
            Assert.Contains("exec-expired requested by operator @ ", text);
            Assert.Contains("— expired", text);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task StatusCommand_text_and_json_report_every_arrest_outcome_kind_json()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            await WriteFullArrestLedgerFixtureAsync(roomDirectory);

            var output = new StringWriter();
            await StatusCommand.ExecuteAsync(new StatusOptions(roomDirectory, Json: true), output, TestContext.Current.CancellationToken);

            using var document = JsonDocument.Parse(output.ToString());
            var arrests = document.RootElement.GetProperty("arrests").EnumerateArray().ToList();
            Assert.Equal(5, arrests.Count);

            ArrestEntry Find(string target) => arrests
                .Select(e => new ArrestEntry(
                    e.GetProperty("target").GetString()!,
                    e.TryGetProperty("outcome", out var o) ? o.GetString() : null,
                    e.TryGetProperty("reason", out var r) ? r.GetString() : null))
                .Single(e => e.Target == target);

            Assert.Equal("delivered", Find("exec-delivered").Outcome);
            Assert.Equal("rejected", Find("exec-rejected-paired").Outcome);
            Assert.Equal("arrest requested but not yet confirmed settled after 5 polls", Find("exec-rejected-paired").Reason);
            Assert.Equal("rejected", Find("exec-rejected-orphan").Outcome);
            Assert.Equal(
                "not currently in flight when this cancel.request was checked — too late (it already settled)",
                Find("exec-rejected-orphan").Reason);
            Assert.Equal("rejected", Find("latest").Outcome);
            Assert.Equal("ambiguous — 2 candidates", Find("latest").Reason);
            Assert.Equal("expired", Find("exec-expired").Outcome);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    private sealed record ArrestEntry(string Target, string? Outcome, string? Reason);

    /// <summary>
    /// LOW finding: pins the degrade behaviour <see cref="StatusCommand.ExecuteAsync"/>'s own remarks
    /// on its room.jsonl read describe (version skew — a RoomEvent discriminator this build does not
    /// know).
    /// </summary>
    [Fact]
    public async Task StatusCommand_degrades_the_ledger_instead_of_failing_when_room_jsonl_is_unreadable()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            await WriteFullArrestLedgerFixtureAsync(roomDirectory);
            var roomLogPath = Path.Combine(roomDirectory, "room.jsonl");
            await File.WriteAllTextAsync(
                roomLogPath, """{"$type":"noSuchDiscriminator","foo":"bar"}""" + "\n", TestContext.Current.CancellationToken);

            var output = new StringWriter();
            await StatusCommand.ExecuteAsync(new StatusOptions(roomDirectory), output, TestContext.Current.CancellationToken);

            var text = output.ToString();
            Assert.Contains("Arrests: ledger unavailable", text);
            Assert.Contains("Workflow status:", text);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// #2045: the text rendering above was the only pinned side of the #1916 degrade — nothing pinned
    /// <c>--json</c>'s own <c>arrestLedgerUnavailableReason</c>, the field whose whole job is to
    /// separate a failed read from an un-arrested room (see
    /// <see cref="Baton.Status.WorkflowStatusView.ArrestLedgerUnavailableReason"/> for that
    /// distinction — <c>arrests</c> is absent either way). Both arms in one test: the clean read is
    /// the polarity control, since a field that were always present would satisfy the failure arm
    /// while telling a reader nothing.
    /// </summary>
    [Fact]
    public async Task StatusCommand_json_carries_the_unavailable_reason_only_when_the_ledger_read_failed()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            await WriteFullArrestLedgerFixtureAsync(roomDirectory);

            // Control arm: the same fixture, read cleanly -- arrests present, no reason field at all.
            var cleanOutput = new StringWriter();
            await StatusCommand.ExecuteAsync(
                new StatusOptions(roomDirectory, Json: true), cleanOutput, TestContext.Current.CancellationToken);

            using (var cleanDocument = JsonDocument.Parse(cleanOutput.ToString()))
            {
                Assert.True(cleanDocument.RootElement.TryGetProperty("arrests", out _));
                Assert.False(cleanDocument.RootElement.TryGetProperty("arrestLedgerUnavailableReason", out _));
            }

            // Failure arm: the same version-skew corruption the text-mode test above uses.
            var roomLogPath = Path.Combine(roomDirectory, "room.jsonl");
            await File.WriteAllTextAsync(
                roomLogPath, """{"$type":"noSuchDiscriminator","foo":"bar"}""" + "\n", TestContext.Current.CancellationToken);

            // The --json path also writes a diagnostic line to stderr (this class does not capture it,
            // exactly as the text-mode degrade test above does not -- stdout is the contract here).
            var degradedOutput = new StringWriter();
            await StatusCommand.ExecuteAsync(
                new StatusOptions(roomDirectory, Json: true), degradedOutput, TestContext.Current.CancellationToken);

            using var degradedDocument = JsonDocument.Parse(degradedOutput.ToString());
            Assert.True(
                degradedDocument.RootElement.TryGetProperty("arrestLedgerUnavailableReason", out var reason),
                "expected --json to carry the reason the ledger read failed");
            Assert.False(string.IsNullOrWhiteSpace(reason.GetString()));

            // The degrade is scoped to the ledger: `arrests` goes absent (it is not emitted as an
            // empty array), and the rest of the view still projects.
            Assert.False(degradedDocument.RootElement.TryGetProperty("arrests", out _));
            Assert.True(degradedDocument.RootElement.TryGetProperty("steps", out _));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    private static async Task<string> WriteThreeStepWorkflowAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("three-step-linear"),
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
                TimeSpan.FromMinutes(3)),
            ["critic"] = new WorkerBindingConfigEntry(
                "shell",
                new WorkerContract("critic", ["plan"], [new ProducedOutput("review")], []),
                CopyFirstInputCommand("review"),
                TimeSpan.FromMinutes(3)),
            ["publisher"] = new WorkerBindingConfigEntry(
                "shell",
                new WorkerContract("publisher", ["review"], [new ProducedOutput("summary")], []),
                CopyFirstInputCommand("summary"),
                TimeSpan.FromMinutes(3)),
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }

    /// <summary>
    /// Three-step chain where step 2 ("critic") waits for a signal file created when
    /// <c>baton status --follow</c> begins its output, eliminating wall-clock races while guaranteeing
    /// that follow observes intermediate events as they land.
    /// </summary>
    private static async Task<string> WriteThreeStepGatedBindingsAsync(string directory, string signalFilePath)
    {
        Directory.CreateDirectory(directory);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry(
                "shell",
                new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                WriteFileCommand("plan", "the-plan"),
                TimeSpan.FromMinutes(3)),
            ["critic"] = new WorkerBindingConfigEntry(
                "shell",
                new WorkerContract("critic", ["plan"], [new ProducedOutput("review")], []),
                GatedCopyFirstInputCommand("review", signalFilePath),
                TimeSpan.FromMinutes(3)),
            ["publisher"] = new WorkerBindingConfigEntry(
                "shell",
                new WorkerContract("publisher", ["review"], [new ProducedOutput("summary")], []),
                CopyFirstInputCommand("summary"),
                TimeSpan.FromMinutes(3)),
        };

        var path = Path.Combine(directory, "gated-bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }

    private static string WriteFileCommand(string outputName, string content) =>
        $"echo {content}>%BATON_OUTPUT_DIR%\\{outputName}";

    private static string CopyFirstInputCommand(string outputName) =>
        $"type %BATON_INPUT_0% >%BATON_OUTPUT_DIR%\\{outputName}";

    private static string GatedCopyFirstInputCommand(string outputName, string signalFilePath)
    {
        // && on both arms, never cmd's & (#809): & runs the payload even when the gating
        // powershell fails to spawn, which skips the gate SILENTLY -- the run reaches Terminal
        // before the follow's first read and the assert fails with a missing-events signature
        // instead of naming the gate. Fail closed: a gate that cannot run fails the step loudly.
        // The wait loop's timeout-expiry path still exits 0, so best-effort semantics after 60s
        // are unchanged.
        var normalizedPath = signalFilePath.Replace("\\", "/");
        return $"powershell -NoProfile -Command \"for ($i=0; $i -lt 1200; $i++) {{ if (Test-Path '{normalizedPath}') {{ break }}; Start-Sleep -Milliseconds 50 }}\" && type %BATON_INPUT_0% >%BATON_OUTPUT_DIR%\\{outputName}";
    }
}

