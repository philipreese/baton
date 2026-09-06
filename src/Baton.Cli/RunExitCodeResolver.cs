using System.Diagnostics;
using Baton.Domain;
using Baton.Status;

namespace Baton.Cli;

/// <summary>
/// The exit codes <c>baton run</c>/<c>baton dispatch</c> return (#1356) — distinct per failure class so a
/// caller can branch on <c>$?</c>/<c>%ERRORLEVEL%</c> alone, without parsing <c>status --json</c>.
/// <c>baton resume</c> (#1359) also routes here, on its own design ruling that it gets the same
/// truthful completion contract. <c>baton cancel</c>/<c>baton decide</c>/<c>baton resolve</c>/<c>baton
/// supply</c> keep their pre-existing 0/1 contract (<c>Program</c> only routes here for
/// <c>run</c>/<c>dispatch</c>/<c>resume</c>) — those commands were not named in #1356's scope (or, for
/// <c>resolve</c>, #1608's), and folding them in was not asked for.
/// </summary>
public enum RunExitCode
{
    Succeeded = 0,
    Failed = 1,
    ValidationRefused = 2,

    /// <summary>
    /// Either a step's own failure was a binding timeout (<see cref="RunExitCodeResolver.ResolveFailed"/>),
    /// or — #1378 — the wait bound expired first (<see cref="CommandResult.WaitTimedOut"/>, which
    /// carries the mechanism). The room's own ledger state differs between the two: the first is a
    /// genuinely Terminal, Failed room; the second is still Paused/Running — read <c>baton status</c>
    /// to tell them apart.
    /// </summary>
    Timeout = 3,
    Cancelled = 4,

    /// <summary>
    /// #1374 F1: <see cref="Baton.Concurrency.WorkflowLockedException"/> or
    /// <see cref="Baton.Store.FlowJournalHeldException"/> reached <c>Program</c>'s catch —
    /// another Flow instance already holds this room. Distinct from <see cref="ValidationRefused"/>
    /// on purpose: this room may be perfectly healthy (a live pump, or a background sweep's brief
    /// lock), so nothing here is refused and no terminal sentinel is written. The caller's answer is
    /// "retry later", not "this room is done" — check <c>baton status</c> or the room's own ledger
    /// rather than treating this exit code as a terminal outcome.
    /// </summary>
    RoomHeld = 5,
}

/// <summary>
/// Classifies a <see cref="CommandResult"/> into a <see cref="RunExitCode"/>. Pure and side-effect
/// free so every class is covered by direct unit tests against hand-built <see cref="FlowState"/>s,
/// not just the handful an end-to-end shell fixture can cheaply reproduce.
/// <para>
/// #1388 review F9: for <c>baton resume</c>, this still classifies the WHOLE room's <see cref="FlowState"/>,
/// not "did the resumed step itself succeed" — a successful resume of one step in a room where a
/// different step already Failed exits <see cref="RunExitCode.Failed"/>, consistent with #1356's
/// room-scoped table rather than a per-verb verdict. Read the resumed step's own
/// <see cref="StepState.Status"/> (via <c>status --json</c>) for that.
/// </para>
/// </summary>
public static class RunExitCodeResolver
{
    public static RunExitCode Resolve(CommandResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        // #1378: baton run --wait --wait-timeout <minutes> expired before the room reached Terminal.
        // Checked ahead of the state-based classification below because the room itself is still
        // Paused/Running -- nothing in the ledger says "timeout", only this call's own poll loop does.
        // The Terminal guard is defense in depth (#1478 review, F1): RunCommand already refuses to
        // pair WaitTimedOut with a Terminal state, and if a future path ever does, the room's real
        // outcome must win over a wait bookkeeping flag -- a written sentinel and exit 3 would
        // contradict each other.
        if (result.WaitTimedOut && result.State.Status != WorkflowStatus.Terminal)
        {
            return RunExitCode.Timeout;
        }

        var outcome = WorkflowOutcome.Describe(result.State);
        return outcome switch
        {
            WorkflowOutcome.Succeeded => RunExitCode.Succeeded,
            // #1945: exit 0, beside Succeeded. The room finished and its work is on the remote; the
            // dispatch timeout only caught the teardown. Anything else would move the bug rather than
            // fix it — WorkflowOutcome.FinishedDuringTeardown's own remarks list every consumer that
            // owes it this reading.
            WorkflowOutcome.FinishedDuringTeardown => RunExitCode.Succeeded,
            WorkflowOutcome.Cancelled => RunExitCode.Cancelled,
            WorkflowOutcome.Failed => ResolveFailed(result.State.Steps),
            // #1608 / #1623: WorkflowOutcome.Describe returns this whenever a step reads
            // IndeterminateAwaitingResolution true — live and tested for all three of its producers
            // (the classifier's captured-response settle, an engine-run verify failure, a token-budget
            // arrest), each pinned in WorkflowOutcomeAndExitCodeTests through this exact Resolve call.
            // Folded into exit code 1 rather than a distinct code: a caller's `$?`/`%ERRORLEVEL%`
            // branch still can't distinguish this from an ordinary Failed on the exit code alone —
            // "we don't know" is not the same failure as a genuine one, but this table stays four
            // codes wide (#1356's own scope), and `status --json`'s `state` field is what a caller
            // reads to tell them apart.
            WorkflowOutcome.Indeterminate => RunExitCode.Failed,
            // Running or Paused: the pump returned short of Terminal (no --wait, or --wait's poll
            // loop was cancelled -- e.g. Ctrl-C -- before the room settled; a --wait-timeout expiry
            // is handled above, ahead of this switch, and never reaches here). Not one of #1356's
            // four named failure classes, so this stays in the general Failed bucket rather than
            // minting a fifth code — a caller that cares about "still going" reads status --json's
            // `state` field instead. Named explicitly, not folded into a wildcard: `outcome` is a
            // plain `string` (WorkflowOutcome's members are `const string`, not a real enum), so the
            // compiler cannot prove this switch exhaustive over the member set the way it would for
            // an actual enum — a silent wildcard here is exactly what let a hypothetical seventh
            // member fall through unnoticed. The `_` arm below throws instead of guessing, and
            // WorkflowOutcomeAndExitCodeTests' vocabulary-pinning test asserts the whole member set so
            // adding one without touching this switch fails at test time even though nothing catches
            // it at compile time. Deliberately no COUNT here: #1945 added a seventh member and the
            // count in this comment and the message below were both stale the moment it did.
            WorkflowOutcome.Running => RunExitCode.Failed,
            WorkflowOutcome.Paused => RunExitCode.Failed,
            _ => throw new UnreachableException(
                $"WorkflowOutcome.Describe returned '{outcome}', which is not one of the known " +
                "WorkflowOutcome members (Succeeded, FinishedDuringTeardown, Failed, Cancelled, " +
                "Indeterminate, Running, Paused). " +
                "A new member was added without sweeping this switch — also sweep RedispatchCommand's " +
                "parent gate, StatusCommand, FleetStatusTool, glass.html's chipsHtml + render buckets, " +
                "and spec/baton.md §3's table."),
        };
    }

    private static RunExitCode ResolveFailed(IReadOnlyList<StepState> steps)
    {
        var hasHardFailure = steps.Any(step => step.Status == StepStatus.Rejected
            || (step.Status == StepStatus.Failed && !WorkflowOutcome.IsTimeoutFailure(step)));

        return hasHardFailure ? RunExitCode.Failed
            : steps.Any(WorkflowOutcome.IsTimeoutFailure) ? RunExitCode.Timeout
            : RunExitCode.Failed;
    }
}
