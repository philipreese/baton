using System.Collections.Concurrent;
using Baton.Domain;
using Baton.Mutation;
using Baton.Projection;
using Baton.Store;

namespace Baton.Cli;

/// <summary>
/// The pump-side reader for <see cref="CancelRequestFile"/> (#1495): polls a room directory for
/// <c>cancel.request</c> at a modest cadence — a cheap <see cref="File.Exists(string)"/> stat per tick,
/// never touching <c>flow.lock</c> — and routes a found request to
/// <see cref="InFlightExecutionRegistry.RequestCancellationAsync"/>, which itself appends the durable
/// <see cref="Baton.Domain.FlowEvent.CancellationRequested"/> record before signalling anything: this
/// poller adds no second recording path of its own. Started and stopped by <see cref="RunCommand"/>
/// alongside its own <see cref="MutationInterface.StartWorkflowAsync"/> call — the registry it is
/// given must be the same instance that call bound to the run's <c>IEventLogWriter</c>.
/// <para>
/// #1556 (generalized from #1563's narrower quota-parked-only case, S0 of the quota design, #802):
/// any target <see cref="Projection.ArrestableExecutions.Find"/> still admits but with no live
/// process to deliver to is marked via <see cref="InFlightExecutionRegistry.MarkArrestIntent"/>
/// instead of being told it is too late (or, pre-#1556, silently left to the bounded retry with no
/// mark at all for the non-parked shapes — see <c>spec/baton.md</c>'s arrest section for the full
/// shape list this now covers). That
/// mark records nothing by itself; the pump validates and appends the durable events once it wakes,
/// exactly as <c>RequestCancellationAsync</c> does for a live process — and it wakes on TWO separate
/// waits, not one: the idle-deferral wait (nothing else in flight) and the busy wait (a sibling
/// step's dispatch still is), both wired to the same latch (<c>MutationInterface</c>'s own remarks on
/// each site have the detail). This poller has no <c>workerBindings</c> in scope, so it cannot itself
/// tell a genuinely non-process target from a Process one that simply has not registered yet — that
/// fail-closed gate is <c>MutationInterface.SettleArrestIntentsAsync</c>'s alone, which is exactly
/// why marking here is unconditional (whenever <see cref="ArrestableExecutions.Find"/> still admits
/// the target) while the pump's own bounded 5-tick ceiling below stays the honest backstop for a
/// still-Process target that never resolves — a <em>file-channel</em> backstop only since #2045: it
/// stops retrying and writes the <c>.rejected</c> body, and deliberately journals no
/// <see cref="FlowEvent.CancellationRejected"/> for a target the mark above still owes a delivery to.
/// </para>
/// </summary>
public static class CancelRequestPoller
{
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(2);

    private static readonly ConcurrentDictionary<(string Path, string Target, DateTime LastWriteUtc), int> RetryCounters = new();

    // #1605 review, F1: the isParked skip below used to print nothing, so an operator watching a
    // park had no signal a mark had actually landed until (possibly a day later) it settled. Printed
    // once per distinct request (keyed the same way RetryCounters is) rather than every ~2s tick for
    // the park's whole duration.
    private static readonly ConcurrentDictionary<(string Path, string Target, DateTime LastWriteUtc), byte> ParkedNoticePrinted = new();

    /// <summary>
    /// Runs until <paramref name="cancellationToken"/> fires. Never throws for a malformed request, a
    /// transient filesystem error, or any other fault a single tick raises — every exception except the
    /// loop's own cancellation is caught, logged, and the loop continues — so a caller can
    /// fire-and-forget this alongside the pump call it accompanies: a poller fault must never replace
    /// whatever that pump call itself threw (or its successful result) by escaping this method.
    /// </summary>
    public static async Task RunAsync(
        string roomDirectoryPath,
        string logPath,
        WorkflowDefinitionSnapshot snapshot,
        InFlightExecutionRegistry inFlightExecutions,
        TimeSpan pollInterval,
        CancellationToken cancellationToken,
        string? roomLogPath = null)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await TickAsync(roomDirectoryPath, logPath, snapshot, inFlightExecutions, cancellationToken, roomLogPath)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // A tick's own fault (a torn log read racing the pump's own writer, an unreadable
                // request file) must never bring down the pump it is only ever a side channel to —
                // log it and keep polling; the request, if any, is simply retried next tick.
                try
                {
                    Console.Error.WriteLine($"cancel.request poll against '{roomDirectoryPath}' failed this tick: {ex.Message}");
                }
                catch
                {
                    // F6: swallow broken stderr pipe
                }
            }
        }
    }

    /// <summary>
    /// One poll cycle: absent file is the overwhelmingly common case and costs one stat. A present file
    /// is parsed, resolved (<see cref="CancelRequestFile.LatestTarget"/> against the room's own
    /// currently-projected candidate — a <see cref="Domain.StepStatus.Running"/> step or a
    /// quota-parked one (#1607) — via <see cref="RunningExecutionResolver"/>, or taken as a literal
    /// <see cref="ExecutionId"/> otherwise), and delivered to <paramref name="inFlightExecutions"/>.
    /// A resolved parked target still lands on the <c>isParked</c> branch below exactly like an
    /// explicit one always has — this method never needed to change for the resolver to widen, only
    /// the resolver itself did. A delivered request or a genuinely settled
    /// one is consumed; an undelivered still-arrestable target (running, quota-parked, or — #1556 PR 1 —
    /// a still-pending step-less execution, via <see cref="Projection.ArrestableExecutions.Find"/>) is
    /// marked on <paramref name="inFlightExecutions"/> (#1556 PR 2) and retried up to 5 ticks before
    /// being rejected with an outcome that says arrest was requested, not that the target was
    /// unreachable — that ceiling rejection is written to the <c>.rejected</c> file and stderr only
    /// (#2045: a marked target never receives a durable <see cref="FlowEvent.CancellationRejected"/>
    /// from it), and the parked shape alone retries unbounded, per its own remarks below; malformed
    /// content or an unresolvable <c>latest</c> is rejected immediately (fail closed, no guessing,
    /// reason in body) rather than retried forever or left to crash the pump.
    /// </summary>
    internal static async Task TickAsync(
        string roomDirectoryPath,
        string logPath,
        WorkflowDefinitionSnapshot snapshot,
        InFlightExecutionRegistry inFlightExecutions,
        CancellationToken cancellationToken,
        string? roomLogPath = null)
    {
        var requestPath = CancelRequestFile.GetPath(roomDirectoryPath);
        if (!File.Exists(requestPath))
        {
            return;
        }

        var lastWriteUtc = new FileInfo(requestPath).LastWriteTimeUtc;
        var content = await CancelRequestFile.TryReadAsync(requestPath, cancellationToken).ConfigureAwait(false);
        if (content is null)
        {
            const string reason = "malformed content (not valid JSON, or a blank/missing Target)";
            CancelRequestFile.Reject(requestPath, target: null, reason);
            // #1530: no Target survives a malformed parse, so there is nothing to key a
            // FlowEvent.CancellationRejected on -- this is the ONE durable record of this rejection
            // beyond the ephemeral .rejected file body and a stderr line.
            await TryRecordUnresolvableAsync(roomLogPath, string.Empty, reason, lastWriteUtc, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        ExecutionId targetExecutionId;
        if (string.Equals(content.Target, CancelRequestFile.LatestTarget, StringComparison.Ordinal))
        {
            var reader = new FlowEventLogReader(logPath);
            var events = await reader.ReadAllAsync(cancellationToken).ConfigureAwait(false);
            var state = StateProjector.Project(events, snapshot);
            var resolved = RunningExecutionResolver.Resolve(state);

            if (resolved.Single is not { } single)
            {
                var reason = resolved.RunningExecutionIds.Count == 0
                    ? "'latest' requested, but no execution is currently Running or quota-parked"
                    : $"'latest' requested, but {resolved.RunningExecutionIds.Count} executions are currently " +
                        $"Running or quota-parked ({string.Join(", ", resolved.RunningExecutionIds.Select(id => id.Value))}) — ambiguous";
                CancelRequestFile.Reject(requestPath, content.Target, reason);
                await TryRecordUnresolvableAsync(
                        roomLogPath, content.Target, reason, content.WrittenAtUtc?.UtcDateTime ?? lastWriteUtc, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            targetExecutionId = single;
        }
        else
        {
            targetExecutionId = new ExecutionId(content.Target);
        }

        var retryKey = (requestPath, content.Target, lastWriteUtc);
        var delivered = await inFlightExecutions.RequestCancellationAsync(targetExecutionId, cancellationToken).ConfigureAwait(false);
        if (delivered)
        {
            RetryCounters.TryRemove(retryKey, out _);
            ParkedNoticePrinted.TryRemove(retryKey, out _);
            CancelRequestFile.Consume(requestPath);
            return;
        }

        // Delivered was false: re-check projection to differentiate settled from still-arrestable
        // (running, quota-parked, or — #1556 PR 1's D2 fix — a still-pending step-less execution,
        // which ArrestableExecutions.Find sees and the old Steps-only lookup below did not).
        var settleCheckReader = new FlowEventLogReader(logPath);
        var settleCheckEvents = await settleCheckReader.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        var settleCheckState = StateProjector.Project(settleCheckEvents, snapshot);
        var targetStep = settleCheckState.Steps.FirstOrDefault(s => s.LatestExecutionId == targetExecutionId);
        var stillArrestable = ArrestableExecutions.Find(settleCheckState, snapshot, targetExecutionId) is not null;

        // #1556 (generalized from #1563's narrower quota-parked-only mark, "three independent locks"
        // finding #802): every shape `stillArrestable` can be true for (see spec/baton.md's arrest
        // section for the enumerated list) is marked on the SAME registry the pump's two waits watch,
        // instead of reporting the false "too late" verdict below or (pre-#1556, for the non-parked
        // shapes) silently falling through to the bounded retry with no mark at all. This poller
        // cannot itself tell non-process from a Process step that has not registered yet (no
        // workerBindings in scope) — that fail-closed proof is MutationInterface.SettleArrestIntentsAsync's,
        // so marking here is unconditional and the pump is the one that may still record nothing.
        // Idempotent: safe to re-mark on every tick until the pump drains it.
        var isParked = targetStep is { Status: StepStatus.Failed, RetryNotBefore: not null };
        if (stillArrestable)
        {
            inFlightExecutions.MarkArrestIntent(
                targetExecutionId,
                isParked
                    ? "quota-parked (no live process to signal)"
                    : "no live process registered for this target");
        }

        if (!stillArrestable)
        {
            RetryCounters.TryRemove(retryKey, out _);
            ParkedNoticePrinted.TryRemove(retryKey, out _);
            // The one-word difference that keeps this honest once the seam above can actually
            // settle a park: an execution the seam itself just cancelled did settle BECAUSE of this
            // request, not despite it — reporting "too late" for that case is the exact false claim
            // #802's "three independent locks" finding identified (F7, #1605 review: that finding is
            // derived from the code path, not a reproduced run — #802's own audit comment self-tags
            // it ASSUMED, high confidence, code-derived). Read off the terminal EVENT rather than the
            // projected step: a step-less target (#1556 PR 1's D2 case) has no Steps row to read
            // Cancelled off at all, so the Steps-only check was always false for it — silently wrong
            // once this poller started marking step-less targets, since the seam can now actually be
            // the thing that cancelled it a moment before this re-check.
            var arrestedByThisRequest = settleCheckEvents
                .OfType<FlowEvent.ExecutionCancelled>()
                .Any(e => e.ExecutionId == targetExecutionId);

            // #1916 fix round 2: "too late (it already settled)" asserts a real execution existed and
            // finished -- true only if some ExecutionRequestAccepted ever named targetExecutionId. A
            // typo'd or stale literal id (this branch takes any non-'latest' Target as-is, unvalidated,
            // per this method's own top) never was one, so it gets ArrestRequestUnresolvable's honest
            // "never existed" reason instead of a false "it settled" — the same distinction
            // MutationInterface.SettleArrestIntentsAsync already draws between "already settled" and
            // "unknown execution id" for the pump-side drop this poller's mark can also produce.
            var everAccepted = settleCheckEvents
                .OfType<FlowEvent.ExecutionRequestAccepted>()
                .Any(e => e.Request.ExecutionId == targetExecutionId);
            const string tooLateReason = "not currently in flight when this cancel.request was checked — too late (it already settled)";
            const string unresolvableReason = "no accepted request named this execution";
            var rejectionReason = everAccepted ? tooLateReason : unresolvableReason;
            try
            {
                Console.Error.WriteLine(arrestedByThisRequest
                    ? $"cancel.request against '{roomDirectoryPath}' named execution '{targetExecutionId.Value}' — arrested by this request."
                    : $"cancel.request against '{roomDirectoryPath}' named execution '{targetExecutionId.Value}', which is {rejectionReason}.");
            }
            catch
            {
                // F6: swallow broken stderr pipe
            }

            // #1530: the "too late"/unresolvable rendering above used to land on stderr only -- issue
            // #1530's own body names this shape explicitly. arrestedByThisRequest needs no separate
            // append here: that branch's own FlowEvent.ExecutionCancelled already implies an earlier
            // FlowEvent.CancellationRequested (MutationInterface.SettleArrestIntentsAsync never
            // appends one without the other), so the ledger already renders it Delivered.
            if (!arrestedByThisRequest)
            {
                if (everAccepted)
                {
                    await inFlightExecutions.RecordCancellationRejectedAsync(targetExecutionId, tooLateReason, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    // A third caller of the durable home TryRecordUnresolvableAsync's own doc names --
                    // see that method's remarks for why room.jsonl, not flow.jsonl, is where this lands.
                    await TryRecordUnresolvableAsync(
                            roomLogPath, content.Target, unresolvableReason,
                            content.WrittenAtUtc?.UtcDateTime ?? lastWriteUtc, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            CancelRequestFile.Consume(requestPath);
            return;
        }

        // #1563: a parked mark is a delivery GUARANTEE, not a hope — MutationInterface's
        // SettleArrestIntentsAsync will drain it and this loop's own derived obligations will
        // finalize it (the ledger-read parked-cancel block, IsParkedRetryTarget-guarded) within the
        // pump's next couple of rounds. Folding this into the bounded-retry counter below would let a slow round (several
        // other steps mid-dispatch, event-log I/O contention) hit the 5-tick ceiling before the pump
        // gets there, rejecting a request that was already going to succeed with the false claim
        // "not reachable" and deleting the pending file out from under the settle that follows
        // moments later. A live pump that never drains its mark is the dead-pump case #1586 covers,
        // not this one (scope note atop this file) — so this path retries forever rather than
        // guessing a ceiling for a wait this poller has no way to bound.
        if (isParked)
        {
            RetryCounters.TryRemove(retryKey, out _);
            if (ParkedNoticePrinted.TryAdd(retryKey, 0))
            {
                try
                {
                    Console.Error.WriteLine(
                        $"cancel.request against '{roomDirectoryPath}' named execution '{targetExecutionId.Value}' — "
                        + "target is quota-parked (no live process to signal); marked for delivery once the pump settles it.");
                }
                catch
                {
                    // F6: swallow broken stderr pipe
                }
            }

            return;
        }

        // Target STILL projects Running and is not parked: count a bounded retry. The mark above has
        // already been (re-)placed on this exact tick, so the honest ceiling here is only for the
        // genuinely unreachable case — a live Process dispatch that never registers within it (a dead
        // or wedged pump) — not for the ordinary non-process/step-less case, which the pump settles
        // within roughly one round of the mark landing.
        var retries = RetryCounters.AddOrUpdate(retryKey, 1, (_, current) => current + 1);
        if (retries >= 5)
        {
            RetryCounters.TryRemove(retryKey, out _);
            const string reason =
                "arrest requested (#1556) but not yet confirmed settled after 5 polls — the pump may still deliver it on "
                    + "its own; if the target is a live process that never registers, use Ctrl+C on the pump or wait for it to settle";
            CancelRequestFile.Reject(requestPath, content.Target, reason);

            // #2045: the FILE channel gives up here; the arrest itself does not, so no durable
            // FlowEvent.CancellationRejected is appended for it. Every target reaching this branch was
            // marked above (this branch is only reachable while ArrestableExecutions.Find still admits
            // it), and since #1825 a mark is a delivery guarantee the pump can honour moments later —
            // SettleArrestIntentsAsync then appends CancellationRequested/ExecutionCancelled for the
            // same id, and ArrestLedgerProjector folds the pair into ONE entry that reads Delivered
            // while still carrying this rejection's reason, a shape ArrestLedgerEntry.Reason
            // ("populated only for Rejected") says cannot exist. The two rejection shapes that DO
            // still journal a CancellationRejected are the ones the registry could not take: the
            // `!stillArrestable` "too late (it already settled)" path above, and
            // MutationInterface.SettleArrestIntentsAsync's own drop of an intent Find no longer
            // admits. stderr keeps the operator-facing signal this branch used to owe the journal.
            try
            {
                Console.Error.WriteLine(
                    $"cancel.request against '{roomDirectoryPath}' named execution '{targetExecutionId.Value}' — {reason}. "
                    + "The arrest stays marked for delivery, so no rejection was recorded to the journal.");
            }
            catch
            {
                // F6: swallow broken stderr pipe
            }
        }
    }

    /// <summary>
    /// #1530: best-effort append of <see cref="RoomEvent.ArrestRequestUnresolvable"/> to
    /// <c>room.jsonl</c> — the durable home for the two rejection shapes above that never resolve an
    /// <see cref="ExecutionId"/> to key a <see cref="FlowEvent.CancellationRejected"/> on.
    /// <paramref name="roomLogPath"/> is <c>null</c> for every caller that predates this feature or a
    /// test exercising <see cref="TickAsync"/> directly; a construction or append fault (room.jsonl
    /// contended by a concurrent room-scoped writer, per <c>BatonPaths.RoomLogFileName</c>'s own
    /// "the two logs take independent locks" remark) is swallowed the same way every other fault in
    /// this poller is — this is a supplementary record, never the rejection itself, which the
    /// <c>.rejected</c> file and stderr line above already recorded unconditionally.
    /// </summary>
    private static async Task TryRecordUnresolvableAsync(
        string? roomLogPath, string target, string reason, DateTime requestedAtUtc, CancellationToken cancellationToken)
    {
        if (roomLogPath is null)
        {
            return;
        }

        try
        {
            await using var roomWriter = new RoomEventLogWriter(roomLogPath);
            var now = DateTimeOffset.UtcNow;
            await roomWriter.AppendAsync(
                    new RoomEvent.ArrestRequestUnresolvable(
                        target, reason, new DateTimeOffset(DateTime.SpecifyKind(requestedAtUtc, DateTimeKind.Utc)), now),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                Console.Error.WriteLine($"Could not record unresolvable cancel.request to '{roomLogPath}': {ex.Message}");
            }
            catch
            {
                // F6: swallow broken stderr pipe
            }
        }
    }
}
