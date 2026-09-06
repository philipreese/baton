using Baton.Domain;

namespace Baton.Vendors;

/// <summary>
/// Materializes a single worker <see cref="WorkerRole"/> from the catalog into the
/// <see cref="WorkflowDefinition"/> + <see cref="WorkerBindingConfigEntry"/> the engine runs — the
/// shared primitive behind <c>baton dispatch &lt;role&gt;</c> (#900, front-door rung 2). It is the one
/// place that turns "what a role produces" (its <see cref="WorkerRole.Outputs"/>) into a
/// <see cref="WorkerContract"/> the engine's <c>ContractValidator</c> enforces, so a role that writes
/// nothing fails loudly without the caller restating the contract.
/// </summary>
/// <remarks>
/// Deliberately surface-agnostic — it takes catalog and domain types only, never a CLI or UI type —
/// so any future built-in template or desktop authoring surface can adopt it in place of hand-rolling
/// its own bindings (#901), rather than growing a second parallel source of truth. Its other consumer
/// today is <see cref="WorkflowTemplateComposer"/>.
/// </remarks>
public static class RoleDispatch
{
    /// <summary>
    /// The reusable core: a resolved role plus a task spec become one worker binding whose contract's
    /// <c>ProducedOutputs</c> are exactly the role's declared outputs, whose grant/timeout/model/effort
    /// come from the role, and whose prompt is the spec with the role's output instructions appended —
    /// single-sourced from the catalog so a spec prompt stays just the task.
    /// </summary>
    /// <param name="role">The resolved catalog role (see <see cref="WorkerRoleCatalog.For"/>).</param>
    /// <param name="spec">The task prompt for this dispatch — what the worker is asked to do.</param>
    /// <param name="adapterOverride">
    /// A vendor adapter to run this role on instead of its tier's default (<see cref="WorkerRole.Adapter"/>) —
    /// the <c>--adapter</c> escape hatch. A role never names a vendor, so this is the only place a
    /// caller picks one, and it does not change the role's capability.
    /// </param>
    /// <param name="workerName">
    /// The binding/contract key for this worker, defaulting to <see cref="WorkerRole.Id"/>. A
    /// multi-phase composer passes a phase-unique name instead — see
    /// <see cref="WorkflowTemplateComposer"/> for why role ids will not do there.
    /// </param>
    /// <param name="workingDirectory">
    /// The directory the worker runs in and may read — set on the binding so a vendor that ignores the
    /// process cwd (agy <c>-p</c>, #491) is still handed the project via <c>--add-dir</c>. Null leaves it
    /// unset, the pre-#1083 behaviour, under which a role dispatched to read the repo was given no path to
    /// it and every repo read was auto-denied.
    /// </param>
    /// <param name="modelOverride">
    /// The model axis, independent of the role ([0017]: vendor, model and effort are three
    /// separate axes over a role's instructions). Null keeps the role's tier model — except when
    /// <paramref name="adapterOverride"/> moves the role to a different vendor, where the tier's
    /// vendor-specific model string is dropped for that vendor's own default (#1082).
    /// </param>
    /// <param name="effortOverride">The effort axis, independent of the role — a behavioural name ([0023]), null keeps the role's tier effort.</param>
    /// <param name="requiredInputs">
    /// The upstream artifacts this worker consumes, in the SAME order as its step definition's
    /// <c>Inputs</c> (#1147): the adapters' prompt builders key the "inputs are available at
    /// <c>BATON_INPUT_&lt;n&gt;</c>" disclosure on the contract's <see cref="WorkerContract.RequiredInputs"/>,
    /// and the variables are positional per the step's list — an input the contract omits is delivered
    /// but never disclosed, so the worker cannot find it. Empty for a role dispatched alone.
    /// </param>
    /// <param name="autoProvisionWorktree">
    /// When an audited grant needs isolation (<see cref="GrantAuditMode.AuditedNotEnforced"/>), declare
    /// a fresh worktree of <paramref name="workingDirectory"/> at <c>HEAD</c> — never handing the
    /// worker that directory as-is, regardless of whether it already happens to be a worktree itself,
    /// because <see cref="WorkerBindingConfigEntry.IsWorktree"/> is the provisioner's own stamp that a
    /// run made the tree (#1354). <see cref="RoleDispatch.Materialize"/> (a direct role dispatch) takes
    /// this path; <see cref="WorkflowTemplateComposer"/> deliberately opts out (R5) — see its own call
    /// site for why.
    /// </param>
    /// <param name="timeoutOverride">
    /// The <c>--timeout</c> escape hatch (#1442), independent of the role like <paramref
    /// name="modelOverride"/>/<paramref name="effortOverride"/> — rationale in spec/baton.md §2.
    /// Null keeps <see cref="WorkerRole.Timeout"/>.
    /// </param>
    /// <param name="attachments">Attached context files supplied by the operator.</param>
    /// <param name="attachmentsDirectory">The directory inside the room artifacts where attached files live.</param>
    /// <param name="tokenBudgetOverride">
    /// The <c>--token-budget</c> escape hatch (#1623), independent of the role like <paramref
    /// name="timeoutOverride"/>. Null keeps <see cref="WorkerRole.TokenBudget"/>, resolved against the
    /// winning <paramref name="adapterOverride"/> (or the role's own tier adapter) via
    /// <see cref="TokenBudgetSpec.Resolve"/> -- #1745: a role's map with no entry for that adapter
    /// throws <see cref="TokenBudgetAdapterNotConfiguredException"/> rather than silently running
    /// unwatched.
    /// </param>
    /// <param name="maxToolStepsOverride">
    /// The <c>--max-tool-steps</c> escape hatch (#1686 review F11), mirroring <paramref
    /// name="tokenBudgetOverride"/> end to end. Null keeps <see cref="WorkerRole.MaxToolSteps"/>.
    /// </param>
    /// <param name="billedRateLimitOverride">
    /// The <c>--billed-rate-limit</c> escape hatch (#1691), mirroring <paramref
    /// name="tokenBudgetOverride"/> end to end. Null keeps <see cref="WorkerRole.BilledRateLimit"/> —
    /// which no role sets, so in practice null means no rate trigger at all.
    /// </param>
    /// <param name="verifyCommandOverride">
    /// The <c>--verify</c> escape hatch (#1702), independent of the role like <paramref
    /// name="tokenBudgetOverride"/>. Null keeps the workspace-resolution order
    /// (<c>Baton.Mutation.VerifyCommandResolver.Resolve</c>): a <c>.baton/verify</c> declaration, then
    /// <see cref="WorkerRole.VerifyPixiTask"/>.
    /// </param>
    /// <param name="expectPrOverride">
    /// The <c>--expect-pr</c> escape hatch (#1788), independent of the role like <paramref
    /// name="tokenBudgetOverride"/> -- but unlike every override above, its EFFECTIVE value is resolved
    /// right here as <c>expectPrOverride ?? role.DeliversBranch</c> rather than left null; spec/baton.md
    /// §3's "Post-exit delivery check" entry states why this one resolves early instead of downstream.
    /// </param>
    /// <param name="skills">
    /// #1151: the canonical skill package names the operator attached with <c>--skill</c>, repeatable.
    /// Each is resolved here through <see cref="SkillPackageResolver"/> and its declared
    /// <c>requires</c> checked against the ROLE's own catalog grant — so an unknown name
    /// (<see cref="UnknownSkillPackageException"/>), an unparseable package
    /// (<see cref="SkillPackageFormatException"/>) or an unsatisfiable requirement
    /// (<see cref="SkillRequirementUnsatisfiedException"/>) refuses before <c>DispatchCommand</c> creates
    /// a room directory. The names, not the resolved packages, are what lands on
    /// <see cref="WorkerBindingConfigEntry.Skills"/>: a package is content that can change between the
    /// dispatch and the redispatch, and the binding records what was ASKED for.
    /// <para>
    /// Checked against <see cref="WorkerRole.Grant"/> rather than the possibly-widened local grant
    /// below, for the same reason <see cref="WorkerBindingConfigEntry.ChangesTree"/> is: that widening
    /// is an audited compensation for an adapter that cannot otherwise reach the outbox, not a
    /// capability the room actually granted, and letting a skill's requirement be satisfied by it would
    /// let operator-authored content ride a carve-out made for a vendor mechanism.
    /// </para>
    /// </param>
    /// <param name="verifyResultsPath">
    /// #1882: where the engine's pre-turn verify step wrote its results, when one ran for this dispatch.
    /// Non-null adds a single paragraph to the prompt pointing the reviewer at that file and requiring
    /// its verdict's runtime claims to cite it. Null (every dispatch without <c>--verify-cmd</c>) adds
    /// nothing at all — the prompt must not mention a file that does not exist.
    /// </param>
    public static WorkerBindingConfigEntry ToBinding(
        WorkerRole role, string spec, string? adapterOverride = null, string? workerName = null,
        string? workingDirectory = null, string? modelOverride = null, string? effortOverride = null,
        IReadOnlyList<string>? requiredInputs = null, string? outputOverride = null,
        bool autoProvisionWorktree = true, TimeSpan? timeoutOverride = null,
        IReadOnlyList<string>? attachments = null, string? attachmentsDirectory = null,
        long? tokenBudgetOverride = null, int? maxToolStepsOverride = null,
        long? billedRateLimitOverride = null, string? verifyCommandOverride = null,
        bool? expectPrOverride = null, string? verifyResultsPath = null,
        IReadOnlyList<string>? skills = null)
    {
        ArgumentNullException.ThrowIfNull(role);
        ArgumentNullException.ThrowIfNull(spec);

        // #1151 (spec/baton.md §9), the ergonomic half: resolve and check before anything is provisioned. The
        // load-bearing copy of both refusals lives in WorkerBindingResolver.Resolve, the seam the
        // `baton run` path also crosses -- this one exists so a typo'd --skill costs nothing.
        var resolvedSkills = SkillPackageResolver.ResolveAll(skills, workingDirectory);
        WorkerBindingResolver.RefuseIfASkillRequiresMoreThanTheGrant(
            string.IsNullOrWhiteSpace(workerName) ? role.Id : workerName, resolvedSkills, role.Grant);

        var outputs = role.Outputs.ToList();
        if (!string.IsNullOrWhiteSpace(outputOverride) && outputs.Count > 0)
        {
            var customName = Path.GetFileName(outputOverride);
            outputs[0] = new WorkerRoleOutput(customName, outputs[0].Schema, outputs[0].Instruction);
        }

        var contract = new WorkerContract(
            WorkerName: string.IsNullOrWhiteSpace(workerName) ? role.Id : workerName,
            RequiredInputs: requiredInputs ?? [],
            ProducedOutputs: outputs.Select(o => new ProducedOutput(o.Name, Schema: o.Schema)).ToList(),
            OptionalMetadata: []);

        // Normalize whichever adapter wins, not just the CLI override: role.Adapter comes from the
        // operator-editable, rebuild-free WorkerTiers.json, so a tier authored as "Claude" must resolve
        // the same as the override path does — otherwise the binding fails with UnknownWorkerAdapterException
        // for an adapter that plainly exists. Since #1567 this normalized string is also what gets frozen
        // onto ExecutionRequest.Adapter and written into flow.jsonl, so it is now the join key of durable
        // history against WorkerAdapterRegistry/StandardWorkerUsageParsers, not just a same-room round-trip
        // through bindings.json — a future change to this normalization changes what already-recorded lines
        // resolve to.
        var adapter = (string.IsNullOrWhiteSpace(adapterOverride) ? role.Adapter : adapterOverride)
            .Trim().ToLowerInvariant();

        // Vendor, model and effort are three independent axes ([0017]): the role carries a
        // default bundle (its tier), and each axis overrides on its own. An explicit --model/--effort
        // wins; with none, swapping the vendor drops the tier's model AND effort. Both are vendor-specific
        // as the catalog actually pins them: the model string plainly so (the measured #1082 failure, the
        // claude CLI handed 'gemini-3.6-flash-high'), and effort because WorkerTiers.json pins raw vendor
        // flag values ("high"/"low"), not the canonical [0023] vocabulary the adapters would map — so an
        // "xhigh"/"max" tier swapped onto agy (which rejects those) would leak the exact same way. On a
        // swap the new vendor falls back to its own default for both, unless the axis is set explicitly.
        var vendorSwapped = !string.Equals(adapter, role.Adapter.Trim().ToLowerInvariant(), StringComparison.Ordinal);
        var model = !string.IsNullOrWhiteSpace(modelOverride) ? modelOverride
            : vendorSwapped ? null
            : role.Model;
        var effort = !string.IsNullOrWhiteSpace(effortOverride) ? effortOverride
            : vendorSwapped ? null
            : role.Effort;

        var grant = role.Grant;
        var grantAuditMode = GrantAuditMode.Enforced;

        if (!role.Grant.WriteFiles && contract.ProducedOutputs.Count > 0)
        {
            if (WorkerAdapterRegistry.Default.TryGetValue(adapter, out var targetAdapter) && !targetAdapter.WithheldWritesReachTheOutbox)
            {
                grant = role.Grant with { WriteFiles = true };
                grantAuditMode = GrantAuditMode.AuditedNotEnforced;
            }
        }

        WorktreeWorkspace? worktreeSpec = null;
        var effectiveWorkDir = workingDirectory;

        if (autoProvisionWorktree && grantAuditMode == GrantAuditMode.AuditedNotEnforced && !string.IsNullOrWhiteSpace(workingDirectory))
        {
            // R1: always a fresh worktree of the caller's directory at HEAD, whether that directory is
            // a plain checkout or already a worktree itself — never trust the caller's own tree, and
            // never stamp IsWorktree on it (see the parameter doc above). WorktreeWorkspaces.Provision
            // is what actually creates the tree and stamps IsWorktree: true once it has.
            worktreeSpec = new WorktreeWorkspace(workingDirectory, "HEAD");
            effectiveWorkDir = null;
        }

        return new WorkerBindingConfigEntry(
            Adapter: adapter,
            Contract: contract,
            PromptTemplate: BuildPrompt(role, adapter, spec, outputs, attachments, attachmentsDirectory, verifyResultsPath),
            Timeout: timeoutOverride ?? role.Timeout,
            Model: model,
            PermissionGrant: grant,
            WorkingDirectory: effectiveWorkDir,
            Effort: effort,
            Worktree: worktreeSpec,
            GrantAuditMode: grantAuditMode,
            IsWorktree: false,
            // #1089, #1540: agy and claude. Streaming puts event-level JSON envelopes on stdout so a running lane's
            // log fills incrementally (feeding the live tail), while agy's terminal `result` event reaches the
            // teardown-hang guard. claude dispatches run plain stream-json --verbose without --include-partial-messages.
            StreamJson: StreamsJson(adapter),
            // #1623: verify is the engine's own step. #1702: the role's own default is now only the
            // lowest-precedence input to VerifyCommandResolver.Resolve, alongside the workspace's own
            // .baton/verify declaration and this verifyCommandOverride -- see VerifyCommandOverride's
            // own remarks on WorkerBindingConfigEntry.
            VerifyPixiTask: role.VerifyPixiTask,
            VerifyCommandOverride: verifyCommandOverride,
            // #1745: --token-budget wins outright; otherwise the role's own spec is resolved against
            // THIS binding's winning adapter (the local `adapter` above, already normalized/overridden),
            // never role.Adapter -- a per-adapter map must answer for the vendor actually dispatched to.
            TokenBudget: tokenBudgetOverride ?? role.TokenBudget?.Resolve(role.Id, adapter),
            // #1686 review F11: the --max-tool-steps escape hatch, mirroring --token-budget.
            MaxToolSteps: maxToolStepsOverride ?? role.MaxToolSteps,
            // #1691: the --billed-rate-limit escape hatch, mirroring both of the above.
            BilledRateLimit: billedRateLimitOverride ?? role.BilledRateLimit,
            // #1622/#1390: read off the CATALOG role's own grant, before the write-widening above can
            // touch it -- see WorkerBindingConfigEntry.ChangesTree's own remarks for why re-deriving
            // this from the (possibly widened) `grant` local a few lines up would misclassify a
            // read-only role under some adapters.
            ChangesTree: role.Grant.WriteFiles && role.Grant.RunShellCommands,
            // #1788: DeliversBranch is purely catalog-controlled (no dispatch-time override exists for
            // it); ExpectPr is resolved HERE against it -- see expectPrOverride's own doc for why.
            DeliversBranch: role.DeliversBranch,
            ExpectPr: expectPrOverride ?? role.DeliversBranch,
            // #1802: purely catalog-controlled, like DeliversBranch -- no dispatch-time override exists.
            AllowsSubagents: role.AllowsSubagents,
            // #1151: the NAMES, already proven resolvable and grant-satisfiable above.
            Skills: resolvedSkills.Count == 0 ? null : resolvedSkills.Select(s => s.Name).ToList());
    }

    /// <summary>
    /// Whether <paramref name="adapter"/> is one of the vendors that streams JSON on stdout
    /// for (issue #1561 finding 10). The single source for a predicate previously duplicated between
    /// <see cref="ToBinding"/> above and <c>RedispatchCommand.InheritBinding</c> — a third streaming
    /// vendor now only needs adding here, rather than in both places with the redispatch path silently
    /// diverging from dispatch if the second site were missed.
    /// </summary>
    public static bool StreamsJson(string adapter) =>
        string.Equals(adapter, "agy", StringComparison.OrdinalIgnoreCase)
        || string.Equals(adapter, "claude", StringComparison.OrdinalIgnoreCase)
        || string.Equals(adapter, "codex", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Wraps <see cref="ToBinding"/> in a single-step workflow — the shape <c>baton dispatch</c> hands to
    /// the same pump <c>baton run</c> drives. The step's <see cref="WorkflowStepDefinition.Outputs"/>
    /// mirror the contract's, so the reporter prints each produced file's path on success.
    /// </summary>
    public static (WorkflowDefinition Definition, IReadOnlyDictionary<string, WorkerBindingConfigEntry> Bindings) Materialize(
        WorkerRole role, string spec, string? adapterOverride = null, string? workingDirectory = null,
        string? modelOverride = null, string? effortOverride = null, string? outputOverride = null,
        TimeSpan? timeoutOverride = null, IReadOnlyList<string>? attachments = null,
        string? attachmentsDirectory = null, long? tokenBudgetOverride = null, int? maxToolStepsOverride = null,
        long? billedRateLimitOverride = null, string? verifyCommandOverride = null, bool? expectPrOverride = null,
        string? verifyResultsPath = null, IReadOnlyList<string>? skills = null)
    {
        ArgumentNullException.ThrowIfNull(role);

        var binding = ToBinding(
            role, spec, adapterOverride, workingDirectory: workingDirectory,
            modelOverride: modelOverride, effortOverride: effortOverride, outputOverride: outputOverride,
            timeoutOverride: timeoutOverride, attachments: attachments, attachmentsDirectory: attachmentsDirectory,
            tokenBudgetOverride: tokenBudgetOverride, maxToolStepsOverride: maxToolStepsOverride,
            billedRateLimitOverride: billedRateLimitOverride,
            verifyCommandOverride: verifyCommandOverride, expectPrOverride: expectPrOverride,
            verifyResultsPath: verifyResultsPath, skills: skills);

        var stepOutputs = binding.Contract.ProducedOutputs.Select(o => o.Name).ToList();

        var definition = new WorkflowDefinition(
            WorkflowTemplateId: new WorkflowTemplateId($"dispatch-{role.Id}"),
            WorkflowTemplateVersion: 1,
            Steps:
            [
                new WorkflowStepDefinition(
                    StepId: new StepId(role.Id),
                    Worker: role.Id,
                    Inputs: [],
                    Outputs: stepOutputs,
                    DependsOn: [],
                    RetryPolicy: new RetryPolicy(3),
                    PausePoint: null)
            ]);

        var bindings = new Dictionary<string, WorkerBindingConfigEntry> { [role.Id] = binding };
        return (definition, bindings);
    }

    /// <summary>
    /// The spec, then the role's output instructions verbatim — so the worker is told to produce
    /// exactly the files the contract asserts. A role always declares at least one output (the catalog
    /// enforces it at load), so the header is never emitted without lines under it.
    /// </summary>
    private static string BuildPrompt(
        WorkerRole role, string adapter, string spec, IReadOnlyList<WorkerRoleOutput>? outputs = null,
        IReadOnlyList<string>? attachments = null, string? attachmentsDirectory = null,
        string? verifyResultsPath = null)
    {
        var activeOutputs = outputs ?? role.Outputs;
        var instructions = string.Join("\n", activeOutputs.Select(o => $"- {o.Instruction}"));
        if (role.Outputs.Count > 0 && activeOutputs.Count > 0 && !string.Equals(role.Outputs[0].Name, activeOutputs[0].Name, StringComparison.Ordinal))
        {
            instructions = instructions.Replace(role.Outputs[0].Name, activeOutputs[0].Name, StringComparison.Ordinal);
        }

        var promptBuilder = new System.Text.StringBuilder();
        promptBuilder.Append(spec.TrimEnd());

        // #1920: the review role is the one whose shell grant is scoped tightly enough that a reviewer
        // discovers its edges by refusal; implement/janitor run unscoped and lose no steps to this.
        if (role.Id == "review" && ReviewToolGuidance(adapter) is { } reviewToolGuidance)
        {
            promptBuilder.Append($"\n\n{reviewToolGuidance}");
        }

        if (attachments is { Count: > 0 } && !string.IsNullOrEmpty(attachmentsDirectory))
        {
            var fileNames = attachments.Select(Path.GetFileName);
            promptBuilder.Append($"\n\nAttached files (in {attachmentsDirectory}): {string.Join(", ", fileNames)}");
        }

        // #1882: before the outputs block, because it is context for the review rather than an
        // instruction about where to write — the reviewer should have read this file before it starts
        // forming the findings the outputs block asks it to record.
        if (!string.IsNullOrWhiteSpace(verifyResultsPath))
        {
            promptBuilder.Append($"\n\n{VerifyResultsParagraph(verifyResultsPath)}");
        }

        promptBuilder.Append($"\n\nRequired outputs:\n{instructions}\n\n{OneShotContract}");
        return promptBuilder.ToString();
    }

    /// <summary>
    /// #1920's ask, verbatim: the one line a codex review prompt carries so the granted read path is
    /// known before the first turn rather than found by refusal. Measured on the issue's room: `rg` was
    /// re-issued four times, and two more steps went to a Windows backslash path, before the model
    /// reached <c>baton_search_text</c>. Both halves are true of the codex channel — the dynamic tools
    /// are what read and search there, and <c>CodexDynamicToolPolicy</c> routes every shell line
    /// through the matcher that refuses a backslash (<c>ShellCommandPatternMatcher</c>) and declares no
    /// <c>rg</c> tool.
    /// </summary>
    private const string CodexReviewToolGuidance =
        "search with baton_search_text, read with baton_read_text; rg and backslash paths are not granted";

    /// <summary>
    /// #1920, claude half. Written from the CLAUDE measurement in the issue's audit comment (46 of 97
    /// refusals on that vendor), not transposed from the codex one: what a claude reviewer actually
    /// loses steps to is <c>cd</c>, <c>cat</c>, <c>head</c>, <c>echo</c> and <c>git grep</c>, plus every
    /// compound line that carries one of them, because a scoped grant judges each segment on its own
    /// (<see cref="ShellCommandPatternMatcher.EvaluateChainedCommand"/>). It deliberately says nothing
    /// about backslash paths: that rule is a property of the shell channel alone, and claude's own Read
    /// and Grep take Windows paths.
    /// </summary>
    private const string ClaudeReviewToolGuidance =
        "read with Read, search with Grep; the Bash grant is a read-only git/gh allowlist, so cd, cat, "
        + "head, echo and git grep are refused, and a chained command (&&, |) is refused whole unless "
        + "every segment is itself granted";

    /// <summary>
    /// #1920: vendor-specific because the tool names are. An adapter with no measured line returns
    /// <see langword="null"/> and the prompt gains nothing rather than a guessed one — agy's own
    /// measured friction in the same audit is repeat reads, not refusals, and is tracked at #1921.
    /// </summary>
    private static string? ReviewToolGuidance(string adapter) => adapter switch
    {
        "codex" => CodexReviewToolGuidance,
        "claude" => ClaudeReviewToolGuidance,
        _ => null,
    };

    /// <summary>
    /// #1882: the one paragraph a review prompt gains when the engine ran a pre-turn verify step. It
    /// states three things a reviewer would otherwise have to guess: that the file exists and is
    /// authoritative, that a non-zero exit in it is evidence rather than a reason to stop, and that a
    /// runtime claim in the verdict must cite it. The last is the point — the failure this replaces is a
    /// review ending with "nothing was executed here, the PR body's numbers are unverified".
    /// </summary>
    private static string VerifyResultsParagraph(string verifyResultsPath) =>
        $"{VerifyResultsParagraphOpening}, with no model "
        + $"involved, and wrote what they did to {verifyResultsPath}. Read that file first: it holds the "
        + "exact command line, exit code, wall clock and output tail for each one, captured by the "
        + "engine rather than reported by anybody. A non-zero exit there is evidence for your review, "
        + "not a reason to stop reviewing. Every runtime claim your verdict makes — a test count, an "
        + "exit code, whether something builds — must cite that file; if a claim you want to make is "
        + "not answered there, say it was not measured rather than asserting it.";

    /// <summary>
    /// The paragraph's opening clause, shared by the builder above and
    /// <see cref="WithoutVerifyResultsParagraph"/> so the text that is written and the text that is
    /// recognized cannot drift apart. It carries no interpolated path, which is what makes it
    /// matchable at all.
    /// </summary>
    private const string VerifyResultsParagraphOpening =
        "Before your first turn the engine ran a set of allowlisted commands for you";

    /// <summary>
    /// #1895: the same prompt with <see cref="VerifyResultsParagraph"/> removed, or unchanged when it
    /// carries none. Its one caller is <c>RedispatchCommand.InheritBinding</c>, and spec/baton.md §9
    /// is the register for why a redispatched review must not inherit it — not restated here.
    /// <para>
    /// Matched on the opening clause and removed whole, paragraph-wise, because the sentence that
    /// carries the path is the second one — a path-substring match would leave the "the engine ran a
    /// set of allowlisted commands for you" claim standing with the citation requirement attached.
    /// </para>
    /// </summary>
    public static string WithoutVerifyResultsParagraph(string promptTemplate)
    {
        ArgumentNullException.ThrowIfNull(promptTemplate);

        if (!promptTemplate.Contains(VerifyResultsParagraphOpening, StringComparison.Ordinal))
        {
            return promptTemplate;
        }

        // The same "\n\n" BuildPrompt joins its blocks with -- this only ever reads a prompt that
        // builder wrote, so there is no other separator to consider.
        var paragraphs = promptTemplate
            .Split("\n\n")
            .Where(paragraph => !paragraph.TrimStart().StartsWith(VerifyResultsParagraphOpening, StringComparison.Ordinal));
        return string.Join("\n\n", paragraphs);
    }

    // #1095: a dispatched worker runs in a one-shot, non-interactive harness — the turn is never
    // resumed. A sonnet implement worker instead scheduled a background test run, ended its turn to
    // wait for the notification, and produced no output; the contract failed and the step retried a
    // worker that would defer identically every time. State the contract in the prompt. Lives here,
    // the dispatch prompt builder, not in the adapter's BuildPrompt (which also runs for the
    // interactive chat turn, where deferring genuinely is fine).
    private const string OneShotContract =
        "This is a single, non-interactive turn: do all of the work to completion now and write the "
        + "required outputs before it ends. Do not schedule background tasks or wait for a "
        + "notification or wake-up — nothing will resume this turn, so any deferred work is lost.";
}
