using Baton.Domain;

namespace Baton.Cli;

/// <summary>
/// The 0/1 exit-code contract <c>baton cancel</c>/<c>baton decide</c>/<c>baton supply</c> keep —
/// deliberately not <see cref="RunExitCodeResolver"/>'s richer table, which #1356 scoped to
/// <c>run</c>/<c>dispatch</c>/<c>resume</c> and which widening here was never asked for.
/// <para>
/// Pure and side-effect free for the same reason <see cref="RunExitCodeResolver"/> is:
/// <c>MutationExitCodeResolverTests</c> asserts every arm against a hand-built
/// <see cref="CommandResult"/> on any platform, where the end-to-end fixture that produces the queued
/// arm for real (<c>CancelCommandEndToEndTests</c>) needs a Windows sharing violation to reach it at
/// all. Extracted from <c>Program</c>'s inline expression by #1650 F2 so that was possible.
/// <para>
/// What is <em>not</em> covered here: that <c>Program</c> still writes a terminal sentinel on the
/// queued arm while returning 1 (see the arm's own comment for why that pair is intended). Both are
/// asserted separately — the exit code by the tests above, the sentinel by
/// <c>TerminalSentinelEndToEndTests</c> — but no test pins the two together.
/// </para>
/// </para>
/// </summary>
public static class MutationExitCodeResolver
{
    public const int Success = 0;
    public const int Failure = 1;

    public static int Resolve(CommandResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        // #1650 F2: checked ahead of the state classification below, and it is the only arm where
        // this resolver disagrees with the room's own ledger on purpose. CancelCommand's live-pump
        // fall-through (#1495) writes a cancel.request file and re-projects; against an already
        // Terminal, all-succeeded room that projection reads Succeeded, so the state-based arm would
        // return 0 for a command that did nothing but drop a file into a room nothing will ever
        // re-read. A scripted caller branching on $? would read that as "cancelled", which is false.
        //
        // The room genuinely IS Terminal, so Program still writes its terminal sentinel for this
        // arm — the sentinel is a fact about the room, this exit code is a verdict on THIS
        // invocation, and the two are allowed to differ. What exit 1 says here is "your cancel was
        // queued, not applied"; `baton status` remains the authority on the room itself.
        if (result.CancellationQueued)
        {
            return Failure;
        }

        // #2073: the idempotent arm — nothing to arrest, nothing written. Ahead of the state
        // classification for the mirror-image reason the queued arm above is: an already-Failed or
        // Cancelled room reads 1 below, and a re-run cancel against it must not look like a new
        // failure to a scripted caller. CancelCommand's own output says which no-op it was.
        if (result.CancelWasNoOp)
        {
            return Success;
        }

        return result.State.Status == WorkflowStatus.Terminal && result.State.Steps.All(step => step.Status == StepStatus.Succeeded)
            ? Success
            : Failure;
    }
}
