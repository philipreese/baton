using Baton.Vendors;
using Baton.Cli.Tests.TestSupport;
using Baton.Artifacts;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Status;

namespace Baton.Cli.Tests;

/// <summary>
/// Unit coverage for <see cref="ExecutionUsageProjector.BuildByExecutionId"/> (issue #1360), isolated
/// from a full <c>RunCommand</c> dispatch: hand-assembled <see cref="LogEntry"/> lists and a
/// hand-written <c>.stdout.log</c>, so the wall-clock arithmetic and the "no terminal pair yet"
/// absence rule are each pinned directly rather than only inferred from an end-to-end room.
/// </summary>
public sealed class ExecutionUsageProjectorTests
{
    [Fact]
    public void An_execution_with_both_start_and_exit_reports_the_exact_millisecond_delta()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-projector-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("exec-1");
            var start = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            var exit = start.AddMilliseconds(2500);

            var entries = new List<LogEntry>
            {
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(executionId, Pid: 123), start),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(executionId, 0, CoreExitReason.Natural), exit),
            };

            var usage = ExecutionUsageProjector.BuildByExecutionId(entries, testRoot, WorkerAdapterRegistry.Default);

            var view = Assert.Single(usage).Value;
            Assert.Equal(2500, view.WallClockMs);
            Assert.Null(view.TokensIn);
            Assert.Null(view.TokensOut);
            Assert.Null(view.Turns);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void An_artifact_checkpoint_has_its_own_usage_row_and_predecessor_lineage()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-projector-checkpoint-{Guid.NewGuid():N}");
        try
        {
            var predecessor = new ExecutionId("exec-arrested");
            var checkpoint = new ExecutionId("exec-checkpoint");
            var start = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            var entries = new List<LogEntry>
            {
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(predecessor, Pid: 123), start),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(predecessor, -1, CoreExitReason.CancelRequested), start.AddSeconds(2)),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(checkpoint, Pid: 124), start.AddSeconds(2)),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(checkpoint, 0, CoreExitReason.Natural), start.AddSeconds(3)),
                new LogEntry.FlowLogEntry(new FlowEvent.ArtifactCheckpointAttempted(
                    checkpoint, predecessor, ["report.md"], CoreExitReason.Natural, new WorkerUsage(TokensIn: 7, TokensOut: 3)), start.AddSeconds(3)),
            };

            var usage = ExecutionUsageProjector.BuildByExecutionId(entries, testRoot, WorkerAdapterRegistry.Default);

            Assert.Equal(2, usage.Count);
            var checkpointUsage = usage[checkpoint.Value];
            Assert.Equal(predecessor.Value, checkpointUsage.PredecessorExecutionId);
            Assert.Equal(7, checkpointUsage.TokensIn);
            Assert.Equal(3, checkpointUsage.TokensOut);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void An_execution_with_only_a_start_event_is_entirely_absent_never_a_zero_wall_clock()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-projector-{Guid.NewGuid():N}");
        try
        {
            var stillRunning = new ExecutionId("exec-running");
            var entries = new List<LogEntry>
            {
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(stillRunning, Pid: 456), DateTime.UtcNow),
            };

            var usage = ExecutionUsageProjector.BuildByExecutionId(entries, testRoot, WorkerAdapterRegistry.Default);

            Assert.Empty(usage);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void An_execution_with_no_captured_stdout_file_reports_wall_clock_with_no_token_fields()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-projector-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("exec-no-stdout");
            var start = DateTime.UtcNow;
            var entries = new List<LogEntry>
            {
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(executionId, Pid: 1), start),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(executionId, 0, CoreExitReason.Natural), start.AddSeconds(1)),
            };

            // No .stdout.log written to disk at all -- the execution's output directory never even exists.
            var usage = ExecutionUsageProjector.BuildByExecutionId(entries, testRoot, WorkerAdapterRegistry.Default);

            var view = Assert.Single(usage).Value;
            Assert.Equal(1000, view.WallClockMs);
            Assert.Null(view.TokensIn);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void An_execution_whose_exit_timestamp_precedes_its_start_is_entirely_absent_never_a_zero_wall_clock()
    {
        // #1360 F6: pins the clamp-avoidance ExecutionUsageProjector.BuildByExecutionId documents --
        // see its own remarks for why. Here: a backwards clock step (NTP correction, VM resume)
        // between the two DateTime.UtcNow stamps produces a negative delta, and the result must treat
        // it the same as a still-running execution -- absent entirely, not a printed 0.
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-projector-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("exec-backwards-clock");
            var start = new DateTime(2026, 1, 1, 12, 0, 5, DateTimeKind.Utc);
            var exit = start.AddSeconds(-3);
            var entries = new List<LogEntry>
            {
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(executionId, Pid: 1), start),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(executionId, 0, CoreExitReason.Natural), exit),
            };

            var usage = ExecutionUsageProjector.BuildByExecutionId(entries, testRoot, WorkerAdapterRegistry.Default);

            Assert.Empty(usage);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void Token_counts_are_still_read_after_a_retention_sweep_moves_stdout_log_to_the_pruned_path()
    {
        // #1360 F7: pins the fallback ExecutionUsageProjector.TryReadWorkerUsage documents -- see
        // that method's own remarks for why the fallback exists. Here: only the pruned path has
        // .stdout.log, so the live-path lookup must fail through to it rather than giving up.
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-projector-pruned-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("exec-pruned");
            var start = DateTime.UtcNow;
            WriteBindings(testRoot, ("plan", "claude"));
            var entries = new List<LogEntry>
            {
                new LogEntry.FlowLogEntry(new FlowEvent.ExecutionRequestAccepted(AcceptedRequest(executionId, "plan"))),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(executionId, Pid: 1), start),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(executionId, 0, CoreExitReason.Natural), start.AddSeconds(4)),
            };

            // No execution_<id> directory at all -- only its pruned counterpart exists, as after a sweep.
            var prunedDir = ArtifactManager.ResolvePrunedOutputDirectory(testRoot, executionId);
            Directory.CreateDirectory(prunedDir);
            File.WriteAllText(
                Path.Combine(prunedDir, ExecutionStreamLogger.StdoutLogFileName),
                """{"type":"result","num_turns":6,"usage":{"input_tokens":12,"output_tokens":9}}""" + "\n");

            var usage = ExecutionUsageProjector.BuildByExecutionId(entries, testRoot, WorkerAdapterRegistry.Default, testRoot);

            var view = Assert.Single(usage).Value;
            Assert.Equal(4000, view.WallClockMs);
            Assert.Equal(12, view.TokensIn);
            Assert.Equal(9, view.TokensOut);
            Assert.Equal(6, view.Turns);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void Token_and_turn_counts_are_read_when_the_execution_is_attributed_to_its_dispatching_adapter()
    {
        // #1360 F1: attribution, not content-sniffing -- the claude-shaped line is only trusted
        // because the execution's own ExecutionRequestAccepted names worker role "plan", and
        // bindings.json maps "plan" to the "claude" adapter, the SAME adapter this execution actually
        // ran through.
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-projector-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("exec-claude-shaped");
            var start = DateTime.UtcNow;
            WriteBindings(testRoot, ("plan", "claude"));
            var entries = new List<LogEntry>
            {
                new LogEntry.FlowLogEntry(new FlowEvent.ExecutionRequestAccepted(AcceptedRequest(executionId, "plan"))),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(executionId, Pid: 1), start),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(executionId, 0, CoreExitReason.Natural), start.AddSeconds(3)),
            };

            var outputDir = ArtifactManager.ResolveOutputDirectory(testRoot, executionId);
            Directory.CreateDirectory(outputDir);
            File.WriteAllText(
                Path.Combine(outputDir, ExecutionStreamLogger.StdoutLogFileName),
                """{"type":"result","num_turns":4,"usage":{"input_tokens":7,"output_tokens":3}}""" + "\n");

            var usage = ExecutionUsageProjector.BuildByExecutionId(entries, testRoot, WorkerAdapterRegistry.Default, testRoot);

            var view = Assert.Single(usage).Value;
            Assert.Equal(3000, view.WallClockMs);
            Assert.Equal(7, view.TokensIn);
            Assert.Equal(3, view.TokensOut);
            Assert.Equal(4, view.Turns);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void Cache_and_thinking_token_fields_flow_through_to_the_projected_view_when_the_line_carries_them()
    {
        // #1569: the new fields must reach ExecutionUsageView, not just WorkerUsage -- pins the
        // constructor wiring in BuildByExecutionId, not only ClaudeWorkerAdapter's own parse.
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-projector-cache-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("exec-cache-thinking");
            var start = DateTime.UtcNow;
            WriteBindings(testRoot, ("plan", "claude"));
            var entries = new List<LogEntry>
            {
                new LogEntry.FlowLogEntry(new FlowEvent.ExecutionRequestAccepted(AcceptedRequest(executionId, "plan"))),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(executionId, Pid: 1), start),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(executionId, 0, CoreExitReason.Natural), start.AddSeconds(2)),
            };

            var outputDir = ArtifactManager.ResolveOutputDirectory(testRoot, executionId);
            Directory.CreateDirectory(outputDir);
            File.WriteAllText(
                Path.Combine(outputDir, ExecutionStreamLogger.StdoutLogFileName),
                """{"type":"result","num_turns":1,"usage":{"input_tokens":2,"output_tokens":17,"cache_creation_input_tokens":0,"cache_read_input_tokens":38741,"output_tokens_details":{"thinking_tokens":6}}}""" + "\n");

            var usage = ExecutionUsageProjector.BuildByExecutionId(entries, testRoot, WorkerAdapterRegistry.Default, testRoot);

            var view = Assert.Single(usage).Value;
            Assert.Equal(2, view.TokensIn);
            Assert.Equal(17, view.TokensOut);
            Assert.Equal(38741, view.CacheReadTokens);
            Assert.Equal(0, view.CacheCreationTokens);
            Assert.Equal(6, view.ThinkingTokens);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void A_worker_echoing_a_vendor_shaped_usage_line_it_did_not_itself_produce_yields_no_token_fields()
    {
        // #1360 F1 spoof regression: a `command`-worker step's stdout is operator-supplied and can
        // contain anything, including a captured claude transcript line -- but this execution was
        // dispatched through the "command" adapter (per bindings.json), which never overrides
        // TryParseFinalUsage, so the spoofed line must never reach ClaudeWorkerAdapter's parser even
        // though ClaudeWorkerAdapter is registered in the same adapters map. Only wallClockMs may
        // survive; the fabricated 100/50/3 must not.
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-projector-spoof-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("exec-spoofed");
            var start = DateTime.UtcNow;
            WriteBindings(testRoot, ("plan", CommandWorkerAdapter.AdapterName));
            var entries = new List<LogEntry>
            {
                new LogEntry.FlowLogEntry(new FlowEvent.ExecutionRequestAccepted(AcceptedRequest(executionId, "plan"))),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(executionId, Pid: 1), start),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(executionId, 0, CoreExitReason.Natural), start.AddSeconds(2.5)),
            };

            var outputDir = ArtifactManager.ResolveOutputDirectory(testRoot, executionId);
            Directory.CreateDirectory(outputDir);
            File.WriteAllText(
                Path.Combine(outputDir, ExecutionStreamLogger.StdoutLogFileName),
                """{"type":"result","num_turns":3,"usage":{"input_tokens":100,"output_tokens":50}}""" + "\n");

            var usage = ExecutionUsageProjector.BuildByExecutionId(entries, testRoot, WorkerAdapterRegistry.Default, testRoot);

            var view = Assert.Single(usage).Value;
            Assert.Equal(2500, view.WallClockMs);
            Assert.Null(view.TokensIn);
            Assert.Null(view.TokensOut);
            Assert.Null(view.Turns);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void A_recorded_adapter_wins_over_bindings_json_even_after_failover_edits_the_file()
    {
        // Issue #1567, quota-design S1 -- see ExecutionRequest.Adapter's doc comment for the full
        // design and the keystone defect this pins. This execution's own ExecutionRequestAccepted
        // records "agy"; bindings.json has since been rebound "plan" to "claude". The recorded value
        // must win.
        //
        // The two vendors' terminal-usage envelopes are shaped differently (AgyUsageParser needs
        // "event":"result" wrapping a nested "result" object; ClaudeUsageParser needs a flat
        // "type":"result"), so picking the wrong parser for a captured agy line doesn't produce a
        // plausible wrong number here -- it fails to parse at all, silently losing real token/turn
        // data for an execution that genuinely has it. That silent loss is exactly what this test
        // pins: this fails against current main, where bindings.json's post-failover "claude" wins
        // and ClaudeUsageParser can't read the agy-shaped line.
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-projector-failover-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("exec-failed-over");
            var start = DateTime.UtcNow;
            WriteBindings(testRoot, ("plan", "claude"));
            var entries = new List<LogEntry>
            {
                new LogEntry.FlowLogEntry(new FlowEvent.ExecutionRequestAccepted(AcceptedRequest(executionId, "plan", adapter: "agy"))),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(executionId, Pid: 1), start),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(executionId, 0, CoreExitReason.Natural), start.AddSeconds(3)),
            };

            var outputDir = ArtifactManager.ResolveOutputDirectory(testRoot, executionId);
            Directory.CreateDirectory(outputDir);
            File.WriteAllText(
                Path.Combine(outputDir, ExecutionStreamLogger.StdoutLogFileName),
                """{"event":"result","result":{"num_turns":5,"usage":{"input_tokens":21,"output_tokens":13}}}""" + "\n");

            var usage = ExecutionUsageProjector.BuildByExecutionId(entries, testRoot, WorkerAdapterRegistry.Default, testRoot);

            var view = Assert.Single(usage).Value;
            Assert.Equal(21, view.TokensIn);
            Assert.Equal(13, view.TokensOut);
            Assert.Equal(5, view.Turns);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void Recorded_adapter_absent_falls_back_to_bindings_json_recorded_adapter_present_ignores_it()
    {
        // Polarity, both directions, and both arms parse a REAL number rather than one of them merely
        // asserting null -- an implementation with no fallback at all (recordedAdapter or nothing)
        // would satisfy a null-vs-non-null assertion just as well as the real fallback does. Instead:
        // bindings.json names "claude" for this worker; the first execution has no recorded Adapter,
        // so it must resolve THROUGH bindings.json to "claude" and parse the claude-shaped line. The
        // second execution's own ExecutionRequestAccepted records "agy" -- a DIFFERENT adapter than
        // bindings.json names for the same worker -- and carries an agy-shaped line instead; it must
        // resolve to "agy" and ignore bindings.json's "claude" entirely. Swapping either execution's
        // envelope shape for the other's adapter would fail to parse, so each arm is only satisfiable
        // by the correct resolution.
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-projector-polarity-{Guid.NewGuid():N}");
        try
        {
            WriteBindings(testRoot, ("plan", "claude"));

            var noRecordedAdapter = new ExecutionId("exec-no-recorded-adapter");
            var recordedAdapterPresent = new ExecutionId("exec-recorded-adapter-present");
            var start = DateTime.UtcNow;
            var entries = new List<LogEntry>
            {
                new LogEntry.FlowLogEntry(new FlowEvent.ExecutionRequestAccepted(AcceptedRequest(noRecordedAdapter, "plan", adapter: null))),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(noRecordedAdapter, Pid: 1), start),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(noRecordedAdapter, 0, CoreExitReason.Natural), start.AddSeconds(1)),

                new LogEntry.FlowLogEntry(new FlowEvent.ExecutionRequestAccepted(AcceptedRequest(recordedAdapterPresent, "plan", adapter: "agy"))),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(recordedAdapterPresent, Pid: 2), start),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(recordedAdapterPresent, 0, CoreExitReason.Natural), start.AddSeconds(1)),
            };

            var noRecordedAdapterOutputDir = ArtifactManager.ResolveOutputDirectory(testRoot, noRecordedAdapter);
            Directory.CreateDirectory(noRecordedAdapterOutputDir);
            File.WriteAllText(
                Path.Combine(noRecordedAdapterOutputDir, ExecutionStreamLogger.StdoutLogFileName),
                """{"type":"result","num_turns":2,"usage":{"input_tokens":8,"output_tokens":4}}""" + "\n");

            var recordedAdapterPresentOutputDir = ArtifactManager.ResolveOutputDirectory(testRoot, recordedAdapterPresent);
            Directory.CreateDirectory(recordedAdapterPresentOutputDir);
            File.WriteAllText(
                Path.Combine(recordedAdapterPresentOutputDir, ExecutionStreamLogger.StdoutLogFileName),
                """{"event":"result","result":{"num_turns":9,"usage":{"input_tokens":55,"output_tokens":22}}}""" + "\n");

            var usage = ExecutionUsageProjector.BuildByExecutionId(entries, testRoot, WorkerAdapterRegistry.Default, testRoot);

            Assert.Equal(8, usage[noRecordedAdapter.Value].TokensIn);
            Assert.Equal(55, usage[recordedAdapterPresent.Value].TokensIn);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void An_execution_with_no_ExecutionRequestAccepted_record_yields_no_token_fields_even_with_a_matching_bindings_entry()
    {
        // #1360 F1: attribution needs BOTH halves -- a worker-role name from the ledger AND that
        // role's adapter from bindings.json. A room with bindings.json but no accepted-request event
        // for this execution (an older ledger, or a log gap) cannot resolve the first half, so it
        // must fail closed exactly like a missing bindings file does.
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-projector-noaccept-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("exec-no-accept");
            var start = DateTime.UtcNow;
            WriteBindings(testRoot, ("plan", "claude"));
            var entries = new List<LogEntry>
            {
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(executionId, Pid: 1), start),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(executionId, 0, CoreExitReason.Natural), start.AddSeconds(1)),
            };

            var outputDir = ArtifactManager.ResolveOutputDirectory(testRoot, executionId);
            Directory.CreateDirectory(outputDir);
            File.WriteAllText(
                Path.Combine(outputDir, ExecutionStreamLogger.StdoutLogFileName),
                """{"type":"result","num_turns":1,"usage":{"input_tokens":1,"output_tokens":1}}""" + "\n");

            var usage = ExecutionUsageProjector.BuildByExecutionId(entries, testRoot, WorkerAdapterRegistry.Default, testRoot);

            var view = Assert.Single(usage).Value;
            Assert.Null(view.TokensIn);
            Assert.Null(view.TokensOut);
            Assert.Null(view.Turns);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void A_rebound_execution_attributes_usage_to_the_new_binding_from_StepRebound()
    {
        // Issue #1583 (operator ruling 2026-09-01): when an execution is rebound, Flow journals
        // FlowEvent.StepRebound naming old->new. ExecutionUsageProjector must honor that event and
        // attribute usage using the new adapter's parser rather than the frozen ExecutionRequest's
        // recorded adapter.
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-projector-rebound-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("exec-rebound");
            var stepId = new StepId("plan");
            var start = DateTime.UtcNow;
            WriteBindings(testRoot, ("plan", "agy"));
            var entries = new List<LogEntry>
            {
                new LogEntry.FlowLogEntry(new FlowEvent.ExecutionRequestAccepted(AcceptedRequest(executionId, "plan", adapter: "agy"))),
                new LogEntry.FlowLogEntry(new FlowEvent.StepRebound(stepId, executionId, PreviousAdapter: "agy", NewAdapter: "claude")),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(executionId, Pid: 1), start),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(executionId, 0, CoreExitReason.Natural), start.AddSeconds(3)),
            };

            var outputDir = ArtifactManager.ResolveOutputDirectory(testRoot, executionId);
            Directory.CreateDirectory(outputDir);
            File.WriteAllText(
                Path.Combine(outputDir, ExecutionStreamLogger.StdoutLogFileName),
                """{"type":"result","num_turns":3,"usage":{"input_tokens":100,"output_tokens":50}}""" + "\n");

            var usage = ExecutionUsageProjector.BuildByExecutionId(entries, testRoot, WorkerAdapterRegistry.Default, testRoot);

            var view = Assert.Single(usage).Value;
            Assert.Equal(100, view.TokensIn);
            Assert.Equal(50, view.TokensOut);
            Assert.Equal(3, view.Turns);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void A_rebound_execution_without_StepRebound_attributes_to_the_originally_recorded_adapter()
    {
        // Polarity partner to the test above: without FlowEvent.StepRebound, ExecutionUsageProjector
        // trusts the accepted request's recorded "agy" adapter. When the log is claude-shaped,
        // AgyUsageParser fails to parse, demonstrating that StepRebound is what flipped attribution.
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-projector-no-rebound-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("exec-no-rebound");
            var start = DateTime.UtcNow;
            WriteBindings(testRoot, ("plan", "agy"));
            var entries = new List<LogEntry>
            {
                new LogEntry.FlowLogEntry(new FlowEvent.ExecutionRequestAccepted(AcceptedRequest(executionId, "plan", adapter: "agy"))),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(executionId, Pid: 1), start),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(executionId, 0, CoreExitReason.Natural), start.AddSeconds(3)),
            };

            var outputDir = ArtifactManager.ResolveOutputDirectory(testRoot, executionId);
            Directory.CreateDirectory(outputDir);
            File.WriteAllText(
                Path.Combine(outputDir, ExecutionStreamLogger.StdoutLogFileName),
                """{"type":"result","num_turns":3,"usage":{"input_tokens":100,"output_tokens":50}}""" + "\n");

            var usage = ExecutionUsageProjector.BuildByExecutionId(entries, testRoot, WorkerAdapterRegistry.Default, testRoot);

            var view = Assert.Single(usage).Value;
            Assert.Null(view.TokensIn);
            Assert.Null(view.TokensOut);
            Assert.Null(view.Turns);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void Two_StepRebound_events_for_the_same_execution_leave_attribution_on_the_binding_that_actually_ran()
    {
        // #1583 HIGH, review scenario B: a rebind claude->agy followed by a reverting rebind agy->claude
        // (the second one journaled only once StateProjector.ApplyEvent projects the first as an
        // override -- see MutationInterfaceCrashRecoveryTests' write-side pin of the same scenario).
        // The read side must land on "claude" -- the binding that actually produced this claude-shaped
        // stdout -- via last-write-wins over the two StepRebound lines, not on the intermediate "agy".
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-projector-double-rebound-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("exec-double-rebound");
            var stepId = new StepId("plan");
            var start = DateTime.UtcNow;
            WriteBindings(testRoot, ("plan", "claude"));
            var entries = new List<LogEntry>
            {
                new LogEntry.FlowLogEntry(new FlowEvent.ExecutionRequestAccepted(AcceptedRequest(executionId, "plan", adapter: "claude"))),
                new LogEntry.FlowLogEntry(new FlowEvent.StepRebound(stepId, executionId, PreviousAdapter: "claude", NewAdapter: "agy")),
                new LogEntry.FlowLogEntry(new FlowEvent.StepRebound(stepId, executionId, PreviousAdapter: "agy", NewAdapter: "claude")),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(executionId, Pid: 1), start),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(executionId, 0, CoreExitReason.Natural), start.AddSeconds(3)),
            };

            var outputDir = ArtifactManager.ResolveOutputDirectory(testRoot, executionId);
            Directory.CreateDirectory(outputDir);
            File.WriteAllText(
                Path.Combine(outputDir, ExecutionStreamLogger.StdoutLogFileName),
                """{"type":"result","num_turns":3,"usage":{"input_tokens":100,"output_tokens":50}}""" + "\n");

            var usage = ExecutionUsageProjector.BuildByExecutionId(entries, testRoot, WorkerAdapterRegistry.Default, testRoot);

            var view = Assert.Single(usage).Value;
            Assert.Equal(100, view.TokensIn);
            Assert.Equal(50, view.TokensOut);
            Assert.Equal(3, view.Turns);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // #1706: the reconciliation triple (billedTokens / liveBilledTokens / billedUnderReadTokens).
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Writes one execution's captured stream and projects it. <paramref name="rolledLines"/> go to
    /// <c>.stdout.log.1</c> — the single rollover file <c>ExecutionStreamLogger</c> writes FIRST once a
    /// stream passes 8 MiB — and <paramref name="currentLines"/> to <c>.stdout.log</c>.
    /// </summary>
    private static ExecutionUsageView ProjectStream(
        string testRoot,
        string adapter,
        IReadOnlyList<string> currentLines,
        IReadOnlyList<string>? rolledLines = null,
        bool truncatedByRollover = false,
        bool truncatedByWriteFailure = false)
    {
        var executionId = new ExecutionId("exec-1706");
        var start = DateTime.UtcNow;
        WriteBindings(testRoot, ("plan", adapter));
        var entries = new List<LogEntry>
        {
            new LogEntry.FlowLogEntry(new FlowEvent.ExecutionRequestAccepted(AcceptedRequest(executionId, "plan"))),
            new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(executionId, Pid: 1), start),
            new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(executionId, 0, CoreExitReason.Natural), start.AddSeconds(1)),
        };

        var outputDir = ArtifactManager.ResolveOutputDirectory(testRoot, executionId);
        Directory.CreateDirectory(outputDir);
        if (rolledLines is not null)
        {
            File.WriteAllLines(Path.Combine(outputDir, ExecutionStreamLogger.StdoutRolloverFileName), rolledLines);
        }

        if (truncatedByRollover)
        {
            // What ExecutionStreamLogger itself writes on the roll that DESTROYS a segment -- an empty
            // sentinel, its existence the whole payload.
            File.WriteAllBytes(Path.Combine(outputDir, ExecutionStreamLogger.StdoutTruncationMarkerFileName), []);
        }

        if (truncatedByWriteFailure)
        {
            // #1876: the other empty sentinel -- what the logger writes when its retry buffer overflows
            // or is still full at terminal.
            File.WriteAllBytes(Path.Combine(outputDir, ExecutionStreamLogger.StdoutWriteFailureMarkerFileName), []);
        }

        File.WriteAllLines(Path.Combine(outputDir, ExecutionStreamLogger.StdoutLogFileName), currentLines);

        return Assert.Single(
            ExecutionUsageProjector.BuildByExecutionId(entries, testRoot, WorkerAdapterRegistry.Default, testRoot)).Value;
    }

    private static string ClaudeAssistantLine(string messageId, long cacheCreation) =>
        "{\"type\":\"assistant\",\"message\":{\"id\":\"" + messageId
        + "\",\"usage\":{\"input_tokens\":2,\"cache_creation_input_tokens\":" + cacheCreation
        + ",\"cache_read_input_tokens\":0,\"output_tokens\":3}}}";

    /// <summary>A claude terminal line whose whole-tree <c>modelUsage</c> bills 1,000 + 500 + 4,000.</summary>
    private const string ClaudeTerminalLine =
        """{"type":"result","num_turns":2,"usage":{"input_tokens":1,"output_tokens":2,"cache_creation_input_tokens":3},"modelUsage":{"claude-opus-5":{"inputTokens":1000,"outputTokens":500,"cacheReadInputTokens":9000,"cacheCreationInputTokens":4000}}}""";

    [Fact]
    public void The_reconciliation_triple_reports_terminal_billed_live_billed_and_their_difference()
    {
        // #1706: the shipped `baton status --json` surface (spec/baton.md §3) -- terminal 5,500 billed
        // against a live floor of 1,200 (cache_creation only, deduped), so 4,300 of this room's real
        // spend was invisible to the budget while it ran.
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-projector-1706-{Guid.NewGuid():N}");
        try
        {
            var view = ProjectStream(testRoot, "claude", [
                ClaudeAssistantLine("msg_1", 700),
                ClaudeAssistantLine("msg_1", 700), // a repeat of the same id -- deduped, never summed twice
                ClaudeAssistantLine("msg_2", 500),
                ClaudeTerminalLine,
            ]);

            Assert.Equal(1000 + 500 + 4000, view.BilledTokens);
            Assert.Equal(700 + 500, view.LiveBilledTokens);
            Assert.Equal(5500 - 1200, view.BilledUnderReadTokens);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void REGRESSION_a_rolled_over_stream_replays_the_rollover_file_too_never_the_tail_alone()
    {
        // #1706 review, the defect this arm exists for: the replay originally read `.stdout.log` only.
        // Once ExecutionStreamLogger has rolled over at 8 MiB the earlier -- usually larger -- half of
        // the stream lives in `.stdout.log.1`, so the live figure came out as a fraction of the truth
        // and the reported under-read was a rollover artifact rather than a measurement -- the real
        // rolled room the projector's own doc comment quantifies. Here the rollover file carries 900 of
        // the 1,200 real cache-creation tokens, so
        // reading the tail alone would report 300 and an under-read of 5,200.
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-projector-1706-roll-{Guid.NewGuid():N}");
        try
        {
            var view = ProjectStream(
                testRoot,
                "claude",
                currentLines: [ClaudeAssistantLine("msg_3", 300), ClaudeTerminalLine],
                rolledLines: [ClaudeAssistantLine("msg_1", 400), ClaudeAssistantLine("msg_2", 500)]);

            Assert.Equal(400 + 500 + 300, view.LiveBilledTokens);
            Assert.Equal(5500 - 1200, view.BilledUnderReadTokens);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void DISCRIMINATING_the_same_stream_without_the_rollover_file_reports_the_tail_only_figure()
    {
        // The control for the arm above: identical current file, no `.stdout.log.1` on disk. If this
        // reported 1,200 the previous test would be passing for a reason unrelated to the rollover
        // read -- and a rollover file that is simply absent (every execution under 8 MiB, i.e. nearly
        // all of them) must contribute nothing rather than failing the read.
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-projector-1706-noroll-{Guid.NewGuid():N}");
        try
        {
            var view = ProjectStream(testRoot, "claude", [ClaudeAssistantLine("msg_3", 300), ClaudeTerminalLine]);

            Assert.Equal(300, view.LiveBilledTokens);
            Assert.Equal(5500 - 300, view.BilledUnderReadTokens);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void CONTROL_an_agy_stream_reconciles_to_a_ZERO_under_read()
    {
        // spec/baton.md §3 leans on agy's zero under-read to give claude's non-zero one its meaning --
        // so it needs to exist as a test, not only as prose.
        //
        // #1706 review M4: this control used to run on a synthetic stream, which asserted its own
        // arithmetic rather than anything about the vendor. It now replays room
        // `dispatch-implement-38c24d11`'s REAL capture -- 70 `agent_response` usage lines plus its own
        // terminal `result`, copied verbatim. The vendor fact underneath is measured in
        // docs/vendor-capabilities.md and pinned directly by `AgyTerminalUsageIsCumulativeTests`; what
        // this exercises is that fact reaching the surface `baton status --json` actually serves.
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-projector-1706-agy-{Guid.NewGuid():N}");
        try
        {
            var realAgyStream = File.ReadAllLines(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "agy-38c24d11-agent-response-usage.jsonl"));

            var view = ProjectStream(testRoot, "agy", realAgyStream);

            // The room's own measured totals -- input 595,684 + output 199,256, from the real terminal
            // line and, identically, from the Σ of its 70 per-turn lines.
            Assert.Equal(794_940, view.BilledTokens);
            Assert.Equal(794_940, view.LiveBilledTokens);
            Assert.Equal(0, view.BilledUnderReadTokens);
            Assert.Null(view.BilledReconciliationUnavailable);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void The_triple_is_absent_entirely_when_the_stream_carries_no_terminal_usage_line()
    {
        // Never a fabricated zero, and never a lone liveBilledTokens with nothing to reconcile it
        // against: all three go together or none does.
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-projector-1706-absent-{Guid.NewGuid():N}");
        try
        {
            var view = ProjectStream(testRoot, "claude", [ClaudeAssistantLine("msg_1", 700)]);

            Assert.Null(view.BilledTokens);
            Assert.Null(view.LiveBilledTokens);
            Assert.Null(view.BilledUnderReadTokens);
            // #1706 review M2: and the reason says WHICH half was missing, so a consumer that got no
            // triple can tell "nothing terminal to reconcile against" from "the replay was unusable".
            Assert.Equal("no-terminal-billed-figure", view.BilledReconciliationUnavailable);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void REGRESSION_billedTokens_is_never_emitted_ALONE_when_the_live_figure_is_unavailable()
    {
        // #1706 review M2 — the defect and the contract are on ExecutionUsageView and in spec/baton.md
        // §6. The reachable shape pinned here: a real terminal line with no usage-bearing line ahead of
        // it, which used to ship `billedTokens` by itself.
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-projector-1706-lone-{Guid.NewGuid():N}");
        try
        {
            var view = ProjectStream(testRoot, "claude", [
                """{"type":"system","subtype":"init","session_id":"s"}""",
                ClaudeTerminalLine,
            ]);

            Assert.Null(view.BilledTokens);
            Assert.Null(view.LiveBilledTokens);
            Assert.Null(view.BilledUnderReadTokens);
            Assert.Equal("no-live-billed-figure", view.BilledReconciliationUnavailable);
            // The rest of the terminal reading is untouched -- this clamp is about the reconciliation
            // triple only, not about withholding figures the terminal line really did report.
            Assert.Equal(2, view.Turns);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void CONTROL_the_same_stream_WITH_a_live_usage_line_emits_all_three()
    {
        // The discriminating arm for the regression above: identical shape apart from one usage-bearing
        // line. Without it, a projector that simply never emitted the triple would pass that test.
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-projector-1706-lone-ctl-{Guid.NewGuid():N}");
        try
        {
            var view = ProjectStream(testRoot, "claude", [
                """{"type":"system","subtype":"init","session_id":"s"}""",
                ClaudeAssistantLine("msg_1", 700),
                ClaudeTerminalLine,
            ]);

            Assert.Equal(5500, view.BilledTokens);
            Assert.Equal(700, view.LiveBilledTokens);
            Assert.Equal(4800, view.BilledUnderReadTokens);
            Assert.Null(view.BilledReconciliationUnavailable);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void REGRESSION_a_TWICE_rolled_stream_reports_the_under_read_as_unknown_not_as_a_number()
    {
        // #1706 review M3. Why a marker is needed at all, and why the reader cannot infer it, is on
        // `ExecutionStreamLogger.StdoutTruncationMarkerFileName` and in `ExecutionUsageProjector`'s
        // rollover arm. What this pins is the behaviour: the once-rolled fix this PR shipped would
        // otherwise produce here the very artifact it was written to remove.
        //
        // The fixture is deliberately the once-rolled test's own stream plus the marker: the number is
        // computable, and refused anyway, which is the whole point.
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-projector-1706-twice-{Guid.NewGuid():N}");
        try
        {
            var view = ProjectStream(
                testRoot,
                "claude",
                [ClaudeAssistantLine("msg_2", 500), ClaudeTerminalLine],
                rolledLines: [ClaudeAssistantLine("msg_1", 700)],
                truncatedByRollover: true);

            Assert.Equal("stream-truncated-by-rollover", view.BilledReconciliationUnavailable);
            Assert.Null(view.BilledTokens);
            Assert.Null(view.LiveBilledTokens);
            Assert.Null(view.BilledUnderReadTokens);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void A_write_failure_gap_reports_its_OWN_reason_not_the_rollover_one()
    {
        // #1876. Two gaps, two remedies -- see spec/baton.md §3. What this arm discriminates is the
        // collapse: reporting the rollover reason for both would send an operator to the retention
        // bound for a problem that is a sharing conflict. The terminal figures are withheld either way,
        // so the reason string is the only thing that tells them apart.
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-projector-1876-{Guid.NewGuid():N}");
        try
        {
            var view = ProjectStream(
                testRoot,
                "claude",
                [ClaudeAssistantLine("msg_2", 500), ClaudeTerminalLine],
                truncatedByWriteFailure: true);

            Assert.Equal("stream-truncated-by-write-failure", view.BilledReconciliationUnavailable);
            Assert.Null(view.BilledTokens);
            Assert.Null(view.LiveBilledTokens);
            Assert.Null(view.BilledUnderReadTokens);

            // Polarity, and the half #1876 exists to protect: the terminal line's own dimensions are
            // still reported. A gap withholds the RECONCILIATION, never the reading itself.
            Assert.Equal(1000, view.TokensIn);
            Assert.Equal(500, view.TokensOut);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void CONTROL_the_same_ONCE_rolled_stream_without_the_marker_still_reconciles()
    {
        // The polarity arm. Same bytes, no marker: the replay spans the whole stream and reports a real
        // figure. Without this, a projector that withheld the triple on every rolled stream -- or on
        // every stream at all -- would pass the regression above.
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-projector-1706-once-{Guid.NewGuid():N}");
        try
        {
            var view = ProjectStream(
                testRoot,
                "claude",
                [ClaudeAssistantLine("msg_2", 500), ClaudeTerminalLine],
                rolledLines: [ClaudeAssistantLine("msg_1", 700)]);

            Assert.Null(view.BilledReconciliationUnavailable);
            Assert.Equal(5500, view.BilledTokens);
            Assert.Equal(1200, view.LiveBilledTokens);
            Assert.Equal(4300, view.BilledUnderReadTokens);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void An_execution_with_no_stdout_log_at_all_reports_no_reconciliation_and_no_reason()
    {
        // #1724 item 2a: the `reading is not null` guard on ExecutionUsageView.cs -- goes red if that
        // guard is removed (i.e. changed to fire whenever `!reconciled`), since a stream that was never
        // captured at all would then wrongly acquire a "no-terminal-billed-figure" reason it never
        // earned. "No stream" and "stream read but unreconcilable" are different states, and only the
        // second one gets a reason string -- spec/baton.md §3's own "absent like the triple itself".
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-projector-1724-nostream-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("exec-no-stream-at-all");
            var start = DateTime.UtcNow;
            var entries = new List<LogEntry>
            {
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(executionId, Pid: 1), start),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(executionId, 0, CoreExitReason.Natural), start.AddSeconds(1)),
            };

            // No ExecutionRequestAccepted, no bindings.json, no output directory: TryReadWorkerUsage
            // returns null before ever touching a stream file.
            var usage = ExecutionUsageProjector.BuildByExecutionId(entries, testRoot, WorkerAdapterRegistry.Default, testRoot);

            var view = Assert.Single(usage).Value;
            Assert.Null(view.BilledTokens);
            Assert.Null(view.LiveBilledTokens);
            Assert.Null(view.BilledUnderReadTokens);
            Assert.Null(view.BilledReconciliationUnavailable);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void An_unreadable_rollover_segment_reports_rollover_segment_unreadable()
    {
        // #1724 item 2b: pins ExecutionUsageProjector's `rollover-segment-unreadable` arm
        // (ExecutionUsageProjector.cs ~430). Goes red if that catch's reason string is removed or
        // changed, or if the guard folded this case into "no-live-billed-figure" instead -- a consumer
        // needs to tell "the replay found nothing" from "the replay could not even read its own input"
        // apart, since only the second is retryable by fixing the file's sharing state.
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-projector-1724-unreadable-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("exec-1724-unreadable");
            var start = DateTime.UtcNow;
            WriteBindings(testRoot, ("plan", "claude"));
            var entries = new List<LogEntry>
            {
                new LogEntry.FlowLogEntry(new FlowEvent.ExecutionRequestAccepted(AcceptedRequest(executionId, "plan"))),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(executionId, Pid: 1), start),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(executionId, 0, CoreExitReason.Natural), start.AddSeconds(1)),
            };

            var outputDir = ArtifactManager.ResolveOutputDirectory(testRoot, executionId);
            Directory.CreateDirectory(outputDir);
            File.WriteAllLines(Path.Combine(outputDir, ExecutionStreamLogger.StdoutLogFileName), [ClaudeTerminalLine]);
            var rolloverPath = Path.Combine(outputDir, ExecutionStreamLogger.StdoutRolloverFileName);
            File.WriteAllText(rolloverPath, "unused");

            using (new FileStream(rolloverPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var usage = ExecutionUsageProjector.BuildByExecutionId(entries, testRoot, WorkerAdapterRegistry.Default, testRoot);

                var view = Assert.Single(usage).Value;
                Assert.Equal("rollover-segment-unreadable", view.BilledReconciliationUnavailable);
                Assert.Null(view.BilledTokens);
                Assert.Null(view.LiveBilledTokens);
                Assert.Null(view.BilledUnderReadTokens);
            }
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // #1709: PeakBilledInWindow -- read off the terminal outcome event, never re-derived from the
    // captured stream (that is LiveBilledTokens's own job, a REPLAY reconstruction; this is a journalled
    // measurement read back verbatim).
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void A_succeeded_execution_with_a_recorded_peak_projects_the_number()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-projector-1709-succeeded-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("exec-1709-succeeded");
            var start = DateTime.UtcNow;
            var entries = new List<LogEntry>
            {
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(executionId, Pid: 1), start),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(executionId, 0, CoreExitReason.Natural), start.AddSeconds(1)),
                new LogEntry.FlowLogEntry(new FlowEvent.ExecutionSucceeded(executionId, PeakBilledInWindow: 344_225)),
            };

            var usage = ExecutionUsageProjector.BuildByExecutionId(entries, testRoot, WorkerAdapterRegistry.Default);

            var view = Assert.Single(usage).Value;
            Assert.Equal(344_225, view.PeakBilledInWindow);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void A_failed_execution_with_a_recorded_peak_projects_the_number()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-projector-1709-failed-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("exec-1709-failed");
            var start = DateTime.UtcNow;
            var entries = new List<LogEntry>
            {
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(executionId, Pid: 1), start),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(executionId, 1, CoreExitReason.Natural), start.AddSeconds(1)),
                new LogEntry.FlowLogEntry(
                    new FlowEvent.ExecutionFailed(executionId, FailureClassification.Retryable, PeakBilledInWindow: 228_536)),
            };

            var usage = ExecutionUsageProjector.BuildByExecutionId(entries, testRoot, WorkerAdapterRegistry.Default);

            var view = Assert.Single(usage).Value;
            Assert.Equal(228_536, view.PeakBilledInWindow);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void An_execution_whose_terminal_event_carries_no_peak_projects_a_null_PeakBilledInWindow()
    {
        // The polarity control, and #1709's own replay claim: a room whose ledger line predates this
        // field (or whose execution ran with no TokenBudgetMonitor in scope) must keep projecting
        // PeakBilledInWindow as null, not a fabricated zero -- without this arm, an implementation that
        // silently defaulted to 0 would pass the two tests above just as well.
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-projector-1709-absent-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("exec-1709-no-peak");
            var start = DateTime.UtcNow;
            var entries = new List<LogEntry>
            {
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(executionId, Pid: 1), start),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(executionId, 0, CoreExitReason.Natural), start.AddSeconds(1)),
                new LogEntry.FlowLogEntry(new FlowEvent.ExecutionSucceeded(executionId)),
            };

            var usage = ExecutionUsageProjector.BuildByExecutionId(entries, testRoot, WorkerAdapterRegistry.Default);

            var view = Assert.Single(usage).Value;
            Assert.Null(view.PeakBilledInWindow);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    private static ExecutionRequest AcceptedRequest(ExecutionId executionId, string worker, string? adapter = null) => new(
        executionId,
        new WorkflowId("wf-usage-test"),
        new StepId(worker),
        worker,
        Inputs: [],
        Outputs: [],
        Timeout: TimeSpan.FromSeconds(30),
        Environment: [],
        UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>(),
        Adapter: adapter);

    private static void WriteBindings(string roomDirectoryPath, params (string WorkerName, string Adapter)[] entries)
    {
        Directory.CreateDirectory(roomDirectoryPath);
        var config = entries.ToDictionary(
            e => e.WorkerName,
            e => new WorkerBindingConfigEntry(
                e.Adapter, new WorkerContract(e.WorkerName, [], [], []), "unused prompt", TimeSpan.FromSeconds(30)));

        File.WriteAllText(
            BatonPaths.RoomBindingsFile(roomDirectoryPath),
            System.Text.Json.JsonSerializer.Serialize(config));
    }
}
