using Baton.Domain;

namespace Baton.Artifacts;

/// <summary>
/// Pre-allocates artifact directories and computes the paths a worker is invoked with.
/// Workers are blind to versioning, lineage, and path topology — they receive only an input set of
/// paths and one output directory, and Flow computes and assigns all of it before dispatch.
/// M7 Phase 6 resolves the open question of how paths are passed: as environment variables,
/// <c>BATON_INPUT_&lt;n&gt;</c> and <c>BATON_OUTPUT_DIR</c>, per the spec's own example.
/// </summary>
public static class ArtifactManager
{
    /// <summary>
    /// The durable filename a step's fully-resolved prompt is written under,
    /// inside its own execution's output directory (issue #292) — <see cref="Dispatch.CoreDispatcher"/>
    /// writes it, before the CLI call, whenever <see cref="Dispatch.CoreDispatchTarget.PromptText"/> is
    /// set; the UI layer's step projector reads it back the same way it reads any other output file,
    /// since it lands in the identical directory <see cref="AllocateOutputDirectory"/> already
    /// allocates. Named as a shared constant, not duplicated as a string literal on both sides, since
    /// both need to agree on it exactly.
    /// </summary>
    public const string PromptFileName = "prompt.txt";

    /// <summary>
    /// The directory under a room directory that every artifact root is built from — the same
    /// shared-constant reasoning as <see cref="PromptFileName"/>: every layer that composes
    /// <c>{roomDirectory}/artifacts</c> must agree on the segment exactly (#773). Tests deliberately
    /// keep restating the literal instead of reading this back: they pin the on-disk contract, which
    /// a change to this constant must break loudly, not follow silently.
    /// </summary>
    public const string ArtifactsDirectoryName = "artifacts";

    /// <summary>
    /// The directory under <c>artifactsRootPath</c> where pruned execution output directories are moved (#973, ADR 0009).
    /// </summary>
    public const string PrunedDirectoryName = "pruned";


    /// <summary>
    /// Creates (if needed) and returns <c>{artifactsRootPath}/execution_{executionId}</c> — the
    /// immutable directory this execution's outputs will be written into. Addressing the
    /// directory by <see cref="ExecutionId"/> rather than a separately tracked sequence number is
    /// what makes every artifact's provenance derivable from the Event Store alone.
    /// </summary>
    public static string AllocateOutputDirectory(string artifactsRootPath, ExecutionId executionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(artifactsRootPath);

        var directory = OutputDirectoryPath(artifactsRootPath, executionId);
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    /// The supplementary artifact a <see cref="Domain.DecisionType.RetryWithRevision"/> or
    /// <see cref="Domain.DecisionType.Supersede"/> decision attaches to its consequence's dispatch:
    /// <paramref name="supplementaryExecutionId"/>'s already-completed output
    /// directory, addressed the same way as any other execution's — no new path convention needed.
    /// </summary>
    public static string ResolveSupplementaryInputPath(string artifactsRootPath, ExecutionId supplementaryExecutionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(artifactsRootPath);

        return OutputDirectoryPath(artifactsRootPath, supplementaryExecutionId);
    }

    /// <summary>
    /// The same addressing <see cref="AllocateOutputDirectory"/> uses, without creating anything —
    /// for a caller that only needs to read an execution's already-allocated output directory (e.g.
    /// <see cref="Outcomes.NonProcessCompletionDetector"/> checking contract satisfaction).
    /// </summary>
    public static string ResolveOutputDirectory(string artifactsRootPath, ExecutionId executionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(artifactsRootPath);

        return OutputDirectoryPath(artifactsRootPath, executionId);
    }

    /// <summary>
    /// Resolves the recoverable, pruned output directory path for <paramref name="executionId"/>:
    /// <c>{artifactsRootPath}/pruned/execution_{executionId}</c> (#973, ADR 0009).
    /// </summary>
    public static string ResolvePrunedOutputDirectory(string artifactsRootPath, ExecutionId executionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(artifactsRootPath);

        return Path.Combine(artifactsRootPath, PrunedDirectoryName, $"execution_{executionId}");
    }


    /// <summary>
    /// Refuses a path the worker process would resolve differently from AER (#668).
    /// </summary>
    /// <remarks>
    /// <see cref="Path.IsPathFullyQualified(string)"/>, not <c>IsPathRooted</c>, and the difference
    /// is not pedantic on Windows. <c>IsPathRooted("C:task2")</c> is <c>true</c> while
    /// <see cref="Path.GetFullPath(string)"/> resolves it against the process's current directory —
    /// a drive-relative path is exactly the defect this guards, and it would have walked straight
    /// through the weaker predicate. <c>"/artifacts/x"</c> is the same shape one step out: rooted,
    /// resolved against the current drive, and fully qualified only on POSIX.
    /// </remarks>
    private static void RefuseRelative(string path, string parameterName)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException(
                $"'{path}' is not fully qualified, and this value is resolved by the worker process " +
                "against its own working directory and drive rather than AER's. Resolve it before " +
                "building the environment (#668).",
                parameterName);
        }
    }

    private static string OutputDirectoryPath(string artifactsRootPath, ExecutionId executionId) =>
        Path.Combine(artifactsRootPath, $"execution_{executionId}");

    /// <summary>
    /// Resolves <paramref name="step"/>'s declared <c>Inputs</c> to concrete file paths, in
    /// declaration order, by locating — among <paramref name="step"/>'s direct
    /// <c>DependsOn</c> — the one dependency whose declared <c>Outputs</c> contains that input's
    /// name, then combining that dependency's most recent successful execution's output directory
    /// with the name itself. Requires every dependency to already have a successful
    /// execution recorded in <paramref name="state"/> — true for any step the Dependency Resolver's
    /// condition 1 has already deemed ready.
    /// </summary>
    public static IReadOnlyList<string> ResolveInputPaths(
        WorkflowStepDefinition step,
        WorkflowDefinitionSnapshot snapshot,
        FlowState state,
        string artifactsRootPath)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrEmpty(artifactsRootPath);

        if (step.Inputs.Count == 0)
        {
            return [];
        }

        var stepDefinitionById = snapshot.Steps.ToDictionary(s => s.StepId);
        var stepStateById = state.Steps.ToDictionary(s => s.StepId);

        var paths = new List<string>(step.Inputs.Count);
        foreach (var inputName in step.Inputs)
        {
            var producer = FindProducer(step, inputName, stepDefinitionById);
            var producerExecutionId = stepStateById[producer.StepId].LatestExecutionId
                ?? throw new ArtifactResolutionException(
                    $"Dependency '{producer.StepId}' has no successful execution yet; cannot resolve " +
                    $"input '{inputName}' for step '{step.StepId}'.");

            paths.Add(Path.Combine(artifactsRootPath, $"execution_{producerExecutionId}", inputName));
        }

        return paths;
    }

    /// <summary>
    /// Builds the AER-computed environment variables a worker is invoked with:
    /// <c>BATON_INPUT_0</c>.. for each resolved input path, in order, <c>BATON_OUTPUT_DIR</c> for
    /// the pre-allocated output directory, <c>BATON_ARTIFACTS_ROOT</c> for <paramref name="artifactsRootPath"/>
    /// itself, <see cref="Dispatch.BuildLockWaitCredit.LogEnvironmentVariable"/> for this execution's
    /// build-lock wait log, <c>BATON_LANE</c> (see remarks) unconditionally, and — only when this
    /// dispatch is a <see cref="Domain.DecisionType.RetryWithRevision"/> or
    /// <see cref="Domain.DecisionType.Supersede"/> consequence carrying a supplement
    /// — <c>BATON_SUPPLEMENTARY_INPUT</c> for <paramref name="supplementaryInputPath"/>. A dedicated
    /// variable, not a declared input name, so it can never collide with a step's own declared
    /// <c>Inputs</c>. Pass-through variables (secrets, vendor settings) are not this method's concern
    /// — they carry no derived value and are resolved separately, immediately before dispatch.
    /// </summary>
    /// <remarks>
    /// <c>BATON_ARTIFACTS_ROOT</c> (M12 Phase 1, #95) exists because a step's own output directory and
    /// every upstream input it reads (<see cref="ResolveInputPaths"/>) are all addressed as sibling
    /// <c>execution_{id}</c> directories under this same root — so one grant covering the root covers
    /// reads and writes alike. This is what lets the Gemini/<c>agy</c> adapter's <c>--add-dir</c>
    /// requirement (spike #21: <c>agy</c> ignores the invoking process's cwd and needs every
    /// directory it touches granted explicitly) be satisfied with a single, vendor-neutral variable
    /// here rather than a per-input, adapter-side <c>dirname</c> derivation, which would have needed
    /// its own answer on Windows for no benefit. Emitted unconditionally, exactly like
    /// <c>BATON_OUTPUT_DIR</c>, since it carries no vendor-specific meaning; an adapter with no use for
    /// it (Claude) simply never references it.
    ///
    /// <c>BATON_LANE</c> (#2129, spec/baton.md C-12) is the marker every dispatched worker is a
    /// "baton lane" rather than a human at a keyboard — every caller of this method is a dispatch,
    /// so it is set unconditionally, present-or-absent rather than carrying a value.
    /// <c>.githooks/pre-push</c> is the sole reader, since a lane's shell inherits its process
    /// environment into its own <c>git push</c>: it swaps the fallback gate task the hook falls
    /// back to when a human pushes for a narrower one (spec/baton.md C-12). A human push never
    /// carries this variable, so the hook's default path is unchanged.
    /// </remarks>
    public static IReadOnlyList<EnvironmentVariable.BatonComputed> BuildEnvironment(
        IReadOnlyList<string> inputPaths,
        string outputDirectory,
        string artifactsRootPath,
        string? supplementaryInputPath = null)
    {
        ArgumentNullException.ThrowIfNull(inputPaths);
        ArgumentException.ThrowIfNullOrEmpty(outputDirectory);
        ArgumentException.ThrowIfNullOrEmpty(artifactsRootPath);

        // #668: these values are read by a DIFFERENT process with a different working directory, so a
        // relative one means two different directories to the two processes that use it. The failure
        // is silent and total — the worker writes its declared output where AER never looks, and the
        // run is reported as `Contract not satisfied` after being paid for in full, indistinguishable
        // from a worker that ignored its instructions.
        //
        // The CLI resolves a room directory at its boundary, which is where the one measured instance
        // came from. This refuses loudly for every other caller, because the cost of being wrong here
        // is a whole frontier-model run and a cause nothing names.
        RefuseRelative(outputDirectory, nameof(outputDirectory));
        RefuseRelative(artifactsRootPath, nameof(artifactsRootPath));
        if (supplementaryInputPath is not null)
        {
            RefuseRelative(supplementaryInputPath, nameof(supplementaryInputPath));
        }

        var variables = new List<EnvironmentVariable.BatonComputed>(inputPaths.Count + 5);
        for (var i = 0; i < inputPaths.Count; i++)
        {
            variables.Add(new EnvironmentVariable.BatonComputed($"BATON_INPUT_{i}", inputPaths[i]));
        }

        variables.Add(new EnvironmentVariable.BatonComputed("BATON_OUTPUT_DIR", outputDirectory));
        variables.Add(new EnvironmentVariable.BatonComputed("BATON_ARTIFACTS_ROOT", artifactsRootPath));

        // #2019: where every build-lock wait this execution's commands pay for is recorded, so the box
        // can be credited for queueing it did not cause. Emitted unconditionally and derived from the
        // output directory rather than declared anywhere, exactly like the two above — the writer is
        // tools/buildlock.py, reading this variable out of the worker's inherited environment, and the
        // reader is Dispatch.BuildLockWaitCredit, which owns what the file means.
        variables.Add(new EnvironmentVariable.BatonComputed(
            Dispatch.BuildLockWaitCredit.LogEnvironmentVariable,
            Path.Combine(outputDirectory, Dispatch.BuildLockWaitCredit.LogFileName)));

        // #2129: present-or-absent, no value carries meaning — see the remarks above for the sole
        // reader (.githooks/pre-push) and what it does with it.
        variables.Add(new EnvironmentVariable.BatonComputed("BATON_LANE", "1"));

        if (supplementaryInputPath is not null)
        {
            variables.Add(new EnvironmentVariable.BatonComputed("BATON_SUPPLEMENTARY_INPUT", supplementaryInputPath));
        }

        return variables;
    }

    private static WorkflowStepDefinition FindProducer(
        WorkflowStepDefinition step,
        string inputName,
        Dictionary<StepId, WorkflowStepDefinition> stepDefinitionById)
    {
        foreach (var dependencyStepId in step.DependsOn)
        {
            if (stepDefinitionById[dependencyStepId].Outputs.Contains(inputName))
            {
                return stepDefinitionById[dependencyStepId];
            }
        }

        throw new ArtifactResolutionException(
            $"No direct dependency of step '{step.StepId}' declares output '{inputName}'.");
    }
}
