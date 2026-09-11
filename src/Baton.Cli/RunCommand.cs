using System.Text.Json;
using Baton.Vendors;
using Baton.Artifacts;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Mutation;
using Baton.Projection;
using Baton.Status;
using Baton.Store;
using Baton.Templates;
using Baton.Workspaces;

namespace Baton.Cli;

/// <summary>
/// <c>baton run</c>, the "pump" that is v1's execution driver (M11 Phase 3): the exact
/// project → resolve → dispatch → await loop <c>WorkflowEndToEndTests</c> has exercised since M7,
/// now reached through a real <see cref="IWorkerAdapter"/> and a real host process instead of a
/// test fixture constructing <see cref="WorkerBinding"/>s by hand.
/// </summary>
public static class RunCommand
{
    private const string ArtifactsDirectoryName = ArtifactManager.ArtifactsDirectoryName;

    /// <summary>
    /// Parses the workflow template and worker-binding config (binding from the already-persisted
    /// snapshot instead, when <paramref name="options"/>'s room directory already has one — a
    /// resumed run, not a fresh one), resolves <paramref name="adapters"/> into
    /// <see cref="WorkerBinding"/>s, and runs the single mutation surface to a terminal state.
    /// A resumed run still reads the named template file when one exists, to refuse a directory
    /// bound to different work (#628); it never binds from it.
    /// </summary>
    /// <exception cref="CliArgumentException">
    /// <paramref name="options"/>'s <c>RoomDirectoryPath</c> has no persisted snapshot yet (a fresh
    /// start) and no <c>WorkflowFilePath</c> was given to bind one from.
    /// </exception>
    /// <exception cref="WorkflowDefinitionValidationException">The workflow template is malformed or invalid.</exception>
    /// <exception cref="SnapshotLoadException">The room directory's persisted snapshot is malformed.</exception>
    /// <exception cref="WorkerBindingConfigException">The worker-binding config is malformed.</exception>
    /// <exception cref="UnknownWorkerAdapterException">
    /// The worker-binding config names an adapter not present in <paramref name="adapters"/>.
    /// </exception>
    /// <exception cref="ResumedTemplateMismatchException">
    /// <paramref name="options"/>'s room directory is already bound to a snapshot, and the workflow
    /// file named is a different template (#628).
    /// </exception>
    /// <exception cref="Baton.Concurrency.WorkflowLockedException">
    /// Another Flow instance already holds this room directory's lock. The pump's own guard
    /// (<see cref="MutationInterface.StartWorkflowAsync"/>) stays deliberately fail-fast: losing it
    /// means a second pump owns this room, and waiting for it is exactly the wrong behaviour.
    /// <para>
    /// #1650 F3, stated because the guard's fail-fast property no longer describes the <em>command</em>:
    /// two bounded waits now precede it on this same invocation — <see cref="WorktreeWorkspaces.Provision"/>
    /// and the <c>FlowEventLogWriter</c> open — so a second <c>baton run</c> against a live pump can
    /// spend up to two <see cref="Baton.Concurrency.RoutineHoldBudget"/> intervals before this guard
    /// ever gets its turn, and normally refuses with the type below rather than this one. Fail-fast
    /// here is now a property of the last step, not of the run.
    /// </para>
    /// </exception>
    /// <exception cref="Baton.Store.FlowJournalHeldException">
    /// #816's journal-held refusal. What a second <c>baton run</c> gets against a live pump, in place
    /// of the lock refusal above (#1650 F3): the writer scoped below is opened before
    /// <see cref="MutationInterface.StartWorkflowAsync"/>'s guard is ever reached, so it is contended
    /// first. <see cref="DecideCommand"/> holds the reasoning.
    /// </exception>
    /// <exception cref="Baton.Status.StaleSentinelDeletionException">
    /// The room carries a stale <c>terminal.json</c> from a prior attempt that could not be deleted, so
    /// this call refuses rather than pumping behind a false "already done" signal (#1608 re-review).
    /// </exception>
    /// <param name="inFlightExecutions">
    /// M15 Phase 4's (issue #140) caller-retained delivery point — forwarded to
    /// <see cref="MutationInterface.StartWorkflowAsync"/>. A caller that retains one can signal a
    /// targeted Cancel to a specific in-flight execution this same call dispatched, without a second
    /// mutation-surface call racing the same guard (originally <c>Baton.RoomSession</c>'s
    /// <c>RoomClient</c>, itself since deleted, #1420). <c>null</c> (every CLI caller today) no longer
    /// means "unreachable mid-pump" as of #1495: this method retains its own instance either way and
    /// runs <see cref="CancelRequestPoller"/> against <paramref name="options"/>'s <c>RoomDirectoryPath</c>
    /// for this call's whole duration — the out-of-band channel <c>baton cancel</c> falls through to
    /// once it finds this room's <c>flow.lock</c> already held.
    /// </param>
    /// <param name="onWorkerStdoutLine">
    /// M24 Phase 1's live in-turn streaming — forwarded verbatim to <see cref="WorkerBindingResolver.Resolve"/>.
    /// Null for <c>baton run</c> by default unless <c>--echo-worker</c> is set (#882). <c>Baton.Daemon</c>'s
    /// in-process session-turn path supplies one only for <c>StreamJson</c> turns (see
    /// <c>Program.ExecuteSessionTurnAsync</c>) — the invariant that keeps the flag from ever
    /// double-echoing there is narrower: no daemon/UI path constructs <see cref="RunOptions"/> with
    /// <c>EchoWorker</c> set, and an explicit callback always wins over the flag below.
    /// </param>
    public static Task<CommandResult> ExecuteAsync(
        RunOptions options,
        IReadOnlyDictionary<string, IWorkerAdapter> adapters,
        InFlightExecutionRegistry? inFlightExecutions = null,
        CancellationToken cancellationToken = default,
        Action<string, string>? onWorkerStdoutLine = null)
        => ExecuteAsync(options, adapters, inFlightExecutions, cancellationToken, onWorkerStdoutLine, testOnlyAfterProvisionBeforeStaleSweepAsync: null);

    /// <param name="testOnlyAfterProvisionBeforeStaleSweepAsync">
    /// #1649: test-only seam, always <c>null</c> in production. Runs after
    /// <see cref="WorktreeWorkspaces.Provision"/> and strictly before
    /// <see cref="CancelRequestFile.DeleteStalePendingRequestAsync"/> — the exact window a concurrent
    /// <c>baton cancel</c> can land a live <c>cancel.request</c> write in — so a test can deterministically
    /// write into that window instead of racing real process timing to hit it.
    /// </param>
    internal static async Task<CommandResult> ExecuteAsync(
        RunOptions options,
        IReadOnlyDictionary<string, IWorkerAdapter> adapters,
        InFlightExecutionRegistry? inFlightExecutions,
        CancellationToken cancellationToken,
        Action<string, string>? onWorkerStdoutLine,
        Func<Task>? testOnlyAfterProvisionBeforeStaleSweepAsync)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(adapters);

        var bindingConfig = await WorkerBindingConfigParser.LoadFromFileAsync(options.BindingsFilePath, cancellationToken)
            .ConfigureAwait(false);

        // This must precede every room-side effect. Run accepts hand-authored bindings.json directly,
        // so DispatchCommand's earlier admission check is not sufficient; refusing after registration
        // or snapshot persistence leaves a non-runnable room that looks like a real dispatch.
        WorkerBindingResolver.RefuseConductorOnlyWorkerModels(bindingConfig);

        // #1649: captured before WorktreeWorkspaces.Provision below runs (and therefore before the
        // sweep further down) — CancelRequestFile.DeleteStalePendingRequestAsync uses this to tell a
        // request written no earlier than THIS invocation started (a concurrent writer racing that
        // window) apart from one left behind by a prior, crashed pump.
        var invocationStartUtc = DateTimeOffset.UtcNow;

        Directory.CreateDirectory(options.RoomDirectoryPath);
        await RegisterRoomAsync(options, cancellationToken).ConfigureAwait(false);

        var snapshotPath = Path.Combine(options.RoomDirectoryPath, BatonPaths.SnapshotFileName);
        var logPath = Path.Combine(options.RoomDirectoryPath, BatonPaths.FlowLogFileName);
        var artifactsRootPath = Path.Combine(options.RoomDirectoryPath, ArtifactsDirectoryName);

        var resumedFromSnapshot = File.Exists(snapshotPath);
        WorkflowDefinitionSnapshot snapshot;
        if (resumedFromSnapshot)
        {
            snapshot = await SnapshotBinder.LoadFromFileAsync(snapshotPath, cancellationToken).ConfigureAwait(false);
            await RefuseIfTheNamedTemplateIsNotTheBoundOneAsync(options, snapshot, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            snapshot = await BindAndPersistAsync(RequireWorkflowFilePath(options), snapshotPath, cancellationToken).ConfigureAwait(false);
        }

        RefuseIfRoomDirectoryIsSensitive(bindingConfig, adapters, options.RoomDirectoryPath);

        // #669: a binding declaring a worktree workspace is provisioned here, before resolution, and its
        // WorkingDirectory rewritten to the provisioned tree — so everything below (and the worker) sees
        // an ordinary directory. Idempotent across resume; torn down after the pump reaches Terminal.
        var (provisionedConfig, provisionedWorktrees) =
            WorktreeWorkspaces.Provision(bindingConfig, options.RoomDirectoryPath);

        // #882, #1540: CoreDispatcher only dispatches BatonTaskEventKind.StdoutChunk to OnStdoutLine.
        // Stderr chunks write to artifacts/stderrTail but are NOT passed to this callback.
        // When --echo-worker is set, stream-json lines are parsed so only human-relevant content is echoed.
        Action<string, string>? effectiveOnWorkerStdoutLine = onWorkerStdoutLine ?? (options.EchoWorker ? CreateEchoWorkerCallback(provisionedConfig, adapters, Console.Out) : null);

        var profiles = await BatonProfileStore.LoadAsync(BatonProfileStore.DefaultPath, cancellationToken).ConfigureAwait(false);
        var workerBindings = WorkerBindingResolver.Resolve(
            provisionedConfig, adapters, profiles, Path.GetDirectoryName(options.BindingsFilePath), effectiveOnWorkerStdoutLine);
        // #802: resolved eagerly alongside workerBindings, same refusal semantics — a role's
        // FallbackOnExhaustion naming an unregistered adapter (or an otherwise-refused entry) fails
        // this dispatch before it starts rather than mid-park.
        var fallbackWorkerBindings = WorkerBindingResolver.ResolveFallbacks(
            provisionedConfig, adapters, profiles, Path.GetDirectoryName(options.BindingsFilePath), effectiveOnWorkerStdoutLine);

        var workflowId = new WorkflowId(options.WorkflowId ?? snapshot.WorkflowTemplateId.Value);

        // #1356: invalidate a stale sentinel from a PRIOR terminal attempt against this same room
        // (a pre-ledger failure now being retried with corrected bindings) before this attempt's
        // own pump can run for any length of time. Left in place, it would read as "already done"
        // to a file-watcher for the whole duration of a genuinely fresh run.
        //
        // #1374 F1: skipped when the room's OWN ledger already says Terminal. Deleting a still-valid
        // terminal record before knowing this attempt will produce a new one means a Ctrl-C (or any
        // interruption) landing right after the delete leaves a genuinely-Terminal room with no
        // sentinel at all -- "absence means not terminal yet" (spec/baton-room-spec-v1.0.md) would then
        // be false. A room that is not yet Terminal never has a sentinel to lose, so the probe costs
        // nothing there and the delete still runs.
        //
        // #1608 re-review finding 2: fail-closed (DeleteStaleSentinel's default), unlike the
        // post-`resolve` call site — a sentinel this call cannot remove is exactly the false
        // "already done" signal above, so refusing before the pump starts is the whole point.
        var priorProbe = await WorkflowTerminalProbe.ProbeAsync(options.RoomDirectoryPath, cancellationToken).ConfigureAwait(false);
        if (!priorProbe.IsTerminal)
        {
            TerminalSentinelWriter.DeleteStaleSentinel(options.RoomDirectoryPath);
        }

        // #1649: test-only hook firing exactly here, before the sweep below.
        if (testOnlyAfterProvisionBeforeStaleSweepAsync is not null)
        {
            await testOnlyAfterProvisionBeforeStaleSweepAsync().ConfigureAwait(false);
        }

        // #1495 review finding 5: clear any unconsumed pending cancel.request from a crashed prior
        // pump before this attempt's poller starts — see CancelRequestFile.DeleteStalePendingRequestAsync,
        // which (#1649) discriminates that from a request a concurrent baton cancel wrote into the
        // window between WorktreeWorkspaces.Provision above and this call.
        var roomLogPath = Path.Combine(options.RoomDirectoryPath, BatonPaths.RoomLogFileName);
        await CancelRequestFile.DeleteStalePendingRequestAsync(options.RoomDirectoryPath, invocationStartUtc, cancellationToken, roomLogPath)
            .ConfigureAwait(false);

        // #1495: retained regardless of whether the caller supplied one, so THIS call can poll
        // cancel.request against it below — a caller-supplied instance is still honoured (forwarded
        // to MutationInterface unchanged), but a null one no longer means "unreachable mid-pump": every
        // baton run/dispatch/redispatch invocation (they all funnel through this method) is now a live
        // arrest target via the file channel, not just whichever caller happens to retain the registry.
        var liveInFlightExecutions = inFlightExecutions ?? new InFlightExecutionRegistry();

        FlowState state;
        {
            // Scoped, not the method-wide `await using` this used to be: a Paused return must
            // release the journal handle before the --wait poll loop below can start, or it holds
            // the exact lock a separate `baton decide` process needs to record the very decision
            // being waited on (FlowJournalHeldException — measured by this PR's own --wait test).
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);

            // #1495: the out-of-band arrest channel's pump-side reader — polls cancel.request at a
            // modest cadence (never flow.lock) for this call's own duration, routing a found request to
            // liveInFlightExecutions. Cancelled the instant the pump call below returns (success,
            // exception, or host stop alike), never left running past this method's own lifetime.
            using var pollCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var pollTask = CancelRequestPoller.RunAsync(
                options.RoomDirectoryPath, logPath, snapshot, liveInFlightExecutions,
                CancelRequestPoller.DefaultPollInterval, pollCancellation.Token, roomLogPath);

            // #1549: the content-free progress heartbeat — a sibling fire-and-forget poller sharing
            // this call's own writer and cancellation lifetime, never the pollCancellation token above
            // stopped and re-created; both stop together when the pump call below returns. Unlike
            // CancelRequestPoller there is no correctness-bearing "final tick" to run after this task
            // is awaited — a heartbeat missed in the last GetInterval() window before exit is exactly
            // as harmless as one missed mid-run (advisory/observability only, no state consequence).
            var heartbeatTask = ExecutionProgressHeartbeat.RunAsync(
                options.RoomDirectoryPath, logPath, artifactsRootPath, snapshot, writer,
                ExecutionProgressHeartbeat.GetInterval(), pollCancellation.Token);

            try
            {
                state = await MutationInterface.StartWorkflowAsync(
                        workflowId,
                        options.RoomDirectoryPath,
                        snapshot,
                        workerBindings,
                        artifactsRootPath,
                        reader,
                        writer,
                        dispatcher,
                        liveInFlightExecutions,
                        cancellationToken,
                        holderDescription: $"baton run pump (pid {Environment.ProcessId})",
                        // #1094: a foreground run that quota-parks would otherwise sit silently until the
                        // reset (~a day out); surface it so the paced wait is legible. To stderr — it is a
                        // status notice, not run output.
                        onVendorQuotaPark: resumesAt => Console.Error.WriteLine(FormatVendorQuotaParkNotice(resumesAt)),
                        settleOnVendorExhaustion: options.SettleOnVendorExhaustion,
                        fallbackWorkerBindings: fallbackWorkerBindings)
                    .ConfigureAwait(false);
            }
            finally
            {
                pollCancellation.Cancel();
                try
                {
                    // Await the poller task before pollCancellation disposes so unobserved faults are drained cleanly while the CTS is still valid.
                    await pollTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected: pollCancellation firing mid-tick (e.g. mid ReadAllAsync) surfaces here,
                    // not as a fault the run's own result should carry.
                }
                catch (Exception ex)
                {
                    try
                    {
                        Console.Error.WriteLine($"cancel.request poller faulted during shutdown: {ex.Message}");
                    }
                    catch
                    {
                    }
                }

                // #1605 review F1: the poller only ticks on its own DefaultPollInterval cadence (2s),
                // but StartWorkflowAsync can settle a parked cancel and return Terminal well inside
                // that window — in the single-parked-lane shape, nothing else keeps the pump alive to
                // reach the tick that would consume the request file, so pollTask above exits with the
                // file still pending in a room whose cancel actually SUCCEEDED, and "arrested by this
                // request" never prints. Run one last tick against the now-final state to close that
                // gap — strictly AFTER pollTask has been awaited above (never concurrently with it: two
                // ticks racing the same request file would double-append CancellationRequested for a
                // still-live target, or race each other's Consume/Reject rename).
                try
                {
                    await CancelRequestPoller.TickAsync(
                            options.RoomDirectoryPath, logPath, snapshot, liveInFlightExecutions, CancellationToken.None, roomLogPath)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    try
                    {
                        Console.Error.WriteLine($"cancel.request final tick failed: {ex.Message}");
                    }
                    catch
                    {
                    }
                }

                try
                {
                    await heartbeatTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected: pollCancellation firing mid-tick surfaces here, same as pollTask above.
                }
                catch (Exception ex)
                {
                    try
                    {
                        Console.Error.WriteLine($"execution progress heartbeat faulted during shutdown: {ex.Message}");
                    }
                    catch
                    {
                    }
                }
            }
        }

        // See RunOptions.Wait's own doc for the full contract; this just implements it.
        var waitTimedOut = false;
        if (options.Wait && state.Status != WorkflowStatus.Terminal && !cancellationToken.IsCancellationRequested)
        {
            (state, waitTimedOut) = await WaitForTerminalAsync(
                    options.RoomDirectoryPath, snapshot, logPath, options.WaitTimeout, cancellationToken)
                .ConfigureAwait(false);
        }

        var worktreeTeardowns = WorktreeProvisioner.TeardownIfTerminal(state.Status, provisionedWorktrees);

        return new CommandResult(
            state, snapshot, resumedFromSnapshot, options.RoomDirectoryPath, worktreeTeardowns, WaitTimedOut: waitTimedOut);
    }

    /// <summary>
    /// #1356's <c>--wait</c> poll loop: re-projects <paramref name="logPath"/> at a fixed interval
    /// (mirroring <c>StatusCommand.FollowAsync</c>'s own poll-on-length-change technique, without the
    /// per-event printing) until <see cref="WorkflowStatus.Terminal"/> or cancellation. Reads only —
    /// this process already returned its own <see cref="FlowEventLogWriter"/>'s lock by the time this
    /// runs, and the state change being waited on is necessarily written by a different process.
    /// <para>
    /// #1378: <paramref name="waitTimeout"/>, when given, bounds the loop with its own linked
    /// cancellation source rather than the caller's <paramref name="cancellationToken"/> — so the
    /// returned <c>WaitTimedOut</c> can tell the two exits apart. It is only ever true when the
    /// timeout itself elapsed; a plain Ctrl-C (the ambient token firing first, including the race
    /// where both fire around the same instant) is reported as a normal cancelled exit, same as
    /// before #1378.
    /// </para>
    /// </summary>
    private static async Task<(FlowState State, bool WaitTimedOut)> WaitForTerminalAsync(
        string roomDirectoryPath, WorkflowDefinitionSnapshot snapshot, string logPath, TimeSpan? waitTimeout,
        CancellationToken cancellationToken)
    {
        var reader = new FlowEventLogReader(logPath);
        var lastObservedLength = -1L;

        using var timeoutCts = waitTimeout is { } timeout ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken) : null;
        timeoutCts?.CancelAfter(waitTimeout!.Value);
        var loopToken = timeoutCts?.Token ?? cancellationToken;

        while (true)
        {
            // One catch covers BOTH cancelable awaits in the iteration: the reader below takes the
            // same loopToken, and an expiry landing mid-read must break to the final-read path like
            // an expiry during the delay does -- not escape as an unhandled crash (#1478 review, F2).
            try
            {
                await Task.Delay(StatusPollIntervalMs, loopToken).ConfigureAwait(false);

                var logFile = new FileInfo(logPath);
                var currentLength = logFile.Exists ? logFile.Length : 0;
                if (currentLength == lastObservedLength)
                {
                    continue;
                }

                lastObservedLength = currentLength;

                var events = await reader.ReadAllAsync(loopToken).ConfigureAwait(false);
                var checkpoint = ProjectionCheckpointStore.Load(roomDirectoryPath);
                var state = StateProjector.Project(events, snapshot, checkpoint);
                if (state.Status == WorkflowStatus.Terminal)
                {
                    return (state, false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // Cancelled or timed out before reaching Terminal: report the latest state we actually
        // observed rather than a synthetic one, same as the pump itself does on a host stop.
        var finalEvents = await reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false);
        var finalState = StateProjector.Project(finalEvents, snapshot, ProjectionCheckpointStore.Load(roomDirectoryPath));

        // Timed out only when OUR OWN timeout source fired first (an ambient Ctrl-C, or the ambient
        // token racing the timeout, reports as a plain cancelled exit) AND the room truly fell short
        // of Terminal. The second clause is load-bearing (#1478 review, F1): a decision landing in
        // the last poll window before the deadline reaches Terminal without the loop ever observing
        // it, and reporting THAT as a timeout would exit 3 while a terminal sentinel gets written --
        // the exact contradiction of the documented "room untouched, still Paused" contract.
        var timedOut = finalState.Status != WorkflowStatus.Terminal
            && timeoutCts is not null && !cancellationToken.IsCancellationRequested;
        return (finalState, timedOut);
    }

    /// <summary>Matches <c>StatusCommand</c>'s own follow-poll cadence (#1356) — see that constant's doc for why a fixed poll rather than a <see cref="FileSystemWatcher"/>.</summary>
    private const int StatusPollIntervalMs = 500;

    /// <summary>
    /// #628: resuming the bound snapshot instead of the named file is intended (M15 Phase 1, #137),
    /// but doing it when the two name different templates ran another room's workflow and reported
    /// its result — the measured case replayed a prior terminal run's declared outputs, timeout and
    /// failure reason, wrote no events, and exited non-zero, which is indistinguishable from a
    /// genuine fresh failure.
    ///
    /// <para>
    /// The named file is parsed in full rather than scraped for its id, so there is one parser for
    /// workflow files and no second reading of the same format to drift. A resume naming a file that
    /// exists but is malformed therefore now fails on it, with the same typed
    /// <see cref="WorkflowDefinitionValidationException"/> a fresh bind would raise; a resume naming
    /// something that is not a readable file is left alone entirely, for the reason below.
    /// </para>
    /// </summary>
    /// <summary>
    /// #1094: the foreground quota-park notice. Local time (the operator's own clock, matching
    /// <c>baton status</c>'s <c>FormatParkedStatus</c>), and it names both what happens next (auto-resume
    /// at the 0026-paced instant) and the escape hatch (Ctrl-C, which records a resumable stop) so a
    /// day-long wait is a legible state rather than a hang.
    /// </summary>
    public static string FormatVendorQuotaParkNotice(DateTimeOffset resumesAt)
    {
        var local = resumesAt.ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        return $"Parked on vendor quota — the run resumes automatically at {local} (local). "
            + "Progress is saved; press Ctrl-C to stop now and resume later by re-running.";
    }

    private static async Task RefuseIfTheNamedTemplateIsNotTheBoundOneAsync(
        RunOptions options, WorkflowDefinitionSnapshot snapshot, CancellationToken cancellationToken)
    {
        // WorkflowFilePath is nullable so an in-process caller resuming a known room directory need
        // not produce one. If a path IS supplied, we check it against the bound snapshot (#653).
        // WorkflowDefinitionParser.LoadFromFileAsync translates missing files into a typed
        // WorkflowDefinitionValidationException (an BatonFlowException) — but an EMPTY path would
        // throw an untyped ArgumentException from the BCL, so whitespace means "not supplied" here
        // rather than trusting every caller to normalize before RunOptions is built.
        if (options.WorkflowFilePath is not { } workflowFilePath || string.IsNullOrWhiteSpace(workflowFilePath))
        {
            return;
        }

        var named = await WorkflowDefinitionParser.LoadFromFileAsync(workflowFilePath, cancellationToken)
            .ConfigureAwait(false);
        if (named.WorkflowTemplateId == snapshot.WorkflowTemplateId)
        {
            return;
        }

        throw new ResumedTemplateMismatchException(
            snapshot.WorkflowTemplateId.Value, named.WorkflowTemplateId.Value, options.RoomDirectoryPath);
    }

    /// <summary>
    /// #599, corrected to a component match by #1834: refuse before
    /// <see cref="WorktreeWorkspaces.Provision"/> or any dispatch when a binding's resolved adapter's
    /// <see cref="IWorkerAdapter.HasSensitiveOutputPathComponent"/> matches the room directory — see
    /// <see cref="SensitiveOutputRootException"/>'s own remarks for what that means and why it is
    /// refused this early. An entry naming an unknown adapter is left for
    /// <see cref="WorkerBindingResolver.Resolve"/>'s own <see cref="UnknownWorkerAdapterException"/>
    /// below rather than duplicated here.
    /// </summary>
    private static void RefuseIfRoomDirectoryIsSensitive(
        IReadOnlyDictionary<string, WorkerBindingConfigEntry> bindingConfig,
        IReadOnlyDictionary<string, IWorkerAdapter> adapters,
        string roomDirectoryPath)
    {
        foreach (var (workerName, entry) in bindingConfig)
        {
            if (!adapters.TryGetValue(entry.Adapter, out var adapter))
            {
                continue;
            }

            if (adapter.HasSensitiveOutputPathComponent(roomDirectoryPath, out var offendingComponent))
            {
                throw new SensitiveOutputRootException(workerName, roomDirectoryPath, offendingComponent!);
            }
        }
    }

    /// <summary>
    /// A fresh start with no <see cref="RunOptions.WorkflowFilePath"/> is a caller error, not a
    /// silent no-op — there is nothing to bind a snapshot from (M15 Phase 1, issue #137).
    /// </summary>
    private static string RequireWorkflowFilePath(RunOptions options) => options.WorkflowFilePath
        ?? throw new CliArgumentException(
            $"Room directory '{options.RoomDirectoryPath}' has no bound snapshot yet, and no workflow " +
            "template file was given to start one fresh.");

    private static async Task<WorkflowDefinitionSnapshot> BindAndPersistAsync(
        string workflowFilePath, string snapshotPath, CancellationToken cancellationToken)
    {
        var definition = await WorkflowDefinitionParser.LoadFromFileAsync(workflowFilePath, cancellationToken).ConfigureAwait(false);
        var snapshot = SnapshotBinder.Bind(definition);
        await SnapshotBinder.PersistAsync(snapshot, snapshotPath, cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    /// <summary>
    /// spec/baton.md §8: records this room into the machine-local multi-project registry so
    /// <c>fleet_status</c> can find it even outside any root a caller happens to scan. Runs on every
    /// call to <see cref="ExecuteAsync"/> (first start and re-entry through this pump alike;
    /// spec/baton.md §8 names which verbs those are) — rather than only when <see cref="RunOptions.RoomDirectoryPath"/>
    /// has no snapshot yet, so a registration lost to an earlier crash (the process died between
    /// <see cref="Directory.CreateDirectory(string)"/> above and this write) is repaired by the next
    /// call through this pump rather than staying permanently unregistered. Re-registering an
    /// already-registered room is harmless: <see cref="RoomRegistryStore.ReadDistinctByRoomAsync"/>
    /// folds repeats down to the last write per room path.
    /// <para>
    /// The mutation verbs (<see cref="ResumeCommand"/> and friends) bypass this pump and therefore
    /// never re-register — spec/baton.md §8 spells out which verbs register and the accepted gap
    /// that leaves (an initially-failed registration driven only by mutation verbs stays
    /// unregistered until the pump next runs against that room).
    /// </para>
    /// <para>
    /// Never gates the run: the registry only adds <c>fleet_status</c> coverage, so a write failure
    /// (an unwritable or momentarily locked registry file, or a lock-name collision) is reported on
    /// stderr and swallowed rather than surfaced as a run failure.
    /// </para>
    /// </summary>
    private static async Task RegisterRoomAsync(RunOptions options, CancellationToken cancellationToken)
    {
        var projectRoot = options.ProjectRootDirectory ?? Directory.GetCurrentDirectory();

        try
        {
            await RoomRegistryStore.AppendAsync(
                options.RoomDirectoryPath, projectRoot, BatonPaths.RoomRegistryFile,
                explicitRegister: options.Register, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            Console.Error.WriteLine(
                $"Could not update the room registry at '{BatonPaths.RoomRegistryFile}': {ex.Message}. "
                + "fleet_status will still find this room via its normal directory scan.");
        }
    }

    /// <summary>
    /// Creates the worker stdout echo callback for <c>--echo-worker</c> (#882, #1540, #1561).
    /// For bindings with <c>StreamJson: true</c>, parses stream-json lines via <see cref="EchoStreamJsonLine"/>
    /// (never-swallow: see that method's own doc comment for what renders specially versus echoes
    /// verbatim, including when <paramref name="adapters"/> has no entry for the binding's adapter).
    /// Non-streaming bindings echo every stdout line verbatim.
    /// </summary>
    internal static Action<string, string> CreateEchoWorkerCallback(
        IReadOnlyDictionary<string, WorkerBindingConfigEntry> bindings,
        IReadOnlyDictionary<string, IWorkerAdapter> adapters,
        TextWriter writer)
    {
        return (workerName, line) =>
        {
            if (bindings.TryGetValue(workerName, out var entry) && entry.StreamJson)
            {
                adapters.TryGetValue(entry.Adapter, out var adapter);
                EchoStreamJsonLine(line, adapter, writer);
            }
            else
            {
                writer.WriteLine(line);
            }
        };
    }

    /// <summary>
    /// Renders one stdout line from a <c>StreamJson</c> worker to <paramref name="writer"/> (#1540, #1561).
    /// Human-relevant text (assistant messages/deltas, tool-use markers, in-turn status heartbeats, a
    /// completed turn's status/error summary) is extracted and printed; malformed/non-JSON lines echo
    /// verbatim. A recognized envelope the adapter deliberately filters (<see cref="WorkerProgressEvent"/>
    /// Kind <c>"ignore"</c> — a claude `thinking`-only block, an agy step_update ACTIVE edge) stays
    /// quiet, same as before #1561. Everything else — a valid-JSON <c>type</c>/<c>event</c> no adapter
    /// recognises at all (a vendor's <c>user</c> tool-result echo today, anything the vendor adds
    /// tomorrow), or a <see cref="WorkerProgressEvent.Kind"/> this switch has no arm for — echoes
    /// verbatim rather than vanishing: nothing valid-JSON this method sees is silently dropped without
    /// the adapter having explicitly decided to drop it. The <c>default</c> arm below is defensive
    /// (today's Kind vocabulary — text/tool/status/result/ignore — is fully covered above); it is what
    /// keeps a future Kind added without a matching case here failing safe instead of vanishing again.
    /// </summary>
    internal static void EchoStreamJsonLine(string line, IWorkerAdapter? adapter, TextWriter writer)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            // Malformed JSON / raw CLI output (e.g. startup warning, crash backtrace) — echo verbatim (never swallow).
            writer.WriteLine(line);
            return;
        }

        if (adapter is not null && adapter.TryParseProgressEvent(line, out var progressEvent) && progressEvent is not null)
        {
            switch (progressEvent.Kind)
            {
                case "text":
                    if (progressEvent.IsPartial)
                    {
                        writer.Write(progressEvent.Text);
                    }
                    else
                    {
                        writer.WriteLine(progressEvent.Text);
                    }
                    break;
                case "tool":
                    writer.WriteLine($"[tool: {progressEvent.Text}]");
                    break;
                case "status":
                    writer.WriteLine($"[status: {progressEvent.Text}]");
                    break;
                case "result":
                    writer.WriteLine($"[result: {progressEvent.Text}]");
                    break;
                case "ignore":
                    // See WorkerProgressEvent's Kind doc comment for what "ignore" means and how it
                    // differs from the unrecognized-envelope fallback below.
                    break;
                default:
                    // A Kind this switch doesn't render yet — never swallow it silently.
                    writer.WriteLine(line);
                    break;
            }
        }
        else
        {
            // Valid JSON, but no adapter or no adapter that recognises this envelope — never swallow it.
            writer.WriteLine(line);
        }
    }
}
