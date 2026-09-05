using System.Reflection;
using Baton.Vendors;
using Baton.Cli;
using Baton;
using Baton.Accounting;
using Baton.Domain;
using Baton.Status;
using Baton.Store;

if (args.Length == 1 && args[0] == "--version")
{
    Console.WriteLine(VersionInfo.GetVersion(Assembly.GetExecutingAssembly()));
    return 0;
}

// #543: the PreToolUse hook target ClaudeWorkerAdapter writes into claude-settings.json, spawned
// directly by Claude Code (exec form -- no shell) on every tool call. Deliberately bypasses the
// workflow-execution pipeline below (WorkerAdapterRegistry, FlowStateReporter, the BatonFlowException
// boundary): none of that applies, and this needs to stay a fast, dependency-free stdin round trip
// since PreToolUse blocks the model's turn until it returns. Not listed in the usage banner below --
// an operator never types this, Claude Code does.
if (args.Length >= 1 && args[0] == "hook-check")
{
    var deniedTools = Environment.GetEnvironmentVariable(HookCheckCommand.DeniedToolsEnvironmentVariable);
    // #649: the hook needs to know where this execution's outbox is to allow a withheld write into
    // it. BATON_OUTPUT_DIR reaches this process the same way the denied list does -- a hook subprocess
    // inherits the worker's environment.
    var outputDir = Environment.GetEnvironmentVariable("BATON_OUTPUT_DIR");
    // #679: where a granted write may land -- see HookCheckCommand.Execute's own parameter docs for
    // what its absence means.
    var workspaceDir = Environment.GetEnvironmentVariable(HookCheckCommand.WorkspaceEnvironmentVariable);
    // #1459: the scoped-shell second layer -- allowed patterns and the standing-deny list, read the
    // same way the denied-tool list above is.
    var shellPatterns = Environment.GetEnvironmentVariable(HookCheckCommand.ShellPatternsEnvironmentVariable);
    var deniedShellPatterns =
        Environment.GetEnvironmentVariable(HookCheckCommand.DeniedShellPatternsEnvironmentVariable);
    // #1683 F2: the option-token deny rung, read the same way. Hook-only on this vendor -- no
    // --disallowedTools entry expresses it (ShellCommandPatternMatcher.IsDeniedByOptionToken).
    var deniedShellOptionTokens = Environment.GetEnvironmentVariable(
        HookCheckCommand.DeniedShellOptionTokensEnvironmentVariable);
    return HookCheckCommand.Execute(
        Console.In, Console.Error, deniedTools, outputDir, workspaceDir, shellPatterns, deniedShellPatterns,
        deniedShellOptionTokens);
}

// #554: the same idea for agy, and a separate command because the two vendors share none of the
// mechanics -- agy nests the tool name at `toolCall.name` and reads its verdict from a `decision`
// field on STDOUT, where claude uses a root-level `tool_name` and signals denial by exiting 2.
// Note `Console.Out`, not `Console.Error`: on this vendor stdout carries the verdict, and anything
// else written there would be unparseable output that agy reads as an allow.
if (args.Length >= 1 && args[0] == "agy-hook-check")
{
    var deniedTools = Environment.GetEnvironmentVariable(AgyHookCheckCommand.DeniedToolsEnvironmentVariable);
    var shellPatterns = Environment.GetEnvironmentVariable(AgyHookCheckCommand.ShellPatternsEnvironmentVariable);
    // #390: the DenyAlways channel — agy's sole enforcement for a standing "never" (no vendor flag can
    // express a command family here), so it is read and passed like the allow channel.
    var deniedShellPatterns = Environment.GetEnvironmentVariable(
        AgyHookCheckCommand.DeniedShellPatternsEnvironmentVariable);
    // #679: the outbox reaches this gate for the GRANTED-write bound only. #649's withheld-write
    // exemption remains claude-only and is not extended here.
    var agyOutputDir = Environment.GetEnvironmentVariable("BATON_OUTPUT_DIR");
    var agyWorkspaceDir = Environment.GetEnvironmentVariable(HookCheckCommand.WorkspaceEnvironmentVariable);
    // #1683 F2: the option-token deny rung, read like the two channels above.
    var agyDeniedShellOptionTokens = Environment.GetEnvironmentVariable(
        AgyHookCheckCommand.DeniedShellOptionTokensEnvironmentVariable);
    // #1680: the first-verdict canary's write side -- naming the file this invocation appends one
    // line to, so AgyWorkerAdapter's caller can later confirm the hook fired at all.
    var agyVerdictLedgerPath = Environment.GetEnvironmentVariable(
        AgyHookCheckCommand.VerdictLedgerEnvironmentVariable);
    return AgyHookCheckCommand.Execute(
        Console.In, Console.Out, deniedTools, shellPatterns, agyOutputDir, agyWorkspaceDir, deniedShellPatterns,
        agyDeniedShellOptionTokens, agyVerdictLedgerPath);
}

// #1853: hidden bidirectional app-server broker. Like hook-check above, this is a vendor subprocess
// endpoint, not an operator workflow verb; CodexWorkerAdapter invokes it with a seeded, non-secret
// launch config after CoreDispatcher has resolved the execution's environment placeholders.
if (args.Length >= 1 && args[0] == "codex-broker")
{
    return await CodexBrokerCommand.RunAsync(args[1..]).ConfigureAwait(false);
}

// #1458: folded from the standalone Baton.Mcp.Host executable -- a stdio MCP server (vendor CLIs
// spawn it per turn via --mcp-config) and a client-facing verb alike, so it is intercepted here
// rather than joining the CommandResult/FlowStateReporter shape every mutating command below shares.
if (args.Length >= 1 && args[0] == "mcp")
{
    return await Baton.Cli.Mcp.McpCommand.RunAsync(args[1..]).ConfigureAwait(false);
}

// #1458: folded from the standalone Baton.Daemon executable -- a long-running background host, not
// a one-shot command, so it never reaches the CommandResult/FlowStateReporter shape below either.
if (args.Length >= 1 && args[0] == "daemon")
{
    await Baton.Cli.Daemon.DaemonHost.RunDaemonAsync(args[1..]).ConfigureAwait(false);
    return 0;
}

var knownSubcommands = new[] { "run", "dispatch", "redispatch", "cancel", "decide", "resolve", "supply", "resume", "status", "watch", "deliver", "templates", "keep", "unkeep", "trust", "room", "rooms", "ledger", "memory", "mcp", "daemon" };
if (args.Length == 0 || !knownSubcommands.Contains(args[0]))
{
    Console.Error.WriteLine(RunOptionsParser.Usage);
    Console.Error.WriteLine($"       {DispatchOptionsParser.Usage[7..]}");
    Console.Error.WriteLine($"       {RedispatchOptionsParser.Usage[7..]}");
    Console.Error.WriteLine(
        "       baton cancel <room-dir> [--execution <execution-id>] [--bindings <bindings-file>] [--workflow-id <id>]");
    Console.Error.WriteLine(
        "       baton decide <room-dir> --execution <execution-id> --type resume|reject|retry-with-revision|supersede " +
        "[--target-step <step-id>] [--supplementary <execution-id>] --bindings <bindings-file> [--workflow-id <id>]");
    Console.Error.WriteLine(
        "       baton resolve <room-dir> [--execution <execution-id>] --accept-capture | --reject --reason <text> " +
        "| --close --reason <text>");
    // #1877: the one thing an operator cannot infer from the grammar — what a rejection LEAVES BEHIND.
    Console.Error.WriteLine(
        "              (--reject and --close settle the step terminally: no retry, room settles; " +
        "re-dispatch the work explicitly if you want it redone)");
    Console.Error.WriteLine(
        "       baton supply <room-dir> --worker <role> --output <name> --file <source-path> " +
        "--bindings <bindings-file> [--workflow-id <id>]");
    Console.Error.WriteLine($"       {ResumeOptionsParser.Usage[7..]}");
    Console.Error.WriteLine($"       {StatusOptionsParser.Usage[7..]}");
    Console.Error.WriteLine($"       {WatchOptionsParser.Usage[7..]}");
    Console.Error.WriteLine($"       {DeliverOptionsParser.Usage[7..]}");
    Console.Error.WriteLine("       baton templates [--json]");
    Console.Error.WriteLine($"       {KeepOptionsParser.Usage[7..]}");
    Console.Error.WriteLine($"       {UnkeepOptionsParser.Usage[7..]}");
    Console.Error.WriteLine($"       {TrustOptionsParser.Usage[7..]}");
    Console.Error.WriteLine($"       {RoomDeleteOptionsParser.Usage[7..]}");
    Console.Error.WriteLine($"       {RoomsPruneOptionsParser.Usage[7..]}");
    Console.Error.WriteLine($"       {LedgerCommand.Usage[7..]}");
    Console.Error.WriteLine($"       {LedgerViewOptionsParser.Usage[7..]}");
    Console.Error.WriteLine(
        "              (the two 'ledger' forms read different files -- 'baton ledger --help' says which)");
    Console.Error.WriteLine($"       {MemoryAuditOptionsParser.Usage[7..]}");
    Console.Error.WriteLine(
        "       baton mcp [--capture-file <path>] [--memory-proposal-tool] [--fleet-status-tool] [--room-detail-tool]");
    Console.Error.WriteLine("       baton daemon [--no-mutex]");
    Console.Error.WriteLine("       baton --version");
    Console.Error.WriteLine();
    Console.Error.WriteLine($"  {RunOptionsParser.ResumeNote}");
    return 64;
}

using var hostStopSource = new CancellationTokenSource();

// The host-initiated stop (M10 Phase 2), finally wired to something: Ctrl+C no longer kills the
// process outright — it cancels the ambient token the pump races against, which records
// CancellationRequested for every in-flight execution before signalling any of them, intent-first.
// Suppressing the default SIGINT behavior is what keeps the process alive long enough for that to
// happen.
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    hostStopSource.Cancel();
};

// #1356: the room directory for whichever mutating command is about to run, captured as soon as
// its options are parsed — declared here, outside the try, so the typed-exception catch below can
// still see it and record a pre-ledger failure sentinel for `run`/`dispatch` even though the
// BatonFlowException that reaches it was thrown from deep inside RunCommand.ExecuteAsync, well past
// the switch's own scope (a variable declared inside `try` is not visible in its `catch`).
string? roomDirectoryPathForFailureSentinel = null;

try
{
    // Read-only, and never a mutation surface (#730) -- it produces no CommandResult (there is
    // nothing "resumed from" and nothing to pump to a fixed point) and always exits 0 when it
    // manages to print a status at all, so it is handled here rather than joining the
    // CommandResult/FlowStateReporter shape every mutating command below shares.
    if (args[0] == "status")
    {
        var statusOptions = StatusOptionsParser.Parse(args[1..]);
        await StatusCommand.ExecuteAsync(statusOptions, Console.Out, hostStopSource.Token).ConfigureAwait(false);
        return 0;
    }

    // #1488: block-free, produces no CommandResult (there is nothing to pump) -- joins status/deliver
    // above rather than the CommandResult/FlowStateReporter switch below.
    if (args[0] == "watch")
    {
        var watchOptions = WatchOptionsParser.Parse(args[1..]);
        return await WatchCommand.ExecuteAsync(watchOptions, Console.Out, hostStopSource.Token).ConfigureAwait(false);
    }

    if (args[0] == "deliver")
    {
        var deliverOptions = DeliverOptionsParser.Parse(args[1..]);
        await DeliverCommand.ExecuteAsync(deliverOptions, Console.Out, hostStopSource.Token).ConfigureAwait(false);
        return 0;
    }

    if (args[0] == "templates")
    {
        return await TemplatesCommand.ExecuteAsync(args[1..], Console.Out, hostStopSource.Token).ConfigureAwait(false);
    }

    // #1156: a filesystem-marker mutation, not a workflow pump — no CommandResult to report, so
    // this joins status/templates above rather than the CommandResult/FlowStateReporter switch below.
    if (args[0] == "keep")
    {
        var keepOptions = KeepOptionsParser.Parse(args[1..]);
        await KeepCommand.MarkAsync(keepOptions, Console.Out, hostStopSource.Token).ConfigureAwait(false);
        return 0;
    }

    if (args[0] == "unkeep")
    {
        var unkeepOptions = UnkeepOptionsParser.Parse(args[1..]);
        await KeepCommand.UnmarkAsync(unkeepOptions, Console.Out, hostStopSource.Token).ConfigureAwait(false);
        return 0;
    }

    // #1166 (TrustCommand's own doc has why this verb exists): list/register/revoke against
    // ProjectCeilingStore produces no CommandResult (no workflow pump), so this joins keep/unkeep/watch
    // above rather than the CommandResult/FlowStateReporter switch below.
    if (args[0] == "trust")
    {
        var trustOptions = TrustOptionsParser.Parse(args[1..]);
        return await TrustCommand.ExecuteAsync(trustOptions, Console.Out, hostStopSource.Token).ConfigureAwait(false);
    }

    // #1659: `room`/`rooms` are noun-first verb groups (only one sub-verb each today —
    // `delete`/`prune` — but the shape leaves room for a later `room show`/`rooms list` without a new
    // top-level word). Neither produces a CommandResult (no workflow pump), so both join
    // keep/unkeep/status/templates above rather than the CommandResult/FlowStateReporter switch below.
    if (args[0] == "room")
    {
        if (args.Length < 2 || args[1] != "delete")
        {
            throw new CliArgumentException($"Unknown 'baton room' sub-verb. {RoomDeleteOptionsParser.Usage}");
        }

        var roomDeleteOptions = RoomDeleteOptionsParser.Parse(args[2..]);
        await RoomDeleteCommand.ExecuteAsync(roomDeleteOptions, Console.Out, hostStopSource.Token).ConfigureAwait(false);
        return 0;
    }

    if (args[0] == "rooms")
    {
        if (args.Length < 2 || args[1] != "prune")
        {
            throw new CliArgumentException($"Unknown 'baton rooms' sub-verb. {RoomsPruneOptionsParser.Usage}");
        }

        var roomsPruneOptions = RoomsPruneOptionsParser.Parse(args[2..]);
        await RoomsPruneCommand.ExecuteAsync(roomsPruneOptions, Console.Out, cancellationToken: hostStopSource.Token).ConfigureAwait(false);
        return 0;
    }

    // #1570: fleet-level, not a room mutation and produces no CommandResult (no workflow pump) --
    // joins room/rooms above rather than the CommandResult/FlowStateReporter switch below.
    if (args[0] == "ledger")
    {
        // Two commands under one verb, against two different files: `--rebuild` re-walks live rooms
        // into the per-execution BURN ledger (#1570, quota-ledger.jsonl), everything else READS the
        // repository-keyed COST ledger (#1849 phase B, ledger/<repo>.jsonl). Neither touches the
        // other's file -- LedgerViewOptionsParser.HelpLines says so where an operator will see it.
        if (args.Length >= 2 && args[1] == "--rebuild")
        {
            if (args.Length > 2)
            {
                throw new CliArgumentException(
                    $"'baton ledger --rebuild' takes no other arguments (got '{args[2]}'). {LedgerCommand.Usage}");
            }

            return await LedgerCommand.RebuildAsync(Console.Out, cancellationToken: hostStopSource.Token).ConfigureAwait(false);
        }

        var ledgerViewOptions = LedgerViewOptionsParser.Parse(args[1..]);
        return await LedgerViewCommand
            .ExecuteAsync(ledgerViewOptions, Console.Out, cancellationToken: hostStopSource.Token).ConfigureAwait(false);
    }

    // #1852 phase A: a noun-first verb group like `room`/`rooms` above -- `audit` is the only
    // sub-verb today, and `sync` (the writing half, phase C) is the reason the shape leaves room for
    // a second. Read-only, produces no CommandResult, so it joins them rather than the switch below.
    if (args[0] == "memory")
    {
        if (args.Length < 2 || args[1] != "audit")
        {
            throw new CliArgumentException($"Unknown 'baton memory' sub-verb. {MemoryAuditOptionsParser.Usage}");
        }

        var memoryAuditOptions = MemoryAuditOptionsParser.Parse(args[2..]);
        return await MemoryAuditCommand
            .ExecuteAsync(memoryAuditOptions, Console.Out, cancellationToken: hostStopSource.Token).ConfigureAwait(false);
    }

    CommandResult result;
    switch (args[0])
    {
        case "run":
            {
                var options = RunOptionsParser.Parse(args[1..]);
                roomDirectoryPathForFailureSentinel = options.RoomDirectoryPath;
                result = await RunCommand.ExecuteAsync(options, WorkerAdapterRegistry.Default, cancellationToken: hostStopSource.Token)
                    .ConfigureAwait(false);
                break;
            }

        case "dispatch":
            {
                var options = DispatchOptionsParser.Parse(args[1..]);
                roomDirectoryPathForFailureSentinel = options.RoomDirectoryPath;
                result = await DispatchCommand.ExecuteAsync(options, WorkerAdapterRegistry.Default, hostStopSource.Token)
                    .ConfigureAwait(false);
                break;
            }

        case "redispatch":
            {
                var options = RedispatchOptionsParser.Parse(args[1..]);
                roomDirectoryPathForFailureSentinel = options.RoomDirectoryPath;
                result = await RedispatchCommand.ExecuteAsync(options, WorkerAdapterRegistry.Default, hostStopSource.Token)
                    .ConfigureAwait(false);
                break;
            }

        case "cancel":
            {
                var options = CancelOptionsParser.Parse(args[1..]);
                result = await CancelCommand.ExecuteAsync(options, WorkerAdapterRegistry.Default, hostStopSource.Token)
                    .ConfigureAwait(false);
                break;
            }

        case "decide":
            {
                var options = DecideOptionsParser.Parse(args[1..]);
                result = await DecideCommand.ExecuteAsync(options, WorkerAdapterRegistry.Default, cancellationToken: hostStopSource.Token)
                    .ConfigureAwait(false);
                break;
            }

        case "resolve":
            {
                var options = ResolveOptionsParser.Parse(args[1..]);
                result = await ResolveCommand.ExecuteAsync(options, hostStopSource.Token)
                    .ConfigureAwait(false);
                break;
            }

        case "resume":
            {
                var options = ResumeOptionsParser.Parse(args[1..]);
                result = await ResumeCommand.ExecuteAsync(options, WorkerAdapterRegistry.Default, hostStopSource.Token)
                    .ConfigureAwait(false);
                break;
            }

        default:
            {
                var options = SupplyOptionsParser.Parse(args[1..]);
                var supplyResult = await SupplyCommand.ExecuteAsync(options, WorkerAdapterRegistry.Default, hostStopSource.Token)
                    .ConfigureAwait(false);
                Console.WriteLine($"Supplementary execution: {supplyResult.ExecutionId}");
                result = supplyResult.Command;
                break;
            }
    }

    FlowStateReporter.Report(Console.Out, result);

    // #669: a provisioned worktree that was kept (uncommitted changes) or could not be removed is
    // surfaced, not swallowed — the run still succeeded, so this is an advisory on stderr.
    foreach (var teardown in result.WorktreeTeardowns)
    {
        Console.Error.WriteLine(
            $"worktree {teardown.Outcome} at {teardown.WorktreePath}"
            + (teardown.Detail is { } detail ? $" — {detail}" : string.Empty));
    }

    // #1356 point 4: written on reaching Terminal for every mutating command, not just `run` — a
    // workflow that pauses and is later carried to Terminal by a separate `baton decide` needs this
    // exactly as much as a straight-through `baton run`. Last, deliberately: every output an outcome
    // could reference is already on disk by the time the pump/decision call above returned.
    if (result.State.Status == WorkflowStatus.Terminal && result.RoomDirectoryPath is { } terminalRoomDirectoryPath)
    {
        // #1360: entries feeds the sentinel's per-execution usage. A fresh ledger read (CommandResult
        // carries only the already-projected FlowState, not the raw entries) -- one extra read at
        // terminal completion, not a hot path.
        var terminalLogPath = Path.Combine(terminalRoomDirectoryPath, BatonPaths.FlowLogFileName);
        var terminalEntries = await new FlowEventLogReader(terminalLogPath)
            .ReadAllEntriesWithTimestampsAsync(CancellationToken.None).ConfigureAwait(false);
        var view = WorkflowStatusProjector.Project(result.State, result.Snapshot, terminalRoomDirectoryPath, terminalEntries);
        // CancellationToken.None: a Ctrl-C that already carried the workflow to Terminal must not
        // then lose the sentinel write for the terminal state it just reached.
        await TerminalSentinelWriter.WriteAsync(terminalRoomDirectoryPath, view, CancellationToken.None).ConfigureAwait(false);

        // #1570: the fleet-level burn ledger, appended right after the sentinel -- terminalEntries is
        // already in hand from the read above, so this costs one more in-memory pass, not a second
        // flow.jsonl read (spec/baton.md §7's harvest-at-settle ruling). Fire-and-forget with respect
        // to the run, same posture as the sentinel write's own fail-open contract
        // (QuotaLedgerStore.AppendAsync's doc comment states the exact rule this leans on): a ledger
        // write must never be the reason a run that already reached Terminal reports as failed.
        try
        {
            var ledgerEntries = QuotaLedgerStore.BuildEntries(terminalEntries, terminalRoomDirectoryPath);
            await QuotaLedgerStore.AppendAsync(ledgerEntries, BatonPaths.QuotaLedgerFile, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            Console.Error.WriteLine($"Could not append to the quota ledger at '{BatonPaths.QuotaLedgerFile}': {ex.Message}.");
        }

        // #1849 phase A: the repository-keyed cost ledger, appended from the SAME terminalEntries the
        // burn ledger above just read -- it consumes that ledger's per-execution source rather than
        // replacing it (CostLedgerStore's own remarks state the split). Its own try/catch, not the
        // block above's: a failure here must not also lose the burn-ledger append, and vice versa.
        // Same fail-open contract either way -- an accounting write never gates a settled run.
        try
        {
            var repository = await RepositoryIdentityResolver
                .TryResolveForRoomAsync(terminalRoomDirectoryPath, CancellationToken.None).ConfigureAwait(false);
            if (repository is not null)
            {
                var costLedgerPath = BatonPaths.CostLedgerFile(repository.FileSlug);

                // #1848: the audited runway override, read back off this room's own bindings.json so a
                // row that only exists because a hold was bypassed says so. Fail-open by construction
                // (RunwayOverrideReasons' own doc) -- an unreadable bindings file costs the stamp, never
                // the row.
                var runwayOverrides = await RunwayOverrideReasons
                    .ReadForRoomAsync(terminalRoomDirectoryPath, CancellationToken.None).ConfigureAwait(false);
                var costEntries = CostLedgerStore.BuildEntries(
                    terminalEntries, terminalRoomDirectoryPath, repository,
                    runwayOverrideReasonByWorker: runwayOverrides);
                await CostLedgerStore.AppendAsync(costEntries, costLedgerPath, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                // The one path that writes no row without raising anything -- said out loud, because an
                // empty cost ledger is otherwise indistinguishable from a fleet that spent nothing.
                Console.Error.WriteLine(
                    $"No repository identity for room '{terminalRoomDirectoryPath}' (git found no origin remote "
                    + "or repository for its recorded project root), so no cost ledger row was written for it.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            Console.Error.WriteLine($"Could not append to the cost ledger: {ex.Message}.");
        }
    }
    else if (args[0] == "resolve" && result.RoomDirectoryPath is { } resolvedNonTerminalRoomDirectoryPath)
    {
        // #1608 review finding 1, narrowed by #1877: `baton resolve --accept-capture` in a multi-step
        // room clears IndeterminateAwaitingResolution and can make a DOWNSTREAM step newly
        // deliverable, so DeriveWorkflowStatus reads Running again on a room whose LAST write above
        // (back when it first settled Indeterminate) left a Terminal sentinel on disk. (#1877: the
        // RESOLVED step itself can no longer put the room here — a rejection now forecloses its own
        // retry — but another still-deliverable step in the same DAG can, so this stays branch-scoped
        // to `resolve` rather than to a verb.) Left in place,
        // that stale terminal.json both fools FleetStatusTool's sentinel-first fast path into a frozen
        // "Indeterminate" reading and permanently blocks RedispatchCommand's own sentinel-gated refusal
        // from ever clearing — a resolved-but-reopened room a harness can never redispatch again. No
        // other verb can turn a Terminal room back non-Terminal, so this is scoped to `resolve` alone
        // rather than a general post-command rule.
        // bestEffort: the resolution above is already durable, so a sentinel this call cannot delete
        // must not report a succeeded resolution as failed (#1608 re-review finding 2 — see
        // DeleteStaleSentinel's own remarks for the opposite choice at RunCommand's call site).
        TerminalSentinelWriter.DeleteStaleSentinel(resolvedNonTerminalRoomDirectoryPath, bestEffort: true);

        // #1608 review finding 4, narrowed by #1877: `resolve` never re-drives the DAG itself
        // (spec/baton.md §3, which enumerates the two shapes that reach here after #1877 narrowed
        // them) — either leaves this room genuinely non-Terminal with nothing left to notice, unless
        // told. Named here rather than left implicit so a harness never has to infer the follow-up.
        //
        // #1608 re-review finding 1: branched on the returned state, because `baton run` is the wrong
        // verb for one of the two non-Terminal shapes. A step that declares a PausePoint and settles
        // Indeterminate is Paused with the flag still set (spec/baton.md §3), and resolving it clears
        // the flag but not the pause — only FlowEvent.WorkflowResumed removes it — so the room is
        // still Paused here, and a `baton run` against it re-enters the same unfulfilled obligation
        // and returns Paused again. `baton decide` is the verb that discharges it, spelled out in
        // full rather than deferred to "the arguments above": FlowStateReporter prints the execution
        // id and supersede targets, but --type and --bindings appear nowhere in this stdout, and a
        // verb an operator cannot actually invoke is the dead end review finding 1 was about.
        Console.WriteLine(result.State.Status == WorkflowStatus.Paused
            ? "Room is not yet complete — this room is still paused; record the pause decision with "
              + "`baton decide <room-dir> --execution <execution-id> --type resume|reject|retry-with-revision|supersede "
              + "--bindings <bindings-file>` (the execution id is printed above), which is what resumes it."
            : $"Room is not yet complete — {RecoveryGuidance.RunRoomDirInstruction}.");
    }

    // #1359: baton resume gets the same truthful exit-code table as run/dispatch — its own design
    // ruling names the completion contract explicitly, unlike cancel/decide/supply below, which
    // #1356 never asked to widen. #1441: baton redispatch drives the identical RunCommand pump a fresh
    // dispatch does, so it gets the same table for the same reason.
    if (args[0] is "run" or "dispatch" or "redispatch" or "resume")
    {
        return (int)RunExitCodeResolver.Resolve(result);
    }

    // Still the 0/1 contract for cancel/decide/supply/resolve — #1356 scoped its exit-code table to
    // run/dispatch only; widening it to the rest was not asked for and is not done here. #1650 F2
    // moved the expression itself into MutationExitCodeResolver (which also handles cancel's queued
    // arm) so its arms are assertable without spawning a process.
    return MutationExitCodeResolver.Resolve(result);
}
catch (BatonFlowException ex) when (ex is Baton.Concurrency.WorkflowLockedException or Baton.Store.FlowJournalHeldException)
{
    // #1374 F1: this room is held by another Flow instance -- most often a live 'baton run' pump on
    // a perfectly healthy room, sometimes a background component's brief lock. Neither is a
    // provisioning/validation refusal, so this must NOT fall into the catch below: writing a
    // Failed sentinel here would tell a file-watcher a running room just died, and it would
    // contradict 'baton status --json' reading the very same room's ledger as Running at the same
    // moment. The room is left exactly as it was; the exit code alone says "retry later".
    WriteErrorWithTry(ex);
    return args[0] is "run" or "dispatch" or "redispatch" or "resume" ? (int)RunExitCode.RoomHeld : 1;
}
catch (Baton.Status.StaleSentinelDeletionException ex)
{
    // #1608 re-review finding 2: a stale terminal.json that could not be deleted refuses the run
    // rather than starting a pump behind a false "already done" signal. Kept out of the catch below
    // deliberately: that one would answer a locked sentinel by trying to WRITE the very same locked
    // path (WriteValidationRefusedAsync -> File.Move onto terminal.json), turning a clean refusal
    // back into the raw IOException this arm exists to remove -- the same "leave the room exactly as
    // it was" carve-out shape #1374 F1 uses above.
    WriteErrorWithTry(ex);
    return args[0] is "run" or "dispatch" or "redispatch" or "resume" ? (int)RunExitCode.ValidationRefused : 1;
}
catch (BatonFlowException ex)
{
    // The typed-exception boundary CLAUDE.md's error-handling rules require: every malformed
    // workflow/bindings/argument failure surfaces as one of these further up the call stack, so
    // this is the one place that turns it into a clean CLI failure instead of a raw stack trace.
    WriteErrorWithTry(ex);

    // #1356 points 2+3: for `run`/`dispatch` specifically, this is the provisioning/validation
    // failure class — distinct from a worker that actually ran and failed — and the room (which
    // Directory.CreateDirectory already created inside RunCommand/DispatchCommand by the time
    // anything here can throw) must be left queryable rather than eternally "Running/no ledger yet".
    //
    // #1374 F1: only when the room is genuinely pre-ledger (RoomLedgerProbe.HasLedger, not a bare
    // File.Exists -- see that type's own doc for why a zero-byte flow.jsonl must not count). A room
    // with a real ledger has been dispatched at least once before -- its ledger (or a still-live
    // pump) is the room's real terminal record, and this invocation's own failure must not overwrite
    // it with a fabricated Failed/no-outputs sentinel (see invoking-baton.md's exit-code section for
    // the scenario this guards). The exit code still reports the refusal; only the sentinel write is
    // conditional.
    if (args[0] is "run" or "dispatch" or "redispatch" && roomDirectoryPathForFailureSentinel is not null)
    {
        if (!RoomLedgerProbe.HasLedger(roomDirectoryPathForFailureSentinel))
        {
            await TerminalSentinelWriter.WriteValidationRefusedAsync(
                roomDirectoryPathForFailureSentinel, ex.Message, CancellationToken.None, ex.TryInvocation).ConfigureAwait(false);
        }

        return (int)RunExitCode.ValidationRefused;
    }

    // #1359: a resume always targets an already-dispatched room — it never has a pre-ledger state to
    // leave a sentinel for (that branch above is run/dispatch-only), but its own refusals (no
    // SessionId recorded, an ambiguous or unresolvable worker, a still-running target) are exactly
    // #1356's ValidationRefused shape: refused before anything new was dispatched.
    if (args[0] == "resume")
    {
        return (int)RunExitCode.ValidationRefused;
    }

    return 1;
}

// #1382 F8: the one place either BatonFlowException catch above prints an error, so a Try line set on
// a future WorkflowLockedException/FlowJournalHeldException is never silently dropped again.
static void WriteErrorWithTry(BatonFlowException ex)
{
    Console.Error.WriteLine(ex.Message);
    if (ex.TryInvocation is not null)
    {
        Console.Error.WriteLine($"Try: {ex.TryInvocation}");
    }
}
