using Baton.Domain;
using Baton.Mutation;

namespace Baton.Vendors;

/// <summary>
/// Turns a parsed worker-binding config into the <c>Baton.Mutation.WorkerBinding</c> dictionary
/// <c>MutationInterface.StartWorkflowAsync</c> needs — the "adapter resolution into WorkerBinding"
/// M11 Phase 1 names, kept out of <c>Baton</c> entirely per CLAUDE.md's Adapter Isolation rule.
/// Every entry resolves to <see cref="WorkerBinding.Process"/>: a worker-binding config describes
/// a real vendor invocation, never a non-process party (<c>Baton.Mutation.WorkerBinding.NonProcess</c>)
/// — those are constructed directly by whatever caller needs one, same as before this
/// seam existed.
/// </summary>
public static class WorkerBindingResolver
{
    /// <param name="config">The parsed worker-binding config to resolve.</param>
    /// <param name="adapters">The registered adapters each entry's <see cref="WorkerBindingConfigEntry.Adapter"/> looks up through.</param>
    /// <param name="profiles">
    /// The local per-machine profile mapping (M23 Phase 3, #272; see <see cref="BatonProfileStore"/>),
    /// consulted only for an entry whose <see cref="WorkerBindingConfigEntry.WorkingDirectory"/> is a
    /// non-rooted profile name rather than a literal path. Null (the default) behaves exactly like an
    /// empty map — every entry naming a profile then throws <see cref="UnknownWorkingDirectoryProfileException"/>,
    /// while an entry with no <see cref="WorkerBindingConfigEntry.WorkingDirectory"/> at all, or a
    /// rooted one, is entirely unaffected.
    /// </param>
    /// <param name="bindingsFileDirectory">
    /// The directory <paramref name="config"/> was loaded from, if known (M23 Phase 3, #272) —
    /// forwarded verbatim into every resolved <see cref="WorkerInvocation.BindingsFileDirectory"/>.
    /// No shipped adapter currently reads it — the one that did (a config-sidecar path resolver) was
    /// retired with the dialogue worker (#1408) — every adapter today ignores it.
    /// </param>
    /// <param name="onWorkerStdoutLine">
    /// M24 Phase 1's live in-turn streaming seam: when supplied, every resolved
    /// <see cref="CoreDispatchTarget"/> gets this wrapped as its <see cref="CoreDispatchTarget.OnStdoutLine"/>,
    /// called with the worker's name and each raw stdout line as its dispatch runs live. Null (the
    /// default) for every caller that has no live consumer for that — <c>baton run</c>/<c>baton decide</c>
    /// from the CLI, any non-interactive workflow — since capturing output at all has a real cost
    /// (<see cref="Baton.Dispatch.CoreDispatcher"/> only turns on stdout capture when
    /// <c>OnStdoutLine</c> is non-null). What this callback actually does with a line — parse it,
    /// broadcast it — is entirely the caller's concern; this seam only ever forwards raw text.
    /// </param>
    /// <exception cref="UnknownWorkerAdapterException">
    /// An entry names an <see cref="WorkerBindingConfigEntry.Adapter"/> not present in <paramref name="adapters"/>.
    /// </exception>
    /// <exception cref="UnknownWorkingDirectoryProfileException">
    /// An entry's <see cref="WorkerBindingConfigEntry.WorkingDirectory"/> names a profile with no
    /// entry in <paramref name="profiles"/>.
    /// </exception>
    /// <exception cref="IncoherentPermissionGrantException">
    /// An entry's <see cref="WorkerBindingConfigEntry.PermissionGrant"/> grants the shell while
    /// withholding a category the shell reaches anyway (#529).
    /// </exception>
    /// <exception cref="UnsatisfiableOutputContractException">
    /// An entry's <see cref="WorkerBindingConfigEntry.Contract"/> declares outputs its
    /// <see cref="WorkerBindingConfigEntry.PermissionGrant"/> gives it no way to write (#629).
    /// </exception>
    public static IReadOnlyDictionary<string, WorkerBinding> Resolve(
        IReadOnlyDictionary<string, WorkerBindingConfigEntry> config,
        IReadOnlyDictionary<string, IWorkerAdapter> adapters,
        IReadOnlyDictionary<string, string>? profiles = null,
        string? bindingsFileDirectory = null,
        Action<string, string>? onWorkerStdoutLine = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(adapters);

        var bindings = new Dictionary<string, WorkerBinding>(config.Count);
        foreach (var (workerName, entry) in config)
        {
            bindings[workerName] = ResolveEntry(workerName, entry, adapters, profiles, bindingsFileDirectory, onWorkerStdoutLine);
        }

        return bindings;
    }

    /// <summary>
    /// Same resolution as <see cref="Resolve"/>, but per-entry and deferred: every bind-time refusal
    /// above only fires for an entry some caller actually looks up by name, never for the rest of the
    /// file merely because it was present (#662). <c>baton run</c> still wants <see cref="Resolve"/>'s
    /// eager form — a fresh dispatch should fail before it starts rather than partway through — but
    /// <c>baton cancel</c>/<c>baton supply</c> act on a room directory whose run has already started, and
    /// need only the bindings a step actually reachable from here will use.
    /// </summary>
    public static IReadOnlyDictionary<string, WorkerBinding> ResolveLazily(
        IReadOnlyDictionary<string, WorkerBindingConfigEntry> config,
        IReadOnlyDictionary<string, IWorkerAdapter> adapters,
        IReadOnlyDictionary<string, string>? profiles = null,
        string? bindingsFileDirectory = null,
        Action<string, string>? onWorkerStdoutLine = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(adapters);

        return new LazyWorkerBindings(
            config,
            (workerName, entry) => ResolveEntry(workerName, entry, adapters, profiles, bindingsFileDirectory, onWorkerStdoutLine));
    }

    /// <summary>
    /// #802: resolves ONLY the entries that declare <see cref="WorkerBindingConfigEntry.FallbackOnExhaustion"/>
    /// (that member's own doc has the scope/permission guarantee this satisfies), each swapped onto
    /// its fallback's Adapter/Model/Effort and resolved through the exact same <see cref="ResolveEntry"/>
    /// path <see cref="Resolve"/> uses for the primary binding. A worker role with no declared fallback
    /// is simply absent from the returned dictionary, which is what <c>Mutation.MutationInterface</c>
    /// reads as "no rescue for this role's park."
    /// </summary>
    public static IReadOnlyDictionary<string, WorkerBinding> ResolveFallbacks(
        IReadOnlyDictionary<string, WorkerBindingConfigEntry> config,
        IReadOnlyDictionary<string, IWorkerAdapter> adapters,
        IReadOnlyDictionary<string, string>? profiles = null,
        string? bindingsFileDirectory = null,
        Action<string, string>? onWorkerStdoutLine = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(adapters);

        var bindings = new Dictionary<string, WorkerBinding>();
        foreach (var (workerName, entry) in config)
        {
            if (entry.FallbackOnExhaustion is not { } fallback)
            {
                continue;
            }

            var fallbackEntry = entry with
            {
                Adapter = fallback.Adapter,
                Model = fallback.Model,
                Effort = fallback.Effort,
                // The fallback's OWN dispatch never declares a further fallback -- #802 rules
                // undeclared/chained failover out permanently (operator ruling, 2026-09-01); a single
                // declared hop is the whole feature.
                FallbackOnExhaustion = null,
            };

            bindings[workerName] = ResolveEntry(workerName, fallbackEntry, adapters, profiles, bindingsFileDirectory, onWorkerStdoutLine);
        }

        return bindings;
    }

    private static WorkerBinding ResolveEntry(
        string workerName,
        WorkerBindingConfigEntry entry,
        IReadOnlyDictionary<string, IWorkerAdapter> adapters,
        IReadOnlyDictionary<string, string>? profiles,
        string? bindingsFileDirectory,
        Action<string, string>? onWorkerStdoutLine)
    {
        if (!adapters.TryGetValue(entry.Adapter, out var adapter))
        {
            throw new UnknownWorkerAdapterException(entry.Adapter, adapters.Keys);
        }

        // Only an ACTUALLY-provisioned worktree counts as isolation. A declared-but-unprovisioned
        // Worktree spec is not: the callers that skip WorktreeWorkspaces.Provision (#1012 records
        // which) would otherwise dispatch an audited worker into a null working directory — the
        // exact unisolated run the audit exists to make impossible.
        if (entry.GrantAuditMode == GrantAuditMode.AuditedNotEnforced && !entry.IsWorktree)
        {
            throw new UnisolatedGrantAuditException(workerName);
        }

        // Both refusals read a grant as deciding what the worker can do, which is only true for an
        // adapter that consumes it. IPermissionGrantTranslator marks that population, and
        // WorkerAdapterRegistryTests (#651) holds the marker to it by dispatching every registered
        // adapter under two different grants and checking which ones change.
        if (adapter is IPermissionGrantTranslator)
        {
            // Order matters, and there is a test for it: a grant can carry both faults at once, and
            // the shell one names the mistake the operator actually made — they reached for the
            // shell believing it escaped the write withhold. Told only that the contract is
            // unsatisfiable, they would grant more shell.
            RefuseIfShellDefeatsAWithheldCategory(workerName, entry.PermissionGrant);
            RefuseIfTheContractCannotBeWritten(
                workerName, entry.Contract, entry.PermissionGrant, adapter.WithheldWritesReachTheOutbox);
        }

        var workingDirectory = ResolveWorkingDirectory(workerName, entry.WorkingDirectory, profiles);

        // #1151: names become packages HERE, at the one seam `baton run` and `baton dispatch` both
        // cross -- so a harness-authored bindings.json naming skills is honoured rather than silently
        // ignored, which is the failure class the whole feature exists to remove (spec/baton.md §9).
        // Both refusals below are the same ones RoleDispatch.ToBinding already raised for a dispatch;
        // that one fires before a room directory exists and is the ergonomic check, this one is the
        // load-bearing one because it is the only check the run path reaches.
        var skills = SkillPackageResolver.ResolveAll(entry.Skills, workingDirectory);
        RefuseIfASkillRequiresMoreThanTheGrant(workerName, skills, entry.PermissionGrant);

        var invocation = new WorkerInvocation(
            entry.PromptTemplate, entry.Model, entry.PermissionScope, entry.PermissionGrant,
            workingDirectory, bindingsFileDirectory, entry.SessionId, entry.ResumeSession,
            entry.StreamJson, entry.LogFilePath, entry.Effort,
            // #588: the same entry.Timeout that becomes ExecutionRequest.Timeout below, handed to the
            // adapter so a vendor CLI with its own internal wait limit can be told about AER's. Passing
            // it here rather than plumbing a per-execution value is what keeps this "once per binding
            // entry" contract intact — both come off `entry`.
            entry.Timeout,
            // #1166 review finding A: forwarded so ProjectCeilingGate keys the ceiling on the stable
            // source repository rather than the ephemeral, room-scoped worktree path above.
            WorktreeSourceRepository: entry.WorktreeSourceRepository,
            AllowsSubagents: entry.AllowsSubagents,
            Skills: skills);
        var target = adapter.Resolve(invocation, entry.Contract);

        if (onWorkerStdoutLine is not null)
        {
            var capturedWorkerName = workerName;
            target = target with { OnStdoutLine = line => onWorkerStdoutLine(capturedWorkerName, line) };
        }

        return new WorkerBinding.Process(
            entry.Contract, target, entry.Timeout, adapter, entry.GrantAuditMode, entry.Adapter, entry.Model, adapter,
            entry.VerifyPixiTask, entry.VerifyCommandOverride, entry.TokenBudget, entry.MaxToolSteps,
            entry.BilledRateLimit, entry.IsWorktree, entry.WorktreeBaseSha, entry.ChangesTree,
            entry.DeliversBranch, entry.ExpectPr);
    }


    /// <summary>
    /// #1151 (spec/baton.md §9): a package's declared <c>requires</c> is <b>checked, never applied</b>. The grant is
    /// not widened to satisfy a skill and the skill is not silently dropped — the bind refuses, naming
    /// the skill and the missing categories. Shared with <see cref="RoleDispatch.ToBinding"/>, which
    /// runs the identical predicate earlier so a bad <c>--skill</c> fails before a room directory
    /// exists; internal rather than private so there is one predicate, not two that can drift.
    /// </summary>
    internal static void RefuseIfASkillRequiresMoreThanTheGrant(
        string workerName, IReadOnlyList<SkillPackage> skills, PermissionGrant? grant)
    {
        foreach (var skill in skills)
        {
            if (skill.Requires.MissingFrom(grant) is { Count: > 0 } missing)
            {
                throw new SkillRequirementUnsatisfiedException(workerName, skill.Name, missing);
            }
        }
    }

    /// <summary>
    /// #529, refused at the execution choke point. The rule itself lives on
    /// <see cref="PermissionGrant.CategoriesDefeatedByTheShell(bool, IReadOnlySet{string})"/> — every surface that needs the
    /// same answer asks it there rather than restating the conditions (#645).
    /// </summary>
    private static void RefuseIfShellDefeatsAWithheldCategory(string workerName, PermissionGrant? grant)
    {
        if (grant?.CategoriesDefeatedByTheShell() is { Count: > 0 } withheld)
        {
            throw new IncoherentPermissionGrantException(workerName, withheld);
        }
    }

    /// <summary>
    /// #629: a contract declares outputs the grant gives the worker no way to write. Refused here
    /// rather than discovered by the contract check after the run has been paid for in full.
    ///
    /// <para>
    /// No shell clause is needed, because <see cref="RefuseIfShellDefeatsAWithheldCategory"/> ran
    /// first: a grant that withholds writes while granting the shell never reaches this line, so
    /// anything still here with <see cref="PermissionGrant.WriteFiles"/> withheld has no shell to
    /// write through either.
    /// </para>
    ///
    /// <para>
    /// <b>Why this asks the adapter (#649).</b> Every declared output resolves under
    /// <c>BATON_OUTPUT_DIR</c> — <c>ContractValidator.Validate</c> combines each
    /// <see cref="ProducedOutput.Name"/> onto the output directory and never looks anywhere else — so
    /// "the grant gives no way to write it" is a claim about one directory, not about the workspace.
    /// On an adapter where a withheld write still reaches that directory the refusal is simply false,
    /// and it refuses precisely the shape #649 exists to enable: a read-only reviewer that declares
    /// <c>review.md</c>. So the question goes to <see cref="IWorkerAdapter.WithheldWritesReachTheOutbox"/>
    /// rather than being answered here, and adapters where it is still true keep the refusal.
    /// </para>
    /// <para>
    /// <b>#1166's second caller.</b> <see cref="ProjectCeilingGate.Apply"/> re-checks the same
    /// condition against a grant it has just narrowed (a coherent role grant can become contract-breaking
    /// once capped by a project ceiling, the same reason it re-runs
    /// <see cref="RefuseIfShellDefeatsAWithheldCategory"/>'s predicate) — internal rather than private so
    /// that gate can call this method directly instead of carrying its own copy of the condition.
    /// </para>
    /// </summary>
    internal static void RefuseIfTheContractCannotBeWritten(
        string workerName, WorkerContract contract, PermissionGrant? grant, bool withheldWritesReachTheOutbox)
    {
        // A null grant is the raw PermissionScope escape hatch — nothing structured to reconcile
        // against the contract, so there is no claim here to check.
        if (grant is null || grant.WriteFiles || contract.ProducedOutputs.Count == 0)
        {
            return;
        }

        if (withheldWritesReachTheOutbox)
        {
            return;
        }

        throw new UnsatisfiableOutputContractException(
            workerName, [.. contract.ProducedOutputs.Select(o => o.Name)]);
    }

    /// <summary>
    /// A rooted path passes through unchanged; a non-rooted one is a profile name, looked up in
    /// <paramref name="profiles"/> — the "portable bindings via per-machine profile mapping"
    /// mechanism (M23 Phase 3, #272). Null stays null: most entries never set a
    /// <see cref="WorkerBindingConfigEntry.WorkingDirectory"/> at all.
    /// </summary>
    private static string? ResolveWorkingDirectory(
        string workerName, string? workingDirectory, IReadOnlyDictionary<string, string>? profiles)
    {
        if (workingDirectory is null)
        {
            return null;
        }

        if (Path.IsPathRooted(workingDirectory))
        {
            return workingDirectory;
        }

        if (profiles is null || !profiles.TryGetValue(workingDirectory, out var resolved))
        {
            throw new UnknownWorkingDirectoryProfileException(workerName, workingDirectory);
        }

        return resolved;
    }
}
