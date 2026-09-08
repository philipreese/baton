using Baton.Domain;

namespace Baton.Vendors;

/// <summary>
/// Composes a <see cref="WorkflowTemplate"/> (an ordered list of role-phases, decision 0047) into the
/// <see cref="WorkflowDefinition"/> + bindings the engine runs — the multi-step analogue of
/// <see cref="RoleDispatch.Materialize"/>, which handles a single role. Each phase names a role
/// (resolved through <see cref="WorkerRoleCatalog"/>) and never re-declares that role's grant, outputs,
/// or timeout; the phases lay out as a Pipeline with sequential <c>DependsOn</c> edges.
/// </summary>
/// <remarks>
/// <para>
/// <b>Input flow is single-blocker (decision 0025).</b> A step has exactly one blocker whose output
/// flows in as its input — by default the previous phase. There is no composition syntax; a step never
/// reads two upstream outputs. So each node's <see cref="WorkflowStepDefinition.Inputs"/> is its
/// blocker's <see cref="WorkflowStepDefinition.Outputs"/> and its <c>DependsOn</c> is that one blocker.
/// </para>
/// <para>
/// <b>The capture step is spliced into the chain, not added as a second input.</b> A phase that
/// declares the closed symbolic input <see cref="DiffOfWorkSoFarInput"/> (decision 0047 §3) gets a
/// capture node inserted immediately before it: <c>… → prior-phase → capture → this-phase</c>. The
/// capture becomes this phase's single blocker, so the diff flows in implicitly and 0025's one-blocker
/// rule holds. This composer only emits the capture <em>step</em>; the engine-run <c>capture</c> adapter
/// that actually runs <c>git diff</c> is <see cref="CaptureWorkerAdapter"/>, and the run entrypoint
/// (<c>baton dispatch</c>) injects the base ref it diffs against — so a template declaring the diff input
/// runs end to end.
/// </para>
/// <para>
/// <b>Keys are phase names, not role ids.</b> A template may name the same role in two phases, so every
/// step id, worker, and binding key is the phase name (<see cref="WorkflowTemplatePhase.Name"/>), which
/// the catalog already enforces unique within a template.
/// </para>
/// </remarks>
public static class WorkflowTemplateComposer
{
    /// <summary>The one closed symbolic input this slice understands (mirrors the catalog's closed set).</summary>
    public const string DiffOfWorkSoFarInput = "diff-of-work-so-far";

    /// <summary>The adapter name the spliced capture step runs on — the engine-run <see cref="CaptureWorkerAdapter"/>.</summary>
    public const string CaptureAdapter = "capture";

    /// <summary>The artifact a capture step produces and its consuming phase reads.</summary>
    public const string CaptureOutputName = "diff-of-work-so-far.diff";

    private const int DefaultRetryAttempts = 3;

    /// <summary>
    /// Turn a template into the definition + bindings the engine runs. Throws
    /// <see cref="KeyNotFoundException"/> (via <see cref="WorkerRoleCatalog.For"/>) if a phase names a
    /// role that is not in the catalog — the same failure the catalog raises at load, surfaced again
    /// here because roles resolve fresh at compose time.
    /// </summary>
    /// <param name="template">The resolved template (see <see cref="WorkflowTemplateCatalog.For"/>).</param>
    /// <param name="adapterOverride">
    /// A vendor adapter to run every phase's role on instead of its tier default — the <c>--adapter</c>
    /// escape hatch, applied uniformly, exactly as <see cref="RoleDispatch"/> applies it to one role.
    /// The spliced capture step ignores it: capture is engine-run, not a vendor.
    /// </param>
    /// <param name="workingDirectory">
    /// The workspace each phase's role runs in and may read — pinned onto every phase binding so a
    /// role dispatched as a template phase gets the same repo read access #1083 gave a role dispatched
    /// on its own. The spliced capture step pins its own working directory separately (it diffs the tree).
    /// </param>
    /// <param name="attachDefaultSkills">
    /// #2110: forwarded to every phase's <see cref="RoleDispatch.ToBinding"/> — a phase is a role, so
    /// its <see cref="WorkerRole.DefaultSkills"/> attach here as on a direct dispatch, and
    /// <c>--no-default-skills</c> opts every phase out at once.
    /// </param>
    public static (WorkflowDefinition Definition, IReadOnlyDictionary<string, WorkerBindingConfigEntry> Bindings) Materialize(
        WorkflowTemplate template, string? adapterOverride = null, string? workingDirectory = null,
        bool attachDefaultSkills = true)
    {
        ArgumentNullException.ThrowIfNull(template);

        var steps = new List<WorkflowStepDefinition>();
        var bindings = new Dictionary<string, WorkerBindingConfigEntry>(StringComparer.Ordinal);

        // A capture step's id is the declaring phase's name + "-capture". Guard against a phase literally
        // named that: without this the capture binding would silently overwrite the real phase's binding
        // in the dictionary below. The engine's validator would still reject the duplicate step id, but
        // catching it here names the actual cause ("phase X collides with Y's generated capture id").
        var phaseNames = template.Phases.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        // The single node whose output flows into the next one (0025). Null/[] before the first node.
        StepId? blockerId = null;
        IReadOnlyList<string> blockerOutputs = [];

        foreach (var phase in template.Phases)
        {
            var role = WorkerRoleCatalog.For(phase.RoleId);

            // Capture splice: a phase declaring the diff input gets a capture node before it, which
            // becomes this phase's blocker so the diff is its single implicit input.
            if (phase.Inputs.Contains(DiffOfWorkSoFarInput, StringComparer.Ordinal))
            {
                var captureId = new StepId($"{phase.Name}-capture");
                if (phaseNames.Contains(captureId.Value))
                {
                    throw new InvalidOperationException(
                        $"Phase '{phase.Name}' declares '{DiffOfWorkSoFarInput}', but its generated capture " +
                        $"step id '{captureId.Value}' collides with a phase named '{captureId.Value}'. Rename one.");
                }

                steps.Add(new WorkflowStepDefinition(
                    StepId: captureId,
                    Worker: captureId.Value,
                    Inputs: [],                                        // runs git against the workspace; reads no artifact
                    Outputs: [CaptureOutputName],
                    DependsOn: blockerId is { } priorForCapture ? [priorForCapture] : [],
                    RetryPolicy: new RetryPolicy(DefaultRetryAttempts),
                    PausePoint: null));
                bindings[captureId.Value] = CaptureBinding(captureId.Value);

                blockerId = captureId;
                blockerOutputs = [CaptureOutputName];
            }

            var stepId = new StepId(phase.Name);
            var outputs = role.Outputs.Select(o => o.Name).ToList();
            steps.Add(new WorkflowStepDefinition(
                StepId: stepId,
                Worker: phase.Name,
                Inputs: blockerOutputs,                                // the single blocker's output flows in (0025)
                Outputs: outputs,
                DependsOn: blockerId is { } blocker ? [blocker] : [],
                RetryPolicy: new RetryPolicy(DefaultRetryAttempts),
                // AskFirst is 0025's gate toggle. An empty SupersedeTargets is a plain approval gate
                // (Resume/Reject/RetryWithRevision); the template model carries no supersede targets, so
                // empty is the faithful mapping until it does.
                PausePoint: phase.AskFirst ? new PausePoint([]) : null));
            // requiredInputs mirrors the step's Inputs above — why the mirroring matters (and its
            // ordering rule) is on RoleDispatch.ToBinding's requiredInputs doc (#1147).
            // autoProvisionWorktree: false (R5, #1354/#1380) — a composed template's audited phases
            // refuse at bind time exactly as before worktree auto-provisioning existed. Widening it to
            // every phase of a multi-step template was out of scope: it would hand each audited phase a
            // fresh HEAD copy blind to what an earlier phase in the SAME run just wrote (e.g. an
            // implement-then-review template), which is worse than the loud bind-time refusal it would
            // replace.
            bindings[phase.Name] = RoleDispatch.ToBinding(
                role, phase.Instruction, adapterOverride, workerName: phase.Name, workingDirectory: workingDirectory,
                requiredInputs: blockerOutputs, autoProvisionWorktree: false,
                attachDefaultSkills: attachDefaultSkills);

            blockerId = stepId;
            blockerOutputs = outputs;
        }

        var definition = new WorkflowDefinition(
            WorkflowTemplateId: new WorkflowTemplateId(template.Id),
            WorkflowTemplateVersion: 1,
            Steps: steps);
        return (definition, bindings);
    }

    // The capture step's binding: the engine-run capture adapter, a contract that produces just the
    // diff artifact, and NO vendor permission grant — it is deterministic engine machinery (git diff
    // with engine-controlled args), not a worker, so it carries no grant to translate (0047 §4).
    // CaptureWorkerAdapter (registered under CaptureAdapter) fulfils this binding; PromptTemplate is
    // empty here and the run entrypoint injects the base ref before dispatch.
    private static WorkerBindingConfigEntry CaptureBinding(string workerName) =>
        new(
            Adapter: CaptureAdapter,
            Contract: new WorkerContract(
                WorkerName: workerName,
                RequiredInputs: [],
                ProducedOutputs: [new ProducedOutput(CaptureOutputName)],
                OptionalMetadata: []),
            PromptTemplate: string.Empty,
            Timeout: TimeSpan.FromMinutes(2),
            PermissionGrant: new PermissionGrant());
}
