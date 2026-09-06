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
/// <c>baton supply</c> (M12 Phase 3): the CLI surface for the supplementary artifact — the one
/// mutation-interface entry point (<see cref="MutationInterface.RecordSupplementaryExecutionAsync"/>)
/// no CLI command reached before this phase. Per M11's decision of record that worker-binding
/// config entries only ever resolve to <see cref="Baton.Mutation.WorkerBinding.Process"/>, the
/// <see cref="Baton.Mutation.WorkerBinding.NonProcess"/> binding this command dispatches under is
/// constructed directly here, from <see cref="SupplyOptions.OutputName"/> — not looked up in the
/// bindings file. Minting alone does not drive the pump (nothing about minting changes
/// readiness), so this command populates the assigned output immediately from
/// <see cref="SupplyOptions.SourceFilePath"/> and then runs one settling pump
/// (<see cref="MutationInterface.StartWorkflowAsync"/>) itself — the same two-call sequence
/// <c>PauseDecisionSupersedeHumanEndToEndTests</c> exercises directly against
/// <c>MutationInterface</c> — so the printed <see cref="ExecutionId"/> is already
/// <see cref="FlowEvent.ExecutionSucceeded"/> by the time this command returns, ready to hand
/// straight to <c>baton decide --supplementary</c>.
/// </summary>
public static class SupplyCommand
{
    private const string ArtifactsDirectoryName = ArtifactManager.ArtifactsDirectoryName;

    /// <exception cref="SnapshotLoadException">
    /// record-once-ok: #443 src/Baton.Cli/DecideCommand.cs
    /// The room directory has no persisted snapshot yet (never started via <c>baton run</c>), or its
    /// persisted snapshot is malformed.
    /// </exception>
    /// <exception cref="WorkerBindingConfigException">The worker-binding config is malformed.</exception>
    /// <exception cref="UnknownWorkerAdapterException">
    /// An adapter the bindings file names is missing from <paramref name="adapters"/> — raised
    /// only when the resume pump first looks that worker up (<see cref="WorkerBindingResolver.ResolveLazily"/>, #662).
    /// </exception>
    /// <exception cref="CliArgumentException"><see cref="SupplyOptions.SourceFilePath"/> does not exist.</exception>
    /// <exception cref="Baton.Concurrency.WorkflowLockedException">
    /// record-once-ok: #443 src/Baton.Cli/RunCommand.cs
    /// Another Flow instance already holds this room directory's lock. This command's own mutation
    /// guard is the fail-fast kind.
    /// </exception>
    /// <exception cref="Baton.Store.FlowJournalHeldException">
    /// #816's journal-held refusal. #1650 F3 established that this, and not the lock refusal above,
    /// is what a supply against a live pump gets; <see cref="DecideCommand"/> carries the reasoning
    /// for the whole four-command population.
    /// </exception>
    public static async Task<SupplyResult> ExecuteAsync(
        SupplyOptions options,
        IReadOnlyDictionary<string, IWorkerAdapter> adapters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(adapters);

        if (!File.Exists(options.SourceFilePath))
        {
            // CliArgumentException (an BatonFlowException), not a raw FileNotFoundException: the latter is
            // not caught by Program's typed boundary and would escape as a crash — the same class fixed
            // across the file loaders. Mirrors DispatchCommand's "Spec file 'X' does not exist" refusal.
            throw new CliArgumentException($"Source file '{options.SourceFilePath}' does not exist.");
        }

        var snapshotPath = Path.Combine(options.RoomDirectoryPath, BatonPaths.SnapshotFileName);
        var logPath = Path.Combine(options.RoomDirectoryPath, BatonPaths.FlowLogFileName);
        var artifactsRootPath = Path.Combine(options.RoomDirectoryPath, ArtifactsDirectoryName);

        if (!File.Exists(snapshotPath))
        {
            throw new SnapshotLoadException(
                $"Room directory '{options.RoomDirectoryPath}' has no bound snapshot — 'baton supply' " +
                "targets a room 'baton run' has already started, and never binds one fresh.");
        }

        var snapshot = await SnapshotBinder.LoadFromFileAsync(snapshotPath, cancellationToken).ConfigureAwait(false);

        var bindingConfig = await WorkerBindingConfigParser.LoadFromFileAsync(options.BindingsFilePath, cancellationToken)
            .ConfigureAwait(false);
        var (provisionedConfig, provisionedWorktrees) =
            WorktreeWorkspaces.Provision(bindingConfig, options.RoomDirectoryPath);
        var profiles = await BatonProfileStore.LoadAsync(BatonProfileStore.DefaultPath, cancellationToken).ConfigureAwait(false);
        // Lazy (#662, same reasoning as CancelCommand): materializing this into a plain Dictionary to
        // add the NonProcess override below would enumerate — and so eagerly resolve and refuse —
        // every other entry in the file, reintroducing the defect for a bindings file naming a worker
        // 'baton supply' never touches.
        var workerBindings = WorkerBindingResolver.ResolveLazily(
            provisionedConfig, adapters, profiles, Path.GetDirectoryName(options.BindingsFilePath));

        var contract = new WorkerContract(options.Worker, RequiredInputs: [], [new ProducedOutput(options.OutputName)], OptionalMetadata: []);
        workerBindings = new WorkerBindingOverride(workerBindings, options.Worker, new WorkerBinding.NonProcess(contract));

        var workflowId = new WorkflowId(options.WorkflowId ?? snapshot.WorkflowTemplateId.Value);

        await using var writer = new FlowEventLogWriter(logPath);
        var reader = new FlowEventLogReader(logPath);
        var dispatcher = new CoreDispatcher(writer, writer);

        var (_, executionId) = await MutationInterface.RecordSupplementaryExecutionAsync(
                workflowId, options.RoomDirectoryPath, snapshot, workerBindings, artifactsRootPath,
                options.Worker, inputs: [], reader, writer, cancellationToken)
            .ConfigureAwait(false);

        var outputDirectory = ArtifactManager.ResolveOutputDirectory(artifactsRootPath, executionId);
        File.Copy(options.SourceFilePath, Path.Combine(outputDirectory, options.OutputName), overwrite: true);

        var settledState = await MutationInterface.StartWorkflowAsync(
                workflowId, options.RoomDirectoryPath, snapshot, workerBindings, artifactsRootPath,
                reader, writer, dispatcher, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var worktreeTeardowns = WorktreeProvisioner.TeardownIfTerminal(settledState.Status, provisionedWorktrees);

        var command = new CommandResult(settledState, snapshot, RoomDirectoryPath: options.RoomDirectoryPath, WorktreeTeardowns: worktreeTeardowns);

        // #1911: the same removal arm, over this verb's own supplementary execution and nothing else.
        // It hangs off no step, so a walk over State.Steps would miss the very file just copied in —
        // and would reach verdicts this call never wrote, including rows an earlier
        // `dispatch --verify-cmd` genuinely measured (#1911 review, medium 1).
        await VerdictInstrumentStamp
            .ApplyAsync(options.RoomDirectoryPath, command, verifyStep: null, onlyExecutionId: executionId)
            .ConfigureAwait(false);

        return new SupplyResult(executionId, command);
    }
}

/// <param name="ExecutionId">The minted supplementary execution's id — pass to <c>baton decide --supplementary</c>.</param>
/// <param name="Command">The settling pump's resulting state, reported the same way as any other command.</param>
public sealed record SupplyResult(ExecutionId ExecutionId, CommandResult Command);
