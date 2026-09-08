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
/// so the shared exit-code path is unchanged for <c>decide</c> and <c>supply</c>. It exists because
/// <see cref="State"/> alone cannot tell this outcome from an applied one — the fall-through
/// re-projects the room and returns whatever it finds. The exit code it maps to, and the direction
/// it corrects, live in one place: the "Exit code, stated once (#2103)" sentence under spec/baton.md
/// §2; <see cref="MutationExitCodeResolver"/> is what reads it.
/// </param>
/// <param name="CancelWasNoOp">
/// #2073: true when <see cref="CancelCommand"/> found nothing to arrest — the room was already
/// Terminal, or the named execution had already settled — and so wrote nothing at all, not even the
/// intent fact. Only <see cref="CancelCommand"/> sets it; <see cref="MutationExitCodeResolver"/> reads
/// it, and the same spec sentence states its exit code and the direction it corrects.
/// </param>
/// <param name="CancelApplied">
/// #2103: true when <see cref="CancelCommand"/> did what it was asked — the intent fact is written and
/// the target left <c>ArrestableExecutions</c> before this call returned. Which of
/// <see cref="CancelCommand"/>'s returns set it, its exit code, and the direction it corrects are in the
/// same spec sentence. Mutually exclusive with <paramref name="CancellationQueued"/> and
/// <paramref name="CancelWasNoOp"/> by construction. Only <see cref="CancelCommand"/> sets it;
/// <see cref="MutationExitCodeResolver"/> reads it, so <c>decide</c>/<c>supply</c>/<c>resolve</c> keep
/// the unchanged state-based arm.
/// </param>
public sealed record CommandResult(
    FlowState State,
    WorkflowDefinitionSnapshot Snapshot,
    bool ResumedFromSnapshot = false,
    string? RoomDirectoryPath = null,
    IReadOnlyList<WorktreeTeardownResult>? WorktreeTeardowns = null,
    bool WaitTimedOut = false,
    bool CancellationQueued = false,
    bool CancelWasNoOp = false,
    bool CancelApplied = false)
{
    /// <summary>Defaults to empty rather than <c>null</c> for callers that omit the argument.</summary>
    public IReadOnlyList<WorktreeTeardownResult> WorktreeTeardowns { get; init; } = WorktreeTeardowns ?? [];
}

