namespace Baton.Cli;

/// <summary>
/// Parsed arguments for <c>baton cancel</c> (M12 Phase 2), the on-demand cancellation surface exposed
/// on the CLI.
/// </summary>
/// <param name="RoomDirectoryPath">
/// An already-started room's durable state directory — <c>baton cancel</c> never binds a fresh
/// snapshot the way <c>baton run</c> does ("mutation commands never bind fresh" rule).
/// </param>
/// <param name="ExecutionId">
/// The target execution's <c>ExecutionId</c> to request cancellation for. <c>null</c> (#1495) means
/// "the target lane" — <see cref="CancelCommand"/> resolves it from the room's own projected state:
/// exactly one candidate's latest execution — a <see cref="Baton.Domain.StepStatus.Running"/> step, or
/// (#1607) a quota-parked one — or a refusal naming every candidate when there are zero or more than
/// one (fail closed, no guessing).
/// </param>
/// <param name="BindingsFilePath">
/// The worker-binding config file. Accepted for compatibility and <b>never read</b> since #2073:
/// <c>baton cancel</c> no longer drives a pump of its own, so there is no worker whose binding it
/// would look up. <see cref="CancelOptionsParser"/> still defaults an omitted <c>--bindings</c> to the
/// room's own <c>bindings.json</c> (#1607), so this field is never null and a script that passes the
/// flag keeps working; a missing file no longer refuses (spec/baton.md §2).
/// </param>
/// <param name="WorkflowId">
/// Accepted for compatibility and never read since #2073, for the same reason as
/// <paramref name="BindingsFilePath"/>: nothing here dispatches.
/// </param>
/// <param name="Reason">
/// #2073: the operator's stated reason, recorded verbatim on the intent fact
/// (<see cref="Baton.Domain.RoomEvent.ArrestIntentRecorded"/>) and echoed into the terminal fact's
/// reason. Optional; <c>null</c> when <c>--reason</c> was not given.
/// </param>
public sealed record CancelOptions(
    string RoomDirectoryPath,
    string? ExecutionId,
    string BindingsFilePath,
    string? WorkflowId = null,
    string? Reason = null);
