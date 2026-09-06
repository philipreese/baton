using Baton.Accounting;
using Baton.Domain;
using Baton.Status;
using Baton.Store;

namespace Baton.Cli;

/// <summary>
/// Everything that happens after a command carries a room to Terminal: the terminal sentinel, the
/// fleet burn ledger, and the repository-keyed cost ledger. Extracted from <c>Program.cs</c> (#1934
/// slice 1) because it now has a second caller — the daemon's queue scheduler dispatches in-process,
/// so a queue-launched room reaches Terminal without <c>Program.cs</c>'s top-level code ever running.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the extraction rather than a second copy.</b> Before it, a queue-launched room got no
/// <c>terminal.json</c> at all — invisible to <c>FleetStatusTool</c>'s sentinel-first fast path, and
/// with no cost-ledger row, which is indistinguishable from a lane that spent nothing. Re-implementing
/// the block daemon-side would have made the accounting rules two places that must agree, which is
/// the drift the record-once gate exists to stop.
/// </para>
/// <para>
/// <b>Fail-open throughout, and unchanged from what <c>Program.cs</c> did.</b> Both ledger writes have
/// their own <c>try</c> so a failure in one cannot lose the other, and neither is ever the reason a
/// run that already reached Terminal reports as failed. Each individual rule's own <c>#</c> reference
/// stays inline below; this doc comment states only what moved and why.
/// </para>
/// </remarks>
public static class TerminalSettleRecorder
{
    /// <summary>
    /// Records <paramref name="result"/>'s settle, or does nothing when it did not reach Terminal or
    /// carries no room.
    /// </summary>
    /// <param name="deliveryProbeToken">
    /// Bounds <see cref="WorkspaceDeliveryProbe"/> alone — the one call here that spawns child
    /// processes touching the network, and so the one a person can still be waiting on after the room
    /// has settled (#1913 review finding 2). Every other write below takes
    /// <see cref="CancellationToken.None"/> deliberately: they are local file writes a Ctrl-C (or, for
    /// the daemon, a shutdown) must not lose, each already bounded by <c>JsonLinesLedger</c>'s lock
    /// timeout. Cancelling during the probe writes the cost row with the delivery fields absent, the
    /// same absence a missing <c>gh</c> produces.
    /// </param>
    public static async Task RecordAsync(CommandResult result, CancellationToken deliveryProbeToken)
    {
        ArgumentNullException.ThrowIfNull(result);

        // #1356 point 4: written on reaching Terminal for every mutating command, not just `run` — a
        // workflow that pauses and is later carried to Terminal by a separate `baton decide` needs this
        // exactly as much as a straight-through `baton run`.
        if (result.State.Status != WorkflowStatus.Terminal || result.RoomDirectoryPath is not { } terminalRoomDirectoryPath)
        {
            return;
        }

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
            var (repository, identitySource) = await RepositoryIdentityResolver
                .TryResolveForRoomAsync(terminalRoomDirectoryPath, CancellationToken.None).ConfigureAwait(false);
            if (repository is not null)
            {
                var costLedgerPath = BatonPaths.CostLedgerFile(repository.FileSlug);

                // #1848's audited runway override and #1499's dispatch --label, both read back off this
                // room's own bindings.json in ONE parse (RoomBindingStamps' own remarks say why one
                // reader rather than two). Fail-open by construction -- an unreadable bindings file
                // costs the stamps, never the row. A pure file read, so CancellationToken.None like the
                // other local writes here rather than the delivery probe's token.
                var stamps = await RoomBindingStamps
                    .ReadForRoomAsync(terminalRoomDirectoryPath, CancellationToken.None).ConfigureAwait(false);

                // #1901 C1: the issue, PR and diff shape each worker's own workspace still holds. Read
                // here rather than inside the ledger for the same reason the overrides above are
                // (WorkspaceDeliveryProbe's own remarks), and fail-open in exactly the same way -- a
                // workspace that is gone, a `gh` that is absent, or a network that is down costs the
                // stamp, never the row. See this method's own parameter doc for why this one call
                // takes the caller's token where every other write here takes None.
                var delivery = await WorkspaceDeliveryProbe
                    .ReadForRoomAsync(terminalRoomDirectoryPath, deliveryProbeToken).ConfigureAwait(false);

                // identitySource, from the resolver rather than assumed here (#1931 re-review MEDIUM):
                // the settle site writes most of the ledger, so a field only the backfill stamped would
                // partition the file by WRITER instead of by provenance -- which is the one question it
                // exists to answer.
                var costEntries = CostLedgerStore.BuildEntries(
                    terminalEntries, terminalRoomDirectoryPath, repository,
                    runwayOverrideReasonByWorker: stamps.RunwayOverrideReasonByWorker,
                    deliveryByWorker: delivery,
                    labelByWorker: stamps.LabelByWorker,
                    identitySource: identitySource);
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
}
