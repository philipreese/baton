using Baton.Vendors;
using Baton.Artifacts;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Mutation;
using Baton.Status;
using Baton.Store;
using Baton.Templates;
using Baton.Workspaces;

namespace Baton.Cli;

/// <summary>
/// <c>baton decide</c>: exposes <see cref="MutationInterface.RecordDecisionAsync"/> on
/// the CLI. The vocabulary is exactly the closed set
/// (<see cref="Domain.DecisionType"/>); every validity rule (which options a given type requires or
/// forbids) stays <c>ExternalDecisionValidator</c>'s, never re-implemented here. Like
/// <see cref="CancelCommand"/>, this never binds a fresh snapshot, and recording a decision resumes
/// the workflow — <c>ExternalDecisionRecorded</c> + <c>WorkflowResumed</c>, then the pump to a fixed
/// point — so this command blocks and reports exactly like <c>baton run</c>.
/// </summary>
public static class DecideCommand
{
    private const string ArtifactsDirectoryName = ArtifactManager.ArtifactsDirectoryName;

    /// <exception cref="SnapshotLoadException">
    /// The room directory has no persisted snapshot yet (never started via <c>baton run</c>), or its
    /// persisted snapshot is malformed.
    /// </exception>
    /// <exception cref="WorkerBindingConfigException">The worker-binding config is malformed.</exception>
    /// <exception cref="UnknownWorkerAdapterException">
    /// The worker-binding config names an adapter not present in <paramref name="adapters"/>.
    /// </exception>
    /// <exception cref="InvalidExternalDecisionException">The decision violates one of the closed set's rules.</exception>
    /// <exception cref="Baton.Concurrency.WorkflowLockedException">
    /// record-once-ok: #443 src/Baton.Cli/RunCommand.cs
    /// Another Flow instance still held this room directory's lock after
    /// <see cref="Baton.Concurrency.RoutineHoldBudget"/> elapsed. #1650 F3: no longer the refusal a
    /// live pump produces in the common case — see <see cref="Baton.Store.FlowJournalHeldException"/>
    /// below for what is.
    /// </exception>
    /// <exception cref="Baton.Store.FlowJournalHeldException">
    /// See that type's own docs for the mechanism (#816). #1650 F3: <b>this</b>, not
    /// <see cref="Baton.Concurrency.WorkflowLockedException"/>, is what a live <c>baton run</c> pump
    /// normally refuses this command with. #1646 stopped <see cref="WorktreeWorkspaces.Provision"/>
    /// from taking <c>flow.lock</c> for a bindings file that declares no worktree — the common shape —
    /// so the first resource this command now contends is the pump's own long-lived <c>flow.jsonl</c>
    /// append handle, which the pump releases strictly <em>after</em> the lock. Both opens are bounded
    /// (<see cref="Baton.Concurrency.RoutineHoldBudget"/>), so this is reached only by a hold that
    /// outlasts a pump's exit tail.
    /// </exception>
    /// <param name="inFlightExecutions">
    /// M15 Phase 4's (issue #140) additive caller-retained delivery point — see
    /// <see cref="RunCommand.ExecuteAsync"/>'s own remarks; forwarded, unchanged, to
    /// <see cref="MutationInterface.RecordDecisionAsync"/>.
    /// </param>
    /// <param name="onWorkerStdoutLine">
    /// M24 Phase 1's live in-turn streaming — forwarded verbatim to <see cref="WorkerBindingResolver.Resolve"/>.
    /// Null for the real <c>baton decide</c> CLI entry point; only <c>Baton.Daemon</c>'s in-process
    /// session-turn path supplies one (see <c>Program.ExecuteSessionTurnAsync</c>).
    /// </param>
    public static async Task<CommandResult> ExecuteAsync(
        DecideOptions options,
        IReadOnlyDictionary<string, IWorkerAdapter> adapters,
        InFlightExecutionRegistry? inFlightExecutions = null,
        CancellationToken cancellationToken = default,
        Action<string, string>? onWorkerStdoutLine = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(adapters);

        var snapshotPath = Path.Combine(options.RoomDirectoryPath, BatonPaths.SnapshotFileName);
        var logPath = Path.Combine(options.RoomDirectoryPath, BatonPaths.FlowLogFileName);
        var artifactsRootPath = Path.Combine(options.RoomDirectoryPath, ArtifactsDirectoryName);

        if (!File.Exists(snapshotPath))
        {
            throw new SnapshotLoadException(
                $"Room directory '{options.RoomDirectoryPath}' has no bound snapshot — 'baton decide' " +
                "targets a room 'baton run' has already started, and never binds one fresh.");
        }

        var snapshot = await SnapshotBinder.LoadFromFileAsync(snapshotPath, cancellationToken).ConfigureAwait(false);

        var bindingConfig = await WorkerBindingConfigParser.LoadFromFileAsync(options.BindingsFilePath, cancellationToken)
            .ConfigureAwait(false);

        // A decision can make a downstream step ready and its settling pump dispatches that fresh
        // worker execution. Admission must precede provisioning and the decision journal mutation.
        WorkerBindingResolver.RefuseConductorOnlyWorkerModels(bindingConfig);

        var (provisionedConfig, provisionedWorktrees) =
            WorktreeWorkspaces.Provision(bindingConfig, options.RoomDirectoryPath);
        var profiles = await BatonProfileStore.LoadAsync(BatonProfileStore.DefaultPath, cancellationToken).ConfigureAwait(false);
        var workerBindings = WorkerBindingResolver.Resolve(
            provisionedConfig, adapters, profiles, Path.GetDirectoryName(options.BindingsFilePath), onWorkerStdoutLine);

        var workflowId = new WorkflowId(options.WorkflowId ?? snapshot.WorkflowTemplateId.Value);
        var referencedExecutionId = new ExecutionId(options.ExecutionId);
        var supplementaryExecutionId = options.SupplementaryExecutionId is { } id ? new ExecutionId(id) : (ExecutionId?)null;

        await using var writer = new FlowEventLogWriter(logPath);
        var reader = new FlowEventLogReader(logPath);
        var dispatcher = new CoreDispatcher(writer, writer);

        var state = await MutationInterface.RecordDecisionAsync(
                workflowId,
                options.RoomDirectoryPath,
                snapshot,
                workerBindings,
                artifactsRootPath,
                reader,
                writer,
                dispatcher,
                referencedExecutionId,
                options.DecisionType,
                options.TargetStepId,
                supplementaryExecutionId,
                inFlightExecutions,
                cancellationToken,
                settleOnVendorExhaustion: options.SettleOnVendorExhaustion)
            .ConfigureAwait(false);

        var worktreeTeardowns = WorktreeProvisioner.TeardownIfTerminal(state.Status, provisionedWorktrees);

        return new CommandResult(state, snapshot, RoomDirectoryPath: options.RoomDirectoryPath, WorktreeTeardowns: worktreeTeardowns);
    }
}
