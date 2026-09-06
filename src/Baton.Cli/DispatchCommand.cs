using System.Text.Json;
using Baton.Accounting;
using Baton.Vendors;
using Baton.Domain;
using Baton.Runway;
using Baton.Status;
using Baton.Templates;

namespace Baton.Cli;

/// <summary>
/// <c>baton dispatch &lt;name&gt;</c> (#900 role dispatch, widened for rung-3 composed templates, #920):
/// resolves <see cref="DispatchOptions.Name"/> as either a worker role (single-step, via
/// <see cref="RoleDispatch"/>, against a <c>--spec</c>) or a workflow template (a composed multi-phase
/// DAG, via <see cref="WorkflowTemplateComposer"/>) — one namespace, decision 0047 §5. Either way it
/// persists the same <c>workflow.json</c>/<c>bindings.json</c> and hands them to
/// <see cref="RunCommand.ExecuteAsync"/>, so outputs are contract-checked by the very pump <c>baton run</c>
/// drives. A template that declares a capture step (0047 §4) gets its base ref — the workspace HEAD at
/// this moment — captured and injected here, the git-aware entrypoint, before the run begins.
/// </summary>
public static class DispatchCommand
{
    private const string WorkflowFileName = "workflow.json";
    private const string BindingsFileName = "bindings.json";

    /// <exception cref="CliArgumentException">
    /// <paramref name="options"/> names neither a role nor a template (or names both), a role without a
    /// <c>--spec</c> or a template with one, a missing spec file, a non-git workspace behind a capture
    /// step, or a catalog that is itself unreadable — every resolution failure is translated so it exits
    /// cleanly through <c>Program</c>'s typed boundary rather than as a raw stack trace.
    /// </exception>
    /// <param name="workspaceDirectory">
    /// The git workspace a capture step operates in — where its base ref is captured <em>and</em> where
    /// its <c>git diff</c> runs (the injection pins both to this one directory, so they cannot diverge).
    /// The process directory in production; left overridable so a test can point a capture at a repo it
    /// controls rather than racing on the process-global current directory. Null resolves to the cwd.
    /// Note it governs the capture step only — a role phase's own working directory is unchanged.
    /// </param>
    /// <param name="evaluateRunway">
    /// #1848's admission gate, per adapter tag. Null (production) reads the operator's thresholds from
    /// <c>settings.json</c> and each vendor's latest harvested snapshot off disk; a test passes its own
    /// so the gate's arms are drivable without a <c>~/.baton</c> snapshot or a vendor CLI.
    /// </param>
    /// <param name="reservationPolicy">
    /// #1896's cross-dispatch reservation policy. Null (production) resolves the one named in
    /// <c>settings.json</c> through <see cref="RunwayReservationPolicies.Resolve"/>; a test passes its
    /// own, which is what makes the policy swappable without retuning the shipped constants.
    /// </param>
    public static async Task<CommandResult> ExecuteAsync(
        DispatchOptions options,
        IReadOnlyDictionary<string, IWorkerAdapter> adapters,
        CancellationToken cancellationToken = default,
        string? workspaceDirectory = null,
        Func<string, RunwayDecision>? evaluateRunway = null,
        IRunwayReservationPolicy? reservationPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(adapters);

        // #1645 half (1) of the drain ruling: refuse while tool-refresh is draining -- see DrainMarker
        // for why this verb is in the refusing population and `baton status` deliberately is not.
        // Placed at the very top, ahead of the --list-capabilities early return below: that path starts
        // no engine and creates no room, so refusing it is not what the marker is for, but a dispatch
        // that is about to be blocked should say so before printing a capabilities dump the operator
        // will not get to use. It is also ahead of Directory.CreateDirectory (below), which is the point
        // -- refresh.py's drain predicate must never see a half-provisioned room this invocation made.
        // (Program's typed boundary does create the room afterwards to leave a ValidationRefused
        // terminal.json in it; a room carrying terminal.json is terminal, so the predicate skips it.)
        if (DrainMarker.RefusalMessage("dispatch") is { } drainRefusal)
        {
            throw new CliArgumentException(drainRefusal, DrainMarker.AbortInvocation);
        }

        // #1645 item 2: a loud, non-fatal WARN when the installed `baton` has drifted behind the repo
        // checkout's current release — see InstalledVersionDrift's own remarks for why this never
        // touches the exit code, and why it borrows Staleness's verdict shape rather than DriftGrace's
        // grace-window one.
        if (InstalledVersionDrift
            .Evaluate(options.RepoPath, VersionInfo.GetVersion(System.Reflection.Assembly.GetExecutingAssembly()))
            .WarnLine() is { } dispatchDriftWarning)
        {
            Console.Error.WriteLine(dispatchDriftWarning);
        }

        if (options.ListCapabilities)
        {
            PrintCapabilities(Console.Out);
            var snapshotId = new WorkflowDefinitionSnapshotId("capabilities");
            return new CommandResult(
                new FlowState(snapshotId, [], WorkflowStatus.Terminal),
                new WorkflowDefinitionSnapshot(snapshotId, new WorkflowTemplateId("capabilities"), 1, []));
        }

        // #1848 review: --override-runway is the audited bypass of a gate a --continue dispatch never
        // consults, so together they are a refusal rather than a no-op. Accepting the flag and dropping
        // it is the one failure the record cannot survive: the operator would read "override recorded"
        // into a room whose bindings.json says nothing. Refused here, ahead of the continuation resolve
        // below, so the message names the flag combination rather than whatever the rehire happens to
        // complain about first — and, like every other pre-provision refusal, before any directory is
        // created. spec/baton.md §7, "Runway hold (#1848)".
        if (options.OverrideRunwayReason is not null && options.ContinueFromRoomDirectoryPath is not null)
        {
            throw new CliArgumentException(
                "'--override-runway' does not apply to a '--continue' dispatch — rehiring a worker the "
                + "fleet already admitted consults no runway gate, so the flag would bypass nothing and "
                + "its reason would be recorded nowhere.",
                "drop --override-runway, or drop --continue and dispatch cold if the runway hold is what you mean to override.");
        }

        var workspace = options.WorkspaceDirectory ?? workspaceDirectory ?? Directory.GetCurrentDirectory();
        var (definition, bindings) = await MaterializeAsync(options, workspace, cancellationToken).ConfigureAwait(false);

        // #1499: stamped onto every entry -- a composed template's bindings.json holds one per phase.
        if (options.Label is not null)
        {
            bindings = bindings.ToDictionary(
                pair => pair.Key, pair => pair.Value with { Label = options.Label }, StringComparer.Ordinal);
        }

        // #1619: same stamp-onto-every-entry rule as Label immediately above.
        if (options.Workstream is not null)
        {
            bindings = bindings.ToDictionary(
                pair => pair.Key, pair => pair.Value with { Workstream = options.Workstream }, StringComparer.Ordinal);
        }

        // #1668: record the active tool commit SHA on each binding for room version tracking.
        if (BatonPaths.TryResolveCurrentToolSha() is { } toolSha)
        {
            bindings = bindings.ToDictionary(
                pair => pair.Key, pair => pair.Value with { ToolSha = toolSha }, StringComparer.Ordinal);
        }

        // #1381: --continue rehires the veteran that ran in a prior terminal room — resolved after the
        // binding has already picked its own adapter (so a mismatch refusal compares the ACTUAL
        // resolved adapter, not just a possibly-null options.Adapter), but before Directory.CreateDirectory
        // below, so a refusal here still lands as a clean pre-ledger ValidationRefused (Program's typed
        // boundary) with no half-provisioned room left behind — the same placement rationale the
        // drain-marker/version-drift checks above already follow. MaterializeTemplateAsync already
        // refused a template dispatch above, so bindings here is always the single-entry dictionary a
        // role dispatch produces.
        ContinuationProvenance? continuation = null;
        if (options.ContinueFromRoomDirectoryPath is not null)
        {
            var (continuedWorkerName, continuedEntry) = bindings.Single();
            WorkerBindingConfigEntry resumedEntry;
            (resumedEntry, continuation) = await ResolveContinuationAsync(
                options.ContinueFromRoomDirectoryPath, continuedEntry, cancellationToken).ConfigureAwait(false);
            bindings = new Dictionary<string, WorkerBindingConfigEntry> { [continuedWorkerName] = resumedEntry };
        }

        // R1 (#1354/#1380): disclose the consequence up front, before the run starts, whenever
        // RoleDispatch.ToBinding declared a fresh worktree for an audited role — the worker then never
        // sees uncommitted or staged changes in `workspace`, only what HEAD already had (finding 5).
        string? workspaceFact = null;
        if (bindings.Values.Any(b => b.Worktree is not null))
        {
            var headSha = await WorkspaceHead.CaptureAsync(workspace, cancellationToken).ConfigureAwait(false);
            var shortSha = headSha.Length > 8 ? headSha[..8] : headSha;
            workspaceFact = $"Workspace: worktree of {workspace} at HEAD ({shortSha}) — uncommitted changes are not visible to the worker";
        }

        // #1442: warn-don't-refuse above the caution threshold — rationale in spec/baton.md §2.
        if (options.Timeout is { } timeoutOverride && timeoutOverride > TimeSpan.FromMinutes(DispatchOptionsParser.WarnTimeoutMinutes))
        {
            Console.Error.WriteLine(
                $"Warning: --timeout {(int)timeoutOverride.TotalMinutes} exceeds "
                + $"{DispatchOptionsParser.WarnTimeoutMinutes} minutes (2h) — a typo here can strand a lane for a long time.");
        }

        // #1882: the --verify-cmd lines are re-parsed HERE rather than at the step itself, for the same
        // placement reason the --continue check above states: a refusal ahead of Directory.CreateDirectory
        // lands as a clean pre-ledger ValidationRefused with no half-provisioned room behind it.
        // DispatchOptionsParser.ParseVerifyCommand already validated every line, so this is unreachable
        // through the CLI -- it is reachable through a hand-built DispatchOptions, which is exactly what
        // an in-process caller (and every test) constructs.
        var verifyCommands = ParseVerifyCommands(options.VerifyCommands);

        // #1848: the runway hold — the last thing checked before this invocation provisions anything,
        // for the same reason the drain refusal is the first: a refusal here must leave no
        // half-provisioned room behind (Program's typed boundary still lands a ValidationRefused
        // terminal.json in the room it creates, the same shape every other pre-run refusal has).
        bindings = await ApplyRunwayGateAsync(options, bindings, workspace, evaluateRunway, reservationPolicy, cancellationToken)
            .ConfigureAwait(false);

        Directory.CreateDirectory(options.RoomDirectoryPath);

        // #1381: cross-room provenance -- the same room-marker lineage fields #1441's redispatch
        // already writes to, extended with the one new fact (ContinuedSessionId) that tells "continued
        // from" apart from "redispatched from" (record-once: no second marker file). A no-op when
        // --continue was never passed.
        if (continuation is not null)
        {
            await InteractiveSessionMaterializer.WriteWorkflowRoomMarkerAsync(
                options.RoomDirectoryPath,
                parentRoomDirectoryPath: continuation.ParentRoomDirectoryPath,
                parentExecutionId: continuation.ParentExecutionId,
                continuedSessionId: continuation.SessionId,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        // #1619: the navigational half of the ruling -- a no-op when --workstream was never passed.
        WorkstreamJunctionLinker.CreateIfRequested(options.Workstream, options.RoomDirectoryPath);

        // #1500/#1576: Copy attached context files into the room before the worker starts, via the
        // seam RedispatchCommand's own --attach path now shares. Attachment content is operator-supplied
        // and inbound: it is never scanned and never published, because the pusher's gather_deliverables
        // reads only terminal.json's declared step outputs (not a directory walk), and an attachment is
        // never a declared output of any step (#1500 second-reader LOW-6 — "never passes the gate" read
        // as either "never scanned" or "the gate withholds it"; state the mechanism instead of the
        // ambiguous phrase).
        RoleSpecMaterializer.CopyAttachmentsIntoRoom(options.Attachments, options.RoomDirectoryPath);

        var primaryOutputName = definition.Steps.FirstOrDefault()?.Outputs.FirstOrDefault() ?? "output";
        Console.Out.WriteLine($"Room directory: {options.RoomDirectoryPath}");
        if (workspaceFact is not null)
        {
            Console.Out.WriteLine(workspaceFact);
        }

        // #1355: the least-privilege grant profile actually in force, so the invoking agent can relay
        // it to its own permission layer honestly. Extends the same printing seam as
        // workspaceFact/output-path above rather than building a second one -- one line per bound
        // worker whose adapter actually consumes a grant, which for a single-role dispatch (the common
        // case) is the one line the issue asks for.
        //
        // F2: a grant is only "what the worker can do" for an adapter that consumes it --
        // WorkerBindingResolver.cs:137-141 already draws this population as `is IPermissionGrantTranslator`
        // (checked against this same `adapters` registry, the one WorkerBindingResolver.Resolve is
        // handed downstream via RunCommand). A binding bound to an adapter outside that population
        // (e.g. a composed template's capture step, which spawns git directly) never had its grant
        // consumed, so its "no-shell"/"no-network" would be false in the only sense an invoking agent's
        // permission layer cares about. Skip it -- no placeholder line either.
        var translatorBindings = bindings
            .Where(pair => adapters.TryGetValue(pair.Value.Adapter, out var boundAdapter) && boundAdapter is IPermissionGrantTranslator)
            .ToList();
        var multipleWorkers = translatorBindings.Count > 1;
        foreach (var (workerName, binding) in translatorBindings)
        {
            var label = multipleWorkers ? $"Grant ({workerName})" : "Grant";
            Console.Out.WriteLine($"{label}: {DescribeGrant(binding)}");
        }

        // #1512: surface the worker's discovered skill roster so a brief that names an absent skill is
        // caught by the operator — printed after the room directory already exists (created at :75
        // above; nothing about this ordering makes the room avoidable) and after the Grant lines.
        // Excludes the capture step: like F2's Grant exclusion (:90-96 above), it spawns git directly
        // rather than running a skill-bearing prompt. This is a DELIBERATELY parallel predicate, not
        // the same one reused — F2 draws its population structurally (`is IPermissionGrantTranslator`)
        // because a grant line is meaningless for an adapter that never consumes a grant; skill
        // discovery has no such dependency; an adapter can discover skills whether or not it also
        // translates a permission grant. Requiring IPermissionGrantTranslator here would wrongly hide
        // a real roster behind an unrelated capability. The two populations already diverge in the test
        // suite (ContractOutputWorkerAdapter is not an IPermissionGrantTranslator, so a plain-adapter
        // dispatch prints a Skills line with no matching Grant line) — that is expected, not a bug.
        var skillBindings = bindings
            .Where(pair => !string.Equals(pair.Value.Adapter, WorkflowTemplateComposer.CaptureAdapter, StringComparison.Ordinal))
            .Where(pair => adapters.ContainsKey(pair.Value.Adapter))
            .ToList();
        var multipleSkillWorkers = skillBindings.Count > 1;
        foreach (var (workerName, binding) in skillBindings)
        {
            var boundAdapter = adapters[binding.Adapter];

            // #1941 review MEDIUM: a binding that declares its own skill set gets exactly that set --
            // WorkerInvocation.Skills REPLACES the workspace scan -- so printing the scan here would
            // name packages the worker will not receive and omit the ones it will, on the one dispatch
            // where the operator was most explicit about what they wanted. The names are what the
            // binding carries and what a redispatch inherits, so the names are what this reports;
            // resolution already happened (RoleDispatch.ToBinding refused an unknown or unsatisfiable
            // one before this room existed), which is why no rung or realization suffix is added.
            if (binding.Skills is { Count: > 0 } declaredSkills)
            {
                var declaredLabel = multipleSkillWorkers ? $"Skills ({workerName}, declared)" : "Skills (declared)";
                Console.Out.WriteLine($"{declaredLabel}: {string.Join(", ", declaredSkills)}");
                continue;
            }

            // H1 (#1512 second-reader finding): for a worktree-provisioned binding, WorkingDirectory
            // is null at this point (WorktreeWorkspaces.cs refuses a binding that sets both) and the
            // worktree the worker will actually run in does not exist yet — it is provisioned later,
            // inside RunCommand, as a fresh checkout at the binding's Ref. Scanning
            // binding.Worktree.Repository instead means scanning the SOURCE repo's raw filesystem,
            // untracked/uncommitted files included — the same gap workspaceFact discloses above for
            // uncommitted changes generally. Rather than assert a roster the worker is not guaranteed
            // to have, say plainly what was scanned.
            string label;
            string targetDirectory;
            if (binding.Worktree is { } worktree)
            {
                targetDirectory = worktree.Repository;
                label = multipleSkillWorkers
                    ? $"Skills ({workerName}, from {worktree.Repository}; the worker runs in a fresh worktree at HEAD)"
                    : $"Skills (from {worktree.Repository}; the worker runs in a fresh worktree at HEAD)";
            }
            else
            {
                targetDirectory = binding.WorkingDirectory ?? workspace;
                label = multipleSkillWorkers ? $"Skills ({workerName})" : "Skills";
            }

            var caps = await boundAdapter.DiscoverCapabilitiesAsync(targetDirectory, cancellationToken).ConfigureAwait(false);
            var skills = caps.Items
                .Where(i => string.Equals(i.Kind, "skill", StringComparison.OrdinalIgnoreCase))
                .Select(i => i.Name)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var skillsText = skills.Count > 0 ? string.Join(", ", skills) : "none discovered";
            Console.Out.WriteLine($"{label}: {skillsText}");
        }

        // R4 (#1354/#1380): the execution-scoped artifact path isn't known until dispatch actually runs,
        // so without --output the only truthful thing to print beforehand is the artifacts directory
        // itself, labeled as a directory — not a fabricated per-execution file path that will not exist
        // (finding 4).
        if (options.OutputPath is not null)
        {
            Console.Out.WriteLine($"Output path: {options.OutputPath}");
        }
        else
        {
            var artifactsDirectory = Path.Combine(options.RoomDirectoryPath, Baton.Artifacts.ArtifactManager.ArtifactsDirectoryName);
            Console.Out.WriteLine($"Artifacts directory: {artifactsDirectory} (each execution's outputs land in its own subdirectory under it)");
        }

        Console.Out.WriteLine($"Completion signal: process exit code or {Path.Combine(options.RoomDirectoryPath, TerminalSentinelWriter.TerminalSentinelFileName)}");

        var workflowFilePath = Path.Combine(options.RoomDirectoryPath, WorkflowFileName);
        var bindingsFilePath = Path.Combine(options.RoomDirectoryPath, BindingsFileName);
        await WorkflowDefinitionWriter.SaveToFileAsync(definition, workflowFilePath, cancellationToken).ConfigureAwait(false);
        await WorkerBindingConfigWriter.SaveToFileAsync(bindings, bindingsFilePath, cancellationToken).ConfigureAwait(false);

        // Register: true -- rationale is spec/baton.md §8 (#1657).
        var runOptions = new RunOptions(
            workflowFilePath, bindingsFilePath, options.RoomDirectoryPath, options.WorkflowId,
            ProjectRootDirectory: workspace, Register: true);
        // #1841: capture a vendor-reported id while stdout is flowing. Reading the finished log is
        // not reliable here: the init envelope is the first line and can be rolled out of a long
        // execution's bounded log. The callback is keyed by the same binding name RunCommand resolved,
        // while the adapter remains solely responsible for recognizing its own vendor envelope.
        var capturedSessionIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var captureLock = new object();
        void CaptureSessionId(string workerName, string rawLine)
        {
            if (!bindings.TryGetValue(workerName, out var entry)
                || !adapters.TryGetValue(entry.Adapter, out var adapter)
                || !adapter.TryParseSessionId(rawLine, out var sessionId)
                || sessionId is not { Length: > 0 })
            {
                return;
            }

            lock (captureLock)
            {
                // Last report wins, including a later attempt by the same binding.
                capturedSessionIds[workerName] = sessionId;
            }
        }

        // #1882: the zero-token verify step, run BEFORE the worker's first turn and after the room
        // exists, so its results file is already on disk when the reviewer reads the prompt paragraph
        // that names it. Nothing here can fail the dispatch: a non-zero exit is what the reviewer reads
        // first, and even an unspawnable command is recorded as a result rather than thrown.
        Baton.Mutation.VerifyStep.Outcome? verifyStep = null;
        if (verifyCommands.Count > 0)
        {
            verifyStep = await RunVerifyStepAsync(options, verifyCommands, workspace, cancellationToken)
                .ConfigureAwait(false);
        }

        var result = await RunCommand.ExecuteAsync(
            runOptions, adapters, cancellationToken: cancellationToken, onWorkerStdoutLine: CaptureSessionId)
            .ConfigureAwait(false);

        // #1882: the engine stamps the instruments onto the verdict the worker just wrote, and does so
        // UNCONDITIONALLY -- VerdictInstrumentStamp's own doc has the reasoning for both halves. #1895
        // moved the body out of this file so `baton redispatch` calls the same helper.
        await VerdictInstrumentStamp.ApplyAsync(options.RoomDirectoryPath, result, verifyStep).ConfigureAwait(false);

        if (options.OutputPath is not null && result.State.Status == WorkflowStatus.Terminal)
        {
            CopyPrimaryOutputToOverride(options, result, primaryOutputName);
        }

        // #1841: read-side session id capture -- records the id an adapter's own vendor stream
        // reported (never mints one; see spec/baton.md §3's dispatch entry for why minting one into
        // the frozen CoreDispatchTarget argv would break a #1373 retry) and records it onto THIS
        // room's own bindings.json entry, exactly where a future `dispatch --continue` off this room
        // reads it (ResolveContinuationAsync below). Touches only SessionId -- never ResumeSession,
        // so a --continue child's own chained `ResumeSession: true` (set above, at :119) survives this
        // rewrite unchanged.
        try
        {
            await RecordCapturedSessionIdsAsync(bindings, capturedSessionIds, bindingsFilePath)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The worker outcome is already journaled. Session metadata improves a future dispatch,
            // but losing it must not suppress this dispatch's terminal sentinel or real exit code.
            Console.Error.WriteLine(
                $"Warning: could not record the vendor session id in '{bindingsFilePath}': {ex.Message} "
                + "This dispatch result is unchanged, but the newly reported id was not recorded; "
                + "a later --continue may refuse when no prior session id exists.");
        }

        return result;
    }

    /// <summary>
    /// Re-derives the argv for each <c>--verify-cmd</c> line. Re-parsed rather than carried so the
    /// string an operator reads back off the room and the argv that actually ran cannot diverge; the
    /// call site's own comment states why it happens before the room directory exists.
    /// </summary>
    private static List<Baton.Mutation.VerifyStepCommand> ParseVerifyCommands(IReadOnlyList<string>? commandLines)
    {
        if (commandLines is not { Count: > 0 })
        {
            return [];
        }

        var commands = new List<Baton.Mutation.VerifyStepCommand>(commandLines.Count);
        foreach (var line in commandLines)
        {
            if (!Baton.Mutation.VerifyStepCommandParser.TryParse(line, out var command, out var error))
            {
                throw new CliArgumentException(error!);
            }

            commands.Add(command!);
        }

        return commands;
    }

    /// <summary>
    /// #1882: runs the allowlisted <c>--verify-cmd</c> commands and records them into the room's
    /// artifacts, printing one line per command so an operator watching the dispatch sees the evidence
    /// being gathered rather than a silent pause.
    /// </summary>
    /// <param name="workspace">
    /// The commands' working directory — the tree this review was dispatched against. Deliberately the
    /// workspace and not the worker's own provisioned worktree: that worktree does not exist yet when
    /// this runs (RunCommand provisions it), and the whole point of running before the first turn is
    /// that the results are on disk when the worker starts. The gap that leaves is the same one
    /// <c>workspaceFact</c> already discloses above — a worktree-provisioned worker sees HEAD, while
    /// these commands see the workspace's actual working tree. It is also what
    /// <c>VerifyStepRunner.MissingBuildLockReason</c> is measured against: an arbitrary <c>--workspace</c>
    /// need not be a Baton checkout at all.
    /// </param>
    private static async Task<Baton.Mutation.VerifyStep.Outcome?> RunVerifyStepAsync(
        DispatchOptions options,
        IReadOnlyList<Baton.Mutation.VerifyStepCommand> commands,
        string workspace,
        CancellationToken cancellationToken)
    {
        var artifactsRoot = Path.Combine(options.RoomDirectoryPath, Baton.Artifacts.ArtifactManager.ArtifactsDirectoryName);
        var timeout = options.VerifyTimeout ?? Baton.Mutation.VerifyStepRunner.DefaultTimeout;
        Console.Out.WriteLine(
            $"Verify step: running {commands.Count} command(s) in {workspace} before the worker's first turn "
            + $"(no model involved, {(int)timeout.TotalMinutes}m per command).");

        try
        {
            var outcome = await Baton.Mutation.VerifyStep
                .RunAndRecordAsync(commands, workspace, artifactsRoot, timeout, cancellationToken)
                .ConfigureAwait(false);

            foreach (var commandResult in outcome.Results)
            {
                // Same three readings of an absent exit code the results file distinguishes -- "exit
                // unknown" was the one spelling that told an operator nothing about which happened.
                var verdict = commandResult switch
                {
                    { TimedOut: true } => "timed out",
                    { ExitCode: null } => "not run",
                    { ExitCode: Baton.Mutation.VerifyStepReport.BuildLockBlockedExitCode } => "blocked on the build lock",
                    var r => $"exit {r.ExitCode}",
                };
                Console.Out.WriteLine($"  {commandResult.CommandLine} -- {verdict} ({commandResult.WallClockMs} ms)");
            }

            Console.Out.WriteLine($"Verify results: {outcome.ResultsFilePath}");
            return outcome;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The commands may well have run; what failed is recording them. Say so and dispatch
            // anyway -- a review with no results file is the pre-#1882 status quo, not a broken run.
            // The prompt's paragraph will point at a file that is absent, which the reviewer can see
            // for itself; fabricating a results file it could not write would be worse.
            Console.Error.WriteLine(
                $"Warning: the verify step could not record its results into '{artifactsRoot}': {ex.Message} "
                + "The review is dispatched without them.");
            return null;
        }
    }

    /// <summary>
    /// #1848's admission gate at the one entry point that admits NEW vendor spend from cold. Every
    /// distinct adapter this dispatch would spawn on is evaluated separately — a claude Hold never
    /// holds an agy dispatch (operator ruling, 2026-09-05) — and each decision is printed as a status
    /// line whether it admits, holds, or is overridden. A Hold with no <c>--override-runway</c> throws,
    /// which is how this exits non-zero (<see cref="RunExitCode.ValidationRefused"/>) with the counters
    /// and the exact flag printed once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not gated: <c>--continue</c>.</b> Rehiring the worker that already ran in a prior room is
    /// continuation of work the fleet already admitted, and the ruling holds new admissions rather than
    /// interrupting work in flight — the same reason <c>baton redispatch</c>/<c>resolve</c>/<c>run</c>/
    /// <c>resume</c> consult no gate at all. spec/baton.md §7's "Runway hold (#1848)" is the register
    /// for that list; this comment does not restate it.
    /// </para>
    /// <para>
    /// <b>Every evaluation journals, since #1896</b> — one append-only row per vendor per dispatch in
    /// <see cref="BatonPaths.RunwayAdmissionLedgerFile"/>, admitted or refused, written before the
    /// refusal below throws. That ledger is also where the cross-dispatch reservation arithmetic reads
    /// its outstanding reservations from, in the same critical section it writes this row in
    /// (<see cref="RunwayAdmissionLedgerStore"/>). The room-facing half is
    /// <see cref="WorkerBindingConfigEntry.RunwayAdmission"/>, stamped below on every binding, next to
    /// <see cref="WorkerBindingConfigEntry.RunwayOverride"/>'s narrower override record. spec/baton.md §7,
    /// "Runway hold (#1848)", is the register for all three surfaces.
    /// </para>
    /// </remarks>
    private static async Task<IReadOnlyDictionary<string, WorkerBindingConfigEntry>> ApplyRunwayGateAsync(
        DispatchOptions options,
        IReadOnlyDictionary<string, WorkerBindingConfigEntry> bindings,
        string workspace,
        Func<string, RunwayDecision>? evaluateRunway,
        IRunwayReservationPolicy? reservationPolicy,
        CancellationToken cancellationToken)
    {
        if (options.ContinueFromRoomDirectoryPath is not null)
        {
            // No gate here, and no --override-runway to drop either: the combination was refused at the
            // top of ExecuteAsync, which is the only reason this early return cannot silently discard
            // an audited flag.
            return bindings;
        }

        var settings = await DaemonSettingsStore.LoadAsync(BatonPaths.SettingsFile, cancellationToken).ConfigureAwait(false);
        var evaluate = evaluateRunway ?? CreateDiskRunwayEvaluator(settings);
        var policy = reservationPolicy ?? RunwayReservationPolicies.Resolve(settings.RunwayHold.ReservationPolicy);

        var decisions = new Dictionary<string, RunwayDecision>(StringComparer.Ordinal);
        foreach (var vendor in bindings.Values
            .Select(b => b.Adapter)
            // The capture step spawns git, not a vendor CLI — it admits no vendor spend to gate.
            .Where(a => !string.Equals(a, WorkflowTemplateComposer.CaptureAdapter, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal))
        {
            decisions[vendor] = evaluate(vendor);
        }

        // Read OUTSIDE the ledger's critical section, deliberately: the lock body below must stay
        // synchronous (Mutex ownership is thread-affine), and this spawns git to resolve a repository
        // identity. Skipped entirely when no vendor reached the reservation arm — a policy that never
        // reads the ledger, and a dispatch every one of whose vendors was already held or is unmeasured,
        // both pay nothing, because an estimate cannot change any of those outcomes.
        var ledgerRows = policy.UsesCostLedger && decisions.Values.Any(d => d.HeadroomPoints is not null)
            ? await TryReadCostLedgerAsync(options, workspace, cancellationToken).ConfigureAwait(false)
            : [];

        var now = DateTimeOffset.UtcNow;
        var requests = new List<RunwayAdmissionRequest>();
        foreach (var (vendor, decision) in decisions)
        {
            var thresholds = settings.RunwayHold.For(vendor);
            var role = ResolveSoleRoleFor(bindings, vendor);

            requests.Add(new RunwayAdmissionRequest(
                Vendor: vendor,
                GateHeld: decision.IsHold,
                Unmeasured: decision.Reason == RunwayGate.UnmeasuredReason,
                GateReason: decision.Reason,
                Counters: decision.Counters,
                WeekHoldPct: thresholds.WeekHoldPct,
                SessionHoldPct: thresholds.SessionHoldPct,
                MaxSnapshotAgeHours: thresholds.EffectiveMaxSnapshotAge.TotalHours,
                SnapshotHarvestedAt: decision.SnapshotHarvestedAt,
                HeadroomPoints: decision.HeadroomPoints,
                Estimate: policy.Estimate(new RunwayEstimateContext(vendor, role, ledgerRows)),
                Room: BatonPaths.RecordKey(options.RoomDirectoryPath),
                Role: role,
                OverrideReason: options.OverrideRunwayReason,
                At: now));
        }

        // One call for the whole dispatch, not one per vendor: the refusal below is all-or-nothing, and
        // the store has to know that before it writes any row (RunwayAdmissionEntry.Dispatched).
        var (recorded, unrecordedReason) = await RecordAdmissionsAsync(requests, cancellationToken).ConfigureAwait(false);
        var admissions = recorded.ToDictionary(
            entry => entry.Vendor, entry => ToBindingRecord(entry, unrecordedReason), StringComparer.Ordinal);

        var holds = admissions.Values
            .Where(a => a.Decision is RunwayAdmissionDecisions.Held or RunwayAdmissionDecisions.HeldOverridden)
            .ToList();

        foreach (var (vendor, admission) in admissions)
        {
            var verdict = admission.Decision switch
            {
                RunwayAdmissionDecisions.HeldOverridden => "HELD, OVERRIDDEN",
                RunwayAdmissionDecisions.Held => "HELD",
                RunwayAdmissionDecisions.Unmeasured => "admit (unmeasured)",
                _ => "admit",
            };
            var because = admission.Reason is { } reason ? $" — {reason}" : string.Empty;
            Console.Out.WriteLine(
                $"Runway ({vendor}): {verdict}{because} [{decisions[vendor].DescribeCounters()}]");
        }

        if (holds.Count > 0 && options.OverrideRunwayReason is null)
        {
            var detail = string.Join(
                "; ", holds.Select(h => $"{h.Vendor}: {h.Reason} [{decisions[h.Vendor].DescribeCounters()}]"));
            throw new CliArgumentException(
                $"Runway hold — not dispatching new work on {string.Join(", ", holds.Select(h => h.Vendor))}. {detail}. "
                + "Work already running is unaffected.",
                $"""dispatch it anyway with --override-runway "<reason>" (the reason is recorded on the room), or wait for the vendor's window to reset.""");
        }

        // Stamped per binding, off that binding's OWN vendor decision — the same stamp-onto-every-entry
        // shape Label/Workstream/ToolSha above already use. RunwayAdmission goes on every binding;
        // RunwayOverride only when the flag was passed, with Used=false as the recorded "the flag was
        // passed and nothing needed bypassing" case the issue asks for by name.
        return bindings.ToDictionary(
            pair => pair.Key,
            pair =>
            {
                if (!decisions.TryGetValue(pair.Value.Adapter, out var decision))
                {
                    return pair.Value;
                }

                var admission = admissions[pair.Value.Adapter];
                var stamped = pair.Value with { RunwayAdmission = admission };
                return options.OverrideRunwayReason is { } overrideReason
                    ? stamped with
                    {
                        RunwayOverride = new RunwayOverride(
                            decision.Vendor,
                            overrideReason,
                            admission.Decision == RunwayAdmissionDecisions.HeldOverridden,
                            decision.Counters,
                            admission.Decision == RunwayAdmissionDecisions.HeldOverridden ? admission.Reason : null),
                    }
                    : stamped;
            },
            StringComparer.Ordinal);
    }

    /// <summary>
    /// Appends this dispatch's facts and returns them, with the reason the ledger could not be written
    /// when it could not. <b>Fails open</b> — spec/baton.md §7 states that posture and what it costs, and
    /// this comment does not restate it. The mechanics here: the fallback is built from an empty row list
    /// with the headroom cleared, so it carries the counters' own verdict and no
    /// <see cref="RunwayAdmissionEntry.OutstandingReservationPoints"/>.
    /// </summary>
    /// <remarks>
    /// #1932 review: spec/baton.md §7 states what an unrecorded admission costs and why it is surfaced
    /// rather than only logged. The mechanics here are just that the reason is returned alongside the
    /// facts and stamped onto every binding's <see cref="RunwayAdmission.UnrecordedReason"/>, which is
    /// what puts it in <c>baton status</c> (text and <c>--json</c>) and <c>fleet_status</c>.
    /// </remarks>
    private static async Task<(IReadOnlyList<RunwayAdmissionEntry> Entries, string? UnrecordedReason)> RecordAdmissionsAsync(
        IReadOnlyList<RunwayAdmissionRequest> requests, CancellationToken cancellationToken)
    {
        try
        {
            return (await RunwayAdmissionLedgerStore
                .ReserveAndRecordAsync(requests, BatonPaths.RunwayAdmissionLedgerFile, cancellationToken)
                .ConfigureAwait(false), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            var reason = $"{BatonPaths.RunwayAdmissionLedgerFile} could not be written: {ex.Message}";
            Console.Error.WriteLine(
                $"Warning: could not record the runway admission decision(s) for "
                + $"'{string.Join("', '", requests.Select(r => r.Vendor))}': {ex.Message} "
                + "The counters' own verdict still applies; no headroom was reserved across dispatches, "
                + "so concurrent dispatches on this machine are deciding against the same snapshot "
                + "unaware of each other. Recorded on the room as 'Runway admission unrecorded'.");
            return (RunwayAdmissionLedgerStore.Decide(
                [.. requests.Select(r => r with { HeadroomPoints = null })], []), reason);
        }
    }

    /// <summary>The binding-facing projection of one recorded fact — the same values, minus the ledger-only
    /// bookkeeping (timestamp, room, role) a room already knows about itself, plus
    /// <paramref name="unrecordedReason"/>, which exists only when the fact was never recorded at all.</summary>
    private static RunwayAdmission ToBindingRecord(RunwayAdmissionEntry entry, string? unrecordedReason) =>
        new(entry.Vendor,
            entry.Decision,
            entry.DecidedBy,
            entry.Reason,
            entry.Counters,
            entry.WeekHoldPct,
            entry.SessionHoldPct,
            entry.HeadroomPoints,
            entry.OutstandingReservationPoints,
            entry.EstimatedBurnPoints,
            entry.EstimateSource,
            unrecordedReason);

    /// <summary>
    /// The one worker role bound to <paramref name="vendor"/>, or null when a composed template bound
    /// several to it. Null is not a failure: the reservation policy's own contract is that an
    /// unattributable dispatch falls back to the flat default rather than borrowing another role's median.
    /// </summary>
    private static string? ResolveSoleRoleFor(
        IReadOnlyDictionary<string, WorkerBindingConfigEntry> bindings, string vendor)
    {
        string? sole = null;
        foreach (var (worker, entry) in bindings)
        {
            if (!string.Equals(entry.Adapter, vendor, StringComparison.Ordinal))
            {
                continue;
            }

            if (sole is not null)
            {
                return null;
            }

            sole = worker;
        }

        return sole;
    }

    /// <summary>
    /// This repository's cost-ledger rows for the reservation policy, or empty when there are none to be
    /// had. Never throws and never blocks a dispatch: an unresolvable repository identity, an absent
    /// ledger, or an unreadable one all resolve to no evidence, which the policy reads as "use the flat
    /// default" — the same fail-open posture <see cref="RepositoryIdentityResolver"/> itself documents.
    /// </summary>
    /// <remarks>
    /// Falls back to <paramref name="workspace"/>, never to the process's current directory, for the case
    /// <see cref="RepositoryIdentityResolver"/>'s own remarks name: a session sitting in one checkout
    /// while dispatching work into another. Estimating a burn from an unrelated repository's cost rows
    /// would be quietly wrong rather than loudly absent, which is the failure this whole ledger exists to
    /// stop producing.
    /// </remarks>
    private static async Task<IReadOnlyList<CostLedgerEntry>> TryReadCostLedgerAsync(
        DispatchOptions options, string workspace, CancellationToken cancellationToken)
    {
        var identity = await RepositoryIdentityResolver
            .TryResolveAsync(options.RepoPath ?? workspace, cancellationToken)
            .ConfigureAwait(false);
        return identity is null
            ? []
            : await CostLedgerStore
                .ReadAllAsync(BatonPaths.CostLedgerFile(identity.FileSlug), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The production evaluator, over the settings the caller already loaded once for this dispatch,
    /// closed over each vendor's latest PERSISTED snapshot. Never spawns a vendor CLI — the daemon
    /// harvests, dispatch reads (<see cref="RunwaySnapshotReader"/>).
    /// </summary>
    private static Func<string, RunwayDecision> CreateDiskRunwayEvaluator(DaemonSettings settings) =>
        vendor => RunwayGate.Evaluate(
            vendor, RunwaySnapshotReader.Read(vendor), settings.RunwayHold.For(vendor), DateTimeOffset.UtcNow);

    /// <summary>
    /// #1841: persists ids already recovered from live worker stdout by
    /// <see cref="IWorkerAdapter.TryParseSessionId"/>. A no-op write when nothing was reported; never
    /// records an empty/null id and never changes <see cref="WorkerBindingConfigEntry.ResumeSession"/>.
    /// </summary>
    private static async Task RecordCapturedSessionIdsAsync(
        IReadOnlyDictionary<string, WorkerBindingConfigEntry> bindings,
        IReadOnlyDictionary<string, string> capturedSessionIds,
        string bindingsFilePath)
    {
        if (capturedSessionIds.Count == 0)
        {
            return;
        }

        var updated = bindings.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var (workerName, sessionId) in capturedSessionIds)
        {
            if (updated.TryGetValue(workerName, out var entry))
            {
                updated[workerName] = entry with { SessionId = sessionId };
            }
        }

        // The workflow token can be intentionally cancelled by the time the worker has terminated.
        // This small terminal bookkeeping step must still make an already-observed id durable.
        await WorkerBindingConfigWriter.SaveToFileAsync(updated, bindingsFilePath, CancellationToken.None)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// R3 (#1354/#1380, finding 3): this copy must never be the thing that kills the process before
    /// <c>Program</c> writes <c>terminal.json</c> (#1374's completion contract) — an existing-directory
    /// destination, a read-only target, or a file another process still holds open all throw
    /// <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>, neither of which derives
    /// from <see cref="BatonFlowException"/>, so neither of <c>Program</c>'s typed catches would have
    /// handled it. Report on stderr and return, letting the normal exit path run — the workflow has
    /// already reached Terminal and its declared output already exists at <c>srcPath</c> regardless of
    /// whether this copy succeeds.
    /// </summary>
    private static void CopyPrimaryOutputToOverride(DispatchOptions options, CommandResult result, string primaryOutputName)
    {
        // #1702: NOT gated on Status == Succeeded — a verify failure flips the step to
        // Failed/Indeterminate after the output already exists on disk (report-953.md's own repro;
        // full account spec/baton.md §3, "the resolved verify command" section). File.Exists(srcPath)
        // below is the real, unconditional gate.
        var step = result.State.Steps.FirstOrDefault(s => s.LatestExecutionId is not null);
        if (step is null || step.LatestExecutionId is not { } execId)
        {
            return;
        }

        var srcPath = Path.Combine(
            options.RoomDirectoryPath, Baton.Artifacts.ArtifactManager.ArtifactsDirectoryName, $"execution_{execId}", primaryOutputName);
        if (!File.Exists(srcPath))
        {
            return;
        }

        try
        {
            var destPath = Path.GetFullPath(options.OutputPath!);
            var destDir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            File.Copy(srcPath, destPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(
                $"Could not copy the declared output to '{options.OutputPath}': {ex.Message}. "
                + $"The output still exists at '{srcPath}'.");
        }
    }

    /// <summary>
    /// Same category vocabulary <c>FakeEchoWorkerAdapter</c>'s translator uses in the test suite
    /// (read/write/shell/network, negated with a <c>no-</c> prefix) -- one register for "what a grant
    /// says", not a second one invented for this printed line.
    /// </summary>
    /// <remarks>
    /// #1355 F1: the <see cref="GrantAuditMode.AuditedNotEnforced"/> branch must say only what that
    /// mode's own doc says is true (<see cref="GrantAuditMode"/>'s remarks) -- the grant EXCEEDS the
    /// role's intent because the vendor hook cannot path-scope it, not "scoped to declared outputs".
    /// What actually bounds the write is the hook confining write-family tools to the worktree/outbox
    /// (<c>AgyHookCheckCommand</c>'s write-family check) -- i.e. every file in the provisioned
    /// worktree -- with declared-output confinement checked only AFTER the run, by
    /// <c>OutcomeClassifier</c>'s worktree-cleanliness audit. Do not restate the two mechanisms here
    /// beyond naming them (record-once); the citations above are the source, this line is the gloss.
    /// </remarks>
    private static string DescribeGrant(WorkerBindingConfigEntry binding)
    {
        var grant = binding.PermissionGrant;
        if (grant is null)
        {
            return "unset (falls back to the adapter's raw PermissionScope)";
        }

        var write = grant.WriteFiles
            ? binding.GrantAuditMode == GrantAuditMode.AuditedNotEnforced
                ? "write (workspace-wide inside an isolated worktree; audited against declared outputs after the run)"
                : "write"
            : "no-write";

        // #1456: an unqualified "shell" would understate a pattern-scoped grant (review's) the same
        // way an unqualified "write" would understate an audited one above -- this line exists so the
        // invoking agent can relay the actual grant honestly, and "shell" alone reads as unscoped.
        var shell = grant.RunShellCommands
            ? grant.ShellCommandPatterns is { Count: > 0 } patterns
                ? $"shell (scoped: {string.Join(", ", patterns)})"
                : "shell"
            : "no-shell";

        return string.Join(
            ", ",
            grant.ReadFiles ? "read" : "no-read",
            write,
            shell,
            grant.NetworkAccess ? "network" : "no-network");
    }

    private static async Task<(WorkflowDefinition Definition, IReadOnlyDictionary<string, WorkerBindingConfigEntry> Bindings)>
        MaterializeAsync(DispatchOptions options, string workspaceDirectory, CancellationToken cancellationToken)
    {
        try
        {
            // The catalog reads are the fail-loud set both catalogs share: a missing file (FileNotFound),
            // malformed JSON (JsonException), a structural fault (InvalidOperationException — duplicate id,
            // empty outputs, capture-id collision), or a phase naming a role the catalog lacks
            // (KeyNotFoundException, via WorkerRoleCatalog.For). None derive from BatonFlowException, so
            // without this they escape Program's boundary as a crash rather than the clean exit promised.
            // This wraps the WHOLE materialization, not just the isTemplate/isRole probes: a template
            // dispatch re-reads the catalog fresh during composition (WorkflowTemplateCatalog.For, and
            // WorkerRoleCatalog.For per phase — All => Load() opens the file on every access, it is not
            // cached), and a fault there must surface as a typed CliArgumentException too (#929). The
            // deliberate CliArgumentException throws below (and WorkspaceHead's non-git refusal) are not in
            // the filter, so they pass through unwrapped.
            var isTemplate = WorkflowTemplateCatalog.All.Any(t => string.Equals(t.Id, options.Name, StringComparison.Ordinal));
            var isRole = WorkerRoleCatalog.All.Any(r => string.Equals(r.Id, options.Name, StringComparison.Ordinal));

            if (isTemplate && isRole)
            {
                throw new CliArgumentException(
                    $"'{options.Name}' is both a workflow template and a worker role. Dispatch is one "
                    + "namespace (decision 0047 §5) — rename one so a dispatch is unambiguous.");
            }

            if (isTemplate)
            {
                return await MaterializeTemplateAsync(options, workspaceDirectory, cancellationToken).ConfigureAwait(false);
            }

            if (isRole)
            {
                return await MaterializeRoleAsync(options, workspaceDirectory, cancellationToken).ConfigureAwait(false);
            }

            throw new CliArgumentException(
                $"No worker role or workflow template named '{options.Name}'.",
                "run 'baton templates' to list available built-ins.");
        }
        catch (Exception ex) when (ex is FileNotFoundException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new CliArgumentException(ex.Message);
        }
    }

    /// <summary>
    /// Prints discoverability information for adapters, models, efforts, and role defaults (#1500).
    /// </summary>
    public static void PrintCapabilities(TextWriter writer) => DispatchCapabilitiesPrinter.Print(writer);

    private static async Task<(WorkflowDefinition, IReadOnlyDictionary<string, WorkerBindingConfigEntry>)>
        MaterializeTemplateAsync(DispatchOptions options, string workspaceDirectory, CancellationToken cancellationToken)
    {
        // #1518: a template rejects every spec source, not just a file — --spec-text/--spec - are two
        // more ways to say the same thing --spec already refuses here, so a template dispatch cannot
        // silently discard an inline spec the way it never could silently discard a file one.
        if (options.SpecFilePath is not null || options.SpecText is not null || options.SpecFromStdin)
        {
            throw new CliArgumentException(
                $"'{options.Name}' is a workflow template — its phases carry their own instructions, so "
                + "--spec/--spec-text does not apply. Pass a spec only when dispatching a role.");
        }

        if (options.Attachments is { Count: > 0 })
        {
            throw new CliArgumentException(
                $"'{options.Name}' is a workflow template — its phases carry their own instructions, so "
                + "--attach does not apply. Pass --attach only when dispatching a role.",
                "remove the --attach flag, or dispatch a single role instead of a template.");
        }

        // #1151: refused rather than silently dropped, the same shape as --attach immediately above. A
        // template binds one worker per phase, and a single flag naming no phase cannot say which of
        // them the skill is for -- attaching it to all of them would be a guess. Role-catalog skills
        // (#1151 S6) are the shape that answers this for a template, and they are not this slice.
        if (options.Skills is { Count: > 0 })
        {
            throw new CliArgumentException(
                $"'{options.Name}' is a workflow template — it binds one worker per phase, so a single "
                + "--skill names no phase to attach to. Pass --skill only when dispatching a role.",
                "remove the --skill flag, or dispatch a single role instead of a template.");
        }

        // R5 (#1354/#1380, finding 7): a template's steps each declare their own output — there is no
        // one "primary output" for --output to rename, and the prior behaviour renamed whichever step
        // happened to be first regardless of what kind of step that was (a capture step, say), silently.
        // Refuse up front, the same way --spec already is above.
        if (options.OutputPath is not null)
        {
            throw new CliArgumentException(
                $"'{options.Name}' is a workflow template — its phases each declare their own outputs, so "
                + "--output does not apply. Pass --output only when dispatching a role.",
                "remove the --output flag, or dispatch a single role instead of a template.");
        }

        if (options.Timeout is not null)
        {
            throw new CliArgumentException(
                $"'{options.Name}' is a workflow template — each phase carries its own role's timeout, so "
                + "--timeout does not apply to one of them. Pass --timeout only when dispatching a role.",
                "remove the --timeout flag, or dispatch a single role instead of a template.");
        }

        if (options.TokenBudget is not null)
        {
            throw new CliArgumentException(
                $"'{options.Name}' is a workflow template — each phase carries its own role's token "
                + "budget, so --token-budget does not apply to one of them. Pass --token-budget only "
                + "when dispatching a role.",
                "remove the --token-budget flag, or dispatch a single role instead of a template.");
        }

        if (options.MaxToolSteps is not null)
        {
            throw new CliArgumentException(
                $"'{options.Name}' is a workflow template — each phase carries its own role's tool-step "
                + "cap, so --max-tool-steps does not apply to one of them. Pass --max-tool-steps only "
                + "when dispatching a role.",
                "remove the --max-tool-steps flag, or dispatch a single role instead of a template.");
        }

        if (options.BilledRateLimit is not null)
        {
            throw new CliArgumentException(
                $"'{options.Name}' is a workflow template — each phase carries its own role's billed-rate "
                + "limit, so --billed-rate-limit does not apply to one of them. Pass --billed-rate-limit "
                + "only when dispatching a role.",
                "remove the --billed-rate-limit flag, or dispatch a single role instead of a template.");
        }

        if (options.VerifyCommand is not null)
        {
            throw new CliArgumentException(
                $"'{options.Name}' is a workflow template — each phase carries its own role's verify "
                + "command, so --verify does not apply to one of them. Pass --verify only when "
                + "dispatching a role.",
                "remove the --verify flag, or dispatch a single role instead of a template.");
        }

        // #1882: the verify step is a REVIEW-role concept (it exists to feed one reviewer's first turn
        // and to stamp that reviewer's verdict.json), and a template has no single review phase to
        // attach it to. Refused up front rather than silently discarded, the same as every escape hatch
        // above it.
        if (options.VerifyCommands is { Count: > 0 })
        {
            throw new CliArgumentException(
                $"'{options.Name}' is a workflow template — the pre-turn verify step belongs to one "
                + "review lane's own first turn, so --verify-cmd does not apply to a template. Pass "
                + "--verify-cmd only when dispatching the review role.",
                "remove the --verify-cmd flag, or dispatch the review role directly.");
        }

        if (options.VerifyTimeout is not null)
        {
            throw new CliArgumentException(
                $"'{options.Name}' is a workflow template — --verify-timeout bounds --verify-cmd, which "
                + "a template does not accept. Pass both only when dispatching the review role.",
                "remove the --verify-timeout flag, or dispatch the review role directly.");
        }

        if (options.ExpectPr is not null)
        {
            throw new CliArgumentException(
                $"'{options.Name}' is a workflow template — each phase carries its own role's delivery "
                + "expectations, so --expect-pr does not apply to one of them. Pass --expect-pr only "
                + "when dispatching a role.",
                "remove the --expect-pr flag, or dispatch a single role instead of a template.");
        }

        if (options.ContinueFromRoomDirectoryPath is not null)
        {
            // #1381: --continue rehires ONE veteran worker's vendor session — a composed template has
            // no single worker to rehire (its phases are separate bindings, possibly separate adapters),
            // so this is refused the same way every other role-only escape hatch above is.
            throw new CliArgumentException(
                $"'{options.Name}' is a workflow template — it has no single worker's vendor session to "
                + "rehire, so --continue does not apply to one of them. Pass --continue only when "
                + "dispatching a role.",
                "remove the --continue flag, or dispatch a single role instead of a template.");
        }

        var template = WorkflowTemplateCatalog.For(options.Name);
        // #1083: hand every phase the workspace too, so a role run as a template phase can read the repo
        // exactly as a directly-dispatched role now can.
        var (definition, bindings) = WorkflowTemplateComposer.Materialize(
            template, options.Adapter, workingDirectory: workspaceDirectory);
        bindings = await InjectCaptureBaseRefAsync(bindings, workspaceDirectory, cancellationToken).ConfigureAwait(false);
        return (definition, bindings);
    }

    private static async Task<(WorkflowDefinition, IReadOnlyDictionary<string, WorkerBindingConfigEntry>)>
        MaterializeRoleAsync(DispatchOptions options, string workspaceDirectory, CancellationToken cancellationToken)
    {
        if (options.SpecFilePath is null && options.SpecText is null && !options.SpecFromStdin)
        {
            throw new CliArgumentException(
                $"'{options.Name}' is a worker role, which runs against a task spec. Pass --spec "
                + "<spec-file>, --spec - to read stdin, or --spec-text <text> for a short inline prompt.",
                $"baton dispatch {options.Name} --spec <spec-file>");
        }

        // #1518: three sources for the one spec string -- spec/baton.md's dispatch entry has the full
        // rationale (record-once, not restated here). Resolved BEFORE the role lookup/--output
        // validation below so a missing/blank spec source is still reported ahead of a --output
        // collision, the same precedence dispatch had before this issue.
        var spec = await ResolveSpecAsync(options, cancellationToken).ConfigureAwait(false);

        var role = WorkerRoleCatalog.For(options.Name);

        // #1882: --verify-cmd is a review-role flag. The gate is the role's own ProducesVerdict rather
        // than a hardcoded "review" string, because the step's other half is stamping `instruments` onto
        // verdict.json — a role with no verdict has nowhere for that to land, so the two facts are the
        // same fact. The message names the OTHER verify flag on purpose -- spec/baton.md §9 states why
        // that disambiguation belongs in the refusal rather than only in the docs.
        if (options.VerifyCommands is { Count: > 0 } && !role.ProducesVerdict)
        {
            throw new CliArgumentException(
                $"'{role.Id}' does not produce a verdict, so --verify-cmd does not apply to it. The "
                + "pre-turn verify step runs allowlisted commands with no model involved and records "
                + "them as the reviewer's instruments — only a verdict-producing role (review) has "
                + "somewhere to record them. This is NOT the same flag as --verify, which overrides the "
                + "post-exit verify command (a role's verify_pixi_task) that decides whether a mutating "
                + "execution settles.",
                $"drop --verify-cmd to dispatch '{role.Id}', or use --verify <cmd> if you meant the "
                + "post-exit verify command.");
        }

        if (options.VerifyTimeout is not null && options.VerifyCommands is not { Count: > 0 })
        {
            throw new CliArgumentException(
                "'--verify-timeout' bounds each --verify-cmd's wall clock, but no --verify-cmd was "
                + "passed — on its own it would bound nothing.",
                "add at least one --verify-cmd, or drop --verify-timeout.");
        }

        if (options.OutputPath is not null)
        {
            ValidateOutputOverride(options, role);
        }

        // #1083: pin the workspace onto the binding so the worker can actually read the project it was
        // dispatched to study — the process cwd alone does not reach agy (`-p` ignores it, #491).
        // #1082: vendor/model/effort are three independent axes over the role's instructions ([0017]).
        // #1576: attach validation, the spec/grant lint, and the Materialize call itself all go through
        // the seam RedispatchCommand's own --spec path now shares (RoleSpecMaterializer).
        return RoleSpecMaterializer.Materialize(
            role, spec, options.Adapter, workingDirectory: workspaceDirectory,
            modelOverride: options.Model, effortOverride: options.Effort, outputOverride: options.OutputPath,
            timeoutOverride: options.Timeout, attachments: options.Attachments, roomDirectoryPath: options.RoomDirectoryPath,
            tokenBudgetOverride: options.TokenBudget, maxToolStepsOverride: options.MaxToolSteps,
            billedRateLimitOverride: options.BilledRateLimit,
            verifyCommandOverride: options.VerifyCommand, expectPrOverride: options.ExpectPr,
            verifyResultsPath: VerifyResultsPath(options),
            // #1151: resolved and requirement-checked inside ToBinding, which runs before
            // Directory.CreateDirectory below -- so an unknown --skill leaves no room behind.
            skills: options.Skills);
    }

    /// <summary>
    /// #1882: where this dispatch's <c>verify-results.md</c> will land, or null when no
    /// <c>--verify-cmd</c> was passed. Computed from the room directory alone — the step runs before
    /// any execution exists, so the file lives in the ROOM's artifacts directory rather than an
    /// execution-scoped one, which is also why the prompt can name its path before the step has run.
    /// </summary>
    private static string? VerifyResultsPath(DispatchOptions options) =>
        options.VerifyCommands is { Count: > 0 }
            ? Path.Combine(
                options.RoomDirectoryPath,
                Baton.Artifacts.ArtifactManager.ArtifactsDirectoryName,
                Baton.Mutation.VerifyStepReport.ResultsFileName)
            : null;

    /// <summary>
    /// Resolves the task-prompt string from whichever of the three <c>--spec</c>/<c>--spec-text</c>
    /// sources <see cref="MaterializeRoleAsync"/> found present (the parser already refused more than
    /// one). A stdin read on an interactive terminal would hang forever waiting for EOF that never
    /// comes — a non-interactive CLI (the same doctrine <c>--timeout</c>'s ceiling rests on) refuses
    /// that outright rather than let a scout's one-liner appear to freeze.
    /// </summary>
    private static async Task<string> ResolveSpecAsync(DispatchOptions options, CancellationToken cancellationToken)
    {
        if (options.SpecText is { } specText)
        {
            return specText;
        }

        if (options.SpecFromStdin)
        {
            if (!Console.IsInputRedirected)
            {
                throw new CliArgumentException(
                    "'--spec -' reads the task prompt from stdin, but stdin is a terminal here — reading "
                    + "it would hang forever waiting for input that never ends.",
                    "pipe the spec text in, e.g. `echo \"...\" | baton dispatch "
                    + $"{options.Name} --spec -`, or pass --spec-text/--spec <spec-file> instead.");
            }

            var stdinSpec = await Console.In.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

            // #1518 second-reader: --spec-text "" is refused at parse time (blank has no sane
            // invocation to correct into, unlike an empty --label) -- stdin cannot be checked until
            // read, which is here, but the refusal must be the same one so a blank prompt is never
            // silently dispatched regardless of which of the two inline sources produced it.
            if (stdinSpec.Trim().Length == 0)
            {
                throw new CliArgumentException(
                    "'--spec -' read nothing but blank/whitespace from stdin — pass the task prompt text "
                    + "on stdin, or use --spec-text/--spec <spec-file> instead.",
                    "pipe non-blank spec text in, or drop --spec - for --spec-text/--spec <spec-file>.");
            }

            return stdinSpec;
        }

        if (!File.Exists(options.SpecFilePath))
        {
            throw new CliArgumentException($"Spec file '{options.SpecFilePath}' does not exist.");
        }

        return await File.ReadAllTextAsync(options.SpecFilePath!, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// R6 (#1354/#1380, finding 8): validated before anything is printed or written — the
    /// materialization that calls this runs before the room directory is even created (finding 6's
    /// three checks). <see cref="Path.GetFileName"/> on a trailing-separator path (<c>--output
    /// reports/</c>) returns an empty string, which would otherwise declare an anonymous
    /// <see cref="ProducedOutput"/> that pays for a full run before failing "contract not satisfied"
    /// with nothing naming the cause. The other two checks catch a rename that collides with something
    /// already writing to the same execution output directory: the engine's own reserved namespace
    /// (<see cref="ReservedOutputNames"/>), its durable prompt capture
    /// (<see cref="Baton.Artifacts.ArtifactManager.PromptFileName"/>), or another output the same
    /// role already declares.
    /// </summary>
    private static void ValidateOutputOverride(DispatchOptions options, WorkerRole role)
    {
        var outputPath = options.OutputPath!;
        var customName = Path.GetFileName(outputPath);
        if (string.IsNullOrEmpty(customName))
        {
            throw new CliArgumentException(
                $"'--output {outputPath}' names no file — a path ending in a directory separator has no "
                + "filename. Pass a file path, e.g. --output report.md.",
                "pass a file path instead of a directory, e.g. --output report.md");
        }

        // #1382 F6: "choose a different file name for --output" restated the message with no
        // invocation in it. The rest of the corrected command is already in scope here -- the
        // replacement file name is genuinely unknowable, so that alone stays a placeholder.
        // #1518: on a --spec-text dispatch, the operator's own text is a SECOND placeholder --
        // options.SpecText is known but echoing it verbatim could emit a broken shell line (embedded
        // quotes/newlines), so "<text>" stays generic rather than round-tripping the actual string. The
        // file path is null in that case, and rendering it verbatim would print an unrunnable
        // "--spec  --output ..." (the same class of bug #1382 F6 itself was about) -- specClause below
        // picks whichever of the three sources the operator actually used instead.
        var specClause = options.SpecFilePath is not null ? $"--spec {options.SpecFilePath}"
            : options.SpecFromStdin ? "--spec -"
            : "--spec-text <text>";
        var retryInvocation = $"baton dispatch {options.Name} {specClause} --output <different-file-name>";

        if (ReservedOutputNames.IsReserved(customName))
        {
            throw new CliArgumentException(
                $"'--output {customName}' is invalid: {ReservedOutputNames.RejectionClause}.",
                retryInvocation);
        }

        if (string.Equals(customName, Baton.Artifacts.ArtifactManager.PromptFileName, StringComparison.Ordinal))
        {
            throw new CliArgumentException(
                $"'--output {customName}' collides with '{Baton.Artifacts.ArtifactManager.PromptFileName}', "
                + "the durable prompt capture the engine writes into every execution's own output directory. "
                + "Choose a different name.",
                retryInvocation);
        }

        if (role.Outputs.Skip(1).Any(o => string.Equals(o.Name, customName, StringComparison.Ordinal)))
        {
            throw new CliArgumentException(
                $"'--output {customName}' collides with role '{role.Id}''s own declared output of the same name.",
                retryInvocation);
        }
    }

    /// <summary>
    /// #1381: the veteran room/execution/session <c>--continue</c> actually rehired, threaded through
    /// to <see cref="InteractiveSessionMaterializer.WriteWorkflowRoomMarkerAsync"/> so the fact lands in
    /// the NEW room's own marker rather than a second file (record-once).
    /// </summary>
    private sealed record ContinuationProvenance(string ParentRoomDirectoryPath, string? ParentExecutionId, string SessionId);

    /// <summary>
    /// <c>--continue &lt;room-dir&gt;</c> (#1381): resolves the terminal room a follow-on brief should
    /// rehire — the vendor session id lives on that room's own single-worker <c>bindings.json</c> entry
    /// (<see cref="WorkerBindingConfigEntry.SessionId"/>, the exact field <see cref="ResumeCommand"/>
    /// already reads for the same-room <c>baton resume</c> case, M24/#1359) — do not add a second
    /// record. What this can and cannot detect before the vendor spawns, and why: spec/baton.md §3's
    /// dispatch entry.
    /// </summary>
    /// <param name="continueFromRoomDirectoryPath">The prior room directory the operator named with <c>--continue</c>.</param>
    /// <param name="entry">
    /// This dispatch's own already-materialized binding — read for the adapter it actually resolved to
    /// (never <c>options.Adapter</c> directly, which is null on the common tier-default path) and
    /// returned with <see cref="WorkerBindingConfigEntry.SessionId"/>/<see cref="WorkerBindingConfigEntry.ResumeSession"/>
    /// overridden to continue the veteran's session.
    /// </param>
    /// <exception cref="CliArgumentException">
    /// The named room does not exist, its bindings.json is unreadable or dispatched more than one
    /// worker, its adapter is not resumable, this dispatch resolves to a different adapter,
    /// spec/baton.md §3), or it has no <see cref="WorkerBindingConfigEntry.SessionId"/> recorded.
    /// </exception>
    private static async Task<(WorkerBindingConfigEntry Entry, ContinuationProvenance Provenance)> ResolveContinuationAsync(
        string continueFromRoomDirectoryPath, WorkerBindingConfigEntry entry, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(continueFromRoomDirectoryPath))
        {
            throw new CliArgumentException(
                $"'--continue {continueFromRoomDirectoryPath}' names a room that does not exist.",
                "pass the terminal room directory of the worker to rehire, e.g. --continue <room-dir>, "
                + "or drop --continue to dispatch cold.");
        }

        var parentBindingsPath = BatonPaths.RoomBindingsFile(continueFromRoomDirectoryPath);
        IReadOnlyDictionary<string, WorkerBindingConfigEntry> parentBindings;
        try
        {
            parentBindings = await WorkerBindingConfigParser.LoadFromFileAsync(parentBindingsPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or JsonException)
        {
            throw new CliArgumentException(
                $"'--continue {continueFromRoomDirectoryPath}' names a room with no readable "
                + $"'{BatonPaths.RoomBindingsFileName}' ({ex.Message}) — only a room 'baton dispatch' "
                + "actually dispatched has a worker to rehire.",
                "pass a room 'baton dispatch' created, or drop --continue to dispatch cold.");
        }

        if (parentBindings.Count != 1)
        {
            throw new CliArgumentException(
                $"'--continue {continueFromRoomDirectoryPath}' dispatched {parentBindings.Count} workers — "
                + "rehiring a veteran only supports a single-role dispatch (baton dispatch <role> --spec "
                + "...), not a composed template.");
        }

        var (parentWorkerName, parentEntry) = parentBindings.Single();

        // #1853 extends #1381's measured Claude continuation set with Codex's measured app-server
        // thread/resume path. Requiring the identical adapter on both sides closes an adapter-swap
        // escape; agy remains gated on its own unmeasured headless conversation resume.
        if (!SupportsDispatchContinuation(parentEntry.Adapter)
            || !string.Equals(entry.Adapter, parentEntry.Adapter, StringComparison.OrdinalIgnoreCase))
        {
            throw new CliArgumentException(
                $"'--continue {continueFromRoomDirectoryPath}' cannot rehire worker '{parentWorkerName}' — "
                + $"its vendor session lives on adapter '{parentEntry.Adapter}', and this dispatch resolved "
                + $"to adapter '{entry.Adapter}'. Rehiring a veteran requires the same supported adapter "
                + "on both rooms (claude or codex); agy remains gated on its own resume measurement.",
                "drop --continue to dispatch cold, or dispatch the same role with the veteran's adapter.");
        }

        if (parentEntry.SessionId is null)
        {
            throw new CliArgumentException(
                $"'--continue {continueFromRoomDirectoryPath}' cannot rehire worker '{parentWorkerName}' — "
                + $"its '{BatonPaths.RoomBindingsFileName}' has no SessionId recorded, so there is no "
                + "vendor session to resume. Ordinary Claude dispatches capture the id reported by "
                + "the worker; this adapter or worker stream reported no usable id.",
                "drop --continue to dispatch this brief cold, or --continue a supported room whose "
                + "bindings.json contains a captured SessionId.");
        }

        // Refuse a still-running veteran outright -- concurrently resuming the same session id is not
        // vendor-guarded against; spec/baton.md §3's dispatch entry has the measurement this rests on.
        var parentTerminal = await TerminalSentinelWriter.TryReadAsync(continueFromRoomDirectoryPath, cancellationToken)
            .ConfigureAwait(false);
        if (parentTerminal is null)
        {
            throw new CliArgumentException(
                $"'--continue {continueFromRoomDirectoryPath}' has not reached a terminal state — rehiring "
                + "a veteran that might still be mid-turn would resume its vendor session concurrently, "
                + "which the vendor's own session-id guard does not protect against (an existence check, "
                + "not a lock).",
                $"wait for '{continueFromRoomDirectoryPath}' to finish (check `baton status "
                + $"{continueFromRoomDirectoryPath}`), then --continue it.");
        }

        var parentExecutionId = parentTerminal.Steps.FirstOrDefault()?.Execution;
        var resumedEntry = entry with { SessionId = parentEntry.SessionId, ResumeSession = true };
        var provenance = new ContinuationProvenance(continueFromRoomDirectoryPath, parentExecutionId, parentEntry.SessionId);
        return (resumedEntry, provenance);
    }

    private static bool SupportsDispatchContinuation(string adapter) =>
        string.Equals(adapter, "claude", StringComparison.OrdinalIgnoreCase)
        || string.Equals(adapter, "codex", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// When a composed template declares a capture step (0047 §4), captures <paramref name="workspaceDirectory"/>'s
    /// HEAD-at-start once and injects it into every capture binding's
    /// <see cref="WorkerBindingConfigEntry.PromptTemplate"/> — the base ref
    /// <see cref="CaptureWorkerAdapter"/> diffs the working tree against — <em>and</em> pins that binding's
    /// <see cref="WorkerBindingConfigEntry.WorkingDirectory"/> to the same workspace. Pinning both is the
    /// point: the base and the <c>git diff</c> that consumes it are then taken in one directory, so they
    /// cannot silently diverge if the process cwd differs from the workspace (a null binding working
    /// directory would fall through to the ambient cwd, diffing a captured SHA against the wrong tree).
    /// A non-git workspace fails loudly here, before the run, rather than opaquely inside the capture step.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, WorkerBindingConfigEntry>> InjectCaptureBaseRefAsync(
        IReadOnlyDictionary<string, WorkerBindingConfigEntry> bindings, string workspaceDirectory, CancellationToken cancellationToken)
    {
        var hasCapture = bindings.Values.Any(
            b => string.Equals(b.Adapter, WorkflowTemplateComposer.CaptureAdapter, StringComparison.Ordinal));
        if (!hasCapture)
        {
            return bindings;
        }

        var baseRef = await WorkspaceHead.CaptureAsync(workspaceDirectory, cancellationToken).ConfigureAwait(false);

        return bindings.ToDictionary(
            pair => pair.Key,
            pair => string.Equals(pair.Value.Adapter, WorkflowTemplateComposer.CaptureAdapter, StringComparison.Ordinal)
                ? pair.Value with { PromptTemplate = baseRef, WorkingDirectory = workspaceDirectory }
                : pair.Value,
            StringComparer.Ordinal);
    }
}
