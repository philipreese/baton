using Baton.Domain;

namespace Baton.Vendors;

/// <summary>
/// #1166: the single choke point both <see cref="ClaudeWorkerAdapter"/> and <see cref="AgyWorkerAdapter"/>
/// call at the top of <c>Resolve</c> — one shared read-store/refuse/cap sequence rather than two copies
/// that drift. Consults <see cref="ProjectCeilingStore"/> and returns an invocation whose
/// <see cref="WorkerInvocation.PermissionGrant"/> is capped to decision 0004's effective grant = role
/// grant ∩ project ceiling.
/// </summary>
internal static class ProjectCeilingGate
{
    /// <param name="invocation">The invocation as resolved so far — read, never mutated (records).</param>
    /// <param name="contract">
    /// The paired <see cref="WorkerContract"/> — its <see cref="WorkerContract.WorkerName"/> names the
    /// exceptions below, and its <see cref="WorkerContract.ProducedOutputs"/> feeds the #629 recheck
    /// (review finding B).
    /// </param>
    /// <param name="withheldWritesReachTheOutbox">
    /// The calling adapter's own <see cref="IWorkerAdapter.WithheldWritesReachTheOutbox"/> — passed in
    /// rather than re-derived, the same "ask the adapter" split
    /// <see cref="WorkerBindingResolver"/>'s own pre-existing check already uses.
    /// </param>
    /// <exception cref="ProjectNotTrustedException">
    /// The ceiling lookup key (<see cref="WorkerInvocation.WorktreeSourceRepository"/> when set,
    /// otherwise <see cref="WorkerInvocation.WorkingDirectory"/>) is set and carries no recorded
    /// <see cref="ProjectCeilingStore"/> entry — or carries a tombstone (#2121), which refuses the same
    /// way with the revocation named.
    /// </exception>
    /// <exception cref="ProjectCeilingRequiresStructuredGrantException">
    /// The recorded ceiling withholds a category but the invocation has no structured
    /// <see cref="PermissionGrant"/> to cap.
    /// </exception>
    /// <exception cref="IncoherentPermissionGrantException">
    /// Capping the role's grant against the ceiling produces the #529 shape a granted, unscoped shell
    /// reaches every category anyway — re-checked here because a coherent role grant can become
    /// incoherent once narrowed (<see cref="PermissionGrant.CategoriesDefeatedByTheShell(bool, IReadOnlySet{string})"/>'s
    /// own remarks are the canonical statement of the rule; this is the same predicate, not a
    /// restatement). #1784: called with the ceiling's own closed categories as
    /// <c>strictCategories</c> — spec/baton.md §9's operator ruling is canonical for why, not
    /// restated here.
    /// </exception>
    /// <exception cref="UnsatisfiableOutputContractException">
    /// Review finding B: capping removed <see cref="PermissionGrant.WriteFiles"/> from a grant whose
    /// contract declares outputs, on an adapter that cannot reach the outbox with writes withheld.
    /// <see cref="WorkerBindingResolver"/>'s own pre-existing version of this check runs on the
    /// UNCAPPED grant, before <c>Resolve</c> is ever called, so it cannot see a contract this gate's
    /// own capping just made unsatisfiable — recreating the exact #629 pay-then-fail hazard one level
    /// down. Re-run here for the same reason the shell-coherence check above is re-run here.
    /// </exception>
    public static WorkerInvocation Apply(
        WorkerInvocation invocation, WorkerContract contract, bool withheldWritesReachTheOutbox)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(contract);

        // Review finding A -- see WorkerInvocation.WorktreeSourceRepository's own doc for why a
        // provisioned worktree overrides the lookup key here; a plain WorkingDirectory dispatch has no
        // such override and uses it directly.
        var ceilingKey = invocation.WorktreeSourceRepository ?? invocation.WorkingDirectory;
        if (string.IsNullOrWhiteSpace(ceilingKey))
        {
            // No project to hold a ceiling against — a plain chat turn or an adapter call with no
            // WorkingDirectory (docs/agents doc: ExecuteSessionTurnAsync never sets one unless the
            // session is attached to a codebase). Decision 0004's ceiling is a project scope; there is
            // no project scope to enforce here.
            return invocation;
        }

        // #2121: the raw record, so a revoked path is refused by name rather than as "never trusted".
        // Either way the refusal is the same fail-closed exception; only the message differs.
        var ceiling = ProjectCeilingStore.TryGetRecord(ceilingKey, ProjectCeilingStore.DefaultPath);
        if (ceiling is null)
        {
            throw new ProjectNotTrustedException(ceilingKey);
        }

        if (ceiling.RevokedAt is { } revokedAt)
        {
            throw new ProjectNotTrustedException(ceilingKey, ceilingKey, revokedAt);
        }

        if (ceiling.IsUnrestricted)
        {
            return invocation;
        }

        if (invocation.PermissionGrant is not { } grant)
        {
            throw new ProjectCeilingRequiresStructuredGrantException(contract.WorkerName, ceilingKey);
        }

        var capped = ceiling.Cap(grant);

        // #1784: strictCategories comes from the ceiling's own booleans, not from `capped` — see
        // CategoriesDefeatedByTheShell's own doc for why the capped value alone cannot be used here.
        // spec/baton.md §9's operator ruling is canonical for the rest; not restated here.
        HashSet<string> strictCategories = [];
        if (!ceiling.WriteFiles)
        {
            strictCategories.Add(nameof(PermissionGrant.WriteFiles));
        }

        if (!ceiling.NetworkAccess)
        {
            strictCategories.Add(nameof(PermissionGrant.NetworkAccess));
        }

        if (capped.CategoriesDefeatedByTheShell(strictCategories: strictCategories) is { Count: > 0 } withheld)
        {
            throw new IncoherentPermissionGrantException(contract.WorkerName, withheld, capped.ShellCommandPatterns);
        }

        // #1166 review finding M1: the same predicate WorkerBindingResolver's pre-existing check uses,
        // called directly rather than re-implemented, so the two rechecks cannot drift apart.
        WorkerBindingResolver.RefuseIfTheContractCannotBeWritten(
            contract.WorkerName, contract, capped, withheldWritesReachTheOutbox);

        return invocation with { PermissionGrant = capped };
    }
}
