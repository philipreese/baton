using System.Text.Json;
using Baton.Vendors;
using Baton.Domain;
using Baton.Status;
using Baton.Templates;

namespace Baton.Cli;

/// <summary>
/// <c>baton redispatch &lt;room-dir&gt;</c> (#1441) — the implementation of the contract spec/baton.md §2
/// states in full (what inherits vs. overrides, the two refusals, the `--output` exception, where
/// lineage lands); this type doc does not restate it. <see cref="DispatchCommand.CopyPrimaryOutputToOverride"/>
/// is the code reference for why a parent's <c>--output</c> destination cannot be recovered here — it
/// is a process-local copy target, never persisted to any room file.
/// </summary>
public static class RedispatchCommand
{
    private const string WorkflowFileName = "workflow.json";
    private const string BindingsFileName = "bindings.json";

    /// <exception cref="CliArgumentException">
    /// The parent room does not exist, has not reached a terminal state (still running, or never
    /// dispatched), bound more than one worker (a composed template), names a role the catalog no
    /// longer has (only reachable when <c>--spec</c> is given, since only then is the catalog
    /// consulted), or a given <c>--spec</c> file does not exist.
    /// </exception>
    public static async Task<CommandResult> ExecuteAsync(
        RedispatchOptions options,
        IReadOnlyDictionary<string, IWorkerAdapter> adapters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(adapters);

        // #1645: first statement of the method so the child room this verb would create does not exist
        // before the refusal. DrainMarker has the rest.
        if (DrainMarker.RefusalMessage("redispatch") is { } drainRefusal)
        {
            throw new CliArgumentException(drainRefusal, DrainMarker.AbortInvocation);
        }

        if (!Directory.Exists(options.ParentRoomDirectoryPath))
        {
            throw new CliArgumentException($"Parent room '{options.ParentRoomDirectoryPath}' does not exist.");
        }

        // Refuse a non-terminal parent (still running, or never dispatched) outright -- a
        // non-interactive CLI has no prompt to gate a "are you sure" behind, the same doctrine
        // DispatchOptionsParser's --timeout ceiling already rests on (spec/baton.md §2).
        var terminalSentinelPath = Path.Combine(options.ParentRoomDirectoryPath, TerminalSentinelWriter.TerminalSentinelFileName);
        if (!File.Exists(terminalSentinelPath))
        {
            // #1586: a missing terminal sentinel means the room never settled — it does NOT
            // distinguish "genuinely still running" from "its engine died mid-wait", the two only
            // being told apart by `baton status` (EngineLivenessProbe's own liveness read). Naming
            // both, and the recovery for the second, is what closes this issue's population: the
            // three verbs an operator reaches for first must point at the verb that actually works
            // (spec/baton.md §3), not only explain their own refusal.
            throw new CliArgumentException(
                $"Parent room '{options.ParentRoomDirectoryPath}' has not reached a terminal state — "
                + "redispatch only reruns a room that has already finished. A missing terminal sentinel "
                + "means one of two things: the room is genuinely still running, or its scheduling engine "
                + $"died before it could settle — check `baton status {options.ParentRoomDirectoryPath}` to tell which.",
                "if it's genuinely running, wait for it or cancel it first; if the engine died, "
                + $"{RecoveryGuidance.RunRoomDirInstruction} (see spec/baton.md §3).");
        }

        // A Succeeded parent needs no confirmation (there is none to ask, non-interactively); a
        // terminal-but-not-Succeeded parent is still allowed, but gets a stderr note rather than a
        // silent redispatch of a failed/cancelled lane.
        var parentTerminal = await TerminalSentinelWriter.TryReadAsync(options.ParentRoomDirectoryPath, cancellationToken)
            .ConfigureAwait(false);

        // Loaded ahead of the Indeterminate refusal below so the refusal's own remedy string can name
        // the parent's recorded flags (adapter/model/timeout/workspace) rather than the dead-end
        // "re-dispatch the parent" (#1623 re-review U1) — the same bindings.json read this method
        // needs later regardless, for the ordinary redispatch path.
        var parentBindingsPath = BatonPaths.RoomBindingsFile(options.ParentRoomDirectoryPath);
        var parentBindings = await WorkerBindingConfigParser.LoadFromFileAsync(parentBindingsPath, cancellationToken)
            .ConfigureAwait(false);

        if (parentBindings.Count != 1)
        {
            throw new CliArgumentException(
                $"Parent room '{options.ParentRoomDirectoryPath}' dispatched {parentBindings.Count} workers — "
                + "redispatch only supports a single-role dispatch (baton dispatch <role> --spec ...), not a "
                + "composed template.");
        }

        var (workerName, parentEntry) = parentBindings.Single();

        // #1586 S1 (ratified amendment, consumer obligation item 2): an Indeterminate parent refuses
        // bare, mirroring #1604's signage pattern (a diagnosis plus a concrete next step) rather than
        // the ordinary warn-and-proceed a Failed/Cancelled parent gets below. "Indeterminate" means
        // journal facts alone could not decide success vs failure — redispatching it silently would
        // treat an unresolved room as though it were an ordinary failed one, discarding the exact
        // ambiguity the state exists to preserve. No `--force` escape hatch: `baton resolve` (#1608)
        // is the only sanctioned way to clear this refusal.
        if (parentTerminal is not null && string.Equals(parentTerminal.State, WorkflowOutcome.Indeterminate, StringComparison.Ordinal))
        {
            // F1 (#1593 review): the refusal is unconditional, but the remedy is picked per producer
            // (spec/baton.md §3's "Consumer obligations" has the reasoning; not restated here).
            // A terminal.json written before IndeterminateProducerKind existed falls back to the
            // pre-F1 hasCapture read.
            // N7 (#1664 re-review): a REJECTED step keeps CapturedResponseFile as its audit trail
            // (StateProjector.cs's CaptureResolved apply clears IndeterminateProducerKind but not the
            // file) while carrying no producer — FirstOrDefault must prefer a step whose
            // IndeterminateProducerKind is non-null over one that only has a stale CapturedResponseFile,
            // or a rejected step sorted ahead of the actually-pending ContractFailure step would win and
            // offer --accept-capture for a room where it throws.
            var indeterminateStep = parentTerminal.Steps.FirstOrDefault(
                    step => step.State == nameof(StepStatus.Failed) && step.IndeterminateProducerKind is not null)
                ?? parentTerminal.Steps.FirstOrDefault(
                    step => step.State == nameof(StepStatus.Failed) && step.CapturedResponseFile is not null);
            var producerKind = indeterminateStep?.IndeterminateProducerKind
                ?? (indeterminateStep?.CapturedResponseFile is not null ? nameof(IndeterminateProducer.CapturedResponse) : null);

            throw new CliArgumentException(
                $"Parent room '{options.ParentRoomDirectoryPath}' settled Indeterminate — journal facts "
                + "alone could not decide whether it succeeded or failed, so redispatching it would "
                + "silently discard that ambiguity rather than resolve it.",
                producerKind switch
                {
                    nameof(IndeterminateProducer.CapturedResponse) =>
                        $"run `baton resolve {options.ParentRoomDirectoryPath} [--execution <id>] "
                        + "--accept-capture | --reject --reason <text>` first, then redispatch — see spec/baton.md §3.",
                    nameof(IndeterminateProducer.ContractFailure) =>
                        $"this room settled Indeterminate with no captured response (an exit-0 contract "
                        + "failure, a dead worker on a mutated workspace, or a #1373 dispatch timeout on "
                        + "one — read the step's reason for which) — `baton resolve --accept-capture` "
                        + $"refuses it (nothing to accept), but `baton resolve {options.ParentRoomDirectoryPath} "
                        + "[--execution <id>] --reject --reason <text>` still resolves it; redispatch the "
                        + "resulting room, or, once resolved, redispatch this one — see spec/baton.md §3.",
                    _ =>
                        "this room settled Indeterminate without a captured response (a verify failure or a "
                        + "token-budget arrest), so there is nothing to accept or reject — "
                        + $"`baton resolve {options.ParentRoomDirectoryPath} [--execution <id>] "
                        + "--close --reason <text>` (#1622 (d)) settles it resolved-but-Failed, which "
                        + "rewrites this room's terminal.json and lifts this refusal; then redispatch this "
                        + "room, or dispatch fresh. Read "
                        + $"`baton status {options.ParentRoomDirectoryPath} --json` for the step's reason and "
                        + "fix the underlying cause first — a fresh room is "
                        + $"{DescribeFreshDispatchRemedy(workerName, parentEntry)}. See spec/baton.md §3.",
                });
        }

        if (parentTerminal is not null && !string.Equals(parentTerminal.State, WorkflowOutcome.Succeeded, StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"Warning: parent room '{options.ParentRoomDirectoryPath}' did not succeed "
                + $"(state: {parentTerminal.State}) — redispatching it anyway.");
        }

        if (options.SpecFilePath is null && options.Attachments is { Count: > 0 })
        {
            // #1576: mirrors DispatchCommand's own "--attach does not apply" refusals (a template's
            // phases, or a role with no --spec). spec/baton.md §2 states the underlying reason a
            // --spec-omitted redispatch cannot take an attachment; record-once, not restated here.
            throw new CliArgumentException(
                "'baton redispatch' without --spec reuses the parent room's already-built prompt "
                + "verbatim, so --attach has no prompt to attach into. Pass --attach only together with --spec.",
                "pass --spec <amended-brief> --attach <file>, or drop --attach to reuse the parent's prompt as-is.");
        }

        WorkflowDefinition definition;
        WorkerBindingConfigEntry entry;
        if (options.SpecFilePath is null)
        {
            // No amended brief: reuse the parent's already-built prompt and step shape verbatim,
            // overriding only the axes the operator actually passed.
            var parentWorkflowPath = Path.Combine(options.ParentRoomDirectoryPath, WorkflowFileName);
            definition = await WorkflowDefinitionParser.LoadFromFileAsync(parentWorkflowPath, cancellationToken).ConfigureAwait(false);
            entry = InheritBinding(parentEntry, options);
            if (!string.Equals(entry.Adapter, parentEntry.Adapter.Trim().ToLowerInvariant(), StringComparison.Ordinal))
            {
                // Loud, not silent — the one inheritance rule that differs from a fresh dispatch
                // (spec/baton.md §2's grant-carry paragraph).
                Console.Error.WriteLine(
                    $"Warning: --adapter {entry.Adapter} inherits the parent's resolved grant, audit mode and "
                    + "worktree intent unchanged; pass --spec to re-derive them for the new adapter.");
            }
        }
        else
        {
            (definition, entry) = await RebuildFromAmendedSpecAsync(workerName, parentEntry, options, cancellationToken)
                .ConfigureAwait(false);
            // #1499/#1619/#1668: RoleDispatch.Materialize knows nothing of labels, workstreams, or tool shas -- apply
            // InheritBinding's own rule for both here too.
            entry = entry with
            {
                Label = (options.LabelSpecified || options.Label is not null) ? options.Label : parentEntry.Label,
                Workstream = (options.WorkstreamSpecified || options.Workstream is not null) ? options.Workstream : parentEntry.Workstream,
                ToolSha = BatonPaths.TryResolveCurrentToolSha() ?? parentEntry.ToolSha,
                // #1151 is deliberately NOT restated here: RebuildFromAmendedSpecAsync hands the same
                // ResolveSkills list to RoleSpecMaterializer, and RoleDispatch.ToBinding sets Skills from
                // it (including the --skill "" clear, which resolves to an empty list and lands as null).
                // Assigning it a second time here made the one that matters undeletable-by-test -- #1941
                // review MEDIUM: either assignment alone satisfied every arm, so neither was discriminated.
            };
        }

        if (options.Timeout is { } timeoutOverride && timeoutOverride > TimeSpan.FromMinutes(DispatchOptionsParser.WarnTimeoutMinutes))
        {
            Console.Error.WriteLine(
                $"Warning: --timeout {(int)timeoutOverride.TotalMinutes} exceeds "
                + $"{DispatchOptionsParser.WarnTimeoutMinutes} minutes (2h) — a typo here can strand a lane for a long time.");
        }

        Directory.CreateDirectory(options.RoomDirectoryPath);

        // #1619: the navigational half of the ruling -- the redispatched room's workstream is whatever
        // InheritBinding just resolved onto `entry` (inherited from the parent, cleared, or overridden),
        // not the raw `options.Workstream` a bare `baton redispatch` never passes at all.
        WorkstreamJunctionLinker.CreateIfRequested(entry.Workstream, options.RoomDirectoryPath);

        // #1576: the same copy DispatchCommand's own --attach path runs -- reached only via the
        // --spec + --attach combination refused above, so this is unreachable on the bare-redispatch path.
        RoleSpecMaterializer.CopyAttachmentsIntoRoom(options.Attachments, options.RoomDirectoryPath);

        Console.Out.WriteLine($"Room directory: {options.RoomDirectoryPath}");
        Console.Out.WriteLine($"Redispatched from: {options.ParentRoomDirectoryPath}");

        // Lineage (#1441): recorded on the room marker, the room-metadata home spec/baton.md §2 already
        // names -- not a new parallel file. The parent's own execution id is cheap here: it is already
        // on parentTerminal, read above for the Succeeded/warning check.
        await InteractiveSessionMaterializer.WriteWorkflowRoomMarkerAsync(
            options.RoomDirectoryPath,
            parentRoomDirectoryPath: options.ParentRoomDirectoryPath,
            parentExecutionId: parentTerminal?.Steps.FirstOrDefault()?.Execution,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var workflowFilePath = Path.Combine(options.RoomDirectoryPath, WorkflowFileName);
        var bindingsFilePath = Path.Combine(options.RoomDirectoryPath, BindingsFileName);
        await WorkflowDefinitionWriter.SaveToFileAsync(definition, workflowFilePath, cancellationToken).ConfigureAwait(false);
        await WorkerBindingConfigWriter.SaveToFileAsync(
            new Dictionary<string, WorkerBindingConfigEntry> { [workerName] = entry }, bindingsFilePath, cancellationToken)
            .ConfigureAwait(false);

        var workspace = entry.WorkingDirectory ?? entry.Worktree?.Repository ?? Directory.GetCurrentDirectory();
        // Register: true -- same rationale as DispatchCommand's own RunOptions construction (spec/baton.md §8, #1657).
        var runOptions = new RunOptions(
            workflowFilePath, bindingsFilePath, options.RoomDirectoryPath, ProjectRootDirectory: workspace, Register: true);
        var result = await RunCommand.ExecuteAsync(runOptions, adapters, cancellationToken: cancellationToken).ConfigureAwait(false);

        // #1895: the same stamp `baton dispatch` runs, always on its no-step arm -- this verb runs no
        // verify step at all. What the removal buys is settled in spec/baton.md §9, under #1882's
        // `--verify-cmd` contract.
        await VerdictInstrumentStamp.ApplyAsync(options.RoomDirectoryPath, result, verifyStep: null).ConfigureAwait(false);

        return result;
    }

    /// <summary>
    /// The binding-inheritance rule for an unchanged spec: start from the parent's exact entry (grant,
    /// worktree intent, contract, already-built prompt included — minus #1882's verify-results
    /// paragraph, the one exception, stripped for the reason
    /// <see cref="RoleDispatch.WithoutVerifyResultsParagraph"/> states) and apply only the axes
    /// <paramref name="options"/> actually set, falling back to the parent's own recorded value for
    /// every axis left null -- adapter, model, effort, workspace, timeout. Public so it is unit-testable
    /// against a hand-built <see cref="WorkerBindingConfigEntry"/> without a room on disk, the same
    /// reusability <see cref="RoleDispatch.ToBinding"/> is public for. #1623 added <c>--token-budget</c>
    /// to the same axis list -- null keeps the parent's.
    /// </summary>
    public static WorkerBindingConfigEntry InheritBinding(WorkerBindingConfigEntry parentEntry, RedispatchOptions options)
    {
        ArgumentNullException.ThrowIfNull(parentEntry);
        ArgumentNullException.ThrowIfNull(options);

        var adapter = InheritedAdapter(parentEntry, options);
        var (model, effort) = InheritedAxes(parentEntry, options);

        var workingDirectory = parentEntry.WorkingDirectory;
        var worktree = parentEntry.Worktree;
        if (options.WorkspaceDirectory is { } newWorkspace)
        {
            // The parent recorded its workspace in exactly one of these two fields -- ToBinding's
            // grant-audit branch decides which -- so override whichever is actually populated.
            if (parentEntry.WorkingDirectory is not null)
            {
                workingDirectory = newWorkspace;
            }

            if (parentEntry.Worktree is { } parentWorktree)
            {
                worktree = parentWorktree with { Repository = newWorkspace };
            }
        }

        return WithResolvedStamps(parentEntry with
        {
            Adapter = adapter,
            Model = model,
            Effort = effort,
            WorkingDirectory = workingDirectory,
            Worktree = worktree,
            Timeout = options.Timeout ?? parentEntry.Timeout,
            TokenBudget = options.TokenBudget ?? parentEntry.TokenBudget,
            MaxToolSteps = options.MaxToolSteps ?? parentEntry.MaxToolSteps,
            BilledRateLimit = options.BilledRateLimit ?? parentEntry.BilledRateLimit, // #1691
            VerifyCommandOverride = options.VerifyCommand ?? parentEntry.VerifyCommandOverride,
            Label = (options.LabelSpecified || options.Label is not null) ? options.Label : parentEntry.Label, // #1499, spec/baton.md §2
            Workstream = (options.WorkstreamSpecified || options.Workstream is not null) ? options.Workstream : parentEntry.Workstream, // #1619, spec/baton.md §2
            ToolSha = BatonPaths.TryResolveCurrentToolSha() ?? parentEntry.ToolSha, // #1668
            Skills = ResolveSkills(parentEntry, options), // #1151, spec/baton.md §9
            // Adapter-derived, not role-derived, so it CAN be recomputed here — carrying the parent's
            // value across a vendor swap would stream-json a claude/agy worker (or text-mode a non-streaming one).
            // Grant/GrantAuditMode/worktree intent stay inherited: spec/baton.md §2 states why.
            StreamJson = RoleDispatch.StreamsJson(adapter),
            // #1895: the one thing the inherited prompt does NOT carry across -- RoleDispatch's own
            // method doc has the reasoning, and spec/baton.md §2 records the exception to "verbatim".
            PromptTemplate = RoleDispatch.WithoutVerifyResultsParagraph(parentEntry.PromptTemplate),
            // A redispatch is a fresh worker turn, never a continuation of the parent's own session.
            SessionId = null,
            ResumeSession = false,
        }, parentEntry, options);
    }

    /// <summary>
    /// The normalized adapter this redispatch binds onto — <c>--adapter</c> if given, else the
    /// parent's. Normalized exactly as <see cref="RoleDispatch.ToBinding"/> normalizes its winner: the
    /// registry lookup is case-sensitive, so an unnormalized "Claude" would fail at resolve time, after
    /// the room's files were already written.
    /// </summary>
    internal static string InheritedAdapter(WorkerBindingConfigEntry parentEntry, RedispatchOptions options) =>
        (options.Adapter ?? parentEntry.Adapter).Trim().ToLowerInvariant();

    /// <summary>
    /// <see cref="RoleDispatch.ToBinding"/>'s vendor-swap axis rule (#1082, spec/baton.md §2), as one
    /// predicate BOTH redispatch paths cross: an explicit <c>--model</c>/<c>--effort</c> wins, and with
    /// none, swapping the vendor drops the parent's rather than carrying it across.
    /// <para>
    /// #1927 review HIGH, sub-note: the amended-spec path used to apply the <c>??</c> half without this
    /// one, passing the parent's model into <see cref="RoleDispatch.ToBinding"/> as an explicit
    /// <c>modelOverride</c> — which outranks that method's own copy of the rule, so the swap leaked the
    /// previous vendor's model into the child's argv. Applying the rule in one shared place is what
    /// #1686 review F2's one-path fix already cost this command once.
    /// </para>
    /// </summary>
    internal static (string? Model, string? Effort) InheritedAxes(
        WorkerBindingConfigEntry parentEntry, RedispatchOptions options)
    {
        var vendorSwapped = IsVendorSwap(parentEntry, options);
        return (
            options.Model ?? (vendorSwapped ? null : parentEntry.Model),
            options.Effort ?? (vendorSwapped ? null : parentEntry.Effort));
    }

    private static bool IsVendorSwap(WorkerBindingConfigEntry parentEntry, RedispatchOptions options) =>
        !string.Equals(
            InheritedAdapter(parentEntry, options),
            parentEntry.Adapter.Trim().ToLowerInvariant(),
            StringComparison.Ordinal);

    /// <summary>
    /// #1927 review HIGH: re-resolves the four DISPLAY stamps for the redispatched entry, on both
    /// paths. They are adapter-derived exactly like <see cref="WorkerBindingConfigEntry.StreamJson"/>
    /// above, so carrying them verbatim across a vendor swap made a redispatched agy room display
    /// (<c>FleetStatusTool</c>), stamp (<c>RoomBindingStamps</c>) and ledger (<c>CostLedgerStore</c>)
    /// the parent's <c>opus</c> — precisely because the axis rule nulls <c>Model</c> on a swap and
    /// every one of those readers falls back to <c>ModelResolved</c> when it is null.
    /// <para>
    /// Keyed PER AXIS, and only on the axes that actually moved: on the same vendor with no override
    /// the parent's stamps are still true, and re-resolving them there could only downgrade a
    /// <see cref="BindingValueSource.Requested"/> source to
    /// <see cref="BindingValueSource.ResolvedDefault"/> — the child inherited the value, it did not
    /// fall back to it.
    /// </para>
    /// <para>
    /// #1927 re-review MEDIUM corrects that "per axis" for the one case where the two axes are NOT
    /// independent: <see cref="RoleDispatch.ResolveEffortStamp"/>'s third rung reads agy's effort off
    /// the RESOLVED MODEL ID's suffix, so moving the model axis moves the correct effort answer too.
    /// A same-vendor <c>--model gemini-3.8-flash-low</c> over an agy parent stamped <c>high</c> kept
    /// that <c>high</c> while the CLI ran at <c>low</c> — an effort the room displays and the model id
    /// contradicts, which is worse than the bare vendor #1927 set out to fix.
    /// </para>
    /// <para>
    /// The trigger is <c>--model</c> given AND the parent recorded NO effort of its own, not "the model
    /// stamp moved": re-resolving whenever the model moves is what would reintroduce the downgrade the
    /// paragraph above warns about (a parent with <c>EffortResolved="careful"</c>/<c>requested</c>
    /// re-stamped <c>careful</c>/<c>resolved-default</c>). A null <c>parentEntry.Effort</c> is exactly
    /// the room the suffix rung exists for: <see cref="RoleDispatch.ToBinding"/> leaves <c>Effort</c>
    /// null only when nothing was requested and no tier supplied one, so nothing requested can be lost
    /// here.
    /// </para>
    /// Public for the same reason <see cref="InheritBinding"/> is: unit-testable against a hand-built
    /// entry, without a room on disk.
    /// </summary>
    public static WorkerBindingConfigEntry WithResolvedStamps(
        WorkerBindingConfigEntry entry, WorkerBindingConfigEntry parentEntry, RedispatchOptions options)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(parentEntry);
        ArgumentNullException.ThrowIfNull(options);

        var vendorSwapped = IsVendorSwap(parentEntry, options);
        var (modelResolved, modelSource) = options.Model is null && !vendorSwapped
            ? (parentEntry.ModelResolved, parentEntry.ModelSource)
            : RoleDispatch.ResolveModelStamp(entry.Adapter, options.Model, entry.Model);
        // The one axis interaction, stated in this method's own remarks: agy's effort rung is a
        // function of `modelResolved`, so a same-vendor --model with nothing requested on the effort
        // axis must re-read the suffix off the NEW id rather than inherit an answer the id contradicts.
        var effortFollowsTheModel = options.Model is not null && parentEntry.Effort is null;
        var (effortResolved, effortSource) = options.Effort is null && !vendorSwapped && !effortFollowsTheModel
            ? (parentEntry.EffortResolved, parentEntry.EffortSource)
            : RoleDispatch.ResolveEffortStamp(entry.Adapter, options.Effort, entry.Effort, modelResolved);

        return entry with
        {
            ModelResolved = modelResolved,
            ModelSource = modelSource,
            EffortResolved = effortResolved,
            EffortSource = effortSource,
        };
    }

    /// <summary>
    /// #1151's inheritance rule, one predicate used by BOTH redispatch paths. spec/baton.md §9 is the
    /// register for the rule and for why replace-not-append is the right default; in short,
    /// <c>--skill</c> absent inherits, <c>--skill ""</c> clears, any <c>--skill &lt;name&gt;</c> replaces
    /// wholesale.
    /// <para>
    /// Keyed on <see cref="RedispatchOptions.SkillsSpecified"/>, not on the list being non-empty: those
    /// two differ in exactly the case the clear token exists for.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<string>? ResolveSkills(WorkerBindingConfigEntry parentEntry, RedispatchOptions options) =>
        options.SkillsSpecified ? options.Skills : parentEntry.Skills;

    /// <summary>The <c>--spec</c>-given path: rebuilds through <see cref="RoleDispatch.Materialize"/>, spec/baton.md §2's named primitive.</summary>
    private static async Task<(WorkflowDefinition Definition, WorkerBindingConfigEntry Entry)> RebuildFromAmendedSpecAsync(
        string workerName, WorkerBindingConfigEntry parentEntry, RedispatchOptions options, CancellationToken cancellationToken)
    {
        if (!File.Exists(options.SpecFilePath))
        {
            throw new CliArgumentException($"Spec file '{options.SpecFilePath}' does not exist.");
        }

        try
        {
            var role = WorkerRoleCatalog.For(workerName);
            var spec = await File.ReadAllTextAsync(options.SpecFilePath!, cancellationToken).ConfigureAwait(false);
            var workspace = options.WorkspaceDirectory ?? parentEntry.WorkingDirectory ?? parentEntry.Worktree?.Repository;
            // #1927 review HIGH, sub-note: the SAME vendor-swap axis rule the no-spec path applies.
            // These reach ToBinding as explicit overrides, which outrank its own copy of the rule --
            // so without this the swap hands the new vendor the previous one's model as real argv.
            var (inheritedModel, inheritedEffort) = InheritedAxes(parentEntry, options);

            // #1576: the same seam DispatchCommand's role path uses -- --attach validation and the
            // spec/grant lint (#1500) now apply here too, rather than the amended-spec path skipping
            // both by calling RoleDispatch.Materialize directly.
            var (definition, bindings) = RoleSpecMaterializer.Materialize(
                role, spec,
                adapterOverride: InheritedAdapter(parentEntry, options),
                workingDirectory: workspace,
                modelOverride: inheritedModel,
                effortOverride: inheritedEffort,
                outputOverride: options.OutputPath,
                timeoutOverride: options.Timeout ?? parentEntry.Timeout,
                attachments: options.Attachments,
                roomDirectoryPath: options.RoomDirectoryPath,
                tokenBudgetOverride: options.TokenBudget ?? parentEntry.TokenBudget,
                maxToolStepsOverride: options.MaxToolSteps ?? parentEntry.MaxToolSteps,
                // #1691: threaded on the amended-spec path too, which is exactly where #1686 review F2
                // found --max-tool-steps silently dropped. Both paths, or the override does not survive
                // a redispatch.
                billedRateLimitOverride: options.BilledRateLimit ?? parentEntry.BilledRateLimit,
                verifyCommandOverride: options.VerifyCommand ?? parentEntry.VerifyCommandOverride,
                // #1151: resolved and requirement-checked by RoleDispatch.ToBinding on this path too,
                // so an amended-spec redispatch inheriting a parent's skill that has since been deleted
                // refuses here rather than dispatching without it.
                skills: ResolveSkills(parentEntry, options));

            // #1927 review HIGH: both paths re-resolve the display stamps through the same rule --
            // ToBinding stamped them from the inherited axes above, which reach it as overrides and so
            // read as "requested" even when the child merely inherited them.
            return (definition, WithResolvedStamps(bindings[role.Id], parentEntry, options));
        }
        catch (Exception ex) when (ex is FileNotFoundException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            // Same translation DispatchCommand.MaterializeAsync applies: a catalog fault must reach
            // Program's typed boundary as a CliArgumentException, never a raw crash.
            throw new CliArgumentException(ex.Message);
        }
    }

    /// <summary>
    /// #1623 re-review U1: the escape a verify-failed or arrested Indeterminate parent's refusal
    /// points at — a fresh `baton dispatch` (a *new* room, whose own ExecutionRequestAccepted clears
    /// the projector's Indeterminate tracking, <see cref="Baton.Projection.StateProjector"/>), carrying
    /// the parent's own recorded flags forward the same way <see cref="RebuildFromAmendedSpecAsync"/>
    /// does for an ordinary redispatch. Never "re-dispatch the parent" — that names the refused command
    /// itself, a closed loop this method exists to stop printing.
    /// </summary>
    private static string DescribeFreshDispatchRemedy(string workerName, WorkerBindingConfigEntry parentEntry)
    {
        var workspace = parentEntry.WorkingDirectory ?? parentEntry.Worktree?.Repository;
        var timeoutMinutes = Math.Max(1, (int)Math.Ceiling(parentEntry.Timeout.TotalMinutes));
        var flags = $"--adapter {parentEntry.Adapter} --timeout {timeoutMinutes}";
        if (parentEntry.Model is { } model)
        {
            flags += $" --model {model}";
        }

        if (workspace is { } dir)
        {
            flags += $" --workspace {dir}";
        }

        return $"`baton dispatch {workerName} --spec <brief> {flags}`";
    }
}
