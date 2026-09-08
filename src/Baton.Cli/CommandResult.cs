using Baton.Domain;
using Baton.Workspaces;

namespace Baton.Cli;

/// <summary>
/// What every mutation command (<c>baton run</c>, <c>baton cancel</c>, <c>baton decide</c>) returns:
/// the pumped-to-fixed-point <see cref="FlowState"/> alongside the bound
/// <see cref="WorkflowDefinitionSnapshot"/> it was projected against — the snapshot is what lets a
/// caller's reporting layer resolve a paused step's declared <c>PausePoint.SupersedeTargets</c>,
/// which <see cref="FlowState"/> alone does not carry.
/// </summary>
/// <param name="ResumedFromSnapshot">
/// Whether this call ran the room directory's already-bound snapshot rather than binding the
/// workflow file it was given (#628). Only <c>baton run</c> can bind one at all, so every other
/// command leaves this at its default — they resume by definition, and saying so per-command would
/// be noise rather than news.
/// </param>
/// <param name="WorktreeTeardowns">
/// Worktree teardowns worth surfacing from a Terminal run (#669) — a tree kept because it carried
/// uncommitted changes, or a removal that could not complete. A clean removal is not listed; empty is
/// the common case.
/// </param>
/// <param name="WaitTimedOut">
/// #1378: true only when <c>baton run --wait --wait-timeout &lt;minutes&gt;</c>'s poll loop stopped
/// because that bound elapsed, not because the room reached Terminal or the caller cancelled. Only
/// <see cref="RunCommand"/> ever sets this; every other command leaves it at its default, since only
/// <c>run</c>'s own <c>--wait</c> has a poll loop for a timeout to bound. <see cref="RunExitCodeResolver"/>
/// reads it to report <see cref="RunExitCode.Timeout"/> ahead of the state-based classification —
/// the room itself is still Paused, not "timed out" by anything the ledger recorded.
/// </param>
/// <param name="CancellationQueued">
/// #1650 F2: true when <see cref="CancelCommand"/> took its live-pump fall-through — the cancellation
/// was written to a <c>cancel.request</c> file for some other pump to consume, not applied by this
/// call. Only <see cref="CancelCommand"/> ever sets it; every other command leaves it at its default,
/// so the shared exit-code path is unchanged for <c>decide</c> and <c>supply</c>.
/// <para>
/// It exists because <see cref="State"/> alone cannot tell the two outcomes apart: the fall-through
/// re-projects the room and returns whatever it finds, so a cancel that did nothing but drop a request
/// file into an already-Terminal, all-succeeded room is indistinguishable from one that carried the
/// room there itself. <see cref="MutationExitCodeResolver"/> is what reads it.
/// </para>
/// </param>
/// <param name="CancelWasNoOp">
/// #2073: true when <see cref="CancelCommand"/> found nothing to arrest — the room was already
/// Terminal, or the named execution had already settled — and so wrote nothing at all, not even the
/// intent fact. The idempotency the verb promises ("says so, exits 0") needs its own flag for the same
/// reason <paramref name="CancellationQueued"/> does: the state alone reads exit 1 for an already-Failed
/// room, which would make re-running a cancel look like a fresh failure. Only <see cref="CancelCommand"/>
/// sets it; <see cref="MutationExitCodeResolver"/> reads it.
/// </param>
public sealed record CommandResult(
    FlowState State,
    WorkflowDefinitionSnapshot Snapshot,
    bool ResumedFromSnapshot = false,
    string? RoomDirectoryPath = null,
    IReadOnlyList<WorktreeTeardownResult>? WorktreeTeardowns = null,
    bool WaitTimedOut = false,
    bool CancellationQueued = false,
    bool CancelWasNoOp = false)
{
    /// <summary>Defaults to empty rather than <c>null</c> for callers that omit the argument.</summary>
    public IReadOnlyList<WorktreeTeardownResult> WorktreeTeardowns { get; init; } = WorktreeTeardowns ?? [];
}

