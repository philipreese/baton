using Baton.Dispatch;
using Baton.Domain;

namespace Baton.Mutation;

/// <summary>
/// Resolves a <see cref="WorkflowStepDefinition.Worker"/> role name (e.g. <c>"architect"</c>) to
/// what the <c>MutationInterface</c> needs to dispatch and classify it, and how. This resolution is
/// left to "configuration external to the workflow" — <c>Baton.Vendors</c> doesn't
/// exist yet (no milestone), so callers supply it directly.
/// </summary>
public abstract record WorkerBinding(WorkerContract Contract, GrantAuditMode GrantAuditMode = GrantAuditMode.Enforced)
{
    /// <summary>
    /// A Core-managed process: the concrete binary to spawn and how long a single
    /// execution may run.
    /// </summary>
    /// <param name="Adapter">
    /// The resolved config entry's <c>WorkerBindingConfigEntry.Adapter</c> name (issue #1567) —
    /// carried this far so <c>MutationInterface</c> can record it onto the
    /// <see cref="Domain.ExecutionRequest"/> it constructs, without re-deriving it from
    /// <see cref="FailureClassifier"/> or re-reading <c>bindings.json</c>. Not itself a vendor
    /// quirk: this is the same plain string <c>Baton.Status</c> already reads out of
    /// <c>bindings.json</c> today, just captured at resolve time instead of at read time.
    /// </param>
    /// <param name="Model">The resolved config entry's <c>WorkerBindingConfigEntry.Model</c>, carried for the same reason.</param>
    /// <param name="VerifyPixiTask">
    /// #1623: this execution's role-default verify task — see
    /// <c>Baton.Vendors.WorkerRole.VerifyPixiTask</c>'s remarks. Lowest-precedence input to
    /// <see cref="VerifyCommandResolver.Resolve"/> (full precedence order on
    /// <c>Baton.Vendors.WorkerBindingConfigEntry.VerifyCommandOverride</c>'s own doc, spec/baton.md §3).
    /// </param>
    /// <param name="VerifyCommandOverride">
    /// #1702: this execution's <c>--verify</c> value, carried the same hop as <paramref name="VerifyPixiTask"/>.
    /// </param>
    /// <param name="VerifiesWorkspace">
    /// #2029: whether this execution's role is graded by the WORKSPACE's own gate suite — see
    /// <c>Baton.Vendors.WorkerRole.VerifiesWorkspace</c>'s remarks for the two sets and why. Read at
    /// the one call site in <c>MutationInterface.DispatchAndRecordOutcomeAsync</c>, which skips
    /// <see cref="VerifyCommandResolver.Resolve"/>'s declaration and role-default arms when false;
    /// <paramref name="VerifyCommandOverride"/> is deliberately outside its reach.
    /// </param>
    /// <param name="TokenBudget">
    /// #1623: the per-execution token ceiling — see <c>Baton.Vendors.WorkerRole.TokenBudget</c>'s
    /// remarks. Null enforces no budget.
    /// </param>
    /// <param name="MaxToolSteps">
    /// #1682: the per-execution tool-step ceiling — see <c>Baton.Vendors.WorkerRole.MaxToolSteps</c>'s
    /// remarks. Null enforces no cap. Independent of <paramref name="TokenBudget"/>: a monitor is
    /// constructed whenever either is set (<c>MutationInterface.DispatchAndRecordOutcomeAsync</c>).
    /// </param>
    /// <param name="BilledRateLimit">
    /// #1691: the billed-rate ceiling — see <c>Baton.Vendors.WorkerRole.BilledRateLimit</c>'s remarks,
    /// including why no role sets one. Null enforces no rate trigger. Independent of the two above: a
    /// monitor is constructed whenever ANY of the three is set
    /// (<c>MutationInterface.DispatchAndRecordOutcomeAsync</c>).
    /// </param>
    /// <param name="IsWorktree">
    /// F4 (#1593 review): whether <see cref="Target"/>'s <c>WorkingDirectory</c> is an ACTUALLY
    /// provisioned worktree (<see cref="Baton.Vendors.WorkerBindingConfigEntry.IsWorktree"/>'s own
    /// stamp), as opposed to null or the operator's own repository (the value a room with no
    /// provisioned worktree carries). <c>Outcomes.OutcomeClassifier</c>'s untouched-workspace
    /// discrimination reads this before passing a path to
    /// <c>Workspaces.WorktreeProvisioner.IsWorkspaceUntouched</c> — a retry decision must never be
    /// handed the operator's own working directory, routinely dirty for reasons that have nothing to
    /// do with the execution.
    /// </param>
    /// <param name="WorktreeBaseSha">
    /// F5/N2 (#1593/#1664 review): <see cref="Baton.Vendors.WorkerBindingConfigEntry.WorktreeBaseSha"/>,
    /// carried the same hop as <see cref="IsWorktree"/> — see that field's own remarks for when it is
    /// null, and <c>WorktreeProvisioner.IsWorkspaceUntouched</c>'s for what it's compared against.
    /// </param>
    /// <param name="DeliversBranch">
    /// #1788: <c>Baton.Vendors.WorkerRole.DeliversBranch</c>, carried the same hop -- see that field's
    /// own remarks for what it gates. Same safe <see langword="false"/> default as <c>ChangesTree</c>
    /// below for any entry not built through <c>Baton.Vendors.RoleDispatch.ToBinding</c>.
    /// </param>
    /// <param name="ExpectPr">
    /// #1788: whether the delivery check's PR half runs — <c>--expect-pr</c>'s resolved value
    /// (<c>Baton.Vendors.RoleDispatch.ToBinding</c>'s <c>expectPrOverride ?? role.DeliversBranch</c>),
    /// already resolved to a definite bool by the time it reaches here. Meaningless when
    /// <see cref="DeliversBranch"/> is false — nothing reads it in that case.
    /// </param>
    public sealed record Process(
        WorkerContract Contract,
        CoreDispatchTarget Target,
        TimeSpan Timeout,
        Outcomes.IFailureClassifier? FailureClassifier = null,
        GrantAuditMode GrantAuditMode = GrantAuditMode.Enforced,
        string? Adapter = null,
        string? Model = null,
        // #1594: same resolved adapter object as FailureClassifier above -- a worker adapter answers
        // both questions, and this is the settle path's seam for the second one (recovering a missing
        // declared output from the worker's own terminal response).
        Outcomes.IWorkerResponseParser? ResponseParser = null,
        string? VerifyPixiTask = null,
        string? VerifyCommandOverride = null,
        long? TokenBudget = null,
        int? MaxToolSteps = null,
        long? BilledRateLimit = null,
        bool IsWorktree = false,
        string? WorktreeBaseSha = null,
        // #1622/#1390: Baton.Vendors.WorkerBindingConfigEntry.ChangesTree, carried the same hop --
        // see that field's own remarks for why it is computed once, upstream, from the catalog role's
        // own grant rather than re-derived here. Outcomes.OutcomeClassifier reads this to decide
        // whether to compute/attach workspaceChanged/hollow onto a Succeeded verdict at all.
        bool ChangesTree = false,
        bool DeliversBranch = false,
        bool ExpectPr = false,
        // #2029: Baton.Vendors.WorkerRole.VerifiesWorkspace, carried the same hop as VerifyPixiTask.
        // Default TRUE, unlike every flag above: "no verify at all" is the direction that silently
        // skips a gate, so a binding built by anything other than RoleDispatch.ToBinding keeps the
        // pre-#2029 behaviour rather than opting itself out.
        bool VerifiesWorkspace = true)
        : WorkerBinding(Contract, GrantAuditMode);

    /// <summary>
    /// A non-process external party — a human, or any other worker tier whose
    /// "execution" is an external event rather than a Core-managed process. Dispatching a step (or
    /// minting a supplementary execution via <c>MutationInterface.RecordSupplementaryExecutionAsync</c>)
    /// bound to this appends <see cref="Domain.FlowEvent.ExecutionRequestAccepted"/> and
    /// pre-allocates the output directory exactly like any other worker, but spawns nothing — no
    /// <c>Target</c>, no <c>Timeout</c>. Completion is detected later, by contract satisfaction
    /// alone (<see cref="Outcomes.NonProcessCompletionDetector"/>), never by a Core exit.
    /// </summary>
    public sealed record NonProcess(WorkerContract Contract, GrantAuditMode GrantAuditMode = GrantAuditMode.Enforced)
        : WorkerBinding(Contract, GrantAuditMode);
}

