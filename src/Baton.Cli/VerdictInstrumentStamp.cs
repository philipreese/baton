namespace Baton.Cli;

/// <summary>
/// #1882: makes every <c>verdict.json</c> a room produced say what the ENGINE knows about the
/// instruments — the verify step's rows when one ran, and the key removed when none did. See
/// <c>Mutation.VerifyStep.InjectInstrumentsAsync</c> for why the engine, and not the model, is the
/// writer.
/// <para>
/// #1895 made this shared rather than a private of <see cref="DispatchCommand"/>: <c>baton
/// redispatch</c> drives the same pump and produced the one review verdict whose <c>instruments</c>
/// was still whatever the model wrote. It calls <see cref="ApplyAsync"/> with a null outcome — no
/// verify step can run on that path — so the removal arm reaches a redispatched review too, which is
/// what stops a model-written array riding verbatim into a <c>--notify</c> payload
/// (<see cref="WatchFireService.BuildPayload"/> deserializes this file off disk with no schema in
/// between).
/// </para>
/// <para>
/// #1911 added two more verbs, so the callers are now four: <see cref="DispatchCommand"/> (both
/// arms), <see cref="RedispatchCommand"/>, <see cref="ResumeCommand"/> and
/// <see cref="SupplyCommand"/>. The last three are removal-arm only — a resumed turn and a supplied
/// file each have no verify outcome of their own behind them, so absent is the only honest thing the
/// engine can say about a verdict those verbs put on disk.
/// <b>Removal is unconditional, but only over the executions the invocation itself produced.</b>
/// It never reaches back to an execution some earlier verb already settled: a prior
/// <c>dispatch --verify-cmd</c> stamped that verdict with rows the engine measured, and stripping
/// them would destroy a record rather than refuse a claim — the failure being priced here is a
/// fabricated <c>instruments</c> array read back as an engine record, which an untouched older
/// execution does not carry (#1911 review, medium 1). <see cref="SupplyCommand"/> passes its own
/// execution and gets no step walk at all. <see cref="ResumeCommand"/> and
/// <see cref="RedispatchCommand"/> keep the walk and need no scoping: both mint a fresh execution
/// for the step they drive, so the earlier one stops being that step's
/// <c>LatestExecutionId</c> and the walk cannot see it — and a room with true rows is single-step
/// anyway, since <c>--verify-cmd</c> is refused for a workflow template
/// (<see cref="DispatchCommand"/>'s own refusal).
/// </para>
/// <para>
/// Called after the run rather than from inside the engine's contract check, for the same reason
/// <see cref="DispatchCommand.CopyPrimaryOutputToOverride"/> is: the execution-scoped artifact
/// directory is not known until the run has produced one. Every step with an execution is visited,
/// not just the first (except on the scoped arm — see <c>ApplyAsync</c>'s <c>onlyExecutionId</c>): a composed
/// template's review phase is not necessarily its first step, and the
/// removal arm has to reach a model-written <c>instruments</c> wherever a verdict was written. A step
/// whose execution wrote no <c>verdict.json</c> — every non-verdict role — is silently skipped, which
/// is why no role check is needed here: the file's existence is the population.
/// </para>
/// </summary>
internal static class VerdictInstrumentStamp
{
    /// <summary>
    /// The review role's structured output (<c>WorkerRoles.json</c>) — the one file this type
    /// annotates. Named rather than derived from the role's output list because the annotation is
    /// specific to the ReviewVerdict schema, not to "whatever the first output happens to be called";
    /// <b>referenced, not respelled</b> (#1913 review finding 7), so this type and the cost ledger's
    /// own read of the same file cannot drift apart.
    /// </summary>
    internal const string VerdictOutputName = Baton.Accounting.CostLedgerStore.VerdictOutputName;

    /// <param name="verifyStep">Null when no verify step ran for this room.</param>
    /// <param name="onlyExecutionId">
    /// #1911: the single execution to visit — <b>when non-null the step walk is skipped entirely</b>,
    /// not extended. <c>baton supply</c>'s supplementary execution is minted with <c>StepId: null</c>
    /// (<see cref="Baton.Mutation.MutationInterface.RecordSupplementaryExecutionAsync"/>), so the walk
    /// would both miss the artifact this verb just copied in and reach verdicts written by executions
    /// that predate it. A <c>--output-name verdict.json</c> supply is the case where an
    /// operator-provided <c>instruments</c> array would otherwise ride into a <c>--notify</c> payload
    /// unchallenged; every OTHER file in the room was somebody else's to settle.
    /// Null for the verbs that drive a pump, whose own executions are their steps' latest.
    /// </param>
    internal static async Task ApplyAsync(
        string roomDirectoryPath,
        CommandResult result,
        Baton.Mutation.VerifyStep.Outcome? verifyStep,
        Baton.Domain.ExecutionId? onlyExecutionId = null)
    {
        IEnumerable<Baton.Domain.ExecutionId> executionIds = onlyExecutionId is { } scoped
            ? [scoped]
            : result.State.Steps
                .Select(step => step.LatestExecutionId)
                .OfType<Baton.Domain.ExecutionId>()
                .Distinct();

        foreach (var execId in executionIds)
        {
            var verdictPath = Path.Combine(
                roomDirectoryPath,
                Baton.Artifacts.ArtifactManager.ArtifactsDirectoryName,
                $"execution_{execId}",
                VerdictOutputName);
            if (!File.Exists(verdictPath))
            {
                continue;
            }

            // CancellationToken.None: the review is already finished and its outputs are already durable.
            var stamped = await Baton.Mutation.VerifyStep
                .InjectInstrumentsAsync(verdictPath, verifyStep?.Instruments, CancellationToken.None)
                .ConfigureAwait(false);
            if (stamped)
            {
                continue;
            }

            Console.Error.WriteLine(verifyStep is null
                ? $"Warning: could not check '{verdictPath}' for a model-written 'instruments' field. No "
                    + "verify step ran for this room, so any value under that key is the worker's own "
                    + "claim rather than an engine record."
                : $"Warning: could not record the verify step's instruments on '{verdictPath}'. The verify "
                    + $"results themselves are unaffected, at '{verifyStep.ResultsFilePath}'.");
        }
    }
}
