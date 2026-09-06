using System.Text.Json;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Status;

namespace Baton.Outcomes;

/// <summary>The four terminal outcomes a completed dispatch is classified into.</summary>
public enum OutcomeVerdict
{
    Succeeded,
    Failed,
    Cancelled,

    /// <summary>
    /// #1608: the two-predicate model's disagreement case (spec/baton.md §3, "The terminal
    /// vocabulary") — the worker's own execution outcome and the contract's completion outcome
    /// disagree, most concretely the #1594 captured-response shape below, where substantial work
    /// happened but the declared output(s) are simply absent. Distinct from
    /// <see cref="Failed"/>: nothing here is a self-reported failure the worker or Flow diagnosed,
    /// and — unlike <see cref="Failed"/> — it carries no <see cref="FailureClassification"/> at all,
    /// since that vocabulary describes why a genuine failure should or should not retry, not why a
    /// verdict cannot yet be read off the journal. Retry-ineligible by its own explicit
    /// <see cref="Scheduling.RetryEngine.MayRetry"/> arm, not by borrowing
    /// <see cref="FailureClassification.Permanent"/>'s unrelated semantics. Only a recorded conductor
    /// resolution (<c>baton resolve</c>) ever settles a step away from this.
    /// </summary>
    Indeterminate,
}

/// <summary>
/// The classified result of a completed dispatch — the input to whichever
/// <see cref="Domain.FlowEvent"/> terminal case the <c>MutationInterface</c> appends to the log.
/// </summary>
/// <param name="Reason">
/// A human-readable diagnostic for a <see cref="OutcomeVerdict.Failed"/> or (#1608)
/// <see cref="OutcomeVerdict.Indeterminate"/> verdict — why exit code, exit reason, and contract
/// state add up to that verdict, computed once here from data available at classification time.
/// Distinct from <paramref name="FailureClassification"/>, which is the worker's own self-reported
/// retry hint, not a diagnostic Flow derives, and which <see cref="OutcomeVerdict.Indeterminate"/>
/// never carries at all. Every failure/indeterminate path <i>in this class</i> sets it, and it is
/// null for <see cref="OutcomeVerdict.Succeeded"/> and <see cref="OutcomeVerdict.Cancelled"/>.
/// <para>
/// That is deliberately a claim about this class and not about stored events. An earlier version of
/// this comment inferred that a null <c>Reason</c> on a persisted
/// <see cref="Domain.FlowEvent.ExecutionFailed"/> therefore means "written before this field
/// existed" — which nothing enforces, since <c>Reason</c> is an optional parameter any call site may
/// omit in silence, and several test fixtures already write real <c>flow.jsonl</c> lines that do.
/// Treat a null on a stored event as "no reason recorded", never as evidence of when it was written.
/// </para>
/// </param>
/// <param name="CapturedResponseFile">
/// #1594: carries <see cref="OutputMaterializer.CapturedResponse.FileName"/> (see
/// <see cref="OutputMaterializer"/>'s class remarks for the ruling, and that record's own remarks for
/// what the pairing with <paramref name="UnsatisfiedOutputNames"/> means) onto the classification.
/// Verdict-independent by construction — this field lives on the
/// classification itself rather than being tied to one <see cref="OutcomeVerdict"/> case, the way the
/// pre-ruling <c>MaterializedOutputs</c> field was tied to <see cref="OutcomeVerdict.Succeeded"/> alone
/// and so went unrecorded whenever an unrelated later gate flipped the verdict.
/// </param>
/// <param name="UnsatisfiedOutputNames">
/// <see cref="OutputMaterializer.CapturedResponse.UnsatisfiedOutputNames"/>, carried the same hop.
/// </param>
/// <param name="SubstantialWorkNoOutputsEvidence">
/// #1586 S1 (the #1594 ruling's tripwire): non-null exactly when the worker's own final usage line
/// (read the same way <see cref="OutputMaterializer.TryCaptureFinalResponse"/> reads its response line
/// — the execution's captured <c>.stdout.log</c>, last non-blank line) reports real work (turns and/or
/// output tokens) while EVERY one of <paramref name="UnsatisfiedOutputNames"/>'s siblings in the
/// contract is missing, never merely present-but-wrong. Verdict-independent by construction, the same
/// reason <paramref name="CapturedResponseFile"/> lives on the classification rather than being tied to
/// one <see cref="OutcomeVerdict"/> case: it is computed once, ahead of whether
/// <see cref="OutputMaterializer.TryCaptureFinalResponse"/> itself succeeds, so it is attached
/// identically to both the captured and the not-captured Failed return in <see cref="Classify"/>.
/// Null whenever no <c>usageParser</c> was supplied, no stdout log exists, the worker's line did not
/// parse, or it reported no turns/tokens at all — never fabricated as "no evidence found" versus
/// "not measured".
/// </param>
public sealed record OutcomeClassification(
    OutcomeVerdict Verdict,
    FailureClassification? FailureClassification = null,
    string? Reason = null,
    DateTimeOffset? RetryNotBefore = null,
    string? CapturedResponseFile = null,
    IReadOnlyList<string>? UnsatisfiedOutputNames = null,
    string? SubstantialWorkNoOutputsEvidence = null,
    // #1622/#1390: spec/baton.md §3's "workspaceChanged/hollow/hollowReason" is the canonical
    // account of these three fields -- gating, meaning, null-vs-false, all stated there once.
    bool? WorkspaceChanged = null,
    bool? Hollow = null,
    string? HollowReason = null,
    // #1945: true only on the one arm that sets it -- see OutcomeClassifier.Classify's TimedOut
    // branch. A FLAG, not a Reason prefix: the succeeded path nulls LatestFailureReason by
    // construction (StateProjector), so a sentence could not survive the hop, and
    // WorkflowOutcome.IsTimeoutFailure's own remarks are already an apology for having to sniff one.
    bool FinishedDuringTeardown = false);

/// <summary>
/// Maps a <see cref="CoreDispatchResult"/> plus a step's <see cref="WorkerContract"/> into one of
/// the four terminal classifications. Flow alone interprets Core's purely
/// mechanical report (exit code + reason) — Core itself has no notion of "success" beyond that.
/// </summary>
public static class OutcomeClassifier
{
    private const int MaxReasonLength = 500;

    /// <summary>
    /// How many unsatisfied outputs a reason names before summarising the rest as "(+N more)".
    /// A contract with more failures than this has a problem the count communicates better than
    /// the list would.
    /// </summary>
    private const int MaxListedOutputs = 8;

    /// <summary>
    /// N1 (#1664 re-review): bounds <see cref="Workspaces.WorktreeProvisioner.DescribeWorkspaceEvidence"/>
    /// as a whole, on top of that method's own per-path and count caps — ten real repo-relative paths,
    /// each individually capped, can still join into a string well past <see cref="MaxReasonLength"/>
    /// on its own. This is the backstop that keeps the suffix's reserved budget
    /// (<see cref="BuildContractFailureReason"/>) from going deeply negative before <see cref="Truncate"/>
    /// even runs.
    /// </summary>
    private const int MaxWorkspaceEvidenceLength = 200;

    /// <summary>
    /// How much of <see cref="CoreDispatchResult.StderrTail"/> a reason renders (#563).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Must stay strictly below <see cref="CoreDispatcher.MaxRetainedStderrLength"/>, and that is
    /// load-bearing rather than incidental. Two caps sit in series — the dispatcher's buffer cap and
    /// this one — but only this one emits a marker. Keeping this the tighter of the two means any
    /// stderr long enough to hit the *silent* cap is necessarily long enough to hit the *marked* one
    /// as well, so an operator is never shown a tail that had content dropped without an ellipsis
    /// saying so. Raise this above that constant and truncation becomes invisible again.
    /// Asserted by <c>OutcomeClassifierTests</c> rather than left to this comment.
    /// </para>
    /// <para>
    /// That argument requires both caps to count the <i>same</i> characters, which is why
    /// <see cref="StderrTailBuffer"/> collapses whitespace at capture time rather than this class
    /// doing it on the way out. While the collapse sat between the two caps the comparison was
    /// between different units and the guarantee was simply false — mostly-whitespace stderr could
    /// lose thousands of characters silently and still fit under this cap unmarked. Moving a
    /// collapse back downstream of the retention cap reintroduces that, whatever the two numbers say.
    /// </para>
    /// </remarks>
    internal const int MaxStderrTailInReason = 350;

    /// <summary>
    /// The one spelling of "Flow's own timeout killed this", opening both timeout reasons this class
    /// produces. <see cref="Status.WorkflowOutcome.IsTimeoutFailure"/> reads this prefix off the
    /// recorded reason — there is no structural <c>FailureClassification</c> for a timeout — so a
    /// reword here is a behaviour change for every surface that tells timeouts apart, not a copy edit.
    /// </summary>
    internal const string TimeoutSentence = "Execution timed out.";

    /// <summary>
    /// Classifies <paramref name="result"/> per this table:
    /// <c>NaturalExit + code 0 + all ProducedOutputs satisfied + no ToolDenied/ExhaustedUntil signal in the stream</c> → Succeeded;
    /// <c>NaturalExit + code 0 + all ProducedOutputs satisfied + a ToolDenied/ExhaustedUntil signal in the stream</c> → Failed (#914/#1622);
    /// <c>NaturalExit + code 0 + an unsatisfied ProducedOutput</c> → Indeterminate (#1593/#1594/#1608, unless a dead worker without result on an untouched workspace);
    /// <c>TimedOut + a mutated workspace that is clean and already pushed</c> → Succeeded, flagged
    /// <see cref="OutcomeClassification.FinishedDuringTeardown"/> (#1945);
    /// <c>TimedOut + any other mutated workspace</c> → Indeterminate (#1373);
    /// <c>NaturalExit</c> otherwise, or <c>TimedOut</c> → Failed;
    /// <c>CancelRequested</c> → Cancelled.
    /// </summary>
    /// <param name="worktreePath">
    /// Only an ACTUALLY-provisioned, auto-isolated worktree (<see cref="Mutation.WorkerBinding.Process.IsWorktree"/>) —
    /// null otherwise, deliberately, per F4 (#1593 review): the retry/grant-audit reads below must
    /// never see the operator's own working directory, routinely dirty for reasons unrelated to this
    /// execution. <paramref name="changesTreeWorkingDirectory"/> below is the separate, wider path for
    /// the #1622/#1390 work-product evidence, which explicitly wants the real directory.
    /// </param>
    /// <param name="changesTreeWorkingDirectory">
    /// #1622/#1390: the caller's own <c>WorkingDirectory</c> when <paramref name="changesTree"/> is
    /// true, regardless of whether it is an auto-provisioned worktree — unlike
    /// <paramref name="worktreePath"/> above, this is never null merely because no isolation was
    /// provisioned, since a tree-changing role's write grant means WriteFiles is true, which by
    /// construction never gets an auto-provisioned worktree (see
    /// <c>Baton.Vendors.RoleDispatch.ToBinding</c>'s own remarks) — so gating this on
    /// <see cref="Mutation.WorkerBinding.Process.IsWorktree"/> the way <paramref name="worktreePath"/>
    /// does would leave workspaceChanged/hollow permanently unable to read "changed".
    /// </param>
    /// <param name="workspaceHeadShaAtStart">
    /// #1373: the commit the probed workspace was at immediately before this execution's process was
    /// spawned, read on the live-dispatch path only. Falls back to <paramref name="worktreeBaseRef"/>,
    /// then to the reflog heuristic — which is what the crash-recovery caller always gets, since it
    /// classifies a recorded exit and there is no "before" left to read.
    /// </param>
    /// <param name="workspaceMutationProbe">
    /// #1373: the seam that keeps this class's own tests off a real git tree. Defaults to
    /// <see cref="Workspaces.WorktreeProvisioner.ReadWorkspaceMutation"/>, which every production
    /// caller uses. The probe's own reads are verified against real temp repositories in
    /// <c>WorktreeProvisionerTests</c> instead — a double cannot answer "does git report this tree
    /// dirty", and F4 (#1593 review) already rewrote one fabricated-workspace test here for exactly
    /// that reason; what a double CAN discriminate is the branch this class takes given a reading,
    /// which is all it is used for.
    /// </param>
    public static OutcomeClassification Classify(
        CoreDispatchResult result,
        WorkerContract contract,
        string outputDirectory,
        IFailureClassifier? failureClassifier = null,
        TimeProvider? timeProvider = null,
        GrantAuditMode grantAuditMode = GrantAuditMode.Enforced,
        string? worktreePath = null,
        IWorkerResponseParser? responseParser = null,
        IWorkerUsageParser? usageParser = null,
        string? worktreeBaseRef = null,
        bool changesTree = false,
        string? changesTreeWorkingDirectory = null,
        int? toolCallCount = null,
        int? hookVerdictCount = null,
        string? workspaceHeadShaAtStart = null,
        Func<string?, string?, Workspaces.WorkspaceMutationReading?>? workspaceMutationProbe = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentException.ThrowIfNullOrEmpty(outputDirectory);

        if (result.Reason == CoreExitReason.CancelRequested)
        {
            // A cancellation is never classified as a failure, and is never retried.
            return new OutcomeClassification(OutcomeVerdict.Cancelled);
        }

        if (result.Reason == CoreExitReason.TimedOut)
        {
            // #1089: a worker can finish its declared work and then hang at process teardown (agy holds a
            // scratch handle and never exits), which WithTimeout kills and reads as TimedOut. A timeout
            // otherwise fails regardless of outputs -- deliberately, because a bare timeout
            // cannot tell "finished then hung" from "killed mid-write with a half-written output". The
            // worker's own terminal success marker (CoreDispatchResult.TerminalSuccessObserved) IS that
            // discriminator: when it was observed AND every declared output is present, the contract is
            // genuinely satisfied and a from-scratch retry (RetryPolicy.MaxAttempts) only rebuilds work that
            // already exists. Absent the marker -- no stream, killed mid-work, or crash-recovery -- this
            // falls through to today's behaviour, so the guard fails safe.
            if (result.TerminalSuccessObserved && ContractValidator.IsSatisfied(contract, outputDirectory))
            {
                return BuildSucceededClassification(
                    contract, changesTreeWorkingDirectory, worktreeBaseRef, changesTree, result.EnginePlacedFiles);
            }

            // #1373: a timeout kill stays retryable only over an empty workspace. The ruling, the
            // measurement behind it, and why the probe fails closed are stated once, in
            // spec/baton.md §3's #1373 paragraph.
            //
            // worktreePath carries F4 (#1593 review)'s constraint on its own doc above.
            // changesTreeWorkingDirectory does NOT, and reading it here is a deliberate RELAXATION of
            // F4, not a case that satisfies it: that parameter was introduced for #1622/#1390's
            // work-product evidence, which decides nothing, and this makes it decide something. The
            // ChangedPathCount half is also ABSOLUTE where the commit half is a delta against
            // workspaceHeadShaAtStart -- it counts what git status reports, with no baseline taken at
            // spawn. spec/baton.md §3's #1373 paragraph states which population that covers, what it
            // costs, and why the ruling accepts it. A null path means this execution had nowhere to
            // leave work, and keeps the retry.
            var mutationProbePath = worktreePath ?? changesTreeWorkingDirectory;
            if (mutationProbePath is not null)
            {
                // #1929 review HIGH: the default probe subtracts AER's own dispatch-time writes, for the
                // same reason BuildSucceededClassification does — otherwise a timeout over an
                // otherwise-clean tree reads Mutated on evidence the engine created and settles
                // Indeterminate instead of a retryable Failed. Bound in a closure rather than widened on
                // the delegate, so an injected test double keeps its two-argument shape.
                var probe = workspaceMutationProbe
                    ?? ((path, since) => Workspaces.WorktreeProvisioner.ReadWorkspaceMutation(
                        path, since, result.EnginePlacedFiles));
                var reading = probe(mutationProbePath, workspaceHeadShaAtStart ?? worktreeBaseRef);

                // #1945: the lane that committed and PUSHED inside its box, then was killed while the
                // repo's pre-push hook was still running gates-fast. Its workspace is clean and its
                // HEAD is already on the remote, so there is nothing for a conductor to resolve and
                // nothing a redispatch would finish -- reporting it as a timeout cost two rooms a
                // manual inspection each on 2026-09-06.
                //
                // Nested INSIDE the Mutated arm on purpose: a lane that did nothing at all is also
                // clean with HEAD == remote, and reads Mutated: false, so it never reaches here and
                // keeps today's plain-timeout Failed below. The evidence is workspace STATE at kill
                // time, never elapsed time -- how long the hook itself ran cannot change this
                // classification, and the hook's own wall clock is already measured once, as the cost
                // ledger's prePushGateMs (spec/baton.md §7). No second timing is minted here.
                if (reading is { Mutated: true, FinishedAndPushed: true })
                {
                    return BuildSucceededClassification(
                        contract, changesTreeWorkingDirectory, worktreeBaseRef, changesTree, result.EnginePlacedFiles)
                        with
                    { FinishedDuringTeardown = true };
                }

                if (reading is { Mutated: true })
                {
                    return new OutcomeClassification(
                        OutcomeVerdict.Indeterminate,
                        FailureClassification: null, // Indeterminate carries no FailureClassification — see OutcomeVerdict.Indeterminate's own remarks.
                        WithStderr(BuildTimeoutOnMutatedWorkspaceReason(reading), result.StderrTail),
                        // Null, so StateProjector records IndeterminateProducer.ContractFailure: this
                        // shape has no captured body to accept and IS something a conductor can reject
                        // after inspecting the workspace, which is precisely that producer's grammar
                        // (spec/baton.md §3's settle-shape table). No fifth producer value for a fifth
                        // source that admits exactly the same verbs.
                        CapturedResponseFile: null);
                }
            }

            var (classification, retryNotBefore) = ReadOrClassifyFailure(contract, outputDirectory, result, failureClassifier, timeProvider);
            return new OutcomeClassification(
                OutcomeVerdict.Failed,
                classification,
                WithStderr(TimeoutSentence, result.StderrTail),
                retryNotBefore);
        }

        // Only CoreExitReason.Natural remains.
        if (result.ExitCode != 0)
        {
            var (classification, retryNotBefore) = ReadOrClassifyFailure(contract, outputDirectory, result, failureClassifier, timeProvider);
            return new OutcomeClassification(
                OutcomeVerdict.Failed,
                classification,
                WithStderr($"Worker exited with non-zero code {result.ExitCode}.", result.StderrTail),
                retryNotBefore);
        }

        var validation = ContractValidator.Validate(contract, outputDirectory);

        // #1586 S1 (the #1594 ruling's tripwire): computed once, ahead of whether a capture below
        // succeeds, so it attaches identically to both the captured and not-captured Failed returns —
        // "regardless of whether a capture succeeded" is the design's own phrasing for exactly this.
        // Deliberately NOT computed for the non-zero-exit-code or timeout paths above: this is scoped
        // to the #1594 shape specifically (a natural exit 0 whose contract is nonetheless unsatisfied),
        // not to every Failed verdict.
        string? substantialWorkNoOutputsEvidence = !validation.IsSatisfied && AllDeclaredOutputsMissing(contract, validation)
            ? DescribeSubstantialWorkEvidence(outputDirectory, usageParser)
            : null;

        if (!validation.IsSatisfied)
        {
            // #1594: the worker exited 0
            // -- it did not crash mid-write -- but a declared output is absent. Give OutputMaterializer
            // a chance to extract the worker's own terminal response into an engine-owned file (see
            // that class's own remarks for why it never touches the declared output directory); this
            // NEVER re-validates the contract, since that directory cannot have changed. #1608: the
            // captured-response arm below settles Indeterminate, not Failed(Permanent) — the two
            // predicates disagree (substantial work happened, but the contract is unsatisfied), which
            // is exactly the shape the journal previously had no word for. Retry-ineligible by
            // RetryEngine.MayRetry's own explicit arm, not by FailureClassification.Permanent's
            // unrelated semantics; only a recorded conductor resolution (baton resolve) settles it.
            var captured = OutputMaterializer.TryCaptureFinalResponse(validation, contract, outputDirectory, responseParser);
            if (captured is not null)
            {
                try
                {
                    Console.Error.WriteLine(
                        "CAPTURED (#1594): the worker's declared output(s) " +
                        string.Join(", ", captured.UnsatisfiedOutputNames.Select(name => $"'{name}'")) +
                        $" were never written by the worker itself. baton captured its terminal " +
                        $"response to '{captured.FileName}' -- the declared output(s) were NOT " +
                        "written, and this execution settles Indeterminate pending conductor resolution " +
                        "('baton resolve').");
                }
                catch (IOException)
                {
                    // Review F6: this runs on the settle path, which has no outer catch -- a broken
                    // stderr pipe on the way out must not itself orphan the execution (#1582's failure
                    // class). The room fact below still carries the capture regardless of whether this
                    // line reached the console.
                }

                var reason = BuildContractFailureReason(
                    validation.UnsatisfiedOutputs,
                    $" Response captured to '{captured.FileName}'; awaiting conductor resolution.");

                return new OutcomeClassification(
                    OutcomeVerdict.Indeterminate,
                    FailureClassification: null, // Indeterminate carries no FailureClassification — see OutcomeVerdict.Indeterminate's own remarks.
                    WithStderr(reason, result.StderrTail),
                    CapturedResponseFile: captured.FileName,
                    UnsatisfiedOutputNames: captured.UnsatisfiedOutputNames,
                    SubstantialWorkNoOutputsEvidence: substantialWorkNoOutputsEvidence);
            }
        }

        if (validation.IsSatisfied)
        {
            if (grantAuditMode == GrantAuditMode.AuditedNotEnforced)
            {
                // Premise verification: BATON_OUTPUT_DIR (the outbox, under artifacts/) lives OUTSIDE the provisioned worktree
                // (workspaces/<worker>), so legitimate output writes never dirty the worktree.
                //
                // #1929 review HIGH, correcting that premise rather than restating it: since #1151 an
                // adapter MAY place files inside the worker's working directory before spawn (the claude
                // adapter's canonical-skill projection), and Audit -- alone among the readers of this
                // tree -- does not subtract them. The list of the ones that DO is on
                // WorktreeProvisioner.ChangedPathsExcludingEnginePlaced, which is where this exception is
                // recorded rather than here. Not reachable through `baton dispatch --role` for claude,
                // whose WithheldWritesReachTheOutbox is true, so RoleDispatch never auto-provisions the
                // audited worktree; it IS reachable from a hand-written binding pairing claude with
                // Worktree + AuditedNotEnforced, where the projection is audited as a stray path and
                // this call settles Failed/Permanent on it.
                //
                // #1929 review round 3 (MEDIUM) did not close that -- why it cannot be closed with a
                // filter is stated once, on ChangedPathsExcludingEnginePlaced. What changed is that the
                // refusal Audit composes now SAYS it did not subtract, so an operator reading it can
                // tell the engine's own writes from a worker's real grant violation.
                var audit = Workspaces.WorktreeProvisioner.Audit(worktreePath);
                if (!audit.IsClean)
                {
                    return new OutcomeClassification(
                        OutcomeVerdict.Failed,
                        FailureClassification.Permanent, // Permanent: a worker mutating files outside declared outputs violates its role contract; retrying will produce identical stray mutations.
                        WithStderr(audit.FailureReason ?? "Grant audit failed: worktree is dirty.", result.StderrTail));
                }
            }

            // #914/#1622: an auto-denied tool and mid-lane quota exhaustion are the two things that
            // veto an otherwise-satisfied exit-0 run — agy denies a tool (or a vendor's quota runs out
            // mid-turn), exits 0, and the worker still writes its contract output, so nothing else here
            // would catch it. Gated specifically on these two classifications: any other classification
            // (Retryable, Permanent) never fires here today (neither adapter's TryClassifyFailure emits
            // them from stderr/stdout prose), and gating narrowly keeps this from ever stamping some
            // other classification with either message below.
            //
            // #1622: this call already reads result.StdoutTail, the same stream the exit-1 path
            // above parses via TryClassifyQuotaExhaustion, so a satisfied-contract exit-0 run that
            // still carries the vendor's quota signal is classified ExhaustedUntil, not Succeeded --
            // RetryEngine then parks it identically to an exit-1 quota failure.
            //
            // #1720 review F1: TryClassifySatisfiedRunFailure, NOT the exit-1 path's
            // TryClassifyFailure -- see that member's own doc (Outcomes.IFailureClassifier) for what
            // makes the satisfied path a different question, and spec/baton.md §3's exit-0 quota
            // veto for the scope (live dispatch only).
            if (failureClassifier is not null && failureClassifier.TryClassifySatisfiedRunFailure(
                    result.StderrTail, result.StdoutTail, timeProvider ?? TimeProvider.System, out var classifiedFailure, out var retryNotBefore))
            {
                if (classifiedFailure == FailureClassification.ToolDenied)
                {
                    return new OutcomeClassification(
                        OutcomeVerdict.Failed,
                        classifiedFailure,
                        WithStderr("Execution failed: a required tool was auto-denied.", result.StderrTail),
                        retryNotBefore);
                }

                if (classifiedFailure == FailureClassification.ExhaustedUntil)
                {
                    return new OutcomeClassification(
                        OutcomeVerdict.Failed,
                        classifiedFailure,
                        WithStderr("Execution exited 0, but the vendor's quota-exhaustion signal was present in the stream.", result.StderrTail),
                        retryNotBefore);
                }
            }

            // #1680's first-verdict canary (#1732 review F3: moved here from just after the entry
            // guards, where it preempted the ExitCode != 0 branch above and turned a retryable quota
            // refusal into Indeterminate). This is now reached ONLY when every other veto above has
            // already let the run through -- exit code 0, ContractValidator.Validate satisfied, the
            // grant audit clean, and no ToolDenied/ExhaustedUntil signal -- so it can only ever
            // downgrade a run that would otherwise return Succeeded next, exactly what this comment and
            // spec/baton.md §9 say it does. Vendor-neutral by construction: this class never parses a
            // vendor's own stream (Architecture Rule 1, CLAUDE.md), so both counts arrive pre-computed
            // -- today only an agy dispatch whose hook is the sole narrowing supplies them
            // (AgyWorkerAdapter.Resolve's CountHookVerdicts, IWorkerUsageParser.CountToolSteps summed
            // over the stream), which is why both default to null and every other caller's
            // classification is unchanged. A worker that issued at least one tool call while its
            // PreToolUse hook recorded zero verdicts means the hook may never have run at all -- on agy
            // an absent hook response reads as an ALLOW rather than an error
            // (agy.hook-malformed-stdout-fails-open), so an otherwise-Succeeded run is not trustworthy;
            // it settles Indeterminate (Domain.IndeterminateProducer.ContractFailure -- CapturedResponseFile
            // stays null, since there is nothing to capture here) pending conductor resolution, exactly
            // like the #1608 disagreement shape. The `result.Reason == CoreExitReason.Natural` guard is
            // redundant with the CancelRequested/TimedOut returns above (only Natural ever reaches
            // here) but kept explicit rather than relying on that ordering never changing again.
            if (result.Reason == CoreExitReason.Natural && toolCallCount is { } calls && calls > 0 && hookVerdictCount == 0)
            {
                return new OutcomeClassification(
                    OutcomeVerdict.Indeterminate,
                    FailureClassification: null,
                    WithStderr(
                        $"The agy PreToolUse hook recorded zero verdicts across {calls} tool call(s) -- it " +
                        "may never have run, which on this vendor is a silent allow rather than an error " +
                        "(agy.hook-malformed-stdout-fails-open). Settling Indeterminate pending conductor " +
                        "resolution ('baton resolve').",
                        result.StderrTail));
            }

            return BuildSucceededClassification(
                contract, changesTreeWorkingDirectory, worktreeBaseRef, changesTree, result.EnginePlacedFiles);
        }

        // #1593: Natural exit 0 with unsatisfied contract settles Indeterminate (spec/baton.md §3 Producers).
        // #1622: A dead streaming worker retains the retryable Failed path when untouched (WorktreeProvisioner.IsWorkspaceUntouched).
        // F6 (#1593 review): keys on CoreDispatchResult.TerminalResultObserved, not TerminalSuccessObserved.
        // Register entry: spec/baton.md §3 F6.
        // #1929 review round 3 (MEDIUM): result.EnginePlacedFiles is threaded through here and into
        // DescribeWorkspaceEvidence below for the same reason the two readers above already take it --
        // AER's own pre-spawn writes are not the worker's work. One predicate, every reader:
        // WorktreeProvisioner.ChangedPathsExcludingEnginePlaced names the full set once.
        var isDeadWorkerWithoutResult = responseParser is not null && !result.TerminalResultObserved;
        if (isDeadWorkerWithoutResult && Workspaces.WorktreeProvisioner.IsWorkspaceUntouched(
                worktreePath, worktreeBaseRef, result.EnginePlacedFiles))
        {
            var (contractClassification, contractRetryNotBefore) = ReadOrClassifyFailure(contract, outputDirectory, result, failureClassifier, timeProvider);
            return new OutcomeClassification(
                OutcomeVerdict.Failed,
                contractClassification,
                WithStderr(BuildContractFailureReason(validation.UnsatisfiedOutputs), result.StderrTail),
                contractRetryNotBefore,
                SubstantialWorkNoOutputsEvidence: substantialWorkNoOutputsEvidence);
        }

        // F2 (#1593 review): closes #1593's second acceptance bullet — see
        // WorktreeProvisioner.DescribeWorkspaceEvidence's own remarks. Appended to the SAME suffix
        // BuildContractFailureReason already reserves budget for, so it truncates the same visible way
        // an overflowing output list does.
        // Null (no worktree, or nothing to report) leaves the reason unchanged — the byte-pinned
        // no-worktree case in Classify_leaves_the_reason_byte_for_byte_unchanged_when_the_worker_wrote_no_stderr.
        var workspaceEvidence = Workspaces.WorktreeProvisioner.DescribeWorkspaceEvidence(
            worktreePath, worktreeBaseRef, result.EnginePlacedFiles);
        var boundedWorkspaceEvidence = workspaceEvidence is null
            ? null
            : Truncate(workspaceEvidence, MaxWorkspaceEvidenceLength);
        var indeterminateSuffix = " — worker exited 0 with work possibly on disk; awaiting conductor resolution."
            + (boundedWorkspaceEvidence is null ? string.Empty : $" Workspace {boundedWorkspaceEvidence}.");

        var indeterminateReason = BuildContractFailureReason(
            validation.UnsatisfiedOutputs,
            indeterminateSuffix);

        return new OutcomeClassification(
            OutcomeVerdict.Indeterminate,
            FailureClassification: null, // Indeterminate carries no FailureClassification — see OutcomeVerdict.Indeterminate's own remarks.
            WithStderr(indeterminateReason, result.StderrTail),
            CapturedResponseFile: null,
            UnsatisfiedOutputNames: validation.UnsatisfiedOutputs.Select(u => u.Name).ToList(),
            SubstantialWorkNoOutputsEvidence: substantialWorkNoOutputsEvidence);
    }

    /// <summary>
    /// Appends a bounded, single-line rendering of the worker's stderr to an already-assembled
    /// reason (#563), or returns <paramref name="reason"/> untouched when the worker wrote nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The base reason is assembled and bounded first, then this is appended with its own separate
    /// budget, rather than both sharing one cap. That is the same split
    /// <see cref="BuildContractFailureReason"/> already documents: a single shared cap lets whichever
    /// part happens to be longer starve the other, and here that would mean a verbose worker's
    /// stderr silently swallowing the contract diagnostic, or vice versa.
    /// </para>
    /// <para>
    /// The <c>stderr:</c> separator deliberately matched the now-retired dialogue worker's own
    /// <c>DialogueRunner</c>, which appended a failed vendor turn's stderr to its own message the
    /// same way since M17 Phase 3 (#166), on the same reasoning: an operator should not have to
    /// learn two spellings of the same fact. That worker was archived in #1408; this rendering
    /// stands on its own now.
    /// </para>
    /// </remarks>
    private static string WithStderr(string reason, string? stderrTail)
    {
        if (string.IsNullOrWhiteSpace(stderrTail))
        {
            // A worker that was genuinely silent must produce the byte-for-byte pre-#563 reason —
            // no empty "stderr:" label, which would read as though it had spoken and said nothing.
            return reason;
        }

        // Idempotent on anything CoreDispatcher produced — StderrTailBuffer already collapsed it, and
        // collapsing is what makes the two caps comparable. Repeated here because CoreDispatchResult
        // is a public record any caller may construct (a test double, a future dispatcher), and a raw
        // multi-line value reaching a line-oriented surface is the failure this prevents. It is not
        // where the guarantee comes from; see MaxStderrTailInReason.
        var collapsed = CollapseWhitespace(stderrTail);
        if (collapsed.Length == 0)
        {
            return reason;
        }

        var kept = ContractValidator.KeepLastWithoutSplittingSurrogatePair(collapsed, MaxStderrTailInReason);

        // The ellipsis goes on the front because the cut is on the front: this is a tail, so what was
        // dropped precedes what is shown. Marking the wrong end would claim the opposite.
        var marker = kept.Length < collapsed.Length ? "…" : string.Empty;

        return $"{reason} stderr: {marker}{kept}";
    }

    /// <summary>
    /// The inverse of <see cref="WithStderr"/>, for surfaces that render the two halves separately
    /// (#617's failed-step banner shows the sentence as a headline and the stderr as an excerpt
    /// block). Lives beside the writer so the <c>" stderr: "</c> spelling has one home and a format
    /// change cannot silently strand a reader — the round-trip test is the drift guard. A reason
    /// with no stderr half comes back whole with a null excerpt. A leading <c>…</c> stays on the
    /// excerpt: it is the writer's truncation mark (<see cref="MaxStderrTailInReason"/>), and
    /// stripping it would re-create, on this one surface, exactly the invisible truncation the
    /// writer's own contract forbids — a cut tail shown as though it were the whole capture.
    /// </summary>
    /// <remarks>
    /// The split takes the <i>first</i> separator occurrence, which is exact for the fixed engine
    /// sentences (<c>Execution timed out.</c>, <c>Worker exited with non-zero code N.</c>) but a
    /// heuristic for contract-failure reasons, whose base embeds worker-produced values
    /// (<see cref="DescribeUnsatisfiedOutput"/>) that could themselves contain the literal
    /// <c>" stderr: "</c> — in which case the sentence truncates early and the excerpt starts with
    /// base-reason text. Last-occurrence was considered and rejected as the worse bet: the tail is
    /// raw worker stderr, where a literal <c>stderr:</c> label is common wrapper output, and
    /// mis-splitting on it would fold real stderr into the headline instead. The combined string
    /// is the only durable record (<c>ExecutionAttempt.Reason</c>), so no parse can be exact for
    /// both halves; this picks the failure mode that needs the rarer content.
    /// </remarks>
    public static (string Sentence, string? StderrExcerpt) SplitReasonAndStderr(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return ("Step failed.", null);
        }

        const string separator = " stderr: ";
        var index = reason.IndexOf(separator, StringComparison.Ordinal);
        if (index < 0)
        {
            return (reason.Trim(), null);
        }

        var sentence = reason[..index].Trim();
        var excerpt = reason[(index + separator.Length)..].Trim();

        return (sentence, excerpt.Length == 0 ? null : excerpt);
    }

    /// <summary>
    /// Flattens stderr to a single line. Every consumer of <c>Reason</c> is line-oriented — the CLI's
    /// <c>FlowStateReporter</c> writes one <c>"  {StepId}: {Status} — {Reason}"</c> line per step —
    /// so an embedded newline would not merely look untidy, it would break that format into rows
    /// that no longer parse as step lines. Vendor CLIs routinely write multi-line errors.
    /// </summary>
    private static string CollapseWhitespace(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        var pendingSpace = false;

        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch))
            {
                // Deferred rather than emitted, so runs collapse and leading/trailing space never lands.
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    /// <summary>
    /// #1373: the diagnostic for a timeout kill that landed on a workspace carrying work.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Opens with <see cref="TimeoutSentence"/> — the same fixed sentence the plain timeout arm uses,
    /// and load-bearing rather than stylistic: <see cref="Status.WorkflowOutcome.IsTimeoutFailure"/>
    /// keys on that prefix, so a room that timed out still reads as a timeout to every surface that
    /// already tells timeouts apart from other failures. Pinned by
    /// <c>OutcomeClassifierTests</c> rather than left to this remark.
    /// </para>
    /// <para>
    /// Carries the <c>awaiting conductor resolution.</c> marker every Indeterminate reason carries,
    /// which <see cref="Projection.StateProjector"/>'s <c>BuildConductorResolvedReason</c> strips when a
    /// conductor settles the step. It is last in the base reason but NOT necessarily last in the stored
    /// one: <see cref="WithStderr"/> appends the worker's stderr after it, and that strip is an
    /// <c>EndsWith</c>, so a reason with stderr keeps the clause through resolution. Pre-existing and
    /// shared with both #1593 arms (one of which already appends workspace evidence past the marker) —
    /// recorded here rather than fixed, since narrowing it touches every producer's reason at once.
    /// Counts and the resolution verb come first, prose last: truncation cuts the tail, so the
    /// actionable half must not be what gets dropped.
    /// </para>
    /// </remarks>
    private static string BuildTimeoutOnMutatedWorkspaceReason(Workspaces.WorkspaceMutationReading reading) =>
        // "then redispatch", never "or redispatch": RedispatchCommand refuses an Indeterminate parent
        // unconditionally and says so, with no --force, so offering the two as alternatives would send
        // a conductor straight into a refusal.
        $"{TimeoutSentence} Workspace carries {reading.Describe()} — resolve it ('baton resolve --reject " +
        "--reason <text>'), then redispatch a brief telling the next worker to finish what this attempt " +
        "started. Not retried: a from-scratch attempt would restart on top of that work — awaiting " +
        "conductor resolution.";

    /// <summary>
    /// Assembles the diagnostic for a natural, exit-0 completion whose contract still isn't
    /// satisfied — the exact signature (worker exited 0, wrote none of its declared outputs) that
    /// previously surfaced as a bare <c>ExecutionFailed</c> with no reason.
    /// </summary>
    /// <remarks>
    /// Bounded in two places, and the split matters. Each output's <i>count</i> is capped here and
    /// each rendered <i>value</i> is capped in <see cref="ContractValidator"/>, both with their own
    /// explicit marker; only then is <see cref="Truncate"/> applied to the whole. Capping solely at
    /// the end was wrong: one large value would eat the entire budget and silently drop every other
    /// unsatisfied output, so a reason that promised to name them all named one. With the per-item
    /// bounds in place the final <see cref="Truncate"/> is a backstop that should not normally fire.
    /// </remarks>
    private static string BuildContractFailureReason(
        IReadOnlyList<UnsatisfiedOutput> unsatisfiedOutputs,
        string? suffix = null)
    {
        var listed = unsatisfiedOutputs.Count <= MaxListedOutputs
            ? unsatisfiedOutputs
            : unsatisfiedOutputs.Take(MaxListedOutputs).ToList();

        var reason = "Contract not satisfied: " + string.Join("; ", listed.Select(DescribeUnsatisfiedOutput));
        var fullSuffix = suffix ?? string.Empty;

        // The suffix's own length is reserved from the budget rather than appended after
        // truncating. Appending it left the marker as the first thing Truncate cut — reinstating,
        // at the count layer, the very "outputs silently dropped with no signal" this cap exists to
        // prevent. A signal that disappears exactly when it becomes true is worse than none.
        var overflow = unsatisfiedOutputs.Count - listed.Count;
        if (overflow > 0)
        {
            var overflowSuffix = $" (+{overflow} more)" + fullSuffix;
            return Truncate(reason, MaxReasonLength - overflowSuffix.Length) + overflowSuffix;
        }

        if (fullSuffix.Length > 0)
        {
            return Truncate(reason, MaxReasonLength - fullSuffix.Length) + fullSuffix;
        }

        return Truncate(reason, MaxReasonLength);
    }

    /// <summary>
    /// "Zero declared outputs" for the #1586 S1 tripwire — not merely unsatisfied, but every declared
    /// output <see cref="UnsatisfiedOutputReason.Missing"/> specifically. A present-but-wrong output
    /// (<see cref="UnsatisfiedOutputReason.NotJson"/>, a failed <see cref="UnsatisfiedOutputReason.ConditionFailed"/>,
    /// a <see cref="UnsatisfiedOutputReason.SchemaViolation"/>) means the worker DID write something —
    /// a different failure than "wrote nothing", and #1606 merging is exactly what keeps "nothing
    /// present" an honest read of "the engine wrote nothing either" (the engine never writes under a
    /// declared name; see <see cref="OutputMaterializer"/>'s class remarks).
    /// </summary>
    private static bool AllDeclaredOutputsMissing(WorkerContract contract, ContractValidationResult validation) =>
        contract.ProducedOutputs.Count > 0
        && validation.UnsatisfiedOutputs.Count == contract.ProducedOutputs.Count
        && validation.UnsatisfiedOutputs.All(u => u.Reason == UnsatisfiedOutputReason.Missing);

    /// <summary>
    /// Builds a plain <see cref="OutcomeVerdict.Succeeded"/> classification, plus — when
    /// <paramref name="changesTree"/> is true — the work-product evidence spec/baton.md §3
    /// ("workspaceChanged/hollow/hollowReason") specifies in full; not restated here. Shared by both
    /// places <see cref="Classify"/> settles Succeeded so the two paths cannot silently diverge.
    /// <paramref name="changesTree"/> is <see cref="Domain.WorkerBinding.Process.ChangesTree"/>,
    /// forwarded down from <c>Mutation.MutationInterface</c>. <paramref name="changesTreeWorkingDirectory"/>
    /// is <see cref="Classify"/>'s own parameter of that name — see its doc for why this is never the
    /// retry-protected <c>worktreePath</c>.
    /// </summary>
    private static OutcomeClassification BuildSucceededClassification(
        WorkerContract contract, string? changesTreeWorkingDirectory, string? worktreeBaseRef, bool changesTree,
        IReadOnlyList<Domain.EnginePlacedFile>? enginePlacedFiles)
    {
        if (!changesTree)
        {
            return new OutcomeClassification(OutcomeVerdict.Succeeded);
        }

        // #1720 review F2: tri-state. When git cannot answer (not a checkout, no upstream, git
        // failure) both fields stay NULL and render as absent -- never a fabricated `true`, which is
        // what negating the fail-closed IsWorkspaceUntouched produced, and never a fabricated
        // `false`, which would pin `hollow` off exactly where the probe is blind.
        // #1929 review HIGH: AER's own dispatch-time writes into this tree (the claude adapter's skill
        // projection) are subtracted here, so `workspaceChanged` cannot be a positive the engine itself
        // manufactured. Refilled from the journaled room fact on the crash-recovery path too (#1933) —
        // WorktreeProvisioner's ChangedPathsExcludingEnginePlaced states the scope once.
        if (!Workspaces.WorktreeProvisioner.TryReadWorkspaceChanged(
                changesTreeWorkingDirectory, worktreeBaseRef, out var workspaceChanged, enginePlacedFiles))
        {
            return new OutcomeClassification(OutcomeVerdict.Succeeded);
        }

        var hollow = !workspaceChanged && contract.ProducedOutputs.Count == 0;
        var hollowReason = hollow
            ? "the worker exited 0 with a satisfied contract, but the worktree is unchanged (no commit, " +
              "no uncommitted changes) and the contract declares no outputs -- a strong hollow-success signal"
            : null;

        return new OutcomeClassification(
            OutcomeVerdict.Succeeded, WorkspaceChanged: workspaceChanged, Hollow: hollow, HollowReason: hollowReason);
    }

    /// <summary>
    /// The "substantial work" half of the #1586 S1 tripwire: the worker's own final usage line —
    /// the execution's captured <c>.stdout.log</c>, last non-blank line, read via
    /// <see cref="OutputMaterializer.TryReadLastNonBlankLine"/> (#1586 S1 review F5: shared with
    /// <see cref="OutputMaterializer.TryCaptureFinalResponse"/>'s response-line read, so the two
    /// cannot silently disagree about what "the worker's final line" is) — reporting turns and/or
    /// output tokens. Chosen over a worktree-dirty read (also considered): <c>worktreePath</c> is the
    /// operator's own working directory whenever no worktree was provisioned, routinely dirty for
    /// reasons that have nothing to do with this execution, which would make the tripwire fire on the
    /// operator's OWN uncommitted changes rather than the worker's. A vendor-reported usage figure has
    /// no such false-positive source. Returns null — not "zero", which this deliberately does not
    /// fabricate — when no parser was supplied, no stdout log exists, the line does not parse, or the
    /// vendor reported neither figure.
    /// </summary>
    private static string? DescribeSubstantialWorkEvidence(string outputDirectory, IWorkerUsageParser? usageParser)
    {
        if (usageParser is null)
        {
            return null;
        }

        var line = OutputMaterializer.TryReadLastNonBlankLine(outputDirectory);
        if (line is null)
        {
            return null;
        }

        if (!usageParser.TryParseFinalUsage(line, out var usage) || usage is null)
        {
            return null;
        }

        if (usage.Turns is > 0 || usage.TokensOut is > 0)
        {
            return $"the worker's own final usage line reports {usage.Turns?.ToString() ?? "an unreported number of"} turn(s) " +
                $"and {usage.TokensOut?.ToString() ?? "an unreported number of"} output token(s)";
        }

        return null;
    }

    private static string DescribeUnsatisfiedOutput(UnsatisfiedOutput output) => output.Reason switch
    {
        UnsatisfiedOutputReason.Missing => $"'{output.Name}' is missing",
        UnsatisfiedOutputReason.NotJson => $"'{output.Name}' is not valid JSON",
        UnsatisfiedOutputReason.ConditionFailed => output.ActualValue is null
            ? $"'{output.Name}': JSON Pointer '{output.ConditionPath}' did not resolve (expected {output.ExpectedValue})"
            : $"'{output.Name}': JSON Pointer '{output.ConditionPath}' resolved to {output.ActualValue}, expected {output.ExpectedValue}",
        UnsatisfiedOutputReason.MalformedCondition =>
            $"'{output.Name}': condition cannot be evaluated — {output.Detail}",
        UnsatisfiedOutputReason.SchemaViolation =>
            $"'{output.Name}' is not a valid document of its declared schema — {output.Detail}",
        _ => throw new ArgumentOutOfRangeException(nameof(output), output.Reason, "Unknown UnsatisfiedOutputReason."),
    };

    /// <summary>
    /// Backstop cap on the assembled reason. Delegates the cut to
    /// <see cref="ContractValidator.TrimWithoutSplittingSurrogatePair"/> so there is one
    /// surrogate-safe truncation in the codebase rather than two that can drift — the per-value cap
    /// needs the identical rule, and a second copy of it is the shape that goes wrong quietly.
    /// The <c>cut &gt; 0</c> guard lives there: it is unreachable while
    /// <see cref="MaxReasonLength"/> is 500, but lowering a display cap is an ordinary later edit and
    /// an unguarded index would throw out of <see cref="Classify"/> while recording an outcome.
    /// </summary>
    private static string Truncate(string value, int maxLength)
    {
        // N1 (#1664 re-review): a caller (BuildContractFailureReason) computes maxLength as the
        // budget minus an already-assembled suffix, which can go negative when the suffix alone
        // overruns MaxReasonLength — clamped here rather than trusted, so Classify cannot throw while
        // recording an outcome. TrimWithoutSplittingSurrogatePair clamps too, defensively; this is the
        // one that decides "value.Length <= maxLength" correctly for a non-positive maxLength.
        maxLength = Math.Max(maxLength, 0);

        if (value.Length <= maxLength)
        {
            return value;
        }

        const string ellipsis = "...";
        return ContractValidator.TrimWithoutSplittingSurrogatePair(value, maxLength - ellipsis.Length) + ellipsis;
    }

    /// <summary>
    /// Looks for a worker's optional self-reported <see cref="Domain.FailureClassification"/>,
    /// reported through one of the contract's declared <c>OptionalMetadata</c> file
    /// roles as a top-level <c>FailureClassification</c> JSON field. Checked in declaration order;
    /// the first metadata file that exists, parses as JSON, and carries a recognized value wins.
    /// Absent or unrecognized — including no <c>OptionalMetadata</c> file at all — is null, which
    /// the domain type documents as "treated as Retryable".
    /// </summary>
    private static FailureClassification? ReadFailureClassification(WorkerContract contract, string outputDirectory)
    {
        foreach (var metadataName in contract.OptionalMetadata)
        {
            var path = Path.Combine(outputDirectory, metadataName);
            if (!File.Exists(path))
            {
                continue;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(File.ReadAllBytes(path));
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("FailureClassification", out var value) &&
                    value.ValueKind == JsonValueKind.String &&
                    Enum.TryParse<FailureClassification>(value.GetString(), ignoreCase: true, out var classification))
                {
                    return classification;
                }
            }
        }

        return null;
    }

    private static (FailureClassification? Classification, DateTimeOffset? RetryNotBefore) ReadOrClassifyFailure(
        WorkerContract contract,
        string outputDirectory,
        CoreDispatchResult result,
        IFailureClassifier? failureClassifier,
        TimeProvider? timeProvider)
    {
        var metadataClassification = ReadFailureClassification(contract, outputDirectory);
        if (metadataClassification is not null)
        {
            return (metadataClassification, null);
        }

        if (failureClassifier is not null && failureClassifier.TryClassifyFailure(
                result.StderrTail, result.StdoutTail, timeProvider ?? TimeProvider.System, out var adapterClassification, out var adapterRetryNotBefore))
        {
            return (adapterClassification, adapterRetryNotBefore);
        }

        return (null, null);
    }
}
