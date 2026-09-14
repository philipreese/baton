using Baton.Domain;
using Baton.Status;

namespace Baton.Tests.Status;

/// <summary>
/// spec/baton.md §7's writer/reader: <see cref="QuotaLedgerStore"/> is the fleet-level burn ledger
/// (issue #1570), sharing <see cref="MutexGuardedFileLock"/> with <c>RoomRegistryStore</c> rather than
/// a second concurrency mechanism. <see cref="QuotaLedgerStore.BuildEntries"/>'s coverage mirrors
/// <c>ExecutionUsageProjectorTests</c>' fixture style deliberately — it wraps
/// <c>ExecutionUsageProjector.BuildByExecutionId</c> rather than re-deriving usage, so this file only
/// pins the ledger-specific additions (adapter/model/outcome resolution, room/execution/at), not the
/// wall-clock/token-absence rules that file already owns.
/// </summary>
public sealed class QuotaLedgerStoreTests
{
    private static string TempLedgerPath() =>
        Path.Combine(Path.GetTempPath(), $"baton-quota-ledger-{Guid.NewGuid():N}.jsonl");

    private static ExecutionRequest AcceptedRequest(ExecutionId executionId, string worker, string? adapter, string? model) => new(
        executionId,
        new WorkflowId("wf-ledger-test"),
        new StepId(worker),
        worker,
        Inputs: [],
        Outputs: [],
        Timeout: TimeSpan.FromSeconds(30),
        Environment: [],
        UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>(),
        Adapter: adapter,
        Model: model);

    [Fact]
    public void BuildEntries_reads_adapter_model_usage_and_outcome_from_a_settled_execution()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"ledger-build-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("exec-1");
            var start = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            var exit = start.AddMilliseconds(4200);

            var entries = new List<LogEntry>
            {
                new LogEntry.FlowLogEntry(new FlowEvent.ExecutionRequestAccepted(
                    AcceptedRequest(executionId, "plan", adapter: "claude", model: "sonnet"))),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(executionId, Pid: 111), start),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(executionId, 0, CoreExitReason.Natural), exit),
                new LogEntry.FlowLogEntry(new FlowEvent.ExecutionSucceeded(executionId)),
            };

            var built = QuotaLedgerStore.BuildEntries(entries, testRoot);

            var entry = Assert.Single(built);
            Assert.Equal("exec-1", entry.Execution);
            Assert.Equal(BatonPaths.RecordKey(testRoot), entry.Room);
            Assert.Equal("claude", entry.Adapter);
            Assert.Equal("sonnet", entry.Model);
            Assert.Equal(4200, entry.WallClockMs);
            Assert.Equal("Succeeded", entry.Outcome);
            Assert.Equal(exit, entry.At);
            Assert.Null(entry.TokensIn);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void BuildEntries_reports_the_FailureClassification_member_name_as_outcome()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"ledger-build-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("exec-exhausted");
            var start = DateTime.UtcNow;

            var entries = new List<LogEntry>
            {
                new LogEntry.FlowLogEntry(new FlowEvent.ExecutionRequestAccepted(
                    AcceptedRequest(executionId, "plan", adapter: "agy", model: "gemini-3-pro"))),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(executionId, Pid: 1), start),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(executionId, 1, CoreExitReason.Natural), start.AddSeconds(1)),
                new LogEntry.FlowLogEntry(new FlowEvent.ExecutionFailed(executionId, FailureClassification.ExhaustedUntil)),
            };

            var built = QuotaLedgerStore.BuildEntries(entries, testRoot);

            var entry = Assert.Single(built);
            Assert.Equal("ExhaustedUntil", entry.Outcome);
            Assert.Equal("agy", entry.Adapter);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void BuildEntries_preserves_each_checkpoint_terminal_outcome_without_merging_its_usage()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"ledger-checkpoints-{Guid.NewGuid():N}");
        try
        {
            var start = DateTime.UtcNow;
            var predecessor = new ExecutionId("original");
            var natural = new ExecutionId("checkpoint-natural");
            var cancelled = new ExecutionId("checkpoint-cancelled");
            var arrested = new ExecutionId("checkpoint-arrested");
            var entries = new List<LogEntry>();
            foreach (var (checkpoint, exitReason, arrestReason) in new[]
            {
                (natural, CoreExitReason.Natural, (ArrestReason?)null),
                (cancelled, CoreExitReason.CancelRequested, (ArrestReason?)null),
                (arrested, CoreExitReason.TimedOut, (ArrestReason?)ArrestReason.ToolStepCap),
            })
            {
                entries.Add(new LogEntry.FlowLogEntry(new FlowEvent.ArtifactCheckpointAttempted(
                    checkpoint, predecessor, ["report.md"])));
                entries.Add(new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(checkpoint, 1), start));
                entries.Add(new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(checkpoint, 0, exitReason), start.AddSeconds(1)));
                entries.Add(new LogEntry.FlowLogEntry(new FlowEvent.ArtifactCheckpointCompleted(
                    checkpoint, exitReason, new WorkerUsage(TokensIn: 1), arrestReason)));
            }

            var built = QuotaLedgerStore.BuildEntries(entries, testRoot).ToDictionary(entry => entry.Execution!);

            Assert.Equal(3, built.Count);
            Assert.Equal("Succeeded", built[natural.Value].Outcome);
            Assert.Equal("Cancelled", built[cancelled.Value].Outcome);
            Assert.Equal("Arrested", built[arrested.Value].Outcome);
            Assert.All(built.Values, entry => Assert.Equal(predecessor.Value, entry.PredecessorExecution));
            Assert.Equal(CoreExitReason.Natural, built[natural.Value].ExitReason);
            Assert.Equal(CoreExitReason.CancelRequested, built[cancelled.Value].ExitReason);
            Assert.Equal(ArrestReason.ToolStepCap, built[arrested.Value].ArrestReason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void BuildEntries_attributes_a_rebound_execution_to_its_new_adapter_and_model()
    {
        // #1781 review finding 1: BuildEntries used to re-derive the StepRebound override itself,
        // untested against this exact scenario, rather than asking ExecutionBindingResolver -- the one
        // primitive ExecutionUsageProjectorTests already pins three ways. This is the ledger-level arm
        // that closes that gap: a rebound to a different adapter AND model must show up on the ledger
        // line, not the frozen ExecutionRequestAccepted's original binding.
        var testRoot = Path.Combine(Path.GetTempPath(), $"ledger-build-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("exec-rebound");
            var stepId = new StepId("plan");
            var start = DateTime.UtcNow;

            var entries = new List<LogEntry>
            {
                new LogEntry.FlowLogEntry(new FlowEvent.ExecutionRequestAccepted(
                    AcceptedRequest(executionId, "plan", adapter: "agy", model: "gemini-3-pro"))),
                new LogEntry.FlowLogEntry(new FlowEvent.StepRebound(
                    stepId, executionId, PreviousAdapter: "agy", PreviousModel: "gemini-3-pro",
                    NewAdapter: "claude", NewModel: "sonnet")),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(executionId, Pid: 1), start),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(executionId, 0, CoreExitReason.Natural), start.AddSeconds(1)),
                new LogEntry.FlowLogEntry(new FlowEvent.ExecutionSucceeded(executionId)),
            };

            var built = QuotaLedgerStore.BuildEntries(entries, testRoot);

            var entry = Assert.Single(built);
            Assert.Equal("claude", entry.Adapter);
            Assert.Equal("sonnet", entry.Model);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void BuildEntries_is_absent_for_an_execution_with_no_exit_event()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"ledger-build-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("exec-running");
            var entries = new List<LogEntry>
            {
                new LogEntry.FlowLogEntry(new FlowEvent.ExecutionRequestAccepted(
                    AcceptedRequest(executionId, "plan", adapter: "claude", model: "sonnet"))),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(executionId, Pid: 1), DateTime.UtcNow),
            };

            var built = QuotaLedgerStore.BuildEntries(entries, testRoot);

            Assert.Empty(built);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task ReadDistinctByExecutionAsync_folds_a_pre_existing_duplicate_line_to_the_last_write()
    {
        // AppendAsync's own dedupe (see its doc comment) means a duplicate line can no longer be
        // created through the store's own API -- this test writes the file directly to prove the
        // read-time fold still recovers gracefully from one anyway (a hand-edited file, or a line
        // written before that dedupe existed).
        var path = TempLedgerPath();
        try
        {
            var first = System.Text.Json.JsonSerializer.Serialize(new QuotaLedgerEntry(Execution: "exec-1", TokensIn: 100));
            var second = System.Text.Json.JsonSerializer.Serialize(new QuotaLedgerEntry(Execution: "exec-1", TokensIn: 250));
            await File.WriteAllTextAsync(path, first + "\n" + second + "\n", TestContext.Current.CancellationToken);

            var distinct = await QuotaLedgerStore.ReadDistinctByExecutionAsync(path, TestContext.Current.CancellationToken);

            var entry = Assert.Single(distinct);
            Assert.Equal(250, entry.TokensIn);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task RebuildAsync_preserves_a_ledger_line_whose_room_the_fresh_walk_did_not_reach()
    {
        // #1570 review (advisor pass): a rebuild sourced from the walk alone would delete every entry
        // whose room RoomRetentionSweep already pruned -- precisely the data the ledger exists to hold
        // past retention. This pins that RebuildAsync starts from the ledger's own content.
        var path = TempLedgerPath();
        try
        {
            await QuotaLedgerStore.AppendAsync(
                [new QuotaLedgerEntry(Execution: "gone-1", Room: "C:/rooms/pruned-already", TokensIn: 999)],
                path,
                TestContext.Current.CancellationToken);

            var freshEntries = new List<QuotaLedgerEntry> { new(Execution: "fresh-1", TokensIn: 42) };
            var result = await QuotaLedgerStore.RebuildAsync(freshEntries, path, TestContext.Current.CancellationToken);

            Assert.Equal(1, result.PreviousCount);
            Assert.Equal(2, result.TotalCount);
            Assert.Equal(1, result.RecoveredCount);

            var distinct = await QuotaLedgerStore.ReadDistinctByExecutionAsync(path, TestContext.Current.CancellationToken);
            var byExecution = distinct.ToDictionary(e => e.Execution!, StringComparer.Ordinal);
            Assert.Equal(999, byExecution["gone-1"].TokensIn);
            Assert.Equal(42, byExecution["fresh-1"].TokensIn);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task RebuildAsync_run_twice_against_the_same_fleet_yields_identical_nonzero_totals()
    {
        var path = TempLedgerPath();
        try
        {
            var freshEntries = new List<QuotaLedgerEntry>
            {
                new(Execution: "exec-a", TokensIn: 100),
                new(Execution: "exec-b", TokensIn: 250),
            };

            await QuotaLedgerStore.RebuildAsync(freshEntries, path, TestContext.Current.CancellationToken);
            var firstTotal = (await QuotaLedgerStore.ReadDistinctByExecutionAsync(path, TestContext.Current.CancellationToken))
                .Sum(e => e.TokensIn ?? 0);

            await QuotaLedgerStore.RebuildAsync(freshEntries, path, TestContext.Current.CancellationToken);
            var secondTotal = (await QuotaLedgerStore.ReadDistinctByExecutionAsync(path, TestContext.Current.CancellationToken))
                .Sum(e => e.TokensIn ?? 0);

            Assert.Equal(350, firstTotal);
            Assert.Equal(firstTotal, secondTotal);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task RebuildAsync_a_fresh_entry_overwrites_the_ledgers_existing_entry_for_the_same_execution()
    {
        // RebuildAsync's own doc comment states the merge direction this pins directly, not just
        // inferred from the "identical totals across two runs" test above.
        var path = TempLedgerPath();
        try
        {
            await QuotaLedgerStore.AppendAsync(
                [new QuotaLedgerEntry(Execution: "exec-a", TokensIn: 10)], path, TestContext.Current.CancellationToken);

            var freshEntries = new List<QuotaLedgerEntry> { new(Execution: "exec-a", TokensIn: 999) };
            await QuotaLedgerStore.RebuildAsync(freshEntries, path, TestContext.Current.CancellationToken);

            var distinct = await QuotaLedgerStore.ReadDistinctByExecutionAsync(path, TestContext.Current.CancellationToken);
            var entry = Assert.Single(distinct);
            Assert.Equal(999, entry.TokensIn);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task This_store_is_the_burn_ledgers_own_JSONL_ledger_under_its_own_lock_name()
    {
        // The wrapper smoke test #1884 left behind when the shared arms moved to JsonLinesLedgerTests.
        // Pins the two things the move could silently change: the lock-name prefix this store was
        // constructed with (MutexGuardedFileLock's own remarks state what renaming it costs), and that
        // the dedupe key really is Execution -- exercised end to end rather than read off the selector.
        Assert.Equal("baton-quota-ledger", QuotaLedgerStore.Ledger.LockNamePrefix);

        var path = TempLedgerPath();
        try
        {
            await QuotaLedgerStore.AppendAsync(
                [new QuotaLedgerEntry(Execution: "exec-a", TokensIn: 10)], path, TestContext.Current.CancellationToken);
            // Simulates BuildEntries re-deriving the same, already-settled execution on a second
            // Terminal-reaching command against the same room, alongside a genuinely new one.
            await QuotaLedgerStore.AppendAsync(
                [
                    new QuotaLedgerEntry(Execution: "exec-a", TokensIn: 10),
                    new QuotaLedgerEntry(Execution: "exec-b", TokensIn: 20),
                ],
                path,
                TestContext.Current.CancellationToken);

            var all = await QuotaLedgerStore.ReadAllAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(2, all.Count);
            Assert.Contains(all, e => e.Execution == "exec-a");
            Assert.Contains(all, e => e.Execution == "exec-b");
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }
}
